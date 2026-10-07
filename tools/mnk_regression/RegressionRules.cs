namespace MnkRegression;

public static class RegressionRules
{
    public static void Apply(ScenarioDefinition scenario, ScenarioResult result)
    {
        AddRange(result, CheckGcdStarvation(scenario, result));
        AddRange(result, CheckLookAway(result));
        AddRange(result, CheckEncounterHint(result));
        AddRange(result, CheckRiddleOfEarth(result));
        AddRange(result, CheckThunderclap(scenario, result));
        AddRange(result, CheckDancingMad(result));
        AddRange(result, CheckEvenBurstSync(result));
    }

    public static void Apply(BattleScenario scenario, ScenarioResult result)
    {
        AddRange(result, result.Frames.SelectMany(f => f.HardFails));
        AddRange(result, CheckGcdStarvation(scenario.Source, result));
        AddRange(result, CheckTargetState(result));
        AddRange(result, CheckLookAway(result));
        AddRange(result, CheckEncounterHint(result));
        AddRange(result, CheckRiddleOfEarth(result));
        AddRange(result, CheckThunderclap(result));
        AddRange(result, CheckUnlockedActions(result));
        AddRange(result, CheckPbTargetLossConsistency(result));
        AddRange(result, CheckPhantomRushPlacement(scenario, result));
        AddRange(result, CheckDancingMad(result));
        AddRange(result, CheckEvenBurstSync(scenario, result));
    }

    private static IEnumerable<HardFailCode> CheckGcdStarvation(ScenarioDefinition scenario, ScenarioResult result)
    {
        var maxGap = MnkPatch75Data.EffectiveGcd(scenario.GcdSeconds, scenario.LevelCap) * 1.5;
        double? lastGcd = null;
        foreach (var frame in result.Frames.Where(f => f.TargetAvailable && f.HaveTarget && f.MeleeAvailable && !f.LookAway && f.GCDReadyIn <= 0.05))
        {
            if (frame.SelectedGCD == null)
            {
                if (lastGcd is { } last && frame.Time - last > maxGap)
                    yield return HardFailCode.GcdStarvation;
                continue;
            }
            lastGcd = frame.Time;
        }
    }

    private static IEnumerable<HardFailCode> CheckLookAway(ScenarioResult result)
    {
        foreach (var frame in result.Frames.Where(f => f.LookAway))
        {
            if (MnkPatch75Data.IsEnemyGcd(frame.SelectedGCD))
                yield return HardFailCode.LookAwayEnemyGcd;
            if (frame.SelectedOGCD.Any(a => a is "RiddleOfFire" or "Brotherhood" or "PerfectBalance" or "Potion"))
                yield return HardFailCode.LookAwayEnemyOgcd;
        }
    }

    private static IEnumerable<HardFailCode> CheckTargetState(ScenarioResult result)
    {
        foreach (var frame in result.Frames)
        {
            if (!frame.TargetAvailable && EnemyGcd(frame.SelectedGCD))
                yield return HardFailCode.TargetableEnemyAction;
            if (!frame.HaveTarget && (EnemyGcd(frame.SelectedGCD) || frame.SelectedOGCD.Any(EnemyOgcd)))
                yield return HardFailCode.HaveTargetEnemyAction;
            if (!frame.MeleeAvailable && MeleeGcd(frame.SelectedGCD))
                yield return HardFailCode.MeleeGcdOutOfRange;
        }
    }

    private static IEnumerable<HardFailCode> CheckEncounterHint(ScenarioResult result)
    {
        foreach (var frame in result.Frames.Where(f => f.EncounterHint is EncounterHintMode.Trash or EncounterHintMode.BossReturn or EncounterHintMode.HoldBurst))
            if (frame.SelectedOGCD.Any(a => AutomaticBurstActionBlockedByEncounterHint(result, a)))
                yield return frame.EncounterHint == EncounterHintMode.BossReturn
                    ? HardFailCode.BossReturnResourceSpend
                    : HardFailCode.EncounterHintAutomaticBurst;
    }

    private static bool AutomaticBurstActionBlockedByEncounterHint(ScenarioResult result, string action)
        => action switch
        {
            "RiddleOfFire" => result.RoFStrategy != AutoForceDelayMode.Force,
            "Brotherhood" => result.BrotherhoodStrategy != AutoForceDelayMode.Force,
            "PerfectBalance" => result.PBStrategy != PBStrategyMode.Force,
            "Potion" => true,
            _ => false
        };

    private static IEnumerable<HardFailCode> CheckRiddleOfEarth(ScenarioResult result)
    {
        if (result.Category != ScenarioCategory.RiddleOfEarth)
            yield break;

        var used = result.Timings.RiddleOfEarth.Count > 0;
        var allowed = result.Frames.Any(f => f.PredictedDamage.Any(d =>
            d.AppliesToSelf
            && d.Activation - f.Time is >= 0 and <= 10
            && d.Type is PredictedDamageKind.Raidwide or PredictedDamageKind.Shared or PredictedDamageKind.Tankbuster));
        if (used != allowed)
            yield return HardFailCode.RiddleOfEarthInvalidDamage;
    }

    private static IEnumerable<HardFailCode> CheckThunderclap(ScenarioDefinition scenario, ScenarioResult result)
    {
        var unsafeWindow = scenario.EventList.Any(e => e.Type == ScenarioEventType.UnsafeThunderclap);
        if (unsafeWindow && result.Frames.Any(f => f.SelectedOGCD.Contains("Thunderclap")))
            yield return HardFailCode.UnsafeThunderclap;
    }

    private static IEnumerable<HardFailCode> CheckThunderclap(ScenarioResult result)
    {
        if (result.Frames.Any(f => !f.ThunderclapSafe && f.SelectedOGCD.Contains("Thunderclap")))
            yield return HardFailCode.UnsafeThunderclap;
    }

    private static IEnumerable<HardFailCode> CheckPbTargetLossConsistency(ScenarioResult result)
    {
        if (result.Category != ScenarioCategory.TargetLostDuringPB)
            yield break;

        var contradictory = result.Frames.Any(f => !f.HaveTarget && f.PerfectBalanceLeft > 0 && EnemyGcd(f.SelectedGCD));
        if (contradictory)
            yield return HardFailCode.PBDuringTargetLossContradiction;
    }

    private static IEnumerable<HardFailCode> CheckPhantomRushPlacement(ScenarioResult result)
    {
        if (result.LevelCap < 70
            || result.NadiStrategy is not (NadiStrategyMode.Automatic or NadiStrategyMode.DoubleLunar)
            || result.BlitzStrategy is BlitzStrategyMode.Delay or BlitzStrategyMode.Force
            || result.BrotherhoodStrategy == AutoForceDelayMode.Delay)
            yield break;

        foreach (var pr in result.Timings.PhantomRush)
        {
            if (!result.Timings.Brotherhood.Any(b => pr >= b - 0.5 && pr <= b + 22))
                yield return HardFailCode.PhantomRushOutsideEvenBurst;
        }
    }

    private static IEnumerable<HardFailCode> CheckPhantomRushPlacement(BattleScenario scenario, ScenarioResult result)
    {
        if (result.LevelCap < 70
            || result.NadiStrategy is not (NadiStrategyMode.Automatic or NadiStrategyMode.DoubleLunar)
            || result.PBStrategy == PBStrategyMode.Force
            || result.BlitzStrategy is BlitzStrategyMode.Delay or BlitzStrategyMode.Force
            || result.BrotherhoodStrategy == AutoForceDelayMode.Delay)
            yield break;

        var initialPendingPhantomRush = scenario.Source.InitialGauge is
        {
            LunarNadi: true,
            SolarNadi: true,
            BlitzLeft: > 0,
            BeastChakra.Count: >= 3
        };
        for (var i = 0; i < result.Timings.PhantomRush.Count; ++i)
        {
            var pr = result.Timings.PhantomRush[i];
            if (initialPendingPhantomRush && i == 0)
                continue;
            if (!result.Timings.Brotherhood.Any(b => pr >= b - 0.5 && pr <= b + 22))
                yield return HardFailCode.PhantomRushOutsideEvenBurst;
        }
    }

    private static IEnumerable<HardFailCode> CheckDancingMad(ScenarioResult result)
    {
        if (result.Category != ScenarioCategory.DancingMad)
            yield break;

        var nextEven = result.Timings.Brotherhood.FirstOrDefault(t => t >= 206);
        if (nextEven <= 0)
            yield return HardFailCode.DancingMadResyncBroken;
        else if (!result.Timings.RiddleOfFire.Any(t => Math.Abs(t - nextEven) <= 5)
            || !result.Timings.PhantomRush.Any(t => t >= nextEven && t <= nextEven + 20))
            yield return HardFailCode.DancingMadResyncBroken;
    }

    private static IEnumerable<HardFailCode> CheckEvenBurstSync(ScenarioResult result)
    {
        if (result.RoFStrategy == AutoForceDelayMode.Delay
            || result.BrotherhoodStrategy == AutoForceDelayMode.Delay
            || result.RoFStrategy == AutoForceDelayMode.Force
            || result.BrotherhoodStrategy == AutoForceDelayMode.Force
            || result.PBStrategy == PBStrategyMode.Force
            || result.BlitzStrategy is BlitzStrategyMode.Delay or BlitzStrategyMode.Multi or BlitzStrategyMode.MultiRoF)
            yield break;

        var scenarioEnd = result.Frames.Count == 0 ? 0 : result.Frames.Max(f => f.Time);
        foreach (var bh in result.Timings.Brotherhood)
        {
            if (scenarioEnd - bh < 25)
                continue;

            if (!result.Timings.RiddleOfFire.Any(r => Math.Abs(r - bh) <= 8))
                yield return HardFailCode.EvenBurstSyncBroken;
            var preBurstPbWindow = MnkPatch75Data.EvenPbTrackingLead;
            if (result.PBStrategy != PBStrategyMode.Delay && result.Timings.PerfectBalance.Count(pb => pb >= bh - preBurstPbWindow && pb <= bh + 20) < 2)
                yield return HardFailCode.OddPbBreaksNextEven;
        }
    }

    private static IEnumerable<HardFailCode> CheckEvenBurstSync(BattleScenario scenario, ScenarioResult result)
    {
        if (result.RoFStrategy == AutoForceDelayMode.Delay
            || result.BrotherhoodStrategy == AutoForceDelayMode.Delay
            || result.RoFStrategy == AutoForceDelayMode.Force
            || result.BrotherhoodStrategy == AutoForceDelayMode.Force
            || result.PBStrategy == PBStrategyMode.Force
            || result.BlitzStrategy is BlitzStrategyMode.Delay or BlitzStrategyMode.Multi or BlitzStrategyMode.MultiRoF)
            yield break;

        var scenarioEnd = result.Frames.Count == 0 ? 0 : result.Frames.Max(f => f.Time);
        foreach (var bh in result.Timings.Brotherhood)
        {
            if (scenarioEnd - bh < 25 || BurstWindowDisrupted(scenario, bh))
                continue;

            if (!result.Timings.RiddleOfFire.Any(r => Math.Abs(r - bh) <= 8))
                yield return HardFailCode.EvenBurstSyncBroken;
            var preBurstPbWindow = MnkPatch75Data.EvenPbTrackingLead;
            if (result.PBStrategy != PBStrategyMode.Delay && result.Timings.PerfectBalance.Count(pb => pb >= bh - preBurstPbWindow && pb <= bh + 20) < 2)
                yield return HardFailCode.OddPbBreaksNextEven;
        }
    }

    private static IEnumerable<HardFailCode> CheckUnlockedActions(ScenarioResult result)
    {
        foreach (var action in result.Frames.SelectMany(f => f.SelectedOGCD).Concat(result.Frames.Select(f => f.SelectedGCD).OfType<string>()))
            if (!MnkPatch75Data.IsUnlocked(action, result.LevelCap))
                yield return HardFailCode.UnlearnedAction;
    }

    private static bool BurstWindowDisrupted(BattleScenario scenario, double anchor)
        => scenario.Source.EventList.Any(e =>
            e.Type is ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable or ScenarioEventType.LookAway
            && e.Start < anchor + 20
            && e.End > anchor - 4);

    private static void AddRange(ScenarioResult result, IEnumerable<HardFailCode> failures)
    {
        foreach (var failure in failures.Distinct())
            if (!result.HardFails.Contains(failure))
                result.HardFails.Add(failure);
    }

    private static bool EnemyGcd(string? action)
        => MnkPatch75Data.IsEnemyGcd(action);

    private static bool EnemyOgcd(string action)
        => action is "SteelPeak" or "ForbiddenChakra" or "HowlingFist" or "Enlightenment";

    private static bool MeleeGcd(string? action)
        => MnkPatch75Data.IsMeleeGcd(action);
}
