using BossMod.Autorotation.Engine;
using BossMod.Data;

namespace BossMod.Autorotation;

// Base class connecting a RotationEngine to BMR. A concrete job module supplies:
//  - its JobDefinition and weights (usually static, shared by all instances),
//  - ReadJobState: game state -> EngineState (gauges, statuses, job cooldowns; GCD / animation lock / combo / targets are filled here),
//  - optionally ActionFor / TargetFor when the definition's ActionId or the default targeting is not enough.
// No module is registered from this file: the class is abstract.
public abstract class EngineRotationModule(RotationModuleManager manager, Actor player, RotationEngine engine) : RotationModule(manager, player)
{
    protected readonly RotationEngine Engine = engine;
    protected JobDefinition Job => Engine.Job;
    public EngineDecision LastDecision { get; private set; }
    // diagnostics (harnesses): called for every decision that ran a search
    public static Action<string>? DebugTrace;

    // raid-buff window multiplier used when only the party's buff timings are known
    protected virtual float RaidBuffMultiplier => 1.05f;
    protected virtual float RaidBuffInterval => 120;
    protected virtual float RaidBuffDuration => 20;
    protected virtual float RaidBuffFirst => 7.8f;
    protected virtual float TransientLossThreshold => 8.5f; // target losses shorter than this are ignored (RPR / BLM practice)
    // harnesses without party buffs: no assumed 2-minute raid-buff cycle when the party reports none
    public static bool AssumeRaidBuffCycle = true;
    // how long a missing / untargetable target is assumed to stay away when nothing forecasts its return
    protected virtual float UnknownDowntime => 2.5f;

    protected abstract void ReadJobState(ref EngineState s, Actor? primaryTarget);

    protected virtual ActionID ActionFor(SkillDef skill) => new(ActionType.Spell, skill.ActionId);
    protected virtual Actor? TargetFor(SkillDef skill, Actor? primaryTarget) => skill.RequiresTarget ? primaryTarget : Player;
    protected virtual byte CountTargets(Actor? primaryTarget) => 1;

    // priority targets within `radius` of the player measured hitbox to hitbox (the Akechi modules' convention)
    protected byte CountTargetsByHitbox(float radius)
    {
        var count = 0;
        foreach (var target in Hints.PriorityTargetsSpan)
            if (target.Actor is { } actor && !actor.IsDeadOrDestroyed && Player.DistanceToHitbox(actor) <= radius)
                ++count;
        return (byte)Math.Clamp(count, 1, 255);
    }

    // UI settings (the job's strategy tracks). SelectTarget runs before the state is read (target choice, timeline inputs);
    // ApplyStrategy after it: forbidden skills go into s.DisabledSkills, forced ones through Force, the AoE setting into s.Targets.
    protected virtual Actor? SelectTarget(StrategyValues strategy, Actor? primaryTarget) => primaryTarget;
    protected Actor? Target { get; private set; } // the target chosen this frame
    protected virtual void ApplyStrategy(StrategyValues strategy, ref EngineState s, Actor? primaryTarget) { }

    // MechanicHints track: which predicted mechanics feed the timeline (boss-module / imported timeline windows, out-of-range and forced-movement forecast)
    protected bool UseTimelineWindows = true, UseForecast = true;
    protected bool UseFightEnd = true; // FightEnd tracks: plan for the predicted end of the fight
    protected void SetMechanicHints(MechanicHintStrategy hints)
    {
        UseTimelineWindows = hints is MechanicHintStrategy.All or MechanicHintStrategy.TimelineOnly;
        UseForecast = hints is MechanicHintStrategy.All or MechanicHintStrategy.ForecastOnly;
    }

    // skills pushed whenever they are legal, on top of the engine's choice (Force options); earlier calls win when several are legal.
    // Force / Forbid of a skill not learned at the player's level (not in the level-synced definition) do nothing
    private readonly int[] _forced = new int[EngineLimits.MaxSkills];
    private int _numForced;
    protected void Force(string skill)
    {
        var i = Job.TrySkillIndex(skill);
        if (i >= 0 && _numForced < _forced.Length)
            _forced[_numForced++] = i;
    }
    protected void Forbid(ref EngineState s, string skill)
    {
        var i = Job.TrySkillIndex(skill);
        if (i >= 0)
            s.DisabledSkills |= 1UL << i;
    }
    protected void ForbidAll(ref EngineState s) => s.DisabledSkills = ulong.MaxValue;

    // xan Targeting Auto / AutoTryPri (Akechi Automatic / AutoHard / AutoTryPrimary): the player's target, or when it is missing or out of
    // range the first priority target within range
    protected Actor? TargetInRange(Actor? primaryTarget, float range)
    {
        if (Player.DistanceToHitbox(primaryTarget) <= range)
            return primaryTarget;
        foreach (var t in Hints.PriorityTargetsSpan)
            if (Player.DistanceToHitbox(t.Actor) <= range)
                return t.Actor;
        return primaryTarget;
    }

    // xan Targeting track
    protected Actor? SelectTarget(Targeting targeting, Actor? primaryTarget, float range)
        => targeting is Targeting.Auto or Targeting.AutoTryPri ? TargetInRange(primaryTarget, range) : primaryTarget;

    // Akechi Targeting track (AutoHard also switches the player's hard target)
    protected Actor? SelectTarget(akechi.Custom.SoftTargetStrategy targeting, Actor? primaryTarget, float range)
    {
        if (targeting is akechi.Custom.SoftTargetStrategy.Manual or akechi.Custom.SoftTargetStrategy.AutoPrimary)
            return primaryTarget;
        var target = TargetInRange(primaryTarget, range);
        if (targeting == akechi.Custom.SoftTargetStrategy.AutoHard && target != primaryTarget)
            Hints.ForcedTarget = target;
        return target;
    }

    // xan AOE track: ST / ForceST plan on one target (ForceST also forbids every skill that hits several targets), ForceAOE on enough
    // targets for every AoE skill
    protected void ApplyAoe(ref EngineState s, AOEStrategy aoe)
    {
        if (aoe is AOEStrategy.ST or AOEStrategy.ForceST)
            s.Targets = 1;
        if (aoe == AOEStrategy.ForceST)
            ForbidMultiTarget(ref s);
        if (aoe == AOEStrategy.ForceAOE)
            ForceAoeTargets(ref s);
    }

    protected void ForbidMultiTarget(ref EngineState s)
    {
        foreach (var sk in Job.Skills)
            if (sk.AoePotency > 0 || sk.AoeExtraPotency > 0 || sk.DotAoe)
                s.DisabledSkills |= 1UL << sk.Index;
    }

    protected void ForceAoeTargets(ref EngineState s)
    {
        foreach (var sk in Job.Skills)
            if (sk.MinAoeTargets < 99 && s.Targets < sk.MinAoeTargets)
                s.Targets = (byte)sk.MinAoeTargets;
    }

    // the definition's Potion skill (PotionCD / Medicated): usable when the setting allows it and a potion is held, else unavailable
    protected void ReadPotion(ref EngineState s, ActionID potion, bool allowed)
    {
        var cd = Job.CooldownIndex("PotionCD");
        var held = World.Client.GetInventoryItemQuantity(potion.ID) > 0;
        s.Charges[cd] = (byte)(allowed && held && PotionCD <= Simulator.CdEpsilon ? 1 : 0);
        s.CdReadyIn[cd] = allowed && held ? (s.Charges[cd] > 0 ? 0 : PotionCD) : 10000;
        ReadStatus(ref s, Job.StatusIndex("Medicated"), Player, 49);
    }

    // jobs whose definition has no Potion skill (adding one changes the search bounds, and the results, with the potion unavailable):
    // the module presses the potion in the first weave slot when the setting allows it now (`now`: the job's burst window), a potion is
    // held and it is off cooldown
    protected void UsePotion(ActionID potion, bool now)
    {
        if (now && Player.InCombat && PotionCD <= Simulator.CdEpsilon && World.Client.GetInventoryItemQuantity(potion.ID) > 0)
            Hints.ActionsToExecute.Push(potion, Player, ActionQueue.Priority.Medium + 2);
    }

    // damage to the player predicted to land within `seconds`
    protected bool PredictedDamageWithin(float seconds)
    {
        foreach (var damage in Hints.PredictedDamage)
        {
            var damageIn = (float)(damage.Activation - World.CurrentTime).TotalSeconds;
            if (damage.Players[PartyState.PlayerSlot] && damageIn >= 0 && damageIn <= seconds)
                return true;
        }
        return false;
    }

    // an Occult Crescent duty action held and ready by the next GCD
    protected bool PhantomActionReady(PhantomID id) => DutyActionCD(ActionID.MakeSpell(id)) <= GCD + 0.05f;

    // seconds since the pull (0 out of combat)
    protected float CombatTime => Player.InCombat && Manager.CombatStart != default ? (float)(World.CurrentTime - Manager.CombatStart).TotalSeconds : 0;

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        _numForced = 0;
        UseTimelineWindows = UseForecast = UseFightEnd = true;
        primaryTarget = Target = SelectTarget(strategy, primaryTarget);
        var s = EngineState.Create(Job);
        s.GcdReadyAt = GCD;
        s.AnimLockAt = 0; // the queue only runs us when an action could be requested; animation lock is handled by ActionManagerEx
        s.Targets = CountTargets(primaryTarget);
        ReadJobState(ref s, primaryTarget);
        ApplyCastInProgress(ref s);
        ApplyStrategy(strategy, ref s, primaryTarget);

        var tl = BuildTimeline(isMoving, primaryTarget);
        if (_epoch == default)
            _epoch = World.CurrentTime;
        var now = (float)(World.CurrentTime - _epoch).TotalSeconds; // small numbers: float keeps millisecond precision
        EngineDecision d;
        if (Player.Level < 100 && HasSyncedRules)
        {
            d = DecideSynced(s, tl);
        }
        else
        {
            // opener (the pull, or the return after a downtime): every cooldown is up at once and a full search is several times its usual size;
            // from the first GCD after an idle stretch it gets a larger total budget for a few seconds (the usual slice per frame, the rest
            // just before the answer is acted on)
            if (GCD > 0)
            {
                if (now - _lastGcdRunning > IdleSeconds)
                    _openerStart = now;
                _lastGcdRunning = now;
            }
            Engine.BudgetScale = now - _openerStart < OpenerSeconds || now - _lastGcdRunning > IdleSeconds ? OpenerBudgetScale : InBurst(s, tl) ? BurstBudgetScale : 1;
            d = Engine.Decide(s, tl, now);
            // an unfinished search while something could be pressed (the queue would act on its answer this frame): it uses the rest of its
            // budget now. Holding the ability for later frames instead cost more than the occasional longer frame
            if (d.Partial && CanPressNow())
                d = Engine.FinishPending(now);
        }
        LastDecision = d;
        if (DebugTrace != null && !d.Reused)
            DebugTrace(FormattableString.Invariant($"[engine {Job.Name}] t={now:f2} gcd={GCD:f2} skill={(d.Skill >= 0 ? Job.Skills[d.Skill].Name : "wait")} nextGcd={(d.NextGcd >= 0 ? Job.Skills[d.NextGcd].Name : "wait")} at={d.ExecuteAt:f2} depth={d.Depth} nodes={d.Nodes} hyst={d.Hysteresis} partial={d.Partial} combo={(s.ComboSkill != EngineLimits.NoCombo ? Job.Skills[s.ComboSkill].Name : "-")}/{World.Client.ComboState.Action}:{World.Client.ComboState.Remaining:f1} targets={s.Targets} legalGcds={string.Join(",", LegalGcds(s, tl))}"));

        if (d.NextGcd >= 0)
        {
            var gcd = Job.Skills[d.NextGcd];
            Hints.ActionsToExecute.Push(ActionFor(gcd), TargetFor(gcd, primaryTarget), ActionQueue.Priority.High + 2, castTime: gcd.CastTime);
        }
        if (d.Skill >= 0 && !Job.Skills[d.Skill].IsGcd)
        {
            var ogcd = Job.Skills[d.Skill];
            Hints.ActionsToExecute.Push(ActionFor(ogcd), TargetFor(ogcd, primaryTarget), ActionQueue.Priority.Low + 1, delay: d.ExecuteAt);
        }
        // forced skills: ahead of the engine's GCD, abilities in the first weave slot
        for (var i = 0; i < _numForced; ++i)
        {
            var sk = Job.Skills[_forced[i]];
            if (Simulator.IsLegal(Job, s, tl, sk))
                Hints.ActionsToExecute.Push(ActionFor(sk), TargetFor(sk, primaryTarget), (sk.IsGcd ? ActionQueue.Priority.High + 3 : ActionQueue.Priority.Medium + 1) - i * 0.01f, castTime: sk.CastTime);
        }
    }

    // Level sync (below 100): the job's fixed priority rules choose the next GCD and the ability to weave instead of the search, favouring
    // reliability over potency (no gauge or charge overcap, no buff / DoT dropped, no combo broken, burst cooldowns on recast or inside the
    // buff, no long holds). Legality is the level-synced definition's, so the strategy tracks' Forbid applies, and Force still pushes on top.
    // SyncedOgcd sees the state now (it may set SyncedOgcdDelay for a late weave, and SyncedOgcdFirst for an ability the next GCD depends on:
    // with no weave window left it goes first and the GCD waits); SyncedGcd sees the state advanced to the GCD, with the chosen ability
    // applied when it goes before it.
    protected virtual bool HasSyncedRules => false;
    protected virtual int SyncedOgcd(in EngineState s, in EngineTimeline tl) => -1;
    protected virtual int SyncedGcd(in EngineState s, in EngineTimeline tl) => -1;
    protected float SyncedOgcdDelay;
    protected bool SyncedOgcdFirst;
    private readonly EvalContext _syncedCtx = new();

    private EngineDecision DecideSynced(in EngineState s, in EngineTimeline tl)
    {
        SyncedOgcdDelay = 0;
        SyncedOgcdFirst = false;
        var ogcd = SyncedOgcd(s, tl);
        var gcdAt = MathF.Max(s.GcdReadyAt, s.AnimLockAt);
        var weaveAt = MathF.Max(SyncedOgcdDelay, s.AnimLockAt);
        var weave = ogcd >= 0 && gcdAt - weaveAt >= Job.Skills[ogcd].AnimationLock + Job.Latency;
        var first = ogcd >= 0 && !weave && SyncedOgcdFirst;
        var g = s;
        if (weave || first)
        {
            Simulator.Advance(Job, ref g, weaveAt - g.Time, _syncedCtx);
            Simulator.Execute(Job, ref g, tl, Job.Skills[ogcd], _syncedCtx);
        }
        Simulator.Advance(Job, ref g, MathF.Max(gcdAt, g.AnimLockAt) - g.Time, _syncedCtx);
        var gcd = SyncedGcd(g, tl);
        return new() { Skill = ogcd >= 0 ? ogcd : gcd, ExecuteAt = ogcd >= 0 ? SyncedOgcdDelay : gcdAt, NextGcd = first ? -1 : gcd };
    }

    // helpers for the synced rules: the skill's index when it is in the level's definition and legal in s, else -1; the first legal of several
    protected int Legal(in EngineState s, in EngineTimeline tl, string skill)
    {
        var i = Job.TrySkillIndex(skill);
        return i >= 0 && Simulator.IsLegal(Job, s, tl, Job.Skills[i]) ? i : -1;
    }

    protected int FirstLegal(in EngineState s, in EngineTimeline tl, params string[] skills)
    {
        foreach (var sk in skills)
        {
            var i = Legal(s, tl, sk);
            if (i >= 0)
                return i;
        }
        return -1;
    }

    protected int Gauge(in EngineState s, string gauge) => s.Gauges[Job.GaugeIndex(gauge)];
    protected float StatusLeft(in EngineState s, string status) => s.StatusLeft[Job.StatusIndex(status)];
    protected int Stacks(in EngineState s, string status) => s.StatusLeft[Job.StatusIndex(status)] > 0 ? s.StatusStacks[Job.StatusIndex(status)] : 0;
    protected int Charges(in EngineState s, string cooldown) => s.Charges[Job.CooldownIndex(cooldown)];
    // seconds until the next charge (0 with a charge up)
    protected float ReadyIn(in EngineState s, string cooldown) => s.Charges[Job.CooldownIndex(cooldown)] > 0 ? 0 : s.CdReadyIn[Job.CooldownIndex(cooldown)];
    // seconds until the cooldown is at its maximum charges (0 when it is)
    protected float FullIn(in EngineState s, string cooldown)
    {
        var i = Job.CooldownIndex(cooldown);
        var missing = Job.Cooldowns[i].MaxCharges - s.Charges[i];
        return missing <= 0 ? 0 : s.CdReadyIn[i] + (missing - 1) * Job.Cooldowns[i].Recast;
    }
    protected bool ComboIs(in EngineState s, string skill) => s.ComboSkill != EngineLimits.NoCombo && s.ComboLeft > 0 && Job.Skills[s.ComboSkill].Name == skill;
    protected bool Disabled(in EngineState s, string skill)
    {
        var i = Job.TrySkillIndex(skill);
        return i < 0 || (s.DisabledSkills & (1UL << i)) != 0;
    }

    // something can be pressed now: no animation lock or cast (the GCD when it is up, or an ability, late weaves included)
    private bool CanPressNow() => World.Client.AnimationLock <= Imminent && Player.CastInfo == null;
    private const float Imminent = 0.05f;

    // Mid-cast the gauges and statuses do not include the spell being cast yet: plan from the end of the cast with its effects
    // applied (the variant of that action legal in the current state), so the next GCD is not chosen from a stale state.
    private void ApplyCastInProgress(ref EngineState s)
    {
        if (Player.CastInfo is not { } cast || cast.RemainingTime <= 0 || cast.Action.Type != ActionType.Spell)
            return;
        var tl = EngineTimeline.Open();
        foreach (var sk in Job.Skills)
        {
            if (sk.ActionId != cast.Action.ID || !Simulator.IsLegalIgnoringCooldown(Job, s, tl, sk))
                continue;
            var gcd = s.GcdReadyAt;
            Simulator.Execute(Job, ref s, tl, sk, _castCtx);
            s.GcdReadyAt = gcd;
            s.AnimLockAt = cast.RemainingTime + 0.1f + Job.Latency;
            return;
        }
        s.AnimLockAt = cast.RemainingTime + 0.1f + Job.Latency;
    }
    private readonly EvalContext _castCtx = new();

    private IEnumerable<string> LegalGcds(EngineState s, EngineTimeline tl)
    {
        var at = s;
        foreach (var sk in Job.Skills)
            if (sk.IsGcd && Simulator.IsLegal(Job, at, tl, sk))
                yield return sk.Name;
    }

    private DateTime _epoch;
    private float _lastGcdRunning = float.NegativeInfinity, _openerStart = float.NegativeInfinity;
    private const float IdleSeconds = 5, OpenerSeconds = 5, OpenerBudgetScale = 8, BurstBudgetScale = 3;

    private int[]? _buffCooldowns;
    // cooldown groups of the skills that apply one of the job's damage buffs
    private int[] BuffCooldownGroups()
    {
        var groups = new List<int>();
        foreach (var sk in Job.Skills)
            if (sk.Cooldown >= 0 && !groups.Contains(sk.Cooldown))
                foreach (var e in sk.Effects)
                    if (e.Kind == EffectKind.StatusApply && Job.Statuses[e.Index].DamageMultiplier > 1)
                    {
                        groups.Add(sk.Cooldown);
                        break;
                    }
        return [.. groups];
    }

    // a damage buff of the job's own is up or comes off cooldown within a few seconds, or a raid buff is up or starts within a few seconds:
    // the decisions that matter most, with the most cooldowns to order (a larger total budget, spent like the opener's)
    private bool InBurst(in EngineState s, in EngineTimeline tl)
    {
        for (var i = 0; i < Job.Statuses.Length; ++i)
            if (Job.Statuses[i].DamageMultiplier > 1 && s.StatusLeft[i] > 0)
                return true;
        _buffCooldowns ??= BuffCooldownGroups();
        foreach (var c in _buffCooldowns)
            if (s.Charges[c] > 0 || s.CdReadyIn[c] <= 3)
                return true;
        return tl.BuffMultiplier(0) > 1 || tl.BuffMultiplier(3) > 1;
    }

    private readonly List<TargetableWindow> _windowScratch = [];
    private readonly List<DamageBuffWindowSnapshot> _buffScratch = [];
    private int _timelineHash;
    private int _timelineVersion;

    protected EngineTimeline BuildTimeline(bool isMoving, Actor? primaryTarget = null)
    {
        var tl = EngineTimeline.Open();
        // no attackable target right now: treat the next seconds as downtime (nothing that needs a target, e.g. Soulsow instead)
        if (primaryTarget == null || !primaryTarget.IsTargetable)
            tl.AddDowntime(0, UnknownDowntime);

        var fight = Hints.FightRemaining;
        if (UseFightEnd && fight.Known)
            tl.FightEndIn = MathF.Max(fight.RemainingSeconds, GCD + 0.1f);

        // target loss: disengage forecast, then the planner's targetable windows
        var dis = Hints.Disengage;
        if (UseForecast && dis.TargetLossIn < float.MaxValue && dis.TargetReturnIn - dis.TargetLossIn >= TransientLossThreshold)
            tl.AddDowntime(dis.TargetLossIn, dis.TargetReturnIn);
        if (UseTimelineWindows && Manager.Planner is { } plan)
        {
            plan.EstimateTargetableWindows(60, _windowScratch);
            foreach (var w in _windowScratch)
                if (!w.Targetable && w.EndIn - w.StartIn >= TransientLossThreshold)
                    tl.AddDowntime(w.StartIn, w.EndIn);
        }
        if (UseForecast && dis.ForcedMoveIn < float.MaxValue)
            tl.AddNoCast(dis.ForcedMoveIn, dis.ForcedMoveIn + MathF.Max(0.5f, dis.ForcedMoveFor));
        if (isMoving)
            tl.AddNoCast(0, 0.5f);

        // raid buffs: active / upcoming party buffs, else the 2-minute cycle from combat start (RaidBuffTimings reads the same windows)
        Bossmods.RaidCooldowns.DamageBuffWindows(Player, null, _buffScratch);
        var windows = _buffScratch;
        foreach (var w in windows)
            tl.AddBuff(w.StartsIn, w.StartsIn + w.Duration, 1 + (RaidBuffMultiplier - 1) * w.Weight);
        if (windows.Count == 0 && AssumeRaidBuffCycle && Manager.CombatStart != default)
        {
            var elapsed = (float)(World.CurrentTime - Manager.CombatStart).TotalSeconds;
            for (var t = RaidBuffFirst; t < elapsed + 360 && tl.NumBuffs < EngineLimits.MaxWindows; t += RaidBuffInterval)
                if (t + RaidBuffDuration > elapsed)
                    tl.AddBuff(t - elapsed, t + RaidBuffDuration - elapsed, RaidBuffMultiplier);
        }

        // bump the version only when the window set changes (windows sliding with time do not count)
        var h = tl.NumDowntime * 7 + tl.NumBuffs * 131 + tl.NumNoCast * 1031 + (float.IsInfinity(tl.FightEndIn) ? 1 : 0);
        for (var i = 0; i < tl.NumBuffs; ++i)
            h = h * 31 + (int)MathF.Round(tl.Buffs[i].Multiplier * 100);
        if (h != _timelineHash)
        {
            _timelineHash = h;
            ++_timelineVersion;
        }
        tl.Version = _timelineVersion;
        return tl;
    }

    // the raid buffs the timeline sees, as (seconds left on the current one, seconds until the next one): the party's windows, else the
    // assumed cycle from combat start (EstimateRaidBuffTimings reads the party's cooldowns only, which the harness has only with party buffs)
    protected (float Left, float In) RaidBuffTimings()
    {
        Bossmods.RaidCooldowns.DamageBuffWindows(Player, null, _buffScratch);
        if (_buffScratch.Count > 0)
        {
            float left = 0, next = float.MaxValue;
            foreach (var w in _buffScratch)
            {
                if (w.StartsIn <= 0)
                    left = MathF.Max(left, w.StartsIn + w.Duration);
                else
                    next = MathF.Min(next, w.StartsIn);
            }
            return (left, next);
        }
        if (!AssumeRaidBuffCycle || Manager.CombatStart == default)
            return (0, 0);
        var cycle = (float)(World.CurrentTime - Manager.CombatStart).TotalSeconds - RaidBuffFirst;
        if (cycle < 0)
            return (0, -cycle);
        cycle %= RaidBuffInterval;
        return cycle < RaidBuffDuration ? (RaidBuffDuration - cycle, 0) : (0, RaidBuffInterval - cycle);
    }

    // helpers for ReadJobState
    protected void ReadCooldown(ref EngineState s, int cdIndex, ActionID action)
    {
        var def = ActionDefinitions.Instance[action];
        if (def == null)
            return;
        var cd = Job.Cooldowns[cdIndex];
        var readyIn = def.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions);
        var full = def.MainCooldownGroup >= 0 ? World.Client.Cooldowns[def.MainCooldownGroup] : default;
        var charges = cd.MaxCharges;
        if (cd.MaxCharges > 1 && full.Total > 0)
        {
            var missing = (full.Total - full.Elapsed) / cd.Recast;
            charges = Math.Clamp(cd.MaxCharges - (int)MathF.Ceiling(missing - 1e-3f), 0, cd.MaxCharges);
            s.CdReadyIn[cdIndex] = charges < cd.MaxCharges ? (full.Total - full.Elapsed) - (cd.MaxCharges - charges - 1) * cd.Recast : 0;
        }
        else
        {
            // an idle group: every charge is up (a charged cooldown at its maximum stops recharging and its group reads empty)
            charges = readyIn > Simulator.CdEpsilon ? 0 : cd.MaxCharges;
            s.CdReadyIn[cdIndex] = charges > 0 ? 0 : readyIn;
        }
        s.Charges[cdIndex] = (byte)charges;
    }

    protected void ReadStatus(ref EngineState s, int statusIndex, Actor? actor, uint sid, bool fromPlayer = true)
    {
        if (actor == null)
            return;
        var st = fromPlayer ? StatusDetails(actor, sid, Player.InstanceID) : StatusDetails(actor, sid, actor.InstanceID);
        if (st.Left > 0)
        {
            s.StatusLeft[statusIndex] = st.Left;
            s.StatusStacks[statusIndex] = (byte)Math.Max(1, st.Stacks);
        }
    }

    protected void ReadCombo(ref EngineState s)
    {
        var combo = World.Client.ComboState;
        if (combo.Remaining <= 0)
            return;
        for (var i = 0; i < Job.Skills.Length; ++i)
        {
            if (Job.Skills[i].ActionId == combo.Action)
            {
                s.ComboSkill = (byte)i;
                s.ComboLeft = combo.Remaining;
                return;
            }
        }
    }
}
