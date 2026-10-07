namespace RprRegression;

public static class HardFailRules
{
    public static void Apply(ScenarioResult result)
    {
        var failures = new List<HardFailRule>();
        failures.AddRange(CheckGcdStop(result));
        failures.AddRange(CheckIllegalActions(result));
        failures.AddRange(CheckBurst(result));
        failures.AddRange(CheckDancingMadPhaseEnshroudBudgets(result));
        failures.AddRange(CheckDancingMadRandomAssignmentPlanner(result));
        failures.AddRange(CheckDeathsDesign(result));
        failures.AddRange(CheckGauge(result));
        failures.AddRange(CheckDrift(result));
        failures.AddRange(CheckAoe(result));
        failures.AddRange(CheckModeSwitch(result));
        failures.AddRange(CheckWeaveAndPotion(result));
        failures.AddRange(CheckOpenerOrder(result));
        failures.AddRange(CheckEnhancedReaverSequence(result));
        failures.AddRange(CheckFastGcdLateSoulSliceBeforePlentifulHarvest(result));
        failures.AddRange(CheckPatch73EvenBurstSequence(result));
        failures.AddRange(CheckPlentifulHarvestBeforeSecondEvenBurstEnshroud(result));
        failures.AddRange(CheckPostPerfectioPrioritySequence(result));
        failures.AddRange(CheckPerfectioBeforeDeathsDesignAtArcaneExpiry(result));
        failures.AddRange(CheckEndingDutyBurstHold(result));
        failures.AddRange(CheckEmulatorSpecificationScenarios(result));

        result.HardFails.Clear();
        result.HardFails.AddRange(failures.Distinct().OrderBy(f => f));
        foreach (var frame in result.Frames)
            if (frame.HardFails.Count > 0)
                foreach (var fail in frame.HardFails)
                    if (!result.HardFails.Contains(fail))
                        result.HardFails.Add(fail);
    }

    private static IEnumerable<HardFailRule> CheckGcdStop(ScenarioResult result)
    {
        foreach (var frame in result.Frames)
        {
            if (frame.ExpectedActionPossible && frame.SelectedGcd == null)
                yield return HardFailRule.GcdStop;
            if (!frame.MeleeAvailable && frame.ExpectedActionPossible && frame.SelectedGcd == null && HasRangedOption(frame))
                yield return HardFailRule.GcdStop;
            if (frame.Level < 90 && frame.BlueSouls == 1 && frame.TargetAvailable && (frame.MeleeAvailable && frame.FallbackTargetAvailable || frame.BestConeTargetAvailable)
                && frame.SelectedGcd is not ("VoidReaping" or "CrossReaping" or "GrimReaping"))
                yield return HardFailRule.GcdStop;
        }
    }

    private static IEnumerable<HardFailRule> CheckIllegalActions(ScenarioResult result)
    {
        foreach (var frame in result.Frames)
        {
            if (frame.SelectedGcd != null && !RprRotationEmulator.Unlocked(frame.Level, frame.SelectedGcd))
                yield return HardFailRule.IllegalAction;
            foreach (var ogcd in frame.SelectedOgcds)
                if (!RprRotationEmulator.Unlocked(frame.Level, ogcd))
                    yield return HardFailRule.IllegalAction;

            if (!frame.FallbackTargetAvailable && IsHostileSingle(frame.SelectedGcd))
                yield return HardFailRule.IllegalAction;
            if (!frame.FallbackTargetAvailable && frame.SelectedOgcds.Any(IsHostileSingle))
                yield return HardFailRule.IllegalAction;
            if (!frame.MeleeAvailable && (IsMeleeThreeYardAction(frame.SelectedGcd) || frame.SelectedOgcds.Any(IsMeleeThreeYardAction)))
                yield return HardFailRule.IllegalAction;
            if (frame.SelectedGcd == "PlentifulHarvest" && !frame.BestLineTargetAvailable)
                yield return HardFailRule.IllegalAction;
            if (frame.SelectedGcd is "Communio" or "Perfectio" or "HarvestMoon" && !frame.BestRangedAoeTargetAvailable)
                yield return HardFailRule.IllegalAction;
            if (frame.SelectedOgcds.Any(action => action is "Gluttony" or "Sacrificium") && !frame.BestRangedAoeTargetAvailable)
                yield return HardFailRule.IllegalAction;
            if ((frame.SelectedGcd is "Guillotine" or "ExecutionersGuillotine" or "GrimReaping" || frame.SelectedOgcds.Any(action => action is "GrimSwathe" or "LemuresScythe")) && !frame.BestConeTargetAvailable)
                yield return HardFailRule.IllegalAction;
            if (frame.BlueSouls == 0 && frame.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" or "Communio")
                yield return HardFailRule.IllegalAction;
            if (frame.ReaverState == ReaverState.None && frame.SelectedGcd is "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine")
                yield return HardFailRule.IllegalAction;
            if (frame.ReaverState != ReaverState.Executioner && frame.SelectedGcd is "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine")
                yield return HardFailRule.IllegalAction;
            var purpleSoulsAfterGcd = frame.PurpleSouls + (frame.Level >= 86 && frame.SelectedGcd is ("VoidReaping" or "CrossReaping" or "GrimReaping") ? 1 : 0);
            var blueSoulsAfterGcd = frame.BlueSouls - (frame.SelectedGcd is ("VoidReaping" or "CrossReaping" or "GrimReaping") ? 1 : 0);
            if (frame.SelectedOgcds.Any(action => action is "LemuresSlice" or "LemuresScythe") && (purpleSoulsAfterGcd < 2 || blueSoulsAfterGcd <= 0))
                yield return HardFailRule.IllegalAction;
            var enshroudIndex = OgcdIndex(frame, "Enshroud");
            if (AvatarActionDuringEnshroud(frame))
                yield return HardFailRule.IllegalAction;
            var sacrificiumIndex = OgcdIndex(frame, "Sacrificium");
            var enshroudBeforeSacrificium = enshroudIndex >= 0 && sacrificiumIndex > enshroudIndex;
            if (sacrificiumIndex >= 0 && ((!frame.Oblatio && !enshroudBeforeSacrificium) || blueSoulsAfterGcd <= 0 && !enshroudBeforeSacrificium))
                yield return HardFailRule.IllegalAction;
            if (frame.SelectedGcd == "PlentifulHarvest" && !frame.PlentifulHarvestReady)
                yield return HardFailRule.IllegalAction;
            if (frame.SelectedGcd == "Perfectio" && !frame.PerfectioParata)
                yield return HardFailRule.IllegalAction;
            if (frame.Level < 90 && frame.SelectedGcd == "Communio")
                yield return HardFailRule.IllegalAction;
            if (frame.Level < 100 && frame.SelectedGcd == "Perfectio")
                yield return HardFailRule.IllegalAction;
            if (frame.Level < 96 && frame.SelectedGcd is "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine")
                yield return HardFailRule.IllegalAction;
            if (frame.Level < 10 && frame.DeathsDesignLeft > 0)
                yield return HardFailRule.IllegalAction;
            if (frame.Level < 72 && frame.ArcaneCircleLeft > 0)
                yield return HardFailRule.IllegalAction;
            if (frame.Level < 60 && frame.SoulSliceCharges > 0)
                yield return HardFailRule.IllegalAction;
            if (frame.SelectedGcd is "SoulSlice" or "SoulScythe" && frame.SoulSliceCharges < 1)
                yield return HardFailRule.IllegalAction;
            if (frame.SelectedOgcds.Contains("Enshroud") && frame.BlueGauge < 50 && !frame.IdealHost && frame.SelectedGcd != "PlentifulHarvest")
                yield return HardFailRule.IllegalAction;
            var redAfterGcd = Math.Min(100, frame.RedGauge + RprRotationEmulator.RedGaugeGainFromGcd(frame.SelectedGcd, frame.ComboLast));
            if (frame.SelectedOgcds.Any(action => action is "Gluttony" or "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe") && redAfterGcd < 50)
                yield return HardFailRule.IllegalAction;
            if (frame.SelectedOgcds.Contains("Potion") && frame.PotionReadyIn > 0.1)
                yield return HardFailRule.IllegalAction;
        }
    }

    internal static bool AvatarActionDuringEnshroud(ActionFrame frame)
    {
        var enshrouded = frame.BlueSouls > 0 && frame.SelectedGcd != "Communio"
            && !(frame.BlueSouls == 1 && frame.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping");
        foreach (var action in frame.SelectedOgcds)
        {
            if (action == "Enshroud")
                enshrouded = true;
            else if (enshrouded && action is "Gluttony" or "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe")
                return true;
        }
        return false;
    }

    private static IEnumerable<HardFailRule> CheckBurst(ScenarioResult result)
    {
        var scenario = result.Scenario;
        var evenAc = result.Frames.FirstOrDefault(f => f.SelectedOgcds.Contains("ArcaneCircle") && f.Time >= 110);
        if (scenario.InitialMode == RotationMode.Full && scenario.Level >= 72 && scenario.KillTime >= 130 && !BasicModeCoversEvenBurst(scenario))
        {
            if (evenAc == null && !scenario.Events.Any(e => e.Type == ScenarioEventType.TargetLost && e.Start <= 120 && e.End >= 120))
                yield return HardFailRule.BurstFailure;
        }

        foreach (var frame in result.Frames)
        {
            if (frame.SelectedOgcds.Contains("Potion") && !frame.SelectedOgcds.Contains("ArcaneCircle") && frame.ArcaneCircleLeft <= 0 && frame.ArcaneCircleReadyIn > scenario.Gcd * 3)
                yield return HardFailRule.BurstFailure;
            if (frame.SelectedOgcds.Contains("Gluttony") && frame.SelectedOgcds.Contains("ArcaneCircle") && OgcdIndex(frame, "Gluttony") < OgcdIndex(frame, "ArcaneCircle"))
                yield return HardFailRule.BurstFailure;
            if (frame.PerfectioParata && frame.Level >= 100 && frame.SelectedGcd == null && frame.TargetAvailable)
                yield return HardFailRule.BurstFailure;
        }

        if (!EvenBurstDisrupted(scenario) && scenario.KillTime >= 150)
        {
            var burstAnchor = evenAc?.Time ?? 120;
            var evenFrames = result.Frames.Where(f => f.Time >= burstAnchor - 45 && f.Time <= burstAnchor + 40).ToList();
            if (scenario.ExpectDoubleEnshroud && scenario.Level >= 80 && evenFrames.Count(f => f.SelectedOgcds.Contains("Enshroud")) < 2)
                yield return HardFailRule.BurstFailure;
            if (scenario.ExpectTwoCommunio && scenario.Level >= 90 && evenFrames.Count(f => f.SelectedGcd == "Communio") < 2)
                yield return HardFailRule.BurstFailure;
            if (scenario.ExpectPerfectio && scenario.Level >= 100 && evenFrames.All(f => f.SelectedGcd != "Perfectio"))
                yield return HardFailRule.BurstFailure;
            if (scenario.ExpectLemure && scenario.Level >= 86 && evenFrames.All(f => !f.SelectedOgcds.Any(a => a is "LemuresSlice" or "LemuresScythe")))
                yield return HardFailRule.BurstFailure;
            if (scenario.ExpectSacrificium && scenario.Level >= 92 && evenFrames.All(f => !f.SelectedOgcds.Contains("Sacrificium")))
                yield return HardFailRule.BurstFailure;
        }
    }

    private static IEnumerable<HardFailRule> CheckDancingMadPhaseEnshroudBudgets(ScenarioResult result)
    {
        if (result.Scenario.Name != "dmu_top_profile_full_timeline")
            yield break;

        if (result.Frames.Any(frame => frame.Time >= 690 && frame.Time < 728
                && (frame.SelectedOgcds.Contains("ArcaneCircle") || frame.SelectedOgcds.Contains("Enshroud")))
            || !result.Frames.Any(frame => frame.Time >= 728 && frame.Time <= 760 && frame.SelectedOgcds.Contains("ArcaneCircle"))
            || !result.Frames.Any(frame => frame.Time >= 728 && frame.Time <= 760 && frame.SelectedOgcds.Contains("Enshroud")))
        {
            yield return HardFailRule.BurstFailure;
            yield break;
        }

        for (var phaseIndex = 0; phaseIndex < 5; ++phaseIndex)
        {
            var enshrouds = result.Frames
                .Where(frame => frame.SelectedOgcds.Contains("Enshroud") && RprRotationEmulator.DancingMadPhaseIndex(frame.Time) == phaseIndex)
                .ToList();
            var burstCount = enshrouds.Count(frame => RprRotationEmulator.DancingMadBurstEnshroudWindowOpen(frame.Time));
            var normalCount = enshrouds.Count - burstCount;
            var budget = RprRotationEmulator.DancingMadEnshroudBudgetForPhase(phaseIndex);
            if (burstCount != budget.Burst || normalCount != budget.Normal)
            {
                yield return HardFailRule.BurstFailure;
                yield break;
            }
        }
    }

    private static IEnumerable<HardFailRule> CheckDancingMadRandomAssignmentPlanner(ScenarioResult result)
    {
        if (!result.Scenario.Name.StartsWith("dmu_random_assignment_", StringComparison.Ordinal))
            yield break;

        var arcaneCircle = result.Frames.FirstOrDefault(frame => frame.SelectedOgcds.Contains("ArcaneCircle") && frame.Time >= 116.5 && frame.Time <= 124.1);
        if (arcaneCircle == null)
        {
            yield return HardFailRule.BurstFailure;
            yield break;
        }

        if (result.Scenario.Name == "dmu_random_assignment_cone_reachable")
        {
            var coneWindow = result.Scenario.Events.First(e => e.Type == ScenarioEventType.MeleeUnavailable);
            var forcedOutReapings = result.Frames
                .Where(frame => frame.Time >= coneWindow.Start && frame.Time < coneWindow.End && frame.BlueSouls > 0)
                .ToList();
            if (forcedOutReapings.Count(frame => frame.SelectedGcd == "GrimReaping") < 4
                || forcedOutReapings.Any(frame => frame.SelectedGcd is "VoidReaping" or "CrossReaping")
                || forcedOutReapings.All(frame => !frame.SelectedOgcds.Contains("LemuresScythe")))
                yield return HardFailRule.BurstFailure;
            yield break;
        }

        var forcedOut = result.Scenario.Events.Single(e => e.Type == ScenarioEventType.MeleeUnavailable);
        var forcedOutFrames = result.Frames.Where(frame => frame.Time >= forcedOut.Start && frame.Time < forcedOut.End).ToList();
        var prematureEnshroud = forcedOutFrames.Any(frame => frame.SelectedOgcds.Contains("Enshroud")
            && forcedOut.End - frame.Time >= result.Scenario.Gcd - 0.35);
        var plentifulHarvest = result.Frames.FirstOrDefault(frame => frame.SelectedGcd == "PlentifulHarvest" && frame.Time >= arcaneCircle.Time);
        var perfectioInArcaneCircle = result.Frames.Any(frame => frame.SelectedGcd == "Perfectio" && frame.Time >= arcaneCircle.Time && frame.ArcaneCircleLeft > 0);
        if (plentifulHarvest == null)
        {
            yield return HardFailRule.BurstFailure;
            yield break;
        }
        if (prematureEnshroud || !perfectioInArcaneCircle)
            yield return HardFailRule.BurstFailure;

        if (result.Scenario.Name == "dmu_random_assignment_line_reachable")
        {
            if (plentifulHarvest.Time < forcedOut.Start || plentifulHarvest.Time >= forcedOut.End || !plentifulHarvest.SelectedOgcds.Contains("Enshroud"))
                yield return HardFailRule.BurstFailure;
        }
        else if (result.Scenario.Name == "dmu_random_assignment_ranged_only")
        {
            if (forcedOutFrames.Any(frame => frame.SelectedGcd == "PlentifulHarvest")
                || forcedOutFrames.All(frame => frame.SelectedGcd is not ("Harpe" or "HarvestMoon"))
                || plentifulHarvest.Time < forcedOut.End)
                yield return HardFailRule.BurstFailure;
        }
    }

    private static IEnumerable<HardFailRule> CheckDeathsDesign(ScenarioResult result)
    {
        foreach (var frame in result.Frames)
        {
            if (frame.TargetAvailable && frame.ArcaneCircleLeft > 0 && frame.DeathsDesignLeft <= 0 && frame.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath") && !DeathsDesignRecoveryException(result, frame))
                yield return HardFailRule.DeathsDesignFailure;
            if (frame.BlueSouls > 0 && frame.DeathsDesignLeft <= 0 && frame.TargetAvailable && frame.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath") && !DeathsDesignRecoveryException(result, frame) && !LateUnbuffedLevel90EnshroudException(frame))
                yield return HardFailRule.DeathsDesignFailure;
            if (frame.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath" && frame.BlueSouls > 0 && frame.Reason != "second DD refresh required during first Enshroud after first Reaping")
                yield return HardFailRule.DeathsDesignFailure;
        }

        if (result.Scenario.EvenBurstOneRefreshEnough)
        {
            foreach (var dd in result.Frames.Where(frame => frame.BlueSouls is > 1 and <= 4 && frame.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath"))
            {
                var ddIndex = result.Frames.IndexOf(dd);
                var firstReaping = result.Frames.Take(ddIndex).LastOrDefault(frame => frame.BlueSouls == 5 && frame.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping");
                if (firstReaping == null)
                    yield return HardFailRule.DeathsDesignFailure;
            }
        }

        if (result.Scenario.EvenBurstTwoRefreshNeeded)
        {
            var firstReaping = result.Frames.FirstOrDefault(f => f.BlueSouls == 5 && f.SelectedGcd is "VoidReaping" or "GrimReaping");
            var dd = result.Frames.FirstOrDefault(f => f.BlueSouls is > 1 and <= 4 && f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath");
            if (firstReaping != null && dd != null && dd.Time < firstReaping.Time)
                yield return HardFailRule.DeathsDesignFailure;
        }
    }

    private static IEnumerable<HardFailRule> CheckGauge(ScenarioResult result)
    {
        var soulSliceTimingScenario = IsSoulSliceTimingScenario(result.Scenario);
        foreach (var frame in result.Frames)
        {
            if (frame.TargetAvailable && frame.FallbackTargetAvailable && (frame.MeleeAvailable || frame.BestConeTargetAvailable) && frame.RedGauge >= 100 && frame.SelectedOgcds.Count == 0 && frame.RotationMode == RotationMode.Full && frame.ReaverState == ReaverState.None && frame.BlueSouls == 0 && frame.Level >= 50 && frame.ArcaneCircleReadyIn > 10)
                yield return HardFailRule.GaugeFailure;
            // The GCD grants soul before its following weave; a later spender cannot undo that overcap.
            if ((frame.SelectedGcd is "SoulSlice" or "SoulScythe") && frame.RedGauge > 50)
                yield return HardFailRule.GaugeFailure;
            if (soulSliceTimingScenario)
            {
                if (result.Scenario.FinalTwoGcdKill && result.Scenario.KillTime - frame.Time <= frame.Gcd * 2.2 && frame.SelectedGcd is ("SoulSlice" or "SoulScythe"))
                    yield return HardFailRule.GaugeFailure;
                if (frame.SelectedGcd is "SoulSlice" or "SoulScythe")
                {
                    if (frame.ArcaneCircleLeft > 0 || frame.BlueSouls > 0 || frame.ReaverState != ReaverState.None || frame.PerfectioParata || frame.PlentifulHarvestReady)
                        yield return HardFailRule.GaugeFailure;
                    if (result.Scenario.Name == "soul_slice_before_arcane_6s" && frame.ArcaneCircleReadyIn > 0 && frame.ArcaneCircleReadyIn <= 6 && frame.SoulSliceCharges < 1.9)
                        yield return HardFailRule.GaugeFailure;
                }
                if (frame.Time <= result.Scenario.Gcd * 2.1 && frame.TargetAvailable && frame.Level >= 60 && frame.SoulSliceCharges >= 1.9 && frame.SelectedGcd is not ("SoulSlice" or "SoulScythe") && !SoulSliceChargeUseBlocked(result.Scenario, frame))
                {
                    if (frame.RedGauge <= 50 || !frame.SelectedOgcds.Any(a => a is "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe"))
                        yield return HardFailRule.GaugeFailure;
                }
            }
            if (frame.TargetAvailable && frame.Level >= 78 && frame.SoulSliceCharges >= 2 && frame.SelectedGcd is not ("SoulSlice" or "SoulScythe" or "ShadowOfDeath" or "WhorlOfDeath") && frame.RotationMode == RotationMode.Full && frame.ReaverState == ReaverState.None && frame.BlueSouls == 0 && !frame.PerfectioParata && !SoulSliceChargeUseBlocked(result.Scenario, frame))
                yield return HardFailRule.GaugeFailure;
            if (frame.TargetAvailable && frame.FallbackTargetAvailable && frame.RotationMode == RotationMode.Basic && frame.RedGauge >= 100 && frame.Level >= 50 && frame.ReaverState == ReaverState.None && frame.BlueSouls == 0 && !frame.PerfectioParata && !frame.PlentifulHarvestReady && !frame.SelectedOgcds.Any(a => a is "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe"))
                yield return HardFailRule.GaugeFailure;
            if (frame.TargetAvailable && frame.RotationMode == RotationMode.Basic && frame.BlueGauge == 100 && frame.RedGauge >= 100 && frame.ReaverState == ReaverState.None && frame.BlueSouls == 0 && !frame.PerfectioParata && !frame.PlentifulHarvestReady && !frame.SelectedOgcds.Any(a => a is "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe"))
                yield return HardFailRule.GaugeFailure;
        }

        if (result.Scenario.ExpectSecondEnshroudBlueGauge && result.Scenario.Level >= 80 && !EvenBurstDisrupted(result.Scenario))
        {
            var preSecondEnshroud = result.Frames.Where(f => f.Time >= 118 && f.Time <= 135 + result.Scenario.Gcd).ToList();
            if (preSecondEnshroud.Count > 0 && preSecondEnshroud.All(f => f.BlueGauge < 50 && !f.SelectedOgcds.Contains("Enshroud") && !f.IdealHost))
                yield return HardFailRule.GaugeFailure;
        }
    }

    private static IEnumerable<HardFailRule> CheckDrift(ScenarioResult result)
    {
        double? previousGluttony = null;
        foreach (var frame in result.Frames)
        {
            if (frame.RotationMode != RotationMode.Full)
            {
                previousGluttony = null;
                continue;
            }

            if (!frame.SelectedOgcds.Contains("Gluttony"))
                continue;

            var executedAt = frame.Time + frame.FirstWeaveOffset + OgcdIndex(frame, "Gluttony") * 0.7;
            if (previousGluttony == null)
            {
                previousGluttony = executedAt;
                continue;
            }

            var readyAt = previousGluttony.Value + 60;
            if (result.Scenario.Category == ScenarioCategory.RealHarness && previousGluttony.Value < 30)
            {
                previousGluttony = executedAt;
                continue;
            }

            if (result.Frames.Any(candidate => candidate.Time >= readyAt
                && candidate.Time < frame.Time
                && candidate.Time - readyAt > GluttonyDriftLimit(candidate.Time)
                && !FinalOrDowntimeException(result.Scenario, candidate.Time)
                && GluttonyCouldHaveBeenUsed(candidate)))
                yield return HardFailRule.DriftFailure;

            previousGluttony = executedAt;
        }
    }

    private static IEnumerable<HardFailRule> CheckEmulatorSpecificationScenarios(ScenarioResult result)
    {
        var first = result.Frames.FirstOrDefault();
        if (first == null)
            yield break;

        if (result.Scenario.Name == "reaver_expiring_enhanced_gibbet" && first.SelectedGcd != "Gibbet")
            yield return HardFailRule.ReaverSequenceFailure;
        if (result.Scenario.Name == "executioner_expiring_enhanced_gallows" && first.SelectedGcd != "ExecutionersGallows")
            yield return HardFailRule.ReaverSequenceFailure;
        if (result.Scenario.Name == "cone_count_independent_from_aoe" && first.SelectedGcd is "Guillotine" or "ExecutionersGuillotine")
            yield return HardFailRule.AoeFailure;
        if (result.Scenario.Name == "grim_swathe_three_targets_enhanced_single_wins" && !first.SelectedOgcds.Contains("UnveiledGibbet"))
            yield return HardFailRule.AoeFailure;
        if (result.Scenario.Name == "combat_soulsow_during_downtime" && result.Frames.All(frame => frame.SelectedGcd != "Soulsow"))
            yield return HardFailRule.GcdStop;
        if (result.Scenario.Name == "target_death_and_return")
        {
            var afterKill = result.Frames.FirstOrDefault(frame => frame.Time >= 30);
            if (afterKill == null || afterKill.DeathsDesignLeft > 0)
                yield return HardFailRule.DeathsDesignFailure;
        }
        if (result.Scenario.Name is "bloodstalk_then_true_north" or "gluttony_then_true_north")
        {
            var creatorIndex = result.Frames.FindIndex(frame => frame.SelectedOgcds.Any(action => action is "BloodStalk" or "Gluttony"));
            if (creatorIndex < 0 || !result.Frames[creatorIndex].SelectedOgcds.Contains("TrueNorth"))
            {
                yield return HardFailRule.ReaverSequenceFailure;
            }
            else
            {
                var positional = result.Frames.Skip(creatorIndex + 1).FirstOrDefault(frame => frame.SelectedGcd is "Gibbet" or "Gallows" or "ExecutionersGibbet" or "ExecutionersGallows");
                if (positional == null || positional.TrueNorthLeft <= 0)
                    yield return HardFailRule.ReaverSequenceFailure;
            }
        }
        if (result.Scenario.Name == "long_fight_potion_recast_and_later_order" && result.Frames.Count(frame => frame.SelectedOgcds.Contains("Potion")) < 2)
            yield return HardFailRule.PotionFailure;
        if (result.Scenario.Name is "red_40_combo_spender_same_weave" or "red_90_combo_cap_spender_same_weave"
            && !first.SelectedOgcds.Any(action => action is "Gluttony" or "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe"))
            yield return HardFailRule.GaugeFailure;
        if (result.Scenario.Name == "red_40_soul_slice_gluttony_same_weave" && !first.SelectedOgcds.Contains("Gluttony"))
            yield return HardFailRule.GaugeFailure;
        if (result.Scenario.Name == "even_burst_shroud_plan_impossible_fallback")
        {
            var firstTargetableIndex = result.Frames.FindIndex(frame => frame.Time >= 125 && frame.TargetAvailable);
            if (firstTargetableIndex < 0 || !result.Frames[firstTargetableIndex].SelectedOgcds.Contains("ArcaneCircle"))
            {
                yield return HardFailRule.BurstFailure;
                yield break;
            }

            var plentifulHarvestIndex = result.Frames.FindIndex(firstTargetableIndex + 1, frame => frame.SelectedGcd == "PlentifulHarvest");
            var setupFrames = plentifulHarvestIndex > firstTargetableIndex
                ? result.Frames.Skip(firstTargetableIndex + 1).Take(plentifulHarvestIndex - firstTargetableIndex - 1).ToList()
                : [];
            if (plentifulHarvestIndex < 0
                || setupFrames.All(frame => frame.SelectedGcd != "SoulSlice")
                || setupFrames.All(frame => !frame.SelectedOgcds.Contains("Gluttony"))
                || setupFrames.Count(frame => frame.SelectedGcd is "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine") < 2)
                yield return HardFailRule.BurstFailure;

            var plentifulHarvestFrame = result.Frames[plentifulHarvestIndex];
            var perfectioFrame = result.Frames.Skip(plentifulHarvestIndex + 1).FirstOrDefault(frame => frame.SelectedGcd == "Perfectio");
            if (!plentifulHarvestFrame.SelectedOgcds.Contains("Enshroud")
                || result.Frames.Skip(plentifulHarvestIndex + 1).All(frame => frame.ArcaneCircleLeft <= 0 || !frame.SelectedOgcds.Any(action => action is "LemuresSlice" or "LemuresScythe"))
                || perfectioFrame == null
                || perfectioFrame.ArcaneCircleLeft <= 0)
                yield return HardFailRule.BurstFailure;
        }
        if (result.Scenario.Name == "dmu_top_profile_opener")
        {
            var expected = new (string Gcd, string[] Ogcds)[]
            {
                ("Harpe", ["ArcaneCircle"]),
                ("ShadowOfDeath", []),
                ("SoulSlice", ["Gluttony"]),
                ("ExecutionersGallows", []),
                ("ExecutionersGibbet", []),
                ("PlentifulHarvest", ["Enshroud", "Sacrificium"]),
                ("VoidReaping", []),
                ("CrossReaping", ["LemuresSlice"]),
                ("VoidReaping", []),
                ("CrossReaping", ["LemuresSlice"]),
                ("Communio", []),
                ("Perfectio", []),
                ("SoulSlice", ["UnveiledGallows"]),
                ("Gallows", []),
                ("ShadowOfDeath", [])
            };
            var openerFrames = result.Frames.Take(expected.Length).ToArray();
            if (openerFrames.Length != expected.Length
                || openerFrames.Where((frame, index) => frame.SelectedGcd != expected[index].Gcd || !frame.SelectedOgcds.SequenceEqual(expected[index].Ogcds)).Any())
                yield return HardFailRule.OpenerFailure;
        }
    }

    private static IEnumerable<HardFailRule> CheckPerfectioBeforeDeathsDesignAtArcaneExpiry(ScenarioResult result)
    {
        if (result.Scenario.Name != "perfectio_before_dd_to_fit_arcane")
            yield break;

        var perfectio = result.Frames.FindIndex(frame => frame.SelectedGcd == "Perfectio");
        var deathsDesign = result.Frames.FindIndex(frame => frame.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath");
        if (perfectio < 0 || deathsDesign < 0 || perfectio >= deathsDesign || result.Frames[perfectio].ArcaneCircleLeft <= 0)
            yield return HardFailRule.BurstFailure;
    }

    private static IEnumerable<HardFailRule> CheckEndingDutyBurstHold(ScenarioResult result)
    {
        if (!result.Scenario.Name.StartsWith("ending_duty_", StringComparison.OrdinalIgnoreCase))
            yield break;

        if (result.Scenario.FinalEncounter)
        {
            if (result.Frames.All(frame => !frame.SelectedOgcds.Contains("ArcaneCircle")))
                yield return HardFailRule.BurstFailure;
            yield break;
        }

        foreach (var frame in result.Frames.Where(frame => frame.Time < 10))
        {
            if (frame.SelectedOgcds.Any(action => action is "Potion" or "ArcaneCircle"))
                yield return HardFailRule.BurstFailure;
            if (frame.ArcaneCircleLeft <= 0 && frame.SelectedOgcds.Contains("Gluttony"))
                yield return HardFailRule.BurstFailure;
            if (frame.ArcaneCircleLeft <= 0 && !frame.IdealHost && frame.SelectedOgcds.Contains("Enshroud"))
                yield return HardFailRule.BurstFailure;
        }

        if (result.Scenario.Name == "ending_duty_ideal_host_cleanup" && result.Frames.All(frame => !frame.SelectedOgcds.Contains("Enshroud")))
            yield return HardFailRule.BurstFailure;
        if (result.Scenario.Name == "ending_duty_active_arcane_gluttony_cleanup" && result.Frames.All(frame => !frame.SelectedOgcds.Contains("Gluttony")))
            yield return HardFailRule.BurstFailure;
    }

    private static IEnumerable<HardFailRule> CheckPatch73EvenBurstSequence(ScenarioResult result)
    {
        if (result.Scenario.Name != "patch73_even_burst_two_ws_before_ac")
            yield break;

        var enshroudIndex = result.Frames.FindIndex(frame => frame.SelectedOgcds.Contains("Enshroud"));
        var arcaneCircleIndex = result.Frames.FindIndex(frame => frame.SelectedOgcds.Contains("ArcaneCircle"));
        if (enshroudIndex < 0 || arcaneCircleIndex <= enshroudIndex)
        {
            yield return HardFailRule.BurstFailure;
            yield break;
        }

        var preArcaneGcds = result.Frames
            .Skip(enshroudIndex + 1)
            .Take(arcaneCircleIndex - enshroudIndex)
            .Select(frame => frame.SelectedGcd)
            .ToArray();
        if (preArcaneGcds.Length != 2
            || preArcaneGcds[0] is not ("VoidReaping" or "CrossReaping" or "GrimReaping")
            || preArcaneGcds[1] is not ("ShadowOfDeath" or "WhorlOfDeath"))
            yield return HardFailRule.BurstFailure;

        var arcaneCircleFrame = result.Frames[arcaneCircleIndex];
        if (arcaneCircleFrame.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath"))
            yield return HardFailRule.BurstFailure;

        var buffedCommunios = result.Frames.Count(frame => frame.Time > arcaneCircleFrame.Time && frame.SelectedGcd == "Communio" && frame.ArcaneCircleLeft > 0);
        if (buffedCommunios < 2)
            yield return HardFailRule.BurstFailure;

        if (!result.Frames.Any(frame => frame.Time > arcaneCircleFrame.Time && frame.SelectedGcd == "Perfectio" && frame.ArcaneCircleLeft > 0))
            yield return HardFailRule.BurstFailure;
    }

    private static IEnumerable<HardFailRule> CheckPlentifulHarvestBeforeSecondEvenBurstEnshroud(ScenarioResult result)
    {
        if (result.Scenario.Name != "even_burst_blue100_ph_before_second_enshroud")
            yield break;

        var plentifulHarvestIndex = result.Frames.FindIndex(frame => frame.SelectedGcd == "PlentifulHarvest");
        var enshroudIndex = result.Frames.FindIndex(frame => frame.SelectedOgcds.Contains("Enshroud"));
        var communioIndex = result.Frames.FindIndex(frame => frame.SelectedGcd == "Communio");
        var perfectioIndex = result.Frames.FindIndex(frame => frame.SelectedGcd == "Perfectio");
        if (plentifulHarvestIndex != 0
            || enshroudIndex != plentifulHarvestIndex
            || communioIndex <= enshroudIndex
            || perfectioIndex <= communioIndex
            || result.Frames[perfectioIndex].ArcaneCircleLeft <= 0)
            yield return HardFailRule.BurstFailure;
    }

    private static IEnumerable<HardFailRule> CheckPostPerfectioPrioritySequence(ScenarioResult result)
    {
        if (!result.Scenario.Name.StartsWith("post_perfectio_", StringComparison.Ordinal))
            yield break;

        var perfectioIndex = result.Frames.FindIndex(frame => frame.SelectedGcd == "Perfectio");
        if (perfectioIndex < 0)
        {
            yield return HardFailRule.BurstFailure;
            yield break;
        }

        var afterPerfectio = result.Frames.Skip(perfectioIndex + 1).ToList();
        if (result.Scenario.Name is "post_perfectio_combo_priority_slice" or "post_perfectio_combo_priority_dying")
        {
            if (afterPerfectio.Count < 2 || afterPerfectio[0].SelectedGcd != "WaxingSlice" || afterPerfectio[1].SelectedGcd != "InfernalSlice")
                yield return HardFailRule.BurstFailure;
            yield break;
        }

        if (result.Scenario.Name == "post_perfectio_combo_priority_waxing")
        {
            if (afterPerfectio.Count == 0 || afterPerfectio[0].SelectedGcd != "InfernalSlice")
                yield return HardFailRule.BurstFailure;
            yield break;
        }

        var gluttonyIndex = afterPerfectio.FindIndex(frame => frame.SelectedOgcds.Contains("Gluttony"));
        if (gluttonyIndex < 0)
        {
            yield return HardFailRule.BurstFailure;
            yield break;
        }

        if (result.Scenario.Name == "post_perfectio_gluttony_ready" && gluttonyIndex != 0)
            yield return HardFailRule.BurstFailure;

        if (result.Scenario.Name == "post_perfectio_soul_slice_gluttony"
            && (gluttonyIndex != 0 || afterPerfectio[0].SelectedGcd is not ("SoulSlice" or "SoulScythe")))
            yield return HardFailRule.BurstFailure;

        var executioners = afterPerfectio
            .Skip(gluttonyIndex + 1)
            .Where(frame => frame.SelectedGcd is "ExecutionersGibbet" or "ExecutionersGallows")
            .Take(2)
            .Select(frame => frame.SelectedGcd)
            .ToArray();
        if (executioners.Length < 2 || executioners[0] != "ExecutionersGibbet" || executioners[1] != "ExecutionersGallows")
            yield return HardFailRule.ReaverSequenceFailure;
    }

    private static double GluttonyDriftLimit(double time)
    {
        var cycle = time % 120;
        return time >= 100 && (cycle >= 100 || cycle <= 20) ? 20.5 : 3.5;
    }

    private static bool GluttonyCouldHaveBeenUsed(ActionFrame frame)
        => frame.RotationMode == RotationMode.Full
        && frame.TargetAvailable
        && frame.BestRangedAoeTargetAvailable
        && frame.Level >= 76
        && Math.Min(100, frame.RedGauge + RprRotationEmulator.RedGaugeGainFromGcd(frame.SelectedGcd, frame.ComboLast)) >= 50
        && frame.ReaverState == ReaverState.None
        && frame.BlueSouls == 0
        && !frame.PerfectioParata
        && !frame.PlentifulHarvestReady
        && frame.EnshroudReadyReason.Length == 0
        && frame.SelectedGcd is not ("PlentifulHarvest" or "Perfectio")
        && !frame.SelectedOgcds.Contains("ArcaneCircle")
        && !frame.SelectedOgcds.Contains("Enshroud")
        && frame.DeathsDesignLeft > frame.Gcd * 3;

    private static bool SoulSliceChargeBlockedByHigherPriority(ActionFrame frame)
        => frame.SelectedGcd is "PlentifulHarvest" or "Communio" or "VoidReaping" or "CrossReaping" or "GrimReaping" or "Perfectio" or "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine"
        || frame.Reason == "post-Perfectio combo recovery"
        || frame.DeathsDesignRefreshReason is "emergency" or "pre_any_enshroud" or "two_refresh_pre_burst"
        || frame.RedGauge > 50 && frame.SelectedOgcds.Any(a => a is "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe" or "Gluttony")
        || frame.SelectedOgcds.Contains("Gluttony") && (frame.ArcaneCircleLeft > 0 || frame.PostPerfectioPriorityActive || frame.TimeSincePerfectioUsed is > 0 && frame.TimeSincePerfectioUsed <= frame.Gcd + 0.1);

    private static bool SoulSliceChargeUseBlocked(ScenarioDefinition scenario, ActionFrame frame)
        => scenario.FinalTwoGcdKill
        || !frame.MeleeAvailable
        || !frame.FallbackTargetAvailable
        || scenario.KillTime - frame.Time <= frame.Gcd * 2.2
        || frame.RedGauge > 50
        || frame.ArcaneCircleLeft > 0
        || frame.BlueSouls > 0
        || frame.ReaverState != ReaverState.None
        || frame.PerfectioParata
        || frame.PlentifulHarvestReady
        || SoulSliceChargeBlockedByHigherPriority(frame);

    private static bool IsSoulSliceTimingScenario(ScenarioDefinition scenario)
        => scenario.Name is "soul_slice_red_50_normal"
        or "soul_slice_red_60_overcap_soon"
        or "soul_slice_red_90_overcap_soon"
        or "soul_slice_red_100_overcap_soon"
        or "soul_slice_combo_protected_red_60"
        or "soul_slice_combo_protected_red_100"
        or "soul_slice_before_arcane_6s"
        or "soul_slice_before_arcane_20s"
        or "soul_slice_basic_mode_red_100"
        or "soul_scythe_aoe_3_targets"
        or "soul_scythe_aoe_to_single_switch"
        or "soul_slice_target_dies_2gcd";

    private static IEnumerable<HardFailRule> CheckAoe(ScenarioResult result)
    {
        var soulSliceTimingScenario = IsSoulSliceTimingScenario(result.Scenario);
        if (result.Scenario.Name == "full_mode_aoe_red_100_blue_100_enshroud_held"
            && !result.Frames.First().SelectedOgcds.Contains("GrimSwathe"))
            yield return HardFailRule.GaugeFailure;

        foreach (var frame in result.Frames)
        {
            if (frame.ReaverState != ReaverState.None && frame.ConeTargets > 3 && frame.BestConeTargetAvailable && frame.GcdCandidates.Any(a => a is "Gibbet" or "Gallows" or "ExecutionersGibbet" or "ExecutionersGallows"))
                yield return HardFailRule.AoeFailure;
            if (frame.ReaverState != ReaverState.None && frame.MeleeAvailable && frame.ConeTargets <= 3 && frame.GcdCandidates.Any(a => a is "Guillotine" or "ExecutionersGuillotine"))
                yield return HardFailRule.AoeFailure;
            if (soulSliceTimingScenario && frame.AoeTargets > 2 && frame.Level >= 60 && frame.SelectedGcd == "SoulSlice")
                yield return HardFailRule.AoeFailure;
            if (frame.GcdCandidates.Contains("SoulScythe") && frame.GcdCandidates.Contains("SoulSlice"))
                yield return HardFailRule.AoeFailure;
            if (frame.Level >= 90 && frame.BlueSouls == 1 && frame.BestRangedAoeTargetAvailable && frame.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping")
                yield return HardFailRule.AoeFailure;
            if (frame.SelectedGcd == "GrimReaping" && !frame.BestConeTargetAvailable)
                yield return HardFailRule.AoeFailure;
            if (frame.SelectedOgcds.Contains("LemuresScythe") && !frame.BestConeTargetAvailable)
                yield return HardFailRule.AoeFailure;
            if (frame.SelectedOgcds.Contains("GrimSwathe") && frame.SelectedOgcds.Contains("BloodStalk"))
                yield return HardFailRule.AoeFailure;
            if (frame.SelectedOgcds.Contains("LemuresScythe") && frame.SelectedOgcds.Contains("LemuresSlice"))
                yield return HardFailRule.AoeFailure;
            if (frame.GcdCandidates.Contains("NightmareScythe") && frame.GcdCandidates.Contains("SpinningScythe"))
                yield return HardFailRule.AoeFailure;
        }
    }

    private static IEnumerable<HardFailRule> CheckModeSwitch(ScenarioResult result)
    {
        foreach (var frame in result.Frames)
        {
            if (frame.RotationMode == RotationMode.Basic && frame.SelectedOgcds.Contains("Gluttony"))
                yield return HardFailRule.ModeSwitchFailure;
            if (frame.RotationMode == RotationMode.Basic && frame.ReaverState != ReaverState.None && frame.SelectedGcd == null && frame.TargetAvailable)
                yield return HardFailRule.ModeSwitchFailure;
            if (frame.RotationMode == RotationMode.Basic && frame.PerfectioParata && frame.SelectedGcd == null && frame.TargetAvailable)
                yield return HardFailRule.ModeSwitchFailure;
        }
    }

    private static IEnumerable<HardFailRule> CheckWeaveAndPotion(ScenarioResult result)
    {
        foreach (var frame in result.Frames)
        {
            var arcaneCircle = OgcdIndex(frame, "ArcaneCircle");
            var potion = OgcdIndex(frame, "Potion");
            var gluttony = OgcdIndex(frame, "Gluttony");
            var sacrificium = OgcdIndex(frame, "Sacrificium");

            if (arcaneCircle >= 0 && gluttony >= 0 && gluttony < arcaneCircle)
                yield return HardFailRule.WeaveOrderFailure;
            if (arcaneCircle >= 0 && sacrificium >= 0 && sacrificium < arcaneCircle)
                yield return HardFailRule.WeaveOrderFailure;
            if (arcaneCircle >= 0 && potion >= 0)
            {
                var laterPotionBurst = frame.Time >= 240;
                if (laterPotionBurst && potion < arcaneCircle || !laterPotionBurst && potion > arcaneCircle)
                    yield return HardFailRule.WeaveOrderFailure;
            }

            if (potion < 0)
                continue;
            if (result.Scenario.Potion == PotionMode.Off)
                yield return HardFailRule.PotionFailure;
            if (result.Scenario.Potion == PotionMode.EvenBurstExceptOpener && frame.Time < 30)
                yield return HardFailRule.PotionFailure;
            if (arcaneCircle < 0 && frame.ArcaneCircleLeft <= 0 && frame.ArcaneCircleReadyIn > result.Scenario.Gcd * 3)
                yield return HardFailRule.PotionFailure;
        }
    }

    private static IEnumerable<HardFailRule> CheckOpenerOrder(ScenarioResult result)
    {
        if (result.Scenario.Category != ScenarioCategory.WeaveValidation || !result.Scenario.Name.Contains("opener", StringComparison.OrdinalIgnoreCase))
            yield break;

        var shadow = FirstActionKey(result, "ShadowOfDeath");
        var potion = FirstActionKey(result, "Potion");
        var soulSlice = FirstActionKey(result, "SoulSlice");
        var arcaneCircle = FirstActionKey(result, "ArcaneCircle");
        var gluttony = FirstActionKey(result, "Gluttony");
        var openerPotionExpected = result.Scenario.Potion == PotionMode.OpenerAndEvenBurst;

        if (arcaneCircle == null)
        {
            yield return HardFailRule.OpenerFailure;
            yield break;
        }

        if (openerPotionExpected && potion == null)
            yield return HardFailRule.OpenerFailure;

        switch (result.Scenario.Opener)
        {
            case OpenerBurstMode.TwoGcd:
                if (soulSlice != null && arcaneCircle < soulSlice && !SameFrameGcdThenOgcd(result, "SoulSlice", "ArcaneCircle") && !SameFrameGcdThenOgcd(result, "SoulScythe", "ArcaneCircle"))
                    yield return HardFailRule.OpenerFailure;
                if (openerPotionExpected && potion != null && shadow != null && potion < shadow && !SameFrameGcdThenOgcd(result, "ShadowOfDeath", "Potion") && !SameFrameGcdThenOgcd(result, "WhorlOfDeath", "Potion"))
                    yield return HardFailRule.OpenerFailure;
                if (openerPotionExpected && potion != null && soulSlice != null && potion > soulSlice)
                    yield return HardFailRule.OpenerFailure;
                if (gluttony != null && gluttony < arcaneCircle)
                    yield return HardFailRule.OpenerFailure;
                if (result.Scenario.Name == "opener_2gcd_weave_order")
                {
                    var shadowFrame = result.Frames.FirstOrDefault(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath");
                    var soulSliceFrame = result.Frames.FirstOrDefault(f => f.SelectedGcd is "SoulSlice" or "SoulScythe");
                    var executioners = result.Frames.Where(f => f.SelectedGcd is "ExecutionersGibbet" or "ExecutionersGallows").Take(2).Select(f => f.SelectedGcd).ToArray();
                    if (openerPotionExpected && (shadowFrame == null || !shadowFrame.SelectedOgcds.Contains("Potion")))
                        yield return HardFailRule.OpenerFailure;
                    if (soulSliceFrame == null
                        || !soulSliceFrame.SelectedOgcds.Contains("ArcaneCircle")
                        || !soulSliceFrame.SelectedOgcds.Contains("Gluttony")
                        || OgcdIndex(soulSliceFrame, "ArcaneCircle") > OgcdIndex(soulSliceFrame, "Gluttony"))
                        yield return HardFailRule.OpenerFailure;
                    if (executioners.Length < 2 || executioners[0] != "ExecutionersGibbet" || executioners[1] != "ExecutionersGallows")
                        yield return HardFailRule.OpenerFailure;
                }
                break;
            case OpenerBurstMode.TwoPointFiveSecond:
                if (soulSlice != null && arcaneCircle > soulSlice)
                    yield return HardFailRule.OpenerFailure;
                if (gluttony != null && gluttony < arcaneCircle)
                    yield return HardFailRule.OpenerFailure;
                if (openerPotionExpected && potion != null && potion > arcaneCircle)
                    yield return HardFailRule.OpenerFailure;
                break;
            case OpenerBurstMode.ZeroSecond:
                if (arcaneCircle > result.Scenario.Gcd * 1.5)
                    yield return HardFailRule.OpenerFailure;
                if (result.Frames.Any(f => f.SelectedOgcds.Contains("Gluttony")
                    && f.Time < 30
                    && Math.Min(100, f.RedGauge + RprRotationEmulator.RedGaugeGainFromGcd(f.SelectedGcd, f.ComboLast)) < 50))
                    yield return HardFailRule.OpenerFailure;
                if (result.Scenario.Name == "opener_0s_weave_order")
                {
                    var soulSliceFrame = result.Frames.FirstOrDefault(f => f.SelectedGcd is "SoulSlice" or "SoulScythe");
                    var shadowFrame = result.Frames.FirstOrDefault(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath");
                    if (soulSliceFrame == null
                        || shadowFrame == null
                        || soulSliceFrame.Time >= shadowFrame.Time
                        || !soulSliceFrame.SelectedOgcds.Contains("ArcaneCircle")
                        || !shadowFrame.SelectedOgcds.Contains("Gluttony"))
                        yield return HardFailRule.OpenerFailure;
                }
                break;
        }

        var potionFrames = result.Frames.Where(frame => frame.SelectedOgcds.Contains("Potion")).ToList();
        for (var i = 1; i < potionFrames.Count; ++i)
            if (potionFrames[i].Time - potionFrames[i - 1].Time < 269.9)
                yield return HardFailRule.PotionFailure;
    }

    private static IEnumerable<HardFailRule> CheckFastGcdLateSoulSliceBeforePlentifulHarvest(ScenarioResult result)
    {
        if (!IsFastGcdLateSoulSliceBeforePhScenario(result.Scenario))
            yield break;

        var first = result.Frames.FirstOrDefault(f => f.Time <= 0.01);
        if (first == null)
            yield break;

        if (result.Scenario.Name == "fast_gcd_246_late_soulslice_before_ph" && first.SelectedGcd != "SoulSlice")
            yield return HardFailRule.GaugeFailure;

        if (result.Scenario.Name == "fast_gcd_246_late_soulscythe_before_ph_aoe" && first.SelectedGcd != "SoulScythe")
            yield return HardFailRule.GaugeFailure;

        if (result.Scenario.Name is "gcd_247_no_late_soulslice_before_ph" or "gcd_248_no_late_soulslice_before_ph" or "gcd_249_no_late_soulslice_before_ph"
            && first.SelectedGcd is "SoulSlice" or "SoulScythe")
            yield return HardFailRule.GaugeFailure;

        if (result.Scenario.Name is "fast_gcd_246_red_60_no_late_soulslice" or "fast_gcd_246_dd_low_no_late_soulslice"
            && first.SelectedGcd is "SoulSlice" or "SoulScythe")
            yield return HardFailRule.GaugeFailure;

        if (first.SelectedGcd is "SoulSlice" or "SoulScythe" && first.RedGauge > 50)
            yield return HardFailRule.GaugeFailure;

        var plentifulHarvest = result.Frames.FirstOrDefault(f => f.SelectedGcd == "PlentifulHarvest");
        if (plentifulHarvest == null || plentifulHarvest.Time > result.Scenario.Gcd * 3.1)
            yield return HardFailRule.BurstFailure;

        if (result.Frames.Any(f => f.TargetAvailable && f.DeathsDesignLeft <= 0 && f.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath") && f.Time <= 35))
            yield return HardFailRule.DeathsDesignFailure;
    }

    private static bool IsFastGcdLateSoulSliceBeforePhScenario(ScenarioDefinition scenario)
        => scenario.Name is "fast_gcd_246_late_soulslice_before_ph"
        or "fast_gcd_246_late_soulscythe_before_ph_aoe"
        or "gcd_247_no_late_soulslice_before_ph"
        or "gcd_248_no_late_soulslice_before_ph"
        or "gcd_249_no_late_soulslice_before_ph"
        or "fast_gcd_246_red_60_no_late_soulslice"
        or "fast_gcd_246_dd_low_no_late_soulslice"
        or "fast_gcd_246_double_enshroud_integrity";

    private static bool IsHostileSingle(string? action)
        => action is "Slice" or "WaxingSlice" or "InfernalSlice" or "ShadowOfDeath" or "SoulSlice" or "Gibbet" or "Gallows" or "ExecutionersGibbet" or "ExecutionersGallows" or "Harpe" or "VoidReaping" or "CrossReaping" or "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "LemuresSlice";

    private static bool IsMeleeThreeYardAction(string? action)
        => action is "Slice" or "WaxingSlice" or "InfernalSlice" or "ShadowOfDeath" or "SoulSlice" or "Gibbet" or "Gallows" or "ExecutionersGibbet" or "ExecutionersGallows" or "VoidReaping" or "CrossReaping" or "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "LemuresSlice";

    private static int OgcdIndex(ActionFrame frame, string action)
    {
        for (var i = 0; i < frame.SelectedOgcds.Count; ++i)
            if (frame.SelectedOgcds[i] == action)
                return i;
        return -1;
    }

    private static IEnumerable<HardFailRule> CheckEnhancedReaverSequence(ScenarioResult result)
    {
        foreach (var frame in result.Frames)
        {
            if (!frame.TargetAvailable
                || !frame.FallbackTargetAvailable
                || !frame.MeleeAvailable
                || frame.ReaverState == ReaverState.None
                || frame.ConeTargets > 3 && frame.BestConeTargetAvailable)
                continue;

            var executioner = frame.ReaverState == ReaverState.Executioner && frame.Level >= 96;
            var expected = frame.EnhancedGallowsLeft > 0
                ? executioner ? "ExecutionersGallows" : "Gallows"
                : frame.EnhancedGibbetLeft > 0
                    ? executioner ? "ExecutionersGibbet" : "Gibbet"
                    : null;

            if (expected != null && frame.SelectedGcd != expected)
                yield return HardFailRule.ReaverSequenceFailure;
        }
    }

    private static bool SameFrameGcdThenOgcd(ScenarioResult result, string gcd, string ogcd)
        => result.Frames.Any(f => f.SelectedGcd == gcd && f.SelectedOgcds.Contains(ogcd));

    private static double? FirstActionKey(ScenarioResult result, string action)
    {
        foreach (var frame in result.Frames)
        {
            if (frame.SelectedGcd == action)
                return frame.Time;
            var index = OgcdIndex(frame, action);
            if (index >= 0)
                return frame.Time + frame.FirstWeaveOffset + index * 0.7;
        }

        return null;
    }

    private static bool HasRangedOption(ActionFrame frame)
        => frame.Level >= 50;

    private static bool BasicModeCoversEvenBurst(ScenarioDefinition scenario)
        => scenario.InitialMode == RotationMode.Basic
        || scenario.Events.Any(e => e.Type == ScenarioEventType.ModeSwitch && e.Mode == RotationMode.Basic && e.Start <= 120 && e.End >= 120);

    private static bool FinalOrDowntimeException(ScenarioDefinition scenario, double time)
        => scenario.FinalTwoGcdKill
        || scenario.KillTime - time <= scenario.Gcd * 2.2
        || scenario.Events.Any(e => e.Type == ScenarioEventType.MeleeUnavailable && e.Start <= time && e.End > time)
        || scenario.Events.Any(e => e.Type == ScenarioEventType.TargetLost
            && (e.Start <= time + scenario.Gcd * 2.2 && e.End > time
                || e.End <= time && time - e.End <= 10));

    private static bool DeathsDesignRecoveryException(ScenarioResult result, ActionFrame frame)
    {
        var scenario = result.Scenario;
        if (FinalOrDowntimeException(scenario, frame.Time))
            return true;

        var burstOccupied = frame.ReaverState != ReaverState.None || frame.BlueSouls > 0;
        var initialRecovery = scenario.Category == ScenarioCategory.RealHarness
            && burstOccupied
            && scenario.DeathsDesignLeft <= scenario.Gcd * 4
            && frame.Time <= scenario.Gcd * 5
            && !result.Frames.Any(candidate => candidate.Time < frame.Time && candidate.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath");
        if (initialRecovery)
            return true;

        if (scenario.Category == ScenarioCategory.RealHarness
            && frame.ReaverState != ReaverState.None
            && result.Frames.Any(candidate => candidate.Time < frame.Time
                && frame.Time - candidate.Time <= scenario.Gcd * 3
                && candidate.SelectedOgcds.Contains("Gluttony")
                && candidate.DeathsDesignLeft <= scenario.Gcd * 2))
        {
            // Production RPR requires two GCDs of DD before Gluttony; the lightweight emulator does not model that guard.
            return true;
        }

        return burstOccupied && scenario.Events.Any(e => e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable
            && e.End <= frame.Time
            && frame.Time - e.End <= scenario.Gcd * 6);
    }

    private static bool LateUnbuffedLevel90EnshroudException(ActionFrame frame)
        => frame.ArcaneCircleLeft <= 0
        && (frame.Level < 90 && frame.BlueSouls == 1
            || frame.Level is >= 90 and < 92 && frame.Time >= 30 && frame.BlueSouls is > 0 and <= 2);

    private static bool EvenBurstDisrupted(ScenarioDefinition scenario)
        => scenario.Events.Any(e =>
            (e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable or ScenarioEventType.EmptyPriorityTargets or ScenarioEventType.NullBestRangedAoeTarget)
            && e.Start < 160
            && e.End > 100);
}

public static class MetricsAnalyzer
{
    private const double ArcaneCircleDamageMultiplier = 1.03;

    public static ScenarioMetrics Build(IReadOnlyList<ActionFrame> frames, ScenarioDefinition scenario)
    {
        var gcdCount = frames.Count(f => f.SelectedGcd != null);
        var targetFrames = frames.Count(f => f.TargetAvailable);
        var targetTime = frames.Where(f => f.TargetAvailable).Sum(f => f.Elapsed);
        var deathsDesignTime = frames.Where(f => f.TargetAvailable).Sum(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath" ? f.Elapsed : Math.Min(f.Elapsed, f.DeathsDesignLeft));
        var skipped = frames.Count(f => f.ExpectedActionPossible && f.SelectedGcd == null);
        var potency = frames.Sum(EstimatedFramePotency);

        return new ScenarioMetrics
        {
            TotalPotencyEstimate = potency,
            GcdUptime = targetFrames == 0 ? 1 : (double)(targetFrames - skipped) / targetFrames,
            GcdCount = gcdCount,
            ArcaneCircleCount = frames.Count(f => f.SelectedOgcds.Contains("ArcaneCircle")),
            GluttonyCount = frames.Count(f => f.SelectedOgcds.Contains("Gluttony")),
            GluttonyDriftSeconds = EstimateGluttonyDrift(frames),
            EnshroudCount = frames.Count(f => f.SelectedOgcds.Contains("Enshroud")),
            CommunioCount = frames.Count(f => f.SelectedGcd == "Communio"),
            PerfectioCount = frames.Count(f => f.SelectedGcd == "Perfectio"),
            LemureCount = frames.Count(f => f.SelectedOgcds.Any(a => a is "LemuresSlice" or "LemuresScythe")),
            SacrificiumCount = frames.Count(f => f.SelectedOgcds.Contains("Sacrificium")),
            SoulSliceChargesLost = frames.Sum(f => SoulSliceChargeLoss(f)),
            RedGaugeOvercap = frames.Count(f => f.Level >= 50 && f.RedGauge + RprRotationEmulator.RedGaugeGainFromGcd(f.SelectedGcd, f.ComboLast) > 100),
            BlueGaugeOvercap = frames.Count(f => f.Level >= 80 && f.BlueGauge > 90 && f.SelectedGcd is "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine"),
            DeathsDesignUptime = targetTime <= 0 ? 1 : deathsDesignTime / targetTime,
            DeathsDesignRefreshCount = frames.Count(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath"),
            WastedDeathsDesignRefreshCount = frames.Count(f => f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath" && f.DeathsDesignLeft > 45),
            RangedGcdCount = frames.Count(f => !f.MeleeAvailable && f.SelectedGcd is "Harpe" or "HarvestMoon" or "Perfectio" or "Communio" or "PlentifulHarvest"),
            InvalidTargetFallbackCount = frames.Count(f => !f.HaveTarget && f.FallbackTargetAvailable && f.SelectedGcd != null),
            SkippedGcdCount = skipped,
            ClippingRiskCount = frames.Count(f => f.ClipRisk),
            ComboBreakCount = CountComboBreaks(frames)
        };
    }

    public static double Score(IReadOnlyList<ActionFrame> frames, ScenarioDefinition scenario)
    {
        var m = Build(frames, scenario);
        return m.TotalPotencyEstimate
            + m.GcdUptime * 10000
            + m.DeathsDesignUptime * 2000
            - m.RedGaugeOvercap * 150
            - m.BlueGaugeOvercap * 150
            - m.SkippedGcdCount * 500
            - m.ClippingRiskCount * 250;
    }

    private static double EstimateGluttonyDrift(IReadOnlyList<ActionFrame> frames)
    {
        double? previousGluttony = null;
        var drift = 0.0;
        foreach (var frame in frames)
        {
            if (frame.RotationMode != RotationMode.Full)
            {
                previousGluttony = null;
                continue;
            }

            if (!frame.SelectedOgcds.Contains("Gluttony"))
                continue;

            var executedAt = frame.Time + frame.FirstWeaveOffset + frame.SelectedOgcds.ToList().IndexOf("Gluttony") * 0.7;
            if (previousGluttony != null)
                drift += Math.Max(0, executedAt - previousGluttony.Value - 60);

            previousGluttony = executedAt;
        }

        return drift;
    }

    private static double SoulSliceChargeLoss(ActionFrame frame)
    {
        if (frame.Level < 60 || frame.SelectedGcd is "SoulSlice" or "SoulScythe")
            return 0;
        var maxCharges = frame.Level >= 78 ? 2 : 1;
        var secondsUntilCap = Math.Max(0, maxCharges - frame.SoulSliceCharges) * 30;
        return Math.Max(0, frame.Elapsed - secondsUntilCap) / 30;
    }

    private static double EstimatedFramePotency(ActionFrame frame)
    {
        var arcaneCircleActive = frame.ArcaneCircleLeft > 0;
        var deathsDesignActive = frame.DeathsDesignLeft > 0;
        var potency = Potency(frame.SelectedGcd, frame)
            * (arcaneCircleActive ? ArcaneCircleDamageMultiplier : 1.0)
            * (deathsDesignActive ? 1.1 : 1.0);
        if (frame.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath")
            deathsDesignActive = true;
        foreach (var action in frame.SelectedOgcds)
        {
            if (action == "ArcaneCircle")
                arcaneCircleActive = true;
            else
                potency += Potency(action, frame)
                    * (arcaneCircleActive ? ArcaneCircleDamageMultiplier : 1.0)
                    * (deathsDesignActive ? 1.1 : 1.0);
        }

        return potency;
    }

    private static int CountComboBreaks(IReadOnlyList<ActionFrame> frames)
    {
        string? expected = null;
        var expiresAt = 0.0;
        var breaks = 0;
        foreach (var frame in frames)
        {
            if (expected != null && frame.Time > expiresAt)
            {
                ++breaks;
                expected = null;
            }

            if (frame.SelectedGcd == expected)
            {
                expected = expected == "WaxingSlice" ? "InfernalSlice" : null;
                expiresAt = frame.Time + 30;
                continue;
            }

            if (frame.SelectedGcd is "Slice" or "SpinningScythe")
            {
                if (expected != null)
                    ++breaks;
                expected = frame.SelectedGcd == "Slice" ? "WaxingSlice" : null;
                expiresAt = frame.Time + 30;
            }
        }

        return breaks;
    }

    private static double Potency(string? action, ActionFrame frame)
    {
        var primary = action switch
        {
            "Perfectio" => 1300,
            "Communio" => 1100,
            "Gluttony" => 560,
            "ExecutionersGibbet" => 700 + (frame.EnhancedGibbetLeft > 0 ? 60 : 0) + (PositionalApplied(frame) ? 60 : 0),
            "ExecutionersGallows" => 700 + (frame.EnhancedGallowsLeft > 0 ? 60 : 0) + (PositionalApplied(frame) ? 60 : 0),
            "ExecutionersGuillotine" => 260,
            "Gibbet" => 500 + (frame.EnhancedGibbetLeft > 0 ? 60 : 0) + (PositionalApplied(frame) ? 60 : 0),
            "Gallows" => 500 + (frame.EnhancedGallowsLeft > 0 ? 60 : 0) + (PositionalApplied(frame) ? 60 : 0),
            "Guillotine" => 200,
            "VoidReaping" => frame.EnhancedVoidReapingLeft > 0 ? 640 : 580,
            "CrossReaping" => frame.EnhancedCrossReapingLeft > 0 ? 640 : 580,
            "GrimReaping" => 220,
            "HarvestMoon" => frame.Level >= 94 ? 800 : 600,
            "PlentifulHarvest" => 680 + 40 * Math.Clamp(frame.ImmortalSacrifice, 1, 8),
            "SoulSlice" => frame.Level >= 94 ? 520 : 460,
            "SoulScythe" => 180,
            "InfernalSlice" => frame.Level >= 94 ? 600 : frame.Level >= 84 ? 500 : frame.Level >= 60 ? 460 : 400,
            "WaxingSlice" => frame.Level >= 94 ? 500 : frame.Level >= 84 ? 400 : frame.Level >= 60 ? 380 : 300,
            "Slice" => frame.Level >= 94 ? 420 : frame.Level >= 84 ? 320 : frame.Level >= 60 ? 300 : 240,
            "ShadowOfDeath" => 300,
            "WhorlOfDeath" => 100,
            "LemuresSlice" => frame.Level >= 94 ? 280 : 240,
            "LemuresScythe" => 100,
            "Sacrificium" => 700,
            "BloodStalk" => 340,
            "UnveiledGibbet" or "UnveiledGallows" => frame.Level >= 94 ? 440 : 400,
            "GrimSwathe" => 140,
            "Harpe" => 300,
            "SpinningScythe" => frame.Level >= 60 ? 140 : 100,
            "NightmareScythe" => frame.Level >= 60 ? 180 : 140,
            _ => 0
        };

        return action switch
        {
            "WhorlOfDeath" or "SoulScythe" or "SpinningScythe" or "NightmareScythe" => AllTargetPotency(primary, frame.AoeTargets),
            "Guillotine" or "ExecutionersGuillotine" or "GrimReaping" or "LemuresScythe" or "GrimSwathe" => AllTargetPotency(primary, frame.ConeTargets),
            "Gluttony" => FalloffPotency(primary, frame.AoeTargets, 0.75),
            "HarvestMoon" => FalloffPotency(primary, frame.AoeTargets, 0.60),
            "PlentifulHarvest" or "Communio" or "Sacrificium" or "Perfectio" => FalloffPotency(primary, frame.AoeTargets, 0.80),
            _ => primary
        };
    }

    private static double AllTargetPotency(double primary, int targets)
        => primary * Math.Max(1, targets);

    private static double FalloffPotency(double primary, int targets, double additionalTargetMultiplier)
        => primary * (1 + Math.Max(0, targets - 1) * additionalTargetMultiplier);

    private static bool PositionalApplied(ActionFrame frame)
        => frame.PositionalCorrect || frame.TrueNorthLeft > 0;
}
