using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using EngineTools;
using RprRegression;
using ScenarioResult = RprRegression.ScenarioResult;

namespace RprEngineEval;

// compare: built-in harness policy (the RPR.cs port the harness was written for) vs the rotation engine on the same scenarios
// tune:    CMA-ES on the engine's RPR weights, scored by the harness (MetricsAnalyzer.Score minus hard-fail penalties)
public static class Program
{
    private sealed class Totals
    {
        public int Scenarios;
        public double Score;
        public double Dps;
        public int FailedScenarios;
        public readonly ConcurrentDictionary<string, int> Fails = new();
        public readonly List<double> Micros = [];
        public readonly List<long> Alloc = [];
        public int EngineWindows, FallbackWindows;
    }

    public static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "compare";
        return mode switch
        {
            "compare" => Compare(args),
            "tune" => Tune(args),
            "explain" => Explain(args),
            "prof" => Prof.Run(),
            "plancheck" => PlanCheck.Run(),
            "prof2" => Prof2.Run(),
            "diagnose" => DiagnoseAll(args),
            "failing" => Failing(args),
            _ => throw new ArgumentException("usage: compare|tune [options]")
        };
    }

    private static List<ScenarioDefinition> Scenarios(string[] args)
    {
        var real = int.Parse(Arg(args, "--real", "300"), CultureInfo.InvariantCulture);
        var seed = int.Parse(Arg(args, "--seed", "7"), CultureInfo.InvariantCulture);
        var list = new List<ScenarioDefinition>(ScenarioCatalog.BuildAll());
        if (real > 0)
            list.AddRange(RealRprHarness.Build(real, seed));
        return list;
    }

    private static EngineWeights Weights(string[] args)
    {
        var path = Arg(args, "--weights", "");
        var w = path.Length > 0 ? EngineWeights.Load(path) : RprDefinition.DefaultWeights();
        var depth = Arg(args, "--depth", "");
        if (depth.Length > 0)
            w.HorizonGcds = int.Parse(depth, CultureInfo.InvariantCulture);
        var budget = Arg(args, "--budget", "");
        if (budget.Length > 0)
            w.BudgetMs = float.Parse(budget, CultureInfo.InvariantCulture);
        return w;
    }

    public static ScenarioResult RunBuiltin(ScenarioDefinition sc, List<double>? micros = null, List<long>? alloc = null)
        => new RprRotationEmulator { PolicyMicros = micros, PolicyAllocBytes = alloc }.Run(sc);

    public static ScenarioResult RunEngine(ScenarioDefinition sc, EngineWeights w, RprEnginePolicy? policy = null, List<double>? micros = null, List<long>? alloc = null)
    {
        policy ??= new RprEnginePolicy(w);
        policy.Prepare(sc.Gcd);
        var emu = new RprRotationEmulator { PolicyOverride = policy.Decide, PolicyMicros = micros, PolicyAllocBytes = alloc };
        return emu.Run(sc);
    }

    public static IEnumerable<string> FailBuckets(ScenarioResult r)
    {
        foreach (var f in r.HardFails)
        {
            if (f == HardFailRule.DriftFailure)
                yield return r.Frames.Any(fr => fr.RotationMode == RotationMode.Basic) ? "drift_mode_switch_window" : "drift_full_mode_gluttony_interval";
            else
                yield return f.ToString();
        }
    }

    private static int Compare(string[] args)
    {
        var scenarios = Scenarios(args);
        var w = Weights(args);
        Console.WriteLine($"scenarios: {scenarios.Count} (catalog + real), engine depth {w.HorizonGcds}, budget {w.BudgetMs} ms");

        var all = (builtin: new Totals(), engine: new Totals());
        var covered = (builtin: new Totals(), engine: new Totals());
        var gate = new object();
        Parallel.ForEach(scenarios, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, sc =>
        {
            var mb = new List<double>(); var ab = new List<long>();
            var me = new List<double>(); var ae = new List<long>();
            var rb = RunBuiltin(sc, mb, ab);
            var policy = new RprEnginePolicy(w);
            var re = RunEngine(sc, w, policy, me, ae);
            var isCovered = RprEnginePolicy.Covers(sc) && sc.InitialMode == RotationMode.Full;
            lock (gate)
            {
                Add(all.builtin, rb, mb, ab, 0, 0);
                Add(all.engine, re, me, ae, policy.EngineWindows, policy.FallbackWindows);
                if (isCovered)
                {
                    Add(covered.builtin, rb, mb, ab, 0, 0);
                    Add(covered.engine, re, me, ae, policy.EngineWindows, policy.FallbackWindows);
                }
            }
        });

        var report = new StringBuilder();
        Report(report, "all scenarios (engine falls back to the built-in policy outside its coverage)", all.builtin, all.engine);
        Report(report, "covered: level 100, normal rotation, full mode (engine decides every window it can)", covered.builtin, covered.engine);
        Console.Write(report);
        var outPath = Arg(args, "--out", "");
        if (outPath.Length > 0)
            File.WriteAllText(outPath, report.ToString());
        return 0;
    }

    private static void Add(Totals t, ScenarioResult r, List<double> micros, List<long> alloc, int engineWindows, int fallbackWindows)
    {
        ++t.Scenarios;
        t.Score += r.Score;
        t.Dps += r.Metrics.TotalPotencyEstimate / r.Scenario.KillTime;
        if (r.HardFails.Count > 0)
            ++t.FailedScenarios;
        foreach (var b in FailBuckets(r).Distinct())
            t.Fails.AddOrUpdate(b, 1, (_, v) => v + 1);
        t.Micros.AddRange(micros);
        t.Alloc.AddRange(alloc);
        t.EngineWindows += engineWindows;
        t.FallbackWindows += fallbackWindows;
    }

    private static void Report(StringBuilder sb, string title, Totals b, Totals e)
    {
        string F(double v) => v.ToString("f1", CultureInfo.InvariantCulture);
        var (bm, bp) = ScenarioRunner.Stats(b.Micros);
        var (em, ep) = ScenarioRunner.Stats(e.Micros);
        sb.AppendLine($"## {title}");
        sb.AppendLine();
        sb.AppendLine($"scenarios: {b.Scenarios}; engine decided {e.EngineWindows} GCD windows, fell back on {e.FallbackWindows}");
        sb.AppendLine();
        sb.AppendLine("| metric | built-in (RPR.cs port) | engine |");
        sb.AppendLine("|---|---:|---:|");
        sb.AppendLine($"| mean potency/s (DPS proxy) | {F(b.Dps / Math.Max(1, b.Scenarios))} | {F(e.Dps / Math.Max(1, e.Scenarios))} |");
        sb.AppendLine($"| mean harness score | {F(b.Score / Math.Max(1, b.Scenarios))} | {F(e.Score / Math.Max(1, e.Scenarios))} |");
        sb.AppendLine($"| scenarios with any hard fail | {b.FailedScenarios} | {e.FailedScenarios} |");
        foreach (var key in b.Fails.Keys.Union(e.Fails.Keys).OrderBy(k => k))
            sb.AppendLine($"| fail: {key} | {b.Fails.GetValueOrDefault(key)} | {e.Fails.GetValueOrDefault(key)} |");
        sb.AppendLine($"| policy time per GCD window, mean (us) | {F(bm)} | {F(em)} |");
        sb.AppendLine($"| policy time per GCD window, p99 (us) | {F(bp)} | {F(ep)} |");
        sb.AppendLine($"| heap allocated per GCD window (bytes) | {F(b.Alloc.Count == 0 ? 0 : b.Alloc.Average())} | {F(e.Alloc.Count == 0 ? 0 : e.Alloc.Average())} |");
        sb.AppendLine();
    }

    private static int Tune(string[] args)
    {
        var generations = int.Parse(Arg(args, "--generations", "8"), CultureInfo.InvariantCulture);
        var seed = int.Parse(Arg(args, "--seed", "1"), CultureInfo.InvariantCulture);
        var limit = int.Parse(Arg(args, "--limit", "80"), CultureInfo.InvariantCulture);
        var outPath = Arg(args, "--out", Path.Combine(Environment.CurrentDirectory, "weights-RPR.json"));
        var baseWeights = Weights(args);
        baseWeights.BudgetMs = 1000; // deterministic: the fixed depth always completes
        var rng = new Random(seed);
        var pool = Scenarios(args).Where(s => RprEnginePolicy.Covers(s) && s.InitialMode == RotationMode.Full).ToList();
        var scenarios = pool.OrderBy(_ => rng.Next()).Take(limit).ToList();
        Console.WriteLine($"tuning RPR weights on {scenarios.Count} covered scenarios (of {pool.Count}), depth {baseWeights.HorizonGcds}, {generations} generations");

        static double Fitness(ScenarioResult r) => r.Score / r.Scenario.KillTime - 50.0 * r.HardFails.Count;
        // ranges widened where the first run pinned a value to its bound (OverCap, LambdaScale, FillerScale, GluttonyCD, Shroud)
        TunedParameter[] parameters =
        [
            new(nameof(EngineWeights.OverCap), 0, 10),
            new(nameof(EngineWeights.Combo), 0, 2),
            new(nameof(EngineWeights.LambdaScale), 0.05f, 1.5f),
            new(nameof(EngineWeights.TargetPull), 0, 1),
            new(nameof(EngineWeights.SwitchMargin), 0, 100),
            new(nameof(EngineWeights.FillerScale), 0.05f, 1.5f),
            new(nameof(EngineWeights.BurstBias), 0, 3),
            new(nameof(EngineWeights.StatusRemainder), 0, 2),
            new("CooldownValue.GluttonyCD", -600, 2500),
            new("StatusValue.DeathsDesign", 0, 120),
            new("GaugeValue.Shroud", -2000, 1500),
        ];
        var best = Tuning.Tune(baseWeights, parameters, scenarios, (w, sc) => Fitness(RunEngine(sc, w)), generations, seed, log: Console.WriteLine);

        var tuned = best.weights.Clone();
        tuned.BudgetMs = 0.5f;
        File.WriteAllText(outPath, tuned.ToJson());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"fitness baseline {best.baseline:f2} -> tuned {best.fitness:f2}; written {outPath}"));
        return 0;
    }

    private static int Explain(string[] args)
    {
        var name = Arg(args, "--scenario", "");
        var sc = Scenarios(args).First(s => name.Length == 0 ? RprEnginePolicy.Covers(s) && s.InitialMode == RotationMode.Full : s.Name == name);
        var policy = new RprEnginePolicy(Weights(args)) { DebugFrom = double.Parse(Arg(args, "--debug-from", "1e9"), CultureInfo.InvariantCulture), DebugTo = double.Parse(Arg(args, "--debug-to", "-1"), CultureInfo.InvariantCulture), DebugLines = Arg(args, "--lines", "").Split(";", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(",")).ToList() };
        var r = RunEngine(sc, Weights(args), policy);
        Console.WriteLine($"{sc.Name}: fails [{string.Join(",", FailBuckets(r))}] score {r.Score:f0}");
        var n = int.Parse(Arg(args, "--frames", "60"), CultureInfo.InvariantCulture);
        foreach (var f in r.Frames.Take(n))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{f.Time,6:f1} {f.SelectedGcd,-22} [{string.Join(",", f.SelectedOgcds)}] red {f.RedGauge} blue {f.BlueGauge} lem {f.BlueSouls}/{f.PurpleSouls} dd {f.DeathsDesignLeft:f0} ac {f.ArcaneCircleLeft:f0}/{f.ArcaneCircleReadyIn:f0} reaver {f.ReaverState} ss {f.SoulSliceCharges:f2} {(f.HardFails.Count > 0 ? "FAIL " + string.Join(",", f.HardFails) : "")}"));
        return 0;
    }

    private static int Failing(string[] args)
    {
        var w = Weights(args);
        var rule = Enum.Parse<HardFailRule>(Arg(args, "--rule", "IllegalAction"));
        Parallel.ForEach(Scenarios(args).Where(s => RprEnginePolicy.Covers(s) && s.InitialMode == RotationMode.Full), sc =>
        {
            if (RunEngine(sc, w).HardFails.Contains(rule))
                Console.WriteLine(sc.Name);
        });
        return 0;
    }

    private static int DiagnoseAll(string[] args)
    {
        var w = Weights(args);
        var counts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        Parallel.ForEach(Scenarios(args).Where(s => RprEnginePolicy.Covers(s) && s.InitialMode == RotationMode.Full), sc =>
        {
            var res = RunEngine(sc, w);
            var bucket = Arg(args, "--bucket", "illegal");
            IEnumerable<string> raw = bucket switch { "gauge" => res.HardFails.Contains(HardFailRule.GaugeFailure) ? DiagnoseBuckets.Gauge(res) : [], "dd" => res.HardFails.Contains(HardFailRule.DeathsDesignFailure) ? (DiagnoseBuckets.Dd(res).ToList() is { Count: > 0 } dl ? dl : ["dd_unmirrored"]) : [], "burst" => res.HardFails.Contains(HardFailRule.BurstFailure) ? (DiagnoseBuckets.Burst(res).ToList() is { Count: > 0 } bl ? bl : ["burst_unmirrored"]) : [], _ => Diagnose.Illegal(res) };
            var lines = Arg(args, "--detail", "") == "1" ? raw.Select(l => l + " | " + sc.Name) : raw.Select(l => l.IndexOf(" (") is var p && p >= 0 ? l[..p] : l);
            foreach (var k in lines.Distinct())
                counts.AddOrUpdate(k, 1, (_, v) => v + 1);
        });
        foreach (var (k, v) in counts.OrderByDescending(kv => kv.Value))
            Console.WriteLine($"{v,5} {k}");
        return 0;
    }

    private static string Arg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
