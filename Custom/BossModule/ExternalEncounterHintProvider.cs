using System.Text.Json.Nodes;

namespace BossMod;

public readonly record struct ExternalEncounterHintSnapshot(
    string Namespace,
    ushort TerritoryId,
    ushort ContentId,
    ExternalZoneConfidence Confidence,
    DateTime ExpiresAt,
    bool IsTrashPhase,
    bool IsMajorAddPhase,
    bool ShouldHoldBurst,
    bool ShouldHoldDokumori,
    bool ShouldForbidTCJ,
    bool ForceBurst,
    bool TargetLostWithin5s,
    bool TargetReturnsWithin10s,
    bool TargetableThroughTCJ,
    bool TargetableDuringKunai);

// Short-lived encounter-phase hints pushed by external sources (IPC, Splatoon layouts, in-process bridges).
// One snapshot is kept per namespace (the latest push replaces the previous one); readers get the merge of every non-expired snapshot matching their zone.
// All times are WorldState times, so replays and live play use the same clock.
public static class ExternalEncounterHintProvider
{
    private const string NamespacePrefix = "bossmod.external.splatoon";
    private const float MaxLifetime = 2f;
    private const int MaxNamespaces = 64;
    private static readonly object LockObj = new();
    private static readonly Dictionary<string, ExternalEncounterHintSnapshot> Snapshots = new(StringComparer.OrdinalIgnoreCase);

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

            var validForSeconds = Math.Clamp(GetFloatAny(root, 0.5f, "validForSeconds", "ttl", "ttlSeconds"), 0.05f, MaxLifetime);
            var snapshot = new ExternalEncounterHintSnapshot(
                ns,
                territoryId,
                contentId,
                confidence,
                now.AddSeconds(validForSeconds),
                GetBool(root, "isTrashPhase", false),
                GetBool(root, "isMajorAddPhase", false),
                GetBool(root, "shouldHoldBurst", false),
                GetBool(root, "shouldHoldDokumori", false),
                GetBool(root, "shouldForbidTCJ", false),
                GetBool(root, "forceBurst", false),
                GetBool(root, "targetLostWithin5s", false),
                GetBool(root, "targetReturnsWithin10s", false),
                GetBool(root, "targetableThroughTCJ", true),
                GetBool(root, "targetableDuringKunai", true));

            return PushSnapshot(snapshot, currentTerritoryId, currentContentId, now);
        }
        catch (Exception ex)
        {
            Service.Log($"[ExternalEncounterHint] Failed to parse external hint payload: {ex.Message}");
            return false;
        }
    }

    public static bool PushSnapshot(ExternalEncounterHintSnapshot snapshot, ushort currentTerritoryId, ushort currentContentId, DateTime now)
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
            Snapshots[snapshot.Namespace] = snapshot;
        }

        return true;
    }

    public static bool TryGetSnapshot(ushort currentTerritoryId, ushort currentContentId, DateTime now, out ExternalEncounterHintSnapshot snapshot)
    {
        lock (LockObj)
        {
            PruneLocked(now);

            ExternalEncounterHintSnapshot? merged = null;
            foreach (var candidate in Snapshots.Values)
            {
                if (candidate.TerritoryId != 0 && candidate.TerritoryId != currentTerritoryId
                    || candidate.ContentId != 0 && candidate.ContentId != currentContentId)
                    continue;

                merged = merged is { } current ? Merge(current, candidate) : candidate;
            }

            snapshot = merged ?? default;
            return merged != null;
        }
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

    private static ExternalEncounterHintSnapshot Merge(ExternalEncounterHintSnapshot c, ExternalEncounterHintSnapshot next)
        => new(
            c.Namespace,
            c.TerritoryId != 0 ? c.TerritoryId : next.TerritoryId,
            c.ContentId != 0 ? c.ContentId : next.ContentId,
            next.Confidence > c.Confidence ? next.Confidence : c.Confidence,
            next.ExpiresAt > c.ExpiresAt ? next.ExpiresAt : c.ExpiresAt,
            c.IsTrashPhase || next.IsTrashPhase,
            c.IsMajorAddPhase || next.IsMajorAddPhase,
            c.ShouldHoldBurst || next.ShouldHoldBurst,
            c.ShouldHoldDokumori || next.ShouldHoldDokumori,
            c.ShouldForbidTCJ || next.ShouldForbidTCJ,
            c.ForceBurst || next.ForceBurst,
            c.TargetLostWithin5s || next.TargetLostWithin5s,
            c.TargetReturnsWithin10s || next.TargetReturnsWithin10s,
            c.TargetableThroughTCJ && next.TargetableThroughTCJ,
            c.TargetableDuringKunai && next.TargetableDuringKunai);

    private static void PruneLocked(DateTime now)
    {
        List<string>? expired = null;
        foreach (var (ns, s) in Snapshots)
            if (s.ExpiresAt <= now)
                (expired ??= []).Add(ns);
        if (expired != null)
            foreach (var ns in expired)
                Snapshots.Remove(ns);
    }

    private static bool ValidNamespace(string ns)
        => !string.IsNullOrWhiteSpace(ns) && ns.StartsWith(NamespacePrefix, StringComparison.OrdinalIgnoreCase);

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
