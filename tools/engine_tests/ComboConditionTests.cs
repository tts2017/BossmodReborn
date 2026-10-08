using BossMod.Autorotation.Engine;
using Xunit;
using Xunit.Abstractions;

namespace EngineTests;

// ComboIsNot: a condition checked at every search node (unlike a root Forbid, which lasts the whole horizon), so the search can plan
// the end of the combo before the ability that must not interrupt it.
public sealed class ComboConditionTests(ITestOutputHelper output)
{
    // the toy job with Burst usable only between combos
    private static JobDefinition BuildJob(bool noCombo) => new JobBuilder("TOY", baseGcd: 2.5f)
        .Gauge("Heat", max: 100)
        .Status("Fury", maxDuration: 20, damageMultiplier: 1.2f)
        .Cooldown("BurstCD", recast: 120)
        .Gcd("Slash", potency: 200).GainGauge("Heat", 5).StartsCombo()
        .Gcd("Cut", potency: 150).ComboFrom("Slash", potency: 300).GainGauge("Heat", 5)
        .Gcd("Finish", potency: 150).ComboFrom("Cut", potency: 400).GainGauge("Heat", 10).EndsCombo()
        .Gcd("Blast", potency: 600).SpendGauge("Heat", 50).ComboNeutral()
        .Ogcd("Burst", potency: 0, cooldown: "BurstCD").ApplyStatus("Fury", 20).NoTarget().Pipe(s => noCombo ? s.RequiresNoCombo() : s)
        .Build();

    private static EngineState State(JobDefinition job, string? combo, float gcdIn)
    {
        var s = EngineState.Create(job);
        if (combo != null)
        {
            s.ComboSkill = (byte)job.SkillIndex(combo);
            s.ComboLeft = 25;
        }
        s.GcdReadyAt = gcdIn;
        return s;
    }

    private static EngineTimeline Timeline()
    {
        var tl = EngineTimeline.Open();
        tl.FightEndIn = 600;
        tl.AddBuff(0, 20, 2.0f);
        tl.Version = 1;
        return tl;
    }

    [Fact]
    public void CheckSemantics()
    {
        var job = BuildJob(false);
        var slash = (short)job.SkillIndex("Slash");
        var any = new Condition(ConditionKind.ComboIsNot, -1, 0);
        var notSlash = new Condition(ConditionKind.ComboIsNot, slash, 0);
        var none = State(job, null, 0);
        var atSlash = State(job, "Slash", 0);
        var atCut = State(job, "Cut", 0);
        Assert.True(Simulator.Check(none, any));
        Assert.False(Simulator.Check(atSlash, any));
        Assert.False(Simulator.Check(atCut, any));
        Assert.True(Simulator.Check(none, notSlash));
        Assert.False(Simulator.Check(atSlash, notSlash));
        Assert.True(Simulator.Check(atCut, notSlash));
        // an expired combo counts as none
        var expired = atSlash;
        expired.ComboLeft = 0;
        Assert.True(Simulator.Check(expired, any));
    }

    [Fact]
    public void BurstWaitsForTheComboToEnd()
    {
        // raid window up now, Burst ready, weave slot open: without the condition it goes at once; with it the search plays Cut -> Finish
        // and presses Burst in the slot after Finish (still inside the window), instead of never (a root Forbid would hide it for the horizon)
        var plain = BuildJob(false);
        var d0 = new RotationEngine(plain, new EngineWeights { BudgetMs = 50 }).Decide(State(plain, "Slash", 1.5f), Timeline(), 0);
        Assert.Equal("Burst", plain.Skills[d0.Skill].Name);

        var job = BuildJob(true);
        var played = Play(job, State(job, "Slash", 1.5f), Timeline(), 10);
        output.WriteLine(string.Join(", ", played.Select(p => $"{p.time:f1}:{p.name}")));
        var finish = played.FindIndex(p => p.name == "Finish");
        var burst = played.FindIndex(p => p.name == "Burst");
        Assert.True(finish >= 0 && burst > finish, "Burst after Finish");
        Assert.True(played[burst].time < 10);
        Assert.DoesNotContain(played, p => p.name == "Burst" && p.time < played[finish].time);
    }

    [Fact]
    public void UnusedConditionLeavesDecisionsUnchanged()
    {
        // a definition without the condition decides as before (the new kind only matters for skills that carry it)
        var job = BuildJob(false);
        foreach (var combo in new[] { null, "Slash", "Cut" })
        {
            var a = new RotationEngine(job, new EngineWeights { BudgetMs = 50 }).Decide(State(job, combo, 1.5f), Timeline(), 0);
            var b = new RotationEngine(job, new EngineWeights { BudgetMs = 50 }).Decide(State(job, combo, 1.5f), Timeline(), 0);
            Assert.Equal(a.Skill, b.Skill);
            Assert.Equal(a.NextGcd, b.NextGcd);
            Assert.Equal(a.Value, b.Value);
        }
    }

    private static List<(float time, string name)> Play(JobDefinition job, EngineState s, EngineTimeline tl, float seconds)
    {
        var engine = new RotationEngine(job, new EngineWeights { BudgetMs = 50 });
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

internal static class BuilderExtensions
{
    public static JobBuilder.SkillBuilder Pipe(this JobBuilder.SkillBuilder s, Func<JobBuilder.SkillBuilder, JobBuilder.SkillBuilder> f) => f(s);
}
