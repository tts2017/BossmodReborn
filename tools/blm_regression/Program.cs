using BlmRegression;

try
{
    BlmRuleset.ValidateProductionParity();
    var options = CliOptions.Parse(args);
    var scenarios = options.All ? BlmScenarioCatalog.BuildAll()
        : options.HighEnd ? BlmScenarioCatalog.BuildHighEndPreflight()
        : BlmScenarioCatalog.Select(options.Pattern ?? "");
    if (scenarios.Count == 0)
        throw new ArgumentException($"No BLM regression scenarios matched '{options.Pattern}'.");

    var runner = new BlmRegressionRunner();
    var output = runner.Run(scenarios);
    runner.WriteOutputs(options.OutDir, output);

    Console.WriteLine($"BLM regression scenarios: {output.Summary.ScenarioCount}");
    Console.WriteLine($"Ruleset: {output.Summary.ToolVersion}");
    Console.WriteLine($"Passed: {output.Summary.PassCount}");
    Console.WriteLine($"Failed: {output.Summary.FailCount}");
    Console.WriteLine($"HardFail: {output.Summary.HardFailCount}");
    Console.WriteLine($"Raw Soft signal: {output.Summary.RawSoftSignalCount}");
    Console.WriteLine($"Raw Soft regression: {output.Summary.RawSoftRegressionCount}");
    Console.WriteLine($"Risk-adjusted Soft regression: {output.Summary.SoftRegressionCount}");
    Console.WriteLine($"CoverageGap: {output.Summary.CoverageGapCount}");
    Console.WriteLine($"Auto-minimized hard-fail reproductions: {output.Reproductions.Count}");
    Console.WriteLine($"Results: {Path.GetFullPath(options.OutDir)}");

    var preflightFailure = options.HighEnd && (output.Summary.SoftRegressionCount > 0 || output.Summary.CoverageGapCount > 0);
    return output.Summary.FailCount == 0 && !preflightFailure ? 0 : 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

internal sealed record CliOptions(bool All, bool HighEnd, string? Pattern, string OutDir)
{
    public static CliOptions Parse(string[] args)
    {
        var all = false;
        var highEnd = false;
        string? pattern = null;
        var outDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results");

        for (var i = 0; i < args.Length; ++i)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--all":
                    all = true;
                    break;
                case "--highend":
                    highEnd = true;
                    break;
                case "--pattern":
                case "--scenario":
                    pattern = RequireValue(args, ref i, arg);
                    break;
                case "--out":
                    outDir = RequireValue(args, ref i, arg);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{arg}'.");
            }
        }

        if (all && highEnd)
            throw new ArgumentException("Specify either --all or --highend, not both.");
        if (!all && !highEnd && string.IsNullOrWhiteSpace(pattern))
            throw new ArgumentException("Specify --all, --highend, or --pattern <pattern>.");

        return new(all, highEnd, pattern, Path.GetFullPath(outDir));
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {option}.");
        return args[++index];
    }
}
