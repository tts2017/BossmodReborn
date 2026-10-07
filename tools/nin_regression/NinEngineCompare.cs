using System.Globalization;
using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;

namespace NinRegression;

// Drives the NIN regression emulator with the rotation engine (NinDefinition): emulator state -> EngineState, engine decision ->
// one emulator action per slot. Level 100, normal rotation only (other scenarios use the built-in policy).
public sealed class NinEnginePolicy(EngineWeights weights)
{
    private readonly RotationEngine _engine = new(NinDefinition.Build(), weights);
    public int EngineSlots;

    public static bool Covers(NinScenario sc) => sc.Level >= 100 && sc.RotationStrategy == RotationStrategy.Normal && sc.BurstStyle == BurstStyle.Normal;

    public NinAction? Decide(NinSimState s, bool gcdSlot)
    {
        if (!Covers(s.Scenario) || s.BasicComboOnly)
            return null;
        ++EngineSlots;
        var job = _engine.Job;
        var e = ReadState(job, s);
        e.GcdReadyAt = gcdSlot ? 0 : (float)Math.Max(0, s.NextGcdAt - s.Time);
        var tl = EngineTimeline.Open();
        tl.FightEndIn = (float)(s.Scenario.Duration - s.Time);
        if (!s.CanActOnEnemy)
            tl.AddDowntime(0, 2.5f);
        tl.Version = s.CanActOnEnemy ? 1 : 2; // a returning target must not reuse the decision made without it
        var d = _engine.Decide(e, tl, (float)s.Time);
        if (gcdSlot)
            return d.NextGcd >= 0 ? Map(job.Skills[d.NextGcd]) : NinAction.None;
        return d.Skill >= 0 && !job.Skills[d.Skill].IsGcd && d.ExecuteAt < 0.05f ? Map(job.Skills[d.Skill]) : NinAction.None;
    }

    private static NinAction Map(SkillDef sk) => sk.Name switch
    {
        "SpinningEdge" => NinAction.SpinningEdge,
        "GustSlash" => NinAction.GustSlash,
        "AeolianEdge" or "AeolianEdgeBare" => NinAction.AeolianEdge,
        "ArmorCrush" => NinAction.ArmorCrush,
        "DeathBlossom" => NinAction.DeathBlossom,
        "HakkeMujinsatsu" => NinAction.HakkeMujinsatsu,
        "ForkedRaiju" => NinAction.FleetingRaiju,
        "PhantomKamaitachi" => NinAction.PhantomKamaitachi,
        "Raiton" => NinAction.Raiton,
        "Suiton" => NinAction.Suiton,
        "Katon" => NinAction.Katon,
        "HyoshoRanryu" => NinAction.HyoshoRanryu,
        "GokaMekkyaku" => NinAction.GokaMekkyaku,
        "TCJCombo" => NinAction.None, // the emulator runs the Ten Chi Jin steps itself
        "Kassatsu" => NinAction.Kassatsu,
        "TenChiJin" => NinAction.TenChiJin,
        "Dokumori" => NinAction.Dokumori,
        "KunaisBane" or "KunaisBaneOdd" => NinAction.KunaisBane,
        "DreamWithinADream" => NinAction.DreamWithinADream,
        "Meisui" => NinAction.Meisui,
        "Bunshin" => NinAction.Bunshin,
        "Bhavacakra" => NinAction.Bhavacakra,
        "ZeshoMeppo" => NinAction.ZeshoMeppo,
        "HellfrogMedium" => NinAction.HellfrogMedium,
        "DeathfrogMedium" => NinAction.DeathfrogMedium,
        "TenriJindo" => NinAction.TenriJindo,
        _ => throw new InvalidOperationException(sk.Name)
    };

    public static EngineState ReadState(JobDefinition job, NinSimState s)
    {
        var e = EngineState.Create(job);
        e.Targets = (byte)Math.Max(1, s.NumAoeTargets);
        e.Gauges[job.GaugeIndex(NinDefinition.Ninki)] = (short)s.Ninki;
        e.Gauges[job.GaugeIndex(NinDefinition.Kazematoi)] = (short)s.Kazematoi;
        e.Gauges[job.GaugeIndex(NinDefinition.Raiju)] = (short)s.RaijuStacks;
        void Status(string name, double left, int stacks = 1)
        {
            if (left <= 0)
                return;
            var i = job.StatusIndex(name);
            e.StatusLeft[i] = (float)left;
            e.StatusStacks[i] = (byte)stacks;
        }
        Status(NinDefinition.Kassatsu, s.KassatsuLeft);
        Status(NinDefinition.ShadowWalker, Math.Max(s.ShadowWalker, s.Hidden ? 20 : 0)); // Hidden also enables Kunai's Bane / Meisui
        Status(NinDefinition.Meisui, s.MeisuiLeft);
        Status(NinDefinition.Higi, s.HigiLeft);
        Status(NinDefinition.TenriReady, s.TenriJindoLeft);
        Status(NinDefinition.PhantomReady, s.PhantomKamaitachiLeft);
        Status(NinDefinition.TenChiJin, s.TenChiJinLeft);
        Status(NinDefinition.KunaisBane, s.TargetTrickLeft);
        Status(NinDefinition.Dokumori, s.TargetMugLeft);
        if (s.BunshinReadyIn > 60)
            Status(NinDefinition.Bunshin, s.BunshinReadyIn - 60, 5); // the emulator does not track the stacks
        void Cd(string name, double readyIn)
        {
            var i = job.CooldownIndex(name);
            e.Charges[i] = (byte)(readyIn > 0 ? 0 : 1);
            e.CdReadyIn[i] = (float)Math.Max(0, readyIn);
        }
        var m = job.CooldownIndex(NinDefinition.MudraCD);
        e.Charges[m] = (byte)s.MudraCharges;
        e.CdReadyIn[m] = s.MudraCharges < 2 ? (float)(s.MudraRecharge > 0 ? s.MudraRecharge : 20) : 0;
        Cd(NinDefinition.KassatsuCD, s.KassatsuReadyIn);
        Cd(NinDefinition.TenChiJinCD, s.TenChiJinReadyIn);
        Cd(NinDefinition.DokumoriCD, s.MugReadyIn);
        Cd(NinDefinition.KunaisBaneCD, s.TrickReadyIn);
        Cd(NinDefinition.DreamCD, s.DreamReadyIn);
        Cd(NinDefinition.MeisuiCD, s.MeisuiReadyIn);
        Cd(NinDefinition.BunshinCD, s.BunshinReadyIn);
        if (s.ComboLastMove is NinAction.SpinningEdge or NinAction.GustSlash or NinAction.DeathBlossom)
        {
            e.ComboSkill = (byte)job.SkillIndex(s.ComboLastMove.ToString());
            e.ComboLeft = 20;
        }
        return e;
    }
}

// engine-compare [--weights <json>] [--budget ms] [--detail]: built-in policy vs NIN [Engine] on every scenario the engine covers
public static class NinEngineCompare
{
    public static int Run(string[] args)
    {
        string Arg(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var path = Arg("--weights", "");
        var w = path.Length > 0 ? EngineWeights.Load(path) : NinDefinition.DefaultWeights();
        var budget = Arg("--budget", "");
        if (budget.Length > 0)
            w.BudgetMs = float.Parse(budget, CultureInfo.InvariantCulture);
        var scenarios = NinScenarioGenerator.BuildAll().Where(NinEnginePolicy.Covers).ToList();
        var rows = new List<(NinScenarioResult Old, NinScenarioResult New)>();
        var micros = new List<double>();
        var gate = new object();
        Parallel.ForEach(scenarios, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) }, sc =>
        {
            var old = new NinRotationEmulator().Run(sc);
            var policy = new NinEnginePolicy(w.Clone());
            var mic = new List<double>();
            var eng = new NinRotationEmulator { PolicyOverride = policy.Decide, PolicyMicros = mic }.Run(sc);
            lock (gate)
            {
                rows.Add((old, eng));
                micros.AddRange(mic);
            }
        });
        Console.WriteLine($"engine-covered scenarios: {rows.Count}, depth {w.HorizonGcds}, budget {w.BudgetMs} ms");
        Console.WriteLine("| metric | built-in | engine |");
        Console.WriteLine("|---|---:|---:|");
        Console.WriteLine($"| scenarios with any hard fail | {rows.Count(r => r.Old.HardFailures.Count > 0)} | {rows.Count(r => r.New.HardFailures.Count > 0)} |");
        Console.WriteLine($"| hard fails (sum) | {rows.Sum(r => r.Old.HardFailures.Count)} | {rows.Sum(r => r.New.HardFailures.Count)} |");
        Console.WriteLine($"| soft regressions (sum) | {rows.Sum(r => r.Old.SoftRegressions.Count)} | {rows.Sum(r => r.New.SoftRegressions.Count)} |");
        foreach (var rule in rows.SelectMany(r => r.Old.HardFailures.Concat(r.New.HardFailures)).Select(f => f.Rule).Distinct().OrderBy(x => x))
            Console.WriteLine($"| fail: {rule} (scenarios) | {rows.Count(r => r.Old.HardFailures.Any(f => f.Rule == rule))} | {rows.Count(r => r.New.HardFailures.Any(f => f.Rule == rule))} |");
        foreach (var rule in rows.SelectMany(r => r.New.SoftRegressions).Select(f => f.Rule).Distinct().OrderBy(x => x))
            Console.WriteLine($"| soft: {rule} (count) | {rows.Sum(r => r.Old.SoftRegressions.Count(f => f.Rule == rule))} | {rows.Sum(r => r.New.SoftRegressions.Count(f => f.Rule == rule))} |");
        if (micros.Count > 0)
        {
            var sorted = micros.OrderBy(x => x).ToList();
            Console.WriteLine($"| engine time per slot mean / p99 (us) | - | {sorted.Average():f1} / {sorted[(int)(sorted.Count * 0.99)]:f1} |");
        }
        if (args.Contains("--detail"))
            foreach (var r in rows.Where(r => r.New.HardFailures.Count > 0).OrderBy(r => r.New.Scenario.Name).Take(40))
                foreach (var f in r.New.HardFailures.Take(2))
                    Console.WriteLine($"FAIL {r.New.Scenario.Name} t={f.Time:f1} {f.Rule}: {f.ExpectedInvariant} | actual {f.ActualAction} | {f.StateSummary}");
        return 0;
    }
}
