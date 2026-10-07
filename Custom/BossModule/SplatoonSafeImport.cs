using System.IO;
using System.Text.Json.Nodes;

namespace BossMod;

// Imports static Splatoon layouts (plain JSON exports) from <config>/SplatoonImports/*.json and republishes their elements
// as short-lived external danger zones / mechanic hints every second while the layout applies to the current zone.
// Element semantics follow Splatoon's Element/Layout format: type 0/1 = circle (fixed / relative to actor), 2/3 = line
// (fixed / relative to actor), 4/5 = cone (relative to actor / fixed); refY is the horizontal (north-south) axis and refZ is height.
public sealed class SplatoonSafeImport(string directory)
{
    private const int MaxFiles = 32;
    private const long MaxFileBytes = 1_000_000;
    private const int MaxElementsPerLayout = 300;
    private const uint LeyLinesOID = 0x179;
    private static readonly HashSet<char> UnsafeNameChars = [.. Path.GetInvalidFileNameChars(), '.', ' '];
    private readonly DirectoryInfo _directory = new(directory);
    private readonly Dictionary<string, CachedFile> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileInfo> _files = [];
    private const double EvaluateInterval = 0.2;
    private DateTime _nextReload = DateTime.MinValue;
    private DateTime _nextEvaluate = DateTime.MinValue;
    private DateTime _nextDirectoryErrorLog = DateTime.MinValue;

    private sealed record class CachedFile(DateTime LastWriteUtc, long Length, string Namespace, JsonNode? Root, bool Failed)
    {
        public bool ElementErrorLogged; // one log line per version of the file for elements skipped because of a malformed value
    }

    private readonly record struct Context(WorldState WS, Actor? Player, Actor? LeyLines, bool InCombat, bool InDuty);

    public void Update(WorldState ws)
    {
        // Elements anchored to an actor are republished as fixed shapes, so the refresh rate is how far a moving boss drags its zone
        // behind it. Re-reading the directory stays at one second; re-evaluating the cached layouts is cheap enough to run faster.
        if (ws.CurrentTime < _nextEvaluate)
            return;

        _nextEvaluate = ws.CurrentTime.AddSeconds(EvaluateInterval);
        var rescan = ws.CurrentTime >= _nextReload;
        if (rescan)
            _nextReload = ws.CurrentTime.AddSeconds(1);
        try
        {
            if (!_directory.Exists)
            {
                _directory.Create();
                return;
            }

            var player = ws.Party.Player();
            Actor? leyLines = null;
            if (player != null)
            {
                foreach (var a in ws.Actors.Actors.Values)
                {
                    if (a.OID == LeyLinesOID && a.OwnerID == player.InstanceID)
                    {
                        leyLines = a;
                        break;
                    }
                }
            }
            var ctx = new Context(ws, player, leyLines, player?.InCombat == true, ws.CurrentCFCID != 0);

            if (rescan)
            {
                _seen.Clear();
                _files.Clear();
                var processed = 0;
                foreach (var file in _directory.EnumerateFiles("*.json").OrderBy(f => f.Name))
                {
                    if (++processed > MaxFiles)
                        break;
                    _seen.Add(file.FullName);
                    _files.Add(file);
                }
            }
            foreach (var file in _files)
                ProcessFile(file, ctx);

            // forget (and clear) files that disappeared
            List<string>? removed = null;
            foreach (var (path, cached) in _cache)
            {
                if (!_seen.Contains(path))
                {
                    ExternalAOEProvider.ClearNamespace(cached.Namespace);
                    ExternalMechanicHintProvider.ClearNamespace(cached.Namespace);
                    ExternalEncounterHintProvider.ClearNamespace(cached.Namespace);
                    (removed ??= []).Add(path);
                }
            }
            if (removed != null)
                foreach (var path in removed)
                    _cache.Remove(path);
        }
        catch (Exception ex)
        {
            if (ws.CurrentTime >= _nextDirectoryErrorLog)
            {
                _nextDirectoryErrorLog = ws.CurrentTime.AddSeconds(30);
                Service.Log($"[SplatoonSafeImport] Failed to update imports: {ex.Message}");
            }
        }
    }

    private void ProcessFile(FileInfo file, in Context ctx)
    {
        var ns = ImportNamespace(file.Name);
        CachedFile? cached = null;
        try
        {
            if (file.Length > MaxFileBytes)
                return;

            // only re-read and re-parse the file when it changed on disk; the parsed tree is reused every second
            if (!_cache.TryGetValue(file.FullName, out cached) || cached.LastWriteUtc != file.LastWriteTimeUtc || cached.Length != file.Length)
            {
                JsonNode? root = null;
                var failed = false;
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(file.FullName));
                }
                catch (Exception ex)
                {
                    failed = true;
                    Service.Log($"[SplatoonSafeImport] Failed to parse {file.Name}: {ex.Message}");
                }
                cached = new(file.LastWriteTimeUtc, file.Length, ns, root, failed);
                _cache[file.FullName] = cached;
            }

            ExternalAOEProvider.ClearNamespace(ns);
            ExternalMechanicHintProvider.ClearNamespace(ns);
            ExternalEncounterHintProvider.ClearNamespace(ns);

            if (cached.Root != null && !cached.Failed)
            {
                string? elementError = null;
                ImportRoot(cached.Root, ns, ctx, ref elementError);
                if (elementError != null && !cached.ElementErrorLogged)
                {
                    cached.ElementErrorLogged = true;
                    Service.Log($"[SplatoonSafeImport] Skipped elements with malformed values in {file.Name}: {elementError}");
                }
            }
        }
        catch (Exception ex)
        {
            // A malformed layout-level value would throw on every evaluation (five times a second): report it once and stop
            // importing this version of the file; a changed file on disk is parsed and tried again.
            Service.Log($"[SplatoonSafeImport] Failed to import {file.Name}: {ex.Message}");
            if (cached != null)
                _cache[file.FullName] = cached with { Failed = true };
            ExternalAOEProvider.ClearNamespace(ns);
            ExternalMechanicHintProvider.ClearNamespace(ns);
            ExternalEncounterHintProvider.ClearNamespace(ns);
        }
    }

    // Namespaces must stay unique per file: the sanitized name alone maps "A B.json", "a_b.json" and "A.B.json" to the same
    // string, and each file clears its namespace before importing, so one file would wipe the other's zones every evaluation.
    private static string ImportNamespace(string fileName)
    {
        var hash = 2166136261u; // FNV-1a over the case-folded name (the file cache is case-insensitive as well)
        foreach (var ch in fileName.ToLowerInvariant())
            hash = (hash ^ ch) * 16777619u;
        return $"bossmod.external.splatoon.import.{SafeNamespacePart(fileName)}.{hash:x8}";
    }

    private static void ImportRoot(JsonNode root, string ns, in Context ctx, ref string? elementError)
    {
        if (root is JsonArray arr)
        {
            foreach (var item in arr)
                if (item is JsonObject layout)
                    ImportLayout(layout, ns, ctx, ref elementError);
            return;
        }

        if (root is not JsonObject obj)
            return;

        var layouts = obj["layouts"] as JsonArray ?? obj["Layouts"] as JsonArray;
        if (layouts != null)
        {
            foreach (var item in layouts)
                if (item is JsonObject layout)
                    ImportLayout(layout, ns, ctx, ref elementError);
            return;
        }

        ImportLayout(obj, ns, ctx, ref elementError);
    }

    private static void ImportLayout(JsonObject layout, string ns, in Context ctx, ref string? elementError)
    {
        var ws = ctx.WS;
        var territoryId = (ushort)GetIntAny(layout, ws.CurrentZone, "territoryId", "TerritoryId", "TerritoryID");
        if (!ZoneMatches(layout, ws.CurrentZone) || territoryId != 0 && territoryId != ws.CurrentZone)
            return;

        if (!GetBoolAny(layout, true, "Enabled", "enabled") || GetBoolAny(layout, false, "Nodraw", "nodraw"))
            return;

        // Layouts that only appear under conditions we cannot evaluate must not become permanent danger zones.
        if (GetBoolAny(layout, false, "UseTriggers", "useTriggers") || GetBoolAny(layout, false, "UseDistanceLimit", "useDistanceLimit"))
            return;

        if (!DisplayConditionMet(GetIntAny(layout, 0, "DCond", "dCond"), ctx) || !JobAllowed(layout, ctx))
            return;

        var elements = layout["ElementsL"] as JsonArray ?? layout["elements"] as JsonArray ?? layout["Elements"] as JsonArray;
        if (elements == null)
            return;

        var now = ws.CurrentTime;
        var count = 0;
        var forcedMoveIn = float.MaxValue;
        var leyLinesUnsafeIn = float.MaxValue;
        var safeSpotAvailable = GetBoolAny(layout, false, "safeSpotAvailable", "SafeSpotAvailable");

        foreach (var node in elements)
        {
            if (++count > MaxElementsPerLayout)
                break;

            ShapeDistance shape;
            float activatesIn, expiresIn;
            string debugName;
            try
            {
                if (node is not JsonObject element || !TryParseElement(element, ctx, out shape, out activatesIn, out expiresIn, out debugName))
                    continue;
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
            {
                // a value of the wrong JSON type (e.g. "radius":"5") only drops its own element, not the rest of the layout
                elementError ??= ex.Message;
                continue;
            }

            ExternalAOEProvider.PushZone(ns, territoryId, ws.CurrentZone, ExternalZoneConfidence.TrustedSplatoonPreset, shape, activatesIn, expiresIn, debugName, now);
            if (ctx.Player != null && activatesIn <= 5 && shape.Contains(ctx.Player.Position))
                forcedMoveIn = Math.Min(forcedMoveIn, activatesIn);

            if (ctx.LeyLines != null && activatesIn <= 10 && shape.Contains(ctx.LeyLines.Position))
                leyLinesUnsafeIn = Math.Min(leyLinesUnsafeIn, activatesIn);
        }

        var targetLossIn = GetFloatAny(layout, GetBoolAny(layout, false, "targetLostWithin5s", "TargetLostWithin5s") ? 5 : float.MaxValue, "targetLossIn", "downtimeIn");
        var targetReturnIn = GetFloatAny(layout, GetBoolAny(layout, false, "targetReturnsWithin10s", "TargetReturnsWithin10s") ? 10 : float.MaxValue, "targetReturnIn", "uptimeIn");
        if (forcedMoveIn != float.MaxValue || leyLinesUnsafeIn != float.MaxValue || targetLossIn != float.MaxValue || targetReturnIn != float.MaxValue || safeSpotAvailable)
        {
            ExternalMechanicHintProvider.PushSnapshot(new(ns, territoryId, ws.CurrentCFCID, ExternalZoneConfidence.TrustedSplatoonPreset, now.AddSeconds(2),
                forcedMoveIn, targetLossIn, targetReturnIn, leyLinesUnsafeIn, safeSpotAvailable), ws.CurrentZone, ws.CurrentCFCID, now);
        }

        PushEncounterHint(layout, ns, territoryId, ws, now);
    }

    private static void PushEncounterHint(JsonObject layout, string ns, ushort territoryId, WorldState ws, DateTime now)
    {
        var hint = GetStringAny(layout, "", "encounterHint", "EncounterHint", "phaseKind", "PhaseKind");
        var isTrashPhase = GetBoolAny(layout, false, "isTrashPhase", "IsTrashPhase")
            || hint.Equals("Trash", StringComparison.OrdinalIgnoreCase)
            || hint.Equals("AllianceTrash", StringComparison.OrdinalIgnoreCase);
        var isMajorAddPhase = GetBoolAny(layout, false, "isMajorAddPhase", "IsMajorAddPhase")
            || hint.Equals("MajorAdd", StringComparison.OrdinalIgnoreCase);
        var shouldHoldBurst = GetBoolAny(layout, false, "shouldHoldBurst", "ShouldHoldBurst")
            || hint.Equals("HoldBurst", StringComparison.OrdinalIgnoreCase);
        var forceBurst = GetBoolAny(layout, false, "forceBurst", "ForceBurst")
            || hint.Equals("ForceBurst", StringComparison.OrdinalIgnoreCase);
        var targetLostWithin5s = GetBoolAny(layout, false, "targetLostWithin5s", "TargetLostWithin5s");
        var targetReturnsWithin10s = GetBoolAny(layout, false, "targetReturnsWithin10s", "TargetReturnsWithin10s")
            || hint.Equals("BossReturn", StringComparison.OrdinalIgnoreCase);

        if (!isTrashPhase && !isMajorAddPhase && !shouldHoldBurst && !forceBurst && !targetLostWithin5s && !targetReturnsWithin10s)
            return;

        ExternalEncounterHintProvider.PushSnapshot(new(ns, territoryId, ws.CurrentCFCID, ExternalZoneConfidence.TrustedSplatoonPreset, now.AddSeconds(2),
            isTrashPhase,
            isMajorAddPhase,
            shouldHoldBurst,
            false,
            false,
            forceBurst,
            targetLostWithin5s,
            targetReturnsWithin10s,
            true,
            true), ws.CurrentZone, ws.CurrentCFCID, now);
    }

    private static bool TryParseElement(JsonObject element, in Context ctx, out ShapeDistance shape, out float activatesIn, out float expiresIn, out string debugName)
    {
        shape = null!;
        debugName = GetStringAny(element, "splatoon", "Name", "name", "debugName");
        activatesIn = Math.Max(0, GetFloatAny(element, 0, "activatesIn", "startsIn"));
        expiresIn = GetFloatAny(element, 2, "expiresIn", "ttl", "ttlSeconds", "duration", "durationSeconds");
        if (expiresIn <= activatesIn)
            expiresIn = activatesIn + 2;

        if (!GetBoolAny(element, true, "Enabled", "enabled") || GetBoolAny(element, false, "Disable", "disabled"))
            return false;

        if (HasUnsafeDynamicContent(element) || IsNonDangerIntent(element) || HasUnsupportedCondition(element))
            return false;

        var type = GetIntAny(element, -1, "type", "Type");
        var shapeName = GetStringAny(element, "", "shape", "Shape", "kind", "Kind");
        var kind = ResolveKind(shapeName, type);
        if (kind == ElementKind.Unknown)
            return false;

        var relativeToActor = type is 1 or 3 or 4;
        if (!TryResolveOriginAndRotation(element, ctx, relativeToActor, out var origin, out var rotation, out var actorRadius))
            return false;

        // splatoon defaults: radius 0.35, and Donut is the width added outside radius rather than an inner radius
        var radius = GetFloatAny(element, 0.35f, "radius", "Radius") + actorRadius;
        var donutWidth = GetFloatAny(element, 0, "donut", "Donut");
        var innerRadius = GetFloatAny(element, 0, "innerRadius");

        switch (kind)
        {
            case ElementKind.Circle:
                if (donutWidth > 0)
                    return TryDonut(origin, radius, radius + donutWidth, out shape);
                if (radius is <= 0 or > 100)
                    return false;
                shape = new SDCircle(origin, radius);
                return true;
            case ElementKind.Donut:
                return donutWidth > 0
                    ? TryDonut(origin, radius, radius + donutWidth, out shape)
                    : TryDonut(origin, innerRadius, radius, out shape);
            case ElementKind.Cone:
            {
                if (radius is <= 0 or > 100)
                    return false;
                // a cone without a real angle span is drawn as a full circle by splatoon
                if (!TryConeSpan(element, out var centerOffset, out var halfAngle))
                {
                    shape = donutWidth > 0 ? new SDDonut(origin, radius, radius + donutWidth) : new SDCircle(origin, radius);
                    return true;
                }
                if (halfAngle.Rad is <= 0 or > MathF.PI)
                    return false;
                shape = new SDCone(origin, radius, rotation + centerOffset, halfAngle);
                return true;
            }
            case ElementKind.Line:
            {
                // splatoon lines run from ref (origin) to off and use the element radius as the half width; thicc is a pixel width for
                // drawing and means nothing in the world
                var halfWidth = GetFloatAny(element, radius, "halfWidth", "widthHalf");
                var length = GetFloatAny(element, 0, "length", "Length");
                if (TryGetFloatAny(element, out var offX, "offX", "OffX") && TryGetFloatAny(element, out var offY, "offY", "OffY"))
                {
                    var offset = new WDir(offX, offY);
                    if (relativeToActor && GetBoolAny(element, false, "includeRotation", "IncludeRotation"))
                        offset = offset.Rotate(rotation);
                    var end = relativeToActor ? origin + offset : new WPos(offX, offY);
                    var dir = end - origin;
                    var len = dir.Length();
                    if (len > 0.01f)
                    {
                        length = len;
                        rotation = Angle.FromDirection(dir);
                    }
                }
                if (length is <= 0 or > 100 || halfWidth is <= 0 or > 50)
                    return false;
                shape = new SDRect(origin, rotation, length, 0, halfWidth);
                return true;
            }
            default:
                return false;
        }
    }

    private enum ElementKind { Unknown, Circle, Donut, Cone, Line }

    private static ElementKind ResolveKind(string shapeName, int type)
    {
        if (shapeName.Length > 0)
        {
            if (shapeName.Equals("circle", StringComparison.OrdinalIgnoreCase))
                return ElementKind.Circle;
            if (shapeName.Equals("donut", StringComparison.OrdinalIgnoreCase))
                return ElementKind.Donut;
            if (shapeName.Equals("cone", StringComparison.OrdinalIgnoreCase))
                return ElementKind.Cone;
            if (shapeName.Equals("rect", StringComparison.OrdinalIgnoreCase) || shapeName.Equals("line", StringComparison.OrdinalIgnoreCase))
                return ElementKind.Line;
            return ElementKind.Unknown;
        }

        // splatoon element types: 0 circle at fixed coordinates, 1 circle relative to actor, 2 line between fixed coordinates, 3 line relative to actor, 4 cone relative to actor, 5 cone at fixed coordinates
        return type switch
        {
            0 or 1 => ElementKind.Circle,
            2 or 3 => ElementKind.Line,
            4 or 5 => ElementKind.Cone,
            _ => ElementKind.Unknown
        };
    }

    private static bool TryDonut(WPos origin, float inner, float outer, out ShapeDistance shape)
    {
        shape = null!;
        if (inner < 0 || outer <= inner || outer > 100)
            return false;
        shape = new SDDonut(origin, inner, outer);
        return true;
    }

    // Cone angles are whole degrees measured from the element rotation, so the span is coneAngleMax - coneAngleMin and the cone sits
    // around the middle of the two, not around the rotation itself. Equal values mean splatoon draws the full circle instead.
    private static bool TryConeSpan(JsonObject element, out Angle centerOffset, out Angle halfAngle)
    {
        centerOffset = default;
        if (TryGetFloatAny(element, out var half, "halfAngleRad"))
        {
            halfAngle = half.Radians();
            return half > 0;
        }
        if (TryGetFloatAny(element, out var halfDeg, "halfAngle", "halfAngleDeg"))
        {
            halfAngle = halfDeg.Degrees();
            return halfDeg > 0;
        }

        var min = GetFloatAny(element, 0, "coneAngleMin", "ConeAngleMin", "angleMin", "AngleMin");
        var max = GetFloatAny(element, 0, "coneAngleMax", "ConeAngleMax", "angleMax", "AngleMax");
        if (max <= min)
        {
            halfAngle = default;
            return false;
        }
        centerOffset = ((min + max) * 0.5f).Degrees();
        halfAngle = ((max - min) * 0.5f).Degrees();
        return true;
    }

    private static bool TryResolveOriginAndRotation(JsonObject element, in Context ctx, bool relativeToActor, out WPos origin, out Angle rotation, out float actorRadius)
    {
        origin = default;
        actorRadius = 0;
        // splatoon stores the extra rotation of an element in radians; generic payloads may state degrees instead
        rotation = GetFloatAny(element, 0, "AdditionalRotation", "additionalRotation", "rotationRad", "rotation", "Rotation", "refRot", "RefRot").Radians()
            + GetFloatAny(element, 0, "rotationDeg").Degrees();

        var actor = ResolveActor(element, ctx, out var hasActorRef);
        if (hasActorRef && actor == null)
            return false; // the element only shows while its actor is there, so without the actor there is no danger zone
        if (relativeToActor && actor == null)
            return false;

        if (actor != null)
        {
            if (GetBoolAny(element, false, "includeHitbox", "IncludeHitbox"))
                actorRadius += actor.HitboxRadius;
            if (GetBoolAny(element, false, "includeRotation", "IncludeRotation"))
                rotation += actor.Rotation;
        }
        if (ctx.Player != null && GetBoolAny(element, false, "includeOwnHitbox", "IncludeOwnHitbox"))
            actorRadius += ctx.Player.HitboxRadius;

        if (relativeToActor)
        {
            // types 1 and 4 sit on the actor with an optional offset; type 3 draws from the actor to its offset point
            origin = actor!.Position;
            if (GetIntAny(element, -1, "type", "Type") is 1 or 4 && TryGetFloatAny(element, out var ox, "offX", "OffX") && TryGetFloatAny(element, out var oy, "offY", "OffY"))
            {
                var offset = new WDir(ox, oy);
                // splatoon rotates the offset with the actor when the element follows its facing
                origin += GetBoolAny(element, false, "includeRotation", "IncludeRotation") ? offset.Rotate(rotation) : offset;
            }
        }
        else
        {
            // fixed coordinates: splatoon stores the horizontal axes in refX/refY (refZ is height); generic payloads use x/z
            // an actor reference on a fixed element is a condition for showing it, not its position
            if (!TryGetFloatAny(element, out var x, "x", "X", "refX", "RefX")
                || !TryGetFloatAny(element, out var z, "z", "Z", "refY", "RefY", "y", "Y"))
                return false;
            origin = new(x, z);
        }

        if (ctx.Player != null && GetBoolAny(element, false, "FaceMe", "faceMe"))
        {
            var toPlayer = ctx.Player.Position - origin;
            if (toPlayer.LengthSq() > 0.01f)
                rotation = Angle.FromDirection(toPlayer);
        }
        return true;
    }

    // Splatoon picks the actor with one comparison type, or with every populated field when refActorComparisonAnd is set; the cast,
    // buff and targetable conditions then decide whether the element shows at all.
    private static Actor? ResolveActor(JsonObject element, in Context ctx, out bool hasActorRef)
    {
        var name = GetStringAny(element, "", "refActorName", "RefActorName");
        var modelId = GetUIntAny(element, 0, "refActorModelID", "RefActorModelID");
        var objectId = GetUIntAny(element, 0, "refActorObjectID", "RefActorObjectID");
        var dataId = GetUIntAny(element, 0, "refActorDataID", "RefActorDataID", "refActorOID", "RefActorOID");
        var npcId = GetUIntAny(element, 0, "refActorNPCID", "RefActorNPCID");
        var nameId = GetUIntAny(element, 0, "refActorNPCNameID", "RefActorNPCNameID", "refActorNameID", "RefActorNameID");
        var comparison = GetIntAny(element, 0, "refActorComparisonType", "RefActorComparisonType");
        var comparisonAnd = GetBoolAny(element, false, "refActorComparisonAnd", "RefActorComparisonAnd");
        var requireCast = GetBoolAny(element, false, "refActorRequireCast", "RefActorRequireCast");
        var castIds = GetUIntArray(element, "refActorCastId", "RefActorCastId", "refActorCastIds", "RefActorCastIds");
        var castReverse = GetBoolAny(element, false, "refActorCastReverse", "RefActorCastReverse");
        var requireBuff = GetBoolAny(element, false, "refActorRequireBuff", "RefActorRequireBuff");
        var buffIds = GetUIntArray(element, "refActorBuffId", "RefActorBuffId");
        var requireAllBuffs = GetBoolAny(element, false, "refActorRequireAllBuffs", "RefActorRequireAllBuffs");
        var invertBuffs = GetBoolAny(element, false, "refActorRequireBuffsInvert", "RefActorRequireBuffsInvert");
        var onlyTargetable = GetBoolAny(element, false, "onlyTargetable", "OnlyTargetable");
        var onlyUntargetable = GetBoolAny(element, false, "onlyUnTargetable", "OnlyUnTargetable");

        hasActorRef = name.Length > 0 || modelId != 0 || objectId != 0 || dataId != 0 || npcId != 0 || nameId != 0
            || requireCast && castIds.Length > 0 || requireBuff && buffIds.Length > 0 || onlyTargetable || onlyUntargetable;
        if (!hasActorRef)
            return null;

        // a model id or the placeholder comparisons cannot be resolved here, so the element is dropped rather than misplaced
        if (modelId != 0 || comparison is 1 or 5 or 7 or 8 or 9)
        {
            hasActorRef = true;
            return null;
        }

        foreach (var actor in ctx.WS.Actors.Actors.Values)
        {
            if (actor.IsDeadOrDestroyed)
                continue;
            if (!MatchesActorIdentity(actor, comparison, comparisonAnd, name, objectId, dataId, npcId, nameId))
                continue;
            if (onlyTargetable && !actor.IsTargetable || onlyUntargetable && actor.IsTargetable)
                continue;
            if (requireCast && castIds.Length > 0)
            {
                var casting = actor.CastInfo != null && Array.IndexOf(castIds, actor.CastInfo.Action.ID) >= 0;
                if (casting == castReverse)
                    continue;
            }
            if (requireBuff && buffIds.Length > 0 && !BuffsMatch(actor, buffIds, requireAllBuffs, invertBuffs))
                continue;
            return actor;
        }
        return null;
    }

    private static bool MatchesActorIdentity(Actor actor, int comparison, bool comparisonAnd, string name, uint objectId, uint dataId, uint npcId, uint nameId)
    {
        if (comparisonAnd)
            return (name.Length == 0 || NameMatches(actor, name))
                && (objectId == 0 || actor.InstanceID == objectId)
                && (dataId == 0 || actor.OID == dataId)
                && (npcId == 0 || actor.NameID == npcId)
                && (nameId == 0 || actor.NameID == nameId);

        return comparison switch
        {
            0 => name.Length > 0 && NameMatches(actor, name),
            2 => objectId != 0 && actor.InstanceID == objectId,
            3 => dataId != 0 && actor.OID == dataId,
            4 => npcId != 0 && actor.NameID == npcId,
            6 => nameId != 0 && actor.NameID == nameId,
            _ => false
        };
    }

    private static bool NameMatches(Actor actor, string name)
        => actor.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || actor.Name.Contains(name, StringComparison.OrdinalIgnoreCase);

    private static bool BuffsMatch(Actor actor, uint[] buffIds, bool requireAll, bool invert)
    {
        var matches = 0;
        foreach (var buff in buffIds)
            if (actor.FindStatus(buff) != null)
                ++matches;
        var satisfied = requireAll ? matches == buffIds.Length : matches > 0;
        return satisfied != invert;
    }

    // Conditions splatoon evaluates but this importer cannot: rather than showing the element regardless, it is dropped, because a
    // conditional zone imported as a permanent one blocks movement and casts for the whole fight.
    private static bool HasUnsupportedCondition(JsonObject element)
        => GetBoolAny(element, false, "tether", "Tether")
        || GetBoolAny(element, false, "onlyVisible", "OnlyVisible")
        || GetBoolAny(element, false, "LimitDistance", "limitDistance")
        || GetBoolAny(element, false, "refActorObjectLife", "RefActorObjectLife")
        || GetBoolAny(element, false, "refActorUseCastTime", "RefActorUseCastTime")
        || GetBoolAny(element, false, "refActorUseOvercast", "RefActorUseOvercast")
        || GetBoolAny(element, false, "refActorUseBuffTime", "RefActorUseBuffTime")
        || GetBoolAny(element, false, "refActorUseBuffParam", "RefActorUseBuffParam")
        || GetBoolAny(element, false, "refTargetYou", "RefTargetYou")
        || GetUIntAny(element, 0, "refActorTargetingYou", "RefActorTargetingYou") != 0
        || GetUIntAny(element, 0, "refActorNamePlateIconID", "RefActorNamePlateIconID") != 0
        || GetStringAny(element, "", "refActorVFXPath", "RefActorVFXPath").Length > 0
        || GetBoolAny(element, false, "UsePlaceholderAsRefPosition", "usePlaceholderAsRefPosition")
        || GetBoolAny(element, false, "UsePlaceholderAsOffPosition", "usePlaceholderAsOffPosition")
        || HasEntries(element, "refActorPlaceholder", "PlaceholdersRefPosition", "PlaceholdersOffPosition")
        || HasTrueFlagStartingWith(element, "LineAdd");

    private static bool HasEntries(JsonObject element, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
            if (element[key] is JsonArray arr && arr.Count > 0)
                return true;
        return false;
    }

    private static bool HasTrueFlagStartingWith(JsonObject element, string prefix)
    {
        foreach (var (key, value) in element)
            // parsed JSON values wrap a JsonElement, so GetValue<object>() never yields a bool: read the token as a bool instead
            if (key.StartsWith(prefix, StringComparison.Ordinal) && value is JsonValue jv && jv.TryGetValue<bool>(out var flag) && flag)
                return true;
        return false;
    }

    private static bool ZoneMatches(JsonObject layout, ushort currentZone)
    {
        var zones = layout["ZoneLockH"] as JsonArray ?? layout["zoneLockH"] as JsonArray;
        if (zones == null || zones.Count == 0)
            return true;

        var listed = false;
        foreach (var zone in zones)
            if (zone?.GetValue<int>() == currentZone)
            {
                listed = true;
                break;
            }

        // the same list is either the zones the layout applies to or the zones it is excluded from
        return GetBoolAny(layout, false, "IsZoneBlacklist", "isZoneBlacklist") ? !listed : listed;
    }

    // Splatoon display conditions, as evaluated by its own renderer: 1/3 need combat, 2/3 need a duty, 4 needs either, 6/8 forbid
    // combat, 7/8 forbid a duty, 9 needs both, and 5 is driven by triggers we do not evaluate.
    private static bool DisplayConditionMet(int condition, in Context ctx)
    {
        if (condition is 1 or 3 && !ctx.InCombat)
            return false;
        if (condition is 2 or 3 && !ctx.InDuty)
            return false;
        if (condition == 4 && !ctx.InCombat && !ctx.InDuty)
            return false;
        if (condition == 5)
            return false; // driven by triggers
        if (condition is 6 or 8 && ctx.InCombat)
            return false;
        if (condition is 7 or 8 && ctx.InDuty)
            return false;
        if (condition == 9 && (!ctx.InCombat || !ctx.InDuty))
            return false;
        return true;
    }

    private static bool JobAllowed(JsonObject layout, in Context ctx)
    {
        var jobs = layout["JobLockH"] as JsonArray ?? layout["jobLockH"] as JsonArray;
        if (jobs == null || jobs.Count == 0)
            return true;
        if (ctx.Player == null)
            return false;

        var current = (int)ctx.Player.Class;
        foreach (var job in jobs)
            if (job?.GetValue<int>() == current)
                return true;
        return false;
    }

    private static bool HasUnsafeDynamicContent(JsonObject element)
        => element.ContainsKey("script")
        || element.ContainsKey("Script")
        || element.ContainsKey("trigger")
        || element.ContainsKey("Trigger")
        || element.ContainsKey("url")
        || element.ContainsKey("URL")
        || element.ContainsKey("web")
        || element.ContainsKey("Web");

    private static bool IsNonDangerIntent(JsonObject element)
    {
        var intent = GetStringAny(element, "Danger", "intent", "Intent", "BossModIntent");
        return intent.Equals("VisualOnly", StringComparison.OrdinalIgnoreCase)
            || intent.Equals("SafeHint", StringComparison.OrdinalIgnoreCase)
            || intent.Equals("Bait", StringComparison.OrdinalIgnoreCase);
    }


    private static string SafeNamespacePart(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(UnsafeNameChars.Contains(ch) ? '_' : char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    private static string GetStringAny(JsonObject obj, string fallback, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
            if (obj[key] is JsonNode node)
                return node.GetValue<string>();
        return fallback;
    }

    private static int GetIntAny(JsonObject obj, int fallback, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
            if (obj[key] is JsonNode node)
                return node.GetValue<int>();
        return fallback;
    }

    private static uint GetUIntAny(JsonObject obj, uint fallback, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
            if (obj[key] is JsonNode node)
                return node.GetValue<uint>();
        return fallback;
    }

    private static float GetFloatAny(JsonObject obj, float fallback, params ReadOnlySpan<string> keys)
        => TryGetFloatAny(obj, out var value, keys) ? value : fallback;

    private static bool TryGetFloatAny(JsonObject obj, out float value, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
            if (obj[key] is JsonNode node)
            {
                value = node.GetValue<float>();
                return true;
            }
        value = default;
        return false;
    }

    private static bool GetBoolAny(JsonObject obj, bool fallback, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
            if (obj[key] is JsonNode node)
                return node.GetValue<bool>();
        return fallback;
    }

    private static uint[] GetUIntArray(JsonObject obj, params ReadOnlySpan<string> keys)
    {
        foreach (var key in keys)
            if (obj[key] is JsonArray arr)
            {
                var result = new List<uint>(arr.Count);
                foreach (var n in arr)
                    if ((n?.GetValue<uint>() ?? 0) is var v && v != 0)
                        result.Add(v);
                return [.. result];
            }
        return [];
    }
}
