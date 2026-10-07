namespace BossMod.Autorotation.Engine.Jobs;

// Paladin (level 100) for the rotation engine: data only, no BossMod dependency (also compiled by the engine tools).
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
        AidExpiacion = 25747, AidCircleOfScorn = 23;

    public static JobDefinition Build(float gcd = 2.5f)
    {
        var holyCast = 1.5f * gcd / 2.5f;
        var b = new JobBuilder("PLD", gcd)
            .Gauge(MP, 10000, flat: true)
            .Status(FightOrFlight, 20, damageMultiplier: 1.25f).Status(GoringReady, 30).Status(Requiescat, 30, maxStacks: 4).Status(ConfiteorReady, 30)
            .Status(FaithReady, 30).Status(TruthReady, 30).Status(ValorReady, 30).Status(HonorReady, 30)
            .Status(AtonementReady, 30).Status(SupplicationReady, 30).Status(SepulchreReady, 30).Status(DivineMight, 30)
            .Cooldown(FightOrFlightCD, 60).Cooldown(ImperatorCD, 60).Cooldown(ExpiacionCD, 30).Cooldown(CircleOfScornCD, 30);

        // ---- combo ----
        b.Gcd("FastBlade", 220, AidFastBlade).StartsCombo();
        b.Gcd("RiotBlade", 170, AidRiotBlade).ComboFrom("FastBlade", 330).IfCombo("FastBlade").GainGauge(MP, 1000);
        b.Gcd("RoyalAuthority", 200, AidRoyalAuthority).ComboFrom("RiotBlade", 460).EndsCombo()
            .IfCombo("RiotBlade").ApplyStatus(DivineMight, 30).IfCombo("RiotBlade").ApplyStatus(AtonementReady, 30)
            .IfCombo("RiotBlade").RemoveStatus(SupplicationReady).IfCombo("RiotBlade").RemoveStatus(SepulchreReady);
        b.Gcd("TotalEclipse", 120, AidTotalEclipse).AoeFalloff(120).StartsCombo();
        b.Gcd("Prominence", 100, AidProminence).AoeFalloff(100).ComboFrom("TotalEclipse", 220).EndsCombo()
            .IfCombo("TotalEclipse").ApplyStatus(DivineMight, 30).IfCombo("TotalEclipse").GainGauge(MP, 1000);

        // ---- Sword Oath chain ----
        b.Gcd("Atonement", 460, AidAtonement).RequiresStatus(AtonementReady).RemoveStatus(AtonementReady).ApplyStatus(SupplicationReady, 30).GainGauge(MP, 400);
        b.Gcd("Supplication", 500, AidSupplication).RequiresStatus(SupplicationReady).RemoveStatus(SupplicationReady).ApplyStatus(SepulchreReady, 30).GainGauge(MP, 400);
        b.Gcd("Sepulchre", 540, AidSepulchre).RequiresStatus(SepulchreReady).RemoveStatus(SepulchreReady).GainGauge(MP, 400);

        // ---- Holy Spirit / Holy Circle (Divine Might first, then a Requiescat stack, else a cast) ----
        b.Gcd("HolySpiritDM", 500, AidHolySpirit).RequiresStatus(DivineMight).RemoveStatus(DivineMight).SpendGauge(MP, 1000);
        b.Gcd("HolySpiritReq", 700, AidHolySpirit).ForbidStatus(DivineMight).RequiresStatus(Requiescat).UseStack(Requiescat).SpendGauge(MP, 1000);
        b.Gcd("HolySpirit", 400, AidHolySpirit).Cast(holyCast).ForbidStatus(DivineMight).ForbidStatus(Requiescat).SpendGauge(MP, 1000);
        b.Gcd("HolyCircleDM", 250, AidHolyCircle).AoeFalloff(250).RequiresStatus(DivineMight).RemoveStatus(DivineMight).SpendGauge(MP, 1000);
        b.Gcd("HolyCircleReq", 350, AidHolyCircle).AoeFalloff(350).ForbidStatus(DivineMight).RequiresStatus(Requiescat).UseStack(Requiescat).SpendGauge(MP, 1000);

        // ---- Confiteor chain (each step uses a Requiescat stack when there is one) ----
        Blade(b.Gcd("Confiteor", 500, AidConfiteor).PotencyIfStatus(Requiescat, 1000).AoeFalloff(500 * Splash), ConfiteorReady, FaithReady);
        Blade(b.Gcd("BladeOfFaith", 260, AidBladeOfFaith).PotencyIfStatus(Requiescat, 760).AoeFalloff(260 * Splash), FaithReady, TruthReady);
        Blade(b.Gcd("BladeOfTruth", 380, AidBladeOfTruth).PotencyIfStatus(Requiescat, 880).AoeFalloff(380 * Splash), TruthReady, ValorReady);
        Blade(b.Gcd("BladeOfValor", 500, AidBladeOfValor).PotencyIfStatus(Requiescat, 1000).AoeFalloff(500 * Splash), ValorReady, HonorReady);
        b.Gcd("GoringBlade", 700, AidGoringBlade).RequiresStatus(GoringReady).RemoveStatus(GoringReady);

        // ---- abilities ----
        b.Ogcd("FightOrFlight", 0, FightOrFlightCD, AidFightOrFlight).NeedsUptime(2).ApplyStatus(FightOrFlight, 20).ApplyStatus(GoringReady, 30);
        b.Ogcd("Imperator", 580, ImperatorCD, AidImperator).AoeFalloff(580 * Splash).ForbidStatus(HonorReady).ApplyStatus(Requiescat, 30, 4).ApplyStatus(ConfiteorReady, 30);
        b.Ogcd("BladeOfHonor", 1000, null, AidBladeOfHonor).AoeFalloff(1000 * Splash).RequiresStatus(HonorReady).RemoveStatus(HonorReady);
        b.Ogcd("Expiacion", 450, ExpiacionCD, AidExpiacion).AoeFalloff(450 * Splash).GainGauge(MP, 500);
        b.Ogcd("CircleOfScorn", 290, CircleOfScornCD, AidCircleOfScorn).AoeFalloff(290);
        return b.Build();
    }

    private static void Blade(JobBuilder.SkillBuilder s, string ready, string next) => s
        .RequiresStatus(ready).RemoveStatus(ready).SpendGauge(MP, 1000).IfStatus(Requiescat).UseStack(Requiescat).ApplyStatus(next, 30);

    public static EngineWeights DefaultWeights() => EngineWeights.Parse(DefaultWeightsJson);

    // CMA-ES on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-PLD-v1.json), BudgetMs for live play
    public const string DefaultWeightsJson = """
    {
      "OverCap": 1.4915171, "Combo": 0.59287137, "LambdaScale": 0.7683747, "TargetPull": 0, "SwitchMargin": 27.52398, "FillerScale": 1.1636959, "BurstBias": 0.60006064,
      "StatusRemainder": 0.5540458, "CycleScale": 1, "CooldownLambdaScale": 1.1999441, "ForecastSelfBuffs": -0.9779256, "UnlockScale": 1.0974472,
      "StatusValue": {}, "CooldownValue": {}, "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 0.8
    }
    """;
}
