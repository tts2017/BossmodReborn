using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using System.Numerics;

using SplatoonGeometry = (string id, string source, string Namespace, string layout, string element, string kind, string renderEngine,
    uint color, System.Numerics.Vector3 center, System.Numerics.Vector3 start, System.Numerics.Vector3 end,
    float? radius, float? innerRadius, float? outerRadius, float? lineRadius,
    float? facingRad, float? halfAngleRad, float? angleMinRad, float? angleMaxRad);
using SplatoonSnapshot = (int version, uint frame, uint territoryId, long generatedAtTickMs,
    System.Collections.Generic.List<(string id, string source, string Namespace, string layout, string element, string kind, string renderEngine,
    uint color, System.Numerics.Vector3 center, System.Numerics.Vector3 start, System.Numerics.Vector3 end,
    float? radius, float? innerRadius, float? outerRadius, float? lineRadius,
    float? facingRad, float? halfAngleRad, float? angleMinRad, float? angleMaxRad)> items);

namespace BossMod;

// Reads what Splatoon is drawing right now over IPC and republishes it as external danger zones. Splatoon evaluates its own layouts,
// triggers, scripts and conditions, so anything that reaches this snapshot is on screen at this moment: the plugin follows what the
// user sees instead of re-deciding when a layout applies.
public sealed class SplatoonLiveZones : IDisposable
{
    public const string HintNamespace = "bossmod.external.splatoon.live";
    private const string SnapshotIpc = "Splatoon.GetActiveDrawGeometryV1";
    private const int SupportedVersion = 1;
    private const long MaxSnapshotAgeMs = 1000;
    private const float ZoneLifetime = 0.35f; // refreshed every frame; long enough to survive a dropped frame
    private const int MaxZones = 64;
    private const float MaxRadius = 100f;

    private readonly ICallGateSubscriber<SplatoonSnapshot>? _snapshot;
    private DateTime _nextRetry;
    private bool _published;

    public SplatoonLiveZones(IDalamudPluginInterface dalamud)
    {
        try
        {
            _snapshot = dalamud.GetIpcSubscriber<SplatoonSnapshot>(SnapshotIpc);
        }
        catch (Exception ex)
        {
            Service.Log($"[SplatoonLiveZones] {SnapshotIpc} is unavailable: {ex.Message}");
        }
    }

    public void Dispose() => Clear();

    public void Update(WorldState ws)
    {
        var config = Service.Config?.Get<CustomConfig>();
        var mode = config?.SplatoonLiveZones ?? SplatoonLiveZoneMode.Off;
        var tag = config?.SplatoonLiveZoneTag ?? "";
        if (mode == SplatoonLiveZoneMode.Off || _snapshot == null || ws.CurrentTime < _nextRetry)
        {
            if (mode == SplatoonLiveZoneMode.Off)
                Clear();
            return;
        }

        SplatoonSnapshot snapshot;
        try
        {
            snapshot = _snapshot.InvokeFunc();
        }
        catch (Exception ex)
        {
            // splatoon is not loaded, or is a build without the snapshot: back off instead of throwing every frame
            _nextRetry = ws.CurrentTime.AddSeconds(10);
            Clear();
            Service.Log($"[SplatoonLiveZones] {SnapshotIpc} failed: {ex.Message}");
            return;
        }

        if (snapshot.version != SupportedVersion || snapshot.items == null
            || snapshot.territoryId != ws.CurrentZone
            || Environment.TickCount64 - snapshot.generatedAtTickMs > MaxSnapshotAgeMs)
        {
            Clear();
            return;
        }

        Publish(snapshot.items, mode, tag, ws);
    }

    private void Publish(List<SplatoonGeometry> items, SplatoonLiveZoneMode mode, string tag, WorldState ws)
    {
        ExternalAOEProvider.ClearNamespace(HintNamespace);
        _published = false;

        var now = ws.CurrentTime;
        var zones = 0;
        for (var i = 0; i < items.Count && zones < MaxZones; ++i)
        {
            var item = items[i];
            if (!ShouldImport(item, mode, tag))
                continue;
            if (!TryBuildShape(item, out var shape))
                continue;

            ExternalAOEProvider.PushZone(HintNamespace, ws.CurrentZone, ws.CurrentZone, ExternalZoneConfidence.TrustedSplatoonScript,
                shape, 0f, ZoneLifetime, item.kind, now);
            ++zones;
            _published = true;
        }
    }

    // Splatoon reports which layout and element drew a shape, so a tag in their names is the precise way to pick the ones to avoid.
    // The colour rule is the fallback for presets that cannot be renamed: their danger shapes keep the red Splatoon defaults to.
    public static bool ShouldImport(in SplatoonGeometry item, SplatoonLiveZoneMode mode, string tag) => mode switch
    {
        SplatoonLiveZoneMode.TaggedLayouts => tag.Length > 0
            && ((item.layout ?? "").Contains(tag, StringComparison.OrdinalIgnoreCase)
                || (item.element ?? "").Contains(tag, StringComparison.OrdinalIgnoreCase)
                || (item.Namespace ?? "").Contains(tag, StringComparison.OrdinalIgnoreCase)),
        SplatoonLiveZoneMode.DangerColored => IsDangerColor(item.color),
        SplatoonLiveZoneMode.All => true,
        _ => false
    };

    // Splatoon draws its fans with the vertex angle pi/2 + a, which points at (-sin a, cos a); this plugin measures angles as
    // (sin a, cos a), so the same direction is the negated angle.
    public static bool TryBuildShape(in SplatoonGeometry item, out ShapeDistance shape)
    {
        shape = null!;
        switch (item.kind)
        {
            case "circle":
            {
                var radius = item.radius ?? item.outerRadius ?? 0f;
                if (radius is <= 0 or > MaxRadius)
                    return false;
                shape = new SDCircle(Ground(item.center), radius);
                return true;
            }
            case "donut":
            {
                var inner = item.innerRadius ?? 0f;
                var outer = item.outerRadius ?? item.radius ?? 0f;
                if (inner < 0 || outer <= inner || outer > MaxRadius)
                    return false;
                shape = new SDDonut(Ground(item.center), inner, outer);
                return true;
            }
            case "cone":
            {
                var outer = item.outerRadius ?? item.radius ?? 0f;
                if (outer is <= 0 or > MaxRadius)
                    return false;
                var min = item.angleMinRad ?? 0f;
                var max = item.angleMaxRad ?? 0f;
                var span = max - min;
                if (span <= 0)
                    return false;
                if (span >= MathF.Tau - 0.01f)
                {
                    var inner = item.innerRadius ?? 0f;
                    shape = inner > 0 ? new SDDonut(Ground(item.center), inner, outer) : new SDCircle(Ground(item.center), outer);
                    return true;
                }
                shape = new SDCone(Ground(item.center), outer, (-(min + max) * 0.5f).Radians(), (span * 0.5f).Radians());
                return true;
            }
            case "line":
            {
                var halfWidth = item.lineRadius ?? item.radius ?? 0f;
                if (halfWidth is <= 0 or > MaxRadius) // a line without width is a tether or a marker, not an area
                    return false;
                var start = Ground(item.start);
                var direction = Ground(item.end) - start;
                var length = direction.Length();
                if (length is <= 0.01f or > MaxRadius)
                    return false;
                shape = new SDRect(start, Angle.FromDirection(direction), length, 0f, halfWidth);
                return true;
            }
            default:
                return false; // dots and text are markers
        }
    }

    // Splatoon colours are ABGR, and its presets keep the default red for danger; safe spots and helpers are recoloured.
    public static bool IsDangerColor(uint color)
    {
        var r = color & 0xFF;
        var g = (color >> 8) & 0xFF;
        var b = (color >> 16) & 0xFF;
        var a = (color >> 24) & 0xFF;
        return a >= 16 && r >= 96 && r > g + 32 && r > b + 32;
    }

    private static WPos Ground(Vector3 position) => new(position.X, position.Z);

    private void Clear()
    {
        if (_published)
        {
            ExternalAOEProvider.ClearNamespace(HintNamespace);
            _published = false;
        }
    }
}

public enum SplatoonLiveZoneMode
{
    Off,
    DangerColored,
    All,
    TaggedLayouts
}
