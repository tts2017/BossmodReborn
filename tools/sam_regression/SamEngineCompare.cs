using System.Globalization;
using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;

namespace SamRegression;

// Drives the SAM regression simulator with the rotation engine (SamDefinition): simulator state -> EngineState, engine decision ->
// one action per slot. Level 100 scenarios with automatic strategy tracks only (others use the built-in policy). The simulator has
// a fixed GCD (route 2.08 / 2.14 with Fuka) and instant Iaijutsu, so the engine's base GCD is the route GCD without Fuka.
public sealed class SamEnginePolicy
{
    private readonly RotationEngine _engine;
    private readonly SamScenario _sc;

    public SamEnginePolicy(SamScenario sc, EngineWeights weights)
    {
        _sc = sc;
        _engine = new(SamDefinition.Build((float)(sc.GcdLength / 0.87)), weights);
    }

    public static bool Covers(SamScenario sc)
        => sc.Level >= 100 && sc.Higanbana == SamHiganbanaStrategy.Auto && sc.Tsubame == SamTsubameStrategy.Auto && sc.Namikiri == SamNamikiriStrategy.Auto
        && sc.Meikyo == SamMeikyoStrategy.Auto;

    public SamAction? Decide(SamState st, int slot)
    {
        if (!st.Melee)
            return null; // out of melee range: the built-in ranged handling (Enpi) is not in the definition
        var job = _engine.Job;
        var s = ReadState(job, st);
        var tl = EngineTimeline.Open();
        tl.FightEndIn = (float)(_sc.Duration - st.Time);
        var version = st.Targetable ? 1 : 2;
        if (!st.Targetable)
            tl.AddDowntime(0, 2.5f);
        foreach (var w in _sc.Downtimes)
        {
            if (w.End <= st.Time || w.Start > st.Time + 30)
                continue;
            tl.AddDowntime((float)Math.Max(0, w.Start - st.Time), (float)(w.End - st.Time));
            version = version * 31 + (int)w.Start;
        }
        tl.Version = version;
        if (slot < 0)
        {
            var d = _engine.Decide(s, tl, (float)st.Time);
            return d.NextGcd >= 0 ? Map(job.Skills[d.NextGcd]) : SamAction.None;
        }
        // weave slots come right after the GCD: the next GCD is a full GCD away
        s.GcdReadyAt = (float)_sc.GcdLength;
        s.AnimLockAt = 0;
        var w2 = _engine.Decide(s, tl, (float)st.Time + 0.01f * (slot + 1));
        return w2.Skill >= 0 && !job.Skills[w2.Skill].IsGcd && w2.ExecuteAt < 0.05f ? Map(job.Skills[w2.Skill]) : SamAction.None;
    }

    private static SamAction Map(SkillDef sk)
    {
        var name = sk.Name.EndsWith("Meikyo") ? sk.Name[..^6] : sk.Name;
        return Enum.TryParse<SamAction>(name, out var a) ? a : throw new InvalidOperationException(sk.Name);
    }

    public static EngineState ReadState(JobDefinition job, SamState st)
    {
        var s = EngineState.Create(job);
        s.Targets = (byte)Math.Max(1, st.EnemyCount);
        void Gauge(string name, int v) => s.Gauges[job.GaugeIndex(name)] = (short)v;
        Gauge(SamDefinition.Kenki, st.Kenki);
        Gauge(SamDefinition.Meditation, st.Meditation);
        var setsu = st.Sen.HasFlag(SamSen.Setsu) ? 1 : 0;
        var getsu = st.Sen.HasFlag(SamSen.Getsu) ? 1 : 0;
        var ka = st.Sen.HasFlag(SamSen.Ka) ? 1 : 0;
        Gauge(SamDefinition.Setsu, setsu);
        Gauge(SamDefinition.Getsu, getsu);
        Gauge(SamDefinition.Ka, ka);
        Gauge(SamDefinition.SenCount, setsu + getsu + ka);
        void Status(string name, double left, int stacks = 1)
        {
            if (left <= 0)
                return;
            var i = job.StatusIndex(name);
            s.StatusLeft[i] = (float)left;
            s.StatusStacks[i] = (byte)Math.Max(1, stacks);
        }
        Status(SamDefinition.Fugetsu, st.DamageBuff);
        Status(SamDefinition.Fuka, st.HasteBuff);
        if (st.MeikyoStacks > 0)
            Status(SamDefinition.Meikyo, st.MeikyoLeft, st.MeikyoStacks);
        Status(SamDefinition.Tendo, st.TendoLeft);
        Status(SamDefinition.OgiReady, st.OgiLeft);
        Status(SamDefinition.NamikiriReady, st.KaeshiNamikiriLeft);
        Status(SamDefinition.ZanshinReady, st.ZanshinLeft);
        Status(SamDefinition.Higanbana, st.HiganbanaDot);
        if (st.TsubameLeft > 0)
        {
            var kaeshi = st.TsubameAction switch
            {
                SamRepeat.Goken => SamDefinition.KaeshiGoken,
                SamRepeat.Setsugekka => SamDefinition.KaeshiSetsugekka,
                SamRepeat.TendoGoken => SamDefinition.TendoKaeshiGoken,
                SamRepeat.TendoSetsugekka => SamDefinition.TendoKaeshiSetsugekka,
                _ => null
            };
            if (kaeshi != null)
                Status(kaeshi, st.TsubameLeft);
        }
        var mk = job.CooldownIndex(SamDefinition.MeikyoCD);
        s.Charges[mk] = (byte)Math.Clamp(st.MeikyoCharges, 0, 2);
        s.CdReadyIn[mk] = st.MeikyoCharges < 2 && !double.IsInfinity(st.MeikyoNextChargeAt) ? (float)Math.Max(0, st.MeikyoNextChargeAt - st.Time) : 0;
        void Cd(string name, double readyIn)
        {
            var i = job.CooldownIndex(name);
            s.Charges[i] = (byte)(readyIn > 0 ? 0 : 1);
            s.CdReadyIn[i] = (float)Math.Max(0, readyIn);
        }
        Cd(SamDefinition.IkishotenCD, st.IkishotenCd);
        Cd(SamDefinition.SeneiCD, st.SeneiGurenCd);
        if (st.Combo is SamAction.Gyofu or SamAction.Hakaze or SamAction.Jinpu or SamAction.Shifu or SamAction.Fuko)
        {
            s.ComboSkill = (byte)job.SkillIndex(st.Combo == SamAction.Hakaze ? "Gyofu" : st.Combo.ToString());
            s.ComboLeft = 20;
        }
        return s;
    }
}

// engine-compare [--weights <json>] [--budget ms] [--limit N] [--detail]: built-in policy vs SAM [Engine] on the covered scenarios
public static class SamEngineCompare
{
    public static int Run(string[] args)
    {
        string Arg(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var path = Arg("--weights", "");
        var w = path.Length > 0 ? EngineWeights.Load(path) : SamDefinition.DefaultWeights();
        var budget = Arg("--budget", "");
        if (budget.Length > 0)
            w.BudgetMs = float.Parse(budget, CultureInfo.InvariantCulture);
        var limit = int.Parse(Arg("--limit", "2000"));
        var scenarios = SamScenarioCatalog.BuildAll(1).Where(SamEnginePolicy.Covers).Take(limit).ToList();
        var sim = new SamSim();
        SamSim.PolicyFactory = null;
        var old = scenarios.AsParallel().AsOrdered().Select(sim.Run).ToList();
        SamSim.PolicyFactory = sc => new SamEnginePolicy(sc, w.Clone()).Decide;
        var eng = scenarios.AsParallel().AsOrdered().Select(sim.Run).ToList();
        SamSim.PolicyFactory = null;
        var rows = old.Zip(eng).ToList();
        Console.WriteLine($"engine-covered scenarios: {rows.Count}, depth {w.HorizonGcds}, budget {w.BudgetMs} ms");
        Console.WriteLine("| metric | built-in | engine |");
        Console.WriteLine("|---|---:|---:|");
        Console.WriteLine($"| scenarios with any hard fail | {rows.Count(r => r.First.HardFailures.Count > 0)} | {rows.Count(r => r.Second.HardFailures.Count > 0)} |");
        Console.WriteLine($"| scenarios with any soft fail | {rows.Count(r => r.First.SoftFailures.Count > 0)} | {rows.Count(r => r.Second.SoftFailures.Count > 0)} |");
        string Kind(string reason) => reason.Split(':', '.', '(')[0].Trim();
        foreach (var k in rows.SelectMany(r => r.First.HardFailures.Concat(r.Second.HardFailures)).Select(f => Kind(f.Reason)).Distinct().OrderBy(x => x))
            Console.WriteLine($"| fail: {k} (scenarios) | {rows.Count(r => r.First.HardFailures.Any(f => Kind(f.Reason) == k))} | {rows.Count(r => r.Second.HardFailures.Any(f => Kind(f.Reason) == k))} |");
        if (args.Contains("--detail"))
            foreach (var r in rows.Where(r => r.Second.HardFailures.Count > 0).Take(20))
                Console.WriteLine($"FAIL {r.Second.Scenario.Name} t={r.Second.HardFailures[0].Time:f1}: {r.Second.HardFailures[0].Reason} | {r.Second.HardFailures[0].State}");
        return 0;
    }
}
