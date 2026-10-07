using System;

namespace BossMod.Autorotation.Engine;

// Upper tier: one-dimensional dynamic programming per resource over the fight split at buff / downtime / fight-end
// boundaries. The value function V_i(holding) at each segment start is kept, so the lower tier can ask for the shadow
// price lambda of a resource at any leaf time: the slope of "spend optimally in the rest of this segment, then follow V".
// Resources are solved independently; their interactions are left to the lower-tier search. Allocation-free after construction.
public sealed class UpperPlanner
{
    public const int MaxSegments = 24;
    public const float Horizon = 360;
    public const int MaxResources = EngineLimits.MaxGauges + EngineLimits.MaxCooldowns;
    private const int Levels = 65;
    private const int SpendSteps = 32;
    private const float SameSegmentDiscount = 0.97f; // a unit held for later in the same segment is worth slightly less than spending it now

    private readonly JobDefinition _job;
    private readonly JobAnalysis _an;

    public int NumSegments { get; private set; }
    public readonly float[] SegStart = new float[MaxSegments + 1];
    private readonly float[] _segMult = new float[MaxSegments];
    private readonly bool[] _segCanSpend = new bool[MaxSegments];
    private bool _horizonIsFightEnd;
    private readonly float[] _scratchBoundaries = new float[MaxSegments * 4];

    private readonly float[] _v = new float[MaxResources * (MaxSegments + 1) * Levels];
    private readonly ResourceParams[] _params = new ResourceParams[MaxResources];
    private readonly float[] _segMultR = new float[MaxResources * MaxSegments]; // per-resource segment multiplier
    private EngineTimeline _tl;
    private float _burstBias = 1;

    // planned holding (raw points / charges) at each segment start along the optimal path: [segment * MaxResources + resource]
    public readonly float[] Target = new float[MaxSegments * MaxResources];

    private struct ResourceParams
    {
        public bool Active;
        public int Levels;
        public float Step, Cap, GainPerSec, SpendsPerSec, UnitValue, RawPerUnit;
        public float ValueDuration;
        public bool GainsDuringDowntime;
    }

    public UpperPlanner(JobDefinition job, JobAnalysis analysis)
    {
        _job = job;
        _an = analysis;
    }

    public static int GaugeResource(int gauge) => gauge;
    public static int CdResource(int cd) => EngineLimits.MaxGauges + cd;

    public int SegmentAt(float t)
    {
        for (var i = NumSegments - 1; i > 0; --i)
            if (t >= SegStart[i])
                return i;
        return 0;
    }

    public float GetTarget(int segment, int resource) => Target[Math.Min(segment, MaxSegments - 1) * MaxResources + resource];

    public void Plan(in EngineState s, in EngineTimeline tl, EngineWeights w)
    {
        _tl = tl;
        _burstBias = w.BurstBias;
        BuildSegments(tl, w);
        for (var r = 0; r < MaxResources; ++r)
            _params[r].Active = false;
        for (var g = 0; g < _job.Gauges.Length; ++g)
        {
            var unit = _an.GaugeUnit[g];
            if (unit <= 0 || _an.GaugeUnitValue[g] <= 0)
                continue;
            var throughput = _an.GaugeSpentByGcd[g] ? 1 / _job.BaseGcd : 1 / 0.7f;
            Solve(GaugeResource(g), s.Gauges[g] / unit, _job.Gauges[g].Max / unit, _an.GaugeGainPerSecond[g] / unit, _an.GaugeUnitValue[g], throughput, false, unit, 0);
        }
        for (var c = 0; c < _job.Cooldowns.Length; ++c)
        {
            var cd = _job.Cooldowns[c];
            if (_an.CdUnitValue[c] <= 0)
                continue;
            var holding = s.Charges[c] + (s.Charges[c] < cd.MaxCharges ? 1 - s.CdReadyIn[c] / cd.Recast : 0);
            var throughput = _an.CdSpentByGcd[c] ? 1 / _job.BaseGcd : 1 / 0.7f;
            Solve(CdResource(c), holding, cd.MaxCharges, 1 / cd.Recast, _an.CdUnitValue[c], throughput, true, 1, _an.CdValueDuration[c]);
        }
    }

    // Shadow price (value per raw point / charge) of holding `holdingRaw` of a resource at time t.
    public float LeafLambda(int r, float t, float holdingRaw)
    {
        ref readonly var p = ref _params[r];
        if (!p.Active)
            return 0;
        var i = SegmentAt(t);
        if (i >= NumSegments || t >= SegStart[NumSegments])
            return _horizonIsFightEnd ? 0 : p.UnitValue / p.RawPerUnit;
        var remaining = SegStart[i + 1] - t;
        var k = holdingRaw / p.RawPerUnit;
        var lo = MathF.Max(0, k - p.Step);
        var hi = MathF.Min(p.Cap, k + p.Step);
        if (hi <= lo)
            return 0;
        var mult = p.ValueDuration > 0 ? 1 + (_tl.AverageBuffMultiplier(t, t + p.ValueDuration) - 1) * _burstBias : _segMult[i];
        var slope = (ValueFrom(r, p, i, remaining, hi, mult) - ValueFrom(r, p, i, remaining, lo, mult)) / (hi - lo);
        return MathF.Max(0, slope) / p.RawPerUnit;
    }

    // best value of holding k units with `remaining` seconds of segment i left: spend some now-ish, carry the rest into V_{i+1}
    private float ValueFrom(int r, in ResourceParams p, int i, float remaining, float k, float mult)
    {
        var gain = (p.GainsDuringDowntime || _segCanSpend[i]) ? p.GainPerSec * remaining : 0;
        var maxSpend = _segCanSpend[i] ? p.SpendsPerSec * remaining : 0;
        var have = k + gain;
        var spendCap = MathF.Min(have, MathF.Floor(maxSpend + 1e-4f));
        var best = float.MinValue;
        var steps = Math.Max(1, Math.Min(SpendSteps, (int)MathF.Ceiling(spendCap * 4)));
        for (var si = 0; si <= steps; ++si)
        {
            var spend = spendCap * si / steps;
            var left = MathF.Min(have - spend, p.Cap);
            var val = spend * p.UnitValue * mult + SameSegmentDiscount * Interp(r, p, i + 1, left);
            if (val > best)
                best = val;
        }
        return best;
    }

    private void BuildSegments(in EngineTimeline tl, EngineWeights w)
    {
        var end = MathF.Min(Horizon, tl.FightEndIn);
        _horizonIsFightEnd = tl.FightEndIn <= Horizon;
        var n = 0;
        _scratchBoundaries[n++] = 0;
        for (var i = 0; i < tl.NumBuffs; ++i)
        {
            _scratchBoundaries[n++] = tl.Buffs[i].Start;
            _scratchBoundaries[n++] = tl.Buffs[i].End;
        }
        for (var i = 0; i < tl.NumDowntime; ++i)
        {
            _scratchBoundaries[n++] = tl.Downtime[i].Start;
            _scratchBoundaries[n++] = tl.Downtime[i].End;
        }
        Array.Sort(_scratchBoundaries, 0, n);
        var count = 0;
        for (var i = 0; i < n && count < MaxSegments; ++i)
        {
            var b = _scratchBoundaries[i];
            if (b < 0 || b >= end || count > 0 && b - SegStart[count - 1] < 0.5f)
                continue;
            SegStart[count++] = b;
        }
        if (count == 0)
            count = 1;
        SegStart[0] = 0;
        SegStart[count] = MathF.Max(end, 0.5f);
        NumSegments = count;
        for (var i = 0; i < count; ++i)
        {
            var mid = (SegStart[i] + SegStart[i + 1]) * 0.5f;
            _segCanSpend[i] = !tl.InDowntime(mid);
            var m = tl.AverageBuffMultiplier(SegStart[i], SegStart[i + 1]);
            _segMult[i] = 1 + (m - 1) * w.BurstBias;
        }
    }

    // holdings in units (1 unit = smallest spend)
    private void Solve(int r, float holding, float cap, float gainPerSec, float unitValue, float spendsPerSec, bool gainsDuringDowntime, float rawPerUnit, float valueDuration)
    {
        for (var i = 0; i < NumSegments; ++i)
            _segMultR[r * MaxSegments + i] = valueDuration > 0 ? 1 + (_tl.AverageBuffMultiplier(SegStart[i], SegStart[i] + valueDuration) - 1) * _burstBias : _segMult[i];
        var levels = Math.Min(Levels, (int)MathF.Ceiling(cap * 8) + 1);
        ref var p = ref _params[r];
        p = new() { Active = true, Levels = levels, Step = cap / (levels - 1), Cap = cap, GainPerSec = gainPerSec, SpendsPerSec = spendsPerSec, UnitValue = unitValue, RawPerUnit = rawPerUnit, GainsDuringDowntime = gainsDuringDowntime, ValueDuration = valueDuration };
        var n = NumSegments;
        var baseIdx = r * (MaxSegments + 1) * Levels;

        for (var k = 0; k < levels; ++k)
            _v[baseIdx + n * Levels + k] = _horizonIsFightEnd ? 0 : k * p.Step * unitValue;

        for (var i = n - 1; i >= 0; --i)
        {
            var len = SegStart[i + 1] - SegStart[i];
            var gain = (gainsDuringDowntime || _segCanSpend[i]) ? gainPerSec * len : 0;
            var maxSpend = _segCanSpend[i] ? spendsPerSec * len : 0;
            for (var k = 0; k < levels; ++k)
                _v[baseIdx + i * Levels + k] = BestSpend(r, p, i, k * p.Step + gain, maxSpend, out _);
        }

        // planned holdings along the optimal path
        var cur = MathF.Min(holding, cap);
        for (var i = 0; i < n; ++i)
        {
            Target[i * MaxResources + r] = cur * rawPerUnit;
            var len = SegStart[i + 1] - SegStart[i];
            var gain = (gainsDuringDowntime || _segCanSpend[i]) ? gainPerSec * len : 0;
            var maxSpend = _segCanSpend[i] ? spendsPerSec * len : 0;
            BestSpend(r, p, i, cur + gain, maxSpend, out var left);
            cur = MathF.Max(0, left);
        }
        for (var i = n; i < MaxSegments; ++i)
            Target[i * MaxResources + r] = Target[(n - 1) * MaxResources + r];
    }

    // whole units spent within segment i (overflow above the cap is lost)
    private float BestSpend(int r, in ResourceParams p, int i, float have, float maxSpend, out float bestLeft)
    {
        var spendCap = MathF.Min(MathF.Floor(have + 1e-4f), MathF.Floor(maxSpend + 1e-4f));
        var steps = Math.Min(SpendSteps, (int)spendCap);
        var best = float.MinValue;
        bestLeft = MathF.Min(have, p.Cap);
        for (var si = 0; si <= steps; ++si)
        {
            var spend = steps == 0 ? 0 : spendCap * si / steps;
            var left = MathF.Min(have - spend, p.Cap);
            var val = spend * p.UnitValue * _segMultR[r * MaxSegments + i] + Interp(r, p, i + 1, left);
            if (val > best)
            {
                best = val;
                bestLeft = left;
            }
        }
        return best;
    }

    private float Interp(int r, in ResourceParams p, int segment, float holding)
    {
        var baseIdx = r * (MaxSegments + 1) * Levels + segment * Levels;
        var x = holding / p.Step;
        var k = (int)x;
        if (k >= p.Levels - 1)
            return _v[baseIdx + p.Levels - 1];
        if (k < 0)
            return _v[baseIdx];
        var f = x - k;
        return _v[baseIdx + k] * (1 - f) + _v[baseIdx + k + 1] * f;
    }
}
