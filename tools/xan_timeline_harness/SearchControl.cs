using System;
using System.Collections.Generic;
using System.Linq;
using BossMod;

namespace XanTimelineHarness;

// Decision hook for the offline search oracle (oracle-search command). Every job emulator calls Decide() right before it executes the
// action the real rotation module picked (after the readiness, irregular-refusal and client-reject checks) and FilterSuppressed()
// right before it asks the queue for the best entry. Process-wide state like ClientReject and Irregular: with Active false every hook
// returns at once, so ordinary runs stay byte-identical.
//
// A "decision" is one such execution that falls inside the controllable window [WindowFrom, WindowTo] (seconds from the fight start),
// numbered in order within one run. A genome assigns each decision index a gene:
//    0      the module's own pick (the whole run reproduces the plain module run when every gene is 0)
//    k > 0  the k-th alternative: the next distinct candidates of the module's own queue that are ready right now and pass the
//           emulator's CanExecute, the irregular refusal and the client-reject check (the module's pick removed, FindBest again)
//    Hold   do not execute now; the picked action is suppressed until the next GCD is used (or HoldCap seconds pass), which is how
//           the oracle delays an oGCD past the GCD or a GCD pick in favour of the module's next candidate
// Outside the window the module is untouched. The oracle only ever chooses among what the module proposes.
internal static class SearchControl
{
    public const int Hold = -1;
    public const int MaxAlternatives = 6;
    public static float HoldCap = 2.5f;

    public static bool Active;
    public static float WindowFrom, WindowTo;
    // window to use when the fight has no episode (the control search); set by the oracle driver, null otherwise
    public static (float From, float To)? FixedWindow;

    // Knowledge levels of the oracle (oracle-search --oracle-know). K0 numbers the decisions of the whole window with one counter (the genome may
    // depend on everything, including how long the episode lasts). K1 and K2 number them per phase instead, so a gene means the same thing in
    // fights whose episodes have different lengths (see OracleKnowledge.cs):
    //   pre     decisions before the episode start, counted from the window start             (K1 only: K2 does not see the start coming)
    //   during  decisions from the episode start (K2: start + ReactionDelay), counted from there
    //   post    decisions from the episode end on, counted from the end (the end is observed once it has happened)
    // Decisions outside the capped gene ranges, K2 decisions before the reaction and everything outside the window run as the module chose.
    public enum Knowledge { K0, K1, K2 }
    public static Knowledge Level = Knowledge.K0;
    public static (float Start, float End)? FixedEpisode; // phase boundaries for a fight without an episode (the control search)
    public static float ReactionDelay = 0.75f;
    public const int PreGenes = 16, DuringGenes = 40, PostGenes = 48;
    private static float _epStart, _epEnd;
    private static int _preIndex, _duringIndex, _postIndex;
    // Burst oracle (oracle-search --oracle-mode burst, BurstOracle.cs): the controllable decisions are the ones of the burst windows around the anchor starter's
    // recast and of the episodes of the (full) irregular schedule, numbered per window so a gene means the same thing in fights whose episodes last differently:
    //   burst window k  decisions from (starter ready in <= PreBurst) until PostBurst after its use, counted from the window start      (the opener, k = 0, is the module's)
    //   post episode e  decisions from the episode end for PostEpisode seconds                                                          (the end is observed once it has happened)
    //   during episode e decisions from the episode start (K2: start + ReactionDelay), counted from the start
    //   before episode e (K1 only) the PreEpisode seconds before the start
    // An episode phase takes precedence over a burst window it overlaps. With BurstControl's policy active the starters are not decisions at all.
    public static bool BurstMode;
    public static int BurstKnow = 1;                 // 1 = K1 (episode starts are known, ends are not), 2 = K2 (starts are noticed ReactionDelay late)
    public static float PreBurst = 30f, PostBurst = 25f, PostEpisode = 25f, PreEpisode = 3f;
    public static bool IncludeOpener;
    public const int BurstCap = 56, PostCap = 30, DuringCap = 10, PreEpCap = 4;
    private const int BurstBase = 0, BurstStride = 56, BurstSlots = 10;
    private const int PostBase = 560, PostStride = 30, PostSlots = 20;
    private const int DuringBase = 1160, DuringStride = 10, DuringSlots = 20;
    private const int PreEpBase = 1360, PreEpStride = 4, PreEpSlots = 20;
    private static readonly Dictionary<int, int> _counters = [];

    // Rules (--burst-rule "<phase>|<module pick>|<HOLD or action name>;..."): a fixed, causal deviation applied to EVERY matching decision of a normal run, instead of a
    // searched genome. phase is one of b_pre, b_ready, b_burst, post, during or * (any controllable window); the module pick and the replacement are action names as
    // the deviation logs print them (Spinning_Edge); HOLD suppresses the pick until the next GCD, a name picks that candidate of the module's own queue when it is ready.
    // This is how a pattern the oracle found is turned into a rule and priced on the whole matrix. Needs BurstMode (set together with the rules).
    public sealed record BurstRule(string Phase, string Picked, string Action);
    public static List<BurstRule> Rules = [];
    public static bool RuleMode => Rules.Count > 0;

    private static int RuleGene(string phase, ActionID picked)
    {
        var name = ActionName(picked);
        foreach (var rule in Rules)
        {
            if ((rule.Phase != "*" && rule.Phase != phase) || rule.Picked != name)
                continue;
            if (rule.Action == "HOLD")
                return Hold;
            for (var k = 0; k < _alts.Count; ++k)
                if (ActionName(_alts[k].Action) == rule.Action)
                    return k + 1;
        }
        return 0;
    }

    private static bool Take(int baseIndex, int cap, int key, out int index)
    {
        if (RuleMode)
        {
            index = 0; // rules do not number decisions
            return true;
        }
        _counters.TryGetValue(key, out var count);
        _counters[key] = count + 1;
        index = count < cap ? baseIndex + count : -1;
        return index >= 0;
    }

    private static bool ClassifyBurst(float now, out int index, out string phase, out float phaseTime)
    {
        index = -1;
        phase = "";
        phaseTime = 0f;
        if (Irregular.Current is { } driver)
        {
            var episodes = driver.Episodes;
            var count = Math.Min(episodes.Count, DuringSlots);
            for (var k = 0; k < count; ++k)
            {
                var e = episodes[k];
                if (now >= e.Start && now < e.End)
                {
                    phase = "during";
                    phaseTime = now - e.Start;
                    if (BurstKnow >= 2 && phaseTime < ReactionDelay)
                        return false;
                    return Take(DuringBase + k * DuringStride, DuringCap, 1000 + k, out index);
                }
            }
            for (var k = 0; k < count; ++k)
            {
                var e = episodes[k];
                if (now >= e.End && now < e.End + PostEpisode)
                {
                    phase = "post";
                    phaseTime = now - e.End;
                    return Take(PostBase + k * PostStride, PostCap, 2000 + k, out index);
                }
            }
            if (BurstKnow < 2)
            {
                for (var k = 0; k < count; ++k)
                {
                    var e = episodes[k];
                    if (now >= e.Start - PreEpisode && now < e.Start)
                    {
                        phase = "pre_ep";
                        phaseTime = now - e.Start;
                        return Take(PreEpBase + k * PreEpStride, PreEpCap, 3000 + k, out index);
                    }
                }
            }
        }
        if (BurstControl.Current is { } tracker)
        {
            var window = -1;
            if (tracker.AnchorReadyIn <= PreBurst)
            {
                window = tracker.AnchorUses;
                phase = tracker.AnchorReadyIn > 0.001f ? "b_pre" : "b_ready";
                phaseTime = -tracker.AnchorReadyIn;
            }
            else if (!float.IsNaN(tracker.AnchorLastUse) && now - tracker.AnchorLastUse <= PostBurst)
            {
                window = tracker.AnchorUses - 1;
                phase = "b_burst";
                phaseTime = now - tracker.AnchorLastUse;
            }
            if (window >= 0 && window < BurstSlots && (window > 0 || IncludeOpener))
                return Take(BurstBase + window * BurstStride, BurstCap, 4000 + window, out index);
        }
        phase = "";
        return false;
    }

    public static int[] Genome = [];
    // when true each decision keeps its candidate names and the emulator state; the cheap per-index figures are always kept
    public static bool Detail;

    public sealed class Decision
    {
        public int Index;
        public float Time;
        public ActionID Picked;           // what the module wanted
        public ActionID Applied;          // what ran (default for Hold)
        public int Gene;                  // gene value as requested
        public int Alternatives;          // number of alternatives available at this point
        public string Names = "";         // Detail only: "picked | alt1 | alt2"
        public string State = "";         // Detail only: emulator state
        public float GcdLeft;
        public string Phase = "";         // burst oracle: which window the decision belongs to
        public float PhaseTime;           // burst oracle: seconds since the phase anchor (episode start / end, starter use; negative before the starter's recast is done)
    }

    // per-run output
    public static readonly List<Decision> Decisions = [];
    public static int HoldsApplied, AlternativesApplied, GenesClipped;

    private static int _index;
    private static readonly HashSet<ActionID> _suppressed = [];
    private static float _suppressedAt;
    private static readonly ActionQueue _scratch = new();
    private static readonly List<ActionQueue.Entry> _alts = [];
    private static bool _windowSet;

    // Called by the runner once per fight after the irregular driver exists.
    public static void BeginRun(IrregularDriver? driver, float marginBefore, float window)
    {
        _index = 0;
        _suppressed.Clear();
        Decisions.Clear();
        HoldsApplied = AlternativesApplied = GenesClipped = 0;
        _windowSet = false;
        _preIndex = _duringIndex = _postIndex = 0;
        _counters.Clear();
        if (BurstMode)
        {
            _windowSet = true;
            return;
        }
        if (Level != Knowledge.K0)
        {
            // phase boundaries come from the episode of the fight, or from the hypothetical one of the control fight
            if (driver is { Episodes.Count: > 0 })
            {
                _epStart = driver.Episodes[0].Start;
                _epEnd = driver.Episodes[0].End;
                _windowSet = true;
            }
            else if (FixedEpisode is { } fixedEpisode)
            {
                _epStart = fixedEpisode.Start;
                _epEnd = fixedEpisode.End;
                _windowSet = true;
            }
            WindowFrom = _epStart - marginBefore;
            WindowTo = _epEnd + window;
        }
        else if (driver is { Episodes.Count: > 0 })
        {
            var episode = driver.Episodes[0];
            WindowFrom = episode.Start - marginBefore;
            WindowTo = episode.End + window;
            _windowSet = true;
        }
        else if (FixedWindow is { } fixedWindow)
        {
            WindowFrom = fixedWindow.From;
            WindowTo = fixedWindow.To;
            _windowSet = true;
        }
    }

    public static string ActionName(ActionID action)
    {
        if (action.ID == 0)
            return "-";
        if (action.Type == ActionType.Spell)
        {
            var name = Service.LuminaRow<Lumina.Excel.Sheets.Action>(action.ID)?.Name.ToString();
            return string.IsNullOrEmpty(name) ? action.ToString() : name.Replace(' ', '_');
        }
        return action.ToString().Replace(' ', '_');
    }

    public static void FilterSuppressed(List<ActionQueue.Entry> entries, WorldState world)
    {
        if (!Active || _suppressed.Count == 0)
            return;
        var now = (float)(world.CurrentTime - ClientReject.BaseTime).TotalSeconds;
        if (now - _suppressedAt > HoldCap)
        {
            _suppressed.Clear();
            return;
        }
        entries.RemoveAll(entry => _suppressed.Contains(entry.Action));
    }

    // Returns the entry to execute, or null for Hold. feasible is the emulator's full CanExecute (job rules included).
    public static ActionQueue.Entry? Decide(ActionQueue queue, ActionQueue.Entry picked, WorldState world, Actor player, AIHints hints, float frameStep,
        Func<ActionQueue.Entry, bool> feasible, Func<string>? describe)
    {
        if (!Active)
            return picked;
        // burst-on-recast policy (BurstControl): a starter the policy released this frame is neither a decision nor open to alternatives or Hold
        if (BurstControl.Pinning && BurstControl.Current is { } burst && burst.ForcedThisFrame(picked.Action))
            return picked;
        var now = (float)(world.CurrentTime - ClientReject.BaseTime).TotalSeconds;
        var chosen = picked;
        var burstPhase = "";
        var burstPhaseTime = 0f;
        var burstIndex = -1;
        if (BurstMode ? ClassifyBurst(now, out burstIndex, out burstPhase, out burstPhaseTime) : _windowSet && now >= WindowFrom && now <= WindowTo)
        {
            var index = BurstMode ? burstIndex : -1;
            if (BurstMode)
            {
                // the index came from the burst windows
            }
            else if (Level == Knowledge.K0)
            {
                index = _index++;
            }
            else if (now < _epStart)
            {
                if (Level == Knowledge.K1 && _preIndex++ is var preIndex && preIndex < PreGenes)
                    index = preIndex;
            }
            else if (now < _epEnd)
            {
                if (now >= _epStart + (Level == Knowledge.K2 ? ReactionDelay : 0f) && _duringIndex++ is var duringIndex && duringIndex < DuringGenes)
                    index = PreGenes + duringIndex;
            }
            else if (_postIndex++ is var postIndex && postIndex < PostGenes)
            {
                index = PreGenes + DuringGenes + postIndex;
            }
            if (index < 0)
            {
                // outside the controllable part of the window at this knowledge level: the module runs as it is
                if (ActionDefinitions.Instance[chosen.Action] is { IsGCD: true })
                    _suppressed.Clear();
                return chosen;
            }
            CollectAlternatives(queue, picked, world, player, hints, frameStep, feasible);
            var gene = RuleMode ? RuleGene(burstPhase, picked.Action) : index < Genome.Length ? Genome[index] : 0;
            var decision = new Decision { Index = index, Time = now, Picked = picked.Action, Gene = gene, Alternatives = _alts.Count, GcdLeft = MathF.Max(0f, world.Client.Cooldowns[ActionDefinitions.GCDGroup].Remaining), Phase = burstPhase, PhaseTime = burstPhaseTime };
            if (Detail)
            {
                decision.Names = string.Join(" | ", _alts.Select(a => ActionName(a.Action)).Prepend(ActionName(picked.Action)));
                decision.State = describe?.Invoke() ?? "";
            }
            Decisions.Add(decision);
            if (gene == Hold)
            {
                ++HoldsApplied;
                _suppressed.Add(picked.Action);
                _suppressedAt = now;
                return null;
            }
            if (gene > 0)
            {
                if (gene <= _alts.Count)
                {
                    chosen = _alts[gene - 1];
                    ++AlternativesApplied;
                }
                else
                {
                    ++GenesClipped; // this trajectory has fewer alternatives here than the one the gene was found in: the module's own pick runs
                }
            }
            decision.Applied = chosen.Action;
        }
        if (ActionDefinitions.Instance[chosen.Action] is { IsGCD: true })
            _suppressed.Clear();
        return chosen;
    }

    private static void CollectAlternatives(ActionQueue queue, ActionQueue.Entry picked, WorldState world, Actor player, AIHints hints, float frameStep, Func<ActionQueue.Entry, bool> feasible)
    {
        _alts.Clear();
        var work = _scratch.Entries;
        work.Clear();
        work.AddRange(queue.Entries);
        RemoveSame(work, picked);
        for (var guard = 0; guard < 4 * MaxAlternatives && work.Count > 0 && _alts.Count < MaxAlternatives; ++guard)
        {
            var entry = _scratch.FindBest(world, player, world.Client.Cooldowns, world.Client.AnimationLock, hints, frameStep, true);
            if (entry.Action.ID == 0)
                break;
            RemoveSame(work, entry);
            var definition = ActionDefinitions.Instance[entry.Action];
            if (definition == null)
                continue;
            // the emulators' own readiness test: not ready when anything is still more than 1 ms away
            if (MathF.Max(entry.Delay, MathF.Max(world.Client.AnimationLock, definition.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions))) > 0.001f)
                continue;
            if (Irregular.Current?.LockReason(definition, player) != null)
                continue;
            if (!feasible(entry))
                continue;
            _alts.Add(entry);
        }
    }

    private static void RemoveSame(List<ActionQueue.Entry> list, in ActionQueue.Entry entry)
    {
        var action = entry.Action;
        var target = entry.Target;
        list.RemoveAll(e => e.Action == action && e.Target == target);
    }
}