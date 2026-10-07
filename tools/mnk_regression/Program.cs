using System.Text.Json;
using System.Text.Json.Serialization;

namespace MnkRegression;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            if (args[0] == "--all")
                return Emulate(["--scenario", "all", "--out", Path.Combine("tools", "mnk_regression", "results", "emulated_current.json"), "--report", Path.Combine("tools", "mnk_regression", "results", "emulated_report.md")]);
            if (args[0] == "--sweep")
                return Sweep(args[1..]);

            return args[0] switch
            {
                "generate-baseline" => GenerateBaseline(args[1..]),
                "run" => Run(args[1..]),
                "emulate" => Emulate(args[1..]),
                "sweep" => Sweep(args[1..]),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static int GenerateBaseline(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (string.IsNullOrWhiteSpace(options.Out))
            throw new ArgumentException("generate-baseline requires --out");

        var scenarios = ScenarioCatalog.Select(options.Scenario);
        if (scenarios.Count == 0)
            throw new ArgumentException($"No scenarios matched '{options.Scenario}'.");

        var result = new ScenarioRunner().Run(scenarios);
        WriteJson(options.Out, result);
        Console.WriteLine($"Baseline written: {options.Out}");
        Console.WriteLine($"Scenarios: {result.Scenarios.Count}");
        return 0;
    }

    private static int Run(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (string.IsNullOrWhiteSpace(options.Out))
            throw new ArgumentException("run requires --out");
        if (string.IsNullOrWhiteSpace(options.Report))
            throw new ArgumentException("run requires --report");

        var scenarios = ScenarioCatalog.Select(options.Scenario);
        if (scenarios.Count == 0)
            throw new ArgumentException($"No scenarios matched '{options.Scenario}'.");

        var current = new ScenarioRunner().Run(scenarios);
        RegressionResult? baseline = null;
        if (!string.IsNullOrWhiteSpace(options.Baseline))
            baseline = ReadJson<RegressionResult>(options.Baseline);

        var compare = ResultComparer.Compare(baseline, current);
        WriteJson(options.Out, current);
        WriteText(options.Report, ResultComparer.BuildReport(current, baseline, compare));

        Console.WriteLine($"Current written: {options.Out}");
        Console.WriteLine($"Report written: {options.Report}");
        Console.WriteLine($"Verdict: {compare.Verdict}");
        return compare.Verdict == "ADOPTABLE" ? 0 : 3;
    }

    private static int Emulate(string[] args)
    {
        var options = CliOptions.Parse(args);
        if (string.IsNullOrWhiteSpace(options.Out))
            throw new ArgumentException("emulate requires --out");
        if (string.IsNullOrWhiteSpace(options.Report))
            throw new ArgumentException("emulate requires --report");

        var scenarios = ScenarioCatalog.SelectBattle(options.Scenario);
        if (scenarios.Count == 0)
            throw new ArgumentException($"No battle scenarios matched '{options.Scenario}'.");

        var current = new BattleEmulator().Run(scenarios);
        RegressionResult? baseline = null;
        if (!string.IsNullOrWhiteSpace(options.Baseline))
            baseline = ReadJson<RegressionResult>(options.Baseline);

        var compare = ResultComparer.Compare(baseline, current);
        WriteJson(options.Out, current);
        WriteText(options.Report, ResultComparer.BuildReport(current, baseline, compare));

        Console.WriteLine($"Emulated current written: {options.Out}");
        Console.WriteLine($"Report written: {options.Report}");
        Console.WriteLine($"Verdict: {compare.Verdict}");
        return compare.Verdict == "ADOPTABLE" ? 0 : 3;
    }

    private static int Sweep(string[] args)
    {
        var options = CliOptions.ParseAllowEmpty(args);
        var scenarioSelector = options.Scenario ?? "all";
        var scenarios = ScenarioCatalog.SelectBattle(scenarioSelector);
        if (scenarios.Count == 0)
            throw new ArgumentException($"No battle scenarios matched '{scenarioSelector}'.");

        var outDir = string.IsNullOrWhiteSpace(options.Out)
            ? Path.Combine("tools", "mnk_regression", "results", "sweep")
            : options.Out;
        Directory.CreateDirectory(outDir);

        var baseline = new BattleEmulator(CandidateMode.Baseline).Run(scenarios);
        WriteJson(Path.Combine(outDir, "baseline.json"), baseline);

        List<(CandidateMode Candidate, RegressionResult Result, CompareReport Compare)> candidates = [];
        foreach (var candidate in Enum.GetValues<CandidateMode>().Where(c => c != CandidateMode.Baseline))
        {
            var result = new BattleEmulator(candidate).Run(scenarios);
            var compare = ResultComparer.Compare(baseline, result);
            candidates.Add((candidate, result, compare));
            WriteJson(Path.Combine(outDir, $"{candidate}.json"), result);
        }

        var report = BuildSweepReport(baseline, candidates);
        var reportPath = string.IsNullOrWhiteSpace(options.Report)
            ? Path.Combine(outDir, "sweep_report.md")
            : options.Report;
        WriteText(reportPath, report);

        Console.WriteLine($"Sweep written: {outDir}");
        Console.WriteLine($"Report written: {reportPath}");
        foreach (var (candidate, result, compare) in candidates)
        {
            var delta = AggregateMetric(result, "TotalPotency") - AggregateMetric(baseline, "TotalPotency");
            Console.WriteLine($"{candidate}: {compare.Verdict}, hardfails={compare.HardFailScenarios.Count}, totalPotencyDelta={delta:0.###}");
        }

        return candidates.All(c => c.Compare.HardFailScenarios.Count == 0) ? 0 : 3;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("MNK regression tool");
        Console.WriteLine("Commands:");
        Console.WriteLine("  generate-baseline --out <path> [--scenario all]");
        Console.WriteLine("  run --out <path> --report <path> [--baseline <path>] [--scenario all]");
        Console.WriteLine("  emulate --out <path> --report <path> [--baseline <path>] [--scenario all]");
        Console.WriteLine("  sweep [--out <dir>] [--report <path>] [--scenario all]");
        Console.WriteLine("  --all");
        Console.WriteLine("  --sweep [--out <dir>] [--report <path>] [--scenario all]");
    }

    private static string BuildSweepReport(RegressionResult baseline, IReadOnlyList<(CandidateMode Candidate, RegressionResult Result, CompareReport Compare)> candidates)
    {
        var lines = new List<string>
        {
            "# MNK Candidate Sweep",
            "",
            $"Generated: {DateTimeOffset.UtcNow:O}",
            $"Baseline scenarios: {baseline.Scenarios.Count}",
            "",
            "| Candidate | Verdict | HardFails | Total potency delta | PPS delta | RoF potency delta | BH potency delta | Potion potency delta | PB overcap delta | Chakra overcap delta |",
            "|---|---|---:|---:|---:|---:|---:|---:|---:|---:|"
        };

        foreach (var (candidate, result, compare) in candidates)
        {
            lines.Add($"| {candidate} | {compare.Verdict} | {compare.HardFailScenarios.Count} | {Delta(baseline, result, "TotalPotency"):0.###} | {Delta(baseline, result, "PPS"):0.###} | {Delta(baseline, result, "RoFPotency"):0.###} | {Delta(baseline, result, "BrotherhoodPotency"):0.###} | {Delta(baseline, result, "PotionPotency"):0.###} | {Delta(baseline, result, "PerfectBalanceOvercapTime"):0.###} | {Delta(baseline, result, "ChakraOvercapFrames"):0.###} |");
        }

        lines.Add("");
        foreach (var (candidate, result, compare) in candidates)
        {
            lines.Add($"## {candidate}");
            lines.Add("");
            lines.Add($"- Verdict: {compare.Verdict}");
            lines.Add($"- HardFail scenarios: {compare.HardFailScenarios.Count}");
            lines.Add($"- Rejection reasons: {(compare.RejectionReasons.Count == 0 ? "none" : string.Join("; ", compare.RejectionReasons))}");
            lines.Add($"- Total potency delta: {Delta(baseline, result, "TotalPotency"):0.###}");
            lines.Add($"- PPS delta: {Delta(baseline, result, "PPS"):0.###}");
            lines.Add($"- Improved: {(compare.ImprovedScenarios.Count == 0 ? "none" : string.Join("; ", compare.ImprovedScenarios.Take(12)))}");
            lines.Add($"- Worsened: {(compare.WorsenedScenarios.Count == 0 ? "none" : string.Join("; ", compare.WorsenedScenarios.Take(12)))}");
            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static double Delta(RegressionResult baseline, RegressionResult candidate, string metric)
        => AggregateMetric(candidate, metric) - AggregateMetric(baseline, metric);

    private static double AggregateMetric(RegressionResult result, string metric)
        => result.Scenarios.Sum(s => s.Metrics.GetValueOrDefault(metric));

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static T ReadJson<T>(string path)
    {
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)
            ?? throw new InvalidDataException($"Could not deserialize {path}");
    }

    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, text);
    }

    private sealed record CliOptions(string? Out, string? Baseline, string? Report, string? Scenario)
    {
        public static CliOptions Parse(string[] args)
        {
            string? output = null;
            string? baseline = null;
            string? report = null;
            string? scenario = "all";

            for (var i = 0; i < args.Length; ++i)
            {
                var key = args[i];
                if (!key.StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"Unexpected argument '{key}'.");
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for '{key}'.");

                var value = args[++i];
                switch (key)
                {
                    case "--out":
                        output = value;
                        break;
                    case "--baseline":
                        baseline = value;
                        break;
                    case "--report":
                        report = value;
                        break;
                    case "--scenario":
                        scenario = value;
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{key}'.");
                }
            }

            return new(output, baseline, report, scenario);
        }

        public static CliOptions ParseAllowEmpty(string[] args)
            => args.Length == 0 ? new(null, null, null, "all") : Parse(args);
    }
}
