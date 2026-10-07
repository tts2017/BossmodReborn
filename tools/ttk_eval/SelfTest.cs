using BossMod;

// Synthetic scenarios for FightTimeEstimator: `TtkEval selftest` (exit code 0 = all passed).
internal static class SelfTest
{
    private static int _failures;
    private static int _checks;

    private static void Check(bool ok, string what)
    {
        ++_checks;
        if (!ok)
        {
            ++_failures;
            Console.WriteLine("FAIL: " + what);
        }
    }

    private static void Near(double actual, double expected, double relTol, string what)
        => Check(Math.Abs(actual - expected) <= relTol * Math.Abs(expected), $"{what}: got {actual:f2}, expected {expected:f2} +-{relTol:P0}");

    private const ulong Boss = 1;

    // Runs `seconds` of a fight at `fps`: hp(t) per target id list. Returns the estimator.
    private static FightTimeEstimator Run(FightTimeConfig? cfg, double seconds, int fps, Func<double, FightTargetSample[]> feed, double startAt = 0, FightTimeEstimator? est = null, Action<double, FightTimeEstimator>? each = null)
    {
        est ??= new FightTimeEstimator(cfg);
        var steps = (int)(seconds * fps);
        for (var i = 0; i <= steps; ++i)
        {
            var t = startAt + (double)i / fps;
            est.Update(t, true, feed(t - startAt));
            each?.Invoke(t, est);
        }
        return est;
    }

    private static FightTimeEstimate Steady(FightTimeConfig cfg, double seconds, ulong id = Boss, float rate = 10_000, uint max = 1_000_000)
        => Run(cfg, seconds, 30, t => [new(id, 1, (uint)(max - rate * t), max)]).Estimate;

    // EndsWithinProbability / Quantile: monotone, 0 when unknown, consistent with the bounds, inverse of each other
    private static void ProbabilityTests()
    {
        var unknown = FightTimeEstimate.Unknown;
        Check(unknown.EndsWithinProbability(30) == 0 && unknown.Quantile(0.5f) == float.MaxValue, "unknown: probability 0, quantile float.MaxValue");
        var e = Steady(FightTimeConfig.Recommended(), 40);
        Check(e.Known, "steady fight known for the probability tests");
        var last = -1f;
        var monotone = true;
        for (var s = 0f; s <= 300; s += 2.5f)
        {
            var p = e.EndsWithinProbability(s);
            monotone &= p >= last - 1e-6f && p is >= 0 and <= 1;
            last = p;
        }
        Check(monotone, "EndsWithinProbability is monotone in 0..1");
        Near(e.EndsWithinProbability(e.LowerBound), 0.10, 0.2, "P(LowerBound) ~ 0.10");
        Near(e.EndsWithinProbability(e.UpperBound), 0.90, 0.2, "P(UpperBound) ~ 0.90");
        Near(e.Quantile(0.10f), e.LowerBound, 0.02, "Quantile(0.1) == LowerBound");
        Near(e.Quantile(0.90f), e.UpperBound, 0.02, "Quantile(0.9) == UpperBound");
        Check(e.EndsWithinProbability(0) == 0 && e.EndsWithinProbability(-5) == 0, "no probability of ending in no time");
        Check(e.EndsWithinProbability(1e6f) > 0.99f, "ends within a very long time with certainty");
        foreach (var p in new[] { 0.02f, 0.2f, 0.5f, 0.8f, 0.98f })
            Near(e.EndsWithinProbability(e.Quantile(p)), p, 0.05, $"probability of the quantile {p}");
        // without a table (the harness constructs estimates this way): log-normal from the bounds
        var ln = new FightTimeEstimate(FightTimeModel.PhaseAware, 60, 60 * MathF.Exp(-1.2816f * 0.2f), 60 * MathF.Exp(1.2816f * 0.2f), 0.5f, 100, 0.5f);
        Near(ln.EndsWithinProbability(60), 0.5, 0.02, "log-normal median");
        Near(ln.EndsWithinProbability(ln.UpperBound), 0.9, 0.02, "log-normal upper");
        var exact = new FightTimeEstimate(FightTimeModel.Prior, 25, 25, 25, 1, 100, 0.2f);
        Check(exact.EndsWithinProbability(24.9f) == 0 && exact.EndsWithinProbability(25.1f) == 1, "a perfect estimate is a step");
    }

    // RemainingAttackable / WithDowntime
    private static void DowntimeTests()
    {
        var e = new FightTimeEstimate(FightTimeModel.PhaseAware, 100, 70, 150, 0.8f, 50, 0.5f);
        Near(e.RemainingAttackable, 100, 0.001, "no downtime: attackable == remaining");
        var d = e.WithDowntime(30, 50);
        Near(d.RemainingAttackable, 80, 0.001, "20 s of announced downtime inside the remaining time");
        Near(d.RemainingSeconds, 100, 0.001, "RemainingSeconds is unchanged");
        Near(d.UpperBoundAttackable, 130, 0.001, "attackable upper bound");
        Check(d.DowntimeBeforeEnd && d.Confidence < e.Confidence * 0.51f, "a downtime before the end halves the confidence");
        Near(e.WithDowntime(30, float.MaxValue).RemainingAttackable, 100 - FightTimeEstimate.UnknownReturnDowntime, 0.001, "unknown return counts as the minimum loss");
        Near(e.WithDowntime(90, 200).RemainingAttackable, 90, 0.001, "a loss that runs past the end only counts up to the end");
        Check(e.WithDowntime(150, 160).RemainingAttackable == 100 && !e.WithDowntime(150, 160).DowntimeBeforeEnd, "a loss after the estimated end is ignored");
        Check(e.WithDowntime(float.MaxValue, float.MaxValue).RemainingAttackable == 100, "no announcement: unchanged");
        var twice = d.WithDowntime(30, 45);
        Near(twice.RemainingAttackable, 80, 0.001, "a second, smaller announcement keeps the larger downtime");
        Near(twice.Confidence, d.Confidence, 0.001, "... and halves the confidence only once");
        Check(FightTimeEstimate.Unknown.WithDowntime(10, 20).RemainingAttackable == float.MaxValue, "unknown stays unknown");
    }

    // BiasWarning: a fight whose biggest target was replaced after an untargetable pause
    private static void BiasTests()
    {
        var single = Steady(FightTimeConfig.Recommended(), 60);
        Check(single.Known && !single.BiasWarning, "single boss: no bias warning");
        var est = new FightTimeEstimator(FightTimeConfig.Recommended());
        FightTargetSample[] Feed(double t)
        {
            if (t < 40)
                return [new(1, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)];
            if (t < 55)
                return []; // intermission
            return [new(2, 2, (uint)(2_000_000 - 10_000 * (t - 55)), 2_000_000)];
        }
        Run(null, 80, 30, Feed, 0, est);
        Check(est.PhaseChanges == 1 && est.GapCount == 1, $"phase change and gap counted ({est.PhaseChanges}, {est.GapCount})");
        Check(est.Estimate.Known && est.Estimate.BiasWarning, "phased fight raises the bias warning");
        Check(est.Estimate.Confidence < single.Confidence, "... and lowers the confidence");
        Check(est.Estimate.UpperBound / est.Estimate.RemainingSeconds > single.UpperBound / single.RemainingSeconds, "... and widens the upper bound");
    }

    // FightPriorStore: built from a handcrafted summary cache, background refresh, failures keep the old table
    private static void PriorStoreTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "ttk_selftest_" + Guid.NewGuid().ToString("N"));
        var cache = ReplaySummaryCache.CacheDirectory(root);
        Directory.CreateDirectory(cache);
        var start = new DateTime(2026, 1, 1);
        PullSummary Pull(uint oid, float seconds, int index)
        {
            var t0 = start.AddHours(index).Ticks;
            List<PullSummary.HPPoint> history = [];
            for (var s = 0; s <= (int)seconds; ++s)
                history.Add(new(t0 + s * TimeSpan.TicksPerSecond, (uint)Math.Max(0, 1_000_000 - 1_000_000 * s / seconds), 1_000_000));
            return new(777, start.AddHours(index), seconds + 2, [oid], [], [], [], [new(oid, 1_000_000, [new(t0, t0 + (long)((seconds + 2) * TimeSpan.TicksPerSecond))], history)]);
        }
        for (var i = 0; i < 3; ++i)
            ReplaySummaryCache.Write(Path.Combine(cache, $"replay{i}.log.json"), new(ReplaySummaryCache.Version, 1, 1, false, [Pull(0x1234, 100 + i, i)]));
        var store = new FightPriorStore(() => root);
        Check(store.Find(777, 0x1234) == null, "empty before the first load");
        store.RefreshIfStale();
        for (var i = 0; i < 100 && (store.Loading || store.Generation == 0); ++i)
            Thread.Sleep(50);
        var prior = store.Find(777, 0x1234);
        Check(prior is { Count: 3 }, $"background load found the three kills (got {prior?.Count})");
        Check(store.Find(777, 0x9999) == null && store.Find(778, 0x1234) == null, "other fights have no prior");
        var generation = store.Generation;
        store.RefreshIfStale();
        for (var i = 0; i < 40 && store.Loading; ++i)
            Thread.Sleep(25);
        Check(store.Generation == generation, "an unchanged cache is not reloaded");
        // the prior works end to end
        var est = new FightTimeEstimator(FightTimeConfig.Recommended()) { Prior = prior };
        Run(null, 40, 30, t => [new(Boss, 0x1234, (uint)(1_000_000 - 10_000 * t), 1_000_000)], 0, est);
        Near(est.Estimate.RemainingSeconds, 60, 0.08, "prior from the store aligns a fight at 60% HP");
        // a corrupt file and a missing folder never throw and keep what is there
        File.WriteAllText(Path.Combine(cache, "broken.log.json"), "{ not json");
        var broken = new FightPriorStore(() => root);
        broken.Load(cache);
        Check(broken.Find(777, 0x1234)?.Count == 3, "a corrupt summary file is skipped");
        var missing = new FightPriorStore(() => Path.Combine(root, "nowhere"));
        missing.RefreshIfStale();
        Thread.Sleep(300);
        Check(missing.FightCount == 0 && !missing.Loading, "a missing folder leaves the store empty");
        var nullDir = new FightPriorStore(() => null);
        nullDir.RefreshIfStale();
        Thread.Sleep(200);
        Check(nullDir.FightCount == 0, "no timelines folder configured: empty store");
    }

    private static FightTimeConfig M3() => FightTimeConfig.Recommended() with { Model = FightTimeModel.PhaseAware };

    public static int Run()
    {
        // 1. constant rate: 1,000,000 HP, 10,000 HP/s -> 100 s fight
        foreach (var fps in new[] { 30, 60, 144 })
        {
            var est = Run(M3(), 40, fps, t => [new(Boss, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)]);
            var e = est.Estimate;
            Check(e.Known, $"constant rate known at 40 s ({fps} fps)");
            Near(e.RemainingSeconds, 60, 0.04, $"constant rate estimate ({fps} fps)");
            Check(e.LowerBound <= e.RemainingSeconds && e.RemainingSeconds <= e.UpperBound, "bounds bracket the estimate");
            Check(e.LowerBound <= 60 && 60 <= e.UpperBound, $"true remaining inside bounds ({fps} fps): {e}");
        }

        // 2. unknown semantics before enough history
        {
            var est = Run(M3(), 3, 60, t => [new(Boss, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)]);
            var e = est.Estimate;
            Check(!e.Known && e.RemainingSeconds == float.MaxValue && e.LowerBound == float.MaxValue && e.UpperBound == float.MaxValue && e.Confidence == 0, "unknown reports float.MaxValue everywhere");
        }

        // 3. scripted HP set (a 40% drop in one frame) is not damage
        {
            var est = Run(M3(), 40, 60, t => [new(Boss, 1, t < 20 ? (uint)(10_000_000 - 100_000 * t) : (uint)(8_000_000 - 4_000_000 - 100_000 * (t - 20)), 10_000_000)]);
            var e = est.Estimate;
            var hp = 4_000_000 - 100_000 * 20.0; // after 20 s at the new level
            Near(e.RemainingSeconds, hp / 100_000, 0.08, "scripted HP set ignored");
            Check(est.JumpCount >= 1, "jump counted");
        }

        // 4. untargetable gap does not dilute the rate
        {
            var est = Run(M3(), 70, 60, t =>
            {
                if (t is >= 20 and < 50)
                    return [];
                var active = t < 20 ? t : t - 30;
                return [new(Boss, 1, (uint)(1_000_000 - 10_000 * active), 1_000_000)];
            });
            Near(est.Estimate.RemainingSeconds, 100 - 40, 0.08, "gap handled on the active clock");
            var ablate = Run(M3() with { UseActiveClock = false }, 70, 60, t =>
            {
                if (t is >= 20 and < 50)
                    return [];
                var active = t < 20 ? t : t - 30;
                return [new(Boss, 1, (uint)(1_000_000 - 10_000 * active), 1_000_000)];
            });
            Check(ablate.Estimate.RemainingSeconds > est.Estimate.RemainingSeconds * 1.2f, "without the active clock the gap dilutes the rate (ablation)");
        }

        // 5. add spawns mid fight: remaining HP grows, rate stays (damage split over both)
        {
            var est = Run(M3(), 60, 60, t =>
            {
                // boss 1,000,000 with 5,000/s, add spawns at 30 s with 100,000 and takes 5,000/s, dies at 50 s
                var list = new List<FightTargetSample> { new(Boss, 1, (uint)(1_000_000 - 5_000 * t), 1_000_000) };
                if (t is >= 30 and < 50)
                    list.Add(new(2, 2, (uint)(100_000 - 5_000 * (t - 30)), 100_000));
                return [.. list];
            });
            // total rate 5,000 (boss) + 5,000 (add 20 s) -> average > 5,000; remaining boss HP 700,000 at 60 s: the estimate must not explode
            var e = est.Estimate;
            Check(e.Known && e.RemainingSeconds > 80 && e.RemainingSeconds < 160, $"add handled: {e}");
        }

        // 6. out of combat resets
        {
            var est = Run(M3(), 30, 60, t => [new(Boss, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)]);
            Check(est.Estimate.Known, "known before leaving combat");
            for (var i = 1; i <= 240; ++i)
                est.Update(30 + i / 60.0, false, []);
            Check(!est.Estimate.Known, "unknown out of combat");
            est.Update(40, true, [new(Boss, 1, 1_000_000, 1_000_000)]);
            Check(!est.Estimate.Known && est.Estimate.Elapsed < 1, "fresh fight after a reset");
        }

        // 7. repeated update with the same time is a no-op
        {
            var a = new FightTimeEstimator(M3());
            var b = new FightTimeEstimator(M3());
            for (var i = 0; i <= 2400; ++i)
            {
                var t = i / 60.0;
                FightTargetSample[] feed = [new(Boss, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)];
                a.Update(t, true, feed);
                b.Update(t, true, feed);
                b.Update(t, true, feed);
            }
            Check(a.Estimate.RemainingSeconds == b.Estimate.RemainingSeconds, "double update within a frame does not change the estimate");
        }

        // 8. no allocation in steady state (with a prior, the heaviest path)
        {
            var prior = new FightPrior();
            for (var k = 0; k < 20; ++k)
            {
                var oid = Enumerable.Repeat(1u, 101).ToArray();
                var frac = Enumerable.Range(0, 101).Select(s => 1f - s / 100f).ToArray();
                prior.Add(new FightPrior.Kill(100, oid, frac));
            }
            var est = new FightTimeEstimator(FightTimeConfig.Recommended()) { Prior = prior };
            long before = 0;
            Run(null, 60, 60, t => [new(Boss, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)], 0, est, (t, _) =>
            {
                if (Math.Abs(t - 20) < 1e-9)
                    before = GC.GetAllocatedBytesForCurrentThread();
            });
            // the lambda above allocates the feed arrays itself: measure the estimator with a preallocated feed instead
            var est2 = new FightTimeEstimator(FightTimeConfig.Recommended()) { Prior = prior };
            var feed2 = new FightTargetSample[1];
            for (var i = 0; i < 600; ++i)
            {
                feed2[0] = new(Boss, 1, (uint)(1_000_000 - 10_000 * (i / 60.0)), 1_000_000);
                est2.Update(i / 60.0, true, feed2);
            }
            var b0 = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 600; i < 6600; ++i)
            {
                feed2[0] = new(Boss, 1, (uint)Math.Max(1, 1_000_000 - 10_000 * (i / 60.0)), 1_000_000);
                est2.Update(i / 60.0, true, feed2);
            }
            var allocated = GC.GetAllocatedBytesForCurrentThread() - b0;
            Check(allocated == 0, $"steady-state Update allocates nothing (allocated {allocated} bytes)");
            _ = before;
        }

        // 9. prior alignment: five earlier kills, linear 100 s
        {
            var prior = new FightPrior();
            for (var k = 0; k < 5; ++k)
                prior.Add(new FightPrior.Kill(100, Enumerable.Repeat(1u, 101).ToArray(), Enumerable.Range(0, 101).Select(s => 1f - s / 100f).ToArray()));
            // this pull is at 60% after 40 s (as the earlier ones)
            var cfg = FightTimeConfig.Recommended() with { Model = FightTimeModel.Prior };
            var est = new FightTimeEstimator(cfg) { Prior = prior };
            Run(null, 40, 60, t => [new(Boss, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)], 0, est);
            Near(est.Estimate.RemainingSeconds, 60, 0.03, "prior-only estimate");
            // a pull that is twice as fast (60% left after 20 s) is matched by HP, not by time
            var est2 = new FightTimeEstimator(cfg) { Prior = prior };
            Run(null, 20, 60, t => [new(Boss, 1, (uint)(1_000_000 - 20_000 * t), 1_000_000)], 0, est2);
            Near(est2.Estimate.RemainingSeconds, 60, 0.03, "prior alignment by HP fraction");
            // another boss entirely: no match -> unknown
            var est3 = new FightTimeEstimator(cfg with { LoosePriorMatch = false }) { Prior = prior };
            Run(null, 40, 60, t => [new(Boss, 99, (uint)(1_000_000 - 10_000 * t), 1_000_000)], 0, est3);
            Check(!est3.Estimate.Known, "prior with a different OID gives no estimate (strict match)");
            var est3b = new FightTimeEstimator(cfg) { Prior = prior };
            Run(null, 40, 60, t => [new(Boss, 99, (uint)(1_000_000 - 10_000 * t), 1_000_000)], 0, est3b);
            Near(est3b.Estimate.RemainingSeconds, 60, 0.05, "loose match: aligned by HP fraction when no kill shows the OID");
            Check(est3b.BoundKind == 4, "... and its bounds are the poorly founded ones");
            // hybrid without a prior is the rate estimate; with agreeing prior it stays near 60
            var h = new FightTimeEstimator(FightTimeConfig.Recommended()) { Prior = prior };
            Run(null, 40, 60, t => [new(Boss, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)], 0, h);
            Near(h.Estimate.RemainingSeconds, 60, 0.04, "hybrid with an agreeing prior");
            var h0 = new FightTimeEstimator(FightTimeConfig.Recommended());
            Run(null, 40, 60, t => [new(Boss, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)], 0, h0);
            Near(h0.Estimate.RemainingSeconds, 60, 0.04, "hybrid without a prior falls back to the rate");
        }

        // 10. M0 (SAM's slope): linear drop is exact, a big step drop clears the history
        {
            var cfg = M3() with { Model = FightTimeModel.Slope5s };
            var est = Run(cfg, 20, 60, t => [new(Boss, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)]);
            Near(est.Estimate.RemainingSeconds, 80, 0.03, "slope estimate on a linear drop");
            var e2 = new FightTimeEstimator(cfg);
            Run(null, 10, 60, t => [new(Boss, 1, (uint)(1_000_000 - 10_000 * t), 1_000_000)], 0, e2);
            e2.Update(10.5, true, [new(Boss, 1, 300_000, 1_000_000)]);
            Check(!e2.Estimate.Known, "slope history cleared after a step drop");
        }

        // 11. many targets (more than the slot table) does not throw and stays sane
        {
            var est = Run(M3(), 20, 30, t =>
            {
                var l = new FightTargetSample[100];
                for (var i = 0; i < l.Length; ++i)
                    l[i] = new((ulong)(100 + i), 5, (uint)(10_000 - 100 * t), 10_000);
                return l;
            });
            Check(est.Estimate.Known, "100 targets: estimate exists");
        }

        // 12. bounds ordering over a noisy fight
        {
            var rng = new Random(1);
            var ok = true;
            Run(FightTimeConfig.Recommended(), 200, 30, t => [new(Boss, 1, (uint)Math.Max(1, 2_000_000 - 8_000 * t - rng.Next(0, 30_000)), 2_000_000)], 0, null, (t, e) =>
            {
                var x = e.Estimate;
                if (x.Known && !(x.LowerBound <= x.RemainingSeconds && x.RemainingSeconds <= x.UpperBound && x.Confidence is > 0 and <= 1))
                    ok = false;
            });
            Check(ok, "Lower <= Remaining <= Upper and 0 < Confidence <= 1 throughout a noisy fight");
        }

        ProbabilityTests();
        DowntimeTests();
        BiasTests();
        PriorStoreTests();

        Console.WriteLine(_failures == 0 ? $"selftest: all {_checks} checks passed" : $"selftest: {_failures} of {_checks} checks FAILED");
        return _failures == 0 ? 0 : 1;
    }
}
