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
using BossMod.SAM;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanSAM = BossMod.Autorotation.xan.SAM;

namespace SamRealHarness;

internal static class Program
{
    private const int DefaultPatternCount = 20_000;
    private static readonly DateTime BaseTime = new(2026, 7, 5, 0, 0, 0, DateTimeKind.Utc);
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
        Console.WriteLine($"real_sam_patterns={count}");
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

        results.SequenceFailures.AddRange(RunLiveSequenceChecks());
        results.SequenceFailures.AddRange(RunTransitionSequenceChecks());

        if (!string.IsNullOrWhiteSpace(options.Out))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Out))!);
            File.WriteAllText(options.Out, JsonSerializer.Serialize(results, JsonOptions));
        }

        Console.WriteLine($"patterns={results.Patterns}");
        Console.WriteLine($"failures={results.Failures}");
        Console.WriteLine($"empty_queues={results.EmptyQueues}");
        Console.WriteLine($"expected_empty_queues={results.ExpectedEmptyQueues}");
        Console.WriteLine($"sequence_failures={results.SequenceFailures.Count}");
        foreach (var failure in results.SequenceFailures)
            Console.Error.WriteLine($"sequence_failure={failure}");
        if (!string.IsNullOrWhiteSpace(options.Out))
            Console.WriteLine($"out={options.Out}");

        return results.Failures == 0 && results.EmptyQueues == 0 && results.SequenceFailures.Count == 0 ? 0 : 3;
    }

    private static bool ExpectedEmptyQueue(RealSamPattern pattern)
        => !pattern.HaveTarget
            || !pattern.Targetable
            || !pattern.CanMelee && pattern.Enpi == XanSAM.EnpiStrategy.None
            || !pattern.CanMelee && pattern.Enpi == XanSAM.EnpiStrategy.Enhanced && pattern.EnhancedEnpiLeft <= 0 && !(pattern.RecentAction == AID.HissatsuYaten && pattern.RecentActionAge <= 1);

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  dotnet run --project tools\\sam_real_harness -- probe [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\sam_real_harness -- count [--limit 20000]");
        Console.Error.WriteLine("  dotnet run --project tools\\sam_real_harness -- run [--limit 20000] [--out <path>] [--sqpack <path>]");
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

    private static RealSamExecutionResult ExecutePattern(RealSamPattern pattern, int sampleIndex)
    {
        try
        {
            var world = BuildWorld(pattern, sampleIndex, out var player, out var target, out var hints);
            using var bossmods = new BossModuleManager(world);
            var db = new RotationDatabase(new DirectoryInfo("tools/sam_real_harness/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
            using var manager = new RotationModuleManager(db, bossmods, hints)
            {
                CombatStart = world.CurrentTime.AddSeconds(-pattern.CombatTimer)
            };
            if (pattern.RecentAction != AID.None)
            {
                var cast = new ActorCastEvent(ActionID.MakeSpell(pattern.RecentAction), target.InstanceID, 0.6f, 1, target.PosRot.XYZ(), 1, 1, default);
                manager.LastCast = (world.CurrentTime.AddSeconds(-pattern.RecentActionAge), cast);
            }

            var module = new XanSAM(manager, player);
            var strategy = BuildStrategy(pattern);
            XanSAM.RealHarnessMeikyoChargesOverride = () => pattern.MeikyoCharges;
            try
            {
                module.Execute(strategy, pattern.HaveTarget ? target : null, pattern.AnimationLockDelay, pattern.IsMoving);
            }
            finally
            {
                XanSAM.RealHarnessMeikyoChargesOverride = null;
            }

            var actions = hints.ActionsToExecute.Entries.Select(ToQueuedActionRecord).ToList();
            var selectedGCD = SelectBestAction(world, player, hints, pattern, gcd: true);
            var selectedOGCD = SelectBestAction(world, player, hints, pattern, gcd: false);
            var statuses = string.Join(',', player.Statuses.Where(s => s.ID != default).Select(s => $"{s.ID}:{MathF.Max(0, (float)(s.ExpireAt - world.CurrentTime).TotalSeconds):0.0}"));
            var state = $"sen={module.Sen};tendo={module.Tendo:0.0};ogi={module.OgiLeft:0.0};tsubame={module.Tsubame.Action}:{module.Tsubame.Left:0.0};meikyo={module.Meikyo.Left:0.0}/{module.Meikyo.Stacks};aoe={module.NumAOECircleTargets};tenka={module.NumTenkaTargets};ogiTargets={module.NumOgiTargets}";
            return new(pattern.Name, actions, selectedGCD, selectedOGCD, statuses, state, null);
        }
        catch (Exception ex)
        {
            return new(pattern.Name, [], null, null, string.Empty, string.Empty, ex.ToString());
        }
    }

    private static QueuedActionRecord ToQueuedActionRecord(ActionQueue.Entry entry)
        => new(entry.Action.Type, entry.Action.ID, entry.Action.ToString(), entry.Target?.InstanceID ?? 0, entry.Priority, entry.Delay);

    private static QueuedActionRecord? SelectBestAction(WorldState world, Actor player, AIHints hints, RealSamPattern pattern, bool gcd)
    {
        var queue = new ActionQueue();
        foreach (var entry in hints.ActionsToExecute.Entries)
        {
            var definition = ActionDefinitions.Instance[entry.Action];
            if (definition?.IsGCD != gcd)
                continue;

            queue.Push(entry.Action, entry.Target, entry.Priority, entry.Expire, entry.Delay, entry.CastTime, entry.TargetPos, entry.FacingAngle, entry.Manual, entry.Force);
        }

        var selected = queue.FindBest(world, player, world.Client.Cooldowns, world.Client.AnimationLock, hints, pattern.AnimationLockDelay, allowDismount: true);
        return selected.Action.ID != 0 ? ToQueuedActionRecord(selected) : null;
    }

    private static WorldState BuildWorld(RealSamPattern pattern, int sampleIndex, out Actor player, out Actor target, out AIHints hints)
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "sam-real-harness");
        var now = BaseTime.AddSeconds(pattern.CombatTimer);
        world.Execute(new WorldState.OpFrameStart(new(now, (ulong)sampleIndex, (uint)sampleIndex, 1.0f / 60.0f, 1.0f / 60.0f, 1), default, BuildSamuraiGauge(pattern.Kenki, pattern.Meditation, pattern.Sen, pattern.Kaeshi), default));
        world.Execute(new WorldState.OpZoneChange(0, (ushort)pattern.ContentId));

        var playerID = 0x10000001ul;
        var targetID = 0x40000001ul;
        var targetDistance = pattern.TargetHitboxDistanceOverride is { } hitboxDistance ? hitboxDistance + 2.5f : pattern.CanMelee ? 2.5f : 12.0f;
        world.Execute(new ActorState.OpCreate(playerID, 0, 0, 0, "Player", 0, ActorType.Player, Class.SAM, pattern.Level, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
        world.Execute(new ActorState.OpCreate(targetID, 0x1234, 2, 0, "Target", 0, ActorType.Enemy, Class.None, 100, new Vector4(targetDistance, 0, 0, pattern.TargetRotation), 2.0f, new(1000000, 1000000, 0, 10000, 10000), pattern.PrimaryTargetableOverride ?? pattern.Targetable, false, default, default, 0));
        world.Execute(new ActorState.OpCombat(playerID, pattern.InCombat));
        world.Execute(new ActorState.OpCombat(targetID, pattern.InCombat));
        world.Execute(new ClientState.OpPlayerStatsChange(new(400, 400, 0)));
        world.Execute(new ClientState.OpClassJobLevelsChange(BuildClassJobLevels(pattern.Level)));
        world.Execute(new ClientState.OpComboChange(new((uint)pattern.ComboLast, 14.5f)));
        world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));

        player = world.Actors.Find(playerID)!;
        target = world.Actors.Find(targetID)!;
        target.Omnidirectional = pattern.TargetOmnidirectional;
        ApplyPlayerStatuses(world, player, pattern);
        ApplyTargetStatuses(world, target, player, pattern.TargetHiganbanaLeft);

        hints = new AIHints();
        if (pattern.HaveTarget && target.IsTargetable && !target.IsDead)
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
            world.Execute(new ActorState.OpCreate(id, (uint)(0x1234 + i), spawnIndex, 0, "Target" + i, 0, ActorType.Enemy, Class.None, 100, new Vector4(targetDistance, 0, i * 2, pattern.TargetRotation), 2.0f, new(1000000, 1000000, 0, 10000, 10000), pattern.Targetable, false, default, default, 0));
            var add = world.Actors.Find(id)!;
            if (!add.IsTargetable || add.IsDead)
                continue;

            if (i == 1)
                ApplyTargetStatuses(world, add, player, pattern.SecondaryHiganbanaLeft);

            var enemy = new AIHints.Enemy(add, pattern.TargetPriority, false);
            if ((uint)add.CharacterSpawnIndex < AIHints.NumEnemies)
                hints.Enemies[add.CharacterSpawnIndex] = enemy;
            hints.PotentialTargets.Add(enemy);
        }

        if (pattern.LookAway)
            hints.ForbiddenDirections.Add((player.AngleTo(target), 180.Degrees(), default));

        hints.Normalize();
        return world;
    }

    private static short[] BuildClassJobLevels(int level)
    {
        var levels = new short[ClientState.NumClassLevels];
        Array.Fill(levels, (short)level);
        return levels;
    }

    private static List<(int, Cooldown)> BuildCooldowns(RealSamPattern pattern)
    {
        List<(int, Cooldown)> cooldowns = [];
        if (pattern.GCDReadyIn > 0)
        {
            const float totalGCD = 2.5f;
            cooldowns.Add((ActionDefinitions.GCDGroup, new(Math.Clamp(totalGCD - (float)pattern.GCDReadyIn, 0, totalGCD), totalGCD)));
        }
        SetCooldown(cooldowns, AID.MeikyoShisui, pattern.MeikyoReadyIn, chargeElapsedOverride: pattern.MeikyoCharges >= 2 ? null : pattern.MeikyoCharges == 1 ? 55.0f : 0.0f);
        SetCooldown(cooldowns, AID.Ikishoten, pattern.IkishotenReadyIn);
        SetCooldown(cooldowns, AID.HissatsuSenei, pattern.SeneiGurenReadyIn);
        SetCooldown(cooldowns, AID.HissatsuGuren, pattern.SeneiGurenReadyIn);
        SetCooldown(cooldowns, AID.Shoha, pattern.ShohaReadyIn);
        SetCooldown(cooldowns, AID.Zanshin, pattern.ZanshinReadyIn);
        SetCooldown(cooldowns, AID.HissatsuShinten, pattern.KenkiSpendReadyIn);
        SetCooldown(cooldowns, AID.HissatsuKyuten, pattern.KenkiSpendReadyIn);
        SetCooldown(cooldowns, AID.HissatsuGyoten, pattern.GyotenReadyIn);
        SetCooldown(cooldowns, AID.HissatsuYaten, pattern.YatenReadyIn);
        SetCooldown(cooldowns, AID.Hagakure, pattern.HagakureReadyIn);
        SetCooldown(cooldowns, AID.TrueNorth, pattern.TrueNorthReadyIn);
        return cooldowns;
    }

    private static void SetCooldown<T>(List<(int, Cooldown)> cooldowns, T aid, double readyIn, float? chargeElapsedOverride = null) where T : Enum
    {
        var def = ActionDefinitions.Instance.Spell(aid);
        if (def == null || def.MainCooldownGroup < 0)
            return;

        var total = Math.Max(def.Cooldown * Math.Max(1, def.MaxChargesAtCap()), 1f);
        var elapsed = chargeElapsedOverride ?? Math.Clamp(total - (float)readyIn, 0, total);
        cooldowns.RemoveAll(c => c.Item1 == def.MainCooldownGroup);
        cooldowns.Add((def.MainCooldownGroup, new(elapsed, total)));
    }

    private static void ApplyPlayerStatuses(WorldState world, Actor player, RealSamPattern pattern)
    {
        var index = 0;
        void Status(SID sid, double left, ushort extra = 0)
        {
            if (left > 0 && index < Actor.NumStatuses)
                world.Execute(new ActorState.OpStatus(player.InstanceID, index++, new((uint)sid, extra, world.FutureTime(left), player.InstanceID)));
        }

        Status(SID.Fugetsu, pattern.FugetsuLeft);
        Status(SID.Fuka, pattern.FukaLeft);
        Status(SID.MeikyoShisui, pattern.MeikyoLeft, (ushort)pattern.MeikyoStacks);
        Status(SID.OgiNamikiriReady, pattern.OgiLeft);
        Status(SID.EnhancedEnpi, pattern.EnhancedEnpiLeft);
        Status(SID.ZanshinReady, pattern.ZanshinLeft);
        Status(SID.Tendo, pattern.TendoLeft);
        Status(SID.TrueNorth, pattern.TrueNorthLeft);

        switch (pattern.TsubameRepeat)
        {
            case XanSAM.IaiRepeat.Goken:
                Status(SID.KaeshiGoken, pattern.TsubameLeft);
                break;
            case XanSAM.IaiRepeat.Setsugekka:
                Status(SID.KaeshiSetsugekka, pattern.TsubameLeft);
                break;
            case XanSAM.IaiRepeat.TendoGoken:
                Status(SID.TendoKaeshiGoken, pattern.TsubameLeft);
                break;
            case XanSAM.IaiRepeat.TendoSetsugekka:
                Status(SID.TendoKaeshiSetsugekka, pattern.TsubameLeft);
                break;
        }
    }

    private static void ApplyTargetStatuses(WorldState world, Actor target, Actor player, double higanbanaLeft)
    {
        var index = 0;
        void Status(SID sid, double left)
        {
            if (left > 0 && index < Actor.NumStatuses)
                world.Execute(new ActorState.OpStatus(target.InstanceID, index++, new((uint)sid, 0, world.FutureTime(left), player.InstanceID)));
        }

        Status(SID.Higanbana, higanbanaLeft);
    }

    private static StrategyValues BuildStrategy(RealSamPattern pattern)
    {
        var definition = XanSAM.Definition();
        var strategy = new StrategyValues(definition.Configs);
        SetTrack(strategy, "Targeting", Targeting.Auto);
        SetTrack(strategy, "AOE", pattern.TargetCount >= 3 ? AOEStrategy.AOE : AOEStrategy.ST);
        SetTrack(strategy, "Buffs", MapOffensive(pattern.Buffs));
        SetTrack(strategy, "Potion", pattern.Potion);
        SetTrack(strategy, "Tsubame", pattern.Tsubame);
        SetTrack(strategy, "Namikiri", pattern.Namikiri);
        SetTrack(strategy, "Higanbana", pattern.Higanbana);
        SetTrack(strategy, "Enpi", pattern.Enpi);
        SetTrack(strategy, "Meikyo", pattern.Meikyo);
        SetTrack(strategy, "TrueNorth", pattern.TrueNorth);
        SetTrack(strategy, "Opener", pattern.Opener);
        SetTrack(strategy, "OpenerBurst", pattern.OpenerBurst);
        SetTrack(strategy, "GCDRoute", pattern.GcdRoute);
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

    private static List<string> RunLiveSequenceChecks()
    {
        List<string> failures = [];
        var checks = Fast208LiveSequenceChecks().Concat(AdditionalLiveChecks()).ToArray();
        for (var i = 0; i < checks.Length; ++i)
        {
            var check = checks[i];
            var result = ExecutePattern(check.Pattern, DefaultPatternCount + i);
            if (result.Exception != null)
            {
                failures.Add($"{check.Name}: {result.Exception}");
                continue;
            }

            var actual = result.SelectedGCD is { } selectedGCD ? (AID)selectedGCD.ID : AID.None;
            if (actual != check.ExpectedGCD)
                failures.Add($"{check.Name}: expected {check.ExpectedGCD}, got {actual}; state={result.State}; statuses={result.Statuses}; queued={string.Join(',', result.Actions.Where(a => a.Type == ActionType.Spell).OrderByDescending(a => a.Priority).Select(a => $"{a.Action}@{a.Priority:0}"))}");

            if (check.ExpectedOGCD is { } expectedOGCD)
            {
                var actualOGCD = result.SelectedOGCD is { } selectedOGCD ? (AID)selectedOGCD.ID : AID.None;
                if (actualOGCD != expectedOGCD)
                    failures.Add($"{check.Name}: expected oGCD {expectedOGCD}, got {actualOGCD}; state={result.State}; queued={string.Join(',', result.Actions.Where(a => a.Type == ActionType.Spell && a.Priority < ActionQueue.Priority.High).OrderByDescending(a => a.Priority).Select(a => $"{a.Action}@{a.Priority:0}"))}");
            }

            if (check.ExpectedGCDTargetIndex is { } targetIndex && result.SelectedGCD?.Target != 0x40000001ul + (ulong)targetIndex)
                failures.Add($"{check.Name}: expected GCD target index {targetIndex}, got 0x{result.SelectedGCD?.Target ?? 0:X}; state={result.State}");

            if (check.ExpectedPotion is { } expectedPotion)
            {
                var potionQueued = result.Actions.Any(a => new ActionID(a.Type, a.ID) == ActionDefinitions.IDPotionStr);
                if (potionQueued != expectedPotion)
                    failures.Add($"{check.Name}: expected potion queued={expectedPotion}, got {potionQueued}; state={result.State}");
            }

            if (check.ForbiddenAction is { } forbidden && result.Actions.Any(a => a.Type == ActionType.Spell && a.ID == (uint)forbidden))
                failures.Add($"{check.Name}: queued forbidden {forbidden}");
        }

        return failures;
    }

    private static List<string> RunTransitionSequenceChecks()
    {
        List<string> failures = [];
        RunFast208OpenerTransition(failures);
        RunSharif214HiganbanaTransition(failures);
        RunSingleTargetKenkiTransition(failures);
        RunAOEKenkiTransition(failures);
        RunIkishotenKenkiTransition(failures);
        RunMeditationPriorityTransition(failures);
        return failures;
    }

    private static void RunFast208OpenerTransition(List<string> failures)
    {
        var state = Fast208Pattern("transition_fast208_opener_01", 0, SenFlags.None, AID.None, 0, meikyoLeft: 20, meikyoStacks: 3, fugetsuLeft: 0, fukaLeft: 0);
        AID[] expected =
        [
            AID.Gekko,
            AID.Kasha,
            AID.Yukikaze,
            AID.TendoSetsugekka,
            AID.TendoKaeshiSetsugekka,
            AID.Gekko,
            AID.Higanbana,
            AID.OgiNamikiri,
            AID.KaeshiNamikiri,
            AID.Kasha,
            AID.Gekko,
            AID.Gyofu,
            AID.Yukikaze,
            AID.TendoSetsugekka,
            AID.TendoKaeshiSetsugekka
        ];

        for (var index = 0; index < expected.Length; ++index)
        {
            state = state with { Name = $"transition_fast208_opener_{index + 1:00}" };
            if (!TryExecuteTransitionStep(failures, state, expected[index], 100_000 + index, out var selected))
                return;

            state = ApplyTransitionGCD(state, selected, 3);
            if (index == 1)
                state = state with { OgiLeft = 30, ZanshinLeft = 30 };
            if (index == 4)
                state = state with { MeikyoLeft = 20, MeikyoStacks = 3 };
        }
    }

    private static void RunSharif214HiganbanaTransition(List<string> failures)
    {
        var state = Fast208Pattern("transition_sharif214_bana_01", 60, SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, AID.None, 5, meikyoLeft: 20, meikyoStacks: 3, tendoLeft: 30) with
        {
            GcdRoute = XanSAM.GCDRouteStrategy.GCD214
        };
        AID[] expected = [AID.TendoSetsugekka, AID.Kasha, AID.Higanbana, AID.TendoKaeshiSetsugekka];

        for (var index = 0; index < expected.Length; ++index)
        {
            state = state with { Name = $"transition_sharif214_bana_{index + 1:00}" };
            if (!TryExecuteTransitionStep(failures, state, expected[index], 101_000 + index, out var selected))
                return;

            state = ApplyTransitionGCD(state, selected, 2.14);
        }
    }

    private static void RunSingleTargetKenkiTransition(List<string> failures)
    {
        var state = KenkiTransitionPattern("transition_kenki_single_01", targets: 1, kenki: 75, meditation: 0, seneiGurenReadyIn: 0, zanshinLeft: 30, shohaReadyIn: 120);
        AID[] expected = [AID.HissatsuSenei, AID.Zanshin, AID.None];
        RunOGCDTransition(failures, state, expected, 102_000);
    }

    private static void RunAOEKenkiTransition(List<string> failures)
    {
        var state = KenkiTransitionPattern("transition_kenki_aoe_01", targets: 3, kenki: 75, meditation: 0, seneiGurenReadyIn: 0, zanshinLeft: 30, shohaReadyIn: 120);
        AID[] expected = [AID.HissatsuGuren, AID.Zanshin, AID.None];
        RunOGCDTransition(failures, state, expected, 103_000);
    }

    private static void RunIkishotenKenkiTransition(List<string> failures)
    {
        var state = KenkiTransitionPattern("transition_ikishoten_01", targets: 1, kenki: 25, meditation: 0, seneiGurenReadyIn: 120, zanshinLeft: 0, shohaReadyIn: 120) with
        {
            IkishotenReadyIn = 0
        };
        AID[] expected = [AID.Ikishoten, AID.Zanshin, AID.HissatsuShinten, AID.None];
        RunOGCDTransition(failures, state, expected, 104_000);
    }

    private static void RunMeditationPriorityTransition(List<string> failures)
    {
        var state = KenkiTransitionPattern("transition_meditation_01", targets: 1, kenki: 75, meditation: 3, seneiGurenReadyIn: 0, zanshinLeft: 30, shohaReadyIn: 0);
        AID[] expected = [AID.HissatsuSenei, AID.Zanshin, AID.Shoha, AID.None];
        RunOGCDTransition(failures, state, expected, 105_000);
    }

    private static RealSamPattern KenkiTransitionPattern(string name, int targets, int kenki, int meditation, double seneiGurenReadyIn, double zanshinLeft, double shohaReadyIn)
        => Fast208Pattern(name, 120, SenFlags.None, AID.None, 60) with
        {
            TargetCount = targets,
            Buffs = AutoForceDelayMode.Auto,
            Kenki = kenki,
            Meditation = meditation,
            IkishotenReadyIn = 120,
            SeneiGurenReadyIn = seneiGurenReadyIn,
            ZanshinLeft = zanshinLeft,
            ZanshinReadyIn = zanshinLeft > 0 ? 0 : 120,
            ShohaReadyIn = shohaReadyIn,
            KenkiSpendReadyIn = 0
        };

    private static void RunOGCDTransition(List<string> failures, RealSamPattern state, IReadOnlyList<AID> expected, int sampleIndex)
    {
        for (var index = 0; index < expected.Count; ++index)
        {
            state = state with { Name = $"{state.Name[..state.Name.LastIndexOf('_')]}_{index + 1:00}" };
            if (!TryExecuteOGCDTransitionStep(failures, state, expected[index], sampleIndex + index, out var selected))
                return;

            state = ApplyTransitionOGCD(state, selected, 0.7);
        }
    }

    private static bool TryExecuteOGCDTransitionStep(List<string> failures, RealSamPattern state, AID expected, int sampleIndex, out AID selected)
    {
        var result = ExecutePattern(state, sampleIndex);
        selected = result.SelectedOGCD is { } selectedOGCD ? (AID)selectedOGCD.ID : AID.None;
        if (result.Exception != null)
        {
            failures.Add($"{state.Name}: {result.Exception}");
            return false;
        }

        if (selected == expected)
            return true;

        failures.Add($"{state.Name}: expected oGCD {expected}, got {selected}; state={result.State}; queued={string.Join(',', result.Actions.Where(a => a.Type == ActionType.Spell && ActionDefinitions.Instance[new ActionID(a.Type, a.ID)]?.IsGCD == false).OrderByDescending(a => a.Priority).Select(a => $"{a.Action}@{a.Priority:0}"))}");
        return false;
    }

    private static RealSamPattern ApplyTransitionOGCD(RealSamPattern state, AID action, double elapsed)
    {
        var kenki = state.Kenki;
        var meditation = state.Meditation;
        var ikishotenReadyIn = Decay(state.IkishotenReadyIn, elapsed);
        var seneiGurenReadyIn = Decay(state.SeneiGurenReadyIn, elapsed);
        var zanshinLeft = Decay(state.ZanshinLeft, elapsed);
        var zanshinReadyIn = Decay(state.ZanshinReadyIn, elapsed);
        var shohaReadyIn = Decay(state.ShohaReadyIn, elapsed);
        var kenkiSpendReadyIn = Decay(state.KenkiSpendReadyIn, elapsed);
        var ogiLeft = Decay(state.OgiLeft, elapsed);

        switch (action)
        {
            case AID.Ikishoten:
                kenki = Math.Min(100, kenki + 50);
                ikishotenReadyIn = 120;
                ogiLeft = 30;
                zanshinLeft = 30;
                zanshinReadyIn = 0;
                break;
            case AID.HissatsuSenei:
            case AID.HissatsuGuren:
                kenki = Math.Max(0, kenki - 25);
                seneiGurenReadyIn = 60;
                break;
            case AID.Zanshin:
                kenki = Math.Max(0, kenki - 50);
                zanshinLeft = 0;
                zanshinReadyIn = 1;
                break;
            case AID.HissatsuShinten:
            case AID.HissatsuKyuten:
                kenki = Math.Max(0, kenki - 25);
                kenkiSpendReadyIn = 1;
                break;
            case AID.Shoha:
                meditation = 0;
                shohaReadyIn = 15;
                break;
        }

        return state with
        {
            Kenki = kenki,
            Meditation = meditation,
            IkishotenReadyIn = ikishotenReadyIn,
            SeneiGurenReadyIn = seneiGurenReadyIn,
            ZanshinLeft = zanshinLeft,
            ZanshinReadyIn = zanshinReadyIn,
            ShohaReadyIn = shohaReadyIn,
            KenkiSpendReadyIn = kenkiSpendReadyIn,
            OgiLeft = ogiLeft
        };
    }

    private static bool TryExecuteTransitionStep(List<string> failures, RealSamPattern state, AID expected, int sampleIndex, out AID selected)
    {
        var result = ExecutePattern(state, sampleIndex);
        selected = result.SelectedGCD is { } selectedGCD ? (AID)selectedGCD.ID : AID.None;
        if (result.Exception != null)
        {
            failures.Add($"{state.Name}: {result.Exception}");
            return false;
        }

        if (selected == expected)
            return true;

        failures.Add($"{state.Name}: expected {expected}, got {selected}; state={result.State}; statuses={result.Statuses}; queued={string.Join(',', result.Actions.Where(a => a.Type == ActionType.Spell).OrderByDescending(a => a.Priority).Select(a => $"{a.Action}@{a.Priority:0}"))}");
        return false;
    }

    private static RealSamPattern ApplyTransitionGCD(RealSamPattern state, AID action, double elapsed)
    {
        var sen = state.Sen;
        var combo = AID.None;
        var meikyoLeft = Decay(state.MeikyoLeft, elapsed);
        var meikyoStacks = state.MeikyoStacks;
        var tendoLeft = Decay(state.TendoLeft, elapsed);
        var tsubameLeft = Decay(state.TsubameLeft, elapsed);
        var tsubameRepeat = state.TsubameRepeat;
        var ogiLeft = Decay(state.OgiLeft, elapsed);
        var kaeshi = state.Kaeshi;
        var dotLeft = Decay(state.TargetHiganbanaLeft, elapsed);
        var fugetsuLeft = Decay(state.FugetsuLeft, elapsed);
        var fukaLeft = Decay(state.FukaLeft, elapsed);

        switch (action)
        {
            case AID.Gyofu:
            case AID.Hakaze:
                combo = action;
                break;
            case AID.Jinpu:
                combo = action;
                fugetsuLeft = 40;
                break;
            case AID.Shifu:
                combo = action;
                fukaLeft = 40;
                break;
            case AID.Yukikaze:
                sen |= SenFlags.Setsu;
                break;
            case AID.Gekko:
            case AID.Mangetsu:
                sen |= SenFlags.Getsu;
                fugetsuLeft = 40;
                break;
            case AID.Kasha:
            case AID.Oka:
                sen |= SenFlags.Ka;
                fukaLeft = 40;
                break;
            case AID.Higanbana:
                sen = SenFlags.None;
                dotLeft = 60;
                break;
            case AID.TenkaGoken:
                sen = SenFlags.None;
                tsubameLeft = 30;
                tsubameRepeat = XanSAM.IaiRepeat.Goken;
                break;
            case AID.MidareSetsugekka:
                sen = SenFlags.None;
                tsubameLeft = 30;
                tsubameRepeat = XanSAM.IaiRepeat.Setsugekka;
                break;
            case AID.TendoGoken:
                sen = SenFlags.None;
                tendoLeft = 0;
                tsubameLeft = 30;
                tsubameRepeat = XanSAM.IaiRepeat.TendoGoken;
                break;
            case AID.TendoSetsugekka:
                sen = SenFlags.None;
                tendoLeft = 0;
                tsubameLeft = 30;
                tsubameRepeat = XanSAM.IaiRepeat.TendoSetsugekka;
                break;
            case AID.KaeshiGoken:
            case AID.KaeshiSetsugekka:
            case AID.TendoKaeshiGoken:
            case AID.TendoKaeshiSetsugekka:
                tsubameLeft = 0;
                tsubameRepeat = XanSAM.IaiRepeat.None;
                break;
            case AID.OgiNamikiri:
                ogiLeft = 0;
                kaeshi = KaeshiAction.Namikiri;
                break;
            case AID.KaeshiNamikiri:
                kaeshi = default;
                break;
        }

        if (meikyoStacks > 0 && action is AID.Yukikaze or AID.Gekko or AID.Kasha or AID.Mangetsu or AID.Oka)
        {
            --meikyoStacks;
            if (meikyoStacks == 0)
            {
                meikyoLeft = 0;
                tendoLeft = 30;
            }
        }

        if (tsubameLeft <= 0)
            tsubameRepeat = XanSAM.IaiRepeat.None;

        return state with
        {
            CombatTimer = state.CombatTimer + (int)Math.Round(elapsed),
            Sen = sen,
            ComboLast = combo,
            MeikyoLeft = meikyoLeft,
            MeikyoStacks = meikyoStacks,
            TendoLeft = tendoLeft,
            TsubameLeft = tsubameLeft,
            TsubameRepeat = tsubameRepeat,
            OgiLeft = ogiLeft,
            Kaeshi = kaeshi,
            TargetHiganbanaLeft = dotLeft,
            FugetsuLeft = fugetsuLeft,
            FukaLeft = fukaLeft
        };
    }

    private static double Decay(double value, double elapsed)
        => value > 0 ? Math.Max(0, value - elapsed) : value;

    private static IReadOnlyList<LiveSequenceCheck> Fast208LiveSequenceChecks()
    {
        return
        [
            new("meikyo_delay_emergency", Fast208Pattern("meikyo_delay_emergency", 4, SenFlags.None, AID.None, 0, fugetsuLeft: 0, fukaLeft: 0), AID.Gyofu, AID.MeikyoShisui),

            new("fast208_opener_01_gekko", Fast208Pattern("fast208_opener_01_gekko", 0, SenFlags.None, AID.None, 0, meikyoLeft: 20, meikyoStacks: 3, fugetsuLeft: 0, fukaLeft: 0), AID.Gekko),
            new("fast208_opener_02_kasha", Fast208Pattern("fast208_opener_02_kasha", 3, SenFlags.Getsu, AID.None, 0, meikyoLeft: 17, meikyoStacks: 2, fugetsuLeft: 40, fukaLeft: 0), AID.Kasha),
            new("fast208_opener_03_yukikaze", Fast208Pattern("fast208_opener_03_yukikaze", 6, SenFlags.Getsu | SenFlags.Ka, AID.None, 0, meikyoLeft: 14, meikyoStacks: 1), AID.Yukikaze),
            new("fast208_opener_04_tendo_setsugekka", Fast208Pattern("fast208_opener_04_tendo_setsugekka", 9, SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, AID.None, 0, tendoLeft: 30), AID.TendoSetsugekka),
            new("fast208_opener_05_tendo_kaeshi", Fast208Pattern("fast208_opener_05_tendo_kaeshi", 12, SenFlags.None, AID.None, 0, tsubameLeft: 30, tsubameRepeat: XanSAM.IaiRepeat.TendoSetsugekka), AID.TendoKaeshiSetsugekka),
            new("fast208_opener_06_gekko", Fast208Pattern("fast208_opener_06_gekko", 15, SenFlags.None, AID.None, 0, meikyoLeft: 20, meikyoStacks: 3, tendoLeft: 30), AID.Gekko),
            new("fast208_opener_07_higanbana", Fast208Pattern("fast208_opener_07_higanbana", 18, SenFlags.Getsu, AID.None, 0, meikyoLeft: 17, meikyoStacks: 2, tendoLeft: 30), AID.Higanbana),
            new("fast208_opener_08_ogi", Fast208Pattern("fast208_opener_08_ogi", 21, SenFlags.None, AID.None, 60, meikyoLeft: 14, meikyoStacks: 2, tendoLeft: 30, ogiLeft: 30), AID.OgiNamikiri),
            new("fast208_opener_09_kaeshi_namikiri", Fast208Pattern("fast208_opener_09_kaeshi_namikiri", 24, SenFlags.None, AID.None, 58, meikyoLeft: 11, meikyoStacks: 2, tendoLeft: 30, kaeshi: KaeshiAction.Namikiri, ogiLeft: 30), AID.KaeshiNamikiri),

            new("fast208_odd_01_kaeshi", Fast208Pattern("fast208_odd_01_kaeshi", 60, SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, AID.None, 60, meikyoLeft: 20, meikyoStacks: 3, tsubameLeft: 30, tsubameRepeat: XanSAM.IaiRepeat.Setsugekka), AID.KaeshiSetsugekka),
            new("fast208_odd_02_tendo_setsugekka", Fast208Pattern("fast208_odd_02_tendo_setsugekka", 62, SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, AID.None, 60, meikyoLeft: 20, meikyoStacks: 3, tendoLeft: 30), AID.TendoSetsugekka),
            new("fast208_odd_03_gekko", Fast208Pattern("fast208_odd_03_gekko", 64, SenFlags.None, AID.None, 0, meikyoLeft: 20, meikyoStacks: 3, tsubameLeft: 30, tsubameRepeat: XanSAM.IaiRepeat.TendoSetsugekka), AID.Gekko),
            new("fast208_odd_04_higanbana", Fast208Pattern("fast208_odd_04_higanbana", 66, SenFlags.Getsu, AID.None, 0, meikyoLeft: 17, meikyoStacks: 2, tsubameLeft: 28, tsubameRepeat: XanSAM.IaiRepeat.TendoSetsugekka), AID.Higanbana),
            new("fast208_odd_05_tendo_kaeshi", Fast208Pattern("fast208_odd_05_tendo_kaeshi", 68, SenFlags.None, AID.None, 60, meikyoLeft: 17, meikyoStacks: 2, tsubameLeft: 26, tsubameRepeat: XanSAM.IaiRepeat.TendoSetsugekka), AID.TendoKaeshiSetsugekka),
            new("fast208_odd_06_kasha", Fast208Pattern("fast208_odd_06_kasha", 70, SenFlags.None, AID.None, 58, meikyoLeft: 14, meikyoStacks: 2), AID.Kasha),
            new("fast208_odd_07_gekko", Fast208Pattern("fast208_odd_07_gekko", 72, SenFlags.Ka, AID.None, 56, meikyoLeft: 11, meikyoStacks: 1), AID.Gekko),
            new("fast208_odd_08_gyofu", Fast208Pattern("fast208_odd_08_gyofu", 74, SenFlags.Getsu | SenFlags.Ka, AID.None, 54), AID.Gyofu),
            new("fast208_odd_09_yukikaze", Fast208Pattern("fast208_odd_09_yukikaze", 76, SenFlags.Getsu | SenFlags.Ka, AID.Gyofu, 52), AID.Yukikaze),
            new("fast208_odd_10_midare", Fast208Pattern("fast208_odd_10_midare", 78, SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, AID.None, 50), AID.MidareSetsugekka),

            new("fast208_even_01_kaeshi", Fast208Pattern("fast208_even_01_kaeshi", 120, SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, AID.None, 60, meikyoLeft: 20, meikyoStacks: 3, tsubameLeft: 30, tsubameRepeat: XanSAM.IaiRepeat.Setsugekka), AID.KaeshiSetsugekka),
            new("fast208_even_02_tendo_setsugekka", Fast208Pattern("fast208_even_02_tendo_setsugekka", 122, SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, AID.None, 60, meikyoLeft: 20, meikyoStacks: 3, tendoLeft: 30), AID.TendoSetsugekka),
            new("fast208_even_03_tendo_kaeshi", Fast208Pattern("fast208_even_03_tendo_kaeshi", 124, SenFlags.None, AID.None, 0, meikyoLeft: 20, meikyoStacks: 3, tsubameLeft: 30, tsubameRepeat: XanSAM.IaiRepeat.TendoSetsugekka), AID.TendoKaeshiSetsugekka),
            new("fast208_even_04_gekko", Fast208Pattern("fast208_even_04_gekko", 126, SenFlags.None, AID.None, 0, meikyoLeft: 20, meikyoStacks: 3), AID.Gekko),
            new("fast208_even_05_higanbana", Fast208Pattern("fast208_even_05_higanbana", 128, SenFlags.Getsu, AID.None, 0, meikyoLeft: 17, meikyoStacks: 2), AID.Higanbana),
            new("fast208_even_06_ogi", Fast208Pattern("fast208_even_06_ogi", 130, SenFlags.None, AID.None, 60, meikyoLeft: 17, meikyoStacks: 2, ogiLeft: 30), AID.OgiNamikiri),
            new("fast208_even_07_kaeshi_namikiri", Fast208Pattern("fast208_even_07_kaeshi_namikiri", 132, SenFlags.None, AID.None, 58, meikyoLeft: 17, meikyoStacks: 2, kaeshi: KaeshiAction.Namikiri, ogiLeft: 30), AID.KaeshiNamikiri),
            new("fast208_even_08_kasha", Fast208Pattern("fast208_even_08_kasha", 134, SenFlags.None, AID.None, 56, meikyoLeft: 14, meikyoStacks: 2), AID.Kasha),
            new("fast208_even_09_gekko", Fast208Pattern("fast208_even_09_gekko", 136, SenFlags.Ka, AID.None, 54, meikyoLeft: 11, meikyoStacks: 1), AID.Gekko),
            new("fast208_even_10_gyofu", Fast208Pattern("fast208_even_10_gyofu", 138, SenFlags.Getsu | SenFlags.Ka, AID.None, 52), AID.Gyofu),
            new("fast208_even_11_yukikaze", Fast208Pattern("fast208_even_11_yukikaze", 140, SenFlags.Getsu | SenFlags.Ka, AID.Gyofu, 50), AID.Yukikaze),
            new("fast208_even_12_midare", Fast208Pattern("fast208_even_12_midare", 142, SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, AID.None, 48), AID.MidareSetsugekka)
        ];
    }

    private static IReadOnlyList<LiveSequenceCheck> AdditionalLiveChecks()
    {
        var aoeBase = Fast208Pattern("aoe", 40, SenFlags.None, AID.None, 60) with { TargetCount = 3, Higanbana = XanSAM.BanaStrategy.Delay };
        var sharifBase = Fast208Pattern("sharif", 60, SenFlags.None, AID.None, 60) with { GcdRoute = XanSAM.GCDRouteStrategy.GCD214 };
        var burstKenkiBase = Fast208Pattern("kenki", 120, SenFlags.None, AID.None, 60) with
        {
            Buffs = AutoForceDelayMode.Auto,
            Kenki = 75,
            SeneiGurenReadyIn = 0,
            ZanshinLeft = 30,
            ZanshinReadyIn = 0
        };
        var blinkBase = Fast208Pattern("blink", 40, SenFlags.None, AID.None, 60) with
        {
            Buffs = AutoForceDelayMode.Auto,
            Kenki = 25,
            IkishotenReadyIn = 120,
            SeneiGurenReadyIn = 120,
            ZanshinReadyIn = 120,
            ShohaReadyIn = 120,
            KenkiSpendReadyIn = 0
        };
        var positionBase = Fast208Pattern("position", 30, SenFlags.None, AID.None, 60, meikyoLeft: 20, meikyoStacks: 3) with
        {
            Opener = XanSAM.OpenerStrategy.GekkoBana,
            GcdRoute = XanSAM.GCDRouteStrategy.GCD214
        };
        var trueNorthBase = Fast208Pattern("true_north", 30, SenFlags.None, AID.Jinpu, 60) with
        {
            Opener = XanSAM.OpenerStrategy.GekkoBana,
            GcdRoute = XanSAM.GCDRouteStrategy.GCD214,
            TrueNorth = XanSAM.TrueNorthStrategy.Auto,
            TrueNorthReadyIn = 0
        };
        var comboPositionBase = Fast208Pattern("combo_position", 30, SenFlags.Setsu, AID.Gyofu, 60) with
        {
            Opener = XanSAM.OpenerStrategy.GekkoBana,
            GcdRoute = XanSAM.GCDRouteStrategy.GCD214
        };
        var rangeBase = Fast208Pattern("range", 40, SenFlags.None, AID.None, 60) with
        {
            Opener = XanSAM.OpenerStrategy.GekkoBana,
            GcdRoute = XanSAM.GCDRouteStrategy.GCD214,
            Higanbana = XanSAM.BanaStrategy.Delay,
            Enpi = XanSAM.EnpiStrategy.Ranged
        };
        var levelSyncBase = Fast208Pattern("level_sync", 120, SenFlags.None, AID.None, 60) with
        {
            Opener = XanSAM.OpenerStrategy.GekkoBana,
            GcdRoute = XanSAM.GCDRouteStrategy.GCD214,
            Higanbana = XanSAM.BanaStrategy.Delay
        };
        var strategyBase = Fast208Pattern("strategy", 40, SenFlags.None, AID.None, 60) with
        {
            Opener = XanSAM.OpenerStrategy.GekkoBana,
            GcdRoute = XanSAM.GCDRouteStrategy.GCD214,
            Higanbana = XanSAM.BanaStrategy.Delay
        };
        var senRecoveryBase = Fast208Pattern("sen_recovery", 52, SenFlags.None, AID.None, 60) with
        {
            Opener = XanSAM.OpenerStrategy.GekkoBana,
            GcdRoute = XanSAM.GCDRouteStrategy.GCD214,
            Higanbana = XanSAM.BanaStrategy.Delay,
            HagakureReadyIn = 0
        };
        var kenkiForecastBase = Fast208Pattern("kenki_forecast", 50, SenFlags.None, AID.None, 60) with
        {
            Opener = XanSAM.OpenerStrategy.GekkoBana,
            GcdRoute = XanSAM.GCDRouteStrategy.GCD214,
            Higanbana = XanSAM.BanaStrategy.Delay,
            Buffs = AutoForceDelayMode.Auto,
            KenkiSpendReadyIn = 0
        };
        var higanbanaTargetBase = Fast208Pattern("higanbana_target", 40, SenFlags.Getsu, AID.None, 0) with
        {
            TargetCount = 2,
            Opener = XanSAM.OpenerStrategy.GekkoBana,
            GcdRoute = XanSAM.GCDRouteStrategy.GCD214,
            Higanbana = XanSAM.BanaStrategy.Automatic
        };

        return
        [
            new("aoe_01_fuko", aoeBase with { Name = "aoe_01_fuko" }, AID.Fuko),
            new("aoe_02_mangetsu_missing_moon", aoeBase with { Name = "aoe_02_mangetsu_missing_moon", Sen = SenFlags.Ka, ComboLast = AID.Fuko }, AID.Mangetsu),
            new("aoe_03_oka_missing_flower", aoeBase with { Name = "aoe_03_oka_missing_flower", Sen = SenFlags.Getsu, ComboLast = AID.Fuko }, AID.Oka),
            new("aoe_04_tenka_three_targets", aoeBase with { Name = "aoe_04_tenka_three_targets", Sen = SenFlags.Getsu | SenFlags.Ka }, AID.TenkaGoken),
            new("aoe_05_tendo_goken_three_targets", aoeBase with { Name = "aoe_05_tendo_goken_three_targets", Sen = SenFlags.Getsu | SenFlags.Ka, TendoLeft = 30 }, AID.TendoGoken),
            new("aoe_06_tendo_kaeshi_goken", aoeBase with { Name = "aoe_06_tendo_kaeshi_goken", TsubameLeft = 30, TsubameRepeat = XanSAM.IaiRepeat.TendoGoken }, AID.TendoKaeshiGoken),
            new("aoe_07_return_to_single", aoeBase with { Name = "aoe_07_return_to_single", TargetCount = 1, Sen = SenFlags.Ka, ComboLast = AID.Fuko }, AID.Gyofu),
            new("ranged_01_enpi", Fast208Pattern("ranged_01_enpi", 40, SenFlags.None, AID.None, 60) with { CanMelee = false, Enpi = XanSAM.EnpiStrategy.Ranged }, AID.Enpi),
            new("movement_01_return_to_melee_uses_gyofu", Fast208Pattern("movement_01_return_to_melee_uses_gyofu", 40, SenFlags.None, AID.None, 60) with { CanMelee = true, Enpi = XanSAM.EnpiStrategy.Ranged }, AID.Gyofu),
            new("movement_02_out_of_melee_none_does_not_queue_melee_gcd", Fast208Pattern("movement_02_out_of_melee_none_does_not_queue_melee_gcd", 40, SenFlags.None, AID.None, 60) with { CanMelee = false, Enpi = XanSAM.EnpiStrategy.None }, AID.None),
            new("target_switch_01_unavailable_primary_uses_secondary", Fast208Pattern("target_switch_01_unavailable_primary_uses_secondary", 40, SenFlags.None, AID.None, 60) with { TargetCount = 2, PrimaryTargetableOverride = false }, AID.Gyofu, ExpectedGCDTargetIndex: 1),
            new("range_01_midare_inside_six", rangeBase with { Name = "range_01_midare_inside_six", Sen = SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, TargetHitboxDistanceOverride = 5.5f }, AID.MidareSetsugekka),
            new("range_02_midare_outside_six_uses_enpi", rangeBase with { Name = "range_02_midare_outside_six_uses_enpi", Sen = SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, TargetHitboxDistanceOverride = 6.5f }, AID.Enpi, AID.MidareSetsugekka),
            new("range_03_ogi_inside_eight", rangeBase with { Name = "range_03_ogi_inside_eight", OgiLeft = 30, TargetHitboxDistanceOverride = 7.5f }, AID.OgiNamikiri),
            new("range_04_ogi_outside_eight_uses_enpi", rangeBase with { Name = "range_04_ogi_outside_eight_uses_enpi", OgiLeft = 30, TargetHitboxDistanceOverride = 8.5f }, AID.Enpi, AID.OgiNamikiri),
            new("range_05_shoha_inside_ten", rangeBase with { Name = "range_05_shoha_inside_ten", Meditation = 3, Buffs = AutoForceDelayMode.Auto, ShohaReadyIn = 0, TargetHitboxDistanceOverride = 9.5f }, AID.Enpi, ExpectedOGCD: AID.Shoha),
            new("range_06_shoha_outside_ten", rangeBase with { Name = "range_06_shoha_outside_ten", Meditation = 3, Buffs = AutoForceDelayMode.Auto, ShohaReadyIn = 0, TargetHitboxDistanceOverride = 10.5f }, AID.Enpi, ExpectedOGCD: AID.None),
            new("range_07_guren_inside_ten", rangeBase with { Name = "range_07_guren_inside_ten", Kenki = 25, Buffs = AutoForceDelayMode.Auto, SeneiGurenReadyIn = 0, TargetHitboxDistanceOverride = 9.5f }, AID.Enpi, ExpectedOGCD: AID.HissatsuGuren),
            new("range_08_guren_outside_ten", rangeBase with { Name = "range_08_guren_outside_ten", Kenki = 25, Buffs = AutoForceDelayMode.Auto, SeneiGurenReadyIn = 0, TargetHitboxDistanceOverride = 10.5f }, AID.Enpi, ExpectedOGCD: AID.None),
            new("range_09_kaeshi_namikiri_outside_eight_uses_enpi", rangeBase with { Name = "range_09_kaeshi_namikiri_outside_eight_uses_enpi", Kaeshi = KaeshiAction.Namikiri, OgiLeft = 30, TargetHitboxDistanceOverride = 8.5f }, AID.Enpi, AID.KaeshiNamikiri),
            new("level_sync_01_lv30_uses_hakaze", levelSyncBase with { Name = "level_sync_01_lv30_uses_hakaze", Level = 30 }, AID.Hakaze, AID.Gyofu),
            new("level_sync_02_lv85_uses_fuga", levelSyncBase with { Name = "level_sync_02_lv85_uses_fuga", Level = 85, TargetCount = 3 }, AID.Fuga, AID.Fuko),
            new("level_sync_03_lv86_uses_fuko", levelSyncBase with { Name = "level_sync_03_lv86_uses_fuko", Level = 86, TargetCount = 3 }, AID.Fuko),
            new("level_sync_04_lv71_uses_guren", levelSyncBase with { Name = "level_sync_04_lv71_uses_guren", Level = 71, Kenki = 25, Buffs = AutoForceDelayMode.Auto, SeneiGurenReadyIn = 0 }, AID.Hakaze, AID.HissatsuSenei, AID.HissatsuGuren),
            new("level_sync_05_lv72_uses_senei", levelSyncBase with { Name = "level_sync_05_lv72_uses_senei", Level = 72, Kenki = 25, Buffs = AutoForceDelayMode.Auto, SeneiGurenReadyIn = 0 }, AID.Hakaze, ExpectedOGCD: AID.HissatsuSenei),
            new("level_sync_06_lv79_does_not_use_shoha", levelSyncBase with { Name = "level_sync_06_lv79_does_not_use_shoha", Level = 79, Meditation = 3, Buffs = AutoForceDelayMode.Auto, ShohaReadyIn = 0 }, AID.Hakaze, AID.Shoha, AID.None),
            new("level_sync_07_lv80_uses_shoha", levelSyncBase with { Name = "level_sync_07_lv80_uses_shoha", Level = 80, Meditation = 3, Buffs = AutoForceDelayMode.Auto, ShohaReadyIn = 0 }, AID.Hakaze, ExpectedOGCD: AID.Shoha),
            new("level_sync_08_lv89_does_not_use_ogi", levelSyncBase with { Name = "level_sync_08_lv89_does_not_use_ogi", Level = 89, OgiLeft = 30 }, AID.Hakaze, AID.OgiNamikiri),
            new("level_sync_09_lv90_uses_ogi", levelSyncBase with { Name = "level_sync_09_lv90_uses_ogi", Level = 90, OgiLeft = 30 }, AID.OgiNamikiri),
            new("level_sync_10_lv95_does_not_use_zanshin", levelSyncBase with { Name = "level_sync_10_lv95_does_not_use_zanshin", Level = 95, Kenki = 50, ZanshinLeft = 1, ZanshinReadyIn = 0, Buffs = AutoForceDelayMode.Auto }, AID.Gyofu, AID.Zanshin, AID.None),
            new("level_sync_11_lv96_uses_zanshin", levelSyncBase with { Name = "level_sync_11_lv96_uses_zanshin", Level = 96, Kenki = 50, ZanshinLeft = 1, ZanshinReadyIn = 0, Buffs = AutoForceDelayMode.Auto }, AID.Gyofu, ExpectedOGCD: AID.Zanshin),
            new("level_sync_12_lv99_uses_midare", levelSyncBase with { Name = "level_sync_12_lv99_uses_midare", Level = 99, Sen = SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, TendoLeft = 30 }, AID.MidareSetsugekka, AID.TendoSetsugekka),
            new("level_sync_13_lv100_uses_tendo", levelSyncBase with { Name = "level_sync_13_lv100_uses_tendo", Level = 100, Sen = SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, TendoLeft = 30 }, AID.TendoSetsugekka),
            new("strategy_01_buffs_delay_blocks_ikishoten", strategyBase with { Name = "strategy_01_buffs_delay_blocks_ikishoten", Buffs = AutoForceDelayMode.Delay, IkishotenReadyIn = 0 }, AID.Gyofu, AID.Ikishoten),
            new("strategy_02_buffs_delay_blocks_senei", strategyBase with { Name = "strategy_02_buffs_delay_blocks_senei", Buffs = AutoForceDelayMode.Delay, Kenki = 25, SeneiGurenReadyIn = 0 }, AID.Gyofu, AID.HissatsuSenei),
            new("strategy_03_buffs_delay_blocks_zanshin", strategyBase with { Name = "strategy_03_buffs_delay_blocks_zanshin", Buffs = AutoForceDelayMode.Delay, Kenki = 50, ZanshinLeft = 1, ZanshinReadyIn = 0 }, AID.Gyofu, AID.Zanshin),
            new("strategy_04_buffs_delay_blocks_shoha", strategyBase with { Name = "strategy_04_buffs_delay_blocks_shoha", Buffs = AutoForceDelayMode.Delay, Meditation = 3, ShohaReadyIn = 0 }, AID.Gyofu, AID.Shoha),
            new("strategy_05_tsubame_delay_blocks_kaeshi", strategyBase with { Name = "strategy_05_tsubame_delay_blocks_kaeshi", Tsubame = XanSAM.TsubameStrategy.Delay, TsubameLeft = 30, TsubameRepeat = XanSAM.IaiRepeat.Setsugekka }, AID.Gyofu, AID.KaeshiSetsugekka),
            new("strategy_06_tsubame_force_uses_kaeshi", strategyBase with { Name = "strategy_06_tsubame_force_uses_kaeshi", Tsubame = XanSAM.TsubameStrategy.Force, TsubameLeft = 30, TsubameRepeat = XanSAM.IaiRepeat.Setsugekka }, AID.KaeshiSetsugekka),
            new("strategy_07_tsubame_hold_waits", strategyBase with { Name = "strategy_07_tsubame_hold_waits", Tsubame = XanSAM.TsubameStrategy.Hold, TsubameLeft = 30, TsubameRepeat = XanSAM.IaiRepeat.Setsugekka }, AID.Gyofu, AID.KaeshiSetsugekka),
            new("strategy_08_tsubame_hold_uses_before_expiry", strategyBase with { Name = "strategy_08_tsubame_hold_uses_before_expiry", Tsubame = XanSAM.TsubameStrategy.Hold, TsubameLeft = 1, TsubameRepeat = XanSAM.IaiRepeat.Setsugekka }, AID.KaeshiSetsugekka),
            new("strategy_09_namikiri_delay_blocks_ogi", strategyBase with { Name = "strategy_09_namikiri_delay_blocks_ogi", Namikiri = XanSAM.NamikiriStrategy.Delay, OgiLeft = 30 }, AID.Gyofu, AID.OgiNamikiri),
            new("strategy_10_namikiri_force_uses_ogi", strategyBase with { Name = "strategy_10_namikiri_force_uses_ogi", Namikiri = XanSAM.NamikiriStrategy.Force, OgiLeft = 30 }, AID.OgiNamikiri),
            new("strategy_11_namikiri_hold_waits", strategyBase with { Name = "strategy_11_namikiri_hold_waits", Namikiri = XanSAM.NamikiriStrategy.Hold, OgiLeft = 30 }, AID.Gyofu, AID.OgiNamikiri),
            new("strategy_12_namikiri_hold_uses_before_expiry", strategyBase with { Name = "strategy_12_namikiri_hold_uses_before_expiry", Namikiri = XanSAM.NamikiriStrategy.Hold, OgiLeft = 1 }, AID.OgiNamikiri),
            new("strategy_13_higanbana_delay_blocks", strategyBase with { Name = "strategy_13_higanbana_delay_blocks", Higanbana = XanSAM.BanaStrategy.Delay, Sen = SenFlags.Getsu, TargetHiganbanaLeft = 0 }, AID.Gyofu, AID.Higanbana),
            new("strategy_14_higanbana_force_uses", strategyBase with { Name = "strategy_14_higanbana_force_uses", Higanbana = XanSAM.BanaStrategy.Force, Sen = SenFlags.Getsu, TargetHiganbanaLeft = 60 }, AID.Higanbana),
            new("strategy_15_meikyo_delay_blocks", strategyBase with { Name = "strategy_15_meikyo_delay_blocks", Meikyo = XanSAM.MeikyoStrategy.Delay, MeikyoCharges = 2, MeikyoReadyIn = 0 }, AID.Gyofu, AID.MeikyoShisui),
            new("strategy_16_potion_none_blocks", strategyBase with { Name = "strategy_16_potion_none_blocks", CombatTimer = 120, Potion = XanSAM.SamPotionStrategy.None, OgiLeft = 30 }, AID.OgiNamikiri, ExpectedPotion: false),
            new("strategy_17_potion_two_minute_uses", strategyBase with { Name = "strategy_17_potion_two_minute_uses", CombatTimer = 120, Potion = XanSAM.SamPotionStrategy.TwoMinuteBurst, OgiLeft = 30 }, AID.OgiNamikiri, ExpectedPotion: true),
            new("strategy_18_namikiri_delay_blocks_kaeshi", strategyBase with { Name = "strategy_18_namikiri_delay_blocks_kaeshi", Namikiri = XanSAM.NamikiriStrategy.Delay, Kaeshi = KaeshiAction.Namikiri, OgiLeft = 30 }, AID.Gyofu, AID.KaeshiNamikiri),
            new("strategy_19_namikiri_force_uses_kaeshi", strategyBase with { Name = "strategy_19_namikiri_force_uses_kaeshi", Namikiri = XanSAM.NamikiriStrategy.Force, Kaeshi = KaeshiAction.Namikiri, OgiLeft = 30 }, AID.KaeshiNamikiri),
            new("strategy_20_namikiri_hold_waits_with_kaeshi", strategyBase with { Name = "strategy_20_namikiri_hold_waits_with_kaeshi", Namikiri = XanSAM.NamikiriStrategy.Hold, Kaeshi = KaeshiAction.Namikiri, OgiLeft = 30 }, AID.Gyofu, AID.KaeshiNamikiri),
            new("strategy_21_namikiri_hold_uses_kaeshi_before_expiry", strategyBase with { Name = "strategy_21_namikiri_hold_uses_kaeshi_before_expiry", Namikiri = XanSAM.NamikiriStrategy.Hold, Kaeshi = KaeshiAction.Namikiri, OgiLeft = 1 }, AID.KaeshiNamikiri),
            new("sen_01_meikyo_moon_held_uses_kasha", senRecoveryBase with { Name = "sen_01_meikyo_moon_held_uses_kasha", Sen = SenFlags.Setsu | SenFlags.Getsu, MeikyoLeft = 20, MeikyoStacks = 3 }, AID.Kasha, AID.Gekko),
            new("sen_02_meikyo_flower_held_uses_gekko", senRecoveryBase with { Name = "sen_02_meikyo_flower_held_uses_gekko", Sen = SenFlags.Setsu | SenFlags.Ka, MeikyoLeft = 20, MeikyoStacks = 3 }, AID.Gekko, AID.Kasha),
            new("sen_03_meikyo_moon_flower_uses_yukikaze", senRecoveryBase with { Name = "sen_03_meikyo_moon_flower_uses_yukikaze", Sen = SenFlags.Getsu | SenFlags.Ka, MeikyoLeft = 20, MeikyoStacks = 3 }, AID.Yukikaze),
            new("sen_04_jinpu_moon_held_suppresses_gekko", senRecoveryBase with { Name = "sen_04_jinpu_moon_held_suppresses_gekko", Sen = SenFlags.Getsu, ComboLast = AID.Jinpu }, AID.Gyofu, AID.Gekko),
            new("sen_05_jinpu_moon_missing_uses_gekko", senRecoveryBase with { Name = "sen_05_jinpu_moon_missing_uses_gekko", ComboLast = AID.Jinpu }, AID.Gekko),
            new("sen_06_jinpu_allows_duplicate_for_expiring_fugetsu", senRecoveryBase with { Name = "sen_06_jinpu_allows_duplicate_for_expiring_fugetsu", Sen = SenFlags.Getsu, ComboLast = AID.Jinpu, FugetsuLeft = 0 }, AID.Gekko),
            new("sen_07_shifu_flower_held_suppresses_kasha", senRecoveryBase with { Name = "sen_07_shifu_flower_held_suppresses_kasha", Sen = SenFlags.Ka, ComboLast = AID.Shifu }, AID.Gyofu, AID.Kasha),
            new("sen_08_shifu_flower_missing_uses_kasha", senRecoveryBase with { Name = "sen_08_shifu_flower_missing_uses_kasha", ComboLast = AID.Shifu }, AID.Kasha),
            new("sen_09_shifu_allows_duplicate_for_expiring_fuka", senRecoveryBase with { Name = "sen_09_shifu_allows_duplicate_for_expiring_fuka", Sen = SenFlags.Ka, ComboLast = AID.Shifu, FukaLeft = 0 }, AID.Kasha),
            new("sen_10_aoe_moon_held_builds_flower", senRecoveryBase with { Name = "sen_10_aoe_moon_held_builds_flower", TargetCount = 3, Sen = SenFlags.Getsu, ComboLast = AID.Fuko }, AID.Oka, AID.Mangetsu),
            new("sen_11_aoe_flower_held_builds_moon", senRecoveryBase with { Name = "sen_11_aoe_flower_held_builds_moon", TargetCount = 3, Sen = SenFlags.Ka, ComboLast = AID.Fuko }, AID.Mangetsu, AID.Oka),
            new("hagakure_01_aoe_wrong_three_sen_recovers", senRecoveryBase with { Name = "hagakure_01_aoe_wrong_three_sen_recovers", TargetCount = 3, Sen = SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, ComboLast = AID.Fuko }, AID.None, ExpectedOGCD: AID.Hagakure),
            new("hagakure_02_aoe_desired_two_sen_uses_tenka", senRecoveryBase with { Name = "hagakure_02_aoe_desired_two_sen_uses_tenka", TargetCount = 3, Sen = SenFlags.Getsu | SenFlags.Ka }, AID.TenkaGoken, AID.Hagakure),
            new("hagakure_03_single_three_sen_uses_midare", senRecoveryBase with { Name = "hagakure_03_single_three_sen_uses_midare", Sen = SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka }, AID.MidareSetsugekka, AID.Hagakure),
            new("hagakure_04_single_one_sen_continues_normal_route", senRecoveryBase with { Name = "hagakure_04_single_one_sen_continues_normal_route", Sen = SenFlags.Getsu }, AID.Gyofu, AID.Hagakure),
            new("kenki_forecast_01_exact_cap_holds", kenkiForecastBase with { Name = "kenki_forecast_01_exact_cap_holds", Kenki = 75 }, AID.Gyofu, ExpectedOGCD: AID.None),
            new("kenki_forecast_02_future_overcap_uses_shinten", kenkiForecastBase with { Name = "kenki_forecast_02_future_overcap_uses_shinten", Kenki = 80 }, AID.Gyofu, ExpectedOGCD: AID.HissatsuShinten),
            new("kenki_forecast_03_aoe_future_overcap_uses_kyuten", kenkiForecastBase with { Name = "kenki_forecast_03_aoe_future_overcap_uses_kyuten", TargetCount = 3, Kenki = 65 }, AID.Fuko, ExpectedOGCD: AID.HissatsuKyuten),
            new("kenki_forecast_04_current_cap_uses_shinten", kenkiForecastBase with { Name = "kenki_forecast_04_current_cap_uses_shinten", Kenki = 100 }, AID.Gyofu, ExpectedOGCD: AID.HissatsuShinten),
            new("kenki_forecast_05_spends_before_upcoming_ikishoten", kenkiForecastBase with { Name = "kenki_forecast_05_spends_before_upcoming_ikishoten", CombatTimer = 110, Kenki = 60, IkishotenReadyIn = 3 }, AID.Gyofu, ExpectedOGCD: AID.HissatsuShinten),
            new("meditation_01_shoha_prevents_next_gcd_overcap", kenkiForecastBase with { Name = "meditation_01_shoha_prevents_next_gcd_overcap", CombatTimer = 120, Sen = SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, Kenki = 75, Meditation = 3, SeneiGurenReadyIn = 0, ZanshinLeft = 30, ZanshinReadyIn = 0, ShohaReadyIn = 0 }, AID.MidareSetsugekka, ExpectedOGCD: AID.Shoha),
            new("meditation_02_senei_precedes_shoha_without_gcd_overcap", kenkiForecastBase with { Name = "meditation_02_senei_precedes_shoha_without_gcd_overcap", CombatTimer = 120, Kenki = 75, Meditation = 3, SeneiGurenReadyIn = 0, ZanshinLeft = 30, ZanshinReadyIn = 0, ShohaReadyIn = 0 }, AID.Gyofu, ExpectedOGCD: AID.HissatsuSenei),
            new("higanbana_01_selects_undotted_secondary", higanbanaTargetBase with { Name = "higanbana_01_selects_undotted_secondary", TargetHiganbanaLeft = 50, SecondaryHiganbanaLeft = 0 }, AID.Higanbana, ExpectedGCDTargetIndex: 1),
            new("higanbana_02_selects_undotted_primary", higanbanaTargetBase with { Name = "higanbana_02_selects_undotted_primary", TargetHiganbanaLeft = 0, SecondaryHiganbanaLeft = 50 }, AID.Higanbana, ExpectedGCDTargetIndex: 0),
            new("higanbana_03_does_not_refresh_early", higanbanaTargetBase with { Name = "higanbana_03_does_not_refresh_early", TargetHiganbanaLeft = 50, SecondaryHiganbanaLeft = 50 }, AID.Gyofu, AID.Higanbana),
            new("higanbana_04_uses_with_fugetsu_without_fuka", higanbanaTargetBase with { Name = "higanbana_04_uses_with_fugetsu_without_fuka", FukaLeft = 0 }, AID.Higanbana),
            new("higanbana_05_waits_without_fugetsu", higanbanaTargetBase with { Name = "higanbana_05_waits_without_fugetsu", FugetsuLeft = 0 }, AID.Gyofu, AID.Higanbana),
            new("higanbana_06_refreshes_at_one_second", higanbanaTargetBase with { Name = "higanbana_06_refreshes_at_one_second", TargetHiganbanaLeft = 1, SecondaryHiganbanaLeft = 50 }, AID.Higanbana),
            new("higanbana_07_waits_at_three_seconds", higanbanaTargetBase with { Name = "higanbana_07_waits_at_three_seconds", TargetHiganbanaLeft = 3, SecondaryHiganbanaLeft = 50 }, AID.Gyofu, AID.Higanbana),
            new("higanbana_08_held_tsubame_does_not_block_refresh", higanbanaTargetBase with { Name = "higanbana_08_held_tsubame_does_not_block_refresh", CombatTimer = 50, TargetHiganbanaLeft = 1, SecondaryHiganbanaLeft = 50, TsubameLeft = 30, TsubameRepeat = XanSAM.IaiRepeat.Setsugekka }, AID.Higanbana),
            new("higanbana_09_expiring_tsubame_precedes_existing_dot_refresh", higanbanaTargetBase with { Name = "higanbana_09_expiring_tsubame_precedes_existing_dot_refresh", TargetHiganbanaLeft = 1, SecondaryHiganbanaLeft = 50, TsubameLeft = 1, TsubameRepeat = XanSAM.IaiRepeat.Setsugekka }, AID.KaeshiSetsugekka),

            new("sharif_01_use_held_kaeshi_in_burst", sharifBase with { Name = "sharif_01_use_held_kaeshi_in_burst", TsubameLeft = 30, TsubameRepeat = XanSAM.IaiRepeat.Setsugekka }, AID.KaeshiSetsugekka),
            new("sharif_02_higanbana_before_held_kaeshi", sharifBase with { Name = "sharif_02_higanbana_before_held_kaeshi", Sen = SenFlags.Getsu, TargetHiganbanaLeft = 0, TsubameLeft = 30, TsubameRepeat = XanSAM.IaiRepeat.Setsugekka }, AID.Higanbana),
            new("sharif_03_hold_kaeshi_before_burst", sharifBase with { Name = "sharif_03_hold_kaeshi_before_burst", CombatTimer = 100, TsubameLeft = 30, TsubameRepeat = XanSAM.IaiRepeat.Setsugekka }, AID.Gyofu, AID.KaeshiSetsugekka),
            new("sharif_04_prepare_snow", sharifBase with { Name = "sharif_04_prepare_snow", CombatTimer = 110, Sen = SenFlags.Getsu | SenFlags.Ka, ComboLast = AID.Gyofu, TargetHiganbanaLeft = 20 }, AID.Yukikaze),
            new("sharif_05_odd_tendo_at_dot_26", sharifBase with { Name = "sharif_05_odd_tendo_at_dot_26", Sen = SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, TendoLeft = 30, TargetHiganbanaLeft = 26 }, AID.TendoSetsugekka),
            new("sharif_06_odd_build_bana_sen_before_kaeshi", sharifBase with { Name = "sharif_06_odd_build_bana_sen_before_kaeshi", TargetHiganbanaLeft = 5, MeikyoLeft = 20, MeikyoStacks = 3, TsubameLeft = 30, TsubameRepeat = XanSAM.IaiRepeat.TendoSetsugekka }, AID.Kasha),
            new("sharif_07_odd_bana_before_tendo_kaeshi", sharifBase with { Name = "sharif_07_odd_bana_before_tendo_kaeshi", Sen = SenFlags.Ka, TargetHiganbanaLeft = 1.5, MeikyoLeft = 17, MeikyoStacks = 2, TsubameLeft = 28, TsubameRepeat = XanSAM.IaiRepeat.TendoSetsugekka }, AID.Higanbana),
            new("sharif_08_odd_tendo_kaeshi_after_bana", sharifBase with { Name = "sharif_08_odd_tendo_kaeshi_after_bana", TargetHiganbanaLeft = 60, TsubameLeft = 26, TsubameRepeat = XanSAM.IaiRepeat.TendoSetsugekka }, AID.TendoKaeshiSetsugekka),
            new("sharif_09_even_dot_24_snow_entry", sharifBase with { Name = "sharif_09_even_dot_24_snow_entry", CombatTimer = 110, Sen = SenFlags.Getsu | SenFlags.Ka, ComboLast = AID.Gyofu, TargetHiganbanaLeft = 24 }, AID.Yukikaze),
            new("sharif_10_even_dot_20_meikyo_getsu", sharifBase with { Name = "sharif_10_even_dot_20_meikyo_getsu", CombatTimer = 120, Sen = SenFlags.Setsu | SenFlags.Ka, TargetHiganbanaLeft = 20, MeikyoLeft = 20, MeikyoStacks = 3 }, AID.Gekko),
            new("sharif_11_even_dot_16_meikyo_ka", sharifBase with { Name = "sharif_11_even_dot_16_meikyo_ka", CombatTimer = 120, Sen = SenFlags.Setsu | SenFlags.Getsu, TargetHiganbanaLeft = 16, MeikyoLeft = 17, MeikyoStacks = 2 }, AID.Kasha),
            new("sharif_12_even_tendo_setsugekka", sharifBase with { Name = "sharif_12_even_tendo_setsugekka", CombatTimer = 120, Sen = SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka, TargetHiganbanaLeft = 12, TendoLeft = 30 }, AID.TendoSetsugekka),
            new("sharif_13_even_bana_before_tendo_kaeshi", sharifBase with { Name = "sharif_13_even_bana_before_tendo_kaeshi", CombatTimer = 120, Sen = SenFlags.Getsu, TargetHiganbanaLeft = 0, TsubameLeft = 30, TsubameRepeat = XanSAM.IaiRepeat.TendoSetsugekka }, AID.Higanbana),
            new("sharif_14_even_tendo_kaeshi_after_bana", sharifBase with { Name = "sharif_14_even_tendo_kaeshi_after_bana", CombatTimer = 120, TargetHiganbanaLeft = 60, TsubameLeft = 28, TsubameRepeat = XanSAM.IaiRepeat.TendoSetsugekka }, AID.TendoKaeshiSetsugekka),

            new("kenki_01_senei_before_zanshin", burstKenkiBase with { Name = "kenki_01_senei_before_zanshin" }, AID.Gyofu, AID.HissatsuShinten, AID.HissatsuSenei),
            new("kenki_02_guren_before_zanshin", burstKenkiBase with { Name = "kenki_02_guren_before_zanshin", TargetCount = 3 }, AID.Fuko, AID.HissatsuKyuten, AID.HissatsuGuren),
            new("kenki_03_senei_reserves_zanshin", burstKenkiBase with { Name = "kenki_03_senei_reserves_zanshin", Kenki = 50 }, AID.Gyofu, AID.Zanshin, AID.HissatsuSenei),
            new("kenki_04_expiring_zanshin_without_fugetsu", burstKenkiBase with { Name = "kenki_04_expiring_zanshin_without_fugetsu", Kenki = 50, FugetsuLeft = 0, ZanshinLeft = 1, SeneiGurenReadyIn = 120 }, AID.Gyofu, ExpectedOGCD: AID.Zanshin),

            new("meikyo_01_two_charges_mid_combo", Fast208Pattern("meikyo_01_two_charges_mid_combo", 40, SenFlags.None, AID.Gyofu, 60) with { Meikyo = XanSAM.MeikyoStrategy.Auto, MeikyoCharges = 2, MeikyoReadyIn = 0 }, AID.Yukikaze, ExpectedOGCD: AID.MeikyoShisui),
            new("meikyo_02_self_prep_without_target", Fast208Pattern("meikyo_02_self_prep_without_target", 120, SenFlags.None, AID.None, 0) with { HaveTarget = false, Targetable = false, TargetCount = 0, CanMelee = false, Meikyo = XanSAM.MeikyoStrategy.Auto, MeikyoCharges = 2, MeikyoReadyIn = 0 }, AID.None, ExpectedOGCD: AID.MeikyoShisui),
            new("potion_01_no_target_no_use", Fast208Pattern("potion_01_no_target_no_use", 120, SenFlags.None, AID.None, 0) with { HaveTarget = false, Targetable = false, TargetCount = 0, CanMelee = false, Potion = XanSAM.SamPotionStrategy.TwoMinuteBurst }, AID.None, ExpectedOGCD: AID.None),
            new("ogi_01_expiring_without_fugetsu", Fast208Pattern("ogi_01_expiring_without_fugetsu", 40, SenFlags.None, AID.None, 60, ogiLeft: 1, fugetsuLeft: 0, fukaLeft: 0), AID.OgiNamikiri),
            new("ogi_02_no_target_no_use", Fast208Pattern("ogi_02_no_target_no_use", 40, SenFlags.None, AID.None, 0, ogiLeft: 30) with { HaveTarget = false, Targetable = false, TargetCount = 0, CanMelee = false }, AID.None),
            new("tsubame_01_goken_no_targets", Fast208Pattern("tsubame_01_goken_no_targets", 40, SenFlags.None, AID.None, 0, tsubameLeft: 30, tsubameRepeat: XanSAM.IaiRepeat.Goken) with { HaveTarget = false, Targetable = false, TargetCount = 0, CanMelee = false }, AID.None),
            new("blink_01_recent_gyoten_reserves_spent_kenki", blinkBase with { Name = "blink_01_recent_gyoten_reserves_spent_kenki", RecentAction = AID.HissatsuGyoten, RecentActionAge = 0.5 }, AID.Gyofu, ExpectedOGCD: AID.None),
            new("blink_02_stale_gyoten_does_not_reduce_kenki", blinkBase with { Name = "blink_02_stale_gyoten_does_not_reduce_kenki", RecentAction = AID.HissatsuGyoten, RecentActionAge = 1.5 }, AID.Gyofu, ExpectedOGCD: AID.HissatsuShinten),
            new("blink_03_recent_yaten_enables_enpi", Fast208Pattern("blink_03_recent_yaten_enables_enpi", 40, SenFlags.None, AID.None, 60) with { CanMelee = false, Enpi = XanSAM.EnpiStrategy.Enhanced, RecentAction = AID.HissatsuYaten, RecentActionAge = 0.5 }, AID.Enpi),
            new("blink_04_stale_yaten_does_not_enable_enpi", Fast208Pattern("blink_04_stale_yaten_does_not_enable_enpi", 40, SenFlags.None, AID.None, 60) with { CanMelee = false, Enpi = XanSAM.EnpiStrategy.Enhanced, RecentAction = AID.HissatsuYaten, RecentActionAge = 1.5 }, AID.None),

            new("position_01_rear_prefers_gekko", positionBase with { Name = "position_01_rear_prefers_gekko", TargetRotation = MathF.PI / 2 }, AID.Gekko),
            new("position_02_flank_prefers_kasha", positionBase with { Name = "position_02_flank_prefers_kasha", TargetRotation = 0 }, AID.Kasha),
            new("position_03_rear_prefers_jinpu_route", comboPositionBase with { Name = "position_03_rear_prefers_jinpu_route", TargetRotation = MathF.PI / 2 }, AID.Jinpu),
            new("position_04_flank_prefers_shifu_route", comboPositionBase with { Name = "position_04_flank_prefers_shifu_route", TargetRotation = 0 }, AID.Shifu),
            new("position_05_omnidirectional_uses_existing_meikyo_order", positionBase with { Name = "position_05_omnidirectional_uses_existing_meikyo_order", TargetRotation = 0, TargetOmnidirectional = true }, AID.Kasha),
            new("true_north_01_correct_rear_does_not_use", trueNorthBase with { Name = "true_north_01_correct_rear_does_not_use", TargetRotation = MathF.PI / 2 }, AID.Gekko, ExpectedOGCD: AID.None),
            new("true_north_02_wrong_position_uses", trueNorthBase with { Name = "true_north_02_wrong_position_uses", TargetRotation = 0 }, AID.Gekko, ExpectedOGCD: AID.TrueNorth),
            new("true_north_03_wrong_position_waits_at_one_second", trueNorthBase with { Name = "true_north_03_wrong_position_waits_at_one_second", TargetRotation = 0, GCDReadyIn = 1 }, AID.Gekko, ExpectedOGCD: AID.None),
            new("true_north_04_wrong_position_uses_at_half_second", trueNorthBase with { Name = "true_north_04_wrong_position_uses_at_half_second", TargetRotation = 0, GCDReadyIn = 0.5 }, AID.Gekko, ExpectedOGCD: AID.TrueNorth),
            new("true_north_05_none_never_uses", trueNorthBase with { Name = "true_north_05_none_never_uses", TargetRotation = 0, TrueNorth = XanSAM.TrueNorthStrategy.None }, AID.Gekko, ExpectedOGCD: AID.None),
            new("true_north_06_omnidirectional_never_uses", trueNorthBase with { Name = "true_north_06_omnidirectional_never_uses", TargetRotation = 0, TargetOmnidirectional = true }, AID.Gekko, ExpectedOGCD: AID.None)
        ];
    }

    private static RealSamPattern Fast208Pattern(
        string name,
        int combatTimer,
        SenFlags sen,
        AID comboLast,
        double targetHiganbanaLeft,
        double meikyoLeft = 0,
        int meikyoStacks = 0,
        double tendoLeft = 0,
        double tsubameLeft = 0,
        XanSAM.IaiRepeat tsubameRepeat = XanSAM.IaiRepeat.None,
        KaeshiAction kaeshi = default,
        double ogiLeft = 0,
        double fugetsuLeft = 40,
        double fukaLeft = 40)
    {
        return GeneratedPattern(1) with
        {
            Name = name,
            CombatTimer = combatTimer,
            Level = 100,
            ContentId = 0,
            InCombat = true,
            TargetCount = 1,
            Targetable = true,
            HaveTarget = true,
            CanMelee = true,
            LookAway = false,
            IsMoving = false,
            TargetPriority = 1,
            Kenki = 0,
            Meditation = 0,
            Sen = sen,
            Kaeshi = kaeshi,
            ComboLast = comboLast,
            FugetsuLeft = fugetsuLeft,
            FukaLeft = fukaLeft,
            MeikyoLeft = meikyoLeft,
            MeikyoStacks = meikyoStacks,
            MeikyoCharges = 1,
            MeikyoReadyIn = 0,
            OgiLeft = ogiLeft,
            ZanshinLeft = 0,
            TendoLeft = tendoLeft,
            EnhancedEnpiLeft = 0,
            TrueNorthLeft = 0,
            TsubameLeft = tsubameLeft,
            TsubameRepeat = tsubameRepeat,
            TargetHiganbanaLeft = targetHiganbanaLeft,
            IkishotenReadyIn = 120,
            SeneiGurenReadyIn = 120,
            ShohaReadyIn = 120,
            KenkiSpendReadyIn = 120,
            ZanshinReadyIn = 120,
            GyotenReadyIn = 120,
            YatenReadyIn = 120,
            HagakureReadyIn = 120,
            TrueNorthReadyIn = 120,
            Buffs = AutoForceDelayMode.Delay,
            Potion = XanSAM.SamPotionStrategy.None,
            Tsubame = XanSAM.TsubameStrategy.Auto,
            Namikiri = XanSAM.NamikiriStrategy.Auto,
            Higanbana = XanSAM.BanaStrategy.Automatic,
            Enpi = XanSAM.EnpiStrategy.None,
            Meikyo = XanSAM.MeikyoStrategy.Delay,
            TrueNorth = XanSAM.TrueNorthStrategy.None,
            Opener = XanSAM.OpenerStrategy.Standard,
            OpenerBurst = XanSAM.OpenerBurstStrategy.Normal,
            GcdRoute = XanSAM.GCDRouteStrategy.GCD208,
            RecentAction = AID.None,
            RecentActionAge = 0,
            TargetRotation = MathF.PI,
            GCDReadyIn = 0,
            AnimationLockDelay = 0
        };
    }

    private static RealSamPattern GeneratedPattern(int index)
    {
        var levels = new[] { 30, 40, 50, 52, 62, 68, 70, 72, 76, 80, 86, 90, 92, 96, 100 };
        var targetCounts = new[] { 1, 0, 1, 2, 3, 4, 6 };
        var kenkiValues = new[] { 0, 10, 25, 40, 50, 75, 90, 100 };
        var meditationValues = new[] { 0, 1, 2, 3 };
        var senValues = new[]
        {
            SenFlags.None,
            SenFlags.Setsu,
            SenFlags.Getsu,
            SenFlags.Ka,
            SenFlags.Setsu | SenFlags.Getsu,
            SenFlags.Setsu | SenFlags.Ka,
            SenFlags.Getsu | SenFlags.Ka,
            SenFlags.Setsu | SenFlags.Getsu | SenFlags.Ka
        };
        var kaeshiValues = new[] { default, KaeshiAction.Namikiri };
        var tsubameValues = Enum.GetValues<XanSAM.IaiRepeat>();
        var combos = new[] { AID.None, AID.Hakaze, AID.Gyofu, AID.Jinpu, AID.Shifu, AID.Fuga, AID.Fuko };
        var buffModes = Enum.GetValues<AutoForceDelayMode>();
        var potionModes = Enum.GetValues<XanSAM.SamPotionStrategy>();
        var tsubameModes = Enum.GetValues<XanSAM.TsubameStrategy>();
        var namikiriModes = Enum.GetValues<XanSAM.NamikiriStrategy>();
        var banaModes = Enum.GetValues<XanSAM.BanaStrategy>();
        var enpiModes = Enum.GetValues<XanSAM.EnpiStrategy>();
        var meikyoModes = Enum.GetValues<XanSAM.MeikyoStrategy>();
        var trueNorthModes = Enum.GetValues<XanSAM.TrueNorthStrategy>();
        var openerModes = Enum.GetValues<XanSAM.OpenerStrategy>();
        var openerBurstModes = Enum.GetValues<XanSAM.OpenerBurstStrategy>();
        var gcdRouteModes = Enum.GetValues<XanSAM.GCDRouteStrategy>();
        var targetPriorities = new[] { 1, 2, 3 };

        var targetCount = Pick(targetCounts, index, 5);
        var level = Pick(levels, index, 7);
        var inOpener = index % 23 == 0;
        var inBurst = index % 11 == 0;
        var haveTarget = targetCount > 0 && (index == 0 || index % 29 != 0);
        var targetable = haveTarget && (index == 0 || index % 31 != 0);
        var canMelee = haveTarget && (index == 0 || index % 17 != 0);
        var meikyoActive = index % 13 == 0 && level >= 50;
        var tsubameRepeat = Pick(tsubameValues, index, 19);
        var tsubameLeft = tsubameRepeat == XanSAM.IaiRepeat.None ? 0 : 30 - index % 24;

        return new(
            Name: $"real_sam_{index:00000}",
            CombatTimer: inOpener ? index % 25 : (index * 7) % 720,
            Level: level,
            ContentId: index % 37 == 0 ? 1094 : 0,
            InCombat: true,
            TargetCount: targetCount,
            Targetable: targetable,
            HaveTarget: haveTarget,
            CanMelee: canMelee,
            LookAway: index % 41 == 0,
            IsMoving: index % 43 == 0,
            TargetPriority: Pick(targetPriorities, index, 7),
            Kenki: Pick(kenkiValues, index, 11),
            Meditation: Pick(meditationValues, index, 13),
            Sen: Pick(senValues, index, 17),
            Kaeshi: index % 47 == 0 && level >= 90 ? KaeshiAction.Namikiri : Pick(kaeshiValues, index, 53),
            ComboLast: Pick(combos, index, 23),
            FugetsuLeft: index % 7 == 0 ? 0 : 40 - index % 37,
            FukaLeft: index % 11 == 0 ? 0 : 40 - index % 37,
            MeikyoLeft: meikyoActive ? 20 - index % 15 : 0,
            MeikyoStacks: meikyoActive ? 1 + index % 3 : 0,
            MeikyoCharges: index % 59 == 0 && level >= 100 ? 2 : index % 5 == 0 && level >= 50 ? 1 : 0,
            MeikyoReadyIn: ReadyValue(index, 2),
            OgiLeft: index % 17 == 0 && level >= 90 ? 30 - index % 20 : 0,
            ZanshinLeft: index % 19 == 0 && level >= 96 ? 30 - index % 20 : 0,
            TendoLeft: index % 13 == 0 && level >= 100 ? 30 - index % 20 : 0,
            EnhancedEnpiLeft: index % 29 == 0 ? 15 - index % 10 : 0,
            TrueNorthLeft: index % 73 == 0 ? 8 : 0,
            TsubameLeft: tsubameLeft,
            TsubameRepeat: tsubameRepeat,
            TargetHiganbanaLeft: index % 3 == 0 ? 0 : 60 - index % 57,
            IkishotenReadyIn: ReadyValue(index, 3),
            SeneiGurenReadyIn: ReadyValue(index, 5),
            ShohaReadyIn: ReadyValue(index, 7),
            KenkiSpendReadyIn: index % 97 == 0 ? 0.8 : 0,
            ZanshinReadyIn: index % 89 == 0 ? 1 : 0,
            GyotenReadyIn: ReadyValue(index, 11),
            YatenReadyIn: ReadyValue(index, 13),
            HagakureReadyIn: ReadyValue(index, 17),
            TrueNorthReadyIn: ReadyValue(index, 19),
            Buffs: Pick(buffModes, index, 29),
            Potion: Pick(potionModes, index, 31),
            Tsubame: Pick(tsubameModes, index, 37),
            Namikiri: Pick(namikiriModes, index, 41),
            Higanbana: Pick(banaModes, index, 43),
            Enpi: Pick(enpiModes, index, 47),
            Meikyo: Pick(meikyoModes, index, 53),
            TrueNorth: Pick(trueNorthModes, index, 59),
            Opener: Pick(openerModes, index, 61),
            OpenerBurst: Pick(openerBurstModes, index, 67),
            GcdRoute: Pick(gcdRouteModes, index, 71),
            RecentAction: AID.None,
            RecentActionAge: 0,
            TargetRotation: MathF.PI,
            GCDReadyIn: 0,
            AnimationLockDelay: (float)(index % 4) * 0.05f);
    }

    private static T Pick<T>(T[] values, int index, int multiplier)
        => values[(index * multiplier) % values.Length];

    private static double ReadyValue(int index, int salt)
    {
        var values = new[] { 0d, 0.5d, 1d, 5d, 10d, 20d, 40d, 60d, 90d, 120d };
        return values[(index * salt) % values.Length];
    }

    private static unsafe ClientState.Gauge BuildSamuraiGauge(int kenki, int meditation, SenFlags sen, KaeshiAction kaeshi)
    {
        SamuraiGauge gauge = default;
        gauge.Kenki = (byte)Math.Clamp(kenki, 0, 100);
        gauge.MeditationStacks = (byte)Math.Clamp(meditation, 0, 3);
        gauge.SenFlags = sen;
        gauge.Kaeshi = kaeshi;
        var raw = (ulong*)Unsafe.AsPointer(ref gauge);
        var low = sizeof(SamuraiGauge) > 8 ? raw[1] : 0;
        var high = sizeof(SamuraiGauge) > 16 ? raw[2] : 0;
        return new(low, high);
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

internal sealed record RealSamPattern(
    string Name,
    int CombatTimer,
    int Level,
    int ContentId,
    bool InCombat,
    int TargetCount,
    bool Targetable,
    bool HaveTarget,
    bool CanMelee,
    bool LookAway,
    bool IsMoving,
    int TargetPriority,
    int Kenki,
    int Meditation,
    SenFlags Sen,
    KaeshiAction Kaeshi,
    AID ComboLast,
    double FugetsuLeft,
    double FukaLeft,
    double MeikyoLeft,
    int MeikyoStacks,
    int MeikyoCharges,
    double MeikyoReadyIn,
    double OgiLeft,
    double ZanshinLeft,
    double TendoLeft,
    double EnhancedEnpiLeft,
    double TrueNorthLeft,
    double TsubameLeft,
    XanSAM.IaiRepeat TsubameRepeat,
    double TargetHiganbanaLeft,
    double IkishotenReadyIn,
    double SeneiGurenReadyIn,
    double ShohaReadyIn,
    double KenkiSpendReadyIn,
    double ZanshinReadyIn,
    double GyotenReadyIn,
    double YatenReadyIn,
    double HagakureReadyIn,
    double TrueNorthReadyIn,
    AutoForceDelayMode Buffs,
    XanSAM.SamPotionStrategy Potion,
    XanSAM.TsubameStrategy Tsubame,
    XanSAM.NamikiriStrategy Namikiri,
    XanSAM.BanaStrategy Higanbana,
    XanSAM.EnpiStrategy Enpi,
    XanSAM.MeikyoStrategy Meikyo,
    XanSAM.TrueNorthStrategy TrueNorth,
    XanSAM.OpenerStrategy Opener,
    XanSAM.OpenerBurstStrategy OpenerBurst,
    XanSAM.GCDRouteStrategy GcdRoute,
    AID RecentAction,
    double RecentActionAge,
    float TargetRotation,
    double GCDReadyIn,
    float AnimationLockDelay)
{
    public bool TargetOmnidirectional { get; init; }
    public bool? PrimaryTargetableOverride { get; init; }
    public float? TargetHitboxDistanceOverride { get; init; }
    public double SecondaryHiganbanaLeft { get; init; }
}

internal sealed record LiveSequenceCheck(string Name, RealSamPattern Pattern, AID ExpectedGCD, AID? ForbiddenAction = null, AID? ExpectedOGCD = null, int? ExpectedGCDTargetIndex = null, bool? ExpectedPotion = null);

internal sealed record QueuedActionRecord(ActionType Type, uint ID, string Action, ulong Target, float Priority, float Delay);

internal sealed record RealSamExecutionResult(string PatternName, List<QueuedActionRecord> Actions, QueuedActionRecord? SelectedGCD, QueuedActionRecord? SelectedOGCD, string Statuses, string State, string? Exception);

internal sealed record RealHarnessResult(int Patterns)
{
    public int Failures { get; set; }
    public int EmptyQueues { get; set; }
    public int ExpectedEmptyQueues { get; set; }
    public List<string> SequenceFailures { get; } = [];
    public List<RealSamExecutionResult> Results { get; } = [];
}
