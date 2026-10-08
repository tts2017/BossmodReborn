using BossMod.Autorotation.Engine;
using Xunit;
using Xunit.Abstractions;

namespace EngineTests;

// StatusLeftAtMost (a refresh window checked at every node) and EngineState.HeldSkills / HeldUntil (a hold the search plans past):
// both let the search place the skill later in its horizon, where a root Forbid (DisabledSkills) hides it for the whole horizon.
public sealed class RefreshAndHoldTests(ITestOutputHelper output)
{
    private static JobDefinition BuildJob() => new JobBuilder("TOY", baseGcd: 2.5f)
        .Gauge("Heat", max: 100)
        .Status("Poison", maxDuration: 30, damageMultiplier: 1.1f) // a debuff: letting it drop costs (the DoT value alone has no tick-loss term)
        .Cooldown("StrikeCD", recast: 30, maxCharges: 2)
        .Gcd("Slash", potency: 200).ComboNeutral()
        .Gcd("Dot", potency: 100).ComboNeutral().Dot("Poison", 30, 60).RequiresStatusLeftAtMost("Poison", 5)
        .Ogcd("Strike", potency: 300, cooldown: "StrikeCD")
        .Build();

    private static EngineTimeline Timeline()
    {
        var tl = EngineTimeline.Open();
        tl.FightEndIn = 600;
        tl.Version = 1;
        return tl;
    }

    [Fact]
    public void StatusLeftAtMostSemantics()
    {
        var job = BuildJob();
        var c = new Condition(ConditionKind.StatusLeftAtMost, (short)job.StatusIndex("Poison"), 5);
        var s = EngineState.Create(job);
        Assert.True(Simulator.Check(s, c)); // inactive
        s.StatusLeft[job.StatusIndex("Poison")] = 5;
        Assert.True(Simulator.Check(s, c));
        s.StatusLeft[job.StatusIndex("Poison")] = 5.5f;
        Assert.False(Simulator.Check(s, c));
    }

    [Fact]
    public void RefreshIsPlannedInsideTheWindow()
    {
        // 8 s of DoT left: the refresh is illegal now and the search plays filler until it is, then refreshes (within the horizon)
        var job = BuildJob();
        var s = EngineState.Create(job);
        s.StatusLeft[job.StatusIndex("Poison")] = 8;
        var played = Play(job, s, Timeline(), 12);
        output.WriteLine(string.Join(", ", played.Select(p => $"{p.time:f1}:{p.name}")));
        var dot = played.FindIndex(p => p.name == "Dot");
        Assert.True(dot >= 0, "Dot pressed");
        Assert.True(played[dot].time >= 3 && played[dot].time < 12);
    }

    [Fact]
    public void HeldSkillReturnsAfterTheHold()
    {
        var job = BuildJob();
        // two Strike charges: pressed at once normally; held 3 s it goes after the hold, while a Forbid hides it for the horizon
        var s = EngineState.Create(job);
        s.GcdReadyAt = 1.5f;
        var d0 = new RotationEngine(job, new EngineWeights { BudgetMs = 50 }).Decide(s, Timeline(), 0);
        Assert.Equal("Strike", job.Skills[d0.Skill].Name);

        var held = s;
        held.HeldSkills = 1UL << job.SkillIndex("Strike");
        held.HeldUntil = 3;
        Assert.NotEqual(s.Hash(job), held.Hash(job));
        var d1 = new RotationEngine(job, new EngineWeights { BudgetMs = 50 }).Decide(held, Timeline(), 0);
        Assert.NotEqual("Strike", d1.Skill >= 0 ? job.Skills[d1.Skill].Name : "wait");
        var played = Play(job, held, Timeline(), 8);
        output.WriteLine(string.Join(", ", played.Select(p => $"{p.time:f1}:{p.name}")));
        var strike = played.FindIndex(p => p.name == "Strike");
        Assert.True(strike >= 0 && played[strike].time >= 3 && played[strike].time < 8, "Strike after the hold");

        // the hold has no effect once it is over
        var over = held;
        over.HeldUntil = 0;
        Assert.Equal(s.Hash(job), over.Hash(job));
        var d2 = new RotationEngine(job, new EngineWeights { BudgetMs = 50 }).Decide(over, Timeline(), 0);
        Assert.Equal("Strike", job.Skills[d2.Skill].Name);
    }

    // executes the engine's choices against the shared mechanics for `seconds` (the state's absolute hold time moves with the clock)
    private static List<(float time, string name)> Play(JobDefinition job, EngineState s, EngineTimeline tl, float seconds)
    {
        var engine = new RotationEngine(job, new EngineWeights { BudgetMs = 50, SwitchMargin = 0 }); // no hysteresis: the toy lines tie often
        var ctx = new EvalContext();
        var res = new List<(float, string)>();
        var clock = 0f;
        for (var guard = 0; clock < seconds && guard < 200; ++guard)
        {
            var local = s;
            local.Time = 0;
            local.GcdReadyAt -= clock;
            local.AnimLockAt -= clock;
            local.HeldUntil -= clock;
            var shifted = tl;
            shifted.FightEndIn -= clock;
            engine.InvalidateCache(); // the toy state can repeat between steps (no combo); the real adapter is driven by the game clock
            var d = engine.Decide(local, shifted, clock);
            Simulator.Advance(job, ref s, clock + d.ExecuteAt - s.Time, ctx);
            clock += d.ExecuteAt;
            if (d.Skill < 0)
            {
                Simulator.Advance(job, ref s, 0.5f, ctx);
                clock += 0.5f;
                continue;
            }
            var skill = job.Skills[d.Skill];
            if (clock >= seconds)
                break;
            Simulator.Execute(job, ref s, tl, skill, ctx);
            res.Add((clock, skill.Name));
        }
        return res;
    }
}
