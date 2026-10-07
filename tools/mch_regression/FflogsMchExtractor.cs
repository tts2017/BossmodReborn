using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MchRegression;

public sealed record MchLogActionEvent(
    string ReportId,
    int FightId,
    int SourceId,
    string ActionName,
    double FightTime,
    double PhaseTime,
    string Phase,
    bool Targetable,
    string Note);

public sealed record MchLogMitigationEvent(
    string ReportId,
    int FightId,
    int SourceId,
    string ActionName,
    double FightTime,
    double PhaseTime,
    string Phase,
    bool TargetRequired,
    string Note);

public sealed record MchProfileEntry(
    string ActionName,
    double MedianTime,
    double P25,
    double P75,
    double UsageRate,
    string FightPhase,
    string Precondition,
    string Note);

public static partial class FflogsMchExtractor
{
    private const int DancingMadEncounterId = 1085;
    private const int DancingMadZoneId = 76;
    private const int DancingMadDifficulty = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly HashSet<string> IgnoredBasicCombo = new(StringComparer.OrdinalIgnoreCase)
    {
        "Split Shot",
        "Slug Shot",
        "Clean Shot",
        "Heated Split Shot",
        "Heated Slug Shot",
        "Heated Clean Shot"
    };

    private static readonly HashSet<string> OffensiveActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Reassemble",
        "Drill",
        "Bioblaster",
        "Air Anchor",
        "Chain Saw",
        "Excavator",
        "Full Metal Field",
        "Barrel Stabilizer",
        "Wildfire",
        "Detonator",
        "Hypercharge",
        "Heat Blast",
        "Blazing Shot",
        "Auto Crossbow",
        "Automaton Queen",
        "Rook Autoturret",
        "Queen Overdrive",
        "Gauss Round",
        "Ricochet",
        "Double Check",
        "Checkmate",
        "Potion",
        "Grade 2 Gemdraught of Dexterity",
        "Grade 3 Gemdraught of Dexterity"
    };

    private static readonly HashSet<string> MitigationActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Tactician",
        "Dismantle"
    };

    private static readonly Dictionary<uint, string> ActionNamesByGameId = new()
    {
        [2864] = "Rook Autoturret",
        [2866] = "Split Shot",
        [2868] = "Slug Shot",
        [2870] = "Spread Shot",
        [2872] = "Hot Shot",
        [2873] = "Clean Shot",
        [2874] = "Gauss Round",
        [2876] = "Reassemble",
        [2878] = "Wildfire",
        [2887] = "Dismantle",
        [2890] = "Ricochet",
        [7410] = "Heat Blast",
        [7411] = "Heated Split Shot",
        [7412] = "Heated Slug Shot",
        [7413] = "Heated Clean Shot",
        [7414] = "Barrel Stabilizer",
        [7415] = "Rook Overdrive",
        [7418] = "Flamethrower",
        [16497] = "Auto Crossbow",
        [16498] = "Drill",
        [16499] = "Bioblaster",
        [16500] = "Air Anchor",
        [16501] = "Automaton Queen",
        [16502] = "Queen Overdrive",
        [16766] = "Detonator",
        [16889] = "Tactician",
        [17209] = "Hypercharge",
        [25786] = "Scattergun",
        [25788] = "Chain Saw",
        [36978] = "Blazing Shot",
        [36979] = "Double Check",
        [36980] = "Checkmate",
        [36981] = "Excavator",
        [36982] = "Full Metal Field"
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var options = ExtractOptions.Parse(args);
        var reports = options.ReportsFile == null ? [] : ReadReportRefs(options.ReportsFile).ToList();
        if (reports.Count == 0 && options.ReportRefs.Count > 0)
            reports.AddRange(options.ReportRefs);

        var token = await ResolveAccessToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("FFLogs extraction skipped: set FFLOGS_ACCESS_TOKEN or FFLOGS_CLIENT_ID + FFLOGS_CLIENT_SECRET.");
            Console.WriteLine($"Queued reports: {reports.Count}");
            Console.WriteLine("Extracted logs: 0");
            return 0;
        }

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await VerifySchema(http);

        if (reports.Count == 0 && options.Top > 0)
            reports.AddRange(await FetchRankedReports(http, options));

        if (reports.Count == 0)
        {
            Console.WriteLine("FFLogs extraction skipped: no reports were provided or discovered. Use --reports <file>, --report <row>, or --top <n> with --encounter and --job.");
            Console.WriteLine("Extracted logs: 0");
            return 0;
        }

        var actionEvents = new List<MchLogActionEvent>();
        var mitigationEvents = new List<MchLogMitigationEvent>();
        foreach (var report in reports)
        {
            var events = await FetchCastEvents(http, report);
            foreach (var ev in events)
            {
                if (IgnoredBasicCombo.Contains(ev.Name))
                    continue;

                var phase = PhaseFromTime(ev.FightTime);
                if (OffensiveActions.Contains(ev.Name))
                    actionEvents.Add(new(report.ReportId, report.FightId, report.SourceId, ev.Name, ev.FightTime, PhaseTime(ev.FightTime), phase, true, "fflogs cast"));
                if (MitigationActions.Contains(ev.Name))
                    mitigationEvents.Add(new(report.ReportId, report.FightId, report.SourceId, ev.Name, ev.FightTime, PhaseTime(ev.FightTime), phase, ev.Name.Equals("Dismantle", StringComparison.OrdinalIgnoreCase), "fflogs mitigation"));
            }
        }

        var outDir = options.OutputDirectory ?? Path.Combine("tools", "mch_regression", "fflogs_out");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "mch_action_events.json"), JsonSerializer.Serialize(actionEvents, JsonOptions));
        File.WriteAllText(Path.Combine(outDir, "mch_mitigation_events.json"), JsonSerializer.Serialize(mitigationEvents, JsonOptions));
        File.WriteAllText(Path.Combine(outDir, "dancing_mad_mch_profile.generated.json"), JsonSerializer.Serialize(MchDancingMadProfileBuilder.Build(actionEvents, mitigationEvents, reports.Count), JsonOptions));

        Console.WriteLine($"Queued reports: {reports.Count}");
        Console.WriteLine($"Extracted action events: {actionEvents.Count}");
        Console.WriteLine($"Extracted mitigation events: {mitigationEvents.Count}");
        Console.WriteLine($"Output: {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static async Task<IReadOnlyList<ReportRef>> FetchRankedReports(HttpClient http, ExtractOptions options)
    {
        var encounterId = EncounterId(options.Encounter);
        var job = JobName(options.Job);
        var top = Math.Clamp(options.Top, 1, 100);
        var partitions = await FetchDefaultPartitions(http);
        var rankings = new List<RankedLog>();

        foreach (var partition in partitions)
            rankings.AddRange(await FetchCharacterRankings(http, encounterId, job, partition));

        var result = new List<ReportRef>();
        var seenPlayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFights = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ranking in rankings
            .OrderByDescending(r => r.Amount)
            .ThenBy(r => r.ReportId, StringComparer.Ordinal)
            .ThenBy(r => r.FightId)
            .ThenBy(r => r.PlayerName, StringComparer.OrdinalIgnoreCase))
        {
            if (result.Count >= top)
                break;

            if (!seenPlayers.Add($"{ranking.ReportId}:{ranking.FightId}:{ranking.PlayerName}"))
                continue;

            if (!seenFights.Add($"{ranking.ReportId}:{ranking.FightId}"))
                continue;

            var sourceId = await ResolveSourceId(http, ranking, job);
            if (sourceId == null)
                continue;

            result.Add(new(ranking.ReportId, ranking.FightId, sourceId.Value));
        }

        Console.WriteLine($"Discovered ranked logs: {rankings.Count}");
        Console.WriteLine($"Selected ranked reports: {result.Count}");
        return result;
    }

    private static int EncounterId(string encounter)
    {
        if (encounter.Equals("Dancing Mad", StringComparison.OrdinalIgnoreCase)
            || encounter.Equals("DMU", StringComparison.OrdinalIgnoreCase)
            || encounter.Equals("絶妖星乱舞", StringComparison.OrdinalIgnoreCase))
            return DancingMadEncounterId;

        if (int.TryParse(encounter, out var id))
            return id;

        throw new ArgumentException($"Unsupported FFLogs encounter '{encounter}'.");
    }

    private static string JobName(string job)
        => job.Equals("MCH", StringComparison.OrdinalIgnoreCase) ? "Machinist" : job;

    private static async Task<IReadOnlyList<int>> FetchDefaultPartitions(HttpClient http)
    {
        const string query = """
        query {
          worldData {
            zone(id: 76) {
              partitions {
                id
                name
                default
              }
            }
          }
        }
        """;

        using var doc = await GraphQl(http, query, new { });
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("worldData", out var worldData)
            || !worldData.TryGetProperty("zone", out var zone)
            || !zone.TryGetProperty("partitions", out var partitions)
            || partitions.ValueKind != JsonValueKind.Array)
            return [1];

        var defaults = partitions.EnumerateArray()
            .Where(p => p.TryGetProperty("default", out var d) && d.GetBoolean())
            .Select(p => p.TryGetProperty("id", out var id) ? id.GetInt32() : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
        if (defaults.Count > 0)
            return defaults;

        return partitions.EnumerateArray()
            .Select(p => p.TryGetProperty("id", out var id) ? id.GetInt32() : 0)
            .Where(id => id > 0)
            .DefaultIfEmpty(1)
            .OrderDescending()
            .Take(1)
            .ToList();
    }

    private static async Task<IReadOnlyList<RankedLog>> FetchCharacterRankings(HttpClient http, int encounterId, string job, int partition)
    {
        const string query = """
        query($id: Int!, $difficulty: Int!, $partition: Int!, $job: String!) {
          worldData {
            encounter(id: $id) {
              characterRankings(page: 1, difficulty: $difficulty, partition: $partition, metric: rdps, specName: $job)
            }
          }
        }
        """;

        using var doc = await GraphQl(http, query, new { id = encounterId, difficulty = DancingMadDifficulty, partition, job });
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("worldData", out var worldData)
            || !worldData.TryGetProperty("encounter", out var encounter)
            || !encounter.TryGetProperty("characterRankings", out var rankingRoot)
            || !rankingRoot.TryGetProperty("rankings", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<RankedLog>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("name", out var nameNode)
                || !row.TryGetProperty("report", out var reportNode)
                || !reportNode.TryGetProperty("code", out var codeNode)
                || !reportNode.TryGetProperty("fightID", out var fightNode))
                continue;

            var name = nameNode.GetString();
            var code = codeNode.GetString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(code))
                continue;

            var amount = row.TryGetProperty("amount", out var amountNode) ? amountNode.GetDouble() : 0;
            result.Add(new(code, fightNode.GetInt32(), name, amount, partition));
        }
        return result;
    }

    private static async Task<int?> ResolveSourceId(HttpClient http, RankedLog ranking, string job)
    {
        const string query = """
        query($code: String!) {
          reportData {
            report(code: $code) {
              masterData {
                actors {
                  id
                  name
                  subType
                  type
                }
              }
            }
          }
        }
        """;

        using var doc = await GraphQl(http, query, new { code = ranking.ReportId });
        if (!doc.RootElement.TryGetProperty("data", out var data)
            || !data.TryGetProperty("reportData", out var reportData)
            || !reportData.TryGetProperty("report", out var report)
            || !report.TryGetProperty("masterData", out var masterData)
            || !masterData.TryGetProperty("actors", out var actors)
            || actors.ValueKind != JsonValueKind.Array)
            return null;

        var matchingActors = actors.EnumerateArray()
            .Where(a => MatchesJob(a, job))
            .ToList();
        var exact = matchingActors.FirstOrDefault(a => a.TryGetProperty("name", out var n) && string.Equals(n.GetString(), ranking.PlayerName, StringComparison.OrdinalIgnoreCase));
        if (exact.ValueKind != JsonValueKind.Undefined && exact.TryGetProperty("id", out var exactId))
            return exactId.GetInt32();

        if (matchingActors.Count == 1 && matchingActors[0].TryGetProperty("id", out var onlyId))
            return onlyId.GetInt32();

        return null;
    }

    private static bool MatchesJob(JsonElement actor, string job)
        => actor.TryGetProperty("type", out var type)
        && type.GetString()?.Equals("Player", StringComparison.OrdinalIgnoreCase) == true
        && actor.TryGetProperty("subType", out var subtype)
        && subtype.GetString()?.Equals(job, StringComparison.OrdinalIgnoreCase) == true;

    private static async Task VerifySchema(HttpClient http)
    {
        var query = """query { __schema { queryType { name } } }""";
        using var doc = await GraphQl(http, query, new { });
        if (!doc.RootElement.TryGetProperty("data", out _))
            throw new InvalidOperationException("FFLogs GraphQL schema introspection failed.");
    }

    private static async Task<IReadOnlyList<RawCastEvent>> FetchCastEvents(HttpClient http, ReportRef report)
    {
        const string query = """
        query($code: String!, $fightIDs: [Int]!, $sourceID: Int!) {
          reportData {
            report(code: $code) {
              fights(fightIDs: $fightIDs) {
                id
                startTime
              }
              events(fightIDs: $fightIDs, sourceID: $sourceID, dataType: Casts, limit: 10000) {
                data
              }
            }
          }
        }
        """;

        using var doc = await GraphQl(http, query, new { code = report.ReportId, fightIDs = new[] { report.FightId }, sourceID = report.SourceId });
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return [];

        if (!data.TryGetProperty("reportData", out var reportData)
            || !reportData.TryGetProperty("report", out var reportNode)
            || !reportNode.TryGetProperty("events", out var eventsNode)
            || !eventsNode.TryGetProperty("data", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
            return [];

        var start = 0.0;
        if (reportNode.TryGetProperty("fights", out var fights)
            && fights.ValueKind == JsonValueKind.Array)
            start = fights.EnumerateArray()
                .Select(f => f.TryGetProperty("startTime", out var st) ? st.GetDouble() : 0)
                .FirstOrDefault(t => t > 0);
        if (start <= 0)
            start = rows.EnumerateArray().Select(e => e.TryGetProperty("timestamp", out var ts) ? ts.GetDouble() : 0).Where(t => t > 0).DefaultIfEmpty(0).Min();

        var result = new List<RawCastEvent>();
        foreach (var row in rows.EnumerateArray())
        {
            var timestamp = row.TryGetProperty("timestamp", out var ts) ? ts.GetDouble() : 0;
            var name = EventActionName(row);
            if (string.IsNullOrWhiteSpace(name))
                continue;
            result.Add(new(name, (timestamp - start) / 1000.0));
        }
        return result;
    }

    private static string EventActionName(JsonElement row)
    {
        if (row.TryGetProperty("ability", out var ability)
            && ability.TryGetProperty("name", out var actionName))
            return actionName.GetString() ?? "";

        if (row.TryGetProperty("abilityGameID", out var id)
            && id.TryGetUInt32(out var gameId)
            && ActionNamesByGameId.TryGetValue(gameId, out var name))
            return name;

        return "";
    }

    private static async Task<JsonDocument> GraphQl(HttpClient http, string query, object variables)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("https://www.fflogs.com/api/v2/client", content);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> ResolveAccessToken()
    {
        var existing = Environment.GetEnvironmentVariable("FFLOGS_ACCESS_TOKEN");
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var clientId = Environment.GetEnvironmentVariable("FFLOGS_CLIENT_ID");
        var clientSecret = Environment.GetEnvironmentVariable("FFLOGS_CLIENT_SECRET");
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return null;

        using var http = new HttpClient();
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
        using var response = await http.PostAsync("https://www.fflogs.com/oauth/token", content);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("access_token", out var token) ? token.GetString() : null;
    }

    private static IEnumerable<ReportRef> ReadReportRefs(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;
            if (TryParseReportRef(trimmed, out var report))
                yield return report;
        }
    }

    private static bool TryParseReportRef(string text, out ReportRef report)
    {
        var url = ReportUrlRegex().Match(text);
        if (url.Success)
        {
            report = new(url.Groups["report"].Value, int.Parse(url.Groups["fight"].Value), int.Parse(url.Groups["source"].Value));
            return true;
        }

        var parts = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && int.TryParse(parts[1], out var fight) && int.TryParse(parts[2], out var source))
        {
            report = new(parts[0], fight, source);
            return true;
        }

        report = default;
        return false;
    }

    private static string PhaseFromTime(double time)
        => time switch
        {
            < 197 => "opener-p1",
            < 320 => "phase-resume",
            < 430 => "final-setup",
            _ => "final"
        };

    private static double PhaseTime(double time)
        => time switch
        {
            < 197 => time,
            < 320 => time - 197,
            < 430 => time - 320,
            _ => time - 430
        };

    [GeneratedRegex(@"fflogs\.com/reports/(?<report>[A-Za-z0-9]+).*?[#&?]fight=(?<fight>\d+).*?[&?]source=(?<source>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReportUrlRegex();

    private readonly record struct RankedLog(string ReportId, int FightId, string PlayerName, double Amount, int Partition);

    private readonly record struct RawCastEvent(string Name, double FightTime);

    private sealed record ExtractOptions(string? ReportsFile, string? OutputDirectory, List<ReportRef> ReportRefs, string Encounter, string Job, int Top)
    {
        public static ExtractOptions Parse(string[] args)
        {
            string? reports = null;
            string? output = null;
            var encounter = "Dancing Mad";
            var job = "Machinist";
            var top = 0;
            var refs = new List<ReportRef>();
            for (var i = 0; i < args.Length; ++i)
            {
                switch (args[i])
                {
                    case "--reports":
                        reports = Require(args, ref i, "--reports");
                        break;
                    case "--out":
                        output = Require(args, ref i, "--out");
                        break;
                    case "--encounter":
                        encounter = Require(args, ref i, "--encounter");
                        break;
                    case "--job":
                        job = Require(args, ref i, "--job");
                        break;
                    case "--top":
                        if (!int.TryParse(Require(args, ref i, "--top"), out top))
                            throw new ArgumentException("Invalid value for --top.");
                        break;
                    case "--report":
                        var raw = Require(args, ref i, "--report");
                        if (TryParseReportRef(raw, out var parsed))
                            refs.Add(parsed);
                        break;
                }
            }
            return new(reports, output, refs, encounter, job, top);
        }

        private static string Require(string[] args, ref int index, string option)
        {
            if (++index >= args.Length)
                throw new ArgumentException($"Missing value for {option}.");
            return args[index];
        }
    }
}

public readonly record struct ReportRef(string ReportId, int FightId, int SourceId);

public static class MchDancingMadProfileBuilder
{
    public static IReadOnlyList<MchProfileEntry> Build(IReadOnlyList<MchLogActionEvent> actions, IReadOnlyList<MchLogMitigationEvent> mitigations, int reportCount)
    {
        var entries = new List<MchProfileEntry>();
        foreach (var group in actions.GroupBy(e => (e.ActionName, e.Phase)))
            entries.Add(BuildEntry(group.Key.ActionName, group.Key.Phase, group.Select(e => e.FightTime).Order().ToList(), reportCount, "offensive", "force"));
        foreach (var group in mitigations.GroupBy(e => (e.ActionName, e.Phase)))
            entries.Add(BuildEntry(group.Key.ActionName, group.Key.Phase, group.Select(e => e.FightTime).Order().ToList(), reportCount, "mitigation", "mitigation before raidwide"));
        return entries.OrderBy(e => e.MedianTime).ThenBy(e => e.ActionName).ToList();
    }

    public static int BuildFromExtractedEvents(string[] args)
    {
        var inDir = "tools/mch_regression/fflogs_out";
        for (var i = 0; i < args.Length; ++i)
            if (args[i] == "--in" && i + 1 < args.Length)
                inDir = args[++i];

        var actionPath = Path.Combine(inDir, "mch_action_events.json");
        var mitPath = Path.Combine(inDir, "mch_mitigation_events.json");
        var readOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var actions = File.Exists(actionPath)
            ? JsonSerializer.Deserialize<List<MchLogActionEvent>>(File.ReadAllText(actionPath), readOptions) ?? []
            : [];
        var mitigations = File.Exists(mitPath)
            ? JsonSerializer.Deserialize<List<MchLogMitigationEvent>>(File.ReadAllText(mitPath), readOptions) ?? []
            : [];
        var reportCount = Math.Max(actions.Select(a => a.ReportId).Concat(mitigations.Select(m => m.ReportId)).Distinct().Count(), 1);
        var profile = Build(actions, mitigations, reportCount);
        Directory.CreateDirectory(inDir);
        File.WriteAllText(Path.Combine(inDir, "dancing_mad_mch_profile.generated.json"), JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Built profile entries: {profile.Count}");
        return 0;
    }

    private static MchProfileEntry BuildEntry(string action, string phase, IReadOnlyList<double> times, int reportCount, string precondition, string note)
    {
        var count = times.Count;
        return new(
            action,
            Percentile(times, 0.50),
            Percentile(times, 0.25),
            Percentile(times, 0.75),
            reportCount <= 0 ? 0 : (double)count / reportCount,
            phase,
            precondition,
            note);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        var index = Math.Clamp((values.Count - 1) * percentile, 0, values.Count - 1);
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
            return Math.Round(values[lower], 3);
        var t = index - lower;
        return Math.Round(values[lower] + (values[upper] - values[lower]) * t, 3);
    }
}
