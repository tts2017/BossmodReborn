using System.Globalization;
using BossMod.Autorotation.Engine;
using EngineTools;

namespace EngineTuner;

// Scores one weight set on one scenario (higher is better). Step 2 ships the engine-model evaluator for the toy job;
// Step 3 adds evaluators that drive the existing job harness emulators (tools/*_regression, *_real_harness).
public interface IScenarioEvaluator
{
    double Evaluate(EngineWeights weights, EngineScenario scenario);
}

public sealed class EngineModelEvaluator(JobDefinition job) : IScenarioEvaluator
{
    public double Evaluate(EngineWeights weights, EngineScenario scenario) => ScenarioRunner.Run(job, weights, scenario).Dps;
}

public readonly record struct TunedParameter(string Name, float Lo, float Hi);

public static class Program
{
    // ranges for the generic weights; [0,1]-normalized for CMA-ES
    public static readonly TunedParameter[] DefaultParameters =
    [
        new(nameof(EngineWeights.OverCap), 0, 3),
        new(nameof(EngineWeights.Combo), 0, 2),
        new(nameof(EngineWeights.LambdaScale), 0.5f, 1.5f),
        new(nameof(EngineWeights.TargetPull), 0, 1),
        new(nameof(EngineWeights.SwitchMargin), 0, 100),
        new(nameof(EngineWeights.FillerScale), 0.5f, 1.5f),
        new(nameof(EngineWeights.BurstBias), 0, 3),
        new(nameof(EngineWeights.StatusRemainder), 0, 2),
    ];

    public static int Main(string[] args)
    {
        var job = Arg(args, "--job", "toy");
        var scenariosPath = Arg(args, "--scenarios", Path.Combine(AppContext.BaseDirectory, "scenarios", "toy.json"));
        var generations = int.Parse(Arg(args, "--generations", "10"), CultureInfo.InvariantCulture);
        var seed = int.Parse(Arg(args, "--seed", "1"), CultureInfo.InvariantCulture);
        var outPath = Arg(args, "--out", Path.Combine(Environment.CurrentDirectory, $"weights-{job}.json"));
        var population = args.Contains("--population") ? int.Parse(Arg(args, "--population", "0"), CultureInfo.InvariantCulture) : (int?)null;

        var (definition, baseWeights) = job switch
        {
            "toy" => (ToyJob.Build(), ToyJob.DefaultWeights()),
            _ => throw new ArgumentException($"unknown job '{job}' (Step 2 supports: toy)")
        };
        // deterministic evaluation: a fixed search depth with an effectively unlimited time budget
        baseWeights.HorizonGcds = 3;
        baseWeights.BudgetMs = 1000;

        var scenarios = EngineScenario.LoadAll(scenariosPath);
        IScenarioEvaluator evaluator = new EngineModelEvaluator(definition);
        Console.WriteLine($"job={job} scenarios={scenarios.Count} generations={generations} seed={seed}");

        var best = Tune(baseWeights, DefaultParameters, scenarios, evaluator, generations, seed, population, (g, bestFit, meanFit, sigma) =>
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"gen {g,3}: best {bestFit,9:f2}  mean {meanFit,9:f2}  sigma {sigma:f3}")));

        File.WriteAllText(outPath, best.weights.ToJson());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"baseline {best.baseline:f2} -> tuned {best.fitness:f2} dps; written {outPath}"));
        return 0;
    }

    public static (EngineWeights weights, double fitness, double baseline) Tune(EngineWeights baseWeights, TunedParameter[] parameters, List<EngineScenario> scenarios,
        IScenarioEvaluator evaluator, int generations, int seed, int? population, Action<int, double, double, double>? log = null)
    {
        double Score(EngineWeights w)
        {
            var sum = 0.0;
            foreach (var sc in scenarios)
                sum += evaluator.Evaluate(w, sc);
            return sum / scenarios.Count;
        }

        EngineWeights Decode(double[] x)
        {
            var w = baseWeights.Clone();
            for (var i = 0; i < parameters.Length; ++i)
            {
                var u = Math.Clamp(x[i], 0, 1);
                w.Set(parameters[i].Name, (float)(parameters[i].Lo + u * (parameters[i].Hi - parameters[i].Lo)));
            }
            return w;
        }

        var start = new double[parameters.Length];
        for (var i = 0; i < parameters.Length; ++i)
            start[i] = Math.Clamp((baseWeights.Get(parameters[i].Name) - parameters[i].Lo) / (parameters[i].Hi - parameters[i].Lo), 0, 1);

        var baseline = Score(baseWeights);
        var bestW = baseWeights;
        var bestFit = baseline;
        var cma = new Cmaes(start, 0.2, seed, population);
        for (var g = 1; g <= generations; ++g)
        {
            var xs = cma.Ask();
            var fit = new double[xs.Length];
            var candidates = xs.Select(Decode).ToArray();
            Parallel.For(0, xs.Length, k => fit[k] = Score(candidates[k]));
            for (var k = 0; k < xs.Length; ++k)
            {
                if (fit[k] > bestFit)
                {
                    bestFit = fit[k];
                    bestW = candidates[k];
                }
            }
            cma.Tell(xs, [.. fit.Select(f => -f)]); // CMA-ES minimizes
            log?.Invoke(g, bestFit, fit.Average(), cma.Sigma);
        }
        return (bestW, bestFit, baseline);
    }

    private static string Arg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
