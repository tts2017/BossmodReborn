using System;
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

    public RotationEngine(JobDefinition job, EngineWeights weights)
    {
        Job = job;
        Analysis = new(job);
        Planner = new(job, Analysis);
        Weights = weights;
        _search = new(this, _ctx);
        ApplyWeights();
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
        _hasLast = false;
        _lastTimelineVersion = int.MinValue;
        _lastPlanTime = float.NegativeInfinity;
        _prevChoice = EngineDecision.Wait;
    }

    private void ApplyWeights()
    {
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
        var key = state.Hash(Job) ^ ((ulong)(uint)timeline.Version * 0x9E3779B97F4A7C15UL);
        if (_hasLast && key == _lastKey)
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

        var decision = _search.Run(state, timeline, now - _lastPlanTime, _prevChoice);
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
    private readonly float[] _rootValues = new float[MoveSlots];
    private readonly bool[] _rootSeen = new bool[MoveSlots];
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

    public LowerSearch(RotationEngine engine, EvalContext ctx)
    {
        _engine = engine;
        _job = engine.Job;
        _ctx = ctx;
    }

    public float RootValue(int skill) => _rootSeen[skill] ? _rootValues[skill] : float.NaN;

    public EngineDecision Run(in EngineState root, in EngineTimeline tl, float planOffset, int prevChoice)
    {
        _tl = tl;
        _planOffset = planOffset;
        var w = _engine.Weights;
        _horizon = MathF.Max(root.GcdReadyAt, root.AnimLockAt) + w.HorizonGcds * _job.BaseGcd;
        _maxSlotValue = _engine.Analysis.MaxSkillValue * 3 * tl.MaxBuffMultiplier() * MaxStatusMultiplier() * Math.Max(1, (int)root.Targets);
        _maxComboBonus = 0;
        foreach (var sk in _job.Skills)
            _maxComboBonus = MathF.Max(_maxComboBonus, sk.ComboBonus);
        _budgetTicks = (long)(w.BudgetMs * Stopwatch.Frequency / 1000);
        ++_generation;
        _nodes = 0;
        _clock.Restart();

        var best = new EngineDecision { Skill = EngineDecision.Wait, NextGcd = EngineDecision.Wait, Value = float.MinValue };
        var rootState = root;
        for (var depth = 1; depth <= Math.Max(1, w.HorizonGcds); ++depth)
        {
            Array.Clear(_rootSeen);
            _rootBest = float.MinValue;
            _aborted = false;
            var value = Search(ref rootState, depth, 0, 0, out var move);
            if (_aborted && depth > 1)
                break;
            best.Skill = move == WaitMove ? EngineDecision.Wait : move;
            best.Value = value;
            best.Depth = depth;
            if (_aborted)
                break;
        }

        // hysteresis: keep the previous choice if it is still available and nearly as good
        if (prevChoice >= 0 && prevChoice != best.Skill && _rootSeen[prevChoice] && _rootValues[prevChoice] >= best.Value - w.SwitchMargin)
        {
            best.Skill = prevChoice;
            best.Value = _rootValues[prevChoice];
            best.Hysteresis = true;
        }

        FillPrincipalVariation(root, ref best);
        best.Nodes = _nodes;
        return best;
    }

    private float MaxStatusMultiplier()
    {
        var m = 1f;
        foreach (var s in _job.Statuses)
            m *= MathF.Max(1, s.DamageMultiplier);
        return m;
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
            var next = ProbeBestMove(s, d.Depth);
            if (next == WaitMove)
            {
                d.NextGcd = EngineDecision.Wait;
                return;
            }
            move = next;
        }
        d.NextGcd = EngineDecision.Wait;
    }

    private byte ProbeBestMove(in EngineState s, int maxDepth)
    {
        var h = s.Hash(_job);
        for (var d = maxDepth; d >= 1; --d)
        {
            var key = h ^ ((ulong)d * 0xC2B2AE3D27D4EB4FUL);
            ref var e = ref _tt[(int)(key & ((1 << TTBits) - 1))];
            if (e.Generation == _generation && e.Key == key)
                return e.BestMove;
        }
        return WaitMove;
    }

    // returns the best future value from s with `gcdsLeft` GCD slots to go
    private float Search(ref EngineState s, int gcdsLeft, int ply, float acc, out byte bestMove)
    {
        bestMove = WaitMove;
        if (gcdsLeft <= 0 || ply >= MaxPly - 1)
        {
            var leaf = Leaf(s);
            if (acc + leaf > _rootBest)
                _rootBest = acc + leaf;
            return leaf;
        }
        if ((++_nodes & 255) == 0 && _clock.ElapsedTicks > _budgetTicks)
            _aborted = true;
        if (_aborted)
            return Leaf(s);

        var hash = s.Hash(_job) ^ ((ulong)gcdsLeft * 0xC2B2AE3D27D4EB4FUL);
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

        // generate legal moves with their immediate values
        var baseIdx = ply * MoveSlots;
        var count = 0;
        var tGcd = MathF.Max(s.GcdReadyAt, s.AnimLockAt);
        var tOgcd = s.AnimLockAt;
        var anyGcd = false;
        foreach (var skill in _job.Skills)
        {
            var t = skill.IsGcd ? tGcd : tOgcd;
            if (!skill.IsGcd && t + skill.AnimationLock + _job.Latency > s.GcdReadyAt + 0.01f)
                continue; // would clip the GCD
            var child = s;
            var waste = Simulator.Advance(_job, ref child, t - child.Time, _ctx);
            if (!Simulator.IsLegal(_job, child, _tl, skill))
                continue;
            var imm = Simulator.Execute(_job, ref child, _tl, skill, _ctx) - waste;
            _moves[baseIdx + count] = (byte)skill.Index;
            _order[baseIdx + count] = skill.Index == ttMove ? float.MaxValue : imm;
            ++count;
            anyGcd |= skill.IsGcd;
        }
        if (!anyGcd)
        {
            _moves[baseIdx + count] = WaitMove;
            _order[baseIdx + count] = ttMove == WaitMove ? float.MaxValue : float.MinValue;
            ++count;
        }
        // insertion sort by order desc (count is small)
        for (var i = 1; i < count; ++i)
        {
            var m = _moves[baseIdx + i];
            var o = _order[baseIdx + i];
            var j = i - 1;
            while (j >= 0 && _order[baseIdx + j] < o)
            {
                _moves[baseIdx + j + 1] = _moves[baseIdx + j];
                _order[baseIdx + j + 1] = _order[baseIdx + j];
                --j;
            }
            _moves[baseIdx + j + 1] = m;
            _order[baseIdx + j + 1] = o;
        }

        var best = float.MinValue;
        var pruned = false;
        var prunedBound = float.MinValue;
        for (var i = 0; i < count; ++i)
        {
            var move = _moves[baseIdx + i];
            var child = s;
            float imm;
            int nextGcds;
            if (move == WaitMove)
            {
                var target = _tl.InDowntime(tGcd) ? _tl.DowntimeEnd(tGcd) : tGcd + 0.5f;
                imm = -Simulator.Advance(_job, ref child, MathF.Max(target, child.Time + 0.1f) - child.Time, _ctx);
                child.GcdReadyAt = MathF.Max(child.GcdReadyAt, child.Time);
                child.AnimLockAt = MathF.Max(child.AnimLockAt, child.Time);
                nextGcds = gcdsLeft - 1;
            }
            else
            {
                var skill = _job.Skills[move];
                var t = skill.IsGcd ? tGcd : tOgcd;
                imm = -Simulator.Advance(_job, ref child, t - child.Time, _ctx);
                imm += Simulator.Execute(_job, ref child, _tl, skill, _ctx);
                nextGcds = skill.IsGcd ? gcdsLeft - 1 : gcdsLeft;
            }

            var bound = imm + UpperBound(child, nextGcds);
            if (ply > 0 && acc + bound <= _rootBest)
            {
                pruned = true;
                prunedBound = MathF.Max(prunedBound, bound);
                continue;
            }

            _returnedBound = false;
            var f = imm + Search(ref child, nextGcds, ply + 1, acc + imm, out _);
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
    private float UpperBound(in EngineState s, int gcdsLeft)
    {
        var w = _engine.Weights;
        var fill = MathF.Max(0, _horizon - MathF.Max(s.Time, s.GcdReadyAt)) * _ctx.FillerPps;
        return gcdsLeft * _maxSlotValue + fill + LeafResources(s) * MathF.Max(1, w.LambdaScale) + LeafStatuses(s) * MathF.Max(1, w.StatusRemainder) + MathF.Max(0, w.Combo) * _maxComboBonus + 1;
    }

    private float Leaf(in EngineState s)
    {
        var w = _engine.Weights;
        var tEff = MathF.Max(s.Time, s.GcdReadyAt);
        var value = (_horizon - tEff) * _ctx.FillerPps; // shared horizon: under-simulated time earns filler, overshoot pays it back
        value += LeafResources(s) * w.LambdaScale;
        value += LeafStatuses(s);
        if (s.ComboSkill != EngineLimits.NoCombo && s.ComboLeft > 0)
            value += w.Combo * _job.Skills[s.ComboSkill].ComboBonus;
        if (w.TargetPull != 0)
        {
            var seg = _engine.Planner.SegmentAt(tEff + _planOffset);
            for (var g = 0; g < _job.Gauges.Length; ++g)
                value -= w.TargetPull * MathF.Abs(s.Gauges[g] - _engine.Planner.GetTarget(seg, UpperPlanner.GaugeResource(g))) / MathF.Max(1, _job.Gauges[g].Max) * _engine.Analysis.FillerPerGcd;
        }
        return value;
    }

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
