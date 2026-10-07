using System.Globalization;
using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;

namespace BlmEngineEval;

// Offline tools for the BLM engine definition:
//   analysis              - derived constants (filler, gauge / cooldown unit values)
//   play [--secs N]       - the engine playing against its own simulator from a cold start (sequence + total value)
//   explain --state k=v.. - root values of one decision (state keys: mp af ui hearts poly soul paradox fs th dot)
// Common options: --weights <json> --depth N --budget ms
public static class Program
{
    public static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var mode = args.Length > 0 ? args[0] : "play";
        return mode switch
        {
            "analysis" => Analysis(args),
            "play" => Play(args),
            "explain" => Explain(args),
            "tune-xan" => XanTuner.Run(args),
            _ => throw new ArgumentException($"unknown mode {mode}")
        };
    }

    public static EngineWeights Weights(string[] args)
    {
        var path = Arg(args, "--weights", "");
        var w = path.Length > 0 ? EngineWeights.Load(path) : BlmDefinition.DefaultWeights();
        var depth = Arg(args, "--depth", "");
        if (depth.Length > 0)
            w.HorizonGcds = int.Parse(depth);
        var budget = Arg(args, "--budget", "");
        if (budget.Length > 0)
            w.BudgetMs = float.Parse(budget);
        return w;
    }

    private static int Analysis(string[] args)
    {
        var e = new RotationEngine(BlmDefinition.Build(), Weights(args));
        var a = e.Analysis;
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        for (var t = 1; t <= 3; ++t)
            Console.WriteLine($"cycle targets={t}: states={a.Cycles[t]?.States} rate={a.Cycles[t]?.Rate:f1} pps ({a.Cycles[t]?.Rate * 2.5f:f0}/gcd) maxH={a.Cycles[t]?.MaxValue:f0}");
        for (var t = 1; t <= 3; ++t)
            Console.WriteLine($"t{t}: {a.Cycles[t]?.CycleDescription}");
        Console.WriteLine($"filler pps={a.FillerPps:f1} perGcd={a.FillerPerGcd:f1} maxSkill={a.MaxSkillValue:f0}");
        for (var g = 0; g < e.Job.Gauges.Length; ++g)
            Console.WriteLine($"gauge {e.Job.Gauges[g].Name}: unit={a.GaugeUnit[g]} value={a.GaugeUnitValue[g]:f1} gain/s={a.GaugeGainPerSecond[g]:f3} flat={e.Job.Gauges[g].Flat} flatValue={e.FlatGaugeValue[g]}");
        for (var c = 0; c < e.Job.Cooldowns.Length; ++c)
            Console.WriteLine($"cd {e.Job.Cooldowns[c].Name}: value={a.CdUnitValue[c]:f1}");
        return 0;
    }

    public static EngineState ColdState(JobDefinition job)
    {
        var s = EngineState.Create(job);
        s.Gauges[job.GaugeIndex(BlmDefinition.MP)] = 10000;
        return s;
    }

    private static int Play(string[] args)
    {
        var secs = float.Parse(Arg(args, "--secs", "120"));
        var job = BlmDefinition.Build();
        var e = new RotationEngine(job, Weights(args));
        var tl = EngineTimeline.Open();
        tl.FightEndIn = secs;
        var s = ColdState(job);
        var ctx = new EvalContext();
        var total = 0f;
        var now = 0f;
        var line = new List<string>();
        while (now < secs)
        {
            var local = s;
            var d = e.Decide(local, tl, now);
            if (d.Skill < 0)
            {
                var dt = MathF.Max(0.1f, MathF.Max(s.GcdReadyAt, s.AnimLockAt) - s.Time);
                Simulator.Advance(job, ref s, dt, ctx);
                now += dt;
                tl.FightEndIn = secs - now;
                s.GcdReadyAt -= dt; s.AnimLockAt -= dt; s.Time = 0;
                continue;
            }
            if (d.ExecuteAt > 0.001f)
            {
                // wait until the decided skill is executable, then use it (no re-decision: a reused decision keeps its old ExecuteAt)
                var dt = d.ExecuteAt;
                Simulator.Advance(job, ref s, dt, ctx);
                now += dt;
                tl.FightEndIn = secs - now;
                s.GcdReadyAt -= dt; s.AnimLockAt -= dt; s.Time = 0;
                if (!Simulator.IsLegal(job, s, tl, job.Skills[d.Skill]))
                    continue;
            }
            var sk = job.Skills[d.Skill];
            var v = Simulator.Execute(job, ref s, tl, sk, ctx);
            total += v;
            line.Add($"{now:f1} {sk.Name} {v:f0} [mp={s.Gauges[0]} af={s.Gauges[1]} ui={s.Gauges[2]} h={s.Gauges[3]} pg={s.Gauges[4]} as={s.Gauges[5]} px={s.Gauges[6]}]");
            var adv = MathF.Max(0.01f, s.AnimLockAt - s.Time);
            Simulator.Advance(job, ref s, adv, ctx);
            now += adv;
            tl.FightEndIn = secs - now;
            s.GcdReadyAt -= adv; s.AnimLockAt -= adv; s.Time = 0;
        }
        foreach (var l in line)
            Console.WriteLine(l);
        Console.WriteLine($"total={total:f0} pps={total / secs:f1}");
        return 0;
    }

    private static int Explain(string[] args)
    {
        var job = BlmDefinition.Build();
        var e = new RotationEngine(job, Weights(args));
        var s = ColdState(job);
        var i = Array.IndexOf(args, "--state");
        if (i >= 0)
        {
            foreach (var kv in args[(i + 1)..].TakeWhile(a => !a.StartsWith("--")))
            {
                var p = kv.Split('=');
                var v = float.Parse(p[1]);
                switch (p[0])
                {
                    case "mp": s.Gauges[job.GaugeIndex(BlmDefinition.MP)] = (short)v; break;
                    case "af": s.Gauges[job.GaugeIndex(BlmDefinition.AstralFire)] = (short)v; break;
                    case "ui": s.Gauges[job.GaugeIndex(BlmDefinition.UmbralIce)] = (short)v; break;
                    case "hearts": s.Gauges[job.GaugeIndex(BlmDefinition.Hearts)] = (short)v; break;
                    case "poly": s.Gauges[job.GaugeIndex(BlmDefinition.Polyglot)] = (short)v; break;
                    case "soul": s.Gauges[job.GaugeIndex(BlmDefinition.AstralSoul)] = (short)v; break;
                    case "paradox": s.Gauges[job.GaugeIndex(BlmDefinition.Paradox)] = (short)v; break;
                    case "fs": s.StatusLeft[job.StatusIndex(BlmDefinition.Firestarter)] = v; break;
                    case "th": s.StatusLeft[job.StatusIndex(BlmDefinition.Thunderhead)] = v; break;
                    case "dot": s.StatusLeft[job.StatusIndex(BlmDefinition.Thunder)] = v; break;
                    case "ptimer": s.StatusLeft[job.StatusIndex(BlmDefinition.PolyglotTimer)] = v; break;
                    case "targets": s.Targets = (byte)v; break;
                    default: throw new ArgumentException(p[0]);
                }
            }
        }
        var tl = EngineTimeline.Open();
        var d = e.Decide(s, tl, 0);
        Console.WriteLine($"decision {(d.Skill >= 0 ? job.Skills[d.Skill].Name : "wait")} next={(d.NextGcd >= 0 ? job.Skills[d.NextGcd].Name : "-")} depth={d.Depth} nodes={d.Nodes} value={d.Value:f0}");
        foreach (var sk in job.Skills)
            if (!float.IsNaN(e.LastRootValue(sk.Index)))
                Console.WriteLine($"  {sk.Name,-18} {e.LastRootValue(sk.Index):f0}");
        var li = Array.IndexOf(args, "--line");
        if (li >= 0)
        {
            var idx = args[li + 1].Split('>').Select(job.SkillIndex).ToArray();
            e.ExplainLine(s, tl, 0, idx, Console.WriteLine);
        }
        return 0;
    }

    public static string Arg(string[] args, string name, string fallback)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
    }
}
