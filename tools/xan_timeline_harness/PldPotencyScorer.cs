using System;
using BossMod;
using AID = BossMod.PLD.AID;
using SID = BossMod.PLD.SID;

namespace XanTimelineHarness;

// Paladin potencies as the 7.5 client describes them (Action sheet descriptions, gnum68 == 19, evaluated per level; checked by
// PldPotencyAudit). Variants: the combo bonus, Divine Might (Holy Spirit / Holy Circle) and Requiescat (Holy Spirit / Holy Circle and the
// Confiteor chain). Divine Might is prioritised over Requiescat when both are up, which is the game rule and the emulator rule.
internal static class PldPotency
{
    public const float CircleOfScornDotTick = 30f;
    public const int CircleOfScornDotTicks = 5; // 15 s, one tick per 3 s
    // the tooltips: "60% less for all remaining enemies" on Confiteor, Blades, Blade of Honor, Imperator, Expiacion
    public const float SplashFalloff = 0.4f;
    // Fight or Flight: the tooltip says "Increases damage dealt by 25%" (checked by PldPotencyAudit)
    public const float FightOrFlightMultiplier = 1.25f;

    public static float Of(AID aid, int level, bool combo = false, bool divineMight = false, bool requiescat = false)
    {
        var l94 = level >= 94;
        var l84 = level >= 84;
        return aid switch
        {
            AID.FastBlade => l94 ? 220 : l84 ? 200 : 150,
            AID.RiotBlade => combo ? (l94 ? 330 : l84 ? 300 : 260) : (l94 ? 170 : l84 ? 140 : 100),
            AID.RageOfHalone => combo ? 330 : 100,
            AID.RoyalAuthority => combo ? (l94 ? 460 : l84 ? 400 : 360) : (l94 ? 200 : l84 ? 140 : 100),
            AID.TotalEclipse => 120,
            AID.Prominence => combo ? 220 : 100,
            AID.HolySpirit => divineMight ? (l94 ? 500 : l84 ? 450 : 400) : requiescat ? (l94 ? 700 : l84 ? 650 : 600) : (l94 ? 400 : l84 ? 350 : 300),
            AID.HolyCircle => divineMight ? 250 : requiescat ? 350 : 100,
            AID.Atonement => l94 ? 460 : l84 ? 400 : 360,
            AID.Supplication => l94 ? 500 : l84 ? 420 : 380,
            AID.Sepulchre => l94 ? 540 : l84 ? 440 : 400,
            AID.Confiteor => requiescat ? (l94 ? 1000 : 920) : (l94 ? 500 : 420),
            AID.BladeOfFaith => requiescat ? (l94 ? 760 : 720) : (l94 ? 260 : 220),
            AID.BladeOfTruth => requiescat ? (l94 ? 880 : 820) : (l94 ? 380 : 320),
            AID.BladeOfValor => requiescat ? (l94 ? 1000 : 920) : (l94 ? 500 : 420),
            AID.BladeOfHonor => 1000,
            AID.Imperator => 580,
            AID.Requiescat => 320,
            AID.GoringBlade => 700,
            AID.SpiritsWithin => 270,
            AID.Expiacion => 450,
            AID.CircleOfScorn => 140,
            AID.Intervene => 150,
            AID.ShieldLob => 100,
            _ => 0
        };
    }

    public enum Shape { Single, SelfCircle, TargetSplash }

    public static Shape ShapeOf(AID aid) => aid switch
    {
        AID.TotalEclipse or AID.Prominence or AID.HolyCircle or AID.CircleOfScorn => Shape.SelfCircle,
        AID.Confiteor or AID.BladeOfFaith or AID.BladeOfTruth or AID.BladeOfValor or AID.BladeOfHonor or AID.Imperator or AID.Expiacion => Shape.TargetSplash,
        _ => Shape.Single
    };
}

// Offline potency estimate for one executed Paladin action, evaluated against the world state before the action is applied (so the
// combo, Divine Might, Requiescat and Fight or Flight are the ones the action is about to use). A fixed yardstick like GnbPotencyScorer:
// a delta between two builds of AkechiPLD.cs is meaningful, the absolute number is not a DPS simulation. The damage over time of Circle
// of Scorn is credited whole at the cast (15 s of 30-potency ticks, snapshotting Fight or Flight, which is how the game treats it).
internal static class PldPotencyScorer
{
    private const uint MedicatedStatus = 49;
    private const float MedicatedMultiplier = 1.06f;
    private const float BattleLitanyMultiplier = 1.05f;
    private const float BrotherhoodMultiplier = 1.05f;

    private const float MeleeRadius = 5f;
    private const float SplashRadius = 5f;

    public static float Estimate(WorldState world, Actor player, AID action, Actor? target)
    {
        var level = player.Level;
        var combo = (AID)world.Client.ComboState.Action;
        var comboHit = action switch
        {
            AID.RiotBlade => combo == AID.FastBlade,
            AID.RageOfHalone or AID.RoyalAuthority => combo == AID.RiotBlade,
            AID.Prominence => combo == AID.TotalEclipse,
            _ => false
        };
        var primary = PldPotency.Of(action, level, comboHit, Has(player, SID.DivineMight), Has(player, SID.Requiescat));
        if (primary <= 0)
            return 0f;

        var global = 1f;
        if (Has(player, SID.FightOrFlight))
            global *= PldPotency.FightOrFlightMultiplier;
        if (player.FindStatus(MedicatedStatus) != null)
            global *= MedicatedMultiplier;
        if (player.FindStatus((uint)BossMod.DRG.SID.BattleLitany) != null)
            global *= BattleLitanyMultiplier;
        if (player.FindStatus((uint)BossMod.MNK.SID.Brotherhood) != null)
            global *= BrotherhoodMultiplier;

        var shape = PldPotency.ShapeOf(action);
        var live = target is { Type: ActorType.Enemy, IsDead: false, IsTargetable: true };
        if (shape == PldPotency.Shape.Single)
            return live ? primary * global : 0f;

        var dot = action == AID.CircleOfScorn ? PldPotency.CircleOfScornDotTick * PldPotency.CircleOfScornDotTicks : 0f;
        var splash = shape == PldPotency.Shape.TargetSplash;
        var anchor = splash && target != null ? target : player;
        var radius = splash ? SplashRadius : MeleeRadius;
        var total = 0f;
        var hit = 0;
        if (splash && live)
        {
            total += primary;
            ++hit;
        }
        foreach (var enemy in world.Actors)
        {
            if (splash && enemy == target || enemy.Type != ActorType.Enemy || enemy.IsDead || !enemy.IsTargetable)
                continue;
            if (anchor.DistanceToHitbox(enemy) > radius)
                continue;
            total += (primary + dot) * (splash && hit > 0 ? PldPotency.SplashFalloff : 1f);
            ++hit;
        }
        return total * global;
    }

    private static bool Has(Actor actor, SID status) => actor.FindStatus((uint)status, actor.InstanceID) != null;
}

// Value of resources still held when a run ends, so a scenario that stops right after a burst hold is not penalised for banking procs
// the way a truncated fight would be. Each pending proc is worth the difference between spending it and pressing the filler GCD it
// displaces (the three-hit combo averaged); a pending oGCD is worth its potency.
internal static class PldTerminalValue
{
    public static float Estimate(WorldState world, Actor player, PldCombatState combat)
    {
        var level = player.Level;
        float Left(SID status) => MathF.Max(0, (float)((player.FindStatus((uint)status, player.InstanceID)?.ExpireAt ?? world.CurrentTime) - world.CurrentTime).TotalSeconds);
        float P(AID aid, bool divineMight = false, bool requiescat = false) => PldPotency.Of(aid, level, false, divineMight, requiescat);
        var filler = (P(AID.FastBlade) + PldPotency.Of(AID.RiotBlade, level, true) + PldPotency.Of(AID.RoyalAuthority, level, true)) / 3f;

        var value = 0f;
        // the Confiteor chain: Confiteor, Faith, Truth, Valor in order, each spending a Requiescat stack; stacks left over are Holy Spirits
        AID[] chain = [AID.Confiteor, AID.BladeOfFaith, AID.BladeOfTruth, AID.BladeOfValor];
        var stacks = combat.RequiescatStacks;
        if (stacks > 0)
        {
            var first = combat.BladeStep > 0 ? combat.BladeStep : Left(SID.ConfiteorReady) > 0 ? 0 : 4;
            var last = level >= 90 ? 4 : 1;
            for (var i = first; i < last && stacks > 0; ++i, --stacks)
                value += P(chain[i], false, true) - filler;
            value += stacks * (P(AID.HolySpirit, false, true) - filler);
        }
        // Sword Oath: every status held is one press, and an Atonement still to press carries its Supplication and Sepulchre with it
        if (Left(SID.AtonementReady) > 0)
            value += P(AID.Atonement) + P(AID.Supplication) + P(AID.Sepulchre) - 3 * filler;
        if (Left(SID.SupplicationReady) > 0)
            value += P(AID.Supplication) + P(AID.Sepulchre) - 2 * filler;
        else if (Left(SID.SepulchreReady) > 0 && Left(SID.AtonementReady) <= 0)
            value += P(AID.Sepulchre) - filler;
        if (Left(SID.DivineMight) > 0)
            value += P(AID.HolySpirit, true) - filler;
        if (Left(SID.GoringBladeReady) > 0)
            value += P(AID.GoringBlade) - filler;
        if (Left(SID.BladeOfHonorReady) > 0)
            value += P(AID.BladeOfHonor);
        return value;
    }
}