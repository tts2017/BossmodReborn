using BossMod;

internal static class Models
{
    private static FightTimeConfig C(FightTimeModel m, float w = 30, float hl = 0, float pace = 0, float minSpan = 8)
        => new() { Model = m, WindowSeconds = w, HalfLifeSeconds = hl, PriorPaceExponent = pace, MinSpanSeconds = minSpan, LowerMult = [.. Enumerable.Repeat(1f, FightTimeConfig.BoundKinds * 7)], UpperMult = [.. Enumerable.Repeat(1f, FightTimeConfig.BoundKinds * 7)] };

    private static FightTimeConfig H(float wmax, float k, float s0, float w = 120)
    {
        var c = C(FightTimeModel.Hybrid, w);
        c.HybridMaxPriorWeight = wmax;
        c.HybridPriorCountHalf = k;
        c.HybridSpreadScale = s0;
        return c;
    }

    public static IEnumerable<Eval.ModelSpec> All()
    {
        yield return new("M5_hybrid_summaryprior", () => H(0.95f, 0.5f, 2), Eval.PriorMode.Summary);
        yield return new("M5_hybrid_summarypast", () => H(0.95f, 0.5f, 2), Eval.PriorMode.SummaryPast);
        yield return new("M4_prior_summary", () => C(FightTimeModel.Prior), Eval.PriorMode.Summary);
        yield return new("M4_prior_summarypast", () => C(FightTimeModel.Prior), Eval.PriorMode.SummaryPast);
        foreach (var cap in new[] { 1, 2, 3, 5, 10 })
            yield return new($"M5_hybrid_cap{cap}_lopo", () => H(0.95f, 0.5f, 2), Eval.PriorMode.Lopo, false, cap);
        foreach (var cap in new[] { 2, 3, 5 })
            yield return new($"M5_hybrid_cap{cap}_past", () => H(0.95f, 0.5f, 2), Eval.PriorMode.Past, false, cap);
        yield return new("M0_slope5s", () => C(FightTimeModel.Slope5s));
        yield return new("M1_average", () => C(FightTimeModel.AverageRate));
        foreach (var w in new[] { 10f, 20f, 30f, 40f, 60f })
            yield return new($"M2_window{w:0}", () => C(FightTimeModel.WindowRate, w));
        foreach (var w in new[] { 10f, 20f, 30f, 40f, 60f, 90f, 120f, 180f, 300f, 1e6f })
            yield return new($"M3_window{w:0}", () => C(FightTimeModel.PhaseAware, w));
        foreach (var hl in new[] { 10f, 20f, 40f })
            yield return new($"M3_ewma{hl:0}", () => C(FightTimeModel.PhaseAware, 30, hl));
        yield return new("M3_w120_noclock", () => { var c = C(FightTimeModel.PhaseAware, 120); c.UseActiveClock = false; return c; });
        foreach (var js in new[] { 3f, 5f, 10f, 20f, 40f })
            yield return new($"M3_w120_jumprate{js:0}", () => { var c = C(FightTimeModel.PhaseAware, 120); c.JumpRateSeconds = js; return c; });
        yield return new("M3_w120_jump_frac_only", () => { var c = C(FightTimeModel.PhaseAware, 120); c.JumpRateSeconds = 0; return c; });
        yield return new("M3_w120_nojump", () => { var c = C(FightTimeModel.PhaseAware, 120); c.JumpFraction = 100; return c; });
        yield return new("M3_w120_bossonly", () => C(FightTimeModel.PhaseAware, 120), Eval.PriorMode.None, true);
        yield return new("M3_window30_bossonly", () => C(FightTimeModel.PhaseAware, 30), Eval.PriorMode.None, true);
        foreach (var (mode, tag) in new[] { (Eval.PriorMode.Lopo, "lopo"), (Eval.PriorMode.Loro, "loro"), (Eval.PriorMode.Past, "past") })
        {
            yield return new($"M4_prior_{tag}", () => C(FightTimeModel.Prior), mode);
            yield return new($"M4_prior_pace05_{tag}", () => C(FightTimeModel.Prior, pace: 0.5f), mode);
            yield return new($"M4_prior_pace1_{tag}", () => C(FightTimeModel.Prior, pace: 1f), mode);
            yield return new($"M5_hybrid_{tag}", () => H(0.95f, 1, 1), mode);
            yield return new($"M5_hybrid_k05s2_{tag}", () => H(0.95f, 0.5f, 2), mode);
            yield return new($"M5_hybrid_k2s05_{tag}", () => H(0.95f, 2, 0.5f), mode);
            yield return new($"M5_hybrid_w1_{tag}", () => H(1f, 0.5f, 2), mode);
            yield return new($"M5_hybrid_w60_{tag}", () => H(0.95f, 1, 1, 60), mode);
        }
    }
}
