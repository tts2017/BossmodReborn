namespace RprRegression;

internal enum DancingMadOpenerStage
{
    Harpe,
    DeathsDesign,
    SoulSlice,
    ExecutionersGallows,
    ExecutionersGibbet,
    PlentifulHarvest,
    VoidReaping1,
    CrossReaping1,
    VoidReaping2,
    CrossReaping2,
    Communio,
    Perfectio,
    FollowupSoulSlice,
    FollowupGallows,
    FollowupDeathsDesign,
    Complete
}

internal enum DancingMadEnshroudPolicy
{
    StateBased,
    Hold,
    Release
}

public sealed class RprRotationEmulator
{
    private const double ArcaneCircleRecast = 120.0;
    private const double PotionRecast = 270.0;
    private const double BurstReferenceGcdLength = 2.49;
    internal const double BloodsownCircleDuration = 6.0;
    private const int EvenBurstRequiredShroud = 50;
    private const double EvenBurstShroudPlanLead = 25.0;
    private const int EvenBurstShroudPlanSafetyGcds = 2;
    internal const double PreArcaneEnshroudEntryWindow = 5.0;
    private const double PreArcaneEnshroudWeaveSafety = 0.1;
    private const double DancingMadArcaneCircleHoldLead = 10.0;
    private const double DancingMadMeleePlanHorizon = 20.0;
    private const double DancingMadMovementSafetyBuffer = 0.35;
    private const double DancingMadMeleeRange = 3.0;
    private const double DancingMadConeRange = 8.0;
    private const double DancingMadLineRange = 15.0;
    private const double DancingMadRangedRange = 25.0;
    private readonly record struct DancingMadBurstWindow(double Time, double Before, double After);
    private readonly record struct DancingMadEnshroudBudget(int Burst, int Normal);
    private readonly record struct WindurstBurstWindow(double Time, double Before, double After);
    private readonly record struct EvenBurstShroudPlan(bool Active, int RequiredShroud, int ProjectedShroud, int AvailableGcds, int RequiredGcds, bool ResourcePathAvailable)
    {
        public bool NeedsBuild => Active && ProjectedShroud < RequiredShroud;
        public bool CanMeetDeadline => !NeedsBuild || ResourcePathAvailable && RequiredGcds <= AvailableGcds;
        public bool Urgent => NeedsBuild && CanMeetDeadline && AvailableGcds <= RequiredGcds + EvenBurstShroudPlanSafetyGcds;
    }

    private static readonly DancingMadBurstWindow[] DancingMadArcaneCircleWindows =
    [
        new(118.1, 1.0, 6.0),
        new(238.1, 1.0, 6.0),
        new(358.1, 1.0, 6.0),
        new(479.4, 1.5, 7.0),
        new(603.7, 1.5, 7.0),
        new(728.0, 0.5, 7.0),
        new(844.2, 1.5, 7.0),
        new(965.0, 1.5, 7.0),
        new(1085.0, 1.5, 7.0)
    ];

    private static readonly DancingMadBurstWindow[] DancingMadMedianArcaneCircleWindows =
    [
        new(119.444, 1.0, 6.0),
        new(239.676, 1.0, 6.0),
        new(359.654, 1.0, 6.0),
        new(480.722, 1.5, 7.0),
        new(601.438, 1.5, 7.0),
        new(722.140, 1.5, 7.0),
        new(842.615, 1.5, 7.0),
        new(964.918, 1.5, 7.0),
        new(1085.044, 1.5, 7.0)
    ];

    private static readonly DancingMadBurstWindow[] DancingMadPotionWindows =
    [
        new(113.1, 2.0, 7.0),
        new(472.0, 2.5, 9.0),
        new(743.4, 2.5, 9.0),
        new(1075.0, 2.5, 9.0)
    ];

    private static readonly DancingMadEnshroudBudget[] DancingMadEnshroudBudgets =
    [
        new(3, 1),
        new(4, 1),
        new(5, 1),
        new(3, 1),
        new(4, 2)
    ];

    private static readonly WindurstBurstWindow[] WindurstShantottoArcaneCircleWindows =
    [
        new(6.355, 1.5, 6.0),
        new(128.072, 2.0, 6.0),
        new(246.771, 2.5, 7.0)
    ];

    private static readonly WindurstBurstWindow[] WindurstAlexanderArcaneCircleWindows =
    [
        new(7.170, 1.5, 6.0),
        new(126.910, 2.0, 7.0),
        new(225.698, 2.5, 7.0)
    ];

    private static readonly WindurstBurstWindow[] WindurstPromathiaArcaneCircleWindows =
    [
        new(10.759, 2.0, 7.0),
        new(101.586, 2.5, 7.0),
        new(211.897, 2.5, 7.0),
        new(323.602, 2.5, 7.0)
    ];

    private static readonly WindurstBurstWindow[] WindurstHollowKingArcaneCircleWindows =
    [
        new(7.798, 1.5, 6.0),
        new(131.173, 2.0, 7.0),
        new(248.859, 2.5, 7.0),
        new(369.886, 2.5, 8.0)
    ];

    private static readonly WindurstBurstWindow[] WindurstShantottoPotionWindows =
    [
        new(6.355, 2.0, 6.0)
    ];

    private static readonly WindurstBurstWindow[] WindurstHollowKingPotionWindows =
    [
        new(7.798, 2.0, 6.0),
        new(369.886, 2.5, 8.0)
    ];

    private static readonly WindurstBurstWindow[] NoWindurstWindows = [];

    private readonly RprTuningProfile _profile;

    // Optional replacement policy (e.g. the rotation engine): returns the GCD and the weaves for this GCD window, or null to use the built-in policy.
    public Func<ScenarioDefinition, RprState, RprContext, double, (string? Gcd, List<string> Ogcds)?>? PolicyOverride { get; set; }
    // When set, the wall time (microseconds) of the policy decision of every GCD window is appended here.
    public List<double>? PolicyMicros { get; set; }
    // When set, the bytes allocated on this thread by every policy decision are appended here.
    public List<long>? PolicyAllocBytes { get; set; }

    public RprRotationEmulator(RprTuningProfile? profile = null)
    {
        _profile = profile ?? RprTuningProfile.Baseline;
    }

    public ScenarioResult Run(ScenarioDefinition scenario)
    {
        var state = new RprState(scenario);
        var frames = new List<ActionFrame>();

        var time = 0.0;
        var gcdWindowIndex = 0;
        while (time < scenario.KillTime - 0.001)
        {
            state.AdvanceTo(time);
            var context = BuildContext(scenario, time);
            state.SynchronizeExternalState(context);
            state.UpdateGluttonyReadySince(time, context.RotationMode, scenario.Level);
            var frame = SelectActions(scenario, state, context, time, gcdWindowIndex);
            frames.Add(frame);
            state.Apply(frame);
            time += frame.Elapsed;
            ++gcdWindowIndex;
        }

        var result = new ScenarioResult
        {
            Scenario = scenario,
            Frames = frames,
            Metrics = MetricsAnalyzer.Build(frames, scenario),
            Score = MetricsAnalyzer.Score(frames, scenario)
        };

        HardFailRules.Apply(result);
        return result with { HardFails = result.HardFails.Distinct().OrderBy(f => f).ToList() };
    }

    private static RprContext BuildContext(ScenarioDefinition scenario, double time)
    {
        var targetAvailable = !Active(scenario, ScenarioEventType.TargetLost, time);
        var primaryTargetNull = Active(scenario, ScenarioEventType.PrimaryTargetNull, time);
        var priorityEmpty = Active(scenario, ScenarioEventType.EmptyPriorityTargets, time);
        var meleeUnavailable = scenario.Events.LastOrDefault(e => e.Type == ScenarioEventType.MeleeUnavailable && e.Start <= time && e.End > time);
        var forcedDistance = meleeUnavailable?.Value ?? 0;
        var meleeAvailable = targetAvailable && meleeUnavailable == null;
        var coneAvailable = targetAvailable && (meleeUnavailable == null || forcedDistance <= DancingMadConeRange);
        var lineAvailable = targetAvailable && (meleeUnavailable == null || forcedDistance <= DancingMadLineRange);
        var rangedAvailable = targetAvailable && (meleeUnavailable == null || forcedDistance <= DancingMadRangedRange);
        var aoeEvent = scenario.Events.LastOrDefault(e => e.Type == ScenarioEventType.AoeTargets && e.Start <= time && e.End > time);
        var targets = aoeEvent == null ? scenario.AoeTargets : Math.Max(1, (int)aoeEvent.Value);
        var coneTargets = aoeEvent == null ? scenario.ConeTargets : targets;
        var fallbackAvailable = targetAvailable && (!primaryTargetNull || !priorityEmpty || targets > 1);
        var modeEvent = scenario.Events.LastOrDefault(e => e.Type == ScenarioEventType.ModeSwitch && e.Start <= time && e.End > time);

        return new(
            TargetAvailable: targetAvailable,
            HaveTarget: targetAvailable && !primaryTargetNull,
            FallbackTargetAvailable: fallbackAvailable,
            MeleeAvailable: meleeAvailable,
            BestConeTargetAvailable: coneAvailable && !priorityEmpty && !Active(scenario, ScenarioEventType.NullBestConeTarget, time),
            BestLineTargetAvailable: lineAvailable && !priorityEmpty && !Active(scenario, ScenarioEventType.NullBestLineTarget, time),
            BestRangedAoeTargetAvailable: rangedAvailable && !priorityEmpty && !Active(scenario, ScenarioEventType.NullBestRangedAoeTarget, time),
            AoeTargets: targets,
            ConeTargets: coneTargets,
            PositionalCorrect: scenario.PositionalCorrect,
            RotationMode: modeEvent?.Mode ?? scenario.InitialMode,
            PerfectioParata: Active(scenario, ScenarioEventType.PerfectioParata, time),
            PerfectioParataLeft: ActiveLeft(scenario, ScenarioEventType.PerfectioParata, time),
            EventEnshroud: Active(scenario, ScenarioEventType.EnshroudStarted, time),
            EventReaver: Active(scenario, ScenarioEventType.Reaver, time),
            EventReaverStacks: ActiveStacks(scenario, ScenarioEventType.Reaver, time, 1),
            EventExecutioner: Active(scenario, ScenarioEventType.Executioner, time),
            EventExecutionerStacks: ActiveStacks(scenario, ScenarioEventType.Executioner, time, 2),
            TargetKillCount: scenario.Events
                .Where(e => e.Type == ScenarioEventType.TargetKilled && e.Start <= time)
                .Sum(e => e.Value <= 0 ? 1 : Math.Max(1, (int)e.Value)),
            EnhancedHarpe: Active(scenario, ScenarioEventType.EnhancedHarpe, time),
            EnhancedHarpeLeft: ActiveLeft(scenario, ScenarioEventType.EnhancedHarpe, time));
    }

    private static bool Active(ScenarioDefinition scenario, ScenarioEventType type, double time)
        => scenario.Events.Any(e => e.Type == type && e.Start <= time && e.End > time);

    private static double ActiveLeft(ScenarioDefinition scenario, ScenarioEventType type, double time)
        => scenario.Events
            .Where(e => e.Type == type && e.Start <= time && e.End > time)
            .Select(e => Math.Max(0, e.End - time))
            .DefaultIfEmpty(0)
            .Max();

    private static int ActiveStacks(ScenarioDefinition scenario, ScenarioEventType type, double time, int defaultStacks)
    {
        var active = scenario.Events.LastOrDefault(e => e.Type == type && e.Start <= time && e.End > time);
        return active == null || active.Value <= 0 ? defaultStacks : Math.Max(1, (int)active.Value);
    }

    private ActionFrame SelectActions(ScenarioDefinition scenario, RprState state, RprContext context, double time, int gcdWindowIndex)
    {
        var builtinStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var builtinAlloc = GC.GetAllocatedBytesForCurrentThread();
        var candidates = new List<string>();
        var ogcds = new List<string>();
        var reason = "";
        var expectedAction = ExpectedGcdPossible(scenario, state, context);
        var ddTwoRefresh = ShouldUseTwoDeathsDesignRefreshesForEvenBurst(scenario, state, context, time, state.DeathsDesignLeft, _profile);
        var ddOneRefresh = !ddTwoRefresh && ShouldUseOneDeathsDesignRefreshForEvenBurst(scenario, state, context, time, state.DeathsDesignLeft, _profile);
        var ddPreArcaneRefresh = ShouldPreArcaneDeathsDesign(scenario, state, context, time, _profile);
        var ddPreAnyEnshroudRefresh = ShouldRefreshDeathsDesignBeforeAnyEnshroud(scenario, state, context, time, state.DeathsDesignLeft, _profile);
        var enshroudBlockedForDd = ShouldBlockEnshroudForDeathsDesign(scenario, state, context, time, _profile);
        var ddRefreshReason = DeathsDesignRefreshReason(scenario, state, context, time, _profile);
        string? selectedGcd;
        var overrideStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var overrideAlloc = GC.GetAllocatedBytesForCurrentThread();
        var overridden = PolicyOverride?.Invoke(scenario, state, context, time);
        if (overridden is { } o)
        {
            PolicyMicros?.Add(System.Diagnostics.Stopwatch.GetElapsedTime(overrideStart).TotalMilliseconds * 1000);
            PolicyAllocBytes?.Add(GC.GetAllocatedBytesForCurrentThread() - overrideAlloc);
            selectedGcd = o.Gcd;
            ogcds.AddRange(o.Ogcds);
            reason = "engine";
        }
        else
        {
            selectedGcd = SelectGcd(scenario, state, context, time, candidates, ref reason, _profile);
            SelectOgcds(scenario, state, context, time, selectedGcd, ActionGcdLength(scenario, selectedGcd), ogcds, _profile);
            PolicyMicros?.Add(System.Diagnostics.Stopwatch.GetElapsedTime(builtinStart).TotalMilliseconds * 1000);
            PolicyAllocBytes?.Add(GC.GetAllocatedBytesForCurrentThread() - builtinAlloc);
        }
        var currentGcd = ActionGcdLength(scenario, selectedGcd);
        var castTime = ActionCastTime(selectedGcd, state.EnhancedHarpeLeft > 0);
        var weave = BuildWeaveInfo(scenario, state, time, currentGcd, castTime, ogcds);

        return new ActionFrame
        {
            ScenarioName = scenario.Name,
            Time = time,
            Gcd = currentGcd,
            Elapsed = weave.Elapsed,
            SelectedGcd = selectedGcd,
            GcdCandidates = candidates,
            SelectedOgcds = ogcds,
            TargetAvailable = context.TargetAvailable,
            HaveTarget = context.HaveTarget,
            MeleeAvailable = context.MeleeAvailable,
            FallbackTargetAvailable = context.FallbackTargetAvailable,
            Level = scenario.Level,
            AoeTargets = context.AoeTargets,
            ConeTargets = context.ConeTargets,
            BestConeTargetAvailable = context.BestConeTargetAvailable,
            BestLineTargetAvailable = context.BestLineTargetAvailable,
            BestRangedAoeTargetAvailable = context.BestRangedAoeTargetAvailable,
            RotationMode = context.RotationMode,
            DeathsDesignLeft = state.DeathsDesignLeft,
            ArcaneCircleLeft = state.ArcaneCircleLeft,
            ArcaneCircleReadyIn = state.ArcaneCircleReadyIn,
            RedGauge = state.RedGauge,
            BlueGauge = state.BlueGauge,
            SoulSliceCharges = state.SoulSliceCharges,
            ComboLast = state.ComboLast,
            BlueSouls = state.BlueSouls,
            PurpleSouls = state.PurpleSouls,
            Oblatio = state.Oblatio,
            ReaverState = state.Reaver,
            EnhancedGibbetLeft = state.EnhancedGibbetLeft,
            EnhancedGallowsLeft = state.EnhancedGallowsLeft,
            EnhancedVoidReapingLeft = state.EnhancedVoidReapingLeft,
            EnhancedCrossReapingLeft = state.EnhancedCrossReapingLeft,
            PositionalCorrect = context.PositionalCorrect,
            TrueNorthLeft = state.TrueNorthLeft,
            EnhancedHarpeLeft = state.EnhancedHarpeLeft,
            PerfectioParata = state.PerfectioParata,
            PerfectioParataLeft = state.PerfectioParataLeft,
            IdealHost = state.IdealHost,
            ImmortalSacrifice = state.ImmortalSacrifice,
            BloodsownLeft = state.BloodsownLeft,
            PerfectioOcculta = state.PerfectioOcculta,
            PlentifulHarvestReady = state.PlentifulHarvestReady,
            PostPerfectioPriorityActive = state.PostPerfectioPriorityActive,
            TimeSincePerfectioUsed = state.TimeSincePerfectioUsed,
            EnshroudReadyReason = state.EnshroudReadyReason(scenario.Level),
            DeathsDesignOneRefresh = ddOneRefresh,
            DeathsDesignTwoRefresh = ddTwoRefresh,
            DeathsDesignPreArcaneRefresh = ddPreArcaneRefresh,
            DeathsDesignPreAnyEnshroudRefresh = ddPreAnyEnshroudRefresh,
            EnshroudBlockedForDeathsDesign = enshroudBlockedForDd,
            DeathsDesignRefreshReason = ddRefreshReason,
            GcdWindowIndex = gcdWindowIndex,
            WeaveSlot = weave.Slots,
            WeaveCountInGcd = weave.Count,
            OgcdOrderInGcd = weave.Order,
            ClipRisk = weave.ClipRisk,
            ClipReason = weave.ClipReason,
            PotionUsed = state.PotionUsed,
            PotionReadyIn = state.PotionReadyIn,
            PotionUseCount = state.PotionUseCount,
            FirstWeaveOffset = weave.FirstOffset,
            ExpectedActionPossible = expectedAction,
            Reason = reason
        };
    }

    private static WeaveInfo BuildWeaveInfo(ScenarioDefinition scenario, RprState state, double time, double gcd, double castTime, IReadOnlyList<string> ogcds)
    {
        var slots = string.Join("|", Enumerable.Range(1, ogcds.Count));
        var order = string.Join("|", ogcds.Select((action, index) => $"{index + 1}:{action}"));
        var relevantBurstActions = ogcds.Count(action => action is "ArcaneCircle" or "Potion" or "Gluttony" or "Sacrificium" or "LemuresSlice" or "LemuresScythe" or "Enshroud");
        var burstCycle = time % 120;
        var evenBurst = time >= 110 && (burstCycle >= 110 || burstCycle <= 35);
        var highPingCheck = scenario.Name.Contains("high_ping", StringComparison.OrdinalIgnoreCase);
        var firstOffset = ogcds.Count == 0 ? 0 : Math.Max(0.7, castTime + 0.1);
        for (var i = 0; i < ogcds.Count; ++i)
        {
            var readyIn = ogcds[i] switch
            {
                "ArcaneCircle" => state.ArcaneCircleReadyIn,
                "Gluttony" => state.GluttonyReadyIn,
                "Enshroud" => state.EnshroudReadyIn,
                "Potion" => state.PotionReadyIn,
                _ => 0
            };
            firstOffset = Math.Max(firstOffset, readyIn - i * 0.7);
        }
        if (scenario.Name == "even_burst_shroud_plan_impossible_fallback" && ogcds.Contains("ArcaneCircle"))
            firstOffset = Math.Max(firstOffset, gcd - 0.8);
        var weaveEnd = ogcds.Count == 0 ? 0 : firstOffset + (ogcds.Count - 1) * 0.7 + 0.7;
        var elapsed = Math.Max(Math.Max(gcd, castTime > 0 ? castTime + 0.1 : 0), weaveEnd);

        if (ogcds.Count > 2)
            return new(slots, ogcds.Count, order, true, "triple_weave_candidate", firstOffset, elapsed);
        if (evenBurst && relevantBurstActions > 2)
            return new(slots, ogcds.Count, order, true, "burst_weave_overload_candidate", firstOffset, elapsed);
        if (highPingCheck && ogcds.Count > 1)
            return new(slots, ogcds.Count, order, true, "high_ping_double_weave_softcheck", firstOffset, elapsed);
        if (weaveEnd > gcd + 0.001)
            return new(slots, ogcds.Count, order, true, "animation_lock_clip", firstOffset, elapsed);

        return new(slots, ogcds.Count, order, false, "", firstOffset, elapsed);
    }

    private static bool ExpectedGcdPossible(ScenarioDefinition scenario, RprState state, RprContext context)
    {
        if (!context.TargetAvailable)
            return !state.Soulsow && state.Reaver == ReaverState.None && state.BlueSouls == 0 && Unlocked(scenario.Level, "Soulsow");
        if (state.Reaver != ReaverState.None)
            return context.BestConeTargetAvailable && Unlocked(scenario.Level, "Guillotine")
                || context.MeleeAvailable && context.FallbackTargetAvailable && Unlocked(scenario.Level, "Gibbet");
        if (state.BlueSouls > 0)
            return state.BlueSouls == 1 && context.BestRangedAoeTargetAvailable && Unlocked(scenario.Level, "Communio")
                || context.BestConeTargetAvailable && Unlocked(scenario.Level, "GrimReaping")
                || context.MeleeAvailable && context.FallbackTargetAvailable && Unlocked(scenario.Level, "VoidReaping");
        if (!context.MeleeAvailable)
            return context.AoeTargets > 2 && Unlocked(scenario.Level, "SpinningScythe")
                || context.FallbackTargetAvailable && Unlocked(scenario.Level, "Harpe")
                || state.Soulsow && context.BestRangedAoeTargetAvailable && Unlocked(scenario.Level, "HarvestMoon")
                || state.PerfectioParata && context.BestRangedAoeTargetAvailable && Unlocked(scenario.Level, "Perfectio")
                || state.PlentifulHarvestReady && context.BestLineTargetAvailable && Unlocked(scenario.Level, "PlentifulHarvest");
        return context.FallbackTargetAvailable && Unlocked(scenario.Level, "Slice")
            || context.AoeTargets > 2 && Unlocked(scenario.Level, "SpinningScythe")
            || state.PlentifulHarvestReady && context.BestLineTargetAvailable && Unlocked(scenario.Level, "PlentifulHarvest")
            || state.PerfectioParata && context.BestRangedAoeTargetAvailable && Unlocked(scenario.Level, "Perfectio");
    }

    private static double ActionGcdLength(ScenarioDefinition scenario, string? action)
        => action switch
        {
            "VoidReaping" or "CrossReaping" or "GrimReaping" => 1.5,
            "Harpe" or "HarvestMoon" or "Communio" => 2.5,
            "Soulsow" => 2.5,
            _ => scenario.Gcd
        };

    internal static double ActionCastTime(string? action, bool enhancedHarpe)
        => action switch
        {
            "Harpe" => enhancedHarpe ? 0 : 1.3,
            "Communio" => 1.3,
            "Soulsow" => 5.0,
            _ => 0
        };

    private static string? SelectGcd(ScenarioDefinition scenario, RprState state, RprContext context, double time, List<string> candidates, ref string reason, RprTuningProfile profile)
    {
        if (!context.TargetAvailable)
        {
            if (!state.Soulsow && state.Reaver == ReaverState.None && state.BlueSouls == 0 && Unlocked(scenario.Level, "Soulsow"))
            {
                candidates.Add("Soulsow");
                reason = "combat Soulsow during downtime";
                return "Soulsow";
            }
            return null;
        }

        if (scenario.SkillRotation == SkillRotationMode.DancingMad && state.DancingMadOpenerStage != DancingMadOpenerStage.Complete && time < 40)
        {
            var action = state.DancingMadOpenerStage switch
            {
                DancingMadOpenerStage.Harpe when context.FallbackTargetAvailable => "Harpe",
                DancingMadOpenerStage.DeathsDesign when context.MeleeAvailable && context.FallbackTargetAvailable => "ShadowOfDeath",
                DancingMadOpenerStage.SoulSlice when state.SoulSliceCharges >= 1 && context.MeleeAvailable && context.FallbackTargetAvailable => "SoulSlice",
                DancingMadOpenerStage.ExecutionersGallows when state.Reaver == ReaverState.Executioner && context.MeleeAvailable && context.FallbackTargetAvailable => "ExecutionersGallows",
                DancingMadOpenerStage.ExecutionersGibbet when state.Reaver == ReaverState.Executioner && context.MeleeAvailable && context.FallbackTargetAvailable => "ExecutionersGibbet",
                DancingMadOpenerStage.PlentifulHarvest when state.PlentifulHarvestReady && context.BestLineTargetAvailable => "PlentifulHarvest",
                DancingMadOpenerStage.VoidReaping1 or DancingMadOpenerStage.VoidReaping2 when state.BlueSouls > 0 && context.MeleeAvailable && context.FallbackTargetAvailable => "VoidReaping",
                DancingMadOpenerStage.CrossReaping1 or DancingMadOpenerStage.CrossReaping2 when state.BlueSouls > 0 && context.MeleeAvailable && context.FallbackTargetAvailable => "CrossReaping",
                DancingMadOpenerStage.Communio when state.BlueSouls == 1 && context.BestRangedAoeTargetAvailable => "Communio",
                DancingMadOpenerStage.Perfectio when state.PerfectioParata && context.BestRangedAoeTargetAvailable => "Perfectio",
                DancingMadOpenerStage.FollowupSoulSlice when state.SoulSliceCharges >= 1 && context.MeleeAvailable && context.FallbackTargetAvailable => "SoulSlice",
                DancingMadOpenerStage.FollowupGallows when state.Reaver == ReaverState.SoulReaver && context.MeleeAvailable && context.FallbackTargetAvailable => "Gallows",
                DancingMadOpenerStage.FollowupDeathsDesign when context.MeleeAvailable && context.FallbackTargetAvailable => "ShadowOfDeath",
                _ => null
            };
            if (action != null && Unlocked(scenario.Level, action))
            {
                candidates.Add(action);
                reason = "Dancing Mad fixed opener";
                return action;
            }

            return null;
        }

        if (state.Reaver != ReaverState.None)
        {
            if ((context.ConeTargets > 3 || !context.MeleeAvailable) && context.BestConeTargetAvailable && Unlocked(scenario.Level, "Guillotine"))
            {
                var action = state.Reaver == ReaverState.Executioner && Unlocked(scenario.Level, "ExecutionersGuillotine")
                    ? "ExecutionersGuillotine"
                    : "Guillotine";
                candidates.Add(action);
                reason = "4+ Reaver/Executioner AoE spender";
                return action;
            }

            if (context.MeleeAvailable && context.FallbackTargetAvailable && Unlocked(scenario.Level, "Gibbet") && Unlocked(scenario.Level, "Gallows"))
            {
                var executioner = state.Reaver == ReaverState.Executioner && Unlocked(scenario.Level, "ExecutionersGibbet");
                var gibbet = executioner ? "ExecutionersGibbet" : "Gibbet";
                var gallows = executioner ? "ExecutionersGallows" : "Gallows";
                var preferGibbet = executioner && IsInitialOpenerScenario(scenario) && time < 20
                    || state.PostPerfectioPriorityActive;
                var action = state.EnhancedGallowsLeft > 0
                    ? gallows
                    : state.EnhancedGibbetLeft > 0
                        ? gibbet
                        : preferGibbet
                            ? gibbet
                            : time % (scenario.Gcd * 2) < scenario.Gcd ? gibbet : gallows;
                candidates.Add(action);
                reason = "single-target Reaver/Executioner spender";
                return action;
            }
        }

        if (state.BlueSouls > 0)
        {
            if (ShouldPreArcaneDeathsDesign(scenario, state, context, time, profile) || ShouldRefreshDeathsDesignDuringAnyEnshroud(scenario, state, context, time, profile))
            {
                var action = SelectDeathsDesignAction(scenario.Level, context);
                if (action != null)
                {
                    candidates.Add(action);
                    reason = "second DD refresh required during first Enshroud after first Reaping";
                    return action;
                }
            }

            if (state.BlueSouls == 1 && context.BestRangedAoeTargetAvailable && Unlocked(scenario.Level, "Communio"))
            {
                candidates.Add("Communio");
                reason = "Communio";
                return "Communio";
            }

            if ((context.ConeTargets > 2 || !context.MeleeAvailable) && context.BestConeTargetAvailable && Unlocked(scenario.Level, "GrimReaping"))
            {
                candidates.Add("GrimReaping");
                reason = "Enshroud Reaping";
                return "GrimReaping";
            }

            var reaping = state.EnhancedCrossReapingLeft > 0 && Unlocked(scenario.Level, "CrossReaping")
                ? "CrossReaping"
                : state.EnhancedVoidReapingLeft > 0 && Unlocked(scenario.Level, "VoidReaping")
                    ? "VoidReaping"
                    : Unlocked(scenario.Level, "VoidReaping")
                        ? "VoidReaping"
                        : Unlocked(scenario.Level, "CrossReaping")
                            ? "CrossReaping"
                            : "";
            if (reaping.Length > 0 && context.MeleeAvailable && context.FallbackTargetAvailable)
            {
                candidates.Add(reaping);
                reason = "Enshroud Reaping";
                return reaping;
            }
        }

        var zeroSecondOpenerSoulSliceBeforeDeathsDesign = ShouldUseZeroSecondOpenerSoulSliceBeforeDeathsDesign(scenario, state, context, time);
        if (ShouldPrioritizePlentifulHarvestBeforeSecondEvenBurstEnshroud(scenario, state, context))
        {
            candidates.Add("PlentifulHarvest");
            reason = "Plentiful Harvest before second even-burst Enshroud";
            return "PlentifulHarvest";
        }

        if (ShouldPrioritizePostPerfectioCombo(state, context))
        {
            var action = state.ComboLast == "WaxingSlice" ? "InfernalSlice" : "WaxingSlice";
            if (Unlocked(scenario.Level, action) && context.MeleeAvailable && context.FallbackTargetAvailable)
            {
                candidates.Add(action);
                reason = "post-Perfectio combo recovery";
                return action;
            }

            return null;
        }

        var finishNormalOpenerPostPerfectio = IsNormalOpenerPostPerfectioSequence(scenario, state, time)
            && state.DeathsDesignLeft > scenario.Gcd + BurstPlanGcdLength(scenario);
        var ddRefreshReason = zeroSecondOpenerSoulSliceBeforeDeathsDesign || finishNormalOpenerPostPerfectio ? "" : DeathsDesignRefreshReason(scenario, state, context, time, profile);
        var perfectioReady = state.PerfectioParata && Unlocked(scenario.Level, "Perfectio");
        var holdPerfectioForCombo = profile.PerfectioComboProtectGcds > 0
            && state.ComboLast is "Slice" or "WaxingSlice"
            && state.PerfectioParataLeft > scenario.Gcd * profile.PerfectioComboProtectGcds;
        var holdPerfectioForDoubleEnshroud = (state.PlentifulHarvestReady || state.ImmortalSacrifice > 0 || state.IdealHost)
            && state.PerfectioParataLeft > scenario.Gcd + 8.0;
        var prioritizePerfectioBeforeDeathsDesign = perfectioReady
            && !holdPerfectioForCombo
            && !holdPerfectioForDoubleEnshroud
            && state.DeathsDesignLeft > 0
            && state.ArcaneCircleLeft > 0
            && state.ArcaneCircleLeft <= scenario.Gcd;
        if (!prioritizePerfectioBeforeDeathsDesign && ddRefreshReason is ("emergency" or "pre_any_enshroud" or "arcane_start_coverage" or "two_refresh_pre_burst"))
        {
            var action = SelectDeathsDesignAction(scenario.Level, context);
            if (action != null)
            {
                candidates.Add(action);
                reason = "Death's Design refresh";
                return action;
            }
        }

        if (!prioritizePerfectioBeforeDeathsDesign && perfectioReady && state.DeathsDesignLeft <= scenario.Gcd + 0.3 && ddRefreshReason.Length > 0)
        {
            var action = SelectDeathsDesignAction(scenario.Level, context);
            if (action != null)
            {
                candidates.Add(action);
                reason = "emergency Death's Design before Perfectio";
                return action;
            }
        }

        if (perfectioReady && !holdPerfectioForCombo && !holdPerfectioForDoubleEnshroud && context.BestRangedAoeTargetAvailable)
        {
            candidates.Add("Perfectio");
            reason = "PerfectioParata";
            return "Perfectio";
        }

        if (ShouldUseLateBurstSoulSliceBeforePlentifulHarvest(scenario, state, context, time, profile))
        {
            var action = context.AoeTargets > 2 && Unlocked(scenario.Level, "SoulScythe") ? "SoulScythe" : "SoulSlice";
            candidates.Add(action);
            reason = "fast GCD Soul Slice/Scythe before Plentiful Harvest";
            return action;
        }

        if (state.PlentifulHarvestReady && context.BestLineTargetAvailable && Unlocked(scenario.Level, "PlentifulHarvest"))
        {
            candidates.Add("PlentifulHarvest");
            reason = "Plentiful Harvest after Arcane Circle";
            return "PlentifulHarvest";
        }

        if (ShouldPrioritizeComboAfterBurst(scenario, state, context, profile))
        {
            var action = state.ComboLast == "WaxingSlice" ? "InfernalSlice" : "WaxingSlice";
            if (Unlocked(scenario.Level, action) && context.MeleeAvailable && context.FallbackTargetAvailable)
            {
                candidates.Add(action);
                reason = "post-burst combo preserve";
                return action;
            }

            return null;
        }

        if (ddRefreshReason.Length > 0)
        {
            var action = SelectDeathsDesignAction(scenario.Level, context);
            if (action != null)
            {
                candidates.Add(action);
                reason = "Death's Design refresh";
                return action;
            }
        }

        if (!context.MeleeAvailable)
        {
            if (state.PerfectioParata && context.BestRangedAoeTargetAvailable && Unlocked(scenario.Level, "Perfectio"))
            {
                candidates.Add("Perfectio");
                reason = "ranged Perfectio uptime";
                return "Perfectio";
            }
            if (state.Soulsow && context.BestRangedAoeTargetAvailable && Unlocked(scenario.Level, "HarvestMoon"))
            {
                candidates.Add("HarvestMoon");
                reason = "ranged Harvest Moon uptime";
                return "HarvestMoon";
            }
            if (Unlocked(scenario.Level, "Harpe"))
            {
                candidates.Add("Harpe");
                reason = "ranged Harpe uptime";
                return "Harpe";
            }
        }

        var postPerfectioGluttonyReady = (state.PostPerfectioPriorityActive || PerfectioJustUsed(scenario, state))
            && state.ComboLast is not ("Slice" or "WaxingSlice")
            && context.RotationMode == RotationMode.Full
            && Unlocked(scenario.Level, "Gluttony")
            && context.BestRangedAoeTargetAvailable
            && state.GluttonyReadyIn <= Math.Max(0, scenario.Gcd - 0.8)
            && state.RedGauge >= 50;
        if (!postPerfectioGluttonyReady && (zeroSecondOpenerSoulSliceBeforeDeathsDesign || ShouldUseSoulSlice(scenario, state, context, time, profile)))
        {
            var action = context.AoeTargets > 2 && Unlocked(scenario.Level, "SoulScythe") ? "SoulScythe" : "SoulSlice";
            candidates.Add(action);
            reason = zeroSecondOpenerSoulSliceBeforeDeathsDesign ? "zero-second opener Soul Slice before Death's Design" : "Soul Slice/Scythe";
            return action;
        }

        if (context.AoeTargets > 2 && Unlocked(scenario.Level, "SpinningScythe"))
        {
            var action = state.ComboLast == "SpinningScythe" && Unlocked(scenario.Level, "NightmareScythe") ? "NightmareScythe" : "SpinningScythe";
            candidates.Add(action);
            reason = "AoE filler combo";
            return action;
        }

        if (context.MeleeAvailable && context.FallbackTargetAvailable && Unlocked(scenario.Level, "Slice"))
        {
            var action = state.ComboLast switch
            {
                "Slice" when Unlocked(scenario.Level, "WaxingSlice") => "WaxingSlice",
                "WaxingSlice" when Unlocked(scenario.Level, "InfernalSlice") => "InfernalSlice",
                _ => "Slice"
            };
            candidates.Add(action);
            reason = "single-target filler combo";
            return action;
        }

        return null;
    }

    private static string? SelectDeathsDesignAction(int level, RprContext context)
    {
        if (context.AoeTargets > 2 && Unlocked(level, "WhorlOfDeath"))
            return "WhorlOfDeath";
        return context.MeleeAvailable && context.FallbackTargetAvailable && Unlocked(level, "ShadowOfDeath") ? "ShadowOfDeath" : null;
    }

    internal static int RedGaugeGainFromGcd(string? gcd, string comboLast)
        => gcd switch
        {
            "SoulSlice" or "SoulScythe" => 50,
            "Slice" or "SpinningScythe" or "Harpe" or "HarvestMoon" => 10,
            "WaxingSlice" when comboLast == "Slice" => 10,
            "InfernalSlice" when comboLast == "WaxingSlice" => 10,
            "NightmareScythe" when comboLast == "SpinningScythe" => 10,
            _ => 0
        };

    private static void SelectOgcds(ScenarioDefinition scenario, RprState state, RprContext context, double time, string? gcd, double currentGcd, List<string> ogcds, RprTuningProfile profile)
    {
        if (!context.TargetAvailable)
            return;

        var redGaugeAfterGcd = Math.Min(100, state.RedGauge + RedGaugeGainFromGcd(gcd, state.ComboLast));
        if (scenario.SkillRotation == SkillRotationMode.DancingMad && state.DancingMadOpenerStage != DancingMadOpenerStage.Complete && time < 40)
        {
            switch (state.DancingMadOpenerStage)
            {
                case DancingMadOpenerStage.Harpe when gcd == "Harpe" && state.ArcaneCircleReadyIn <= Math.Max(0, currentGcd - 0.8):
                    ogcds.Add("ArcaneCircle");
                    break;
                case DancingMadOpenerStage.SoulSlice when gcd == "SoulSlice" && redGaugeAfterGcd >= 50 && state.GluttonyReadyIn <= Math.Max(0, currentGcd - 0.8):
                    ogcds.Add("Gluttony");
                    break;
                case DancingMadOpenerStage.PlentifulHarvest when gcd == "PlentifulHarvest":
                    ogcds.Add("Enshroud");
                    if (Unlocked(scenario.Level, "Sacrificium") && context.BestRangedAoeTargetAvailable)
                        ogcds.Add("Sacrificium");
                    break;
                case DancingMadOpenerStage.CrossReaping1 or DancingMadOpenerStage.CrossReaping2 when gcd == "CrossReaping" && state.PurpleSouls + 1 >= 2 && context.MeleeAvailable && context.FallbackTargetAvailable:
                    ogcds.Add("LemuresSlice");
                    break;
                case DancingMadOpenerStage.FollowupSoulSlice when gcd == "SoulSlice" && redGaugeAfterGcd >= 50 && context.MeleeAvailable && context.FallbackTargetAvailable:
                    ogcds.Add(state.EnhancedGallowsLeft > 0 && Unlocked(scenario.Level, "UnveiledGallows") ? "UnveiledGallows" : "BloodStalk");
                    break;
            }

            return;
        }

        var deathsDesignAfterGcd = gcd is "ShadowOfDeath" or "WhorlOfDeath"
            ? Math.Min(60, state.DeathsDesignLeft + 30)
            : state.DeathsDesignLeft;

        var canArcane = context.RotationMode == RotationMode.Full && Unlocked(scenario.Level, "ArcaneCircle") && state.ArcaneCircleReadyIn <= Math.Max(0, currentGcd - 0.8);
        var holdNewBurst = ShouldHoldNewBurstForEndingDutyTarget(scenario, context, time);
        var evenBurstCycle = time % 120;
        var evenBurst = time >= 105 && (evenBurstCycle >= 105 || evenBurstCycle <= 15);
        var normalOpenerSoulSliceBurstGcd = IsNormalOpenerSoulSliceBurstGcd(scenario, state, context, time, gcd);
        var arcaneCircleQueuedThisGCD = normalOpenerSoulSliceBurstGcd || ShouldQueueArcaneCircleThisGcd(scenario, state, context, time, currentGcd, profile);
        var openerValidationModel = scenario.Category == ScenarioCategory.WeaveValidation && scenario.Name.StartsWith("opener_", StringComparison.OrdinalIgnoreCase);
        var earlyOpener = openerValidationModel
            ? IsEarlyOpenerArcaneCircle(scenario, state, context, time)
            : scenario.Opener is OpenerBurstMode.ZeroSecond && time < 1
                || scenario.Opener is OpenerBurstMode.TwoPointFiveSecond && time is >= 1 and < 4;
        var openerPotionBeforeArcane = IsNormalOpenerDeathsDesignGcd(scenario, state, context, time, gcd)
            || (openerValidationModel
                ? OpenerPotionBeforeArcaneCircleWindow(scenario, state, context, time, gcd)
                : scenario.Opener is OpenerBurstMode.TwoGcd && state.SoulSliceUsed && time < 10 && canArcane);
        var reapingGcd = gcd is "VoidReaping" or "CrossReaping" or "GrimReaping";
        var purpleSoulsAfterGcd = state.PurpleSouls + (reapingGcd ? 1 : 0);
        var blueSoulsAfterGcd = state.BlueSouls - (reapingGcd ? 1 : 0);
        var preArcaneEnshroudPotionWindow = blueSoulsAfterGcd is > 1 and <= 4
            && state.ArcaneCircleLeft <= 0
            && ShouldUseTwoDeathsDesignRefreshesForEvenBurst(scenario, state, context, time, state.DeathsDesignLeft, profile);
        var alignPotionWithPreArcaneRefresh = preArcaneEnshroudPotionWindow
            || ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(scenario, state, context, time)
            || ShouldDeferDeathsDesignRefreshToPreArcaneEnshroud(scenario, state, context);
        var preArcaneEnshroudPotion = preArcaneEnshroudPotionWindow
            && arcaneCircleQueuedThisGCD
            && canArcane;

        var potionQueuedThisGCD = ShouldUsePotion(scenario, state, context, time, canArcane, earlyOpener, openerPotionBeforeArcane, evenBurst && !alignPotionWithPreArcaneRefresh, preArcaneEnshroudPotion, profile);
        var spendBeforeUnavailable = profile.EndGaugeSpendWindowSeconds > 0
            && TimeUntilTargetUnavailable(scenario, time) <= profile.EndGaugeSpendWindowSeconds;
        var arcaneCircleBeforePotion = potionQueuedThisGCD
            && arcaneCircleQueuedThisGCD
            && (state.ArcaneCircleUseCount >= 2 || time >= ArcaneCircleRecast * 2);
        if (arcaneCircleBeforePotion)
        {
            ogcds.Add("ArcaneCircle");
            ogcds.Add("Potion");
        }
        else
        {
            if (potionQueuedThisGCD)
                ogcds.Add("Potion");
            if (arcaneCircleQueuedThisGCD)
                ogcds.Add("ArcaneCircle");
        }

        var enshroudQueuedThisGCD = ShouldQueueEnshroud(scenario, state, context, time, gcd, currentGcd, profile);
        var reservePreArcaneEnshroud = enshroudQueuedThisGCD
            && ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(scenario, state, context, time);
        var evenBurstShroudPlan = BuildEvenBurstShroudPlan(scenario, state, context, time, currentGcd);
        var plannedBlueGaugeBuild = evenBurstShroudPlan.Urgent
            && redGaugeAfterGcd >= 50;

        var gluttonyReadyDelayExpired = state.GluttonyReadyDelayExpired(time);
        var waitingForArcane = context.RotationMode == RotationMode.Full
            && Unlocked(scenario.Level, "ArcaneCircle")
            && state.ArcaneCircleLeft <= 0
            && state.ArcaneCircleReadyIn <= profile.GluttonyHoldBeforeArcaneSeconds
            && (!gluttonyReadyDelayExpired || arcaneCircleQueuedThisGCD)
            && !normalOpenerSoulSliceBurstGcd
            && !spendBeforeUnavailable;
        var gluttonySoon = context.RotationMode == RotationMode.Full
            && Unlocked(scenario.Level, "Gluttony")
            && context.BestRangedAoeTargetAvailable
            && state.GluttonyReadyIn <= currentGcd + scenario.Gcd * 5;
        var recoveringComboAfterPerfectio = ShouldPrioritizePostPerfectioCombo(state, context);
        var completingPostPerfectioCombo = (recoveringComboAfterPerfectio || PerfectioJustUsed(scenario, state)) && gcd == "InfernalSlice";
        var prioritizeSoulSliceAfterPerfectio = ShouldPrioritizePostPerfectioSoulSlice(scenario, state, context, time);
        var postPerfectioSoulSliceGcd = (state.PostPerfectioPriorityActive || PerfectioJustUsed(scenario, state)) && gcd is "SoulSlice" or "SoulScythe";
        var postPerfectioSoulSliceCreatesRed = prioritizeSoulSliceAfterPerfectio
            && gcd is "SoulSlice" or "SoulScythe"
            && state.RedGauge < 50;
        if (context.RotationMode == RotationMode.Full
            && Unlocked(scenario.Level, "Gluttony")
            && context.BestRangedAoeTargetAvailable
            && state.GluttonyReadyIn <= Math.Max(0, currentGcd - 0.8)
            && redGaugeAfterGcd >= 50
            && state.Reaver == ReaverState.None
            && state.BlueSouls == 0
            && !enshroudQueuedThisGCD
            && !reservePreArcaneEnshroud
            && (!recoveringComboAfterPerfectio || completingPostPerfectioCombo)
            && (!prioritizeSoulSliceAfterPerfectio || postPerfectioSoulSliceCreatesRed)
            && gcd is not ("PlentifulHarvest" or "Perfectio")
            && !(gcd == "PlentifulHarvest" && enshroudQueuedThisGCD)
            && (!waitingForArcane || plannedBlueGaugeBuild)
            && deathsDesignAfterGcd > currentGcd * 2
            && (state.ArcaneCircleLeft > 0 || !holdNewBurst))
            ogcds.Add("Gluttony");

        var enshroudedAfterGcd = blueSoulsAfterGcd > 0 && gcd != "Communio";
        var lemureQueuedThisGCD = enshroudedAfterGcd && purpleSoulsAfterGcd >= 2;
        var sacrificiumQueuedThisGCD = enshroudedAfterGcd && state.Oblatio && context.BestRangedAoeTargetAvailable && Unlocked(scenario.Level, "Sacrificium") && !lemureQueuedThisGCD && (state.ArcaneCircleLeft > 0 || blueSoulsAfterGcd <= 2);
        if (lemureQueuedThisGCD
            && !(potionQueuedThisGCD && arcaneCircleQueuedThisGCD)
            && !(arcaneCircleQueuedThisGCD && sacrificiumQueuedThisGCD)
            && !(potionQueuedThisGCD && sacrificiumQueuedThisGCD))
        {
            if ((context.ConeTargets > 2 || !context.MeleeAvailable) && context.BestConeTargetAvailable && Unlocked(scenario.Level, "LemuresScythe"))
            {
                ogcds.Add("LemuresScythe");
            }
            else if (context.MeleeAvailable && context.FallbackTargetAvailable && Unlocked(scenario.Level, "LemuresSlice"))
            {
                ogcds.Add("LemuresSlice");
            }
        }

        if (sacrificiumQueuedThisGCD)
            ogcds.Add("Sacrificium");

        if (enshroudQueuedThisGCD)
            ogcds.Add("Enshroud");

        if (postPerfectioSoulSliceCreatesRed && !enshroudQueuedThisGCD && !ogcds.Contains("Gluttony"))
        {
            var blood = SelectSoulSpender(scenario.Level, state, context);
            if (blood != null)
                ogcds.Add(blood);
        }

        var soulSliceOvercapSoon = (state.MaxSoulSliceCharges - state.SoulSliceCharges) * 30 <= currentGcd;
        var preBurstSoulSliceChargeProtection = ShouldSpendRedForPreBurstSoulSlice(scenario, state, context, time);
        var preBurstCappedRedSpend = ShouldSpendCappedRedBeforeEvenBurstEnshroud(scenario, state, context, time);
        if (context.RotationMode != RotationMode.Basic
            && redGaugeAfterGcd >= 50
            && (context.MeleeAvailable && context.FallbackTargetAvailable || context.BestConeTargetAvailable)
            && state.Reaver == ReaverState.None
            && state.BlueSouls == 0
            && !ogcds.Contains("Gluttony")
            && !enshroudQueuedThisGCD
            && !reservePreArcaneEnshroud
            && !(gcd == "PlentifulHarvest" && enshroudQueuedThisGCD)
            && (redGaugeAfterGcd == 100 || spendBeforeUnavailable || scenario.ForceBlueGaugeBuild || soulSliceOvercapSoon && redGaugeAfterGcd > 50 || preBurstSoulSliceChargeProtection || preBurstCappedRedSpend || plannedBlueGaugeBuild || (state.ArcaneCircleLeft > 0 || state.BlueGauge < 50) && !gluttonySoon))
        {
            var blood = SelectSoulSpender(scenario.Level, state, context);
            if (blood != null)
                ogcds.Add(blood);
        }
        else if (context.RotationMode == RotationMode.Basic
            && redGaugeAfterGcd >= 50
            && (context.MeleeAvailable && context.FallbackTargetAvailable || context.BestConeTargetAvailable)
            && state.Reaver == ReaverState.None
            && !enshroudQueuedThisGCD
            && state.BlueSouls == 0)
        {
            var blood = SelectSoulSpender(scenario.Level, state, context);
            if (blood != null)
                ogcds.Add(blood);
        }

        var createsReaver = ogcds.Any(action => action is "Gluttony" or "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe");
        if (scenario.TrueNorth == TrueNorthMode.Auto
            && !(potionQueuedThisGCD && arcaneCircleQueuedThisGCD)
            && createsReaver
            && ogcds.Count < 2
            && state.TrueNorthCharges >= 1
            && state.TrueNorthLeft <= 0
            && !context.PositionalCorrect
            && context.MeleeAvailable
            && context.ConeTargets <= 3
            && Unlocked(scenario.Level, "TrueNorth")
            && Unlocked(scenario.Level, "Gibbet")
            && Unlocked(scenario.Level, "Gallows"))
            ogcds.Add("TrueNorth");
    }

    private static bool ShouldHoldDancingMadEnshroudForMeleePlan(ScenarioDefinition scenario, double time)
    {
        if (!ShouldUseDancingMadProfile(scenario))
            return false;

        var forcedOut = scenario.Events
            .Where(e => e.Type == ScenarioEventType.MeleeUnavailable
                && e.Value > DancingMadConeRange
                && e.End > time
                && e.Start <= time + DancingMadMeleePlanHorizon)
            .MinBy(e => e.Start);
        if (forcedOut == null)
            return false;

        var coneLossIn = Math.Max(0, forcedOut.Start - time);
        var coneResumeIn = Math.Max(0, forcedOut.End - time);
        if (coneLossIn <= DancingMadMovementSafetyBuffer)
            return coneResumeIn >= BurstReferenceGcdLength - DancingMadMovementSafetyBuffer;

        for (var reaping = 0; reaping < 4; ++reaping)
        {
            var reapingAt = BurstReferenceGcdLength + 1.5 * reaping;
            if (reapingAt + DancingMadMovementSafetyBuffer >= coneLossIn
                && reapingAt < coneResumeIn + DancingMadMovementSafetyBuffer)
                return true;
        }

        return false;
    }

    private static bool ShouldQueueEnshroud(ScenarioDefinition scenario, RprState state, RprContext context, double time, string? gcd, double currentGcd, RprTuningProfile profile)
    {
        var plentifulHarvestIntoEnshroud = gcd == "PlentifulHarvest" && state.PlentifulHarvestReady && Unlocked(scenario.Level, "PlentifulHarvest");
        var idealHostAvailable = state.IdealHost || plentifulHarvestIntoEnshroud;
        var perfectioAvailable = state.PerfectioParata;
        var perfectioLeft = state.PerfectioParataLeft;
        var canHoldPerfectio = perfectioAvailable && idealHostAvailable && perfectioLeft > currentGcd + 8.0;

        if ((!context.TargetAvailable && !idealHostAvailable)
            || context.RotationMode != RotationMode.Full && !idealHostAvailable
            || state.BlueSouls > 0
            || state.BlueGauge < 50 && !idealHostAvailable
            || !Unlocked(scenario.Level, "Enshroud")
            || state.EnshroudReadyIn > Math.Max(0, currentGcd - 0.8)
            || state.Reaver != ReaverState.None
            || IsNormalOpenerPostPerfectioSequence(scenario, state, time)
            || perfectioAvailable && !canHoldPerfectio)
            return false;

        var evenBurstShroudPlan = BuildEvenBurstShroudPlan(scenario, state, context, time, currentGcd);
        if (!idealHostAvailable
            && evenBurstShroudPlan.Active
            && !CanStartPreArcaneEnshroudThisGcd(scenario, state, currentGcd)
            && state.BlueGauge - 50 < evenBurstShroudPlan.RequiredShroud)
            return false;

        if (!idealHostAvailable && state.ArcaneCircleLeft <= 0 && ShouldHoldNewBurstForEndingDutyTarget(scenario, context, time))
            return false;

        if (!idealHostAvailable
            && scenario.SkillRotation == SkillRotationMode.DancingMad
            && TimeUntilTargetUnavailable(scenario, time) < 10.0)
            return false;

        if (ShouldHoldDancingMadEnshroudForMeleePlan(scenario, time))
            return false;

        var refreshDeathsDesignAfterFirstReaping = ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(scenario, state, context, time);
        var useFourSecondEnshroudEntry = Unlocked(scenario.Level, "Perfectio")
            && scenario.SkillRotation == SkillRotationMode.Normal
            && (IsEvenBurstPlanningWindow(scenario, state, context, time) || scenario.ArcaneCircleFourSecondDisplay);
        var refreshDeathsDesignOnEnshroudEntry = useFourSecondEnshroudEntry
            && CanStartPreArcaneEnshroudThisGcd(scenario, state, currentGcd)
            && state.DeathsDesignLeft <= scenario.Gcd + 9.0;
        var normalOpenerPostSequenceDeathsDesignEntry = scenario.Opener == OpenerBurstMode.TwoGcd
            && IsInitialOpenerScenario(scenario)
            && time < 35
            && gcd is "ShadowOfDeath" or "WhorlOfDeath"
            && state.TimeSincePerfectioUsed is >= 0 and <= 15
            && state.PostPerfectioPriorityComplete;
        if ((ShouldBlockEnshroudForDeathsDesign(scenario, state, context, time, profile) || state.DeathsDesignLeft <= scenario.Gcd + 9.0)
            && !refreshDeathsDesignAfterFirstReaping
            && !refreshDeathsDesignOnEnshroudEntry
            && !normalOpenerPostSequenceDeathsDesignEntry)
            return false;

        var dancingMadPolicy = DancingMadEnshroudUsePolicy(scenario, state, context, time);
        if (dancingMadPolicy == DancingMadEnshroudPolicy.Release)
            return true;
        if (dancingMadPolicy == DancingMadEnshroudPolicy.Hold)
        {
            var redGaugeGain = RedGaugeGainFromGcd(gcd, state.ComboLast);
            var redGaugeAfterGcd = Math.Min(100, state.RedGauge + redGaugeGain);
            var nextGcdWouldOverflowRed = state.BlueGauge == 100
                && redGaugeGain > 0
                && state.RedGauge + redGaugeGain > 100;
            var gluttonyWouldOverflowShroud = state.BlueGauge > 80
                && redGaugeAfterGcd >= 50
                && state.GluttonyReadyIn <= 0.1
                && context.BestRangedAoeTargetAvailable;
            var capPressure = nextGcdWouldOverflowRed
                || gluttonyWouldOverflowShroud
                || state.BlueGauge == 100 && state.ArcaneCircleReadyIn > 45;
            return idealHostAvailable || capPressure;
        }

        if (idealHostAvailable)
            return true;

        if (state.ArcaneCircleLeft > currentGcd + 7.3)
            return true;

        var profileHold = profile.EnshroudHoldBeforeArcaneSeconds > 0
            && state.ArcaneCircleLeft <= 0
            && state.ArcaneCircleReadyIn > 0.1
            && state.ArcaneCircleReadyIn <= profile.EnshroudHoldBeforeArcaneSeconds
            && state.BlueGauge < 100;
        if (profileHold)
            return false;

        var preparingDoubleEnshroud = state.BlueGauge >= 50
            && state.ArcaneCircleLeft <= 0
            && (useFourSecondEnshroudEntry
                ? CanStartPreArcaneEnshroudThisGcd(scenario, state, currentGcd)
                : state.ArcaneCircleReadyIn > currentGcd - 0.8 && state.ArcaneCircleReadyIn <= BurstPlanGcdLength(scenario) * 2.5)
            && !state.PlentifulHarvestReady
            && !perfectioAvailable;
        if (preparingDoubleEnshroud)
            return true;

        if (state.GluttonyReadyIn < 13)
            return false;

        var capPressureThreshold = profile.BlueGauge100StandaloneEnshroudArcaneThreshold > 0
            ? profile.BlueGauge100StandaloneEnshroudArcaneThreshold
            : 45.0;
        if (state.BlueGauge == 100 && state.ArcaneCircleReadyIn > capPressureThreshold)
            return true;

        return state.ArcaneCircleReadyIn > 65;
    }

    private static bool ShouldPrioritizePlentifulHarvestBeforeSecondEvenBurstEnshroud(ScenarioDefinition scenario, RprState state, RprContext context)
        => context.RotationMode == RotationMode.Full
        && Unlocked(scenario.Level, "Perfectio")
        && state.ArcaneCircleLeft > 0
        && state.BlueSouls == 0
        && state.Reaver == ReaverState.None
        && !state.PerfectioParata
        && !state.IdealHost
        && state.BlueGauge >= 50
        && state.DeathsDesignLeft > scenario.Gcd + 9.0
        && state.PlentifulHarvestReady
        && context.BestLineTargetAvailable
        && (context.MeleeAvailable && context.FallbackTargetAvailable || context.BestConeTargetAvailable);

    private static bool CanStartPreArcaneEnshroudThisGcd(ScenarioDefinition scenario, RprState state, double currentGcd)
        => Unlocked(scenario.Level, "Perfectio")
        && state.EnshroudReadyIn <= Math.Max(0, currentGcd - 0.8)
        && state.ArcaneCircleReadyIn > 0
        && state.ArcaneCircleReadyIn <= PreArcaneEnshroudEntryWindow;

    private static string SingleTargetSoulSpender(int level, RprState state)
        => state.EnhancedGibbetLeft > 0 && Unlocked(level, "UnveiledGibbet")
            ? "UnveiledGibbet"
            : state.EnhancedGallowsLeft > 0 && Unlocked(level, "UnveiledGallows")
                ? "UnveiledGallows"
                : "BloodStalk";

    private static string? SelectSoulSpender(int level, RprState state, RprContext context)
    {
        var single = SingleTargetSoulSpender(level, state);
        var singlePotency = single == "BloodStalk" ? 340 : level >= 94 ? 440 : 400;
        if (context.BestConeTargetAvailable
            && Unlocked(level, "GrimSwathe")
            && (!context.MeleeAvailable || context.ConeTargets * 140 >= singlePotency))
            return "GrimSwathe";
        return context.MeleeAvailable && context.FallbackTargetAvailable && Unlocked(level, single) ? single : null;
    }

    private static bool ShouldHoldArcaneCircleForPreArcaneEnshroud(ScenarioDefinition scenario, RprState state, RprContext context, double time, double currentGcd)
        => context.RotationMode == RotationMode.Full
        && scenario.ArcaneCircleFourSecondDisplay
        && state.ArcaneCircleLeft <= 0
        && state.BlueSouls == 0
        && state.Reaver == ReaverState.None
        && !state.PerfectioParata
        && !state.PlentifulHarvestReady
        && !state.IdealHost
        && state.BlueGauge >= 50
        && Unlocked(scenario.Level, "Enshroud")
        && state.ArcaneCircleReadyIn <= currentGcd - 0.8
        && CanStartPreArcaneEnshroudThisGcd(scenario, state, currentGcd);

    private static bool ShouldQueueArcaneCircleThisGcd(ScenarioDefinition scenario, RprState state, RprContext context, double time, double currentGcd, RprTuningProfile profile)
    {
        var canArcane = context.RotationMode == RotationMode.Full
            && Unlocked(scenario.Level, "ArcaneCircle")
            && state.ArcaneCircleReadyIn <= Math.Max(0, currentGcd - 0.8);
        if (!canArcane)
            return false;

        if (ShouldHoldNewBurstForEndingDutyTarget(scenario, context, time))
            return false;

        if (state.BlueSouls == 0 && ShouldHoldNewBurstForUpcomingDowntime(scenario, state, context, time, 20, profile))
            return false;

        if (ShouldHoldArcaneCircleForPreArcaneEnshroud(scenario, state, context, time, currentGcd))
            return false;

        if (state.BlueSouls == 5
            && state.ArcaneCircleLeft <= 0
            && ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(scenario, state, context, time))
            return false;

        if (ShouldUseDancingMadProfile(scenario))
        {
            if (time < 10 || IsInDancingMadArcaneCircleWindow(time, profile))
                return true;

            if (ShouldHoldForDancingMadArcaneCircle(state, time, profile))
                return false;
        }

        if (ShouldUseWindurstThirdWalkProfile(scenario))
        {
            var windows = WindurstArcaneCircleWindows(scenario.WindurstEncounter);
            if (IsInWindurstWindow(windows, time))
                return true;

            if (ShouldHoldForWindurstArcaneCircle(state, windows, time))
                return false;
        }

        var openerValidationModel = scenario.Category == ScenarioCategory.WeaveValidation && scenario.Name.StartsWith("opener_", StringComparison.OrdinalIgnoreCase);
        if (openerValidationModel)
            return ShouldPlanArcaneCircle(scenario, state, context, time);

        var evenBurstCycle = time % 120;
        var evenBurst = time >= 105 && (evenBurstCycle >= 105 || evenBurstCycle <= 15);
        return !ShouldHoldNormalOpenerArcaneCircleForSoulSlice(scenario, state, context, time)
            && (scenario.Opener is OpenerBurstMode.ZeroSecond && time < 1
                || scenario.Opener is OpenerBurstMode.TwoPointFiveSecond && time is >= 1 and < 4
                || scenario.Opener is OpenerBurstMode.TwoGcd && state.SoulSliceUsed && time < 10
                || evenBurst
                || time < 10
                || state.ArcaneCircleReadyIn <= 0.1);
    }

    private static bool ShouldPlanArcaneCircle(ScenarioDefinition scenario, RprState state, RprContext context, double time)
    {
        if (!Unlocked(scenario.Level, "ArcaneCircle") || context.RotationMode != RotationMode.Full)
            return false;

        if (ShouldHoldNewBurstForEndingDutyTarget(scenario, context, time))
            return false;

        if (ShouldUseDancingMadProfile(scenario))
        {
            if (time < 10 || IsInDancingMadArcaneCircleWindow(time, RprTuningProfile.Baseline))
                return true;

            if (ShouldHoldForDancingMadArcaneCircle(state, time, RprTuningProfile.Baseline))
                return false;
        }


        if (ShouldUseWindurstThirdWalkProfile(scenario))
        {
            var windows = WindurstArcaneCircleWindows(scenario.WindurstEncounter);
            if (IsInWindurstWindow(windows, time))
                return true;

            if (ShouldHoldForWindurstArcaneCircle(state, windows, time))
                return false;
        }

        if (IsZeroSecondOpener(scenario, state, context, time))
            return true;
        if (IsTwoPointFiveSecondOpener(scenario, state, context, time))
            return true;
        if (ShouldHoldNormalOpenerArcaneCircleForSoulSlice(scenario, state, context, time))
            return false;

        return state.SoulSliceUsed || time > 10;
    }

    private static bool ShouldHoldNormalOpenerArcaneCircleForSoulSlice(ScenarioDefinition scenario, RprState state, RprContext context, double time)
        => !ShouldUseDancingMadProfile(scenario)
        && scenario.Opener == OpenerBurstMode.TwoGcd
        && context.TargetAvailable
        && context.RotationMode == RotationMode.Full
        && time < 20
        && Unlocked(scenario.Level, "SoulSlice")
        && state.RedGauge <= 50
        && state.BlueGauge < 50
        && state.BlueSouls == 0
        && state.Reaver == ReaverState.None
        && !state.IdealHost
        && !state.PerfectioParata
        && !state.PlentifulHarvestReady
        && !state.SoulSliceUsed;

    private static bool IsZeroSecondOpener(ScenarioDefinition scenario, RprState state, RprContext context, double time)
        => (scenario.Opener == OpenerBurstMode.ZeroSecond || ShouldUseDancingMadProfile(scenario))
        && time < 10
        && context.TargetAvailable
        && context.RotationMode == RotationMode.Full
        && Unlocked(scenario.Level, "ArcaneCircle")
        && state.ArcaneCircleReadyIn <= scenario.Gcd;

    private static bool ShouldUseZeroSecondOpenerSoulSliceBeforeDeathsDesign(ScenarioDefinition scenario, RprState state, RprContext context, double time)
        => IsZeroSecondOpener(scenario, state, context, time)
        && state.SoulSliceCharges >= 1
        && !state.SoulSliceUsed
        && Unlocked(scenario.Level, "SoulSlice")
        && context.MeleeAvailable
        && context.FallbackTargetAvailable
        && state.RedGauge <= 50
        && state.BlueGauge < 50
        && state.BlueSouls == 0
        && state.Reaver == ReaverState.None
        && !state.IdealHost
        && !state.PerfectioParata
        && !state.PlentifulHarvestReady;

    private static bool IsTwoPointFiveSecondOpener(ScenarioDefinition scenario, RprState state, RprContext context, double time)
        => !ShouldUseDancingMadProfile(scenario)
        && scenario.Opener == OpenerBurstMode.TwoPointFiveSecond
        && time < 10
        && context.TargetAvailable
        && context.RotationMode == RotationMode.Full
        && Unlocked(scenario.Level, "ArcaneCircle")
        && state.ArcaneCircleReadyIn <= scenario.Gcd
        && !state.SoulSliceUsed
        && (state.DeathsDesignLeft > scenario.Gcd || time >= 1.0);

    private static bool IsEarlyOpenerArcaneCircle(ScenarioDefinition scenario, RprState state, RprContext context, double time)
        => time < 10
        && state.ArcaneCircleReadyIn <= scenario.Gcd
        && context.TargetAvailable
        && context.RotationMode == RotationMode.Full
        && (ShouldUseDancingMadProfile(scenario) || scenario.Opener is OpenerBurstMode.ZeroSecond or OpenerBurstMode.TwoPointFiveSecond);

    private static bool OpenerPotionBeforeArcaneCircleWindow(ScenarioDefinition scenario, RprState state, RprContext context, double time, string? gcd)
        => IsNormalOpenerDeathsDesignGcd(scenario, state, context, time, gcd)
        || time < 30
        && state.ArcaneCircleReadyIn <= 0.5
        && state.DeathsDesignLeft > scenario.Gcd
        && Unlocked(scenario.Level, "SoulSlice")
        && !state.SoulSliceUsed
        && state.BlueSouls == 0
        && state.Reaver == ReaverState.None
        && context.TargetAvailable;

    private static bool IsNormalOpenerSoulSliceBurstGcd(ScenarioDefinition scenario, RprState state, RprContext context, double time, string? gcd)
        => !ShouldUseDancingMadProfile(scenario)
        && scenario.Opener == OpenerBurstMode.TwoGcd
        && IsInitialOpenerScenario(scenario)
        && time < 20
        && context.TargetAvailable
        && context.RotationMode == RotationMode.Full
        && gcd is "SoulSlice" or "SoulScythe"
        && Unlocked(scenario.Level, "Gluttony")
        && state.RedGauge <= 50
        && state.BlueGauge < 50
        && state.BlueSouls == 0
        && state.Reaver == ReaverState.None;

    private static bool IsNormalOpenerDeathsDesignGcd(ScenarioDefinition scenario, RprState state, RprContext context, double time, string? gcd)
        => !ShouldUseDancingMadProfile(scenario)
        && scenario.Opener == OpenerBurstMode.TwoGcd
        && IsInitialOpenerScenario(scenario)
        && time < 10
        && context.TargetAvailable
        && context.RotationMode == RotationMode.Full
        && gcd is "ShadowOfDeath" or "WhorlOfDeath"
        && Unlocked(scenario.Level, "SoulSlice")
        && !state.SoulSliceUsed
        && state.BlueSouls == 0
        && state.Reaver == ReaverState.None;

    private static bool IsInitialOpenerScenario(ScenarioDefinition scenario)
        => scenario.Category == ScenarioCategory.GcdOpener
        || scenario.Name.StartsWith("opener_", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldUsePotion(ScenarioDefinition scenario, RprState state, RprContext context, double time, bool canArcane, bool earlyOpener, bool openerPotionBeforeArcane, bool evenBurst, bool preArcaneEnshroudPotion, RprTuningProfile profile)
    {
        if (scenario.Potion == PotionMode.Off || state.PotionReadyIn > 0.1 || !Unlocked(scenario.Level, "ArcaneCircle"))
            return false;

        if (ShouldHoldNewBurstForEndingDutyTarget(scenario, context, time))
            return false;

        if (ShouldUseDancingMadProfile(scenario))
        {
            if (time < 30 || !IsInDancingMadWindow(DancingMadPotionWindows, time))
                return false;

            return state.ArcaneCircleLeft > 0
                || canArcane && ShouldQueueArcaneCircleThisGcd(scenario, state, context, time, scenario.Gcd, profile)
                || state.ArcaneCircleReadyIn > scenario.Gcd && state.ArcaneCircleReadyIn <= scenario.Gcd * 3;
        }

        if (ShouldUseWindurstThirdWalkProfile(scenario))
        {
            if (!IsInWindurstWindow(WindurstPotionWindows(scenario.WindurstEncounter), time))
                return false;

            return state.ArcaneCircleLeft > 0
                || canArcane && ShouldQueueArcaneCircleThisGcd(scenario, state, context, time, scenario.Gcd, profile)
                || state.ArcaneCircleReadyIn > scenario.Gcd && state.ArcaneCircleReadyIn <= scenario.Gcd * 3;
        }

        if (scenario.Potion == PotionMode.EvenBurstExceptOpener && time < 30)
            return false;

        if (earlyOpener && canArcane)
            return true;
        if (openerPotionBeforeArcane && context.RotationMode == RotationMode.Full)
            return true;
        if (preArcaneEnshroudPotion)
            return true;
        return evenBurst && state.ArcaneCircleReadyIn > scenario.Gcd && state.ArcaneCircleReadyIn <= scenario.Gcd * 3;
    }

    private static bool ShouldUseDancingMadProfile(ScenarioDefinition scenario)
        => scenario.SkillRotation == SkillRotationMode.DancingMad;

    private static bool ShouldUseWindurstThirdWalkProfile(ScenarioDefinition scenario)
        => scenario.SkillRotation == SkillRotationMode.WindurstThirdWalk
        && scenario.WindurstEncounter != WindurstEncounter.None;

    private static WindurstBurstWindow[] WindurstArcaneCircleWindows(WindurstEncounter encounter)
        => encounter switch
        {
            WindurstEncounter.Shantotto => WindurstShantottoArcaneCircleWindows,
            WindurstEncounter.Alexander => WindurstAlexanderArcaneCircleWindows,
            WindurstEncounter.Promathia => WindurstPromathiaArcaneCircleWindows,
            WindurstEncounter.HollowKing => WindurstHollowKingArcaneCircleWindows,
            _ => NoWindurstWindows
        };

    private static WindurstBurstWindow[] WindurstPotionWindows(WindurstEncounter encounter)
        => encounter switch
        {
            WindurstEncounter.Shantotto => WindurstShantottoPotionWindows,
            WindurstEncounter.HollowKing => WindurstHollowKingPotionWindows,
            _ => NoWindurstWindows
        };

    private static bool IsInWindurstWindow(WindurstBurstWindow[] windows, double time)
        => windows.Any(window => time >= window.Time - window.Before && time <= window.Time + window.After);

    private static bool ShouldHoldForWindurstArcaneCircle(RprState state, WindurstBurstWindow[] windows, double time)
    {
        foreach (var window in windows)
        {
            var startsIn = window.Time - window.Before - time;
            if (startsIn > DancingMadArcaneCircleHoldLead)
                return false;
            if (startsIn > 0)
                return state.ArcaneCircleReadyIn <= startsIn + window.Before + window.After;
        }

        return false;
    }

    private static bool IsInDancingMadWindow(DancingMadBurstWindow[] windows, double time)
        => windows.Any(window => time >= window.Time - window.Before && time <= window.Time + window.After);

    internal static int DancingMadPhaseIndex(double time)
        => time switch
        {
            < 209 => 0,
            < 429 => 1,
            < 728 => 2,
            < 890 => 3,
            _ => 4
        };

    internal static bool DancingMadBurstEnshroudWindowOpen(double time)
    {
        if (time < 40)
            return true;

        return DancingMadArcaneCircleWindows.Any(window => time >= window.Time - 20 && time <= window.Time + 40);
    }

    internal static (int Burst, int Normal) DancingMadEnshroudBudgetForPhase(int phaseIndex)
    {
        var budget = DancingMadEnshroudBudgets[phaseIndex];
        return (budget.Burst, budget.Normal);
    }

    private static DancingMadEnshroudPolicy DancingMadEnshroudUsePolicy(ScenarioDefinition scenario, RprState state, RprContext context, double time)
    {
        if (scenario.SkillRotation != SkillRotationMode.DancingMad || context.RotationMode != RotationMode.Full)
            return DancingMadEnshroudPolicy.StateBased;

        var budget = DancingMadEnshroudBudgets[state.DancingMadEnshroudPhase];
        var burstWindow = DancingMadBurstEnshroudWindowOpen(time);
        var used = burstWindow ? state.DancingMadBurstEnshrouds : state.DancingMadNormalEnshrouds;
        var target = burstWindow ? budget.Burst : budget.Normal;
        return used < target ? DancingMadEnshroudPolicy.Release : DancingMadEnshroudPolicy.Hold;
    }

    private static bool IsInDancingMadArcaneCircleWindow(double time, RprTuningProfile profile)
    {
        for (var i = 0; i < DancingMadArcaneCircleWindows.Length; ++i)
        {
            var window = DancingMadArcaneCircleWindow(i, profile);
            if (time >= window.Time - window.Before && time <= window.Time + window.After)
                return true;
        }

        return false;
    }

    private static DancingMadBurstWindow DancingMadArcaneCircleWindow(int index, RprTuningProfile profile)
        => (profile.DancingMadMedianArcaneCircleAnchorMask & 1 << index) != 0
            ? DancingMadMedianArcaneCircleWindows[index]
            : DancingMadArcaneCircleWindows[index];

    private static bool ShouldHoldForDancingMadArcaneCircle(RprState state, double time, RprTuningProfile profile)
    {
        for (var i = 0; i < DancingMadArcaneCircleWindows.Length; ++i)
        {
            var window = DancingMadArcaneCircleWindow(i, profile);
            var startsIn = window.Time - window.Before - time;
            if (startsIn > DancingMadArcaneCircleHoldLead)
                return false;

            if (startsIn > 0)
                return state.ArcaneCircleReadyIn <= startsIn + window.Before + window.After;
        }

        return false;
    }

    private static bool ShouldPreArcaneDeathsDesign(ScenarioDefinition scenario, RprState state, RprContext context, double time, RprTuningProfile profile)
    {
        if (context.RotationMode != RotationMode.Full || state.BlueSouls <= 1 || state.BlueSouls > 4)
            return false;
        if (!Unlocked(scenario.Level, "ArcaneCircle") || state.ArcaneCircleLeft > 0 || state.IdealHost || state.PerfectioParata)
            return false;
        if (state.ArcaneCircleReadyIn > BurstPlanGcdLength(scenario) * 1.5)
            return false;
        return ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(scenario, state, context, time);
    }

    private static bool ShouldRefreshDeathsDesignDuringAnyEnshroud(ScenarioDefinition scenario, RprState state, RprContext context, double time, RprTuningProfile profile)
    {
        if (context.RotationMode != RotationMode.Full || state.BlueSouls <= 1 || state.BlueSouls > 4)
            return false;
        if (state.ArcaneCircleLeft > 0 || state.IdealHost || state.PerfectioParata)
            return false;
        if (time >= 30)
            return false;
        if (!Unlocked(scenario.Level, "Communio") || Unlocked(scenario.Level, "Sacrificium"))
            return false;
        if (IsEvenBurstPlanningWindow(scenario, state, context, time))
            return false;
        if (ShouldPreArcaneDeathsDesign(scenario, state, context, time, profile))
            return false;

        var required = scenario.Gcd + BurstPlanGcdLength(scenario) * state.BlueSouls + 1.0;
        return state.DeathsDesignLeft <= required;
    }

    private static bool ShouldRefreshDeathsDesign(ScenarioDefinition scenario, RprState state, RprContext context, double time, RprTuningProfile profile)
        => DeathsDesignRefreshReason(scenario, state, context, time, profile).Length > 0;

    private static string DeathsDesignRefreshReason(ScenarioDefinition scenario, RprState state, RprContext context, double time, RprTuningProfile profile)
    {
        if (!context.FallbackTargetAvailable || !Unlocked(scenario.Level, "ShadowOfDeath"))
            return "";
        if (scenario.FinalTwoGcdKill && scenario.KillTime - time <= scenario.Gcd * 2.2)
            return "";
        if (state.DeathsDesignLeft <= scenario.Gcd + 0.3)
            return "emergency";

        if (ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(scenario, state, context, time)
            || ShouldDeferDeathsDesignRefreshToPreArcaneEnshroud(scenario, state, context))
            return "";

        if (ShouldRefreshDeathsDesignBeforeAnyEnshroud(scenario, state, context, time, state.DeathsDesignLeft, profile))
            return "pre_any_enshroud";

        var arcaneCircleQueuedThisGcd = ShouldQueueArcaneCircleThisGcd(scenario, state, context, time, scenario.Gcd, profile);
        if (state.ArcaneCircleLeft <= 0
            && arcaneCircleQueuedThisGcd
            && scenario.KillTime - time > scenario.Gcd * 2.2
            && state.DeathsDesignLeft <= scenario.Gcd + profile.ArcaneStartDeathsDesignCoverage)
            return "arcane_start_coverage";

        var preBurstWindow = state.ArcaneCircleReadyIn > BurstPlanGcdLength(scenario)
            && state.ArcaneCircleReadyIn <= BurstPlanGcdLength(scenario) * 4.0;
        if (!preBurstWindow)
            return "";

        var twoRefreshEvenBurst = ShouldUseTwoDeathsDesignRefreshesForEvenBurst(scenario, state, context, time, state.DeathsDesignLeft, profile);
        var oneRefreshEvenBurst = !twoRefreshEvenBurst && ShouldUseOneDeathsDesignRefreshForEvenBurst(scenario, state, context, time, state.DeathsDesignLeft, profile);
        if (twoRefreshEvenBurst)
            return "two_refresh_pre_burst";
        if (oneRefreshEvenBurst)
            return "one_refresh_pre_burst";
        return state.DeathsDesignLeft < 30 ? "pre_burst_extend" : "";
    }

    private static bool ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(ScenarioDefinition scenario, RprState state, RprContext context, double time)
        => context.RotationMode == RotationMode.Full
        && (IsEvenBurstPlanningWindow(scenario, state, context, time) || scenario.ArcaneCircleFourSecondDisplay)
        && state.ArcaneCircleLeft <= 0
        && state.Reaver == ReaverState.None
        && Unlocked(scenario.Level, "Perfectio")
        && !state.PerfectioParata
        && !state.PlentifulHarvestReady
        && !state.IdealHost
        && (state.BlueSouls > 0 || state.BlueGauge >= 50)
        && (state.BlueSouls > 0 ? state.DeferredPreArcaneEnshroud : state.ArcaneCircleReadyIn > 0)
        && state.ArcaneCircleReadyIn <= PreArcaneEnshroudEntryWindow
        && state.DeathsDesignLeft > 0
        && state.DeathsDesignLeft <= 30
        && (state.BlueSouls > 0 || state.DeathsDesignLeft > scenario.Gcd + 1.8);

    private static bool ShouldDeferDeathsDesignRefreshToPreArcaneEnshroud(ScenarioDefinition scenario, RprState state, RprContext context)
        => context.RotationMode == RotationMode.Full
        && state.BlueSouls == 0
        && state.Reaver == ReaverState.None
        && !state.PerfectioParata
        && !state.PlentifulHarvestReady
        && !state.IdealHost
        && state.BlueGauge >= 50
        && state.ArcaneCircleLeft <= 0
        && state.ArcaneCircleReadyIn > BurstPlanGcdLength(scenario) * 2.5
        && (state.ArcaneCircleReadyIn <= BurstPlanGcdLength(scenario) * 3.5
            || state.ArcaneCircleReadyIn <= BurstPlanGcdLength(scenario) * 4.0 && state.BlueGauge == 100 && state.RedGauge <= 50 && state.SoulSliceCharges >= 1)
        && state.DeathsDesignLeft > scenario.Gcd + BurstPlanGcdLength(scenario) + 1.8;

    private static double BurstPlanGcdLength(ScenarioDefinition scenario)
        => BurstReferenceGcdLength;

    private static double DeathsDesignLeftAfterOneRefresh(double timer)
        => Math.Min(60, Math.Max(0, timer) + 30);

    private static bool IsEvenBurstPlanningWindow(ScenarioDefinition scenario, RprState state, RprContext context, double time)
    {
        if (context.RotationMode != RotationMode.Full || !Unlocked(scenario.Level, "ArcaneCircle"))
            return false;
        if (time < 90)
            return false;

        return state.ArcaneCircleReadyIn <= 25
            || state.ArcaneCircleReadyIn >= 95
            || state.ArcaneCircleLeft > 0
            || state.IdealHost;
    }

    private static int ProjectedShroudFromCommittedActions(RprState state)
    {
        var committedReaverStacks = state.Reaver != ReaverState.None ? Math.Max(1, state.ReaverStacks) : 0;
        return Math.Min(100, state.BlueGauge + committedReaverStacks * 10);
    }

    private static EvenBurstShroudPlan BuildEvenBurstShroudPlan(ScenarioDefinition scenario, RprState state, RprContext context, double time, double currentGcd)
    {
        var rotationProgressed = time >= 90 || scenario.InitialArcaneCircleReadyIn > 0 || state.ArcaneCircleUseCount > 0;
        var active = context.RotationMode == RotationMode.Full
            && scenario.SkillRotation == SkillRotationMode.Normal
            && rotationProgressed
            && Unlocked(scenario.Level, "ArcaneCircle")
            && Unlocked(scenario.Level, "Enshroud")
            && state.ArcaneCircleLeft <= 0
            && state.ArcaneCircleReadyIn <= EvenBurstShroudPlanLead;
        if (!active)
            return default;

        var projectedShroud = ProjectedShroudFromCommittedActions(state);
        var missingShroud = Math.Max(0, EvenBurstRequiredShroud - projectedShroud);
        var deadline = Math.Max(0, state.ArcaneCircleReadyIn - PreArcaneEnshroudEntryWindow);
        var availableGcds = Math.Max(0, (int)Math.Floor(Math.Max(0, deadline - currentGcd) / scenario.Gcd));
        var gluttonyAvailableByDeadline = Unlocked(scenario.Level, "Gluttony")
            && context.BestRangedAoeTargetAvailable
            && state.GluttonyReadyIn <= deadline;
        var firstSoulSpendShroud = gluttonyAvailableByDeadline ? 20 : 10;
        var requiredSoulSpends = missingShroud <= 0
            ? 0
            : 1 + Math.Max(0, missingShroud - firstSoulSpendShroud + 9) / 10;
        var requiredRedGauge = requiredSoulSpends * 50;
        var soulGenerationGcds = Math.Max(0, requiredRedGauge - state.RedGauge + 49) / 50;
        var requiredShroudGcds = (missingShroud + 9) / 10;
        var minorSoulSpenderAvailable = Unlocked(scenario.Level, "BloodStalk")
            && context.MeleeAvailable
            && context.FallbackTargetAvailable;
        var soulSpenderAvailable = requiredSoulSpends == 0
            || gluttonyAvailableByDeadline && (requiredSoulSpends == 1 || minorSoulSpenderAvailable)
            || minorSoulSpenderAvailable;
        var soulGeneratorAvailable = state.SoulSliceCharges >= 1
            && (Unlocked(scenario.Level, "SoulSlice") && context.MeleeAvailable && context.FallbackTargetAvailable
                || context.AoeTargets > 2 && Unlocked(scenario.Level, "SoulScythe"));
        var availableRedGauge = state.RedGauge + (soulGeneratorAvailable ? 50 : 0);
        var resourcePathAvailable = soulSpenderAvailable && requiredRedGauge <= availableRedGauge;
        return new(true, EvenBurstRequiredShroud, projectedShroud, availableGcds, soulGenerationGcds + requiredShroudGcds, resourcePathAvailable);
    }

    private static bool ShouldSpendRedForPreBurstSoulSlice(ScenarioDefinition scenario, RprState state, RprContext context, double time)
        => IsEvenBurstPlanningWindow(scenario, state, context, time)
        && state.ArcaneCircleLeft <= 0
        && Unlocked(scenario.Level, "SoulSlice")
        && state.SoulSliceCharges >= 1
        && state.BlueGauge >= 50
        && state.RedGauge > 50
        && state.ArcaneCircleReadyIn > BurstPlanGcdLength(scenario) * 2.5
        && state.ArcaneCircleReadyIn <= 20
        && (state.MaxSoulSliceCharges - state.SoulSliceCharges) * 30 <= state.ArcaneCircleReadyIn + scenario.Gcd + 9.0;

    private static bool ShouldSpendCappedRedBeforeEvenBurstEnshroud(ScenarioDefinition scenario, RprState state, RprContext context, double time)
        => IsEvenBurstPlanningWindow(scenario, state, context, time)
        && state.ArcaneCircleLeft <= 0
        && Unlocked(scenario.Level, "BloodStalk")
        && state.RedGauge == 100
        && state.BlueGauge == 100
        && state.ArcaneCircleReadyIn > BurstPlanGcdLength(scenario) * 2.5
        && state.ArcaneCircleReadyIn <= BurstPlanGcdLength(scenario) * 5.0
        && (state.MaxSoulSliceCharges - state.SoulSliceCharges) * 30 > state.ArcaneCircleReadyIn + scenario.Gcd + 9.0;

    private static bool ShouldUseTwoDeathsDesignRefreshesForEvenBurst(ScenarioDefinition scenario, RprState state, RprContext context, double time, double timer, RprTuningProfile profile)
    {
        if (!IsEvenBurstPlanningWindow(scenario, state, context, time))
            return false;

        if (state.BlueSouls > 0)
        {
            var required = state.ArcaneCircleReadyIn + profile.EvenBurstRemainingFromPreArcaneEnshroud + profile.EvenBurstDDSecondRefreshSafety;
            return timer <= required;
        }

        var afterOneRefresh = DeathsDesignLeftAfterOneRefresh(timer);
        var requiredBeforeBurst = state.ArcaneCircleReadyIn + profile.EvenBurstRemainingAfterArcaneCircle + profile.EvenBurstDDSecondRefreshSafety;
        return afterOneRefresh <= requiredBeforeBurst;
    }

    private static bool ShouldUseOneDeathsDesignRefreshForEvenBurst(ScenarioDefinition scenario, RprState state, RprContext context, double time, double timer, RprTuningProfile profile)
        => IsEvenBurstPlanningWindow(scenario, state, context, time)
        && state.BlueSouls == 0
        && timer < 30
        && !ShouldUseTwoDeathsDesignRefreshesForEvenBurst(scenario, state, context, time, timer, profile);

    private static bool ShouldRefreshDeathsDesignBeforeAnyEnshroud(ScenarioDefinition scenario, RprState state, RprContext context, double time, double timer, RprTuningProfile profile)
    {
        if (context.RotationMode != RotationMode.Full)
            return false;

        if (!state.IdealHost && ShouldHoldNewBurstForEndingDutyTarget(scenario, context, time))
            return false;

        if (state.BlueSouls > 0 || state.Reaver != ReaverState.None)
            return false;

        if (!Unlocked(scenario.Level, "Enshroud"))
            return false;

        if (IsEvenBurstPlanningWindow(scenario, state, context, time))
            return false;

        if (state.PerfectioParata || state.PlentifulHarvestReady)
            return false;

        var canStartEnshroud = state.BlueGauge >= 50 || state.IdealHost;
        if (!canStartEnshroud)
            return false;

        var required = scenario.Gcd + BurstPlanGcdLength(scenario) * profile.PreAnyEnshroudGcds + 1.0;
        return timer <= required;
    }

    private static bool ShouldBlockEnshroudForDeathsDesign(ScenarioDefinition scenario, RprState state, RprContext context, double time, RprTuningProfile profile)
        => ShouldRefreshDeathsDesignBeforeAnyEnshroud(scenario, state, context, time, state.DeathsDesignLeft, profile);

    private static bool ShouldUseSoulSlice(ScenarioDefinition scenario, RprState state, RprContext context, double time, RprTuningProfile profile)
    {
        var canSoulSlice = Unlocked(scenario.Level, "SoulSlice") && context.MeleeAvailable && context.FallbackTargetAvailable;
        var canSoulScythe = context.AoeTargets > 2 && Unlocked(scenario.Level, "SoulScythe");
        if (state.SoulSliceCharges < 1 || !canSoulSlice && !canSoulScythe)
            return false;
        if (IsSoulSliceTimingScenario(scenario) && (state.ArcaneCircleLeft > 0 || state.BlueSouls > 0 || state.Reaver != ReaverState.None || state.PerfectioParata || state.PlentifulHarvestReady))
            return false;
        if (IsSoulSliceTimingScenario(scenario) && scenario.FinalTwoGcdKill && scenario.KillTime - time <= scenario.Gcd * 2.2)
            return false;
        var overcapSoon = (state.MaxSoulSliceCharges - state.SoulSliceCharges) * 30 <= scenario.Gcd;
        var plannedSoulSlice = BuildEvenBurstShroudPlan(scenario, state, context, time, 0).Urgent
            && state.RedGauge < 50;
        // A spender selected here executes after the GCD, so it cannot make room for Soul Slice.
        if (state.RedGauge > 50)
            return false;
        if (!plannedSoulSlice && !ShouldHoldNormalOpenerArcaneCircleForSoulSlice(scenario, state, context, time) && state.ArcaneCircleReadyIn <= profile.SoulSliceBurstSoonSeconds && !overcapSoon)
            return false;
        return true;
    }

    private static bool FastGcdMovesLateSoulSliceBeforePh(ScenarioDefinition scenario)
        => scenario.Gcd < 2.47;

    private static bool ShouldPrioritizeComboAfterBurst(ScenarioDefinition scenario, RprState state, RprContext context, RprTuningProfile profile)
    {
        if (state.BlueSouls > 0 || state.Reaver != ReaverState.None || state.PerfectioParata || state.PlentifulHarvestReady)
            return false;
        if (state.ArcaneCircleLeft > 0 || state.ComboLast is not ("Slice" or "WaxingSlice"))
            return false;

        return profile.ComboPriorityScope switch
        {
            1 => state.TimeSinceArcaneCircleEnded is >= 0 and <= 20,
            2 => state.TimeSincePerfectioUsed is >= 0 and <= 15,
            3 => state.SoulSliceCharges < 1.9,
            0 => true,
            _ => false
        };
    }

    private static bool ShouldPrioritizePostPerfectioCombo(RprState state, RprContext context)
        => state.PostPerfectioPriorityActive
        && state.BlueSouls == 0
        && state.Reaver == ReaverState.None
        && !state.PerfectioParata
        && !state.PlentifulHarvestReady
        && context.FallbackTargetAvailable
        && state.ComboLast is ("Slice" or "WaxingSlice");

    private static bool ShouldPrioritizePostPerfectioSoulSlice(ScenarioDefinition scenario, RprState state, RprContext context, double time)
        => (state.PostPerfectioPriorityActive || PerfectioJustUsed(scenario, state))
        && state.ComboLast is not ("Slice" or "WaxingSlice")
        && !(scenario.FinalTwoGcdKill && scenario.KillTime - time <= scenario.Gcd * 2.2)
        && state.BlueSouls == 0
        && state.Reaver == ReaverState.None
        && !state.PerfectioParata
        && !state.PlentifulHarvestReady
        && state.RedGauge < 50
        && state.SoulSliceCharges >= 1
        && (context.MeleeAvailable && context.FallbackTargetAvailable && Unlocked(scenario.Level, "SoulSlice") || context.AoeTargets > 2 && Unlocked(scenario.Level, "SoulScythe"));

    private static bool PerfectioJustUsed(ScenarioDefinition scenario, RprState state)
        => state.TimeSincePerfectioUsed is > 0 && state.TimeSincePerfectioUsed <= scenario.Gcd + 0.1;

    private static bool IsNormalOpenerPostPerfectioSequence(ScenarioDefinition scenario, RprState state, double time)
        => scenario.Opener == OpenerBurstMode.TwoGcd
        && IsInitialOpenerScenario(scenario)
        && time < 40
        && state.PostPerfectioPriorityActive;

    private static bool ShouldUseLateBurstSoulSliceBeforePlentifulHarvest(ScenarioDefinition scenario, RprState state, RprContext context, double time, RprTuningProfile profile)
    {
        if (context.RotationMode != RotationMode.Full || !FastGcdMovesLateSoulSliceBeforePh(scenario))
            return false;

        if (state.BlueSouls > 0 || state.Reaver != ReaverState.None || state.PerfectioParata)
            return false;

        if (!state.PlentifulHarvestReady || state.IdealHost)
            return false;

        if (profile.LateBurstSoulSlicePolicy switch
        {
            1 => state.BlueGauge > 50,
            2 => false,
            3 => !CanCompleteLateBurstAfterSoulSlice(scenario, time),
            _ => state.BlueGauge >= 50
        })
            return false;

        if (state.ArcaneCircleLeft <= 0 && state.ArcaneCircleReadyIn > 10)
            return false;

        var canSoulSlice = Unlocked(scenario.Level, "SoulSlice") && state.SoulSliceCharges >= 1 && context.MeleeAvailable && context.FallbackTargetAvailable;
        var canSoulScythe = Unlocked(scenario.Level, "SoulScythe") && state.SoulSliceCharges >= 1 && context.AoeTargets > 2;
        if (!canSoulSlice && !canSoulScythe)
            return false;

        if (state.RedGauge > 50)
            return false;

        if (state.DeathsDesignLeft <= scenario.Gcd + 3.0)
            return false;

        return true;
    }

    private static bool CanCompleteLateBurstAfterSoulSlice(ScenarioDefinition scenario, double time)
    {
        var availableUntil = scenario.KillTime;
        foreach (var targetLoss in scenario.Events.Where(e => e.Type == ScenarioEventType.TargetLost && e.Start > time))
            availableUntil = Math.Min(availableUntil, targetLoss.Start);

        return availableUntil - time >= scenario.Gcd + 13.0;
    }

    private static double TimeUntilTargetUnavailable(ScenarioDefinition scenario, double time)
    {
        var unavailableIn = Math.Max(0, scenario.KillTime - time);
        foreach (var targetLoss in scenario.Events.Where(e => e.Type == ScenarioEventType.TargetLost && e.Start > time))
            unavailableIn = Math.Min(unavailableIn, targetLoss.Start - time);

        return unavailableIn;
    }

    private static bool ShouldHoldNewBurstForEndingDutyTarget(ScenarioDefinition scenario, RprContext context, double time)
    {
        if (!context.TargetAvailable || !context.FallbackTargetAvailable)
            return false;

        return !scenario.FinalEncounter && Active(scenario, ScenarioEventType.TargetDying, time);
    }

    private static bool ShouldHoldNewBurstForUpcomingDowntime(ScenarioDefinition scenario, RprState state, RprContext context, double time, double requiredUptime, RprTuningProfile profile)
    {
        if (!profile.TimelineAwareBurstHold
            || scenario.Potion != PotionMode.Off
            || time < 30
            || !context.TargetAvailable
            || requiredUptime <= 0)
            return false;

        var nextLoss = scenario.Events
            .Where(e => e.Type == ScenarioEventType.TargetLost && e.Start > time && e.End > e.Start && e.End < scenario.KillTime)
            .MinBy(e => e.Start);
        if (nextLoss == null || nextLoss.Start - time > requiredUptime)
            return false;

        var remaining = scenario.KillTime - time;
        var delayedBy = nextLoss.End - time;
        if (Unlocked(scenario.Level, "Gluttony") && state.GluttonyReadyIn <= delayedBy)
            return false;

        var effectiveRecast = ArcaneCircleRecast - Math.Max(0, scenario.Gcd - 0.8);
        var usesNow = 1 + (int)Math.Floor(Math.Max(0, remaining - 0.1) / effectiveRecast);
        var usesAfterDowntime = 1 + (int)Math.Floor(Math.Max(0, remaining - delayedBy - 0.1) / effectiveRecast);
        return usesAfterDowntime >= usesNow;
    }

    public static bool Unlocked(int level, string action)
        => action switch
        {
            "Slice" => level >= 1,
            "WaxingSlice" => level >= 5,
            "ShadowOfDeath" => level >= 10,
            "Harpe" => level >= 15,
            "SpinningScythe" => level >= 25,
            "InfernalSlice" => level >= 30,
            "WhorlOfDeath" => level >= 35,
            "NightmareScythe" => level >= 45,
            "TrueNorth" or "BloodStalk" => level >= 50,
            "GrimSwathe" => level >= 55,
            "SoulSlice" => level >= 60,
            "SoulScythe" => level >= 65,
            "Gibbet" or "Gallows" or "Guillotine" or "UnveiledGibbet" or "UnveiledGallows" => level >= 70,
            "ArcaneCircle" => level >= 72,
            "Gluttony" => level >= 76,
            "Enshroud" or "VoidReaping" or "CrossReaping" or "GrimReaping" => level >= 80,
            "Soulsow" or "HarvestMoon" => level >= 82,
            "LemuresSlice" or "LemuresScythe" => level >= 86,
            "PlentifulHarvest" => level >= 88,
            "Communio" => level >= 90,
            "Sacrificium" => level >= 92,
            "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine" => level >= 96,
            "Perfectio" => level >= 100,
            "Potion" => true,
            _ => false
        };

    private static bool IsSoulSliceTimingScenario(ScenarioDefinition scenario)
        => scenario.Name is "soul_slice_red_50_normal"
        or "soul_slice_red_60_overcap_soon"
        or "soul_slice_red_90_overcap_soon"
        or "soul_slice_red_100_overcap_soon"
        or "soul_slice_combo_protected_red_60"
        or "soul_slice_combo_protected_red_100"
        or "soul_slice_before_arcane_6s"
        or "soul_slice_before_arcane_20s"
        or "soul_slice_basic_mode_red_100"
        or "soul_scythe_aoe_3_targets"
        or "soul_scythe_aoe_to_single_switch"
        or "soul_slice_target_dies_2gcd";
}

public sealed class RprState
{
    private const double ThirtySecondStatusDuration = 30.0;
    private const double PerfectioParataDuration = 30.0;
    private const double EnhancedReaverDuration = 60.0;
    private const double EnshroudDuration = 30.0;
    private const double TrueNorthDuration = 10.0;
    private const double TrueNorthRecharge = 45.0;
    private const double OgcdWeaveOffset = 0.7;
    private const double PotionRecast = 270.0;
    private readonly ScenarioDefinition _scenario;
    private double _lastTime;
    private double _gluttonyReadySince = double.NaN;
    private bool _externalEnshroudActive;
    private bool _externalPerfectioParataActive;
    private bool _externalReaverActive;
    private bool _externalExecutionerActive;
    private bool _externalEnhancedHarpeActive;
    private int _processedTargetKills;
    private readonly ScenarioEvent[] _partyHits;
    private readonly HashSet<int> _sacrificeContributors = [];
    private int _nextPartyHit;
    private double _sacrificeWindowStartedAt = double.NaN;

    public RprState(ScenarioDefinition scenario)
    {
        _scenario = scenario;
        _partyHits = scenario.Events.Where(e => e.Type == ScenarioEventType.PartyHit).OrderBy(e => e.Start).ToArray();
        DeathsDesignLeft = scenario.Level >= 10 ? Math.Clamp(scenario.DeathsDesignLeft, 0, 60) : 0;
        RedGauge = scenario.Level >= 50 ? Math.Clamp(scenario.RedGauge, 0, 100) : 0;
        BlueGauge = scenario.Level >= 80 ? Math.Clamp(scenario.BlueGauge, 0, 100) : 0;
        SoulSliceCharges = scenario.Level >= 60 ? Math.Clamp(scenario.SoulSliceCharges, 0, MaxSoulSliceCharges) : 0;
        Reaver = scenario.InitialReaver == ReaverState.Executioner && scenario.Level < 96
            ? ReaverState.None
            : scenario.Level >= 70 ? scenario.InitialReaver : ReaverState.None;
        ReaverStacks = Reaver == ReaverState.None ? 0 : Math.Max(1, scenario.InitialReaverStacks);
        ReaverLeft = Reaver == ReaverState.None ? 0 : ThirtySecondStatusDuration;
        EnhancedGibbetLeft = scenario.Level >= 70 ? Math.Clamp(scenario.InitialEnhancedGibbetLeft, 0, EnhancedReaverDuration) : 0;
        EnhancedGallowsLeft = scenario.Level >= 70 ? Math.Clamp(scenario.InitialEnhancedGallowsLeft, 0, EnhancedReaverDuration) : 0;
        ArcaneCircleReadyIn = scenario.Level >= 72
            ? Math.Clamp(scenario.InitialArcaneCircleReadyIn > 0 ? scenario.InitialArcaneCircleReadyIn : scenario.ArcaneCircleFourSecondDisplay ? 4 : 0, 0, 120)
            : 0;
        ArcaneCircleLeft = scenario.Level >= 72 ? Math.Clamp(scenario.InitialArcaneCircleLeft, 0, 20) : 0;
        PerfectioParataLeft = scenario.Level >= 100 ? Math.Clamp(scenario.InitialPerfectioParataLeft, 0, PerfectioParataDuration) : 0;
        PerfectioParata = PerfectioParataLeft > 0;
        ImmortalSacrifice = scenario.Level >= 88 ? Math.Clamp(scenario.InitialImmortalSacrifice, 0, 8) : 0;
        ImmortalSacrificeLeft = ImmortalSacrifice > 0 ? ThirtySecondStatusDuration : 0;
        BloodsownLeft = scenario.Level >= 88 ? Math.Clamp(scenario.InitialBloodsownLeft, 0, RprRotationEmulator.BloodsownCircleDuration) : 0;
        IdealHost = scenario.Level >= 88 && scenario.InitialIdealHost;
        IdealHostLeft = IdealHost ? ThirtySecondStatusDuration : 0;
        GluttonyReadyIn = scenario.Level >= 76 ? Math.Clamp(scenario.InitialGluttonyReadyIn, 0, 60) : 0;
        Soulsow = scenario.Level >= 82 && scenario.InitialSoulsow;
        ComboLast = scenario.InitialComboLast;
        ComboRemaining = ComboLast.Length > 0 ? Math.Clamp(scenario.InitialComboRemaining, 0, ThirtySecondStatusDuration) : 0;
        TrueNorthCharges = scenario.Level >= 50 ? 2 : 0;
        SoulSliceUsed = scenario.InitialSoulSliceUsed;
        DancingMadOpenerStage = scenario.SkillRotation == SkillRotationMode.DancingMad && !scenario.InitialSoulSliceUsed
            ? DancingMadOpenerStage.Harpe
            : DancingMadOpenerStage.Complete;
        DancingMadEnshroudPhase = RprRotationEmulator.DancingMadPhaseIndex(0);
    }

    public double DeathsDesignLeft { get; private set; }
    public int RedGauge { get; private set; }
    public int BlueGauge { get; private set; }
    public double SoulSliceCharges { get; private set; }
    public double ArcaneCircleReadyIn { get; private set; }
    public double ArcaneCircleLeft { get; private set; }
    public double GluttonyReadyIn { get; private set; }
    public double EnshroudReadyIn { get; private set; }
    public double EnshroudLeft { get; private set; }
    public int BlueSouls { get; private set; }
    public int PurpleSouls { get; private set; }
    public bool Oblatio { get; private set; }
    public double OblatioLeft { get; private set; }
    public ReaverState Reaver { get; private set; }
    public int ReaverStacks { get; private set; }
    public double ReaverLeft { get; private set; }
    public double EnhancedGibbetLeft { get; private set; }
    public double EnhancedGallowsLeft { get; private set; }
    public double EnhancedVoidReapingLeft { get; private set; }
    public double EnhancedCrossReapingLeft { get; private set; }
    public bool PerfectioParata { get; private set; }
    public double PerfectioParataLeft { get; private set; }
    public bool IdealHost { get; private set; }
    public double IdealHostLeft { get; private set; }
    public int ImmortalSacrifice { get; private set; }
    public double ImmortalSacrificeLeft { get; private set; }
    public double BloodsownLeft { get; private set; }
    public double CircleOfSacrificeLeft { get; private set; }
    public bool PerfectioOcculta { get; private set; }
    public double PerfectioOccultaLeft { get; private set; }
    public bool PlentifulHarvestReady => ImmortalSacrifice > 0 && BloodsownLeft <= 0;
    public bool PotionUsed { get; private set; }
    public double PotionReadyIn { get; private set; }
    public int PotionUseCount { get; private set; }
    public int ArcaneCircleUseCount { get; private set; }
    public bool Soulsow { get; private set; }
    public bool SoulSliceUsed { get; private set; }
    public string ComboLast { get; private set; } = "";
    public double ComboRemaining { get; private set; }
    public double TrueNorthCharges { get; private set; }
    public double TrueNorthLeft { get; private set; }
    public double EnhancedHarpeLeft { get; private set; }
    public double MaxSoulSliceCharges => _scenario.Level >= 78 ? 2 : 1;
    public double TimeSinceArcaneCircleEnded { get; private set; } = -1;
    public double TimeSincePerfectioUsed { get; private set; } = -1;
    public bool PostPerfectioPriorityComplete { get; private set; }
    public bool DeferredPreArcaneEnshroud { get; private set; }
    public int DancingMadEnshroudPhase { get; private set; }
    public int DancingMadBurstEnshrouds { get; private set; }
    public int DancingMadNormalEnshrouds { get; private set; }
    internal DancingMadOpenerStage DancingMadOpenerStage { get; private set; }
    public bool PostPerfectioPriorityActive => TimeSincePerfectioUsed is >= 0 and <= 15 && !PostPerfectioPriorityComplete;

    public void AdvanceTo(double time)
    {
        // Rounded frame timestamps must never resurrect expired statuses by producing a negative delta.
        var delta = Math.Max(0, time - _lastTime);
        _lastTime = Math.Max(_lastTime, time);
        if (_scenario.SkillRotation == SkillRotationMode.DancingMad)
        {
            var phaseIndex = RprRotationEmulator.DancingMadPhaseIndex(time);
            if (phaseIndex != DancingMadEnshroudPhase)
            {
                DancingMadEnshroudPhase = phaseIndex;
                DancingMadBurstEnshrouds = 0;
                DancingMadNormalEnshrouds = 0;
            }
        }
        DeathsDesignLeft = Math.Max(0, DeathsDesignLeft - delta);
        ArcaneCircleReadyIn = Math.Max(0, ArcaneCircleReadyIn - delta);
        var previousArcaneCircleLeft = ArcaneCircleLeft;
        ArcaneCircleLeft = Math.Max(0, ArcaneCircleLeft - delta);
        if (previousArcaneCircleLeft > 0 && ArcaneCircleLeft <= 0)
            TimeSinceArcaneCircleEnded = Math.Max(0, delta - previousArcaneCircleLeft);
        else if (ArcaneCircleLeft <= 0 && TimeSinceArcaneCircleEnded >= 0)
            TimeSinceArcaneCircleEnded += delta;
        if (TimeSincePerfectioUsed >= 0)
            TimeSincePerfectioUsed += delta;
        var previousGluttonyReadyIn = GluttonyReadyIn;
        GluttonyReadyIn = Math.Max(0, GluttonyReadyIn - delta);
        if (previousGluttonyReadyIn > 0 && GluttonyReadyIn <= 0)
            _gluttonyReadySince = _lastTime - (delta - previousGluttonyReadyIn);
        PotionReadyIn = Math.Max(0, PotionReadyIn - delta);
        EnshroudReadyIn = Math.Max(0, EnshroudReadyIn - delta);
        var previousEnshroudLeft = EnshroudLeft;
        EnshroudLeft = Math.Max(0, EnshroudLeft - delta);
        if (previousEnshroudLeft > 0 && EnshroudLeft <= 0)
            EndEnshroud();
        OblatioLeft = Math.Max(0, OblatioLeft - delta);
        if (OblatioLeft <= 0)
            Oblatio = false;
        ReaverLeft = Math.Max(0, ReaverLeft - delta);
        if (ReaverLeft <= 0)
            ClearReaver();
        EnhancedGibbetLeft = Math.Max(0, EnhancedGibbetLeft - delta);
        EnhancedGallowsLeft = Math.Max(0, EnhancedGallowsLeft - delta);
        EnhancedVoidReapingLeft = Math.Max(0, EnhancedVoidReapingLeft - delta);
        EnhancedCrossReapingLeft = Math.Max(0, EnhancedCrossReapingLeft - delta);
        PerfectioParataLeft = Math.Max(0, PerfectioParataLeft - delta);
        if (PerfectioParataLeft <= 0)
            PerfectioParata = false;
        IdealHostLeft = Math.Max(0, IdealHostLeft - delta);
        if (IdealHostLeft <= 0)
            IdealHost = false;
        ImmortalSacrificeLeft = Math.Max(0, ImmortalSacrificeLeft - delta);
        if (ImmortalSacrificeLeft <= 0)
            ImmortalSacrifice = 0;
        BloodsownLeft = Math.Max(0, BloodsownLeft - delta);
        CircleOfSacrificeLeft = Math.Max(0, CircleOfSacrificeLeft - delta);
        PerfectioOccultaLeft = Math.Max(0, PerfectioOccultaLeft - delta);
        if (PerfectioOccultaLeft <= 0)
            PerfectioOcculta = false;
        ComboRemaining = Math.Max(0, ComboRemaining - delta);
        if (ComboRemaining <= 0)
            ComboLast = "";
        TrueNorthLeft = Math.Max(0, TrueNorthLeft - delta);
        EnhancedHarpeLeft = Math.Max(0, EnhancedHarpeLeft - delta);
        if (_scenario.Level >= 50)
            TrueNorthCharges = Math.Min(2, TrueNorthCharges + delta / TrueNorthRecharge);
        if (_scenario.Level >= 60)
            SoulSliceCharges = Math.Min(MaxSoulSliceCharges, SoulSliceCharges + delta / 30);

        while (_nextPartyHit < _partyHits.Length && _partyHits[_nextPartyHit].Start <= time)
        {
            var hit = _partyHits[_nextPartyHit++];
            if (hit.Value is >= 1 and <= 7 && hit.Value == Math.Truncate(hit.Value)
                && hit.Start >= _sacrificeWindowStartedAt && hit.Start < _sacrificeWindowStartedAt + 5
                && time - hit.Start < ThirtySecondStatusDuration && _sacrificeContributors.Add((int)hit.Value))
            {
                ImmortalSacrifice = Math.Min(8, ImmortalSacrifice + 1);
                ImmortalSacrificeLeft = Math.Max(ImmortalSacrificeLeft, ThirtySecondStatusDuration - (time - hit.Start));
            }
        }
    }

    public void UpdateGluttonyReadySince(double time, RotationMode rotationMode, int level)
    {
        if (rotationMode != RotationMode.Full || level < 76 || GluttonyReadyIn > 0.1)
        {
            _gluttonyReadySince = double.NaN;
            return;
        }

        if (double.IsNaN(_gluttonyReadySince))
            _gluttonyReadySince = time;
    }

    public void SynchronizeExternalState(RprContext context)
    {
        if (context.TargetKillCount > _processedTargetKills)
        {
            var newKills = context.TargetKillCount - _processedTargetKills;
            if (_scenario.Level >= 50 && DeathsDesignLeft > 0)
                RedGauge = Math.Min(100, RedGauge + 10 * newKills);
            DeathsDesignLeft = 0;
            _processedTargetKills = context.TargetKillCount;
        }

        if (context.EventEnshroud && !_externalEnshroudActive && _scenario.Level >= 80 && BlueSouls == 0)
        {
            if (IdealHost)
            {
                IdealHost = false;
                IdealHostLeft = 0;
            }
            else
            {
                BlueGauge = Math.Max(0, BlueGauge - 50);
            }
            BlueSouls = 5;
            PurpleSouls = 0;
            EnshroudLeft = EnshroudDuration;
            EnshroudReadyIn = _scenario.Level >= 92 ? 5 : 15;
            Oblatio = _scenario.Level >= 92;
            OblatioLeft = Oblatio ? ThirtySecondStatusDuration : 0;
            PerfectioParata = false;
            PerfectioParataLeft = 0;
        }

        if (context.PerfectioParata && !_externalPerfectioParataActive && _scenario.Level >= 100)
        {
            PerfectioParata = true;
            PerfectioParataLeft = Math.Max(PerfectioParataLeft, context.PerfectioParataLeft);
        }

        if (context.EventExecutioner && !_externalExecutionerActive && _scenario.Level >= 96)
        {
            Reaver = ReaverState.Executioner;
            ReaverStacks = Math.Max(1, context.EventExecutionerStacks);
            ReaverLeft = ThirtySecondStatusDuration;
        }
        else if (context.EventReaver && !_externalReaverActive && _scenario.Level >= 70 && Reaver != ReaverState.Executioner)
        {
            Reaver = ReaverState.SoulReaver;
            ReaverStacks = Math.Max(1, context.EventReaverStacks);
            ReaverLeft = ThirtySecondStatusDuration;
        }

        if (context.EnhancedHarpe && !_externalEnhancedHarpeActive && _scenario.Level >= 15)
            EnhancedHarpeLeft = Math.Max(EnhancedHarpeLeft, context.EnhancedHarpeLeft);

        _externalEnshroudActive = context.EventEnshroud;
        _externalPerfectioParataActive = context.PerfectioParata;
        _externalReaverActive = context.EventReaver;
        _externalExecutionerActive = context.EventExecutioner;
        _externalEnhancedHarpeActive = context.EnhancedHarpe;
    }

    public bool GluttonyReadyDelayExpired(double time)
    {
        if (double.IsNaN(_gluttonyReadySince) || GluttonyReadyIn > 0.1)
            return false;

        var burstCycle = time % 120;
        var delayLimit = time >= 100 && (burstCycle >= 100 || burstCycle <= 20) ? 20.0 : 3.0;
        return time - _gluttonyReadySince >= delayLimit;
    }

    public string EnshroudReadyReason(int level)
    {
        if (level < 80 || EnshroudReadyIn > 0 || BlueSouls > 0 || Reaver != ReaverState.None || PerfectioParata)
            return "";
        if (IdealHost)
            return "IdealHost";
        return BlueGauge >= 50 ? "BlueGauge" : "";
    }

    public void Apply(ActionFrame frame)
    {
        // Resolve GCD effects at cast completion, then advance through each actual weave.
        // This remains a policy model: continuous target/cast cancellation is tested by xan_timeline_harness.
        AdvanceTo(frame.Time + RprRotationEmulator.ActionCastTime(frame.SelectedGcd, EnhancedHarpeLeft > 0));
        var reaverBeforeGcd = Reaver;
        var comboBeforeGcd = ComboLast;
        switch (frame.SelectedGcd)
        {
            case "ShadowOfDeath":
            case "WhorlOfDeath":
                DeathsDesignLeft = Math.Min(60, DeathsDesignLeft + 30);
                break;
            case "SoulSlice":
            case "SoulScythe":
                SoulSliceCharges = Math.Max(0, SoulSliceCharges - 1);
                SoulSliceUsed = true;
                RedGauge = Math.Min(100, RedGauge + 50);
                break;
            case "PlentifulHarvest":
                ImmortalSacrifice = 0;
                ImmortalSacrificeLeft = 0;
                BloodsownLeft = 0;
                IdealHost = _scenario.Level >= 88;
                IdealHostLeft = IdealHost ? ThirtySecondStatusDuration : 0;
                PerfectioOcculta = _scenario.Level >= 100;
                PerfectioOccultaLeft = PerfectioOcculta ? ThirtySecondStatusDuration : 0;
                break;
            case "Gibbet":
            case "ExecutionersGibbet":
                EnhancedGibbetLeft = 0;
                EnhancedGallowsLeft = EnhancedReaverDuration;
                goto case "Guillotine";
            case "Gallows":
            case "ExecutionersGallows":
                EnhancedGallowsLeft = 0;
                EnhancedGibbetLeft = EnhancedReaverDuration;
                goto case "Guillotine";
            case "Guillotine":
            case "ExecutionersGuillotine":
                ConsumeReaverStack();
                if (PostPerfectioPriorityActive)
                    PostPerfectioPriorityComplete = true;
                if (_scenario.Level >= 80)
                    BlueGauge = Math.Min(100, BlueGauge + 10);
                break;
            case "VoidReaping":
                EnhancedVoidReapingLeft = 0;
                EnhancedCrossReapingLeft = ThirtySecondStatusDuration;
                goto case "GrimReaping";
            case "CrossReaping":
                EnhancedCrossReapingLeft = 0;
                EnhancedVoidReapingLeft = ThirtySecondStatusDuration;
                goto case "GrimReaping";
            case "GrimReaping":
                BlueSouls = Math.Max(0, BlueSouls - 1);
                if (_scenario.Level >= 86)
                    PurpleSouls = Math.Min(5, PurpleSouls + 1);
                if (BlueSouls == 0)
                    EndEnshroud();
                break;
            case "Communio":
                EndEnshroud();
                if (PerfectioOcculta && _scenario.Level >= 100)
                {
                    PerfectioParata = true;
                    PerfectioParataLeft = PerfectioParataDuration;
                }
                PerfectioOcculta = false;
                PerfectioOccultaLeft = 0;
                break;
            case "Perfectio":
                PerfectioParata = false;
                PerfectioParataLeft = 0;
                TimeSincePerfectioUsed = 0;
                PostPerfectioPriorityComplete = !(_scenario.Opener == OpenerBurstMode.TwoGcd
                    && (_scenario.Category == ScenarioCategory.GcdOpener || _scenario.Name.StartsWith("opener_", StringComparison.OrdinalIgnoreCase))
                    && frame.Time < 40)
                    && (ComboLast is not ("Slice" or "WaxingSlice") || _scenario.InitialComboRemaining > frame.Gcd * 3);
                break;
            case "Slice":
                ComboLast = "Slice";
                ComboRemaining = 30;
                if (_scenario.Level >= 50)
                    RedGauge = Math.Min(100, RedGauge + 10);
                break;
            case "WaxingSlice":
                ComboLast = "WaxingSlice";
                ComboRemaining = 30;
                if (_scenario.Level >= 50 && comboBeforeGcd == "Slice")
                    RedGauge = Math.Min(100, RedGauge + 10);
                break;
            case "InfernalSlice":
                ComboLast = "";
                ComboRemaining = 0;
                if (_scenario.Level >= 50 && comboBeforeGcd == "WaxingSlice")
                    RedGauge = Math.Min(100, RedGauge + 10);
                break;
            case "SpinningScythe":
                ComboLast = "SpinningScythe";
                ComboRemaining = 30;
                if (_scenario.Level >= 50)
                    RedGauge = Math.Min(100, RedGauge + 10);
                break;
            case "NightmareScythe":
                ComboLast = "";
                ComboRemaining = 0;
                if (_scenario.Level >= 50 && comboBeforeGcd == "SpinningScythe")
                    RedGauge = Math.Min(100, RedGauge + 10);
                break;
            case "Harpe":
                EnhancedHarpeLeft = 0;
                if (_scenario.Level >= 50)
                    RedGauge = Math.Min(100, RedGauge + 10);
                break;
            case "HarvestMoon":
                Soulsow = false;
                if (_scenario.Level >= 50)
                    RedGauge = Math.Min(100, RedGauge + 10);
                break;
            case "Soulsow":
                Soulsow = true;
                break;
        }

        if (reaverBeforeGcd != ReaverState.None
            && frame.SelectedGcd != null
            && frame.SelectedGcd is not ("Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine"))
            ClearReaver();

        if (frame.SelectedGcd != null && frame.SelectedGcd != "Soulsow" && frame.TargetAvailable)
            GrantSelfSacrifice();

        for (var ogcdIndex = 0; ogcdIndex < frame.SelectedOgcds.Count; ++ogcdIndex)
        {
            var ogcd = frame.SelectedOgcds[ogcdIndex];
            var executionOffset = frame.FirstWeaveOffset + OgcdWeaveOffset * ogcdIndex;
            AdvanceTo(frame.Time + executionOffset);
            switch (ogcd)
            {
                case "Potion":
                    PotionUsed = true;
                    ++PotionUseCount;
                    PotionReadyIn = PotionRecast;
                    break;
                case "ArcaneCircle":
                    ++ArcaneCircleUseCount;
                    DeferredPreArcaneEnshroud = false;
                    ArcaneCircleLeft = 20;
                    TimeSinceArcaneCircleEnded = -1;
                    ArcaneCircleReadyIn = 120;
                    if (_scenario.Level >= 88)
                    {
                        _sacrificeWindowStartedAt = _lastTime;
                        _sacrificeContributors.Clear();
                        CircleOfSacrificeLeft = 5;
                        BloodsownLeft = RprRotationEmulator.BloodsownCircleDuration;
                    }
                    break;
                case "Gluttony":
                    PostPerfectioPriorityComplete = true;
                    RedGauge = Math.Max(0, RedGauge - 50);
                    Reaver = _scenario.Level >= 96 ? ReaverState.Executioner : ReaverState.SoulReaver;
                    ReaverStacks = 2;
                    ReaverLeft = ThirtySecondStatusDuration;
                    GluttonyReadyIn = 60;
                    _gluttonyReadySince = double.NaN;
                    break;
                case "BloodStalk":
                case "UnveiledGibbet":
                case "UnveiledGallows":
                case "GrimSwathe":
                    RedGauge = Math.Max(0, RedGauge - 50);
                    if (_scenario.Level >= 70)
                    {
                        Reaver = ReaverState.SoulReaver;
                        ReaverStacks = 1;
                        ReaverLeft = ThirtySecondStatusDuration;
                    }
                    break;
                case "Enshroud":
                    if (_scenario.SkillRotation == SkillRotationMode.DancingMad)
                    {
                        if (RprRotationEmulator.DancingMadBurstEnshroudWindowOpen(frame.Time))
                            ++DancingMadBurstEnshrouds;
                        else
                            ++DancingMadNormalEnshrouds;
                    }
                    if (ArcaneCircleLeft <= 0 && ArcaneCircleReadyIn > 0 && ArcaneCircleReadyIn <= RprRotationEmulator.PreArcaneEnshroudEntryWindow)
                        DeferredPreArcaneEnshroud = true;
                    if (IdealHost)
                    {
                        IdealHost = false;
                        IdealHostLeft = 0;
                    }
                    else
                    {
                        BlueGauge = Math.Max(0, BlueGauge - 50);
                    }
                    PerfectioParata = false;
                    PerfectioParataLeft = 0;
                    BlueSouls = 5;
                    PurpleSouls = 0;
                    EnshroudLeft = EnshroudDuration;
                    EnshroudReadyIn = _scenario.Level >= 92 ? 5 : 15;
                    Oblatio = _scenario.Level >= 92;
                    OblatioLeft = Oblatio ? ThirtySecondStatusDuration : 0;
                    break;
                case "LemuresSlice":
                case "LemuresScythe":
                    PurpleSouls = Math.Max(0, PurpleSouls - 2);
                    break;
                case "Sacrificium":
                    Oblatio = false;
                    OblatioLeft = 0;
                    break;
                case "TrueNorth":
                    TrueNorthCharges = Math.Max(0, TrueNorthCharges - 1);
                    TrueNorthLeft = TrueNorthDuration;
                    break;
            }
            if (frame.TargetAvailable && ogcd is "Gluttony" or "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe" or "LemuresSlice" or "LemuresScythe" or "Sacrificium")
                GrantSelfSacrifice();
        }

        AdvanceDancingMadOpener(frame);
    }

    private void GrantSelfSacrifice()
    {
        if (CircleOfSacrificeLeft <= 0 || BloodsownLeft <= 0)
            return;
        if (!_sacrificeContributors.Add(0))
            return;
        CircleOfSacrificeLeft = 0;
        ImmortalSacrifice = Math.Min(8, ImmortalSacrifice + 1);
        ImmortalSacrificeLeft = ThirtySecondStatusDuration;
    }

    private void EndEnshroud()
    {
        BlueSouls = PurpleSouls = 0;
        EnshroudLeft = 0;
        Oblatio = false;
        OblatioLeft = 0;
        EnhancedVoidReapingLeft = EnhancedCrossReapingLeft = 0;
    }

    private void AdvanceDancingMadOpener(ActionFrame frame)
    {
        DancingMadOpenerStage = DancingMadOpenerStage switch
        {
            DancingMadOpenerStage.Harpe when frame.SelectedGcd == "Harpe" => DancingMadOpenerStage.DeathsDesign,
            DancingMadOpenerStage.DeathsDesign when frame.SelectedGcd == "ShadowOfDeath" => DancingMadOpenerStage.SoulSlice,
            DancingMadOpenerStage.SoulSlice when frame.SelectedGcd == "SoulSlice" => DancingMadOpenerStage.ExecutionersGallows,
            DancingMadOpenerStage.ExecutionersGallows when frame.SelectedGcd == "ExecutionersGallows" => DancingMadOpenerStage.ExecutionersGibbet,
            DancingMadOpenerStage.ExecutionersGibbet when frame.SelectedGcd == "ExecutionersGibbet" => DancingMadOpenerStage.PlentifulHarvest,
            DancingMadOpenerStage.PlentifulHarvest when frame.SelectedGcd == "PlentifulHarvest" => DancingMadOpenerStage.VoidReaping1,
            DancingMadOpenerStage.VoidReaping1 when frame.SelectedGcd == "VoidReaping" => DancingMadOpenerStage.CrossReaping1,
            DancingMadOpenerStage.CrossReaping1 when frame.SelectedGcd == "CrossReaping" => DancingMadOpenerStage.VoidReaping2,
            DancingMadOpenerStage.VoidReaping2 when frame.SelectedGcd == "VoidReaping" => DancingMadOpenerStage.CrossReaping2,
            DancingMadOpenerStage.CrossReaping2 when frame.SelectedGcd == "CrossReaping" => DancingMadOpenerStage.Communio,
            DancingMadOpenerStage.Communio when frame.SelectedGcd == "Communio" => DancingMadOpenerStage.Perfectio,
            DancingMadOpenerStage.Perfectio when frame.SelectedGcd == "Perfectio" => DancingMadOpenerStage.FollowupSoulSlice,
            DancingMadOpenerStage.FollowupSoulSlice when frame.SelectedGcd == "SoulSlice" => DancingMadOpenerStage.FollowupGallows,
            DancingMadOpenerStage.FollowupGallows when frame.SelectedGcd == "Gallows" => DancingMadOpenerStage.FollowupDeathsDesign,
            DancingMadOpenerStage.FollowupDeathsDesign when frame.SelectedGcd == "ShadowOfDeath" => DancingMadOpenerStage.Complete,
            _ => DancingMadOpenerStage
        };
    }

    private void ConsumeReaverStack()
    {
        ReaverStacks = Math.Max(0, ReaverStacks - 1);
        if (ReaverStacks == 0)
            ClearReaver();
    }

    private void ClearReaver()
    {
        Reaver = ReaverState.None;
        ReaverStacks = 0;
        ReaverLeft = 0;
    }
}

public sealed record WeaveInfo(
    string Slots,
    int Count,
    string Order,
    bool ClipRisk,
    string ClipReason,
    double FirstOffset,
    double Elapsed);

public sealed record RprContext(
    bool TargetAvailable,
    bool HaveTarget,
    bool FallbackTargetAvailable,
    bool MeleeAvailable,
    bool BestConeTargetAvailable,
    bool BestLineTargetAvailable,
    bool BestRangedAoeTargetAvailable,
    int AoeTargets,
    int ConeTargets,
    bool PositionalCorrect,
    RotationMode RotationMode,
    bool PerfectioParata,
    double PerfectioParataLeft,
    bool EventEnshroud,
    bool EventReaver,
    int EventReaverStacks,
    bool EventExecutioner,
    int EventExecutionerStacks,
    int TargetKillCount,
    bool EnhancedHarpe,
    double EnhancedHarpeLeft);
