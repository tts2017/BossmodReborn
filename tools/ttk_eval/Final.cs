using System.Globalization;
using System.Text;
using BossMod;

// The recommended configuration (FightTimeConfig.Recommended): calibrates its bounds and reports them.
//   TtkEval eval final <ttkDir> <outDir> [--verify]
// Without --verify the bound tables are fitted: on the training replays (reported on the test replays) and on everything (printed as the
// C# literal that FightTimeConfig.Recommended ships). With --verify the shipped tables are used as they are (the estimator's own bounds).
internal static class Final
{
    private static string F2(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture) + "f";

    private static FightTimeConfig Raw(FightTimeModel model)
    {
        var c = FightTimeConfig.Recommended();
        c.Model = model;
        c.LowerMult = [.. Enumerable.Repeat(1f, FightTimeConfig.BoundKinds * 7)];
        c.UpperMult = [.. Enumerable.Repeat(1f, FightTimeConfig.BoundKinds * 7)];
        return c;
    }

    // kinds 0 / 1 / 5 (rate only) from the no-prior run, which sees every fight; kinds 2..4 (prior-led) from the hybrid run
    private static (float[] Lo, float[] Hi, float[] QLn) Merge((float[] Lo, float[] Hi, float[] QLn) rate, (float[] Lo, float[] Hi, float[] QLn) prior)
    {
        var lo = (float[])prior.Lo.Clone();
        var hi = (float[])prior.Hi.Clone();
        var q = (float[])prior.QLn.Clone();
        foreach (var k in new[] { 0, 1, 5 })
        {
            Array.Copy(rate.Lo, k * 7, lo, k * 7, 7);
            Array.Copy(rate.Hi, k * 7, hi, k * 7, 7);
            Array.Copy(rate.QLn, k * 49, q, k * 49, 49);
        }
        return (lo, hi, q);
    }

    private static string Literal(string name, float[] a)
    {
        var sb = new StringBuilder($"    public float[] {name} =\n    [\n");
        for (var k = 0; k < FightTimeConfig.BoundKinds; ++k)
            sb.Append($"        {string.Join(", ", a.Skip(k * 7).Take(7).Select(F))},{(k == 0 ? " // single target" : k == 1 ? " // several targets" : k == 5 ? " // multi-phase fight (bias warning)" : $" // prior-led tier {k - 1}")}\n");
        return sb.Append("    ];").ToString();
    }

    // ln(truth / estimate) at the 7 quantile nodes of every kind / bucket cell: 7 cells of 7 values per kind
    private static string QuantileLiteral(float[] q)
    {
        var sb = new StringBuilder("    public static readonly float[] QuantileLnTable =\n    [\n");
        for (var k = 0; k < FightTimeConfig.BoundKinds; ++k)
        {
            sb.Append($"        // kind {k}\n");
            for (var b = 0; b < 7; ++b)
                sb.Append($"        {string.Join(", ", q.Skip((k * 7 + b) * 7).Take(7).Select(F))},\n");
        }
        return sb.Append("    ];").ToString();
    }

    public static int Run(Eval.Data d, string outDir, List<string> opts)
    {
        Directory.CreateDirectory(outDir);
        var verify = opts.Contains("--verify");
        const float minT = 20, step = 5;
        Func<Eval.Row, bool> isTest = r => d.TestReplays.Contains(d.Kills[r.Pull].Replay);
        var csv = new StringBuilder(Eval.StatsHeader + "\n");
        var modes = new[] { (Eval.PriorMode.Lopo, "lopo"), (Eval.PriorMode.Loro, "loro"), (Eval.PriorMode.Past, "past"), (Eval.PriorMode.Summary, "summary"), (Eval.PriorMode.SummaryPast, "summarypast") };

        List<Eval.Row> Sim(string name, FightTimeModel model, Eval.PriorMode mode, bool unity)
        {
            var spec = new Eval.ModelSpec(name, () => unity ? Raw(model) : FightTimeConfig.Recommended() with { }, mode);
            return Eval.SimulateAll(d, spec, minT, step, out _);
        }

        void Report(string name, List<Eval.Row> rows, float[]? lo, float[]? hi, string tag)
        {
            foreach (var (split, subset) in new[] { ("test", rows.Where(isTest).ToList()), ("all", rows) })
            {
                csv.AppendLine(Eval.StatsRow(name + tag, split, "overall", Eval.Compute(subset, lo, hi)));
                foreach (var g in subset.GroupBy(r => Eval.TruthBucket(r.Truth)))
                    csv.AppendLine(Eval.StatsRow(name + tag, split, "bucket:" + g.Key, Eval.Compute(g.ToList(), lo, hi)));
                foreach (var g in subset.GroupBy(r => d.Class[r.Pull]))
                {
                    csv.AppendLine(Eval.StatsRow(name + tag, split, "class:" + g.Key, Eval.Compute(g.ToList(), lo, hi)));
                    foreach (var gb in g.GroupBy(r => Eval.TruthBucket(r.Truth)))
                        csv.AppendLine(Eval.StatsRow(name + tag, split, "class:" + g.Key + "|bucket:" + gb.Key, Eval.Compute(gb.ToList(), lo, hi)));
                }
            }
            var t = Eval.Compute(rows.Where(isTest).ToList(), lo, hi);
            Console.WriteLine($"{name + tag,-26} test n={t.N} known={t.Known / (double)t.N:P1} medAE={t.MedAE:f2} MAE={t.MAE:f2} medRel={t.MedRel:f3} bias={t.BiasMed:f2} lowViol={t.LowerViol:P1} upCov={t.UpperCov:P1} lbRatio={t.LbRatio:f2} p15={t.Prec15:f2}/{t.Rec15:f2} p30={t.Prec30:f2}/{t.Rec30:f2}");
        }

        // PIT histogram: EndsWithinProbability(real remaining) should be uniform; 10 bins, per class, test replays only
        void PitReport(string name, List<Eval.Row> rows)
        {
            foreach (var g in rows.Where(isTest).Where(r => r.Known && !float.IsNaN(r.Pit)).GroupBy(r => "all").Concat(rows.Where(isTest).Where(r => r.Known && !float.IsNaN(r.Pit)).GroupBy(r => d.Class[r.Pull])))
            {
                var bins = new int[10];
                foreach (var r in g)
                    ++bins[Math.Min(9, (int)(r.Pit * 10))];
                var n = g.Count();
                var ks = 0.0;
                var cum = 0;
                for (var i = 0; i < 10; ++i)
                {
                    cum += bins[i];
                    ks = Math.Max(ks, Math.Abs(cum / (double)n - (i + 1) / 10.0));
                }
                csv.AppendLine($"# pit {name} {g.Key} n={n} ks={ks:f3} bins={string.Join(" ", bins.Select(b => (b / (double)n).ToString("0.000", CultureInfo.InvariantCulture)))}");
            }
        }

        if (verify)
        {
            foreach (var (mode, tag) in modes.Prepend((Eval.PriorMode.None, "none")))
            {
                var spec = new Eval.ModelSpec("Recommended_" + tag, () => FightTimeConfig.Recommended(), mode);
                var rows = Eval.SimulateAll(d, spec, minT, step, out var kt);
                Report("Recommended", rows, null, null, "_" + tag);
                PitReport("Recommended_" + tag, rows);
                if (opts.Contains("--dump"))
                    File.WriteAllLines(Path.Combine(outDir, $"samples_{tag}.csv"), rows.Where(r => r.Known).Select(r => string.Join(",", [d.Class[r.Pull], d.Kills[r.Pull].Replay, F2(r.T), F2(r.Truth), F2(r.Est), F2(r.Lo), F2(r.Hi), r.Bias ? "1" : "0", r.Kind.ToString(), r.Pit.ToString("0.####", CultureInfo.InvariantCulture), r.Phase.ToString(), r.Gap.ToString()])).Prepend("class,replay,t,truth,est,lo,hi,bias,kind,pit,phase,gap"));
                csv.AppendLine($"# knowntime Recommended_{tag} {kt:f4}");
            }
            File.WriteAllText(Path.Combine(outDir, "final_verify.csv"), csv.ToString());
            return 0;
        }

        var rows3 = Sim("M3_final", FightTimeModel.PhaseAware, Eval.PriorMode.None, true);
        var fit3 = Eval.FitQuantiles(rows3.Where(r => !isTest(r)));
        var perMode = new Dictionary<string, List<Eval.Row>>();
        foreach (var (mode, tag) in modes)
            perMode[tag] = Sim("M5_final_" + tag, FightTimeModel.Hybrid, mode, true);
        var fit5 = Eval.FitQuantiles(perMode["summary"].Where(r => !isTest(r)));
        var trainFit = Merge(fit3, fit5);
        Console.WriteLine("== bounds fitted on the training replays, reported on the test replays");
        Report("M3_final", rows3, trainFit.Lo, trainFit.Hi, "");
        foreach (var (_, tag) in modes)
            Report("M5_final", perMode[tag], trainFit.Lo, trainFit.Hi, "_" + tag);
        var allFit = Merge(Eval.FitQuantiles(rows3), Eval.FitQuantiles(perMode["summary"]));
        Console.WriteLine("== bounds fitted on all pulls (the shipped table); in-sample");
        Report("M3_final", rows3, allFit.Lo, allFit.Hi, "_allfit");
        Report("M5_final", perMode["summary"], allFit.Lo, allFit.Hi, "_summary_allfit");
        var literal = Literal("LowerMult", allFit.Lo) + "\n" + Literal("UpperMult", allFit.Hi) + "\n" + QuantileLiteral(allFit.QLn);
        File.WriteAllText(Path.Combine(outDir, "bounds_literal.txt"), literal + "\n");
        File.WriteAllText(Path.Combine(outDir, "final_metrics.csv"), csv.ToString());
        Console.WriteLine(literal);
        return 0;
    }
}
