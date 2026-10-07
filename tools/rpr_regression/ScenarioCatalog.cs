namespace RprRegression;

public static class ScenarioCatalog
{
    public static IReadOnlyList<ScenarioDefinition> BuildAll()
    {
        var scenarios = new List<ScenarioDefinition>();
        AddGcdOpeners(scenarios);
        AddModeSwitches(scenarios);
        AddDeathsDesign(scenarios);
        AddArcanePotion(scenarios);
        AddGluttony(scenarios);
        AddGauge(scenarios);
        AddEnshroud(scenarios);
        AddReaverExecutioner(scenarios);
        AddAoeTargeting(scenarios);
        AddDowntime(scenarios);
        AddRangedUptime(scenarios);
        AddTrueNorth(scenarios);
        AddLevelSync(scenarios);
        AddWeaveValidation(scenarios);
        AddDancingMadProfile(scenarios);
        AddWindurstThirdWalkProfile(scenarios);
        AddExpandedValidationMatrix(scenarios);
        return scenarios;
    }

    public static IReadOnlyList<ScenarioDefinition> Select(string selector)
    {
        var all = BuildAll();
        if (selector.Equals("all", StringComparison.OrdinalIgnoreCase))
            return all;

        return all.Where(s =>
            s.Name.Contains(selector, StringComparison.OrdinalIgnoreCase)
            || s.Category.ToString().Equals(selector, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private static void AddGcdOpeners(List<ScenarioDefinition> scenarios)
    {
        foreach (var gcd in new[] { 2.47, 2.48, 2.49 })
        {
            scenarios.Add(Base($"gcd_{gcd:0.00}_opener_2gcd", ScenarioCategory.GcdOpener) with { Gcd = gcd, Opener = OpenerBurstMode.TwoGcd, SoulSliceCharges = 2 });
            scenarios.Add(Base($"gcd_{gcd:0.00}_opener_2_5s", ScenarioCategory.GcdOpener) with { Gcd = gcd, Opener = OpenerBurstMode.TwoPointFiveSecond });
            scenarios.Add(Base($"gcd_{gcd:0.00}_opener_0s", ScenarioCategory.GcdOpener) with { Gcd = gcd, Opener = OpenerBurstMode.ZeroSecond });
        }

        scenarios.Add(Base("opener_harpe_delay_tank_fa_wait", ScenarioCategory.GcdOpener) with { Gcd = 2.49, Opener = OpenerBurstMode.TwoGcd, SoulSliceCharges = 2 });
    }

    private static void AddModeSwitches(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("fullmode_standard", ScenarioCategory.RotationMode));
        scenarios.Add(Base("basicmode_standard", ScenarioCategory.RotationMode) with { InitialMode = RotationMode.Basic });
        scenarios.Add(Base("full_to_basic_midfight", ScenarioCategory.RotationMode) with { Events = [Mode(60, 480, RotationMode.Basic)] });
        scenarios.Add(Base("basic_to_full_midfight", ScenarioCategory.RotationMode) with { InitialMode = RotationMode.Basic, Events = [Mode(60, 480, RotationMode.Full)] });
        scenarios.Add(Base("basic_switch_during_burst", ScenarioCategory.RotationMode) with { Events = [Mode(118, 480, RotationMode.Basic)] });
        scenarios.Add(Base("basic_switch_during_enshroud", ScenarioCategory.RotationMode) with { Events = [Event(ScenarioEventType.EnshroudStarted, 116, 125), Mode(118, 480, RotationMode.Basic)] });
        scenarios.Add(Base("basic_switch_perfectio_parata", ScenarioCategory.RotationMode) with { Events = [Event(ScenarioEventType.PerfectioParata, 118, 125), Mode(119, 480, RotationMode.Basic)] });
        scenarios.Add(Base("basic_switch_reaver", ScenarioCategory.RotationMode) with { InitialReaver = ReaverState.SoulReaver, Events = [Mode(2, 480, RotationMode.Basic)] });
        scenarios.Add(Base("basic_switch_executioner", ScenarioCategory.RotationMode) with { InitialReaver = ReaverState.Executioner, Events = [Mode(2, 480, RotationMode.Basic)] });
    }

    private static void AddDeathsDesign(List<ScenarioDefinition> scenarios)
    {
        foreach (var left in new[] { 60, 29, 15, 10, 5, 0 })
            scenarios.Add(Base($"dd_left_{left}", ScenarioCategory.DeathsDesign) with { DeathsDesignLeft = left });

        scenarios.Add(Base("even_burst_dd_one_refresh_enough", ScenarioCategory.DeathsDesign) with { KillTime = 260, DeathsDesignLeft = 24, EvenBurstOneRefreshEnough = true });
        scenarios.Add(Base("even_burst_dd_two_refresh_needed", ScenarioCategory.DeathsDesign) with { KillTime = 260, DeathsDesignLeft = 3, EvenBurstTwoRefreshNeeded = true });
        scenarios.Add(Base("dd_falls_before_enshroud", ScenarioCategory.DeathsDesign) with { DeathsDesignLeft = 6, BlueGauge = 50 });
        scenarios.Add(Base("dd_falls_before_perfectio", ScenarioCategory.DeathsDesign) with { DeathsDesignLeft = 4, Events = [Event(ScenarioEventType.PerfectioParata, 118, 126)] });
        scenarios.Add(Base("perfectio_before_dd_to_fit_arcane", ScenarioCategory.DeathsDesign) with { KillTime = 5, DeathsDesignLeft = 2, RedGauge = 0, BlueGauge = 0, InitialArcaneCircleReadyIn = 117.8, InitialArcaneCircleLeft = 2.2, InitialPerfectioParataLeft = 20 });
        scenarios.Add(Base("dd_target_loss_soon_skip", ScenarioCategory.DeathsDesign) with { DeathsDesignLeft = 4, Events = [Event(ScenarioEventType.TargetLost, 5, 11)] });
        scenarios.Add(Base("dd_target_dies_soon_skip", ScenarioCategory.DeathsDesign) with { DeathsDesignLeft = 4, FinalTwoGcdKill = true, KillTime = 12 });
        scenarios.Add(Base("dd_filler_beats_refresh", ScenarioCategory.DeathsDesign) with { DeathsDesignLeft = 35, RedGauge = 100 });
        scenarios.Add(Base("dd_needed_for_even_burst", ScenarioCategory.DeathsDesign) with { KillTime = 260, DeathsDesignLeft = 2, EvenBurstTwoRefreshNeeded = true });
    }

    private static void AddArcanePotion(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("ac_ready", ScenarioCategory.ArcanePotion) with { Potion = PotionMode.OpenerAndEvenBurst });
        scenarios.Add(Base("ac_four_second_display", ScenarioCategory.ArcanePotion) with { ArcaneCircleFourSecondDisplay = true });
        scenarios.Add(Base("ac_one_gcd_later", ScenarioCategory.ArcanePotion) with { ArcaneCircleFourSecondDisplay = true, Gcd = 2.47 });
        scenarios.Add(Base("pot_off", ScenarioCategory.ArcanePotion) with { Potion = PotionMode.Off });
        scenarios.Add(Base("pot_opener_even", ScenarioCategory.ArcanePotion) with { Potion = PotionMode.OpenerAndEvenBurst });
        scenarios.Add(Base("pot_even_except_opener", ScenarioCategory.ArcanePotion) with { Potion = PotionMode.EvenBurstExceptOpener });
        scenarios.Add(Base("pot_two_gcd_before", ScenarioCategory.ArcanePotion) with { KillTime = 260, Potion = PotionMode.OpenerAndEvenBurst });
        scenarios.Add(Base("pot_2_5s_same_gcd", ScenarioCategory.ArcanePotion) with { Opener = OpenerBurstMode.TwoPointFiveSecond, Potion = PotionMode.OpenerAndEvenBurst });
        scenarios.Add(Base("pot_0s_same_gcd", ScenarioCategory.ArcanePotion) with { Opener = OpenerBurstMode.ZeroSecond, Potion = PotionMode.OpenerAndEvenBurst });
        scenarios.Add(Base("ac_not_delayed_for_dd", ScenarioCategory.ArcanePotion) with { DeathsDesignLeft = 0, KillTime = 260 });
        scenarios.Add(Base("patch73_even_burst_two_ws_before_ac", ScenarioCategory.ArcanePotion) with
        {
            KillTime = 25,
            Potion = PotionMode.Off,
            DeathsDesignLeft = 30,
            RedGauge = 50,
            BlueGauge = 50,
            SoulSliceCharges = 1,
            InitialArcaneCircleReadyIn = 4.5,
            ArcaneCircleFourSecondDisplay = true,
            ExpectDoubleEnshroud = true,
            ExpectTwoCommunio = true,
            ExpectPerfectio = true
        });
    }

    private static void AddGluttony(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("gluttony_ready_3s", ScenarioCategory.Gluttony) with { GluttonyReadyAtStart = true, RedGauge = 50 });
        scenarios.Add(Base("gluttony_even_drift_2m", ScenarioCategory.Gluttony) with { KillTime = 260, RedGauge = 50 });
        scenarios.Add(Base("gluttony_even_drift_4m", ScenarioCategory.Gluttony) with { KillTime = 500, RedGauge = 50 });
        scenarios.Add(Base("gluttony_even_drift_6m", ScenarioCategory.Gluttony) with { KillTime = 740, RedGauge = 50 });
        scenarios.Add(Base("gluttony_even_drift_8m", ScenarioCategory.Gluttony) with { KillTime = 980, RedGauge = 50 });
        scenarios.Add(Base("gluttony_no_ac_same_gcd_lead", ScenarioCategory.Gluttony) with { RedGauge = 50 });
        scenarios.Add(Base("gluttony_after_raidbuff", ScenarioCategory.Gluttony) with { RedGauge = 50 });
        scenarios.Add(Base("gluttony_basicmode_forbidden", ScenarioCategory.Gluttony) with { InitialMode = RotationMode.Basic, RedGauge = 100 });
        scenarios.Add(Base("post_perfectio_combo_priority_slice", ScenarioCategory.Gluttony) with { KillTime = 30, DeathsDesignLeft = 6, RedGauge = 30, BlueGauge = 0, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 105, InitialArcaneCircleLeft = 15, InitialComboLast = "Slice", InitialComboRemaining = 6, Events = [Event(ScenarioEventType.PerfectioParata, 0, 0.1)] });
        scenarios.Add(Base("post_perfectio_combo_priority_waxing", ScenarioCategory.Gluttony) with { KillTime = 30, DeathsDesignLeft = 6, RedGauge = 30, BlueGauge = 0, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 105, InitialArcaneCircleLeft = 15, InitialComboLast = "WaxingSlice", InitialComboRemaining = 6, Events = [Event(ScenarioEventType.PerfectioParata, 0, 0.1)] });
        scenarios.Add(Base("post_perfectio_combo_priority_dying", ScenarioCategory.Gluttony) with { KillTime = 10, DeathsDesignLeft = 30, RedGauge = 40, BlueGauge = 0, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 105, InitialArcaneCircleLeft = 15, InitialComboLast = "Slice", InitialComboRemaining = 6, FinalTwoGcdKill = true, Events = [Event(ScenarioEventType.PerfectioParata, 0, 0.1)] });
        scenarios.Add(Base("post_perfectio_gluttony_ready", ScenarioCategory.Gluttony) with { KillTime = 20, DeathsDesignLeft = 60, RedGauge = 50, BlueGauge = 0, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 105, InitialArcaneCircleLeft = 15, Events = [Event(ScenarioEventType.PerfectioParata, 0, 0.1)] });
        scenarios.Add(Base("post_perfectio_soul_slice_gluttony", ScenarioCategory.Gluttony) with { KillTime = 20, DeathsDesignLeft = 60, RedGauge = 0, BlueGauge = 0, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 105, InitialArcaneCircleLeft = 15, Events = [Event(ScenarioEventType.PerfectioParata, 0, 0.1)] });
    }

    private static void AddGauge(List<ScenarioDefinition> scenarios)
    {
        foreach (var red in new[] { 0, 40, 50, 60, 90, 100 })
            scenarios.Add(Base($"red_gauge_{red}", ScenarioCategory.Gauge) with { RedGauge = red });
        foreach (var blue in new[] { 0, 40, 50, 90, 100 })
            scenarios.Add(Base($"blue_gauge_{blue}", ScenarioCategory.Gauge) with { BlueGauge = blue });

        scenarios.Add(Base("pre_even_blue_below_50", ScenarioCategory.Gauge) with { BlueGauge = 40, RedGauge = 50, ForceBlueGaugeBuild = true });
        scenarios.Add(Base("pre_even_red_spend_for_blue", ScenarioCategory.Gauge) with { BlueGauge = 40, RedGauge = 90, ForceBlueGaugeBuild = true });
        scenarios.Add(Base("blue_100_ac_far_single_enshroud", ScenarioCategory.Gauge) with { BlueGauge = 100, RedGauge = 50 });
        scenarios.Add(Base("blue_100_basic_red_overcap", ScenarioCategory.Gauge) with { InitialMode = RotationMode.Basic, BlueGauge = 100, RedGauge = 100 });
        scenarios.Add(Base("basic_mode_red_100_single", ScenarioCategory.Gauge) with { InitialMode = RotationMode.Basic, RedGauge = 100 });
        scenarios.Add(Base("basic_mode_red_100_aoe", ScenarioCategory.Gauge) with { InitialMode = RotationMode.Basic, RedGauge = 100, AoeTargets = 3, ConeTargets = 3 });
        scenarios.Add(Base("full_mode_aoe_red_100_blue_100_enshroud_held", ScenarioCategory.Gauge) with { KillTime = 20, RedGauge = 100, BlueGauge = 100, InitialArcaneCircleReadyIn = 30, InitialGluttonyReadyIn = 10, AoeTargets = 3, ConeTargets = 3 });
        scenarios.Add(Base("basic_mode_blue_100_red_100", ScenarioCategory.Gauge) with { InitialMode = RotationMode.Basic, BlueGauge = 100, RedGauge = 100 });
        scenarios.Add(Base("basic_mode_soul_slice_overcap_red_60", ScenarioCategory.Gauge) with { InitialMode = RotationMode.Basic, SoulSliceCharges = 1.95, RedGauge = 60 });
        scenarios.Add(Base("basic_mode_soul_slice_overcap_red_100", ScenarioCategory.Gauge) with { InitialMode = RotationMode.Basic, SoulSliceCharges = 1.95, RedGauge = 100 });
        scenarios.Add(Base("basic_mode_no_gluttony", ScenarioCategory.Gauge) with { InitialMode = RotationMode.Basic, RedGauge = 100 });
        scenarios.Add(Base("basic_mode_fullmode_switch_red_overcap", ScenarioCategory.Gauge) with { RedGauge = 100, Events = [Mode(30, 90, RotationMode.Basic), Mode(90, 240, RotationMode.Full)] });
        scenarios.Add(Base("soul_slice_two_charges_soon", ScenarioCategory.Gauge) with { SoulSliceCharges = 1.95, RedGauge = 40 });
        scenarios.Add(Base("red_40_combo_spender_same_weave", ScenarioCategory.Gauge) with { Level = 70, KillTime = 8, Potion = PotionMode.Off, DeathsDesignLeft = 60, RedGauge = 40, BlueGauge = 0, SoulSliceCharges = 0, InitialArcaneCircleReadyIn = 120, InitialSoulSliceUsed = true });
        scenarios.Add(Base("red_40_soul_slice_gluttony_same_weave", ScenarioCategory.Gauge) with { KillTime = 8, Potion = PotionMode.Off, DeathsDesignLeft = 60, RedGauge = 40, BlueGauge = 0, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 120, InitialGluttonyReadyIn = 0, InitialSoulSliceUsed = true });
        scenarios.Add(Base("red_90_combo_cap_spender_same_weave", ScenarioCategory.Gauge) with { Level = 70, KillTime = 8, Potion = PotionMode.Off, DeathsDesignLeft = 60, RedGauge = 90, BlueGauge = 0, SoulSliceCharges = 0, InitialArcaneCircleReadyIn = 120, InitialSoulSliceUsed = true });
        scenarios.Add(Base("soul_slice_would_red_overcap", ScenarioCategory.Gauge) with { SoulSliceCharges = 1.95, RedGauge = 90 });
        scenarios.Add(Base("soul_slice_red_50_normal", ScenarioCategory.Gauge) with { KillTime = 20, BlueGauge = 0, RedGauge = 50, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 20 });
        scenarios.Add(Base("soul_slice_red_60_overcap_soon", ScenarioCategory.Gauge) with { KillTime = 20, Level = 78, BlueGauge = 0, RedGauge = 60, SoulSliceCharges = 1.95, InitialArcaneCircleReadyIn = 20 });
        scenarios.Add(Base("soul_slice_red_90_overcap_soon", ScenarioCategory.Gauge) with { KillTime = 20, Level = 78, BlueGauge = 0, RedGauge = 90, SoulSliceCharges = 1.95, InitialArcaneCircleReadyIn = 20 });
        scenarios.Add(Base("soul_slice_red_100_overcap_soon", ScenarioCategory.Gauge) with { KillTime = 20, Level = 78, BlueGauge = 0, RedGauge = 100, SoulSliceCharges = 1.95, InitialArcaneCircleReadyIn = 20 });
        scenarios.Add(Base("soul_slice_combo_protected_red_60", ScenarioCategory.Gauge) with { KillTime = 20, Level = 78, BlueGauge = 0, RedGauge = 60, SoulSliceCharges = 1.95, InitialArcaneCircleReadyIn = 20, InitialComboLast = "Slice" });
        scenarios.Add(Base("soul_slice_combo_protected_red_100", ScenarioCategory.Gauge) with { KillTime = 20, Level = 78, BlueGauge = 0, RedGauge = 100, SoulSliceCharges = 1.95, InitialArcaneCircleReadyIn = 20, InitialComboLast = "Slice" });
        scenarios.Add(Base("soul_slice_before_arcane_6s", ScenarioCategory.Gauge) with { KillTime = 20, BlueGauge = 0, RedGauge = 50, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 6, InitialSoulSliceUsed = true });
        scenarios.Add(Base("soul_slice_before_arcane_20s", ScenarioCategory.Gauge) with { KillTime = 20, BlueGauge = 0, RedGauge = 50, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 20, InitialSoulSliceUsed = true });
        scenarios.Add(Base("soul_slice_basic_mode_red_100", ScenarioCategory.Gauge) with { KillTime = 20, InitialMode = RotationMode.Basic, BlueGauge = 100, RedGauge = 100, SoulSliceCharges = 1.95, InitialArcaneCircleReadyIn = 20 });
        scenarios.Add(Base("soul_scythe_aoe_3_targets", ScenarioCategory.Gauge) with { KillTime = 20, BlueGauge = 0, RedGauge = 50, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 20, AoeTargets = 3, ConeTargets = 3 });
        scenarios.Add(Base("soul_scythe_aoe_to_single_switch", ScenarioCategory.Gauge) with { KillTime = 20, BlueGauge = 0, RedGauge = 50, SoulSliceCharges = 1.95, InitialArcaneCircleReadyIn = 20, AoeTargets = 3, ConeTargets = 3, Events = [Targets(5, 20, 1)] });
        scenarios.Add(Base("soul_slice_target_dies_2gcd", ScenarioCategory.Gauge) with { BlueGauge = 0, RedGauge = 50, SoulSliceCharges = 1.95, InitialArcaneCircleReadyIn = 20, FinalTwoGcdKill = true, KillTime = 8 });
        scenarios.Add(Base("even_burst_shroud_plan_blue30_red0", ScenarioCategory.Gauge) with { KillTime = 160, BlueGauge = 30, RedGauge = 0, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 120, InitialSoulSliceUsed = true, ExpectSecondEnshroudBlueGauge = true, Events = [Event(ScenarioEventType.TargetLost, 0, 92)] });
        scenarios.Add(Base("even_burst_shroud_plan_impossible_fallback", ScenarioCategory.Gauge) with { KillTime = 160, BlueGauge = 0, RedGauge = 0, SoulSliceCharges = 0, InitialArcaneCircleReadyIn = 120, InitialSoulSliceUsed = true, Events = [Event(ScenarioEventType.TargetLost, 0, 125)] });
        scenarios.Add(Base("fast_gcd_246_late_soulslice_before_ph", ScenarioCategory.Gauge) with { Gcd = 2.46, KillTime = 260, RedGauge = 50, BlueGauge = 0, DeathsDesignLeft = 30, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 112, InitialArcaneCircleLeft = 12, InitialImmortalSacrifice = 8, InitialBloodsownLeft = 0 });
        scenarios.Add(Base("fast_gcd_246_late_soulscythe_before_ph_aoe", ScenarioCategory.Gauge) with { Gcd = 2.46, KillTime = 260, RedGauge = 50, BlueGauge = 0, DeathsDesignLeft = 30, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 112, InitialArcaneCircleLeft = 12, InitialImmortalSacrifice = 8, InitialBloodsownLeft = 0, AoeTargets = 3, ConeTargets = 3 });
        scenarios.Add(Base("gcd_247_no_late_soulslice_before_ph", ScenarioCategory.Gauge) with { Gcd = 2.47, KillTime = 260, RedGauge = 50, BlueGauge = 0, DeathsDesignLeft = 30, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 112, InitialArcaneCircleLeft = 12, InitialImmortalSacrifice = 8, InitialBloodsownLeft = 0 });
        scenarios.Add(Base("gcd_248_no_late_soulslice_before_ph", ScenarioCategory.Gauge) with { Gcd = 2.48, KillTime = 260, RedGauge = 50, BlueGauge = 0, DeathsDesignLeft = 30, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 112, InitialArcaneCircleLeft = 12, InitialImmortalSacrifice = 8, InitialBloodsownLeft = 0 });
        scenarios.Add(Base("gcd_249_no_late_soulslice_before_ph", ScenarioCategory.Gauge) with { Gcd = 2.49, KillTime = 260, RedGauge = 50, BlueGauge = 0, DeathsDesignLeft = 30, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 112, InitialArcaneCircleLeft = 12, InitialImmortalSacrifice = 8, InitialBloodsownLeft = 0 });
        scenarios.Add(Base("fast_gcd_246_red_60_no_late_soulslice", ScenarioCategory.Gauge) with { Gcd = 2.46, KillTime = 260, RedGauge = 60, BlueGauge = 0, DeathsDesignLeft = 30, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 112, InitialArcaneCircleLeft = 12, InitialImmortalSacrifice = 8, InitialBloodsownLeft = 0 });
        scenarios.Add(Base("fast_gcd_246_dd_low_no_late_soulslice", ScenarioCategory.Gauge) with { Gcd = 2.46, KillTime = 260, RedGauge = 50, BlueGauge = 0, DeathsDesignLeft = 5, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 112, InitialArcaneCircleLeft = 12, InitialImmortalSacrifice = 8, InitialBloodsownLeft = 0 });
        scenarios.Add(Base("fast_gcd_246_double_enshroud_integrity", ScenarioCategory.Gauge) with { Gcd = 2.46, KillTime = 260, RedGauge = 50, BlueGauge = 0, DeathsDesignLeft = 30, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 112, InitialArcaneCircleLeft = 12, InitialImmortalSacrifice = 8, InitialBloodsownLeft = 0, ExpectDoubleEnshroud = true, ExpectTwoCommunio = true, ExpectPerfectio = true });
        scenarios.Add(Base("fast_gcd_246_blue_50_late_soulslice_compare", ScenarioCategory.Gauge) with { Gcd = 2.46, KillTime = 260, RedGauge = 50, BlueGauge = 50, DeathsDesignLeft = 30, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 112, InitialArcaneCircleLeft = 12, InitialImmortalSacrifice = 8, InitialBloodsownLeft = 0 });
        scenarios.Add(Base("fast_gcd_246_blue_60_late_soulslice_compare", ScenarioCategory.Gauge) with { Gcd = 2.46, KillTime = 260, RedGauge = 50, BlueGauge = 60, DeathsDesignLeft = 30, SoulSliceCharges = 1, InitialArcaneCircleReadyIn = 112, InitialArcaneCircleLeft = 12, InitialImmortalSacrifice = 8, InitialBloodsownLeft = 0 });
    }

    private static void AddEnshroud(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("two_minute_three_enshrouds", ScenarioCategory.Enshroud) with { KillTime = 260, BlueGauge = 100, RedGauge = 100, ExpectDoubleEnshroud = true, ExpectTwoCommunio = true, ExpectPerfectio = true, ExpectLemure = true, ExpectSacrificium = true });
        scenarios.Add(Base("even_double_enshroud", ScenarioCategory.Enshroud) with { KillTime = 260, BlueGauge = 100, RedGauge = 50, ExpectDoubleEnshroud = true, ExpectTwoCommunio = true, ExpectPerfectio = true, ExpectLemure = true, ExpectSacrificium = true });
        scenarios.Add(Base("even_burst_blue100_ph_before_second_enshroud", ScenarioCategory.Enshroud) with
        {
            KillTime = 25,
            InitialSoulSliceUsed = true,
            DeathsDesignLeft = 30,
            RedGauge = 0,
            BlueGauge = 100,
            SoulSliceCharges = 2,
            InitialArcaneCircleReadyIn = 106.5,
            InitialArcaneCircleLeft = 13.5,
            InitialImmortalSacrifice = 8,
            InitialBloodsownLeft = 0,
            ExpectPerfectio = true
        });
        scenarios.Add(Base("first_enshroud_reaping_dd_ac", ScenarioCategory.Enshroud) with { EvenBurstTwoRefreshNeeded = true, ExpectDoubleEnshroud = true, ExpectTwoCommunio = true, Events = [Event(ScenarioEventType.EnshroudStarted, 116, 126)] });
        scenarios.Add(Base("no_dd_before_first_reaping", ScenarioCategory.Enshroud) with { EvenBurstTwoRefreshNeeded = true, Events = [Event(ScenarioEventType.EnshroudStarted, 116, 126)] });
        scenarios.Add(Base("communio_two", ScenarioCategory.Enshroud) with { KillTime = 260, BlueGauge = 100, ExpectTwoCommunio = true });
        scenarios.Add(Base("perfectio_not_late", ScenarioCategory.Enshroud) with { ExpectPerfectio = true, Events = [Event(ScenarioEventType.PerfectioParata, 122, 132)] });
        scenarios.Add(Base("lemure_not_missed", ScenarioCategory.Enshroud) with { ExpectLemure = true, Events = [Event(ScenarioEventType.EnshroudStarted, 116, 126)] });
        scenarios.Add(Base("sacrificium_not_missed", ScenarioCategory.Enshroud) with { ExpectSacrificium = true, Events = [Event(ScenarioEventType.EnshroudStarted, 116, 126)] });
        scenarios.Add(Base("idealhost_not_rotted", ScenarioCategory.Enshroud) with { Events = [Event(ScenarioEventType.EnshroudStarted, 120, 130)] });
        scenarios.Add(Base("full_to_basic_with_idealhost_dd_low", ScenarioCategory.Enshroud) with { InitialIdealHost = true, DeathsDesignLeft = 5, BlueGauge = 0, RedGauge = 0, KillTime = 45, Events = [Mode(0, 45, RotationMode.Basic)] });
        scenarios.Add(Base("full_to_basic_with_idealhost_dd_safe", ScenarioCategory.Enshroud) with { InitialIdealHost = true, DeathsDesignLeft = 30, BlueGauge = 0, RedGauge = 0, KillTime = 45, Events = [Mode(0, 45, RotationMode.Basic)] });
        scenarios.Add(Base("basic_cleanup_enshroud_dd_expiring", ScenarioCategory.Enshroud) with { InitialMode = RotationMode.Basic, InitialIdealHost = true, DeathsDesignLeft = 2, BlueGauge = 0, RedGauge = 0, KillTime = 45 });
        scenarios.Add(Base("perfectio_parata_not_dropped", ScenarioCategory.Enshroud) with { ExpectPerfectio = true, Events = [Event(ScenarioEventType.PerfectioParata, 120, 130)] });
        scenarios.Add(Base("enshroud_single_to_aoe_switch", ScenarioCategory.Enshroud) with { Potion = PotionMode.Off, DeathsDesignLeft = 60, BlueGauge = 100, RedGauge = 0, Events = [Targets(4, 20, 3)] });
        scenarios.Add(Base("enshroud_aoe_to_single_switch", ScenarioCategory.Enshroud) with { Potion = PotionMode.Off, DeathsDesignLeft = 60, BlueGauge = 100, RedGauge = 0, AoeTargets = 3, ConeTargets = 3, Events = [Targets(4, 20, 1)] });
        scenarios.Add(Base("enshroud_target_count_flaps_2_3_2", ScenarioCategory.Enshroud) with { Potion = PotionMode.Off, DeathsDesignLeft = 60, BlueGauge = 100, RedGauge = 0, AoeTargets = 2, ConeTargets = 2, Events = [Targets(4, 8, 3)] });
        scenarios.Add(Base("enshroud_target_count_flaps_3_2_3", ScenarioCategory.Enshroud) with { Potion = PotionMode.Off, DeathsDesignLeft = 60, BlueGauge = 100, RedGauge = 0, AoeTargets = 3, ConeTargets = 3, Events = [Targets(4, 8, 2)] });
        scenarios.Add(Base("enshroud_bestcone_null_fallback_single", ScenarioCategory.Enshroud) with { Potion = PotionMode.Off, DeathsDesignLeft = 60, BlueGauge = 100, RedGauge = 0, AoeTargets = 3, ConeTargets = 3, Events = [Event(ScenarioEventType.NullBestConeTarget, 2, 20)] });
        scenarios.Add(Base("enshroud_primary_null_bestcone_valid", ScenarioCategory.Enshroud) with { Potion = PotionMode.Off, DeathsDesignLeft = 60, BlueGauge = 100, RedGauge = 0, AoeTargets = 3, ConeTargets = 3, Events = [Event(ScenarioEventType.PrimaryTargetNull, 2, 20)] });
        scenarios.Add(Base("enshroud_bluesouls_1_communio_no_reaping", ScenarioCategory.Enshroud) with { Potion = PotionMode.Off, DeathsDesignLeft = 60, BlueGauge = 100, RedGauge = 0 });
        scenarios.Add(Base("enshroud_lemure_scythe_bestcone_null", ScenarioCategory.Enshroud) with { Potion = PotionMode.Off, DeathsDesignLeft = 60, BlueGauge = 100, RedGauge = 0, AoeTargets = 3, ConeTargets = 3, Events = [Event(ScenarioEventType.NullBestConeTarget, 7, 20)] });
        scenarios.Add(Base("enshroud_lemure_scythe_to_slice_fallback", ScenarioCategory.Enshroud) with { Potion = PotionMode.Off, DeathsDesignLeft = 60, BlueGauge = 100, RedGauge = 0, AoeTargets = 3, ConeTargets = 3, Events = [Event(ScenarioEventType.NullBestConeTarget, 9, 14), Targets(14, 20, 1)] });
    }

    private static void AddReaverExecutioner(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("soulreaver_1_stack", ScenarioCategory.ReaverExecutioner) with { InitialReaver = ReaverState.SoulReaver });
        scenarios.Add(Base("executioner_2_stack", ScenarioCategory.ReaverExecutioner) with { InitialReaver = ReaverState.Executioner, InitialReaverStacks = 2 });
        scenarios.Add(Base("executioner_enhanced_gibbet_sequence", ScenarioCategory.ReaverExecutioner) with { InitialReaver = ReaverState.Executioner, InitialReaverStacks = 2, InitialEnhancedGibbetLeft = 60 });
        scenarios.Add(Base("executioner_enhanced_gallows_sequence", ScenarioCategory.ReaverExecutioner) with { InitialReaver = ReaverState.Executioner, InitialReaverStacks = 2, InitialEnhancedGallowsLeft = 60 });
        scenarios.Add(Base("reaver_expiring_enhanced_gibbet", ScenarioCategory.ReaverExecutioner) with { KillTime = 3, InitialReaver = ReaverState.SoulReaver, InitialEnhancedGibbetLeft = 0.5 });
        scenarios.Add(Base("executioner_expiring_enhanced_gallows", ScenarioCategory.ReaverExecutioner) with { KillTime = 3, InitialReaver = ReaverState.Executioner, InitialReaverStacks = 2, InitialEnhancedGallowsLeft = 0.5 });
        scenarios.Add(Base("reaver_no_normal_gcd", ScenarioCategory.ReaverExecutioner) with { InitialReaver = ReaverState.SoulReaver, DeathsDesignLeft = 2 });
        scenarios.Add(Base("executioner_no_bloodstalk", ScenarioCategory.ReaverExecutioner) with { InitialReaver = ReaverState.Executioner, RedGauge = 100 });
        scenarios.Add(Base("reaver_three_targets_single", ScenarioCategory.ReaverExecutioner) with { InitialReaver = ReaverState.SoulReaver, AoeTargets = 3, ConeTargets = 3 });
        scenarios.Add(Base("reaver_four_targets_guillotine", ScenarioCategory.ReaverExecutioner) with { InitialReaver = ReaverState.SoulReaver, AoeTargets = 4, ConeTargets = 4 });
        scenarios.Add(Base("executioner_four_targets_guillotine", ScenarioCategory.ReaverExecutioner) with { InitialReaver = ReaverState.Executioner, AoeTargets = 4, ConeTargets = 4 });
    }

    private static void AddAoeTargeting(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("single_to_three_targets", ScenarioCategory.AoeTargeting) with { Events = [Targets(20, 80, 3)] });
        scenarios.Add(Base("three_to_single_target", ScenarioCategory.AoeTargeting) with { AoeTargets = 3, ConeTargets = 3, Events = [Targets(20, 80, 1)] });
        scenarios.Add(Base("four_to_single_target", ScenarioCategory.AoeTargeting) with { AoeTargets = 4, ConeTargets = 4, Events = [Targets(20, 80, 1)] });
        scenarios.Add(Base("soul_scythe_slice_exclusive", ScenarioCategory.AoeTargeting) with { AoeTargets = 3, ConeTargets = 3, SoulSliceCharges = 1.9 });
        scenarios.Add(Base("grim_swathe_bloodstalk_exclusive", ScenarioCategory.AoeTargeting) with { AoeTargets = 3, ConeTargets = 3, RedGauge = 100 });
        scenarios.Add(Base("grim_swathe_three_targets_enhanced_single_wins", ScenarioCategory.AoeTargeting) with { AoeTargets = 3, ConeTargets = 3, RedGauge = 100, BlueGauge = 0, InitialEnhancedGibbetLeft = 60, InitialGluttonyReadyIn = 60, InitialSoulSliceUsed = true });
        scenarios.Add(Base("cone_count_independent_from_aoe", ScenarioCategory.AoeTargeting) with { InitialReaver = ReaverState.SoulReaver, AoeTargets = 4, ConeTargets = 1, KillTime = 3 });
        scenarios.Add(Base("lemure_scythe_slice_exclusive", ScenarioCategory.AoeTargeting) with { AoeTargets = 3, ConeTargets = 3, Events = [Event(ScenarioEventType.EnshroudStarted, 20, 30)] });
        scenarios.Add(Base("nightmare_spinning_exclusive", ScenarioCategory.AoeTargeting) with { AoeTargets = 3, ConeTargets = 3 });
        scenarios.Add(Base("best_cone_null", ScenarioCategory.AoeTargeting) with { AoeTargets = 4, ConeTargets = 4, Events = [Event(ScenarioEventType.NullBestConeTarget, 0, 480)] });
        scenarios.Add(Base("best_line_null", ScenarioCategory.AoeTargeting) with { Events = [Event(ScenarioEventType.NullBestLineTarget, 0, 480)] });
        scenarios.Add(Base("best_ranged_aoe_null", ScenarioCategory.AoeTargeting) with { Events = [Event(ScenarioEventType.NullBestRangedAoeTarget, 0, 480)] });
        scenarios.Add(Base("primary_target_null_fallback", ScenarioCategory.AoeTargeting) with { Events = [Event(ScenarioEventType.PrimaryTargetNull, 20, 25)] });
        scenarios.Add(Base("priority_targets_empty", ScenarioCategory.AoeTargeting) with { Events = [Event(ScenarioEventType.EmptyPriorityTargets, 20, 25)] });
        scenarios.Add(Base("target_death_and_return", ScenarioCategory.AoeTargeting) with { RedGauge = 0, Events = [Event(ScenarioEventType.TargetKilled, 30, 30.001), Event(ScenarioEventType.TargetLost, 30, 35), Event(ScenarioEventType.TargetReturned, 35, 480)] });
    }

    private static void AddDowntime(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("target_loss_5s", ScenarioCategory.Downtime) with { Events = [Event(ScenarioEventType.TargetLost, 5, 10)] });
        scenarios.Add(Base("target_loss_10s", ScenarioCategory.Downtime) with { Events = [Event(ScenarioEventType.TargetLost, 10, 20)] });
        scenarios.Add(Base("target_loss_20s", ScenarioCategory.Downtime) with { Events = [Event(ScenarioEventType.TargetLost, 20, 40)] });
        scenarios.Add(Base("short_phase_return", ScenarioCategory.Downtime) with { Events = [Event(ScenarioEventType.TargetLost, 30, 33)] });
        scenarios.Add(Base("long_phase_transition", ScenarioCategory.Downtime) with { Events = [Event(ScenarioEventType.TargetLost, 60, 100)] });
        scenarios.Add(Base("burst_interrupted", ScenarioCategory.Downtime) with { Events = [Event(ScenarioEventType.TargetLost, 120, 126)] });
        scenarios.Add(Base("enshroud_interrupted", ScenarioCategory.Downtime) with { Events = [Event(ScenarioEventType.EnshroudStarted, 116, 126), Event(ScenarioEventType.TargetLost, 119, 123)] });
        scenarios.Add(Base("perfectio_target_loss", ScenarioCategory.Downtime) with { Events = [Event(ScenarioEventType.PerfectioParata, 116, 126), Event(ScenarioEventType.TargetLost, 118, 122)] });
        scenarios.Add(Base("ac_before_target_loss", ScenarioCategory.Downtime) with { Events = [Event(ScenarioEventType.TargetLost, 122, 128)] });
        scenarios.Add(Base("dd_refresh_then_target_loss", ScenarioCategory.Downtime) with { DeathsDesignLeft = 2, Events = [Event(ScenarioEventType.TargetLost, 5, 10)] });
        scenarios.Add(Base("kill_within_two_gcd", ScenarioCategory.Downtime) with { FinalTwoGcdKill = true, KillTime = 8 });
        scenarios.Add(Base("kill_within_five_gcd", ScenarioCategory.Downtime) with { KillTime = 15 });
        scenarios.Add(Base("ending_duty_intermediate_boss_hold_burst", ScenarioCategory.Downtime) with { KillTime = 15, InitialSoulSliceUsed = true, DeathsDesignLeft = 30, RedGauge = 50, BlueGauge = 50, Events = [Event(ScenarioEventType.TargetDying, 0, 10)] });
        scenarios.Add(Base("ending_duty_trash_pack_hold_burst", ScenarioCategory.Downtime) with { KillTime = 15, InitialSoulSliceUsed = true, DeathsDesignLeft = 30, RedGauge = 50, BlueGauge = 50, AoeTargets = 3, ConeTargets = 3, Events = [Event(ScenarioEventType.TargetDying, 0, 10)] });
        scenarios.Add(Base("ending_duty_final_boss_allows_burst", ScenarioCategory.Downtime) with { KillTime = 15, FinalEncounter = true, InitialSoulSliceUsed = true, DeathsDesignLeft = 30, RedGauge = 50, BlueGauge = 50, Events = [Event(ScenarioEventType.TargetDying, 0, 10)] });
        scenarios.Add(Base("ending_duty_ideal_host_cleanup", ScenarioCategory.Downtime) with { KillTime = 15, InitialSoulSliceUsed = true, DeathsDesignLeft = 30, RedGauge = 0, BlueGauge = 0, InitialIdealHost = true, Events = [Event(ScenarioEventType.TargetDying, 0, 10)] });
        scenarios.Add(Base("ending_duty_active_arcane_gluttony_cleanup", ScenarioCategory.Downtime) with { KillTime = 15, InitialSoulSliceUsed = true, DeathsDesignLeft = 30, RedGauge = 50, BlueGauge = 0, InitialArcaneCircleReadyIn = 110, InitialArcaneCircleLeft = 10, Events = [Event(ScenarioEventType.TargetDying, 0, 10)] });
        scenarios.Add(Base("timeline_ac_hold_before_downtime", ScenarioCategory.Downtime) with { KillTime = 220, Level = 72, Potion = PotionMode.Off, InitialSoulSliceUsed = true, DeathsDesignLeft = 60, RedGauge = 0, BlueGauge = 0, SoulSliceCharges = 0, Events = [Event(ScenarioEventType.TargetLost, 124, 134)] });
        scenarios.Add(Base("combat_soulsow_during_downtime", ScenarioCategory.Downtime) with { KillTime = 12, InitialSoulsow = false, Events = [Event(ScenarioEventType.TargetLost, 0, 6)] });
    }

    private static void AddRangedUptime(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("melee_available", ScenarioCategory.RangedUptime));
        scenarios.Add(Base("melee_unavailable_1gcd", ScenarioCategory.RangedUptime) with { Events = [Event(ScenarioEventType.MeleeUnavailable, 10, 12.5)] });
        scenarios.Add(Base("melee_unavailable_2gcd", ScenarioCategory.RangedUptime) with { Events = [Event(ScenarioEventType.MeleeUnavailable, 10, 15)] });
        scenarios.Add(Base("melee_unavailable_5s", ScenarioCategory.RangedUptime) with { Events = [Event(ScenarioEventType.MeleeUnavailable, 10, 15)] });
        scenarios.Add(Base("harpe_uptime", ScenarioCategory.RangedUptime) with { Level = 50, Events = [Event(ScenarioEventType.MeleeUnavailable, 10, 15)] });
        scenarios.Add(Base("harvest_moon_uptime", ScenarioCategory.RangedUptime) with { Level = 82, Events = [Event(ScenarioEventType.MeleeUnavailable, 10, 15)] });
        scenarios.Add(Base("perfectio_ranged_uptime", ScenarioCategory.RangedUptime) with { Level = 100, Events = [Event(ScenarioEventType.PerfectioParata, 10, 20), Event(ScenarioEventType.MeleeUnavailable, 10, 15)] });
        scenarios.Add(Base("enhanced_harpe_instant_uptime", ScenarioCategory.RangedUptime) with { Level = 100, InitialSoulsow = false, Events = [Event(ScenarioEventType.EnhancedHarpe, 0, 10), Event(ScenarioEventType.MeleeUnavailable, 0, 5)] });
    }

    private static void AddTrueNorth(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("positional_success", ScenarioCategory.TrueNorth) with { InitialReaver = ReaverState.SoulReaver, TrueNorth = TrueNorthMode.Auto });
        scenarios.Add(Base("positional_failure_auto_tn", ScenarioCategory.TrueNorth) with { InitialReaver = ReaverState.SoulReaver, TrueNorth = TrueNorthMode.Auto, PositionalCorrect = false });
        scenarios.Add(Base("true_north_off", ScenarioCategory.TrueNorth) with { InitialReaver = ReaverState.SoulReaver, TrueNorth = TrueNorthMode.Off, PositionalCorrect = false });
        scenarios.Add(Base("non_positional_target", ScenarioCategory.TrueNorth));
        scenarios.Add(Base("aoe_no_true_north", ScenarioCategory.TrueNorth) with { InitialReaver = ReaverState.SoulReaver, AoeTargets = 4, ConeTargets = 4, TrueNorth = TrueNorthMode.Auto });
        scenarios.Add(Base("executioner_positional_tn", ScenarioCategory.TrueNorth) with { InitialReaver = ReaverState.Executioner, TrueNorth = TrueNorthMode.Auto, PositionalCorrect = false });
        scenarios.Add(Base("bloodstalk_then_true_north", ScenarioCategory.TrueNorth) with { Level = 70, KillTime = 8, RedGauge = 100, BlueGauge = 0, InitialSoulSliceUsed = true, TrueNorth = TrueNorthMode.Auto, PositionalCorrect = false });
        scenarios.Add(Base("gluttony_then_true_north", ScenarioCategory.TrueNorth) with { Level = 96, KillTime = 8, Potion = PotionMode.Off, RedGauge = 50, BlueGauge = 0, InitialArcaneCircleReadyIn = 120, InitialSoulSliceUsed = true, TrueNorth = TrueNorthMode.Auto, PositionalCorrect = false });
    }

    private static void AddLevelSync(List<ScenarioDefinition> scenarios)
    {
        foreach (var level in new[] { 50, 60, 70, 72, 76, 80, 82, 88, 90, 92, 96, 100 })
            scenarios.Add(Base($"level_sync_{level}", ScenarioCategory.LevelSync) with { Level = level, KillTime = 180 });

        scenarios.Add(Base("level80_enshroud_final_reaping", ScenarioCategory.LevelSync) with { Level = 80, KillTime = 30, Potion = PotionMode.Off, DeathsDesignLeft = 60, RedGauge = 0, BlueGauge = 100 });
        scenarios.Add(Base("level82_enshroud_final_reaping", ScenarioCategory.LevelSync) with { Level = 82, KillTime = 30, Potion = PotionMode.Off, DeathsDesignLeft = 60, RedGauge = 0, BlueGauge = 100 });
    }

    private static void AddWeaveValidation(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("opener_2gcd_weave_order", ScenarioCategory.WeaveValidation) with { Opener = OpenerBurstMode.TwoGcd, Potion = PotionMode.OpenerAndEvenBurst, KillTime = 180, DeathsDesignLeft = 0, RedGauge = 0, BlueGauge = 0, SoulSliceCharges = 2 });
        scenarios.Add(Base("opener_25s_weave_order", ScenarioCategory.WeaveValidation) with { Opener = OpenerBurstMode.TwoPointFiveSecond, Potion = PotionMode.OpenerAndEvenBurst, KillTime = 180, DeathsDesignLeft = 0, RedGauge = 0, BlueGauge = 0 });
        scenarios.Add(Base("opener_0s_weave_order", ScenarioCategory.WeaveValidation) with { Opener = OpenerBurstMode.ZeroSecond, Potion = PotionMode.OpenerAndEvenBurst, KillTime = 180, DeathsDesignLeft = 0, RedGauge = 0, BlueGauge = 0 });
        scenarios.Add(Base("even_burst_pot_ac_order", ScenarioCategory.WeaveValidation) with { KillTime = 260, Potion = PotionMode.OpenerAndEvenBurst, BlueGauge = 100, RedGauge = 50, ExpectDoubleEnshroud = true, ExpectTwoCommunio = true, ExpectPerfectio = true });
        scenarios.Add(Base("long_fight_potion_recast_and_later_order", ScenarioCategory.WeaveValidation) with { KillTime = 500, Potion = PotionMode.OpenerAndEvenBurst });
        scenarios.Add(Base("even_burst_ac_sacrificium_order", ScenarioCategory.WeaveValidation) with { KillTime = 260, BlueGauge = 100, RedGauge = 50, ExpectDoubleEnshroud = true, ExpectTwoCommunio = true, ExpectPerfectio = true, ExpectSacrificium = true });
        scenarios.Add(Base("even_burst_lemure_slice_density", ScenarioCategory.WeaveValidation) with { KillTime = 260, BlueGauge = 100, RedGauge = 50, ExpectDoubleEnshroud = true, ExpectTwoCommunio = true, ExpectLemure = true, ExpectSacrificium = true });
        scenarios.Add(Base("even_burst_no_triple_weave", ScenarioCategory.WeaveValidation) with { KillTime = 260, BlueGauge = 100, RedGauge = 100, ExpectDoubleEnshroud = true, ExpectTwoCommunio = true, ExpectPerfectio = true, ExpectLemure = true, ExpectSacrificium = true });
        scenarios.Add(Base("high_ping_single_weave_softcheck", ScenarioCategory.WeaveValidation) with { KillTime = 260, BlueGauge = 100, RedGauge = 50, ExpectLemure = true, ExpectSacrificium = true });
        scenarios.Add(Base("gcd_247_weave_density", ScenarioCategory.WeaveValidation) with { Gcd = 2.47, KillTime = 260, BlueGauge = 100, RedGauge = 100, ExpectDoubleEnshroud = true, ExpectTwoCommunio = true, ExpectPerfectio = true });
        scenarios.Add(Base("gcd_249_weave_density", ScenarioCategory.WeaveValidation) with { Gcd = 2.49, KillTime = 260, BlueGauge = 100, RedGauge = 100, ExpectDoubleEnshroud = true, ExpectTwoCommunio = true, ExpectPerfectio = true });
    }

    private static void AddDancingMadProfile(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("dmu_top_profile_opener", ScenarioCategory.ArcanePotion) with
        {
            SkillRotation = SkillRotationMode.DancingMad,
            Opener = OpenerBurstMode.TwoGcd,
            Potion = PotionMode.OpenerAndEvenBurst,
            DeathsDesignLeft = 0,
            RedGauge = 0,
            BlueGauge = 0,
            SoulSliceCharges = 2,
            KillTime = 35
        });
        scenarios.Add(Base("dmu_top_profile_first_potion", ScenarioCategory.ArcanePotion) with
        {
            SkillRotation = SkillRotationMode.DancingMad,
            Potion = PotionMode.EvenBurstExceptOpener,
            InitialSoulSliceUsed = true,
            InitialArcaneCircleReadyIn = 118.1,
            KillTime = 140
        });
        scenarios.Add(Base("dmu_top_profile_potion_off", ScenarioCategory.ArcanePotion) with
        {
            SkillRotation = SkillRotationMode.DancingMad,
            Potion = PotionMode.Off,
            InitialSoulSliceUsed = true,
            InitialArcaneCircleReadyIn = 118.1,
            KillTime = 140
        });
        scenarios.Add(Base("dmu_top_profile_missed_ac_recovery", ScenarioCategory.ArcanePotion) with
        {
            SkillRotation = SkillRotationMode.DancingMad,
            Potion = PotionMode.Off,
            InitialSoulSliceUsed = true,
            InitialArcaneCircleReadyIn = 118.1,
            KillTime = 150,
            Events = [Event(ScenarioEventType.TargetLost, 116, 127)]
        });
        scenarios.Add(Base("dmu_random_assignment_cone_reachable", ScenarioCategory.RangedUptime) with
        {
            SkillRotation = SkillRotationMode.DancingMad,
            Potion = PotionMode.Off,
            InitialSoulSliceUsed = true,
            InitialArcaneCircleReadyIn = 118.1,
            DeathsDesignLeft = 60,
            BlueGauge = 100,
            SoulSliceCharges = 2,
            KillTime = 145,
            Events =
            [
                // Cover all four 1.5s Reapings after the first legal Enshroud weave.
                Event(ScenarioEventType.MeleeUnavailable, 2, 10, 6),
                Event(ScenarioEventType.MeleeUnavailable, 113, 126, 6)
            ]
        });
        scenarios.Add(Base("dmu_random_assignment_line_reachable", ScenarioCategory.RangedUptime) with
        {
            SkillRotation = SkillRotationMode.DancingMad,
            Potion = PotionMode.Off,
            InitialSoulSliceUsed = true,
            InitialArcaneCircleReadyIn = 118.1,
            DeathsDesignLeft = 60,
            BlueGauge = 100,
            SoulSliceCharges = 2,
            KillTime = 145,
            // PH only unlocks six seconds after AC; keep line range until that first legal PH GCD.
            Events = [Event(ScenarioEventType.MeleeUnavailable, 113, 127, 12)]
        });
        scenarios.Add(Base("dmu_random_assignment_ranged_only", ScenarioCategory.RangedUptime) with
        {
            SkillRotation = SkillRotationMode.DancingMad,
            Potion = PotionMode.Off,
            InitialSoulSliceUsed = true,
            InitialArcaneCircleReadyIn = 118.1,
            DeathsDesignLeft = 60,
            BlueGauge = 100,
            SoulSliceCharges = 2,
            KillTime = 145,
            Events = [Event(ScenarioEventType.MeleeUnavailable, 113, 126, 20)]
        });
        scenarios.Add(Base("dmu_top_profile_full_timeline", ScenarioCategory.ArcanePotion) with
        {
            SkillRotation = SkillRotationMode.DancingMad,
            Potion = PotionMode.OpenerAndEvenBurst,
            DeathsDesignLeft = 0,
            RedGauge = 0,
            BlueGauge = 0,
            SoulSliceCharges = 2,
            KillTime = 1109,
            Events =
            [
                Event(ScenarioEventType.TargetLost, 197.3, 209.2),
                Event(ScenarioEventType.TargetLost, 381.5, 428.9),
                Event(ScenarioEventType.TargetLost, 719.1, 725.8),
                Event(ScenarioEventType.TargetLost, 856.1, 889.2)
            ]
        });
    }

    private static void AddWindurstThirdWalkProfile(List<ScenarioDefinition> scenarios)
    {
        scenarios.Add(Base("windurst_shantotto_profile", ScenarioCategory.ArcanePotion) with
        {
            SkillRotation = SkillRotationMode.WindurstThirdWalk,
            WindurstEncounter = WindurstEncounter.Shantotto,
            KillTime = 270
        });
        scenarios.Add(Base("windurst_alexander_profile", ScenarioCategory.ArcanePotion) with
        {
            SkillRotation = SkillRotationMode.WindurstThirdWalk,
            WindurstEncounter = WindurstEncounter.Alexander,
            KillTime = 250
        });
        scenarios.Add(Base("windurst_promathia_profile", ScenarioCategory.ArcanePotion) with
        {
            SkillRotation = SkillRotationMode.WindurstThirdWalk,
            WindurstEncounter = WindurstEncounter.Promathia,
            KillTime = 345
        });
        scenarios.Add(Base("windurst_hollow_king_profile", ScenarioCategory.ArcanePotion) with
        {
            SkillRotation = SkillRotationMode.WindurstThirdWalk,
            WindurstEncounter = WindurstEncounter.HollowKing,
            KillTime = 400
        });
        scenarios.Add(Base("windurst_profile_without_encounter_falls_back", ScenarioCategory.ArcanePotion) with
        {
            SkillRotation = SkillRotationMode.WindurstThirdWalk,
            WindurstEncounter = WindurstEncounter.None,
            KillTime = 150
        });
    }

    private static void AddExpandedValidationMatrix(List<ScenarioDefinition> scenarios)
    {
        foreach (var gcd in new[] { 2.47, 2.48, 2.49 })
        foreach (var opener in new[] { OpenerBurstMode.TwoGcd, OpenerBurstMode.TwoPointFiveSecond, OpenerBurstMode.ZeroSecond })
        foreach (var ddRoute in new[] { "one_dd", "two_dd" })
        foreach (var killTime in Enumerable.Range(0, 10).Select(i => 360 + i * 30))
        {
            var twoRefresh = ddRoute == "two_dd";
            var name = $"matrix_g{gcd:0.00}_{opener}_{ddRoute}_kt{killTime}";
            scenarios.Add(Base(name, ScenarioCategory.Enshroud) with
            {
                Gcd = gcd,
                Opener = opener,
                KillTime = killTime,
                DeathsDesignLeft = twoRefresh ? 3 : 24,
                EvenBurstOneRefreshEnough = !twoRefresh,
                EvenBurstTwoRefreshNeeded = twoRefresh,
                BlueGauge = 100,
                RedGauge = 50,
                ExpectDoubleEnshroud = true,
                ExpectTwoCommunio = true,
                ExpectPerfectio = true,
                ExpectLemure = true,
                ExpectSacrificium = true,
                ExpectSecondEnshroudBlueGauge = true
            });
        }

        foreach (var loss in new[]
        {
            ("target_loss_pre_ac", 114.0, 121.0),
            ("target_loss_enshroud", 118.0, 124.0),
            ("target_loss_pre_perfectio", 124.0, 130.0)
        })
            scenarios.Add(Base(loss.Item1, ScenarioCategory.Downtime) with
            {
                KillTime = 420,
                BlueGauge = 100,
                RedGauge = 50,
                Events = [Event(ScenarioEventType.TargetLost, loss.Item2, loss.Item3)]
            });

        scenarios.Add(Base("matrix_full_basic_full_even", ScenarioCategory.RotationMode) with
        {
            KillTime = 420,
            Events = [Mode(60, 150, RotationMode.Basic), Mode(150, 420, RotationMode.Full)]
        });
        scenarios.Add(Base("matrix_basic_full_basic_even", ScenarioCategory.RotationMode) with
        {
            InitialMode = RotationMode.Basic,
            KillTime = 420,
            Events = [Mode(60, 180, RotationMode.Full), Mode(180, 420, RotationMode.Basic)]
        });

        scenarios.Add(Base("matrix_single_three_four_single", ScenarioCategory.AoeTargeting) with
        {
            KillTime = 420,
            Events = [Targets(30, 90, 3), Targets(90, 150, 4), Targets(150, 420, 1)]
        });
        scenarios.Add(Base("matrix_four_three_single_four", ScenarioCategory.AoeTargeting) with
        {
            AoeTargets = 4,
            ConeTargets = 4,
            KillTime = 420,
            Events = [Targets(30, 90, 3), Targets(90, 150, 1), Targets(150, 420, 4)]
        });

        foreach (var level in new[] { 1, 5, 10, 15, 25, 30, 35, 45, 50, 55, 60, 65, 70, 72, 76, 78, 80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100 })
            scenarios.Add(Base($"matrix_level_{level}_even_burst", ScenarioCategory.LevelSync) with
            {
                Level = level,
                KillTime = 420,
                BlueGauge = level >= 80 ? 100 : 50,
                RedGauge = level >= 70 ? 50 : 0,
                ExpectDoubleEnshroud = level >= 100,
                ExpectTwoCommunio = level >= 100,
                ExpectPerfectio = level >= 100,
                ExpectLemure = level >= 100,
                ExpectSacrificium = level >= 100,
                ExpectSecondEnshroudBlueGauge = level >= 100
            });
    }

    private static ScenarioDefinition Base(string name, ScenarioCategory category)
        => new()
        {
            Name = name,
            Category = category,
            Seed = StableSeed(name),
            KillTime = 240,
            Gcd = 2.49,
            Opener = OpenerBurstMode.TwoGcd,
            Potion = PotionMode.OpenerAndEvenBurst,
            InitialMode = RotationMode.Full,
            Level = 100,
            DeathsDesignLeft = 30,
            RedGauge = 50,
            BlueGauge = 50,
            SoulSliceCharges = 1,
            AoeTargets = 1,
            ConeTargets = 1
        };

    private static ScenarioEvent Event(ScenarioEventType type, double start, double end, double value = 0)
        => new() { Type = type, Start = start, End = end, Value = value };

    private static ScenarioEvent Mode(double start, double end, RotationMode mode)
        => new() { Type = ScenarioEventType.ModeSwitch, Start = start, End = end, Mode = mode };

    private static ScenarioEvent Targets(double start, double end, int targets)
        => new() { Type = ScenarioEventType.AoeTargets, Start = start, End = end, Value = targets };

    private static int StableSeed(string text)
    {
        unchecked
        {
            var hash = 17;
            foreach (var ch in text)
                hash = hash * 31 + ch;
            return Math.Abs(hash);
        }
    }
}
