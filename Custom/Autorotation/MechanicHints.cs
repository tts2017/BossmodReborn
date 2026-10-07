namespace BossMod.Autorotation;

// Which predicted mechanics a rotation may act on. Option 0 is the default for presets that never set the track.
public enum MechanicHintStrategy
{
    [Option("全部: タイムライン+移動予測+Splatoon/IPC")]
    All,
    [Option("タイムラインのみ: ボスモジュールのダウンタイム+取り込みタイムライン")]
    TimelineOnly,
    [Option("移動予測のみ: 射程外・強制移動")]
    ForecastOnly,
    [Option("使わない: 従来通り")]
    Off,
}

// Per-frame view of the predicted mechanics a rotation reads. Times are seconds from now; float.MaxValue means nothing is predicted.
// TargetLossIn/TargetReturnIn: the next long target loss (LongLossSeconds or more) over the enabled sources, earliest loss wins and
// keeps its own return. RangeLossIn/RangeReturnIn: hints.Disengage, short dodges included. State*: raw state machine transitions,
// unfiltered, for BLM's historical merge. Snapshot/Encounter: the provider snapshots as the older consumers (BLM, RPR, MNK) read them.
public readonly record struct MechanicForecast(
    MechanicHintStrategy Mode,
    float TargetLossIn,
    float TargetReturnIn,
    float ForcedMoveIn,
    float ForcedMoveFor,
    float RangeLossIn,
    float RangeReturnIn,
    float LeyLinesUnsafeIn,
    float StateLossIn,
    float StateReturnIn,
    float StateMoveIn,
    float StatePositioningIn,
    bool HasSnapshot,
    ExternalMechanicHintSnapshot Snapshot,
    bool HasEncounter,
    ExternalEncounterHintSnapshot Encounter)
{
    public const float LongLossSeconds = 8.5f;
    // How far ahead the imported-timeline follower vouches for its predictions: with a confirmed clock and no long loss within this, it
    // publishes an empty snapshot (ExternalTimelineHints.Publish). StateForecast gives the same assurance from a native state machine.
    public const float ForecastSeconds = 25f;

    // The boss module's own state machine can tell for ForecastSeconds whether a long loss comes: it is neither trivial nor rebuilt from
    // an import (the follower speaks for those), its active state is not overdue, and the states known from here reach that far without
    // a branch. Like a published snapshot, it turns "no loss predicted" into a statement rather than silence.
    public bool StateForecast { get; init; }

    public static readonly MechanicForecast None = new(MechanicHintStrategy.Off, float.MaxValue, float.MaxValue, float.MaxValue, 0f, float.MaxValue, float.MaxValue,
        float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue, false, default, false, default);

    public bool Enabled => Mode != MechanicHintStrategy.Off;
    public bool DowntimeNow => TargetLossIn <= 0f;
    public bool ReturnKnown => TargetReturnIn < float.MaxValue;
    public bool LossWithin(float seconds) => TargetLossIn <= seconds;
    public bool ForcedMoveWithin(float seconds) => ForcedMoveIn <= seconds;
    public bool RangeLossWithin(float seconds) => RangeLossIn <= seconds;

    // Hold a window only if the loss cuts more than one GCD of it, the return is known, and using it now would push the next use past the return.
    public bool ShouldHoldWindow(float windowLength, float cooldown, float gcd)
        => ReturnKnown && !DowntimeNow && TargetLossIn < windowLength - gcd && cooldown > TargetReturnIn;

    // An effect still up when the loss starts that runs out before the target returns: spend it before the loss.
    public bool ExpiresDuringLoss(float timeLeft)
        => timeLeft > 0f && !DowntimeNow && TargetLossIn < timeLeft && timeLeft <= TargetReturnIn;

    public static MechanicHintSources SourcesFor(MechanicHintStrategy mode) => mode switch
    {
        MechanicHintStrategy.All => MechanicHintSources.All,
        MechanicHintStrategy.TimelineOnly => MechanicHintSources.Timeline,
        MechanicHintStrategy.ForecastOnly => MechanicHintSources.Forecast,
        _ => MechanicHintSources.None
    };

    public static MechanicForecast Build(MechanicHintStrategy mode, WorldState ws, AIHints hints, BossModule? module)
    {
        if (mode == MechanicHintStrategy.Off)
            return None;

        var now = ws.CurrentTime;
        var lossIn = float.MaxValue;
        var returnIn = float.MaxValue;
        var moveIn = float.MaxValue;
        var moveFor = 0f;
        var rangeLossIn = float.MaxValue;
        var rangeReturnIn = float.MaxValue;
        var leyLinesUnsafeIn = float.MaxValue;
        var stateLossIn = float.MaxValue;
        var stateReturnIn = float.MaxValue;
        var stateMoveIn = float.MaxValue;
        var statePositioningIn = float.MaxValue;
        var stateForecast = false;

        if (mode is MechanicHintStrategy.All or MechanicHintStrategy.TimelineOnly && module?.StateMachine is { ActivePhase: not null } sm)
        {
            var downtimeStart = sm.NextTransitionWithFlag(StateMachine.StateHint.DowntimeStart);
            var downtimeEnd = sm.NextTransitionWithFlag(StateMachine.StateHint.DowntimeEnd);
            var positioningStart = sm.NextTransitionWithFlag(StateMachine.StateHint.PositioningStart);
            var knockback = sm.NextTransitionWithFlag(StateMachine.StateHint.Knockback);
            // the chain is anchored where the active state should have ended, so an overdue state drags every later transition into the
            // past, where it reads as happening now for as long as the state lasts (PlanExecution.OverdueGraceSeconds): past the grace,
            // nothing still pending is known - only a downtime already running (its end before any new start) keeps its late return.
            // The State* fields below are unfiltered by length (no 8.5 s rule) but do respect this overdue rule. A running downtime whose
            // planned end is itself already in the past has overrun its script, so its return is unknown too (otherwise SecondsUntil
            // would clamp it to 0 and the target would read as returning "now" for the whole overrun).
            var overdue = sm.ActiveState != null && sm.TimeSinceTransition > sm.ActiveState.Duration + PlanExecution.OverdueGraceSeconds;
            stateForecast = !overdue && !ExternalTimelineHints.ShouldRun(module) && KnownChainSeconds(sm) >= ForecastSeconds;
            if (overdue)
            {
                if (downtimeEnd >= downtimeStart)
                    downtimeEnd = DateTime.MaxValue;
                if (downtimeEnd <= now)
                    downtimeEnd = DateTime.MaxValue;
                downtimeStart = positioningStart = knockback = DateTime.MaxValue;
            }
            stateLossIn = SecondsUntil(downtimeStart, now);
            stateReturnIn = SecondsUntil(downtimeEnd, now);
            statePositioningIn = SecondsUntil(positioningStart, now);
            stateMoveIn = Math.Min(statePositioningIn, SecondsUntil(knockback, now));
            var knownLossIn = SecondsUntil(downtimeStart, now);
            var knownReturnIn = SecondsUntil(downtimeEnd, now);
            var knownPositioningIn = SecondsUntil(positioningStart, now);
            // a DowntimeEnd before the next DowntimeStart means the downtime is already running
            if (knownReturnIn < knownLossIn)
                OfferLoss(0f, knownReturnIn);
            else
                OfferLoss(knownLossIn, knownReturnIn);
            moveIn = Math.Min(moveIn, Math.Min(knownPositioningIn, SecondsUntil(knockback, now)));
            leyLinesUnsafeIn = Math.Min(leyLinesUnsafeIn, knownPositioningIn);
        }

        var hasSnapshot = ExternalMechanicHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, now, SourcesFor(mode), out var snapshot);
        if (hasSnapshot)
        {
            OfferLoss(snapshot.TargetLossIn, snapshot.TargetReturnIn);
            if (snapshot.ForcedMoveIn < moveIn)
            {
                moveIn = snapshot.ForcedMoveIn;
                moveFor = 0f;
            }
            leyLinesUnsafeIn = Math.Min(leyLinesUnsafeIn, snapshot.LeyLinesUnsafeIn);
        }

        if (mode is MechanicHintStrategy.All or MechanicHintStrategy.ForecastOnly)
        {
            var d = hints.Disengage;
            if (d.ForcedMoveIn < moveIn)
            {
                moveIn = d.ForcedMoveIn;
                moveFor = d.ForcedMoveFor;
            }
            rangeLossIn = d.TargetLossIn;
            rangeReturnIn = d.TargetReturnIn;
        }

        ExternalEncounterHintSnapshot encounter = default;
        var hasEncounter = mode == MechanicHintStrategy.All && ExternalEncounterHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, now, out encounter);

        return new(mode, lossIn, returnIn, moveIn, moveFor, rangeLossIn, rangeReturnIn, leyLinesUnsafeIn,
            stateLossIn, stateReturnIn, stateMoveIn, statePositioningIn, hasSnapshot, snapshot, hasEncounter, encounter) { StateForecast = stateForecast };

        void OfferLoss(float loss, float ret)
        {
            if (!(loss < float.MaxValue))
                return;
            // a loss that has not started yet must be long; a running one is reported with loss 0 and only its remaining time, so its
            // length is unknown here - keep it, otherwise the last 8.5 s of every downtime would lose their return time
            if (loss > 0f && ret < float.MaxValue && ret - loss < LongLossSeconds)
                return;
            loss = Math.Max(0f, loss);
            if (loss < lossIn)
            {
                lossIn = loss;
                returnIn = ret;
            }
        }
    }

    // seconds from now covered by the active state and its single-successor chain, stopping once past ForecastSeconds
    private static float KnownChainSeconds(StateMachine sm)
    {
        var state = sm.ActiveState;
        if (state == null)
            return 0f;
        var seconds = state.Duration - sm.TimeSinceTransition;
        while (seconds < ForecastSeconds && state.NextStates is { Length: 1 } next)
        {
            state = next[0];
            seconds += state.Duration;
        }
        return seconds;
    }

    private static float SecondsUntil(DateTime t, DateTime now) => t == DateTime.MaxValue ? float.MaxValue : Math.Max(0f, (float)(t - now).TotalSeconds);
}
