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
    private bool _pending;
    private ulong _pendingKey;
    private float _lastSearchNow = float.NegativeInfinity;

    // forget the cached result (the next Decide searches even if the state is unchanged)
    public void InvalidateCache() { _hasLast = false; _pending = false; }

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
            Planner.Plan(state, timeline, Weights);
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
        _lastTimelineVersion = int.MinValue;
        _lastPlanTime = float.NegativeInfinity;
        _prevChoice = EngineDecision.Wait;
    }

    private void ApplyWeights()
    {
        for (var g = 0; g < Job.Gauges.Length; ++g)
            Analysis.GaugeUnitValue[g] = Analysis.GaugeUnitValueBase[g] + (Weights.GaugeValue.TryGetValue(Job.Gauges[g].Name, out var gv) ? gv : 0);
        for (var c = 0; c < Job.Cooldowns.Length; ++c)
            Analysis.CdUnitValue[c] = Analysis.CdUnitValueBase[c] + (Weights.CooldownValue.TryGetValue(Job.Cooldowns[c].Name, out var cv) ? cv : 0);
        _ctx.FillerPps = Analysis.FillerPps * Weights.FillerScale;
        for (var g = 0; g < Job.Gauges.Length; ++g)
            _ctx.GaugeWastePerPoint[g] = Analysis.GaugeUnit[g] > 0 ? Analysis.GaugeUnitValue[g] / Analysis.GaugeUnit[g] * Weights.OverCap : 0;
        for (var c = 0; c < Job.Cooldowns.Length; ++c)
            _ctx.CdWastePerSecond[c] = Analysis.CdUnitValue[c] / Job.Cooldowns[c].Recast * Weights.OverCap;
        for (var i = 0; i < Job.Statuses.Length; ++i)
            _ctx.StatusValuePerSecond[i] = Analysis.StatusValuePerSecond[i] + (Weights.StatusValue.TryGetValue(Job.Statuses[i].Name, out var v) ? v : 0);
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
                return Continue(key);
            _pending = false;
        }
        if (_hasLast && key == _lastKey && now - _lastSearchNow < MaxReuseAge && now >= _lastSearchNow)
        {
            var d = _last;
            d.Reused = true;
            return d;
        }

        if (timeline.Version != _lastTimelineVersion || now - _lastPlanTime >= ReplanInterval)
        {
            Planner.Plan(state, timeline, Weights);
            _lastTimelineVersion = timeline.Version;
            _lastPlanTime = now;
        }

        _search.Start(state, timeline, now - _lastPlanTime, _prevChoice);
        _lastSearchNow = now;
        return Continue(key);
    }

    private EngineDecision Continue(ulong key)
    {
        var frameTicks = FrameBudgetMs > 0 ? (long)(FrameBudgetMs * Stopwatch.Frequency / 1000) : long.MaxValue;
        var finished = _search.Continue(frameTicks, out var decision);
        _pending = !finished;
        _pendingKey = key;
        if (finished)
            _prevChoice = decision.Skill;
        _last = decision;
        _lastKey = key;
        _hasLast = true;
        return decision;
    }
}

internal sealed class LowerSearch
{
    private const int MaxPly = 40;
    private const int MoveSlots = EngineLimits.MaxSkills + 1;
    private const byte WaitMove = 0xFE;
    private const int TTBits = 16;
    private const int MaxDeepOgcds = 1; // below the root only the best two weave candidates (by immediate value) are searched
    private const int MaxDeepGcds = 3;  // below the root only the best three GCD candidates (previous best move first, then immediate value)

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
    private long _budgetTicks;
    private float _maxSlotValue;
    private float _maxComboBonus;
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
        var res = LeafResources(s) * _engine.Weights.LambdaScale;
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
        _maxSlotValue = (MaxImmediate(true, root.Targets) + MaxDeepOgcds * MaxImmediate(false, root.Targets)) * tl.MaxBuffMultiplier() * MaxStatusMultiplier();
        _maxComboBonus = MathF.Max(0, _engine.Analysis.MaxComboChainValue);
        var maxMult = tl.MaxBuffMultiplier();
        var maxRes = 0f;
        for (var r = 0; r < UpperPlanner.MaxResources; ++r)
            maxRes += _engine.Planner.MaxLeafValue(r, maxMult);
        var maxStatus = 0f;
        for (var i = 0; i < _job.Statuses.Length; ++i)
            maxStatus += _ctx.StatusValuePerSecond[i] * _job.Statuses[i].MaxDuration * maxMult;
        _maxLeafExtra = maxRes * MathF.Max(1, w.LambdaScale) + maxStatus * MathF.Max(1, w.StatusRemainder) + MathF.Max(0, w.Combo) * _maxComboBonus + 1;
        ++_generation;
        _nodes = 0;
        _spentTicks = 0;
        _root = root;
        _nextDepth = 1;
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
        var totalTicks = (long)(w.BudgetMs * Stopwatch.Frequency / 1000);
        var maxDepth = Math.Max(1, w.HorizonGcds);
        var allowed = Math.Min(sliceTicks, Math.Max(0, totalTicks - _spentTicks));
        _clock.Restart();
        while (_nextDepth <= maxDepth)
        {
            _budgetTicks = _nextDepth == 1 ? long.MaxValue : allowed;
            Array.Clear(_rootSeen);
            _rootBest = float.MinValue;
            _aborted = false;
            var value = Search(ref _root, _nextDepth, 0, 0, -1, out var move);
            if (_aborted)
                break;
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
        var finished = _nextDepth > maxDepth || _spentTicks >= totalTicks;

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
        if ((++_nodes & 7) == 0 && _clock.ElapsedTicks > _budgetTicks)
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
        foreach (var skill in _job.Skills)
        {
            if (!skill.IsGcd && tOgcd + skill.AnimationLock + _job.Latency > s.GcdReadyAt + 0.01f)
                continue; // would clip the GCD
            if (!skill.IsGcd && skill.Index <= prevOgcd)
                continue; // weaves in one window are searched in index order only (A,B and B,A reach the same state)
            if (!Simulator.IsLegal(_job, skill.IsGcd ? atGcd : atOgcd, _tl, skill))
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
            anyGcd |= skill.IsGcd;
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
        value += LeafResources(s) * w.LambdaScale;
        value += LeafStatuses(s);
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
        for (var g = 0; g < _job.Gauges.Length; ++g)
            if (s.Gauges[g] > 0)
                value += planner.LeafLambda(UpperPlanner.GaugeResource(g), t, s.Gauges[g]) * s.Gauges[g];
        for (var c = 0; c < _job.Cooldowns.Length; ++c)
        {
            var cd = _job.Cooldowns[c];
            var holding = s.Charges[c] + (s.Charges[c] < cd.MaxCharges ? 1 - s.CdReadyIn[c] / cd.Recast : 0);
            if (holding > 0)
                value += planner.LeafLambda(UpperPlanner.CdResource(c), t, holding) * holding;
        }
        return value;
    }

    // remaining value of active statuses: damage multipliers keep paying out after the horizon
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private float LeafStatuses(in EngineState s)
    {
        var w = _engine.Weights;
        var value = 0f;
        var tEff = MathF.Max(s.Time, s.GcdReadyAt);
        var fightLeft = _tl.FightEndIn - tEff;
        for (var i = 0; i < _job.Statuses.Length; ++i)
        {
            if (s.StatusLeft[i] <= 0)
                continue;
            var left = MathF.Min(s.StatusLeft[i] - (tEff - s.Time), fightLeft);
            if (left > 0)
                value += _ctx.StatusValuePerSecond[i] * left * w.StatusRemainder * _tl.AverageBuffMultiplier(tEff, tEff + left);
        }
        return value;
    }
}
