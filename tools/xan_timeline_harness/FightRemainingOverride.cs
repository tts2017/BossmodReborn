using System;
using System.Collections.Generic;
using System.Linq;
using BossMod;

namespace XanTimelineHarness;

// XAN_HARNESS_TTK: what the rotation sees in hints.FightRemaining (the shared fight-time estimate, FightTimeEstimator), so the value of knowing
// the fight length can be measured without the estimator's own error. Unset: unchanged (FightRemaining stays unknown, output byte-identical).
//   perfect             exact seconds to the scenario end (the frame loop stops at the scenario duration, which is the time the target dies),
//                       Lower = Upper = point, Known. Target-loss windows are NOT subtracted: it is wall seconds, as the estimator reports them.
//   noisy:<sigma>[:<b>[:aware]] the exact remaining R times exp(b + n(t) * s(R)): n(t) is smooth unit noise (value noise on a 6 s grid, deterministic
//                       per scenario, time and XAN_HARNESS_TTK_SEED, default 1) and s(R) = (sigma / 0.17) * sqrt(0.17^2 + (3.3 / R)^2), clamped to
//                       +-1.2, refit to the measured error of the improved estimator (bmr_ttk2_out report): sigma 0.17 = no prior, single boss
//                       (median relative error ~0.45 / 0.27 / 0.15 / 0.11 for R <= 10 / 10-20 / 20-40 / 40-80 s); 0.09 = prior-led (summary
//                       prior); 0.40 with b = -0.25 = alliance / multi-phase without a prior; 0.6 = trash packs. b is a bias in ln units.
//                       Lower / Upper = estimate * exp(-/+ 1.2816 * s(estimate)): the 10 / 90 percent range of the injected noise at the reported
//                       value, and EndsWithinProbability / Quantile come from a 7-node quantile table of the same noise. By default the bias is not
//                       known to the estimator, so a biased run under-covers like the estimator does (the structural detector catches about a
//                       quarter of the alliance bias); the token `aware` says the bias is known (BiasWarning set, bounds and quantiles centred on
//                       it), which brackets a better detector. Unknown for the first 8 s unless sigma < 0.1 (a prior knows from the start).
//   blind               Known with no information: Remaining = Upper = float.MaxValue, Lower = 0. A control for rules that branch on Known.
internal static class FightRemainingOverride
{
    public enum Kind { Off, Perfect, Noisy, Blind }

    public static readonly Kind Mode;
    public static readonly float Sigma;
    public static readonly float Bias;
    public static readonly bool BiasAware;
    public static readonly int Seed = 1;
    private const float KnotSpacing = 6f;
    private const float ShortTermSeconds = 3.3f;
    private const float RefSigma = 0.17f;
    private const float MaxLogError = 1.2f;

    static FightRemainingOverride()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("XAN_HARNESS_TTK_SEED"), out var seed))
            Seed = seed;
        var spec = Environment.GetEnvironmentVariable("XAN_HARNESS_TTK");
        if (string.IsNullOrWhiteSpace(spec))
        {
            QuantileTable = [];
            return;
        }
        var parts = spec.Split(':');
        switch (parts[0].Trim().ToLowerInvariant())
        {
            case "perfect":
                Mode = Kind.Perfect;
                break;
            case "blind":
                Mode = Kind.Blind;
                break;
            case "noisy":
                Mode = Kind.Noisy;
                Sigma = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : RefSigma;
                Bias = parts.Length > 2 ? float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture) : 0f;
                BiasAware = parts.Length > 3 && parts[3].Trim().Equals("aware", StringComparison.OrdinalIgnoreCase);
                break;
            default:
                throw new ArgumentException($"XAN_HARNESS_TTK must be perfect, blind or noisy:<sigma>[:<bias>[:aware]], got '{spec}'");
        }
        QuantileTable = Mode == Kind.Noisy ? BuildQuantileTable() : [];
    }

    public static bool Enabled => Mode != Kind.Off;

    public static ulong ScenarioHash(string key)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in key)
            hash = (hash ^ c) * 1099511628211UL;
        return hash;
    }

    // ln(truth / estimate) quantile nodes (FightTimeEstimate.QuantileProbs) of the injected noise for each spread on a 0.01 grid, shared by
    // all estimates so the harness stays allocation free per frame
    private const int SpreadSteps = 121;
    private static readonly float[] QuantileTable; // built in the static constructor, after the spec (bias, aware) is parsed

    private static float[] BuildQuantileTable()
    {
        float[] z = [-1.6449f, -1.2816f, -0.6745f, 0f, 0.6745f, 1.2816f, 1.6449f];
        var table = new float[SpreadSteps * FightTimeEstimate.QuantileNodes];
        var centre = BiasAware ? -Bias : 0f;
        for (var i = 0; i < SpreadSteps; ++i)
            for (var j = 0; j < FightTimeEstimate.QuantileNodes; ++j)
                table[i * FightTimeEstimate.QuantileNodes + j] = centre + z[j] * (i * 0.01f);
        return table;
    }

    private static int QuantileOffset(float spread) => Math.Clamp((int)MathF.Round(spread / 0.01f), 0, SpreadSteps - 1) * FightTimeEstimate.QuantileNodes;

    // time: seconds since the pull (negative during a countdown); the scenario ends at `duration`
    public static FightTimeEstimate At(ulong scenario, float duration, float time)
    {
        var remaining = MathF.Max(0f, duration - time);
        switch (Mode)
        {
            case Kind.Perfect:
                return new(FightTimeModel.Prior, remaining, remaining, remaining, 1f, MathF.Max(0f, time), 1f);
            case Kind.Blind:
                return new(FightTimeModel.PhaseAware, float.MaxValue, 0f, float.MaxValue, 0.01f, MathF.Max(0f, time), 1f);
            case Kind.Noisy:
            {
                if (Sigma >= 0.1f && time < 8f)
                    return FightTimeEstimate.Unknown;
                var r = MathF.Max(remaining, 0.05f);
                var scale = Sigma / RefSigma * MathF.Sqrt(RefSigma * RefSigma + MathF.Pow(ShortTermSeconds / r, 2));
                var logError = Math.Clamp(Bias + (float)SmoothNoise(scenario, time) * scale, -MaxLogError, MaxLogError);
                var estimate = r * MathF.Exp(logError);
                // calibrated 10 / 90 percent range of the noise at the reported value, the bias unknown to the estimator (as in the report: a
                // multi-phase fight without a prior is biased low and its Upper then covers too little)
                var spread = MathF.Min(MaxLogError, Sigma / RefSigma * MathF.Sqrt(RefSigma * RefSigma + MathF.Pow(ShortTermSeconds / MathF.Max(estimate, 0.05f), 2)));
                // ln(truth / estimate) = -(bias + n s): centred on -bias when the bias is known, else on 0
                var centre = BiasAware ? -Bias : 0f;
                var lower = estimate * MathF.Exp(centre - 1.2816f * spread);
                var upper = estimate * MathF.Exp(centre + 1.2816f * spread);
                var confidence = 1f / (1f + MathF.Log(MathF.Max(upper / MathF.Max(lower, 0.01f), 1f)));
                if (BiasAware)
                    confidence *= 0.7f;
                return new(Sigma >= 0.1f ? FightTimeModel.PhaseAware : FightTimeModel.Hybrid, estimate, lower, upper, confidence, MathF.Max(0f, time), 1f, QuantileTable, QuantileOffset(spread), BiasAware);
            }
            default:
                return FightTimeEstimate.Unknown;
        }
    }

    // 	tk-override-dump: the error profile of the current XAN_HARNESS_TTK mode over many synthetic scenarios (median |ln(estimate / truth)| by remaining time,
    // the share of truths below Lower / above Upper, and one sample series), for checking the noise against the estimator's measured error.
    public static int Dump()
    {
        Console.WriteLine(FormattableString.Invariant($"mode={Mode} sigma={Sigma} bias={Bias} seed={Seed}"));
        string[] names = ["<=10", "10-20", "20-40", "40-80", "80-160", ">160"];
        float[] edges = [10, 20, 40, 80, 160];
        var errors = names.Select(_ => new List<double>()).ToArray();
        var below = new int[names.Length];
        var above = new int[names.Length];
        for (var s = 0; s < 400; ++s)
        {
            var scenario = ScenarioHash("s" + s);
            for (var t = 10f; t < 299f; t += 2.5f)
            {
                var e = At(scenario, 300f, t);
                if (!e.Known)
                    continue;
                var r = 300f - t;
                var b = 0;
                while (b < edges.Length && r >= edges[b])
                    ++b;
                errors[b].Add(Math.Abs(Math.Log(e.RemainingSeconds / r)));
                if (r < e.LowerBound) ++below[b];
                if (r > e.UpperBound) ++above[b];
            }
        }
        for (var b = 0; b < names.Length; ++b)
        {
            errors[b].Sort();
            if (errors[b].Count == 0)
                continue;
            Console.WriteLine(FormattableString.Invariant($"R {names[b],-7} n={errors[b].Count,5} median|ln err|={errors[b][errors[b].Count / 2]:f3} (median rel err ~{Math.Exp(errors[b][errors[b].Count / 2]) - 1:f2}) truth<Lower {100.0 * below[b] / errors[b].Count:f1}% truth>Upper {100.0 * above[b] / errors[b].Count:f1}%"));
        }
        var sc = ScenarioHash("sample");
        for (var t = 0f; t <= 300f; t += 20f)
        {
            var e = At(sc, 300f, t);
            Console.WriteLine(FormattableString.Invariant($"t={t,5:f0} R={300 - t,5:f0} known={e.Known} est={(e.Known ? e.RemainingSeconds : -1):f1} lower={(e.Known ? e.LowerBound : -1):f1} upper={(e.Known ? e.UpperBound : -1):f1}"));
        }
        return 0;
    }

    // value noise: independent unit normals on a grid, smoothstep-blended and renormalised to unit variance
    private static double SmoothNoise(ulong scenario, float time)
    {
        var x = time / KnotSpacing;
        var k = (long)Math.Floor(x);
        var u = x - k;
        var w = u * u * (3 - 2 * u);
        var value = Knot(scenario, k) * (1 - w) + Knot(scenario, k + 1) * w;
        return value / Math.Sqrt((1 - w) * (1 - w) + w * w);
    }

    private static double Knot(ulong scenario, long knot)
    {
        var h1 = Mix(scenario ^ (ulong)knot * 0x9E3779B97F4A7C15UL ^ (ulong)Seed * 0xC2B2AE3D27D4EB4FUL);
        var h2 = Mix(h1 + 0x165667B19E3779F9UL);
        var u1 = ((h1 >> 11) + 0.5) / 9007199254740992.0;
        var u2 = ((h2 >> 11) + 0.5) / 9007199254740992.0;
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    private static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}