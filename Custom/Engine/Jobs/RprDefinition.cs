namespace BossMod.Autorotation.Engine.Jobs;

// Reaper for the rotation engine (any level; the weights were tuned at level 100): data only, no BossMod dependency (also compiled by tools/rpr_engine_eval).
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

    // level: the player's (synced) level. Skills not unlocked there are left out; potencies, recasts, charges and effects follow its
    // traits (potency branches as tools/xan_timeline_harness/RprPotencyScorer.cs: m1 = level 74, m2 = 84, m3 = 94).
    public static JobDefinition Build(float gcd = 2.5f, int level = 100)
    {
        var m1 = level >= 74;
        var m2 = level >= 84;
        var m3 = level >= 94;
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
            .Cooldown(SoulSliceCD, 30, maxCharges: level >= 78 ? 2 : 1) // Tempered Soul (78)
            .Cooldown(ArcaneCircleCD, 120)
            .Cooldown(GluttonyCD, 60)
            .Cooldown(EnshroudCD, 5) // 5 s at every level since patch 7.3 (Enhanced Enshroud at 92 only grants Oblatio)
            .Cooldown(PotionCD, 270);
        // Gluttony grants Executioner from 96 (Enhanced Gluttony), Soul Reaver before
        var gluttonyReaver = level >= 96 ? Executioner : SoulReaver;
        // Communio (90) takes the last Lemure, so the reapings need 2 left; before it every Lemure is a reaping and the fifth one (1 left
        // before it) ends the Enshroud
        var reapingLemure = level >= 90 ? 2 : 1;
        var lastLemure = level >= 90 ? 0 : 1;

        // --- combo and fillers (blocked under Soul Reaver / Executioner and while Enshrouded: the game refuses them there) ---
        b.Gcd("Slice", m3 ? 420 : m2 ? 320 : m1 ? 300 : 240, 24373).StartsCombo().GainGauge(Soul, 10).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded);
        if (level >= 5)
            b.Gcd("WaxingSlice", 200, 24374).ComboFrom("Slice", m3 ? 500 : m2 ? 400 : m1 ? 380 : 300).IfCombo("Slice").GainGauge(Soul, 10).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded);
        if (level >= 30)
            b.Gcd("InfernalSlice", 200, 24375).ComboFrom("WaxingSlice", m3 ? 600 : m2 ? 500 : m1 ? 460 : 400).IfCombo("WaxingSlice").GainGauge(Soul, 10).EndsCombo().ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded);
        if (level >= 25)
            b.Gcd("SpinningScythe", 0, 24376).Shape(AoeShape.SelfCircle, 5, 0, 3).Aoe(m1 ? 140 : 100, 3).RequiresTargets(3).StartsCombo().GainGauge(Soul, 10).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded);
        if (level >= 45)
            b.Gcd("NightmareScythe", 0, 24377).Shape(AoeShape.SelfCircle, 5, 0, 3).Aoe(m1 ? 180 : 140, 3).RequiresTargets(3).RequiresCombo("SpinningScythe").GainGauge(Soul, 10).EndsCombo().ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded);
        if (level >= 10)
            b.Gcd("ShadowOfDeath", 300, 24378).ComboNeutral().ForbidStatus(Enshrouded).ApplyStatus(DeathsDesign, 30, extend: true).ForbidStatuses(ReaverStatuses);
        if (level >= 35)
            b.Gcd("WhorlOfDeath", 0, 24379).Shape(AoeShape.SelfCircle, 5, 0, 3).Aoe(100, 3).RequiresTargets(3).ComboNeutral().ForbidStatus(Enshrouded).ApplyStatus(DeathsDesign, 30, extend: true).ForbidStatuses(ReaverStatuses);
        if (level >= 60)
            b.Gcd("SoulSlice", m3 ? 520 : 460, 24380).UsesCooldown(SoulSliceCD).RequiresGaugeAtMost(Soul, 50).ComboNeutral().GainGauge(Soul, 50).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded);
        if (level >= 65)
            b.Gcd("SoulScythe", 0, 24381).Shape(AoeShape.SelfCircle, 5, 0, 3).Aoe(180, 3).RequiresTargets(3).UsesCooldown(SoulSliceCD).RequiresGaugeAtMost(Soul, 50).ComboNeutral().GainGauge(Soul, 50).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded);
        if (level >= 82)
            b.Gcd("HarvestMoon", m3 ? 800 : 600, 24388).ComboNeutral().RequiresStatus(Soulsow).RemoveStatus(Soulsow).GainGauge(Soul, 10).ForbidStatuses(ReaverStatuses).ForbidStatus(Enshrouded)
                .Gcd("Soulsow", 0, 24387).NoTarget().Cast(5).ComboNeutral().ForbidStatus(Soulsow).ForbidStatuses(ReaverStatuses).ApplyStatus(Soulsow, 3600);

        // --- Soul Reaver / Executioner GCDs (Shroud from 80) ---
        if (level >= 70)
        {
            var gibbet = b.Gcd("Gibbet", 500, 24382).ComboNeutral().ConsumeStacks(SoulReaver).ForbidStatus(EnhancedGallows).PotencyIfStatus(EnhancedGibbet, 560)
                .RemoveStatus(EnhancedGibbet).ApplyStatus(EnhancedGallows, 60);
            var gallows = b.Gcd("Gallows", 500, 24383).ComboNeutral().ConsumeStacks(SoulReaver).ForbidStatus(EnhancedGibbet).PotencyIfStatus(EnhancedGallows, 560)
                .RemoveStatus(EnhancedGallows).ApplyStatus(EnhancedGibbet, 60);
            var guillotine = b.Gcd("Guillotine", 0, 24384).Shape(AoeShape.Cone, 8, 90, 8).Aoe(200, 4).RequiresTargets(4).ComboNeutral().ConsumeStacks(SoulReaver);
            if (level >= 80)
            {
                gibbet.GainGauge(Shroud, 10);
                gallows.GainGauge(Shroud, 10);
                guillotine.GainGauge(Shroud, 10);
            }
        }
        if (level >= 96)
            b.Gcd("ExecutionersGibbet", 700, 36970).ComboNeutral().ConsumeStacks(Executioner).ForbidStatus(EnhancedGallows).PotencyIfStatus(EnhancedGibbet, 760)
                    .RemoveStatus(EnhancedGibbet).ApplyStatus(EnhancedGallows, 60).GainGauge(Shroud, 10)
                .Gcd("ExecutionersGallows", 700, 36971).ComboNeutral().ConsumeStacks(Executioner).ForbidStatus(EnhancedGibbet).PotencyIfStatus(EnhancedGallows, 760)
                    .RemoveStatus(EnhancedGallows).ApplyStatus(EnhancedGibbet, 60).GainGauge(Shroud, 10)
                .Gcd("ExecutionersGuillotine", 0, 36972).Shape(AoeShape.Cone, 8, 90, 8).Aoe(260, 4).RequiresTargets(4).ComboNeutral().ConsumeStacks(Executioner).GainGauge(Shroud, 10);

        // --- Enshroud (1.5 s reapings while 2+ Lemure remain; the last one is always Communio, which ends it; Void Shroud from 86) ---
        if (level >= 80)
        {
            var void_ = b.Gcd("VoidReaping", m3 ? 580 : 500, 24395).Recast(1.5f).ComboNeutral().RequiresStatus(Enshrouded).RequiresGauge(Lemure, reapingLemure).PotencyIfStatus(EnhancedVoid, m3 ? 640 : 560)
                .GainGauge(Lemure, -1);
            var cross = b.Gcd("CrossReaping", m3 ? 580 : 500, 24396).Recast(1.5f).ComboNeutral().RequiresStatus(Enshrouded).RequiresGauge(Lemure, reapingLemure).PotencyIfStatus(EnhancedCross, m3 ? 640 : 560)
                .GainGauge(Lemure, -1);
            var grim = b.Gcd("GrimReaping", 0, 24397).Shape(AoeShape.Cone, 8, 90, 8).Aoe(220, 3).RequiresTargets(3).Recast(1.5f).ComboNeutral().RequiresStatus(Enshrouded).RequiresGauge(Lemure, reapingLemure)
                .GainGauge(Lemure, -1);
            if (level >= 86)
            {
                void_.GainGauge(Void, 1);
                cross.GainGauge(Void, 1);
                grim.GainGauge(Void, 1);
            }
            void_.RemoveStatus(EnhancedVoid).ApplyStatus(EnhancedCross, 30);
            cross.RemoveStatus(EnhancedCross).ApplyStatus(EnhancedVoid, 30);
            foreach (var r in new[] { void_, cross, grim })
                r.IfGaugeAtMost(Lemure, lastLemure).RemoveStatus(Enshrouded).IfGaugeAtMost(Lemure, lastLemure).GainGauge(Void, -5)
                    .IfGaugeAtMost(Lemure, lastLemure).RemoveStatus(Oblatio).IfGaugeAtMost(Lemure, lastLemure).ApplyStatus(EnshroudEnding, 1.2f);
        }
        if (level >= 90)
            b.Gcd("Communio", 1100, 24398).Cast(1.3f).ComboNeutral().RequiresStatus(Enshrouded).RequiresGauge(Lemure, 1).RequiresGaugeAtMost(Lemure, 1)
                .GainGauge(Lemure, -5).GainGauge(Void, -5).RemoveStatuses(Enshrouded, Oblatio).ApplyStatus(EnshroudEnding, 1.2f)
                .IfStatus(PerfectioOcculta).ApplyStatus(PerfectioParata, 30).RemoveStatus(PerfectioOcculta);
        if (level >= 100)
            b.Gcd("Perfectio", 1300, 36973).ComboNeutral().ConsumeStacks(PerfectioParata).ForbidStatuses(ReaverStatuses);
        if (level >= 88)
        {
            var harvest = b.Gcd("PlentifulHarvest", 1000, 24385).Shape(AoeShape.Line, 15, 2, 15).ComboNeutral().RequiresStatus(ImmortalSacrifice).ForbidStatuses(Bloodsown, SoulReaver, Executioner, Enshrouded)
                .RemoveStatus(ImmortalSacrifice).ApplyStatus(IdealHost, 30);
            if (level >= 100)
                harvest.ApplyStatus(PerfectioOcculta, 30);
        }

        // --- oGCDs --- (Soul spenders need enough Death's Design left for the reaver GCDs that follow: Shadow of Death cannot interrupt them)
        // Arcane Circle and the potion first: weaves of one window are searched in definition order, and the burst opens with AC
        if (level >= 72)
        {
            var circle = b.Ogcd("ArcaneCircle", 0, ArcaneCircleCD, 24405).NoTarget().PartyValue(0).ApplyStatus(ArcaneCircle, 20);
            if (level >= 88)
                circle.ApplyStatus(ImmortalSacrifice, 30, stacks: 8).ApplyStatus(Bloodsown, 6);
        }
        b.Ogcd("Potion", 0, PotionCD).NoTarget().RequiresStatus(ArcaneCircle).ApplyStatus(Medicated, 30);
        if (level >= 50)
        {
            var stalk = b.Ogcd("BloodStalk", 340, actionId: 24389).RequiresStatusLeft(DeathsDesign, 3.5f).SpendGauge(Soul, 50).ForbidStatuses(Enshrouded, SoulReaver, Executioner, EnshroudEnding);
            if (level >= 70)
                stalk.PotencyIfStatus(EnhancedGibbet, m3 ? 440 : 400).PotencyIfStatus(EnhancedGallows, m3 ? 440 : 400).ApplyStatus(SoulReaver, 30);
        }
        if (level >= 55)
        {
            var swathe = b.Ogcd("GrimSwathe", 0, actionId: 24392).Shape(AoeShape.Cone, 8, 90, 8).RequiresStatusLeft(DeathsDesign, 3.5f).Aoe(140, 3).RequiresTargets(3).SpendGauge(Soul, 50).ForbidStatuses(Enshrouded, SoulReaver, Executioner, EnshroudEnding);
            if (level >= 70)
                swathe.ApplyStatus(SoulReaver, 30);
        }
        if (level >= 76)
            b.Ogcd("Gluttony", 560, GluttonyCD, 24393).RequiresStatusLeft(DeathsDesign, 6).SpendGauge(Soul, 50).ForbidStatuses(Enshrouded, SoulReaver, Executioner, EnshroudEnding).ApplyStatus(gluttonyReaver, 30, stacks: 2);
        if (level >= 80)
        {
            var enshroud = b.Ogcd("Enshroud", 0, EnshroudCD, 24394).NoTarget().RequiresStatusLeft(DeathsDesign, 13).SpendGauge(Shroud, 50).ForbidStatuses(IdealHost, Enshrouded, SoulReaver, Executioner)
                .GainGauge(Lemure, 5).ApplyStatus(Enshrouded, 30);
            if (level >= 92)
                enshroud.ApplyStatus(Oblatio, 30);
            enshroud.RemoveStatus(PerfectioParata);
        }
        if (level >= 88)
        {
            var ideal = b.Ogcd("EnshroudIdeal", 0, EnshroudCD, 24394).NoTarget().RequiresStatusLeft(DeathsDesign, 13).RequiresStatus(IdealHost).ForbidStatuses(Enshrouded, SoulReaver, Executioner)
                .RemoveStatus(IdealHost).GainGauge(Lemure, 5).ApplyStatus(Enshrouded, 30);
            if (level >= 92)
                ideal.ApplyStatus(Oblatio, 30);
            ideal.RemoveStatus(PerfectioParata);
        }
        if (level >= 86)
            b.Ogcd("LemuresSlice", 280, actionId: 24399).RequiresStatus(Enshrouded).SpendGauge(Void, 2)
                .Ogcd("LemuresScythe", 0, actionId: 24400).Shape(AoeShape.Cone, 8, 90, 8).Aoe(100, 3).RequiresTargets(3).RequiresStatus(Enshrouded).SpendGauge(Void, 2);
        if (level >= 92)
            b.Ogcd("Sacrificium", 700, actionId: 36969).Shape(AoeShape.TargetCircle, 5, 0, 25).ConsumeStacks(Oblatio);
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

    // CMA-ES on the xan timeline harness (9 fights with --party-buffs 7.8, deterministic search; tools/blm_engine_eval tuned/weights-RPR-v3.json,
    // two runs from the rpr_engine_eval weights: 682,501 -> 689,570 -> 689,683; a 5-GCD horizon scored lower even retuned, 685,919).
    // MinNodes / SliceNodes: every search (at most about 1,000 nodes in the 9 fights) completes, within the frame it starts (the slice is larger
    // than any search), so live play equals the deterministic search; BudgetMs only caps a search larger than MinNodes.
    public const string DefaultWeightsJson = """
    {
      "OverCap": 4, "Combo": 0.60711783, "LambdaScale": 0.77821386, "TargetPull": 0, "SwitchMargin": 23.335081,
      "FillerScale": 0.9681626, "BurstBias": 1.7269272, "StatusRemainder": 0.30177203, "CycleScale": 1, "CooldownLambdaScale": -1,
      "ForecastSelfBuffs": 0, "UnlockScale": 0,
      "StatusValue": { "DeathsDesign": 0 }, "CooldownValue": { "GluttonyCD": 0 }, "GaugeValue": { "Shroud": -222.96074 },
      "HorizonGcds": 4, "BudgetMs": 10, "MinNodes": 1500, "SliceNodes": 1500, "BoundOgcdsPerSlot": 1
    }
    """;
}
