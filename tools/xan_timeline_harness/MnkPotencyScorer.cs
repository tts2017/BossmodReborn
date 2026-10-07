using BossMod;
using AID = BossMod.MNK.AID;
using SID = BossMod.MNK.SID;

namespace XanTimelineHarness;

// Offline potency estimate for one executed monk action, evaluated against the world state *before* the action is
// applied. Positionals are assumed to hit (the rotation queues True North for them).
//
// Every number below is the level-100 value read out of the client's own tooltips - see the `mnk-potency-dump`
// command, which prints ActionTransient.Description including the level-gated <if(gnum72>=N,...)> branches. Do not
// "correct" these from memory; re-run the dump instead.
internal static class MnkPotencyScorer
{
    private const uint MedicatedStatus = 49;
    private const float MedicatedMultiplier = 1.08f;
    private const float RiddleOfFireMultiplier = 1.15f;
    private const float BrotherhoodMultiplier = 1.05f;
    private const float BattleLitanyMultiplier = 1.05f;
    // tooltips say "35% less for all remaining enemies"
    private const float Falloff = 0.65f;
    // Leaping Opo/Bootshine and Shadow of the Destroyer say "Opo-opo Form Bonus: Guarantees a critical hit", and the
    // bonus also applies while Perfect Balance or Formless Fist is standing in for the form. A guaranteed crit is not
    // free potency, so the flat tooltip number understates those GCDs by the ratio between a guaranteed crit and an
    // average hit: with a level-100 crit rate of ~28% and a crit multiplier of ~1.62, that is 1.62 / (1 + 0.28 * 0.62)
    // = 1.38. The absolute number is gear dependent; what matters here is that opo GCDs stop being scored as if the
    // bonus did not exist, which is what made every "shift opo into Riddle of Fire" experiment look worthless.
    private const float GuaranteedCritMultiplier = 1.38f;

    public static float Estimate(WorldState world, Actor player, AID action, Actor? target, int opoFury, int raptorFury, int coeurlFury, int chakra)
    {
        var level = player.Level;
        var l84 = level >= 84;
        var l92 = level >= 92;
        var l94 = level >= 94;

        float primary;
        var falloff = 1f;
        var shape = Shape.Single;
        switch (action)
        {
            // opo-opo step: Bootshine becomes Leaping Opo at 92
            case AID.Bootshine:
            case AID.LeapingOpo:
                primary = l92
                    ? opoFury > 0 ? 460 : 260
                    : opoFury > 0 ? l84 ? 420 : 380 : l84 ? 220 : 180;
                if (OpoFormBonusActive(player))
                    primary *= GuaranteedCritMultiplier;
                break;
            case AID.DragonKick: primary = l94 ? 320 : l84 ? 280 : 240; break;

            // raptor step: True Strike becomes Rising Raptor at 92
            case AID.TrueStrike:
            case AID.RisingRaptor:
                primary = l92
                    ? raptorFury > 0 ? 540 : 340
                    : raptorFury > 0 ? l84 ? 500 : 460 : l84 ? 300 : 260;
                break;
            case AID.TwinSnakes: primary = l94 ? 420 : l84 ? 380 : 340; break;

            // coeurl step: Snap Punch becomes Pouncing Coeurl at 92; flank assumed
            case AID.SnapPunch:
            case AID.PouncingCoeurl:
                primary = l92
                    ? coeurlFury > 0 ? 520 : 370
                    : coeurlFury > 0 ? l84 ? 480 : 440 : l84 ? 330 : 290;
                break;
            case AID.Demolish: primary = l94 ? 420 : l84 ? 380 : 340; break; // rear assumed

            // Six-sided Star spends every chakra and gains 80 per chakra
            case AID.SixSidedStar: primary = (l94 ? 780 : 710) + 80 * chakra; break;

            // aoe combo (self-centred, no falloff)
            case AID.ArmOfTheDestroyer: primary = OpoFormBonusActive(player) ? 120 : 110; shape = Shape.SelfCircle; break;
            case AID.ShadowOfTheDestroyer:
                primary = 120;
                if (OpoFormBonusActive(player))
                    primary *= GuaranteedCritMultiplier;
                shape = Shape.SelfCircle;
                break;
            case AID.FourPointFury: primary = 140; shape = Shape.SelfCircle; break;
            case AID.Rockbreaker: primary = 150; shape = Shape.SelfCircle; break;

            // masterful blitz
            case AID.ElixirField: primary = 800; falloff = Falloff; shape = Shape.SelfCircle; break;
            case AID.ElixirBurst: primary = 900; falloff = Falloff; shape = Shape.SelfCircle; break;
            case AID.FlintStrike: primary = 800; falloff = Falloff; shape = Shape.SelfCircle; break;
            case AID.RisingPhoenix: primary = 900; falloff = Falloff; shape = Shape.SelfCircle; break;
            case AID.CelestialRevolution: primary = 600; break;
            case AID.TornadoKick: primary = 1200; falloff = Falloff; shape = Shape.Splash; break;
            case AID.PhantomRush: primary = l94 ? 1500 : 1400; falloff = Falloff; shape = Shape.Splash; break;

            // replies
            case AID.FiresReply: primary = 1400; falloff = Falloff; shape = Shape.Splash; break;
            case AID.WindsReply: primary = 1040; falloff = Falloff; shape = Shape.Line; break;

            // chakra spenders
            case AID.SteelPeak: primary = 180; break;
            case AID.ForbiddenChakra: primary = l84 ? 400 : 310; break;
            case AID.HowlingFist: primary = 100; falloff = Falloff; shape = Shape.Line; break;
            case AID.Enlightenment: primary = 160; falloff = Falloff; shape = Shape.Line; break;

            default: return 0;
        }

        var global = 1f;
        if (player.FindStatus((uint)SID.RiddleOfFire, player.InstanceID) != null)
            global *= RiddleOfFireMultiplier;
        if (player.FindStatus((uint)SID.Brotherhood) != null)
            global *= BrotherhoodMultiplier;
        if (player.FindStatus(MedicatedStatus) != null)
            global *= MedicatedMultiplier;
        if (player.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null)
            global *= BattleLitanyMultiplier;

        if (shape == Shape.Single)
            return target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true } ? primary * global : 0f;

        var anchor = shape == Shape.Splash && target != null ? target : player;
        var radius = shape switch { Shape.SelfCircle => 5f, Shape.Line => 10f, Shape.Splash => 5f, _ => 0f };
        var total = 0f;
        var hit = 0;
        // main target first so falloff applies to the rest
        if (target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true })
        {
            total += primary;
            ++hit;
        }
        foreach (var enemy in world.Actors)
        {
            if (enemy == target || enemy.Type != ActorType.Enemy || enemy.IsDead || !enemy.IsTargetable)
                continue;
            if (anchor.DistanceToHitbox(enemy) > radius)
                continue;
            total += primary * (hit == 0 ? 1f : falloff);
            ++hit;
        }
        return total * global;
    }

    // Opo-opo form, or either of the two effects that substitute for a form requirement. Perfect Balance and Formless
    // Fist both grant the form bonus, which is why the standard rotation spends Perfect Balance on opo GCDs.
    private static bool OpoFormBonusActive(Actor player)
        => player.FindStatus((uint)SID.OpoOpoForm, player.InstanceID) != null
        || player.FindStatus((uint)SID.FormlessFist, player.InstanceID) != null
        || player.FindStatus((uint)SID.PerfectBalance, player.InstanceID) != null;

    private enum Shape { Single, SelfCircle, Line, Splash }
}
