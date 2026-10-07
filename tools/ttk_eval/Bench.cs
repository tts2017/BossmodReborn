using System.Diagnostics;
using BossMod;

// Cost of FightTimeEstimator.Update in the recommended configuration: every extracted pull is replayed at 60 updates per second (the
// 0.5 s feed frames repeated), with and without a prior (the biggest one: all other kills of the fight). Allocation is measured on the
// calling thread over the whole timed loop; time per call from a high-resolution timestamp around each call.
//   TtkEval bench <ttkDir>
internal static class Bench
{
    private static long allocated;
    private static bool Consume;
    public static float Sink;

    public static int Run(string ttkDir)
    {
        var d = Eval.Load(ttkDir);
        foreach (var (withPrior, consume) in new[] { (false, false), (true, false), (true, true) })
        {
            Consume = consume;
            // warm-up (JIT, tiered compilation) on a few pulls
            for (var i = 0; i < Math.Min(30, d.Kills.Count); ++i)
                Replay(d, i, withPrior, null, out _);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var durations = new List<long>(16_000_000);
            allocated = 0;
            var gcBefore = GC.CollectionCount(0);
            long calls = 0;
            var sw = Stopwatch.StartNew();
            for (var i = 0; i < d.Kills.Count; ++i)
            {
                Replay(d, i, withPrior, durations, out var n);
                calls += n;
            }
            sw.Stop();
            var alloc = allocated; // only inside Update calls: constructing an estimator per pull (about 27 KB) is not steady state
            durations.Sort();
            double Tick(double ticks) => ticks * 1e9 / Stopwatch.Frequency;
            double Q(double q) => Tick(durations[(int)Math.Min(durations.Count - 1, q * durations.Count)]);
            Console.WriteLine($"withPrior={withPrior} consumerCalls={consume}: calls={calls:N0} total={sw.Elapsed.TotalSeconds:f2}s mean={Tick(durations.Average()):f0}ns p50={Q(0.5):f0}ns p99={Q(0.99):f0}ns p99.9={Q(0.999):f0}ns max={Tick(durations[^1]) / 1000:f1}us over1ms={durations.Count(x => Tick(x) > 1e6)} allocatedBytesInUpdate={alloc} gen0GCs={GC.CollectionCount(0) - gcBefore}");
        }
        return 0;
    }

    private static void Replay(Eval.Data d, int pull, bool withPrior, List<long>? durations, out int calls)
    {
        var p = d.Kills[pull];
        var est = new FightTimeEstimator(FightTimeConfig.Recommended());
        if (withPrior)
            est.Prior = Eval.BuildPrior(d, p, Eval.PriorMode.Lopo);
        var buf = new FightTargetSample[256];
        calls = 0;
        foreach (var f in p.Frames)
        {
            if (f.T >= p.KillTime)
                break;
            var n = 0;
            foreach (var t in f.Targets)
                buf[n++] = new(t.ID, t.OID, t.CurHP, t.MaxHP);
            for (var s = 0; s < 30; ++s)
            {
                var now = f.T + s / 60.0;
                var a0 = durations != null ? GC.GetAllocatedBytesForCurrentThread() : 0;
                var t0 = Stopwatch.GetTimestamp();
                est.Update(now, true, buf.AsSpan(0, n));
                if (Consume)
                {
                    // what a rotation module does with the estimate each frame: probability, attackable remaining, a bound
                    var e = est.Estimate.WithDowntime(12f, 20f);
                    Sink += e.EndsWithinProbability(15f) + e.RemainingAttackable + e.Quantile(0.3f) + (e.BiasWarning ? 1 : 0);
                }
                var dt = Stopwatch.GetTimestamp() - t0;
                if (durations != null)
                    allocated += GC.GetAllocatedBytesForCurrentThread() - a0;
                durations?.Add(dt);
                ++calls;
            }
        }
    }
}
