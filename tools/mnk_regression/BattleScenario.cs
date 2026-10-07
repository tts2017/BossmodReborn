namespace MnkRegression;

public sealed record StrategyProfile(
    double GcdSeconds,
    OpenerRoFOffsetMode OpenerRoFOffset,
    BurstTimingMode BurstTiming,
    EncounterHintMode EncounterHint,
    int ContentId,
    RotationMode RotationMode,
    PBStrategyMode PBStrategy,
    BlitzStrategyMode BlitzStrategy,
    NadiStrategyMode NadiStrategy,
    AutoForceDelayMode RoFStrategy,
    AutoForceDelayMode BrotherhoodStrategy,
    AutoForceDelayMode RoWStrategy,
    AutoForceDelayMode RoEStrategy,
    ThunderclapStrategyMode ThunderclapStrategy,
    int LevelCap);

public sealed record BattleScenario(
    string Name,
    ScenarioCategory Category,
    double DurationSeconds,
    double TickInterval,
    StrategyProfile StrategyProfile,
    BattleState InitialState,
    IReadOnlyList<BattleEvent> Events,
    IReadOnlyList<string> ExpectedTags,
    ScenarioDefinition Source);
