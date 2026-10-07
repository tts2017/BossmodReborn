using BossMod.Autorotation.Engine;
using Xunit;

namespace EngineTests;

// The generic mechanics added for casters: effect conditions on the pre-skill state, gauge set / scale, DoTs, periodic gauges,
// instant-cast consumption, falloff AoE and the cycle model.
public sealed class CasterMechanicsTests
{
    private static readonly EvalContext Ctx = new();

    private static JobDefinition Job() => new JobBuilder("CASTER", 2.5f)
        .Gauge("Mana", 10, flat: true).Gauge("Fire", 3, flat: true).Gauge("Ice", 3, flat: true).Gauge("Stacks", 3)
        .Status("Swift", 10, castTimeMultiplier: 0, consumedByCast: true)
        .Status("Triple", 15, maxStacks: 3, castTimeMultiplier: 0, consumedByCast: true)
        .Status("Dot", 30)
        .Status("Timer", 30)
        .Periodic("Timer", "Stacks", 1)
        .Cooldown("SwapCD", 5)
        .CycleGauge("Mana", 1).CycleGauge("Fire", 1).CycleGauge("Ice", 1).CycleCooldown("SwapCD")
        .Gcd("Nuke", 300).Cast(2).RequiresGauge("Fire", 1).SpendGauge("Mana", 2)
        .Gcd("Refill", 100).Cast(2).RequiresGauge("Ice", 1).SetGauge("Mana", 10)
        .Gcd("Burn", 200).AoeFalloff(100).RequiresGauge("Fire", 1).ScaleGauge("Mana", 0.5f)
        .Gcd("Zap", 50).Dot("Dot", 30, 30)
        .Gcd("Ignite", 100).Cast(2).RequiresGaugeAtMost("Fire", 0).RequiresGaugeAtMost("Ice", 0).SetGauge("Fire", 1)
        .Ogcd("Swap", 0, "SwapCD")
            .IfGaugeAtLeast("Fire", 1).SetGauge("Ice", 1).IfGaugeAtLeast("Fire", 1).SetGauge("Fire", 0)
            .IfGaugeAtLeast("Ice", 1).SetGauge("Fire", 1).IfGaugeAtLeast("Ice", 1).SetGauge("Ice", 0)
        .Ogcd("Swiftcast", 0).ApplyStatus("Swift", 10)
        .Ogcd("Triplecast", 0).ApplyStatus("Triple", 15, 3)
        .Build();

    [Fact]
    public void EffectConditionsSeeThePreSkillState()
    {
        var job = Job();
        var s = EngineState.Create(job);
        s.Gauges[job.GaugeIndex("Fire")] = 1;
        Simulator.Execute(job, ref s, EngineTimeline.Open(), job.Skills[job.SkillIndex("Swap")], Ctx);
        // "if fire: go to ice" and "if ice: go to fire" must not chain into fire again
        Assert.Equal(0, s.Gauges[job.GaugeIndex("Fire")]);
        Assert.Equal(1, s.Gauges[job.GaugeIndex("Ice")]);
    }

    [Fact]
    public void GaugeSetAndScale()
    {
        var job = Job();
        var s = EngineState.Create(job);
        s.Gauges[job.GaugeIndex("Ice")] = 1;
        Simulator.Execute(job, ref s, EngineTimeline.Open(), job.Skills[job.SkillIndex("Refill")], Ctx);
        Assert.Equal(10, s.Gauges[job.GaugeIndex("Mana")]);
        s.Gauges[job.GaugeIndex("Fire")] = 1;
        Simulator.Execute(job, ref s, EngineTimeline.Open(), job.Skills[job.SkillIndex("Burn")], Ctx);
        Assert.Equal(5, s.Gauges[job.GaugeIndex("Mana")]);
    }

    [Fact]
    public void FalloffAoeAddsReducedPotencyPerExtraTarget()
    {
        var job = Job();
        var s = EngineState.Create(job);
        s.Targets = 3;
        Assert.Equal(400, Simulator.Potency(job, s, job.Skills[job.SkillIndex("Burn")]));
    }

    [Fact]
    public void DotValueCountsOnlyTheSecondsGained()
    {
        var job = Job();
        var tl = EngineTimeline.Open();
        var s = EngineState.Create(job);
        var zap = job.Skills[job.SkillIndex("Zap")];
        Assert.Equal(50 + 300, Simulator.Execute(job, ref s, tl, zap, Ctx), 3); // 30 s x 10 potency/s
        s.StatusLeft[job.StatusIndex("Dot")] = 20;
        Assert.Equal(50 + 100, Simulator.Execute(job, ref s, tl, zap, Ctx), 3); // refresh with 20 s left gains 10 s
        tl.FightEndIn = 5;
        s.StatusLeft[job.StatusIndex("Dot")] = 0;
        Assert.Equal(50 + 50, Simulator.Execute(job, ref s, tl, zap, Ctx), 3); // the fight ends after 5 s
    }

    [Fact]
    public void PeriodicStatusGrantsGaugeAndRestarts()
    {
        var job = Job();
        var s = EngineState.Create(job);
        s.StatusLeft[job.StatusIndex("Timer")] = 1;
        Simulator.Advance(job, ref s, 62, Ctx);
        Assert.Equal(3, s.Gauges[job.GaugeIndex("Stacks")]); // expiries at 1, 31, 61
        Assert.Equal(29, s.StatusLeft[job.StatusIndex("Timer")], 3);
    }

    [Fact]
    public void InstantCastUsesSwiftcastBeforeTriplecast()
    {
        var job = Job();
        var tl = EngineTimeline.Open();
        var s = EngineState.Create(job);
        s.Gauges[job.GaugeIndex("Fire")] = 1;
        s.Gauges[job.GaugeIndex("Mana")] = 10;
        Simulator.Execute(job, ref s, tl, job.Skills[job.SkillIndex("Swiftcast")], Ctx);
        Simulator.Execute(job, ref s, tl, job.Skills[job.SkillIndex("Triplecast")], Ctx);
        s.AnimLockAt = s.GcdReadyAt = 0;
        Simulator.Execute(job, ref s, tl, job.Skills[job.SkillIndex("Nuke")], Ctx);
        Assert.False(s.HasStatus(job.StatusIndex("Swift")));
        Assert.Equal(3, s.StatusStacks[job.StatusIndex("Triple")]);
        Assert.True(s.AnimLockAt < 1); // it was instant
        s.AnimLockAt = s.GcdReadyAt = 0;
        Simulator.Execute(job, ref s, tl, job.Skills[job.SkillIndex("Nuke")], Ctx);
        Assert.Equal(2, s.StatusStacks[job.StatusIndex("Triple")]);
        // an instant skill (no cast time) does not use a stack
        s.AnimLockAt = s.GcdReadyAt = 0;
        Simulator.Execute(job, ref s, tl, job.Skills[job.SkillIndex("Zap")], Ctx);
        Assert.Equal(2, s.StatusStacks[job.StatusIndex("Triple")]);
    }

    [Fact]
    public void CycleModelFindsTheBestLoop()
    {
        // best loop: Nuke x5 (spending all mana), Swap to ice, Refill x2, Swap back to fire
        var job = Job();
        var model = new CycleModel(job, 1);
        Assert.True(model.States > 4);
        // 5 x 300 + 2 x 100 (the second Refill waits out the 5 s Swap cooldown) over 7 GCDs and two 0.1 s weaves
        Assert.True(model.Rate > 0, $"rate {model.Rate}");
        Assert.InRange(model.Rate, 1700 / 17.7f - 1, 1700 / 17.7f + 1);
        // with full mana in fire the future is worth more than with empty mana in fire
        var full = EngineState.Create(job);
        full.Gauges[job.GaugeIndex("Fire")] = 1;
        full.Gauges[job.GaugeIndex("Mana")] = 10;
        var empty = full;
        empty.Gauges[job.GaugeIndex("Mana")] = 0;
        Assert.True(model.Value(full) > model.Value(empty));
    }
}
