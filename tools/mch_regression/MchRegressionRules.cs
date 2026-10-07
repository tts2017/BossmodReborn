namespace MchRegression;

public sealed record MchRegressionFinding(
    string Scenario,
    double Time,
    string Rule,
    string Expected,
    string Actual,
    string StateSummary,
    IReadOnlyList<MchActionUse> LastActions);

public sealed record MchScenarioVerdict(
    MchScenario Scenario,
    bool Passed,
    IReadOnlyList<MchRegressionFinding> HardFails,
    IReadOnlyList<MchRegressionFinding> SoftFails,
    IReadOnlyList<string> CoverageGaps,
    MchSimulationResult Simulation);

public static class MchRegressionRules
{
    public static MchScenarioVerdict Evaluate(MchSimulationResult result, string source)
    {
        var hard = new List<MchRegressionFinding>();
        var soft = new List<MchRegressionFinding>();
        var coverage = new List<string>();
        ValidateSource(result, source, hard, coverage);
        ValidateActions(result, hard, soft);
        ValidateScenarioSpecific(result, hard, soft, coverage);
        return new(result.Scenario, hard.Count == 0 && coverage.Count == 0, hard, soft, coverage, result);
    }

    private static void ValidateSource(MchSimulationResult result, string source, List<MchRegressionFinding> hard, List<string> coverage)
    {
        if (!source.Contains("private void OverheatedGCDs(Enemy? primaryTarget)", StringComparison.Ordinal))
            coverage.Add("MCH.cs OverheatedGCDs not found");
        if (!source.Contains("private void ToolGCDs(in Strategy strategy, Enemy? primaryTarget)", StringComparison.Ordinal))
            coverage.Add("MCH.cs ToolGCDs not found");
        if (!source.Contains("private void UseCharges(in Strategy strategy, Enemy? primaryTarget)", StringComparison.Ordinal))
            coverage.Add("MCH.cs UseCharges not found");
        if (!source.Contains("private bool ShouldReassemble(in Strategy strategy, Enemy? primaryTarget)", StringComparison.Ordinal))
            coverage.Add("MCH.cs ShouldReassemble not found");
        if (!source.Contains("private bool ShouldMinion(in Strategy strategy, Enemy? primaryTarget)", StringComparison.Ordinal))
            coverage.Add("MCH.cs ShouldMinion not found");
        if (!source.Contains("private bool ShouldHypercharge(in Strategy strategy, Enemy? primaryTarget)", StringComparison.Ordinal))
            coverage.Add("MCH.cs ShouldHypercharge not found");
        if (!source.Contains("private Enemy? GetWildfireTarget(in Strategy strategy, Enemy? primaryTarget)", StringComparison.Ordinal))
            coverage.Add("MCH.cs GetWildfireTarget not found");
        if (!source.Contains("private void PushDancingMadForcedGCD(Enemy? primaryTarget)", StringComparison.Ordinal))
            coverage.Add("MCH.cs PushDancingMadForcedGCD not found");
        if (!source.Contains("!ShouldUseDancingMadProfile() || Overheated", StringComparison.Ordinal))
            AddHard(result, hard, 0, "Source: DancingMadOverheatGuard", "PushDancingMadForcedGCD returns during Overheated", "guard missing from MCH.cs");
        if (!source.Contains("NextToolCharge > GCD && ExcavatorLeft == 0 && FMFLeft == 0", StringComparison.Ordinal))
            AddHard(result, hard, 0, "Source: WildfireOpenerWaitsFMF", "opener Wildfire waits for tools, Excavator, and FMF", "opener Wildfire condition is incomplete");
        if (source.Contains("PushGCD(AID.FullMetalField", StringComparison.Ordinal) && ExtractMethod(source, "private void OverheatedGCDs").Contains("AID.FullMetalField", StringComparison.Ordinal))
            AddHard(result, hard, 0, "Source: OverheatFMF", "OverheatedGCDs does not push Full Metal Field", "Full Metal Field appears in OverheatedGCDs");
    }

    private static void ValidateActions(MchSimulationResult result, List<MchRegressionFinding> hard, List<MchRegressionFinding> soft)
    {
        var actions = result.Actions;
        var wildfireUses = actions.Where(a => a.Action == MchAction.Wildfire).ToArray();
        var queenUses = actions.Where(a => a.Action is MchAction.AutomatonQueen or MchAction.RookAutoturret).ToArray();
        foreach (var action in actions)
        {
            if (!MchActionModel.IsUnlocked(action.Action, result.Scenario.Level))
                AddHard(result, hard, action.Time, "HardFail01: LockedAction", "No locked action is used", $"{MchActionModel.Name(action.Action)} at level {result.Scenario.Level}");
            if (MchActionModel.IsEnemyAction(action.Action) && action.TargetCount <= 0)
                AddHard(result, hard, action.Time, "HardFail02: EnemyActionDuringUntargetable", "Enemy-target action waits for a target", $"{MchActionModel.Name(action.Action)} with targetCount=0");
            if (action.OverheatLeft > 0 && MchActionModel.IsToolBlockedDuringOverheat(action.Action))
                AddHard(result, hard, action.Time, "HardFail03: ToolDuringOverheat", "Overheat GCDs are only Blazing Shot / Heat Blast / Auto Crossbow", MchActionModel.Name(action.Action));
            if (action.Note.Contains("reassemble-invalid-target", StringComparison.Ordinal))
                AddHard(result, hard, action.Time, "HardFail08-10: ReassembleInvalidTarget", "Reassemble only lands on Drill/AirAnchor/HotShot/ChainSaw/Excavator", MchActionModel.Name(action.Action));
            if (action.Action is MchAction.QueenOverdrive or MchAction.RookOverdrive)
                AddHard(result, hard, action.Time, "HardFail14: Overdrive", "Queen/Rook Overdrive is not automatic outside End", MchActionModel.Name(action.Action));
            if (result.Scenario.PotionStrategy == MchPotionStrategy.Off && action.Action == MchAction.Potion)
                AddHard(result, hard, action.Time, "HardFail15: PotionDisabled", "Potion disabled scenarios do not use potion", "Potion used");
            if (result.Scenario.PotionStrategy == MchPotionStrategy.EvenBursts && action.Action == MchAction.Potion && action.Time < 30)
                AddHard(result, hard, action.Time, "HardFail16: EvenPotionOpener", "EvenBurstOnly does not use opener potion", "Potion used in opener");
            if (action.Action == MchAction.Potion && action.Time >= 30 && !PotionNearAirAnchorOrHotShot(actions, action))
                AddHard(result, hard, action.Time, "HardFail18: PotionPosition", "Even-burst potion is before AirAnchor/HotShot", "Potion not adjacent to AirAnchor/HotShot");
        }

        ValidateHeatWindows(result, hard);
        ValidateWildfireWindows(result, hard, soft);
        ValidateChargeOverflow(result, hard, soft);

        if (result.Scenario.WildfireStrategy != MchWildfireStrategy.Delay && result.Scenario.HyperchargeStrategy != MchOffensiveStrategy.Delay && result.Scenario.Level >= 45 && result.Scenario.Duration >= 120 && wildfireUses.Length == 0)
            AddHard(result, hard, result.Scenario.Duration, "HardFail28: NoWildfire", "Wildfire is used during combat", "No Wildfire casts");
        if (result.Scenario.QueenStrategy != MchQueenStrategy.Never && result.Scenario.Level >= 80 && result.Scenario.Duration >= 150 && queenUses.Length == 0)
            AddHard(result, hard, result.Scenario.Duration, "HardFail29: NoQueen", "Queen/Rook is used during combat", "No Queen/Rook casts");
        if (result.Scenario.TargetOverride && queenUses.Length > 0 && !result.FinalState.QueenTargetFixed)
            AddHard(result, hard, queenUses[0].Time, "HardFail30: QueenTargetOverride", "Queen/Rook target remains fixed after override", "Queen target marker was not set");
    }

    private static void ValidateScenarioSpecific(MchSimulationResult result, List<MchRegressionFinding> hard, List<MchRegressionFinding> soft, List<string> coverage)
    {
        if (result.Actions.Count == 0 && result.Scenario.Duration > 0)
            AddHard(result, hard, 0, "HardFail24: NoActions", "Combat produces actions when targets exist", "No actions");
        if (result.Scenario.Name == "Potion_OpenerAndEvenBurst" && !result.Actions.Any(a => a.Action == MchAction.Potion && a.Time is >= -2.2 and <= -1.8))
            AddHard(result, hard, 0, "HardFail17: MissingOpenerPotion", "OpenerAndEvenBurst uses potion around countdown 2s", "No -2s potion");
        if (result.Scenario.Name.StartsWith("FullUptime_3Targets", StringComparison.Ordinal) && !result.Actions.Any(a => a.Action is MchAction.Scattergun or MchAction.Bioblaster))
            AddSoft(result, soft, 30, "SoftFail14: ThreeTargetAoe", "3 target scenarios transition to AoE where valid", "No Scattergun/Bioblaster observed");
        if (result.Scenario.Name.StartsWith("FullUptime_6Targets", StringComparison.Ordinal) && result.Actions.Any(a => a.Action == MchAction.AirAnchor && a.Battery > 80))
            AddSoft(result, soft, 60, "SoftFail12: AirAnchorSixTargets", "6+ target AirAnchor is delayed unless needed", "Air Anchor used at high battery");
        if (result.Scenario.DancingMad && !result.Actions.Any(a => a.Action is MchAction.AirAnchor or MchAction.ChainSaw or MchAction.Excavator))
            coverage.Add("Dancing Mad offensive windows did not become reachable in this scenario");
    }

    private static void ValidateHeatWindows(MchSimulationResult result, List<MchRegressionFinding> hard)
    {
        var actions = result.Actions;
        for (var i = 0; i < actions.Count; ++i)
        {
            if (actions[i].Action != MchAction.Hypercharge)
                continue;
            var heatGcds = actions.Skip(i + 1).TakeWhile(a => a.Time <= actions[i].Time + 10.2).Where(a => MchActionModel.IsGcd(a.Action)).ToArray();
            var firstFive = heatGcds.Take(5).ToArray();
            if (firstFive.Length >= 5 && firstFive.Any(a => !MchActionModel.IsHeatGcd(a.Action)))
                AddHard(result, hard, actions[i].Time, "HardFail04: BrokenHeatFive", "First five Overheat GCDs are Heat Blast/Blazing Shot/Auto Crossbow", string.Join(">", firstFive.Select(a => MchActionModel.Name(a.Action))));
        }
    }

    private static void ValidateWildfireWindows(MchSimulationResult result, List<MchRegressionFinding> hard, List<MchRegressionFinding> soft)
    {
        foreach (var wf in result.Actions.Where(a => a.Action == MchAction.Wildfire))
        {
            var heatGcds = result.Actions.Count(a => a.Time > wf.Time && a.Time <= wf.Time + 10.2 && MchActionModel.IsHeatGcd(a.Action));
            if (heatGcds <= 2)
                AddHard(result, hard, wf.Time, "HardFail05: WeakWildfire", "Wildfire has at least 3 Heat GCDs", $"{heatGcds} Heat GCDs");
            var drift = Math.Abs((wf.Time % 120) - 0);
            drift = Math.Min(drift, Math.Abs(120 - drift));
            if (wf.Time > 90 && drift > 20)
                AddSoft(result, soft, wf.Time, "SoftFail07: WildfireDrift", "Wildfire stays near 2-minute cadence", $"drift={drift:F1}s");
        }
    }

    private static void ValidateChargeOverflow(MchSimulationResult result, List<MchRegressionFinding> hard, List<MchRegressionFinding> soft)
    {
        foreach (var action in result.Actions)
        {
        }
        ValidateSustainedChargeCap(result, hard);
    }

    private static void ValidateSustainedChargeCap(MchSimulationResult result, List<MchRegressionFinding> hard)
    {
        if (result.Scenario.Level < 92)
            return;

        ValidateSustainedCap(result, hard, "Gauss Round:3/3", "HardFail12: GaussCapped", MchAction.GaussRound, MchAction.DoubleCheck);
        ValidateSustainedCap(result, hard, "Ricochet:3/3", "HardFail12: RicochetCapped", MchAction.Ricochet, MchAction.Checkmate);
    }

    private static void ValidateSustainedCap(MchSimulationResult result, List<MchRegressionFinding> hard, string cappedText, string rule, MchAction lowAction, MchAction highAction)
    {
        double? cappedSince = null;
        foreach (var action in result.Actions)
        {
            if (action.Time < 5)
                continue;

            if (action.Action == lowAction || action.Action == highAction || !action.Charges.Contains(cappedText, StringComparison.Ordinal))
            {
                cappedSince = null;
                continue;
            }

            cappedSince ??= action.Time;
            if (action.Time - cappedSince > 10)
            {
                AddHard(result, hard, action.Time, rule, $"{MchActionModel.Name(highAction)} does not remain capped for more than 10s", action.Charges);
                return;
            }
        }
    }

    private static bool PotionNearAirAnchorOrHotShot(IReadOnlyList<MchActionUse> actions, MchActionUse potion)
        => actions.Any(a => a.Time > potion.Time && a.Time <= potion.Time + 4.0 && a.Action is MchAction.AirAnchor or MchAction.HotShot);

    private static void AddHard(MchSimulationResult result, List<MchRegressionFinding> hard, double time, string rule, string expected, string actual)
        => hard.Add(new(result.Scenario.Name, time, rule, expected, actual, result.FinalState.Summary(), LastActionsAt(result, time)));

    private static void AddSoft(MchSimulationResult result, List<MchRegressionFinding> soft, double time, string rule, string expected, string actual)
        => soft.Add(new(result.Scenario.Name, time, rule, expected, actual, result.FinalState.Summary(), LastActionsAt(result, time)));

    private static IReadOnlyList<MchActionUse> LastActionsAt(MchSimulationResult result, double time)
        => result.Actions.Where(a => a.Time <= time).TakeLast(10).ToArray();

    private static string ExtractMethod(string source, string marker)
    {
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return string.Empty;
        var next = source.IndexOf("\n    private ", start + marker.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }
}
