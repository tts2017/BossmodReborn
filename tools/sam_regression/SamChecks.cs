namespace SamRegression;

public enum SamFailureSeverity
{
    HardFail,
    SoftFail,
    CoverageGap
}

public sealed record SamFailure
{
    public required SamFailureSeverity Severity { get; init; }
    public required string ScenarioName { get; init; }
    public int Seed { get; init; }
    public double Time { get; init; }
    public required string Reason { get; init; }
    public required string State { get; init; }
    public required IReadOnlyList<SamActionLog> Last20Actions { get; init; }
    public string SuspectedLogic { get; init; } = "";
}

public sealed record SamScenarioResult
{
    public required SamScenario Scenario { get; init; }
    public List<SamActionLog> Actions { get; init; } = [];
    public List<SamFailure> HardFailures { get; init; } = [];
    public List<SamFailure> SoftFailures { get; init; } = [];
    public List<SamFailure> CoverageGaps { get; init; } = [];
    public bool Passed => HardFailures.Count == 0;
}

public sealed record SamRegressionSummary
{
    public string ToolVersion { get; init; } = "sam-regression-v1";
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Scenarios { get; init; }
    public int Passed { get; init; }
    public int HardFail { get; init; }
    public int SoftFail { get; init; }
    public int CoverageGap { get; init; }
}

public sealed record SamRegressionOutput
{
    public SamRegressionSummary Summary { get; init; } = new();
    public List<SamScenarioResult> Results { get; init; } = [];
}

public static class SamChecks
{
    private static readonly SamAction[] Fast208OpenerGcdSequence =
    [
        SamAction.Gekko,
        SamAction.Kasha,
        SamAction.Yukikaze,
        SamAction.TendoSetsugekka,
        SamAction.TendoKaeshiSetsugekka,
        SamAction.Gekko,
        SamAction.Higanbana,
        SamAction.OgiNamikiri,
        SamAction.KaeshiNamikiri,
        SamAction.Kasha,
        SamAction.Gekko,
        SamAction.Gyofu,
        SamAction.Yukikaze,
        SamAction.TendoSetsugekka,
        SamAction.TendoKaeshiSetsugekka
    ];

    private static readonly SamAction[] Fast208OddBurstGcdSequence =
    [
        SamAction.KaeshiSetsugekka,
        SamAction.TendoSetsugekka,
        SamAction.Gekko,
        SamAction.Higanbana,
        SamAction.TendoKaeshiSetsugekka,
        SamAction.Kasha,
        SamAction.Gekko,
        SamAction.Gyofu,
        SamAction.Yukikaze,
        SamAction.MidareSetsugekka
    ];

    private static readonly SamAction[] Fast208EvenBurstGcdSequence =
    [
        SamAction.KaeshiSetsugekka,
        SamAction.TendoSetsugekka,
        SamAction.TendoKaeshiSetsugekka,
        SamAction.Gekko,
        SamAction.Higanbana,
        SamAction.OgiNamikiri,
        SamAction.KaeshiNamikiri,
        SamAction.Kasha,
        SamAction.Gekko,
        SamAction.Gyofu,
        SamAction.Yukikaze,
        SamAction.MidareSetsugekka
    ];

    internal static void EvaluateStep(SamScenario scenario, SamState state, SamScenarioResult result)
    {
        if (state.Targetable && (state.Melee || state.RangedOnly) && state.Time - state.LastGcdAt > scenario.GcdLength * 2.05)
            Hard(scenario, state, result, "ターゲット殴れる状態で2GCD以上GCDが出ない", "Exec fallback / Enpi fallback");

        if (state.SameInvalidActions >= 2 && state.Targetable)
            Hard(scenario, state, result, "同じ無効アクションを連続で選び続ける、またはGCD候補がNone", "GCD candidate selection");

        if (state.Kenki > 100)
            Hard(scenario, state, result, "剣気が100を超えた", "plannedKenki / Kenki gauge handling");

        if (scenario.Higanbana != SamHiganbanaStrategy.Force)
        {
            TrackMissingBuff(scenario, state, result, ref state.DamageBuffMissingSince, state.DamageBuff, "風月が長時間切れる", "GetHakazeComboAction / GetMeikyoAction");
            TrackMissingBuff(scenario, state, result, ref state.HasteBuffMissingSince, state.HasteBuff, "風花が長時間切れる", "GetHakazeComboAction / GetMeikyoAction");
        }

        if (scenario.Higanbana != SamHiganbanaStrategy.Delay && scenario.Level >= 30 && state.Targetable && state.Melee)
            TrackDotGap(scenario, state, result);

        if (state.MeikyoLeft <= 0 && state.MeikyoStacks > 0)
            Hard(scenario, state, result, "明鏡スタックを消費できず効果切れする", "UseMeikyo / GetMeikyoAction");

        CheckExpiredReady(scenario, state, result);
        CheckBurstKenkiOrder(scenario, state, result);
    }

    internal static void EvaluateFinal(SamScenario scenario, SamState state, SamScenarioResult result)
    {
        var coveredActions = result.Actions.Select(a => a.Action).ToHashSet();
        if (scenario.Level >= 100 && scenario.TargetPattern == SamTargetPattern.SingleTarget && scenario.RaidBuffProfile != SamRaidBuffProfile.None)
        {
            if (scenario.Namikiri != SamNamikiriStrategy.Delay && !coveredActions.Contains(SamAction.OgiNamikiri) && !OgiReadyForActiveBuffAtEnd(scenario, state))
                Soft(scenario, state, result, "2分バースト内または戦闘中に奥義波切が入らない", "ShouldUseOgiNamikiriNow");
            if (!coveredActions.Contains(SamAction.HissatsuSenei) && !coveredActions.Contains(SamAction.HissatsuGuren))
                Soft(scenario, state, result, "2分バースト内に閃影/紅蓮が入らない", "OGCD Senei/Guren block");
        }

        if (scenario.GcdRoute == SamGcdRoute.GCD208 && scenario.Level >= 100)
            CheckFast208Order(scenario, state, result);
    }

    public static SamRegressionOutput BuildOutput(IReadOnlyList<SamScenarioResult> results, bool includeCoverage)
    {
        var allResults = results.ToList();
        var coverage = includeCoverage ? EvaluateCoverage(allResults) : [];
        if (coverage.Count > 0 && allResults.Count > 0)
            allResults[0].CoverageGaps.AddRange(coverage);

        return new SamRegressionOutput
        {
            Summary = new SamRegressionSummary
            {
                Scenarios = allResults.Count,
                Passed = allResults.Count(r => r.Passed),
                HardFail = allResults.Sum(r => r.HardFailures.Count),
                SoftFail = allResults.Sum(r => r.SoftFailures.Count),
                CoverageGap = allResults.Sum(r => r.CoverageGaps.Count)
            },
            Results = allResults
        };
    }

    private static void TrackMissingBuff(SamScenario scenario, SamState state, SamScenarioResult result, ref double missingSince, double left, string reason, string suspected)
    {
        if (scenario.Level < 50 || !state.Targetable || !state.Melee || state.Time < 35)
            return;

        if (left > 0)
        {
            missingSince = -1;
            return;
        }

        if (missingSince < 0)
            missingSince = state.Time;
        if (state.Time - missingSince > 18)
            Hard(scenario, state, result, reason, suspected);
    }

    private static void TrackDotGap(SamScenario scenario, SamState state, SamScenarioResult result)
    {
        if (state.Time < 30 || scenario.Downtimes.Any(d => d.Contains(state.Time) || Math.Abs(d.End - state.Time) < 12))
            return;

        if (state.HiganbanaDot > 0)
        {
            state.DotMissingSince = -1;
            return;
        }

        if (state.DotMissingSince < 0)
            state.DotMissingSince = state.Time;
        if (state.Time - state.DotMissingSince > 20)
            Hard(scenario, state, result, "彼岸花更新が可能なのに長時間切れる", "CanUseHiganbanaNow / UseIaijutsu");
    }

    private static void CheckExpiredReady(SamScenario scenario, SamState state, SamScenarioResult result)
    {
        var last = state.Actions.LastOrDefault();
        if (last == null)
            return;

        if (last.State.OgiReady <= 0 && last.Action != SamAction.OgiNamikiri && last.State.OgiReady > 0)
            Hard(scenario, state, result, "奥義波切 Ready を効果切れさせる", "ShouldUseOgiNamikiriNow");
        if (last.State.KaeshiNamikiriReady <= 0 && last.Action != SamAction.KaeshiNamikiri && last.State.KaeshiNamikiriReady > 0)
            Hard(scenario, state, result, "返し波切 Ready を効果切れさせる", "ShouldUseKaeshiNamikiriNow");
        if (last.State.TsubameReady <= 0 && last.State.TsubameAction != SamRepeat.None && last.Action is not SamAction.KaeshiGoken and not SamAction.KaeshiSetsugekka and not SamAction.TendoKaeshiGoken and not SamAction.TendoKaeshiSetsugekka)
            Hard(scenario, state, result, "返し雪月花/返し五剣 Ready を効果切れさせる", "ShouldUseTsubameNow");
        if (last.State.ZanshinReady <= 0 && last.Action != SamAction.Zanshin && last.State.ZanshinReady > 0)
            Hard(scenario, state, result, "残心 Ready を効果切れさせる", "ShouldUseZanshinNow");
    }

    private static void CheckBurstKenkiOrder(SamScenario scenario, SamState state, SamScenarioResult result)
    {
        if (SamTimeline.RaidBuffsLeft(scenario, state.Time) <= 0)
            return;

        var sameTimeSpend = state.Actions
            .Where(a => Math.Abs(a.Time - state.Time) < 0.001)
            .Where(a => a.Action is SamAction.HissatsuSenei or SamAction.HissatsuGuren or SamAction.Zanshin or SamAction.HissatsuShinten or SamAction.HissatsuKyuten)
            .ToList();
        var firstLowPrioritySpend = sameTimeSpend.FindIndex(a => a.Action is SamAction.HissatsuShinten or SamAction.HissatsuKyuten);
        if (firstLowPrioritySpend >= 0 && sameTimeSpend.Skip(firstLowPrioritySpend + 1).Any(a => a.Action is SamAction.HissatsuSenei or SamAction.HissatsuGuren or SamAction.Zanshin))
            Hard(scenario, state, result, "バースト中の剣気優先度が 閃影/紅蓮 > 残心 > 震天/九天 になっていない", "OGCD plannedKenki priority");
    }

    private static bool OgiReadyForActiveBuffAtEnd(SamScenario scenario, SamState state)
        => state.OgiLeft > 0 && SamTimeline.RaidBuffsLeft(scenario, state.Time) > 0;

    private static void CheckFast208Order(SamScenario scenario, SamState state, SamScenarioResult result)
    {
        if (!ShouldCheckFast208Sequence(scenario))
            return;

        var opener = result.Actions.Where(a => a.Time < 35 && a.Kind == SamActionKind.GCD).Select(a => a.Action).ToList();
        var higan = opener.IndexOf(SamAction.Higanbana);
        var tendo = opener.IndexOf(SamAction.TendoSetsugekka);
        var ogi = opener.IndexOf(SamAction.OgiNamikiri);
        if (higan >= 0 && tendo >= 0 && higan < tendo)
            Soft(scenario, state, result, "2.08ルートで天道前に彼岸花が入る", "ShouldDelayHiganbanaForFast208OpenerOrder");
        if (ogi >= 0 && higan >= 0 && ogi < higan)
            Soft(scenario, state, result, "2.08ルートで奥義が彼岸花より前に入る", "ShouldDelayNamikiriForFast208OpenerHiganbana");

        CheckExactGcdPrefix(scenario, state, result, 0, 35, Fast208OpenerGcdSequence, "2.08開幕GCD順序", "Fast208 opener route");
        if (scenario.Duration >= 92)
            CheckGcdSubsequence(scenario, state, result, 56, 92, Fast208OddBurstGcdSequence, "2.08奇数バーストGCD順序", "Fast208 odd burst route");
        if (scenario.Duration >= 150)
            CheckGcdSubsequence(scenario, state, result, 116, 150, Fast208EvenBurstGcdSequence, "2.08偶数バーストGCD順序", "Fast208 even burst route");
    }

    private static bool ShouldCheckFast208Sequence(SamScenario scenario)
        => scenario.GcdRoute == SamGcdRoute.GCD208
            && scenario.Level >= 100
            && scenario.OpenerBurst == SamOpenerBurst.Normal
            && scenario.TargetPattern == SamTargetPattern.SingleTarget
            && scenario.Higanbana == SamHiganbanaStrategy.Auto
            && scenario.Tsubame == SamTsubameStrategy.Auto
            && scenario.Namikiri == SamNamikiriStrategy.Auto
            && scenario.Meikyo == SamMeikyoStrategy.Auto
            && scenario.Downtimes.Count == 0;

    private static void CheckExactGcdPrefix(SamScenario scenario, SamState state, SamScenarioResult result, double start, double end, IReadOnlyList<SamAction> expected, string label, string suspected)
    {
        var actual = result.Actions
            .Where(a => a.Kind == SamActionKind.GCD && a.Time >= start && a.Time < end)
            .Select(a => a.Action)
            .Take(expected.Count)
            .ToList();

        if (actual.Count < expected.Count || !actual.SequenceEqual(expected))
            Soft(scenario, state, result, $"{label}が期待順と一致しない expected=[{string.Join(", ", expected)}] actual=[{string.Join(", ", actual)}]", suspected);
    }

    private static void CheckGcdSubsequence(SamScenario scenario, SamState state, SamScenarioResult result, double start, double end, IReadOnlyList<SamAction> expected, string label, string suspected)
    {
        var actual = result.Actions
            .Where(a => a.Kind == SamActionKind.GCD && a.Time >= start && a.Time < end)
            .Select(a => a.Action)
            .ToList();

        var cursor = 0;
        foreach (var action in actual)
        {
            if (cursor < expected.Count && action == expected[cursor])
                ++cursor;
        }

        if (cursor < expected.Count)
            Soft(scenario, state, result, $"{label}が期待順と一致しない missing={expected[cursor]} expected=[{string.Join(", ", expected)}] actual=[{string.Join(", ", actual)}]", suspected);
    }

    private static List<SamFailure> EvaluateCoverage(IReadOnlyList<SamScenarioResult> results)
    {
        var scenarios = results.Select(r => r.Scenario).ToList();
        var gaps = new List<string>();
        AddMissing(gaps, "duration", new[] { 360d, 480d, 600d, 720d }, scenarios.Select(s => s.Duration));
        AddMissing(gaps, "level", new[] { 50, 60, 70, 80, 90, 100 }, scenarios.Select(s => s.Level));
        AddMissing(gaps, "openerBurst", Enum.GetValues<SamOpenerBurst>(), scenarios.Select(s => s.OpenerBurst));
        AddMissing(gaps, "gcdRoute", Enum.GetValues<SamGcdRoute>(), scenarios.Select(s => s.GcdRoute));
        AddMissing(gaps, "targetPattern", Enum.GetValues<SamTargetPattern>(), scenarios.Select(s => s.TargetPattern));
        AddMissing(gaps, "raidBuffProfile", Enum.GetValues<SamRaidBuffProfile>(), scenarios.Select(s => s.RaidBuffProfile));
        AddMissing(gaps, "raidBuffOffset", new[] { -5d, -2d, 0d, 2d, 5d, 10d }, scenarios.Select(s => s.RaidBuffOffset));
        AddMissing(gaps, "potion", Enum.GetValues<SamPotionStrategy>(), scenarios.Select(s => s.Potion));
        AddMissing(gaps, "trueNorth", Enum.GetValues<SamTrueNorthStrategy>(), scenarios.Select(s => s.TrueNorth));
        AddMissing(gaps, "higanbana", Enum.GetValues<SamHiganbanaStrategy>(), scenarios.Select(s => s.Higanbana));
        AddMissing(gaps, "tsubame", Enum.GetValues<SamTsubameStrategy>(), scenarios.Select(s => s.Tsubame));
        AddMissing(gaps, "namikiri", Enum.GetValues<SamNamikiriStrategy>(), scenarios.Select(s => s.Namikiri));
        AddMissing(gaps, "meikyo", Enum.GetValues<SamMeikyoStrategy>(), scenarios.Select(s => s.Meikyo));

        return gaps.Select(g => new SamFailure
        {
            Severity = SamFailureSeverity.CoverageGap,
            ScenarioName = results.FirstOrDefault()?.Scenario.Name ?? "none",
            Time = 0,
            Reason = g,
            State = "coverage",
            Last20Actions = []
        }).ToList();
    }

    private static void AddMissing<T>(List<string> gaps, string axis, IEnumerable<T> expected, IEnumerable<T> actual)
    {
        var actualSet = actual.ToHashSet();
        foreach (var value in expected)
            if (!actualSet.Contains(value))
                gaps.Add($"CoverageGap: {axis} missing {value}");
    }

    private static void Hard(SamScenario scenario, SamState state, SamScenarioResult result, string reason, string suspected)
    {
        if (result.HardFailures.Any(f => f.Reason == reason))
            return;
        result.HardFailures.Add(Failure(SamFailureSeverity.HardFail, scenario, state, reason, suspected));
    }

    private static void Soft(SamScenario scenario, SamState state, SamScenarioResult result, string reason, string suspected)
    {
        if (result.SoftFailures.Any(f => f.Reason == reason))
            return;
        result.SoftFailures.Add(Failure(SamFailureSeverity.SoftFail, scenario, state, reason, suspected));
    }

    private static SamFailure Failure(SamFailureSeverity severity, SamScenario scenario, SamState state, string reason, string suspected)
        => new()
        {
            Severity = severity,
            ScenarioName = scenario.Name,
            Seed = scenario.Seed,
            Time = state.Time,
            Reason = reason,
            State = StateSummary(state.Snapshot(scenario)),
            Last20Actions = state.Actions.TakeLast(20).ToList(),
            SuspectedLogic = suspected
        };

    private static string StateSummary(SamStateSnapshot s)
        => $"t={s.Time:0.00} kenki={s.Kenki} med={s.Meditation} sen={s.Sen} combo={s.Combo} meikyo={s.MeikyoStacks}/{s.MeikyoCharges} buffs={s.DamageBuff:0.0}/{s.HasteBuff:0.0} dot={s.Dot:0.0} tsubame={s.TsubameAction}:{s.TsubameReady:0.0} ogi={s.OgiReady:0.0}/{s.KaeshiNamikiriReady:0.0} tendo={s.TendoReady:0.0} zanshin={s.ZanshinReady:0.0} target={s.Targetable}/{s.Melee}/{s.RangedOnly} enemies={s.EnemyCount} buffsIn={s.RaidBuffsIn:0.0} buffsLeft={s.RaidBuffsLeft:0.0}";
}
