using System.Text;

namespace MnkRegression;

public static class ResultComparer
{
    private const double ScoreRegressionThreshold = 0.01;

    public static CompareReport Compare(RegressionResult? baseline, RegressionResult current)
    {
        var hardFailScenarios = current.Scenarios
            .Where(s => s.HardFails.Count > 0)
            .Select(s => $"{s.ScenarioName}: {string.Join(", ", s.HardFails)}")
            .ToList();

        List<string> worsened = [];
        List<string> improved = [];
        if (baseline != null)
        {
            var baselineByName = baseline.Scenarios.ToDictionary(s => s.ScenarioName, StringComparer.OrdinalIgnoreCase);
            foreach (var scenario in current.Scenarios)
            {
                if (!baselineByName.TryGetValue(scenario.ScenarioName, out var baseScenario))
                    continue;

                if (scenario.HardFails.Count > baseScenario.HardFails.Count || scenario.Score < baseScenario.Score - ScoreRegressionThreshold)
                    worsened.Add($"{scenario.ScenarioName}: score {baseScenario.Score:0.###} -> {scenario.Score:0.###}, hardfails {baseScenario.HardFails.Count} -> {scenario.HardFails.Count}");
                else if (scenario.HardFails.Count < baseScenario.HardFails.Count || scenario.Score > baseScenario.Score + ScoreRegressionThreshold)
                    improved.Add($"{scenario.ScenarioName}: score {baseScenario.Score:0.###} -> {scenario.Score:0.###}, hardfails {baseScenario.HardFails.Count} -> {scenario.HardFails.Count}");
            }
        }

        List<string> rejectionReasons = [];
        if (hardFailScenarios.Count > 0)
            rejectionReasons.Add($"HardFail {hardFailScenarios.Count} scenario(s)");
        if (current.Scenarios.Any(s => s.Category == ScenarioCategory.DancingMad && s.HardFails.Count > 0))
            rejectionReasons.Add("Dancing Mad P1->P2 scenario has HardFail");
        if (current.Scenarios.Any(s => s.Category == ScenarioCategory.LookAway && s.HardFails.Count > 0))
            rejectionReasons.Add("LookAway scenario has HardFail");
        if (current.Scenarios.Any(s => s.Category == ScenarioCategory.RiddleOfEarth && s.HardFails.Count > 0))
            rejectionReasons.Add("Riddle of Earth scenario has HardFail");
        if (baseline != null && HasMajorRegression(baseline, current))
            rejectionReasons.Add("Baseline comparison found major regression");
        if (baseline != null && EvenPb2SuccessRate(current) + 0.0001 < EvenPb2SuccessRate(baseline))
            rejectionReasons.Add("Even PB2 success rate is lower than baseline");
        if (baseline != null && EvenSyncPrecision(current) > EvenSyncPrecision(baseline) + 0.0001)
            rejectionReasons.Add("Even RoF/BH sync precision is lower than baseline");
        if (baseline != null && PhantomRushEvenSuccessRate(current) + 0.0001 < PhantomRushEvenSuccessRate(baseline))
            rejectionReasons.Add("Phantom Rush even burst success rate is lower than baseline");
        if (baseline != null && GcdStopTime(current) > GcdStopTime(baseline) + 0.0001)
            rejectionReasons.Add("GCD stop time increased from baseline");

        var verdict = rejectionReasons.Count == 0 ? "ADOPTABLE" : "REJECTED";
        return new(verdict, rejectionReasons, hardFailScenarios, worsened, improved);
    }

    public static string BuildReport(RegressionResult current, RegressionResult? baseline, CompareReport compare)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# MNK Regression Report");
        sb.AppendLine();
        sb.AppendLine($"Generated: {current.GeneratedAt:O}");
        sb.AppendLine($"Current MNK hash: `{current.MnkSourceHash}`");
        if (baseline != null)
            sb.AppendLine($"Baseline MNK hash: `{baseline.MnkSourceHash}`");
        sb.AppendLine();
        sb.AppendLine($"Total scenarios: {current.Scenarios.Count}");
        sb.AppendLine($"HardFail scenarios: {compare.HardFailScenarios.Count}");
        sb.AppendLine();

        AppendList(sb, "HardFail Scenarios", compare.HardFailScenarios);
        AppendList(sb, "Worsened Scenarios", compare.WorsenedScenarios);
        AppendList(sb, "Improved Scenarios", compare.ImprovedScenarios);

        AppendCoverageSummary(sb, current);

        sb.AppendLine("## Scenario Summary");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Category | Level | Rotation | PB | Blitz | RoF | BH | TC | Score | HardFails | RoF | BH | PB | Potion | PhantomRush | GCD stop |");
        sb.AppendLine("|---|---:|---:|---|---|---|---|---|---|---:|---:|---|---|---|---|---|---:|");
        foreach (var scenario in current.Scenarios.OrderBy(s => s.Category).ThenBy(s => s.ScenarioName))
        {
            var gcdStop = scenario.Metrics.GetValueOrDefault("GcdStopTime");
            sb.AppendLine($"| {scenario.ScenarioName} | {scenario.Category} | {scenario.LevelCap} | {scenario.RotationMode} | {scenario.PBStrategy} | {scenario.BlitzStrategy} | {scenario.RoFStrategy} | {scenario.BrotherhoodStrategy} | {scenario.ThunderclapStrategy} | {scenario.Score:0.###} | {scenario.HardFails.Count} | {Times(scenario.Timings.RiddleOfFire)} | {Times(scenario.Timings.Brotherhood)} | {Times(scenario.Timings.PerfectBalance)} | {Times(scenario.Timings.Potion)} | {Times(scenario.Timings.PhantomRush)} | {gcdStop:0.###} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Key Action Timelines");
        foreach (var scenario in current.Scenarios.OrderBy(s => s.ScenarioName))
        {
            sb.AppendLine();
            sb.AppendLine($"### {scenario.ScenarioName}");
            sb.AppendLine($"- RoF: {Times(scenario.Timings.RiddleOfFire)}");
            sb.AppendLine($"- Brotherhood: {Times(scenario.Timings.Brotherhood)}");
            sb.AppendLine($"- Perfect Balance: {Times(scenario.Timings.PerfectBalance)}");
            sb.AppendLine($"- Potion: {Times(scenario.Timings.Potion)}");
            sb.AppendLine($"- Phantom Rush: {Times(scenario.Timings.PhantomRush)}");
            sb.AppendLine($"- Riddle of Earth: {Times(scenario.Timings.RiddleOfEarth)}");
            sb.AppendLine($"- Targetable=false intervals: {Intervals(scenario.Frames, f => !f.TargetAvailable)}");
            sb.AppendLine($"- HaveTarget=false intervals: {Intervals(scenario.Frames, f => !f.HaveTarget)}");
            sb.AppendLine($"- CanMelee=false intervals: {Intervals(scenario.Frames, f => !f.MeleeAvailable)}");
            sb.AppendLine($"- LookAway intervals: {Intervals(scenario.Frames, f => f.LookAway)}");
            sb.AppendLine($"- Forbidden intervals: {Intervals(scenario.Frames, f => f.Forbidden)}");
            sb.AppendLine($"- Riddle of Earth reasons: {Reasons(scenario.Frames)}");
            sb.AppendLine($"- Action timeline: {ActionTimeline(scenario.Frames)}");
            var stopped = scenario.Frames.Where(f => f.TargetAvailable && f.MeleeAvailable && !f.LookAway && f.SelectedGCD == null).Select(f => f.Time).ToList();
            sb.AppendLine($"- GCD stop frames: {Times(stopped)}");
            sb.AppendLine($"- LookAway violations: {scenario.HardFails.Count(f => f is HardFailCode.LookAwayEnemyGcd or HardFailCode.LookAwayEnemyOgcd)}");
            sb.AppendLine($"- AIHints/RoE violations: {scenario.HardFails.Count(f => f is HardFailCode.RiddleOfEarthInvalidDamage or HardFailCode.UnsafeThunderclap)}");
        }
        sb.AppendLine();

        if (compare.Verdict == "REJECTED")
        {
            sb.AppendLine("Rejected reasons:");
            foreach (var reason in compare.RejectionReasons)
                sb.AppendLine($"- {reason}");
            sb.AppendLine();
        }

        sb.AppendLine($"Verdict: {compare.Verdict}");
        return sb.ToString();
    }

    private static bool HasMajorRegression(RegressionResult baseline, RegressionResult current)
    {
        var baselineByName = baseline.Scenarios.ToDictionary(s => s.ScenarioName, StringComparer.OrdinalIgnoreCase);
        return current.Scenarios.Any(s =>
            baselineByName.TryGetValue(s.ScenarioName, out var b)
            && (s.HardFails.Count > b.HardFails.Count || s.Score < b.Score - 5));
    }

    private static double EvenPb2SuccessRate(RegressionResult result)
    {
        var values = result.Scenarios.Select(s => s.Metrics.GetValueOrDefault("EvenPB2SuccessCount")).ToList();
        return values.Count == 0 ? 0 : values.Average();
    }

    private static double EvenSyncPrecision(RegressionResult result)
    {
        var values = result.Scenarios.Select(s => s.Metrics.GetValueOrDefault("EvenSyncScore")).ToList();
        return values.Count == 0 ? 0 : values.Average();
    }

    private static double GcdStopTime(RegressionResult result)
        => result.Scenarios.Sum(s => s.Metrics.GetValueOrDefault("GcdStopTime"));

    private static double PhantomRushEvenSuccessRate(RegressionResult result)
    {
        var values = result.Scenarios.Select(s => s.Metrics.GetValueOrDefault("PhantomRushEvenSuccessCount")).ToList();
        return values.Count == 0 ? 0 : values.Average();
    }

    private static string Times(IEnumerable<double> values)
    {
        var list = values.Select(v => v.ToString("0.###")).Take(12).ToList();
        if (list.Count == 0)
            return "-";
        return string.Join(", ", list);
    }

    private static string Intervals(IEnumerable<ActionFrame> frames, Func<ActionFrame, bool> predicate)
    {
        var ordered = frames.OrderBy(f => f.Time).ToList();
        List<string> intervals = [];
        double? start = null;
        double last = 0;
        foreach (var frame in ordered)
        {
            if (predicate(frame))
            {
                start ??= frame.Time;
                last = frame.Time;
            }
            else if (start is { } s)
            {
                intervals.Add($"{s:0.###}-{last:0.###}");
                start = null;
            }
        }
        if (start is { } finalStart)
            intervals.Add($"{finalStart:0.###}-{last:0.###}");
        return intervals.Count == 0 ? "-" : string.Join(", ", intervals.Take(12));
    }

    private static string Reasons(IEnumerable<ActionFrame> frames)
    {
        var reasons = frames
            .Where(f => f.SelectedOGCD.Contains("RiddleOfEarth") && !string.IsNullOrWhiteSpace(f.RiddleOfEarthReason))
            .Select(f => $"{f.Time:0.###}:{f.RiddleOfEarthReason}")
            .Take(12)
            .ToList();
        return reasons.Count == 0 ? "-" : string.Join(", ", reasons);
    }

    private static string ActionTimeline(IEnumerable<ActionFrame> frames)
    {
        var actions = frames
            .Where(f => f.SelectedGCD != null || f.SelectedOGCD.Count > 0)
            .Select(f =>
            {
                var ogcd = f.SelectedOGCD.Count == 0 ? "" : $"/{string.Join("+", f.SelectedOGCD)}";
                return $"{f.Time:0.###}:{f.SelectedGCD ?? "-"}{ogcd}";
            })
            .Take(24)
            .ToList();
        return actions.Count == 0 ? "-" : string.Join(", ", actions);
    }

    private static void AppendList(StringBuilder sb, string title, IReadOnlyList<string> items)
    {
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        if (items.Count == 0)
            sb.AppendLine("- none");
        else
            foreach (var item in items)
                sb.AppendLine($"- {item}");
        sb.AppendLine();
    }

    private static void AppendCoverageSummary(StringBuilder sb, RegressionResult current)
    {
        sb.AppendLine("## Coverage Summary");
        sb.AppendLine();
        AppendGroup(sb, "Categories", current.Scenarios.GroupBy(s => s.Category.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "BurstTiming", current.Scenarios.GroupBy(s => s.BurstTiming.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "OpenerRoFOffset", current.Scenarios.GroupBy(s => s.OpenerRoFOffset.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "RotationMode", current.Scenarios.GroupBy(s => s.RotationMode.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "PBStrategy", current.Scenarios.GroupBy(s => s.PBStrategy.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "BlitzStrategy", current.Scenarios.GroupBy(s => s.BlitzStrategy.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "RoFStrategy", current.Scenarios.GroupBy(s => s.RoFStrategy.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "BrotherhoodStrategy", current.Scenarios.GroupBy(s => s.BrotherhoodStrategy.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "RoWStrategy", current.Scenarios.GroupBy(s => s.RoWStrategy.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "RoEStrategy", current.Scenarios.GroupBy(s => s.RoEStrategy.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "ThunderclapStrategy", current.Scenarios.GroupBy(s => s.ThunderclapStrategy.ToString()).OrderBy(g => g.Key));
        AppendGroup(sb, "LevelCap", current.Scenarios.GroupBy(s => s.LevelCap.ToString()).OrderBy(g => int.Parse(g.Key)));
        sb.AppendLine();
    }

    private static void AppendGroup(StringBuilder sb, string title, IEnumerable<IGrouping<string, ScenarioResult>> groups)
        => sb.AppendLine($"- {title}: {string.Join(", ", groups.Select(g => $"{g.Key}={g.Count()}"))}");
}
