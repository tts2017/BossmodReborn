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
using BossMod.DRG;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanDRG = BossMod.Autorotation.xan.DRG;

namespace DrgRealHarness;

internal static class Program
{
    private const int DefaultPatternCount = 10_000;
    private static readonly DateTime BaseTime = new(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
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
        Console.WriteLine($"real_drg_patterns={count}");
        return 0;
    }

    private static int Run(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);

        var limit = options.Limit ?? DefaultPatternCount;
        var results = new RealHarnessResult(limit);
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

    private static bool ExpectedEmptyQueue(RealDrgPattern pattern)
        => !pattern.HaveTarget || !pattern.Targetable || pattern.TargetPriority < 0 || pattern.TargetDistance > 20;

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  dotnet run --project tools\\drg_real_harness -- probe [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\drg_real_harness -- count [--limit 10000]");
        Console.Error.WriteLine("  dotnet run --project tools\\drg_real_harness -- run [--limit 10000] [--out <path>] [--sqpack <path>]");
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

    private static RealDrgExecutionResult ExecutePattern(RealDrgPattern pattern, int sampleIndex)
    {
        try
        {
            var world = BuildWorld(pattern, sampleIndex, out var player, out var target, out var hints);
            using var bossmods = new BossModuleManager(world);
            var db = new RotationDatabase(new DirectoryInfo("tools/drg_real_harness/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
            using var manager = new RotationModuleManager(db, bossmods, hints)
            {
                CombatStart = world.CurrentTime.AddSeconds(-pattern.CombatTimer)
            };

            var module = new XanDRG(manager, player);
            var strategy = BuildStrategy(pattern);
            module.Execute(strategy, pattern.HaveTarget ? target : null, pattern.AnimationLockDelay, pattern.IsMoving);

            return new(pattern.Name, [.. hints.ActionsToExecute.Entries.Select(e => new QueuedActionRecord(e.Action.ToString(), e.Target?.InstanceID ?? 0, e.Priority, e.Delay))], null);
        }
        catch (Exception ex)
        {
            return new(pattern.Name, [], ex.ToString());
        }
    }

    private static WorldState BuildWorld(RealDrgPattern pattern, int sampleIndex, out Actor player, out Actor target, out AIHints hints)
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "drg-real-harness");
        var now = BaseTime.AddSeconds(pattern.CombatTimer);
        world.Execute(new WorldState.OpFrameStart(new(now, (ulong)sampleIndex, (uint)sampleIndex, 1.0f / 60.0f, 1.0f / 60.0f, 1), default, BuildDragoonGauge(pattern), default));
        world.Execute(new WorldState.OpZoneChange(0, (ushort)pattern.ContentId));

        var playerID = 0x10000001ul;
        var targetID = 0x40000001ul;
        world.Execute(new ActorState.OpCreate(playerID, 0, 0, 0, "Player", 0, ActorType.Player, Class.DRG, pattern.Level, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
        world.Execute(new ActorState.OpCreate(targetID, 0x1234, 2, 0, "Target", 0, ActorType.Enemy, Class.None, 100, new Vector4(pattern.TargetDistance, 0, 0, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), pattern.Targetable, false, default, default, 0));
        world.Execute(new ActorState.OpCombat(playerID, pattern.InCombat));
        world.Execute(new ActorState.OpCombat(targetID, pattern.InCombat));
        world.Execute(new ClientState.OpPlayerStatsChange(new(pattern.SkillSpeed, 400, 0)));
        world.Execute(new ClientState.OpClassJobLevelsChange(BuildClassJobLevels(pattern.Level)));
        world.Execute(new ClientState.OpComboChange(new((uint)pattern.ComboLast, 14.5f)));
        world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));

        player = world.Actors.Find(playerID)!;
        target = world.Actors.Find(targetID)!;
        ApplyPlayerStatuses(world, player, pattern);

        hints = new AIHints();
        if (pattern.HaveTarget)
        {
            ApplyTargetStatuses(world, target, player, pattern, dotIndex: 0);
            var enemy = new AIHints.Enemy(target, pattern.TargetPriority, false);
            if ((uint)target.CharacterSpawnIndex < AIHints.NumEnemies)
                hints.Enemies[target.CharacterSpawnIndex] = enemy;
            hints.PotentialTargets.Add(enemy);
        }

        for (var i = 1; i < pattern.TargetCount; ++i)
        {
            var id = 0x40000001ul + (ulong)i;
            var spawnIndex = 2 + i * 2;
            var angle = i % 2 == 0 ? 0.18f : -0.18f;
            var distance = Math.Min(pattern.TargetDistance + i % 3, 9.0f);
            world.Execute(new ActorState.OpCreate(id, (uint)(0x1234 + i), spawnIndex, 0, "Target" + i, 0, ActorType.Enemy, Class.None, 100, new Vector4(MathF.Cos(angle) * distance, 0, MathF.Sin(angle) * distance, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), pattern.Targetable, false, default, default, 0));
            var add = world.Actors.Find(id)!;
            ApplyTargetStatuses(world, add, player, pattern, dotIndex: i);
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

    private static List<(int, Cooldown)> BuildCooldowns(RealDrgPattern pattern)
    {
        List<(int, Cooldown)> cooldowns = [];
        SetCooldown(cooldowns, AID.LanceCharge, pattern.LanceChargeReadyIn);
        SetCooldown(cooldowns, AID.BattleLitany, pattern.BattleLitanyReadyIn);
        SetCooldown(cooldowns, AID.Geirskogul, pattern.GeirskogulReadyIn);
        SetCooldown(cooldowns, AID.Jump, pattern.HighJumpReadyIn);
        SetCooldown(cooldowns, AID.HighJump, pattern.HighJumpReadyIn);
        SetCooldown(cooldowns, AID.DragonfireDive, pattern.DragonfireDiveReadyIn);
        SetCooldown(cooldowns, AID.Stardiver, pattern.StardiverReadyIn);
        SetCooldown(cooldowns, AID.WyrmwindThrust, pattern.WyrmwindReadyIn);
        SetCooldown(cooldowns, AID.LifeSurge, pattern.LifeSurgeReadyIn, pattern.LifeSurgeCharges >= MaxChargeCount(AID.LifeSurge, pattern.Level) ? null : pattern.LifeSurgeCharges == 1 ? 20.0f : 0.0f);
        SetCooldown(cooldowns, AID.WingedGlide, pattern.WingedGlideReadyIn, pattern.WingedGlideCharges >= MaxChargeCount(AID.WingedGlide, pattern.Level) ? null : pattern.WingedGlideCharges == 1 ? 30.0f : 0.0f);
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

    private static void ApplyPlayerStatuses(WorldState world, Actor player, RealDrgPattern pattern)
    {
        var index = 0;
        void Status(SID sid, double left)
        {
            if (left > 0 && index < Actor.NumStatuses)
                world.Execute(new ActorState.OpStatus(player.InstanceID, index++, new((uint)sid, 0, world.FutureTime(left), player.InstanceID)));
        }

        Status(SID.PowerSurge, pattern.PowerSurgeLeft);
        Status(SID.LanceCharge, pattern.LanceChargeLeft);
        Status(SID.BattleLitany, pattern.BattleLitanyLeft);
        Status(SID.LifeSurge, pattern.LifeSurgeLeft);
        Status(SID.DraconianFire, pattern.DraconianFireLeft);
        Status(SID.DiveReady, pattern.DiveReadyLeft);
        Status(SID.NastrondReady, pattern.NastrondReadyLeft);
        Status(SID.DragonsFlight, pattern.DragonsFlightLeft);
        Status(SID.StarcrossReady, pattern.StarcrossReadyLeft);
        Status(SID.EnhancedPiercingTalon, pattern.EnhancedTalonLeft);
    }

    private static void ApplyTargetStatuses(WorldState world, Actor target, Actor player, RealDrgPattern pattern, int dotIndex)
    {
        var left = dotIndex switch
        {
            0 => pattern.DotLeft0,
            1 => pattern.DotLeft1,
            _ => 0
        };

        if (left > 0)
            world.Execute(new ActorState.OpStatus(target.InstanceID, 0, new((uint)(pattern.UseChaoticSpringDot ? SID.ChaoticSpring : SID.ChaosThrust), 0, world.FutureTime(left), player.InstanceID)));
    }

    private static StrategyValues BuildStrategy(RealDrgPattern pattern)
    {
        var definition = XanDRG.Definition();
        var strategy = new StrategyValues(definition.Configs);
        SetTrack(strategy, "Targeting", Targeting.Auto);
        SetTrack(strategy, "AOE", pattern.TargetCount >= 3 ? AOEStrategy.AOE : AOEStrategy.ST);
        SetTrack(strategy, "Buffs", OffensiveStrategy.Automatic);
        SetTrack(strategy, "Dive", pattern.DiveStrategy);
        SetTrack(strategy, "Iainuki", EnabledByDefault.Disabled);
        SetTrack(strategy, "Zeninage", EnabledByDefault.Disabled);
        SetTrack(strategy, "LC", pattern.LanceStrategy);
        SetTrack(strategy, "HJMD", pattern.HjmdStrategy);
        return strategy;
    }

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

    private static RealDrgPattern GeneratedPattern(int index)
    {
        var levels = new[] { 18, 30, 50, 58, 64, 70, 72, 76, 80, 82, 86, 90, 92, 96, 100 };
        var targetCounts = new[] { 1, 1, 2, 3, 4, 5, 6 };
        var combos = new[]
        {
            AID.None,
            AID.TrueThrust,
            AID.RaidenThrust,
            AID.Disembowel,
            AID.SpiralBlow,
            AID.ChaosThrust,
            AID.ChaoticSpring,
            AID.VorpalThrust,
            AID.LanceBarrage,
            AID.FullThrust,
            AID.HeavensThrust,
            AID.FangAndClaw,
            AID.WheelingThrust,
            AID.DoomSpike,
            AID.DraconianFury,
            AID.SonicThrust
        };
        var diveModes = Enum.GetValues<XanDRG.DiveStrategy>();
        var lanceModes = Enum.GetValues<XanDRG.LanceChargeStrategy>();
        var hjmdModes = Enum.GetValues<XanDRG.HJMDStrategy>();
        var targetCount = Pick(targetCounts, index, 7);
        var level = Pick(levels, index, 3);
        var melee = index % 17 != 0;

        return new(
            Name: $"real_drg_{index:00000}",
            CombatTimer: index % 23 == 0 ? index % 30 : (index * 7) % 720,
            Level: level,
            ContentId: 0,
            InCombat: true,
            TargetCount: targetCount,
            Targetable: true,
            HaveTarget: true,
            TargetPriority: Pick(new[] { 1, 2, 3 }, index, 11),
            TargetDistance: melee ? 2.5f : Pick(new[] { 4.5f, 8.0f, 12.0f, 18.0f }, index, 13),
            LookAway: index % 41 == 0,
            IsMoving: index % 43 == 0,
            SkillSpeed: Pick(new[] { 380, 400, 450, 520 }, index, 5),
            ComboLast: Pick(combos, index, 19),
            Eyes: index % 3,
            Focus: index % 2 == 0 ? 2 : index % 3,
            LotdLeft: index % 11 == 0 && level >= 70 ? 8 + index % 12 : 0,
            PowerSurgeLeft: index % 5 == 0 ? 3 + index % 24 : 30,
            LanceChargeLeft: index % 13 == 0 && level >= 30 ? 4 + index % 16 : 0,
            BattleLitanyLeft: index % 17 == 0 && level >= 52 ? 5 + index % 15 : 0,
            LifeSurgeLeft: index % 29 == 0 && level >= 6 ? 3 : 0,
            DraconianFireLeft: index % 7 == 0 && level >= 76 ? 8 + index % 12 : 0,
            DiveReadyLeft: index % 23 == 0 && level >= 68 ? 10 : 0,
            NastrondReadyLeft: index % 19 == 0 && level >= 70 ? 8 : 0,
            DragonsFlightLeft: index % 31 == 0 && level >= 92 ? 10 : 0,
            StarcrossReadyLeft: index % 37 == 0 && level >= 100 ? 10 : 0,
            EnhancedTalonLeft: index % 47 == 0 && level >= 15 ? 10 : 0,
            DotLeft0: index % 3 == 0 && level >= 50 ? 2 + index % 22 : 0,
            DotLeft1: targetCount == 2 && level >= 50 ? index % 2 == 0 ? 0 : 4 + index % 18 : 0,
            UseChaoticSpringDot: level >= 86,
            LanceChargeReadyIn: ReadyValue(index, 2),
            BattleLitanyReadyIn: ReadyValue(index, 3),
            GeirskogulReadyIn: ReadyValue(index, 5),
            HighJumpReadyIn: ReadyValue(index, 7),
            DragonfireDiveReadyIn: ReadyValue(index, 11),
            StardiverReadyIn: ReadyValue(index, 13),
            WyrmwindReadyIn: ReadyValue(index, 17),
            LifeSurgeReadyIn: ReadyValue(index, 19),
            WingedGlideReadyIn: ReadyValue(index, 23),
            LifeSurgeCharges: index % 5 == 0 && level >= 88 ? 2 : index % 3 == 0 && level >= 6 ? 1 : 0,
            WingedGlideCharges: index % 5 == 0 && level >= 84 ? 2 : index % 3 == 0 && level >= 45 ? 1 : 0,
            DiveStrategy: Pick(diveModes, index, 29),
            LanceStrategy: Pick(lanceModes, index, 31),
            HjmdStrategy: Pick(hjmdModes, index, 37),
            AnimationLockDelay: (float)(index % 4) * 0.05f);
    }

    private static T Pick<T>(T[] values, int index, int multiplier)
        => values[(index * multiplier) % values.Length];

    private static double ReadyValue(int index, int salt)
    {
        var values = new[] { 0d, 0.5d, 1d, 2.4d, 5d, 10d, 20d, 40d, 60d, 90d, 120d };
        return values[(index * salt) % values.Length];
    }

    private static unsafe ClientState.Gauge BuildDragoonGauge(RealDrgPattern pattern)
    {
        DragoonGauge gauge = default;
        gauge.EyeCount = (byte)Math.Clamp(pattern.Eyes, 0, 2);
        gauge.FirstmindsFocusCount = (byte)Math.Clamp(pattern.Focus, 0, 2);
        gauge.LotdTimer = (short)Math.Clamp((int)(pattern.LotdLeft * 1000), 0, short.MaxValue);
        var raw = (ulong*)Unsafe.AsPointer(ref gauge);
        return new(raw[0], sizeof(DragoonGauge) > 8 ? raw[1] : 0);
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

internal sealed record RealDrgPattern(
    string Name,
    double CombatTimer,
    int Level,
    int ContentId,
    bool InCombat,
    int TargetCount,
    bool Targetable,
    bool HaveTarget,
    int TargetPriority,
    float TargetDistance,
    bool LookAway,
    bool IsMoving,
    int SkillSpeed,
    AID ComboLast,
    int Eyes,
    int Focus,
    double LotdLeft,
    double PowerSurgeLeft,
    double LanceChargeLeft,
    double BattleLitanyLeft,
    double LifeSurgeLeft,
    double DraconianFireLeft,
    double DiveReadyLeft,
    double NastrondReadyLeft,
    double DragonsFlightLeft,
    double StarcrossReadyLeft,
    double EnhancedTalonLeft,
    double DotLeft0,
    double DotLeft1,
    bool UseChaoticSpringDot,
    double LanceChargeReadyIn,
    double BattleLitanyReadyIn,
    double GeirskogulReadyIn,
    double HighJumpReadyIn,
    double DragonfireDiveReadyIn,
    double StardiverReadyIn,
    double WyrmwindReadyIn,
    double LifeSurgeReadyIn,
    double WingedGlideReadyIn,
    int LifeSurgeCharges,
    int WingedGlideCharges,
    XanDRG.DiveStrategy DiveStrategy,
    XanDRG.LanceChargeStrategy LanceStrategy,
    XanDRG.HJMDStrategy HjmdStrategy,
    float AnimationLockDelay);

internal sealed record QueuedActionRecord(string Action, ulong Target, float Priority, float Delay);

internal sealed record RealDrgExecutionResult(string PatternName, List<QueuedActionRecord> Actions, string? Exception);

internal sealed record RealHarnessResult(int Patterns)
{
    public int Failures { get; set; }
    public int EmptyQueues { get; set; }
    public int ExpectedEmptyQueues { get; set; }
    public List<RealDrgExecutionResult> Results { get; } = [];
}
