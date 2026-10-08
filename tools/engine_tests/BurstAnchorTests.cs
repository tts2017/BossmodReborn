using BossMod.Autorotation.Engine;
using Xunit;
using Xunit.Abstractions;

namespace EngineTests;

// SkillDef.AnchorMultiplier / AnchorDuration (JobBuilder.BurstAnchor): a 2-minute cooldown without a damage status of its own is
// projected by RotationEngine.PlanTimeline as a burst window from its cooldown's return, so the upper tier holds the gauges for it.
public sealed class BurstAnchorTests(ITestOutputHelper output)
{
    private static JobDefinition BuildJob(bool anchor)
    {
        var b = new JobBuilder("TOY", baseGcd: 2.5f)
            .Gauge("Heat", max: 100)
            .Cooldown("AnchorCD", recast: 120)
            .Gcd("Slash", potency: 200).ComboNeutral().GainGauge("Heat", 10)
            .Gcd("Burst", potency: 600).ComboNeutral().SpendGauge("Heat", 50)
            .Ogcd("Anchor", potency: 0, cooldown: "AnchorCD").NoTarget();
        if (anchor)
            b.BurstAnchor(1.5f, 20);
        return b.Build();
    }

    private static EngineTimeline Timeline()
    {
        var tl = EngineTimeline.Open();
        tl.FightEndIn = 600;
        tl.Version = 1;
        return tl;
    }

    private static string Describe(in EngineTimeline tl)
    {
        var parts = new List<string>();
        for (var i = 0; i < tl.NumBuffs; ++i)
            parts.Add($"[{tl.Buffs[i].Start:f0}-{tl.Buffs[i].End:f0} x{tl.Buffs[i].Multiplier:f2}]");
        return string.Join(" ", parts);
    }

    [Fact]
    public void AnchorWindowsAreProjectedFromTheCooldownReturn()
    {
        var job = BuildJob(anchor: true);
        Assert.True(job.HasBurstAnchors);
        var engine = new RotationEngine(job, new EngineWeights { BudgetMs = 50 }); // ForecastSelfBuffs 0: the anchor is projected anyway
        var cd = job.CooldownIndex("AnchorCD");

        // up: a window now, then every recast
        var up = EngineState.Create(job);
        var tl = engine.PlannedTimeline(up, Timeline());
        output.WriteLine("up: " + Describe(tl));
        Assert.Equal(2, tl.NumBuffs);
        Assert.Equal(0, tl.Buffs[0].Start);
        Assert.Equal(20, tl.Buffs[0].End);
        Assert.Equal(1.5f, tl.Buffs[0].Multiplier);
        Assert.Equal(120, tl.Buffs[1].Start);

        // 40 s away: the first window at 40
        var away = EngineState.Create(job);
        away.Charges[cd] = 0;
        away.CdReadyIn[cd] = 40;
        tl = engine.PlannedTimeline(away, Timeline());
        output.WriteLine("40 s away: " + Describe(tl));
        Assert.Equal(2, tl.NumBuffs);
        Assert.Equal(40, tl.Buffs[0].Start);
        Assert.Equal(60, tl.Buffs[0].End);
        Assert.Equal(160, tl.Buffs[1].Start);

        // used 5 s ago: the window is still open for 15 s, the next one at the return
        var used = EngineState.Create(job);
        used.Charges[cd] = 0;
        used.CdReadyIn[cd] = 115;
        tl = engine.PlannedTimeline(used, Timeline());
        output.WriteLine("used 5 s ago: " + Describe(tl));
        Assert.Equal(2, tl.NumBuffs);
        Assert.Equal(0, tl.Buffs[0].Start);
        Assert.Equal(15, tl.Buffs[0].End);
        Assert.Equal(115, tl.Buffs[1].Start);

        // forbidden (a strategy track): not projected
        var disabled = away;
        disabled.DisabledSkills = 1UL << job.SkillIndex("Anchor");
        tl = engine.PlannedTimeline(disabled, Timeline());
        Assert.Equal(0, tl.NumBuffs);
    }

    [Fact]
    public void JobsWithoutAnchorsAreUnchanged()
    {
        var job = BuildJob(anchor: false);
        Assert.False(job.HasBurstAnchors);
        var engine = new RotationEngine(job, new EngineWeights { BudgetMs = 50 });
        var s = EngineState.Create(job);
        s.Charges[job.CooldownIndex("AnchorCD")] = 0;
        s.CdReadyIn[job.CooldownIndex("AnchorCD")] = 40;
        var tl = engine.PlannedTimeline(s, Timeline());
        Assert.Equal(0, tl.NumBuffs);
    }

    [Fact]
    public void GaugeIsHeldForAnAnchorCloseAhead()
    {
        // 50 Heat with the anchor 6 s away: the search's leaf lands inside the projected window, where the upper tier's shadow price of
        // the Heat held is 1.5 x its plain value, so the spender waits (the gauge banks without overcapping); with the anchor 90 s away
        // the price is the plain value and the spender goes at once
        var job = BuildJob(anchor: true);
        var cd = job.CooldownIndex("AnchorCD");
        var s = EngineState.Create(job);
        s.Gauges[job.GaugeIndex("Heat")] = 50;
        s.Charges[cd] = 0;
        s.CdReadyIn[cd] = 6;
        var near = new RotationEngine(job, new EngineWeights { BudgetMs = 50, SwitchMargin = 0 }).Decide(s, Timeline(), 0);
        output.WriteLine($"near: {(near.NextGcd >= 0 ? job.Skills[near.NextGcd].Name : "wait")}");
        Assert.Equal("Slash", job.Skills[near.NextGcd].Name);

        s.CdReadyIn[cd] = 90;
        var far = new RotationEngine(job, new EngineWeights { BudgetMs = 50, SwitchMargin = 0 }).Decide(s, Timeline(), 0);
        output.WriteLine($"far: {(far.NextGcd >= 0 ? job.Skills[far.NextGcd].Name : "wait")}");
        Assert.Equal("Burst", job.Skills[far.NextGcd].Name);
    }
}
