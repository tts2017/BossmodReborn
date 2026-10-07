using System.Diagnostics;
using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;

namespace RprEngineEval;

public static class Prof
{
    private static void LowerSearchReset() => typeof(RotationEngine).Assembly.GetType("BossMod.Autorotation.Engine.LowerSearch")!.GetFields().Where(f => f.Name.StartsWith("Prof")).ToList().ForEach(f => f.SetValue(null, 0L));

    public static int Run()
    {
        var job = RprDefinition.Build(2.48f);
        var engine = new RotationEngine(job, RprDefinition.DefaultWeights());
        var s = EngineState.Create(job);
        s.Gauges[job.GaugeIndex("Soul")] = 60;
        s.Gauges[job.GaugeIndex("Shroud")] = 40;
        s.StatusLeft[job.StatusIndex("DeathsDesign")] = 25;
        s.StatusStacks[job.StatusIndex("DeathsDesign")] = 1;
        s.ComboSkill = (byte)job.SkillIndex("Slice"); s.ComboLeft = 20;
        var tl = EngineTimeline.Open(); tl.FightEndIn = 400; tl.AddBuff(30, 50, 1.05f); tl.AddBuff(150, 170, 1.05f); tl.Version = 1;
        engine.Planner.Plan(s, tl, engine.Weights);
        var ctx = new EvalContext();
        const int N = 1_000_000;
        void Time(string name, Action body) { body(); var sw = Stopwatch.StartNew(); body(); Console.WriteLine($"{name,-28} {sw.Elapsed.TotalMilliseconds * 1e6 / N,8:f1} ns"); }
        ulong sink = 0; float fs = 0;
        Time("Hash", () => { for (var i = 0; i < N; ++i) sink += s.Hash(job); });
        Time("IsLegal all skills", () => { for (var i = 0; i < N / 10; ++i) foreach (var sk in job.Skills) sink += Simulator.IsLegal(job, s, tl, sk) ? 1UL : 0; });
        Time("copy state", () => { for (var i = 0; i < N; ++i) { var c = s; sink += c.ComboSkill; } });
        Time("copy+Execute(Waxing)", () => { var sk = job.Skills[job.SkillIndex("WaxingSlice")]; for (var i = 0; i < N; ++i) { var c = s; fs += Simulator.Execute(job, ref c, tl, sk, ctx); } });
        Time("copy+Advance(2.5)", () => { for (var i = 0; i < N; ++i) { var c = s; fs += Simulator.Advance(job, ref c, 2.5f, ctx); } });
        Time("Plan", () => { for (var i = 0; i < N / 1000; ++i) engine.Planner.Plan(s, tl, engine.Weights); });
        Console.WriteLine("(IsLegal all skills is per 1/10 of N: multiply by 10 for one full scan)");
        var w = engine.Weights; w.BudgetMs = 100; if (Environment.GetEnvironmentVariable("DEPTH") is { } dd) w.HorizonGcds = int.Parse(dd);
        for (var i = 0; i < 30; ++i) { engine.InvalidateCache(); var ws = s; ws.ComboLeft -= i * 0.01f; engine.Decide(ws, tl, 0); }

        engine.InvalidateCache();
        var sw2 = Stopwatch.StartNew();
        var d = engine.Decide(s, tl, 0);
        Console.WriteLine($"depth-4 decision: {sw2.Elapsed.TotalMilliseconds * 1000:f0} us, nodes {d.Nodes}, skills {job.Skills.Length}, statuses {job.Statuses.Length}");

        Console.WriteLine($"{sink}{fs}".Length);
        return 0;
    }
}

public static class Prof2
{
    public static int Run()
    {
        var job = RprDefinition.Build(2.48f);
        var w = RprDefinition.DefaultWeights();
        w.BudgetMs = 1000;
        w.HorizonGcds = int.Parse(Environment.GetEnvironmentVariable("DEPTH") ?? "3");
        var engine = new RotationEngine(job, w);
        var s = EngineState.Create(job);
        s.Gauges[job.GaugeIndex("Soul")] = 60;
        s.Gauges[job.GaugeIndex("Shroud")] = 40;
        s.StatusLeft[job.StatusIndex("DeathsDesign")] = 25;
        s.StatusStacks[job.StatusIndex("DeathsDesign")] = 1;
        s.ComboSkill = (byte)job.SkillIndex("Slice"); s.ComboLeft = 20;
        var tl = EngineTimeline.Open(); tl.FightEndIn = 400; tl.AddBuff(30, 50, 1.05f); tl.AddBuff(150, 170, 1.05f);
        foreach (var replan in new[] { false, true })
        {
            var times = new List<double>(); var nodes = 0L;
            for (var i = 0; i < 400; ++i)
            {
                engine.InvalidateCache();
                tl.Version = replan ? i : 0;
                var st = s; st.ComboLeft -= (i % 50) * 0.1f;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var d = engine.Decide(st, tl, replan ? 0 : i * 0.001f);
                times.Add(sw.Elapsed.TotalMilliseconds * 1000);
                nodes += d.Nodes;
                if (i == 0) times.Clear();
            }
            times.Sort();

            Console.WriteLine($"replan={replan} depth={w.HorizonGcds}: mean {times.Average():f0} us p50 {times[times.Count / 2]:f0} p99 {times[(int)(times.Count * 0.99)]:f0} nodes {nodes / 400.0:f0}");
        }
        return 0;
    }
}
