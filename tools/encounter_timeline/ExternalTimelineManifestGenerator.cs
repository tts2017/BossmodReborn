using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EncounterTimeline;

// These records mirror BossMod.Timeline.External.ExternalPlannerTimeline: the generator cannot reference the plugin, so the
// property names and the enum declaration order (serialized as integers) must stay identical on both sides.
public enum ExternalTimelineStateKind
{
    Timeout,
    CastStart,
    Targetable,
    Untargetable,
    AddedCombatant,
    AbilityUsed
}

public enum ExternalTimelineSource
{
    EventTrigger,
    Cactbot,
    Replay,
    User,
    FFLogs
}

public enum ExternalTimelineWindowKind
{
    BossUntargetable,
    NoTarget,
    AddsPresent
}

public sealed record ExternalTimelineManifest(IReadOnlyList<ExternalTimelineDefinition> Timelines);
public sealed record ExternalTimelineWindow(ExternalTimelineWindowKind Kind, float Start, float? End, float Confidence);
public sealed record ExternalTimelineDefinition(int ZoneID, string SourceFile, bool AutomaticFallback, IReadOnlyList<ExternalTimelineSequence> Sequences, ExternalTimelineSource Source = ExternalTimelineSource.EventTrigger, float Confidence = 1f);
public sealed record ExternalTimelineSequence(int Index, float StartTime, IReadOnlyList<ExternalTimelineState> States, float? PredictionEndTime = null, IReadOnlyList<uint>? BossOIDs = null, IReadOnlyList<ExternalTimelineWindow>? Windows = null);
public sealed record ExternalTimelineState(float Time, string Name, ExternalTimelineStateKind Kind, IReadOnlyList<uint> IDs, TimelineStateHint Hint);

public static class ExternalTimelineManifestGenerator
{
    private const float SequenceGap = 300f;
    private static readonly Regex AddsTitle = new(@"\badds?\b|\bspawn", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Event Trigger's files are mostly copies of cactbot's, so a cactbot entry is only worth carrying when it actually differs from the
    // Event Trigger entry of the same zone: an identical twin would only make the follower rank and scan the same data twice.
    public static ExternalTimelineManifest Generate(EventTriggerTimelineCatalog catalog, CactbotTriggerCatalog? cactbot)
    {
        List<ExternalTimelineDefinition> result = [];
        Dictionary<int, ExternalTimelineDefinition> eventTrigger = [];
        foreach (var mapping in catalog.TimelinesByZone.OrderBy(mapping => mapping.Key))
        {
            if (mapping.Key == 134 && mapping.Value.FileName == "test.txt")
                continue; // event-trigger's smoke-test file is mapped to an overworld zone
            var isCactbotFill = catalog.CactbotTimelinesByZone.TryGetValue(mapping.Key, out var cactbotTimeline) && ReferenceEquals(cactbotTimeline, mapping.Value);
            if (isCactbotFill)
                continue; // emitted below with its own source
            var definition = GenerateTimeline(mapping.Key, mapping.Value, cactbot?.TriggersForZone(mapping.Key) ?? [], ExternalTimelineSource.EventTrigger);
            result.Add(definition);
            eventTrigger[mapping.Key] = definition;
        }
        foreach (var mapping in catalog.CactbotTimelinesByZone.OrderBy(mapping => mapping.Key))
        {
            var definition = GenerateTimeline(mapping.Key, mapping.Value, cactbot?.TriggersForZone(mapping.Key) ?? [], ExternalTimelineSource.Cactbot);
            if (eventTrigger.TryGetValue(mapping.Key, out var twin) && SameSequences(twin, definition))
                continue;
            result.Add(definition);
        }
        return new(result);
    }

    private static bool SameSequences(ExternalTimelineDefinition a, ExternalTimelineDefinition b)
        => JsonSerializer.Serialize(a.Sequences) == JsonSerializer.Serialize(b.Sequences);

    private static ExternalTimelineDefinition GenerateTimeline(int zoneID, EventTriggerTimeline timeline, IReadOnlyList<CactbotTrigger> triggers, ExternalTimelineSource source)
    {
        var entries = timeline.Events
            .Where(entry => IsVisible(timeline, entry))
            .GroupBy(entry => (entry.Time, entry.Kind, CastStart: timeline.Actions.Any(action => action.LineNumber == entry.LineNumber && action.Kind == EventTriggerTimelineActionKind.StartsUsing)))
            .Select(group =>
            {
                var groupedEvents = group.OrderBy(entry => entry.LineNumber).ToArray();
                var actions = timeline.Actions
                    .Where(action => groupedEvents.Any(entry => entry.LineNumber == action.LineNumber))
                    .OrderBy(action => action.LineNumber)
                    .ToArray();
                var title = string.Join(" / ", groupedEvents
                    .Select(entry => entry.Title)
                    .Where(value => value is not ("--Reset--" or "--sync--"))
                    .Distinct(StringComparer.Ordinal));
                return new Entry(group.Key.Time, title, groupedEvents.Select(entry => entry.Kind).Distinct().ToArray(), actions);
            })
            .OrderBy(entry => entry.Time)
            .ToArray();

        List<ExternalTimelineSequence> sequences = [];
        List<ExternalTimelineState> states = [];
        var sequenceStart = entries.Length > 0 ? InferSequenceStart(entries[0].Time) : 0f;
        foreach (var entry in entries)
        {
            if (states.Count > 0 && entry.Time - states[^1].Time > SequenceGap)
            {
                sequences.Add(new(sequences.Count, sequenceStart, states.ToArray()));
                states.Clear();
                sequenceStart = InferSequenceStart(entry.Time);
            }
            states.Add(BuildState(entry, triggers));
        }
        if (states.Count > 0)
            sequences.Add(new(sequences.Count, sequenceStart, states.ToArray()));

        // A flat state tree cannot represent a jump. Keep the source data, but stop
        // automatic prediction at the first loop instead of treating its preview as an ending.
        for (var index = 0; index < sequences.Count; ++index)
        {
            var sequence = sequences[index];
            var end = index + 1 < sequences.Count ? sequences[index + 1].States[0].Time : float.PositiveInfinity;
            var firstJump = timeline.Jumps.Where(jump => jump.Time >= sequence.States[0].Time && jump.Time < end).Select(jump => (float?)jump.Time).FirstOrDefault();
            sequence = sequence with { PredictionEndTime = firstJump };
            sequences[index] = sequence with { Windows = BuildWindows(timeline, sequence) };
        }
        var automaticFallback = timeline.Jumps.All(jump => jump.Target is { } target && target > 0f && target < jump.Time);
        return new(zoneID, timeline.FileName, automaticFallback, sequences, source, 1f);
    }

    // BossUntargetable follows the --untargetable--/--targetable-- markers. NoTarget is only claimed when nothing else acts inside
    // the window: a cast by another source, an added combatant or an "adds" title means something attackable is there.
    // The boss is the most frequent source within this sequence's span: dungeon files hold several bosses in one file, split
    // into sequences by the gap between them, so a file-wide mode would make a later boss's own casts look like adds.
    public static IReadOnlyList<ExternalTimelineWindow> BuildWindows(EventTriggerTimeline timeline, ExternalTimelineSequence sequence)
    {
        var sequenceStart = sequence.States[0].Time;
        var sequenceEnd = sequence.States[^1].Time;
        var boss = timeline.Actions.Where(action => action.Time >= sequenceStart && action.Time <= sequenceEnd)
            .SelectMany(action => action.Sources).GroupBy(source => source, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault();
        List<ExternalTimelineWindow> result = [];
        float? lossAt = null;
        foreach (var state in sequence.States)
        {
            if (state.Kind == ExternalTimelineStateKind.Untargetable && lossAt == null)
                lossAt = state.Time;
            else if (state.Kind == ExternalTimelineStateKind.Targetable && lossAt is { } start)
            {
                result.Add(new(ExternalTimelineWindowKind.BossUntargetable, start, state.Time, 1f));
                result.Add(new(SomethingAttackableInside(timeline, sequence, boss, start, state.Time) ? ExternalTimelineWindowKind.AddsPresent : ExternalTimelineWindowKind.NoTarget, start, state.Time, 1f));
                lossAt = null;
            }
        }
        if (lossAt is { } openStart)
            result.Add(new(ExternalTimelineWindowKind.BossUntargetable, openStart, null, 1f));
        return result;
    }

    // The start boundary is inclusive: adds are commonly authored on the same line time as the --untargetable-- marker.
    private static bool SomethingAttackableInside(EventTriggerTimeline timeline, ExternalTimelineSequence sequence, string? boss, float start, float end)
        => timeline.Actions.Any(action => action.Time >= start && action.Time < end && action.Sources.Any(source => !string.Equals(source, boss, StringComparison.Ordinal)))
        || timeline.Events.Any(entry => entry.Time >= start && entry.Time < end && (entry.Kind == EventTriggerTimelineEventKind.AddedCombatant || AddsTitle.IsMatch(entry.Title)))
        || sequence.States.Any(state => state.Time >= start && state.Time < end && (state.Kind == ExternalTimelineStateKind.AddedCombatant || AddsTitle.IsMatch(state.Name)));

    private static ExternalTimelineState BuildState(Entry entry, IReadOnlyList<CactbotTrigger> triggers)
    {
        var actionIDs = entry.Actions.SelectMany(action => action.ActionIDs).Distinct().Order().ToArray();
        var matchingTriggers = MatchTriggers(entry.Name, actionIDs, triggers);
        var startsUsingIDs = entry.Actions
            .Where(action => action.Kind == EventTriggerTimelineActionKind.StartsUsing)
            .SelectMany(action => action.ActionIDs)
            .Distinct()
            .Order()
            .ToArray();
        var addedCombatantIDs = matchingTriggers
            .Where(trigger => trigger.EventType == CactbotEventType.AddedCombatant)
            .SelectMany(trigger => trigger.NpcBaseIDs)
            .Distinct()
            .Order()
            .ToArray();
        var abilityIDs = entry.Actions
            .Where(action => action.Kind == EventTriggerTimelineActionKind.Ability)
            .SelectMany(action => action.ActionIDs)
            .Distinct()
            .Order()
            .ToArray();
        var kind = startsUsingIDs.Length > 0
            ? ExternalTimelineStateKind.CastStart
            : entry.Kinds.Contains(EventTriggerTimelineEventKind.Targetable)
                ? ExternalTimelineStateKind.Targetable
                : entry.Kinds.Contains(EventTriggerTimelineEventKind.Untargetable)
                    ? ExternalTimelineStateKind.Untargetable
                    : entry.Kinds.Contains(EventTriggerTimelineEventKind.AddedCombatant) && addedCombatantIDs.Length > 0
                        ? ExternalTimelineStateKind.AddedCombatant
                        : abilityIDs.Length > 0
                            ? ExternalTimelineStateKind.AbilityUsed
                            : ExternalTimelineStateKind.Timeout;
        var ids = kind switch
        {
            ExternalTimelineStateKind.CastStart => startsUsingIDs,
            ExternalTimelineStateKind.AddedCombatant => addedCombatantIDs,
            ExternalTimelineStateKind.AbilityUsed => abilityIDs,
            ExternalTimelineStateKind.Timeout => actionIDs,
            _ => []
        };
        var hint = entry.Actions.Any(action => action.Kind == EventTriggerTimelineActionKind.Ability)
            ? matchingTriggers.Where(trigger => trigger.EventType is CactbotEventType.StartsUsing or CactbotEventType.Ability)
                .Aggregate(TimelineStateHint.None, (result, trigger) => result | trigger.StateHint)
            : TimelineStateHint.None;
        var normalizedTitle = Normalize(entry.Name);
        if (normalizedTitle.Contains("raidwide", StringComparison.Ordinal))
            hint |= TimelineStateHint.Raidwide;
        if (normalizedTitle.Contains("tankbuster", StringComparison.Ordinal))
            hint |= TimelineStateHint.Tankbuster;
        if (normalizedTitle.Contains("knockback", StringComparison.Ordinal))
            hint |= TimelineStateHint.Knockback;
        return new(entry.Time, entry.Name, kind, ids, hint);
    }

    private static float InferSequenceStart(float firstEventTime)
    {
        var bucket = MathF.Floor(firstEventTime / 1000f) * 1000f;
        return firstEventTime >= 900f && firstEventTime - bucket < 100f ? bucket : 0f;
    }

    private static bool IsVisible(EventTriggerTimeline timeline, EventTriggerTimelineEvent entry)
        => entry.Kind != EventTriggerTimelineEventKind.Label && (entry.Kind is EventTriggerTimelineEventKind.Targetable or EventTriggerTimelineEventKind.Untargetable or EventTriggerTimelineEventKind.AddedCombatant
            || entry.Title is not ("--Reset--" or "--sync--") && entry.Title.Length > 0
            || entry.Title == "--sync--" && timeline.Actions.Any(action => action.LineNumber == entry.LineNumber && action.Kind == EventTriggerTimelineActionKind.StartsUsing));

    private static IReadOnlyList<CactbotTrigger> MatchTriggers(string title, IReadOnlyCollection<uint> actionIDs, IReadOnlyList<CactbotTrigger> triggers)
    {
        var exact = triggers.Where(trigger => trigger.EventType is CactbotEventType.StartsUsing or CactbotEventType.Ability && trigger.IDs.Any(actionIDs.Contains)).ToArray();
        if (exact.Length > 0)
            return exact;

        var normalizedTitle = Normalize(title);
        if (normalizedTitle.Length < 6)
            return [];
        var titleMatches = triggers
            .Where(trigger =>
            {
                var normalizedTrigger = Normalize(trigger.TriggerID);
                return normalizedTrigger.Contains(normalizedTitle, StringComparison.Ordinal) || normalizedTitle.Contains(normalizedTrigger, StringComparison.Ordinal);
            })
            .ToArray();
        var distinctSyncSets = titleMatches
            .Where(trigger => trigger.EventType == CactbotEventType.StartsUsing && trigger.IDs.Count > 0)
            .Select(trigger => string.Join(",", trigger.IDs.Order()))
            .Distinct(StringComparer.Ordinal)
            .Count();
        return distinctSyncSets <= 1 ? titleMatches : titleMatches.Where(trigger => trigger.EventType != CactbotEventType.StartsUsing).ToArray();
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record Entry(float Time, string Name, IReadOnlyList<EventTriggerTimelineEventKind> Kinds, IReadOnlyList<EventTriggerTimelineAction> Actions);
}
