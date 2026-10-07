using System.Globalization;
using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;

namespace BlmRegression;

// engine-compare [--weights <json>] [--budget ms] [--pattern p] [--detail]: built-in policy vs BLM [Engine] on the regression
// scenarios (all, or those matching the pattern). Hard fails by rule, policy time and allocations per frame.
public static class BlmEngineCompare
{
    public static int Run(string[] args)
    {
        string Arg(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var weightsPath = Arg("--weights", "");
        var w = weightsPath.Length > 0 ? EngineWeights.Load(weightsPath) : BlmDefinition.DefaultWeights();
        var budget = Arg("--budget", "");
        if (budget.Length > 0)
            w.BudgetMs = float.Parse(budget, CultureInfo.InvariantCulture);
        var pattern = Arg("--pattern", "");
        var detail = args.Contains("--detail");
        var scenarios = pattern.Length > 0 ? BlmScenarioCatalog.Select(pattern) : BlmScenarioCatalog.BuildAll();

        var rows = new List<(BlmScenario Sc, BlmScenarioResult Old, BlmScenarioResult New, int EngineFrames)>();
        var micros = (old: new List<double>(), eng: new List<double>());
        var alloc = (old: new List<long>(), eng: new List<long>());
        var gate = new object();
        Parallel.ForEach(scenarios, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, sc =>
        {
            var mo = new List<double>(); var ao = new List<long>();
            var me = new List<double>(); var ae = new List<long>();
            var old = new BlmRotationEmulator { PolicyOverride = (_, _) => null, PolicyMicros = mo, PolicyAllocBytes = ao }.Run(sc);
            var policy = new BlmEnginePolicy(w.Clone());
            var eng = new BlmRotationEmulator { PolicyOverride = policy.Decide, PolicyMicros = me, PolicyAllocBytes = ae }.Run(sc);
            lock (gate)
            {
                rows.Add((sc, old, eng, policy.EngineFrames));
                if (policy.EngineFrames > 0)
                {
                    micros.old.AddRange(mo); alloc.old.AddRange(ao);
                    micros.eng.AddRange(me); alloc.eng.AddRange(ae);
                }
            }
        });

        var covered = rows.Where(r => r.EngineFrames > 0).ToList();
        Console.WriteLine($"scenarios: {rows.Count}, engine-covered: {covered.Count}, engine depth {w.HorizonGcds}, budget {w.BudgetMs} ms");
        void Table(string title, List<(BlmScenario Sc, BlmScenarioResult Old, BlmScenarioResult New, int EngineFrames)> set)
        {
            Console.WriteLine($"## {title} ({set.Count} scenarios)");
            Console.WriteLine("| metric | built-in (xan port) | engine |");
            Console.WriteLine("|---|---:|---:|");
            Console.WriteLine($"| scenarios with any hard fail | {set.Count(r => r.Old.HardFails.Count > 0)} | {set.Count(r => r.New.HardFails.Count > 0)} |");
            Console.WriteLine($"| GCDs (sum) | {set.Sum(r => r.Old.Metrics.GcdCount)} | {set.Sum(r => r.New.Metrics.GcdCount)} |");
            Console.WriteLine($"| GCD uptime (mean) | {set.Average(r => r.Old.Metrics.GcdUptime):f4} | {set.Average(r => r.New.Metrics.GcdUptime):f4} |");
            Console.WriteLine($"| soft regressions (sum) | {set.Sum(r => r.Old.Metrics.SoftRegressionCount)} | {set.Sum(r => r.New.Metrics.SoftRegressionCount)} |");
            var rules = set.SelectMany(r => r.Old.HardFails.Concat(r.New.HardFails)).Distinct().OrderBy(x => x);
            foreach (var rule in rules)
                Console.WriteLine($"| fail: {rule} (scenarios) | {set.Count(r => r.Old.HardFails.Contains(rule))} | {set.Count(r => r.New.HardFails.Contains(rule))} |");
        }
        if (covered.Count > 0)
        {
            Table("engine-covered", covered);
            static (double mean, double p99) Stats(List<double> xs) { if (xs.Count == 0) return (0, 0); var s = xs.OrderBy(x => x).ToList(); return (s.Average(), s[(int)Math.Min(s.Count - 1, Math.Ceiling(s.Count * 0.99) - 1)]); }
            var so = Stats(micros.old); var se = Stats(micros.eng);
            Console.WriteLine($"| policy time per frame, mean (us) | {so.mean:f1} | {se.mean:f1} |");
            Console.WriteLine($"| policy time per frame, p99 (us) | {so.p99:f1} | {se.p99:f1} |");
            Console.WriteLine($"| heap allocated per frame (bytes) | {(alloc.old.Count > 0 ? alloc.old.Average() : 0):f1} | {(alloc.eng.Count > 0 ? alloc.eng.Average() : 0):f1} |");
        }
        if (detail)
        {
            foreach (var r in covered.Where(r => r.New.HardFails.Count > 0).OrderBy(r => r.Sc.Name))
            {
                var first = r.New.Frames.FirstOrDefault(f => f.HardFails.Count > 0);
                Console.WriteLine($"FAIL {r.Sc.Name}: {string.Join(",", r.New.HardFails.Distinct())}" + (first != null ? $" first t={first.Time} gcd={first.SelectedGcd} ogcds={string.Join("+", first.SelectedOgcds)} el={first.Element} mp={first.MP} h={first.Hearts} pg={first.Polyglot} as={first.AstralSoul} px={first.Paradox} move={first.ForcedMoveNow} tgt={first.TargetAvailable}" : ""));
            }
        }
        return 0;
    }
}
