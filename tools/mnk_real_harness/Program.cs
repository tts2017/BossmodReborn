using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BossMod;
using BossMod.Autorotation;
using BossMod.Autorotation.xan;
using BossMod.MNK;
using EncounterTimeline;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using MnkRegression;
using XanMNK = BossMod.Autorotation.xan.MNK;

namespace MnkRealHarness;

internal static class Program
{
    private const int DefaultPatternCount = 20_000;
    private const float TimelineStep = 0.05f;
    private const float TimelineGCD = (float)MnkPatch75Data.DefaultLevel100Gcd;
    private const float PBNearOvercapWindow = 30f;
    private const uint DMUPrimaryActorOID = 0x4C30;
    private const uint DMUPhase2ActorOID = 0x4C32;
    private const uint DMUChaosP3ActorOID = 0x4C34;
    private const uint DMUExdeathP3ActorOID = 0x4C35;
    private const uint DMUKefkaP3ActorOID = 0x4BFB;
    private const uint DMUKefkaP4ActorOID = 0x482B;
    private const uint DMUKefkaP5ActorOID = 0x4C37;
    private const uint DMURevoltingRuinIIIAID = 50179;
    private const uint DMUUltimateEmbraceAID = 49740;
    private const uint DMUForsakenAID = 47804;
    private static readonly DateTime BaseTime = new(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
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
                "dmu" => DMUPlannerIntegration(options),
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
        Console.WriteLine($"pattern={result.Pattern.Name}");
        Console.WriteLine($"queued={result.Actions.Count}");
        foreach (var action in result.Actions)
            Console.WriteLine($"{action.Action} target={action.Target:X} priority={action.Priority:f1}");
        return result.Exception == null ? 0 : 1;
    }

    private static int Count(HarnessOptions options)
    {
        var start = options.Start;
        var count = options.Limit ?? DefaultPatternCount;
        HashSet<RealMnkPattern> uniquePatterns = [];
        for (var offset = 0; offset < count; ++offset)
            uniquePatterns.Add(GeneratedPattern(start + offset) with { Name = string.Empty });

        Console.WriteLine($"start={start}");
        Console.WriteLine($"real_mnk_patterns={count}");
        Console.WriteLine($"duplicate_patterns={count - uniquePatterns.Count}");
        return uniquePatterns.Count == count ? 0 : 3;
    }

    private static int Run(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);

        var limit = options.Limit ?? DefaultPatternCount;
        var start = options.Start;
        var results = new RealHarnessResult(start, limit);
        var persistResults = !string.IsNullOrWhiteSpace(options.Out);
        HashSet<RealMnkPattern> uniquePatterns = [];
        for (var offset = 0; offset < limit; ++offset)
        {
            var i = start + offset;
            var pattern = GeneratedPattern(i);
            if (!uniquePatterns.Add(pattern with { Name = string.Empty }))
                ++results.DuplicatePatterns;

            var result = ExecutePattern(pattern, sampleIndex: i);
            var failed = result.Exception != null || result.PolicyFailures.Count != 0;
            if (persistResults || failed)
                results.Results.Add(result);

            if (failed)
            {
                ++results.Failures;
                results.PolicyFailures += result.PolicyFailures.Count;
            }
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

        Console.WriteLine($"start={results.Start}");
        Console.WriteLine($"patterns={results.Patterns}");
        Console.WriteLine($"failures={results.Failures}");
        Console.WriteLine($"duplicate_patterns={results.DuplicatePatterns}");
        Console.WriteLine($"policy_failures={results.PolicyFailures}");
        Console.WriteLine($"empty_queues={results.EmptyQueues}");
        Console.WriteLine($"expected_empty_queues={results.ExpectedEmptyQueues}");
        if (!string.IsNullOrWhiteSpace(options.Out))
            Console.WriteLine($"out={options.Out}");

        var pbForceNoShiftPassed = CheckPBForceNoShift();
        var raidBuffWindowsPassed = CheckRaidBuffWindows();
        return results.Failures == 0 && results.DuplicatePatterns == 0 && pbForceNoShiftPassed && raidBuffWindowsPassed ? 0 : 3;
    }

    private static bool CheckRaidBuffWindows()
    {
        var checks = 0;
        var failures = 0;
        var world = BuildWorld(TimelinePattern("raid_buff_windows"), 0, out var player, out var target, out _);
        using var cooldowns = new RaidCooldowns(world);
        Array.Clear(player.Statuses);
        Array.Clear(target.Statuses);

        void Check(string name, Actor? currentTarget, params RaidCooldowns.DamageBuffWindowSnapshot[] expected)
        {
            ++checks;
            var actual = cooldowns.DamageBuffWindows(player, currentTarget);
            if (actual.SequenceEqual(expected))
                return;

            ++failures;
            if (failures <= 5)
                Console.Error.WriteLine($"raid_buff_windows {name}: expected=[{string.Join("; ", expected)}], actual=[{string.Join("; ", actual)}]");
        }

        (uint Status, ActionID Action, bool Debuff, float Weight)[] statuses =
        [
            ((uint)BossMod.AST.SID.Divination, ActionID.MakeSpell(BossMod.AST.AID.Divination), false, 1),
            ((uint)BossMod.DRG.SID.BattleLitany, ActionID.MakeSpell(BossMod.DRG.AID.BattleLitany), false, 1),
            ((uint)BossMod.RPR.SID.ArcaneCircle, ActionID.MakeSpell(BossMod.RPR.AID.ArcaneCircle), false, 1),
            ((uint)SID.Brotherhood, ActionID.MakeSpell(AID.Brotherhood), false, 1),
            ((uint)BossMod.BRD.SID.BattleVoice, ActionID.MakeSpell(BossMod.BRD.AID.BattleVoice), false, 1),
            ((uint)BossMod.DNC.SID.TechnicalFinish, ActionID.MakeSpell(BossMod.DNC.AID.QuadrupleTechnicalFinish), false, 1),
            ((uint)BossMod.SMN.SID.SearingLight, ActionID.MakeSpell(BossMod.SMN.AID.SearingLight), false, 1),
            ((uint)BossMod.RDM.SID.Embolden, ActionID.MakeSpell(BossMod.RDM.AID.Embolden), false, 1),
            ((uint)BossMod.PCT.SID.StarryMuse, ActionID.MakeSpell(BossMod.PCT.AID.StarryMuse), false, 1),
            ((uint)BossMod.SCH.SID.ChainStratagem, ActionID.MakeSpell(BossMod.SCH.AID.ChainStratagem), true, 1.1f),
            ((uint)BossMod.NIN.SID.Dokumori, ActionID.MakeSpell(BossMod.NIN.AID.Dokumori), true, 1.1f),
            ((uint)BossMod.NIN.SID.VulnerabilityUp, ActionID.MakeSpell(BossMod.NIN.AID.Dokumori), true, 1.1f)
        ];

        Check("empty", target);
        foreach (var status in statuses)
        foreach (var remaining in new[] { -1.0, 0.0, 0.001, 12.0, 20.0, 25.0 })
        {
            Array.Clear(player.Statuses);
            Array.Clear(target.Statuses);
            var recipient = status.Debuff ? target : player;
            world.Execute(new ActorState.OpStatus(recipient.InstanceID, 0, new(status.Status, 0, world.FutureTime(remaining), player.InstanceID)));
            RaidCooldowns.DamageBuffWindowSnapshot[] expected = remaining > 0
                ? [new(status.Action, 0, (float)remaining, status.Weight)]
                : [];
            Check($"status={status.Status}, remaining={remaining}", target, expected);
            Check($"status={status.Status}, remaining={remaining}, no target", null, status.Debuff ? [] : expected);
        }

        Array.Clear(player.Statuses);
        Array.Clear(target.Statuses);
        world.Execute(new ActorState.OpStatus(player.InstanceID, 0, new(uint.MaxValue, 0, world.FutureTime(12), player.InstanceID)));
        world.Execute(new ActorState.OpStatus(target.InstanceID, 0, new(uint.MaxValue, 0, world.FutureTime(12), player.InstanceID)));
        Check("unrecognized statuses", target);

        var buffAction = ActionID.MakeSpell(BossMod.AST.AID.Divination);
        var debuffAction = ActionID.MakeSpell(BossMod.SCH.AID.ChainStratagem);
        world.Execute(new ActorState.OpStatus(player.InstanceID, 0, new((uint)BossMod.AST.SID.Divination, 0, world.FutureTime(12), player.InstanceID)));
        world.Execute(new ActorState.OpStatus(target.InstanceID, 0, new((uint)BossMod.SCH.SID.ChainStratagem, 0, world.FutureTime(7), player.InstanceID)));
        world.Execute(new ActorState.OpCastEvent(player.InstanceID, new(buffAction, player.InstanceID, 0, 0, player.PosRot.XYZ(), 0, 0, player.Rotation)));
        world.Execute(new ActorState.OpCastEvent(player.InstanceID, new(debuffAction, target.InstanceID, 0, 0, player.PosRot.XYZ(), 0, 0, player.Rotation)));
        var observedAt = world.CurrentTime;
        foreach (var elapsed in new[] { 0.0, 5.0, 7.0, 12.0, 120.0, 121.0 })
        {
            world.Execute(new WorldState.OpFrameStart(new(observedAt.AddSeconds(elapsed), 0, 0, 0, 0, 1), default, default, default));
            List<RaidCooldowns.DamageBuffWindowSnapshot> expected = [];
            if (elapsed < 12)
                expected.Add(new(buffAction, 0, (float)(12 - elapsed), 1));
            if (elapsed < 7)
                expected.Add(new(debuffAction, 0, (float)(7 - elapsed), 1.1f));
            expected.Add(new(buffAction, (float)Math.Max(0, 120 - elapsed), 20, 1));
            expected.Add(new(debuffAction, (float)Math.Max(0, 120 - elapsed), 20, 1.1f));
            Check($"active and predicted windows after {elapsed}s", target, [.. expected]);
        }

        Console.WriteLine($"raid_buff_window_checks={checks}");
        Console.WriteLine($"raid_buff_window_failures={failures}");
        return failures == 0;
    }

    private static bool CheckPBForceNoShift()
    {
        var checks = 0;
        var failures = 0;
        foreach (var level in new[] { 49, 50, 100 })
        foreach (var rotation in new[] { RotationMode.Full, RotationMode.BasicAndChakraOvercap })
        foreach (var formlessLeft in new[] { 0.0, 0.1, 20.0 })
        foreach (var pbStrategy in new[] { XanMNK.PBStrategy.Force, XanMNK.PBStrategy.ForceNoShift, XanMNK.PBStrategy.Delay })
        {
            var pattern = TimelinePattern($"pb_{pbStrategy}_{level}_{rotation}_{formlessLeft}") with
            {
                CombatTimer = 30,
                Level = level,
                RotationMode = rotation,
                PBStrategy = pbStrategy == XanMNK.PBStrategy.Delay ? PBStrategyMode.Delay : PBStrategyMode.Force,
                FormlessFistLeft = formlessLeft,
                RoFStrategy = AutoForceDelayMode.Delay,
                BrotherhoodStrategy = AutoForceDelayMode.Delay,
                BlitzStrategy = BlitzStrategyMode.Delay
            };
            var result = ExecutePattern(pattern, sampleIndex: 0, pbStrategyOverride: pbStrategy);
            var expected = level >= 50 && (pbStrategy == XanMNK.PBStrategy.Force || pbStrategy == XanMNK.PBStrategy.ForceNoShift && formlessLeft == 0);
            ++checks;
            if (result.Exception != null || result.PolicyFailures.Count != 0 || HasAction(result.Actions, AID.PerfectBalance, pattern.AnimationLockDelay) != expected)
            {
                ++failures;
                Console.Error.WriteLine($"{pattern.Name}: expected PB={expected}, exception={result.Exception}, failures={string.Join(';', result.PolicyFailures)}");
            }
        }

        ++checks;
        int[] savedStrategyValues = [(int)XanMNK.PBStrategy.Automatic, (int)XanMNK.PBStrategy.ForceOpo,
            (int)XanMNK.PBStrategy.Force, (int)XanMNK.PBStrategy.Delay,
            (int)XanMNK.PBStrategy.DowntimeSolar, (int)XanMNK.PBStrategy.DowntimeLunar];
        if (!savedStrategyValues.SequenceEqual(Enumerable.Range(0, savedStrategyValues.Length)))
        {
            ++failures;
            Console.Error.WriteLine("Existing PB strategy values changed");
        }

        Console.WriteLine($"pb_force_no_shift_checks={checks}");
        Console.WriteLine($"pb_force_no_shift_failures={failures}");
        return failures == 0;
    }

    private static bool ExpectedEmptyQueue(RealMnkPattern pattern)
        => !pattern.HaveTarget
        || !pattern.Targetable
        || pattern.TargetPriority < 0
        || pattern.LookAway
        || pattern.RotationMode == RotationMode.BasicAndChakraOvercap;

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
        var eventTrigger = options.TimelineRoot is null ? null : LoadDancingMadEventTriggerTimeline(ResolveEventTriggerTimelineRoot(options.TimelineRoot));
        var results = TimelineSuiteScenarios(eventTrigger).Select(ExecuteTimeline).ToList();
        var dmu = ExecuteDMUPlannerIntegration();
        if (!string.IsNullOrWhiteSpace(options.Out))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Out))!);
            File.WriteAllText(options.Out, JsonSerializer.Serialize(results, JsonOptions));
        }
        Console.WriteLine($"timeline_scenarios={results.Count}");
        foreach (var result in results)
            Console.WriteLine($"scenario={result.Scenario} gcd={result.GCDActions} ogcd={result.OGCDActions} pb={result.PerfectBalances} blitz={result.Blitzes} pr={result.PhantomRushes} failures={result.Failures.Count}");
        if (eventTrigger is not null)
            PrintEventTriggerTimelineInput(eventTrigger);
        Console.WriteLine($"scenario={dmu.Scenario} module={dmu.ModuleType} phases={dmu.Phases.Count} downtimes={dmu.Downtimes.Count} resumes={dmu.Resumes.Count} pending_blitz_recovery={dmu.PendingBlitz.RecoveryBlitzActions.Count} pending_formless_recovery={dmu.PendingBlitz.FormlessRecoveryActions.Count} pending_blitz_delay={dmu.PendingBlitz.DelayQueuedBlitzOnRecovery} pending_pb_recovery={dmu.PendingPB.RecoveryPBGCDs.Count} earths_reply_early={dmu.EarthsReply.PartyAllDamagedQueued} mechanics={dmu.MechanicHints.Results.Count} failures={dmu.Failures.Count}");

        var failures = results.Sum(result => result.Failures.Count) + dmu.Failures.Count + (eventTrigger?.Failures.Count ?? 0);
        Console.WriteLine($"failures={failures}");
        if (!string.IsNullOrWhiteSpace(options.Out))
            Console.WriteLine($"out={options.Out}");
        return failures == 0 ? 0 : 3;
    }

    private static int EventTriggerTimelineSource(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        var eventTrigger = LoadDancingMadEventTriggerTimeline(ResolveEventTriggerTimelineRoot(options.TimelineRoot));
        var result = ExecuteTimeline(EventTriggerDancingMadTimelineScenario(eventTrigger));
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

    private static EventTriggerTimelineInput LoadDancingMadEventTriggerTimeline(string resourcesDirectory)
    {
        const int dancingMadZoneID = 1363;
        var catalog = EventTriggerTimelineCatalog.Load(resourcesDirectory);
        var timeline = catalog.TimelineForZone(dancingMadZoneID);
        var fullDuration = DancingMadFullDuration(timeline);
        var targetUnavailableWindows = DancingMadTargetUnavailableWindows(timeline, fullDuration);
        List<string> failures = [];

        if (!timeline.FileName.Equals("dancing_mad.txt", StringComparison.OrdinalIgnoreCase))
            failures.Add($"Event Trigger zone {dancingMadZoneID} resolved '{timeline.FileName}' instead of dancing_mad.txt");
        if (!targetUnavailableWindows.Any(window => Math.Abs(window.Start - 197.3) <= TimelineStep && Math.Abs(window.End - 207.6) <= TimelineStep))
            failures.Add("Event Trigger dancing_mad.txt did not expose the P1-to-P2 197.3s-207.6s target-loss window");
        if (!targetUnavailableWindows.Any(window => Math.Abs(window.Start - 500.0) <= TimelineStep && Math.Abs(window.End - 540.3) <= TimelineStep))
            failures.Add("Event Trigger dancing_mad.txt did not expose the P2-to-P3 target-loss window from Aero III Assault to both targetable");

        return new(resourcesDirectory, dancingMadZoneID, timeline.FileName, catalog.Summary, fullDuration, targetUnavailableWindows, failures);
    }

    private static TimelineWindow[] DancingMadTargetUnavailableWindows(EventTriggerTimeline timeline, float fullDuration)
    {
        var p2End = timeline.Events.Single(entry => entry.Title.Equals("Aero III Assault", StringComparison.Ordinal)).Time;
        var p3Start = timeline.Events.First(entry => entry.Time > p2End && entry.Title.Equals("--both targetable--", StringComparison.Ordinal)).Time;
        return timeline.TargetUnavailableWindows(fullDuration)
            .Select(window => new TimelineWindow(window.Start, window.End))
            .Append(new TimelineWindow(p2End, p3Start))
            .OrderBy(window => window.Start)
            .ToArray();
    }

    private static float DancingMadFullDuration(EventTriggerTimeline timeline)
    {
        var phase5 = timeline.Sections.Single(section => section.Title.StartsWith("Phase 5 -", StringComparison.Ordinal));
        var nextSection = timeline.Sections.First(section => section.StartTime > phase5.StartTime);
        var phase5Events = timeline.Events.Where(entry => entry.Time >= phase5.StartTime && entry.Time < nextSection.StartTime).ToArray();
        if (phase5Events.Length == 0)
            throw new InvalidDataException("Event Trigger dancing_mad.txt has no Phase 5 events before its next section.");
        return phase5Events[^1].Time + TimelineGCD + 1;
    }

    private static MnkTimelineScenario EventTriggerDancingMadTimelineScenario(EventTriggerTimelineInput input)
    {
        var pattern = TimelinePattern("dancing_mad_event_trigger") with { ContentId = 1094 };
        return new(pattern.Name, pattern, 220, input.TargetUnavailableWindows, (NadiFlags)0, [], 0,
            MnkTimelineRequirement.ForbidCoreBurstDuringTargetLoss | MnkTimelineRequirement.ResumeGCD);
    }

    private static MnkTimelineScenario EventTriggerDancingMadFullTimelineScenario(EventTriggerTimelineInput input)
    {
        var pattern = TimelinePattern("dancing_mad_event_trigger_full") with { ContentId = 1094 };
        return new(pattern.Name, pattern, input.FullDuration, input.TargetUnavailableWindows, (NadiFlags)0, [], 0,
            MnkTimelineRequirement.ForbidCoreBurstDuringTargetLoss | MnkTimelineRequirement.ResumeGCD | MnkTimelineRequirement.ResumeAutomaticPB);
    }

    private static MnkTimelineScenario EventTriggerDancingMadTopLogProfileTimelineScenario(EventTriggerTimelineInput input)
    {
        var pattern = TimelinePattern("dancing_mad_top_log_profile_event_trigger_full") with
        {
            ContentId = 1094,
            Opener = OpenerRoFOffsetMode.ZeroSecondBurst,
            BurstTiming = BurstTimingMode.Cooldown,
            RoWStrategy = AutoForceDelayMode.Automatic,
            RiddleOfWindReadyIn = 0
        };
        return new(pattern.Name, pattern, input.FullDuration, input.TargetUnavailableWindows, (NadiFlags)0, [], 0,
            MnkTimelineRequirement.ForbidCoreBurstDuringTargetLoss | MnkTimelineRequirement.ResumeGCD | MnkTimelineRequirement.ResumeAutomaticPB | MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.OpeningDoubleLunar | MnkTimelineRequirement.DancingMadTopLogProfile,
            PotionStrategyOverride: XanMNK.PotionStrategy.OpenerAndEvenBursts,
            SkillRotationOverride: XanMNK.SkillRotationMode.DancingMad);
    }

    private static void PrintEventTriggerTimelineInput(EventTriggerTimelineInput input)
    {
        Console.WriteLine($"event_trigger_resources={input.ResourcesDirectory}");
        Console.WriteLine($"event_trigger_timeline_files={input.Summary.TimelineFiles}");
        Console.WriteLine($"event_trigger_zone_mappings={input.Summary.ZoneMappings}");
        Console.WriteLine($"event_trigger_targetable_events={input.Summary.TargetableEvents}");
        Console.WriteLine($"event_trigger_untargetable_events={input.Summary.UntargetableEvents}");
        Console.WriteLine($"event_trigger_zone={input.ZoneID} file={input.FileName} duration={input.FullDuration:f1} target_loss={string.Join(',', input.TargetUnavailableWindows.Select(window => $"{window.Start:f1}-{window.End:f1}"))} failures={input.Failures.Count}");
        foreach (var failure in input.Failures)
            Console.WriteLine(failure);
    }

    private static void PrintTimelineResult(RealMnkTimelineResult result)
    {
        Console.WriteLine($"scenario={result.Scenario}");
        Console.WriteLine($"duration={result.Duration:f1}");
        Console.WriteLine($"gcd={result.GCDActions}");
        Console.WriteLine($"ogcd={result.OGCDActions}");
        Console.WriteLine($"pb={result.PerfectBalances}");
        Console.WriteLine($"blitz={result.Blitzes}");
        Console.WriteLine($"phantom_rush={result.PhantomRushes}");
        Console.WriteLine($"failures={result.Failures.Count}");
        foreach (var failure in result.Failures)
            Console.WriteLine(failure);
    }

    private static int Usage(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        Console.Error.WriteLine("Usage:");
        Console.Error.WriteLine("  dotnet run --project tools\\mnk_real_harness -- probe [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\mnk_real_harness -- count [--start <index>] [--limit 20000]");
        Console.Error.WriteLine("  dotnet run --project tools\\mnk_real_harness -- run [--start <index>] [--limit 20000] [--out <path>] [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\mnk_real_harness -- timeline [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\mnk_real_harness -- suite [--timeline-root <resources>] [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\mnk_real_harness -- dmu [--sqpack <path>]");
        Console.Error.WriteLine("  dotnet run --project tools\\mnk_real_harness -- event-timeline --timeline-root <resources> [--sqpack <path>]");
        return 2;
    }

    private static int DMUPlannerIntegration(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        var result = ExecuteDMUPlannerIntegration();
        Console.WriteLine($"scenario={result.Scenario}");
        Console.WriteLine($"module={result.ModuleType}");
        foreach (var mechanic in result.MechanicHints.Results)
            Console.WriteLine($"mechanic={mechanic.Mechanic} type={mechanic.Type} predicted_damage={mechanic.PredictedDamageCount} self_prediction={mechanic.SelfPrediction} roe_automatic={mechanic.RoEAutomaticQueued} roe_delay={mechanic.RoEDelayQueued}");
        foreach (var downtime in result.Downtimes)
            Console.WriteLine($"downtime={downtime.Transition} potential_targets={downtime.PotentialTargetCount} core_burst_actions={string.Join(',', downtime.CoreBurstActions)}");
        foreach (var resume in result.Resumes)
            Console.WriteLine($"resume={resume.Transition} potential_targets={resume.PotentialTargetCount} target_gcd_actions={string.Join(',', resume.TargetGCDActions)}");
        Console.WriteLine($"pending_blitz=downtime_blitz={result.PendingBlitz.BlitzQueuedDuringDowntime} recovery_actions={string.Join(',', result.PendingBlitz.RecoveryBlitzActions)} recovery_pb={result.PendingBlitz.PBQueuedOnRecovery} formless_actions={string.Join(',', result.PendingBlitz.FormlessRecoveryActions)} blitz_before_formless={result.PendingBlitz.BlitzQueuedBeforeFormless} delay_recovery_blitz={result.PendingBlitz.DelayQueuedBlitzOnRecovery}");
        Console.WriteLine($"pending_pb=downtime_target_gcd={result.PendingPB.TargetGCDQueuedDuringDowntime} recovery_actions={string.Join(',', result.PendingPB.RecoveryPBGCDs)} recovery_pb={result.PendingPB.PBQueuedOnRecovery}");
        Console.WriteLine($"earths_reply=party_all={result.EarthsReply.PartyAllDamagedQueued} party_partial={result.EarthsReply.PartiallyDamagedQueued} expiring={result.EarthsReply.ExpiringQueued} basic={result.EarthsReply.BasicModeQueued} dead_excluded={result.EarthsReply.DeadMemberQueued} zero_hp_excluded={result.EarthsReply.ZeroMaxHPMemberQueued} missing_excluded={result.EarthsReply.MissingMemberQueued}");
        foreach (var phase in result.Phases)
        {
            var downtimeStart = phase.DowntimeStart?.ToString("f2") ?? "none";
            Console.WriteLine($"phase={phase.Phase} index={phase.Index} starts_with_downtime={phase.StartsWithDowntime} ends_with_downtime={phase.PlannerEndsWithDowntime} targets={phase.TargetCount} target_priority={phase.TargetPriority} phase_end={phase.PhaseEnd:f2} downtime_start={downtimeStart} burst_horizon={phase.BurstHorizon:f2}");
        }
        Console.WriteLine($"failures={result.Failures.Count}");
        foreach (var failure in result.Failures)
            Console.WriteLine(failure);
        return result.Failures.Count == 0 ? 0 : 3;
    }

    private static DmuPlannerIntegrationResult ExecuteDMUPlannerIntegration()
    {
        const string scenario = "dmu_planner_p1_p5";
        List<string> failures = [];
        List<DmuPlannerPhaseResult> phases = [];
        List<DmuDowntimeResult> downtimes = [];
        List<DmuResumeResult> resumes = [];
            var pendingBlitz = new DmuPendingBlitzResult(false, [], false, [], false, false);
            var pendingPB = new DmuPendingPBResult(false, [], false);
            var earthsReply = new DmuEarthsReplyResult(false, false, false, false, false, false, false);
            var mechanicHints = new DmuMechanicHintResult([], []);
            XanMNK? p2ToP3RoFAircastModule = null;
            var p2ToP3RoFAircastApplied = false;
            var p2ToP3RoFAircastStatusIndex = Actor.NumStatuses - 8;
            string moduleType = "<unresolved>";

        try
        {
            var info = BossModuleRegistry.FindByOID(DMUPrimaryActorOID);
            if (info == null)
            {
                failures.Add("DMU registry entry was not found for the P1 primary actor");
                return new(scenario, moduleType, mechanicHints, phases, downtimes, resumes, pendingBlitz, pendingPB, earthsReply, failures);
            }

            moduleType = info.ModuleType.FullName ?? info.ModuleType.Name;
            if (info.GroupType != BossModuleInfo.GroupType.CFC || info.GroupID != 1094)
                failures.Add("DMU registry entry does not identify CFC 1094");
            if (info.Maturity != BossModuleInfo.Maturity.WIP)
                failures.Add("DMU registry entry does not retain its WIP maturity");

            var world = BuildDMUWorld(out var player, out var p1, out var p2, out var hints);
            using var bossmods = new BossModuleManager(world);
            using var dmu = BossModuleRegistry.CreateModuleForActor(world, p1);
            if (dmu == null)
            {
                failures.Add("DMU module could not be constructed through BossModuleRegistry at WIP maturity");
                return new(scenario, moduleType, mechanicHints, phases, downtimes, resumes, pendingBlitz, pendingPB, earthsReply, failures);
            }

            bossmods.ActiveModule = dmu;
            dmu.Update();
            if (!ReferenceEquals(bossmods.ActiveModule, dmu))
                failures.Add("BossModuleManager did not retain the direct DMU active module");

            using var zoneModules = new ZoneModuleManager(world);
            using var hintsBuilder = new AIHintsBuilder(world, bossmods, zoneModules, null);
            var db = new RotationDatabase(new DirectoryInfo("tools/mnk_real_harness/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
            using var manager = new RotationModuleManager(db, bossmods, hints)
            {
                CombatStart = BaseTime
            };
            manager.Planner = new PlanExecution(dmu, null);
            var mnk = new XanMNK(manager, player);
            var strategy = BuildStrategy(TimelinePattern(scenario));

            var roePattern = TimelinePattern(scenario + "_roe") with
            {
                RoFStrategy = AutoForceDelayMode.Delay,
                BrotherhoodStrategy = AutoForceDelayMode.Delay,
                PBStrategy = PBStrategyMode.Delay,
                BlitzStrategy = BlitzStrategyMode.Delay,
                RoWStrategy = AutoForceDelayMode.Delay,
                RoEStrategy = AutoForceDelayMode.Automatic,
                RiddleOfEarthReadyIn = 0
            };
            world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(roePattern)));
            List<DmuDamageMechanicResult> mechanicResults = [];
            List<string> mechanicFailures = [];

            DmuDamageMechanicResult VerifyRoE(string mechanic, AIHints.PredictedDamageType type, bool expectedSelfPrediction, Actor target)
            {
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(roePattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var selfPrediction = hints.PredictedDamage.Any(damage => damage.Type == type
                    && damage.Players[PartyState.PlayerSlot]
                    && damage.Activation > world.CurrentTime
                    && damage.Activation <= world.FutureTime(10));
                var predictedDamageCount = hints.PredictedDamage.Count;
                mnk.Execute(BuildStrategy(roePattern), target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var roeAutomaticQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.RiddleOfEarth));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                mnk.Execute(BuildStrategy(roePattern with { RoEStrategy = AutoForceDelayMode.Delay }), target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var roeDelayQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.RiddleOfEarth));

                if (selfPrediction != expectedSelfPrediction)
                    mechanicFailures.Add($"DMU {mechanic} self-targeted {type} prediction was {selfPrediction}, expected {expectedSelfPrediction}");
                if (roeAutomaticQueued != expectedSelfPrediction)
                    mechanicFailures.Add($"MNK RoE Automatic queue for DMU {mechanic} was {roeAutomaticQueued}, expected {expectedSelfPrediction}");
                if (roeDelayQueued)
                    mechanicFailures.Add($"MNK RoE Delay queued against DMU {mechanic}");

                return new(mechanic, type, predictedDamageCount, selfPrediction, roeAutomaticQueued, roeDelayQueued);
            }

            var earthsReplyPattern = TimelinePattern(scenario + "_earths_reply") with
            {
                CombatTimer = 180,
                RoFStrategy = AutoForceDelayMode.Delay,
                BrotherhoodStrategy = AutoForceDelayMode.Delay,
                PBStrategy = PBStrategyMode.Delay,
                BlitzStrategy = BlitzStrategyMode.Delay,
                RoWStrategy = AutoForceDelayMode.Delay,
                RoEStrategy = AutoForceDelayMode.Delay
            };

            bool QueueEarthsReply(RealMnkPattern pattern, Actor target)
            {
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var module = new XanMNK(manager, player);
                module.Execute(BuildStrategy(pattern), target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                return hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.EarthsReply));
            }

            DmuEarthsReplyResult VerifyEarthsReply(Actor target, Actor partyMember)
            {
                var statusIndex = Actor.NumStatuses - 4;
                world.Execute(new ActorState.OpStatus(player.InstanceID, statusIndex, new((uint)SID.EarthsRumination, 0, world.FutureTime(20), player.InstanceID)));
                world.Execute(new ActorState.OpHPMP(player.InstanceID, new(90000, 100000, 0, 10000, 10000)));
                world.Execute(new ActorState.OpHPMP(partyMember.InstanceID, new(80000, 100000, 0, 10000, 10000)));
                var partyAllDamagedQueued = QueueEarthsReply(earthsReplyPattern, target);

                world.Execute(new ActorState.OpHPMP(partyMember.InstanceID, new(100000, 100000, 0, 10000, 10000)));
                var partiallyDamagedQueued = QueueEarthsReply(earthsReplyPattern, target);

                world.Execute(new ActorState.OpHPMP(player.InstanceID, new(100000, 100000, 0, 10000, 10000)));
                world.Execute(new ActorState.OpStatus(player.InstanceID, statusIndex, new((uint)SID.EarthsRumination, 0, world.FutureTime(0.1f), player.InstanceID)));
                var expiringQueued = QueueEarthsReply(earthsReplyPattern, target);
                var basicModeQueued = QueueEarthsReply(earthsReplyPattern with { RotationMode = RotationMode.BasicAndChakraOvercap }, target);

                world.Execute(new ActorState.OpStatus(player.InstanceID, statusIndex, new((uint)SID.EarthsRumination, 0, world.FutureTime(20), player.InstanceID)));
                world.Execute(new ActorState.OpHPMP(player.InstanceID, new(90000, 100000, 0, 10000, 10000)));
                world.Execute(new ActorState.OpDead(partyMember.InstanceID, true));
                var deadMemberQueued = QueueEarthsReply(earthsReplyPattern, target);
                world.Execute(new ActorState.OpDead(partyMember.InstanceID, false));

                world.Execute(new ActorState.OpStatus(player.InstanceID, statusIndex, new((uint)SID.EarthsRumination, 0, world.FutureTime(20), player.InstanceID)));
                world.Execute(new ActorState.OpHPMP(partyMember.InstanceID, new(0, 0, 0, 10000, 10000)));
                var zeroMaxHPMemberQueued = QueueEarthsReply(earthsReplyPattern, target);

                world.Execute(new ActorState.OpStatus(player.InstanceID, statusIndex, new((uint)SID.EarthsRumination, 0, world.FutureTime(20), player.InstanceID)));
                world.Execute(new ActorState.OpDestroy(partyMember.InstanceID));
                var missingMemberQueued = QueueEarthsReply(earthsReplyPattern, target);
                world.Execute(new ActorState.OpStatus(player.InstanceID, statusIndex, default));
                world.Execute(new ActorState.OpHPMP(player.InstanceID, new(100000, 100000, 0, 10000, 10000)));

                if (!partyAllDamagedQueued)
                    failures.Add("MNK did not queue Earth's Reply while every live party member was damaged");
                if (partiallyDamagedQueued)
                    failures.Add("MNK queued Earth's Reply while a live party member was at full health");
                if (!expiringQueued)
                    failures.Add("MNK did not queue Earth's Reply when Earths Rumination was expiring");
                if (basicModeQueued)
                    failures.Add("MNK queued Earth's Reply in BasicAndChakraOvercap mode");
                if (!deadMemberQueued)
                    failures.Add("MNK did not ignore a dead party member for Earth's Reply damage detection");
                if (!zeroMaxHPMemberQueued)
                    failures.Add("MNK did not ignore a zero-MaxHP party member for Earth's Reply damage detection");
                if (!missingMemberQueued)
                    failures.Add("MNK did not ignore a missing party member for Earth's Reply damage detection");

                return new(partyAllDamagedQueued, partiallyDamagedQueued, expiringQueued, basicModeQueued, deadMemberQueued, zeroMaxHPMemberQueued, missingMemberQueued);
            }

            world.Execute(new ActorState.OpCastInfo(p1.InstanceID, new()
            {
                Action = new(ActionType.Spell, DMURevoltingRuinIIIAID),
                TargetID = player.InstanceID,
                Location = p1.PosRot.XYZ(),
                TotalTime = 5,
            }));
            dmu.Update();
            mechanicResults.Add(VerifyRoE("RevoltingRuinIII", AIHints.PredictedDamageType.Tankbuster, true, p1));

            void VerifyPhase(string phaseName, int expectedIndex, bool expectedStartsWithDowntime, bool expectedEndsWithDowntime, int expectedTargetCount, Actor target)
            {
                var activePhase = dmu.StateMachine.ActivePhase;
                var actualIndex = dmu.StateMachine.ActivePhaseIndex;
                var startsWithDowntime = activePhase?.Hint.HasFlag(StateMachine.PhaseHint.StartWithDowntime) == true;
                if (actualIndex != expectedIndex)
                    failures.Add($"DMU did not activate {phaseName}; active phase was {actualIndex}");
                if (startsWithDowntime != expectedStartsWithDowntime)
                    failures.Add($"DMU {phaseName} StartWithDowntime was {startsWithDowntime}, expected {expectedStartsWithDowntime}");

                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var targetHint = hints.PotentialTargets.FirstOrDefault(candidate => candidate.Actor.InstanceID == target.InstanceID);
                var targetPriority = targetHint?.Priority ?? AIHints.Enemy.PriorityUndesirable;
                if (hints.PotentialTargets.Count != expectedTargetCount)
                    failures.Add($"DMU {phaseName} AIHints had {hints.PotentialTargets.Count} target(s), expected {expectedTargetCount}");
                if (targetHint == null || targetPriority < 0)
                    failures.Add($"DMU {phaseName} AIHints did not expose the active target at an automatic-rotation priority");
                mnk.Execute(strategy, target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var phaseEnd = mnk.EstimatedPhaseEnd;
                var downtimeStart = mnk.EstimatedDowntimeStart;
                var burstHorizon = mnk.EstimatedBurstHorizon;
                var plannerEndsWithDowntime = downtimeStart != null;
                phases.Add(new(phaseName, actualIndex, startsWithDowntime, plannerEndsWithDowntime, hints.PotentialTargets.Count, targetPriority, phaseEnd ?? -1, downtimeStart, burstHorizon ?? -1));

                if (phaseEnd == null || burstHorizon == null)
                {
                    failures.Add($"MNK did not derive a planner horizon during {phaseName}");
                    return;
                }

                if (expectedEndsWithDowntime)
                {
                    if (downtimeStart == null)
                        failures.Add($"MNK did not identify the next DMU downtime after {phaseName}");
                    else if (Math.Abs(phaseEnd.Value - downtimeStart.Value) > 0.1f || Math.Abs(phaseEnd.Value - burstHorizon.Value) > 0.1f)
                        failures.Add($"MNK {phaseName} planner horizon did not clamp to the next DMU downtime boundary");
                }
                else if (downtimeStart != null || Math.Abs(phaseEnd.Value - burstHorizon.Value) > 0.1f)
                {
                    failures.Add($"MNK final DMU phase did not retain its full fight horizon");
                }
            }

            void VerifyDowntimeBurstHold(string transition)
            {
                var downtimePattern = TimelinePattern($"{scenario}_{transition}") with
                {
                    CombatTimer = 180,
                    EncounterHint = transition == "P2ToP3" ? EncounterHintMode.Automatic : EncounterHintMode.Boss,
                    RoEStrategy = AutoForceDelayMode.Delay
                };
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(downtimePattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var holdModule = new XanMNK(manager, player);
                holdModule.Execute(BuildStrategy(downtimePattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var coreBurstActions = hints.ActionsToExecute.Entries
                    .Where(entry => entry.Action.Type == ActionType.Spell)
                    .Select(entry => (AID)entry.Action.ID)
                    .Where(action => action is AID.RiddleOfFire or AID.Brotherhood or AID.PerfectBalance or AID.RiddleOfWind)
                    .Select(action => action.ToString())
                    .ToList();
                downtimes.Add(new(transition, hints.PotentialTargets.Count, coreBurstActions));
                var unexpectedCoreBurstActions = transition == "P2ToP3"
                    ? coreBurstActions.Where(action => action != AID.RiddleOfFire.ToString()).ToList()
                    : coreBurstActions;
                if (unexpectedCoreBurstActions.Count != 0)
                    failures.Add($"MNK queued {string.Join(", ", unexpectedCoreBurstActions)} during DMU {transition} downtime");
                if (transition == "P2ToP3" && !coreBurstActions.Contains(AID.RiddleOfFire.ToString()))
                    failures.Add("MNK did not aircast Riddle of Fire during DMU P2ToP3 downtime when it was ready");
            }

            void VerifyLongDowntimeCoreBurstHold(string transition, float elapsed, int frame)
            {
                AdvanceDMUFrame(world, elapsed, frame);
                dmu.Update();
                var downtimePattern = TimelinePattern($"{scenario}_{transition}_long_downtime") with
                {
                    CombatTimer = 180,
                    EncounterHint = EncounterHintMode.Automatic,
                    RoEStrategy = AutoForceDelayMode.Delay
                };
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(downtimePattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var holdModule = new XanMNK(manager, player);
                holdModule.Execute(BuildStrategy(downtimePattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var coreBurstActions = hints.ActionsToExecute.Entries
                    .Where(entry => entry.Action.Type == ActionType.Spell)
                    .Select(entry => (AID)entry.Action.ID)
                    .Where(action => action is AID.RiddleOfFire or AID.Brotherhood or AID.PerfectBalance or AID.RiddleOfWind)
                    .ToList();
                if (coreBurstActions.Count != 0)
                    failures.Add($"MNK queued {string.Join(", ", coreBurstActions)} after {elapsed:f1}s of DMU {transition} downtime");
            }

            void VerifyResumeGCD(string transition, Actor target)
            {
                var resumePattern = TimelinePattern($"{scenario}_{transition}_resume") with
                {
                    CombatTimer = 180,
                    RoFStrategy = AutoForceDelayMode.Delay,
                    BrotherhoodStrategy = AutoForceDelayMode.Delay,
                    PBStrategy = PBStrategyMode.Delay,
                    BlitzStrategy = BlitzStrategyMode.Delay,
                    RoWStrategy = AutoForceDelayMode.Delay,
                    RoEStrategy = AutoForceDelayMode.Delay
                };
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(resumePattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var resumeModule = new XanMNK(manager, player);
                resumeModule.Execute(BuildStrategy(resumePattern), target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var targetGCDActions = hints.ActionsToExecute.Entries
                    .Where(entry => entry.Target?.InstanceID == target.InstanceID && ActionDefinitions.Instance[entry.Action]?.IsGCD == true)
                    .Select(entry => entry.Action.ToString())
                    .ToList();
                resumes.Add(new(transition, hints.PotentialTargets.Count, targetGCDActions));
                if (targetGCDActions.Count == 0)
                    failures.Add($"MNK did not queue a target GCD after DMU {transition} target return");
            }

            void VerifyP2ToP3RoFAircast()
            {
                var pattern = TimelinePattern($"{scenario}_p2_to_p3_rof_aircast") with
                {
                    CombatTimer = 180,
                    EncounterHint = EncounterHintMode.Automatic,
                    RoFStrategy = AutoForceDelayMode.Automatic,
                    BrotherhoodStrategy = AutoForceDelayMode.Delay,
                    PBStrategy = PBStrategyMode.Delay,
                    BlitzStrategy = BlitzStrategyMode.Delay,
                    RoWStrategy = AutoForceDelayMode.Delay,
                    RoEStrategy = AutoForceDelayMode.Delay,
                    RiddleOfFireReadyIn = 0,
                    BrotherhoodReadyIn = 60,
                    PerfectBalanceReadyIn = 60
                };
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                p2ToP3RoFAircastModule = new XanMNK(manager, player);
                p2ToP3RoFAircastModule.Execute(BuildStrategy(pattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
                p2ToP3RoFAircastApplied = hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.RiddleOfFire));
                if (!p2ToP3RoFAircastApplied)
                {
                    failures.Add("MNK did not queue the stateful Riddle of Fire aircast during DMU P2ToP3 downtime");
                    return;
                }

                world.Execute(new ActorState.OpStatus(player.InstanceID, p2ToP3RoFAircastStatusIndex, new((uint)SID.RiddleOfFire, 0, world.FutureTime(20), player.InstanceID)));
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern with { RiddleOfFireReadyIn = 60 })));
            }

            void VerifyP2ToP3RoFAircastGuards()
            {
                var delayPattern = TimelinePattern($"{scenario}_p2_to_p3_rof_delay") with
                {
                    CombatTimer = 180,
                    EncounterHint = EncounterHintMode.Automatic,
                    RoFStrategy = AutoForceDelayMode.Delay,
                    BrotherhoodStrategy = AutoForceDelayMode.Delay,
                    PBStrategy = PBStrategyMode.Delay,
                    BlitzStrategy = BlitzStrategyMode.Delay,
                    RoWStrategy = AutoForceDelayMode.Delay,
                    RoEStrategy = AutoForceDelayMode.Delay,
                    RiddleOfFireReadyIn = 0,
                    BrotherhoodReadyIn = 60,
                    PerfectBalanceReadyIn = 60
                };
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(delayPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                new XanMNK(manager, player).Execute(BuildStrategy(delayPattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
                if (hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.RiddleOfFire)))
                    failures.Add("MNK queued Riddle of Fire during DMU P2ToP3 downtime while RoF strategy was Delay");

                var holdPattern = delayPattern with { Name = $"{scenario}_p2_to_p3_rof_hold", EncounterHint = EncounterHintMode.HoldBurst, RoFStrategy = AutoForceDelayMode.Automatic };
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(holdPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                new XanMNK(manager, player).Execute(BuildStrategy(holdPattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
                if (hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.RiddleOfFire)))
                    failures.Add("MNK queued Riddle of Fire during DMU P2ToP3 downtime while EncounterHint was HoldBurst");
            }

            void VerifyP2ToP3ManualOverrides()
            {
                var forceRoFPattern = TimelinePattern($"{scenario}_p2_to_p3_force_rof") with
                {
                    CombatTimer = 180,
                    EncounterHint = EncounterHintMode.HoldBurst,
                    RoFStrategy = AutoForceDelayMode.Force,
                    BrotherhoodStrategy = AutoForceDelayMode.Delay,
                    PBStrategy = PBStrategyMode.Delay,
                    BlitzStrategy = BlitzStrategyMode.Delay,
                    RoWStrategy = AutoForceDelayMode.Delay,
                    RoEStrategy = AutoForceDelayMode.Delay,
                    RiddleOfFireReadyIn = 0,
                    BrotherhoodReadyIn = 60,
                    PerfectBalanceReadyIn = 60
                };
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(forceRoFPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                new XanMNK(manager, player).Execute(BuildStrategy(forceRoFPattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
                if (!hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.RiddleOfFire)))
                    failures.Add("MNK did not preserve Force Riddle of Fire during DMU P2ToP3 downtime");

                var forcePBPattern = forceRoFPattern with { Name = $"{scenario}_p2_to_p3_force_pb", RoFStrategy = AutoForceDelayMode.Delay, PBStrategy = PBStrategyMode.Force, PerfectBalanceReadyIn = 0 };
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(forcePBPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                new XanMNK(manager, player).Execute(BuildStrategy(forcePBPattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
                if (!hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.PerfectBalance)))
                    failures.Add("MNK did not preserve Force Perfect Balance during DMU P2ToP3 downtime");

                var forceBlitzPattern = forceRoFPattern with { Name = $"{scenario}_p2_to_p3_force_blitz", RoFStrategy = AutoForceDelayMode.Delay, BlitzStrategy = BlitzStrategyMode.Force };
                AdvanceDMUFrame(world, 0, 9, BuildMonkGauge(0, [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 0, NadiFlags.Lunar | NadiFlags.Solar, 20));
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(forceBlitzPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                new XanMNK(manager, player).Execute(BuildStrategy(forceBlitzPattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
                if (hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.PhantomRush)))
                    failures.Add("MNK queued a targetless Force Phantom Rush during DMU P2ToP3 downtime");
                AdvanceDMUFrame(world, 0, 9, default(ClientState.Gauge));
            }

            void VerifyP2ToP3RoFAircastRecovery(Actor target)
            {
                if (!p2ToP3RoFAircastApplied || p2ToP3RoFAircastModule == null)
                    return;

                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                p2ToP3RoFAircastModule.Execute(BuildStrategy(TimelinePattern($"{scenario}_p2_to_p3_rof_recovery") with
                {
                    CombatTimer = 180,
                    EncounterHint = EncounterHintMode.Automatic,
                    RoFStrategy = AutoForceDelayMode.Automatic,
                    BrotherhoodStrategy = AutoForceDelayMode.Delay,
                    PBStrategy = PBStrategyMode.Delay,
                    BlitzStrategy = BlitzStrategyMode.Delay,
                    RoWStrategy = AutoForceDelayMode.Delay,
                    RoEStrategy = AutoForceDelayMode.Delay,
                    RiddleOfFireReadyIn = 60,
                    BrotherhoodReadyIn = 60,
                    PerfectBalanceReadyIn = 60
                }), target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var targetGCDQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Target?.InstanceID == target.InstanceID && ActionDefinitions.Instance[entry.Action]?.IsGCD == true);
                var rofQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.RiddleOfFire));
                if (!targetGCDQueued)
                    failures.Add("MNK did not queue a target GCD after the stateful DMU P2ToP3 Riddle of Fire aircast");
                if (rofQueued)
                    failures.Add("MNK queued Riddle of Fire again after the stateful DMU P2ToP3 aircast was active");
                world.Execute(new ActorState.OpStatus(player.InstanceID, p2ToP3RoFAircastStatusIndex, default));
            }

            var pendingBlitzPattern = TimelinePattern($"{scenario}_pending_blitz") with
            {
                CombatTimer = 180,
                RoFStrategy = AutoForceDelayMode.Delay,
                BrotherhoodStrategy = AutoForceDelayMode.Delay,
                PBStrategy = PBStrategyMode.Automatic,
                BlitzStrategy = BlitzStrategyMode.Automatic,
                RoWStrategy = AutoForceDelayMode.Delay,
                RoEStrategy = AutoForceDelayMode.Delay
            };

            static bool IsBlitzAction(AID action)
                => action is AID.ElixirField or AID.ElixirBurst or AID.RisingPhoenix or AID.FlintStrike or AID.CelestialRevolution or AID.TornadoKick or AID.PhantomRush;

            static bool IsAOEGCD(AID action)
                => action is AID.ArmOfTheDestroyer or AID.ShadowOfTheDestroyer or AID.FourPointFury or AID.Rockbreaker;

            void VerifyP3MultiTargetActions(Actor chaos, Actor exdeath, Actor kefka)
            {
                var pattern = TimelinePattern($"{scenario}_p3_multi_target") with
                {
                    CombatTimer = 180,
                    TargetCount = 3,
                    RoFStrategy = AutoForceDelayMode.Delay,
                    BrotherhoodStrategy = AutoForceDelayMode.Delay,
                    PBStrategy = PBStrategyMode.Delay,
                    BlitzStrategy = BlitzStrategyMode.Automatic,
                    RoWStrategy = AutoForceDelayMode.Delay,
                    RoEStrategy = AutoForceDelayMode.Delay,
                    RiddleOfFireReadyIn = 60,
                    BrotherhoodReadyIn = 60,
                    PerfectBalanceReadyIn = 60
                };
                var validTargets = new[] { chaos.InstanceID, exdeath.InstanceID, kefka.InstanceID };
                var opoStatusIndex = Actor.NumStatuses - 8;

                world.Execute(new ActorState.OpStatus(player.InstanceID, opoStatusIndex, new((uint)SID.OpoOpoForm, 0, world.FutureTime(20), player.InstanceID)));
                AdvanceDMUFrame(world, 0, 9, default(ClientState.Gauge));
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var aoeModule = new XanMNK(manager, player);
                aoeModule.Execute(BuildStrategy(pattern), chaos, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var aoeQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action.Type == ActionType.Spell && IsAOEGCD((AID)entry.Action.ID));
                if (aoeModule.NumAOETargets != 3 || !aoeModule.UseAOE || !aoeQueued)
                    failures.Add($"MNK did not use the three-target AOE path in DMU P3; aoe_targets={aoeModule.NumAOETargets} use_aoe={aoeModule.UseAOE} queued={string.Join(", ", hints.ActionsToExecute.Entries.Select(entry => entry.Action))}");

                world.Execute(new ActorState.OpStatus(player.InstanceID, opoStatusIndex, default));
                var rofStatusIndex = opoStatusIndex;
                world.Execute(new ActorState.OpStatus(player.InstanceID, rofStatusIndex, new((uint)SID.RiddleOfFire, 0, world.FutureTime(20), player.InstanceID)));
                AdvanceDMUFrame(world, 0, 9, BuildMonkGauge(0, [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 0, NadiFlags.Lunar | NadiFlags.Solar, 20));
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var blitzModule = new XanMNK(manager, player);
                blitzModule.Execute(BuildStrategy(pattern), chaos, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var phantomRush = hints.ActionsToExecute.Entries.FirstOrDefault(entry => entry.Action == ActionID.MakeSpell(AID.PhantomRush));
                if (blitzModule.NumBlitzTargets != 3 || phantomRush.Target == null || !validTargets.Contains(phantomRush.Target.InstanceID))
                    failures.Add($"MNK did not queue Phantom Rush on a valid three-target DMU P3 target; blitz_targets={blitzModule.NumBlitzTargets} target={phantomRush.Target?.Name ?? "<none>"} queued={string.Join(", ", hints.ActionsToExecute.Entries.Select(entry => entry.Action))}");
                world.Execute(new ActorState.OpStatus(player.InstanceID, rofStatusIndex, default));
                AdvanceDMUFrame(world, 0, 9, default(ClientState.Gauge));
            }

            var pendingModule = new XanMNK(manager, player);

            void VerifyPendingBlitzDuringDowntime()
            {
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pendingBlitzPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                pendingModule.Execute(BuildStrategy(pendingBlitzPattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var blitzQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action.Type == ActionType.Spell && IsBlitzAction((AID)entry.Action.ID));
                if (blitzQueued)
                    failures.Add("MNK queued a pending Blitz during DMU P1ToP2 downtime");
                pendingBlitz = pendingBlitz with { BlitzQueuedDuringDowntime = blitzQueued };
            }

            void VerifyPendingBlitzRecovery(Actor target)
            {
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pendingBlitzPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                pendingModule.Execute(BuildStrategy(pendingBlitzPattern), target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var recoveryBlitzActions = hints.ActionsToExecute.Entries
                    .Where(entry => entry.Action.Type == ActionType.Spell && IsBlitzAction((AID)entry.Action.ID))
                    .Select(entry => entry.Action.ToString())
                    .ToList();
                var queuedActions = hints.ActionsToExecute.Entries.Select(entry => entry.Action.ToString()).ToList();
                var pbQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.PerfectBalance));
                if (recoveryBlitzActions.Count == 0)
                    failures.Add($"MNK did not queue pending Blitz after DMU P1ToP2 target return; beast={string.Join('/', pendingModule.BeastChakra)} blitz_left={pendingModule.BlitzLeft:f2} blitz_targets={pendingModule.NumBlitzTargets} aoe_targets={pendingModule.NumAOETargets} queued {string.Join(", ", queuedActions)}");
                if (pbQueued)
                    failures.Add("MNK queued Perfect Balance before pending Blitz after DMU P1ToP2 target return");
                pendingBlitz = pendingBlitz with { RecoveryBlitzActions = recoveryBlitzActions, PBQueuedOnRecovery = pbQueued };
            }

            var pendingPBPattern = TimelinePattern($"{scenario}_pending_pb") with
            {
                CombatTimer = 180,
                RoFStrategy = AutoForceDelayMode.Delay,
                BrotherhoodStrategy = AutoForceDelayMode.Delay,
                PBStrategy = PBStrategyMode.Delay,
                BlitzStrategy = BlitzStrategyMode.Delay,
                RoWStrategy = AutoForceDelayMode.Delay,
                RoEStrategy = AutoForceDelayMode.Delay
            };

            StrategyValues PendingPBStrategy()
            {
                var strategy = BuildStrategy(pendingPBPattern);
                SetTrack(strategy, "Nadi", XanMNK.NadiStrategy.DoubleLunar);
                return strategy;
            }

            var pendingPBModule = new XanMNK(manager, player);

            void VerifyPendingPBDuringDowntime(Actor target)
            {
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pendingPBPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                pendingPBModule.Execute(PendingPBStrategy(), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var targetGCDQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Target?.InstanceID == target.InstanceID && ActionDefinitions.Instance[entry.Action]?.IsGCD == true);
                if (targetGCDQueued)
                    failures.Add("MNK queued a target GCD while Perfect Balance was held during DMU target loss");
                pendingPB = pendingPB with { TargetGCDQueuedDuringDowntime = targetGCDQueued };
            }

            void VerifyPendingPBRecovery(Actor target)
            {
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pendingPBPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                pendingPBModule.Execute(PendingPBStrategy(), target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var recoveryPBGCDs = hints.ActionsToExecute.Entries
                    .Where(entry => entry.Action == ActionID.MakeSpell(AID.DragonKick) && entry.Target?.InstanceID == target.InstanceID)
                    .Select(entry => entry.Action.ToString())
                    .ToList();
                var queuedActions = hints.ActionsToExecute.Entries.Select(entry => entry.Action.ToString()).ToList();
                var pbQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.PerfectBalance));
                if (recoveryPBGCDs.Count == 0)
                    failures.Add($"MNK did not continue Perfect Balance with Dragon Kick after DMU target return; form={pendingPBModule.CurrentForm}/{pendingPBModule.EffectiveForm} beast={string.Join('/', pendingPBModule.BeastChakra)} pb_left={pendingPBModule.PerfectBalanceLeft:f2} queued {string.Join(", ", queuedActions)}");
                if (pbQueued)
                    failures.Add("MNK queued Perfect Balance again before completing an active Perfect Balance after DMU target return");
                pendingPB = new(pendingPB.TargetGCDQueuedDuringDowntime, recoveryPBGCDs, pbQueued);
            }

            var pendingFormlessBlitzModule = new XanMNK(manager, player);

            void PrimePendingFormlessBlitzDuringDowntime()
            {
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pendingBlitzPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                pendingFormlessBlitzModule.Execute(BuildStrategy(pendingBlitzPattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
            }

            void VerifyFormlessBeforePendingBlitzRecovery(Actor target)
            {
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pendingBlitzPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                pendingFormlessBlitzModule.Execute(BuildStrategy(pendingBlitzPattern), target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var formlessRecoveryActions = hints.ActionsToExecute.Entries
                    .Where(entry => entry.Action == ActionID.MakeSpell(AID.DragonKick) && entry.Target?.InstanceID == target.InstanceID)
                    .Select(entry => entry.Action.ToString())
                    .ToList();
                var blitzQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action.Type == ActionType.Spell && IsBlitzAction((AID)entry.Action.ID));
                var queuedActions = hints.ActionsToExecute.Entries.Select(entry => entry.Action.ToString()).ToList();
                if (formlessRecoveryActions.Count == 0)
                    failures.Add($"MNK did not consume Formless Fist before the pending Blitz after DMU target return; form={pendingFormlessBlitzModule.CurrentForm}/{pendingFormlessBlitzModule.EffectiveForm} beast={string.Join('/', pendingFormlessBlitzModule.BeastChakra)} blitz_left={pendingFormlessBlitzModule.BlitzLeft:f2} queued {string.Join(", ", queuedActions)}");
                if (blitzQueued)
                    failures.Add("MNK queued a pending Blitz before consuming Formless Fist after DMU target return");
                pendingBlitz = pendingBlitz with { FormlessRecoveryActions = formlessRecoveryActions, BlitzQueuedBeforeFormless = blitzQueued };
            }

            var pendingBlitzDelayPattern = pendingBlitzPattern with { BlitzStrategy = BlitzStrategyMode.Delay };
            var pendingBlitzDelayModule = new XanMNK(manager, player);

            void PrimePendingBlitzDelayDuringDowntime()
            {
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pendingBlitzDelayPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                pendingBlitzDelayModule.Execute(BuildStrategy(pendingBlitzDelayPattern), null, estimatedAnimLockDelay: 0.05f, isMoving: false);
            }

            void VerifyPendingBlitzDelayRecovery(Actor target)
            {
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pendingBlitzDelayPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                pendingBlitzDelayModule.Execute(BuildStrategy(pendingBlitzDelayPattern), target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var blitzQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action.Type == ActionType.Spell && IsBlitzAction((AID)entry.Action.ID));
                if (blitzQueued)
                    failures.Add("MNK queued a pending Blitz after DMU target return while Blitz strategy was Delay");
                pendingBlitz = pendingBlitz with { DelayQueuedBlitzOnRecovery = blitzQueued };
            }

            void VerifyPendingBlitzBeatsSixSidedStarAtP1End()
            {
                var pattern = TimelinePattern($"{scenario}_pending_blitz_sss") with
                {
                    CombatTimer = 196,
                    Opener = OpenerRoFOffsetMode.ZeroSecondBurst,
                    RoFStrategy = AutoForceDelayMode.Delay,
                    BrotherhoodStrategy = AutoForceDelayMode.Delay,
                    PBStrategy = PBStrategyMode.Delay,
                    BlitzStrategy = BlitzStrategyMode.Automatic,
                    RoWStrategy = AutoForceDelayMode.Delay,
                    RoEStrategy = AutoForceDelayMode.Delay
                };
                world.Execute(new ActorState.OpCastInfo(p1.InstanceID, default));
                AdvanceDMUFrame(world, 196, 196, BuildMonkGauge(0, [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 0, 0, 10));
                dmu.Update();
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var sssStrategy = BuildStrategy(pattern);
                SetTrack(sssStrategy, "SixSidedStar", OffensiveStrategy.Automatic);
                var module = new XanMNK(manager, player);
                var planner = manager.Planner;
                manager.Planner = null;
                module.Execute(sssStrategy, p1, estimatedAnimLockDelay: 0.05f, isMoving: false);
                manager.Planner = planner;
                var blitzQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action.Type == ActionType.Spell && IsBlitzAction((AID)entry.Action.ID));
                var sssQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.SixSidedStar));
                if (!blitzQueued || sssQueued)
                {
                    var queuedActions = string.Join(", ", hints.ActionsToExecute.Entries.Select(entry => entry.Action));
                    failures.Add($"MNK did not keep a pending Blitz ahead of Six-Sided Star before DMU P1 downtime; blitz={blitzQueued} sss={sssQueued} beast={module.BeastCount} blitz_left={module.BlitzLeft:f2} blitz_targets={module.NumBlitzTargets} queued {queuedActions}");
                }
            }

            void VerifyP2FinalEvenBlitzBeatsFormless()
            {
                var pattern = TimelinePattern($"{scenario}_p2_final_even_blitz") with
                {
                    CombatTimer = 180,
                    EncounterHint = EncounterHintMode.Automatic,
                    PBStrategy = PBStrategyMode.Automatic,
                    BlitzStrategy = BlitzStrategyMode.Automatic,
                    RoFStrategy = AutoForceDelayMode.Automatic,
                    BrotherhoodStrategy = AutoForceDelayMode.Automatic,
                    RoWStrategy = AutoForceDelayMode.Delay,
                    RoEStrategy = AutoForceDelayMode.Delay,
                    RiddleOfFireReadyIn = 60,
                    BrotherhoodReadyIn = 60,
                    PerfectBalanceReadyIn = 60
                };
                var rofStatusIndex = Actor.NumStatuses - 7;
                var brotherhoodStatusIndex = Actor.NumStatuses - 6;
                var formlessStatusIndex = Actor.NumStatuses - 5;
                var planner = manager.Planner;
                manager.Planner = new PlanExecution(dmu, new Plan("dmu_p2_final_even_blitz", dmu.GetType()) { PhaseDurations = [1, 11.5f, 1, 1, 1] });
                world.Execute(new ActorState.OpStatus(player.InstanceID, rofStatusIndex, new((uint)SID.RiddleOfFire, 0, world.FutureTime(20), player.InstanceID)));
                world.Execute(new ActorState.OpStatus(player.InstanceID, brotherhoodStatusIndex, new((uint)SID.Brotherhood, 0, world.FutureTime(20), player.InstanceID)));
                world.Execute(new ActorState.OpStatus(player.InstanceID, formlessStatusIndex, new((uint)SID.FormlessFist, 0, world.FutureTime(20), player.InstanceID)));
                AdvanceDMUFrame(world, 0, 7, BuildMonkGauge(0, [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 0, 0, 20));
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var module = new XanMNK(manager, player);
                module.Execute(BuildStrategy(pattern), p2, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var highestPriorityGCD = hints.ActionsToExecute.Entries
                    .Where(entry => ActionDefinitions.Instance[entry.Action]?.IsGCD == true)
                    .OrderByDescending(entry => entry.Priority)
                    .FirstOrDefault();
                var highestPriorityAction = highestPriorityGCD.Action.Type == ActionType.Spell ? (AID)highestPriorityGCD.Action.ID : AID.None;
                if (!IsBlitzAction(highestPriorityAction))
                    failures.Add($"MNK did not prioritize the pending Blitz over Formless Fist in the final DMU P2 even burst; queued {string.Join(", ", hints.ActionsToExecute.Entries.Select(entry => entry.Action))}");
                world.Execute(new ActorState.OpStatus(player.InstanceID, rofStatusIndex, default));
                world.Execute(new ActorState.OpStatus(player.InstanceID, brotherhoodStatusIndex, default));
                world.Execute(new ActorState.OpStatus(player.InstanceID, formlessStatusIndex, default));
                AdvanceDMUFrame(world, 0, 7, default(ClientState.Gauge));
                manager.Planner = planner;
            }

            void VerifyP5FinalResourceBurn(Actor target)
            {
                var blitzPattern = TimelinePattern($"{scenario}_p5_final_phantom_rush") with
                {
                    CombatTimer = 180,
                    EncounterHint = EncounterHintMode.Boss,
                    RoFStrategy = AutoForceDelayMode.Delay,
                    BrotherhoodStrategy = AutoForceDelayMode.Delay,
                    PBStrategy = PBStrategyMode.Delay,
                    BlitzStrategy = BlitzStrategyMode.Automatic,
                    RoWStrategy = AutoForceDelayMode.Delay,
                    RoEStrategy = AutoForceDelayMode.Delay,
                    RiddleOfFireReadyIn = 60,
                    BrotherhoodReadyIn = 60,
                    PerfectBalanceReadyIn = 60
                };
                var planner = manager.Planner;
                manager.Planner = new PlanExecution(dmu, new Plan("dmu_p5_final_resource_burn", dmu.GetType()) { PhaseDurations = [1, 1, 1, 1, 5] });
                AdvanceDMUFrame(world, 0, 14, BuildMonkGauge(0, [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 0, NadiFlags.Lunar | NadiFlags.Solar, 20));
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(blitzPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var blitzStrategy = BuildStrategy(blitzPattern);
                SetTrack(blitzStrategy, "FightEnd", XanMNK.FightEndStrategy.Automatic);
                var blitzModule = new XanMNK(manager, player);
                blitzModule.Execute(blitzStrategy, target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var phantomRushQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.PhantomRush) && entry.Target?.InstanceID == target.InstanceID);
                if (!phantomRushQueued)
                    failures.Add($"MNK did not consume Phantom Rush in the final DMU P5 resource-burn window; queued {string.Join(", ", hints.ActionsToExecute.Entries.Select(entry => entry.Action))}");

                var raptorStatusIndex = Actor.NumStatuses - 8;
                var pbPattern = blitzPattern with { Name = $"{scenario}_p5_final_no_pb", PBStrategy = PBStrategyMode.Automatic, BlitzStrategy = BlitzStrategyMode.Delay, PerfectBalanceReadyIn = 0 };
                world.Execute(new ActorState.OpStatus(player.InstanceID, raptorStatusIndex, new((uint)SID.RaptorForm, 0, world.FutureTime(20), player.InstanceID)));
                AdvanceDMUFrame(world, 0, 14, default(ClientState.Gauge));
                world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pbPattern)));
                hintsBuilder.Update(hints, PartyState.PlayerSlot, moveImminent: false);
                var pbStrategy = BuildStrategy(pbPattern);
                SetTrack(pbStrategy, "FightEnd", XanMNK.FightEndStrategy.Automatic);
                var pbModule = new XanMNK(manager, player);
                pbModule.Execute(pbStrategy, target, estimatedAnimLockDelay: 0.05f, isMoving: false);
                var pbQueued = hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionID.MakeSpell(AID.PerfectBalance));
                if (pbQueued)
                    failures.Add("MNK started Perfect Balance despite the final DMU P5 resource-burn horizon being too short to complete it");
                world.Execute(new ActorState.OpStatus(player.InstanceID, raptorStatusIndex, default));
                AdvanceDMUFrame(world, 0, 14, default(ClientState.Gauge));
                manager.Planner = planner;
            }

            VerifyPendingBlitzBeatsSixSidedStarAtP1End();
            VerifyPhase("P1", 0, false, true, 1, p1);

            world.Execute(new ActorState.OpTargetable(p1.InstanceID, false));
            AdvanceDMUFrame(world, 1, 1, BuildMonkGauge(0, [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 0, 0, 3));
            dmu.Update();
            VerifyDowntimeBurstHold("P1ToP2");
            VerifyPendingBlitzDuringDowntime();
            world.Execute(new ActorState.OpTargetable(p2.InstanceID, true));
            world.Execute(new ActorState.OpCombat(p2.InstanceID, true));
            AdvanceDMUFrame(world, 1, 2);
            dmu.Update();
            VerifyPendingBlitzRecovery(p2);
            VerifyResumeGCD("P1ToP2", p2);
            AdvanceDMUFrame(world, 0, 2, default(ClientState.Gauge));
            VerifyPhase("P2", 1, true, true, 1, p2);
            VerifyP2FinalEvenBlitzBeatsFormless();

            world.Execute(new ActorState.OpCastInfo(p2.InstanceID, new()
            {
                Action = new(ActionType.Spell, DMUUltimateEmbraceAID),
                TargetID = player.InstanceID,
                Location = p2.PosRot.XYZ(),
                TotalTime = 5,
            }));
            dmu.Update();
            mechanicResults.Add(VerifyRoE("UltimateEmbrace", AIHints.PredictedDamageType.Tankbuster, false, p2));
            world.Execute(new ActorState.OpCastInfo(p2.InstanceID, null));
            dmu.Update();

            world.Execute(new ActorState.OpCastInfo(p2.InstanceID, new()
            {
                Action = new(ActionType.Spell, DMUForsakenAID),
                TargetID = player.InstanceID,
                Location = p2.PosRot.XYZ(),
                TotalTime = 7,
            }));
            dmu.Update();
            mechanicResults.Add(VerifyRoE("Forsaken", AIHints.PredictedDamageType.Raidwide, true, p2));
            world.Execute(new ActorState.OpCastInfo(p2.InstanceID, null));
            dmu.Update();
            mechanicHints = new(mechanicResults, mechanicFailures);
            failures.AddRange(mechanicFailures);

            const ulong earthsReplyPartyMemberID = 0x10000002;
            world.Execute(new ActorState.OpCreate(earthsReplyPartyMemberID, 0, 16, 0, "Party Member", 0, ActorType.Player, Class.DRG, 100, new Vector4(1, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
            world.Execute(new PartyState.OpModify(1, new(2, earthsReplyPartyMemberID, false)));
            var earthsReplyPartyMember = world.Actors.Find(earthsReplyPartyMemberID)!;
            earthsReply = VerifyEarthsReply(p2, earthsReplyPartyMember);
            world.Execute(new PartyState.OpModify(1, PartyState.EmptySlot));

            var pendingPBStatusIndex = Actor.NumStatuses - 2;
            var pendingPBFormStatusIndex = Actor.NumStatuses - 1;
            world.Execute(new ActorState.OpStatus(player.InstanceID, pendingPBStatusIndex, new((uint)SID.PerfectBalance, 1, world.FutureTime(8), player.InstanceID)));
            world.Execute(new ActorState.OpStatus(player.InstanceID, pendingPBFormStatusIndex, new((uint)SID.OpoOpoForm, 0, world.FutureTime(8), player.InstanceID)));
            world.Execute(new ActorState.OpTargetable(p2.InstanceID, false));
            AdvanceDMUFrame(world, 1, 3, BuildMonkGauge(0, [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 0, 0, 0));
            dmu.Update();
            VerifyPendingPBDuringDowntime(p2);
            world.Execute(new ActorState.OpTargetable(p2.InstanceID, true));
            AdvanceDMUFrame(world, 1, 4);
            dmu.Update();
            VerifyPendingPBRecovery(p2);
            world.Execute(new ActorState.OpStatus(player.InstanceID, pendingPBStatusIndex, default));
            world.Execute(new ActorState.OpStatus(player.InstanceID, pendingPBFormStatusIndex, default));
            AdvanceDMUFrame(world, 0, 4, default(ClientState.Gauge));
            dmu.Update();

            var pendingFormlessStatusIndex = Actor.NumStatuses - 3;
            world.Execute(new ActorState.OpStatus(player.InstanceID, pendingFormlessStatusIndex, new((uint)SID.FormlessFist, 0, world.FutureTime(8), player.InstanceID)));
            world.Execute(new ActorState.OpTargetable(p2.InstanceID, false));
            AdvanceDMUFrame(world, 1, 5, BuildMonkGauge(0, [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 0, 0, 6));
            dmu.Update();
            PrimePendingFormlessBlitzDuringDowntime();
            world.Execute(new ActorState.OpTargetable(p2.InstanceID, true));
            AdvanceDMUFrame(world, 1, 6, BuildMonkGauge(0, [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 0, 0, 4));
            dmu.Update();
            VerifyFormlessBeforePendingBlitzRecovery(p2);
            world.Execute(new ActorState.OpStatus(player.InstanceID, pendingFormlessStatusIndex, default));
            AdvanceDMUFrame(world, 0, 6, default(ClientState.Gauge));
            dmu.Update();

            world.Execute(new ActorState.OpTargetable(p2.InstanceID, false));
            AdvanceDMUFrame(world, 1, 7, BuildMonkGauge(0, [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 0, 0, 3));
            dmu.Update();
            PrimePendingBlitzDelayDuringDowntime();
            world.Execute(new ActorState.OpTargetable(p2.InstanceID, true));
            AdvanceDMUFrame(world, 1, 8);
            dmu.Update();
            VerifyPendingBlitzDelayRecovery(p2);
            AdvanceDMUFrame(world, 0, 8, default(ClientState.Gauge));
            dmu.Update();

            var chaos = CreateDMUEnemy(world, 0x40000003, DMUChaosP3ActorOID, 6, "Chaos", false);
            var exdeath = CreateDMUEnemy(world, 0x40000004, DMUExdeathP3ActorOID, 8, "Exdeath", false);
            var p3 = CreateDMUEnemy(world, 0x40000005, DMUKefkaP3ActorOID, 10, "Kefka P3", false);
            dmu.Update();
            world.Execute(new ActorState.OpHPMP(p2.InstanceID, new(900000, 1000000, 0, 10000, 10000)));
            world.Execute(new ActorState.OpTargetable(p2.InstanceID, false));
            AdvanceDMUFrame(world, 1, 9);
            dmu.Update();
            VerifyDowntimeBurstHold("P2ToP3");
            VerifyP2ToP3RoFAircastGuards();
            VerifyP2ToP3ManualOverrides();
            VerifyP2ToP3RoFAircast();
            world.Execute(new ActorState.OpTargetable(chaos.InstanceID, true));
            world.Execute(new ActorState.OpTargetable(exdeath.InstanceID, true));
            world.Execute(new ActorState.OpTargetable(p3.InstanceID, true));
            dmu.Update();
            VerifyP2ToP3RoFAircastRecovery(chaos);
            VerifyResumeGCD("P2ToP3", chaos);
            VerifyPhase("P3", 2, true, true, 3, chaos);
            VerifyP3MultiTargetActions(chaos, exdeath, p3);

            var p4 = CreateDMUEnemy(world, 0x40000006, DMUKefkaP4ActorOID, 12, "Kefka P4", false);
            dmu.Update();
            world.Execute(new ActorState.OpDestroy(chaos.InstanceID));
            world.Execute(new ActorState.OpDestroy(exdeath.InstanceID));
            world.Execute(new ActorState.OpDestroy(p3.InstanceID));
            AdvanceDMUFrame(world, 1, 10);
            dmu.Update();
            VerifyDowntimeBurstHold("P3ToP4");
            VerifyLongDowntimeCoreBurstHold("P3ToP4", 30, 11);
            world.Execute(new ActorState.OpTargetable(p4.InstanceID, true));
            dmu.Update();
            VerifyResumeGCD("P3ToP4", p4);
            VerifyPhase("P4", 3, true, true, 1, p4);

            var p5 = CreateDMUEnemy(world, 0x40000007, DMUKefkaP5ActorOID, 14, "Kefka P5", false);
            dmu.Update();
            world.Execute(new ActorState.OpDestroy(p4.InstanceID));
            AdvanceDMUFrame(world, 1, 12);
            dmu.Update();
            VerifyDowntimeBurstHold("P4ToP5");
            VerifyLongDowntimeCoreBurstHold("P4ToP5", 30, 13);
            world.Execute(new ActorState.OpTargetable(p5.InstanceID, true));
            dmu.Update();
            VerifyResumeGCD("P4ToP5", p5);
            VerifyPhase("P5", 4, true, false, 1, p5);
            VerifyP5FinalResourceBurn(p5);
        }
        catch (Exception ex)
        {
            failures.Add($"{ex.GetType().Name}: {ex.Message}");
        }

        return new(scenario, moduleType, mechanicHints, phases, downtimes, resumes, pendingBlitz, pendingPB, earthsReply, failures);
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

    private static RealMnkExecutionResult ExecutePattern(RealMnkPattern pattern, int sampleIndex, XanMNK.PBStrategy? pbStrategyOverride = null)
    {
        try
        {
            var world = BuildWorld(pattern, sampleIndex, out var player, out var target, out var hints);
            using var bossmods = new BossModuleManager(world);
            var db = new RotationDatabase(new DirectoryInfo("tools/mnk_real_harness/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
            using var manager = new RotationModuleManager(db, bossmods, hints)
            {
                CombatStart = world.CurrentTime.AddSeconds(-pattern.CombatTimer)
            };

            var module = new XanMNK(manager, player);
            var strategy = BuildStrategy(pattern);
            if (pbStrategyOverride is { } pbStrategy)
                SetTrack(strategy, "PB", pbStrategy);
            module.Execute(strategy, pattern.HaveTarget ? target : null, pattern.AnimationLockDelay, isMoving: false);

            var actions = hints.ActionsToExecute.Entries.Select(e =>
            {
                var definition = ActionDefinitions.Instance[e.Action];
                var readyIn = definition?.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions) ?? float.MaxValue;
                return new QueuedActionRecord(e.Action.ID, e.Action.ToString(), e.Target?.InstanceID ?? 0, e.Priority, e.Delay, readyIn);
            }).ToList();
            return new(pattern, actions, ValidatePattern(pattern, actions), null);
        }
        catch (Exception ex)
        {
            return new(pattern, [], [], ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static List<string> ValidatePattern(RealMnkPattern pattern, List<QueuedActionRecord> actions)
    {
        List<string> failures = [];
        void AssertNotQueued(bool condition, AID action, string reason)
        {
            if (condition && HasAction(actions, action, pattern.AnimationLockDelay))
                failures.Add($"{reason}: {action}");
        }

        AssertNotQueued(pattern.RoFStrategy == AutoForceDelayMode.Delay, AID.RiddleOfFire, "RoF Delay queued an action");
        AssertNotQueued(pattern.BrotherhoodStrategy == AutoForceDelayMode.Delay, AID.Brotherhood, "Brotherhood Delay queued an action");
        AssertNotQueued(pattern.PBStrategy == PBStrategyMode.Delay, AID.PerfectBalance, "PB Delay queued an action");
        AssertNotQueued(pattern.RoWStrategy == AutoForceDelayMode.Delay, AID.RiddleOfWind, "RoW Delay queued an action");
        AssertNotQueued(pattern.RoEStrategy == AutoForceDelayMode.Delay, AID.RiddleOfEarth, "RoE Delay queued an action");
        AssertNotQueued(pattern.ThunderclapStrategy == ThunderclapStrategyMode.None, AID.Thunderclap, "Thunderclap None queued an action");

        var blitzDelayed = pattern.BlitzStrategy == BlitzStrategyMode.Delay;
        foreach (var action in BlitzActions)
            AssertNotQueued(blitzDelayed, action, "Blitz Delay queued an action");

        foreach (var action in actions)
        {
            if (!ActionUnlockedAtLevel(new ActionID(action.ActionID), pattern.Level))
                failures.Add($"queued an action above level {pattern.Level}: {action.Action}");
        }

        if (pattern.TargetPriority is AIHints.Enemy.PriorityInvincible or AIHints.Enemy.PriorityForbidden
            && actions.Any(action => IsPatternEnemyTarget(pattern, action.Target)))
        {
            failures.Add("queued an enemy-targeted action for an invincible or forbidden target");
        }

        if (!AutomaticBurstBlocked(pattern))
            return failures;

        AssertNotQueued(pattern.RoFStrategy == AutoForceDelayMode.Automatic, AID.RiddleOfFire, "blocked context queued automatic RoF");
        AssertNotQueued(pattern.BrotherhoodStrategy == AutoForceDelayMode.Automatic, AID.Brotherhood, "blocked context queued automatic Brotherhood");
        AssertNotQueued(pattern.PBStrategy == PBStrategyMode.Automatic && ActionUnlockedAtLevel(AID.RiddleOfFire, pattern.Level), AID.PerfectBalance, "blocked context queued automatic PB");
        AssertNotQueued(pattern.RoWStrategy == AutoForceDelayMode.Automatic, AID.RiddleOfWind, "blocked context queued automatic RoW");
        return failures;
    }

    private static bool AutomaticBurstBlocked(RealMnkPattern pattern)
        => !pattern.HaveTarget
        || !pattern.Targetable
        || pattern.TargetPriority is AIHints.Enemy.PriorityInvincible or AIHints.Enemy.PriorityForbidden
        || pattern.RotationMode == RotationMode.BasicAndChakraOvercap
        || pattern.EncounterHint is EncounterHintMode.Trash or EncounterHintMode.BossReturn or EncounterHintMode.HoldBurst
        || pattern.LookAway && pattern.ContentId != 1094;

    private static bool HasAction(List<QueuedActionRecord> actions, AID action, float animationLockDelay)
        => actions.Any(candidate => candidate.ActionID == (uint)action && candidate.ReadyIn <= animationLockDelay + 0.1f);

    private static bool ActionUnlockedAtLevel(AID action, int level)
        => ActionDefinitions.Instance.Spell(action) is { } definition && level >= definition.MinLevel;

    private static bool ActionUnlockedAtLevel(ActionID action, int level)
        => action.Type != ActionType.Spell || ActionDefinitions.Instance[action] is not { } definition || level >= definition.MinLevel;

    private static bool IsPatternEnemyTarget(RealMnkPattern pattern, ulong target)
        => target >= 0x40000001 && target < 0x40000001 + (ulong)pattern.TargetCount;

    private static readonly AID[] BlitzActions =
    [
        AID.ElixirField,
        AID.ElixirBurst,
        AID.RisingPhoenix,
        AID.FlintStrike,
        AID.CelestialRevolution,
        AID.TornadoKick,
        AID.PhantomRush
    ];

    private static RealMnkTimelineResult ExecuteTimeline(MnkTimelineScenario scenario)
    {
        var pattern = scenario.Pattern;
        var world = BuildWorld(pattern, sampleIndex: 0, out var player, out var target, out var hints);
        world.Execute(new ClientState.OpPlayerStatsChange(new(SkillSpeedForTimelineGCD(), SkillSpeedForTimelineGCD(), MnkPatch75Data.GreasedLightningHasteModifier(100))));
        world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));

        using var bossmods = new BossModuleManager(world);
        var db = new RotationDatabase(new DirectoryInfo("tools/mnk_real_harness/.autorotation"), new FileInfo("BossMod/DefaultRotationPresets.json"));
        using var manager = new RotationModuleManager(db, bossmods, hints)
        {
            CombatStart = BaseTime
        };

        var strategy = BuildStrategy(pattern);
        if (scenario.SkillRotationOverride is { } skillRotation)
            SetTrack(strategy, "SkillRotation", skillRotation);
        SetTrack(strategy, "Nadi", scenario.NadiStrategyOverride ?? XanMNK.NadiStrategy.DoubleLunar);
        if (scenario.PBStrategyOverride is { } pbStrategy)
            SetTrack(strategy, "PB", pbStrategy);
        if (scenario.RoFStrategyOverride is { } rofStrategy)
            SetTrack(strategy, "RoF", rofStrategy);
        if (scenario.RoWStrategyOverride is { } rowStrategy)
            SetTrack(strategy, "RoW", rowStrategy);
        if (scenario.FiresReplyStrategyOverride is { } firesReplyStrategy)
            SetTrack(strategy, "FiresReply", firesReplyStrategy);
        if (scenario.WindsReplyStrategyOverride is { } windsReplyStrategy)
            SetTrack(strategy, "WindsReply", windsReplyStrategy);
        if (scenario.TrueNorthStrategyOverride is { } trueNorthStrategy)
            SetTrack(strategy, "TrueNorth", trueNorthStrategy);
        if (scenario.FormShiftStrategyOverride is { } formShiftStrategy)
            SetTrack(strategy, "FormShift", formShiftStrategy);
        if (scenario.MeditateStrategyOverride is { } meditateStrategy)
            SetTrack(strategy, "Meditate", meditateStrategy);
        SetTrack(strategy, "Pot", scenario.PotionStrategyOverride ?? XanMNK.PotionStrategy.Manual);
        var timeline = new RealMnkTimeline(world, player, target, hints, new XanMNK(manager, player), strategy, scenario);
        return timeline.Run();
    }

    private static RealMnkPattern TimelinePattern(string name)
        => GeneratedPattern(0) with
        {
            Name = name,
            CombatTimer = 0,
            Level = 100,
            Gcd = TimelineGCD,
            Opener = OpenerRoFOffsetMode.Standard78,
            BurstTiming = BurstTimingMode.Cooldown,
            EncounterHint = EncounterHintMode.Boss,
            RotationMode = RotationMode.Full,
            PBStrategy = PBStrategyMode.Automatic,
            BlitzStrategy = BlitzStrategyMode.Automatic,
            RoFStrategy = AutoForceDelayMode.Automatic,
            BrotherhoodStrategy = AutoForceDelayMode.Automatic,
            RoWStrategy = AutoForceDelayMode.Delay,
            RoEStrategy = AutoForceDelayMode.Delay,
            ThunderclapStrategy = ThunderclapStrategyMode.None,
            ContentId = 0,
            TargetCount = 1,
            Targetable = true,
            HaveTarget = true,
            CanMelee = true,
            LookAway = false,
            TargetPriority = 1,
            PredictedDamage = PredictedDamageKind.None,
            PredictedDamageSelf = false,
            PredictedDamageIn = 0,
            CurrentForm = "None",
            FormLeft = 0,
            FormlessFistLeft = 0,
            RiddleOfFireLeft = 0,
            BrotherhoodLeft = 0,
            PerfectBalanceLeft = 0,
            PerfectBalanceStacks = 0,
            FiresReplyLeft = 0,
            WindsReplyLeft = 0,
            EarthsReplyLeft = 0,
            RiddleOfEarthLeft = 0,
            RiddleOfFireReadyIn = 0,
            BrotherhoodReadyIn = 0,
            PerfectBalanceReadyIn = 0,
            RiddleOfWindReadyIn = 60,
            RiddleOfEarthReadyIn = 120,
            ThunderclapReadyIn = 30,
            TrueNorthReadyIn = 45,
            AnimationLockDelay = 0
        };

    private static MnkTimelineScenario StandardTimelineScenario()
    {
        var pattern = TimelinePattern("standard78_double_lunar");
        return new(pattern.Name, pattern, 160, [], (NadiFlags)0, [], 0,
            MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.EvenTwoPB | MnkTimelineRequirement.EvenPhantomRushInsideBuff | MnkTimelineRequirement.OpeningDoubleLunar | MnkTimelineRequirement.OddPreRoFPBRequiresOvercap);
    }

    private static IReadOnlyList<MnkTimelineScenario> TimelineSuiteScenarios(EventTriggerTimelineInput? dancingMadEventTrigger = null)
    {
        var standard = StandardTimelineScenario();
        var countdownStandardPattern = TimelinePattern("countdown_standard78_double_lunar");
        var countdownEarlyPattern = TimelinePattern("countdown_early58_double_lunar") with
        {
            Opener = OpenerRoFOffsetMode.Early58
        };
        var zeroSecondPattern = TimelinePattern("zero_second_double_lunar") with
        {
            Opener = OpenerRoFOffsetMode.ZeroSecondBurst
        };
        var partyBurstAlignedPattern = TimelinePattern("party_burst_aligned_double_lunar") with
        {
            Opener = OpenerRoFOffsetMode.PartyBurstAligned,
            BurstTiming = BurstTimingMode.SynergyFixed
        };
        var meleeSafePattern = TimelinePattern("melee_safe_standard78_double_lunar") with
        {
            BurstTiming = BurstTimingMode.MeleeSafe
        };
        var singleToAOEPattern = TimelinePattern("single_to_aoe_to_single") with
        {
            TargetCount = 4,
            RoFStrategy = AutoForceDelayMode.Delay,
            BrotherhoodStrategy = AutoForceDelayMode.Delay,
            PBStrategy = PBStrategyMode.Delay,
            BlitzStrategy = BlitzStrategyMode.Delay
        };
        var aoeToSinglePattern = singleToAOEPattern with
        {
            Name = "aoe_to_single"
        };
        var basicAndChakraOvercapPattern = TimelinePattern("basic_and_chakra_overcap") with
        {
            RotationMode = RotationMode.BasicAndChakraOvercap,
            RoWStrategy = AutoForceDelayMode.Automatic,
            RiddleOfWindReadyIn = 0
        };
        var forceCoreBurstPattern = TimelinePattern("force_core_burst") with
        {
            RoFStrategy = AutoForceDelayMode.Force,
            BrotherhoodStrategy = AutoForceDelayMode.Force,
            PBStrategy = PBStrategyMode.Force,
            RoWStrategy = AutoForceDelayMode.Delay,
            BlitzStrategy = BlitzStrategyMode.Delay
        };
        var delayCoreBurstPattern = TimelinePattern("delay_core_burst") with
        {
            RoFStrategy = AutoForceDelayMode.Delay,
            BrotherhoodStrategy = AutoForceDelayMode.Delay,
            PBStrategy = PBStrategyMode.Delay,
            RoWStrategy = AutoForceDelayMode.Delay,
            BlitzStrategy = BlitzStrategyMode.Delay
        };
        var nadiAutomaticPattern = TimelinePattern("nadi_automatic");
        var nadiLunarSolarPattern = TimelinePattern("nadi_lunar_solar");
        var nadiLunarPattern = TimelinePattern("nadi_lunar");
        var nadiSolarPattern = TimelinePattern("nadi_solar");
        var blitzForcePattern = TimelinePattern("blitz_force_pending") with
        {
            RoFStrategy = AutoForceDelayMode.Delay,
            BrotherhoodStrategy = AutoForceDelayMode.Delay,
            PBStrategy = PBStrategyMode.Delay,
            BlitzStrategy = BlitzStrategyMode.Force,
            RoWStrategy = AutoForceDelayMode.Delay
        };
        var blitzDelayPattern = blitzForcePattern with
        {
            Name = "blitz_delay_pending",
            BlitzStrategy = BlitzStrategyMode.Delay
        };
        var blitzRoFPattern = TimelinePattern("blitz_rof_pending") with
        {
            BrotherhoodStrategy = AutoForceDelayMode.Delay,
            PBStrategy = PBStrategyMode.Delay,
            BlitzStrategy = BlitzStrategyMode.RoF,
            RoWStrategy = AutoForceDelayMode.Delay
        };
        var blitzMultiPattern = blitzForcePattern with
        {
            Name = "blitz_multi_pending",
            BlitzStrategy = BlitzStrategyMode.Multi,
            TargetCount = 4
        };
        var blitzMultiRoFPattern = blitzRoFPattern with
        {
            Name = "blitz_multi_rof_pending",
            BlitzStrategy = BlitzStrategyMode.MultiRoF,
            TargetCount = 4
        };
        var forceOpoPattern = TimelinePattern("force_opo_pb") with
        {
            RoFStrategy = AutoForceDelayMode.Delay,
            BrotherhoodStrategy = AutoForceDelayMode.Delay,
            PBStrategy = PBStrategyMode.Delay,
            BlitzStrategy = BlitzStrategyMode.Delay,
            RoWStrategy = AutoForceDelayMode.Delay
        };
        var downtimeSolarPattern = forceOpoPattern with
        {
            Name = "downtime_solar_pb",
            HaveTarget = false
        };
        var downtimeLunarPattern = downtimeSolarPattern with
        {
            Name = "downtime_lunar_pb"
        };
        var forceMidWeavePattern = forceOpoPattern with
        {
            Name = "force_midweave_rof"
        };
        var lookawayPattern = TimelinePattern("lookaway_holds_enemy_actions") with
        {
            LookAway = true,
            RoWStrategy = AutoForceDelayMode.Automatic,
            RiddleOfWindReadyIn = 0
        };
        var rowImmediatePattern = TimelinePattern("row_immediate") with
        {
            RoFStrategy = AutoForceDelayMode.Delay,
            BrotherhoodStrategy = AutoForceDelayMode.Delay,
            PBStrategy = PBStrategyMode.Delay,
            BlitzStrategy = BlitzStrategyMode.Delay,
            RoWStrategy = AutoForceDelayMode.Delay,
            RiddleOfWindReadyIn = 0
        };
        var rowRoFAlignedPattern = rowImmediatePattern with
        {
            Name = "row_rof_aligned",
            RoFStrategy = AutoForceDelayMode.Automatic
        };
        var thunderclapGapClosePattern = rowImmediatePattern with
        {
            Name = "thunderclap_gap_close",
            CanMelee = false,
            ThunderclapStrategy = ThunderclapStrategyMode.GapClose,
            ThunderclapReadyIn = 0
        };
        var firesReplyPattern = rowImmediatePattern with
        {
            Name = "fires_reply_automatic",
            CurrentForm = "Raptor",
            FormLeft = 30,
            FiresReplyLeft = 8
        };
        var windsReplyPattern = firesReplyPattern with
        {
            Name = "winds_reply_force",
            FiresReplyLeft = 0,
            WindsReplyLeft = 8
        };
        var protectedReplyPattern = firesReplyPattern with
        {
            Name = "pb_protects_against_reply",
            PerfectBalanceLeft = 20,
            PerfectBalanceStacks = 3,
            PerfectBalanceReadyIn = 60
        };
        var potionNowPattern = rowImmediatePattern with
        {
            Name = "potion_now"
        };
        var potionOpenerPattern = TimelinePattern("potion_delayed_opener") with
        {
            RoWStrategy = AutoForceDelayMode.Delay
        };
        var chakraPattern = rowImmediatePattern with
        {
            Name = "chakra_overcap",
            TrueNorthReadyIn = 60
        };
        var chakraBrotherhoodPattern = chakraPattern with
        {
            Name = "chakra_brotherhood_standard",
            BrotherhoodLeft = 15
        };
        var trueNorthPattern = rowImmediatePattern with
        {
            Name = "true_north_force",
            TrueNorthReadyIn = 0
        };
        var downtimeFormShiftPattern = rowImmediatePattern with
        {
            Name = "downtime_formshift_leaping_opo",
            CanMelee = false,
            ThunderclapStrategy = ThunderclapStrategyMode.None,
            RiddleOfFireReadyIn = 30,
            BrotherhoodReadyIn = 60,
            PerfectBalanceReadyIn = 35
        };
        var thunderclapNonePattern = thunderclapGapClosePattern with
        {
            Name = "thunderclap_none",
            ThunderclapStrategy = ThunderclapStrategyMode.None
        };
        var holdBurstPattern = TimelinePattern("hold_burst_auto") with
        {
            EncounterHint = EncounterHintMode.HoldBurst,
            RoWStrategy = AutoForceDelayMode.Delay
        };
        var meditateSafePattern = rowImmediatePattern with
        {
            Name = "meditate_safe",
            HaveTarget = false,
            Targetable = false
        };
        var meditateGreedyPattern = downtimeFormShiftPattern with
        {
            Name = "meditate_greedy"
        };
        var meditateForcePattern = rowImmediatePattern with
        {
            Name = "meditate_force"
        };
        var oddRoFOutOfMeleePattern = TimelinePattern("odd_rof_recast_out_of_melee") with
        {
            BurstTiming = BurstTimingMode.MeleeSafe,
            PBStrategy = PBStrategyMode.Delay,
            BlitzStrategy = BlitzStrategyMode.Delay
        };
        var pendingPhantomRushPattern = TimelinePattern("pending_phantom_rush") with
        {
            RiddleOfFireReadyIn = 60,
            BrotherhoodReadyIn = 60,
            PerfectBalanceReadyIn = 60
        };
        var pendingRecoveryPattern = TimelinePattern("pending_resource_target_loss_resume") with
        {
            RoFStrategy = AutoForceDelayMode.Delay,
            BrotherhoodStrategy = AutoForceDelayMode.Delay,
            PBStrategy = PBStrategyMode.Delay,
            BlitzStrategy = BlitzStrategyMode.Automatic,
            RoWStrategy = AutoForceDelayMode.Delay
        };
        var dancingMadPattern = TimelinePattern("dancing_mad_p1_p2_resume") with
        {
            ContentId = 1094
        };
        var dancingMadTopLogProfilePattern = TimelinePattern("dancing_mad_top_log_profile") with
        {
            ContentId = 1094,
            Opener = OpenerRoFOffsetMode.ZeroSecondBurst,
            BurstTiming = BurstTimingMode.Cooldown,
            RoWStrategy = AutoForceDelayMode.Automatic,
            RiddleOfWindReadyIn = 0
        };

        List<MnkTimelineScenario> scenarios =
        [
            standard,
            new(countdownStandardPattern.Name, countdownStandardPattern, 40, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.DelayedFixedOpener | MnkTimelineRequirement.OpeningDoubleLunar, 15),
            new(countdownEarlyPattern.Name, countdownEarlyPattern, 40, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.DelayedFixedOpener | MnkTimelineRequirement.OpeningDoubleLunar, 15),
            new(zeroSecondPattern.Name, zeroSecondPattern, 40, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.OpeningDoubleLunar | MnkTimelineRequirement.ZeroSecondCoreBurstFastPack),
            new(dancingMadTopLogProfilePattern.Name, dancingMadTopLogProfilePattern, 130, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.OpeningDoubleLunar | MnkTimelineRequirement.DancingMadTopLogProfile,
                PotionStrategyOverride: XanMNK.PotionStrategy.OpenerAndEvenBursts,
                SkillRotationOverride: XanMNK.SkillRotationMode.DancingMad),
            new("dancing_mad_top_log_profile_target_loss", dancingMadTopLogProfilePattern with { Name = "dancing_mad_top_log_profile_target_loss" }, 40, [new(0, 8)], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ForbidCoreBurstDuringTargetLoss | MnkTimelineRequirement.ResumeGCD,
                SkillRotationOverride: XanMNK.SkillRotationMode.DancingMad),
            new(partyBurstAlignedPattern.Name, partyBurstAlignedPattern, 40, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.OpeningDoubleLunar),
            new(meleeSafePattern.Name, meleeSafePattern, 40, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.DelayedFixedOpener | MnkTimelineRequirement.OpeningDoubleLunar),
            new(singleToAOEPattern.Name, singleToAOEPattern, 35, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.DynamicAOETransition,
                TargetCountWindows: [new(0, 10, 1), new(10, 20, 4), new(20, 35, 1)]),
            new(aoeToSinglePattern.Name, aoeToSinglePattern, 35, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.DynamicAOETransition,
                TargetCountWindows: [new(0, 15, 4), new(15, 35, 1)]),
            new(basicAndChakraOvercapPattern.Name, basicAndChakraOvercapPattern, 30, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.NoAutomaticCoreBurst),
            new(forceCoreBurstPattern.Name, forceCoreBurstPattern, 15, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ForceCoreBurst),
            new(delayCoreBurstPattern.Name, delayCoreBurstPattern, 30, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.DelayCoreBurst),
            new(nadiAutomaticPattern.Name, nadiAutomaticPattern, 40, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OpeningTwoPB, NadiStrategyOverride: XanMNK.NadiStrategy.Automatic),
            new(nadiLunarSolarPattern.Name, nadiLunarSolarPattern, 40, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.OpeningLunarSolar, NadiStrategyOverride: XanMNK.NadiStrategy.LunarSolar),
            new(nadiLunarPattern.Name, nadiLunarPattern, 40, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.OpeningDoubleLunar, NadiStrategyOverride: XanMNK.NadiStrategy.Lunar),
            new(nadiSolarPattern.Name, nadiSolarPattern, 40, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OpeningTwoPB | MnkTimelineRequirement.OpeningSolar, NadiStrategyOverride: XanMNK.NadiStrategy.Solar),
            new(blitzForcePattern.Name, blitzForcePattern, 5, [], (NadiFlags)0,
                [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 15, MnkTimelineRequirement.BlitzForceImmediate),
            new(blitzDelayPattern.Name, blitzDelayPattern, 8, [], (NadiFlags)0,
                [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 15, MnkTimelineRequirement.BlitzDelay),
            new(blitzRoFPattern.Name, blitzRoFPattern, 15, [], (NadiFlags)0,
                [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 18, MnkTimelineRequirement.BlitzRoFAligned),
            new(blitzMultiPattern.Name, blitzMultiPattern, 10, [], (NadiFlags)0,
                [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 18, MnkTimelineRequirement.BlitzMultiAfterTargets,
                TargetCountWindows: [new(0, 5, 1), new(5, 10, 4)]),
            new(blitzMultiRoFPattern.Name, blitzMultiRoFPattern, 15, [], (NadiFlags)0,
                [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 18, MnkTimelineRequirement.BlitzMultiRoFAligned),
            new(forceOpoPattern.Name, forceOpoPattern, 8, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ForceOpoPB, PBStrategyOverride: XanMNK.PBStrategy.ForceOpo),
            new(downtimeSolarPattern.Name, downtimeSolarPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.DowntimePB, PBStrategyOverride: XanMNK.PBStrategy.DowntimeSolar),
            new(downtimeLunarPattern.Name, downtimeLunarPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.DowntimePB, PBStrategyOverride: XanMNK.PBStrategy.DowntimeLunar),
            new(forceMidWeavePattern.Name, forceMidWeavePattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ForceMidWeaveRoF, RoFStrategyOverride: XanMNK.RoFStrategy.ForceMidWeave),
            new(lookawayPattern.Name, lookawayPattern, 12, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.LookAwayHoldsEnemyActions),
            new("row_automatic_opening", rowImmediatePattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.RoWImmediate, RoWStrategyOverride: XanMNK.RoWStrategy.Automatic),
            new("row_force", rowImmediatePattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.RoWImmediate, RoWStrategyOverride: XanMNK.RoWStrategy.Force),
            new("row_opener_cooldown", rowImmediatePattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.RoWImmediate, RoWStrategyOverride: XanMNK.RoWStrategy.OpenerCooldown),
            new("row_opener_two_minute_adaptive", rowImmediatePattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.RoWImmediate, RoWStrategyOverride: XanMNK.RoWStrategy.OpenerTwoMinuteAdaptive),
            new("row_rof_aligned", rowRoFAlignedPattern, 15, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.RoWRoFAligned, RoWStrategyOverride: XanMNK.RoWStrategy.RoFAligned),
            new("row_opener_two_minute_then_cooldown", rowRoFAlignedPattern, 15, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.RoWRoFAligned, RoWStrategyOverride: XanMNK.RoWStrategy.OpenerTwoMinuteThenCooldown),
            new(thunderclapGapClosePattern.Name, thunderclapGapClosePattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ThunderclapGapClose),
            new(firesReplyPattern.Name, firesReplyPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.FiresReplyAutomatic, FiresReplyStrategyOverride: XanMNK.FRStrategy.Automatic),
            new("fires_reply_delay", firesReplyPattern with { Name = "fires_reply_delay" }, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ReplyDelay, FiresReplyStrategyOverride: XanMNK.FRStrategy.Delay),
            new(windsReplyPattern.Name, windsReplyPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.WindsReplyForce, WindsReplyStrategyOverride: XanMNK.WRStrategy.Force),
            new("winds_reply_delay", windsReplyPattern with { Name = "winds_reply_delay" }, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ReplyDelay, WindsReplyStrategyOverride: XanMNK.WRStrategy.Delay),
            new(protectedReplyPattern.Name, protectedReplyPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.PBProtectsAgainstReply, FiresReplyStrategyOverride: XanMNK.FRStrategy.Automatic),
            new(potionNowPattern.Name, potionNowPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.PotionNow, PotionStrategyOverride: XanMNK.PotionStrategy.Now),
            new(potionOpenerPattern.Name, potionOpenerPattern, 20, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.PotionPreBuff, CountdownSeconds: 15, PotionStrategyOverride: XanMNK.PotionStrategy.OpenerAndEvenBursts),
            new(chakraPattern.Name, chakraPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ChakraOvercap, InitialChakra: 5),
            new("chakra_basic_overcap", chakraPattern with { Name = "chakra_basic_overcap", RotationMode = RotationMode.BasicAndChakraOvercap }, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ChakraOvercap | MnkTimelineRequirement.NoAutomaticCoreBurst, InitialChakra: 5),
            new(chakraBrotherhoodPattern.Name, chakraBrotherhoodPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ChakraOvercap, InitialChakra: 5),
            new("chakra_brotherhood_overcap", chakraBrotherhoodPattern with { Name = "chakra_brotherhood_overcap" }, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ChakraOvercap, InitialChakra: 10),
            new(trueNorthPattern.Name, trueNorthPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.TrueNorthForce, TrueNorthStrategyOverride: OffensiveStrategy.Force),
            new("true_north_delay", trueNorthPattern with { Name = "true_north_delay" }, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.TrueNorthDelay, TrueNorthStrategyOverride: OffensiveStrategy.Delay),
            new(downtimeFormShiftPattern.Name, downtimeFormShiftPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.DowntimeFormShift, InitialOpoStacks: 1, FormShiftStrategyOverride: OffensiveStrategy.Automatic),
            new("downtime_formshift_delay", downtimeFormShiftPattern with { Name = "downtime_formshift_delay" }, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.FormShiftDelay, InitialOpoStacks: 1, FormShiftStrategyOverride: OffensiveStrategy.Delay),
            new("downtime_formshift_force", downtimeFormShiftPattern with { Name = "downtime_formshift_force" }, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.DowntimeFormShift, InitialOpoStacks: 1, FormShiftStrategyOverride: OffensiveStrategy.Force),
            new(thunderclapNonePattern.Name, thunderclapNonePattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ThunderclapNone),
            new(holdBurstPattern.Name, holdBurstPattern, 10, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.NoAutomaticCoreBurst),
            new(meditateSafePattern.Name, meditateSafePattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.MeditateSafe, MeditateStrategyOverride: XanMNK.MeditationStrategy.Safe),
            new(meditateGreedyPattern.Name, meditateGreedyPattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.MeditateGreedy, MeditateStrategyOverride: XanMNK.MeditationStrategy.Greedy),
            new(meditateForcePattern.Name, meditateForcePattern, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.MeditateForce, MeditateStrategyOverride: XanMNK.MeditationStrategy.Force),
            new("meditate_delay", meditateGreedyPattern with { Name = "meditate_delay" }, 5, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.MeditateDelay, MeditateStrategyOverride: XanMNK.MeditationStrategy.Delay),
            new(oddRoFOutOfMeleePattern.Name, oddRoFOutOfMeleePattern, 75, [], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.OddRoFRecastOutsideMelee, MeleeUnavailableWindows: [new(64, 70)]),
            new(pendingPhantomRushPattern.Name, pendingPhantomRushPattern, 8, [], NadiFlags.Lunar | NadiFlags.Solar,
                [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 4, MnkTimelineRequirement.PendingPhantomRush),
            new("pending_pb_target_loss_resume", pendingRecoveryPattern with { Name = "pending_pb_target_loss_resume", PerfectBalanceLeft = 20, PerfectBalanceStacks = 3 }, 10, [new(0, 3)], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ForbidCoreBurstDuringTargetLoss | MnkTimelineRequirement.ResumeGCD | MnkTimelineRequirement.ResumePendingPB),
            new("pending_blitz_target_loss_resume", pendingRecoveryPattern with { Name = "pending_blitz_target_loss_resume" }, 10, [new(0, 3)], (NadiFlags)0,
                [BeastChakraType.OpoOpo, BeastChakraType.OpoOpo, BeastChakraType.OpoOpo], 10,
                MnkTimelineRequirement.ForbidCoreBurstDuringTargetLoss | MnkTimelineRequirement.ResumeGCD | MnkTimelineRequirement.ResumePendingBlitz | MnkTimelineRequirement.NoMeditateDuringPendingAutomaticBlitz),
            new(dancingMadPattern.Name, dancingMadPattern, 220, dancingMadEventTrigger?.TargetUnavailableWindows ?? [new(197.3, 207.6)], (NadiFlags)0, [], 0,
                MnkTimelineRequirement.ForbidCoreBurstDuringTargetLoss | MnkTimelineRequirement.ResumeGCD)
        ];

        if (dancingMadEventTrigger != null)
        {
            scenarios.Add(EventTriggerDancingMadFullTimelineScenario(dancingMadEventTrigger));
            scenarios.Add(EventTriggerDancingMadTopLogProfileTimelineScenario(dancingMadEventTrigger));
        }
        return scenarios;
    }

    private static float FixedOpenerRoFOffset(OpenerRoFOffsetMode opener)
        => opener switch
        {
            OpenerRoFOffsetMode.Standard78 => 7.8f,
            OpenerRoFOffsetMode.Early58 => 5.8f,
            _ => 0
        };

    private static int SkillSpeedForTimelineGCD()
        => SkillSpeedForGCD(TimelineGCD, 100);

    private static int SkillSpeedForGCD(float gcd, int level)
    {
        var haste = MnkPatch75Data.GreasedLightningHasteModifier(level);
        var bestSpeed = 400;
        var bestDelta = float.MaxValue;
        for (var speed = 0; speed <= 6_000; ++speed)
        {
            var delta = MathF.Abs(ActionSpeed.GCDRounded(speed, haste, level) - gcd);
            if (delta < bestDelta)
            {
                bestSpeed = speed;
                bestDelta = delta;
            }
        }
        return bestSpeed;
    }

    private static WorldState BuildWorld(RealMnkPattern pattern, int sampleIndex, out Actor player, out Actor target, out AIHints hints)
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "real-harness");
        var now = BaseTime.AddSeconds(pattern.CombatTimer);
        world.Execute(new WorldState.OpFrameStart(new(now, (ulong)sampleIndex, (uint)sampleIndex, 1.0f / 60.0f, 1.0f / 60.0f, 1), default, default, default));
        world.Execute(new WorldState.OpZoneChange(0, (ushort)pattern.ContentId));

        var playerID = 0x10000001ul;
        var targetID = 0x40000001ul;
        var targetDistance = pattern.CanMelee ? 2.5f : 8.0f;
        world.Execute(new ActorState.OpCreate(playerID, 0, 0, 0, "Player", 0, ActorType.Player, Class.MNK, pattern.Level, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
        world.Execute(new PartyState.OpModify(PartyState.PlayerSlot, new(1, playerID, false)));
        world.Execute(new ActorState.OpCreate(targetID, 0x1234, 2, 0, "Target", 0, ActorType.Enemy, Class.None, 100, new Vector4(targetDistance, 0, 0, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), pattern.Targetable, false, default, default, 0));
        world.Execute(new ActorState.OpCombat(playerID, true));
        world.Execute(new ActorState.OpCombat(targetID, true));
        var effectiveGcd = (float)MnkPatch75Data.EffectiveGcd(pattern.Gcd, pattern.Level);
        var skillSpeed = SkillSpeedForGCD(effectiveGcd, pattern.Level);
        world.Execute(new ClientState.OpPlayerStatsChange(new(skillSpeed, skillSpeed, MnkPatch75Data.GreasedLightningHasteModifier(pattern.Level))));
        world.Execute(new ClientState.OpClassJobLevelsChange(BuildClassJobLevels(pattern.Level)));
        world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(pattern)));

        player = world.Actors.Find(playerID)!;
        target = world.Actors.Find(targetID)!;
        ApplyStatuses(world, player, pattern);

        hints = new AIHints();
        if (pattern.HaveTarget)
        {
            var enemy = new AIHints.Enemy(target, pattern.TargetPriority, false);
            if ((uint)target.CharacterSpawnIndex < AIHints.NumEnemies)
                hints.Enemies[target.CharacterSpawnIndex] = enemy;
            hints.PotentialTargets.Add(enemy);
            hints.HighestPotentialTargetPriority = pattern.TargetPriority;
        }

        for (var i = 1; i < pattern.TargetCount; ++i)
        {
            var id = 0x40000001ul + (ulong)i;
            var spawnIndex = 2 + i * 2;
            world.Execute(new ActorState.OpCreate(id, (uint)(0x1234 + i), spawnIndex, 0, "Target" + i, 0, ActorType.Enemy, Class.None, 100, new Vector4(targetDistance, 0, i * 1.25f, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), pattern.Targetable, false, default, default, 0));
            var add = world.Actors.Find(id)!;
            var enemy = new AIHints.Enemy(add, pattern.TargetPriority, false);
            if ((uint)add.CharacterSpawnIndex < AIHints.NumEnemies)
                hints.Enemies[add.CharacterSpawnIndex] = enemy;
            hints.PotentialTargets.Add(enemy);
        }

        if (pattern.LookAway)
            hints.ForbiddenDirections.Add((player.AngleTo(target), 180.Degrees(), default));
        if (pattern.PredictedDamage != PredictedDamageKind.None)
            hints.PredictedDamage.Add(new(BitMask.Build(pattern.PredictedDamageSelf ? 0 : 1), world.FutureTime(pattern.PredictedDamageIn), MapPredictedDamage(pattern.PredictedDamage)));

        return world;
    }

    private static WorldState BuildDMUWorld(out Actor player, out Actor p1, out Actor p2, out AIHints hints)
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "dmu-real-harness");
        world.Execute(new WorldState.OpFrameStart(new(BaseTime, 0, 0, 1.0f / 60.0f, 1.0f / 60.0f, 1), default, default, default));
        world.Execute(new WorldState.OpZoneChange(0, 1094));

        const ulong playerID = 0x10000001;
        const ulong p1ID = 0x40000001;
        const ulong p2ID = 0x40000002;
        world.Execute(new ActorState.OpCreate(playerID, 0, 0, 0, "Player", 0, ActorType.Player, Class.MNK, 100, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
        world.Execute(new PartyState.OpModify(PartyState.PlayerSlot, new(1, playerID, false)));
        world.Execute(new ActorState.OpCreate(p1ID, DMUPrimaryActorOID, 2, 0, "Kefka", 0, ActorType.Enemy, Class.None, 100, new Vector4(2.5f, 0, 0, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), true, false, default, default, 0));
        world.Execute(new ActorState.OpCreate(p2ID, DMUPhase2ActorOID, 4, 0, "Kefka P2", 0, ActorType.Enemy, Class.None, 100, new Vector4(2.5f, 0, 2.5f, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), false, false, default, default, 0));
        world.Execute(new ActorState.OpCombat(playerID, true));
        world.Execute(new ActorState.OpCombat(p1ID, true));
        world.Execute(new ClientState.OpPlayerStatsChange(new(SkillSpeedForTimelineGCD(), SkillSpeedForTimelineGCD(), MnkPatch75Data.GreasedLightningHasteModifier(100))));
        world.Execute(new ClientState.OpClassJobLevelsChange(BuildClassJobLevels(100)));
        world.Execute(new ClientState.OpCooldown(true, BuildCooldowns(TimelinePattern("dmu_planner"))));

        player = world.Actors.Find(playerID)!;
        p1 = world.Actors.Find(p1ID)!;
        p2 = world.Actors.Find(p2ID)!;
        hints = new AIHints();
        return world;
    }

    private static Actor CreateDMUEnemy(WorldState world, ulong instanceID, uint oid, int spawnIndex, string name, bool targetable)
    {
        world.Execute(new ActorState.OpCreate(instanceID, oid, spawnIndex, 0, name, 0, ActorType.Enemy, Class.None, 100, new Vector4(2.5f, 0, 0, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), targetable, false, default, default, 0));
        world.Execute(new ActorState.OpCombat(instanceID, true));
        return world.Actors.Find(instanceID)!;
    }

    private static void AdvanceDMUFrame(WorldState world, float elapsed, int frame, ClientState.Gauge? gauge = null)
    {
        var timestamp = world.CurrentTime.AddSeconds(elapsed);
        world.Execute(new WorldState.OpFrameStart(new(timestamp, (ulong)frame, (uint)frame, elapsed, elapsed, 1), TimeSpan.FromSeconds(elapsed), gauge ?? world.Client.GaugePayload, default));
    }

    private static List<(int, Cooldown)> BuildCooldowns(RealMnkPattern pattern)
    {
        List<(int, Cooldown)> cooldowns = [];
        SetCooldown(cooldowns, AID.PerfectBalance, pattern.PerfectBalanceReadyIn);
        SetCooldown(cooldowns, AID.RiddleOfFire, pattern.RiddleOfFireReadyIn);
        SetCooldown(cooldowns, AID.Brotherhood, pattern.BrotherhoodReadyIn);
        SetCooldown(cooldowns, AID.RiddleOfWind, pattern.RiddleOfWindReadyIn);
        SetCooldown(cooldowns, AID.RiddleOfEarth, pattern.RiddleOfEarthReadyIn);
        SetCooldown(cooldowns, AID.Thunderclap, pattern.ThunderclapReadyIn);
        SetCooldown(cooldowns, BossMod.ClassShared.AID.TrueNorth, pattern.TrueNorthReadyIn);
        return cooldowns;
    }

    private static void SetCooldown<T>(List<(int, Cooldown)> cooldowns, T aid, double readyIn) where T : Enum
    {
        var def = ActionDefinitions.Instance.Spell(aid);
        if (def == null || def.MainCooldownGroup < 0)
            return;

        var maxCharges = Math.Max(1, def.MaxChargesAtCap());
        var total = Math.Max(def.Cooldown * maxCharges, 1f);
        var singleCharge = total / maxCharges;
        var elapsed = readyIn <= 0
            ? total
            : Math.Max(0, singleCharge - Math.Min((float)readyIn, singleCharge));
        cooldowns.RemoveAll(cooldown => cooldown.Item1 == def.MainCooldownGroup);
        cooldowns.Add((def.MainCooldownGroup, new(elapsed, total)));
    }

    private static short[] BuildClassJobLevels(int level)
    {
        var levels = new short[ClientState.NumClassLevels];
        Array.Fill(levels, (short)level);
        return levels;
    }

    private static unsafe ClientState.Gauge BuildMonkGauge(int chakra, ReadOnlySpan<BeastChakraType> beastChakra, byte furyStacks, NadiFlags nadi, float blitzLeft, byte opoStacks = 0)
    {
        MonkGauge gauge = default;
        gauge.Chakra = (byte)Math.Clamp(chakra, 0, 10);
        gauge.BeastChakra1 = beastChakra.Length > 0 ? beastChakra[0] : BeastChakraType.None;
        gauge.BeastChakra2 = beastChakra.Length > 1 ? beastChakra[1] : BeastChakraType.None;
        gauge.BeastChakra3 = beastChakra.Length > 2 ? beastChakra[2] : BeastChakraType.None;
        gauge.BeastChakraStacks = (byte)((furyStacks & ~0x03) | Math.Clamp((int)opoStacks, 0, 3));
        gauge.Nadi = nadi;
        gauge.BlitzTimeRemaining = (ushort)Math.Clamp((int)MathF.Round(blitzLeft * 1000), 0, ushort.MaxValue);

        var raw = (ulong*)&gauge;
        return new(raw[1], sizeof(MonkGauge) > 16 ? raw[2] : 0);
    }

    private static void ApplyStatuses(WorldState world, Actor player, RealMnkPattern pattern)
    {
        var index = 0;
        void Status(SID sid, double left, ushort extra = 0)
        {
            if (left > 0 && index < Actor.NumStatuses)
                world.Execute(new ActorState.OpStatus(player.InstanceID, index++, new((uint)sid, extra, world.FutureTime(left), player.InstanceID)));
        }

        Status(SID.RiddleOfFire, pattern.RiddleOfFireLeft);
        Status(SID.Brotherhood, pattern.BrotherhoodLeft);
        Status(SID.PerfectBalance, pattern.PerfectBalanceLeft, (ushort)pattern.PerfectBalanceStacks);
        Status(SID.FormlessFist, pattern.FormlessFistLeft);
        Status(SID.FiresRumination, pattern.FiresReplyLeft);
        Status(SID.WindsRumination, pattern.WindsReplyLeft);
        Status(SID.EarthsRumination, pattern.EarthsReplyLeft);
        Status(SID.RiddleOfEarth, pattern.RiddleOfEarthLeft);
        Status(pattern.CurrentForm switch
        {
            "Opo" => SID.OpoOpoForm,
            "Raptor" => SID.RaptorForm,
            "Coeurl" => SID.CoeurlForm,
            _ => SID.None
        }, pattern.FormLeft);
    }

    private static StrategyValues BuildStrategy(RealMnkPattern pattern)
    {
        var definition = XanMNK.Definition();
        var strategy = new StrategyValues(definition.Configs);
        SetTrack(strategy, "Targeting", Targeting.Auto);
        SetTrack(strategy, "AOE", pattern.TargetCount >= 3 ? AOEStrategy.AOE : AOEStrategy.ST);
        SetTrack(strategy, "BH", MapOffensive(pattern.BrotherhoodStrategy));
        SetTrack(strategy, "BurstTiming", MapBurstTiming(pattern.BurstTiming));
        SetTrack(strategy, "RoF", MapRoF(pattern.RoFStrategy));
        SetTrack(strategy, "OpenerRoFOffset", MapOpener(pattern.Opener));
        SetTrack(strategy, "RoW", MapRoW(pattern.RoWStrategy));
        SetTrack(strategy, "RoE", MapRoE(pattern.RoEStrategy));
        SetTrack(strategy, "PB", MapPB(pattern.PBStrategy));
        SetTrack(strategy, "Nadi", pattern.NadiStrategy);
        SetTrack(strategy, "Blitz", MapBlitz(pattern.BlitzStrategy));
        SetTrack(strategy, "TC", MapThunderclap(pattern.ThunderclapStrategy));
        SetTrack(strategy, "RotationMode", pattern.RotationMode == RotationMode.BasicAndChakraOvercap ? XanMNK.RotationModeStrategy.BasicAndChakraOvercap : XanMNK.RotationModeStrategy.Automatic);
        SetTrack(strategy, "EncounterHint", MapEncounterHint(pattern.EncounterHint));
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

    private static RealMnkPattern GeneratedPattern(int index)
    {
        var openers = Enum.GetValues<OpenerRoFOffsetMode>();
        var burstTimings = Enum.GetValues<BurstTimingMode>();
        var encounters = Enum.GetValues<EncounterHintMode>();
        var rotations = Enum.GetValues<RotationMode>();
        var pbStrategies = Enum.GetValues<PBStrategyMode>();
        var nadiStrategies = Enum.GetValues<XanMNK.NadiStrategy>();
        var blitzStrategies = Enum.GetValues<BlitzStrategyMode>();
        var forceDelay = Enum.GetValues<AutoForceDelayMode>();
        var tcStrategies = Enum.GetValues<ThunderclapStrategyMode>();
        var levels = new[] { 35, 50, 52, 60, 64, 68, 70, 72, 80, 90, 96, 100 };
        var gcds = new[] { 1.93, 1.94, 1.95 };
        var targetCounts = new[] { 1, 2, 3, 4 };
        var forms = new[] { "None", "Opo", "Raptor", "Coeurl" };
        var targetPriorities = new[] { 1, 0, AIHints.Enemy.PriorityPointless, AIHints.Enemy.PriorityInvincible, AIHints.Enemy.PriorityForbidden };
        var damageKinds = new[] { PredictedDamageKind.None, PredictedDamageKind.Raidwide, PredictedDamageKind.Shared, PredictedDamageKind.Tankbuster, PredictedDamageKind.AutoAttack };
        var uniqueKey = (uint)index;
        var combatTimer = (int)(uniqueKey % 480);
        uniqueKey /= 480;
        var level = levels[(int)(uniqueKey % (uint)levels.Length)];
        uniqueKey /= (uint)levels.Length;
        var form = forms[(int)(uniqueKey % (uint)forms.Length)];
        var perfectBalanceActive = SampleChance(index, 0x0D8E8BBD, 19);

        return new(
            Name: $"real_matrix_{index:00000}",
            CombatTimer: combatTimer,
            Level: level,
            Gcd: Sample(gcds, index, 0xBD8C05B7),
            Opener: Sample(openers, index, 0x0A73A4E9),
            BurstTiming: Sample(burstTimings, index, 0xC8A4B29D),
            EncounterHint: Sample(encounters, index, 0xF3D1B671),
            RotationMode: Sample(rotations, index, 0xB1E4CD27),
            PBStrategy: Sample(pbStrategies, index, 0x3C6EF372),
            NadiStrategy: Sample(nadiStrategies, index, 0x243F6A88),
            BlitzStrategy: Sample(blitzStrategies, index, 0x9E3779B9),
            RoFStrategy: Sample(forceDelay, index, 0x7F4A7C15),
            BrotherhoodStrategy: Sample(forceDelay, index, 0x94D049BB),
            RoWStrategy: Sample(forceDelay, index, 0xD2511F53),
            RoEStrategy: Sample(forceDelay, index, 0xCD9E8D57),
            ThunderclapStrategy: Sample(tcStrategies, index, 0x165667B1),
            ContentId: SampleChance(index, 0xA0761D64, 23) ? 1094 : 0,
            TargetCount: Sample(targetCounts, index, 0xE7037ED1),
            Targetable: !SampleChance(index, 0x8EBC6AF1, 31),
            HaveTarget: !SampleChance(index, 0x589965CD, 37),
            CanMelee: !SampleChance(index, 0x1D8E4E27, 5),
            LookAway: SampleChance(index, 0xEB44ACC9, 41),
            TargetPriority: Sample(targetPriorities, index, 0xA5B85C5E),
            PredictedDamage: Sample(damageKinds, index, 0xD6E8FEB8),
            PredictedDamageSelf: !SampleChance(index, 0xC13FA9A9, 7),
            PredictedDamageIn: 1 + Sample(index, 0x91E10DA5, 10),
            CurrentForm: form,
            FormLeft: form != "None" ? 28 : 0,
            FormlessFistLeft: SampleChance(index, 0x6A09E667, 11) ? 20 : 0,
            RiddleOfFireLeft: SampleChance(index, 0xBB67AE85, 13) ? 12 : 0,
            BrotherhoodLeft: SampleChance(index, 0x3C6EF372, 17) ? 12 : 0,
            PerfectBalanceLeft: perfectBalanceActive ? 8 : 0,
            PerfectBalanceStacks: perfectBalanceActive ? 1 + Sample(index, 0xA54FF53A, 3) : 0,
            FiresReplyLeft: SampleChance(index, 0x510E527F, 71) ? 8 : 0,
            WindsReplyLeft: SampleChance(index, 0x9B05688C, 73) ? 8 : 0,
            EarthsReplyLeft: SampleChance(index, 0x1F83D9AB, 79) ? 8 : 0,
            RiddleOfEarthLeft: SampleChance(index, 0x5BE0CD19, 83) ? 8 : 0,
            RiddleOfFireReadyIn: ReadyValue(index, 0xCBBB9D5D),
            BrotherhoodReadyIn: ReadyValue(index, 0x629A292A),
            PerfectBalanceReadyIn: ReadyValue(index, 0x9159015A),
            RiddleOfWindReadyIn: ReadyValue(index, 0x152FECD8),
            RiddleOfEarthReadyIn: ReadyValue(index, 0x67332667),
            ThunderclapReadyIn: ReadyValue(index, 0x8EB44A87),
            TrueNorthReadyIn: ReadyValue(index, 0xDB0C2E0D),
            AnimationLockDelay: Sample(index, 0x47B5481D, 4) * 0.05f);
    }

    private static T Sample<T>(T[] values, int index, uint salt)
        => values[Sample(index, salt, values.Length)];

    private static int Sample(int index, uint salt, int count)
        => (int)(SampleHash(index, salt) % (uint)count);

    private static bool SampleChance(int index, uint salt, int denominator)
        => Sample(index, salt, denominator) == 0;

    private static uint SampleHash(int index, uint salt)
    {
        var value = unchecked((uint)index) + salt + 0x9E3779B9;
        value = unchecked((value ^ value >> 16) * 0x7FEB352D);
        value = unchecked((value ^ value >> 15) * 0x846CA68B);
        return value ^ value >> 16;
    }

    private static double ReadyValue(int index, uint salt)
    {
        var values = new[] { 0d, 1d, 5d, 10d, 20d, 40d, 60d };
        return Sample(values, index, salt);
    }

    private static XanMNK.BurstTimingStrategy MapBurstTiming(BurstTimingMode mode)
        => mode switch
        {
            BurstTimingMode.Cooldown => XanMNK.BurstTimingStrategy.Cooldown,
            BurstTimingMode.MeleeSafe => XanMNK.BurstTimingStrategy.MeleeSafe,
            _ => XanMNK.BurstTimingStrategy.SynergyFixed
        };

    private static XanMNK.OpenerRoFOffsetStrategy MapOpener(OpenerRoFOffsetMode mode)
        => mode switch
        {
            OpenerRoFOffsetMode.Early58 => XanMNK.OpenerRoFOffsetStrategy.Early58,
            OpenerRoFOffsetMode.ZeroSecondBurst => XanMNK.OpenerRoFOffsetStrategy.ZeroSecondBurst,
            OpenerRoFOffsetMode.PartyBurstAligned => XanMNK.OpenerRoFOffsetStrategy.PartyBurstAligned,
            _ => XanMNK.OpenerRoFOffsetStrategy.Standard78
        };

    private static OffensiveStrategy MapOffensive(AutoForceDelayMode mode)
        => mode switch
        {
            AutoForceDelayMode.Force => OffensiveStrategy.Force,
            AutoForceDelayMode.Delay => OffensiveStrategy.Delay,
            _ => OffensiveStrategy.Automatic
        };

    private static XanMNK.RoFStrategy MapRoF(AutoForceDelayMode mode)
        => mode switch
        {
            AutoForceDelayMode.Force => XanMNK.RoFStrategy.Force,
            AutoForceDelayMode.Delay => XanMNK.RoFStrategy.Delay,
            _ => XanMNK.RoFStrategy.Automatic
        };

    private static XanMNK.RoWStrategy MapRoW(AutoForceDelayMode mode)
        => mode switch
        {
            AutoForceDelayMode.Force => XanMNK.RoWStrategy.Force,
            AutoForceDelayMode.Delay => XanMNK.RoWStrategy.Delay,
            _ => XanMNK.RoWStrategy.Automatic
        };

    private static XanMNK.RoEStrategy MapRoE(AutoForceDelayMode mode)
        => mode == AutoForceDelayMode.Delay ? XanMNK.RoEStrategy.Delay : XanMNK.RoEStrategy.Automatic;

    private static XanMNK.PBStrategy MapPB(PBStrategyMode mode)
        => mode switch
        {
            PBStrategyMode.Force => XanMNK.PBStrategy.Force,
            PBStrategyMode.Delay => XanMNK.PBStrategy.Delay,
            _ => XanMNK.PBStrategy.Automatic
        };

    private static XanMNK.BlitzStrategy MapBlitz(BlitzStrategyMode mode)
        => mode switch
        {
            BlitzStrategyMode.RoF => XanMNK.BlitzStrategy.RoF,
            BlitzStrategyMode.Multi => XanMNK.BlitzStrategy.Multi,
            BlitzStrategyMode.MultiRoF => XanMNK.BlitzStrategy.MultiRoF,
            BlitzStrategyMode.Force => XanMNK.BlitzStrategy.Force,
            BlitzStrategyMode.Delay => XanMNK.BlitzStrategy.Delay,
            _ => XanMNK.BlitzStrategy.Automatic
        };

    private static XanMNK.TCStrategy MapThunderclap(ThunderclapStrategyMode mode)
        => mode == ThunderclapStrategyMode.GapClose ? XanMNK.TCStrategy.GapClose : XanMNK.TCStrategy.None;

    private static XanMNK.MNKEncounterHintStrategy MapEncounterHint(EncounterHintMode mode)
        => mode switch
        {
            EncounterHintMode.Boss => XanMNK.MNKEncounterHintStrategy.Boss,
            EncounterHintMode.Trash => XanMNK.MNKEncounterHintStrategy.Trash,
            EncounterHintMode.BossReturn => XanMNK.MNKEncounterHintStrategy.BossReturn,
            EncounterHintMode.HoldBurst => XanMNK.MNKEncounterHintStrategy.HoldBurst,
            _ => XanMNK.MNKEncounterHintStrategy.Automatic
        };

    private static AIHints.PredictedDamageType MapPredictedDamage(PredictedDamageKind kind)
        => kind switch
        {
            PredictedDamageKind.Raidwide => AIHints.PredictedDamageType.Raidwide,
            PredictedDamageKind.Shared => AIHints.PredictedDamageType.Shared,
            PredictedDamageKind.Tankbuster => AIHints.PredictedDamageType.Tankbuster,
            _ => AIHints.PredictedDamageType.None
        };

    private sealed class RealMnkTimeline
    {
        private const double RiddleOfFireDuration = MnkPatch75Data.RiddleOfFireDuration;
        private const double BrotherhoodDuration = MnkPatch75Data.BrotherhoodDuration;
        private const double PerfectBalanceDuration = MnkPatch75Data.PerfectBalanceDuration;
        private const double RiddleOfWindDuration = MnkPatch75Data.RiddleOfWindDuration;
        private const double FiresReplyDuration = MnkPatch75Data.FiresReplyDuration;
        private const double WindsReplyDuration = MnkPatch75Data.WindsReplyDuration;
        private const double FormDuration = 30;
        private const double FormlessFistDuration = 30;
        private const double BlitzDuration = 20;
        private const double GCDGapLimit = 5;

        private readonly WorldState _world;
        private readonly Actor _player;
        private readonly Actor _target;
        private readonly List<Actor> _additionalTargets;
        private readonly AIHints _hints;
        private readonly XanMNK _module;
        private readonly StrategyValues _strategy;
        private readonly MnkTimelineScenario _scenario;
        private readonly TimelineState _state = new();
        private readonly double _effectiveGcd;

        public RealMnkTimeline(WorldState world, Actor player, Actor target, AIHints hints, XanMNK module, StrategyValues strategy, MnkTimelineScenario scenario)
        {
            _world = world;
            _player = player;
            _target = target;
            _additionalTargets = _world.Actors
                .Where(actor => actor.Type == ActorType.Enemy && actor.InstanceID != _target.InstanceID)
                .OrderBy(actor => actor.InstanceID)
                .ToList();
            _hints = hints;
            _module = module;
            _strategy = strategy;
            _scenario = scenario;
            _effectiveGcd = MnkPatch75Data.EffectiveGcd(scenario.Pattern.Gcd, scenario.Pattern.Level);
            _state.TargetAvailable = scenario.Pattern.HaveTarget && scenario.Pattern.Targetable;
            _state.ActiveTargetCount = _state.TargetAvailable ? 1 : 0;
            _state.Chakra = scenario.InitialChakra;
            _state.OpoStacks = (byte)scenario.InitialOpoStacks;
            _state.Nadi = scenario.InitialNadi;
            _state.BeastChakra.AddRange(scenario.InitialBeastChakra);
            _state.CurrentForm = scenario.Pattern.CurrentForm switch
            {
                "Opo" => XanMNK.Form.OpoOpo,
                "Raptor" => XanMNK.Form.Raptor,
                "Coeurl" => XanMNK.Form.Coeurl,
                _ => XanMNK.Form.None
            };
            _state.FormUntil = scenario.Pattern.FormLeft;
            _state.FormlessFistUntil = scenario.Pattern.FormlessFistLeft;
            _state.RiddleOfFireUntil = scenario.Pattern.RiddleOfFireLeft;
            _state.RiddleOfWindUntil = 0;
            _state.BrotherhoodUntil = scenario.Pattern.BrotherhoodLeft;
            _state.PerfectBalanceUntil = scenario.Pattern.PerfectBalanceLeft;
            _state.PerfectBalanceStacks = scenario.Pattern.PerfectBalanceStacks;
            _state.FiresReplyUntil = scenario.Pattern.FiresReplyLeft;
            _state.WindsReplyUntil = scenario.Pattern.WindsReplyLeft;
            _state.EarthsReplyUntil = scenario.Pattern.EarthsReplyLeft;
            _state.RiddleOfEarthUntil = scenario.Pattern.RiddleOfEarthLeft;
            _state.BlitzUntil = scenario.InitialBlitzLeft > 0 ? scenario.InitialBlitzLeft : 0;
            _state.LastGCDAt = -_effectiveGcd;
        }

        public RealMnkTimelineResult Run()
        {
            var frames = (int)MathF.Round((_scenario.Duration + _scenario.CountdownSeconds) / TimelineStep);
            for (var frame = 0; frame <= frames; ++frame)
            {
                AdvanceFrame(frame);
                UpdateCombatState();
                UpdateTargetAvailability();
                UpdateMeleeAvailability();
                UpdateTargetCount();
                ExpireResources();
                SyncStatuses();
                var primaryTarget = RefreshHints();
                _module.Execute(_strategy, primaryTarget?.Actor, estimatedAnimLockDelay: 0.05f, isMoving: false);
                CaptureCoreBurstQueue();
                CapturePotionQueue();

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
                _state.PerfectBalances,
                _state.Blitzes,
                _state.PhantomRushes,
                [.. _state.Actions],
                [.. _state.CoreBurstQueue],
                [.. _state.Failures]);
        }

        private void AdvanceFrame(int frame)
        {
            _state.Frame = frame;
            _state.Time = -_scenario.CountdownSeconds + frame * TimelineStep;
            var timestamp = BaseTime.AddSeconds(_state.Time);
            var elapsed = frame == 0 ? 0 : TimelineStep;
            _world.Execute(new WorldState.OpFrameStart(new(timestamp, (ulong)frame, (uint)frame, elapsed, elapsed, 1), TimeSpan.FromSeconds(elapsed), BuildMonkGauge(_state.Chakra, _state.BeastChakra.ToArray(), _state.FuryStacks, _state.Nadi, Left(_state.BlitzUntil), _state.OpoStacks), default));
        }

        private void UpdateCombatState()
        {
            var inCombat = _state.Time >= 0;
            if (_player.InCombat != inCombat)
                _world.Execute(new ActorState.OpCombat(_player.InstanceID, inCombat));

            float? countdown = _state.Time < 0 ? (float)-_state.Time : null;
            if (_world.Client.CountdownRemaining != countdown)
                _world.Execute(new ClientState.OpCountdownChange(countdown));
        }

        private void UpdateTargetAvailability()
        {
            var available = _scenario.Pattern.HaveTarget
                && _scenario.Pattern.Targetable
                && !_scenario.TargetUnavailableWindows.Any(window => window.Contains(_state.Time));
            if (available == _state.TargetAvailable)
                return;

            _state.TargetAvailable = available;
            _world.Execute(new ActorState.OpTargetable(_target.InstanceID, available));
            if (available)
            {
                _state.TargetReturnedAt = _state.Time;
                _state.TargetReturnTimes.Add(_state.Time);
                _state.EmptyReadyTime = 0;
                _state.GCDGapReported = false;
            }
        }

        private void UpdateMeleeAvailability()
        {
            var meleeAvailable = (_scenario.Pattern.CanMelee || _state.ThunderclapLanded)
                && !(_scenario.MeleeUnavailableWindows?.Any(window => window.Contains(_state.Time)) ?? false);
            if (meleeAvailable == _state.MeleeAvailable)
                return;

            _state.MeleeAvailable = meleeAvailable;
            var targetDistance = meleeAvailable ? 2.5f : 8.0f;
            _world.Execute(new ActorState.OpMove(_target.InstanceID, new Vector4(targetDistance, 0, 0, MathF.PI)));
        }

        private void UpdateTargetCount()
        {
            var count = _scenario.Pattern.TargetCount;
            if (_scenario.TargetCountWindows != null)
            {
                foreach (var window in _scenario.TargetCountWindows)
                {
                    if (!window.Contains(_state.Time))
                        continue;

                    count = window.Count;
                    break;
                }
            }

            count = _state.TargetAvailable ? Math.Clamp(count, 1, _additionalTargets.Count + 1) : 0;
            _state.ActiveTargetCount = count;
            for (var index = 0; index < _additionalTargets.Count; ++index)
            {
                var targetable = index < count - 1;
                if (_additionalTargets[index].IsTargetable != targetable)
                    _world.Execute(new ActorState.OpTargetable(_additionalTargets[index].InstanceID, targetable));
            }
        }

        private void ExpireResources()
        {
            if (_state.PerfectBalanceUntil > 0 && _state.PerfectBalanceUntil <= _state.Time)
            {
                if (_state.PerfectBalanceStacks > 0 && _state.TargetAvailable)
                    AddFailure($"{_state.Time:f2}: Perfect Balance expired with {_state.PerfectBalanceStacks} GCDs remaining");
                _state.PerfectBalanceUntil = 0;
                _state.PerfectBalanceStacks = 0;
                if (_state.BeastChakra.Count < 3)
                    _state.BeastChakra.Clear();
            }

            if (_state.BlitzUntil > 0 && _state.BlitzUntil <= _state.Time)
            {
                if (_state.BeastChakra.Count == 3 && _state.TargetAvailable)
                    AddFailure($"{_state.Time:f2}: Masterful Blitz expired while target was available");
                _state.BlitzUntil = 0;
                _state.BeastChakra.Clear();
            }
        }

        private AIHints.Enemy? RefreshHints()
        {
            _hints.Clear();
            if (!_state.TargetAvailable)
                return null;

            if (_scenario.Pattern.LookAway)
                _hints.ForbiddenDirections.Add((_player.AngleTo(_target), 180.Degrees(), default));

            AIHints.Enemy AddTarget(Actor actor)
            {
                var enemy = new AIHints.Enemy(actor, _scenario.Pattern.TargetPriority, false);
                if ((uint)actor.CharacterSpawnIndex < AIHints.NumEnemies)
                    _hints.Enemies[actor.CharacterSpawnIndex] = enemy;
                _hints.PotentialTargets.Add(enemy);
                return enemy;
            }

            var enemy = AddTarget(_target);
            foreach (var target in _additionalTargets)
                if (target.IsTargetable && !target.IsDead)
                    AddTarget(target);
            _hints.HighestPotentialTargetPriority = _scenario.Pattern.TargetPriority;
            return enemy;
        }

        private void SyncStatuses()
        {
            var formStatus = _state.CurrentForm switch
            {
                XanMNK.Form.OpoOpo => SID.OpoOpoForm,
                XanMNK.Form.Raptor => SID.RaptorForm,
                XanMNK.Form.Coeurl => SID.CoeurlForm,
                _ => SID.None
            };
            SyncActorStatuses(_player,
            [
                new((uint)SID.RiddleOfFire, _state.RiddleOfFireUntil, 0),
                new((uint)SID.RiddleOfWind, _state.RiddleOfWindUntil, 0),
                new((uint)SID.Brotherhood, _state.BrotherhoodUntil, 0),
                new((uint)SID.PerfectBalance, _state.PerfectBalanceUntil, (ushort)_state.PerfectBalanceStacks),
                new((uint)SID.FormlessFist, _state.FormlessFistUntil, 0),
                new((uint)SID.FiresRumination, _state.FiresReplyUntil, 0),
                new((uint)SID.WindsRumination, _state.WindsReplyUntil, 0),
                new((uint)SID.EarthsRumination, _state.EarthsReplyUntil, 0),
                new((uint)SID.RiddleOfEarth, _state.RiddleOfEarthUntil, 0),
                new((uint)formStatus, _state.FormUntil, 0)
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

            var definition = ActionDefinitions.Instance[entry.Action];
            if (definition == null)
                return false;

            var readyIn = definition.ReadyIn(_world.Client.Cooldowns, _world.Client.DutyActions);
            var startDelay = MathF.Max(entry.Delay, MathF.Max(_world.Client.AnimationLock, readyIn));
            if (startDelay > TimelineStep * 0.5f)
                return false;

            ExecuteAction(entry, definition, readyIn);
            return true;
        }

        private void CaptureCoreBurstQueue()
        {
            foreach (var entry in _hints.ActionsToExecute.Entries)
            {
                if (entry.Action.Type != ActionType.Spell)
                    continue;

                var action = (AID)entry.Action.ID;
                if (action is not (AID.PerfectBalance or AID.Brotherhood or AID.RiddleOfFire))
                    continue;

                var definition = ActionDefinitions.Instance[entry.Action];
                var readyIn = definition?.ReadyIn(_world.Client.Cooldowns, _world.Client.DutyActions) ?? float.MaxValue;
                _state.CoreBurstQueue.Add(new((float)_state.Time, action.ToString(), entry.Priority, entry.Delay, readyIn));
            }
        }

        private void CapturePotionQueue()
        {
            if (_hints.ActionsToExecute.Entries.Any(entry => entry.Action == ActionDefinitions.IDPotionStr))
                _state.PotionQueueTimes.Add(_state.Time);
        }

        private void ExecuteAction(ActionQueue.Entry entry, ActionDefinition definition, float readyIn)
        {
            var action = entry.Action.Type == ActionType.Spell ? (AID)entry.Action.ID : AID.None;
            var gcd = definition.IsGCD;
            var perfectBalanceCapIn = ActionDefinitions.Instance.Spell(AID.PerfectBalance)?.ChargeCapIn(_world.Client.Cooldowns, _world.Client.DutyActions, _player.Level) ?? float.MaxValue;
            if (gcd)
            {
                StartGCD();
                ++_state.GCDActions;
                _state.LastGCDAt = _state.Time;
                _state.GCDGapReported = false;
            }
            else
            {
                StartActionCooldown(definition);
                ++_state.OGCDActions;
            }

            _world.Execute(new ClientState.OpAnimationLockChange(definition.InstantAnimLock));
            ApplyAction(action, gcd);
            var sequence = (uint)_state.ExecutedActions.Count + 1;
            _world.Execute(new ActorState.OpCastEvent(
                _player.InstanceID,
                new ActorCastEvent(entry.Action, entry.Target?.InstanceID ?? _player.InstanceID, definition.InstantAnimLock, 1, default, sequence, sequence, _player.Rotation)));
            _state.ExecutedActions.Add(new(_state.Time, action, gcd, entry.Target?.InstanceID ?? 0, _state.TargetAvailable, perfectBalanceCapIn, _state.ActiveTargetCount, entry.Action));
            _state.Actions.Add(new(
                (float)_state.Time,
                action.ToString(),
                gcd,
                readyIn,
                _state.BeastChakra.Count,
                _state.Nadi,
                Left(_state.PerfectBalanceUntil),
                Left(_state.BlitzUntil),
                Left(_state.RiddleOfFireUntil),
                Left(_state.BrotherhoodUntil),
                _state.TargetAvailable));
        }

        private void ApplyAction(AID action, bool gcd)
        {
            switch (action)
            {
                case AID.RiddleOfFire:
                    _state.RiddleOfFireUntil = _state.Time + RiddleOfFireDuration;
                    if (_player.Level >= 100)
                        _state.FiresReplyUntil = _state.Time + FiresReplyDuration;
                    break;
                case AID.RiddleOfWind:
                    _state.RiddleOfWindUntil = _state.Time + RiddleOfWindDuration;
                    if (_player.Level >= 96)
                        _state.WindsReplyUntil = _state.Time + WindsReplyDuration;
                    break;
                case AID.Thunderclap:
                    _state.ThunderclapLanded = true;
                    break;
                case AID.Brotherhood:
                    _state.BrotherhoodUntil = _state.Time + BrotherhoodDuration;
                    break;
                case AID.PerfectBalance:
                    _state.PerfectBalanceUntil = _state.Time + PerfectBalanceDuration;
                    _state.PerfectBalanceStacks = 3;
                    ++_state.PerfectBalances;
                    break;
                case AID.FormShift:
                    _state.FormlessFistUntil = _state.Time + FormlessFistDuration;
                    break;
                case AID.FiresReply:
                    _state.FiresReplyUntil = 0;
                    break;
                case AID.WindsReply:
                    _state.WindsReplyUntil = 0;
                    break;
                case AID.EarthsReply:
                    _state.EarthsReplyUntil = 0;
                    break;
            }

            if (IsBlitz(action))
                ResolveBlitz(action);
            else if (gcd && TryGetBeastChakra(action, out var beast))
                ResolveFormGCD(action, beast);

            if (IsChakraAction(action))
                _state.Chakra = Math.Max(0, _state.Chakra - 5);

            if (!_state.TargetAvailable && IsCoreBurstAction(action))
                ++_state.CoreBurstActionsDuringTargetLoss;
        }

        private void ResolveFormGCD(AID action, BeastChakraType beast)
        {
            UpdateForm(action, beast);
            if (_state.PerfectBalanceUntil <= _state.Time || _state.PerfectBalanceStacks <= 0)
                return;

            if (_state.BeastChakra.Count < 3)
                _state.BeastChakra.Add(beast);
            _state.PerfectBalanceStacks = Math.Max(0, _state.PerfectBalanceStacks - 1);
            if (_state.PerfectBalanceStacks == 0)
                _state.PerfectBalanceUntil = 0;
            if (_state.BeastChakra.Count == 3)
                _state.BlitzUntil = _state.Time + BlitzDuration;
        }

        private void ResolveBlitz(AID action)
        {
            ++_state.Blitzes;
            _state.BeastChakra.Clear();
            _state.BlitzUntil = 0;
            switch (action)
            {
                case AID.ElixirField:
                case AID.ElixirBurst:
                    _state.Nadi |= NadiFlags.Lunar;
                    break;
                case AID.RisingPhoenix:
                case AID.FlintStrike:
                    _state.Nadi |= NadiFlags.Solar;
                    break;
                case AID.CelestialRevolution:
                    if ((_state.Nadi & NadiFlags.Lunar) == 0)
                        _state.Nadi |= NadiFlags.Lunar;
                    else if ((_state.Nadi & NadiFlags.Solar) == 0)
                        _state.Nadi |= NadiFlags.Solar;
                    break;
                case AID.PhantomRush:
                    ++_state.PhantomRushes;
                    _state.Nadi = (NadiFlags)0;
                    break;
                case AID.TornadoKick:
                    _state.Nadi = (NadiFlags)0;
                    break;
            }
        }

        private void UpdateForm(AID action, BeastChakraType beast)
        {
            _state.CurrentForm = beast switch
            {
                BeastChakraType.OpoOpo => XanMNK.Form.Raptor,
                BeastChakraType.Raptor => XanMNK.Form.Coeurl,
                BeastChakraType.Coeurl => XanMNK.Form.OpoOpo,
                _ => _state.CurrentForm
            };
            _state.FormUntil = _state.Time + FormDuration;

            var value = beast switch
            {
                BeastChakraType.OpoOpo => GetFury(0),
                BeastChakraType.Raptor => GetFury(2),
                BeastChakraType.Coeurl => GetFury(4),
                _ => 0
            };
            var gain = action is AID.DragonKick or AID.TwinSnakes or AID.Demolish;
            var coeurl = beast == BeastChakraType.Coeurl;
            SetFury(beast switch
            {
                BeastChakraType.OpoOpo => 0,
                BeastChakraType.Raptor => 2,
                BeastChakraType.Coeurl => 4,
                _ => 0
            }, gain ? Math.Min(3, value + (coeurl ? 2 : 1)) : Math.Max(0, value - 1));
        }

        private int GetFury(int shift) => (_state.FuryStacks >> shift) & 3;

        private void SetFury(int shift, int value)
            => _state.FuryStacks = (byte)((_state.FuryStacks & ~(3 << shift)) | (Math.Clamp(value, 0, 3) << shift));

        private void StartGCD()
            => _world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.GCDGroup, new Cooldown(0, (float)_effectiveGcd))]));

        private void StartActionCooldown(ActionDefinition definition)
        {
            StartActionCooldownGroup(definition.MainCooldownGroup, definition.Cooldown, definition.MaxChargesAtLevel(_player.Level), definition.MaxChargesAtCap());
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
            if (!_player.InCombat
                || !_state.TargetAvailable
                || _scenario.Pattern.LookAway
                || _scenario.Requirements.HasFlag(MnkTimelineRequirement.MeditateDelay))
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
            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.OpeningTwoPB) && CountActions(AID.PerfectBalance, 0, 35) < 2)
                AddFailure("timeline did not execute two Perfect Balance uses in the opening burst");

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.OpeningDoubleLunar))
            {
                var openingLunarBlitzes = _state.ExecutedActions.Count(action => action.Time is >= 0 and <= 35
                    && (action.Action == AID.ElixirField || action.Action == AID.ElixirBurst));
                var openingSolarBlitz = _state.ExecutedActions.Any(action => action.Time is >= 0 and <= 35
                    && (action.Action == AID.FlintStrike || action.Action == AID.RisingPhoenix));
                if (openingLunarBlitzes < 2 || openingSolarBlitz)
                    AddFailure("timeline did not keep the DoubleLunar opener on two lunar Blitzes");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.OpeningLunarSolar))
            {
                var openingLunar = _state.ExecutedActions.Any(action => action.Time is >= 0 and <= 35
                    && (action.Action == AID.ElixirField || action.Action == AID.ElixirBurst));
                var openingSolar = _state.ExecutedActions.Any(action => action.Time is >= 0 and <= 35
                    && (action.Action == AID.FlintStrike || action.Action == AID.RisingPhoenix));
                if (!openingLunar || !openingSolar)
                    AddFailure("timeline did not keep the LunarSolar opener on lunar and solar Blitzes");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.OpeningSolar))
            {
                var openingSolarBlitzes = _state.ExecutedActions.Count(action => action.Time is >= 0 and <= 35
                    && (action.Action == AID.FlintStrike || action.Action == AID.RisingPhoenix));
                var openingLunarBlitz = _state.ExecutedActions.Any(action => action.Time is >= 0 and <= 35
                    && (action.Action == AID.ElixirField || action.Action == AID.ElixirBurst));
                if (openingSolarBlitzes < 2 || openingLunarBlitz)
                    AddFailure("timeline did not keep the Solar opener on two solar Blitzes");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.EvenTwoPB) && CountActions(AID.PerfectBalance, 115, 155) < 2)
                AddFailure("timeline did not execute two Perfect Balance uses in the first non-opener even burst");

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.EvenPhantomRushInsideBuff))
            {
                var phantomRush = _state.Actions.FirstOrDefault(action => action.Action == nameof(AID.PhantomRush) && action.Time is >= 115 and <= 155);
                if (phantomRush == null
                    || phantomRush.RiddleOfFireLeft <= TimelineGCD
                    || phantomRush.BrotherhoodLeft <= TimelineGCD)
                {
                    AddFailure("timeline did not use the first non-opener Phantom Rush inside overlapping Riddle of Fire and Brotherhood");
                }
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.PendingPhantomRush)
                && !_state.ExecutedActions.Any(action => action.Action == AID.PhantomRush && action.Time <= _scenario.InitialBlitzLeft))
                AddFailure("timeline did not consume pending Phantom Rush before its Blitz deadline");

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ForbidCoreBurstDuringTargetLoss) && _state.CoreBurstActionsDuringTargetLoss > 0)
                AddFailure($"timeline executed {_state.CoreBurstActionsDuringTargetLoss} core burst action(s) while target was unavailable");

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ResumeGCD))
            {
                foreach (var returnTime in _state.TargetReturnTimes)
                {
                    var nextTargetLoss = _scenario.TargetUnavailableWindows.FirstOrDefault(window => window.Start > returnTime);
                    if (nextTargetLoss.End > nextTargetLoss.Start && nextTargetLoss.Start <= returnTime + GCDGapLimit)
                        continue;
                    if (!_state.ExecutedActions.Any(action => action.GCD && action.TargetAvailable && action.Time >= returnTime && action.Time <= returnTime + GCDGapLimit))
                        AddFailure($"timeline did not resume a GCD after target return at {returnTime:f2}");
                }
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ResumeAutomaticPB))
            {
                var pbCooldown = ActionDefinitions.Instance.Spell(AID.PerfectBalance)?.Cooldown ?? TimelineGCD;
                foreach (var returnTime in _state.TargetReturnTimes)
                {
                    var deadline = returnTime + pbCooldown + TimelineGCD;
                    var nextTargetLoss = _scenario.TargetUnavailableWindows.FirstOrDefault(window => window.Start > returnTime);
                    if (nextTargetLoss.End > nextTargetLoss.Start && nextTargetLoss.Start <= deadline)
                        continue;
                    if (!_state.ExecutedActions.Any(action => action.Action == AID.PerfectBalance && action.Time >= returnTime && action.Time <= deadline))
                        AddFailure($"timeline did not resume automatic Perfect Balance after target return at {returnTime:f2}");
                }
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ResumePendingPB))
            {
                var recovery = _state.ExecutedActions.FirstOrDefault(action => action.GCD && action.TargetAvailable && action.Time >= _state.TargetReturnedAt);
                if (!TryGetBeastChakra(recovery.Action, out _))
                    AddFailure("timeline did not continue an active Perfect Balance with a form GCD after target return");
                if (_state.ExecutedActions.Any(action => action.Action == AID.PerfectBalance && action.Time >= _state.TargetReturnedAt))
                    AddFailure("timeline queued a new Perfect Balance before continuing the active Perfect Balance after target return");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ResumePendingBlitz))
            {
                var recovery = _state.ExecutedActions.FirstOrDefault(action => action.GCD && action.TargetAvailable && action.Time >= _state.TargetReturnedAt);
                if (!IsBlitz(recovery.Action))
                    AddFailure("timeline did not consume a pending Blitz before a normal GCD after target return");
                if (_state.ExecutedActions.Any(action => action.Action == AID.PerfectBalance && action.Time >= _state.TargetReturnedAt))
                    AddFailure("timeline queued Perfect Balance before consuming a pending Blitz after target return");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.NoMeditateDuringPendingAutomaticBlitz)
                && _state.ExecutedActions.Any(action => action.Action == AID.SteeledMeditation && !action.TargetAvailable))
            {
                AddFailure("timeline used Meditate while an automatic pending Blitz had to survive target loss");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.DelayedFixedOpener))
            {
                var openerAt = FixedOpenerRoFOffset(_scenario.Pattern.Opener);
                var rof = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfFire);
                var brotherhood = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.Brotherhood);
                if (rof.Action == AID.None || brotherhood.Action == AID.None)
                {
                    AddFailure("timeline did not use both Riddle of Fire and Brotherhood during the delayed opener");
                }
                else
                {
                    if (rof.Time < openerAt - TimelineStep || brotherhood.Time < openerAt - TimelineStep)
                        AddFailure("timeline used a delayed-opener burst buff before its configured RoF offset");
                    if (rof.Time > openerAt + TimelineGCD || brotherhood.Time > openerAt + TimelineGCD)
                        AddFailure("timeline did not use delayed-opener burst buffs within one GCD of the configured RoF offset");
                    if (Math.Abs(rof.Time - brotherhood.Time) > TimelineGCD)
                        AddFailure("timeline separated delayed-opener Riddle of Fire and Brotherhood by more than one GCD");
                }

                var openerQueues = _state.CoreBurstQueue
                    .Where(entry => entry.Time >= openerAt - TimelineStep && entry.Time <= openerAt + TimelineStep)
                    .GroupBy(entry => entry.Time)
                    .ToArray();
                var pairedQueue = openerQueues
                    .Any(entries => entries.Any(entry => entry.Action == nameof(AID.RiddleOfFire) && entry.Delay <= TimelineStep && entry.ReadyIn <= TimelineStep)
                        && entries.Any(entry => entry.Action == nameof(AID.Brotherhood) && entry.Delay <= TimelineStep && entry.ReadyIn <= TimelineStep));
                if (!pairedQueue)
                    AddFailure("timeline did not queue delayed-opener Riddle of Fire and Brotherhood together at the configured RoF offset");
                if (openerQueues.Any(entries => entries.Any(entry => entry.Action == nameof(AID.PerfectBalance))
                    && entries.Any(entry => entry.Action == nameof(AID.Brotherhood))
                    && entries.Any(entry => entry.Action == nameof(AID.RiddleOfFire))))
                    AddFailure("timeline applied the zero-second core burst fast pack to a delayed opener");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ZeroSecondCoreBurstFastPack))
            {
                var packQueued = _state.CoreBurstQueue
                    .Where(entry => entry.Time <= TimelineGCD)
                    .GroupBy(entry => entry.Time)
                    .Any(entries => entries.Any(entry => entry.Action == nameof(AID.PerfectBalance) && entry.Delay <= TimelineStep && entry.ReadyIn <= TimelineStep)
                        && entries.Any(entry => entry.Action == nameof(AID.Brotherhood) && entry.Delay <= TimelineStep && entry.ReadyIn <= TimelineStep)
                        && entries.Any(entry => entry.Action == nameof(AID.RiddleOfFire) && entry.Delay <= TimelineStep && entry.ReadyIn <= TimelineStep));
                if (!packQueued)
                    AddFailure("timeline did not queue the zero-second Perfect Balance, Brotherhood, and Riddle of Fire fast pack together");

                var perfectBalance = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.PerfectBalance);
                var brotherhood = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.Brotherhood);
                var riddleOfFire = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfFire);
                if (perfectBalance.Action == AID.None || brotherhood.Action == AID.None || riddleOfFire.Action == AID.None)
                {
                    AddFailure("timeline did not execute the zero-second Perfect Balance, Brotherhood, and Riddle of Fire fast pack");
                }
                else
                {
                    if (perfectBalance.Time > TimelineGCD || brotherhood.Time > TimelineGCD || riddleOfFire.Time > TimelineGCD)
                        AddFailure("timeline did not execute the zero-second core burst fast pack in the opening GCD window");
                    if (perfectBalance.Time >= brotherhood.Time || brotherhood.Time >= riddleOfFire.Time)
                        AddFailure("timeline did not execute the zero-second core burst fast pack in Perfect Balance, Brotherhood, Riddle of Fire order");
                    if (_state.ExecutedActions.Any(action => action.GCD && action.Time > perfectBalance.Time && action.Time < riddleOfFire.Time))
                        AddFailure("timeline split the zero-second core burst fast pack across GCDs");
                }
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.DancingMadTopLogProfile))
            {
                var brotherhood = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.Brotherhood);
                var riddleOfFire = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfFire);
                var perfectBalance = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.PerfectBalance);
                var riddleOfWind = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfWind);
                if (brotherhood.Action == AID.None || brotherhood.Time is < 1 or > 5)
                    AddFailure("timeline did not place the top-log Brotherhood opener after the first GCD and before the recovery fallback");
                if (riddleOfFire.Action == AID.None || riddleOfFire.Time is < 2 or > 7)
                    AddFailure("timeline did not place the top-log Riddle of Fire opener after Brotherhood");
                if (perfectBalance.Action == AID.None || perfectBalance.Time is < 2.5f or > 10)
                    AddFailure("timeline did not place the top-log Perfect Balance opener after the burst buffs");
                if (riddleOfWind.Action == AID.None || riddleOfWind.Time is < 3.5f or > 10)
                    AddFailure("timeline did not place the top-log Riddle of Wind opener after the burst buffs");
                if (_state.PotionQueueTimes.Any(time => time < 105))
                    AddFailure("timeline queued a Dancing Mad profile potion before its top-log pre-buff window");
                if (!_state.PotionQueueTimes.Any(time => time is >= 110 and <= 120))
                    AddFailure("timeline did not queue a Dancing Mad profile potion in its top-log pre-buff window");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.OddRoFRecastOutsideMelee)
                && !_state.ExecutedActions.Any(action => action.Action == AID.RiddleOfFire && action.Time is >= 64 and <= 70))
                AddFailure("timeline did not execute the odd Riddle of Fire recast while the target was outside melee range");

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.OddPreRoFPBRequiresOvercap))
            {
                var oddRiddleOfFire = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfFire && action.Time >= 60);
                if (oddRiddleOfFire.Action == AID.None)
                {
                    AddFailure("timeline did not execute the odd Riddle of Fire burst");
                }
                else if (_state.ExecutedActions.Any(action => action.Action == AID.PerfectBalance
                    && action.Time >= 60
                    && action.Time < oddRiddleOfFire.Time
                    && action.PerfectBalanceCapIn > PBNearOvercapWindow + TimelineStep))
                {
                    AddFailure("timeline used Perfect Balance before the odd Riddle of Fire without imminent Perfect Balance overcap");
                }
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.DynamicAOETransition))
            {
                var windows = _scenario.TargetCountWindows;
                if (windows == null)
                {
                    AddFailure("timeline did not define target-count windows for the dynamic AOE transition");
                }
                else
                {
                    var aoeWindow = windows.FirstOrDefault(window => window.Count >= 3);
                    if (aoeWindow.Count < 3)
                    {
                        AddFailure("timeline did not include a three-target AOE window");
                    }
                    else if (!_state.ExecutedActions.Any(action => action.GCD && action.TargetCount >= 3 && IsAOEGCD(action.Action)))
                    {
                        AddFailure("timeline did not execute an AOE GCD during the three-target window");
                    }

                    var returnToSingleWindow = windows.FirstOrDefault(window => window.Count == 1 && window.Start >= aoeWindow.End);
                    if (returnToSingleWindow.End > returnToSingleWindow.Start
                        && !_state.ExecutedActions.Any(action => action.GCD && action.TargetCount == 1 && action.Time >= returnToSingleWindow.Start && !IsAOEGCD(action.Action)))
                    {
                        AddFailure("timeline did not resume a single-target GCD after the AOE window");
                    }
                }
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.NoAutomaticCoreBurst)
                && _state.ExecutedActions.Any(action => action.Action is AID.RiddleOfFire or AID.Brotherhood or AID.PerfectBalance or AID.RiddleOfWind))
            {
                AddFailure("timeline executed an automatic core burst action in BasicAndChakraOvercap mode");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ForceCoreBurst))
            {
                var forcedActions = _state.ExecutedActions
                    .Where(action => action.Time <= 10)
                    .Select(action => action.Action)
                    .ToHashSet();
                if (!forcedActions.Contains(AID.RiddleOfFire)
                    || !forcedActions.Contains(AID.Brotherhood)
                    || !forcedActions.Contains(AID.PerfectBalance))
                {
                    AddFailure("timeline did not execute each forced core burst action");
                }
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.DelayCoreBurst)
                && _state.ExecutedActions.Any(action => action.Action is AID.RiddleOfFire or AID.Brotherhood or AID.PerfectBalance or AID.RiddleOfWind))
            {
                AddFailure("timeline executed a delayed core burst action");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.BlitzForceImmediate)
                && !_state.ExecutedActions.Any(action => IsBlitz(action.Action) && action.Time <= TimelineGCD))
            {
                AddFailure("timeline did not execute a forced pending Blitz in the opening GCD window");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.BlitzDelay)
                && _state.ExecutedActions.Any(action => IsBlitz(action.Action)))
            {
                AddFailure("timeline executed a delayed pending Blitz");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.BlitzRoFAligned))
            {
                var rof = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfFire);
                var blitz = _state.ExecutedActions.FirstOrDefault(action => IsBlitz(action.Action));
                if (rof.Action == AID.None || blitz.Action == AID.None || blitz.Time < rof.Time)
                    AddFailure("timeline did not hold the RoF Blitz until Riddle of Fire was active");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.BlitzMultiAfterTargets)
                && (!_state.ExecutedActions.Any(action => IsBlitz(action.Action) && action.Time >= 5 && action.TargetCount >= 4)
                    || _state.ExecutedActions.Any(action => IsBlitz(action.Action) && action.Time < 5)))
            {
                AddFailure("timeline did not hold the Multi Blitz until multiple targets were available");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.BlitzMultiRoFAligned))
            {
                var rof = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfFire);
                var blitz = _state.ExecutedActions.FirstOrDefault(action => IsBlitz(action.Action));
                if (rof.Action == AID.None || blitz.Action == AID.None || blitz.Time < rof.Time || blitz.TargetCount < 4)
                    AddFailure("timeline did not hold the MultiRoF Blitz for Riddle of Fire and multiple targets");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ForceOpoPB))
            {
                var forceOpoPB = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.PerfectBalance);
                var opoBeforePB = _state.ExecutedActions.Any(action => action.GCD && action.Time <= forceOpoPB.Time
                    && action.Action is AID.DragonKick or AID.Bootshine or AID.LeapingOpo);
                if (forceOpoPB.Action == AID.None || !opoBeforePB)
                    AddFailure("timeline did not hold ForceOpo Perfect Balance until after an Opo GCD");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.DowntimePB)
                && !_state.ExecutedActions.Any(action => action.Action == AID.PerfectBalance && !action.TargetAvailable))
            {
                AddFailure("timeline did not execute the downtime Perfect Balance while no target was available");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ForceMidWeaveRoF))
            {
                var rof = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfFire);
                var precedingGCD = _state.ExecutedActions.Any(action => action.GCD && action.Time <= rof.Time);
                if (rof.Action == AID.None || !precedingGCD)
                    AddFailure("timeline did not execute ForceMidWeave Riddle of Fire after a preceding GCD");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.LookAwayHoldsEnemyActions)
                && _state.ExecutedActions.Any(action => action.TargetID == _target.InstanceID))
            {
                AddFailure("timeline executed an enemy-targeted action while lookaway was active");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.RoWImmediate)
                && !_state.ExecutedActions.Any(action => action.Action == AID.RiddleOfWind && action.Time <= TimelineGCD))
            {
                AddFailure("timeline did not execute the immediate Riddle of Wind strategy in the opening GCD window");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.RoWRoFAligned))
            {
                var rof = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfFire);
                var row = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfWind);
                if (rof.Action == AID.None || row.Action == AID.None || row.Time < rof.Time)
                    AddFailure("timeline did not hold the Riddle of Wind strategy until Riddle of Fire");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ThunderclapGapClose)
                && !_state.ExecutedActions.Any(action => action.Action == AID.Thunderclap && action.TargetID == _target.InstanceID))
            {
                AddFailure("timeline did not execute Thunderclap for a safe out-of-melee target");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.FiresReplyAutomatic)
                && !_state.ExecutedActions.Any(action => action.Action == AID.FiresReply))
            {
                AddFailure("timeline did not execute the automatic Fire's Reply");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.WindsReplyForce)
                && !_state.ExecutedActions.Any(action => action.Action == AID.WindsReply))
            {
                AddFailure("timeline did not execute the forced Wind's Reply");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ReplyDelay)
                && _state.ExecutedActions.Any(action => action.Action is AID.FiresReply or AID.WindsReply))
            {
                AddFailure("timeline executed a delayed Reply action");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.PBProtectsAgainstReply))
            {
                var firstGCD = _state.ExecutedActions.FirstOrDefault(action => action.GCD);
                if (firstGCD.Action is AID.None or AID.FiresReply or AID.WindsReply)
                    AddFailure("timeline let a non-expiring Reply displace the first Perfect Balance GCD");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.PotionNow)
                && !_state.PotionQueueTimes.Any(time => time <= TimelineGCD))
            {
                AddFailure("timeline did not queue PotionStrategy.Now in the opening GCD window");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.PotionPreBuff))
            {
                var potionTimes = _state.PotionQueueTimes;
                var rof = _state.ExecutedActions.FirstOrDefault(action => action.Action == AID.RiddleOfFire);
                if (potionTimes.Count == 0 || rof.Action == AID.None || potionTimes.Min() > rof.Time)
                    AddFailure("timeline did not queue the delayed-opener Potion before Riddle of Fire");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ChakraOvercap)
                && !_state.ExecutedActions.Any(action => IsChakraAction(action.Action)))
            {
                AddFailure("timeline did not spend Chakra at its active overcap threshold");
            }

            var trueNorth = ActionID.MakeSpell(BossMod.ClassShared.AID.TrueNorth);
            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.TrueNorthForce)
                && !_state.ExecutedActions.Any(action => action.RawAction == trueNorth))
            {
                AddFailure("timeline did not execute forced True North");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.TrueNorthDelay)
                && _state.ExecutedActions.Any(action => action.RawAction == trueNorth))
            {
                AddFailure("timeline executed delayed True North");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.DowntimeFormShift)
                && !_state.ExecutedActions.Any(action => action.Action == AID.FormShift))
            {
                AddFailure("timeline did not use Form Shift to prepare Leaping Opo during downtime");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.FormShiftDelay)
                && _state.ExecutedActions.Any(action => action.Action == AID.FormShift))
            {
                AddFailure("timeline executed delayed Form Shift during downtime");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.ThunderclapNone)
                && _state.ExecutedActions.Any(action => action.Action == AID.Thunderclap))
            {
                AddFailure("timeline executed Thunderclap while its strategy was None");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.MeditateSafe)
                && !_state.ExecutedActions.Any(action => action.Action == AID.SteeledMeditation))
            {
                AddFailure("timeline did not use Safe Meditate while the target was unavailable");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.MeditateGreedy)
                && !_state.ExecutedActions.Any(action => action.Action == AID.SteeledMeditation))
            {
                AddFailure("timeline did not use Greedy Meditate while outside melee range");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.MeditateForce)
                && !_state.ExecutedActions.Any(action => action.Action == AID.SteeledMeditation))
            {
                AddFailure("timeline did not use forced Meditate");
            }

            if (_scenario.Requirements.HasFlag(MnkTimelineRequirement.MeditateDelay)
                && _state.ExecutedActions.Any(action => action.Action == AID.SteeledMeditation))
            {
                AddFailure("timeline executed delayed Meditate");
            }

        }

        private int CountActions(AID action, double start, double end)
            => _state.ExecutedActions.Count(candidate => candidate.Action == action && candidate.Time >= start && candidate.Time <= end);

        private void AddFailure(string failure)
        {
            if (!_state.Failures.Contains(failure))
                _state.Failures.Add(failure);
        }

        private float Left(double until) => (float)Math.Max(0, until - _state.Time);

        private static bool IsCoreBurstAction(AID action)
            => action is AID.RiddleOfFire or AID.Brotherhood or AID.PerfectBalance or AID.RiddleOfWind;

        private static bool IsAOEGCD(AID action)
            => action is AID.ArmOfTheDestroyer or AID.ShadowOfTheDestroyer or AID.FourPointFury or AID.Rockbreaker;

        private static bool IsBlitz(AID action)
            => action is AID.ElixirField or AID.ElixirBurst or AID.RisingPhoenix or AID.FlintStrike or AID.CelestialRevolution or AID.TornadoKick or AID.PhantomRush;

        private static bool IsChakraAction(AID action)
            => action is AID.SteelPeak or AID.HowlingFist or AID.ForbiddenChakra or AID.Enlightenment;

        private static bool TryGetBeastChakra(AID action, out BeastChakraType beast)
        {
            beast = action switch
            {
                AID.Bootshine or AID.DragonKick or AID.LeapingOpo or AID.ArmOfTheDestroyer or AID.ShadowOfTheDestroyer => BeastChakraType.OpoOpo,
                AID.TrueStrike or AID.TwinSnakes or AID.RisingRaptor or AID.FourPointFury => BeastChakraType.Raptor,
                AID.SnapPunch or AID.Demolish or AID.PouncingCoeurl or AID.Rockbreaker => BeastChakraType.Coeurl,
                _ => BeastChakraType.None
            };
            return beast != BeastChakraType.None;
        }

        private sealed class TimelineState
        {
            public double Time;
            public int Frame;
            public int Chakra;
            public byte OpoStacks;
            public byte FuryStacks;
            public NadiFlags Nadi;
            public readonly List<BeastChakraType> BeastChakra = [];
            public bool TargetAvailable;
            public int ActiveTargetCount;
            public bool MeleeAvailable;
            public bool ThunderclapLanded;
            public double TargetReturnedAt;
            public readonly List<double> TargetReturnTimes = [];
            public double LastGCDAt;
            public double EmptyReadyTime;
            public bool GCDGapReported;
            public XanMNK.Form CurrentForm;
            public double FormUntil;
            public double FormlessFistUntil;
            public double RiddleOfFireUntil;
            public double RiddleOfWindUntil;
            public double BrotherhoodUntil;
            public double PerfectBalanceUntil;
            public int PerfectBalanceStacks;
            public double FiresReplyUntil;
            public double WindsReplyUntil;
            public double EarthsReplyUntil;
            public double RiddleOfEarthUntil;
            public double BlitzUntil;
            public int GCDActions;
            public int OGCDActions;
            public int PerfectBalances;
            public int Blitzes;
            public int PhantomRushes;
            public int CoreBurstActionsDuringTargetLoss;
            public readonly List<MnkTimelineActionRecord> Actions = [];
            public readonly List<MnkTimelineQueueRecord> CoreBurstQueue = [];
            public readonly List<double> PotionQueueTimes = [];
            public readonly List<ExecutedTimelineAction> ExecutedActions = [];
            public readonly List<string> Failures = [];
        }

        private readonly record struct TimelineStatus(uint ID, double Until, ushort Extra);
        private readonly record struct ExecutedTimelineAction(double Time, AID Action, bool GCD, ulong TargetID, bool TargetAvailable, float PerfectBalanceCapIn, int TargetCount, ActionID RawAction);
    }

    private sealed record HarnessOptions(int Start, int? Limit, string? Out, string? Sqpack, string? TimelineRoot)
    {
        public static HarnessOptions Parse(string[] args)
        {
            var start = 0;
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
                    case "--start":
                        start = int.Parse(value);
                        break;
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

            return new(start, limit, output, sqpack, timelineRoot);
        }
    }
}

internal sealed record RealMnkPattern(
    string Name,
    int CombatTimer,
    int Level,
    double Gcd,
    OpenerRoFOffsetMode Opener,
    BurstTimingMode BurstTiming,
    EncounterHintMode EncounterHint,
    RotationMode RotationMode,
    PBStrategyMode PBStrategy,
    XanMNK.NadiStrategy NadiStrategy,
    BlitzStrategyMode BlitzStrategy,
    AutoForceDelayMode RoFStrategy,
    AutoForceDelayMode BrotherhoodStrategy,
    AutoForceDelayMode RoWStrategy,
    AutoForceDelayMode RoEStrategy,
    ThunderclapStrategyMode ThunderclapStrategy,
    int ContentId,
    int TargetCount,
    bool Targetable,
    bool HaveTarget,
    bool CanMelee,
    bool LookAway,
    int TargetPriority,
    PredictedDamageKind PredictedDamage,
    bool PredictedDamageSelf,
    double PredictedDamageIn,
    string CurrentForm,
    double FormLeft,
    double FormlessFistLeft,
    double RiddleOfFireLeft,
    double BrotherhoodLeft,
    double PerfectBalanceLeft,
    int PerfectBalanceStacks,
    double FiresReplyLeft,
    double WindsReplyLeft,
    double EarthsReplyLeft,
    double RiddleOfEarthLeft,
    double RiddleOfFireReadyIn,
    double BrotherhoodReadyIn,
    double PerfectBalanceReadyIn,
    double RiddleOfWindReadyIn,
    double RiddleOfEarthReadyIn,
    double ThunderclapReadyIn,
    double TrueNorthReadyIn,
    float AnimationLockDelay);

internal sealed record QueuedActionRecord(uint ActionID, string Action, ulong Target, float Priority, float Delay, float ReadyIn);

internal sealed record RealMnkExecutionResult(RealMnkPattern Pattern, List<QueuedActionRecord> Actions, List<string> PolicyFailures, string? Exception);

[Flags]
internal enum MnkTimelineRequirement : long
{
    None = 0,
    OpeningTwoPB = 1 << 0,
    EvenTwoPB = 1 << 1,
    PendingPhantomRush = 1 << 2,
    ForbidCoreBurstDuringTargetLoss = 1 << 3,
    ResumeGCD = 1 << 4,
    DelayedFixedOpener = 1 << 5,
    OpeningDoubleLunar = 1 << 6,
    ZeroSecondCoreBurstFastPack = 1 << 7,
    OddRoFRecastOutsideMelee = 1 << 8,
    OddPreRoFPBRequiresOvercap = 1 << 9,
    DynamicAOETransition = 1 << 10,
    NoAutomaticCoreBurst = 1 << 11,
    ForceCoreBurst = 1 << 12,
    DelayCoreBurst = 1 << 13,
    OpeningLunarSolar = 1 << 14,
    OpeningSolar = 1 << 15,
    BlitzForceImmediate = 1 << 16,
    BlitzDelay = 1 << 17,
    BlitzRoFAligned = 1 << 18,
    BlitzMultiAfterTargets = 1 << 19,
    BlitzMultiRoFAligned = 1 << 20,
    ForceOpoPB = 1 << 21,
    DowntimePB = 1 << 22,
    ForceMidWeaveRoF = 1 << 23,
    LookAwayHoldsEnemyActions = 1 << 24,
    RoWImmediate = 1 << 25,
    RoWRoFAligned = 1 << 26,
    ThunderclapGapClose = 1 << 27,
    FiresReplyAutomatic = 1L << 28,
    WindsReplyForce = 1L << 29,
    ReplyDelay = 1L << 30,
    PBProtectsAgainstReply = 1L << 31,
    PotionNow = 1L << 32,
    PotionPreBuff = 1L << 33,
    ChakraOvercap = 1L << 34,
    TrueNorthForce = 1L << 35,
    TrueNorthDelay = 1L << 36,
    DowntimeFormShift = 1L << 37,
    ThunderclapNone = 1L << 38,
    FormShiftDelay = 1L << 39,
    MeditateSafe = 1L << 40,
    MeditateGreedy = 1L << 41,
    MeditateForce = 1L << 42,
    MeditateDelay = 1L << 43,
    EvenPhantomRushInsideBuff = 1L << 44,
    ResumePendingPB = 1L << 45,
    ResumePendingBlitz = 1L << 46,
    NoMeditateDuringPendingAutomaticBlitz = 1L << 47,
    ResumeAutomaticPB = 1L << 48,
    DancingMadTopLogProfile = 1L << 49
}

internal readonly record struct TimelineWindow(double Start, double End)
{
    public bool Contains(double time) => time >= Start && time < End;
}

internal readonly record struct TimelineTargetCountWindow(double Start, double End, int Count)
{
    public bool Contains(double time) => time >= Start && time < End;
}

internal sealed record MnkTimelineScenario(
    string Name,
    RealMnkPattern Pattern,
    float Duration,
    TimelineWindow[] TargetUnavailableWindows,
    NadiFlags InitialNadi,
    BeastChakraType[] InitialBeastChakra,
    float InitialBlitzLeft,
    MnkTimelineRequirement Requirements,
    float CountdownSeconds = 0,
    TimelineWindow[]? MeleeUnavailableWindows = null,
    TimelineTargetCountWindow[]? TargetCountWindows = null,
    XanMNK.NadiStrategy? NadiStrategyOverride = null,
    XanMNK.PBStrategy? PBStrategyOverride = null,
    XanMNK.RoFStrategy? RoFStrategyOverride = null,
    XanMNK.RoWStrategy? RoWStrategyOverride = null,
    XanMNK.FRStrategy? FiresReplyStrategyOverride = null,
    XanMNK.WRStrategy? WindsReplyStrategyOverride = null,
    XanMNK.PotionStrategy? PotionStrategyOverride = null,
    int InitialChakra = 0,
    int InitialOpoStacks = 0,
    OffensiveStrategy? TrueNorthStrategyOverride = null,
    OffensiveStrategy? FormShiftStrategyOverride = null,
    XanMNK.MeditationStrategy? MeditateStrategyOverride = null,
    XanMNK.SkillRotationMode? SkillRotationOverride = null);

internal sealed record MnkTimelineActionRecord(
    float Time,
    string Action,
    bool GCD,
    float ReadyIn,
    int BeastCount,
    NadiFlags Nadi,
    float PerfectBalanceLeft,
    float BlitzLeft,
    float RiddleOfFireLeft,
    float BrotherhoodLeft,
    bool TargetAvailable);

internal sealed record MnkTimelineQueueRecord(float Time, string Action, float Priority, float Delay, float ReadyIn);

internal sealed record RealMnkTimelineResult(
    string Scenario,
    float Duration,
    int Frames,
    int GCDActions,
    int OGCDActions,
    int PerfectBalances,
    int Blitzes,
    int PhantomRushes,
    List<MnkTimelineActionRecord> Actions,
    List<MnkTimelineQueueRecord> CoreBurstQueue,
    List<string> Failures);

internal sealed record EventTriggerTimelineInput(
    string ResourcesDirectory,
    int ZoneID,
    string FileName,
    EventTriggerTimelineSummary Summary,
    float FullDuration,
    TimelineWindow[] TargetUnavailableWindows,
    List<string> Failures);

internal sealed record DmuPlannerPhaseResult(
    string Phase,
    int Index,
    bool StartsWithDowntime,
    bool PlannerEndsWithDowntime,
    int TargetCount,
    int TargetPriority,
    float PhaseEnd,
    float? DowntimeStart,
    float BurstHorizon);

internal sealed record DmuDamageMechanicResult(
    string Mechanic,
    AIHints.PredictedDamageType Type,
    int PredictedDamageCount,
    bool SelfPrediction,
    bool RoEAutomaticQueued,
    bool RoEDelayQueued);

internal sealed record DmuMechanicHintResult(List<DmuDamageMechanicResult> Results, List<string> Failures);

internal sealed record DmuDowntimeResult(string Transition, int PotentialTargetCount, List<string> CoreBurstActions);

internal sealed record DmuResumeResult(string Transition, int PotentialTargetCount, List<string> TargetGCDActions);

internal sealed record DmuPendingBlitzResult(bool BlitzQueuedDuringDowntime, List<string> RecoveryBlitzActions, bool PBQueuedOnRecovery, List<string> FormlessRecoveryActions, bool BlitzQueuedBeforeFormless, bool DelayQueuedBlitzOnRecovery);

internal sealed record DmuPendingPBResult(bool TargetGCDQueuedDuringDowntime, List<string> RecoveryPBGCDs, bool PBQueuedOnRecovery);

internal sealed record DmuEarthsReplyResult(bool PartyAllDamagedQueued, bool PartiallyDamagedQueued, bool ExpiringQueued, bool BasicModeQueued, bool DeadMemberQueued, bool ZeroMaxHPMemberQueued, bool MissingMemberQueued);

internal sealed record DmuPlannerIntegrationResult(string Scenario, string ModuleType, DmuMechanicHintResult MechanicHints, List<DmuPlannerPhaseResult> Phases, List<DmuDowntimeResult> Downtimes, List<DmuResumeResult> Resumes, DmuPendingBlitzResult PendingBlitz, DmuPendingPBResult PendingPB, DmuEarthsReplyResult EarthsReply, List<string> Failures);

internal sealed record RealHarnessResult(int Start, int Patterns)
{
    public int Failures { get; set; }
    public int DuplicatePatterns { get; set; }
    public int PolicyFailures { get; set; }
    public int EmptyQueues { get; set; }
    public int ExpectedEmptyQueues { get; set; }
    public List<RealMnkExecutionResult> Results { get; } = [];
}
