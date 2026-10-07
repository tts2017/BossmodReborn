using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using EncounterTimeline;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  dotnet run --project tools/encounter_timeline -- <resources-directory> <zone-id> [output-file] [--cactbot <cactbot-directory>]");
    Console.Error.WriteLine("  dotnet run --project tools/encounter_timeline -- <resources-directory> --all <output-directory> [--cactbot <cactbot-directory>] [--manifest <output-file>]");
    return 2;
}

string? cactbotDirectory = null;
string? manifestPath = null;
List<string> positional = [];
for (var index = 0; index < args.Length; ++index)
{
    if (args[index] == "--cactbot")
    {
        if (++index >= args.Length || cactbotDirectory != null)
        {
            Console.Error.WriteLine("The --cactbot option requires exactly one source directory.");
            return 2;
        }
        cactbotDirectory = args[index];
    }
    else if (args[index] == "--manifest")
    {
        if (++index >= args.Length || manifestPath != null)
        {
            Console.Error.WriteLine("The --manifest option requires exactly one output file.");
            return 2;
        }
        manifestPath = args[index];
    }
    else
    {
        positional.Add(args[index]);
    }
}
if (positional.Count is < 2 or > 3)
{
    Console.Error.WriteLine("Invalid positional arguments.");
    return 2;
}
if (manifestPath != null && positional[1] != "--all")
{
    Console.Error.WriteLine("The --manifest option is only valid with --all.");
    return 2;
}

var catalog = EventTriggerTimelineCatalog.Load(positional[0], cactbotDirectory);
var cactbot = cactbotDirectory != null ? CactbotTriggerCatalog.Load(cactbotDirectory) : null;
if (positional[1] == "--all")
{
    if (positional.Count != 3)
    {
        Console.Error.WriteLine("The --all command requires an output directory.");
        return 2;
    }

    var outputDirectory = Path.GetFullPath(positional[2]);
    Directory.CreateDirectory(outputDirectory);
    List<TimelineFlagReportEntry> report = [];
    foreach (var mapping in catalog.TimelinesByZone.OrderBy(mapping => mapping.Key))
    {
        var fileName = $"{mapping.Key}-{Path.GetFileNameWithoutExtension(mapping.Value.FileName)}.cs.txt";
        var triggers = cactbot?.TriggersForZone(mapping.Key) ?? [];
        var generatedCode = EventTriggerTimelineSkeletonGenerator.Generate(mapping.Value, mapping.Key, triggers);
        File.WriteAllText(Path.Combine(outputDirectory, fileName), generatedCode);
        report.Add(new(
            mapping.Key,
            mapping.Value.FileName,
            CountOccurrences(generatedCode, "    Condition("),
            CountOccurrences(generatedCode, "    Timeout("),
            CountLinesContaining(generatedCode, "    Targetable(", ", false,"),
            CountLinesContaining(generatedCode, "    Targetable(", ", true,"),
            CountOccurrences(generatedCode, "StateMachine.StateHint.Raidwide"),
            CountOccurrences(generatedCode, "StateMachine.StateHint.Tankbuster"),
            CountOccurrences(generatedCode, "StateMachine.StateHint.Knockback"),
            triggers.Count,
            triggers.Count(trigger => trigger.HasDynamicFilter)));
    }
    if (cactbot != null)
    {
        var reportDocument = new
        {
            Source = "EventTrigger timelines with cactbot trigger metadata; no replay data",
            TimelineCount = report.Count,
            UsableTimelineCount = report.Count(entry => entry.LiveSyncConditions + entry.FallbackTimeoutStates + entry.DowntimeStartFlags + entry.DowntimeEndFlags > 0),
            UnavailableTimelines = report
                .Where(entry => entry.LiveSyncConditions + entry.FallbackTimeoutStates + entry.DowntimeStartFlags + entry.DowntimeEndFlags == 0)
                .Select(entry => new { entry.ZoneID, entry.Timeline })
                .ToArray(),
            StateCount = report.Sum(entry => entry.LiveSyncConditions + entry.FallbackTimeoutStates + entry.DowntimeStartFlags + entry.DowntimeEndFlags),
            LiveSyncConditions = report.Sum(entry => entry.LiveSyncConditions),
            FallbackTimeoutStates = report.Sum(entry => entry.FallbackTimeoutStates),
            DowntimeStartFlags = report.Sum(entry => entry.DowntimeStartFlags),
            DowntimeEndFlags = report.Sum(entry => entry.DowntimeEndFlags),
            RaidwideFlags = report.Sum(entry => entry.RaidwideFlags),
            TankbusterFlags = report.Sum(entry => entry.TankbusterFlags),
            KnockbackFlags = report.Sum(entry => entry.KnockbackFlags),
            Timelines = report
        };
        File.WriteAllText(Path.Combine(outputDirectory, "cactbot-sync-report.json"), JsonSerializer.Serialize(reportDocument, new JsonSerializerOptions { WriteIndented = true }));
    }
    if (manifestPath != null)
    {
        var fullManifestPath = Path.GetFullPath(manifestPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullManifestPath)!);
        var manifest = ExternalTimelineManifestGenerator.Generate(catalog, cactbot);
        File.WriteAllText(fullManifestPath, JsonSerializer.Serialize(manifest));
        Console.WriteLine($"manifest={fullManifestPath}");
        Console.WriteLine($"manifest_timelines={manifest.Timelines.Count} manifest_sequences={manifest.Timelines.Sum(timeline => timeline.Sequences.Count)} manifest_states={manifest.Timelines.SelectMany(timeline => timeline.Sequences).Sum(sequence => sequence.States.Count)}");
        var manifestSequences = manifest.Timelines.SelectMany(timeline => timeline.Sequences).ToArray();
        Console.WriteLine($"manifest_event_trigger={manifest.Timelines.Count(timeline => timeline.Source == ExternalTimelineSource.EventTrigger)} manifest_cactbot={manifest.Timelines.Count(timeline => timeline.Source == ExternalTimelineSource.Cactbot)} manifest_zones={manifest.Timelines.Select(timeline => timeline.ZoneID).Distinct().Count()}");
        Console.WriteLine($"manifest_ability_states={manifestSequences.Sum(sequence => sequence.States.Count(state => state.Kind == ExternalTimelineStateKind.AbilityUsed))} manifest_windows={manifestSequences.Sum(sequence => sequence.Windows?.Count ?? 0)} no_target={manifestSequences.Sum(sequence => sequence.Windows?.Count(window => window.Kind == ExternalTimelineWindowKind.NoTarget) ?? 0)} adds_present={manifestSequences.Sum(sequence => sequence.Windows?.Count(window => window.Kind == ExternalTimelineWindowKind.AddsPresent) ?? 0)} open_windows={manifestSequences.Sum(sequence => sequence.Windows?.Count(window => window.End == null) ?? 0)}");
    }
    Console.WriteLine($"Generated {report.Count} external timeline skeletons in '{outputDirectory}'.");
    Console.WriteLine($"states={report.Sum(entry => entry.LiveSyncConditions + entry.FallbackTimeoutStates + entry.DowntimeStartFlags + entry.DowntimeEndFlags)} live_sync={report.Sum(entry => entry.LiveSyncConditions)} downtime={report.Sum(entry => entry.DowntimeStartFlags)}/{report.Sum(entry => entry.DowntimeEndFlags)} raidwide={report.Sum(entry => entry.RaidwideFlags)} tankbuster={report.Sum(entry => entry.TankbusterFlags)} knockback={report.Sum(entry => entry.KnockbackFlags)}");
    return 0;
}

if (!int.TryParse(positional[1], NumberStyles.None, CultureInfo.InvariantCulture, out var zoneID))
{
    Console.Error.WriteLine($"Invalid zone id '{positional[1]}'.");
    return 2;
}

var timeline = catalog.TimelineForZone(zoneID);
var skeleton = EventTriggerTimelineSkeletonGenerator.Generate(timeline, zoneID, cactbot?.TriggersForZone(zoneID));
if (positional.Count == 3)
{
    var outputPath = Path.GetFullPath(positional[2]);
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    File.WriteAllText(outputPath, skeleton);
    Console.WriteLine($"Generated '{outputPath}'.");
}
else
{
Console.Write(skeleton);
}
return 0;

static int CountOccurrences(string text, string value)
{
    var result = 0;
    for (var index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        ++result;
    return result;
}

static int CountLinesContaining(string text, string prefix, string suffix)
    => text.Split('\n').Count(line => line.Contains(prefix, StringComparison.Ordinal) && line.Contains(suffix, StringComparison.Ordinal));

internal sealed record TimelineFlagReportEntry(
    int ZoneID,
    string Timeline,
    int LiveSyncConditions,
    int FallbackTimeoutStates,
    int DowntimeStartFlags,
    int DowntimeEndFlags,
    int RaidwideFlags,
    int TankbusterFlags,
    int KnockbackFlags,
    int TriggerCount,
    int DynamicFilterCount);
