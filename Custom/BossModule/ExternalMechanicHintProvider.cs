using System.Text.Json.Nodes;

namespace BossMod;

// Where a mechanic hint comes from, so a rotation can choose which predictions to act on.
[Flags]
public enum MechanicHintSources
{
    None = 0,
    Timeline = 1 << 0, // imported fight timelines (ExternalTimelineHints)
    Forecast = 1 << 1, // DisengageForecaster
    External = 1 << 2, // Splatoon, IPC, SplatoonSafeImport, test harnesses
    All = Timeline | Forecast | External
}

public readonly record struct ExternalMechanicHintSnapshot(
    string Namespace,
    ushort TerritoryId,
    ushort ContentId,
    ExternalZoneConfidence Confidence,
    DateTime ExpiresAt,
    float ForcedMoveIn,
    float TargetLossIn,
    float TargetReturnIn,
    float LeyLinesUnsafeIn,
    bool SafeSpotAvailable);

// Short-lived mechanic timing hints pushed by external sources (IPC, Splatoon layouts, in-process bridges).
// One snapshot is kept per namespace (the latest push replaces the previous one); readers get the merge of every non-expired snapshot matching their zone.
// All times are WorldState times, so replays and live play use the same clock.
public static class ExternalMechanicHintProvider
{
    private const string NamespacePrefix = "bossmod.external.splatoon";
    private const string InternalNamespacePrefix = "bossmod.internal."; // in-process producers such as DisengageForecaster
    public const string TimelineNamespace = "bossmod.internal.timeline";
    public const string ForecastNamespace = "bossmod.internal.disengage-forecast";
    private const float MaxLifetime = 5f;
    private const int MaxNamespaces = 64;
    private static readonly object LockObj = new();
    // The timings in a snapshot count from the moment it was pushed, but a snapshot can be read for up to MaxLifetime afterwards:
    // the push time is kept with it and readers get the timings aged to their own "now".
    private static readonly Dictionary<string, (ExternalMechanicHintSnapshot Snapshot, DateTime PushedAt)> Snapshots = new(StringComparer.OrdinalIgnoreCase);

    public static bool PushJson(string payload, ushort currentTerritoryId, ushort currentContentId, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return false;

        try
        {
            if (JsonNode.Parse(payload) is not JsonObject root)
                return false;

            var ns = GetString(root, "namespace");
            if (!ValidNamespace(ns))
                return false;

            if (GetInt(root, "version", 1) != 1)
                return false;

            var territoryId = (ushort)GetInt(root, "territoryId", currentTerritoryId);
            if (territoryId != 0 && territoryId != currentTerritoryId)
                return false;

            var contentId = (ushort)GetIntAny(root, currentContentId, "contentId", "cfcId");
            if (contentId != 0 && contentId != currentContentId)
                return false;

            var confidence = GetEnum(root, "confidence", ExternalZoneConfidence.Unknown);
            if (confidence < ExternalZoneConfidence.TrustedSplatoonPreset)
                return false;

            var ttl = Math.Clamp(GetFloatAny(root, 0.5f, "validForSeconds", "ttl", "ttlSeconds"), 0.05f, MaxLifetime);
            return PushSnapshot(new(ns, territoryId, contentId, confidence, now.AddSeconds(ttl),
                GetFloatAny(root, float.MaxValue, "forcedMoveIn", "forcedMoveSoonIn"),
                GetFloatAny(root, GetBool(root, "targetLostWithin5s", false) ? 5 : float.MaxValue, "targetLossIn", "downtimeIn"),
                GetFloatAny(root, GetBool(root, "targetReturnsWithin10s", false) ? 10 : float.MaxValue, "targetReturnIn", "uptimeIn"),
                GetFloatAny(root, float.MaxValue, "leyLinesUnsafeIn", "leyLinesMoveIn"),
                GetBool(root, "safeSpotAvailable", false)), currentTerritoryId, currentContentId, now);
        }
        catch (Exception ex)
        {
            Service.Log($"[ExternalMechanicHint] Failed to parse external mechanic hint payload: {ex.Message}");
            return false;
        }
    }

    public static bool PushSnapshot(ExternalMechanicHintSnapshot snapshot, ushort currentTerritoryId, ushort currentContentId, DateTime now)
    {
        if (!ValidNamespace(snapshot.Namespace)
            || snapshot.Confidence < ExternalZoneConfidence.TrustedSplatoonPreset
            || snapshot.ExpiresAt <= now
            || snapshot.ExpiresAt > now.AddSeconds(MaxLifetime)
            || snapshot.TerritoryId != 0 && snapshot.TerritoryId != currentTerritoryId
            || snapshot.ContentId != 0 && snapshot.ContentId != currentContentId)
            return false;

        lock (LockObj)
        {
            PruneLocked(now);
            if (!Snapshots.ContainsKey(snapshot.Namespace) && Snapshots.Count >= MaxNamespaces)
                return false;
            Snapshots[snapshot.Namespace] = (Normalize(snapshot), now);
        }

        return true;
    }

    public static MechanicHintSources SourceOf(string ns)
        => ns.Equals(TimelineNamespace, StringComparison.OrdinalIgnoreCase) ? MechanicHintSources.Timeline
        : ns.Equals(ForecastNamespace, StringComparison.OrdinalIgnoreCase) ? MechanicHintSources.Forecast
        : MechanicHintSources.External;

    public static bool TryGetSnapshot(ushort currentTerritoryId, ushort currentContentId, DateTime now, out ExternalMechanicHintSnapshot snapshot)
        => TryGetSnapshot(currentTerritoryId, currentContentId, now, MechanicHintSources.All, out snapshot);

    public static bool TryGetSnapshot(ushort currentTerritoryId, ushort currentContentId, DateTime now, MechanicHintSources sources, out ExternalMechanicHintSnapshot snapshot)
    {
        lock (LockObj)
        {
            PruneLocked(now);

            ExternalMechanicHintSnapshot? merged = null;
            foreach (var (stored, pushedAt) in Snapshots.Values)
            {
                var candidate = Aged(stored, (float)(now - pushedAt).TotalSeconds);
                if (candidate.TerritoryId != 0 && candidate.TerritoryId != currentTerritoryId
                    || candidate.ContentId != 0 && candidate.ContentId != currentContentId
                    || (SourceOf(candidate.Namespace) & sources) == 0)
                    continue;

                merged = merged is { } current ? Merge(current, candidate) : candidate;
            }

            snapshot = merged ?? default;
            return merged != null;
        }
    }

    public static void ApplyToHints(AIHints hints, ushort currentTerritoryId, ushort currentContentId, DateTime now)
    {
        if (!TryGetSnapshot(currentTerritoryId, currentContentId, now, out var snapshot))
            return;

        if (snapshot.ForcedMoveIn <= 0.75f)
            hints.MaxCastTime = Math.Min(hints.MaxCastTime, 0);
    }

    public static bool ClearNamespace(string ns)
    {
        if (!ValidNamespace(ns))
            return false;

        lock (LockObj)
            Snapshots.Remove(ns);

        return true;
    }

    public static bool ClearAll()
    {
        lock (LockObj)
            Snapshots.Clear();
        return true;
    }

    private static ExternalMechanicHintSnapshot Normalize(ExternalMechanicHintSnapshot s)
        => s with
        {
            ForcedMoveIn = ValidHintTime(s.ForcedMoveIn) ? s.ForcedMoveIn : float.MaxValue,
            TargetLossIn = ValidHintTime(s.TargetLossIn) ? s.TargetLossIn : float.MaxValue,
            TargetReturnIn = ValidHintTime(s.TargetReturnIn) ? s.TargetReturnIn : float.MaxValue,
            LeyLinesUnsafeIn = ValidHintTime(s.LeyLinesUnsafeIn) ? s.LeyLinesUnsafeIn : float.MaxValue,
        };

    // a timing already passed reads 0 (the event is happening); unknown timings (MaxValue) stay unknown
    private static ExternalMechanicHintSnapshot Aged(ExternalMechanicHintSnapshot s, float elapsed)
    {
        if (elapsed <= 0)
            return s;
        static float Age(float value, float elapsed) => value == float.MaxValue ? value : Math.Max(0, value - elapsed);
        return s with
        {
            ForcedMoveIn = Age(s.ForcedMoveIn, elapsed),
            TargetLossIn = Age(s.TargetLossIn, elapsed),
            TargetReturnIn = Age(s.TargetReturnIn, elapsed),
            LeyLinesUnsafeIn = Age(s.LeyLinesUnsafeIn, elapsed),
        };
    }

    private static bool ValidHintTime(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value != float.MaxValue;

    // A target loss and its return describe one downtime window, so they must come from the same source: taking Math.Min of each
    // independently could pair one hint's early loss with another hint's late return (or a return-only hint's return with a later loss),
    // producing a window no source ever described. The pair with the earliest loss wins (earliest return on a tie); when no source has
    // a loss, the hints describe a downtime already running and the earliest return is kept as before.
    private static ExternalMechanicHintSnapshot Merge(ExternalMechanicHintSnapshot c, ExternalMechanicHintSnapshot next)
    {
        var cHasLoss = c.TargetLossIn != float.MaxValue;
        var nextHasLoss = next.TargetLossIn != float.MaxValue;
        float lossIn, returnIn;
        if (cHasLoss != nextHasLoss)
            (lossIn, returnIn) = cHasLoss ? (c.TargetLossIn, c.TargetReturnIn) : (next.TargetLossIn, next.TargetReturnIn);
        else if (cHasLoss)
            (lossIn, returnIn) = next.TargetLossIn < c.TargetLossIn || next.TargetLossIn == c.TargetLossIn && next.TargetReturnIn < c.TargetReturnIn
                ? (next.TargetLossIn, next.TargetReturnIn)
                : (c.TargetLossIn, c.TargetReturnIn);
        else
            (lossIn, returnIn) = (float.MaxValue, Math.Min(c.TargetReturnIn, next.TargetReturnIn));
        return new(
            c.Namespace,
            c.TerritoryId != 0 ? c.TerritoryId : next.TerritoryId,
            c.ContentId != 0 ? c.ContentId : next.ContentId,
            next.Confidence > c.Confidence ? next.Confidence : c.Confidence,
            next.ExpiresAt > c.ExpiresAt ? next.ExpiresAt : c.ExpiresAt,
            Math.Min(c.ForcedMoveIn, next.ForcedMoveIn),
            lossIn,
            returnIn,
            Math.Min(c.LeyLinesUnsafeIn, next.LeyLinesUnsafeIn),
            c.SafeSpotAvailable || next.SafeSpotAvailable);
    }

    private static void PruneLocked(DateTime now)
    {
        List<string>? expired = null;
        foreach (var (ns, s) in Snapshots)
            if (s.Snapshot.ExpiresAt <= now)
                (expired ??= []).Add(ns);
        if (expired != null)
            foreach (var ns in expired)
                Snapshots.Remove(ns);
    }

    private static bool ValidNamespace(string ns)
        => !string.IsNullOrWhiteSpace(ns) && (ns.StartsWith(NamespacePrefix, StringComparison.OrdinalIgnoreCase) || ns.StartsWith(InternalNamespacePrefix, StringComparison.OrdinalIgnoreCase));

    private static string GetString(JsonObject obj, string key, string fallback = "")
        => obj[key]?.GetValue<string>() ?? fallback;

    private static int GetInt(JsonObject obj, string key, int fallback = default)
        => obj[key]?.GetValue<int>() ?? fallback;

    private static int GetIntAny(JsonObject obj, int fallback, params string[] keys)
    {
        foreach (var key in keys)
            if (obj[key] is JsonNode node)
                return node.GetValue<int>();
        return fallback;
    }

    private static float GetFloatAny(JsonObject obj, float fallback, params string[] keys)
    {
        foreach (var key in keys)
            if (obj[key] is JsonNode node)
                return node.GetValue<float>();
        return fallback;
    }

    private static bool GetBool(JsonObject obj, string key, bool fallback)
        => obj[key]?.GetValue<bool>() ?? fallback;

    private static T GetEnum<T>(JsonObject obj, string key, T fallback) where T : struct, Enum
    {
        if (obj[key] is not JsonNode node)
            return fallback;

        if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
            return Enum.IsDefined(typeof(T), node.GetValue<int>()) ? (T)(object)node.GetValue<int>() : fallback;

        var value = node.GetValue<string>();
        return Enum.TryParse<T>(value, true, out var parsed) ? parsed : fallback;
    }
}
