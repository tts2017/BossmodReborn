using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using BossMod;
using BossMod.Autorotation;
using BossMod.Autorotation.xan;
using BossMod.VPR;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanVPR = BossMod.Autorotation.xan.VPR;

namespace VprRegression;

internal static class Program
{
    private static readonly DateTime BaseTime = new(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);

    public static int Main(string[] args)
    {
        var command = args.FirstOrDefault() ?? "all";
        var options = HarnessOptions.Parse(args.Skip(1).ToArray());
        return command switch
        {
            "emulator" => RunEmulator(),
            "real" => RunReal(options),
            "timeline" => RunTimeline(options),
            "highend" => RunHighEnd(options),
            "all" => RunEmulator() == 0 && RunReal(options) == 0 && RunTimeline(options) == 0 ? RunHighEnd(options) : 3,
            _ => Usage(command)
        };
    }

    private static int RunEmulator()
    {
        var checks = VprRotationEmulator.RunSuite();
        foreach (var check in checks)
            Console.WriteLine($"{check.Name}: {(check.Passed ? "PASS" : "FAIL")} {check.Detail}");
        Console.WriteLine($"emulator_checks={checks.Count}");
        Console.WriteLine($"emulator_failures={checks.Count(c => !c.Passed)}");
        return checks.All(c => c.Passed) ? 0 : 3;
    }

    private static int RunReal(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        var results = RealCases().Select(ExecuteCase).ToList();
        foreach (var result in results)
        {
            Console.WriteLine($"{result.Name}: {(result.Failures.Count == 0 ? "PASS" : "FAIL")} queued={string.Join(',', result.Actions)}");
            foreach (var failure in result.Failures)
                Console.WriteLine($"  {failure}");
        }
        Console.WriteLine($"real_cases={results.Count}");
        Console.WriteLine($"real_failures={results.Sum(r => r.Failures.Count)}");
        RunTargetSelectionMatrix();
        return results.All(r => r.Failures.Count == 0) ? 0 : 3;
    }

    private static void RunTargetSelectionMatrix()
    {
        var samples = new List<double>();
        long allocated = 0;
        using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        var targetFields = new[] { "BestRangedAOETarget", "BestGenerationTarget", "BestLegacyTarget" }
            .Select(name => typeof(XanVPR).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!).ToArray();
        var random = new Random(1732);
        foreach (var targetCount in new[] { 1, 3, 8, 24 })
        foreach (var layout in Enumerable.Range(0, 8))
        {
            var state = new RealCase("target_selection", TargetCount: targetCount, Coil: 2, Offering: 50, InstinctLeft: 40, SwiftscaledLeft: 40);
            var world = BuildWorld(state, out var player, out var target, out var hints);
            using var bossmods = new BossModuleManager(world);
            var database = new RotationDatabase(new DirectoryInfo("tools/vpr_regression/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
            using var manager = new RotationModuleManager(database, bossmods, hints) { CombatStart = world.CurrentTime.AddSeconds(-state.CombatTimer) };
            using var module = new XanVPR(manager, player);
            var enemies = hints.PotentialTargets.ToArray();
            foreach (var targeting in Enum.GetValues<Targeting>())
            foreach (var aoe in Enum.GetValues<AOEStrategy>())
            foreach (var phase in Enumerable.Range(0, 3))
            {
                var strategy = BuildStrategy(state);
                SetTrack(strategy, "Targeting", targeting);
                SetTrack(strategy, "AOE", aoe);
                hints.Clear();
                for (var index = 0; index < enemies.Length; ++index)
                {
                    var enemy = enemies[index];
                    var position = layout == 0 ? new Vector4(2.5f, 0, index * 1.5f, MathF.PI)
                        : new Vector4(random.NextSingle() * 30 - 5, 0, random.NextSingle() * 16 - 8, MathF.PI);
                    world.Execute(new ActorState.OpMove(enemy.Actor.InstanceID, position));
                    enemy.Priority = phase == 1 && index % 3 == 0 ? AIHints.Enemy.PriorityForbidden
                        : phase == 2 && index % 3 == 0 ? AIHints.Enemy.PriorityUndesirable : 1;
                    hints.PotentialTargets.Add(enemy);
                    hints.Enemies[enemy.Actor.CharacterSpawnIndex] = enemy;
                }
                hints.Normalize();
                var primary = layout == 7 ? null : target;
                module.Execute(strategy, primary, 0.05f, false);
                hints.ActionsToExecute.Clear();
                var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                module.Execute(strategy, primary, 0.05f, false);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                allocated += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                samples.Add(elapsed);
                var selected = string.Join(',', targetFields.Select(field => (field.GetValue(module) as AIHints.Enemy)?.Actor.InstanceID ?? 0));
                var queue = string.Join(';', hints.ActionsToExecute.Entries.Select(entry => FormattableString.Invariant($"{entry.Action.Type}:{entry.Action.ID}:{entry.Target?.InstanceID ?? 0}:{entry.Priority:R}:{entry.Delay:R}")));
                digest.AppendData(System.Text.Encoding.UTF8.GetBytes($"{selected}:{module.NumRangedAOETargets}:{queue}\n"));
            }
        }
        samples.Sort();
        Console.WriteLine(FormattableString.Invariant($"vpr_target_cases={samples.Count} hash={Convert.ToHexString(digest.GetHashAndReset())} allocated_bytes={allocated} p50_us={samples[samples.Count / 2]:F3} p95_us={samples[(int)(samples.Count * 0.95)]:F3} p99_us={samples[(int)(samples.Count * 0.99)]:F3}"));
    }

    private static int RunTimeline(HarnessOptions options)
        => RunTimeline(options, TimelineFrames(), "timeline");

    private static int RunHighEnd(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        var caseResults = HighEndCases().Select(ExecuteCase).ToList();
        var timelineResult = RunTimeline(options, HighEndTimelineFrames(), "highend_timeline");
        foreach (var result in caseResults)
        {
            Console.WriteLine($"{result.Name}: {(result.Failures.Count == 0 ? "PASS" : "FAIL")} queued={string.Join(',', result.Actions)}");
            foreach (var failure in result.Failures)
                Console.WriteLine($"  {failure}");
        }
        Console.WriteLine($"highend_cases={caseResults.Count}");
        Console.WriteLine($"highend_case_failures={caseResults.Sum(r => r.Failures.Count)}");
        return timelineResult == 0 && caseResults.All(r => r.Failures.Count == 0) ? 0 : 3;
    }

    private static int RunTimeline(HarnessOptions options, IReadOnlyList<TimelineFrame> frames, string label)
    {
        InitializeBossMod(options.Sqpack);
        var first = frames[0].State;
        var world = BuildWorld(first, out var player, out var target, out var hints);
        using var bossmods = new BossModuleManager(world);
        var db = new RotationDatabase(new DirectoryInfo("tools/vpr_regression/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
        using var manager = new RotationModuleManager(db, bossmods, hints)
        {
            CombatStart = world.CurrentTime.AddSeconds(-first.CombatTimer)
        };
        var module = new XanVPR(manager, player);
        List<RealCaseResult> results = [];

        for (var index = 0; index < frames.Count; ++index)
        {
            var frame = frames[index];
            AdvanceTimelineFrame(world, player, target, hints, frame.State, index + 2);
            module.Execute(BuildStrategy(frame.State), frame.State.HaveTarget ? target : null, estimatedAnimLockDelay: 0.05f, isMoving: false);
            results.Add(ValidateCase(frame.State, hints));
        }

        foreach (var result in results)
        {
            Console.WriteLine($"{result.Name}: {(result.Failures.Count == 0 ? "PASS" : "FAIL")} queued={string.Join(',', result.Actions)}");
            foreach (var failure in result.Failures)
                Console.WriteLine($"  {failure}");
        }
        Console.WriteLine($"{label}_frames={results.Count}");
        Console.WriteLine($"{label}_failures={results.Sum(r => r.Failures.Count)}");
        return results.All(r => r.Failures.Count == 0) ? 0 : 3;
    }

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Usage: dotnet run --project tools/vpr_regression -- [emulator|real|timeline|highend|all] [--sqpack <path>]");
        return 2;
    }

    private static void InitializeBossMod(string? sqpackOverride)
    {
        var sqpackPath = ResolveSqpackPath(sqpackOverride);
        Service.LuminaGameData = new Lumina.GameData(sqpackPath);
        Service.Config.Initialize();
    }

    private static string ResolveSqpackPath(string? sqpackOverride)
    {
        string?[] candidates =
        [
            sqpackOverride,
            Environment.GetEnvironmentVariable("BOSSMOD_SQPACK_PATH"),
            @"C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack",
            @"D:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack",
            @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack",
            @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY XIV Online\game\sqpack"
        ];

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("Could not find FFXIV sqpack. Pass --sqpack or set BOSSMOD_SQPACK_PATH.");
    }

    private static IReadOnlyList<RealCase> RealCases()
        =>
        [
            new("dread_combo_finishes_before_ire", DreadCombo: DreadCombo.Dreadwinder, Coil: 0, Offering: 50, SerpentsIreReadyIn: 0, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.HuntersCoil, AID.SwiftskinsCoil], ForbiddenActions: [AID.SerpentsIre], MinimumPriority: 18),
            new("auto_ire_waits_for_legacy", SerpentCombo: SerpentCombo.FourthLegacy, Coil: 0, Offering: 0, SerpentsIreReadyIn: 0, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.FourthLegacy], ForbiddenActions: [AID.SerpentsIre]),
            new("reawaken_sequence_keeps_generation", Anguine: 3, Coil: 3, Offering: 100, OutOfMeleeRange: true, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.ThirdGeneration], ForbiddenActions: [AID.UncoiledFury, AID.WrithingSnap], MinimumPriority: 30),
            new("second_reawaken_waits_for_follow_up", SerpentCombo: SerpentCombo.FourthLegacy, Offering: 50, InstinctLeft: 40, SwiftscaledLeft: 40, DoubleReawakenGoal: 2, DoubleReawakenStarted: 1, RequiredActions: [AID.FourthLegacy], ForbiddenActions: [AID.Reawaken]),
            new("second_reawaken_starts_after_follow_up", Offering: 50, InstinctLeft: 40, SwiftscaledLeft: 40, DoubleReawakenGoal: 2, DoubleReawakenStarted: 1, RequiredActions: [AID.Reawaken], ForbiddenActions: [AID.UncoiledFury], MinimumPriority: 30),
            new("range_fury_auto", Coil: 1, Offering: 0, OutOfMeleeRange: true, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.UncoiledFury]),
            new("range_fury_off", Coil: 1, Offering: 0, OutOfMeleeRange: true, InstinctLeft: 40, SwiftscaledLeft: 40, UncoiledFuryRange: XanVPR.UncoiledFuryRangeStrategy.Off, ForbiddenActions: [AID.UncoiledFury]),
            new("true_north_does_not_reuse_active_effect", DreadCombo: DreadCombo.Dreadwinder, Coil: 0, Offering: 0, TrueNorthLeft: 8, InstinctLeft: 40, SwiftscaledLeft: 40, ForbiddenActions: [AID.TrueNorth]),
            new("level_sync_single_second_step", Level: 5, ComboLast: AID.SteelFangs, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.HuntersSting], ForbiddenActions: [AID.SwiftskinsSting]),
            new("level_sync_aoe_entry", Level: 25, TargetCount: 3, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.SteelMaw], ForbiddenActions: [AID.HuntersBite, AID.SwiftskinsBite]),
            new("null_target_has_no_queue", HaveTarget: false, ExpectEmpty: true)
        ];

    private static IReadOnlyList<TimelineFrame> TimelineFrames()
        =>
        [
            new(new("timeline_dreadwinder", DreadCombo: DreadCombo.Dreadwinder, Offering: 50, SerpentsIreReadyIn: 0, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.HuntersCoil, AID.SwiftskinsCoil], ForbiddenActions: [AID.SerpentsIre], MinimumPriority: 18)),
            new(new("timeline_second_coil", DreadCombo: DreadCombo.HuntersCoil, Offering: 50, SerpentsIreReadyIn: 0, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.SwiftskinsCoil], ForbiddenActions: [AID.SerpentsIre], MinimumPriority: 18)),
            new(new("timeline_ire_after_dread", Offering: 50, SerpentsIreReadyIn: 0, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.SerpentsIre])),
            new(new("timeline_free_reawaken", Offering: 50, SerpentsIreReadyIn: 120, ReawakenReadyLeft: 20, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.Reawaken], MinimumPriority: 30)),
            new(new("timeline_generation", Offering: 50, SerpentsIreReadyIn: 120, Anguine: 3, ReawakenLeft: 10, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.ThirdGeneration], ForbiddenActions: [AID.UncoiledFury, AID.WrithingSnap], MinimumPriority: 30)),
            new(new("timeline_follow_up_before_second_reawaken", SerpentCombo: SerpentCombo.FourthLegacy, Offering: 50, SerpentsIreReadyIn: 120, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.FourthLegacy], ForbiddenActions: [AID.Reawaken])),
            new(new("timeline_second_reawaken", Offering: 50, SerpentsIreReadyIn: 120, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.Reawaken], ForbiddenActions: [AID.UncoiledFury], MinimumPriority: 30))
        ];

    private static IReadOnlyList<RealCase> HighEndCases()
        =>
        [
            new("highend_aoe_combo_survives_target_drop", ComboLast: AID.SteelMaw, TargetCount: 1, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.HuntersBite, AID.SwiftskinsBite]),
            new("highend_single_combo_survives_target_rise", ComboLast: AID.SteelFangs, TargetCount: 3, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.HuntersSting, AID.SwiftskinsSting]),
            new("highend_dread_finishes_before_ire", DreadCombo: DreadCombo.Dreadwinder, Offering: 50, SerpentsIreReadyIn: 0, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.HuntersCoil, AID.SwiftskinsCoil], ForbiddenActions: [AID.SerpentsIre], MinimumPriority: 18)
        ];

    private static IReadOnlyList<TimelineFrame> HighEndTimelineFrames()
        =>
        [
            new(new("highend_ready_during_target_loss", HaveTarget: false, Offering: 50, SerpentsIreReadyIn: 120, ReawakenReadyLeft: 20, InstinctLeft: 40, SwiftscaledLeft: 40, ExpectEmpty: true)),
            new(new("highend_target_return_reawaken", Offering: 50, SerpentsIreReadyIn: 120, ReawakenReadyLeft: 20, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.Reawaken], MinimumPriority: 30)),
            new(new("highend_forced_range_generation", Offering: 50, SerpentsIreReadyIn: 120, Anguine: 3, ReawakenLeft: 10, OutOfMeleeRange: true, Slither: XanVPR.SlitherStrategy.OpenerAndBurstRecovery, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.ThirdGeneration, AID.Slither], RequiredAll: true, ForbiddenActions: [AID.UncoiledFury, AID.WrithingSnap], MinimumPriority: 30)),
            new(new("highend_follow_up_before_second_reawaken", SerpentCombo: SerpentCombo.FourthLegacy, Offering: 50, SerpentsIreReadyIn: 120, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.FourthLegacy], ForbiddenActions: [AID.Reawaken])),
            new(new("highend_second_reawaken", Offering: 50, SerpentsIreReadyIn: 120, InstinctLeft: 40, SwiftscaledLeft: 40, RequiredActions: [AID.Reawaken], ForbiddenActions: [AID.UncoiledFury], MinimumPriority: 30))
        ];

    private static RealCaseResult ExecuteCase(RealCase testCase)
    {
        try
        {
            var world = BuildWorld(testCase, out var player, out var target, out var hints);
            using var bossmods = new BossModuleManager(world);
            var db = new RotationDatabase(new DirectoryInfo("tools/vpr_regression/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
            using var manager = new RotationModuleManager(db, bossmods, hints)
            {
                CombatStart = world.CurrentTime.AddSeconds(-testCase.CombatTimer)
            };
            var module = new XanVPR(manager, player);
            SetDoubleReawakenState(module, testCase);
            module.Execute(BuildStrategy(testCase), testCase.HaveTarget ? target : null, estimatedAnimLockDelay: 0.05f, isMoving: false);
            return ValidateCase(testCase, hints);
        }
        catch (Exception ex)
        {
            return new(testCase.Name, [], [ex.ToString()]);
        }
    }

    private static RealCaseResult ValidateCase(RealCase testCase, AIHints hints)
    {
        var queued = hints.ActionsToExecute.Entries
            .Select(entry => (AID)entry.Action.ID)
            .ToList();
        List<string> failures = [];
        if (testCase.ExpectEmpty && queued.Count > 0)
            failures.Add($"expected an empty queue, got: {string.Join(',', queued)}");
        if (testCase.Required.Count > 0)
        {
            if (testCase.RequiredAll)
            {
                var missing = testCase.Required.Where(action => !queued.Contains(action)).ToList();
                if (missing.Count > 0)
                    failures.Add($"missing required actions: {string.Join(',', missing)}");
            }
            else if (!queued.Any(action => testCase.Required.Contains(action)))
            {
                failures.Add($"missing required action: {string.Join(" or ", testCase.Required)}");
            }
        }
        foreach (var forbidden in testCase.Forbidden)
        {
            if (queued.Contains(forbidden))
                failures.Add($"forbidden action queued: {forbidden}");
        }
        if (testCase.MinimumPriority is { } minimum && testCase.Required.Count > 0)
        {
            var matching = hints.ActionsToExecute.Entries
                .Where(entry => testCase.Required.Contains((AID)entry.Action.ID))
                .Select(entry => entry.Priority)
                .DefaultIfEmpty(float.MinValue)
                .Max();
            if (matching < minimum)
                failures.Add($"priority {matching:f1} is below required {minimum:f1}");
        }
        return new(testCase.Name, queued, failures);
    }

    private static WorldState BuildWorld(RealCase testCase, out Actor player, out Actor target, out AIHints hints)
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "vpr-real-harness");
        var now = BaseTime.AddSeconds(testCase.CombatTimer);
        world.Execute(new WorldState.OpFrameStart(new(now, 1, 1, 1.0f / 60.0f, 1.0f / 60.0f, 1), default, BuildViperGauge(testCase), default));
        world.Execute(new WorldState.OpZoneChange(0, 0));

        const ulong playerID = 0x10000001;
        const ulong targetID = 0x40000001;
        var targetDistance = testCase.OutOfMeleeRange ? 12.0f : 2.5f;
        world.Execute(new ActorState.OpCreate(playerID, 0, 0, 0, "Player", 0, ActorType.Player, Class.VPR, testCase.Level, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
        world.Execute(new ActorState.OpCreate(targetID, 0x1234, 2, 0, "Target", 0, ActorType.Enemy, Class.None, 100, new Vector4(targetDistance, 0, 0, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), true, false, default, default, 0));
        world.Execute(new ActorState.OpCombat(playerID, true));
        world.Execute(new ActorState.OpCombat(targetID, true));
        world.Execute(new ClientState.OpPlayerStatsChange(new(400, 400, 0)));
        world.Execute(new ClientState.OpClassJobLevelsChange(BuildClassJobLevels(testCase.Level)));
        world.Execute(new ClientState.OpComboChange(new((uint)testCase.ComboLast, 14.5f)));
        world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(testCase)));

        player = world.Actors.Find(playerID)!;
        target = world.Actors.Find(targetID)!;
        ApplyStatuses(world, player, testCase);

        hints = new AIHints();
        if (testCase.HaveTarget)
        {
            AddPotentialTarget(hints, target);
            for (var index = 1; index < testCase.TargetCount; ++index)
            {
                var instanceID = targetID + (ulong)index;
                var spawnIndex = 2 + index * 2;
                world.Execute(new ActorState.OpCreate(instanceID, 0x1234 + (uint)index, spawnIndex, 0, $"Target{index}", 0, ActorType.Enemy, Class.None, 100, new Vector4(targetDistance, 0, index * 1.5f, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), true, false, default, default, 0));
                world.Execute(new ActorState.OpCombat(instanceID, true));
                AddPotentialTarget(hints, world.Actors.Find(instanceID)!);
            }
            hints.HighestPotentialTargetPriority = 1;
        }
        return world;
    }

    private static void AddPotentialTarget(AIHints hints, Actor target)
    {
        var enemy = new AIHints.Enemy(target, 1, false);
        hints.Enemies[target.CharacterSpawnIndex] = enemy;
        hints.PotentialTargets.Add(enemy);
    }

    private static void AdvanceTimelineFrame(WorldState world, Actor player, Actor target, AIHints hints, RealCase state, int frame)
    {
        var timestamp = BaseTime.AddSeconds(state.CombatTimer + frame * 2.5f);
        world.Execute(new WorldState.OpFrameStart(new(timestamp, (ulong)frame, (uint)frame, 2.5f, 2.5f, 1), TimeSpan.FromSeconds(2.5f), BuildViperGauge(state), default));
        world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(state)));
        var targetDistance = state.OutOfMeleeRange ? 12.0f : 2.5f;
        world.Execute(new ActorState.OpMove(target.InstanceID, new Vector4(targetDistance, 0, 0, MathF.PI)));
        world.Execute(new ActorState.OpTargetable(target.InstanceID, state.HaveTarget));
        ClearStatuses(world, player);
        ApplyStatuses(world, player, state);
        hints.Clear();
        if (state.HaveTarget)
        {
            AddPotentialTarget(hints, target);
            hints.HighestPotentialTargetPriority = 1;
        }
    }

    private static void ClearStatuses(WorldState world, Actor actor)
    {
        for (var index = 0; index < Actor.NumStatuses; ++index)
        {
            if (actor.Statuses[index].ID != 0)
                world.Execute(new ActorState.OpStatus(actor.InstanceID, index, default));
        }
    }

    private static short[] BuildClassJobLevels(int level)
    {
        var levels = new short[ClientState.NumClassLevels];
        Array.Fill(levels, (short)level);
        return levels;
    }

    private static List<(int, Cooldown)> BuildCooldowns(RealCase testCase)
    {
        List<(int, Cooldown)> cooldowns = [(ActionDefinitions.GCDGroup, new Cooldown(2.5f, 2.5f))];
        SetCooldown(cooldowns, AID.Vicewinder, testCase.VicewinderReadyIn);
        SetCooldown(cooldowns, AID.Vicepit, testCase.VicewinderReadyIn);
        SetCooldown(cooldowns, AID.SerpentsIre, testCase.SerpentsIreReadyIn);
        SetCooldown(cooldowns, AID.Slither, 0);
        SetCooldown(cooldowns, AID.TrueNorth, testCase.TrueNorthReadyIn);
        return cooldowns;
    }

    private static void SetCooldown<T>(List<(int, Cooldown)> cooldowns, T action, float readyIn) where T : Enum
    {
        var definition = ActionDefinitions.Instance.Spell(action);
        if (definition == null || definition.MainCooldownGroup < 0)
            return;

        var total = Math.Max(definition.Cooldown * Math.Max(1, definition.MaxChargesAtCap()), 1f);
        var elapsed = Math.Clamp(total - readyIn, 0, total);
        cooldowns.RemoveAll(entry => entry.Item1 == definition.MainCooldownGroup);
        cooldowns.Add((definition.MainCooldownGroup, new(elapsed, total)));
    }

    private static void ApplyStatuses(WorldState world, Actor player, RealCase testCase)
    {
        var index = 0;
        void Status(SID status, float left)
        {
            if (left > 0 && index < Actor.NumStatuses)
                world.Execute(new ActorState.OpStatus(player.InstanceID, index++, new((uint)status, 0, world.FutureTime(left), player.InstanceID)));
        }

        Status(SID.HuntersInstinct, testCase.InstinctLeft);
        Status(SID.Swiftscaled, testCase.SwiftscaledLeft);
        Status(SID.ReawakenReady, testCase.ReawakenReadyLeft);
        Status(SID.Reawakened, testCase.ReawakenLeft);
        Status(SID.TrueNorth, testCase.TrueNorthLeft);
        Status(SID.PoisedForTwinfang, testCase.PoisedForTwinfangLeft);
        Status(SID.PoisedForTwinblood, testCase.PoisedForTwinbloodLeft);
    }

    private static StrategyValues BuildStrategy(RealCase testCase)
    {
        var strategy = new StrategyValues(XanVPR.Definition().Configs);
        SetTrack(strategy, "Targeting", Targeting.Auto);
        SetTrack(strategy, "AOE", AOEStrategy.AOE);
        SetTrack(strategy, "Buffs", OffensiveStrategy.Automatic);
        SetTrack(strategy, "OpenerBurst", XanVPR.OpenerBurstStrategy.Standard);
        SetTrack(strategy, "SerpentsIre", testCase.SerpentsIre);
        SetTrack(strategy, "Potion", XanVPR.PotionStrategy.Off);
        SetTrack(strategy, "WrithingSnap", XanVPR.SnapStrategy.None);
        SetTrack(strategy, "UncoiledFuryRange", testCase.UncoiledFuryRange);
        SetTrack(strategy, "Slither", testCase.Slither);
        SetTrack(strategy, "TrueNorth", testCase.TrueNorth);
        return strategy;
    }

    private static void SetDoubleReawakenState(XanVPR module, RealCase testCase)
    {
        if (testCase.DoubleReawakenGoal <= 0)
            return;

        var type = typeof(XanVPR);
        type.GetField("_serpentsIreReawakenGoal", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(module, testCase.DoubleReawakenGoal);
        type.GetField("_serpentsIreReawakenStarted", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(module, testCase.DoubleReawakenStarted);
    }

    private static void SetTrack<T>(StrategyValues strategy, string internalName, T option) where T : struct, Enum
    {
        for (var index = 0; index < strategy.Configs.Count; ++index)
        {
            if (strategy.Configs[index] is StrategyConfigTrack track && track.InternalName == internalName)
            {
                ((StrategyValueTrack)strategy.Values[index]).Option = Convert.ToInt32(option);
                return;
            }
        }

        throw new InvalidOperationException($"Missing strategy track '{internalName}'.");
    }

    private static unsafe ClientState.Gauge BuildViperGauge(RealCase testCase)
    {
        ViperGauge gauge = default;
        gauge.DreadCombo = testCase.DreadCombo;
        gauge.RattlingCoilStacks = (byte)Math.Clamp(testCase.Coil, 0, 3);
        gauge.SerpentOffering = (byte)Math.Clamp(testCase.Offering, 0, 100);
        gauge.AnguineTribute = (byte)Math.Clamp(testCase.Anguine, 0, 5);
        gauge.SerpentComboState = (byte)((byte)testCase.SerpentCombo * 4);
        var raw = (ulong*)Unsafe.AsPointer(ref gauge);
        var low = sizeof(ViperGauge) > 8 ? raw[1] : 0;
        var high = sizeof(ViperGauge) > 16 ? raw[2] : 0;
        return new(low, high);
    }

    private sealed record HarnessOptions(string? Sqpack)
    {
        public static HarnessOptions Parse(string[] args)
        {
            string? sqpack = null;
            for (var index = 0; index < args.Length; ++index)
            {
                if (args[index] != "--sqpack" || index + 1 >= args.Length)
                    throw new ArgumentException($"Unknown or incomplete option '{args[index]}'.");
                sqpack = args[++index];
            }
            return new(sqpack);
        }
    }

    private sealed record RealCase(
        string Name,
        int Level = 100,
        int TargetCount = 1,
        bool HaveTarget = true,
        DreadCombo DreadCombo = (DreadCombo)0,
        SerpentCombo SerpentCombo = (SerpentCombo)0,
        AID ComboLast = AID.None,
        int Coil = 0,
        int Offering = 0,
        int Anguine = 0,
        bool OutOfMeleeRange = false,
        float CombatTimer = 120,
        float InstinctLeft = 0,
        float SwiftscaledLeft = 0,
        float ReawakenReadyLeft = 0,
        float ReawakenLeft = 0,
        float TrueNorthLeft = 0,
        float TrueNorthReadyIn = 0,
        float SerpentsIreReadyIn = 120,
        float VicewinderReadyIn = 40,
        float PoisedForTwinfangLeft = 0,
        float PoisedForTwinbloodLeft = 0,
        int DoubleReawakenGoal = 0,
        int DoubleReawakenStarted = 0,
        XanVPR.SerpentsIreStrategy SerpentsIre = XanVPR.SerpentsIreStrategy.Auto,
        XanVPR.UncoiledFuryRangeStrategy UncoiledFuryRange = XanVPR.UncoiledFuryRangeStrategy.Auto,
        XanVPR.SlitherStrategy Slither = XanVPR.SlitherStrategy.Off,
        XanVPR.TrueNorthStrategy TrueNorth = XanVPR.TrueNorthStrategy.Auto,
        bool ExpectEmpty = false,
        IReadOnlyList<AID>? RequiredActions = null,
        IReadOnlyList<AID>? ForbiddenActions = null,
        bool RequiredAll = false,
        float? MinimumPriority = null)
    {
        public IReadOnlyList<AID> Required { get; init; } = RequiredActions ?? [];
        public IReadOnlyList<AID> Forbidden { get; init; } = ForbiddenActions ?? [];
    }

    private sealed record RealCaseResult(string Name, IReadOnlyList<AID> Actions, IReadOnlyList<string> Failures);

    private sealed record TimelineFrame(RealCase State);
}
