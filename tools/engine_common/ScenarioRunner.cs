using System.Diagnostics;
using System.Text.Json;
using BossMod.Autorotation.Engine;

namespace EngineTools;

// Generic scenario, same granularity as the job harnesses' ScenarioDefinition (kill time, GCD, events, initial resources).
public sealed record EngineScenario
{
    public string Name { get; init; } = "";
    public float KillTime { get; init; } = 300;
    public int Targets { get; init; } = 1;
    public float FirstRaidBuff { get; init; } = 7.8f;   // party buffs first at 7.8 s, then every RaidBuffInterval
    public float RaidBuffInterval { get; init; } = 120;
    public float RaidBuffDuration { get; init; } = 20;
    public float RaidBuffMultiplier { get; init; } = 1.15f;
    public List<float[]> Downtime { get; init; } = []; // [start, end]
    public Dictionary<string, int> InitialGauges { get; init; } = [];

    public static List<EngineScenario> LoadAll(string path) => JsonSerializer.Deserialize<List<EngineScenario>>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
}

public readonly record struct ScenarioResult(string Name, float Damage, float Dps, int Decisions, double MeanMicros, double P99Micros);

// Plays a whole scenario with the engine against the engine's own mechanics model (Simulator).
// Note: tuning against the same model the engine plans with is circular; real tuning plugs a job harness emulator in here (Step 3).
public static class ScenarioRunner
{
    public static ScenarioResult Run(JobDefinition job, EngineWeights weights, EngineScenario sc, List<double>? decisionMicros = null)
    {
        var engine = new RotationEngine(job, weights);
        var ctx = new EvalContext(); // scoring: plain potency, no penalties
        var s = EngineState.Create(job);
        s.Targets = (byte)sc.Targets;
        foreach (var (name, value) in sc.InitialGauges)
            s.Gauges[job.GaugeIndex(name)] = (short)value;

        var clock = 0f;
        var damage = 0f;
        var decisions = 0;
        var sw = new Stopwatch();
        var times = decisionMicros ?? [];
        var guard = 0;
        while (clock < sc.KillTime && ++guard < 100000)
        {
            var tl = BuildTimeline(sc, clock);
            var local = s;
            local.Time = 0;
            local.GcdReadyAt -= clock;
            local.AnimLockAt -= clock;
            sw.Restart();
            var d = engine.Decide(local, tl, clock);
            sw.Stop();
            if (!d.Reused)
                times.Add(sw.Elapsed.TotalMilliseconds * 1000);
            ++decisions;

            var next = clock + MathF.Max(0.1f, d.ExecuteAt);
            if (d.Skill == EngineDecision.Wait)
            {
                AdvanceAbsolute(job, ref s, next, ctx);
                clock = next;
                continue;
            }
            var skill = job.Skills[d.Skill];
            AdvanceAbsolute(job, ref s, clock + d.ExecuteAt, ctx);
            clock += d.ExecuteAt;
            var abs = BuildTimeline(sc, 0); // absolute-time timeline for scoring
            if (!Simulator.IsLegal(job, s, abs, skill))
            {
                AdvanceAbsolute(job, ref s, clock + 0.1f, ctx);
                clock += 0.1f;
                continue;
            }
            damage += Simulator.Execute(job, ref s, abs, skill, ctx);
        }
        var (mean, p99) = Stats(times);
        return new(sc.Name, damage, damage / sc.KillTime, decisions, mean, p99);
    }

    private static void AdvanceAbsolute(JobDefinition job, ref EngineState s, float t, EvalContext ctx) => Simulator.Advance(job, ref s, t - s.Time, ctx);

    // timeline relative to `now`
    public static EngineTimeline BuildTimeline(EngineScenario sc, float now)
    {
        var tl = EngineTimeline.Open();
        tl.FightEndIn = sc.KillTime - now;
        for (var t = sc.FirstRaidBuff; t < sc.KillTime && tl.NumBuffs < EngineLimits.MaxWindows; t += sc.RaidBuffInterval)
            if (t + sc.RaidBuffDuration > now)
                tl.AddBuff(t - now, t + sc.RaidBuffDuration - now, sc.RaidBuffMultiplier);
        foreach (var w in sc.Downtime)
            if (w[1] > now)
                tl.AddDowntime(w[0] - now, w[1] - now);
        tl.Version = tl.NumBuffs * 31 + tl.NumDowntime;
        return tl;
    }

    public static (double mean, double p99) Stats(List<double> xs)
    {
        if (xs.Count == 0)
            return (0, 0);
        var sorted = xs.OrderBy(x => x).ToList();
        return (xs.Average(), sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.99))]);
    }
}
