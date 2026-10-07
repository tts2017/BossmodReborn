using System.Globalization;
using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;

namespace MnkRegression;

// Drives the MNK regression emulator with the rotation engine (MnkDefinition): emulator state -> EngineState, engine decision ->
// one action per slot. Level 100, full rotation, automatic strategy tracks only (other scenarios use the built-in policy).
public sealed class MnkEnginePolicy(EngineWeights weights)
{
    private readonly RotationEngine _engine = new(MnkDefinition.Build(), weights);

    public static bool Covers(BattleScenario sc)
    {
        var p = sc.StrategyProfile;
        return p.LevelCap >= 100 && p.RotationMode == RotationMode.Full && p.PBStrategy == PBStrategyMode.Automatic && p.BlitzStrategy == BlitzStrategyMode.Automatic
            && p.NadiStrategy == NadiStrategyMode.Automatic && p.RoFStrategy == AutoForceDelayMode.Automatic && p.BrotherhoodStrategy == AutoForceDelayMode.Automatic
            && p.RoWStrategy == AutoForceDelayMode.Automatic;
    }

    public string? Decide(BattleState st, bool gcdSlot)
    {
        var job = _engine.Job;
        var s = ReadState(job, st);
        s.GcdReadyAt = gcdSlot ? 0 : (float)Math.Max(0, st.GCDReadyIn);
        var tl = EngineTimeline.Open();
        var down = !st.Targetable || !st.HaveTarget || !st.CanMelee || st.LookAwayActive;
        if (down)
            tl.AddDowntime(0, 2.5f);
        if (st.EstimatedFightEnd is { } end)
            tl.FightEndIn = (float)Math.Max(0, end - st.Time);
        tl.Version = down ? 2 : 1;
        var d = _engine.Decide(s, tl, (float)st.Time);
        if (gcdSlot)
            return d.NextGcd >= 0 ? Map(job.Skills[d.NextGcd]) : "None";
        return d.Skill >= 0 && !job.Skills[d.Skill].IsGcd && d.ExecuteAt < 0.05f ? Map(job.Skills[d.Skill]) : "None";
    }

    private static string Map(SkillDef sk) => sk.Name.StartsWith("ElixirBurst") ? "ElixirBurst" : sk.Name.StartsWith("PerfectBalance") ? "PerfectBalance" : sk.Name.StartsWith("Brotherhood") ? "Brotherhood" : sk.Name;

    public static EngineState ReadState(JobDefinition job, BattleState st)
    {
        var g = st.PlayerGauge;
        var s = EngineState.Create(job);
        s.Targets = (byte)Math.Max(1, st.NumMeleeAOETargets);
        void Gauge(string name, int v) => s.Gauges[job.GaugeIndex(name)] = (short)v;
        Gauge(MnkDefinition.OpoFury, g.OpoFury);
        Gauge(MnkDefinition.RaptorFury, g.RaptorFury);
        Gauge(MnkDefinition.CoeurlFury, g.CoeurlFury);
        var opo = g.BeastChakra.Count(b => b == "Opo");
        var raptor = g.BeastChakra.Count(b => b == "Raptor");
        var coeurl = g.BeastChakra.Count(b => b == "Coeurl");
        Gauge(MnkDefinition.BeastOpo, opo);
        Gauge(MnkDefinition.BeastRaptor, raptor);
        Gauge(MnkDefinition.BeastCoeurl, coeurl);
        Gauge(MnkDefinition.BeastTotal, opo + raptor + coeurl);
        Gauge(MnkDefinition.Lunar, g.LunarNadi ? 1 : 0);
        Gauge(MnkDefinition.Solar, g.SolarNadi ? 1 : 0);
        Gauge(MnkDefinition.NadiCount, (g.LunarNadi ? 1 : 0) + (g.SolarNadi ? 1 : 0));
        Gauge(MnkDefinition.ChakraQ, Math.Min(40, g.Chakra * 4 + (int)(g.ChakraProgress * 4)));
        void Status(string name, double left, int stacks = 1)
        {
            if (left <= 0)
                return;
            var i = job.StatusIndex(name);
            s.StatusLeft[i] = (float)left;
            s.StatusStacks[i] = (byte)Math.Max(1, stacks);
        }
        if (g.PerfectBalanceLeft <= 0)
        {
            if (g.CurrentForm == "Opo")
                Status(MnkDefinition.OpoForm, 30);
            else if (g.CurrentForm == "Raptor")
                Status(MnkDefinition.RaptorForm, 30); // the emulator keeps forms without timers
            else if (g.CurrentForm == "Coeurl")
                Status(MnkDefinition.CoeurlForm, 30);
        }
        Status(MnkDefinition.Formless, g.FormlessFistLeft);
        if (g.PerfectBalanceStacks > 0)
            Status(MnkDefinition.PerfectBalance, g.PerfectBalanceLeft, g.PerfectBalanceStacks);
        if (opo + raptor + coeurl >= 3)
            Status(MnkDefinition.BlitzReady, Math.Max(0.1, g.BlitzLeft));
        Status(MnkDefinition.RiddleOfFire, g.RiddleOfFireLeft);
        Status(MnkDefinition.Brotherhood, g.BrotherhoodLeft);
        Status(MnkDefinition.MeditativeBrotherhood, g.BrotherhoodLeft);
        Status(MnkDefinition.FiresRumination, g.FiresReplyLeft);
        Status(MnkDefinition.WindsRumination, g.WindsReplyLeft);
        void Cd(string name, CooldownState c)
        {
            var i = job.CooldownIndex(name);
            var max = job.Cooldowns[i].MaxCharges;
            var charges = max > 1 ? (int)Math.Floor(c.Charges + 1e-6) : c.ReadyIn > 0 ? 0 : 1;
            s.Charges[i] = (byte)Math.Clamp(charges, 0, max);
            s.CdReadyIn[i] = charges < max ? (float)Math.Max(0, c.ReadyIn) : 0;
        }
        Cd(MnkDefinition.PerfectBalanceCD, st.Cooldowns.PerfectBalance);
        Cd(MnkDefinition.RiddleOfFireCD, st.Cooldowns.RiddleOfFire);
        Cd(MnkDefinition.BrotherhoodCD, st.Cooldowns.Brotherhood);
        Cd(MnkDefinition.RiddleOfWindCD, st.Cooldowns.RiddleOfWind);
        return s;
    }
}

// engine-compare [--weights <json>] [--budget ms] [--detail]: built-in policy vs MNK [Engine] on every battle scenario the engine covers
public static class MnkEngineCompare
{
    public static int Run(string[] args)
    {
        string Arg(string name, string fallback) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var path = Arg("--weights", "");
        var w = path.Length > 0 ? EngineWeights.Load(path) : MnkDefinition.DefaultWeights();
        var budget = Arg("--budget", "");
        if (budget.Length > 0)
            w.BudgetMs = float.Parse(budget, CultureInfo.InvariantCulture);
        var scenarios = ScenarioCatalog.SelectBattle("all").Where(MnkEnginePolicy.Covers).ToList();

        MnkActionSimulator.PolicyFactory = null;
        var old = new BattleEmulator().Run(scenarios);
        var micros = new List<double>();
        MnkActionSimulator.PolicyMicros = micros;
        MnkActionSimulator.PolicyFactory = _ => new MnkEnginePolicy(w.Clone()).Decide;
        var eng = new BattleEmulator().Run(scenarios);
        MnkActionSimulator.PolicyFactory = null;
        MnkActionSimulator.PolicyMicros = null;

        var rows = old.Scenarios.Zip(eng.Scenarios).ToList();
        double Sum(IEnumerable<ScenarioResult> r, string key) => r.Sum(x => x.Metrics.GetValueOrDefault(key));
        Console.WriteLine($"engine-covered scenarios: {rows.Count}, depth {w.HorizonGcds}, budget {w.BudgetMs} ms");
        Console.WriteLine("| metric | built-in | engine |");
        Console.WriteLine("|---|---:|---:|");
        Console.WriteLine($"| total potency (sum) | {Sum(old.Scenarios, "TotalPotency"):f0} | {Sum(eng.Scenarios, "TotalPotency"):f0} |");
        Console.WriteLine($"| scenarios with any hard fail | {rows.Count(r => r.First.HardFails.Count > 0)} | {rows.Count(r => r.Second.HardFails.Count > 0)} |");
        foreach (var key in new[] { "PhantomRushCount", "MasterfulBlitzCount", "PerfectBalanceUses", "ChakraOvercapFrames", "MeasuredGcdIdleTime" })
            Console.WriteLine($"| {key} (sum) | {Sum(old.Scenarios, key):f1} | {Sum(eng.Scenarios, key):f1} |");
        foreach (var rule in rows.SelectMany(r => r.First.HardFails.Concat(r.Second.HardFails)).Distinct().OrderBy(x => x))
            Console.WriteLine($"| fail: {rule} (scenarios) | {rows.Count(r => r.First.HardFails.Contains(rule))} | {rows.Count(r => r.Second.HardFails.Contains(rule))} |");
        if (micros.Count > 0)
        {
            var sorted = micros.OrderBy(x => x).ToList();
            Console.WriteLine($"| engine time per slot mean / p99 (us) | - | {sorted.Average():f1} / {sorted[(int)(sorted.Count * 0.99)]:f1} |");
        }
        if (args.Contains("--detail"))
            foreach (var (o, e) in rows.Where(r => r.Second.HardFails.Count > 0).Take(40))
                Console.WriteLine($"FAIL {e.ScenarioName}: {string.Join(",", e.HardFails.Distinct())} potency {o.Metrics.GetValueOrDefault("TotalPotency"):f0} -> {e.Metrics.GetValueOrDefault("TotalPotency"):f0}");
        if (args.Contains("--counts"))
            foreach (var (o, e) in rows.Take(1))
            {
                string Counts(ScenarioResult r) => string.Join(" ", r.Frames.SelectMany(f => f.SelectedOGCD.Append(f.SelectedGCD ?? "")).Where(a => a != "").GroupBy(a => a).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}={g.Count()}"));
                Console.WriteLine($"COUNTS old {o.ScenarioName}: {Counts(o)}");
                Console.WriteLine($"COUNTS eng {e.ScenarioName}: {Counts(e)}");
            }
        if (args.Contains("--timings"))
            foreach (var (o, e) in rows.Where(r => r.Second.HardFails.Count > 0).Take(2))
                Console.WriteLine($"TIMINGS {e.ScenarioName}: BH [{string.Join(",", e.Timings.Brotherhood.Select(t => t.ToString("f1")))}] RoF [{string.Join(",", e.Timings.RiddleOfFire.Select(t => t.ToString("f1")))}] PB [{string.Join(",", e.Timings.PerfectBalance.Select(t => t.ToString("f1")))}] PR [{string.Join(",", e.Timings.PhantomRush.Select(t => t.ToString("f1")))}] blitz [{string.Join(",", e.Timings.MasterfulBlitz.Select(t => t.ToString("f1")))}]");
        return 0;
    }
}
