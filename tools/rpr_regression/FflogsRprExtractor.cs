using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RprRegression;

public sealed record RprLogFight(
    string ReportId,
    int FightId,
    int SourceId,
    string PlayerName,
    double Rdps,
    int Partition,
    double Duration,
    bool Kill);

public sealed record RprLogCastEvent(
    string ReportId,
    int FightId,
    int SourceId,
    uint AbilityId,
    string ActionName,
    double FightTime,
    int? ArcaneCircleIndex,
    double? OffsetFromArcaneCircle);

public sealed record RprLogActionAnchor(
    int BurstIndex,
    string ActionName,
    int Occurrence,
    int SupportingCasts,
    int EligibleBursts,
    double UsageRate,
    double MedianOffset,
    double P25Offset,
    double P75Offset,
    bool Stable);

public sealed record RprLogBurstSample(
    string ReportId,
    int FightId,
    int SourceId,
    int BurstIndex,
    double ArcaneCircleTime,
    int PreDeathsDesignRefreshes,
    int PostDeathsDesignRefreshes,
    int EnshroudCount,
    int CommunioCount,
    int PerfectioCount,
    int SoulSliceCount,
    double? PotionOffset,
    double? GluttonyOffset,
    IReadOnlyList<string> OrderedActions);

public sealed record RprLogBurstDecisionProfile(
    int BurstIndex,
    int SampleCount,
    int EligibleFightCount,
    double ArcaneCircleUsageRate,
    double MedianArcaneCircleTime,
    double ZeroDeathsDesignRefreshRate,
    double OneDeathsDesignRefreshRate,
    double TwoOrMoreDeathsDesignRefreshRate,
    double MedianEnshroudCount,
    double MedianCommunioCount,
    double MedianPerfectioCount,
    double MedianSoulSliceCount,
    double? MedianPotionOffset,
    double? MedianGluttonyOffset);

public sealed record RprLogPolicyCandidate(
    string DecisionDomain,
    int BurstIndex,
    string Recommendation,
    int SupportingSamples,
    double Confidence,
    bool RequiresRegressionProof,
    string RuntimeGuard);

public sealed record RprLogDerivedProfile(
    DateTimeOffset GeneratedAt,
    int EncounterId,
    int ZoneId,
    int Difficulty,
    string Metric,
    int ReportCount,
    double KillTimeP25,
    double KillTimeMedian,
    double KillTimeP75,
    bool FallbackToStateRotation,
    double MaxTimelineDriftSeconds,
    int MinimumSupportingLogs,
    IReadOnlyList<RprLogBurstDecisionProfile> BurstDecisions,
    IReadOnlyList<RprLogActionAnchor> ActionAnchors,
    IReadOnlyList<RprLogPolicyCandidate> PolicyCandidates);

public static partial class FflogsRprExtractor
{
    private const int DancingMadEncounterId = 1085;
    private const int DancingMadZoneId = 76;
    private const int DancingMadDifficulty = 100;
    private const double BurstWindowBefore = 20.0;
    private const double BurstWindowAfter = 40.0;
    private const uint MedicatedBuffId = 1000049;
    private const double ArcaneCircleCycle = 120.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Dictionary<uint, string> ActionNamesByGameId = new()
    {
        [24373] = "Slice",
        [24374] = "Waxing Slice",
        [24375] = "Infernal Slice",
        [24376] = "Spinning Scythe",
        [24377] = "Nightmare Scythe",
        [24378] = "Shadow of Death",
        [24379] = "Whorl of Death",
        [24380] = "Soul Slice",
        [24381] = "Soul Scythe",
        [24382] = "Gibbet",
        [24383] = "Gallows",
        [24384] = "Guillotine",
        [24385] = "Plentiful Harvest",
        [24386] = "Harpe",
        [24387] = "Soulsow",
        [24388] = "Harvest Moon",
        [24389] = "Blood Stalk",
        [24390] = "Unveiled Gibbet",
        [24391] = "Unveiled Gallows",
        [24392] = "Grim Swathe",
        [24393] = "Gluttony",
        [24394] = "Enshroud",
        [24395] = "Void Reaping",
        [24396] = "Cross Reaping",
        [24397] = "Grim Reaping",
        [24398] = "Communio",
        [24399] = "Lemure's Slice",
        [24400] = "Lemure's Scythe",
        [24405] = "Arcane Circle",
        [36969] = "Sacrificium",
        [36970] = "Executioner's Gibbet",
        [36971] = "Executioner's Gallows",
        [36972] = "Executioner's Guillotine",
        [36973] = "Perfectio"
    };

    private static readonly HashSet<string> TrackedActions = new(ActionNamesByGameId.Values, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        var options = ExtractOptions.Parse(args);
        var reports = options.ReportsFile == null ? [] : ReadReportRefs(options.ReportsFile).ToList();
        if (reports.Count == 0 && options.ReportRefs.Count > 0)
            reports.AddRange(options.ReportRefs);

        var token = await ResolveAccessToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Set FFLOGS_ACCESS_TOKEN or FFLOGS_CLIENT_ID and FFLOGS_CLIENT_SECRET before extracting FFLogs data.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await VerifySchema(http);

        var ranked = new Dictionary<string, RankedLog>(StringComparer.OrdinalIgnoreCase);
        var discoveredFromRankings = reports.Count == 0;
        if (reports.Count == 0)
        {
            var discovered = await FetchRankedReports(http, options);
            reports.AddRange(discovered.Select(item => item.Report));
            foreach (var item in discovered)
                ranked[$"{item.Report.ReportId}:{item.Report.FightId}:{item.Report.SourceId}"] = item.Ranking;
        }

        if (reports.Count == 0)
            throw new InvalidOperationException("No FFLogs reports were provided or discovered.");

        var fights = new List<RprLogFight>();
        var casts = new List<RprLogCastEvent>();
        foreach (var report in reports.Distinct())
        {
            if (discoveredFromRankings && fights.Count >= options.Top)
                break;

            var extracted = await FetchFight(http, report);
            if (extracted.Casts.Count == 0)
            {
                Console.WriteLine($"Skipped report with no tracked RPR casts: {report.ReportId} fight {report.FightId.ToString(CultureInfo.InvariantCulture)} source {report.SourceId.ToString(CultureInfo.InvariantCulture)}");
                continue;
            }

            ranked.TryGetValue($"{report.ReportId}:{report.FightId}:{report.SourceId}", out var ranking);
            fights.Add(new(
                report.ReportId,
                report.FightId,
                report.SourceId,
                ranking.PlayerName ?? "",
                ranking.Rdps,
                ranking.Partition,
                extracted.Duration,
                extracted.Kill));
            casts.AddRange(NormalizeCasts(report, extracted.Casts));
        }

        var outDir = options.OutputDirectory ?? Path.Combine("tools", "rpr_regression", "fflogs_out");
        Directory.CreateDirectory(outDir);
        WriteJson(Path.Combine(outDir, "rpr_log_fights.json"), fights);
        WriteJson(Path.Combine(outDir, "rpr_cast_events.json"), casts);
        var profile = RprLogDerivedProfileBuilder.Build(casts, fights, options.EncounterId, options.ZoneId, options.Difficulty);
        WriteJson(Path.Combine(outDir, "rpr_log_derived_profile.json"), profile);
        RprLogDerivedProfileBuilder.WriteMarkdown(Path.Combine(outDir, "rpr_log_profile.md"), profile);

        Console.WriteLine($"Selected ranked reports: {fights.Count}");
        Console.WriteLine($"Extracted RPR cast events: {casts.Count}");
        Console.WriteLine($"Stable action anchors: {profile.ActionAnchors.Count(anchor => anchor.Stable)}");
        Console.WriteLine($"Output: {Path.GetFullPath(outDir)}");
        return 0;
    }

    private static async Task<IReadOnlyList<RankedReport>> FetchRankedReports(HttpClient http, ExtractOptions options)
    {
        var partitions = await FetchDefaultPartitions(http, options.ZoneId);
        var rankings = new List<RankedLog>();
        foreach (var partition in partitions)
            rankings.AddRange(await FetchCharacterRankings(http, options.EncounterId, options.Difficulty, partition));

        var result = new List<RankedReport>();
        var seenFights = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ranking in rankings.OrderByDescending(row => row.Rdps).ThenBy(row => row.ReportId, StringComparer.Ordinal).ThenBy(row => row.FightId))
        {
            if (result.Count >= Math.Min(100, options.Top + 10))
                break;
            if (!seenFights.Add($"{ranking.ReportId}:{ranking.FightId}"))
                continue;

            var sourceId = await ResolveSourceId(http, ranking);
            if (sourceId != null)
                result.Add(new(new(ranking.ReportId, ranking.FightId, sourceId.Value), ranking));
        }

        Console.WriteLine($"Discovered RPR ranking rows: {rankings.Count}");
        return result;
    }

    private static async Task<IReadOnlyList<int>> FetchDefaultPartitions(HttpClient http, int zoneId)
    {
        const string query = """
        query($zoneId: Int!) {
          worldData {
            zone(id: $zoneId) {
              partitions { id name default }
            }
          }
        }
        """;

        using var doc = await GraphQl(http, query, new { zoneId });
        var partitions = doc.RootElement.GetProperty("data").GetProperty("worldData").GetProperty("zone").GetProperty("partitions");
        var defaults = partitions.EnumerateArray()
            .Where(node => node.TryGetProperty("default", out var value) && value.GetBoolean())
            .Select(node => node.GetProperty("id").GetInt32())
            .Distinct()
            .ToList();
        return defaults.Count > 0
            ? defaults
            : partitions.EnumerateArray().Select(node => node.GetProperty("id").GetInt32()).OrderDescending().Take(1).ToList();
    }

    private static async Task<IReadOnlyList<RankedLog>> FetchCharacterRankings(HttpClient http, int encounterId, int difficulty, int partition)
    {
        const string query = """
        query($id: Int!, $difficulty: Int!, $partition: Int!) {
          worldData {
            encounter(id: $id) {
              characterRankings(page: 1, difficulty: $difficulty, partition: $partition, metric: rdps, specName: "Reaper")
            }
          }
        }
        """;

        using var doc = await GraphQl(http, query, new { id = encounterId, difficulty, partition });
        if (!TryProperty(doc.RootElement, out var rows, "data", "worldData", "encounter", "characterRankings", "rankings") || rows.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<RankedLog>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("name", out var playerNode)
                || !row.TryGetProperty("report", out var reportNode)
                || !reportNode.TryGetProperty("code", out var codeNode)
                || !reportNode.TryGetProperty("fightID", out var fightNode))
                continue;

            var reportId = codeNode.GetString();
            var player = playerNode.GetString();
            if (string.IsNullOrWhiteSpace(reportId) || string.IsNullOrWhiteSpace(player))
                continue;
            result.Add(new(reportId, fightNode.GetInt32(), player, row.TryGetProperty("amount", out var amount) ? amount.GetDouble() : 0, partition));
        }
        return result;
    }

    private static async Task<int?> ResolveSourceId(HttpClient http, RankedLog ranking)
    {
        const string query = """
        query($code: String!) {
          reportData {
            report(code: $code) {
              masterData { actors { id name subType type } }
            }
          }
        }
        """;

        using var doc = await GraphQl(http, query, new { code = ranking.ReportId });
        if (!TryProperty(doc.RootElement, out var actors, "data", "reportData", "report", "masterData", "actors") || actors.ValueKind != JsonValueKind.Array)
            return null;

        var matches = actors.EnumerateArray()
            .Where(actor => actor.TryGetProperty("type", out var type) && type.GetString() == "Player"
                && actor.TryGetProperty("subType", out var subtype) && subtype.GetString() == "Reaper")
            .ToList();
        var exact = matches.FirstOrDefault(actor => actor.TryGetProperty("name", out var name) && name.GetString()?.Equals(ranking.PlayerName, StringComparison.OrdinalIgnoreCase) == true);
        if (exact.ValueKind != JsonValueKind.Undefined)
            return exact.GetProperty("id").GetInt32();
        return matches.Count == 1 ? matches[0].GetProperty("id").GetInt32() : null;
    }

    private static async Task<ExtractedFight> FetchFight(HttpClient http, RprReportRef report)
    {
        const string query = """
        query($code: String!, $fightIDs: [Int]!, $sourceID: Int!, $startTime: Float) {
          reportData {
            report(code: $code) {
              fights(fightIDs: $fightIDs) { id startTime endTime kill }
              events(fightIDs: $fightIDs, sourceID: $sourceID, dataType: Casts, startTime: $startTime, limit: 10000) {
                data
                nextPageTimestamp
              }
              medicated: events(fightIDs: $fightIDs, targetID: $sourceID, abilityID: 1000049, dataType: Buffs, limit: 100) {
                data
              }
            }
          }
        }
        """;

        var raw = new List<RawCastEvent>();
        double? pageStart = null;
        double fightStart = 0;
        double fightEnd = 0;
        var kill = false;
        while (true)
        {
            using var doc = await GraphQl(http, query, new { code = report.ReportId, fightIDs = new[] { report.FightId }, sourceID = report.SourceId, startTime = pageStart });
            if (!TryProperty(doc.RootElement, out var reportNode, "data", "reportData", "report"))
                break;

            if (fightStart <= 0 && reportNode.TryGetProperty("fights", out var fights) && fights.ValueKind == JsonValueKind.Array)
            {
                var fight = fights.EnumerateArray().FirstOrDefault();
                if (fight.ValueKind != JsonValueKind.Undefined)
                {
                    fightStart = fight.GetProperty("startTime").GetDouble();
                    fightEnd = fight.GetProperty("endTime").GetDouble();
                    kill = fight.TryGetProperty("kill", out var killNode) && killNode.GetBoolean();
                }
            }

            if (!reportNode.TryGetProperty("events", out var events))
                break;
            if (events.TryGetProperty("data", out var rows) && rows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in rows.EnumerateArray())
                {
                    if (row.TryGetProperty("type", out var typeNode) && typeNode.GetString()?.Equals("begincast", StringComparison.OrdinalIgnoreCase) == true)
                        continue;
                    var timestamp = row.TryGetProperty("timestamp", out var timeNode) ? timeNode.GetDouble() : 0;
                    var abilityId = row.TryGetProperty("abilityGameID", out var idNode) && idNode.TryGetUInt32(out var id) ? id : 0;
                    var name = EventActionName(row, abilityId);
                    if (IsTrackedAction(name))
                        raw.Add(new(abilityId, name, timestamp));
                }
            }

            if (pageStart == null && reportNode.TryGetProperty("medicated", out var medicated)
                && medicated.TryGetProperty("data", out var buffRows) && buffRows.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in buffRows.EnumerateArray())
                {
                    if (!row.TryGetProperty("type", out var typeNode) || typeNode.GetString()?.Equals("applybuff", StringComparison.OrdinalIgnoreCase) != true)
                        continue;
                    var timestamp = row.TryGetProperty("timestamp", out var timeNode) ? timeNode.GetDouble() : 0;
                    raw.Add(new(MedicatedBuffId, "Potion", timestamp));
                }
            }

            var next = events.TryGetProperty("nextPageTimestamp", out var nextNode) && nextNode.ValueKind == JsonValueKind.Number ? nextNode.GetDouble() : 0;
            if (next <= 0 || pageStart != null && next <= pageStart.Value)
                break;
            pageStart = next;
        }

        return new(Math.Max(0, (fightEnd - fightStart) / 1000.0), kill, raw.Select(cast => cast with { Timestamp = (cast.Timestamp - fightStart) / 1000.0 }).OrderBy(cast => cast.Timestamp).ToList());
    }

    private static IReadOnlyList<RprLogCastEvent> NormalizeCasts(RprReportRef report, IReadOnlyList<RawCastEvent> casts)
    {
        var arcaneTimes = casts.Where(cast => cast.ActionName.Equals("Arcane Circle", StringComparison.OrdinalIgnoreCase)).Select(cast => cast.Timestamp).ToList();
        return casts.Select(cast =>
        {
            var matches = arcaneTimes
                .Select(time => (Time: time, BurstSlot: ArcaneCircleBurstSlot(time), Distance: Math.Abs(cast.Timestamp - time)))
                .Where(item => cast.Timestamp >= item.Time - BurstWindowBefore && cast.Timestamp <= item.Time + BurstWindowAfter)
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.BurstSlot)
                .ToList();
            var matched = matches.Count > 0;
            var nearest = matched ? matches[0] : default;
            return new RprLogCastEvent(
                report.ReportId,
                report.FightId,
                report.SourceId,
                cast.AbilityId,
                cast.ActionName,
                Math.Round(cast.Timestamp, 3),
                matched ? nearest.BurstSlot : null,
                matched ? Math.Round(cast.Timestamp - nearest.Time, 3) : null);
        }).ToList();
    }

    private static int ArcaneCircleBurstSlot(double time)
        => Math.Max(0, (int)Math.Round(time / ArcaneCircleCycle, MidpointRounding.AwayFromZero));

    private static bool IsTrackedAction(string name)
        => TrackedActions.Contains(name)
        || name.Contains("Gemdraught of Strength", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Tincture of Strength", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Potion", StringComparison.OrdinalIgnoreCase);

    private static string EventActionName(JsonElement row, uint abilityId)
    {
        if (row.TryGetProperty("ability", out var ability) && ability.TryGetProperty("name", out var nameNode))
            return nameNode.GetString() ?? "";
        return ActionNamesByGameId.GetValueOrDefault(abilityId, abilityId == 0 ? "" : $"Action {abilityId.ToString(CultureInfo.InvariantCulture)}");
    }

    private static async Task VerifySchema(HttpClient http)
    {
        using var doc = await GraphQl(http, "query { __schema { queryType { name } } }", new { });
        if (!doc.RootElement.TryGetProperty("data", out _))
            throw new InvalidOperationException("FFLogs GraphQL schema introspection failed.");
    }

    private static async Task<JsonDocument> GraphQl(HttpClient http, string query, object variables)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { query, variables }), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync("https://www.fflogs.com/api/v2/client", content);
        var body = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("errors", out var errors))
        {
            var messages = errors.EnumerateArray().Select(error => error.TryGetProperty("message", out var message) ? message.GetString() : error.ToString());
            doc.Dispose();
            throw new InvalidOperationException($"FFLogs GraphQL error: {string.Join("; ", messages)}");
        }
        return doc;
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

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
        using var response = await http.PostAsync("https://www.fflogs.com/oauth/token", content);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("access_token", out var token) ? token.GetString() : null;
    }

    private static IEnumerable<RprReportRef> ReadReportRefs(string path)
    {
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith('#') && TryParseReportRef(trimmed, out var report))
                yield return report;
        }
    }

    private static bool TryParseReportRef(string text, out RprReportRef report)
    {
        var url = ReportUrlRegex().Match(text);
        if (url.Success)
        {
            report = new(url.Groups["report"].Value, int.Parse(url.Groups["fight"].Value, CultureInfo.InvariantCulture), int.Parse(url.Groups["source"].Value, CultureInfo.InvariantCulture));
            return true;
        }

        var parts = text.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var fight) && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var source))
        {
            report = new(parts[0], fight, source);
            return true;
        }

        report = default;
        return false;
    }

    private static bool TryProperty(JsonElement root, out JsonElement value, params string[] path)
    {
        value = root;
        foreach (var part in path)
            if (!value.TryGetProperty(part, out value))
                return false;
        return true;
    }

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));

    [GeneratedRegex(@"fflogs\.com/reports/(?<report>[A-Za-z0-9]+).*?[#&?]fight=(?<fight>\d+).*?[&?]source=(?<source>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ReportUrlRegex();

    private readonly record struct RprReportRef(string ReportId, int FightId, int SourceId);
    private readonly record struct RankedReport(RprReportRef Report, RankedLog Ranking);
    private readonly record struct RankedLog(string ReportId, int FightId, string? PlayerName, double Rdps, int Partition);
    private readonly record struct RawCastEvent(uint AbilityId, string ActionName, double Timestamp);
    private readonly record struct ExtractedFight(double Duration, bool Kill, IReadOnlyList<RawCastEvent> Casts);

    private sealed record ExtractOptions(string? ReportsFile, string? OutputDirectory, List<RprReportRef> ReportRefs, int EncounterId, int ZoneId, int Difficulty, int Top)
    {
        public static ExtractOptions Parse(string[] args)
        {
            string? reports = null;
            string? output = null;
            var encounterId = DancingMadEncounterId;
            var zoneId = DancingMadZoneId;
            var difficulty = DancingMadDifficulty;
            var top = 20;
            var refs = new List<RprReportRef>();

            for (var i = 0; i < args.Length; ++i)
            {
                switch (args[i])
                {
                    case "--reports":
                        reports = Require(args, ref i, "--reports");
                        break;
                    case "--report":
                        var raw = Require(args, ref i, "--report");
                        if (!TryParseReportRef(raw, out var parsed))
                            throw new ArgumentException("--report must be an FFLogs URL containing fight and source, or '<report> <fight> <source>'.");
                        refs.Add(parsed);
                        break;
                    case "--out":
                        output = Require(args, ref i, "--out");
                        break;
                    case "--encounter":
                        var encounter = Require(args, ref i, "--encounter");
                        encounterId = encounter.Equals("DMU", StringComparison.OrdinalIgnoreCase) || encounter.Equals("Dancing Mad", StringComparison.OrdinalIgnoreCase) || encounter.Equals("絶妖星乱舞", StringComparison.OrdinalIgnoreCase)
                            ? DancingMadEncounterId
                            : int.Parse(encounter, CultureInfo.InvariantCulture);
                        break;
                    case "--zone":
                        zoneId = int.Parse(Require(args, ref i, "--zone"), CultureInfo.InvariantCulture);
                        break;
                    case "--difficulty":
                        difficulty = int.Parse(Require(args, ref i, "--difficulty"), CultureInfo.InvariantCulture);
                        break;
                    case "--top":
                        top = Math.Clamp(int.Parse(Require(args, ref i, "--top"), CultureInfo.InvariantCulture), 1, 100);
                        break;
                }
            }

            return new(reports, output, refs, encounterId, zoneId, difficulty, top);
        }

        private static string Require(string[] args, ref int index, string option)
        {
            if (++index >= args.Length)
                throw new ArgumentException($"Missing value for {option}.");
            return args[index];
        }
    }
}

public static class RprLogDerivedProfileBuilder
{
    private const double ReferenceGcd = 2.50;
    private const int MinimumSupportingLogs = 5;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static RprLogDerivedProfile Build(IReadOnlyList<RprLogCastEvent> casts, IReadOnlyList<RprLogFight> fights, int encounterId, int zoneId, int difficulty)
    {
        var samples = BuildBurstSamples(casts);
        var decisions = samples
            .GroupBy(sample => sample.BurstIndex)
            .OrderBy(group => group.Key)
            .Select(group => BuildDecision(group, EligibleFightCount(fights, group.Key)))
            .ToList();
        var anchors = BuildAnchors(casts, samples);
        var policies = BuildPolicyCandidates(samples, decisions);
        var killTimes = fights.Where(fight => fight.Duration > 0).Select(fight => fight.Duration).Order().ToList();
        return new(
            DateTimeOffset.UtcNow,
            encounterId,
            zoneId,
            difficulty,
            "rdps",
            fights.Count,
            Percentile(killTimes, 0.25),
            Percentile(killTimes, 0.50),
            Percentile(killTimes, 0.75),
            true,
            ReferenceGcd,
            MinimumSupportingLogs,
            decisions,
            anchors,
            policies);
    }

    public static int BuildFromExtractedEvents(string[] args)
    {
        var inDir = Path.Combine("tools", "rpr_regression", "fflogs_out");
        var encounterId = 1085;
        var zoneId = 76;
        var difficulty = 100;
        for (var i = 0; i < args.Length; ++i)
        {
            if (args[i] == "--in" && i + 1 < args.Length)
                inDir = args[++i];
            else if (args[i] == "--encounter" && i + 1 < args.Length)
                encounterId = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--zone" && i + 1 < args.Length)
                zoneId = int.Parse(args[++i], CultureInfo.InvariantCulture);
            else if (args[i] == "--difficulty" && i + 1 < args.Length)
                difficulty = int.Parse(args[++i], CultureInfo.InvariantCulture);
        }

        var castPath = Path.Combine(inDir, "rpr_cast_events.json");
        var fightPath = Path.Combine(inDir, "rpr_log_fights.json");
        if (!File.Exists(castPath) || !File.Exists(fightPath))
            throw new FileNotFoundException("Expected rpr_cast_events.json and rpr_log_fights.json in the input directory.");

        var casts = JsonSerializer.Deserialize<List<RprLogCastEvent>>(File.ReadAllText(castPath), JsonOptions) ?? [];
        var fights = JsonSerializer.Deserialize<List<RprLogFight>>(File.ReadAllText(fightPath), JsonOptions) ?? [];
        var profile = Build(casts, fights, encounterId, zoneId, difficulty);
        File.WriteAllText(Path.Combine(inDir, "rpr_log_derived_profile.json"), JsonSerializer.Serialize(profile, JsonOptions));
        WriteMarkdown(Path.Combine(inDir, "rpr_log_profile.md"), profile);
        Console.WriteLine($"Built RPR log profile from {fights.Count} fights with {profile.ActionAnchors.Count(anchor => anchor.Stable)} stable anchors.");
        return 0;
    }

    public static void WriteMarkdown(string path, RprLogDerivedProfile profile)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RPR FFLogs-derived profile");
        sb.AppendLine();
        sb.AppendLine($"- Metric: {profile.Metric}");
        sb.AppendLine($"- Reports: {profile.ReportCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Kill time p25/median/p75: {profile.KillTimeP25:F3} / {profile.KillTimeMedian:F3} / {profile.KillTimeP75:F3}");
        sb.AppendLine($"- Stable anchors: {profile.ActionAnchors.Count(anchor => anchor.Stable).ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine("- Runtime policy: timeline anchors must fall back to the state rotation when drift exceeds one reference GCD.");
        sb.AppendLine();
        sb.AppendLine("## Burst decisions");
        foreach (var decision in profile.BurstDecisions)
        {
            sb.AppendLine($"- Burst {decision.BurstIndex}: samples={decision.SampleCount}/{decision.EligibleFightCount}, AC usage={decision.ArcaneCircleUsageRate:P0}, AC={decision.MedianArcaneCircleTime:F3}s, DD 0/1/2+={decision.ZeroDeathsDesignRefreshRate:P0}/{decision.OneDeathsDesignRefreshRate:P0}/{decision.TwoOrMoreDeathsDesignRefreshRate:P0}, Enshroud={decision.MedianEnshroudCount:F1}, Communio={decision.MedianCommunioCount:F1}, Perfectio={decision.MedianPerfectioCount:F1}, SoulSlice={decision.MedianSoulSliceCount:F1}, PotionOffset={FormatNullable(decision.MedianPotionOffset)}, GluttonyOffset={FormatNullable(decision.MedianGluttonyOffset)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Log-derived policy candidates");
        sb.AppendLine("These remain offline candidates until the existing regression and production harness prove them without a required-metric regression.");
        foreach (var candidate in profile.PolicyCandidates)
            sb.AppendLine($"- Burst {candidate.BurstIndex} {candidate.DecisionDomain}: {candidate.Recommendation}, confidence={candidate.Confidence:P0}, n={candidate.SupportingSamples}, regressionProof={candidate.RequiresRegressionProof}, guard={candidate.RuntimeGuard}");

        sb.AppendLine();
        sb.AppendLine("## Stable action anchors");
        foreach (var anchor in profile.ActionAnchors.Where(anchor => anchor.Stable).OrderBy(anchor => anchor.BurstIndex).ThenBy(anchor => anchor.MedianOffset))
            sb.AppendLine($"- Burst {anchor.BurstIndex}: {anchor.ActionName} #{anchor.Occurrence}, median={anchor.MedianOffset:+0.000;-0.000;0.000}s, p25/p75={anchor.P25Offset:+0.000;-0.000;0.000}/{anchor.P75Offset:+0.000;-0.000;0.000}, usage={anchor.UsageRate:P0}, n={anchor.SupportingCasts}");

        File.WriteAllText(path, sb.ToString());
    }

    private static IReadOnlyList<RprLogBurstSample> BuildBurstSamples(IReadOnlyList<RprLogCastEvent> casts)
    {
        var result = new List<RprLogBurstSample>();
        foreach (var group in casts.Where(cast => cast.ArcaneCircleIndex != null).GroupBy(cast => (cast.ReportId, cast.FightId, cast.SourceId, BurstIndex: cast.ArcaneCircleIndex!.Value)))
        {
            var ordered = group.OrderBy(cast => cast.FightTime).ToList();
            var arcane = ordered.FirstOrDefault(cast => cast.ActionName.Equals("Arcane Circle", StringComparison.OrdinalIgnoreCase));
            if (arcane == null)
                continue;
            var dd = ordered.Where(cast => IsDeathsDesign(cast.ActionName)).ToList();
            result.Add(new(
                group.Key.ReportId,
                group.Key.FightId,
                group.Key.SourceId,
                group.Key.BurstIndex,
                arcane.FightTime,
                dd.Count(cast => cast.OffsetFromArcaneCircle is >= -20 and < 0),
                dd.Count(cast => cast.OffsetFromArcaneCircle is >= 0 and <= 20),
                ordered.Count(cast => cast.ActionName.Equals("Enshroud", StringComparison.OrdinalIgnoreCase)),
                ordered.Count(cast => cast.ActionName.Equals("Communio", StringComparison.OrdinalIgnoreCase)),
                ordered.Count(cast => cast.ActionName.Equals("Perfectio", StringComparison.OrdinalIgnoreCase)),
                ordered.Count(cast => cast.ActionName is "Soul Slice" or "Soul Scythe"),
                FirstOffset(ordered, IsPotion),
                FirstOffset(ordered, action => action.Equals("Gluttony", StringComparison.OrdinalIgnoreCase)),
                ordered.Select(cast => $"{cast.OffsetFromArcaneCircle:+0.000;-0.000;0.000}:{cast.ActionName}").ToList()));
        }
        return result;
    }

    private static RprLogBurstDecisionProfile BuildDecision(IGrouping<int, RprLogBurstSample> group, int eligibleFightCount)
    {
        var samples = group.ToList();
        var refreshCounts = samples.Select(sample => sample.PreDeathsDesignRefreshes + sample.PostDeathsDesignRefreshes).ToList();
        return new(
            group.Key,
            samples.Count,
            eligibleFightCount,
            eligibleFightCount == 0 ? 0 : (double)samples.Count / eligibleFightCount,
            Median(samples.Select(sample => sample.ArcaneCircleTime)),
            Rate(refreshCounts, count => count == 0),
            Rate(refreshCounts, count => count == 1),
            Rate(refreshCounts, count => count >= 2),
            Median(samples.Select(sample => (double)sample.EnshroudCount)),
            Median(samples.Select(sample => (double)sample.CommunioCount)),
            Median(samples.Select(sample => (double)sample.PerfectioCount)),
            Median(samples.Select(sample => (double)sample.SoulSliceCount)),
            NullableMedian(samples.Select(sample => sample.PotionOffset)),
            NullableMedian(samples.Select(sample => sample.GluttonyOffset)));
    }

    private static IReadOnlyList<RprLogActionAnchor> BuildAnchors(IReadOnlyList<RprLogCastEvent> casts, IReadOnlyList<RprLogBurstSample> samples)
    {
        var eligible = samples.GroupBy(sample => sample.BurstIndex).ToDictionary(group => group.Key, group => group.Count());
        var occurrences = casts
            .Where(cast => cast.ArcaneCircleIndex != null && cast.OffsetFromArcaneCircle != null)
            .GroupBy(cast => (cast.ReportId, cast.FightId, cast.SourceId, BurstIndex: cast.ArcaneCircleIndex!.Value, cast.ActionName))
            .SelectMany(group => group.OrderBy(cast => cast.FightTime).Select((cast, index) => (Cast: cast, Occurrence: index + 1)));

        return occurrences
            .GroupBy(item => (BurstIndex: item.Cast.ArcaneCircleIndex!.Value, item.Cast.ActionName, item.Occurrence))
            .Select(group =>
            {
                var offsets = group.Select(item => item.Cast.OffsetFromArcaneCircle!.Value).Order().ToList();
                var eligibleBursts = eligible.GetValueOrDefault(group.Key.BurstIndex);
                var usage = eligibleBursts == 0 ? 0 : (double)offsets.Count / eligibleBursts;
                var p25 = Percentile(offsets, 0.25);
                var p75 = Percentile(offsets, 0.75);
                return new RprLogActionAnchor(
                    group.Key.BurstIndex,
                    group.Key.ActionName,
                    group.Key.Occurrence,
                    offsets.Count,
                    eligibleBursts,
                    usage,
                    Percentile(offsets, 0.50),
                    p25,
                    p75,
                    eligibleBursts >= MinimumSupportingLogs && usage >= 0.60 && p75 - p25 <= ReferenceGcd * 1.5);
            })
            .OrderBy(anchor => anchor.BurstIndex)
            .ThenBy(anchor => anchor.MedianOffset)
            .ThenBy(anchor => anchor.ActionName)
            .ToList();
    }

    private static IReadOnlyList<RprLogPolicyCandidate> BuildPolicyCandidates(IReadOnlyList<RprLogBurstSample> samples, IReadOnlyList<RprLogBurstDecisionProfile> decisions)
    {
        const string guard = "encounter and kill-time bucket match; fall back when timeline drift exceeds 2.50s or the live gauge/status state differs";
        var result = new List<RprLogPolicyCandidate>();
        foreach (var decision in decisions)
        {
            var group = samples.Where(sample => sample.BurstIndex == decision.BurstIndex).ToList();
            var arcaneRecommendation = decision.ArcaneCircleUsageRate >= 0.70
                ? $"use near {decision.MedianArcaneCircleTime:F3}s"
                : decision.ArcaneCircleUsageRate <= 0.30
                    ? "hold or skip this cycle"
                    : "adaptive use/hold decision";
            result.Add(new("ArcaneCircleTiming", decision.BurstIndex, arcaneRecommendation, decision.EligibleFightCount, Math.Max(decision.ArcaneCircleUsageRate, 1 - decision.ArcaneCircleUsageRate), true, guard));

            var ddChoices = new[]
            {
                (Name: "0 Death's Design refreshes", Rate: decision.ZeroDeathsDesignRefreshRate),
                (Name: "1 Death's Design refresh", Rate: decision.OneDeathsDesignRefreshRate),
                (Name: "2+ Death's Design refreshes", Rate: decision.TwoOrMoreDeathsDesignRefreshRate)
            };
            var dd = ddChoices.OrderByDescending(choice => choice.Rate).First();
            result.Add(new("DeathsDesignRefreshCount", decision.BurstIndex, dd.Rate >= 0.70 ? dd.Name : "adaptive 1/2 refresh decision", decision.SampleCount, dd.Rate, true, guard));

            AddCountPolicy(result, "EnshroudCount", decision.BurstIndex, group.Select(sample => sample.EnshroudCount), guard);
            AddCountPolicy(result, "CommunioCount", decision.BurstIndex, group.Select(sample => sample.CommunioCount), guard);

            var potionRate = Rate(group, sample => sample.PotionOffset != null);
            if (potionRate >= 0.50 && decision.MedianPotionOffset != null)
                result.Add(new("PotionOffsetFromArcaneCircle", decision.BurstIndex, FormatNullable(decision.MedianPotionOffset), decision.SampleCount, potionRate, true, guard));

            var gluttonyRate = Rate(group, sample => sample.GluttonyOffset != null);
            if (gluttonyRate >= 0.50 && decision.MedianGluttonyOffset != null)
                result.Add(new("GluttonyOffsetFromArcaneCircle", decision.BurstIndex, FormatNullable(decision.MedianGluttonyOffset), decision.SampleCount, gluttonyRate, true, guard));
        }
        return result;
    }

    private static int EligibleFightCount(IReadOnlyList<RprLogFight> fights, int burstIndex)
    {
        var expectedTime = burstIndex * 120.0;
        return fights.Count(fight => fight.Duration >= Math.Max(0, expectedTime - 20.0));
    }

    private static void AddCountPolicy(List<RprLogPolicyCandidate> result, string domain, int burstIndex, IEnumerable<int> values, string guard)
    {
        var counts = values.ToList();
        if (counts.Count == 0)
            return;
        var mode = counts.GroupBy(value => value).OrderByDescending(group => group.Count()).ThenBy(group => group.Key).First();
        var confidence = (double)mode.Count() / counts.Count;
        result.Add(new(domain, burstIndex, confidence >= 0.70 ? mode.Key.ToString(CultureInfo.InvariantCulture) : "adaptive", counts.Count, confidence, true, guard));
    }

    private static bool IsDeathsDesign(string action)
        => action is "Shadow of Death" or "Whorl of Death";

    private static bool IsPotion(string action)
        => action.Equals("Potion", StringComparison.OrdinalIgnoreCase)
        || action.Contains("Gemdraught of Strength", StringComparison.OrdinalIgnoreCase)
        || action.Contains("Tincture of Strength", StringComparison.OrdinalIgnoreCase);

    private static double? FirstOffset(IEnumerable<RprLogCastEvent> casts, Func<string, bool> predicate)
        => casts.Where(cast => predicate(cast.ActionName)).Select(cast => cast.OffsetFromArcaneCircle).FirstOrDefault(offset => offset != null);

    private static double Rate<T>(IReadOnlyCollection<T> values, Func<T, bool> predicate)
        => values.Count == 0 ? 0 : (double)values.Count(predicate) / values.Count;

    private static double Median(IEnumerable<double> values)
        => Percentile(values.Order().ToList(), 0.50);

    private static double? NullableMedian(IEnumerable<double?> values)
    {
        var present = values.Where(value => value != null).Select(value => value!.Value).Order().ToList();
        return present.Count == 0 ? null : Percentile(present, 0.50);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        var position = (values.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return Math.Round(values[lower], 3);
        return Math.Round(values[lower] + (values[upper] - values[lower]) * (position - lower), 3);
    }

    private static string FormatNullable(double? value)
        => value == null ? "n/a" : value.Value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture) + "s";
}
