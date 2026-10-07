using BossMod;
using AID = BossMod.MCH.AID;

namespace XanTimelineHarness;

// Machinist potencies as the 7.5 client describes them (Action sheet descriptions, gnum68 == 31, evaluated per level), including the
// Automaton Queen / Rook Autoturret attacks at the 50 Battery they are listed for (they scale linearly up to double at 100).
internal static class MchPotency
{
    public const uint ArmPunch = 16504;
    public const uint PileBunker = 16503;
    public const uint RollerDash = 17206;
    public const uint CrownedCollider = 25787;
    public const uint VolleyFire = 2891;
    public const uint RookOverload = 7416;

    public static float Of(AID aid, int level, bool combo)
    {
        var l94 = level >= 94;
        var l84 = level >= 84;
        return aid switch
        {
            AID.SplitShot => 140,
            AID.SlugShot => combo ? 210 : 100,
            AID.CleanShot => combo ? 270 : 100,
            AID.HeatedSplitShot => l94 ? 220 : l84 ? 200 : 180,
            AID.HeatedSlugShot => combo ? (l94 ? 320 : l84 ? 300 : 280) : (l94 ? 140 : l84 ? 120 : 100),
            AID.HeatedCleanShot => combo ? (l94 ? 420 : l84 ? 380 : 360) : (l94 ? 160 : l84 ? 120 : 100),
            AID.HotShot => 240,
            AID.GaussRound or AID.Ricochet => 130,
            AID.DoubleCheck or AID.Checkmate => 180,
            AID.SpreadShot => 110,
            AID.Scattergun => 130,
            AID.AutoCrossbow => l94 ? 180 : 140,
            AID.HeatBlast => 200,
            AID.BlazingShot => l94 ? 240 : 220,
            AID.Drill or AID.AirAnchor or AID.ChainSaw => l94 ? 660 : 620,
            AID.Excavator => 660,
            AID.FullMetalField => 900,
            AID.Bioblaster => 50,
            _ => 0
        };
    }

    // Wildfire: per weaponskill landed on the target while it lasts, up to 6
    public static float WildfirePerHit(int level) => level >= 78 ? 240 : 100;
    public const int WildfireMaxHits = 6;
    public const float WildfireDuration = 10;
    // Overheated: added to single-target weaponskills
    public const float OverheatedBonus = 20;
    public const float BioblasterTick = 50;
    public const float BioblasterDuration = 15;
    public const float FlamethrowerTick = 120;
    public const float FlamethrowerDuration = 10;
    public const float DotTick = 3;

    // at 50 Battery; each attack scales with Battery / 50 at deployment
    public static float PetAttack(uint id) => id switch
    {
        ArmPunch => 120,
        PileBunker => 340,
        RollerDash => 240,
        CrownedCollider => 390,
        VolleyFire => 35,
        RookOverload => 160,
        _ => 0
    };

    public static bool GuaranteedCritDirect(AID aid) => aid == AID.FullMetalField;

    // share of the damage the other targets take (Ricochet / Double Check / Checkmate 30% less, the rest 25% less)
    public static float Falloff(AID aid) => aid switch
    {
        AID.Ricochet or AID.DoubleCheck or AID.Checkmate => 0.7f,
        AID.ChainSaw or AID.Excavator or AID.FullMetalField => 0.75f,
        _ => 1
    };

    public enum Shape { Single, Cone12, Target5, Line25 }

    public static Shape ShapeOf(AID aid) => aid switch
    {
        AID.SpreadShot or AID.Scattergun or AID.AutoCrossbow or AID.Bioblaster or AID.Flamethrower => Shape.Cone12,
        AID.Ricochet or AID.DoubleCheck or AID.Checkmate or AID.Excavator or AID.FullMetalField => Shape.Target5,
        AID.ChainSaw => Shape.Line25,
        _ => Shape.Single
    };

    public static bool IsSingleTargetWeaponskill(AID aid) => aid is AID.SplitShot or AID.SlugShot or AID.CleanShot or AID.HeatedSplitShot or AID.HeatedSlugShot
        or AID.HeatedCleanShot or AID.HotShot or AID.HeatBlast or AID.BlazingShot or AID.Drill or AID.AirAnchor;
}
