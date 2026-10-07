namespace BossMod;

// Follows the imported per-content timeline during a fight and tells rotations when the target will be gone, without replacing the
// state machine of a module. The timeline clock is anchored on observed enemy actions: a cast start or an ability effect that matches
// a sync point places us on the timeline, later matches keep the clock honest. Only downtimes long enough to matter are published,
// and only while the clock was confirmed recently, because a rotation reads a published target loss as a reason to hold bursts and
// gauge spenders.
public sealed class ExternalTimelineHints : IDisposable
{
    public const string HintNamespace = ExternalMechanicHintProvider.TimelineNamespace;
    public const float MinPublishedLoss = 8.5f; // matches DisengageForecaster: shorter gaps are dodges or blips, not downtime
    public const float SyncTolerance = 6f; // an action may land this far from its timeline slot and still count as the same event
    private const float SyncLifetime = 60f; // how long a confirmation keeps the clock trustworthy
    private const float TargetableSyncTolerance = 20f; // how far the boss may appear or vanish from its entry and still be that entry
    private const float ContradictionGrace = 3f; // how long the fight may disagree with the timeline before the clock is dropped
    private const float EdgeSettle = 1f; // how long a change of anything attackable must last before it counts as a window edge
    public const float ContinuationGap = 3f; // a followed boss back in combat within this many seconds of leaving it is the same fight
    private const float Horizon = Autorotation.MechanicForecast.ForecastSeconds; // predictions further out than this drift too much to be worth publishing
    private const float AlignmentTolerance = 1.5f; // how far an observed action may sit from its entry while still counting as a match
    private const int MinAlignmentScore = 2; // actions that must agree before the clock moves to a new place on the timeline
    private const float MaxBackwardJump = 30f; // a confirmed clock only moves further back than this on stronger evidence
    private const int BackwardJumpScore = 3; // distinct actions that must agree before a confirmed clock jumps far backwards
    private const int HistorySize = 8;
    public const float MinWindowConfidence = 0.5f; // measured windows below this spread too much across pulls to plan around
    // HP-gated branches (TimelineSequence.Branch): the boss HP is only forecast to the decision point from this close, because a
    // linear extrapolation further out picks the wrong sibling; the forecast must clear the threshold by the margin to count.
    private const float BranchMaxExtrapolation = 8f;
    private const float BranchForecastMargin = 3f; // HP percentage points
    private const float BranchCastGrace = 1f; // how long past the decision point the early sibling's cast may still come
    private const float BranchPointTolerance = 0.1f; // a state this close to DecisionTime is the branch point itself
    // A window starting this close before DecisionTime is the early window itself: the extractor caps DecisionTime at a median of
    // the early pulls' window starts, which the early sibling's own window may miss by float noise.
    private const float BranchWindowEpsilon = 0.05f;
    private const float HpSlopeWindow = 10f; // seconds of boss HP samples the slope is fitted over
    private const float MinHpSlopeSpan = 5f; // samples covering less time than this give no slope (a fresh pull, a new boss)
    private const float HpSampleInterval = 0.5f;
    private const int HpSampleCapacity = 32; // 16 s of samples at the interval above: the slope window plus slack

    private readonly WorldState _ws;
    private readonly EventSubscriptions _subscriptions;
    private ExternalPlannerTimeline.TimelineDefinition? _timeline;
    private ushort _timelineZone = ushort.MaxValue;
    private int _timelineGeneration = -1; // TimelineStore.Generation the current timeline was resolved from
    private ExternalPlannerTimeline.TimelineSequence? _sequence;
    private ExternalPlannerTimeline.TimelineSequence? _lastConfirmed; // the sequence the fight last confirmed; the next pull likely continues after it
    private DateTime _anchorAt;
    private float _anchorTime;
    private DateTime _syncedAt;
    private bool _published;
    private readonly List<(DateTime At, uint ActionID, bool IsCastStart)> _history = [];
    private DateTime _contradictedSince;
    private bool _confirmed;
    private float _sequenceTail; // last timeline moment the anchored sequence describes (see SequenceTail)
    private ExternalPlannerTimeline.TimelineSequence? _exhaustedSequence; // a sequence the clock ran out of; no alignment may return to it before the next pull
    private DateTime _bossLeftCombatAt; // when the followed boss last left combat; default when it never did
    private ExternalPlannerTimeline.TimelineSequence? _branchDecided; // the sibling this pull's branch was decided for; null while undecided
    // Boss HP% samples for the branch forecast, a ring buffer so sampling allocates nothing per frame. Cleared with the branch state.
    private readonly DateTime[] _hpSampleAt = new DateTime[HpSampleCapacity];
    private readonly float[] _hpSamplePercent = new float[HpSampleCapacity];
    private int _hpSampleCount;
    private int _hpSampleNext; // slot the next sample goes to
    private ulong _hpSampleActor; // the enemy the samples belong to
    private float _hpSlope = float.NaN; // fitted when a sample is added (see HpSlope)
    // The divergence entries of the last branch looked at (see CollectDivergenceStates), kept until the siblings change.
    private ExternalPlannerTimeline.TimelineSequence? _divergenceEarly;
    private ExternalPlannerTimeline.TimelineSequence? _divergenceLate;
    private readonly List<ExternalPlannerTimeline.TimelineState> _divergenceStates = [];
    private float _divergenceWindowStart = float.MaxValue; // EarlyWindowStart of _divergenceEarly
    // Attackability edges (see TrackAttackability): the settled value of AnyAttackable, a change of it waiting to settle, and when
    // the world last changed in a way that can flip it.
    private bool _attackabilityKnown;
    private bool _attackable;
    private bool _attackabilityFlipPending;
    private DateTime _attackabilityFlipAt;
    private DateTime _attackabilityChangedAt;
    private DateTime _attackabilityCheckedAt;
    private bool _targetsUpInDowntime; // this frame, the clock is inside a NoTarget window but something is attackable (see CheckAgainstWorld)

    // Measurement switches for tools/timeline_hint_harness; production leaves them at their defaults.
    public static bool UseAbilitySync = true;
    public static ExternalPlannerTimeline.TimelineSource? OnlySource;

    // A clock placed by the pull stays usable while the fight keeps matching it; one placed by actions also expires on silence.
    // Predictions need a clock that a live event has confirmed recently. The pull alone places the clock, but a fight that runs even a
    // few percent off the import drifts far enough to announce a downtime that never comes, and rotations hold bursts for those.
    public bool Synced => _sequence != null && _confirmed && (_ws.CurrentTime - _syncedAt).TotalSeconds <= SyncLifetime;
    public float TimelineTime => _sequence == null ? float.NaN : _anchorTime + (float)(_ws.CurrentTime - _anchorAt).TotalSeconds;
    // A confirmed clock that ran past everything the sequence knows: the import has no information there, so it predicts nothing
    // and refuses to be re-aligned back into that sequence (a fight looping past a shorter source pull would otherwise drag the clock
    // back into an earlier loop and announce a downtime that already happened). Exhaustion is remembered until a real pull start:
    // the contradiction check or an expired clock would otherwise hand the same sequence straight back to alignment.
    private bool RefreshExhausted()
    {
        if (_sequence == null || !_confirmed || TimelineTime <= _sequenceTail + SyncTolerance)
            return _sequence != null && _sequence == _exhaustedSequence;
        _exhaustedSequence = _sequence;
        return true;
    }
    public static bool Enabled => Service.Config?.Get<CustomConfig>()?.UseExternalTimelineHints == true;

    public ExternalTimelineHints(WorldState ws)
    {
        _ws = ws;
        _subscriptions = new(ws.Actors.CastStarted.Subscribe(OnCastStarted), ws.Actors.CastEvent.Subscribe(OnCastEvent), ws.Actors.InCombatChanged.Subscribe(OnCombatChanged), ws.Actors.IsTargetableChanged.Subscribe(OnTargetableChanged),
            ws.Actors.IsDeadChanged.Subscribe(NoteAttackabilityChange), ws.Actors.Removed.Subscribe(NoteAttackabilityChange));
    }

    public void Dispose()
    {
        _subscriptions.Dispose();
        _exhaustedSequence = null;
        _bossLeftCombatAt = default;
        ResetBranch();
        Unpublish();
    }

    public void Update(BossModule? activeModule)
    {
        if (!Enabled || !ShouldRun(activeModule))
        {
            // Nothing is tracked while not running: a change seen on resuming would be dated back to whatever happened meanwhile.
            ResetAttackability();
            Unpublish();
            return;
        }

        // A zone change or a store reload (user files re-read) replaces the timeline; anything anchored on the old one is dropped.
        if (_timelineZone != _ws.CurrentZone || _timelineGeneration != TimelineStore.Generation)
        {
            _timelineZone = _ws.CurrentZone;
            _timeline = ResolveTimeline();
            _sequence = null;
            _lastConfirmed = null;
            _exhaustedSequence = null;
            _bossLeftCombatAt = default;
            _confirmed = false;
            _history.Clear();
            ResetBranch();
            ResetAttackability();
        }

        UpdateBranch();
        RefreshExhausted();
        CheckAgainstWorld();
        Publish();
    }

    // The best-ranked import for the current zone (or the best of the requested source when measuring). Reading the generation
    // before the lookup means a reload racing with this call is noticed on the next frame rather than missed.
    private ExternalPlannerTimeline.TimelineDefinition? ResolveTimeline()
    {
        _timelineGeneration = TimelineStore.Generation;
        return OnlySource is { } only
            ? TimelineStore.CandidatesForZone(_timelineZone).FirstOrDefault(t => t.Source == only)
            : TimelineStore.ForZone(_timelineZone);
    }

    // A module with a real state machine already feeds the rotation through its own planner, so the import stays out of its way.
    // (a module built without registry info has an empty state machine, which is no timeline either)
    // A state machine that ExternalPlannerTimeline rebuilt from this same import is not the module's own: it only turns targetability
    // markers into downtime, and imports written from replays or FFLogs carry their downtime as NoTarget windows instead, so without
    // the follower such a fight would announce no target loss at all.
    public static bool ShouldRun(BossModule? module)
        => module == null || module.StateMachineFromTimeline || module.StateMachine.Phases.Count == 0
            || module.StateMachine.Phases.Count == 1 && module.StateMachine.Phases[0].InitialState is { NextStates: null, Update: null };

    private void OnCastStarted(Actor actor)
    {
        if (_timeline == null || actor.Type != ActorType.Enemy || actor.IsAlly || actor.CastInfo is not { } cast || !cast.IsSpell())
            return;
        Observe(cast.Action.ID, true);
    }

    // Instant abilities never start a cast, and most imported timelines are written from their effects rather than from cast bars, so
    // the effect event is the sync point that actually exists for them.
    private void OnCastEvent(Actor actor, ActorCastEvent ev)
    {
        if (!UseAbilitySync || _timeline == null || actor.Type != ActorType.Enemy || actor.IsAlly || !ev.IsSpell())
            return;
        Observe(ev.Action.ID, false);
    }

    private void Observe(uint actionID, bool isCastStart)
    {
        if (_timeline == null || ReplayTimelineExtractor.GenericActionIDs.Contains(actionID))
            return;

        var now = _ws.CurrentTime;
        _history.Add((now, actionID, isCastStart));
        if (_history.Count > HistorySize)
            _history.RemoveAt(0);
        var history = _history.Select(h => ((float)(now - h.At).TotalSeconds, h.ActionID, h.IsCastStart)).ToList();
        CheckDivergence(actionID, isCastStart);

        // Place the clock by alignment rather than by a single landmark: for every timeline entry the newest action could be, check how
        // many of the recently observed actions also land on an entry with that offset. A single shared id (an autoattack, an add that
        // also appears later) agrees with nothing, so it cannot drag the clock into the wrong phase on its own.
        // Every ranked candidate for the zone competes; the store's order breaks ties, so a better-ranked import keeps the clock.
        var wanted = isCastStart ? ExternalPlannerTimeline.ExternalStateKind.CastStart : ExternalPlannerTimeline.ExternalStateKind.AbilityUsed;
        ExternalPlannerTimeline.TimelineDefinition? bestTimeline = null;
        ExternalPlannerTimeline.TimelineSequence? bestSequence = null;
        ExternalPlannerTimeline.TimelineState? bestState = null;
        var bestTime = 0f;
        var bestScore = 0;
        var bestDrift = float.MaxValue;
        var currentTime = TimelineTime;
        // Every entry of an exhausted sequence lies behind the clock, so none of them may take it back; the fight may still move on to
        // another sequence (the next phase or boss) from there.
        RefreshExhausted();
        foreach (var timeline in CandidateTimelines())
        {
            foreach (var sequence in timeline.Sequences)
            {
                if (sequence == _exhaustedSequence)
                    continue;
                foreach (var state in sequence.States)
                {
                    if (state.Kind != wanted || !state.IDs.Contains(actionID))
                        continue;

                    var score = AlignmentScore(sequence, state.Time, history, UseAbilitySync);
                    var drift = _sequence == sequence && !float.IsNaN(currentTime) ? Math.Abs(state.Time - currentTime) : float.MaxValue;
                    if (score > bestScore || score == bestScore && drift < bestDrift)
                    {
                        bestTimeline = timeline;
                        bestSequence = sequence;
                        bestState = state;
                        bestTime = state.Time;
                        bestScore = score;
                        bestDrift = drift;
                    }
                }
            }
        }

        // Keeping a running clock aligned only needs the entry to be where we already are; taking a new clock needs corroboration,
        // and a confirmed clock needs more of it before it jumps far backwards.
        if (bestSequence == null || bestScore < RequiredScore(_confirmed, _sequence == bestSequence, currentTime, bestTime, bestDrift, SyncTolerance, MaxBackwardJump))
            return;

        // The fight moving from one sibling of a branch to the other (from the one followed while undecided, or from the one decided)
        // at or after the decision point is the fight telling which way it went: that sibling is the decision from here on. Before the
        // decision point the siblings describe the same fight, and an entry only one of them kept (each is built from its own pulls by
        // majority) says nothing about the branch; the clock just follows it there and UpdateBranch puts it back on a decided sibling.
        // A decision already made only turns to the early sibling on one of its divergence entries: any other entry of it can be what
        // most of its own pulls happened to keep, and would overturn a forecast that had cleared its margin. (Turning to the late
        // sibling is not restricted.)
        ExternalPlannerTimeline.TimelineSequence? from = null;
        var flipNeedsDivergence = false;
        if (bestSequence.Branch is { } branch && bestTime >= branch.DecisionTime - BranchPointTolerance)
        {
            var decided = BranchDecision(branch);
            from = decided ?? _sequence;
            flipNeedsDivergence = decided != null && branch.Below;
        }
        _timeline = bestTimeline;
        Anchor(bestSequence, bestTime);
        Confirm();
        if (from != null && !ReferenceEquals(from, bestSequence) && from.Branch?.Group == bestSequence.Branch!.Group && Contains(bestTimeline!, from)
            && (!flipNeedsDivergence || IsDivergenceEntry(bestTimeline!, bestSequence, bestState!)))
            _branchDecided = bestSequence;
        // An alignment that lands exactly on a divergence entry of the early sibling is that entry observed, even when the clock was
        // not trusted enough for CheckDivergence (unconfirmed, or placed elsewhere): the clock now sits on it. (The late sibling cannot
        // show the id within SyncTolerance of it: CollectDivergenceStates already excludes that.)
        if (bestSequence.Branch is { } placed && BranchDecision(placed) == null && IsDivergenceEntry(_timeline!, bestSequence, bestState!))
            DecideFor(bestSequence);
    }

    // Whether `state` is a divergence entry of `sequence`, which must then be the early sibling of its branch group in the timeline.
    private bool IsDivergenceEntry(ExternalPlannerTimeline.TimelineDefinition timeline, ExternalPlannerTimeline.TimelineSequence sequence, ExternalPlannerTimeline.TimelineState state)
        => sequence.Branch is { } branch && Siblings(timeline, branch.Group) is ({ } early, { } late) && ReferenceEquals(early, sequence)
            && ContainsState(DivergenceStatesFor(early, late), state);

    // A divergence entry of the early sibling observed where the clock expects it (within SyncTolerance of the decision point) decides
    // the branch for that sibling, whatever the HP forecast says (DecideBranch with divergenceCastSeen). The match is refused when the
    // late sibling shows the same id within SyncTolerance of the clock, since a late pull could be showing it then.
    private void CheckDivergence(uint actionID, bool isCastStart)
    {
        if (_sequence?.Branch is not { } branch || BranchDecision(branch) != null || !Synced || _timeline == null)
            return;
        if (Siblings(_timeline, branch.Group) is not ({ } early, { } late))
            return;
        var clock = TimelineTime;
        if (Math.Abs(clock - branch.DecisionTime) > SyncTolerance || LateShowsIdNear(late, actionID, clock, SyncTolerance))
            return;
        var wanted = isCastStart ? ExternalPlannerTimeline.ExternalStateKind.CastStart : ExternalPlannerTimeline.ExternalStateKind.AbilityUsed;
        foreach (var state in DivergenceStatesFor(early, late))
        {
            if (state.Kind == wanted && state.IDs.Contains(actionID))
            {
                DecideFor(early);
                return;
            }
        }
    }

    // How many distinct action ids an alignment needs before the clock takes it. Refining a running clock in place (same sequence,
    // drift within syncTolerance) needs one; a new place on the timeline needs MinAlignmentScore; a confirmed clock asked to move
    // more than maxBackwardJump backwards needs BackwardJumpScore, because a cast pair the source repeats always offers its earlier
    // copy too. Times are timeline seconds, so a candidate on another sequence that far behind the current clock is the same jump.
    public static int RequiredScore(bool confirmed, bool sameSequence, float currentTime, float candidateTime, float drift, float syncTolerance, float maxBackwardJump)
    {
        if (sameSequence && drift <= syncTolerance)
            return 1;
        if (confirmed && !float.IsNaN(currentTime) && currentTime - candidateTime > maxBackwardJump)
            return BackwardJumpScore;
        return MinAlignmentScore;
    }

    // Whether timelineTime lies more than tolerance past the last moment the sequence describes: its last state or the latest
    // window end, whichever is later. An open-ended window describes nothing past its start.
    public static bool IsExhausted(ExternalPlannerTimeline.TimelineSequence sequence, float timelineTime, float tolerance)
        => !float.IsNaN(timelineTime) && timelineTime > SequenceTail(sequence) + tolerance;

    // The last timeline moment the sequence describes: its start, its last state or the latest window end, whichever is later.
    public static float SequenceTail(ExternalPlannerTimeline.TimelineSequence sequence)
    {
        var last = sequence.StartTime;
        foreach (var state in sequence.States)
            last = Math.Max(last, state.Time);
        if (sequence.Windows is { } windows)
            foreach (var w in windows)
                if (w.End is { } end)
                    last = Math.Max(last, end);
        return last;
    }

    // A combat flag on an enemy starts a pull when nothing else was fighting yet, or when it is a boss the import names (adds may
    // already be in combat when their boss engages). Anything else is an enemy joining a fight that is already placed on the timeline.
    // The boss the synced clock already follows re-entering combat right after leaving it (a combat flag that flickers around a
    // vanish) continues the fight rather than starting one, so a confirmed clock is not thrown away for it (see IsContinuation).
    public static bool IsPullStart(bool actorInCombat, bool anyOtherEnemyAlreadyInCombat, bool actorIsListedBoss, bool continuationOfCurrentBoss)
        => actorInCombat && !continuationOfCurrentBoss && (!anyOtherEnemyAlreadyInCombat || actorIsListedBoss);

    // Whether a followed boss entering combat continues the fight the clock is placed in: the clock must be synced and not
    // exhausted, the boss must be one the current sequence names, and it must have left combat no more than gap seconds ago
    // (secondsSinceLeft is NaN when it never left, which is a flag that flickered and also continues). A longer gap is a wipe or a
    // reset followed by a re-pull, and that is a pull start even while the old clock is still synced.
    public static bool IsContinuation(bool synced, bool exhausted, bool listedInCurrent, float secondsSinceLeft, float gap)
        => synced && !exhausted && listedInCurrent && (float.IsNaN(secondsSinceLeft) || secondsSinceLeft <= gap);

    private IEnumerable<ExternalPlannerTimeline.TimelineDefinition> CandidateTimelines()
    {
        if (_timeline == null)
            return [];
        if (OnlySource != null)
            return [_timeline];
        var ranked = TimelineStore.CandidatesForZone(_timelineZone);
        return ranked.Contains(_timeline) ? ranked : [_timeline, .. ranked];
    }

    // How many distinct action ids among the recently observed actions fit the timeline if the newest one sits at anchorTime. History
    // entries are (seconds before the newest action, action id, whether it was a cast start rather than an effect). A single id that
    // repeats on a fixed period matches at every multiple of that period, so it corroborates nothing on its own and counts once.
    public static int AlignmentScore(ExternalPlannerTimeline.TimelineSequence sequence, float anchorTime, IReadOnlyList<(float SecondsAgo, uint ActionID, bool IsCastStart)> history, bool useAbilitySync)
    {
        HashSet<uint> matched = [];
        foreach (var (secondsAgo, actionID, isCastStart) in history)
        {
            if (!isCastStart && !useAbilitySync || ReplayTimelineExtractor.GenericActionIDs.Contains(actionID))
                continue;
            var expected = anchorTime - secondsAgo;
            if (expected < sequence.StartTime - AlignmentTolerance)
                continue;
            var wanted = isCastStart ? ExternalPlannerTimeline.ExternalStateKind.CastStart : ExternalPlannerTimeline.ExternalStateKind.AbilityUsed;
            foreach (var state in sequence.States)
            {
                if (state.Kind == wanted && state.IDs.Contains(actionID) && Math.Abs(state.Time - expected) <= AlignmentTolerance)
                {
                    matched.Add(actionID);
                    break;
                }
            }
        }
        return matched.Count;
    }

    // Imported timelines are written from the pull, so entering combat already places the clock. Actions only refine it from there.
    // The boss id picks the sequence when the import knows it; otherwise a fresh pull after a confirmed sequence most likely continues
    // with the next one (the next boss of a dungeon), and the first sequence is the fallback.
    // Each pull starts from the store's best-ranked import again: an action alignment during the previous pull may have handed the
    // clock to a lower-ranked candidate, and that choice should not outlive the pull it was made in. The last confirmed sequence
    // only means something on the timeline it belongs to. A real pull start (see IsPullStart) always replaces the clock and forgets
    // any exhausted sequence; an enemy joining a fight that is already placed, or the followed boss back in combat right after
    // leaving it (see IsContinuation), only replaces an expired clock.
    private void OnCombatChanged(Actor actor)
    {
        NoteAttackabilityChange(actor);
        if (_timeline == null || actor.Type != ActorType.Enemy || actor.IsAlly)
            return;
        var listedInCurrent = _sequence?.BossOIDs is { Count: > 0 } current && current.Contains(actor.OID);
        if (!actor.InCombat)
        {
            if (listedInCurrent)
                _bossLeftCombatAt = _ws.CurrentTime;
            return;
        }

        var anyOtherInCombat = false;
        foreach (var a in _ws.Actors)
        {
            if (a != actor && a.Type == ActorType.Enemy && !a.IsAlly && a.InCombat && !a.IsDeadOrDestroyed)
            {
                anyOtherInCombat = true;
                break;
            }
        }
        var listedBoss = _timeline.Sequences.Any(s => s.BossOIDs is { Count: > 0 } bosses && bosses.Contains(actor.OID));
        var secondsSinceLeft = _bossLeftCombatAt == default ? float.NaN : (float)(_ws.CurrentTime - _bossLeftCombatAt).TotalSeconds;
        var continuation = IsContinuation(Synced, RefreshExhausted(), listedInCurrent, secondsSinceLeft, ContinuationGap);
        if (IsPullStart(actor.InCombat, anyOtherInCombat, listedBoss, continuation))
        {
            _exhaustedSequence = null;
            _bossLeftCombatAt = default;
            ResetBranch();
        }
        else if (_sequence != null && Synced)
            return;

        var resolved = ResolveTimeline();
        if (resolved == null)
            return;
        if (!ReferenceEquals(resolved, _timeline))
        {
            _timeline = resolved;
            _lastConfirmed = null;
        }

        var candidates = _timeline.Sequences.Where(s => s.States.Count > 0 || s.Windows is { Count: > 0 }).ToList();
        var byOID = candidates.FirstOrDefault(s => s.BossOIDs is { Count: > 0 } && s.BossOIDs.Contains(actor.OID));
        var sequence = byOID ?? (_lastConfirmed is { } last && NextAfter(candidates, last) is { } next ? next : candidates.FirstOrDefault());
        if (sequence != null)
        {
            Anchor(sequence, sequence.StartTime);
            _confirmed = false;
        }
    }

    // The boss appearing or disappearing is the event this whole prediction is about, so it is also the strongest correction: it puts
    // the clock exactly on the matching entry and keeps later windows accurate even when the fight runs faster or slower than the import.
    private void OnTargetableChanged(Actor actor)
    {
        NoteAttackabilityChange(actor);
        // A death also flips targetability, but it is the end of the fight, not an edge on the timeline.
        if (_sequence == null || actor.Type != ActorType.Enemy || actor.IsAlly || actor.IsDeadOrDestroyed)
            return;
        // An exhausted sequence has no edge left worth snapping to; a late edge would only pull the clock back behind its tail.
        if (RefreshExhausted())
            return;

        var now = TimelineTime;
        if (float.IsNaN(now))
            return;

        // A listed boss follows its own windows; an unlisted enemy can only be matched against the moments nothing was attackable.
        if (_sequence.BossOIDs is { Count: > 0 } bosses && !bosses.Contains(actor.OID))
            return;
        if (MatchTargetableEdge(_sequence, now, actor.IsTargetable, TargetableSyncTolerance, MinWindowConfidence) is not { } time)
            return;

        Anchor(_sequence, time);
        Confirm();
    }

    // The timeline moment closest to `now` at which the tracked enemy becomes targetable (wantedTargetable) or untargetable, within
    // tolerance, or null. Sequences with measured windows use BossUntargetable edges when they name their bosses and NoTarget edges
    // otherwise; legacy sequences use the targetability markers. Windows under minConfidence are ignored.
    public static float? MatchTargetableEdge(ExternalPlannerTimeline.TimelineSequence sequence, float now, bool wantedTargetable, float tolerance, float minConfidence)
    {
        float? best = null;
        var bestDistance = tolerance;
        void Consider(float time)
        {
            var distance = Math.Abs(time - now);
            if (distance <= bestDistance)
            {
                best = time;
                bestDistance = distance;
            }
        }

        if (sequence.Windows is { Count: > 0 } windows)
        {
            var kind = sequence.BossOIDs is { Count: > 0 } ? ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable : ExternalPlannerTimeline.TimelineWindowKind.NoTarget;
            foreach (var w in windows)
            {
                if (w.Kind != kind || w.Confidence < minConfidence)
                    continue;
                if (!wantedTargetable)
                    Consider(w.Start);
                else if (w.End is { } end)
                    Consider(end);
            }
        }
        else
        {
            var wanted = wantedTargetable ? ExternalPlannerTimeline.ExternalStateKind.Targetable : ExternalPlannerTimeline.ExternalStateKind.Untargetable;
            foreach (var state in sequence.States)
                if (state.Kind == wanted)
                    Consider(state.Time);
        }
        return best;
    }

    // Something to attack running out, or coming back, is the edge of a NoTarget window even where no boss changes its targetability:
    // a wave fight empties when its last add dies, however fast that was. Per frame, a change of AnyAttackable that has lasted
    // EdgeSettle (a kill between two adds of a wave empties the field for a moment) puts the clock on the matching window edge, back
    // at the moment the change happened. Nothing is tracked without a sequence, on an exhausted one or out of combat (no tracked enemy).
    private void TrackAttackability(bool trackedEnemy, bool anyAttackable)
    {
        var now = _ws.CurrentTime;
        var lastChecked = _attackabilityCheckedAt;
        _attackabilityCheckedAt = now;
        var clock = TimelineTime;
        if (_sequence == null || !trackedEnemy || float.IsNaN(clock) || RefreshExhausted())
        {
            _attackabilityKnown = false;
            _attackabilityFlipPending = false; // (_targetsUpInDowntime belongs to the caller's frame)
            return;
        }
        if (!_attackabilityKnown)
        {
            _attackabilityKnown = true;
            _attackable = anyAttackable;
            _attackabilityFlipPending = false;
            return;
        }
        if (anyAttackable == _attackable)
        {
            _attackabilityFlipPending = false; // back before it settled: a blip, not an edge
            return;
        }
        if (!_attackabilityFlipPending)
        {
            // The world event that caused the change dates it; the update only sees it afterwards (a world change after the last
            // update of a frame is only seen on the next frame).
            _attackabilityFlipPending = true;
            _attackabilityFlipAt = _attackabilityChangedAt >= lastChecked && _attackabilityChangedAt <= now ? _attackabilityChangedAt : now;
        }
        var sinceFlip = (float)(now - _attackabilityFlipAt).TotalSeconds;
        if (sinceFlip < EdgeSettle)
            return;

        _attackable = anyAttackable;
        _attackabilityFlipPending = false;
        if (MatchAttackabilityEdge(_sequence, clock - sinceFlip, anyAttackable, SyncTolerance, MinWindowConfidence, WindowStartLimit()) is not { } edge)
            return;
        Anchor(_sequence, edge, _attackabilityFlipAt);
        Confirm();
    }

    // Tracking starts over from the next observation (see TrackAttackability): no settled value, no change waiting to settle.
    private void ResetAttackability()
    {
        _attackabilityKnown = false;
        _attackabilityFlipPending = false;
        _targetsUpInDowntime = false;
    }

    // Any enemy entering or leaving combat, changing targetability, dying or going away may change AnyAttackable; the time of the
    // latest such event dates a change the next update sees.
    private void NoteAttackabilityChange(Actor actor)
    {
        if (actor.Type == ActorType.Enemy && !actor.IsAlly)
            _attackabilityChangedAt = _ws.CurrentTime;
    }

    // The NoTarget window edge an attackability change at clockAtEdge is: the start of a window not yet over when nothing is
    // attackable any more (attackable false), the end of one already entered when something is again, the nearest within tolerance,
    // or null. Only windows of at least minConfidence starting before windowStartLimit count (an undecided branch: see NextDowntime).
    // NoTarget windows are used whether or not the sequence names its bosses, since this is the whole fight emptying, not a boss
    // going away. Legacy sequences (no windows) have none.
    public static float? MatchAttackabilityEdge(ExternalPlannerTimeline.TimelineSequence sequence, float clockAtEdge, bool attackable, float tolerance, float minConfidence, float windowStartLimit)
    {
        if (sequence.Windows is not { Count: > 0 } windows)
            return null;
        float? best = null;
        var bestDistance = tolerance;
        foreach (var w in windows)
        {
            if (w.Kind != ExternalPlannerTimeline.TimelineWindowKind.NoTarget || w.Confidence < minConfidence || w.Start >= windowStartLimit)
                continue;
            // A loss is never the start of a window the clock has already passed, and a return never the end of one it has not
            // entered yet: those windows are not the downtime the change is about.
            float edge;
            if (!attackable)
            {
                if (w.End is { } passedEnd && passedEnd <= clockAtEdge)
                    continue;
                edge = w.Start;
            }
            else if (w.End is { } end && w.Start < clockAtEdge)
            {
                edge = end;
            }
            else
            {
                continue;
            }
            var distance = Math.Abs(edge - clockAtEdge);
            if (distance <= bestDistance)
            {
                best = edge;
                bestDistance = distance;
            }
        }
        return best;
    }

    // Whether a disagreement between the timeline and the world at `now` is a window edge that has not come yet rather than a
    // different fight: something attackable inside a NoTarget window that started no more than tolerance ago (the start is late), or
    // nothing attackable after one that ended no more than tolerance ago (the end is late). The edge itself resyncs the clock when it
    // comes (TrackAttackability). Only windows of at least minConfidence count; legacy sequences (no windows) have none.
    public static bool IsLateWindowEdge(ExternalPlannerTimeline.TimelineSequence sequence, float now, bool anyAttackable, float tolerance, float minConfidence)
    {
        if (sequence.Windows is not { Count: > 0 } windows)
            return false;
        foreach (var w in windows)
        {
            if (w.Kind != ExternalPlannerTimeline.TimelineWindowKind.NoTarget || w.Confidence < minConfidence)
                continue;
            if (anyAttackable ? w.Start <= now && (w.End is not { } end || now < end) && now - w.Start <= tolerance
                : w.End is { } wEnd && wEnd <= now && now - wEnd <= tolerance)
                return true;
        }
        return false;
    }

    // Whether the clock moving from `from` onto `to` keeps its time base: the same sequence, or the other sibling of its HP-gated
    // branch in `timeline` (siblings share everything before the decision point, their clock included).
    public static bool SharesTimeBase(ExternalPlannerTimeline.TimelineDefinition? timeline, ExternalPlannerTimeline.TimelineSequence? from, ExternalPlannerTimeline.TimelineSequence to)
        => ReferenceEquals(from, to) || from?.Branch is { } a && to.Branch is { } b && a.Group == b.Group && timeline != null && Contains(timeline, from) && Contains(timeline, to);

    private void Anchor(ExternalPlannerTimeline.TimelineSequence sequence, float timelineTime) => Anchor(sequence, timelineTime, _ws.CurrentTime);

    // Places the clock so that it read timelineTime at `at` (the present unless an edge is dated back to when it happened).
    // An attackability change seen on another time base is not an edge of this one, so tracking starts over: dropping only a
    // pending change would see the same difference again on the next frame and date it to the event that moved the clock.
    private void Anchor(ExternalPlannerTimeline.TimelineSequence sequence, float timelineTime, DateTime at)
    {
        if (!SharesTimeBase(_timeline, _sequence, sequence))
            ResetAttackability();
        _sequence = sequence;
        _sequenceTail = SequenceTail(sequence);
        _anchorAt = at;
        _anchorTime = timelineTime;
        _syncedAt = _ws.CurrentTime;
        _contradictedSince = default;
    }

    private void Confirm()
    {
        _confirmed = true;
        _lastConfirmed = _sequence;
    }

    // The clock is only worth trusting while the fight still looks like the timeline. Comparing the predicted availability of a target
    // with what the client reports is cheap and catches the usual divergences: a different boss in the zone, a skipped phase, a kill
    // that outruns the script. Adds keep a target available while the boss is away, so the comparison is "anything attackable", not
    // "the boss is targetable".
    // An attackability edge settled this frame (TrackAttackability) resyncs the clock before the comparison. On a confirmed clock, a
    // window edge that is only late (IsLateWindowEdge) is not a contradiction until it is later than SyncTolerance. Whenever the
    // clock is inside a NoTarget window but something is attackable (a late start, an early end waiting to settle, or a clock about
    // to be dropped), nothing is published (_targetsUpInDowntime): announcing a downtime that is due or still running would make
    // rotations hold with targets up.
    private void CheckAgainstWorld()
    {
        _targetsUpInDowntime = false;
        var boss = _sequence == null ? null : FindTrackedEnemy(false);
        var anyAttackable = boss != null && AnyAttackable();
        TrackAttackability(boss != null, anyAttackable);
        if (_sequence == null)
            return;

        if (boss == null)
        {
            _contradictedSince = default;
            return;
        }

        var clock = TimelineTime;
        var predictedAttackable = !PredictedNoTargetAt(_sequence, clock, MinWindowConfidence);
        if (predictedAttackable == anyAttackable)
        {
            _contradictedSince = default;
            if (!predictedAttackable) // a downtime that actually happened where the timeline said it would
                Confirm();
            return;
        }

        _targetsUpInDowntime = anyAttackable; // the prediction disagrees, so something attackable means the clock is in a window

        // Only a clock this pull has confirmed may wait for a late edge: one merely placed by a combat start may sit long before the
        // real pull, and its window never coming is exactly how it is found out.
        if (_confirmed && IsLateWindowEdge(_sequence, clock, anyAttackable, SyncTolerance, MinWindowConfidence))
        {
            _contradictedSince = default;
            return;
        }

        if (_contradictedSince == default)
            _contradictedSince = _ws.CurrentTime;
        else if ((_ws.CurrentTime - _contradictedSince).TotalSeconds > ContradictionGrace)
            _sequence = null; // the fight no longer follows the import; a new action alignment may place us again
    }

    // The boss the sequence names when it is present (the one with the most max HP when several are, which is the boss the extractor
    // learns branch thresholds on), otherwise the biggest enemy in the fight. With listedOnly, a sequence that names its bosses gets
    // one of them or nothing. The actor dictionary is walked directly: ActorState's own enumerator is an interface and allocates.
    private Actor? FindTrackedEnemy(bool listedOnly)
    {
        var bosses = _sequence?.BossOIDs is { Count: > 0 } listedBosses ? listedBosses : null;
        Actor? best = null;
        var bestListed = false;
        foreach (var actor in _ws.Actors.Actors.Values)
        {
            if (actor.Type != ActorType.Enemy || actor.IsAlly || !actor.InCombat || actor.IsDeadOrDestroyed)
                continue;
            var listed = bosses != null && bosses.Contains(actor.OID);
            if (listedOnly && bosses != null && !listed)
                continue;
            if (best == null || listed && !bestListed || listed == bestListed && actor.HPMP.MaxHP > best.HPMP.MaxHP)
            {
                best = actor;
                bestListed = listed;
            }
        }
        return best;
    }

    // Whether any enemy in the fight can be attacked: what CheckAgainstWorld compares with the timeline.
    private bool AnyAttackable()
    {
        foreach (var a in _ws.Actors.Actors.Values)
            if (a.Type == ActorType.Enemy && !a.IsAlly && a.InCombat && !a.IsDeadOrDestroyed && a.IsTargetable)
                return true;
        return false;
    }

    // HP-gated branches. The followed sequence may be one of two siblings (TimelineSequence.Branch) that share everything before the
    // decision point. Until the pull decides which one it is, the clock follows either and only the shared part is published; the
    // decision then moves the running clock onto the chosen sibling.
    private void ResetBranch()
    {
        _branchDecided = null;
        ResetHpSamples(0);
    }

    private void ResetHpSamples(ulong actor)
    {
        _hpSampleCount = 0;
        _hpSampleNext = 0;
        _hpSampleActor = actor;
        _hpSlope = float.NaN;
    }

    // Per frame: keeps the clock on the decided sibling (a placement by boss id may have put it on the other one), and while
    // undecided asks DecideBranch whether the early vanish (a branch without a divergence entry), the HP forecast or the cast grace
    // settles it. The boss HP is only read where a forecast can come of it: with a threshold, from HpSlopeWindow before the
    // extrapolation starts until the grace ends.
    private void UpdateBranch()
    {
        if (_sequence?.Branch is not { } branch || _timeline == null)
            return;
        if (BranchDecision(branch) is { } decided)
        {
            if (!ReferenceEquals(decided, _sequence))
                SwitchSibling(decided);
            return;
        }
        if (!Synced || Siblings(_timeline, branch.Group) is not ({ } early, { } late))
            return;
        var now = TimelineTime;
        // Without an entry of its own at the decision point, the early sibling shows itself by going away: nothing attackable where it
        // predicts nothing to attack (the test CheckAgainstWorld makes) is its vanish. Only its own window counts, the one starting
        // at the decision point: a window before it is shared with the late sibling and says nothing about the branch.
        var vanished = DivergenceStatesFor(early, late).Count == 0 && now >= _divergenceWindowStart
            && PredictedNoTargetAt(early, now, MinWindowConfidence) && FindTrackedEnemy(false) != null && !AnyAttackable();
        var hp = float.NaN;
        if (branch.HpThreshold != null && branch.DecisionTime - now <= BranchMaxExtrapolation + HpSlopeWindow && now <= branch.DecisionTime + BranchCastGrace)
            hp = SampleBossHP();
        var decision = DecideBranch(now, branch.DecisionTime, branch.HpThreshold, hp, _hpSlope, vanished, BranchMaxExtrapolation, BranchForecastMargin, BranchCastGrace);
        if (decision >= 0)
            DecideFor(decision == 0 ? early : late);
    }

    // The sibling this pull's branch was decided for, when that decision belongs to this branch group of the current timeline.
    private ExternalPlannerTimeline.TimelineSequence? BranchDecision(ExternalPlannerTimeline.TimelineBranch branch)
        => _branchDecided?.Branch?.Group == branch.Group && _timeline != null && Contains(_timeline, _branchDecided!) ? _branchDecided : null;

    private void DecideFor(ExternalPlannerTimeline.TimelineSequence sibling)
    {
        _branchDecided = sibling;
        if (!ReferenceEquals(_sequence, sibling))
            SwitchSibling(sibling);
    }

    // The clock moves onto the sibling as it runs: its time, anchor and confirmation stay, only the sequence (and its tail) changes.
    private void SwitchSibling(ExternalPlannerTimeline.TimelineSequence sibling)
    {
        _sequence = sibling;
        _sequenceTail = SequenceTail(sibling);
    }

    // The early (Below) and late siblings of a branch group; either is null when the timeline lacks it (then nothing is decided).
    private static (ExternalPlannerTimeline.TimelineSequence? Early, ExternalPlannerTimeline.TimelineSequence? Late) Siblings(ExternalPlannerTimeline.TimelineDefinition timeline, int group)
    {
        ExternalPlannerTimeline.TimelineSequence? early = null;
        ExternalPlannerTimeline.TimelineSequence? late = null;
        foreach (var sequence in timeline.Sequences)
        {
            if (sequence.Branch is not { } b || b.Group != group)
                continue;
            if (b.Below)
                early ??= sequence;
            else
                late ??= sequence;
        }
        return (early, late);
    }

    // The divergence entries of these siblings, collected once per pair of siblings (the lists they come from never change), with the
    // early window start (_divergenceWindowStart). Both are taken at the early sibling's own DecisionTime, which the pair (an
    // immutable early sibling) fixes, so the cache cannot mix entries of one decision point with the window of another.
    private List<ExternalPlannerTimeline.TimelineState> DivergenceStatesFor(ExternalPlannerTimeline.TimelineSequence early, ExternalPlannerTimeline.TimelineSequence late)
    {
        if (!ReferenceEquals(early, _divergenceEarly) || !ReferenceEquals(late, _divergenceLate))
        {
            var decisionTime = early.Branch!.DecisionTime;
            CollectDivergenceStates(early, late, decisionTime, _divergenceStates);
            _divergenceWindowStart = EarlyWindowStart(early, decisionTime);
            _divergenceEarly = early;
            _divergenceLate = late;
        }
        return _divergenceStates;
    }

    // Where the early sibling goes away: the start of its first NoTarget window at least MinPublishedLoss long that starts at the
    // decision point or later (the extractor never puts DecisionTime after it; BranchWindowEpsilon absorbs its rounding), or
    // float.MaxValue when it has none.
    public static float EarlyWindowStart(ExternalPlannerTimeline.TimelineSequence early, float decisionTime)
    {
        if (early.Windows is { } windows)
            foreach (var w in windows)
                if (w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget && w.Start >= decisionTime - BranchWindowEpsilon && w.End is { } end && end - w.Start >= MinPublishedLoss)
                    return w.Start;
        return float.MaxValue;
    }

    // The early sibling's divergence entries, as the extractor defines DecisionTime (ReplayTimelineExtractor.DivergenceTime): its
    // observable (cast or ability) entries at the decision point none of whose ids the late sibling shows at any time up to the early
    // window start plus SyncTolerance. Any of them seen decides the early sibling; an entry the late sibling shares decides nothing,
    // since a late pull would show it too while the branch is still open. Empty when the decision point was capped at the early
    // window start without an entry of its own there.
    public static void CollectDivergenceStates(ExternalPlannerTimeline.TimelineSequence early, ExternalPlannerTimeline.TimelineSequence late, float decisionTime, List<ExternalPlannerTimeline.TimelineState> result)
    {
        result.Clear();
        var exclusionEnd = EarlyWindowStart(early, decisionTime);
        exclusionEnd = exclusionEnd == float.MaxValue ? float.MaxValue : exclusionEnd + SyncTolerance;
        foreach (var state in early.States)
        {
            if (Math.Abs(state.Time - decisionTime) > BranchPointTolerance || state.Kind is not (ExternalPlannerTimeline.ExternalStateKind.CastStart or ExternalPlannerTimeline.ExternalStateKind.AbilityUsed))
                continue;
            var shared = false;
            foreach (var other in late.States)
            {
                if (other.Time <= exclusionEnd && other.IDs.Exists(state.IDs.Contains))
                {
                    shared = true;
                    break;
                }
            }
            if (!shared)
                result.Add(state);
        }
    }

    // Whether the late sibling has actionID at an entry within tolerance of the clock.
    public static bool LateShowsIdNear(ExternalPlannerTimeline.TimelineSequence late, uint actionID, float clock, float tolerance)
    {
        foreach (var state in late.States)
            if (Math.Abs(state.Time - clock) <= tolerance && state.IDs.Contains(actionID))
                return true;
        return false;
    }

    private static bool ContainsState(List<ExternalPlannerTimeline.TimelineState> states, ExternalPlannerTimeline.TimelineState state)
    {
        foreach (var s in states)
            if (ReferenceEquals(s, state))
                return true;
        return false;
    }

    private static bool Contains(ExternalPlannerTimeline.TimelineDefinition timeline, ExternalPlannerTimeline.TimelineSequence sequence)
    {
        foreach (var s in timeline.Sequences)
            if (ReferenceEquals(s, sequence))
                return true;
        return false;
    }

    // The candidate after `last`, skipping the other siblings of its branch (they are the same boss), or null past the end.
    private static ExternalPlannerTimeline.TimelineSequence? NextAfter(List<ExternalPlannerTimeline.TimelineSequence> candidates, ExternalPlannerTimeline.TimelineSequence last)
    {
        var i = candidates.IndexOf(last);
        if (i < 0)
            return null;
        var next = i + 1;
        while (next < candidates.Count && last.Branch is { } branch && candidates[next].Branch?.Group == branch.Group)
            ++next;
        return next < candidates.Count ? candidates[next] : null;
    }

    // The HP% of the boss the threshold was learned on, now: a listed boss when the sequence names its bosses (an add or another
    // enemy never stands in for it), NaN when there is none. Every HpSampleInterval it also goes into the ring buffer and the slope
    // is fitted again; a different actor starts the buffer over.
    private float SampleBossHP()
    {
        var boss = FindTrackedEnemy(true);
        if (boss == null || boss.HPMP.MaxHP == 0)
            return float.NaN;
        var percent = 100f * boss.HPMP.CurHP / boss.HPMP.MaxHP;
        var now = _ws.CurrentTime;
        if (boss.InstanceID != _hpSampleActor)
            ResetHpSamples(boss.InstanceID);
        var newest = (_hpSampleNext + HpSampleCapacity - 1) % HpSampleCapacity;
        if (_hpSampleCount == 0 || (now - _hpSampleAt[newest]).TotalSeconds >= HpSampleInterval)
        {
            _hpSampleAt[_hpSampleNext] = now;
            _hpSamplePercent[_hpSampleNext] = percent;
            _hpSampleNext = (_hpSampleNext + 1) % HpSampleCapacity;
            _hpSampleCount = Math.Min(_hpSampleCount + 1, HpSampleCapacity);
            _hpSlope = HpSlope(now);
        }
        return percent;
    }

    // Least-squares slope, in percent per second, of the HP samples from the last HpSlopeWindow seconds before `now`; NaN when they
    // cover less than MinHpSlopeSpan.
    private float HpSlope(DateTime now)
    {
        var n = 0;
        double sumT = 0, sumP = 0, sumTT = 0, sumTP = 0, oldest = 0;
        for (var k = 0; k < _hpSampleCount; ++k)
        {
            var slot = (_hpSampleNext + HpSampleCapacity - 1 - k) % HpSampleCapacity; // newest first
            var t = -(now - _hpSampleAt[slot]).TotalSeconds;
            if (t < -HpSlopeWindow)
                break;
            double p = _hpSamplePercent[slot];
            ++n;
            sumT += t;
            sumP += p;
            sumTT += t * t;
            sumTP += t * p;
            oldest = Math.Min(oldest, t);
        }
        if (n < 2 || -oldest < MinHpSlopeSpan)
            return float.NaN;
        var denominator = n * sumTT - sumT * sumT;
        return denominator > 0 ? (float)((n * sumTP - sumT * sumP) / denominator) : float.NaN;
    }

    // Whether the timeline says nothing is attackable at `now`: a NoTarget window (of at least minConfidence) when the sequence has
    // measured windows, the targetability markers otherwise. Plain loops: this runs every frame while synced.
    public static bool PredictedNoTargetAt(ExternalPlannerTimeline.TimelineSequence sequence, float now, float minConfidence)
    {
        if (sequence.Windows is { Count: > 0 } windows)
        {
            foreach (var w in windows)
            {
                if (w.Kind != ExternalPlannerTimeline.TimelineWindowKind.NoTarget || w.Confidence < minConfidence)
                    continue;
                if (w.Start <= now && (w.End is not { } end || now < end))
                    return true;
            }
            return false;
        }

        var targetable = true;
        foreach (var state in sequence.States)
        {
            if (state.Time > now)
                break;
            if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Untargetable)
                targetable = false;
            else if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Targetable)
                targetable = true;
        }
        return !targetable;
    }

    // Next downtime on the followed timeline, as seconds from now; both values are float.MaxValue when nothing is predicted.
    // While the branch of the followed sequence is undecided only what the siblings share is known: nothing starting at or after
    // the decision point (less BranchWindowEpsilon, so a capped decision point also holds back the early window it was capped at)
    // is published. (A branch whose other sibling is missing from the timeline is never decided and not cut.)
    // Nothing either while the clock is inside a NoTarget window with something attackable (see CheckAgainstWorld): the targets are
    // up, and the windows behind it wait for the clock to be resynced on the real edge (or dropped).
    public (float LossIn, float ReturnIn) NextDowntime()
    {
        if (_sequence == null || !Synced || RefreshExhausted() || _targetsUpInDowntime)
            return (float.MaxValue, float.MaxValue);
        return SelectDowntime(_sequence, TimelineTime, Horizon, MinPublishedLoss, MinWindowConfidence, WindowStartLimit());
    }

    // Windows starting at or after this are not known yet: the decision point of an undecided branch (see NextDowntime).
    private float WindowStartLimit()
        => _sequence?.Branch is { } branch && BranchDecision(branch) == null && _timeline != null && Siblings(_timeline, branch.Group) is ({ }, { })
            ? branch.DecisionTime - BranchWindowEpsilon : float.MaxValue;

    // Which sibling of an HP-gated branch the fight takes: -1 undecided, 0 the early sibling (Below), 1 the late one. The early
    // sibling's own sync point seen at the decision point decides early; the decision point passing by castGrace without it decides
    // late. Before that, a known threshold lets the boss HP decide: within maxExtrapolation before the decision point, HP is
    // extrapolated linearly to it, and a forecast at least margin below or above the threshold decides. Once the decision point has
    // passed, the fight has taken its path and an early one shows itself (its cast, its vanish), so HP may only confirm the late
    // sibling: the current HP at least margin above the threshold, whatever the slope. A NaN HP (no boss) decides nothing, nor does
    // a NaN slope (too few samples) before the decision point.
    public static int DecideBranch(float now, float decisionTime, float? threshold, float hpNow, float hpSlopePerSec, bool divergenceCastSeen, float maxExtrapolation, float margin, float castGrace)
    {
        if (divergenceCastSeen)
            return 0;
        if (now > decisionTime + castGrace)
            return 1;
        if (threshold is { } t && decisionTime - now <= maxExtrapolation)
        {
            if (now > decisionTime)
                return hpNow >= t + margin ? 1 : -1;
            var forecast = hpNow + hpSlopePerSec * (decisionTime - now);
            if (forecast <= t - margin)
                return 0;
            if (forecast >= t + margin)
                return 1;
        }
        return -1;
    }

    // The first downtime that has not finished yet and is worth publishing: within the horizon, at least minLoss long, and (for measured
    // windows) confident enough. Short windows are skipped rather than allowed to hide a long one right behind them. Only NoTarget
    // windows count: a boss gap with adds present still has something to hit. Nothing starting at or after windowStartLimit is
    // published (an undecided branch: see NextDowntime).
    public static (float LossIn, float ReturnIn) SelectDowntime(ExternalPlannerTimeline.TimelineSequence sequence, float now, float horizon, float minLoss, float minConfidence, float windowStartLimit = float.MaxValue)
    {
        var end = sequence.PredictionEndTime ?? float.MaxValue;
        if (sequence.Windows is { Count: > 0 } windows)
        {
            // Windows are stored ordered by Start (the extractor sorts them and the user files are written from it), so the first
            // window past the prediction limit, the horizon or the start limit ends the search.
            foreach (var w in windows)
            {
                if (w.Start > end || w.Start - now > horizon || w.Start >= windowStartLimit)
                    break;
                if (w.Kind != ExternalPlannerTimeline.TimelineWindowKind.NoTarget || w.End is not { } wEnd || wEnd <= now || w.Confidence < minConfidence || wEnd - w.Start < minLoss)
                    continue;
                return (Math.Max(0f, w.Start - now), wEnd - now);
            }
            return (float.MaxValue, float.MaxValue);
        }

        // Legacy sequences carry no windows: read the targetability markers instead.
        var lossAt = float.MaxValue;
        var targetable = true;
        foreach (var state in sequence.States)
        {
            if (state.Time > end)
                break;
            if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Untargetable && targetable)
            {
                targetable = false;
                lossAt = state.Time;
            }
            else if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Targetable && !targetable)
            {
                targetable = true;
                if (state.Time > now && state.Time - lossAt >= minLoss)
                {
                    if (lossAt - now > horizon || lossAt >= windowStartLimit)
                        return (float.MaxValue, float.MaxValue);
                    return (Math.Max(0f, lossAt - now), state.Time - now);
                }
                lossAt = float.MaxValue;
            }
        }

        // A downtime whose end is past the prediction limit has no known return time, and a rotation cannot plan around that.
        return (float.MaxValue, float.MaxValue);
    }

    // SelectDowntime has already kept only downtimes at least MinPublishedLoss long in full, so one that has started stays published
    // (loss 0, return at its end) until it ends, however little of it remains.
    private void Publish()
    {
        var (lossIn, returnIn) = NextDowntime();
        if (lossIn > Horizon)
        {
            // A clock that can vouch for the whole horizon says "no long loss coming" with an empty snapshot, so a rotation can tell it from
            // "no timeline here" (NIN's burst rules read it that way); anything less certain publishes nothing.
            if (NoLossWithinHorizon())
                Push(float.MaxValue, float.MaxValue);
            else
                Unpublish();
            return;
        }

        Push(lossIn, returnIn);
    }

    private void Push(float lossIn, float returnIn)
    {
        var zone = _ws.CurrentZone;
        var cfc = _ws.CurrentCFCID;
        _published |= ExternalMechanicHintProvider.PushSnapshot(new(HintNamespace, zone, cfc, ExternalZoneConfidence.TrustedSplatoonScript, _ws.FutureTime(0.5d),
            float.MaxValue, lossIn, returnIn, float.MaxValue, false), zone, cfc, _ws.CurrentTime);
    }

    // Whether the confirmed clock knows the next Horizon seconds hold no long loss: still inside what the sequence describes and predicts,
    // no undecided branch deciding within it, and no NoTarget window (however uncertain, or open-ended) or untargetable marker in it.
    private bool NoLossWithinHorizon()
    {
        if (_sequence == null || !Synced || RefreshExhausted() || _targetsUpInDowntime)
            return false;
        var now = TimelineTime;
        var until = now + Horizon;
        if (until > _sequenceTail || until > (_sequence.PredictionEndTime ?? float.MaxValue) || until >= WindowStartLimit())
            return false;
        if (_sequence.Windows is { Count: > 0 } windows)
        {
            foreach (var w in windows)
                if (w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget && w.Start <= until && (w.End is not { } end || end > now && end - w.Start >= MinPublishedLoss))
                    return false;
            return true;
        }
        foreach (var state in _sequence.States)
            if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Untargetable && state.Time > now && state.Time <= until)
                return false;
        return true;
    }

    private void Unpublish()
    {
        if (_published)
        {
            ExternalMechanicHintProvider.ClearNamespace(HintNamespace);
            _published = false;
        }
    }
}
