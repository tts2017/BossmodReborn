using BossMod.Autorotation.Engine;
using BossMod.Data;

namespace BossMod.Autorotation;

// Base class connecting a RotationEngine to BMR. A concrete job module supplies:
//  - its JobDefinition and weights (usually static, shared by all instances),
//  - ReadJobState: game state -> EngineState (gauges, statuses, job cooldowns; GCD / animation lock / combo / targets are filled here),
//  - optionally ActionFor / TargetFor when the definition's ActionId or the default targeting is not enough.
// No module is registered from this file: the class is abstract.
public abstract class EngineRotationModule : RotationModule
{
    protected EngineRotationModule(RotationModuleManager manager, Actor player, RotationEngine engine) : base(manager, player) => _engine = engine;

    // an engine whose construction takes long (BLM's cycle model, about 0.8 s on its first build for a GCD): built on a worker thread so
    // the frame that selects the module does not stall the game; until it is ready the module pushes nothing. Harnesses and tests set
    // SynchronousCreation (every run starts with the engine ready, as before)
    protected EngineRotationModule(RotationModuleManager manager, Actor player, Func<RotationEngine> create) : base(manager, player)
    {
        if (SynchronousCreation)
            _engine = create();
        else
            _pendingEngine = System.Threading.Tasks.Task.Run(() => WarmUp(create()));
    }

    public static bool SynchronousCreation;
    private RotationEngine? _engine;
    private readonly System.Threading.Tasks.Task<RotationEngine>? _pendingEngine;
    protected RotationEngine Engine => _engine!;
    // the engine can decide (always, unless it is still being built)
    protected bool EngineReady
    {
        get
        {
            if (_engine == null && _pendingEngine is { IsCompleted: true } pending)
                _engine = pending.Result; // a failed build rethrows here, on the frame thread, where BMR logs it
            return _engine != null;
        }
    }

    // the first search JIT-compiles the search code (30-40 ms): done on the worker thread, on a throwaway engine of the same job so the
    // returned engine starts from a clean state
    private static RotationEngine WarmUp(RotationEngine engine)
    {
        var scratch = new RotationEngine(engine.Job, engine.Weights);
        var s = EngineState.Create(engine.Job);
        s.Targets = 1;
        scratch.Decide(s, EngineTimeline.Open(), 0);
        return engine;
    }
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
    // the cooldown group the job's 2-minute burst is built around (Arcane Circle, Ikishoten, Bloodfest ...): when no party buff windows
    // are known and this cooldown returns well after the assumed cycle's next window would start (a pull without a countdown: a dungeon
    // pull, a re-engage with the cooldowns partly down), the assumed cycle starts at its return instead of 7.8 s after the pull
    protected virtual string? MainAnchorCooldown => null;
    private int _mainAnchor = -2; // cooldown index, -1 none
    private float _assumedCycleStart; // absolute seconds from combat start of the assumed cycle's first window (this combat)
    private DateTime _assumedCycleCombat; // the combat it was decided for (once, at the first decision of the combat)
    // how long a missing / untargetable target is assumed to stay away when nothing forecasts its return
    protected virtual float UnknownDowntime => 2.5f;

    protected abstract void ReadJobState(ref EngineState s, Actor? primaryTarget);

    protected virtual ActionID ActionFor(SkillDef skill) => new(ActionType.Spell, skill.ActionId);
    // an AoE skill goes to the enemy its shape hits the most priority targets from (ReadTargets, the Targeting setting); a single-target
    // DoT to the DoT target; everything else to the player's target
    protected virtual Actor? TargetFor(SkillDef skill, Actor? primaryTarget)
    {
        if (!skill.RequiresTarget)
            return Player;
        if (skill.Shape >= 0 && _shapeBest[skill.Shape] is { } best)
            return best;
        if (IsSingleTargetDot(skill) && _dotTarget != null)
            return _dotTarget;
        return primaryTarget;
    }
    protected virtual byte CountTargets(Actor? primaryTarget) => 1;

    // --- AoE targeting (the UI Targeting setting, as the xan modules' SelectTarget / AOETargetScorer read it) ---
    protected enum TargetSetting : byte { Manual, Auto, AutoPrimary, AutoTryPrimary }
    private TargetSetting _targeting = TargetSetting.Manual;
    private float _primaryRange = 3; // the range SelectTarget was given (AutoTryPrimary / the DoT target fallback)
    private AoeSetting _aoe;
    private AoeCandidate[] _candidates = new AoeCandidate[32];
    private Actor[] _candidateActors = new Actor[32];
    private readonly Actor?[] _shapeBest = new Actor?[EngineLimits.MaxShapes];
    private readonly int[] _shapeHits = new int[EngineLimits.MaxShapes];
    private Actor? _dotTarget;

    private static bool IsSingleTargetDot(SkillDef skill) => skill.DotStatus >= 0 && !skill.DotAoe && skill.Shape < 0;

    // per decision, after the strategy is applied: for every AoE shape of the job the best target and its hit count (Basexan's scorer for
    // the Targeting setting, the AOE setting's adjustment, forbidden targets), into s.ShapeTargets for the planner and _shapeBest for
    // TargetFor. A shape with no usable target (a forbidden target would be hit, no target at all) disables its skills, as the old modules
    // never pushed a skill whose best target was null. The single-target DoT target follows the ForbidDOTs hint.
    private void ReadTargets(ref EngineState s, Actor? primaryTarget)
    {
        _dotTarget = null;
        if (Job.Shapes.Length == 0 && !Array.Exists(Job.Skills, IsSingleTargetDot))
            return;
        var priority = Hints.PriorityTargetsSpan;
        var forbidden = Hints.ForbiddenTargetsSpan;
        var n = priority.Length + forbidden.Length + 1;
        if (_candidates.Length < n)
        {
            _candidates = new AoeCandidate[n * 2];
            _candidateActors = new Actor[n * 2];
        }
        var count = 0;
        var primary = -1;
        for (var i = 0; i < priority.Length; ++i)
            Add(priority[i].Actor, false, false);
        for (var i = 0; i < forbidden.Length; ++i)
            Add(forbidden[i].Actor, true, forbidden[i].Priority == AIHints.Enemy.PriorityUndesirable);
        if (primaryTarget != null && primary < 0)
        {
            // the player's target is in neither list: a candidate that hits nothing extra
            _candidates[count] = new(primaryTarget.Position.X, primaryTarget.Position.Z, primaryTarget.HitboxRadius, false, false, false);
            _candidateActors[count] = primaryTarget;
            primary = count++;
        }
        var enemies = new ReadOnlySpan<AoeCandidate>(_candidates, 0, count);
        float px = Player.Position.X, pz = Player.Position.Z;
        for (var i = 0; i < Job.Shapes.Length; ++i)
        {
            var shape = Job.Shapes[i];
            var mode = _targeting switch
            {
                TargetSetting.Manual => TargetMode.Manual,
                TargetSetting.AutoPrimary => TargetMode.AutoPrimary,
                TargetSetting.AutoTryPrimary => Player.DistanceToHitbox(primaryTarget) <= shape.Range ? TargetMode.AutoPrimary : TargetMode.Auto,
                _ => TargetMode.Auto,
            };
            if (_aoe == AoeSetting.ForceSingleTarget)
                mode = TargetMode.Manual;
            var best = AoeTargeting.Select(shape, mode, _aoe, px, pz, enemies, primary, out var hits);
            _shapeBest[i] = best >= 0 ? _candidateActors[best] : null;
            // ForceAOE: the count every AoE skill is planned on (ForceAoeTargets), at least what the shape hits
            _shapeHits[i] = _aoe == AoeSetting.ForceAoe ? Math.Max(hits, s.Targets) : hits;
            s.ShapeTargets[i] = (byte)Math.Clamp(_shapeHits[i], 1, 255);
            // (with no attackable target the timeline's downtime already covers it: a horizon-wide disable would also hide the skills
            // from the plan for after the target returns)
            if (hits == 0 && _aoe != AoeSetting.ForceSingleTarget && primaryTarget is { IsTargetable: true })
                foreach (var sk in Job.Skills)
                    if (sk.Shape == i)
                        s.DisabledSkills |= 1UL << sk.Index;
        }

        // single-target DoTs: the player's target unless DoTs on it are forbidden; then (Auto) the first priority target in range
        // that allows them, else no DoT
        if (primaryTarget != null && Hints.FindEnemy(primaryTarget) is { ForbidDOTs: true })
        {
            var auto = _targeting is TargetSetting.Auto || _targeting == TargetSetting.AutoTryPrimary && primaryTarget == null;
            if (auto)
                foreach (var e in priority)
                    if (!e.ForbidDOTs && Player.DistanceToHitbox(e.Actor) <= _primaryRange)
                    {
                        _dotTarget = e.Actor;
                        break;
                    }
            if (_dotTarget == null)
                foreach (var sk in Job.Skills)
                    if (IsSingleTargetDot(sk))
                        s.DisabledSkills |= 1UL << sk.Index;
        }

        void Add(Actor actor, bool isForbidden, bool undesirable)
        {
            _candidates[count] = new(actor.Position.X, actor.Position.Z, actor.HitboxRadius, isForbidden, undesirable);
            _candidateActors[count] = actor;
            if (actor == primaryTarget)
                primary = count;
            ++count;
        }
    }

    // the hit count of the shape of `skill` (the main count for single-target skills): the synced rules decide AoE skills on it
    protected int ShapeTargets(in EngineState s, string skill)
    {
        var i = Job.TrySkillIndex(skill);
        return i < 0 ? s.Targets : s.TargetsOf(Job.Skills[i].Shape);
    }

    private void Push(SkillDef skill, Actor? primaryTarget, float priority, float castTime = 0, float delay = 0, bool trace = false)
    {
        var target = TargetFor(skill, primaryTarget);
        var action = ActionFor(skill);
        Hints.ActionsToExecute.Push(action, target, priority, castTime: castTime, delay: delay, targetPos: AreaPosition(action, target));
        if (trace && DebugTrace != null && skill.Shape >= 0)
        {
            var shape = Job.Shapes[skill.Shape];
            DebugTrace(FormattableString.Invariant($"[target {Job.Name}] t={CombatTime:f2} skill={skill.Name} shape={shape.Kind}:{shape.Size:g}{(shape.Width > 0 ? "/" + shape.Width.ToString("g", System.Globalization.CultureInfo.InvariantCulture) : "")} targeting={_targeting} aoe={_aoe} target={(target != null ? target.Name + "#" + target.InstanceID.ToString("X") : "none")} hits={_shapeHits[skill.Shape]}"));
        }
    }

    // a ground-targeted action (Ley Lines) is placed at the player when it has no range, else at its target (Basexan.PushAction): without a
    // location the client places it at the map origin and refuses it, and the engine would keep planning it every weave window
    private Vector3 AreaPosition(ActionID action, Actor? target)
    {
        var def = ActionDefinitions.Instance[action];
        if (def == null || (def.AllowedTargets & ActionTargets.Area) == 0)
            return default;
        return def.Range == 0 ? Player.PosRot.XYZ() : target?.PosRot.XYZ() ?? Player.PosRot.XYZ();
    }

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
    // a hold the search plans past: the skill is unusable for `seconds` from now and legal again after (one hold time per decision: the
    // latest requested applies to every held skill), unlike Forbid, which lasts the whole search horizon
    protected void Hold(ref EngineState s, string skill, float seconds)
    {
        var i = Job.TrySkillIndex(skill);
        if (i < 0 || seconds <= 0)
            return;
        s.HeldSkills |= 1UL << i;
        s.HeldUntil = MathF.Max(s.HeldUntil, s.Time + seconds);
    }

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

    // xan Targeting track (the setting is kept for the AoE shapes' target choice, ReadTargets)
    protected Actor? SelectTarget(Targeting targeting, Actor? primaryTarget, float range)
    {
        _targeting = targeting switch
        {
            Targeting.Auto => TargetSetting.Auto,
            Targeting.AutoPrimary => TargetSetting.AutoPrimary,
            Targeting.AutoTryPri => TargetSetting.AutoTryPrimary,
            _ => TargetSetting.Manual,
        };
        _primaryRange = range;
        return targeting is Targeting.Auto or Targeting.AutoTryPri ? TargetInRange(primaryTarget, range) : primaryTarget;
    }

    // Akechi Targeting track (AutoHard also switches the player's hard target)
    protected Actor? SelectTarget(akechi.Custom.SoftTargetStrategy targeting, Actor? primaryTarget, float range)
    {
        _targeting = targeting switch
        {
            akechi.Custom.SoftTargetStrategy.Manual => TargetSetting.Manual,
            akechi.Custom.SoftTargetStrategy.AutoPrimary => TargetSetting.AutoPrimary,
            akechi.Custom.SoftTargetStrategy.AutoTryPrimary => TargetSetting.AutoTryPrimary,
            _ => TargetSetting.Auto,
        };
        _primaryRange = range;
        if (targeting is akechi.Custom.SoftTargetStrategy.Manual or akechi.Custom.SoftTargetStrategy.AutoPrimary)
            return primaryTarget;
        var target = TargetInRange(primaryTarget, range);
        if (targeting == akechi.Custom.SoftTargetStrategy.AutoHard && target != primaryTarget)
            Hints.ForcedTarget = target;
        return target;
    }

    // xan AOE track: ST / ForceST plan on one target (ForceST also forbids every skill that hits several targets), ForceAOE on enough
    // targets for every AoE skill
    protected void ApplyAoe(ref EngineState s, AOEStrategy aoe) => ApplyAoe(ref s, aoe switch
    {
        AOEStrategy.ST => AoeSetting.SingleTarget,
        AOEStrategy.ForceST => AoeSetting.ForceSingleTarget,
        AOEStrategy.ForceAOE => AoeSetting.ForceAoe,
        _ => AoeSetting.Auto,
    });

    protected void ApplyAoe(ref EngineState s, AoeSetting aoe)
    {
        _aoe = aoe;
        if (aoe is AoeSetting.SingleTarget or AoeSetting.ForceSingleTarget)
            s.Targets = 1;
        if (aoe == AoeSetting.ForceSingleTarget)
            ForbidMultiTarget(ref s);
        if (aoe == AoeSetting.ForceAoe)
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

    public sealed override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        var mode = ReadComposition(strategy);
        strategy = WithoutComposition(strategy);
        _baseline ??= mode != CompositionStrategy.EngineOnly ? CreateBaseline() : null;
        Overriding = false;
        if (_baseline == null || mode == CompositionStrategy.EngineOnly)
        {
            ExecuteJob(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
            return;
        }

        // two tiers: the xan / Akechi module decides, then the engine replaces its choice only where the predicted mechanics make a better
        // move (ShouldOverride). Both push into the shared queue; the losing side's entries are taken out again
        var queue = Hints.ActionsToExecute.Entries;
        var baseStart = queue.Count;
        _baseline.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        var baseEnd = queue.Count;
        if (mode == CompositionStrategy.BaselineOnly || !EngineReady)
            return;
        var forcedTarget = Hints.ForcedTarget;
        var positional = Hints.RecommendedPositional;
        _decided = false;
        ExecuteJob(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        var engineEnd = queue.Count;
        // the job module answered without a search (Ten Chi Jin, a countdown opener ...): the first tier's answer stands
        Overriding = _decided && ShouldOverride(queue, baseStart, baseEnd, estimatedAnimLockDelay, isMoving);
        if (Overriding)
        {
            queue.RemoveRange(baseStart, baseEnd - baseStart);
        }
        else
        {
            queue.RemoveRange(baseEnd, engineEnd - baseEnd);
            Hints.ForcedTarget = forcedTarget;
            Hints.RecommendedPositional = positional;
        }
    }

    // --- two tiers: the xan / Akechi module first, the engine where the timeline makes a better move ---
    public enum CompositionStrategy
    {
        [Option("xan/Akechi 基本 + 予測で最善手がある時だけ [Engine] に差し替え")]
        BaselineFirst,
        [Option("[Engine] のみ (従来の動作)")]
        EngineOnly,
        [Option("xan/Akechi のみ (差し替えなし)")]
        BaselineOnly,
    }
    public const string CompositionTrack = "Composition";

    // appended after the job's strategy tracks (presets store tracks by name, so existing presets keep their values; option 0 is the default)
    protected static RotationModuleDefinition WithComposition(RotationModuleDefinition def)
    {
        var track = new StrategyConfigTrack(typeof(CompositionStrategy), CompositionTrack, "回しの組み立て (1 段目 xan/Akechi、2 段目 [Engine])", 100, typeof(TrackRenderer));
        foreach (var value in Enum.GetValues<CompositionStrategy>())
        {
            var field = typeof(CompositionStrategy).GetField(value.ToString())!;
            var display = field.GetCustomAttributes(typeof(OptionAttribute), false) is [OptionAttribute o, ..] ? o.DisplayName ?? value.ToString() : value.ToString();
            track.Options.Add(new(value.ToString(), display));
        }
        def.Configs.Add(track);
        return def;
    }

    private int _compositionIndex = -2;
    private CompositionStrategy ReadComposition(StrategyValues strategy)
    {
        if (_compositionIndex == -2)
            _compositionIndex = strategy.Configs.FindIndex(c => c.InternalName == CompositionTrack);
        return _compositionIndex >= 0 && strategy.Values[_compositionIndex] is StrategyValueTrack v ? (CompositionStrategy)v.Option : CompositionStrategy.EngineOnly;
    }

    // the job's own tracks only (ValueConverter.FromValues requires exactly the strategy struct's track count): the composition track is
    // the last one; the values are shared, not copied
    private StrategyValues? _jobValues;
    private StrategyValues WithoutComposition(StrategyValues strategy)
    {
        if (_compositionIndex < 0 || _compositionIndex != strategy.Configs.Count - 1)
            return strategy;
        if (_jobValues is not { } values || values.Configs.Count != _compositionIndex)
            _jobValues = values = new StrategyValues(strategy.Configs.GetRange(0, _compositionIndex));
        Array.Copy(strategy.Values, values.Values, _compositionIndex);
        return values;
    }

    // everything the module does on the engine's side for one frame: the search and the pushes of its decision (ExecuteEngine), plus
    // whatever a job module adds around it (utility actions, countdown openers ...). In the two-tier mode all of it is dropped on the frames
    // where the first tier's choice stands
    protected virtual void ExecuteJob(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        if (EngineReady)
            ExecuteEngine(strategy, primaryTarget, isMoving);
    }
    private bool _decided; // ExecuteEngine ran this frame (LastDecision is this frame's)

    // the module whose decisions are the first tier (the xan / Akechi module of the same strategy tracks); null: the engine alone
    protected virtual RotationModule? CreateBaseline() => null;
    private RotationModule? _baseline;

    public override void Dispose()
    {
        _baseline?.Dispose();
        base.Dispose();
    }
    // this frame the engine's choice replaced the first tier's (diagnostics, harnesses)
    public bool Overriding { get; private set; }
    public static int OverrideFrames, CompareFrames; // harness counters

    // The engine replaces the first tier's move when (a) the two press different things now, (b) the engine's search values its own move
    // above the best line starting with the first tier's move by OverrideMargin, and (c) that advantage comes from the predicted timeline:
    // the same comparison on a timeline without predictions (no downtime / forced movement / fight end / raid buff windows ahead, only what
    // holds now) does not show it. A disagreement the engine also has without predictions is a difference of the two models, where the
    // first tier is kept. Below level 100 the synced rules give no search values: the first tier is kept.
    private bool ShouldOverride(List<ActionQueue.Entry> queue, int start, int end, float animLockDelay, bool isMoving)
    {
        var d = LastDecision;
        var baseMove = _lastSynced ? UnknownMove : BaselineMove(queue, start, end, animLockDelay);
        if (baseMove == d.Skill)
        {
            // the tiers agree again: a commitment ends here
            _overrideMove = UnknownMove;
            _commitUntil = float.NegativeInfinity;
            return false;
        }
        // an override in progress: the engine plays its line until the first tier's move matches it again (or CommitSeconds pass), so the
        // two plans are not interleaved move by move
        if (_lastNow < _commitUntil && !_lastSynced)
        {
            ++OverrideFrames;
            return true;
        }
        if (baseMove == UnknownMove)
        {
            _overrideMove = UnknownMove;
            return false;
        }
        ++CompareFrames;
        var real = d.Value - Engine.LastRootValue(baseMove);
        var margin = OverrideMargin(d.Value);
        // a move kept from the previous frames needs half the margin (no flipping between the tiers inside one weave window)
        if (_overrideMove == d.Skill && _overrideBase == baseMove)
            margin *= 0.5f;
        if (!(real >= margin))
        {
            _overrideMove = UnknownMove;
            return false;
        }

        var neutral = NeutralEngine();
        var tl = NeutralTimeline(isMoving);
        var nd = neutral.Decide(_lastState, tl, _lastNow);
        if (nd.Partial && CanPressNow())
            neutral.FinishPending(_lastNow);
        var unpredicted = neutral.LastRootValue(d.Skill) - neutral.LastRootValue(baseMove);
        // the prediction is what makes the difference: without it the engine rates the first tier's move about as good as its own (two models
        // disagreeing on their own is not a reason to switch, and alternating between their plans loses to either one alone)
        var over = unpredicted < margin && real - MathF.Max(0, unpredicted) >= margin; // NaN (not searched to the end): no override
        DebugTrace?.Invoke(FormattableString.Invariant($"[engine {Job.Name}] compare base={MoveName(baseMove)} engine={MoveName(d.Skill)} gap={real:f1} unpredicted={unpredicted:f1} margin={margin:f1} override={over}"));
        if (over)
        {
            ++OverrideFrames;
            _overrideMove = d.Skill;
            _overrideBase = baseMove;
            _commitUntil = _lastNow + CommitSeconds;
        }
        else
        {
            _overrideMove = UnknownMove;
        }
        return over;
    }

    private const int UnknownMove = -2;
    private int _overrideMove = UnknownMove, _overrideBase = UnknownMove;
    private float _commitUntil = float.NegativeInfinity;
    protected virtual float CommitSeconds => 10;
    protected virtual float OverrideMargin(float value) => MathF.Max(30, 0.01f * MathF.Abs(value));
    private string MoveName(int move) => move >= 0 ? Job.Skills[move].Name : move == EngineDecision.Wait ? "wait" : "?";

    // the first tier's move in the engine's terms: what the queue would press now from its entries alone, else (a GCD rolling, nothing to
    // weave) its highest-priority GCD; UnknownMove when that is an action the definition does not have (role actions, items ...)
    private readonly ActionQueue _scratchQueue = new();
    private int BaselineMove(List<ActionQueue.Entry> queue, int start, int end, float animLockDelay)
    {
        if (start == end)
            return EngineDecision.Wait;
        _scratchQueue.Clear();
        for (var i = start; i < end; ++i)
            _scratchQueue.Entries.Add(queue[i]);
        var best = _scratchQueue.FindBest(World, Player, World.Client.Cooldowns, World.Client.AnimationLock, Hints, animLockDelay, false);
        if (best.Action)
            return SkillFor(best.Action);
        var topGcd = -1;
        var topPriority = float.MinValue;
        for (var i = start; i < end; ++i)
        {
            var e = queue[i];
            if (e.Priority > topPriority && SkillFor(e.Action) is var sk && sk >= 0 && Job.Skills[sk].IsGcd)
            {
                topGcd = sk;
                topPriority = e.Priority;
            }
        }
        return topGcd >= 0 ? topGcd : UnknownMove;
    }

    // the definition's skill pressed by `action`: the variant legal in the state just read, else the first with that action
    private int SkillFor(ActionID action)
    {
        if (action.Type != ActionType.Spell)
            return UnknownMove;
        var first = UnknownMove;
        foreach (var sk in Job.Skills)
        {
            if (ActionFor(sk) != action)
                continue;
            if (Simulator.IsLegal(Job, _lastState, _lastTimeline, sk))
                return sk.Index;
            if (first == UnknownMove)
                first = sk.Index;
        }
        return first;
    }

    // a second engine searching the same state on the unpredicted timeline (same definition and weights)
    private RotationEngine? _neutral;
    private RotationEngine NeutralEngine()
    {
        _neutral ??= new RotationEngine(Engine.Job, Engine.Weights) { FrameBudgetMs = Engine.FrameBudgetMs, ReplanInterval = Engine.ReplanInterval, MaxReuseAge = Engine.MaxReuseAge };
        if (!ReferenceEquals(_neutral.Weights, Engine.Weights))
            _neutral.SetWeights(Engine.Weights);
        _neutral.BudgetScale = Engine.BudgetScale;
        return _neutral;
    }

    // what holds now only: no target now and moving now (as BuildTimeline), nothing predicted
    private EngineTimeline NeutralTimeline(bool isMoving)
    {
        var tl = EngineTimeline.Open();
        if (Target == null || !Target.IsTargetable)
            tl.AddDowntime(0, UnknownDowntime);
        if (isMoving)
            tl.AddNoCast(0, 0.5f);
        tl.Version = isMoving ? 1 : 0;
        return tl;
    }

    private EngineState _lastState;
    private EngineTimeline _lastTimeline;
    private float _lastNow;
    private bool _lastSynced;

    private void ExecuteEngine(StrategyValues strategy, Actor? primaryTarget, bool isMoving)
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
        _assumedCycleStart = AssumedCycleStart(s);
        _aoe = AoeSetting.Auto;
        ApplyStrategy(strategy, ref s, primaryTarget);
        ReadTargets(ref s, primaryTarget);

        var tl = BuildTimeline(isMoving, primaryTarget);
        if (_epoch == default)
            _epoch = World.CurrentTime;
        var now = (float)(World.CurrentTime - _epoch).TotalSeconds; // small numbers: float keeps millisecond precision
        EngineDecision d;
        _lastState = s;
        _lastTimeline = tl;
        _lastNow = now;
        _lastSynced = Player.Level < 100 && HasSyncedRules;
        if (_lastSynced)
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
        _decided = true;
        if (DebugTrace != null && !d.Reused)
            DebugTrace(FormattableString.Invariant($"[engine {Job.Name}] t={now:f2} gcd={GCD:f2} skill={(d.Skill >= 0 ? Job.Skills[d.Skill].Name : "wait")} nextGcd={(d.NextGcd >= 0 ? Job.Skills[d.NextGcd].Name : "wait")} at={d.ExecuteAt:f2} depth={d.Depth} nodes={d.Nodes} hyst={d.Hysteresis} partial={d.Partial} combo={(s.ComboSkill != EngineLimits.NoCombo ? Job.Skills[s.ComboSkill].Name : "-")}/{World.Client.ComboState.Action}:{World.Client.ComboState.Remaining:f1} targets={s.Targets} legalGcds={string.Join(",", LegalGcds(s, tl))}"));

        // a GCD the plan makes legal only through the ability before it (Kassatsu -> Hyosho Ranryu, whose first mudra would otherwise go
        // first when the GCD is already up, and the sequence then does not match the ability): the GCD waits for the ability's frame
        if (d.NextGcd >= 0 && !(d.Skill >= 0 && !Job.Skills[d.Skill].IsGcd && GcdNeedsAbilityFirst(s, tl, d.Skill, d.NextGcd)))
        {
            var gcd = Job.Skills[d.NextGcd];
            Push(gcd, primaryTarget, ActionQueue.Priority.High + 2, castTime: gcd.CastTime, trace: !d.Reused);
        }
        if (d.Skill >= 0 && !Job.Skills[d.Skill].IsGcd)
        {
            var ogcd = Job.Skills[d.Skill];
            Push(ogcd, primaryTarget, ActionQueue.Priority.Low + 1, delay: d.ExecuteAt, trace: !d.Reused);
        }
        // forced skills: ahead of the engine's GCD, abilities in the first weave slot
        for (var i = 0; i < _numForced; ++i)
        {
            var sk = Job.Skills[_forced[i]];
            if (Simulator.IsLegal(Job, s, tl, sk))
                Push(sk, primaryTarget, (sk.IsGcd ? ActionQueue.Priority.High + 3 : ActionQueue.Priority.Medium + 1) - i * 0.01f, castTime: sk.CastTime);
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

    // whether `gcd` is illegal in s but legal once `ogcd` has been used (the plan's weave unlocks it)
    private bool GcdNeedsAbilityFirst(in EngineState s, in EngineTimeline tl, int ogcd, int gcd)
    {
        var at = s;
        Simulator.Advance(Job, ref at, MathF.Max(s.GcdReadyAt, s.AnimLockAt) - at.Time, _castCtx);
        if (Simulator.IsLegal(Job, at, tl, Job.Skills[gcd]))
            return false;
        var after = s;
        Simulator.Advance(Job, ref after, s.AnimLockAt - after.Time, _castCtx);
        if (!Simulator.IsLegal(Job, after, tl, Job.Skills[ogcd]))
            return false;
        Simulator.Execute(Job, ref after, tl, Job.Skills[ogcd], _castCtx);
        Simulator.Advance(Job, ref after, MathF.Max(after.GcdReadyAt, after.AnimLockAt) - after.Time, _castCtx);
        return Simulator.IsLegal(Job, after, tl, Job.Skills[gcd]);
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
            for (var t = _assumedCycleStart; t < elapsed + 360 && tl.NumBuffs < EngineLimits.MaxWindows; t += RaidBuffInterval)
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
        var cycle = (float)(World.CurrentTime - Manager.CombatStart).TotalSeconds - _assumedCycleStart;
        if (cycle < 0)
            return (0, -cycle);
        cycle %= RaidBuffInterval;
        return cycle < RaidBuffDuration ? (RaidBuffDuration - cycle, 0) : (0, RaidBuffInterval - cycle);
    }

    // the assumed cycle's first window (seconds from combat start), decided once per combat at its first decision: RaidBuffFirst, or
    // the main anchor's return when it is on cooldown then and comes back after the assumed first window would be half over (a pull
    // without a countdown with the anchor partly down). Later in the combat the anchor drifting (downtime, a late press) does not move
    // the cycle: a party's buffs do not follow the player's drift, and the player's own raid buff is known from its cast anyway
    private float AssumedCycleStart(in EngineState s)
    {
        if (Manager.CombatStart == default)
            return RaidBuffFirst;
        if (Manager.CombatStart == _assumedCycleCombat)
            return _assumedCycleStart;
        _assumedCycleCombat = Manager.CombatStart;
        if (_mainAnchor == -2)
            _mainAnchor = MainAnchorCooldown != null ? Array.FindIndex(Job.Cooldowns, cd => cd.Name == MainAnchorCooldown) : -1;
        if (_mainAnchor < 0 || s.Charges[_mainAnchor] > 0 || !AssumeRaidBuffCycle)
            return RaidBuffFirst;
        var elapsed = (float)(World.CurrentTime - Manager.CombatStart).TotalSeconds;
        var next = RaidBuffFirst;
        while (next + RaidBuffDuration <= elapsed)
            next += RaidBuffInterval;
        var anchorAt = elapsed + s.CdReadyIn[_mainAnchor];
        return anchorAt > next + RaidBuffDuration * 0.5f ? anchorAt : RaidBuffFirst;
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
