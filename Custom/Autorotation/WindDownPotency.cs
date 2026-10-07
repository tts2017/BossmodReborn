namespace BossMod.Autorotation;

// Potencies that rank wind-down candidates before a long target loss (spec rule 2b). Direct is the max-level client tooltip value after
// Label, checked by the harness command `potency-audit` (tools/xan_timeline_harness/PotencyAudit.cs); Total is what the ranking uses (DoT
// ticks, the follow-up oGCD the action unlocks and sequence bonuses included, from the audited component values in the comments). Filler is
// the average max-level potency of the job's normal three-step combo, the baseline a candidate has to beat.
public static class WindDownPotency
{
    public readonly record struct Entry(uint Action, float Direct, float Total, string Label = "potency of");

    public const float FillerNIN = 370, FillerVPR = 370, FillerMCH = 320, FillerGNB = 380, FillerPLD = 340;

    public static readonly Entry[] NIN =
    [
        new((uint)BossMod.NIN.AID.Raiton, 740, 740),
        new((uint)BossMod.NIN.AID.HyoshoRanryu, 1300, 1300),
        new((uint)BossMod.NIN.AID.FleetingRaiju, 700, 700),
        new((uint)BossMod.NIN.AID.PhantomKamaitachi, 700, 700),
    ];

    public static readonly Entry[] VPR =
    [
        new((uint)BossMod.VPR.AID.Vicewinder, 540, 540),
        new((uint)BossMod.VPR.AID.HuntersCoil, 630, 800),       // + Twinfang Bite 170 (venom)
        new((uint)BossMod.VPR.AID.SwiftskinsCoil, 630, 800),    // + Twinblood Bite 170 (venom)
        new((uint)BossMod.VPR.AID.UncoiledFury, 680, 1020),     // + Uncoiled Twinfang 170 + Uncoiled Twinblood 170 (poised)
        new((uint)BossMod.VPR.AID.Reawaken, 750, 750),
        new((uint)BossMod.VPR.AID.FirstGeneration, 480, 1000),  // 680 in sequence + First Legacy 320; Second-Fourth identical
        new((uint)BossMod.VPR.AID.Ouroboros, 1150, 1150),
    ];

    public static readonly Entry[] MCH =
    [
        new((uint)BossMod.MCH.AID.Drill, 660, 660),
        new((uint)BossMod.MCH.AID.AirAnchor, 660, 660),
        new((uint)BossMod.MCH.AID.ChainSaw, 660, 660),
        new((uint)BossMod.MCH.AID.Excavator, 660, 660),
        new((uint)BossMod.MCH.AID.FullMetalField, 900, 900),
        new((uint)BossMod.MCH.AID.BlazingShot, 240, 240),
        new((uint)BossMod.MCH.AID.DoubleCheck, 180, 180),
        new((uint)BossMod.MCH.AID.Checkmate, 180, 180),
    ];

    public static readonly Entry[] GNB =
    [
        new((uint)BossMod.GNB.AID.GnashingFang, 440, 660),      // + Jugular Rip 220
        new((uint)BossMod.GNB.AID.SavageClaw, 500, 760),        // + Abdomen Tear 260
        new((uint)BossMod.GNB.AID.WickedTalon, 560, 860),       // + Eye Gouge 300
        new((uint)BossMod.GNB.AID.DoubleDown, 1000, 1000),      // 2 cartridges
        new((uint)BossMod.GNB.AID.SonicBreak, 340, 940),        // + DoT 120 x 5 ticks (15 s)
        new((uint)BossMod.GNB.AID.BurstStrike, 420, 600),       // + Hypervelocity 180
        new((uint)BossMod.GNB.AID.BlastingZone, 800, 800),
        new((uint)BossMod.GNB.AID.BowShock, 150, 450),          // + DoT 60 x 5 ticks (15 s)
    ];

    public static readonly Entry[] PLD =
    [
        new((uint)BossMod.PLD.AID.GoringBlade, 700, 700),
        new((uint)BossMod.PLD.AID.HolySpirit, 500, 500, "Divine Might Potency:"),
        new((uint)BossMod.PLD.AID.Atonement, 460, 460),
        new((uint)BossMod.PLD.AID.Supplication, 500, 500),
        new((uint)BossMod.PLD.AID.Sepulchre, 540, 540),
        new((uint)BossMod.PLD.AID.Confiteor, 1000, 1000, "Requiescat Potency:"),
        new((uint)BossMod.PLD.AID.BladeOfFaith, 760, 760, "Requiescat Potency:"),
        new((uint)BossMod.PLD.AID.BladeOfTruth, 880, 880, "Requiescat Potency:"),
        new((uint)BossMod.PLD.AID.BladeOfValor, 1000, 1000, "Requiescat Potency:"),
        new((uint)BossMod.PLD.AID.CircleOfScorn, 140, 290),     // + DoT 30 x 5 ticks (15 s)
        new((uint)BossMod.PLD.AID.Expiacion, 450, 450),
        new((uint)BossMod.PLD.AID.BladeOfHonor, 1000, 1000),
    ];

    public static float Of(Entry[] table, uint action)
    {
        foreach (var e in table)
            if (e.Action == action)
                return e.Total;
        return 0;
    }
}
