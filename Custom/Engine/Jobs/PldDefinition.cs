namespace BossMod.Autorotation.Engine.Jobs;

// Paladin for the rotation engine (any level; weights tuned at level 100 and per level band, see DefaultWeights): data only, no BossMod dependency (also compiled by the engine tools).
// Mechanics and potencies follow tools/xan_timeline_harness/PldCombatState.cs + PldPotencyScorer.cs (Fight or Flight x1.25, target
// splash 40% on the Confiteor chain / Imperator / Blade of Honor / Expiacion, Circle of Scorn's DoT credited at the press).
// Holy Spirit / Holy Circle: Divine Might first (instant, stronger), else a Requiescat stack (instant, strongest), else a 1.5 s cast.
// The Confiteor chain is statuses for its next step; MP is a Flat gauge (costs and the restores the harness grants; natural regen is
// read from the game every frame). No Intervene: it is a dash (gated by the dash setting), left to the regular PLD modules.
public static class PldDefinition
{
    public const string MP = "MP";

    public const string FightOrFlight = "FightOrFlight", GoringReady = "GoringBladeReady", Requiescat = "Requiescat", ConfiteorReady = "ConfiteorReady",
        FaithReady = "BladeOfFaithReady", TruthReady = "BladeOfTruthReady", ValorReady = "BladeOfValorReady", HonorReady = "BladeOfHonorReady",
        AtonementReady = "AtonementReady", SupplicationReady = "SupplicationReady", SepulchreReady = "SepulchreReady", DivineMight = "DivineMight";

    public const string FightOrFlightCD = "FightOrFlightCD", ImperatorCD = "ImperatorCD", ExpiacionCD = "ExpiacionCD", CircleOfScornCD = "CircleOfScornCD";

    public const float Splash = 0.4f;

    public const uint AidFastBlade = 9, AidRiotBlade = 15, AidRoyalAuthority = 3539, AidTotalEclipse = 7381, AidProminence = 16457, AidAtonement = 16460,
        AidSupplication = 36918, AidSepulchre = 36919, AidHolySpirit = 7384, AidHolyCircle = 16458, AidConfiteor = 16459, AidBladeOfFaith = 25748,
        AidBladeOfTruth = 25749, AidBladeOfValor = 25750, AidGoringBlade = 3538, AidFightOrFlight = 20, AidImperator = 36921, AidBladeOfHonor = 36922,
        AidExpiacion = 25747, AidCircleOfScorn = 23, AidRageOfHalone = 21, AidRequiescat = 7383, AidSpiritsWithin = 29;

    // level: the player's (synced) level. Skills not unlocked there are left out; potencies follow PldPotencyScorer.cs (l84 / l94 branches).
    // Rage of Halone before Royal Authority (60), Spirits Within before Expiacion (86), Requiescat (single target, 320) before Imperator
    // (96); Divine Might from 64 (Prominence's from 72), Sword Oath from 76, Riot Blade's MP from 58, Confiteor from 80, the Blades from 90,
    // Blade of Honor at 100.
    public static JobDefinition Build(float gcd = 2.5f, int level = 100)
    {
        var l84 = level >= 84;
        var l94 = level >= 94;
        var holyCast = 1.5f * gcd / 2.5f;
        var b = new JobBuilder("PLD", gcd)
            .Gauge(MP, 10000, flat: true)
            .Status(FightOrFlight, 20, damageMultiplier: 1.25f).Status(GoringReady, 30).Status(Requiescat, 30, maxStacks: 4).Status(ConfiteorReady, 30)
            .Status(FaithReady, 30).Status(TruthReady, 30).Status(ValorReady, 30).Status(HonorReady, 30)
            .Status(AtonementReady, 30).Status(SupplicationReady, 30).Status(SepulchreReady, 30).Status(DivineMight, 30)
            .Cooldown(FightOrFlightCD, 60).Cooldown(ImperatorCD, 60).Cooldown(ExpiacionCD, 30).Cooldown(CircleOfScornCD, 30);

        // ---- combo ----
        b.Gcd("FastBlade", l94 ? 220 : l84 ? 200 : 150, AidFastBlade).StartsCombo();
        if (level >= 4)
        {
            var riot = b.Gcd("RiotBlade", l94 ? 170 : l84 ? 140 : 100, AidRiotBlade).ComboFrom("FastBlade", l94 ? 330 : l84 ? 300 : 260);
            if (level >= 58)
                riot.IfCombo("FastBlade").GainGauge(MP, 1000);
        }
        if (level >= 60)
        {
            var royal = b.Gcd("RoyalAuthority", l94 ? 200 : l84 ? 140 : 100, AidRoyalAuthority).ComboFrom("RiotBlade", l94 ? 460 : l84 ? 400 : 360).EndsCombo();
            if (level >= 64)
                royal.IfCombo("RiotBlade").ApplyStatus(DivineMight, 30);
            if (level >= 76)
                royal.IfCombo("RiotBlade").ApplyStatus(AtonementReady, 30)
                    .IfCombo("RiotBlade").RemoveStatus(SupplicationReady).IfCombo("RiotBlade").RemoveStatus(SepulchreReady);
        }
        else if (level >= 26)
            b.Gcd("RoyalAuthority", 100, AidRageOfHalone).ComboFrom("RiotBlade", 330).EndsCombo();
        if (level >= 6)
            b.Gcd("TotalEclipse", 120, AidTotalEclipse).Shape(AoeShape.SelfCircle, 5, 0, 3).AoeFalloff(120).StartsCombo();
        if (level >= 40)
        {
            var prominence = b.Gcd("Prominence", 100, AidProminence).Shape(AoeShape.SelfCircle, 5, 0, 3).AoeFalloff(100).ComboFrom("TotalEclipse", 220).EndsCombo();
            if (level >= 72)
                prominence.IfCombo("TotalEclipse").ApplyStatus(DivineMight, 30).IfCombo("TotalEclipse").GainGauge(MP, 1000);
        }

        // ---- Sword Oath chain ----
        if (level >= 76)
        {
            b.Gcd("Atonement", l94 ? 460 : l84 ? 400 : 360, AidAtonement).RequiresStatus(AtonementReady).RemoveStatus(AtonementReady).ApplyStatus(SupplicationReady, 30).GainGauge(MP, 400);
            b.Gcd("Supplication", l94 ? 500 : l84 ? 420 : 380, AidSupplication).RequiresStatus(SupplicationReady).RemoveStatus(SupplicationReady).ApplyStatus(SepulchreReady, 30).GainGauge(MP, 400);
            b.Gcd("Sepulchre", l94 ? 540 : l84 ? 440 : 400, AidSepulchre).RequiresStatus(SepulchreReady).RemoveStatus(SepulchreReady).GainGauge(MP, 400);
        }

        // ---- Holy Spirit / Holy Circle (Divine Might first, then a Requiescat stack, else a cast) ----
        if (level >= 64)
        {
            b.Gcd("HolySpiritDM", l94 ? 500 : l84 ? 450 : 400, AidHolySpirit).RequiresStatus(DivineMight).RemoveStatus(DivineMight).SpendGauge(MP, 1000);
            b.Gcd("HolySpiritReq", l94 ? 700 : l84 ? 650 : 600, AidHolySpirit).ForbidStatus(DivineMight).RequiresStatus(Requiescat).UseStack(Requiescat).SpendGauge(MP, 1000);
            b.Gcd("HolySpirit", l94 ? 400 : l84 ? 350 : 300, AidHolySpirit).Cast(holyCast).ForbidStatus(DivineMight).ForbidStatus(Requiescat).SpendGauge(MP, 1000);
        }
        if (level >= 72)
        {
            b.Gcd("HolyCircleDM", 250, AidHolyCircle).Shape(AoeShape.SelfCircle, 5, 0, 5).AoeFalloff(250).RequiresStatus(DivineMight).RemoveStatus(DivineMight).SpendGauge(MP, 1000);
            b.Gcd("HolyCircleReq", 350, AidHolyCircle).Shape(AoeShape.SelfCircle, 5, 0, 5).AoeFalloff(350).ForbidStatus(DivineMight).RequiresStatus(Requiescat).UseStack(Requiescat).SpendGauge(MP, 1000);
        }

        // ---- Confiteor chain (each step uses a Requiescat stack when there is one; the Blades from 90) ----
        if (level >= 80)
            Blade(b.Gcd("Confiteor", l94 ? 500 : 420, AidConfiteor).Shape(AoeShape.TargetCircle, 5, 0, 25).PotencyIfStatus(Requiescat, l94 ? 1000 : 920).AoeFalloff(l94 ? 500 * Splash : 420 * Splash), ConfiteorReady, level >= 90 ? FaithReady : null);
        if (level >= 90)
        {
            Blade(b.Gcd("BladeOfFaith", l94 ? 260 : 220, AidBladeOfFaith).Shape(AoeShape.TargetCircle, 5, 0, 25).PotencyIfStatus(Requiescat, l94 ? 760 : 720).AoeFalloff(l94 ? 260 * Splash : 220 * Splash), FaithReady, TruthReady);
            Blade(b.Gcd("BladeOfTruth", l94 ? 380 : 320, AidBladeOfTruth).Shape(AoeShape.TargetCircle, 5, 0, 25).PotencyIfStatus(Requiescat, l94 ? 880 : 820).AoeFalloff(l94 ? 380 * Splash : 320 * Splash), TruthReady, ValorReady);
            Blade(b.Gcd("BladeOfValor", l94 ? 500 : 420, AidBladeOfValor).Shape(AoeShape.TargetCircle, 5, 0, 25).PotencyIfStatus(Requiescat, l94 ? 1000 : 920).AoeFalloff(l94 ? 500 * Splash : 420 * Splash), ValorReady, level >= 100 ? HonorReady : null);
        }
        if (level >= 54)
            b.Gcd("GoringBlade", 700, AidGoringBlade).RequiresStatus(GoringReady).RemoveStatus(GoringReady);

        // ---- abilities ----
        var fof = b.Ogcd("FightOrFlight", 0, FightOrFlightCD, AidFightOrFlight).NeedsUptime(2).ApplyStatus(FightOrFlight, 20);
        if (level >= 54)
            fof.ApplyStatus(GoringReady, 30);
        if (level >= 96)
            b.Ogcd("Imperator", 580, ImperatorCD, AidImperator).Shape(AoeShape.TargetCircle, 5, 0, 25).AoeFalloff(580 * Splash).ForbidStatus(HonorReady).ApplyStatus(Requiescat, 30, 4).ApplyStatus(ConfiteorReady, 30);
        else if (level >= 68)
        {
            var req = b.Ogcd("Imperator", 320, ImperatorCD, AidRequiescat).Shape(AoeShape.TargetCircle, 5, 0, 25).ApplyStatus(Requiescat, 30, 4);
            if (level >= 80)
                req.ApplyStatus(ConfiteorReady, 30);
        }
        if (level >= 100)
            b.Ogcd("BladeOfHonor", 1000, null, AidBladeOfHonor).Shape(AoeShape.TargetCircle, 5, 0, 25).AoeFalloff(1000 * Splash).RequiresStatus(HonorReady).RemoveStatus(HonorReady);
        if (level >= 86)
            b.Ogcd("Expiacion", 450, ExpiacionCD, AidExpiacion).Shape(AoeShape.TargetCircle, 5, 0, 3).AoeFalloff(450 * Splash).GainGauge(MP, 500);
        else if (level >= 30)
        {
            var spirits = b.Ogcd("Expiacion", 270, ExpiacionCD, AidSpiritsWithin).Shape(AoeShape.TargetCircle, 5, 0, 3);
            if (level >= 58)
                spirits.GainGauge(MP, 500);
        }
        if (level >= 50)
            b.Ogcd("CircleOfScorn", 290, CircleOfScornCD, AidCircleOfScorn).Shape(AoeShape.SelfCircle, 5, 0, 5).AoeFalloff(290);
        return b.Build();
    }

    // next: the status for the following step of the chain (null: the chain ends here at this level)
    private static void Blade(JobBuilder.SkillBuilder s, string ready, string? next)
    {
        s.RequiresStatus(ready).RemoveStatus(ready).SpendGauge(MP, 1000).IfStatus(Requiescat).UseStack(Requiescat);
        if (next != null)
            s.ApplyStatus(next, 30);
    }

    // level sync: the set tuned at the lowest level of the player's band (80-89 L80, 70-79 L70); the Lv100 set at 100 and in the other bands
    public static EngineWeights DefaultWeights(int level = 100) => EngineWeights.Parse(level switch
    {
        >= 90 => DefaultWeightsJson,
        >= 80 => WeightsL80Json,
        >= 70 => WeightsL70Json,
        _ => DefaultWeightsJson
    });

    // CMA-ES on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-PLD-v1.json, at horizon 4; the
    // same weights score higher at a 6-GCD horizon, so no retune). MinNodes / SliceNodes: every search (at most about 38,700 nodes in the 9 fights)
    // completes and frame slices are counted in nodes, so live play equals the deterministic search; BudgetMs only caps a search larger than MinNodes.
    public const string DefaultWeightsJson = """
    {
      "OverCap": 1.4915171, "Combo": 0.59287137, "LambdaScale": 0.7683747, "TargetPull": 0, "SwitchMargin": 27.52398, "FillerScale": 1.1636959, "BurstBias": 0.60006064,
      "StatusRemainder": 0.5540458, "CycleScale": 1, "CooldownLambdaScale": 1.1999441, "ForecastSelfBuffs": -0.9779256, "UnlockScale": 1.0974472,
      "StatusValue": {}, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 6,
      "BudgetMs": 100,
      "MinNodes": 46000,
      "SliceNodes": 3880
    }
    """;

    // CMA-ES at level 80 on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-PLD-L80.json),
    // search settings as the Lv100 set
    public const string WeightsL80Json = """
    {
      "OverCap": 1.7296668, "Combo": 0, "LambdaScale": 0.98177767, "TargetPull": 0, "SwitchMargin": 0, "FillerScale": 0.9313585, "BurstBias": 0,
      "StatusRemainder": 1.0600373, "CycleScale": 1, "CooldownLambdaScale": 1.1790073, "ForecastSelfBuffs": -0.9779256, "UnlockScale": 1.345213,
      "StatusValue": {}, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8
    }
    """;

    // CMA-ES at level 70 on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-PLD-L70.json),
    // search settings as the Lv100 set
    public const string WeightsL70Json = """
    {
      "OverCap": 2.6594245, "Combo": 0.8553889, "LambdaScale": 1, "TargetPull": 0, "SwitchMargin": 1.6818128, "FillerScale": 1.5, "BurstBias": 0.49591658,
      "StatusRemainder": 2, "CycleScale": 1, "CooldownLambdaScale": 0, "ForecastSelfBuffs": -0.9779256, "UnlockScale": 3,
      "StatusValue": {}, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8
    }
    """;
}
