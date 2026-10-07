namespace BossMod.Autorotation.xan;

// Single source of Reaper potency numbers used by the rotation's forecasts and comparisons.
// Pure data with no engine dependencies so an offline tool can compile the same file.
// Trait flags: m1 = Death Scythe Mastery, m2 = Death Scythe Mastery II, m3 = Melee Mastery III.
public static class RprPotency
{
    // Uncomboed follow-ups and ranged fillers (used by offline scoring only).
    public const float WaxingSliceUncomboed = 260f;
    public const float InfernalSliceUncomboed = 280f;
    public const float NightmareScytheUncomboedPerTarget = 120f;
    public const float Harpe = 300f;

    // Reaver AoE and Enshroud actions
    public const float GuillotinePerTarget = 200f;
    public const float ExecutionersGuillotinePerTarget = 300f;
    public static float VoidReaping(bool m3) => m3 ? 580f : 500f;
    public const float EnhancedReapingBonus = 60f;
    public const float GrimReapingPerTarget = 220f;
    public const float LemuresSlice = 280f;
    public const float LemuresScythePerTarget = 100f;
    public const float Communio = 1100f;
    public const float CommunioFalloff = 0.4f;
    public const float Sacrificium = 530f;
    public const float SacrificiumFalloff = 0.5f;

    // Damage multipliers
    public const float DeathsDesignMultiplier = 1.10f;
    public const float ArcaneCircleMultiplier = 1.03f;

    // Single-target combo
    public static float Slice(bool m1, bool m2, bool m3) => m3 ? 420f : m2 ? 320f : m1 ? 300f : 240f;
    public static float WaxingSliceCombo(bool m1, bool m2, bool m3) => m3 ? 500f : m2 ? 400f : m1 ? 380f : 300f;
    public static float InfernalSliceCombo(bool m1, bool m2, bool m3) => m3 ? 600f : m2 ? 500f : m1 ? 460f : 400f;
    public static float ComboAverage(bool m1, bool m2, bool m3)
        => (Slice(m1, m2, m3) + WaxingSliceCombo(m1, m2, m3) + InfernalSliceCombo(m1, m2, m3)) / 3f;

    // AoE combo, per target
    public static float SpinningScythe(bool m1) => m1 ? 140f : 100f;
    public static float NightmareScytheCombo(bool m1) => m1 ? 180f : 140f;

    // Soul generators
    public static float SoulSlice(bool m3) => m3 ? 520f : 460f;
    public const float SoulScythePerTarget = 180f;
    public const float ShadowOfDeath = 300f;
    public const float WhorlOfDeathPerTarget = 100f;
    public static float HarvestMoon(bool m3) => m3 ? 800f : 600f;
    public const float HarvestMoonFalloff = 0.6f;

    // Soul spenders
    public const float BloodStalk = 340f;
    public static float UnveiledGibbet(bool m3) => m3 ? 440f : 400f;
    public const float GrimSwathePerTarget = 140f;
    public const float Gluttony = 560f;
    public const float GluttonyFalloff = 0.75f;

    // Reaver GCDs. Enhanced bonus applies to Gibbet/Gallows and their Executioner's versions.
    public const float Gibbet = 560f;
    public const float ExecutionersGibbet = 760f;
    public const float EnhancedReaverBonus = 60f;
    public const float EnhancedReaverDuration = 60f;

    // Burst
    public const float Perfectio = 1300f;
    public const float PerfectioFalloff = 0.8f;
    public static float PlentifulHarvest(int immortalSacrificeStacks) => 680f + 40f * Math.Clamp(immortalSacrificeStacks, 1, 8);
    public const float PlentifulHarvestFalloff = 0.8f;
    // Total value assigned to one full Enshroud sequence by the normal-rotation resource model.
    public const float EnshroudSequenceValue = 4860f;

    // Penalty applied when a clipped Gluttony leaves no weave slot for True North before the first Executioner.
    public const float MissedPositionalLoss = 60f;
}
