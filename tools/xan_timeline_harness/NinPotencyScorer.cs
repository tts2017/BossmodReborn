using BossMod;
using BossMod.Autorotation.xan;
using AID = BossMod.NIN.AID;

namespace XanTimelineHarness;

// Potency of one NIN press on one target before multipliers. Values come from NinPotency (audited against the client); only the
// per-press state - combo, Kazematoi, Meisui - is decided here, read before the press consumes it.
internal static class NinPotencyScorer
{
    // The harness stands the player in front of the target, where no ninja plays from; positionals are credited so that Aeolian
    // Edge and Armor Crush are scored the way they land in real play. The rotation's own positional choice is left untouched.
    public static bool AssumePositionals = true;

    public static float Base(AID aid, int level, bool combo, bool kazematoi, bool meisui)
    {
        var positional = AssumePositionals;
        return aid switch
        {
            AID.GustSlash or AID.HakkeMujinsatsu => NinPotency.Of(aid, level, combo ? NinPotencyVariant.Combo : NinPotencyVariant.Base),
            AID.AeolianEdge => NinPotency.Of(aid, level, combo ? positional ? NinPotencyVariant.ComboPositional : NinPotencyVariant.Combo : positional ? NinPotencyVariant.Positional : NinPotencyVariant.Base)
                + (kazematoi && level >= 54 ? NinPotency.KazematoiBonus : 0),
            AID.ArmorCrush => NinPotency.Of(aid, level, combo ? positional ? NinPotencyVariant.ComboPositional : NinPotencyVariant.Combo : positional ? NinPotencyVariant.Positional : NinPotencyVariant.Base),
            AID.TrickAttack => NinPotency.Of(aid, level, positional ? NinPotencyVariant.Positional : NinPotencyVariant.Base),
            AID.Bhavacakra or AID.ZeshoMeppo => NinPotency.Of(aid, level, meisui ? NinPotencyVariant.Meisui : NinPotencyVariant.Base),
            AID.Doton or AID.TCJDoton => NinPotency.Of(aid, level) * 6, // 18s of ground effect ticking every 3s; the harness targets never leave it
            _ => NinPotency.Of(aid, level)
        };
    }

    // Seconds between the press and the damage landing, which is when a debuff on the target is checked. BossMod's table covers the
    // regular actions; the Ten Chi Jin versions reuse the timing of the ninjutsu they cast.
    public static float LandingDelay(AID aid)
    {
        var lookup = aid switch
        {
            AID.FumaTen or AID.FumaChi or AID.FumaJin => AID.FumaShuriken,
            AID.TCJKaton => AID.Katon,
            AID.TCJRaiton => AID.Raiton,
            AID.TCJHyoton => AID.Hyoton,
            AID.TCJHuton => AID.Huton,
            AID.TCJDoton => AID.Doton,
            AID.TCJSuiton => AID.Suiton,
            _ => aid
        };
        var delay = ApplicationDelay.Get(ActionID.MakeSpell(lookup));
        return delay > 0 ? delay : 0.6f;
    }
}
