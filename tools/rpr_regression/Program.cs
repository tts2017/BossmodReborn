using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RprRegression;

public static class Program
{
    private const int OfflinePlannerBeamWidth = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Any(arg => arg.Equals("--extract-fflogs", StringComparison.OrdinalIgnoreCase)))
                return FflogsRprExtractor.RunAsync(args).GetAwaiter().GetResult();

            if (args.Any(arg => arg.Equals("--build-fflogs-profile", StringComparison.OrdinalIgnoreCase)))
                return RprLogDerivedProfileBuilder.BuildFromExtractedEvents(args);

            var options = CliOptions.Parse(args);
            if (options.Plan)
                return RunOfflinePlanner(options);
            if (options.Optimize)
                return RunOptimizer(options);

            if (!options.All && !options.Real && string.IsNullOrWhiteSpace(options.Scenario))
                throw new ArgumentException("Specify --all or --scenario <name/category>.");
            if (string.IsNullOrWhiteSpace(options.Out))
                throw new ArgumentException("Specify --out <directory>.");

            var scenarios = options.Real ? RealRprHarness.Build(options.Patterns, options.Seed) : options.All ? ScenarioCatalog.BuildAll() : ScenarioCatalog.Select(options.Scenario!);
            if (options.Real && !string.IsNullOrWhiteSpace(options.Scenario))
                scenarios = SelectRealScenarios(scenarios, options.Scenario);
            if (scenarios.Count == 0)
                throw new ArgumentException($"No scenarios matched '{options.Scenario}'.");

            if (options.Real)
                Console.WriteLine("Execution engine: RprRotationEmulator stress model; production RPR.cs is validated by tools\\xan_timeline_harness.");

            var results = options.Real
                ? RunStressEmulator(scenarios)
                : scenarios.Select(new RprRotationEmulator().Run).ToList();
            var output = BuildOutput(results, null);
            RegressionOutput? baseline = null;
            if (!string.IsNullOrWhiteSpace(options.Baseline))
            {
                baseline = ReadJson<RegressionOutput>(options.Baseline);
                output = BuildOutput(results, baseline);
            }

            WriteOutputs(options.Out!, output, baseline, compactJson: options.Real && !options.RealFullOutput, writeAllTimelines: !string.IsNullOrWhiteSpace(options.Scenario));
            if (options.Real)
                WriteRealHarnessSummary(options.Out!, output, options);
            Console.WriteLine($"RPR regression scenarios: {output.Summary.ScenarioCount}");
            Console.WriteLine($"Passed: {output.Summary.PassCount}");
            Console.WriteLine($"Failed: {output.Summary.FailCount}");
            Console.WriteLine($"HardFail: {output.Summary.HardFailCountByRule.Values.Sum()}");
            Console.WriteLine($"Results: {Path.GetFullPath(options.Out!)}");
            return output.Summary.FailCount == 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static RegressionOutput BuildOutput(IReadOnlyList<ScenarioResult> scenarios, RegressionOutput? baseline)
    {
        var hardFailCounts = scenarios
            .SelectMany(s => s.HardFails)
            .GroupBy(f => f)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        var regressions = baseline == null
            ? []
            : CompareScenarioResults(baseline.Scenarios, scenarios).Regressions.ToList();

        var summary = new SummaryResult
        {
            ScenarioCount = scenarios.Count,
            PassCount = scenarios.Count(s => s.Passed),
            FailCount = scenarios.Count(s => !s.Passed),
            HardFailCountByRule = hardFailCounts,
            BaselineScore = baseline?.Summary.CandidateScore ?? scenarios.Sum(s => s.Score),
            CandidateScore = scenarios.Sum(s => s.Score),
            Regressions = regressions
        };

        return new RegressionOutput
        {
            Summary = summary,
            Scenarios = scenarios.ToList()
        };
    }

    private static CompareResult CompareScenarioResults(IReadOnlyList<ScenarioResult> baseline, IReadOnlyList<ScenarioResult> candidate)
    {
        var rejectionReasons = new List<string>();
        var regressions = new List<string>();
        var baselineByName = baseline.ToDictionary(s => s.Scenario.Name);

        foreach (var current in candidate)
        {
            if (!baselineByName.TryGetValue(current.Scenario.Name, out var previous))
                continue;

            if (current.HardFails.Count > previous.HardFails.Count)
                regressions.Add($"{current.Scenario.Name}: hard fails increased {previous.HardFails.Count} -> {current.HardFails.Count}");
            if (current.Metrics.GcdUptime + 0.0001 < previous.Metrics.GcdUptime)
                regressions.Add($"{current.Scenario.Name}: GCD uptime worsened {previous.Metrics.GcdUptime:P2} -> {current.Metrics.GcdUptime:P2}");
            if (current.Metrics.ArcaneCircleCount < previous.Metrics.ArcaneCircleCount)
                regressions.Add($"{current.Scenario.Name}: Arcane Circle count decreased");
            if (current.Metrics.EnshroudCount < previous.Metrics.EnshroudCount && MeaningfulEnshroudRegression(previous, current))
                regressions.Add($"{current.Scenario.Name}: Enshroud count decreased");
            if (current.Metrics.CommunioCount < previous.Metrics.CommunioCount)
                regressions.Add($"{current.Scenario.Name}: Communio count decreased");
            if (current.Metrics.PerfectioCount < previous.Metrics.PerfectioCount)
                regressions.Add($"{current.Scenario.Name}: Perfectio count decreased");
            if (current.Metrics.GluttonyCount < previous.Metrics.GluttonyCount && !HasBasicModeWindow(current.Scenario))
                regressions.Add($"{current.Scenario.Name}: Gluttony count decreased");
            if (current.Metrics.DeathsDesignUptime + 0.0001 < previous.Metrics.DeathsDesignUptime)
                regressions.Add($"{current.Scenario.Name}: DD uptime worsened");
            if (current.Metrics.RedGaugeOvercap > previous.Metrics.RedGaugeOvercap)
                regressions.Add($"{current.Scenario.Name}: RedGauge overcap increased");
            if (current.Metrics.BlueGaugeOvercap > previous.Metrics.BlueGaugeOvercap && !HasBasicModeWindow(current.Scenario))
                regressions.Add($"{current.Scenario.Name}: BlueGauge overcap increased");
        }

        if (candidate.Any(s => s.HardFails.Count > 0))
            rejectionReasons.Add("HardFail > 0");
        if (regressions.Count > 0)
            rejectionReasons.Add("baseline regression detected");

        return new CompareResult(rejectionReasons.Count == 0 ? "ADOPTABLE" : "REJECTED", rejectionReasons, regressions);
    }

    private static bool MeaningfulEnshroudRegression(ScenarioResult previous, ScenarioResult current)
        => current.Metrics.CommunioCount < previous.Metrics.CommunioCount
        || current.Metrics.PerfectioCount < previous.Metrics.PerfectioCount
        || current.Score + 0.0001 < previous.Score;

    private static bool HasBasicModeWindow(ScenarioDefinition scenario)
        => scenario.InitialMode == RotationMode.Basic
        || scenario.Events.Any(e => e.Type == ScenarioEventType.ModeSwitch && e.Mode == RotationMode.Basic);

    private static IReadOnlyList<ScenarioDefinition> SelectRealScenarios(IReadOnlyList<ScenarioDefinition> scenarios, string selector)
    {
        if (selector.Equals("all", StringComparison.OrdinalIgnoreCase))
            return scenarios;

        return scenarios.Where(s =>
            s.Name.Contains(selector, StringComparison.OrdinalIgnoreCase)
            || s.Category.ToString().Equals(selector, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static void WriteOutputs(string outDir, RegressionOutput output, RegressionOutput? baseline, bool compactJson = false, bool writeAllTimelines = false)
    {
        Directory.CreateDirectory(outDir);
        var timelineDir = Path.Combine(outDir, "timelines");
        if (Directory.Exists(timelineDir))
            Directory.Delete(timelineDir, recursive: true);
        Directory.CreateDirectory(timelineDir);

        WriteJson(Path.Combine(outDir, "summary.json"), output.Summary);
        WriteCsv(Path.Combine(outDir, "scenarios.csv"), output.Scenarios);
        WriteCompare(Path.Combine(outDir, "compare.md"), output, baseline);
        WriteWeaveAnalysis(outDir, output);
        WriteWeaveActionabilityAnalysis(outDir, output);
        WriteHighEndRotationOpportunityAnalysis(outDir, output);

        foreach (var result in output.Scenarios.Where(s => writeAllTimelines || s.HardFails.Count > 0))
            WriteTimeline(Path.Combine(timelineDir, $"{Sanitize(result.Scenario.Name)}.csv"), result);

        WriteJson(Path.Combine(outDir, "baseline.json"), compactJson ? Compact(output) : output);
    }

    private static List<ScenarioResult> RunStressEmulator(IReadOnlyList<ScenarioDefinition> scenarios)
        => scenarios
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
            .Select(s => new RprRotationEmulator().Run(s))
            .ToList();

    private static RegressionOutput Compact(RegressionOutput output)
        => output with
        {
            Scenarios = output.Scenarios
                .Select(s => s with { Frames = [] })
                .ToList()
        };

    private static int RunOfflinePlanner(CliOptions options)
    {
        if (!options.All && !options.Real && string.IsNullOrWhiteSpace(options.Scenario))
            throw new ArgumentException("Specify --all, --real, or --scenario <name/category> with --plan.");
        if (string.IsNullOrWhiteSpace(options.Out))
            throw new ArgumentException("Specify --out <directory> with --plan.");

        var scenarios = options.Real ? RealRprHarness.Build(options.Patterns, options.Seed) : options.All ? ScenarioCatalog.BuildAll() : ScenarioCatalog.Select(options.Scenario!);
        if (options.Real && !string.IsNullOrWhiteSpace(options.Scenario))
            scenarios = SelectRealScenarios(scenarios, options.Scenario);
        if (scenarios.Count == 0)
            throw new ArgumentException($"No scenarios matched '{options.Scenario}'.");

        var baseline = string.IsNullOrWhiteSpace(options.Baseline)
            ? BuildOutput(RunScenarios(scenarios, RprTuningProfile.Baseline, options.Real), null)
            : ReadJson<RegressionOutput>(options.Baseline);
        var baselineNames = baseline.Scenarios.Select(s => s.Scenario.Name).ToHashSet(StringComparer.Ordinal);
        var missingBaselineScenarios = scenarios.Where(s => !baselineNames.Contains(s.Name)).Select(s => s.Name).ToList();
        if (missingBaselineScenarios.Count > 0)
            throw new InvalidDataException($"Baseline is missing {missingBaselineScenarios.Count.ToString(CultureInfo.InvariantCulture)} planner scenarios, first: {missingBaselineScenarios[0]}");

        Directory.CreateDirectory(options.Out!);
        var profiles = BuildOfflinePlannerCandidates();
        if (!string.IsNullOrWhiteSpace(options.Candidate))
            profiles = profiles.Where(p => p.Name.Contains(options.Candidate, StringComparison.OrdinalIgnoreCase)).ToList();

        var evaluated = profiles
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
            .Select(profile =>
            {
                var output = BuildOutput(RunScenarios(scenarios, profile, parallel: false), baseline);
                return new OfflinePlannerBeamNode(profile, BuildOfflinePlannerCandidateResult(profile, output, baseline), [profile.Name]);
            })
            .ToList();
        var rows = evaluated.Select(e => e.Result).ToList();
        var beam = BuildOfflinePlannerBeam(evaluated, scenarios, baseline);
        var provenBeam = beam
            .Where(candidate => candidate.Result.Verdict == "PROVEN" && candidate.Decisions.Count > 1)
            .OrderByDescending(candidate => PlannerBeamScore(candidate.Result))
            .ToList();

        var proven = rows
            .Where(r => r.Verdict == "PROVEN")
            .OrderByDescending(r => r.TotalPotencyDelta)
            .ThenByDescending(r => r.DeathsDesignUptimeDelta)
            .ThenBy(r => r.GaugeLossDelta)
            .ToList();
        WriteJson(Path.Combine(options.Out!, "offline_planner_summary.json"), new
        {
            Baseline = options.Baseline ?? "generated current rotation",
            ScenarioCount = scenarios.Count,
            CandidateCount = rows.Count,
            ProvenCount = proven.Count,
            ProvenCandidates = proven,
            BeamWidth = OfflinePlannerBeamWidth,
            BeamCandidateCount = beam.Count,
            ProvenBeamCount = provenBeam.Count,
            ProvenBeamCandidates = provenBeam.Select(PlannerBeamOutput).ToList(),
            Candidates = rows
        });
        WriteJson(Path.Combine(options.Out!, "offline_planner_proven.json"), proven);
        WriteJson(Path.Combine(options.Out!, "offline_planner_beam.json"), new
        {
            BeamWidth = OfflinePlannerBeamWidth,
            Candidates = beam.Select(PlannerBeamOutput).ToList(),
            Proven = provenBeam.Select(PlannerBeamOutput).ToList()
        });
        WriteOfflinePlannerCandidatesCsv(Path.Combine(options.Out!, "offline_planner_candidates.csv"), rows);
        WriteOfflinePlannerCompare(Path.Combine(options.Out!, "offline_planner_compare.md"), rows, proven);
        WriteOfflinePlannerBeam(Path.Combine(options.Out!, "offline_planner_beam.md"), beam, provenBeam);

        Console.WriteLine($"RPR offline planner scenarios: {scenarios.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Candidates: {rows.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Proven: {proven.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Beam candidates: {beam.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Proven beam: {provenBeam.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Results: {Path.GetFullPath(options.Out!)}");
        return 0;
    }

    private static List<OfflinePlannerBeamNode> BuildOfflinePlannerBeam(
        IReadOnlyList<OfflinePlannerBeamNode> singles,
        IReadOnlyList<ScenarioDefinition> scenarios,
        RegressionOutput baseline)
    {
        var promising = singles
            .Where(candidate => candidate.Result.Verdict != "REJECTED"
                && (candidate.Result.TotalPotencyDelta > 0.0001
                    || candidate.Result.DeathsDesignUptimeDelta > 0.0000001
                    || candidate.Result.GaugeLossDelta < -0.0001))
            .GroupBy(candidate => candidate.Profile.Parameter, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        if (promising.Count < 2)
            return [];

        var baselineProfile = RprTuningProfile.Baseline;
        var baselineResult = BuildOfflinePlannerCandidateResult(baselineProfile, baseline, baseline);
        var frontier = new List<OfflinePlannerBeamNode> { new(baselineProfile, baselineResult, []) };
        var results = new List<OfflinePlannerBeamNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { PlannerProfileKey(baselineProfile) };

        foreach (var group in promising)
        {
            var pending = new List<(RprTuningProfile Profile, IReadOnlyList<string> Decisions)>();
            var next = new List<OfflinePlannerBeamNode>(frontier);
            foreach (var node in frontier)
            {
                foreach (var option in group)
                {
                    var profile = ApplyPlannerOption(node.Profile, option.Profile, node.Decisions.Count + 1);
                    if (!seen.Add(PlannerProfileKey(profile)))
                        continue;

                    pending.Add((profile, [.. node.Decisions, option.Profile.Name]));
                }
            }

            var evaluated = pending
                .AsParallel()
                .AsOrdered()
                .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
                .Select(candidate =>
                {
                    var output = BuildOutput(RunScenarios(scenarios, candidate.Profile, parallel: false), baseline);
                    return new OfflinePlannerBeamNode(candidate.Profile, BuildOfflinePlannerCandidateResult(candidate.Profile, output, baseline), candidate.Decisions);
                })
                .ToList();
            results.AddRange(evaluated);
            next.AddRange(evaluated.Where(candidate => candidate.Result.Verdict != "REJECTED"));
            frontier = next
                .OrderByDescending(candidate => PlannerBeamScore(candidate.Result))
                .ThenBy(candidate => candidate.Profile.Name, StringComparer.Ordinal)
                .Take(OfflinePlannerBeamWidth)
                .ToList();
        }

        return results
            .OrderByDescending(candidate => PlannerBeamScore(candidate.Result))
            .ThenBy(candidate => candidate.Profile.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static RprTuningProfile ApplyPlannerOption(RprTuningProfile current, RprTuningProfile option, int depth)
    {
        var combined = option.Parameter switch
        {
            "EnshroudHoldBeforeArcaneSeconds" => current with { EnshroudHoldBeforeArcaneSeconds = option.EnshroudHoldBeforeArcaneSeconds },
            "PreAnyEnshroudGcds" => current with { PreAnyEnshroudGcds = option.PreAnyEnshroudGcds },
            "ArcaneStartDeathsDesignCoverage" => current with { ArcaneStartDeathsDesignCoverage = option.ArcaneStartDeathsDesignCoverage },
            "ComboPriorityScope" => current with { ComboPriorityScope = option.ComboPriorityScope },
            "SoulSliceBurstSoonSeconds" => current with { SoulSliceBurstSoonSeconds = option.SoulSliceBurstSoonSeconds },
            "GluttonyHoldBeforeArcaneSeconds" => current with { GluttonyHoldBeforeArcaneSeconds = option.GluttonyHoldBeforeArcaneSeconds },
            "EndGaugeSpendWindowSeconds" => current with { EndGaugeSpendWindowSeconds = option.EndGaugeSpendWindowSeconds },
            "TimelineAwareBurstHold" => current with { TimelineAwareBurstHold = option.TimelineAwareBurstHold },
            "DancingMadMedianArcaneCircleAnchorMask" => current with { DancingMadMedianArcaneCircleAnchorMask = current.DancingMadMedianArcaneCircleAnchorMask | option.DancingMadMedianArcaneCircleAnchorMask },
            _ => throw new InvalidOperationException($"Unsupported planner parameter '{option.Parameter}'.")
        };

        return combined with
        {
            Name = $"beam_{depth.ToString(CultureInfo.InvariantCulture)}_{current.Name}_{option.Name}",
            DecisionDomain = "Beam policy composition",
            Parameter = $"{current.Parameter}+{option.Parameter}",
            BaselineValue = "baseline policy",
            CandidateValue = "combined policy"
        };
    }

    private static string PlannerProfileKey(RprTuningProfile profile)
        => string.Join('|',
            profile.EnshroudHoldBeforeArcaneSeconds.ToString("R", CultureInfo.InvariantCulture),
            profile.PreAnyEnshroudGcds.ToString("R", CultureInfo.InvariantCulture),
            profile.ArcaneStartDeathsDesignCoverage.ToString("R", CultureInfo.InvariantCulture),
            profile.ComboPriorityScope.ToString(CultureInfo.InvariantCulture),
            profile.SoulSliceBurstSoonSeconds.ToString("R", CultureInfo.InvariantCulture),
            profile.GluttonyHoldBeforeArcaneSeconds.ToString("R", CultureInfo.InvariantCulture),
            profile.EndGaugeSpendWindowSeconds.ToString("R", CultureInfo.InvariantCulture),
            profile.TimelineAwareBurstHold ? "1" : "0",
            profile.DancingMadMedianArcaneCircleAnchorMask.ToString(CultureInfo.InvariantCulture));

    private static double PlannerBeamScore(OfflinePlannerCandidateResult result)
        => result.Verdict == "REJECTED"
            ? double.NegativeInfinity
            : result.TotalPotencyDelta
                + result.DeathsDesignUptimeDelta * 1_000_000
                - result.GaugeLossDelta * 1_000
                - Math.Max(0, result.GluttonyDriftDelta) * 10_000
                - Math.Max(0, result.ClipRiskDelta) * 100_000;

    private static object PlannerBeamOutput(OfflinePlannerBeamNode candidate)
        => new
        {
            candidate.Profile.Name,
            candidate.Decisions,
            candidate.Result.Verdict,
            Score = PlannerBeamScore(candidate.Result),
            candidate.Result.HardFailCount,
            candidate.Result.RegressionCount,
            candidate.Result.ChangedDecisionCount,
            candidate.Result.TotalPotencyDelta,
            candidate.Result.DeathsDesignUptimeDelta,
            candidate.Result.GaugeLossDelta,
            candidate.Result.GluttonyDriftDelta,
            candidate.Result.EnshroudCountDelta,
            candidate.Result.CommunioCountDelta,
            candidate.Result.PerfectioCountDelta,
            candidate.Result.SacrificiumCountDelta,
            candidate.Result.LemureCountDelta,
            candidate.Result.ClipRiskDelta,
            candidate.Result.Reason
        };

    private sealed record OfflinePlannerBeamNode(
        RprTuningProfile Profile,
        OfflinePlannerCandidateResult Result,
        IReadOnlyList<string> Decisions);

    private static IReadOnlyList<RprTuningProfile> BuildOfflinePlannerCandidates()
    {
        var baseline = RprTuningProfile.Baseline;
        return
        [
            baseline with { Name = "enshroud_hold_8s", DecisionDomain = "Enshroud use or hold", Parameter = "EnshroudHoldBeforeArcaneSeconds", BaselineValue = "0", CandidateValue = "8", EnshroudHoldBeforeArcaneSeconds = 8 },
            baseline with { Name = "enshroud_hold_12s", DecisionDomain = "Enshroud use or hold", Parameter = "EnshroudHoldBeforeArcaneSeconds", BaselineValue = "0", CandidateValue = "12", EnshroudHoldBeforeArcaneSeconds = 12 },
            baseline with { Name = "enshroud_hold_16s", DecisionDomain = "Enshroud use or hold", Parameter = "EnshroudHoldBeforeArcaneSeconds", BaselineValue = "0", CandidateValue = "16", EnshroudHoldBeforeArcaneSeconds = 16 },
            baseline with { Name = "enshroud_hold_20s", DecisionDomain = "Enshroud use or hold", Parameter = "EnshroudHoldBeforeArcaneSeconds", BaselineValue = "0", CandidateValue = "20", EnshroudHoldBeforeArcaneSeconds = 20 },

            baseline with { Name = "dd_pre_enshroud_4gcd", DecisionDomain = "DD now or next GCD", Parameter = "PreAnyEnshroudGcds", BaselineValue = "5", CandidateValue = "4", PreAnyEnshroudGcds = 4 },
            baseline with { Name = "dd_pre_enshroud_4_5gcd", DecisionDomain = "DD now or next GCD", Parameter = "PreAnyEnshroudGcds", BaselineValue = "5", CandidateValue = "4.5", PreAnyEnshroudGcds = 4.5 },
            baseline with { Name = "dd_pre_enshroud_5_5gcd", DecisionDomain = "DD now or next GCD", Parameter = "PreAnyEnshroudGcds", BaselineValue = "5", CandidateValue = "5.5", PreAnyEnshroudGcds = 5.5 },
            baseline with { Name = "dd_pre_enshroud_6gcd", DecisionDomain = "DD now or next GCD", Parameter = "PreAnyEnshroudGcds", BaselineValue = "5", CandidateValue = "6", PreAnyEnshroudGcds = 6 },
            baseline with { Name = "dd_arcane_coverage_18s", DecisionDomain = "DD now or next GCD", Parameter = "ArcaneStartDeathsDesignCoverage", BaselineValue = "20", CandidateValue = "18", ArcaneStartDeathsDesignCoverage = 18 },
            baseline with { Name = "dd_arcane_coverage_22s", DecisionDomain = "DD now or next GCD", Parameter = "ArcaneStartDeathsDesignCoverage", BaselineValue = "20", CandidateValue = "22", ArcaneStartDeathsDesignCoverage = 22 },

            baseline with { Name = "soul_slice_combo_ac_end_20s", DecisionDomain = "Soul Slice or combo", Parameter = "ComboPriorityScope", BaselineValue = "disabled", CandidateValue = "AC end +20s", ComboPriorityScope = 1 },
            baseline with { Name = "soul_slice_combo_perfectio_15s", DecisionDomain = "Soul Slice or combo", Parameter = "ComboPriorityScope", BaselineValue = "disabled", CandidateValue = "Perfectio +15s", ComboPriorityScope = 2 },
            baseline with { Name = "soul_slice_release_near_charge_cap", DecisionDomain = "Soul Slice or combo", Parameter = "ComboPriorityScope", BaselineValue = "disabled", CandidateValue = "release near charge cap", ComboPriorityScope = 3 },
            baseline with { Name = "soul_slice_burst_hold_4s", DecisionDomain = "Soul Slice or combo", Parameter = "SoulSliceBurstSoonSeconds", BaselineValue = "6", CandidateValue = "4", SoulSliceBurstSoonSeconds = 4 },
            baseline with { Name = "soul_slice_burst_hold_8s", DecisionDomain = "Soul Slice or combo", Parameter = "SoulSliceBurstSoonSeconds", BaselineValue = "6", CandidateValue = "8", SoulSliceBurstSoonSeconds = 8 },

            baseline with { Name = "gluttony_hold_6s", DecisionDomain = "Gluttony now or Arcane Circle", Parameter = "GluttonyHoldBeforeArcaneSeconds", BaselineValue = "10", CandidateValue = "6", GluttonyHoldBeforeArcaneSeconds = 6 },
            baseline with { Name = "gluttony_hold_8s", DecisionDomain = "Gluttony now or Arcane Circle", Parameter = "GluttonyHoldBeforeArcaneSeconds", BaselineValue = "10", CandidateValue = "8", GluttonyHoldBeforeArcaneSeconds = 8 },
            baseline with { Name = "gluttony_hold_12s", DecisionDomain = "Gluttony now or Arcane Circle", Parameter = "GluttonyHoldBeforeArcaneSeconds", BaselineValue = "10", CandidateValue = "12", GluttonyHoldBeforeArcaneSeconds = 12 },
            baseline with { Name = "gluttony_hold_15s", DecisionDomain = "Gluttony now or Arcane Circle", Parameter = "GluttonyHoldBeforeArcaneSeconds", BaselineValue = "10", CandidateValue = "15", GluttonyHoldBeforeArcaneSeconds = 15 },

            baseline with { Name = "end_gauge_spend_4s", DecisionDomain = "Gauge spend before end or downtime", Parameter = "EndGaugeSpendWindowSeconds", BaselineValue = "0", CandidateValue = "4", EndGaugeSpendWindowSeconds = 4 },
            baseline with { Name = "end_gauge_spend_6s", DecisionDomain = "Gauge spend before end or downtime", Parameter = "EndGaugeSpendWindowSeconds", BaselineValue = "0", CandidateValue = "6", EndGaugeSpendWindowSeconds = 6 },
            baseline with { Name = "end_gauge_spend_8s", DecisionDomain = "Gauge spend before end or downtime", Parameter = "EndGaugeSpendWindowSeconds", BaselineValue = "0", CandidateValue = "8", EndGaugeSpendWindowSeconds = 8 },
            baseline with { Name = "end_gauge_spend_10s", DecisionDomain = "Gauge spend before end or downtime", Parameter = "EndGaugeSpendWindowSeconds", BaselineValue = "0", CandidateValue = "10", EndGaugeSpendWindowSeconds = 10 },
            baseline with { Name = "timeline_aware_burst_no_hold", DecisionDomain = "Burst now or after downtime", Parameter = "TimelineAwareBurstHold", BaselineValue = "true", CandidateValue = "false", TimelineAwareBurstHold = false },
            baseline with { Name = "dmu_ac_anchor_1_fflogs_median", DecisionDomain = "Dancing Mad Arcane Circle timing", Parameter = "DancingMadMedianArcaneCircleAnchorMask", BaselineValue = "0", CandidateValue = "1", DancingMadMedianArcaneCircleAnchorMask = 1 << 0 },
            baseline with { Name = "dmu_ac_anchor_2_fflogs_median", DecisionDomain = "Dancing Mad Arcane Circle timing", Parameter = "DancingMadMedianArcaneCircleAnchorMask", BaselineValue = "0", CandidateValue = "2", DancingMadMedianArcaneCircleAnchorMask = 1 << 1 },
            baseline with { Name = "dmu_ac_anchor_3_fflogs_median", DecisionDomain = "Dancing Mad Arcane Circle timing", Parameter = "DancingMadMedianArcaneCircleAnchorMask", BaselineValue = "0", CandidateValue = "4", DancingMadMedianArcaneCircleAnchorMask = 1 << 2 },
            baseline with { Name = "dmu_ac_anchor_4_fflogs_median", DecisionDomain = "Dancing Mad Arcane Circle timing", Parameter = "DancingMadMedianArcaneCircleAnchorMask", BaselineValue = "0", CandidateValue = "8", DancingMadMedianArcaneCircleAnchorMask = 1 << 3 },
            baseline with { Name = "dmu_ac_anchor_5_fflogs_median", DecisionDomain = "Dancing Mad Arcane Circle timing", Parameter = "DancingMadMedianArcaneCircleAnchorMask", BaselineValue = "0", CandidateValue = "16", DancingMadMedianArcaneCircleAnchorMask = 1 << 4 },
            baseline with { Name = "dmu_ac_anchor_6_fflogs_median", DecisionDomain = "Dancing Mad Arcane Circle timing", Parameter = "DancingMadMedianArcaneCircleAnchorMask", BaselineValue = "0", CandidateValue = "32", DancingMadMedianArcaneCircleAnchorMask = 1 << 5 },
            baseline with { Name = "dmu_ac_anchor_7_fflogs_median", DecisionDomain = "Dancing Mad Arcane Circle timing", Parameter = "DancingMadMedianArcaneCircleAnchorMask", BaselineValue = "0", CandidateValue = "64", DancingMadMedianArcaneCircleAnchorMask = 1 << 6 },
            baseline with { Name = "dmu_ac_anchor_8_fflogs_median", DecisionDomain = "Dancing Mad Arcane Circle timing", Parameter = "DancingMadMedianArcaneCircleAnchorMask", BaselineValue = "0", CandidateValue = "128", DancingMadMedianArcaneCircleAnchorMask = 1 << 7 },
            baseline with { Name = "dmu_ac_anchor_9_fflogs_median", DecisionDomain = "Dancing Mad Arcane Circle timing", Parameter = "DancingMadMedianArcaneCircleAnchorMask", BaselineValue = "0", CandidateValue = "256", DancingMadMedianArcaneCircleAnchorMask = 1 << 8 }
        ];
    }

    private static OfflinePlannerCandidateResult BuildOfflinePlannerCandidateResult(RprTuningProfile profile, RegressionOutput output, RegressionOutput baseline)
    {
        var candidateMetrics = AggregateMetrics(output.Scenarios);
        var baselineMetrics = AggregateMetrics(baseline.Scenarios);
        var hardFailCount = output.Summary.HardFailCountByRule.Values.Sum();
        var regressionCount = output.Summary.Regressions.Count;
        var totalPotencyDelta = candidateMetrics.TotalPotencyEstimate - baselineMetrics.TotalPotencyEstimate;
        var deathsDesignUptimeDelta = candidateMetrics.DeathsDesignUptime - baselineMetrics.DeathsDesignUptime;
        var redGaugeOvercapDelta = candidateMetrics.RedGaugeOvercap - baselineMetrics.RedGaugeOvercap;
        var blueGaugeOvercapDelta = candidateMetrics.BlueGaugeOvercap - baselineMetrics.BlueGaugeOvercap;
        var soulSliceChargeLossDelta = candidateMetrics.SoulSliceChargesLost - baselineMetrics.SoulSliceChargesLost;
        var gaugeLossDelta = redGaugeOvercapDelta + blueGaugeOvercapDelta + soulSliceChargeLossDelta;
        var decisionChanges = BuildPlannerDecisionChanges(output.Scenarios, baseline.Scenarios);
        var safetyFailures = new List<string>();
        if (hardFailCount > 0)
            safetyFailures.Add($"HardFail {hardFailCount.ToString(CultureInfo.InvariantCulture)}");
        if (regressionCount > 0)
            safetyFailures.Add($"regressions {regressionCount.ToString(CultureInfo.InvariantCulture)}");
        if (candidateMetrics.GcdUptime + 0.0001 < baselineMetrics.GcdUptime)
            safetyFailures.Add("GCD uptime worsened");
        if (candidateMetrics.GluttonyDriftSeconds > baselineMetrics.GluttonyDriftSeconds + 0.0001)
            safetyFailures.Add("Gluttony drift worsened");
        if (candidateMetrics.EnshroudCount < baselineMetrics.EnshroudCount)
            safetyFailures.Add("Enshroud count decreased");
        if (candidateMetrics.CommunioCount < baselineMetrics.CommunioCount)
            safetyFailures.Add("Communio count decreased");
        if (candidateMetrics.PerfectioCount < baselineMetrics.PerfectioCount)
            safetyFailures.Add("Perfectio count decreased");
        if (candidateMetrics.SacrificiumCount < baselineMetrics.SacrificiumCount)
            safetyFailures.Add("Sacrificium count decreased");
        if (candidateMetrics.LemureCount < baselineMetrics.LemureCount)
            safetyFailures.Add("Lemure count decreased");
        if (candidateMetrics.ClippingRiskCount > baselineMetrics.ClippingRiskCount)
            safetyFailures.Add("clip risk increased");

        var improvementFailures = new List<string>();
        if (totalPotencyDelta <= 0.0001)
            improvementFailures.Add("total potency did not improve");
        if (deathsDesignUptimeDelta <= 0.0000001)
            improvementFailures.Add("DD uptime did not improve");
        if (gaugeLossDelta >= -0.0001)
            improvementFailures.Add("aggregate gauge loss did not improve");
        if (decisionChanges.Count == 0)
            improvementFailures.Add("no rotation decision changed");

        var verdict = safetyFailures.Count > 0 ? "REJECTED" : improvementFailures.Count == 0 ? "PROVEN" : "NOT_PROVEN";
        var reason = verdict switch
        {
            "PROVEN" => "HardFail 0, regressions none, potency/DD uptime/gauge loss all improved",
            "REJECTED" => string.Join("; ", safetyFailures),
            _ => string.Join("; ", improvementFailures)
        };

        return new()
        {
            Name = profile.Name,
            DecisionDomain = profile.DecisionDomain,
            Parameter = profile.Parameter,
            BaselineValue = profile.BaselineValue,
            CandidateValue = profile.CandidateValue,
            Verdict = verdict,
            ScenarioCount = output.Summary.ScenarioCount,
            HardFailCount = hardFailCount,
            RegressionCount = regressionCount,
            ChangedDecisionCount = decisionChanges.Count,
            TotalPotencyDelta = totalPotencyDelta,
            DeathsDesignUptimeDelta = deathsDesignUptimeDelta,
            GaugeLossDelta = gaugeLossDelta,
            RedGaugeOvercapDelta = redGaugeOvercapDelta,
            BlueGaugeOvercapDelta = blueGaugeOvercapDelta,
            SoulSliceChargeLossDelta = soulSliceChargeLossDelta,
            GluttonyDriftDelta = candidateMetrics.GluttonyDriftSeconds - baselineMetrics.GluttonyDriftSeconds,
            EnshroudCountDelta = candidateMetrics.EnshroudCount - baselineMetrics.EnshroudCount,
            CommunioCountDelta = candidateMetrics.CommunioCount - baselineMetrics.CommunioCount,
            PerfectioCountDelta = candidateMetrics.PerfectioCount - baselineMetrics.PerfectioCount,
            SacrificiumCountDelta = candidateMetrics.SacrificiumCount - baselineMetrics.SacrificiumCount,
            LemureCountDelta = candidateMetrics.LemureCount - baselineMetrics.LemureCount,
            ClipRiskDelta = candidateMetrics.ClippingRiskCount - baselineMetrics.ClippingRiskCount,
            DecisionExamples = decisionChanges.Take(8).ToList(),
            Reason = reason
        };
    }

    private static List<string> BuildPlannerDecisionChanges(IReadOnlyList<ScenarioResult> candidate, IReadOnlyList<ScenarioResult> baseline)
    {
        var baselineByName = baseline.ToDictionary(s => s.Scenario.Name, StringComparer.Ordinal);
        var changes = new List<string>();
        foreach (var currentScenario in candidate)
        {
            if (!baselineByName.TryGetValue(currentScenario.Scenario.Name, out var previousScenario))
                continue;

            var previousByWindow = previousScenario.Frames.ToDictionary(f => f.GcdWindowIndex);
            foreach (var current in currentScenario.Frames)
            {
                if (!previousByWindow.TryGetValue(current.GcdWindowIndex, out var previous))
                    continue;
                if (current.SelectedGcd == previous.SelectedGcd && current.SelectedOgcds.SequenceEqual(previous.SelectedOgcds))
                    continue;

                changes.Add($"{currentScenario.Scenario.Name}@{current.Time.ToString("0.###", CultureInfo.InvariantCulture)}: GCD {previous.SelectedGcd ?? "none"}->{current.SelectedGcd ?? "none"}; oGCD {string.Join('|', previous.SelectedOgcds)}->{string.Join('|', current.SelectedOgcds)}");
            }
        }

        return changes;
    }

    private static int RunOptimizer(CliOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Out))
            throw new ArgumentException("Specify --out <directory>.");
        if (string.IsNullOrWhiteSpace(options.Baseline))
            throw new ArgumentException("Specify --baseline <baseline.json>.");

        var scenarios = options.Real ? RealRprHarness.Build(options.Patterns, options.Seed) : ScenarioCatalog.BuildAll();
        if (options.Real && !string.IsNullOrWhiteSpace(options.Scenario))
            scenarios = SelectRealScenarios(scenarios, options.Scenario);
        else if (!options.Real && !string.IsNullOrWhiteSpace(options.Scenario))
            scenarios = ScenarioCatalog.Select(options.Scenario);

        var baseline = ReadJson<RegressionOutput>(options.Baseline);
        Directory.CreateDirectory(options.Out!);
        var candidateRoot = Path.Combine(options.Out!, "candidates");
        if (Directory.Exists(candidateRoot))
            Directory.Delete(candidateRoot, recursive: true);
        Directory.CreateDirectory(candidateRoot);

        var candidates = options.Exhaustive ? BuildExhaustiveOptimizerCandidates() : BuildOptimizerCandidates();
        if (!string.IsNullOrWhiteSpace(options.Candidate))
            candidates = candidates
                .Where(c => c.Name.Contains(options.Candidate, StringComparison.OrdinalIgnoreCase))
                .ToList();
        var rows = options.Exhaustive
            ? candidates
                .AsParallel()
                .AsOrdered()
                .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
                .Select(profile => BuildOptimizerCandidate(profile, scenarios, baseline, options))
                .ToList()
            : BuildOptimizerCandidatesWithOutputs(candidates, scenarios, baseline, options, candidateRoot);

        var adoptable = rows
            .Where(r => r.Verdict == "ADOPTABLE")
            .OrderByDescending(r => r.ScoreDelta)
            .ThenByDescending(r => r.TotalPotencyDelta)
            .ToList();
        var best = adoptable.FirstOrDefault();

        WriteJson(Path.Combine(options.Out!, "optimizer_summary.json"), new
        {
            Baseline = options.Baseline,
            ScenarioCount = scenarios.Count,
            CandidateCount = rows.Count,
            AdoptableCount = adoptable.Count,
            BestCandidate = best,
            Candidates = rows
        });
        WriteOptimizerCandidatesCsv(Path.Combine(options.Out!, "optimizer_candidates.csv"), rows);
        WriteOptimizerCompare(Path.Combine(options.Out!, "optimizer_compare.md"), rows, best);

        Console.WriteLine($"RPR optimizer candidates: {rows.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"ADOPTABLE: {adoptable.Count.ToString(CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Best: {(best == null ? "none" : best.Name)}");
        Console.WriteLine($"Results: {Path.GetFullPath(options.Out!)}");
        return 0;
    }

    private static List<OptimizerCandidateResult> BuildOptimizerCandidatesWithOutputs(IReadOnlyList<RprTuningProfile> candidates, IReadOnlyList<ScenarioDefinition> scenarios, RegressionOutput baseline, CliOptions options, string candidateRoot)
    {
        var rows = new List<OptimizerCandidateResult>();
        foreach (var profile in candidates)
        {
            var results = RunScenarios(scenarios, profile, options.Real);
            var output = BuildOutput(results, baseline);
            var candidateDir = Path.Combine(candidateRoot, Sanitize(profile.Name));
            WriteOutputs(candidateDir, output, baseline, compactJson: options.Real && !options.RealFullOutput);
            rows.Add(BuildOptimizerCandidateResult(profile, output, baseline));
        }

        return rows;
    }

    private static OptimizerCandidateResult BuildOptimizerCandidate(RprTuningProfile profile, IReadOnlyList<ScenarioDefinition> scenarios, RegressionOutput baseline, CliOptions options)
    {
        var results = RunScenarios(scenarios, profile, options.Real);
        var output = BuildOutput(results, baseline);
        return BuildOptimizerCandidateResult(profile, output, baseline);
    }

    private static List<ScenarioResult> RunScenarios(IReadOnlyList<ScenarioDefinition> scenarios, RprTuningProfile profile, bool parallel)
    {
        if (!parallel)
            return scenarios.Select(new RprRotationEmulator(profile).Run).ToList();

        return scenarios
            .AsParallel()
            .AsOrdered()
            .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
            .Select(s => new RprRotationEmulator(profile).Run(s))
            .ToList();
    }

    private static IReadOnlyList<RprTuningProfile> BuildOptimizerCandidates()
    {
        var baseline = RprTuningProfile.Baseline;
        return
        [
            baseline with
            {
                Name = "gluttony_hold_before_arcane_8s",
                Parameter = "GluttonyHoldBeforeArcaneSeconds",
                BaselineValue = "10",
                CandidateValue = "8",
                GluttonyHoldBeforeArcaneSeconds = 8
            },
            baseline with
            {
                Name = "gluttony_hold_before_arcane_12s",
                Parameter = "GluttonyHoldBeforeArcaneSeconds",
                BaselineValue = "10",
                CandidateValue = "12",
                GluttonyHoldBeforeArcaneSeconds = 12
            },
            baseline with
            {
                Name = "soul_slice_burst_soon_4s",
                Parameter = "SoulSliceBurstSoonSeconds",
                BaselineValue = "6",
                CandidateValue = "4",
                SoulSliceBurstSoonSeconds = 4
            },
            baseline with
            {
                Name = "soul_slice_burst_soon_8s",
                Parameter = "SoulSliceBurstSoonSeconds",
                BaselineValue = "6",
                CandidateValue = "8",
                SoulSliceBurstSoonSeconds = 8
            },
            baseline with
            {
                Name = "blue100_enshroud_ac_threshold_40s",
                Parameter = "BlueGauge100StandaloneEnshroudArcaneThreshold",
                BaselineValue = "off",
                CandidateValue = "40",
                BlueGauge100StandaloneEnshroudArcaneThreshold = 40
            },
            baseline with
            {
                Name = "blue100_enshroud_ac_threshold_50s",
                Parameter = "BlueGauge100StandaloneEnshroudArcaneThreshold",
                BaselineValue = "off",
                CandidateValue = "50",
                BlueGauge100StandaloneEnshroudArcaneThreshold = 50
            },
            baseline with
            {
                Name = "perfectio_combo_protect_1gcd",
                Parameter = "PerfectioComboProtectGcds",
                BaselineValue = "0",
                CandidateValue = "1",
                PerfectioComboProtectGcds = 1
            },
            baseline with
            {
                Name = "perfectio_combo_protect_2gcd",
                Parameter = "PerfectioComboProtectGcds",
                BaselineValue = "0",
                CandidateValue = "2",
                PerfectioComboProtectGcds = 2
            },
            baseline with
            {
                Name = "pre_any_enshroud_4_5gcd",
                Parameter = "PreAnyEnshroudGcds",
                BaselineValue = "5",
                CandidateValue = "4.5",
                PreAnyEnshroudGcds = 4.5
            },
            baseline with
            {
                Name = "pre_any_enshroud_5_5gcd",
                Parameter = "PreAnyEnshroudGcds",
                BaselineValue = "5",
                CandidateValue = "5.5",
                PreAnyEnshroudGcds = 5.5
            },
            baseline with
            {
                Name = "even_burst_after_ac_20s",
                Parameter = "EvenBurstRemainingAfterArcaneCircle",
                BaselineValue = "22",
                CandidateValue = "20",
                EvenBurstRemainingAfterArcaneCircle = 20
            },
            baseline with
            {
                Name = "even_burst_after_ac_24s",
                Parameter = "EvenBurstRemainingAfterArcaneCircle",
                BaselineValue = "22",
                CandidateValue = "24",
                EvenBurstRemainingAfterArcaneCircle = 24
            },
            baseline with
            {
                Name = "even_burst_pre_arcane_18s",
                Parameter = "EvenBurstRemainingFromPreArcaneEnshroud",
                BaselineValue = "20",
                CandidateValue = "18",
                EvenBurstRemainingFromPreArcaneEnshroud = 18
            },
            baseline with
            {
                Name = "even_burst_pre_arcane_22s",
                Parameter = "EvenBurstRemainingFromPreArcaneEnshroud",
                BaselineValue = "20",
                CandidateValue = "22",
                EvenBurstRemainingFromPreArcaneEnshroud = 22
            },
            baseline with
            {
                Name = "even_burst_dd_safety_1s",
                Parameter = "EvenBurstDDSecondRefreshSafety",
                BaselineValue = "1.5",
                CandidateValue = "1",
                EvenBurstDDSecondRefreshSafety = 1
            },
            baseline with
            {
                Name = "even_burst_dd_safety_2s",
                Parameter = "EvenBurstDDSecondRefreshSafety",
                BaselineValue = "1.5",
                CandidateValue = "2",
                EvenBurstDDSecondRefreshSafety = 2
            },
            baseline with
            {
                Name = "combo_priority_current_all_burst_outside",
                Parameter = "ComboPriorityScope",
                BaselineValue = "emulator disabled",
                CandidateValue = "all burst outside",
                ComboPriorityScope = 0
            },
            baseline with
            {
                Name = "combo_priority_ac_end_20s",
                Parameter = "ComboPriorityScope",
                BaselineValue = "all burst outside",
                CandidateValue = "AC end +20s",
                ComboPriorityScope = 1
            },
            baseline with
            {
                Name = "combo_priority_perfectio_15s",
                Parameter = "ComboPriorityScope",
                BaselineValue = "all burst outside",
                CandidateValue = "Perfectio +15s",
                ComboPriorityScope = 2
            },
            baseline with
            {
                Name = "combo_priority_release_near_soul_slice_cap",
                Parameter = "ComboPriorityScope",
                BaselineValue = "all burst outside",
                CandidateValue = "release near Soul Slice cap",
                ComboPriorityScope = 3
            },
            baseline with
            {
                Name = "late_ph_soul_slice_blue_le_50",
                Parameter = "LateBurstSoulSlicePolicy",
                BaselineValue = "BlueGauge < 50",
                CandidateValue = "BlueGauge <= 50",
                LateBurstSoulSlicePolicy = 1
            },
            baseline with
            {
                Name = "late_ph_soul_slice_red_le_50",
                Parameter = "LateBurstSoulSlicePolicy",
                BaselineValue = "BlueGauge < 50",
                CandidateValue = "RedGauge <= 50",
                LateBurstSoulSlicePolicy = 2
            },
            baseline with
            {
                Name = "late_ph_soul_slice_burst_completion_safe",
                Parameter = "LateBurstSoulSlicePolicy",
                BaselineValue = "BlueGauge < 50",
                CandidateValue = "burst completion safe",
                LateBurstSoulSlicePolicy = 3
            },
            baseline with
            {
                Name = "arcane_start_dd_coverage_16s",
                Parameter = "ArcaneStartDeathsDesignCoverage",
                BaselineValue = "20",
                CandidateValue = "16",
                ArcaneStartDeathsDesignCoverage = 16
            },
            baseline with
            {
                Name = "arcane_start_dd_coverage_18s",
                Parameter = "ArcaneStartDeathsDesignCoverage",
                BaselineValue = "20",
                CandidateValue = "18",
                ArcaneStartDeathsDesignCoverage = 18
            },
            baseline with
            {
                Name = "arcane_start_dd_coverage_22s",
                Parameter = "ArcaneStartDeathsDesignCoverage",
                BaselineValue = "20",
                CandidateValue = "22",
                ArcaneStartDeathsDesignCoverage = 22
            },
            baseline with
            {
                Name = "timeline_aware_burst_no_hold",
                Parameter = "TimelineAwareBurstHold",
                BaselineValue = "true",
                CandidateValue = "false",
                TimelineAwareBurstHold = false
            }
        ];
    }

    private static IReadOnlyList<RprTuningProfile> BuildExhaustiveOptimizerCandidates()
    {
        var candidates = new List<RprTuningProfile>();
        var baseline = RprTuningProfile.Baseline;
        var dimensions = new OptimizerDimension[]
        {
            new("GluttonyHoldBeforeArcaneSeconds", "g", "10", [8.0, 12.0], (profile, value) => profile with { GluttonyHoldBeforeArcaneSeconds = value }),
            new("SoulSliceBurstSoonSeconds", "ss", "6", [4.0, 8.0], (profile, value) => profile with { SoulSliceBurstSoonSeconds = value }),
            new("BlueGauge100StandaloneEnshroudArcaneThreshold", "b", "off", [40.0, 50.0], (profile, value) => profile with { BlueGauge100StandaloneEnshroudArcaneThreshold = value }),
            new("PerfectioComboProtectGcds", "p", "0", [1.0, 2.0], (profile, value) => profile with { PerfectioComboProtectGcds = (int)value }),
            new("PreAnyEnshroudGcds", "pa", "5", [4.5, 5.5], (profile, value) => profile with { PreAnyEnshroudGcds = value }),
            new("EvenBurstRemainingAfterArcaneCircle", "aa", "22", [20.0, 24.0], (profile, value) => profile with { EvenBurstRemainingAfterArcaneCircle = value }),
            new("EvenBurstRemainingFromPreArcaneEnshroud", "pe", "20", [18.0, 22.0], (profile, value) => profile with { EvenBurstRemainingFromPreArcaneEnshroud = value }),
            new("EvenBurstDDSecondRefreshSafety", "dd", "1.5", [1.0, 2.0], (profile, value) => profile with { EvenBurstDDSecondRefreshSafety = value })
        };

        for (var mask = 1; mask < 1 << dimensions.Length; ++mask)
        {
            var selected = Enumerable.Range(0, dimensions.Length).Where(i => (mask & (1 << i)) != 0).ToArray();
            if (selected.Length > 3)
                continue;

            AddExhaustiveCandidates(candidates, baseline, dimensions, selected, 0, [], []);
        }

        return candidates;
    }

    private static void AddExhaustiveCandidates(List<RprTuningProfile> candidates, RprTuningProfile profile, OptimizerDimension[] dimensions, int[] selected, int index, List<string> nameParts, List<string> valueParts)
    {
        if (index >= selected.Length)
        {
            var name = $"exhaustive_{string.Join("_", nameParts)}";
            candidates.Add(profile with
            {
                Name = name,
                Parameter = "exhaustive",
                BaselineValue = "baseline",
                CandidateValue = string.Join(", ", valueParts)
            });
            return;
        }

        var dimension = dimensions[selected[index]];
        foreach (var value in dimension.Values)
        {
            var valueText = FormatCandidateValue(value);
            nameParts.Add($"{dimension.ShortName}{valueText}");
            valueParts.Add($"{dimension.Label}={dimension.BaselineValue}->{valueText}");
            AddExhaustiveCandidates(candidates, dimension.Apply(profile, value), dimensions, selected, index + 1, nameParts, valueParts);
            nameParts.RemoveAt(nameParts.Count - 1);
            valueParts.RemoveAt(valueParts.Count - 1);
        }
    }

    private static string FormatCandidateValue(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', '_');

    private sealed record OptimizerDimension(string Label, string ShortName, string BaselineValue, double[] Values, Func<RprTuningProfile, double, RprTuningProfile> Apply);

    private static OptimizerCandidateResult BuildOptimizerCandidateResult(RprTuningProfile profile, RegressionOutput output, RegressionOutput baseline)
    {
        var candidateMetrics = AggregateMetrics(output.Scenarios);
        var baselineMetrics = AggregateMetrics(baseline.Scenarios);
        var hardFailCount = output.Summary.HardFailCountByRule.Values.Sum();
        var baselineHardFailCount = baseline.Summary.HardFailCountByRule.Values.Sum();
        var hardFailDelta = hardFailCount - baselineHardFailCount;
        var regressionCount = output.Summary.Regressions.Count;
        var scoreDelta = output.Summary.CandidateScore - output.Summary.BaselineScore;
        var totalPotencyDelta = candidateMetrics.TotalPotencyEstimate - baselineMetrics.TotalPotencyEstimate;
        var gcdUptimeDelta = candidateMetrics.GcdUptime - baselineMetrics.GcdUptime;
        var deathsDesignUptimeDelta = candidateMetrics.DeathsDesignUptime - baselineMetrics.DeathsDesignUptime;
        var redGaugeOvercapDelta = candidateMetrics.RedGaugeOvercap - baselineMetrics.RedGaugeOvercap;
        var blueGaugeOvercapDelta = candidateMetrics.BlueGaugeOvercap - baselineMetrics.BlueGaugeOvercap;
        var soulSliceChargeLossDelta = candidateMetrics.SoulSliceChargesLost - baselineMetrics.SoulSliceChargesLost;
        var gluttonyDriftDelta = candidateMetrics.GluttonyDriftSeconds - baselineMetrics.GluttonyDriftSeconds;
        var enshroudCountDelta = candidateMetrics.EnshroudCount - baselineMetrics.EnshroudCount;
        var communioCountDelta = candidateMetrics.CommunioCount - baselineMetrics.CommunioCount;
        var perfectioCountDelta = candidateMetrics.PerfectioCount - baselineMetrics.PerfectioCount;
        var sacrificiumCountDelta = candidateMetrics.SacrificiumCount - baselineMetrics.SacrificiumCount;
        var lemureCountDelta = candidateMetrics.LemureCount - baselineMetrics.LemureCount;
        var clipRiskDelta = candidateMetrics.ClippingRiskCount - baselineMetrics.ClippingRiskCount;
        var comboBreakDelta = candidateMetrics.ComboBreakCount - baselineMetrics.ComboBreakCount;
        var disqualifiers = new List<string>();
        if (hardFailDelta > 0)
            disqualifiers.Add($"HardFail +{hardFailDelta.ToString(CultureInfo.InvariantCulture)} ({hardFailCount.ToString(CultureInfo.InvariantCulture)} total)");
        if (regressionCount > 0)
            disqualifiers.Add($"Regressions {regressionCount.ToString(CultureInfo.InvariantCulture)}");
        if (gcdUptimeDelta < -0.0001)
            disqualifiers.Add("GCD uptime worsened");
        if (deathsDesignUptimeDelta < -0.0001)
            disqualifiers.Add("DD uptime worsened");
        if (redGaugeOvercapDelta > 0)
            disqualifiers.Add("RedGauge overcap increased");
        if (blueGaugeOvercapDelta > 0)
            disqualifiers.Add("BlueGauge overcap increased");
        if (soulSliceChargeLossDelta > 0)
            disqualifiers.Add("Soul Slice charge loss increased");
        if (gluttonyDriftDelta > 0.0001)
            disqualifiers.Add("Gluttony drift increased");
        if (enshroudCountDelta < 0)
            disqualifiers.Add("Enshroud count decreased");
        if (communioCountDelta < 0)
            disqualifiers.Add("Communio count decreased");
        if (perfectioCountDelta < 0)
            disqualifiers.Add("Perfectio count decreased");
        if (sacrificiumCountDelta < 0)
            disqualifiers.Add("Sacrificium count decreased");
        if (lemureCountDelta < 0)
            disqualifiers.Add("Lemure count decreased");
        if (clipRiskDelta > 0)
            disqualifiers.Add("clip risk increased");
        if (comboBreakDelta > 0)
            disqualifiers.Add("combo breaks increased");

        var verdict = disqualifiers.Count == 0 ? "ADOPTABLE" : "REJECTED";
        var reason = verdict == "ADOPTABLE" ? "strict optimizer gates passed" : string.Join("; ", disqualifiers);

        return new()
        {
            Name = profile.Name,
            Parameter = profile.Parameter,
            BaselineValue = profile.BaselineValue,
            CandidateValue = profile.CandidateValue,
            Verdict = verdict,
            ScenarioCount = output.Summary.ScenarioCount,
            Passed = output.Summary.PassCount,
            Failed = output.Summary.FailCount,
            HardFailCount = hardFailCount,
            HardFailDelta = hardFailDelta,
            RegressionCount = regressionCount,
            ScoreDelta = scoreDelta,
            TotalPotencyDelta = totalPotencyDelta,
            GcdUptimeDelta = gcdUptimeDelta,
            DeathsDesignUptimeDelta = deathsDesignUptimeDelta,
            RedGaugeOvercapDelta = redGaugeOvercapDelta,
            BlueGaugeOvercapDelta = blueGaugeOvercapDelta,
            SoulSliceChargeLossDelta = soulSliceChargeLossDelta,
            GluttonyDriftDelta = gluttonyDriftDelta,
            EnshroudCountDelta = enshroudCountDelta,
            CommunioCountDelta = communioCountDelta,
            PerfectioCountDelta = perfectioCountDelta,
            SacrificiumCountDelta = sacrificiumCountDelta,
            LemureCountDelta = lemureCountDelta,
            ClipRiskDelta = clipRiskDelta,
            ComboBreakDelta = comboBreakDelta,
            AdoptabilityReason = reason
        };
    }

    private static ScenarioMetrics AggregateMetrics(IReadOnlyList<ScenarioResult> scenarios)
        => new()
        {
            TotalPotencyEstimate = scenarios.Sum(s => s.Metrics.TotalPotencyEstimate),
            GcdUptime = scenarios.Count == 0 ? 0 : scenarios.Average(s => s.Metrics.GcdUptime),
            GcdCount = scenarios.Sum(s => s.Metrics.GcdCount),
            ArcaneCircleCount = scenarios.Sum(s => s.Metrics.ArcaneCircleCount),
            GluttonyCount = scenarios.Sum(s => s.Metrics.GluttonyCount),
            GluttonyDriftSeconds = scenarios.Sum(s => s.Metrics.GluttonyDriftSeconds),
            EnshroudCount = scenarios.Sum(s => s.Metrics.EnshroudCount),
            CommunioCount = scenarios.Sum(s => s.Metrics.CommunioCount),
            PerfectioCount = scenarios.Sum(s => s.Metrics.PerfectioCount),
            LemureCount = scenarios.Sum(s => s.Metrics.LemureCount),
            SacrificiumCount = scenarios.Sum(s => s.Metrics.SacrificiumCount),
            SoulSliceChargesLost = scenarios.Sum(s => s.Metrics.SoulSliceChargesLost),
            RedGaugeOvercap = scenarios.Sum(s => s.Metrics.RedGaugeOvercap),
            BlueGaugeOvercap = scenarios.Sum(s => s.Metrics.BlueGaugeOvercap),
            DeathsDesignUptime = scenarios.Count == 0 ? 0 : scenarios.Average(s => s.Metrics.DeathsDesignUptime),
            DeathsDesignRefreshCount = scenarios.Sum(s => s.Metrics.DeathsDesignRefreshCount),
            WastedDeathsDesignRefreshCount = scenarios.Sum(s => s.Metrics.WastedDeathsDesignRefreshCount),
            RangedGcdCount = scenarios.Sum(s => s.Metrics.RangedGcdCount),
            InvalidTargetFallbackCount = scenarios.Sum(s => s.Metrics.InvalidTargetFallbackCount),
            SkippedGcdCount = scenarios.Sum(s => s.Metrics.SkippedGcdCount),
            ClippingRiskCount = scenarios.Sum(s => s.Metrics.ClippingRiskCount),
            ComboBreakCount = scenarios.Sum(s => s.Metrics.ComboBreakCount)
        };

    private static void WriteRealHarnessSummary(string outDir, RegressionOutput output, CliOptions options)
    {
        var hardFailTotal = output.Summary.HardFailCountByRule.Values.Sum();
        var failed = output.Scenarios.Where(s => s.HardFails.Count > 0).Take(100).Select(s => new
        {
            s.Scenario.Name,
            s.Scenario.Seed,
            s.Scenario.KillTime,
            s.Scenario.Gcd,
            s.Scenario.Opener,
            s.Scenario.Potion,
            s.Scenario.InitialMode,
            s.Scenario.Level,
            HardFails = string.Join("|", s.HardFails)
        }).ToList();

        WriteJson(Path.Combine(outDir, "real_harness_summary.json"), new
        {
            options.Patterns,
            options.Seed,
            output.Summary.ScenarioCount,
            output.Summary.PassCount,
            output.Summary.FailCount,
            HardFailTotal = hardFailTotal,
            output.Summary.HardFailCountByRule,
            FirstFailures = failed
        });

        var sb = new StringBuilder();
        sb.AppendLine("# RPR real harness summary");
        sb.AppendLine();
        sb.AppendLine($"- patterns requested: {options.Patterns.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- seed: {options.Seed.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- scenarios: {output.Summary.ScenarioCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- passed: {output.Summary.PassCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- failed: {output.Summary.FailCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- hardfail total: {hardFailTotal.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## HardFail by rule");
        if (output.Summary.HardFailCountByRule.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in output.Summary.HardFailCountByRule)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## First failures");
        if (failed.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var failure in failed)
                sb.AppendLine($"- {failure.Name}: {failure.HardFails}");

        File.WriteAllText(Path.Combine(outDir, "real_harness_summary.md"), sb.ToString());
        WriteRealHardFailAnalysis(outDir, output);
    }

    private static void WriteRealHardFailAnalysis(string outDir, RegressionOutput output)
    {
        var classified = output.Scenarios
            .Where(s => s.HardFails.Count > 0)
            .Select(s => new
            {
                Scenario = s.Scenario.Name,
                s.Scenario.Level,
                s.Scenario.KillTime,
                s.Scenario.Gcd,
                Opener = s.Scenario.Opener.ToString(),
                Potion = s.Scenario.Potion.ToString(),
                Mode = s.Scenario.InitialMode.ToString(),
                HardFails = s.HardFails.Select(f => f.ToString()).ToList(),
                Buckets = ClassifyRealHardFail(s).ToList(),
                Metrics = new
                {
                    s.Metrics.ArcaneCircleCount,
                    s.Metrics.GluttonyCount,
                    s.Metrics.EnshroudCount,
                    s.Metrics.CommunioCount,
                    s.Metrics.PerfectioCount,
                    s.Metrics.RedGaugeOvercap,
                    s.Metrics.BlueGaugeOvercap,
                    s.Metrics.DeathsDesignUptime,
                    s.Metrics.DeathsDesignRefreshCount,
                    s.Metrics.ClippingRiskCount
                },
                Representative = RepresentativeFailureFrame(s)
            })
            .ToList();

        var bucketCounts = classified
            .SelectMany(s => s.Buckets)
            .GroupBy(b => b)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .Select(g => new { Bucket = g.Key, Count = g.Count() })
            .ToList();

        var soulSliceClassification = BuildSoulSliceOvercapClassification(output);

        WriteJson(Path.Combine(outDir, "real_hardfail_buckets.json"), new
        {
            ScenarioCount = output.Summary.ScenarioCount,
            FailedScenarioCount = classified.Count,
            BucketCounts = bucketCounts,
            SoulSliceOvercap = soulSliceClassification,
            Scenarios = classified.Take(500).ToList()
        });

        var sb = new StringBuilder();
        sb.AppendLine("# RPR real harness HardFail analysis");
        sb.AppendLine();
        sb.AppendLine($"- scenarios: {output.Summary.ScenarioCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- failed scenarios: {classified.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Bucket counts");
        if (bucketCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var bucket in bucketCounts)
                sb.AppendLine($"- {bucket.Bucket}: {bucket.Count.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Representative scenarios");
        foreach (var item in classified.Take(40))
        {
            var frame = item.Representative;
            sb.AppendLine($"- {item.Scenario}: {string.Join("|", item.HardFails)}; {string.Join(", ", item.Buckets)}; t={frame?.Time.ToString(CultureInfo.InvariantCulture) ?? "n/a"} gcd={frame?.SelectedGcd ?? ""} ogcd={frame?.Ogcd ?? ""} red={frame?.RedGauge.ToString(CultureInfo.InvariantCulture) ?? ""} blue={frame?.BlueGauge.ToString(CultureInfo.InvariantCulture) ?? ""} dd={frame?.DeathsDesignLeft.ToString(CultureInfo.InvariantCulture) ?? ""}");
        }

        sb.AppendLine();
        sb.AppendLine("## Soul Slice charge overcap sub-classification");
        sb.AppendLine($"- bucket scenarios: {soulSliceClassification.BucketScenarioCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- excluded_low_level: {soulSliceClassification.ExcludedLowLevel.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- excluded_pushed_by_higher_priority_gcd: {soulSliceClassification.ExcludedPushedByHigherPriorityGcd.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- excluded_target_loss_or_kill_edge: {soulSliceClassification.ExcludedTargetLossOrKillEdge.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- excluded_unrealistic_initial_state: {soulSliceClassification.ExcludedUnrealisticInitialState.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- excluded_seeded_opening_gluttony_after_enshroud: {soulSliceClassification.ExcludedSeededOpeningGluttonyAfterEnshroud.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- true_rpr_logic_candidate: {soulSliceClassification.TrueRprLogicCandidateCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("### True RPR logic candidates (first 40)");
        if (soulSliceClassification.TrueCandidates.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var candidate in soulSliceClassification.TrueCandidates.Take(40))
            {
                var frame = candidate.Frame;
                sb.AppendLine($"- {candidate.Scenario}: frames={candidate.SurvivingFrameCount.ToString(CultureInfo.InvariantCulture)} t={frame.Time.ToString(CultureInfo.InvariantCulture)} gcd={frame.SelectedGcd ?? ""} ogcd={frame.Ogcd} red={frame.RedGauge.ToString(CultureInfo.InvariantCulture)} blueGauge={frame.BlueGauge.ToString(CultureInfo.InvariantCulture)} dd={frame.DeathsDesignLeft.ToString(CultureInfo.InvariantCulture)} acIn={frame.ArcaneCircleReadyIn.ToString(CultureInfo.InvariantCulture)} charges={frame.SoulSliceCharges.ToString(CultureInfo.InvariantCulture)} reason={frame.Reason}");
            }

        File.WriteAllText(Path.Combine(outDir, "real_hardfail_analysis.md"), sb.ToString());
        WriteDdDroppedEnshroudClassification(outDir, output);
        WriteEvenBurstDeathsDesignClassification(outDir, output);
        WriteGluttonyDriftClassification(outDir, output);
        WriteModeSwitchGluttonyDriftClassification(outDir, output);
        WriteBurstFailureClassification(outDir, output);
        WriteIllegalActionClassification(outDir, output);
        WriteRemainingGaugeClassification(outDir, output);
        WriteLowLevelGaugeClassification(outDir, output);
        WriteActionabilitySummary(outDir, output, soulSliceClassification);
        WriteSoftOpportunityAnalysis(outDir, output);
        WriteSoftMetricReviewBreakdown(outDir, output);
        WriteAdoptionCandidateRanking(outDir, output);
    }

    private static void WriteSoftOpportunityAnalysis(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .Where(s => s.Passed)
            .SelectMany(ClassifySoftOpportunities)
            .ToList();
        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var actionabilityCounts = cases
            .GroupBy(c => c.Actionability)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        WriteJson(Path.Combine(outDir, "soft_opportunity_buckets.json"), new
        {
            PassedScenarioCount = output.Scenarios.Count(s => s.Passed),
            CaseCount = cases.Count,
            CategoryCounts = categoryCounts,
            ActionabilityCounts = actionabilityCounts,
            Cases = cases.Take(1000).ToList()
        });

        var sb = new StringBuilder();
        sb.AppendLine("# RPR soft opportunity analysis");
        sb.AppendLine();
        sb.AppendLine($"- passed scenarios: {output.Scenarios.Count(s => s.Passed).ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- soft cases: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        if (categoryCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in categoryCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        if (actionabilityCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in actionabilityCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- This report only ranks passed-scenario soft metrics. It does not mark RPR.cs changes as safe by itself.");
        sb.AppendLine("- clip_risk cases are soft because weave order and potion/Arcane Circle checks already passed; broad Arcane Circle weave restriction was previously rejected.");
        sb.AppendLine("- low_dd_uptime cases should be reviewed only when no target/downtime/kill edge explains the loss.");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 20)");
            foreach (var item in cases.Where(c => c.Category == category).Take(20))
                sb.AppendLine($"- {item.Scenario}: lv={item.Level.ToString(CultureInfo.InvariantCulture)} kt={item.KillTime.ToString(CultureInfo.InvariantCulture)} gcd={item.Gcd.ToString(CultureInfo.InvariantCulture)} metric={item.Metric.ToString(CultureInfo.InvariantCulture)} t={item.Time.ToString(CultureInfo.InvariantCulture)} gcdAction={item.SelectedGcd ?? ""} ogcd={item.Ogcd} actionability={item.Actionability} note={item.Note}");
        }

        File.WriteAllText(Path.Combine(outDir, "soft_opportunity_analysis.md"), sb.ToString());
    }

    private static void WriteSoftMetricReviewBreakdown(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .Where(s => s.Passed)
            .SelectMany(ClassifySoftMetricReviewCase)
            .ToList();
        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var actionabilityCounts = cases
            .GroupBy(c => c.Actionability)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        WriteJson(Path.Combine(outDir, "soft_metric_review_buckets.json"), new
        {
            CaseCount = cases.Count,
            HypothesisCandidateCount = cases.Count(c => c.Actionability == "hypothesis_candidate_requires_candidate_run"),
            CategoryCounts = categoryCounts,
            ActionabilityCounts = actionabilityCounts,
            HypothesisCandidates = cases.Where(c => c.Actionability == "hypothesis_candidate_requires_candidate_run").ToList(),
            Cases = cases.Take(1000).ToList()
        });

        var candidateTimelineDir = Path.Combine(outDir, "soft_metric_candidate_timelines");
        if (Directory.Exists(candidateTimelineDir))
            Directory.Delete(candidateTimelineDir, recursive: true);
        Directory.CreateDirectory(candidateTimelineDir);
        var candidateScenarioNames = cases
            .Where(c => c.Actionability == "hypothesis_candidate_requires_candidate_run")
            .Select(c => c.Scenario)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var result in output.Scenarios.Where(s => candidateScenarioNames.Contains(s.Scenario.Name)))
            WriteTimeline(Path.Combine(candidateTimelineDir, $"{Sanitize(result.Scenario.Name)}.csv"), result);

        var sb = new StringBuilder();
        sb.AppendLine("# RPR soft metric review breakdown");
        sb.AppendLine();
        sb.AppendLine($"- cases: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- hypothesis_candidate_requires_candidate_run: {cases.Count(c => c.Actionability == "hypothesis_candidate_requires_candidate_run").ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        if (categoryCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in categoryCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        if (actionabilityCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in actionabilityCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- This report further classifies passed-scenario soft_metric_review cases only.");
        sb.AppendLine("- hypothesis_candidate_requires_candidate_run is not approval to edit RPR.cs; it only identifies a narrow idea that must still pass standard and real regression with no regressions.");
        sb.AppendLine("- Broad Arcane Circle weave reservation, broad Enshroud blocking, and hardfail rule changes remain rejected.");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 20)");
            foreach (var item in cases.Where(c => c.Category == category).Take(20))
                sb.AppendLine($"- {item.Scenario}: lv={item.Level.ToString(CultureInfo.InvariantCulture)} kt={item.KillTime.ToString(CultureInfo.InvariantCulture)} gcd={item.Gcd.ToString(CultureInfo.InvariantCulture)} metric={item.Metric.ToString(CultureInfo.InvariantCulture)} t={item.Time.ToString(CultureInfo.InvariantCulture)} gcdAction={item.SelectedGcd ?? ""} ogcd={item.Ogcd} red={item.RedGauge.ToString(CultureInfo.InvariantCulture)} blue={item.BlueGauge.ToString(CultureInfo.InvariantCulture)} dd={item.DeathsDesignLeft.ToString(CultureInfo.InvariantCulture)} acIn={item.ArcaneCircleReadyIn.ToString(CultureInfo.InvariantCulture)} acLeft={item.ArcaneCircleLeft.ToString(CultureInfo.InvariantCulture)} reason={item.Reason} actionability={item.Actionability} note={item.Note}");
        }

        File.WriteAllText(Path.Combine(outDir, "soft_metric_review_breakdown.md"), sb.ToString());
    }

    private static IEnumerable<SoftMetricReviewCase> ClassifySoftMetricReviewCase(ScenarioResult result)
    {
        var scenario = result.Scenario;

        if (result.Metrics.DeathsDesignUptime < 0.985)
        {
            var frame = result.Frames.FirstOrDefault(f => f.TargetAvailable && f.DeathsDesignLeft <= 0)
                ?? result.Frames.FirstOrDefault(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath")
                ?? result.Frames[0];
            if (!TargetLossOrKillEdge(scenario, frame))
                yield return SoftMetricReview(result, frame, ClassifyLowDeathsDesignUptimeReview(result, frame), result.Metrics.DeathsDesignUptime);
        }

        if (result.Metrics.BlueGaugeOvercap > 0)
        {
            var frame = result.Frames.FirstOrDefault(f => f.BlueGauge >= 100) ?? result.Frames[0];
            if (SoftBlueOvercapCategory(scenario, frame) == "blue_overcap_soft_review")
                yield return SoftMetricReview(result, frame, ClassifyBlueOvercapSoftReview(result, frame), result.Metrics.BlueGaugeOvercap);
        }

        if (result.Metrics.RedGaugeOvercap > 0)
        {
            var frame = result.Frames.FirstOrDefault(f => f.RedGauge >= 100) ?? result.Frames[0];
            if (SoftRedOvercapCategory(scenario, frame) == "red_overcap_soft_review")
                yield return SoftMetricReview(result, frame, ClassifyRedOvercapSoftReview(result, frame), result.Metrics.RedGaugeOvercap);
        }

        if (result.Metrics.SkippedGcdCount > 0)
        {
            var frame = result.Frames.FirstOrDefault(f => f.ExpectedActionPossible && f.SelectedGcd == null) ?? result.Frames[0];
            if (!TargetLossOrKillEdge(scenario, frame))
                yield return SoftMetricReview(result, frame, "skipped_gcd_soft_review", result.Metrics.SkippedGcdCount);
        }
    }

    private static string ClassifyLowDeathsDesignUptimeReview(ScenarioResult result, ActionFrame frame)
    {
        if (frame.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath")
            return "low_dd_refresh_already_selected";
        if (frame.Time < 30 || UnrealisticInitialState(result.Scenario, frame))
            return "low_dd_opener_or_seed_pressure";
        if (frame.ArcaneCircleLeft > 0 || frame.ArcaneCircleReadyIn <= result.Scenario.Gcd * 3)
            return "low_dd_burst_window_tradeoff";
        if (frame.SelectedGcd is "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine"
            || frame.ReaverState != ReaverState.None)
            return "low_dd_reaver_executioner_priority";
        if (frame.BlueSouls > 0 || frame.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" or "Communio")
            return "low_dd_enshroud_occupancy";
        if (frame.PerfectioParata || frame.SelectedGcd == "Perfectio")
            return "low_dd_perfectio_priority";
        return "low_dd_refresh_hypothesis_candidate";
    }

    private static string ClassifyBlueOvercapSoftReview(ScenarioResult result, ActionFrame frame)
    {
        if (frame.RotationMode == RotationMode.Basic)
            return "blue_overcap_basic_mode_no_new_enshroud";
        if (frame.SelectedOgcds.Contains("Enshroud"))
            return "blue_overcap_enshroud_already_selected";
        if (frame.Time < 30 || UnrealisticInitialState(result.Scenario, frame))
            return "blue_overcap_opener_or_seed_pressure";
        if (frame.ArcaneCircleLeft > 0 || frame.ArcaneCircleReadyIn <= result.Scenario.Gcd * 3 || frame.SelectedOgcds.Contains("ArcaneCircle"))
            return "blue_overcap_burst_window_density";
        if (frame.DeathsDesignLeft <= result.Scenario.Gcd * 5 + 1.0)
            return "blue_overcap_dd_insufficient_for_enshroud";
        if (!string.IsNullOrEmpty(frame.EnshroudReadyReason)
            && frame.ArcaneCircleReadyIn > 45
            && frame.ReaverState == ReaverState.None
            && frame.BlueSouls == 0
            && !frame.PerfectioParata
            && !frame.PlentifulHarvestReady)
            return "blue_overcap_single_enshroud_hypothesis_candidate";
        if (frame.SelectedGcd is "Slice" or "WaxingSlice" or "InfernalSlice" or "SpinningScythe" or "NightmareScythe")
            return "blue_overcap_filler_during_hold";
        return "blue_overcap_soft_review_no_safe_local_candidate";
    }

    private static string ClassifyRedOvercapSoftReview(ScenarioResult result, ActionFrame frame)
    {
        if (frame.Time < 30 || UnrealisticInitialState(result.Scenario, frame))
            return "red_overcap_opener_or_seed_pressure";
        if (frame.ArcaneCircleLeft > 0 || frame.ArcaneCircleReadyIn <= result.Scenario.Gcd * 3 || frame.SelectedOgcds.Contains("ArcaneCircle"))
            return "red_overcap_burst_window_density";
        if (frame.SelectedGcd is "Slice" or "WaxingSlice" or "InfernalSlice" or "SpinningScythe" or "NightmareScythe"
            && !frame.SelectedOgcds.Any(a => a is "BloodStalk" or "GrimSwathe" or "Gluttony"))
            return "red_overcap_spend_hypothesis_candidate";
        return "red_overcap_soft_review_no_safe_local_candidate";
    }

    private static string SoftMetricReviewActionability(string category)
        => category.EndsWith("_hypothesis_candidate", StringComparison.Ordinal)
            ? "hypothesis_candidate_requires_candidate_run"
            : category.Contains("seed", StringComparison.Ordinal) || category.Contains("opener", StringComparison.Ordinal)
                ? "harness_or_seed_pressure"
                : category.Contains("burst", StringComparison.Ordinal)
                    ? "blocked_by_burst_planning"
                    : category.Contains("priority", StringComparison.Ordinal) || category.Contains("occupancy", StringComparison.Ordinal)
                        ? "blocked_by_rotation_state"
                        : "soft_review_no_current_rpr_change";

    private static SoftMetricReviewCase SoftMetricReview(ScenarioResult result, ActionFrame frame, string category, double metric)
        => new(
            result.Scenario.Name,
            result.Scenario.Level,
            result.Scenario.KillTime,
            result.Scenario.Gcd,
            result.Scenario.Opener.ToString(),
            result.Scenario.Potion.ToString(),
            result.Scenario.InitialMode.ToString(),
            category,
            SoftMetricReviewActionability(category),
            metric,
            frame.Time,
            frame.SelectedGcd,
            string.Join("|", frame.SelectedOgcds),
            frame.RedGauge,
            frame.BlueGauge,
            frame.DeathsDesignLeft,
            frame.ArcaneCircleReadyIn,
            frame.ArcaneCircleLeft,
            frame.Reason,
            SoftMetricReviewNote(category));

    private static string SoftMetricReviewNote(string category)
        => SoftMetricReviewActionability(category) == "hypothesis_candidate_requires_candidate_run"
            ? "narrow candidate only; must be proven against adopted standard and real baselines before any RPR.cs change"
            : "classified away from immediate RPR.cs change";

    private sealed record SoftMetricReviewCase(
        string Scenario,
        int Level,
        double KillTime,
        double Gcd,
        string Opener,
        string Potion,
        string Mode,
        string Category,
        string Actionability,
        double Metric,
        double Time,
        string? SelectedGcd,
        string Ogcd,
        int RedGauge,
        int BlueGauge,
        double DeathsDesignLeft,
        double ArcaneCircleReadyIn,
        double ArcaneCircleLeft,
        string Reason,
        string Note);

    private static IEnumerable<SoftOpportunityCase> ClassifySoftOpportunities(ScenarioResult result)
    {
        var scenario = result.Scenario;
        var edge = scenario.FinalTwoGcdKill
            || scenario.Events.Any(e => e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable or ScenarioEventType.PrimaryTargetNull or ScenarioEventType.EmptyPriorityTargets);

        if (result.Metrics.ClippingRiskCount > 0)
        {
            var frame = result.Frames.FirstOrDefault(f => f.ClipRisk) ?? result.Frames[0];
            yield return SoftOpportunity(result, frame, SoftClipCategory(frame), "soft_weave_risk", result.Metrics.ClippingRiskCount, "order and potion checks pass; broad Arcane Circle weave restriction was rejected");
        }

        if (result.Metrics.DeathsDesignUptime < 0.985)
        {
            var frame = result.Frames.FirstOrDefault(f => f.TargetAvailable && f.DeathsDesignLeft <= 0)
                ?? result.Frames.FirstOrDefault(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath")
                ?? result.Frames[0];
            yield return SoftOpportunity(result, frame, "low_dd_uptime", edge ? "target_or_kill_edge_soft" : "soft_metric_review", result.Metrics.DeathsDesignUptime, edge ? "target/downtime/kill edge present" : "passed scenario with lower DD uptime; candidate must preserve burst counts");
        }

        if (result.Metrics.RedGaugeOvercap > 0)
        {
            var frame = result.Frames.FirstOrDefault(f => f.RedGauge >= 100) ?? result.Frames[0];
            var category = SoftRedOvercapCategory(scenario, frame);
            yield return SoftOpportunity(result, frame, category, SoftOvercapActionability(category), result.Metrics.RedGaugeOvercap, "passed scenario; check priority occupancy before any change");
        }

        if (result.Metrics.BlueGaugeOvercap > 0)
        {
            var frame = result.Frames.FirstOrDefault(f => f.BlueGauge >= 100) ?? result.Frames[0];
            var category = SoftBlueOvercapCategory(scenario, frame);
            yield return SoftOpportunity(result, frame, category, SoftOvercapActionability(category), result.Metrics.BlueGaugeOvercap, "passed scenario; Enshroud count must not be reduced");
        }

        if (result.Metrics.SkippedGcdCount > 0)
        {
            var frame = result.Frames.FirstOrDefault(f => f.ExpectedActionPossible && f.SelectedGcd == null) ?? result.Frames[0];
            yield return SoftOpportunity(result, frame, "skipped_gcd_soft", edge ? "target_or_kill_edge_soft" : "soft_metric_review", result.Metrics.SkippedGcdCount, edge ? "target/downtime/kill edge present" : "passed scenario with skipped GCD");
        }
    }

    private static string SoftClipCategory(ActionFrame frame)
    {
        var hasPotion = frame.SelectedOgcds.Contains("Potion");
        var hasArcaneCircle = frame.SelectedOgcds.Contains("ArcaneCircle");
        var hasEnshroud = frame.SelectedOgcds.Contains("Enshroud");
        var hasRedSpend = frame.SelectedOgcds.Any(a => a is "BloodStalk" or "GrimSwathe" or "Gluttony");
        var hasLemureOrSacrificium = frame.SelectedOgcds.Any(a => a is "LemuresSlice" or "LemuresScythe" or "Sacrificium");

        if (hasPotion && hasArcaneCircle && hasRedSpend)
            return "clip_potion_arcane_red_spend";
        if (hasPotion && hasArcaneCircle && hasEnshroud)
            return "clip_potion_arcane_enshroud";
        if (hasArcaneCircle && hasEnshroud && hasRedSpend)
            return "clip_arcane_enshroud_red_spend";
        if (hasArcaneCircle && hasLemureOrSacrificium)
            return "clip_arcane_lemure_sacrificium";
        if (hasEnshroud && hasLemureOrSacrificium)
            return "clip_enshroud_lemure_sacrificium";
        return "clip_other_soft";
    }

    private static string SoftRedOvercapCategory(ScenarioDefinition scenario, ActionFrame frame)
    {
        if (TargetLossOrKillEdge(scenario, frame))
            return "red_overcap_target_or_kill_edge";
        if (UnrealisticInitialState(scenario, frame))
            return "red_overcap_seeded_initial_state";
        if (frame.ReaverState != ReaverState.None || frame.SelectedGcd is "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine")
            return "red_overcap_reaver_executioner_priority";
        if (frame.BlueSouls > 0 || frame.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" or "Communio")
            return "red_overcap_enshroud_occupancy";
        if (frame.PerfectioParata || frame.SelectedGcd == "Perfectio")
            return "red_overcap_perfectio_priority";
        if (frame.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath" || frame.DeathsDesignRefreshReason != "")
            return "red_overcap_dd_priority";
        return "red_overcap_soft_review";
    }

    private static string SoftBlueOvercapCategory(ScenarioDefinition scenario, ActionFrame frame)
    {
        if (scenario.Level < 80)
            return "blue_overcap_low_level_no_enshroud";
        if (TargetLossOrKillEdge(scenario, frame))
            return "blue_overcap_target_or_kill_edge";
        if (UnrealisticInitialState(scenario, frame))
            return "blue_overcap_seeded_initial_state";
        if (frame.BlueSouls > 0 || frame.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" or "Communio")
            return "blue_overcap_enshroud_occupancy";
        if (frame.ReaverState != ReaverState.None || frame.SelectedGcd is "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine")
            return "blue_overcap_reaver_executioner_priority";
        if (frame.PerfectioParata || frame.SelectedGcd == "Perfectio")
            return "blue_overcap_perfectio_priority";
        if (frame.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath" || frame.DeathsDesignRefreshReason != "")
            return "blue_overcap_dd_priority";
        return "blue_overcap_soft_review";
    }

    private static string SoftOvercapActionability(string category)
        => category.EndsWith("_soft_review", StringComparison.Ordinal)
            ? "soft_metric_review"
            : category.Contains("target_or_kill_edge", StringComparison.Ordinal) || category.Contains("seeded_initial_state", StringComparison.Ordinal)
                ? "target_or_kill_edge_soft"
                : category.Contains("low_level", StringComparison.Ordinal)
                    ? "level_sync_model_pressure"
                    : "blocked_by_rotation_state";

    private static SoftOpportunityCase SoftOpportunity(ScenarioResult result, ActionFrame frame, string category, string actionability, double metric, string note)
        => new(
            result.Scenario.Name,
            result.Scenario.Level,
            result.Scenario.KillTime,
            result.Scenario.Gcd,
            result.Scenario.Opener.ToString(),
            result.Scenario.Potion.ToString(),
            result.Scenario.InitialMode.ToString(),
            category,
            actionability,
            metric,
            frame.Time,
            frame.SelectedGcd,
            string.Join("|", frame.SelectedOgcds),
            frame.Reason,
            note);

    private sealed record SoftOpportunityCase(
        string Scenario,
        int Level,
        double KillTime,
        double Gcd,
        string Opener,
        string Potion,
        string Mode,
        string Category,
        string Actionability,
        double Metric,
        double Time,
        string? SelectedGcd,
        string Ogcd,
        string Reason,
        string Note);

    private static void WriteAdoptionCandidateRanking(string outDir, RegressionOutput output)
    {
        var actionability = LoadJsonElement(Path.Combine(outDir, "actionability_summary.json"));
        var softMetric = LoadJsonElement(Path.Combine(outDir, "soft_metric_review_buckets.json"));
        var weave = LoadJsonElement(Path.Combine(outDir, "weave_actionability_buckets.json"));
        var highEnd = LoadJsonElement(Path.Combine(outDir, "high_end_rotation_opportunities.json"));

        var entries = new[]
        {
            CreateAdoptionCandidateEntry(
                "real_hardfail_actionability",
                JsonInt(actionability, "TrueRprLogicCandidateTotal"),
                JsonInt(actionability, "HardFailTotal"),
                "HardFail buckets after exclusions; only true RPR logic candidates should drive RPR.cs changes"),
            CreateAdoptionCandidateEntry(
                "soft_metric_review",
                JsonInt(softMetric, "HypothesisCandidateCount"),
                JsonInt(softMetric, "CaseCount"),
                "Passed-scenario soft metrics; candidate count excludes known priority, opener, edge, and rejected-pattern cases"),
            CreateAdoptionCandidateEntry(
                "weave_actionability",
                JsonInt(weave, "LocalCandidateCount"),
                JsonInt(weave, "CaseCount"),
                "Clip/weave density review; broad Arcane Circle gating is already rejected unless a local candidate remains"),
            CreateAdoptionCandidateEntry(
                "high_end_rotation_opportunities",
                JsonInt(highEnd, "CandidateCount"),
                JsonInt(highEnd, "CaseCount"),
                "Lv100 FullMode non-edge opportunities; candidates still require separate standard and real regression runs")
        };
        var candidateTotal = entries.Sum(e => e.CandidateCount);
        var regressions = output.Summary.Regressions.Count;

        WriteJson(Path.Combine(outDir, "adoption_candidate_ranking.json"), new
        {
            output.Summary.ScenarioCount,
            output.Summary.PassCount,
            output.Summary.FailCount,
            HardFailTotal = output.Summary.HardFailCountByRule.Values.Sum(),
            RegressionCount = regressions,
            CandidateTotal = candidateTotal,
            Verdict = candidateTotal == 0 && regressions == 0 ? "NO_CURRENT_RPR_LOGIC_CANDIDATE" : "REVIEW_REQUIRED",
            Entries = entries.OrderByDescending(e => e.CandidateCount).ThenBy(e => e.Name).ToList()
        });

        var sb = new StringBuilder();
        sb.AppendLine("# RPR adoption candidate ranking");
        sb.AppendLine();
        sb.AppendLine($"- scenarios: {output.Summary.ScenarioCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- passed: {output.Summary.PassCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- failed: {output.Summary.FailCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- hardfail total: {output.Summary.HardFailCountByRule.Values.Sum().ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- regressions: {(regressions == 0 ? "none" : regressions.ToString(CultureInfo.InvariantCulture))}");
        sb.AppendLine($"- candidate total: {candidateTotal.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Candidate pools");
        foreach (var entry in entries.OrderByDescending(e => e.CandidateCount).ThenBy(e => e.Name))
            sb.AppendLine($"- {entry.Name}: candidates={entry.CandidateCount.ToString(CultureInfo.InvariantCulture)}, reviewed={entry.ReviewedCount.ToString(CultureInfo.InvariantCulture)}, note={entry.Note}");

        sb.AppendLine();
        sb.AppendLine("## Decision");
        if (candidateTotal == 0 && regressions == 0)
        {
            sb.AppendLine("- No current RPR.cs logic candidate is justified by the analyzed buckets.");
            sb.AppendLine("- Keep RPR.cs unchanged until a new narrow hypothesis produces evidence and passes standard plus real regression.");
        }
        else
        {
            sb.AppendLine("- Review the nonzero candidate pool before touching RPR.cs.");
            sb.AppendLine("- Apply at most one local candidate and compare against the adopted standard and real baselines.");
        }

        File.WriteAllText(Path.Combine(outDir, "adoption_candidate_ranking.md"), sb.ToString());
    }

    private static AdoptionCandidateEntry CreateAdoptionCandidateEntry(string name, int candidateCount, int reviewedCount, string note)
        => new(name, candidateCount, reviewedCount, note);

    private sealed record AdoptionCandidateEntry(string Name, int CandidateCount, int ReviewedCount, string Note);

    private static void WriteActionabilitySummary(string outDir, RegressionOutput output, SoulSliceOvercapClassification soulSliceClassification)
    {
        var ddEnshroud = LoadJsonElement(Path.Combine(outDir, "dd_dropped_enshroud_buckets.json"));
        var ddEven = LoadJsonElement(Path.Combine(outDir, "dd_even_burst_buckets.json"));
        var driftFull = LoadJsonElement(Path.Combine(outDir, "drift_gluttony_buckets.json"));
        var driftMode = LoadJsonElement(Path.Combine(outDir, "drift_mode_switch_buckets.json"));
        var burst = LoadJsonElement(Path.Combine(outDir, "burst_failure_buckets.json"));
        var illegal = LoadJsonElement(Path.Combine(outDir, "illegal_action_buckets.json"));
        var remainingGauge = LoadJsonElement(Path.Combine(outDir, "remaining_gauge_buckets.json"));
        var lowLevelGauge = LoadJsonElement(Path.Combine(outDir, "low_level_gauge_buckets.json"));

        var entries = new[]
        {
            ActionabilityEntry("Soul Slice charge overcap", soulSliceClassification.BucketScenarioCount, soulSliceClassification.TrueRprLogicCandidateCount, "all survivors are excluded by low level, priority, target/kill edge, seed state, or seeded opener Gluttony"),
            ActionabilityEntry("Low-level gauge pressure", JsonInt(lowLevelGauge, "BucketScenarioCount"), JsonInt(lowLevelGauge, "TrueRprLogicCandidateCount"), "Lv60 spender gap, red-gauge block, seed state, target/kill edge, or DD priority"),
            ActionabilityEntry("Death's Design during Enshroud", JsonInt(ddEnshroud, "BucketScenarioCount"), JsonInt(ddEnshroud, "TrueRprLogicCandidateCount"), "broad fixes are proven unsafe or cases are harness/edge artifacts"),
            ActionabilityEntry("Death's Design during even burst", JsonInt(ddEven, "BucketScenarioCount"), JsonInt(ddEven, "TrueRprLogicCandidateCount"), "overlaps Enshroud or Reaver/Executioner occupancy"),
            ActionabilityEntry("Full-mode Gluttony drift", JsonInt(driftFull, "BucketScenarioCount"), JsonInt(driftFull, "TrueRprLogicCandidateCount"), "resource starvation, occupancy, target/downtime, or GCD-grid margin"),
            ActionabilityEntry("Mode-switch Gluttony drift", JsonInt(driftMode, "BucketScenarioCount"), JsonInt(driftMode, "TrueRprLogicCandidateCount"), "failing pairs are in Full segments, not direct Basic/Full timer defects"),
            ActionabilityEntry("Remaining GaugeFailure", JsonInt(remainingGauge, "BucketScenarioCount"), JsonInt(remainingGauge, "TrueRprLogicCandidateCount"), "Reaver/Executioner priority or burst occupancy"),
            ActionabilityEntry("BurstFailure", JsonInt(burst, "BucketScenarioCount"), JsonInt(burst, "TrueRprLogicCandidateCount"), "strict window edge, target/mode/kill edge, or DD uptime tradeoff"),
            ActionabilityEntry("IllegalAction / level sync", JsonInt(illegal, "BucketScenarioCount"), JsonInt(illegal, "TrueRprLogicCandidateCount"), "single target-fallback harness edge")
        };
        var totalTrue = entries.Sum(e => e.TrueRprLogicCandidates);

        WriteJson(Path.Combine(outDir, "actionability_summary.json"), new
        {
            output.Summary.ScenarioCount,
            output.Summary.PassCount,
            output.Summary.FailCount,
            HardFailTotal = output.Summary.HardFailCountByRule.Values.Sum(),
            output.Summary.HardFailCountByRule,
            output.Summary.BaselineScore,
            output.Summary.CandidateScore,
            output.Summary.Regressions,
            TrueRprLogicCandidateTotal = totalTrue,
            Buckets = entries
        });

        var sb = new StringBuilder();
        sb.AppendLine("# RPR real harness actionability summary");
        sb.AppendLine();
        sb.AppendLine($"- scenarios: {output.Summary.ScenarioCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- passed: {output.Summary.PassCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- failed: {output.Summary.FailCount.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- hardfail total: {output.Summary.HardFailCountByRule.Values.Sum().ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- regressions: {(output.Summary.Regressions.Count == 0 ? "none" : string.Join("; ", output.Summary.Regressions))}");
        sb.AppendLine($"- true RPR logic candidates: {totalTrue.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Bucket actionability");
        foreach (var entry in entries)
            sb.AppendLine($"- {entry.Name}: bucket={entry.BucketScenarioCount.ToString(CultureInfo.InvariantCulture)}, true={entry.TrueRprLogicCandidates.ToString(CultureInfo.InvariantCulture)}, note={entry.Note}");

        sb.AppendLine();
        sb.AppendLine("## Recommendation");
        if (totalTrue == 0)
        {
            sb.AppendLine("- Do not change RPR.cs from the current real-harness HardFail buckets.");
            sb.AppendLine("- Next safe work should be either a new targeted hypothesis outside these buckets, or harness-model work with separate baseline adoption.");
        }
        else
        {
            sb.AppendLine("- Inspect the true candidate buckets before changing RPR.cs. Apply at most one candidate and compare against the adopted baseline.");
        }

        File.WriteAllText(Path.Combine(outDir, "actionability_summary.md"), sb.ToString());
    }

    private static ActionabilitySummaryEntry ActionabilityEntry(string name, int bucketScenarioCount, int trueRprLogicCandidates, string note)
        => new(name, bucketScenarioCount, trueRprLogicCandidates, note);

    private static JsonElement LoadJsonElement(string path)
        => JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();

    private static int JsonInt(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Number
            ? property.GetInt32()
            : 0;

    private sealed record ActionabilitySummaryEntry(string Name, int BucketScenarioCount, int TrueRprLogicCandidates, string Note);

    private static void WriteEvenBurstDeathsDesignClassification(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .Where(s => s.HardFails.Contains(HardFailRule.DeathsDesignFailure))
            .SelectMany(ClassifyEvenBurstDeathsDesignFailure)
            .ToList();
        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        WriteJson(Path.Combine(outDir, "dd_even_burst_buckets.json"), new
        {
            BucketScenarioCount = cases.Select(c => c.Scenario).Distinct().Count(),
            CategoryCounts = categoryCounts,
            TrueRprLogicCandidateCount = cases.Count(c => c.RequiresRprReview),
            Cases = cases
        });

        var sb = new StringBuilder();
        sb.AppendLine("# dd_dropped_during_even_burst classification");
        sb.AppendLine();
        sb.AppendLine($"- bucket scenarios: {cases.Select(c => c.Scenario).Distinct().Count().ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- classified findings: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- true_rpr_logic_candidate: {cases.Count(c => c.RequiresRprReview).ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        foreach (var kvp in categoryCounts)
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 20)");
            foreach (var item in cases.Where(c => c.Category == category).Take(20))
                sb.AppendLine($"- {item.Scenario}: t={item.Time.ToString(CultureInfo.InvariantCulture)} gcd={item.SelectedGcd ?? ""} ogcd={item.Ogcd} dd={item.DeathsDesignLeft.ToString(CultureInfo.InvariantCulture)} souls={item.BlueSouls.ToString(CultureInfo.InvariantCulture)} reaver={item.ReaverState} acLeft={item.ArcaneCircleLeft.ToString(CultureInfo.InvariantCulture)} acIn={item.ArcaneCircleReadyIn.ToString(CultureInfo.InvariantCulture)} idealHost={item.IdealHost.ToString(CultureInfo.InvariantCulture)} perf={item.PerfectioParata.ToString(CultureInfo.InvariantCulture)} ph={item.PlentifulHarvestReady.ToString(CultureInfo.InvariantCulture)} reason={item.Reason}");
        }

        File.WriteAllText(Path.Combine(outDir, "dd_even_burst_classification.md"), sb.ToString());
    }

    private static IEnumerable<EvenBurstDeathsDesignCase> ClassifyEvenBurstDeathsDesignFailure(ScenarioResult result)
    {
        foreach (var frame in result.Frames.Where(f =>
            f.Time >= 110
            && f.Time % 120 <= 35
            && f.DeathsDesignLeft <= 0
            && f.TargetAvailable
            && f.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath")
            && !DdFinalOrDowntimeException(result.Scenario, f.Time)))
        {
            var category = ClassifyEvenBurstDeathsDesignFrame(frame);
            yield return new(
                result.Scenario.Name,
                category,
                category == "true_rpr_logic_candidate",
                frame.Time,
                frame.SelectedGcd,
                string.Join("|", frame.SelectedOgcds),
                frame.DeathsDesignLeft,
                frame.BlueSouls,
                frame.ReaverState,
                frame.ArcaneCircleLeft,
                frame.ArcaneCircleReadyIn,
                frame.IdealHost,
                frame.PerfectioParata,
                frame.PlentifulHarvestReady,
                frame.Reason);
            yield break;
        }
    }

    private static string ClassifyEvenBurstDeathsDesignFrame(ActionFrame frame)
    {
        if (frame.BlueSouls > 0)
            return "during_enshroud_overlap";
        if (frame.ReaverState != ReaverState.None)
            return "reaver_executioner_occupancy";
        if (frame.PerfectioParata || frame.SelectedGcd == "Perfectio")
            return "perfectio_window";
        if (frame.PlentifulHarvestReady || frame.IdealHost || frame.SelectedGcd == "PlentifulHarvest")
            return "ph_idealhost_window";
        if (frame.ArcaneCircleLeft > 0)
            return "raidbuff_window_uncovered";
        return "true_rpr_logic_candidate";
    }

    private sealed record EvenBurstDeathsDesignCase(
        string Scenario,
        string Category,
        bool RequiresRprReview,
        double Time,
        string? SelectedGcd,
        string Ogcd,
        double DeathsDesignLeft,
        int BlueSouls,
        ReaverState ReaverState,
        double ArcaneCircleLeft,
        double ArcaneCircleReadyIn,
        bool IdealHost,
        bool PerfectioParata,
        bool PlentifulHarvestReady,
        string Reason);

    private static void WriteRemainingGaugeClassification(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .Where(s => s.HardFails.Contains(HardFailRule.GaugeFailure))
            .SelectMany(ClassifyRemainingGaugeFailures)
            .ToList();
        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var bucketCounts = cases
            .GroupBy(c => c.Bucket)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var actionabilityCounts = cases
            .GroupBy(c => RemainingGaugeActionability(c.Category))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        WriteJson(Path.Combine(outDir, "remaining_gauge_buckets.json"), new
        {
            BucketScenarioCount = cases.Select(c => c.Scenario).Distinct().Count(),
            BucketCounts = bucketCounts,
            CategoryCounts = categoryCounts,
            ActionabilityCounts = actionabilityCounts,
            TrueRprLogicCandidateCount = cases.Count(c => c.RequiresRprReview),
            Cases = cases
        });

        var sb = new StringBuilder();
        sb.AppendLine("# remaining GaugeFailure classification");
        sb.AppendLine();
        sb.AppendLine($"- bucket scenarios: {cases.Select(c => c.Scenario).Distinct().Count().ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- classified findings: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- true_rpr_logic_candidate: {cases.Count(c => c.RequiresRprReview).ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Bucket counts");
        foreach (var kvp in bucketCounts)
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        foreach (var kvp in categoryCounts)
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        foreach (var kvp in actionabilityCounts)
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 20)");
            foreach (var item in cases.Where(c => c.Category == category).Take(20))
                sb.AppendLine($"- {item.Scenario}: {item.Bucket} lv={item.Level.ToString(CultureInfo.InvariantCulture)} t={item.Time.ToString(CultureInfo.InvariantCulture)} gcd={item.SelectedGcd ?? ""} ogcd={item.Ogcd} red={item.RedGauge.ToString(CultureInfo.InvariantCulture)} blue={item.BlueGauge.ToString(CultureInfo.InvariantCulture)} souls={item.BlueSouls.ToString(CultureInfo.InvariantCulture)} reaver={item.ReaverState} acIn={item.ArcaneCircleReadyIn.ToString(CultureInfo.InvariantCulture)} target={item.TargetAvailable} fallback={item.FallbackTargetAvailable} reason={item.Reason}");
        }

        File.WriteAllText(Path.Combine(outDir, "remaining_gauge_classification.md"), sb.ToString());
    }

    private static IEnumerable<RemainingGaugeCase> ClassifyRemainingGaugeFailures(ScenarioResult result)
    {
        foreach (var frame in result.Frames)
        {
            if (frame.RotationMode == RotationMode.Basic
                && frame.RedGauge >= 100
                && frame.BlueSouls == 0
                && !frame.PerfectioParata
                && !frame.PlentifulHarvestReady
                && !frame.SelectedOgcds.Any(a => a is "BloodStalk" or "GrimSwathe"))
            {
                yield return BuildRemainingGaugeCase(result, frame, "gauge_basic_red_overcap", ClassifyBasicRedOvercap(result.Scenario, frame));
            }
        }

        if (result.Scenario.ExpectSecondEnshroudBlueGauge && result.Scenario.Level >= 80)
        {
            var preSecondEnshroud = result.Frames.Where(f => f.Time >= 118 && f.Time <= 135).ToList();
            if (preSecondEnshroud.Count > 0 && preSecondEnshroud.All(f => f.BlueGauge < 50 && !f.SelectedOgcds.Contains("Enshroud") && !f.IdealHost))
            {
                var frame = preSecondEnshroud.OrderByDescending(f => f.BlueGauge).First();
                yield return BuildRemainingGaugeCase(result, frame, "gauge_even_second_enshroud_blue_shortage", ClassifyEvenSecondEnshroudShortage(result.Scenario, preSecondEnshroud));
            }
        }
    }

    private static string ClassifyBasicRedOvercap(ScenarioDefinition scenario, ActionFrame frame)
    {
        if (!frame.TargetAvailable || !frame.FallbackTargetAvailable)
            return "target_unavailable";
        if (scenario.Events.Any(e => e.Type == ScenarioEventType.ModeSwitch && Math.Abs(e.Start - frame.Time) <= scenario.Gcd * 1.2))
            return "mode_switch_boundary";
        if (frame.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath")
            return "dd_refresh_priority";
        if (frame.SelectedGcd is "SoulSlice" or "SoulScythe")
            return "soul_slice_same_gcd";
        if (frame.ReaverState != ReaverState.None
            || frame.SelectedGcd is "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine")
            return "reaver_executioner_priority";
        if (frame.Level < 50)
            return "level_unavailable";
        return "true_rpr_logic_candidate";
    }

    private static string ClassifyEvenSecondEnshroudShortage(ScenarioDefinition scenario, IReadOnlyList<ActionFrame> frames)
    {
        if (scenario.Events.Any(e => (e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable or ScenarioEventType.PrimaryTargetNull or ScenarioEventType.EmptyPriorityTargets) && e.Start < 135 && e.End > 100))
            return "target_or_downtime_window";
        if (scenario.Events.Any(e => e.Type == ScenarioEventType.ModeSwitch && e.Mode == RotationMode.Basic && e.Start < 135 && e.End > 100))
            return "mode_switch_window";
        if (scenario.KillTime <= 150)
            return "kill_edge";
        if (frames.Any(f => f.BlueSouls > 0 || f.ReaverState != ReaverState.None))
            return "burst_occupancy";
        if (frames.All(f => f.RedGauge < 50))
            return "red_gauge_starvation";
        return "true_rpr_logic_candidate";
    }

    private static string RemainingGaugeActionability(string category)
    {
        if (category == "true_rpr_logic_candidate")
            return "true_rpr_logic_candidate";
        if (category is "target_unavailable" or "target_or_downtime_window" or "kill_edge" or "level_unavailable")
            return "harness_or_edge_case";
        if (category is "mode_switch_boundary" or "mode_switch_window")
            return "mode_transition_edge";
        if (category is "reaver_executioner_priority" or "burst_occupancy" or "dd_refresh_priority" or "soul_slice_same_gcd")
            return "blocked_by_higher_priority_rotation";
        if (category == "red_gauge_starvation")
            return "blocked_by_resource_state";
        return "not_actionable";
    }

    private static RemainingGaugeCase BuildRemainingGaugeCase(ScenarioResult result, ActionFrame frame, string bucket, string category)
        => new(
            result.Scenario.Name,
            result.Scenario.Level,
            bucket,
            category,
            category == "true_rpr_logic_candidate",
            frame.Time,
            frame.SelectedGcd,
            string.Join("|", frame.SelectedOgcds),
            frame.RedGauge,
            frame.BlueGauge,
            frame.BlueSouls,
            frame.ReaverState,
            frame.ArcaneCircleReadyIn,
            frame.TargetAvailable,
            frame.FallbackTargetAvailable,
            frame.Reason);

    private sealed record RemainingGaugeCase(
        string Scenario,
        int Level,
        string Bucket,
        string Category,
        bool RequiresRprReview,
        double Time,
        string? SelectedGcd,
        string Ogcd,
        int RedGauge,
        int BlueGauge,
        int BlueSouls,
        ReaverState ReaverState,
        double ArcaneCircleReadyIn,
        bool TargetAvailable,
        bool FallbackTargetAvailable,
        string Reason);

    private static void WriteLowLevelGaugeClassification(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .Where(s => s.HardFails.Contains(HardFailRule.GaugeFailure) && s.Scenario.Level < 80)
            .Select(ClassifyLowLevelGaugeFailure)
            .ToList();
        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var levelCounts = cases
            .GroupBy(c => c.Level)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var actionabilityCounts = cases
            .GroupBy(c => c.Actionability)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        WriteJson(Path.Combine(outDir, "low_level_gauge_buckets.json"), new
        {
            BucketScenarioCount = cases.Count,
            LevelCounts = levelCounts,
            CategoryCounts = categoryCounts,
            ActionabilityCounts = actionabilityCounts,
            TrueRprLogicCandidateCount = cases.Count(c => c.Actionability == "true_rpr_logic_candidate"),
            Cases = cases
        });

        var sb = new StringBuilder();
        sb.AppendLine("# gauge_low_level_or_no_enshroud classification");
        sb.AppendLine();
        sb.AppendLine($"- bucket scenarios: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- true_rpr_logic_candidate: {cases.Count(c => c.Actionability == "true_rpr_logic_candidate").ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Level counts");
        foreach (var kvp in levelCounts)
            sb.AppendLine($"- Lv{kvp.Key.ToString(CultureInfo.InvariantCulture)}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        foreach (var kvp in categoryCounts)
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        foreach (var kvp in actionabilityCounts)
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- Lv60 has Soul Slice but no Gibbet/Gallows spender, so Soul Slice charge pressure can be real in the harness while not mapping to a safe RPR.cs improvement.");
        sb.AppendLine("- true_rpr_logic_candidate is limited to Lv70+ frames where Soul Slice charge pressure is not explained by red-gauge overflow, Reaver/DD priority, seed state, target loss, or kill edge.");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 20)");
            foreach (var item in cases.Where(c => c.Category == category).Take(20))
            {
                sb.AppendLine($"- {item.Scenario}: lv={item.Level.ToString(CultureInfo.InvariantCulture)} t={item.Time.ToString(CultureInfo.InvariantCulture)} gcd={item.SelectedGcd ?? ""} ogcd={item.Ogcd} red={item.RedGauge.ToString(CultureInfo.InvariantCulture)} blue={item.BlueGauge.ToString(CultureInfo.InvariantCulture)} charges={item.SoulSliceCharges.ToString(CultureInfo.InvariantCulture)} reaver={item.ReaverState} dd={item.DeathsDesignLeft.ToString(CultureInfo.InvariantCulture)} actionability={item.Actionability} reason={item.Reason}");
            }
        }

        File.WriteAllText(Path.Combine(outDir, "low_level_gauge_classification.md"), sb.ToString());
    }

    private static LowLevelGaugeCase ClassifyLowLevelGaugeFailure(ScenarioResult result)
    {
        var scenario = result.Scenario;
        var frame = result.Frames.FirstOrDefault(LowLevelSoulSliceChargeFrame)
            ?? result.Frames.FirstOrDefault(f => (f.SelectedGcd is "SoulSlice" or "SoulScythe") && f.RedGauge > 50 && !f.SelectedOgcds.Any(a => a is "BloodStalk" or "GrimSwathe"))
            ?? result.Frames.FirstOrDefault(f => f.RedGauge >= 100)
            ?? result.Frames.FirstOrDefault(f => f.SelectedGcd != null || f.SelectedOgcds.Count > 0)
            ?? result.Frames[0];
        var category = LowLevelGaugeCategory(scenario, frame);
        var actionability = LowLevelGaugeActionability(category);

        return new(
            scenario.Name,
            scenario.Level,
            scenario.KillTime,
            scenario.Gcd,
            scenario.Opener.ToString(),
            scenario.Potion.ToString(),
            scenario.InitialMode.ToString(),
            category,
            actionability,
            frame.Time,
            frame.SelectedGcd,
            string.Join("|", frame.SelectedOgcds),
            frame.RedGauge,
            frame.BlueGauge,
            frame.SoulSliceCharges,
            frame.DeathsDesignLeft,
            frame.ReaverState,
            frame.TargetAvailable,
            frame.FallbackTargetAvailable,
            frame.Reason);
    }

    private static bool LowLevelSoulSliceChargeFrame(ActionFrame frame)
        => frame.TargetAvailable
        && frame.Level >= 60
        && frame.SoulSliceCharges >= 2
        && frame.SelectedGcd is not ("SoulSlice" or "SoulScythe")
        && frame.BlueSouls == 0
        && frame.ReaverState == ReaverState.None;

    private static string LowLevelGaugeCategory(ScenarioDefinition scenario, ActionFrame frame)
    {
        if (TargetLossOrKillEdge(scenario, frame))
            return "target_loss_or_kill_edge";
        if (UnrealisticInitialState(scenario, frame))
            return "seeded_initial_charge_state";
        if (scenario.Level < 70)
            return "lv60_no_reaver_spender_charge_pressure";
        if (frame.ReaverState != ReaverState.None || frame.SelectedGcd is "Gibbet" or "Gallows" or "Guillotine")
            return "reaver_priority";
        if (frame.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath" || frame.DeathsDesignRefreshReason != "")
            return "deaths_design_priority";
        if ((frame.SelectedGcd is "SoulSlice" or "SoulScythe") && frame.RedGauge > 50)
            return "soul_slice_would_red_overcap";
        if (frame.RedGauge > 50)
            return "red_gauge_blocks_soul_slice";
        if (frame.SoulSliceCharges >= 2 && frame.RedGauge <= 50)
            return "true_rpr_logic_candidate";
        return "low_level_harness_pressure";
    }

    private static string LowLevelGaugeActionability(string category)
        => category == "true_rpr_logic_candidate"
            ? "true_rpr_logic_candidate"
            : category is "target_loss_or_kill_edge" or "seeded_initial_charge_state"
                ? "harness_or_edge_case"
                : category is "lv60_no_reaver_spender_charge_pressure" or "low_level_harness_pressure"
                    ? "level_sync_model_pressure"
                    : "blocked_by_rotation_state";

    private sealed record LowLevelGaugeCase(
        string Scenario,
        int Level,
        double KillTime,
        double Gcd,
        string Opener,
        string Potion,
        string Mode,
        string Category,
        string Actionability,
        double Time,
        string? SelectedGcd,
        string Ogcd,
        int RedGauge,
        int BlueGauge,
        double SoulSliceCharges,
        double DeathsDesignLeft,
        ReaverState ReaverState,
        bool TargetAvailable,
        bool FallbackTargetAvailable,
        string Reason);

    private static void WriteIllegalActionClassification(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .Where(s => s.HardFails.Contains(HardFailRule.IllegalAction))
            .Select(ClassifyIllegalAction)
            .ToList();
        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var actionabilityCounts = cases
            .GroupBy(c => IllegalActionActionability(c.Category))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        WriteJson(Path.Combine(outDir, "illegal_action_buckets.json"), new
        {
            BucketScenarioCount = cases.Count,
            CategoryCounts = categoryCounts,
            ActionabilityCounts = actionabilityCounts,
            TrueRprLogicCandidateCount = cases.Count(c => c.RequiresRprReview),
            Cases = cases
        });

        var sb = new StringBuilder();
        sb.AppendLine("# illegal_action_or_level_sync classification");
        sb.AppendLine();
        sb.AppendLine($"- bucket scenarios: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- true_rpr_logic_candidate: {cases.Count(c => c.RequiresRprReview).ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        if (categoryCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in categoryCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        if (actionabilityCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in actionabilityCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category}");
            foreach (var item in cases.Where(c => c.Category == category).Take(20))
                sb.AppendLine($"- {item.Scenario}: lv={item.Level.ToString(CultureInfo.InvariantCulture)} t={item.Time.ToString(CultureInfo.InvariantCulture)} gcd={item.SelectedGcd ?? ""} ogcd={item.Ogcd} blueSouls={item.BlueSouls.ToString(CultureInfo.InvariantCulture)} target={item.TargetAvailable} fallback={item.FallbackTargetAvailable} reason={item.Reason} details={string.Join("|", item.Details)}");
        }

        File.WriteAllText(Path.Combine(outDir, "illegal_action_classification.md"), sb.ToString());
    }

    private static string IllegalActionActionability(string category)
        => category is "level_sync_locked_action" or "resource_state_mismatch" or "other_illegal_action"
            ? "true_rpr_logic_candidate"
            : category == "target_fallback_unavailable"
                ? "harness_or_target_edge"
                : "not_actionable";

    private static IllegalActionCase ClassifyIllegalAction(ScenarioResult result)
    {
        foreach (var frame in result.Frames)
        {
            var details = IllegalActionDetails(frame).ToList();
            if (details.Count == 0)
                continue;

            var category = details.Any(d => d.Contains("target", StringComparison.OrdinalIgnoreCase))
                ? "target_fallback_unavailable"
                : details.Any(d => d.Contains("locked", StringComparison.OrdinalIgnoreCase))
                    ? "level_sync_locked_action"
                    : details.Any(d => d.Contains("blue", StringComparison.OrdinalIgnoreCase))
                        ? "resource_state_mismatch"
                        : "other_illegal_action";
            return new(
                result.Scenario.Name,
                result.Scenario.Level,
                category,
                category is "level_sync_locked_action" or "resource_state_mismatch",
                frame.Time,
                frame.SelectedGcd,
                string.Join("|", frame.SelectedOgcds),
                frame.BlueSouls,
                frame.TargetAvailable,
                frame.FallbackTargetAvailable,
                frame.Reason,
                details);
        }

        return new(
            result.Scenario.Name,
            result.Scenario.Level,
            "unreproduced_by_classifier",
            false,
            0,
            null,
            "",
            0,
            false,
            false,
            "",
            []);
    }

    private static IEnumerable<string> IllegalActionDetails(ActionFrame frame)
    {
        if (frame.SelectedGcd != null && !RprRotationEmulator.Unlocked(frame.Level, frame.SelectedGcd))
            yield return $"gcd_locked:{frame.SelectedGcd}";
        foreach (var ogcd in frame.SelectedOgcds)
            if (!RprRotationEmulator.Unlocked(frame.Level, ogcd))
                yield return $"ogcd_locked:{ogcd}";

        if (!frame.FallbackTargetAvailable && IsHostileSingleAction(frame.SelectedGcd))
            yield return $"hostile_gcd_without_target:{frame.SelectedGcd}";
        if (!frame.FallbackTargetAvailable)
            foreach (var ogcd in frame.SelectedOgcds.Where(IsHostileSingleAction))
                yield return $"hostile_ogcd_without_target:{ogcd}";

        if (HardFailRules.AvatarActionDuringEnshroud(frame))
            yield return "blue_souls_avatar_during_enshroud";
        if (frame.BlueSouls == 0 && frame.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" or "Communio")
            yield return $"blue_souls_zero:{frame.SelectedGcd}";
        if (frame.Level < 90 && frame.SelectedGcd == "Communio")
            yield return "communio_below_90";
        if (frame.Level < 100 && frame.SelectedGcd == "Perfectio")
            yield return "perfectio_below_100";
        if (frame.Level < 96 && frame.SelectedGcd is "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine")
            yield return $"executioner_action_below_96:{frame.SelectedGcd}";
    }

    private static bool IsHostileSingleAction(string? action)
        => action is "Slice" or "WaxingSlice" or "InfernalSlice" or "ShadowOfDeath" or "SoulSlice" or "Gibbet" or "Gallows" or "ExecutionersGibbet" or "ExecutionersGallows" or "Harpe" or "HarvestMoon" or "Perfectio" or "PlentifulHarvest" or "VoidReaping" or "Communio" or "Gluttony" or "BloodStalk" or "LemuresSlice" or "Sacrificium";

    private sealed record IllegalActionCase(
        string Scenario,
        int Level,
        string Category,
        bool RequiresRprReview,
        double Time,
        string? SelectedGcd,
        string Ogcd,
        int BlueSouls,
        bool TargetAvailable,
        bool FallbackTargetAvailable,
        string Reason,
        IReadOnlyList<string> Details);

    private static void WriteBurstFailureClassification(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .Where(s => s.HardFails.Contains(HardFailRule.BurstFailure))
            .Select(ClassifyBurstFailure)
            .ToList();

        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var issueCounts = cases
            .SelectMany(c => c.Issues)
            .GroupBy(i => i)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var actionabilityCounts = cases
            .GroupBy(c => BurstFailureActionability(c.Category))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var trueCandidates = cases.Where(c => c.RequiresRprReview).ToList();

        WriteJson(Path.Combine(outDir, "burst_failure_buckets.json"), new
        {
            BucketScenarioCount = cases.Count,
            CategoryCounts = categoryCounts,
            IssueCounts = issueCounts,
            ActionabilityCounts = actionabilityCounts,
            TrueRprLogicCandidateCount = trueCandidates.Count,
            Cases = cases
        });

        var sb = new StringBuilder();
        sb.AppendLine("# burst_expectation_missed classification");
        sb.AppendLine();
        sb.AppendLine($"- bucket scenarios: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- true_rpr_logic_candidate: {trueCandidates.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        if (categoryCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in categoryCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Issue counts");
        if (issueCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in issueCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        if (actionabilityCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in actionabilityCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- target_or_mode_or_kill_edge: burst expectation failed while target loss, melee downtime, basic-mode coverage, or kill edge affects the checked window.");
        sb.AppendLine("- hardfail_window_edge: the strict HardFail window misses a second Communio/Enshroud that lands within the next 5 seconds; this is not an RPR.cs change candidate by itself.");
        sb.AppendLine("- dd_refresh_preserves_uptime_pushes_communio: an in-burst Death's Design refresh preserves DD uptime but pushes the second Communio outside the strict check window.");
        sb.AppendLine("- missing_core_burst_package: Double Enshroud / Communio / Perfectio expectations were not met in an otherwise clean even-burst window.");
        sb.AppendLine("- missing_followup_ogcd: Lemure or Sacrificium expectation was not met while core burst pieces were present or separately counted.");
        sb.AppendLine("- true_rpr_logic_candidate marks clean full-mode cases without target/downtime/basic/kill-edge explanation.");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 20)");
            foreach (var item in cases.Where(c => c.Category == category).Take(20))
            {
                sb.AppendLine($"- {item.Scenario}: lv={item.Level.ToString(CultureInfo.InvariantCulture)} gcd={item.Gcd.ToString(CultureInfo.InvariantCulture)} kt={item.KillTime.ToString(CultureInfo.InvariantCulture)} anchor={item.BurstAnchor.ToString(CultureInfo.InvariantCulture)} issues={string.Join("|", item.Issues)} counts=AC:{item.ArcaneCircleCount.ToString(CultureInfo.InvariantCulture)} En:{item.EnshroudCount.ToString(CultureInfo.InvariantCulture)} Co:{item.CommunioCount.ToString(CultureInfo.InvariantCulture)} Pe:{item.PerfectioCount.ToString(CultureInfo.InvariantCulture)} Le:{item.LemureCount.ToString(CultureInfo.InvariantCulture)} Sa:{item.SacrificiumCount.ToString(CultureInfo.InvariantCulture)} times=AC[{string.Join(",", item.ArcaneCircleTimes.Select(FormatDouble))}] PH[{string.Join(",", item.PlentifulHarvestTimes.Select(FormatDouble))}] En[{string.Join(",", item.EnshroudTimes.Select(FormatDouble))}] Co[{string.Join(",", item.CommunioTimes.Select(FormatDouble))}] Pe[{string.Join(",", item.PerfectioTimes.Select(FormatDouble))}] DD[{string.Join(",", item.DeathsDesignRefreshTimes.Select(FormatDouble))}] events={item.EventSummary}");
            }
        }

        File.WriteAllText(Path.Combine(outDir, "burst_failure_classification.md"), sb.ToString());
    }

    private static string BurstFailureActionability(string category)
    {
        if (category is "missing_core_burst_package" or "missing_followup_ogcd" or "burst_order_or_stall")
            return "true_rpr_logic_candidate";
        if (category == "hardfail_window_edge")
            return "hardfail_window_edge";
        if (category == "dd_refresh_preserves_uptime_pushes_communio")
            return "dd_uptime_tradeoff";
        if (category == "target_or_mode_or_kill_edge")
            return "harness_or_edge_case";
        return "not_actionable";
    }

    private static BurstFailureCase ClassifyBurstFailure(ScenarioResult result)
    {
        var scenario = result.Scenario;
        var issues = new List<string>();
        var evenAc = result.Frames.FirstOrDefault(f => f.SelectedOgcds.Contains("ArcaneCircle") && f.Time >= 110);
        var burstAnchor = evenAc?.Time ?? 120;
        var evenFrames = result.Frames.Where(f => f.Time >= burstAnchor - 45 && f.Time <= burstAnchor + 40).ToList();
        var relaxedEvenFrames = result.Frames.Where(f => f.Time >= burstAnchor - 45 && f.Time <= burstAnchor + 45).ToList();

        var targetOrDowntimeEvents = scenario.Events
            .Where(e => e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable or ScenarioEventType.PrimaryTargetNull or ScenarioEventType.EmptyPriorityTargets
                && e.Start < burstAnchor + 40
                && e.End > burstAnchor - 45)
            .ToList();
        var modeEvents = scenario.Events
            .Where(e => e.Type == ScenarioEventType.ModeSwitch && e.Mode == RotationMode.Basic && e.Start < burstAnchor + 40 && e.End > burstAnchor - 45)
            .ToList();

        if (scenario.InitialMode == RotationMode.Full && scenario.Level >= 72 && scenario.KillTime >= 130 && !BurstBasicModeCoversEven(scenario))
        {
            if (evenAc == null && !scenario.Events.Any(e => e.Type == ScenarioEventType.TargetLost && e.Start <= 120 && e.End >= 120))
                issues.Add("missing_even_arcane_circle");
        }

        if (result.Frames.Any(f => f.SelectedOgcds.Contains("Potion") && !f.SelectedOgcds.Contains("ArcaneCircle") && f.ArcaneCircleLeft <= 0 && f.ArcaneCircleReadyIn > f.Gcd * 3))
            issues.Add("potion_without_arcane_circle_plan");
        if (result.Frames.Any(f => f.SelectedOgcds.Contains("Gluttony") && f.SelectedOgcds.Contains("ArcaneCircle") && f.ArcaneCircleLeft <= 0))
            issues.Add("gluttony_same_gcd_before_arcane_buff");
        if (result.Frames.Any(f => f.PerfectioParata && f.Level >= 100 && f.SelectedGcd == null && f.TargetAvailable))
            issues.Add("perfectio_parata_stalled");

        var enshroudCount = evenFrames.Count(f => f.SelectedOgcds.Contains("Enshroud"));
        var communioCount = evenFrames.Count(f => f.SelectedGcd == "Communio");
        var perfectioCount = evenFrames.Count(f => f.SelectedGcd == "Perfectio");
        var lemureCount = evenFrames.Count(f => f.SelectedOgcds.Any(a => a is "LemuresSlice" or "LemuresScythe"));
        var sacrificiumCount = evenFrames.Count(f => f.SelectedOgcds.Contains("Sacrificium"));
        var relaxedCommunioCount = relaxedEvenFrames.Count(f => f.SelectedGcd == "Communio");
        var relaxedEnshroudCount = relaxedEvenFrames.Count(f => f.SelectedOgcds.Contains("Enshroud"));

        if (!BurstEvenDisrupted(scenario) && scenario.KillTime >= 150)
        {
            if (scenario.ExpectDoubleEnshroud && scenario.Level >= 80 && enshroudCount < 2)
                issues.Add("missing_double_enshroud");
            if (scenario.ExpectTwoCommunio && scenario.Level >= 90 && communioCount < 2)
                issues.Add("missing_two_communio");
            if (scenario.ExpectPerfectio && scenario.Level >= 100 && perfectioCount == 0)
                issues.Add("missing_perfectio");
            if (scenario.ExpectLemure && scenario.Level >= 86 && lemureCount == 0)
                issues.Add("missing_lemure");
            if (scenario.ExpectSacrificium && scenario.Level >= 92 && sacrificiumCount == 0)
                issues.Add("missing_sacrificium");
        }

        if (issues.Count == 0)
            issues.Add("unclassified_burst_failure");

        var targetOrModeOrKillEdge =
            targetOrDowntimeEvents.Count > 0
            || modeEvents.Count > 0
            || scenario.InitialMode == RotationMode.Basic
            || scenario.KillTime - burstAnchor <= scenario.Gcd * 16;
        var hardfailWindowEdge =
            issues.Contains("missing_two_communio") && relaxedCommunioCount >= 2
            || issues.Contains("missing_double_enshroud") && relaxedEnshroudCount >= 2;
        var ddRefreshPushedCommunio = issues.Contains("missing_two_communio")
            && result.Frames.Any(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath"
                && f.Time > burstAnchor + 25
                && f.Time <= burstAnchor + 40);
        var hasCoreBurstIssue = issues.Any(i => i is "missing_even_arcane_circle" or "missing_double_enshroud" or "missing_two_communio" or "missing_perfectio");
        var hasFollowupIssue = issues.Any(i => i is "missing_lemure" or "missing_sacrificium");
        var category = targetOrModeOrKillEdge
            ? "target_or_mode_or_kill_edge"
            : hardfailWindowEdge
                ? "hardfail_window_edge"
                : ddRefreshPushedCommunio
                    ? "dd_refresh_preserves_uptime_pushes_communio"
                    : hasCoreBurstIssue
                        ? "missing_core_burst_package"
                        : hasFollowupIssue
                            ? "missing_followup_ogcd"
                            : "burst_order_or_stall";
        var requiresRprReview = !targetOrModeOrKillEdge && !hardfailWindowEdge && !ddRefreshPushedCommunio && scenario.InitialMode == RotationMode.Full && scenario.Level >= 80;
        var eventSummary = string.Join("|", targetOrDowntimeEvents.Concat(modeEvents).Select(e => $"{e.Type}@{e.Start.ToString(CultureInfo.InvariantCulture)}-{e.End.ToString(CultureInfo.InvariantCulture)}"));

        return new(
            scenario.Name,
            scenario.Level,
            scenario.KillTime,
            scenario.Gcd,
            scenario.Opener.ToString(),
            scenario.Potion.ToString(),
            scenario.InitialMode.ToString(),
            category,
            requiresRprReview,
            burstAnchor,
            result.Frames.Count(f => f.SelectedOgcds.Contains("ArcaneCircle")),
            enshroudCount,
            communioCount,
            perfectioCount,
            lemureCount,
            sacrificiumCount,
            targetOrDowntimeEvents.Count,
            modeEvents.Count,
            eventSummary,
            result.Frames.Where(f => f.SelectedOgcds.Contains("ArcaneCircle")).Select(f => f.Time).ToList(),
            result.Frames.Where(f => f.SelectedGcd == "PlentifulHarvest").Select(f => f.Time).ToList(),
            result.Frames.Where(f => f.SelectedOgcds.Contains("Enshroud")).Select(f => f.Time).ToList(),
            result.Frames.Where(f => f.SelectedGcd == "Communio").Select(f => f.Time).ToList(),
            result.Frames.Where(f => f.SelectedGcd == "Perfectio").Select(f => f.Time).ToList(),
            result.Frames.Where(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath").Select(f => f.Time).ToList(),
            issues);
    }

    private static string FormatDouble(double value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool BurstBasicModeCoversEven(ScenarioDefinition scenario)
        => scenario.InitialMode == RotationMode.Basic
        || scenario.Events.Any(e => e.Type == ScenarioEventType.ModeSwitch && e.Mode == RotationMode.Basic && e.Start <= 120 && e.End >= 120);

    private static bool BurstEvenDisrupted(ScenarioDefinition scenario)
        => scenario.Events.Any(e =>
            (e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable)
            && e.Start < 160
            && e.End > 100);

    private sealed record BurstFailureCase(
        string Scenario,
        int Level,
        double KillTime,
        double Gcd,
        string Opener,
        string Potion,
        string Mode,
        string Category,
        bool RequiresRprReview,
        double BurstAnchor,
        int ArcaneCircleCount,
        int EnshroudCount,
        int CommunioCount,
        int PerfectioCount,
        int LemureCount,
        int SacrificiumCount,
        int TargetOrDowntimeEventCount,
        int ModeEventCount,
        string EventSummary,
        IReadOnlyList<double> ArcaneCircleTimes,
        IReadOnlyList<double> PlentifulHarvestTimes,
        IReadOnlyList<double> EnshroudTimes,
        IReadOnlyList<double> CommunioTimes,
        IReadOnlyList<double> PerfectioTimes,
        IReadOnlyList<double> DeathsDesignRefreshTimes,
        IReadOnlyList<string> Issues);

    private static void WriteGluttonyDriftClassification(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .Where(s => s.HardFails.Contains(HardFailRule.DriftFailure) && s.Frames.All(f => f.RotationMode == RotationMode.Full))
            .Select(ClassifyGluttonyDrift)
            .ToList();

        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var categoryLevelBands = cases
            .GroupBy(c => c.Category)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(c => c.LevelBand).OrderBy(b => b.Key).ToDictionary(b => b.Key, b => b.Count()));
        var actionabilityCounts = cases
            .GroupBy(c => GluttonyDriftActionability(c.Category))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        WriteJson(Path.Combine(outDir, "drift_gluttony_buckets.json"), new
        {
            BucketScenarioCount = cases.Count,
            CategoryCounts = categoryCounts,
            CategoryLevelBands = categoryLevelBands,
            ActionabilityCounts = actionabilityCounts,
            TrueRprLogicCandidateCount = cases.Count(c => GluttonyDriftRequiresRprReview(c.Category)),
            Cases = cases
        });

        var sb = new StringBuilder();
        sb.AppendLine("# drift_full_mode_gluttony_interval classification");
        sb.AppendLine();
        sb.AppendLine($"- bucket scenarios: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        foreach (var kvp in categoryCounts)
        {
            var bands = string.Join(", ", categoryLevelBands[kvp.Key].Select(b => $"{b.Key}={b.Value.ToString(CultureInfo.InvariantCulture)}"));
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)} ({bands})");
        }

        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        foreach (var kvp in actionabilityCounts)
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- true_rpr_logic_candidate: {cases.Count(c => GluttonyDriftRequiresRprReview(c.Category)).ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- seeded_initial_state: first failing pair starts from opener-window Gluttony and is distorted by real harness initial gauges/reaver/charges.");
        sb.AppendLine("- waiting_for_arcane_hold_*: interval contains frames where Gluttony is intentionally held for Arcane Circle alignment.");
        sb.AppendLine("- red_gauge_starvation: after the drift threshold, otherwise-open frames did not have RedGauge >= 50 until the late Gluttony.");
        sb.AppendLine("- enshroud_or_reaver_occupancy: after the drift threshold, Enshroud/Reaver occupied the period where Gluttony would otherwise fit.");
        sb.AppendLine("- gcd_grid_threshold_margin: drift exceeded the rule by less than or equal to one scenario GCD, matching next-GCD quantization rather than a distinct hold.");
        sb.AppendLine("- true_rpr_logic_candidate: no target/kill/seed/waiting/low-red/occupancy explanation was found by frame facts.");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 15)");
            foreach (var item in cases.Where(c => c.Category == category).Take(15))
            {
                sb.AppendLine($"- {item.Scenario}: {item.LevelBand} pair={item.PreviousGluttonyTime.ToString(CultureInfo.InvariantCulture)}->{item.CurrentGluttonyTime.ToString(CultureInfo.InvariantCulture)} interval={item.Interval.ToString(CultureInfo.InvariantCulture)} drift={item.Drift.ToString(CultureInfo.InvariantCulture)} even={item.EvenWindow.ToString(CultureInfo.InvariantCulture)} red={item.MinRedInGap.ToString(CultureInfo.InvariantCulture)}-{item.MaxRedInGap.ToString(CultureInfo.InvariantCulture)} blueSoulsFrames={item.BlueSoulsFramesInGap.ToString(CultureInfo.InvariantCulture)} reaverFrames={item.ReaverFramesInGap.ToString(CultureInfo.InvariantCulture)} waitingAC={item.WaitingForArcaneFramesInGap.ToString(CultureInfo.InvariantCulture)} events={item.EventsInGap}");
            }
        }

        File.WriteAllText(Path.Combine(outDir, "drift_gluttony_classification.md"), sb.ToString());
    }

    private static void WriteModeSwitchGluttonyDriftClassification(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .Where(s => s.HardFails.Contains(HardFailRule.DriftFailure) && s.Frames.Any(f => f.RotationMode == RotationMode.Basic))
            .Select(ClassifyModeSwitchGluttonyDrift)
            .ToList();

        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var actionabilityCounts = cases
            .GroupBy(c => c.Actionability)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        WriteJson(Path.Combine(outDir, "drift_mode_switch_buckets.json"), new
        {
            BucketScenarioCount = cases.Count,
            CategoryCounts = categoryCounts,
            ActionabilityCounts = actionabilityCounts,
            TrueRprLogicCandidateCount = cases.Count(c => c.Actionability == "true_rpr_logic_candidate"),
            Cases = cases
        });

        var sb = new StringBuilder();
        sb.AppendLine("# drift_mode_switch_window classification");
        sb.AppendLine();
        sb.AppendLine($"- bucket scenarios: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- true_rpr_logic_candidate: {cases.Count(c => c.Actionability == "true_rpr_logic_candidate").ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        foreach (var kvp in categoryCounts)
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        foreach (var kvp in actionabilityCounts)
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- This bucket contains scenarios with at least one BasicMode frame, but the failing Gluttony pair is still evaluated only across FullMode frames because the HardFail drift rule resets on BasicMode frames.");
        sb.AppendLine("- full_segment_* categories reuse the same frame-fact checks as drift_full_mode_gluttony_interval and are not direct BasicMode timer defects.");
        sb.AppendLine("- basic_window_overlaps_pair would indicate a true mode-switch boundary issue; none should appear unless the drift reset model changes.");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 15)");
            foreach (var item in cases.Where(c => c.Category == category).Take(15))
            {
                sb.AppendLine($"- {item.Scenario}: pair={item.PreviousGluttonyTime.ToString(CultureInfo.InvariantCulture)}->{item.CurrentGluttonyTime.ToString(CultureInfo.InvariantCulture)} interval={item.Interval.ToString(CultureInfo.InvariantCulture)} drift={item.Drift.ToString(CultureInfo.InvariantCulture)} even={item.EvenWindow.ToString(CultureInfo.InvariantCulture)} modeRelation={item.ModeEventRelation} base={item.BaseCategory} actionability={item.Actionability} red={item.MinRedInGap.ToString(CultureInfo.InvariantCulture)}-{item.MaxRedInGap.ToString(CultureInfo.InvariantCulture)} blueSoulsFrames={item.BlueSoulsFramesInGap.ToString(CultureInfo.InvariantCulture)} reaverFrames={item.ReaverFramesInGap.ToString(CultureInfo.InvariantCulture)} waitingAC={item.WaitingForArcaneLateFramesInGap.ToString(CultureInfo.InvariantCulture)} events={item.EventsInGap}");
            }
        }

        File.WriteAllText(Path.Combine(outDir, "drift_mode_switch_classification.md"), sb.ToString());
    }

    private static ModeSwitchGluttonyDriftCase ClassifyModeSwitchGluttonyDrift(ScenarioResult result)
    {
        var baseCase = ClassifyGluttonyDrift(result);
        var modeEvents = result.Scenario.Events
            .Where(e => e.Type == ScenarioEventType.ModeSwitch && e.Mode == RotationMode.Basic)
            .ToList();
        var overlappingMode = modeEvents
            .Where(e => e.Start < baseCase.CurrentGluttonyTime && e.End > baseCase.PreviousGluttonyTime)
            .ToList();
        var nearbyMode = modeEvents
            .Where(e => e.End <= baseCase.CurrentGluttonyTime && baseCase.CurrentGluttonyTime - e.End <= result.Scenario.Gcd * 2.2
                || e.Start >= baseCase.PreviousGluttonyTime && e.Start - baseCase.PreviousGluttonyTime <= result.Scenario.Gcd * 2.2)
            .ToList();
        var modeRelation = overlappingMode.Count > 0
            ? "overlaps_failing_pair"
            : nearbyMode.Count > 0
                ? "near_failing_pair"
                : "outside_failing_pair";
        var category = overlappingMode.Count > 0
            ? "basic_window_overlaps_pair"
            : $"full_segment_{baseCase.Category}";
        var actionability = overlappingMode.Count > 0
            ? "true_rpr_logic_candidate"
            : GluttonyDriftActionability(baseCase.Category);

        return new(
            baseCase.Scenario,
            baseCase.Level,
            baseCase.LevelBand,
            baseCase.KillTime,
            baseCase.Gcd,
            baseCase.Opener,
            baseCase.Potion,
            baseCase.Mode,
            category,
            baseCase.Category,
            actionability,
            modeRelation,
            string.Join("|", modeEvents.Select(e => $"{e.Start.ToString(CultureInfo.InvariantCulture)}-{e.End.ToString(CultureInfo.InvariantCulture)}")),
            baseCase.PreviousGluttonyTime,
            baseCase.CurrentGluttonyTime,
            baseCase.Interval,
            baseCase.Drift,
            baseCase.EvenWindow,
            baseCase.ThresholdTime,
            baseCase.MinRedInGap,
            baseCase.MaxRedInGap,
            baseCase.BlueSoulsFramesInGap,
            baseCase.ReaverFramesInGap,
            baseCase.WaitingForArcaneLateFramesInGap,
            baseCase.EventsInGap);
    }

    private static bool GluttonyDriftRequiresRprReview(string category)
        => category == "true_rpr_logic_candidate";

    private static string GluttonyDriftActionability(string category)
    {
        if (GluttonyDriftRequiresRprReview(category))
            return "true_rpr_logic_candidate";
        if (category is "target_loss_or_downtime_in_gap" or "kill_edge" or "seeded_initial_state")
            return "harness_or_edge_case";
        if (category is "red_gauge_starvation" or "enshroud_or_reaver_occupancy" or "waiting_for_arcane_hold" or "waiting_for_arcane_hold_even_window_mismatch")
            return "blocked_by_rotation_state";
        if (category == "gcd_grid_threshold_margin")
            return "gcd_quantization_margin";
        return "not_actionable";
    }

    private static GluttonyDriftCase ClassifyGluttonyDrift(ScenarioResult result)
    {
        var pair = FirstFailingGluttonyPair(result)
            ?? throw new InvalidDataException($"No failing Gluttony pair found for {result.Scenario.Name}");
        var scenario = result.Scenario;
        var gapFrames = result.Frames
            .Where(f => f.Time > pair.Previous.Time && f.Time < pair.Current.Time && f.RotationMode == RotationMode.Full)
            .ToList();
        var thresholdTime = pair.Previous.Time + (pair.EvenWindow ? 80.5 : 63.5);
        var lateFrames = gapFrames.Where(f => f.Time >= thresholdTime).ToList();
        var targetOrDowntime = scenario.Events
            .Where(e => e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable or ScenarioEventType.PrimaryTargetNull or ScenarioEventType.EmptyPriorityTargets
                && e.Start < pair.Current.Time
                && e.End > pair.Previous.Time)
            .ToList();
        var seeded = pair.Previous.Time < 30;
        var killEdge = scenario.KillTime - pair.Current.Time <= scenario.Gcd * 2.2;
        var waitingFrames = gapFrames.Where(IsWaitingForArcaneGluttonyHold).ToList();
        var waitingFramesLate = lateFrames.Where(IsWaitingForArcaneGluttonyHold).ToList();
        var usableFramesLate = lateFrames
            .Where(f => f.TargetAvailable && f.FallbackTargetAvailable && f.BlueSouls == 0 && f.ReaverState == ReaverState.None && f.SelectedGcd != null)
            .ToList();
        var lowRedLate = usableFramesLate.Count > 0 && usableFramesLate.All(f => f.RedGauge < 50 || IsWaitingForArcaneGluttonyHold(f));
        var occupiedLate = lateFrames.Any(f => f.BlueSouls > 0 || f.ReaverState != ReaverState.None);
        var eventSummary = string.Join("|", targetOrDowntime.Select(e => $"{e.Type}@{e.Start.ToString(CultureInfo.InvariantCulture)}-{e.End.ToString(CultureInfo.InvariantCulture)}"));
        var category = targetOrDowntime.Count > 0
            ? "target_loss_or_downtime_in_gap"
            : killEdge
                ? "kill_edge"
                : seeded
                    ? "seeded_initial_state"
                    : waitingFramesLate.Count > 0
                        ? pair.EvenWindow ? "waiting_for_arcane_hold" : "waiting_for_arcane_hold_even_window_mismatch"
                        : lowRedLate
                            ? "red_gauge_starvation"
                            : occupiedLate
                                ? "enshroud_or_reaver_occupancy"
                                : pair.Drift - (pair.EvenWindow ? 20.5 : 3.5) <= scenario.Gcd
                                    ? "gcd_grid_threshold_margin"
                                    : "true_rpr_logic_candidate";

        return new(
            scenario.Name,
            scenario.Level,
            LevelBandFor(scenario.Level),
            scenario.KillTime,
            scenario.Gcd,
            scenario.Opener.ToString(),
            scenario.Potion.ToString(),
            scenario.InitialMode.ToString(),
            category,
            pair.Previous.Time,
            pair.Current.Time,
            pair.Current.Time - pair.Previous.Time,
            pair.Drift,
            pair.EvenWindow,
            thresholdTime,
            gapFrames.Count == 0 ? 0 : gapFrames.Min(f => f.RedGauge),
            gapFrames.Count == 0 ? 0 : gapFrames.Max(f => f.RedGauge),
            gapFrames.Count(f => f.BlueSouls > 0),
            gapFrames.Count(f => f.ReaverState != ReaverState.None),
            waitingFrames.Count,
            waitingFramesLate.Count,
            lateFrames.Count(f => f.RedGauge < 50),
            targetOrDowntime.Count,
            eventSummary,
            new GluttonyDriftFrame(
                pair.Previous.Time,
                pair.Previous.SelectedGcd,
                string.Join("|", pair.Previous.SelectedOgcds),
                pair.Previous.RedGauge,
                pair.Previous.BlueGauge,
                pair.Previous.DeathsDesignLeft,
                pair.Previous.ArcaneCircleReadyIn,
                pair.Previous.ArcaneCircleLeft,
                pair.Previous.BlueSouls,
                pair.Previous.ReaverState,
                pair.Previous.Reason),
            new GluttonyDriftFrame(
                pair.Current.Time,
                pair.Current.SelectedGcd,
                string.Join("|", pair.Current.SelectedOgcds),
                pair.Current.RedGauge,
                pair.Current.BlueGauge,
                pair.Current.DeathsDesignLeft,
                pair.Current.ArcaneCircleReadyIn,
                pair.Current.ArcaneCircleLeft,
                pair.Current.BlueSouls,
                pair.Current.ReaverState,
                pair.Current.Reason));
    }

    private static GluttonyPair? FirstFailingGluttonyPair(ScenarioResult result)
    {
        ActionFrame? previous = null;
        foreach (var frame in result.Frames)
        {
            if (frame.RotationMode != RotationMode.Full)
            {
                previous = null;
                continue;
            }

            if (!frame.SelectedOgcds.Contains("Gluttony"))
                continue;

            if (previous == null)
            {
                previous = frame;
                continue;
            }

            var drift = frame.Time - previous.Time - 60;
            var evenWindow = frame.Time % 120 <= 25 || frame.Time % 120 >= 95;
            if (!evenWindow && drift > 3.5 || evenWindow && drift > 20.5)
                return new(previous, frame, drift, evenWindow);

            previous = frame;
        }

        return null;
    }

    private static bool IsWaitingForArcaneGluttonyHold(ActionFrame frame)
        => frame.TargetAvailable
        && frame.FallbackTargetAvailable
        && frame.RedGauge >= 50
        && frame.ReaverState == ReaverState.None
        && frame.BlueSouls == 0
        && frame.ArcaneCircleLeft <= 0
        && frame.ArcaneCircleReadyIn <= 10;

    private static string LevelBandFor(int level)
        => level < 80 ? "lv50_79" : level < 90 ? "lv80_89" : level < 92 ? "lv90_91" : level < 100 ? "lv92_99" : "lv100";

    private sealed record GluttonyPair(ActionFrame Previous, ActionFrame Current, double Drift, bool EvenWindow);

    private sealed record GluttonyDriftFrame(
        double Time,
        string? SelectedGcd,
        string Ogcd,
        int RedGauge,
        int BlueGauge,
        double DeathsDesignLeft,
        double ArcaneCircleReadyIn,
        double ArcaneCircleLeft,
        int BlueSouls,
        ReaverState ReaverState,
        string Reason);

    private sealed record GluttonyDriftCase(
        string Scenario,
        int Level,
        string LevelBand,
        double KillTime,
        double Gcd,
        string Opener,
        string Potion,
        string Mode,
        string Category,
        double PreviousGluttonyTime,
        double CurrentGluttonyTime,
        double Interval,
        double Drift,
        bool EvenWindow,
        double ThresholdTime,
        int MinRedInGap,
        int MaxRedInGap,
        int BlueSoulsFramesInGap,
        int ReaverFramesInGap,
        int WaitingForArcaneFramesInGap,
        int WaitingForArcaneLateFramesInGap,
        int LowRedLateFrames,
        int EventCountInGap,
        string EventsInGap,
        GluttonyDriftFrame Previous,
        GluttonyDriftFrame Current);

    private sealed record ModeSwitchGluttonyDriftCase(
        string Scenario,
        int Level,
        string LevelBand,
        double KillTime,
        double Gcd,
        string Opener,
        string Potion,
        string Mode,
        string Category,
        string BaseCategory,
        string Actionability,
        string ModeEventRelation,
        string ModeEvents,
        double PreviousGluttonyTime,
        double CurrentGluttonyTime,
        double Interval,
        double Drift,
        bool EvenWindow,
        double ThresholdTime,
        int MinRedInGap,
        int MaxRedInGap,
        int BlueSoulsFramesInGap,
        int ReaverFramesInGap,
        int WaitingForArcaneLateFramesInGap,
        string EventsInGap);

    private static void WriteDdDroppedEnshroudClassification(string outDir, RegressionOutput output)
    {
        var cases = new List<DdEnshroudCase>();
        foreach (var result in output.Scenarios)
        {
            if (!result.HardFails.Contains(HardFailRule.DeathsDesignFailure))
                continue;
            if (!result.Frames.Any(f => f.BlueSouls > 0 && f.DeathsDesignLeft <= 0 && f.TargetAvailable && f.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath")))
                continue;

            cases.Add(ClassifyDdDroppedEnshroud(result));
        }

        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var categoryLevelBands = cases
            .GroupBy(c => c.Category)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(c => c.LevelBand).OrderBy(b => b.Key).ToDictionary(b => b.Key, b => b.Count()));
        var actionabilityCounts = cases
            .GroupBy(c => DdEnshroudActionability(c.Category))
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());

        WriteJson(Path.Combine(outDir, "dd_dropped_enshroud_buckets.json"), new
        {
            BucketScenarioCount = cases.Count,
            CategoryCounts = categoryCounts,
            CategoryLevelBands = categoryLevelBands,
            ActionabilityCounts = actionabilityCounts,
            TrueRprLogicCandidateCount = cases.Count(c => DdEnshroudRequiresRprReview(c.Category)),
            Cases = cases
        });

        var sb = new StringBuilder();
        sb.AppendLine("# dd_dropped_during_enshroud classification");
        sb.AppendLine();
        sb.AppendLine($"- bucket scenarios: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        foreach (var kvp in categoryCounts)
        {
            var bands = string.Join(", ", categoryLevelBands[kvp.Key].Select(b => $"{b.Key}={b.Value.ToString(CultureInfo.InvariantCulture)}"));
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)} ({bands})");
        }
        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        foreach (var kvp in actionabilityCounts)
            sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- true_rpr_logic_candidate: {cases.Count(c => DdEnshroudRequiresRprReview(c.Category)).ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- excused_target_loss_downtime_kill_edge: all loose-bucket frames already excused by the rule's FinalOrDowntimeException; DeathsDesignFailure came from another DD rule.");
        sb.AppendLine("- target_loss_stretched_enshroud / seeded_initial_state_enshroud: emulator harness artifacts (downtime stalls the shroud / opener-window shroud from seeded gauge+DD).");
        sb.AppendLine("- missed_by_*: existing refresh predicates should have fired per their own conditions but did not (emulator/RPR divergence suspects).");
        sb.AppendLine("- burst_planning_blocked_*: pre-enshroud refresh was suppressed by even-burst planning / Perfectio / PH by design; broad candidate fixes for this bucket reduced DD failures but caused major count / uptime regressions, so this bucket is not a true RPR logic candidate without a narrower hypothesis.");
        sb.AppendLine("- threshold_short_*: DD at entry was above the pre-any-enshroud threshold, so no refresh was requested, yet the shroud outlived DD.");
        sb.AppendLine("- *_pre_refresh_coverable = one pre-entry refresh (+30) would have covered the shroud; *_in_shroud_only = it would not.");
        sb.AppendLine("- true RPR logic candidate pool = missed_by_* + threshold_short_* + unclassified. The previously broad burst_planning_blocked_* route is tracked as proven_unsafe_broad_candidate instead.");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 15)");
            foreach (var item in cases.Where(c => c.Category == category).Take(15))
            {
                var v = item.Violation;
                sb.AppendLine($"- {item.Scenario}: entry={item.EntryTime.ToString(CultureInfo.InvariantCulture)} ddAtEntry={item.DdAtEntry.ToString(CultureInfo.InvariantCulture)} needed={item.NeededDuration.ToString(CultureInfo.InvariantCulture)} threshold={item.PreAnyThreshold.ToString(CultureInfo.InvariantCulture)} burstPlan={item.EntryInBurstPlanning.ToString(CultureInfo.InvariantCulture)} entryOgcd={item.EntryOgcds} | viol t={v.Time.ToString(CultureInfo.InvariantCulture)} gcd={v.SelectedGcd ?? ""} souls={v.BlueSouls.ToString(CultureInfo.InvariantCulture)} acLeft={v.ArcaneCircleLeft.ToString(CultureInfo.InvariantCulture)} idealHost={v.IdealHost.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        File.WriteAllText(Path.Combine(outDir, "dd_dropped_enshroud_classification.md"), sb.ToString());
    }

    private static DdEnshroudCase ClassifyDdDroppedEnshroud(ScenarioResult result)
    {
        var scenario = result.Scenario;
        var levelBand = scenario.Level < 90 ? "lv80_89" : scenario.Level < 92 ? "lv90_91" : "lv92_plus";
        var burstPlanGcd = Math.Max(scenario.Gcd, 2.50);
        var preAnyThreshold = scenario.Gcd + burstPlanGcd * 5.0 + 1.0;

        var strict = result.Frames
            .Where(f => f.BlueSouls > 0 && f.DeathsDesignLeft <= 0 && f.TargetAvailable
                && f.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath")
                && !DdFinalOrDowntimeException(scenario, f.Time))
            .ToList();

        DdEnshroudCase Build(string category, ActionFrame? entry, ActionFrame violation, double ddAtEntry, double needed, bool burstPlanning)
            => new(
                scenario.Name,
                scenario.Level,
                scenario.KillTime,
                scenario.Gcd,
                scenario.Opener.ToString(),
                scenario.Potion.ToString(),
                scenario.InitialMode.ToString(),
                category,
                levelBand,
                entry?.Time ?? -1,
                ddAtEntry,
                Math.Min(60, ddAtEntry + 30),
                needed,
                preAnyThreshold,
                burstPlanning,
                entry == null ? "" : string.Join("|", entry.SelectedOgcds),
                entry?.EnshroudReadyReason ?? "",
                new DdEnshroudFrameInfo(
                    violation.Time,
                    violation.SelectedGcd,
                    string.Join("|", violation.SelectedOgcds),
                    violation.BlueSouls,
                    violation.DeathsDesignLeft,
                    violation.ArcaneCircleLeft,
                    violation.IdealHost,
                    violation.Reason));

        if (strict.Count == 0)
        {
            var loose = result.Frames.First(f => f.BlueSouls > 0 && f.DeathsDesignLeft <= 0 && f.TargetAvailable && f.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath"));
            return Build("excused_target_loss_downtime_kill_edge", null, loose, 0, 0, false);
        }

        var violation = strict[0];
        var entryFrame = result.Frames.LastOrDefault(f => f.Time < violation.Time && f.SelectedOgcds.Contains("Enshroud"));
        if (entryFrame == null)
            return Build("unclassified_no_entry", null, violation, 0, 0, false);

        var ddRaw = entryFrame.DeathsDesignLeft;
        var ddEffective = entryFrame.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath" ? Math.Min(60, ddRaw + 30) : ddRaw;
        var needed = violation.Time - entryFrame.Time;
        var burstPlanning = InEvenBurstPlanningWindow(entryFrame) || entryFrame.PerfectioParata || entryFrame.PlentifulHarvestReady;
        var coverable = Math.Min(60, ddEffective + 30) >= needed - 0.001;

        if (scenario.Events.Any(e => e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable or ScenarioEventType.PrimaryTargetNull or ScenarioEventType.EmptyPriorityTargets && e.Start < violation.Time && e.End > entryFrame.Time))
            return Build("target_or_downtime_stretched_enshroud", entryFrame, violation, ddEffective, needed, burstPlanning);
        if (entryFrame.Time < 30 && scenario.BlueGauge >= 50)
            return Build("seeded_initial_state_enshroud", entryFrame, violation, ddEffective, needed, burstPlanning);

        var duringExceptionMissed = scenario.Level is >= 90 and < 92 && result.Frames.Any(f =>
            f.Time > entryFrame.Time
            && f.Time < violation.Time
            && f.Time < 30
            && f.BlueSouls is >= 2 and <= 4
            && f.ArcaneCircleLeft <= 0
            && !f.IdealHost
            && !f.PerfectioParata
            && !InEvenBurstPlanningWindow(f)
            && f.DeathsDesignLeft <= scenario.Gcd + burstPlanGcd * f.BlueSouls + 1.0
            && f.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath"));
        if (duringExceptionMissed)
            return Build("missed_by_during_enshroud_exception", entryFrame, violation, ddEffective, needed, burstPlanning);

        if (ddRaw <= preAnyThreshold && entryFrame.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath"))
        {
            if (!burstPlanning)
                return Build("missed_by_pre_any_enshroud_refresh", entryFrame, violation, ddEffective, needed, burstPlanning);
            return Build(coverable ? "burst_planning_blocked_pre_refresh_coverable" : "burst_planning_blocked_in_shroud_only", entryFrame, violation, ddEffective, needed, burstPlanning);
        }

        return Build(coverable ? "threshold_short_pre_refresh_coverable" : "threshold_short_in_shroud_only", entryFrame, violation, ddEffective, needed, burstPlanning);
    }

    private static bool DdEnshroudRequiresRprReview(string category)
        => category.StartsWith("missed_by_", StringComparison.Ordinal)
        || category.StartsWith("threshold_short_", StringComparison.Ordinal)
        || category == "unclassified_no_entry";

    private static string DdEnshroudActionability(string category)
    {
        if (DdEnshroudRequiresRprReview(category))
            return "true_rpr_logic_candidate";
        if (category.StartsWith("burst_planning_blocked_", StringComparison.Ordinal))
            return "proven_unsafe_broad_candidate";
        if (category is "target_or_downtime_stretched_enshroud" or "seeded_initial_state_enshroud" or "excused_target_loss_downtime_kill_edge")
            return "harness_or_edge_case";
        return "not_actionable";
    }

    private static bool InEvenBurstPlanningWindow(ActionFrame frame)
        => frame.Time >= 90 && (frame.Time % 120 >= 95 || frame.Time % 120 <= 25 || frame.ArcaneCircleLeft > 0 || frame.IdealHost);

    private static bool DdFinalOrDowntimeException(ScenarioDefinition scenario, double time)
        => scenario.FinalTwoGcdKill
        || scenario.KillTime - time <= scenario.Gcd * 2.2
        || scenario.Events.Any(e => e.Type == ScenarioEventType.TargetLost && e.Start <= time + scenario.Gcd * 2.2 && e.End > time);

    private sealed record DdEnshroudFrameInfo(
        double Time,
        string? SelectedGcd,
        string Ogcd,
        int BlueSouls,
        double DeathsDesignLeft,
        double ArcaneCircleLeft,
        bool IdealHost,
        string Reason);

    private sealed record DdEnshroudCase(
        string Scenario,
        int Level,
        double KillTime,
        double Gcd,
        string Opener,
        string Potion,
        string Mode,
        string Category,
        string LevelBand,
        double EntryTime,
        double DdAtEntry,
        double DdAfterEntryRefresh,
        double NeededDuration,
        double PreAnyThreshold,
        bool EntryInBurstPlanning,
        string EntryOgcds,
        string EnshroudReadyReason,
        DdEnshroudFrameInfo Violation);

    private static SoulSliceOvercapClassification BuildSoulSliceOvercapClassification(RegressionOutput output)
    {
        var excludedLowLevel = 0;
        var excludedPushed = 0;
        var excludedTargetOrKillEdge = 0;
        var excludedUnrealisticStart = 0;
        var excludedSeededOpeningGluttony = 0;
        var trueCandidates = new List<SoulSliceOvercapCandidate>();
        var bucketScenarioCount = 0;

        foreach (var result in output.Scenarios)
        {
            if (!result.HardFails.Contains(HardFailRule.GaugeFailure))
                continue;
            if (!result.Frames.Any(f => f.SoulSliceCharges >= 2 && f.SelectedGcd is not ("SoulSlice" or "SoulScythe") && f.BlueSouls == 0 && f.ReaverState == ReaverState.None))
                continue;

            ++bucketScenarioCount;
            var scenario = result.Scenario;
            if (scenario.Level < 80)
            {
                ++excludedLowLevel;
                continue;
            }

            var strict = result.Frames.Where(StrictSoulSliceOvercapViolation).ToList();
            if (strict.Count == 0)
            {
                ++excludedPushed;
                continue;
            }

            var surviving = strict
                .Where(f => !TargetLossOrKillEdge(scenario, f) && !UnrealisticInitialState(scenario, f))
                .ToList();
            if (surviving.Count == 0)
            {
                if (strict.All(f => TargetLossOrKillEdge(scenario, f)))
                    ++excludedTargetOrKillEdge;
                else
                    ++excludedUnrealisticStart;
                continue;
            }

            if (SeededOpeningGluttonyAfterEnshroud(scenario, surviving))
            {
                ++excludedSeededOpeningGluttony;
                continue;
            }

            var frame = surviving[0];
            trueCandidates.Add(new SoulSliceOvercapCandidate(
                scenario.Name,
                scenario.Level,
                scenario.KillTime,
                scenario.Gcd,
                scenario.Opener.ToString(),
                scenario.Potion.ToString(),
                scenario.InitialMode.ToString(),
                scenario.RedGauge,
                scenario.BlueGauge,
                scenario.DeathsDesignLeft,
                scenario.SoulSliceCharges,
                surviving.Count,
                new SoulSliceOvercapFrame(
                    frame.Time,
                    frame.SelectedGcd,
                    string.Join("|", frame.SelectedOgcds),
                    frame.RedGauge,
                    frame.BlueGauge,
                    frame.DeathsDesignLeft,
                    frame.ArcaneCircleReadyIn,
                    frame.ArcaneCircleLeft,
                    frame.SoulSliceCharges,
                    frame.Reason)));
        }

        return new SoulSliceOvercapClassification(
            bucketScenarioCount,
            excludedLowLevel,
            excludedPushed,
            excludedTargetOrKillEdge,
            excludedUnrealisticStart,
            excludedSeededOpeningGluttony,
            trueCandidates.Count,
            trueCandidates.Take(200).ToList());
    }

    private static bool StrictSoulSliceOvercapViolation(ActionFrame f)
        => f.TargetAvailable
        && f.Level >= 60
        && f.SoulSliceCharges >= 2
        && f.RotationMode == RotationMode.Full
        && f.ReaverState == ReaverState.None
        && f.BlueSouls == 0
        && !f.PerfectioParata
        && f.SelectedGcd is not ("SoulSlice" or "SoulScythe" or "ShadowOfDeath" or "WhorlOfDeath" or "PlentifulHarvest" or "Communio" or "VoidReaping" or "CrossReaping" or "GrimReaping" or "Perfectio" or "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine")
        && f.DeathsDesignRefreshReason is not ("emergency" or "pre_any_enshroud" or "two_refresh_pre_burst")
        && !(f.RedGauge > 50 && f.SelectedOgcds.Any(a => a is "BloodStalk" or "GrimSwathe" or "Gluttony"))
        && !(f.SelectedOgcds.Contains("Gluttony") && (f.ArcaneCircleLeft > 0 || f.PostPerfectioPriorityActive || f.TimeSincePerfectioUsed is > 0 && f.TimeSincePerfectioUsed <= f.Gcd + 0.1));

    private static bool TargetLossOrKillEdge(ScenarioDefinition scenario, ActionFrame frame)
        => frame.SelectedGcd == null
        || !frame.MeleeAvailable
        || !frame.FallbackTargetAvailable
        || scenario.KillTime - frame.Time <= scenario.Gcd * 2.2
        || scenario.Events.Any(e =>
            (e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable or ScenarioEventType.PrimaryTargetNull or ScenarioEventType.EmptyPriorityTargets)
            && e.End <= frame.Time
            && frame.Time - e.End <= scenario.Gcd * 1.2);

    private static bool UnrealisticInitialState(ScenarioDefinition scenario, ActionFrame frame)
        => scenario.SoulSliceCharges >= 1.9 && frame.Time < 10;

    private static bool SeededOpeningGluttonyAfterEnshroud(ScenarioDefinition scenario, IReadOnlyList<ActionFrame> frames)
        => scenario.Level is >= 80 and < 90
        && scenario.BlueGauge >= 50
        && scenario.SoulSliceCharges >= 1.5
        && frames.Count == 1
        && frames[0].Time <= 25
        && frames[0].SelectedOgcds.Contains("Gluttony")
        && frames[0].SelectedGcd is not ("SoulSlice" or "SoulScythe")
        && frames[0].SoulSliceCharges >= 2;

    private sealed record SoulSliceOvercapFrame(
        double Time,
        string? SelectedGcd,
        string Ogcd,
        int RedGauge,
        int BlueGauge,
        double DeathsDesignLeft,
        double ArcaneCircleReadyIn,
        double ArcaneCircleLeft,
        double SoulSliceCharges,
        string Reason);

    private sealed record SoulSliceOvercapCandidate(
        string Scenario,
        int Level,
        double KillTime,
        double Gcd,
        string Opener,
        string Potion,
        string Mode,
        int InitialRed,
        int InitialBlue,
        double InitialDeathsDesign,
        double InitialSoulSliceCharges,
        int SurvivingFrameCount,
        SoulSliceOvercapFrame Frame);

    private sealed record SoulSliceOvercapClassification(
        int BucketScenarioCount,
        int ExcludedLowLevel,
        int ExcludedPushedByHigherPriorityGcd,
        int ExcludedTargetLossOrKillEdge,
        int ExcludedUnrealisticInitialState,
        int ExcludedSeededOpeningGluttonyAfterEnshroud,
        int TrueRprLogicCandidateCount,
        List<SoulSliceOvercapCandidate> TrueCandidates);

    private static IEnumerable<string> ClassifyRealHardFail(ScenarioResult result)
    {
        if (result.HardFails.Contains(HardFailRule.GaugeFailure))
        {
            if (result.Scenario.Level < 80)
                yield return "gauge_low_level_or_no_enshroud";
            if (result.Frames.Any(f => (f.SelectedGcd is "SoulSlice" or "SoulScythe") && f.RedGauge > 50 && !f.SelectedOgcds.Any(a => a is "BloodStalk" or "GrimSwathe")))
                yield return "gauge_soul_slice_would_red_overcap";
            if (result.Frames.Any(f => f.RotationMode == RotationMode.Basic && f.RedGauge >= 100 && f.BlueSouls == 0 && !f.SelectedOgcds.Any(a => a is "BloodStalk" or "GrimSwathe")))
                yield return "gauge_basic_red_overcap";
            if (result.Frames.Any(f => f.RotationMode == RotationMode.Full && f.RedGauge >= 100 && f.BlueSouls == 0 && f.ReaverState == ReaverState.None && f.SelectedOgcds.Count == 0 && f.ArcaneCircleReadyIn > 10))
                yield return "gauge_full_red_overcap_idle";
            if (result.Frames.Any(f => f.SoulSliceCharges >= 2 && f.SelectedGcd is not ("SoulSlice" or "SoulScythe") && f.BlueSouls == 0 && f.ReaverState == ReaverState.None))
                yield return "gauge_soul_slice_charge_overcap";
            if (result.Scenario.ExpectSecondEnshroudBlueGauge && result.Frames.Where(f => f.Time >= 118 && f.Time <= 135).Any() && result.Frames.Where(f => f.Time >= 118 && f.Time <= 135).All(f => f.BlueGauge < 50 && !f.IdealHost && !f.SelectedOgcds.Contains("Enshroud")))
                yield return "gauge_even_second_enshroud_blue_shortage";
        }

        if (result.HardFails.Contains(HardFailRule.DeathsDesignFailure))
        {
            if (result.Frames.Any(f => f.BlueSouls > 0 && f.DeathsDesignLeft <= 0 && f.TargetAvailable && f.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath")))
                yield return "dd_dropped_during_enshroud";
            if (result.Frames.Any(f => f.Time >= 110 && f.Time % 120 <= 35 && f.DeathsDesignLeft <= 0 && f.TargetAvailable))
                yield return "dd_dropped_during_even_burst";
            if (result.Frames.Any(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath" && f.BlueSouls > 0 && f.Reason != "second DD refresh required during first Enshroud after first Reaping"))
                yield return "dd_illegal_enshroud_refresh";
        }

        if (result.HardFails.Contains(HardFailRule.DriftFailure))
        {
            yield return result.Frames.Any(f => f.RotationMode == RotationMode.Basic)
                ? "drift_mode_switch_window"
                : "drift_full_mode_gluttony_interval";
        }

        if (result.HardFails.Contains(HardFailRule.BurstFailure))
            yield return "burst_expectation_missed";

        if (result.HardFails.Contains(HardFailRule.IllegalAction))
            yield return "illegal_action_or_level_sync";
    }

    private static RealFailureFrameSummary? RepresentativeFailureFrame(ScenarioResult result)
    {
        var frame = result.Frames.FirstOrDefault(f =>
            result.HardFails.Contains(HardFailRule.GaugeFailure) && ((f.SelectedGcd is "SoulSlice" or "SoulScythe") && f.RedGauge > 50 || f.RedGauge >= 100)
            || result.HardFails.Contains(HardFailRule.DeathsDesignFailure) && f.DeathsDesignLeft <= 0 && f.TargetAvailable
            || result.HardFails.Contains(HardFailRule.DriftFailure) && f.SelectedOgcds.Contains("Gluttony"))
            ?? result.Frames.FirstOrDefault(f => f.SelectedGcd != null || f.SelectedOgcds.Count > 0);

        return frame == null
            ? null
            : new(
                frame.Time,
                frame.SelectedGcd,
                string.Join("|", frame.SelectedOgcds),
                frame.RedGauge,
                frame.BlueGauge,
                frame.DeathsDesignLeft,
                frame.ArcaneCircleLeft,
                frame.ArcaneCircleReadyIn,
                frame.BlueSouls,
                frame.ReaverState,
                frame.RotationMode,
                frame.Reason);
    }

    private sealed record RealFailureFrameSummary(
        double Time,
        string? SelectedGcd,
        string Ogcd,
        int RedGauge,
        int BlueGauge,
        double DeathsDesignLeft,
        double ArcaneCircleLeft,
        double ArcaneCircleReadyIn,
        int BlueSouls,
        ReaverState ReaverState,
        RotationMode RotationMode,
        string Reason);

    private static void WriteCsv(string path, IReadOnlyList<ScenarioResult> scenarios)
    {
        var sb = new StringBuilder();
        sb.AppendLine("scenario,seed,kill_time,gcd,opener,pot,mode,result,hard_fail_reason,total_potency,gcd_uptime,ac_count,gluttony_count,enshroud_count,communio_count,perfectio_count,dd_uptime,dd_refresh_count,red_overcap,blue_overcap,skipped_gcd,clip_risk_count");
        foreach (var result in scenarios)
        {
            var s = result.Scenario;
            var m = result.Metrics;
            sb.AppendCsv(s.Name).AppendCsv(s.Seed.ToString(CultureInfo.InvariantCulture)).AppendCsv(s.KillTime.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(s.Gcd.ToString(CultureInfo.InvariantCulture)).AppendCsv(s.Opener.ToString()).AppendCsv(s.Potion.ToString()).AppendCsv(s.InitialMode.ToString());
            sb.AppendCsv(result.Passed ? "PASS" : "FAIL").AppendCsv(string.Join("|", result.HardFails));
            sb.AppendCsv(m.TotalPotencyEstimate.ToString(CultureInfo.InvariantCulture)).AppendCsv(m.GcdUptime.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(m.ArcaneCircleCount.ToString(CultureInfo.InvariantCulture)).AppendCsv(m.GluttonyCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(m.EnshroudCount.ToString(CultureInfo.InvariantCulture)).AppendCsv(m.CommunioCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(m.PerfectioCount.ToString(CultureInfo.InvariantCulture)).AppendCsv(m.DeathsDesignUptime.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(m.DeathsDesignRefreshCount.ToString(CultureInfo.InvariantCulture)).AppendCsv(m.RedGaugeOvercap.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(m.BlueGaugeOvercap.ToString(CultureInfo.InvariantCulture)).AppendCsv(m.SkippedGcdCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(m.ClippingRiskCount.ToString(CultureInfo.InvariantCulture), end: true);
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteTimeline(string path, ScenarioResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("time,gcd,ogcd,target,red,blue,soul_slice_charges,dd_left,ac_left,blue_souls,reaver,enhanced_gibbet_left,enhanced_gallows_left,immortal_sacrifice,bloodsown_left,ideal_host,perfectio_occulta,perfectio_parata,perfectio_parata_left,plentiful_harvest_ready,enshroud_ready_reason,dd_one_refresh,dd_two_refresh,dd_pre_arcane_refresh,dd_pre_any_enshroud_refresh,enshroud_blocked_for_dd,dd_refresh_reason,gcd_window_index,weave_slot,weave_count_in_gcd,ogcd_order_in_gcd,clip_risk,clip_reason,reason");
        foreach (var frame in result.Frames)
        {
            sb.AppendCsv(frame.Time.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.SelectedGcd ?? "");
            sb.AppendCsv(string.Join("|", frame.SelectedOgcds)).AppendCsv(frame.TargetAvailable ? "available" : "lost");
            sb.AppendCsv(frame.RedGauge.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.BlueGauge.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.SoulSliceCharges.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.DeathsDesignLeft.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.ArcaneCircleLeft.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.BlueSouls.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.ReaverState.ToString());
            sb.AppendCsv(frame.EnhancedGibbetLeft.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.EnhancedGallowsLeft.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.ImmortalSacrifice.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.BloodsownLeft.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.IdealHost.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.PerfectioOcculta.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.PerfectioParata.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.PerfectioParataLeft.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.PlentifulHarvestReady.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.EnshroudReadyReason).AppendCsv(frame.DeathsDesignOneRefresh.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.DeathsDesignTwoRefresh.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.DeathsDesignPreArcaneRefresh.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.DeathsDesignPreAnyEnshroudRefresh.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.EnshroudBlockedForDeathsDesign.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.DeathsDesignRefreshReason).AppendCsv(frame.GcdWindowIndex.ToString(CultureInfo.InvariantCulture));
            sb.AppendCsv(frame.WeaveSlot).AppendCsv(frame.WeaveCountInGcd.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.OgcdOrderInGcd);
            sb.AppendCsv(frame.ClipRisk.ToString(CultureInfo.InvariantCulture)).AppendCsv(frame.ClipReason).AppendCsv(frame.Reason, end: true);
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteWeaveAnalysis(string outDir, RegressionOutput output)
    {
        var frames = output.Scenarios.SelectMany(s => s.Frames.Select(f => (Scenario: s.Scenario, Frame: f))).ToList();
        var tripleWeave = frames.Where(x => x.Frame.ClipReason == "triple_weave_candidate").ToList();
        var burstOverload = frames.Where(x => x.Frame.ClipReason == "burst_weave_overload_candidate").ToList();
        var highPing = frames.Where(x => x.Frame.ClipReason == "high_ping_double_weave_softcheck").ToList();
        var acBeforeGluttonyFailures = frames.Where(x => OgcdIndex(x.Frame, "ArcaneCircle") >= 0 && OgcdIndex(x.Frame, "Gluttony") >= 0 && OgcdIndex(x.Frame, "Gluttony") < OgcdIndex(x.Frame, "ArcaneCircle")).ToList();
        var acBeforeSacrificiumFailures = frames.Where(x => OgcdIndex(x.Frame, "ArcaneCircle") >= 0 && OgcdIndex(x.Frame, "Sacrificium") >= 0 && OgcdIndex(x.Frame, "Sacrificium") < OgcdIndex(x.Frame, "ArcaneCircle")).ToList();
        var potionAfterArcaneFailures = frames.Where(x =>
        {
            var arcaneCircle = OgcdIndex(x.Frame, "ArcaneCircle");
            var potion = OgcdIndex(x.Frame, "Potion");
            if (arcaneCircle < 0 || potion < 0)
                return false;
            var laterPotionBurst = x.Frame.Time >= 240;
            return laterPotionBurst ? potion < arcaneCircle : potion > arcaneCircle;
        }).ToList();
        var potionStandalone = frames.Where(x => OgcdIndex(x.Frame, "Potion") >= 0 && OgcdIndex(x.Frame, "ArcaneCircle") < 0 && x.Frame.ArcaneCircleLeft <= 0 && x.Frame.ArcaneCircleReadyIn > x.Frame.Gcd * 3).ToList();
        var potionOff = frames.Where(x => x.Scenario.Potion == PotionMode.Off && OgcdIndex(x.Frame, "Potion") >= 0).ToList();
        var potionOpenerExcept = frames.Where(x => x.Scenario.Potion == PotionMode.EvenBurstExceptOpener && x.Frame.Time < 30 && OgcdIndex(x.Frame, "Potion") >= 0).ToList();
        var enshroudReapingConflicts = frames.Where(x => x.Frame.BlueSouls > 0 && x.Frame.GcdCandidates.Contains("GrimReaping") && x.Frame.GcdCandidates.Any(a => a is "VoidReaping" or "CrossReaping")).ToList();
        var enshroudAoeSingleSwitches = output.Scenarios
            .SelectMany(s => s.Frames.Zip(s.Frames.Skip(1), (a, b) => (Scenario: s.Scenario, Previous: a, Frame: b)))
            .Where(x => x.Frame.BlueSouls > 0 && x.Previous.ConeTargets != x.Frame.ConeTargets)
            .ToList();
        var enshroudFallbackTargetUsed = frames.Where(x => x.Frame.BlueSouls > 0 && x.Frame.ConeTargets > 2 && !x.Frame.BestConeTargetAvailable && x.Frame.SelectedGcd is "VoidReaping" or "CrossReaping").ToList();
        var enshroudEmptyGcdCandidates = frames.Where(x => x.Frame.BlueSouls > 0 && x.Frame.TargetAvailable && x.Frame.ExpectedActionPossible && x.Frame.SelectedGcd == null).ToList();
        var lemureScytheToSliceFallback = frames.Where(x => x.Frame.BlueSouls > 0 && x.Frame.ConeTargets > 2 && !x.Frame.BestConeTargetAvailable && x.Frame.SelectedOgcds.Contains("LemuresSlice")).ToList();
        var openerIssues = output.Scenarios
            .Where(s => s.Scenario.Category == ScenarioCategory.WeaveValidation && s.Scenario.Name.Contains("opener", StringComparison.OrdinalIgnoreCase))
            .Select(s => new WeaveScenarioIssue(s.Scenario.Name, string.Join("|", s.HardFails), FirstActionSummary(s)))
            .ToList();
        var rprCandidates = output.Scenarios
            .Where(s => s.HardFails.Any(f => f is HardFailRule.WeaveOrderFailure or HardFailRule.PotionFailure or HardFailRule.OpenerFailure))
            .Select(s => new WeaveScenarioIssue(s.Scenario.Name, string.Join("|", s.HardFails), s.HardFails.Contains(HardFailRule.OpenerFailure) ? FirstActionSummary(s) : FirstRiskSummary(s)))
            .ToList();

        var bucket = new WeaveBucketSummary(
            TripleWeaveCandidates: tripleWeave.Count,
            BurstWeaveOverloadCandidates: burstOverload.Count,
            HighPingDoubleWeaveSoftcheckCount: highPing.Count,
            ArcaneCircleBeforeGluttonyFailures: acBeforeGluttonyFailures.Count,
            ArcaneCircleBeforeSacrificiumFailures: acBeforeSacrificiumFailures.Count,
            PotionAfterArcaneCircleFailures: potionAfterArcaneFailures.Count,
            PotionStandaloneFailures: potionStandalone.Count,
            PotionOffFailures: potionOff.Count,
            PotionEvenExceptOpenerFailures: potionOpenerExcept.Count,
            OpenerIssues: openerIssues,
            RprChangeCandidates: rprCandidates);

        WriteJson(Path.Combine(outDir, "weave_buckets.json"), bucket);

        var sb = new StringBuilder();
        sb.AppendLine("# RPR weave / clip analysis");
        sb.AppendLine();
        sb.AppendLine($"- triple weave candidates: {tripleWeave.Count}");
        sb.AppendLine($"- burst weave overload candidates: {burstOverload.Count}");
        sb.AppendLine($"- high-ping double-weave softcheck count: {highPing.Count}");
        sb.AppendLine($"- Arcane Circle before Gluttony failures: {acBeforeGluttonyFailures.Count}");
        sb.AppendLine($"- Arcane Circle before Sacrificium failures: {acBeforeSacrificiumFailures.Count}");
        sb.AppendLine($"- Potion after Arcane Circle failures: {potionAfterArcaneFailures.Count}");
        sb.AppendLine($"- Potion standalone failures: {potionStandalone.Count}");
        sb.AppendLine($"- Potion Off failures: {potionOff.Count}");
        sb.AppendLine($"- Potion opener-excluded failures: {potionOpenerExcept.Count}");
        sb.AppendLine($"- enshroud_reaping_conflict_count: {enshroudReapingConflicts.Count}");
        sb.AppendLine($"- enshroud_aoe_single_switch_count: {enshroudAoeSingleSwitches.Count}");
        sb.AppendLine($"- enshroud_fallback_target_used_count: {enshroudFallbackTargetUsed.Count}");
        sb.AppendLine($"- enshroud_empty_gcd_candidate_count: {enshroudEmptyGcdCandidates.Count}");
        sb.AppendLine($"- lemure_scythe_to_slice_fallback_count: {lemureScytheToSliceFallback.Count}");
        sb.AppendLine();
        sb.AppendLine("## Opener mode checks");
        if (openerIssues.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var issue in openerIssues)
                sb.AppendLine($"- {issue.Scenario}: {issue.HardFails}; {issue.Summary}");
        sb.AppendLine();
        sb.AppendLine("## RPR.cs change candidates");
        if (rprCandidates.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var issue in rprCandidates)
                sb.AppendLine($"- {issue.Scenario}: {issue.HardFails}; {issue.Summary}");

        File.WriteAllText(Path.Combine(outDir, "weave_analysis.md"), sb.ToString());
    }

    private static void WriteWeaveActionabilityAnalysis(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .SelectMany(result => result.Frames
                .Where(f => f.ClipReason == "triple_weave_candidate")
                .Select(frame => ClassifyWeaveActionability(result, frame)))
            .ToList();
        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var actionabilityCounts = cases
            .GroupBy(c => c.Actionability)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var candidateScenarioNames = cases
            .Where(c => c.Actionability == "local_candidate_requires_candidate_run")
            .Select(c => c.Scenario)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        WriteJson(Path.Combine(outDir, "weave_actionability_buckets.json"), new
        {
            CaseCount = cases.Count,
            LocalCandidateCount = cases.Count(c => c.Actionability == "local_candidate_requires_candidate_run"),
            CategoryCounts = categoryCounts,
            ActionabilityCounts = actionabilityCounts,
            LocalCandidates = cases.Where(c => c.Actionability == "local_candidate_requires_candidate_run").ToList(),
            Cases = cases.Take(1000).ToList()
        });

        var candidateTimelineDir = Path.Combine(outDir, "weave_candidate_timelines");
        if (Directory.Exists(candidateTimelineDir))
            Directory.Delete(candidateTimelineDir, recursive: true);
        Directory.CreateDirectory(candidateTimelineDir);
        foreach (var result in output.Scenarios.Where(s => candidateScenarioNames.Contains(s.Scenario.Name)))
            WriteTimeline(Path.Combine(candidateTimelineDir, $"{Sanitize(result.Scenario.Name)}.csv"), result);

        var sb = new StringBuilder();
        sb.AppendLine("# RPR weave actionability analysis");
        sb.AppendLine();
        sb.AppendLine($"- triple weave cases: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- local_candidate_requires_candidate_run: {cases.Count(c => c.Actionability == "local_candidate_requires_candidate_run").ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        if (categoryCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in categoryCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        if (actionabilityCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in actionabilityCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- This report classifies triple weave candidates without changing HardFail rules.");
        sb.AppendLine("- Broad Arcane Circle weave reservation and broad Enshroud deferral are proven unsafe from prior rejected candidates.");
        sb.AppendLine("- A local candidate still requires a separate RPR.cs candidate run against standard and real baselines before adoption.");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 20)");
            foreach (var item in cases.Where(c => c.Category == category).Take(20))
                sb.AppendLine($"- {item.Scenario}: t={item.Time.ToString(CultureInfo.InvariantCulture)} gcd={item.SelectedGcd ?? ""} ogcd={item.Ogcd} red={item.RedGauge.ToString(CultureInfo.InvariantCulture)} blue={item.BlueGauge.ToString(CultureInfo.InvariantCulture)} dd={item.DeathsDesignLeft.ToString(CultureInfo.InvariantCulture)} acIn={item.ArcaneCircleReadyIn.ToString(CultureInfo.InvariantCulture)} acLeft={item.ArcaneCircleLeft.ToString(CultureInfo.InvariantCulture)} souls={item.BlueSouls.ToString(CultureInfo.InvariantCulture)} actionability={item.Actionability} note={item.Note}");
        }

        File.WriteAllText(Path.Combine(outDir, "weave_actionability_analysis.md"), sb.ToString());
    }

    private static WeaveActionabilityCase ClassifyWeaveActionability(ScenarioResult result, ActionFrame frame)
    {
        var category = WeaveActionabilityCategory(frame);
        return new(
            result.Scenario.Name,
            result.Scenario.Level,
            result.Scenario.KillTime,
            result.Scenario.Gcd,
            result.Scenario.Opener.ToString(),
            result.Scenario.Potion.ToString(),
            result.Scenario.InitialMode.ToString(),
            category,
            WeaveActionability(category),
            frame.Time,
            frame.SelectedGcd,
            string.Join("|", frame.SelectedOgcds),
            frame.RedGauge,
            frame.BlueGauge,
            frame.DeathsDesignLeft,
            frame.ArcaneCircleReadyIn,
            frame.ArcaneCircleLeft,
            frame.BlueSouls,
            frame.PurpleSouls,
            frame.Reason,
            WeaveActionabilityNote(category));
    }

    private static string WeaveActionabilityCategory(ActionFrame frame)
    {
        var hasPotion = frame.SelectedOgcds.Contains("Potion");
        var hasArcaneCircle = frame.SelectedOgcds.Contains("ArcaneCircle");
        var hasEnshroud = frame.SelectedOgcds.Contains("Enshroud");
        var hasBloodStalk = frame.SelectedOgcds.Contains("BloodStalk");
        var hasGrimSwathe = frame.SelectedOgcds.Contains("GrimSwathe");
        var hasGluttony = frame.SelectedOgcds.Contains("Gluttony");
        var hasSacrificium = frame.SelectedOgcds.Contains("Sacrificium");
        var hasLemure = frame.SelectedOgcds.Any(a => a is "LemuresSlice" or "LemuresScythe");
        var hasTrueNorth = frame.SelectedOgcds.Contains("TrueNorth");
        var hasRedSpend = hasBloodStalk || hasGrimSwathe || hasGluttony;

        if (hasArcaneCircle && hasEnshroud)
            return "arcane_enshroud_density_proven_unsafe_to_block";
        if (hasPotion && hasArcaneCircle && hasRedSpend)
            return frame.RedGauge >= 90 ? "potion_arcane_red_spend_overcap_prevention" : "potion_arcane_red_spend_soft_risk";
        if (hasArcaneCircle && hasRedSpend)
            return frame.RedGauge >= 90 ? "arcane_red_spend_overcap_prevention" : "arcane_red_spend_soft_risk";
        if (hasArcaneCircle && hasSacrificium)
            return "arcane_sacrificium_density_soft_risk";
        if (hasArcaneCircle && hasLemure)
            return "arcane_lemure_density_soft_risk";
        if (hasSacrificium && hasLemure)
            return frame.PurpleSouls > 2 ? "lemure_sacrificium_local_candidate" : "lemure_sacrificium_overcap_prevention";
        if (hasPotion && hasTrueNorth)
            return "potion_true_north_local_candidate";
        if (hasEnshroud && hasRedSpend)
            return "enshroud_red_spend_soft_risk";
        return "weave_density_soft_risk_no_safe_local_candidate";
    }

    private static string WeaveActionability(string category)
        => category.EndsWith("_local_candidate", StringComparison.Ordinal)
            ? "local_candidate_requires_candidate_run"
            : category.Contains("proven_unsafe", StringComparison.Ordinal)
                ? "proven_unsafe_broad_candidate"
                : category.Contains("overcap_prevention", StringComparison.Ordinal)
                    ? "necessary_overcap_prevention"
                    : "soft_weave_risk_no_current_rpr_change";

    private static string WeaveActionabilityNote(string category)
        => WeaveActionability(category) == "local_candidate_requires_candidate_run"
            ? "narrow local deferral may be testable, but must preserve counts and uptime"
            : "not an immediate RPR.cs change candidate";

    private sealed record WeaveActionabilityCase(
        string Scenario,
        int Level,
        double KillTime,
        double Gcd,
        string Opener,
        string Potion,
        string Mode,
        string Category,
        string Actionability,
        double Time,
        string? SelectedGcd,
        string Ogcd,
        int RedGauge,
        int BlueGauge,
        double DeathsDesignLeft,
        double ArcaneCircleReadyIn,
        double ArcaneCircleLeft,
        int BlueSouls,
        int PurpleSouls,
        string Reason,
        string Note);

    private static void WriteHighEndRotationOpportunityAnalysis(string outDir, RegressionOutput output)
    {
        var cases = output.Scenarios
            .Where(IsHighEndReviewScenario)
            .SelectMany(ClassifyHighEndRotationOpportunity)
            .ToList();
        var categoryCounts = cases
            .GroupBy(c => c.Category)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var actionabilityCounts = cases
            .GroupBy(c => c.Actionability)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var candidateScenarioNames = cases
            .Where(c => c.Actionability == "candidate_requires_candidate_run")
            .Select(c => c.Scenario)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        WriteJson(Path.Combine(outDir, "high_end_rotation_opportunities.json"), new
        {
            ReviewedScenarioCount = output.Scenarios.Count(IsHighEndReviewScenario),
            CaseCount = cases.Count,
            CandidateCount = cases.Count(c => c.Actionability == "candidate_requires_candidate_run"),
            CategoryCounts = categoryCounts,
            ActionabilityCounts = actionabilityCounts,
            Candidates = cases.Where(c => c.Actionability == "candidate_requires_candidate_run").ToList(),
            Cases = cases.Take(1000).ToList()
        });

        var candidateTimelineDir = Path.Combine(outDir, "high_end_candidate_timelines");
        if (Directory.Exists(candidateTimelineDir))
            Directory.Delete(candidateTimelineDir, recursive: true);
        Directory.CreateDirectory(candidateTimelineDir);
        foreach (var result in output.Scenarios.Where(s => candidateScenarioNames.Contains(s.Scenario.Name)))
            WriteTimeline(Path.Combine(candidateTimelineDir, $"{Sanitize(result.Scenario.Name)}.csv"), result);

        var sb = new StringBuilder();
        sb.AppendLine("# RPR high-end rotation opportunity analysis");
        sb.AppendLine();
        sb.AppendLine($"- reviewed scenarios: {output.Scenarios.Count(IsHighEndReviewScenario).ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- cases: {cases.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- candidate_requires_candidate_run: {cases.Count(c => c.Actionability == "candidate_requires_candidate_run").ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Category counts");
        if (categoryCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in categoryCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Actionability counts");
        if (actionabilityCounts.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in actionabilityCounts)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("## Notes");
        sb.AppendLine("- This analysis reviews passed Lv100 FullMode non-edge scenarios only.");
        sb.AppendLine("- It is a triage report. RPR.cs changes still require a separate candidate run against standard and real baselines.");
        sb.AppendLine("- Cases marked no_current_rpr_change are either already covered by earlier rejected candidates or are expected burst/priority tradeoffs.");

        foreach (var category in categoryCounts.Keys)
        {
            sb.AppendLine();
            sb.AppendLine($"## {category} (first 20)");
            foreach (var item in cases.Where(c => c.Category == category).Take(20))
                sb.AppendLine($"- {item.Scenario}: t={item.Time.ToString(CultureInfo.InvariantCulture)} gcd={item.SelectedGcd ?? ""} ogcd={item.Ogcd} red={item.RedGauge.ToString(CultureInfo.InvariantCulture)} blue={item.BlueGauge.ToString(CultureInfo.InvariantCulture)} dd={item.DeathsDesignLeft.ToString(CultureInfo.InvariantCulture)} acIn={item.ArcaneCircleReadyIn.ToString(CultureInfo.InvariantCulture)} acLeft={item.ArcaneCircleLeft.ToString(CultureInfo.InvariantCulture)} metric={item.Metric.ToString(CultureInfo.InvariantCulture)} actionability={item.Actionability} note={item.Note}");
        }

        File.WriteAllText(Path.Combine(outDir, "high_end_rotation_opportunities.md"), sb.ToString());
    }

    private static bool IsHighEndReviewScenario(ScenarioResult result)
    {
        var scenario = result.Scenario;
        return result.Passed
            && scenario.Level == 100
            && scenario.InitialMode == RotationMode.Full
            && scenario.KillTime >= 360
            && !scenario.FinalTwoGcdKill
            && !scenario.Events.Any(e => e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable or ScenarioEventType.PrimaryTargetNull or ScenarioEventType.EmptyPriorityTargets or ScenarioEventType.ModeSwitch);
    }

    private static IEnumerable<HighEndOpportunityCase> ClassifyHighEndRotationOpportunity(ScenarioResult result)
    {
        if (result.Metrics.DeathsDesignUptime < 0.99)
        {
            var frame = result.Frames.FirstOrDefault(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath")
                ?? result.Frames.FirstOrDefault(f => f.DeathsDesignLeft <= 0 && f.TargetAvailable)
                ?? result.Frames[0];
            yield return HighEndOpportunity(result, frame, HighEndDeathsDesignUptimeCategory(result, frame), result.Metrics.DeathsDesignUptime);
        }

        if (result.Metrics.BlueGaugeOvercap > 0)
        {
            var frame = result.Frames.FirstOrDefault(f => f.BlueGauge >= 100) ?? result.Frames[0];
            yield return HighEndOpportunity(result, frame, HighEndBlueOvercapCategory(frame), result.Metrics.BlueGaugeOvercap);
        }

        if (result.Metrics.RedGaugeOvercap > 0)
        {
            var frame = result.Frames.FirstOrDefault(f => f.RedGauge >= 100) ?? result.Frames[0];
            yield return HighEndOpportunity(result, frame, HighEndRedOvercapCategory(frame), result.Metrics.RedGaugeOvercap);
        }

        if (result.Metrics.ClippingRiskCount > 0)
        {
            var frame = result.Frames.FirstOrDefault(f => f.ClipRisk) ?? result.Frames[0];
            yield return HighEndOpportunity(result, frame, HighEndClipRiskCategory(frame), result.Metrics.ClippingRiskCount);
        }

        foreach (var frame in result.Frames.Where(f => f.SelectedOgcds.Contains("Gluttony") && f.ArcaneCircleLeft <= 0 && f.ArcaneCircleReadyIn > 15))
            yield return HighEndOpportunity(result, frame, HighEndGluttonyOutsideArcaneCategory(result, frame), result.Metrics.GluttonyDriftSeconds);

        foreach (var frame in result.Frames.Where(f => f.SelectedOgcds.Contains("Enshroud") && f.ArcaneCircleLeft <= 0 && f.ArcaneCircleReadyIn is > 0 and <= 20))
            yield return HighEndOpportunity(result, frame, HighEndEnshroudBeforeNearArcaneCategory(frame), frame.ArcaneCircleReadyIn);

        foreach (var frame in result.Frames.Where(f => f.SelectedGcd == "Perfectio" && f.ArcaneCircleLeft <= 0 && f.ArcaneCircleReadyIn > 20))
            yield return HighEndOpportunity(result, frame, HighEndPerfectioOutsideArcaneCategory(result, frame), frame.ArcaneCircleReadyIn);
    }

    private static string HighEndGluttonyOutsideArcaneCategory(ScenarioResult result, ActionFrame frame)
    {
        if (frame.ArcaneCircleReadyIn <= 20)
        {
            if (frame.RedGauge >= 90)
                return "gluttony_outside_arcane_15_20_red_overcap_prevention";
            if (result.Metrics.GluttonyDriftSeconds >= 2.9)
                return "gluttony_outside_arcane_15_20_drift_cap_pressure";
            if (frame.DeathsDesignLeft <= frame.Gcd * 2)
                return "gluttony_outside_arcane_15_20_dd_pressure";
            return "gluttony_outside_arcane_15_20_review_no_safe_candidate";
        }

        if (frame.ArcaneCircleReadyIn <= 30)
            return frame.RedGauge >= 90
                ? "gluttony_outside_arcane_20_30_red_overcap_prevention"
                : "gluttony_outside_arcane_20_30_normal_cycle";

        if (frame.ArcaneCircleReadyIn <= 60)
            return "gluttony_outside_arcane_midcycle";

        return "gluttony_outside_arcane_far_cycle";
    }

    private static string HighEndPerfectioOutsideArcaneCategory(ScenarioResult result, ActionFrame frame)
    {
        var communio = result.Frames.LastOrDefault(f => f.Time <= frame.Time && f.SelectedGcd == "Communio");
        var secondsSinceCommunio = communio == null ? double.PositiveInfinity : frame.Time - communio.Time;

        if (secondsSinceCommunio <= frame.Gcd * 1.2)
            return "perfectio_outside_arcane_immediate_after_communio";

        if (frame.ArcaneCircleReadyIn <= 30)
        {
            if (frame.PerfectioParataLeft > frame.ArcaneCircleReadyIn + frame.Gcd)
                return "perfectio_outside_arcane_20_30_hold_rejected_count_loss";
            return "perfectio_outside_arcane_20_30_expiry_pressure";
        }

        if (frame.ArcaneCircleReadyIn <= 60)
            return "perfectio_outside_arcane_midcycle";

        return "perfectio_outside_arcane_far_cycle";
    }

    private static string HighEndEnshroudBeforeNearArcaneCategory(ActionFrame frame)
    {
        if (frame.ArcaneCircleReadyIn <= frame.Gcd * 2.5)
            return "enshroud_near_arcane_double_entry_window";

        if (frame.BlueGauge >= 100)
            return "enshroud_near_arcane_blue_overcap_relief";

        if (frame.DeathsDesignLeft <= frame.Gcd * 5 + 1.0)
            return "enshroud_near_arcane_dd_pressure";

        return "enshroud_near_arcane_hold_rejected_broad_candidate";
    }

    private static string HighEndDeathsDesignUptimeCategory(ScenarioResult result, ActionFrame frame)
    {
        var firstDeathDesign = result.Frames.FirstOrDefault(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath");
        if (firstDeathDesign != null && firstDeathDesign.Time < 30)
            return "high_end_dd_uptime_opener_application_gap";

        var dropped = result.Frames.FirstOrDefault(f => f.Time > 30 && f.TargetAvailable && f.DeathsDesignLeft <= 0);
        if (dropped == null)
            return "high_end_dd_uptime_metric_rounding";

        if (dropped.BlueSouls > 0 || dropped.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" or "Communio")
            return "high_end_dd_uptime_enshroud_occupancy";

        if (dropped.ReaverState != ReaverState.None || dropped.SelectedGcd is "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine")
            return "high_end_dd_uptime_reaver_occupancy";

        if (dropped.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath")
        {
            var previousBurstGcd = result.Frames.Any(f =>
                f.Time < dropped.Time
                && dropped.Time - f.Time <= f.Gcd * 3.5
                && f.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" or "Communio" or "Perfectio" or "PlentifulHarvest"
                    or "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine");
            if (previousBurstGcd)
                return "high_end_dd_uptime_post_burst_refresh_gap";

            return "high_end_dd_uptime_refreshed_on_gap_frame";
        }

        return "high_end_dd_uptime_review";
    }

    private static string HighEndBlueOvercapCategory(ActionFrame frame)
    {
        if (frame.SelectedOgcds.Contains("Enshroud"))
            return "high_end_blue_overcap_enshroud_already_selected";
        if (frame.BlueSouls > 0 || frame.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" or "Communio")
            return "high_end_blue_overcap_enshroud_occupancy";
        if (frame.DeathsDesignLeft <= frame.Gcd * 5 + 1.0)
            return "high_end_blue_overcap_dd_insufficient_for_enshroud";
        if (frame.ArcaneCircleLeft > 0 || frame.ArcaneCircleReadyIn <= 20)
            return "high_end_blue_overcap_burst_planning";
        return "high_end_blue_overcap_review";
    }

    private static string HighEndRedOvercapCategory(ActionFrame frame)
    {
        if (frame.SelectedOgcds.Any(a => a is "BloodStalk" or "GrimSwathe" or "Gluttony"))
            return "high_end_red_overcap_spend_already_selected";
        if (frame.ReaverState != ReaverState.None || frame.BlueSouls > 0)
            return "high_end_red_overcap_occupied";
        if (frame.ArcaneCircleLeft > 0 || frame.ArcaneCircleReadyIn <= 20)
            return "high_end_red_overcap_burst_planning";
        return "high_end_red_overcap_review";
    }

    private static string HighEndClipRiskCategory(ActionFrame frame)
    {
        var hasArcane = frame.SelectedOgcds.Contains("ArcaneCircle");
        var hasPotion = frame.SelectedOgcds.Contains("Potion");
        var hasEnshroud = frame.SelectedOgcds.Contains("Enshroud");
        var hasRedSpend = frame.SelectedOgcds.Any(a => a is "BloodStalk" or "GrimSwathe" or "Gluttony");

        if (hasArcane && hasEnshroud)
            return "high_end_clip_arcane_enshroud_proven_unsafe_to_block";
        if (hasPotion && hasArcane && hasRedSpend)
            return "high_end_clip_potion_arcane_red_spend_rejected_candidate";
        return "high_end_clip_soft_risk";
    }

    private static HighEndOpportunityCase HighEndOpportunity(ScenarioResult result, ActionFrame frame, string category, double metric)
        => new(
            result.Scenario.Name,
            category,
            HighEndOpportunityActionability(category),
            metric,
            frame.Time,
            frame.SelectedGcd,
            string.Join("|", frame.SelectedOgcds),
            frame.RedGauge,
            frame.BlueGauge,
            frame.DeathsDesignLeft,
            frame.ArcaneCircleReadyIn,
            frame.ArcaneCircleLeft,
            frame.PerfectioParataLeft,
            frame.Reason,
            HighEndOpportunityNote(category));

    private static string HighEndOpportunityActionability(string category)
        => category is "high_end_blue_overcap_review" or "high_end_red_overcap_review"
            ? "candidate_requires_candidate_run"
            : "no_current_rpr_change";

    private static string HighEndOpportunityNote(string category)
    {
        var actionability = HighEndOpportunityActionability(category);
        if (actionability == "candidate_requires_candidate_run")
            return "narrow high-end candidate only; must preserve DD uptime, counts, and real score";
        if (category == "perfectio_outside_arcane_20_30_hold_rejected_count_loss")
            return "tested as Perfectio hold-for-Arcane candidate; rejected due to count loss and new regressions";
        if (category == "enshroud_near_arcane_hold_rejected_broad_candidate")
            return "broad Enshroud hold near Arcane was rejected previously; needs a narrower hypothesis before RPR.cs changes";
        if (category == "high_end_dd_uptime_review")
            return "DD uptime soft case requiring timeline review before any RPR.cs candidate";
        return "not an immediate RPR.cs change candidate";
    }

    private sealed record HighEndOpportunityCase(
        string Scenario,
        string Category,
        string Actionability,
        double Metric,
        double Time,
        string? SelectedGcd,
        string Ogcd,
        int RedGauge,
        int BlueGauge,
        double DeathsDesignLeft,
        double ArcaneCircleReadyIn,
        double ArcaneCircleLeft,
        double PerfectioParataLeft,
        string Reason,
        string Note);

    private static int OgcdIndex(ActionFrame frame, string action)
    {
        for (var i = 0; i < frame.SelectedOgcds.Count; ++i)
            if (frame.SelectedOgcds[i] == action)
                return i;
        return -1;
    }

    private static string FirstActionSummary(ScenarioResult result)
    {
        var actions = new[] { "ShadowOfDeath", "Potion", "SoulSlice", "ArcaneCircle", "Gluttony", "PlentifulHarvest", "Enshroud", "Communio", "Perfectio" };
        return string.Join(", ", actions.Select(action => $"{action}={FirstActionTime(result, action)}"));
    }

    private static string FirstRiskSummary(ScenarioResult result)
    {
        var risk = result.Frames.FirstOrDefault(f => f.ClipRisk || f.HardFails.Count > 0 || f.SelectedOgcds.Count > 1);
        if (risk == null)
            return FirstActionSummary(result);

        return $"t={risk.Time.ToString(CultureInfo.InvariantCulture)} gcd={risk.SelectedGcd ?? ""} ogcd={string.Join("|", risk.SelectedOgcds)} order={risk.OgcdOrderInGcd} clip={risk.ClipReason}";
    }

    private static string FirstActionTime(ScenarioResult result, string action)
    {
        foreach (var frame in result.Frames)
        {
            if (frame.SelectedGcd == action)
                return frame.Time.ToString(CultureInfo.InvariantCulture);
            if (frame.SelectedOgcds.Contains(action))
                return frame.Time.ToString(CultureInfo.InvariantCulture);
        }

        return "none";
    }

    private static void WriteCompare(string path, RegressionOutput output, RegressionOutput? baseline)
    {
        var compare = baseline == null
            ? new CompareResult(output.Summary.FailCount == 0 ? "ADOPTABLE" : "REJECTED", output.Summary.FailCount == 0 ? [] : ["HardFail > 0"], output.Summary.Regressions)
            : CompareScenarioResults(baseline.Scenarios, output.Scenarios);

        var sb = new StringBuilder();
        sb.AppendLine("# RPR regression compare");
        sb.AppendLine();
        sb.AppendLine($"- Verdict: {compare.Verdict}");
        sb.AppendLine($"- Scenarios: {output.Summary.ScenarioCount}");
        sb.AppendLine($"- Passed: {output.Summary.PassCount}");
        sb.AppendLine($"- Failed: {output.Summary.FailCount}");
        sb.AppendLine($"- HardFail total: {output.Summary.HardFailCountByRule.Values.Sum()}");
        sb.AppendLine($"- Baseline score: {output.Summary.BaselineScore.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Candidate score: {output.Summary.CandidateScore.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine();
        sb.AppendLine("## Rejection reasons");
        if (compare.RejectionReasons.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var reason in compare.RejectionReasons)
                sb.AppendLine($"- {reason}");
        sb.AppendLine();
        sb.AppendLine("## Regressions");
        if (compare.Regressions.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var regression in compare.Regressions)
                sb.AppendLine($"- {regression}");
        sb.AppendLine();
        sb.AppendLine("## HardFail by rule");
        if (output.Summary.HardFailCountByRule.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var kvp in output.Summary.HardFailCountByRule)
                sb.AppendLine($"- {kvp.Key}: {kvp.Value}");

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteOptimizerCandidatesCsv(string path, IReadOnlyList<OptimizerCandidateResult> rows)
    {
        var sb = new StringBuilder();
        sb.AppendCsv("candidate")
            .AppendCsv("parameter")
            .AppendCsv("baseline_value")
            .AppendCsv("candidate_value")
            .AppendCsv("verdict")
            .AppendCsv("hardfail_count")
            .AppendCsv("hardfail_delta")
            .AppendCsv("regression_count")
            .AppendCsv("score_delta")
            .AppendCsv("total_potency_delta")
            .AppendCsv("dd_uptime_delta")
            .AppendCsv("red_overcap_delta")
            .AppendCsv("blue_overcap_delta")
            .AppendCsv("gluttony_drift_delta")
            .AppendCsv("enshroud_count_delta")
            .AppendCsv("communio_count_delta")
            .AppendCsv("perfectio_count_delta")
            .AppendCsv("sacrificium_count_delta")
            .AppendCsv("lemure_count_delta")
            .AppendCsv("clip_risk_delta")
            .AppendCsv("combo_break_delta")
            .AppendCsv("reason", end: true);

        foreach (var row in rows)
        {
            sb.AppendCsv(row.Name)
                .AppendCsv(row.Parameter)
                .AppendCsv(row.BaselineValue)
                .AppendCsv(row.CandidateValue)
                .AppendCsv(row.Verdict)
                .AppendCsv(row.HardFailCount.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.HardFailDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RegressionCount.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.ScoreDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.TotalPotencyDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.DeathsDesignUptimeDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RedGaugeOvercapDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.BlueGaugeOvercapDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.GluttonyDriftDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.EnshroudCountDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.CommunioCountDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.PerfectioCountDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.SacrificiumCountDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.LemureCountDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.ClipRiskDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.ComboBreakDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.AdoptabilityReason, end: true);
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteOptimizerCompare(string path, IReadOnlyList<OptimizerCandidateResult> rows, OptimizerCandidateResult? best)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RPR optimizer compare");
        sb.AppendLine();
        sb.AppendLine($"- Candidates: {rows.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- ADOPTABLE: {rows.Count(r => r.Verdict == "ADOPTABLE").ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Best candidate: {(best == null ? "none" : best.Name)}");
        if (best != null)
        {
            sb.AppendLine($"- Best parameter: {best.Parameter}");
            sb.AppendLine($"- Best value: {best.BaselineValue} -> {best.CandidateValue}");
            sb.AppendLine($"- Best score delta: {best.ScoreDelta.ToString(CultureInfo.InvariantCulture)}");
        }
        sb.AppendLine();
        sb.AppendLine("## Candidates");
        foreach (var row in rows.OrderByDescending(r => r.Verdict == "ADOPTABLE").ThenByDescending(r => r.ScoreDelta))
        {
            sb.AppendLine($"- {row.Name}: {row.Verdict}, score_delta={row.ScoreDelta.ToString(CultureInfo.InvariantCulture)}, hardfail={row.HardFailCount.ToString(CultureInfo.InvariantCulture)} ({row.HardFailDelta.ToString(CultureInfo.InvariantCulture)} delta), regressions={row.RegressionCount.ToString(CultureInfo.InvariantCulture)}, {row.Parameter} {row.BaselineValue}->{row.CandidateValue}");
            sb.AppendLine($"  - deltas: potency={row.TotalPotencyDelta.ToString(CultureInfo.InvariantCulture)}, dd={row.DeathsDesignUptimeDelta.ToString(CultureInfo.InvariantCulture)}, red={row.RedGaugeOvercapDelta.ToString(CultureInfo.InvariantCulture)}, blue={row.BlueGaugeOvercapDelta.ToString(CultureInfo.InvariantCulture)}, drift={row.GluttonyDriftDelta.ToString(CultureInfo.InvariantCulture)}, enshroud={row.EnshroudCountDelta.ToString(CultureInfo.InvariantCulture)}, communio={row.CommunioCountDelta.ToString(CultureInfo.InvariantCulture)}, perfectio={row.PerfectioCountDelta.ToString(CultureInfo.InvariantCulture)}, clip={row.ClipRiskDelta.ToString(CultureInfo.InvariantCulture)}, combo_break={row.ComboBreakDelta.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"  - reason: {row.AdoptabilityReason}");
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteOfflinePlannerCandidatesCsv(string path, IReadOnlyList<OfflinePlannerCandidateResult> rows)
    {
        var sb = new StringBuilder();
        sb.AppendCsv("candidate")
            .AppendCsv("decision_domain")
            .AppendCsv("parameter")
            .AppendCsv("baseline_value")
            .AppendCsv("candidate_value")
            .AppendCsv("verdict")
            .AppendCsv("hardfail_count")
            .AppendCsv("regression_count")
            .AppendCsv("changed_decisions")
            .AppendCsv("total_potency_delta")
            .AppendCsv("dd_uptime_delta")
            .AppendCsv("gauge_loss_delta")
            .AppendCsv("red_overcap_delta")
            .AppendCsv("blue_overcap_delta")
            .AppendCsv("soul_slice_charge_loss_delta")
            .AppendCsv("gluttony_drift_delta")
            .AppendCsv("enshroud_count_delta")
            .AppendCsv("communio_count_delta")
            .AppendCsv("perfectio_count_delta")
            .AppendCsv("sacrificium_count_delta")
            .AppendCsv("lemure_count_delta")
            .AppendCsv("clip_risk_delta")
            .AppendCsv("reason", end: true);

        foreach (var row in rows)
        {
            sb.AppendCsv(row.Name)
                .AppendCsv(row.DecisionDomain)
                .AppendCsv(row.Parameter)
                .AppendCsv(row.BaselineValue)
                .AppendCsv(row.CandidateValue)
                .AppendCsv(row.Verdict)
                .AppendCsv(row.HardFailCount.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RegressionCount.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.ChangedDecisionCount.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.TotalPotencyDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.DeathsDesignUptimeDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.GaugeLossDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.RedGaugeOvercapDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.BlueGaugeOvercapDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.SoulSliceChargeLossDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.GluttonyDriftDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.EnshroudCountDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.CommunioCountDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.PerfectioCountDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.SacrificiumCountDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.LemureCountDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.ClipRiskDelta.ToString(CultureInfo.InvariantCulture))
                .AppendCsv(row.Reason, end: true);
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteOfflinePlannerCompare(string path, IReadOnlyList<OfflinePlannerCandidateResult> rows, IReadOnlyList<OfflinePlannerCandidateResult> proven)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RPR offline planner compare");
        sb.AppendLine();
        sb.AppendLine($"- Candidates: {rows.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- PROVEN: {proven.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine("- PROVEN requires HardFail 0, regressions none, total potency improvement, DD uptime improvement, and aggregate gauge-loss improvement.");
        sb.AppendLine();
        sb.AppendLine("## Proven local decisions");
        if (proven.Count == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            foreach (var row in proven)
                sb.AppendLine($"- {row.Name}: {row.DecisionDomain}, potency={row.TotalPotencyDelta.ToString(CultureInfo.InvariantCulture)}, DD={row.DeathsDesignUptimeDelta.ToString(CultureInfo.InvariantCulture)}, gauge_loss={row.GaugeLossDelta.ToString(CultureInfo.InvariantCulture)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Candidate rows");
        foreach (var row in rows.OrderByDescending(r => r.Verdict == "PROVEN").ThenByDescending(r => r.TotalPotencyDelta))
        {
            sb.AppendLine($"- {row.Name}: {row.Verdict}, domain={row.DecisionDomain}, {row.Parameter} {row.BaselineValue}->{row.CandidateValue}");
            sb.AppendLine($"  - hardfail={row.HardFailCount.ToString(CultureInfo.InvariantCulture)}, regressions={row.RegressionCount.ToString(CultureInfo.InvariantCulture)}, changed={row.ChangedDecisionCount.ToString(CultureInfo.InvariantCulture)}");
            sb.AppendLine($"  - potency={row.TotalPotencyDelta.ToString(CultureInfo.InvariantCulture)}, DD={row.DeathsDesignUptimeDelta.ToString(CultureInfo.InvariantCulture)}, gauge_loss={row.GaugeLossDelta.ToString(CultureInfo.InvariantCulture)} (red={row.RedGaugeOvercapDelta.ToString(CultureInfo.InvariantCulture)}, blue={row.BlueGaugeOvercapDelta.ToString(CultureInfo.InvariantCulture)}, soul_slice={row.SoulSliceChargeLossDelta.ToString(CultureInfo.InvariantCulture)})");
            sb.AppendLine($"  - reason: {row.Reason}");
            foreach (var example in row.DecisionExamples.Take(3))
                sb.AppendLine($"  - example: {example}");
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteOfflinePlannerBeam(string path, IReadOnlyList<OfflinePlannerBeamNode> rows, IReadOnlyList<OfflinePlannerBeamNode> proven)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RPR offline planner beam search");
        sb.AppendLine();
        sb.AppendLine($"- Beam width: {OfflinePlannerBeamWidth.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Evaluated combinations: {rows.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Proven combinations: {proven.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine("- Unsafe branches are pruned before the next decision domain.");
        sb.AppendLine();
        sb.AppendLine("## Proven combinations");
        if (proven.Count == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            foreach (var candidate in proven)
            {
                sb.AppendLine($"- {candidate.Profile.Name}: {string.Join(" + ", candidate.Decisions)}");
                sb.AppendLine($"  - potency={candidate.Result.TotalPotencyDelta.ToString(CultureInfo.InvariantCulture)}, DD={candidate.Result.DeathsDesignUptimeDelta.ToString(CultureInfo.InvariantCulture)}, gauge_loss={candidate.Result.GaugeLossDelta.ToString(CultureInfo.InvariantCulture)}, changed={candidate.Result.ChangedDecisionCount.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Highest-ranked safe combinations");
        foreach (var candidate in rows.Where(candidate => candidate.Result.Verdict != "REJECTED").Take(20))
        {
            sb.AppendLine($"- {candidate.Result.Verdict}: {string.Join(" + ", candidate.Decisions)}");
            sb.AppendLine($"  - score={PlannerBeamScore(candidate.Result).ToString(CultureInfo.InvariantCulture)}, potency={candidate.Result.TotalPotencyDelta.ToString(CultureInfo.InvariantCulture)}, DD={candidate.Result.DeathsDesignUptimeDelta.ToString(CultureInfo.InvariantCulture)}, gauge_loss={candidate.Result.GaugeLossDelta.ToString(CultureInfo.InvariantCulture)}");
        }

        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));

    private static T ReadJson<T>(string path)
        => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"Could not parse {path}");

    private static string Sanitize(string name)
        => string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));

    private sealed record CliOptions(string? Job, bool All, bool Real, bool Plan, bool Optimize, bool Exhaustive, int Patterns, int Seed, bool RealFullOutput, string? Scenario, string? Candidate, string? Out, string? Baseline)
    {
        public static CliOptions Parse(string[] args)
        {
            if (args.Length > 0 && args[0].Equals("emulate", StringComparison.OrdinalIgnoreCase))
                args = args[1..];

            string? job = "RPR";
            string? scenario = null;
            string? candidate = null;
            string? output = null;
            string? baseline = null;
            var all = false;
            var real = false;
            var plan = false;
            var optimize = false;
            var exhaustive = false;
            var patterns = 20000;
            var seed = 75100;
            var realFullOutput = false;

            for (var i = 0; i < args.Length; ++i)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--job":
                        job = RequireValue(args, ref i, arg);
                        break;
                    case "--all":
                        all = true;
                        break;
                    case "--real":
                        real = true;
                        break;
                    case "--plan":
                        plan = true;
                        break;
                    case "--optimize":
                        optimize = true;
                        break;
                    case "--exhaustive":
                        exhaustive = true;
                        optimize = true;
                        break;
                    case "--patterns":
                        patterns = int.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture);
                        break;
                    case "--seed":
                        seed = int.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture);
                        break;
                    case "--real-full-output":
                        realFullOutput = true;
                        break;
                    case "--scenario":
                        scenario = RequireValue(args, ref i, arg);
                        break;
                    case "--candidate":
                        candidate = RequireValue(args, ref i, arg);
                        break;
                    case "--out":
                        output = RequireValue(args, ref i, arg);
                        break;
                    case "--baseline":
                        baseline = RequireValue(args, ref i, arg);
                        break;
                    default:
                        throw new ArgumentException($"Unknown argument '{arg}'.");
                }
            }

            if (!string.Equals(job, "RPR", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only --job RPR is supported.");

            if (patterns <= 0)
                throw new ArgumentException("--patterns must be positive.");

            return new(job, all, real, plan, optimize, exhaustive, patterns, seed, realFullOutput, scenario, candidate, output, baseline);
        }

        private static string RequireValue(string[] args, ref int index, string option)
        {
            if (index + 1 >= args.Length)
                throw new ArgumentException($"Missing value for {option}.");
            return args[++index];
        }
    }

    private sealed record WeaveBucketSummary(
        int TripleWeaveCandidates,
        int BurstWeaveOverloadCandidates,
        int HighPingDoubleWeaveSoftcheckCount,
        int ArcaneCircleBeforeGluttonyFailures,
        int ArcaneCircleBeforeSacrificiumFailures,
        int PotionAfterArcaneCircleFailures,
        int PotionStandaloneFailures,
        int PotionOffFailures,
        int PotionEvenExceptOpenerFailures,
        IReadOnlyList<WeaveScenarioIssue> OpenerIssues,
        IReadOnlyList<WeaveScenarioIssue> RprChangeCandidates);

    private sealed record WeaveScenarioIssue(string Scenario, string HardFails, string Summary);
}

public static class CsvExtensions
{
    public static StringBuilder AppendCsv(this StringBuilder sb, string value, bool end = false)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            value = "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        sb.Append(value);
        sb.Append(end ? Environment.NewLine : ',');
        return sb;
    }
}
