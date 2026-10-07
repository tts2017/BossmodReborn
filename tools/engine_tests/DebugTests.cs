using BossMod.Autorotation.Engine;
using EngineTools;
using Xunit;
using Xunit.Abstractions;

namespace EngineTests;

public sealed class DebugTests(ITestOutputHelper output)
{
    [Fact]
    public void DumpBurstRootValues()
    {
        var job = ToyJob.Build();
        foreach (var depth in new[] { 1, 2, 4 }) {
        var engine = new RotationEngine(job, new EngineWeights { BudgetMs = 50, HorizonGcds = depth });
        output.WriteLine($"--- depth {depth}");
        var s = EngineState.Create(job);
        s.ComboSkill = (byte)job.SkillIndex("Slash"); s.ComboLeft = 25; s.GcdReadyAt = 1.5f;
        s.Charges[job.CooldownIndex("StrikeCD")] = 0; s.CdReadyIn[job.CooldownIndex("StrikeCD")] = 25;
        var tl = EngineTimeline.Open(); tl.FightEndIn = 600; tl.AddBuff(0, 20, 1.3f); tl.Version = 1;
        var d = engine.Decide(s, tl, 0);
        var an = engine.Analysis;
        output.WriteLine($"fillerPps={an.FillerPps:f1} perGcd={an.FillerPerGcd:f1} cdValue={string.Join(",", an.CdUnitValue)} dur={string.Join(",", an.CdValueDuration)}");
        foreach (var sk in job.Skills)
            output.WriteLine($"{sk.Name}: {engine.LastRootValue(sk.Index):f1}");
        output.WriteLine($"lambda burst at t=0 holding 1: {engine.Planner.LeafLambda(UpperPlanner.CdResource(0), 0, 1):f1}; at t=10: {engine.Planner.LeafLambda(UpperPlanner.CdResource(0), 10, 1):f1}");
        }
    }
}
