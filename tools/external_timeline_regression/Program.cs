using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BossMod;
using BossMod.Autorotation;
using EncounterTimeline;

var failures = new List<string>();
var tests = 0;
void Check(string name, Action test)
{
    ++tests;
    try { test(); }
    catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
}
void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

var fixtureRoot = Path.Combine(Path.GetTempPath(), "bossmod-external-timeline-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(fixtureRoot, "timeline"));
File.WriteAllText(Path.Combine(fixtureRoot, "timelines.csv"), "777,\"test.txt\"\n");
EventTriggerTimelineCatalog Fixture(string contents)
{
    File.WriteAllText(Path.Combine(fixtureRoot, "timeline", "test.txt"), contents);
    return EventTriggerTimelineCatalog.Load(fixtureRoot);
}
// A cactbot layout: resources/zone_id.ts maps names to ids, ui/raidboss/data/<x>.ts names the timeline file next to it.
string CactbotFixture(string zoneName, int zoneID, string timelineContents)
{
    var root = Path.Combine(fixtureRoot, "cactbot");
    Directory.CreateDirectory(Path.Combine(root, "resources"));
    Directory.CreateDirectory(Path.Combine(root, "ui", "raidboss", "data", "t"));
    File.WriteAllText(Path.Combine(root, "resources", "zone_id.ts"), $"export default {{\n  {zoneName}: {zoneID},\n}};\n");
    File.WriteAllText(Path.Combine(root, "ui", "raidboss/data/t", "t.ts"), $"const triggerSet = {{\n  zoneId: ZoneId.{zoneName},\n  timelineFile: 't.txt',\n  triggers: [],\n}};\n");
    File.WriteAllText(Path.Combine(root, "ui", "raidboss", "data", "t", "t.txt"), timelineContents);
    return root;
}

try
{
    Check("commented sync is not executable", () =>
    {
        var timeline = Fixture("5.0 \"Hit\" # Ability { id: \"1234\", source: \"Boss\" }\n").TimelineForZone(777);
        Require(timeline.Actions.Count == 0, "comment was parsed as an action");
    });
    Check("named forward jump is not flattened", () =>
    {
        var catalog = Fixture("5.0 \"Hit\" Ability { id: \"1234\", source: \"Boss\" } jump \"branch\"\n1000.0 label \"branch\"\n1005.0 \"Other\"\n");
        var definition = ExternalTimelineManifestGenerator.Generate(catalog, null).Timelines.Single();
        Require(!definition.AutomaticFallback, "named branch accepted as linear");
        Require(definition.Sequences.SelectMany(s => s.States).All(s => s.Name != "branch"), "label became a mechanic");
    });
    Check("forcejump is not flattened", () =>
    {
        var definition = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"Hit\" forcejump 1000\n1005.0 \"Other\"\n"), null).Timelines.Single();
        Require(!definition.AutomaticFallback, "forcejump accepted as linear");
    });
    Check("unresolved jump is not flattened", () =>
    {
        var definition = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"Hit\" jump \"missing\"\n"), null).Timelines.Single();
        Require(!definition.AutomaticFallback, "missing jump target accepted");
    });
    Check("status ID is not an action ID", () =>
    {
        var timeline = Fixture("5.0 \"Hit\" Ability { id: \"1234\", source: \"Boss\" }\n").TimelineForZone(777);
        CactbotTrigger[] triggers = [new(777, "Test", "Status", CactbotEventType.GainsEffect, [0x1234], [], [], TimelineStateHint.Raidwide, false, "test.ts", 1)];
        var skeleton = EventTriggerTimelineSkeletonGenerator.Generate(timeline, 777, triggers);
        Require(!skeleton.Contains("StateHint.Raidwide"), "status ID created a raidwide flag");
    });
    Check("simultaneous targetability and cast are both kept", () =>
    {
        var definition = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"--untargetable--\"\n5.0 \"Cast\" StartsUsing { id: \"1234\", source: \"Boss\" }\n"), null).Timelines.Single();
        var states = definition.Sequences.SelectMany(sequence => sequence.States).ToArray();
        Require(states.Any(state => state.Kind == ExternalTimelineStateKind.Untargetable), "cast discarded the downtime transition");
        Require(states.Any(state => state.Kind == ExternalTimelineStateKind.CastStart), "cast sync was discarded");
    });
    Check("loop boundary is retained without a fake label event", () =>
    {
        var definition = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 label \"loop\"\n5.0 \"First\" Ability { id: \"1234\", source: \"Boss\" }\n15.0 \"Repeat\" forcejump \"loop\"\n25.0 \"Preview\"\n"), null).Timelines.Single();
        Require(definition.AutomaticFallback && definition.Sequences[0].PredictionEndTime == 15, "loop boundary missing");
        Require(definition.Sequences[0].States.All(state => state.Name != "loop"), "label is visible");
    });
    Check("ability lines become AbilityUsed sync points", () =>
    {
        var def = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"Hit\" Ability { id: \"1234\", source: \"Boss\" }\n9.0 \"Cast\" StartsUsing { id: \"1235\", source: \"Boss\" }\n"), null).Timelines.Single();
        var states = def.Sequences[0].States;
        Require(states[0].Kind == ExternalTimelineStateKind.AbilityUsed && states[0].IDs.SequenceEqual([0x1234u]), "ability line kind");
        Require(states[1].Kind == ExternalTimelineStateKind.CastStart, "cast line kind");
        Require(def.Source == ExternalTimelineSource.EventTrigger && def.Confidence == 1f, "event trigger source");
    });
    Check("boss-only downtime becomes a NoTarget window", () =>
    {
        var def = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n30.0 \"--targetable--\"\n35.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n"), null).Timelines.Single();
        var windows = def.Sequences[0].Windows!;
        Require(windows.Any(w => w is { Kind: ExternalTimelineWindowKind.BossUntargetable, Start: 10, End: 30 }), "boss window");
        Require(windows.Any(w => w is { Kind: ExternalTimelineWindowKind.NoTarget, Start: 10, End: 30, Confidence: 1f }), "no target window");
        Require(!windows.Any(w => w.Kind == ExternalTimelineWindowKind.AddsPresent), "no adds");
    });
    Check("downtime with other casters is AddsPresent, not NoTarget", () =>
    {
        var def = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n15.0 \"Bite\" Ability { id: \"9\", source: \"Add\" }\n30.0 \"--targetable--\"\n35.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n"), null).Timelines.Single();
        var windows = def.Sequences[0].Windows!;
        Require(windows.Any(w => w is { Kind: ExternalTimelineWindowKind.AddsPresent, Start: 10, End: 30 }), "adds window");
        Require(!windows.Any(w => w.Kind == ExternalTimelineWindowKind.NoTarget), "no NoTarget window with adds");
    });
    Check("downtime with an adds title is AddsPresent", () =>
    {
        var def = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n12.0 \"--adds targetable--\"\n30.0 \"--targetable--\"\n"), null).Timelines.Single();
        Require(def.Sequences[0].Windows!.Any(w => w.Kind == ExternalTimelineWindowKind.AddsPresent), "adds title");
    });
    Check("open downtime has no end", () =>
    {
        var def = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n20.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n"), null).Timelines.Single();
        Require(def.Sequences[0].Windows!.Single(w => w.Kind == ExternalTimelineWindowKind.BossUntargetable).End == null, "open end");
    });
    Check("boss is picked per sequence, not over the whole file", () =>
    {
        // Boss1 dominates the file; Boss2's own casts inside Boss2's downtime must not count as "something else".
        var def = ExternalTimelineManifestGenerator.Generate(Fixture(
            "5.0 \"A\" Ability { id: \"1\", source: \"Boss1\" }\n10.0 \"B\" Ability { id: \"2\", source: \"Boss1\" }\n20.0 \"C\" Ability { id: \"3\", source: \"Boss1\" }\n30.0 \"D\" Ability { id: \"4\", source: \"Boss1\" }\n60.0 \"E\" Ability { id: \"5\", source: \"Boss1\" }\n"
            + "1000.0 \"X\" Ability { id: \"11\", source: \"Boss2\" }\n1010.0 \"--untargetable--\"\n1015.0 \"Y\" Ability { id: \"12\", source: \"Boss2\" }\n1030.0 \"--targetable--\"\n1035.0 \"Z\" Ability { id: \"13\", source: \"Boss2\" }\n"), null).Timelines.Single();
        Require(def.Sequences.Count == 2, $"expected two sequences, got {def.Sequences.Count}");
        var windows = def.Sequences[1].Windows!;
        Require(windows.Any(w => w is { Kind: ExternalTimelineWindowKind.NoTarget, Start: 1010, End: 1030 }), "second boss window is not NoTarget");
        Require(!windows.Any(w => w.Kind == ExternalTimelineWindowKind.AddsPresent), "second boss's own casts counted as adds");
    });
    Check("spawn on the untargetable boundary makes the window AddsPresent", () =>
    {
        var byEvent = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n10.0 \"Add\" AddedCombatant { name: \"Add\" }\n30.0 \"--targetable--\"\n35.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n"), null).Timelines.Single();
        Require(byEvent.Sequences[0].Windows!.Any(w => w is { Kind: ExternalTimelineWindowKind.AddsPresent, Start: 10, End: 30 }), "boundary AddedCombatant ignored");
        var byCast = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n10.0 \"Bite\" Ability { id: \"9\", source: \"Add\" }\n30.0 \"--targetable--\"\n35.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n"), null).Timelines.Single();
        Require(byCast.Sequences[0].Windows!.Any(w => w is { Kind: ExternalTimelineWindowKind.AddsPresent, Start: 10, End: 30 }), "boundary non-boss cast ignored");
    });
    Check("cactbot timelines are emitted as their own source, event trigger kept", () =>
    {
        Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n");
        var cactbot = CactbotFixture("TestZone", 777, "6.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n");
        var catalog = EventTriggerTimelineCatalog.Load(fixtureRoot, cactbot);
        Require(catalog.CactbotTimelinesByZone.ContainsKey(777), "cactbot mapping");
        var defs = ExternalTimelineManifestGenerator.Generate(catalog, null).Timelines.Where(t => t.ZoneID == 777).ToArray();
        Require(defs.Length == 2, $"expected both sources, got {defs.Length}");
        Require(defs.Any(d => d.Source == ExternalTimelineSource.EventTrigger) && defs.Any(d => d.Source == ExternalTimelineSource.Cactbot), "sources");
    });
    Check("cactbot twin identical to event trigger is not emitted", () =>
    {
        const string same = "5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n30.0 \"--targetable--\"\n";
        Fixture(same);
        var catalog = EventTriggerTimelineCatalog.Load(fixtureRoot, CactbotFixture("TestZone", 777, same));
        Require(catalog.CactbotTimelinesByZone.ContainsKey(777), "cactbot mapping");
        var defs = ExternalTimelineManifestGenerator.Generate(catalog, null).Timelines.Where(t => t.ZoneID == 777).ToArray();
        Require(defs.Length == 1 && defs[0].Source == ExternalTimelineSource.EventTrigger, $"expected the event trigger entry only, got {string.Join(",", defs.Select(d => d.Source))}");
    });
    Check("cactbot-only zone is still emitted", () =>
    {
        Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n");
        var catalog = EventTriggerTimelineCatalog.Load(fixtureRoot, CactbotFixture("OtherZone", 778, "6.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n"));
        var defs = ExternalTimelineManifestGenerator.Generate(catalog, null).Timelines;
        Require(defs.Any(t => t.ZoneID == 778 && t.Source == ExternalTimelineSource.Cactbot), "cactbot-only zone missing");
        Require(defs.Any(t => t.ZoneID == 777 && t.Source == ExternalTimelineSource.EventTrigger), "event trigger zone missing");
    });
    Check("zone 134 test timeline is excluded", () =>
    {
        File.WriteAllText(Path.Combine(fixtureRoot, "timelines.csv"), "777,\"test.txt\"\n134,\"test.txt\"\n");
        var defs = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n"), null).Timelines;
        Require(defs.All(d => d.ZoneID != 134), "zone 134 present");
        File.WriteAllText(Path.Combine(fixtureRoot, "timelines.csv"), "777,\"test.txt\"\n");
    });
    Check("legacy manifest json loads with default source and confidence", () =>
    {
        var json = """{"ZoneID":1,"SourceFile":"x.txt","AutomaticFallback":true,"Sequences":[{"Index":0,"StartTime":0,"States":[{"Time":1,"Name":"a","Kind":0,"IDs":[5],"Hint":0}],"PredictionEndTime":null}]}""";
        var def = JsonSerializer.Deserialize<ExternalPlannerTimeline.TimelineDefinition>(json)!;
        Require(def.Source == ExternalPlannerTimeline.TimelineSource.EventTrigger, "default source");
        Require(def.Confidence == 1f, "default confidence");
        Require(def.Sequences[0].BossOIDs == null && def.Sequences[0].Windows == null, "optional lists default to null");
        Require(def.Sequences[0].Branch == null, "a sequence without a branch loads with none");
    });
    Check("new fields round-trip through json", () =>
    {
        var def = new ExternalPlannerTimeline.TimelineDefinition(2, "r.json", false,
            [new(0, 0, [new(3, "hit", ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, [7], 0)], null, [0x1234], [new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 10, 25, 0.75f)])],
            ExternalPlannerTimeline.TimelineSource.Replay, 0.9f);
        var back = JsonSerializer.Deserialize<ExternalPlannerTimeline.TimelineDefinition>(JsonSerializer.Serialize(def))!;
        Require(back.Source == ExternalPlannerTimeline.TimelineSource.Replay && back.Confidence == 0.9f, "source/confidence lost");
        Require(back.Sequences[0].BossOIDs![0] == 0x1234, "boss oid lost");
        Require(back.Sequences[0].Windows![0] is { Kind: ExternalPlannerTimeline.TimelineWindowKind.NoTarget, Start: 10, End: 25, Confidence: 0.75f }, "window lost");
        Require(back.Sequences[0].States[0].Kind == ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, "AbilityUsed lost");
    });

    Service.LuminaGameData = new Lumina.GameData(args.Length > 0 ? args[0] : @"C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
    Service.Config.Initialize();
    Service.Config.Get<BossModuleConfig>().UseExternalPlannerTimelines = true; // the tool exercises the planner fallback, which is off by default
    Check("replay builder keeps the new actor's identity when an instance id is reused", () =>
    {
        // SanDoriaTheSecondWalk: 40000D3E was an EventObj at t=0, destroyed, then reused by the boss (0x460F) much later.
        const ulong id = 0x40000D3E;
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using var builder = new ReplayBuilder("synthetic");
        builder.Start(TimeSpan.TicksPerSecond, "synthetic");
        var frame = 0u;
        void Frame(float seconds) => builder.AddOp(new WorldState.OpFrameStart(new FrameState(t0.AddSeconds(seconds), 0, ++frame, 1, 1, 1), default, default, default));
        Frame(0);
        builder.AddOp(new ActorState.OpCreate(id, 0x1E932D, 0, 0x111, "Obj", 0, ActorType.EventObj, Class.None, 0, default, 1, default, false, false, 0, 0, 0));
        Frame(1);
        builder.AddOp(new ActorState.OpDestroy(id));
        Frame(5);
        builder.AddOp(new ActorState.OpCreate(id, 0x460F, 1, 0x222, "Boss", 0, ActorType.Enemy, Class.None, 100, default, 5, new(1000, 1000, 0, 0, 0), true, false, 0x1234, 0, 0));
        Frame(10);
        var participants = builder.Finish().Participants.Where(p => p.InstanceID == id).ToList();
        Require(participants.Count == 2, $"participants for the reused id: {participants.Count}");
        Require(participants[0] is { OID: 0x1E932D, Type: ActorType.EventObj, LayoutID: 0x111, OwnerID: 0 }, $"first: {participants[0].OID:X}/{participants[0].Type}/{participants[0].LayoutID:X}/{participants[0].OwnerID:X}");
        Require(participants[1] is { OID: 0x460F, Type: ActorType.Enemy, LayoutID: 0x222, OwnerID: 0x1234 }, $"second: {participants[1].OID:X}/{participants[1].Type}/{participants[1].LayoutID:X}/{participants[1].OwnerID:X}");
    });
    Check("replay parsing does not follow the shared config", () =>
    {
        // Replays are parsed on background threads (the automatic timelines, the replay list); a subscription to the shared config
        // would run there whenever the config is changed on the draw thread. Counted through the event's delegate, without firing
        // it (other subscribers in this process would react).
        var config = BossModuleManager.Config;
        var field = typeof(Event).GetField("_ev", BindingFlags.NonPublic | BindingFlags.Instance)!;
        int Subscribers() => (field.GetValue(config.Modified) as Delegate)?.GetInvocationList().Length ?? 0;
        var before = Subscribers();
        var showDemo = config.ShowDemo;
        try
        {
            config.ShowDemo = true; // read by each manager as it is made; not fired
            using (var live = new BossModuleManager(new WorldState(TimeSpan.TicksPerSecond, "test")))
                Require(Subscribers() == before + 1 && live.PendingModules.Any(m => m is DemoModule), "a default manager does not follow the config");
            Require(Subscribers() == before, "a disposed manager is still subscribed");
            using (var replay = new BossModuleManager(new WorldState(TimeSpan.TicksPerSecond, "test"), followConfigChanges: false))
                Require(Subscribers() == before && !replay.PendingModules.Concat(replay.LoadedModules).Any(m => m is DemoModule), "a replay manager follows the config");
            using (var builder = new ReplayBuilder("synthetic"))
                Require(Subscribers() == before, "the replay builder's manager subscribes to the config");
        }
        finally
        {
            config.ShowDemo = showDemo;
        }
    });
    var info = BossModuleRegistry.RegisteredModules.Values.Single(i => i.ModuleType.Name.Contains("CausticGrebuloff", StringComparison.Ordinal));
    Check("planner is available before entering duty", () => Require(info.PlanLevel == 90, $"PlanLevel={info.PlanLevel}"));
    Check("offline and in-duty timelines agree", () =>
    {
        using var offline = BossModuleRegistry.CreateModuleForConfigPlanning(info.ModuleType)!;
        var duty = Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(info.GroupID)!.Value;
        var world = new WorldState(TimeSpan.TicksPerSecond, "synthetic") { CurrentZone = (ushort)duty.TerritoryType.RowId, CurrentCFCID = (ushort)info.GroupID };
        using var live = BossModuleRegistry.CreateModule(info, world, new(1, info.PrimaryActorOID, -1, 0, "Boss", 0, ActorType.Enemy, Class.None, 90, default))!;
        var offlineTree = new StateMachineTree(offline.StateMachine);
        var liveTree = new StateMachineTree(live.StateMachine);
        Require(offlineTree.Nodes.Count > 1, "offline timeline is still trivial");
        Require(offlineTree.Nodes.Select(p => (p.Key, p.Value.Time)).SequenceEqual(liveTree.Nodes.Select(p => (p.Key, p.Value.Time))), "offline/live timelines differ");
        Require(offlineTree.TotalBranches == 1, "unrelated dungeon bosses are planner branches");
        Require(Math.Abs(offlineTree.Nodes.Values.First(n => n.State.Name == "Miasmata").Time - 18.1f) < 0.01f, "opening hit time shifted");
        _ = new PlanExecution(offline, null);
    });

    var zone = (ushort)Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(info.GroupID)!.Value.TerritoryType.RowId;
    try
    {
        var synthetic = new ExternalTimelineDefinition(zone, "synthetic.txt", true,
            [new ExternalTimelineSequence(0, 0, [
                new(2, "Opening raidwide", ExternalTimelineStateKind.Timeout, [0x653C], TimelineStateHint.Raidwide),
                new(5, "", ExternalTimelineStateKind.CastStart, [0x653C], TimelineStateHint.None),
                new(10, "Final raidwide", ExternalTimelineStateKind.Timeout, [0x653C], TimelineStateHint.Raidwide)])]);
        TimelineStore.SetCandidatesForTesting(zone, [JsonSerializer.Deserialize<ExternalPlannerTimeline.TimelineDefinition>(JsonSerializer.Serialize(synthetic))!]);
        Check("opening events and final flag survive", () =>
        {
            using var module = BossModuleRegistry.CreateModuleForConfigPlanning(info.ModuleType)!;
            var tree = new StateMachineTree(module.StateMachine);
            Require(tree.Nodes.Values.Any(n => n.State.Name == "Opening raidwide" && n.Time == 2), "opening event was discarded");
            var final = tree.Nodes.Values.Single(n => n.State.Name == "Final raidwide");
            Require(final.State.NextStates?.Length == 1, "final flag never transitions into a successor");
            Require(tree.Phases[0].Duration >= 10000, "last imported event became a predicted kill");
        });
        Check("original component callbacks survive replacement", () =>
        {
            using var module = BossModuleRegistry.CreateModuleForConfigPlanning(info.ModuleType)!;
            var callbacks = new List<string>();
            var originalState = new StateMachine.State { Name = "Enrage", Duration = 10000, Enter = () => callbacks.Add("state enter"), Exit = () => callbacks.Add("state exit") };
            var originalPhase = new StateMachine.Phase(originalState, "Original") { Enter = () => callbacks.Add("phase enter"), Exit = () => callbacks.Add("phase exit") };
            var replacement = ExternalPlannerTimeline.Apply(module, new StateMachine([originalPhase]));
            replacement.Start(DateTime.UnixEpoch);
            replacement.Update(DateTime.UnixEpoch.AddSeconds(2));
            Require(callbacks.SequenceEqual(["phase enter", "state enter"]), "state exit ran at the first imported event");
            replacement.Reset();
            Require(callbacks.SequenceEqual(["phase enter", "state enter", "state exit", "phase exit"]), "callback order changed");
        });
        Check("native timeline is preserved", () =>
        {
            using var module = BossModuleRegistry.CreateModuleForConfigPlanning(info.ModuleType)!;
            var native = new StateMachine([new(new StateMachine.State { Name = "Native", Duration = 123, NextStates = [new() { Name = "Native end", Duration = 10 }] }, "Native")]);
            Require(ReferenceEquals(native, ExternalPlannerTimeline.Apply(module, native)), "native state machine changed");
        });
        Check("apply is suppressed on the flagged thread only", () =>
        {
            using var module = BossModuleRegistry.CreateModuleForConfigPlanning(info.ModuleType)!;
            var original = new StateMachine([new(new StateMachine.State { Name = "Enrage", Duration = 10000 }, "Original")]);
            ExternalPlannerTimeline.SuppressApply = true;
            try
            {
                Require(ReferenceEquals(original, ExternalPlannerTimeline.Apply(module, original)), "suppressed apply replaced the state machine");
                StateMachine? other = null;
                Exception? error = null;
                // An exception escaping a thread would end the whole regression run: it is carried back and fails this check.
                var thread = new Thread(() =>
                {
                    try { other = ExternalPlannerTimeline.Apply(module, original); }
                    catch (Exception ex) { error = ex; }
                });
                thread.Start();
                thread.Join();
                Require(error == null, $"apply on another thread threw: {error?.Message}");
                Require(other != null && !ReferenceEquals(original, other), "suppression reached another thread");
            }
            finally { ExternalPlannerTimeline.SuppressApply = false; }
        });
        Check("the follower keeps running on a state machine rebuilt from the import and stays out of a native one", () =>
        {
            using var rebuilt = BossModuleRegistry.CreateModuleForConfigPlanning(info.ModuleType)!;
            Require(rebuilt.StateMachineFromTimeline, "the import did not replace the trivial state machine");
            Require(ExternalTimelineHints.ShouldRun(rebuilt), "the follower stops on a state machine rebuilt from the import");
            var nativeInfo = BossModuleRegistry.RegisteredModules.Values.Single(i => i.ModuleType.Name == "A35ShinryuParadox");
            using var native = BossModuleRegistry.CreateModuleForConfigPlanning(nativeInfo.ModuleType)!;
            Require(!native.StateMachineFromTimeline, "a native state machine is marked as rebuilt from the import");
            Require(!ExternalTimelineHints.ShouldRun(native), "the follower runs next to a native state machine");
        });
        Check("MechanicForecast vouches for a native state machine that knows the coming seconds, not for a rebuilt one or with the hints off", () =>
        {
            var ws = new WorldState(TimeSpan.TicksPerSecond, "synthetic");
            var nativeInfo = BossModuleRegistry.RegisteredModules.Values.Single(i => i.ModuleType.Name == "A35ShinryuParadox");
            using var native = BossModuleRegistry.CreateModuleForConfigPlanning(nativeInfo.ModuleType)!;
            native.StateMachine.Start(ws.CurrentTime);
            Require(MechanicForecast.Build(MechanicHintStrategy.All, ws, new AIHints(), native).StateForecast, "a native state machine at its pull does not vouch");
            Require(!MechanicForecast.Build(MechanicHintStrategy.Off, ws, new AIHints(), native).StateForecast, "the Off mode vouches");
            Require(!MechanicForecast.Build(MechanicHintStrategy.ForecastOnly, ws, new AIHints(), native).StateForecast, "the ForecastOnly mode vouches for a state machine");
            using var rebuilt = BossModuleRegistry.CreateModuleForConfigPlanning(info.ModuleType)!;
            rebuilt.StateMachine.Start(ws.CurrentTime);
            Require(!MechanicForecast.Build(MechanicHintStrategy.All, ws, new AIHints(), rebuilt).StateForecast, "a state machine rebuilt from the import vouches");
        });
        Check("fresh cast on state entry is accepted; previous cast is ignored", () =>
        {
            using var module = BossModuleRegistry.CreateModuleForConfigPlanning(info.ModuleType)!;
            var state = new StateMachineTree(module.StateMachine).Nodes.Values.Single(node => node.State.ID == 0).State;
            module.PrimaryActor.CastInfo = new() { Action = new(ActionType.Spell, 0x653C), TotalTime = 5, ElapsedTime = 4 };
            state.Enter?.Invoke();
            Require(state.Update!(0) == -1, "previous cast advanced the next sync");
            module.PrimaryActor.CastInfo = new() { Action = new(ActionType.Spell, 0x653C), TotalTime = 5 };
            state.Enter?.Invoke();
            Require(state.Update!(0) == 0, "fresh cast on state entry was ignored");
        });
    }
    finally { TimelineStore.SetCandidatesForTesting(zone, null); }

    Check("priority order: replay > user > fflogs > cactbot > event trigger, then confidence", () =>
    {
        ExternalPlannerTimeline.TimelineDefinition Make(ExternalPlannerTimeline.TimelineSource source, float confidence) => new(5, source.ToString(), true, [], source, confidence);
        var ranked = TimelineStore.Rank([Make(ExternalPlannerTimeline.TimelineSource.EventTrigger, 1f), Make(ExternalPlannerTimeline.TimelineSource.Cactbot, 1f), Make(ExternalPlannerTimeline.TimelineSource.FFLogs, 1f), Make(ExternalPlannerTimeline.TimelineSource.Replay, 0.3f), Make(ExternalPlannerTimeline.TimelineSource.Replay, 0.8f), Make(ExternalPlannerTimeline.TimelineSource.User, 1f)]);
        Require(ranked.Select(t => t.Source).SequenceEqual([ExternalPlannerTimeline.TimelineSource.Replay, ExternalPlannerTimeline.TimelineSource.Replay, ExternalPlannerTimeline.TimelineSource.User, ExternalPlannerTimeline.TimelineSource.FFLogs, ExternalPlannerTimeline.TimelineSource.Cactbot, ExternalPlannerTimeline.TimelineSource.EventTrigger]), "rank order");
        Require(ranked[0].Confidence == 0.8f, "confidence tie-break");
    });
    Check("user directory overrides embedded, broken file is skipped, legacy file loads", () =>
    {
        var dir = Path.Combine(fixtureRoot, "timelines");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
        File.WriteAllText(Path.Combine(dir, "legacy.json"), """{"Timelines":[{"ZoneID":60001,"SourceFile":"legacy.txt","AutomaticFallback":true,"Sequences":[]}]}""");
        File.WriteAllText(Path.Combine(dir, "user.json"), """{"Timelines":[{"ZoneID":1122,"SourceFile":"mine.json","AutomaticFallback":true,"Sequences":[],"Source":3,"Confidence":1}]}""");
        var saved = TimelineStore.UserDirectory;
        try
        {
            TimelineStore.UserDirectory = dir;
            TimelineStore.Reload();
            Require(TimelineStore.ForZone(60001) is { Source: ExternalPlannerTimeline.TimelineSource.EventTrigger, Confidence: 1f }, "legacy defaults");
            Require(TimelineStore.ForZone(1122) is { Source: ExternalPlannerTimeline.TimelineSource.User }, "user override for TOP");
            Require(TimelineStore.CandidatesForZone(1122).Count >= 2, "embedded candidate kept behind user");
            Require(TimelineStore.ForZone(193) != null, "embedded zones still load next to a broken file");
        }
        finally
        {
            TimelineStore.UserDirectory = saved;
            TimelineStore.Reload();
        }
    });
    Check("missing user folder keeps the embedded set and later lookups do not throw", () =>
    {
        var saved = TimelineStore.UserDirectory;
        try
        {
            TimelineStore.UserDirectory = Path.Combine(fixtureRoot, "missing-timelines");
            TimelineStore.Reload();
            Require(TimelineStore.ForZone(193) != null, "embedded set lost");
            Require(TimelineStore.ForZone(193) != null, "second lookup after reload");
            Require(TimelineStore.AllTimelines().Any(t => t.ZoneID == 193), "AllTimelines after reload");
        }
        finally
        {
            TimelineStore.UserDirectory = saved;
            TimelineStore.Reload();
        }
    });
    Check("store generation advances on reload and unsorted user windows are sorted", () =>
    {
        var dir = Path.Combine(fixtureRoot, "timelines-gen");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "unsorted.json"), """{"Timelines":[{"ZoneID":60003,"SourceFile":"u.json","AutomaticFallback":true,"Sequences":[{"Index":0,"StartTime":0,"States":[],"PredictionEndTime":null,"Windows":[{"Kind":1,"Start":40,"End":60,"Confidence":1},{"Kind":1,"Start":10,"End":30,"Confidence":1}]}],"Source":3,"Confidence":1}]}""");
        var saved = TimelineStore.UserDirectory;
        try
        {
            TimelineStore.UserDirectory = dir;
            var before = TimelineStore.Generation;
            TimelineStore.Reload();
            Require(TimelineStore.Generation > before, $"generation {before} -> {TimelineStore.Generation}");
            var windows = TimelineStore.ForZone(60003)!.Sequences[0].Windows!;
            Require(windows.Select(w => w.Start).SequenceEqual([10f, 40f]), "windows not sorted by start");
            var again = TimelineStore.Generation;
            TimelineStore.Reload();
            Require(TimelineStore.Generation == again + 1, "second reload did not advance the generation by one");
        }
        finally
        {
            TimelineStore.UserDirectory = saved;
            TimelineStore.Reload();
        }
    });
    Check("background reloads collapse and end on the latest folder", () =>
    {
        var dir = Path.Combine(fixtureRoot, "timelines-bg");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bg.json"), """{"Timelines":[{"ZoneID":60004,"SourceFile":"bg.json","AutomaticFallback":true,"Sequences":[{"Index":0,"StartTime":0,"States":[],"PredictionEndTime":null,"Windows":[]}],"Source":3,"Confidence":1}]}""");
        var saved = TimelineStore.UserDirectory;
        try
        {
            var before = TimelineStore.Generation;
            // the folder field fires once per keystroke: an intermediate folder first, the final one right after
            TimelineStore.UserDirectory = Path.Combine(fixtureRoot, "timelines-b");
            TimelineStore.ReloadInBackground();
            TimelineStore.UserDirectory = dir;
            TimelineStore.ReloadInBackground();
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (TimelineStore.BackgroundReloadActive && DateTime.UtcNow < deadline)
                Thread.Sleep(5);
            Require(!TimelineStore.BackgroundReloadActive, "background reload did not finish");
            Require(TimelineStore.ForZone(60004) != null, "latest folder not applied");
            Require(TimelineStore.ForZone(193) != null, "embedded set lost");
            var after = TimelineStore.Generation;
            Require(after > before && after <= before + 2, $"generation {before} -> {after}");
        }
        finally
        {
            TimelineStore.UserDirectory = saved;
            TimelineStore.Reload();
        }
    });
    Check("AllTimelines skips an empty test override", () =>
    {
        try
        {
            TimelineStore.SetCandidatesForTesting(60002, []);
            var all = TimelineStore.AllTimelines().ToList();
            Require(all.All(t => t.ZoneID != 60002), "empty override listed");
            Require(all.Any(t => t.ZoneID == 193), "embedded zones missing");
        }
        finally { TimelineStore.SetCandidatesForTesting(60002, null); }
    });
    Check("wired follower: a confirmed clock says \"no long loss within the horizon\" with an empty snapshot, and nothing it cannot vouch for", () =>
    {
        // casts A at 5 s and B at 10 s, then a 20 s NoTarget window at 60-80 that ends the sequence
        const ushort wiredZone = 60011;
        const uint bossOID = 0x6001;
        const ulong bossID = 0x1001;
        const uint castA = 0x5011;
        const uint castB = 0x5012;
        var config = Service.Config.Get<BossModuleConfig>();
        var savedHints = config.UseExternalTimelineHints;
        config.UseExternalTimelineHints = true;
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            var sequence = new ExternalPlannerTimeline.TimelineSequence(0, 0,
                [new(5, "A", ExternalPlannerTimeline.ExternalStateKind.CastStart, [castA], 0), new(10, "B", ExternalPlannerTimeline.ExternalStateKind.CastStart, [castB], 0)],
                null, [bossOID],
                [new(ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable, 60, 80, 1f), new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 60, 80, 1f)]);
            TimelineStore.SetCandidatesForTesting(wiredZone, [new(wiredZone, "wired-empty.json", true, [sequence], ExternalPlannerTimeline.TimelineSource.Replay, 0.9f)]);
            var ws = new WorldState(TimeSpan.TicksPerSecond, "synthetic") { CurrentZone = wiredZone, CurrentCFCID = 0 };
            using var hints = new ExternalTimelineHints(ws);
            var frame = 0u;
            void Tick(float seconds)
            {
                ws.Execute(new WorldState.OpFrameStart(new FrameState(t0.AddSeconds(seconds), 0, ++frame, 1, 1, 1), default, default, default));
                hints.Update(null);
            }
            void AdvanceTo(float from, float to)
            {
                for (var s = from + 1; s <= to; ++s)
                    Tick(s);
            }
            bool Snapshot(out ExternalMechanicHintSnapshot snapshot) => ExternalMechanicHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, ws.CurrentTime, MechanicHintSources.Timeline, out snapshot);
            bool Empty() => Snapshot(out var snapshot) && snapshot.TargetLossIn == float.MaxValue && snapshot.TargetReturnIn == float.MaxValue;
            bool Loss() => Snapshot(out var snapshot) && snapshot.TargetLossIn < float.MaxValue;

            Tick(0);
            ws.Execute(new ActorState.OpCreate(bossID, bossOID, 0, 0, "Boss", 0, ActorType.Enemy, Class.None, 100, default, 5, new(1000, 1000, 0, 0, 0), true, false, 0, 0, 0));
            ws.Execute(new ActorState.OpCombat(bossID, true));
            Tick(1);
            Require(!hints.Synced && !Snapshot(out _), "an unconfirmed clock published a snapshot");

            AdvanceTo(1, 5);
            ws.Execute(new ActorState.OpCastInfo(bossID, new() { Action = new(ActionType.Spell, castA), TotalTime = 3 }));
            Tick(6);
            Require(hints.Synced, "cast A did not confirm the clock");
            Require(Empty(), "a confirmed clock with the next loss 54 s away did not publish an empty snapshot");
            ws.Execute(new ActorState.OpCastInfo(bossID, null));

            AdvanceTo(6, 36);
            Require(Loss(), "the loss 24 s away was not published");

            // the window runs to the end of what the sequence knows: from there on the clock cannot vouch for the horizon
            AdvanceTo(36, 59);
            ws.Execute(new ActorState.OpTargetable(bossID, false));
            AdvanceTo(59, 79);
            ws.Execute(new ActorState.OpTargetable(bossID, true));
            AdvanceTo(79, 81);
            Require(!Snapshot(out _), "a clock past what its sequence knows published a snapshot");
        }
        finally
        {
            TimelineStore.SetCandidatesForTesting(wiredZone, null);
            config.UseExternalTimelineHints = savedHints;
        }
    });
    Check("wired follower: exhaustion sticks through a contradiction drop until a real pull start", () =>
    {
        // The real ExternalTimelineHints against a synthetic WorldState: a two-cast sequence (A at 5 s, B at 10 s) with a NoTarget
        // window 20-40 s, so the sequence tail is 40 s and the clock is exhausted past 46 s.
        const ushort wiredZone = 60010;
        const uint bossOID = 0x6000;
        const ulong bossID = 0x1000;
        const uint castA = 0x5001;
        const uint castB = 0x5002;
        var config = Service.Config.Get<BossModuleConfig>();
        var savedHints = config.UseExternalTimelineHints;
        config.UseExternalTimelineHints = true;
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            var sequence = new ExternalPlannerTimeline.TimelineSequence(0, 0,
                [new(5, "A", ExternalPlannerTimeline.ExternalStateKind.CastStart, [castA], 0), new(10, "B", ExternalPlannerTimeline.ExternalStateKind.CastStart, [castB], 0)],
                null, [bossOID],
                [new(ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable, 20, 40, 1f), new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 20, 40, 1f)]);
            TimelineStore.SetCandidatesForTesting(wiredZone, [new(wiredZone, "wired.json", true, [sequence], ExternalPlannerTimeline.TimelineSource.Replay, 0.9f)]);
            var ws = new WorldState(TimeSpan.TicksPerSecond, "synthetic") { CurrentZone = wiredZone, CurrentCFCID = 0 };
            using var hints = new ExternalTimelineHints(ws);
            var frame = 0u;
            // One frame per second, with the follower updated after each like the plugin's per-frame Update.
            void Tick(float seconds)
            {
                ws.Execute(new WorldState.OpFrameStart(new FrameState(t0.AddSeconds(seconds), 0, ++frame, 1, 1, 1), default, default, default));
                hints.Update(null);
            }
            void AdvanceTo(float from, float to)
            {
                for (var s = from + 1; s <= to; ++s)
                    Tick(s);
            }
            bool Published() => ExternalMechanicHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, ws.CurrentTime, MechanicHintSources.Timeline, out var snapshot) && snapshot.TargetLossIn < float.MaxValue;
            void Cast(uint action) => ws.Execute(new ActorState.OpCastInfo(bossID, new() { Action = new(ActionType.Spell, action), TotalTime = 3 }));
            void CastEnd() => ws.Execute(new ActorState.OpCastInfo(bossID, null));
            void Combat(bool value) => ws.Execute(new ActorState.OpCombat(bossID, value));
            void Targetable(bool value) => ws.Execute(new ActorState.OpTargetable(bossID, value));

            // Pull start: the boss spawns targetable and enters combat with nothing else fighting.
            Tick(0);
            ws.Execute(new ActorState.OpCreate(bossID, bossOID, 0, 0, "Boss", 0, ActorType.Enemy, Class.None, 100, default, 5, new(1000, 1000, 0, 0, 0), true, false, 0, 0, 0));
            Combat(true);
            Tick(1);
            Require(Math.Abs(hints.TimelineTime - 1) < 0.01f, $"pull start did not place the clock: {hints.TimelineTime}");
            Require(!hints.Synced && !Published(), "an unconfirmed clock published");

            // Casts A (t=5) and B (t=10) confirm the clock; the 20 s window at 20-40 is then inside the horizon and published.
            AdvanceTo(1, 5);
            Cast(castA);
            Tick(6);
            Require(hints.Synced && Math.Abs(hints.TimelineTime - 6) < 0.01f, $"cast A did not confirm the clock: {hints.TimelineTime}");
            Require(Published(), "confirmed clock with a window ahead published nothing");
            AdvanceTo(6, 8);
            CastEnd();
            AdvanceTo(8, 10);
            Cast(castB);
            Tick(11);
            Require(Math.Abs(hints.TimelineTime - 11) < 0.01f, $"cast B moved the clock: {hints.TimelineTime}");
            CastEnd();

            // The followed boss dropping and re-entering combat within the same second while synced is a flicker: the clock stays.
            Tick(12);
            Combat(false);
            Combat(true);
            Tick(13);
            Require(hints.Synced && Math.Abs(hints.TimelineTime - 13) < 0.01f, $"boss re-entering combat restarted the clock: {hints.TimelineTime}");
            Require(Published(), "continuation dropped the published hint");

            // The same boss back in combat 30 s after leaving (a wipe and a re-pull, the old clock still synced) is a pull start: the
            // clock restarts at the sequence start, unconfirmed, and needs the casts again. Everything after this is shifted by `o`.
            Tick(14);
            Combat(false);
            AdvanceTo(14, 44);
            Combat(true);
            Tick(45);
            const float o = 44;
            Require(!hints.Synced && Math.Abs(hints.TimelineTime - 1) < 0.01f, $"re-pull after a 30 s gap did not restart the clock: {hints.TimelineTime}");
            Require(!Published(), "the restarted clock kept the old hint");
            AdvanceTo(45, o + 5);
            Cast(castA);
            Tick(o + 6);
            Require(hints.Synced && Math.Abs(hints.TimelineTime - 6) < 0.01f, $"re-pull did not re-align on cast A: {hints.TimelineTime}");
            AdvanceTo(o + 6, o + 8);
            CastEnd();
            AdvanceTo(o + 8, o + 10);
            Cast(castB);
            Tick(o + 11);
            Require(Math.Abs(hints.TimelineTime - 11) < 0.01f, $"re-pull did not follow cast B: {hints.TimelineTime}");
            Require(Published(), "re-pull published nothing");
            CastEnd();

            // The vanish happens where the timeline says: the edges snap the clock, the world check agrees throughout.
            AdvanceTo(o + 11, o + 20);
            Targetable(false);
            AdvanceTo(o + 20, o + 40);
            Targetable(true);
            AdvanceTo(o + 40, o + 60);
            Require(hints.Synced && Math.Abs(hints.TimelineTime - 60) < 0.01f, $"clock after the window: {hints.TimelineTime}");
            Require(!Published(), "an exhausted clock published");

            // The fight contradicts the exhausted clock (boss gone while the timeline predicts a target) for longer than the grace: the
            // sequence is dropped, but its exhaustion is remembered.
            Targetable(false);
            AdvanceTo(o + 60, o + 66);
            Require(float.IsNaN(hints.TimelineTime), $"contradiction did not drop the sequence: {hints.TimelineTime}");
            Targetable(true);
            AdvanceTo(o + 66, o + 70);
            Cast(castA);
            AdvanceTo(o + 70, o + 73);
            CastEnd();
            AdvanceTo(o + 73, o + 75);
            Cast(castB);
            AdvanceTo(o + 75, o + 80);
            Require(float.IsNaN(hints.TimelineTime) || hints.TimelineTime >= 60, $"replayed casts re-aligned into the exhausted sequence: {hints.TimelineTime}");
            Require(!Published(), "re-alignment into the exhausted sequence published a hint");
            CastEnd();

            // A real pull start (everything leaves combat, one enemy enters) forgets the exhaustion and alignment works again.
            AdvanceTo(o + 80, o + 85);
            Combat(false);
            Tick(o + 86);
            Combat(true);
            AdvanceTo(o + 86, o + 91);
            Cast(castA);
            Tick(o + 92);
            Require(hints.Synced && Math.Abs(hints.TimelineTime - 6) < 0.01f, $"new pull did not re-align on cast A: {hints.TimelineTime}");
            AdvanceTo(o + 92, o + 94);
            CastEnd();
            AdvanceTo(o + 94, o + 96);
            Cast(castB);
            Tick(o + 97);
            Require(Math.Abs(hints.TimelineTime - 11) < 0.01f, $"new pull did not follow cast B: {hints.TimelineTime}");
            Require(Published(), "new pull published nothing");
        }
        finally
        {
            ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
            TimelineStore.SetCandidatesForTesting(wiredZone, null);
            config.UseExternalTimelineHints = savedHints;
        }
    });
    Check("wired follower: HP-gated branch siblings are chosen by the boss HP forecast, the divergence cast or the early vanish", () =>
    {
        // Two siblings of one boss: shared casts 0x1000 (5 s) and 0x1001 (10 s). The early sibling casts 0x3000 at 30 s (its own sync
        // point, the divergence) and goes away 36-60 s; the late one goes away 56-80 s. The branch is decided at 30 s on a 50% HP
        // threshold. The late sibling also kept a cast 0x1002 at 25 s that the early one did not (each sibling keeps what most of its
        // own pulls had), and both siblings show 0x3001: the early one at 30 s, the late one at 40 s, before the early window start
        // plus the sync tolerance (42 s), so it is shared and decides nothing. The early sibling also kept a cast 0x3002 at 32 s,
        // after the decision point but not at it, so it is not a divergence entry.
        const ushort wiredZone = 60011;
        // The same boss where the early sibling has no own sync point: the decision point is its window start (capped), which the
        // extractor's median put a hair after the window start the early sibling carries (35.98 s).
        const ushort cappedZone = 60012;
        // As the capped zone, with a window both siblings share before the decision point (15-24 s).
        const ushort cappedSharedZone = 60013;
        const uint bossOID = 0x6100;
        const ulong bossID = 0x1100;
        const uint addOID = 0x6101;
        const ulong addID = 0x1101;
        const uint shared1 = 0x1000;
        const uint shared2 = 0x1001;
        const uint lateOnly = 0x1002;
        const uint divergence = 0x3000;
        const uint sharedAtDecision = 0x3001;
        const uint earlyAfterDecision = 0x3002;
        var config = Service.Config.Get<BossModuleConfig>();
        var savedHints = config.UseExternalTimelineHints;
        config.UseExternalTimelineHints = true;
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            const ExternalPlannerTimeline.ExternalStateKind cast = ExternalPlannerTimeline.ExternalStateKind.CastStart;
            List<ExternalPlannerTimeline.TimelineState> Shared() => [new(5, "A", cast, [shared1], 0), new(10, "B", cast, [shared2], 0)];
            List<ExternalPlannerTimeline.TimelineWindow> Gone(float start, float end) => [
                new(ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable, start, end, 1f),
                new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, start, end, 1f)];
            var early = new ExternalPlannerTimeline.TimelineSequence(0, 0, [.. Shared(), new(30, "Shared", cast, [sharedAtDecision], 0), new(30, "Transition", cast, [divergence], 0),
                new(32, "Early after", cast, [earlyAfterDecision], 0)], null, [bossOID], Gone(36, 60), new(0, 30, 50, true));
            var late = new ExternalPlannerTimeline.TimelineSequence(1, 0, [.. Shared(), new(25, "Late only", cast, [lateOnly], 0), new(40, "Shared", cast, [sharedAtDecision], 0)],
                null, [bossOID], Gone(56, 80), new(0, 30, 50, false));
            TimelineStore.SetCandidatesForTesting(wiredZone, [new(wiredZone, "branch.json", true, [early, late], ExternalPlannerTimeline.TimelineSource.Replay, 0.9f)]);
            var cappedEarly = new ExternalPlannerTimeline.TimelineSequence(0, 0, Shared(), null, [bossOID], Gone(35.98f, 60), new(0, 36, null, true));
            var cappedLate = new ExternalPlannerTimeline.TimelineSequence(1, 0, Shared(), null, [bossOID], Gone(56, 80), new(0, 36, null, false));
            TimelineStore.SetCandidatesForTesting(cappedZone, [new(cappedZone, "capped.json", true, [cappedEarly, cappedLate], ExternalPlannerTimeline.TimelineSource.Replay, 0.9f)]);
            var sharedEarly = cappedEarly with { Windows = [.. Gone(15, 24), .. Gone(35.98f, 60)] };
            var sharedLate = cappedLate with { Windows = [.. Gone(15, 24), .. Gone(56, 80)] };
            TimelineStore.SetCandidatesForTesting(cappedSharedZone, [new(cappedSharedZone, "capped-shared.json", true, [sharedEarly, sharedLate], ExternalPlannerTimeline.TimelineSource.Replay, 0.9f)]);

            float Published(WorldState ws) => ExternalMechanicHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, ws.CurrentTime, MechanicHintSources.Timeline, out var snapshot) ? snapshot.TargetLossIn : float.MaxValue;
            // One pull starting `start` seconds into the world: the follower runs in the zone first (as it does every frame in the
            // plugin), then the boss enters combat with nothing else fighting. Each second, the boss HP% comes from hpAt and the casts
            // listed (the shared ones at 5 and 10 s unless left out) start and last 3 s; the boss is untargetable from gone.From until
            // gone.To, and at `bossDiesAt` an unlisted add at 30% with ten times the boss max HP enters the fight and the boss dies.
            // Actions of a second run before the follower's update of that second, like a frame of the plugin. The pull ends with
            // everything leaving combat. Returns the published TargetLossIn for every second of the pull (float.MaxValue when nothing
            // is published).
            float[] RunPull(WorldState ws, ExternalTimelineHints hints, int start, Func<int, float> hpAt, int until, (int At, uint Action)[]? casts = null,
                bool sharedCasts = true, (int From, int To)? gone = null, int? bossDiesAt = null)
            {
                Dictionary<int, uint> castAt = sharedCasts ? new() { [5] = shared1, [10] = shared2 } : [];
                foreach (var (at, action) in casts ?? [])
                    castAt[at] = action;
                void Frame(int s) => ws.Execute(new WorldState.OpFrameStart(new FrameState(t0.AddSeconds(start + s), 0, (uint)(start + s + 1), 1, 1, 1), default, default, default));
                ActorHPMP HP(int s) => new((uint)MathF.Round(100_000 * hpAt(s) / 100), 100_000, 0, 0, 0);

                Frame(0);
                if (ws.Actors.Find(bossID) == null)
                    ws.Execute(new ActorState.OpCreate(bossID, bossOID, 0, 0, "Boss", 0, ActorType.Enemy, Class.None, 100, default, 5, HP(0), true, false, 0, 0, 0));
                else
                    ws.Execute(new ActorState.OpHPMP(bossID, HP(0)));
                hints.Update(null);
                ws.Execute(new ActorState.OpCombat(bossID, true));
                hints.Update(null);
                var published = new float[until + 1];
                published[0] = Published(ws);
                var bossAlive = true;
                for (var s = 1; s <= until; ++s)
                {
                    Frame(s);
                    if (s == bossDiesAt)
                    {
                        ws.Execute(new ActorState.OpCreate(addID, addOID, 1, 0, "Add", 0, ActorType.Enemy, Class.None, 100, default, 5, new(300_000, 1_000_000, 0, 0, 0), true, false, 0, 0, 0));
                        ws.Execute(new ActorState.OpCombat(addID, true));
                        ws.Execute(new ActorState.OpDead(bossID, true));
                        bossAlive = false;
                    }
                    else if (bossAlive)
                    {
                        ws.Execute(new ActorState.OpHPMP(bossID, HP(s)));
                    }
                    if (castAt.ContainsKey(s - 3))
                        ws.Execute(new ActorState.OpCastInfo(bossID, null));
                    if (castAt.TryGetValue(s, out var action))
                        ws.Execute(new ActorState.OpCastInfo(bossID, new() { Action = new(ActionType.Spell, action), TotalTime = 3 }));
                    if (s == gone?.From)
                        ws.Execute(new ActorState.OpTargetable(bossID, false));
                    else if (s == gone?.To)
                        ws.Execute(new ActorState.OpTargetable(bossID, true));
                    hints.Update(null);
                    published[s] = Published(ws);
                }
                if (ws.Actors.Find(bossID)?.CastInfo != null)
                    ws.Execute(new ActorState.OpCastInfo(bossID, null));
                if (ws.Actors.Find(bossID)?.IsTargetable == false)
                    ws.Execute(new ActorState.OpTargetable(bossID, true));
                ws.Execute(new ActorState.OpCombat(bossID, false));
                if (ws.Actors.Find(addID) != null)
                    ws.Execute(new ActorState.OpDestroy(addID));
                return published;
            }
            float[] FreshPull(ushort zone, Func<int, float> hpAt, int until, (int At, uint Action)[]? casts = null, bool sharedCasts = true, (int From, int To)? gone = null, int? bossDiesAt = null)
            {
                ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
                var ws = new WorldState(TimeSpan.TicksPerSecond, "synthetic") { CurrentZone = zone, CurrentCFCID = 0 };
                using var hints = new ExternalTimelineHints(ws);
                var published = RunPull(ws, hints, 0, hpAt, until, casts, sharedCasts, gone, bossDiesAt);
                ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
                return published;
            }
            // Every second in [from, to] publishes a loss max(0, windowStart - now) seconds ahead; every second from quietFrom up to
            // `from` publishes nothing.
            void RequireLoss(string label, float[] published, int from, int to, float windowStart, int quietFrom = 1)
            {
                for (var s = quietFrom; s < from; ++s)
                    Require(published[s] == float.MaxValue, $"{label}: published a loss in {published[s]} at {s} s, before {from} s");
                for (var s = from; s <= to; ++s)
                    Require(Math.Abs(published[s] - Math.Max(0, windowStart - s)) < 0.05f, $"{label}: at {s} s the loss is in {published[s]}, expected {Math.Max(0, windowStart - s)} (the window at {windowStart} s)");
            }

            // A: HP falls 2 pt/s, so 8 s before the decision the forecast is 56 - 16 = 40 (< 47): the early window is published from 22 s,
            // 8 s before the divergence cast would come, and the late window never is.
            RequireLoss("HP forecast below", FreshPull(wiredZone, s => 100 - 2 * s, 35), 22, 35, 36);
            // B: HP stays at 90%: the forecast decides the late sibling, whose window is published once it is inside the horizon.
            RequireLoss("HP forecast above", FreshPull(wiredZone, _ => 90, 40), 31, 40, 56);
            // B': HP within the margin and no divergence cast: undecided until the cast grace passes, then the late window.
            RequireLoss("cast grace passed", FreshPull(wiredZone, _ => 50, 40), 32, 40, 56);
            // C: HP within the margin, the divergence cast at 30 s decides the early sibling on the spot: 6 s of warning.
            RequireLoss("divergence cast", FreshPull(wiredZone, _ => 50, 35, [(30, divergence)]), 30, 35, 36);
            // C': 0x3001 sits at the decision point too, but the late sibling shows it at 40 s: it decides nothing, the grace does.
            RequireLoss("shared cast at the decision point", FreshPull(wiredZone, _ => 50, 40, [(30, sharedAtDecision)]), 32, 40, 56);
            // D: as A, then the cast only the late sibling kept (25 s, before the decision point) aligns the clock onto the late sibling.
            // Before the decision point that is not the branch showing: the early decision stands and its window stays published.
            RequireLoss("late-only entry before the decision point", FreshPull(wiredZone, s => 100 - 2 * s, 35, [(25, lateOnly)]), 22, 35, 36);
            // E: HP high decides the late sibling at 22 s, but the early sibling's own cast comes at 30 s: alignment moves the clock onto
            // the early sibling at the decision point, which overrides the forecast.
            RequireLoss("divergence cast after a late forecast", FreshPull(wiredZone, _ => 90, 35, [(30, divergence)]), 30, 35, 36);
            // E': HP high decides the late sibling at 22 s; at 32 s the early sibling's 0x3002 (after the decision point, not a
            // divergence entry) aligns the clock onto it. That does not overturn the decision: the late window stays published.
            RequireLoss("non-divergence early entry after a late forecast", FreshPull(wiredZone, _ => 90, 40, [(32, earlyAfterDecision)]), 31, 40, 56);
            // E'': as E', but decided early first (HP falling): the same alignment onto the early sibling changes nothing either.
            RequireLoss("non-divergence early entry after an early forecast", FreshPull(wiredZone, s => 100 - 2 * s, 35, [(32, earlyAfterDecision)]), 22, 35, 36);
            // F: no shared cast confirms the clock, so the divergence cast arrives on an unconfirmed clock; the alignment it confirms sits
            // exactly on the divergence entry, which decides the early sibling.
            RequireLoss("divergence cast confirming the clock", FreshPull(wiredZone, _ => 50, 35, [(30, divergence)], sharedCasts: false), 30, 35, 36);
            // G: the boss dies at 18 s and a bigger unlisted add at 30% carries on: the forecast only reads a listed boss, so nothing is
            // forecast (the add would forecast early) and the grace decides the late sibling.
            RequireLoss("listed boss gone before the decision point", FreshPull(wiredZone, s => 100 - 2 * s, 40, bossDiesAt: 18), 32, 40, 56);

            // H: a second pull in the same follower does not inherit the first pull's decision. The first pull decides early (as A) and
            // ends at 35 s; the boss re-engages 15 s later with HP high, which decides late.
            {
                ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
                var ws = new WorldState(TimeSpan.TicksPerSecond, "synthetic") { CurrentZone = wiredZone, CurrentCFCID = 0 };
                using var hints = new ExternalTimelineHints(ws);
                RequireLoss("first pull", RunPull(ws, hints, 0, s => 100 - 2 * s, 35), 22, 35, 36);
                RequireLoss("second pull", RunPull(ws, hints, 50, _ => 90, 40), 31, 40, 56);
                ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
            }

            // I: capped decision point (no divergence entry). Undecided, nothing starting from the decision point is published, including
            // the early window that starts 0.02 s before it. The boss vanishing where the early sibling goes away decides early.
            RequireLoss("capped: early vanish", FreshPull(cappedZone, _ => 50, 40, gone: (36, 99)), 36, 40, 35.98f);
            // J: capped, the boss stays: the grace decides the late sibling.
            RequireLoss("capped: no vanish", FreshPull(cappedZone, _ => 50, 40), 38, 40, 56);
            // K: capped, the boss goes away 15-24 s where both siblings do: that shared window is published as usual, but it is not the
            // early sibling's vanish. After it nothing is published until the grace decides the late sibling.
            RequireLoss("capped: shared window before the decision point", FreshPull(cappedSharedZone, _ => 50, 40, gone: (15, 24)), 38, 40, 56, quietFrom: 25);
        }
        finally
        {
            ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
            TimelineStore.SetCandidatesForTesting(wiredZone, null);
            TimelineStore.SetCandidatesForTesting(cappedZone, null);
            TimelineStore.SetCandidatesForTesting(cappedSharedZone, null);
            config.UseExternalTimelineHints = savedHints;
        }
    });

    // Attackability edges (the moment nothing is left to attack, or something is again) matched against NoTarget windows.
    ExternalPlannerTimeline.TimelineWindow EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind kind, float start, float? end, float confidence = 1f) => new(kind, start, end, confidence);
    ExternalPlannerTimeline.TimelineSequence EdgeSequence(uint[]? bosses, params ExternalPlannerTimeline.TimelineWindow[] windows) => new(0, 0, [], null, bosses == null ? null : [.. bosses], [.. windows]);
    Check("attackability edge: the nearest NoTarget window edge within tolerance, start or end by direction", () =>
    {
        var seq = EdgeSequence(null, EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 30, 45), EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 50, 70));
        Require(ExternalTimelineHints.MatchAttackabilityEdge(seq, 34, false, 6, 0.5f, float.MaxValue) is { } late && Math.Abs(late - 30) < 0.01f, "a late loss did not match the window start");
        Require(ExternalTimelineHints.MatchAttackabilityEdge(seq, 27, false, 6, 0.5f, float.MaxValue) is { } early && Math.Abs(early - 30) < 0.01f, "an early loss did not match the window start");
        // 47 is 2 s past the first window's end but 3 s before the second's start: a loss matches starts only.
        Require(ExternalTimelineHints.MatchAttackabilityEdge(seq, 47, false, 6, 0.5f, float.MaxValue) is { } nearestStart && Math.Abs(nearestStart - 50) < 0.01f, "a loss matched something other than the nearest start");
        Require(ExternalTimelineHints.MatchAttackabilityEdge(seq, 47, true, 6, 0.5f, float.MaxValue) is { } nearestEnd && Math.Abs(nearestEnd - 45) < 0.01f, "a return did not match the nearest end");
        Require(ExternalTimelineHints.MatchAttackabilityEdge(seq, 40, false, 6, 0.5f, float.MaxValue) == null, "a loss 10 s from any start matched");
        Require(ExternalTimelineHints.MatchAttackabilityEdge(seq, 60, true, 6, 0.5f, float.MaxValue) == null, "a return 10 s from any end matched");
        Require(ExternalTimelineHints.MatchAttackabilityEdge(EdgeSequence(null, EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 30, null)), 31, true, 6, 0.5f, float.MaxValue) == null, "an open window matched a return");
    });
    Check("attackability edge: only confident NoTarget windows, whatever the sequence names", () =>
    {
        var low = EdgeSequence(null, EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 30, 45, 0.3f));
        Require(ExternalTimelineHints.MatchAttackabilityEdge(low, 31, false, 6, 0.5f, float.MaxValue) == null, "a low-confidence window matched");
        // A sequence naming its bosses still matches NoTarget windows (the whole fight emptied), never BossUntargetable ones, even a
        // nearer one.
        var named = EdgeSequence([0x100], EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable, 26, 45), EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 30, 45));
        Require(ExternalTimelineHints.MatchAttackabilityEdge(named, 27, false, 6, 0.5f, float.MaxValue) is { } start && Math.Abs(start - 30) < 0.01f, $"a named sequence did not use its NoTarget window: {ExternalTimelineHints.MatchAttackabilityEdge(named, 27, false, 6, 0.5f, float.MaxValue)}");
        var bossOnly = EdgeSequence([0x100], EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable, 20, 45));
        Require(ExternalTimelineHints.MatchAttackabilityEdge(bossOnly, 21, false, 6, 0.5f, float.MaxValue) == null, "a BossUntargetable window matched");
        var legacy = new ExternalPlannerTimeline.TimelineSequence(0, 0, [new(30, "", ExternalPlannerTimeline.ExternalStateKind.Untargetable, [], 0)], null);
        Require(ExternalTimelineHints.MatchAttackabilityEdge(legacy, 31, false, 6, 0.5f, float.MaxValue) == null, "a legacy marker matched");
    });
    Check("attackability edge: windows starting at or after the undecided-branch limit are not candidates", () =>
    {
        var seq = EdgeSequence(null, EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 10, 20), EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 30, 45));
        Require(ExternalTimelineHints.MatchAttackabilityEdge(seq, 31, false, 6, 0.5f, 30) == null, "the start of a window at the limit matched");
        Require(ExternalTimelineHints.MatchAttackabilityEdge(seq, 44, true, 6, 0.5f, 29.95f) == null, "the end of a window past the limit matched");
        Require(ExternalTimelineHints.MatchAttackabilityEdge(seq, 22, true, 6, 0.5f, 30) is { } end && Math.Abs(end - 20) < 0.01f, "a window before the limit stopped matching");
        // 26 is 4 s before the excluded start and 6 s after the allowed end: a loss matches nothing.
        Require(ExternalTimelineHints.MatchAttackabilityEdge(seq, 26, false, 6, 0.5f, 30) == null, "a loss matched past the limit");
    });
    Check("attackability edge: a loss never matches a window already passed, a return never one not yet entered", () =>
    {
        // A short window 50-53 the clock has passed and a long one at 62: a loss at 55 is 5 s from the passed start, 7 s from the next.
        var passed = EdgeSequence(null, EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 50, 53), EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 62, 80));
        Require(ExternalTimelineHints.MatchAttackabilityEdge(passed, 55, false, 6, 0.5f, float.MaxValue) == null, $"a loss matched a passed window: {ExternalTimelineHints.MatchAttackabilityEdge(passed, 55, false, 6, 0.5f, float.MaxValue)}");
        Require(ExternalTimelineHints.MatchAttackabilityEdge(passed, 52, false, 6, 0.5f, float.MaxValue) is { } inside && Math.Abs(inside - 50) < 0.01f, "a loss inside a window stopped matching its start");
        // A window 55-58 not entered yet and one 40-48.5 behind: a return at 54 is 4 s from the unentered end, 5.5 s from the other.
        var ahead = EdgeSequence(null, EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 40, 48.5f), EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 55, 58));
        Require(ExternalTimelineHints.MatchAttackabilityEdge(ahead, 54, true, 6, 0.5f, float.MaxValue) is { } end && Math.Abs(end - 48.5f) < 0.01f, $"a return matched a window not entered: {ExternalTimelineHints.MatchAttackabilityEdge(ahead, 54, true, 6, 0.5f, float.MaxValue)}");
        Require(ExternalTimelineHints.MatchAttackabilityEdge(ahead, 56, true, 6, 0.5f, float.MaxValue) is { } early && Math.Abs(early - 58) < 0.01f, "an early return inside a window stopped matching its end");
    });
    Check("attackability edge: only the same sequence or its branch sibling keeps the time base", () =>
    {
        var early = new ExternalPlannerTimeline.TimelineSequence(0, 0, [], null, null, null, new(3, 30, 50, true));
        var late = new ExternalPlannerTimeline.TimelineSequence(1, 0, [], null, null, null, new(3, 30, 50, false));
        var other = new ExternalPlannerTimeline.TimelineSequence(2, 0, [], null);
        var otherGroup = new ExternalPlannerTimeline.TimelineSequence(3, 0, [], null, null, null, new(4, 30, 50, false));
        var timeline = new ExternalPlannerTimeline.TimelineDefinition(1, "t.json", true, [early, late, other, otherGroup], ExternalPlannerTimeline.TimelineSource.Replay, 1f);
        Require(ExternalTimelineHints.SharesTimeBase(timeline, early, early), "the same sequence");
        Require(ExternalTimelineHints.SharesTimeBase(timeline, early, late) && ExternalTimelineHints.SharesTimeBase(timeline, late, early), "the siblings of a branch");
        Require(!ExternalTimelineHints.SharesTimeBase(timeline, early, other), "another sequence");
        Require(!ExternalTimelineHints.SharesTimeBase(timeline, late, otherGroup), "another branch group");
        Require(!ExternalTimelineHints.SharesTimeBase(timeline, null, early), "no sequence before");
        var elsewhere = new ExternalPlannerTimeline.TimelineSequence(0, 0, [], null, null, null, new(3, 30, 50, true));
        Require(!ExternalTimelineHints.SharesTimeBase(timeline, elsewhere, late), "the same group number in another timeline");
    });
    Check("late window edges: inside a window that started within tolerance, or after one that ended within it", () =>
    {
        var seq = EdgeSequence(null, EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 30, 45));
        Require(ExternalTimelineHints.IsLateWindowEdge(seq, 34, true, 6, 0.5f), "a start 4 s late was a contradiction");
        Require(ExternalTimelineHints.IsLateWindowEdge(seq, 36, true, 6, 0.5f), "a start exactly the tolerance late was a contradiction");
        Require(!ExternalTimelineHints.IsLateWindowEdge(seq, 36.5f, true, 6, 0.5f), "a start past the tolerance was excused");
        Require(ExternalTimelineHints.IsLateWindowEdge(seq, 49, false, 6, 0.5f), "an end 4 s late was a contradiction");
        Require(!ExternalTimelineHints.IsLateWindowEdge(seq, 51.5f, false, 6, 0.5f), "an end past the tolerance was excused");
        Require(!ExternalTimelineHints.IsLateWindowEdge(seq, 25, false, 6, 0.5f), "nothing attackable before any window was excused");
        Require(!ExternalTimelineHints.IsLateWindowEdge(EdgeSequence(null, EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 30, 45, 0.3f)), 33, true, 6, 0.5f), "a low-confidence window excused a contradiction");
        Require(!ExternalTimelineHints.IsLateWindowEdge(EdgeSequence(null, EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.AddsPresent, 30, 45)), 33, true, 6, 0.5f), "an adds window excused a contradiction");
        var legacy = new ExternalPlannerTimeline.TimelineSequence(0, 0, [new(30, "", ExternalPlannerTimeline.ExternalStateKind.Untargetable, [], 0), new(45, "", ExternalPlannerTimeline.ExternalStateKind.Targetable, [], 0)], null);
        Require(!ExternalTimelineHints.IsLateWindowEdge(legacy, 33, true, 6, 0.5f), "legacy markers excused a contradiction");
    });
    Check("started downtime: the length filter reads the full window, measured or legacy", () =>
    {
        var measured = EdgeSequence(null, EdgeWindow(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 30, 39));
        var (loss, ret) = ExternalTimelineHints.SelectDowntime(measured, 31, 25, 8.5f, 0.5f);
        Require(loss == 0 && Math.Abs(ret - 8) < 0.01f, $"a 9 s window 1 s in: {loss}/{ret}");
        var legacy = new ExternalPlannerTimeline.TimelineSequence(0, 0, [new(30, "", ExternalPlannerTimeline.ExternalStateKind.Untargetable, [], 0), new(39, "", ExternalPlannerTimeline.ExternalStateKind.Targetable, [], 0)], null);
        (loss, ret) = ExternalTimelineHints.SelectDowntime(legacy, 31, 25, 8.5f, 0.5f);
        Require(loss == 0 && Math.Abs(ret - 8) < 0.01f, $"a 9 s legacy span 1 s in: {loss}/{ret}");
        var legacyShort = new ExternalPlannerTimeline.TimelineSequence(0, 0, [new(30, "", ExternalPlannerTimeline.ExternalStateKind.Untargetable, [], 0), new(35, "", ExternalPlannerTimeline.ExternalStateKind.Targetable, [], 0)], null);
        Require(ExternalTimelineHints.SelectDowntime(legacyShort, 25, 25, 8.5f, 0.5f).LossIn == float.MaxValue, "a 5 s legacy span was selected");
    });
    Check("wired follower: a started downtime stays published until it ends", () =>
    {
        // A boss (casts 0x5301 at 5 s and 0x5302 at 10 s confirm the clock) goes away 20-25 s (too short to publish), 30-39 s (just
        // long enough) and 45-75 s, exactly where the timeline says. One frame per second, world changes before the update.
        const ushort zone = 60015;
        const uint bossOID = 0x6300;
        const ulong bossID = 0x1300;
        var config = Service.Config.Get<BossModuleConfig>();
        var savedHints = config.UseExternalTimelineHints;
        config.UseExternalTimelineHints = true;
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            var sequence = new ExternalPlannerTimeline.TimelineSequence(0, 0,
                [new(5, "A", ExternalPlannerTimeline.ExternalStateKind.CastStart, [0x5301], 0), new(10, "B", ExternalPlannerTimeline.ExternalStateKind.CastStart, [0x5302], 0)],
                null, [bossOID], [new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 20, 25, 1f), new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 30, 39, 1f),
                    new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 45, 75, 1f)]);
            TimelineStore.SetCandidatesForTesting(zone, [new(zone, "started.json", true, [sequence], ExternalPlannerTimeline.TimelineSource.Replay, 0.9f)]);
            ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
            var ws = new WorldState(TimeSpan.TicksPerSecond, "synthetic") { CurrentZone = zone, CurrentCFCID = 0 };
            using var hints = new ExternalTimelineHints(ws);
            var published = new (float Loss, float Return)[75];
            for (var s = 0; s < 75; ++s)
            {
                ws.Execute(new WorldState.OpFrameStart(new FrameState(t0.AddSeconds(s), 0, (uint)(s + 1), 1, 1, 1), default, default, default));
                if (s == 0)
                {
                    hints.Update(null);
                    ws.Execute(new ActorState.OpCreate(bossID, bossOID, 0, 0, "Boss", 0, ActorType.Enemy, Class.None, 100, default, 5, new(1000, 1000, 0, 0, 0), true, false, 0, 0, 0));
                    ws.Execute(new ActorState.OpCombat(bossID, true));
                }
                if (s is 5 or 10)
                    ws.Execute(new ActorState.OpCastInfo(bossID, new() { Action = new(ActionType.Spell, s == 5 ? 0x5301u : 0x5302u), TotalTime = 3 }));
                else if (s is 8 or 13)
                    ws.Execute(new ActorState.OpCastInfo(bossID, null));
                if (s is 20 or 30 or 45)
                    ws.Execute(new ActorState.OpTargetable(bossID, false));
                else if (s is 25 or 39)
                    ws.Execute(new ActorState.OpTargetable(bossID, true));
                hints.Update(null);
                var has = ExternalMechanicHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, ws.CurrentTime, MechanicHintSources.Timeline, out var snapshot);
                published[s] = has ? (snapshot.TargetLossIn, snapshot.TargetReturnIn) : (float.MaxValue, float.MaxValue);
            }
            ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
            void RequirePublished(int s, float loss, float ret)
                => Require(Math.Abs(published[s].Loss - loss) < 0.01f && Math.Abs(published[s].Return - ret) < 0.01f, $"at {s} s published {published[s]}, expected ({loss}, {ret})");
            // The 5 s window is never published: while it runs, the 9 s one behind it is.
            for (var s = 20; s < 25; ++s)
                RequirePublished(s, 30 - s, 39 - s);
            // The 9 s window stays published as it runs, down to its last second.
            for (var s = 30; s < 39; ++s)
                RequirePublished(s, 0, 39 - s);
            // So does the 30 s one, through its last 3 s.
            for (var s = 45; s < 75; ++s)
                RequirePublished(s, 0, 75 - s);
        }
        finally
        {
            ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
            TimelineStore.SetCandidatesForTesting(zone, null);
            config.UseExternalTimelineHints = savedHints;
        }
    });
    Check("wired follower: attackability edges resync the clock at kill-triggered window edges", () =>
    {
        // A boss above the arena (listed, in combat, never targetable) casts 0x5101 at 5 s and 0x5102 at 10 s; a wave of adds is
        // the only thing to attack. Nothing is attackable 30-45 s on the timeline. The pull runs on half-second frames; the world
        // changes of a frame happen before the follower's update of that frame, as in the plugin.
        const ushort edgeZone = 60014;
        const uint bossOID = 0x6200;
        const ulong bossID = 0x1200;
        const uint waveOID = 0x6201;
        const ulong wave1ID = 0x1201;
        const ulong wave2ID = 0x1202;
        const uint boss2OID = 0x6202;
        const ulong boss2ID = 0x1203;
        var config = Service.Config.Get<BossModuleConfig>();
        var savedHints = config.UseExternalTimelineHints;
        config.UseExternalTimelineHints = true;
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        try
        {
            var sequence = new ExternalPlannerTimeline.TimelineSequence(0, 0,
                [new(5, "A", ExternalPlannerTimeline.ExternalStateKind.CastStart, [0x5101], 0), new(10, "B", ExternalPlannerTimeline.ExternalStateKind.CastStart, [0x5102], 0)],
                null, [bossOID], [new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 30, 45, 1f)]);
            // A second boss with its own sequence (a window 3-20 s), which only its entering combat places the clock on.
            var sequence2 = new ExternalPlannerTimeline.TimelineSequence(1, 0, [], null, [boss2OID], [new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 3, 20, 1f)]);
            TimelineStore.SetCandidatesForTesting(edgeZone, [new(edgeZone, "edge.json", true, [sequence, sequence2], ExternalPlannerTimeline.TimelineSource.Replay, 0.9f)]);

            // Per half-second frame of the pull: the clock and the published (loss, return) after the follower's update.
            // wave1Gone: when wave 1 dies (or turns untargetable for `blip` seconds and comes back); wave2At: when wave 2 enters.
            // The follower is disabled over [disabledFrom, disabledTo); the second boss enters combat, untargetable, at boss2At.
            (float Clock, float Loss, float Return)[] RunPull(float wave1Gone, float wave2At, float until, float blip = 0, bool casts = true, float disabledFrom = -1, float disabledTo = -1, float boss2At = -1)
            {
                ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
                var ws = new WorldState(TimeSpan.TicksPerSecond, "synthetic") { CurrentZone = edgeZone, CurrentCFCID = 0 };
                using var hints = new ExternalTimelineHints(ws);
                var frames = (int)(until * 2);
                var result = new (float, float, float)[frames + 1];
                for (var i = 0; i <= frames; ++i)
                {
                    var s = i / 2f;
                    ws.Execute(new WorldState.OpFrameStart(new FrameState(t0.AddSeconds(s), 0, (uint)(i + 1), 1, 1, 1), default, default, default));
                    config.UseExternalTimelineHints = !(s >= disabledFrom && s < disabledTo);
                    if (i == 0)
                    {
                        hints.Update(null); // the follower runs in the zone before the pull (it resolves the timeline there)
                        ws.Execute(new ActorState.OpCreate(bossID, bossOID, 0, 0, "Boss", 0, ActorType.Enemy, Class.None, 100, default, 5, new(1000, 1000, 0, 0, 0), false, false, 0, 0, 0));
                        ws.Execute(new ActorState.OpCreate(wave1ID, waveOID, 1, 0, "Wave", 0, ActorType.Enemy, Class.None, 100, default, 1, new(100, 100, 0, 0, 0), true, false, 0, 0, 0));
                        ws.Execute(new ActorState.OpCombat(bossID, true));
                        ws.Execute(new ActorState.OpCombat(wave1ID, true));
                    }
                    if (casts && s == 5)
                        ws.Execute(new ActorState.OpCastInfo(bossID, new() { Action = new(ActionType.Spell, 0x5101), TotalTime = 3 }));
                    else if (casts && s == 10)
                        ws.Execute(new ActorState.OpCastInfo(bossID, new() { Action = new(ActionType.Spell, 0x5102), TotalTime = 3 }));
                    else if (casts && s is 8 or 13)
                        ws.Execute(new ActorState.OpCastInfo(bossID, null));
                    if (s == wave1Gone)
                    {
                        if (blip > 0)
                            ws.Execute(new ActorState.OpTargetable(wave1ID, false));
                        else
                            ws.Execute(new ActorState.OpDead(wave1ID, true));
                    }
                    if (blip > 0 && s == wave1Gone + blip)
                        ws.Execute(new ActorState.OpTargetable(wave1ID, true));
                    if (s == boss2At)
                    {
                        ws.Execute(new ActorState.OpCreate(boss2ID, boss2OID, 3, 0, "Boss 2", 0, ActorType.Enemy, Class.None, 100, default, 5, new(1000, 1000, 0, 0, 0), false, false, 0, 0, 0));
                        ws.Execute(new ActorState.OpCombat(boss2ID, true));
                    }
                    if (s == wave2At)
                    {
                        ws.Execute(new ActorState.OpCreate(wave2ID, waveOID, 2, 0, "Wave", 0, ActorType.Enemy, Class.None, 100, default, 1, new(100, 100, 0, 0, 0), true, false, 0, 0, 0));
                        ws.Execute(new ActorState.OpCombat(wave2ID, true));
                    }
                    hints.Update(null);
                    var published = ExternalMechanicHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, ws.CurrentTime, MechanicHintSources.Timeline, out var snapshot);
                    result[i] = (hints.TimelineTime, published ? snapshot.TargetLossIn : float.MaxValue, published ? snapshot.TargetReturnIn : float.MaxValue);
                }
                ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
                return result;
            }
            (float Clock, float Loss, float Return) At((float, float, float)[] run, float s) => run[(int)(s * 2)];
            void RequireClock(string label, (float, float, float)[] run, float s, float expected)
                => Require(Math.Abs(At(run, s).Clock - expected) < 0.01f, $"{label}: clock at {s} s is {At(run, s).Clock}, expected {expected}");

            // On time: nothing changes, the window is published from the confirmation on and its return counts down.
            var onTime = RunPull(30, 45, 50);
            RequireClock("on time", onTime, 40, 40);
            Require(At(onTime, 31).Loss == 0 && Math.Abs(At(onTime, 31).Return - 14) < 0.01f, $"on time: published {At(onTime, 31)} at 31 s");

            // Late start (wave 1 dies 4 s late, at 34 s): not a contradiction while it is within the tolerance, and the window is not
            // published while the adds are still up. Once the loss has lasted 1 s, the clock is put back so that 34 s was the window
            // start; the end (wave 2 at 49 s, 15 s after the loss) then agrees and nothing is dropped.
            var lateStart = RunPull(34, 49, 51);
            for (var s = 30f; s < 34; s += 0.5f)
                Require(!float.IsNaN(At(lateStart, s).Clock) && At(lateStart, s).Loss == float.MaxValue, $"late start: at {s} s the clock is {At(lateStart, s).Clock} and the loss {At(lateStart, s).Loss}");
            RequireClock("late start", lateStart, 35, 31);
            Require(At(lateStart, 35).Loss == 0 && Math.Abs(At(lateStart, 35).Return - 14) < 0.01f, $"late start: published {At(lateStart, 35)} at 35 s");
            RequireClock("late start, after the window", lateStart, 51, 47);

            // Early start (wave 1 dies at 27 s): the clock is put forward to the window start and the return comes 15 s after the loss.
            var earlyStart = RunPull(27, 42, 44);
            RequireClock("early start", earlyStart, 28, 31);
            Require(At(earlyStart, 28).Loss == 0 && Math.Abs(At(earlyStart, 28).Return - 14) < 0.01f, $"early start: published {At(earlyStart, 28)} at 28 s");
            RequireClock("early start, after the window", earlyStart, 44, 47);

            // Early end (wave 2 enters at 42 s, 3 s before the window end): while the return settles, the world contradicts the
            // downtime the clock is in, and nothing is published; then the clock is put forward to the window end.
            var earlyEnd = RunPull(30, 42, 44);
            Require(At(earlyEnd, 41.5f).Loss == 0, $"early end: nothing published before the return ({At(earlyEnd, 41.5f)})");
            foreach (var s in (float[])[42, 42.5f])
                Require(At(earlyEnd, s).Loss == float.MaxValue, $"early end: published {At(earlyEnd, s)} at {s} s with the adds back");
            RequireClock("early end", earlyEnd, 43, 46);

            // Late end (wave 2 enters 4 s late, at 49 s): not a contradiction within the tolerance; the return resyncs the clock.
            var lateEnd = RunPull(30, 49, 51);
            RequireClock("late end", lateEnd, 48, 48);
            RequireClock("late end", lateEnd, 50, 46);

            // A loss shorter than a second (wave 1 untargetable 26-26.5 s, 4 s before the window) is discarded: the clock stays.
            var blip = RunPull(26, 45, 31, blip: 0.5f);
            RequireClock("blip", blip, 28, 28);

            // A start later than the tolerance (wave 1 only dies at 45 s) is a contradiction again: past 36 s the grace runs and the
            // clock is dropped once it has passed. Nothing is published inside the window with the adds up, before or after the
            // tolerance.
            var tooLate = RunPull(45, 60, 41);
            for (var s = 30f; s < 40; s += 0.5f)
                Require(At(tooLate, s).Loss == float.MaxValue, $"too late: published {At(tooLate, s)} at {s} s with the adds up");
            Require(!float.IsNaN(At(tooLate, 39.5f).Clock), $"too late: dropped already at 39.5 s");
            Require(float.IsNaN(At(tooLate, 40).Clock), $"too late: still following at 40 s ({At(tooLate, 40).Clock})");

            // Without the casts the clock is only placed by the combat start, never confirmed: a window start that does not come is
            // a contradiction right away (a pull start may have placed it long before the real pull), so the clock is dropped once
            // the 3 s grace has passed.
            // Inside the window with the adds up, it publishes nothing either.
            var unconfirmed = RunPull(45, 60, 41, casts: false);
            for (var s = 30f; s < 34; s += 0.5f)
                Require(At(unconfirmed, s).Loss == float.MaxValue, $"unconfirmed: published {At(unconfirmed, s)} at {s} s with the adds up");
            Require(!float.IsNaN(At(unconfirmed, 33).Clock), $"unconfirmed: dropped already at 33 s");
            Require(float.IsNaN(At(unconfirmed, 33.5f).Clock), $"unconfirmed: still following at 33.5 s ({At(unconfirmed, 33.5f).Clock})");

            // The follower is disabled 15-33 s and wave 1 dies at 25 s meanwhile: resuming does not settle that change as an edge
            // dated back to 25 s (which would put the clock 5 s forward, onto the window start); tracking starts over.
            var resumed = RunPull(25, 60, 34, disabledFrom: 15, disabledTo: 33);
            RequireClock("resumed", resumed, 33, 33);
            RequireClock("resumed", resumed, 34, 34);

            // Wave 1 dies at 30 s and, while that loss settles, a second boss enters combat and places the clock on its own sequence
            // at 30.5 s. The loss belongs to the first sequence and is not matched on the second (its window start at 3 s would pull
            // the clock 3.5 s forward).
            var otherSequence = RunPull(30, 60, 32, boss2At: 30.5f);
            RequireClock("another sequence", otherSequence, 31, 0.5f);
            RequireClock("another sequence", otherSequence, 32, 1.5f);
        }
        finally
        {
            ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);
            TimelineStore.SetCandidatesForTesting(edgeZone, null);
            config.UseExternalTimelineHints = savedHints;
        }
    });

    var scanCount = 0;
    var externalCount = 0;
    var openDowntimeTails = new List<string>();
    foreach (var candidate in BossModuleRegistry.RegisteredModules.Values.Where(candidate => candidate.PlanLevel > 0 && candidate.GroupType == BossModuleInfo.GroupType.CFC))
    {
        ++scanCount;
        Check("planner scan " + candidate.ModuleType.FullName, () =>
        {
            using var module = BossModuleRegistry.CreateModuleForConfigPlanning(candidate.ModuleType)!;
            var tree = new StateMachineTree(module.StateMachine);
            Require(tree.Nodes.Count > 1 || candidate.ModuleType.GetCustomAttribute<ModuleInfoAttribute>()?.PlanLevel > 0, "planner enabled with a trivial state machine");
            var duty = Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(candidate.GroupID)!.Value;
            var world = new WorldState(TimeSpan.TicksPerSecond, "synthetic") { CurrentZone = (ushort)duty.TerritoryType.RowId, CurrentCFCID = (ushort)candidate.GroupID };
            using var live = BossModuleRegistry.CreateModule(candidate, world, new(1, candidate.PrimaryActorOID, -1, 0, "Boss", 0, ActorType.Enemy, Class.None, candidate.PlanLevel, default))!;
            var liveTree = new StateMachineTree(live.StateMachine);
            Require(tree.Nodes.Select(p => (p.Key, p.Value.Time, p.Value.State.EndHint)).SequenceEqual(liveTree.Nodes.Select(p => (p.Key, p.Value.Time, p.Value.State.EndHint))), "offline/live state graph differs");
            _ = new PlanExecution(module, null);
            if (tree.Nodes.TryGetValue(0xFF000000, out var tail))
            {
                ++externalCount;
                if (tail.IsDowntime)
                    openDowntimeTails.Add(candidate.ModuleType.Name);
            }
        });
    }
    Console.WriteLine($"planner_scan={scanCount} external_fallbacks={externalCount}");
    Check("no unbounded imported downtime", () => Require(openDowntimeTails.Count == 0, "unbounded imported downtime: " + string.Join(", ", openDowntimeTails)));

    Check("summary cache round trip and freshness", () =>
    {
        var dir = Path.Combine(fixtureRoot, "cache1");
        Directory.CreateDirectory(dir);
        var replay = new FileInfo(Path.Combine(dir, "Some_Duty_2026.log"));
        File.WriteAllText(replay.FullName, "x");
        replay.Refresh();
        var start = new DateTime(2026, 9, 29, 1, 2, 3, DateTimeKind.Utc);
        // The boss HP history is what later rebuilds read for HP-gated branches, so it has to survive the cache as well.
        PullSummary.BossHP boss = new(0x100, 1000000, [new(start.Ticks, start.AddSeconds(60).Ticks)],
            [new(start.Ticks, 1000000, 1000000), new(start.AddSeconds(10).Ticks, 800000, 1000000), new(start.AddSeconds(30.5).Ticks, 250000, 1000000)]);
        var pull = new PullSummary(900, start, 60f, [0x100u],
            [new(1.5f, 1234, ExternalPlannerTimeline.ExternalStateKind.CastStart)], [new(20f, 35f)], [], [boss]);
        var path = ReplaySummaryCache.CachePath(dir, replay.FullName);
        ReplaySummaryCache.Write(path, ReplaySummaryCache.ForReplay(replay, [pull], false));
        var fresh = ReplaySummaryCache.ReadFresh(path, replay);
        Require(fresh != null && fresh.Pulls.Count == 1 && fresh.Pulls[0].NoTarget[0].End == 35f, "fresh read failed");
        var cached = fresh!.Pulls[0];
        Require(JsonSerializer.Serialize(cached) == JsonSerializer.Serialize(pull), "pull changed through the cache");
        float[] probes = [-1f, 0f, 5f, 10f, 30f, 31f, 60f, 61f];
        Require(probes.All(s => PullSummary.PrimaryBossHPPercent(cached, s) == PullSummary.PrimaryBossHPPercent(pull, s)), "boss HP differs through the cache");
        Require(probes.Count(s => PullSummary.PrimaryBossHPPercent(pull, s) != null) >= 3, "HP probes are vacuous");
        // Each key on its own makes the cache stale: a rewrite of the same size, and a new size under the old write time.
        var written = replay.LastWriteTimeUtc;
        File.SetLastWriteTimeUtc(replay.FullName, written.AddSeconds(1));
        replay.Refresh();
        Require(ReplaySummaryCache.ReadFresh(path, replay) == null, "new write time with the same size still fresh");
        File.SetLastWriteTimeUtc(replay.FullName, written);
        replay.Refresh();
        Require(ReplaySummaryCache.ReadFresh(path, replay) != null, "restored write time not fresh");
        File.AppendAllText(replay.FullName, "more");
        File.SetLastWriteTimeUtc(replay.FullName, written);
        replay.Refresh();
        Require(replay.LastWriteTimeUtc == written, "write time not restored");
        Require(ReplaySummaryCache.ReadFresh(path, replay) == null, "new size with the same write time still fresh");
        Require(ReplaySummaryCache.Read(path) != null, "stale cache unreadable for rebuilds");
        Require(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "temp file left behind");
        // A write that fails (here the target is a directory) throws and still leaves no temp file behind.
        var blocked = ReplaySummaryCache.CachePath(dir, "Blocked.log");
        Directory.CreateDirectory(blocked);
        var threw = false;
        try { ReplaySummaryCache.Write(blocked, ReplaySummaryCache.ForReplay(replay, [pull], false)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { threw = true; }
        Require(threw, "write over a directory did not fail");
        Require(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "failed write left its temp file");
    });
    Check("corrupt cache is stale", () =>
    {
        var dir = Path.Combine(fixtureRoot, "cache2");
        Directory.CreateDirectory(dir);
        var replay = new FileInfo(Path.Combine(dir, "Other_2026.log"));
        File.WriteAllText(replay.FullName, "x");
        replay.Refresh();
        var path = ReplaySummaryCache.CachePath(dir, replay.FullName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"Version\":1,\"Pulls\":[{");
        Require(ReplaySummaryCache.Read(path) == null && ReplaySummaryCache.ReadFresh(path, replay) == null, "corrupt cache accepted");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(ReplaySummaryCache.ForReplay(replay, [], false) with { Version = ReplaySummaryCache.Version + 1 }));
        Require(ReplaySummaryCache.Read(path) == null, "other version accepted");
        // System.Text.Json leaves a missing constructor argument or a null list element as null, so valid JSON of the right version can
        // still have a hole anywhere in a pull; it must read as never summarized (under matching keys too), not throw at every rebuild.
        var pull = new PullSummary(1, DateTime.UnixEpoch, 10f, [1u], [new(1f, 2, ExternalPlannerTimeline.ExternalStateKind.CastStart)], [new(1f, 2f)], [new(3f, 4f)],
            [new(1, 100, [new(0, 10)], [new(0, 100, 100)])]);
        var whole = JsonSerializer.Serialize(ReplaySummaryCache.ForReplay(replay, [pull], false));
        File.WriteAllText(path, whole);
        Require(ReplaySummaryCache.ReadFresh(path, replay) != null, "complete cache rejected");
        var empty = JsonSerializer.Serialize(ReplaySummaryCache.ForReplay(replay, [], false));
        List<string> holes = [empty.Replace("\"Pulls\":[]", "\"Pulls\":[null]"), empty.Replace("\"Pulls\":[]", "\"Pulls\":[{\"Zone\":1}]")];
        foreach (var list in new[] { "BossOIDs", "Events", "NoTarget", "BossUntargetable", "Bosses", "Existence", "History" })
        {
            holes.Add(whole.Replace($"\"{list}\":[", $"\"{list}\":null,\"Hole\":[")); // the list itself missing (unknown members are skipped)
            holes.Add(whole.Replace($"\"{list}\":[", $"\"{list}\":[null,"));
        }
        Require(holes.All(text => text != whole && text != empty), "a hole was not made");
        foreach (var text in holes)
        {
            File.WriteAllText(path, text);
            Require(ReplaySummaryCache.Read(path) == null && ReplaySummaryCache.ReadFresh(path, replay) == null, "incomplete cache accepted: " + text);
        }
    });
    Check("a cache that cannot be opened is an error for Read and stale for ReadFresh", () =>
    {
        var dir = Path.Combine(fixtureRoot, "cache3");
        Directory.CreateDirectory(dir);
        var replay = new FileInfo(Path.Combine(dir, "Held_2026.log"));
        File.WriteAllText(replay.FullName, "x");
        replay.Refresh();
        var path = ReplaySummaryCache.CachePath(dir, replay.FullName);
        ReplaySummaryCache.Write(path, ReplaySummaryCache.ForReplay(replay, [], false));
        // A cache held by another process is not a missing or corrupt one: a rebuild must be able to tell and not leave it out.
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var threw = false;
            try { ReplaySummaryCache.Read(path); }
            catch (IOException) { threw = true; }
            Require(threw, "a held cache read as missing or corrupt");
            Require(ReplaySummaryCache.ReadFresh(path, replay) == null, "a held cache read as fresh");
        }
        Require(ReplaySummaryCache.ReadFresh(path, replay) != null, "released cache not fresh");
        Require(ReplaySummaryCache.Read(ReplaySummaryCache.CachePath(dir, "Missing.log")) == null, "a missing cache is not null");
    });

    // A pull summary with the given NoTarget windows and a fixed sync cast, for gate checks.
    PullSummary GatePull(ushort zone, uint boss, float duration, params (float Start, float End)[] windows)
        => new(zone, DateTime.UnixEpoch.AddHours(zone).AddMinutes(windows.Length + duration), duration, [boss],
            [new(5f, 4321, ExternalPlannerTimeline.ExternalStateKind.CastStart)],
            windows.Select(w => new ReplayTimelineExtractor.Window(w.Start, w.End)).ToList(), [], []);
    Check("gate keeps a window every pull reproduces", () =>
    {
        var pulls = new[] { 30f, 31f, 30.5f, 29.5f }.Select(s => GatePull(9001, 0x10, 120f, (s, s + 15f))).ToList();
        var result = AutoTimelineGate.Evaluate(9001, pulls);
        Require(result.Timeline is { Source: ExternalPlannerTimeline.TimelineSource.AutoReplay }, "no auto timeline");
        Require(result.KeptPublishable == 1, $"kept {result.KeptPublishable}");
    });
    Check("gate drops a window that does not reproduce", () =>
    {
        // Three pulls go away around 30 s, three around 40 s, each for 9 s. Each group spreads over 4 s, so the 10 s gap between them
        // is no HP-gated branch (a clean 30/40 split would be one, and without HP data its windows would go unevaluated instead) and
        // the pulls merge into one window (35-44 s, confidence 0.5 from the 30 and 40 s quartiles) that is publishable. Holding out an
        // early pull predicts 40 s (after its window ended at 35 or 39 s) and holding out a late pull predicts 30 s (more than 5 s
        // before its window at 40 or 44 s), each prediction publishable at confidence 0.5: no held-out pull is hit.
        // Confirmation also takes a real window starting from 5 s before to 30 s after the prediction: the 30 s predictions for the
        // 40, 40 and 44 s pulls are confirmed (40 and 44 lie in [25, 60]), the 40 s predictions for the 26, 30 and 30 s pulls are not
        // (their windows start before 35). 3 of 6 confirmed is under two thirds (3 * 3 = 9 < 6 * 2 = 12): still dropped.
        var pulls = new[] { 26f, 30f, 30f, 40f, 40f, 44f }.Select(s => GatePull(9002, 0x10, 120f, (s, s + 9f))).ToList();
        var result = AutoTimelineGate.Evaluate(9002, pulls);
        Require(result.Timeline!.Sequences.Single().Branch == null, "the pulls were split into a branch");
        Require(result.KeptPublishable == 0, $"kept {result.KeptPublishable}");
        Require(result.Report.Any(l => l.Contains("dropped")), "no dropped line in the report");
        Require(result.Report.Any(l => l.Trim() == "window 35.0-44.0 evaluated=6 confirmed=3 hits=0 dropped"), string.Join(" | ", result.Report));
    });
    Check("gate keeps a window whose predictions land on short real downtimes when some hit", () =>
    {
        // Three pulls go away at 30 s for 12 s and two for 7 s. The full set's window is 30-42 s (end median 42 of 37, 37, 42, 42, 42;
        // confidence 0.75), publishable. Holding out a 12 s pull leaves ends 37, 37, 42, 42: median 39.5, a 9.5 s window, predicted
        // at 30 s inside the pull's real 12 s window: a hit. Holding out a 7 s pull leaves ends 37, 42, 42, 42: 30-42 s again. Its real
        // 7 s window is too short to be a hit, but it starts at 30 s, inside [25, 60], and lasts at least MinConfirmSeconds: the
        // prediction is no false alarm. 5 evaluated, 5 confirmed, 3 hits: kept (3 hits alone, 9 < 10, would have dropped it).
        var pulls = new[] { 12f, 12f, 12f, 7f, 7f }.Select(d => GatePull(9011, 0x10, 120f, (30f, 30f + d))).ToList();
        var result = AutoTimelineGate.Evaluate(9011, pulls);
        Require(result.Report.Any(l => l.Trim() == "window 30.0-42.0 evaluated=5 confirmed=5 hits=3 kept"), string.Join(" | ", result.Report));
        Require(result.KeptPublishable == 1, $"kept {result.KeptPublishable}");
    });
    Check("gate drops a window no held-out pull hits, however many it confirms", () =>
    {
        // Three pulls go away at 30 s for 9 s and two for 7 s. The full set's window is 30-39 s (end median 39 of 37, 37, 39, 39, 39;
        // confidence 0.9), publishable. Holding out a 9 s pull leaves ends 37, 37, 39, 39: median 38, an 8 s window under the publish
        // bar, so that pull is not evaluated. Holding out a 7 s pull leaves ends 37, 39, 39, 39: the 30-39 s window again, predicted
        // at 30 s, and confirmed by the pull's real 7 s window starting there, but no hit. 2 evaluated, 2 confirmed, 0 hits: a window
        // no held-out pull reproduces as a real downtime is dropped (MinHits).
        var pulls = new[] { 9f, 9f, 9f, 7f, 7f }.Select(d => GatePull(9014, 0x10, 120f, (30f, 30f + d))).ToList();
        var result = AutoTimelineGate.Evaluate(9014, pulls);
        Require(result.Report.Any(l => l.Trim() == "window 30.0-39.0 evaluated=2 confirmed=2 hits=0 dropped"), string.Join(" | ", result.Report));
        Require(result.KeptPublishable == 0, $"kept {result.KeptPublishable}");
    });
    Check("gate does not confirm a prediction with a real gap shorter than MinConfirmSeconds", () =>
    {
        // Five pulls go away at 30 s for 12 s; three only drop their target at 40 s for 2 s (or 3 s). Starts 30 x5 and 40 x3: median 30,
        // quartiles 30 and 40 (confidence 0.5); ends 42 (or 42 x5 and 43 x3): the full window is 30-42 s, publishable. Holding out a
        // 12 s pull leaves starts 30 x4 and 40 x3: median 30 again, confidence 0.5, predicted at 30 s: a hit. Holding out a short pull
        // leaves starts 30 x5 and 40 x2: predicted at 30 s; the pull's real gap starts at 40 s, inside [25, 60], but only a gap of at
        // least MinConfirmSeconds confirms. 2 s: 8 evaluated, 5 confirmed (the hits), 15 < 16: dropped. 3 s: 8 confirmed, kept.
        foreach (var (gap, zone, expected) in new[] { (2f, (ushort)9015, "window 30.0-42.0 evaluated=8 confirmed=5 hits=5 dropped"),
            (3f, (ushort)9016, "window 30.0-42.0 evaluated=8 confirmed=8 hits=5 kept") })
        {
            var pulls = Enumerable.Repeat((30f, 42f), 5).Concat(Enumerable.Repeat((40f, 40f + gap), 3)).Select(w => GatePull(zone, 0x10, 120f, w)).ToList();
            var result = AutoTimelineGate.Evaluate(zone, pulls);
            Require(result.Report.Any(l => l.Trim() == expected), $"{gap}: {string.Join(" | ", result.Report)}");
            Require(result.KeptPublishable == (gap >= 3f ? 1 : 0), $"{gap}: kept {result.KeptPublishable}");
        }
    });
    Check("gate drops a window whose predictions land far from any real downtime", () =>
    {
        // Five pulls go away at 30 s for 10 s. Three only drop their target at 30 s for 2 s (too short to confirm anything) and then
        // for 4 s at 62 s (or 58 s). Index 0 merges starts all 30 and ends 32 x3 and 40 x5: the full window is 30-40 s (end quartiles
        // 32 and 40, confidence 0.6); the 4 s windows are in three pulls of eight, no majority. Holding out any pull still leaves the
        // 40 s end median: 30-40 s predicted at 30 s. The five 10 s pulls hit it. At 62 s an outlier's 4 s window starts 32 s after
        // the prediction, past FalseAlarmAfter: 8 evaluated, 5 confirmed, 15 < 16, dropped. At 58 s it starts 28 s after: 8
        // confirmed, kept.
        foreach (var (outlier, zone, expected) in new[] { (62f, (ushort)9012, "window 30.0-40.0 evaluated=8 confirmed=5 hits=5 dropped"),
            (58f, (ushort)9013, "window 30.0-40.0 evaluated=8 confirmed=8 hits=5 kept") })
        {
            var pulls = Enumerable.Range(0, 5).Select(_ => GatePull(zone, 0x10, 120f, (30f, 40f)))
                .Concat(Enumerable.Range(0, 3).Select(_ => GatePull(zone, 0x10, 120f, (30f, 32f), (outlier, outlier + 4f)))).ToList();
            var result = AutoTimelineGate.Evaluate(zone, pulls);
            Require(result.Report.Any(l => l.Trim() == expected), $"{outlier}: {string.Join(" | ", result.Report)}");
            Require(result.KeptPublishable == (outlier < 60f ? 1 : 0), $"{outlier}: kept {result.KeptPublishable}");
        }
    });
    Check("gate withholds windows below three pulls", () =>
    {
        var pulls = new[] { 30f, 30f }.Select(s => GatePull(9003, 0x10, 120f, (s, s + 15f))).ToList();
        var result = AutoTimelineGate.Evaluate(9003, pulls);
        Require(result.KeptPublishable == 0 && result.Timeline!.Sequences.All(s => s.Windows is not { Count: > 0 }), "windows kept from two pulls");
        Require(result.Timeline!.Sequences.Single().States.Count > 0, "sync points lost");
    });
    Check("wiped pull is not evaluated past its end", () =>
    {
        // Three full pulls reproduce the window at 60 s; two wipes at 40 s must not count against it.
        var pulls = new[] { GatePull(9004, 0x10, 120f, (60f, 75f)), GatePull(9004, 0x10, 120f, (60f, 75f)), GatePull(9004, 0x10, 120f, (61f, 76f)),
            GatePull(9004, 0x10, 40f), GatePull(9004, 0x10, 40f) }.ToList();
        var result = AutoTimelineGate.Evaluate(9004, pulls);
        Require(result.KeptPublishable == 1, $"kept {result.KeptPublishable}");
    });
    Check("gate keeps short and non-NoTarget windows untouched", () =>
    {
        // Each pull has a 3 s NoTarget blip, a long NoTarget window, and a boss-untargetable stretch with adds up (which the build
        // turns into a BossUntargetable and an AddsPresent window). Only long NoTarget windows go through the holdout.
        var pulls = new[] { 30f, 31f, 30f }.Select(s => new PullSummary(9005, DateTime.UnixEpoch.AddHours(9005), 120f, [0x10u],
            [new(5f, 4321, ExternalPlannerTimeline.ExternalStateKind.CastStart)],
            [new(s, s + 3f), new(s + 40f, s + 55f)], [new(s + 60f, s + 80f)], [])).ToList();
        var built = ReplayTimelineExtractor.BuildBossSet(9005, pulls).Sequences.Single().Windows!.Where(w => !AutoTimelineGate.Publishable(w)).ToList();
        Require(built.Any(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget && w.End - w.Start < 5f)
            && built.Any(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable)
            && built.Any(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.AddsPresent), "fixture lacks a short, boss or adds window");
        var result = AutoTimelineGate.Evaluate(9005, pulls);
        var gated = result.Timeline!.Sequences.Single().Windows!.Where(w => !AutoTimelineGate.Publishable(w)).ToList();
        Require(gated.SequenceEqual(built), "short or non-NoTarget windows changed");
    });
    Check("gate leaves windows past an HP branch without threshold unevaluated", () =>
    {
        // Three pulls go away at 30 s and three at 40 s: a clean split, so the set is an HP-gated branch deciding at 30 s. The pulls
        // carry no HP data, so no threshold tells which sibling a held-out pull took: neither sibling's window is evaluated.
        var pulls = new[] { 30f, 30f, 30f, 40f, 40f, 40f }.Select(s => GatePull(9006, 0x10, 120f, (s, s + 9f))).ToList();
        var result = AutoTimelineGate.Evaluate(9006, pulls);
        var sequences = result.Timeline!.Sequences;
        Require(sequences.Count == 2 && sequences.All(s => s.Branch is { HpThreshold: null, DecisionTime: 30f }), "not an HP branch at 30 s without threshold");
        var report = string.Join(" | ", result.Report);
        Require(result.Report.Any(l => l.Trim() == "set=10 pulls=6 branched decision=30.0 threshold=-"), $"branch not reported: {report}");
        Require(result.Report.Any(l => l.Trim() == "early window 30.0-39.0 evaluated=0 confirmed=0 hits=0 dropped")
            && result.Report.Any(l => l.Trim() == "late window 40.0-49.0 evaluated=0 confirmed=0 hits=0 dropped"), $"sibling windows not reported as unevaluated: {report}");
        Require(result.KeptPublishable == 0 && sequences.All(s => !s.Windows!.Any(AutoTimelineGate.Publishable)), $"kept {result.KeptPublishable}");
    });
    Check("branch siblings count the window they share once", () =>
    {
        // Every pull goes away early, then three at 30 s and three at 40 s: the branch decides at 30 s and both siblings carry the early
        // window, which every held-out pull reproduces. Each sibling is built from its own pulls: the early ones go away at 10 s, the
        // late ones at 11 s, so the two copies of the shared window differ. The early sibling's copy counts, the late sibling's (it
        // starts before the decision point) does not: once, by the gate and by CountPublishable.
        var pulls = new[] { 30f, 30f, 30f, 40f, 40f, 40f }.Select(s => GatePull(9007, 0x10, 120f, (s < 35f ? 10f : 11f, s < 35f ? 20f : 21f), (s, s + 9f))).ToList();
        var result = AutoTimelineGate.Evaluate(9007, pulls);
        var sequences = result.Timeline!.Sequences;
        Require(sequences.Count == 2 && sequences.All(s => s.Branch is { DecisionTime: 30f } && s.Windows!.Count(AutoTimelineGate.Publishable) == 1), "shared window not kept in both siblings");
        Require(sequences.Select(s => s.Windows!.Single(AutoTimelineGate.Publishable).Start).SequenceEqual([10f, 11f]), "the siblings' copies of the shared window do not differ");
        Require(result.KeptPublishable == 1, $"kept {result.KeptPublishable}");
        Require(AutoTimelineGate.CountPublishable(result.Timeline) == 1, $"counted {AutoTimelineGate.CountPublishable(result.Timeline)}");
    });
    Check("window with one evaluable held-out pull is dropped", () =>
    {
        // Five pulls go away at 39, 50, 50, 50 and 60 s for 15 s. The full set's window (50-65 s) is publishable: with five starts
        // the quartiles are 50 and 50. A rebuild from four pulls spans all four starts: only holding out the 39 s pull leaves a
        // 10 s spread (confidence 0.5, still publishable); every other rebuild falls under 0.5 and predicts nothing. The one
        // evaluable pull hits, and one is still less than MinEvaluated.
        var pulls = new[] { 39f, 50f, 50f, 50f, 60f }.Select(s => GatePull(9008, 0x10, 120f, (s, s + 15f))).ToList();
        var result = AutoTimelineGate.Evaluate(9008, pulls);
        Require(result.Report.Any(l => l.Trim() == "window 50.0-65.0 evaluated=1 confirmed=1 hits=1 dropped"), string.Join(" | ", result.Report));
        Require(result.KeptPublishable == 0, $"kept {result.KeptPublishable}");
    });
    Check("gate evaluation stops on cancellation", () =>
    {
        var pulls = new[] { 30f, 31f, 30.5f }.Select(s => GatePull(9017, 0x10, 120f, (s, s + 15f))).ToList();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var threw = false;
        try { AutoTimelineGate.Evaluate(9017, pulls, cancel: cancel.Token); }
        catch (OperationCanceledException) { threw = true; }
        Require(threw, "a cancelled evaluation ran to the end");
        Require(AutoTimelineGate.Evaluate(9017, pulls).KeptPublishable == 1, "the window is not kept without cancellation");
    });
    Check("gate builds each boss set from its most recent pulls when capped", () =>
    {
        // Three older pulls never go away; the three most recent go away at 30 s for 15 s. All six: the window is in three of six
        // pulls (the majority of six is three), 30-45 s. Holding out a recent pull leaves two windows in five pulls, under the
        // majority: nothing predicted. Holding out an old pull predicts 30 s where it has no downtime: 3 evaluated, 0 confirmed,
        // dropped. Capped at the 3 most recent (by start, whatever their order in the list), every holdout predicts 30 s in a pull
        // that has the window: 3 hits, kept.
        PullSummary Pull(int minute, bool away) => (away ? GatePull(9018, 0x10, 120f, (30f, 45f)) : GatePull(9018, 0x10, 120f)) with { Start = DateTime.UnixEpoch.AddDays(1).AddMinutes(minute) };
        List<PullSummary> pulls = [Pull(5, true), Pull(0, false), Pull(4, true), Pull(1, false), Pull(2, false), Pull(3, true)];
        var all = AutoTimelineGate.Evaluate(9018, pulls);
        Require(all.Report.Any(l => l.Trim() == "set=10 pulls=6") && all.Report.Any(l => l.Trim() == "window 30.0-45.0 evaluated=3 confirmed=0 hits=0 dropped")
            && all.KeptPublishable == 0, $"uncapped: {string.Join(" | ", all.Report)}");
        var capped = AutoTimelineGate.Evaluate(9018, pulls, maxPullsPerSet: 3);
        Require(capped.Report.Any(l => l.Trim() == "set=10 pulls=3 (most recent of 6)") && capped.Report.Any(l => l.Trim() == "window 30.0-45.0 evaluated=3 confirmed=3 hits=3 kept")
            && capped.KeptPublishable == 1, $"capped: {string.Join(" | ", capped.Report)}");
        Require(capped.Report[0] == "zone=9018 pulls=6", $"zone line: {capped.Report[0]}");
    });
    Check("the lower-ranked timeline is the best of FFLogs, Cactbot and EventTrigger", () =>
    {
        ExternalPlannerTimeline.TimelineDefinition Def(ExternalPlannerTimeline.TimelineSource s) => new(9601, s.ToString(), true, [], s, 0.5f);
        try
        {
            TimelineStore.SetCandidatesForTesting(9601, TimelineStore.Rank([Def(ExternalPlannerTimeline.TimelineSource.EventTrigger), Def(ExternalPlannerTimeline.TimelineSource.Replay),
                Def(ExternalPlannerTimeline.TimelineSource.AutoReplay), Def(ExternalPlannerTimeline.TimelineSource.Cactbot), Def(ExternalPlannerTimeline.TimelineSource.FFLogs),
                Def(ExternalPlannerTimeline.TimelineSource.User)]));
            Require(AutoTimelineGate.LowerRanked(9601)?.Source == ExternalPlannerTimeline.TimelineSource.FFLogs, $"all sources: {AutoTimelineGate.LowerRanked(9601)?.Source}");
            TimelineStore.SetCandidatesForTesting(9601, TimelineStore.Rank([Def(ExternalPlannerTimeline.TimelineSource.EventTrigger), Def(ExternalPlannerTimeline.TimelineSource.AutoReplay),
                Def(ExternalPlannerTimeline.TimelineSource.Cactbot)]));
            Require(AutoTimelineGate.LowerRanked(9601)?.Source == ExternalPlannerTimeline.TimelineSource.Cactbot, $"no FFLogs: {AutoTimelineGate.LowerRanked(9601)?.Source}");
            TimelineStore.SetCandidatesForTesting(9601, TimelineStore.Rank([Def(ExternalPlannerTimeline.TimelineSource.Replay), Def(ExternalPlannerTimeline.TimelineSource.AutoReplay)]));
            Require(AutoTimelineGate.LowerRanked(9601) == null, $"only higher sources: {AutoTimelineGate.LowerRanked(9601)?.Source}");
        }
        finally
        {
            TimelineStore.SetCandidatesForTesting(9601, null);
        }
    });
    Check("gate report numbers ignore the thread culture", () =>
    {
        var previous = CultureInfo.CurrentCulture;
        var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        comma.NumberFormat.NumberDecimalSeparator = ",";
        try
        {
            CultureInfo.CurrentCulture = comma;
            var dir = Path.Combine(fixtureRoot, "culture");
            AutoTimelineGate.Publish(dir, 9009, AutoTimelineGate.Evaluate(9009, new[] { 30f, 31f, 30.5f }.Select(s => GatePull(9009, 0x10, 120f, (s, s + 15f))).ToList()), null);
            AutoTimelineGate.Publish(dir, 9010, AutoTimelineGate.Evaluate(9010, new[] { 30f, 30f, 30f, 40f, 40f, 40f }.Select(s => GatePull(9010, 0x10, 120f, (s, s + 9f))).ToList()), null);
            var report = File.ReadAllLines(Path.Combine(dir, "auto", "report.txt"));
            Require(report.Any(l => l.Trim() == "window 30.5-45.5 evaluated=3 confirmed=3 hits=3 kept")
                && report.Any(l => l.Trim() == "set=10 pulls=6 branched decision=30.0 threshold=-"), string.Join(" | ", report));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    });

    Check("auto timelines rank between user files and FFLogs", () =>
    {
        ExternalPlannerTimeline.TimelineDefinition Def(ExternalPlannerTimeline.TimelineSource s) => new(9100, s.ToString(), true, [], s, 0.5f);
        var ranked = TimelineStore.Rank([Def(ExternalPlannerTimeline.TimelineSource.EventTrigger), Def(ExternalPlannerTimeline.TimelineSource.AutoReplay),
            Def(ExternalPlannerTimeline.TimelineSource.FFLogs), Def(ExternalPlannerTimeline.TimelineSource.User), Def(ExternalPlannerTimeline.TimelineSource.Replay)]);
        Require(string.Join(",", ranked.Select(t => t.Source)) == "Replay,User,AutoReplay,FFLogs,EventTrigger", string.Join(",", ranked.Select(t => t.Source)));
    });
    Check("store reads the auto folder, manual files win", () =>
    {
        var dir = Path.Combine(fixtureRoot, "store-auto");
        Directory.CreateDirectory(Path.Combine(dir, "auto", "cache"));
        ExternalPlannerTimeline.TimelineDefinition Def(ExternalPlannerTimeline.TimelineSource s, int zone) => new(zone, s.ToString(), true,
            [new(0, 0, [new(5f, "", ExternalPlannerTimeline.ExternalStateKind.CastStart, [1u], 0)], null)], s, 0.9f);
        File.WriteAllText(Path.Combine(dir, "9200-replay.json"), System.Text.Json.JsonSerializer.Serialize(new { Timelines = new[] { Def(ExternalPlannerTimeline.TimelineSource.Replay, 9200) } }));
        File.WriteAllText(Path.Combine(dir, "auto", "9200-auto.json"), System.Text.Json.JsonSerializer.Serialize(new { Timelines = new[] { Def(ExternalPlannerTimeline.TimelineSource.AutoReplay, 9200) } }));
        File.WriteAllText(Path.Combine(dir, "auto", "9201-auto.json"), System.Text.Json.JsonSerializer.Serialize(new { Timelines = new[] { Def(ExternalPlannerTimeline.TimelineSource.AutoReplay, 9201) } }));
        File.WriteAllText(Path.Combine(dir, "auto", "cache", "x.log.json"), "{\"Timelines\":[]}");
        // A summary cache is never read as timelines, even one that would parse as a timeline file.
        File.WriteAllText(Path.Combine(dir, "auto", "cache", "y.log.json"), System.Text.Json.JsonSerializer.Serialize(new { Timelines = new[] { Def(ExternalPlannerTimeline.TimelineSource.AutoReplay, 9202) } }));
        var previous = TimelineStore.UserDirectory;
        try
        {
            TimelineStore.UserDirectory = dir;
            TimelineStore.Reload();
            Require(TimelineStore.ForZone(9200)?.Source == ExternalPlannerTimeline.TimelineSource.Replay, "manual file lost to auto");
            Require(TimelineStore.ForZone(9201)?.Source == ExternalPlannerTimeline.TimelineSource.AutoReplay, "auto file not loaded");
            Require(TimelineStore.ForZone(9202) == null, "summary cache folder read as timelines");
        }
        finally
        {
            TimelineStore.UserDirectory = previous;
            TimelineStore.Reload();
        }
    });
    Check("auto output is written, withheld or removed", () =>
    {
        var dir = Path.Combine(fixtureRoot, "publish");
        var pulls = new[] { 30f, 31f, 30.5f }.Select(s => GatePull(9300, 0x10, 120f, (s, s + 15f))).ToList();
        var good = AutoTimelineGate.Evaluate(9300, pulls);
        var written = AutoTimelineGate.Publish(dir, 9300, good, null);
        var file = Path.Combine(dir, "auto", "9300-auto.json");
        Require(File.Exists(file) && TimelineStore.LoadUserFile(file).Single().Source == ExternalPlannerTimeline.TimelineSource.AutoReplay, "not written");
        Require(written == "zone=9300 written: 1 validated window", written);
        // An FFLogs timeline with more publishable windows than the auto one keeps the zone: the auto file is removed.
        var ffl = new ExternalPlannerTimeline.TimelineDefinition(9300, "ffl", true, [new(0, 0, [], null, null,
            [new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 10f, 30f, 0.9f), new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 60f, 80f, 0.9f)])],
            ExternalPlannerTimeline.TimelineSource.FFLogs, 0.9f);
        var line = AutoTimelineGate.Publish(dir, 9300, good, ffl);
        Require(!File.Exists(file) && line == "zone=9300 not written: FFLogs timeline has 2 windows, more than the 1 validated", $"not withheld: {line}");
        var report = File.ReadAllText(Path.Combine(dir, "auto", "report.txt"));
        Require(report.Split('\n').Count(l => l.StartsWith("zone=9300", StringComparison.Ordinal)) == 1, "report keeps two blocks for one zone");
        Require(!Directory.EnumerateFiles(Path.Combine(dir, "auto"), "*.tmp").Any(), "temp file left behind");
    });
    Check("failed auto writes leave no temp file", () =>
    {
        var dir = Path.Combine(fixtureRoot, "publish-blocked");
        var auto = AutoTimelineGate.AutoDirectory(dir);
        var result = AutoTimelineGate.Evaluate(9301, new[] { 30f, 31f, 30.5f }.Select(s => GatePull(9301, 0x10, 120f, (s, s + 15f))).ToList());
        Require(result.KeptPublishable == 1, $"kept {result.KeptPublishable}");
        // The timeline file, then the report, is a directory: moving the temp file over it fails, and the call throws without
        // leaving the temp file behind.
        foreach (var blocked in new[] { "9301-auto.json", "report.txt" })
        {
            Directory.CreateDirectory(Path.Combine(auto, blocked));
            var threw = false;
            try { AutoTimelineGate.Publish(dir, 9301, result, null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { threw = true; }
            Require(threw, $"publish over a directory {blocked} did not fail");
            Require(!Directory.EnumerateFiles(auto, "*.tmp").Any(), $"failed write of {blocked} left its temp file");
            Directory.Delete(Path.Combine(auto, blocked));
        }
    });
    Check("publish outcomes read as sentences, singular and plural", () =>
    {
        Require(AutoTimelineGate.Decide(1, null) == (true, "1 validated window"), AutoTimelineGate.Decide(1, null).Reason);
        Require(AutoTimelineGate.Decide(6, null) == (true, "6 validated windows"), AutoTimelineGate.Decide(6, null).Reason);
        Require(AutoTimelineGate.Decide(0, null) == (false, "no validated window"), AutoTimelineGate.Decide(0, null).Reason);
    });
    Check("publishable windows of legacy sequences are counted", () =>
    {
        var legacy = new ExternalPlannerTimeline.TimelineDefinition(9400, "et", true, [new(0, 0, [
            new(10f, "", ExternalPlannerTimeline.ExternalStateKind.Untargetable, [], 0), new(30f, "", ExternalPlannerTimeline.ExternalStateKind.Targetable, [], 0),
            new(40f, "", ExternalPlannerTimeline.ExternalStateKind.Untargetable, [], 0), new(44f, "", ExternalPlannerTimeline.ExternalStateKind.Targetable, [], 0)], null)],
            ExternalPlannerTimeline.TimelineSource.EventTrigger, 1f);
        Require(AutoTimelineGate.CountPublishable(legacy) == 1, $"counted {AutoTimelineGate.CountPublishable(legacy)}");
    });

    Check("automatic extraction is on by default and follows the timeline hints setting", () =>
    {
        Require(new BossModuleConfig().AutoExtractTimelines, "AutoExtractTimelines is off by default");
        var depends = typeof(BossModuleConfig).GetField(nameof(BossModuleConfig.AutoExtractTimelines))?.GetCustomAttributes(typeof(PropertyDisplayAttribute), false)
            .OfType<PropertyDisplayAttribute>().SingleOrDefault()?.Depends;
        Require(depends == nameof(BossModuleConfig.UseExternalTimelineHints), $"AutoExtractTimelines depends on '{depends}'");
        // Reads the field through the source-generated metadata the config screen and the saved file use.
        var shown = Service.Config.ConsoleCommand(["BossModuleConfig", "AutoExtractTimelines"], save: false);
        Require(shown is ["True"], $"generated metadata: {string.Join(" | ", shown)}");
    });

    // Extractor checks write under a fixture folder the store does not read: the reload after a rebuild is a no-op there.
    static void NoReload() { }
    // Waits until the extractor has drained its queue (the worker thread runs asynchronously).
    void WaitIdle(AutoTimelineExtractor x)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!x.Idle && sw.Elapsed < TimeSpan.FromSeconds(20))
            Thread.Sleep(20);
        Require(x.Idle, "extractor did not go idle");
    }
    Check("extractor summarizes once and publishes the zone", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker1");
        var replays = Path.Combine(root, "replays");
        Directory.CreateDirectory(replays);
        var files = new[] { 30f, 31f, 30.5f }.Select((s, i) => { var f = Path.Combine(replays, $"Duty_{i}.log"); File.WriteAllText(f, $"r{i}"); return (f, s); }).ToList();
        var calls = 0;
        using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => replays, (path, _) =>
        {
            Interlocked.Increment(ref calls);
            var s = files.Single(f => f.f == path).s;
            return [GatePull(9500, 0x10, 120f, (s, s + 15f)) with { Start = DateTime.UnixEpoch.AddMinutes(files.FindIndex(f => f.f == path)) }];
        }, reloadStore: NoReload);
        foreach (var (f, _) in files)
            x.EnqueueReplay(f);
        WaitIdle(x);
        Require(File.Exists(Path.Combine(root, "timelines", "auto", "9500-auto.json")), "zone not published");
        Require(calls == 3, $"summarized {calls} times");
        x.EnqueueReplay(files[0].f);
        WaitIdle(x);
        Require(calls == 3, "unchanged replay summarized again");
        x.EnqueueAll();
        WaitIdle(x);
        Require(calls == 3, "EnqueueAll re-summarized fresh replays");
    });
    Check("missing replay is skipped", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker2");
        var calls = 0;
        using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (_, _) => { ++calls; return []; }, reloadStore: NoReload);
        x.EnqueueReplay(Path.Combine(root, "gone.log"));
        WaitIdle(x);
        Require(calls == 0 && !File.Exists(ReplaySummaryCache.CachePath(Path.Combine(root, "timelines"), "gone.log")), "missing replay processed");
    });
    Check("dispose during a slow summarize", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker3");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Slow.log");
        File.WriteAllText(file, "x");
        var started = new ManualResetEventSlim();
        var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (_, cancel) =>
        {
            started.Set();
            cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            cancel.ThrowIfCancellationRequested();
            return [];
        }, reloadStore: NoReload);
        x.EnqueueReplay(file);
        Require(started.Wait(TimeSpan.FromSeconds(10)), "summarize never started");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        x.Dispose();
        Require(sw.Elapsed < TimeSpan.FromSeconds(5), $"dispose took {sw.Elapsed}");
        Require(!File.Exists(ReplaySummaryCache.CachePath(Path.Combine(root, "timelines"), file)), "cancelled summary was written");
    });
    Check("directory is read per job", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker4");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "D.log");
        File.WriteAllText(file, "x");
        var target = Path.Combine(root, "first");
        using var x = new AutoTimelineExtractor(() => target, () => root, (_, _) => [], reloadStore: NoReload);
        target = Path.Combine(root, "second");
        x.EnqueueReplay(file);
        WaitIdle(x);
        Require(File.Exists(ReplaySummaryCache.CachePath(Path.Combine(root, "second"), file)), "old directory used");
    });
    Check("failed summaries are remembered", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker5");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Bad.log");
        File.WriteAllText(file, "x");
        var calls = 0;
        using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (_, _) => { ++calls; return null; }, reloadStore: NoReload);
        x.EnqueueReplay(file);
        WaitIdle(x);
        x.EnqueueReplay(file);
        WaitIdle(x);
        Require(calls == 1 && ReplaySummaryCache.Read(ReplaySummaryCache.CachePath(Path.Combine(root, "timelines"), file))?.Failed == true, "failure not remembered");
    });

    // Polls a condition the worker thread brings about: true once it holds, false after the timeout.
    bool WaitFor(Func<bool> condition, double seconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(seconds))
                return false;
            Thread.Sleep(5);
        }
        return true;
    }
    // Three replays Z_0..Z_2 of one zone whose pulls publish a window, and a summarizer that serves them.
    (string Timelines, string Replays, AutoTimelineExtractor.Summarizer Summarize) ExtractorZone(string name, ushort zone)
    {
        var root = Path.Combine(fixtureRoot, name);
        var replays = Path.Combine(root, "replays");
        Directory.CreateDirectory(replays);
        var starts = new[] { 30f, 31f, 30.5f };
        for (var i = 0; i < starts.Length; ++i)
            File.WriteAllText(Path.Combine(replays, $"Z_{i}.log"), $"r{i}");
        return (Path.Combine(root, "timelines"), replays, (path, _) =>
        {
            var i = int.Parse(Path.GetFileNameWithoutExtension(path)[2..], CultureInfo.InvariantCulture);
            return [GatePull(zone, 0x10, 120f, (starts[i], starts[i] + 15f)) with { Start = DateTime.UnixEpoch.AddMinutes(i) }];
        });
    }
    bool NoTempFiles(string dir) => !Directory.Exists(dir) || !Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories).Any();
    // Held with FileShare.None, a file cannot be replaced: the worker's first attempt fails for certain, and the file is released
    // only once the worker reports that it is waiting to try again.
    Check("a locked timeline file is written on a later attempt", () =>
    {
        var (timelines, replays, summarize) = ExtractorZone("worker6", 9501);
        var target = Path.Combine(AutoTimelineGate.AutoDirectory(timelines), "9501-auto.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "held");
        // The reload runs after the publish, still inside the rebuild job: the status it sees is the job's once the retry succeeded.
        AutoTimelineExtractor? self = null;
        string? statusAfterPublish = null;
        using var x = new AutoTimelineExtractor(() => timelines, () => replays, summarize, reloadStore: () => statusAfterPublish = self!.Status);
        self = x;
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            x.EnqueueAll();
            Require(WaitFor(() => x.Status.Contains("retrying", StringComparison.Ordinal), 10), $"no retry reported: {x.Status}");
        }
        WaitIdle(x);
        Require(TimelineStore.LoadUserFile(target) is [{ Source: ExternalPlannerTimeline.TimelineSource.AutoReplay }], "held timeline file never replaced");
        Require(NoTempFiles(timelines), "temp file left behind");
        Require(statusAfterPublish != null && statusAfterPublish.Contains("rebuilding zone 9501", StringComparison.Ordinal)
            && !statusAfterPublish.Contains("retrying", StringComparison.Ordinal), $"retry outlived the write: {statusAfterPublish}");
    });
    Check("a failing report still reloads the zone file it follows", () =>
    {
        var (timelines, replays, summarize) = ExtractorZone("worker12", 9505);
        var auto = AutoTimelineGate.AutoDirectory(timelines);
        var report = Path.Combine(auto, "report.txt");
        Directory.CreateDirectory(auto);
        File.WriteAllText(report, "held");
        var reloads = 0;
        using var x = new AutoTimelineExtractor(() => timelines, () => replays, summarize, reloadStore: () => Interlocked.Increment(ref reloads));
        // Held through every attempt: Publish writes the zone file, then fails on the report, each time.
        using (new FileStream(report, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            x.EnqueueAll();
            WaitIdle(x);
        }
        Require(TimelineStore.LoadUserFile(Path.Combine(auto, "9505-auto.json")) is [{ Source: ExternalPlannerTimeline.TimelineSource.AutoReplay }], "zone file not written");
        Require(File.ReadAllText(report) == "held" && x.Status.Contains("failed", StringComparison.Ordinal), $"report did not fail: {x.Status}");
        Require(reloads == 1, $"reloads after a failed report: {reloads}");
        Require(NoTempFiles(timelines), "temp file left behind");
        x.EnqueueAll();
        WaitIdle(x);
        Require(reloads == 2 && File.ReadAllText(report).StartsWith("zone=9505 written", StringComparison.Ordinal), $"reloads after a clean publish: {reloads}");
    });
    Check("status counts the queue as it is now", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker13");
        Directory.CreateDirectory(root);
        var files = new[] { "Q0.log", "Q1.log", "Q2.log" }.Select(n => Path.Combine(root, n)).ToList();
        foreach (var f in files)
            File.WriteAllText(f, "q");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (_, _) =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20));
            return [];
        }, reloadStore: NoReload);
        try
        {
            x.EnqueueReplay(files[0]);
            Require(entered.Wait(TimeSpan.FromSeconds(10)), "summarize never started");
            x.EnqueueReplay(files[1]);
            x.EnqueueReplay(files[2]);
            Require(x.Status.StartsWith("summarizing Q0.log (2 queued)", StringComparison.Ordinal), $"stale queue count: {x.Status}");
        }
        finally { release.Set(); }
        WaitIdle(x);
        Require(x.Status == "idle", $"idle status: {x.Status}");
    });
    Check("a replay that changes while it is read is summarized again", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker14");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Growing.log");
        File.WriteAllText(file, "partial");
        var calls = 0;
        // The first read fails on a recording that is still being written, and the recording closes before the read returns.
        using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (path, _) =>
        {
            if (++calls > 1)
                return [];
            File.AppendAllText(path, " and the rest");
            return null;
        }, reloadStore: NoReload);
        x.EnqueueReplay(file);
        WaitIdle(x);
        x.EnqueueReplay(file);
        WaitIdle(x);
        Require(calls == 2, $"changed replay summarized {calls} times");
        var cache = ReplaySummaryCache.CachePath(Path.Combine(root, "timelines"), file);
        Require(ReplaySummaryCache.ReadFresh(cache, new FileInfo(file)) is { Failed: false }, "changed replay stuck as failed");
        x.EnqueueReplay(file);
        WaitIdle(x);
        Require(calls == 2, "unchanged replay summarized again");
    });
    Check("a locked summary file is written on a later attempt", () =>
    {
        var (timelines, replays, summarize) = ExtractorZone("worker7", 9502);
        var replay = Path.Combine(replays, "Z_0.log");
        var cache = ReplaySummaryCache.CachePath(timelines, replay);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "stale");
        using var x = new AutoTimelineExtractor(() => timelines, () => replays, summarize, reloadStore: NoReload);
        using (new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            x.EnqueueReplay(replay);
            Require(WaitFor(() => x.Status.Contains("retrying", StringComparison.Ordinal), 10), $"no retry reported: {x.Status}");
        }
        WaitIdle(x);
        Require(ReplaySummaryCache.ReadFresh(cache, new FileInfo(replay)) is { Failed: false, Pulls.Count: 1 }, "held summary never replaced");
        Require(NoTempFiles(timelines), "temp file left behind");
    });
    Check("a write that keeps failing is logged and the queue moves on", () =>
    {
        var (timelines, replays, summarize) = ExtractorZone("worker8", 9503);
        var target = Path.Combine(AutoTimelineGate.AutoDirectory(timelines), "9503-auto.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "held");
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var previous = Service.LogHandlerDebug;
        Service.LogHandlerDebug = logged.Enqueue;
        try
        {
            using var x = new AutoTimelineExtractor(() => timelines, () => replays, summarize, reloadStore: NoReload);
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                x.EnqueueAll();
                WaitIdle(x);
            }
            Require(logged.Any(l => l.Contains("9503", StringComparison.Ordinal) && l.Contains("failed", StringComparison.Ordinal)), $"failure not logged: {string.Join(" | ", logged)}");
            Require(File.ReadAllText(target) == "held" && NoTempFiles(timelines), "held timeline file touched");
            // The worker survived the failed job: the next request for the zone writes it.
            x.EnqueueAll();
            WaitIdle(x);
            Require(TimelineStore.LoadUserFile(target) is [{ Source: ExternalPlannerTimeline.TimelineSource.AutoReplay }], "queue stopped after a failed job");
        }
        finally { Service.LogHandlerDebug = previous; }
    });
    Check("a summarizer that throws is remembered as failed", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker9");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Throws.log");
        File.WriteAllText(file, "x");
        var calls = 0;
        using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (_, _) => { ++calls; throw new InvalidOperationException("broken replay"); }, reloadStore: NoReload);
        x.EnqueueReplay(file);
        WaitIdle(x);
        x.EnqueueReplay(file);
        WaitIdle(x);
        Require(calls == 1 && ReplaySummaryCache.Read(ReplaySummaryCache.CachePath(Path.Combine(root, "timelines"), file))?.Failed == true, $"throwing summarizer not remembered (calls={calls})");
    });
    Check("a cancellation that is not Dispose does not stop the worker", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker10");
        Directory.CreateDirectory(root);
        var (first, second) = (Path.Combine(root, "A.log"), Path.Combine(root, "B.log"));
        File.WriteAllText(first, "a");
        File.WriteAllText(second, "b");
        using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root,
            (path, _) => path == first ? throw new OperationCanceledException("unrelated") : [], reloadStore: NoReload);
        x.EnqueueReplay(first);
        x.EnqueueReplay(second);
        WaitIdle(x);
        Require(ReplaySummaryCache.Read(ReplaySummaryCache.CachePath(Path.Combine(root, "timelines"), second)) is { Failed: false }, "worker stopped at a foreign cancellation");
    });
    Check("a summary cache that stays unreadable fails the rebuild and keeps the zone file", () =>
    {
        var (timelines, replays, summarize) = ExtractorZone("worker15", 9506);
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var previous = Service.LogHandlerDebug;
        Service.LogHandlerDebug = logged.Enqueue;
        try
        {
            using var x = new AutoTimelineExtractor(() => timelines, () => replays, summarize, reloadStore: NoReload);
            x.EnqueueAll();
            WaitIdle(x);
            var target = Path.Combine(AutoTimelineGate.AutoDirectory(timelines), "9506-auto.json");
            var report = Path.Combine(AutoTimelineGate.AutoDirectory(timelines), "report.txt");
            Require(File.Exists(target), "zone not published before the lock");
            var (before, reportBefore) = (File.ReadAllText(target), File.ReadAllText(report));
            // Held through every attempt: the rebuild cannot read one of the three summaries. Publishing from the other two (fewer
            // than MinPulls) would remove the zone file.
            using (new FileStream(ReplaySummaryCache.CachePath(timelines, Path.Combine(replays, "Z_1.log")), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                x.EnqueueAll();
                WaitIdle(x);
            }
            Require(File.Exists(target) && File.ReadAllText(target) == before && File.ReadAllText(report) == reportBefore, "zone file or report changed by a rebuild that could not read a summary");
            Require(x.Status.Contains("zone 9506 failed", StringComparison.Ordinal), $"failed rebuild not in the status: {x.Status}");
            Require(logged.Any(l => l.Contains("zone 9506 failed", StringComparison.Ordinal)), $"failed rebuild not logged: {string.Join(" | ", logged)}");
            Require(NoTempFiles(timelines), "temp file left behind");
        }
        finally { Service.LogHandlerDebug = previous; }
    });
    Check("a corrupt summary cache is skipped, logged once, and the others still publish", () =>
    {
        var (timelines, replays, summarize) = ExtractorZone("worker16", 9507);
        var corrupt = ReplaySummaryCache.CachePath(timelines, "Broken.log");
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
        File.WriteAllText(corrupt, "{\"Version\":1,\"Pulls\":[{");
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var previous = Service.LogHandlerDebug;
        Service.LogHandlerDebug = logged.Enqueue;
        try
        {
            using var x = new AutoTimelineExtractor(() => timelines, () => replays, summarize, reloadStore: NoReload);
            x.EnqueueAll();
            WaitIdle(x);
            Require(TimelineStore.LoadUserFile(Path.Combine(AutoTimelineGate.AutoDirectory(timelines), "9507-auto.json")) is [{ Source: ExternalPlannerTimeline.TimelineSource.AutoReplay }],
                $"zone not published past a corrupt summary: {x.Status}");
            // Rewritten, still corrupt: read again by the next rebuild, but reported once per session.
            File.WriteAllText(corrupt, "not json");
            File.SetLastWriteTimeUtc(corrupt, DateTime.UtcNow.AddMinutes(1));
            x.EnqueueAll();
            WaitIdle(x);
            Require(logged.Count(l => l.Contains("Broken.log.json", StringComparison.Ordinal)) == 1, $"corrupt summary not logged once: {string.Join(" | ", logged)}");
            Require(x.Status.Contains("zone=9507 written", StringComparison.Ordinal), $"second rebuild: {x.Status}");
        }
        finally { Service.LogHandlerDebug = previous; }
    });
    Check("status counts the replays whose summary failed since the last full pass", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker17");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "F0.log", "F1.log", "Ok.log" })
            File.WriteAllText(Path.Combine(root, name), name);
        using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root,
            (path, _) => Path.GetFileName(path).StartsWith('F') ? null : [], reloadStore: NoReload);
        x.EnqueueAll();
        WaitIdle(x);
        Require(x.Status.EndsWith(" | failed summaries: 2", StringComparison.Ordinal), $"first pass: {x.Status}");
        // The next pass reads the failures back from the cache: they are counted again, not added to the first pass.
        x.EnqueueAll();
        WaitIdle(x);
        Require(x.Status.EndsWith(" | failed summaries: 2", StringComparison.Ordinal), $"second pass: {x.Status}");
    });
    Check("a paused extractor starts no job until the pause ends, and disposes promptly while paused", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker18");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Combat.log");
        File.WriteAllText(file, "x");
        var paused = 1;
        var calls = 0;
        using (var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (_, _) => { Interlocked.Increment(ref calls); return []; },
            reloadStore: NoReload, shouldPause: () => Volatile.Read(ref paused) == 1))
        {
            x.EnqueueReplay(file);
            Require(WaitFor(() => x.Status.StartsWith("paused (in combat) (1 queued)", StringComparison.Ordinal), 5), $"not paused: {x.Status}");
            Thread.Sleep(1500); // past at least one poll of the pause
            Require(Volatile.Read(ref calls) == 0 && !x.Idle, $"a job started while paused (calls={calls}, {x.Status})");
            Volatile.Write(ref paused, 0);
            WaitIdle(x);
            Require(calls == 1 && x.Status == "idle", $"after the pause: calls={calls}, {x.Status}");
        }
        var held = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (_, _) => { Interlocked.Increment(ref calls); return []; },
            reloadStore: NoReload, shouldPause: () => true);
        File.WriteAllText(Path.Combine(root, "Combat2.log"), "y");
        held.EnqueueReplay(Path.Combine(root, "Combat2.log"));
        Require(WaitFor(() => held.Status.StartsWith("paused (in combat)", StringComparison.Ordinal), 5), $"not paused: {held.Status}");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        held.Dispose();
        Require(sw.Elapsed < TimeSpan.FromSeconds(0.5), $"dispose while paused took {sw.Elapsed}");
        Require(calls == 1, $"a paused job ran on dispose (calls={calls})");
    });
    Check("a replay deleted after its parse keeps its summary", () =>
    {
        var root = Path.Combine(fixtureRoot, "worker11");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Deleted.log");
        File.WriteAllText(file, "x");
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var previous = Service.LogHandlerDebug;
        Service.LogHandlerDebug = logged.Enqueue;
        try
        {
            using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (path, _) =>
            {
                File.Delete(path);
                return [GatePull(9504, 0x10, 120f, (30f, 45f))];
            }, reloadStore: NoReload);
            x.EnqueueReplay(file);
            WaitIdle(x);
            // Rebuilds read every summary whether or not its replay still exists, so a complete parse is kept.
            Require(ReplaySummaryCache.Read(ReplaySummaryCache.CachePath(Path.Combine(root, "timelines"), file)) is { Failed: false, Pulls: [{ Zone: 9504 }] }, "summary of a parsed replay was dropped");
            Require(!logged.Any(l => l.Contains("[AutoTimelines]", StringComparison.Ordinal)), $"deletion reported as a failure: {string.Join(" | ", logged)}");
        }
        finally { Service.LogHandlerDebug = previous; }
    });
}
finally
{
    if (Path.GetFullPath(fixtureRoot).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(fixtureRoot, true);
}

foreach (var failure in failures)
    Console.Error.WriteLine(failure);
Console.WriteLine($"tests={tests} passed={tests - failures.Count} failed={failures.Count}; source=external/synthetic; replay=none");
return failures.Count == 0 ? 0 : 1;
