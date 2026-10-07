namespace BossMod.Autorotation.Engine.Jobs;

// Gunbreaker for the rotation engine (any level; the weights were tuned at level 100): data only, no BossMod dependency (also compiled by the engine tools).
// Mechanics and potencies follow tools/xan_timeline_harness/GnbCombatState.cs + GnbPotencyScorer.cs (No Mercy x1.20; Sonic Break /
// Bow Shock DoTs credited in full at the press; any GCD drops pending Continuation procs). Cartridges cap at 3, or 6 under Bloodfest
// (gains past the cap are lost; a combo finisher gives one cartridge: below 3 always, at 3+ only under Bloodfest). The Gnashing Fang / Reign chains are statuses for their next step.
// Spec (agents_gnb.md): Bloodfest paired with No Mercy (right before it, or right after it in the same burst); Double Down inside No Mercy
// (no hard requirement: the search keeps every Double Down inside No Mercy by itself, and as a requirement it made a live search that
// ends early drift its No Mercy windows). Bloodfest recast is 60 s (the action sheet the harness
// reads, as agents_gnb.md says).
// Every GCD outside the 1-2-3 combo breaks it (as in the harness).
public static class GnbDefinition
{
    public const string Ammo = "Ammo";

    public const string NoMercy = "NoMercy", Bloodfest = "Bloodfest", ReadyToBreak = "ReadyToBreak", ReadyToReign = "ReadyToReign",
        SavageReady = "SavageClawReady", TalonReady = "WickedTalonReady", NobleReady = "NobleBloodReady", LionReady = "LionHeartReady",
        ReadyToRip = "ReadyToRip", ReadyToTear = "ReadyToTear", ReadyToGouge = "ReadyToGouge", ReadyToBlast = "ReadyToBlast", ReadyToRaze = "ReadyToRaze";

    public const string NoMercyCD = "NoMercyCD", BloodfestCD = "BloodfestCD", GnashingFangCD = "GnashingFangCD", DoubleDownCD = "DoubleDownCD",
        SonicBreakCD = "SonicBreakCD", ZoneCD = "ZoneCD", BowShockCD = "BowShockCD";

    public const uint AidKeenEdge = 16137, AidBrutalShell = 16139, AidSolidBarrel = 16145, AidBurstStrike = 16162, AidDemonSlice = 16141,
        AidDemonSlaughter = 16149, AidFatedCircle = 16163, AidGnashingFang = 16146, AidSavageClaw = 16147, AidWickedTalon = 16150,
        AidDoubleDown = 25760, AidSonicBreak = 16153, AidReignOfBeasts = 36937, AidNobleBlood = 36938, AidLionHeart = 36939,
        AidNoMercy = 16138, AidBloodfest = 16164, AidBlastingZone = 16165, AidDangerZone = 16144, AidBowShock = 16159, AidJugularRip = 16156, AidAbdomenTear = 16157,
        AidEyeGouge = 16158, AidHypervelocity = 25759, AidFatedBrand = 36936;

    private static readonly string[] Continuations = [ReadyToRip, ReadyToTear, ReadyToGouge, ReadyToBlast, ReadyToRaze];

    // level: the player's (synced) level. Skills not unlocked there are left out; potencies follow GnbPotencyScorer.cs (Melee Mastery 84,
    // Enhanced Brutal Shell 52), the cartridge cap Cartridge Charge II (3 from 88, 2 before; doubled under Bloodfest). Danger Zone before
    // Blasting Zone (80); the Continuation procs from 70 (Hypervelocity 86, Fated Brand 96), Ready to Break from 54, Ready to Reign at 100.
    public static JobDefinition Build(float gcd = 2.5f, int level = 100)
    {
        var mastery = level >= 84;
        var maxAmmo = level >= 88 ? 3 : 2;
        var continuation = level >= 70;
        var b = new JobBuilder("GNB", gcd)
            .Gauge(Ammo, maxAmmo * 2)
            .Status(NoMercy, 20, damageMultiplier: 1.20f).Status(Bloodfest, 30).Status(ReadyToBreak, 30).Status(ReadyToReign, 30)
            .Status(SavageReady, 30).Status(TalonReady, 30).Status(NobleReady, 30).Status(LionReady, 30)
            .Status(ReadyToRip, 10).Status(ReadyToTear, 10).Status(ReadyToGouge, 10).Status(ReadyToBlast, 10).Status(ReadyToRaze, 10)
            .Cooldown(NoMercyCD, 60).Cooldown(BloodfestCD, 60).Cooldown(GnashingFangCD, 30, 2).Cooldown(DoubleDownCD, 60)
            .Cooldown(SonicBreakCD, 60).Cooldown(ZoneCD, 30).Cooldown(BowShockCD, 60);

        // ---- 1-2-3 combo ----
        Gcd(b.Gcd("KeenEdge", mastery ? 300 : 200, AidKeenEdge).StartsCombo());
        if (level >= 4)
            Gcd(b.Gcd("BrutalShell", 160, AidBrutalShell).ComboFrom("KeenEdge", mastery ? 380 : level >= 52 ? 300 : 240));
        if (level >= 26)
            Gcd(b.Gcd("SolidBarrel", 140, AidSolidBarrel).ComboFrom("BrutalShell", mastery ? 460 : 360).EndsCombo()
                .IfCombo("BrutalShell").IfGaugeAtMost(Ammo, maxAmmo - 1).GainGauge(Ammo, 1).IfCombo("BrutalShell").IfStatus(Bloodfest).IfGaugeAtLeast(Ammo, maxAmmo).GainGauge(Ammo, 1));
        if (level >= 10)
            Gcd(b.Gcd("DemonSlice", 100, AidDemonSlice).AoeFalloff(100).StartsCombo());
        if (level >= 40)
            Gcd(b.Gcd("DemonSlaughter", 100, AidDemonSlaughter).AoeFalloff(100).ComboFrom("DemonSlice", 160).EndsCombo()
                .IfCombo("DemonSlice").IfGaugeAtMost(Ammo, maxAmmo - 1).GainGauge(Ammo, 1).IfCombo("DemonSlice").IfStatus(Bloodfest).IfGaugeAtLeast(Ammo, maxAmmo).GainGauge(Ammo, 1));

        // ---- cartridge GCDs ----
        if (level >= 30)
        {
            var burst = Gcd(b.Gcd("BurstStrike", mastery ? 420 : 340, AidBurstStrike).SpendGauge(Ammo, 1));
            if (level >= 86)
                burst.ApplyStatus(ReadyToBlast, 10);
        }
        if (level >= 72)
        {
            var fated = Gcd(b.Gcd("FatedCircle", 300, AidFatedCircle).AoeFalloff(300).SpendGauge(Ammo, 1));
            if (level >= 96)
                fated.ApplyStatus(ReadyToRaze, 10);
        }
        if (level >= 60)
        {
            var fang = Gcd(b.Gcd("GnashingFang", mastery ? 440 : 330, AidGnashingFang).UsesCooldown(GnashingFangCD).SpendGauge(Ammo, 1)
                .ForbidStatuses(SavageReady, TalonReady, NobleReady, LionReady)).ApplyStatus(SavageReady, 30);
            var claw = Gcd(b.Gcd("SavageClaw", mastery ? 500 : 410, AidSavageClaw).RequiresStatus(SavageReady).RemoveStatus(SavageReady)).ApplyStatus(TalonReady, 30);
            var talon = Gcd(b.Gcd("WickedTalon", mastery ? 560 : 490, AidWickedTalon).RequiresStatus(TalonReady).RemoveStatus(TalonReady));
            if (continuation)
            {
                fang.ApplyStatus(ReadyToRip, 10);
                claw.ApplyStatus(ReadyToTear, 10);
                talon.ApplyStatus(ReadyToGouge, 10);
            }
        }
        if (level >= 90)
            Gcd(b.Gcd("DoubleDown", 1000, AidDoubleDown).AoeFalloff(1000 * 0.85f).UsesCooldown(DoubleDownCD).SpendGauge(Ammo, 2));
        if (level >= 54)
            Gcd(b.Gcd("SonicBreak", 940, AidSonicBreak).RequiresStatus(ReadyToBreak).RemoveStatus(ReadyToBreak));
        if (level >= 100)
        {
            Gcd(b.Gcd("ReignOfBeasts", 800, AidReignOfBeasts).RequiresStatus(ReadyToReign).RemoveStatus(ReadyToReign)).ApplyStatus(NobleReady, 30);
            Gcd(b.Gcd("NobleBlood", 900, AidNobleBlood).RequiresStatus(NobleReady).RemoveStatus(NobleReady)).ApplyStatus(LionReady, 30);
            Gcd(b.Gcd("LionHeart", 1000, AidLionHeart).RequiresStatus(LionReady).RemoveStatus(LionReady));
        }

        // ---- abilities ----
        var noMercy = b.Ogcd("NoMercy", 0, NoMercyCD, AidNoMercy).NeedsUptime(2).ApplyStatus(NoMercy, 20);
        if (level >= 54)
            noMercy.ApplyStatus(ReadyToBreak, 30);
        if (level >= 76)
        {
            var bloodfest = b.Ogcd("Bloodfest", 0, BloodfestCD, AidBloodfest).NeedsUptime(2).RequiresCooldownAtMost(NoMercyCD, 2.5f)
                .ApplyStatus(Bloodfest, 30).GainGauge(Ammo, maxAmmo);
            // the same press paired the other way round: right after No Mercy, in its first weave window (otherwise a No Mercy pressed first would
            // lock Bloodfest out until the next one)
            var after = b.Ogcd("BloodfestAfter", 0, BloodfestCD, AidBloodfest).NeedsUptime(2).RequiresStatusLeft(NoMercy, 17)
                .ApplyStatus(Bloodfest, 30).GainGauge(Ammo, maxAmmo);
            if (level >= 100)
            {
                bloodfest.ApplyStatus(ReadyToReign, 30);
                after.ApplyStatus(ReadyToReign, 30);
            }
        }
        // Danger Zone before Blasting Zone (80)
        if (level >= 80)
            b.Ogcd("BlastingZone", 800, ZoneCD, AidBlastingZone);
        else if (level >= 18)
            b.Ogcd("BlastingZone", 250, ZoneCD, AidDangerZone);
        if (level >= 62)
            b.Ogcd("BowShock", 450, BowShockCD, AidBowShock).AoeFalloff(450);
        if (continuation)
        {
            b.Ogcd("JugularRip", mastery ? 220 : 180, null, AidJugularRip).RequiresStatus(ReadyToRip).RemoveStatus(ReadyToRip);
            b.Ogcd("AbdomenTear", mastery ? 260 : 220, null, AidAbdomenTear).RequiresStatus(ReadyToTear).RemoveStatus(ReadyToTear);
            b.Ogcd("EyeGouge", mastery ? 300 : 260, null, AidEyeGouge).RequiresStatus(ReadyToGouge).RemoveStatus(ReadyToGouge);
        }
        if (level >= 86)
            b.Ogcd("Hypervelocity", mastery ? 180 : 140, null, AidHypervelocity).RequiresStatus(ReadyToBlast).RemoveStatus(ReadyToBlast);
        if (level >= 96)
            b.Ogcd("FatedBrand", 120, null, AidFatedBrand).AoeFalloff(120).RequiresStatus(ReadyToRaze).RemoveStatus(ReadyToRaze);
        return b.Build();
    }

    // every GCD drops the Continuation procs still pending (the harness counts them as overwritten)
    private static JobBuilder.SkillBuilder Gcd(JobBuilder.SkillBuilder s) => s.RemoveStatuses(Continuations);

    public static EngineWeights DefaultWeights() => EngineWeights.Parse(DefaultWeightsJson);

    // CMA-ES on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-GNB-v4.json), BudgetMs for live play
    public const string DefaultWeightsJson = """
    {
      "OverCap": 2.0351698, "Combo": 3, "LambdaScale": 0.9404562, "TargetPull": 0, "SwitchMargin": 0, "FillerScale": 0.6423435, "BurstBias": 0,
      "StatusRemainder": 1.1710489, "CycleScale": 1, "CooldownLambdaScale": 0, "ForecastSelfBuffs": -1, "UnlockScale": 3,
      "StatusValue": {}, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8
    }
    """;
}
