using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EncounterTimeline;

public static class EventTriggerTimelineSkeletonGenerator
{
    public static string Generate(EventTriggerTimeline timeline, int zoneID, IReadOnlyList<CactbotTrigger>? cactbotTriggers = null)
    {
        cactbotTriggers ??= [];
        var output = new StringBuilder();
        output.AppendLine($"// Generated development skeleton from {timeline.FileName} (TerritoryType {zoneID}).");
        output.AppendLine("// External EventTrigger/cactbot data only; no replay data was used.");
        output.AppendLine("// Cast-start sync points, targetability, and unambiguous AddedCombatant states use live event conditions; remaining Timeout states preserve expected ordering only.");
        if (cactbotTriggers.Count > 0)
        {
            output.AppendLine($"// Cactbot: {cactbotTriggers.Count} static/dynamic trigger definitions from {string.Join(", ", cactbotTriggers.Select(trigger => trigger.FileName).Distinct(StringComparer.OrdinalIgnoreCase))}.");
            var oidCandidates = cactbotTriggers.SelectMany(trigger => trigger.NpcBaseIDs).Distinct().Order().ToArray();
            if (oidCandidates.Length > 0)
                output.AppendLine($"// Cactbot actor OID candidates (not automatically promoted to primary boss): {string.Join(", ", oidCandidates.Select(id => $"0x{id:X}"))}.");
        }
        output.AppendLine();

        var sections = BuildSections(timeline);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var methodName = UniqueIdentifier(section.Title, usedNames);
            output.AppendLine($"private void {methodName}(uint id)");
            output.AppendLine("{");

            var stateIndex = 0u;
            var previousTime = section.StartTime;
            foreach (var entry in section.Entries)
            {
                var delay = Math.Max(0, entry.Time - previousTime);
                var title = Escape(entry.Title);
                var actionComment = ActionComment(entry.Actions);
                var cactbotMatches = MatchCactbotTriggers(entry, cactbotTriggers);
                var stateID = $"id + 0x{(stateIndex * 0x10).ToString("X", CultureInfo.InvariantCulture)}";
                WriteState(output, stateID, delay, title, entry, cactbotMatches);
                output.Append(" // t=");
                output.Append(entry.Time.ToString("0.0##", CultureInfo.InvariantCulture));
                if (actionComment.Length > 0)
                {
                    output.Append("; ");
                    output.Append(actionComment);
                }
                if (cactbotMatches.Count > 0)
                {
                    output.Append("; cactbot ");
                    output.Append(string.Join(" | ", cactbotMatches.Select(CactbotComment)));
                }
                output.AppendLine();
                previousTime = entry.Time;
                ++stateIndex;
            }

            if (stateIndex == 0)
                output.AppendLine("    // No visible timeline entries in this section.");
            output.AppendLine("}");
            output.AppendLine();
        }

        return output.ToString();
    }

    private static void WriteState(StringBuilder output, string stateID, float delay, string title, SkeletonEntry entry, IReadOnlyList<CactbotTrigger> cactbotMatches)
    {
        var timelineStartsUsingIDs = entry.Actions
            .Where(action => action.Kind == EventTriggerTimelineActionKind.StartsUsing)
            .SelectMany(action => action.ActionIDs)
            .Distinct()
            .ToArray();
        var startsUsingIDs = timelineStartsUsingIDs.Order().ToArray();
        if (startsUsingIDs.Length > 0)
        {
            output.Append("    Condition(");
            output.Append(stateID);
            output.Append(", ");
            output.Append(FloatLiteral(delay));
            output.Append(", () => Module.WorldState.Actors.Any(actor => actor.CastInfo?.Action.ID ");
            if (startsUsingIDs.Length == 1)
            {
                output.Append("== 0x");
                output.Append(startsUsingIDs[0].ToString("X", CultureInfo.InvariantCulture));
            }
            else
            {
                output.Append("is ");
                output.Append(string.Join(" or ", startsUsingIDs.Select(id => $"0x{id:X}")));
            }
            output.Append("), \"");
            output.Append(title);
            output.Append("\", 5f, ");
            output.Append(FloatLiteral(Math.Max(0, delay - 5)));
            output.Append(")");
        }
        else if (entry.Kinds.Contains(EventTriggerTimelineEventKind.Targetable))
        {
            output.Append($"    Targetable({stateID}, true, {FloatLiteral(delay)}, \"{title}\")");
        }
        else if (entry.Kinds.Contains(EventTriggerTimelineEventKind.Untargetable))
        {
            output.Append($"    Targetable({stateID}, false, {FloatLiteral(delay)}, \"{title}\")");
        }
        else
        {
            var addedCombatantIDs = cactbotMatches
                .Where(trigger => trigger.EventType == CactbotEventType.AddedCombatant)
                .SelectMany(trigger => trigger.NpcBaseIDs)
                .Distinct()
                .Order()
                .ToArray();
            if (entry.Kinds.Contains(EventTriggerTimelineEventKind.AddedCombatant) && addedCombatantIDs.Length > 0)
            {
                output.Append($"    Condition({stateID}, {FloatLiteral(delay)}, () => Module.WorldState.Actors.Any(actor => actor.OID ");
                if (addedCombatantIDs.Length == 1)
                    output.Append($"== 0x{addedCombatantIDs[0]:X}");
                else
                    output.Append($"is {string.Join(" or ", addedCombatantIDs.Select(id => $"0x{id:X}"))}");
                output.Append($"), \"{title}\", 5f, {FloatLiteral(Math.Max(0, delay - 5))})");
            }
            else
            {
                output.Append($"    Timeout({stateID}, {FloatLiteral(delay)}, \"{title}\")");
            }
        }

        var stateHint = entry.Actions.Any(action => action.Kind == EventTriggerTimelineActionKind.Ability)
            ? ClassifyStateHint(entry.Title, cactbotMatches)
            : TimelineStateHint.None;
        if (stateHint != TimelineStateHint.None)
            output.Append($".SetHint({StateHintExpression(stateHint)})");
        output.Append(';');
    }

    private sealed record SkeletonEntry(float Time, string Title, IReadOnlyList<EventTriggerTimelineEventKind> Kinds, IReadOnlyList<EventTriggerTimelineAction> Actions);
    private sealed record SkeletonSection(string Title, float StartTime, IReadOnlyList<SkeletonEntry> Entries);

    private static IReadOnlyList<SkeletonSection> BuildSections(EventTriggerTimeline timeline)
    {
        var sectionStarts = timeline.Sections.Count > 0
            ? timeline.Sections
            : [new("Timeline", timeline.Events.Count > 0 ? timeline.Events[0].Time : 0, 0)];
        List<SkeletonSection> result = [];

        for (var sectionIndex = 0; sectionIndex < sectionStarts.Count; ++sectionIndex)
        {
            var section = sectionStarts[sectionIndex];
            var end = sectionIndex + 1 < sectionStarts.Count ? sectionStarts[sectionIndex + 1].StartTime : float.PositiveInfinity;
            var entries = timeline.Events
                .Where(entry => entry.Time >= section.StartTime && entry.Time < end && IsVisible(timeline, entry))
                .GroupBy(entry => (entry.Time, entry.Kind, CastStart: timeline.Actions.Any(action => action.LineNumber == entry.LineNumber && action.Kind == EventTriggerTimelineActionKind.StartsUsing)))
                .Select(group =>
                {
                    var groupedEvents = group.OrderBy(entry => entry.LineNumber).ToArray();
                    var titles = groupedEvents
                        .Select(entry => entry.Title)
                        .Where(title => title is not ("--Reset--" or "--sync--"))
                        .Distinct(StringComparer.Ordinal);
                    var actions = timeline.Actions
                        .Where(action => groupedEvents.Any(entry => entry.LineNumber == action.LineNumber))
                        .OrderBy(action => action.LineNumber)
                        .ToArray();
                    return new SkeletonEntry(group.Key.Time, string.Join(" / ", titles), groupedEvents.Select(entry => entry.Kind).Distinct().ToArray(), actions);
                })
                .OrderBy(entry => entry.Time)
                .ToArray();
            result.Add(new(section.Title, section.StartTime, entries));
        }

        return result;
    }

    private static bool IsVisible(EventTriggerTimeline timeline, EventTriggerTimelineEvent entry)
        => entry.Kind != EventTriggerTimelineEventKind.Label && (entry.Kind is EventTriggerTimelineEventKind.Targetable or EventTriggerTimelineEventKind.Untargetable or EventTriggerTimelineEventKind.AddedCombatant
            || entry.Title is not ("--Reset--" or "--sync--") && entry.Title.Length > 0
            || entry.Title == "--sync--" && timeline.Actions.Any(action => action.LineNumber == entry.LineNumber && action.Kind == EventTriggerTimelineActionKind.StartsUsing));

    private static TimelineStateHint ClassifyStateHint(string title, IReadOnlyList<CactbotTrigger> triggers)
    {
        var result = triggers.Where(trigger => trigger.EventType is CactbotEventType.StartsUsing or CactbotEventType.Ability)
            .Aggregate(TimelineStateHint.None, (hints, trigger) => hints | trigger.StateHint);
        var normalizedTitle = Normalize(title);
        if (normalizedTitle.Contains("raidwide", StringComparison.Ordinal))
            result |= TimelineStateHint.Raidwide;
        if (normalizedTitle.Contains("tankbuster", StringComparison.Ordinal))
            result |= TimelineStateHint.Tankbuster;
        if (normalizedTitle.Contains("knockback", StringComparison.Ordinal))
            result |= TimelineStateHint.Knockback;
        return result;
    }

    private static string StateHintExpression(TimelineStateHint hints)
    {
        List<string> result = [];
        if (hints.HasFlag(TimelineStateHint.Raidwide))
            result.Add("StateMachine.StateHint.Raidwide");
        if (hints.HasFlag(TimelineStateHint.Tankbuster))
            result.Add("StateMachine.StateHint.Tankbuster");
        if (hints.HasFlag(TimelineStateHint.Knockback))
            result.Add("StateMachine.StateHint.Knockback");
        return string.Join(" | ", result);
    }

    private static string ActionComment(IReadOnlyList<EventTriggerTimelineAction> actions)
        => string.Join(" | ", actions.Select(action =>
            $"{action.Kind} {string.Join('/', action.ActionIDs.Select(id => id.ToString("X", CultureInfo.InvariantCulture)))} from {string.Join('/', action.Sources)}"));

    private static IReadOnlyList<CactbotTrigger> MatchCactbotTriggers(SkeletonEntry entry, IReadOnlyList<CactbotTrigger> triggers)
    {
        var actionIDs = entry.Actions.SelectMany(action => action.ActionIDs).ToHashSet();
        var exact = triggers.Where(trigger => trigger.EventType is CactbotEventType.StartsUsing or CactbotEventType.Ability && trigger.IDs.Any(actionIDs.Contains)).ToArray();
        if (exact.Length > 0)
            return exact;

        var normalizedTitle = Normalize(entry.Title);
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

    private static string CactbotComment(CactbotTrigger trigger)
    {
        var details = trigger.IDs.Count > 0 ? $" {string.Join('/', trigger.IDs.Select(id => id.ToString("X", CultureInfo.InvariantCulture)))}" : "";
        if (trigger.NpcBaseIDs.Count > 0)
            details += $" OID={string.Join('/', trigger.NpcBaseIDs.Select(id => $"0x{id:X}"))}";
        return $"{trigger.EventType}{details} [{trigger.TriggerID}]";
    }

    private static string Normalize(string value)
        => new(value.Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string UniqueIdentifier(string value, ISet<string> used)
    {
        var chars = value.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray();
        var identifier = new string(chars).Trim('_');
        if (identifier.Length == 0)
            identifier = "Timeline";
        if (char.IsAsciiDigit(identifier[0]))
            identifier = "Timeline_" + identifier;

        var unique = identifier;
        for (var suffix = 2; !used.Add(unique); ++suffix)
            unique = identifier + "_" + suffix.ToString(CultureInfo.InvariantCulture);
        return unique;
    }

    private static string FloatLiteral(float value) => value.ToString("0.0##", CultureInfo.InvariantCulture) + "f";
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
