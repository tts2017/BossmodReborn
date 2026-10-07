using System.Globalization;
using System.Text;
using BossMod;

// Offline evaluation of the fight-time models over the extracted pulls (.ttk files written by `extract`).
//   TtkEval eval survey <ttkDir> <out.csv>                      one row per pull with the kill evidence
//   TtkEval eval run <ttkDir> <outDir> [--only a,b] [--min-t 20] [--step 5]
internal static class Eval
{
    public enum PriorMode { None, Lopo, Loro, Past, Summary, SummaryPast } // Past: only pulls recorded before this one (what a live session would have had)

    public sealed record ModelSpec(string Name, Func<FightTimeConfig> Config, PriorMode Prior = PriorMode.None, bool BossOnly = false, int PriorCap = 0);

    public readonly record struct Row(int Pull, float T, float Truth, bool Known, float Est, float Frac, float Elapsed, int Feed = 0, int Seen = 0, float M3 = float.NaN, float M4 = float.NaN, int PriorN = 0, float PriorSpread = float.NaN, int Kind = 0, float Lo = float.NaN, float Hi = float.NaN, int Phase = 0, int Gap = 0, int MaxFeed = 0, bool Co = false, int Killed = 0, float Pit = float.NaN, bool Bias = false, bool CoD = false);

    public sealed class Data
    {
        public List<PullRecord> Kills = [];
        public List<PullRecord> All = [];
        public Dictionary<(ushort, string), List<(PullRecord Pull, FightPrior.Kill Kill)>> Groups = [];
        public HashSet<string> TestReplays = [];
        // priors rebuilt from the plugin's replay summaries (timelines\auto\cache) through FightPriorBuilder; filled by LoadSummaries
        public Dictionary<(ushort, string), List<(string Replay, long StartTicks, FightPrior.Kill Kill)>> SummaryGroups = [];
        public string[] Class = [];
    }

    public static string ClassOf(PullRecord p) => p.ContentType switch
    {
        2 => p.CFCName == "the Clyteum" ? "dungeon-clyteum" : "dungeon",
        4 => "trial",
        5 => p.Members == 4 ? "alliance" : "raid8",
        21 => p.Module ? "dd-boss" : "dd-trash",
        _ => "other",
    };

    public static int Run(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: TtkEval eval <survey|run> <ttkDir> <out> ...");
            return 2;
        }
        var data = Load(args[2]);
        var cache = args.SkipWhile(a => a != "--summary-cache").Skip(1).FirstOrDefault();
        if (cache != null)
            LoadSummaries(data, cache);
        if (args[1] == "survey")
        {
            File.WriteAllText(args[3], string.Join(",", Extract.SurveyHeader) + "\n" + string.Join("\n", data.All.Select(p => p.SurveyRow())) + "\n");
            Console.WriteLine($"{data.All.Count} pulls, {data.Kills.Count} kills");
            return 0;
        }
        if (args[1] == "primcmp")
        {
            // replay-built kill (feed end, primary = biggest attackable target) vs the one FightPriorBuilder makes from the summary of the same pull
            var diffs = new List<(float D, string Class, float Dur)>();
            var oidMismatch = 0;
            var fracDiff = new List<float>();
            var n = 0;
            foreach (var (zk, list) in data.SummaryGroups)
                foreach (var (replay, ticks, kill) in list)
                {
                    var pull = data.Kills.FirstOrDefault(p => p.Replay == replay && p.Start.Ticks == ticks);
                    if (pull == null || !data.Groups.TryGetValue((pull.Zone, pull.Key), out var g))
                        continue;
                    var mine = g.FirstOrDefault(x => ReferenceEquals(x.Pull, pull)).Kill;
                    if (mine == null)
                        continue;
                    ++n;
                    diffs.Add((kill.Duration - mine.Duration, ClassOf(pull), mine.Duration));
                    var len = Math.Min(kill.Frac.Length, mine.Frac.Length);
                    for (var i = 0; i < len; ++i)
                    {
                        if (kill.OID[i] != mine.OID[i]) { ++oidMismatch; continue; }
                        fracDiff.Add(MathF.Abs(kill.Frac[i] - mine.Frac[i]));
                    }
                }
            Console.WriteLine($"matched pulls: {n}");
            foreach (var g in diffs.GroupBy(x => x.Class))
            {
                var s = g.Select(x => x.D).Order().ToList();
                Console.WriteLine($"  {g.Key,-16} n={s.Count,4} duration diff (summary - replay) median {s[s.Count / 2]:f1}s q10 {s[(int)(0.1 * (s.Count - 1))]:f1} q90 {s[(int)(0.9 * (s.Count - 1))]:f1}; |diff|>5s: {s.Count(v => MathF.Abs(v) > 5)}");
            }
            // kill pulls whose summary gives no kill: why
            var cacheDir = args.SkipWhile(a => a != "--summary-cache").Skip(1).First();
            var missing = new List<string>();
            foreach (var replay in data.Kills.Select(p => p.Replay).Distinct())
            {
                var file = ReplaySummaryCache.Read(Path.Combine(cacheDir, replay + ".json"));
                if (file == null) continue;
                foreach (var s in file.Pulls)
                {
                    var pull = data.Kills.FirstOrDefault(p => p.Replay == replay && p.Start.Ticks == s.Start.Ticks);
                    if (pull == null || FightPriorBuilder.KillOf(s) != null) continue;
                    var last = s.Bosses.Where(b => b.History.Count > 0).Select(b => $"{b.OID:X}:{100.0 * b.History[^1].CurHP / Math.Max(1u, b.History[^1].MaxHP):f0}%");
                    missing.Add($"{ClassOf(pull)} basis={pull.KillBasis} dur={pull.KillTime:f0} bosses={string.Join(",", last)}");
                }
            }
            Console.WriteLine($"kill pulls without a summary kill: {missing.Count}");
            foreach (var g in missing.GroupBy(m => m.Split(' ')[0] + " " + m.Split(' ')[1]))
                Console.WriteLine($"  {g.Key}: {g.Count()}  e.g. {g.First()}");
            fracDiff.Sort();
            Console.WriteLine($"track: oid mismatches {oidMismatch}, |frac diff| median {fracDiff[fracDiff.Count / 2]:f3} q90 {fracDiff[(int)(0.9 * (fracDiff.Count - 1))]:f3}");
            return 0;
        }
        if (args[1] == "run")
            return RunModels(data, args[3], args.Skip(4).ToList());
        if (args[1] == "final")
            return Final.Run(data, args[3], args.Skip(4).ToList());
        return 2;
    }

    public static Data Load(string dir)
    {
        var d = new Data();
        foreach (var file in Directory.EnumerateFiles(dir, "*.ttk").Order())
            d.All.AddRange(PullRecord.ReadFile(file));
        d.Kills = d.All.Where(p => p.KillTime > 0).ToList();
        d.Class = d.Kills.Select(ClassOf).ToArray();
        foreach (var p in d.Kills.Where(p => p.Module)) // priors come from encounters (a boss module ran), as the plugin's replay summaries do
        {
            var key = (p.Zone, p.Key);
            if (!d.Groups.TryGetValue(key, out var list))
                d.Groups[key] = list = [];
            list.Add((p, MakeKill(p)));
        }
        // deterministic replay-level split: every second replay (in name order) is a test replay
        var replays = d.Kills.Select(p => p.Replay).Distinct().Order().ToList();
        for (var i = 0; i < replays.Count; ++i)
            if (i % 2 == 1)
                d.TestReplays.Add(replays[i]);
        return d;
    }

    // Per second: OID and HP fraction of the biggest target in the feed (carried over feed gaps).
    public static FightPrior.Kill MakeKill(PullRecord p)
    {
        var len = (int)MathF.Floor(p.KillTime) + 1;
        var oid = new uint[len];
        var frac = new float[len];
        uint lastOid = 0;
        float lastFrac = 1;
        var firstSet = false;
        for (var i = 0; i < len; ++i)
        {
            var fi = 2 * i;
            if (fi < p.Frames.Count && p.Frames[fi].Targets.Count > 0)
            {
                var t = Biggest(p.Frames[fi].Targets);
                lastOid = t.OID;
                lastFrac = (float)t.CurHP / t.MaxHP;
                if (!firstSet)
                {
                    firstSet = true;
                    for (var j = 0; j < i; ++j) { oid[j] = lastOid; frac[j] = 1; }
                }
            }
            oid[i] = lastOid;
            frac[i] = lastFrac;
        }
        return new FightPrior.Kill(p.KillTime, oid, frac);
    }

    private static PullRecord.Target Biggest(List<PullRecord.Target> targets)
    {
        var best = targets[0];
        foreach (var t in targets)
            if (t.MaxHP > best.MaxHP || t.MaxHP == best.MaxHP && (t.OID < best.OID || t.OID == best.OID && t.ID < best.ID))
                best = t;
        return best;
    }

    public static void LoadSummaries(Data d, string cacheDir)
    {
        foreach (var replay in d.All.Select(p => p.Replay).Distinct())
        {
            var file = ReplaySummaryCache.Read(Path.Combine(cacheDir, replay + ".json"));
            if (file == null || file.Failed)
                continue;
            foreach (var s in file.Pulls)
                if (s.Zone != 0 && FightPriorBuilder.KillOf(s) is { } kill)
                {
                    var key = (s.Zone, string.Join(",", s.BossOIDs.Order()));
                    if (!d.SummaryGroups.TryGetValue(key, out var list))
                        d.SummaryGroups[key] = list = [];
                    list.Add((replay, s.Start.Ticks, kill));
                }
        }
    }

    public static FightPrior BuildPrior(Data d, PullRecord p, PriorMode mode, int cap = 0)
    {
        var prior = new FightPrior();
        if (mode is PriorMode.Summary or PriorMode.SummaryPast)
        {
            if (p.Module && d.SummaryGroups.TryGetValue((p.Zone, p.Key), out var sg))
                foreach (var (replay, ticks, kill) in sg)
                    if (!(replay == p.Replay && ticks == p.Start.Ticks) && !(mode == PriorMode.SummaryPast && ticks >= p.Start.Ticks))
                        prior.Add(kill);
            return prior;
        }
        if (mode == PriorMode.None || !p.Module || !d.Groups.TryGetValue((p.Zone, p.Key), out var group))
            return prior;
        IEnumerable<(PullRecord Pull, FightPrior.Kill Kill)> others = group.Where(g => !ReferenceEquals(g.Pull, p) && !(mode == PriorMode.Loro && g.Pull.Replay == p.Replay) && !(mode == PriorMode.Past && g.Pull.Start >= p.Start));
        if (cap > 0)
            others = others.OrderByDescending(g => g.Pull.Start).Take(cap); // the most recent ones
        foreach (var (_, kill) in others)
            prior.Add(kill);
        return prior;
    }

    public static List<Row> Simulate(Data d, int pullIndex, ModelSpec spec, float minT, float step, out int frames, out int knownFrames)
    {
        frames = 0;
        knownFrames = 0;
        var p = d.Kills[pullIndex];
        var est = new FightTimeEstimator(spec.Config());
        if (spec.Prior != PriorMode.None)
            est.Prior = BuildPrior(d, p, spec.Prior, spec.PriorCap);
        List<Row> rows = [];
        var buf = new FightTargetSample[256];
        var nextSample = minT;
        foreach (var f in p.Frames)
        {
            if (f.T >= p.KillTime)
                break;
            var n = 0;
            foreach (var t in f.Targets)
            {
                if (spec.BossOnly && !t.Boss)
                    continue;
                buf[n++] = new(t.ID, t.OID, t.CurHP, t.MaxHP);
            }
            est.Update(f.T, true, buf.AsSpan(0, n));
            if (n > 0)
            {
                ++frames;
                if (est.Estimate.Known)
                    ++knownFrames;
            }
            if (f.T + 1e-4f >= nextSample)
            {
                nextSample += step;
                if (f.Targets.Count == 0)
                    continue; // consumers act only while they have a target
                var e = est.Estimate;
                rows.Add(new(pullIndex, f.T, p.KillTime - f.T, e.Known, e.Known ? e.RemainingSeconds : float.NaN, e.PrimaryHPFraction, e.Elapsed,
                    est.FeedCount, est.SeenTargets, est.DbgM3, est.DbgM4, est.DbgPriorCount, est.DbgPriorSpread, e.Known ? est.BoundKind : 0, e.Known ? e.LowerBound : float.NaN, e.Known ? e.UpperBound : float.NaN, est.PhaseChanges, est.GapCount, est.MaxFeedSeen, false, 0, e.Known ? e.EndsWithinProbability(p.KillTime - f.T) : float.NaN, e.BiasWarning, false));
            }
        }
        return rows;
    }

    public static List<Row> SimulateAll(Data d, ModelSpec spec, float minT, float step, out double knownTimeFrac)
    {
        var parts = new List<Row>[d.Kills.Count];
        long frames = 0, known = 0;
        Parallel.For(0, d.Kills.Count, new ParallelOptions { MaxDegreeOfParallelism = 12 }, i =>
        {
            parts[i] = Simulate(d, i, spec, minT, step, out var fr, out var kn);
            Interlocked.Add(ref frames, fr);
            Interlocked.Add(ref known, kn);
        });
        knownTimeFrac = frames == 0 ? 0 : known / (double)frames;
        return parts.SelectMany(r => r).ToList();
    }

    public static readonly float[] Edges = [5, 10, 20, 40, 80, 160];
    public static readonly float[] TruthEdges = [10, 20, 40, 80, 160];
    public static readonly string[] TruthBuckets = ["<=10", "10-20", "20-40", "40-80", "80-160", ">160"];
    private static int Bucket(float v, float[] edges) { var b = 0; while (b < edges.Length && v >= edges[b]) ++b; return b; }
    public static string TruthBucket(float truth) => TruthBuckets[Bucket(truth, TruthEdges)];

    private static float Quantile(List<float> sorted, double q)
    {
        if (sorted.Count == 0)
            return float.NaN;
        var pos = q * (sorted.Count - 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (float)(pos - lo);
    }

    // Calibrated multipliers: per bucket of the point estimate, the 10th / 90th percentile of truth / estimate on the training rows.
    public const int Kinds = FightTimeConfig.BoundKinds;
    public static int BoundIndex(Row r) => r.Kind * (Edges.Length + 1) + Bucket(r.Est, Edges);

    // Layout [kind * 7 + bucket]. Buckets with too few samples borrow from the neighbours of the same kind, then from all kinds.
    public static (float[] Lower, float[] Upper) FitBounds(IEnumerable<Row> train, double qLo = 0.10, double qHi = 0.90, int minPerBucket = 40)
    {
        var (lo, hi, _) = FitQuantiles(train, minPerBucket);
        return (lo, hi);
    }

    // Per (kind, estimate bucket) cell: the 5 / 10 / 25 / 50 / 75 / 90 / 95 % quantiles of truth / estimate (ln of them in the third array,
    // 7 values per cell, strictly increasing); the 10 / 90 % ones are the Lower / Upper multipliers.
    public static (float[] Lower, float[] Upper, float[] QLn) FitQuantiles(IEnumerable<Row> train, int minPerBucket = 40)
    {
        var nb = Edges.Length + 1;
        var ratios = Enumerable.Range(0, Kinds * nb).Select(_ => new List<float>()).ToArray();
        foreach (var r in train)
            if (r.Known && r.Est > 0)
                ratios[BoundIndex(r)].Add(r.Truth / r.Est);
        var all = ratios.SelectMany(x => x).Order().ToList();
        var lo = new float[Kinds * nb];
        var hi = new float[Kinds * nb];
        var qln = new float[Kinds * nb * 7];
        for (var k = 0; k < Kinds; ++k)
            for (var b = 0; b < nb; ++b)
            {
                var pool = new List<float>(ratios[k * nb + b]);
                var span = 0;
                while (pool.Count < minPerBucket && span < nb)
                {
                    ++span;
                    if (b - span >= 0) pool.AddRange(ratios[k * nb + b - span]);
                    if (b + span < nb) pool.AddRange(ratios[k * nb + b + span]);
                }
                if (pool.Count < minPerBucket)
                    pool = [.. ratios.SelectMany(x => x)]; // no data for this kind at all: the pooled quantiles
                pool.Sort();
                var src = pool.Count > 0 ? pool : all;
                var prev = float.NegativeInfinity;
                for (var q = 0; q < 7; ++q)
                {
                    var v = MathF.Log(MathF.Max(Quantile(src, FightTimeEstimate.QuantileProbs[q]), 1e-3f));
                    v = MathF.Max(v, prev + 0.002f); // strictly increasing nodes
                    qln[(k * nb + b) * 7 + q] = prev = v;
                }
                lo[k * nb + b] = MathF.Exp(qln[(k * nb + b) * 7 + 1]);
                hi[k * nb + b] = MathF.Exp(qln[(k * nb + b) * 7 + 5]);
            }
        return (lo, hi, qln);
    }

    public sealed record Stats(int N, int Known, double MAE, double MedAE, double MedRel, double BiasMean, double BiasMed, double LowerViol, double UpperCov,
        double Prec15, double Rec15, double Prec30, double Rec30, double PrecUp15, double RecUp15, double PrecUp30, double RecUp30, double LbRatio = double.NaN);

    public static Stats Compute(IReadOnlyList<Row> rows, float[]? lowerMult, float[]? upperMult)
    {
        float Lower(Row r) => lowerMult != null ? r.Est * lowerMult[BoundIndex(r)] : r.Lo;
        float Upper(Row r) => upperMult != null ? r.Est * upperMult[BoundIndex(r)] : r.Hi;
        var known = rows.Where(r => r.Known).ToList();
        if (known.Count == 0)
            return new(rows.Count, 0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
        var absErr = known.Select(r => (double)Math.Abs(r.Est - r.Truth)).ToList();
        var signed = known.Select(r => (double)(r.Est - r.Truth)).OrderBy(x => x).ToList();
        var rel = known.Select(r => (double)Math.Abs(r.Est - r.Truth) / r.Truth).OrderBy(x => x).ToList();
        var sortedAbs = absErr.OrderBy(x => x).ToList();
        var lowerViol = known.Count(r => r.Truth < Lower(r)) / (double)known.Count;
        var upperCov = known.Count(r => r.Truth <= Upper(r)) / (double)known.Count;
        var lbRatio = known.Select(r => (double)(Lower(r) / r.Truth)).OrderBy(x => x).ToList();
        (double P, double R) Decide(float x, bool useUpper)
        {
            int tp = 0, fp = 0, fn = 0;
            foreach (var r in rows)
            {
                var actual = r.Truth < x;
                var pred = r.Known && (useUpper ? Upper(r) : r.Est) < x;
                if (pred && actual) ++tp;
                else if (pred) ++fp;
                else if (actual) ++fn;
            }
            return (tp + fp == 0 ? double.NaN : tp / (double)(tp + fp), tp + fn == 0 ? double.NaN : tp / (double)(tp + fn));
        }
        var (p15, r15) = Decide(15, false);
        var (p30, r30) = Decide(30, false);
        var (pu15, ru15) = Decide(15, true);
        var (pu30, ru30) = Decide(30, true);
        return new(rows.Count, known.Count, absErr.Average(), sortedAbs[sortedAbs.Count / 2], rel[rel.Count / 2], signed.Average(), signed[signed.Count / 2],
            lowerViol, upperCov, p15, r15, p30, r30, pu15, ru15, pu30, ru30, lbRatio[lbRatio.Count / 2]);
    }

    private static string F(double v) => double.IsNaN(v) ? "" : v.ToString("0.####", CultureInfo.InvariantCulture);

    public static string StatsRow(string model, string split, string group, Stats s)
        => string.Join(",", [model, split, group, s.N.ToString(), s.Known.ToString(), F(s.N == 0 ? double.NaN : s.Known / (double)s.N), F(s.MAE), F(s.MedAE), F(s.MedRel), F(s.BiasMean), F(s.BiasMed), F(s.LowerViol), F(s.UpperCov),
            F(s.Prec15), F(s.Rec15), F(s.Prec30), F(s.Rec30), F(s.PrecUp15), F(s.RecUp15), F(s.PrecUp30), F(s.RecUp30), F(s.LbRatio)]);

    public const string StatsHeader = "model,split,group,n,known,known_frac,mae,medae,medrel,bias_mean,bias_med,lower_viol,upper_cov,prec15,rec15,prec30,rec30,precup15,recup15,precup30,recup30,lb_ratio_med";

    // kills per content class (all / test replays), replays, and the pulls of the class that were not kills
    public static void WriteClassCounts(Data d, string path)
    {
        var lines = new List<string> { "class,kill_pulls,test_kill_pulls,replays,test_replays,non_kill_pulls,median_kill_s,p10_kill_s,p90_kill_s,boss_sets" };
        var classOf = d.All.ToDictionary(p => p, ClassOf);
        foreach (var g in d.Kills.Select((p, i) => (p, c: d.Class[i])).GroupBy(x => x.c).OrderBy(g => g.Key))
        {
            var k = g.Select(x => x.p.KillTime).Order().ToList();
            var reps = g.Select(x => x.p.Replay).Distinct().ToList();
            lines.Add(string.Join(",", [g.Key, g.Count().ToString(), g.Count(x => d.TestReplays.Contains(x.p.Replay)).ToString(), reps.Count.ToString(), reps.Count(r => d.TestReplays.Contains(r)).ToString(),
                d.All.Count(p => p.KillTime <= 0 && classOf[p] == g.Key).ToString(), F(k[k.Count / 2]), F(k[(int)(0.1 * (k.Count - 1))]), F(k[(int)(0.9 * (k.Count - 1))]), g.Select(x => (x.p.Zone, x.p.Key)).Distinct().Count().ToString()]));
        }
        File.WriteAllLines(path, lines);
    }

    private static int RunModels(Data d, string outDir, List<string> opts)
    {
        Directory.CreateDirectory(outDir);
        float minT = 20, step = 5;
        HashSet<string>? only = null;
        for (var i = 0; i < opts.Count; ++i)
        {
            if (opts[i] == "--only") only = [.. opts[++i].Split(',')];
            else if (opts[i] == "--min-t") minT = float.Parse(opts[++i], CultureInfo.InvariantCulture);
            else if (opts[i] == "--step") step = float.Parse(opts[++i], CultureInfo.InvariantCulture);
        }
        Console.WriteLine($"{d.All.Count} pulls, {d.Kills.Count} kills, {d.TestReplays.Count} test replays");
        WriteClassCounts(d, Path.Combine(outDir, "class_counts.csv"));
        var specs = Models.All().Where(s => only == null || only.Contains(s.Name)).ToList();
        var csv = new StringBuilder(StatsHeader + "\n");
        var samples = new StringBuilder("model,split,class,pull,replay,t,truth,known,est,lo,hi,frac,elapsed,feed,seen,m3,m4,prior_n,prior_spread,kind,phase,gap,maxfeed,co,killed,cod,bias\n");
        foreach (var spec in specs)
        {
            var rows = SimulateAll(d, spec, minT, step, out var knownTime);
            Func<Row, bool> isTest = r => d.TestReplays.Contains(d.Kills[r.Pull].Replay);
            var train = rows.Where(r => !isTest(r)).ToList();
            var (lo, hi) = FitBounds(train);
            foreach (var (split, subset) in new[] { ("test", rows.Where(isTest).ToList()), ("all", rows) })
            {
                csv.AppendLine(StatsRow(spec.Name, split, "overall", Compute(subset, lo, hi)));
                foreach (var g in subset.GroupBy(r => TruthBucket(r.Truth)))
                    csv.AppendLine(StatsRow(spec.Name, split, "bucket:" + g.Key, Compute(g.ToList(), lo, hi)));
                foreach (var g in subset.GroupBy(r => d.Class[r.Pull]))
                {
                    csv.AppendLine(StatsRow(spec.Name, split, "class:" + g.Key, Compute(g.ToList(), lo, hi)));
                    foreach (var gb in g.GroupBy(r => TruthBucket(r.Truth)))
                        csv.AppendLine(StatsRow(spec.Name, split, "class:" + g.Key + "|bucket:" + gb.Key, Compute(gb.ToList(), lo, hi)));
                }
            }
            csv.AppendLine($"# knowntime {spec.Name} {knownTime:f4}");
            csv.AppendLine($"# bounds {spec.Name} lower={string.Join(" ", lo.Select(x => F(x)))} upper={string.Join(" ", hi.Select(x => F(x)))}");
            var overall = Compute(rows.Where(isTest).ToList(), lo, hi);
            Console.WriteLine($"{spec.Name,-34} test n={overall.N,5} known={overall.Known / (double)Math.Max(1, overall.N):P0} knownTime={knownTime:P0} medAE={overall.MedAE:f1} MAE={overall.MAE:f1} medRel={overall.MedRel:f2} bias={overall.BiasMed:f1} lowViol={overall.LowerViol:P0} upCov={overall.UpperCov:P0}");
            if (only != null)
                foreach (var r in rows)
                {
                    var b = r.Known ? BoundIndex(r) : 0;
                    samples.AppendLine(string.Join(",", [spec.Name, isTest(r) ? "test" : "train", d.Class[r.Pull], r.Pull.ToString(), d.Kills[r.Pull].Replay, F(r.T), F(r.Truth), r.Known ? "1" : "0",
                        F(r.Est), r.Known ? F(r.Est * lo[b]) : "", r.Known ? F(r.Est * hi[b]) : "", F(r.Frac), F(r.Elapsed), r.Feed.ToString(), r.Seen.ToString(), F(r.M3), F(r.M4), r.PriorN.ToString(), F(r.PriorSpread), r.Kind.ToString(), r.Phase.ToString(), r.Gap.ToString(), r.MaxFeed.ToString(), r.Co ? "1" : "0", r.Killed.ToString(), r.CoD ? "1" : "0", r.Bias ? "1" : "0"]));
                }
        }
        File.WriteAllText(Path.Combine(outDir, "metrics.csv"), csv.ToString());
        if (only != null)
            File.WriteAllText(Path.Combine(outDir, "samples.csv"), samples.ToString());
        return 0;
    }
}
