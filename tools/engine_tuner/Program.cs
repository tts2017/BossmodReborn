using System.Globalization;
using BossMod.Autorotation.Engine;
using EngineTools;

namespace EngineTuner;

// Toy-job tuner (engine-model evaluator). The RPR tuner with the real harness evaluator lives in tools/rpr_engine_eval.
public static class Program
{
    public static int Main(string[] args)
    {
        var scenariosPath = Arg(args, "--scenarios", Path.Combine(AppContext.BaseDirectory, "scenarios", "toy.json"));
        var generations = int.Parse(Arg(args, "--generations", "10"), CultureInfo.InvariantCulture);
        var seed = int.Parse(Arg(args, "--seed", "1"), CultureInfo.InvariantCulture);
        var outPath = Arg(args, "--out", Path.Combine(Environment.CurrentDirectory, "weights-toy.json"));

        var job = ToyJob.Build();
        var baseWeights = ToyJob.DefaultWeights();
        baseWeights.HorizonGcds = 3;   // deterministic evaluation: fixed depth, effectively unlimited budget
        baseWeights.BudgetMs = 1000;
        var scenarios = EngineScenario.LoadAll(scenariosPath);
        Console.WriteLine($"job=toy scenarios={scenarios.Count} generations={generations} seed={seed}");

        var best = Tuning.Tune(baseWeights, Tuning.DefaultParameters, scenarios, (w, sc) => ScenarioRunner.Run(job, w, sc).Dps, generations, seed, log: Console.WriteLine);
        File.WriteAllText(outPath, best.weights.ToJson());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"baseline {best.baseline:f2} -> tuned {best.fitness:f2} dps; written {outPath}"));
        return 0;
    }

    private static string Arg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
