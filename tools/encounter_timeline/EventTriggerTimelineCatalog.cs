using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace EncounterTimeline;

public enum EventTriggerTimelineEventKind
{
    Other,
    InCombat,
    Targetable,
    Untargetable,
    AddedCombatant,
    Label
}

public enum EventTriggerTimelineActionKind
{
    StartsUsing,
    Ability
}

public readonly record struct EventTriggerTimelineEvent(float Time, EventTriggerTimelineEventKind Kind, string Title, int LineNumber);
public readonly record struct EventTriggerTimelineAction(float Time, EventTriggerTimelineActionKind Kind, string Title, IReadOnlyList<uint> ActionIDs, IReadOnlyList<string> Sources, int LineNumber);
public readonly record struct EventTriggerTimelineSection(string Title, float StartTime, int LineNumber);
public readonly record struct EventTriggerTimelineJump(float Time, float? Target, bool Forced, int LineNumber);

public readonly record struct EventTriggerTimelineWindow(float Start, float End)
{
    public bool Contains(float time) => time >= Start && time < End;
}

public sealed record EventTriggerTimelineSummary(
    int TimelineFiles,
    int ZoneMappings,
    int InCombatEvents,
    int TargetableEvents,
    int UntargetableEvents,
    int AddedCombatantEvents);

public sealed class EventTriggerTimeline
{
    internal EventTriggerTimeline(string fullPath, IReadOnlyList<EventTriggerTimelineEvent> events, IReadOnlyList<EventTriggerTimelineAction> actions, IReadOnlyList<EventTriggerTimelineSection> sections, IReadOnlyList<EventTriggerTimelineJump> jumps)
    {
        FullPath = fullPath;
        FileName = Path.GetFileName(fullPath);
        Events = events;
        Actions = actions;
        Sections = sections;
        Jumps = jumps;
    }

    public string FullPath { get; }
    public string FileName { get; }
    public IReadOnlyList<EventTriggerTimelineEvent> Events { get; }
    public IReadOnlyList<EventTriggerTimelineAction> Actions { get; }
    public IReadOnlyList<EventTriggerTimelineSection> Sections { get; }
    public IReadOnlyList<EventTriggerTimelineJump> Jumps { get; }

    public IReadOnlyList<EventTriggerTimelineWindow> TargetUnavailableWindows(float endTime)
    {
        List<EventTriggerTimelineWindow> result = [];
        var targetable = true;
        var unavailableStart = 0f;

        foreach (var entry in Events)
        {
            if (entry.Time > endTime)
                break;

            switch (entry.Kind)
            {
                case EventTriggerTimelineEventKind.Untargetable when targetable:
                    targetable = false;
                    unavailableStart = entry.Time;
                    break;
                case EventTriggerTimelineEventKind.Targetable when !targetable:
                    if (entry.Time > unavailableStart)
                        result.Add(new(unavailableStart, entry.Time));
                    targetable = true;
                    break;
            }
        }

        if (!targetable && endTime > unavailableStart)
            result.Add(new(unavailableStart, endTime));

        return result;
    }
}

public sealed class EventTriggerTimelineCatalog
{
    private static readonly Regex MappingLine = new("""^\s*(?<zone>\d+)\s*,\s*"(?<file>[^"]+)"\s*$""", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TimedEntry = new("""^\s*(?<time>\d+(?:\.\d+)?)\s+(?:"(?<title>[^"]*)"|label\s+"(?<label>[^"]*)")(?<tail>.*)$""", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TimelineAction = new("(?<!#)\\b(?<kind>StartsUsing|Ability)\\s*\\{\\s*id:\\s*(?<ids>\"[^\"]+\"|\\[[^\\]]+\\])\\s*,\\s*source:\\s*(?<sources>\"[^\"]+\"|\\[[^\\]]+\\])\\s*\\}", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex QuotedValue = new("\"(?<value>[^\"]+)\"", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Jump = new("""\b(?<kind>forcejump|jump)\s+(?:"(?<label>[^"]+)"|(?<target>\d+(?:\.\d+)?))""", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CactbotZoneID = new("""^\s*'?(?<name>[A-Za-z0-9_]+)'?\s*:\s*(?<id>\d+)\s*,""", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CactbotZone = new("""zoneId:\s*ZoneId\.(?<name>[A-Za-z0-9_]+)""", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CactbotTimelineFile = new("""timelineFile:\s*'(?<file>[^']+)'""", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private EventTriggerTimelineCatalog(string resourcesDirectory, IReadOnlyList<EventTriggerTimeline> timelines, IReadOnlyDictionary<int, EventTriggerTimeline> timelinesByZone, IReadOnlyDictionary<int, EventTriggerTimeline> cactbotTimelinesByZone)
    {
        ResourcesDirectory = resourcesDirectory;
        Timelines = timelines;
        TimelinesByZone = timelinesByZone;
        CactbotTimelinesByZone = cactbotTimelinesByZone;
        Summary = new(
            timelines.Count,
            timelinesByZone.Count,
            timelines.Sum(timeline => timeline.Events.Count(entry => entry.Kind == EventTriggerTimelineEventKind.InCombat)),
            timelines.Sum(timeline => timeline.Events.Count(entry => entry.Kind == EventTriggerTimelineEventKind.Targetable)),
            timelines.Sum(timeline => timeline.Events.Count(entry => entry.Kind == EventTriggerTimelineEventKind.Untargetable)),
            timelines.Sum(timeline => timeline.Events.Count(entry => entry.Kind == EventTriggerTimelineEventKind.AddedCombatant)));
    }

    public string ResourcesDirectory { get; }
    public IReadOnlyList<EventTriggerTimeline> Timelines { get; }
    public IReadOnlyDictionary<int, EventTriggerTimeline> TimelinesByZone { get; }
    // Every cactbot timeline mapping, including zones Event Trigger also maps: the generator emits these as their own source.
    public IReadOnlyDictionary<int, EventTriggerTimeline> CactbotTimelinesByZone { get; }
    public EventTriggerTimelineSummary Summary { get; }

    public EventTriggerTimeline TimelineForZone(int zoneID)
        => TimelinesByZone.TryGetValue(zoneID, out var timeline)
            ? timeline
            : throw new KeyNotFoundException($"No Event Trigger timeline is mapped for zone {zoneID}.");

    // Cactbot ships its own copy of the same timeline format and maps zones from its trigger files instead of a csv. TimelinesByZone
    // only takes cactbot entries for zones Event Trigger does not map, so existing entries keep their source while content Event
    // Trigger has not picked up yet (Occult Crescent, freshly patched duties) still gets a timeline. CactbotTimelinesByZone keeps
    // every cactbot mapping regardless, so the manifest can carry both sources side by side.
    public static EventTriggerTimelineCatalog Load(string resourcesDirectory, string? cactbotDirectory)
    {
        var catalog = Load(resourcesDirectory);
        if (cactbotDirectory == null)
            return catalog;

        var zoneIDs = ParseCactbotZoneIDs(Path.Combine(cactbotDirectory, "resources", "zone_id.ts"));
        var dataDirectory = Path.Combine(cactbotDirectory, "ui", "raidboss", "data");
        if (zoneIDs.Count == 0 || !Directory.Exists(dataDirectory))
            return catalog;

        var timelines = catalog.Timelines.ToList();
        var timelinesByZone = catalog.TimelinesByZone.ToDictionary(entry => entry.Key, entry => entry.Value);
        Dictionary<int, EventTriggerTimeline> cactbotByZone = [];
        foreach (var (zoneID, path) in ParseCactbotTimelineMappings(dataDirectory, zoneIDs))
        {
            if (cactbotByZone.ContainsKey(zoneID) || !File.Exists(path))
                continue;
            var timeline = ParseTimeline(path);
            // Several cactbot files only carry reset and sync lines for their trigger set: importing those adds an empty zone entry.
            if (timeline.Actions.Count == 0 && !timeline.Events.Any(entry => entry.Kind is EventTriggerTimelineEventKind.Targetable or EventTriggerTimelineEventKind.Untargetable or EventTriggerTimelineEventKind.AddedCombatant))
                continue;
            cactbotByZone.Add(zoneID, timeline);
            if (!timelinesByZone.ContainsKey(zoneID))
            {
                timelines.Add(timeline);
                timelinesByZone.Add(zoneID, timeline);
            }
        }

        return new(catalog.ResourcesDirectory, timelines, timelinesByZone, cactbotByZone);
    }

    private static IReadOnlyDictionary<string, int> ParseCactbotZoneIDs(string zoneIDPath)
    {
        Dictionary<string, int> result = new(StringComparer.Ordinal);
        if (!File.Exists(zoneIDPath))
            return result;
        foreach (var line in File.ReadLines(zoneIDPath))
        {
            var match = CactbotZoneID.Match(line);
            if (match.Success)
                result[match.Groups["name"].Value] = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
        }
        return result;
    }

    private static IEnumerable<(int ZoneID, string Path)> ParseCactbotTimelineMappings(string dataDirectory, IReadOnlyDictionary<string, int> zoneIDs)
    {
        foreach (var path in Directory.EnumerateFiles(dataDirectory, "*.ts", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var text = File.ReadAllText(path);
            var zone = CactbotZone.Match(text);
            var file = CactbotTimelineFile.Match(text);
            if (!zone.Success || !file.Success || !zoneIDs.TryGetValue(zone.Groups["name"].Value, out var zoneID))
                continue;
            yield return (zoneID, Path.Combine(Path.GetDirectoryName(path)!, file.Groups["file"].Value));
        }
    }

    public static EventTriggerTimelineCatalog Load(string resourcesDirectory)
    {
        var root = Path.GetFullPath(resourcesDirectory);
        var mappingPath = Path.Combine(root, "timelines.csv");
        var timelineDirectory = Path.Combine(root, "timeline");
        if (!File.Exists(mappingPath) || !Directory.Exists(timelineDirectory))
            throw new DirectoryNotFoundException($"Event Trigger resources were not found below '{root}'.");

        var timelines = Directory
            .EnumerateFiles(timelineDirectory, "*.txt", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(ParseTimeline)
            .ToList();
        var timelinesByFile = timelines.ToDictionary(timeline => timeline.FileName, StringComparer.OrdinalIgnoreCase);
        Dictionary<int, EventTriggerTimeline> timelinesByZone = [];

        foreach (var mapping in ParseMappings(mappingPath))
        {
            if (!timelinesByFile.TryGetValue(mapping.FileName, out var timeline))
                throw new FileNotFoundException($"Timeline '{mapping.FileName}' mapped from zone {mapping.ZoneID} was not found.", mappingPath);
            timelinesByZone.Add(mapping.ZoneID, timeline);
        }

        return new(root, timelines, timelinesByZone, new Dictionary<int, EventTriggerTimeline>());
    }

    private static IEnumerable<(int ZoneID, string FileName)> ParseMappings(string mappingPath)
    {
        foreach (var line in File.ReadLines(mappingPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var match = MappingLine.Match(line);
            if (!match.Success)
                throw new FormatException($"Invalid timeline mapping: '{line}'.");

            yield return (int.Parse(match.Groups["zone"].Value, CultureInfo.InvariantCulture), match.Groups["file"].Value);
        }
    }

    private static EventTriggerTimeline ParseTimeline(string path)
    {
        List<EventTriggerTimelineEvent> events = [];
        List<EventTriggerTimelineAction> actions = [];
        List<EventTriggerTimelineSection> sections = [];
        List<(float Time, string? Label, float? Target, bool Forced, int Line)> jumps = [];
        Dictionary<string, float> labels = new(StringComparer.Ordinal);
        string? pendingSection = null;
        var pendingSectionLine = 0;
        var lineNumber = 0;
        foreach (var line in File.ReadLines(path))
        {
            ++lineNumber;
            var trimmed = line.Trim();
            if (trimmed.StartsWith("### ", StringComparison.Ordinal) || trimmed.StartsWith("# Phase ", StringComparison.Ordinal))
            {
                pendingSection = trimmed.StartsWith("### ", StringComparison.Ordinal) ? trimmed[4..].Trim() : trimmed[2..].Trim();
                pendingSectionLine = lineNumber;
                continue;
            }

            var comment = line.IndexOf('#');
            var match = TimedEntry.Match(comment >= 0 ? line[..comment] : line);
            if (!match.Success)
                continue;

            var title = match.Groups["title"].Success ? match.Groups["title"].Value : match.Groups["label"].Value;
            var tail = match.Groups["tail"].Value;
            var time = float.Parse(match.Groups["time"].Value, CultureInfo.InvariantCulture);
            if (pendingSection != null)
            {
                sections.Add(new(pendingSection, time, pendingSectionLine));
                pendingSection = null;
            }
            var isLabel = match.Groups["label"].Success;
            if (isLabel)
                labels[title] = time;
            events.Add(new(time, isLabel ? EventTriggerTimelineEventKind.Label : Classify(title, tail), title, lineNumber));
            actions.AddRange(ParseActions(time, title, tail, lineNumber));
            var jump = Jump.Match(tail);
            if (jump.Success && title != "--Reset--")
                jumps.Add((time,
                    jump.Groups["label"].Success ? jump.Groups["label"].Value : null,
                    jump.Groups["target"].Success ? float.Parse(jump.Groups["target"].Value, CultureInfo.InvariantCulture) : null,
                    jump.Groups["kind"].Value == "forcejump", lineNumber));
        }

        return new(path, events.OrderBy(entry => entry.Time).ThenBy(entry => entry.LineNumber).ToArray(), actions.OrderBy(action => action.Time).ThenBy(action => action.LineNumber).ToArray(), sections.OrderBy(section => section.StartTime).ThenBy(section => section.LineNumber).ToArray(),
            jumps.Select(jump => new EventTriggerTimelineJump(jump.Time,
                jump.Label != null ? labels.TryGetValue(jump.Label, out var target) ? target : null : jump.Target,
                jump.Forced, jump.Line)).OrderBy(jump => jump.Time).ToArray());
    }

    private static IEnumerable<EventTriggerTimelineAction> ParseActions(float time, string title, string tail, int lineNumber)
    {
        foreach (Match match in TimelineAction.Matches(tail))
        {
            List<uint> actionIDs = [];
            foreach (Match id in QuotedValue.Matches(match.Groups["ids"].Value))
            {
                if (!uint.TryParse(id.Groups["value"].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var actionID))
                {
                    actionIDs.Clear();
                    break;
                }
                actionIDs.Add(actionID);
            }

            var sources = QuotedValue.Matches(match.Groups["sources"].Value).Select(value => value.Groups["value"].Value).ToArray();
            if (actionIDs.Count == 0 || sources.Length == 0)
                continue;

            yield return new(time, match.Groups["kind"].Value == "StartsUsing" ? EventTriggerTimelineActionKind.StartsUsing : EventTriggerTimelineActionKind.Ability, title, actionIDs.ToArray(), sources, lineNumber);
        }
    }

    private static EventTriggerTimelineEventKind Classify(string title, string tail)
    {
        if (title.Equals("--untargetable--", StringComparison.Ordinal))
            return EventTriggerTimelineEventKind.Untargetable;
        if (title.Equals("--targetable--", StringComparison.Ordinal))
            return EventTriggerTimelineEventKind.Targetable;
        if (tail.Contains("InCombat", StringComparison.Ordinal))
            return EventTriggerTimelineEventKind.InCombat;
        if (tail.Contains("AddedCombatant", StringComparison.Ordinal))
            return EventTriggerTimelineEventKind.AddedCombatant;
        return EventTriggerTimelineEventKind.Other;
    }
}
