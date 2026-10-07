using System.Text.Json;
using System.Text.Json.Serialization;

namespace DrgRegression;

internal static class Program
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
            var options = CliOptions.Parse(args);
            var scenarios = BuildScenarios(options);
            var simulator = new DrgSimulator();
            var results = scenarios.Select(simulator.Run).ToList();
            var output = new DrgRegressionOutput
            {
                Summary = new()
                {
                    Scenarios = results.Count,
                    HardFail = results.Sum(r => r.HardFails.Count),
                    SoftFinding = results.Sum(r => r.SoftFindings.Count)
                },
                Scenarios = results
            };

            Print(output);

            if (!string.IsNullOrWhiteSpace(options.Dump))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Dump))!);
                File.WriteAllText(options.Dump, JsonSerializer.Serialize(output, JsonOptions));
                Console.WriteLine($"dump={options.Dump}");
            }

            return output.Summary.HardFail == 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine("Usage: dotnet run --project tools/drg_regression/DrgRegression.csproj -- [--all] [--scenario <name>] [--dump <path>]");
            return 2;
        }
    }

    private static IReadOnlyList<DrgScenario> BuildScenarios(CliOptions options)
    {
        var scenarios = new[]
        {
            new DrgScenario("single_full_uptime", DrgScenarioKind.SingleTarget, InitialPowerSurge: 30, InitialDot0: 24),
            new DrgScenario("two_target_dot_maintenance", DrgScenarioKind.TwoTargetDots, InitialTargetCount: 2, InitialPowerSurge: 30, InitialDot0: 18, InitialDot1: 0),
            new DrgScenario("three_target_aoe", DrgScenarioKind.Aoe, InitialTargetCount: 5, InitialPowerSurge: 30, InitialFocus: 1),
            new DrgScenario("single_aoe_single_switch", DrgScenarioKind.TargetSwitch, InitialTargetCount: 1, InitialPowerSurge: 8, InitialDot0: 5),
            new DrgScenario("temporary_target_loss", DrgScenarioKind.TargetLost, InitialPowerSurge: 30, InitialDot0: 20),
            new DrgScenario("ranged_start_piercing_talon", DrgScenarioKind.RangedStart, InitialPowerSurge: 0),
            new DrgScenario("lotd_burst_spend", DrgScenarioKind.Burst, InitialTargetCount: 1, InitialPowerSurge: 30, InitialDot0: 20, InitialLotd: 20, InitialFocus: 2)
        };

        if (options.All)
            return scenarios;

        if (!string.IsNullOrWhiteSpace(options.Scenario))
            return scenarios.Where(s => s.Name.Equals(options.Scenario, StringComparison.OrdinalIgnoreCase)).ToArray();

        return scenarios.Take(1).ToArray();
    }

    private static void Print(DrgRegressionOutput output)
    {
        Console.WriteLine($"scenarios={output.Summary.Scenarios}");
        Console.WriteLine($"hard_fail={output.Summary.HardFail}");
        Console.WriteLine($"soft_findings={output.Summary.SoftFinding}");

        foreach (var scenario in output.Scenarios)
        {
            var firstGcd = scenario.Actions.FirstOrDefault(a => a.Kind == DrgActionKind.GCD)?.Action.ToString() ?? "None";
            Console.WriteLine($"{scenario.Scenario.Name}: actions={scenario.Actions.Count} first_gcd={firstGcd} hard={scenario.HardFails.Count} soft={scenario.SoftFindings.Count}");
        }
    }

    private sealed record CliOptions(bool All, string? Scenario, string? Dump)
    {
        public static CliOptions Parse(string[] args)
        {
            var all = false;
            string? scenario = null;
            string? dump = null;

            for (var i = 0; i < args.Length; ++i)
            {
                switch (args[i])
                {
                    case "--all":
                        all = true;
                        break;
                    case "--scenario":
                        scenario = Value(args, ref i);
                        break;
                    case "--dump":
                        dump = Value(args, ref i);
                        break;
                    default:
                        throw new ArgumentException($"Unknown option '{args[i]}'.");
                }
            }

            return new(all, scenario, dump);
        }

        private static string Value(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
                throw new ArgumentException($"Missing value for '{args[i]}'.");
            return args[++i];
        }
    }
}
