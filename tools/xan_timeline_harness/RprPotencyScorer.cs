using System;
using System.Linq;
using BossMod;
using BossMod.Autorotation.xan;
using AID = BossMod.RPR.AID;
using SID = BossMod.RPR.SID;

namespace XanTimelineHarness;

// Offline potency estimate for one executed action, evaluated against the world state *before* the action is applied.
// Positionals are assumed to hit; cone/line angles are approximated by range. Consistent across builds, so deltas are meaningful.
internal static class RprPotencyScorer
{
    private const uint MedicatedStatus = 49;
    private const float MedicatedMultiplier = 1.06f;
    private const float BattleLitanyMultiplier = 1.05f;
    private const float BrotherhoodMultiplier = 1.05f;

    public static float Estimate(WorldState world, Actor player, AID action, Actor? target)
    {
        var level = player.Level;
        var m1 = level >= 74;
        var m2 = level >= 84;
        var m3 = level >= 94;
        var combo = (AID)world.Client.ComboState.Action;
        var enhancedGibbet = Has(player, SID.EnhancedGibbet, player);
        var enhancedGallows = Has(player, SID.EnhancedGallows, player);
        var enhancedVoid = Has(player, SID.EnhancedVoidReaping, player);
        var enhancedCross = Has(player, SID.EnhancedCrossReaping, player);
        var sacrificeStacks = player.FindStatus((uint)SID.ImmortalSacrifice, player.InstanceID)?.Extra & 0xFF ?? 0;

        float primary;
        var falloff = 1f;
        var shape = Shape.Single;
        switch (action)
        {
            case AID.Slice: primary = RprPotency.Slice(m1, m2, m3); break;
            case AID.WaxingSlice: primary = combo == AID.Slice ? RprPotency.WaxingSliceCombo(m1, m2, m3) : RprPotency.WaxingSliceUncomboed; break;
            case AID.InfernalSlice: primary = combo == AID.WaxingSlice ? RprPotency.InfernalSliceCombo(m1, m2, m3) : RprPotency.InfernalSliceUncomboed; break;
            case AID.SpinningScythe: primary = RprPotency.SpinningScythe(m1); shape = Shape.SelfCircle; break;
            case AID.NightmareScythe: primary = combo == AID.SpinningScythe ? RprPotency.NightmareScytheCombo(m1) : RprPotency.NightmareScytheUncomboedPerTarget; shape = Shape.SelfCircle; break;
            case AID.ShadowofDeath: primary = RprPotency.ShadowOfDeath; break;
            case AID.WhorlofDeath: primary = RprPotency.WhorlOfDeathPerTarget; shape = Shape.SelfCircle; break;
            case AID.SoulSlice: primary = RprPotency.SoulSlice(m3); break;
            case AID.SoulScythe: primary = RprPotency.SoulScythePerTarget; shape = Shape.SelfCircle; break;
            case AID.Harpe: primary = RprPotency.Harpe; break;
            case AID.HarvestMoon: primary = RprPotency.HarvestMoon(m3); falloff = RprPotency.HarvestMoonFalloff; shape = Shape.Splash; break;
            case AID.BloodStalk: primary = RprPotency.BloodStalk; break;
            case AID.UnveiledGibbet or AID.UnveiledGallows: primary = RprPotency.UnveiledGibbet(m3); break;
            case AID.GrimSwathe: primary = RprPotency.GrimSwathePerTarget; shape = Shape.Cone; break;
            case AID.Gluttony: primary = RprPotency.Gluttony; falloff = RprPotency.GluttonyFalloff; shape = Shape.Splash; break;
            case AID.Gibbet: primary = RprPotency.Gibbet + (enhancedGibbet ? RprPotency.EnhancedReaverBonus : 0); break;
            case AID.Gallows: primary = RprPotency.Gibbet + (enhancedGallows ? RprPotency.EnhancedReaverBonus : 0); break;
            case AID.Guillotine: primary = RprPotency.GuillotinePerTarget; shape = Shape.Cone; break;
            case AID.ExecutionersGibbet: primary = RprPotency.ExecutionersGibbet + (enhancedGibbet ? RprPotency.EnhancedReaverBonus : 0); break;
            case AID.ExecutionersGallows: primary = RprPotency.ExecutionersGibbet + (enhancedGallows ? RprPotency.EnhancedReaverBonus : 0); break;
            case AID.ExecutionersGuillotine: primary = RprPotency.ExecutionersGuillotinePerTarget; shape = Shape.Cone; break;
            case AID.VoidReaping: primary = RprPotency.VoidReaping(m3) + (enhancedVoid ? RprPotency.EnhancedReapingBonus : 0); break;
            case AID.CrossReaping: primary = RprPotency.VoidReaping(m3) + (enhancedCross ? RprPotency.EnhancedReapingBonus : 0); break;
            case AID.GrimReaping: primary = RprPotency.GrimReapingPerTarget; shape = Shape.Cone; break;
            case AID.LemuresSlice: primary = RprPotency.LemuresSlice; break;
            case AID.LemuresScythe: primary = RprPotency.LemuresScythePerTarget; shape = Shape.Cone; break;
            case AID.Communio: primary = RprPotency.Communio; falloff = RprPotency.CommunioFalloff; shape = Shape.Splash; break;
            case AID.Sacrificium: primary = RprPotency.Sacrificium; falloff = RprPotency.SacrificiumFalloff; shape = Shape.Splash; break;
            case AID.Perfectio: primary = RprPotency.Perfectio; falloff = RprPotency.PerfectioFalloff; shape = Shape.Splash; break;
            case AID.PlentifulHarvest: primary = RprPotency.PlentifulHarvest(sacrificeStacks); falloff = RprPotency.PlentifulHarvestFalloff; shape = Shape.Line; break;
            default: return 0;
        }

        var global = 1f;
        if (Has(player, SID.ArcaneCircle, player))
            global *= RprPotency.ArcaneCircleMultiplier;
        if (player.FindStatus(MedicatedStatus) != null)
            global *= MedicatedMultiplier;
        if (player.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null)
            global *= BattleLitanyMultiplier;
        if (player.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null)
            global *= BrotherhoodMultiplier;

        var total = 0f;
        var hit = 0;
        var anchor = shape == Shape.Splash && target != null ? target : player;
        var radius = shape switch { Shape.SelfCircle => 5f, Shape.Cone => 8f, Shape.Line => 15f, Shape.Splash => 5f, _ => 0f };
        if (shape == Shape.Single)
        {
            if (target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true })
                total += primary * (Has(target, SID.DeathsDesign, player) ? RprPotency.DeathsDesignMultiplier : 1f);
            return total * global;
        }

        // Main target first so falloff applies to the others.
        if (target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true })
        {
            total += primary * (Has(target, SID.DeathsDesign, player) ? RprPotency.DeathsDesignMultiplier : 1f);
            ++hit;
        }
        foreach (var enemy in world.Actors)
        {
            if (enemy == target || enemy.Type != ActorType.Enemy || enemy.IsDead || !enemy.IsTargetable)
                continue;
            if (anchor.DistanceToHitbox(enemy) > radius)
                continue;
            var multiplier = hit == 0 ? 1f : falloff;
            total += primary * multiplier * (Has(enemy, SID.DeathsDesign, player) ? RprPotency.DeathsDesignMultiplier : 1f);
            ++hit;
        }
        return total * global;
    }

    private static bool Has(Actor actor, SID status, Actor source) => actor.FindStatus((uint)status, source.InstanceID) != null;

    private enum Shape { Single, SelfCircle, Cone, Line, Splash }
}

// Value of resources still held when a run ends, so a scenario that ends right after a target-loss hold is not
// penalised for saving gauge the way a truncated fight would be. Coefficients follow the rotation's own marginal
// resource model at level 100 (a spent resource displaces one filler GCD).
internal static class RprTerminalValue
{
    private const float GCD = 2.5f;
    private const float Filler = (420f + 500f + 600f) / 3f;
    private const float EnshroudFillerGCDs = (1.5f * 4 + 2.5f) / GCD;
    public static readonly float ShroudPoint = (RprPotency.EnshroudSequenceValue - EnshroudFillerGCDs * Filler - EnshroudFillerGCDs * 10f * (440f + 620f - Filler) / 60f) / (50f + EnshroudFillerGCDs * 100f / 60f);
    public static readonly float SoulPoint = (440f + 620f + 10f * ShroudPoint - Filler) / 60f;
    public static readonly float ReaverStack = 620f + 10f * ShroudPoint - Filler;
    public static readonly float ExecutionerStack = 820f + 10f * ShroudPoint - Filler;
    public static readonly float SoulSliceCharge = 520f + 50f * SoulPoint - Filler;
    public static readonly float GluttonyReady = 560f + 2f * ExecutionerStack - 50f * SoulPoint;
    public static readonly float FreeEnshroud = RprPotency.EnshroudSequenceValue - EnshroudFillerGCDs * Filler;

    public static float Estimate(WorldState world, Actor player, RprCombatState combat)
    {
        float Left(SID status) => MathF.Max(0, (float)((player.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);
        int Stacks(SID status) => player.FindStatus((uint)status, player.InstanceID)?.Extra & 0xFF ?? 0;
        float ReadyIn(AID aid) => ActionDefinitions.Instance.Spell(aid)!.ReadyIn(world.Client.Cooldowns, world.Client.DutyActions);

        var value = combat.Soul * SoulPoint + combat.Shroud * ShroudPoint;
        if (Left(SID.Executioner) > 0)
            value += Stacks(SID.Executioner) * ExecutionerStack;
        else if (Left(SID.SoulReaver) > 0)
            value += Stacks(SID.SoulReaver) * ReaverStack;
        if (combat.Lemure > 0)
            value += RprPotency.Communio - Filler + (combat.Lemure - 1) * (RprPotency.VoidReaping(player.Level >= 94) + RprPotency.EnhancedReapingBonus - Filler * 1.5f / GCD)
                + (Left(SID.Oblatio) > 0 ? RprPotency.Sacrificium : 0f) + combat.Void / 2 * RprPotency.LemuresSlice;
        if (Left(SID.IdealHost) > 0)
            value += FreeEnshroud;
        if (Left(SID.PerfectioParata) > 0)
            value += RprPotency.Perfectio - Filler;
        if (Left(SID.ImmortalSacrifice) > 0)
            value += RprPotency.PlentifulHarvest(Stacks(SID.ImmortalSacrifice)) - Filler + FreeEnshroud;
        var soulSlice = ActionDefinitions.Instance.Spell(AID.SoulSlice)!;
        var charges = soulSlice.MaxChargesAtLevel(player.Level);
        var capIn = soulSlice.ChargeCapIn(world.Client.Cooldowns, world.Client.DutyActions, player.Level);
        var readyCharges = Math.Clamp(charges - (int)MathF.Ceiling(MathF.Max(0, capIn) / soulSlice.Cooldown - 0.0001f), 0, charges);
        value += readyCharges * SoulSliceCharge;
        if (ReadyIn(AID.Gluttony) <= 0 && combat.Soul >= 50 && combat.Lemure == 0)
            value += GluttonyReady - 50f * SoulPoint;
        var target = world.Actors.FirstOrDefault(actor => actor.Type == ActorType.Enemy && !actor.IsDead && actor.IsTargetable);
        if (target != null)
        {
            var ddLeft = MathF.Max(0, (float)((target.FindStatus((uint)SID.DeathsDesign, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);
            value += MathF.Min(30f, ddLeft) / GCD * Filler * (RprPotency.DeathsDesignMultiplier - 1f);
        }
        return value;
    }
}
