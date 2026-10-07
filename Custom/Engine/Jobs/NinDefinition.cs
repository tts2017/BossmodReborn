namespace BossMod.Autorotation.Engine.Jobs;

// Ninja for the rotation engine (any level; the weights were tuned at level 100): data only, no BossMod dependency (also compiled by tools/nin_engine_eval).
// Potencies and rules follow tools/xan_timeline_harness/NinCombatState.cs and NINBurstPlanner.NinPotency (level >= 94), with
// positionals credited (the harness scores them unconditionally; the module uses True North in the game).
// A ninjutsu is one engine skill covering its whole mudra sequence: its recast is the sequence's total GCD time (0.5 s per mudra +
// 1.5 s ninjutsu), its lock leaves the weave slot after the ninjutsu. NinEngineModule presses the mudras one by one.
// Ten Chi Jin is the press (TenChiJin) plus one engine skill for its three steps (TCJCombo).
// Burst rules from the user's behaviour spec (tools/nin_regression HardFail rules): Ten Chi Jin and Meisui only inside our Dokumori window
// (Meisui not while Ten Chi Jin is about to be ready), Kassatsu's ninjutsu before Raiju / Phantom Kamaitachi, an odd Kunai's Bane only when
// it will be back for the next Dokumori.
public static class NinDefinition
{
    // gauges
    public const string Ninki = "Ninki";
    public const string Kazematoi = "Kazematoi";
    public const string Raiju = "Raiju"; // Raiju Ready stacks (a gauge: every melee weaponskill clears it)

    // statuses
    public const string Kassatsu = "Kassatsu";
    public const string ShadowWalker = "ShadowWalker";
    public const string Meisui = "Meisui";
    public const string Higi = "Higi";
    public const string TenriReady = "TenriJindoReady";
    public const string Bunshin = "Bunshin";
    public const string PhantomReady = "PhantomKamaitachiReady";
    public const string TenChiJin = "TenChiJin";
    public const string KunaisBane = "KunaisBane";   // our debuff on the target (+10%)
    public const string Dokumori = "Dokumori";       // our debuff on the target (+5%)

    // cooldown groups
    public const string MudraCD = "MudraCD";
    public const string KassatsuCD = "KassatsuCD";
    public const string TenChiJinCD = "TenChiJinCD";
    public const string DokumoriCD = "DokumoriCD";
    public const string KunaisBaneCD = "KunaisBaneCD";
    public const string DreamCD = "DreamCD";
    public const string MeisuiCD = "MeisuiCD";
    public const string BunshinCD = "BunshinCD";

    public const float MudraStep = 0.5f, NinjutsuGcd = 1.5f, NinjutsuLock = 0.6f;

    // game action ids (BossMod.NIN.AID)
    public const uint AidSpinningEdge = 2240, AidGustSlash = 2242, AidAeolianEdge = 2255, AidArmorCrush = 3563, AidDeathBlossom = 2254,
        AidHakke = 16488, AidPhantomKamaitachi = 25774, AidForkedRaiju = 25777, AidFleetingRaiju = 25778,
        AidFuma = 2265, AidKaton = 2266, AidRaiton = 2267, AidSuiton = 2271, AidHyosho = 16492, AidGoka = 16491,
        AidTen1 = 2259, AidChi1 = 2261, AidJin1 = 2263, AidTen2 = 18805, AidChi2 = 18806, AidJin2 = 18807,
        AidFumaTen = 18873, AidFumaChi = 18874, AidTCJKaton = 18876, AidTCJRaiton = 18877, AidTCJSuiton = 18881,
        AidKassatsu = 2264, AidTenChiJin = 7403, AidDokumori = 36957, AidKunaisBane = 36958, AidTrickAttack = 2258, AidDream = 3566, AidMeisui = 16489,
        AidBhavacakra = 7402, AidZesho = 36960, AidHellfrog = 7401, AidDeathfrog = 36959, AidBunshin = 16493, AidTenriJindo = 36961;

    // level: the player's (synced) level. Skills not unlocked there are left out; potencies follow NINBurstPlanner.NinPotency (l74 / l84 /
    // l94 branches), Ninki from weaponskills Shukiho (62 / 78 / 84), Kazematoi from Armor Crush (54). Kunai's Bane (92) is Trick Attack
    // before it (same KunaisBane status: the 10% window); below Hyosho Ranryu / Goka Mekkyaku (76) Kassatsu boosts a Raiton / Katon
    // (KassatsuRaiton / KassatsuKaton). Raiju from 90, Meisui's Bhavacakra bonus from 88, Higi from 96.
    public static JobDefinition Build(float gcd = 2.12f, int level = 100)
    {
        var l74 = level >= 74;
        var l84 = level >= 84;
        var l94 = level >= 94;
        var raiju = level >= 90;
        var shukiho = level >= 62 ? 5 : 0;
        var finisherNinki = level >= 84 ? 15 : level >= 78 ? 10 : level >= 62 ? 5 : 0;
        var b = new JobBuilder("NIN", gcd)
            .Gauge(Ninki, 100).Gauge(Kazematoi, 5).Gauge(Raiju, 3)
            .Status(Kassatsu, 15).Status(ShadowWalker, 20).Status(Meisui, 30).Status(Higi, 30).Status(TenriReady, 30)
            .Status(Bunshin, 30, maxStacks: 5).Status(PhantomReady, 45).Status(TenChiJin, 6)
            // the debuff windows run from the press until the server re-application expires (harness: 15 s + 1.29 s, 20 s + 1.07 s)
            .Status(KunaisBane, 16.29f, damageMultiplier: 1.10f).Status(Dokumori, 21.07f, damageMultiplier: 1.05f)
            .Shadow(Bunshin, 160, 80, Ninki, 5)
            .Cooldown(MudraCD, 20, 2).Cooldown(KassatsuCD, 60).Cooldown(TenChiJinCD, 120).Cooldown(DokumoriCD, 120)
            .Cooldown(KunaisBaneCD, 60).Cooldown(DreamCD, 60).Cooldown(MeisuiCD, 120).Cooldown(BunshinCD, 90);

        // ---- weaponskills (each clears Raiju Ready) ----
        b.Gcd("SpinningEdge", l94 ? 300 : l84 ? 220 : 180, AidSpinningEdge).Weaponskill().StartsCombo().GainGauge(Ninki, shukiho).SetGauge(Raiju, 0);
        if (level >= 4)
            b.Gcd("GustSlash", l94 ? 240 : l84 ? 160 : l74 ? 120 : 100, AidGustSlash).Weaponskill().ComboFrom("SpinningEdge", l94 ? 400 : l84 ? 320 : l74 ? 280 : 260)
                .IfCombo("SpinningEdge").GainGauge(Ninki, shukiho).SetGauge(Raiju, 0);
        if (level >= 54)
            b.Gcd("AeolianEdge", l94 ? 380 : l74 ? 300 : 260, AidAeolianEdge).Weaponskill().ComboFrom("GustSlash", l94 ? 560 : l74 ? 480 : 440).EndsCombo().RequiresGauge(Kazematoi, 1)
                .IfCombo("GustSlash").GainGauge(Ninki, finisherNinki).GainGauge(Kazematoi, -1).SetGauge(Raiju, 0);
        if (level >= 26)
            b.Gcd("AeolianEdgeBare", l94 ? 280 : l74 ? 200 : 160, AidAeolianEdge).Weaponskill().ComboFrom("GustSlash", l94 ? 460 : l74 ? 380 : 340).EndsCombo().RequiresGaugeAtMost(Kazematoi, 0)
                .IfCombo("GustSlash").GainGauge(Ninki, finisherNinki).SetGauge(Raiju, 0);
        if (level >= 54)
            b.Gcd("ArmorCrush", l94 ? 300 : l74 ? 200 : 160, AidArmorCrush).Weaponskill().ComboFrom("GustSlash", l94 ? 500 : l74 ? 400 : 360).EndsCombo()
                .IfCombo("GustSlash").GainGauge(Ninki, finisherNinki).IfCombo("GustSlash").GainGauge(Kazematoi, 2).SetGauge(Raiju, 0);
        if (level >= 38)
            b.Gcd("DeathBlossom", 100, AidDeathBlossom).Weaponskill().AoeFalloff(100).StartsCombo().GainGauge(Ninki, shukiho).SetGauge(Raiju, 0);
        if (level >= 52)
            b.Gcd("HakkeMujinsatsu", 100, AidHakke).Weaponskill().AoeFalloff(100).ComboFrom("DeathBlossom", 120).EndsCombo()
                .IfCombo("DeathBlossom").GainGauge(Ninki, shukiho).SetGauge(Raiju, 0);
        if (raiju)
            b.Gcd("ForkedRaiju", l94 ? 700 : 560, AidForkedRaiju).Weaponskill().ComboNeutral().ForbidStatus(Kassatsu).SpendGauge(Raiju, 1).GainGauge(Ninki, 5);
        if (level >= 82)
            b.Gcd("PhantomKamaitachi", 700, AidPhantomKamaitachi).ComboNeutral().ForbidStatus(Kassatsu).RequiresStatus(PhantomReady).RemoveStatus(PhantomReady).GainGauge(Ninki, 10);

        // ---- ninjutsu (whole mudra sequences) ----
        var raiton = l94 ? 740 : 650;
        if (level >= 35)
        {
            var r = Ninjutsu(b.Gcd("Raiton", raiton, AidRaiton), 2).UsesCooldown(MudraCD).ForbidStatus(Kassatsu);
            if (raiju)
                r.GainGauge(Raiju, 1);
        }
        if (level >= 45)
            Ninjutsu(b.Gcd("Suiton", l94 ? 580 : 500, AidSuiton), 3).UsesCooldown(MudraCD).ForbidStatus(Kassatsu).ApplyStatus(ShadowWalker, 20);
        if (level >= 35)
            Ninjutsu(b.Gcd("Katon", 350, AidKaton).AoeFalloff(350), 2).UsesCooldown(MudraCD).ForbidStatus(Kassatsu);
        if (level >= 76)
        {
            Ninjutsu(b.Gcd("HyoshoRanryu", 1300 * 1.3f, AidHyosho), 2).RequiresStatus(Kassatsu).RemoveStatus(Kassatsu);
            Ninjutsu(b.Gcd("GokaMekkyaku", 850 * 1.3f, AidGoka).AoeFalloff(850 * 1.3f), 2).RequiresStatus(Kassatsu).RemoveStatus(Kassatsu);
        }
        else if (level >= 50)
        {
            Ninjutsu(b.Gcd("KassatsuRaiton", 650 * 1.3f, AidRaiton), 2).RequiresStatus(Kassatsu).RemoveStatus(Kassatsu);
            Ninjutsu(b.Gcd("KassatsuKaton", 350 * 1.3f, AidKaton).AoeFalloff(350 * 1.3f), 2).RequiresStatus(Kassatsu).RemoveStatus(Kassatsu);
        }
        // Ten Chi Jin: Fuma (1.0 s) -> Raiton (1.0 s) -> Suiton (1.5 s)
        if (level >= 70)
        {
            var tcj = b.Gcd("TCJCombo", (l94 ? 500 : 450) + raiton + (l94 ? 580 : 500), AidFumaTen).ComboNeutral().Recast(1.0f + 1.0f + 1.5f).Lock(1.0f + 1.0f + NinjutsuLock)
                .RequiresStatus(TenChiJin).RemoveStatus(TenChiJin);
            if (raiju)
                tcj.GainGauge(Raiju, 1);
            tcj.ApplyStatus(ShadowWalker, 20);
            b.LockSkillsDuring(TenChiJin, "TCJCombo");
        }

        // ---- abilities ----
        if (level >= 50)
            b.Ogcd("Kassatsu", 0, KassatsuCD, AidKassatsu).ForbidStatus(Kassatsu).ApplyStatus(Kassatsu, 15);
        if (level >= 70)
        {
            var tenChiJin = b.Ogcd("TenChiJin", 0, TenChiJinCD, AidTenChiJin).ForbidStatus(Kassatsu).RequiresStatusLeft(Dokumori, 2.5f).ApplyStatus(TenChiJin, 6);
            if (level >= 100)
                tenChiJin.ApplyStatus(TenriReady, 30);
        }
        if (level >= 66)
        {
            var dokumori = b.Ogcd("Dokumori", 400, DokumoriCD, AidDokumori).GainGauge(Ninki, 40);
            if (level >= 96)
                dokumori.ApplyStatus(Higi, 30);
            dokumori.ApplyStatus(Dokumori, 21.07f);
        }
        // Trick Attack before Kunai's Bane (92)
        var kunai = level >= 92 ? 700 : 400;
        var kunaiAid = level >= 92 ? AidKunaisBane : AidTrickAttack;
        if (level >= 18)
        {
            b.Ogcd("KunaisBane", kunai, KunaisBaneCD, kunaiAid).RequiresStatus(Dokumori).RequiresStatus(ShadowWalker).RemoveStatus(ShadowWalker).ApplyStatus(KunaisBane, 16.29f);
            // an odd-minute Kunai's Bane only when it will be back for the next Dokumori
            b.Ogcd("KunaisBaneOdd", kunai, KunaisBaneCD, kunaiAid).ForbidStatus(Dokumori).RequiresCooldownAtLeast(DokumoriCD, 55).RequiresStatus(ShadowWalker).RemoveStatus(ShadowWalker).ApplyStatus(KunaisBane, 16.29f);
        }
        if (level >= 56)
            b.Ogcd("DreamWithinADream", 540, DreamCD, AidDream);
        if (level >= 72)
        {
            var meisui = b.Ogcd("Meisui", 0, MeisuiCD, AidMeisui).RequiresStatusLeft(Dokumori, 2.5f).RequiresCooldownAtLeast(TenChiJinCD, 10).RequiresStatus(ShadowWalker).RemoveStatus(ShadowWalker).GainGauge(Ninki, 50);
            if (level >= 88)
                meisui.ApplyStatus(Meisui, 30);
        }
        if (level >= 80)
        {
            var bunshin = b.Ogcd("Bunshin", 0, BunshinCD, AidBunshin).SpendGauge(Ninki, 50).ApplyStatus(Bunshin, 30, 5);
            if (level >= 82)
                bunshin.ApplyStatus(PhantomReady, 45);
        }
        if (level >= 68)
        {
            var bhava = b.Ogcd("Bhavacakra", l94 ? 400 : 350, null, AidBhavacakra).ForbidStatus(Higi).SpendGauge(Ninki, 50);
            if (level >= 88)
                bhava.PotencyIfStatus(Meisui, l94 ? 550 : 500);
            bhava.RemoveStatus(Meisui);
        }
        if (level >= 96)
            b.Ogcd("ZeshoMeppo", 700, null, AidZesho).RequiresStatus(Higi).SpendGauge(Ninki, 50).PotencyIfStatus(Meisui, 850).RemoveStatus(Higi).RemoveStatus(Meisui);
        if (level >= 62)
            b.Ogcd("HellfrogMedium", 250, null, AidHellfrog).AoeFalloff(250).ForbidStatus(Higi).SpendGauge(Ninki, 50);
        if (level >= 96)
            b.Ogcd("DeathfrogMedium", 400, null, AidDeathfrog).AoeFalloff(400).RequiresStatus(Higi).SpendGauge(Ninki, 50).RemoveStatus(Higi);
        if (level >= 100)
            b.Ogcd("TenriJindo", 1100, null, AidTenriJindo).RequiresStatus(TenriReady).RemoveStatus(TenriReady);
        return b.Build();
    }

    private static JobBuilder.SkillBuilder Ninjutsu(JobBuilder.SkillBuilder s, int mudras)
        => s.ComboNeutral().Recast(mudras * MudraStep + NinjutsuGcd).Lock(mudras * MudraStep + NinjutsuLock);

    // the mudra presses of a ninjutsu skill (first press, then follow-ups); under Kassatsu the first press uses the follow-up ids
    public static uint[] MudraSequence(string skill, bool kassatsu) => skill switch
    {
        "Raiton" => [AidTen1, AidChi2],
        "Suiton" => [AidTen1, AidChi2, AidJin2],
        "Katon" => [AidChi1, AidTen2],
        "HyoshoRanryu" => [kassatsu ? AidTen2 : AidTen1, AidJin2],
        "GokaMekkyaku" => [kassatsu ? AidChi2 : AidChi1, AidTen2],
        "KassatsuRaiton" => [kassatsu ? AidTen2 : AidTen1, AidChi2],
        "KassatsuKaton" => [kassatsu ? AidChi2 : AidChi1, AidTen2],
        _ => []
    };

    public static EngineWeights DefaultWeights() => EngineWeights.Parse(DefaultWeightsJson);

    // CMA-ES on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-NIN-v1.json), BudgetMs for live play
    public const string DefaultWeightsJson = """
    {
      "OverCap": 1.742, "Combo": 0.729, "LambdaScale": 0.107, "TargetPull": 0, "SwitchMargin": 0, "FillerScale": 1.021,
      "BurstBias": 0.114, "StatusRemainder": 1.255, "CycleScale": 1, "CooldownLambdaScale": 0.058, "ForecastSelfBuffs": 1, "UnlockScale": 1.643,
      "StatusValue": {}, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8
    }
    """;
}
