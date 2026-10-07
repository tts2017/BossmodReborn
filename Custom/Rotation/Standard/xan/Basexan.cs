using BossMod.Data;
using System.Diagnostics.CodeAnalysis;
using static BossMod.AIHints;

namespace BossMod.Autorotation.xan.Custom;


public abstract class Attackxan<AID, TraitID, TValues>(RotationModuleManager manager, Actor player, PotionType potType = PotionType.None) : Basexan<AID, TraitID, TValues>(manager, player, potType)
    where AID : struct, Enum
    where TraitID : Enum
    where TValues : struct
{
    protected sealed override float GCDLength => AttackGCDLength;
}

public abstract class Castxan<AID, TraitID, TValues>(RotationModuleManager manager, Actor player, PotionType potType = PotionType.None) : Basexan<AID, TraitID, TValues>(manager, player, potType)
    where AID : struct, Enum
    where TraitID : Enum
    where TValues : struct
{
    protected sealed override float GCDLength => SpellGCDLength;
}

public abstract class Basexan<AID, TraitID, TValues>(RotationModuleManager manager, Actor player, PotionType potType) : TypedRotationModule<TValues>(manager, player)
    where AID : struct, Enum
    where TraitID : Enum
    where TValues : struct
{
    // --- formerly added to upstream RotationModule by the old fork ---
    protected Actor? ResolveTargetOverride(in StrategyValueTrack strategy) => ResolveTarget(strategy);
    protected AIHints.Enemy? ResolveTargetOverride<T>(in Track<T> track) where T : struct => ResolveEnemy(track);

    // Same search as FindBetterTargetBy with a struct scorer, so callers that run every frame need not allocate closures and delegates.
    protected (Actor? Target, P Priority) FindBetterTargetByScorer<P, TScorer>(Actor? initial, float maxDistanceFromPlayer, in TScorer scorer) where P : struct, IComparable where TScorer : struct, ITargetScorer<P>
    {
        bool inRange(Actor tar) => tar.Position.InCircle(Player.Position, maxDistanceFromPlayer + tar.HitboxRadius + 0.5f);

        if (initial != null && !inRange(initial))
        {
            initial = null;
        }

        var bestTarget = initial;
        var bestPrio = initial != null ? scorer.Score(initial) : default;
        var priorityTargets = Hints.PriorityTargetsSpan;
        var len = priorityTargets.Length;
        for (var i = 0; i < len; ++i)
        {
            var enemy = priorityTargets[i];
            if (enemy.Actor == initial || !inRange(enemy.Actor) || !scorer.Accept(enemy))
                continue;

            var newPrio = scorer.Score(enemy.Actor);
            if (Comparer<P>.Default.Compare(newPrio, bestPrio) > 0)
            {
                bestPrio = newPrio;
                bestTarget = enemy.Actor;
            }
        }
        return (bestTarget, bestPrio);
    }

    public PotionType PotionType { get; init; } = potType;

    protected float PelotonLeft { get; private set; }
    protected float SwiftcastLeft { get; private set; }
    protected float TrueNorthLeft { get; private set; }
    protected float CombatTimer { get; private set; }
    protected float AnimationLockDelay { get; private set; }
    protected float RaidBuffsIn { get; private set; }
    protected float RaidBuffsLeft { get; private set; }
    protected float DowntimeIn { get; private set; }
    protected float? UptimeIn { get; private set; }

    // predicted mechanics (see MechanicForecast); modules refresh it at the top of Exec with their MechanicHints track
    protected MechanicForecast Mechanic { get; private set; } = MechanicForecast.None;
    protected void UpdateMechanicForecast(MechanicHintStrategy mode) => Mechanic = MechanicForecast.Build(mode, World, Hints, Bossmods.ActiveModule);

    // seconds until the charge spent by pressing the action now is back
    protected float RecastRecoveredIn(AID aid)
    {
        var def = ActionDefinitions.Instance.Spell(aid);
        return def == null ? float.MaxValue : def.ChargeCapIn(World.Client.Cooldowns, World.Client.DutyActions, Player.Level) + def.Cooldown;
    }

    protected Enemy? PlayerTarget { get; private set; }
    protected bool IsMoving { get; private set; }
    protected float PotionLeft { get; private set; }

    protected float? CountdownRemaining => World.Client.CountdownRemaining;
    protected float AnimLock => World.Client.AnimationLock;

    protected float AttackGCDLength => ActionSpeed.GCDRounded(World.Client.PlayerStats.SkillSpeed, World.Client.PlayerStats.Haste, Player.Level);
    protected float SpellGCDLength => ActionSpeed.GCDRounded(World.Client.PlayerStats.SpellSpeed, World.Client.PlayerStats.Haste, Player.Level);

    protected float ReadyIn(AID action) => Unlocked(action) ? ActionDefinitions.Instance.Spell(action)!.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions) : float.MaxValue;
    protected float MaxChargesIn(AID action) => Unlocked(action) ? ActionDefinitions.Instance.Spell(action)!.ChargeCapIn(World.Client.Cooldowns, World.Client.DutyActions, Player.Level) : float.MaxValue;

    protected float DutyActionReadyIn<ID>(ID aid) where ID : Enum => DutyActionReadyIn(ActionID.MakeSpell(aid));

    protected float DutyActionReadyIn(ActionID aid) => DutyActionCD(aid);

    protected abstract float GCDLength { get; }

    public bool CanFitGCD(float duration, int extraGCDs = 0) => GCD + GCDLength * extraGCDs < duration;

    protected bool HaveRaidBuffs => HaveRaidBuffsUntil(GCD);
    protected bool HaveRaidBuffsUntil(float deadline) => RaidBuffsLeft > deadline || RaidBuffsIn > 9000;

    // frame alignment/cooldown reduction produces inconsistent results in combat, i.e. on MCH, 2.5 GCD drill will quasi-randomly not be considered ready
    // according to the balance discord, there's a random chance that actions you use will have an animation lock longer or shorter than intended by 1 tick (+/- 40ms) and this problem is apparently more common for players that use lag reduction tools
    // i'm just going to assume this bug can also happen to cooldowns, it's not easy to test and there's unlikely to be much data from vanilla players about it, since they all use native action queueing
    protected bool GCDReady(AID aid) => ReadyIn(aid) < GCD + 0.05f;
    protected bool DutyActionGCDReady<ID>(ID aid) where ID : Enum => DutyActionReadyIn(aid) < GCD + 0.05f;

    protected bool OnCooldown(AID aid) => MaxChargesIn(aid) > 0;

    public bool CanWeave(float cooldown, float actionLock, int extraGCDs = 0, float extraFixedDelay = 0)
        => Math.Max(cooldown, AnimLock) + actionLock + AnimationLockDelay <= GCD + GCDLength * extraGCDs + extraFixedDelay;

    public bool CanWeave(AID aid, int extraGCDs = 0, float extraFixedDelay = 0)
    {
        // TODO is this actually helpful?
        if (!Unlocked(aid))
            return false;

        var def = ActionDefinitions.Instance[ActionID.MakeSpell(aid)]!;

        // amnesia check
        if (def.Category == ActionCategory.Ability && Player.FindStatus(1092) != null)
            return false;

        return CanWeave(ReadyIn(aid), def.InstantAnimLock, extraGCDs, extraFixedDelay);
    }

    protected AID NextGCD;
    protected int NextGCDPrio;
    protected uint MP;

    public const float DefaultOGCDPriority = ActionQueue.Priority.Low + 1;
    public const float DefaultGCDPriority = ActionQueue.Priority.High + 2;

    protected AID ComboLastMove => (AID)(object)World.Client.ComboState.Action;

    protected float GetApplicationDelay(AID action) => ApplicationDelay.Get((uint)(object)action);

    // which action categories the player's statuses make the client refuse this frame (Pacification / Silence / Amnesia / stun-type);
    // refreshed once per Execute, see ActionLocks
    protected ActionLockState Locks { get; private set; }

    // the client would refuse this action right now because of a status on the player; a module that wants its queue limited to what
    // the client accepts overrides CanUse with !IsActionLocked(action)
    protected bool IsActionLocked(AID aid) => Locks.IsLocked(ActionDefinitions.Instance.Spell(aid));

    // an ability pressed while a Pacification is about to end keeps the animation lock past the moment the next weaponskill could start
    // (the end of the lock or of the GCD recast, whichever is later) and so delays it; weaving it after that weaponskill costs nothing.
    // Only a Pacification can leave an ability free to press with the weaponskill pending: every other lock refuses the ability too.
    // Harness (irregular lockout, DRG): without this the resume excess after a Pacification grows 0.025 -> 0.033 s and peaks at 1.5 s.
    protected bool DelaysWeaponskillResume(AID aid)
    {
        if (Locks.WeaponskillLeft <= 0 || Locks.AllLocked)
            return false;

        var def = ActionDefinitions.Instance.Spell(aid);
        return def is { Category: ActionCategory.Ability } && Math.Max(0, AnimLock) + def.InstantAnimLock + AnimationLockDelay > Math.Max(Locks.WeaponskillLeft, GCD);
    }

    // override if some action requires specific runtime checks that aren't covered by the existing framework code
    protected virtual bool CanUse(AID action) => true;

    protected void PushGCD<P>(AID aid, Actor? target, P priority, float delay = 0, bool setRotation = false) where P : Enum
        => PushGCD(aid, target, (int)(object)priority, delay, setRotation);

    protected void PushGCD<P>(AID aid, Enemy? target, P priority, float delay = 0, bool useOnDyingTarget = true, bool setRotation = false) where P : Enum
    {
        if (target?.Priority is Enemy.PriorityInvincible or Enemy.PriorityForbidden)
            return;

        if (!useOnDyingTarget && target?.Priority is Enemy.PriorityPointless)
            return;

        PushGCD(aid, target?.Actor, (int)(object)priority, delay, setRotation);
    }

    protected void PushGCD(AID aid, Enemy? target, int priority = 2, float delay = 0, bool setRotation = false) => PushGCD(aid, target?.Actor, priority, delay, setRotation);

    protected void PushGCD(AID aid, Actor? target, int priority = 2, float delay = 0, bool setRotation = false)
    {
        if (priority == 0)
            return;

        Angle? facing = setRotation && target is { } tar ? Player.AngleTo(tar) : null;

        if (PushAction(aid, target, ActionQueue.Priority.High + priority, delay, facingAngle: facing) && priority > NextGCDPrio)
        {
            NextGCD = aid;
            NextGCDPrio = priority;
        }
    }

    protected void PushOGCD<P>(AID aid, Actor? target, P priority, float delay = 0, bool setRotation = false) where P : Enum
        => PushOGCD(aid, target, (int)(object)priority, delay, setRotation);

    protected void PushOGCD<P>(AID aid, Enemy? target, P priority, float delay = 0, bool useOnDyingTarget = true, bool setRotation = false) where P : Enum
    {
        if (target?.Priority is Enemy.PriorityInvincible or Enemy.PriorityForbidden)
            return;

        if (!useOnDyingTarget && target?.Priority is Enemy.PriorityPointless)
            return;

        PushOGCD(aid, target?.Actor, (int)(object)priority, delay, setRotation);
    }

    protected void PushOGCD(AID aid, Enemy? target, int priority = 1, float delay = 0, bool setRotation = false) => PushOGCD(aid, target?.Actor, priority, delay, setRotation);

    protected void PushOGCD(AID aid, Actor? target, int priority = 1, float delay = 0, bool setRotation = false)
    {
        if (priority == 0)
            return;

        Angle? facing = setRotation && target is { } tar ? Player.AngleTo(tar) : null;

        PushAction(aid, target, ActionQueue.Priority.Low + priority, delay, facingAngle: facing);
    }

    protected bool UsePlanned<T>(in Track<T> strategyTrack, AID action, Enemy? defaultTarget, float delay = 0, float additionalPriority = 0, bool forced = false, Func<Enemy?, bool>? predicate = null, bool setRotation = false) where T : struct
    {
        var realTarget = ResolveEnemy(strategyTrack) ?? defaultTarget;

        if (predicate?.Invoke(realTarget) == false)
            return false;

        Angle? facing = setRotation && realTarget is { Actor: var a } ? Player.AngleTo(a) : null;

        return PushAction(action, realTarget?.Actor, strategyTrack.Priority() + additionalPriority, delay, forced, facingAngle: facing);
    }

    protected bool UsePlanned<T>(in Track<T> strategyTrack, AID action, Actor? defaultTarget, float delay = 0, float additionalPriority = 0, bool forced = false, Func<Actor?, bool>? predicate = null, bool setRotation = false) where T : struct
    {
        var realTarget = ResolveTarget(strategyTrack) ?? defaultTarget;

        if (predicate?.Invoke(realTarget) == false)
            return false;

        Angle? facing = setRotation && realTarget is { } a ? Player.AngleTo(a) : null;

        return PushAction(action, realTarget, strategyTrack.Priority() + additionalPriority, delay, forced, facingAngle: facing);
    }

    protected bool PushAction(AID aid, Actor? target, float priority, float delay, bool forced = false, Angle? facingAngle = null)
    {
        if ((uint)(object)aid == 0)
            return false;

        if (!CanUse(aid))
            return false;

        var def = ActionDefinitions.Instance.Spell(aid);
        if (def == null || !def.IsUnlocked(World, Player))
            return false;

        if (def.Range != 0 && target == null)
        {
            // Service.Log($"Queued targeted action ({aid}) with no target");
            return false;
        }

        Vector3 targetPos = default;

        if ((def.AllowedTargets & ActionTargets.Area) != 0)
        {
            if (def.Range == 0)
                targetPos = Player.PosRot.XYZ();
            else if (target != null)
                targetPos = target.PosRot.XYZ();
        }

        Hints.ActionsToExecute.Push(ActionID.MakeSpell(aid), target, priority, delay: delay, targetPos: targetPos, castTime: GetSlidecastTime(aid), facingAngle: facingAngle, forced: forced);
        return true;
    }

    /// <summary>
    /// <para>Tries to select a suitable primary target.</para>
    /// <para>If the provided <paramref name="primaryTarget"/> is null, an NPC, or non-enemy object; it will be reset to <c>null</c>.</para>
    /// <para>Additionally, if <paramref name="range"/> is set to <c>Targeting.Auto</c>, and the user's current target is more than <paramref name="range"/> yalms from the player, this function attempts to find a closer one. No prioritization is done; if any target is returned, it is simply the actor that was earliest in the object table. If no closer target is found, <paramref name="primaryTarget"/> will remain unchanged.</para>
    /// </summary>
    /// <param name="strategy">Targeting strategy</param>
    /// <param name="primaryTarget">Player's current target - may be null</param>
    /// <param name="range">Maximum distance from the player to search for a candidate target</param>
    protected void SelectPrimaryTarget<S>(in S strategy, ref Enemy? primaryTarget, float range) where S : IStrategyCommon
    {
        if (strategy.Targeting is Targeting.Auto or Targeting.AutoTryPri)
        {
            if (Player.DistanceToHitbox(primaryTarget) > range)
            {
                var targets = Hints.PriorityTargetsSpan;
                var len = targets.Length;
                for (var i = 0; i < len; ++i)
                {
                    var candidate = targets[i];
                    if (Player.DistanceToHitbox(candidate.Actor) <= range)
                    {
                        primaryTarget = candidate;
                        break;
                    }
                }
            }
        }
    }

    protected delegate bool PositionCheck(Actor playerTarget, Actor targetToTest);
    protected delegate P PriorityFunc<P>(int totalTargets, Actor primaryTarget);

    // The strategy is a generic parameter rather than IStrategyCommon: passing a job's strategy struct as the interface boxed the whole struct on every call, several times per frame.
    protected (Enemy? Best, int Targets) SelectTarget<S>(
        in S strategy,
        Enemy? primaryTarget,
        float range,
        PositionCheck isInAOE,
        Dictionary<Actor, int>? targetCounts = null
    ) where S : IStrategyCommon => SelectTarget(strategy, primaryTarget, range, isInAOE, (numTargets, _) => numTargets, a => a, targetCounts);

    protected (Enemy? Best, int Targets) SelectTargetByHP<S>(in S strategy, Enemy? primaryTarget, float range, PositionCheck isInAOE) where S : IStrategyCommon
        => SelectTarget(strategy, primaryTarget, range, isInAOE, (numTargets, actor) => (numTargets, numTargets > 2 ? actor.HPMP.CurHP : 0), args => args.numTargets);

    protected (Enemy? Best, int Priority) SelectTarget<S, P>(
        in S strategy,
        Enemy? primaryTarget,
        float range,
        PositionCheck isInAOE,
        PriorityFunc<P> prioritize,
        Func<P, int> simplify,
        Dictionary<Actor, int>? targetCounts = null
    ) where S : IStrategyCommon where P : struct, IComparable
    {
        var targeting = strategy.Targeting;
        var aoe = strategy.AOE;

        // in regular ST mode and when using a skill that deals splash damage (like Primal Rend), it is possible that primary target has prio 0 if the splash damage would hit a forbidden target
        // however, in force-ST mode, prio is *always* 0, so we cannot find a better target - in this case, skip entirely
        if (aoe == AOEStrategy.ForceST)
            targeting = Targeting.Manual;

        if (targeting == Targeting.AutoTryPri)
            targeting = Player.DistanceToHitbox(primaryTarget) <= range ? Targeting.AutoPrimary : Targeting.Auto;

        var (newtarget, newprio) = targeting switch
        {
            Targeting.Auto => FindBetterTargetByScorer<P, AOETargetScorer<P>>(primaryTarget?.Actor, range, new(this, primaryTarget, aoe, isInAOE, prioritize, targetCounts, null)),
            Targeting.AutoPrimary => primaryTarget == null ? (null, default) : FindBetterTargetByScorer<P, AOETargetScorer<P>>(
                primaryTarget.Actor,
                range,
                new(this, primaryTarget, aoe, isInAOE, prioritize, targetCounts, primaryTarget.Actor)
            ),
            _ => (primaryTarget?.Actor, primaryTarget == null ? default : new AOETargetScorer<P>(this, primaryTarget, aoe, isInAOE, prioritize, targetCounts, null).Score(primaryTarget.Actor))
        };
        var newnewprio = simplify(newprio);
        return (newnewprio > 0 ? Hints.FindEnemy(newtarget) : null, newnewprio);
    }

    // Scores a SelectTarget candidate by how many enemies its AoE would hit. A struct, so the per-frame search allocates no closure or delegate.
    private readonly struct AOETargetScorer<P> : ITargetScorer<P> where P : struct, IComparable
    {
        private readonly Basexan<AID, TraitID, TValues> _module;
        private readonly Enemy? _primaryTarget;
        private readonly AOEStrategy _aoe;
        private readonly PositionCheck _isInAOE;
        private readonly PriorityFunc<P> _prioritize;
        private readonly Dictionary<Actor, int>? _targetCounts;
        private readonly Actor? _mustHit; // AutoPrimary: only candidates whose AoE also hits the player's target

        public AOETargetScorer(Basexan<AID, TraitID, TValues> module, Enemy? primaryTarget, AOEStrategy aoe, PositionCheck isInAOE, PriorityFunc<P> prioritize, Dictionary<Actor, int>? targetCounts, Actor? mustHit)
        {
            _module = module;
            _primaryTarget = primaryTarget;
            _aoe = aoe;
            _isInAOE = isInAOE;
            _prioritize = prioritize;
            _targetCounts = targetCounts;
            _mustHit = mustHit;
        }

        public bool Accept(Enemy enemy) => _mustHit == null || _isInAOE(enemy.Actor, _mustHit);

        public P Score(Actor potentialTarget)
        {
            // Shared only within one execution with the same primary target, shape, and AoE strategy.
            if (_targetCounts != null && _targetCounts.TryGetValue(potentialTarget, out var cachedCount))
                return _prioritize(cachedCount, potentialTarget);

            var numForbidden = 0;
            var forbiddentargets = _module.Hints.ForbiddenTargetsSpan;
            var count = forbiddentargets.Length;
            for (var i = 0; i < count; ++i)
            {
                var enemy = forbiddentargets[i];
                if (_isInAOE(potentialTarget, enemy.Actor))
                {
                    ++numForbidden;
                }
            }

            var numOk = 0;
            var targets = _module.Hints.PriorityTargetsSpan;
            var len = targets.Length;
            for (var i = 0; i < len; ++i)
            {
                var enemy = targets[i];
                if (_isInAOE(potentialTarget, enemy.Actor))
                {
                    ++numOk;
                }
            }

            var isOutOfCombat = potentialTarget == _primaryTarget?.Actor && _primaryTarget?.Priority == Enemy.PriorityUndesirable;

            int numTargets;

            // manually selected target is out of combat and nobody else will be hit
            if (isOutOfCombat && numForbidden == 1 && numOk == 0)
                numTargets = 1;

            // forbidden target will be hit
            else if (numForbidden > 0)
                numTargets = 0;

            // for player-sourced targeted AOEs, the action is hardcoded to hit the main target
            // this means that even if the primary target technically isn't in the shape (e.g. MCH chainsaw, at maximum range, is 0.5 units too short to hit the targeted mob) it will still be counted
            // for all other targets, the server checks based on our rotation and position at snapshot time (yes, seriously)
            else
                numTargets = Math.Max(1, numOk);

            var adjustedTargets = _module.AdjustNumTargets(_aoe, numTargets);
            if (_targetCounts != null)
                _targetCounts[potentialTarget] = adjustedTargets;
            return _prioritize(adjustedTargets, potentialTarget);
        }
    }

    /// <summary>
    /// <para>Find a good target to apply a DoT effect to. Has no effect if auto-targeting is disabled.</para>
    /// <para>If <c>Hints.PriorityTargets</c> contains more than <c>maxAllowedTargets</c>, <c>null</c> will be returned. Enemies with <c>ForbidDOTs = true</c> are not counted in this case.</para>
    /// </summary>
    /// <typeparam name="P"></typeparam>
    /// <param name="strategy"></param>
    /// <param name="initial"></param>
    /// <param name="getTimer"></param>
    /// <param name="maxAllowedTargets"></param>
    /// <returns></returns>
    protected (Enemy? Target, P Timer) SelectDotTarget<S, P>(in S strategy, Enemy? initial, Func<Actor?, P> getTimer, int maxAllowedTargets) where S : IStrategyCommon where P : struct, IComparable
    {
        var forbidden = initial?.ForbidDOTs ?? false;
        switch (strategy.Targeting)
        {
            case Targeting.Manual:
            case Targeting.AutoPrimary:
                return forbidden ? (null, getTimer(null)) : (initial, getTimer(initial?.Actor));
            case Targeting.AutoTryPri:
                if (initial != null)
                    return forbidden ? (null, getTimer(null)) : (initial, getTimer(initial?.Actor));
                break;
        }

        var newTarget = initial;
        var initialTimer = forbidden ? getTimer(null) : getTimer(initial?.Actor);
        var newTimer = initialTimer;

        var numTargets = 0;

        foreach (var dotTarget in Hints.PriorityTargetsSpan)
        {
            if (dotTarget.ForbidDOTs)
                continue;

            if (++numTargets > maxAllowedTargets)
                return (null, getTimer(null));

            var thisTimer = getTimer(dotTarget.Actor);
            if (thisTimer.CompareTo(newTimer) < 0)
            {
                newTarget = dotTarget;
                newTimer = thisTimer;
            }
        }

        return (newTarget, newTimer);
    }

    // used for casters that don't have a separate maximize-AOE function
    protected void GoalZoneSingle(float range)
    {
        if (PlayerTarget != null)
            Hints.GoalZones.Add(Hints.GoalSingleTarget(PlayerTarget.Actor, Player, World.Actors, range));
    }

    protected void GoalZoneCombined<S>(in S strategy, float range, Func<WPos, float> fAoe, AID firstUnlockedAoeAction, int minAoe, float? maximumActionRange = null) where S : IStrategyCommon
    {
        var a = strategy.AOE;
        var (_, positional, imminent, _) = Hints.RecommendedPositional;

        if (!strategy.AOE.AOEOk() || !Unlocked(firstUnlockedAoeAction))
            minAoe = 50;

        if (PlayerTarget == null)
        {
            if (minAoe < 50)
                Hints.GoalZones.Add(fAoe);
        }
        else
        {
            Hints.GoalZones.Add(GoalCombined(Hints.GoalSingleTarget(PlayerTarget.Actor, imminent ? positional : Positional.Any, Player, World.Actors, range), fAoe, minAoe));
            if (maximumActionRange is float r)
                Hints.GoalZones.Add(Hints.GoalSingleTarget(PlayerTarget.Actor, Player, World.Actors, r, 0.5f));
        }
    }

    protected int NumMeleeAOETargets<S>(in S strategy) where S : IStrategyCommon => NumNearbyTargets(strategy, 5);

    protected int NumNearbyTargets<S>(in S strategy, float range) where S : IStrategyCommon => AdjustNumTargets(strategy.AOE, Hints.NumPriorityTargetsInAOECircle(Player.Position, range));

    protected int AdjustNumTargets(AOEStrategy aoe, int reported)
        => reported == 0 ? 0 : aoe switch
        {
            AOEStrategy.AOE => reported,
            AOEStrategy.ST => 1,
            AOEStrategy.ForceAOE => 10,
            AOEStrategy.ForceST => 0,
            _ => 0
        };

    protected PositionCheck IsSplashTarget => (primary, other) => TargetInAOECircle(other, primary.Position, 5);
    protected PositionCheck Is25yRectTarget => (primary, other) => TargetInAOERect(other, Player.Position, Player.DirectionTo(primary), 25, 2);

    /// <summary>
    /// Get <em>effective</em> cast time for the provided action.<br/>
    /// The default implementation returns the action's base cast time multiplied by the player's spellspeed factor, which accounts for haste buffs (like Leylines) and slow debuffs. It also accounts for Swiftcast.<br/>
    /// Subclasses should handle job-specific cast speed adjustments, such as RDM's Dualcast or PCT's motifs.
    /// </summary>
    /// <param name="aid"></param>
    /// <returns></returns>
    protected virtual float GetCastTime(AID aid)
    {
        var def = ActionDefinitions.Instance.Spell(aid);
        if (def == null)
            return 0;

        var hasteMod = GCDLength / 2.5f;

        if (SwiftcastLeft > GCD && def.Category is ActionCategory.Spell)
            return 0;

        return def.CastTime * hasteMod;
    }

    protected float NextCastStart => AnimLock > GCD ? AnimLock + AnimationLockDelay : GCD;

    protected float GetSlidecastTime(AID aid) => Math.Max(0, GetCastTime(aid) - 0.5f);
    protected float GetSlidecastEnd(AID aid) => NextCastStart + GetSlidecastTime(aid);

    protected bool Unlocked(AID aid) => ActionUnlocked(ActionID.MakeSpell(aid));
    protected bool Unlocked(TraitID tid) => TraitUnlocked((uint)(object)tid);

    protected Positional GetCurrentPositional(Actor target) => target.Omnidirectional
        ? Positional.Any
        : (Player.Position - target.Position).Normalized().Dot(target.Rotation.ToDirection()) switch
        {
            < -0.7071068f => Positional.Rear,
            < 0.7071068f => Positional.Flank,
            _ => Positional.Front
        };

    protected bool NextPositionalImminent;
    protected bool NextPositionalCorrect;

    protected void UpdatePositionals(Enemy? enemy, ref (Positional pos, bool imm) positional)
    {
        var trueNorth = TrueNorthLeft > GCD;
        var target = enemy?.Actor;
        if (
            // positionals irrelevant
            target is { Omnidirectional: true }
            // enemy is targeting us and is not busy casting, so we assume they will turn to face the player
            // (excluding striking dummies, which don't move)
            || target is { TargetID: var t, CastInfo: null, IsStrikingDummy: false } && t == Player.InstanceID
            || enemy?.Priority < 0
        )
            positional = (Positional.Any, false);

        NextPositionalImminent = !trueNorth && positional.imm;
        NextPositionalCorrect = trueNorth || target == null || positional.pos switch
        {
            Positional.Flank => Math.Abs(target.Rotation.ToDirection().Dot((Player.Position - target.Position).Normalized())) < 0.7071067f,
            Positional.Rear => target.Rotation.ToDirection().Dot((Player.Position - target.Position).Normalized()) < -0.7071068f,
            // the only Front positional is Goblin Punch, used by BLU, who can't use True North anyway, so it's irrelevant
            _ => true
        };
        Hints.RecommendedPositional = (target, positional.pos, NextPositionalImminent, NextPositionalCorrect);
    }

    private float? _prevCountdown;
    private DateTime _cdLockout;

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "determinism is intentional here")]
    private void PretendCountdown()
    {
        if (CountdownRemaining == null || Player.InCombat)
        {
            _cdLockout = DateTime.MinValue;
            _prevCountdown = null;
        }
        else if (_prevCountdown == null)
        {
            var wait = (float)new Random((int)World.Frame.Index).NextDouble() + 0.5f;
            _cdLockout = World.FutureTime(wait);
            _prevCountdown = CountdownRemaining;
        }
    }

    public sealed override void Execute(in TValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        IsMoving = isMoving;
        NextGCD = default;
        NextGCDPrio = 0;
        PlayerTarget = Hints.FindEnemy(primaryTarget);

        PretendCountdown();

        Locks = ActionLocks.Read(Player, World.CurrentTime);
        var pelo = Player.FindStatus(ClassShared.SID.Peloton);
        PelotonLeft = pelo != null ? StatusDuration(pelo.Value.ExpireAt) : 0;
        SwiftcastLeft = Utils.MaxAll(StatusLeft(ClassShared.SID.Swiftcast), StatusLeft(ClassShared.SID.LostChainspell), StatusLeft(PhantomSID.OccultQuick));
        TrueNorthLeft = StatusLeft(ClassShared.SID.TrueNorth);

        AnimationLockDelay = estimatedAnimLockDelay;

        CombatTimer = (float)(World.CurrentTime - Manager.CombatStart).TotalSeconds;
        (RaidBuffsLeft, RaidBuffsIn) = EstimateRaidBuffTimings(primaryTarget);

        if (Manager.Planner is { } planner && planner.EstimateTimeToNextDowntime() is (var downtimeNow, var stateLeft))
        {
            // an overdue state (an HP-gated phase outlasting the plan) leaves stateLeft frozen at "the state ends now", which read as a
            // downtime always a few seconds away (or 0, "now") while the target stayed up: a downtime that has not started is then unknown
            // the same holds for a downtime state that outlasts its plan: stateLeft reads "the target returns now" for the whole
            // overrun, so its return is unknown (as MechanicForecast already treats it)
            var overdue = planner.CurrentStateOverdue();
            DowntimeIn = downtimeNow ? 0 : overdue ? float.MaxValue : stateLeft;
            UptimeIn = downtimeNow ? (overdue ? null : stateLeft) : 0;
        }
        else
        {
            DowntimeIn = float.MaxValue;
            UptimeIn = null;
        }

        MP = (uint)Math.Clamp(Player.PendingMPRaw, 0, Player.HPMP.MaxMP);

        if (_cdLockout > World.CurrentTime)
            return;

        if (Player.FindStatus(49) is ActorStatus st && Food.GetPotionType(st.Extra) == PotionType)
            PotionLeft = StatusDuration(st.ExpireAt);
        else
            PotionLeft = 0;

        Exec(strategy, PlayerTarget);
    }

    // other classes have timed personal buffs to plan around, like blm leylines, mch overheat, gnb nomercy
    // war could also be here but i dont have a war rotation
    private bool IsSelfish(Class cls) => cls is Class.VPR or Class.SAM or Class.WHM or Class.SGE or Class.DRK;

    private new (float Left, float In) EstimateRaidBuffTimings(Actor? primaryTarget)
    {
        if (Bossmods.ActiveModule?.Info?.GroupType is BossModuleInfo.GroupType.BozjaDuel && IsSelfish(Player.Class))
            return (float.MaxValue, 0);

        if (primaryTarget?.IsStrikingDummy == true)
        {
            // hack for a dummy: expect that raidbuffs appear at 7.8s and then every 120s
            var cycleTime = CombatTimer - 7.8f;
            if (cycleTime < 0)
                return (0, 7.8f - CombatTimer); // very beginning of a fight

            cycleTime %= 120;
            return cycleTime < 20 ? (20 - cycleTime, 0) : (0, 120 - cycleTime);
        }

        var buffsIn = Bossmods.RaidCooldowns.NextDamageBuffIn2();
        if (buffsIn == null)
        {
            if (CombatTimer < 7.8f && World.Party.WithoutSlot(includeDead: true, excludeAlliance: true, excludeNPCs: true).Skip(1).Any(HavePartyBuff))
                buffsIn = 7.8f - CombatTimer;
            else
                // no party members with raid buffs, assume we're never getting any
                buffsIn = float.MaxValue;
        }

        return (Bossmods.RaidCooldowns.DamageBuffLeft(Player, primaryTarget), buffsIn.Value);
    }

    static bool HavePartyBuff(Actor player) => player.Class switch
    {
        Class.MNK => player.Level >= 70, // brotherhood
        Class.DRG => player.Level >= 52, // battle litany
        Class.NIN => player.Level >= 45, // mug/dokumori - level check is for suiton/huton, which grant Shadow Walker
        Class.RPR => player.Level >= 72, // arcane circle

        Class.SMN => player.Level >= 66, // searing light
        Class.RDM => player.Level >= 58, // embolden
        Class.PCT => player.Level >= 70, // starry muse

        Class.BRD => player.Level >= 50, // battle voice - not counting songs since they are permanent kinda
        Class.DNC => player.Level >= 70, // tech finish

        Class.SCH => player.Level >= 66, // chain
        Class.AST => player.Level >= 50, // divination

        _ => false
    };

    public abstract void Exec(in TValues strategy, Enemy? primaryTarget);

    protected (float Left, int Stacks) Status<SID>(SID status, float? pendingDuration = null) where SID : Enum => Player.FindStatus(status, pendingDuration == null ? null : World.FutureTime(pendingDuration.Value)) is ActorStatus s ? (StatusDuration(s.ExpireAt), s.Extra & 0xFF) : (0, 0);
    protected float StatusLeft<SID>(SID status, float? pendingDuration = null) where SID : Enum => Status(status, pendingDuration).Left;
    protected int StatusStacks<SID>(SID status, float? pendingDuration = null) where SID : Enum => Status(status, pendingDuration).Stacks;

    protected float HPRatio(Actor actor) => (float)actor.HPMP.CurHP / Player.HPMP.MaxHP;
    protected float HPRatio() => HPRatio(Player);

    protected uint PredictedHP(Actor actor) => (uint)actor.PendingHPClamped;
    protected float PredictedHPRatio(Actor actor) => (float)PredictedHP(actor) / actor.HPMP.MaxHP;
}

static class Extendxan
{
    public static RotationModuleDefinition.ConfigRef<OffensiveStrategy> DefineShared(this RotationModuleDefinition def, string buffTrackName)
    {
        return def.DefineSharedTA().DefineSimple(SharedTrack.Buffs, "Buffs", displayName: buffTrackName, uiPriority: 498, renderer: typeof(OffensiveStrategyRenderer));
    }

    public static RotationModuleDefinition DefineSharedTA(this RotationModuleDefinition def)
    {
        def.Define(SharedTrack.Targeting).As<Targeting>("Targeting", "対象選択", uiPriority: 500, renderer: typeof(TargetingRenderer))
            .AddOption(Autorotation.Targeting.Manual, "すべてのアクションでプレイヤーの現在ターゲットを使う")
            .AddOption(Autorotation.Targeting.Auto, "範囲アクションで最適な対象を自動選択する(周囲の巻き込み数最大)")
            .AddOption(Autorotation.Targeting.AutoPrimary, "範囲アクションで最適な対象を自動選択しつつ、プレイヤーのターゲットにも必ず当てる")
            .AddOption(Autorotation.Targeting.AutoTryPri, "範囲アクションで最適な対象を自動選択し、プレイヤーにターゲットがいればその対象にも当てる");

        def.Define(SharedTrack.AOE).As<AOEStrategy>("AOE", "範囲回し", uiPriority: 499)
            .AddOption(AOEStrategy.AOE, "有利なら範囲回しを使う")
            .AddOption(AOEStrategy.ST, "単体回しを使う")
            .AddOption(AOEStrategy.ForceAOE, "1体でも常に範囲回しを使う")
            .AddOption(AOEStrategy.ForceST, "単体回しを使う; 複数対象に当たるアクションを一切使わない");

        return def;
    }

    public static RotationModuleDefinition.ConfigRef<OffensiveStrategy> DefineSimple<Index>(this RotationModuleDefinition def, Index track, string name, string displayName = "", int minLevel = 1, float uiPriority = 0, Type? renderer = null) where Index : Enum
    {
        return def.Define(track).As<OffensiveStrategy>(name, displayName, uiPriority: uiPriority, renderer: renderer ?? typeof(OffensiveStrategyRenderer))
            .AddOption(OffensiveStrategy.Automatic, "最適なタイミングで使う", minLevel: minLevel)
            .AddOption(OffensiveStrategy.Delay, "使わない", minLevel: minLevel)
            .AddOption(OffensiveStrategy.Force, "即使用", minLevel: minLevel);
    }

    public static AOEStrategy AOE(this StrategyValues strategy) => strategy.Option(SharedTrack.AOE).As<AOEStrategy>();
    public static Targeting Targeting(this StrategyValues strategy) => strategy.Option(SharedTrack.Targeting).As<Targeting>();
    public static OffensiveStrategy Simple<Index>(this StrategyValues strategy, Index track) where Index : Enum => strategy.Option(track).As<OffensiveStrategy>();
    public static bool BuffsOk(this StrategyValues strategy) => strategy.Option(SharedTrack.Buffs).As<OffensiveStrategy>() != OffensiveStrategy.Delay;
    public static bool AOEOk(this StrategyValues strategy) => strategy.AOE().AOEOk();
    public static bool AOEOk(this AOEStrategy aoe) => aoe is AOEStrategy.AOE or AOEStrategy.ForceAOE;
    public static float DistanceToHitbox(this Actor actor, Enemy? other) => actor.DistanceToHitbox(other?.Actor);

    public static bool IsEnabled(this EnabledByDefault d) => d == EnabledByDefault.Enabled;
    public static bool IsEnabled(this DisabledByDefault d) => d == DisabledByDefault.Enabled;
    public static bool IsEnabled(this Track<EnabledByDefault> d) => d.Value == EnabledByDefault.Enabled;
    public static bool IsEnabled(this Track<DisabledByDefault> d) => d.Value == DisabledByDefault.Enabled;
}
