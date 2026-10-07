using System.Text.Json.Nodes;

namespace BossMod;

public enum ExternalZoneIntent
{
    Danger,
    SafeHint,
    Bait,
    VisualOnly
}

public enum ExternalZoneConfidence
{
    NativeBossMod = 100,
    TrustedSplatoonScript = 70,
    TrustedSplatoonPreset = 60,
    GenericSplatoon = 30,
    Unknown = 0
}

public enum ExternalZoneShapeKind
{
    Circle,
    Donut,
    Rect,
    Cone,
    Capsule
}

public static class ExternalAOEProvider
{
    private const string NamespacePrefix = "bossmod.external.splatoon";
    private const float MaxZoneLifetime = 30f;
    private const float MaxHorizon = 20f;
    private const long MaxBridgeStaleMs = 1500;
    private const int MaxZonesPerNamespace = 256;
    private const int MaxZonesTotal = 2048;
    private static readonly object LockObj = new();
    private static readonly List<ExternalZone> Zones = [];
    // Pushes come in bursts (a live bridge republishes up to 64 zones a frame): the per-namespace counts and the earliest expiry are
    // kept up to date on every add and removal, so a push does not rescan (and re-prune) the whole list.
    private static readonly Dictionary<string, int> CountByNamespace = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime EarliestExpiry = DateTime.MaxValue;
    private static long LastUpdateMs;

    private readonly record struct ExternalZone(
        string Namespace,
        ushort TerritoryId,
        ExternalZoneConfidence Confidence,
        ShapeDistance Shape,
        DateTime ActivatesAt,
        DateTime ExpiresAt,
        ulong Source);

    public static bool PushJson(string payload, ushort currentTerritoryId, DateTime now)
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

            var territoryId = (ushort)GetInt(root, "territoryId", currentTerritoryId);
            var zonesNode = root["zones"];
            var accepted = 0;

            lock (LockObj)
            {
                PruneLocked(now);
                var budget = ZoneBudgetLocked(ns);
                if (zonesNode is JsonArray zones)
                {
                    foreach (var node in zones)
                        if (budget > 0 && node is JsonObject zone && TryParseZone(zone, ns, territoryId, currentTerritoryId, now, out var parsed))
                        {
                            AddLocked(parsed);
                            ++accepted;
                            --budget;
                        }
                }
                else if (budget > 0 && TryParseZone(root, ns, territoryId, currentTerritoryId, now, out var parsed))
                {
                    AddLocked(parsed);
                    ++accepted;
                }

                if (accepted > 0)
                    LastUpdateMs = Environment.TickCount64;
                PruneLocked(now);
            }

            return accepted > 0;
        }
        catch (Exception ex)
        {
            Service.Log($"[ExternalAOE] Failed to parse external AOE payload: {ex.Message}");
            return false;
        }
    }

    public static bool ClearNamespace(string ns)
    {
        if (!ValidNamespace(ns))
            return false;

        lock (LockObj)
        {
            if (CountByNamespace.GetValueOrDefault(ns) > 0)
                RemoveWhereLocked(static (z, ns) => z.Namespace.Equals(ns, StringComparison.OrdinalIgnoreCase), ns);
            LastUpdateMs = Environment.TickCount64;
        }
        return true;
    }

    public static bool ClearAll()
    {
        lock (LockObj)
        {
            Zones.Clear();
            CountByNamespace.Clear();
            EarliestExpiry = DateTime.MaxValue;
            LastUpdateMs = Environment.TickCount64;
        }
        return true;
    }

    public static bool PushZone(string ns, ushort territoryId, ushort currentTerritoryId, ExternalZoneConfidence confidence, ShapeDistance shape, float activatesIn, float expiresIn, string debugName, DateTime now)
    {
        if (!ValidNamespace(ns)
            || confidence < ExternalZoneConfidence.TrustedSplatoonPreset
            || territoryId != 0 && territoryId != currentTerritoryId
            || expiresIn <= activatesIn
            || expiresIn - activatesIn > MaxZoneLifetime)
            return false;

        lock (LockObj)
        {
            PruneLocked(now);
            if (ZoneBudgetLocked(ns) <= 0)
                return false;
            AddLocked(new(ns, territoryId, confidence, shape, now.AddSeconds(Math.Max(0, activatesIn)), now.AddSeconds(expiresIn), SourceFor(ns, debugName)));
            LastUpdateMs = Environment.TickCount64;
        }
        return true;
    }

    public static void ApplyToHints(AIHints hints, ushort currentTerritoryId, DateTime now, float horizon = MaxHorizon)
    {
        lock (LockObj)
        {
            if (Environment.TickCount64 - LastUpdateMs > MaxBridgeStaleMs)
                return;

            PruneLocked(now);
            var cutoff = now.AddSeconds(Math.Min(horizon, MaxHorizon));
            foreach (var zone in Zones)
            {
                if (zone.TerritoryId != 0 && zone.TerritoryId != currentTerritoryId)
                    continue;
                if (zone.ActivatesAt > cutoff || zone.ExpiresAt <= now)
                    continue;

                hints.AddForbiddenZone(zone.Shape, zone.ActivatesAt, zone.Source);
            }
        }
    }

    private static bool TryParseZone(JsonObject obj, string rootNamespace, ushort rootTerritoryId, ushort currentTerritoryId, DateTime now, out ExternalZone zone)
    {
        zone = default;

        var ns = GetString(obj, "namespace", rootNamespace);
        if (!ValidNamespace(ns))
            return false;

        var territoryId = (ushort)GetInt(obj, "territoryId", rootTerritoryId);
        if (territoryId != 0 && territoryId != currentTerritoryId)
            return false;

        var intent = GetEnum(obj, "intent", ExternalZoneIntent.VisualOnly);
        if (intent != ExternalZoneIntent.Danger)
            return false;

        var confidence = GetEnum(obj, "confidence", ExternalZoneConfidence.Unknown);
        if (confidence < ExternalZoneConfidence.TrustedSplatoonPreset)
            return false;

        if (!GetBool(obj, "affectsPlayer", true) || !GetBool(obj, "affectsMeleeUptime", true))
            return false;

        var activatesIn = Math.Max(0, GetFloatAny(obj, 0, "activatesIn", "startsIn"));
        var duration = GetFloatAny(obj, -1, "duration", "durationSeconds");
        var expiresIn = GetFloatAny(obj, duration >= 0 ? activatesIn + duration : -1, "expiresIn", "ttl", "ttlSeconds");
        if (expiresIn <= activatesIn || expiresIn - activatesIn > MaxZoneLifetime)
            return false;

        if (!TryParseShape(obj, out var shape))
            return false;

        zone = new(ns, territoryId, confidence, shape, now.AddSeconds(activatesIn), now.AddSeconds(expiresIn), SourceFor(ns, GetString(obj, "debugName")));
        return true;
    }

    private static bool TryParseShape(JsonObject obj, out ShapeDistance shape)
    {
        shape = null!;
        var kind = GetEnum(obj, "shape", ExternalZoneShapeKind.Circle);
        var origin = GetOrigin(obj);
        var rotation = obj["rotationDeg"] is JsonNode rotDeg ? rotDeg.GetValue<float>().Degrees() : GetFloatAny(obj, 0, "rotation", "rotationRad").Radians();

        switch (kind)
        {
            case ExternalZoneShapeKind.Circle:
            {
                var radius = GetFloat(obj, "radius");
                if (radius is <= 0 or > 100)
                    return false;
                shape = new SDCircle(origin, radius);
                return true;
            }
            case ExternalZoneShapeKind.Donut:
            {
                var inner = GetFloatAny(obj, 0, "innerRadius", "inner");
                var outer = GetFloatAny(obj, 0, "outerRadius", "radius", "outer");
                if (inner < 0 || outer <= inner || outer > 100)
                    return false;
                shape = new SDDonut(origin, inner, outer);
                return true;
            }
            case ExternalZoneShapeKind.Rect:
            {
                var lengthFront = GetFloatAny(obj, GetFloatAny(obj, 0, "length"), "lengthFront", "front");
                var lengthBack = GetFloatAny(obj, 0, "lengthBack", "back");
                var halfWidth = GetFloatAny(obj, 0, "halfWidth", "widthHalf");
                if (lengthFront < 0 || lengthBack < 0 || lengthFront + lengthBack <= 0 || lengthFront + lengthBack > 100 || halfWidth is <= 0 or > 50)
                    return false;
                shape = new SDRect(origin, rotation, lengthFront, lengthBack, halfWidth);
                return true;
            }
            case ExternalZoneShapeKind.Cone:
            {
                var radius = GetFloat(obj, "radius");
                var halfAngle = obj["halfAngleDeg"] is JsonNode haDeg ? haDeg.GetValue<float>().Degrees() : GetFloatAny(obj, 0, "halfAngle", "halfAngleRad").Radians();
                if (radius is <= 0 or > 100 || halfAngle.Rad is <= 0 or > MathF.PI)
                    return false;
                shape = new SDCone(origin, radius, rotation, halfAngle);
                return true;
            }
            case ExternalZoneShapeKind.Capsule:
            {
                var length = GetFloatAny(obj, 0, "length");
                var radius = GetFloat(obj, "radius");
                if (length is <= 0 or > 100 || radius is <= 0 or > 50)
                    return false;
                shape = new SDCapsule(origin, rotation, length, radius);
                return true;
            }
            default:
                return false;
        }
    }

    private static void PruneLocked(DateTime now)
    {
        if (now >= EarliestExpiry)
            RemoveWhereLocked(static (z, now) => z.ExpiresAt <= now, now);
    }

    private static void AddLocked(ExternalZone zone)
    {
        Zones.Add(zone);
        CountByNamespace[zone.Namespace] = CountByNamespace.GetValueOrDefault(zone.Namespace) + 1;
        if (zone.ExpiresAt < EarliestExpiry)
            EarliestExpiry = zone.ExpiresAt;
    }

    // removes matching zones in place (keeping the order of the rest) and brings the counts and the earliest expiry up to date
    private static void RemoveWhereLocked<T>(Func<ExternalZone, T, bool> remove, T arg)
    {
        var kept = 0;
        var earliest = DateTime.MaxValue;
        for (var i = 0; i < Zones.Count; ++i)
        {
            var zone = Zones[i];
            if (remove(zone, arg))
            {
                var left = CountByNamespace.GetValueOrDefault(zone.Namespace) - 1;
                if (left > 0)
                    CountByNamespace[zone.Namespace] = left;
                else
                    CountByNamespace.Remove(zone.Namespace);
                continue;
            }
            Zones[kept++] = zone;
            if (zone.ExpiresAt < earliest)
                earliest = zone.ExpiresAt;
        }
        Zones.RemoveRange(kept, Zones.Count - kept);
        EarliestExpiry = earliest;
    }

    // remaining number of zones this namespace may add (bounded per namespace and in total, so a misbehaving bridge cannot flood the pathfinder)
    private static int ZoneBudgetLocked(string ns)
        => Math.Min(MaxZonesPerNamespace - CountByNamespace.GetValueOrDefault(ns), MaxZonesTotal - Zones.Count);

    private static bool ValidNamespace(string ns)
        => !string.IsNullOrWhiteSpace(ns) && ns.StartsWith(NamespacePrefix, StringComparison.OrdinalIgnoreCase);

    private static ulong SourceFor(string ns, string debugName) => (ulong)HashCode.Combine(ns, debugName);

    private static WPos GetOrigin(JsonObject obj)
    {
        if (obj["origin"] is JsonObject origin)
            return new(GetFloat(origin, "x"), GetFloatAny(origin, 0, "z", "y"));
        return new(GetFloat(obj, "x"), GetFloatAny(obj, 0, "z", "y"));
    }

    private static string GetString(JsonObject obj, string key, string fallback = "")
        => obj[key]?.GetValue<string>() ?? fallback;

    private static int GetInt(JsonObject obj, string key, int fallback = default)
        => obj[key]?.GetValue<int>() ?? fallback;

    private static float GetFloat(JsonObject obj, string key, float fallback = default)
        => obj[key]?.GetValue<float>() ?? fallback;

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
