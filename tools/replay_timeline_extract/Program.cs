using System.Text.Json;
using BossMod;

// usage: ReplayTimelineExtract [--out <dir>] [--zone <id>] [--auto <timelinesDir> [--max-pulls <K|all>]] <replay.log|dir>...
// Writes one <zone>-replay.json per zone into <dir> (default: %APPDATA%\XIVLauncher\pluginConfigs\BossMod\timelines).
// --auto runs the plugin's automatic-timeline gate instead: each zone is published to <timelinesDir>\auto\<zone>-auto.json (or
// not, with the reason) and the gate's per-window decisions are printed and written to auto\report.txt. The lower-ranked source a
// zone is compared with comes from the embedded manifest plus the files directly in <timelinesDir>, as in the plugin.
// --max-pulls builds each boss set from its K most recent pulls (all: no cap); the default is the plugin's
// AutoTimelineGate.DefaultMaxPullsPerSet.
string? outDir = null;
string? autoDir = null;
ushort? onlyZone = null;
int? maxPulls = null;
List<string> inputs = [];
for (var i = 0; i < args.Length; ++i)
{
    if (args[i] == "--out") outDir = args[++i];
    else if (args[i] == "--zone") onlyZone = ushort.Parse(args[++i]);
    else if (args[i] == "--auto") autoDir = args[++i];
    else if (args[i] == "--max-pulls") maxPulls = args[++i] == "all" ? int.MaxValue : int.Parse(args[i]);
    else inputs.Add(args[i]);
}
if (inputs.Count == 0 || outDir != null && autoDir != null || maxPulls != null && (autoDir == null || maxPulls < 1))
{
    Console.Error.WriteLine("usage: ReplayTimelineExtract [--out <dir>] [--zone <id>] [--auto <timelinesDir> [--max-pulls <K|all>]] <replay.log|dir>... (--out and --auto exclude each other; K >= 1)");
    return 2;
}
if (autoDir == null)
{
    outDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "BossMod", "timelines");
    Directory.CreateDirectory(outDir);
}
// Building a WorldState touches ActionDefinitions, which reads game sheets: same setup as tools/external_timeline_regression.
Service.LuminaGameData = new Lumina.GameData(@"C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
Service.Config.Initialize();
// ReplayParserLog.Parse swallows failures and reports them through Service.Log; surface them instead of silently yielding empty replays.
Service.LogHandlerDebug = msg =>
{
    if (msg.StartsWith("Failed to read", StringComparison.Ordinal))
        Console.Error.WriteLine(msg);
};

// One replay at a time: each is parsed and its pulls summarized inside SummarizeFile, so the replay is unreachable once it returns
// and memory holds the summaries of every replay but at most one replay (as the plugin's rebuild does); peak_mb measures that.
var files = inputs.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.log", SearchOption.AllDirectories) : [p]).ToList();
List<PullSummary> summaries = [];
var replayCount = 0;
foreach (var file in files)
{
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (pulls, line) = SummarizeFile(file);
        if (pulls != null)
        {
            ++replayCount;
            summaries.AddRange(pulls);
        }
        Console.WriteLine($"{line} summarize_ms={sw.ElapsedMilliseconds}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{file}: {ex.Message}");
    }
}

var zoneCount = 0;
if (autoDir != null)
{
    TimelineStore.UserDirectory = autoDir;
    TimelineStore.Reload();
    foreach (var zone in summaries.Select(p => p.Zone).Where(z => z != 0).Distinct().Order())
    {
        if (onlyZone != null && zone != onlyZone)
            continue;
        ++zoneCount;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = AutoTimelineGate.Evaluate(zone, summaries, maxPulls ?? AutoTimelineGate.DefaultMaxPullsPerSet);
        var outcome = AutoTimelineGate.Publish(autoDir, zone, result, AutoTimelineGate.LowerRanked(zone));
        Console.WriteLine($"{outcome} rebuild_ms={sw.ElapsedMilliseconds}");
        foreach (var line in result.Report.Skip(1))
            Console.WriteLine(line);
        // Round-trip check: a written file must load through the path the plugin's store uses for the auto folder.
        if (outcome.StartsWith($"zone={zone} written", StringComparison.Ordinal))
        {
            var path = Path.Combine(AutoTimelineGate.AutoDirectory(autoDir), $"{zone}-auto.json");
            var reloaded = TimelineStore.LoadUserFile(path);
            if (reloaded.Count != 1 || reloaded[0].ZoneID != zone || reloaded[0].Source != ExternalPlannerTimeline.TimelineSource.AutoReplay
                || reloaded[0].Sequences.Count != result.Timeline!.Sequences.Count)
                Console.Error.WriteLine($"zone={zone}: LoadUserFile round-trip mismatch (loaded {reloaded.Count} timelines)");
        }
    }
}
else
{
    var extracted = ReplayTimelineExtractor.Extract(summaries);
    zoneCount = extracted.Count;
    foreach (var (zone, timeline) in extracted.OrderBy(kv => kv.Key))
    {
        if (onlyZone != null && zone != onlyZone)
            continue;
        var path = Path.Combine(outDir!, $"{zone}-replay.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Timelines = new[] { timeline } }));
        var windows = timeline.Sequences.SelectMany(s => s.Windows ?? []).ToList();
        Console.WriteLine($"zone={zone} sequences={timeline.Sequences.Count} states={timeline.Sequences.Sum(s => s.States.Count)} windows={windows.Count} no_target={windows.Count(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget)} confidence={timeline.Confidence:f2} -> {path}");
        foreach (var line in ReplayTimelineExtractor.DescribeBranches(timeline))
            Console.WriteLine(line);
        // Round-trip check: the file must load through the same path the plugin uses for the user folder.
        var reloaded = TimelineStore.LoadUserFile(path);
        if (reloaded.Count != 1 || reloaded[0].ZoneID != zone || reloaded[0].Sequences.Count != timeline.Sequences.Count)
            Console.Error.WriteLine($"zone={zone}: LoadUserFile round-trip mismatch (loaded {reloaded.Count} timelines)");
    }
}
Console.WriteLine($"replays={replayCount} zones={zoneCount} peak_mb={System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)}");
return 0;

// Parses one replay and summarizes its pulls (null for an empty replay, which is not counted), with the per-replay line. The replay
// lives only in this frame: not inlined, so it is unreachable while the caller parses the next file.
[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static (List<PullSummary>? Pulls, string Line) SummarizeFile(string file)
{
    var progress = 0f;
    var replay = ReplayParserLog.Parse(file, ref progress, CancellationToken.None);
    var pulls = ReplayTimelineExtractor.FindPulls(replay);
    var line = $"{Path.GetFileName(file)}: ops={replay.Ops.Count} encounters={replay.Encounters.Count} pulls={pulls.Count}";
    return (replay.Ops.Count > 0 ? pulls.Select(p => PullSummary.Summarize(p)).ToList() : null, line);
}
