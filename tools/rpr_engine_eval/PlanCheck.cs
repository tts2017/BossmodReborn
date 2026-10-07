using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;

namespace RprEngineEval;

public static class PlanCheck
{
    public static int Run()
    {
        var job = RprDefinition.Build(2.48f);
        var engine = new RotationEngine(job, RprDefinition.DefaultWeights());
        var an = engine.Analysis;
        for (var g = 0; g < job.Gauges.Length; ++g)
            Console.WriteLine($"gauge {job.Gauges[g].Name}: unit {an.GaugeUnit[g]} unitValue {an.GaugeUnitValue[g]:f1} gain/s {an.GaugeGainPerSecond[g]:f2} byGcd {an.GaugeSpentByGcd[g]}");
        for (var c = 0; c < job.Cooldowns.Length; ++c)
            Console.WriteLine($"cd {job.Cooldowns[c].Name}: unitValue {an.CdUnitValue[c]:f1} dur {an.CdValueDuration[c]}");
        Console.WriteLine($"fillerPps {an.FillerPps:f1} perGcd {an.FillerPerGcd:f1}");
        var s = EngineState.Create(job);
        s.Gauges[job.GaugeIndex("Lemure")] = 5;
        var tl = EngineTimeline.Open(); tl.FightEndIn = 400; tl.AddBuff(30, 50, 1.05f); tl.Version = 1;
        engine.Planner.Plan(s, tl, engine.Weights);
        var r = UpperPlanner.GaugeResource(job.GaugeIndex("Lemure"));
        foreach (var t in new[] { 0f, 5f, 10f })
            Console.WriteLine($"t={t}: " + string.Join(" ", Enumerable.Range(0, 6).Select(k => $"k{k}={engine.Planner.LeafLambda(r, t, k):f1}")));
        return 0;
    }
}
