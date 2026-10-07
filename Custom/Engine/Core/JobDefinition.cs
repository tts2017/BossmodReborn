using System;
using System.Collections.Generic;

namespace BossMod.Autorotation.Engine;

public enum ConditionKind : byte
{
    None,
    GaugeAtLeast,
    GaugeAtMost,
    StatusActive,
    StatusInactive,
    StacksAtLeast,
    ComboIs,
    TargetsAtLeast,
    TargetsAtMost,
    ConeTargetsAtLeast,
    StatusLeftAtLeast, // status active with at least Value seconds left
    CooldownAtLeast,   // cooldown group Index has no charge and its next charge is at least Value seconds away
}

public readonly record struct Condition(ConditionKind Kind, short Index, float Value)
{
    public static readonly Condition Always = new(ConditionKind.None, 0, 0);
}

public enum EffectKind : byte
{
    GaugeAdd,
    StatusApply,     // set remaining time to min(left + Value, max) if Extend, else max(left, Value) capped; stacks = Stacks
    StatusAddStacks,
    StatusConsumeStacks, // removes the status when stacks reach 0
    StatusRemove,
    CooldownReduce,
    GaugeSet,        // gauge = Value (element stance, a refilled bar)
    GaugeScale,      // gauge = floor(gauge x Value)
}

// If / If2: the effect applies only when both hold; conditions are checked against the state before the skill (all effects see the same state)
public readonly record struct Effect(EffectKind Kind, short Index, float Value, byte Stacks, bool Extend, Condition If)
{
    public Condition If2 { get; init; } = Condition.Always;
}

public enum ComboMode : byte
{
    Break,    // weaponskill outside any combo: clears the combo (FFXIV default for GCDs)
    Neutral,  // does not touch the combo (spells, abilities, most gauge spenders)
    Start,    // sets the combo to this skill
    Continue, // requires nothing, sets the combo to this skill (ComboFrom gives the bonus potency)
    End,      // clears the combo
}

public readonly record struct ConditionalPotency(Condition If, float Potency);

public sealed class SkillDef
{
    public required string Name;
    public int Index;
    public uint ActionId;
    public bool IsGcd;
    public float Potency;
    public float AoePotency;      // per target, used when Targets >= MinAoeTargets
    public int MinAoeTargets = 99;
    public float AoeExtraPotency; // potency on each target after the first (falloff AoE: first target Potency, others this x the same multiplier); always used
    public int DotStatus = -1;    // damage-over-time status this skill applies (value = DotPps x seconds of DoT gained, before the fight ends)
    public float DotPps;          // DoT potency per second on one target (tick potency / tick interval)
    public bool DotAoe;           // the DoT lands on every target
    public bool Weaponskill;      // eligible for shadow hits (StatusDef.ShadowPotency)
    public bool Cone;             // AoE shape counted with EngineState.ConeTargets instead of Targets
    public float CastTime;
    public float AnimationLock = 0.6f;
    public float Recast;          // GCD recast for GCDs (0 = job base GCD); ignored for oGCDs (they use the cooldown group)
    public int Cooldown = -1;     // cooldown group index
    public float PartyValue;      // potency-equivalent value for the party (raid buffs)
    public bool RequiresTarget = true;
    public float UptimeNeeded;    // >0: not usable if downtime starts within this many seconds (buffs and resources that would be wasted into a gap)
    public float StillNeeded;     // >0: not usable if forced movement (a no-cast window) starts within this many seconds (placed effects)
    public ComboMode Combo;
    public Condition[] Conditions = [];
    public Effect[] Effects = [];
    public ConditionalPotency[] PotencyIf = [];

    // derived at Build
    public float ComboBonus;      // best extra potency the next combo step gains if the combo is kept
    public bool ConditionalEffects; // some effect has a condition (Execute then keeps the pre-skill state for them)
}

public sealed class GaugeDef
{
    public required string Name;
    public int Max;
    public bool Flat;            // not planned by the upper tier and never counted as overcap waste (MP, element stance); its leaf value is weights.GaugeValue per point
}

public sealed class StatusDef
{
    public required string Name;
    public float MaxDuration;
    public int MaxStacks = 1;
    public float DamageMultiplier = 1;
    public float GcdRecastMultiplier = 1; // haste (Ley Lines 0.85) or mode GCD (use GcdRecastOverride)
    public float GcdRecastOverride;       // e.g. Enshroud 1.5 s; 0 = none
    public float CastTimeMultiplier = 1;  // 0 = instant (Swiftcast); consumption is an effect of the casting skill
    public ulong AllowedSkills;           // non-zero: while active only these skills (bit = skill index) may be used
    public bool MustNotExpire;            // expiry inside the search is penalized as invalid (BLM element)
    public bool Upkeep;                   // refresh overflow counts as waste
    public int PeriodicGauge = -1;        // a running timer: on expiry adds PeriodicAmount to this gauge and restarts at MaxDuration (Polyglot)
    public int PeriodicAmount;
    public bool ConsumedByCast;           // instant-cast status (CastTimeMultiplier 0): a skill with a cast time uses it up (one stack); the first active one in definition order is used
    public float ShadowPotency;           // while active, every Weaponskill hits again for this (x targets for AoE skills: ShadowAoePotency), uses a stack (Bunshin)
    public float ShadowAoePotency;
    public int ShadowGauge = -1;          // and adds ShadowGaugeAmount to this gauge
    public int ShadowGaugeAmount;
}

public sealed class CooldownDef
{
    public required string Name;
    public float Recast;
    public int MaxCharges = 1;
}

public sealed class JobDefinition
{
    public required string Name;
    public float BaseGcd;
    public float Latency = 0.05f;
    public float FillerOverride;   // >0: nominal filler potency per GCD (jobs without a free filler GCD)
    public int[] CycleGauges = [];   // CycleModel state: gauges (bucketed by CycleSteps) and statuses (present / absent)
    public int[] CycleSteps = [];
    public int[] CycleStatuses = [];
    public int[] CycleCooldowns = [];
    public GaugeDef[] Gauges = [];
    public StatusDef[] Statuses = [];
    public CooldownDef[] Cooldowns = [];
    public SkillDef[] Skills = [];

    // plain loops (no delegates): called every frame by the state readers, must not allocate
    public int GaugeIndex(string name) { for (var i = 0; i < Gauges.Length; ++i) if (Gauges[i].Name == name) return i; throw Unknown("gauge", name); }
    public int StatusIndex(string name) { for (var i = 0; i < Statuses.Length; ++i) if (Statuses[i].Name == name) return i; throw Unknown("status", name); }
    public int CooldownIndex(string name) { for (var i = 0; i < Cooldowns.Length; ++i) if (Cooldowns[i].Name == name) return i; throw Unknown("cooldown", name); }
    public int SkillIndex(string name) { for (var i = 0; i < Skills.Length; ++i) if (Skills[i].Name == name) return i; throw Unknown("skill", name); }

    private static ArgumentException Unknown(string kind, string name) => new($"unknown {kind} '{name}'");
}

// Fluent builder: skills refer to gauges / statuses / cooldowns / other skills by name; references are resolved and
// checked in Build(), so a typo fails at startup instead of silently producing a wrong rotation.
public sealed class JobBuilder(string name, float baseGcd)
{
    private readonly List<GaugeDef> _gauges = [];
    private readonly List<StatusDef> _statuses = [];
    private readonly List<CooldownDef> _cooldowns = [];
    private readonly List<SkillBuilder> _skills = [];
    private float _latency = 0.05f;
    private float _filler;
    private readonly List<(string Status, string Gauge, int Amount)> _periodic = [];
    private readonly List<(string Gauge, int Step)> _cycleGauges = [];
    private readonly List<string> _cycleStatuses = [];
    private readonly List<string> _cycleCooldowns = [];

    public JobBuilder Latency(float seconds) { _latency = seconds; return this; }
    public JobBuilder Gauge(string gauge, int max, bool flat = false) { _gauges.Add(new() { Name = gauge, Max = max, Flat = flat }); return this; }
    // nominal filler potency per GCD, for jobs whose GCDs all need a resource (the analysis cannot measure a free filler)
    public JobBuilder FillerPotency(float perGcd) { _filler = perGcd; return this; }
    // `status` is a repeating timer that adds `amount` to `gauge` each time it runs out
    public JobBuilder Periodic(string status, string gauge, int amount) { _periodic.Add((status, gauge, amount)); return this; }
    // `status` is a shadow: while active each Weaponskill also hits for potency (aoePotency per target for AoE skills), uses one stack and adds gaugeAmount to gauge
    public JobBuilder Shadow(string status, float potency, float aoePotency, string? gauge = null, int gaugeAmount = 0) { _shadows.Add((status, potency, aoePotency, gauge, gaugeAmount)); return this; }
    private readonly List<(string Status, float Potency, float Aoe, string? Gauge, int Amount)> _shadows = [];
    // long-run cycle state for CycleModel (resources whose trade-offs span more than the search horizon); the first gauge starts full
    public JobBuilder CycleGauge(string gauge, int step) { _cycleGauges.Add((gauge, step)); return this; }
    public JobBuilder CycleStatus(string status) { _cycleStatuses.Add(status); return this; }
    public JobBuilder CycleCooldown(string cd) { _cycleCooldowns.Add(cd); return this; } // a short cooldown the cycle relies on (single charge)
    public JobBuilder Cooldown(string cd, float recast, int maxCharges = 1) { _cooldowns.Add(new() { Name = cd, Recast = recast, MaxCharges = maxCharges }); return this; }

    public JobBuilder Status(string status, float maxDuration, float damageMultiplier = 1, int maxStacks = 1, bool upkeep = false, bool mustNotExpire = false,
        float gcdRecastMultiplier = 1, float gcdRecastOverride = 0, float castTimeMultiplier = 1, bool consumedByCast = false)
    {
        _statuses.Add(new()
        {
            Name = status, MaxDuration = maxDuration, DamageMultiplier = damageMultiplier, MaxStacks = maxStacks, Upkeep = upkeep, MustNotExpire = mustNotExpire,
            GcdRecastMultiplier = gcdRecastMultiplier, GcdRecastOverride = gcdRecastOverride, CastTimeMultiplier = castTimeMultiplier, ConsumedByCast = consumedByCast
        });
        return this;
    }

    public SkillBuilder Gcd(string skill, float potency, uint actionId = 0) => AddSkill(skill, potency, actionId, true);
    public SkillBuilder Ogcd(string skill, float potency, string? cooldown = null, uint actionId = 0)
    {
        var s = AddSkill(skill, potency, actionId, false);
        if (cooldown != null)
            s.UsesCooldown(cooldown);
        return s;
    }

    private SkillBuilder AddSkill(string skill, float potency, uint actionId, bool gcd)
    {
        var b = new SkillBuilder(this, new SkillDef { Name = skill, Potency = potency, ActionId = actionId, IsGcd = gcd, Combo = gcd ? ComboMode.Break : ComboMode.Neutral });
        _skills.Add(b);
        return b;
    }

    public JobDefinition Build()
    {
        if (_gauges.Count > EngineLimits.MaxGauges || _statuses.Count > EngineLimits.MaxStatuses || _cooldowns.Count > EngineLimits.MaxCooldowns || _skills.Count > EngineLimits.MaxSkills)
            throw new InvalidOperationException("job definition exceeds EngineLimits");
        var job = new JobDefinition
        {
            Name = name, BaseGcd = baseGcd, Latency = _latency, FillerOverride = _filler,
            Gauges = [.. _gauges], Statuses = [.. _statuses], Cooldowns = [.. _cooldowns],
            Skills = new SkillDef[_skills.Count]
        };
        for (var i = 0; i < _skills.Count; ++i)
        {
            job.Skills[i] = _skills[i].Def;
            job.Skills[i].Index = i;
        }
        foreach (var b in _skills)
            b.Resolve(job);
        foreach (var b in _statusLocks)
            b(job);
        job.CycleGauges = [.. _cycleGauges.ConvertAll(c => job.GaugeIndex(c.Gauge))];
        job.CycleSteps = [.. _cycleGauges.ConvertAll(c => Math.Max(1, c.Step))];
        job.CycleStatuses = [.. _cycleStatuses.ConvertAll(job.StatusIndex)];
        job.CycleCooldowns = [.. _cycleCooldowns.ConvertAll(job.CooldownIndex)];
        foreach (var (status, potency, aoe, gauge, amount) in _shadows)
        {
            var st = job.Statuses[job.StatusIndex(status)];
            st.ShadowPotency = potency;
            st.ShadowAoePotency = aoe;
            st.ShadowGauge = gauge != null ? job.GaugeIndex(gauge) : -1;
            st.ShadowGaugeAmount = amount;
        }
        foreach (var (status, gauge, amount) in _periodic)
        {
            var st = job.Statuses[job.StatusIndex(status)];
            st.PeriodicGauge = job.GaugeIndex(gauge);
            st.PeriodicAmount = amount;
        }
        foreach (var s in job.Skills)
            foreach (var e in s.Effects)
                s.ConditionalEffects |= e.If.Kind != ConditionKind.None || e.If2.Kind != ConditionKind.None;
        // cone skills check the cone target count
        foreach (var s in job.Skills)
            if (s.Cone)
                for (var i = 0; i < s.Conditions.Length; ++i)
                    if (s.Conditions[i].Kind == ConditionKind.TargetsAtLeast)
                        s.Conditions[i] = s.Conditions[i] with { Kind = ConditionKind.ConeTargetsAtLeast };
        foreach (var s in job.Skills)
        {
            s.RequiresTarget &= s.Potency > 0 || s.AoePotency > 0 || s.PotencyIf.Length > 0;
        }
        foreach (var s in job.Skills)
        {
            var bonus = 0f;
            foreach (var next in job.Skills)
                foreach (var p in next.PotencyIf)
                    if (p.If.Kind == ConditionKind.ComboIs && p.If.Index == s.Index)
                        bonus = MathF.Max(bonus, p.Potency - next.Potency);
            s.ComboBonus = bonus;
        }
        return job;
    }

    private readonly List<Action<JobDefinition>> _statusLocks = [];

    // while `status` is active only the listed skills can be used (forced sequences such as Ten Chi Jin)
    public JobBuilder LockSkillsDuring(string status, params string[] allowed)
    {
        _statusLocks.Add(job =>
        {
            ulong mask = 0;
            foreach (var a in allowed)
                mask |= 1UL << job.SkillIndex(a);
            job.Statuses[job.StatusIndex(status)].AllowedSkills = mask;
        });
        return this;
    }

    public sealed class SkillBuilder(JobBuilder owner, SkillDef def)
    {
        internal readonly SkillDef Def = def;
        private readonly List<Func<JobDefinition, Condition>> _conditions = [];
        private readonly List<Func<JobDefinition, Effect>> _effects = [];
        private readonly List<Func<JobDefinition, ConditionalPotency>> _potencies = [];
        private string? _cooldown;
        private Func<JobDefinition, Condition>? _pendingEffectCondition;
        private Func<JobDefinition, Condition>? _pendingEffectCondition2;
        private string? _dotStatus;

        // chaining back to the job builder
        public SkillBuilder Gcd(string skill, float potency, uint actionId = 0) => owner.Gcd(skill, potency, actionId);
        public SkillBuilder Ogcd(string skill, float potency, string? cooldown = null, uint actionId = 0) => owner.Ogcd(skill, potency, cooldown, actionId);
        public JobBuilder Job => owner;
        public JobDefinition Build() => owner.Build();

        public SkillBuilder ActionId(uint id) { Def.ActionId = id; return this; }
        public SkillBuilder Aoe(float potencyPerTarget, int minTargets) { Def.AoePotency = potencyPerTarget; Def.MinAoeTargets = minTargets; return this; }
        public SkillBuilder Cone() { Def.Cone = true; return this; }
        // falloff AoE: the primary target takes the skill potency, every other target `extraPerTarget` (scaled like the primary hit)
        public SkillBuilder AoeFalloff(float extraPerTarget) { Def.AoeExtraPotency = extraPerTarget; return this; }
        // applies a damage-over-time status; its value is counted when applied (potency per tick / interval x seconds gained)
        public SkillBuilder Dot(string status, float duration, float potencyPerTick, float tickInterval = 3, bool aoe = false)
        {
            _dotStatus = status;
            Def.DotPps = potencyPerTick / tickInterval;
            Def.DotAoe = aoe;
            return ApplyStatus(status, duration);
        }
        public SkillBuilder Cast(float seconds) { Def.CastTime = seconds; return this; }
        public SkillBuilder Lock(float seconds) { Def.AnimationLock = seconds; return this; }
        public SkillBuilder Recast(float seconds) { Def.Recast = seconds; return this; }
        public SkillBuilder PartyValue(float potency) { Def.PartyValue = potency; return this; }
        public SkillBuilder NoTarget() { Def.RequiresTarget = false; return this; }
        public SkillBuilder Weaponskill() { Def.Weaponskill = true; return this; }
        public SkillBuilder NeedsUptime(float seconds) { Def.UptimeNeeded = seconds; return this; }
        public SkillBuilder NeedsStanding(float seconds) { Def.StillNeeded = seconds; return this; }
        public SkillBuilder UsesCooldown(string cd) { _cooldown = cd; return this; }

        public SkillBuilder StartsCombo() { Def.Combo = ComboMode.Start; return this; }
        public SkillBuilder EndsCombo() { Def.Combo = ComboMode.End; return this; }
        public SkillBuilder ComboNeutral() { Def.Combo = ComboMode.Neutral; return this; }
        // potency when the previous combo skill was `from`; keeps the combo going
        public SkillBuilder ComboFrom(string from, float potency)
        {
            if (Def.Combo is ComboMode.Break or ComboMode.Neutral)
                Def.Combo = ComboMode.Continue;
            _potencies.Add(job => new(new(ConditionKind.ComboIs, (short)job.SkillIndex(from), 0), potency));
            return this;
        }
        public SkillBuilder RequiresCombo(string from) { _conditions.Add(job => new(ConditionKind.ComboIs, (short)job.SkillIndex(from), 0)); return ComboFrom(from, Def.Potency); }

        public SkillBuilder PotencyIfStatus(string status, float potency) { _potencies.Add(job => new(new(ConditionKind.StatusActive, (short)job.StatusIndex(status), 0), potency)); return this; }
        // potency when the gauge is at least `atLeast` (first matching PotencyIf wins: list stronger cases first)
        public SkillBuilder PotencyIfGauge(string gauge, int atLeast, float potency) { _potencies.Add(job => new(new(ConditionKind.GaugeAtLeast, (short)job.GaugeIndex(gauge), atLeast), potency)); return this; }

        public SkillBuilder RequiresGauge(string gauge, int atLeast) { _conditions.Add(job => new(ConditionKind.GaugeAtLeast, (short)job.GaugeIndex(gauge), atLeast)); return this; }
        public SkillBuilder RequiresGaugeAtMost(string gauge, int atMost) { _conditions.Add(job => new(ConditionKind.GaugeAtMost, (short)job.GaugeIndex(gauge), atMost)); return this; }
        public SkillBuilder RequiresStatus(string status) { _conditions.Add(job => new(ConditionKind.StatusActive, (short)job.StatusIndex(status), 0)); return this; }
        public SkillBuilder ForbidStatus(string status) { _conditions.Add(job => new(ConditionKind.StatusInactive, (short)job.StatusIndex(status), 0)); return this; }
        public SkillBuilder RequiresStatusLeft(string status, float seconds) { _conditions.Add(job => new(ConditionKind.StatusLeftAtLeast, (short)job.StatusIndex(status), seconds)); return this; }
        public SkillBuilder RequiresStacks(string status, int atLeast) { _conditions.Add(job => new(ConditionKind.StacksAtLeast, (short)job.StatusIndex(status), atLeast)); return this; }
        public SkillBuilder RequiresTargets(int atLeast) { _conditions.Add(_ => new(ConditionKind.TargetsAtLeast, 0, atLeast)); return this; }
        public SkillBuilder RequiresCooldownAtLeast(string cd, float seconds) { _conditions.Add(job => new(ConditionKind.CooldownAtLeast, (short)job.CooldownIndex(cd), seconds)); return this; }

        // the next effect only applies when the condition holds (e.g. a gain unlocked by a trait or a status)
        public SkillBuilder IfStatus(string status) => PendingIf(job => new(ConditionKind.StatusActive, (short)job.StatusIndex(status), 0));
        public SkillBuilder IfStatusInactive(string status) => PendingIf(job => new(ConditionKind.StatusInactive, (short)job.StatusIndex(status), 0));
        public SkillBuilder IfGaugeAtLeast(string gauge, int atLeast) => PendingIf(job => new(ConditionKind.GaugeAtLeast, (short)job.GaugeIndex(gauge), atLeast));
        // a second If* before the effect adds a second condition (both must hold)
        private SkillBuilder PendingIf(Func<JobDefinition, Condition> c)
        {
            if (_pendingEffectCondition == null)
                _pendingEffectCondition = c;
            else
                _pendingEffectCondition2 = c;
            return this;
        }
        public SkillBuilder IfCombo(string from) => PendingIf(job => new(ConditionKind.ComboIs, (short)job.SkillIndex(from), 0));
        public SkillBuilder IfGaugeAtMost(string gauge, int atMost) => PendingIf(job => new(ConditionKind.GaugeAtMost, (short)job.GaugeIndex(gauge), atMost));
        public SkillBuilder ForbidStatuses(params string[] statuses) { foreach (var s in statuses) ForbidStatus(s); return this; }
        public SkillBuilder RemoveStatuses(params string[] statuses) { foreach (var s in statuses) RemoveStatus(s); return this; }

        public SkillBuilder GainGauge(string gauge, int amount) => AddEffect(job => new(EffectKind.GaugeAdd, (short)job.GaugeIndex(gauge), amount, 0, false, Condition.Always));
        public SkillBuilder SpendGauge(string gauge, int amount)
        {
            _conditions.Add(job => new(ConditionKind.GaugeAtLeast, (short)job.GaugeIndex(gauge), amount));
            return AddEffect(job => new(EffectKind.GaugeAdd, (short)job.GaugeIndex(gauge), -amount, 0, false, Condition.Always));
        }
        public SkillBuilder ApplyStatus(string status, float duration, int stacks = 1, bool extend = false)
            => AddEffect(job => new(EffectKind.StatusApply, (short)job.StatusIndex(status), duration, (byte)stacks, extend, Condition.Always));
        public SkillBuilder AddStacks(string status, int stacks) => AddEffect(job => new(EffectKind.StatusAddStacks, (short)job.StatusIndex(status), 0, (byte)stacks, false, Condition.Always));
        public SkillBuilder ConsumeStacks(string status, int stacks = 1)
        {
            _conditions.Add(job => new(ConditionKind.StacksAtLeast, (short)job.StatusIndex(status), stacks));
            return AddEffect(job => new(EffectKind.StatusConsumeStacks, (short)job.StatusIndex(status), 0, (byte)stacks, false, Condition.Always));
        }
        public SkillBuilder RemoveStatus(string status) => AddEffect(job => new(EffectKind.StatusRemove, (short)job.StatusIndex(status), 0, 0, false, Condition.Always));
        public SkillBuilder ReduceCooldown(string cd, float seconds) => AddEffect(job => new(EffectKind.CooldownReduce, (short)job.CooldownIndex(cd), seconds, 0, false, Condition.Always));
        public SkillBuilder SetGauge(string gauge, int value) => AddEffect(job => new(EffectKind.GaugeSet, (short)job.GaugeIndex(gauge), value, 0, false, Condition.Always));
        public SkillBuilder ScaleGauge(string gauge, float factor) => AddEffect(job => new(EffectKind.GaugeScale, (short)job.GaugeIndex(gauge), factor, 0, false, Condition.Always));

        private SkillBuilder AddEffect(Func<JobDefinition, Effect> make)
        {
            var cond = _pendingEffectCondition;
            var cond2 = _pendingEffectCondition2;
            _pendingEffectCondition = _pendingEffectCondition2 = null;
            _effects.Add(cond == null ? make : cond2 == null ? job => make(job) with { If = cond(job) } : job => make(job) with { If = cond(job), If2 = cond2(job) });
            return this;
        }

        internal void Resolve(JobDefinition job)
        {
            if (_cooldown != null)
                Def.Cooldown = job.CooldownIndex(_cooldown);
            if (_dotStatus != null)
                Def.DotStatus = job.StatusIndex(_dotStatus);
            Def.Conditions = [.. _conditions.ConvertAll(c => c(job))];
            Def.Effects = [.. _effects.ConvertAll(e => e(job))];
            Def.PotencyIf = [.. _potencies.ConvertAll(p => p(job))];
        }
    }
}
