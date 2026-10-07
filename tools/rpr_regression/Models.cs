using System.Text.Json.Serialization;

namespace RprRegression;

public enum OpenerBurstMode
{
    TwoGcd,
    TwoPointFiveSecond,
    ZeroSecond
}

public enum PotionMode
{
    OpenerAndEvenBurst,
    EvenBurstExceptOpener,
    Off
}

public enum RotationMode
{
    Full,
    Basic
}

public enum SkillRotationMode
{
    Normal,
    DancingMad,
    WindurstThirdWalk
}

public enum WindurstEncounter
{
    None,
    Shantotto,
    Alexander,
    Promathia,
    HollowKing
}

public enum TrueNorthMode
{
    Auto,
    Off
}

public enum ReaverState
{
    None,
    SoulReaver,
    Executioner
}

public enum ScenarioCategory
{
    GcdOpener,
    RotationMode,
    DeathsDesign,
    ArcanePotion,
    Gluttony,
    Gauge,
    Enshroud,
    ReaverExecutioner,
    AoeTargeting,
    Downtime,
    RangedUptime,
    TrueNorth,
    LevelSync,
    WeaveValidation,
    RealHarness
}

public enum ScenarioEventType
{
    TargetLost,
    TargetReturned,
    TargetKilled,
    TargetDying,
    MeleeUnavailable,
    ModeSwitch,
    EnshroudStarted,
    PerfectioParata,
    Reaver,
    Executioner,
    AoeTargets,
    NullBestConeTarget,
    NullBestLineTarget,
    NullBestRangedAoeTarget,
    PrimaryTargetNull,
    EmptyPriorityTargets,
    EnhancedHarpe,
    PartyHit // Value identifies a party member (1-7); each can contribute once during Circle of Sacrifice.
}

public enum HardFailRule
{
    GcdStop,
    IllegalAction,
    BurstFailure,
    DeathsDesignFailure,
    GaugeFailure,
    DriftFailure,
    AoeFailure,
    ModeSwitchFailure,
    WeaveOrderFailure,
    PotionFailure,
    OpenerFailure,
    ReaverSequenceFailure
}

public sealed record ScenarioEvent
{
    public required ScenarioEventType Type { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public double Value { get; init; }
    public RotationMode? Mode { get; init; }
}

public sealed record ScenarioDefinition
{
    public required string Name { get; init; }
    public required ScenarioCategory Category { get; init; }
    public int Seed { get; init; }
    public double KillTime { get; init; } = 480;
    public double Gcd { get; init; } = 2.50;
    public OpenerBurstMode Opener { get; init; } = OpenerBurstMode.TwoGcd;
    public PotionMode Potion { get; init; } = PotionMode.OpenerAndEvenBurst;
    public RotationMode InitialMode { get; init; } = RotationMode.Full;
    public SkillRotationMode SkillRotation { get; init; } = SkillRotationMode.Normal;
    public WindurstEncounter WindurstEncounter { get; init; }
    public TrueNorthMode TrueNorth { get; init; } = TrueNorthMode.Auto;
    public bool PositionalCorrect { get; init; } = true;
    public int Level { get; init; } = 100;
    public double DeathsDesignLeft { get; init; } = 30;
    public int RedGauge { get; init; } = 50;
    public int BlueGauge { get; init; } = 50;
    public double SoulSliceCharges { get; init; } = 1;
    public double InitialArcaneCircleReadyIn { get; init; }
    public double InitialArcaneCircleLeft { get; init; }
    public double InitialPerfectioParataLeft { get; init; }
    public int InitialImmortalSacrifice { get; init; }
    public double InitialBloodsownLeft { get; init; }
    public bool InitialIdealHost { get; init; }
    public string InitialComboLast { get; init; } = "";
    public double InitialComboRemaining { get; init; } = 30;
    public bool InitialSoulSliceUsed { get; init; }
    public bool InitialSoulsow { get; init; } = true;
    public int AoeTargets { get; init; } = 1;
    public int ConeTargets { get; init; } = 1;
    public ReaverState InitialReaver { get; init; } = ReaverState.None;
    public int InitialReaverStacks { get; init; }
    public double InitialEnhancedGibbetLeft { get; init; }
    public double InitialEnhancedGallowsLeft { get; init; }
    public bool EvenBurstOneRefreshEnough { get; init; }
    public bool EvenBurstTwoRefreshNeeded { get; init; }
    public bool ArcaneCircleFourSecondDisplay { get; init; }
    public bool GluttonyReadyAtStart { get; init; }
    public double InitialGluttonyReadyIn { get; init; }
    public bool ForceBlueGaugeBuild { get; init; }
    public bool FinalTwoGcdKill { get; init; }
    public bool FinalEncounter { get; init; }
    public bool ExpectDoubleEnshroud { get; init; }
    public bool ExpectTwoCommunio { get; init; }
    public bool ExpectPerfectio { get; init; }
    public bool ExpectLemure { get; init; }
    public bool ExpectSacrificium { get; init; }
    public bool ExpectSecondEnshroudBlueGauge { get; init; }
    public IReadOnlyList<ScenarioEvent> Events { get; init; } = [];
}

public sealed record ActionFrame
{
    public required string ScenarioName { get; init; }
    public required double Time { get; init; }
    public required double Gcd { get; init; }
    public double Elapsed { get; init; }
    public string? SelectedGcd { get; init; }
    public IReadOnlyList<string> GcdCandidates { get; init; } = [];
    public IReadOnlyList<string> SelectedOgcds { get; init; } = [];
    public bool TargetAvailable { get; init; }
    public bool HaveTarget { get; init; }
    public bool MeleeAvailable { get; init; }
    public bool FallbackTargetAvailable { get; init; }
    public int Level { get; init; }
    public int AoeTargets { get; init; }
    public int ConeTargets { get; init; }
    public bool BestConeTargetAvailable { get; init; }
    public bool BestLineTargetAvailable { get; init; }
    public bool BestRangedAoeTargetAvailable { get; init; }
    public RotationMode RotationMode { get; init; }
    public double DeathsDesignLeft { get; init; }
    public double ArcaneCircleLeft { get; init; }
    public double ArcaneCircleReadyIn { get; init; }
    public int RedGauge { get; init; }
    public int BlueGauge { get; init; }
    public double SoulSliceCharges { get; init; }
    public string ComboLast { get; init; } = "";
    public int BlueSouls { get; init; }
    public int PurpleSouls { get; init; }
    public bool Oblatio { get; init; }
    public ReaverState ReaverState { get; init; }
    public double EnhancedGibbetLeft { get; init; }
    public double EnhancedGallowsLeft { get; init; }
    public double EnhancedVoidReapingLeft { get; init; }
    public double EnhancedCrossReapingLeft { get; init; }
    public bool PositionalCorrect { get; init; }
    public double TrueNorthLeft { get; init; }
    public double EnhancedHarpeLeft { get; init; }
    public bool PerfectioParata { get; init; }
    public double PerfectioParataLeft { get; init; }
    public bool IdealHost { get; init; }
    public int ImmortalSacrifice { get; init; }
    public double BloodsownLeft { get; init; }
    public bool PerfectioOcculta { get; init; }
    public bool PlentifulHarvestReady { get; init; }
    public bool PostPerfectioPriorityActive { get; init; }
    public double TimeSincePerfectioUsed { get; init; }
    public string EnshroudReadyReason { get; init; } = "";
    public bool DeathsDesignOneRefresh { get; init; }
    public bool DeathsDesignTwoRefresh { get; init; }
    public bool DeathsDesignPreArcaneRefresh { get; init; }
    public bool DeathsDesignPreAnyEnshroudRefresh { get; init; }
    public bool EnshroudBlockedForDeathsDesign { get; init; }
    public string DeathsDesignRefreshReason { get; init; } = "";
    public int GcdWindowIndex { get; init; }
    public string WeaveSlot { get; init; } = "";
    public int WeaveCountInGcd { get; init; }
    public string OgcdOrderInGcd { get; init; } = "";
    public bool ClipRisk { get; init; }
    public string ClipReason { get; init; } = "";
    public bool PotionUsed { get; init; }
    public double PotionReadyIn { get; init; }
    public int PotionUseCount { get; init; }
    public double FirstWeaveOffset { get; init; }
    public bool ExpectedActionPossible { get; init; }
    public string Reason { get; init; } = "";
    public IReadOnlyList<HardFailRule> HardFails { get; init; } = [];
}

public sealed record ScenarioMetrics
{
    public double TotalPotencyEstimate { get; init; }
    public double GcdUptime { get; init; }
    public int GcdCount { get; init; }
    public int ArcaneCircleCount { get; init; }
    public int GluttonyCount { get; init; }
    public double GluttonyDriftSeconds { get; init; }
    public int EnshroudCount { get; init; }
    public int CommunioCount { get; init; }
    public int PerfectioCount { get; init; }
    public int LemureCount { get; init; }
    public int SacrificiumCount { get; init; }
    public double SoulSliceChargesLost { get; init; }
    public int RedGaugeOvercap { get; init; }
    public int BlueGaugeOvercap { get; init; }
    public double DeathsDesignUptime { get; init; }
    public int DeathsDesignRefreshCount { get; init; }
    public int WastedDeathsDesignRefreshCount { get; init; }
    public int RangedGcdCount { get; init; }
    public int InvalidTargetFallbackCount { get; init; }
    public int SkippedGcdCount { get; init; }
    public int ClippingRiskCount { get; init; }
    public int ComboBreakCount { get; init; }
}

public sealed record ScenarioResult
{
    public required ScenarioDefinition Scenario { get; init; }
    public List<ActionFrame> Frames { get; init; } = [];
    public List<HardFailRule> HardFails { get; init; } = [];
    public ScenarioMetrics Metrics { get; init; } = new();
    public double Score { get; init; }
    public bool Passed => HardFails.Count == 0;
}

public sealed record SummaryResult
{
    public string ToolVersion { get; init; } = "rpr-regression-v1";
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public int ScenarioCount { get; init; }
    public int PassCount { get; init; }
    public int FailCount { get; init; }
    public Dictionary<HardFailRule, int> HardFailCountByRule { get; init; } = [];
    public double BaselineScore { get; init; }
    public double CandidateScore { get; init; }
    public List<string> Regressions { get; init; } = [];
}

public sealed record RegressionOutput
{
    public SummaryResult Summary { get; init; } = new();
    public List<ScenarioResult> Scenarios { get; init; } = [];
}

public sealed record CompareResult(
    string Verdict,
    IReadOnlyList<string> RejectionReasons,
    IReadOnlyList<string> Regressions);

public sealed record RprTuningProfile
{
    public static RprTuningProfile Baseline { get; } = new()
    {
        Name = "baseline",
        Parameter = "baseline",
        BaselineValue = "",
        CandidateValue = ""
    };

    public required string Name { get; init; }
    public required string Parameter { get; init; }
    public required string BaselineValue { get; init; }
    public required string CandidateValue { get; init; }
    public string DecisionDomain { get; init; } = "";
    public double EvenBurstDDSecondRefreshSafety { get; init; } = 1.5;
    public double EvenBurstRemainingAfterArcaneCircle { get; init; } = 22.0;
    public double EvenBurstRemainingFromPreArcaneEnshroud { get; init; } = 20.0;
    public double ArcaneStartDeathsDesignCoverage { get; init; } = 20.0;
    public double PreAnyEnshroudGcds { get; init; } = 5.0;
    public double GluttonyHoldBeforeArcaneSeconds { get; init; } = 10.0;
    public double SoulSliceBurstSoonSeconds { get; init; } = 6.0;
    public double BlueGauge100StandaloneEnshroudArcaneThreshold { get; init; } = 0.0;
    public double EnshroudHoldBeforeArcaneSeconds { get; init; }
    public double EndGaugeSpendWindowSeconds { get; init; }
    public int PerfectioComboProtectGcds { get; init; }
    public int ComboPriorityScope { get; init; } = -1;
    public int LateBurstSoulSlicePolicy { get; init; }
    public int DancingMadMedianArcaneCircleAnchorMask { get; init; }
    public bool TimelineAwareBurstHold { get; init; } = true;
}

public sealed record OfflinePlannerCandidateResult
{
    public required string Name { get; init; }
    public required string DecisionDomain { get; init; }
    public required string Parameter { get; init; }
    public required string BaselineValue { get; init; }
    public required string CandidateValue { get; init; }
    public required string Verdict { get; init; }
    public int ScenarioCount { get; init; }
    public int HardFailCount { get; init; }
    public int RegressionCount { get; init; }
    public int ChangedDecisionCount { get; init; }
    public double TotalPotencyDelta { get; init; }
    public double DeathsDesignUptimeDelta { get; init; }
    public double GaugeLossDelta { get; init; }
    public int RedGaugeOvercapDelta { get; init; }
    public int BlueGaugeOvercapDelta { get; init; }
    public double SoulSliceChargeLossDelta { get; init; }
    public double GluttonyDriftDelta { get; init; }
    public int EnshroudCountDelta { get; init; }
    public int CommunioCountDelta { get; init; }
    public int PerfectioCountDelta { get; init; }
    public int SacrificiumCountDelta { get; init; }
    public int LemureCountDelta { get; init; }
    public int ClipRiskDelta { get; init; }
    public IReadOnlyList<string> DecisionExamples { get; init; } = [];
    public required string Reason { get; init; }
}

public sealed record OptimizerCandidateResult
{
    public required string Name { get; init; }
    public required string Parameter { get; init; }
    public required string BaselineValue { get; init; }
    public required string CandidateValue { get; init; }
    public required string Verdict { get; init; }
    public int ScenarioCount { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public int HardFailCount { get; init; }
    public int HardFailDelta { get; init; }
    public int RegressionCount { get; init; }
    public double ScoreDelta { get; init; }
    public double TotalPotencyDelta { get; init; }
    public double GcdUptimeDelta { get; init; }
    public double DeathsDesignUptimeDelta { get; init; }
    public int RedGaugeOvercapDelta { get; init; }
    public int BlueGaugeOvercapDelta { get; init; }
    public double SoulSliceChargeLossDelta { get; init; }
    public double GluttonyDriftDelta { get; init; }
    public int EnshroudCountDelta { get; init; }
    public int CommunioCountDelta { get; init; }
    public int PerfectioCountDelta { get; init; }
    public int SacrificiumCountDelta { get; init; }
    public int LemureCountDelta { get; init; }
    public int ClipRiskDelta { get; init; }
    public int ComboBreakDelta { get; init; }
    public required string AdoptabilityReason { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, Converters = [typeof(JsonStringEnumConverter<OpenerBurstMode>), typeof(JsonStringEnumConverter<PotionMode>), typeof(JsonStringEnumConverter<RotationMode>), typeof(JsonStringEnumConverter<SkillRotationMode>), typeof(JsonStringEnumConverter<WindurstEncounter>), typeof(JsonStringEnumConverter<TrueNorthMode>), typeof(JsonStringEnumConverter<ReaverState>), typeof(JsonStringEnumConverter<ScenarioCategory>), typeof(JsonStringEnumConverter<ScenarioEventType>), typeof(JsonStringEnumConverter<HardFailRule>)])]
[JsonSerializable(typeof(SummaryResult))]
[JsonSerializable(typeof(RegressionOutput))]
public partial class RprRegressionJsonContext : JsonSerializerContext;
