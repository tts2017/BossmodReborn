namespace MchRegression;

public enum MchAction
{
    None,
    SplitShot,
    SlugShot,
    CleanShot,
    HeatedSplitShot,
    HeatedSlugShot,
    HeatedCleanShot,
    SpreadShot,
    Scattergun,
    HotShot,
    AirAnchor,
    Drill,
    Bioblaster,
    ChainSaw,
    Excavator,
    FullMetalField,
    HeatBlast,
    BlazingShot,
    AutoCrossbow,
    Reassemble,
    Flamethrower,
    GaussRound,
    Ricochet,
    DoubleCheck,
    Checkmate,
    BarrelStabilizer,
    Wildfire,
    Hypercharge,
    AutomatonQueen,
    RookAutoturret,
    QueenOverdrive,
    RookOverdrive,
    LegGraze,
    Potion,
    Tactician,
    Dismantle
}

public enum MchActionKind
{
    Gcd,
    Ogcd,
    Item
}

public enum MchQueenStrategy
{
    MinGauge,
    FullGauge,
    RaidBuffsOnly,
    Never
}

public enum MchWildfireStrategy
{
    ASAP,
    Delay,
    Hypercharge
}

public enum MchPotionStrategy
{
    Off,
    OpenerAndEvenBursts,
    EvenBursts
}

public enum MchToolStrategy
{
    Automatic,
    Delay
}

public enum MchOffensiveStrategy
{
    Automatic,
    Force,
    Delay
}

public sealed record MchActionDefinition(
    MchAction Action,
    string Name,
    MchActionKind Kind,
    int UnlockLevel,
    double Recast,
    int MaxCharges,
    bool RequiresEnemyTarget,
    bool IsHeatGcd,
    bool GrantsBattery,
    int BatteryGain,
    bool IsReassembleEligible);

public static class MchActionModel
{
    public const double StandardGcd = 2.5;
    public const double HeatGcd = 1.5;
    public const int AutoCrossbowBreakpoint = 6;
    public const int AutoCrossbowHighTargetBreakpoint = 8;
    public const int ToolPriorityHigh = 20;
    public const int ToolPriorityChainSaw = 10;

    public static readonly IReadOnlyDictionary<MchAction, MchActionDefinition> Definitions = BuildDefinitions();

    public static MchActionDefinition Definition(MchAction action) => Definitions[action];

    public static bool IsUnlocked(MchAction action, int level)
        => Definitions.TryGetValue(action, out var definition) && level >= definition.UnlockLevel;

    public static bool IsGcd(MchAction action)
        => Definitions.TryGetValue(action, out var definition) && definition.Kind == MchActionKind.Gcd;

    public static bool IsOgcd(MchAction action)
        => Definitions.TryGetValue(action, out var definition) && definition.Kind == MchActionKind.Ogcd;

    public static bool IsEnemyAction(MchAction action)
        => Definitions.TryGetValue(action, out var definition) && definition.RequiresEnemyTarget;

    public static bool IsHeatGcd(MchAction action)
        => Definitions.TryGetValue(action, out var definition) && definition.IsHeatGcd;

    public static bool IsToolBlockedDuringOverheat(MchAction action)
        => action is MchAction.FullMetalField or MchAction.Excavator or MchAction.ChainSaw or MchAction.AirAnchor or MchAction.Drill or MchAction.Bioblaster;

    public static bool IsReassembleEligible(MchAction action)
        => Definitions.TryGetValue(action, out var definition) && definition.IsReassembleEligible;

    public static bool IsBasicCombo(MchAction action)
        => action is MchAction.SplitShot or MchAction.SlugShot or MchAction.CleanShot or MchAction.HeatedSplitShot or MchAction.HeatedSlugShot or MchAction.HeatedCleanShot;

    public static MchAction BestSplitShot(int level) => IsUnlocked(MchAction.HeatedSplitShot, level) ? MchAction.HeatedSplitShot : MchAction.SplitShot;
    public static MchAction BestSlugShot(int level) => IsUnlocked(MchAction.HeatedSlugShot, level) ? MchAction.HeatedSlugShot : MchAction.SlugShot;
    public static MchAction BestCleanShot(int level) => IsUnlocked(MchAction.HeatedCleanShot, level) ? MchAction.HeatedCleanShot : MchAction.CleanShot;
    public static MchAction BestHeatShot(int level) => IsUnlocked(MchAction.BlazingShot, level) ? MchAction.BlazingShot : MchAction.HeatBlast;
    public static MchAction BestSpreadShot(int level) => IsUnlocked(MchAction.Scattergun, level) ? MchAction.Scattergun : MchAction.SpreadShot;
    public static MchAction BestGaussRound(int level) => IsUnlocked(MchAction.DoubleCheck, level) ? MchAction.DoubleCheck : MchAction.GaussRound;
    public static MchAction BestRicochet(int level) => IsUnlocked(MchAction.Checkmate, level) ? MchAction.Checkmate : MchAction.Ricochet;

    public static string Name(MchAction action)
        => Definitions.TryGetValue(action, out var definition) ? definition.Name : action.ToString();

    private static Dictionary<MchAction, MchActionDefinition> BuildDefinitions()
    {
        var definitions = new Dictionary<MchAction, MchActionDefinition>
        {
            [MchAction.None] = new(MchAction.None, "None", MchActionKind.Gcd, 1, StandardGcd, 1, false, false, false, 0, false),
            [MchAction.SplitShot] = new(MchAction.SplitShot, "Split Shot", MchActionKind.Gcd, 1, StandardGcd, 1, true, false, false, 0, false),
            [MchAction.SlugShot] = new(MchAction.SlugShot, "Slug Shot", MchActionKind.Gcd, 2, StandardGcd, 1, true, false, false, 0, false),
            [MchAction.CleanShot] = new(MchAction.CleanShot, "Clean Shot", MchActionKind.Gcd, 26, StandardGcd, 1, true, false, true, 10, false),
            [MchAction.HeatedSplitShot] = new(MchAction.HeatedSplitShot, "Heated Split Shot", MchActionKind.Gcd, 54, StandardGcd, 1, true, false, false, 0, false),
            [MchAction.HeatedSlugShot] = new(MchAction.HeatedSlugShot, "Heated Slug Shot", MchActionKind.Gcd, 60, StandardGcd, 1, true, false, false, 0, false),
            [MchAction.HeatedCleanShot] = new(MchAction.HeatedCleanShot, "Heated Clean Shot", MchActionKind.Gcd, 64, StandardGcd, 1, true, false, true, 10, false),
            [MchAction.SpreadShot] = new(MchAction.SpreadShot, "Spread Shot", MchActionKind.Gcd, 18, StandardGcd, 1, true, false, false, 0, false),
            [MchAction.Scattergun] = new(MchAction.Scattergun, "Scattergun", MchActionKind.Gcd, 82, StandardGcd, 1, true, false, false, 0, false),
            [MchAction.HotShot] = new(MchAction.HotShot, "Hot Shot", MchActionKind.Gcd, 4, StandardGcd, 1, true, false, true, 20, true),
            [MchAction.AirAnchor] = new(MchAction.AirAnchor, "Air Anchor", MchActionKind.Gcd, 76, StandardGcd, 1, true, false, true, 20, true),
            [MchAction.Drill] = new(MchAction.Drill, "Drill", MchActionKind.Gcd, 58, 20, 2, true, false, false, 0, true),
            [MchAction.Bioblaster] = new(MchAction.Bioblaster, "Bioblaster", MchActionKind.Gcd, 72, 20, 2, true, false, false, 0, false),
            [MchAction.ChainSaw] = new(MchAction.ChainSaw, "Chain Saw", MchActionKind.Gcd, 90, StandardGcd, 1, true, false, true, 20, true),
            [MchAction.Excavator] = new(MchAction.Excavator, "Excavator", MchActionKind.Gcd, 96, StandardGcd, 1, true, false, true, 20, true),
            [MchAction.FullMetalField] = new(MchAction.FullMetalField, "Full Metal Field", MchActionKind.Gcd, 100, StandardGcd, 1, true, false, false, 0, false),
            [MchAction.HeatBlast] = new(MchAction.HeatBlast, "Heat Blast", MchActionKind.Gcd, 35, HeatGcd, 1, true, true, false, 0, false),
            [MchAction.BlazingShot] = new(MchAction.BlazingShot, "Blazing Shot", MchActionKind.Gcd, 68, HeatGcd, 1, true, true, false, 0, false),
            [MchAction.AutoCrossbow] = new(MchAction.AutoCrossbow, "Auto Crossbow", MchActionKind.Gcd, 52, HeatGcd, 1, true, true, false, 0, false),
            [MchAction.Reassemble] = new(MchAction.Reassemble, "Reassemble", MchActionKind.Gcd, 10, StandardGcd, 2, false, false, false, 0, false),
            [MchAction.Flamethrower] = new(MchAction.Flamethrower, "Flamethrower", MchActionKind.Gcd, 70, StandardGcd, 1, true, false, false, 0, false),
            [MchAction.GaussRound] = new(MchAction.GaussRound, "Gauss Round", MchActionKind.Ogcd, 15, 30, 3, true, false, false, 0, false),
            [MchAction.Ricochet] = new(MchAction.Ricochet, "Ricochet", MchActionKind.Ogcd, 50, 30, 3, true, false, false, 0, false),
            [MchAction.DoubleCheck] = new(MchAction.DoubleCheck, "Double Check", MchActionKind.Ogcd, 92, 30, 3, true, false, false, 0, false),
            [MchAction.Checkmate] = new(MchAction.Checkmate, "Checkmate", MchActionKind.Ogcd, 92, 30, 3, true, false, false, 0, false),
            [MchAction.BarrelStabilizer] = new(MchAction.BarrelStabilizer, "Barrel Stabilizer", MchActionKind.Ogcd, 66, 120, 1, false, false, false, 0, false),
            [MchAction.Wildfire] = new(MchAction.Wildfire, "Wildfire", MchActionKind.Ogcd, 45, 120, 1, true, false, false, 0, false),
            [MchAction.Hypercharge] = new(MchAction.Hypercharge, "Hypercharge", MchActionKind.Ogcd, 30, 10, 1, false, false, false, 0, false),
            [MchAction.AutomatonQueen] = new(MchAction.AutomatonQueen, "Automaton Queen", MchActionKind.Ogcd, 80, 1, 1, false, false, false, 0, false),
            [MchAction.RookAutoturret] = new(MchAction.RookAutoturret, "Rook Autoturret", MchActionKind.Ogcd, 40, 1, 1, false, false, false, 0, false),
            [MchAction.QueenOverdrive] = new(MchAction.QueenOverdrive, "Queen Overdrive", MchActionKind.Ogcd, 80, 1, 1, false, false, false, 0, false),
            [MchAction.RookOverdrive] = new(MchAction.RookOverdrive, "Rook Overdrive", MchActionKind.Ogcd, 40, 1, 1, false, false, false, 0, false),
            [MchAction.LegGraze] = new(MchAction.LegGraze, "Leg Graze", MchActionKind.Ogcd, 6, 30, 1, true, false, false, 0, false),
            [MchAction.Potion] = new(MchAction.Potion, "Potion", MchActionKind.Item, 1, 270, 1, false, false, false, 0, false),
            [MchAction.Tactician] = new(MchAction.Tactician, "Tactician", MchActionKind.Ogcd, 56, 90, 1, false, false, false, 0, false),
            [MchAction.Dismantle] = new(MchAction.Dismantle, "Dismantle", MchActionKind.Ogcd, 62, 120, 1, true, false, false, 0, false)
        };
        return definitions;
    }
}
