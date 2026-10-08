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
    // a cooldown this close to ready counts as ready: recasts read from the client land a hair after the GCD they line up with
    // (float timers), and the client lets an action through that much early anyway
    public const float CdEpsilon = 0.005f;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float GcdRecast(JobDefinition job, in EngineState s, SkillDef skill)
    {
        var recast = skill.Recast > 0 ? skill.Recast : job.BaseGcd;
        foreach (var i in job.RecastStatuses)
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
        foreach (var i in job.CastStatuses)
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
        ConditionKind.CooldownAtLeast => s.Charges[c.Index] == 0 && s.CdReadyIn[c.Index] >= c.Value,
        ConditionKind.AnyStatusActive => s.StatusLeft[c.Index] > 0 || AnyInMask(s, (int)c.Value),
        ConditionKind.CooldownAtMost => s.Charges[c.Index] > 0 || s.CdReadyIn[c.Index] <= c.Value,
        ConditionKind.ChargesAtLeast => s.Charges[c.Index] >= c.Value,
        ConditionKind.RechargeAtMost => s.CdReadyIn[c.Index] <= c.Value,
        ConditionKind.ComboIsNot => s.ComboSkill == EngineLimits.NoCombo || s.ComboLeft <= 0 || c.Index >= 0 && s.ComboSkill != c.Index,
        ConditionKind.StatusLeftAtMost => s.StatusLeft[c.Index] <= c.Value,
        _ => false
    };

    // legality apart from the cooldown charge (analysis: what a skill would do if it were available)
    public static bool IsLegalIgnoringCooldown(JobDefinition job, in EngineState s, in EngineTimeline tl, SkillDef skill)
    {
        var c = s;
        if (skill.Cooldown >= 0)
            c.Charges[skill.Cooldown] = 1;
        return IsLegal(job, c, tl, skill);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AnyInMask(in EngineState s, int mask)
    {
        for (var i = 0; mask != 0; ++i, mask >>= 1)
            if ((mask & 1) != 0 && s.StatusLeft[i] > 0)
                return true;
        return false;
    }

    // bit = status index, set while the status is active (for IsLegal's quick pre-check)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static uint ActiveStatusMask(JobDefinition job, in EngineState s)
    {
        var m = 0u;
        for (var i = 0; i < job.Statuses.Length; ++i)
            if (s.StatusLeft[i] > 0)
                m |= 1u << i;
        return m;
    }

    // IsLegal with a precomputed ActiveStatusMask: skills whose status conditions fail are rejected without evaluating them
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool IsLegal(JobDefinition job, in EngineState s, in EngineTimeline tl, SkillDef skill, uint activeStatuses)
        => (skill.RequiredStatusMask & ~activeStatuses) == 0 && (skill.ForbiddenStatusMask & activeStatuses) == 0 && IsLegal(job, s, tl, skill);

    // whether no downtime or no-cast window starts within the longest span IsLegal looks ahead from s.Time (then IsLegal skips them)
    public static bool WindowsClear(JobDefinition job, in EngineState s, in EngineTimeline tl)
        => !tl.OverlapsDowntime(s.Time, s.Time + job.MaxWindowCheck) && !tl.OverlapsNoCast(s.Time, s.Time + job.MaxWindowCheck);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool IsLegal(JobDefinition job, in EngineState s, in EngineTimeline tl, SkillDef skill, uint activeStatuses, bool windowsClear)
    {
        if ((skill.RequiredStatusMask & ~activeStatuses) != 0 || (skill.ForbiddenStatusMask & activeStatuses) != 0)
            return false;
        if (!windowsClear)
            return IsLegal(job, s, tl, skill);
        if ((s.DisabledSkills & (1UL << skill.Index)) != 0 || (s.HeldSkills & (1UL << skill.Index)) != 0 && s.Time < s.HeldUntil)
            return false;
        if (skill.Cooldown >= 0 && s.Charges[skill.Cooldown] == 0)
            return false;
        foreach (ref readonly var c in skill.Conditions.AsSpan())
            if (!Check(s, c))
                return false;
        foreach (var i in job.LockStatuses)
            if (s.StatusLeft[i] > 0 && (job.Statuses[i].AllowedSkills & (1UL << skill.Index)) == 0)
                return false;
        return !(skill.RequiresTarget || skill.UptimeNeeded > 0) || s.Time < tl.FightEndIn;
    }

    // whether `skill` can be used at s.Time (the caller advances the state to the execution time first)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static bool IsLegal(JobDefinition job, in EngineState s, in EngineTimeline tl, SkillDef skill)
    {
        if ((s.DisabledSkills & (1UL << skill.Index)) != 0 || (s.HeldSkills & (1UL << skill.Index)) != 0 && s.Time < s.HeldUntil)
            return false;
        if (skill.Cooldown >= 0 && s.Charges[skill.Cooldown] == 0)
            return false;
        foreach (ref readonly var c in skill.Conditions.AsSpan())
            if (!Check(s, c))
                return false;
        foreach (var i in job.LockStatuses)
            if (s.StatusLeft[i] > 0 && (job.Statuses[i].AllowedSkills & (1UL << skill.Index)) == 0)
                return false;
        var t = s.Time;
        var cast = CastTime(job, s, skill);
        if (skill.RequiresTarget && (t >= tl.FightEndIn || tl.OverlapsDowntime(t, t + MathF.Max(cast, 0.01f))))
            return false;
        if (cast > 0 && tl.OverlapsNoCast(t, t + cast))
            return false;
        if (skill.UptimeNeeded > 0 && (t >= tl.FightEndIn || tl.OverlapsDowntime(t, t + skill.UptimeNeeded)))
            return false;
        if (skill.StillNeeded > 0 && tl.OverlapsNoCast(t, t + skill.StillNeeded))
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
            var pg = job.Statuses[i].PeriodicGauge;
            if (pg >= 0)
            {
                // repeating timer: grant the gauge on each expiry, restart (lost grants over the cap are waste)
                while (s.StatusLeft[i] <= 0)
                {
                    var gv = s.Gauges[pg] + job.Statuses[i].PeriodicAmount;
                    if (gv > job.Gauges[pg].Max)
                    {
                        waste += (gv - job.Gauges[pg].Max) * ctx.GaugeWastePerPoint[pg];
                        gv = job.Gauges[pg].Max;
                    }
                    s.Gauges[pg] = (short)gv;
                    s.StatusLeft[i] += job.Statuses[i].MaxDuration;
                }
                continue;
            }
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
                if (s.CdReadyIn[i] > remaining + CdEpsilon)
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
        if (skill.AoeExtraPotency > 0 && targets > 1 && skill.Potency > 0)
            p += skill.AoeExtraPotency * (p / skill.Potency) * (targets - 1);
        return p;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float DamageMultiplier(JobDefinition job, in EngineState s, in EngineTimeline tl)
    {
        var m = tl.BuffMultiplier(s.Time);
        foreach (var i in job.DamageStatuses)
            if (s.StatusLeft[i] > 0)
                m *= job.Statuses[i].DamageMultiplier;
        return m;
    }

    // Cheap estimate of Execute's value without simulating the skill (potency x multipliers, party value, DoT seconds gained, shadow
    // hits; no waste terms): the search uses it to choose which moves to simulate below the root. mult = DamageMultiplier at s.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float QuickValue(JobDefinition job, in EngineState s, in EngineTimeline tl, SkillDef skill, float mult)
    {
        if (s.Time >= tl.FightEndIn)
            return 0;
        var v = Potency(job, s, skill) * mult + skill.PartyValue;
        if (skill.DotStatus >= 0)
        {
            var fightLeft = tl.FightEndIn - s.Time;
            var duration = 0f;
            foreach (ref readonly var e in skill.Effects.AsSpan())
                if (e.Kind == EffectKind.StatusApply && e.Index == skill.DotStatus)
                    duration = e.Value;
            var gained = MathF.Max(0, MathF.Min(duration, fightLeft)) - MathF.Max(0, MathF.Min(s.StatusLeft[skill.DotStatus], fightLeft));
            v += skill.DotPps * gained * (skill.DotAoe ? Math.Max(1, (int)s.Targets) : 1) * mult;
        }
        if (skill.Weaponskill)
        {
            foreach (var i in job.ShadowStatuses)
            {
                if (s.StatusLeft[i] <= 0)
                    continue;
                var st = job.Statuses[i];
                var aoe = skill.AoeExtraPotency > 0 || skill.AoePotency > 0 && s.Targets >= skill.MinAoeTargets;
                v += (aoe ? st.ShadowAoePotency * Math.Max(1, (int)s.Targets) : st.ShadowPotency) * mult;
            }
        }
        return v;
    }

    // Executes a legal skill at s.Time; returns its immediate value (potency x multipliers + party value - waste).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static float Execute(JobDefinition job, ref EngineState s, in EngineTimeline tl, SkillDef skill, EvalContext ctx)
    {
        var t = s.Time;
        var value = 0f;
        var mult = 1f;
        if (t < tl.FightEndIn)
        {
            mult = DamageMultiplier(job, s, tl);
            value = Potency(job, s, skill) * mult + skill.PartyValue;
        }
        // cast time and instant-cast consumption are decided by the state before the skill's own effects
        var cast = CastTime(job, s, skill);
        if (cast <= 0 && skill.CastTime > 0)
            ConsumeInstantCast(job, ref s);

        if (skill.Weaponskill)
            value += ShadowHits(job, ref s, skill, mult, ctx);

        // effect conditions see the state before the skill (so e.g. "if Astral Fire: go to Umbral Ice" and "if Umbral Ice: go to Astral Fire" do not chain)
        // (evaluated up front into a mask instead of copying the state)
        var effects = skill.Effects;
        var apply = ~0UL;
        if (skill.ConditionalEffects)
            for (var i = 0; i < effects.Length; ++i)
                if (!Check(s, effects[i].If) || !Check(s, effects[i].If2) || !Check(s, effects[i].If3))
                    apply &= ~(1UL << i);
        for (var ei = 0; ei < effects.Length; ++ei)
        {
            if ((apply & (1UL << ei)) == 0)
                continue;
            ref readonly var e = ref effects[ei];
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
                case EffectKind.GaugeSet:
                    s.Gauges[e.Index] = (short)Math.Clamp((int)e.Value, 0, job.Gauges[e.Index].Max);
                    break;
                case EffectKind.GaugeScale:
                    s.Gauges[e.Index] = (short)Math.Clamp((int)(s.Gauges[e.Index] * e.Value), 0, job.Gauges[e.Index].Max);
                    break;
                case EffectKind.StatusApply:
                    var st = job.Statuses[e.Index];
                    var oldLeft = s.StatusLeft[e.Index];
                    var left = e.Extend ? oldLeft + e.Value : MathF.Max(oldLeft, e.Value);
                    if (left > st.MaxDuration)
                    {
                        if (st.Upkeep)
                            value -= (left - st.MaxDuration) * ctx.StatusValuePerSecond[e.Index];
                        left = st.MaxDuration;
                    }
                    if (e.Index == skill.DotStatus)
                    {
                        // a DoT replaces the old one: value = DoT seconds gained before the fight ends, snapshotting the current multipliers
                        left = e.Value;
                        var fightLeft = tl.FightEndIn - t;
                        var gained = MathF.Max(0, MathF.Min(left, fightLeft)) - MathF.Max(0, MathF.Min(oldLeft, fightLeft));
                        var hits = skill.DotAoe ? Math.Max(1, (int)s.Targets) : 1;
                        value += skill.DotPps * gained * hits * mult;
                    }
                    else if (!e.Extend && st.Upkeep && oldLeft > 0)
                    {
                        value -= MathF.Min(oldLeft, e.Value) * ctx.StatusValuePerSecond[e.Index]; // clipped remainder of the old application
                    }
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
                s.ComboSkill = (byte)skill.Index;
                s.ComboLeft = 30;
                break;
            case ComboMode.Continue:
                // a middle step continues the combo only when pressed as combo (out of combo it breaks it, as in the game)
                var continues = false;
                foreach (ref readonly var c in skill.PotencyIf.AsSpan())
                    continues |= c.If.Kind == ConditionKind.ComboIs && Check(s, c.If);
                s.ComboSkill = continues ? (byte)skill.Index : EngineLimits.NoCombo;
                s.ComboLeft = continues ? 30 : 0;
                break;
            case ComboMode.End:
            case ComboMode.Break:
                s.ComboSkill = EngineLimits.NoCombo;
                s.ComboLeft = 0;
                break;
        }

        if (skill.IsGcd)
            s.GcdReadyAt = t + MathF.Max(GcdRecast(job, s, skill), cast);
        s.AnimLockAt = t + (cast > 0 ? cast + 0.1f : skill.AnimationLock) + job.Latency;
        return value;
    }

    // shadows (Bunshin): each active one hits along with a weaponskill, using a stack and adding its gauge
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static float ShadowHits(JobDefinition job, ref EngineState s, SkillDef skill, float mult, EvalContext ctx)
    {
        var value = 0f;
        foreach (var i in job.ShadowStatuses)
        {
            var st = job.Statuses[i];
            if (s.StatusLeft[i] <= 0)
                continue;
            var aoe = skill.AoeExtraPotency > 0 || skill.AoePotency > 0 && s.Targets >= skill.MinAoeTargets;
            value += (aoe ? st.ShadowAoePotency * Math.Max(1, (int)s.Targets) : st.ShadowPotency) * mult;
            if (st.ShadowGauge >= 0)
            {
                var g = s.Gauges[st.ShadowGauge] + st.ShadowGaugeAmount;
                var max = job.Gauges[st.ShadowGauge].Max;
                if (g > max)
                {
                    value -= (g - max) * ctx.GaugeWastePerPoint[st.ShadowGauge];
                    g = max;
                }
                s.Gauges[st.ShadowGauge] = (short)g;
            }
            if (s.StatusStacks[i] > 1)
                --s.StatusStacks[i];
            else
            {
                s.StatusStacks[i] = 0;
                s.StatusLeft[i] = 0;
            }
        }
        return value;
    }

    // a skill with a cast time was made instant: use up the first active instant-cast status (one stack)
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ConsumeInstantCast(JobDefinition job, ref EngineState s)
    {
        foreach (var i in job.InstantStatuses)
        {
            if (s.StatusLeft[i] <= 0)
                continue;
            if (s.StatusStacks[i] > 1)
            {
                --s.StatusStacks[i];
            }
            else
            {
                s.StatusStacks[i] = 0;
                s.StatusLeft[i] = 0;
            }
            return;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void ReduceCooldown(JobDefinition job, ref EngineState s, int index, float seconds)
    {
        var cd = job.Cooldowns[index];
        while (seconds > 0 && s.Charges[index] < cd.MaxCharges)
        {
            if (s.CdReadyIn[index] > seconds + CdEpsilon)
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
