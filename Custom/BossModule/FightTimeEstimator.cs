namespace BossMod;

// Remaining-fight-time (time-to-kill) estimation shared by rotation modules. Pure C#: no Dalamud / WorldState dependency, so the
// replay evaluator (tools/ttk_eval) and harnesses run exactly the code the plugin runs. AIHintsBuilder feeds it once per frame with the
// priority targets and publishes the result as AIHints.FightRemaining; no rotation module reads it yet.
//
// What is estimated: seconds until the fed targets are gone for good (killed, or gone from the feed because the fight ended). The
// estimate is a point value plus a calibrated [LowerBound, UpperBound] range (about the 10th / 90th percentile of the real remaining
// time, see FightTimeConfig). Unknown (Known == false) reports float.MaxValue in all three, which is what "no evidence that the fight
// ends soon" means to a consumer, so code that only compares against thresholds keeps its current behaviour.
public enum FightTimeModel : byte
{
    None = 0, // unknown
    Slope5s, // M0: linear HP slope over the last 5 s of the biggest target (the crude estimate SAM used to carry)
    AverageRate, // M1: average damage rate of the biggest target since it appeared
    WindowRate, // M2: net HP drop of the biggest target over the last WindowSeconds
    PhaseAware, // M3: per-target damage deltas on the clock of "something attackable exists", jumps / adds / gaps handled
    Prior, // M4: aligned with earlier kills of the same fight (needs a FightPrior)
    Hybrid, // M5: M3 and M4 blended by how well this pull matches the earlier ones
}

// One fed target: a hostile, attackable, living enemy (the highest-priority group of AIHints). Targets with MaxHP == 0 or CurHP == 0 are ignored.
public readonly record struct FightTargetSample(ulong ID, uint OID, uint CurHP, uint MaxHP);

public readonly struct FightTimeEstimate
{
    // Probabilities of the calibrated quantile nodes (FightTimeConfig.QuantileMult): the 10th and 90th are LowerBound and UpperBound.
    public static readonly float[] QuantileProbs = [0.05f, 0.10f, 0.25f, 0.50f, 0.75f, 0.90f, 0.95f];
    public const int QuantileNodes = 7;
    private const byte FlagBias = 1;
    private const byte FlagDowntimeBeforeEnd = 2;
    // an announced loss without a known return counts as at least this long (the shortest loss the forecast publishes)
    public const float UnknownReturnDowntime = 8.5f;

    private readonly float _remaining;
    private readonly float _lower;
    private readonly float _upper;
    private readonly float _confidence;
    private readonly float[]? _table; // ln of the calibrated multipliers (truth / estimate) at QuantileProbs: _table[_offset .. _offset + 7); null: log-normal from the bounds
    private readonly int _offset;
    private readonly float _downtime; // seconds of announced downtime inside the remaining time (RemainingAttackable = Remaining - this)
    private readonly byte _flags;
    public readonly FightTimeModel Model;
    public readonly float Elapsed; // seconds since the fight (combat) started, as the estimator saw it
    public readonly float PrimaryHPFraction; // HP fraction of the biggest fed target, 1 when unknown

    public FightTimeEstimate(FightTimeModel model, float remaining, float lower, float upper, float confidence, float elapsed, float primaryHPFraction)
        : this(model, remaining, lower, upper, confidence, elapsed, primaryHPFraction, null, 0, false) { }

    public FightTimeEstimate(FightTimeModel model, float remaining, float lower, float upper, float confidence, float elapsed, float primaryHPFraction, float[]? table, int offset, bool biasWarning)
    {
        Model = model;
        _remaining = remaining;
        _lower = lower;
        _upper = upper;
        _confidence = confidence;
        Elapsed = elapsed;
        PrimaryHPFraction = primaryHPFraction;
        _table = table;
        _offset = offset;
        _downtime = 0;
        _flags = biasWarning ? FlagBias : (byte)0;
    }

    private FightTimeEstimate(in FightTimeEstimate o, float downtime, byte flags, float confidence)
    {
        Model = o.Model;
        _remaining = o._remaining;
        _lower = o._lower;
        _upper = o._upper;
        _confidence = confidence;
        Elapsed = o.Elapsed;
        PrimaryHPFraction = o.PrimaryHPFraction;
        _table = o._table;
        _offset = o._offset;
        _downtime = downtime;
        _flags = flags;
    }

    public bool Known => Model != FightTimeModel.None;
    public float RemainingSeconds => Known ? _remaining : float.MaxValue; // point estimate
    public float LowerBound => Known ? _lower : float.MaxValue; // "at least this long" (about the 10th percentile): use for "can I afford X?"
    public float UpperBound => Known ? _upper : float.MaxValue; // "at most this long" (about the 90th percentile): use for "will this resource be wasted?"
    // 1 / (1 + ln(Upper / Lower)): 1 = tight, towards 0 = wide; halved while an announced downtime lies before the estimated end (the end is then
    // ambiguous: it may be the downtime), scaled by 0.7 under BiasWarning
    public float Confidence => Known ? _confidence : 0;
    // The fight has the structure that makes the rate estimate come out too short (the biggest target was replaced after an untargetable pause:
    // a multi-phase fight). UpperBound is already widened by its own calibration; treat RemainingSeconds as a lower estimate.
    public bool BiasWarning => Known && (_flags & FlagBias) != 0;
    // An announced target loss (WithDowntime) falls before the estimated end.
    public bool DowntimeBeforeEnd => Known && (_flags & FlagDowntimeBeforeEnd) != 0;
    // The remaining time without the announced downtime inside it: the time a target will really be attackable (== RemainingSeconds when none is announced).
    public float RemainingAttackable => Known ? MathF.Max(0, _remaining - _downtime) : float.MaxValue;
    public float LowerBoundAttackable => Known ? MathF.Max(0, _lower - _downtime) : float.MaxValue;
    public float UpperBoundAttackable => Known ? MathF.Max(0, _upper - _downtime) : float.MaxValue;
    public static FightTimeEstimate Unknown => default;

    // Probability (0..1) that the fight ends within `seconds` from now. Unknown: 0 (no evidence that it ends soon, like the bounds).
    // Calibrated on the evaluation data together with LowerBound / UpperBound, so EndsWithinProbability(LowerBound) ~ 0.1 and (UpperBound) ~ 0.9.
    // Use it to pick the threshold of a rule from its own cost ratio: dump a resource that is worth `g` if the fight ends within the time it
    // takes to use it, and costs `c` when held for nothing, when EndsWithinProbability(t) > c / (c + g).
    public float EndsWithinProbability(float seconds)
    {
        if (!Known)
            return 0;
        if (seconds <= 0)
            return 0;
        if (_remaining <= 0)
            return 1;
        var x = MathF.Log(seconds / _remaining);
        if (_table == null)
        {
            var sigma = SigmaFromBounds();
            if (sigma <= 1e-4f)
                return x >= 0 ? 1 : 0;
            return (float)NormalCdf(x / sigma);
        }
        // x is ln(seconds / estimate), the nodes are the calibrated ln(truth / estimate) at QuantileProbs
        Span<float> nodes = stackalloc float[QuantileNodes];
        for (var i = 0; i < QuantileNodes; ++i)
            nodes[i] = _table[_offset + i];
        if (x <= nodes[0])
        {
            var k = TailSlope(QuantileProbs[0], QuantileProbs[1], nodes[0], nodes[1]);
            return Math.Clamp(QuantileProbs[0] * MathF.Exp(k * (x - nodes[0])), 0, QuantileProbs[0]);
        }
        if (x >= nodes[QuantileNodes - 1])
        {
            var k = TailSlope(1 - QuantileProbs[QuantileNodes - 1], 1 - QuantileProbs[QuantileNodes - 2], -nodes[QuantileNodes - 1], -nodes[QuantileNodes - 2]);
            return Math.Clamp(1 - (1 - QuantileProbs[QuantileNodes - 1]) * MathF.Exp(-k * (x - nodes[QuantileNodes - 1])), QuantileProbs[QuantileNodes - 1], 1);
        }
        for (var i = 1; i < QuantileNodes; ++i)
            if (x <= nodes[i])
            {
                var span = nodes[i] - nodes[i - 1];
                var f = span > 1e-6f ? (x - nodes[i - 1]) / span : 1f;
                return QuantileProbs[i - 1] + f * (QuantileProbs[i] - QuantileProbs[i - 1]);
            }
        return 1;
    }

    // Seconds from now that the fight ends within with probability p (the inverse of EndsWithinProbability); float.MaxValue when unknown.
    public float Quantile(float p)
    {
        if (!Known)
            return float.MaxValue;
        p = Math.Clamp(p, 1e-4f, 1 - 1e-4f);
        if (_table == null)
            return _remaining * MathF.Exp(SigmaFromBounds() * (float)InverseNormalCdf(p));
        Span<float> nodes = stackalloc float[QuantileNodes];
        for (var i = 0; i < QuantileNodes; ++i)
            nodes[i] = _table[_offset + i];
        float x;
        if (p <= QuantileProbs[0])
            x = nodes[0] + MathF.Log(p / QuantileProbs[0]) / TailSlope(QuantileProbs[0], QuantileProbs[1], nodes[0], nodes[1]);
        else if (p >= QuantileProbs[QuantileNodes - 1])
            x = nodes[QuantileNodes - 1] - MathF.Log((1 - p) / (1 - QuantileProbs[QuantileNodes - 1])) / TailSlope(1 - QuantileProbs[QuantileNodes - 1], 1 - QuantileProbs[QuantileNodes - 2], -nodes[QuantileNodes - 1], -nodes[QuantileNodes - 2]);
        else
        {
            x = nodes[QuantileNodes - 1];
            for (var i = 1; i < QuantileNodes; ++i)
                if (p <= QuantileProbs[i])
                {
                    var f = (p - QuantileProbs[i - 1]) / (QuantileProbs[i] - QuantileProbs[i - 1]);
                    x = nodes[i - 1] + f * (nodes[i] - nodes[i - 1]);
                    break;
                }
        }
        return _remaining * MathF.Exp(x);
    }

    // The announced downtime (target loss in `lossIn` seconds, back after `returnIn`; float.MaxValue = not announced / unknown return) inside
    // the remaining time. A loss before the estimated end makes the end ambiguous, so Confidence is halved.
    public FightTimeEstimate WithDowntime(float lossIn, float returnIn)
    {
        if (!Known || lossIn >= float.MaxValue || lossIn >= _remaining)
            return this;
        lossIn = MathF.Max(lossIn, 0);
        var back = returnIn >= float.MaxValue ? lossIn + UnknownReturnDowntime : MathF.Max(returnIn, lossIn);
        var downtime = MathF.Max(0, MathF.Min(back, _remaining) - lossIn);
        // a second announcement (timeline and forecast both see the same loss) keeps the larger downtime and halves the confidence only once
        if ((_flags & FlagDowntimeBeforeEnd) != 0)
            return downtime > _downtime ? new(this, downtime, _flags, _confidence) : this;
        return new(this, downtime, (byte)(_flags | FlagDowntimeBeforeEnd), _confidence * 0.5f);
    }

    private float SigmaFromBounds() => _upper > _lower && _lower > 0 ? MathF.Log(_upper / _lower) / (2 * 1.2816f) : 0;

    // slope k of p(x) = p0 * exp(k (x - x0)) through the first two nodes (a tail that continues the curve), at least a gentle decay
    private static float TailSlope(float p0, float p1, float x0, float x1) => x1 - x0 > 1e-6f && p1 > p0 ? MathF.Max(MathF.Log(p1 / p0) / (x1 - x0), 0.5f) : 4f;

    private static double NormalCdf(double x)
    {
        // Abramowitz-Stegun 7.1.26 (|error| < 1.5e-7)
        var z = Math.Abs(x) / Math.Sqrt(2);
        var t = 1 / (1 + 0.3275911 * z);
        var y = 1 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-z * z);
        return 0.5 * (1 + (x >= 0 ? y : -y));
    }

    private static double InverseNormalCdf(double p)
    {
        // Acklam's rational approximation
        ReadOnlySpan<double> a = [-3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02, 1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00];
        ReadOnlySpan<double> b = [-5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02, 6.680131188771972e+01, -1.328068155288572e+01];
        ReadOnlySpan<double> c = [-7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00, -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00];
        ReadOnlySpan<double> d = [7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00];
        if (p < 0.02425)
        {
            var q = Math.Sqrt(-2 * Math.Log(p));
            return (((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        if (p > 1 - 0.02425)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - p));
            return -(((((c[0] * q + c[1]) * q + c[2]) * q + c[3]) * q + c[4]) * q + c[5]) / ((((d[0] * q + d[1]) * q + d[2]) * q + d[3]) * q + 1);
        }
        var r = p - 0.5;
        var s = r * r;
        return (((((a[0] * s + a[1]) * s + a[2]) * s + a[3]) * s + a[4]) * s + a[5]) * r / (((((b[0] * s + b[1]) * s + b[2]) * s + b[3]) * s + b[4]) * s + 1);
    }

    public override string ToString() => Known ? $"{Model} {_remaining:f1}s [{_lower:f1}, {_upper:f1}] conf {_confidence:f2}{(BiasWarning ? " bias?" : "")}" : "unknown";
}

// Earlier kills of one fight (one zone + boss set): duration and the HP fraction of the biggest target once a second.
public sealed class FightPrior
{
    public sealed class Kill(float duration, uint[] oid, float[] frac)
    {
        public readonly float Duration = duration;
        public readonly uint[] OID = oid; // OID of the biggest target at second i
        public readonly float[] Frac = frac; // its HP fraction at second i
    }

    private readonly List<Kill> _kills = [];
    public IReadOnlyList<Kill> Kills => _kills;
    public int Count => _kills.Count;

    public void Add(Kill kill) => _kills.Add(kill);

    // For every earlier kill that shows the same biggest target, the time that kill still had left at the point where its HP matched
    // `frac` (the match nearest to `elapsed` wins, so repeated plateaus and resets pick the right occurrence). The result is sorted
    // in out[0..n]; `pace` > 0 scales each by (elapsed / that kill's time at the match)^pace, i.e. a pull that is ahead of the earlier one
    // is expected to stay ahead.
    public int Align(float elapsed, uint oid, float frac, float pace, float[] outRemaining, bool anyOid = false)
    {
        var n = 0;
        for (var j = 0; j < _kills.Count && n < outRemaining.Length; ++j)
        {
            var k = _kills[j];
            var best = -1;
            var bestCost = float.MaxValue;
            for (var i = 0; i < k.Frac.Length; ++i)
            {
                if (!anyOid && k.OID[i] != oid)
                    continue;
                var cost = MathF.Abs(k.Frac[i] - frac) + 0.0005f * MathF.Abs(i - elapsed);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = i;
                }
            }
            if (best < 0)
                continue;
            var rem = MathF.Max(0.5f, k.Duration - best);
            if (pace > 0 && best >= 5 && elapsed >= 5)
                rem *= MathF.Pow(Math.Clamp(elapsed / best, 0.5f, 2f), pace);
            outRemaining[n++] = rem;
        }
        Array.Sort(outRemaining, 0, n);
        return n;
    }
}

public sealed record FightTimeConfig
{
    public FightTimeModel Model = FightTimeModel.PhaseAware;
    public float WindowSeconds = 30; // M2 / M3 window (seconds of wall / attackable time)
    public float HalfLifeSeconds; // M3: > 0 weights recent damage exponentially instead of the boxcar window
    public float MinSpanSeconds = 8; // M3: attackable seconds of history needed before an estimate is given
    public float M2MinSpanSeconds = 5; // M1 / M2: wall seconds since the biggest target appeared
    public float JumpFraction = 0.25f; // a single-frame HP drop above this share of MaxHP ...
    public float JumpRateSeconds = 10; // ... and above this many seconds of the fight's own average damage rate is a scripted HP set, not damage
    public bool UseActiveClock = true; // M3: time without an attackable target does not count as fight time (false: plain wall clock, for ablation)
    public float SamplePeriod = 0.5f;
    public float MaxFrameGap = 2; // a longer pause between two frames does not count as fight time
    public float OutOfCombatReset = 3; // seconds out of combat before the fight state is dropped
    public float LongGapReset = 30; // a gap this long between two updates (zone change, pause) also resets
    public float PriorPaceExponent;
    // M5: prior weight w = HybridMaxPriorWeight * n / (n + HybridPriorCountHalf) / (1 + (spread / HybridSpreadScale)^2), n = aligned earlier
    // kills, spread = ln(q75 / q25) of their per-kill remaining times; the estimate is exp(w ln prior + (1 - w) ln rate).
    public float HybridMaxPriorWeight = 0.95f;
    public float HybridPriorCountHalf = 1;
    public float HybridSpreadScale = 1;
    public int MinPriorKills = 2;
    public bool LoosePriorMatch = true; // no earlier kill shows the biggest target: match by HP fraction alone (poorly founded, bound kind 4)

    // Calibrated bounds: a table per kind of estimate (0 = rate from a single target, 1 = rate with several targets (adds, packs), 2..4 = led
    // by the prior: 2 when it is well founded (at least 12 earlier kills that agree with each other, log spread < 0.1, and with the rate
    // estimate, disagreement < 0.2), 4 when poorly (fewer than 5 kills, spread >= 0.25 or disagreement >= 0.4), 3 otherwise; 5 = rate from a
    // fight that changed its biggest target after an untargetable pause (a multi-phase fight: the rate estimate runs short, BiasWarning)), each bucketed
    // by the point estimate (edges in seconds); Lower / Upper = estimate * multiplier. Layout: [kind * (BoundEdges.Length + 1) + bucket].
    public const int BoundKinds = 6;
    public float[] BoundEdges = [5, 10, 20, 40, 80, 160];
    public float[] LowerMult = [.. Enumerable.Repeat(0.5f, BoundKinds * 7)];
    public float[] UpperMult = [.. Enumerable.Repeat(2f, BoundKinds * 7)];
    // ln(truth / estimate) at FightTimeEstimate.QuantileProbs (5 / 10 / 25 / 50 / 75 / 90 / 95 %) for the same kind / bucket cells, 7 values per
    // cell: [(kind * (BoundEdges.Length + 1) + bucket) * 7 + node]. Null: the quantiles are derived from the bounds (log-normal).
    public float[]? QuantileLn;

    // The configuration the evaluation recommends (tools/ttk_eval, docs in report.md): M5 hybrid, 120 s window, calibrated bounds. Without a
    // prior it is the M3 rate estimate. The bound tables below are the all-pull fit printed by 	tk_eval eval final.
    // ln(truth / estimate) at 5 / 10 / 25 / 50 / 75 / 90 / 95 % per kind x bucket cell (7 cells of 7 nodes per kind), same fit as LowerMult / UpperMult.
    public static readonly float[] QuantileLnTable =
    [
        // kind 0
        -1.0914f, -0.6412f, -0.0861f, 0.241f, 0.682f, 1.3007f, 1.9706f,
        -0.7851f, -0.615f, -0.3106f, -0.0205f, 0.2503f, 0.4528f, 0.567f,
        -0.619f, -0.4522f, -0.2472f, 0.0082f, 0.2065f, 0.3384f, 0.4192f,
        -0.4264f, -0.3042f, -0.1372f, 0.0079f, 0.1208f, 0.2119f, 0.2728f,
        -0.2042f, -0.157f, -0.0756f, 0.0208f, 0.1146f, 0.2312f, 0.311f,
        -0.1727f, -0.1245f, -0.0577f, 0.022f, 0.143f, 0.2695f, 0.3519f,
        -0.2539f, -0.1996f, -0.094f, 0.0322f, 0.223f, 0.3895f, 0.4655f,
        // kind 1
        -0.9003f, -0.4396f, 0.092f, 0.9334f, 1.5694f, 3.4451f, 4.4856f,
        -1.0833f, -0.7595f, -0.3141f, 0.1046f, 0.6372f, 2.3487f, 2.7141f,
        -1.2233f, -0.8524f, -0.3686f, 0.0088f, 0.5648f, 1.6743f, 2.0423f,
        -1.0259f, -0.7636f, -0.3128f, 0.0332f, 0.4659f, 1.0873f, 1.6357f,
        -0.9338f, -0.6146f, -0.2214f, 0.1512f, 0.569f, 0.968f, 1.1913f,
        -0.9671f, -0.6482f, -0.2678f, 0.0904f, 0.3939f, 0.665f, 0.8218f,
        -2.5772f, -1.7769f, -0.712f, -0.0845f, 0.1952f, 0.4665f, 0.6129f,
        // kind 2
        -0.2752f, -0.2389f, -0.1387f, 0.02f, 0.0899f, 0.1547f, 0.2253f,
        -0.2752f, -0.2389f, -0.1387f, 0.02f, 0.0899f, 0.1547f, 0.2253f,
        -0.2752f, -0.2389f, -0.1387f, 0.02f, 0.0899f, 0.1547f, 0.2253f,
        -0.2775f, -0.2394f, -0.1389f, 0.02f, 0.0885f, 0.1489f, 0.2216f,
        -0.12f, -0.0852f, -0.0404f, -0f, 0.0432f, 0.088f, 0.1118f,
        -0.0816f, -0.062f, -0.0275f, 0.0013f, 0.0267f, 0.0529f, 0.0718f,
        -0.0714f, -0.0577f, -0.0227f, 0.0053f, 0.0335f, 0.0596f, 0.1125f,
        // kind 3
        -0.2835f, -0.1903f, -0.0619f, 0.0881f, 0.2756f, 0.4991f, 1.8537f,
        -0.3003f, -0.2094f, -0.0837f, 0.0678f, 0.2185f, 0.3924f, 1.8419f,
        -0.2211f, -0.1736f, -0.069f, 0.0801f, 0.2412f, 1.2979f, 1.6671f,
        -0.3284f, -0.205f, -0.0809f, 0.0109f, 0.087f, 0.1572f, 0.2043f,
        -0.1576f, -0.1192f, -0.0545f, 0.0074f, 0.0582f, 0.1073f, 0.1435f,
        -0.098f, -0.0713f, -0.0218f, 0.0192f, 0.0552f, 0.0957f, 0.1331f,
        -0.0823f, -0.0622f, -0.0233f, 0.0057f, 0.0521f, 0.0882f, 0.1258f,
        // kind 4
        -0.6603f, -0.265f, 0.0347f, 0.4185f, 1.1199f, 2.3426f, 3.5262f,
        -0.3603f, -0.2371f, -0.0516f, 0.2178f, 0.8034f, 1.5545f, 2.4331f,
        -0.5008f, -0.3087f, -0.0641f, 0.1076f, 0.3673f, 1.3066f, 1.6235f,
        -0.4337f, -0.3426f, -0.135f, 0.0207f, 0.133f, 0.2997f, 0.4775f,
        -0.3099f, -0.2408f, -0.0808f, 0.0261f, 0.1448f, 0.3377f, 0.4906f,
        -0.2928f, -0.2072f, -0.0484f, 0.0211f, 0.1514f, 0.2286f, 0.2671f,
        -0.2957f, -0.2034f, -0.043f, 0.0064f, 0.0648f, 0.1347f, 0.1733f,
        // kind 5
        -0.1455f, 0.0411f, 0.328f, 0.9945f, 3.892f, 4.8879f, 5.3965f,
        -0.0896f, 0.03f, 0.235f, 0.7854f, 2.6689f, 3.027f, 3.3522f,
        -0.6949f, -0.0865f, 0.1796f, 0.5999f, 0.9407f, 2.2916f, 2.5357f,
        -0.3596f, -0.1082f, 0.0622f, 0.4361f, 0.9667f, 1.6991f, 1.973f,
        -0.2444f, -0.1463f, -0.0311f, 0.0711f, 0.2653f, 0.6983f, 0.8615f,
        -0.213f, -0.1479f, -0.0695f, -0.0136f, 0.0646f, 0.2855f, 0.8658f,
        -3.1996f, -2.7646f, -1.3601f, -0.0695f, 0.1605f, 0.4205f, 0.656f,
    ];

    public static FightTimeConfig Recommended() => new()
    {
        Model = FightTimeModel.Hybrid,
        WindowSeconds = 120,
        HybridMaxPriorWeight = 0.95f,
        HybridPriorCountHalf = 0.5f,
        HybridSpreadScale = 2,
        LowerMult =
        [
            0.5267f, 0.5407f, 0.6362f, 0.7377f, 0.8547f, 0.883f, 0.819f, // single target
            0.6443f, 0.4679f, 0.4264f, 0.466f, 0.5409f, 0.523f, 0.1692f, // several targets
            0.7875f, 0.7875f, 0.7875f, 0.7871f, 0.9184f, 0.9399f, 0.9439f, // prior-led tier 1
            0.8268f, 0.8111f, 0.8407f, 0.8147f, 0.8876f, 0.9311f, 0.9397f, // prior-led tier 2
            0.7672f, 0.7889f, 0.7344f, 0.7099f, 0.786f, 0.8129f, 0.816f, // prior-led tier 3
            1.042f, 1.0304f, 0.9172f, 0.8975f, 0.8639f, 0.8625f, 0.063f, // multi-phase fight (bias warning)
        ],
        UpperMult =
        [
            3.6718f, 1.5727f, 1.4027f, 1.236f, 1.2601f, 1.3093f, 1.4762f, // single target
            31.345f, 10.4719f, 5.3348f, 2.9664f, 2.6327f, 1.9445f, 1.5945f, // several targets
            1.1673f, 1.1673f, 1.1673f, 1.1606f, 1.092f, 1.0543f, 1.0615f, // prior-led tier 1
            1.6472f, 1.4805f, 3.6616f, 1.1702f, 1.1133f, 1.1004f, 1.0922f, // prior-led tier 2
            10.4087f, 4.7327f, 3.6936f, 1.3495f, 1.4017f, 1.2569f, 1.1442f, // prior-led tier 3
            132.6722f, 20.6343f, 9.8903f, 5.4689f, 2.0103f, 1.3304f, 1.5227f, // multi-phase fight (bias warning)
        ],
        QuantileLn = QuantileLnTable,
    };

}

public sealed class FightTimeEstimator
{
    private struct Slot
    {
        public ulong ID;
        public uint PrevHP;
        public uint MaxHP;
        public double LastSeen;
        public int Stamp;
        public bool Used;
    }

    private struct Sample
    {
        public double Wall;
        public double Active;
        public double Damage;
        public uint PrimHP;
        public int PrimEpoch;
    }

    private const int MaxSlots = 64;
    private const int RingSize = 512;
    private const int SlopeSamples = 32;
    private const float SlopeWindow = 5;
    private const float SlopeMinSpan = 2;
    private const float SlopeMaxStepDrop = 0.3f;

    private readonly FightTimeConfig _cfg;
    private readonly Slot[] _slots = new Slot[MaxSlots];
    private readonly Sample[] _ring = new Sample[RingSize];
    private int _head;
    private int _count;
    private FightPrior? _prior;
    private float[] _scratch = new float[8];

    private bool _started;
    private double _t0;
    private double _lastNow = double.NegativeInfinity;
    private double _outOfCombat;
    private bool _prevHadFeed;
    private int _stamp;
    private double _active;
    private double _damage;
    private double _gapLen;
    private double _lastSampleWall = double.NegativeInfinity;
    private int _primEpoch;
    private ulong _primID;
    private double _epochWall0;
    private uint _epochHP0;
    private uint _primOID;
    private uint _primMax;
    private uint _primHP;
    private double _remainingHP;
    private int _feedCount;

    // M0 state
    private readonly double[] _slopeT = new double[SlopeSamples];
    private readonly uint[] _slopeHP = new uint[SlopeSamples];
    private int _slopeCount;
    private ulong _slopeID;

    // cached estimate
    private double _computedWall;
    private FightTimeModel _cModel;
    private float _cRemaining;
    private float _cConf;
    private int _cKind;
    private int _cOffset;
    private bool _cBias;
    private bool _priorLoose;
    private float _hybridW;
    private float _cLower;
    private float _cUpper;
    private float _cFrac = 1;

    public FightTimeEstimator(FightTimeConfig? config = null) => _cfg = config ?? FightTimeConfig.Recommended();

    public FightTimeConfig Config => _cfg;
    public FightTimeEstimate Estimate { get; private set; }
    public int JumpCount { get; private set; } // scripted HP jumps ignored so far (diagnostics)
    public int FeedCount => _feedCount; // targets in the last feed
    public int SeenTargets { get; private set; } // distinct targets seen in this fight (diagnostics)
    public int PhaseChanges { get; private set; } // times the biggest target was replaced while the fight went on (a phase / boss change)
    public int GapCount { get; private set; } // pauses of 2 s or more without any attackable target inside the fight (intermissions, untargetable phases)
    public float GapSeconds { get; private set; }
    public int MaxFeedSeen { get; private set; }
    public int BoundKind => _cKind; // which bounds table the last estimate used (diagnostics)

    // Diagnostics of the last Hybrid computation (NaN when a part was unavailable): both estimates, how many earlier kills were aligned
    // and their log spread.
    public float DbgM3 { get; private set; } = float.NaN;
    public float DbgM4 { get; private set; } = float.NaN;
    public int DbgPriorCount { get; private set; }
    public float DbgPriorSpread { get; private set; } = float.NaN;
    public float DbgDisagree { get; private set; } // |ln(rate / prior)| when both existed, else 0

    public FightPrior? Prior
    {
        get => _prior;
        set
        {
            _prior = value;
            var size = Math.Max(8, value?.Count ?? 0);
            if (_scratch.Length < size)
                _scratch = new float[size];
        }
    }

    public void Reset()
    {
        Array.Clear(_slots);
        _head = 0;
        _count = 0;
        _started = false;
        _outOfCombat = 0;
        _prevHadFeed = false;
        _active = 0;
        _damage = 0;
        _lastSampleWall = double.NegativeInfinity;
        _primEpoch = 0;
        _primID = 0;
        _primOID = 0;
        _primMax = 0;
        _primHP = 0;
        _remainingHP = 0;
        _feedCount = 0;
        _slopeCount = 0;
        _slopeID = 0;
        _cModel = FightTimeModel.None;
        JumpCount = 0;
        SeenTargets = 0;
        PhaseChanges = 0;
        GapCount = 0;
        GapSeconds = 0;
        MaxFeedSeen = 0;
        _gapLen = 0;
        Estimate = default;
    }

    // `now` is a monotonic time in seconds (any origin). Calling again with the same or an earlier time is a no-op, so several consumers
    // may call per frame. Allocation-free.
    public void Update(double now, bool inCombat, ReadOnlySpan<FightTargetSample> targets)
    {
        if (now <= _lastNow)
            return;
        var dt = now - _lastNow;
        _lastNow = now;
        if (dt > _cfg.LongGapReset)
            Reset();

        if (!inCombat)
        {
            _outOfCombat += double.IsFinite(dt) ? dt : 0;
            if (_outOfCombat >= _cfg.OutOfCombatReset && (_started || Estimate.Known))
                Reset();
            Estimate = default;
            return;
        }
        _outOfCombat = 0;
        if (!double.IsFinite(dt))
            dt = 0;
        if (!_started)
        {
            _started = true;
            _t0 = now; // elapsed counts from the first in-combat update
        }

        ++_stamp;
        // Pass 1: damage deltas of known targets, new targets, the biggest target, remaining HP.
        var feed = 0;
        double remaining = 0;
        var primIdx = -1;
        for (var i = 0; i < targets.Length; ++i)
        {
            ref readonly var t = ref targets[i];
            if (t.MaxHP == 0 || t.CurHP == 0)
                continue;
            ++feed;
            remaining += t.CurHP;
            if (primIdx < 0 || t.MaxHP > targets[primIdx].MaxHP || t.MaxHP == targets[primIdx].MaxHP && (t.OID < targets[primIdx].OID || t.OID == targets[primIdx].OID && t.ID < targets[primIdx].ID))
                primIdx = i;
            var s = FindSlot(t.ID);
            if (s >= 0)
            {
                ref var slot = ref _slots[s];
                if (now - slot.LastSeen <= 1.0 && slot.PrevHP > t.CurHP)
                {
                    var drop = slot.PrevHP - t.CurHP;
                    // a scripted HP set is both a big share of the target and far more than the party deals in a while; with no rate yet, nothing is
                    var rate = _active >= 5 ? _damage / _active : double.MaxValue;
                    if (drop > _cfg.JumpFraction * t.MaxHP && drop > _cfg.JumpRateSeconds * rate)
                        ++JumpCount;
                    else
                        _damage += drop;
                }
                slot.PrevHP = t.CurHP;
                slot.MaxHP = t.MaxHP;
                slot.LastSeen = now;
                slot.Stamp = _stamp;
            }
            else
            {
                var free = FreeSlot();
                if (free >= 0)
                    _slots[free] = new Slot { ID = t.ID, PrevHP = t.CurHP, MaxHP = t.MaxHP, LastSeen = now, Stamp = _stamp, Used = true };
                ++SeenTargets;
            }
        }
        // Pass 2: targets that left the feed. One that was nearly dead was killed: its last HP counts as damage.
        for (var i = 0; i < MaxSlots; ++i)
        {
            ref var slot = ref _slots[i];
            if (!slot.Used || slot.Stamp == _stamp)
                continue;
            if (slot.Stamp == _stamp - 1 && slot.PrevHP <= 0.02f * slot.MaxHP)
            {
                _damage += slot.PrevHP;
                slot = default;
            }
            else if (now - slot.LastSeen > 10)
                slot = default;
        }

        _feedCount = feed;
        _remainingHP = remaining;
        if (feed == 0)
        {
            if (!_cfg.UseActiveClock)
                _active += Math.Min(dt, _cfg.MaxFrameGap);
            if (_prevHadFeed || _gapLen > 0 || _count > 0)
                _gapLen += Math.Min(dt, _cfg.MaxFrameGap);
            _prevHadFeed = false;
            Estimate = default;
            return;
        }

        if (_prevHadFeed || !_cfg.UseActiveClock)
            _active += Math.Min(dt, _cfg.MaxFrameGap);
        if (!_prevHadFeed && _gapLen >= 2)
        {
            ++GapCount;
            GapSeconds += (float)_gapLen;
        }
        _gapLen = 0;
        _prevHadFeed = true;
        if (feed > MaxFeedSeen)
            MaxFeedSeen = feed;

        ref readonly var prim = ref targets[primIdx];
        _primOID = prim.OID;
        _primMax = prim.MaxHP;
        _primHP = prim.CurHP;
        if (prim.ID != _primID)
        {
            if (_primID != 0)
                ++PhaseChanges;
            _primID = prim.ID;
            ++_primEpoch;
            _epochWall0 = now;
            _epochHP0 = prim.CurHP;
        }

        if (now - _lastSampleWall >= _cfg.SamplePeriod)
        {
            _lastSampleWall = now;
            ref var smp = ref _ring[_head];
            smp.Wall = now;
            smp.Active = _active;
            smp.Damage = _damage;
            smp.PrimHP = prim.CurHP;
            smp.PrimEpoch = _primEpoch;
            _head = (_head + 1) % RingSize;
            if (_count < RingSize)
                ++_count;
            SlopeSample(now, prim);
            Recompute(now);
        }
        Publish(now);
    }

    private int FindSlot(ulong id)
    {
        for (var i = 0; i < MaxSlots; ++i)
            if (_slots[i].Used && _slots[i].ID == id)
                return i;
        return -1;
    }

    private int FreeSlot()
    {
        for (var i = 0; i < MaxSlots; ++i)
            if (!_slots[i].Used)
                return i;
        return -1;
    }

    private ref Sample Back(int k) => ref _ring[(_head - 1 - k + 2 * RingSize) % RingSize];

    // Replicates the SAM slope (samples every SamplePeriod, 5 s window, history cleared on a big step drop or a new target).
    private void SlopeSample(double now, in FightTargetSample prim)
    {
        if (prim.ID != _slopeID)
        {
            _slopeID = prim.ID;
            _slopeCount = 0;
        }
        if (_slopeCount > 0 && (float)_slopeHP[_slopeCount - 1] - prim.CurHP > SlopeMaxStepDrop * prim.MaxHP)
            _slopeCount = 0;
        if (_slopeCount == SlopeSamples)
        {
            Array.Copy(_slopeT, 1, _slopeT, 0, SlopeSamples - 1);
            Array.Copy(_slopeHP, 1, _slopeHP, 0, SlopeSamples - 1);
            --_slopeCount;
        }
        _slopeT[_slopeCount] = now;
        _slopeHP[_slopeCount] = prim.CurHP;
        ++_slopeCount;
        var keepFrom = now - SlopeWindow;
        var stale = -1;
        for (var i = 0; i < _slopeCount; ++i)
            if (_slopeT[i] >= keepFrom)
            {
                stale = i;
                break;
            }
        if (stale > 1)
        {
            var remove = stale - 1;
            Array.Copy(_slopeT, remove, _slopeT, 0, _slopeCount - remove);
            Array.Copy(_slopeHP, remove, _slopeHP, 0, _slopeCount - remove);
            _slopeCount -= remove;
        }
    }

    private float SlopeRemaining(double now)
    {
        if (_slopeCount < 2)
            return -1;
        var span = (float)(now - _slopeT[0]);
        var dropped = (float)_slopeHP[0] - _primHP;
        if (span < SlopeMinSpan || dropped <= 0)
            return -1;
        return _primHP / (dropped / span);
    }

    private float AverageRateRemaining(double now)
    {
        var span = (float)(now - _epochWall0);
        var dropped = (float)_epochHP0 - _primHP;
        if (span < _cfg.M2MinSpanSeconds || dropped <= 0)
            return -1;
        return _primHP / (dropped / span);
    }

    private float WindowRateRemaining(double now)
    {
        var baseWall = _epochWall0;
        var baseHP = _epochHP0;
        for (var k = 0; k < _count; ++k)
        {
            ref var s = ref Back(k);
            if (s.PrimEpoch != _primEpoch)
                break;
            if (s.Wall <= now - _cfg.WindowSeconds)
            {
                baseWall = s.Wall;
                baseHP = s.PrimHP;
                break;
            }
        }
        var span = (float)(now - baseWall);
        var dropped = (float)baseHP - _primHP;
        if (span < _cfg.M2MinSpanSeconds || dropped <= 0)
            return -1;
        return _primHP / (dropped / span);
    }

    private float PhaseAwareRemaining()
    {
        double num = 0, den = 0, cover = 0;
        var window = _cfg.WindowSeconds;
        var hl = _cfg.HalfLifeSeconds;
        var reach = hl > 0 ? 4 * hl : window;
        for (var k = 0; k + 1 < _count; ++k)
        {
            ref var s = ref Back(k);
            ref var p = ref Back(k + 1);
            var age = _active - s.Active;
            if (age >= reach)
                break;
            var dti = s.Active - p.Active;
            if (dti <= 0)
                continue;
            var w = hl > 0 ? Math.Pow(0.5, age / hl) : 1;
            num += w * (s.Damage - p.Damage);
            den += w * dti;
            cover += dti;
        }
        if (cover < _cfg.MinSpanSeconds || num <= 0)
            return -1;
        var rate = num / den;
        return (float)(_remainingHP / rate);
    }

    private float PriorRemaining(double now, float elapsed, out int n)
    {
        n = 0;
        if (_prior == null || _prior.Count < _cfg.MinPriorKills)
            return -1;
        var frac = _primMax > 0 ? (float)_primHP / _primMax : 1;
        n = _prior.Align(elapsed, _primOID, frac, _cfg.PriorPaceExponent, _scratch);
        _priorLoose = false;
        if (n < _cfg.MinPriorKills && _cfg.LoosePriorMatch)
        {
            // no earlier kill shows this biggest target (the summary track and the live feed can disagree about which of several bosses is the biggest
            // attackable one): align by HP fraction alone and say so
            n = _prior.Align(elapsed, _primOID, frac, _cfg.PriorPaceExponent, _scratch, anyOid: true);
            _priorLoose = n >= _cfg.MinPriorKills;
        }
        DbgPriorCount = n;
        DbgPriorSpread = n >= 2 ? MathF.Log(MathF.Max(_scratch[Math.Min(n - 1, 3 * n / 4)], 0.5f) / MathF.Max(_scratch[n / 4], 0.5f)) : float.NaN;
        if (n < _cfg.MinPriorKills)
            return -1;
        return _scratch[n / 2];
    }

    private void Recompute(double now)
    {
        var elapsed = (float)(now - _t0);
        float est = -1;
        var model = _cfg.Model;
        switch (model)
        {
            case FightTimeModel.Slope5s:
                est = SlopeRemaining(now);
                break;
            case FightTimeModel.AverageRate:
                est = AverageRateRemaining(now);
                break;
            case FightTimeModel.WindowRate:
                est = WindowRateRemaining(now);
                break;
            case FightTimeModel.PhaseAware:
                est = PhaseAwareRemaining();
                break;
            case FightTimeModel.Prior:
                est = PriorRemaining(now, elapsed, out _);
                break;
            case FightTimeModel.Hybrid:
                {
                    var m3 = PhaseAwareRemaining();
                    var m4 = PriorRemaining(now, elapsed, out var n);
                    DbgM3 = m3 > 0 ? m3 : float.NaN;
                    DbgM4 = m4 > 0 ? m4 : float.NaN;
                    DbgDisagree = m3 > 0 && m4 > 0 ? MathF.Abs(MathF.Log(m3 / m4)) : 0;
                    if (m3 > 0 && m4 > 0)
                    {
                        // trust the prior to the degree that enough earlier kills agree with each other
                        var s = float.IsNaN(DbgPriorSpread) ? 0 : DbgPriorSpread / _cfg.HybridSpreadScale;
                        _hybridW = _cfg.HybridMaxPriorWeight * n / (n + _cfg.HybridPriorCountHalf) / (1 + s * s);
                        est = MathF.Exp(_hybridW * MathF.Log(m4) + (1 - _hybridW) * MathF.Log(m3));
                    }
                    else
                    {
                        est = m3 > 0 ? m3 : m4;
                        _hybridW = m3 > 0 ? 0 : 1;
                    }
                    break;
                }
        }

        _computedWall = now;
        _cFrac = _primMax > 0 ? (float)_primHP / _primMax : 1;
        if (est <= 0 || !float.IsFinite(est))
        {
            _cModel = FightTimeModel.None;
            return;
        }
        est = MathF.Min(est, 36000);
        _cModel = model;
        _cRemaining = est;
        var b = 0;
        var edges = _cfg.BoundEdges;
        while (b < edges.Length && est >= edges[b])
            ++b;
        _cKind = BoundKindOf(model);
        var idx = _cKind * (edges.Length + 1) + b;
        _cLower = est * _cfg.LowerMult[idx];
        _cUpper = est * _cfg.UpperMult[idx];
        _cOffset = idx * FightTimeEstimate.QuantileNodes;
        _cBias = _cKind == 5;
        _cConf = 1f / (1f + MathF.Log(MathF.Max(_cUpper / MathF.Max(_cLower, 0.01f), 1f))) * (_cBias ? 0.7f : 1f);
    }

    private int BoundKindOf(FightTimeModel model)
    {
        if (model == FightTimeModel.Prior || model == FightTimeModel.Hybrid && _hybridW >= 0.5f)
        {
            var s = DbgPriorSpread;
            var d = model == FightTimeModel.Hybrid ? DbgDisagree : 0;
            if (DbgPriorCount < 5 || float.IsNaN(s) || s >= 0.25f || d >= 0.4f || _priorLoose)
                return 4;
            return DbgPriorCount < 12 || s >= 0.1f || d >= 0.2f ? 3 : 2;
        }
        if (PhaseChanges >= 1 && GapCount >= 1)
            return 5;
        return _feedCount >= 2 || SeenTargets >= 3 ? 1 : 0;
    }

    private void Publish(double now)
    {
        if (_cModel == FightTimeModel.None)
        {
            Estimate = default;
            return;
        }
        // the estimate was computed at the last sample; time has passed since
        var shift = (float)(now - _computedWall);
        Estimate = new(_cModel, MathF.Max(0, _cRemaining - shift), MathF.Max(0, _cLower - shift), MathF.Max(0, _cUpper - shift), _cConf, (float)(now - _t0), _cFrac, _cfg.QuantileLn, _cOffset, _cBias);
    }
}
