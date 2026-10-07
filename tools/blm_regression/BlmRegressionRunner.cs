using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlmRegression;

public sealed class BlmRegressionRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public BlmRotationEmulator Emulator { get; init; } = new();

    public BlmRegressionOutput Run(IReadOnlyList<BlmScenario> scenarios)
    {
        var emulator = Emulator;
        var results = scenarios.Select(emulator.Run).ToList();
        return BuildOutput(results, emulator);
    }

    public void WriteOutputs(string outDir, BlmRegressionOutput output)
    {
        Directory.CreateDirectory(outDir);
        WriteJson(Path.Combine(outDir, "blm_regression_latest.json"), output);
        WriteJson(Path.Combine(outDir, "blm_regression_repros.json"), output.Reproductions);
        File.WriteAllText(Path.Combine(outDir, "blm_regression_summary.md"), BuildMarkdown(output));
    }

    private static BlmRegressionOutput BuildOutput(IReadOnlyList<BlmScenarioResult> scenarios, BlmRotationEmulator emulator)
    {
        var hardFailCounts = scenarios
            .SelectMany(s => s.HardFails)
            .GroupBy(f => f)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Count());
        var firstHardFails = scenarios
            .Where(s => s.HardFails.Count > 0)
            .SelectMany(s => s.HardFails.Take(3).Select(f => $"{s.Scenario.Name}: {f}"))
            .Take(20)
            .ToList();

        return new BlmRegressionOutput
        {
            Summary = new BlmSummaryResult
            {
                ScenarioCount = scenarios.Count,
                PassCount = scenarios.Count(s => s.Passed),
                FailCount = scenarios.Count(s => !s.Passed),
                HardFailCount = scenarios.Sum(s => s.HardFails.Count),
                RawSoftSignalCount = scenarios.Sum(s => s.Metrics.RawSoftSignalCount),
                RawSoftRegressionCount = scenarios.Sum(s => s.Metrics.RawSoftRegressionCount),
                SoftRegressionCount = scenarios.Sum(s => s.Metrics.SoftRegressionCount),
                CoverageGapCount = scenarios.Sum(s => s.Metrics.CoverageGapCount),
                HardFailCountByRule = hardFailCounts,
                FirstHardFails = firstHardFails
            },
            Scenarios = scenarios.ToList(),
            Reproductions = BuildFailureReproductions(scenarios, emulator)
        };
    }

    private static List<BlmFailureReproduction> BuildFailureReproductions(IReadOnlyList<BlmScenarioResult> results, BlmRotationEmulator emulator)
    {
        var reproductions = new List<BlmFailureReproduction>();
        foreach (var result in results.Where(r => r.HardFails.Count > 0))
        {
            foreach (var rule in result.HardFails.Distinct())
            {
                var scenario = MinimizeScenarioForRule(result.Scenario, rule, emulator);
                var minimized = emulator.Run(scenario);
                reproductions.Add(new()
                {
                    SourceScenarioName = result.Scenario.Name,
                    Rule = rule,
                    Scenario = scenario,
                    FirstFailureTrace = RepresentativeTrace(minimized, frame => frame.HardFails.Contains(rule))
                });
            }
        }

        return reproductions;
    }

    private static BlmScenario MinimizeScenarioForRule(BlmScenario scenario, BlmHardFailRule rule, BlmRotationEmulator emulator)
    {
        var events = scenario.Events.ToList();
        for (var index = 0; index < events.Count;)
        {
            var candidateEvents = events.Where((_, i) => i != index).ToArray();
            var candidate = scenario with { Events = candidateEvents };
            if (CausesRule(candidate, rule, emulator))
            {
                events.RemoveAt(index);
                continue;
            }

            ++index;
        }

        var minimized = scenario with { Events = events };
        var firstFailure = emulator.Run(minimized).Frames.FirstOrDefault(frame => frame.HardFails.Contains(rule));
        if (firstFailure == null)
            return minimized;

        var shortened = minimized with { Duration = Math.Max(BlmConstants.Gcd, firstFailure.Time + BlmConstants.Gcd) };
        return CausesRule(shortened, rule, emulator) ? shortened : minimized;
    }

    private static bool CausesRule(BlmScenario scenario, BlmHardFailRule rule, BlmRotationEmulator emulator)
        => emulator.Run(scenario).HardFails.Contains(rule);

    private static string BuildMarkdown(BlmRegressionOutput output)
    {
        var softFindings = BuildSoftFindings(output);
        var sb = new StringBuilder();
        sb.AppendLine("# BLM regression summary");
        sb.AppendLine();
        sb.AppendLine($"- Generated: {output.Summary.GeneratedAt:O}");
        sb.AppendLine($"- Scenarios: {output.Summary.ScenarioCount}");
        sb.AppendLine($"- Passed: {output.Summary.PassCount}");
        sb.AppendLine($"- Failed: {output.Summary.FailCount}");
        sb.AppendLine($"- HardFail total: {output.Summary.HardFailCount}");
        sb.AppendLine($"- Raw Soft signal total: {output.Summary.RawSoftSignalCount}");
        sb.AppendLine($"- Raw Soft regression total: {output.Summary.RawSoftRegressionCount}");
        sb.AppendLine($"- Risk-adjusted Soft regression total: {output.Summary.SoftRegressionCount}");
        sb.AppendLine($"- Coverage gap total: {output.Summary.CoverageGapCount}");
        sb.AppendLine($"- Auto-minimized hard-fail reproductions: {output.Reproductions.Count}");
        sb.AppendLine($"- Raw Soft signal thresholds: Thunder blank > {BlmConstants.Gcd * 2:0.###}s, Polyglot max hold > {BlmConstants.Gcd * 4:0.###}s.");
        sb.AppendLine("- Phantom actions: lightweight routing checks enabled for disabled/enabled/target-lost/movement coverage.");
        AppendCoverageSummary(sb, output);
        AppendSoftClassification(sb, output, softFindings);
        sb.AppendLine();
        sb.AppendLine("## HardFail by rule");
        if (output.Summary.HardFailCountByRule.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var (rule, count) in output.Summary.HardFailCountByRule)
                sb.AppendLine($"- {rule}: {count}");

        sb.AppendLine();
        sb.AppendLine("## First HardFail 20");
        if (output.Summary.FirstHardFails.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var fail in output.Summary.FirstHardFails)
                sb.AppendLine($"- {fail}");

        sb.AppendLine();
        sb.AppendLine("## Auto-minimized hard-fail reproductions");
        if (output.Reproductions.Count == 0)
        {
            sb.AppendLine("- none");
        }
        else
        {
            foreach (var reproduction in output.Reproductions.Take(20))
                sb.AppendLine($"- {reproduction.SourceScenarioName}: {reproduction.Rule}; trace `{reproduction.FirstFailureTrace}`; events={reproduction.Scenario.Events.Count}; duration={reproduction.Scenario.Duration.ToString("0.###", CultureInfo.InvariantCulture)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Scenario summary");
        sb.AppendLine("| Scenario | Result | HardFail | Raw Signal | Raw Soft | Risk Soft | Coverage | GCDs | Raw thunder blank | Actionable thunder blank | Raw poly hold | Wasted grants |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var result in output.Scenarios.OrderBy(s => s.Scenario.Name, StringComparer.Ordinal))
        {
            var m = result.Metrics;
            sb.Append('|').Append(Escape(result.Scenario.Name))
                .Append('|').Append(result.Passed ? "PASS" : "FAIL")
                .Append('|').Append(result.HardFails.Count.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.RawSoftSignalCount.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.RawSoftRegressionCount.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.SoftRegressionCount.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.CoverageGapCount.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.GcdCount.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.RawThunderBlankSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append('|').Append(m.ThunderBlankSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append('|').Append(m.RawPolyglotMaxHoldSeconds.ToString("0.###", CultureInfo.InvariantCulture))
                .Append('|').Append(m.PolyglotWastedGrantCount.ToString(CultureInfo.InvariantCulture))
                .AppendLine("|");
        }

        sb.AppendLine();
        sb.AppendLine("## Action trace excerpts");
        foreach (var result in output.Scenarios.Where(s => s.HardFails.Count > 0).Take(5))
        {
            sb.AppendLine($"### {result.Scenario.Name}");
            foreach (var frame in result.Frames.Take(20))
                sb.AppendLine($"- t={frame.Time.ToString("0.###", CultureInfo.InvariantCulture)} gcd={frame.SelectedGcd} ogcd={string.Join("|", frame.SelectedOgcds)} elem={frame.Element} mp={frame.MP} poly={frame.Polyglot} soul={frame.AstralSoul} reason={frame.Reason}");
        }

        if (!output.Scenarios.Any(s => s.HardFails.Count > 0))
            sb.AppendLine("- no hardfail traces");

        return sb.ToString();
    }

    private static void AppendCoverageSummary(StringBuilder sb, BlmRegressionOutput output)
    {
        sb.AppendLine();
        sb.AppendLine("## Coverage summary");
        sb.AppendLine("| Category | Scenarios |");
        sb.AppendLine("|---|---:|");
        foreach (var group in output.Scenarios
            .GroupBy(s => s.Scenario.Category)
            .OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            sb.Append('|').Append(group.Key)
                .Append('|').Append(group.Count().ToString(CultureInfo.InvariantCulture))
                .AppendLine("|");
        }

        var matrix = output.Scenarios.Where(s => s.Scenario.Category == BlmScenarioCategory.CombatMatrix).ToList();
        if (matrix.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("## Combat matrix coverage");
        sb.AppendLine($"- Matrix scenarios: {matrix.Count.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"- Levels: {string.Join(", ", matrix.Select(s => s.Scenario.Level).Distinct().Order())}");
        sb.AppendLine($"- Initial target counts: {string.Join(", ", matrix.Select(s => s.Scenario.InitialTargets).Distinct().Order())}");
        sb.AppendLine($"- Rotations: {string.Join(", ", matrix.Select(s => s.Scenario.Rotation).Distinct().OrderBy(r => r.ToString()))}");
        sb.AppendLine();
        sb.AppendLine("| Level | Targets | Scenarios | HardFail | Risk Soft |");
        sb.AppendLine("|---:|---:|---:|---:|---:|");
        foreach (var group in matrix
            .GroupBy(s => new { s.Scenario.Level, s.Scenario.InitialTargets })
            .OrderBy(g => g.Key.Level)
            .ThenBy(g => g.Key.InitialTargets))
        {
            sb.Append('|').Append(group.Key.Level.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(group.Key.InitialTargets.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(group.Count().ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(group.Sum(s => s.HardFails.Count).ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(group.Sum(s => s.Metrics.SoftRegressionCount).ToString(CultureInfo.InvariantCulture))
                .AppendLine("|");
        }
    }

    private static List<SoftRegressionFinding> BuildSoftFindings(BlmRegressionOutput output)
    {
        var findings = new List<SoftRegressionFinding>();
        foreach (var result in output.Scenarios)
        {
            var scenario = result.Scenario;
            var metrics = result.Metrics;
            if (metrics.HasRawThunderSignal || metrics.ThunderBlankSeconds > 0)
                findings.Add(ClassifyThunderBlank(result));
            if (metrics.HasRawPolyglotSignal || metrics.PolyglotWastedGrantCount > 0)
                findings.Add(ClassifyPolyglotOvercap(result));
            if (metrics.CoverageGapCount > 0)
                findings.Add(new(scenario.Name, ScenarioPattern(scenario), "CoverageGap", SoftRegressionClassification.CoverageGap, metrics.CoverageGapCount, RepresentativeTrace(result, _ => true), "The lightweight Phantom/duty model could not verify all expected routing checks for this scenario; keep it separate from BLM.cs issue candidates."));
            if (metrics.ElementDropCount > 0)
                findings.Add(new(scenario.Name, ScenarioPattern(scenario), "ElementDrop", SoftRegressionClassification.BlmIssueCandidate, metrics.ElementDropCount, RepresentativeTrace(result, f => f.Element == 0), "AF/UI expired while a target was available; this is a live rotation break candidate."));
            if (metrics.ForcedMovementHardcastAttempts > 0)
                findings.Add(new(scenario.Name, ScenarioPattern(scenario), "ForcedMovementHardcast", SoftRegressionClassification.BlmIssueCandidate, metrics.ForcedMovementHardcastAttempts, RepresentativeTrace(result, f => f.ForcedMoveNow), "The model attempted hardcasts during forced movement; this should be checked against movement instant gates."));
            if (metrics.Standard57OrderViolations > 0)
                findings.Add(new(scenario.Name, ScenarioPattern(scenario), "Standard57Order", SoftRegressionClassification.BlmIssueCandidate, metrics.Standard57OrderViolations, RepresentativeTrace(result, _ => true), "Standard57 opener order diverged from the expected Manafont / Xeno / Flare Star chain."));
            if (metrics.ModeHandoffGcdStops > 0)
                findings.Add(new(scenario.Name, ScenarioPattern(scenario), "ModeHandoffStop", SoftRegressionClassification.BlmIssueCandidate, metrics.ModeHandoffGcdStops, RepresentativeTrace(result, _ => true), "Mode handoff produced a GCD stop window; this is a handoff recovery candidate."));
        }

        return findings;
    }

    private static SoftRegressionFinding ClassifyThunderBlank(BlmScenarioResult result)
    {
        var scenario = result.Scenario;
        var metrics = result.Metrics;
        if (scenario.Thunder == BlmThunderStrategy.Delay)
        {
            return new(
                scenario.Name,
                ScenarioPattern(scenario),
                "ThunderBlank",
                SoftRegressionClassification.ScenarioConfiguration,
                1,
                RepresentativeTrace(result, f => f.TargetAvailable && (UsesAoeThunder(f) ? f.AoeThunderLeft <= 0 : f.ThunderLeft <= 0)),
                "ThunderStrategy.Delay intentionally disables maintenance, but the generic blank-DoT soft counter still reports it.");
        }

        if (scenario.Thunder == BlmThunderStrategy.InstantOnly)
        {
            return new(
                scenario.Name,
                ScenarioPattern(scenario),
                "ThunderBlank",
                SoftRegressionClassification.ScenarioConfiguration,
                1,
                RepresentativeTrace(result, f => f.TargetAvailable && (UsesAoeThunder(f) ? f.AoeThunderLeft <= 0 : f.ThunderLeft <= 0)),
                "ThunderStrategy.InstantOnly is movement-only in this scenario; DoT maintenance is not expected.");
        }

        if (scenario.Rotation == BlmRotationStrategy.PolyglotOvercapOnly)
        {
            return new(
                scenario.Name,
                ScenarioPattern(scenario),
                "ThunderBlank",
                SoftRegressionClassification.ScenarioConfiguration,
                1,
                RepresentativeTrace(result, f => f.TargetAvailable && (UsesAoeThunder(f) ? f.AoeThunderLeft <= 0 : f.ThunderLeft <= 0)),
                "PolyglotOvercapOnly intentionally restricts GCD automation; Thunder maintenance is not expected from this mode.");
        }

        if (metrics.ThunderBlankSeconds > 0)
        {
            return new(
                scenario.Name,
                ScenarioPattern(scenario),
                "ThunderBlank",
                SoftRegressionClassification.BlmIssueCandidate,
                1,
                RepresentativeTrace(result, f => f.TargetAvailable && f.Thunderhead && (UsesAoeThunder(f) ? f.AoeThunderLeft <= 0 : f.ThunderLeft <= 0)),
                "Thunderhead was available with a target and no movement/downtime block, but DoT remained blank for more than two GCDs.");
        }

        return new(
            scenario.Name,
            ScenarioPattern(scenario),
            "ThunderBlank",
            SoftRegressionClassification.StrictThreshold,
            1,
            RepresentativeTrace(result, f => f.TargetAvailable && (UsesAoeThunder(f) ? f.AoeThunderLeft <= 0 : f.ThunderLeft <= 0)),
            "The raw blank-DoT counter includes periods without Thunderhead and target-count transitions. No actionable blank remained while Thunderhead, target, and movement conditions allowed a refresh.");
    }

    private static bool UsesAoeThunder(BlmActionFrame frame)
        => frame.Targets >= 2 && frame.Level >= 26;

    private static SoftRegressionFinding ClassifyPolyglotOvercap(BlmScenarioResult result)
    {
        var scenario = result.Scenario;
        var metrics = result.Metrics;
        if (metrics.PolyglotWastedGrantCount > 0)
        {
            return new(
                scenario.Name,
                ScenarioPattern(scenario),
                "PolyglotWastedGrant",
                SoftRegressionClassification.BlmIssueCandidate,
                metrics.PolyglotWastedGrantCount,
                RepresentativeTrace(result, f => f.Polyglot >= MaxPolyglotForLevel(f.Level) && f.NextPolyglot <= BlmConstants.Gcd),
                "A Polyglot grant was wasted while the target was available and no downtime/movement hold was active.");
        }

        if (scenario.Level < 70)
        {
            return new(
                scenario.Name,
                ScenarioPattern(scenario),
                "PolyglotOvercap",
                SoftRegressionClassification.ScenarioConfiguration,
                1,
                RepresentativeTrace(result, _ => true),
                "Polyglot is unavailable below level 70, but the generic capped-resource soft counter is still active for this low-level scenario.");
        }

        return new(
            scenario.Name,
            ScenarioPattern(scenario),
            "PolyglotMaxHold",
            SoftRegressionClassification.StrictThreshold,
            1,
            RepresentativeTrace(result, f => f.Polyglot >= MaxPolyglotForLevel(f.Level)),
            "Polyglot was held at max, but no grant was observed as wasted. This remains raw risk context only, because live BLM can reserve Polyglot for raid buffs, movement, or target return.");
    }

    private static void AppendSoftClassification(StringBuilder sb, BlmRegressionOutput output, IReadOnlyList<SoftRegressionFinding> findings)
    {
        sb.AppendLine();
        sb.AppendLine("## Soft signal classification");
        if (findings.Count == 0)
        {
            sb.AppendLine("- none");
            return;
        }

        sb.AppendLine("| Classification | Count | Notes |");
        sb.AppendLine("|---|---:|---|");
        foreach (var group in findings.GroupBy(f => f.Classification).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
            sb.AppendLine($"|{group.Key}|{group.Sum(f => f.Count).ToString(CultureInfo.InvariantCulture)}|{ClassificationNote(group.Key)}|");

        sb.AppendLine();
        sb.AppendLine("## Soft signal by scenario");
        sb.AppendLine("| Scenario | Pattern | Type | Class | Count | Trace | Reason |");
        sb.AppendLine("|---|---|---|---|---:|---|---|");
        foreach (var finding in findings
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.ScenarioName, StringComparer.Ordinal)
            .ThenBy(f => f.RegressionType, StringComparer.Ordinal)
            .Take(80))
        {
            sb.Append('|').Append(Escape(finding.ScenarioName))
                .Append('|').Append(Escape(finding.Pattern))
                .Append('|').Append(Escape(finding.RegressionType))
                .Append('|').Append(finding.Classification)
                .Append('|').Append(finding.Count.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(Escape(finding.RepresentativeTrace))
                .Append('|').Append(Escape(finding.Reason))
                .AppendLine("|");
        }

        AppendFindingList(sb, "## Real issue candidates", findings.Where(f => f.Classification == SoftRegressionClassification.BlmIssueCandidate));
        AppendFindingList(sb, "## Emulator-only or threshold candidates", findings.Where(f => f.Classification is SoftRegressionClassification.EmulatorApproximation or SoftRegressionClassification.StrictThreshold or SoftRegressionClassification.ScenarioConfiguration));
        AppendFindingList(sb, "## Coverage gaps", findings.Where(f => f.Classification == SoftRegressionClassification.CoverageGap));

        sb.AppendLine();
        sb.AppendLine("## Likely BLM.cs changes");
        var realCandidates = findings.Where(f => f.Classification == SoftRegressionClassification.BlmIssueCandidate).ToList();
        if (realCandidates.Count == 0)
        {
            sb.AppendLine($"- none from the current risk-adjusted soft findings. Raw soft findings are retained for emulator/threshold context, but none currently requires a BLM.cs change.");
        }
        else
        {
            foreach (var finding in realCandidates)
                sb.AppendLine($"- {finding.ScenarioName}: {finding.RegressionType} ({finding.Reason})");
        }

        sb.AppendLine();
        sb.AppendLine("## Phantom action impact");
        AppendPhantomCoverage(sb, output);
    }

    private static void AppendPhantomCoverage(StringBuilder sb, BlmRegressionOutput output)
    {
        var phantomScenarios = output.Scenarios
            .Where(s => s.Scenario.RequiresPhantomCoverage)
            .OrderBy(s => s.Scenario.Name, StringComparer.Ordinal)
            .ToList();
        if (phantomScenarios.Count == 0)
        {
            sb.AppendLine("- none");
            return;
        }

        sb.AppendLine("| Scenario | Enabled | Phantom GCDs | Movement fallback | Target-lost suppressions | Return GCDs | Null guard checks | CoverageGap |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var result in phantomScenarios)
        {
            var m = result.Metrics;
            sb.Append('|').Append(Escape(result.Scenario.Name))
                .Append('|').Append(result.Scenario.PhantomActionsEnabled ? "true" : "false")
                .Append('|').Append(m.PhantomActionsUsed.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.PhantomMovementFallbacks.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.PhantomTargetLostSuppressions.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.PhantomReturnGcds.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.PhantomNullGuardChecks.ToString(CultureInfo.InvariantCulture))
                .Append('|').Append(m.CoverageGapCount.ToString(CultureInfo.InvariantCulture))
                .AppendLine("|");
        }

        if (phantomScenarios.All(s => s.Metrics.CoverageGapCount == 0))
            sb.AppendLine("- All current Phantom/duty representative scenarios are covered by the lightweight emulator checks.");
        else
            sb.AppendLine("- Remaining CoverageGap rows are retained separately from BLM.cs issue candidates.");
    }

    private static void AppendFindingList(StringBuilder sb, string title, IEnumerable<SoftRegressionFinding> findings)
    {
        sb.AppendLine();
        sb.AppendLine(title);
        var list = findings.Take(30).ToList();
        if (list.Count == 0)
        {
            sb.AppendLine("- none");
            return;
        }

        foreach (var finding in list)
            sb.AppendLine($"- {finding.ScenarioName}: {finding.RegressionType}, trace `{finding.RepresentativeTrace}`; {finding.Reason}");
    }

    private static string ClassificationNote(SoftRegressionClassification classification)
        => classification switch
        {
            SoftRegressionClassification.EmulatorApproximation => "The model is missing a live BLM mechanism or intentionally simplified resource generation.",
            SoftRegressionClassification.StrictThreshold => "The soft expectation is too broad and should be refined before treating it as a code bug.",
            SoftRegressionClassification.BlmIssueCandidate => "Potential live BLM.cs problem; inspect live helper chain before changing code.",
            SoftRegressionClassification.ScenarioConfiguration => "The scenario strategy or level makes the generic soft counter expected/noisy.",
            SoftRegressionClassification.CoverageGap => "Remaining coverage gap not represented precisely enough by the lightweight emulator.",
            _ => ""
        };

    private static string ScenarioPattern(BlmScenario scenario)
    {
        var name = scenario.Name;
        var suffix = "_" + scenario.Rotation;
        return name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : name;
    }

    private static string RepresentativeTrace(BlmScenarioResult result, Func<BlmActionFrame, bool> predicate)
    {
        var frame = result.Frames.FirstOrDefault(predicate) ?? result.Frames.FirstOrDefault();
        if (frame == null)
            return "none";

        return string.Create(CultureInfo.InvariantCulture, $"t={frame.Time:0.###} gcd={frame.SelectedGcd} ogcd={string.Join('+', frame.SelectedOgcds)} elem={frame.Element} mp={frame.MP} poly={frame.Polyglot} next={frame.NextPolyglot:0.###} soul={frame.AstralSoul} th={frame.ThunderLeft:0.###} aoth={frame.AoeThunderLeft:0.###} reason={frame.Reason}");
    }

    private static int MaxPolyglotForLevel(int level)
        => level >= 98 ? 3 : level >= 80 ? 2 : level >= 70 ? 1 : 0;

    private static string Escape(string value)
        => value.Replace("|", "\\|", StringComparison.Ordinal);

    private static void WriteJson<T>(string path, T value)
        => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));

    private enum SoftRegressionClassification
    {
        EmulatorApproximation,
        StrictThreshold,
        BlmIssueCandidate,
        ScenarioConfiguration,
        CoverageGap
    }

    private sealed record SoftRegressionFinding(
        string ScenarioName,
        string Pattern,
        string RegressionType,
        SoftRegressionClassification Classification,
        int Count,
        string RepresentativeTrace,
        string Reason);
}
