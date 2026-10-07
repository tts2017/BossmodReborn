namespace MnkRegression;

public static class ScenarioCatalog
{
    public static IReadOnlyList<ScenarioDefinition> All { get; } = Build();

    public static IReadOnlyList<ScenarioDefinition> Select(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector) || selector.Equals("all", StringComparison.OrdinalIgnoreCase))
            return All;

        if (selector.Equals("dancing_mad", StringComparison.OrdinalIgnoreCase))
            return All.Where(s => s.Category == ScenarioCategory.DancingMad).ToList();
        if (selector.Equals("lookaway", StringComparison.OrdinalIgnoreCase))
            return All.Where(s => s.Category == ScenarioCategory.LookAway).ToList();
        if (selector.Equals("roe", StringComparison.OrdinalIgnoreCase))
            return All.Where(s => s.Category == ScenarioCategory.RiddleOfEarth).ToList();
        if (selector.Equals("combat_matrix", StringComparison.OrdinalIgnoreCase))
            return All.Where(s => s.Category == ScenarioCategory.CombatMatrix).ToList();

        return All.Where(s =>
            s.Name.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || s.Category.ToString().Equals(selector, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static IReadOnlyList<BattleScenario> SelectBattle(string? selector)
        => Select(selector).Select(ToBattleScenario).ToList();

    public static BattleScenario ToBattleScenario(ScenarioDefinition source)
    {
        var initial = new BattleState
        {
            TargetCount = source.TargetCount,
            NumAOETargets = source.TargetCount,
            NumMeleeAOETargets = source.TargetCount,
            EncounterHint = source.EncounterHint,
            ThunderclapSafe = true
        };

        List<BattleEvent> events =
        [
            BattleEvent.TargetCountWindow(0, source.DurationSeconds + 1, source.TargetCount, source.TargetCount, source.TargetCount)
        ];

        foreach (var ev in source.EventList)
            AddConvertedEvent(events, source, ev);

        AddScenarioSpecificEvents(events, source);

        if (source.InitialGauge != null)
            events.Add(BattleEvent.GaugeSnapshotEvent(0, source.InitialGauge));
        if (source.InitialCooldowns != null)
            events.Add(BattleEvent.CooldownSnapshotEvent(0, source.InitialCooldowns));

        var strategy = new StrategyProfile(
            source.GcdSeconds,
            source.OpenerRoFOffset,
            source.BurstTiming,
            source.EncounterHint,
            source.ContentId,
            source.RotationMode,
            source.PBStrategy,
            source.BlitzStrategy,
            source.NadiStrategy,
            source.RoFStrategy,
            source.BrotherhoodStrategy,
            source.RoWStrategy,
            source.RoEStrategy,
            source.ThunderclapStrategy,
            source.LevelCap);
        var tags = new[]
        {
            source.Category.ToString(),
            source.BurstTiming.ToString(),
            source.OpenerRoFOffset.ToString(),
            source.RotationMode.ToString(),
            source.PBStrategy.ToString(),
            source.BlitzStrategy.ToString()
        };

        return new(source.Name, source.Category, source.DurationSeconds, 0.05, strategy, initial, events.OrderBy(e => e.Start).ThenBy(e => e.Kind).ToList(), tags, source);
    }

    private static void AddConvertedEvent(List<BattleEvent> events, ScenarioDefinition source, ScenarioEvent ev)
    {
        switch (ev.Type)
        {
            case ScenarioEventType.TargetLost:
                events.Add(BattleEvent.TargetableWindow(ev.Start, ev.End, targetable: false, haveTarget: false));
                events.Add(BattleEvent.MeleeWindow(ev.Start, ev.End, canMelee: false));
                break;
            case ScenarioEventType.MeleeUnavailable:
                events.Add(BattleEvent.MeleeWindow(ev.Start, ev.End, canMelee: false));
                break;
            case ScenarioEventType.ForbiddenZone:
                events.Add(BattleEvent.ForbiddenZoneWindow(ev.Start, ev.End));
                break;
            case ScenarioEventType.LookAway:
                events.Add(BattleEvent.LookAwayWindow(ev.Start, ev.End));
                break;
            case ScenarioEventType.PredictedDamage:
                events.Add(BattleEvent.PredictedDamage(ev.Start, ev.DamageType, ev.AppliesToSelf));
                break;
            case ScenarioEventType.PhaseEnd:
                events.Add(BattleEvent.PhaseEndEstimate(ev.End));
                events.Add(BattleEvent.DowntimeEstimate(ev.Start));
                break;
            case ScenarioEventType.FightEnd:
                events.Add(BattleEvent.FightEndEstimate(ev.Start));
                break;
            case ScenarioEventType.BossReturn:
                events.Add(BattleEvent.EncounterHintWindow(ev.Start, ev.End, EncounterHintMode.BossReturn));
                break;
            case ScenarioEventType.HoldBurst:
                events.Add(BattleEvent.EncounterHintWindow(ev.Start, ev.End, EncounterHintMode.HoldBurst));
                break;
            case ScenarioEventType.UnsafeThunderclap:
                events.Add(BattleEvent.ThunderclapSafetyWindow(ev.Start, ev.End, safe: false));
                break;
        }
    }

    private static void AddScenarioSpecificEvents(List<BattleEvent> events, ScenarioDefinition source)
    {
        if (source.Category == ScenarioCategory.TargetLostDuringPB)
        {
            events.Add(BattleEvent.GaugeSnapshotEvent(60, new()
            {
                PerfectBalanceLeft = 12,
                PerfectBalanceStacks = source.Name.Contains("after_1st", StringComparison.OrdinalIgnoreCase) ? 2 :
                    source.Name.Contains("after_2nd", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                BeastChakra = source.Name.Contains("blitz", StringComparison.OrdinalIgnoreCase)
                    ? ["Opo", "Raptor", "Coeurl"]
                    : ["Opo"],
                BlitzLeft = source.Name.Contains("blitz", StringComparison.OrdinalIgnoreCase) ? 18 : 0,
                LunarNadi = true,
                SolarNadi = false,
                CurrentForm = "Raptor"
            }));
        }

        if (source.Category == ScenarioCategory.DancingMad)
        {
            events.Add(BattleEvent.TargetableWindow(197.0, 206.0, targetable: false, haveTarget: false));
            events.Add(BattleEvent.MeleeWindow(197.0, 206.0, canMelee: false));
            events.Add(BattleEvent.DowntimeEstimate(197.0));
            events.Add(BattleEvent.PhaseEndEstimate(197.0));
            events.Add(BattleEvent.GaugeSnapshotEvent(180.0, new()
            {
                RiddleOfFireLeft = 18,
                BrotherhoodLeft = 0,
                PerfectBalanceLeft = 10,
                PerfectBalanceStacks = 2,
                CurrentForm = "Raptor",
                LunarNadi = true,
                SolarNadi = false
            }));
            events.Add(BattleEvent.CooldownSnapshotEvent(206.0, new()
            {
                PerfectBalanceCharges = 2,
                RiddleOfFireReadyIn = 34,
                BrotherhoodReadyIn = 34,
                PotionReadyIn = 34
            }));
        }

        if (source.Category == ScenarioCategory.MechanicHints && source.Name.Contains("thunderclap", StringComparison.OrdinalIgnoreCase))
        {
            var unsafeEvent = source.EventList.FirstOrDefault(e => e.Type == ScenarioEventType.UnsafeThunderclap);
            if (unsafeEvent == null)
                events.Add(BattleEvent.ThunderclapSafetyWindow(0, source.DurationSeconds, safe: true));
        }
    }

    private static IReadOnlyList<ScenarioDefinition> Build()
    {
        List<ScenarioDefinition> scenarios =
        [
            new("full_uptime_8m_gcd247_standard78", ScenarioCategory.FullUptime, 480, 2.47, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown),
            new("full_uptime_8m_gcd249_standard78", ScenarioCategory.FullUptime, 480, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown),
            new("full_uptime_8m_gcd247_early58", ScenarioCategory.FullUptime, 480, 2.47, OpenerRoFOffsetMode.Early58, BurstTimingMode.Cooldown),
            new("full_uptime_8m_gcd249_early58", ScenarioCategory.FullUptime, 480, 2.49, OpenerRoFOffsetMode.Early58, BurstTimingMode.Cooldown),
            new("full_uptime_8m_zero_second", ScenarioCategory.FullUptime, 480, 2.49, OpenerRoFOffsetMode.ZeroSecondBurst, BurstTimingMode.Cooldown),
            new("full_uptime_10m_party_burst_aligned", ScenarioCategory.FullUptime, 600, 2.49, OpenerRoFOffsetMode.PartyBurstAligned, BurstTimingMode.SynergyFixed),
            new("full_uptime_12m_synergy_fixed", ScenarioCategory.FullUptime, 720, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed),

            new("burst_timing_synergy_fixed", ScenarioCategory.BurstTiming, 480, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed),
            new("burst_timing_melee_safe", ScenarioCategory.BurstTiming, 480, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe),
            new("burst_timing_cooldown", ScenarioCategory.BurstTiming, 480, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown),
        ];

        foreach (var second in new[] { 50d, 55d, 58d, 60d, 62d })
            scenarios.Add(new($"odd_burst_{(second == 62 ? "resume" : "melee_loss")}_{second:0}s", ScenarioCategory.OddBurstMeleeLoss, 240, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe,
                Events: [new(ScenarioEventType.MeleeUnavailable, second, second + 4)]));

        foreach (var second in new[] { 110d, 115d, 118d, 120d, 123d })
            scenarios.Add(new($"even_burst_{(second == 123 ? "resume" : "melee_loss")}_{second:0}s", ScenarioCategory.EvenBurstMeleeLoss, 300, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe,
                Events: [new(ScenarioEventType.MeleeUnavailable, second, second + 5)]));

        scenarios.AddRange([
            new("pb_target_lost_after_1st_gcd", ScenarioCategory.TargetLostDuringPB, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.TargetLost, 62.6, 68)]),
            new("pb_target_lost_after_2nd_gcd", ScenarioCategory.TargetLostDuringPB, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.TargetLost, 65.1, 70)]),
            new("pb_target_lost_after_3rd_gcd", ScenarioCategory.TargetLostDuringPB, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.TargetLost, 67.5, 72)]),
            new("blitz_ready_target_lost", ScenarioCategory.TargetLostDuringPB, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.TargetLost, 69.8, 75)]),
            new("blitz_unspent_phase_transition", ScenarioCategory.TargetLostDuringPB, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.TargetLost, 69.8, 82), new(ScenarioEventType.PhaseEnd, 69.8, 82)]),

            new("dancing_mad_p1_p2_resume", ScenarioCategory.DancingMad, 300, 2.49, OpenerRoFOffsetMode.ZeroSecondBurst, BurstTimingMode.SynergyFixed, ContentId: 1094, Events: [new(ScenarioEventType.TargetLost, 197, 206), new(ScenarioEventType.PhaseEnd, 197, 206)]),

            new("lookaway_before_gcd", ScenarioCategory.LookAway, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.LookAway, 20, 23)]),
            new("lookaway_before_ogcd", ScenarioCategory.LookAway, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.LookAway, 7, 9)]),
            new("lookaway_during_pb", ScenarioCategory.LookAway, 160, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.LookAway, 63, 67)]),
            new("lookaway_before_blitz", ScenarioCategory.LookAway, 160, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.LookAway, 68, 70)]),
            new("lookaway_before_rof_bh", ScenarioCategory.LookAway, 160, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.LookAway, 118, 121)]),

            new("forbidden_next_gcd_only", ScenarioCategory.MechanicHints, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe, Events: [new(ScenarioEventType.ForbiddenZone, 30, 32)]),
            new("forbidden_two_gcds", ScenarioCategory.MechanicHints, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe, Events: [new(ScenarioEventType.ForbiddenZone, 30, 36)]),
            new("forbidden_during_rof", ScenarioCategory.MechanicHints, 160, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe, Events: [new(ScenarioEventType.ForbiddenZone, 60, 64)]),
            new("forbidden_during_bh", ScenarioCategory.MechanicHints, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe, Events: [new(ScenarioEventType.ForbiddenZone, 120, 124)]),
            new("forbidden_during_pb", ScenarioCategory.MechanicHints, 160, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe, Events: [new(ScenarioEventType.ForbiddenZone, 63, 67)]),
            new("safe_thunderclap_available", ScenarioCategory.MechanicHints, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe, Events: [new(ScenarioEventType.MeleeUnavailable, 35, 38)]),
            new("unsafe_thunderclap_blocked", ScenarioCategory.MechanicHints, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe, Events: [new(ScenarioEventType.MeleeUnavailable, 35, 38), new(ScenarioEventType.UnsafeThunderclap, 35, 38)]),

            new("roe_raidwide_8s", ScenarioCategory.RiddleOfEarth, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, Events: [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Raidwide)]),
            new("roe_shared_8s", ScenarioCategory.RiddleOfEarth, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, Events: [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Shared)]),
            new("roe_tankbuster_8s", ScenarioCategory.RiddleOfEarth, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, Events: [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Tankbuster)]),
            new("roe_non_self_damage_8s", ScenarioCategory.RiddleOfEarth, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, Events: [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Raidwide, AppliesToSelf: false)]),
            new("roe_auto_attack_8s", ScenarioCategory.RiddleOfEarth, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, Events: [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.AutoAttack)]),
            new("roe_raidwide_12s", ScenarioCategory.RiddleOfEarth, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, Events: [new(ScenarioEventType.PredictedDamage, 12, 12, PredictedDamageKind.Raidwide)]),
        ]);

        scenarios.AddRange([
            new("single_target", ScenarioCategory.TargetCount, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, TargetCount: 1),
            new("two_targets", ScenarioCategory.TargetCount, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, TargetCount: 2),
            new("three_targets_short", ScenarioCategory.TargetCount, 80, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, TargetCount: 3),
            new("three_targets_long", ScenarioCategory.TargetCount, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, TargetCount: 3, LongAoe: true),
            new("four_targets_long", ScenarioCategory.TargetCount, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, TargetCount: 4, LongAoe: true),
            new("boss_return_with_adds", ScenarioCategory.TargetCount, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, EncounterHint: EncounterHintMode.BossReturn, TargetCount: 3, Events: [new(ScenarioEventType.BossReturn, 0, 40)]),
            new("alliance_trash", ScenarioCategory.TargetCount, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, EncounterHint: EncounterHintMode.Trash, TargetCount: 4, LongAoe: true),
            new("major_add", ScenarioCategory.TargetCount, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, TargetCount: 2),

            new("phase_end_15s", ScenarioCategory.EndBurn, 80, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.PhaseEnd, 65, 80)]),
            new("phase_end_25s", ScenarioCategory.EndBurn, 100, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.PhaseEnd, 75, 100)]),
            new("phase_end_45s", ScenarioCategory.EndBurn, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.PhaseEnd, 75, 120)]),
            new("fight_end_70s", ScenarioCategory.EndBurn, 70, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.FightEnd, 70, 70)]),
            new("fight_end_120s", ScenarioCategory.EndBurn, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.FightEnd, 120, 120)]),
            new("fight_end_ignore", ScenarioCategory.EndBurn, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, Events: [new(ScenarioEventType.FightEnd, 120, 120)]),
            new("planner_phase_end", ScenarioCategory.EndBurn, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.PhaseEnd, 150, 180)]),
            new("planner_final_phase", ScenarioCategory.EndBurn, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed, Events: [new(ScenarioEventType.FightEnd, 180, 180)]),
        ]);

        scenarios.AddRange([
            new("strategy_basic_chakra_overcap_no_auto_burst", ScenarioCategory.StrategyMatrix, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                RotationMode: RotationMode.BasicAndChakraOvercap),
            new("strategy_pb_force_holdburst_allowed", ScenarioCategory.StrategyMatrix, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                EncounterHint: EncounterHintMode.HoldBurst, PBStrategy: PBStrategyMode.Force),
            new("strategy_pb_delay_no_even_pb_requirement", ScenarioCategory.StrategyMatrix, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                PBStrategy: PBStrategyMode.Delay),
            new("strategy_rof_delay_bh_delay_no_sync_requirement", ScenarioCategory.StrategyMatrix, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                RoFStrategy: AutoForceDelayMode.Delay, BrotherhoodStrategy: AutoForceDelayMode.Delay),
            new("strategy_rof_force_bh_force_holdburst_allowed", ScenarioCategory.StrategyMatrix, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                EncounterHint: EncounterHintMode.HoldBurst, RoFStrategy: AutoForceDelayMode.Force, BrotherhoodStrategy: AutoForceDelayMode.Force),
            new("strategy_row_delay_no_row_use", ScenarioCategory.StrategyMatrix, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                RoWStrategy: AutoForceDelayMode.Delay),
            new("strategy_roe_delay_ignores_predicted_damage", ScenarioCategory.StrategyMatrix, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                RoEStrategy: AutoForceDelayMode.Delay, Events: [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Raidwide)]),
            new("strategy_tc_none_melee_unavailable", ScenarioCategory.StrategyMatrix, 80, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe,
                ThunderclapStrategy: ThunderclapStrategyMode.None, Events: [new(ScenarioEventType.MeleeUnavailable, 20, 26)]),
            new("strategy_blitz_force_pending_both_nadi", ScenarioCategory.StrategyMatrix, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                BlitzStrategy: BlitzStrategyMode.Force,
                InitialGauge: new() { BeastChakra = ["Opo", "Raptor", "Coeurl"], LunarNadi = true, SolarNadi = true, BlitzLeft = 18, CurrentForm = "Raptor" }),
            new("strategy_blitz_delay_pending_no_forced_use", ScenarioCategory.StrategyMatrix, 40, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                BlitzStrategy: BlitzStrategyMode.Delay,
                InitialGauge: new() { BeastChakra = ["Opo", "Raptor", "Coeurl"], LunarNadi = true, SolarNadi = true, BlitzLeft = 18, CurrentForm = "Raptor" }),
            new("strategy_blitz_multi_single_target_hold", ScenarioCategory.StrategyMatrix, 80, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                BlitzStrategy: BlitzStrategyMode.Multi,
                InitialGauge: new() { BeastChakra = ["Opo", "Raptor", "Coeurl"], LunarNadi = false, SolarNadi = true, BlitzLeft = 18, CurrentForm = "Raptor" }),
            new("strategy_blitz_multi_three_targets_use", ScenarioCategory.StrategyMatrix, 80, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                TargetCount: 3, LongAoe: true, BlitzStrategy: BlitzStrategyMode.Multi,
                InitialGauge: new() { BeastChakra = ["Opo", "Raptor", "Coeurl"], LunarNadi = false, SolarNadi = true, BlitzLeft = 18, CurrentForm = "Raptor" }),
        ]);

        scenarios.AddRange([
            new("low_level_50_pb_no_blitz_bh_rof", ScenarioCategory.LowLevel, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, LevelCap: 50),
            new("low_level_64_roe_available_no_bh", ScenarioCategory.LowLevel, 80, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                LevelCap: 64, Events: [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Raidwide)]),
            new("low_level_70_bh_without_row", ScenarioCategory.LowLevel, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown, LevelCap: 70),
        ]);

        scenarios.AddRange([
            new("nadi_none_even_rebuild", ScenarioCategory.ResourceCarryover, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                InitialGauge: new() { LunarNadi = false, SolarNadi = false, CurrentForm = "Raptor" }),
            new("nadi_lunar_only_even_rebuild", ScenarioCategory.ResourceCarryover, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                InitialGauge: new() { LunarNadi = true, SolarNadi = false, CurrentForm = "Raptor" }),
            new("nadi_solar_only_even_rebuild", ScenarioCategory.ResourceCarryover, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                InitialGauge: new() { LunarNadi = false, SolarNadi = true, CurrentForm = "Raptor" }),
            new("nadi_both_pending_phantom_rush", ScenarioCategory.ResourceCarryover, 80, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                InitialGauge: new() { BeastChakra = ["Opo", "Raptor", "Coeurl"], LunarNadi = true, SolarNadi = true, BlitzLeft = 18, CurrentForm = "Raptor" }),
            new("formless_blitz_held_after_downtime", ScenarioCategory.ResourceCarryover, 80, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                InitialGauge: new() { BeastChakra = ["Opo", "Raptor", "Coeurl"], LunarNadi = true, SolarNadi = true, BlitzLeft = 18, FormlessFistLeft = 30, CurrentForm = "Opo" },
                Events: [new(ScenarioEventType.TargetLost, 0, 8)]),
        ]);

        scenarios.AddRange([
            new("boss_return_just_before_odd_burst", ScenarioCategory.BossReturnBoundary, 120, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed,
                Events: [new(ScenarioEventType.BossReturn, 54, 62)]),
            new("boss_return_just_after_odd_burst", ScenarioCategory.BossReturnBoundary, 140, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed,
                Events: [new(ScenarioEventType.BossReturn, 63, 72)]),
            new("hold_burst_just_before_even_burst", ScenarioCategory.BossReturnBoundary, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed,
                Events: [new(ScenarioEventType.HoldBurst, 114, 122)]),
            new("target_lost_just_before_even_burst", ScenarioCategory.BossReturnBoundary, 180, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed,
                Events: [new(ScenarioEventType.TargetLost, 116, 123)]),
            new("target_returns_with_pb2_and_bh_delayed", ScenarioCategory.BossReturnBoundary, 260, 2.49, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed,
                Events: [new(ScenarioEventType.TargetLost, 116, 130)],
                InitialCooldowns: new() { PerfectBalanceCharges = 2, BrotherhoodReadyIn = 20, RiddleOfFireReadyIn = 0 }),
        ]);

        scenarios.AddRange(BuildCombatMatrixScenarios());

        return scenarios;
    }

    private static IEnumerable<ScenarioDefinition> BuildCombatMatrixScenarios()
    {
        const double defaultDuration = 240;
        const double defaultGcd = 2.49;

        foreach (var opener in Enum.GetValues<OpenerRoFOffsetMode>())
            yield return new($"combat_matrix_opener_{MatrixName(opener)}", ScenarioCategory.CombatMatrix, defaultDuration, defaultGcd, opener, BurstTimingMode.Cooldown);

        foreach (var burstTiming in Enum.GetValues<BurstTimingMode>())
            yield return new($"combat_matrix_burst_timing_{MatrixName(burstTiming)}", ScenarioCategory.CombatMatrix, defaultDuration, defaultGcd, OpenerRoFOffsetMode.Standard78, burstTiming);

        foreach (var opener in Enum.GetValues<OpenerRoFOffsetMode>())
            foreach (var burstTiming in Enum.GetValues<BurstTimingMode>())
                yield return new($"combat_matrix_opener_{MatrixName(opener)}_{MatrixName(burstTiming)}", ScenarioCategory.CombatMatrix, defaultDuration, defaultGcd, opener, burstTiming);

        foreach (var encounterHint in Enum.GetValues<EncounterHintMode>())
            yield return new($"combat_matrix_encounter_{MatrixName(encounterHint)}", ScenarioCategory.CombatMatrix, 180, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.SynergyFixed,
                EncounterHint: encounterHint,
                Events: EncounterEvents(encounterHint));

        foreach (var rotationMode in Enum.GetValues<RotationMode>())
            yield return new($"combat_matrix_rotation_{MatrixName(rotationMode)}", ScenarioCategory.CombatMatrix, 180, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                RotationMode: rotationMode);

        foreach (var pbStrategy in Enum.GetValues<PBStrategyMode>())
            foreach (var blitzStrategy in Enum.GetValues<BlitzStrategyMode>())
                yield return new($"combat_matrix_pb_{MatrixName(pbStrategy)}_blitz_{MatrixName(blitzStrategy)}", ScenarioCategory.CombatMatrix, 180, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                    PBStrategy: pbStrategy,
                    BlitzStrategy: blitzStrategy,
                    InitialGauge: PendingBlitzGauge(blitzStrategy));

        foreach (var nadiStrategy in Enum.GetValues<NadiStrategyMode>())
            yield return new($"combat_matrix_nadi_{MatrixName(nadiStrategy)}", ScenarioCategory.CombatMatrix, defaultDuration, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                NadiStrategy: nadiStrategy);

        foreach (var roFStrategy in Enum.GetValues<AutoForceDelayMode>())
            foreach (var brotherhoodStrategy in Enum.GetValues<AutoForceDelayMode>())
                yield return new($"combat_matrix_rof_{MatrixName(roFStrategy)}_bh_{MatrixName(brotherhoodStrategy)}", ScenarioCategory.CombatMatrix, 180, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                    RoFStrategy: roFStrategy,
                    BrotherhoodStrategy: brotherhoodStrategy);

        foreach (var rowStrategy in Enum.GetValues<AutoForceDelayMode>())
            foreach (var roeStrategy in Enum.GetValues<AutoForceDelayMode>())
                yield return new($"combat_matrix_row_{MatrixName(rowStrategy)}_roe_{MatrixName(roeStrategy)}", ScenarioCategory.CombatMatrix, 180, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                    RoWStrategy: rowStrategy,
                    RoEStrategy: roeStrategy,
                    Events: [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Raidwide)]);

        foreach (var tcStrategy in Enum.GetValues<ThunderclapStrategyMode>())
            yield return new($"combat_matrix_tc_{MatrixName(tcStrategy)}", ScenarioCategory.CombatMatrix, 90, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe,
                ThunderclapStrategy: tcStrategy,
                Events: [new(ScenarioEventType.MeleeUnavailable, 20, 26), new(ScenarioEventType.UnsafeThunderclap, 20, 26)]);

        foreach (var level in new[] { 1, 15, 18, 20, 26, 30, 35, 38, 40, 45, 50, 52, 54, 60, 64, 68, 70, 72, 74, 76, 80, 82, 86, 88, 90, 92, 96, 100 })
            yield return new($"combat_matrix_level_{level}", ScenarioCategory.CombatMatrix, 180, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                LevelCap: level,
                Events: level >= 64 ? [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Raidwide)] : null);

        foreach (var targetCount in new[] { 1, 2, 3, 4 })
            yield return new($"combat_matrix_targets_{targetCount}", ScenarioCategory.CombatMatrix, 180, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                TargetCount: targetCount,
                LongAoe: targetCount >= 3);

        foreach (var gcd in new[] { 2.47, 2.49, 2.50 })
            yield return new($"combat_matrix_gcd_{gcd:0.00}".Replace('.', '_'), ScenarioCategory.CombatMatrix, defaultDuration, gcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown);

        foreach (var (name, events) in CombatEventPatterns())
            yield return new($"combat_matrix_event_{name}", ScenarioCategory.CombatMatrix, 180, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.MeleeSafe,
                Events: events);

        foreach (var (name, gauge) in CombatGaugePatterns())
            yield return new($"combat_matrix_gauge_{name}", ScenarioCategory.CombatMatrix, 120, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                InitialGauge: gauge);

        foreach (var (name, cooldowns) in CombatCooldownPatterns())
            yield return new($"combat_matrix_cooldown_{name}", ScenarioCategory.CombatMatrix, 180, defaultGcd, OpenerRoFOffsetMode.Standard78, BurstTimingMode.Cooldown,
                InitialCooldowns: cooldowns);
    }

    private static IReadOnlyList<ScenarioEvent>? EncounterEvents(EncounterHintMode encounterHint)
        => encounterHint switch
        {
            EncounterHintMode.BossReturn => [new(ScenarioEventType.BossReturn, 0, 40)],
            EncounterHintMode.HoldBurst => [new(ScenarioEventType.HoldBurst, 0, 40)],
            _ => null
        };

    private static GaugeSnapshot? PendingBlitzGauge(BlitzStrategyMode blitzStrategy)
        => blitzStrategy is BlitzStrategyMode.Force or BlitzStrategyMode.Delay or BlitzStrategyMode.Multi or BlitzStrategyMode.MultiRoF
            ? new() { BeastChakra = ["Opo", "Raptor", "Coeurl"], LunarNadi = true, SolarNadi = true, BlitzLeft = 18, CurrentForm = "Raptor" }
            : null;

    private static IEnumerable<(string Name, IReadOnlyList<ScenarioEvent> Events)> CombatEventPatterns()
    {
        yield return ("target_lost_short", [new(ScenarioEventType.TargetLost, 58, 62)]);
        yield return ("target_lost_long", [new(ScenarioEventType.TargetLost, 116, 130), new(ScenarioEventType.PhaseEnd, 116, 130)]);
        yield return ("melee_loss_short", [new(ScenarioEventType.MeleeUnavailable, 58, 62)]);
        yield return ("melee_loss_long", [new(ScenarioEventType.MeleeUnavailable, 116, 126)]);
        yield return ("forbidden_zone", [new(ScenarioEventType.ForbiddenZone, 60, 64)]);
        yield return ("lookaway", [new(ScenarioEventType.LookAway, 60, 63)]);
        yield return ("unsafe_thunderclap", [new(ScenarioEventType.MeleeUnavailable, 35, 38), new(ScenarioEventType.UnsafeThunderclap, 35, 38)]);
        yield return ("raidwide_8s", [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Raidwide)]);
        yield return ("shared_8s", [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Shared)]);
        yield return ("tankbuster_8s", [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Tankbuster)]);
        yield return ("non_self_damage", [new(ScenarioEventType.PredictedDamage, 8, 8, PredictedDamageKind.Raidwide, AppliesToSelf: false)]);
        yield return ("phase_end", [new(ScenarioEventType.PhaseEnd, 150, 180)]);
        yield return ("fight_end", [new(ScenarioEventType.FightEnd, 180, 180)]);
        yield return ("boss_return", [new(ScenarioEventType.BossReturn, 54, 62)]);
        yield return ("hold_burst", [new(ScenarioEventType.HoldBurst, 114, 122)]);
    }

    private static IEnumerable<(string Name, GaugeSnapshot Gauge)> CombatGaugePatterns()
    {
        yield return ("nadi_none", new() { LunarNadi = false, SolarNadi = false, CurrentForm = "Raptor" });
        yield return ("nadi_lunar", new() { LunarNadi = true, SolarNadi = false, CurrentForm = "Raptor" });
        yield return ("nadi_solar", new() { LunarNadi = false, SolarNadi = true, CurrentForm = "Raptor" });
        yield return ("nadi_both", new() { LunarNadi = true, SolarNadi = true, CurrentForm = "Raptor" });
        yield return ("pb_active_one_beast", new() { PerfectBalanceLeft = 10, PerfectBalanceStacks = 2, BeastChakra = ["Opo"], LunarNadi = true, SolarNadi = false, CurrentForm = "Raptor" });
        yield return ("pb_active_two_beast", new() { PerfectBalanceLeft = 8, PerfectBalanceStacks = 1, BeastChakra = ["Opo", "Raptor"], LunarNadi = true, SolarNadi = false, CurrentForm = "Coeurl" });
        yield return ("pending_blitz_lunar", new() { BeastChakra = ["Opo", "Raptor", "Coeurl"], LunarNadi = true, SolarNadi = false, BlitzLeft = 18, CurrentForm = "Raptor" });
        yield return ("pending_phantom_rush", new() { BeastChakra = ["Opo", "Raptor", "Coeurl"], LunarNadi = true, SolarNadi = true, BlitzLeft = 18, CurrentForm = "Raptor" });
        yield return ("formless_pending_blitz", new() { BeastChakra = ["Opo", "Raptor", "Coeurl"], LunarNadi = true, SolarNadi = true, BlitzLeft = 18, FormlessFistLeft = 30, CurrentForm = "Opo" });
    }

    private static IEnumerable<(string Name, CooldownSnapshot Cooldowns)> CombatCooldownPatterns()
    {
        yield return ("pb0_rof_ready_bh20", new() { PerfectBalanceCharges = 0, PerfectBalanceReadyIn = 40, RiddleOfFireReadyIn = 0, BrotherhoodReadyIn = 20 });
        yield return ("pb1_rof20_bh20", new() { PerfectBalanceCharges = 1, RiddleOfFireReadyIn = 20, BrotherhoodReadyIn = 20 });
        yield return ("pb2_rof_ready_bh_ready", new() { PerfectBalanceCharges = 2, RiddleOfFireReadyIn = 0, BrotherhoodReadyIn = 0 });
        yield return ("pb2_rof40_bh40", new() { PerfectBalanceCharges = 2, RiddleOfFireReadyIn = 40, BrotherhoodReadyIn = 40 });
        yield return ("row_ready_roe_ready", new() { RiddleOfWindReadyIn = 0, RiddleOfEarthReadyIn = 0 });
        yield return ("tc0_tn0", new() { ThunderclapCharges = 0, ThunderclapReadyIn = 20, TrueNorthCharges = 0, TrueNorthReadyIn = 20 });
    }

    private static string MatrixName<T>(T value)
        where T : struct, Enum
        => value.ToString().ToLowerInvariant();
}
