using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BossMod;

namespace XanTimelineHarness;

// "Burst on recast, never delayed": the harness side of the study.
//
// A burst starter is the 60 s / 120 s cooldown action that opens a burst window (Brotherhood, Arcane Circle, Dokumori, Wildfire, ...). This file does two things
// for the job emulators, both off unless an option asks for them (a default run stays byte-identical):
//   1. metrics (--burst-metrics on | --burst-csv <file>): for every cycle of every starter the time from "recast finished" to "used" (burst latency), split
//      into what kept the starter from going out (no target, locked, out of range, an emulator prerequisite missing, the module not proposing it, a module
//      delay or the weave window, a higher priority pick), and the overlap of the burst with the --party-buffs windows (burst alignment);
//   2. a variant policy (--burst-policy forced|inject): the starter is released the moment it is usable. forced = only when the module has it in its candidate
//      list (its priority is raised above every other oGCD, below every GCD, and its delay removed), inject = also when the module did not propose it.
//      The opener (the first use of each starter) stays with the module unless --burst-opener pin is given.
//
// Process-wide state like SearchControl / Irregular: the harness runs one fight at a time.
internal static class BurstControl
{
    public enum PolicyKind { Default, Forced, Inject }

    public static bool Metrics;
    public static string? CsvPath;
    public static string? ScenPath;        // --burst-scen <file>: one compact row per fight (totals + burst columns), instead of the heavy XAN_HARNESS_TRACE_DIR
    public static PolicyKind Policy = PolicyKind.Default;
    public static bool PinOpener;          // --burst-opener pin: the policy also takes over the first use of every starter
    public static bool PrimaryOnly;        // --burst-starters primary: only the headline starters (the stars of the report) take part (metrics and policy)
    public static HashSet<string>? OnlyNames; // --burst-only A,B: only these starters (by Starter.Name) take part, to price one starter at a time
    public static float PinPriority = 4000.5f; // above every oGCD of the xan and akechi modules (Low + p), below every GCD (High + p, p >= 1)
    public static bool Enabled => Metrics || CsvPath != null || ScenPath != null || Policy != PolicyKind.Default;
    // the oracle pins the starters through SearchControl whenever a policy is active
    public static bool Pinning => Policy != PolicyKind.Default;

    // the tracker of the fight in progress (null when nothing is enabled)
    public static BurstTracker? Current;

    // every cycle row of the run of a job, printed and cleared by Print()
    public static readonly List<BurstTracker.Cycle> Rows = [];
    public static long ForcedFrames, InjectedFrames, NotProposedFrames;
    public static int LastAnchorUses;       // anchor starter uses of the last fight (the oracle skips fights without a second recast)
    public static bool CollectRows = true;   // the oracle runs hundreds of thousands of fights and never prints the rows
    private static StreamWriter? _csv;

    public sealed record Starter(string Name, ActionID Action, float BurstSeconds, bool Primary);

    private static Starter S<T>(T aid, string name, float burst, bool primary) where T : Enum => new(name, ActionID.MakeSpell(aid), burst, primary);

    // Level 100 starters per job (see report.md for the choice). Order matters: the first primary starter is the anchor of the oracle's burst windows.
    public static IReadOnlyList<Starter> StartersFor(Class job) => job switch
    {
        Class.MNK => [S(BossMod.MNK.AID.Brotherhood, "Brotherhood", 20, true), S(BossMod.MNK.AID.RiddleOfFire, "RiddleOfFire", 20, false)],
        Class.RPR => [S(BossMod.RPR.AID.ArcaneCircle, "ArcaneCircle", 20, true)],
        Class.MCH => [S(BossMod.MCH.AID.Wildfire, "Wildfire", 10, true), S(BossMod.MCH.AID.BarrelStabilizer, "BarrelStabilizer", 30, true)],
        Class.BLM => [S(BossMod.BLM.AID.LeyLines, "LeyLines", 20, true), S(BossMod.BLM.AID.Amplifier, "Amplifier", 15, true), S(BossMod.BLM.AID.Manafont, "Manafont", 15, false)],
        Class.SAM => [S(BossMod.SAM.AID.Ikishoten, "Ikishoten", 20, true), S(BossMod.SAM.AID.MeikyoShisui, "MeikyoShisui", 20, false)],
        Class.VPR => [S(BossMod.VPR.AID.SerpentsIre, "SerpentsIre", 20, true)],
        Class.GNB => [S(BossMod.GNB.AID.NoMercy, "NoMercy", 20, true), S(BossMod.GNB.AID.Bloodfest, "Bloodfest", 20, true)],
        Class.DRG => [S(BossMod.DRG.AID.BattleLitany, "BattleLitany", 20, true), S(BossMod.DRG.AID.LanceCharge, "LanceCharge", 20, false)],
        Class.NIN => [S(BossMod.NIN.AID.Dokumori, "Dokumori", 20, true), S(BossMod.NIN.AID.KunaisBane, "KunaisBane", 15, false)],
        Class.PLD => [S(BossMod.PLD.AID.FightOrFlight, "FightOrFlight", 20, true), S(BossMod.PLD.AID.Imperator, "Imperator", 20, false)],
        _ => []
    };

    // the anchor of the oracle's burst windows: the first 120 s starter, or the first starter of a job that only has 60 s ones
    public static Starter? AnchorOf(Class job) => StartersFor(job).FirstOrDefault(s => s.Primary) ?? StartersFor(job).FirstOrDefault();

    // --burst-* options (HarnessOptions.Parse): metrics on|off, csv <file>, policy default|forced|inject, opener free|pin, starters all|primary, priority <float>
    public static void Configure(IReadOnlyDictionary<string, string> args)
    {
        foreach (var (key, value) in args)
        {
            switch (key)
            {
                case "--burst-metrics": Metrics = value is "on" or "1" or "true"; break;
                case "--burst-csv": CsvPath = value; break;
                case "--burst-scen": ScenPath = value; break;
                case "--burst-policy": Policy = Enum.Parse<PolicyKind>(value, true); break;
                case "--burst-opener": PinOpener = value == "pin"; break;
                case "--burst-starters": PrimaryOnly = value == "primary"; break;
                case "--burst-only": OnlyNames = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase); break;
                case "--burst-priority": PinPriority = float.Parse(value, CultureInfo.InvariantCulture); break;
                case "--burst-rule":
                    // <phase>|<module pick>|<HOLD or action>[;...] see SearchControl.BurstRule; turns the oracle's search machinery into a fixed rule for a whole matrix run
                    SearchControl.Rules = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(r => r.Split('|')).Select(p => new SearchControl.BurstRule(p[0], p[1], p[2])).ToList();
                    break;
                default: throw new ArgumentException($"Unknown burst option {key}.");
            }
        }
        if (SearchControl.RuleMode)
        {
            SearchControl.Active = true;
            SearchControl.BurstMode = true;
            SearchControl.BurstKnow = 2; // a rule may only use what a player sees: no episode start in advance
            SearchControl.Genome = [];
            Metrics = true; // the windows come from the tracker
        }
        if (Enabled)
            Metrics = true;
    }

    public static void WriteCsv(string line)
    {
        if (CsvPath == null)
            return;
        if (_csv == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(CsvPath))!);
            _csv = new StreamWriter(CsvPath, false);
            _csv.WriteLine(BurstTracker.Cycle.CsvHeader);
        }
        _csv.WriteLine(line);
    }

    public static void FlushCsv() => _csv?.Flush();

    private static StreamWriter? _scen;

    public static void WriteScen(string line)
    {
        if (ScenPath == null)
            return;
        if (_scen == null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ScenPath))!);
            _scen = new StreamWriter(ScenPath, false);
            _scen.WriteLine("job,scenario,duration,potency,total,rdps,gcds,failures," + BurstTracker.CsvHeader);
        }
        _scen.WriteLine(line);
        _scen.Flush();
    }

    private static double Percentile(List<float> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];

    // stdout summary of the cycles collected since the last call (one job's matrix run), then reset
    public static void Print(TextWriter writer, string job)
    {
        if (!Metrics)
            return;
        var inv = CultureInfo.InvariantCulture;
        foreach (var group in Rows.GroupBy(r => r.Starter))
        {
            foreach (var (label, rows) in new[] { ("all", group.ToList()), ("after_opener", group.Where(r => r.Index > 0).ToList()) })
            {
                var used = rows.Where(r => !r.Unused).ToList();
                var lat = used.Select(r => r.Latency).OrderBy(x => x).ToList();
                var moduleLat = used.Select(r => r.ModuleSeconds).OrderBy(x => x).ToList();
                var overlap = used.Where(r => !float.IsNaN(r.OverlapFraction)).ToList();
                writer.WriteLine(string.Create(inv, $"burst job={job} policy={Policy.ToString().ToLowerInvariant()} starter={group.Key} set={label} cycles={rows.Count} used={used.Count} unused={rows.Count - used.Count} lat_avg={(lat.Count > 0 ? lat.Average() : double.NaN):f3} lat_p50={Percentile(lat, 0.5):f3} lat_p90={Percentile(lat, 0.9):f3} lat_max={(lat.Count > 0 ? lat[^1] : double.NaN):f2} module_avg={(moduleLat.Count > 0 ? moduleLat.Average() : double.NaN):f3} module_p90={Percentile(moduleLat, 0.9):f3} le_0.1s={used.Count(r => r.ModuleSeconds <= 0.1f)} gt_2s={used.Count(r => r.ModuleSeconds > 2f)} gt_5s={used.Count(r => r.ModuleSeconds > 5f)} aligned={overlap.Count(r => r.OverlapFraction >= 0.5f)}/{overlap.Count} overlap_avg={(overlap.Count > 0 ? overlap.Average(r => r.OverlapFraction) : double.NaN):f3}"));
            }
        }
        if (Policy != PolicyKind.Default)
            writer.WriteLine(string.Create(inv, $"burst_policy job={job} policy={Policy.ToString().ToLowerInvariant()} forced_frames={ForcedFrames} injected_frames={InjectedFrames} not_proposed_ready_frames={NotProposedFrames}"));
        Rows.Clear();
        ForcedFrames = InjectedFrames = NotProposedFrames = 0;
        FlushCsv();
    }
}

// Per-fight tracker of the starters of one job.
internal sealed class BurstTracker
{
    public enum Cause { NoTarget, Locked, Range, Prereq, NotProposed, ModuleDelay, GcdWindow, Outprio }

    public sealed class Cycle
    {
        public string Job = "", Scenario = "", Starter = "";
        public int Index;                 // 0 = the opener use
        public float ReadyAt;             // recast finished
        public float UsedAt = float.NaN;
        public bool Unused;               // still unused at the end of the fight
        public float EndAt;               // fight end (censoring time for an unused cycle)
        public bool Prepull;              // used during the countdown
        public bool StoredCharge;         // another charge was already waiting when the previous use happened
        public bool Forced;               // the policy took the starter out of the queue (forced or injected)
        public bool Injected;             // ... although the module had not proposed it
        public bool ProposedAtUse;        // the module had it in its candidate list in the frame it went out
        public readonly float[] Seconds = new float[8];
        public string StateAtReady = "", QueueAtReady = "", StateAtUse = "";
        public float OverlapFraction = float.NaN;
        public float OverlapSeconds = float.NaN;
        public float BurstSeconds;

        public float Latency => (Unused ? EndAt : UsedAt) - ReadyAt;
        public float ExternalSeconds => Seconds[(int)Cause.NoTarget] + Seconds[(int)Cause.Locked] + Seconds[(int)Cause.Range];
        public float ModuleSeconds => Seconds[(int)Cause.Prereq] + Seconds[(int)Cause.NotProposed] + Seconds[(int)Cause.ModuleDelay] + Seconds[(int)Cause.GcdWindow] + Seconds[(int)Cause.Outprio];

        public static string CsvHeader => "job,scenario,starter,idx,ready_t,used_t,unused,latency,external_s,module_s,no_target_s,locked_s,range_s,prereq_s,not_proposed_s,module_delay_s,gcd_window_s,outprio_s,forced,injected,proposed_at_use,stored_charge,prepull,overlap_s,overlap_frac,state_at_ready,queue_at_ready,state_at_use";

        public string Csv()
        {
            var inv = CultureInfo.InvariantCulture;
            return string.Create(inv, $"{Job},{Scenario},{Starter},{Index},{ReadyAt:f2},{(Unused ? "" : UsedAt.ToString("f2", inv))},{(Unused ? 1 : 0)},{Latency:f2},{ExternalSeconds:f2},{ModuleSeconds:f2},{Seconds[0]:f2},{Seconds[1]:f2},{Seconds[2]:f2},{Seconds[3]:f2},{Seconds[4]:f2},{Seconds[5]:f2},{Seconds[6]:f2},{Seconds[7]:f2},{(Forced ? 1 : 0)},{(Injected ? 1 : 0)},{(ProposedAtUse ? 1 : 0)},{(StoredCharge ? 1 : 0)},{(Prepull ? 1 : 0)},{(float.IsNaN(OverlapSeconds) ? "" : OverlapSeconds.ToString("f2", inv))},{(float.IsNaN(OverlapFraction) ? "" : OverlapFraction.ToString("f3", inv))},{StateAtReady.Replace(',', ' ')},{QueueAtReady.Replace(',', ' ')},{StateAtUse.Replace(',', ' ')}");
        }
    }

    private sealed class State(BurstControl.Starter starter, ActionDefinition definition)
    {
        public readonly BurstControl.Starter Starter = starter;
        public readonly ActionDefinition Definition = definition;
        public bool Ready;
        public Cycle? Open;
        public int Uses;
        public Cause FrameCause;
        public bool FrameForced, FrameInjected, FrameProposed;
        public float LastUsedAt = float.NaN;
    }

    private readonly Class _job;
    private readonly string _scenario;
    private readonly WorldState _world;
    private readonly Actor _player;
    private readonly float _frameStep;
    private readonly Func<string> _stateText;
    private readonly Func<ActionQueue.Entry, bool> _feasible;
    private readonly List<State> _states = [];
    private readonly List<Cycle> _cycles = [];

    // anchor starter of the oracle's burst windows (SearchControl): seconds until its recast is done (0 while ready and unused), when it was last used, how often
    public float AnchorReadyIn { get; private set; } = float.MaxValue;
    public float AnchorLastUse { get; private set; } = float.NaN;
    public int AnchorUses { get; private set; }

    public BurstTracker(Class job, string scenario, WorldState world, Actor player, float frameStep, Func<string> stateText, Func<ActionQueue.Entry, bool> feasible)
    {
        _job = job;
        _scenario = scenario;
        _world = world;
        _player = player;
        _frameStep = frameStep;
        _stateText = stateText;
        _feasible = feasible;
        foreach (var starter in BurstControl.StartersFor(job))
        {
            if (BurstControl.PrimaryOnly && !starter.Primary)
                continue;
            if (BurstControl.OnlyNames != null && !BurstControl.OnlyNames.Contains(starter.Name))
                continue;
            if (ActionDefinitions.Instance[starter.Action] is { } definition)
                _states.Add(new(starter, definition));
        }
    }

    public IReadOnlyList<Cycle> Cycles => _cycles;

    // the policy released this starter in the frame being executed
    public bool ForcedThisFrame(ActionID action)
    {
        foreach (var state in _states)
            if (state.FrameForced && state.Starter.Action == action)
                return true;
        return false;
    }

    // uses that happened before the first tracked frame (the countdown)
    public void SeedPrepull(IEnumerable<(float Time, ActionID Action)> actions)
    {
        foreach (var (time, action) in actions)
        {
            var state = _states.FirstOrDefault(s => s.Starter.Action == action);
            if (state == null)
                continue;
            _cycles.Add(new Cycle { Job = _job.ToString().ToLowerInvariant(), Scenario = _scenario, Starter = state.Starter.Name, Index = state.Uses++, ReadyAt = time, UsedAt = time, Prepull = true, BurstSeconds = state.Starter.BurstSeconds, ProposedAtUse = true });
            state.LastUsedAt = time;
            if (BurstControl.AnchorOf(_job) is { } anchor && anchor.Action == action)
            {
                AnchorLastUse = time;
                ++AnchorUses;
            }
        }
    }

    private static string QueueSummary(AIHints hints)
        => string.Join("|", hints.ActionsToExecute.Entries.OrderByDescending(e => e.Priority).Take(4).Select(e => SearchControl.ActionName(e.Action) + (e.Delay > 0 ? "+" + e.Delay.ToString("f1", CultureInfo.InvariantCulture) : "")));

    // Before the emulator picks its action of this frame: classifies every ready starter, and applies the policy to the queue.
    public void Before(AIHints hints, Actor? target, bool targetAvailable, float time)
    {
        var gcdRemaining = _world.Client.Cooldowns[ActionDefinitions.GCDGroup].Remaining;
        var animLock = _world.Client.AnimationLock;
        var anchor = BurstControl.AnchorOf(_job);
        foreach (var state in _states)
        {
            var def = state.Definition;
            state.FrameForced = state.FrameInjected = state.FrameProposed = false;
            if (!def.IsUnlocked(_world, _player))
            {
                state.Ready = false;
                continue;
            }
            var readyIn = def.ReadyIn(_world.Client.Cooldowns, _world.Client.DutyActions);
            if (anchor != null && state.Starter.Action == anchor.Action)
                AnchorReadyIn = readyIn;
            if (readyIn > 0.001f)
            {
                state.Ready = false;
                continue;
            }
            if (!state.Ready)
            {
                state.Ready = true;
                state.Open = new Cycle
                {
                    Job = _job.ToString().ToLowerInvariant(), Scenario = _scenario, Starter = state.Starter.Name, Index = state.Uses, ReadyAt = time, BurstSeconds = state.Starter.BurstSeconds,
                    StoredCharge = state.Uses > 0 && !float.IsNaN(state.LastUsedAt) && time - state.LastUsedAt <= _frameStep * 1.5f,
                    StateAtReady = _stateText(), QueueAtReady = QueueSummary(hints)
                };
            }

            // where is the starter in the module's queue?
            var queueIndex = hints.ActionsToExecute.Entries.FindIndex(e => e.Action == state.Starter.Action);
            state.FrameProposed = queueIndex >= 0;
            var hostile = def.AllowedTargets.HasFlag(ActionTargets.Hostile) && !def.AllowedTargets.HasFlag(ActionTargets.Self);
            var entry = queueIndex >= 0 ? hints.ActionsToExecute.Entries[queueIndex] : new ActionQueue.Entry(state.Starter.Action, hostile ? target : _player, ActionQueue.Priority.Medium, float.MaxValue, 0, 0, default, null, false, false);

            // classify the frame
            Cause cause;
            var externallyBlocked = false;
            if (!targetAvailable || target == null)
            {
                cause = Cause.NoTarget;
                externallyBlocked = true;
            }
            else if (Irregular.Current?.LockReason(def, _player) != null || (def.RequiresLineOfSight && hostile && target.Visibility == Visibility.Blocked))
            {
                cause = Cause.Locked;
                externallyBlocked = true;
            }
            else if (def.Range > 0 && hostile && _player.DistanceToHitbox(target) > def.Range + 0.001f)
            {
                cause = Cause.Range;
                externallyBlocked = true;
            }
            else if (!_feasible(entry))
            {
                cause = Cause.Prereq;
            }
            else if (queueIndex < 0)
            {
                cause = Cause.NotProposed;
            }
            else if (entry.Delay > 0.05f || animLock > 0.05f)
            {
                cause = Cause.ModuleDelay;
            }
            else if (gcdRemaining > 0.05f && gcdRemaining < def.InstantAnimLock + _frameStep)
            {
                cause = Cause.GcdWindow;
            }
            else
            {
                cause = Cause.Outprio;
            }
            state.FrameCause = cause;

            // the variant policy
            if (BurstControl.Policy != BurstControl.PolicyKind.Default && !externallyBlocked && cause != Cause.Prereq && (state.Uses > 0 || BurstControl.PinOpener))
            {
                if (queueIndex >= 0 || BurstControl.Policy == BurstControl.PolicyKind.Inject)
                {
                    var pinned = new ActionQueue.Entry(entry.Action, entry.Target, BurstControl.PinPriority, entry.Expire, 0, entry.CastTime, entry.TargetPos, entry.FacingAngle, entry.Manual, entry.Force);
                    if (queueIndex >= 0)
                        hints.ActionsToExecute.Entries[queueIndex] = pinned;
                    else
                        hints.ActionsToExecute.Entries.Add(pinned);
                    state.FrameForced = true;
                    state.FrameInjected = queueIndex < 0;
                    if (queueIndex < 0)
                        ++BurstControl.InjectedFrames;
                    else
                        ++BurstControl.ForcedFrames;
                }
                else
                {
                    ++BurstControl.NotProposedFrames; // forced policy: the module did not propose it, nothing to release
                }
            }
        }
    }

    // After the emulator executed (or did not): books the frame on the open cycles and closes the ones that went out.
    public void After(IReadOnlyList<ActionID> newActions, float time)
    {
        var anchor = BurstControl.AnchorOf(_job);
        foreach (var state in _states)
        {
            if (!state.Ready || state.Open == null)
                continue;
            var cycle = state.Open;
            if (newActions.Contains(state.Starter.Action))
            {
                cycle.UsedAt = time;
                cycle.Forced = state.FrameForced;
                cycle.Injected = state.FrameInjected;
                cycle.ProposedAtUse = state.FrameProposed;
                cycle.StateAtUse = _stateText();
                _cycles.Add(cycle);
                state.Open = null;
                state.Ready = false;
                ++state.Uses;
                state.LastUsedAt = time;
                if (anchor != null && state.Starter.Action == anchor.Action)
                {
                    AnchorLastUse = time;
                    ++AnchorUses;
                }
            }
            else
            {
                cycle.Seconds[(int)state.FrameCause] += _frameStep;
            }
        }
    }

    // End of the fight: cycles still waiting are censored at the end, party overlap is computed, rows are published.
    public void Finish(float endTime, IReadOnlyList<(float Start, float End)> partyWindows, bool hasParty)
    {
        BurstControl.LastAnchorUses = AnchorUses;
        foreach (var state in _states)
        {
            if (state.Open != null)
            {
                state.Open.Unused = true;
                state.Open.EndAt = endTime;
                _cycles.Add(state.Open);
                state.Open = null;
            }
        }
        foreach (var cycle in _cycles)
        {
            if (hasParty && !cycle.Unused)
            {
                var burstStart = cycle.UsedAt;
                var burstEnd = cycle.UsedAt + cycle.BurstSeconds;
                var overlap = 0f;
                foreach (var (start, end) in partyWindows)
                    overlap += Math.Max(0f, Math.Min(burstEnd, end) - Math.Max(burstStart, start));
                cycle.OverlapSeconds = overlap;
                cycle.OverlapFraction = cycle.BurstSeconds > 0 ? Math.Min(1f, overlap / Math.Min(cycle.BurstSeconds, 20f)) : 0f;
            }
            if (BurstControl.CollectRows)
            {
                BurstControl.Rows.Add(cycle);
                BurstControl.WriteCsv(cycle.Csv());
            }
        }
    }

    // summary.csv columns appended when metrics are on (primary starters / all starters, the opener excluded)
    public const string CsvHeader = "burst_p_n,burst_p_used,burst_p_lat,burst_p_mod,burst_p_ext,burst_p_gt2,burst_p_aligned,burst_p_overlap,burst_a_n,burst_a_used,burst_a_lat,burst_a_mod,burst_a_ext,burst_a_gt2,burst_a_aligned,burst_a_overlap,burst_forced,burst_injected";

    public string SummaryCsv()
    {
        string Part(Func<Cycle, bool> filter)
        {
            var rows = _cycles.Where(c => c.Index > 0 && filter(c)).ToList();
            var used = rows.Where(c => !c.Unused).ToList();
            return string.Create(CultureInfo.InvariantCulture, $"{rows.Count},{used.Count},{used.Sum(c => c.Latency):f2},{used.Sum(c => c.ModuleSeconds):f2},{used.Sum(c => c.ExternalSeconds):f2},{used.Count(c => c.ModuleSeconds > 2f)},{used.Count(c => !float.IsNaN(c.OverlapFraction) && c.OverlapFraction >= 0.5f)},{used.Where(c => !float.IsNaN(c.OverlapFraction)).Sum(c => c.OverlapFraction):f3}");
        }
        var primary = _states.Where(s => s.Starter.Primary).Select(s => s.Starter.Name).ToHashSet();
        return string.Create(CultureInfo.InvariantCulture, $"{Part(c => primary.Contains(c.Starter))},{Part(_ => true)},{_cycles.Count(c => c.Forced && !c.Injected)},{_cycles.Count(c => c.Injected)}");
    }
}