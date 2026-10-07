using System.Text.Json;
using BossMod;

namespace FFLogsTimelineExtract;

// usage: FFLogsTimelineExtract --territory <id> --fflogs-zone <id> [--encounter <id>] [--reports N=10] [--max-fights M=20] [--out <dir>] [--self-test]
// Pulls kill fights of one territory from public FFLogs reports, rebuilds each as a synthetic Replay and runs the same
// ReplayTimelineExtractor as the replay tool, writing <territory>-fflogs.json into the user timeline folder.
public static class Program
{
    private const string Usage = "usage: FFLogsTimelineExtract --territory <id> --fflogs-zone <id> [--encounter <id>] [--reports N=10] [--max-fights M=20] [--out <dir>] [--self-test]\n"
        + "credentials: environment variables FFLOGS_CLIENT_ID / FFLOGS_CLIENT_SECRET";

    private const int ReportPageSize = 50; // ~35k of the 50k complexity budget with the fight fields above

    public static int Main(string[] args)
    {
        if (args.Contains("--self-test"))
            return SelfTest();
        try
        {
            return Run(args).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            Console.Error.WriteLine($"fflogs: {ex.Message}");
            return 1;
        }
    }

    // Kill fights in the requested territory (and encounter, when given), in listing order, at most maxFights of them.
    public static List<FFLogsFight> SelectFights(IReadOnlyList<FFLogsFight> fights, ushort territory, int maxFights, int? encounter = null)
        => fights.Where(f => f.Kill && f.Zone == territory && (encounter == null || f.EncounterID == encounter)).Take(maxFights).ToList();

    // Every non-boss gets the same flat max HP, so a trash-only cluster passes the extractor's "half of the biggest HP" boss rule
    // and becomes a sequence of its own. Only sequences that contain an actor FFLogs flagged as Boss are kept; with no flagged
    // actor at all (a report without subType data) everything is kept rather than nothing.
    public static ExternalPlannerTimeline.TimelineDefinition KeepBossSequences(ExternalPlannerTimeline.TimelineDefinition timeline, IReadOnlySet<uint> bossOIDs)
    {
        if (bossOIDs.Count == 0)
            return timeline;
        var kept = timeline.Sequences.Where(s => s.BossOIDs != null && s.BossOIDs.Any(bossOIDs.Contains)).Select((s, i) => s with { Index = i }).ToList();
        if (kept.Count == 0)
            return timeline with { Sequences = [] };
        // Branch.Group names the early sibling by its index, which the renumbering just changed: a pair kept whole points at its
        // early sibling's new index, and a sibling whose partner was dropped is no longer half of a branch.
        foreach (var group in kept.Where(s => s.Branch != null).GroupBy(s => s.Branch!.Group).ToList())
        {
            var siblings = group.ToList();
            var early = siblings.Where(s => s.Branch!.Below).ToList();
            var whole = siblings.Count == 2 && early.Count == 1;
            foreach (var sibling in siblings)
                kept[sibling.Index] = sibling with { Branch = whole ? sibling.Branch! with { Group = early[0].Index } : null };
        }
        // Same per-sequence confidence rule as ReplayTimelineExtractor.Build, averaged over what is left.
        var confidence = kept.Average(s => s.Windows is { Count: > 0 } w ? w.Average(x => x.Confidence) : s.States.Count == 0 ? 0.2f : 0.6f);
        return timeline with { Sequences = kept, Confidence = confidence };
    }

    // Extract always labels its output as Replay; the tool relabels it so the store ranks it below the user's own replays and edits.
    public static IReadOnlyDictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> Relabel(IReadOnlyDictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> extracted, string sourceFile)
        => extracted.ToDictionary(kv => kv.Key, kv => kv.Value with { Source = ExternalPlannerTimeline.TimelineSource.FFLogs, SourceFile = sourceFile });

    private static async Task<int> Run(string[] args)
    {
        ushort? territory = null;
        int? fflogsZone = null;
        int? encounter = null;
        var reports = 10;
        var maxFights = 20;
        string? outDir = null;
        try
        {
            for (var i = 0; i < args.Length; ++i)
            {
                switch (args[i])
                {
                    case "--territory": territory = ushort.Parse(args[++i]); break;
                    case "--fflogs-zone": fflogsZone = int.Parse(args[++i]); break;
                    case "--encounter": encounter = int.Parse(args[++i]); break;
                    case "--reports": reports = int.Parse(args[++i]); break;
                    case "--max-fights": maxFights = int.Parse(args[++i]); break;
                    case "--out": outDir = args[++i]; break;
                    default: Console.Error.WriteLine(Usage); return 2;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or OverflowException)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (territory == null || fflogsZone == null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        var clientId = Environment.GetEnvironmentVariable("FFLOGS_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("FFLOGS_CLIENT_SECRET");
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        outDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "BossModReborn", "timelines");

        using var client = new FFLogsClient(clientId, clientSecret);
        // A report is a whole play session, so the zone filter only picks reports that touched the zone; the encounter filter
        // (when given) trims each report's fight list server-side. The listing is paged because the API caps query complexity
        // (a 100-report page is over the limit), and it stops as soon as enough fights are in hand.
        List<FFLogsFight> all = [];
        var reportsSeen = 0;
        for (var page = 1; reportsSeen < reports; ++page)
        {
            var pageSize = Math.Min(ReportPageSize, reports - reportsSeen);
            var listing = await client.QueryAsync(
                "query($z:Int,$n:Int,$p:Int,$enc:Int){ reportData { reports(zoneID:$z, limit:$n, page:$p) { data { code startTime fights(encounterID:$enc) { id encounterID startTime endTime kill gameZone { id } } } } } }",
                new { z = fflogsZone.Value, n = pageSize, p = page, enc = encounter });
            var reportRows = listing.GetProperty("reportData").GetProperty("reports").GetProperty("data");
            foreach (var report in reportRows.EnumerateArray())
            {
                var code = report.GetProperty("code").GetString()!;
                var reportStart = (long)report.GetProperty("startTime").GetDouble();
                foreach (var fight in report.GetProperty("fights").EnumerateArray())
                {
                    var zone = fight.TryGetProperty("gameZone", out var gz) && gz.ValueKind == JsonValueKind.Object && gz.TryGetProperty("id", out var gzId) && gzId.ValueKind == JsonValueKind.Number ? gzId.GetInt32() : 0;
                    all.Add(new(code, fight.GetProperty("id").GetInt32(), reportStart, fight.GetProperty("startTime").GetDouble(), fight.GetProperty("endTime").GetDouble(),
                        (ushort)Math.Clamp(zone, 0, ushort.MaxValue), fight.GetProperty("kill").ValueKind == JsonValueKind.True)
                    { EncounterID = fight.TryGetProperty("encounterID", out var enc) && enc.ValueKind == JsonValueKind.Number ? enc.GetInt32() : 0 });
                }
            }
            reportsSeen += reportRows.GetArrayLength();
            if (reportRows.GetArrayLength() < pageSize || SelectFights(all, territory.Value, maxFights, encounter).Count >= maxFights)
                break;
        }
        var selected = SelectFights(all, territory.Value, maxFights, encounter);
        Console.WriteLine($"reports={reportsSeen} fights_listed={all.Count} fights_selected={selected.Count}");
        if (selected.Count == 0)
        {
            // Nothing matched: show what the listing actually contained so a wrong --territory/--encounter is obvious.
            foreach (var g in all.GroupBy(f => (f.Zone, f.EncounterID, f.Kill)).OrderByDescending(g => g.Count()).Take(10))
                Console.Error.WriteLine($"  listed: zone={g.Key.Zone} encounter={g.Key.EncounterID} kill={g.Key.Kill} fights={g.Count()}");
        }

        List<Replay> replays = [];
        List<string> sources = [];
        HashSet<uint> bossOIDs = [];
        foreach (var fight in selected)
        {
            // One unreadable fight (report withdrawn between listing and fetch, or an unexpected payload: a missing key, a null
            // sub-object under GetProperty) skips that fight only. Anything else (the API refusing or failing a request, the network)
            // would hit every remaining fight the same way, so the loop stops and the fights already fetched are still written.
            try
            {
                await FetchFight(client, fight, replays, sources, bossOIDs);
            }
            catch (FFLogsFightSkipped ex)
            {
                Console.WriteLine($"{fight.Code} fight={fight.Id} skipped: {ex.Message}");
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException || ex is InvalidOperationException and not FFLogsRequestFailed)
            {
                Console.WriteLine($"{fight.Code} fight={fight.Id} skipped: unexpected payload ({ex.Message})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"stopping after {replays.Count} fights: {ex.Message}");
                break;
            }
        }

        string points;
        try
        {
            // Best effort: everything above is already fetched, so a failed rate-limit read must not discard the run.
            points = (await client.PointsSpentAsync()).ToString();
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"rateLimitData unavailable: {ex.Message}");
            points = "?";
        }
        return Finish(territory.Value, outDir, replays, sources, bossOIDs, points);
    }

    private static async Task FetchFight(FFLogsClient client, FFLogsFight fight, List<Replay> replays, List<string> sources, HashSet<uint> bossOIDs)
    {
        var meta = await client.QueryAsync(
            "query($c:String!,$f:[Int]){ reportData { report(code:$c) { masterData { actors(type:\"NPC\") { id gameID name subType } } fights(fightIDs:$f) { enemyNPCs { id } } } } }",
            new { c = fight.Code, f = new[] { fight.Id } });
        var report = meta.GetProperty("reportData").GetProperty("report");
        if (report.ValueKind != JsonValueKind.Object)
            throw new FFLogsFightSkipped($"report {fight.Code} is not accessible");
        var enemyIds = report.GetProperty("fights").EnumerateArray().SelectMany(f => f.GetProperty("enemyNPCs").EnumerateArray()).Select(n => n.GetProperty("id").GetInt32()).ToHashSet();
        var actors = report.GetProperty("masterData").GetProperty("actors").EnumerateArray()
            .Where(a => enemyIds.Contains(a.GetProperty("id").GetInt32()))
            .Select(a => new FFLogsActor(a.GetProperty("id").GetInt32(), (uint)a.GetProperty("gameID").GetInt64(), a.GetProperty("name").GetString() ?? "", a.GetProperty("subType").GetString() == "Boss"))
            .ToList();
        var casts = await client.EventsAsync(fight, "Casts", null);
        var deaths = await client.EventsAsync(fight, "Deaths", null);
        var targetability = await client.EventsAsync(fight, "All", "type = 'targetabilityupdate'");
        // Player damage on enemies: the only way to tell an attackable enemy from a helper that was never targetable.
        var damageDone = await client.EventsAsync(fight, "DamageDone", null, "Friendlies");
        replays.Add(FFLogsReplayBuilder.Build(fight, actors, casts, deaths, targetability, damageDone));
        sources.Add($"fflogs:{fight.Code}:{fight.Id}");
        bossOIDs.UnionWith(actors.Where(a => a.IsBoss).Select(a => a.GameId));
        Console.WriteLine($"{fight.Code} fight={fight.Id} len={(fight.EndMs - fight.StartMs) / 1000:f0} enemies={actors.Count} casts={casts.GetArrayLength()} deaths={deaths.GetArrayLength()} targetability={targetability.GetArrayLength()} damage={damageDone.GetArrayLength()}");
    }

    private static int Finish(ushort territory, string outDir, List<Replay> replays, List<string> sources, HashSet<uint> bossOIDs, string points)
    {
        if (replays.Count == 0)
        {
            Console.WriteLine($"fights=0 sequences=0 windows=0 no_target=0 confidence=0.00 points_spent={points}");
            return 1;
        }
        var extracted = Relabel(ReplayTimelineExtractor.Extract(replays), string.Join(";", sources));
        if (!extracted.TryGetValue(territory, out var raw) || KeepBossSequences(raw, bossOIDs) is not { Sequences.Count: > 0 } timeline)
        {
            Console.Error.WriteLine($"no boss pull of territory {territory} survived extraction (zones found: {string.Join(",", extracted.Keys)})");
            Console.WriteLine($"fights={replays.Count} sequences=0 windows=0 no_target=0 confidence=0.00 points_spent={points}");
            return 1;
        }
        foreach (var s in timeline.Sequences)
            Console.WriteLine($"  seq={s.Index} bosses={string.Join(",", (s.BossOIDs ?? []).Select(o => $"0x{o:X}"))} states={s.States.Count} windows={s.Windows?.Count ?? 0}");
        foreach (var line in ReplayTimelineExtractor.DescribeBranches(timeline))
            Console.WriteLine(line);
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"{territory}-fflogs.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Timelines = new[] { timeline } }));
        // Round-trip check: the file must load through the same path the plugin uses for the user folder.
        var reloaded = TimelineStore.LoadUserFile(path);
        if (reloaded.Count != 1 || reloaded[0].ZoneID != territory || reloaded[0].Source != ExternalPlannerTimeline.TimelineSource.FFLogs)
            Console.Error.WriteLine($"{path}: LoadUserFile round-trip mismatch (loaded {reloaded.Count} timelines)");
        var windows = timeline.Sequences.SelectMany(s => s.Windows ?? []).ToList();
        Console.WriteLine($"fights={replays.Count} sequences={timeline.Sequences.Count} windows={windows.Count} no_target={windows.Count(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget)} confidence={timeline.Confidence:f2} points_spent={points} -> {path}");
        return 0;
    }

    static int SelfTest()
    {
        var failures = new List<string>(); var tests = 0;
        void Check(string name, Action test) { ++tests; try { test(); } catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); } }
        void Require(bool c, string m) { if (!c) throw new InvalidOperationException(m); }
        var fight = new FFLogsFight("TEST", 1, 1_700_000_000_000, 10_000, 400_000, 1203, true);
        var actors = new List<FFLogsActor> { new(58, 0x4234, "Barreltender", true), new(65, 0x41BE, "Anthracite", true), new(70, 0x4B78, "Petromole", false), new(90, 0x4B7A, "Helper", false) };
        JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
        // two bosses in one fight: boss A acts 20-100 s, boss B 200-380 s; trash 12-18 s; boss B vanishes 250-270 (targetability), trash instance 2 dies at 300;
        // a never-targetable helper (no damage taken, no death, no update) casts inside the vanish window and must not block it
        var casts = J("""
          [
          {"timestamp":255000,"type":"cast","sourceID":90,"abilityGameID":4000},{"timestamp":265000,"type":"cast","sourceID":90,"abilityGameID":4000},
          {"timestamp":22000,"type":"begincast","sourceID":58,"abilityGameID":1000,"duration":3000},{"timestamp":25000,"type":"cast","sourceID":58,"abilityGameID":1000},
          {"timestamp":60000,"type":"begincast","sourceID":58,"abilityGameID":1001,"duration":2000},{"timestamp":62000,"type":"cast","sourceID":58,"abilityGameID":1001},
          {"timestamp":205000,"type":"begincast","sourceID":65,"abilityGameID":2000,"duration":3000},{"timestamp":208000,"type":"cast","sourceID":65,"abilityGameID":2000},
          {"timestamp":300000,"type":"begincast","sourceID":65,"abilityGameID":2001,"duration":3000},{"timestamp":303000,"type":"cast","sourceID":65,"abilityGameID":2001},
          {"timestamp":13000,"type":"cast","sourceID":70,"sourceInstance":1,"abilityGameID":7},
          {"timestamp":290000,"type":"cast","sourceID":70,"sourceInstance":2,"abilityGameID":7}]
          """);
        var deaths = J("""
          [
          {"timestamp":100000,"type":"death","targetID":58},
          {"timestamp":18000,"type":"death","targetID":70,"targetInstance":1},
          {"timestamp":300000,"type":"death","targetID":70,"targetInstance":2},
          {"timestamp":380000,"type":"death","targetID":65}]
          """);
        var targetability = J("""
          [
          {"timestamp":250000,"type":"targetabilityupdate","sourceID":65,"targetID":65,"targetable":0},
          {"timestamp":270000,"type":"targetabilityupdate","sourceID":65,"targetID":65,"targetable":1}]
          """);
        // player damage: both bosses and both trash instances are hit, the helper never is
        var damageDone = J("""
          [
          {"timestamp":30000,"type":"damage","sourceID":1,"targetID":58,"abilityGameID":100,"amount":500},
          {"timestamp":210000,"type":"damage","sourceID":1,"targetID":65,"abilityGameID":100,"amount":500},
          {"timestamp":14000,"type":"damage","sourceID":1,"targetID":70,"targetInstance":1,"abilityGameID":100,"amount":500},
          {"timestamp":292000,"type":"damage","sourceID":1,"targetID":70,"targetInstance":2,"abilityGameID":100,"amount":500}]
          """);
        var replay = FFLogsReplayBuilder.Build(fight, actors, casts, deaths, targetability, damageDone);
        Check("participants: one per actor instance, boss HP marks bosses", () =>
        {
            Require(replay.Participants.Count == 5, $"participants={replay.Participants.Count}");
            var a = replay.Participants.Single(p => p.OID == 0x4234);
            Require(a.HPMPHistory.Values.First().MaxHP == 1_000_000 && replay.Participants.Single(p => p.OID == 0x4B78 && p.InstanceID != replay.Participants.First(q => q.OID == 0x4B78).InstanceID) != null, "boss hp / instances");
            Require(a.ZoneID == 1203, "zone");
        });
        Check("instances are separate participants with their own death", () =>
        {
            var trash = replay.Participants.Where(p => p.OID == 0x4B78).OrderBy(p => p.WorldExistence[0].Start).ToList();
            Require(trash.Count == 2, "two instances");
            Require(trash[0].DeadHistory.Keys.Single() == fight.ToTime(18000) && trash[1].DeadHistory.Keys.Single() == fight.ToTime(300000), "deaths per instance");
        });
        Check("no updates means targetable for the whole existence", () =>
        {
            var a = replay.Participants.Single(p => p.OID == 0x4234);
            Require(a.TargetableHistory.Count == 1 && a.TargetableHistory.Values[0], "targetable from spawn");
            Require(a.WorldExistence[0].Start == fight.ToTime(22000) && a.WorldExistence[0].End == fight.ToTime(100000), "existence = first event .. death");
        });
        Check("targetability updates and casts/actions land on the boss", () =>
        {
            var b = replay.Participants.Single(p => p.OID == 0x41BE);
            Require(b.TargetableHistory.Count == 3 && !b.TargetableAt(fight.ToTime(260000)) && b.TargetableAt(fight.ToTime(275000)), "targetability");
            Require(b.Casts.Count == 2 && b.Casts[0].ID.ID == 2000 && Math.Abs(b.Casts[0].ExpectedCastTime - 3f) < 0.01f, "casts");
            Require(replay.Actions.Count(x => x.Source == b) == 2, "actions");
        });
        Check("dungeon fight splits into boss pulls and Extract yields two sequences with the vanish window", () =>
        {
            var pulls = ReplayTimelineExtractor.FindPulls(replay);
            Require(pulls.Count == 2, $"pulls={pulls.Count}");
            var tl = Relabel(ReplayTimelineExtractor.Extract([replay]), "fflogs:TEST:1")[1203];
            Require(tl.Source == ExternalPlannerTimeline.TimelineSource.FFLogs, "source");
            Require(tl.Sequences.Count == 2 && tl.Sequences[1].BossOIDs!.SequenceEqual([0x41BEu]), "sequences");
            Require(tl.Sequences[1].Windows!.Any(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget && Math.Abs(w.Start - 45) < 0.5 && Math.Abs(w.End!.Value - 65) < 0.5), "vanish window relative to first boss cast at 205 s");
        });
        Check("an enemy without a death ends at its last event instead of bridging to the fight end", () =>
        {
            // A helper that casts once at 120 s and never dies must not connect boss A's pull to boss B's.
            var helperActors = actors.Concat([new FFLogsActor(80, 0x4B79, "Helper", false)]).ToList();
            var helperCasts = J("""
              [{"timestamp":120000,"type":"cast","sourceID":80,"abilityGameID":3000}]
              """);
            var merged = JsonSerializer.SerializeToElement(casts.EnumerateArray().Concat(helperCasts.EnumerateArray()).ToList());
            var r = FFLogsReplayBuilder.Build(fight, helperActors, merged, deaths, targetability, damageDone);
            var helper = r.Participants.Single(p => p.OID == 0x4B79);
            Require(helper.WorldExistence[0].Start == fight.ToTime(120000) && helper.WorldExistence[0].End == fight.ToTime(120000), "existence = first .. last event");
            Require(ReplayTimelineExtractor.FindPulls(r).Count == 2, "helper does not bridge the boss pulls");
        });
        Check("trash-only sequences are dropped, boss sequences renumbered", () =>
        {
            // Trash with the same flat HP passes the extractor's boss rule; only sequences with a Boss-flagged OID survive.
            var trashSeq = new ExternalPlannerTimeline.TimelineSequence(0, 0, [], null, [0x4B78u], []);
            var bossSeq = new ExternalPlannerTimeline.TimelineSequence(1, 0, [new(0, "", ExternalPlannerTimeline.ExternalStateKind.CastStart, [2000], 0)], null, [0x41BEu], []);
            var tl = new ExternalPlannerTimeline.TimelineDefinition(1203, "x", true, [trashSeq, bossSeq], ExternalPlannerTimeline.TimelineSource.FFLogs, 0.4f);
            var kept = KeepBossSequences(tl, new HashSet<uint> { 0x41BE });
            Require(kept.Sequences.Count == 1 && kept.Sequences[0].Index == 0 && kept.Sequences[0].BossOIDs!.SequenceEqual([0x41BEu]), "kept the boss sequence");
            Require(Math.Abs(kept.Confidence - 0.6f) < 0.001f, $"confidence={kept.Confidence}");
            Require(KeepBossSequences(tl, new HashSet<uint>()).Sequences.Count == 2, "no flagged boss keeps everything");
        });
        Check("branch groups follow the renumbering; a sibling left alone loses its branch", () =>
        {
            ExternalPlannerTimeline.TimelineSequence Seq(int index, uint oid, ExternalPlannerTimeline.TimelineBranch? branch = null)
                => new(index, 0, [new(0, "", ExternalPlannerTimeline.ExternalStateKind.CastStart, [2000 + (uint)index], 0)], null, [oid], [], branch);
            // trash(0), then an HP-gated boss as siblings 1 and 2 (group 1), then another boss.
            var tl = new ExternalPlannerTimeline.TimelineDefinition(1203, "x", true,
                [Seq(0, 0x4B78), Seq(1, 0x41BE, new(1, 94.6f, 46.4f, true)), Seq(2, 0x41BE, new(1, 94.6f, 46.4f, false)), Seq(3, 0x4234)], ExternalPlannerTimeline.TimelineSource.FFLogs, 0.4f);
            var keptTimeline = KeepBossSequences(tl, new HashSet<uint> { 0x41BE, 0x4234 });
            var kept = keptTimeline.Sequences;
            Require(kept.Count == 3 && kept.Select(s => s.Index).SequenceEqual([0, 1, 2]), $"kept={kept.Count}");
            // The printed branch line names the renumbered group; siblings without windows have no window start.
            var lines = ReplayTimelineExtractor.DescribeBranches(keptTimeline).ToList();
            Require(lines.Count == 1 && lines[0] == "branch group=0 boss=0x41BE decision=94.60 threshold=46.40 early=- late=-", $"branch lines: {string.Join(" | ", lines)}");
            Require(kept[0].Branch is { Group: 0, Below: true } && kept[1].Branch is { Group: 0, Below: false } && kept[2].Branch == null, $"groups: {kept[0].Branch} / {kept[1].Branch}");
            Require(kept[0].Branch!.DecisionTime == 94.6f && kept[0].Branch!.HpThreshold == 46.4f, "branch values kept");
            // Only one sibling carries a flagged boss: the survivor is no longer half of a branch.
            var split = new ExternalPlannerTimeline.TimelineDefinition(1203, "x", true,
                [Seq(0, 0x4B78), Seq(1, 0x41BE, new(1, 94.6f, 46.4f, true)), Seq(2, 0x4B79, new(1, 94.6f, 46.4f, false))], ExternalPlannerTimeline.TimelineSource.FFLogs, 0.4f);
            var alone = KeepBossSequences(split, new HashSet<uint> { 0x41BE }).Sequences;
            Require(alone.Count == 1 && alone[0].Index == 0 && alone[0].Branch == null, $"lone sibling: count={alone.Count} branch={alone.FirstOrDefault()?.Branch}");
        });
        Check("an enemy that never took player damage is an untargetable helper", () =>
        {
            var helper = replay.Participants.Single(p => p.OID == 0x4B7A);
            Require(helper.TargetableHistory.Count == 1 && !helper.TargetableHistory.Values[0] && !helper.IsTargetOfAnyActions, "helper untargetable from spawn");
            Require(helper.WorldExistence[0].Start == fight.ToTime(255000) && helper.WorldExistence[0].End == fight.ToTime(265000), "helper existence");
            Require(!helper.TargetableAt(fight.ToTime(260000)), "helper stays untargetable");
        });
        Check("an enemy with damage taken but no updates stays targetable", () =>
        {
            foreach (var trash in replay.Participants.Where(p => p.OID == 0x4B78))
                Require(trash.TargetableHistory.Count == 1 && trash.TargetableHistory.Values[0] && trash.IsTargetOfAnyActions, "trash targetable");
            var a = replay.Participants.Single(p => p.OID == 0x4234);
            Require(a.IsTargetOfAnyActions && a.TargetableAt(fight.ToTime(90000)), "damaged boss targetable");
        });
        Check("a boss that never took damage is still attackable", () =>
        {
            var r = FFLogsReplayBuilder.Build(fight, actors, casts, deaths, targetability, J("[]"));
            var a = r.Participants.Single(p => p.OID == 0x4234);
            Require(a.IsTargetOfAnyActions && a.TargetableHistory.Values[0], "boss safety");
            Require(r.Participants.Where(p => p.OID == 0x4B78).All(p => !p.TargetableHistory.Values[0]), "undamaged trash is a helper");
        });
        Check("event paging stops on a cursor that does not advance, at the fight end, and at the page cap", () =>
        {
            Require(FFLogsClient.ShouldContinuePaging(1000, 2000, 1, 400_000), "advancing cursor continues");
            Require(!FFLogsClient.ShouldContinuePaging(1000, null, 1, 400_000), "no cursor stops");
            Require(!FFLogsClient.ShouldContinuePaging(1000, 1000, 1, 400_000), "repeated cursor stops");
            Require(!FFLogsClient.ShouldContinuePaging(1000, 500, 1, 400_000), "backwards cursor stops");
            Require(!FFLogsClient.ShouldContinuePaging(1000, 400_000, 1, 400_000), "cursor at fight end stops");
            Require(FFLogsClient.ShouldContinuePaging(1000, 2000, 49, 400_000) && !FFLogsClient.ShouldContinuePaging(1000, 2000, 50, 400_000), "page cap at 50");
        });
        Check("paging stop reasons name the cause; only a missing cursor is the normal end", () =>
        {
            Require(FFLogsClient.PagingStopReason(1000, 2000, 1, 400_000) == null, "advancing cursor has no stop reason");
            Require(FFLogsClient.PagingStopReason(1000, null, 1, 400_000) == FFLogsClient.NoNextCursor, "missing cursor is the normal end");
            Require(FFLogsClient.PagingStopReason(1000, 1000, 1, 400_000) is { } repeated && repeated != FFLogsClient.NoNextCursor && repeated.Contains("advance"), "repeated cursor reason");
            Require(FFLogsClient.PagingStopReason(1000, 500, 1, 400_000) is { } backwards && backwards.Contains("backwards"), "backwards cursor reason");
            Require(FFLogsClient.PagingStopReason(1000, 400_000, 1, 400_000) is { } atEnd && atEnd.Contains("fight end"), "fight end reason");
            Require(FFLogsClient.PagingStopReason(1000, 2000, 50, 400_000) is { } cap && cap.Contains("page cap"), "page cap reason");
        });
        Check("skip and stop exceptions are told apart", () =>
        {
            Exception skipped = new FFLogsFightSkipped("x");
            Exception failed = new FFLogsRequestFailed("x");
            Require(skipped is not InvalidOperationException, "a skipped fight must not look like a request failure");
            Require(failed is InvalidOperationException, "a request failure is reported by the top-level handler");
            // A null sub-object under GetProperty is the payload shape the per-fight skip must catch.
            var nullReport = JsonDocument.Parse("""{"report":null}""").RootElement.GetProperty("report");
            try { nullReport.GetProperty("fights"); throw new Exception("GetProperty on null did not throw"); }
            catch (InvalidOperationException ex) { Require(ex is not FFLogsRequestFailed, "json access failure is a skip, not a stop"); }
        });
        Check("no kill fights -> no output", () => Require(SelectFights([new FFLogsFight("X", 1, 0, 0, 1000, 1203, false)], 1203, 20).Count == 0, "wipe kept"));
        foreach (var f in failures) Console.Error.WriteLine(f);
        Console.WriteLine($"tests={tests} passed={tests - failures.Count} failed={failures.Count}; source=synthetic; replay=none");
        return failures.Count == 0 ? 0 : 1;
    }
}
