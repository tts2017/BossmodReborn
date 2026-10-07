using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BossMod;
using EncounterTimeline;

namespace XanTimelineHarness;

// oracle-search: offline search oracle for the headroom of a rotation module around ONE unpredicted irregular episode.
//
// Per scenario (a match of the combat-matrix suite) and per episode kind the fight carries exactly one episode (Irregular.SingleEpisode), and is run
//   a) without the episode (same range/movement/client-reject enforcement as the episode runs),
//   b) with the episode and the real module unchanged,
//   c) with the episode and the best genome the search found (decisions in [episode.Start - pre, episode.End + window] are controllable, see SearchControl).
// Fitness is the per-fight Metrics.Total, the same field irregular-compare uses. Loss L = a - b, headroom H = c - b.
// The oracle only knows what a perfect-hindsight replanner at the episode start would know: the episode end time (the genome was found by replaying
// the whole fight), and it can only choose among the candidates the module itself proposes.
//
// Search = greedy coordinate ascent over the decision indices in order (every alternative and Hold, earlier genes fixed, keep the best if it beats the
// current total by more than eps), then a steady-state RHEA-lite refinement (population, 1-3 gene mutations, uniform crossover) seeded with the chain
// result, then a prune pass that zeroes genes which do not matter and measures each remaining gene by leave-one-out.
// Gene 0 everywhere is verified to reproduce the plain module run (total and action hash) for every scenario.
internal static partial class Program
{
    private sealed class OracleConfig
    {
        public int[] Seeds = [1];
        public IrregularKind[] Kinds = [IrregularKind.Lockout, IrregularKind.LineOfSight, IrregularKind.Range, IrregularKind.Loss];
        public float Rate = 4f;
        public int ShardIndex, ShardCount = 1;
        public int Sample;           // processed scenarios per (kind, seed) over all shards; 0 = all
        public int Budget = 300;     // RHEA evaluations per scenario
        public int Population = 24;
        public float Window = 25f, Before = 3f, Recovery = 25f;
        public double Eps = 0.5;
        public string Out = "oracle";
        public int RngSeed = 1;
        public bool ChainOnly;
        public int MaxChainEvals = 600;
        public string? TraceDir;      // --oracle-trace-dir: write the action traces of the four final replays (b, c, a, c0) of every scenario
        public float ValShift1 = 3.1f, ValShift2 = 8.7f; // validation: replay the found genomes in fights this many seconds longer (0 = off)
        public SearchControl.Knowledge Know = SearchControl.Knowledge.K0; // --oracle-know K0|K1|K2, see OracleKnowledge.cs
        public int Lengths = 4;          // K1/K2: episode lengths the genome is evaluated on at once
        public float Reaction = -1f;     // K2: seconds until the episode is noticed (negative = 0.5..1.0 drawn per scenario)
        public bool Control = true;  // also search the fight without the episode over the same window, to separate the module slack that has nothing to do with the episode
        public string Mode = "";     // --oracle-mode burst: the burst-pinned oracle over the full irregular schedule (BurstOracle.cs)
        public float MinDuration = 170f; // burst mode: only fights at least this long (the opener plus at least one later recast)
        public bool Rdps = true;     // burst mode: fitness is Metrics.RdpsTotal (party side of our own buffs included) instead of Metrics.Total
        public bool Robust;          // burst mode K1/K2: fitness over the length variants is 0.5 * mean + 0.5 * worst variant (a gene has to help in every variant) instead of the mean
    }

    private readonly record struct OracleDecisionInfo(int Index, float Time, ActionID Picked, ActionID Applied, int Gene, int Alternatives, float GcdLeft, string Names, string State, string Phase = "", float PhaseTime = 0f);

    private sealed class OracleEval
    {
        public bool Ok;
        public double Total, Potency;
        public ulong Hash;
        public int Failures, Clipped, Holds, Alts;
        public OracleDecisionInfo[] Decisions = [];
    }

    private static OracleConfig ParseOracleConfig(HarnessOptions options)
    {
        var cfg = new OracleConfig();
        var inv = CultureInfo.InvariantCulture;
        foreach (var (key, value) in options.OracleArgs)
        {
            switch (key)
            {
                case "--oracle-seeds": cfg.Seeds = value.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.Parse(s, inv)).ToArray(); break;
                case "--oracle-kinds": cfg.Kinds = value.Split(",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Irregular.ParseKinds).ToArray(); break;
                case "--oracle-rate": cfg.Rate = float.Parse(value, inv); break;
                case "--oracle-shard":
                    var parts = value.Split("/");
                    cfg.ShardIndex = int.Parse(parts[0], inv);
                    cfg.ShardCount = int.Parse(parts[1], inv);
                    break;
                case "--oracle-sample": cfg.Sample = int.Parse(value, inv); break;
                case "--oracle-budget": cfg.Budget = int.Parse(value, inv); break;
                case "--oracle-pop": cfg.Population = int.Parse(value, inv); break;
                case "--oracle-window": cfg.Window = float.Parse(value, inv); break;
                case "--oracle-pre": cfg.Before = float.Parse(value, inv); break;
                case "--oracle-recovery": cfg.Recovery = float.Parse(value, inv); break;
                case "--oracle-eps": cfg.Eps = double.Parse(value, inv); break;
                case "--oracle-out": cfg.Out = value; break;
                case "--oracle-rng": cfg.RngSeed = int.Parse(value, inv); break;
                case "--oracle-chain-only": cfg.ChainOnly = value == "1"; break;
                case "--oracle-control": cfg.Control = value == "1"; break;
                case "--oracle-val-shifts":
                    var shifts = value.Split(",");
                    cfg.ValShift1 = float.Parse(shifts[0], inv);
                    cfg.ValShift2 = shifts.Length > 1 ? float.Parse(shifts[1], inv) : 0f;
                    break;
                case "--oracle-know": cfg.Know = Enum.Parse<SearchControl.Knowledge>(value, true); break;
                case "--oracle-lens": cfg.Lengths = int.Parse(value, inv); break;
                case "--oracle-reaction": cfg.Reaction = float.Parse(value, inv); break;
                case "--oracle-trace-dir": cfg.TraceDir = value; break;
                case "--oracle-mode": cfg.Mode = value; break;
                case "--oracle-min-duration": cfg.MinDuration = float.Parse(value, inv); break;
                case "--oracle-objective": cfg.Rdps = value != "total"; break;
                case "--oracle-fitness": cfg.Robust = value == "robust"; break;
                case "--oracle-pre-burst": SearchControl.PreBurst = float.Parse(value, inv); break;
                case "--oracle-post-burst": SearchControl.PostBurst = float.Parse(value, inv); break;
                case "--oracle-post-episode": SearchControl.PostEpisode = float.Parse(value, inv); break;
                case "--oracle-opener": SearchControl.IncludeOpener = value == "1"; break;
                case "--oracle-max-chain": cfg.MaxChainEvals = int.Parse(value, inv); break;
                case "--oracle-hold-cap": SearchControl.HoldCap = float.Parse(value, inv); break;
                default: throw new ArgumentException($"Unknown oracle option {key}.");
            }
        }
        return cfg;
    }

    private static uint OracleHash(string text, uint salt)
    {
        var hash = 2166136261u ^ salt;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash;
    }

    // one fight with the given genome; control=false is the plain module (no hook, no window)
    private static OracleEval OracleEvaluate(JobAdapter job, TimelineMatrixScenario scenario, OracleConfig cfg, int seed, IrregularKind kind, bool episode, bool control, int[] genome, bool detail, IrregularDriver.Episode? replay = null)
    {
        ResetGnbLearnedWindows();
        ClientReject.Reset();
        ClientReject.Scenario = scenario.Name;
        Irregular.EnforceBaseline = true;
        Irregular.SingleEpisode = episode;
        Irregular.SingleEpisodeRecovery = cfg.Recovery;
        Irregular.LastSingle = null;
        Irregular.ReplayTemplate = replay;
        Irregular.Seed = episode ? seed : null;
        Irregular.RatePerMinute = cfg.Rate;
        Irregular.Kinds = kind;
        SearchControl.Active = control;
        SearchControl.Genome = genome;
        SearchControl.Detail = detail;
        try
        {
            var result = new ContinuousTimelineRunner(job, scenario.ZoneID, scenario.Duration, scenario.TargetUnavailableWindows, null, []).Run();
            var eval = new OracleEval
            {
                Ok = true,
                Total = result.Metrics.Total,
                Potency = result.Metrics.Potency,
                Hash = ContinuousTimelineRunner.LastActionHash,
                Failures = result.Failures.Count
            };
            if (control)
            {
                eval.Clipped = SearchControl.GenesClipped;
                eval.Holds = SearchControl.HoldsApplied;
                eval.Alts = SearchControl.AlternativesApplied;
                eval.Decisions = SearchControl.Decisions.Select(d => new OracleDecisionInfo(d.Index, d.Time, d.Picked, d.Applied, d.Gene, d.Alternatives, d.GcdLeft, d.Names, d.State)).ToArray();
            }
            return eval;
        }
        catch (Exception ex)
        {
            Irregular.Current = null;
            Console.WriteLine($"oracle_exception scenario={scenario.Name} {ex.GetType().Name}: {ex.Message.Split(Environment.NewLine)[0]}");
            return new OracleEval { Ok = false, Total = double.NegativeInfinity };
        }
        finally
        {
            SearchControl.Active = false;
            Irregular.ReplayTemplate = null;
        }
    }

    private static string GenomeKey(int[] genome) => string.Join(",", Trim(genome));

    private static int[] Trim(int[] genome)
    {
        var length = genome.Length;
        while (length > 0 && genome[length - 1] == 0)
            --length;
        return genome[..length];
    }

    private static int OracleRunCount;

    private static int RunOracleSearch(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        var inv = CultureInfo.InvariantCulture;
        var cfg = ParseOracleConfig(options);
        var catalog = EventTriggerTimelineCatalog.Load(ResolveEventTriggerTimelineRoot(options.TimelineRoot));
        var jobs = SelectJobs(options.JobSelector);
        var scenarios = BuildTimelineMatrix(catalog, CombatMatrixTargetLossWarmups)
            .Where(scenario => options.ScenarioFilter == null || scenario.Name.Contains(options.ScenarioFilter, StringComparison.OrdinalIgnoreCase))
            .Take(options.ScenarioLimit ?? int.MaxValue)
            .Select(scenario => options.ExtraPostRoll is { } extra ? scenario with { Duration = scenario.Duration + extra } : scenario)
            .ToArray();
        ContinuousTimelineRunner.TargetLossHintLead = options.TargetLossHintLead;
        ContinuousTimelineRunner.PlayerLevel = options.PlayerLevel;
        ContinuousTimelineRunner.PlayerSkillSpeed = options.SkillSpeed;
        ContinuousTimelineRunner.Potions = options.Potions;
        ContinuousTimelineRunner.ExtraTargets = options.ExtraTargets;
        ContinuousTimelineRunner.MnkEncounterHintOverride = options.MnkEncounterHint;
        ContinuousTimelineRunner.BlmRotationOverride = options.BlmRotation;
        ContinuousTimelineRunner.TrackOverrides = options.TrackOverrides;
        ContinuousTimelineRunner.PartyBuffFirstCast = options.PartyBuffFirstCast;
        ContinuousTimelineRunner.StartSoulsow = options.StartSoulsow;
        ContinuousTimelineRunner.RandomDisengageSeed = null;
        ContinuousTimelineRunner.OracleMarginBefore = cfg.Before;
        ContinuousTimelineRunner.OracleWindow = cfg.Window;
        ClientReject.Enabled = true;
        ClientReject.BaseTime = BaseTime;
        SearchControl.Level = cfg.Know;
        if (cfg.Know != SearchControl.Knowledge.K0)
            Console.WriteLine(string.Create(inv, $"oracle_know level={cfg.Know} lengths={cfg.Lengths} reaction={(cfg.Reaction < 0 ? "0.5-1.0" : cfg.Reaction.ToString("f2", inv))}"));
        Console.WriteLine(string.Create(inv, $"oracle_search jobs={string.Join(",", jobs.Select(j => j.Name))} scenarios={scenarios.Length} seeds={string.Join(",", cfg.Seeds)} kinds={string.Join(",", cfg.Kinds.Select(Irregular.FormatKinds))} rate={cfg.Rate:f1} shard={cfg.ShardIndex}/{cfg.ShardCount} sample={cfg.Sample} budget={cfg.Budget} pop={cfg.Population} window={cfg.Window:f0} pre={cfg.Before:f0} recovery={cfg.Recovery:f0} level={options.PlayerLevel} skill_speed={options.SkillSpeed?.ToString(inv) ?? "default"} tracks={string.Join(";", options.TrackOverrides.Select(t => $"{t.Track}={t.Option}"))}"));

        if (cfg.Mode == "burst")
            return RunBurstOracle(options, cfg, jobs, scenarios);
        var scenPath = cfg.Out + "_scen.csv";
        var devPath = cfg.Out + "_dev.csv";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(scenPath))!);
        using var scenCsv = new StreamWriter(scenPath, false);
        using var devCsv = new StreamWriter(devPath, false);
        using var oppCsv = new StreamWriter(cfg.Out + "_opp.csv", false);
        oppCsv.WriteLine("job,kind,seed,half,scenario,module,phase,count");
        scenCsv.WriteLine("job,kind,seed,half,scenario,duration,ep_start,ep_end,ep_len,ep_detail,block_gcd,a_total,b_total,c_total,loss,headroom,h_over_l,h_over_a,decisions,evals,chain_evals,rhea_evals,chain_gain,n_genes,n_hold,n_alt,clipped,seconds,selftest,b_failures,c_failures,b_refused,ctrl_hash_same,c0_total,h0,control_ok,control_evals,v1_b,v1_c,v1_a,v1_c0,v2_b,v2_c,v2_a,v2_c0" + (cfg.Know != SearchControl.Knowledge.K0 ? ",know,nlen,lens,exp_b,exp_c,exp_a,exp_c0" : ""));
        devCsv.WriteLine("job,kind,seed,half,scenario,fight,idx,t,t_rel_start,t_rel_end,phase,gene,module,applied,alternatives,state,gcd_left,prev_applied,next_applied,marginal,chain_gain,ep_detail,ep_len,block_gcd" + (cfg.Know != SearchControl.Knowledge.K0 ? ",know" : ""));

        foreach (var job in jobs)
        {
            // BLM runs its boundary self-tests inside the first BLM run of a process; burn one so no measured run carries them
            if (job.Class == Class.BLM && scenarios.Length > 0)
                OracleEvaluate(job, scenarios[0], cfg, cfg.Seeds[0], cfg.Kinds[0], false, false, [], false);
            foreach (var kind in cfg.Kinds)
            foreach (var seed in cfg.Seeds)
            {
                var kindName = Irregular.FormatKinds(kind);
                // scenarios in a seed/kind specific pseudo random order: the first N that carry an eligible episode are the sample
                var ordered = scenarios.Select(s => (Scenario: s, Hash: OracleHash(s.Name + "|" + kindName + "|" + seed, 77u))).OrderBy(x => x.Hash).Select(x => x.Scenario).ToArray();
                var perShard = cfg.Sample > 0 ? (cfg.Sample + cfg.ShardCount - 1) / cfg.ShardCount : int.MaxValue;
                var processed = 0;
                var skippedNoEpisode = 0;
                var selftestFailures = 0;
                var started = System.Diagnostics.Stopwatch.StartNew();
                double sumA = 0, sumB = 0, sumC = 0;
                for (var i = 0; i < ordered.Length && processed < perShard; ++i)
                {
                    if (i % cfg.ShardCount != cfg.ShardIndex)
                        continue;
                    var scenario = ordered[i];
                    var row = cfg.Know == SearchControl.Knowledge.K0 ? OracleScenario(job, scenario, cfg, seed, kind, kindName, scenCsv, devCsv, oppCsv) : OracleScenarioK(job, scenario, cfg, seed, kind, kindName, scenCsv, devCsv, oppCsv);
                    if (row == null)
                    {
                        ++skippedNoEpisode;
                        continue;
                    }
                    ++processed;
                    if (!row.Value.SelfTest)
                        ++selftestFailures;
                    sumA += row.Value.A;
                    sumB += row.Value.B;
                    sumC += row.Value.C;
                    if (processed % 10 == 0)
                    {
                        scenCsv.Flush();
                        devCsv.Flush();
                        Console.WriteLine(string.Create(inv, $"oracle_progress job={job.Name} kind={kindName} seed={seed} done={processed} skipped={skippedNoEpisode} selftest_fail={selftestFailures} loss={sumA - sumB:f0} headroom={sumC - sumB:f0} seconds={started.Elapsed.TotalSeconds:f0}"));
                    }
                }
                scenCsv.Flush();
                devCsv.Flush();
                Console.WriteLine(string.Create(inv, $"oracle_result job={job.Name} kind={kindName} seed={seed} scenarios={processed} skipped_no_episode={skippedNoEpisode} selftest_fail={selftestFailures} a={sumA:f0} b={sumB:f0} c={sumC:f0} loss={sumA - sumB:f0} headroom={sumC - sumB:f0} h_over_l={(sumA > sumB ? (sumC - sumB) / (sumA - sumB) : 0):f4} h_over_a={(sumA > 0 ? (sumC - sumB) / sumA : 0):f5} seconds={started.Elapsed.TotalSeconds:f0} runs={OracleRunCount}"));
            }
        }
        Irregular.Seed = null;
        Irregular.SingleEpisode = false;
        Irregular.EnforceBaseline = false;
        return 0;
    }

    private static string OraclePhase(float time, float start, float end)
        => time < start ? "pre" : time < end ? "during" : time < end + 5 ? "post0-5" : time < end + 10 ? "post5-10" : "post10+";

    private readonly record struct OracleRow(double A, double B, double C, bool SelfTest);

    private sealed class OracleOutcome
    {
        public OracleEval Best = new();
        public int[] Genome = [];
        public int Evals, ChainEvals, RheaEvals;
        public double ChainTotal;
        public Dictionary<int, double> Marginals = [];
        public Dictionary<int, double> ChainGains = [];
    }

    // The search itself, independent of what is being searched: run(genome) plays the fight with that genome, start is the all-zero genome run.
    private static OracleOutcome OracleOptimize(Func<int[], OracleEval> run, OracleEval start, OracleConfig cfg, int rngSeed, bool sparse = false, bool timeOrder = false)
    {
        var outcome = new OracleOutcome();
        var maxAlt = new Dictionary<int, int>();
        var cache = new Dictionary<string, OracleEval> { [""] = start };
        var evals = 0;
        void Note(OracleEval e)
        {
            foreach (var d in e.Decisions)
                maxAlt[d.Index] = Math.Max(maxAlt.GetValueOrDefault(d.Index), d.Alternatives);
        }
        OracleEval Search(int[] genome)
        {
            var key = GenomeKey(genome);
            if (cache.TryGetValue(key, out var hit))
                return hit;
            ++evals;
            var eval = run(Trim(genome));
            cache[key] = eval;
            if (eval.Ok)
                Note(eval);
            return eval;
        }
        Note(start);

        var best = start;
        var bestGenome = Array.Empty<int>();
        // (i) greedy chain: decision indices in order, every alternative and Hold with the earlier genes fixed
        var genomeList = new List<int>();
        // the next decision is the first one with a larger gene index than the last one handled (the decisions of K0 are numbered 0,1,2,..., so this is the old i+1)
        var chainIndex = -1;
        var visited = new HashSet<int>();
        while (evals < cfg.MaxChainEvals)
        {
            int next;
            if (timeOrder)
            {
                // burst oracle: the decisions of different windows have unrelated gene numbers, so the chain follows the time of the fight instead
                next = -1;
                for (var q = 0; q < best.Decisions.Length; ++q)
                    if (!visited.Contains(best.Decisions[q].Index) && (next < 0 || best.Decisions[q].Time < best.Decisions[next].Time))
                        next = q;
            }
            else
                next = Array.FindIndex(best.Decisions, d => d.Index > chainIndex);
            if (next < 0)
                break;
            var i = best.Decisions[next].Index;
            chainIndex = i;
            visited.Add(i);
            var alternatives = best.Decisions[next].Alternatives;
            var improvedValue = 0;
            var improved = best;
            for (var value = SearchControl.Hold; value <= alternatives; ++value)
            {
                if (value == 0)
                    continue;
                var trial = genomeList.ToList();
                while (trial.Count <= i)
                    trial.Add(0);
                trial[i] = value;
                var eval = Search(trial.ToArray());
                if (eval.Ok && eval.Total > improved.Total + cfg.Eps)
                {
                    improved = eval;
                    improvedValue = value;
                }
            }
            if (improvedValue != 0)
            {
                while (genomeList.Count <= i)
                    genomeList.Add(0);
                genomeList[i] = improvedValue;
                outcome.ChainGains[i] = improved.Total - best.Total;
                best = improved;
            }
        }
        bestGenome = Trim(genomeList.ToArray());
        outcome.ChainEvals = evals;
        outcome.ChainTotal = best.Total;

        // (ii) RHEA-lite refinement seeded with the chain result
        if (!cfg.ChainOnly && cfg.Budget > 0)
        {
            var rng = new Random(rngSeed ^ cfg.RngSeed);
            var population = new List<(int[] Genome, double Total)> { (bestGenome, best.Total) };
            var limit = evals + cfg.Budget;
            int[] Mutate(int[] parent)
            {
                var length = sparse ? Math.Max(parent.Length, maxAlt.Count > 0 ? maxAlt.Keys.Max() + 1 : 0) : Math.Max(parent.Length, Math.Min(maxAlt.Count > 0 ? maxAlt.Keys.Max() + 1 : 0, best.Decisions.Length + 4));
                if (length == 0)
                    return parent;
                var child = new int[length];
                Array.Copy(parent, child, parent.Length);
                var mutations = 1 + rng.Next(3);
                var sparseKeys = sparse ? maxAlt.Keys.OrderBy(k => k).ToArray() : [];
                for (var m = 0; m < mutations; ++m)
                {
                    int position;
                    var active = child.Select((v, idx) => (v, idx)).Where(x => x.v != 0).Select(x => x.idx).ToArray();
                    // a third of the mutations revisit or undo an already deviating gene, the rest explore new positions
                    if (active.Length > 0 && rng.NextDouble() < 0.33)
                        position = active[rng.Next(active.Length)];
                    else if (sparse)
                        position = sparseKeys[rng.Next(sparseKeys.Length)];
                    else
                        position = rng.Next(length);
                    var alt = maxAlt.GetValueOrDefault(position);
                    child[position] = rng.Next(alt + 2) - 1; // Hold, 0, 1..alt
                }
                return child;
            }
            int[] Crossover(int[] x, int[] y)
            {
                var length = Math.Max(x.Length, y.Length);
                var child = new int[length];
                for (var i = 0; i < length; ++i)
                    child[i] = rng.Next(2) == 0 ? (i < x.Length ? x[i] : 0) : (i < y.Length ? y[i] : 0);
                return child;
            }
            var stagnant = 0;
            while (evals < limit && population.Count < cfg.Population && stagnant <= 200)
            {
                var genome = Mutate(bestGenome);
                var before = evals;
                var eval = Search(genome);
                if (evals == before)
                {
                    ++stagnant;
                    continue;
                }
                if (!eval.Ok)
                    continue;
                population.Add((Trim(genome), eval.Total));
                if (eval.Total > best.Total + 1e-9)
                {
                    best = eval;
                    bestGenome = Trim(genome);
                }
            }
            while (evals < limit && population.Count > 1 && stagnant <= 200)
            {
                (int[] Genome, double Total) Tournament()
                {
                    var p = population[rng.Next(population.Count)];
                    var q = population[rng.Next(population.Count)];
                    return p.Total >= q.Total ? p : q;
                }
                var parent = Tournament();
                var child = rng.NextDouble() < 0.3 ? Crossover(parent.Genome, Tournament().Genome) : parent.Genome;
                child = Mutate(child);
                var before = evals;
                var eval = Search(child);
                if (evals == before)
                {
                    ++stagnant; // the neighbourhood is exhausted when every proposal was already evaluated
                    continue;
                }
                stagnant = 0;
                if (!eval.Ok)
                    continue;
                var worst = 0;
                for (var i = 1; i < population.Count; ++i)
                    if (population[i].Total < population[worst].Total)
                        worst = i;
                if (eval.Total >= population[worst].Total)
                    population[worst] = (Trim(child), eval.Total);
                if (eval.Total > best.Total + 1e-9)
                {
                    best = eval;
                    bestGenome = Trim(child);
                }
            }
        }
        outcome.RheaEvals = evals - outcome.ChainEvals;

        // (iii) prune: drop genes that do not matter, measure the rest by leave-one-out
        if (bestGenome.Length > 0)
        {
            var current = bestGenome.ToArray();
            for (var i = 0; i < current.Length; ++i)
            {
                if (current[i] == 0)
                    continue;
                var without = current.ToArray();
                without[i] = 0;
                var eval = Search(without);
                if (eval.Ok && eval.Total >= best.Total - 1e-9)
                {
                    current = Trim(without);
                    best = eval;
                    continue;
                }
                outcome.Marginals[i] = eval.Ok ? best.Total - eval.Total : double.NaN;
            }
            bestGenome = Trim(current);
        }
        outcome.Best = best;
        outcome.Genome = bestGenome;
        outcome.Evals = evals;
        return outcome;
    }

    private static OracleRow? OracleScenario(JobAdapter job, TimelineMatrixScenario scenario, OracleConfig cfg, int seed, IrregularKind kind, string kindName, StreamWriter scenCsv, StreamWriter devCsv, StreamWriter oppCsv)
    {
        var inv = CultureInfo.InvariantCulture;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int runs = 0;
        OracleEval Eval(bool episode, bool control, int[] genome, bool detail = false, float extend = 0f, IrregularDriver.Episode? replay = null)
        {
            ++runs;
            ++OracleRunCount;
            return OracleEvaluate(job, extend > 0 ? scenario with { Duration = scenario.Duration + extend } : scenario, cfg, seed, kind, episode, control, genome, detail, replay);
        }

        // b: the plain module with the episode; no episode scheduled means nothing to measure
        SearchControl.FixedWindow = null;
        var b = Eval(true, false, []);
        var episode = Irregular.LastSingle;
        if (!b.Ok || episode == null)
            return null;
        var episodeStart = episode.Start;
        var episodeEnd = episode.End;
        var episodeLength = episodeEnd - episodeStart;
        var detail = episode.Kind switch
        {
            IrregularKind.Lockout => episode.Status.ToString(),
            IrregularKind.Range => string.Create(inv, $"far={episode.Far:f1}"),
            _ => ""
        };
        var blocksGcd = episode.BlocksGCD;
        var refused = episode.Refusals;
        var half = (int)(OracleHash(scenario.Name + "|" + kindName + "|" + seed, 991u) & 1u);
        var rngSeed = (int)(OracleHash(scenario.Name + "|" + kindName + "|" + seed, 4242u) & 0x7FFFFFFF);

        // gene 0 everywhere must be the plain module run, byte for byte
        var b0 = Eval(true, true, []);
        var selfTest = b0.Ok && b0.Hash == b.Hash && b0.Total == b.Total && b0.Decisions.All(d => d.Applied == d.Picked);
        var a = Eval(false, false, []);
        if (selfTest)
        {
            // how often the module picked each action in each phase of the window: the denominator for the deviation rates
            foreach (var group in b0.Decisions.GroupBy(d => (Action: SearchControl.ActionName(d.Picked), Phase: OraclePhase(d.Time, episodeStart, episodeEnd))))
                oppCsv.WriteLine(string.Create(inv, $"{job.Name},{kindName},{seed},{half},{scenario.Name.Replace(',', ';')},{group.Key.Action},{group.Key.Phase},{group.Count()}"));
        }

        // (c) the oracle on the fight with the episode
        var outcome = selfTest ? OracleOptimize(genome => Eval(true, true, genome), b0, cfg, rngSeed) : new OracleOutcome { Best = b0 };
        var c = b0;
        if (selfTest)
        {
            // the final run, with the candidate names and the emulator state, is also the determinism check of the whole search
            c = Eval(true, true, outcome.Genome, detail: true);
            if (c.Total != outcome.Best.Total)
            {
                Console.WriteLine(string.Create(inv, $"oracle_nondeterministic scenario={scenario.Name} search_total={outcome.Best.Total:f2} replay_total={c.Total:f2}"));
                selfTest = false;
            }
        }

        // control: the same search on the fight WITHOUT the episode over the same time window. Whatever it finds is slack of the module that has
        // nothing to do with the episode (end-of-fight and burst alignment, hindsight luck), so headroom - control headroom is the part the episode adds.
        var controlTotal = a.Total;
        var controlOk = false;
        OracleOutcome control = new() { Best = a.Ok ? new OracleEval { Ok = true, Total = a.Total } : new OracleEval() };
        OracleEval c0 = a;
        if (cfg.Control && selfTest)
        {
            SearchControl.FixedWindow = (episodeStart - cfg.Before, episodeEnd + cfg.Window);
            var a0 = Eval(false, true, []);
            controlOk = a0.Ok && a0.Hash == a.Hash && a0.Total == a.Total;
            if (controlOk)
            {
                control = OracleOptimize(genome => Eval(false, true, genome), a0, cfg, rngSeed ^ 0x2545F491);
                c0 = Eval(false, true, control.Genome, detail: true);
                controlTotal = c0.Total;
                if (c0.Total != control.Best.Total)
                {
                    Console.WriteLine(string.Create(inv, $"oracle_nondeterministic scenario={scenario.Name} control search_total={control.Best.Total:f2} replay_total={c0.Total:f2}"));
                    controlOk = false;
                    controlTotal = a.Total;
                }
            }
            SearchControl.FixedWindow = null;
        }

        // end-shift validation: the genomes were found for ONE fight length, so part of the headroom can be end-of-fight alignment luck (a tool that happens
        // to fit once more before the end). Replaying both genomes in fights a few seconds longer, against the plain module in the same fights, shows
        // how much of the headroom survives. The episode itself is rebuilt unchanged (the seeded schedule would differ with the length).
        var validation = new double[8];
        var validated = false;
        if (selfTest && (cfg.ValShift1 > 0 || cfg.ValShift2 > 0))
        {
            var template = episode;
            var shifts = new[] { cfg.ValShift1, cfg.ValShift2 };
            for (var s = 0; s < 2; ++s)
            {
                if (shifts[s] <= 0)
                    continue;
                SearchControl.FixedWindow = null;
                var bv = Eval(true, false, [], extend: shifts[s], replay: template);
                var cv = Eval(true, true, outcome.Genome, extend: shifts[s], replay: template);
                var av = Eval(false, false, [], extend: shifts[s]);
                var c0v = a;
                if (cfg.Control && controlOk)
                {
                    SearchControl.FixedWindow = (episodeStart - cfg.Before, episodeEnd + cfg.Window);
                    c0v = Eval(false, true, control.Genome, extend: shifts[s]);
                    SearchControl.FixedWindow = null;
                }
                validation[s * 4] = bv.Total;
                validation[s * 4 + 1] = cv.Total;
                validation[s * 4 + 2] = av.Total;
                validation[s * 4 + 3] = cfg.Control && controlOk ? c0v.Total : av.Total;
                validated = true;
            }
        }

        if (cfg.TraceDir != null)
        {
            // XAN_HARNESS_TRACE_DIR traces of the four fights, in the order b, c, a, c0, one folder per scenario
            var folder = Path.Combine(cfg.TraceDir, job.Name + "_" + kindName + "_s" + seed + "_" + (OracleHash(scenario.Name, 5u) % 100000).ToString("D5"));
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(folder).FullName, "scenario.txt"), scenario.Name + " episode=" + episodeStart.ToString("f2", inv) + "-" + episodeEnd.ToString("f2", inv) + " " + detail + " genome=" + string.Join(",", outcome.Genome) + " control_genome=" + string.Join(",", control.Genome) + "\n");
            var previous = Environment.GetEnvironmentVariable("XAN_HARNESS_TRACE_DIR");
            Environment.SetEnvironmentVariable("XAN_HARNESS_TRACE_DIR", folder);
            Eval(true, false, []);                  // 1: b, the module with the episode
            Eval(true, true, outcome.Genome);       // 2: c, the oracle with the episode
            Eval(false, false, []);                 // 3: a, no episode
            if (cfg.Control && controlOk)
            {
                SearchControl.FixedWindow = (episodeStart - cfg.Before, episodeEnd + cfg.Window);
                Eval(false, true, control.Genome);  // 4: c0, the control oracle
                SearchControl.FixedWindow = null;
            }
            Environment.SetEnvironmentVariable("XAN_HARNESS_TRACE_DIR", previous);
        }

        var loss = a.Total - b.Total;
        var headroom = c.Total - b.Total;
        var holds = outcome.Genome.Count(g => g == SearchControl.Hold);
        var alts = outcome.Genome.Count(g => g > 0);
        var controlHeadroom = controlTotal - a.Total;
        scenCsv.WriteLine(string.Create(inv, $"{job.Name},{kindName},{seed},{half},{scenario.Name.Replace(',', ';')},{scenario.Duration:f1},{episodeStart:f2},{episodeEnd:f2},{episodeLength:f2},{detail},{(blocksGcd ? 1 : 0)},{a.Total:f1},{b.Total:f1},{c.Total:f1},{loss:f1},{headroom:f1},{(loss > 1 ? headroom / loss : 0):f4},{(a.Total > 0 ? headroom / a.Total : 0):f5},{b0.Decisions.Length},{runs},{outcome.ChainEvals},{outcome.RheaEvals},{outcome.ChainTotal - b.Total:f1},{holds + alts},{holds},{alts},{c.Clipped},{timer.Elapsed.TotalSeconds:f2},{(selfTest ? 1 : 0)},{b.Failures},{c.Failures},{refused},{(b0.Hash == b.Hash ? 1 : 0)},{controlTotal:f1},{controlHeadroom:f1},{(controlOk ? 1 : 0)},{(cfg.Control && controlOk ? control.Evals : 0)},{string.Join(",", validation.Select(v => v.ToString("f1", inv)))}"));
        void Deviations(string fight, OracleOutcome outc, OracleEval run)
        {
            for (var i = 0; i < outc.Genome.Length; ++i)
            {
                if (outc.Genome[i] == 0 || i >= run.Decisions.Length)
                    continue;
                var d = run.Decisions[i];
                var phase = OraclePhase(d.Time, episodeStart, episodeEnd);
                var applied = outc.Genome[i] == SearchControl.Hold ? "HOLD" : SearchControl.ActionName(d.Applied);
                var prev = i > 0 ? SearchControl.ActionName(run.Decisions[i - 1].Applied) : "-";
                var next = i + 1 < run.Decisions.Length ? (run.Decisions[i + 1].Gene == SearchControl.Hold ? "HOLD" : SearchControl.ActionName(run.Decisions[i + 1].Applied)) : "-";
                devCsv.WriteLine(string.Create(inv, $"{job.Name},{kindName},{seed},{half},{scenario.Name.Replace(',', ';')},{fight},{i},{d.Time:f2},{d.Time - episodeStart:f2},{d.Time - episodeEnd:f2},{phase},{outc.Genome[i]},{SearchControl.ActionName(d.Picked)},{applied},{d.Names.Replace(',', ';')},{d.State},{d.GcdLeft:f2},{prev},{next},{(outc.Marginals.TryGetValue(i, out var mg) ? mg : double.NaN):f1},{(outc.ChainGains.TryGetValue(i, out var cg) ? cg : double.NaN):f1},{detail},{episodeLength:f2},{(blocksGcd ? 1 : 0)}"));
            }
        }
        if (selfTest)
            Deviations("episode", outcome, c);
        if (cfg.Control && controlOk)
            Deviations("control", control, c0);
        return new OracleRow(a.Total, b.Total, c.Total, selfTest);
    }
}