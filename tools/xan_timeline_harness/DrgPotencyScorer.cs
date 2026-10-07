using System;
using BossMod;
using AID = BossMod.DRG.AID;

namespace XanTimelineHarness;

// Dragoon potencies as the 7.5 client describes them (Action sheet descriptions, gnum68 == 22, evaluated per level), and the
// expected-crit model the scorer uses. Positionals are assumed to land (the rotation spends True North for them).
internal static class DrgPotency
{
    // A mid-tier level-100 set: 25% critical hit rate, 160% critical damage. Only ratios matter: a normal hit is worth
    // 1 + p(M-1), a guaranteed critical M x (1 + b(M-1)) with b the critical-rate buffs (Life Surge's rule), both divided by the
    // unbuffed expectation so the numbers stay in potency.
    public const float CritRate = 0.25f;
    public const float CritDamage = 1.60f;
    public const float BaseExpectation = 1 + CritRate * (CritDamage - 1);

    public static float NormalHit(float critBuff) => (1 + (CritRate + critBuff) * (CritDamage - 1)) / BaseExpectation;
    public static float GuaranteedCrit(float critBuff) => CritDamage * (1 + critBuff * (CritDamage - 1)) / BaseExpectation;

    public const float DotTick = 3f;
    public const float DotDuration = 24f;

    // (base, combo) for the combo weaponskills; the positional variants are the combo ones plus 40 where the game grants it
    public static float Of(AID aid, int level, bool combo)
    {
        var l76 = level >= 76;
        var l94 = level >= 94;
        return aid switch
        {
            AID.TrueThrust => l76 ? 230 : 170,
            AID.RaidenThrust => l94 ? 320 : 280,
            AID.VorpalThrust => combo ? (l76 ? 280 : 250) : (l76 ? 130 : 100),
            AID.LanceBarrage => combo ? 340 : 130,
            AID.FullThrust => combo ? 380 : 100,
            AID.HeavensThrust => combo ? (l94 ? 460 : 400) : (l94 ? 160 : 100),
            AID.Disembowel => combo ? (l76 ? 250 : 210) : (l76 ? 140 : 100),
            AID.SpiralBlow => combo ? 300 : 140,
            // rear positional included
            AID.ChaosThrust => combo ? 260 : 140,
            AID.ChaoticSpring => combo ? (l94 ? 340 : 300) : (l94 ? 180 : 140),
            // flank / rear positional included
            AID.FangAndClaw or AID.WheelingThrust => combo ? (l94 ? 340 : 300) : (l94 ? 180 : 140),
            AID.Drakesbane => l94 ? 460 : level >= 86 ? 400 : 380,
            AID.PiercingTalon => l76 ? 200 : 150,
            AID.DoomSpike => 110,
            AID.SonicThrust => combo ? 120 : 100,
            AID.CoerthanTorment => combo ? 150 : 100,
            AID.DraconianFury => 130,
            AID.Jump => level >= 54 ? 320 : 250,
            AID.HighJump => 400,
            AID.MirageDive => 380,
            AID.Geirskogul => level >= 90 ? 280 : 200,
            AID.Nastrond => level >= 90 ? 720 : 600,
            AID.Stardiver => l94 ? 840 : 720,
            AID.Starcross => 1000,
            AID.DragonfireDive => 500,
            AID.RiseOfTheDragon => 550,
            AID.WyrmwindThrust => l94 ? 440 : 420,
            _ => 0
        };
    }

    public static float EnhancedTalon(int level) => level >= 76 ? 350 : 250;
    public static float DotTickPotency(AID aid) => aid == AID.ChaoticSpring ? 45 : 40;

    // falloff for the targets after the first (the tooltips: 50% less, 40% less for the Stardiver family); 1 = no falloff
    public static float Falloff(AID aid) => aid switch
    {
        AID.Geirskogul or AID.Nastrond or AID.WyrmwindThrust or AID.DragonfireDive or AID.RiseOfTheDragon => 0.5f,
        AID.Stardiver or AID.Starcross => 0.6f,
        _ => 1
    };

    public enum Shape { Single, Line10, Line15, TargetCircle5 }

    public static Shape ShapeOf(AID aid) => aid switch
    {
        AID.DoomSpike or AID.SonicThrust or AID.CoerthanTorment or AID.DraconianFury => Shape.Line10,
        AID.Geirskogul or AID.Nastrond or AID.WyrmwindThrust => Shape.Line15,
        AID.DragonfireDive or AID.Stardiver or AID.Starcross or AID.RiseOfTheDragon => Shape.TargetCircle5,
        _ => Shape.Single
    };
}
