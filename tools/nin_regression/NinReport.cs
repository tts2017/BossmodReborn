using System.Text.Json;
using System.Text.Json.Serialization;

namespace NinRegression;

public static class NinReport
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static NinRegressionOutput Build(IReadOnlyList<NinScenarioResult> results)
    {
        var summary = new NinRegressionSummary
        {
            Scenarios = results.Count,
            Passed = results.Count(r => r.Passed),
            HardFail = results.Sum(r => r.HardFailures.Count),
            SoftRegression = results.Sum(r => r.SoftRegressions.Count),
            CoverageGap = results.Sum(r => r.CoverageGaps.Count)
        };
        return new NinRegressionOutput { Summary = summary, Results = results.ToList() };
    }

    public static void Write(NinRegressionOutput output, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var sampleDir = Path.Combine(outDir, "nin_regression_samples");
        if (Directory.Exists(sampleDir))
            Directory.Delete(sampleDir, recursive: true);
        Directory.CreateDirectory(sampleDir);

        var failures = output.Results
            .SelectMany(r => r.HardFailures.Concat(r.SoftRegressions))
            .ToList();

        WriteJson(Path.Combine(outDir, "nin_regression_summary.json"), output.Summary);
        WriteJson(Path.Combine(outDir, "nin_regression_failures.json"), failures);

        foreach (var result in output.Results.Where(r => r.HardFailures.Count > 0 || r.SoftRegressions.Count > 0).Take(50))
            WriteJson(Path.Combine(sampleDir, $"{Sanitize(result.Scenario.Name)}.json"), result with { Actions = result.Actions.TakeLast(200).ToList() });

        foreach (var result in output.Results.Where(r => r.HardFailures.Count == 0 && r.SoftRegressions.Count == 0).Take(20))
            WriteJson(Path.Combine(sampleDir, $"{Sanitize(result.Scenario.Name)}.json"), result with { Actions = result.Actions.TakeLast(120).ToList() });
    }

    public static void Print(NinRegressionOutput output)
    {
        Console.WriteLine("NIN regression");
        Console.WriteLine($"Scenarios: {output.Summary.Scenarios}");
        Console.WriteLine($"Passed: {output.Summary.Passed}");
        Console.WriteLine($"HardFail: {output.Summary.HardFail}");
        Console.WriteLine($"Soft regression: {output.Summary.SoftRegression}");
        Console.WriteLine($"CoverageGap: {output.Summary.CoverageGap}");

        var failuresToPrint = output.Results.SelectMany(r => r.HardFailures).Concat(output.Results.SelectMany(r => r.SoftRegressions)).Take(20);
        foreach (var failure in failuresToPrint)
        {
            Console.WriteLine();
            Console.WriteLine($"{failure.Severity}: {failure.ScenarioName}");
            Console.WriteLine($"seed: {failure.Seed}");
            Console.WriteLine($"time: {failure.Time:0.000}");
            Console.WriteLine($"state: {failure.StateSummary}");
            Console.WriteLine($"expected invariant: {failure.ExpectedInvariant}");
            Console.WriteLine($"actual action: {failure.ActualAction}");
            Console.WriteLine($"suspected function name: {failure.SuspectedFunctionName}");
            Console.WriteLine("last 20 actions:");
            foreach (var action in failure.Last20Actions)
                Console.WriteLine($"  {action.Time:0.000} {action.Kind} {action.Action} {action.TargetKind} {action.Reason}");
        }
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }

    private static string Sanitize(string value)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            value = value.Replace(c, '_');
        return value.Length > 120 ? value[..120] : value;
    }
}
