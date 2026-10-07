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
}

public readonly record struct Effect(EffectKind Kind, short Index, float Value, byte Stacks, bool Extend, Condition If);

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
    public float CastTime;
    public float AnimationLock = 0.6f;
    public float Recast;          // GCD recast for GCDs (0 = job base GCD); ignored for oGCDs (they use the cooldown group)
    public int Cooldown = -1;     // cooldown group index
    public float PartyValue;      // potency-equivalent value for the party (raid buffs)
    public bool RequiresTarget = true;
    public ComboMode Combo;
    public Condition[] Conditions = [];
    public Effect[] Effects = [];
    public ConditionalPotency[] PotencyIf = [];

    // derived at Build
    public float ComboBonus;      // best extra potency the next combo step gains if the combo is kept
}

public sealed class GaugeDef
{
    public required string Name;
    public int Max;
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
    public GaugeDef[] Gauges = [];
    public StatusDef[] Statuses = [];
    public CooldownDef[] Cooldowns = [];
    public SkillDef[] Skills = [];

    public int GaugeIndex(string name) => Find(Gauges, g => g.Name == name, "gauge", name);
    public int StatusIndex(string name) => Find(Statuses, g => g.Name == name, "status", name);
    public int CooldownIndex(string name) => Find(Cooldowns, g => g.Name == name, "cooldown", name);
    public int SkillIndex(string name) => Find(Skills, g => g.Name == name, "skill", name);

    private static int Find<T>(T[] items, Func<T, bool> pred, string kind, string name)
    {
        for (var i = 0; i < items.Length; ++i)
            if (pred(items[i]))
                return i;
        throw new ArgumentException($"unknown {kind} '{name}'");
    }
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

    public JobBuilder Latency(float seconds) { _latency = seconds; return this; }
    public JobBuilder Gauge(string gauge, int max) { _gauges.Add(new() { Name = gauge, Max = max }); return this; }
    public JobBuilder Cooldown(string cd, float recast, int maxCharges = 1) { _cooldowns.Add(new() { Name = cd, Recast = recast, MaxCharges = maxCharges }); return this; }

    public JobBuilder Status(string status, float maxDuration, float damageMultiplier = 1, int maxStacks = 1, bool upkeep = false, bool mustNotExpire = false,
        float gcdRecastMultiplier = 1, float gcdRecastOverride = 0, float castTimeMultiplier = 1)
    {
        _statuses.Add(new()
        {
            Name = status, MaxDuration = maxDuration, DamageMultiplier = damageMultiplier, MaxStacks = maxStacks, Upkeep = upkeep, MustNotExpire = mustNotExpire,
            GcdRecastMultiplier = gcdRecastMultiplier, GcdRecastOverride = gcdRecastOverride, CastTimeMultiplier = castTimeMultiplier
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
            Name = name, BaseGcd = baseGcd, Latency = _latency,
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
        foreach (var s in job.Skills)
        {
            s.RequiresTarget &= s.Potency > 0 || s.AoePotency > 0 || s.PotencyIf.Length > 0;
            if (s.CastTime > 0 && s.AnimationLock == 0.6f)
                s.AnimationLock = s.CastTime + 0.1f;
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

        // chaining back to the job builder
        public SkillBuilder Gcd(string skill, float potency, uint actionId = 0) => owner.Gcd(skill, potency, actionId);
        public SkillBuilder Ogcd(string skill, float potency, string? cooldown = null, uint actionId = 0) => owner.Ogcd(skill, potency, cooldown, actionId);
        public JobBuilder Job => owner;
        public JobDefinition Build() => owner.Build();

        public SkillBuilder ActionId(uint id) { Def.ActionId = id; return this; }
        public SkillBuilder Aoe(float potencyPerTarget, int minTargets) { Def.AoePotency = potencyPerTarget; Def.MinAoeTargets = minTargets; return this; }
        public SkillBuilder Cast(float seconds) { Def.CastTime = seconds; return this; }
        public SkillBuilder Lock(float seconds) { Def.AnimationLock = seconds; return this; }
        public SkillBuilder Recast(float seconds) { Def.Recast = seconds; return this; }
        public SkillBuilder PartyValue(float potency) { Def.PartyValue = potency; return this; }
        public SkillBuilder NoTarget() { Def.RequiresTarget = false; return this; }
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

        public SkillBuilder RequiresGauge(string gauge, int atLeast) { _conditions.Add(job => new(ConditionKind.GaugeAtLeast, (short)job.GaugeIndex(gauge), atLeast)); return this; }
        public SkillBuilder RequiresGaugeAtMost(string gauge, int atMost) { _conditions.Add(job => new(ConditionKind.GaugeAtMost, (short)job.GaugeIndex(gauge), atMost)); return this; }
        public SkillBuilder RequiresStatus(string status) { _conditions.Add(job => new(ConditionKind.StatusActive, (short)job.StatusIndex(status), 0)); return this; }
        public SkillBuilder ForbidStatus(string status) { _conditions.Add(job => new(ConditionKind.StatusInactive, (short)job.StatusIndex(status), 0)); return this; }
        public SkillBuilder RequiresStacks(string status, int atLeast) { _conditions.Add(job => new(ConditionKind.StacksAtLeast, (short)job.StatusIndex(status), atLeast)); return this; }
        public SkillBuilder RequiresTargets(int atLeast) { _conditions.Add(_ => new(ConditionKind.TargetsAtLeast, 0, atLeast)); return this; }

        // the next effect only applies when the condition holds (e.g. a gain unlocked by a trait or a status)
        public SkillBuilder IfStatus(string status) { _pendingEffectCondition = job => new(ConditionKind.StatusActive, (short)job.StatusIndex(status), 0); return this; }

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

        private SkillBuilder AddEffect(Func<JobDefinition, Effect> make)
        {
            var cond = _pendingEffectCondition;
            _pendingEffectCondition = null;
            _effects.Add(cond == null ? make : job => make(job) with { If = cond(job) });
            return this;
        }

        internal void Resolve(JobDefinition job)
        {
            if (_cooldown != null)
                Def.Cooldown = job.CooldownIndex(_cooldown);
            Def.Conditions = [.. _conditions.ConvertAll(c => c(job))];
            Def.Effects = [.. _effects.ConvertAll(e => e(job))];
            Def.PotencyIf = [.. _potencies.ConvertAll(p => p(job))];
        }
    }
}
