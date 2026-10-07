using BossMod;

// usage: TimelineHintHarness [--source EventTrigger|Cactbot|Replay|FFLogs] [--sync caststart|all] [--holdout] [--gate] [--csv <file>] [--timelines <dir>] <replay.log|dir>...
// Replays every log through the real ExternalTimelineHints and scores its published target-loss hints against the NoTarget
// windows measured from the same replay. --holdout builds replay timelines from all other logs before scoring one. --gate
// mimics the plugin's module gate: zones whose registered module has a real state machine get no hints and are not scored.
// --timelines points the store at a folder of user timeline files (e.g. FFLogs-derived ones) instead of the plugin config folder.
const float LongLoss = 8.5f;
const float FalseAlarmSeconds = 30f;
const float LeadCap = 25f;

ExternalPlannerTimeline.TimelineSource? source = null;
var sync = "all";
var holdout = false;
var gate = false;
string? csv = null;
string? timelinesDir = null;
List<string> inputs = [];
for (var i = 0; i < args.Length; ++i)
{
    if (args[i] == "--source") source = Enum.Parse<ExternalPlannerTimeline.TimelineSource>(args[++i]);
    else if (args[i] == "--sync") sync = args[++i];
    else if (args[i] == "--holdout") holdout = true;
    else if (args[i] == "--gate") gate = true;
    else if (args[i] == "--csv") csv = args[++i];
    else if (args[i] == "--timelines") timelinesDir = args[++i];
    else inputs.Add(args[i]);
}
if (inputs.Count == 0)
{
    Console.Error.WriteLine("usage: TimelineHintHarness [--source X] [--sync caststart|all] [--holdout] [--gate] [--csv f] [--timelines dir] <replay.log|dir>...");
    return 2;
}

Service.LuminaGameData = new Lumina.GameData(@"C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
Service.Config.Initialize();
if (timelinesDir != null)
{
    TimelineStore.UserDirectory = timelinesDir;
    TimelineStore.Reload();
}
Service.Config.Get<BossModuleConfig>().UseExternalTimelineHints = true;
ExternalTimelineHints.UseAbilitySync = sync == "all";
ExternalTimelineHints.OnlySource = source;
// ReplayParserLog.Parse swallows failures and reports them through Service.Log; surface them instead of silently yielding empty replays.
Service.LogHandlerDebug = msg =>
{
    if (msg.StartsWith("Failed to read", StringComparison.Ordinal))
        Console.Error.WriteLine(msg);
};

// The plugin only runs the follower when the active module's own state machine is trivial (ExternalTimelineHints.ShouldRun); with
// --gate the harness applies the same test per zone, using the module a registered duty would instantiate there.
HashSet<ushort> gatedZones = [];
if (gate)
{
    foreach (var info in BossModuleRegistry.RegisteredModules.Values)
    {
        if (info.GroupType != BossModuleInfo.GroupType.CFC)
            continue;
        var zone = Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(info.GroupID)?.TerritoryType.RowId;
        if (zone is not { } z || z == 0 || z > ushort.MaxValue || gatedZones.Contains((ushort)z))
            continue;
        try
        {
            using var module = BossModuleRegistry.CreateModuleForConfigPlanning(info.ModuleType);
            if (module == null)
                continue;
            if (!ExternalTimelineHints.ShouldRun(module))
                gatedZones.Add((ushort)z);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{info.ModuleType.Name}: {ex.Message}");
        }
    }
}

var files = inputs.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.log", SearchOption.AllDirectories) : [p]).ToList();
List<(string File, Replay Replay)> replays = [];
foreach (var file in files)
{
    try
    {
        var progress = 0f;
        var replay = ReplayParserLog.Parse(file, ref progress, CancellationToken.None);
        if (replay.Ops.Count > 0)
            replays.Add((file, replay));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{file}: {ex.Message}");
    }
}

var rows = new List<string> { "file,zone,pull,window_start,window_end,duration,predicted,lead,loss_error,return_error" };
var hits = 0; var windows = 0; var falseAlarms = 0; var errors = 0; var gatedWindows = 0;
List<float> leads = []; List<float> lossErrors = []; List<float> returnErrors = [];
// Return-time accuracy once a downtime has started: per long window (hit or not), the first publication from 1 s into the window
// until its end whose published loss is now (LossAt within 0.5 s of the tick), measured as its ReturnAt against the real end.
List<float> inWindowReturnErrors = [];

foreach (var (file, replay) in replays)
{
    List<ushort> overridden = [];
    try
    {
        if (holdout)
        {
            foreach (var (zone, timeline) in ReplayTimelineExtractor.Extract(replays.Where(r => r.File != file).Select(r => r.Replay)))
            {
                TimelineStore.SetCandidatesForTesting(zone, [timeline, .. TimelineStore.CandidatesForZone(zone).Where(t => t.Source != ExternalPlannerTimeline.TimelineSource.Replay)]);
                overridden.Add(zone);
            }
        }

        var player = new ReplayPlayer(replay);
        using var hints = new ExternalTimelineHints(player.WorldState);
        // (time, predicted loss at, predicted return at) for every tick that published a hint
        List<(DateTime At, DateTime LossAt, DateTime ReturnAt)> published = [];
        var wasGated = false;
        while (player.TickForward())
        {
            var ws = player.WorldState;
            var gated = gatedZones.Contains(ws.CurrentZone);
            if (gated)
            {
                if (!wasGated)
                    ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
                wasGated = true;
                continue;
            }
            wasGated = false;
            hints.Update(null);
            if (ExternalMechanicHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, ws.CurrentTime, MechanicHintSources.Timeline, out var snapshot) && snapshot.TargetLossIn < float.MaxValue)
                published.Add((ws.CurrentTime, ws.CurrentTime.AddSeconds(snapshot.TargetLossIn), ws.CurrentTime.AddSeconds(snapshot.TargetReturnIn)));
        }
        ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);

        var pulls = ReplayTimelineExtractor.FindPulls(replay);
        var pullIndex = 0;
        // truthLong drives hit/lead/error metrics; truthAll (every NoTarget window, however short) only excuses false alarms,
        // because a hint that lands on a real 5 s gap is early or imprecise, not invented.
        List<(DateTime Start, DateTime End)> truthLong = [];
        List<(DateTime Start, DateTime End)> truthAll = [];
        foreach (var pull in pulls)
        {
            var pullGated = gatedZones.Contains(pull.Zone);
            foreach (var w in ReplayTimelineExtractor.NoTargetWindows(pull))
            {
                var start = pull.Start.AddSeconds(w.Start);
                var end = pull.Start.AddSeconds(w.End);
                if (pullGated)
                {
                    // Nothing is published there, so the window is neither a scoring target nor an excuse for a false alarm.
                    if (w.End - w.Start >= LongLoss)
                        ++gatedWindows;
                    continue;
                }
                truthAll.Add((start, end));
                if (w.End - w.Start < LongLoss)
                    continue;
                truthLong.Add((start, end));
                ++windows;
                // the first publication before the window started whose predicted loss lands inside the window (with slack)
                var first = published.FirstOrDefault(p => p.At < start && Inside(p.LossAt, start, end));
                var predicted = first.At != default;
                if (predicted)
                {
                    ++hits;
                    var lead = Math.Min(LeadCap, (float)(start - first.At).TotalSeconds);
                    leads.Add(lead);
                    var last = published.Last(p => p.At < start && Inside(p.LossAt, start, end));
                    lossErrors.Add((float)(last.LossAt - start).TotalSeconds);
                    returnErrors.Add((float)(last.ReturnAt - end).TotalSeconds);
                }
                var inWindow = published.FirstOrDefault(p => p.At >= start.AddSeconds(1) && p.At < end && p.LossAt <= p.At.AddSeconds(0.5));
                if (inWindow.At != default)
                    inWindowReturnErrors.Add((float)(inWindow.ReturnAt - end).TotalSeconds);
                rows.Add(FormattableString.Invariant($"{Path.GetFileName(file)},{pull.Zone},{pullIndex},{w.Start:f1},{w.End:f1},{w.End - w.Start:f1},{(predicted ? 1 : 0)},{(predicted ? leads[^1] : -1):f1},{(predicted ? lossErrors[^1] : 0):f1},{(predicted ? returnErrors[^1] : 0):f1}"));
            }
            ++pullIndex;
        }
        // A false alarm is a predicted loss that neither falls inside any real window (same containment as the hit test) nor is
        // followed by one starting within FalseAlarmSeconds. Predictions are bucketed by 5 s of predicted time so one wrong call
        // republished every tick counts once.
        foreach (var group in published.GroupBy(p => (long)Math.Floor((p.LossAt - replay.Ops[0].Timestamp).TotalSeconds / 5)))
        {
            var p = group.First();
            if (!truthAll.Any(t => Inside(p.LossAt, t.Start, t.End) || t.Start >= p.LossAt.AddSeconds(-5) && t.Start <= p.LossAt.AddSeconds(FalseAlarmSeconds)))
            {
                ++falseAlarms;
                // False alarms are written with pull=-1 and the predicted loss/return as seconds from the start of the replay.
                var origin = replay.Ops[0].Timestamp;
                var zone = pulls.FirstOrDefault(pl => pl.Start <= p.At && p.At <= pl.End)?.Zone ?? pulls.LastOrDefault(pl => pl.Start <= p.At)?.Zone ?? 0;
                rows.Add(FormattableString.Invariant($"{Path.GetFileName(file)},{zone},-1,{(p.LossAt - origin).TotalSeconds:f1},{(p.ReturnAt - origin).TotalSeconds:f1},{(p.ReturnAt - p.LossAt).TotalSeconds:f1},0,{(p.LossAt - p.At).TotalSeconds:f1},0,0"));
            }
        }
    }
    catch (Exception ex)
    {
        ++errors;
        Console.Error.WriteLine($"{file}: {ex}");
    }
    finally
    {
        foreach (var zone in overridden)
            TimelineStore.SetCandidatesForTesting(zone, null);
    }
}

// A predicted loss counts as belonging to a window when it lands in [start - 5, end].
static bool Inside(DateTime lossAt, DateTime start, DateTime end) => lossAt >= start.AddSeconds(-5) && lossAt <= end;
float Median(List<float> v) => v.Count == 0 ? float.NaN : v.Order().ElementAt(v.Count / 2);
// Nearest-rank percentile: the smallest value at or above which 90% of the samples fall.
float P90(List<float> v) => v.Count == 0 ? float.NaN : v.Order().ElementAt(Math.Clamp((int)Math.Ceiling(v.Count * 0.9) - 1, 0, v.Count - 1));
var absLoss = lossErrors.Select(Math.Abs).ToList();
var absReturn = returnErrors.Select(Math.Abs).ToList();
var absInWindow = inWindowReturnErrors.Select(Math.Abs).ToList();
Console.WriteLine(FormattableString.Invariant($"source={source?.ToString() ?? "ranked"} sync={sync} holdout={holdout} gate={gate} timelines={timelinesDir ?? "-"} replays={replays.Count} windows={windows} hit={hits} hit_rate={(windows == 0 ? 0 : (float)hits / windows):f3} lead_median={Median(leads):f1} false_alarms={falseAlarms} loss_err_median={Median(absLoss):f1} loss_err_p90={P90(absLoss):f1} return_err_median={Median(absReturn):f1} return_err_p90={P90(absReturn):f1} errors={errors} gated_zones={gatedZones.Count} gated_windows={gatedWindows} inwin={inWindowReturnErrors.Count} inwin_return_err_median={Median(absInWindow):f1} inwin_return_err_p90={P90(absInWindow):f1}"));
if (csv != null)
    File.WriteAllLines(csv, rows);
return 0;
