using System.Text.Json.Serialization;

namespace BossMod;

public static class ExternalPlannerTimeline
{
    private static bool Enabled => Service.Config?.Get<CustomConfig>()?.UseExternalPlannerTimelines == true;

    // Set on the thread that parses replays for automatic extraction: a timeline replacing a module's state machine during that
    // parse would feed the extraction's own output back into the pulls it learns from.
    [ThreadStatic] public static bool SuppressApply;

    public static StateMachine Apply(BossModule module, StateMachine original)
    {
        if (!Enabled || SuppressApply)
            return original;
        var selection = SelectTimeline(module.Info);
        if (selection == null || !IsTrivial(original))
            return original;

        var originalPhase = original.Phases[0];
        var replacement = new ExternalStateMachineBuilder(module, selection.Value.Timeline, selection.Value.Sequence, originalPhase.InitialState.Duration).Build();
        var replacementPhase = replacement.Phases[0];
        replacementPhase.Enter = originalPhase.Enter;
        replacementPhase.InitialState.Enter += originalPhase.InitialState.Enter;
        replacementPhase.Exit = originalPhase.InitialState.Exit + originalPhase.Exit;
        replacementPhase.Update = originalPhase.Update;
        replacementPhase.Hint = originalPhase.Hint;
        replacementPhase.ExpectedDuration = originalPhase.ExpectedDuration;
        return replacement;
    }

    // Resolve from the encounter metadata, including while editing a plan in a fake WorldState.
    // Neither the current territory nor the current player's level describes the edited duty.
    public static int PlanLevelFor(BossModuleRegistry.Info info)
    {
        if (!Enabled || SelectTimeline(info) == null)
            return 0;
        var duty = Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(info.GroupID);
        return duty?.ClassJobLevelSync ?? 0;
    }

    private static bool IsTrivial(StateMachine stateMachine)
        => stateMachine.Phases.Count == 1
            && stateMachine.Phases[0].InitialState is { NextStates: null, Update: null };

    private static (TimelineDefinition Timeline, TimelineSequence Sequence)? SelectTimeline(BossModuleRegistry.Info? info)
    {
        if (info is not { GroupType: BossModuleInfo.GroupType.CFC, ActionIDType: { IsEnum: true } actionIDType } || Service.LuminaGameData == null)
            return null;
        var zoneID = Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(info.GroupID)?.TerritoryType.RowId ?? 0;
        if (zoneID is 0 or > ushort.MaxValue || TimelineStore.ForZone((ushort)zoneID) is not { } timeline || !timeline.AutomaticFallback)
            return null;

        var sequences = timeline.Sequences.Where(sequence => sequence.States.Count > 0).ToArray();
        var moduleActions = Enum.GetValues(actionIDType).Cast<object>().Select(Convert.ToUInt32).ToHashSet();
        // Shared autoattacks/helper IDs cannot identify a boss. A single matching section is
        // required; multi-phase/branch sections must keep their native state machine.
        var idUseCounts = sequences.SelectMany(sequence => ActionIDs(sequence).Distinct())
            .GroupBy(id => id).ToDictionary(group => group.Key, group => group.Count());
        var candidates = sequences.Where(sequence => ActionIDs(sequence).Any(id => moduleActions.Contains(id) && idUseCounts[id] == 1)).ToArray();
        return candidates.Length == 1 && candidates[0].States[0].Time - candidates[0].StartTime <= 120f && PredictableStateCount(candidates[0]) > 0
            ? (timeline, candidates[0]) : null;
    }

    private static IEnumerable<uint> ActionIDs(TimelineSequence sequence)
        => sequence.States.Where(state => state.Kind is ExternalStateKind.CastStart or ExternalStateKind.Timeout or ExternalStateKind.AbilityUsed).SelectMany(state => state.IDs);

    private static int PredictableStateCount(TimelineSequence sequence)
    {
        var count = 0;
        var downtimeStart = -1;
        foreach (var state in sequence.States)
        {
            if (sequence.PredictionEndTime is { } end && state.Time > end)
                break;
            if (state.Kind == ExternalStateKind.Untargetable && downtimeStart < 0)
                downtimeStart = count;
            else if (state.Kind == ExternalStateKind.Targetable)
                downtimeStart = -1;
            ++count;
        }
        // An open downtime at the import boundary has no known return time. Leave
        // that entire interval unknown instead of advertising downtime until enrage.
        return downtimeStart >= 0 ? downtimeStart : count;
    }

    private sealed class ExternalStateMachineBuilder : StateMachineBuilder
    {
        private readonly TimelineDefinition _timeline;
        private readonly TimelineSequence _sequence;
        private readonly float _originalDuration;

        public ExternalStateMachineBuilder(BossModule module, TimelineDefinition timeline, TimelineSequence sequence, float originalDuration) : base(module)
        {
            _timeline = timeline;
            _sequence = sequence;
            _originalDuration = originalDuration;
            DeathPhase(0, BuildTimeline);
        }

        private void BuildTimeline(uint id)
        {
            // Retain the previous import's IDs after its first anchor so existing entries
            // keep referring to the same mechanics when the discarded opener is restored.
            var multiple = _timeline.Sequences.Count(sequence => sequence.States.Count > 0) > 1;
            var idUseCounts = _timeline.Sequences.SelectMany(sequence => ActionIDs(sequence).Distinct())
                .GroupBy(actionID => actionID).ToDictionary(group => group.Key, group => group.Count());
            var anchorIndex = _sequence.States.FindIndex(state => multiple
                ? state.Kind is ExternalStateKind.CastStart or ExternalStateKind.Timeout or ExternalStateKind.AbilityUsed && state.IDs.Any(actionID => idUseCounts[actionID] == 1)
                : state.Kind == ExternalStateKind.CastStart && state.IDs.Count > 0);
            var branchBase = multiple ? ((uint)_sequence.Index + 1) << 24 : 0u;
            var anchorIsCast = anchorIndex >= 0 && _sequence.States[anchorIndex].Kind == ExternalStateKind.CastStart;
            var previousTime = _sequence.StartTime;
            var predictableCount = PredictableStateCount(_sequence);
            for (var index = 0; index < predictableCount; ++index)
            {
                var definition = _sequence.States[index];
                var stateID = anchorIndex < 0 ? branchBase + (uint)index * 0x10
                    : index < anchorIndex ? 0xFE000000u + (uint)index * 0x10
                    : index == anchorIndex && anchorIsCast ? id
                    : branchBase + (uint)(index - anchorIndex - (multiple && anchorIsCast ? 1 : 0)) * 0x10;
                var delay = Math.Max(0f, definition.Time - previousTime);
                var state = definition.Kind switch
                {
                    ExternalStateKind.CastStart => CastSync(stateID, delay, definition),
                    ExternalStateKind.Targetable => Targetable(stateID, true, delay, definition.Name, Math.Max(0f, delay - 5f)),
                    ExternalStateKind.Untargetable => Targetable(stateID, false, delay, definition.Name, Math.Max(0f, delay - 5f)),
                    ExternalStateKind.AddedCombatant => Condition(stateID, delay, () => Module.WorldState.Actors.Any(actor => !actor.IsDeadOrDestroyed && definition.IDs.Contains(actor.OID)), definition.Name, 5f, Math.Max(0f, delay - 5f)),
                    _ => Timeout(stateID, delay, definition.Name)
                };
                state.SetHint(ConvertHint(definition.Hint));
                previousTime = definition.Time;
            }
            // The source ending (or looping) does not predict a kill. A successor also
            // makes the last event's EndHint observable by the planner and live state machine.
            SimpleState(0xFF000000, Math.Max(0f, _originalDuration - (previousTime - _sequence.StartTime)), "");
        }

        private State CastSync(uint id, float delay, TimelineState definition)
        {
            var enteredAt = default(DateTime);
            var state = Condition(id, delay, () => FindCast(definition.IDs) is { } cast
                && cast.ElapsedTime <= (Module.WorldState.CurrentTime - enteredAt).TotalSeconds + 0.1,
                definition.Name, 5f, Math.Max(0f, delay - 5f));
            state.OnEnter(() => enteredAt = Module.WorldState.CurrentTime);
            return state;
        }

        private ActorCastInfo? FindCast(IReadOnlyCollection<uint> actionIDs)
            => Module.PrimaryActor.CastInfo is { } primaryCast && primaryCast.IsSpell() && actionIDs.Contains(primaryCast.Action.ID)
                ? primaryCast
                : Module.WorldState.Actors.FirstOrDefault(actor => !actor.IsDeadOrDestroyed && actor.CastInfo is { } cast && cast.IsSpell() && actionIDs.Contains(cast.Action.ID))?.CastInfo;

        private static StateMachine.StateHint ConvertHint(int hint)
        {
            var result = StateMachine.StateHint.None;
            if ((hint & 1) != 0)
                result |= StateMachine.StateHint.Raidwide;
            if ((hint & 2) != 0)
                result |= StateMachine.StateHint.Tankbuster;
            if ((hint & 4) != 0)
                result |= StateMachine.StateHint.Knockback;
            return result;
        }
    }

    // Lookup for other consumers of the imported timelines, such as ExternalTimelineHints, which follows the timeline without
    // replacing the state machine of a module.
    public static TimelineDefinition? ForZone(ushort zoneID) => TimelineStore.ForZone(zoneID);

    public static IEnumerable<TimelineDefinition> AllTimelines() => TimelineStore.AllTimelines();

    public enum ExternalStateKind
    {
        Timeout,
        CastStart,
        Targetable,
        Untargetable,
        AddedCombatant,
        AbilityUsed // an Ability line: the effect landed, matched against Actors.CastEvent
    }

    public enum TimelineSource { EventTrigger, Cactbot, Replay, User, FFLogs, AutoReplay }

    public enum TimelineWindowKind
    {
        BossUntargetable, // the boss itself cannot be targeted
        NoTarget, // nothing attackable is left: what rotations read as downtime
        AddsPresent // the boss is gone but adds can be attacked
    }

    public sealed record TimelineWindow(TimelineWindowKind Kind, float Start, float? End, float Confidence);

    // Marks a sequence as one of two siblings of an HP-gated branch: the fight takes one path or the other depending on the boss HP
    // at DecisionTime. Siblings share Group (the index of the early sibling). The Below sibling applies when the primary boss HP% at
    // DecisionTime is at or under HpThreshold, the other one when it is over; a null threshold means HP did not separate the pulls.
    // DecisionTime is the early sibling's divergence entry (its own cast or ability there, which the late pulls do not show until
    // past the early window start plus the sync tolerance), or the early window start when it has none (a capped branch).
    // The follower (ExternalTimelineHints) decides a pull by, whichever comes first:
    // - the divergence cast observed near DecisionTime, or an alignment landing on it: early;
    // - the boss HP forecast from up to 8 s before DecisionTime, when a threshold exists: early or late by a 3 pt margin; after
    //   DecisionTime the HP may only confirm late;
    // - for a capped branch, the boss vanishing inside the early sibling's own window: early;
    // - DecisionTime passing by the 1 s grace without any of these: late.
    // An alignment moving the clock to the other sibling at or after DecisionTime also decides; once decided, only a divergence
    // entry may turn the decision to the early sibling. Before a decision, nothing starting at or after DecisionTime is published.
    public sealed record TimelineBranch(int Group, float DecisionTime, float? HpThreshold, bool Below);

    public sealed record TimelineDefinition(int ZoneID, string SourceFile, bool AutomaticFallback, List<TimelineSequence> Sequences, TimelineSource Source = TimelineSource.EventTrigger, float Confidence = 1f);
    // Branch is left out of the JSON when null, so timelines without branches serialize exactly as before it existed.
    public sealed record TimelineSequence(int Index, float StartTime, List<TimelineState> States, float? PredictionEndTime, List<uint>? BossOIDs = null, List<TimelineWindow>? Windows = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TimelineBranch? Branch = null);
    public sealed record TimelineState(float Time, string Name, ExternalStateKind Kind, List<uint> IDs, int Hint);
}
