using System.Globalization;
using BossMod.Autorotation.Engine;

namespace EngineTools;

public readonly record struct TunedParameter(string Name, float Lo, float Hi);

// CMA-ES over a subset of the engine weights, each mapped from [0,1] to its range. The evaluator scores a weight set on
// one scenario (higher is better); the fitness is the mean over all scenarios. Candidates are evaluated in parallel.
public static class Tuning
{
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

    public static (EngineWeights weights, double fitness, double baseline) Tune<TScenario>(EngineWeights baseWeights, TunedParameter[] parameters, IReadOnlyList<TScenario> scenarios,
        Func<EngineWeights, TScenario, double> evaluate, int generations, int seed, int? population = null, Action<string>? log = null)
    {
        double Score(EngineWeights w)
        {
            var sum = 0.0;
            foreach (var sc in scenarios)
                sum += evaluate(w, sc);
            return sum / scenarios.Count;
        }

        EngineWeights Decode(double[] x)
        {
            var w = baseWeights.Clone();
            for (var i = 0; i < parameters.Length; ++i)
                w.Set(parameters[i].Name, (float)(parameters[i].Lo + Math.Clamp(x[i], 0, 1) * (parameters[i].Hi - parameters[i].Lo)));
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
            var candidates = xs.Select(Decode).ToArray();
            var fit = new double[xs.Length];
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
            log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"gen {g,3}: best {bestFit,10:f2}  mean {fit.Average(),10:f2}  sigma {cma.Sigma:f3}"));
        }
        return (bestW, bestFit, baseline);
    }
}
