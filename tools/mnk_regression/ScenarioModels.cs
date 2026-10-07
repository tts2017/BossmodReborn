namespace MnkRegression;

public enum BurstTimingMode
{
    Cooldown,
    SynergyFixed,
    MeleeSafe
}

public enum OpenerRoFOffsetMode
{
    Standard78,
    Early58,
    ZeroSecondBurst,
    PartyBurstAligned
}

public enum EncounterHintMode
{
    Automatic,
    Boss,
    Trash,
    BossReturn,
    HoldBurst
}

public enum RotationMode
{
    Full,
    BasicAndChakraOvercap
}

public enum AutoForceDelayMode
{
    Automatic,
    Force,
    Delay
}

public enum PBStrategyMode
{
    Automatic,
    Force,
    Delay
}

public enum BlitzStrategyMode
{
    Automatic,
    RoF,
    Multi,
    MultiRoF,
    Force,
    Delay
}

public enum NadiStrategyMode
{
    Automatic,
    LunarSolar,
    Lunar,
    Solar,
    DoubleLunar
}

public enum ThunderclapStrategyMode
{
    GapClose,
    None
}

public enum ScenarioCategory
{
    FullUptime,
    BurstTiming,
    OddBurstMeleeLoss,
    EvenBurstMeleeLoss,
    TargetLostDuringPB,
    DancingMad,
    LookAway,
    MechanicHints,
    RiddleOfEarth,
    TargetCount,
    EndBurn,
    StrategyMatrix,
    CombatMatrix,
    LowLevel,
    BossReturnBoundary,
    ResourceCarryover
}

public enum ScenarioEventType
{
    TargetLost,
    MeleeUnavailable,
    ForbiddenZone,
    LookAway,
    PredictedDamage,
    PhaseEnd,
    FightEnd,
    BossReturn,
    HoldBurst,
    UnsafeThunderclap
}

public enum PredictedDamageKind
{
    None,
    Raidwide,
    Shared,
    Tankbuster,
    AutoAttack
}

public sealed record ScenarioEvent(
    ScenarioEventType Type,
    double Start,
    double End,
    PredictedDamageKind DamageType = PredictedDamageKind.None,
    bool AppliesToSelf = true,
    string Detail = "")
{
    public bool ActiveAt(double time) => time >= Start && time < End;
}

public sealed record ScenarioDefinition(
    string Name,
    ScenarioCategory Category,
    double DurationSeconds,
    double GcdSeconds,
    OpenerRoFOffsetMode OpenerRoFOffset,
    BurstTimingMode BurstTiming,
    EncounterHintMode EncounterHint = EncounterHintMode.Boss,
    int ContentId = 0,
    int TargetCount = 1,
    bool LongAoe = false,
    IReadOnlyList<ScenarioEvent>? Events = null,
    RotationMode RotationMode = RotationMode.Full,
    PBStrategyMode PBStrategy = PBStrategyMode.Automatic,
    BlitzStrategyMode BlitzStrategy = BlitzStrategyMode.Automatic,
    NadiStrategyMode NadiStrategy = NadiStrategyMode.Automatic,
    AutoForceDelayMode RoFStrategy = AutoForceDelayMode.Automatic,
    AutoForceDelayMode BrotherhoodStrategy = AutoForceDelayMode.Automatic,
    AutoForceDelayMode RoWStrategy = AutoForceDelayMode.Automatic,
    AutoForceDelayMode RoEStrategy = AutoForceDelayMode.Automatic,
    ThunderclapStrategyMode ThunderclapStrategy = ThunderclapStrategyMode.GapClose,
    int LevelCap = 100,
    GaugeSnapshot? InitialGauge = null,
    CooldownSnapshot? InitialCooldowns = null)
{
    public IReadOnlyList<ScenarioEvent> EventList => Events ?? [];
}
