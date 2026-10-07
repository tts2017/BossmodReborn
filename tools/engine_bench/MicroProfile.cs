using System.Diagnostics;
using BossMod.Autorotation.Engine;
using EngineTools;

// per-primitive costs, run with: EngineBench prof
public static class MicroProfile
{
    public static void Run()
    {
        var job = ToyJob.Build();
        var engine = new RotationEngine(job, new EngineWeights());
        var ctx = new EvalContext();
        var s = EngineState.Create(job);
        s.ComboSkill = 0; s.ComboLeft = 20; s.GcdReadyAt = 1;
        var tl = EngineTimeline.Open(); tl.FightEndIn = 300; tl.AddBuff(5, 25, 1.15f); tl.AddBuff(125, 145, 1.15f); tl.Version = 1;
        engine.Planner.Plan(s, tl, engine.Weights);
        const int N = 2_000_000;
        void Time(string name, Action body)
        {
            body();
            var sw = Stopwatch.StartNew();
            body();
            Console.WriteLine($"{name,-24} {sw.Elapsed.TotalMilliseconds * 1e6 / N,8:f1} ns");
        }
        ulong sink = 0; float fs = 0;
        Time("Hash", () => { for (var i = 0; i < N; ++i) sink += s.Hash(job); });
        Time("IsLegal x6", () => { for (var i = 0; i < N / 6; ++i) foreach (var sk in job.Skills) sink += Simulator.IsLegal(job, s, tl, sk) ? 1UL : 0; });
        Time("copy+Execute(Cut)", () => { var sk = job.Skills[1]; for (var i = 0; i < N; ++i) { var c = s; fs += Simulator.Execute(job, ref c, tl, sk, ctx); } });
        Time("copy+Advance(1s)", () => { for (var i = 0; i < N; ++i) { var c = s; fs += Simulator.Advance(job, ref c, 1, ctx); } });
        Time("LeafLambda (cached)", () => { for (var i = 0; i < N; ++i) fs += engine.Planner.LeafLambda(0, 10 + (i & 7) * 0.25f, 50); });
        Time("AverageBuffMultiplier", () => { for (var i = 0; i < N; ++i) fs += tl.AverageBuffMultiplier(10, 20 + (i & 3)); });
        Console.WriteLine($"{sink}{fs}".Length);
    }
}
