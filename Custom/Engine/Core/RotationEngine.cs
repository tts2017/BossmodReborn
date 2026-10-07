using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Diagnostics;

namespace BossMod.Autorotation.Engine;

public struct EngineDecision
{
    public const int Wait = -1;
    public int Skill;        // root move: the skill to press now (GCD or weave oGCD), or Wait
    public float ExecuteAt;  // seconds from now when it becomes executable
    public int NextGcd;      // first GCD of the principal variation (same as Skill when the root move is a GCD)
    public float Value;      // value of the principal variation (potency-equivalent)
    public int Depth;        // deepest completed iteration (GCD slots)
    public int Nodes;
    public bool Reused;      // returned from the cache without searching
    public bool Hysteresis;  // previous choice kept by the switch margin
    public bool Partial;     // search not finished yet (it continues on the next call with the same state)
}

// Facade: upper-tier planning on timeline changes, lower-tier iterative-deepening branch-and-bound search per decision,
// result reuse while the state hash does not change. One instance per job; not thread-safe. No allocations per decision.
public sealed class RotationEngine
{
    public readonly JobDefinition Job;
    public readonly JobAnalysis Analysis;
    public readonly float[] FlatGaugeValue = new float[EngineLimits.MaxGauges]; // per point, for Flat gauges (weights.GaugeValue)
    public readonly UpperPlanner Planner;
    public EngineWeights Weights { get; private set; }

    private readonly EvalContext _ctx = new();
    private readonly LowerSearch _search;

    private ulong _lastKey;
    private EngineDecision _last;
    private bool _hasLast;
    private int _lastTimelineVersion = int.MinValue;
    private float _lastPlanTime = float.NegativeInfinity;
    private int _prevChoice = EngineDecision.Wait;

    public float ReplanInterval = 2.0f;
    public float MaxReuseAge = 1.0f; // seconds a result is reused while only timers change
    // > 0: per-call slice of the search budget; the search then spreads over several calls (frames) and returns its best-so-far meanwhile
    public float FrameBudgetMs = 0;
    // multiplies the total search budget, not the frame slice (the caller raises it for the opener, where every cooldown is up at once
    // and a full search is several times the usual size; the extra is spent when the answer is about to be acted on, see FinishPending)
    public float BudgetScale = 1;
    private bool _pending;
    private ulong _pendingKey;
    private EngineDecision _finished; // the last finished search and the discrete state (key) it was searched for
    private ulong _finishedKey;
    private float _finishedNow;
    private bool _hasFinished;
    private float _lastSearchNow = float.NegativeInfinity;

    // forget the cached result (the next Decide searches even if the state is unchanged)
    public void InvalidateCache() { _hasLast = false; _pending = false; _hasFinished = false; }

    public RotationEngine(JobDefinition job, EngineWeights weights)
    {
        Job = job;
        Analysis = new(job);
        Planner = new(job, Analysis);
        Weights = weights;
        _search = new(this, _ctx);
        ApplyWeights();
    }

    // diagnostics: plays a fixed line from the state (each skill at its earliest time) and logs immediate values and the leaf breakdown
    public float ExplainLine(in EngineState state, in EngineTimeline timeline, float now, ReadOnlySpan<int> skills, Action<string> log)
    {
        if (timeline.Version != _lastTimelineVersion || now - _lastPlanTime >= ReplanInterval)
        {
            Planner.Plan(state, PlanTimeline(state, timeline), Weights);
            _lastTimelineVersion = timeline.Version;
            _lastPlanTime = now;
        }
        return _search.ExplainLine(state, timeline, now - _lastPlanTime, skills, log);
    }

    // diagnostics: value of each root move in the last search (NaN = not searched)
    public float LastRootValue(int skill) => _search.RootValue(skill);

    public void SetWeights(EngineWeights weights)
    {
        Weights = weights;
        ApplyWeights();
        Reset();
    }

    public void Reset()
    {
        _pending = false;
        _hasLast = false;
        _hasFinished = false;
        _lastTimelineVersion = int.MinValue;
        _lastPlanTime = float.NegativeInfinity;
        _prevChoice = EngineDecision.Wait;
    }

    private void ApplyWeights()
    {
        for (var g = 0; g < Job.Gauges.Length; ++g)
            FlatGaugeValue[g] = Job.Gauges[g].Flat && Weights.GaugeValue.TryGetValue(Job.Gauges[g].Name, out var fv) ? fv : 0;
        for (var g = 0; g < Job.Gauges.Length; ++g)
            Analysis.GaugeUnitValue[g] = Analysis.GaugeUnitValueBase[g] + (Weights.GaugeValue.TryGetValue(Job.Gauges[g].Name, out var gv) ? gv : 0);
        for (var c = 0; c < Job.Cooldowns.Length; ++c)
            Analysis.CdUnitValue[c] = Analysis.CdUnitValueBase[c] + (Weights.CooldownValue.TryGetValue(Job.Cooldowns[c].Name, out var cv) ? cv : 0) + Weights.UnlockScale * Analysis.CdUnlockValue[c];
        _ctx.FillerPps = Analysis.FillerPps * Weights.FillerScale;
        for (var g = 0; g < Job.Gauges.Length; ++g)
            _ctx.GaugeWastePerPoint[g] = Analysis.GaugeUnit[g] > 0 ? Analysis.GaugeUnitValue[g] / Analysis.GaugeUnit[g] * Weights.OverCap : 0;
        for (var c = 0; c < Job.Cooldowns.Length; ++c)
            _ctx.CdWastePerSecond[c] = Analysis.CdUnitValue[c] / Job.Cooldowns[c].Recast * Weights.OverCap;
        for (var i = 0; i < Job.Statuses.Length; ++i)
            _ctx.StatusValuePerSecond[i] = Analysis.StatusValuePerSecond[i] + (Weights.StatusValue.TryGetValue(Job.Statuses[i].Name, out var v) ? v : 0);
    }

    // The upper tier also sees the job's own damage buffs ahead (a cooldown skill applying a damage-multiplier status, assumed used
    // when ready and every recast after), so other resources are held for them; the search itself sees those statuses directly.
    private EngineTimeline PlanTimeline(in EngineState s, in EngineTimeline timeline)
    {
        if (Weights.ForecastSelfBuffs <= 0)
            return timeline;
        var tl = timeline;
        foreach (var sk in Job.Skills)
        {
            if (sk.Cooldown < 0)
                continue;
            foreach (var e in sk.Effects)
            {
                if (e.Kind != EffectKind.StatusApply || Job.Statuses[e.Index].DamageMultiplier <= 1)
                    continue;
                var mult = Job.Statuses[e.Index].DamageMultiplier;
                var left = s.StatusLeft[e.Index];
                if (left > 0)
                    tl.AddBuff(0, left, mult);
                var recast = Job.Cooldowns[sk.Cooldown].Recast;
                var start = s.Charges[sk.Cooldown] > 0 ? MathF.Max(0, left) : s.CdReadyIn[sk.Cooldown];
                for (var k = 0; k < 3 && start < 180 && tl.NumBuffs < EngineLimits.MaxWindows; ++k, start += recast)
                    tl.AddBuff(start, start + e.Value, mult);
            }
        }
        return tl;
    }

    // `now`: absolute clock (any monotonic seconds); used to decide when the upper tier is stale.
    public EngineDecision Decide(in EngineState state, in EngineTimeline timeline, float now)
    {
        // reuse while nothing discrete changed (gauges, charges, which statuses are up, combo, targets) and the last search is recent:
        // timers ticking down alone do not trigger a new search more often than every MaxReuseAge seconds
        var key = state.Hash(Job, 1000) ^ ((ulong)(uint)timeline.Version * 0x9E3779B97F4A7C15UL);
        // an unfinished search continues while the state is unchanged (it was started on an earlier call; timers drifting a few frames do not matter)
        if (_pending)
        {
            if (key == _pendingKey)
                return Continue(key, now, 0);
            _pending = false;
        }
        if (_hasLast && key == _lastKey && now - _lastSearchNow < MaxReuseAge && now >= _lastSearchNow)
        {
            var d = _last;
            d.Reused = true;
            d.ExecuteAt = MathF.Max(0, d.ExecuteAt - (now - _lastSearchNow)); // the decision was timed from the search root
            return d;
        }

        var planTicks = 0L;
        if (timeline.Version != _lastTimelineVersion || now - _lastPlanTime >= ReplanInterval)
        {
            var t0 = Stopwatch.GetTimestamp();
            Planner.Plan(state, PlanTimeline(state, timeline), Weights);
            _lastTimelineVersion = timeline.Version;
            _lastPlanTime = now;
            planTicks = Stopwatch.GetTimestamp() - t0;
        }

        _search.Start(state, timeline, now - _lastPlanTime, _prevChoice);
        _lastSearchNow = now;
        return Continue(key, now, planTicks);
    }

    // an unfinished search gets the rest of its total budget now (the caller is about to act on its answer); otherwise the last decision
    public EngineDecision FinishPending(float now)
    {
        if (!_pending)
            return _last;
        return Continue(_pendingKey, now, 0, long.MaxValue);
    }

    // spentTicks: work already done this frame (a replan) counts against the frame budget
    private EngineDecision Continue(ulong key, float now, long spentTicks, long sliceTicks = 0)
    {
        var frameTicks = sliceTicks > 0 ? sliceTicks : FrameBudgetMs > 0 ? Math.Max(1, (long)(FrameBudgetMs * Stopwatch.Frequency / 1000) - spentTicks) : long.MaxValue;
        var finished = _search.Continue(frameTicks, out var decision);
        _pending = !finished;
        _pendingKey = key;
        if (finished)
        {
            _prevChoice = decision.Skill;
            _finished = decision;
            _finishedKey = key;
            _finishedNow = _lastSearchNow;
            _hasFinished = true;
        }
        else if (_hasFinished && key == _finishedKey && now >= _finishedNow)
        {
            // a refresh of a finished search (only timers changed since) keeps acting on the finished answer until it completes
            var d = _finished;
            d.Reused = true;
            d.ExecuteAt = MathF.Max(0, d.ExecuteAt - (now - _finishedNow));
            return d;
        }
        _last = decision;
        _lastKey = key;
        _hasLast = true;
        decision.ExecuteAt = MathF.Max(0, decision.ExecuteAt - (now - _lastSearchNow)); // a search resumed on a later frame was timed from its root
        return decision;
    }
}

internal sealed class LowerSearch
{
    private const int MaxPly = 40;
    private const int MoveSlots = EngineLimits.MaxSkills + 1;
    private const byte WaitMove = 0xFE;
    private const int TTBits = 16;
    private const int MaxDeepOgcds = 1; // below the root only the best one weave candidate (by immediate value) is searched
    private const int MaxDeepGcds = 3;  // below the root only the best three GCD candidates (previous best move first, then immediate value)
    private const int MaxRootGcds = 4;  // at the root: every legal weave, the best four GCD candidates

    private struct TTEntry
    {
        public ulong Key;
        public float Value;
        public int Generation;
        public byte BestMove;
        public bool Exact;
    }

    private readonly RotationEngine _engine;
    private readonly JobDefinition _job;
    private readonly EvalContext _ctx;
    private readonly TTEntry[] _tt = new TTEntry[1 << TTBits];
    private readonly byte[] _moves = new byte[MaxPly * MoveSlots];
    private readonly float[] _order = new float[MaxPly * MoveSlots];
    private readonly EngineState[] _children = new EngineState[MaxPly * MoveSlots];
    private readonly float[] _imm = new float[MaxPly * MoveSlots];
    private readonly int[] _nextGcds = new int[MaxPly * MoveSlots];
    private readonly byte[] _perm = new byte[MaxPly * MoveSlots];
    private readonly byte[] _pv = new byte[MaxPly * MaxPly]; // triangular principal-variation table
    private readonly int[] _pvLen = new int[MaxPly];
    private readonly byte[] _rootPv = new byte[MaxPly];
    private int _rootPvLen;
    private readonly float[] _rootValues = new float[MoveSlots];
    private readonly bool[] _rootSeen = new bool[MoveSlots];
    private readonly float[] _doneValues = new float[MoveSlots];
    private readonly bool[] _doneSeen = new bool[MoveSlots];
    private EngineState _root;
    private int _nextDepth;
    private int _prevChoiceForRun;
    private EngineDecision _best;
    private long _spentTicks;
    private readonly Stopwatch _clock = new();
    private int _generation;

    private EngineTimeline _tl;
    private float _planOffset;
    private float _horizon;
    private float _rootBest;
    private int _nodes;
    private bool _aborted;
    private int _abortedDepth; // iteration depth an earlier slice stopped in (0: none); its finished root moves are kept and skipped on resume
    private bool _resuming;
    private long _budgetTicks;
    private long _sliceBudget;
    private int _nodeFloor;
    private int _sliceNodeEnd;
    private float _maxSlotValue;
    private float _maxComboBonus;
    private CycleModel? _cycle;
    private bool _returnedBound;
    private float _maxLeafExtra; // per-decision constant part of the pruning bound (resources, statuses, combo)

    public LowerSearch(RotationEngine engine, EvalContext ctx)
    {
        _engine = engine;
        _job = engine.Job;
        _ctx = ctx;
    }

    public float ExplainLine(in EngineState root, in EngineTimeline tl, float planOffset, ReadOnlySpan<int> skills, Action<string> log)
    {
        _tl = tl;
        _planOffset = planOffset;
        _horizon = MathF.Max(root.GcdReadyAt, root.AnimLockAt) + _engine.Weights.HorizonGcds * _job.BaseGcd;
        var s = root;
        var total = 0f;
        foreach (var idx in skills)
        {
            var skill = _job.Skills[idx];
            var t = skill.IsGcd ? MathF.Max(s.GcdReadyAt, s.AnimLockAt) : s.AnimLockAt;
            var waste = Simulator.Advance(_job, ref s, t - s.Time, _ctx);
            var legal = Simulator.IsLegal(_job, s, _tl, skill);
            var imm = Simulator.Execute(_job, ref s, _tl, skill, _ctx) - waste;
            total += imm;
            log($"  {s.Time,5:f1} {skill.Name,-20} imm {imm,7:f0} legal {legal}");
        }
        var tEff = MathF.Max(s.Time, s.GcdReadyAt);
        var fill = (_horizon - tEff) * _ctx.FillerPps;
        var res = LeafResources(s);
        var st = LeafStatuses(s);
        var leaf = Leaf(s);
        var planner = _engine.Planner;
        var tl2 = tEff + _planOffset;
        var parts = new List<string>();
        for (var g = 0; g < _job.Gauges.Length; ++g)
            parts.Add($"{_job.Gauges[g].Name}={s.Gauges[g]}x{planner.LeafLambda(UpperPlanner.GaugeResource(g), tl2, s.Gauges[g]):f1}");
        for (var c = 0; c < _job.Cooldowns.Length; ++c)
            parts.Add($"{_job.Cooldowns[c].Name}={s.Charges[c]}+{s.CdReadyIn[c]:f0}s");
        log($"  leaf {leaf:f0} = fill {fill:f0} + resources {res:f0} + statuses {st:f0} + combo/pull {leaf - fill - res - st:f0}; total {total + leaf:f0}; {string.Join(" ", parts)}");
        return total + leaf;
    }

    public float RootValue(int skill) => _doneSeen[skill] ? _doneValues[skill] : float.NaN;

    // one-shot search within the whole budget
    public EngineDecision Run(in EngineState root, in EngineTimeline tl, float planOffset, int prevChoice)
    {
        Start(root, tl, planOffset, prevChoice);
        Continue(long.MaxValue, out var d);
        return d;
    }

    // Prepares a new search from `root`. The iterative deepening then runs in Continue, possibly over several calls:
    // the transposition table and the completed depths survive between calls, so each call picks up where the last stopped.
    public void Start(in EngineState root, in EngineTimeline tl, float planOffset, int prevChoice)
    {
        _tl = tl;
        _planOffset = planOffset;
        var w = _engine.Weights;
        _horizon = MathF.Max(root.GcdReadyAt, root.AnimLockAt) + w.HorizonGcds * _job.BaseGcd;
        _maxSlotValue = (MaxImmediate(true, root.Targets) + w.BoundOgcdsPerSlot * MaxImmediate(false, root.Targets)) * tl.MaxBuffMultiplier() * MaxStatusMultiplier();
        _maxComboBonus = MathF.Max(0, _engine.Analysis.MaxComboChainValue);
        var maxMult = tl.MaxBuffMultiplier();
        var maxRes = 0f;
        for (var r = 0; r < UpperPlanner.MaxResources; ++r)
            maxRes += _engine.Planner.MaxLeafValue(r, maxMult);
        var maxStatus = 0f;
        for (var i = 0; i < _job.Statuses.Length; ++i)
            maxStatus += _ctx.StatusValuePerSecond[i] * _job.Statuses[i].MaxDuration * maxMult;
        var maxFlat = 0f;
        for (var g = 0; g < _job.Gauges.Length; ++g)
            maxFlat += MathF.Max(0, _engine.FlatGaugeValue[g]) * _job.Gauges[g].Max;
        _maxLeafExtra = maxRes * MathF.Max(1, MathF.Max(w.LambdaScale, w.CooldownLambdaScale)) + maxStatus * MathF.Max(1, w.StatusRemainder) + MathF.Max(0, w.Combo) * _maxComboBonus + 1;
        _maxLeafExtra += maxFlat;
        _cycle = _engine.Analysis.CycleFor(root.Targets);
        if (_cycle != null)
            _maxLeafExtra += MathF.Max(0, w.CycleScale) * _cycle.MaxValue;
        ++_generation;
        _nodes = 0;
        _spentTicks = 0;
        _root = root;
        _nextDepth = 1;
        _abortedDepth = 0;
        _prevChoiceForRun = prevChoice;
        _best = new EngineDecision { Skill = EngineDecision.Wait, NextGcd = EngineDecision.Wait, Value = float.MinValue };
        _rootPvLen = 0;
        Array.Clear(_doneSeen);
    }

    // Runs iterations until this call's slice or the total budget is used, or the horizon depth is done (returns true).
    // Depth 1 always completes. `result` is the best line of the deepest completed iteration.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool Continue(long sliceTicks, out EngineDecision result)
    {
        var w = _engine.Weights;
        var totalTicks = (long)(w.BudgetMs * _engine.BudgetScale * Stopwatch.Frequency / 1000);
        var maxDepth = Math.Max(1, w.HorizonGcds);
        // the slice is a hard stop (a frame); the total budget only stops a search that has done at least MinNodes nodes, so when it stops
        // depends on the work done, not on the clock, unless the search is that large
        var totalLeft = Math.Max(0, totalTicks - _spentTicks);
        _nodeFloor = w.MinNodes;
        _sliceNodeEnd = w.SliceNodes > 0 && sliceTicks != long.MaxValue ? _nodes + w.SliceNodes : int.MaxValue;
        _clock.Restart();
        while (_nextDepth <= maxDepth)
        {
            _sliceBudget = _nextDepth == 1 || _sliceNodeEnd != int.MaxValue ? long.MaxValue : sliceTicks;
            _budgetTicks = _nextDepth == 1 ? long.MaxValue : totalLeft;
            _resuming = _abortedDepth == _nextDepth;
            if (!_resuming)
            {
                Array.Clear(_rootSeen);
                _rootBest = float.MinValue;
            }
            _aborted = false;
            var value = Search(ref _root, _nextDepth, 0, 0, -1, out var move);
            if (_aborted)
            {
                _abortedDepth = _nextDepth;
                break;
            }
            _abortedDepth = 0;
            _best.Skill = move == WaitMove ? EngineDecision.Wait : move;
            _best.Value = value;
            _best.Depth = _nextDepth;
            _rootPvLen = _pvLen[0];
            Array.Copy(_pv, 0, _rootPv, 0, _rootPvLen);
            Array.Copy(_rootSeen, _doneSeen, _rootSeen.Length);
            Array.Copy(_rootValues, _doneValues, _rootValues.Length);
            ++_nextDepth;
        }
        _spentTicks += _clock.ElapsedTicks;
        var finished = _nextDepth > maxDepth || _spentTicks >= totalTicks && _nodes >= _nodeFloor;

        var best = _best;
        // weaves of one window: among near-equal oGCD roots press the one defined first (e.g. Arcane Circle before Gluttony), so a
        // caller that asks one oGCD at a time still gets the definition order the search assumes for a window
        if (best.Skill >= 0 && !_job.Skills[best.Skill].IsGcd)
            for (var j = 0; j < best.Skill; ++j)
                if (_doneSeen[j] && !_job.Skills[j].IsGcd && _doneValues[j] >= best.Value - MathF.Max(1, w.SwitchMargin))
                {
                    best.Skill = j;
                    best.Value = _doneValues[j];
                    break;
                }
        // hysteresis: keep the previous choice if it is still available and nearly as good
        var prev = _prevChoiceForRun;
        if (prev >= 0 && prev != best.Skill && _doneSeen[prev] && _doneValues[prev] >= best.Value - w.SwitchMargin)
        {
            best.Skill = prev;
            best.Value = _doneValues[prev];
            best.Hysteresis = true;
        }
        if (!best.Hysteresis && _rootPvLen > 0 && _rootPv[0] == best.Skill)
            PrincipalVariationFromTable(_root, ref best);
        // the table line can stop early (a transposition-table hit returns no line below it): walk the table instead
        if (best.NextGcd < 0 && best.Skill >= 0)
            FillPrincipalVariation(_root, ref best);
        best.Nodes = _nodes;
        best.Partial = !finished;
        result = best;
        return finished;
    }

    private float MaxImmediate(bool gcd, int targets)
    {
        var m = 0f;
        foreach (var s in _job.Skills)
        {
            if (s.IsGcd != gcd)
                continue;
            var p = MathF.Max(s.Potency, s.AoePotency * targets);
            foreach (var c in s.PotencyIf)
                p = MathF.Max(p, c.Potency);
            if (s.AoeExtraPotency > 0 && s.Potency > 0)
                p += s.AoeExtraPotency * (p / s.Potency) * Math.Max(0, targets - 1);
            if (s.DotStatus >= 0)
                p += s.DotPps * _job.Statuses[s.DotStatus].MaxDuration * (s.DotAoe ? Math.Max(1, targets) : 1);
            if (s.Weaponskill)
                foreach (var st in _job.Statuses)
                    p += MathF.Max(st.ShadowPotency, st.ShadowAoePotency * targets);
            m = MathF.Max(m, p + s.PartyValue);
        }
        return m;
    }

    private float MaxStatusMultiplier()
    {
        var m = 1f;
        foreach (var s in _job.Statuses)
            m *= MathF.Max(1, s.DamageMultiplier);
        return m;
    }

    private void PrincipalVariationFromTable(in EngineState root, ref EngineDecision d)
    {
        var skill = _job.Skills[d.Skill];
        d.ExecuteAt = MathF.Max(0, (skill.IsGcd ? MathF.Max(root.GcdReadyAt, root.AnimLockAt) : root.AnimLockAt) - root.Time);
        d.NextGcd = EngineDecision.Wait;
        for (var i = 0; i < _rootPvLen; ++i)
        {
            var m = _rootPv[i];
            if (m == WaitMove)
                return;
            if (_job.Skills[m].IsGcd)
            {
                d.NextGcd = m;
                return;
            }
        }
    }

    private void FillPrincipalVariation(in EngineState root, ref EngineDecision d)
    {
        var s = root;
        var move = d.Skill;
        for (var i = 0; i < 6; ++i)
        {
            if (move == EngineDecision.Wait)
            {
                d.NextGcd = EngineDecision.Wait;
                if (i == 0)
                    d.ExecuteAt = 0;
                return;
            }
            var skill = _job.Skills[move];
            var t = skill.IsGcd ? MathF.Max(s.GcdReadyAt, s.AnimLockAt) : s.AnimLockAt;
            if (i == 0)
                d.ExecuteAt = MathF.Max(0, t - root.Time);
            if (skill.IsGcd)
            {
                d.NextGcd = move;
                return;
            }
            Simulator.Advance(_job, ref s, t - s.Time, _ctx);
            Simulator.Execute(_job, ref s, _tl, skill, _ctx);
            var next = ProbeBestMove(s, d.Depth, move);
            if (next == WaitMove)
            {
                d.NextGcd = EngineDecision.Wait;
                return;
            }
            move = next;
        }
        d.NextGcd = EngineDecision.Wait;
    }

    private byte ProbeBestMove(in EngineState s, int maxDepth, int prevOgcd)
    {
        var h = s.Hash(_job);
        for (var d = maxDepth; d >= 1; --d)
        {
            var key = h ^ ((ulong)d * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(prevOgcd + 1) * 0x165667B19E3779F9UL);
            ref var e = ref _tt[(int)(key & ((1 << TTBits) - 1))];
            if (e.Generation == _generation && e.Key == key)
                return e.BestMove;
        }
        return WaitMove;
    }

    // The legal moves worth simulating below the root: the transposition-table move, the MaxDeepGcds best GCDs and the MaxDeepOgcds best
    // weaves by Simulator.QuickValue (bit = skill index). anyGcd: whether any GCD is legal at all.
    private readonly float[] _pickKey = new float[MoveSlots];
    private readonly byte[] _pickMove = new byte[MoveSlots];
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ulong PickCandidates(in EngineState atGcd, in EngineState atOgcd, float tOgcd, float gcdReadyAt, int prevOgcd, byte ttMove, int keep, int maxGcds, int maxOgcds, out bool anyGcd)
    {
        anyGcd = false;
        ulong picked = 0;
        var multGcd = Simulator.DamageMultiplier(_job, atGcd, _tl);
        var multOgcd = Simulator.DamageMultiplier(_job, atOgcd, _tl);
        var activeGcd = Simulator.ActiveStatusMask(_job, atGcd);
        var activeOgcd = Simulator.ActiveStatusMask(_job, atOgcd);
        var clearGcd = Simulator.WindowsClear(_job, atGcd, _tl);
        var clearOgcd = Simulator.WindowsClear(_job, atOgcd, _tl);
        // one pass: GCD candidates fill the key arrays from the front, weave candidates from the back
        var nG = 0;
        var nO = 0;
        var last = MoveSlots - 1;
        foreach (var skill in _job.Skills)
        {
            var gcd = skill.IsGcd;
            if (!gcd && (tOgcd + skill.AnimationLock + _job.Latency > gcdReadyAt + 0.01f || skill.Index <= prevOgcd))
                continue;
            ref readonly var at = ref gcd ? ref atGcd : ref atOgcd;
            if (!Simulator.IsLegal(_job, at, _tl, skill, gcd ? activeGcd : activeOgcd, gcd ? clearGcd : clearOgcd))
                continue;
            anyGcd |= gcd;
            if (skill.Index == ttMove || skill.Index == keep)
            {
                picked |= 1UL << skill.Index;
                continue;
            }
            var k = gcd ? nG++ : last - nO++;
            _pickKey[k] = Simulator.QuickValue(_job, at, _tl, skill, gcd ? multGcd : multOgcd);
            _pickMove[k] = (byte)skill.Index;
        }
        for (var t = Math.Min(nG, maxGcds); t > 0; --t)
        {
            var bi = 0;
            for (var j = 1; j < nG; ++j)
                if (_pickKey[j] > _pickKey[bi])
                    bi = j;
            picked |= 1UL << _pickMove[bi];
            _pickKey[bi] = float.MinValue;
        }
        for (var t = Math.Min(nO, maxOgcds); t > 0; --t)
        {
            var bi = last;
            for (var j = last - 1; j > last - nO; --j)
                if (_pickKey[j] > _pickKey[bi])
                    bi = j;
            picked |= 1UL << _pickMove[bi];
            _pickKey[bi] = float.MinValue;
        }
        return picked;
    }

    // returns the best future value from s with `gcdsLeft` GCD slots to go
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float Search(ref EngineState s, int gcdsLeft, int ply, float acc, int prevOgcd, out byte bestMove)
    {
        bestMove = WaitMove;
        _pvLen[ply] = 0;
        if (gcdsLeft <= 0 || ply >= MaxPly - 1)
        {
            var leaf = Leaf(s);
            if (acc + leaf > _rootBest)
                _rootBest = acc + leaf;
            return leaf;
        }
        if ((++_nodes & 7) == 0 && (_nodes >= _sliceNodeEnd && _nextDepth > 1 || _clock.ElapsedTicks > _sliceBudget || _clock.ElapsedTicks > _budgetTicks && _nodes >= _nodeFloor))
            _aborted = true;
        if (_aborted)
            return Leaf(s);

        var hash = s.Hash(_job) ^ ((ulong)gcdsLeft * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(prevOgcd + 1) * 0x165667B19E3779F9UL); // the weave-order restriction changes the subtree
        ref var tt = ref _tt[(int)(hash & ((1 << TTBits) - 1))];
        var ttMove = WaitMove;
        if (tt.Generation == _generation && tt.Key == hash)
        {
            if (tt.Exact && ply > 0)
            {
                bestMove = tt.BestMove;
                if (acc + tt.Value > _rootBest)
                    _rootBest = acc + tt.Value;
                return tt.Value;
            }
            ttMove = tt.BestMove;
        }

        // generate legal moves; the resulting child states and immediate values are kept for the loop below
        var baseIdx = ply * MoveSlots;
        var count = 0;
        var tGcd = MathF.Max(s.GcdReadyAt, s.AnimLockAt);
        var tOgcd = s.AnimLockAt;
        var anyGcd = false;
        var atGcd = s;
        var wasteGcd = Simulator.Advance(_job, ref atGcd, tGcd - atGcd.Time, _ctx);
        var atOgcd = s;
        var wasteOgcd = Simulator.Advance(_job, ref atOgcd, tOgcd - atOgcd.Time, _ctx);
        // below the root only the best few GCDs / weaves are searched: pick them by a cheap estimate of their immediate value first and
        // simulate only those (simulating every legal move was most of the node cost)
        // the root keeps every weave and the best few GCDs (plus the previous choice, for the hysteresis): a GCD that is not among them
        // by immediate value (an AoE version on one target, a combo break) does not get a subtree
        var picked = ply == 0
            ? PickCandidates(atGcd, atOgcd, tOgcd, s.GcdReadyAt, prevOgcd, ttMove, _prevChoiceForRun, MaxRootGcds, MoveSlots, out anyGcd)
            : PickCandidates(atGcd, atOgcd, tOgcd, s.GcdReadyAt, prevOgcd, ttMove, -1, MaxDeepGcds, MaxDeepOgcds, out anyGcd);
        foreach (var skill in _job.Skills)
        {
            if ((picked & (1UL << skill.Index)) == 0)
                continue;
            var k = baseIdx + count;
            ref var c = ref _children[k];
            c = skill.IsGcd ? atGcd : atOgcd;
            _imm[k] = Simulator.Execute(_job, ref c, _tl, skill, _ctx) - (skill.IsGcd ? wasteGcd : wasteOgcd);
            _nextGcds[k] = skill.IsGcd ? gcdsLeft - 1 : gcdsLeft;
            _moves[k] = (byte)skill.Index;
            _order[k] = skill.Index == ttMove ? float.MaxValue : _imm[k];
            _perm[k] = (byte)count;
            ++count;
        }
        if (!anyGcd)
        {
            var k = baseIdx + count;
            ref var c = ref _children[k];
            c = s;
            var target = _tl.InDowntime(tGcd) ? _tl.DowntimeEnd(tGcd) : tGcd + 0.5f;
            _imm[k] = -Simulator.Advance(_job, ref c, MathF.Max(target, c.Time + 0.1f) - c.Time, _ctx);
            c.GcdReadyAt = MathF.Max(c.GcdReadyAt, c.Time);
            c.AnimLockAt = MathF.Max(c.AnimLockAt, c.Time);
            _nextGcds[k] = gcdsLeft - 1;
            _moves[k] = WaitMove;
            _order[k] = ttMove == WaitMove ? float.MaxValue : float.MinValue;
            _perm[k] = (byte)count;
            ++count;
        }
        // insertion sort of the permutation by order desc (count is small)
        for (var i = 1; i < count; ++i)
        {
            var p = _perm[baseIdx + i];
            var o = _order[baseIdx + p];
            var j = i - 1;
            while (j >= 0 && _order[baseIdx + _perm[baseIdx + j]] < o)
            {
                _perm[baseIdx + j + 1] = _perm[baseIdx + j];
                --j;
            }
            _perm[baseIdx + j + 1] = p;
        }

        var best = float.MinValue;
        var pruned = false;
        var prunedBound = float.MinValue;
        var ogcdsTaken = 0;
        var gcdsTaken = 0;
        for (var i = 0; i < count; ++i)
        {
            var k = baseIdx + _perm[baseIdx + i];
            var move = _moves[k];
            if (ply >= 1 && move != WaitMove && (_job.Skills[move].IsGcd ? ++gcdsTaken > MaxDeepGcds : ++ogcdsTaken > MaxDeepOgcds))
                continue;
            var imm = _imm[k];
            var nextGcds = _nextGcds[k];
            ref var child = ref _children[k];

            var bound = imm + UpperBound(child, nextGcds);
            if (ply > 0 && acc + bound <= _rootBest)
            {
                pruned = true;
                prunedBound = MathF.Max(prunedBound, bound);
                continue;
            }

            if (ply == 0 && _resuming && move != WaitMove && _rootSeen[move])
            {
                // finished by an earlier slice of this iteration (exact value; its line is recovered from the table if it is best)
                if (_rootValues[move] > best)
                {
                    best = _rootValues[move];
                    bestMove = move;
                    _pv[0] = move;
                    _pvLen[0] = 1;
                }
                continue;
            }
            _returnedBound = false;
            var f = imm + Search(ref child, nextGcds, ply + 1, acc + imm, move != WaitMove && !_job.Skills[move].IsGcd ? move : -1, out _);
            var childBounded = _returnedBound;
            if (childBounded)
                pruned = true;
            if (ply == 0 && move != WaitMove && !_aborted && !childBounded)
            {
                _rootValues[move] = f;
                _rootSeen[move] = true;
            }
            if (f > best)
            {
                best = f;
                bestMove = move;
                var row = ply * MaxPly;
                _pv[row] = move;
                var childLen = Math.Min(_pvLen[ply + 1], MaxPly - 1 - ply);
                Array.Copy(_pv, (ply + 1) * MaxPly, _pv, row + 1, childLen);
                _pvLen[ply] = 1 + childLen;
            }
            if (_aborted)
                break;
        }
        if (count == 0)
            best = Leaf(s);
        else if (best == float.MinValue)
        {
            // every child was cut: the true value is at most the pruning threshold; report that (never better than the line already found)
            best = MathF.Min(prunedBound, _rootBest - acc);
            _returnedBound = true;
        }

        if (!_aborted)
        {
            tt.Key = hash;
            tt.Generation = _generation;
            tt.Value = best;
            tt.BestMove = bestMove;
            tt.Exact = !pruned;
        }
        return best;
    }

    // optimistic bound on the future value: every remaining slot at the best skill value plus the best leaf
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float UpperBound(in EngineState s, int gcdsLeft)
    {
        var w = _engine.Weights;
        var fill = MathF.Max(0, _horizon - MathF.Max(s.Time, s.GcdReadyAt)) * _ctx.FillerPps;
        return gcdsLeft * _maxSlotValue + fill + _maxLeafExtra;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float Leaf(in EngineState s)
    {
        var w = _engine.Weights;
        var tEff = MathF.Max(s.Time, s.GcdReadyAt);
        var value = (_horizon - tEff) * _ctx.FillerPps; // shared horizon: under-simulated time earns filler, overshoot pays it back
        value += LeafResources(s);
        value += LeafStatuses(s);
        for (var g = 0; g < _job.Gauges.Length; ++g)
            value += _engine.FlatGaugeValue[g] * s.Gauges[g];
        if (_cycle != null)
            value += w.CycleScale * _cycle.Value(s);
        if (s.ComboSkill != EngineLimits.NoCombo && s.ComboLeft > 0)
            value += w.Combo * _engine.Analysis.ComboChainValue[s.ComboSkill];
        if (w.TargetPull != 0)
        {
            var seg = _engine.Planner.SegmentAt(tEff + _planOffset);
            for (var g = 0; g < _job.Gauges.Length; ++g)
                value -= w.TargetPull * MathF.Abs(s.Gauges[g] - _engine.Planner.GetTarget(seg, UpperPlanner.GaugeResource(g))) / MathF.Max(1, _job.Gauges[g].Max) * _engine.Analysis.FillerPerGcd;
        }
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float LeafResources(in EngineState s)
    {
        var planner = _engine.Planner;
        var t = MathF.Max(s.Time, s.GcdReadyAt) + _planOffset;
        var value = 0f;
        var w = _engine.Weights;
        var cdScale = w.CooldownLambdaScale >= 0 ? w.CooldownLambdaScale : w.LambdaScale;
        // (a zero scale skips its loop: the planner lookups are a good part of a leaf)
        for (var g = 0; g < _job.Gauges.Length && w.LambdaScale != 0; ++g)
            if (s.Gauges[g] > 0)
                value += _job.Gauges[g].Flat ? 0 : planner.LeafLambda(UpperPlanner.GaugeResource(g), t, s.Gauges[g]) * s.Gauges[g] * w.LambdaScale;
        for (var c = 0; c < _job.Cooldowns.Length && cdScale != 0; ++c)
        {
            var cd = _job.Cooldowns[c];
            var holding = s.Charges[c] + (s.Charges[c] < cd.MaxCharges ? 1 - s.CdReadyIn[c] / cd.Recast : 0);
            if (holding > 0)
                value += planner.LeafLambda(UpperPlanner.CdResource(c), t, holding) * holding * cdScale;
        }
        return value;
    }

    // remaining value of active statuses: damage multipliers keep paying out after the horizon
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float LeafStatuses(in EngineState s)
    {
        var w = _engine.Weights;
        if (w.StatusRemainder == 0)
            return 0;
        var value = 0f;
        var tEff = MathF.Max(s.Time, s.GcdReadyAt);
        var fightLeft = _tl.FightEndIn - tEff;
        for (var i = 0; i < _job.Statuses.Length; ++i)
        {
            if (s.StatusLeft[i] <= 0 || _ctx.StatusValuePerSecond[i] == 0)
                continue;
            var left = MathF.Min(s.StatusLeft[i] - (tEff - s.Time), fightLeft);
            if (left > 0)
                value += _ctx.StatusValuePerSecond[i] * left * w.StatusRemainder * _tl.AverageBuffMultiplier(tEff, tEff + left);
        }
        return value;
    }
}
