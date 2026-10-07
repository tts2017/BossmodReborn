using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BossMod;
using EncounterTimeline;

namespace XanTimelineHarness;

// oracle-search --oracle-mode burst: the burst-pinned oracle (see report.md of the "burst on recast, never delayed" study).
//
// Fights are the combat-matrix scenarios that last long enough for a second recast of the anchor starter, carrying the FULL seeded irregular schedule (several random
// episodes, Irregular.Kinds all). Three policies are measured on every fight:
//   b  the module as it is                                   (BurstControl policy default)
//   p  the module with every starter released on recast       (policy inject, opener left to the module)  - the pinned module
//   c  p plus the genome the search found                      (everything except the starters is open to the search)
// The genome indexes the decisions per window (SearchControl burst mode: the windows around the anchor starter's recast, and the episodes' during/post/pre phases),
// so a gene means the same thing in fights whose episodes last differently. Knowledge levels:
//   K0  the genome is fitted on the actual fight (hindsight about the lengths of the episodes)
//   K1  the genome is fitted on the MEAN of several fights that share the schedule but give every episode another length (stratified quantiles of the length
//       distribution of its kind, all episodes at the same quantile); the actual fight is a held-out test. The episode STARTS are known (pre-episode genes).
//   K2  as K1 and the starts are noticed 0.5-1.0 s late (no pre-episode genes, the first decisions of an episode stay with the module)
// Controls: the same search on the fight WITHOUT episodes (only burst-window genes exist there) measures what hindsight finds in the burst windows by luck, and
// both genomes are replayed in fights 3.1 s and 8.7 s longer (the end of the fight shifts alignment luck).
internal static partial class Program
{
    private static OracleEval BurstEvaluate(JobAdapter job, TimelineMatrixScenario scenario, OracleConfig cfg, int seed, List<IrregularDriver.Episode>? schedule, bool seeded, bool pinned, bool control, int[] genome, bool detail)
    {
        ResetGnbLearnedWindows();
        ClientReject.Reset();
        ClientReject.Scenario = scenario.Name;
        Irregular.EnforceBaseline = true;
        Irregular.SingleEpisode = false;
        Irregular.ReplayTemplate = null;
        Irregular.ReplayList = schedule;
        Irregular.Seed = seeded || schedule != null ? seed : null;
        Irregular.RatePerMinute = cfg.Rate;
        Irregular.Kinds = cfg.Kinds.Aggregate((x, y) => x | y);
        BurstControl.Policy = pinned ? BurstControl.PolicyKind.Inject : BurstControl.PolicyKind.Default;
        SearchControl.Active = control;
        SearchControl.Genome = genome;
        SearchControl.Detail = detail;
        try
        {
            var result = new ContinuousTimelineRunner(job, scenario.ZoneID, scenario.Duration, scenario.TargetUnavailableWindows, null, []).Run();
            var eval = new OracleEval
            {
                Ok = true,
                Total = cfg.Rdps ? result.Metrics.RdpsTotal : result.Metrics.Total,
                Potency = result.Metrics.Total,
                Hash = ContinuousTimelineRunner.LastActionHash,
                Failures = result.Failures.Count
            };
            if (control)
            {
                eval.Clipped = SearchControl.GenesClipped;
                eval.Holds = SearchControl.HoldsApplied;
                eval.Alts = SearchControl.AlternativesApplied;
                eval.Decisions = SearchControl.Decisions.Select(d => new OracleDecisionInfo(d.Index, d.Time, d.Picked, d.Applied, d.Gene, d.Alternatives, d.GcdLeft, d.Names, d.State, d.Phase, d.PhaseTime)).ToArray();
            }
            ++OracleRunCount;
            return eval;
        }
        catch (Exception ex)
        {
            Irregular.Current = null;
            BurstControl.Current = null;
            Console.WriteLine($"oracle_exception scenario={scenario.Name} {ex.GetType().Name}: {ex.Message.Split(Environment.NewLine)[0]}");
            return new OracleEval { Ok = false, Total = double.NegativeInfinity };
        }
        finally
        {
            SearchControl.Active = false;
            Irregular.ReplayList = null;
            BurstControl.Policy = BurstControl.PolicyKind.Default;
        }
    }

    // the schedule with every episode at the j-th of L stratified quantiles of the length distribution of its kind, cut off where it would run into the next episode
    // or the next target-unavailable window, as the seeded schedule itself never lets it
    private static List<IrregularDriver.Episode> BurstVariantSchedule(IReadOnlyList<IrregularDriver.Episode> actual, int j, int count, IReadOnlyList<EventTriggerTimelineWindow> windows)
    {
        var list = new List<IrregularDriver.Episode>();
        for (var i = 0; i < actual.Count; ++i)
        {
            var e = actual[i];
            var (lo, hi) = e.Kind switch
            {
                IrregularKind.Lockout => (0.5f, 8f),
                IrregularKind.Range => (1f, 15f),
                _ => (1f, 10f)
            };
            var extra = e.Kind == IrregularKind.Range ? 3f : 0f;
            var nextStart = i + 1 < actual.Count ? actual[i + 1].Start : float.MaxValue;
            var nextWindow = windows.Where(w => w.Start > e.Start).Select(w => w.Start).DefaultIfEmpty(float.MaxValue).Min();
            var cap = MathF.Min(nextStart - 2.5f, nextWindow - 4f) - e.Start - extra;
            var hiEff = MathF.Max(lo + 0.01f, MathF.Min(hi, cap));
            var hold = lo + (hiEff - lo) * ((j + 0.5f) / count);
            list.Add(new IrregularDriver.Episode(e.Kind, e.Start, hold, e.Status, e.Far) { Nominal = e.Kind == IrregularKind.Range ? hold + 3f : hold, BlocksGCD = e.BlocksGCD });
        }
        return list;
    }

    private static OracleEval BurstCombine(List<OracleEval> evals, OracleConfig cfg)
    {
        var merged = OracleCombine(evals);
        if (cfg.Robust && merged.Ok && evals.Count > 1)
            merged.Total = 0.5 * merged.Total + 0.5 * evals.Min(e => e.Total);
        return merged;
    }

    private static int RunBurstOracle(HarnessOptions options, OracleConfig cfg, IReadOnlyList<JobAdapter> jobs, TimelineMatrixScenario[] scenarios)
    {
        var inv = CultureInfo.InvariantCulture;
        SearchControl.BurstMode = true;
        SearchControl.BurstKnow = cfg.Know == SearchControl.Knowledge.K2 ? 2 : 1;
        BurstControl.Metrics = true; // the tracker feeds the burst windows of the search
        BurstControl.CollectRows = false;
        BurstControl.CsvPath = null;
        BurstControl.ScenPath = null;
        var know = cfg.Know.ToString();
        Console.WriteLine(string.Create(inv, $"burst_oracle know={know}{(cfg.Robust ? "(robust)" : "")} lengths={(cfg.Know == SearchControl.Knowledge.K0 ? 1 : cfg.Lengths)} rate={cfg.Rate:f1} min_duration={cfg.MinDuration:f0} objective={(cfg.Rdps ? "rdps" : "total")} pre_burst={SearchControl.PreBurst:f0} post_burst={SearchControl.PostBurst:f0} post_episode={SearchControl.PostEpisode:f0} opener={(SearchControl.IncludeOpener ? 1 : 0)} budget={cfg.Budget} max_chain={cfg.MaxChainEvals}"));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cfg.Out + "_scen.csv"))!);
        using var scenCsv = new StreamWriter(cfg.Out + "_scen.csv", false);
        using var devCsv = new StreamWriter(cfg.Out + "_dev.csv", false);
        using var oppCsv = new StreamWriter(cfg.Out + "_opp.csv", false);
        scenCsv.WriteLine("job,seed,know,scenario,duration,n_eps,ep_seconds,anchor_uses,selftest,a_mod,a_pin,b,p,c,fit_gain,g_b_pre,g_b_ready,g_b_burst,g_post,g_during,g_pre_ep,genes,holds,alts,n_dec,evals,chain_evals,rhea_evals,c0,a0_ctrl,ctrl_evals,v1_b,v1_p,v1_c,v1_a_pin,v1_c0,v2_b,v2_p,v2_c,v2_a_pin,v2_c0,seconds,b_fail,p_fail,c_fail");
        devCsv.WriteLine("job,seed,know,scenario,fight,idx,t,phase,phase_time,gene,module,applied,alternatives,state,gcd_left,prev,next,marginal,chain_gain");
        oppCsv.WriteLine("job,seed,know,scenario,module,phase,count");

        foreach (var job in jobs)
        {
            // BLM runs its boundary self-tests inside the first BLM run of a process; burn one so no measured run carries them
            if (job.Class == Class.BLM && scenarios.Length > 0)
                BurstEvaluate(job, scenarios[0], cfg, cfg.Seeds[0], null, false, false, false, [], false);
            var eligible = scenarios.Where(s => s.Duration >= cfg.MinDuration).ToArray();
            var ordered = eligible.Select(s => (Scenario: s, Hash: OracleHash(s.Name + "|burst|" + cfg.Seeds[0], 77u))).OrderBy(x => x.Hash).Select(x => x.Scenario).ToArray();
            var perShard = cfg.Sample > 0 ? (cfg.Sample + cfg.ShardCount - 1) / cfg.ShardCount : int.MaxValue;
            var processed = 0;
            var skipped = 0;
            var started = System.Diagnostics.Stopwatch.StartNew();
            double sumB = 0, sumP = 0, sumC = 0;
            for (var i = 0; i < ordered.Length && processed < perShard; ++i)
            {
                if (i % cfg.ShardCount != cfg.ShardIndex)
                    continue;
                var seed = cfg.Seeds[(i / cfg.ShardCount) % cfg.Seeds.Length];
                var row = BurstScenario(job, ordered[i], cfg, seed, know, scenCsv, devCsv, oppCsv);
                if (row == null)
                {
                    ++skipped;
                    continue;
                }
                ++processed;
                sumB += row.Value.B;
                sumP += row.Value.A;
                sumC += row.Value.C;
                if (processed % 5 == 0)
                {
                    scenCsv.Flush();
                    devCsv.Flush();
                    oppCsv.Flush();
                    Console.WriteLine(string.Create(inv, $"burst_oracle_progress job={job.Name} know={know} done={processed} skipped={skipped} b={sumB:f0} p={sumP:f0} c={sumC:f0} c_over_b={(sumB > 0 ? sumC / sumB - 1 : 0):f5} c_over_p={(sumP > 0 ? sumC / sumP - 1 : 0):f5} seconds={started.Elapsed.TotalSeconds:f0}"));
                }
            }
            scenCsv.Flush();
            devCsv.Flush();
            oppCsv.Flush();
            Console.WriteLine(string.Create(inv, $"burst_oracle_result job={job.Name} know={know} scenarios={processed} skipped={skipped} b={sumB:f0} p={sumP:f0} c={sumC:f0} p_over_b={(sumB > 0 ? sumP / sumB - 1 : 0):f5} c_over_p={(sumP > 0 ? sumC / sumP - 1 : 0):f5} c_over_b={(sumB > 0 ? sumC / sumB - 1 : 0):f5} seconds={started.Elapsed.TotalSeconds:f0} runs={OracleRunCount}"));
        }
        Irregular.Seed = null;
        Irregular.EnforceBaseline = false;
        SearchControl.BurstMode = false;
        return 0;
    }

    // A = pinned module (p), B = module as it is (b), C = oracle (c) in the returned row
    private static OracleRow? BurstScenario(JobAdapter job, TimelineMatrixScenario scenario, OracleConfig cfg, int seed, string know, StreamWriter scenCsv, StreamWriter devCsv, StreamWriter oppCsv)
    {
        var inv = CultureInfo.InvariantCulture;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var scenName = scenario.Name.Replace(',', ';');
        var rngSeed = (int)(OracleHash(scenario.Name + "|burst|" + seed, 4242u) & 0x7FFFFFFF);
        SearchControl.ReactionDelay = cfg.Reaction >= 0 ? cfg.Reaction : 0.5f + 0.5f * ((OracleHash(scenario.Name + "|burst|" + seed, 313u) % 1000u) / 1000f);

        // the actual schedule
        var b = BurstEvaluate(job, scenario, cfg, seed, null, true, false, false, [], false);
        if (!b.Ok)
            return null;
        var actual = Irregular.LastSchedule.Select(e => new IrregularDriver.Episode(e.Kind, e.Start, e.Hold, e.Status, e.Far) { Nominal = e.Nominal, BlocksGCD = e.BlocksGCD }).ToList();
        if (actual.Count == 0)
            return null;
        var pPlain = BurstEvaluate(job, scenario, cfg, seed, actual, true, true, false, [], false);
        var anchorUses = BurstControl.LastAnchorUses;
        if (!pPlain.Ok || anchorUses < 2)
            return null;

        // self-tests: replaying the schedule reproduces the seeded fight; gene 0 reproduces the pinned module
        var bReplay = BurstEvaluate(job, scenario, cfg, seed, actual, true, false, false, [], false);
        var p0 = BurstEvaluate(job, scenario, cfg, seed, actual, true, true, true, [], false);
        var selfTest = bReplay.Ok && bReplay.Hash == b.Hash && bReplay.Total == b.Total && p0.Ok && p0.Hash == pPlain.Hash && p0.Total == pPlain.Total && p0.Decisions.All(d => d.Applied == d.Picked);
        if (!selfTest)
        {
            Console.WriteLine($"burst_selftest_fail job={job.Name} scenario={scenario.Name} replay_same={(bReplay.Ok && bReplay.Hash == b.Hash && bReplay.Total == b.Total)} gene0_same={(p0.Ok && p0.Hash == pPlain.Hash && p0.Total == pPlain.Total)}");
            return new OracleRow(pPlain.Total, b.Total, b.Total, false);
        }
        var aMod = BurstEvaluate(job, scenario, cfg, seed, null, false, false, false, [], false);
        var aPin = BurstEvaluate(job, scenario, cfg, seed, null, false, true, false, [], false);

        // how often the pinned module picks each action in each phase (the denominator of the deviation rates)
        foreach (var group in p0.Decisions.GroupBy(d => (Action: SearchControl.ActionName(d.Picked), Phase: d.Phase)))
            oppCsv.WriteLine(string.Create(inv, $"{job.Name},{seed},{know},{scenName},{group.Key.Action},{group.Key.Phase},{group.Count()}"));

        // the fights the genome is fitted on
        var fights = new List<List<IrregularDriver.Episode>>();
        var variants = cfg.Know == SearchControl.Knowledge.K0 ? 1 : Math.Max(1, cfg.Lengths);
        if (cfg.Know == SearchControl.Knowledge.K0)
            fights.Add(actual);
        else
            for (var j = 0; j < variants; ++j)
                fights.Add(BurstVariantSchedule(actual, j, variants, scenario.TargetUnavailableWindows));
        var zero = new List<OracleEval>();
        foreach (var f in fights)
        {
            var plain = BurstEvaluate(job, scenario, cfg, seed, f, true, true, false, [], false);
            var z = BurstEvaluate(job, scenario, cfg, seed, f, true, true, true, [], false);
            if (!plain.Ok || !z.Ok || z.Hash != plain.Hash || z.Total != plain.Total)
            {
                Console.WriteLine($"burst_selftest_fail job={job.Name} scenario={scenario.Name} variant gene0");
                return new OracleRow(pPlain.Total, b.Total, b.Total, false);
            }
            zero.Add(z);
        }
        var start = BurstCombine(zero, cfg);
        var outcome = OracleOptimize(genome => BurstCombine(fights.Select(f => BurstEvaluate(job, scenario, cfg, seed, f, true, true, true, genome, false)).ToList(), cfg), start, cfg, rngSeed, sparse: true, timeOrder: true);
        var c = BurstEvaluate(job, scenario, cfg, seed, actual, true, true, true, outcome.Genome, true);

        // what each kind of window contributed: the genes of one phase set back to 0, on the actual fight
        double Gain(params string[] phases)
        {
            var genome = outcome.Genome.ToArray();
            foreach (var d in c.Decisions)
                if (d.Gene != 0 && phases.Contains(d.Phase) && d.Index < genome.Length)
                    genome[d.Index] = 0;
            return c.Total - BurstEvaluate(job, scenario, cfg, seed, actual, true, true, true, genome, false).Total;
        }
        var gBPre = Gain("b_pre");
        var gBReady = Gain("b_ready");
        var gBBurst = Gain("b_burst");
        var gPost = Gain("post");
        var gDuring = Gain("during");
        var gPreEp = Gain("pre_ep");

        // control: the same search on the fight without episodes (burst windows only)
        OracleOutcome control = new();
        var c0 = aPin;
        var a0 = BurstEvaluate(job, scenario, cfg, seed, null, false, true, true, [], false);
        var controlOk = a0.Ok && a0.Hash == aPin.Hash && a0.Total == aPin.Total;
        if (cfg.Control && controlOk)
        {
            control = OracleOptimize(genome => BurstEvaluate(job, scenario, cfg, seed, null, false, true, true, genome, false), a0, cfg, rngSeed ^ 0x2545F491, sparse: true, timeOrder: true);
            c0 = BurstEvaluate(job, scenario, cfg, seed, null, false, true, true, control.Genome, true);
        }

        // end-shift validation (alignment luck at the end of the fight), against the same fights without the genome
        var validation = new double[10];
        var shifts = new[] { cfg.ValShift1, cfg.ValShift2 };
        for (var s = 0; s < 2; ++s)
        {
            if (shifts[s] <= 0)
                continue;
            var longer = scenario with { Duration = scenario.Duration + shifts[s] };
            validation[s * 5] = BurstEvaluate(job, longer, cfg, seed, actual, true, false, false, [], false).Total;
            validation[s * 5 + 1] = BurstEvaluate(job, longer, cfg, seed, actual, true, true, false, [], false).Total;
            validation[s * 5 + 2] = BurstEvaluate(job, longer, cfg, seed, actual, true, true, true, outcome.Genome, false).Total;
            validation[s * 5 + 3] = BurstEvaluate(job, longer, cfg, seed, null, false, true, false, [], false).Total;
            validation[s * 5 + 4] = cfg.Control && controlOk ? BurstEvaluate(job, longer, cfg, seed, null, false, true, true, control.Genome, false).Total : validation[s * 5 + 3];
        }

        var holds = outcome.Genome.Count(g => g == SearchControl.Hold);
        var alts = outcome.Genome.Count(g => g > 0);
        var episodeSeconds = actual.Sum(e => e.End > 0 ? e.End - e.Start : e.Nominal);
        scenCsv.WriteLine(string.Create(inv, $"{job.Name},{seed},{know},{scenName},{scenario.Duration:f1},{actual.Count},{episodeSeconds:f1},{anchorUses},1,{aMod.Total:f1},{aPin.Total:f1},{b.Total:f1},{pPlain.Total:f1},{c.Total:f1},{outcome.Best.Total - start.Total:f1},{gBPre:f1},{gBReady:f1},{gBBurst:f1},{gPost:f1},{gDuring:f1},{gPreEp:f1},{holds + alts},{holds},{alts},{p0.Decisions.Length},{outcome.Evals},{outcome.ChainEvals},{outcome.RheaEvals},{c0.Total:f1},{a0.Total:f1},{(controlOk ? control.Evals : 0)},{string.Join(",", validation.Select(v => v.ToString("f1", inv)))},{timer.Elapsed.TotalSeconds:f1},{b.Failures},{pPlain.Failures},{c.Failures}"));

        void Deviations(string fight, OracleOutcome outc, OracleEval run)
        {
            for (var p = 0; p < run.Decisions.Length; ++p)
            {
                var d = run.Decisions[p];
                if (d.Gene == 0 || (d.Gene != SearchControl.Hold && d.Applied == d.Picked))
                    continue;
                var applied = d.Gene == SearchControl.Hold ? "HOLD" : SearchControl.ActionName(d.Applied);
                var prev = p > 0 ? SearchControl.ActionName(run.Decisions[p - 1].Applied) : "-";
                var next = p + 1 < run.Decisions.Length ? (run.Decisions[p + 1].Gene == SearchControl.Hold ? "HOLD" : SearchControl.ActionName(run.Decisions[p + 1].Applied)) : "-";
                devCsv.WriteLine(string.Create(inv, $"{job.Name},{seed},{know},{scenName},{fight},{d.Index},{d.Time:f2},{d.Phase},{d.PhaseTime:f2},{d.Gene},{SearchControl.ActionName(d.Picked)},{applied},{d.Names.Replace(',', ';')},{d.State},{d.GcdLeft:f2},{prev},{next},{(outc.Marginals.TryGetValue(d.Index, out var mg) ? mg : double.NaN):f1},{(outc.ChainGains.TryGetValue(d.Index, out var cg) ? cg : double.NaN):f1}"));
            }
        }
        Deviations("episodes", outcome, c);
        if (cfg.Control && controlOk)
            Deviations("control", control, c0);
        return new OracleRow(pPlain.Total, b.Total, c.Total, true);
    }
}