using BossMod;
using AID = BossMod.SAM.AID;

namespace XanTimelineHarness;

// Samurai potencies as the 7.5 client describes them (Action sheet descriptions, gnum68 == 34, evaluated per level). Positionals are
// assumed to land and are stored with their bonus included; "combo" is also what Meikyo Shisui grants.
internal static class SamPotency
{
    public static float Of(AID aid, int level, bool combo)
    {
        var l94 = level >= 94;
        var l84 = level >= 84;
        var l66 = level >= 66;
        return aid switch
        {
            AID.Hakaze => l66 ? 200 : 180,
            AID.Gyofu => 240,
            AID.Jinpu or AID.Shifu => combo ? (l94 ? 300 : l66 ? 280 : 260) : (l94 ? 140 : l66 ? 120 : 100),
            // rear / flank included
            AID.Gekko or AID.Kasha => combo ? (l94 ? 420 : l84 ? 370 : 360) : (l94 ? 210 : l84 ? 160 : 150),
            AID.Yukikaze => combo ? (l94 ? 340 : l84 ? 290 : 280) : (l94 ? 160 : l84 ? 110 : 100),
            AID.Enpi => 100,
            AID.Fuga => 90,
            AID.Fuko => 100,
            AID.Mangetsu or AID.Oka => combo ? 120 : 100,
            AID.Higanbana => 200,
            AID.TenkaGoken => 300,
            AID.MidareSetsugekka or AID.KaeshiSetsugekka => l94 ? 680 : 620,
            AID.KaeshiGoken => 300,
            AID.TendoGoken or AID.TendoKaeshiGoken => 410,
            AID.TendoSetsugekka or AID.TendoKaeshiSetsugekka => 1100,
            AID.OgiNamikiri or AID.KaeshiNamikiri => l94 ? 1000 : 860,
            AID.Zanshin => 940,
            AID.Shoha => l94 ? 640 : 560,
            AID.HissatsuShinten => 250,
            AID.HissatsuKyuten => 100,
            AID.HissatsuGyoten => 100,
            AID.HissatsuYaten => 100,
            AID.HissatsuSenei => 800,
            AID.HissatsuGuren => 400,
            _ => 0
        };
    }

    public static float EnhancedEnpi(int level) => level >= 94 ? 270 : 260;
    public static float HiganbanaTick(int level) => level >= 94 ? 50 : 45;
    public const float HiganbanaDuration = 60;
    public const float DotTick = 3;

    public static bool GuaranteedCrit(AID aid) => aid is AID.MidareSetsugekka or AID.KaeshiSetsugekka or AID.TendoSetsugekka or AID.TendoKaeshiSetsugekka
        or AID.OgiNamikiri or AID.KaeshiNamikiri;

    public static float Falloff(AID aid) => aid is AID.OgiNamikiri or AID.KaeshiNamikiri or AID.Zanshin or AID.Shoha ? 0.6f : 1;

    public enum Shape { Single, Cone8, Self5, Self8, Line10 }

    public static Shape ShapeOf(AID aid) => aid switch
    {
        AID.Fuga or AID.OgiNamikiri or AID.KaeshiNamikiri or AID.Zanshin => Shape.Cone8,
        AID.Fuko or AID.Mangetsu or AID.Oka or AID.HissatsuKyuten => Shape.Self5,
        AID.TenkaGoken or AID.KaeshiGoken or AID.TendoGoken or AID.TendoKaeshiGoken => Shape.Self8,
        AID.HissatsuGuren or AID.Shoha => Shape.Line10,
        _ => Shape.Single
    };
}
