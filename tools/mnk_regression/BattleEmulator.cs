using System.Security.Cryptography;

namespace MnkRegression;

public sealed class BattleEmulator
{
    private readonly CandidateMode _candidate;

    public BattleEmulator(CandidateMode candidate = CandidateMode.Baseline)
    {
        _candidate = candidate;
    }

    public RegressionResult Run(IEnumerable<BattleScenario> scenarios)
    {
        var result = new RegressionResult
        {
            ToolVersion = "mnk-regression-emulator-patch75-v2",
            Candidate = _candidate,
            MnkSourceHash = ComputeMnkSourceHash()
        };

        foreach (var scenario in scenarios)
            result.Scenarios.Add(RunScenario(scenario, _candidate));

        return result;
    }

    private static ScenarioResult RunScenario(BattleScenario scenario, CandidateMode candidate)
    {
        var state = CloneInitialState(scenario.InitialState);
        var timeline = new BattleTimeline(scenario);
        var simulator = new MnkActionSimulator(scenario, candidate);
        var result = new ScenarioResult
        {
            ScenarioName = scenario.Name,
            Category = scenario.Category,
            BurstTiming = scenario.StrategyProfile.BurstTiming,
            OpenerRoFOffset = scenario.StrategyProfile.OpenerRoFOffset,
            RotationMode = scenario.StrategyProfile.RotationMode,
            PBStrategy = scenario.StrategyProfile.PBStrategy,
            BlitzStrategy = scenario.StrategyProfile.BlitzStrategy,
            NadiStrategy = scenario.StrategyProfile.NadiStrategy,
            RoFStrategy = scenario.StrategyProfile.RoFStrategy,
            BrotherhoodStrategy = scenario.StrategyProfile.BrotherhoodStrategy,
            RoWStrategy = scenario.StrategyProfile.RoWStrategy,
            RoEStrategy = scenario.StrategyProfile.RoEStrategy,
            ThunderclapStrategy = scenario.StrategyProfile.ThunderclapStrategy,
            LevelCap = scenario.StrategyProfile.LevelCap,
            EffectiveGcd = MnkPatch75Data.EffectiveGcd(scenario.StrategyProfile.GcdSeconds, scenario.StrategyProfile.LevelCap)
        };

        MnkStateSnapshot? previousRecorded = null;
        for (var time = 0.0; time <= scenario.DurationSeconds + 0.0001; time += scenario.TickInterval)
        {
            time = Math.Round(time, 4);
            simulator.Advance(state, time == 0 ? 0 : scenario.TickInterval);
            timeline.Apply(state, time);
            simulator.ExecuteFrame(state);

            if (time > 0
                && state.GCDReadyIn <= 0.001
                && state.Targetable
                && state.HaveTarget
                && state.CanMelee
                && !state.LookAwayActive
                && state.LastGCDAction == null)
                state.GcdIdleTime += scenario.TickInterval;

            if (ShouldRecordSnapshot(state, previousRecorded, scenario.TickInterval))
            {
                var snapshot = MnkStateSnapshot.From(state);
                result.Snapshots.Add(snapshot);
                result.Frames.Add(ToActionFrame(scenario, snapshot));
                previousRecorded = snapshot;
            }
        }

        PopulateTimings(scenario, state, result);
        PopulateDamageMetrics(state, scenario, result);
        RegressionRules.Apply(scenario, result);
        result.Score = ScoreScenario(result);
        return result;
    }

    private static bool ShouldRecordSnapshot(BattleState state, MnkStateSnapshot? previous, double tick)
    {
        if (state.LastGCDAction != null || state.LastOGCDActions.Count > 0 || state.HardFailCandidates.Count > 0)
            return true;
        if (previous == null)
            return true;
        if (Math.Abs(state.Time % 5.0) <= tick / 2)
            return true;
        return previous.Targetable != state.Targetable
            || previous.HaveTarget != state.HaveTarget
            || previous.CanMelee != state.CanMelee
            || previous.LookAwayActive != state.LookAwayActive
            || previous.ForbiddenZoneActive != state.ForbiddenZoneActive
            || previous.ThunderclapSafe != state.ThunderclapSafe
            || previous.EncounterHint != state.EncounterHint
            || previous.PredictedDamage.Count != state.PredictedDamage.Count;
    }

    private static ActionFrame ToActionFrame(BattleScenario scenario, MnkStateSnapshot snapshot)
        => new()
        {
            ScenarioName = scenario.Name,
            Time = snapshot.Time,
            CombatTimer = snapshot.CombatTimer,
            SelectedGCD = snapshot.SelectedGCD,
            SelectedOGCD = snapshot.SelectedOGCDs,
            TargetAvailable = snapshot.Targetable,
            HaveTarget = snapshot.HaveTarget,
            MeleeAvailable = snapshot.CanMelee,
            Forbidden = snapshot.ForbiddenZoneActive,
            ThunderclapSafe = snapshot.ThunderclapSafe,
            NumAOETargets = snapshot.NumAOETargets,
            EncounterHint = snapshot.EncounterHint,
            BurstTiming = scenario.StrategyProfile.BurstTiming,
            OpenerRoFOffset = scenario.StrategyProfile.OpenerRoFOffset,
            PBLeft = snapshot.PerfectBalanceLeft,
            PBCharges = snapshot.PerfectBalanceCharges,
            BlitzLeft = snapshot.BlitzLeft,
            Nadi = snapshot.Nadi,
            FireLeft = snapshot.RiddleOfFireLeft,
            BrotherhoodLeft = snapshot.BrotherhoodLeft,
            PerfectBalanceLeft = snapshot.PerfectBalanceLeft,
            FormShiftLeft = snapshot.FormlessFistLeft,
            GCDReadyIn = snapshot.GCDReadyIn,
            Chakra = snapshot.Chakra,
            BeastChakra = snapshot.BeastChakra,
            PotionUsed = snapshot.SelectedOGCDs.Contains("Potion"),
            LookAway = snapshot.LookAwayActive,
            PredictedDamage = snapshot.PredictedDamage,
            HardFails = snapshot.HardFailCandidates,
            RiddleOfEarthReason = snapshot.RiddleOfEarthReason,
            ScoreContribution = ScoreFrame(snapshot)
        };

    private static double ScoreFrame(MnkStateSnapshot snapshot)
    {
        var score = 0.0;
        if (snapshot.Targetable && snapshot.HaveTarget && snapshot.CanMelee && !snapshot.LookAwayActive && snapshot.SelectedGCD != null)
            score += 1;
        if (snapshot.SelectedOGCDs.Count > 0)
            score += snapshot.SelectedOGCDs.Count * 0.25;
        if (snapshot.SelectedGCD == "PhantomRush")
            score += 2;
        return score;
    }

    private static void PopulateTimings(BattleScenario scenario, BattleState state, ScenarioResult result)
    {
        var initialGauge = scenario.Source.InitialGauge;
        if (initialGauge?.PerfectBalanceLeft > 0 || initialGauge?.BlitzLeft > 0 || initialGauge?.BeastChakra?.Count > 0)
        {
            var firstBrotherhood = state.ActionHistory.FirstOrDefault(a => a.Action == "Brotherhood" && a.Time <= 30)?.Time ?? 0;
            result.Timings.PerfectBalance.Add(firstBrotherhood > 0 ? Math.Max(0, firstBrotherhood - 2.5) : 0);
        }

        foreach (var action in state.ActionHistory)
        {
            switch (action.Action)
            {
                case "RiddleOfFire":
                    result.Timings.RiddleOfFire.Add(action.Time);
                    break;
                case "Brotherhood":
                    result.Timings.Brotherhood.Add(action.Time);
                    break;
                case "PerfectBalance":
                    result.Timings.PerfectBalance.Add(action.Time);
                    break;
                case "ElixirField":
                case "ElixirBurst":
                case "CelestialRevolution":
                case "FlintStrike":
                case "RisingPhoenix":
                case "TornadoKick":
                case "PhantomRush":
                    result.Timings.MasterfulBlitz.Add(action.Time);
                    if (MnkPatch75Data.IsPhantomRush(action.Action))
                        result.Timings.PhantomRush.Add(action.Time);
                    break;
                case "Potion":
                    result.Timings.Potion.Add(action.Time);
                    break;
                case "RiddleOfEarth":
                    result.Timings.RiddleOfEarth.Add(action.Time);
                    break;
            }
        }
    }

    private static double ScoreScenario(ScenarioResult result)
    {
        var score = result.Frames.Sum(f => f.ScoreContribution);
        score += result.Metrics.GetValueOrDefault("PPS") * 10;
        score += result.Timings.Brotherhood.Count * 3;
        score += result.Timings.PhantomRush.Count * 5;
        score += result.Timings.RiddleOfEarth.Count * 2;
        score -= result.HardFails.Count * 1000;

        var preBurstPbWindow = MnkPatch75Data.EvenPbTrackingLead;
        result.Metrics["EvenPB2SuccessCount"] = result.Timings.Brotherhood.Count(b => result.Timings.PerfectBalance.Count(pb => pb >= b - preBurstPbWindow && pb <= b + 20) >= 2);
        result.Metrics["EvenSyncScore"] = result.Timings.Brotherhood.Count == 0 ? 0 : result.Timings.Brotherhood.Average(b => result.Timings.RiddleOfFire.Count == 0 ? 999 : result.Timings.RiddleOfFire.Min(r => Math.Abs(r - b)));
        result.Metrics["PhantomRushEvenSuccessCount"] = result.Timings.Brotherhood.Count(b => result.Timings.PhantomRush.Any(pr => pr >= b && pr <= b + 22));
        result.Metrics["GcdStopTime"] = Math.Round(result.Metrics.GetValueOrDefault("MeasuredGcdIdleTime"), 3);
        return Math.Round(score, 3);
    }

    private static void PopulateDamageMetrics(BattleState state, BattleScenario scenario, ScenarioResult result)
    {
        var totalBasePotency = 0.0;
        var totalPotency = 0.0;
        var rofPotency = 0.0;
        var brotherhoodPotency = 0.0;
        var potionPotency = 0.0;
        var pbOvercapTime = 0.0;
        var chakraOvercapFrames = 0;

        foreach (var action in state.ActionHistory)
        {
            if (action.BasePotency <= 0)
                continue;

            totalBasePotency += action.BasePotency;
            totalPotency += action.EffectivePotency;
            if (action.UnderRiddleOfFire)
                rofPotency += action.EffectivePotency;
            if (action.UnderBrotherhood)
                brotherhoodPotency += action.EffectivePotency;
            if (action.UnderPotion)
                potionPotency += action.EffectivePotency;
        }

        foreach (var snapshot in result.Snapshots)
        {
            if (snapshot.PerfectBalanceCharges >= 2)
                pbOvercapTime += scenario.TickInterval;
            if (snapshot.Chakra >= 5 && snapshot.SelectedOGCDs.All(a => a is not "ForbiddenChakra" and not "Enlightenment"))
                chakraOvercapFrames++;
        }

        result.Metrics["TotalBasePotency"] = Math.Round(totalBasePotency, 3);
        result.Metrics["TotalPotency"] = Math.Round(totalPotency, 3);
        result.Metrics["PPS"] = scenario.DurationSeconds > 0 ? Math.Round(totalPotency / scenario.DurationSeconds, 3) : 0;
        result.Metrics["RoFPotency"] = Math.Round(rofPotency, 3);
        result.Metrics["BrotherhoodPotency"] = Math.Round(brotherhoodPotency, 3);
        result.Metrics["PotionPotency"] = Math.Round(potionPotency, 3);
        result.Metrics["PerfectBalanceUses"] = result.Timings.PerfectBalance.Count;
        result.Metrics["PerfectBalanceOvercapTime"] = Math.Round(pbOvercapTime, 3);
        result.Metrics["ChakraOvercapFrames"] = chakraOvercapFrames;
        result.Metrics["PhantomRushCount"] = result.Timings.PhantomRush.Count;
        result.Metrics["MasterfulBlitzCount"] = result.Timings.MasterfulBlitz.Count;
        result.Metrics["RoFDrift"] = DriftScore(result.Timings.RiddleOfFire, 60.0);
        result.Metrics["BrotherhoodDrift"] = DriftScore(result.Timings.Brotherhood, 120.0);
        result.Metrics["MeasuredGcdIdleTime"] = Math.Round(state.GcdIdleTime, 3);
    }

    private static double DriftScore(IReadOnlyList<double> timings, double interval)
    {
        if (timings.Count <= 1)
            return 0;

        var drift = 0.0;
        for (var i = 1; i < timings.Count; ++i)
            drift += Math.Abs(timings[i] - timings[i - 1] - interval);
        return Math.Round(drift / (timings.Count - 1), 3);
    }

    private static BattleState CloneInitialState(BattleState source)
    {
        var state = new BattleState
        {
            TargetCount = source.TargetCount,
            NumAOETargets = source.NumAOETargets,
            NumMeleeAOETargets = source.NumMeleeAOETargets,
            EncounterHint = source.EncounterHint,
            ThunderclapSafe = source.ThunderclapSafe,
            SafeMeleeAvailable = source.SafeMeleeAvailable
        };
        state.PlayerGauge.Chakra = source.PlayerGauge.Chakra;
        state.PlayerGauge.ChakraProgress = source.PlayerGauge.ChakraProgress;
        state.PlayerGauge.BeastChakra.AddRange(source.PlayerGauge.BeastChakra);
        state.PlayerGauge.LunarNadi = source.PlayerGauge.LunarNadi;
        state.PlayerGauge.SolarNadi = source.PlayerGauge.SolarNadi;
        state.PlayerGauge.CurrentForm = source.PlayerGauge.CurrentForm;
        state.PlayerGauge.OpoFury = source.PlayerGauge.OpoFury;
        state.PlayerGauge.RaptorFury = source.PlayerGauge.RaptorFury;
        state.PlayerGauge.CoeurlFury = source.PlayerGauge.CoeurlFury;
        state.PlayerGauge.PerfectBalancePlan = source.PlayerGauge.PerfectBalancePlan;
        state.PlayerGauge.OpoOpoFormLeft = source.PlayerGauge.OpoOpoFormLeft;
        state.PlayerGauge.RaptorFormLeft = source.PlayerGauge.RaptorFormLeft;
        state.PlayerGauge.CoeurlFormLeft = source.PlayerGauge.CoeurlFormLeft;
        state.PlayerGauge.FormlessFistLeft = source.PlayerGauge.FormlessFistLeft;
        state.PlayerGauge.DisciplinedFistLeft = source.PlayerGauge.DisciplinedFistLeft;
        state.PlayerGauge.LeadenFistLeft = source.PlayerGauge.LeadenFistLeft;
        state.PlayerGauge.PerfectBalanceLeft = source.PlayerGauge.PerfectBalanceLeft;
        state.PlayerGauge.PerfectBalanceStacks = source.PlayerGauge.PerfectBalanceStacks;
        state.PlayerGauge.RiddleOfFireLeft = source.PlayerGauge.RiddleOfFireLeft;
        state.PlayerGauge.BrotherhoodLeft = source.PlayerGauge.BrotherhoodLeft;
        state.PlayerGauge.RiddleOfWindLeft = source.PlayerGauge.RiddleOfWindLeft;
        state.PlayerGauge.PotionLeft = source.PlayerGauge.PotionLeft;
        state.PlayerGauge.RiddleOfEarthLeft = source.PlayerGauge.RiddleOfEarthLeft;
        state.PlayerGauge.EarthsReplyLeft = source.PlayerGauge.EarthsReplyLeft;
        state.PlayerGauge.WindsReplyLeft = source.PlayerGauge.WindsReplyLeft;
        state.PlayerGauge.FiresReplyLeft = source.PlayerGauge.FiresReplyLeft;
        state.PlayerGauge.BlitzLeft = source.PlayerGauge.BlitzLeft;
        state.Cooldowns.PerfectBalance.Charges = source.Cooldowns.PerfectBalance.Charges;
        state.Cooldowns.PerfectBalance.ReadyIn = source.Cooldowns.PerfectBalance.ReadyIn;
        state.Cooldowns.RiddleOfFire.ReadyIn = source.Cooldowns.RiddleOfFire.ReadyIn;
        state.Cooldowns.Brotherhood.ReadyIn = source.Cooldowns.Brotherhood.ReadyIn;
        state.Cooldowns.RiddleOfWind.ReadyIn = source.Cooldowns.RiddleOfWind.ReadyIn;
        state.Cooldowns.RiddleOfEarth.ReadyIn = source.Cooldowns.RiddleOfEarth.ReadyIn;
        state.Cooldowns.Potion.ReadyIn = source.Cooldowns.Potion.ReadyIn;
        state.Cooldowns.Thunderclap.Charges = source.Cooldowns.Thunderclap.Charges;
        state.Cooldowns.Thunderclap.ReadyIn = source.Cooldowns.Thunderclap.ReadyIn;
        state.Cooldowns.TrueNorth.Charges = source.Cooldowns.TrueNorth.Charges;
        state.Cooldowns.TrueNorth.ReadyIn = source.Cooldowns.TrueNorth.ReadyIn;
        return state;
    }

    private static string ComputeMnkSourceHash()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "BossMod", "Autorotation", "Standard", "xan", "Melee", "MNK.cs");
        if (!File.Exists(path))
            return "";
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BossModReborn.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}
