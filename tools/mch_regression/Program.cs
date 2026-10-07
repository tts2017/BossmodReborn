namespace MchRegression;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Any(a => a.Equals("--extract-fflogs", StringComparison.OrdinalIgnoreCase)))
                return FflogsMchExtractor.RunAsync(args).GetAwaiter().GetResult();

            if (args.Any(a => a.Equals("--build-dancing-mad-profile", StringComparison.OrdinalIgnoreCase)))
                return MchDancingMadProfileBuilder.BuildFromExtractedEvents(args);

            var options = MchRunOptions.Parse(args);
            var repoRoot = FindRepoRoot();
            var mchPath = FindMchPath(repoRoot);
            var source = File.ReadAllText(mchPath);
            var scenarios = options.ScenarioName == null
                ? MchScenarioCatalog.BuildAll(options.DurationOverride, options.SeedOverride)
                : MchScenarioCatalog.BuildAll(options.DurationOverride, options.SeedOverride).Where(s => s.Name.Equals(options.ScenarioName, StringComparison.OrdinalIgnoreCase)).ToArray();

            if (options.ScenarioName != null && scenarios.Count == 0)
                throw new ArgumentException($"No MCH regression scenario matched '{options.ScenarioName}'.");

            var simulator = new MchSimulator();
            var verdicts = scenarios.Select(s => MchRegressionRules.Evaluate(simulator.Run(s), source)).ToArray();
            MchReport.Print(verdicts);

            if (!string.IsNullOrWhiteSpace(options.DumpPath))
                MchReport.DumpCsv(options.DumpPath, verdicts);

            return verdicts.Any(v => v.HardFails.Count > 0 || v.CoverageGaps.Count > 0) ? 3 : 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BossModReborn.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate BossModReborn.sln from the current directory.");
    }

    private static string FindMchPath(string repoRoot)
    {
        var rangedPath = Path.Combine(repoRoot, "BossMod", "Autorotation", "Standard", "xan", "Ranged", "MCH.cs");
        if (File.Exists(rangedPath))
            return rangedPath;
        var flatPath = Path.Combine(repoRoot, "BossMod", "Autorotation", "Standard", "xan", "MCH.cs");
        if (File.Exists(flatPath))
            return flatPath;
        throw new FileNotFoundException("Could not locate MCH.cs in xan/Ranged or xan root.");
    }
}

public sealed record MchRunOptions(string? ScenarioName, double? DurationOverride, int? SeedOverride, string? DumpPath)
{
    public static MchRunOptions Parse(string[] args)
    {
        string? scenario = null;
        double? duration = null;
        int? seed = null;
        string? dump = null;
        for (var i = 0; i < args.Length; ++i)
        {
            var arg = args[i];
            if (arg.Equals("--all", StringComparison.OrdinalIgnoreCase))
                continue;
            if (arg.Equals("--scenario", StringComparison.OrdinalIgnoreCase))
            {
                scenario = RequiredValue(args, ref i, "--scenario");
                continue;
            }
            if (arg.Equals("--duration", StringComparison.OrdinalIgnoreCase))
            {
                duration = double.Parse(RequiredValue(args, ref i, "--duration"));
                continue;
            }
            if (arg.Equals("--seed", StringComparison.OrdinalIgnoreCase))
            {
                seed = int.Parse(RequiredValue(args, ref i, "--seed"));
                continue;
            }
            if (arg.Equals("--dump", StringComparison.OrdinalIgnoreCase))
            {
                dump = RequiredValue(args, ref i, "--dump");
                continue;
            }
            throw new ArgumentException("Usage: dotnet run --project tools/mch_regression/MchRegression.csproj -- [--all] [--scenario <name>] [--duration <seconds>] [--seed <number>] [--dump <path>]");
        }
        return new(scenario, duration, seed, dump);
    }

    private static string RequiredValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
            throw new ArgumentException($"Missing value for {option}.");
        return args[index];
    }
}
