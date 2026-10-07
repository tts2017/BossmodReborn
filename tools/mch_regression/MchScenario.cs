namespace MchRegression;

public sealed record MchScenario(
    string Name,
    double Duration,
    int Level,
    MchScenarioKind Kind,
    MchQueenStrategy QueenStrategy,
    MchWildfireStrategy WildfireStrategy,
    MchPotionStrategy PotionStrategy,
    MchToolStrategy ToolStrategy,
    MchOffensiveStrategy HyperchargeStrategy,
    MchOffensiveStrategy BuffsStrategy,
    int Seed,
    int InitialTargetCount,
    bool DancingMad,
    bool TargetOverride,
    bool NormalOvercapOnly)
{
    public static readonly MchScenario Default = new(
        "Default",
        180,
        100,
        MchScenarioKind.FullUptimeSingleTarget,
        MchQueenStrategy.MinGauge,
        MchWildfireStrategy.ASAP,
        MchPotionStrategy.Off,
        MchToolStrategy.Automatic,
        MchOffensiveStrategy.Automatic,
        MchOffensiveStrategy.Automatic,
        1,
        1,
        false,
        false,
        false);
}

public enum MchScenarioKind
{
    FullUptimeSingleTarget,
    FullUptimeFixedTargets,
    SwitchTargetsEvery30s,
    RandomTargetSwitch,
    Downtime,
    TargetLost,
    DancingMad,
    LowLevel,
    Potion,
    Strategy,
    Random,
    CombatMatrix
}

public static class MchScenarioCatalog
{
    public static IReadOnlyList<MchScenario> BuildAll(double? durationOverride, int? seedOverride)
    {
        var scenarios = new List<MchScenario>();
        AddNamedScenarios(scenarios, durationOverride, seedOverride);
        AddCombatMatrixScenarios(scenarios, durationOverride, seedOverride);
        return scenarios;
    }

    public static IReadOnlyList<MchScenario> BuildNamedOnly(double? durationOverride, int? seedOverride)
    {
        var scenarios = new List<MchScenario>();
        AddNamedScenarios(scenarios, durationOverride, seedOverride);
        return scenarios;
    }

    private static void AddNamedScenarios(List<MchScenario> scenarios, double? durationOverride, int? seedOverride)
    {
        Add(scenarios, "FullUptime_3m_ST", 180, 100, MchScenarioKind.FullUptimeSingleTarget, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "FullUptime_6m_ST", 360, 100, MchScenarioKind.FullUptimeSingleTarget, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "FullUptime_8m_ST", 480, 100, MchScenarioKind.FullUptimeSingleTarget, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "FullUptime_10m_ST", 600, 100, MchScenarioKind.FullUptimeSingleTarget, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "FullUptime_2Targets", 180, 100, MchScenarioKind.FullUptimeFixedTargets, targetCount: 2, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "FullUptime_3Targets", 180, 100, MchScenarioKind.FullUptimeFixedTargets, targetCount: 3, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "FullUptime_4Targets", 180, 100, MchScenarioKind.FullUptimeFixedTargets, targetCount: 4, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "FullUptime_6Targets", 180, 100, MchScenarioKind.FullUptimeFixedTargets, targetCount: 6, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "FullUptime_8Targets", 180, 100, MchScenarioKind.FullUptimeFixedTargets, targetCount: 8, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "ST_to_AOE_every_30s", 240, 100, MchScenarioKind.SwitchTargetsEvery30s, targetCount: 1, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "AOE_to_ST_every_30s", 240, 100, MchScenarioKind.SwitchTargetsEvery30s, targetCount: 6, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "ST_AOE_random_switch", 240, 100, MchScenarioKind.RandomTargetSwitch, seed: 101, seedOverride: seedOverride, durationOverride: durationOverride);
        AddDowntimeSet(scenarios, durationOverride, seedOverride);
        AddTargetLostSet(scenarios, durationOverride, seedOverride);
        AddDancingMadSet(scenarios, durationOverride, seedOverride);
        AddLowLevelSet(scenarios, durationOverride, seedOverride);
        AddPotionSet(scenarios, durationOverride, seedOverride);
        AddStrategySet(scenarios, durationOverride, seedOverride);
        for (var i = 1; i <= 5; ++i)
            Add(scenarios, $"Random_Seed_{i:000}", 300, 100, MchScenarioKind.Random, seed: i, seedOverride: seedOverride, durationOverride: durationOverride);
    }

    private static void AddDowntimeSet(List<MchScenario> scenarios, double? durationOverride, int? seedOverride)
    {
        Add(scenarios, "Downtime_30s_at_90s", 240, 100, MchScenarioKind.Downtime, seed: 90, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Downtime_15s_before_2min", 240, 100, MchScenarioKind.Downtime, seed: 105, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Downtime_10s_after_Wildfire", 180, 100, MchScenarioKind.Downtime, seed: 35, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Downtime_during_Queen", 240, 100, MchScenarioKind.Downtime, seed: 60, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Downtime_after_BarrelStabilizer", 240, 100, MchScenarioKind.Downtime, seed: 123, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Downtime_before_FullMetalField", 240, 100, MchScenarioKind.Downtime, seed: 132, seedOverride: seedOverride, durationOverride: durationOverride);
    }

    private static void AddTargetLostSet(List<MchScenario> scenarios, double? durationOverride, int? seedOverride)
    {
        Add(scenarios, "TargetLost_5s_random", 240, 100, MchScenarioKind.TargetLost, seed: 5, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "TargetLost_during_Hypercharge", 180, 100, MchScenarioKind.TargetLost, seed: 44, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "TargetLost_before_Wildfire", 180, 100, MchScenarioKind.TargetLost, seed: 111, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "TargetLost_after_ChainSaw", 180, 100, MchScenarioKind.TargetLost, seed: 125, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "TargetLost_before_Queen", 180, 100, MchScenarioKind.TargetLost, seed: 58, seedOverride: seedOverride, durationOverride: durationOverride);
    }

    private static void AddDancingMadSet(List<MchScenario> scenarios, double? durationOverride, int? seedOverride)
    {
        Add(scenarios, "DancingMad_P1_to_P2", 230, 100, MchScenarioKind.DancingMad, dancingMad: true, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "DancingMad_P2_downtime_resume", 360, 100, MchScenarioKind.DancingMad, dancingMad: true, seed: 205, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "DancingMad_2min_window", 180, 100, MchScenarioKind.DancingMad, dancingMad: true, seed: 120, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "DancingMad_4min_window", 300, 100, MchScenarioKind.DancingMad, dancingMad: true, seed: 240, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "DancingMad_6min_window", 420, 100, MchScenarioKind.DancingMad, dancingMad: true, seed: 360, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "DancingMad_final_phase", 720, 100, MchScenarioKind.DancingMad, dancingMad: true, seed: 430, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "DancingMad_target_lost_during_forced_window", 240, 100, MchScenarioKind.DancingMad, dancingMad: true, seed: 44, seedOverride: seedOverride, durationOverride: durationOverride);
    }

    private static void AddLowLevelSet(List<MchScenario> scenarios, double? durationOverride, int? seedOverride)
    {
        Add(scenarios, "Level30_ST", 120, 30, MchScenarioKind.LowLevel, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level40_Queen_Rook", 150, 40, MchScenarioKind.LowLevel, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level45_Wildfire", 150, 45, MchScenarioKind.LowLevel, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level52_AutoCrossbow", 150, 52, MchScenarioKind.LowLevel, targetCount: 6, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level58_Drill", 150, 58, MchScenarioKind.LowLevel, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level66_BarrelStabilizer", 180, 66, MchScenarioKind.LowLevel, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level72_Bioblaster", 180, 72, MchScenarioKind.LowLevel, targetCount: 3, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level80_AirAnchor", 180, 80, MchScenarioKind.LowLevel, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level90_ChainSaw", 180, 90, MchScenarioKind.LowLevel, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level94_Drill2Charge", 180, 94, MchScenarioKind.LowLevel, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level96_Excavator", 180, 96, MchScenarioKind.LowLevel, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Level100_FullMetalField", 180, 100, MchScenarioKind.LowLevel, seedOverride: seedOverride, durationOverride: durationOverride);
    }

    private static void AddPotionSet(List<MchScenario> scenarios, double? durationOverride, int? seedOverride)
    {
        Add(scenarios, "Potion_Disabled", 300, 100, MchScenarioKind.Potion, potion: MchPotionStrategy.Off, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Potion_OpenerAndEvenBurst", 300, 100, MchScenarioKind.Potion, potion: MchPotionStrategy.OpenerAndEvenBursts, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Potion_EvenBurstOnly", 300, 100, MchScenarioKind.Potion, potion: MchPotionStrategy.EvenBursts, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Potion_NotReadyAt2min_UseNextEven", 540, 100, MchScenarioKind.Potion, potion: MchPotionStrategy.EvenBursts, seed: 270, seedOverride: seedOverride, durationOverride: durationOverride);
    }

    private static void AddStrategySet(List<MchScenario> scenarios, double? durationOverride, int? seedOverride)
    {
        Add(scenarios, "Queen_MinGauge", 300, 100, MchScenarioKind.Strategy, queen: MchQueenStrategy.MinGauge, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Queen_FullGauge", 300, 100, MchScenarioKind.Strategy, queen: MchQueenStrategy.FullGauge, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Queen_RaidBuffsOnly", 300, 100, MchScenarioKind.Strategy, queen: MchQueenStrategy.RaidBuffsOnly, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Queen_Never", 300, 100, MchScenarioKind.Strategy, queen: MchQueenStrategy.Never, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Wildfire_ASAP", 300, 100, MchScenarioKind.Strategy, wildfire: MchWildfireStrategy.ASAP, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Wildfire_Hypercharge", 300, 100, MchScenarioKind.Strategy, wildfire: MchWildfireStrategy.Hypercharge, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Wildfire_Delay", 300, 100, MchScenarioKind.Strategy, wildfire: MchWildfireStrategy.Delay, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Tools_Delay", 300, 100, MchScenarioKind.Strategy, tools: MchToolStrategy.Delay, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Hypercharge_Delay", 300, 100, MchScenarioKind.Strategy, hypercharge: MchOffensiveStrategy.Delay, seedOverride: seedOverride, durationOverride: durationOverride);
        Add(scenarios, "Buffs_Delay", 300, 100, MchScenarioKind.Strategy, buffs: MchOffensiveStrategy.Delay, seedOverride: seedOverride, durationOverride: durationOverride);
    }

    private static void AddCombatMatrixScenarios(List<MchScenario> scenarios, double? durationOverride, int? seedOverride)
    {
        var levels = new[] { 30, 40, 45, 52, 58, 66, 72, 80, 90, 94, 96, 100 };
        var targetCounts = new[] { 1, 2, 3, 4, 6, 8 };
        var queens = Enum.GetValues<MchQueenStrategy>();
        var wildfires = Enum.GetValues<MchWildfireStrategy>();
        var tools = Enum.GetValues<MchToolStrategy>();
        var potions = Enum.GetValues<MchPotionStrategy>();
        var hypercharges = new[] { MchOffensiveStrategy.Automatic, MchOffensiveStrategy.Delay };
        var seeds = new[] { 11, 23 };
        foreach (var level in levels)
            foreach (var targets in targetCounts)
                foreach (var queen in queens)
                    foreach (var wildfire in wildfires)
                        foreach (var tool in tools)
                            foreach (var potion in potions)
                                foreach (var hypercharge in hypercharges)
                                    foreach (var seed in seeds)
                                    {
                                        var name = $"CombatMatrix_L{level}_T{targets}_{queen}_{wildfire}_{tool}_{potion}_{hypercharge}_S{seed}";
                                        Add(scenarios, name, 90, level, MchScenarioKind.CombatMatrix, targetCount: targets, queen: queen, wildfire: wildfire, potion: potion, tools: tool, hypercharge: hypercharge, seed: seed, seedOverride: seedOverride, durationOverride: durationOverride);
                                    }
    }

    private static void Add(
        List<MchScenario> scenarios,
        string name,
        double duration,
        int level,
        MchScenarioKind kind,
        int targetCount = 1,
        MchQueenStrategy queen = MchQueenStrategy.MinGauge,
        MchWildfireStrategy wildfire = MchWildfireStrategy.ASAP,
        MchPotionStrategy potion = MchPotionStrategy.Off,
        MchToolStrategy tools = MchToolStrategy.Automatic,
        MchOffensiveStrategy hypercharge = MchOffensiveStrategy.Automatic,
        MchOffensiveStrategy buffs = MchOffensiveStrategy.Automatic,
        int seed = 1,
        bool dancingMad = false,
        bool targetOverride = false,
        bool normalOvercapOnly = false,
        double? durationOverride = null,
        int? seedOverride = null)
    {
        var scenario = new MchScenario(
            name,
            durationOverride ?? duration,
            level,
            kind,
            queen,
            wildfire,
            potion,
            tools,
            hypercharge,
            buffs,
            seedOverride ?? seed,
            targetCount,
            dancingMad,
            targetOverride,
            normalOvercapOnly);
        scenarios.Add(scenario);
    }
}
