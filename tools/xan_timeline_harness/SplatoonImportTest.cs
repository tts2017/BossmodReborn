using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using BossMod;

namespace XanTimelineHarness;

// Feeds hand-written layouts through SplatoonSafeImport and prints the zones that reach the hints, so the element semantics can be
// checked against Splatoon: donut inner and outer radii, cone direction, fixed coordinates that only use an actor as a condition,
// display conditions and the conditions this importer refuses to guess at.
internal static class SplatoonImportTest
{
    private static readonly DateTime BaseTime = new(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);
    private const ulong PlayerID = 0x10000001;
    private const ulong BossID = 0x40000001;

    private sealed record Case(string Name, string Layout, Func<WPos, bool>? Inside = null, Func<WPos, bool>? Outside = null, int ExpectedZones = 1);

    public static int Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bmr_splatoon_import_test");
        Directory.CreateDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            File.Delete(file);

        var failures = 0;
        foreach (var test in Cases())
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                File.Delete(file);
            File.WriteAllText(Path.Combine(directory, "layout.json"), test.Layout);

            var world = BuildWorld();
            var import = new SplatoonSafeImport(directory);
            ExternalAOEProvider.ClearAll();
            import.Update(world);

            var hints = new AIHints();
            ExternalAOEProvider.ApplyToHints(hints, world.CurrentZone, world.CurrentTime);
            var zones = hints.ForbiddenZones.Count;
            var ok = zones == test.ExpectedZones;
            if (ok && zones > 0)
            {
                var shape = hints.ForbiddenZones[0].shapeDistance;
                if (test.Inside != null)
                    ok = InsideCheck(shape, test.Inside);
                if (ok && test.Outside != null)
                    ok = OutsideCheck(shape, test.Outside);
            }
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {test.Name} zones={zones} expected={test.ExpectedZones}");
            if (!ok)
                ++failures;
        }

        failures += RunLiveChecks();
        Console.WriteLine($"failures={failures}");
        return failures == 0 ? 0 : 1;
    }

    private static bool InsideCheck(ShapeDistance shape, Func<WPos, bool> points)
    {
        foreach (var p in Samples())
            if (points(p) && !shape.Contains(p))
                return false;
        return true;
    }

    private static bool OutsideCheck(ShapeDistance shape, Func<WPos, bool> points)
    {
        foreach (var p in Samples())
            if (points(p) && shape.Contains(p))
                return false;
        return true;
    }

    private static IEnumerable<WPos> Samples()
    {
        for (var x = -30f; x <= 30f; x += 0.5f)
            for (var z = -30f; z <= 30f; z += 0.5f)
                yield return new(x, z);
    }

    private static WorldState BuildWorld()
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "splatoon-import-test");
        world.Execute(new WorldState.OpFrameStart(new(BaseTime, 0, 0, 0, 0, 1), default, default, default));
        world.Execute(new WorldState.OpZoneChange(1122, 1122));
        world.Execute(new ActorState.OpCreate(PlayerID, 0, 0, 0, "Player", 0, ActorType.Player, Class.RPR, 100, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
        world.Execute(new PartyState.OpModify(PartyState.PlayerSlot, new(1, PlayerID, false)));
        world.Execute(new ActorState.OpCombat(PlayerID, true));
        world.Execute(new ActorState.OpCreate(BossID, 0x1234, 1, 0, "Boss", 7777, ActorType.Enemy, Class.None, 100, new Vector4(10, 0, 10, 0), 2f, new(1000000, 1000000, 0, 10000, 10000), true, false, default, default, 0));
        world.Execute(new ActorState.OpCombat(BossID, true));
        return world;
    }

    // The live bridge converts what Splatoon is drawing; these check the geometry it produces, above all the fan angles, which use the
    // opposite sign to this plugin.
    private static int RunLiveChecks()
    {
        var failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} live: {name}");
            if (!ok)
                ++failures;
        }

        var north = Item("cone", center: new(0, 0, 0), outerRadius: 10f, angleMin: -0.3f, angleMax: 0.3f);
        Check("cone points north", SplatoonLiveZones.TryBuildShape(north, out var northShape)
            && northShape.Contains(new(0, 5)) && !northShape.Contains(new(5, 0)) && !northShape.Contains(new(0, -5)));

        // splatoon angle +pi/2 draws towards -x, which is this plugin at -pi/2
        var west = Item("cone", center: new(0, 0, 0), outerRadius: 10f, angleMin: MathF.PI / 2 - 0.3f, angleMax: MathF.PI / 2 + 0.3f);
        Check("cone sign is mirrored", SplatoonLiveZones.TryBuildShape(west, out var westShape)
            && westShape.Contains(new(-5, 0)) && !westShape.Contains(new(5, 0)));

        var full = Item("cone", center: new(0, 0, 0), outerRadius: 8f, angleMin: -MathF.PI, angleMax: MathF.PI);
        Check("full span is a circle", SplatoonLiveZones.TryBuildShape(full, out var fullShape)
            && fullShape.Contains(new(0, 5)) && fullShape.Contains(new(5, 0)) && !fullShape.Contains(new(0, 9)));

        var donut = Item("donut", center: new(0, 0, 0), innerRadius: 5f, outerRadius: 10f);
        Check("donut keeps its hole", SplatoonLiveZones.TryBuildShape(donut, out var donutShape)
            && donutShape.Contains(new(0, 7)) && !donutShape.Contains(new(0, 2)) && !donutShape.Contains(new(0, 12)));

        var line = Item("line", start: new(0, 0, 0), end: new(0, 0, 20), lineRadius: 2f);
        Check("line has width", SplatoonLiveZones.TryBuildShape(line, out var lineShape)
            && lineShape.Contains(new(1, 10)) && !lineShape.Contains(new(5, 10)));

        var tether = Item("line", start: new(0, 0, 0), end: new(0, 0, 20), lineRadius: 0f);
        Check("width-less line is skipped", !SplatoonLiveZones.TryBuildShape(tether, out _));

        Check("marker kinds are skipped", !SplatoonLiveZones.TryBuildShape(Item("dot", center: new(0, 0, 0), radius: 1f), out _)
            && !SplatoonLiveZones.TryBuildShape(Item("text", center: new(0, 0, 0)), out _));

        Check("danger colour filter", SplatoonLiveZones.IsDangerColor(0xC80000FF) && !SplatoonLiveZones.IsDangerColor(0xC800FF00) && !SplatoonLiveZones.IsDangerColor(0x000000FF));

        var tagged = Item("circle", center: new(0, 0, 0), radius: 5f, layout: "P3 bmr avoid", element: "chariot");
        var untagged = Item("circle", center: new(0, 0, 0), radius: 5f, layout: "P3 markers", element: "safe spot");
        Check("tagged layouts mode", SplatoonLiveZones.ShouldImport(tagged, SplatoonLiveZoneMode.TaggedLayouts, "bmr")
            && !SplatoonLiveZones.ShouldImport(untagged, SplatoonLiveZoneMode.TaggedLayouts, "bmr")
            && !SplatoonLiveZones.ShouldImport(tagged, SplatoonLiveZoneMode.TaggedLayouts, ""));
        Check("tag matches element and script names", SplatoonLiveZones.ShouldImport(Item("circle", radius: 5f, element: "BMR donut"), SplatoonLiveZoneMode.TaggedLayouts, "bmr")
            && SplatoonLiveZones.ShouldImport(Item("circle", radius: 5f, ns: "bmr.scripts"), SplatoonLiveZoneMode.TaggedLayouts, "bmr"));
        Check("other modes ignore the tag", SplatoonLiveZones.ShouldImport(untagged, SplatoonLiveZoneMode.All, "bmr")
            && !SplatoonLiveZones.ShouldImport(untagged, SplatoonLiveZoneMode.Off, "bmr"));
        return failures;
    }

    private static (string, string, string, string, string, string, string, uint, Vector3, Vector3, Vector3, float?, float?, float?, float?, float?, float?, float?, float?) Item(
        string kind, Vector3 center = default, Vector3 start = default, Vector3 end = default, uint color = 0xC80000FF,
        float? radius = null, float? innerRadius = null, float? outerRadius = null, float? lineRadius = null,
        float? angleMin = null, float? angleMax = null, string layout = "", string element = "", string ns = "")
        => ("id", "render", ns, layout, element, kind, "DirectX11", color, center, start, end, radius, innerRadius, outerRadius, lineRadius, null, null, angleMin, angleMax);

    private static IEnumerable<Case> Cases()
    {
        // donut: splatoon uses radius as the inner edge and Donut as the width added outside it
        yield return new("donut inner 5 outer 10",
            Layout("""{"type":0,"refX":0,"refY":0,"radius":5,"Donut":5}"""),
            Inside: p => Near(p, 7f), Outside: p => Near(p, 2f) || Near(p, 14f));

        // cone: the angles are degrees around the element rotation, so 0..90 points between +x and +z
        yield return new("cone 0..90 degrees",
            Layout("""{"type":5,"refX":0,"refY":0,"radius":10,"coneAngleMin":0,"coneAngleMax":90}"""),
            Inside: p => Close(p, new(4f, 4f)), Outside: p => Close(p, new(-4f, 4f)));

        // a five degree cone used to be read as five radians and dropped
        yield return new("narrow cone",
            Layout("""{"type":5,"refX":0,"refY":0,"radius":20,"coneAngleMin":-5,"coneAngleMax":5}"""),
            Inside: p => Close(p, new(0f, 10f)), Outside: p => Close(p, new(6f, 10f)));

        // a fixed element that only uses an actor as a condition stays at its coordinates
        yield return new("fixed circle with actor condition",
            Layout("""{"type":0,"refX":0,"refY":0,"radius":3,"refActorComparisonType":6,"refActorNPCNameID":7777}"""),
            Inside: p => Near(p, 1f), Outside: p => Close(p, new(10f, 10f)));

        // the same element without a matching actor produces nothing
        yield return new("fixed circle with missing actor", ExpectedZones: 0,
            Layout: Layout("""{"type":0,"refX":0,"refY":0,"radius":3,"refActorComparisonType":6,"refActorNPCNameID":4242}"""));

        // a relative element does sit on its actor
        yield return new("circle on actor",
            Layout("""{"type":1,"radius":4,"refActorComparisonType":6,"refActorNPCNameID":7777}"""),
            Inside: p => Close(p, new(10f, 10f)), Outside: p => Near(p, 1f));

        // line between two fixed points, with the element radius as half width
        yield return new("line with width",
            Layout("""{"type":2,"refX":0,"refY":0,"offX":0,"offY":20,"radius":2}"""),
            Inside: p => Close(p, new(1f, 10f)), Outside: p => Close(p, new(5f, 10f)));

        yield return new("zone blacklist", ExpectedZones: 0,
            Layout: Layout("""{"type":0,"refX":0,"refY":0,"radius":5}""", "\"ZoneLockH\":[1122],\"IsZoneBlacklist\":true,"));

        yield return new("zone whitelist elsewhere", ExpectedZones: 0,
            Layout: Layout("""{"type":0,"refX":0,"refY":0,"radius":5}""", "\"ZoneLockH\":[999],"));

        yield return new("display condition forbids combat", ExpectedZones: 0,
            Layout: Layout("""{"type":0,"refX":0,"refY":0,"radius":5}""", "\"DCond\":6,"));

        yield return new("trigger driven layout", ExpectedZones: 0,
            Layout: Layout("""{"type":0,"refX":0,"refY":0,"radius":5}""", "\"UseTriggers\":true,"));

        yield return new("tether element", ExpectedZones: 0,
            Layout: Layout("""{"type":3,"radius":2,"tether":true,"refActorComparisonType":6,"refActorNPCNameID":7777}"""));

        yield return new("buff condition not met", ExpectedZones: 0,
            Layout: Layout("""{"type":1,"radius":4,"refActorComparisonType":6,"refActorNPCNameID":7777,"refActorRequireBuff":true,"refActorBuffId":[1234]}"""));

        // a line whose length splatoon extends by a hitbox cannot be sized here, so it is dropped (the flag used to be misread)
        yield return new("line lengthened by hitbox", ExpectedZones: 0,
            Layout: Layout("""{"type":2,"refX":0,"refY":0,"offX":0,"offY":20,"radius":2,"LineAddHitboxLengthY":true}"""));

        // the same flag set to false keeps the line
        yield return new("line with hitbox flag off",
            Layout("""{"type":2,"refX":0,"refY":0,"offX":0,"offY":20,"radius":2,"LineAddHitboxLengthY":false}"""),
            Inside: p => Close(p, new(1f, 10f)), Outside: p => Close(p, new(5f, 10f)));

        // a value of the wrong JSON type drops only its own element; the next element of the layout is still imported
        yield return new("malformed element skipped",
            Layout("""{"type":0,"refX":0,"refY":0,"radius":"five"},{"type":0,"refX":0,"refY":0,"radius":3}"""),
            Inside: p => Near(p, 1f), Outside: p => Close(p, new(10f, 10f)));
    }

    private static string Layout(string element, string extra = "")
        => $$"""{"Enabled":true,{{extra}}"ElementsL":[{{element}}]}""";

    private static bool Near(WPos p, float distance) => Math.Abs((p - new WPos(0, 0)).Length() - distance) < 0.26f;

    private static bool Close(WPos p, WPos target) => (p - target).Length() < 0.26f;
}
