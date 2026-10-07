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
using BossMod.NIN;
using EncounterTimeline;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanNIN = BossMod.Autorotation.xan.NIN;

namespace NinRealHarness;

internal static class Program
{
    private const int DefaultPatternCount = 5_000;
    private const float TimelineDuration = 360;
    private const float TimelineStep = 0.05f;
    private const float TimelineGCD = 2.12f;
    private const float TimelineMudraGCD = 0.50f;
    private const float TimelineTenChiJinGCD = 1.50f;
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
                "timeline" => Timeline(options),
                "suite" => TimelineSuite(options),
                "event-timeline" => EventTriggerTimelineSource(options),
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
        foreach (var failure in result.ValidationFailures)
            Console.WriteLine(failure);
        return result.Exception == null && result.ValidationFailures.Count == 0 ? 0 : 1;
    }

    private static int Count(HarnessOptions options)
    {
        var count = options.Limit ?? DefaultPatternCount;
        Console.WriteLine($"real_nin_patterns={count}");
        return 0;
    }

    private static int Run(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);

        var limit = options.Limit ?? DefaultPatternCount;
        var results = new RealHarnessResult(limit);
        for (var i = 0; i < limit; ++i)
        {
            var result = ExecutePattern(GeneratedPattern(i), sampleIndex: i);
            results.Results.Add(result);
            if (result.Exception != null || result.ValidationFailures.Count > 0)
                ++results.Failures;
            else if (result.Actions.Count == 0)
                ++results.EmptyQueues;
        }

        if (!string.IsNullOrWhiteSpace(options.Out))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Out))!);
            File.WriteAllText(options.Out, JsonSerializer.Serialize(results, JsonOptions));
        }

        Console.WriteLine($"patterns={results.Patterns}");
        Console.WriteLine($"failures={results.Failures}");
        Console.WriteLine($"empty_queues={results.EmptyQueues}");
        if (!string.IsNullOrWhiteSpace(options.Out))
            Console.WriteLine($"out={options.Out}");

        return results.Failures == 0 ? 0 : 3;
    }

    private static int Timeline(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);

        var result = ExecuteTimeline(StandardTimelineScenario());
        if (!string.IsNullOrWhiteSpace(options.Out))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Out))!);
            File.WriteAllText(options.Out, JsonSerializer.Serialize(result, JsonOptions));
        }

        PrintTimelineResult(result);
        if (!string.IsNullOrWhiteSpace(options.Out))
            Console.WriteLine($"out={options.Out}");

        return result.Failures.Count == 0 ? 0 : 3;
    }

    private static int TimelineSuite(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);

        IReadOnlyList<EventTriggerTimelineInput> eventTriggers = options.TimelineRoot is null ? [] : LoadEventTriggerTimelineSuite(ResolveEventTriggerTimelineRoot(options.TimelineRoot));
        var suite = new RealNinTimelineSuiteResult([.. TimelineSuiteScenarios(eventTriggers.Select(input => input.Scenario)).Select(ExecuteTimeline)]);
        if (!string.IsNullOrWhiteSpace(options.Out))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Out))!);
            File.WriteAllText(options.Out, JsonSerializer.Serialize(suite, JsonOptions));
        }

        Console.WriteLine($"scenarios={suite.Results.Count}");
        foreach (var result in suite.Results)
            Console.WriteLine($"scenario={result.Scenario} actions={result.Actions.Count} failures={result.Failures.Count}");
        foreach (var eventTrigger in eventTriggers)
            PrintEventTriggerTimelineInput(eventTrigger);
        var failures = suite.Failures + eventTriggers.Sum(input => input.Failures.Count);
        Console.WriteLine($"failures={failures}");
        if (!string.IsNullOrWhiteSpace(options.Out))
            Console.WriteLine($"out={options.Out}");

        return failures == 0 ? 0 : 3;
    }

    private static int EventTriggerTimelineSource(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        var eventTrigger = LoadEventTriggerTimelineSuite(ResolveEventTriggerTimelineRoot(options.TimelineRoot)).Single(input => input.ZoneID == 1363);
        var result = ExecuteTimeline(eventTrigger.Scenario);
        PrintEventTriggerTimelineInput(eventTrigger);
        PrintTimelineResult(result);
        return eventTrigger.Failures.Count == 0 && result.Failures.Count == 0 ? 0 : 3;
    }

    private static string ResolveEventTriggerTimelineRoot(string? timelineRoot)
    {
        var candidates = new[]
        {
            timelineRoot,
            Environment.GetEnvironmentVariable("EVENT_TRIGGER_TIMELINE_ROOT")
        };

        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return candidate;

        throw new DirectoryNotFoundException("Pass --timeline-root <Event Trigger timelines/resources> or set EVENT_TRIGGER_TIMELINE_ROOT.");
    }

    private static IReadOnlyList<EventTriggerTimelineInput> LoadEventTriggerTimelineSuite(string resourcesDirectory)
    {
        var catalog = EventTriggerTimelineCatalog.Load(resourcesDirectory);
        return
        [
            LoadEventTriggerTimeline(catalog, 733, "unending_coil_ultimate.txt", "ucob_event_trigger"),
            LoadEventTriggerTimeline(catalog, 777, "ultima_weapon_ultimate.txt", "uwu_event_trigger"),
            LoadEventTriggerTimeline(catalog, 887, "the_epic_of_alexander.txt", "tea_event_trigger"),
            LoadEventTriggerTimeline(catalog, 968, "dragonsongs_reprise_ultimate.txt", "dsr_event_trigger"),
            LoadEventTriggerTimeline(catalog, 1122, "the_omega_protocol.txt", "top_event_trigger"),
            LoadEventTriggerTimeline(catalog, 1238, "futures_rewritten.txt", "fru_event_trigger"),
            LoadEventTriggerTimeline(catalog, 1363, "dancing_mad.txt", "dancing_mad_event_trigger", 220)
        ];
    }

    private static EventTriggerTimelineInput LoadEventTriggerTimeline(EventTriggerTimelineCatalog catalog, int zoneID, string expectedFileName, string scenarioName, float? duration = null)
    {
        var timeline = catalog.TimelineForZone(zoneID);
        var scanEnd = timeline.Events.Count > 0 ? timeline.Events.Max(entry => entry.Time) + TimelineStep : TimelineStep;
        var allTargetUnavailableWindows = timeline.TargetUnavailableWindows(scanEnd);
        var scenarioDuration = duration ?? (allTargetUnavailableWindows.Count > 0 ? allTargetUnavailableWindows.Max(window => window.End) + 5 : scanEnd);
        var targetUnavailableWindows = allTargetUnavailableWindows
            .Where(window => window.Start < scenarioDuration)
            .Select(window => new TimelineWindow(window.Start, MathF.Min(window.End, scenarioDuration)))
            .ToArray();
        List<string> failures = [];

        if (!timeline.FileName.Equals(expectedFileName, StringComparison.OrdinalIgnoreCase))
            failures.Add($"Event Trigger zone {zoneID} resolved '{timeline.FileName}' instead of {expectedFileName}");
        if (targetUnavailableWindows.Length == 0)
            failures.Add($"Event Trigger {timeline.FileName} did not expose a target-loss window");
        if (zoneID == 1363 && !targetUnavailableWindows.Any(window => Math.Abs(window.Start - 197.3) <= TimelineStep && Math.Abs(window.End - 207.6) <= TimelineStep))
            failures.Add("Event Trigger dancing_mad.txt did not expose the P1-to-P2 197.3s-207.6s target-loss window");

        var pattern = TimelinePattern() with { Name = scenarioName, ContentId = 1094 };
        var scenario = new TimelineScenario(pattern.Name, pattern, scenarioDuration, targetUnavailableWindows, [],
            TimelineRequirement.ForbidEnemyTargetsDuringTargetLoss | TimelineRequirement.ResumeGCDAfterTargetReturn, 0);
        return new(catalog.ResourcesDirectory, zoneID, timeline.FileName, catalog.Summary, targetUnavailableWindows, scenario, failures);
    }

    private static void PrintEventTriggerTimelineInput(EventTriggerTimelineInput input)
    {
        Console.WriteLine($"event_trigger_resources={input.ResourcesDirectory}");
        Console.WriteLine($"event_trigger_timeline_files={input.Summary.TimelineFiles}");
        Console.WriteLine($"event_trigger_zone_mappings={input.Summary.ZoneMappings}");
        Console.WriteLine($"event_trigger_targetable_events={input.Summary.TargetableEvents}");
        Console.WriteLine($"event_trigger_untargetable_events={input.Summary.UntargetableEvents}");
        Console.WriteLine($"event_trigger_zone={input.ZoneID} file={input.FileName} target_loss={string.Join(',', input.TargetUnavailableWindows.Select(window => $"{window.Start:f1}-{window.End:f1}"))} failures={input.Failures.Count}");
        foreach (var failure in input.Failures)
            Console.WriteLine(failure);
    }

    private static void PrintTimelineResult(RealNinTimelineResult result)
    {
        Console.WriteLine($"scenario={result.Scenario}");
        Console.WriteLine($"duration={result.Duration:f1}");
        Console.WriteLine($"actions={result.Actions.Count}");
        Console.WriteLine($"gcd_actions={result.GCDActions}");
        Console.WriteLine($"ogcd_actions={result.OGCDActions}");
        Console.WriteLine($"dokumori={result.DokumoriActions}");
        Console.WriteLine($"kunai={result.KunaiActions}");
        Console.WriteLine($"ten_chi_jin={result.TenChiJinActions}");
        Console.WriteLine($"meisui={result.MeisuiActions}");
        Console.WriteLine($"tenri_jindo={result.TenriJindoActions}");
        Console.WriteLine($"ninki_spenders={result.NinkiSpenders}");
        Console.WriteLine($"failures={result.Failures.Count}");
    }

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  dotnet run --project tools\\nin_real_harness -- probe [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\nin_real_harness -- count [--limit 5000]");
        Console.Error.WriteLine("  dotnet run --project tools\\nin_real_harness -- run [--limit 5000] [--out <path>] [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\nin_real_harness -- timeline [--out <path>] [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\nin_real_harness -- suite [--timeline-root <resources>] [--out <path>] [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\nin_real_harness -- event-timeline --timeline-root <resources> [--sqpack <path>]");
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

    private static RealNinExecutionResult ExecutePattern(RealNinPattern pattern, int sampleIndex)
    {
        try
        {
            var world = BuildWorld(pattern, sampleIndex, out var player, out var target, out _, out var hints);
            using var bossmods = new BossModuleManager(world);
            var db = new RotationDatabase(new DirectoryInfo("tools/nin_real_harness/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
            using var manager = new RotationModuleManager(db, bossmods, hints)
            {
                CombatStart = world.CurrentTime.AddSeconds(-pattern.CombatTimer)
            };

            var module = new XanNIN(manager, player);
            var strategy = BuildStrategy(pattern);
            XanNIN.RealHarnessMudraChargesOverride = () => pattern.MudraCharges;
            try
            {
                module.Execute(strategy, pattern.HaveTarget ? target : null, pattern.AnimationLockDelay, isMoving: false);
            }
            finally
            {
                XanNIN.RealHarnessMudraChargesOverride = null;
            }

            var entries = hints.ActionsToExecute.Entries.ToList();
            return new(
                pattern.Name,
                [.. entries.Select(e => new QueuedActionRecord(e.Action.ToString(), e.Target?.InstanceID ?? 0, e.Priority, e.Delay))],
                null,
                ValidateQueuedActions(pattern, entries.Select(e => e.Action), world, player));
        }
        catch (Exception ex)
        {
            return new(pattern.Name, [], ex.ToString(), []);
        }
    }

    private static List<string> ValidateQueuedActions(RealNinPattern pattern, IEnumerable<ActionID> actions, WorldState world, Actor player)
    {
        var queued = actions.ToHashSet();
        var failures = new List<string>();

        foreach (var action in queued)
        {
            var definition = ActionDefinitions.Instance[action];
            if (definition != null && !definition.IsUnlocked(world, player))
                failures.Add($"{pattern.Name}: queued level-locked action {action} at level {pattern.Level}");
        }

        void Forbid(AID action, string reason)
        {
            if (queued.Contains(ActionID.MakeSpell(action)))
                failures.Add($"{pattern.Name}: queued {action}: {reason}");
        }

        Forbid(AID.Doton, "automatic Doton is forbidden");
        Forbid(AID.TCJDoton, "automatic TCJ Doton is forbidden");
        Forbid(AID.RabbitMedium, "Rabbit Medium is forbidden");

        if (pattern.Level >= 66)
            Forbid(AID.Mug, "Dokumori must replace Mug at level 66+");
        else
            Forbid(AID.Dokumori, "Dokumori is unavailable below level 66");

        if (pattern.Level >= 92)
            Forbid(AID.TrickAttack, "Kunai's Bane must replace Trick Attack at level 92+");
        else
            Forbid(AID.KunaisBane, "Kunai's Bane is unavailable below level 92");

        if (pattern.Level >= 96 && pattern.HigiLeft > 0)
        {
            Forbid(AID.Bhavacakra, "Higi must replace Bhavacakra with Zesho Meppo");
            Forbid(AID.HellfrogMedium, "Higi must replace Hellfrog Medium with Deathfrog Medium");
        }
        else
        {
            Forbid(AID.ZeshoMeppo, "Zesho Meppo requires level 96 and Higi");
            Forbid(AID.DeathfrogMedium, "Deathfrog Medium requires level 96 and Higi");
        }

        if (pattern.TargetCount > 2 || pattern.Level < 68)
        {
            Forbid(AID.Bhavacakra, "single-target ninki spender is not valid for this target count or level");
            Forbid(AID.ZeshoMeppo, "single-target ninki spender is not valid for this target count or level");
        }
        else
        {
            Forbid(AID.HellfrogMedium, "AOE ninki spender requires three or more targets");
            Forbid(AID.DeathfrogMedium, "AOE ninki spender requires three or more targets");
        }

        if (pattern.BasicComboOnly)
        {
            var comboActions = new HashSet<ActionID>
            {
                ActionID.MakeSpell(AID.SpinningEdge),
                ActionID.MakeSpell(AID.GustSlash),
                ActionID.MakeSpell(AID.AeolianEdge),
                ActionID.MakeSpell(AID.ArmorCrush),
                ActionID.MakeSpell(AID.DeathBlossom),
                ActionID.MakeSpell(AID.HakkeMujinsatsu)
            };
            foreach (var action in queued.Where(action => !comboActions.Contains(action)))
                failures.Add($"{pattern.Name}: BasicComboOnly queued non-combo action {action}");
        }

        return failures;
    }

    private static RealNinTimelineResult ExecuteTimeline(TimelineScenario scenario)
    {
        var pattern = scenario.Pattern;
        var world = BuildWorld(pattern, sampleIndex: 0, out var player, out var target, out var targets, out var hints);
        world.Execute(new ClientState.OpPlayerStatsChange(new(SkillSpeedForTimelineGCD(), SkillSpeedForTimelineGCD(), 0)));
        world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));
        world.Execute(new ClientState.OpInventoryChange(ActionDefinitions.IDPotionDex.ID, 10));

        using var bossmods = new BossModuleManager(world);
        var db = new RotationDatabase(new DirectoryInfo("tools/nin_real_harness/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
        using var manager = new RotationModuleManager(db, bossmods, hints)
        {
            CombatStart = BaseTime
        };

        var module = new XanNIN(manager, player);
        var timeline = new RealNinTimeline(world, player, target, targets, hints, module, BuildStrategy(pattern), scenario);
        XanNIN.RealHarnessMudraChargesOverride = timeline.MudraCharges;
        try
        {
            return timeline.Run();
        }
        finally
        {
            XanNIN.RealHarnessMudraChargesOverride = null;
        }
    }

    private static RealNinPattern TimelinePattern()
        => GeneratedPattern(0) with
        {
            Name = "real_nin_timeline",
            CombatTimer = 0,
            Level = 100,
            ContentId = 0,
            InCombat = true,
            TargetCount = 1,
            Targetable = true,
            HaveTarget = true,
            CanMelee = true,
            LookAway = false,
            TargetPriority = 1,
            Ninki = 50,
            Kazematoi = 5,
            ComboLast = AID.None,
            MudraCharges = 2,
            MudraCapIn = 0,
            MudraLeft = 0,
            MudraParam = 0,
            HiddenLeft = 0,
            ShadowWalkerLeft = 20,
            KassatsuLeft = 0,
            PhantomKamaitachiLeft = 0,
            RaijuLeft = 0,
            RaijuStacks = 0,
            TenChiJinLeft = 0,
            TenChiJinParam = 0,
            MeisuiLeft = 0,
            HigiLeft = 0,
            TenriLeft = 0,
            TrueNorthLeft = 0,
            BunshinLeft = 0,
            TargetMugLeft = 0,
            TargetTrickLeft = 0,
            MugReadyIn = 0,
            TrickReadyIn = 0,
            KassatsuReadyIn = 0,
            TenChiJinReadyIn = 0,
            MeisuiReadyIn = 0,
            BunshinReadyIn = 0,
            DreamReadyIn = 0,
            NinkiSpendReadyIn = 0,
            TenriReadyIn = 0,
            TrueNorthReadyIn = 0,
            BasicComboOnly = false,
            Buffs = AutoForceDelayMode.Auto,
            BurstStyle = XanNIN.BurstStyle.Normal,
            Potion = XanNIN.PotionStrategy.EvenBurst,
            TrueNorth = XanNIN.TrueNorthStrategy.Auto,
            Hide = false,
            ForkedRaiju = true,
            PhantomCannon = false,
            AnimationLockDelay = 0.05f
        };

    private static TimelineScenario StandardTimelineScenario()
    {
        var pattern = TimelinePattern();
        return new(
            pattern.Name,
            pattern,
            TimelineDuration,
            [new(90, 105), new(210, 225)],
            [new(45, 50), new(165, 170), new(285, 290)],
            TimelineRequirement.TenChiJinMeisuiTenriChain | TimelineRequirement.NormalKunaiEndAligned,
            0);
    }

    private static TimelineScenario UltimateZeroSecondOpeningTimelineScenario()
    {
        var pattern = TimelinePattern() with
        {
            Name = "ultimate_zero_second_opening",
            BurstStyle = XanNIN.BurstStyle.UltimateZeroSecond
        };
        return new(pattern.Name, pattern, 20, [], [], TimelineRequirement.UltimateZeroSecondOpening, 0);
    }

    private static IReadOnlyList<TimelineScenario> TimelineSuiteScenarios(IEnumerable<TimelineScenario>? eventTriggerScenarios = null)
    {
        List<TimelineScenario> scenarios =
        [
            StandardTimelineScenario(),
            UltimateZeroSecondOpeningTimelineScenario(),
            NinkiBeforeDokumoriTimelineScenario(),
            ExactCapBeforeDokumoriTimelineScenario(),
            LowLevelMugWithoutDokumoriNinkiTimelineScenario(),
            Level66DokumoriTimelineScenario(),
            Level91TrickAttackTimelineScenario(),
            Level92KunaisBaneTimelineScenario(),
            Level95BhavacakraWithoutHigiTimelineScenario(),
            Level96ZeshoMeppoTimelineScenario(),
            Level96DeathfrogMediumTimelineScenario(),
            NinkiBeforeMeisuiTimelineScenario(),
            HighNinkiHigiBeforeBunshinTimelineScenario(),
            HighNinkiHigiBeforeBunshinTargetTrickTimelineScenario(),
            MudraCapBeforeDokumoriTimelineScenario(),
            SingleTargetKassatsuHyoshoTimelineScenario(),
            TwoTargetKassatsuGokaTimelineScenario(),
            TwoTargetRaitonTimelineScenario(),
            ThreeTargetKatonTimelineScenario(),
            TwoTargetBhavacakraTimelineScenario(),
            ThreeTargetHellfrogTimelineScenario(),
            OddKunaiSkipTimelineScenario(),
            TargetLostBeforeKunaiTimelineScenario(),
            EvenBurstTenChiJinMeisuiTimelineScenario(),
            OddKunaiNoTenChiJinMeisuiTimelineScenario()
        ];

        if (eventTriggerScenarios is not null)
            scenarios.AddRange(eventTriggerScenarios);
        return scenarios;
    }

    private static RealNinPattern FocusedTimelinePattern(string name)
        => TimelinePattern() with
        {
            Name = name,
            Ninki = 0,
            ComboLast = AID.None,
            MudraCharges = 0,
            MudraCapIn = 20,
            ShadowWalkerLeft = 0,
            KassatsuLeft = 0,
            PhantomKamaitachiLeft = 0,
            RaijuLeft = 0,
            RaijuStacks = 0,
            TenChiJinLeft = 0,
            TenChiJinParam = 0,
            MeisuiLeft = 0,
            HigiLeft = 0,
            TenriLeft = 0,
            TrueNorthLeft = 0,
            BunshinLeft = 0,
            TargetMugLeft = 0,
            TargetTrickLeft = 0,
            MugReadyIn = 60,
            TrickReadyIn = 60,
            KassatsuReadyIn = 60,
            TenChiJinReadyIn = 60,
            MeisuiReadyIn = 60,
            BunshinReadyIn = 60,
            DreamReadyIn = 60,
            NinkiSpendReadyIn = 0,
            TenriReadyIn = 60,
            TrueNorthReadyIn = 60,
            Potion = XanNIN.PotionStrategy.None,
            TrueNorth = XanNIN.TrueNorthStrategy.None,
            ForkedRaiju = false
        };

    private static TimelineScenario NinkiBeforeDokumoriTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("ninki_before_dokumori") with
        {
            Ninki = 95,
            MugReadyIn = 0
        };
        return new(pattern.Name, pattern, 12, [], [], TimelineRequirement.NinkiSpendBeforeDokumori, 0);
    }

    private static TimelineScenario ExactCapBeforeDokumoriTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("exact_cap_before_dokumori") with
        {
            Ninki = 60,
            MugReadyIn = 0
        };
        return new(pattern.Name, pattern, 12, [], [], TimelineRequirement.NinkiSpendBeforeDokumori, 0);
    }

    private static TimelineScenario LowLevelMugWithoutDokumoriNinkiTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("low_level_mug_without_dokumori_ninki") with
        {
            Level = 62,
            Ninki = 60,
            MugReadyIn = 0
        };
        return new(pattern.Name, pattern, 6, [], [], TimelineRequirement.ForbidNinkiSpendBeforeMug, 0);
    }

    private static TimelineScenario Level66DokumoriTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("level_66_dokumori") with
        {
            Level = 66,
            MugReadyIn = 0
        };
        return new(pattern.Name, pattern, 6, [], [], TimelineRequirement.RequireDokumori, 0);
    }

    private static TimelineScenario Level91TrickAttackTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("level_91_trick_attack") with
        {
            Level = 91,
            ShadowWalkerLeft = 20,
            TrickReadyIn = 0
        };
        return new(pattern.Name, pattern, 6, [], [], TimelineRequirement.RequireTrickAttack, 0);
    }

    private static TimelineScenario Level92KunaisBaneTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("level_92_kunais_bane") with
        {
            Level = 92,
            ShadowWalkerLeft = 20,
            TrickReadyIn = 0
        };
        return new(pattern.Name, pattern, 6, [], [], TimelineRequirement.RequireKunaisBane, 0);
    }

    private static TimelineScenario Level95BhavacakraWithoutHigiTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("level_95_bhavacakra_without_higi") with
        {
            Level = 95,
            Ninki = 10,
            MugReadyIn = 0
        };
        return new(pattern.Name, pattern, 8, [], [], TimelineRequirement.RequireBhavacakraAfterDokumori, 0);
    }

    private static TimelineScenario Level96ZeshoMeppoTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("level_96_zesho_meppo") with
        {
            Level = 96,
            Ninki = 10,
            MugReadyIn = 0
        };
        return new(pattern.Name, pattern, 8, [], [], TimelineRequirement.RequireZeshoMeppoAfterDokumori, 0);
    }

    private static TimelineScenario Level96DeathfrogMediumTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("level_96_deathfrog_medium") with
        {
            Level = 96,
            TargetCount = 3,
            Ninki = 10,
            MugReadyIn = 0
        };
        return new(pattern.Name, pattern, 8, [], [], TimelineRequirement.RequireDeathfrogMediumAfterDokumori, 0);
    }

    private static TimelineScenario NinkiBeforeMeisuiTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("ninki_before_meisui") with
        {
            Ninki = 95,
            ShadowWalkerLeft = 20,
            TargetMugLeft = 20,
            MeisuiReadyIn = 0
        };
        return new(pattern.Name, pattern, 8, [], [], TimelineRequirement.NinkiSpendBeforeMeisui, 0);
    }

    private static TimelineScenario HighNinkiHigiBeforeBunshinTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("high_ninki_higi_before_bunshin") with
        {
            Ninki = 95,
            HigiLeft = 20,
            TargetMugLeft = 20,
            BunshinReadyIn = 0
        };
        return new(pattern.Name, pattern, 8, [], [], TimelineRequirement.HigiSpenderBeforeBunshin, 0);
    }

    private static TimelineScenario HighNinkiHigiBeforeBunshinTargetTrickTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("high_ninki_higi_before_bunshin_target_trick") with
        {
            Ninki = 95,
            HigiLeft = 20,
            TargetTrickLeft = 15,
            BunshinReadyIn = 0
        };
        return new(pattern.Name, pattern, 8, [], [], TimelineRequirement.HigiSpenderBeforeBunshin, 0);
    }

    private static TimelineScenario MudraCapBeforeDokumoriTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("mudra_cap_before_dokumori") with
        {
            MudraCharges = 2,
            MudraCapIn = 0,
            MugReadyIn = 8
        };
        return new(pattern.Name, pattern, 14, [], [], TimelineRequirement.NinjutsuBeforeDokumori, 4.25f);
    }

    private static TimelineScenario TwoTargetKassatsuGokaTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("two_target_kassatsu_goka") with
        {
            TargetCount = 2,
            TargetMugLeft = 20,
            KassatsuReadyIn = 0
        };
        return new(pattern.Name, pattern, 10, [], [], TimelineRequirement.KassatsuGokaMekkyaku, 0);
    }

    private static TimelineScenario SingleTargetKassatsuHyoshoTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("single_target_kassatsu_hyosho") with
        {
            TargetMugLeft = 20,
            KassatsuReadyIn = 0
        };
        return new(pattern.Name, pattern, 10, [], [], TimelineRequirement.KassatsuHyoshoRanryu, 0);
    }

    private static TimelineScenario TwoTargetRaitonTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("two_target_raiton") with
        {
            TargetCount = 2,
            MudraCharges = 2,
            MudraCapIn = 0
        };
        return new(pattern.Name, pattern, 8, [], [], TimelineRequirement.RaitonForTwoTargets, 0);
    }

    private static TimelineScenario ThreeTargetKatonTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("three_target_katon") with
        {
            TargetCount = 3,
            MudraCharges = 2,
            MudraCapIn = 0
        };
        return new(pattern.Name, pattern, 8, [], [], TimelineRequirement.KatonWithoutDoton, 0);
    }

    private static TimelineScenario TwoTargetBhavacakraTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("two_target_bhavacakra") with
        {
            TargetCount = 2,
            Ninki = 95
        };
        return new(pattern.Name, pattern, 6, [], [], TimelineRequirement.BhavacakraForTwoTargets, 0);
    }

    private static TimelineScenario ThreeTargetHellfrogTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("three_target_hellfrog") with
        {
            TargetCount = 3,
            Ninki = 95
        };
        return new(pattern.Name, pattern, 6, [], [], TimelineRequirement.HellfrogForThreeTargets, 0);
    }

    private static TimelineScenario OddKunaiSkipTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("odd_kunai_skip_55") with
        {
            ShadowWalkerLeft = 20,
            MugReadyIn = 54,
            TrickReadyIn = 0
        };
        return new(pattern.Name, pattern, 10, [], [], TimelineRequirement.ForbidKunai, 0);
    }

    private static TimelineScenario TargetLostBeforeKunaiTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("target_lost_before_kunai") with
        {
            ShadowWalkerLeft = 20,
            MugReadyIn = 60,
            TrickReadyIn = 5
        };
        return new(
            pattern.Name,
            pattern,
            30,
            [new(4, 20)],
            [],
            TimelineRequirement.ForbidKunai | TimelineRequirement.ForbidShadowWalkerPreparationAfterTargetReturn,
            0);
    }

    private static TimelineScenario EvenBurstTenChiJinMeisuiTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("even_burst_ten_chi_jin_meisui") with
        {
            TargetMugLeft = 20,
            TenChiJinReadyIn = 0,
            MeisuiReadyIn = 0
        };
        return new(pattern.Name, pattern, 15, [], [], TimelineRequirement.TenChiJinMeisuiTenriChain, 0);
    }

    private static TimelineScenario OddKunaiNoTenChiJinMeisuiTimelineScenario()
    {
        var pattern = FocusedTimelinePattern("odd_kunai_no_ten_chi_jin_meisui") with
        {
            ShadowWalkerLeft = 20,
            TargetTrickLeft = 15,
            TenChiJinReadyIn = 0,
            MeisuiReadyIn = 0
        };
        return new(pattern.Name, pattern, 12, [], [], TimelineRequirement.ForbidTenChiJinAndMeisui, 0);
    }

    private static int SkillSpeedForTimelineGCD()
    {
        var bestSpeed = 400;
        var bestDelta = float.MaxValue;
        for (var speed = 0; speed <= 6_000; ++speed)
        {
            var delta = MathF.Abs(ActionSpeed.GCDRounded(speed, 0, 100) - TimelineGCD);
            if (delta < bestDelta)
            {
                bestSpeed = speed;
                bestDelta = delta;
            }
        }
        return bestSpeed;
    }

    private static WorldState BuildWorld(RealNinPattern pattern, int sampleIndex, out Actor player, out Actor target, out List<Actor> targets, out AIHints hints)
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "nin-real-harness");
        var now = BaseTime.AddSeconds(pattern.CombatTimer);
        world.Execute(new WorldState.OpFrameStart(new(now, (ulong)sampleIndex, (uint)sampleIndex, 1.0f / 60.0f, 1.0f / 60.0f, 1), default, BuildNinjaGauge(pattern.Ninki, pattern.Kazematoi), default));
        world.Execute(new WorldState.OpZoneChange(0, (ushort)pattern.ContentId));

        var playerID = 0x10000001ul;
        var targetID = 0x40000001ul;
        var targetDistance = pattern.CanMelee ? 2.5f : 12.0f;
        world.Execute(new ActorState.OpCreate(playerID, 0, 0, 0, "Player", 0, ActorType.Player, Class.NIN, pattern.Level, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
        world.Execute(new ActorState.OpCreate(targetID, 0x1234, 2, 0, "Target", 0, ActorType.Enemy, Class.None, 100, new Vector4(targetDistance, 0, 0, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), pattern.Targetable, false, default, default, 0));
        world.Execute(new ActorState.OpCombat(playerID, pattern.InCombat));
        world.Execute(new ActorState.OpCombat(targetID, pattern.InCombat));
        world.Execute(new ClientState.OpPlayerStatsChange(new(400, 400, 0)));
        world.Execute(new ClientState.OpClassJobLevelsChange(BuildClassJobLevels(pattern.Level)));
        world.Execute(new ClientState.OpComboChange(new((uint)pattern.ComboLast, 14.5f)));
        world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));

        player = world.Actors.Find(playerID)!;
        target = world.Actors.Find(targetID)!;
        targets = [target];
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
            world.Execute(new ActorState.OpCreate(id, (uint)(0x1234 + i), spawnIndex, 0, "Target" + i, 0, ActorType.Enemy, Class.None, 100, new Vector4(targetDistance, 0, i * 2, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), pattern.Targetable, false, default, default, 0));
            var add = world.Actors.Find(id)!;
            world.Execute(new ActorState.OpCombat(add.InstanceID, pattern.InCombat));
            targets.Add(add);
            var enemy = new AIHints.Enemy(add, pattern.TargetPriority, false);
            if ((uint)add.CharacterSpawnIndex < AIHints.NumEnemies)
                hints.Enemies[add.CharacterSpawnIndex] = enemy;
            hints.PotentialTargets.Add(enemy);
        }

        if (pattern.HaveTarget)
            hints.HighestPotentialTargetPriority = pattern.TargetPriority;

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

    private static List<(int, Cooldown)> BuildCooldowns(RealNinPattern pattern)
    {
        List<(int, Cooldown)> cooldowns = [];
        SetCooldown(cooldowns, AID.Ten1, pattern.MudraCapIn, chargeElapsedOverride: pattern.MudraCharges >= 2 ? null : pattern.MudraCharges == 1 ? 20.0f : 0.0f);
        SetCooldown(cooldowns, AID.Mug, pattern.MugReadyIn);
        SetCooldown(cooldowns, AID.Dokumori, pattern.MugReadyIn);
        SetCooldown(cooldowns, AID.TrickAttack, pattern.TrickReadyIn);
        SetCooldown(cooldowns, AID.KunaisBane, pattern.TrickReadyIn);
        SetCooldown(cooldowns, AID.Kassatsu, pattern.KassatsuReadyIn);
        SetCooldown(cooldowns, AID.TenChiJin, pattern.TenChiJinReadyIn);
        SetCooldown(cooldowns, AID.Meisui, pattern.MeisuiReadyIn);
        SetCooldown(cooldowns, AID.Bunshin, pattern.BunshinReadyIn);
        SetCooldown(cooldowns, AID.DreamWithinADream, pattern.DreamReadyIn);
        SetCooldown(cooldowns, AID.Assassinate, pattern.DreamReadyIn);
        SetCooldown(cooldowns, AID.Bhavacakra, pattern.NinkiSpendReadyIn);
        SetCooldown(cooldowns, AID.HellfrogMedium, pattern.NinkiSpendReadyIn);
        SetCooldown(cooldowns, AID.ZeshoMeppo, pattern.NinkiSpendReadyIn);
        SetCooldown(cooldowns, AID.DeathfrogMedium, pattern.NinkiSpendReadyIn);
        SetCooldown(cooldowns, AID.TenriJindo, pattern.TenriReadyIn);
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

    private static void ApplyPlayerStatuses(WorldState world, Actor player, RealNinPattern pattern)
    {
        var index = 0;
        void Status(SID sid, double left, ushort extra = 0)
        {
            if (left > 0 && index < Actor.NumStatuses)
                world.Execute(new ActorState.OpStatus(player.InstanceID, index++, new((uint)sid, extra, world.FutureTime(left), player.InstanceID)));
        }

        Status(SID.Hidden, pattern.HiddenLeft);
        Status(SID.ShadowWalker, pattern.ShadowWalkerLeft);
        Status(SID.Kassatsu, pattern.KassatsuLeft);
        Status(SID.PhantomKamaitachiReady, pattern.PhantomKamaitachiLeft);
        Status(SID.RaijuReady, pattern.RaijuLeft, (ushort)pattern.RaijuStacks);
        Status(SID.TenChiJin, pattern.TenChiJinLeft, (ushort)pattern.TenChiJinParam);
        Status(SID.Mudra, pattern.MudraLeft, (ushort)pattern.MudraParam);
        Status(SID.Meisui, pattern.MeisuiLeft);
        Status(SID.Higi, pattern.HigiLeft);
        Status(SID.TenriJindoReady, pattern.TenriLeft);
        Status(SID.TrueNorth, pattern.TrueNorthLeft);
        Status(SID.Bunshin, pattern.BunshinLeft);
    }

    private static void ApplyTargetStatuses(WorldState world, Actor target, Actor player, RealNinPattern pattern)
    {
        var index = 0;
        void Status(SID sid, double left)
        {
            if (left > 0 && index < Actor.NumStatuses)
                world.Execute(new ActorState.OpStatus(target.InstanceID, index++, new((uint)sid, 0, world.FutureTime(left), player.InstanceID)));
        }

        Status(SID.TrickAttack, pattern.TargetTrickLeft);
        Status(SID.KunaisBane, pattern.TargetTrickLeft);
        Status(SID.VulnerabilityUp, pattern.TargetMugLeft);
        Status(SID.Dokumori, pattern.TargetMugLeft);
    }

    private static StrategyValues BuildStrategy(RealNinPattern pattern)
    {
        var definition = XanNIN.Definition();
        var strategy = new StrategyValues(definition.Configs);
        SetTrack(strategy, "Targeting", Targeting.Auto);
        SetTrack(strategy, "AOE", AOEStrategy.AOE);
        SetTrack(strategy, "Rotation", pattern.BasicComboOnly ? XanNIN.RotationStrategy.BasicComboOnly : XanNIN.RotationStrategy.Normal);
        SetTrack(strategy, "Buffs", MapOffensive(pattern.Buffs));
        SetTrack(strategy, "BurstStyle", pattern.BurstStyle);
        SetTrack(strategy, "Potion", pattern.Potion);
        SetTrack(strategy, "TrueNorth", pattern.TrueNorth);
        SetTrack(strategy, "Hide", pattern.Hide ? EnabledByDefault.Enabled : EnabledByDefault.Disabled);
        SetTrack(strategy, "ForkedRaiju", pattern.ForkedRaiju ? EnabledByDefault.Enabled : EnabledByDefault.Disabled);
        SetTrack(strategy, "PCAN", pattern.PhantomCannon ? EnabledByDefault.Enabled : EnabledByDefault.Disabled);
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

    private static RealNinPattern GeneratedPattern(int index)
    {
        var levels = new[] { 15, 30, 35, 45, 50, 56, 62, 66, 70, 72, 76, 80, 90, 92, 96, 100 };
        var targetCounts = new[] { 1, 1, 2, 3, 4 };
        var ninkiValues = new[] { 0, 30, 45, 50, 80, 90, 95, 100 };
        var kazematoiValues = new[] { 0, 1, 3, 5 };
        var combos = new[] { AID.None, AID.SpinningEdge, AID.GustSlash, AID.DeathBlossom };
        var buffModes = Enum.GetValues<AutoForceDelayMode>();
        var burstStyles = Enum.GetValues<XanNIN.BurstStyle>();
        var potionModes = Enum.GetValues<XanNIN.PotionStrategy>();
        var trueNorthModes = Enum.GetValues<XanNIN.TrueNorthStrategy>();
        var targetPriorities = new[] { 1, 2, 3 };

        var level = Pick(levels, index, 3);
        var inBurst = index % 11 == 0;
        var inTrick = index % 13 == 0;
        var mudraState = index % 17;

        return new(
            Name: $"real_nin_{index:00000}",
            CombatTimer: (index * 7) % 720,
            Level: level,
            ContentId: index % 29 == 0 ? 1094 : 0,
            InCombat: true,
            TargetCount: Pick(targetCounts, index, 5),
            Targetable: true,
            HaveTarget: true,
            CanMelee: true,
            LookAway: index % 41 == 0,
            TargetPriority: Pick(targetPriorities, index, 7),
            Ninki: Pick(ninkiValues, index, 11),
            Kazematoi: Pick(kazematoiValues, index, 13),
            ComboLast: Pick(combos, index, 17),
            MudraCharges: index % 3,
            MudraCapIn: index % 3 == 2 ? 0 : 20 - index % 20,
            MudraLeft: mudraState == 1 ? 4 : 0,
            MudraParam: mudraState == 1 ? 2 : 0,
            HiddenLeft: index % 19 == 0 ? 20 : 0,
            ShadowWalkerLeft: index % 7 == 0 ? 18 : 0,
            KassatsuLeft: index % 43 == 0 ? 12 : 0,
            PhantomKamaitachiLeft: index % 47 == 0 ? 30 : 0,
            RaijuLeft: index % 53 == 0 ? 25 : 0,
            RaijuStacks: index % 53 == 0 ? 1 + index % 3 : 0,
            TenChiJinLeft: index % 59 == 0 && level >= 70 ? 5 : 0,
            TenChiJinParam: index % 59 == 0 && level >= 70 ? index % 3 : 0,
            MeisuiLeft: index % 61 == 0 ? 20 : 0,
            HigiLeft: index % 67 == 0 ? 20 : 0,
            TenriLeft: index % 71 == 0 && level >= 100 ? 20 : 0,
            TrueNorthLeft: index % 73 == 0 ? 8 : 0,
            BunshinLeft: index % 79 == 0 ? 12 : 0,
            TargetMugLeft: inBurst ? 20 - index % 15 : 0,
            TargetTrickLeft: inTrick ? 15 - index % 10 : 0,
            MugReadyIn: ReadyValue(index, 2),
            TrickReadyIn: ReadyValue(index, 3),
            KassatsuReadyIn: ReadyValue(index, 5),
            TenChiJinReadyIn: ReadyValue(index, 7),
            MeisuiReadyIn: ReadyValue(index, 11),
            BunshinReadyIn: ReadyValue(index, 13),
            DreamReadyIn: ReadyValue(index, 17),
            NinkiSpendReadyIn: index % 97 == 0 ? 0.8 : 0,
            TenriReadyIn: index % 89 == 0 ? 1 : 0,
            TrueNorthReadyIn: ReadyValue(index, 19),
            BasicComboOnly: index % 101 == 0,
            Buffs: Pick(buffModes, index, 23),
            BurstStyle: Pick(burstStyles, index, 29),
            Potion: Pick(potionModes, index, 31),
            TrueNorth: Pick(trueNorthModes, index, 37),
            Hide: index % 3 != 0,
            ForkedRaiju: index % 2 == 0,
            PhantomCannon: false,
            AnimationLockDelay: (float)(index % 4) * 0.05f);
    }

    private static T Pick<T>(T[] values, int index, int multiplier)
        => values[(index * multiplier) % values.Length];

    private static double ReadyValue(int index, int salt)
    {
        var values = new[] { 0d, 0.5d, 1d, 5d, 10d, 20d, 40d, 60d, 90d, 120d };
        return values[(index * salt) % values.Length];
    }

    private static unsafe ClientState.Gauge BuildNinjaGauge(int ninki, int kazematoi)
    {
        NinjaGauge gauge = default;
        gauge.Ninki = (byte)Math.Clamp(ninki, 0, 100);
        gauge.Kazematoi = (byte)Math.Clamp(kazematoi, 0, 5);
        var raw = (ulong*)Unsafe.AsPointer(ref gauge);
        var low = sizeof(NinjaGauge) > 8 ? raw[1] : 0;
        var high = sizeof(NinjaGauge) > 16 ? raw[2] : 0;
        return new(low, high);
    }

    private sealed record HarnessOptions(int? Limit, string? Out, string? Sqpack, string? TimelineRoot)
    {
        public static HarnessOptions Parse(string[] args)
        {
            int? limit = null;
            string? output = null;
            string? sqpack = null;
            string? timelineRoot = null;

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
                    case "--timeline-root":
                        timelineRoot = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{key}'.");
                }
            }

            return new(limit, output, sqpack, timelineRoot);
        }
    }

    private sealed class RealNinTimeline
    {
        private const double DokumoriDuration = 20;
        private const double KunaiDuration = 15;
        private const double ShadowWalkerDuration = 20;
        private const double KassatsuDuration = 15;
        private const double TenChiJinDuration = 6;
        private const double MeisuiDuration = 30;
        private const double HigiDuration = 30;
        private const double RaijuDuration = 30;
        private const double BunshinDuration = 30;
        private const double PhantomKamaitachiDuration = 45;
        private const double TenriJindoDuration = 30;
        private const double TrueNorthDuration = 10;
        private const double PotionDuration = 30;
        private const double MudraDuration = 6;
        private const float ComboDuration = 14.5f;
        private const double GCDGapLimit = 5;

        private readonly WorldState _world;
        private readonly Actor _player;
        private readonly Actor _target;
        private readonly List<Actor> _targets;
        private readonly AIHints _hints;
        private readonly XanNIN _module;
        private readonly StrategyValues _strategy;
        private readonly TimelineScenario _scenario;
        private readonly TimelineState _state = new();

        public RealNinTimeline(WorldState world, Actor player, Actor target, List<Actor> targets, AIHints hints, XanNIN module, StrategyValues strategy, TimelineScenario scenario)
        {
            _world = world;
            _player = player;
            _target = target;
            _targets = targets;
            _hints = hints;
            _module = module;
            _strategy = strategy;
            _scenario = scenario;
            var pattern = scenario.Pattern;
            _state.Ninki = pattern.Ninki;
            _state.Kazematoi = pattern.Kazematoi;
            _state.TargetAvailable = pattern.HaveTarget && pattern.Targetable;
            _state.MudraParam = pattern.MudraParam;
            _state.TenChiJinParam = pattern.TenChiJinParam;
            _state.RaijuStacks = pattern.RaijuStacks;
            _state.ShadowWalkerUntil = pattern.ShadowWalkerLeft;
            _state.KassatsuUntil = pattern.KassatsuLeft;
            _state.PhantomKamaitachiUntil = pattern.PhantomKamaitachiLeft;
            _state.RaijuUntil = pattern.RaijuLeft;
            _state.TenChiJinUntil = pattern.TenChiJinLeft;
            _state.MudraUntil = pattern.MudraLeft;
            _state.MeisuiUntil = pattern.MeisuiLeft;
            _state.HigiUntil = pattern.HigiLeft;
            _state.TenriJindoUntil = pattern.TenriLeft;
            _state.TrueNorthUntil = pattern.TrueNorthLeft;
            _state.BunshinUntil = pattern.BunshinLeft;
            _state.DokumoriUntil = pattern.TargetMugLeft;
            _state.KunaiUntil = pattern.TargetTrickLeft;
            _state.LastGCDAt = -TimelineGCD;
        }

        public int MudraCharges()
        {
            var def = ActionDefinitions.Instance.Spell(AID.Ten1);
            if (def == null || def.MainCooldownGroup < 0)
                return 0;

            var cooldown = _world.Client.Cooldowns[def.MainCooldownGroup];
            if (cooldown.Total <= 0 || def.Cooldown <= 0)
                return def.MaxChargesAtLevel(_player.Level);

            var charges = (int)MathF.Floor(cooldown.Elapsed / def.Cooldown + 0.001f);
            return Math.Clamp(charges, 0, def.MaxChargesAtLevel(_player.Level));
        }

        public RealNinTimelineResult Run()
        {
            var frames = (int)MathF.Round(_scenario.Duration / TimelineStep);
            for (var frame = 0; frame <= frames; ++frame)
            {
                if (frame > 0)
                    AdvanceFrame(frame);

                UpdateTargetAvailability();
                SyncStatuses();
                UpdateMudraCapHold();
                var primaryTarget = RefreshHints();
                _module.Execute(_strategy, primaryTarget?.Actor, estimatedAnimLockDelay: 0.05f, isMoving: IsMoving());

                var executed = TryExecuteBestAction();
                CheckInvariants(executed);
            }

            CheckScenarioRequirements();

            return new(
                _scenario.Name,
                (float)_state.Time,
                _state.Frame,
                _state.GCDActions,
                _state.OGCDActions,
                _state.DokumoriActions,
                _state.KunaiActions,
                _state.TenChiJinActions,
                _state.MeisuiActions,
                _state.TenriJindoActions,
                _state.NinkiSpenders,
                [.. _state.Actions],
                [.. _state.Failures]
            );
        }

        private void AdvanceFrame(int frame)
        {
            _state.Frame = frame;
            _state.Time = frame * TimelineStep;
            var timestamp = BaseTime.AddSeconds(_state.Time);
            _world.Execute(new WorldState.OpFrameStart(new(timestamp, (ulong)frame, (uint)frame, TimelineStep, TimelineStep, 1), TimeSpan.FromSeconds(TimelineStep), BuildNinjaGauge(_state.Ninki, _state.Kazematoi), default));
        }

        private void UpdateTargetAvailability()
        {
            var available = TargetAvailable();
            if (available == _state.TargetAvailable)
                return;

            _state.TargetAvailable = available;
            foreach (var target in _targets)
                _world.Execute(new ActorState.OpTargetable(target.InstanceID, available));
            if (available)
            {
                _state.TargetReturnedAt = _state.Time;
                _state.EmptyReadyTime = 0;
                _state.GCDGapReported = false;
            }
        }

        private AIHints.Enemy? RefreshHints()
        {
            _hints.Clear();
            if (!_state.TargetAvailable)
                return null;

            AIHints.Enemy? primaryTarget = null;
            foreach (var target in _targets)
            {
                var enemy = new AIHints.Enemy(target, _scenario.Pattern.TargetPriority, false);
                if ((uint)target.CharacterSpawnIndex < AIHints.NumEnemies)
                    _hints.Enemies[target.CharacterSpawnIndex] = enemy;
                _hints.PotentialTargets.Add(enemy);
                if (target == _target)
                    primaryTarget = enemy;
            }
            _hints.HighestPotentialTargetPriority = _scenario.Pattern.TargetPriority;
            return primaryTarget;
        }

        private void SyncStatuses()
        {
            SyncActorStatuses(_player,
            [
                new((uint)SID.ShadowWalker, _state.ShadowWalkerUntil, 0),
                new((uint)SID.Kassatsu, _state.KassatsuUntil, 0),
                new((uint)SID.PhantomKamaitachiReady, _state.PhantomKamaitachiUntil, 0),
                new((uint)SID.RaijuReady, _state.RaijuUntil, (ushort)_state.RaijuStacks),
                new((uint)SID.TenChiJin, _state.TenChiJinUntil, (ushort)_state.TenChiJinParam),
                new((uint)SID.Mudra, _state.MudraUntil, (ushort)_state.MudraParam),
                new((uint)SID.Meisui, _state.MeisuiUntil, 0),
                new((uint)SID.Higi, _state.HigiUntil, 0),
                new((uint)SID.TenriJindoReady, _state.TenriJindoUntil, 0),
                new((uint)SID.TrueNorth, _state.TrueNorthUntil, 0),
                new((uint)SID.Bunshin, _state.BunshinUntil, 0),
                new(49, _state.PotionUntil, (ushort)PotionType.Dexterity)
            ]);
            SyncActorStatuses(_target,
            [
                new((uint)SID.Dokumori, _player.Level >= 66 ? _state.DokumoriUntil : 0, 0),
                new((uint)SID.VulnerabilityUp, _player.Level < 66 ? _state.DokumoriUntil : 0, 0),
                new((uint)SID.KunaisBane, _player.Level >= 92 ? _state.KunaiUntil : 0, 0),
                new((uint)SID.TrickAttack, _player.Level < 92 ? _state.KunaiUntil : 0, 0)
            ]);
        }

        private void SyncActorStatuses(Actor actor, ReadOnlySpan<TimelineStatus> statuses)
        {
            for (var index = 0; index < Actor.NumStatuses; ++index)
            {
                var desired = index < statuses.Length && statuses[index].Until > _state.Time
                    ? new ActorStatus(statuses[index].ID, statuses[index].Extra, BaseTime.AddSeconds(statuses[index].Until), _player.InstanceID)
                    : default;
                ref readonly var current = ref actor.Statuses[index];
                if (current.ID != desired.ID || current.Extra != desired.Extra || current.ExpireAt != desired.ExpireAt || current.SourceID != desired.SourceID)
                    _world.Execute(new ActorState.OpStatus(actor.InstanceID, index, desired));
            }
        }

        private bool TryExecuteBestAction()
        {
            var entry = _hints.ActionsToExecute.FindBest(_world, _player, _world.Client.Cooldowns, _world.Client.AnimationLock, _hints, instantAnimLockDelay: 0.05f, allowDismount: true);
            if (entry.Action.ID == 0)
                return false;

            var def = ActionDefinitions.Instance[entry.Action];
            if (def == null)
                return false;

            var startDelay = MathF.Max(entry.Delay, MathF.Max(_world.Client.AnimationLock, def.ReadyIn(_world.Client.Cooldowns, _world.Client.DutyActions)));
            if (startDelay > TimelineStep * 0.5f)
                return false;

            ExecuteAction(entry, def);
            return true;
        }

        private void ExecuteAction(ActionQueue.Entry entry, ActionDefinition definition)
        {
            var action = entry.Action.Type == ActionType.Spell ? (AID)entry.Action.ID : AID.None;
            var gcd = definition.IsGCD;
            var higiWasActive = _state.HigiUntil > _state.Time;

            if (!definition.IsUnlocked(_world, _player))
                AddFailure($"{_state.Time:f2}: executed level-locked action {entry.Action} at level {_player.Level}");

            if (gcd)
            {
                StartGCD(GCDDuration(action));
                _state.GCDActions++;
                _state.LastGCDAt = _state.Time;
                _state.GCDGapReported = false;
            }
            else
            {
                StartActionCooldown(definition);
                _state.OGCDActions++;
            }

            if (IsMudraStep(action))
                StartActionCooldown(definition);

            _world.Execute(new ClientState.OpAnimationLockChange(definition.InstantAnimLock));
            if (entry.Action.Type == ActionType.Item && entry.Action.ID == ActionDefinitions.IDPotionDex.ID)
            {
                StartPotionCooldown();
                _state.PotionUntil = _state.Time + PotionDuration;
                _world.Execute(new ClientState.OpInventoryChange(entry.Action.ID, Math.Max(0, _world.Client.GetInventoryItemQuantity(entry.Action.ID) - 1)));
            }
            else
            {
                ApplyNinjaAction(action, higiWasActive);
            }

            if (action != AID.None)
                _state.ExecutedActions.Add(new(_state.Time, action, gcd, _state.TargetAvailable, entry.Target?.InstanceID ?? 0));

            _state.Actions.Add(new(
                (float)_state.Time,
                entry.Action.ToString(),
                gcd,
                _state.Ninki,
                Left(_state.DokumoriUntil),
                Left(_state.KunaiUntil),
                Left(_state.ShadowWalkerUntil)
            ));
        }

        private void ApplyNinjaAction(AID action, bool higiWasActive)
        {
            if (action is AID.Doton or AID.TCJDoton)
                AddFailure($"{_state.Time:f2}: automatic Doton action selected: {action}");
            if (_player.Level >= 66 && action == AID.Mug || _player.Level < 66 && action == AID.Dokumori)
                AddFailure($"{_state.Time:f2}: level {_player.Level} selected invalid Mug/Dokumori replacement: {action}");
            if (_player.Level >= 92 && action == AID.TrickAttack || _player.Level < 92 && action == AID.KunaisBane)
                AddFailure($"{_state.Time:f2}: level {_player.Level} selected invalid Trick Attack/Kunai's Bane replacement: {action}");
            if (higiWasActive && action is AID.Bhavacakra or AID.HellfrogMedium)
                AddFailure($"{_state.Time:f2}: Higi selected obsolete ninki spender: {action}");
            if (_player.Level >= 76 && action == AID.Hyoton)
                AddFailure($"{_state.Time:f2}: selected Hyoton without an active Kassatsu upgrade");
            if (_scenario.Pattern.TargetCount > 2 && action is AID.Bhavacakra or AID.ZeshoMeppo)
                AddFailure($"{_state.Time:f2}: selected single-target ninki spender for {_scenario.Pattern.TargetCount} targets: {action}");
            if (_scenario.Pattern.TargetCount <= 2 && _player.Level >= 68 && action is AID.HellfrogMedium or AID.DeathfrogMedium)
                AddFailure($"{_state.Time:f2}: selected AOE ninki spender for {_scenario.Pattern.TargetCount} targets: {action}");

            switch (action)
            {
                case AID.Ten1:
                case AID.Ten2:
                case AID.Chi1:
                case AID.Chi2:
                case AID.Jin1:
                case AID.Jin2:
                    AddMudra(action);
                    break;
                case AID.Dokumori:
                    _state.DokumoriUntil = _state.Time + DokumoriDuration;
                    if (_player.Level >= 96)
                        _state.HigiUntil = _state.Time + HigiDuration;
                    GainNinki(40, "Dokumori");
                    _state.DokumoriActions++;
                    break;
                case AID.Mug:
                    _state.DokumoriUntil = _state.Time + DokumoriDuration;
                    break;
                case AID.KunaisBane:
                    _state.KunaiUntil = _state.Time + KunaiDuration;
                    _state.ShadowWalkerUntil = 0;
                    _state.KunaiActions++;
                    break;
                case AID.TrickAttack:
                    _state.KunaiUntil = _state.Time + KunaiDuration;
                    _state.ShadowWalkerUntil = 0;
                    _state.KunaiActions++;
                    break;
                case AID.Kassatsu:
                    _state.KassatsuUntil = _state.Time + KassatsuDuration;
                    break;
                case AID.TenChiJin:
                    _state.TenChiJinUntil = _state.Time + TenChiJinDuration;
                    _state.TenChiJinParam = 0;
                    _state.TenChiJinActions++;
                    break;
                case AID.Meisui:
                    _state.ShadowWalkerUntil = 0;
                    _state.MeisuiUntil = _state.Time + MeisuiDuration;
                    GainNinki(50, "Meisui");
                    _state.MeisuiActions++;
                    break;
                case AID.Bunshin:
                    SpendNinki(50, "Bunshin");
                    _state.BunshinUntil = _state.Time + BunshinDuration;
                    _state.BunshinHits = 0;
                    break;
                case AID.PhantomKamaitachi:
                    _state.PhantomKamaitachiUntil = 0;
                    GainNinki(10, "Phantom Kamaitachi");
                    break;
                case AID.ForkedRaiju:
                case AID.FleetingRaiju:
                    ConsumeRaiju();
                    break;
                case AID.Bhavacakra:
                case AID.HellfrogMedium:
                case AID.ZeshoMeppo:
                case AID.DeathfrogMedium:
                    SpendNinki(50, action.ToString());
                    if (action is AID.ZeshoMeppo or AID.DeathfrogMedium)
                        _state.HigiUntil = 0;
                    _state.NinkiSpenders++;
                    break;
                case AID.TenriJindo:
                    _state.TenriJindoUntil = 0;
                    _state.TenriJindoActions++;
                    break;
                case AID.TrueNorth:
                    _state.TrueNorthUntil = _state.Time + TrueNorthDuration;
                    break;
            }

            if (IsTenChiJinAction(action))
                ResolveTenChiJinAction(action);
            else if (IsNinjutsu(action))
                ResolveNinjutsu(action);

            if (IsWeaponskill(action))
                ResolveWeaponskill(action);
        }

        private void AddMudra(AID action)
        {
            var value = action switch
            {
                AID.Ten1 or AID.Ten2 => 1,
                AID.Chi1 or AID.Chi2 => 2,
                AID.Jin1 or AID.Jin2 => 3,
                _ => 0
            };
            var shift = (_state.MudraParam & 3) == 0 ? 0 : ((_state.MudraParam & 12) == 0 ? 2 : 4);
            _state.MudraParam |= value << shift;
            _state.MudraUntil = _state.Time + MudraDuration;
        }

        private void ResolveNinjutsu(AID action)
        {
            _state.MudraUntil = 0;
            _state.MudraParam = 0;
            if (action is AID.Suiton or AID.Huton)
                _state.ShadowWalkerUntil = _state.Time + ShadowWalkerDuration;
            if (action == AID.Raiton)
            {
                _state.RaijuStacks = Math.Min(3, _state.RaijuStacks + 1);
                _state.RaijuUntil = _state.Time + RaijuDuration;
            }
            if (action is AID.HyoshoRanryu or AID.GokaMekkyaku)
                _state.KassatsuUntil = 0;
        }

        private void ResolveTenChiJinAction(AID action)
        {
            switch (action)
            {
                case AID.FumaTen:
                    _state.TenChiJinParam = 1;
                    return;
                case AID.TCJRaiton:
                    _state.TenChiJinParam = 4;
                    return;
                case AID.FumaJin:
                    _state.TenChiJinParam = 1;
                    return;
                case AID.TCJHyoton:
                    _state.TenChiJinParam = 2;
                    return;
                case AID.TCJSuiton:
                    _state.ShadowWalkerUntil = _state.Time + ShadowWalkerDuration;
                    break;
            }

            _state.TenChiJinUntil = 0;
            _state.TenChiJinParam = 0;
            if (_player.Level >= 100)
                _state.TenriJindoUntil = _state.Time + TenriJindoDuration;
        }

        private void ResolveWeaponskill(AID action)
        {
            if (_player.Level >= 62)
                GainNinki(5, action.ToString());
            switch (action)
            {
                case AID.SpinningEdge:
                case AID.GustSlash:
                case AID.DeathBlossom:
                    _world.Execute(new ClientState.OpComboChange(new((uint)action, ComboDuration)));
                    break;
                case AID.AeolianEdge:
                    _state.Kazematoi = Math.Max(0, _state.Kazematoi - 1);
                    _world.Execute(new ClientState.OpComboChange(default));
                    break;
                case AID.ArmorCrush:
                    _state.Kazematoi = Math.Min(5, _state.Kazematoi + 2);
                    _world.Execute(new ClientState.OpComboChange(default));
                    break;
                case AID.HakkeMujinsatsu:
                    _world.Execute(new ClientState.OpComboChange(default));
                    break;
            }

            if (_state.BunshinUntil <= _state.Time)
                return;

            _state.BunshinHits++;
            if (_state.BunshinHits >= 5)
            {
                _state.BunshinUntil = 0;
                _state.PhantomKamaitachiUntil = _state.Time + PhantomKamaitachiDuration;
            }
        }

        private void ConsumeRaiju()
        {
            _state.RaijuStacks = Math.Max(0, _state.RaijuStacks - 1);
            if (_state.RaijuStacks == 0)
                _state.RaijuUntil = 0;
        }

        private void GainNinki(int amount, string source)
        {
            if (_state.Ninki + amount > 100)
                AddFailure($"{_state.Time:f2}: ninki overcap from {source}: {_state.Ninki}+{amount}");
            _state.Ninki = Math.Min(100, _state.Ninki + amount);
        }

        private void SpendNinki(int amount, string source)
        {
            if (_state.Ninki < amount)
                AddFailure($"{_state.Time:f2}: insufficient ninki for {source}: {_state.Ninki}/{amount}");
            _state.Ninki = Math.Max(0, _state.Ninki - amount);
        }

        private void StartGCD(float duration)
            => _world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new Cooldown(0, duration))]));

        private void StartPotionCooldown()
            => _world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.PotionCDGroup, new Cooldown(0, 270))]));

        private void StartActionCooldown(ActionDefinition definition)
        {
            StartActionCooldownGroup(definition.MainCooldownGroup, definition.Cooldown, definition.MaxChargesAtLevel(_player.Level), definition.MaxChargesAtCap());
            if (definition.ExtraCooldownGroup != definition.MainCooldownGroup)
                StartActionCooldownGroup(definition.ExtraCooldownGroup, definition.Cooldown, definition.MaxChargesAtLevel(_player.Level), definition.MaxChargesAtCap());
        }

        private void StartActionCooldownGroup(int group, float cooldown, int maxChargesAtLevel, int maxChargesAtCap)
        {
            if (group < 0 || group == ActionDefinitions.GCDGroup || cooldown <= 0)
                return;

            var maxCharges = Math.Max(1, maxChargesAtCap);
            var levelCharges = Math.Clamp(maxChargesAtLevel, 1, maxCharges);
            var current = _world.Client.Cooldowns[group];
            var elapsed = current.Total > 0 ? MathF.Max(0, current.Elapsed - cooldown) : cooldown * (levelCharges - 1);
            _world.Execute(new ClientState.OpCooldown(false, [(group, new Cooldown(elapsed, cooldown * maxCharges))]));
        }

        private void CheckInvariants(bool executed)
        {
            if (!_state.TargetAvailable)
                return;

            var gcdReady = _world.Client.Cooldowns[ActionDefinitions.GCDGroup].Remaining <= TimelineStep * 0.5f;
            if (!executed && gcdReady && _world.Client.AnimationLock <= TimelineStep * 0.5f && _state.Time > _state.TargetReturnedAt + 0.5)
            {
                _state.EmptyReadyTime += TimelineStep;
                if (_state.EmptyReadyTime > 0.25)
                    AddFailure($"{_state.Time:f2}: no executable action while GCD was ready");
            }
            else
            {
                _state.EmptyReadyTime = 0;
            }

            if (!_state.GCDGapReported && _state.Time > _state.TargetReturnedAt + 0.5 && _state.Time - _state.LastGCDAt > GCDGapLimit)
            {
                _state.GCDGapReported = true;
                AddFailure($"{_state.Time:f2}: no GCD action for {_state.Time - _state.LastGCDAt:f2}s while target was available");
            }
        }

        private void CheckScenarioRequirements()
        {
            if (_scenario.Requirements.HasFlag(TimelineRequirement.TenChiJinMeisuiTenriChain))
            {
                var tenChiJinAt = FirstActionTime(AID.TenChiJin);
                var meisuiAt = FirstActionTime(AID.Meisui);
                var tenriAt = FirstActionTime(AID.TenriJindo);
                if (double.IsPositiveInfinity(tenChiJinAt))
                    AddFailure("timeline did not execute Ten Chi Jin");
                if (!double.IsPositiveInfinity(tenChiJinAt) && (double.IsPositiveInfinity(meisuiAt) || meisuiAt <= tenChiJinAt))
                    AddFailure("timeline executed Ten Chi Jin without a subsequent Meisui");
                if (!double.IsPositiveInfinity(tenChiJinAt) && (double.IsPositiveInfinity(tenriAt) || tenriAt <= tenChiJinAt))
                    AddFailure("timeline executed Ten Chi Jin without a subsequent Tenri Jindo");
            }

            if (_scenario.Requirements.HasFlag(TimelineRequirement.NormalKunaiEndAligned))
            {
                var dokumoriAt = FirstActionTime(AID.Dokumori);
                var kunaiAt = FirstActionTime(AID.KunaisBane);
                var endDrift = Math.Abs((dokumoriAt + DokumoriDuration) - (kunaiAt + KunaiDuration));
                if (double.IsPositiveInfinity(dokumoriAt) || double.IsPositiveInfinity(kunaiAt))
                    AddFailure("timeline did not execute the Normal Dokumori/Kunai opening");
                else if (endDrift > 0.25 + TimelineStep)
                    AddFailure($"Normal Kunai end alignment drifted by {endDrift:f2}s");
            }

            if (_scenario.Requirements.HasFlag(TimelineRequirement.UltimateZeroSecondOpening))
            {
                var kassatsuAt = FirstActionTime(AID.Kassatsu);
                var dokumoriAt = FirstActionTime(AID.Dokumori);
                var kunaiAt = FirstActionTime(AID.KunaisBane);
                if (double.IsPositiveInfinity(kassatsuAt) || double.IsPositiveInfinity(dokumoriAt) || double.IsPositiveInfinity(kunaiAt))
                    AddFailure("timeline did not execute the UltimateZeroSecond Kassatsu/Dokumori/Kunai opening");
                else if (kassatsuAt >= dokumoriAt || dokumoriAt >= kunaiAt || kunaiAt - dokumoriAt > 1.5)
                    AddFailure($"UltimateZeroSecond opening order was invalid: Kassatsu={kassatsuAt:f2}, Dokumori={dokumoriAt:f2}, Kunai={kunaiAt:f2}");
            }

            if (_scenario.Requirements.HasFlag(TimelineRequirement.NinkiSpendBeforeDokumori))
                RequireActionBefore("ninki spender before Dokumori", IsNinkiSpender, action => action == AID.Dokumori);

            if (_scenario.Requirements.HasFlag(TimelineRequirement.ForbidNinkiSpendBeforeMug))
            {
                var mugAt = FirstActionTime(AID.Mug);
                var spenderAt = FirstActionTime(IsNinkiSpender);
                if (double.IsPositiveInfinity(mugAt))
                    AddFailure("timeline did not execute Mug");
                else if (spenderAt < mugAt)
                    AddFailure("timeline spent ninki before level-synced Mug");
            }

            if (_scenario.Requirements.HasFlag(TimelineRequirement.RequireDokumori) && double.IsPositiveInfinity(FirstActionTime(AID.Dokumori)))
                AddFailure("timeline did not execute Dokumori at level 66");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.RequireTrickAttack) && double.IsPositiveInfinity(FirstActionTime(AID.TrickAttack)))
                AddFailure("timeline did not execute Trick Attack below level 92");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.RequireKunaisBane) && double.IsPositiveInfinity(FirstActionTime(AID.KunaisBane)))
                AddFailure("timeline did not execute Kunai's Bane at level 92");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.RequireBhavacakraAfterDokumori))
                RequireActionBefore("Bhavacakra after Dokumori below level 96", action => action == AID.Dokumori, action => action == AID.Bhavacakra);

            if (_scenario.Requirements.HasFlag(TimelineRequirement.RequireZeshoMeppoAfterDokumori))
                RequireActionBefore("Zesho Meppo after Dokumori at level 96", action => action == AID.Dokumori, action => action == AID.ZeshoMeppo);

            if (_scenario.Requirements.HasFlag(TimelineRequirement.RequireDeathfrogMediumAfterDokumori))
                RequireActionBefore("Deathfrog Medium after Dokumori at level 96 with three targets", action => action == AID.Dokumori, action => action == AID.DeathfrogMedium);

            if (_scenario.Requirements.HasFlag(TimelineRequirement.NinkiSpendBeforeMeisui))
                RequireActionBefore("ninki spender before Meisui", IsNinkiSpender, action => action == AID.Meisui);

            if (_scenario.Requirements.HasFlag(TimelineRequirement.HigiSpenderBeforeBunshin))
                RequireActionBefore("Higi spender before Bunshin", action => action is AID.ZeshoMeppo or AID.DeathfrogMedium, action => action == AID.Bunshin);

            if (_scenario.Requirements.HasFlag(TimelineRequirement.NinjutsuBeforeDokumori))
                RequireActionBefore("ninjutsu before Dokumori", IsNinjutsu, action => action == AID.Dokumori);

            if (_scenario.Requirements.HasFlag(TimelineRequirement.KassatsuGokaMekkyaku) && !_state.ExecutedActions.Any(action => action.Action == AID.GokaMekkyaku))
                AddFailure("timeline did not execute Goka Mekkyaku for two targets during Kassatsu");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.KassatsuHyoshoRanryu) && !_state.ExecutedActions.Any(action => action.Action == AID.HyoshoRanryu))
                AddFailure("timeline did not execute Hyosho Ranryu for one target during Kassatsu");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.RaitonForTwoTargets) && !_state.ExecutedActions.Any(action => action.Action == AID.Raiton))
                AddFailure("timeline did not execute Raiton for two targets");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.KatonWithoutDoton) && !_state.ExecutedActions.Any(action => action.Action == AID.Katon))
                AddFailure("timeline did not execute Katon for three targets");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.BhavacakraForTwoTargets) && !_state.ExecutedActions.Any(action => action.Action == AID.Bhavacakra))
                AddFailure("timeline did not execute Bhavacakra for two targets");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.HellfrogForThreeTargets) && !_state.ExecutedActions.Any(action => action.Action == AID.HellfrogMedium))
                AddFailure("timeline did not execute Hellfrog Medium for three targets");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.ForbidKunai) && _state.ExecutedActions.Any(action => action.Action is AID.KunaisBane or AID.TrickAttack))
                AddFailure("timeline executed Kunai's Bane despite the required skip");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.ForbidTenChiJinAndMeisui) && _state.ExecutedActions.Any(action => action.Action is AID.TenChiJin or AID.Meisui))
                AddFailure("timeline executed Ten Chi Jin or Meisui outside the Dokumori window");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.ForbidShadowWalkerPreparationAfterTargetReturn)
                && _state.ExecutedActions.Any(action => action.Time >= _state.TargetReturnedAt && (action.Action is AID.Suiton or AID.Huton)))
                AddFailure("timeline prepared ShadowWalker after the post-downtime Kunai skip");

            if (_scenario.Requirements.HasFlag(TimelineRequirement.ForbidEnemyTargetsDuringTargetLoss)
                && _state.ExecutedActions.Any(action => !action.TargetAvailable && _targets.Any(target => target.InstanceID == action.TargetID)))
            {
                AddFailure("timeline executed an enemy-targeted action while the Event Trigger target was unavailable");
            }

            if (_scenario.Requirements.HasFlag(TimelineRequirement.ResumeGCDAfterTargetReturn)
                && !_state.ExecutedActions.Any(action => action.GCD && action.TargetAvailable && action.Time >= _state.TargetReturnedAt && action.Time <= _state.TargetReturnedAt + GCDGapLimit))
            {
                AddFailure("timeline did not resume a GCD after the Event Trigger target return");
            }

            if (_scenario.MaximumMudraCapHold > 0 && _state.MaxMudraCapHold > _scenario.MaximumMudraCapHold)
                AddFailure($"timeline held two Mudra charges for {_state.MaxMudraCapHold:f2}s, exceeding {_scenario.MaximumMudraCapHold:f2}s");
        }

        private void RequireActionBefore(string description, Func<AID, bool> firstAction, Func<AID, bool> secondAction)
        {
            var firstAt = FirstActionTime(firstAction);
            var secondAt = FirstActionTime(secondAction);
            if (double.IsPositiveInfinity(firstAt) || double.IsPositiveInfinity(secondAt) || firstAt >= secondAt)
                AddFailure($"timeline did not execute {description}");
        }

        private double FirstActionTime(AID action)
            => FirstActionTime(candidate => candidate == action);

        private double FirstActionTime(Func<AID, bool> matches)
        {
            foreach (var action in _state.ExecutedActions)
            {
                if (matches(action.Action))
                    return action.Time;
            }
            return double.PositiveInfinity;
        }

        private void UpdateMudraCapHold()
        {
            if (!_state.TargetAvailable || _state.MudraUntil > _state.Time || MudraCharges() < 2)
            {
                _state.MudraCapStartedAt = double.NaN;
                return;
            }

            if (double.IsNaN(_state.MudraCapStartedAt))
                _state.MudraCapStartedAt = _state.Time;
            _state.MaxMudraCapHold = Math.Max(_state.MaxMudraCapHold, _state.Time - _state.MudraCapStartedAt);
        }

        private void AddFailure(string failure)
        {
            if (!_state.Failures.Contains(failure))
                _state.Failures.Add(failure);
        }

        private float Left(double until) => (float)Math.Max(0, until - _state.Time);

        private bool TargetAvailable()
            => _scenario.Pattern.HaveTarget && _scenario.Pattern.Targetable && !_scenario.TargetUnavailableWindows.Any(window => window.Contains(_state.Time));

        private bool IsMoving()
            => _scenario.MovementWindows.Any(window => window.Contains(_state.Time));

        private static bool IsMudraStep(AID action)
            => action is AID.Ten1 or AID.Ten2 or AID.Chi1 or AID.Chi2 or AID.Jin1 or AID.Jin2;

        private static bool IsNinjutsu(AID action)
            => action is AID.FumaShuriken or AID.Katon or AID.Raiton or AID.Hyoton or AID.HyoshoRanryu or AID.Huton or AID.Suiton or AID.GokaMekkyaku or AID.Doton;

        private static bool IsNinkiSpender(AID action)
            => action is AID.Bhavacakra or AID.HellfrogMedium or AID.ZeshoMeppo or AID.DeathfrogMedium;

        private static bool IsTenChiJinAction(AID action)
            => action is AID.FumaJin or AID.FumaChi or AID.FumaTen or AID.TCJRaiton or AID.TCJKaton or AID.TCJSuiton or AID.TCJHyoton or AID.TCJHuton or AID.TCJDoton;

        private static bool IsWeaponskill(AID action)
            => action is AID.SpinningEdge or AID.GustSlash or AID.AeolianEdge or AID.ArmorCrush or AID.DeathBlossom or AID.HakkeMujinsatsu or AID.ForkedRaiju or AID.FleetingRaiju;

        private static float GCDDuration(AID action)
            => IsMudraStep(action) ? TimelineMudraGCD : IsTenChiJinAction(action) ? TimelineTenChiJinGCD : TimelineGCD;

        private sealed class TimelineState
        {
            public double Time;
            public int Frame;
            public int Ninki;
            public int Kazematoi;
            public bool TargetAvailable = true;
            public double TargetReturnedAt;
            public double LastGCDAt;
            public double EmptyReadyTime;
            public bool GCDGapReported;
            public double MudraCapStartedAt = double.NaN;
            public double MaxMudraCapHold;
            public int MudraParam;
            public int TenChiJinParam;
            public int RaijuStacks;
            public int BunshinHits;
            public double ShadowWalkerUntil;
            public double KassatsuUntil;
            public double PhantomKamaitachiUntil;
            public double RaijuUntil;
            public double TenChiJinUntil;
            public double MudraUntil;
            public double MeisuiUntil;
            public double HigiUntil;
            public double TenriJindoUntil;
            public double TrueNorthUntil;
            public double BunshinUntil;
            public double PotionUntil;
            public double DokumoriUntil;
            public double KunaiUntil;
            public int GCDActions;
            public int OGCDActions;
            public int DokumoriActions;
            public int KunaiActions;
            public int TenChiJinActions;
            public int MeisuiActions;
            public int TenriJindoActions;
            public int NinkiSpenders;
            public readonly List<TimelineActionRecord> Actions = [];
            public readonly List<ExecutedTimelineAction> ExecutedActions = [];
            public readonly List<string> Failures = [];
        }

        private readonly record struct TimelineStatus(uint ID, double Until, ushort Extra);
        private readonly record struct ExecutedTimelineAction(double Time, AID Action, bool GCD, bool TargetAvailable, ulong TargetID);
    }
}

[Flags]
internal enum TimelineRequirement
{
    None = 0,
    TenChiJinMeisuiTenriChain = 1 << 0,
    NinkiSpendBeforeDokumori = 1 << 1,
    NinkiSpendBeforeMeisui = 1 << 2,
    HigiSpenderBeforeBunshin = 1 << 3,
    NinjutsuBeforeDokumori = 1 << 4,
    ForbidKunai = 1 << 5,
    ForbidTenChiJinAndMeisui = 1 << 6,
    ForbidShadowWalkerPreparationAfterTargetReturn = 1 << 7,
    KassatsuGokaMekkyaku = 1 << 8,
    KatonWithoutDoton = 1 << 9,
    ForbidEnemyTargetsDuringTargetLoss = 1 << 10,
    ResumeGCDAfterTargetReturn = 1 << 11,
    ForbidNinkiSpendBeforeMug = 1 << 12,
    RequireDokumori = 1 << 13,
    RequireTrickAttack = 1 << 14,
    RequireKunaisBane = 1 << 15,
    RequireBhavacakraAfterDokumori = 1 << 16,
    RequireZeshoMeppoAfterDokumori = 1 << 17,
    RequireDeathfrogMediumAfterDokumori = 1 << 18,
    KassatsuHyoshoRanryu = 1 << 19,
    RaitonForTwoTargets = 1 << 20,
    BhavacakraForTwoTargets = 1 << 21,
    HellfrogForThreeTargets = 1 << 22,
    NormalKunaiEndAligned = 1 << 23,
    UltimateZeroSecondOpening = 1 << 24
}

internal readonly record struct TimelineWindow(double Start, double End)
{
    public bool Contains(double time) => time >= Start && time < End;
}

internal sealed record TimelineScenario(
    string Name,
    RealNinPattern Pattern,
    float Duration,
    TimelineWindow[] TargetUnavailableWindows,
    TimelineWindow[] MovementWindows,
    TimelineRequirement Requirements,
    float MaximumMudraCapHold);

internal sealed record EventTriggerTimelineInput(
    string ResourcesDirectory,
    int ZoneID,
    string FileName,
    EventTriggerTimelineSummary Summary,
    TimelineWindow[] TargetUnavailableWindows,
    TimelineScenario Scenario,
    List<string> Failures);

internal enum AutoForceDelayMode
{
    Auto,
    Force,
    Delay
}

internal sealed record RealNinPattern(
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
    int TargetPriority,
    int Ninki,
    int Kazematoi,
    AID ComboLast,
    int MudraCharges,
    double MudraCapIn,
    double MudraLeft,
    int MudraParam,
    double HiddenLeft,
    double ShadowWalkerLeft,
    double KassatsuLeft,
    double PhantomKamaitachiLeft,
    double RaijuLeft,
    int RaijuStacks,
    double TenChiJinLeft,
    int TenChiJinParam,
    double MeisuiLeft,
    double HigiLeft,
    double TenriLeft,
    double TrueNorthLeft,
    double BunshinLeft,
    double TargetMugLeft,
    double TargetTrickLeft,
    double MugReadyIn,
    double TrickReadyIn,
    double KassatsuReadyIn,
    double TenChiJinReadyIn,
    double MeisuiReadyIn,
    double BunshinReadyIn,
    double DreamReadyIn,
    double NinkiSpendReadyIn,
    double TenriReadyIn,
    double TrueNorthReadyIn,
    bool BasicComboOnly,
    AutoForceDelayMode Buffs,
    XanNIN.BurstStyle BurstStyle,
    XanNIN.PotionStrategy Potion,
    XanNIN.TrueNorthStrategy TrueNorth,
    bool Hide,
    bool ForkedRaiju,
    bool PhantomCannon,
    float AnimationLockDelay);

internal sealed record QueuedActionRecord(string Action, ulong Target, float Priority, float Delay);

internal sealed record RealNinExecutionResult(string PatternName, List<QueuedActionRecord> Actions, string? Exception, List<string> ValidationFailures);

internal sealed record RealHarnessResult(int Patterns)
{
    public int Failures { get; set; }
    public int EmptyQueues { get; set; }
    public List<RealNinExecutionResult> Results { get; } = [];
}

internal sealed record TimelineActionRecord(float Time, string Action, bool GCD, int Ninki, float DokumoriLeft, float KunaiLeft, float ShadowWalkerLeft);

internal sealed record RealNinTimelineResult(
    string Scenario,
    float Duration,
    int Frames,
    int GCDActions,
    int OGCDActions,
    int DokumoriActions,
    int KunaiActions,
    int TenChiJinActions,
    int MeisuiActions,
    int TenriJindoActions,
    int NinkiSpenders,
    List<TimelineActionRecord> Actions,
    List<string> Failures
);

internal sealed record RealNinTimelineSuiteResult(List<RealNinTimelineResult> Results)
{
    public int Failures => Results.Sum(result => result.Failures.Count);
}
