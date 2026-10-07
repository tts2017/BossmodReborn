namespace BossMod.Autorotation.Engine.Jobs;

// Ninja (level 100) for the rotation engine: data only, no BossMod dependency (also compiled by tools/nin_engine_eval).
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
        AidKassatsu = 2264, AidTenChiJin = 7403, AidDokumori = 36957, AidKunaisBane = 36958, AidDream = 3566, AidMeisui = 16489,
        AidBhavacakra = 7402, AidZesho = 36960, AidHellfrog = 7401, AidDeathfrog = 36959, AidBunshin = 16493, AidTenriJindo = 36961;

    public static JobDefinition Build(float gcd = 2.12f)
    {
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
        b.Gcd("SpinningEdge", 300, AidSpinningEdge).Weaponskill().StartsCombo().GainGauge(Ninki, 5).SetGauge(Raiju, 0);
        b.Gcd("GustSlash", 240, AidGustSlash).Weaponskill().ComboFrom("SpinningEdge", 400).IfCombo("SpinningEdge").GainGauge(Ninki, 5).SetGauge(Raiju, 0);
        b.Gcd("AeolianEdge", 380, AidAeolianEdge).Weaponskill().ComboFrom("GustSlash", 560).EndsCombo().RequiresGauge(Kazematoi, 1)
            .IfCombo("GustSlash").GainGauge(Ninki, 15).GainGauge(Kazematoi, -1).SetGauge(Raiju, 0);
        b.Gcd("AeolianEdgeBare", 280, AidAeolianEdge).Weaponskill().ComboFrom("GustSlash", 460).EndsCombo().RequiresGaugeAtMost(Kazematoi, 0)
            .IfCombo("GustSlash").GainGauge(Ninki, 15).SetGauge(Raiju, 0);
        b.Gcd("ArmorCrush", 300, AidArmorCrush).Weaponskill().ComboFrom("GustSlash", 500).EndsCombo()
            .IfCombo("GustSlash").GainGauge(Ninki, 15).IfCombo("GustSlash").GainGauge(Kazematoi, 2).SetGauge(Raiju, 0);
        b.Gcd("DeathBlossom", 100, AidDeathBlossom).Weaponskill().AoeFalloff(100).StartsCombo().GainGauge(Ninki, 5).SetGauge(Raiju, 0);
        b.Gcd("HakkeMujinsatsu", 100, AidHakke).Weaponskill().AoeFalloff(100).ComboFrom("DeathBlossom", 120).EndsCombo()
            .IfCombo("DeathBlossom").GainGauge(Ninki, 5).SetGauge(Raiju, 0);
        b.Gcd("ForkedRaiju", 700, AidForkedRaiju).Weaponskill().ComboNeutral().ForbidStatus(Kassatsu).SpendGauge(Raiju, 1).GainGauge(Ninki, 5);
        b.Gcd("PhantomKamaitachi", 700, AidPhantomKamaitachi).ComboNeutral().ForbidStatus(Kassatsu).RequiresStatus(PhantomReady).RemoveStatus(PhantomReady).GainGauge(Ninki, 10);

        // ---- ninjutsu (whole mudra sequences) ----
        Ninjutsu(b.Gcd("Raiton", 740, AidRaiton), 2).UsesCooldown(MudraCD).ForbidStatus(Kassatsu).GainGauge(Raiju, 1);
        Ninjutsu(b.Gcd("Suiton", 580, AidSuiton), 3).UsesCooldown(MudraCD).ForbidStatus(Kassatsu).ApplyStatus(ShadowWalker, 20);
        Ninjutsu(b.Gcd("Katon", 350, AidKaton).AoeFalloff(350), 2).UsesCooldown(MudraCD).ForbidStatus(Kassatsu);
        Ninjutsu(b.Gcd("HyoshoRanryu", 1300 * 1.3f, AidHyosho), 2).RequiresStatus(Kassatsu).RemoveStatus(Kassatsu);
        Ninjutsu(b.Gcd("GokaMekkyaku", 850 * 1.3f, AidGoka).AoeFalloff(850 * 1.3f), 2).RequiresStatus(Kassatsu).RemoveStatus(Kassatsu);
        // Ten Chi Jin: Fuma (1.0 s) -> Raiton (1.0 s) -> Suiton (1.5 s)
        b.Gcd("TCJCombo", 500 + 740 + 580, AidFumaTen).ComboNeutral().Recast(1.0f + 1.0f + 1.5f).Lock(1.0f + 1.0f + NinjutsuLock)
            .RequiresStatus(TenChiJin).RemoveStatus(TenChiJin).GainGauge(Raiju, 1).ApplyStatus(ShadowWalker, 20);
        b.LockSkillsDuring(TenChiJin, "TCJCombo");

        // ---- abilities ----
        b.Ogcd("Kassatsu", 0, KassatsuCD, AidKassatsu).ForbidStatus(Kassatsu).ApplyStatus(Kassatsu, 15);
        b.Ogcd("TenChiJin", 0, TenChiJinCD, AidTenChiJin).ForbidStatus(Kassatsu).RequiresStatusLeft(Dokumori, 2.5f).ApplyStatus(TenChiJin, 6).ApplyStatus(TenriReady, 30);
        b.Ogcd("Dokumori", 400, DokumoriCD, AidDokumori).GainGauge(Ninki, 40).ApplyStatus(Higi, 30).ApplyStatus(Dokumori, 21.07f);
        b.Ogcd("KunaisBane", 700, KunaisBaneCD, AidKunaisBane).RequiresStatus(Dokumori).RequiresStatus(ShadowWalker).RemoveStatus(ShadowWalker).ApplyStatus(KunaisBane, 16.29f);
        // an odd-minute Kunai's Bane only when it will be back for the next Dokumori
        b.Ogcd("KunaisBaneOdd", 700, KunaisBaneCD, AidKunaisBane).ForbidStatus(Dokumori).RequiresCooldownAtLeast(DokumoriCD, 55).RequiresStatus(ShadowWalker).RemoveStatus(ShadowWalker).ApplyStatus(KunaisBane, 16.29f);
        b.Ogcd("DreamWithinADream", 540, DreamCD, AidDream);
        b.Ogcd("Meisui", 0, MeisuiCD, AidMeisui).RequiresStatusLeft(Dokumori, 2.5f).RequiresCooldownAtLeast(TenChiJinCD, 10).RequiresStatus(ShadowWalker).RemoveStatus(ShadowWalker).GainGauge(Ninki, 50).ApplyStatus(Meisui, 30);
        b.Ogcd("Bunshin", 0, BunshinCD, AidBunshin).SpendGauge(Ninki, 50).ApplyStatus(Bunshin, 30, 5).ApplyStatus(PhantomReady, 45);
        b.Ogcd("Bhavacakra", 400, null, AidBhavacakra).ForbidStatus(Higi).SpendGauge(Ninki, 50).PotencyIfStatus(Meisui, 550).RemoveStatus(Meisui);
        b.Ogcd("ZeshoMeppo", 700, null, AidZesho).RequiresStatus(Higi).SpendGauge(Ninki, 50).PotencyIfStatus(Meisui, 850).RemoveStatus(Higi).RemoveStatus(Meisui);
        b.Ogcd("HellfrogMedium", 250, null, AidHellfrog).AoeFalloff(250).ForbidStatus(Higi).SpendGauge(Ninki, 50);
        b.Ogcd("DeathfrogMedium", 400, null, AidDeathfrog).AoeFalloff(400).RequiresStatus(Higi).SpendGauge(Ninki, 50).RemoveStatus(Higi);
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
