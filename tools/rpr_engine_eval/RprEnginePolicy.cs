using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using RprRegression;

namespace RprEngineEval;

// Drives the RPR harness with the rotation engine: harness state -> EngineState, engine decisions -> one GCD window
// (GCD + weaves). Situations the level-100 engine definition does not cover fall back to the built-in policy (null).
public sealed class RprEnginePolicy
{
    private readonly EngineWeights _weights;
    private readonly Dictionary<int, RotationEngine> _engines = []; // per GCD length (ms)
    public int EngineWindows;
    public int FallbackWindows;
    public double DebugFrom = double.MaxValue, DebugTo = double.MinValue;
    public List<string[]> DebugLines = [];
    private readonly EvalContext _ctx = new();
    private readonly List<string> _ogcds = new(2);

    // build the engine for this GCD length up front, so its one-time setup is not counted as decision time
    public void Prepare(double gcd) => EngineFor(gcd);

    public RprEnginePolicy(EngineWeights weights) => _weights = weights;

    public static bool Covers(ScenarioDefinition sc) => sc.Level >= 100 && sc.SkillRotation == SkillRotationMode.Normal;

    private RotationEngine EngineFor(double gcd)
    {
        var key = (int)Math.Round(gcd * 1000);
        if (!_engines.TryGetValue(key, out var e))
            _engines[key] = e = new RotationEngine(RprDefinition.Build((float)gcd), _weights);
        return e;
    }

    public (string? Gcd, List<string> Ogcds)? Decide(ScenarioDefinition sc, RprState st, RprContext ctx, double time)
    {
        if (!Covers(sc) || ctx.RotationMode != RotationMode.Full || ctx.TargetAvailable && (!ctx.MeleeAvailable || !ctx.HaveTarget))
        {
            ++FallbackWindows;
            return null;
        }
        ++EngineWindows;
        var engine = EngineFor(sc.Gcd);
        var job = engine.Job;
        var s = ReadState(job, sc, st, ctx, time);
        var tl = BuildTimeline(sc, time);

        var ogcds = new List<string>(2);
        var d = engine.Decide(s, tl, (float)time);
        if (time >= DebugFrom && time <= DebugTo)
            Console.WriteLine($"  t={time:f1} root={(d.Skill >= 0 ? job.Skills[d.Skill].Name : "wait")} depth={d.Depth} nodes={d.Nodes} " + string.Join(" ", job.Skills.Where(sk => !float.IsNaN(engine.LastRootValue(sk.Index))).Select(sk => $"{sk.Name}={engine.LastRootValue(sk.Index):f0}")));
        if (time >= DebugFrom && time <= DebugTo)
            foreach (var line in DebugLines)
            {
                var idx = line.Select(n => job.SkillIndex(n)).ToArray();
                Console.WriteLine($"   line {string.Join(">", line)}:");
                engine.ExplainLine(s, tl, (float)time, idx, Console.WriteLine);
            }
        string? gcd = null;
        if (d.Skill >= 0 && job.Skills[d.Skill].IsGcd)
        {
            var skill = job.Skills[d.Skill];
            gcd = RprDefinition.HarnessName(skill, s, job);
            var ctxEval = _ctx;
            Simulator.Execute(job, ref s, tl, skill, ctxEval);
            // weaves after the GCD: ask again from the post-GCD state until the engine wants the next GCD
            for (var i = 0; i < 2; ++i)
            {
                var local = s;
                Simulator.Advance(job, ref local, local.AnimLockAt - local.Time, ctxEval);
                var w = engine.Decide(local, tl, (float)time + local.Time);
                if (w.Skill < 0 || job.Skills[w.Skill].IsGcd)
                    break;
                var og = job.Skills[w.Skill];
                // harness rule: a weave Enshroud is judged on the gauge at the start of the window (except after Plentiful Harvest); wait one window
                if (og.Name == "Enshroud" && st.BlueGauge < 50 && !st.IdealHost && gcd != "PlentifulHarvest")
                    break;
                // harness rule: the potion cooldown is judged at the start of the window
                if (og.Name == "Potion" && st.PotionReadyIn > 0.1)
                    break;
                ogcds.Add(RprDefinition.HarnessName(og, local, job));
                Simulator.Execute(job, ref local, tl, og, ctxEval);
                s = local;
            }
        }
        // harness output convention: before 240 s the potion is listed before Arcane Circle in a shared window (the engine presses AC first)
        var ac = ogcds.IndexOf("ArcaneCircle");
        var pot = ogcds.IndexOf("Potion");
        if (ac >= 0 && pot > ac && time < 240)
            (ogcds[ac], ogcds[pot]) = (ogcds[pot], ogcds[ac]);
        // the engine found no usable GCD although there is a target (e.g. one Lemure left but Communio has no target): let the built-in policy decide
        if (gcd == null && ctx.TargetAvailable)
        {
            --EngineWindows;
            ++FallbackWindows;
            return null;
        }
        return (gcd, ogcds);
    }

    public static EngineState ReadState(JobDefinition job, ScenarioDefinition sc, RprState st, RprContext ctx, double time = 0)
    {
        var s = EngineState.Create(job);
        s.Targets = (byte)Math.Max(1, ctx.AoeTargets);
        for (var i = 0; i < job.Shapes.Length; ++i)
            s.ShapeTargets[i] = (byte)Math.Max(1, job.Shapes[i].Kind == AoeShape.Cone ? ctx.ConeTargets : ctx.AoeTargets);
        // targeting the harness reports as unavailable (null best AoE / line / cone target)
        ulong Mask(params string[] names) { ulong m = 0; foreach (var n in names) m |= 1UL << job.SkillIndex(n); return m; }
        if (!ctx.BestRangedAoeTargetAvailable)
            s.DisabledSkills |= Mask("Communio", "Perfectio", "HarvestMoon", "Gluttony", "Sacrificium");
        if (!ctx.BestLineTargetAvailable)
            s.DisabledSkills |= Mask("PlentifulHarvest");
        if (!ctx.BestConeTargetAvailable)
            s.DisabledSkills |= Mask("Guillotine", "ExecutionersGuillotine", "GrimReaping", "GrimSwathe", "LemuresScythe");
        s.Gauges[job.GaugeIndex(RprDefinition.Soul)] = (short)st.RedGauge;
        s.Gauges[job.GaugeIndex(RprDefinition.Shroud)] = (short)st.BlueGauge;
        s.Gauges[job.GaugeIndex(RprDefinition.Lemure)] = (short)st.BlueSouls;
        s.Gauges[job.GaugeIndex(RprDefinition.Void)] = (short)st.PurpleSouls;

        void Status(string name, double left, int stacks = 1)
        {
            if (left <= 0)
                return;
            var i = job.StatusIndex(name);
            s.StatusLeft[i] = (float)left;
            s.StatusStacks[i] = (byte)Math.Max(1, stacks);
        }
        Status(RprDefinition.DeathsDesign, st.DeathsDesignLeft);
        Status(RprDefinition.ArcaneCircle, st.ArcaneCircleLeft);
        if (st.Reaver == ReaverState.SoulReaver)
            Status(RprDefinition.SoulReaver, st.ReaverLeft, st.ReaverStacks);
        else if (st.Reaver == ReaverState.Executioner)
            Status(RprDefinition.Executioner, st.ReaverLeft, st.ReaverStacks);
        Status(RprDefinition.EnhancedGibbet, st.EnhancedGibbetLeft);
        Status(RprDefinition.EnhancedGallows, st.EnhancedGallowsLeft);
        Status(RprDefinition.EnhancedVoid, st.EnhancedVoidReapingLeft);
        Status(RprDefinition.EnhancedCross, st.EnhancedCrossReapingLeft);
        if (st.BlueSouls > 0)
            Status(RprDefinition.Enshrouded, Math.Max(0.1, st.EnshroudLeft));
        if (st.Oblatio)
            Status(RprDefinition.Oblatio, st.OblatioLeft);
        if (st.IdealHost)
            Status(RprDefinition.IdealHost, st.IdealHostLeft);
        if (st.PerfectioOcculta)
            Status(RprDefinition.PerfectioOcculta, st.PerfectioOccultaLeft);
        if (st.PerfectioParata)
            Status(RprDefinition.PerfectioParata, st.PerfectioParataLeft);
        if (st.ImmortalSacrifice > 0)
            Status(RprDefinition.ImmortalSacrifice, st.ImmortalSacrificeLeft, st.ImmortalSacrifice);
        Status(RprDefinition.Bloodsown, st.BloodsownLeft);
        if (st.Soulsow)
            Status(RprDefinition.Soulsow, 3600);
        if (st.PotionUsed && st.PotionReadyIn > 240)
            Status(RprDefinition.Medicated, st.PotionReadyIn - 240);

        void Cd(string name, double readyIn)
        {
            var i = job.CooldownIndex(name);
            s.Charges[i] = (byte)(readyIn > 0 ? 0 : 1);
            s.CdReadyIn[i] = (float)Math.Max(0, readyIn);
        }
        var ss = job.CooldownIndex(RprDefinition.SoulSliceCD);
        var whole = (int)Math.Floor(st.SoulSliceCharges); // no epsilon: the harness treats 0.9999 as "no charge"
        s.Charges[ss] = (byte)Math.Min(2, whole);
        s.CdReadyIn[ss] = whole >= 2 ? 0 : (float)((1 - (st.SoulSliceCharges - whole)) * 30);
        Cd(RprDefinition.ArcaneCircleCD, st.ArcaneCircleReadyIn);
        Cd(RprDefinition.GluttonyCD, st.GluttonyReadyIn);
        Cd(RprDefinition.EnshroudCD, st.EnshroudReadyIn);
        if (sc.Potion == PotionMode.Off || sc.Potion == PotionMode.EvenBurstExceptOpener && time < 30)
            Cd(RprDefinition.PotionCD, 10000);
        else
            Cd(RprDefinition.PotionCD, st.PotionReadyIn);

        if (st.ComboRemaining > 0 && st.ComboLast is "Slice" or "WaxingSlice" or "SpinningScythe")
        {
            s.ComboSkill = (byte)job.SkillIndex(st.ComboLast);
            s.ComboLeft = (float)st.ComboRemaining;
        }
        return s;
    }

    // party raid buffs every 120 s from 7.8 s (harness convention), target losses forecast 30 s ahead, exact kill time
    public static EngineTimeline BuildTimeline(ScenarioDefinition sc, double time)
    {
        var tl = EngineTimeline.Open();
        tl.FightEndIn = (float)(sc.KillTime - time);
        for (var t = 7.8; t < sc.KillTime && tl.NumBuffs < EngineLimits.MaxWindows; t += 120)
            if (t + 20 > time && t < time + 360)
                tl.AddBuff((float)(t - time), (float)(t + 20 - time), 1.05f);
        var version = tl.NumBuffs;
        foreach (var e in sc.Events)
        {
            if (e.Type != ScenarioEventType.TargetLost || e.End <= time || e.Start > time + 30 || e.End - e.Start < 8.5 && e.Start > time)
                continue;
            tl.AddDowntime((float)(e.Start - time), (float)(e.End - time));
            version = version * 31 + (int)e.Start;
        }
        tl.Version = version;
        return tl;
    }
}
