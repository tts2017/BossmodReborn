using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BossMod;
using EncounterTimeline;

namespace XanTimelineHarness;

// oracle-search --oracle-know K1|K2: the oracle with less knowledge than K0 (see OracleSearch.cs for K0 and the shared search).
//
// K0 knows when the episode starts and ends and tunes one gene per decision of the whole window to that single fight.
// K1 does not know how long the episode will last. The genome is therefore indexed by phase (SearchControl: pre / during / post), so one gene means the
//    same thing in every fight, and its fitness is the MEAN total over several fights that share the episode (kind, start, status, distance) but
//    differ in length (quantiles of the length distribution of that kind). The optimum is the best policy for an unknown length. It still sees
//    the episode start coming (the pre genes) and, once the episode has ended, how long it lasted (post genes are counted from the end).
// K2 additionally does not see the start coming: no pre genes, and the first decisions of the episode (the reaction delay, 0.5 to 1.0 s drawn per
//    scenario) run as the module chose.
// Both are measured the way K0 is: against the plain module on the actual episode (H = c - b, where the actual length is NOT one of the lengths
// the genome was fitted on), against a control search on the fight without the episode with the same phase structure (H0), and by replaying in
// fights that are 3.1 s and 8.7 s longer (validation). The self-test is repeated per level: gene 0 on every fight reproduces the plain module.
internal static partial class Program
{
    private sealed class OracleFight
    {
        public IrregularDriver.Episode Template = null!;
        public float Extend;   // the fight is lengthened so the recovery after this episode is still observable
        public float Start, End;
        public float Hold;
    }

    private static OracleEval OracleCombine(List<OracleEval> evals)
    {
        var merged = new SortedDictionary<int, OracleDecisionInfo>();
        ulong hash = 14695981039346656037UL;
        double total = 0;
        foreach (var e in evals)
        {
            if (!e.Ok)
                return new OracleEval { Ok = false, Total = double.NegativeInfinity };
            total += e.Total;
            hash = (hash ^ e.Hash) * 1099511628211UL;
            foreach (var d in e.Decisions)
                merged[d.Index] = merged.TryGetValue(d.Index, out var old) ? old with { Alternatives = Math.Max(old.Alternatives, d.Alternatives) } : d;
        }
        return new OracleEval { Ok = true, Total = total / evals.Count, Hash = hash, Decisions = merged.Values.ToArray() };
    }

    private static OracleRow? OracleScenarioK(JobAdapter job, TimelineMatrixScenario scenario, OracleConfig cfg, int seed, IrregularKind kind, string kindName, StreamWriter scenCsv, StreamWriter devCsv, StreamWriter oppCsv)
    {
        var inv = CultureInfo.InvariantCulture;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var runs = 0;
        SearchControl.FixedWindow = null;
        SearchControl.FixedEpisode = null;

        // the actual episode of this scenario, exactly as K0 picks it
        ++runs;
        var b = OracleEvaluate(job, scenario, cfg, seed, kind, true, false, [], false);
        var episode = Irregular.LastSingle;
        if (!b.Ok || episode == null)
            return null;
        var half = (int)(OracleHash(scenario.Name + "|" + kindName + "|" + seed, 991u) & 1u);
        var rngSeed = (int)(OracleHash(scenario.Name + "|" + kindName + "|" + seed, 4242u) & 0x7FFFFFFF);
        SearchControl.ReactionDelay = cfg.Reaction >= 0 ? cfg.Reaction : 0.5f + 0.5f * ((OracleHash(scenario.Name + "|" + kindName + "|" + seed, 313u) % 1000u) / 1000f);
        var episodeStart = episode.Start;
        var episodeEnd = episode.End;
        var detail = episode.Kind switch
        {
            IrregularKind.Lockout => episode.Status.ToString(),
            IrregularKind.Range => string.Create(inv, $"far={episode.Far:f1}"),
            _ => ""
        };
        var actual = new OracleFight { Template = episode, Start = episodeStart, End = episodeEnd, Hold = episode.Hold };

        // the episode lengths the genome is fitted on: stratified quantiles of the length distribution of the kind (IrregularDriver), cut off where the
        // episode would run into the next target-unavailable window, as the schedule itself never lets it
        var (lo, hi) = kind switch
        {
            IrregularKind.Lockout => (0.5f, 8f),
            IrregularKind.Range => (1f, 15f),
            _ => (1f, 10f)
        };
        var nextWindow = scenario.TargetUnavailableWindows.Where(w => w.Start > episodeStart).Select(w => w.Start).DefaultIfEmpty(float.MaxValue).Min();
        var cap = nextWindow - 4f - episodeStart - (kind == IrregularKind.Range ? 3f : 0f);
        var hiEff = MathF.Max(lo + 0.01f, MathF.Min(hi, cap));
        var fights = new List<OracleFight>();
        for (var j = 0; j < cfg.Lengths; ++j)
        {
            var hold = lo + (hiEff - lo) * ((j + 0.5f) / cfg.Lengths);
            var nominal = kind == IrregularKind.Range ? hold + 3f : hold;
            var template = new IrregularDriver.Episode(kind, episodeStart, hold, episode.Status, episode.Far) { Nominal = nominal, BlocksGCD = episode.BlocksGCD };
            fights.Add(new OracleFight { Template = template, Start = episodeStart, Hold = hold, Extend = MathF.Max(0f, episodeStart + nominal + cfg.Recovery - scenario.Duration) });
        }

        OracleEval Run(OracleFight f, bool withEpisode, bool control, int[] genome, bool detailed = false, float extra = 0f)
        {
            ++runs;
            ++OracleRunCount;
            SearchControl.FixedEpisode = withEpisode ? null : (f.Start, f.End);
            var evaluated = OracleEvaluate(job, scenario with { Duration = scenario.Duration + f.Extend + extra }, cfg, seed, kind, withEpisode, control, genome, detailed, withEpisode ? f.Template : null);
            SearchControl.FixedEpisode = null;
            return evaluated;
        }

        // the end of each sampled episode is whatever the driver makes of it for this job; the plain run also gives the self-test reference
        var plainEpisode = new List<OracleEval>();
        var plainControl = new List<OracleEval>();
        foreach (var f in fights)
        {
            plainEpisode.Add(Run(f, true, false, []));
            f.End = Irregular.LastSingle?.End ?? f.Start + f.Hold;
        }
        foreach (var f in fights)
            plainControl.Add(Run(f, false, false, []));
        var actualReplay = Run(actual, true, false, []);
        var a = OracleEvaluate(job, scenario, cfg, seed, kind, false, false, [], false);
        ++runs;
        if (plainEpisode.Any(e => !e.Ok) || plainControl.Any(e => !e.Ok) || !actualReplay.Ok)
            return null;

        // self-test: gene 0 on every fight reproduces the plain module (total, action hash, every decision keeps the module pick)
        var selfTest = actualReplay.Hash == b.Hash && actualReplay.Total == b.Total;
        var zero = new List<OracleEval>();
        for (var j = 0; j < fights.Count && selfTest; ++j)
        {
            var e = Run(fights[j], true, true, []);
            zero.Add(e);
            selfTest = e.Ok && e.Hash == plainEpisode[j].Hash && e.Total == plainEpisode[j].Total && e.Decisions.All(d => d.Applied == d.Picked);
        }
        var zeroControl = new List<OracleEval>();
        for (var j = 0; j < fights.Count && selfTest; ++j)
        {
            var e = Run(fights[j], false, true, []);
            zeroControl.Add(e);
            selfTest = e.Ok && e.Hash == plainControl[j].Hash && e.Total == plainControl[j].Total && e.Decisions.All(d => d.Applied == d.Picked);
        }
        var actualZero = Run(actual, true, true, []);
        selfTest = selfTest && actualZero.Ok && actualZero.Hash == b.Hash && actualZero.Total == b.Total && actualZero.Decisions.All(d => d.Applied == d.Picked);
        var actualControlPlain = Run(actual, false, false, []);
        var actualControlZero = Run(actual, false, true, []);
        var controlSelf = selfTest && actualControlZero.Ok && actualControlZero.Hash == actualControlPlain.Hash && actualControlZero.Total == actualControlPlain.Total;
        if (!selfTest)
            return new OracleRow(a.Total, b.Total, b.Total, false);

        foreach (var group in actualZero.Decisions.GroupBy(d => (Action: SearchControl.ActionName(d.Picked), Phase: OraclePhase(d.Time, episodeStart, episodeEnd))))
            oppCsv.WriteLine(string.Create(inv, $"{job.Name},{kindName},{seed},{half},{scenario.Name.Replace(',', ';')},{group.Key.Action},{group.Key.Phase},{group.Count()}"));

        // (c) the episode fights: the genome is fitted on the mean over the sampled lengths
        var start = OracleCombine(zero);
        var outcome = OracleOptimize(genome => OracleCombine(fights.Select(f => Run(f, true, true, genome)).ToList()), start, cfg, rngSeed, sparse: true);
        var c = Run(actual, true, true, outcome.Genome, detailed: true);

        // control: the same search on the fights without the episode, same phases, same lengths
        OracleOutcome control = new();
        var c0 = actualControlPlain;
        var controlOk = false;
        if (cfg.Control && controlSelf)
        {
            var startControl = OracleCombine(zeroControl);
            control = OracleOptimize(genome => OracleCombine(fights.Select(f => Run(f, false, true, genome)).ToList()), startControl, cfg, rngSeed ^ 0x2545F491, sparse: true);
            c0 = Run(actual, false, true, control.Genome, detailed: true);
            controlOk = c0.Ok;
        }

        // end-shift validation on the actual episode, as in K0
        var validation = new double[8];
        if (cfg.ValShift1 > 0 || cfg.ValShift2 > 0)
        {
            var shifts = new[] { cfg.ValShift1, cfg.ValShift2 };
            for (var s = 0; s < 2; ++s)
            {
                if (shifts[s] <= 0)
                    continue;
                var bv = Run(actual, true, false, [], extra: shifts[s]);
                var cv = Run(actual, true, true, outcome.Genome, extra: shifts[s]);
                var av = Run(actual, false, false, [], extra: shifts[s]);
                var c0v = controlOk ? Run(actual, false, true, control.Genome, extra: shifts[s]) : av;
                validation[s * 4] = bv.Total;
                validation[s * 4 + 1] = cv.Total;
                validation[s * 4 + 2] = av.Total;
                validation[s * 4 + 3] = c0v.Total;
            }
        }

        var expB = plainEpisode.Average(e => e.Total);
        var expC = outcome.Best.Total;
        var expA = plainControl.Average(e => e.Total);
        var expC0 = controlOk ? control.Best.Total : expA;
        var c0Total = controlOk ? c0.Total : a.Total;
        var loss = a.Total - b.Total;
        var headroom = c.Total - b.Total;
        var holds = outcome.Genome.Count(g => g == SearchControl.Hold);
        var alts = outcome.Genome.Count(g => g > 0);
        var lens = string.Join(";", fights.Select(f => f.Hold.ToString("f1", inv)));
        scenCsv.WriteLine(string.Create(inv, $"{job.Name},{kindName},{seed},{half},{scenario.Name.Replace(',', ';')},{scenario.Duration:f1},{episodeStart:f2},{episodeEnd:f2},{episodeEnd - episodeStart:f2},{detail},{(episode.BlocksGCD ? 1 : 0)},{a.Total:f1},{b.Total:f1},{c.Total:f1},{loss:f1},{headroom:f1},{(loss > 1 ? headroom / loss : 0):f4},{(a.Total > 0 ? headroom / a.Total : 0):f5},{actualZero.Decisions.Length},{runs},{outcome.ChainEvals},{outcome.RheaEvals},{outcome.ChainTotal - expB:f1},{holds + alts},{holds},{alts},{c.Clipped},{timer.Elapsed.TotalSeconds:f2},1,{b.Failures},{c.Failures},{episode.Refusals},1,{c0Total:f1},{c0Total - a.Total:f1},{(controlOk ? 1 : 0)},{(controlOk ? control.Evals : 0)},{string.Join(",", validation.Select(v => v.ToString("f1", inv)))},{cfg.Know},{fights.Count},{lens},{expB:f1},{expC:f1},{expA:f1},{expC0:f1}"));

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
                devCsv.WriteLine(string.Create(inv, $"{job.Name},{kindName},{seed},{half},{scenario.Name.Replace(',', ';')},{fight},{d.Index},{d.Time:f2},{d.Time - episodeStart:f2},{d.Time - episodeEnd:f2},{OraclePhase(d.Time, episodeStart, episodeEnd)},{d.Gene},{SearchControl.ActionName(d.Picked)},{applied},{d.Names.Replace(',', ';')},{d.State},{d.GcdLeft:f2},{prev},{next},{(outc.Marginals.TryGetValue(d.Index, out var mg) ? mg : double.NaN):f1},{(outc.ChainGains.TryGetValue(d.Index, out var cg) ? cg : double.NaN):f1},{detail},{episodeEnd - episodeStart:f2},{(episode.BlocksGCD ? 1 : 0)},{cfg.Know}"));
            }
        }
        Deviations("episode", outcome, c);
        if (controlOk)
            Deviations("control", control, c0);
        return new OracleRow(a.Total, b.Total, c.Total, true);
    }
}