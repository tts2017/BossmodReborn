using System;
using System.Runtime.CompilerServices;

namespace BossMod.Autorotation.Engine;

// Values the search needs per resource, refreshed by the upper tier. Allocated once per engine.
public sealed class EvalContext
{
    public readonly float[] GaugeWastePerPoint = new float[EngineLimits.MaxGauges];
    public readonly float[] CdWastePerSecond = new float[EngineLimits.MaxCooldowns];
    public readonly float[] StatusValuePerSecond = new float[EngineLimits.MaxStatuses];
    public float FillerPps;
    public float InvalidPenalty = 100000;
}

// The shared game mechanics: legality, time advance and skill execution. All methods are allocation-free.
public static class Simulator
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float GcdRecast(JobDefinition job, in EngineState s, SkillDef skill)
    {
        var recast = skill.Recast > 0 ? skill.Recast : job.BaseGcd;
        for (var i = 0; i < job.Statuses.Length; ++i)
        {
            if (s.StatusLeft[i] <= 0)
                continue;
            var st = job.Statuses[i];
            if (st.GcdRecastOverride > 0)
                recast = st.GcdRecastOverride;
            recast *= st.GcdRecastMultiplier;
        }
        return recast;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float CastTime(JobDefinition job, in EngineState s, SkillDef skill)
    {
        if (skill.CastTime <= 0)
            return 0;
        var cast = skill.CastTime;
        for (var i = 0; i < job.Statuses.Length; ++i)
            if (s.StatusLeft[i] > 0)
                cast *= job.Statuses[i].CastTimeMultiplier;
        return cast;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool Check(in EngineState s, in Condition c) => c.Kind switch
    {
        ConditionKind.None => true,
        ConditionKind.GaugeAtLeast => s.Gauges[c.Index] >= c.Value,
        ConditionKind.GaugeAtMost => s.Gauges[c.Index] <= c.Value,
        ConditionKind.StatusActive => s.StatusLeft[c.Index] > 0,
        ConditionKind.StatusInactive => s.StatusLeft[c.Index] <= 0,
        ConditionKind.StacksAtLeast => s.StatusLeft[c.Index] > 0 && s.StatusStacks[c.Index] >= c.Value,
        ConditionKind.ComboIs => s.ComboSkill == c.Index && s.ComboLeft > 0,
        ConditionKind.TargetsAtLeast => s.Targets >= c.Value,
        ConditionKind.TargetsAtMost => s.Targets <= c.Value,
        ConditionKind.ConeTargetsAtLeast => (s.ConeTargets > 0 ? s.ConeTargets : s.Targets) >= c.Value,
        ConditionKind.StatusLeftAtLeast => s.StatusLeft[c.Index] >= c.Value,
        _ => false
    };

    // whether `skill` can be used at s.Time (the caller advances the state to the execution time first)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool IsLegal(JobDefinition job, in EngineState s, in EngineTimeline tl, SkillDef skill)
    {
        if ((s.DisabledSkills & (1UL << skill.Index)) != 0)
            return false;
        if (skill.Cooldown >= 0 && s.Charges[skill.Cooldown] == 0)
            return false;
        foreach (ref readonly var c in skill.Conditions.AsSpan())
            if (!Check(s, c))
                return false;
        for (var i = 0; i < job.Statuses.Length; ++i)
            if (s.StatusLeft[i] > 0 && job.Statuses[i].AllowedSkills != 0 && (job.Statuses[i].AllowedSkills & (1UL << skill.Index)) == 0)
                return false;
        var t = s.Time;
        var cast = CastTime(job, s, skill);
        if (skill.RequiresTarget && (t >= tl.FightEndIn || tl.OverlapsDowntime(t, t + MathF.Max(cast, 0.01f))))
            return false;
        if (cast > 0 && tl.OverlapsNoCast(t, t + cast))
            return false;
        return true;
    }

    // Advances timers by dt; returns the waste accumulated (cooldowns sitting at max charges, MustNotExpire statuses running out).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Advance(JobDefinition job, ref EngineState s, float dt, EvalContext ctx)
    {
        if (dt <= 0)
            return 0;
        var waste = 0f;
        s.Time += dt;
        for (var i = 0; i < job.Statuses.Length; ++i)
        {
            if (s.StatusLeft[i] <= 0)
                continue;
            s.StatusLeft[i] -= dt;
            if (s.StatusLeft[i] <= 0)
            {
                s.StatusLeft[i] = 0;
                s.StatusStacks[i] = 0;
                if (job.Statuses[i].MustNotExpire)
                    waste += ctx.InvalidPenalty;
            }
        }
        for (var i = 0; i < job.Cooldowns.Length; ++i)
        {
            var cd = job.Cooldowns[i];
            var remaining = dt;
            while (remaining > 0 && s.Charges[i] < cd.MaxCharges)
            {
                if (s.CdReadyIn[i] > remaining)
                {
                    s.CdReadyIn[i] -= remaining;
                    remaining = 0;
                    break;
                }
                remaining -= s.CdReadyIn[i];
                ++s.Charges[i];
                s.CdReadyIn[i] = s.Charges[i] < cd.MaxCharges ? cd.Recast : 0;
            }
            if (remaining > 0)
                waste += remaining * ctx.CdWastePerSecond[i]; // sat at max charges: recharge time lost (drift)
        }
        if (s.ComboSkill != EngineLimits.NoCombo)
        {
            s.ComboLeft -= dt;
            if (s.ComboLeft <= 0)
            {
                s.ComboSkill = EngineLimits.NoCombo;
                s.ComboLeft = 0;
            }
        }
        return waste;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Potency(JobDefinition job, in EngineState s, SkillDef skill)
    {
        var p = skill.Potency;
        foreach (ref readonly var c in skill.PotencyIf.AsSpan())
        {
            if (Check(s, c.If))
            {
                p = c.Potency;
                break;
            }
        }
        var targets = skill.Cone && s.ConeTargets > 0 ? s.ConeTargets : s.Targets;
        if (skill.AoePotency > 0 && targets >= skill.MinAoeTargets)
            p = skill.AoePotency * targets;
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float DamageMultiplier(JobDefinition job, in EngineState s, in EngineTimeline tl)
    {
        var m = tl.BuffMultiplier(s.Time);
        for (var i = 0; i < job.Statuses.Length; ++i)
            if (s.StatusLeft[i] > 0)
                m *= job.Statuses[i].DamageMultiplier;
        return m;
    }

    // Executes a legal skill at s.Time; returns its immediate value (potency x multipliers + party value - waste).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Execute(JobDefinition job, ref EngineState s, in EngineTimeline tl, SkillDef skill, EvalContext ctx)
    {
        var t = s.Time;
        var value = 0f;
        if (t < tl.FightEndIn)
            value = Potency(job, s, skill) * DamageMultiplier(job, s, tl) + skill.PartyValue;

        foreach (ref readonly var e in skill.Effects.AsSpan())
        {
            if (!Check(s, e.If))
                continue;
            switch (e.Kind)
            {
                case EffectKind.GaugeAdd:
                    var max = job.Gauges[e.Index].Max;
                    var g = s.Gauges[e.Index] + (int)e.Value;
                    if (g > max)
                    {
                        value -= (g - max) * ctx.GaugeWastePerPoint[e.Index];
                        g = max;
                    }
                    s.Gauges[e.Index] = (short)Math.Max(0, g);
                    break;
                case EffectKind.StatusApply:
                    var st = job.Statuses[e.Index];
                    var left = e.Extend ? s.StatusLeft[e.Index] + e.Value : MathF.Max(s.StatusLeft[e.Index], e.Value);
                    if (left > st.MaxDuration)
                    {
                        if (st.Upkeep)
                            value -= (left - st.MaxDuration) * ctx.StatusValuePerSecond[e.Index];
                        left = st.MaxDuration;
                    }
                    if (!e.Extend && st.Upkeep && s.StatusLeft[e.Index] > 0)
                        value -= MathF.Min(s.StatusLeft[e.Index], e.Value) * ctx.StatusValuePerSecond[e.Index]; // clipped remainder of the old application
                    s.StatusLeft[e.Index] = left;
                    s.StatusStacks[e.Index] = (byte)Math.Min(st.MaxStacks, Math.Max(1, (int)e.Stacks));
                    break;
                case EffectKind.StatusAddStacks:
                    var sd = job.Statuses[e.Index];
                    s.StatusStacks[e.Index] = (byte)Math.Min(sd.MaxStacks, s.StatusStacks[e.Index] + e.Stacks);
                    if (s.StatusLeft[e.Index] <= 0)
                        s.StatusLeft[e.Index] = sd.MaxDuration;
                    break;
                case EffectKind.StatusConsumeStacks:
                    var stacks = s.StatusStacks[e.Index] - e.Stacks;
                    if (stacks <= 0)
                    {
                        s.StatusStacks[e.Index] = 0;
                        s.StatusLeft[e.Index] = 0;
                    }
                    else
                    {
                        s.StatusStacks[e.Index] = (byte)stacks;
                    }
                    break;
                case EffectKind.StatusRemove:
                    s.StatusLeft[e.Index] = 0;
                    s.StatusStacks[e.Index] = 0;
                    break;
                case EffectKind.CooldownReduce:
                    ReduceCooldown(job, ref s, e.Index, e.Value);
                    break;
            }
        }

        if (skill.Cooldown >= 0)
        {
            var cd = job.Cooldowns[skill.Cooldown];
            if (s.Charges[skill.Cooldown] == cd.MaxCharges)
                s.CdReadyIn[skill.Cooldown] = cd.Recast;
            --s.Charges[skill.Cooldown];
        }

        switch (skill.Combo)
        {
            case ComboMode.Start:
            case ComboMode.Continue:
                s.ComboSkill = (byte)skill.Index;
                s.ComboLeft = 30;
                break;
            case ComboMode.End:
            case ComboMode.Break:
                s.ComboSkill = EngineLimits.NoCombo;
                s.ComboLeft = 0;
                break;
        }

        var cast = CastTime(job, s, skill);
        if (skill.IsGcd)
            s.GcdReadyAt = t + MathF.Max(GcdRecast(job, s, skill), cast);
        s.AnimLockAt = t + (cast > 0 ? cast + 0.1f : skill.AnimationLock) + job.Latency;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ReduceCooldown(JobDefinition job, ref EngineState s, int index, float seconds)
    {
        var cd = job.Cooldowns[index];
        while (seconds > 0 && s.Charges[index] < cd.MaxCharges)
        {
            if (s.CdReadyIn[index] > seconds)
            {
                s.CdReadyIn[index] -= seconds;
                return;
            }
            seconds -= s.CdReadyIn[index];
            ++s.Charges[index];
            s.CdReadyIn[index] = s.Charges[index] < cd.MaxCharges ? cd.Recast : 0;
        }
    }
}
