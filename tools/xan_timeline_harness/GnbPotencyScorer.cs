using System;
using BossMod;
using AID = BossMod.GNB.AID;
using SID = BossMod.GNB.SID;

namespace XanTimelineHarness;

// Offline potency estimate for one executed Gunbreaker action, evaluated against the world state *before* the action
// is applied. Single-target potencies are the level-100 values; AoE actions are summed over the enemies actually in
// range with the game's falloff. The absolute number is not a DPS simulation - it is a fixed yardstick, so a delta
// between two builds of AkechiGNB.cs is meaningful even though the total is not.
internal static class GnbPotencyScorer
{
    private const uint MedicatedStatus = 49;
    private const float MedicatedMultiplier = 1.06f;
    private const float BattleLitanyMultiplier = 1.05f;
    private const float BrotherhoodMultiplier = 1.05f;
    // No Mercy is a flat +20% for its 20s, and it is the whole point of the rotation being measured.
    private const float NoMercyMultiplier = 1.20f;

    private const float MeleeRadius = 5f;
    private const float BlastRadius = 5f;

    public static float Estimate(WorldState world, Actor player, AID action, Actor? target)
    {
        var level = player.Level;
        // Melee Mastery (L84) raises the 1-2-3 combo and the Gnashing Fang chain; Enhanced Brutal Shell is L52.
        var meleeMastery = level >= 84;
        var enhancedBrutal = level >= 52;

        float primary;
        var falloff = 1f;
        var shape = Shape.Single;
        switch (action)
        {
            case AID.KeenEdge: primary = meleeMastery ? 300 : 200; break;
            case AID.BrutalShell: primary = meleeMastery ? 380 : enhancedBrutal ? 300 : 240; break;
            case AID.SolidBarrel: primary = meleeMastery ? 460 : 360; break;
            case AID.BurstStrike: primary = meleeMastery ? 460 : 380; break;
            case AID.DemonSlice: primary = meleeMastery ? 160 : 100; shape = Shape.SelfCircle; break;
            case AID.DemonSlaughter: primary = meleeMastery ? 200 : 160; shape = Shape.SelfCircle; break;
            case AID.FatedCircle: primary = meleeMastery ? 320 : 300; shape = Shape.SelfCircle; break;
            case AID.GnashingFang: primary = meleeMastery ? 500 : 380; break;
            case AID.SavageClaw: primary = meleeMastery ? 560 : 460; break;
            case AID.WickedTalon: primary = meleeMastery ? 620 : 540; break;
            case AID.DoubleDown: primary = 1200; falloff = 0.85f; shape = Shape.SelfCircle; break; // tooltip: 15% less on every target after the first
            // Sonic Break is 300 up front plus a 30s / 60-per-tick dot; the harness has no dot ticker, so the
            // full expected value is credited at cast time.
            case AID.SonicBreak: primary = 300 + 600; break;
            case AID.ReignOfBeasts: primary = 800; break;
            case AID.NobleBlood: primary = 900; break;
            case AID.LionHeart: primary = 1000; break;
            case AID.BlastingZone: primary = 800; break;
            case AID.DangerZone: primary = 250; break;
            // Bow Shock is 150 on hit plus a 15s / 60-per-tick dot, again credited up front.
            case AID.BowShock: primary = 150 + 300; shape = Shape.SelfCircle; break;
            case AID.JugularRip: primary = 240; break;
            case AID.AbdomenTear: primary = 280; break;
            case AID.EyeGouge: primary = 320; break;
            case AID.Hypervelocity: primary = 200; break;
            case AID.FatedBrand: primary = 120; shape = Shape.Splash; break;
            case AID.LightningShot: primary = 150; break;
            default: return 0;
        }

        var global = 1f;
        if (Has(player, SID.NoMercy))
            global *= NoMercyMultiplier;
        if (player.FindStatus(MedicatedStatus) != null)
            global *= MedicatedMultiplier;
        if (player.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null)
            global *= BattleLitanyMultiplier;
        if (player.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null)
            global *= BrotherhoodMultiplier;

        if (shape == Shape.Single)
            return target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true } ? primary * global : 0f;

        var anchor = shape == Shape.Splash && target != null ? target : player;
        var radius = shape == Shape.Splash ? BlastRadius : MeleeRadius;
        var total = 0f;
        var hit = 0;
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

    private static bool Has(Actor actor, SID status) => actor.FindStatus((uint)status, actor.InstanceID) != null;

    private enum Shape { Single, SelfCircle, Splash }
}

// Value of resources still held when a run ends, so a scenario that ends right after a burst hold is not penalised
// for banking cartridges the way a truncated fight would be. A cartridge is worth the difference between spending it
// on Burst Strike and pressing the filler GCD it displaces; an unfinished gauge combo is worth its remaining steps.
internal static class GnbTerminalValue
{
    private const float Filler = (300f + 380f + 460f) / 3f;

    public static float Estimate(WorldState world, Actor player, GnbCombatState combat)
    {
        float Left(SID status) => MathF.Max(0, (float)((player.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);

        // Burst Strike (460) plus its Hypervelocity continuation (200), minus the filler GCD it replaces.
        const float Cartridge = 460f + 200f - Filler;
        var value = combat.Ammo * Cartridge;

        // Gnashing Fang / Reign steps already paid for: each remaining step is a free GCD's worth of extra potency.
        value += combat.GunComboStep switch
        {
            1 => 560f + 280f + 620f + 320f - 2 * Filler,
            2 => 620f + 320f - Filler,
            3 => 900f + 1000f - 2 * Filler,
            4 => 1000f - Filler,
            _ => 0f,
        };

        if (Left(SID.ReadyToReign) > 0)
            value += 800f + 900f + 1000f - 3 * Filler;
        if (Left(SID.ReadyToBreak) > 0)
            value += 900f - Filler;
        if (Left(SID.ReadyToRip) > 0)
            value += 240f;
        if (Left(SID.ReadyToTear) > 0)
            value += 280f;
        if (Left(SID.ReadyToGouge) > 0)
            value += 320f;
        if (Left(SID.ReadyToBlast) > 0)
            value += 200f;
        if (Left(SID.ReadyToRaze) > 0)
            value += 120f;
        return value;
    }
}
