using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using BossMod;
using BossMod.Autorotation;
using BossMod.Autorotation.xan;
using Dalamud.Plugin.Services;
using EncounterTimeline;
using XanBLM = BossMod.Autorotation.xan.Custom.BLM;
using XanDRG = BossMod.Autorotation.xan.Custom.DRG;
using XanMCH = BossMod.Autorotation.xan.Custom.MCH;
using XanMNK = BossMod.Autorotation.xan.Custom.MNK;
using XanNIN = BossMod.Autorotation.xan.Custom.NIN;
using XanRPR = BossMod.Autorotation.xan.Custom.RPR;
using XanSAM = BossMod.Autorotation.xan.Custom.SAM;
using XanVPR = BossMod.Autorotation.xan.Custom.VPR;
using MNKAID = BossMod.MNK.AID;
using RPRAID = BossMod.RPR.AID;
using RPRSID = BossMod.RPR.SID;
using GNBAID = BossMod.GNB.AID;
using AkechiGNB = BossMod.Autorotation.akechi.Custom.AkechiGNB;
using XanGNB = BossMod.Autorotation.xan.Custom.GNB;
using PLDAID = BossMod.PLD.AID;
using AkechiPLD = BossMod.Autorotation.akechi.Custom.AkechiPLD;
using XanPLD = BossMod.Autorotation.xan.Custom.PLD;

namespace XanTimelineHarness;

internal static partial class Program
{
    private const int DancingMadTimelineZoneID = 1363;
    private const int DancingMadContentID = 1094;
    private const float FrameStep = 0.05f;
    private const float DefaultGCD = 2.50f;
    private const float ResumeGCDLimit = 3.50f;
    private const float MatrixOpeningDuration = 60f;
    private const float MatrixTargetLossPreRoll = 12f;
    private const float MatrixTargetReturnPostRoll = 12f;
    private const float MatrixMaximumDowntimeDuration = 30f;
    private static readonly float[] MatrixTargetLossWarmups = [0];
    private static readonly float[] CombatMatrixTargetLossWarmups = [0, 60, 120, 180];
    private const ulong TimelineTargetID = 0x40000001;
    private const ulong DmuPhase2TargetID = 0x40000002;
    private const ulong DmuPhase3KefkaTargetID = 0x40000003;
    private const ulong DmuPhase3ChaosTargetID = 0x40000004;
    private const ulong DmuPhase3ExdeathTargetID = 0x40000005;
    private const ulong DmuPhase4TargetID = 0x40000006;
    private const ulong DmuPhase4NeoExdeathTargetID = 0x40000007;
    private const ulong DmuPhase4ChaosTargetID = 0x40000008;
    private const ulong DmuPhase5TargetID = 0x40000009;
    private const uint DmuPrimaryActorOID = 0x4C30;
    private const uint DmuPhase2ActorOID = 0x4C32;
    private const uint DmuPhase3KefkaActorOID = 0x4BFB;
    private const uint DmuPhase3ChaosActorOID = 0x4C34;
    private const uint DmuPhase3ExdeathActorOID = 0x4C35;
    private const uint DmuPhase4ActorOID = 0x482B;
    private const uint DmuPhase4NeoExdeathActorOID = 0x4C36;
    private const uint DmuPhase4ChaosActorOID = 0x4C33;
    private const uint DmuPhase5ActorOID = 0x4C37;
    private const uint DmuAeroIIIAssaultActionID = 0xC3F7;
    private static readonly DateTime BaseTime = new(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);
    private static bool _blmBoundariesChecked;
    private static readonly JobAdapter[] Jobs =
    [
        new("drg", Class.DRG, XanDRG.Definition, static (manager, player) => new XanDRG(manager, player)),
        new("mch", Class.MCH, XanMCH.Definition, static (manager, player) => new XanMCH(manager, player)),
        new("mnk", Class.MNK, XanMNK.Definition, static (manager, player) => new XanMNK(manager, player)),
        new("blm", Class.BLM, XanBLM.Definition, static (manager, player) => new XanBLM(manager, player)),
        new("sam", Class.SAM, XanSAM.Definition, static (manager, player) => new XanSAM(manager, player)),
        new("vpr", Class.VPR, XanVPR.Definition, static (manager, player) => new XanVPR(manager, player)),
        new("rpr", Class.RPR, XanRPR.Definition, static (manager, player) => new XanRPR(manager, player)),
        // RPR on the rotation engine (Custom/Engine), same simulation and scorer as "rpr"
        new("rpr-engine", Class.RPR, BossMod.Autorotation.RprEngineModule.Definition, static (manager, player) => new BossMod.Autorotation.RprEngineModule(manager, player)),
        // BLM on the rotation engine, same simulation and scorer as "blm"
        new("blm-engine", Class.BLM, BossMod.Autorotation.BlmEngineModule.Definition, static (manager, player) => new BossMod.Autorotation.BlmEngineModule(manager, player)),
        new("nin", Class.NIN, XanNIN.Definition, static (manager, player) => new XanNIN(manager, player)),
        // NIN on the rotation engine, same simulation and scorer as "nin"
        new("nin-engine", Class.NIN, BossMod.Autorotation.NinEngineModule.Definition, static (manager, player) => new BossMod.Autorotation.NinEngineModule(manager, player)),
        new("mnk-engine", Class.MNK, BossMod.Autorotation.MnkEngineModule.Definition, static (manager, player) => new BossMod.Autorotation.MnkEngineModule(manager, player)),
        new("sam-engine", Class.SAM, BossMod.Autorotation.SamEngineModule.Definition, static (manager, player) => new BossMod.Autorotation.SamEngineModule(manager, player)),
        new("gnb-engine", Class.GNB, BossMod.Autorotation.GnbEngineModule.Definition, static (manager, player) => new BossMod.Autorotation.GnbEngineModule(manager, player)),
        new("pld-engine", Class.PLD, BossMod.Autorotation.PldEngineModule.Definition, static (manager, player) => new BossMod.Autorotation.PldEngineModule(manager, player)),
        // the only akechi module in the matrix: AkechiGNB.cs is the production GNB rotation and has no other harness
        new("gnb", Class.GNB, AkechiGNB.Definition, static (manager, player) => new AkechiGNB(manager, player)),
        // reference implementation, run through the same simulation and the same scorer so AkechiGNB's choices can
        // be compared against an independent rotation instead of against a hand-computed potency guess
        new("gnbx", Class.GNB, XanGNB.Definition, static (manager, player) => new XanGNB(manager, player)),
        // AkechiPLD.cs is the production PLD rotation; pldx is the xan reference module run through the same simulation and scorer
        new("pld", Class.PLD, AkechiPLD.Definition, static (manager, player) => new AkechiPLD(manager, player)),
        new("pldx", Class.PLD, XanPLD.Definition, static (manager, player) => new XanPLD(manager, player))
    ];

    public static int Main(string[] args)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("ENGINE_DEPTH") is { Length: > 0 } engineDepth)
            {
                var ew = BossMod.Autorotation.Engine.Jobs.RprDefinition.DefaultWeights();
                ew.HorizonGcds = int.Parse(engineDepth);
                if (Environment.GetEnvironmentVariable("ENGINE_BUDGET_MS") is { Length: > 0 } eb)
                    ew.BudgetMs = float.Parse(eb, System.Globalization.CultureInfo.InvariantCulture);
                BossMod.Autorotation.RprEngineModule.WeightsOverride = ew;
                var bw = BossMod.Autorotation.Engine.Jobs.BlmDefinition.DefaultWeights();
                bw.HorizonGcds = ew.HorizonGcds;
                bw.BudgetMs = ew.BudgetMs;
                BossMod.Autorotation.BlmEngineModule.WeightsOverride = bw;
            }
            if (Environment.GetEnvironmentVariable("ENGINE_WEIGHTS") is { Length: > 0 } weightsPath)
            {
                BossMod.Autorotation.RprEngineModule.WeightsOverride = BossMod.Autorotation.Engine.EngineWeights.Load(weightsPath);
                BossMod.Autorotation.BlmEngineModule.WeightsOverride = BossMod.Autorotation.Engine.EngineWeights.Load(weightsPath);
                BossMod.Autorotation.NinEngineModule.WeightsOverride = BossMod.Autorotation.Engine.EngineWeights.Load(weightsPath);
                BossMod.Autorotation.MnkEngineModule.WeightsOverride = BossMod.Autorotation.Engine.EngineWeights.Load(weightsPath);
                BossMod.Autorotation.SamEngineModule.WeightsOverride = BossMod.Autorotation.Engine.EngineWeights.Load(weightsPath);
                BossMod.Autorotation.GnbEngineModule.WeightsOverride = BossMod.Autorotation.Engine.EngineWeights.Load(weightsPath);
                BossMod.Autorotation.PldEngineModule.WeightsOverride = BossMod.Autorotation.Engine.EngineWeights.Load(weightsPath);
            }
            if (Environment.GetEnvironmentVariable("ENGINE_FRAME_MS") is { Length: > 0 } frameMs)
            {
                BossMod.Autorotation.RprEngineModule.FrameBudgetOverride = float.Parse(frameMs, System.Globalization.CultureInfo.InvariantCulture);
                BossMod.Autorotation.BlmEngineModule.FrameBudgetOverride = BossMod.Autorotation.RprEngineModule.FrameBudgetOverride;
                BossMod.Autorotation.NinEngineModule.FrameBudgetOverride = BossMod.Autorotation.RprEngineModule.FrameBudgetOverride;
                BossMod.Autorotation.MnkEngineModule.FrameBudgetOverride = BossMod.Autorotation.RprEngineModule.FrameBudgetOverride;
                BossMod.Autorotation.SamEngineModule.FrameBudgetOverride = BossMod.Autorotation.RprEngineModule.FrameBudgetOverride;
                BossMod.Autorotation.GnbEngineModule.FrameBudgetOverride = BossMod.Autorotation.RprEngineModule.FrameBudgetOverride;
                BossMod.Autorotation.PldEngineModule.FrameBudgetOverride = BossMod.Autorotation.RprEngineModule.FrameBudgetOverride;
            }
            if (Environment.GetEnvironmentVariable("ENGINE_REPLAN_S") is { Length: > 0 } replan)
                BossMod.Autorotation.NinEngineModule.ReplanOverride = float.Parse(replan, System.Globalization.CultureInfo.InvariantCulture);
            if (Environment.GetEnvironmentVariable("ENGINE_NO_BUFF_CYCLE") == "1")
                BossMod.Autorotation.EngineRotationModule.AssumeRaidBuffCycle = false;
            if (Environment.GetEnvironmentVariable("ENGINE_TRACE") == "1")
                BossMod.Autorotation.EngineRotationModule.DebugTrace = Console.WriteLine;
            var command = args.Length > 0 ? args[0] : "event-timeline";
            // takes replay files or directories instead of harness options
            if (command == "nin-replay-scan")
            {
                InitializeBossMod(null);
                var paths = args[1..].SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.log") : [p]).ToList();
                return NinReplayScan.Run(paths);
            }
            // takes replay files or directories instead of harness options
            if (command == "pld-replay-check")
            {
                InitializeBossMod(null);
                var paths = args[1..].SelectMany(p => Directory.Exists(p) ? Directory.GetFiles(p, "*.log") : [p]).ToList();
                return PldReplayCheck.Run(paths);
            }
            ForkScanOptions? forkScan = null;
            if (command == "blm-fork-scan")
            {
                var (rest, fork) = ExtractForkScanOptions(args[1..]);
                args = [command, .. rest];
                forkScan = fork;
            }
            var options = HarnessOptions.Parse(args.Length > 0 ? args[1..] : []);
            // --countdown also sets the length of the NIN pre-pull countdown (other jobs never get one in the timeline suites)
            ContinuousTimelineRunner.PrePullCountdown = options.CountdownSeconds;
            return command switch
            {
                "event-timeline" or "all" => RunEventTimeline(options),
                "dmu-full" => RunDmuFullTimeline(options),
                "timeline-matrix" => RunTimelineMatrix(options),
                "timeline-combat-matrix" => RunTimelineMatrix(options, combatMatrix: true),
                "blm-fork-scan" => RunBlmForkScan(options, forkScan!),
                "rpr-potion" => RunRprPotionTests(options),
                "mechanic-hints" => RunMechanicHints(options),
                "ttk-override-dump" => FightRemainingOverride.Dump(),
                "potency-audit" => RunPotencyAudit(options),
                "nin-emulator-selftest" => RunNinEmulatorSelfTest(options),
                "nin-planner-selftest" => NinPlannerSelfTest.Run(),
                "timeline-hints" => RunTimelineHints(options),
                "mnk-potency-dump" => RunMnkPotencyDump(options),
                "mnk-action-scan" => RunMnkActionScan(options),
                "splatoon-import" => RunSplatoonImportTest(options),
                "list-tracks" => RunListTracks(options),
                "irregular-compare" => RunIrregularCompare(options),
                "oracle-search" => RunOracleSearch(options),
                "rpr-potion-boundaries" => RunRprPotionTests(options, boundariesOnly: true),
                _ => Usage(command)
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    // Checks that hand-written Splatoon layouts import into the danger zones they describe.
    private static int RunMnkPotencyDump(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        MnkPotencyDump.Run();
        return 0;
    }

    private static int RunMnkActionScan(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        MnkActionScan.Run();
        return 0;
    }

    // The internal names --track matches on are not the labels the UI shows, so without a way to read them off the
    // module definition every --track spelling would be a guess.
    private static int RunListTracks(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        foreach (var job in SelectJobs(options.JobSelector))
        {
            Console.WriteLine($"job={job.Name}");
            foreach (var config in job.Definition().Configs)
            {
                if (config is not StrategyConfigTrack track)
                    continue;
                Console.WriteLine($"track={track.InternalName} name={track.DisplayName}");
                foreach (var option in track.Options)
                    Console.WriteLine($"  option={option.InternalName}");
            }
        }
        return 0;
    }

    private static int RunSplatoonImportTest(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        return SplatoonImportTest.Run();
    }

    // Replays the imported timelines against the live tracker; --zone limits it to one duty, --scenario-filter verbose prints each zone.
    private static int RunTimelineHints(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        return TimelineHintsValidation.Run(options.ZoneID == DancingMadTimelineZoneID ? null : options.ZoneID, options.ScenarioFilter == "verbose", options.ExtraPostRoll ?? 0f);
    }

    private static int RunMechanicHints(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        return MechanicHintsSelfTest.Run();
    }

    private static int RunNinEmulatorSelfTest(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        return NinEmulatorSelfTest.Run();
    }

    private static int RunPotencyAudit(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        return PotencyAudit.Run();
    }

    private static int RunRprPotionTests(HarnessOptions options, bool boundariesOnly = false)
    {
        InitializeBossMod(options.Sqpack);
        ContinuousTimelineRunner.CountdownSeconds = options.CountdownSeconds;
        Console.WriteLine($"countdown={options.CountdownSeconds?.ToString("f1", System.Globalization.CultureInfo.InvariantCulture) ?? "off"}");
        var job = Jobs.Single(job => job.Name == "rpr");
        var basis = new RprPotionTestCase("blue90-ac20-gluttony-ready") { Boundary = true, ExpectImmediateSpend = true };
        var deadlineBoundary = 4f + basis.GCD + ActionDefinitions.Instance.Spell(RPRAID.Gibbet)!.InstantAnimLock + FrameStep + 0.1f;
        var cases = new List<RprPotionTestCase>
        {
            basis,
            basis with { Name = "blue90-ac20-gluttony-later", GluttonyIn = 30 },
            basis with { Name = "blue90-ac30-gluttony-ready", ArcaneCircleIn = 30 },
            basis with { Name = "blue90-ac30-enshroud-not-ready", ArcaneCircleIn = 30, EnshroudIn = 5 },
            basis with { Name = "blue90-ac30-gluttony-later-enshroud-not-ready", ArcaneCircleIn = 30, GluttonyIn = 30, EnshroudIn = 5 },
            basis with { Name = "blue90-before-plan-window", ArcaneCircleIn = 25.05f },
            basis with { Name = "blue90-plan-window-start", ArcaneCircleIn = 25 },
            basis with { Name = "blue90-inside-plan-window", ArcaneCircleIn = 24.95f },
            basis with { Name = "blue90-before-deadline", ArcaneCircleIn = deadlineBoundary + FrameStep },
            basis with { Name = "blue90-deadline-no-margin", ArcaneCircleIn = deadlineBoundary - FrameStep, ExpectImmediateSpend = false },
            basis with { Name = "blue90-potion-off", Potion = XanRPR.PotionUseStrategy.Off },
            basis with { Name = "blue90-no-potion-stock", PotionCount = 0 },
            basis with { Name = "blue90-potion-not-ready", PotionIn = 60 },
            basis with { Name = "blue90-red-insufficient", Soul = 40, ExpectImmediateSpend = false },
            basis with { Name = "blue100-no-overcap", Shroud = 100, ExpectImmediateSpend = false },
            basis with { Name = "blue90-combo-expiring", ComboLeft = 2, ExpectImmediateSpend = false },
            basis with { Name = "blue90-design-expiring", DeathsDesignLeft = 2, ExpectImmediateSpend = false },
            basis with { Name = "blue90-no-current-weave", GCD = 0.5f, ExpectImmediateSpend = false },
            basis with { Name = "blue90-spender-cooldown", SoulSpenderIn = 2, ExpectImmediateSpend = false },
            basis with { Name = "blue90-reaver-disabled", Reaver = XanRPR.SoulReaverStrategy.ForceBreak, ExpectImmediateSpend = false },
            basis with { Name = "blue90-enshroud-delayed", Enshroud = OffensiveStrategy.Delay, ExpectImmediateSpend = false },
            basis with { Name = "blue90-red-delayed", RedGauge = XanRPR.RedGaugeStrategy.Delay, ExpectImmediateSpend = false },
            basis with { Name = "blue90-target-loss-imminent", DowntimeIn = 1, ExpectImmediateSpend = false },
            basis with { Name = "blue80-only-one-reaver-fits", Shroud = 80, ArcaneCircleIn = deadlineBoundary + FrameStep },
            basis with { Name = "blue80-two-reavers-fit", Shroud = 80, ArcaneCircleIn = deadlineBoundary + 2.5f + FrameStep, ExpectGluttony = true },
            basis with { Name = "blue70-reserve-upcoming-gluttony", Shroud = 70, GluttonyIn = 5, ArcaneCircleIn = 30, EnshroudIn = 15, ExpectImmediateSpend = false, ExpectGluttony = true, ExpectDelayedGluttony = true },
            basis with { Name = "blue80-reserve-upcoming-gluttony", Shroud = 80, GluttonyIn = 5, ArcaneCircleIn = 30, EnshroudIn = 15, ExpectImmediateSpend = false, ExpectGluttony = true, ExpectDelayedGluttony = true },
            basis with { Name = "blue90-upcoming-gluttony-would-overcap", GluttonyIn = 5 },
            basis with { Name = "blue70-deadline-before-gluttony", Shroud = 70, GluttonyIn = 5, ArcaneCircleIn = deadlineBoundary + FrameStep },
        };
        foreach (var shroud in Enumerable.Range(0, 9).Select(index => index * 10))
        {
            cases.Add(basis with { Name = $"blue{shroud}-gluttony-ready", Shroud = shroud, ExpectGluttony = true });
            cases.Add(basis with { Name = $"blue{shroud}-gluttony-later", Shroud = shroud, GluttonyIn = 30 });
        }
        var boundaryCases = cases.ToArray();
        foreach (var potion in new[] { XanRPR.PotionUseStrategy.Off, XanRPR.PotionUseStrategy.OpenerAndEvenBurst })
        foreach (var test in boundaryCases)
        {
            var expectEnshroud = potion == XanRPR.PotionUseStrategy.Off && test.ArcaneCircleIn > 25 && test.EnshroudIn == 0;
            cases.Add(test with { Name = $"{potion}-{test.Name}", Potion = potion, ExpectImmediateSpend = test.ExpectImmediateSpend && !expectEnshroud, ExpectEnshroud = expectEnshroud });
        }
        if (!boundariesOnly)
        {
            foreach (var skillSpeed in new[] { 420, 800, 1500, 2500 })
            foreach (var potion in Enum.GetValues<XanRPR.PotionUseStrategy>())
                cases.Add(new($"natural-sks{skillSpeed}-{potion}") { SkillSpeed = skillSpeed, Potion = potion });
        }

        var failures = new List<string>();
        var checks = 0;
        foreach (var test in cases)
        {
            var runner = new ContinuousTimelineRunner(job, 0, test.Boundary ? test.ExpectDelayedGluttony ? 10 : 5 : 540, [], null, []);
            var result = runner.RunRprPotionTest(test);
            checks += result.Checks;
            failures.AddRange(result.Failures);
        }
        Console.WriteLine($"rpr_potion_cases={cases.Count} checks={checks} failures={failures.Count}");
        foreach (var failure in failures)
            Console.WriteLine(failure);
        return failures.Count == 0 ? 0 : 3;
    }

    private static int RunDmuFullTimeline(HarnessOptions options)
    {
        if (options.ZoneID != DancingMadTimelineZoneID)
            throw new ArgumentException($"dmu-full requires --zone {DancingMadTimelineZoneID}.");

        return RunEventTimeline(options, fullDmu: true);
    }

    private static int RunEventTimeline(HarnessOptions options, bool fullDmu = false)
    {
        InitializeBossMod(options.Sqpack);
        var resourcesDirectory = ResolveEventTriggerTimelineRoot(options.TimelineRoot);
        var catalog = EventTriggerTimelineCatalog.Load(resourcesDirectory);
        var timeline = catalog.TimelineForZone(options.ZoneID);
        var dmuSchedule = options.ZoneID == DancingMadTimelineZoneID ? DmuPhaseSchedule.From(timeline) : null;
        var duration = options.Duration ?? (fullDmu ? dmuSchedule!.FullDuration : DefaultDuration(timeline, options.ZoneID));
        var windows = timeline.TargetUnavailableWindows(duration).ToArray();
        var jobs = SelectJobs(options.JobSelector);
        var failures = ValidateTimelineInput(timeline, options.ZoneID, duration, windows).ToList();

        Console.WriteLine($"event_trigger_resources={catalog.ResourcesDirectory}");
        Console.WriteLine($"event_trigger_timeline_files={catalog.Summary.TimelineFiles}");
        Console.WriteLine($"event_trigger_zone_mappings={catalog.Summary.ZoneMappings}");
        Console.WriteLine($"event_trigger_zone={options.ZoneID} file={timeline.FileName} duration={duration:f1}");
        Console.WriteLine($"event_trigger_target_loss={FormatWindows(windows)}");
        ContinuousTimelineRunner.TargetLossHintLead = options.TargetLossHintLead;
        ContinuousTimelineRunner.PlayerLevel = options.PlayerLevel;
        ContinuousTimelineRunner.PlayerSkillSpeed = options.SkillSpeed;
        ContinuousTimelineRunner.Potions = options.Potions;
        ContinuousTimelineRunner.ExtraTargets = options.ExtraTargets;
        ContinuousTimelineRunner.MnkEncounterHintOverride = options.MnkEncounterHint;
        ContinuousTimelineRunner.BlmRotationOverride = options.BlmRotation;
        ContinuousTimelineRunner.TrackOverrides = options.TrackOverrides;
        Console.WriteLine($"blm_rotation={options.BlmRotation ?? "default"}");
        Console.WriteLine($"target_loss_hints={options.TargetLossHintLead?.ToString("f1", System.Globalization.CultureInfo.InvariantCulture) ?? "off"}");
        Console.WriteLine(FormattableString.Invariant($"player_level={options.PlayerLevel}"));
        // printed only when overridden, so runs that do not ask for gear keep their output comparable to older logs
        if (options.SkillSpeed is { } skillSpeed)
            Console.WriteLine(FormattableString.Invariant($"skill_speed={skillSpeed}"));
        // same rule for the potion stock: a run that asks for none keeps the output of older logs
        if (options.Potions is { } potionCount)
            Console.WriteLine(FormattableString.Invariant($"potions={potionCount}"));
        // same rule for the track pins: a run that pins nothing keeps the output of older logs
        foreach (var (trackName, optionName) in options.TrackOverrides)
            Console.WriteLine($"track_override={trackName}={optionName}");
        ContinuousTimelineRunner.PartyBuffFirstCast = options.PartyBuffFirstCast;
        Console.WriteLine($"party_buffs={options.PartyBuffFirstCast?.ToString("f1", System.Globalization.CultureInfo.InvariantCulture) ?? "off"}");
        ContinuousTimelineRunner.RandomDisengageSeed = options.RandomDisengageSeed;
        ContinuousTimelineRunner.DisengageForecastEnabled = options.DisengageForecastEnabled;
        ContinuousTimelineRunner.StartSoulsow = options.StartSoulsow;
        if (options.RandomDisengageSeed is { } disengageSeed)
            Console.WriteLine($"random_disengage={disengageSeed} forecast={(options.DisengageForecastEnabled ? "on" : "off")}");
        ApplyIrregularOptions(options);
        if (dmuSchedule != null)
        {
            Console.WriteLine($"dmu_source_schedule=p1_loss={dmuSchedule.P1TargetLossStart:f1};p2={dmuSchedule.P2Start:f1}-{dmuSchedule.P2End:f1};p3={dmuSchedule.P3Start:f1};p4={dmuSchedule.P4Start:f1};p5={dmuSchedule.P5Start:f1};end={dmuSchedule.FullDuration:f1}");
            Console.WriteLine($"dmu_source_actions={timeline.Actions.Count}");
        }

        foreach (var job in jobs)
        {
            var result = new ContinuousTimelineRunner(job, options.ZoneID, duration, windows, dmuSchedule, dmuSchedule != null ? timeline.Actions : []).Run();
            failures.AddRange(result.Failures.Select(failure => $"{job.Name}: {failure}"));
            Console.WriteLine($"job={job.Name} frames={result.Frames} actions={result.Actions} gcd={result.GCDActions} ogcd={result.OGCDActions} target_loss_actions={result.EnemyTargetActionsDuringTargetLoss} resume_gcd={result.ResumeGCDActions} dmu_events={result.DmuTimelineEventsEmitted} dmu_events_skipped={result.DmuTimelineEventsSkipped} dmu_damage_frames={result.DmuPredictedDamageFrames} roe={result.RiddleOfEarthActions} dmu_phase={result.ActiveBossPhase?.ToString() ?? "n/a"} failures={result.Failures.Count} {result.Metrics.Format()}");
            foreach (var failure in result.Failures)
                Console.WriteLine($"  {failure}");
            PrintIrregularTotals(job);
            BurstControl.Print(Console.Out, job.Name);
        }

        if (ContinuousTimelineRunner.ActionCounts is { } eventCounts)
            foreach (var (action, count) in eventCounts.OrderByDescending(kv => kv.Value))
                Console.WriteLine(FormattableString.Invariant($"action_count action={(action.Type == ActionType.Spell ? Service.LuminaRow<Lumina.Excel.Sheets.Action>(action.ID)?.Name.ToString() ?? "?" : action.ToString()).Replace(' ', '_')}({action.ID}) count={count}"));
        ExecProfile.Print(Console.Out); // XAN_HARNESS_EXEC_PROFILE=1: per-call Execute cost and allocations of these runs
        Console.WriteLine($"failures={failures.Count}");
        foreach (var failure in failures)
            Console.WriteLine(failure);
        return failures.Count == 0 ? 0 : 3;
    }

    private static int RunTimelineMatrix(HarnessOptions options, bool combatMatrix = false)
    {
        InitializeBossMod(options.Sqpack);
        var resourcesDirectory = ResolveEventTriggerTimelineRoot(options.TimelineRoot);
        var catalog = EventTriggerTimelineCatalog.Load(resourcesDirectory);
        var jobs = SelectJobs(options.JobSelector);
        var targetLossWarmups = combatMatrix ? CombatMatrixTargetLossWarmups : MatrixTargetLossWarmups;
        var scenarios = BuildTimelineMatrix(catalog, targetLossWarmups)
            .Where(scenario => options.ScenarioFilter == null || scenario.Name.Contains(options.ScenarioFilter, StringComparison.OrdinalIgnoreCase))
            .Take(options.ScenarioLimit ?? int.MaxValue)
            // Extra plain uptime after the last target-return, so 2-minute bursts shifted by a few seconds are not cut off by the scenario end.
            .Select(scenario => options.ExtraPostRoll is { } extra ? scenario with { Duration = scenario.Duration + extra } : scenario)
            .ToArray();
        var dmuTimeline = catalog.TimelineForZone(DancingMadTimelineZoneID);
        var dmuSchedule = DmuPhaseSchedule.From(dmuTimeline);
        var failures = new List<string>();

        Console.WriteLine($"event_trigger_resources={catalog.ResourcesDirectory}");
        Console.WriteLine($"event_trigger_timeline_files={catalog.Summary.TimelineFiles}");
        Console.WriteLine($"event_trigger_zone_mappings={catalog.Summary.ZoneMappings}");
        Console.WriteLine($"timeline_matrix_mode={(combatMatrix ? "combat" : "targetability")}");
        Console.WriteLine($"timeline_matrix_target_loss_warmups={string.Join(',', targetLossWarmups.Select(warmup => warmup.ToString("f0")))}");
        Console.WriteLine($"timeline_matrix_scenarios={scenarios.Length}");
        Console.WriteLine($"timeline_matrix_limit={options.ScenarioLimit?.ToString() ?? "none"}");
        Console.WriteLine(FormattableString.Invariant($"timeline_matrix_repeat={options.Repeat}"));
        Console.WriteLine($"matrix_post_roll={options.ExtraPostRoll?.ToString("f1", System.Globalization.CultureInfo.InvariantCulture) ?? "off"}");
        ContinuousTimelineRunner.TargetLossHintLead = options.TargetLossHintLead;
        ContinuousTimelineRunner.PlayerLevel = options.PlayerLevel;
        ContinuousTimelineRunner.PlayerSkillSpeed = options.SkillSpeed;
        ContinuousTimelineRunner.Potions = options.Potions;
        ContinuousTimelineRunner.ExtraTargets = options.ExtraTargets;
        ContinuousTimelineRunner.MnkEncounterHintOverride = options.MnkEncounterHint;
        ContinuousTimelineRunner.BlmRotationOverride = options.BlmRotation;
        ContinuousTimelineRunner.TrackOverrides = options.TrackOverrides;
        Console.WriteLine($"blm_rotation={options.BlmRotation ?? "default"}");
        Console.WriteLine($"target_loss_hints={options.TargetLossHintLead?.ToString("f1", System.Globalization.CultureInfo.InvariantCulture) ?? "off"}");
        Console.WriteLine(FormattableString.Invariant($"player_level={options.PlayerLevel}"));
        // printed only when overridden, so runs that do not ask for gear keep their output comparable to older logs
        if (options.SkillSpeed is { } skillSpeed)
            Console.WriteLine(FormattableString.Invariant($"skill_speed={skillSpeed}"));
        // same rule for the potion stock: a run that asks for none keeps the output of older logs
        if (options.Potions is { } potionCount)
            Console.WriteLine(FormattableString.Invariant($"potions={potionCount}"));
        // same rule for the track pins: a run that pins nothing keeps the output of older logs
        foreach (var (trackName, optionName) in options.TrackOverrides)
            Console.WriteLine($"track_override={trackName}={optionName}");
        ContinuousTimelineRunner.PartyBuffFirstCast = options.PartyBuffFirstCast;
        Console.WriteLine($"party_buffs={options.PartyBuffFirstCast?.ToString("f1", System.Globalization.CultureInfo.InvariantCulture) ?? "off"}");
        ContinuousTimelineRunner.RandomDisengageSeed = options.RandomDisengageSeed;
        ContinuousTimelineRunner.DisengageForecastEnabled = options.DisengageForecastEnabled;
        ContinuousTimelineRunner.StartSoulsow = options.StartSoulsow;
        if (options.RandomDisengageSeed is { } disengageSeed)
            Console.WriteLine($"random_disengage={disengageSeed} forecast={(options.DisengageForecastEnabled ? "on" : "off")}");
        ApplyIrregularOptions(options);

        foreach (var job in jobs)
        {
            var aggregate = new TimelineMatrixAggregate();
            foreach (var scenario in scenarios)
            {
                if (options.ScenarioFilter != null)
                    Console.WriteLine($"scenario={scenario.Name} duration={scenario.Duration:f1} windows={FormatWindows(scenario.TargetUnavailableWindows)}");
                for (var pull = 0; pull < options.Repeat; ++pull)
                {
                    // the reset belongs between scenarios, not between repeats of one - repeats are the same fight
                    if (pull == 0)
                        ResetGnbLearnedWindows();
                    var result = new ContinuousTimelineRunner(job, scenario.ZoneID, scenario.Duration, scenario.TargetUnavailableWindows, null, []).Run();
                    aggregate.Add(result);
                    DumpGnbLearnedWindows($"{scenario.Name} pull={pull + 1}");
                    foreach (var failure in result.Failures)
                        failures.Add($"{job.Name}: {scenario.Name}: pull={pull + 1}: {failure}");
                }
            }

            var dmuResult = new ContinuousTimelineRunner(job, DancingMadTimelineZoneID, dmuSchedule.FullDuration, dmuTimeline.TargetUnavailableWindows(dmuSchedule.FullDuration).ToArray(), dmuSchedule, dmuTimeline.Actions).Run();
            aggregate.Add(dmuResult);
            foreach (var failure in dmuResult.Failures)
                failures.Add($"{job.Name}: dmu-full: {failure}");

            Console.WriteLine($"job={job.Name} scenarios={aggregate.Scenarios} frames={aggregate.Frames} actions={aggregate.Actions} gcd={aggregate.GCDActions} ogcd={aggregate.OGCDActions} target_loss_actions={aggregate.EnemyTargetActionsDuringTargetLoss} resume_gcd={aggregate.ResumeGCDActions} dmu_events={dmuResult.DmuTimelineEventsEmitted} dmu_events_skipped={dmuResult.DmuTimelineEventsSkipped} dmu_damage_frames={dmuResult.DmuPredictedDamageFrames} roe={dmuResult.RiddleOfEarthActions} dmu_phase={dmuResult.ActiveBossPhase?.ToString() ?? "n/a"} failures={aggregate.Failures} {aggregate.Metrics.Format()}");
            if (ContinuousTimelineRunner.RandomDisengageSeed != null)
            {
                Console.WriteLine($"disengage job={job.Name} events={ContinuousTimelineRunner.DisengageEvents} moving_frames={ContinuousTimelineRunner.DisengageMovingFrames} interrupted_casts={ContinuousTimelineRunner.DisengageInterruptedCasts}");
                ContinuousTimelineRunner.DisengageEvents = ContinuousTimelineRunner.DisengageMovingFrames = ContinuousTimelineRunner.DisengageInterruptedCasts = 0;
            }
            PrintIrregularTotals(job);
            BurstControl.Print(Console.Out, job.Name);
        }

        ExecProfile.Print(Console.Out); // XAN_HARNESS_EXEC_PROFILE=1: per-call Execute cost and allocations of these runs
        if (Irregular.Enabled)
            ClientReject.Print(Console.Out, BaseTime); // --irregular implies the client-reject accounting, so the refused pushes are listed per action
        Console.WriteLine($"failures={failures.Count}");
        foreach (var failure in failures)
            Console.WriteLine(failure);
        return failures.Count == 0 ? 0 : 3;
    }

    // --irregular: the events go through the same post-pick refusal path as XAN_HARNESS_CLIENT_REJECT, so the option implies it.
    // Nothing is printed and nothing changes without the option, which keeps every other run byte-identical.
    private static void ApplyIrregularOptions(HarnessOptions options)
    {
        Irregular.Seed = options.IrregularSeed;
        Irregular.RatePerMinute = options.IrregularRate;
        Irregular.Kinds = Irregular.ParseKinds(options.IrregularKinds);
        if (!Irregular.Enabled)
            return;
        ClientReject.Enabled = true;
        ClientReject.BaseTime = BaseTime;
        Console.WriteLine(FormattableString.Invariant($"irregular={options.IrregularSeed} rate={options.IrregularRate:f1} kinds={Irregular.FormatKinds(Irregular.Kinds)}"));
    }

    private static void PrintIrregularTotals(JobAdapter job)
    {
        if (!Irregular.Enabled)
            return;
        Console.WriteLine(FormattableString.Invariant($"irregular job={job.Name} seed={Irregular.Seed} rate={Irregular.RatePerMinute:f1} kinds={Irregular.FormatKinds(Irregular.Kinds)} {ContinuousTimelineRunner.IrregularTotals.Format()}"));
        ContinuousTimelineRunner.IrregularTotals = IrregularDriver.Stats.Empty;
    }

    // irregular-compare: every selected job runs the combat matrix once without events (baseline) and once per (kind set, seed, rate),
    // and the loss beyond the unavoidable time is reported per scenario and per job. The baseline is the same scenario, route and
    // enforcement with no episodes, so the normalized extra_loss_ratio isolates what the rotation lost on top of the stopped GCDs.
    private static int RunIrregularCompare(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var catalog = EventTriggerTimelineCatalog.Load(ResolveEventTriggerTimelineRoot(options.TimelineRoot));
        var jobs = SelectJobs(options.JobSelector);
        var scenarios = BuildTimelineMatrix(catalog, CombatMatrixTargetLossWarmups)
            .Where(scenario => options.ScenarioFilter == null || scenario.Name.Contains(options.ScenarioFilter, StringComparison.OrdinalIgnoreCase))
            .Take(options.ScenarioLimit ?? int.MaxValue)
            .Select(scenario => options.ExtraPostRoll is { } extra ? scenario with { Duration = scenario.Duration + extra } : scenario)
            .ToArray();
        ContinuousTimelineRunner.TargetLossHintLead = options.TargetLossHintLead;
        ContinuousTimelineRunner.PlayerLevel = options.PlayerLevel;
        ContinuousTimelineRunner.PlayerSkillSpeed = options.SkillSpeed;
        ContinuousTimelineRunner.Potions = options.Potions;
        ContinuousTimelineRunner.ExtraTargets = options.ExtraTargets;
        ContinuousTimelineRunner.MnkEncounterHintOverride = options.MnkEncounterHint;
        ContinuousTimelineRunner.BlmRotationOverride = options.BlmRotation;
        ContinuousTimelineRunner.TrackOverrides = options.TrackOverrides;
        ContinuousTimelineRunner.PartyBuffFirstCast = options.PartyBuffFirstCast;
        ContinuousTimelineRunner.StartSoulsow = options.StartSoulsow;
        ContinuousTimelineRunner.RandomDisengageSeed = null;
        var seeds = (options.IrregularSeeds ?? options.IrregularSeed?.ToString(inv) ?? "1,2,3").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.Parse(s, inv)).ToArray();
        var rates = (options.IrregularRates ?? options.IrregularRate.ToString(inv)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => float.Parse(s, inv)).ToArray();
        var kindSets = (options.IrregularKindSets ?? options.IrregularKinds).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Irregular.ParseKinds).ToArray();
        ClientReject.Enabled = true;
        ClientReject.BaseTime = BaseTime;
        Console.WriteLine($"irregular_compare jobs={string.Join(',', jobs.Select(j => j.Name))} scenarios={scenarios.Length} seeds={string.Join(',', seeds)} rates={string.Join(',', rates.Select(r => r.ToString("f1", inv)))} kinds={string.Join(';', kindSets.Select(Irregular.FormatKinds))} level={options.PlayerLevel} skill_speed={options.SkillSpeed?.ToString(inv) ?? "default"} tracks={string.Join(';', options.TrackOverrides.Select(t => $"{t.Track}={t.Option}"))}");
        using var csv = options.IrregularOut != null ? new StreamWriter(options.IrregularOut) : null;
        // expected = block_s x the baseline's potency per targetable second: block_s leaves out lockouts that cannot stop this job's GCD
        // (Silence on a melee, Amnesia), so extra_loss_ratio = 0 means exactly the stopped-GCD time was lost
        csv?.WriteLine("job,kinds,seed,rate,scenario,duration,episodes,ep_seconds,block_s,lockout_s,los_s,range_s,loss_s,base_total,irr_total,base_potency,irr_potency,lost,expected,extra_loss_ratio,base_gcds,irr_gcds,gcd_in,gcd_lost_expected,base_idle,irr_idle_in,irr_idle_out,resume_avg,resume_max,resume_excess_avg,resume_excess_max,no_resume,refused_in,refused_out,cast_interrupts,base_failures,irr_failures,exception");

        (TimelineResult? Result, string Exception) RunScenario(JobAdapter job, TimelineMatrixScenario scenario)
        {
            ClientReject.Scenario = scenario.Name;
            try
            {
                return (new ContinuousTimelineRunner(job, scenario.ZoneID, scenario.Duration, scenario.TargetUnavailableWindows, null, []).Run(), "");
            }
            catch (Exception ex)
            {
                Irregular.Current = null;
                return (null, $"{ex.GetType().Name}: {ex.Message.Split('\n')[0].Trim()}");
            }
        }

        var exitCode = 0;
        var summary = new List<string>();
        foreach (var job in jobs)
        {
            // BLM's self-tests run inside the first BLM run of a process and touch that run's world, so burn one run before measuring.
            Irregular.Seed = null;
            Irregular.EnforceBaseline = true;
            if (job.Class == Class.BLM && scenarios.Length > 0)
                RunScenario(job, scenarios[0]);
            var baseline = scenarios.Select(scenario => RunScenario(job, scenario)).ToArray();
            var baseExceptions = baseline.Count(b => b.Result == null);
            var baseGcdLength = baseline.Sum(b => b.Result?.Metrics.GcdActions ?? 0) > 0 ? baseline.Sum(b => (b.Result?.Metrics.TargetFrames ?? 0) * FrameStep) / baseline.Sum(b => b.Result?.Metrics.GcdActions ?? 0) : DefaultGCD;
            Console.WriteLine(FormattableString.Invariant($"irregular_baseline job={job.Name} total={baseline.Sum(b => b.Result?.Metrics.Total ?? 0):f0} gcds={baseline.Sum(b => b.Result?.Metrics.GcdActions ?? 0)} idle={baseline.Sum(b => b.Result?.Metrics.GcdIdleFrames ?? 0)} failures={baseline.Sum(b => b.Result?.Failures.Count ?? 0)} exceptions={baseExceptions} avg_gcd={baseGcdLength:f3}"));
            foreach (var kinds in kindSets)
            foreach (var seed in seeds)
            foreach (var rate in rates)
            {
                Irregular.Seed = seed;
                Irregular.RatePerMinute = rate;
                Irregular.Kinds = kinds;
                ClientReject.Reset();
                ContinuousTimelineRunner.IrregularTotals = IrregularDriver.Stats.Empty;
                double lost = 0, expected = 0, baseTotal = 0, irrTotal = 0;
                int baseGcds = 0, irrGcds = 0, baseIdle = 0, irrFailures = 0, exceptions = 0, baseFailures = 0;
                var stats = IrregularDriver.Stats.Empty;
                var kindsName = Irregular.FormatKinds(kinds);
                for (var i = 0; i < scenarios.Length; ++i)
                {
                    var scenario = scenarios[i];
                    var (irr, exception) = RunScenario(job, scenario);
                    var basis = baseline[i].Result;
                    if (irr == null || basis == null)
                    {
                        ++exceptions;
                        exitCode = 3;
                        Console.WriteLine($"irregular_exception job={job.Name} kinds={kindsName} seed={seed} rate={rate.ToString("f1", inv)} scenario={scenario.Name} {(irr == null ? exception : "baseline: " + baseline[i].Exception)}");
                        csv?.WriteLine(FormattableString.Invariant($"{job.Name},{kindsName},{seed},{rate:f1},{scenario.Name.Replace(',', ';')},{scenario.Duration:f1},,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,{(irr == null ? exception : "baseline: " + baseline[i].Exception).Replace(',', ';')}"));
                        continue;
                    }
                    var s = irr.Irregular;
                    var targetSeconds = basis.Metrics.TargetFrames * FrameStep;
                    var perSecond = targetSeconds > 0 ? basis.Metrics.Total / targetSeconds : 0;
                    var scenarioLost = basis.Metrics.Total - irr.Metrics.Total;
                    var scenarioExpected = s.BlockSeconds * perSecond;
                    lost += scenarioLost;
                    expected += scenarioExpected;
                    baseTotal += basis.Metrics.Total;
                    irrTotal += irr.Metrics.Total;
                    baseGcds += basis.Metrics.GcdActions;
                    irrGcds += irr.Metrics.GcdActions;
                    baseIdle += basis.Metrics.GcdIdleFrames;
                    baseFailures += basis.Failures.Count;
                    irrFailures += irr.Failures.Count;
                    stats = stats.Add(s);
                    csv?.WriteLine(FormattableString.Invariant($"{job.Name},{kindsName},{seed},{rate:f1},{scenario.Name.Replace(',', ';')},{scenario.Duration:f1},{s.Episodes},{s.Seconds:f2},{s.BlockSeconds:f2},{s.LockoutSeconds:f2},{s.LosSeconds:f2},{s.RangeSeconds:f2},{s.LossSeconds:f2},{basis.Metrics.Total:f1},{irr.Metrics.Total:f1},{basis.Metrics.Potency:f1},{irr.Metrics.Potency:f1},{scenarioLost:f1},{scenarioExpected:f1},{(scenarioExpected > 0 ? (scenarioLost - scenarioExpected) / scenarioExpected : 0):f4},{basis.Metrics.GcdActions},{irr.Metrics.GcdActions},{s.GCDsInside},{s.BlockSeconds / baseGcdLength:f2},{basis.Metrics.GcdIdleFrames},{s.IdleIn},{s.IdleOut},{s.ResumeAverage:f3},{s.ResumeMax:f3},{s.ResumeExcessAverage:f3},{s.ResumeExcessMax:f3},{s.NoResume},{s.RefusedIn},{s.RefusedOut},{s.CastInterrupts},{basis.Failures.Count},{irr.Failures.Count},"));
                }
                var ratio = expected > 0 ? (lost - expected) / expected : 0;
                var line = FormattableString.Invariant($"irregular_result job={job.Name} kinds={kindsName} seed={seed} rate={rate:f1} scenarios={scenarios.Length} episodes={stats.Episodes} ep_seconds={stats.Seconds:f1} block_s={stats.BlockSeconds:f1} base_total={baseTotal:f0} irr_total={irrTotal:f0} lost={lost:f0} expected={expected:f0} extra_loss_ratio={ratio:f4} gcd_lost={baseGcds - irrGcds} gcd_lost_expected={stats.BlockSeconds / baseGcdLength:f1} gcd_in={stats.GCDsInside} idle_in={stats.IdleIn} idle_out_extra={stats.IdleOut - baseIdle} resume_avg={stats.ResumeAverage:f3} resume_max={stats.ResumeMax:f2} resume_excess_avg={stats.ResumeExcessAverage:f3} resume_excess_max={stats.ResumeExcessMax:f2} no_resume={stats.NoResume} refused_in={stats.RefusedIn} refused_out={stats.RefusedOut} cast_interrupts={stats.CastInterrupts} base_failures={baseFailures} irr_failures={irrFailures} exceptions={exceptions}");
                Console.WriteLine(line);
                summary.Add(line);
                csv?.WriteLine(FormattableString.Invariant($"{job.Name},{kindsName},{seed},{rate:f1},ALL,,{stats.Episodes},{stats.Seconds:f2},{stats.BlockSeconds:f2},{stats.LockoutSeconds:f2},{stats.LosSeconds:f2},{stats.RangeSeconds:f2},{stats.LossSeconds:f2},{baseTotal:f1},{irrTotal:f1},,,{lost:f1},{expected:f1},{ratio:f4},{baseGcds},{irrGcds},{stats.GCDsInside},{stats.BlockSeconds / baseGcdLength:f2},{baseIdle},{stats.IdleIn},{stats.IdleOut},{stats.ResumeAverage:f3},{stats.ResumeMax:f3},{stats.ResumeExcessAverage:f3},{stats.ResumeExcessMax:f3},{stats.NoResume},{stats.RefusedIn},{stats.RefusedOut},{stats.CastInterrupts},{baseFailures},{irrFailures},{exceptions}"));
                csv?.Flush();
                // the refused pushes of this configuration, per action, the way client_reject prints them
                using var refusals = new StringWriter();
                ClientReject.Print(refusals, BaseTime);
                foreach (var refusal in refusals.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => l.StartsWith("client_reject job=")).Take(8))
                    Console.WriteLine($"irregular_refusals kinds={kindsName} seed={seed} rate={rate.ToString("f1", inv)} {refusal.Trim()}");
            }
        }
        Irregular.Seed = null;
        Irregular.EnforceBaseline = false;
        Console.WriteLine("irregular_summary");
        foreach (var line in summary)
            Console.WriteLine(line);
        return exitCode;
    }

    private static IEnumerable<TimelineMatrixScenario> BuildTimelineMatrix(EventTriggerTimelineCatalog catalog, IReadOnlyList<float> targetLossWarmups)
    {
        var zonesByTimeline = catalog.TimelinesByZone
            .GroupBy(entry => entry.Value.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Min(entry => entry.Key), StringComparer.OrdinalIgnoreCase);

        foreach (var timeline in catalog.Timelines.OrderBy(timeline => timeline.FileName, StringComparer.OrdinalIgnoreCase))
        {
            if (timeline.FileName.Equals("dancing_mad.txt", StringComparison.OrdinalIgnoreCase))
                continue;

            var zoneID = zonesByTimeline.GetValueOrDefault(timeline.FullPath);
            var fullDuration = DefaultDuration(timeline, zoneID);
            // One scenario per timeline, start to finish, so a run sees every downtime of the fight in order. Live play only
            // trusts the imported timeline after the first downtime matches it, which a single-window slice can never show.
            if (Environment.GetEnvironmentVariable("XAN_HARNESS_FULL_TIMELINE") == "1")
            {
                yield return new($"zone={zoneID}:file={timeline.FileName}:full", zoneID, fullDuration, SliceTargetUnavailableWindows(timeline, 0, fullDuration));
                continue;
            }

            var openingDuration = Math.Min(MatrixOpeningDuration, fullDuration);
            yield return new($"zone={zoneID}:file={timeline.FileName}:opening", zoneID, openingDuration, SliceTargetUnavailableWindows(timeline, 0, openingDuration));

            var windows = timeline.TargetUnavailableWindows(fullDuration);
            for (var index = 0; index < windows.Count; ++index)
            {
                var window = windows[index];
                var downtimeDuration = window.End - window.Start;
                if (downtimeDuration <= MatrixMaximumDowntimeDuration)
                {
                    var start = Math.Max(0, window.Start - MatrixTargetLossPreRoll);
                    var end = Math.Min(fullDuration, window.End + MatrixTargetReturnPostRoll + ResumeGCDLimit);
                    foreach (var scenario in AddTargetLossWarmups(new($"zone={zoneID}:file={timeline.FileName}:downtime={index + 1}", zoneID, end - start, SliceTargetUnavailableWindows(timeline, start, end)), targetLossWarmups))
                        yield return scenario;
                }
                else
                {
                    var lossStart = Math.Max(0, window.Start - MatrixTargetLossPreRoll);
                    var lossEnd = Math.Min(fullDuration, window.Start + MatrixMaximumDowntimeDuration);
                    foreach (var scenario in AddTargetLossWarmups(new($"zone={zoneID}:file={timeline.FileName}:downtime={index + 1}:loss", zoneID, lossEnd - lossStart, SliceTargetUnavailableWindows(timeline, lossStart, lossEnd)), targetLossWarmups))
                        yield return scenario;

                    var returnStart = Math.Max(0, window.End - MatrixTargetLossPreRoll);
                    var returnEnd = Math.Min(fullDuration, window.End + MatrixTargetReturnPostRoll + ResumeGCDLimit);
                    foreach (var scenario in AddTargetLossWarmups(new($"zone={zoneID}:file={timeline.FileName}:downtime={index + 1}:return", zoneID, returnEnd - returnStart, SliceTargetUnavailableWindows(timeline, returnStart, returnEnd)), targetLossWarmups))
                        yield return scenario;
                }
            }
        }
    }

    private static IEnumerable<TimelineMatrixScenario> AddTargetLossWarmups(TimelineMatrixScenario scenario, IReadOnlyList<float> warmups)
    {
        foreach (var warmup in warmups)
        {
            var windows = scenario.TargetUnavailableWindows
                .Select(window => new EventTriggerTimelineWindow(window.Start + warmup, window.End + warmup))
                .ToArray();
            yield return new($"{scenario.Name}:warmup={warmup:f0}", scenario.ZoneID, scenario.Duration + warmup, windows);
        }
    }

    private static IReadOnlyList<EventTriggerTimelineWindow> SliceTargetUnavailableWindows(EventTriggerTimeline timeline, float start, float end)
        => timeline.TargetUnavailableWindows(end)
            .Where(window => window.End > start && window.Start < end)
            .Select(window => new EventTriggerTimelineWindow(Math.Max(0, window.Start - start), Math.Min(end - start, window.End - start)))
            .Where(window => window.End > window.Start)
            .ToArray();

    // GNB_RESET_LEARNED=1 empties AkechiGNB's static learned target-loss window list between scenarios. The list is
    // deliberately static so a window learned on one pull carries into the next, but the matrix reuses one zone id
    // for many different synthetic downtime schedules, so windows learned in one scenario can mispredict the next.
    // Toggling this isolates that effect from a genuine rotation stall.
    private static FieldInfo? _gnbLearnedWindowsField;
    private static void ResetGnbLearnedWindows()
    {
        if (Environment.GetEnvironmentVariable("GNB_RESET_LEARNED") != "1")
            return;
        _gnbLearnedWindowsField ??= typeof(AkechiGNB).GetField("Planner74LearnedTargetLossWindows", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AkechiGNB.Planner74LearnedTargetLossWindows not found.");
        (_gnbLearnedWindowsField.GetValue(null) as System.Collections.IList)?.Clear();
    }

    // GNB_DEBUG_LEARNED=1 prints AkechiGNB's learned target-loss windows after each pull, so the confirmation
    // counting can be checked directly instead of inferred from whether behaviour changed.
    private static void DumpGnbLearnedWindows(string label)
    {
        if (Environment.GetEnvironmentVariable("GNB_DEBUG_LEARNED") != "1")
            return;
        _gnbLearnedWindowsField ??= typeof(AkechiGNB).GetField("Planner74LearnedTargetLossWindows", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("AkechiGNB.Planner74LearnedTargetLossWindows not found.");
        if (_gnbLearnedWindowsField.GetValue(null) is not System.Collections.IList list)
            return;
        Console.Error.WriteLine($"learned[{label}] count={list.Count}");
        foreach (var entry in list)
        {
            if (entry == null)
                continue;
            var type = entry.GetType();
            string F(string name) => type.GetField(name)?.GetValue(entry)?.ToString() ?? "?";
            Console.Error.WriteLine(FormattableString.Invariant(
                $"  zone={F("ZoneID")} boss={F("BossOID")} start={F("Start")} end={F("End")} conf={F("Confirmations")} seen={F("SeenThisPull")} checked={F("CheckedThisPull")}"));
        }
    }

    private static IReadOnlyList<JobAdapter> SelectJobs(string selector)
    {
        if (selector.Equals("all", StringComparison.OrdinalIgnoreCase))
            return Jobs;

        var requested = selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requested.Length == 0)
            throw new ArgumentException("Specify --job all or a comma-separated subset of drg,mch,mnk,blm,sam,vpr,rpr,gnb,gnbx,pld,pldx.");

        List<JobAdapter> result = [];
        foreach (var name in requested)
        {
            var job = Jobs.FirstOrDefault(job => job.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (job == null)
                throw new ArgumentException($"Unknown job '{name}'. Specify drg,mch,mnk,blm,sam,vpr,rpr,gnb,gnbx,pld,pldx, or all.");
            if (!result.Contains(job))
                result.Add(job);
        }
        return result;
    }

    // A track name that does not resolve has to be loud: falling back to the default would look like a setting that
    // changes nothing, which is exactly the conclusion the run is meant to test.
    private static void ApplyTrackOverrides(JobAdapter job, StrategyValues strategy, IReadOnlyList<(string Track, string Option)> overrides)
    {
        foreach (var (trackName, optionName) in overrides)
        {
            var trackIndex = strategy.Configs.FindIndex(config => config is StrategyConfigTrack track && track.InternalName.Equals(trackName, StringComparison.OrdinalIgnoreCase));
            if (trackIndex < 0)
            {
                var availableTracks = string.Join(", ", strategy.Configs.OfType<StrategyConfigTrack>().Select(track => track.InternalName));
                throw new ArgumentException($"Unknown strategy track '{trackName}' for job '{job.Name}'. Available tracks: {availableTracks}");
            }
            var trackConfig = (StrategyConfigTrack)strategy.Configs[trackIndex];
            var optionIndex = trackConfig.Options.FindIndex(option => option.InternalName.Equals(optionName, StringComparison.OrdinalIgnoreCase));
            if (optionIndex < 0)
            {
                var availableOptions = string.Join(", ", trackConfig.Options.Select(option => option.InternalName));
                throw new ArgumentException($"Unknown option '{optionName}' for track '{trackConfig.InternalName}' of job '{job.Name}'. Available options: {availableOptions}");
            }
            ((StrategyValueTrack)strategy.Values[trackIndex]).Option = optionIndex;
        }
    }

    private static IEnumerable<string> ValidateTimelineInput(EventTriggerTimeline timeline, int zoneID, float duration, IReadOnlyList<EventTriggerTimelineWindow> windows)
    {
        if (zoneID != DancingMadTimelineZoneID)
            yield break;

        if (windows.Count == 0)
            yield return $"Event Trigger zone {zoneID} ({timeline.FileName}) has no target-unavailable window inside {duration:f1}s";
        else if (!windows.Any(window => Math.Abs(window.Start - 197.3f) <= FrameStep && Math.Abs(window.End - 207.6f) <= FrameStep))
            yield return "Event Trigger dancing_mad.txt did not expose the P1-to-P2 197.3s-207.6s target-loss window";
    }

    private static float DefaultDuration(EventTriggerTimeline timeline, int zoneID)
    {
        if (zoneID == DancingMadTimelineZoneID)
            return 220;

        return Math.Max(60, timeline.Events.Count == 0 ? 60 : timeline.Events.Max(entry => entry.Time) + 10);
    }

    // blm-fork-scan: one-step deviations from production BLM.cs, each carried to the end of the fight by BLM.cs itself.
    // For every distinct fight the baseline run records each GCD decision and the GCDs that were legal at that instant; each
    // alternative is then forced at exactly that frame (the runs are deterministic, so everything before it is identical) and
    // the fight is replayed to the end. d_potency is how much damage that single different choice gained or lost by the end.
    private sealed record ForkScanOptions(string Out, string Split, int Shard, int Shards, int Every, bool Combat, float EndGuard, string? SeriesOut, int Depth = 1);

    private static (string[] Remaining, ForkScanOptions Fork) ExtractForkScanOptions(string[] args)
    {
        string? output = null;
        string? seriesOut = null;
        var split = "search";
        int shard = 0, shards = 1, every = 1, depth = 1;
        var combat = true;
        var endGuard = 30f;
        var rest = new List<string>();
        for (var i = 0; i < args.Length; ++i)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--fork-out":
                    output = Next();
                    break;
                case "--fork-series-out":
                    seriesOut = Next();
                    break;
                case "--fork-split":
                    split = Next();
                    break;
                case "--fork-shard":
                    var parts = Next().Split('/');
                    shard = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                    shards = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                    break;
                case "--fork-every":
                    // 0 = baseline only: one row per fight, for comparing two builds on the same half of the fights
                    every = Math.Max(0, int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture));
                    break;
                case "--fork-depth":
                    // >1: at each sampled decision, every sequence of choices over the next k GCD decisions, carried to the end
                    depth = Math.Clamp(int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture), 1, 6);
                    break;
                case "--fork-matrix":
                    combat = false;
                    break;
                case "--fork-end-guard":
                    endGuard = float.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture);
                    break;
                default:
                    rest.Add(args[i]);
                    break;
            }
        }
        if (split is not ("search" or "holdout" or "all"))
            throw new ArgumentException("--fork-split must be search, holdout or all.");
        return ([.. rest], new(output ?? throw new ArgumentException("blm-fork-scan needs --fork-out <csv>."), split, shard, shards, every, combat, endGuard, seriesOut, depth));
    }

    private static string ForkScenarioKey(TimelineMatrixScenario scenario)
        => FormattableString.Invariant($"{scenario.ZoneID}:{scenario.Duration:f1}:{FormatWindows(scenario.TargetUnavailableWindows)}");

    // FNV-1a: string.GetHashCode is randomised per process, and the split and the shards must agree across processes.
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return hash;
    }

    private static int RunBlmForkScan(HarnessOptions options, ForkScanOptions fork)
    {
        InitializeBossMod(options.Sqpack);
        var catalog = EventTriggerTimelineCatalog.Load(ResolveEventTriggerTimelineRoot(options.TimelineRoot));
        // Only BLM records decisions to fork from; any job can run the baseline-only A/B of two builds (--fork-every 0).
        var job = SelectJobs(options.JobSelector == "all" ? "blm" : options.JobSelector).Single();
        if (job.Class != Class.BLM && fork.Every != 0)
            throw new ArgumentException("blm-fork-scan forks only BLM; other jobs take --fork-every 0.");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // The combat matrix repeats some fights under several names; a deviation scan needs each distinct fight once.
        var distinct = BuildTimelineMatrix(catalog, fork.Combat ? CombatMatrixTargetLossWarmups : MatrixTargetLossWarmups)
            .Where(scenario => options.ScenarioFilter == null || scenario.Name.Contains(options.ScenarioFilter, StringComparison.OrdinalIgnoreCase))
            .GroupBy(ForkScenarioKey)
            .Select(group => group.First())
            .ToArray();
        // The split belongs to the fight, so a fight used to find a candidate can never be the one that decides whether it ships.
        var scenarios = distinct
            .Where(scenario => fork.Split == "all" || (StableHash(ForkScenarioKey(scenario)) % 2 == 0) == (fork.Split == "search"))
            .Where((_, index) => index % fork.Shards == fork.Shard)
            .Take(options.ScenarioLimit ?? int.MaxValue)
            // --post-roll is applied after the split, so the extra uptime never moves a fight from one half to the other
            .Select(scenario => options.ExtraPostRoll is { } extra ? scenario with { Duration = scenario.Duration + extra } : scenario)
            .ToArray();

        ContinuousTimelineRunner.PlayerLevel = options.PlayerLevel;
        ContinuousTimelineRunner.TargetLossHintLead = options.TargetLossHintLead;
        ContinuousTimelineRunner.PartyBuffFirstCast = options.PartyBuffFirstCast;
        ContinuousTimelineRunner.RandomDisengageSeed = options.RandomDisengageSeed;
        ContinuousTimelineRunner.DisengageForecastEnabled = options.DisengageForecastEnabled;
        ContinuousTimelineRunner.PlayerSkillSpeed = options.SkillSpeed;
        ContinuousTimelineRunner.BlmRotationOverride = options.BlmRotation;
        ContinuousTimelineRunner.TrackOverrides = options.TrackOverrides;
        Console.WriteLine($"fork_scan job={job.Name} distinct={distinct.Length} split={fork.Split} shard={fork.Shard}/{fork.Shards} scenarios={scenarios.Length} every={fork.Every} end_guard={fork.EndGuard.ToString("f0", inv)} matrix={(fork.Combat ? "combat" : "targetability")} blm_rotation={options.BlmRotation ?? "default"} target_loss_hints={options.TargetLossHintLead?.ToString("f1", inv) ?? "off"} post_roll={options.ExtraPostRoll?.ToString("f0", inv) ?? "off"} party_buffs={options.PartyBuffFirstCast?.ToString("f1", inv) ?? "off"} random_disengage={options.RandomDisengageSeed?.ToString(inv) ?? "off"}");
        if (scenarios.Length == 0)
            return 0;

        TimelineResult Run(TimelineMatrixScenario scenario) => new ContinuousTimelineRunner(job, scenario.ZoneID, scenario.Duration, scenario.TargetUnavailableWindows, null, []).Run();

        // BLM's self-tests run inside the first BLM run of a process and touch that run's world, so burn one run before measuring.
        ContinuousTimelineRunner.BlmDecisionLog = null;
        ContinuousTimelineRunner.BlmForce = null;
        Run(scenarios[0]);
        ClientReject.Reset();
        ClientReject.BaseTime = BaseTime;
        ExecProfile.Reset();
        ContinuousTimelineRunner.ActionCounts?.Clear();

        using var csv = new StreamWriter(fork.Out);
        csv.WriteLine("scenario,duration,t,chosen,alt,outcome,base_potency,fork_potency,d_potency,base_total,fork_total,d_total,base_failures,fork_failures,element,mp,hearts,poly,soul,paradox,firestarter,thunderhead,instant,leylines,to_end,to_loss,in_loss,"
            + string.Join(',', ForkHorizons.Select(h => FormattableString.Invariant($"pf{h},bf{h},kf{h},af{h},pb{h},bb{h},kb{h}"))));
        using var series = fork.SeriesOut != null ? new StreamWriter(fork.SeriesOut) : null;
        series?.WriteLine("scenario,duration,t,potency,buffed,avail,key");
        int forks = 0, notApplied = 0, nullMismatches = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        foreach (var scenario in scenarios)
        {
            ClientReject.Scenario = scenario.Name;
            var decisions = new List<BlmCombatState.DecisionPoint>();
            ContinuousTimelineRunner.BlmDecisionLog = decisions;
            ContinuousTimelineRunner.BlmForce = null;
            ContinuousTimelineRunner.BlmSeries = [];
            var baseline = Run(scenario);
            var baseSeries = ContinuousTimelineRunner.BlmSeries;
            ContinuousTimelineRunner.BlmSeries = null;
            ContinuousTimelineRunner.BlmDecisionLog = null;
            if (series != null)
                foreach (var sample in baseSeries)
                    series.WriteLine(FormattableString.Invariant($"{scenario.Name.Replace(',', ';')},{scenario.Duration:f1},{sample.T:f2},{sample.Potency:f1},{sample.Buffed:f1},{(sample.TargetAvailable ? 1 : 0)},{sample.Key}"));
            var basePotency = baseline.Metrics.Potency;
            var baseTotal = baseline.Metrics.Potency + baseline.Metrics.TerminalValue;
            if (fork.Every == 0)
            {
                csv.WriteLine(FormattableString.Invariant($"{scenario.Name.Replace(',', ';')},{scenario.Duration:f1},baseline,{basePotency:f0},{baseline.Metrics.RaidBuffedPotency:f0},{baseline.Metrics.TerminalValue:f0},{baseline.Failures.Count},{baseline.Metrics.GcdIdleFrames}"));
                csv.Flush();
                continue;
            }

            // A null fork forces what the baseline chose anyway. It must reproduce the baseline exactly, or the forcing itself
            // perturbs the run and none of this fight's deltas mean anything.
            if (decisions.Count > 0)
            {
                ContinuousTimelineRunner.BlmForce = (decisions[0].At, decisions[0].Chosen);
                var nullFork = Run(scenario);
                if (nullFork.Metrics.Potency != basePotency || nullFork.Metrics.TerminalValue != baseline.Metrics.TerminalValue || nullFork.Actions != baseline.Actions)
                {
                    ++nullMismatches;
                    Console.WriteLine(FormattableString.Invariant($"null_fork_mismatch scenario={scenario.Name} base={basePotency:f0} null={nullFork.Metrics.Potency:f0} outcome={ContinuousTimelineRunner.LastBlmForceOutcome}"));
                    ContinuousTimelineRunner.BlmForce = null;
                    continue;
                }
            }

            if (fork.Depth > 1)
            {
                forks += ForkDepthSearch(scenario, decisions, baseline, baseSeries, fork, csv, Run, ref notApplied);
                ContinuousTimelineRunner.BlmForce = null;
                ContinuousTimelineRunner.BlmForceFollowups = null;
                csv.Flush();
                continue;
            }

            for (var index = 0; index < decisions.Count; index += fork.Every)
            {
                var decision = decisions[index];
                var t = (float)(decision.At - BaseTime).TotalSeconds;
                var toEnd = scenario.Duration - t;
                if (toEnd < fork.EndGuard)
                    continue;
                var inLoss = scenario.TargetUnavailableWindows.Any(window => t >= window.Start && t < window.End);
                var nextLoss = scenario.TargetUnavailableWindows.Where(window => window.Start > t).Select(window => window.Start - t).DefaultIfEmpty(-1f).Min();
                foreach (var alternative in decision.Alternatives)
                {
                    ContinuousTimelineRunner.BlmForce = (decision.At, alternative);
                    ContinuousTimelineRunner.BlmSeries = [];
                    var result = Run(scenario);
                    var forkSeries = ContinuousTimelineRunner.BlmSeries;
                    ContinuousTimelineRunner.BlmSeries = null;
                    var outcome = ContinuousTimelineRunner.LastBlmForceOutcome;
                    ++forks;
                    if (outcome != "applied")
                        ++notApplied;
                    var potency = result.Metrics.Potency;
                    var total = result.Metrics.Potency + result.Metrics.TerminalValue;
                    csv.WriteLine(FormattableString.Invariant($"{scenario.Name.Replace(',', ';')},{scenario.Duration:f1},{t:f2},{ForkActionName(decision.Chosen)},{ForkActionName(alternative)},{outcome},{basePotency:f0},{potency:f0},{potency - basePotency:f0},{baseTotal:f0},{total:f0},{total - baseTotal:f0},{baseline.Failures.Count},{result.Failures.Count},{decision.Element},{decision.MP},{decision.Hearts},{decision.Polyglot},{decision.AstralSoul},{(decision.Paradox ? 1 : 0)},{(decision.Firestarter ? 1 : 0)},{(decision.Thunderhead ? 1 : 0)},{(decision.InstantCast ? 1 : 0)},{(decision.LeyLines ? 1 : 0)},{toEnd:f1},{nextLoss:f1},{(inLoss ? 1 : 0)},{ForkHorizonColumns(t, baseSeries, forkSeries)}"));
                }
            }
            ContinuousTimelineRunner.BlmForce = null;
            csv.Flush();
        }
        Console.WriteLine(FormattableString.Invariant($"fork_scan_done forks={forks} not_applied={notApplied} null_fork_mismatches={nullMismatches} seconds={started.Elapsed.TotalSeconds:f0} out={fork.Out}"));
        ClientReject.Print(Console.Out, BaseTime);
        ExecProfile.Print(Console.Out);
        if (ContinuousTimelineRunner.ActionCounts is { } counts)
            foreach (var (action, count) in counts.OrderByDescending(kv => kv.Value))
                Console.WriteLine(FormattableString.Invariant($"action_count action={(action.Type == ActionType.Spell ? Service.LuminaRow<Lumina.Excel.Sheets.Action>(action.ID)?.Name.ToString() ?? "?" : action.ToString()).Replace(' ', '_')}({action.ID}) count={count}"));
        return nullMismatches == 0 ? 0 : 3;
    }

    private const float ForkDepthHorizon = 30;

    // --fork-depth k: exhaustive search over the next k GCD decisions from a sampled decision (each node branches on the baseline's
    // own choice plus every legal alternative at that moment), each leaf carried to the end of the fight by BLM.cs. One row per
    // depth d <= k: the best leaf at depth d, both by the fight total and by potency over [t, t+30]. Branching on the baseline's
    // choice reuses the parent's run, so a leaf at depth d also covers every shallower deviation.
    private static int ForkDepthSearch(TimelineMatrixScenario scenario, List<BlmCombatState.DecisionPoint> decisions, TimelineResult baseline, List<BlmSample> baseSeries,
        ForkScanOptions fork, StreamWriter csv, Func<TimelineMatrixScenario, TimelineResult> run, ref int notApplied)
    {
        var runs = 0;
        var notAppliedLocal = 0;
        static float PotencyAt(List<BlmSample> series, float at)
        {
            var i = series.FindIndex(sample => sample.T >= at - 0.001f);
            return i < 0 ? float.NaN : series[i].Potency;
        }
        var baseTotal = baseline.Metrics.Potency + baseline.Metrics.TerminalValue;
        for (var index = 0; index < decisions.Count; index += Math.Max(1, fork.Every))
        {
            var decision = decisions[index];
            var t = (float)(decision.At - BaseTime).TotalSeconds;
            if (scenario.Duration - t < fork.EndGuard)
                continue;
            var basePotAt = PotencyAt(baseSeries, t + ForkDepthHorizon);
            if (float.IsNaN(basePotAt))
                continue;
            var bestTotal = new (double Value, string Seq)[fork.Depth + 1];
            var bestPot = new (double Value, string Seq)[fork.Depth + 1];
            var leaves = new int[fork.Depth + 1];
            // every leaf, not just the best: the maximum over more leaves grows on trajectory noise alone
            var sumTotal = new double[fork.Depth + 1];
            var sumPot = new double[fork.Depth + 1];
            var betterTotal = new int[fork.Depth + 1];
            var betterPot = new int[fork.Depth + 1];
            for (var d = 0; d <= fork.Depth; ++d)
            {
                bestTotal[d] = (double.MinValue, "");
                bestPot[d] = (double.MinValue, "");
            }

            void Visit(List<ActionID> prefix, List<BlmCombatState.DecisionPoint> log, double total, double potAt)
            {
                var depth = prefix.Count;
                if (depth > 0)
                {
                    ++leaves[depth];
                    sumTotal[depth] += total - baseTotal;
                    sumPot[depth] += potAt - basePotAt;
                    if (total > baseTotal + 0.5)
                        ++betterTotal[depth];
                    if (potAt > basePotAt + 0.5)
                        ++betterPot[depth];
                    var seq = string.Join(">", prefix.Select(ForkActionName));
                    if (total > bestTotal[depth].Value)
                        bestTotal[depth] = (total, seq);
                    if (potAt > bestPot[depth].Value)
                        bestPot[depth] = (potAt, seq);
                }
                if (depth == fork.Depth || index + depth >= log.Count)
                    return;
                var next = log[index + depth];
                // the baseline's own choice here continues this very run
                prefix.Add(next.Chosen);
                Visit(prefix, log, total, potAt);
                prefix.RemoveAt(prefix.Count - 1);
                foreach (var alternative in next.Alternatives)
                {
                    prefix.Add(alternative);
                    var childLog = new List<BlmCombatState.DecisionPoint>();
                    ContinuousTimelineRunner.BlmForce = (decision.At, prefix[0]);
                    ContinuousTimelineRunner.BlmForceFollowups = [.. prefix.Skip(1)];
                    ContinuousTimelineRunner.BlmDecisionLog = childLog;
                    ContinuousTimelineRunner.BlmSeries = [];
                    var result = run(scenario);
                    var series = ContinuousTimelineRunner.BlmSeries;
                    ContinuousTimelineRunner.BlmSeries = null;
                    ContinuousTimelineRunner.BlmDecisionLog = null;
                    ++runs;
                    if (ContinuousTimelineRunner.LastBlmForceOutcome == "applied" && ContinuousTimelineRunner.LastBlmFollowupsComplete)
                        Visit(prefix, childLog, result.Metrics.Potency + result.Metrics.TerminalValue, PotencyAt(series, t + ForkDepthHorizon));
                    else
                        ++notAppliedLocal;
                    prefix.RemoveAt(prefix.Count - 1);
                }
            }

            Visit([], decisions, baseTotal, basePotAt);
            for (var d = 1; d <= fork.Depth; ++d)
                csv.WriteLine(FormattableString.Invariant($"{scenario.Name.Replace(',', ';')},{scenario.Duration:f1},{t:f2},depth,{ForkActionName(decision.Chosen)},{d},{leaves[d]},{baseTotal:f0},{bestTotal[d].Value:f0},{bestTotal[d].Value - baseTotal:f0},{bestTotal[d].Seq},{basePotAt:f1},{bestPot[d].Value:f1},{bestPot[d].Value - basePotAt:f1},{bestPot[d].Seq},{scenario.Duration - t:f1},{sumTotal[d] / Math.Max(1, leaves[d]):f1},{betterTotal[d]},{sumPot[d] / Math.Max(1, leaves[d]):f1},{betterPot[d]}"));
        }
        notApplied += notAppliedLocal;
        return runs;
    }

    private static readonly int[] ForkHorizons = [15, 30, 60];

    // Per horizon H: the fork's and the baseline's cumulative potency, raid-buffed potency and state key at t+H, and whether the
    // fork had a target for the whole of [t, t+H]. Empty cells when the fight ends before t+H.
    private static string ForkHorizonColumns(float t, List<BlmSample> baseSeries, List<BlmSample> forkSeries)
    {
        var cells = new List<string>();
        foreach (var h in ForkHorizons)
        {
            var at = t + h;
            var f = forkSeries.FindIndex(sample => sample.T >= at - 0.001f);
            var b = baseSeries.FindIndex(sample => sample.T >= at - 0.001f);
            var start = forkSeries.FindIndex(sample => sample.T >= t - 0.001f);
            if (f < 0 || b < 0 || start < 0)
            {
                cells.Add(",,,,,,");
                continue;
            }
            var available = forkSeries.Skip(start).Take(f - start + 1).All(sample => sample.TargetAvailable);
            cells.Add(FormattableString.Invariant($"{forkSeries[f].Potency:f1},{forkSeries[f].Buffed:f1},{forkSeries[f].Key},{(available ? 1 : 0)},{baseSeries[b].Potency:f1},{baseSeries[b].Buffed:f1},{baseSeries[b].Key}"));
        }
        return string.Join(',', cells);
    }

    private static string ForkActionName(ActionID action) => action.Type == ActionType.Spell ? ((BossMod.BLM.AID)action.ID).ToString() : action.ToString();

    private static string FormatWindows(IReadOnlyList<EventTriggerTimelineWindow> windows)
        => windows.Count == 0 ? "none" : string.Join(',', windows.Select(window => $"{window.Start:f1}-{window.End:f1}"));

    private static string ResolveEventTriggerTimelineRoot(string? timelineRoot)
    {
        var candidates = new[]
        {
            timelineRoot,
            Environment.GetEnvironmentVariable("EVENT_TRIGGER_TIMELINE_ROOT"),
            @"F:\event-trigger-master\timelines\src\main\resources"
        };

        foreach (var candidate in candidates)
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return candidate;

        throw new DirectoryNotFoundException("Pass --timeline-root <Event Trigger timelines/resources> or set EVENT_TRIGGER_TIMELINE_ROOT.");
    }

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  dotnet run --project tools\\xan_timeline_harness -- event-timeline --timeline-root <resources> [--job all|drg,mch,mnk,blm,sam,vpr,rpr] [--zone <id>] [--duration <seconds>] [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\xan_timeline_harness -- dmu-full --timeline-root <resources> [--job all|drg,mch,mnk,blm,sam,vpr,rpr] [--duration <seconds>] [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\xan_timeline_harness -- timeline-matrix --timeline-root <resources> [--job all|drg,mch,mnk,blm,sam,vpr,rpr] [--scenario-limit <count>] [--post-roll <seconds>] [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\xan_timeline_harness -- timeline-combat-matrix --timeline-root <resources> [--job all|drg,mch,mnk,blm,sam,vpr,rpr] [--scenario-limit <count>] [--post-roll <seconds>] [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\xan_timeline_harness -- rpr-potion [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\xan_timeline_harness -- rpr-potion-boundaries [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\xan_timeline_harness -- timeline-hints [--zone <id>] [--post-roll <drift fraction>] [--scenario-filter verbose]");
        Console.Error.WriteLine("  dotnet run --project tools\\xan_timeline_harness -- splatoon-import");
        Console.Error.WriteLine("  dotnet run --project tools\\xan_timeline_harness -- list-tracks [--job all|drg,mch,mnk,blm,sam,vpr,rpr,gnb,gnbx,pld,pldx] [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\xan_timeline_harness -- irregular-compare [--job ...] [--irregular-seeds 1,2,3] [--irregular-rates 2,4] [--irregular-kind-sets lockout;los;range;loss;all] [--irregular-out <csv>] (combat matrix, baseline vs. irregular events per job)");
        Console.Error.WriteLine("                  [--irregular <seed>] adds unpredicted lockouts (stun/sleep/down for the count/pacification/silence/amnesia), line-of-sight blocks, knockback holds out of range and target loss, with no hint; implies client-reject accounting;");
        Console.Error.WriteLine("                  [--irregular-rate <per minute>] (default 2) and [--irregular-kinds lockout,los,range,loss|all] (default all) shape those events.");
        Console.Error.WriteLine("  common options: [--target-loss-hints <lead seconds>] announces upcoming target-unavailable windows to the module as external mechanic hints;");
        Console.Error.WriteLine("                  [--scenario-filter <text>] runs only matrix scenarios whose name contains the text; XAN_HARNESS_TRACE_DIR=<dir> writes per-run action traces and summary.csv;");
        Console.Error.WriteLine("                  [--party-buffs <first cast seconds>] adds a DRG and a MNK party member casting Battle Litany / Brotherhood at that time and every 120s after (20s windows, scorer +5% each);");
        Console.Error.WriteLine("                  [--post-roll <seconds>] extends every generated matrix scenario by that much plain uptime after its last target-return (event-timeline / dmu-full durations are unchanged).");
        Console.Error.WriteLine("                  [--random-disengage <seed>] adds random telegraphed chariots, baited puddles and far stacks off the timeline; the player dodges late, range and cast interruption are enforced (generic timelines only);");
        Console.Error.WriteLine("                  [--disengage-forecast on|off] whether the disengage forecast turns those mechanics into hints for the rotation (default on);");
        Console.Error.WriteLine("                  [--track <Track>=<Option>] pins a strategy track to one of its options, repeatable; list-tracks prints the internal names;");
        Console.Error.WriteLine("                  [--start-soulsow on|off] RPR scenarios start with Soulsow applied, so Harvest Moon is available as a ranged filler (default off);");
        Console.Error.WriteLine("                  XAN_HARNESS_DISENGAGE_DEBUG=<from>-<to> prints per-frame position, movement, forecast and queued candidates inside that time range;");
        Console.Error.WriteLine("                  XAN_HARNESS_HOLD_MOVE=1 tells the rotation the movement escape hatch key is held for the whole run (the character still stands still, so casts complete).");
        return 2;
    }

    private static void InitializeBossMod(string? sqpackOverride)
    {
        Service.LuminaGameData = new Lumina.GameData(ResolveSqpackPath(sqpackOverride));
        Service.Config.Initialize();
        InitializeHarnessServices();
    }

    private static void InitializeHarnessServices()
    {
        if (Service.SigScanner != null)
            return;

        var scannerProperty = typeof(Service).GetProperty(nameof(Service.SigScanner), BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("Could not resolve Service.SigScanner.");
        scannerProperty.SetValue(null, DispatchProxy.Create<ISigScanner, NullSigScanner>());
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
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return candidate;

        throw new DirectoryNotFoundException("Could not find FFXIV sqpack. Pass --sqpack or set BOSSMOD_SQPACK_PATH.");
    }

    private sealed record JobAdapter(string Name, Class Class, Func<RotationModuleDefinition> Definition, Func<RotationModuleManager, Actor, RotationModule> CreateModule);

    private sealed record DmuPhaseSchedule(float P1TargetLossStart, float P2Start, float P2End, float P3Start, float P4Start, float P5Start, float FullDuration)
    {
        public static DmuPhaseSchedule From(EventTriggerTimeline timeline)
        {
            var targetLoss = timeline.TargetUnavailableWindows(float.MaxValue).FirstOrDefault();
            // God Kefka is untargetable at 0% HP before Aero III Assault starts casting, and the module's P3 begins on that and then
            // waits for the cast: P2 ends when the cast starts, not when it resolves
            var aeroCastTime = Service.LuminaRow<Lumina.Excel.Sheets.Action>(DmuAeroIIIAssaultActionID) is { } aero ? (aero.Cast100ms + aero.ExtraCastTime100ms) * 0.1f : 0;
            var p2End = RequireSingleEvent(timeline, "Aero III Assault").Time - aeroCastTime;
            var p3Start = RequireFirstEventAfter(timeline, "--both targetable--", p2End).Time;
            var p4 = RequireSingleSection(timeline, "Phase 4 -");
            var p5 = RequireSingleSection(timeline, "Phase 5 -");
            var nextSection = timeline.Sections.FirstOrDefault(section => section.StartTime > p5.StartTime);
            if (targetLoss.End <= targetLoss.Start || nextSection.LineNumber == 0)
                throw new InvalidDataException($"Event Trigger timeline '{timeline.FileName}' does not expose the DMU phase boundaries required by dmu-full.");

            var p5Events = timeline.Events.Where(entry => entry.Time >= p5.StartTime && entry.Time < nextSection.StartTime).ToArray();
            if (p5Events.Length == 0 || !(targetLoss.Start < targetLoss.End && targetLoss.End < p2End && p2End < p3Start && p3Start < p4.StartTime && p4.StartTime < p5.StartTime))
                throw new InvalidDataException($"Event Trigger timeline '{timeline.FileName}' has invalid DMU phase ordering.");

            return new(targetLoss.Start, targetLoss.End, p2End, p3Start, p4.StartTime, p5.StartTime, p5Events[^1].Time + ResumeGCDLimit);
        }

        private static EventTriggerTimelineEvent RequireSingleEvent(EventTriggerTimeline timeline, string title)
        {
            var matches = timeline.Events.Where(entry => entry.Title.Equals(title, StringComparison.Ordinal)).ToArray();
            return matches.Length == 1
                ? matches[0]
                : throw new InvalidDataException($"Event Trigger timeline '{timeline.FileName}' must contain exactly one '{title}' event.");
        }

        private static EventTriggerTimelineEvent RequireFirstEventAfter(EventTriggerTimeline timeline, string title, float time)
        {
            var match = timeline.Events.FirstOrDefault(entry => entry.Time > time && entry.Title.Equals(title, StringComparison.Ordinal));
            return match.LineNumber != 0
                ? match
                : throw new InvalidDataException($"Event Trigger timeline '{timeline.FileName}' has no '{title}' event after {time:f1}s.");
        }

        private static EventTriggerTimelineSection RequireSingleSection(EventTriggerTimeline timeline, string prefix)
        {
            var matches = timeline.Sections.Where(section => section.Title.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            return matches.Length == 1
                ? matches[0]
                : throw new InvalidDataException($"Event Trigger timeline '{timeline.FileName}' must contain exactly one '{prefix}' section.");
        }
    }

    private class NullSigScanner : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            if (targetMethod?.ReturnType == typeof(IntPtr))
                return IntPtr.Zero;
            if (targetMethod?.ReturnType == typeof(bool))
                return false;
            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    private sealed class ContinuousTimelineRunner(JobAdapter job, int zoneID, float duration, IReadOnlyList<EventTriggerTimelineWindow> targetUnavailableWindows, DmuPhaseSchedule? dmuSchedule, IReadOnlyList<EventTriggerTimelineAction> dmuTimelineActions)
    {
        private readonly JobAdapter _job = job;
        private readonly int _zoneID = zoneID;
        private readonly float _duration = duration;
        private readonly IReadOnlyList<EventTriggerTimelineWindow> _targetUnavailableWindows = targetUnavailableWindows;
        private readonly DmuPhaseSchedule? _dmuSchedule = dmuSchedule;
        private readonly IReadOnlyList<EventTriggerTimelineAction> _dmuTimelineActions = dmuTimelineActions;
        private readonly List<string> _failures = [];
        private readonly List<ExecutedAction> _actions = [];
        private readonly HashSet<ulong> _enemyTargetIDs = [];
        private readonly List<Actor> _extraTargets = [];
        private readonly List<double> _targetReturnTimes = [];
        private int _frames;
        private int _targetFrames;
        private int _gcdIdleFrames;
        private int _ddFrames;
        private float _partyArcaneCircleValue;
        private WorldState? _terminalWorld;
        private Actor? _terminalPlayer;
        private float _ddGapStart = -1;
        private ulong _ddGapTarget;
        private readonly List<(float Start, float End, ulong Target)> _ddGaps = [];
        private RprCombatState? _rprCombat;
        private BlmCombatState? _blmCombat;
        private MnkCombatState? _mnkCombat;
        private GnbCombatState? _gnbCombat;
        private PldCombatState? _pldCombat;
        private NinCombatState? _ninCombat;
        private DrgCombatState? _drgCombat;
        private SamCombatState? _samCombat;
        private VprCombatState? _vprCombat;
        private MchCombatState? _mchCombat;
        private long _executeTicks;
        private BurstTracker? _burst;
        private string _burstKey = "";
        private ulong _ttkScenario;
        // XAN_HARNESS_TTK: publishes the (perfect / noisy / blind) fight-time estimate; a no-op when the variable is unset
        private void ApplyFightRemaining(AIHints hints, float time)
        {
            if (FightRemainingOverride.Enabled)
                hints.FightRemaining = FightRemainingOverride.At(_ttkScenario, _duration, time);
        }

        // Optional target-loss foresight for generic timelines. DMU runs keep their own boss module state machine.
        public static float? TargetLossHintLead;
        // Live play only trusts the imported timeline after it has seen one downtime happen where the timeline said it would,
        // so this mode withholds the lead-in hint for the first window of a scenario and keeps the in-window return hint.
        private static readonly bool SkipFirstWindowLead = Environment.GetEnvironmentVariable("XAN_HARNESS_HINTS_AFTER_FIRST_LOSS") == "1";
        // level the simulated player is synced to; MNK.cs is full of Unlocked() branches that Lv100 never exercises
        public static int PlayerLevel = 100;
        // Optional SkillSpeed stat for the simulated player. Null keeps the harness default, whose GCD is the 2.50s
        // baseline; a geared Lv100 melee sits well below that, and buff windows are measured in GCDs, not seconds.
        public static int? PlayerSkillSpeed;
        // Optional number of strength potions put into the simulated player's inventory. Null leaves the inventory empty,
        // which is what every existing baseline was taken with; the Potion track can only fire when an item is actually held.
        public static int? Potions;
        // number of extra enemies stacked next to the primary target, so the AoE side of the rotation gets exercised
        public static int ExtraTargets;
        // Optional BLM "Rotation mode" override so planner-based modes can be measured; the track default is the fixed rotation.
        public static string? BlmRotationOverride;
        // MNK EncounterHint track override: the generic timeline has no boss module, so three or more enemies resolve
        // to Trash and every burst is withheld - forcing Boss is what lets the AoE rotation be measured at all
        public static string? MnkEncounterHintOverride;
        // Generic strategy track overrides (--track), so any UI-selectable setting can be measured instead of only the
        // two tracks that happened to get a one-off flag. Applied after the job-specific overrides above.
        public static IReadOnlyList<(string Track, string Option)> TrackOverrides = [];
        // blm-fork-scan hooks, handed to BlmCombatState for the next run: record decisions, or force one deviation.
        public static List<BlmCombatState.DecisionPoint>? BlmDecisionLog;
        public static (DateTime At, ActionID Action)? BlmForce;
        public static string LastBlmForceOutcome = "";
        // XAN_HARNESS_ACTION_COUNTS=1: blm-fork-scan --fork-every 0 sums the executed actions of every fight and prints them
        public static readonly Dictionary<ActionID, long>? ActionCounts = Environment.GetEnvironmentVariable("XAN_HARNESS_ACTION_COUNTS") == "1" ? [] : null;
        public static ActionID[]? BlmForceFollowups;
        // oracle-search: FNV-1a over the executed actions of the last run (time in ms, action, GCD flag, target); two runs with the same hash did the same thing
        public static ulong LastActionHash;
        private static ulong HashActions(List<ExecutedAction> actions)
        {
            var hash = 14695981039346656037UL;
            foreach (var a in actions)
            {
                hash = (hash ^ (ulong)(long)MathF.Round(a.Time * 1000f)) * 1099511628211UL;
                hash = (hash ^ (ulong)a.Action.Raw) * 1099511628211UL;
                hash = (hash ^ (a.GCD ? 1UL : 0UL)) * 1099511628211UL;
                hash = (hash ^ a.TargetID) * 1099511628211UL;
            }
            return hash;
        }
        // oracle-search: controllable window around the single irregular episode (seconds before its start / after its end)
        public static float OracleMarginBefore = 3f;
        public static float OracleWindow = 25f;
        public static bool LastBlmFollowupsComplete;
        // blm-fork-scan value series: every 0.5 s the cumulative potency (raid-buffed potency kept apart) and the gauge and
        // cooldown state, so a deviation can be scored as damage over [t, t+H] plus the value of the state it leaves at t+H.
        public static List<BlmSample>? BlmSeries;
        private int _blmSeriesIndex;
        private float _blmSeriesPotency, _blmSeriesBuffed;
        // Optional random telegraphed mechanics off the timeline (generic timelines only), and whether the disengage forecast sees them.
        public static int? RandomDisengageSeed;
        // Start RPR scenarios with Soulsow up, the way a player who applied it before the pull would enter a mid-fight window.
        public static bool StartSoulsow;
        public static bool DisengageForecastEnabled = true;
        public static int DisengageEvents;
        public static int DisengageMovingFrames;
        public static int DisengageInterruptedCasts;
        // --irregular: per-job totals of the unpredicted episodes (see IrregularDriver), printed and reset after each job
        public static IrregularDriver.Stats IrregularTotals = IrregularDriver.Stats.Empty;
        private IrregularDriver? _irregular;
        private IrregularDriver.Stats _irregularStats = IrregularDriver.Stats.Empty;
        private static readonly (float From, float To)? DisengageDebugRange = ParseDebugRange(Environment.GetEnvironmentVariable("XAN_HARNESS_DISENGAGE_DEBUG"));

        // XAN_HARNESS_HOLD_MOVE=1: pretend the movement escape hatch key is held for the whole run, which is what Plugin.cs
        // now feeds into isMoving. Every rotation module sees continuous movement, so this is how the held-key state gets
        // measured offline - the real key is read in DrawUI, which the harness never calls.
        private static readonly bool HoldMove = Environment.GetEnvironmentVariable("XAN_HARNESS_HOLD_MOVE") == "1";
        // XAN_HARNESS_SM_WINDOWS=1: deliver the target-unavailable windows through a boss state machine (see WindowStateMachineModule)
        private static readonly bool StateMachineWindows = Environment.GetEnvironmentVariable("XAN_HARNESS_SM_WINDOWS") == "1";
        private static readonly bool LiveFollowerHints = Environment.GetEnvironmentVariable("XAN_HARNESS_HINT_LIVE") == "1";

        private static (float, float)? ParseDebugRange(string? value)
        {
            var parts = value?.Split('-');
            return parts is { Length: 2 } && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var from)
                && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var to) ? (from, to) : null;
        }
        // Providers only accept namespaces under the external-hint prefix.
        private const string TargetLossHintSource = "bossmod.external.splatoon.harness-timeline";

        private void PushTargetLossHint(WorldState world, float time)
        {
            if (TargetLossHintLead is not { } lead || _dmuSchedule != null)
                return;

            var lossIn = float.MaxValue;
            var returnIn = float.MaxValue;
            var firstPending = true;
            foreach (var window in _targetUnavailableWindows)
            {
                if (window.End <= time)
                {
                    firstPending = false;
                    continue;
                }
                // the follower never publishes a window shorter than ExternalTimelineHints.MinPublishedLoss
                if (LiveFollowerHints && window.End - window.Start < ExternalTimelineHints.MinPublishedLoss)
                    continue;
                if (window.Contains(time))
                {
                    lossIn = 0;
                    returnIn = window.End - time;
                }
                else if (window.Start - time <= lead && !(SkipFirstWindowLead && firstPending))
                {
                    lossIn = window.Start - time;
                    returnIn = window.End - time;
                }
                break;
            }

            // XAN_HARNESS_HINT_LIVE=1: publish the way the imported-timeline follower does in game (ExternalTimelineHints.Publish),
            // only while a loss is within the lead, and nothing otherwise
            if (LiveFollowerHints && lossIn == float.MaxValue)
            {
                ExternalMechanicHintProvider.ClearNamespace(TargetLossHintSource);
                return;
            }
            // nothing within the lead is still a statement from the provider: keep an empty snapshot published so consumers can tell
            // "no loss coming" from "no provider"
            ExternalMechanicHintProvider.PushSnapshot(
                new(TargetLossHintSource, world.CurrentZone, world.CurrentCFCID, ExternalZoneConfidence.TrustedSplatoonScript, world.FutureTime(1), float.MaxValue, lossIn, returnIn, float.MaxValue, false),
                world.CurrentZone, world.CurrentCFCID, world.CurrentTime);
        }

        // Optional DRG + MNK party members casting Battle Litany / Brotherhood so RaidBuffsLeft / RaidBuffsIn see real windows.
        public static float? PartyBuffFirstCast;
        private const ulong PartyDrgID = 0x10000002;
        private const ulong PartyMnkID = 0x10000003;
        private const float PartyBuffDuration = 20f;
        private const float PartyBuffCooldown = 120f;
        private const float PartyBuffMinimumUptime = 15f; // real parties hold 2-minute buffs until the target will stay up this long
        private Actor? _partyDrg;
        private Actor? _partyMnk;
        private float _partyBuffDue;
        // XAN_HARNESS_PARTY_JITTER=<seconds>: each party cast after the first lands 0..seconds later than its cooldown allows (deterministic
        // per scenario and cast index), so the module's "last cast + 120 s" prediction is off by that much. Default 0 = unchanged.
        private static readonly float PartyJitter = float.TryParse(Environment.GetEnvironmentVariable("XAN_HARNESS_PARTY_JITTER"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var jitter) ? jitter : 0f;
        private float _partyBuffDelay;
        private static readonly float PartySkip = float.TryParse(Environment.GetEnvironmentVariable("XAN_HARNESS_PARTY_SKIP"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var skip) ? skip : 0f;

        private float PartyBuffSkipRoll()
        {
            var key = FormattableString.Invariant($"skip:{_zoneID}:{_duration:f1}:{string.Join(';', _targetUnavailableWindows.Select(window => FormattableString.Invariant($"{window.Start:f1}-{window.End:f1}")))}:{_partyBuffCasts}");
            uint h = 2166136261;
            foreach (var c in key)
                h = (h ^ c) * 16777619;
            h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12;
            return (h % 10001) / 10000f;
        }
        private int _partyBuffCasts;

        private float NextPartyBuffDelay()
        {
            ++_partyBuffCasts;
            if (PartyJitter <= 0)
                return 0;
            var key = FormattableString.Invariant($"{_zoneID}:{_duration:f1}:{string.Join(';', _targetUnavailableWindows.Select(window => FormattableString.Invariant($"{window.Start:f1}-{window.End:f1}")))}:{_partyBuffCasts}");
            uint h = 2166136261;
            foreach (var c in key)
                h = (h ^ c) * 16777619;
            h ^= h >> 15; h *= 0x2c1b3c6d; h ^= h >> 12;
            return (h % 10001) / 10000f * PartyJitter;
        }
        private readonly List<(float Start, float End)> _partyWindows = []; // burst study: when the simulated party raid buffs were up
        private int _raidBuffFrames;
        private uint _partyBuffSequence;
        // rDPS proxy: potency the other seven party members gain from our Arcane Circle (3%), ~207 potency/s each, x1.5 inside their burst.
        private const float PartyPotencyPerSecond = 1450f;
        private const float PartyBurstMultiplier = 1.5f;
        private const float ArcaneCirclePartyBonus = 0.03f;

        private void AccumulatePartyArcaneCircleValue(Actor player, bool targetAvailable)
        {
            if (targetAvailable && (player.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || player.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null))
                ++_raidBuffFrames;
            if (!targetAvailable || player.FindStatus((uint)RPRSID.ArcaneCircle, player.InstanceID) == null)
                return;

            var partyBurst = player.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || player.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
            _partyArcaneCircleValue += ArcaneCirclePartyBonus * PartyPotencyPerSecond * FrameStep * (partyBurst ? PartyBurstMultiplier : 1f);
        }

        // Generic runs: hold the party buffs if a target-unavailable window starts within PartyBuffMinimumUptime (or is active now).
        // DMU runs keep the "target available now" rule since the phase schedule is not expressed as windows.
        private bool PartyBuffTargetStaysUp(float time)
        {
            if (_dmuSchedule != null)
                return true;

            var nextStart = float.MaxValue;
            foreach (var window in _targetUnavailableWindows)
            {
                if (window.Contains(time))
                    return false;
                if (window.Start > time)
                    nextStart = Math.Min(nextStart, window.Start);
            }
            return nextStart - time >= PartyBuffMinimumUptime;
        }

        private void DrivePartyBuffs(WorldState world, Actor player, float time, bool targetAvailable)
        {
            if (_partyDrg == null || _partyMnk == null)
                return;

            RemoveExpiredStatus(world, player, (uint)BossMod.DRG.SID.BattleLitany);
            RemoveExpiredStatus(world, player, (uint)BossMod.MNK.SID.Brotherhood);
            if (time < _partyBuffDue + _partyBuffDelay || !targetAvailable || !PartyBuffTargetStaysUp(time))
                return;

            if (PartySkip > 0 && _partyBuffCasts > 0 && PartyBuffSkipRoll() < PartySkip)
            {
                // XAN_HARNESS_PARTY_SKIP=<p>: this window is not cast at all (the module's "last cast + 120 s" prediction is stale)
                _partyBuffDue = time + PartyBuffCooldown;
                _partyBuffDelay = NextPartyBuffDelay();
                return;
            }

            EmitPartyCast(world, _partyDrg, ActionID.MakeSpell(BossMod.DRG.AID.BattleLitany));
            EmitPartyCast(world, _partyMnk, ActionID.MakeSpell(BossMod.MNK.AID.Brotherhood));
            ApplyStatus(world, player, (uint)BossMod.DRG.SID.BattleLitany, PartyBuffDuration, _partyDrg.InstanceID);
            ApplyStatus(world, player, (uint)BossMod.MNK.SID.Brotherhood, PartyBuffDuration, _partyMnk.InstanceID);
            _partyBuffDue = time + PartyBuffCooldown;
            _partyBuffDelay = NextPartyBuffDelay();
            _partyWindows.Add((time, time + PartyBuffDuration));
        }

        private void EmitPartyCast(WorldState world, Actor caster, ActionID action)
        {
            ++_partyBuffSequence;
            world.Execute(new ActorState.OpCastEvent(caster.InstanceID, new ActorCastEvent(action, caster.InstanceID, 0.6f, 1, caster.PosRot.XYZ(), _partyBuffSequence, _partyBuffSequence, caster.Rotation)));
        }

        private static void ApplyStatus(WorldState world, Actor actor, uint status, float duration, ulong sourceID)
        {
            var slot = Array.FindIndex(actor.Statuses, s => s.ID == status && s.SourceID == sourceID);
            if (slot < 0)
                slot = Array.FindIndex(actor.Statuses, s => s.ID == 0);
            if (slot < 0)
                throw new InvalidOperationException("Party buff driver ran out of status slots.");
            world.Execute(new ActorState.OpStatus(actor.InstanceID, slot, new(status, 0, world.CurrentTime.AddSeconds(duration), sourceID)));
        }

        private static void RemoveExpiredStatus(WorldState world, Actor actor, uint status)
        {
            for (var i = 0; i < actor.Statuses.Length; ++i)
                if (actor.Statuses[i].ID == status && actor.Statuses[i].ExpireAt <= world.CurrentTime)
                    world.Execute(new ActorState.OpStatus(actor.InstanceID, i, default));
        }

        private void TrackDDGap(float time, bool gapNow, ulong targetID)
        {
            if (gapNow && (_ddGapStart < 0 || _ddGapTarget != targetID))
            {
                if (_ddGapStart >= 0)
                    _ddGaps.Add((_ddGapStart, time, _ddGapTarget));
                _ddGapStart = time;
                _ddGapTarget = targetID;
            }
            else if (!gapNow && _ddGapStart >= 0)
            {
                _ddGaps.Add((_ddGapStart, time, _ddGapTarget));
                _ddGapStart = -1;
            }
        }

        private RotationMetrics BuildMetrics()
        {
            int Count(RPRAID aid) => _actions.Count(action => action.Action == ActionID.MakeSpell(aid));
            // agents_gnb.md calls "Gnashing Fang 3 GCDs before No Mercy" the single most important anchor of the
            // rotation. Counted in GCDs, not seconds: a downtime in the middle of the chain stretches the wall clock
            // without changing the GCD order. The opening No Mercy is excluded - no Gnashing Fang can precede it.
            var gnbGCDs = _gnbCombat == null ? [] : _actions.Where(a => a.GCD).ToList();
            var gnbNoMercies = _gnbCombat == null ? [] : _actions.Where(a => a.Action == ActionID.MakeSpell(GNBAID.NoMercy)).Skip(1).ToList();
            var gnbAnchorable = gnbNoMercies.Count;
            var gnbAnchors = gnbNoMercies.Count(nm => gnbGCDs.Where(g => g.Time <= nm.Time).TakeLast(3).Any(g => g.Action == ActionID.MakeSpell(GNBAID.GnashingFang)));
            var terminal = _rprCombat != null && _terminalWorld != null && _terminalPlayer != null ? RprTerminalValue.Estimate(_terminalWorld, _terminalPlayer, _rprCombat)
                : _gnbCombat != null && _terminalWorld != null && _terminalPlayer != null ? GnbTerminalValue.Estimate(_terminalWorld, _terminalPlayer, _gnbCombat)
                : _blmCombat != null ? BlmTerminalValue.Estimate(_blmCombat)
                : _ninCombat != null && _terminalWorld != null && _terminalPlayer != null ? NinTerminalValue.Estimate(_terminalWorld, _terminalPlayer, _ninCombat)
                : _drgCombat != null && _terminalWorld != null && _terminalPlayer != null ? DrgTerminalValue.Estimate(_terminalWorld, _terminalPlayer, _drgCombat)
                : _samCombat != null && _terminalWorld != null && _terminalPlayer != null ? SamTerminalValue.Estimate(_terminalWorld, _terminalPlayer, _samCombat)
                : _vprCombat != null && _terminalWorld != null && _terminalPlayer != null ? VprTerminalValue.Estimate(_terminalWorld, _terminalPlayer, _vprCombat)
                : _mchCombat != null && _terminalWorld != null && _terminalPlayer != null ? MchTerminalValue.Estimate(_terminalWorld, _terminalPlayer, _mchCombat)
                : _pldCombat != null && _terminalWorld != null && _terminalPlayer != null ? PldTerminalValue.Estimate(_terminalWorld, _terminalPlayer, _pldCombat) : 0f;
            return new(_actions.Sum(action => action.Potency) + (_blmCombat?.DotPotency ?? 0f) + (_samCombat?.DotPotency ?? 0f) + (_mchCombat?.DotPotency ?? 0f), terminal, _ddFrames, _targetFrames, _rprCombat?.SoulOvercap ?? 0, _rprCombat?.ShroudOvercap ?? 0,
                Count(RPRAID.Enshroud), Count(RPRAID.Communio), Count(RPRAID.Perfectio), Count(RPRAID.Gluttony), Count(RPRAID.PlentifulHarvest),
                _blmCombat?.PolyglotOvercap ?? 0, _executeTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, _partyArcaneCircleValue,
                _mnkCombat?.ChakraOvercap ?? 0, _mnkCombat?.BlitzesUsed ?? 0, _mnkCombat?.PhantomRushes ?? 0, _mnkCombat?.PerfectBalancesUsed ?? 0, _mnkCombat?.DroppedBeastChakra ?? 0,
                _mnkCombat?.RiddleOfFireGCDs ?? 0, _mnkCombat?.BrotherhoodGCDs ?? 0, _mnkCombat?.RiddleOfFirePotency ?? 0f, _mnkCombat?.OpoGCDs ?? 0, _mnkCombat?.RiddleOfFireOpoGCDs ?? 0, _mnkCombat?.ExpiredBlitzes ?? 0, _mnkCombat?.WastedPerfectBalances ?? 0, _mnkCombat?.FuryOpoGCDs ?? 0, _mnkCombat?.RiddleOfFireFuryOpoGCDs ?? 0, _mnkCombat?.PerfectBalanceGCDs ?? 0, _mnkCombat?.PerfectBalanceFuryOpoGCDs ?? 0, _mnkCombat?.RepliesUsed ?? 0, _mnkCombat?.RepliesExpired ?? 0, _gcdIdleFrames, _actions.Count(action => action.GCD),
                _actions.Where(action => action.RaidBuffed).Sum(action => action.Potency), _raidBuffFrames,
                _actions.Count(action => action.RaidBuffed && action.Action == ActionID.MakeSpell(RPRAID.Perfectio)),
                _actions.Count(action => action.RaidBuffed && action.Action == ActionID.MakeSpell(RPRAID.Communio)),
                _actions.Count(action => action.RaidBuffed && action.Action == ActionID.MakeSpell(RPRAID.PlentifulHarvest)),
                _gnbCombat?.CartridgeOvercap ?? 0, _gnbCombat?.NoMercyUses ?? 0, _gnbCombat?.NoMercyGCDs ?? 0, _gnbCombat?.GnashingFangChains ?? 0, _gnbCombat?.ReignChains ?? 0,
                _gnbCombat?.DoubleDowns ?? 0, _gnbCombat?.SonicBreaks ?? 0, (_gnbCombat?.ContinuationsExpired ?? 0) + (_gnbCombat?.ContinuationsOverwritten ?? 0), _gnbCombat?.BurstGCDsOutsideNoMercy ?? 0, gnbAnchors, gnbAnchorable, _gnbCombat?.BurstOpenersOutsideNoMercy ?? 0,
                _ninCombat?.PartyDokumoriValue ?? 0f, _ninCombat?.KunaiPotency ?? 0f, _ninCombat?.KunaiGCDs ?? 0, _ninCombat?.RaijuLost ?? 0, _ninCombat?.Rabbits ?? 0, _ninCombat?.NinjutsuRejectedFrames ?? 0, _ninCombat?.InvalidTenChiJinFrames ?? 0,
                _ninCombat?.TenChiJinIncomplete ?? 0, _ninCombat?.MudraTimeouts ?? 0, _ninCombat?.MudraCapFrames ?? 0, _ninCombat?.NinkiOvercap ?? 0, _ninCombat?.RaitonFirstBursts ?? 0, _ninCombat?.KassatsuFirstBursts ?? 0,
                _ninCombat != null && _ninCombat.OpenerKunaiGCD >= 0 && _ninCombat.OpenerDokumoriGCD >= 0 ? _ninCombat.OpenerDokumoriGCD : 0, _ninCombat != null && _ninCombat.OpenerKunaiGCD >= 0 && _ninCombat.OpenerDokumoriGCD >= 0 ? _ninCombat.OpenerKunaiGCD : 0, _ninCombat != null && _ninCombat.OpenerKunaiGCD >= 0 && _ninCombat.OpenerDokumoriGCD >= 0 ? 1 : 0,
                _drgCombat?.PartyLitanyValue ?? 0f, _drgCombat?.LanceChargeGCDs ?? 0, _drgCombat?.LifeOfTheDragonGCDs ?? 0, _drgCombat?.LifeSurgesLost ?? 0, _drgCombat?.LifeSurgesWeak ?? 0, _drgCombat?.ProcsLost ?? 0,
                _drgCombat?.FocusOvercap ?? 0, _drgCombat?.NoPowerSurgeGCDs ?? 0, _drgCombat?.DotGapFrames ?? 0, _drgCombat?.Geirskoguls ?? 0, _drgCombat?.Stardivers ?? 0,
                _samCombat?.KenkiOvercap ?? 0, _samCombat?.MeditationOvercap ?? 0, _samCombat?.SenOvercap ?? 0, _samCombat?.ProcsLost ?? 0, _samCombat?.NoFugetsuGCDs ?? 0,
                _samCombat?.DotGapFrames ?? 0, _samCombat?.Iaijutsu ?? 0, _samCombat?.Tsubame ?? 0, _samCombat?.Namikiri ?? 0, _samCombat?.DotPotency ?? 0f,
                _vprCombat?.OfferingOvercap ?? 0, _vprCombat?.CoilOvercap ?? 0, _vprCombat?.ProcsLost ?? 0, _vprCombat?.FollowUpsLost ?? 0, _vprCombat?.NoInstinctGCDs ?? 0,
                _vprCombat?.Reawakens ?? 0, _vprCombat?.Generations ?? 0, _vprCombat?.UncoiledFuries ?? 0, _vprCombat?.Coils ?? 0, _vprCombat?.PositionalsMissed ?? 0,
                _mchCombat?.HeatOvercap ?? 0, _mchCombat?.BatteryOvercap ?? 0, _mchCombat?.ProcsLost ?? 0, _mchCombat?.Hypercharges ?? 0, _mchCombat?.OverheatedShots ?? 0,
                _mchCombat?.OverheatedStacksLost ?? 0, _mchCombat?.Wildfires ?? 0, _mchCombat?.WildfireHits ?? 0, _mchCombat?.Queens ?? 0, _mchCombat?.QueenBattery ?? 0,
                _mchCombat?.PetHitsLost ?? 0, _mchCombat?.ReassembledTools ?? 0, _mchCombat?.ReassembleWasted ?? 0, _mchCombat?.DotPotency ?? 0f,
                _pldCombat?.Metrics ?? default);
        }

        // Ninja always starts a pull with the countdown Suiton (it is what makes the opening Kunai's Bane possible), so NIN runs get a
        // pre-pull countdown by default; --countdown changes its length. Other jobs keep starting in combat at time zero.
        public const float DefaultNinPrePull = 10f;
        // Samurai's opener presses Meikyo Shisui in the last 14s of the countdown (xan SAM.cs meikyoCutoff)
        public const float DefaultSamPrePull = 15f;
        // Viper presses its first weaponskill with 1.16s left on the countdown (xan VPR.cs)
        public const float DefaultVprPrePull = 5f;
        // Machinist presses Reassemble in the last 5s of the countdown and its first tool with 1.15s left (xan MCH.cs)
        public const float DefaultMchPrePull = 6f;
        public static float? PrePullCountdown;

        public TimelineResult Run()
        {
            var prePull = _dmuSchedule != null ? 0f : _job.Class == Class.NIN ? PrePullCountdown ?? DefaultNinPrePull : _job.Class == Class.SAM ? PrePullCountdown ?? DefaultSamPrePull
                : _job.Class == Class.VPR ? PrePullCountdown ?? DefaultVprPrePull
                : _job.Class == Class.MCH ? PrePullCountdown ?? DefaultMchPrePull : 0f;
            var world = BuildWorld(out var player, out var target, out var dmuActors, out var hints, prePull);
            _ttkScenario = FightRemainingOverride.ScenarioHash(FormattableString.Invariant($"{_zoneID}:{_duration:f1}:{string.Join(';', _targetUnavailableWindows.Select(window => FormattableString.Invariant($"{window.Start:f1}-{window.End:f1}")))}"));
            using var bossmods = new BossModuleManager(world);
            var database = new RotationDatabase(new DirectoryInfo("tools/xan_timeline_harness/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
            using var manager = new RotationModuleManager(database, bossmods, hints)
            {
                CombatStart = BaseTime
            };
            var dmu = CreateDmuModule(world, bossmods, target, dmuActors, manager);
            using var dmuDisposable = dmu;
            using var windowModule = StateMachineWindows && _dmuSchedule == null ? WindowStateMachineModule.Create(world, bossmods, target, manager, _targetUnavailableWindows, BaseTime) : null;
            var dmuActionDriver = dmu != null && dmuActors != null && _dmuSchedule != null ? new DmuTimelineActionDriver(_dmuTimelineActions, _dmuSchedule) : null;
            var dmuHints = dmu != null ? new AIHints() : null;
            var dmuPredictedDamageFrames = 0;
            if (_job.Class == Class.BLM && !_blmBoundariesChecked)
            {
                ValidateBlmBoundaries(world, player, target, manager, hints);
                ValidateBlmPlannerSelection(world, player, target, manager, hints);
                _blmBoundariesChecked = true;
            }
            using var module = _job.CreateModule(manager, player);
            var strategy = new StrategyValues(_job.Definition().Configs);
            if (_job.Class == Class.BLM && BlmRotationOverride != null)
            {
                var trackIndex = strategy.Configs.FindIndex(config => config is StrategyConfigTrack track && track.Options.Any(option => option.InternalName == "FuturePlanner"));
                var optionIndex = trackIndex >= 0 ? ((StrategyConfigTrack)strategy.Configs[trackIndex]).Options.FindIndex(option => option.InternalName == BlmRotationOverride) : -1;
                if (optionIndex < 0)
                    throw new ArgumentException($"Unknown BLM rotation option '{BlmRotationOverride}'.");
                ((StrategyValueTrack)strategy.Values[trackIndex]).Option = optionIndex;
            }
            if (_job.Class == Class.MNK && MnkEncounterHintOverride != null)
            {
                var hintTrack = strategy.Configs.FindIndex(config => config is StrategyConfigTrack track && track.Options.Any(option => option.InternalName == "ForceBurst"));
                var hintOption = hintTrack >= 0 ? ((StrategyConfigTrack)strategy.Configs[hintTrack]).Options.FindIndex(option => option.InternalName == MnkEncounterHintOverride) : -1;
                if (hintOption < 0)
                    throw new ArgumentException($"Unknown MNK encounter hint option '{MnkEncounterHintOverride}'.");
                ((StrategyValueTrack)strategy.Values[hintTrack]).Option = hintOption;
            }
            ApplyTrackOverrides(_job, strategy, TrackOverrides);
            var wasTargetAvailable = true;
            if (StartSoulsow && _job.Class == Class.RPR)
                world.Execute(new ActorState.OpStatus(player.InstanceID, 20, new((uint)RPRSID.Soulsow, 0, BaseTime.AddSeconds(100000), player.InstanceID)));
            DisengageDriver? disengage = null;
            if (RandomDisengageSeed is { } disengageSeed && _dmuSchedule == null)
            {
                var scenarioKey = FormattableString.Invariant($"{_zoneID}:{_duration:f1}:{string.Join(';', _targetUnavailableWindows.Select(window => FormattableString.Invariant($"{window.Start:f1}-{window.End:f1}")))}");
                disengage = new(disengageSeed, scenarioKey, _duration, _targetUnavailableWindows, _job.Class.GetRole() is Role.Melee or Role.Tank, player.Position);
                DisengageEvents += disengage.EventCount;
                SetEnforceRangeAndMovement();
                if (_job.Class == Class.BLM)
                {
                    // the MechanicHints track defaults to All; pinned here so a --track override cannot switch disengage handling off in this mode
                    var trackIndex = strategy.Configs.FindIndex(config => config.InternalName == "MechanicHints");
                    var optionIndex = trackIndex >= 0 ? ((StrategyConfigTrack)strategy.Configs[trackIndex]).Options.FindIndex(option => option.InternalName == "All") : -1;
                    if (optionIndex < 0)
                        throw new InvalidOperationException("BLM MechanicHints/All option not found.");
                    ((StrategyValueTrack)strategy.Values[trackIndex]).Option = optionIndex;
                }
            }
            // --irregular (generic timelines only, like the disengage driver): unpredicted lockouts, LoS blocks, holds out of range and
            // target loss. The baseline half of irregular-compare takes the same range/movement enforcement with no episodes.
            if (_dmuSchedule == null && (Irregular.Enabled || Irregular.EnforceBaseline))
            {
                SetEnforceRangeAndMovement();
                if (Irregular.Seed is { } irregularSeed)
                {
                    var scenarioKey = FormattableString.Invariant($"{_zoneID}:{_duration:f1}:{string.Join(';', _targetUnavailableWindows.Select(window => FormattableString.Invariant($"{window.Start:f1}-{window.End:f1}")))}");
                    List<Actor> enemies = [target, .. _extraTargets];
                    _irregular = new(irregularSeed, scenarioKey, _duration, _targetUnavailableWindows, Irregular.RatePerMinute, Irregular.Kinds, player, target, enemies,
                        _job.Class.GetRole() is not (Role.Melee or Role.Tank), _job.Class == Class.BLM ? ActionCategory.Spell : ActionCategory.Weaponskill, () =>
                        {
                            _rprCombat?.InterruptCast();
                            _blmCombat?.InterruptCast();
                            _samCombat?.InterruptCast();
                            _pldCombat?.InterruptCast();
                        });
                    Irregular.Current = _irregular;
                }
            }

            if (SearchControl.Active)
                SearchControl.BeginRun(_irregular, OracleMarginBefore, OracleWindow); // oracle-search: controllable window around the episode
            var wasCasting = false;
            try
            {
                if (prePull > 0)
                    RunPrePull(world, player, target, hints, module, strategy, prePull);
                if (BurstControl.Enabled)
                {
                    _burstKey = FormattableString.Invariant($"z{_zoneID}_d{_duration:f1}_w{FightRemainingOverride.ScenarioHash(string.Join(';', _targetUnavailableWindows.Select(window => FormattableString.Invariant($"{window.Start:f1}-{window.End:f1}")))) % 100000000UL}");
                    _burst = new BurstTracker(_job.Class, _burstKey, world, player, FrameStep, BurstStateText, BurstFeasible);
                    _burst.SeedPrepull(_actions.Where(action => action.Time < 0).Select(action => (action.Time, action.Action)));
                    BurstControl.Current = _burst;
                }

                for (var frame = 0; ; ++frame)
                {
                    var time = frame * FrameStep;
                    if (time > _duration + FrameStep * 0.5f)
                        break;

                    _frames = frame + 1;
                    AdvanceFrame(world, time, frame);
                    _irregular?.Update(world, time);
                    var activeTargets = dmuActors != null && _dmuSchedule != null
                        ? UpdateDmuTimeline(world, dmuActors, _dmuSchedule, time)
                        : UpdateGenericTimeline(world, target, time, _irregular?.TargetLost ?? false);
                    var targetAvailable = activeTargets.Count > 0;
                    var activeTarget = targetAvailable ? activeTargets[0] : null;
                    if (targetAvailable && !wasTargetAvailable)
                        _targetReturnTimes.Add(time);
                    wasTargetAvailable = targetAvailable;
                    PushTargetLossHint(world, time);
                    DrivePartyBuffs(world, player, time, targetAvailable);
                    if (_job.Class == Class.RPR)
                        AccumulatePartyArcaneCircleValue(player, targetAvailable);
                    // every job needs the targetable-frame denominator for gcd_uptime; RPR still sees the same count
                    if (activeTarget != null)
                        ++_targetFrames;
                    if (_job.Class == Class.RPR && activeTarget != null)
                    {
                        var ddActive = activeTarget.FindStatus((uint)RPRSID.DeathsDesign, player.InstanceID) != null;
                        if (ddActive)
                            ++_ddFrames;
                        TrackDDGap(time, !ddActive, activeTarget.InstanceID);
                    }
                    else
                        TrackDDGap(time, false, 0);

                    var moving = disengage != null && activeTarget != null && disengage.Update(world, player, target, time);
                    if (_irregular is { Moving: true })
                        moving = true; // knockback flight and the walk back
                    // The held key is not physical movement: the character stands still, so casts still complete. Only the
                    // rotation is told to behave as if moving, which is exactly what Plugin.cs does with the escape hatch.
                    var moduleMoving = moving || HoldMove;
                    if (_rprCombat != null)
                        _rprCombat.Moving = moving;
                    if (_blmCombat != null)
                        _blmCombat.Moving = moving;
                    if (_mnkCombat != null)
                        _mnkCombat.Moving = moving;
                    if (_gnbCombat != null)
                        _gnbCombat.Moving = moving;
                    if (_ninCombat != null)
                        _ninCombat.Moving = moving;
                    if (_drgCombat != null)
                        _drgCombat.Moving = moving;
                    if (_samCombat != null)
                        _samCombat.Moving = moving;
                    if (_vprCombat != null)
                        _vprCombat.Moving = moving;
                    if (_mchCombat != null)
                        _mchCombat.Moving = moving;
                    if (_pldCombat != null)
                        _pldCombat.Moving = moving;
                    _rprCombat?.Advance();
                    _blmCombat?.Advance();
                    _mnkCombat?.Advance();
                    _gnbCombat?.Advance();
                    _ninCombat?.Advance();
                    _drgCombat?.Advance();
                    _samCombat?.Advance();
                    _vprCombat?.Advance();
                    _mchCombat?.Advance();
                    _pldCombat?.Advance();
                    if (dmuActionDriver != null)
                        dmuActionDriver.EmitThrough(world, player, dmuActors!, time);
                    dmu?.Update();
                    windowModule?.StateMachine.Update(world.CurrentTime);
                    RefreshHints(hints, activeTargets);
                    ApplyFightRemaining(hints, time);
                    if (dmuHints != null)
                    {
                        dmuHints.Clear();
                        dmu!.CalculateAIHints(PartyState.PlayerSlot, player, PartyRolesConfig.Assignment.Unassigned, dmuHints);
                        if (dmuHints.PredictedDamage.Count > 0)
                        {
                            hints.PredictedDamage.AddRange(dmuHints.PredictedDamage);
                            ++dmuPredictedDamageFrames;
                        }
                    }
                    if (disengage != null)
                    {
                        disengage.AddZones(hints, world, time);
                        if (DisengageForecastEnabled)
                            DisengageForecaster.Apply(world, hints, player);
                        // XAN_HARNESS_DISENGAGE_DEBUG=<from>-<to>: per-frame player position, movement and forecast inside that time range
                        if (DisengageDebugRange is var (debugFrom, debugTo) && time >= debugFrom && time <= debugTo)
                        {
                            var f = hints.Disengage;
                            Console.WriteLine(FormattableString.Invariant($"dbg z={_zoneID} d={_duration:f1} t={time:f2} pos=({player.Position.X:f2},{player.Position.Z:f2}) moving={moving} zones={hints.ForbiddenZones.Count} moveIn={(f.ForcedMoveIn < 1000 ? f.ForcedMoveIn : -1):f2} moveFor={f.ForcedMoveFor:f2} lossIn={(f.TargetLossIn < 1000 ? f.TargetLossIn : -1):f2} returnIn={(f.TargetReturnIn < 1000 ? f.TargetReturnIn : -1):f2} maxCast={(hints.MaxCastTime < 1000 ? hints.MaxCastTime : -1):f2} cast={player.CastInfo?.Action.ToString() ?? "-"}"));
                        }
                    }
                    var allocStart = ExecProfile.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
                    var executeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    module.Execute(strategy, activeTarget, estimatedAnimLockDelay: FrameStep, isMoving: moduleMoving);
                    var executeTicks = System.Diagnostics.Stopwatch.GetTimestamp() - executeStart;
                    _executeTicks += executeTicks;
                    if (ExecProfile.Enabled)
                        ExecProfile.Record(_job.Name, executeTicks, GC.GetAllocatedBytesForCurrentThread() - allocStart, time);
                    if (module is XanNIN nin)
                        NinDecisionLog.Observe(nin, _zoneID, _duration, time);
                    if (DisengageDebugRange is var (queueFrom, queueTo) && time >= queueFrom && time <= queueTo)
                    {
                        var queued = string.Join(';', hints.ActionsToExecute.Entries.Select(entry => FormattableString.Invariant($"{entry.Action}:p{entry.Priority:f0}:cast{entry.CastTime:f2}:{(entry.Target != null ? player.DistanceToHitbox(entry.Target).ToString("f1", System.Globalization.CultureInfo.InvariantCulture) : "-")}")));
                        Console.WriteLine(FormattableString.Invariant($"dbgq z={_zoneID} d={_duration:f1} t={time:f2} gcd={world.Client.Cooldowns[ActionDefinitions.GCDGroup].Remaining:f2} queue={queued}"));
                    }
                    var burstActionsBefore = _actions.Count;
                    _burst?.Before(hints, activeTarget, targetAvailable, time);
                    ExecuteBestAction(world, player, hints, targetAvailable, time);
                    if (_burst != null)
                    {
                        _burstNew.Clear();
                        for (var i = burstActionsBefore; i < _actions.Count; ++i)
                            _burstNew.Add(_actions[i].Action);
                        _burst.After(_burstNew, time);
                    }
                    // "could have pressed a GCD and did not": the direct GCD-uptime signal agents_mnk.md ranks first
                    if (targetAvailable && player.CastInfo == null && world.Client.Cooldowns[ActionDefinitions.GCDGroup].Remaining <= 0 && world.Client.AnimationLock <= 0)
                    {
                        ++_gcdIdleFrames;
                        _irregular?.NoteIdle();
                    }
                    if (_irregular != null)
                    {
                        var casting = player.CastInfo != null;
                        if (casting && !wasCasting)
                            _irregular.NoteCastStart(time);
                        wasCasting = casting;
                    }
                    if (BlmSeries != null && _blmCombat != null && frame % 10 == 0)
                        BlmSeries.Add(SampleBlm(world, time, targetAvailable));
                }
            }
            finally
            {
                ExternalMechanicHintProvider.ClearNamespace(TargetLossHintSource);
                DisengageForecaster.Reset();
                Irregular.Current = null;
                BurstControl.Current = null;
            }
            _ninCombat?.Finish();
            _burst?.Finish(_duration, _partyWindows, PartyBuffFirstCast != null);
            if (disengage != null)
            {
                DisengageMovingFrames += disengage.MovingFrames;
                DisengageInterruptedCasts += (_blmCombat?.InterruptedCasts ?? 0) + (_rprCombat?.InterruptedCasts ?? 0) + (_samCombat?.InterruptedCasts ?? 0) + (_pldCombat?.InterruptedCasts ?? 0);
            }
            if (_irregular != null)
            {
                _irregularStats = _irregular.Finish(_actions.Select(action => (action.Time, action.GCD)), _duration);
                IrregularTotals = IrregularTotals.Add(_irregularStats);
            }

            var riddleOfEarthActions = _job.Class == Class.MNK ? _actions.Count(action => action.Action == ActionID.MakeSpell(MNKAID.RiddleOfEarth)) : 0;
            CheckRequirements(dmu, dmuActionDriver, dmuPredictedDamageFrames, riddleOfEarthActions);
            _terminalWorld = world;
            _terminalPlayer = player;
            var metrics = BuildMetrics();
            if (_burst != null && BurstControl.ScenPath != null)
                BurstControl.WriteScen(FormattableString.Invariant($"{_job.Name},{_burstKey},{_duration:f1},{metrics.Potency:f1},{metrics.Total:f1},{metrics.RdpsTotal:f1},{_actions.Count(action => action.GCD)},{_failures.Count},{_burst.SummaryCsv()}"));
            if (ActionCounts != null)
                foreach (var action in _actions)
                    ActionCounts[action.Action] = ActionCounts.GetValueOrDefault(action.Action) + 1;
            WriteTrace(metrics);
            LastActionHash = HashActions(_actions);
            LastBlmForceOutcome = _blmCombat?.ForceOutcome ?? "";
            LastBlmFollowupsComplete = _blmCombat != null && !_blmCombat.FollowupIllegal && _blmCombat.FollowupsApplied == (_blmCombat.ForceFollowups?.Length ?? 0);
            return new(_frames, _actions.Count, _actions.Count(action => action.GCD), _actions.Count(action => !action.GCD), _actions.Count(action => !action.TargetAvailable && _enemyTargetIDs.Contains(action.TargetID)), _actions.Count(action => action.GCD && _targetReturnTimes.Any(returnTime => action.Time >= returnTime && action.Time <= returnTime + ResumeGCDLimit)), dmuActionDriver?.Emitted ?? 0, dmuActionDriver?.Skipped ?? 0, dmuPredictedDamageFrames, riddleOfEarthActions, dmu?.StateMachine.ActivePhaseIndex, _failures, metrics, _irregularStats);
        }

        // The disengage and irregular drivers move the player, so the emulators have to refuse out-of-range targets and cast starts
        // while moving; every other run leaves this off and stays byte-identical.
        private void SetEnforceRangeAndMovement()
        {
            if (_rprCombat != null)
                _rprCombat.EnforceRangeAndMovement = true;
            if (_blmCombat != null)
                _blmCombat.EnforceRangeAndMovement = true;
            if (_mnkCombat != null)
                _mnkCombat.EnforceRangeAndMovement = true;
            if (_gnbCombat != null)
                _gnbCombat.EnforceRangeAndMovement = true;
            if (_ninCombat != null)
                _ninCombat.EnforceRangeAndMovement = true;
            if (_drgCombat != null)
                _drgCombat.EnforceRangeAndMovement = true;
            if (_samCombat != null)
                _samCombat.EnforceRangeAndMovement = true;
            if (_vprCombat != null)
                _vprCombat.EnforceRangeAndMovement = true;
            if (_mchCombat != null)
                _mchCombat.EnforceRangeAndMovement = true;
            if (_pldCombat != null)
                _pldCombat.EnforceRangeAndMovement = true;
        }

        // Countdown before the pull: the player is out of combat with the target up, so the rotation prepares what it prepares before a
        // real pull (NIN: Hide if the mudra are down, the countdown Suiton). Frames run up to the last step before time zero.
        private void RunPrePull(WorldState world, Actor player, Actor target, AIHints hints, RotationModule module, StrategyValues strategy, float prePull)
        {
            var frames = (int)MathF.Round(prePull / FrameStep);
            for (var k = 1; k < frames; ++k)
            {
                var time = -prePull + k * FrameStep;
                AdvanceFrameExplicit(world, time, (ulong)(1_000_000 + k), FrameStep);
                world.Execute(new ClientState.OpCountdownChange(-time));
                _ninCombat?.Advance();
                _samCombat?.Advance();
                _vprCombat?.Advance();
                _mchCombat?.Advance();
                RefreshHints(hints, new[] { target });
                ApplyFightRemaining(hints, time);
                module.Execute(strategy, target, estimatedAnimLockDelay: FrameStep, isMoving: false);
                ExecuteBestAction(world, player, hints, true, time);
            }
            world.Execute(new ClientState.OpCountdownChange(null));
            world.Execute(new ActorState.OpCombat(player.InstanceID, true));
        }

        // XAN_HARNESS_TRACE_DIR: write every executed action of every run as CSV so two builds can be diffed line by line.
        private static int _traceSequence;
        private void WriteTrace(RotationMetrics metrics)
        {
            var directory = Environment.GetEnvironmentVariable("XAN_HARNESS_TRACE_DIR");
            if (string.IsNullOrWhiteSpace(directory))
                return;
            Directory.CreateDirectory(directory);
            var sequence = Interlocked.Increment(ref _traceSequence);
            var name = FormattableString.Invariant($"{sequence:D4}_{_job.Name}_z{_zoneID}_d{_duration:f1}");
            if (_irregular != null)
                name += FormattableString.Invariant($"_irr{Irregular.Seed}");
            using (var writer = new StreamWriter(Path.Combine(directory, name + ".csv")))
            {
                writer.WriteLine("time,action,gcd,target_available,target,potency");
                foreach (var action in _actions)
                    writer.WriteLine(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{action.Time:f3},{action.Action},{(action.GCD ? 1 : 0)},{(action.TargetAvailable ? 1 : 0)},{action.TargetID:X},{action.Potency:f1}"));
                foreach (var failure in _failures)
                    writer.WriteLine($"failure,{failure.Replace(',', ';')}");
                if (_ninCombat != null)
                {
                    foreach (var window in _ninCombat.KunaiWindows)
                        writer.WriteLine($"kunai_window,{window}");
                    foreach (var ev in _ninCombat.Events)
                        writer.WriteLine($"nin_event,{ev}");
                }
                if (_drgCombat != null)
                    foreach (var ev in _drgCombat.Events)
                        writer.WriteLine($"drg_event,{ev}");
                if (_samCombat != null)
                    foreach (var ev in _samCombat.Events)
                        writer.WriteLine($"sam_event,{ev}");
                if (_vprCombat != null)
                    foreach (var ev in _vprCombat.Events)
                        writer.WriteLine($"vpr_event,{ev}");
                if (_mchCombat != null)
                    foreach (var ev in _mchCombat.Events)
                        writer.WriteLine($"mch_event,{ev}");
                if (_pldCombat != null)
                    foreach (var ev in _pldCombat.Events)
                        writer.WriteLine($"pld_event,{ev}");
                foreach (var gap in _ddGaps)
                    if (gap.End - gap.Start >= 0.5f)
                        writer.WriteLine(FormattableString.Invariant($"dd_gap,{gap.Start:f2},{gap.End:f2},{gap.End - gap.Start:f2},{gap.Target:X}"));
                // irregular,<kind>,<start>,<end>,<seconds>,<detail>,refused=,gcd_in=,idle_in=,resume= - one line per episode that started
                if (_irregular != null)
                    foreach (var episode in _irregular.Episodes)
                        if (episode.Started)
                            writer.WriteLine($"irregular,{episode.Describe()}");
            }
            var summaryPath = Path.Combine(directory, "summary.csv");
            // the irr_* columns are appended at the end so older parsers and diffs of the leading columns keep working
            if (!File.Exists(summaryPath))
                File.WriteAllText(summaryPath, "run,frames,actions,potency,terminal,total,dd_uptime,soul_overcap,shroud_overcap,enshroud,communio,perfectio,gluttony,ph,polyglot_overcap,exec_ms,party_ac,rdps,chakra_overcap,blitz,pr,pb,beast_drop,rof_gcds,rof_potency,opo_gcds,rof_opo_gcds,bh_gcds,replies,replies_lost,gcd_uptime,gcd_idle,cart_overcap,nm,nm_gcds,gf_chains,reign_chains,dd,sb,cont_lost,burst_outside_nm,pre_nm_gf,anchorable_nm,burst_openers_outside_nm,party_doku,kunai_potency,kunai_gcds,raiju_lost,rabbits,ninjutsu_rejected,tcj_invalid,tcj_incomplete,mudra_timeouts,mudra_cap_frames,ninki_overcap,rf,kf,opener_dok_gcd_sum,opener_kunai_gcd_sum,opener_runs,party_litany,lc_gcds,lotd_gcds,ls_lost,ls_weak,procs_lost,focus_overcap,no_surge_gcds,dot_gap_frames,geirskogul,stardiver,kenki_overcap,meditation_overcap,sen_overcap,sam_procs_lost,no_fugetsu_gcds,sam_dot_gap_frames,iaijutsu,tsubame,namikiri,sam_dot_potency,offering_overcap,coil_overcap,vpr_procs_lost,follow_ups_lost,no_instinct_gcds,reawaken,generations,uncoiled_fury,coils,positionals_missed,heat_overcap,battery_overcap,mch_procs_lost,hypercharge,overheated_shots,overheat_lost,wildfire,wildfire_hits,queens,queen_battery,pet_hits_lost,reassembled_tools,reassemble_wasted,mch_dot_potency,failures," + IrregularDriver.Stats.CsvHeader + (_pldCombat != null ? "," + PldMetrics.CsvHeader : "") + (BurstControl.Enabled ? "," + BurstTracker.CsvHeader : "") + "\n");
            File.AppendAllText(summaryPath, FormattableString.Invariant($"{name},{_frames},{_actions.Count},{metrics.Csv()},{_failures.Count},{_irregularStats.Csv()}{(_pldCombat != null ? "," + metrics.Pld.Csv() : "")}{(BurstControl.Enabled && _burst != null ? "," + _burst.SummaryCsv() : "")}\n"));
        }

        public (int Checks, IReadOnlyList<string> Failures) RunRprPotionTest(RprPotionTestCase test)
        {
            var world = BuildWorld(out var player, out var target, out _, out var hints);
            using var bossmods = new BossModuleManager(world);
            var database = new RotationDatabase(new DirectoryInfo("tools/xan_timeline_harness/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
            using var manager = new RotationModuleManager(database, bossmods, hints) { CombatStart = test.Boundary ? BaseTime.AddSeconds(-300) : BaseTime };
            using var module = new XanRPR(manager, player);
            var strategy = new StrategyValues(XanRPR.Definition().Configs);
            void Option<T>(string track, T value) where T : Enum
                => ((StrategyValueTrack)strategy.Values[strategy.Configs.FindIndex(config => config.InternalName == track)]).Option = Convert.ToInt32(value);
            Option("POT", test.Potion);
            Option("Reaver", test.Reaver);
            Option("Enshroud", test.Enshroud);
            Option("RedGauge", test.RedGauge);
            world.Execute(new ClientState.OpPlayerStatsChange(new(test.SkillSpeed, 400, 100)));
            world.Execute(new ClientState.OpInventoryChange(ActionDefinitions.IDPotionStr.ID, test.PotionCount));
            var actions = new List<RprPotionAction>();
            float StatusLeft(uint status) => MathF.Max(0, (float)((player.FindStatus(status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);
            RprCombatState? combat = null;
            var potency = 0f;
            combat = new(world, player, FrameStep, (action, _, _, actionPotency) =>
            {
                potency += actionPotency;
                actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, combat!.Shroud, StatusLeft(49), StatusLeft((uint)RPRSID.ArcaneCircle), module.RedGauge));
            }, test.Boundary ? test.Soul : 0, test.Boundary ? test.Shroud : 0);
            void Cooldown(RPRAID action, float remaining)
                => world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.Instance.Spell(action)!.ActualMainCooldownGroup(world.Client.DutyActions), new(0, remaining))]));
            if (test.Boundary)
            {
                world.Execute(new ActorState.OpStatus(target.InstanceID, 0, new((uint)RPRSID.DeathsDesign, 0, world.FutureTime(test.DeathsDesignLeft), player.InstanceID)));
                Cooldown(RPRAID.ArcaneCircle, 120);
                combat.Advance();
                module.Execute(strategy, target, FrameStep, false); // Observe an established cycle before the tested pre-burst state.
                Cooldown(RPRAID.ArcaneCircle, test.ArcaneCircleIn);
                Cooldown(RPRAID.Gluttony, test.GluttonyIn);
                Cooldown(RPRAID.Enshroud, test.EnshroudIn);
                Cooldown(RPRAID.SoulSlice, 60);
                Cooldown(RPRAID.BloodStalk, test.SoulSpenderIn);
                world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new(0, test.GCD)), (ActionDefinitions.PotionCDGroup, new(0, test.PotionIn))]));
                world.Execute(new ClientState.OpComboChange(new((uint)RPRAID.Slice, test.ComboLeft)));
            }

            var checks = 0;
            void Check(bool condition, string message)
            {
                ++checks;
                if (!condition)
                    _failures.Add($"{test.Name}: {message}");
            }
            static bool SoulSpender(ActionID action) => action.Type == ActionType.Spell && (RPRAID)action.ID is RPRAID.BloodStalk or RPRAID.UnveiledGibbet or RPRAID.UnveiledGallows or RPRAID.GrimSwathe or RPRAID.Gluttony;
            const string mechanicSource = "bossmod.external.splatoon.rpr-potion-test"; // providers reject namespaces outside the external-hint prefix
            var frameOffset = 0;
            if (!test.Boundary && CountdownSeconds is { } countdown)
            {
                // Pre-pull: out of combat with a running countdown, so the module casts Soulsow and pre-casts Harpe.
                world.Execute(new ActorState.OpCombat(player.InstanceID, false));
                world.Execute(new ClientState.OpCountdownChange(countdown));
                frameOffset = (int)MathF.Round(countdown / FrameStep);
                for (var frame = 0; frame < frameOffset; ++frame)
                {
                    AdvanceFrameExplicit(world, (frame - frameOffset) * FrameStep, (ulong)frame, frame == 0 ? 0 : FrameStep);
                    combat.Advance();
                    RefreshHints(hints, [target]);
                    module.Execute(strategy, target, FrameStep, false);
                    combat.ExecuteBestAction(hints);
                }
                world.Execute(new ClientState.OpCountdownChange(null));
                world.Execute(new ActorState.OpCombat(player.InstanceID, true));
            }
            try
            {
                for (var frame = 0; frame * FrameStep <= _duration + FrameStep * 0.5f; ++frame)
                {
                    AdvanceFrameExplicit(world, frame * FrameStep, (ulong)(frame + frameOffset), frame == 0 && frameOffset == 0 ? 0 : FrameStep);
                    combat.Advance();
                    RefreshHints(hints, [target]);
                    if (test.DowntimeIn < float.MaxValue)
                        ExternalMechanicHintProvider.PushSnapshot(new(mechanicSource, world.CurrentZone, world.CurrentCFCID, ExternalZoneConfidence.TrustedSplatoonScript, world.FutureTime(1), float.MaxValue, MathF.Max(0, test.DowntimeIn - frame * FrameStep), float.MaxValue, float.MaxValue, false), world.CurrentZone, world.CurrentCFCID, world.CurrentTime);
                    module.Execute(strategy, target, FrameStep, false);
                    combat.ExecuteBestAction(hints);
                    if (frame == 0 && test.Boundary)
                    {
                        var spender = actions.FirstOrDefault(action => SoulSpender(action.Action));
                        Check((spender != null) == test.ExpectImmediateSpend, $"immediate spender expected={test.ExpectImmediateSpend}, actions={string.Join(',', actions.Select(action => action.Action))}");
                        if (test.ExpectImmediateSpend)
                            Check(spender != null && (spender.Action == ActionID.MakeSpell(RPRAID.Gluttony)) == test.ExpectGluttony, $"Gluttony expected={test.ExpectGluttony}, actual={spender?.Action}");
                        if (test.ExpectEnshroud)
                            Check(actions.Any(action => action.Action == ActionID.MakeSpell(RPRAID.Enshroud)), "an already-due normal Enshroud must retain priority over optional Soul conversion");
                    }
                }
            }
            finally
            {
                ExternalMechanicHintProvider.ClearNamespace(mechanicSource);
            }

            if (test.Boundary && (test.ExpectImmediateSpend || test.ExpectDelayedGluttony))
            {
                if (test.ExpectDelayedGluttony)
                    Check(actions.Any(action => action.Action == ActionID.MakeSpell(RPRAID.Gluttony) && action.Time >= test.GluttonyIn), "reserved Soul must be converted through the upcoming Gluttony");
                var expectedReavers = test.ExpectGluttony ? 2 : 1;
                var reavers = actions.Where(action => action.Action.Type == ActionType.Spell && (RPRAID)action.Action.ID is RPRAID.Gibbet or RPRAID.Gallows or RPRAID.Guillotine or RPRAID.ExecutionersGibbet or RPRAID.ExecutionersGallows or RPRAID.ExecutionersGuillotine).Take(expectedReavers).ToArray();
                Check(reavers.Length == expectedReavers && reavers[^1].Shroud == test.Shroud + expectedReavers * 10, "spender must convert Soul to Shroud without overcap");
                Check(reavers.Length == expectedReavers && reavers[^1].Time + ActionDefinitions.Instance[reavers[^1].Action]!.InstantAnimLock + FrameStep < test.ArcaneCircleIn - 4, "Reavers must finish their locks before AC-4s");
            }
            if (!test.Boundary)
            {
                var circles = actions.Where(action => action.Action == ActionID.MakeSpell(RPRAID.ArcaneCircle)).ToArray();
                Check(circles.Length >= 5, "continuous timeline must cover five Arcane Circles");
                for (var index = 1; index < circles.Length; ++index)
                    Check(MathF.Abs(circles[index].Time - circles[index - 1].Time - 120) <= FrameStep * 1.1f, $"Arcane Circle drift at {circles[index].Time:f2}s");
                var potions = actions.Where(action => action.Action == ActionDefinitions.IDPotionStr).ToArray();
                Check(test.Potion == XanRPR.PotionUseStrategy.Off ? potions.Length == 0 : potions.Any(action => action.Time > 40), "potion usage must match the selected policy");
                if (test.Potion == XanRPR.PotionUseStrategy.EvenBurstExceptOpener)
                    Check(potions.All(action => action.Time >= 40), "opener must not use a potion");
                foreach (var potion in potions.Where(action => action.Time > 40))
                {
                    var communios = actions.Where(action => action.Action == ActionID.MakeSpell(RPRAID.Communio) && action.Time >= potion.Time && action.Time < potion.Time + 30).ToArray();
                    var enshrouds = actions.Where(action => action.Action == ActionID.MakeSpell(RPRAID.Enshroud) && action.Time >= potion.Time - 10 && action.Time < potion.Time + 30).ToArray();
                    Check(communios.Length == 3 && enshrouds.Length == 3, $"potion at {potion.Time:f2}s needs three Enshrouds/Communios; got {enshrouds.Length}/{communios.Length}");
                    if (Environment.GetEnvironmentVariable("XAN_HARNESS_POTION_WINDOWS") == "1" && communios.Length == 3 && enshrouds.Length == 3)
                        Console.WriteLine($"{test.Name} potion_window_ok={string.Join(';', actions.Where(action => action.Time >= potion.Time - 35 && action.Time < potion.Time + 35).Select(action => $"{action.Time:f2}:{action.Action}:blue{action.Shroud}:soul_before{action.SoulBefore}"))}");
                    if (communios.Length != 3 || enshrouds.Length != 3)
                    {
                        Console.WriteLine($"{test.Name} potion_window={string.Join(';', actions.Where(action => action.Time >= potion.Time - 35 && action.Time < potion.Time + 35).Select(action => $"{action.Time:f2}:{action.Action}:blue{action.Shroud}:soul_before{action.SoulBefore}"))}");
                        continue;
                    }
                    Check(enshrouds[0].Shroud == 50, "first potion-burst Enshroud must start with Shroud 100");
                    Check(actions.Any(action => action.Action == ActionID.MakeSpell(RPRAID.PlentifulHarvest) && action.Time > communios[0].Time && action.Time < enshrouds[1].Time), "PH must precede the second Enshroud");
                    Check(actions.Any(action => action.Action == ActionID.MakeSpell(RPRAID.Perfectio) && action.Time > communios[1].Time && action.Time < enshrouds[2].Time && action.PotionLeft > 0 && action.ArcaneCircleLeft > 0), "Perfectio must stay before the third Enshroud and inside both buffs");
                    Check(communios[2].PotionLeft > 0, "third Communio must finish inside the potion");
                    Console.WriteLine($"{test.Name} potion={potion.Time:f2} third_communio={communios[2].Time:f2} potion_left={communios[2].PotionLeft:f2}");
                }
                var timeline = string.Join(';', actions.Select(action => FormattableString.Invariant($"{action.Time:F2}:{action.Action.Type}:{action.Action.ID}")));
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(timeline)));
                Console.WriteLine($"{test.Name} actions={actions.Count} hash={hash}");
                Console.WriteLine(FormattableString.Invariant($"{test.Name} potency={potency:f0}"));
                if (Environment.GetEnvironmentVariable("XAN_HARNESS_OPENER") == "1")
                    Console.WriteLine($"{test.Name} opener={string.Join(';', actions.Where(action => action.Time < 12).Select(action => FormattableString.Invariant($"{action.Time:f2}:{action.Action}")))}");
            }
            return (checks, _failures);
        }

        private void ValidateBlmBoundaries(WorldState world, Actor player, Actor target, RotationModuleManager manager, AIHints hints)
        {
            using var blm = new XanBLM(manager, player);
            const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            object? Call(string name, params object?[] args) => typeof(XanBLM).GetMethod(name, members)!.Invoke(blm, args);
            static void Set(object state, string name, object value) => state.GetType().GetField(name)!.SetValue(state, value);
            void Check(bool condition, string name)
            {
                if (!condition)
                    _failures.Add($"BLM boundary: {name}");
            }
            void Equal(float actual, float expected, string name) => Check(MathF.Abs(actual - expected) < 0.001f, $"{name}: expected={expected}, actual={actual}");
            static Track<T> Track<T>(T value) where T : struct => new(value, new StrategyValueTrack(), 0);
            var strategy = new XanBLM.Strategy
            {
                Targeting = Track(Targeting.Auto),
                AOE = Track(AOEStrategy.AOE),
                Thunder = Track(XanBLM.ThunderStrategy.Delay),
                Triplecast = Track(XanBLM.TriplecastStrategy.Delay),
                Manafont = Track(OffensiveStrategy.Delay),
                Leylines = Track(XanBLM.LeylinesStrategy.Delay),
            };
            var stats = world.Client.PlayerStats;
            var hpmp = player.HPMP;
            var level = player.Level;
            try
            {
                foreach (var testLevel in new[] { 18, 20, 34, 35, 40, 49, 50, 57 })
                foreach (var mp in new[] { 0, 799, 800, 1599, 1600, 2000, 2999, 3000 })
                {
                    world.Execute(new ActorState.OpClassChange(player.InstanceID, Class.BLM, testLevel));
                    world.Execute(new ActorState.OpHPMP(player.InstanceID, new(hpmp.CurHP, hpmp.MaxHP, hpmp.Shield, (uint)mp, hpmp.MaxMP)));
                    blm.Execute(strategy, target, FrameStep, false);
                    blm.Element = testLevel >= 35 ? 3 : testLevel >= 20 ? 2 : 1;
                    blm.Hearts = 0;
                    blm.Thunderhead = false;
                    blm.Firestarter = false;
                    hints.ActionsToExecute.Clear();
                    Call("FireAOELowLevel", strategy, hints.FindEnemy(target));
                    var expected = testLevel >= 50 && mp is >= 800 and < 3000 ? BossMod.BLM.AID.Flare
                        : mp >= 3000 ? BossMod.BLM.AID.Fire2
                        : testLevel >= 35 ? BossMod.BLM.AID.Blizzard3 : BossMod.BLM.AID.Blizzard1;
                    Check(hints.ActionsToExecute.Entries.Any(e => e.Action == ActionID.MakeSpell(expected)), $"AoE recovery lv={testLevel} mp={mp}, missing {expected}");
                }

                var transposeGroup = ActionDefinitions.Instance.Spell(BossMod.BLM.AID.Transpose)!.ActualMainCooldownGroup(world.Client.DutyActions);
                var transposeCooldown = world.Client.Cooldowns[transposeGroup];
                try
                {
                    foreach (var testLevel in new[] { 12, 17, 18, 20, 34, 35, 40, 50, 57, 58, 70, 82, 100 })
                    foreach (var mp in new[] { 0, 799, 800, 2399, 2400, 9600 })
                    {
                        world.Execute(new ActorState.OpClassChange(player.InstanceID, Class.BLM, testLevel));
                        world.Execute(new ActorState.OpHPMP(player.InstanceID, new(hpmp.CurHP, hpmp.MaxHP, hpmp.Shield, (uint)mp, hpmp.MaxMP)));
                        blm.Execute(strategy, target, FrameStep, false);
                        Check((bool)Call("CanQueueGCD", BossMod.BLM.AID.Blizzard2)! == testLevel >= 12 && (bool)Call("CanQueueGCD", BossMod.BLM.AID.HighBlizzard2)! == testLevel >= 82, $"Blizzard II dispatch follows its unlock level at lv={testLevel}");
                        var maxElement = testLevel >= 35 ? 3 : testLevel >= 20 ? 2 : 1;
                        foreach (var targets in new[] { 2, 3, 4 })
                        foreach (var element in new[] { 0, maxElement, -1, -maxElement }.Distinct())
                        foreach (var hearts in new[] { 0, blm.MaxHearts }.Distinct())
                        foreach (var transposeIn in new[] { 0f, 5f })
                        {
                            world.Execute(new ClientState.OpCooldown(false, [(transposeGroup, new Cooldown(0, transposeIn))]));
                            blm.NumAOETargets = targets;
                            blm.Element = element;
                            blm.Hearts = hearts;
                            blm.AstralSoul = 0;
                            blm.Polyglot = 0;
                            blm.Paradox = false;
                            blm.Thunderhead = false;
                            blm.Firestarter = false;
                            hints.ActionsToExecute.Clear();
                            if (!(bool)Call("TryPushNeutralAOEStartGCD", strategy, hints.FindEnemy(target))!)
                                Call(element > 0 ? "FirePhase" : "IcePhase", strategy, hints.FindEnemy(target));

                            var context = $"lv={testLevel} targets={targets} element={element} hearts={hearts} mp={mp} transpose={transposeIn}";
                            var blizzard2Worthwhile = testLevel >= 82 ? targets >= 3 : testLevel >= 12 && targets >= 4;
                            var blizzard2Queued = hints.ActionsToExecute.Entries.Any(e => e.Action.ID is (uint)BossMod.BLM.AID.Blizzard2 or (uint)BossMod.BLM.AID.HighBlizzard2);
                            // Blizzard II / High Blizzard II is the neutral-element AoE entry from 3 targets (4 before the upgrade) and the pre-Flare Star fire-to-ice fallback; never otherwise.
                            Check(!blizzard2Queued || blizzard2Worthwhile && (element == 0 || element > 0 && testLevel < 100), $"Blizzard II only where it beats Blizzard III: {context}");
                            Check(element != 0 || !blizzard2Worthwhile || blizzard2Queued, $"neutral AoE entry uses Blizzard II from {(testLevel >= 82 ? 3 : 4)} targets: {context}");
                            if (testLevel == 100 && element != 0)
                                Check(!hints.ActionsToExecute.Entries.Any(e => e.Action.ID is (uint)BossMod.BLM.AID.Blizzard3 or (uint)BossMod.BLM.AID.HighBlizzard2 or (uint)BossMod.BLM.AID.Fire2 or (uint)BossMod.BLM.AID.HighFire2), $"level 100 AoE uses Transpose for element swaps: {context}");
                            Check((bool)Call("UseAOERotation")! == (testLevel >= 18 || blizzard2Worthwhile), $"AoE rotation from Fire II, or from Blizzard II with 4+ targets: {context}");
                            if (testLevel < 18 && !blizzard2Worthwhile)
                                continue;
                            Check(hints.ActionsToExecute.Entries.Any(e => ActionDefinitions.Instance[e.Action]?.IsGCD == true || e.Action == ActionID.MakeSpell(BossMod.BLM.AID.Transpose)), $"AoE has a recovery action: {context}");

                            if (testLevel < 58)
                                continue;
                            var planned = Call("CurrentAOEPlannerState", strategy, null)!;
                            Set(planned, "DowntimeIn", float.MaxValue);
                            var actions = ((IEnumerable<BossMod.BLM.AID>)Call("AOEPlannerActions", planned)!).ToArray();
                            Check(actions.Any(a => a is BossMod.BLM.AID.Blizzard2 or BossMod.BLM.AID.HighBlizzard2) == (element == 0 && blizzard2Worthwhile), $"Blizzard II planner candidate only as the neutral AoE entry from {(testLevel >= 82 ? 3 : 4)} targets: {context}");
                            if (element == 0)
                            {
                                var iceEntry = blizzard2Worthwhile ? testLevel >= 82 ? BossMod.BLM.AID.HighBlizzard2 : BossMod.BLM.AID.Blizzard2 : BossMod.BLM.AID.Blizzard3;
                                Check(actions.Contains(iceEntry), $"neutral AoE planner enters ice with {iceEntry}: {context}");
                                object?[] entryArgs = [planned, iceEntry];
                                Check((bool)Call("ApplyAOEPlannerAction", entryArgs)!, $"AoE {iceEntry} applies: {context}");
                                var entered = entryArgs[0]!;
                                Check((int)entered.GetType().GetField("Element")!.GetValue(entered)! == -3, $"AoE {iceEntry} grants UI3: {context}");
                            }
                            if (element < 0 && (hearts < blm.MaxHearts || mp < 2400 || testLevel == 100 && transposeIn > 0))
                                Check(actions.Contains(targets >= 3 ? BossMod.BLM.AID.Freeze : BossMod.BLM.AID.Blizzard4), $"AoE planner restores hearts/MP: {context}");
                        }
                    }

                    world.Execute(new ActorState.OpClassChange(player.InstanceID, Class.BLM, 100));
                    foreach (var targets in new[] { 2, 3 })
                    {
                        var heartAction = targets >= 3 ? BossMod.BLM.AID.Freeze : BossMod.BLM.AID.Blizzard4;
                        foreach (var step in new (int Element, int MP, int Hearts, int Soul, bool Paradox, float TransposeIn, BossMod.BLM.AID Action)[]
                        {
                            (1, 2500, 3, 0, false, 0f, BossMod.BLM.AID.Flare),
                            (3, 833, 0, 3, false, 0f, BossMod.BLM.AID.Flare),
                            (3, 0, 0, 6, false, 0f, BossMod.BLM.AID.FlareStar),
                            (3, 0, 0, 0, false, 0f, BossMod.BLM.AID.Transpose),
                            (-1, 0, 0, 0, true, 5f, heartAction),
                            (-1, 2500, 3, 0, true, 2.5f, BossMod.BLM.AID.Paradox),
                            (-1, 2500, 3, 0, false, 0f, BossMod.BLM.AID.Transpose),
                        })
                        {
                            world.Execute(new ActorState.OpHPMP(player.InstanceID, new(hpmp.CurHP, hpmp.MaxHP, hpmp.Shield, (uint)step.MP, hpmp.MaxMP)));
                            blm.Execute(strategy, target, FrameStep, false);
                            world.Execute(new ClientState.OpCooldown(false, [(transposeGroup, new Cooldown(0, step.TransposeIn))]));
                            blm.NumAOETargets = targets;
                            blm.Element = step.Element;
                            blm.Hearts = step.Hearts;
                            blm.AstralSoul = step.Soul;
                            blm.Paradox = step.Paradox;
                            blm.Polyglot = 0;
                            blm.Thunderhead = false;
                            blm.Firestarter = false;
                            hints.ActionsToExecute.Clear();
                            Call(step.Element > 0 ? "FirePhase" : "IcePhase", strategy, hints.FindEnemy(target));
                            var best = hints.ActionsToExecute.Entries.OrderByDescending(e => e.Priority).FirstOrDefault();
                            Check(best.Action == ActionID.MakeSpell(step.Action), $"level 100 Transpose loop targets={targets}: expected={step.Action}, actual={best.Action}");
                        }

                        var loop = Call("CurrentAOEPlannerState", strategy, null)!;
                        Set(loop, "Element", 1);
                        Set(loop, "MP", 2500);
                        Set(loop, "Hearts", 3);
                        Set(loop, "AstralSoul", 0);
                        Set(loop, "Paradox", false);
                        Set(loop, "TransposeReadyIn", 0f);
                        Set(loop, "NextPolyglot", 30f);
                        Set(loop, "ElementTimer", float.MaxValue);
                        Set(loop, "DowntimeIn", float.MaxValue);
                        Set(loop, "AllowManafont", false);
                        Set(loop, "AllowThunder", false);
                        Set(loop, "AllowBurstActions", false);
                        for (var cycle = 0; cycle < 8; ++cycle)
                        foreach (var action in new[] { BossMod.BLM.AID.Flare, BossMod.BLM.AID.Flare, BossMod.BLM.AID.FlareStar, BossMod.BLM.AID.Transpose, heartAction, BossMod.BLM.AID.Paradox, BossMod.BLM.AID.Transpose })
                        {
                            var candidates = (IEnumerable<BossMod.BLM.AID>)Call("AOEPlannerActions", loop)!;
                            Check(candidates.Contains(action), $"Transpose planner cycle={cycle} targets={targets}: missing {action}");
                            object?[] loopArgs = [loop, action];
                            Check((bool)Call("ApplyAOEPlannerAction", loopArgs)!, $"Transpose planner cycle={cycle}: cannot apply {action}");
                            loop = loopArgs[0]!;
                            if (action == heartAction)
                            {
                                Check((int)loop.GetType().GetField("Element")!.GetValue(loop)! == -1, "Transpose heart spell preserves UI1");
                                Check((int)loop.GetType().GetField("MP")!.GetValue(loop)! == 2500, "UI1 heart spell restores 2500 MP");
                            }
                        }
                    }
                }
                finally
                {
                    world.Execute(new ClientState.OpCooldown(false, [(transposeGroup, transposeCooldown)]));
                }

                world.Execute(new ActorState.OpClassChange(player.InstanceID, Class.BLM, 100));
                world.Execute(new ActorState.OpHPMP(player.InstanceID, hpmp));
                world.Execute(new ActorState.OpStatus(player.InstanceID, 0, new((uint)BossMod.BLM.SID.Swiftcast, 0, world.FutureTime(10), player.InstanceID)));
                blm.Execute(strategy, target, FrameStep, false);
                blm.Element = 3;
                blm.AstralSoul = 6;
                blm.Thunderhead = true;
                var state = Call("CurrentPlannerState", strategy, false)!;
                Set(state, "ThunderLeft", 0f);
                Set(state, "DowntimeIn", float.MaxValue);
                Set(state, "ElementTimer", float.MaxValue);
                Check(!(bool)Call("ShouldPlanThunder", state)!, "Thunder Delay must suppress ST planning");
                Equal((float)Call("PlannerCastTime", BossMod.BLM.AID.Fire3, state)!, 3.5f, "Swiftcast must not leak into planner base casts");
                Check((bool)Call("UsesReservedInstant", BossMod.BLM.AID.Fire3, state)!, "active Swiftcast must be consumed even with TC Delay");
                var stepType = typeof(XanBLM).GetNestedType("PlannerStep", BindingFlags.NonPublic)!;
                var kindType = typeof(XanBLM).GetNestedType("PlannerRouteKind", BindingFlags.NonPublic)!;
                object?[] stepArgs = [state, Enum.Parse(stepType, "Fire3"), Enum.ToObject(kindType, 0)];
                Check((bool)Call("ApplyPlannerStep", stepArgs)!, "ST instant action applies");
                state = stepArgs[0]!;
                Equal((float)Call("PlannerActionTime", BossMod.BLM.AID.Fire3, state, false)!, 3.6f, "ST cast after Swiftcast consumption (3.5s cast + 0.1s caster tax)");
                Check((int)state.GetType().GetField("ActiveInstantBudget")!.GetValue(state)! == 0, "ST instant budget exhausted");

                var aoeState = Call("CurrentAOEPlannerState", strategy, null)!;
                Set(aoeState, "ThunderLeft", 0f);
                Set(aoeState, "DotTargets", 3);
                Set(aoeState, "Targets", 3);
                Set(aoeState, "DowntimeIn", float.MaxValue);
                Set(aoeState, "ElementTimer", float.MaxValue);
                Set(aoeState, "Element", -3);
                var aoeActions = (IEnumerable<BossMod.BLM.AID>)Call("AOEPlannerActions", aoeState)!;
                Check(!aoeActions.Contains(BossMod.BLM.AID.HighThunder2), "Thunder Delay must suppress AoE planning");
                Equal((float)Call("AOEPlannerActionTime", BossMod.BLM.AID.Blizzard3, aoeState)!, 2.5f, "AoE active Swiftcast");
                object?[] aoeArgs = [aoeState, BossMod.BLM.AID.Blizzard3];
                Check((bool)Call("ApplyAOEPlannerAction", aoeArgs)!, "AoE instant action applies");
                Equal((float)Call("AOEPlannerActionTime", BossMod.BLM.AID.Blizzard3, aoeArgs[0])!, 3.6f, "AoE cast after Swiftcast consumption (3.5s cast + 0.1s caster tax)");

                foreach (var thunder in new[] { XanBLM.ThunderStrategy.Delay, XanBLM.ThunderStrategy.InstantOnly })
                {
                    strategy.Thunder = Track(thunder);
                    hints.ActionsToExecute.Clear();
                    Call("PushPlannerAction", strategy, BossMod.BLM.AID.HighThunder, hints.FindEnemy(target));
                    Call("PushAOEPlannerAction", strategy, BossMod.BLM.AID.HighThunder2, hints.FindEnemy(target));
                    Check(hints.ActionsToExecute.Entries.Count == 0, $"stationary Thunder {thunder} dispatch");
                }

                world.Execute(new ActorState.OpStatus(player.InstanceID, 0, default));
                foreach (var spellSpeed in new[] { 400, 1000, 2000 })
                foreach (var inLeyLines in new[] { false, true })
                foreach (var remaining in new[] { 0f, 0.1f, 20f })
                {
                    world.Execute(new ClientState.OpPlayerStatsChange(new(400, spellSpeed, inLeyLines ? 85 : 100)));
                    blm.InLeyLines = inLeyLines;
                    Set(state, "LeyLinesLeft", remaining);
                    var expected = ActionSpeed.GCDRounded(spellSpeed, remaining > 0 ? 85 : 100, 100);
                    Equal((float)Call("PlannerActionTime", BossMod.BLM.AID.Xenoglossy, state, false)!, expected, $"LL snapshot sps={spellSpeed} active={inLeyLines} remaining={remaining}");
                }

                strategy.Targeting = Track(Targeting.Manual);
                var selected = ((AIHints.Enemy?, int))Call("SelectAOECenterTarget", strategy, null)!;
                Check(selected.Item1 == null && selected.Item2 == 0, "Manual targeting must not acquire another AoE target");

                const ulong invincibleID = 0x400000FF;
                CreateEnemy(world, invincibleID, 0x1234, 20, "Invincible", target.PosRot, true);
                var invincible = new AIHints.Enemy(world.Actors.Find(invincibleID)!, AIHints.Enemy.PriorityInvincible, false);
                hints.PotentialTargets.Add(invincible);
                hints.Enemies[invincible.Actor.CharacterSpawnIndex] = invincible;
                hints.Normalize();
                Check((int)Call("CountSplashTargets", hints.FindEnemy(target))! == 1, "invincible enemy is not an AoE count");
                strategy.Targeting = Track(Targeting.Auto);
                selected = ((AIHints.Enemy?, int))Call("SelectAOECenterTarget", strategy, invincible)!;
                Check(selected.Item1?.Actor == target && selected.Item2 == 1, "auto targeting replaces invincible target");
                strategy.Thunder = Track(XanBLM.ThunderStrategy.Delay);
                hints.ActionsToExecute.Clear();
                blm.Execute(strategy, invincible.Actor, FrameStep, false);
                Check(hints.ActionsToExecute.Entries.Any(e => e.Target == target && ActionDefinitions.Instance[e.Action]?.IsGCD == true), "ST auto rotation recovers from an invincible selected target");
                strategy.Targeting = Track(Targeting.Manual);
                selected = ((AIHints.Enemy?, int))Call("SelectAOECenterTarget", strategy, invincible)!;
                Check(selected.Item1 == null && selected.Item2 == 0, "manual invincible target does not trigger auto fallback");
                world.Execute(new ActorState.OpDestroy(invincibleID));

                strategy.Leylines = new(XanBLM.LeylinesStrategy.Force, new StrategyValueTrack(), 5000);
                world.Execute(new ClientState.OpPlayerStatsChange(stats));
                blm.Execute(strategy, target, FrameStep, true);
                hints.ActionsToExecute.Clear();
                Call("UseLeylines", strategy, hints.FindEnemy(target));
                Check(!hints.ActionsToExecute.Entries.Any(e => e.Action == ActionID.MakeSpell(BossMod.BLM.AID.LeyLines)), "moving forbids automatic Ley Lines even in Force mode");

                var manualConfig = Service.Config.Get<ActionTweaksConfig>();
                var useManualQueue = manualConfig.UseManualQueue;
                try
                {
                    manualConfig.UseManualQueue = true;
                    var manual = new ManualActionQueueTweak(world, hints);
                    var leyLines = ActionID.MakeSpell(BossMod.BLM.AID.LeyLines);
                    var swiftcast = ActionID.MakeSpell(BossMod.BLM.AID.Swiftcast);
                    bool PushManual(ActionID action) => manual.Push(action, player.InstanceID, 0, false, () => (0, null), () => target.InstanceID);
                    Check(PushManual(leyLines) && PushManual(swiftcast) && PushManual(swiftcast), "manual Ley Lines and emergency action are accepted");
                    manual.Pop(swiftcast);
                    var queue = new ActionQueue();
                    manual.FillQueue(queue);
                    Check(queue.Entries.Any(e => e.Action == leyLines), "manual Ley Lines survives another action's emergency mode");
                    var entries = (System.Collections.IEnumerable)typeof(ManualActionQueueTweak).GetField("_queue", members)!.GetValue(manual)!;
                    foreach (var entry in entries)
                    {
                        var expiry = (DateTime)entry.GetType().GetProperty("ExpireAt")!.GetValue(entry)!;
                        Check(expiry > world.FutureTime(4), "manual Ley Lines survives a cast longer than the old three-second queue");
                    }
                }
                finally
                {
                    manualConfig.UseManualQueue = useManualQueue;
                }
            }
            finally
            {
                world.Execute(new ActorState.OpStatus(player.InstanceID, 0, default));
                world.Execute(new ActorState.OpClassChange(player.InstanceID, Class.BLM, level));
                world.Execute(new ActorState.OpHPMP(player.InstanceID, hpmp));
                world.Execute(new ClientState.OpPlayerStatsChange(stats));
                RefreshHints(hints, [target]);
            }
        }

        private void ValidateBlmPlannerSelection(WorldState world, Actor player, Actor target, RotationModuleManager manager, AIHints hints)
        {
            const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var stMethod = typeof(XanBLM).GetMethod("FindBestPlannerState", members)!;
            var aoeMethod = typeof(XanBLM).GetMethod("FindBestAOEPlannerState", members)!;
            var stats = world.Client.PlayerStats;
            var hpmp = player.HPMP;
            var level = player.Level;
            var samples = new List<double>();
            long allocated = 0;
            using var digest = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var jsonOptions = new System.Text.Json.JsonSerializerOptions
            {
                IncludeFields = true,
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            };
            static Track<T> Track<T>(T value) where T : struct => new(value, new StrategyValueTrack(), 0);
            try
            {
                foreach (var testLevel in new[] { 58, 80, 100 })
                foreach (var targets in new[] { 1, 3, 8 })
                foreach (var element in new[] { -3, -1, 0, 1, 3 })
                foreach (var mp in new[] { 0, 2400, 10000 })
                foreach (var haste in new[] { 100, 85 })
                {
                    world.Execute(new ActorState.OpClassChange(player.InstanceID, Class.BLM, testLevel));
                    world.Execute(new ActorState.OpHPMP(player.InstanceID, new(hpmp.CurHP, hpmp.MaxHP, hpmp.Shield, (uint)mp, hpmp.MaxMP)));
                    world.Execute(new ClientState.OpPlayerStatsChange(new(400, 2000, haste)));
                    RefreshHints(hints, [target]);
                    using var module = new XanBLM(manager, player);
                    var strategy = new XanBLM.Strategy
                    {
                        Targeting = Track(Targeting.Auto),
                        AOE = Track(AOEStrategy.AOE),
                        Thunder = Track(XanBLM.ThunderStrategy.Automatic),
                        Triplecast = Track(XanBLM.TriplecastStrategy.Delay),
                        Manafont = Track(OffensiveStrategy.Delay),
                        Leylines = Track(XanBLM.LeylinesStrategy.Delay),
                    };
                    module.Execute(strategy, target, FrameStep, false);
                    module.Element = element;
                    module.Hearts = element == 0 ? 0 : 3;
                    module.AstralSoul = element > 0 && mp == 0 ? 6 : 0;
                    module.Polyglot = 1;
                    module.Paradox = testLevel >= 90 && element != 0;
                    module.Firestarter = element < 0;
                    module.Thunderhead = true;
                    module.NumAOETargets = targets;
                    var method = targets == 1 ? stMethod : aoeMethod;
                    object?[] arguments = targets == 1 ? [strategy, null, true] : [strategy, null];
                    var cache = (System.Collections.IDictionary)typeof(XanBLM).GetField(targets == 1 ? "STPlannerCache" : "AOEPlannerCache", members)!.GetValue(module)!;
                    cache.Clear();
                    method.Invoke(module, arguments); // Warm the search and its buffers before measuring.
                    cache.Clear();
                    var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    var result = method.Invoke(module, arguments)!;
                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                    allocated += GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
                    samples.Add(elapsed);
                    var json = System.Text.Json.JsonSerializer.Serialize(result, result.GetType(), jsonOptions);
                    digest.AppendData(System.Text.Encoding.UTF8.GetBytes(json));
                    var cached = method.Invoke(module, arguments)!;
                    if (json != System.Text.Json.JsonSerializer.Serialize(cached, cached.GetType(), jsonOptions))
                        _failures.Add($"BLM planner cache mismatch: level={testLevel}, targets={targets}, element={element}, mp={mp}, haste={haste}");
                }
            }
            finally
            {
                world.Execute(new ActorState.OpClassChange(player.InstanceID, Class.BLM, level));
                world.Execute(new ActorState.OpHPMP(player.InstanceID, hpmp));
                world.Execute(new ClientState.OpPlayerStatsChange(stats));
                RefreshHints(hints, [target]);
            }
            samples.Sort();
            Console.WriteLine(FormattableString.Invariant($"blm_planner_cases={samples.Count} hash={Convert.ToHexString(digest.GetHashAndReset())} allocated_bytes={allocated} p50_us={samples[samples.Count / 2]:F3} p95_us={samples[(int)(samples.Count * 0.95)]:F3} p99_us={samples[(int)(samples.Count * 0.99)]:F3}"));
        }

        private WorldState BuildWorld(out Actor player, out Actor target, out DmuActors? dmuActors, out AIHints hints, float prePull = 0)
        {
            var world = new WorldState(TimeSpan.TicksPerSecond, "xan-timeline-harness");
            var dancingMad = _zoneID == DancingMadTimelineZoneID;
            var contentID = dancingMad ? DancingMadContentID : 0;
            world.Execute(new WorldState.OpFrameStart(new(BaseTime.AddSeconds(-prePull), 0, 0, 0, 0, 1), default, default, default));
            world.Execute(new WorldState.OpZoneChange((ushort)_zoneID, (ushort)contentID));

            const ulong playerID = 0x10000001;
            const ulong targetID = TimelineTargetID;
            world.Execute(new ActorState.OpCreate(playerID, 0, 0, 0, "Player", 0, ActorType.Player, _job.Class, (byte)PlayerLevel, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
            world.Execute(new PartyState.OpModify(PartyState.PlayerSlot, new(1, playerID, false)));
            if (PartyBuffFirstCast is { } firstCast)
            {
                CreatePartyMember(world, PartyDrgID, 1, 2, "Party DRG", Class.DRG, new(-2.5f, 0, 2.5f, 0));
                CreatePartyMember(world, PartyMnkID, 2, 3, "Party MNK", Class.MNK, new(2.5f, 0, 2.5f, 0));
                _partyBuffDue = firstCast;
            }
            CreateEnemy(world, targetID, dancingMad ? DmuPrimaryActorOID : 0x1234, 2, dancingMad ? "Kefka P1" : "Target", new(2.5f, 0, 0, MathF.PI), true);
            // a pre-pull countdown starts the player out of combat; Run() puts it in combat at time zero
            world.Execute(new ActorState.OpCombat(playerID, prePull <= 0));
            if (dancingMad)
            {
                CreateEnemy(world, DmuPhase2TargetID, DmuPhase2ActorOID, 4, "Kefka P2", new(2.5f, 0, 2.5f, MathF.PI), false);
                CreateEnemy(world, DmuPhase3KefkaTargetID, DmuPhase3KefkaActorOID, 6, "Kefka P3", new(2.5f, 0, -2.5f, MathF.PI), false);
                CreateEnemy(world, DmuPhase3ChaosTargetID, DmuPhase3ChaosActorOID, 8, "Chaos", new(2.5f, 0, 0, MathF.PI), false);
                CreateEnemy(world, DmuPhase3ExdeathTargetID, DmuPhase3ExdeathActorOID, 10, "Exdeath", new(7.5f, 0, 0, MathF.PI), false);
                CreateEnemy(world, DmuPhase4TargetID, DmuPhase4ActorOID, 12, "Kefka P4", new(2.5f, 0, 0, MathF.PI), false);
                CreateEnemy(world, DmuPhase4NeoExdeathTargetID, DmuPhase4NeoExdeathActorOID, 14, "Neo Exdeath", new(7.5f, 0, 0, MathF.PI), false);
                CreateEnemy(world, DmuPhase4ChaosTargetID, DmuPhase4ChaosActorOID, 16, "Chaos P4", new(-2.5f, 0, 0, MathF.PI), false);
                CreateEnemy(world, DmuPhase5TargetID, DmuPhase5ActorOID, 18, "Kefka P5", new(2.5f, 0, 0, MathF.PI), false);
            }
            world.Execute(new ClientState.OpPlayerStatsChange(new(400, 400, 100)));
            // only when asked: an unconditional restat would move every existing baseline, and the per-frame haste
            // sync in the job combat states carries whatever SkillSpeed it finds here forward unchanged
            if (PlayerSkillSpeed is { } skillSpeed)
                world.Execute(new ClientState.OpPlayerStatsChange(new(skillSpeed, 400, 100)));
            // same rule: stocking the inventory is opt-in, because a held potion lets the Potion track act and moves results
            if (Potions is { } potionCount)
                world.Execute(new ClientState.OpInventoryChange((_job.Class is Class.NIN or Class.VPR or Class.MCH ? ActionDefinitions.IDPotionDex : ActionDefinitions.IDPotionStr).ID, (uint)potionCount));
            world.Execute(new ClientState.OpClassJobLevelsChange(BuildClassJobLevels(PlayerLevel)));
            world.Execute(new ClientState.OpComboChange(default));
            world.Execute(new ClientState.OpCooldown(true, []));

            player = world.Actors.Find(playerID)!;
            target = world.Actors.Find(targetID)!;
            _partyDrg = world.Actors.Find(PartyDrgID);
            _partyMnk = world.Actors.Find(PartyMnkID);
            dmuActors = dancingMad
                ? new(target, world.Actors.Find(DmuPhase2TargetID)!, world.Actors.Find(DmuPhase3KefkaTargetID)!, world.Actors.Find(DmuPhase3ChaosTargetID)!, world.Actors.Find(DmuPhase3ExdeathTargetID)!, world.Actors.Find(DmuPhase4TargetID)!, world.Actors.Find(DmuPhase4NeoExdeathTargetID)!, world.Actors.Find(DmuPhase4ChaosTargetID)!, world.Actors.Find(DmuPhase5TargetID)!)
                : null;
            // extra enemies are packed inside the 5y self-AoE radius and inside melee range of the player, so
            // NumAOETargets rises with --extra-targets and the AoE breakpoints actually get evaluated
            if (dmuActors == null && ExtraTargets > 0)
            {
                ReadOnlySpan<Vector4> extraSpots =
                [
                    new(2.5f, 0, 2.0f, MathF.PI), new(2.5f, 0, -2.0f, MathF.PI), new(4.0f, 0, 0, MathF.PI),
                    new(1.0f, 0, 2.5f, MathF.PI), new(1.0f, 0, -2.5f, MathF.PI), new(4.0f, 0, 2.0f, MathF.PI),
                    new(4.0f, 0, -2.0f, MathF.PI), new(0.5f, 0, 3.0f, MathF.PI)
                ];
                for (var i = 0; i < ExtraTargets; ++i)
                {
                    var id = TimelineTargetID + (ulong)i + 1;
                    CreateEnemy(world, id, 0x1234, 30 + i * 2, FormattableString.Invariant($"Add {i + 1}"), extraSpots[i], true);
                    _extraTargets.Add(world.Actors.Find(id)!);
                }
            }

            if (dmuActors != null)
            {
                foreach (var enemy in dmuActors.All)
                    _enemyTargetIDs.Add(enemy.InstanceID);
            }
            else
            {
                _enemyTargetIDs.Add(target.InstanceID);
                foreach (var extra in _extraTargets)
                    _enemyTargetIDs.Add(extra.InstanceID);
            }
            hints = new AIHints();
            RefreshHints(hints, new[] { target });
            var buffTarget = player;
            if (_job.Class == Class.RPR)
            {
                _rprCombat = new(world, player, FrameStep, (action, executedTargetID, gcd, potency) =>
                {
                    var available = _enemyTargetIDs.Any(id => world.Actors.Find(id) is { IsTargetable: true, IsDead: false });
                    var raidBuffed = buffTarget.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || buffTarget.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
                    _actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, gcd, available, executedTargetID, potency, raidBuffed));
                });
            }
            else if (_job.Class == Class.BLM)
            {
                _blmCombat = new(world, player, FrameStep, (action, executedTargetID, gcd, potency) =>
                {
                    var available = _enemyTargetIDs.Any(id => world.Actors.Find(id) is { IsTargetable: true, IsDead: false });
                    // same raid-buff flag the other jobs record; always false unless --party-buffs puts Litany / Brotherhood up
                    var raidBuffed = buffTarget.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || buffTarget.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
                    _actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, gcd, available, executedTargetID, potency, raidBuffed));
                })
                {
                    DecisionLog = BlmDecisionLog,
                    Force = BlmForce,
                    ForceFollowups = BlmForceFollowups,
                };
            }
            else if (_job.Class == Class.MNK)
            {
                _mnkCombat = new(world, player, FrameStep, (action, executedTargetID, gcd, potency) =>
                {
                    var available = _enemyTargetIDs.Any(id => world.Actors.Find(id) is { IsTargetable: true, IsDead: false });
                    _actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, gcd, available, executedTargetID, potency));
                });
            }
            else if (_job.Class == Class.GNB)
            {
                _gnbCombat = new(world, player, FrameStep, (action, executedTargetID, gcd, potency) =>
                {
                    var available = _enemyTargetIDs.Any(id => world.Actors.Find(id) is { IsTargetable: true, IsDead: false });
                    var raidBuffed = buffTarget.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || buffTarget.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
                    _actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, gcd, available, executedTargetID, potency, raidBuffed));
                });
            }
            else if (_job.Class == Class.NIN)
            {
                _ninCombat = new(world, player, FrameStep, (action, executedTargetID, gcd, potency) =>
                {
                    var available = _enemyTargetIDs.Any(id => world.Actors.Find(id) is { IsTargetable: true, IsDead: false });
                    var raidBuffed = buffTarget.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || buffTarget.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
                    _actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, gcd, available, executedTargetID, potency, raidBuffed));
                })
                {
                    BaseTime = BaseTime
                };
                _ninCombat.Initialize();
            }
            else if (_job.Class == Class.MCH)
            {
                _mchCombat = new(world, player, FrameStep, (action, executedTargetID, gcd, potency) =>
                {
                    var available = _enemyTargetIDs.Any(id => world.Actors.Find(id) is { IsTargetable: true, IsDead: false });
                    var raidBuffed = buffTarget.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || buffTarget.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
                    _actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, gcd, available, executedTargetID, potency, raidBuffed));
                })
                {
                    BaseTime = BaseTime
                };
            }
            else if (_job.Class == Class.VPR)
            {
                _vprCombat = new(world, player, FrameStep, (action, executedTargetID, gcd, potency) =>
                {
                    var available = _enemyTargetIDs.Any(id => world.Actors.Find(id) is { IsTargetable: true, IsDead: false });
                    var raidBuffed = buffTarget.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || buffTarget.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
                    _actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, gcd, available, executedTargetID, potency, raidBuffed));
                })
                {
                    BaseTime = BaseTime
                };
            }
            else if (_job.Class == Class.SAM)
            {
                _samCombat = new(world, player, FrameStep, (action, executedTargetID, gcd, potency) =>
                {
                    var available = _enemyTargetIDs.Any(id => world.Actors.Find(id) is { IsTargetable: true, IsDead: false });
                    var raidBuffed = buffTarget.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || buffTarget.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
                    _actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, gcd, available, executedTargetID, potency, raidBuffed));
                })
                {
                    BaseTime = BaseTime
                };
            }
            else if (_job.Class == Class.PLD)
            {
                _pldCombat = new(world, player, FrameStep, (action, executedTargetID, gcd, potency) =>
                {
                    var available = _enemyTargetIDs.Any(id => world.Actors.Find(id) is { IsTargetable: true, IsDead: false });
                    var raidBuffed = buffTarget.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || buffTarget.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
                    _actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, gcd, available, executedTargetID, potency, raidBuffed));
                })
                {
                    BaseTime = BaseTime
                };
            }
            else if (_job.Class == Class.DRG)
            {
                _drgCombat = new(world, player, FrameStep, (action, executedTargetID, gcd, potency) =>
                {
                    var available = _enemyTargetIDs.Any(id => world.Actors.Find(id) is { IsTargetable: true, IsDead: false });
                    var raidBuffed = buffTarget.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null || buffTarget.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null;
                    _actions.Add(new((float)(world.CurrentTime - BaseTime).TotalSeconds, action, gcd, available, executedTargetID, potency, raidBuffed));
                })
                {
                    BaseTime = BaseTime
                };
            }
            return world;
        }

        private BossModule? CreateDmuModule(WorldState world, BossModuleManager bossmods, Actor target, DmuActors? dmuActors, RotationModuleManager manager)
        {
            if (dmuActors == null)
                return null;

            var dmu = BossModuleRegistry.CreateModuleForActor(world, target);
            if (dmu == null)
            {
                _failures.Add("DMU BossModule could not be constructed through BossModuleRegistry");
                return null;
            }

            bossmods.ActiveModule = dmu;
            manager.Planner = new PlanExecution(dmu, null);
            dmu.Update();
            return dmu;
        }

        private IReadOnlyList<Actor> UpdateGenericTimeline(WorldState world, Actor target, float time, bool irregularLoss = false)
        {
            // A window clamped to the scenario end (long-downtime "loss" slices) is still open on the final frame: `Contains` is half-open, so
            // without this the boss became targetable for exactly one 0.05s frame at time == duration and gifted a terminal instant GCD.
            // An irregular loss episode (--irregular) hides the target the same way, but is not part of the windows the hints announce.
            var targetAvailable = !irregularLoss && !_targetUnavailableWindows.Any(window => window.Contains(time) || (time >= window.Start && window.End >= _duration - FrameStep * 0.5f));
            SetTargetable(world, target, targetAvailable);
            foreach (var extra in _extraTargets)
                SetTargetable(world, extra, targetAvailable);
            if (!targetAvailable)
                return [];
            if (_extraTargets.Count == 0)
                return new[] { target };
            var all = new List<Actor>(_extraTargets.Count + 1) { target };
            all.AddRange(_extraTargets);
            return all;
        }

        private static IReadOnlyList<Actor> UpdateDmuTimeline(WorldState world, DmuActors actors, DmuPhaseSchedule schedule, float time)
        {
            if (time < schedule.P1TargetLossStart)
            {
                SetTargetable(world, actors.Phase1, true);
                return [actors.Phase1];
            }

            SetTargetable(world, actors.Phase1, false);
            if (time < schedule.P2Start)
                return [];

            if (time < schedule.P2End)
            {
                SetTargetable(world, actors.Phase2, true);
                return [actors.Phase2];
            }

            SetTargetable(world, actors.Phase2, false);
            SetHPRatioBelowFull(world, actors.Phase2);
            if (time < schedule.P3Start)
                return [];

            if (time < schedule.P4Start)
            {
                SetTargetable(world, actors.Phase3Chaos, true);
                SetTargetable(world, actors.Phase3Exdeath, true);
                return [actors.Phase3Chaos, actors.Phase3Exdeath];
            }

            SetTargetable(world, actors.Phase3Chaos, false);
            SetTargetable(world, actors.Phase3Exdeath, false);
            SetDead(world, actors.Phase3Chaos, true);
            SetDead(world, actors.Phase3Exdeath, true);
            if (time < schedule.P5Start)
            {
                SetTargetable(world, actors.Phase4, true);
                return [actors.Phase4];
            }

            SetTargetable(world, actors.Phase4, false);
            SetDead(world, actors.Phase4, true);
            SetTargetable(world, actors.Phase5, true);
            return [actors.Phase5];
        }

        private static short[] BuildClassJobLevels(int level)
        {
            var levels = new short[ClientState.NumClassLevels];
            Array.Fill(levels, (short)level);
            return levels;
        }

        private static void CreatePartyMember(WorldState world, ulong instanceID, int partySlot, ulong contentID, string name, Class clasz, Vector4 posRot)
        {
            world.Execute(new ActorState.OpCreate(instanceID, 0, 0, 0, name, 0, ActorType.Player, clasz, 100, posRot, 0.5f, new(100000, 100000, 0, 10000, 10000), false, true, default, default, 0));
            world.Execute(new PartyState.OpModify(partySlot, new(contentID, instanceID, false)));
            world.Execute(new ActorState.OpCombat(instanceID, true));
        }

        private static void CreateEnemy(WorldState world, ulong instanceID, uint oid, int spawnIndex, string name, Vector4 posRot, bool targetable)
        {
            world.Execute(new ActorState.OpCreate(instanceID, oid, spawnIndex, 0, name, 0, ActorType.Enemy, Class.None, 100, posRot, 2.0f, new(1000000, 1000000, 0, 10000, 10000), targetable, false, default, default, 0));
            world.Execute(new ActorState.OpCombat(instanceID, true));
        }

        private static void RefreshHints(AIHints hints, IReadOnlyList<Actor> targets)
        {
            hints.Clear();
            foreach (var target in targets)
            {
                var enemy = new AIHints.Enemy(target, 1, false);
                if ((uint)target.CharacterSpawnIndex < AIHints.NumEnemies)
                    hints.Enemies[target.CharacterSpawnIndex] = enemy;
                hints.PotentialTargets.Add(enemy);
            }
            if (targets.Count > 0)
                hints.HighestPotentialTargetPriority = 1;
        }

        private static void SetTargetable(WorldState world, Actor target, bool targetable)
        {
            if (target.IsTargetable != targetable)
                world.Execute(new ActorState.OpTargetable(target.InstanceID, targetable));
        }

        private static void SetHPRatioBelowFull(WorldState world, Actor target)
        {
            if (target.HPMP.CurHP > 1)
                world.Execute(new ActorState.OpHPMP(target.InstanceID, new(1, target.HPMP.MaxHP, target.HPMP.Shield, target.HPMP.CurMP, target.HPMP.MaxMP)));
        }

        private static void SetDead(WorldState world, Actor target, bool dead)
        {
            if (target.IsDead != dead)
                world.Execute(new ActorState.OpDead(target.InstanceID, dead));
        }

        public static float? CountdownSeconds;

        private static void AdvanceFrameExplicit(WorldState world, float time, ulong index, float elapsed)
            => world.Execute(new WorldState.OpFrameStart(new(BaseTime.AddSeconds(time), index, (uint)index, elapsed, elapsed, 1), TimeSpan.FromSeconds(elapsed), world.Client.GaugePayload, default));

        private static void AdvanceFrame(WorldState world, float time, int frame)
        {
            var elapsed = frame == 0 ? 0 : FrameStep;
            world.Execute(new WorldState.OpFrameStart(new(BaseTime.AddSeconds(time), (ulong)frame, (uint)frame, elapsed, elapsed, 1), TimeSpan.FromSeconds(elapsed), world.Client.GaugePayload, default));
        }

        private BlmSample SampleBlm(WorldState world, float time, bool targetAvailable)
        {
            for (; _blmSeriesIndex < _actions.Count; ++_blmSeriesIndex)
            {
                var action = _actions[_blmSeriesIndex];
                _blmSeriesPotency += action.Potency;
                if (action.RaidBuffed)
                    _blmSeriesBuffed += action.Potency;
            }
            float ReadyIn(BossMod.BLM.AID aid) => ActionDefinitions.Instance[ActionID.MakeSpell(aid)]!.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions);
            var blm = _blmCombat!;
            return new(time, _blmSeriesPotency + blm.DotPotency, _blmSeriesBuffed, targetAvailable, blm.Element, blm.MP, blm.Hearts, blm.Polyglot, blm.AstralSoul, blm.Paradox, blm.Firestarter,
                blm.Thunderhead, blm.InstantCast, blm.LeyLinesActive, ReadyIn(BossMod.BLM.AID.Manafont), ReadyIn(BossMod.BLM.AID.Triplecast), ReadyIn(BossMod.BLM.AID.Swiftcast), ReadyIn(BossMod.BLM.AID.Amplifier));
        }

        private readonly List<ActionID> _burstNew = [];

        private string BurstStateText()
            => _rprCombat?.BurstState() ?? _blmCombat?.BurstState() ?? _mnkCombat?.BurstState() ?? _gnbCombat?.BurstState() ?? _drgCombat?.BurstState() ?? _samCombat?.BurstState()
            ?? _vprCombat?.BurstState() ?? _mchCombat?.BurstState() ?? _pldCombat?.BurstState() ?? _ninCombat?.BurstState() ?? "";

        private bool BurstFeasible(ActionQueue.Entry entry)
            => _rprCombat?.BurstFeasible(entry) ?? _blmCombat?.BurstFeasible(entry) ?? _mnkCombat?.BurstFeasible(entry) ?? _gnbCombat?.BurstFeasible(entry) ?? _drgCombat?.BurstFeasible(entry) ?? _samCombat?.BurstFeasible(entry)
            ?? _vprCombat?.BurstFeasible(entry) ?? _mchCombat?.BurstFeasible(entry) ?? _pldCombat?.BurstFeasible(entry) ?? _ninCombat?.BurstFeasible(entry) ?? true;

        private void ExecuteBestAction(WorldState world, Actor player, AIHints hints, bool targetAvailable, float time)
        {
            if (_rprCombat != null)
            {
                _rprCombat.ExecuteBestAction(hints);
                return;
            }
            if (_blmCombat != null)
            {
                _blmCombat.ExecuteBestAction(hints);
                return;
            }
            if (_mnkCombat != null)
            {
                _mnkCombat.ExecuteBestAction(hints);
                return;
            }
            if (_gnbCombat != null)
            {
                _gnbCombat.ExecuteBestAction(hints);
                return;
            }
            if (_drgCombat != null)
            {
                _drgCombat.ExecuteBestAction(hints);
                return;
            }
            if (_samCombat != null)
            {
                _samCombat.ExecuteBestAction(hints);
                return;
            }
            if (_vprCombat != null)
            {
                _vprCombat.ExecuteBestAction(hints);
                return;
            }
            if (_mchCombat != null)
            {
                _mchCombat.ExecuteBestAction(hints);
                return;
            }
            if (_pldCombat != null)
            {
                _pldCombat.ExecuteBestAction(hints);
                return;
            }
            if (_ninCombat != null)
            {
                _ninCombat.ExecuteBestAction(hints);
                return;
            }

            var entry = hints.ActionsToExecute.FindBest(world, player, world.Client.Cooldowns, world.Client.AnimationLock, hints, instantAnimLockDelay: FrameStep, allowDismount: true);
            if (entry.Action.ID == 0)
                return;

            var definition = ActionDefinitions.Instance[entry.Action];
            if (definition == null)
                return;

            var readyIn = definition.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions);
            var startDelay = MathF.Max(entry.Delay, MathF.Max(world.Client.AnimationLock, readyIn));
            if (startDelay > FrameStep * 0.5f)
                return;

            var gcd = definition.IsGCD;
            if (gcd)
            {
                world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new Cooldown(0, DefaultGCD))]));
                if (entry.Action.Type == ActionType.Spell)
                    world.Execute(new ClientState.OpComboChange(new(entry.Action.ID, 30)));
            }
            else
            {
                StartActionCooldown(world, player, definition);
            }

            world.Execute(new ClientState.OpAnimationLockChange(definition.InstantAnimLock));
            _actions.Add(new(time, entry.Action, gcd, targetAvailable, entry.Target?.InstanceID ?? 0));
            if (!targetAvailable && entry.Target != null && _enemyTargetIDs.Contains(entry.Target.InstanceID))
                _failures.Add($"{time:f2}: queued enemy-target action {entry.Action} while target was unavailable");
        }

        private static void StartActionCooldown(WorldState world, Actor player, ActionDefinition definition)
        {
            var group = definition.ActualMainCooldownGroup(world.Client.DutyActions);
            if (group < 0 || group == ActionDefinitions.GCDGroup || definition.Cooldown <= 0)
                return;

            var maxCharges = Math.Max(1, definition.MaxChargesAtCap());
            var levelCharges = Math.Clamp(definition.MaxChargesAtLevel(player.Level), 1, maxCharges);
            var current = world.Client.Cooldowns[group];
            var elapsed = current.Total > 0 ? MathF.Max(0, current.Elapsed - definition.Cooldown) : definition.Cooldown * (levelCharges - 1);
            world.Execute(new ClientState.OpCooldown(false, [(group, new Cooldown(elapsed, definition.Cooldown * maxCharges))]));
        }

        private void CheckRequirements(BossModule? dmu, DmuTimelineActionDriver? dmuActionDriver, int dmuPredictedDamageFrames, int riddleOfEarthActions)
        {
            if (_targetUnavailableWindows.Count > 0 && _actions.Any(action => !action.TargetAvailable && _enemyTargetIDs.Contains(action.TargetID)))
                _failures.Add("executed an explicitly targeted action during an Event Trigger target-unavailable window");

            foreach (var returnTime in _targetReturnTimes)
            {
                if (returnTime + ResumeGCDLimit > _duration)
                    continue;
                if (!_actions.Any(action => action.GCD && action.TargetAvailable && action.Time >= returnTime && action.Time <= returnTime + ResumeGCDLimit))
                    _failures.Add($"no GCD action within {ResumeGCDLimit:f2}s after target return at {returnTime:f2}");
            }

            if (dmu != null && _dmuSchedule != null)
            {
                if (dmuActionDriver == null || dmuActionDriver.Emitted == 0)
                    _failures.Add("DMU did not receive any deterministic Event Trigger action events");
                if (_job.Class == Class.MNK && PlayerLevel >= 64 && dmuPredictedDamageFrames > 0 && riddleOfEarthActions == 0)
                    _failures.Add("MNK did not use Riddle of Earth despite DMU predicted damage under the default Automatic strategy");

                var expectedPhase = _duration >= _dmuSchedule.P5Start + FrameStep ? 4
                    : _duration >= _dmuSchedule.P4Start + FrameStep ? 3
                    : _duration >= _dmuSchedule.P2End + FrameStep ? 2
                    : _duration >= _dmuSchedule.P2Start + FrameStep ? 1
                    : 0;
                if (dmu.StateMachine.ActivePhaseIndex != expectedPhase)
                    _failures.Add($"DMU StateMachine active phase was {dmu.StateMachine.ActivePhaseIndex}, expected {expectedPhase} from the Event Trigger phase schedule");
            }
        }

        // Replays the Event Trigger timeline's actions into the world so the real DMU module can follow the fight. The timeline lists most
        // boss casts only by their resolving Ability line, while the module's ActorCast states wait for the cast to start: for such lines a
        // cast start is synthesized at (ability time - sheet cast time) on the same caster, unless a StartsUsing line already covers it or
        // the caster is still busy with an earlier cast.
        private sealed class DmuTimelineActionDriver
        {
            private enum Caster { None, Phase1, Phase2, Phase3Chaos, Phase3Exdeath, Phase4, Phase4NeoExdeath, Phase4Chaos, Phase5 }

            private readonly record struct DriverEvent(float Time, EventTriggerTimelineActionKind Kind, uint ActionID, Caster Caster, float FinishTime, bool Synthesized);

            private readonly List<DriverEvent> _events;
            private int _nextEvent;

            public int Emitted { get; private set; }
            public int Skipped { get; private set; }

            public DmuTimelineActionDriver(IReadOnlyList<EventTriggerTimelineAction> actions, DmuPhaseSchedule schedule)
            {
                List<DriverEvent> events = [];
                HashSet<(uint, string, float)> coveredAbilities = [];
                Dictionary<Caster, float> busyUntil = [];
                for (var index = 0; index < actions.Count; ++index)
                {
                    var entry = actions[index];
                    var caster = entry.ActionIDs.Count == 1 && entry.Sources.Count == 1 ? ResolveCaster(schedule, entry.Sources[0], entry.Time) : Caster.None;
                    if (caster == Caster.None)
                    {
                        events.Add(new(entry.Time, entry.Kind, 0, Caster.None, 0, false));
                        continue;
                    }

                    if (entry.Kind == EventTriggerTimelineActionKind.StartsUsing)
                    {
                        var finishTime = FindFinishTime(actions, index, entry);
                        if (finishTime > entry.Time)
                        {
                            coveredAbilities.Add((entry.ActionIDs[0], entry.Sources[0], finishTime));
                            busyUntil[caster] = Math.Max(busyUntil.GetValueOrDefault(caster), finishTime);
                        }
                        events.Add(new(entry.Time, entry.Kind, entry.ActionIDs[0], caster, finishTime, false));
                        continue;
                    }

                    if (!coveredAbilities.Contains((entry.ActionIDs[0], entry.Sources[0], entry.Time))
                        && Service.LuminaRow<Lumina.Excel.Sheets.Action>(entry.ActionIDs[0]) is { } row
                        && (row.Cast100ms + row.ExtraCastTime100ms) * 0.1f is var castTime && castTime > 0)
                    {
                        var start = Math.Max(0, entry.Time - castTime);
                        if (start >= busyUntil.GetValueOrDefault(caster) - 0.0001f && entry.Time > start)
                        {
                            busyUntil[caster] = entry.Time;
                            events.Add(new(start, EventTriggerTimelineActionKind.StartsUsing, entry.ActionIDs[0], caster, entry.Time, true));
                        }
                    }
                    events.Add(new(entry.Time, entry.Kind, entry.ActionIDs[0], caster, 0, false));
                }

                // stable, so a synthesized start stays ahead of anything listed at the same time after it
                _events = [.. events.Select((e, i) => (e, i)).OrderBy(p => p.e.Time).ThenBy(p => p.i).Select(p => p.e)];
            }

            public void EmitThrough(WorldState world, Actor player, DmuActors actors, float time)
            {
                while (_nextEvent < _events.Count && _events[_nextEvent].Time <= time + 0.0001f)
                {
                    var entry = _events[_nextEvent++];
                    if (entry.Caster == Caster.None || entry.Kind == EventTriggerTimelineActionKind.StartsUsing && entry.FinishTime <= entry.Time)
                    {
                        ++Skipped;
                        continue;
                    }

                    var caster = ActorFor(actors, entry.Caster);
                    var action = new ActionID(ActionType.Spell, entry.ActionID);
                    if (entry.Kind == EventTriggerTimelineActionKind.StartsUsing)
                    {
                        world.Execute(new ActorState.OpCastInfo(caster.InstanceID, new()
                        {
                            Action = action,
                            TargetID = player.InstanceID,
                            Rotation = caster.Rotation,
                            Location = player.PosRot.XYZ(),
                            ElapsedTime = 0,
                            TotalTime = entry.FinishTime - entry.Time
                        }));
                    }
                    else
                    {
                        world.Execute(new ActorState.OpCastEvent(caster.InstanceID, new(action, player.InstanceID, 0, 0, caster.PosRot.XYZ(), 0, 0, caster.Rotation)));
                        if (caster.CastInfo?.Action == action)
                            world.Execute(new ActorState.OpCastInfo(caster.InstanceID, null));
                    }

                    if (!entry.Synthesized)
                        ++Emitted;
                }
            }

            private static float FindFinishTime(IReadOnlyList<EventTriggerTimelineAction> actions, int startIndex, EventTriggerTimelineAction started)
            {
                for (var index = startIndex + 1; index < actions.Count; ++index)
                {
                    var candidate = actions[index];
                    if (candidate.Kind == EventTriggerTimelineActionKind.Ability
                        && candidate.ActionIDs.Count == 1
                        && candidate.Sources.Count == 1
                        && candidate.ActionIDs[0] == started.ActionIDs[0]
                        && candidate.Sources[0].Equals(started.Sources[0], StringComparison.Ordinal))
                        return candidate.Time;
                }

                return 0;
            }

            // P3 "Kefka" is what the module reads as BossP3(): OID.Kefka (0x4C30), the P1 actor, not the 0x4BFB spawn
            private static Caster ResolveCaster(DmuPhaseSchedule schedule, string source, float time) => source switch
            {
                "Kefka" when time < schedule.P2Start => Caster.Phase1,
                "Kefka" when time < schedule.P2End => Caster.Phase2,
                "Kefka" when time < schedule.P4Start => Caster.Phase1,
                "Kefka" when time < schedule.P5Start => Caster.Phase4,
                "Kefka" => Caster.Phase5,
                "Chaos" when time < schedule.P4Start => Caster.Phase3Chaos,
                "Chaos" => Caster.Phase4Chaos,
                "Exdeath" => Caster.Phase3Exdeath,
                "Neo Exdeath" => Caster.Phase4NeoExdeath,
                _ => Caster.None
            };

            private static Actor ActorFor(DmuActors actors, Caster caster) => caster switch
            {
                Caster.Phase1 => actors.Phase1,
                Caster.Phase2 => actors.Phase2,
                Caster.Phase3Chaos => actors.Phase3Chaos,
                Caster.Phase3Exdeath => actors.Phase3Exdeath,
                Caster.Phase4 => actors.Phase4,
                Caster.Phase4NeoExdeath => actors.Phase4NeoExdeath,
                Caster.Phase4Chaos => actors.Phase4Chaos,
                Caster.Phase5 => actors.Phase5,
                _ => throw new ArgumentOutOfRangeException(nameof(caster))
            };
        }

        private sealed record DmuActors(Actor Phase1, Actor Phase2, Actor Phase3Kefka, Actor Phase3Chaos, Actor Phase3Exdeath, Actor Phase4, Actor Phase4NeoExdeath, Actor Phase4Chaos, Actor Phase5)
        {
            public IReadOnlyList<Actor> All => [Phase1, Phase2, Phase3Kefka, Phase3Chaos, Phase3Exdeath, Phase4, Phase4NeoExdeath, Phase4Chaos, Phase5];
        }
    }

    private sealed record HarnessOptions(string? TimelineRoot, string? Sqpack, string JobSelector, int ZoneID, float? Duration, int? ScenarioLimit, float? TargetLossHintLead, string? ScenarioFilter, string? BlmRotation, float? PartyBuffFirstCast, float? ExtraPostRoll, float? CountdownSeconds, int? RandomDisengageSeed, bool DisengageForecastEnabled, bool StartSoulsow, int PlayerLevel, int ExtraTargets, string? MnkEncounterHint, IReadOnlyList<(string Track, string Option)> TrackOverrides, int Repeat = 1, int? SkillSpeed = null, int? Potions = null)
    {
        // --irregular family (see IrregularDriver); the *-lists are only read by irregular-compare
        public int? IrregularSeed { get; init; }
        public float IrregularRate { get; init; } = 2f;
        public string IrregularKinds { get; init; } = "all";
        public string? IrregularSeeds { get; init; }
        public string? IrregularRates { get; init; }
        public string? IrregularKindSets { get; init; }
        public string? IrregularOut { get; init; }
        // oracle-search options (--oracle-*), see OracleSearch.cs
        public Dictionary<string, string> OracleArgs { get; init; } = [];

        public static HarnessOptions Parse(string[] args)
        {
            int? irregularSeed = null;
            var irregularRate = 2f;
            var irregularKinds = "all";
            string? irregularSeeds = null, irregularRates = null, irregularKindSets = null, irregularOut = null;
            Dictionary<string, string> oracleArgs = [];
            Dictionary<string, string> burstArgs = [];
            string? timelineRoot = null;
            string? sqpack = null;
            var jobSelector = "all";
            var zoneID = DancingMadTimelineZoneID;
            float? duration = null;
            int? scenarioLimit = null;
            var repeat = 1;
            float? targetLossHintLead = null;
            var playerLevel = 100;
            int? skillSpeed = null;
            int? potions = null;
            var extraTargets = 0;
            string? mnkEncounterHint = null;
            List<(string Track, string Option)> trackOverrides = [];
            string? scenarioFilter = null;
            string? blmRotation = null;
            float? partyBuffFirstCast = null;
            float? extraPostRoll = null;
            float? countdownSeconds = null;
            int? randomDisengageSeed = null;
            var disengageForecast = true;
            var startSoulsow = false;

            for (var index = 0; index < args.Length; ++index)
            {
                var option = args[index];
                if (index + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for '{option}'.");
                var value = args[++index];
                switch (option)
                {
                    case "--timeline-root":
                        timelineRoot = value;
                        break;
                    case "--sqpack":
                        sqpack = value;
                        break;
                    case "--job":
                        jobSelector = value;
                        break;
                    case "--zone":
                        zoneID = int.Parse(value);
                        break;
                    case "--duration":
                        duration = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (duration <= 0)
                            throw new ArgumentOutOfRangeException(nameof(args), "--duration must be positive.");
                        break;
                    case "--repeat":
                        // run each scenario this many times in a row: one process, one zone, repeated pulls of the
                        // same fight, which is what AkechiGNB's learned target-loss windows are meant to learn from
                        repeat = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (repeat <= 0)
                            throw new ArgumentOutOfRangeException(nameof(args), "--repeat must be positive.");
                        break;
                    case "--scenario-limit":
                        scenarioLimit = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (scenarioLimit <= 0)
                            throw new ArgumentOutOfRangeException(nameof(args), "--scenario-limit must be positive.");
                        break;
                    case "--scenario-filter":
                        scenarioFilter = value;
                        break;
                    case "--blm-rotation":
                        // BLM "Rotation mode" track option by internal name (Automatic, FuturePlanner, WindurstThirdWalk, PolyglotOvercapOnly).
                        blmRotation = value;
                        break;
                    case "--mnk-hint":
                        // MNK EncounterHint track option by internal name (Boss, Trash, AllianceTrash, MajorAdd, ForceBurst, ...)
                        mnkEncounterHint = value;
                        break;
                    case "--track":
                        // <TrackInternalName>=<OptionInternalName>, repeatable: pins any UI-selectable strategy track, so a
                        // setting a player can pick (RoF=Delay, PB=Force) can be measured instead of only the module default.
                        // Applied after --blm-rotation / --mnk-hint, which keep working for the runs that already use them.
                        var separator = value.IndexOf('=');
                        if (separator <= 0 || separator == value.Length - 1)
                            throw new ArgumentException("--track must be <TrackInternalName>=<OptionInternalName>.");
                        trackOverrides.Add((value[..separator], value[(separator + 1)..]));
                        break;
                    case "--extra-targets":
                        // additional enemies packed around the primary target so NumAOETargets rises above 1
                        extraTargets = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (extraTargets is < 0 or > 8)
                            throw new ArgumentOutOfRangeException(nameof(args), "--extra-targets must be 0..8.");
                        break;
                    case "--skill-speed":
                        // the harness default leaves every job at the 2.50s baseline GCD, but a Lv100 BiS melee is geared
                        // past that: the Riddle of Fire window fits a different number of GCDs at 1.94s than at 2.00s
                        skillSpeed = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (skillSpeed <= 0)
                            throw new ArgumentOutOfRangeException(nameof(args), "--skill-speed must be positive.");
                        break;
                    case "--potions":
                        // the simulated player starts with an empty inventory, so every job's Potion track is a no-op by
                        // default: with nothing held the rotation asks for a potion and the use is silently dropped
                        potions = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (potions < 0)
                            throw new ArgumentOutOfRangeException(nameof(args), "--potions must be non-negative.");
                        break;
                    case "--level":
                        // sync the simulated player to this level, so the Unlocked() gating in the rotation is exercised
                        playerLevel = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (playerLevel is < 1 or > 100)
                            throw new ArgumentOutOfRangeException(nameof(args), "--level must be 1..100.");
                        break;
                    case "--target-loss-hints":
                        // Lead time (seconds) at which the harness starts announcing an upcoming target-unavailable window to the
                        // module as an external mechanic hint (TargetLossIn / TargetReturnIn), the way a boss module or timeline would.
                        targetLossHintLead = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (targetLossHintLead <= 0)
                            throw new ArgumentOutOfRangeException(nameof(args), "--target-loss-hints must be positive.");
                        break;
                    case "--party-buffs":
                        // Time (seconds) of the first Battle Litany / Brotherhood cast by the simulated DRG and MNK party members; repeats every 120s.
                        partyBuffFirstCast = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (partyBuffFirstCast <= 0)
                            throw new ArgumentOutOfRangeException(nameof(args), "--party-buffs must be positive.");
                        break;
                    case "--countdown":
                        // rpr-potion natural fights: start out of combat with this countdown so the module pre-pulls (Soulsow, Harpe).
                        countdownSeconds = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (countdownSeconds <= 0)
                            throw new ArgumentOutOfRangeException(nameof(args), "--countdown must be positive.");
                        break;
                    case "--post-roll":
                        // Extra uptime (seconds) appended to every generated matrix scenario after its last target-return window.
                        extraPostRoll = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (extraPostRoll < 0 || float.IsNaN(extraPostRoll.Value))
                            throw new ArgumentOutOfRangeException(nameof(args), "--post-roll must be zero or positive.");
                        break;
                    case "--random-disengage":
                        // Seed for random telegraphed mechanics that are not on the timeline (chariots, baited puddles, far stacks). The player
                        // dodges them as late as possible, and the combat emulators enforce attack range and cast interruption by movement.
                        randomDisengageSeed = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--start-soulsow":
                        // on: RPR scenarios begin with Soulsow applied, so Harvest Moon is available as a ranged filler.
                        startSoulsow = value == "on" ? true : value == "off" ? false : throw new ArgumentException("--start-soulsow must be on or off.");
                        break;
                    case "--disengage-forecast":
                        // on (default) or off: whether DisengageForecaster turns those mechanics into hints for the rotation.
                        disengageForecast = value switch
                        {
                            "on" => true,
                            "off" => false,
                            _ => throw new ArgumentException("--disengage-forecast must be on or off.")
                        };
                        break;
                    case "--irregular":
                        // Seed for unpredicted lockouts / line-of-sight blocks / knockback holds / target loss (IrregularDriver); independent of --random-disengage.
                        irregularSeed = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    case "--irregular-rate":
                        irregularRate = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                        if (irregularRate <= 0)
                            throw new ArgumentOutOfRangeException(nameof(args), "--irregular-rate must be positive.");
                        break;
                    case "--irregular-kinds":
                        irregularKinds = value;
                        break;
                    case "--irregular-seeds":
                        irregularSeeds = value; // irregular-compare: comma-separated seeds
                        break;
                    case "--irregular-rates":
                        irregularRates = value; // irregular-compare: comma-separated events per minute
                        break;
                    case "--irregular-kind-sets":
                        irregularKindSets = value; // irregular-compare: semicolon-separated kind lists, e.g. lockout;los;range;loss;all
                        break;
                    case "--irregular-out":
                        irregularOut = value; // irregular-compare: per-scenario and per-configuration CSV
                        break;
                    default:
                        if (option.StartsWith("--oracle-", StringComparison.Ordinal))
                        {
                            oracleArgs[option] = value;
                            break;
                        }
                        if (option.StartsWith("--burst-", StringComparison.Ordinal))
                        {
                            burstArgs[option] = value;
                            break;
                        }
                        throw new ArgumentException($"Unknown option '{option}'.");
                }
            }

            BurstControl.Configure(burstArgs); // --burst-*: the burst-on-recast study, see BurstControl.cs
            return new(timelineRoot, sqpack, jobSelector, zoneID, duration, scenarioLimit, targetLossHintLead, scenarioFilter, blmRotation, partyBuffFirstCast, extraPostRoll, countdownSeconds, randomDisengageSeed, disengageForecast, startSoulsow, playerLevel, extraTargets, mnkEncounterHint, trackOverrides, repeat, skillSpeed, potions)
            {
                IrregularSeed = irregularSeed,
                IrregularRate = irregularRate,
                IrregularKinds = irregularKinds,
                IrregularSeeds = irregularSeeds,
                IrregularRates = irregularRates,
                IrregularKindSets = irregularKindSets,
                IrregularOut = irregularOut,
                OracleArgs = oracleArgs
            };
        }
    }

    private sealed record RprPotionTestCase(string Name)
    {
        public bool Boundary { get; init; }
        public bool ExpectImmediateSpend { get; init; }
        public bool ExpectGluttony { get; init; }
        public bool ExpectDelayedGluttony { get; init; }
        public bool ExpectEnshroud { get; init; }
        public XanRPR.PotionUseStrategy Potion { get; init; } = XanRPR.PotionUseStrategy.EvenBurstExceptOpener;
        public XanRPR.SoulReaverStrategy Reaver { get; init; } = XanRPR.SoulReaverStrategy.Automatic;
        public XanRPR.RedGaugeStrategy RedGauge { get; init; } = XanRPR.RedGaugeStrategy.Automatic;
        public OffensiveStrategy Enshroud { get; init; } = OffensiveStrategy.Automatic;
        public int SkillSpeed { get; init; } = 420;
        public uint PotionCount { get; init; } = 3;
        public int Soul { get; init; } = 50;
        public int Shroud { get; init; } = 90;
        public float ArcaneCircleIn { get; init; } = 20;
        public float GluttonyIn { get; init; }
        public float EnshroudIn { get; init; }
        public float PotionIn { get; init; }
        public float SoulSpenderIn { get; init; }
        public float GCD { get; init; } = 1.5f;
        public float ComboLeft { get; init; } = 30;
        public float DeathsDesignLeft { get; init; } = 60;
        public float DowntimeIn { get; init; } = float.MaxValue;
    }
    private sealed record RprPotionAction(float Time, ActionID Action, int Shroud, float PotionLeft, float ArcaneCircleLeft, int SoulBefore);
    internal readonly record struct BlmSample(float T, float Potency, float Buffed, bool TargetAvailable, int Element, int MP, int Hearts, int Polyglot, int AstralSoul, bool Paradox, bool Firestarter,
        bool Thunderhead, bool InstantCast, bool LeyLines, float ManafontIn, float TriplecastIn, float SwiftcastIn, float AmplifierIn)
    {
        public string Key => FormattableString.Invariant($"{Element}/{MP}/{Hearts}/{Polyglot}/{AstralSoul}/{(Paradox ? 1 : 0)}/{(Firestarter ? 1 : 0)}/{(Thunderhead ? 1 : 0)}/{(InstantCast ? 1 : 0)}/{(LeyLines ? 1 : 0)}/{ManafontIn:f1}/{TriplecastIn:f1}/{SwiftcastIn:f1}/{AmplifierIn:f1}");
    }

    private sealed record ExecutedAction(float Time, ActionID Action, bool GCD, bool TargetAvailable, ulong TargetID, float Potency = 0f, bool RaidBuffed = false);
    // Mnk* are the monk quality metrics: agents_mnk.md ranks GCD uptime, buff-window density and Phantom Rush count
    // above raw potency, and unlike a potency table those can be measured instead of remembered.
    private sealed record RotationMetrics(float Potency, float TerminalValue, int DDFrames, int TargetFrames, int SoulOvercap, int ShroudOvercap, int Enshrouds, int Communios, int Perfectios, int Gluttonies, int Harvests, int PolyglotOvercap = 0, double ExecuteMs = 0, float PartyArcaneCircleValue = 0f,
        int MnkChakraOvercap = 0, int MnkBlitzes = 0, int MnkPhantomRushes = 0, int MnkPerfectBalances = 0, int MnkBeastDropped = 0, int MnkRoFGCDs = 0, int MnkBrotherhoodGCDs = 0, float MnkRoFPotency = 0f, int MnkOpoGCDs = 0, int MnkRoFOpoGCDs = 0, int MnkBlitzesExpired = 0, int MnkWastedPBs = 0, int MnkFuryOpoGCDs = 0, int MnkRoFFuryOpoGCDs = 0, int MnkPBGCDs = 0, int MnkPBFuryOpoGCDs = 0, int MnkReplies = 0, int MnkRepliesExpired = 0, int GcdIdleFrames = 0, int GcdActions = 0,
        float RaidBuffedPotency = 0f, int RaidBuffFrames = 0, int BuffedPerfectios = 0, int BuffedCommunios = 0, int BuffedHarvests = 0,
        int GnbCartOvercap = 0, int GnbNoMercies = 0, int GnbNoMercyGCDs = 0, int GnbGnashingChains = 0, int GnbReignChains = 0,
        int GnbDoubleDowns = 0, int GnbSonicBreaks = 0, int GnbContinuationsLost = 0, int GnbBurstGCDsOutsideNM = 0, int GnbPreNMGFAnchors = 0, int GnbAnchorableNoMercies = 0, int GnbBurstOpenersOutsideNM = 0,
        float NinPartyDokumori = 0f, float NinKunaiPotency = 0f, int NinKunaiGCDs = 0, int NinRaijuLost = 0, int NinRabbits = 0, int NinRejectedFrames = 0, int NinInvalidTcjFrames = 0,
        int NinTcjIncomplete = 0, int NinMudraTimeouts = 0, int NinMudraCapFrames = 0, int NinNinkiOvercap = 0, int NinRaitonFirst = 0, int NinKassatsuFirst = 0,
        int NinOpenerDokGCDSum = 0, int NinOpenerKunaiGCDSum = 0, int NinOpenerRuns = 0,
        float DrgPartyLitany = 0f, int DrgLanceGCDs = 0, int DrgLotdGCDs = 0, int DrgLifeSurgeLost = 0, int DrgLifeSurgeWeak = 0, int DrgProcsLost = 0,
        int DrgFocusOvercap = 0, int DrgNoSurgeGCDs = 0, int DrgDotGapFrames = 0, int DrgGeirskoguls = 0, int DrgStardivers = 0,
        int SamKenkiOvercap = 0, int SamMeditationOvercap = 0, int SamSenOvercap = 0, int SamProcsLost = 0, int SamNoFugetsuGCDs = 0,
        int SamDotGapFrames = 0, int SamIaijutsu = 0, int SamTsubame = 0, int SamNamikiri = 0, float SamDotPotency = 0f,
        int VprOfferingOvercap = 0, int VprCoilOvercap = 0, int VprProcsLost = 0, int VprFollowUpsLost = 0, int VprNoInstinctGCDs = 0,
        int VprReawakens = 0, int VprGenerations = 0, int VprUncoiledFuries = 0, int VprCoils = 0, int VprPositionalsMissed = 0,
        int MchHeatOvercap = 0, int MchBatteryOvercap = 0, int MchProcsLost = 0, int MchHypercharges = 0, int MchOverheatedShots = 0,
        int MchOverheatedStacksLost = 0, int MchWildfires = 0, int MchWildfireHits = 0, int MchQueens = 0, int MchQueenBattery = 0,
        int MchPetHitsLost = 0, int MchReassembledTools = 0, int MchReassembleWasted = 0, float MchDotPotency = 0f,
        PldMetrics Pld = default)
    {
        public static readonly RotationMetrics Empty = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        public float DDUptime => TargetFrames > 0 ? (float)DDFrames / TargetFrames : 0;
        // share of targetable time covered by GCDs (2.5s baseline); GcdIdleFrames counts frames where a GCD was
        // off cooldown, unlocked and simply not pressed - it stays 0 in the harness, where the player never walks away
        public float GcdUptime => TargetFrames > 0 ? GcdActions * 2.5f / (TargetFrames * 0.05f) : 0;
        public float Total => Potency + TerminalValue;
        public float RdpsTotal => Total + PartyArcaneCircleValue + NinPartyDokumori + DrgPartyLitany;
        public RotationMetrics Add(RotationMetrics other) => new(Potency + other.Potency, TerminalValue + other.TerminalValue, DDFrames + other.DDFrames, TargetFrames + other.TargetFrames, SoulOvercap + other.SoulOvercap, ShroudOvercap + other.ShroudOvercap, Enshrouds + other.Enshrouds, Communios + other.Communios, Perfectios + other.Perfectios, Gluttonies + other.Gluttonies, Harvests + other.Harvests, PolyglotOvercap + other.PolyglotOvercap, ExecuteMs + other.ExecuteMs, PartyArcaneCircleValue + other.PartyArcaneCircleValue,
            MnkChakraOvercap + other.MnkChakraOvercap, MnkBlitzes + other.MnkBlitzes, MnkPhantomRushes + other.MnkPhantomRushes, MnkPerfectBalances + other.MnkPerfectBalances, MnkBeastDropped + other.MnkBeastDropped, MnkRoFGCDs + other.MnkRoFGCDs, MnkBrotherhoodGCDs + other.MnkBrotherhoodGCDs, MnkRoFPotency + other.MnkRoFPotency, MnkOpoGCDs + other.MnkOpoGCDs, MnkRoFOpoGCDs + other.MnkRoFOpoGCDs, MnkBlitzesExpired + other.MnkBlitzesExpired, MnkWastedPBs + other.MnkWastedPBs, MnkFuryOpoGCDs + other.MnkFuryOpoGCDs, MnkRoFFuryOpoGCDs + other.MnkRoFFuryOpoGCDs, MnkPBGCDs + other.MnkPBGCDs, MnkPBFuryOpoGCDs + other.MnkPBFuryOpoGCDs, MnkReplies + other.MnkReplies, MnkRepliesExpired + other.MnkRepliesExpired, GcdIdleFrames + other.GcdIdleFrames, GcdActions + other.GcdActions,
            RaidBuffedPotency + other.RaidBuffedPotency, RaidBuffFrames + other.RaidBuffFrames, BuffedPerfectios + other.BuffedPerfectios, BuffedCommunios + other.BuffedCommunios, BuffedHarvests + other.BuffedHarvests,
            GnbCartOvercap + other.GnbCartOvercap, GnbNoMercies + other.GnbNoMercies, GnbNoMercyGCDs + other.GnbNoMercyGCDs, GnbGnashingChains + other.GnbGnashingChains, GnbReignChains + other.GnbReignChains,
            GnbDoubleDowns + other.GnbDoubleDowns, GnbSonicBreaks + other.GnbSonicBreaks, GnbContinuationsLost + other.GnbContinuationsLost, GnbBurstGCDsOutsideNM + other.GnbBurstGCDsOutsideNM,
            GnbPreNMGFAnchors + other.GnbPreNMGFAnchors, GnbAnchorableNoMercies + other.GnbAnchorableNoMercies, GnbBurstOpenersOutsideNM + other.GnbBurstOpenersOutsideNM,
            NinPartyDokumori + other.NinPartyDokumori, NinKunaiPotency + other.NinKunaiPotency, NinKunaiGCDs + other.NinKunaiGCDs, NinRaijuLost + other.NinRaijuLost, NinRabbits + other.NinRabbits, NinRejectedFrames + other.NinRejectedFrames, NinInvalidTcjFrames + other.NinInvalidTcjFrames,
            NinTcjIncomplete + other.NinTcjIncomplete, NinMudraTimeouts + other.NinMudraTimeouts, NinMudraCapFrames + other.NinMudraCapFrames, NinNinkiOvercap + other.NinNinkiOvercap, NinRaitonFirst + other.NinRaitonFirst, NinKassatsuFirst + other.NinKassatsuFirst,
            NinOpenerDokGCDSum + other.NinOpenerDokGCDSum, NinOpenerKunaiGCDSum + other.NinOpenerKunaiGCDSum, NinOpenerRuns + other.NinOpenerRuns,
            DrgPartyLitany + other.DrgPartyLitany, DrgLanceGCDs + other.DrgLanceGCDs, DrgLotdGCDs + other.DrgLotdGCDs, DrgLifeSurgeLost + other.DrgLifeSurgeLost, DrgLifeSurgeWeak + other.DrgLifeSurgeWeak, DrgProcsLost + other.DrgProcsLost,
            DrgFocusOvercap + other.DrgFocusOvercap, DrgNoSurgeGCDs + other.DrgNoSurgeGCDs, DrgDotGapFrames + other.DrgDotGapFrames, DrgGeirskoguls + other.DrgGeirskoguls, DrgStardivers + other.DrgStardivers,
            SamKenkiOvercap + other.SamKenkiOvercap, SamMeditationOvercap + other.SamMeditationOvercap, SamSenOvercap + other.SamSenOvercap, SamProcsLost + other.SamProcsLost, SamNoFugetsuGCDs + other.SamNoFugetsuGCDs,
            SamDotGapFrames + other.SamDotGapFrames, SamIaijutsu + other.SamIaijutsu, SamTsubame + other.SamTsubame, SamNamikiri + other.SamNamikiri, SamDotPotency + other.SamDotPotency,
            VprOfferingOvercap + other.VprOfferingOvercap, VprCoilOvercap + other.VprCoilOvercap, VprProcsLost + other.VprProcsLost, VprFollowUpsLost + other.VprFollowUpsLost, VprNoInstinctGCDs + other.VprNoInstinctGCDs,
            VprReawakens + other.VprReawakens, VprGenerations + other.VprGenerations, VprUncoiledFuries + other.VprUncoiledFuries, VprCoils + other.VprCoils, VprPositionalsMissed + other.VprPositionalsMissed,
            MchHeatOvercap + other.MchHeatOvercap, MchBatteryOvercap + other.MchBatteryOvercap, MchProcsLost + other.MchProcsLost, MchHypercharges + other.MchHypercharges, MchOverheatedShots + other.MchOverheatedShots,
            MchOverheatedStacksLost + other.MchOverheatedStacksLost, MchWildfires + other.MchWildfires, MchWildfireHits + other.MchWildfireHits, MchQueens + other.MchQueens, MchQueenBattery + other.MchQueenBattery,
            MchPetHitsLost + other.MchPetHitsLost, MchReassembledTools + other.MchReassembledTools, MchReassembleWasted + other.MchReassembleWasted, MchDotPotency + other.MchDotPotency,
            Pld.Add(other.Pld));
        public string Format() => FormattableString.Invariant($"potency={Potency:f0} terminal={TerminalValue:f0} total={Total:f0} dd_uptime={DDUptime:f4} soul_overcap={SoulOvercap} shroud_overcap={ShroudOvercap} enshroud={Enshrouds} communio={Communios} perfectio={Perfectios} gluttony={Gluttonies} ph={Harvests} polyglot_overcap={PolyglotOvercap} exec_ms={ExecuteMs:f0} party_ac={PartyArcaneCircleValue:f0} rdps={RdpsTotal:f0} chakra_overcap={MnkChakraOvercap} blitz={MnkBlitzes} pr={MnkPhantomRushes} pb={MnkPerfectBalances} beast_drop={MnkBeastDropped} blitz_expired={MnkBlitzesExpired} pb_wasted={MnkWastedPBs} rof_gcds={MnkRoFGCDs} rof_potency={MnkRoFPotency:f0} opo_gcds={MnkOpoGCDs} rof_opo_gcds={MnkRoFOpoGCDs} fury_opo={MnkFuryOpoGCDs} rof_fury_opo={MnkRoFFuryOpoGCDs} pb_gcds={MnkPBGCDs} pb_fury_opo={MnkPBFuryOpoGCDs} bh_gcds={MnkBrotherhoodGCDs} replies={MnkReplies} replies_lost={MnkRepliesExpired} gcd_uptime={GcdUptime:f4} gcd_idle={GcdIdleFrames} buffed_potency={RaidBuffedPotency:f0} buff_frames={RaidBuffFrames} buffed_perfectio={BuffedPerfectios} buffed_communio={BuffedCommunios} buffed_ph={BuffedHarvests} cart_overcap={GnbCartOvercap} nm={GnbNoMercies} nm_gcds={GnbNoMercyGCDs} gf_chains={GnbGnashingChains} reign_chains={GnbReignChains} dd={GnbDoubleDowns} sb={GnbSonicBreaks} cont_lost={GnbContinuationsLost} burst_outside_nm={GnbBurstGCDsOutsideNM} pre_nm_gf={GnbPreNMGFAnchors}/{GnbAnchorableNoMercies} burst_openers_outside_nm={GnbBurstOpenersOutsideNM} party_doku={NinPartyDokumori:f0} kunai_potency={NinKunaiPotency:f0} kunai_gcds={NinKunaiGCDs} raiju_lost={NinRaijuLost} rabbits={NinRabbits} ninjutsu_rejected={NinRejectedFrames} tcj_invalid={NinInvalidTcjFrames} tcj_incomplete={NinTcjIncomplete} mudra_timeouts={NinMudraTimeouts} mudra_cap_s={NinMudraCapFrames * 0.05f:f1} ninki_overcap={NinNinkiOvercap} rf={NinRaitonFirst} kf={NinKassatsuFirst} opener_dok_gcd={(NinOpenerRuns > 0 ? (float)NinOpenerDokGCDSum / NinOpenerRuns : 0):f2} opener_kunai_gcd={(NinOpenerRuns > 0 ? (float)NinOpenerKunaiGCDSum / NinOpenerRuns : 0):f2} party_litany={DrgPartyLitany:f0} lc_gcds={DrgLanceGCDs} lotd_gcds={DrgLotdGCDs} ls_lost={DrgLifeSurgeLost} ls_weak={DrgLifeSurgeWeak} procs_lost={DrgProcsLost} focus_overcap={DrgFocusOvercap} no_surge_gcds={DrgNoSurgeGCDs} dot_gap_s={DrgDotGapFrames * 0.05f:f1} geirskogul={DrgGeirskoguls} stardiver={DrgStardivers} kenki_overcap={SamKenkiOvercap} meditation_overcap={SamMeditationOvercap} sen_overcap={SamSenOvercap} sam_procs_lost={SamProcsLost} no_fugetsu_gcds={SamNoFugetsuGCDs} sam_dot_gap_s={SamDotGapFrames * 0.05f:f1} iaijutsu={SamIaijutsu} tsubame={SamTsubame} namikiri={SamNamikiri} sam_dot_potency={SamDotPotency:f0} offering_overcap={VprOfferingOvercap} coil_overcap={VprCoilOvercap} vpr_procs_lost={VprProcsLost} follow_ups_lost={VprFollowUpsLost} no_instinct_gcds={VprNoInstinctGCDs} reawaken={VprReawakens} generations={VprGenerations} uncoiled_fury={VprUncoiledFuries} coils={VprCoils} positionals_missed={VprPositionalsMissed} heat_overcap={MchHeatOvercap} battery_overcap={MchBatteryOvercap} mch_procs_lost={MchProcsLost} hypercharge={MchHypercharges} overheated_shots={MchOverheatedShots} overheat_lost={MchOverheatedStacksLost} wildfire={MchWildfires} wildfire_hits={MchWildfireHits} queens={MchQueens} queen_battery={MchQueenBattery} pet_hits_lost={MchPetHitsLost} reassembled_tools={MchReassembledTools} reassemble_wasted={MchReassembleWasted} mch_dot_potency={MchDotPotency:f0}{(Pld.Present ? " " + Pld.Format() : "")}");
        public string Csv() => FormattableString.Invariant($"{Potency:f1},{TerminalValue:f1},{Total:f1},{DDUptime:f4},{SoulOvercap},{ShroudOvercap},{Enshrouds},{Communios},{Perfectios},{Gluttonies},{Harvests},{PolyglotOvercap},{ExecuteMs:f1},{PartyArcaneCircleValue:f1},{RdpsTotal:f1},{MnkChakraOvercap},{MnkBlitzes},{MnkPhantomRushes},{MnkPerfectBalances},{MnkBeastDropped},{MnkRoFGCDs},{MnkRoFPotency:f1},{MnkOpoGCDs},{MnkRoFOpoGCDs},{MnkBrotherhoodGCDs},{MnkReplies},{MnkRepliesExpired},{GcdUptime:f4},{GcdIdleFrames},{GnbCartOvercap},{GnbNoMercies},{GnbNoMercyGCDs},{GnbGnashingChains},{GnbReignChains},{GnbDoubleDowns},{GnbSonicBreaks},{GnbContinuationsLost},{GnbBurstGCDsOutsideNM},{GnbPreNMGFAnchors},{GnbAnchorableNoMercies},{GnbBurstOpenersOutsideNM},{NinPartyDokumori:f1},{NinKunaiPotency:f1},{NinKunaiGCDs},{NinRaijuLost},{NinRabbits},{NinRejectedFrames},{NinInvalidTcjFrames},{NinTcjIncomplete},{NinMudraTimeouts},{NinMudraCapFrames},{NinNinkiOvercap},{NinRaitonFirst},{NinKassatsuFirst},{NinOpenerDokGCDSum},{NinOpenerKunaiGCDSum},{NinOpenerRuns},{DrgPartyLitany:f1},{DrgLanceGCDs},{DrgLotdGCDs},{DrgLifeSurgeLost},{DrgLifeSurgeWeak},{DrgProcsLost},{DrgFocusOvercap},{DrgNoSurgeGCDs},{DrgDotGapFrames},{DrgGeirskoguls},{DrgStardivers},{SamKenkiOvercap},{SamMeditationOvercap},{SamSenOvercap},{SamProcsLost},{SamNoFugetsuGCDs},{SamDotGapFrames},{SamIaijutsu},{SamTsubame},{SamNamikiri},{SamDotPotency:f1},{VprOfferingOvercap},{VprCoilOvercap},{VprProcsLost},{VprFollowUpsLost},{VprNoInstinctGCDs},{VprReawakens},{VprGenerations},{VprUncoiledFuries},{VprCoils},{VprPositionalsMissed},{MchHeatOvercap},{MchBatteryOvercap},{MchProcsLost},{MchHypercharges},{MchOverheatedShots},{MchOverheatedStacksLost},{MchWildfires},{MchWildfireHits},{MchQueens},{MchQueenBattery},{MchPetHitsLost},{MchReassembledTools},{MchReassembleWasted},{MchDotPotency:f1}");
    }
    private sealed record TimelineResult(int Frames, int Actions, int GCDActions, int OGCDActions, int EnemyTargetActionsDuringTargetLoss, int ResumeGCDActions, int DmuTimelineEventsEmitted, int DmuTimelineEventsSkipped, int DmuPredictedDamageFrames, int RiddleOfEarthActions, int? ActiveBossPhase, IReadOnlyList<string> Failures, RotationMetrics Metrics, IrregularDriver.Stats Irregular = default);
    private sealed record TimelineMatrixScenario(string Name, int ZoneID, float Duration, IReadOnlyList<EventTriggerTimelineWindow> TargetUnavailableWindows);

    private sealed class TimelineMatrixAggregate
    {
        public int Scenarios { get; private set; }
        public int Frames { get; private set; }
        public int Actions { get; private set; }
        public int GCDActions { get; private set; }
        public int OGCDActions { get; private set; }
        public int EnemyTargetActionsDuringTargetLoss { get; private set; }
        public int ResumeGCDActions { get; private set; }
        public int Failures { get; private set; }
        public RotationMetrics Metrics { get; private set; } = RotationMetrics.Empty;

        public void Add(TimelineResult result)
        {
            ++Scenarios;
            Metrics = Metrics.Add(result.Metrics);
            Frames += result.Frames;
            Actions += result.Actions;
            GCDActions += result.GCDActions;
            OGCDActions += result.OGCDActions;
            EnemyTargetActionsDuringTargetLoss += result.EnemyTargetActionsDuringTargetLoss;
            ResumeGCDActions += result.ResumeGCDActions;
            Failures += result.Failures.Count;
        }
    }
}
