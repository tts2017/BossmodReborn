namespace BossMod.Autorotation.Engine.Jobs;

// Monk (level 100) for the rotation engine: data only, no BossMod dependency (also compiled by the engine tools).
// Mechanics follow tools/xan_timeline_harness/MnkCombatState.cs + MnkPotencyScorer.cs (positionals credited, Opo-opo guaranteed
// crit x1.38, Riddle of Fire x1.15, Brotherhood x1.05; a blitz does not grant Formless Fist there).
// Forms: Opo-opo skills need no form; Raptor / Coeurl skills need their form, Formless Fist or Perfect Balance (RequiresAnyStatus).
// Outside Perfect Balance each skill sets the next form (and uses up Formless Fist when used through it); under Perfect Balance it
// banks a Beast Chakra of its type instead. Beast Chakra are counted per type (BeastOpo / BeastRaptor / BeastCoeurl, plus
// BeastTotal), so Elixir Burst (3 of a kind) and Rising Phoenix (one of each) are legal only for their mix; with both Nadi any
// full gauge becomes Phantom Rush. Chakra is counted in quarters (ChakraQ: +1 per weaponskill, +4 under Meditative Brotherhood).
public static class MnkDefinition
{
    // gauges
    public const string OpoFury = "OpoFury", RaptorFury = "RaptorFury", CoeurlFury = "CoeurlFury";
    public const string BeastOpo = "BeastOpo", BeastRaptor = "BeastRaptor", BeastCoeurl = "BeastCoeurl", BeastTotal = "BeastTotal";
    public const string Lunar = "Lunar", Solar = "Solar", NadiCount = "NadiCount";
    public const string ChakraQ = "ChakraQ";

    // statuses
    public const string OpoForm = "OpoForm", RaptorForm = "RaptorForm", CoeurlForm = "CoeurlForm", Formless = "FormlessFist";
    public const string PerfectBalance = "PerfectBalance", BlitzReady = "BlitzReady";
    public const string RiddleOfFire = "RiddleOfFire", Brotherhood = "Brotherhood", MeditativeBrotherhood = "MeditativeBrotherhood";
    public const string FiresRumination = "FiresRumination", WindsRumination = "WindsRumination";

    // cooldown groups
    public const string PerfectBalanceCD = "PerfectBalanceCD", RiddleOfFireCD = "RiddleOfFireCD", BrotherhoodCD = "BrotherhoodCD", RiddleOfWindCD = "RiddleOfWindCD";

    public const float Crit = 1.38f;

    // game action ids (BossMod.MNK.AID)
    public const uint AidBootshine = 53, AidDragonKick = 74, AidTrueStrike = 54, AidTwinSnakes = 61, AidSnapPunch = 56, AidDemolish = 66,
        AidShadowOfTheDestroyer = 25767, AidFourPointFury = 16473, AidRockbreaker = 70,
        AidElixirBurst = 36948, AidRisingPhoenix = 25768, AidPhantomRush = 25769, AidFiresReply = 36950, AidWindsReply = 36949,
        AidForbiddenChakra = 3547, AidEnlightenment = 16474, AidPerfectBalance = 69, AidRiddleOfFire = 7395, AidBrotherhood = 7396, AidRiddleOfWind = 25766;

    public static JobDefinition Build(float gcd = 2.0f)
    {
        var b = new JobBuilder("MNK", gcd)
            .Gauge(OpoFury, 1).Gauge(RaptorFury, 1).Gauge(CoeurlFury, 2)
            .Gauge(BeastOpo, 3).Gauge(BeastRaptor, 3).Gauge(BeastCoeurl, 3).Gauge(BeastTotal, 3)
            .Gauge(Lunar, 1).Gauge(Solar, 1).Gauge(NadiCount, 2).Gauge(ChakraQ, 40)
            .Status(OpoForm, 30).Status(RaptorForm, 30).Status(CoeurlForm, 30).Status(Formless, 30)
            .Status(PerfectBalance, 20, maxStacks: 3)
            .Status(BlitzReady, 20, mustNotExpire: true) // a full Beast gauge left to run out loses the blitz
            .Status(RiddleOfFire, 20, damageMultiplier: 1.15f).Status(Brotherhood, 20, damageMultiplier: 1.05f).Status(MeditativeBrotherhood, 20)
            .Status(FiresRumination, 20).Status(WindsRumination, 15)
            .Cooldown(PerfectBalanceCD, 40, 2).Cooldown(RiddleOfFireCD, 60).Cooldown(BrotherhoodCD, 120).Cooldown(RiddleOfWindCD, 90)
            // every GCD needs a form or a fury stack, so the analysis cannot measure a free filler: the basic loop averages about 500
            .FillerPotency(500);

        // ---- Opo-opo step (no form needed; guaranteed crit with Opo-opo form, Formless Fist or Perfect Balance) ----
        Step(b.Gcd("LeapingOpo", 460, AidBootshine).PotencyIfAnyStatus(460 * Crit, OpoForm, Formless, PerfectBalance).SpendGauge(OpoFury, 1), true, OpoForm, RaptorForm, BeastOpo);
        Step(b.Gcd("Bootshine", 260, AidBootshine).PotencyIfAnyStatus(260 * Crit, OpoForm, Formless, PerfectBalance).RequiresGaugeAtMost(OpoFury, 0), true, OpoForm, RaptorForm, BeastOpo);
        Step(b.Gcd("DragonKick", 320, AidDragonKick).SetGauge(OpoFury, 1), true, OpoForm, RaptorForm, BeastOpo);
        Step(b.Gcd("ShadowOfTheDestroyer", 120, AidShadowOfTheDestroyer).AoeFalloff(120).PotencyIfAnyStatus(120 * Crit, OpoForm, Formless, PerfectBalance), true, OpoForm, RaptorForm, BeastOpo);
        // ---- Raptor step ----
        Step(b.Gcd("RisingRaptor", 540, AidTrueStrike).SpendGauge(RaptorFury, 1), false, RaptorForm, CoeurlForm, BeastRaptor);
        Step(b.Gcd("TrueStrike", 340, AidTrueStrike).RequiresGaugeAtMost(RaptorFury, 0), false, RaptorForm, CoeurlForm, BeastRaptor);
        Step(b.Gcd("TwinSnakes", 420, AidTwinSnakes).SetGauge(RaptorFury, 1), false, RaptorForm, CoeurlForm, BeastRaptor);
        Step(b.Gcd("FourPointFury", 140, AidFourPointFury).AoeFalloff(140), false, RaptorForm, CoeurlForm, BeastRaptor);
        // ---- Coeurl step ----
        Step(b.Gcd("PouncingCoeurl", 520, AidSnapPunch).SpendGauge(CoeurlFury, 1), false, CoeurlForm, OpoForm, BeastCoeurl);
        Step(b.Gcd("SnapPunch", 370, AidSnapPunch).RequiresGaugeAtMost(CoeurlFury, 0), false, CoeurlForm, OpoForm, BeastCoeurl);
        Step(b.Gcd("Demolish", 420, AidDemolish).SetGauge(CoeurlFury, 2), false, CoeurlForm, OpoForm, BeastCoeurl);
        Step(b.Gcd("Rockbreaker", 150, AidRockbreaker).AoeFalloff(150), false, CoeurlForm, OpoForm, BeastCoeurl);

        // ---- blitzes (Masterful Blitz resolves by the Beast Chakra mix and the Nadi) ----
        foreach (var kind in new[] { BeastOpo, BeastRaptor, BeastCoeurl })
            Blitz(b.Gcd("ElixirBurst" + kind[5..], 900, AidElixirBurst).AoeFalloff(900 * 0.65f).RequiresGauge(kind, 3).RequiresGaugeAtMost(NadiCount, 1)
                .IfGaugeAtMost(Lunar, 0).GainGauge(NadiCount, 1).SetGauge(Lunar, 1));
        Blitz(b.Gcd("RisingPhoenix", 900, AidRisingPhoenix).AoeFalloff(900 * 0.65f).RequiresGauge(BeastOpo, 1).RequiresGauge(BeastRaptor, 1).RequiresGauge(BeastCoeurl, 1)
            .RequiresGaugeAtMost(NadiCount, 1).IfGaugeAtMost(Solar, 0).GainGauge(NadiCount, 1).SetGauge(Solar, 1));
        Blitz(b.Gcd("PhantomRush", 1500, AidPhantomRush).AoeFalloff(1500 * 0.65f).RequiresGauge(BeastTotal, 3).RequiresGauge(NadiCount, 2).RequiresStatus(Brotherhood)
            .SetGauge(Lunar, 0).SetGauge(Solar, 0).GainGauge(NadiCount, -2)); // a counted spend: the Nadi get a shadow price

        // ---- Riddle follow-ups ----
        b.Gcd("FiresReply", 1400, AidFiresReply).ComboNeutral().AoeFalloff(1400 * 0.65f).Weaponskill().RequiresStatus(FiresRumination).RemoveStatus(FiresRumination)
            .ApplyStatus(Formless, 30).GainGauge(ChakraQ, 1).IfStatus(MeditativeBrotherhood).GainGauge(ChakraQ, 3);
        b.Gcd("WindsReply", 1040, AidWindsReply).ComboNeutral().AoeFalloff(1040 * 0.65f).Weaponskill().RequiresStatus(WindsRumination).RemoveStatus(WindsRumination)
            .GainGauge(ChakraQ, 1).IfStatus(MeditativeBrotherhood).GainGauge(ChakraQ, 3);

        // ---- abilities ----
        b.Ogcd("ForbiddenChakra", 400, null, AidForbiddenChakra).SpendGauge(ChakraQ, 20);
        b.Ogcd("Enlightenment", 160, null, AidEnlightenment).AoeFalloff(160 * 0.65f).SpendGauge(ChakraQ, 20);
        // spec (agents_mnk.md, mnk_regression rules): two Perfect Balances in the even (Brotherhood) window, the first just before Riddle of
        // Fire; one in the odd window, only while it leaves a charge coming back for the next even window (the next charge within 20 s:
        // with the even window about 60 s away, the one after it is back in time); Phantom Rush inside Brotherhood;
        // Brotherhood with Riddle of Fire
        b.Ogcd("PerfectBalance", 0, PerfectBalanceCD, AidPerfectBalance).RequiresStatus(RiddleOfFire).RequiresStatus(Brotherhood).RequiresGaugeAtMost(BeastTotal, 0).ForbidStatus(PerfectBalance).ApplyStatus(PerfectBalance, 20, 3);
        b.Ogcd("PerfectBalanceOdd", 0, PerfectBalanceCD, AidPerfectBalance).RequiresStatus(RiddleOfFire).ForbidStatus(Brotherhood).RequiresCooldownAtLeast(BrotherhoodCD, 20).RequiresRechargeAtMost(PerfectBalanceCD, 20).RequiresGaugeAtMost(BeastTotal, 0).ForbidStatus(PerfectBalance).ApplyStatus(PerfectBalance, 20, 3);
        b.Ogcd("PerfectBalancePre", 0, PerfectBalanceCD, AidPerfectBalance).ForbidStatus(RiddleOfFire).RequiresCooldownAtMost(RiddleOfFireCD, 4).RequiresCooldownAtMost(BrotherhoodCD, 5).RequiresGaugeAtMost(BeastTotal, 0).ForbidStatus(PerfectBalance).ApplyStatus(PerfectBalance, 20, 3);
        b.Ogcd("RiddleOfFire", 0, RiddleOfFireCD, AidRiddleOfFire).NeedsUptime(2).ApplyStatus(RiddleOfFire, 20).ApplyStatus(FiresRumination, 20);
        b.Ogcd("Brotherhood", 0, BrotherhoodCD, AidBrotherhood).NeedsUptime(2).RequiresStatusLeft(RiddleOfFire, 15.5f).ApplyStatus(Brotherhood, 20).ApplyStatus(MeditativeBrotherhood, 20);
        b.Ogcd("BrotherhoodFirst", 0, BrotherhoodCD, AidBrotherhood).NeedsUptime(2).ForbidStatus(RiddleOfFire).RequiresCooldownAtMost(RiddleOfFireCD, 1).ApplyStatus(Brotherhood, 20).ApplyStatus(MeditativeBrotherhood, 20);
        b.Ogcd("RiddleOfWind", 0, RiddleOfWindCD, AidRiddleOfWind).NeedsUptime(2).ApplyStatus(WindsRumination, 15);
        return b.Build();
    }

    // a form step: outside Perfect Balance it needs its form (Raptor / Coeurl steps), uses up Formless Fist when used without its form
    // and sets the next form; under Perfect Balance it banks a Beast Chakra (the third one starts the blitz timer). Every weaponskill
    // builds a quarter chakra (four under Meditative Brotherhood).
    private static void Step(JobBuilder.SkillBuilder s, bool isOpo, string form, string next, string beast)
    {
        s.ComboNeutral().Weaponskill();
        if (!isOpo)
            s.RequiresAnyStatus(form, Formless, PerfectBalance);
        s.IfStatusInactive(PerfectBalance).IfStatusInactive(form).RemoveStatus(Formless)
            .IfStatusInactive(PerfectBalance).RemoveStatus(OpoForm)
            .IfStatusInactive(PerfectBalance).RemoveStatus(RaptorForm)
            .IfStatusInactive(PerfectBalance).RemoveStatus(CoeurlForm)
            .IfStatusInactive(PerfectBalance).ApplyStatus(next, 30)
            .IfStatus(PerfectBalance).GainGauge(beast, 1)
            .IfStatus(PerfectBalance).GainGauge(BeastTotal, 1)
            .IfStatus(PerfectBalance).IfGaugeAtLeast(BeastTotal, 2).ApplyStatus(BlitzReady, 20)
            .IfStatus(PerfectBalance).UseStack(PerfectBalance)
            .GainGauge(ChakraQ, 1).IfStatus(MeditativeBrotherhood).GainGauge(ChakraQ, 3);
    }

    private static void Blitz(JobBuilder.SkillBuilder s) => s.ComboNeutral().Weaponskill()
        .SetGauge(BeastOpo, 0).SetGauge(BeastRaptor, 0).SetGauge(BeastCoeurl, 0).SetGauge(BeastTotal, 0).RemoveStatus(BlitzReady)
        .GainGauge(ChakraQ, 1).IfStatus(MeditativeBrotherhood).GainGauge(ChakraQ, 3);

    // the game action of a skill (variants share one); the plugin presses the base actions (Bootshine for Leaping Opo etc.)
    public static uint ActionOf(SkillDef s) => s.ActionId;

    public static EngineWeights DefaultWeights() => EngineWeights.Parse(DefaultWeightsJson);

    // CMA-ES on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-MNK-v3.json), BudgetMs for live play
    // MinNodes / SliceNodes: every search (at most about 4400 nodes, the opener) completes and frame slices are counted in nodes, so
    // live play does not depend on the machine's timing (it diverged by a few thousand potency between runs otherwise)
    public const string DefaultWeightsJson = """
    {
      "OverCap": 3.2322094, "Combo": 0.14532924, "LambdaScale": 0.37984324, "TargetPull": 0, "SwitchMargin": 2.063169, "FillerScale": 1.1005285, "BurstBias": 0,
      "StatusRemainder": 2, "CycleScale": 1, "CooldownLambdaScale": 0.12746207, "ForecastSelfBuffs": 1, "UnlockScale": 1.0827847,
      "StatusValue": {}, "CooldownValue": { "PerfectBalanceCD": 0, "BrotherhoodCD": 0 }, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8,
      "MinNodes": 4500,
      "SliceNodes": 40
    }
    """;
}
