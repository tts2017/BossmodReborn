namespace MnkRegression;

public sealed record GaugeSnapshot
{
    public int? Chakra { get; init; }
    public double? ChakraProgress { get; init; }
    public IReadOnlyList<string>? BeastChakra { get; init; }
    public bool? LunarNadi { get; init; }
    public bool? SolarNadi { get; init; }
    public string? CurrentForm { get; init; }
    public int? OpoFury { get; init; }
    public int? RaptorFury { get; init; }
    public int? CoeurlFury { get; init; }
    public string? PerfectBalancePlan { get; init; }
    public double? FormlessFistLeft { get; init; }
    public double? PerfectBalanceLeft { get; init; }
    public int? PerfectBalanceStacks { get; init; }
    public double? RiddleOfFireLeft { get; init; }
    public double? BrotherhoodLeft { get; init; }
    public double? RiddleOfWindLeft { get; init; }
    public double? PotionLeft { get; init; }
    public double? RiddleOfEarthLeft { get; init; }
    public double? EarthsReplyLeft { get; init; }
    public double? WindsReplyLeft { get; init; }
    public double? FiresReplyLeft { get; init; }
    public double? BlitzLeft { get; init; }
}

public sealed record CooldownSnapshot
{
    public double? PerfectBalanceCharges { get; init; }
    public double? PerfectBalanceReadyIn { get; init; }
    public double? RiddleOfFireReadyIn { get; init; }
    public double? BrotherhoodReadyIn { get; init; }
    public double? RiddleOfWindReadyIn { get; init; }
    public double? RiddleOfEarthReadyIn { get; init; }
    public double? PotionReadyIn { get; init; }
    public double? ThunderclapCharges { get; init; }
    public double? ThunderclapReadyIn { get; init; }
    public double? TrueNorthCharges { get; init; }
    public double? TrueNorthReadyIn { get; init; }
}

public sealed record BattleEvent(
    BattleEventKind Kind,
    double Start,
    double End,
    bool? Targetable = null,
    bool? HaveTarget = null,
    bool? CanMelee = null,
    int? TargetCount = null,
    int? NumAOETargets = null,
    int? NumMeleeAOETargets = null,
    EncounterHintMode? EncounterHint = null,
    bool? Active = null,
    bool? ThunderclapSafe = null,
    PredictedDamageKind DamageType = PredictedDamageKind.None,
    bool SelfTargeted = true,
    double? Estimate = null,
    GaugeSnapshot? Gauge = null,
    CooldownSnapshot? Cooldowns = null,
    string Detail = "")
{
    public bool ActiveAt(double time) => time >= Start && time < End;

    public static BattleEvent TargetableWindow(double start, double end, bool targetable, bool haveTarget)
        => new(BattleEventKind.TargetableWindow, start, end, Targetable: targetable, HaveTarget: haveTarget);

    public static BattleEvent MeleeWindow(double start, double end, bool canMelee)
        => new(BattleEventKind.MeleeWindow, start, end, CanMelee: canMelee);

    public static BattleEvent TargetCountWindow(double start, double end, int targets, int aoeTargets, int meleeAoeTargets)
        => new(BattleEventKind.TargetCountWindow, start, end, TargetCount: targets, NumAOETargets: aoeTargets, NumMeleeAOETargets: meleeAoeTargets);

    public static BattleEvent EncounterHintWindow(double start, double end, EncounterHintMode hint)
        => new(BattleEventKind.EncounterHintWindow, start, end, EncounterHint: hint);

    public static BattleEvent LookAwayWindow(double start, double end)
        => new(BattleEventKind.LookAwayWindow, start, end, Active: true);

    public static BattleEvent ForbiddenZoneWindow(double start, double end)
        => new(BattleEventKind.ForbiddenZoneWindow, start, end, Active: true);

    public static BattleEvent ThunderclapSafetyWindow(double start, double end, bool safe)
        => new(BattleEventKind.ThunderclapSafetyWindow, start, end, ThunderclapSafe: safe);

    public static BattleEvent PredictedDamage(double activation, PredictedDamageKind damageType, bool selfTargeted)
        => new(BattleEventKind.PredictedDamageEvent, activation, activation, DamageType: damageType, SelfTargeted: selfTargeted);

    public static BattleEvent PhaseEndEstimate(double at)
        => new(BattleEventKind.PhaseEndEstimateEvent, at, at, Estimate: at);

    public static BattleEvent FightEndEstimate(double at)
        => new(BattleEventKind.FightEndEstimateEvent, at, at, Estimate: at);

    public static BattleEvent DowntimeEstimate(double at)
        => new(BattleEventKind.DowntimeEstimateEvent, at, at, Estimate: at);

    public static BattleEvent GaugeSnapshotEvent(double at, GaugeSnapshot gauge)
        => new(BattleEventKind.GaugeSnapshotEvent, at, at, Gauge: gauge);

    public static BattleEvent CooldownSnapshotEvent(double at, CooldownSnapshot cooldowns)
        => new(BattleEventKind.CooldownSnapshotEvent, at, at, Cooldowns: cooldowns);
}
