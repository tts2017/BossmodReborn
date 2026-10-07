using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using BossMod;
using BossMod.Autorotation;
using BossMod.Autorotation.xan;
using BossMod.MCH;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanMCH = BossMod.Autorotation.xan.MCH;

namespace MchRealHarness;

internal static class Program
{
    private const int DefaultPatternCount = 20_000;
    private static readonly DateTime BaseTime = new(2026, 7, 7, 0, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Main(string[] args)
    {
        try
        {
            var command = args.Length > 0 ? args[0] : "probe";
            var options = HarnessOptions.Parse(args.Length > 0 ? args[1..] : []);
            return command switch
            {
                "probe" => Probe(options),
                "count" => Count(options),
                "run" => Run(options),
                _ => Usage(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int Probe(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        var result = ExecutePattern(GeneratedPattern(0), sampleIndex: 0);
        Console.WriteLine($"pattern={result.PatternName}");
        Console.WriteLine($"queued={result.Actions.Count}");
        foreach (var action in result.Actions)
            Console.WriteLine($"{action.Action} target={action.Target:X} priority={action.Priority:f1} delay={action.Delay:f2}");
        if (result.Exception != null)
            Console.WriteLine(result.Exception);
        return result.Exception == null ? 0 : 1;
    }

    private static int Count(HarnessOptions options)
    {
        var count = options.Limit ?? DefaultPatternCount;
        Console.WriteLine($"real_mch_patterns={count}");
        return 0;
    }

    private static int Run(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);

        var limit = options.Limit ?? DefaultPatternCount;
        var results = new RealHarnessResult(limit);
        results.Failures += RunPlannerChecks();
        for (var i = 0; i < limit; ++i)
        {
            var pattern = GeneratedPattern(i);
            var result = ExecutePattern(pattern, sampleIndex: i);
            results.Results.Add(result);
            if (result.Exception != null)
                ++results.Failures;
            else if (result.Actions.Count == 0)
            {
                if (ExpectedEmptyQueue(pattern))
                    ++results.ExpectedEmptyQueues;
                else
                    ++results.EmptyQueues;
            }
        }

        if (!string.IsNullOrWhiteSpace(options.Out))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Out))!);
            File.WriteAllText(options.Out, JsonSerializer.Serialize(results, JsonOptions));
        }

        Console.WriteLine($"patterns={results.Patterns}");
        Console.WriteLine($"failures={results.Failures}");
        Console.WriteLine($"empty_queues={results.EmptyQueues}");
        Console.WriteLine($"expected_empty_queues={results.ExpectedEmptyQueues}");
        if (!string.IsNullOrWhiteSpace(options.Out))
            Console.WriteLine($"out={options.Out}");

        return results.Failures == 0 ? 0 : 3;
    }

    private static int RunPlannerChecks()
    {
        var checks = 0;
        var failures = 0;
        void Check(bool condition, string message)
        {
            ++checks;
            if (!condition)
            {
                ++failures;
                Console.WriteLine($"MCH planner: {message}");
            }
        }
        var pattern = GeneratedPattern(1) with
        {
            Level = 100, CombatTimer = 60, ContentId = 0, TargetCount = 1, TargetPriority = 1,
            InCombat = true, HaveTarget = true, Targetable = true, LookAway = false,
            Heat = 0, Battery = 0, ComboLast = AID.None, OverheatLeft = 0, HyperchargedLeft = 0,
            ReassembleLeft = 0, WildfireLeft = 0, ExcavatorLeft = 0, FullMetalFieldLeft = 0,
            DrillReadyIn = 0, DrillCharges = 2, AirAnchorReadyIn = 0, ChainSawReadyIn = 60,
            WildfireReadyIn = 60, BarrelReadyIn = 60,
        };
        var world = BuildWorld(pattern, 0, out var player, out var target, out var hints);
        hints.Normalize();
        var queue = new ActionQueue();
        ActionQueue.Entry Select(ActionQueue.Entry baseline)
            => MchRealtimeValuePlanner.Select(queue, baseline, world, player, world.Client.Cooldowns, 0, hints, 0.05f, false);
        foreach (var speed in new[] { 420, 2500 })
        foreach (var haste in new[] { 100, 85, 60 })
        foreach (var remaining in new[] { 0f, 0.5f, 1.5f })
        foreach (var priorityLoss in new[] { 0, 1, 2, 3 })
        {
            world.Execute(new ClientState.OpPlayerStatsChange(new(speed, 400, haste)));
            world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, remaining))]));
            queue.Clear();
            queue.Push(ActionID.MakeSpell(AID.AirAnchor), target, 4010);
            queue.Push(ActionID.MakeSpell(AID.Drill), target, 4010 - priorityLoss);
            var baseline = queue.Entries[0];
            // Both routes execute one Drill, one Air Anchor, and one combo starter.
            // Only cooldown progress and the explicit priority penalty differ.
            var gcd = ActionSpeed.GCDRounded(speed, haste, player.Level);
            var expectedGain = gcd * 660f * 0.35f * (1f / 20f - 1f / 40f) - priorityLoss * 4;
            var expected = expectedGain > 5 ? AID.Drill : AID.AirAnchor;
            Check(Select(baseline).Action == ActionID.MakeSpell(expected), $"GCD={gcd:F2}, remaining={remaining}, priority_loss={priorityLoss}, expected={expected}");
        }

        world.Execute(new ClientState.OpPlayerStatsChange(new(420, 400, 100)));
        world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, default)]));
        foreach (var mode in new[] { "manual", "forced", "expiring" })
        {
            queue.Clear();
            queue.Push(ActionID.MakeSpell(AID.AirAnchor), target, 4010, expire: mode == "expiring" ? 1 : float.MaxValue, manual: mode == "manual", forced: mode == "forced");
            queue.Push(ActionID.MakeSpell(AID.Drill), target, 4010);
            Check(Select(queue.Entries[0]).Action == ActionID.MakeSpell(AID.AirAnchor), $"preserve {mode} baseline");
        }

        queue.Clear();
        queue.Push(ActionID.MakeSpell(AID.AirAnchor), target, 4010);
        queue.Push(ActionID.MakeSpell(AID.Drill), target, 4010);
        foreach (var status in new[] { SID.Reassembled, SID.WildfirePlayer, SID.Hypercharged, SID.ExcavatorReady, SID.FullMetalMachinist })
        {
            world.Execute(new ActorState.OpStatus(player.InstanceID, 0, new((uint)status, 0, world.FutureTime(10), player.InstanceID)));
            Check(Select(queue.Entries[0]).Action == ActionID.MakeSpell(AID.AirAnchor), $"preserve protected {status} state");
        }
        world.Execute(new ActorState.OpStatus(player.InstanceID, 0, default));
        foreach (var action in new[] { AID.Wildfire, AID.Hypercharge, AID.BarrelStabilizer, AID.AutomatonQueen, AID.Excavator, AID.FullMetalField })
        {
            queue.Push(ActionID.MakeSpell(action), player, 2000);
            Check(Select(queue.Entries[0]).Action == ActionID.MakeSpell(AID.AirAnchor), $"preserve protected {action} queue");
            queue.Entries.RemoveAt(queue.Entries.Count - 1);
        }

        const ulong distantID = 0x400000FF;
        world.Execute(new ActorState.OpCreate(distantID, 0x1235, 10, 0, "Distant", 0, ActorType.Enemy, Class.None, 100, new Vector4(100, 0, 100, 0), 2, new(1000000, 1000000, 0, 10000, 10000), true, false, default, default, 0));
        var distant = new AIHints.Enemy(world.Actors.Find(distantID)!, 1, false);
        var chainSawGroup = ActionDefinitions.Instance.Spell(AID.ChainSaw)!.MainCooldownGroup;
        foreach (var readyIn in new[] { 0f, 1f, 3f, 5f, 10f })
        {
            world.Execute(new ClientState.OpCooldown(false, [(chainSawGroup, new(0, readyIn))]));
            hints.PotentialTargets.Remove(distant);
            hints.Normalize();
            var expected = Select(queue.Entries[0]).Action;
            hints.PotentialTargets.Add(distant);
            hints.Normalize();
            Check(Select(queue.Entries[0]).Action == expected, $"distant enemy must not change the AoE value, Chain Saw in {readyIn}");
        }
        Console.WriteLine($"mch_planner_checks={checks} failures={failures}");
        return failures;
    }

    private static bool ExpectedEmptyQueue(RealMchPattern pattern)
        => !pattern.HaveTarget || !pattern.Targetable || pattern.TargetPriority < 0;

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  dotnet run --project tools\\mch_real_harness -- probe [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\mch_real_harness -- count [--limit 20000]");
        Console.Error.WriteLine("  dotnet run --project tools\\mch_real_harness -- run [--limit 20000] [--out <path>] [--sqpack <path>]");
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
        var candidates = new[]
        {
            sqpackOverride,
            Environment.GetEnvironmentVariable("BOSSMOD_SQPACK_PATH"),
            @"C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack",
            @"D:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack",
            @"C:\Program Files (x86)\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack",
            @"C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY XIV Online\game\sqpack"
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return candidate;
        }

        throw new DirectoryNotFoundException("Could not find FFXIV sqpack. Pass --sqpack or set BOSSMOD_SQPACK_PATH.");
    }

    private static RealMchExecutionResult ExecutePattern(RealMchPattern pattern, int sampleIndex)
    {
        try
        {
            var world = BuildWorld(pattern, sampleIndex, out var player, out var target, out var hints);
            using var bossmods = new BossModuleManager(world);
            var db = new RotationDatabase(new DirectoryInfo("tools/mch_real_harness/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
            using var manager = new RotationModuleManager(db, bossmods, hints)
            {
                CombatStart = world.CurrentTime.AddSeconds(-pattern.CombatTimer)
            };

            var module = new XanMCH(manager, player);
            var strategy = BuildStrategy(pattern);
            module.Execute(strategy, pattern.HaveTarget ? target : null, pattern.AnimationLockDelay, pattern.IsMoving);

            return new(pattern.Name, [.. hints.ActionsToExecute.Entries.Select(e => new QueuedActionRecord(e.Action.ToString(), e.Target?.InstanceID ?? 0, e.Priority, e.Delay))], null);
        }
        catch (Exception ex)
        {
            return new(pattern.Name, [], ex.ToString());
        }
    }

    private static WorldState BuildWorld(RealMchPattern pattern, int sampleIndex, out Actor player, out Actor target, out AIHints hints)
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "mch-real-harness");
        var now = BaseTime.AddSeconds(pattern.CombatTimer);
        world.Execute(new WorldState.OpFrameStart(new(now, (ulong)sampleIndex, (uint)sampleIndex, 1.0f / 60.0f, 1.0f / 60.0f, 1), default, BuildMachinistGauge(pattern), default));
        world.Execute(new WorldState.OpZoneChange(0, (ushort)pattern.ContentId));

        var playerID = 0x10000001ul;
        var targetID = 0x40000001ul;
        var targetDistance = pattern.CanHitCone ? 8.0f : 18.0f;
        world.Execute(new ActorState.OpCreate(playerID, 0, 0, 0, "Player", 0, ActorType.Player, Class.MCH, pattern.Level, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
        world.Execute(new ActorState.OpCreate(targetID, 0x1234, 2, 0, "Target", 0, ActorType.Enemy, Class.None, 100, new Vector4(targetDistance, 0, 0, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), pattern.Targetable, false, default, default, 0));
        world.Execute(new ActorState.OpCombat(playerID, pattern.InCombat));
        world.Execute(new ActorState.OpCombat(targetID, pattern.InCombat));
        world.Execute(new ClientState.OpPlayerStatsChange(new(pattern.SkillSpeed, 400, 0)));
        world.Execute(new ClientState.OpClassJobLevelsChange(BuildClassJobLevels(pattern.Level)));
        world.Execute(new ClientState.OpComboChange(new((uint)pattern.ComboLast, 14.5f)));
        world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));

        player = world.Actors.Find(playerID)!;
        target = world.Actors.Find(targetID)!;
        ApplyPlayerStatuses(world, player, pattern);
        ApplyTargetStatuses(world, target, player, pattern);

        hints = new AIHints();
        if (pattern.HaveTarget)
        {
            var enemy = new AIHints.Enemy(target, pattern.TargetPriority, false);
            if ((uint)target.CharacterSpawnIndex < AIHints.NumEnemies)
                hints.Enemies[target.CharacterSpawnIndex] = enemy;
            hints.PotentialTargets.Add(enemy);
        }

        for (var i = 1; i < pattern.TargetCount; ++i)
        {
            var id = 0x40000001ul + (ulong)i;
            var spawnIndex = 2 + i * 2;
            var angle = i % 2 == 0 ? 0.28f : -0.28f;
            var distance = pattern.CanHitCone ? 8.0f + i % 3 : 18.0f + i % 4;
            world.Execute(new ActorState.OpCreate(id, (uint)(0x1234 + i), spawnIndex, 0, "Target" + i, 0, ActorType.Enemy, Class.None, 100, new Vector4(MathF.Cos(angle) * distance, 0, MathF.Sin(angle) * distance, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), pattern.Targetable, false, default, default, 0));
            var add = world.Actors.Find(id)!;
            var enemy = new AIHints.Enemy(add, pattern.TargetPriority, false);
            if ((uint)add.CharacterSpawnIndex < AIHints.NumEnemies)
                hints.Enemies[add.CharacterSpawnIndex] = enemy;
            hints.PotentialTargets.Add(enemy);
        }

        if (pattern.LookAway)
            hints.ForbiddenDirections.Add((player.AngleTo(target), 180.Degrees(), default));

        return world;
    }

    private static short[] BuildClassJobLevels(int level)
    {
        var levels = new short[ClientState.NumClassLevels];
        Array.Fill(levels, (short)level);
        return levels;
    }

    private static List<(int, Cooldown)> BuildCooldowns(RealMchPattern pattern)
    {
        List<(int, Cooldown)> cooldowns = [];
        SetCooldown(cooldowns, AID.Reassemble, pattern.ReassembleReadyIn, pattern.ReassembleCharges >= 2 ? null : pattern.ReassembleCharges == 1 ? 55.0f : 0.0f);
        SetCooldown(cooldowns, AID.GaussRound, pattern.GaussReadyIn, pattern.GaussCharges >= MaxChargeCount(AID.GaussRound, pattern.Level) ? null : pattern.GaussCharges == 2 ? 20.0f : pattern.GaussCharges == 1 ? 10.0f : 0.0f);
        SetCooldown(cooldowns, AID.Ricochet, pattern.RicochetReadyIn, pattern.RicochetCharges >= MaxChargeCount(AID.Ricochet, pattern.Level) ? null : pattern.RicochetCharges == 2 ? 20.0f : pattern.RicochetCharges == 1 ? 10.0f : 0.0f);
        SetCooldown(cooldowns, AID.Hypercharge, pattern.HyperchargeReadyIn);
        SetCooldown(cooldowns, AID.Wildfire, pattern.WildfireReadyIn);
        SetCooldown(cooldowns, AID.Drill, pattern.DrillReadyIn, pattern.DrillCharges >= MaxChargeCount(AID.Drill, pattern.Level) ? null : pattern.DrillCharges == 1 ? 10.0f : 0.0f);
        SetCooldown(cooldowns, AID.Bioblaster, pattern.DrillReadyIn, pattern.DrillCharges >= MaxChargeCount(AID.Bioblaster, pattern.Level) ? null : pattern.DrillCharges == 1 ? 10.0f : 0.0f);
        SetCooldown(cooldowns, AID.AirAnchor, pattern.AirAnchorReadyIn);
        SetCooldown(cooldowns, AID.HotShot, pattern.AirAnchorReadyIn);
        SetCooldown(cooldowns, AID.ChainSaw, pattern.ChainSawReadyIn);
        SetCooldown(cooldowns, AID.BarrelStabilizer, pattern.BarrelReadyIn);
        SetCooldown(cooldowns, AID.Flamethrower, pattern.FlamethrowerReadyIn);
        SetCooldown(cooldowns, AID.Tactician, pattern.TacticianReadyIn);
        SetCooldown(cooldowns, AID.Dismantle, pattern.DismantleReadyIn);
        SetCooldown(cooldowns, AID.RookAutoturret, pattern.QueenReadyIn);
        SetCooldown(cooldowns, AID.AutomatonQueen, pattern.QueenReadyIn);
        return cooldowns;
    }

    private static int MaxChargeCount(AID action, int level)
    {
        var definition = ActionDefinitions.Instance.Spell(action);
        return definition?.MaxChargesAtLevel(level) ?? 1;
    }

    private static void SetCooldown<T>(List<(int, Cooldown)> cooldowns, T aid, double readyIn, float? chargeElapsedOverride = null) where T : Enum
    {
        var definition = ActionDefinitions.Instance.Spell(aid);
        if (definition == null || definition.MainCooldownGroup < 0)
            return;

        var total = Math.Max(definition.Cooldown, 1f);
        var elapsed = chargeElapsedOverride ?? Math.Clamp(total - (float)readyIn, 0, total);
        cooldowns.Add((definition.MainCooldownGroup, new(elapsed, total)));
    }

    private static void ApplyPlayerStatuses(WorldState world, Actor player, RealMchPattern pattern)
    {
        var index = 0;
        void Status(SID sid, double left, ushort extra = 0)
        {
            if (left > 0 && index < Actor.NumStatuses)
                world.Execute(new ActorState.OpStatus(player.InstanceID, index++, new((uint)sid, extra, world.FutureTime(left), player.InstanceID)));
        }

        Status(SID.Reassembled, pattern.ReassembleLeft);
        Status(SID.Overheated, pattern.OverheatLeft, (ushort)pattern.OverheatStacks);
        Status(SID.WildfirePlayer, pattern.WildfireLeft);
        Status(SID.Hypercharged, pattern.HyperchargedLeft);
        Status(SID.ExcavatorReady, pattern.ExcavatorLeft);
        Status(SID.FullMetalMachinist, pattern.FullMetalFieldLeft);
        Status(SID.Flamethrower, pattern.FlamethrowerLeft);
        Status(SID.Tactician, pattern.TacticianLeft);
        if (pattern.PotionLeft > 0)
            world.Execute(new ActorState.OpStatus(player.InstanceID, index++, new(49, (ushort)PotionType.Dexterity, world.FutureTime(pattern.PotionLeft), player.InstanceID)));
    }

    private static void ApplyTargetStatuses(WorldState world, Actor target, Actor player, RealMchPattern pattern)
    {
        var index = 0;
        void Status(SID sid, double left)
        {
            if (left > 0 && index < Actor.NumStatuses)
                world.Execute(new ActorState.OpStatus(target.InstanceID, index++, new((uint)sid, 0, world.FutureTime(left), player.InstanceID)));
        }

        Status(SID.WildfireTarget, pattern.TargetWildfireLeft);
        Status(SID.Dismantled, pattern.DismantledLeft);
        Status(SID.Bioblaster, pattern.BioblasterDotLeft);
    }

    private static StrategyValues BuildStrategy(RealMchPattern pattern)
    {
        var definition = XanMCH.Definition();
        var strategy = new StrategyValues(definition.Configs);
        SetTrack(strategy, "Targeting", Targeting.Auto);
        SetTrack(strategy, "AOE", pattern.TargetCount >= 3 ? AOEStrategy.AOE : AOEStrategy.ST);
        SetTrack(strategy, "Queen", pattern.QueenStrategy);
        SetTrack(strategy, "WF", pattern.WildfireStrategy);
        SetTrack(strategy, "Potion", pattern.PotionStrategy);
        SetTrack(strategy, "Buffs", MapOffensive(pattern.BuffsStrategy));
        SetTrack(strategy, "Hypercharge", MapOffensive(pattern.HyperchargeStrategy));
        SetTrack(strategy, "Tools", pattern.ToolStrategy);
        return strategy;
    }

    private static OffensiveStrategy MapOffensive(AutoForceDelayMode mode)
        => mode switch
        {
            AutoForceDelayMode.Force => OffensiveStrategy.Force,
            AutoForceDelayMode.Delay => OffensiveStrategy.Delay,
            _ => OffensiveStrategy.Automatic
        };

    private static void SetTrack<T>(StrategyValues strategy, string internalName, T option) where T : struct, Enum
    {
        for (var i = 0; i < strategy.Configs.Count; ++i)
        {
            if (strategy.Configs[i] is StrategyConfigTrack track && track.InternalName == internalName)
            {
                ((StrategyValueTrack)strategy.Values[i]).Option = Convert.ToInt32(option);
                return;
            }
        }
    }

    private static RealMchPattern GeneratedPattern(int index)
    {
        var levels = new[] { 30, 40, 45, 52, 58, 66, 68, 72, 76, 80, 82, 90, 92, 96, 100 };
        var targetCounts = new[] { 1, 1, 2, 3, 4, 5, 6, 7, 8 };
        var heatValues = new[] { 0, 20, 40, 50, 70, 90, 100 };
        var batteryValues = new[] { 0, 30, 50, 60, 70, 80, 90, 100 };
        var combos = new[] { AID.None, AID.SplitShot, AID.SlugShot, AID.HeatedSplitShot, AID.HeatedSlugShot };
        var queenModes = Enum.GetValues<XanMCH.QueenStrategy>();
        var wildfireModes = Enum.GetValues<XanMCH.WildfireStrategy>();
        var potionModes = Enum.GetValues<XanMCH.MCHPotionStrategy>();
        var toolModes = Enum.GetValues<XanMCH.ToolStrategy>();
        var forceDelayModes = Enum.GetValues<AutoForceDelayMode>();
        var targetPriorities = new[] { 1, 2, 3 };
        var targetCount = Pick(targetCounts, index, 7);
        var level = Pick(levels, index, 3);
        var overheat = index % 17 == 0 && level >= 35;
        var hypercharged = index % 19 == 0 && level >= 66;
        var dancingMad = index % 37 == 0;
        var targetable = true;
        var haveTarget = true;

        return new(
            Name: $"real_mch_{index:00000}",
            CombatTimer: index % 23 == 0 ? index % 30 : (index * 7) % 720,
            Level: level,
            ContentId: dancingMad ? 1094 : 0,
            InCombat: true,
            TargetCount: targetCount,
            Targetable: targetable,
            HaveTarget: haveTarget,
            CanHitCone: index % 13 != 0,
            LookAway: index % 41 == 0,
            IsMoving: index % 43 == 0,
            TargetPriority: Pick(targetPriorities, index, 11),
            SkillSpeed: Pick(new[] { 380, 400, 450, 520 }, index, 5),
            Heat: Pick(heatValues, index, 13),
            Battery: Pick(batteryValues, index, 17),
            OverheatLeft: overheat ? 3.0 + index % 6 : 0,
            OverheatStacks: overheat ? 1 + index % 5 : 0,
            HyperchargedLeft: hypercharged ? 25 - index % 20 : 0,
            ReassembleLeft: index % 23 == 0 && level >= 10 ? 5 + index % 10 : 0,
            WildfireLeft: index % 31 == 0 && level >= 45 ? 5 + index % 8 : 0,
            ExcavatorLeft: index % 29 == 0 && level >= 96 ? 12 + index % 15 : 0,
            FullMetalFieldLeft: index % 31 == 0 && level >= 100 ? 8 + index % 18 : 0,
            FlamethrowerLeft: index % 113 == 0 && level >= 70 ? 6 : 0,
            TacticianLeft: index % 127 == 0 && level >= 56 ? 10 : 0,
            PotionLeft: index % 131 == 0 ? 15 : 0,
            TargetWildfireLeft: index % 31 == 0 && level >= 45 ? 5 + index % 8 : 0,
            DismantledLeft: index % 137 == 0 && level >= 62 ? 8 : 0,
            BioblasterDotLeft: index % 53 == 0 && level >= 72 ? 10 : 0,
            ComboLast: Pick(combos, index, 19),
            ReassembleCharges: index % 7 == 0 && level >= 84 ? 2 : index % 5 == 0 && level >= 10 ? 1 : 0,
            GaussCharges: index % 11 == 0 && level >= 92 ? 3 : index % 5 == 0 && level >= 15 ? 2 : index % 3 == 0 && level >= 15 ? 1 : 0,
            RicochetCharges: index % 13 == 0 && level >= 92 ? 3 : index % 7 == 0 && level >= 50 ? 2 : index % 5 == 0 && level >= 50 ? 1 : 0,
            DrillCharges: index % 17 == 0 && level >= 94 ? 2 : index % 3 == 0 && level >= 58 ? 1 : 0,
            ReassembleReadyIn: ReadyValue(index, 2),
            GaussReadyIn: ReadyValue(index, 3),
            RicochetReadyIn: ReadyValue(index, 5),
            HyperchargeReadyIn: ReadyValue(index, 7),
            WildfireReadyIn: ReadyValue(index, 11),
            DrillReadyIn: ReadyValue(index, 13),
            AirAnchorReadyIn: ReadyValue(index, 17),
            ChainSawReadyIn: ReadyValue(index, 19),
            BarrelReadyIn: ReadyValue(index, 23),
            FlamethrowerReadyIn: ReadyValue(index, 29),
            TacticianReadyIn: ReadyValue(index, 31),
            DismantleReadyIn: ReadyValue(index, 37),
            QueenReadyIn: ReadyValue(index, 41),
            QueenStrategy: Pick(queenModes, index, 43),
            WildfireStrategy: Pick(wildfireModes, index, 47),
            PotionStrategy: Pick(potionModes, index, 53),
            ToolStrategy: Pick(toolModes, index, 59),
            BuffsStrategy: Pick(forceDelayModes, index, 61),
            HyperchargeStrategy: Pick(forceDelayModes, index, 67),
            AnimationLockDelay: (float)(index % 4) * 0.05f);
    }

    private static T Pick<T>(T[] values, int index, int multiplier)
        => values[(index * multiplier) % values.Length];

    private static double ReadyValue(int index, int salt)
    {
        var values = new[] { 0d, 0.5d, 1d, 2.4d, 5d, 10d, 20d, 40d, 60d, 90d, 120d };
        return values[(index * salt) % values.Length];
    }

    private static unsafe ClientState.Gauge BuildMachinistGauge(RealMchPattern pattern)
    {
        MachinistGauge gauge = default;
        gauge.Heat = (byte)Math.Clamp(pattern.Heat, 0, 100);
        gauge.Battery = (byte)Math.Clamp(pattern.Battery, 0, 100);
        gauge.OverheatTimeRemaining = (short)Math.Clamp((int)(pattern.OverheatLeft * 1000), 0, short.MaxValue);
        gauge.TimerActive = (byte)(pattern.OverheatLeft > 0 ? 1 : 0);
        var raw = (ulong*)Unsafe.AsPointer(ref gauge);
        return new(raw[0], sizeof(MachinistGauge) > 8 ? raw[1] : 0);
    }

    private sealed record HarnessOptions(int? Limit, string? Out, string? Sqpack)
    {
        public static HarnessOptions Parse(string[] args)
        {
            int? limit = null;
            string? output = null;
            string? sqpack = null;

            for (var i = 0; i < args.Length; ++i)
            {
                var key = args[i];
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for '{key}'.");
                var value = args[++i];
                switch (key)
                {
                    case "--limit":
                        limit = int.Parse(value);
                        break;
                    case "--out":
                        output = value;
                        break;
                    case "--sqpack":
                        sqpack = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{key}'.");
                }
            }

            return new(limit, output, sqpack);
        }
    }
}

internal enum AutoForceDelayMode
{
    Auto,
    Force,
    Delay
}

internal sealed record RealMchPattern(
    string Name,
    int CombatTimer,
    int Level,
    int ContentId,
    bool InCombat,
    int TargetCount,
    bool Targetable,
    bool HaveTarget,
    bool CanHitCone,
    bool LookAway,
    bool IsMoving,
    int TargetPriority,
    int SkillSpeed,
    int Heat,
    int Battery,
    double OverheatLeft,
    int OverheatStacks,
    double HyperchargedLeft,
    double ReassembleLeft,
    double WildfireLeft,
    double ExcavatorLeft,
    double FullMetalFieldLeft,
    double FlamethrowerLeft,
    double TacticianLeft,
    double PotionLeft,
    double TargetWildfireLeft,
    double DismantledLeft,
    double BioblasterDotLeft,
    AID ComboLast,
    int ReassembleCharges,
    int GaussCharges,
    int RicochetCharges,
    int DrillCharges,
    double ReassembleReadyIn,
    double GaussReadyIn,
    double RicochetReadyIn,
    double HyperchargeReadyIn,
    double WildfireReadyIn,
    double DrillReadyIn,
    double AirAnchorReadyIn,
    double ChainSawReadyIn,
    double BarrelReadyIn,
    double FlamethrowerReadyIn,
    double TacticianReadyIn,
    double DismantleReadyIn,
    double QueenReadyIn,
    XanMCH.QueenStrategy QueenStrategy,
    XanMCH.WildfireStrategy WildfireStrategy,
    XanMCH.MCHPotionStrategy PotionStrategy,
    XanMCH.ToolStrategy ToolStrategy,
    AutoForceDelayMode BuffsStrategy,
    AutoForceDelayMode HyperchargeStrategy,
    float AnimationLockDelay);

internal sealed record QueuedActionRecord(string Action, ulong Target, float Priority, float Delay);

internal sealed record RealMchExecutionResult(string PatternName, List<QueuedActionRecord> Actions, string? Exception);

internal sealed record RealHarnessResult(int Patterns)
{
    public int Failures { get; set; }
    public int EmptyQueues { get; set; }
    public int ExpectedEmptyQueues { get; set; }
    public List<RealMchExecutionResult> Results { get; } = [];
}
