using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FFLogsTimelineExtract;

// One fight that cannot be used (its report was withdrawn or made private between the listing and the fetch); the run skips it
// and continues with the next fight.
public sealed class FFLogsFightSkipped(string message) : Exception(message);

// The API itself refused or failed a request (token, HTTP status, GraphQL errors, empty data); the run stops fetching and keeps
// what it already has. Derives from InvalidOperationException so the top-level handler reports it like any other request failure.
public sealed class FFLogsRequestFailed(string message) : InvalidOperationException(message);

// Minimal FFLogs v2 client-credentials GraphQL client. The access token lives in memory only and is never logged or written.
public sealed class FFLogsClient(string clientId, string clientSecret) : IDisposable
{
    private const string TokenUrl = "https://www.fflogs.com/oauth/token";
    private const string GraphQLUrl = "https://www.fflogs.com/api/v2/client";
    private const int EventPageLimit = 10000;
    private const int MaxEventPages = 50; // 500k events; a fight never needs that, so it only stops a misbehaving cursor from spending points
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private string? _token;

    public void Dispose() => _http.Dispose();

    // Fetches the client-credentials token once and caches it for the lifetime of the client.
    public async Task<string> TokenAsync()
    {
        if (_token != null)
            return _token;
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("grant_type", "client_credentials")]);
        using var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new FFLogsRequestFailed($"token request failed: HTTP {(int)response.StatusCode}");
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String)
            throw new FFLogsRequestFailed("token response has no access_token");
        return _token = token.GetString()!;
    }

    // Runs one GraphQL query and returns its "data" object; GraphQL "errors" become an exception with their messages.
    // Rate limiting (429) and server errors (5xx) are retried with a short backoff before giving up.
    public async Task<JsonElement> QueryAsync(string query, object variables)
    {
        var token = await TokenAsync();
        var payload = JsonSerializer.Serialize(new { query, variables });
        string body;
        for (var attempt = 0; ; ++attempt)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, GraphQLUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request);
            body = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                break;
            var status = (int)response.StatusCode;
            var retryable = status == 429 || status >= 500;
            if (!retryable || attempt >= RetryDelays.Length)
                throw new FFLogsRequestFailed($"graphql request failed: HTTP {status}");
            await Task.Delay(RetryDelays[attempt]);
        }
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray().Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString());
            throw new FFLogsRequestFailed($"graphql errors: {string.Join("; ", messages)}");
        }
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            throw new FFLogsRequestFailed($"graphql response has no data: {Truncate(body)}");
        return data.Clone();
    }

    // All events of one fight for the given data type, following nextPageTimestamp until the fight end; the result is a
    // single JSON array of the concatenated pages.
    public async Task<JsonElement> EventsAsync(FFLogsFight fight, string dataType, string? filterExpression, string hostilityType = "Enemies")
    {
        List<JsonElement> events = [];
        var start = fight.StartMs;
        for (var page = 1; ; ++page)
        {
            var data = await QueryAsync(
                "query($c:String!,$f:[Int],$dt:EventDataType,$h:HostilityType,$s:Float,$e:Float,$l:Int,$fx:String){ reportData { report(code:$c) { events(fightIDs:$f, dataType:$dt, hostilityType:$h, startTime:$s, endTime:$e, limit:$l, filterExpression:$fx) { data nextPageTimestamp } } } }",
                new { c = fight.Code, f = new[] { fight.Id }, dt = dataType, h = hostilityType, s = start, e = fight.EndMs, l = EventPageLimit, fx = filterExpression });
            var report = data.GetProperty("reportData").GetProperty("report");
            if (report.ValueKind != JsonValueKind.Object)
                throw new FFLogsFightSkipped($"report {fight.Code} is not accessible");
            var chunk = report.GetProperty("events");
            if (chunk.TryGetProperty("data", out var rows) && rows.ValueKind == JsonValueKind.Array)
                events.AddRange(rows.EnumerateArray());
            var next = chunk.TryGetProperty("nextPageTimestamp", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetDouble() : (double?)null;
            if (PagingStopReason(start, next, page, fight.EndMs) is { } reason)
            {
                // The normal end of a fight is a missing cursor; anything else is worth a line, because the events after it are lost.
                if (reason != NoNextCursor)
                    Console.Error.WriteLine($"{fight.Code} fight={fight.Id} {dataType}: paging stopped after page {page} ({reason})");
                break;
            }
            start = next!.Value;
        }
        return JsonSerializer.SerializeToElement(events);
    }

    public const string NoNextCursor = "no next cursor";

    // The paging decision, kept pure so the self-test can cover it: the cursor must move forward, stay inside the fight, and the
    // page count is capped so a cursor that repeats itself cannot loop and spend points.
    public static bool ShouldContinuePaging(double start, double? next, int page, double endMs = double.MaxValue)
        => PagingStopReason(start, next, page, endMs) == null;

    // Why paging stops after this page, or null to continue. Only a missing cursor is the normal end of a fight.
    public static string? PagingStopReason(double start, double? next, int page, double endMs = double.MaxValue)
        => next is not { } n ? NoNextCursor
        : n == start ? "cursor did not advance"
        : n < start ? "cursor moved backwards"
        : n >= endMs ? "cursor at or past the fight end"
        : page >= MaxEventPages ? $"page cap of {MaxEventPages} reached"
        : null;

    public async Task<int> PointsSpentAsync()
    {
        var data = await QueryAsync("{ rateLimitData { pointsSpentThisHour } }", new { });
        return (int)data.GetProperty("rateLimitData").GetProperty("pointsSpentThisHour").GetDouble();
    }

    private static string Truncate(string s) => s.Length <= 300 ? s : s[..300] + "...";
}
