namespace MnkRegression;

public enum HardFailCode
{
    BuildUnavailable,
    RuntimeException,
    GcdStarvation,
    LookAwayEnemyGcd,
    LookAwayEnemyOgcd,
    EncounterHintAutomaticBurst,
    SixSidedStarOverPbOrBlitz,
    BeastGaugeBlocksPB,
    BlitzExpired,
    OddPbBreaksNextEven,
    EvenBurstSyncBroken,
    PotionStrategyViolation,
    RiddleOfEarthInvalidDamage,
    UnsafeThunderclap,
    BossReturnResourceSpend,
    DancingMadResyncBroken,
    TargetableEnemyAction,
    HaveTargetEnemyAction,
    MeleeGcdOutOfRange,
    PBDuringTargetLossContradiction,
    PhantomRushOutsideEvenBurst,
    TrashResourceSpend,
    UnlearnedAction
}

public enum CandidateMode
{
    Baseline,
    RiddleOfWindNoTwoMinuteHold,
    RiddleOfWindBurstOnly,
    PhantomRushNoBurstHold,
    PhantomRushStrictBurstHold,
    ChakraHoldForBrotherhood,
    ChakraOvercapOnlyOutsideBurst,
    SixSidedStarBeforeDowntime,
    SixSidedStarBeforeLongMeleeLoss
}

public sealed record PredictedDamageSnapshot(
    double Activation,
    PredictedDamageKind Type,
    bool AppliesToSelf);

public sealed record ActionFrame
{
    public required string ScenarioName { get; init; }
    public required double Time { get; init; }
    public required double CombatTimer { get; init; }
    public string? SelectedGCD { get; init; }
    public IReadOnlyList<string> SelectedOGCD { get; init; } = [];
    public bool TargetAvailable { get; init; }
    public bool HaveTarget { get; init; } = true;
    public bool MeleeAvailable { get; init; }
    public bool Forbidden { get; init; }
    public bool ThunderclapSafe { get; init; } = true;
    public int NumAOETargets { get; init; }
    public EncounterHintMode EncounterHint { get; init; }
    public BurstTimingMode BurstTiming { get; init; }
    public OpenerRoFOffsetMode OpenerRoFOffset { get; init; }
    public double PBLeft { get; init; }
    public double PBCharges { get; init; }
    public double BlitzLeft { get; init; }
    public string Nadi { get; init; } = "None";
    public double FireLeft { get; init; }
    public double BrotherhoodLeft { get; init; }
    public double PerfectBalanceLeft { get; init; }
    public double FormShiftLeft { get; init; }
    public double GCDReadyIn { get; init; }
    public int Chakra { get; init; }
    public string BeastChakra { get; init; } = "None";
    public bool PotionUsed { get; init; }
    public bool LookAway { get; init; }
    public IReadOnlyList<PredictedDamageSnapshot> PredictedDamage { get; init; } = [];
    public IReadOnlyList<HardFailCode> HardFails { get; init; } = [];
    public string RiddleOfEarthReason { get; init; } = "";
    public double ScoreContribution { get; init; }
}

public sealed record KeyTimings
{
    public List<double> RiddleOfFire { get; init; } = [];
    public List<double> Brotherhood { get; init; } = [];
    public List<double> PerfectBalance { get; init; } = [];
    public List<double> MasterfulBlitz { get; init; } = [];
    public List<double> PhantomRush { get; init; } = [];
    public List<double> Potion { get; init; } = [];
    public List<double> RiddleOfEarth { get; init; } = [];
}

public sealed record ScenarioResult
{
    public required string ScenarioName { get; init; }
    public required ScenarioCategory Category { get; init; }
    public required BurstTimingMode BurstTiming { get; init; }
    public required OpenerRoFOffsetMode OpenerRoFOffset { get; init; }
    public RotationMode RotationMode { get; init; } = RotationMode.Full;
    public PBStrategyMode PBStrategy { get; init; } = PBStrategyMode.Automatic;
    public BlitzStrategyMode BlitzStrategy { get; init; } = BlitzStrategyMode.Automatic;
    public NadiStrategyMode NadiStrategy { get; init; } = NadiStrategyMode.Automatic;
    public AutoForceDelayMode RoFStrategy { get; init; } = AutoForceDelayMode.Automatic;
    public AutoForceDelayMode BrotherhoodStrategy { get; init; } = AutoForceDelayMode.Automatic;
    public AutoForceDelayMode RoWStrategy { get; init; } = AutoForceDelayMode.Automatic;
    public AutoForceDelayMode RoEStrategy { get; init; } = AutoForceDelayMode.Automatic;
    public ThunderclapStrategyMode ThunderclapStrategy { get; init; } = ThunderclapStrategyMode.GapClose;
    public int LevelCap { get; init; } = 100;
    public double EffectiveGcd { get; init; } = 2.0;
    public List<ActionFrame> Frames { get; init; } = [];
    public List<MnkStateSnapshot> Snapshots { get; init; } = [];
    public List<HardFailCode> HardFails { get; init; } = [];
    public KeyTimings Timings { get; init; } = new();
    public Dictionary<string, double> Metrics { get; init; } = [];
    public double Score { get; set; }
}

public sealed record RegressionResult
{
    public string ToolVersion { get; init; } = "mnk-regression-v1";
    public CandidateMode Candidate { get; init; } = CandidateMode.Baseline;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public string MnkSourceHash { get; init; } = "";
    public List<ScenarioResult> Scenarios { get; init; } = [];
}

public sealed record CompareReport(
    string Verdict,
    IReadOnlyList<string> RejectionReasons,
    IReadOnlyList<string> HardFailScenarios,
    IReadOnlyList<string> WorsenedScenarios,
    IReadOnlyList<string> ImprovedScenarios);
