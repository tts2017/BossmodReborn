namespace BossMod.Autorotation.Engine.Jobs;

// Samurai for the rotation engine (any level; weights tuned at level 100 and per level band, see DefaultWeights): data only, no BossMod dependency (also compiled by the engine tools).
// Mechanics follow tools/xan_timeline_harness/SamCombatState.cs + SamPotencyScorer.cs: positionals credited, Fugetsu x1.13, Fuka 13%
// haste on GCDs and casts, Iaijutsu / Ogi Namikiri cast 1.3 s (Enhanced Iaijutsu; scaled by speed and Fuka), Setsugekka and Namikiri scored with the
// guaranteed-crit factor 1.391, Higanbana 50 per 3 s for 60 s. Sen are three flags plus SenCount (Iaijutsu are gated by the exact
// count). Meikyo Shisui: every combo step counts as combo and uses a stack; Gekko / Kasha then grant Fugetsu / Fuka.
// Tsubame-gaeshi is the Kaeshi skills gated by their statuses (as in the harness, no charges).
public static class SamDefinition
{
    // gauges
    public const string Kenki = "Kenki", Meditation = "Meditation", Setsu = "Setsu", Getsu = "Getsu", Ka = "Ka", SenCount = "SenCount";

    // statuses
    public const string Fugetsu = "Fugetsu", Fuka = "Fuka", Meikyo = "MeikyoShisui", Tendo = "Tendo", OgiReady = "OgiNamikiriReady",
        NamikiriReady = "KaeshiNamikiriReady", ZanshinReady = "ZanshinReady", KaeshiGoken = "KaeshiGoken", KaeshiSetsugekka = "KaeshiSetsugekka",
        TendoKaeshiGoken = "TendoKaeshiGoken", TendoKaeshiSetsugekka = "TendoKaeshiSetsugekka", Higanbana = "Higanbana";

    // cooldown groups
    public const string MeikyoCD = "MeikyoCD", IkishotenCD = "IkishotenCD", SeneiCD = "SeneiCD", ShohaCD = "ShohaCD";

    public const float Crit = 1.6f / 1.15f;

    // game action ids (BossMod.SAM.AID)
    public const uint AidHakaze = 7477, AidFuga = 7483, AidGyofu = 36963, AidJinpu = 7478, AidShifu = 7479, AidGekko = 7481, AidKasha = 7482, AidYukikaze = 7480,
        AidFuko = 25780, AidMangetsu = 7484, AidOka = 7485, AidHiganbana = 7489, AidTenkaGoken = 7488, AidMidare = 7487,
        AidTendoGoken = 36965, AidTendoSetsugekka = 36966, AidKaeshiGoken = 16485, AidKaeshiSetsugekka = 16486,
        AidTendoKaeshiGoken = 36967, AidTendoKaeshiSetsugekka = 36968, AidOgiNamikiri = 25781, AidKaeshiNamikiri = 25782,
        AidMeikyo = 7499, AidIkishoten = 16482, AidZanshin = 36964, AidShinten = 7490, AidKyuten = 7491, AidSenei = 16481, AidGuren = 7496, AidShoha = 16487;

    // level: the player's (synced) level. Skills not unlocked there are left out; potencies follow SamPotencyScorer.cs (l66 / l84 / l94
    // branches), Kenki gains Kenki Mastery (52 / 62), Fugetsu / Fuka Enhanced Fugetsu and Fuka (78), the Iaijutsu cast Enhanced Iaijutsu (74),
    // Meikyo Shisui's second charge Enhanced Meikyo Shisui (76); Tsubame-gaeshi from 76, Meditation from 80.
    public static JobDefinition Build(float gcd = 2.5f, int level = 100)
    {
        var l94 = level >= 94;
        var l84 = level >= 84;
        var l66 = level >= 66;
        var meikyo = level >= 50;
        var kaeshi = level >= 76;
        var meditation = level >= 80;
        // Kenki Mastery (52) / Kenki Mastery II (62)
        int Kenki62(int full, int at52) => level >= 62 ? full : level >= 52 ? at52 : 0;
        var cast = (level >= 74 ? 1.3f : 1.8f) * gcd / 2.5f;
        var fufu = level >= 78;
        var b = new JobBuilder("SAM", gcd)
            .Gauge(Kenki, 100).Gauge(Meditation, 3).Gauge(Setsu, 1).Gauge(Getsu, 1).Gauge(Ka, 1).Gauge(SenCount, 3)
            .Status(Fugetsu, 40, damageMultiplier: fufu ? 1.13f : 1.10f).Status(Fuka, 40, gcdRecastMultiplier: fufu ? 0.87f : 0.90f, castTimeMultiplier: fufu ? 0.87f : 0.90f)
            .Status(Meikyo, 20, maxStacks: 3).Status(Tendo, 30).Status(OgiReady, 30).Status(NamikiriReady, 30).Status(ZanshinReady, 30)
            .Status(KaeshiGoken, 30).Status(KaeshiSetsugekka, 30).Status(TendoKaeshiGoken, 30).Status(TendoKaeshiSetsugekka, 30)
            .Status(Higanbana, 60)
            // Senei / Guren: 60 s from Enhanced Hissatsu (94)
            .Cooldown(MeikyoCD, 55, level >= 76 ? 2 : 1).Cooldown(IkishotenCD, 120).Cooldown(SeneiCD, level >= 94 ? 60 : 120).Cooldown(ShohaCD, 15);

        // ---- combo (Meikyo Shisui: every step counts as combo and uses a stack) ----
        // Gyofu (92) replaces Hakaze
        if (level >= 92)
            b.Gcd("Gyofu", 240, AidGyofu).StartsCombo().GainGauge(Kenki, 5);
        else
            b.Gcd("Gyofu", l66 ? 200 : 180, AidHakaze).StartsCombo().GainGauge(Kenki, level >= 62 ? 5 : 0);
        if (level >= 4)
            Step(b, "Jinpu", AidJinpu, l94 ? 140 : l66 ? 120 : 100, l94 ? 300 : l66 ? 280 : 260, "Gyofu", ComboMode.Continue, level >= 62 ? 5 : 0, null, Fugetsu, null, 0, meikyo);
        if (level >= 18)
            Step(b, "Shifu", AidShifu, l94 ? 140 : l66 ? 120 : 100, l94 ? 300 : l66 ? 280 : 260, "Gyofu", ComboMode.Continue, level >= 62 ? 5 : 0, null, Fuka, null, 0, meikyo);
        if (level >= 30)
            Step(b, "Gekko", AidGekko, l94 ? 210 : l84 ? 160 : 150, l94 ? 420 : l84 ? 370 : 360, "Jinpu", ComboMode.End, Kenki62(10, 5), Getsu, null, Fugetsu, 0, meikyo);
        if (level >= 40)
            Step(b, "Kasha", AidKasha, l94 ? 210 : l84 ? 160 : 150, l94 ? 420 : l84 ? 370 : 360, "Shifu", ComboMode.End, Kenki62(10, 5), Ka, null, Fuka, 0, meikyo);
        if (level >= 50)
            Step(b, "Yukikaze", AidYukikaze, l94 ? 160 : l84 ? 110 : 100, l94 ? 340 : l84 ? 290 : 280, "Gyofu", ComboMode.End, Kenki62(15, 10), Setsu, null, null, 0, meikyo);
        // Fuko (86) replaces Fuga (a cone)
        if (level >= 86)
            b.Gcd("Fuko", 100, AidFuko).AoeFalloff(100).StartsCombo().GainGauge(Kenki, 10);
        else if (level >= 26)
            b.Gcd("Fuko", 90, AidFuga).Cone().AoeFalloff(90).StartsCombo().GainGauge(Kenki, level >= 62 ? 5 : 0);
        if (level >= 35)
            Step(b, "Mangetsu", AidMangetsu, 100, 120, "Fuko", ComboMode.End, Kenki62(10, 5), Getsu, Fugetsu, Fugetsu, 100, meikyo);
        if (level >= 45)
            Step(b, "Oka", AidOka, 100, 120, "Fuko", ComboMode.End, Kenki62(10, 5), Ka, Fuka, Fuka, 100, meikyo);

        // ---- Iaijutsu (casts; exact Sen count; Tendo upgrades them) and Tsubame-gaeshi ----
        if (level >= 30)
            Iai(b.Gcd("Higanbana", 200, AidHiganbana).Cast(cast).Dot(Higanbana, 60, l94 ? 50 : 45), 1, meditation);
        if (level >= 40)
        {
            var tenka = b.Gcd("TenkaGoken", 300, AidTenkaGoken).Cast(cast).AoeFalloff(300).ForbidStatus(Tendo);
            if (kaeshi)
                tenka.ApplyStatus(KaeshiGoken, 30);
            Iai(tenka, 2, meditation);
        }
        if (level >= 50)
        {
            var midare = b.Gcd("MidareSetsugekka", l94 ? 680 * Crit : 620 * Crit, AidMidare).Cast(cast).ForbidStatus(Tendo);
            if (kaeshi)
                midare.ApplyStatus(KaeshiSetsugekka, 30);
            Iai(midare, 3, meditation);
        }
        if (level >= 100)
        {
            Iai(b.Gcd("TendoGoken", 410, AidTendoGoken).Cast(cast).AoeFalloff(410).RequiresStatus(Tendo).RemoveStatus(Tendo).ApplyStatus(TendoKaeshiGoken, 30), 2, meditation);
            Iai(b.Gcd("TendoSetsugekka", 1100 * Crit, AidTendoSetsugekka).Cast(cast).RequiresStatus(Tendo).RemoveStatus(Tendo).ApplyStatus(TendoKaeshiSetsugekka, 30), 3, meditation);
        }
        if (kaeshi)
        {
            b.Gcd("KaeshiGoken", 300, AidKaeshiGoken).ComboNeutral().AoeFalloff(300).RequiresStatus(KaeshiGoken).RemoveStatus(KaeshiGoken);
            b.Gcd("KaeshiSetsugekka", l94 ? 680 * Crit : 620 * Crit, AidKaeshiSetsugekka).ComboNeutral().RequiresStatus(KaeshiSetsugekka).RemoveStatus(KaeshiSetsugekka);
        }
        if (level >= 100)
        {
            b.Gcd("TendoKaeshiGoken", 410, AidTendoKaeshiGoken).ComboNeutral().AoeFalloff(410).RequiresStatus(TendoKaeshiGoken).RemoveStatus(TendoKaeshiGoken);
            b.Gcd("TendoKaeshiSetsugekka", 1100 * Crit, AidTendoKaeshiSetsugekka).ComboNeutral().RequiresStatus(TendoKaeshiSetsugekka).RemoveStatus(TendoKaeshiSetsugekka);
        }
        if (level >= 90)
        {
            b.Gcd("OgiNamikiri", l94 ? 1000 * Crit : 860 * Crit, AidOgiNamikiri).Cast(cast).ComboNeutral().Cone().AoeFalloff(l94 ? 600 * Crit : 516 * Crit).RequiresStatus(OgiReady).RemoveStatus(OgiReady)
                .ApplyStatus(NamikiriReady, 30).GainGauge(Meditation, 1);
            b.Gcd("KaeshiNamikiri", l94 ? 1000 * Crit : 860 * Crit, AidKaeshiNamikiri).ComboNeutral().Cone().AoeFalloff(l94 ? 600 * Crit : 516 * Crit).RequiresStatus(NamikiriReady).RemoveStatus(NamikiriReady);
        }

        // ---- abilities ----
        if (meikyo)
        {
            var shisui = b.Ogcd("MeikyoShisui", 0, MeikyoCD, AidMeikyo).ForbidStatus(Meikyo).ApplyStatus(Meikyo, 20, 3);
            // not while a Tendo is still unused: it does not stack, and a second Meikyo Shisui before the Tendo Setsugekka only refreshes it
            // (ersharifst 7.4: both openers press the second Meikyo Shisui after the Tendo Setsugekka)
            if (level >= 100)
                shisui.ForbidStatus(Tendo).ApplyStatus(Tendo, 30);
        }
        if (level >= 68)
        {
            var ikishoten = b.Ogcd("Ikishoten", 0, IkishotenCD, AidIkishoten).NeedsUptime(2).GainGauge(Kenki, 50);
            if (level >= 90)
                ikishoten.ApplyStatus(OgiReady, 30);
            if (level >= 96)
                ikishoten.ApplyStatus(ZanshinReady, 30);
        }
        if (level >= 96)
            b.Ogcd("Zanshin", 940, null, AidZanshin).Cone().AoeFalloff(940 * 0.6f).RequiresStatus(ZanshinReady).RemoveStatus(ZanshinReady).SpendGauge(Kenki, 50);
        if (level >= 72)
            b.Ogcd("HissatsuSenei", 800, SeneiCD, AidSenei).SpendGauge(Kenki, 25);
        if (level >= 70)
            b.Ogcd("HissatsuGuren", 400, SeneiCD, AidGuren).AoeFalloff(400).SpendGauge(Kenki, 25);
        if (level >= 52)
            b.Ogcd("HissatsuShinten", 250, null, AidShinten).SpendGauge(Kenki, 25);
        if (level >= 62)
            b.Ogcd("HissatsuKyuten", 100, null, AidKyuten).AoeFalloff(100).SpendGauge(Kenki, 25);
        if (meditation)
            b.Ogcd("Shoha", l94 ? 640 : 560, ShohaCD, AidShoha).AoeFalloff(l94 ? 640 * 0.6f : 560 * 0.6f).SpendGauge(Meditation, 3);
        return b.Build();
    }

    // a combo step, as two skills: the normal one (gains only as combo) and its Meikyo Shisui variant (always counts as combo, uses a
    // stack, Gekko / Kasha also grant Fugetsu / Fuka; only once Meikyo Shisui is unlocked); aoe > 0: potency on every target
    private static void Step(JobBuilder b, string name, uint aid, float potency, float comboPotency, string from, ComboMode mode, int kenki, string? sen, string? buff, string? meikyoBuff, float aoe, bool meikyo)
    {
        var s = b.Gcd(name, potency, aid).ComboFrom(from, comboPotency).ForbidStatus(Meikyo);
        if (mode == ComboMode.End)
            s.EndsCombo();
        if (aoe > 0)
            s.AoeFalloff(aoe);
        s.IfCombo(from).GainGauge(Kenki, kenki);
        if (sen != null)
            s.IfCombo(from).IfGaugeAtMost(sen, 0).GainGauge(SenCount, 1).IfCombo(from).SetGauge(sen, 1);
        if (buff != null)
            s.IfCombo(from).ApplyStatus(buff, 40);
        if (!meikyo)
            return;

        var m = b.Gcd(name + "Meikyo", comboPotency, aid).EndsCombo().RequiresStatus(Meikyo).UseStack(Meikyo).GainGauge(Kenki, kenki);
        if (aoe > 0)
            m.AoeFalloff(aoe * comboPotency / potency);
        if (sen != null)
            m.IfGaugeAtMost(sen, 0).GainGauge(SenCount, 1).SetGauge(sen, 1);
        if (buff != null)
            m.ApplyStatus(buff, 40);
        if (meikyoBuff != null && meikyoBuff != buff)
            m.ApplyStatus(meikyoBuff, 40);
    }

    // meditation: the Iaijutsu also grants a Meditation stack (level 80)
    private static void Iai(JobBuilder.SkillBuilder s, int sen, bool meditation)
    {
        s.ComboNeutral()
            .RequiresGauge(SenCount, sen).RequiresGaugeAtMost(SenCount, sen)
            .SetGauge(Setsu, 0).SetGauge(Getsu, 0).SetGauge(Ka, 0).GainGauge(SenCount, -sen); // a counted spend: Sen get a shadow price
        if (meditation)
            s.GainGauge(Meditation, 1);
    }

    // level sync: the set tuned at the lowest level of the player's band (90-99 L90, 80-89 L80, 70-79 L70); the Lv100 set at 100 and in the other bands
    public static EngineWeights DefaultWeights(int level = 100) => EngineWeights.Parse(level switch
    {
        >= 100 => DefaultWeightsJson,
        >= 90 => WeightsL90Json,
        >= 80 => WeightsL80Json,
        >= 70 => WeightsL70Json,
        _ => DefaultWeightsJson
    });

    // CMA-ES on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-SAM-v2.json), BudgetMs for live play
    public const string DefaultWeightsJson = """
    {
      "OverCap": 0, "Combo": 1.6498287, "LambdaScale": 0.44048476, "TargetPull": 0, "SwitchMargin": 4.051655, "FillerScale": 0.57231796, "BurstBias": 0,
      "StatusRemainder": 1.3374653, "CycleScale": 1, "CooldownLambdaScale": 0, "ForecastSelfBuffs": 1, "UnlockScale": 1.2380846,
      "StatusValue": { "Fugetsu": 0, "Fuka": 0 }, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8
    }
    """;

    // CMA-ES at level 90 on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-SAM-L90.json),
    // search settings as the Lv100 set
    public const string WeightsL90Json = """
    {
      "OverCap": 0.28648, "Combo": 1.6719396, "LambdaScale": 0.4268299, "TargetPull": 0, "SwitchMargin": 0, "FillerScale": 0.53795385, "BurstBias": 0,
      "StatusRemainder": 1.2577714, "CycleScale": 1, "CooldownLambdaScale": 0.7710779, "ForecastSelfBuffs": 1, "UnlockScale": 1.2300762,
      "StatusValue": { "Fugetsu": 0, "Fuka": 6.530524 }, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8
    }
    """;

    // CMA-ES at level 80 on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-SAM-L80.json),
    // search settings as the Lv100 set
    public const string WeightsL80Json = """
    {
      "OverCap": 0, "Combo": 2, "LambdaScale": 0.33683532, "TargetPull": 0, "SwitchMargin": 0, "FillerScale": 0.5, "BurstBias": 0,
      "StatusRemainder": 1.1185102, "CycleScale": 1, "CooldownLambdaScale": 0.3846011, "ForecastSelfBuffs": 1, "UnlockScale": 2,
      "StatusValue": { "Fugetsu": 0, "Fuka": 0 }, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8
    }
    """;

    // CMA-ES at level 70 on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-SAM-L70.json),
    // search settings as the Lv100 set
    public const string WeightsL70Json = """
    {
      "OverCap": 0.03778802, "Combo": 1.7485423, "LambdaScale": 0.30415797, "TargetPull": 0, "SwitchMargin": 0, "FillerScale": 0.5606293, "BurstBias": 0.11387898,
      "StatusRemainder": 0.8727938, "CycleScale": 1, "CooldownLambdaScale": 0.004396679, "ForecastSelfBuffs": 1, "UnlockScale": 1.8318105,
      "StatusValue": { "Fugetsu": 0.9533995, "Fuka": 10.322646 }, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8
    }
    """;
}
