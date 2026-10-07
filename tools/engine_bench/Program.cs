using System.Diagnostics;
using System.Globalization;
using BossMod.Autorotation.Engine;
using EngineTools;

if (args.Length > 0 && args[0] == "prof") { MicroProfile.Run(); return; }

// Per-decision time (mean / p99) and heap allocations for the toy job, with the default search settings
// (depth 4, 0.5 ms budget). States are randomized so the result cache never short-circuits the search.
var job = ToyJob.Build();
var weights = ToyJob.DefaultWeights();
if (args.Length > 1)
    weights.HorizonGcds = int.Parse(args[1], CultureInfo.InvariantCulture);
if (args.Length > 2)
    weights.BudgetMs = float.Parse(args[2], CultureInfo.InvariantCulture);
var decisions = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 20000;
var engine = new RotationEngine(job, weights);
var rng = new Random(7);
var heat = job.GaugeIndex("Heat");
var burst = job.CooldownIndex("BurstCD");
var strike = job.CooldownIndex("StrikeCD");
var combos = new[] { EngineLimits.NoCombo, (byte)job.SkillIndex("Slash"), (byte)job.SkillIndex("Cut") };

var states = new EngineState[1024];
var timelines = new EngineTimeline[states.Length];
for (var i = 0; i < states.Length; ++i)
{
    var s = EngineState.Create(job);
    s.Gauges[heat] = (short)(rng.Next(0, 21) * 5);
    s.ComboSkill = combos[rng.Next(combos.Length)];
    s.ComboLeft = s.ComboSkill == EngineLimits.NoCombo ? 0 : 20;
    s.Charges[burst] = (byte)rng.Next(0, 2);
    s.CdReadyIn[burst] = s.Charges[burst] == 0 ? (float)rng.NextDouble() * 120 : 0;
    s.Charges[strike] = (byte)rng.Next(0, 3);
    s.CdReadyIn[strike] = s.Charges[strike] < 2 ? (float)rng.NextDouble() * 30 : 0;
    s.GcdReadyAt = (float)rng.NextDouble() * 2.5f;
    if (rng.Next(3) == 0)
    {
        s.StatusLeft[job.StatusIndex("Fury")] = (float)rng.NextDouble() * 20;
        s.StatusStacks[job.StatusIndex("Fury")] = 1;
    }
    states[i] = s;
    var tl = EngineTimeline.Open();
    tl.FightEndIn = 30 + (float)rng.NextDouble() * 400;
    var b = (float)rng.NextDouble() * 120 - 20;
    tl.AddBuff(b, b + 20, 1.15f);
    tl.AddBuff(b + 120, b + 140, 1.15f);
    if (rng.Next(4) == 0)
    {
        var d = 5 + (float)rng.NextDouble() * 60;
        tl.AddDowntime(d, d + 20);
    }
    tl.Version = i; // forces an upper-tier replan as well: worst case
    timelines[i] = tl;
}

// upper tier alone
{
    var planSw = Stopwatch.StartNew();
    for (var i = 0; i < 2000; ++i)
        engine.Planner.Plan(states[i % states.Length], timelines[i % states.Length], weights);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"upper tier replan: {planSw.Elapsed.TotalMilliseconds * 1000 / 2000:f1} us"));
}

// warm-up (JIT / tiering)
for (var i = 0; i < 3000; ++i)
    engine.Decide(states[i % states.Length], timelines[i % states.Length], i * 0.1f);

var times = new List<double>(decisions);
var depths = new int[8];
long nodes = 0;
var sw = new Stopwatch();
var allocBefore = GC.GetAllocatedBytesForCurrentThread();
for (var i = 0; i < decisions; ++i)
{
    var k = i % states.Length;
    var s = states[k];
    s.GcdReadyAt += (i / states.Length) * 0.001f; // never identical to an earlier decision: no reuse
    sw.Restart();
    engine.InvalidateCache();
    var d = engine.Decide(s, timelines[k], 10000 + i * 0.1f);
    sw.Stop();
    times.Add(sw.Elapsed.TotalMilliseconds * 1000);
    ++depths[Math.Min(d.Depth, 7)];
    nodes += d.Nodes;
}
var allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
var (mean, p99) = ScenarioRunner.Stats(times);
times.Sort();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"decisions: {decisions} (replan every decision, no result reuse)"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"time per decision: mean {mean:f1} us, p50 {times[times.Count / 2]:f1} us, p99 {p99:f1} us, max {times[^1]:f1} us"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"nodes per decision: {nodes / (double)decisions:f0}; completed depth histogram: {string.Join(" ", depths.Select((c, i) => $"d{i}={c}"))}"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"heap allocated: {allocated} bytes total, {allocated / (double)decisions:f1} bytes per decision"));

// typical cadence: the timeline is stable, so the upper tier only replans every ReplanInterval (2 s of game time)
{
    var t2 = new List<double>(decisions);
    long n2 = 0;
    for (var i = 0; i < decisions; ++i)
    {
        var k = i % states.Length;
        var s = states[k];
        s.GcdReadyAt += 0.5f + (i / states.Length) * 0.001f;
        var tl = timelines[k];
        tl.Version = -1;
        sw.Restart();
        engine.InvalidateCache();
        var d = engine.Decide(s, tl, 50000 + i * 0.1f);
        sw.Stop();
        t2.Add(sw.Elapsed.TotalMilliseconds * 1000);
        n2 += d.Nodes;
    }
    var (m2, q2) = ScenarioRunner.Stats(t2);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"stable timeline: mean {m2:f1} us, p99 {q2:f1} us, nodes {n2 / (double)decisions:f0}"));
}

// whole-fight run: realistic cadence including cache reuse between frames
var fightTimes = new List<double>();
var result = ScenarioRunner.Run(job, weights, new EngineScenario { Name = "toy-300s", KillTime = 300 }, fightTimes);
var (fm, fp) = ScenarioRunner.Stats(fightTimes);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"300 s scenario: {result.Decisions} decisions, {fightTimes.Count} searched; mean {fm:f1} us, p99 {fp:f1} us; dps {result.Dps:f1}"));
