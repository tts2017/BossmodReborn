namespace BossMod.Autorotation.Engine.Jobs;

// Reaper (level 100) for the rotation engine: data only, no BossMod dependency (also compiled by tools/rpr_engine_eval).
// Skill names match the RPR harness action names (tools/rpr_regression) so its scenarios can drive the engine directly.
// Potencies follow the harness (and live game values at level 100 where the harness has none).
public static class RprDefinition
{
    // status names (also used by the state readers)
    public const string DeathsDesign = "DeathsDesign";
    public const string ArcaneCircle = "ArcaneCircle";
    public const string SoulReaver = "SoulReaver";
    public const string Executioner = "Executioner";
    public const string EnhancedGibbet = "EnhancedGibbet";
    public const string EnhancedGallows = "EnhancedGallows";
    public const string EnhancedVoid = "EnhancedVoidReaping";
    public const string EnhancedCross = "EnhancedCrossReaping";
    public const string Enshrouded = "Enshrouded";
    public const string Oblatio = "Oblatio";
    public const string IdealHost = "IdealHost";
    public const string PerfectioOcculta = "PerfectioOcculta";
    public const string PerfectioParata = "PerfectioParata";
    public const string ImmortalSacrifice = "ImmortalSacrifice";
    public const string Bloodsown = "BloodsownCircle";
    public const string Soulsow = "Soulsow";
    public const string Medicated = "Medicated";
    public const string EnshroudEnding = "EnshroudEnding"; // harness rule: no Soul spender in the weave right after the Enshroud ends

    // gauges
    public const string Soul = "Soul";
    public const string Shroud = "Shroud";
    public const string Lemure = "Lemure";
    public const string Void = "Void";

    // cooldown groups
    public const string SoulSliceCD = "SoulSliceCD";
    public const string ArcaneCircleCD = "ArcaneCircleCD";
    public const string GluttonyCD = "GluttonyCD";
    public const string EnshroudCD = "EnshroudCD";
    public const string PotionCD = "PotionCD";

    private static readonly string[] ReaverStatuses = [SoulReaver, Executioner];

    public static JobDefinition Build(float gcd = 2.5f)
    {
        var b = new JobBuilder("RPR", gcd)
            .Gauge(Soul, 100).Gauge(Shroud, 100).Gauge(Lemure, 5).Gauge(Void, 5)
            .Status(DeathsDesign, 60, damageMultiplier: 1.1f, upkeep: true)
            .Status(ArcaneCircle, 20, damageMultiplier: 1.03f)
            .Status(SoulReaver, 30, maxStacks: 2)
            .Status(Executioner, 30, maxStacks: 2)
            .Status(EnhancedGibbet, 60).Status(EnhancedGallows, 60)
            .Status(EnhancedVoid, 30).Status(EnhancedCross, 30)
            .Status(Enshrouded, 30)
            .Status(Oblatio, 30)
            .Status(IdealHost, 30)
            .Status(PerfectioOcculta, 30)
            .Status(PerfectioParata, 30)
            .Status(ImmortalSacrifice, 30, maxStacks: 8)
            .Status(Bloodsown, 6)
            .Status(Soulsow, 3600)
            .Status(Medicated, 30, damageMultiplier: 1.05f)
            .Status(EnshroudEnding, 1.2f)
            .Cooldown(SoulSliceCD, 30, maxCharges: 2)
            .Cooldown(ArcaneCircleCD, 120)
            .Cooldown(GluttonyCD, 60)
            .Cooldown(EnshroudCD, 5)
            .Cooldown(PotionCD, 270);

        // --- combo and fillers (blocked under Soul Reaver / Executioner and while Enshrouded: the game refuses them there) ---
        b.Gcd("Slice", 420, 24373).StartsCombo().GainGauge(Soul, 10).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded)
            .Gcd("WaxingSlice", 200, 24374).ComboFrom("Slice", 500).IfCombo("Slice").GainGauge(Soul, 10).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded)
            .Gcd("InfernalSlice", 200, 24375).ComboFrom("WaxingSlice", 600).IfCombo("WaxingSlice").GainGauge(Soul, 10).EndsCombo().ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded)
            .Gcd("SpinningScythe", 0, 24376).Aoe(140, 3).RequiresTargets(3).StartsCombo().GainGauge(Soul, 10).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded)
            .Gcd("NightmareScythe", 0, 24377).Aoe(180, 3).RequiresTargets(3).RequiresCombo("SpinningScythe").GainGauge(Soul, 10).EndsCombo().ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded)
            .Gcd("ShadowOfDeath", 300, 24378).ComboNeutral().ForbidStatus(Enshrouded).ApplyStatus(DeathsDesign, 30, extend: true).ForbidStatuses(ReaverStatuses)
            .Gcd("WhorlOfDeath", 0, 24379).Aoe(100, 3).RequiresTargets(3).ComboNeutral().ForbidStatus(Enshrouded).ApplyStatus(DeathsDesign, 30, extend: true).ForbidStatuses(ReaverStatuses)
            .Gcd("SoulSlice", 520, 24380).UsesCooldown(SoulSliceCD).RequiresGaugeAtMost(Soul, 50).ComboNeutral().GainGauge(Soul, 50).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded)
            .Gcd("SoulScythe", 0, 24381).Aoe(180, 3).RequiresTargets(3).UsesCooldown(SoulSliceCD).RequiresGaugeAtMost(Soul, 50).ComboNeutral().GainGauge(Soul, 50).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded)
            .Gcd("HarvestMoon", 800, 24388).ComboNeutral().RequiresStatus(Soulsow).RemoveStatus(Soulsow).GainGauge(Soul, 10).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded)
            .Gcd("Soulsow", 0, 24387).NoTarget().Cast(5).ComboNeutral().ForbidStatus(Soulsow).ForbidStatuses(ReaverStatuses).ApplyStatus(Soulsow, 3600)

            // --- Soul Reaver / Executioner GCDs ---
            .Gcd("Gibbet", 500, 24382).ComboNeutral().ConsumeStacks(SoulReaver).ForbidStatus(EnhancedGallows).PotencyIfStatus(EnhancedGibbet, 560)
                .RemoveStatus(EnhancedGibbet).ApplyStatus(EnhancedGallows, 60).GainGauge(Shroud, 10)
            .Gcd("Gallows", 500, 24383).ComboNeutral().ConsumeStacks(SoulReaver).ForbidStatus(EnhancedGibbet).PotencyIfStatus(EnhancedGallows, 560)
                .RemoveStatus(EnhancedGallows).ApplyStatus(EnhancedGibbet, 60).GainGauge(Shroud, 10)
            .Gcd("Guillotine", 0, 24384).Aoe(200, 4).Cone().RequiresTargets(4).ComboNeutral().ConsumeStacks(SoulReaver).GainGauge(Shroud, 10)
            .Gcd("ExecutionersGibbet", 700, 36970).ComboNeutral().ConsumeStacks(Executioner).ForbidStatus(EnhancedGallows).PotencyIfStatus(EnhancedGibbet, 760)
                .RemoveStatus(EnhancedGibbet).ApplyStatus(EnhancedGallows, 60).GainGauge(Shroud, 10)
            .Gcd("ExecutionersGallows", 700, 36971).ComboNeutral().ConsumeStacks(Executioner).ForbidStatus(EnhancedGibbet).PotencyIfStatus(EnhancedGallows, 760)
                .RemoveStatus(EnhancedGallows).ApplyStatus(EnhancedGibbet, 60).GainGauge(Shroud, 10)
            .Gcd("ExecutionersGuillotine", 0, 36972).Aoe(260, 4).Cone().RequiresTargets(4).ComboNeutral().ConsumeStacks(Executioner).GainGauge(Shroud, 10)

            // --- Enshroud (1.5 s reapings while 2+ Lemure remain; the last one is always Communio, which ends it) ---
            .Gcd("VoidReaping", 580, 24395).Recast(1.5f).ComboNeutral().RequiresStatus(Enshrouded).RequiresGauge(Lemure, 2).PotencyIfStatus(EnhancedVoid, 640)
                .GainGauge(Lemure, -1).GainGauge(Void, 1).RemoveStatus(EnhancedVoid).ApplyStatus(EnhancedCross, 30)
                .IfGaugeAtMost(Lemure, 0).RemoveStatus(Enshrouded).IfGaugeAtMost(Lemure, 0).GainGauge(Void, -5).IfGaugeAtMost(Lemure, 0).RemoveStatus(Oblatio).IfGaugeAtMost(Lemure, 0).ApplyStatus(EnshroudEnding, 1.2f)
            .Gcd("CrossReaping", 580, 24396).Recast(1.5f).ComboNeutral().RequiresStatus(Enshrouded).RequiresGauge(Lemure, 2).PotencyIfStatus(EnhancedCross, 640)
                .GainGauge(Lemure, -1).GainGauge(Void, 1).RemoveStatus(EnhancedCross).ApplyStatus(EnhancedVoid, 30)
                .IfGaugeAtMost(Lemure, 0).RemoveStatus(Enshrouded).IfGaugeAtMost(Lemure, 0).GainGauge(Void, -5).IfGaugeAtMost(Lemure, 0).RemoveStatus(Oblatio).IfGaugeAtMost(Lemure, 0).ApplyStatus(EnshroudEnding, 1.2f)
            .Gcd("GrimReaping", 0, 24397).Aoe(220, 3).Cone().RequiresTargets(3).Recast(1.5f).ComboNeutral().RequiresStatus(Enshrouded).RequiresGauge(Lemure, 2)
                .GainGauge(Lemure, -1).GainGauge(Void, 1)
                .IfGaugeAtMost(Lemure, 0).RemoveStatus(Enshrouded).IfGaugeAtMost(Lemure, 0).GainGauge(Void, -5).IfGaugeAtMost(Lemure, 0).RemoveStatus(Oblatio).IfGaugeAtMost(Lemure, 0).ApplyStatus(EnshroudEnding, 1.2f)
            .Gcd("Communio", 1100, 24398).Cast(1.3f).ComboNeutral().RequiresStatus(Enshrouded).RequiresGauge(Lemure, 1).RequiresGaugeAtMost(Lemure, 1)
                .GainGauge(Lemure, -5).GainGauge(Void, -5).RemoveStatuses(Enshrouded, Oblatio).ApplyStatus(EnshroudEnding, 1.2f)
                .IfStatus(PerfectioOcculta).ApplyStatus(PerfectioParata, 30).RemoveStatus(PerfectioOcculta)
            .Gcd("Perfectio", 1300, 36973).ComboNeutral().ConsumeStacks(PerfectioParata).ForbidStatuses(ReaverStatuses)
            .Gcd("PlentifulHarvest", 1000, 24385).ComboNeutral().RequiresStatus(ImmortalSacrifice).ForbidStatuses(Bloodsown, SoulReaver, Executioner, Enshrouded)
                .RemoveStatus(ImmortalSacrifice).ApplyStatus(IdealHost, 30).ApplyStatus(PerfectioOcculta, 30)

            // --- oGCDs --- (Soul spenders need enough Death's Design left for the reaver GCDs that follow: Shadow of Death cannot interrupt them)
            // Arcane Circle and the potion first: weaves of one window are searched in definition order, and the burst opens with AC
            .Ogcd("ArcaneCircle", 0, ArcaneCircleCD, 24405).NoTarget().PartyValue(0)
                .ApplyStatus(ArcaneCircle, 20).ApplyStatus(ImmortalSacrifice, 30, stacks: 8).ApplyStatus(Bloodsown, 6)
            .Ogcd("Potion", 0, PotionCD).NoTarget().RequiresStatus(ArcaneCircle).ApplyStatus(Medicated, 30)
            .Ogcd("BloodStalk", 340, actionId: 24389).RequiresStatusLeft(DeathsDesign, 3.5f).SpendGauge(Soul, 50).ForbidStatuses(Enshrouded, SoulReaver, Executioner, EnshroudEnding)
                .PotencyIfStatus(EnhancedGibbet, 440).PotencyIfStatus(EnhancedGallows, 440).ApplyStatus(SoulReaver, 30)
            .Ogcd("GrimSwathe", 0, actionId: 24392).RequiresStatusLeft(DeathsDesign, 3.5f).Aoe(140, 3).Cone().RequiresTargets(3).SpendGauge(Soul, 50).ForbidStatuses(Enshrouded, SoulReaver, Executioner, EnshroudEnding).ApplyStatus(SoulReaver, 30)
            .Ogcd("Gluttony", 560, GluttonyCD, 24393).RequiresStatusLeft(DeathsDesign, 6).SpendGauge(Soul, 50).ForbidStatuses(Enshrouded, SoulReaver, Executioner, EnshroudEnding).ApplyStatus(Executioner, 30, stacks: 2)
            .Ogcd("Enshroud", 0, EnshroudCD, 24394).NoTarget().RequiresStatusLeft(DeathsDesign, 13).SpendGauge(Shroud, 50).ForbidStatuses(IdealHost, Enshrouded, SoulReaver, Executioner)
                .GainGauge(Lemure, 5).ApplyStatus(Enshrouded, 30).ApplyStatus(Oblatio, 30).RemoveStatus(PerfectioParata)
            .Ogcd("EnshroudIdeal", 0, EnshroudCD, 24394).NoTarget().RequiresStatusLeft(DeathsDesign, 13).RequiresStatus(IdealHost).ForbidStatuses(Enshrouded, SoulReaver, Executioner)
                .RemoveStatus(IdealHost).GainGauge(Lemure, 5).ApplyStatus(Enshrouded, 30).ApplyStatus(Oblatio, 30).RemoveStatus(PerfectioParata)
            .Ogcd("LemuresSlice", 280, actionId: 24399).RequiresStatus(Enshrouded).SpendGauge(Void, 2)
            .Ogcd("LemuresScythe", 0, actionId: 24400).Aoe(100, 3).Cone().RequiresTargets(3).RequiresStatus(Enshrouded).SpendGauge(Void, 2)
            .Ogcd("Sacrificium", 700, actionId: 36969).ConsumeStacks(Oblatio)
            ;
        return b.Build();
    }

    // Harness action name for an engine skill (the engine splits a few actions by state)
    public static string HarnessName(SkillDef skill, in EngineState s, JobDefinition job) => skill.Name switch
    {
        "EnshroudIdeal" => "Enshroud",
        "BloodStalk" when s.HasStatus(job.StatusIndex(EnhancedGibbet)) => "UnveiledGibbet",
        "BloodStalk" when s.HasStatus(job.StatusIndex(EnhancedGallows)) => "UnveiledGallows",
        _ => skill.Name
    };

    public static EngineWeights DefaultWeights() => EngineWeights.Parse(DefaultWeightsJson);

    // CMA-ES result, second run with widened ranges (tools/rpr_engine_eval tune --real 300 --limit 80 --generations 10 --seed 2 --depth 4,
    // started from the first run; harness fitness 435.8 -> 462.7). LambdaScale and FillerScale settle at their lower bound (0.05): the
    // shadow prices and horizon filler matter little once the definition carries the RPR rules; kept small rather than removed.
    public const string DefaultWeightsJson = """
    {
      "OverCap": 2.0607274, "Combo": 1.0372564, "LambdaScale": 0.05, "TargetPull": 0, "SwitchMargin": 33.370934,
      "FillerScale": 0.05, "BurstBias": 2.5960407, "StatusRemainder": 1.0677418,
      "StatusValue": { "DeathsDesign": 0 }, "CooldownValue": { "GluttonyCD": 727.738 }, "GaugeValue": { "Shroud": -188.75427 },
      "HorizonGcds": 4, "BudgetMs": 0.5
    }
    """;
}
