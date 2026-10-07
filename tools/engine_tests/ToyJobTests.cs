using BossMod.Autorotation.Engine;
using EngineTools;
using Xunit;
using Xunit.Abstractions;

namespace EngineTests;

public sealed class ToyJobTests(ITestOutputHelper output)
{
    private static readonly JobDefinition Job = ToyJob.Build();

    private static RotationEngine NewEngine() => new(Job, new EngineWeights { BudgetMs = 50 }); // generous budget: tests check decisions, not speed

    private static EngineState State(int heat = 0, string? combo = null, bool burstReady = false, int strikeCharges = 0, float gcdIn = 0, float animLockIn = 0)
    {
        var s = EngineState.Create(Job);
        s.Gauges[Job.GaugeIndex("Heat")] = (short)heat;
        if (combo != null)
        {
            s.ComboSkill = (byte)Job.SkillIndex(combo);
            s.ComboLeft = 25;
        }
        var burst = Job.CooldownIndex("BurstCD");
        if (!burstReady)
        {
            s.Charges[burst] = 0;
            s.CdReadyIn[burst] = 100;
        }
        var strike = Job.CooldownIndex("StrikeCD");
        s.Charges[strike] = (byte)strikeCharges;
        s.CdReadyIn[strike] = strikeCharges < 2 ? 25 : 0;
        s.GcdReadyAt = gcdIn;
        s.AnimLockAt = animLockIn;
        return s;
    }

    private static EngineTimeline Timeline(float fightEnd = 600, float buffStart = float.NaN, float buffEnd = float.NaN, float mult = 1.3f)
    {
        var tl = EngineTimeline.Open();
        tl.FightEndIn = fightEnd;
        if (!float.IsNaN(buffStart))
            tl.AddBuff(buffStart, buffEnd, mult);
        tl.Version = 1;
        return tl;
    }

    private string Name(int skill) => skill < 0 ? "<wait>" : Job.Skills[skill].Name;

    private EngineDecision Decide(in EngineState s, in EngineTimeline tl)
    {
        var d = NewEngine().Decide(s, tl, 0);
        output.WriteLine($"skill={Name(d.Skill)} nextGcd={Name(d.NextGcd)} at={d.ExecuteAt:f2} value={d.Value:f0} depth={d.Depth} nodes={d.Nodes}");
        return d;
    }

    [Fact]
    public void ContinuesCombo()
    {
        Assert.Equal("Cut", Name(Decide(State(combo: "Slash"), Timeline()).NextGcd));
        Assert.Equal("Finish", Name(Decide(State(combo: "Cut"), Timeline()).NextGcd));
        Assert.Equal("Slash", Name(Decide(State(), Timeline()).NextGcd));
    }

    [Fact]
    public void HoldsGaugeForUpcomingBurst()
    {
        // 50 Heat, raid buff window opens in 15 s: keep comboing (the combo adds at most 20 before the window, no overcap)
        var d = Decide(State(heat: 50, combo: "Slash"), Timeline(buffStart: 15, buffEnd: 35));
        Assert.NotEqual("Blast", Name(d.NextGcd));
    }

    [Fact]
    public void SpendsGaugeInsideBurst()
    {
        // play through a 10 s raid window starting with 60 Heat: the spender must land inside it (order within the window is free)
        var played = Play(State(heat: 60, combo: "Slash"), Timeline(buffStart: 0, buffEnd: 10), seconds: 10);
        output.WriteLine(string.Join(", ", played.Select(p => $"{p.time:f1}:{p.name}")));
        Assert.Contains(played, p => p.name == "Blast" && p.time < 10);
    }

    // executes the engine's choices against the shared mechanics for `seconds`
    private static List<(float time, string name)> Play(EngineState s, EngineTimeline tl, float seconds)
    {
        var engine = NewEngine();
        var ctx = new EvalContext();
        var res = new List<(float, string)>();
        var clock = 0f;
        for (var guard = 0; clock < seconds && guard < 200; ++guard)
        {
            var local = s;
            local.Time = 0;
            local.GcdReadyAt -= clock;
            local.AnimLockAt -= clock;
            var shifted = tl;
            shifted.FightEndIn -= clock;
            for (var i = 0; i < shifted.NumBuffs; ++i)
            {
                shifted.Buffs[i].Start -= clock;
                shifted.Buffs[i].End -= clock;
            }
            var d = engine.Decide(local, shifted, clock);
            Simulator.Advance(Job, ref s, clock + d.ExecuteAt - s.Time, ctx);
            clock += d.ExecuteAt;
            if (d.Skill < 0)
            {
                Simulator.Advance(Job, ref s, 0.5f, ctx);
                clock += 0.5f;
                continue;
            }
            var skill = Job.Skills[d.Skill];
            if (clock >= seconds)
                break;
            Simulator.Execute(Job, ref s, tl, skill, ctx);
            res.Add((clock, skill.Name));
        }
        return res;
    }

    [Fact]
    public void AvoidsOvercap()
    {
        // 100 Heat and the next combo step would add 10: spend now even without a burst in sight
        var d = Decide(State(heat: 100, combo: "Cut"), Timeline());
        Assert.Equal("Blast", Name(d.NextGcd));
    }

    [Fact]
    public void DumpsGaugeBeforeFightEnd()
    {
        var d = Decide(State(heat: 50, combo: "Slash"), Timeline(fightEnd: 3, buffStart: 30, buffEnd: 50));
        Assert.Equal("Blast", Name(d.NextGcd));
    }

    [Fact]
    public void AlignsSelfBuffWithRaidWindow()
    {
        // Burst ready, weave slot open (GCD in 1.5 s), strong raid window (x2 so alignment clearly matters for this toy's 20% buff).
        // Window 8 s away: hold. Window active: press it now.
        var hold = Decide(State(burstReady: true, combo: "Slash", gcdIn: 1.5f), Timeline(buffStart: 8, buffEnd: 28, mult: 2.0f));
        Assert.NotEqual("Burst", Name(hold.Skill));
        var press = Decide(State(burstReady: true, combo: "Slash", gcdIn: 1.5f), Timeline(buffStart: 0, buffEnd: 20, mult: 2.0f));
        Assert.Equal("Burst", Name(press.Skill));
    }

    [Fact]
    public void ChargesDoNotCapButOneIsHeldForBurst()
    {
        // capped at 2 charges, no burst soon: use one (otherwise the recharge is wasted)
        var capped = Decide(State(strikeCharges: 2, combo: "Slash", gcdIn: 1.5f), Timeline());
        Assert.Equal("Strike", Name(capped.Skill));
        // 1 charge (next in 25 s), burst in 10 s: hold it for the window
        var held = Decide(State(strikeCharges: 1, combo: "Slash", gcdIn: 1.5f), Timeline(buffStart: 10, buffEnd: 30));
        Assert.NotEqual("Strike", Name(held.Skill));
    }

    [Fact]
    public void ReusesResultWhileStateUnchanged()
    {
        var engine = NewEngine();
        var s = State(combo: "Slash", gcdIn: 1.0f);
        var tl = Timeline();
        var first = engine.Decide(s, tl, 0);
        var second = engine.Decide(s, tl, 0.01f);
        Assert.False(first.Reused);
        Assert.True(second.Reused);
        Assert.Equal(first.Skill, second.Skill);
    }

    [Fact]
    public void FullScenarioBeatsGreedyFiller()
    {
        var sc = new EngineScenario { Name = "toy-300s", KillTime = 300 };
        var engine = ScenarioRunner.Run(Job, new EngineWeights { BudgetMs = 5 }, sc);
        var fillerOnly = new JobAnalysis(Job).FillerPps * sc.KillTime;
        output.WriteLine($"engine damage {engine.Damage:f0} (dps {engine.Dps:f1}) vs filler-only {fillerOnly:f0}; decisions {engine.Decisions}");
        Assert.True(engine.Damage > fillerOnly * 1.15f); // the filler loop is the real 1-2-3 combo (a middle step out of combo breaks it)
    }
}
