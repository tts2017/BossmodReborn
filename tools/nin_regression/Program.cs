namespace NinRegression;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            var scenarios = BuildScenarios(options);
            if (scenarios.Count == 0)
                throw new ArgumentException("No scenarios matched the requested selector.");

            var emulator = new NinRotationEmulator();
            var results = scenarios
                .AsParallel()
                .AsOrdered()
                .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
                .Select(emulator.Run)
                .ToList();

            var output = NinReport.Build(results);
            NinReport.Write(output, options.Out);
            NinReport.Print(output);
            Console.WriteLine($"Results: {Path.GetFullPath(options.Out)}");
            return output.Summary.HardFail == 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static IReadOnlyList<NinScenario> BuildScenarios(CliOptions options)
    {
        if (options.FuzzCount > 0)
            return NinScenarioGenerator.BuildFuzz(options.FuzzCount);
        if (!string.IsNullOrWhiteSpace(options.Scenario))
            return NinScenarioGenerator.Select(options.Scenario);
        if (options.All)
            return NinScenarioGenerator.BuildAll();
        throw new ArgumentException("Specify --all, --scenario <name/category>, or --fuzz <count>.");
    }
}

public sealed record CliOptions
{
    public bool All { get; init; }
    public string? Scenario { get; init; }
    public int FuzzCount { get; init; }
    public string Out { get; init; } = Path.Combine("tools", "nin_regression", "out");

    public static CliOptions Parse(string[] args)
    {
        var all = false;
        string? scenario = null;
        var fuzz = 0;
        var outDir = Path.Combine("tools", "nin_regression", "out");

        for (var i = 0; i < args.Length; ++i)
        {
            switch (args[i])
            {
                case "--all":
                    all = true;
                    break;
                case "--scenario":
                    scenario = RequireValue(args, ref i, "--scenario");
                    break;
                case "--fuzz":
                    fuzz = int.Parse(RequireValue(args, ref i, "--fuzz"));
                    break;
                case "--out":
                    outDir = RequireValue(args, ref i, "--out");
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        return new CliOptions
        {
            All = all,
            Scenario = scenario,
            FuzzCount = fuzz,
            Out = outDir
        };
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        index++;
        return args[index];
    }
}
