using System;

namespace BossMod.Autorotation.Engine;

// Per-job constants derived once from the definition: filler damage rate, natural gauge generation and the
// potency-equivalent value of one unit of each resource. Feeds the upper tier and the leaf evaluation.
public sealed class JobAnalysis
{
    public readonly JobDefinition Job;
    public readonly float FillerPps;
    public readonly float FillerPerGcd;
    public readonly float[] GaugeGainPerSecond;
    public readonly float[] GaugeUnit;          // smallest spend (0 = gauge has no spender)
    public readonly float[] GaugeUnitValue;     // value of one unit spent, net of the displaced filler
    public readonly bool[] GaugeSpentByGcd;
    public readonly float[] CdUnitValue;        // value of one charge spent
    public readonly bool[] CdSpentByGcd;
    public readonly float[] CdValueDuration;   // >0 when the charge's value is a timed damage buff: its payoff spans this many seconds
    public readonly float[] GaugeUnitValueBase;  // from the definition alone; GaugeUnitValue = base + weights.GaugeValue
    public readonly float[] CdUnitValueBase;     // from the definition alone; CdUnitValue = base + weights.CooldownValue
    public readonly float[] CdUnlockValue;       // extra value of a charge from the skills its statuses unlock (added x weights.UnlockScale)
    public readonly float[] StatusValuePerSecond; // damage-multiplier statuses: (m-1) x filler rate
    public readonly float MaxSkillValue;        // upper bound of one skill's immediate value (single target, unbuffed)
    public readonly float[] ComboChainValue;    // per skill: value of having it as the last combo step = sum over the remaining chain of (combo potency - filler per GCD)
    public readonly float MaxComboChainValue;
    public readonly CycleModel?[] Cycles = new CycleModel?[4]; // by target count (1..3; 3 = three or more), when the job declares a cycle state

    public CycleModel? CycleFor(int targets) => Cycles[Math.Clamp(targets, 1, 3)];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, float, int), CycleModel> _cycleCache = new();

    public JobAnalysis(JobDefinition job)
    {
        Job = job;
        (FillerPps, FillerPerGcd, GaugeGainPerSecond) = MeasureFiller(job);
        if (job.FillerOverride > 0)
        {
            FillerPerGcd = job.FillerOverride;
            FillerPps = job.FillerOverride / job.BaseGcd;
        }
        if (job.CycleGauges.Length > 0)
        {
            // the long-run optimal rate is the rate every line is compared to
            System.Threading.Tasks.Parallel.For(1, 4, t => Cycles[t] = _cycleCache.GetOrAdd((job.Name, job.BaseGcd, t), k => new CycleModel(job, k.Item3)));
            FillerPps = Cycles[1]!.Rate;
            FillerPerGcd = FillerPps * job.BaseGcd;
        }

        StatusValuePerSecond = new float[job.Statuses.Length];
        for (var i = 0; i < job.Statuses.Length; ++i)
            StatusValuePerSecond[i] = MathF.Max(0, job.Statuses[i].DamageMultiplier - 1) * FillerPps;

        GaugeUnit = new float[job.Gauges.Length];
        GaugeUnitValue = new float[job.Gauges.Length];
        GaugeSpentByGcd = new bool[job.Gauges.Length];
        CdUnitValue = new float[job.Cooldowns.Length];
        CdSpentByGcd = new bool[job.Cooldowns.Length];
        CdValueDuration = new float[job.Cooldowns.Length];
        CdUnlockValue = new float[job.Cooldowns.Length];
        var perPoint = new float[job.Gauges.Length];
        foreach (var s in job.Skills)
        {
            var value = SkillValue(s);
            MaxSkillValue = MathF.Max(MaxSkillValue, value);
            var net = value - (s.IsGcd ? FillerPerGcd : 0);
            foreach (var e in s.Effects)
            {
                // a spend is a gauge decrease the skill requires (SpendGauge / RequiresGauge); a decrease without that condition is a reset
                // (e.g. Communio clearing the Void gauge) and does not make the skill a user of that gauge
                if (e.Kind != EffectKind.GaugeAdd || e.Value >= 0 || !RequiresGauge(s, e.Index) || job.Gauges[e.Index].Flat)
                    continue;
                var cost = -e.Value;
                if (GaugeUnit[e.Index] == 0 || cost < GaugeUnit[e.Index])
                    GaugeUnit[e.Index] = cost;
                if (net / cost > perPoint[e.Index])
                {
                    perPoint[e.Index] = net / cost;
                    GaugeSpentByGcd[e.Index] = s.IsGcd;
                }
            }
            if (s.Cooldown >= 0 && net > CdUnitValue[s.Cooldown])
            {
                CdUnitValue[s.Cooldown] = net;
                CdSpentByGcd[s.Cooldown] = s.IsGcd;
                CdValueDuration[s.Cooldown] = BuffDuration(s);
            }
        }
        // second pass: a skill that generates another gauge (Enshroud -> 5 Lemure, Soul Slice -> 50 Soul) is worth that gauge too
        foreach (var s in job.Skills)
        {
            var gained = GaugeGainValue(s, perPoint);
            if (gained <= 0)
                continue;
            var net = SkillValue(s) - (s.IsGcd ? FillerPerGcd : 0) + gained;
            foreach (var e in s.Effects)
                if (e.Kind == EffectKind.GaugeAdd && e.Value < 0 && RequiresGauge(s, e.Index) && !job.Gauges[e.Index].Flat && net / -e.Value > perPoint[e.Index])
                    perPoint[e.Index] = net / -e.Value;
            if (s.Cooldown >= 0 && net > CdUnitValue[s.Cooldown])
                CdUnitValue[s.Cooldown] = net;
        }
        for (var g = 0; g < job.Gauges.Length; ++g)
            GaugeUnitValue[g] = perPoint[g] * GaugeUnit[g];
        ComboChainValue = new float[job.Skills.Length];
        for (var i = 0; i < job.Skills.Length; ++i)
            ComboChainValue[i] = ChainValue(job, i, 0);
        foreach (var v in ComboChainValue)
            MaxComboChainValue = MathF.Max(MaxComboChainValue, v);
        // cooldowns whose skill grants a status other skills need (Ten Chi Jin -> its steps, Kassatsu -> Hyosho Ranryu) or a shadow (Bunshin):
        // worth what those skills add over filler
        foreach (var s in job.Skills)
        {
            if (s.Cooldown < 0)
                continue;
            var unlocked = SkillValue(s) - (s.IsGcd ? FillerPerGcd : 0);
            foreach (var e in s.Effects)
            {
                if (e.Kind != EffectKind.StatusApply)
                    continue;
                var st = job.Statuses[e.Index];
                if (st.ShadowPotency > 0)
                    unlocked += st.ShadowPotency * Math.Max(1, (int)e.Stacks);
                var best = 0f;
                foreach (var user in job.Skills)
                {
                    if (user == s || !RequiresStatus(user, e.Index))
                        continue;
                    var time = user.IsGcd ? (user.Recast > 0 ? user.Recast : job.BaseGcd) : 0;
                    best = MathF.Max(best, SkillValue(user) - FillerPps * time);
                }
                unlocked += best;
            }
            CdUnlockValue[s.Cooldown] = MathF.Max(CdUnlockValue[s.Cooldown], unlocked - CdUnitValue[s.Cooldown]);
        }
        // cooldowns whose skill changes the cycle state (Manafont refilling MP): worth their best use on the optimal cycle
        if (Cycles[1] is { } cycle)
        {
            foreach (var s in job.Skills)
            {
                if (s.Cooldown < 0)
                    continue;
                var gain = cycle.BestUseGain(s);
                if (gain > CdUnitValue[s.Cooldown])
                {
                    CdUnitValue[s.Cooldown] = gain;
                    CdSpentByGcd[s.Cooldown] = s.IsGcd;
                }
            }
        }
        GaugeUnitValueBase = [.. GaugeUnitValue];
        CdUnitValueBase = [.. CdUnitValue];
    }

    // duration of the damage-multiplier status a skill applies, if that status is where most of its value comes from
    private float BuffDuration(SkillDef s)
    {
        var buff = 0f;
        var duration = 0f;
        foreach (var e in s.Effects)
        {
            if (e.Kind != EffectKind.StatusApply)
                continue;
            var v = MathF.Max(0, Job.Statuses[e.Index].DamageMultiplier - 1) * FillerPps * e.Value;
            if (v > buff)
            {
                buff = v;
                duration = e.Value;
            }
        }
        return buff > s.Potency + s.PartyValue ? duration : 0;
    }

    // immediate potency plus the expected value of the damage-multiplier statuses the skill applies
    public float SkillValue(SkillDef s)
    {
        var p = s.Potency;
        foreach (var c in s.PotencyIf)
            p = MathF.Max(p, c.Potency);
        var v = p + s.PartyValue;
        if (s.DotStatus >= 0)
            v += s.DotPps * Job.Statuses[s.DotStatus].MaxDuration;
        foreach (var e in s.Effects)
            if (e.Kind == EffectKind.StatusApply)
                v += MathF.Max(0, Job.Statuses[e.Index].DamageMultiplier - 1) * FillerPps * e.Value;
        return v;
    }

    // Plays only "free" GCDs (no cost, no cooldown, no required status) greedily for 60 s to measure the filler rate and
    // how fast each gauge fills on its own.
    private static (float pps, float perGcd, float[] gain) MeasureFiller(JobDefinition job)
    {
        var ctx = new EvalContext();
        var tl = EngineTimeline.Open();
        var s = EngineState.Create(job);
        var total = 0f;
        var gcds = 0;
        var gaugeGain = new float[job.Gauges.Length];
        while (s.Time < 60)
        {
            SkillDef? best = null;
            var bestPotency = -1f;
            foreach (var skill in job.Skills)
            {
                if (!skill.IsGcd || skill.Cooldown >= 0 || !IsFree(skill) || !Simulator.IsLegal(job, s, tl, skill))
                    continue;
                var p = Simulator.Potency(job, s, skill);
                if (p > bestPotency)
                {
                    bestPotency = p;
                    best = skill;
                }
            }
            if (best == null)
                break;
            var before = s;
            total += Simulator.Execute(job, ref s, tl, best, ctx);
            for (var g = 0; g < job.Gauges.Length; ++g)
                gaugeGain[g] += s.Gauges[g] - before.Gauges[g];
            for (var g = 0; g < job.Gauges.Length; ++g)
                s.Gauges[g] = 0; // measure gain without hitting the cap
            ++gcds;
            Simulator.Advance(job, ref s, s.GcdReadyAt - s.Time, ctx);
        }
        var time = MathF.Max(s.Time, 1);
        for (var g = 0; g < gaugeGain.Length; ++g)
            gaugeGain[g] /= time;
        return (total / time, gcds > 0 ? total / gcds : 0, gaugeGain);
    }

    // best continuation from `skill` as the open combo step (a phase-neutral value: a full cycle sums to about zero)
    private float ChainValue(JobDefinition job, int skill, int depth)
    {
        if (depth > 8)
            return 0;
        var best = float.MinValue;
        foreach (var next in job.Skills)
            foreach (var p in next.PotencyIf)
                if (p.If.Kind == ConditionKind.ComboIs && p.If.Index == skill)
                    best = MathF.Max(best, p.Potency - FillerPerGcd + (next.Combo == ComboMode.Continue ? ChainValue(job, next.Index, depth + 1) : 0));
        return best == float.MinValue ? 0 : best;
    }

    private static float GaugeGainValue(SkillDef s, float[] perPoint)
    {
        var v = 0f;
        foreach (var e in s.Effects)
            if (e.Kind == EffectKind.GaugeAdd && e.Value > 0 && e.If.Kind == ConditionKind.None)
                v += e.Value * perPoint[e.Index];
        return v;
    }

    private static bool RequiresStatus(SkillDef s, int status)
    {
        foreach (var c in s.Conditions)
            if (c.Kind is ConditionKind.StatusActive or ConditionKind.StacksAtLeast or ConditionKind.StatusLeftAtLeast && c.Index == status)
                return true;
        return false;
    }

    private static bool RequiresGauge(SkillDef s, int gauge)
    {
        foreach (var c in s.Conditions)
            if (c.Kind == ConditionKind.GaugeAtLeast && c.Index == gauge && c.Value > 0)
                return true;
        return false;
    }

    private static bool IsFree(SkillDef s)
    {
        foreach (var c in s.Conditions)
            if (c.Kind is not (ConditionKind.ComboIs or ConditionKind.TargetsAtLeast or ConditionKind.TargetsAtMost or ConditionKind.StatusInactive))
                return false;
        foreach (var e in s.Effects)
            if (e.Kind == EffectKind.GaugeAdd && e.Value < 0 || e.Kind == EffectKind.StatusConsumeStacks)
                return false;
        return true;
    }
}
