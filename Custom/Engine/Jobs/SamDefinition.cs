namespace BossMod.Autorotation.Engine.Jobs;

// Samurai (level 100) for the rotation engine: data only, no BossMod dependency (also compiled by the engine tools).
// Mechanics follow tools/xan_timeline_harness/SamCombatState.cs + SamPotencyScorer.cs: positionals credited, Fugetsu x1.13, Fuka 13%
// haste on GCDs and casts, Iaijutsu / Ogi Namikiri cast 1.8 s (scaled by speed and Fuka), Setsugekka and Namikiri scored with the
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
    public const uint AidGyofu = 36963, AidJinpu = 7478, AidShifu = 7479, AidGekko = 7481, AidKasha = 7482, AidYukikaze = 7480,
        AidFuko = 25780, AidMangetsu = 7484, AidOka = 7485, AidHiganbana = 7489, AidTenkaGoken = 7488, AidMidare = 7487,
        AidTendoGoken = 36965, AidTendoSetsugekka = 36966, AidKaeshiGoken = 16485, AidKaeshiSetsugekka = 16486,
        AidTendoKaeshiGoken = 36967, AidTendoKaeshiSetsugekka = 36968, AidOgiNamikiri = 25781, AidKaeshiNamikiri = 25782,
        AidMeikyo = 7499, AidIkishoten = 16482, AidZanshin = 36964, AidShinten = 7490, AidKyuten = 7491, AidSenei = 16481, AidGuren = 7496, AidShoha = 16487;

    public static JobDefinition Build(float gcd = 2.5f)
    {
        var cast = 1.8f * gcd / 2.5f;
        var b = new JobBuilder("SAM", gcd)
            .Gauge(Kenki, 100).Gauge(Meditation, 3).Gauge(Setsu, 1).Gauge(Getsu, 1).Gauge(Ka, 1).Gauge(SenCount, 3)
            .Status(Fugetsu, 40, damageMultiplier: 1.13f).Status(Fuka, 40, gcdRecastMultiplier: 0.87f, castTimeMultiplier: 0.87f)
            .Status(Meikyo, 20, maxStacks: 3).Status(Tendo, 30).Status(OgiReady, 30).Status(NamikiriReady, 30).Status(ZanshinReady, 30)
            .Status(KaeshiGoken, 30).Status(KaeshiSetsugekka, 30).Status(TendoKaeshiGoken, 30).Status(TendoKaeshiSetsugekka, 30)
            .Status(Higanbana, 60)
            .Cooldown(MeikyoCD, 55, 2).Cooldown(IkishotenCD, 120).Cooldown(SeneiCD, 120).Cooldown(ShohaCD, 15);

        // ---- combo (Meikyo Shisui: every step counts as combo and uses a stack) ----
        b.Gcd("Gyofu", 240, AidGyofu).StartsCombo().GainGauge(Kenki, 5);
        Step(b, "Jinpu", AidJinpu, 140, 300, "Gyofu", ComboMode.Continue, 5, null, Fugetsu, null, 0);
        Step(b, "Shifu", AidShifu, 140, 300, "Gyofu", ComboMode.Continue, 5, null, Fuka, null, 0);
        Step(b, "Gekko", AidGekko, 210, 420, "Jinpu", ComboMode.End, 10, Getsu, null, Fugetsu, 0);
        Step(b, "Kasha", AidKasha, 210, 420, "Shifu", ComboMode.End, 10, Ka, null, Fuka, 0);
        Step(b, "Yukikaze", AidYukikaze, 160, 340, "Gyofu", ComboMode.End, 15, Setsu, null, null, 0);
        b.Gcd("Fuko", 100, AidFuko).AoeFalloff(100).StartsCombo().GainGauge(Kenki, 10);
        Step(b, "Mangetsu", AidMangetsu, 100, 120, "Fuko", ComboMode.End, 10, Getsu, Fugetsu, Fugetsu, 100);
        Step(b, "Oka", AidOka, 100, 120, "Fuko", ComboMode.End, 10, Ka, Fuka, Fuka, 100);

        // ---- Iaijutsu (casts; exact Sen count; Tendo upgrades them) and Tsubame-gaeshi ----
        Iai(b.Gcd("Higanbana", 200, AidHiganbana).Cast(cast).Dot(Higanbana, 60, 50), 1);
        Iai(b.Gcd("TenkaGoken", 300, AidTenkaGoken).Cast(cast).AoeFalloff(300).ForbidStatus(Tendo).ApplyStatus(KaeshiGoken, 30), 2);
        Iai(b.Gcd("MidareSetsugekka", 680 * Crit, AidMidare).Cast(cast).ForbidStatus(Tendo).ApplyStatus(KaeshiSetsugekka, 30), 3);
        Iai(b.Gcd("TendoGoken", 410, AidTendoGoken).Cast(cast).AoeFalloff(410).RequiresStatus(Tendo).RemoveStatus(Tendo).ApplyStatus(TendoKaeshiGoken, 30), 2);
        Iai(b.Gcd("TendoSetsugekka", 1100 * Crit, AidTendoSetsugekka).Cast(cast).RequiresStatus(Tendo).RemoveStatus(Tendo).ApplyStatus(TendoKaeshiSetsugekka, 30), 3);
        b.Gcd("KaeshiGoken", 300, AidKaeshiGoken).ComboNeutral().AoeFalloff(300).RequiresStatus(KaeshiGoken).RemoveStatus(KaeshiGoken);
        b.Gcd("KaeshiSetsugekka", 680 * Crit, AidKaeshiSetsugekka).ComboNeutral().RequiresStatus(KaeshiSetsugekka).RemoveStatus(KaeshiSetsugekka);
        b.Gcd("TendoKaeshiGoken", 410, AidTendoKaeshiGoken).ComboNeutral().AoeFalloff(410).RequiresStatus(TendoKaeshiGoken).RemoveStatus(TendoKaeshiGoken);
        b.Gcd("TendoKaeshiSetsugekka", 1100 * Crit, AidTendoKaeshiSetsugekka).ComboNeutral().RequiresStatus(TendoKaeshiSetsugekka).RemoveStatus(TendoKaeshiSetsugekka);
        b.Gcd("OgiNamikiri", 1000 * Crit, AidOgiNamikiri).Cast(cast).ComboNeutral().Cone().AoeFalloff(600 * Crit).RequiresStatus(OgiReady).RemoveStatus(OgiReady)
            .ApplyStatus(NamikiriReady, 30).GainGauge(Meditation, 1);
        b.Gcd("KaeshiNamikiri", 1000 * Crit, AidKaeshiNamikiri).ComboNeutral().Cone().AoeFalloff(600 * Crit).RequiresStatus(NamikiriReady).RemoveStatus(NamikiriReady);

        // ---- abilities ----
        b.Ogcd("MeikyoShisui", 0, MeikyoCD, AidMeikyo).ForbidStatus(Meikyo).ApplyStatus(Meikyo, 20, 3).ApplyStatus(Tendo, 30);
        b.Ogcd("Ikishoten", 0, IkishotenCD, AidIkishoten).NeedsUptime(2).GainGauge(Kenki, 50).ApplyStatus(OgiReady, 30).ApplyStatus(ZanshinReady, 30);
        b.Ogcd("Zanshin", 940, null, AidZanshin).Cone().AoeFalloff(940 * 0.6f).RequiresStatus(ZanshinReady).RemoveStatus(ZanshinReady).SpendGauge(Kenki, 50);
        b.Ogcd("HissatsuSenei", 800, SeneiCD, AidSenei).SpendGauge(Kenki, 25);
        b.Ogcd("HissatsuGuren", 400, SeneiCD, AidGuren).AoeFalloff(400).SpendGauge(Kenki, 25);
        b.Ogcd("HissatsuShinten", 250, null, AidShinten).SpendGauge(Kenki, 25);
        b.Ogcd("HissatsuKyuten", 100, null, AidKyuten).AoeFalloff(100).SpendGauge(Kenki, 25);
        b.Ogcd("Shoha", 640, ShohaCD, AidShoha).AoeFalloff(640 * 0.6f).SpendGauge(Meditation, 3);
        return b.Build();
    }

    // a combo step, as two skills: the normal one (gains only as combo) and its Meikyo Shisui variant (always counts as combo, uses a
    // stack, Gekko / Kasha also grant Fugetsu / Fuka); aoe > 0: potency on every target
    private static void Step(JobBuilder b, string name, uint aid, float potency, float comboPotency, string from, ComboMode mode, int kenki, string? sen, string? buff, string? meikyoBuff, float aoe)
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

    private static void Iai(JobBuilder.SkillBuilder s, int sen) => s.ComboNeutral()
        .RequiresGauge(SenCount, sen).RequiresGaugeAtMost(SenCount, sen)
        .SetGauge(Setsu, 0).SetGauge(Getsu, 0).SetGauge(Ka, 0).GainGauge(SenCount, -sen).GainGauge(Meditation, 1); // a counted spend: Sen get a shadow price

    public static EngineWeights DefaultWeights() => EngineWeights.Parse(DefaultWeightsJson);

    // CMA-ES on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-SAM-v1.json), BudgetMs for live play
    public const string DefaultWeightsJson = """
    {
      "OverCap": 1.560, "Combo": 1.409, "LambdaScale": 0.317, "TargetPull": 0, "SwitchMargin": 0, "FillerScale": 0.5, "BurstBias": 1.057,
      "StatusRemainder": 1.863, "CycleScale": 1, "CooldownLambdaScale": 0, "ForecastSelfBuffs": 1, "UnlockScale": 1.145,
      "StatusValue": { "Fugetsu": 0, "Fuka": 2.948 }, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8
    }
    """;
}
