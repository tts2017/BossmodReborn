using System;
using System.Collections.Generic;

namespace BossMod.Autorotation.Engine;

// Long-run value of a job's "cycle state" (e.g. BLM element stance / MP / Umbral Hearts / Paradox / Astral Soul / Firestarter),
// for jobs whose resources trade off over a cycle longer than the search horizon. Built once from the definition:
//  1. states: the job's declared cycle gauges (bucketed) and cycle statuses (present / absent), everything else zero;
//  2. the graph of those states reachable from a cold start using the skills that only depend on the cycle state
//     (no cooldown longer than a few seconds, conditions only on cycle gauges / statuses / targets);
//  3. the best long-run potency rate Rate and the relative value h(s) of every state (average-reward Bellman equation
//     h(s) = max_a [r(s,a) - Rate * t(a) + h(s')], solved by bisection on Rate and relative value iteration).
// The search adds h(leaf) to its leaf value; Rate replaces the measured filler rate (it is the rate any line is compared to).
// Allocation-free lookups; the construction allocates.
public sealed class CycleModel
{
    public readonly float Rate;
    public readonly float MaxValue;
    private readonly JobDefinition _job;
    private readonly int[] _gauges;
    private readonly int[] _steps;
    private readonly int[] _statuses;
    private readonly int[] _cds;
    private readonly float _cdStep;
    private readonly int[] _radix; // bucket count per dimension (gauges, then statuses = 2)
    private readonly float[] _value; // dense over the bucket product; NaN = not reachable
    public readonly int States;
    private int[] _cycleKeys = [];
    public string CycleDescription = ""; // diagnostics: skills along the optimal cycle
    private int _targets;

    // Best value of using `skill` (a skill outside the model that changes the cycle state, e.g. Manafont) at some state of the
    // optimal cycle: immediate value + h(after) - h(before) - the time it takes at the cycle rate. 0 if it never helps.
    public float BestUseGain(SkillDef skill)
    {
        var ctx = new EvalContext { InvalidPenalty = 0 };
        var tl = EngineTimeline.Open();
        var bestGain = 0f;
        foreach (var key in _cycleKeys)
        {
            var s = Decode(key, _targets);
            if (!Simulator.IsLegalIgnoringCooldown(_job, s, tl, skill))
                continue;
            var n = s;
            var r = Simulator.Execute(_job, ref n, tl, skill, ctx);
            var t = skill.IsGcd ? MathF.Max(n.GcdReadyAt, n.AnimLockAt) : 0;
            bestGain = MathF.Max(bestGain, r - Rate * t + Value(n) - Value(s));
        }
        return bestGain;
    }

    public const float OgcdTime = 0.1f;

    public CycleModel(JobDefinition job, int targets)
    {
        _job = job;
        _gauges = job.CycleGauges;
        _steps = job.CycleSteps;
        _statuses = job.CycleStatuses;
        _cds = job.CycleCooldowns;
        _cdStep = job.BaseGcd;
        _radix = new int[_gauges.Length + _statuses.Length + _cds.Length];
        var size = 1;
        for (var i = 0; i < _gauges.Length; ++i)
            size *= _radix[i] = job.Gauges[_gauges[i]].Max / _steps[i] + 1;
        for (var i = 0; i < _statuses.Length; ++i)
            size *= _radix[_gauges.Length + i] = 2;
        // short cooldowns in the state: remaining time in GCD steps (so a 5 s Transpose cannot be used every GCD)
        for (var i = 0; i < _cds.Length; ++i)
            size *= _radix[_gauges.Length + _statuses.Length + i] = (int)MathF.Ceiling(job.Cooldowns[_cds[i]].Recast / _cdStep) + 1;
        if (size > 4_000_000)
            throw new InvalidOperationException("cycle state too large");
        _value = new float[size];
        Array.Fill(_value, float.NaN);

        var skills = new List<SkillDef>();
        foreach (var s in job.Skills)
            if (InModel(s))
                skills.Add(s);

        // breadth-first enumeration of the reachable states and their transitions
        var ctx = new EvalContext { InvalidPenalty = 0 };
        var tl = EngineTimeline.Open();
        var index = new Dictionary<int, int>();
        var keys = new List<int>();
        var edges = new List<(int From, int To, float R, float T)>();
        var edgeSkills = new List<int>();
        var start = Canonical(EngineState.Create(job), targets);
        start.Gauges[_gauges[0]] = (short)job.Gauges[_gauges[0]].Max; // first cycle gauge starts full (MP)
        var startKey = Key(start);
        index[startKey] = 0;
        keys.Add(startKey);
        var queue = new Queue<int>();
        queue.Enqueue(startKey);
        while (queue.Count > 0)
        {
            var key = queue.Dequeue();
            var from = index[key];
            var s = Decode(key, targets);
            foreach (var sk in skills)
            {
                if (!Simulator.IsLegal(job, s, tl, sk))
                    continue;
                var n = s;
                var r = Simulator.Execute(job, ref n, tl, sk, ctx);
                var t = sk.IsGcd ? MathF.Max(n.GcdReadyAt - s.Time, n.AnimLockAt - s.Time) : OgcdTime;
                Simulator.Advance(job, ref n, t, ctx);
                var nk = Key(n);
                if (!index.TryGetValue(nk, out var to))
                {
                    index[nk] = to = keys.Count;
                    keys.Add(nk);
                    queue.Enqueue(nk);
                }
                edges.Add((from, to, r, t));
                edgeSkills.Add(sk.Index);
            }
        }
        States = keys.Count;

        // bisection on the rate: below the optimum the values grow without bound, above it they fall
        var h = new float[States];
        var maxRate = 0f;
        foreach (var e in edges)
            if (e.T > OgcdTime)
                maxRate = MathF.Max(maxRate, e.R / e.T);
        float lo = 0, hi = maxRate;
        var sweeps = 400;
        var g = new Graph(edges, States);
        for (var it = 0; it < 20; ++it)
        {
            var mid = (lo + hi) / 2;
            var growth = Drift(g, h, mid, sweeps);
            if (growth > 0)
                lo = mid;
            else
                hi = mid;
        }
        Rate = lo;
        // relative values at the optimal rate, normalized so the worst reachable state is 0
        Array.Clear(h);
        Relax(g, h, Rate, sweeps * 2, normalize: true);
        var min = float.MaxValue;
        foreach (var v in h)
            min = MathF.Min(min, v);
        for (var i = 0; i < States; ++i)
        {
            _value[keys[i]] = h[i] - min;
            MaxValue = MathF.Max(MaxValue, h[i] - min);
        }

        // states on the optimal cycle: follow the greedy policy from the start for a while
        var best = new int[States];
        Array.Fill(best, -1);
        for (var i = 0; i < g.From.Length; ++i)
        {
            var v = g.R[i] - Rate * g.T[i] + h[g.To[i]];
            var b = best[g.From[i]];
            if (b < 0 || v > g.R[b] - Rate * g.T[b] + h[g.To[b]])
                best[g.From[i]] = i;
        }
        var onCycle = new HashSet<int>();
        var cur = 0;
        for (var step = 0; step < 400 && best[cur] >= 0; ++step)
        {
            if (step >= 100)
            {
                onCycle.Add(keys[cur]);
                if (step < 160)
                    CycleDescription += $"{job.Skills[edgeSkills[best[cur]]].Name}({g.R[best[cur]]:f0}) ";
            }
            cur = g.To[best[cur]];
        }
        _cycleKeys = [.. onCycle];
        _targets = targets;
    }

    // a skill belongs to the cycle model if it only depends on cycle state (and targets) and has no long cooldown
    private bool InModel(SkillDef s)
    {
        // a DoT's value depends on its remaining time, which the model does not track
        if (s.DotStatus >= 0 && Array.IndexOf(_statuses, s.DotStatus) < 0)
            return false;
        if (s.Cooldown >= 0 && Array.IndexOf(_cds, s.Cooldown) < 0)
            return false;
        foreach (var c in s.Conditions)
        {
            switch (c.Kind)
            {
                case ConditionKind.GaugeAtLeast:
                case ConditionKind.GaugeAtMost:
                    if (Array.IndexOf(_gauges, (int)c.Index) < 0)
                        return false;
                    break;
                case ConditionKind.StatusActive:
                case ConditionKind.StacksAtLeast:
                case ConditionKind.StatusLeftAtLeast:
                    if (Array.IndexOf(_statuses, (int)c.Index) < 0)
                        return false;
                    break;
                case ConditionKind.ComboIs:
                    return false;
            }
        }
        return true;
    }

    private sealed class Graph
    {
        public readonly int[] From, To;
        public readonly float[] R, T;
        public readonly float[] Next;
        public Graph(List<(int From, int To, float R, float T)> edges, int states)
        {
            From = new int[edges.Count]; To = new int[edges.Count]; R = new float[edges.Count]; T = new float[edges.Count];
            for (var i = 0; i < edges.Count; ++i)
                (From[i], To[i], R[i], T[i]) = edges[i];
            Next = new float[states];
        }
    }

    private static float Drift(Graph edges, float[] h, float rate, int sweeps)
    {
        Array.Clear(h);
        var mid = 0f;
        for (var k = 0; k < sweeps; ++k)
        {
            Sweep(edges, h, rate);
            if (k == sweeps / 2)
                mid = h[0];
        }
        return h[0] - mid;
    }

    private static void Relax(Graph edges, float[] h, float rate, int sweeps, bool normalize)
    {
        for (var k = 0; k < sweeps; ++k)
        {
            Sweep(edges, h, rate);
            if (normalize)
            {
                var off = h[0];
                for (var i = 0; i < h.Length; ++i)
                    h[i] -= off;
            }
        }
    }

    // one synchronous Bellman sweep: h'(s) = max over edges of r - rate * t + h(s')
    private static void Sweep(Graph g, float[] h, float rate)
    {
        var next = g.Next;
        Array.Fill(next, float.NegativeInfinity);
        var from = g.From; var to = g.To; var r = g.R; var t = g.T;
        for (var i = 0; i < from.Length; ++i)
        {
            var v = r[i] - rate * t[i] + h[to[i]];
            if (v > next[from[i]])
                next[from[i]] = v;
        }
        for (var i = 0; i < h.Length; ++i)
            h[i] = float.IsNegativeInfinity(next[i]) ? h[i] : next[i];
    }

    private EngineState Canonical(EngineState s, int targets)
    {
        var c = EngineState.Create(_job);
        c.Targets = (byte)targets;
        for (var i = 0; i < _gauges.Length; ++i)
            c.Gauges[_gauges[i]] = (short)(Math.Clamp((int)MathF.Round((float)s.Gauges[_gauges[i]] / _steps[i]), 0, _radix[i] - 1) * _steps[i]);
        foreach (var st in _statuses)
        {
            if (s.StatusLeft[st] > 0)
            {
                c.StatusLeft[st] = _job.Statuses[st].MaxDuration;
                c.StatusStacks[st] = 1;
            }
        }
        // no cooldown limits inside the model
        return c;
    }

    private int Key(in EngineState s)
    {
        var key = 0;
        for (var i = 0; i < _gauges.Length; ++i)
            key = key * _radix[i] + Math.Clamp((int)MathF.Round((float)s.Gauges[_gauges[i]] / _steps[i]), 0, _radix[i] - 1);
        for (var i = 0; i < _statuses.Length; ++i)
            key = key * 2 + (s.StatusLeft[_statuses[i]] > 0 ? 1 : 0);
        for (var i = 0; i < _cds.Length; ++i)
        {
            var r = _radix[_gauges.Length + _statuses.Length + i];
            var c = _cds[i];
            key = key * r + (s.Charges[c] > 0 ? 0 : Math.Clamp((int)MathF.Ceiling(s.CdReadyIn[c] / _cdStep - 0.001f), 0, r - 1));
        }
        return key;
    }

    private EngineState Decode(int key, int targets)
    {
        var s = EngineState.Create(_job);
        s.Targets = (byte)targets;
        for (var i = _cds.Length - 1; i >= 0; --i)
        {
            var r = _radix[_gauges.Length + _statuses.Length + i];
            var bucket = key % r;
            key /= r;
            if (bucket > 0)
            {
                s.Charges[_cds[i]] = 0;
                s.CdReadyIn[_cds[i]] = bucket * _cdStep;
            }
        }
        for (var i = _statuses.Length - 1; i >= 0; --i)
        {
            if (key % 2 == 1)
            {
                s.StatusLeft[_statuses[i]] = _job.Statuses[_statuses[i]].MaxDuration;
                s.StatusStacks[_statuses[i]] = 1;
            }
            key /= 2;
        }
        for (var i = _gauges.Length - 1; i >= 0; --i)
        {
            s.Gauges[_gauges[i]] = (short)(key % _radix[i] * _steps[i]);
            key /= _radix[i];
        }
        return s;
    }

    // h of the state's cycle part; a state the model never reaches (e.g. MP between buckets after natural regen) takes the
    // nearest reachable state with less of the first cycle gauge
    public float Value(in EngineState s)
    {
        var key = Key(s);
        var v = _value[key];
        if (!float.IsNaN(v))
            return v;
        var stride = 1;
        for (var i = 1; i < _radix.Length; ++i)
            stride *= _radix[i];
        var bucket = key / stride;
        for (var b = bucket - 1; b >= 0; --b)
        {
            key -= stride;
            v = _value[key];
            if (!float.IsNaN(v))
                return v;
        }
        return 0;
    }
}
