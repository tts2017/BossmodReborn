namespace BossMod.Autorotation.Engine.Jobs;

// Viper for the rotation engine (any level; the weights were tuned at level 100): data only, no BossMod dependency (also compiled by the engine tools).
// Mechanics and potencies follow tools/xan_timeline_harness/VprCombatState.cs + VprPotencyScorer.cs (positionals credited; Hunter's
// Instinct x1.10, Swiftscaled -15% on every weaponskill recast; the honed / venom bonuses as conditional potencies; the Reawaken sequence
// as a locked chain through the Anguine Tribute gauge with the Generations at their in-sequence potency; falloff 0.25).
// The follow-up abilities are modelled as the game's gauge does it: Serpent's Tail (Death Rattle / Last Lash / the Legacies) as the Tail
// gauge, the twin windows (Twinfang / Twinblood Bite after a coil, Thresh after a den, Uncoiled after Uncoiled Fury) as the TwinWindow
// gauge plus two ready statuses; any weaponskill drops whatever is pending (as the harness counts them lost). The dread chains
// (Vicewinder -> Hunter's Coil + Swiftskin's Coil, Vicepit -> the dens) are "ok" statuses, one per coil, both granted by the opener
// and each used up by its coil; the basic combos, Reawaken and Uncoiled Fury wait until a chain is finished (the guides never
// interrupt one). The basic combos run through the Step gauge (a second step combos from either first step, the finishers only as combo).
// Writhing Snap and Slither are pressed by the module and are not in the definition.
public static class VprDefinition
{
    // gauges
    public const string Offering = "Offering", Coil = "Coil", Anguine = "Anguine";
    public const string Venom = "Venom";           // the finisher venom held: 1 Flankstung, 2 Flanksbane, 3 Hindstung, 4 Hindsbane (they never stack)
    public const string GrimVenom = "GrimVenom";   // 1 Grimhunter's, 2 Grimskin's
    public const string Tail = "Tail";             // Serpent's Tail pending: 1 Death Rattle, 2 Last Lash, 3 a Legacy
    public const string TwinWindow = "TwinWindow"; // 1 coil (after Hunter's), 2 coil (after Swiftskin's), 3 den (Hunter's), 4 den (Swiftskin's), 5 Uncoiled Fury
    public const string Step = "Step";             // the combo step: 1 Steel / Reaving Fangs, 2 Hunter's Sting, 3 Swiftskin's Sting; 4 Steel / Reaving Maw, 5 Hunter's Bite, 6 Swiftskin's Bite

    // statuses
    public const string HuntersInstinct = "HuntersInstinct", Swiftscaled = "Swiftscaled", HonedSteel = "HonedSteel", HonedReavers = "HonedReavers";
    public const string HuntersVenom = "HuntersVenom", SwiftskinsVenom = "SwiftskinsVenom", FellhuntersVenom = "FellhuntersVenom", FellskinsVenom = "FellskinsVenom";
    public const string PoisedForTwinfang = "PoisedForTwinfang", PoisedForTwinblood = "PoisedForTwinblood";
    public const string ReawakenReady = "ReawakenReady", Reawakened = "Reawakened";
    public const string TwinfangReady = "TwinfangReady", TwinbloodReady = "TwinbloodReady";
    public const string HunterCoilOk = "HunterCoilOk", SwiftCoilOk = "SwiftCoilOk", HunterDenOk = "HunterDenOk", SwiftDenOk = "SwiftDenOk";

    // cooldown groups
    public const string ViceCD = "VicewinderCD", IreCD = "SerpentsIreCD";

    // game action ids (BossMod.VPR.AID)
    public const uint AidSteelFangs = 34606, AidHuntersSting = 34608, AidReavingFangs = 34607, AidSwiftskinsSting = 34609, AidSteelMaw = 34614,
        AidFlankstingStrike = 34610, AidFlanksbaneFang = 34611, AidHindstingStrike = 34612, AidHindsbaneFang = 34613, AidReavingMaw = 34615,
        AidHuntersBite = 34616, AidSwiftskinsBite = 34617, AidJaggedMaw = 34618, AidBloodiedMaw = 34619, AidDeathRattle = 34634, AidLastLash = 34635,
        AidVicewinder = 34620, AidHuntersCoil = 34621, AidSwiftskinsCoil = 34622, AidVicepit = 34623, AidSwiftskinsDen = 34625, AidHuntersDen = 34624,
        AidTwinfangBite = 34636, AidTwinbloodBite = 34637, AidTwinfangThresh = 34638, AidTwinbloodThresh = 34639, AidUncoiledFury = 34633,
        AidSerpentsIre = 34647, AidThirdGeneration = 34629, AidFourthGeneration = 34630, AidSecondGeneration = 34628, AidFirstGeneration = 34627,
        AidReawaken = 34626, AidUncoiledTwinfang = 34644, AidUncoiledTwinblood = 34645, AidOuroboros = 34631, AidFourthLegacy = 34643,
        AidSecondLegacy = 34641, AidFirstLegacy = 34640, AidThirdLegacy = 34642;

    private static readonly string[] CoilChain = [HunterCoilOk, SwiftCoilOk, HunterDenOk, SwiftDenOk];

    // the Anguine Tribute of a Reawaken: 5 with Ouroboros (96), 4 before
    public static int AnguineMax(int level) => level >= 96 ? 5 : 4;

    // level: the player's (synced) level. Skills not unlocked there are left out; potencies follow VprPotencyScorer.cs (Melee Mastery 74 / 84),
    // Rattling Coils 2 from 82 and 3 from 88, the Offering from 90, the twin venoms from 75 (coils) / 80 (dens), Death Rattle 55, Last
    // Lash 60, the Legacies at 100, Uncoiled Fury's twins from 92, Ouroboros 96. gcd: the weaponskill recast without Swiftscaled (the
    // definition applies the haste); the longer recasts (coils 3.0 s, Uncoiled Fury 3.5 s, Reawaken 2.2 s, Generations 2.0 s, Ouroboros 3.0 s)
    // scale with it.
    public static JobDefinition Build(float gcd = 2.5f, int level = 100)
    {
        var l74 = level >= 74;
        var l84 = level >= 84;
        var coilMax = level >= 88 ? 3 : level >= 82 ? 2 : 0;
        var anguineMax = AnguineMax(level);
        float Recast(float baseSeconds) => baseSeconds * gcd / 2.5f;
        var b = new JobBuilder("VPR", gcd)
            .Gauge(Offering, 100).Gauge(Coil, coilMax > 0 ? coilMax : 1).Gauge(Anguine, 5)
            .Gauge(Venom, 4, flat: true).Gauge(GrimVenom, 2, flat: true).Gauge(Tail, 3, flat: true).Gauge(TwinWindow, 5, flat: true).Gauge(Step, 6, flat: true)
            .Status(HuntersInstinct, 40, damageMultiplier: 1.10f)
            .Status(Swiftscaled, 40, gcdRecastMultiplier: 0.85f)
            .Status(HonedSteel, 60).Status(HonedReavers, 60)
            .Status(HuntersVenom, 30).Status(SwiftskinsVenom, 30).Status(FellhuntersVenom, 30).Status(FellskinsVenom, 30)
            .Status(PoisedForTwinfang, 60).Status(PoisedForTwinblood, 60)
            .Status(ReawakenReady, 30).Status(Reawakened, 30)
            .Status(TwinfangReady, 30).Status(TwinbloodReady, 30)
            .Status(HunterCoilOk, 60).Status(SwiftCoilOk, 60).Status(HunterDenOk, 60).Status(SwiftDenOk, 60)
            .Cooldown(ViceCD, 40, 2).Cooldown(IreCD, 120);
        // the dual-wield combo's average potency per GCD with its Death Rattle (the free-filler analysis cannot run the finishers, which need
        // the Step gauge): honed fangs, sting, venomed finisher, Death Rattle (55) over three GCDs
        b.FillerPotency(((l74 ? 200 : 180) + (level >= 10 ? 100 : 0) + (l74 ? 300 : 280) + (l84 ? 400 : 380) + (level >= 30 ? 100 : 0) + (level >= 55 ? 280 : 0)) / 3f);

        // ---- dual-wield combo: Steel / Reaving Fangs (honed by the other one) -> Sting (Hunter's Instinct / Swiftscaled) -> finisher (venom, positional) ----
        // the steps run through the Step gauge (1 after either fangs, 2 after Hunter's Sting, 3 after Swiftskin's Sting; the AoE combo 4 / 5 / 6),
        // so a second step combos from either first step and the finishers are only legal as combo (the game refuses them otherwise)
        var fangs = l74 ? 200 : 180;
        Fangs(b, "SteelFangs", fangs, AidSteelFangs, HonedSteel, HonedReavers, level >= 10, 1);
        if (level >= 10)
            Fangs(b, "ReavingFangs", fangs, AidReavingFangs, HonedReavers, HonedSteel, true, 1);
        if (level >= 5)
            Sting(b, "HuntersSting", l74 ? 300 : 280, AidHuntersSting, HuntersInstinct, "SteelFangs", "ReavingFangs", 1, 2, level);
        if (level >= 20)
            Sting(b, "SwiftskinsSting", l74 ? 300 : 280, AidSwiftskinsSting, Swiftscaled, "SteelFangs", "ReavingFangs", 1, 3, level);
        if (level >= 30)
        {
            var fin = l84 ? 400 : 380;
            Finisher(b, "FlankstingStrike", fin, AidFlankstingStrike, "HuntersSting", 2, 1, 3, level);
            Finisher(b, "FlanksbaneFang", fin, AidFlanksbaneFang, "HuntersSting", 2, 2, 4, level);
            Finisher(b, "HindstingStrike", fin, AidHindstingStrike, "SwiftskinsSting", 3, 3, 2, level);
            Finisher(b, "HindsbaneFang", fin, AidHindsbaneFang, "SwiftskinsSting", 3, 4, 1, level);
        }

        // ---- AoE combo (5y circle around the player) ----
        if (level >= 25)
            Fangs(b, "SteelMaw", 120, AidSteelMaw, HonedSteel, HonedReavers, true, 4, aoe: true);
        if (level >= 35)
            Fangs(b, "ReavingMaw", 120, AidReavingMaw, HonedReavers, HonedSteel, true, 4, aoe: true);
        if (level >= 40)
            Sting(b, "HuntersBite", 180, AidHuntersBite, HuntersInstinct, "SteelMaw", "ReavingMaw", 4, 5, level, aoe: true);
        if (level >= 45)
            Sting(b, "SwiftskinsBite", 180, AidSwiftskinsBite, Swiftscaled, "SteelMaw", "ReavingMaw", 4, 6, level, aoe: true);
        if (level >= 50)
        {
            // Jagged Maw (Grimhunter's Venom) / Bloodied Maw (Grimskin's Venom) from either bite
            AoeFinisher(b, "JaggedMaw", AidJaggedMaw, 1, 2, level);
            AoeFinisher(b, "BloodiedMaw", AidBloodiedMaw, 2, 1, level);
        }

        // ---- Vicewinder / Vicepit chains (3.0 s coils; the twins from 75 / 80; 5 Offering from 90) ----
        if (level >= 65)
        {
            var vice = Ws(b.Gcd("Vicewinder", 540, AidVicewinder).UsesCooldown(ViceCD).ComboNeutral().ForbidStatus(Reawakened).ForbidStatuses(CoilChain)
                .ApplyStatus(HunterCoilOk, 60).ApplyStatus(SwiftCoilOk, 60));
            if (coilMax > 0)
                vice.GainGauge(Coil, 1);
            Coil_(b, "HuntersCoil", 680, AidHuntersCoil, HunterCoilOk, HuntersInstinct, HuntersVenom, 1, level >= 75, level, Recast(3.0f));
            Coil_(b, "SwiftskinsCoil", 680, AidSwiftskinsCoil, SwiftCoilOk, Swiftscaled, SwiftskinsVenom, 2, level >= 75, level, Recast(3.0f));
        }
        if (level >= 70)
        {
            var pit = Ws(b.Gcd("Vicepit", 250, AidVicepit).AoeFalloff(250).UsesCooldown(ViceCD).ComboNeutral().ForbidStatus(Reawakened).ForbidStatuses(CoilChain)
                .ApplyStatus(HunterDenOk, 60).ApplyStatus(SwiftDenOk, 60));
            if (coilMax > 0)
                pit.GainGauge(Coil, 1);
            Coil_(b, "HuntersDen", 300, AidHuntersDen, HunterDenOk, HuntersInstinct, FellhuntersVenom, 3, level >= 80, level, Recast(3.0f), aoe: true);
            Coil_(b, "SwiftskinsDen", 300, AidSwiftskinsDen, SwiftDenOk, Swiftscaled, FellskinsVenom, 4, level >= 80, level, Recast(3.0f), aoe: true);
        }

        // ---- Uncoiled Fury (ranged, 3.5 s; its twins from 92) ----
        if (level >= 82)
        {
            var fury = Ws(b.Gcd("UncoiledFury", 680, AidUncoiledFury).AoeFalloff(170).Recast(Recast(3.5f)).ComboNeutral().ForbidStatuses(CoilChain).ForbidStatus(Reawakened).SpendGauge(Coil, 1));
            if (level >= 92)
                fury.ApplyStatus(PoisedForTwinfang, 60).SetGauge(TwinWindow, 5).ApplyStatus(TwinfangReady, 30).ApplyStatus(TwinbloodReady, 30);
        }

        // ---- Reawaken: Offering 50 or Ready to Reawaken (Serpent's Ire); Generations 1-4 (Legacies at 100), Ouroboros (96) ----
        if (level >= 90)
        {
            Ws(b.Gcd("Reawaken", 750, AidReawaken).AoeFalloff(750 * 0.25f).Recast(Recast(2.2f)).ComboNeutral().ForbidStatuses(CoilChain).ForbidStatus(Reawakened)
                .ForbidStatus(ReawakenReady).SpendGauge(Offering, 50).SetGauge(Anguine, anguineMax).ApplyStatus(Reawakened, 30));
            Ws(b.Gcd("ReawakenReady", 750, AidReawaken).AoeFalloff(750 * 0.25f).Recast(Recast(2.2f)).ComboNeutral().ForbidStatuses(CoilChain).ForbidStatus(Reawakened)
                .RequiresStatus(ReawakenReady).RemoveStatus(ReawakenReady).SetGauge(Anguine, anguineMax).ApplyStatus(Reawakened, 30));
            Generation(b, "FirstGeneration", AidFirstGeneration, anguineMax, level, Recast(2.0f));
            Generation(b, "SecondGeneration", AidSecondGeneration, anguineMax - 1, level, Recast(2.0f));
            Generation(b, "ThirdGeneration", AidThirdGeneration, anguineMax - 2, level, Recast(2.0f));
            var fourth = Generation(b, "FourthGeneration", AidFourthGeneration, anguineMax - 3, level, Recast(2.0f));
            if (level < 96)
                fourth.RemoveStatus(Reawakened);
            else
                Ws(b.Gcd("Ouroboros", 1150, AidOuroboros).AoeFalloff(1150 * 0.25f).Recast(Recast(3.0f)).ComboNeutral().RequiresStatus(Reawakened)
                    .RequiresGauge(Anguine, 1).RequiresGaugeAtMost(Anguine, 1).SetGauge(Anguine, 0).RemoveStatus(Reawakened));
        }

        // ---- abilities: the follow-ups first (weaves of one window are searched in definition order), then Serpent's Ire ----
        if (level >= 55)
            b.Ogcd("DeathRattle", 280, actionId: AidDeathRattle).RequiresGauge(Tail, 1).RequiresGaugeAtMost(Tail, 1).SetGauge(Tail, 0);
        if (level >= 60)
            b.Ogcd("LastLash", 120, actionId: AidLastLash).AoeFalloff(120).RequiresGauge(Tail, 2).RequiresGaugeAtMost(Tail, 2).SetGauge(Tail, 0);
        if (level >= 100)
            b.Ogcd("Legacy", 320, actionId: AidFirstLegacy).AoeFalloff(80).RequiresGauge(Tail, 3).SetGauge(Tail, 0);
        if (level >= 75)
        {
            // the twins in the order the window's venom makes them worth: after Hunter's Coil the twinfang (Hunter's Venom) buffs the twinblood,
            // after Swiftskin's Coil the twinblood (Swiftskin's Venom) buffs the twinfang. A weave window is searched in definition order, so
            // each window has its own pair (the module presses the same two actions)
            Twins(b, 1, "TwinfangBite", AidTwinfangBite, TwinfangReady, HuntersVenom, "TwinbloodBite", AidTwinbloodBite, TwinbloodReady, SwiftskinsVenom, 120, 50);
            Twins(b, 2, "TwinbloodBiteSwift", AidTwinbloodBite, TwinbloodReady, SwiftskinsVenom, "TwinfangBiteSwift", AidTwinfangBite, TwinfangReady, HuntersVenom, 120, 50);
        }
        if (level >= 80)
        {
            Twins(b, 3, "TwinfangThresh", AidTwinfangThresh, TwinfangReady, FellhuntersVenom, "TwinbloodThresh", AidTwinbloodThresh, TwinbloodReady, FellskinsVenom, 50, 30, aoe: true);
            Twins(b, 4, "TwinbloodThreshSwift", AidTwinbloodThresh, TwinbloodReady, FellskinsVenom, "TwinfangThreshSwift", AidTwinfangThresh, TwinfangReady, FellhuntersVenom, 50, 30, aoe: true);
        }
        if (level >= 92)
        {
            b.Ogcd("UncoiledTwinfang", 120, actionId: AidUncoiledTwinfang).AoeFalloff(30).RequiresStatus(TwinfangReady).RequiresGauge(TwinWindow, 5)
                .PotencyIfStatus(PoisedForTwinfang, 170).RemoveStatus(PoisedForTwinfang).RemoveStatus(TwinfangReady).ApplyStatus(PoisedForTwinblood, 60);
            b.Ogcd("UncoiledTwinblood", 120, actionId: AidUncoiledTwinblood).AoeFalloff(30).RequiresStatus(TwinbloodReady).RequiresGauge(TwinWindow, 5)
                .PotencyIfStatus(PoisedForTwinblood, 170).RemoveStatus(PoisedForTwinblood).RemoveStatus(TwinbloodReady);
        }
        if (level >= 86)
        {
            var ire = b.Ogcd("SerpentsIre", 0, IreCD, AidSerpentsIre).NoTarget().NeedsUptime(2);
            if (coilMax > 0)
                ire.GainGauge(Coil, 1);
            if (level >= 90)
                ire.ApplyStatus(ReawakenReady, 30);
        }
        return b.Build();
    }

    // a weaponskill: drops the pending Serpent's Tail and twin follow-ups (the harness counts them lost) and the combo step
    private static JobBuilder.SkillBuilder Ws(JobBuilder.SkillBuilder s) => s.Weaponskill().SetGauge(Tail, 0).SetGauge(TwinWindow, 0).SetGauge(Step, 0).RemoveStatus(TwinfangReady).RemoveStatus(TwinbloodReady);

    // a first step: +100 (+20 for the maws) with its honed status, which it uses up, granting the other one (from 10)
    private static void Fangs(JobBuilder b, string name, float potency, uint aid, string honed, string grants, bool grant, int step, bool aoe = false)
    {
        // (the step is set after Ws, which clears it)
        var s = Ws(b.Gcd(name, potency, aid).PotencyIfStatus(honed, potency + (aoe ? 20 : 100)).StartsCombo().ForbidStatus(Reawakened).ForbidStatuses(CoilChain)
            .RemoveStatus(honed)).SetGauge(Step, step);
        if (aoe)
            s.AoeFalloff(potency);
        if (grant)
            s.ApplyStatus(grants, 60);
    }

    // a second step from either first step: refreshes its buff for 40 s
    private static void Sting(JobBuilder b, string name, float potency, uint aid, string buff, string from1, string from2, int fromStep, int step, int level, bool aoe = false)
    {
        var s = Ws(b.Gcd(name, potency, aid).RequiresGauge(Step, fromStep).RequiresGaugeAtMost(Step, fromStep).ComboFrom(from1, potency).ComboFrom(from2, potency)
            .ForbidStatus(Reawakened).ForbidStatuses(CoilChain).ApplyStatus(buff, 40)).SetGauge(Step, step);
        if (aoe)
            s.AoeFalloff(potency);
    }

    // a dual-wield finisher: +100 with its venom (the Venom gauge == venom), grants the next venom, Death Rattle (55), 10 Offering (90)
    private static void Finisher(JobBuilder b, string name, float potency, uint aid, string from, int fromStep, int venom, int grants, int level)
    {
        var s = Ws(b.Gcd(name, potency, aid).PotencyIfGauge(Venom, venom + 1, potency).PotencyIfGauge(Venom, venom, potency + 100).RequiresGauge(Step, fromStep).RequiresGaugeAtMost(Step, fromStep)
            .ComboFrom(from, potency).EndsCombo().ForbidStatus(Reawakened).ForbidStatuses(CoilChain).SetGauge(Venom, grants));
        if (level >= 55)
            s.SetGauge(Tail, 1);
        if (level >= 90)
            s.GainGauge(Offering, 10);
    }

    // an AoE finisher from either bite (Step 5 / 6): +40 with its grim venom, Last Lash (60), 10 Offering (90)
    private static void AoeFinisher(JobBuilder b, string name, uint aid, int venom, int grants, int level)
    {
        var s = Ws(b.Gcd(name, 180, aid).AoeFalloff(180).PotencyIfGauge(GrimVenom, venom + 1, 180).PotencyIfGauge(GrimVenom, venom, 220).RequiresGauge(Step, 5).RequiresGaugeAtMost(Step, 6)
            .ComboFrom("HuntersBite", 180).ComboFrom("SwiftskinsBite", 180).EndsCombo().ForbidStatus(Reawakened).ForbidStatuses(CoilChain).SetGauge(GrimVenom, grants));
        if (level >= 60)
            s.SetGauge(Tail, 2);
        if (level >= 90)
            s.GainGauge(Offering, 10);
    }

    // the two twin abilities of one window: the first is buffed by the window's venom (which it uses up) and grants the other venom for the
    // second; both are only legal in that window (TwinWindow) while their ready status lasts
    private static void Twins(JobBuilder b, int window, string first, uint firstAid, string firstReady, string firstVenom, string second, uint secondAid, string secondReady, string secondVenom, float potency, float bonus, bool aoe = false)
    {
        var f = b.Ogcd(first, potency, actionId: firstAid).RequiresStatus(firstReady).RequiresGauge(TwinWindow, window).RequiresGaugeAtMost(TwinWindow, window)
            .PotencyIfStatus(firstVenom, potency + bonus).RemoveStatus(firstVenom).RemoveStatus(firstReady).ApplyStatus(secondVenom, 30);
        var s = b.Ogcd(second, potency, actionId: secondAid).RequiresStatus(secondReady).RequiresGauge(TwinWindow, window).RequiresGaugeAtMost(TwinWindow, window)
            .PotencyIfStatus(secondVenom, potency + bonus).RemoveStatus(secondVenom).RemoveStatus(secondReady);
        if (aoe)
        {
            f.AoeFalloff(potency);
            s.AoeFalloff(potency);
        }
    }

    // a coil / den: needs its "ok" from the opener (or the other coil), refreshes its buff, opens the twin window with its venom (from
    // `twins`), 5 Offering from 90
    private static void Coil_(JobBuilder b, string name, float potency, uint aid, string ok, string buff, string venom, int window, bool twins, int level, float recast, bool aoe = false)
    {
        var s = Ws(b.Gcd(name, potency, aid).Recast(recast).ComboNeutral().ForbidStatus(Reawakened).RequiresStatus(ok).RemoveStatus(ok).ApplyStatus(buff, 40));
        if (aoe)
            s.AoeFalloff(potency);
        if (twins)
            s.ApplyStatus(venom, 30).SetGauge(TwinWindow, window).ApplyStatus(TwinfangReady, 30).ApplyStatus(TwinbloodReady, 30);
        if (level >= 90)
            s.GainGauge(Offering, 5);
    }

    // a Generation step: in sequence (the only weaponskill allowed between the steps is Uncoiled Fury, which does not break it), a Legacy at 100
    private static JobBuilder.SkillBuilder Generation(JobBuilder b, string name, uint aid, int anguine, int level, float recast)
    {
        var s = Ws(b.Gcd(name, 680, aid).AoeFalloff(170).Recast(recast).ComboNeutral().RequiresStatus(Reawakened).RequiresGauge(Anguine, anguine).RequiresGaugeAtMost(Anguine, anguine)
            .GainGauge(Anguine, -1));
        if (level >= 100)
            s.SetGauge(Tail, 3);
        return s;
    }

    public static EngineWeights DefaultWeights(int level = 100) => EngineWeights.Parse(DefaultWeightsJson);

    // CMA-ES on the xan timeline harness (9 fights with --party-buffs 7.8, deterministic search; tools/blm_engine_eval tuned/weights-VPR-v1.json,
    // from the NIN Lv100 set: 698,114 -> 707,192), BudgetMs for live play. MinNodes / SliceNodes: every search completes and frame slices
    // are counted in nodes, so live play does not depend on the machine's timing (as MNK / DRG)
    public const string DefaultWeightsJson = """
    {
      "OverCap": 0.79599893, "Combo": 0.007616225, "LambdaScale": 0.69946855, "TargetPull": 0, "SwitchMargin": 0, "FillerScale": 1.5,
      "BurstBias": 0.6045252, "StatusRemainder": 0.5499885, "CycleScale": 1, "CooldownLambdaScale": 0.05082979, "ForecastSelfBuffs": 1, "UnlockScale": 1.8880919,
      "StatusValue": { "Swiftscaled": 39.253796 }, "CooldownValue": { "SerpentsIreCD": 0 }, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8,
      "MinNodes": 2000,
      "SliceNodes": 40
    }
    """;
}
