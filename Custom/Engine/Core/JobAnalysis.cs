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
    public readonly float[] StatusValuePerSecond; // damage-multiplier statuses: (m-1) x filler rate
    public readonly float MaxSkillValue;        // upper bound of one skill's immediate value (single target, unbuffed)
    public readonly float[] ComboChainValue;    // per skill: value of having it as the last combo step = sum over the remaining chain of (combo potency - filler per GCD)
    public readonly float MaxComboChainValue;

    public JobAnalysis(JobDefinition job)
    {
        Job = job;
        (FillerPps, FillerPerGcd, GaugeGainPerSecond) = MeasureFiller(job);

        StatusValuePerSecond = new float[job.Statuses.Length];
        for (var i = 0; i < job.Statuses.Length; ++i)
            StatusValuePerSecond[i] = MathF.Max(0, job.Statuses[i].DamageMultiplier - 1) * FillerPps;

        GaugeUnit = new float[job.Gauges.Length];
        GaugeUnitValue = new float[job.Gauges.Length];
        GaugeSpentByGcd = new bool[job.Gauges.Length];
        CdUnitValue = new float[job.Cooldowns.Length];
        CdSpentByGcd = new bool[job.Cooldowns.Length];
        CdValueDuration = new float[job.Cooldowns.Length];
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
                if (e.Kind != EffectKind.GaugeAdd || e.Value >= 0 || !RequiresGauge(s, e.Index))
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
                if (e.Kind == EffectKind.GaugeAdd && e.Value < 0 && RequiresGauge(s, e.Index) && net / -e.Value > perPoint[e.Index])
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
