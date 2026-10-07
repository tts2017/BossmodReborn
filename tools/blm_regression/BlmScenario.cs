using System.Text.Json.Serialization;
using ProductionBLM = BossMod.Autorotation.xan.Custom.BLM;

namespace BlmRegression;

public static class BlmRuleset
{
    public static void ValidateProductionParity()
    {
        var production = Enum.GetNames<ProductionBLM.RotationStrategy>();
        var emulated = Enum.GetNames<BlmRotationStrategy>();
        if (!production.SequenceEqual(emulated, StringComparer.Ordinal))
            throw new InvalidOperationException($"BLM rotation modes drifted from production: production=[{string.Join(',', production)}], emulator=[{string.Join(',', emulated)}]");
        BlmRotationEmulator.ValidateRuleset();
    }
}

public enum BlmRotationStrategy
{
    Automatic = (int)ProductionBLM.RotationStrategy.Automatic,
    FuturePlanner = (int)ProductionBLM.RotationStrategy.FuturePlanner,
    WindurstThirdWalk = (int)ProductionBLM.RotationStrategy.WindurstThirdWalk,
    PolyglotOvercapOnly = (int)ProductionBLM.RotationStrategy.PolyglotOvercapOnly
}

public enum BlmEncounterHintStrategy
{
    Automatic,
    Boss,
    Trash,
    AllianceTrash,
    MajorAdd,
    BossReturn,
    HoldBurst,
    ForceBurst,
    Off
}

public enum BlmExternalHintStrategy
{
    Off,
    MechanicOnly,
    Full
}

public enum BlmThunderStrategy
{
    Automatic,
    Delay,
    Force,
    InstantOnly,
    ForbidInstant
}

public enum BlmLeylinesStrategy
{
    OpenerOnly,
    Delay,
    Force,
    EvenBurst,
    FuturePlanner
}

public enum BlmTriplecastStrategy
{
    Automatic,
    Delay,
    Force
}

public enum BlmOffensiveStrategy
{
    Delay,
    Automatic,
    Force
}

public enum BlmAction
{
    None,
    Fire1,
    Fire2,
    Fire3,
    Fire4,
    Despair,
    Flare,
    FlareStar,
    Blizzard1,
    Blizzard2,
    Blizzard3,
    Blizzard4,
    Freeze,
    Paradox,
    Transpose,
    UmbralSoul,
    Thunder1,
    Thunder2,
    Thunder3,
    Thunder4,
    HighThunder,
    HighThunder2,
    Xenoglossy,
    Foul,
    Manafont,
    Amplifier,
    LeyLines,
    BetweenTheLines,
    Retrace,
    Swiftcast,
    Triplecast,
    LucidDreaming,
    Zeninage,
    Iainuki,
    OccultQuick,
    OccultComet,
    Scathe
}

public enum BlmHardFailRule
{
    GcdStop,
    RepeatedNonProgress,
    NegativeMP,
    ElementOutOfRange,
    HeartsOutOfRange,
    PolyglotOvercap,
    AstralSoulOutOfRange,
    IllegalAction,
    CooldownInvalid,
    NoTargetGcd,
    CastDuringCast,
    Fire4OutsideAF3,
    Blizzard4OutsideUI,
    DespairInvalid,
    FlareStarWithoutSoul,
    PolyglotInvalid,
    ThunderWithoutThunderhead,
    ParadoxInvalid,
    TransposeCooldown,
    ManafontInvalid,
    LeyLinesUnsafe,
    LeyLinesDuringMovement,
    BetweenTheLinesAutoPush,
    RetraceMissing,
    ElementDropped,
    PolyglotCappedTooLong,
    FlareStarDelayed,
    AoeStuckOnSingle,
    SingleStuckOnAoe
}

public enum BlmScenarioCategory
{
    FullUptime,
    OpenerBurstResource,
    TargetLostDowntime,
    Movement,
    LeyLines,
    Aoe,
    Thunder,
    ModeExternal,
    Coverage,
    CombatMatrix,
    HighEndPreflight
}

public enum BlmScenarioEventType
{
    TargetLost,
    Downtime,
    ForcedMove,
    MovementEscapeHatch,
    LeyLinesUnsafe,
    LookAway,
    TargetCount,
    ModeSwitch,
    TargetDying
}

public sealed record BlmScenarioEvent
{
    public required BlmScenarioEventType Type { get; init; }
    public double Start { get; init; }
    public double End { get; init; }
    public int Targets { get; init; }
    public BlmRotationStrategy? Rotation { get; init; }
}

public sealed record BlmScenario
{
    public required string Name { get; init; }
    public required BlmScenarioCategory Category { get; init; }
    public int Level { get; init; } = 100;
    public double Duration { get; init; } = 360;
    public int InitialTargets { get; init; } = 1;
    public int InitialElement { get; init; }
    public int InitialMP { get; init; } = BlmConstants.MaxMP;
    public int InitialHearts { get; init; } = 3;
    public int InitialPolyglot { get; init; }
    public int InitialAstralSoul { get; init; }
    public double InitialThunderLeft { get; init; }
    public double InitialAoeThunderLeft { get; init; }
    public double InitialNextPolyglot { get; init; } = BlmConstants.PolyglotInterval;
    public bool InitialParadox { get; init; }
    public bool InitialThunderhead { get; init; } = true;
    public bool InitialFirestarter { get; init; }
    public BlmRotationStrategy Rotation { get; init; } = BlmRotationStrategy.Automatic;
    public BlmEncounterHintStrategy EncounterHint { get; init; } = BlmEncounterHintStrategy.Boss;
    public BlmExternalHintStrategy ExternalHints { get; init; } = BlmExternalHintStrategy.Full;
    public BlmThunderStrategy Thunder { get; init; } = BlmThunderStrategy.Automatic;
    public BlmLeylinesStrategy Leylines { get; init; } = BlmLeylinesStrategy.EvenBurst;
    public BlmTriplecastStrategy Triplecast { get; init; } = BlmTriplecastStrategy.Automatic;
    public BlmOffensiveStrategy Manafont { get; init; } = BlmOffensiveStrategy.Automatic;
    public bool RequiresPhantomCoverage { get; init; }
    public bool PhantomActionsEnabled { get; init; }
    public uint ContentFinderConditionID { get; init; }
    public uint EncounterNameID { get; init; }
    public IReadOnlyList<BlmScenarioEvent> Events { get; init; } = [];
}

public sealed record BlmActionFrame
{
    public required string ScenarioName { get; init; }
    public required double Time { get; init; }
    public BlmAction SelectedGcd { get; init; }
    public IReadOnlyList<BlmAction> SelectedOgcds { get; init; } = [];
    public string Reason { get; init; } = "";
    public bool TargetAvailable { get; init; }
    public bool DowntimeNow { get; init; }
    public bool ForcedMoveNow { get; init; }
    public bool MovementEscapeHatchHeld { get; init; }
    public bool LookAwayNow { get; init; }
    public int Level { get; init; }
    public int Targets { get; init; }
    public BlmRotationStrategy Rotation { get; init; }
    public int Element { get; init; }
    public double ElementTimerLeft { get; init; }
    public int MP { get; init; }
    public int Hearts { get; init; }
    public int Polyglot { get; init; }
    public double NextPolyglot { get; init; }
    public int AstralSoul { get; init; }
    public bool Paradox { get; init; }
    public bool Thunderhead { get; init; }
    public bool Firestarter { get; init; }
    public double ThunderLeft { get; init; }
    public double AoeThunderLeft { get; init; }
    public double ManafontReadyIn { get; init; }
    public double AmplifierReadyIn { get; init; }
    public double LeyLinesLeft { get; init; }
    public int LeyLinesCharges { get; init; }
    public double SwiftcastReadyIn { get; init; }
    public int TriplecastCharges { get; init; }
    public IReadOnlyList<BlmHardFailRule> HardFails { get; init; } = [];
}

public sealed record BlmScenarioMetrics
{
    public int GcdCount { get; init; }
    public int OgcdCount { get; init; }
    public double GcdUptime { get; init; }
    public double RawThunderBlankSeconds { get; init; }
    public double ThunderBlankSeconds { get; init; }
    public double RawPolyglotMaxHoldSeconds { get; init; }
    public double PolyglotOvercapSeconds { get; init; }
    public int PolyglotWastedGrantCount { get; init; }
    public int ElementDropCount { get; init; }
    public int ForcedMovementHardcastAttempts { get; init; }
    public double LeyLinesUptimeSeconds { get; init; }
    public int ManafontCount { get; init; }
    public int AmplifierCount { get; init; }
    public int Standard57OrderViolations { get; init; }
    public int ModeHandoffGcdStops { get; init; }
    public int PhantomActionsUsed { get; init; }
    public int PhantomMovementFallbacks { get; init; }
    public int PhantomTargetLostSuppressions { get; init; }
    public int PhantomReturnGcds { get; init; }
    public int PhantomNullGuardChecks { get; init; }
    public int CoverageGapCount { get; init; }
    public bool HasRawThunderSignal => RawThunderBlankSeconds > BlmConstants.Gcd * 2;
    public bool HasRawPolyglotSignal => RawPolyglotMaxHoldSeconds > BlmConstants.Gcd * 4;
    public int RawSoftSignalCount => (HasRawThunderSignal ? 1 : 0)
        + (HasRawPolyglotSignal ? 1 : 0)
        + ElementDropCount
        + ForcedMovementHardcastAttempts
        + Standard57OrderViolations
        + ModeHandoffGcdStops;
    public int RawSoftRegressionCount => (ThunderBlankSeconds > 0 ? 1 : 0)
        + PolyglotWastedGrantCount
        + ElementDropCount
        + ForcedMovementHardcastAttempts
        + Standard57OrderViolations
        + ModeHandoffGcdStops;
    public int SoftRegressionCount => (ThunderBlankSeconds > 0 ? 1 : 0)
        + PolyglotWastedGrantCount
        + ElementDropCount
        + ForcedMovementHardcastAttempts
        + Standard57OrderViolations
        + ModeHandoffGcdStops;
}

public sealed record BlmScenarioResult
{
    public required BlmScenario Scenario { get; init; }
    public List<BlmActionFrame> Frames { get; init; } = [];
    public List<BlmHardFailRule> HardFails { get; init; } = [];
    public BlmScenarioMetrics Metrics { get; init; } = new();
    public bool Passed => HardFails.Count == 0;
}

public sealed record BlmSummaryResult
{
    public string ToolVersion { get; init; } = BlmConstants.RulesetVersion;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public int ScenarioCount { get; init; }
    public int PassCount { get; init; }
    public int FailCount { get; init; }
    public int HardFailCount { get; init; }
    public int RawSoftSignalCount { get; init; }
    public int RawSoftRegressionCount { get; init; }
    public int SoftRegressionCount { get; init; }
    public int CoverageGapCount { get; init; }
    public Dictionary<BlmHardFailRule, int> HardFailCountByRule { get; init; } = [];
    public List<string> FirstHardFails { get; init; } = [];
}

public sealed record BlmRegressionOutput
{
    public BlmSummaryResult Summary { get; init; } = new();
    public List<BlmScenarioResult> Scenarios { get; init; } = [];
    public List<BlmFailureReproduction> Reproductions { get; init; } = [];
}

public sealed record BlmFailureReproduction
{
    public required string SourceScenarioName { get; init; }
    public required BlmHardFailRule Rule { get; init; }
    public required BlmScenario Scenario { get; init; }
    public required string FirstFailureTrace { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, Converters = [
    typeof(JsonStringEnumConverter<BlmRotationStrategy>),
    typeof(JsonStringEnumConverter<BlmEncounterHintStrategy>),
    typeof(JsonStringEnumConverter<BlmExternalHintStrategy>),
    typeof(JsonStringEnumConverter<BlmThunderStrategy>),
    typeof(JsonStringEnumConverter<BlmLeylinesStrategy>),
    typeof(JsonStringEnumConverter<BlmTriplecastStrategy>),
    typeof(JsonStringEnumConverter<BlmOffensiveStrategy>),
    typeof(JsonStringEnumConverter<BlmAction>),
    typeof(JsonStringEnumConverter<BlmHardFailRule>),
    typeof(JsonStringEnumConverter<BlmScenarioCategory>),
    typeof(JsonStringEnumConverter<BlmScenarioEventType>)
])]
[JsonSerializable(typeof(BlmRegressionOutput))]
public partial class BlmRegressionJsonContext : JsonSerializerContext;

public static class BlmConstants
{
    public const string RulesetVersion = "blm-regression-ffxiv-7.5-pve-v3";
    public const int MaxMP = 10000;
    public const int MaxHearts = 3;
    public const int MaxPolyglot = 3;
    public const int MaxAstralSoul = 6;
    public const double Gcd = 2.5;
    public const double LeyLinesSpeedMultiplier = 0.85;
    public const double ManafontRecast = 100;
    public const double ManafontPreEnhancedRecast = 120;
    public const double AmplifierRecast = 120;
    public const double LeyLinesChargeRecast = 120;
    public const double SwiftcastRecast = 60;
    public const double EnhancedSwiftcastRecast = 40;
    public const double TriplecastChargeRecast = 60;
    public const double OgcdAnimationLock = 0.6;
    public const double SwiftcastDuration = 10;
    public const double TriplecastDuration = 15;
    public const double LeyLinesDuration = 20;
    public const double ElementTimer = float.MaxValue;
    public const double PolyglotInterval = 30;
    public const double ThunderRefreshWindow = 6;
    public const double LeyLinesUnsafeHold = 6;
    public const double DowntimeLeyLinesHold = 12;
}
