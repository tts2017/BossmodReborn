using BossMod;
using AID = BossMod.VPR.AID;

namespace XanTimelineHarness;

// Viper potencies as the 7.5 client describes them (Action sheet descriptions, gnum68 == 41, evaluated per level). Positionals are
// assumed to land and are stored with their bonus included; the venom / honed bonuses are separate constants because they depend on
// which status the press consumes.
internal static class VprPotency
{
    public static float Of(AID aid, int level) => aid switch
    {
        AID.SteelFangs or AID.ReavingFangs => level >= 74 ? 200 : 180,
        AID.HuntersSting or AID.SwiftskinsSting => level >= 74 ? 300 : 280,
        // flank / rear included
        AID.FlankstingStrike or AID.FlanksbaneFang or AID.HindstingStrike or AID.HindsbaneFang => level >= 84 ? 400 : 380,
        AID.WrithingSnap => 200,
        AID.SteelMaw or AID.ReavingMaw => 120,
        AID.HuntersBite or AID.SwiftskinsBite => 180,
        AID.JaggedMaw or AID.BloodiedMaw => 180,
        AID.DeathRattle => 280,
        AID.LastLash => 120,
        AID.Vicewinder => 540,
        // flank / rear included
        AID.HuntersCoil or AID.SwiftskinsCoil => 680,
        AID.Vicepit => 250,
        AID.HuntersDen or AID.SwiftskinsDen => 300,
        AID.TwinfangBite or AID.TwinbloodBite => 120,
        AID.TwinfangThresh or AID.TwinbloodThresh => 50,
        AID.UncoiledFury => 680,
        AID.UncoiledTwinfang or AID.UncoiledTwinblood => 120,
        AID.FirstGeneration or AID.SecondGeneration or AID.ThirdGeneration or AID.FourthGeneration => 480,
        AID.Reawaken => 750,
        AID.Ouroboros => 1150,
        AID.FirstLegacy or AID.SecondLegacy or AID.ThirdLegacy or AID.FourthLegacy => 320,
        _ => 0
    };

    // what a finisher / coil loses when its flank or rear is missed
    public const float FinisherPositionalBonus = 60;
    public const float CoilPositionalBonus = 50;
    // Honed Steel / Honed Reavers on the matching Fangs / Maw
    public const float HonedFangsBonus = 100;
    public const float HonedMawBonus = 20;
    // the venom a finisher consumes (Flankstung Venom for Flanksting Strike, ...)
    public const float VenomBonus = 100;
    // Grimhunter's / Grimskin's Venom on Jagged / Bloodied Maw
    public const float GrimBonus = 40;
    // Hunter's / Swiftskin's Venom and Poised for Twinfang / Twinblood on the twin oGCDs
    public const float TwinBonus = 50;
    // Fellhunter's / Fellskin's Venom on the Thresh oGCDs
    public const float ThreshBonus = 30;
    // a Generation executed right after the previous step of the Reawaken sequence
    public const float GenerationInSequence = 680;

    public static bool Falloff(AID aid) => aid is AID.UncoiledFury or AID.FirstGeneration or AID.SecondGeneration or AID.ThirdGeneration or AID.FourthGeneration
        or AID.Reawaken or AID.Ouroboros or AID.UncoiledTwinfang or AID.UncoiledTwinblood or AID.FirstLegacy or AID.SecondLegacy or AID.ThirdLegacy or AID.FourthLegacy;
    public const float FalloffMultiplier = 0.25f;

    public enum Shape { Single, Self5, Target5 }

    public static Shape ShapeOf(AID aid) => aid switch
    {
        AID.SteelMaw or AID.ReavingMaw or AID.HuntersBite or AID.SwiftskinsBite or AID.JaggedMaw or AID.BloodiedMaw or AID.LastLash or AID.Vicepit
            or AID.HuntersDen or AID.SwiftskinsDen or AID.TwinfangThresh or AID.TwinbloodThresh or AID.Reawaken => Shape.Self5,
        AID.UncoiledFury or AID.FirstGeneration or AID.SecondGeneration or AID.ThirdGeneration or AID.FourthGeneration or AID.Ouroboros
            or AID.UncoiledTwinfang or AID.UncoiledTwinblood or AID.FirstLegacy or AID.SecondLegacy or AID.ThirdLegacy or AID.FourthLegacy => Shape.Target5,
        _ => Shape.Single
    };
}
