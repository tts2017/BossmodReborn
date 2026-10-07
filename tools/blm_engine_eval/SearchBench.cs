using System.Diagnostics;
using BossMod.Autorotation.Engine;

namespace BlmEngineEval;

// searchbench --def <job> [--secs 120] [--budget ms] [--weights json]: plays the job against its own simulator (no time limit,
// raid buffs every 120 s from 7.8 s), records the root state of every GCD decision, then re-searches each one
//  - without a time limit (reference: nodes, time, decision),
//  - with the job's live budget in one call (depth reached, decision agreement, and the reference value lost by its choice).
public static class SearchBench
{
    public static int Run(string[] args)
    {
        var secs = float.Parse(Program.Arg(args, "--secs", "120"));
        var job = Program.Build(args);
        var w = Program.Weights(args);
        var live = w.Clone();
        var budget = Program.Arg(args, "--budget", "");
        live.BudgetMs = budget.Length > 0 ? float.Parse(budget) : 0.8f;
        var full = w.Clone();
        full.BudgetMs = 10000;

        // 1. collect root states from a self-play run
        var roots = new List<(EngineState S, EngineTimeline Tl, float Now)>();
        var player = new RotationEngine(job, full);
        var s = Program.Arg(args, "--def", "blm") != "blm" ? EngineState.Create(job) : Program.ColdState(job);
        var ctx = new EvalContext();
        var now = 0f;
        while (now < secs)
        {
            var tl = Timeline(secs, now);
            if (s.GcdReadyAt - s.Time <= 0.001f && s.AnimLockAt - s.Time <= 0.001f)
                roots.Add((s, tl, now));
            var d = player.Decide(s, tl, now);
            float dt;
            if (d.Skill < 0)
                dt = MathF.Max(0.1f, MathF.Max(s.GcdReadyAt, s.AnimLockAt) - s.Time);
            else if (d.ExecuteAt > 0.001f)
                dt = d.ExecuteAt;
            else
            {
                Simulator.Execute(job, ref s, tl, job.Skills[d.Skill], ctx);
                dt = MathF.Max(0.01f, s.AnimLockAt - s.Time);
            }
            Simulator.Advance(job, ref s, dt, ctx);
            now += dt;
            s.GcdReadyAt -= s.Time; s.AnimLockAt -= s.Time; s.Time = 0;
        }

        // 2. re-search each root (fresh engines, so no reuse between roots)
        var times = new List<double>();
        var nodes = new List<long>();
        var liveTimes = new List<double>();
        var liveDepth = new List<int>();
        int agree = 0, n = 0;
        double lost = 0;
        // the live engine persists across roots like in play (planner every 8 s, lambda cache kept), the reference is fresh each time
        var liveEngine = new RotationEngine(job, live) { FrameBudgetMs = 0, ReplanInterval = 8 };
        foreach (var (rs, rtl, rnow) in roots)
        {
            var refEngine = new RotationEngine(job, full);
            refEngine.Decide(rs, rtl, rnow); // warm-up (JIT, planner)
            refEngine.Reset();
            var t0 = Stopwatch.GetTimestamp();
            var dr = refEngine.Decide(rs, rtl, rnow);
            times.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
            nodes.Add(dr.Nodes);
            t0 = Stopwatch.GetTimestamp();
            var dl = liveEngine.Decide(rs, rtl, rnow);
            liveTimes.Add(Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
            liveDepth.Add(dl.Depth);
            ++n;
            if (dl.Skill == dr.Skill)
                ++agree;
            else if (dl.Skill >= 0 && dr.Skill >= 0)
            {
                var vRef = refEngine.LastRootValue(dr.Skill);
                var vLive = refEngine.LastRootValue(dl.Skill);
                if (!float.IsNaN(vRef) && !float.IsNaN(vLive))
                    lost += vRef - vLive;
            }
        }
        // 3. micro costs on the collected roots
        {
            var c2 = new EvalContext();
            var tl0 = Timeline(secs, 0);
            const int Reps = 200;
            long tLegal = 0, tExec = 0, tHash = 0, tAdv = 0; long legal = 0, calls = 0;
            ulong sink = 0;
            foreach (var (rs, _, _) in roots)
            {
                Micro.Legal(job, rs, tl0, 50); Micro.ExecLegal(job, rs, tl0, 50, c2);
                var t1 = Stopwatch.GetTimestamp();
                legal += Micro.Legal(job, rs, tl0, Reps);
                tLegal += Stopwatch.GetTimestamp() - t1;
                t1 = Stopwatch.GetTimestamp();
                sink += (ulong)Micro.ExecLegal(job, rs, tl0, Reps, c2);
                tExec += Stopwatch.GetTimestamp() - t1;
                t1 = Stopwatch.GetTimestamp();
                MicroHash.Run(job, rs, 100);
                sink ^= MicroHash.Run(job, rs, Reps * 10);
                tHash += Stopwatch.GetTimestamp() - t1;
                t1 = Stopwatch.GetTimestamp();
                for (var r = 0; r < Reps * 10; ++r) { var cs = rs; Simulator.Advance(job, ref cs, 1.3f, c2); }
                tAdv += Stopwatch.GetTimestamp() - t1;
            }
            double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;
            var nr = roots.Count;
            var eng = new RotationEngine(job, full);
            eng.Decide(roots[0].S, tl0, 0);
            var cyc = eng.Analysis.CycleFor(1);
            long tc = 0, tl2 = 0;
            foreach (var (rs, _, _) in roots)
            {
                if (cyc != null) { MicroLeaf.Cycle(cyc, rs, 50); var a = Stopwatch.GetTimestamp(); MicroLeaf.Cycle(cyc, rs, Reps * 10); tc += Stopwatch.GetTimestamp() - a; }
                MicroLeaf.Lambdas(eng, rs, 50); var b = Stopwatch.GetTimestamp(); MicroLeaf.Lambdas(eng, rs, Reps * 10); tl2 += Stopwatch.GetTimestamp() - b;
            }
            long tq = 0;
            foreach (var (rs, _, _) in roots) { MicroQuick.Run(job, rs, tl0, 50); var q0 = Stopwatch.GetTimestamp(); MicroQuick.Run(job, rs, tl0, Reps); tq += Stopwatch.GetTimestamp() - q0; }
            Console.WriteLine($"  micro: QuickValue all {job.Skills.Length} skills + multiplier {Us(tq) / (nr * Reps) * 1000:f0} ns");
            Console.WriteLine($"  micro: cycle value {Us(tc) / (nr * Reps * 10) * 1000:f0} ns, all lambdas {Us(tl2) / (nr * Reps * 10) * 1000:f0} ns ({job.Gauges.Length + job.Cooldowns.Length} resources)");
            Console.WriteLine($"  micro: IsLegal over all {job.Skills.Length} skills {Us(tLegal) / (nr * Reps):f2} us ({legal / (double)(nr * Reps):f1} legal), legal+Execute all {Us(tExec - tLegal) / (nr * Reps):f2} us, Hash {Us(tHash) / (nr * Reps * 10) * 1000:f0} ns, Advance {Us(tAdv) / (nr * Reps * 10) * 1000:f0} ns {sink % 2}");
        }
        static double P(List<double> xs, double q) { var s = xs.OrderBy(x => x).ToList(); return s.Count == 0 ? 0 : s[(int)Math.Min(s.Count - 1, s.Count * q)]; }
        Console.WriteLine($"{job.Name}: {n} roots, horizon {w.HorizonGcds} GCDs");
        Console.WriteLine($"  full search: mean {times.Average():f0} us, p50 {P(times, 0.5):f0}, p90 {P(times, 0.9):f0}, max {times.Max():f0}; nodes mean {nodes.Average():f0}, max {nodes.Max()}");
        Console.WriteLine($"  live budget {live.BudgetMs} ms: mean {liveTimes.Average():f0} us, depth reached mean {liveDepth.Average():f2} (full {w.HorizonGcds}), agreement {100.0 * agree / Math.Max(1, n):f1}%, value lost per root {lost / Math.Max(1, n):f1}");
        return 0;
    }

    private static EngineTimeline Timeline(float secs, float now)
    {
        var tl = EngineTimeline.Open();
        tl.FightEndIn = secs - now;
        for (var t = 7.8f; t < secs && tl.NumBuffs < EngineLimits.MaxWindows; t += 120)
            if (t + 20 > now && t < now + 360)
                tl.AddBuff(t - now, t + 20 - now, 1.05f);
        tl.Version = tl.NumBuffs;
        return tl;
    }
}

public static class Micro
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static long Legal(JobDefinition job, in EngineState s, in EngineTimeline tl, int reps)
    {
        long legal = 0;
        var skills = job.Skills;
        for (var r = 0; r < reps; ++r)
            for (var i = 0; i < skills.Length; ++i)
                if (Simulator.IsLegal(job, s, tl, skills[i]))
                    ++legal;
        return legal;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static float ExecLegal(JobDefinition job, in EngineState s, in EngineTimeline tl, int reps, EvalContext ctx)
    {
        var sum = 0f;
        var skills = job.Skills;
        for (var r = 0; r < reps; ++r)
            for (var i = 0; i < skills.Length; ++i)
            {
                if (!Simulator.IsLegal(job, s, tl, skills[i]))
                    continue;
                var c = s;
                sum += Simulator.Execute(job, ref c, tl, skills[i], ctx);
            }
        return sum;
    }
}

public static class MicroHash
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static ulong Run(JobDefinition job, in EngineState s, int reps)
    {
        ulong h = 0;
        for (var r = 0; r < reps; ++r)
            h ^= s.Hash(job);
        return h;
    }
}

public static class MicroLeaf
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static float Cycle(CycleModel m, in EngineState s, int reps)
    {
        var v = 0f;
        for (var r = 0; r < reps; ++r)
            v += m.Value(s);
        return v;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static float Lambdas(RotationEngine e, in EngineState s, int reps)
    {
        var v = 0f;
        var job = e.Job;
        for (var r = 0; r < reps; ++r)
        {
            var t = (r % 40) * 0.25f;
            for (var g = 0; g < job.Gauges.Length; ++g)
                v += e.Planner.LeafLambda(UpperPlanner.GaugeResource(g), t, s.Gauges[g]);
            for (var c = 0; c < job.Cooldowns.Length; ++c)
                v += e.Planner.LeafLambda(UpperPlanner.CdResource(c), t, s.Charges[c] + 0.5f);
        }
        return v;
    }
}

public static class MicroQuick
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static float Run(JobDefinition job, in EngineState s, in EngineTimeline tl, int reps)
    {
        var v = 0f;
        var skills = job.Skills;
        for (var r = 0; r < reps; ++r)
        {
            var m = Simulator.DamageMultiplier(job, s, tl);
            for (var i = 0; i < skills.Length; ++i)
                v += Simulator.QuickValue(job, s, tl, skills[i], m);
        }
        return v;
    }
}
