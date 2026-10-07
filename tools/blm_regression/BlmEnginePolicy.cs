using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;

namespace BlmRegression;

// Drives the BLM regression emulator with the rotation engine (Custom/Engine, BlmDefinition): emulator frame -> EngineState,
// engine decisions -> the oGCDs woven before the GCD and the GCD. Scenarios outside the level-100 definition or with
// non-default strategy tracks (the engine has no Force / Delay switches yet) fall back to the built-in policy (null).
public sealed class BlmEnginePolicy(EngineWeights weights)
{
    private readonly RotationEngine _engine = new(BlmDefinition.Build(), weights);
    private readonly EvalContext _ctx = new();
    private readonly List<BlmAction> _ogcds = new(2);
    public int EngineFrames;
    public int FallbackFrames;
    private readonly List<SkillDef> _pendingWeaves = new(2);

    public static bool Covers(BlmScenario sc)
        => sc.Level >= 100 && sc.Rotation is BlmRotationStrategy.Automatic or BlmRotationStrategy.FuturePlanner
        && !sc.RequiresPhantomCoverage && sc.Thunder == BlmThunderStrategy.Automatic && sc.Manafont == BlmOffensiveStrategy.Automatic
        && sc.Triplecast == BlmTriplecastStrategy.Automatic && sc.Leylines is BlmLeylinesStrategy.EvenBurst or BlmLeylinesStrategy.FuturePlanner or BlmLeylinesStrategy.OpenerOnly
        && sc.EncounterHint is not (BlmEncounterHintStrategy.HoldBurst or BlmEncounterHintStrategy.ForceBurst);

    public (BlmAction Gcd, IReadOnlyList<BlmAction> Ogcds)? Decide(BlmScenario sc, BlmPolicyView v)
    {
        if (!Covers(sc) || v.Rotation is not (BlmRotationStrategy.Automatic or BlmRotationStrategy.FuturePlanner))
        {
            ++FallbackFrames;
            return null;
        }
        ++EngineFrames;
        var job = _engine.Job;
        var s = ReadState(job, v);
        var tl = BuildTimeline(sc, v);
        // Ley Lines would have to be left soon (the emulator's LeyLinesUnsafe forecast; in the game a forced-movement forecast does this)
        if (v.LeyLinesUnsafeIn <= 6)
            s.DisabledSkills |= 1UL << job.SkillIndex("LeyLines");
        _ogcds.Clear();
        var now = (float)v.Time;
        // weaves the engine planned after the previous frame's GCD: the emulator applies them at the start of this frame, before its GCD
        foreach (var w in _pendingWeaves)
        {
            if (!Simulator.IsLegal(job, s, tl, w))
                continue;
            _ogcds.Add(Map(w));
            Simulator.Execute(job, ref s, tl, w, _ctx);
        }
        _pendingWeaves.Clear();
        s.AnimLockAt = 0; // the emulator treats a frame's weaves as instantaneous

        var d = _engine.Decide(s, tl, now);
        if (d.Skill >= 0 && !job.Skills[d.Skill].IsGcd && d.ExecuteAt < 0.01f && _ogcds.Count < 2)
        {
            // the engine wants an ability before the GCD
            var og = job.Skills[d.Skill];
            _ogcds.Add(Map(og));
            Simulator.Execute(job, ref s, tl, og, _ctx);
            s.AnimLockAt = 0;
            d = _engine.Decide(s, tl, now);
        }
        if (d.NextGcd < 0)
            return (BlmAction.None, _ogcds.ToArray());
        var gcd = job.Skills[d.NextGcd];
        Simulator.Execute(job, ref s, tl, gcd, _ctx);

        // weaves after the GCD (at most two, each finishing before the next GCD)
        for (var i = 0; i < 2; ++i)
        {
            Simulator.Advance(job, ref s, MathF.Max(0, s.AnimLockAt - s.Time), _ctx);
            var w = _engine.Decide(s, tl, now + s.Time);
            if (w.Skill < 0 || job.Skills[w.Skill].IsGcd || s.Time + w.ExecuteAt + 0.6f > s.GcdReadyAt + 0.01f)
                break;
            var og = job.Skills[w.Skill];
            Simulator.Advance(job, ref s, w.ExecuteAt, _ctx);
            _pendingWeaves.Add(og);
            Simulator.Execute(job, ref s, tl, og, _ctx);
        }
        return (Map(gcd), _ogcds.ToArray());
    }

    private static BlmAction Map(SkillDef sk) => sk.ActionId switch
    {
        BlmDefinition.AidFire3 => BlmAction.Fire3,
        BlmDefinition.AidFire4 => BlmAction.Fire4,
        BlmDefinition.AidDespair => BlmAction.Despair,
        BlmDefinition.AidFlareStar => BlmAction.FlareStar,
        BlmDefinition.AidFlare => BlmAction.Flare,
        BlmDefinition.AidHighFire2 => BlmAction.Fire2,
        BlmDefinition.AidParadox => BlmAction.Paradox,
        BlmDefinition.AidBlizzard3 => BlmAction.Blizzard3,
        BlmDefinition.AidBlizzard4 => BlmAction.Blizzard4,
        BlmDefinition.AidFreeze => BlmAction.Freeze,
        BlmDefinition.AidHighBlizzard2 => BlmAction.Blizzard2,
        BlmDefinition.AidHighThunder => BlmAction.HighThunder,
        BlmDefinition.AidHighThunder2 => BlmAction.HighThunder2,
        BlmDefinition.AidXenoglossy => BlmAction.Xenoglossy,
        BlmDefinition.AidFoul => BlmAction.Foul,
        BlmDefinition.AidUmbralSoul => BlmAction.UmbralSoul,
        BlmDefinition.AidTranspose => BlmAction.Transpose,
        BlmDefinition.AidManafont => BlmAction.Manafont,
        BlmDefinition.AidAmplifier => BlmAction.Amplifier,
        BlmDefinition.AidLeyLines => BlmAction.LeyLines,
        BlmDefinition.AidTriplecast => BlmAction.Triplecast,
        BlmDefinition.AidSwiftcast => BlmAction.Swiftcast,
        _ => throw new InvalidOperationException($"no emulator action for {sk.Name}")
    };

    public static EngineState ReadState(JobDefinition job, BlmPolicyView v)
    {
        var s = EngineState.Create(job);
        s.Targets = (byte)Math.Max(1, v.Targets);
        s.Gauges[job.GaugeIndex(BlmDefinition.MP)] = (short)v.MP;
        s.Gauges[job.GaugeIndex(BlmDefinition.AstralFire)] = (short)Math.Max(0, v.Element);
        s.Gauges[job.GaugeIndex(BlmDefinition.UmbralIce)] = (short)Math.Max(0, -v.Element);
        s.Gauges[job.GaugeIndex(BlmDefinition.Hearts)] = (short)v.Hearts;
        s.Gauges[job.GaugeIndex(BlmDefinition.Polyglot)] = (short)v.Polyglot;
        s.Gauges[job.GaugeIndex(BlmDefinition.AstralSoul)] = (short)v.AstralSoul;
        s.Gauges[job.GaugeIndex(BlmDefinition.Paradox)] = (short)(v.Paradox ? 1 : 0);
        void Status(string name, double left, int stacks = 1)
        {
            if (left <= 0)
                return;
            var i = job.StatusIndex(name);
            s.StatusLeft[i] = (float)left;
            s.StatusStacks[i] = (byte)Math.Max(1, stacks);
        }
        if (v.Element != 0)
            Status(BlmDefinition.PolyglotTimer, v.NextPolyglot > 0 ? v.NextPolyglot : 30);
        if (v.Firestarter)
            Status(BlmDefinition.Firestarter, 30); // the emulator has no proc timers
        if (v.Thunderhead)
            Status(BlmDefinition.Thunderhead, 30);
        Status(BlmDefinition.Thunder, v.ThunderLeft);
        if (v.TriplecastStacks > 0)
            Status(BlmDefinition.Triplecast, v.TriplecastLeft, v.TriplecastStacks);
        Status(BlmDefinition.Swiftcast, v.SwiftcastLeft);
        if (v.InLeyLines)
            Status(BlmDefinition.LeyLines, v.LeyLinesLeft);

        void Cd(string name, double readyIn, int charges = -1, double chargeReadyIn = 0)
        {
            var i = job.CooldownIndex(name);
            if (charges >= 0)
            {
                s.Charges[i] = (byte)charges;
                s.CdReadyIn[i] = charges < job.Cooldowns[i].MaxCharges ? (float)chargeReadyIn : 0;
            }
            else
            {
                s.Charges[i] = (byte)(readyIn > 0 ? 0 : 1);
                s.CdReadyIn[i] = (float)Math.Max(0, readyIn);
            }
        }
        Cd(BlmDefinition.TransposeCD, v.TransposeReadyIn);
        Cd(BlmDefinition.ManafontCD, v.ManafontReadyIn);
        Cd(BlmDefinition.AmplifierCD, v.AmplifierReadyIn);
        Cd(BlmDefinition.SwiftcastCD, v.SwiftcastReadyIn);
        Cd(BlmDefinition.LeyLinesCD, 0, v.LeyLinesCharges, v.LeyLinesChargeReadyIn);
        Cd(BlmDefinition.TriplecastCD, 0, v.TriplecastCharges, v.TriplecastChargeReadyIn);
        return s;
    }

    // target losses / downtime become downtime windows, forced movement and look-away become no-cast windows (30 s ahead)
    public static EngineTimeline BuildTimeline(BlmScenario sc, BlmPolicyView v)
    {
        var tl = EngineTimeline.Open();
        tl.FightEndIn = (float)(sc.Duration - v.Time);
        var version = 1;
        foreach (var e in sc.Events)
        {
            if (e.End <= v.Time || e.Start > v.Time + 30)
                continue;
            var start = (float)Math.Max(0, e.Start - v.Time);
            var end = (float)(e.End - v.Time);
            switch (e.Type)
            {
                case BlmScenarioEventType.TargetLost:
                case BlmScenarioEventType.Downtime:
                    tl.AddDowntime(start, end);
                    break;
                case BlmScenarioEventType.ForcedMove:
                case BlmScenarioEventType.LookAway:
                    tl.AddNoCast(start, end);
                    break;
                default:
                    continue;
            }
            version = version * 31 + (int)(e.Start * 10) + (int)e.Type;
        }
        tl.Version = version;
        return tl;
    }
}
