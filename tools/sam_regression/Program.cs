using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SamRegression;

try
{
    var options = CliOptions.Parse(args);
    var scenarios = options.All ? SamScenarioCatalog.BuildAll(options.Seed) : SamScenarioCatalog.Select(options.Scenario ?? "", options.Seed);
    if (scenarios.Count == 0)
        throw new ArgumentException($"No SAM regression scenarios matched '{options.Scenario}'.");

    var sim = new SamSim();
    var results = scenarios
        .AsParallel()
        .AsOrdered()
        .WithDegreeOfParallelism(Math.Max(1, Environment.ProcessorCount / 2))
        .Select(sim.Run)
        .ToList();

    var output = SamChecks.BuildOutput(results, options.All);
    Print(output);
    if (!string.IsNullOrWhiteSpace(options.DumpPath))
        WriteDump(output, options.DumpPath);

    return output.Summary.HardFail == 0 ? 0 : 3;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

static void Print(SamRegressionOutput output)
{
    Console.WriteLine("SAM regression");
    Console.WriteLine($"Scenarios: {output.Summary.Scenarios}");
    Console.WriteLine($"Passed: {output.Summary.Passed}");
    Console.WriteLine($"HardFail: {output.Summary.HardFail}");
    Console.WriteLine($"SoftFail: {output.Summary.SoftFail}");
    Console.WriteLine($"CoverageGap: {output.Summary.CoverageGap}");

    var failures = output.Results.SelectMany(r => r.HardFailures)
        .Concat(output.Results.SelectMany(r => r.SoftFailures))
        .Concat(output.Results.SelectMany(r => r.CoverageGaps))
        .Take(25)
        .ToList();

    foreach (var failure in failures)
    {
        Console.WriteLine();
        Console.WriteLine($"{failure.Severity}: {failure.ScenarioName}");
        Console.WriteLine($"time: {failure.Time:0.000}");
        Console.WriteLine($"reason: {failure.Reason}");
        Console.WriteLine($"state: {failure.State}");
        if (!string.IsNullOrWhiteSpace(failure.SuspectedLogic))
            Console.WriteLine($"suspected logic: {failure.SuspectedLogic}");
        if (failure.Last20Actions.Count > 0)
        {
            Console.WriteLine("last 20 actions:");
            foreach (var action in failure.Last20Actions)
                Console.WriteLine($"  {action.Time:0.000} {action.Kind} {action.Action} {action.Target} {action.Reason}");
        }
    }
}

static void WriteDump(SamRegressionOutput output, string path)
{
    path = Path.GetFullPath(path);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    if (Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase))
    {
        var sb = new StringBuilder();
        sb.AppendLine("scenario,severity,time,reason,state");
        foreach (var failure in output.Results.SelectMany(r => r.HardFailures.Concat(r.SoftFailures).Concat(r.CoverageGaps)))
            sb.AppendLine($"{Csv(failure.ScenarioName)},{failure.Severity},{failure.Time:0.000},{Csv(failure.Reason)},{Csv(failure.State)}");
        File.WriteAllText(path, sb.ToString());
    }
    else
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            Converters = { new JsonStringEnumConverter() }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(output, options));
    }
}

static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

internal sealed record CliOptions(bool All, string? Scenario, string? DumpPath, int Seed)
{
    public static CliOptions Parse(string[] args)
    {
        var all = false;
        string? scenario = null;
        string? dump = null;
        var seed = 1;

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
                case "--dump":
                    dump = RequireValue(args, ref i, "--dump");
                    break;
                case "--seed":
                    seed = int.Parse(RequireValue(args, ref i, "--seed"));
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        if (!all && string.IsNullOrWhiteSpace(scenario))
            throw new ArgumentException("Specify --all or --scenario <name>.");

        return new(all, scenario, dump, seed);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException($"{option} requires a value.");
        return args[++index];
    }
}
