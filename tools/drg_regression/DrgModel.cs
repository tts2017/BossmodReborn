namespace DrgRegression;

public enum DrgActionKind
{
    None,
    GCD,
    OGCD
}

public enum DrgAction
{
    None,
    TrueThrust,
    RaidenThrust,
    VorpalThrust,
    LanceBarrage,
    Disembowel,
    SpiralBlow,
    FullThrust,
    HeavensThrust,
    ChaosThrust,
    ChaoticSpring,
    FangAndClaw,
    WheelingThrust,
    Drakesbane,
    DoomSpike,
    WingedGlide,
    DraconianFury,
    SonicThrust,
    CoerthanTorment,
    PiercingTalon,
    LanceCharge,
    BattleLitany,
    Geirskogul,
    HighJump,
    MirageDive,
    DragonfireDive,
    RiseOfTheDragon,
    Nastrond,
    Stardiver,
    Starcross,
    WyrmwindThrust,
    LifeSurge
}

public enum DrgScenarioKind
{
    SingleTarget,
    TwoTargetDots,
    Aoe,
    TargetSwitch,
    TargetLost,
    RangedStart,
    Burst
}

public sealed record DrgScenario(
    string Name,
    DrgScenarioKind Kind,
    int Level = 100,
    double Duration = 180,
    double GcdLength = 2.5,
    int InitialTargetCount = 1,
    int InitialFocus = 0,
    double InitialPowerSurge = 0,
    double InitialDot0 = 0,
    double InitialDot1 = 0,
    double InitialLotd = 0,
    DrgAction InitialCombo = DrgAction.None);

public sealed record DrgActionLog(
    double Time,
    DrgActionKind Kind,
    DrgAction Action,
    string Target,
    string Reason,
    DrgStateSnapshot State);

public sealed record DrgStateSnapshot
{
    public double Time { get; init; }
    public double GcdReadyIn { get; init; }
    public DrgAction Combo { get; init; }
    public int TargetCount { get; init; }
    public bool Targetable { get; init; }
    public bool Melee { get; init; }
    public double PowerSurge { get; init; }
    public double Dot0 { get; init; }
    public double Dot1 { get; init; }
    public double DraconianFire { get; init; }
    public int Focus { get; init; }
    public double Lotd { get; init; }
    public double LanceCharge { get; init; }
    public double BattleLitany { get; init; }
    public double LifeSurgeBuff { get; init; }
    public int LifeSurgeCharges { get; init; }
    public double NastrondReady { get; init; }
    public double DragonsFlight { get; init; }
    public double StarcrossReady { get; init; }
}

public sealed record DrgFinding(double Time, string Rule, string Expected, string Actual);

public sealed record DrgScenarioResult
{
    public required DrgScenario Scenario { get; init; }
    public List<DrgActionLog> Actions { get; } = [];
    public List<DrgFinding> HardFails { get; } = [];
    public List<DrgFinding> SoftFindings { get; } = [];
}

public sealed record DrgRegressionSummary
{
    public int Scenarios { get; init; }
    public int HardFail { get; init; }
    public int SoftFinding { get; init; }
}

public sealed record DrgRegressionOutput
{
    public DrgRegressionSummary Summary { get; init; } = new();
    public List<DrgScenarioResult> Scenarios { get; init; } = [];
}
