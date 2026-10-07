namespace MchRegression;

public sealed class MchSimulator
{
    private const double SimulationStep = 0.1;
    private const double OgcdLock = 0.65;
    private const double ReassembleCooldown = 55;
    private const double DrillCooldown = 20;
    private const double BioblasterCooldown = 20;
    private const double AirAnchorCooldown = 40;
    private const double ChainSawCooldown = 60;
    private const double WildfireCooldown = 120;
    private const double BarrelCooldown = 120;
    private const double GaussCooldown = 30;
    private const double RicochetCooldown = 30;
    private const double PotionCooldown = 270;

    private static readonly MchDancingMadWindow[] DancingMadWindows =
    [
        new(0.3, MchAction.Drill, 0.8, 1.5),
        new(2.8, MchAction.AirAnchor, 1.5, 2.0),
        new(5.3, MchAction.ChainSaw, 1.5, 2.5),
        new(8.1, MchAction.Excavator, 2.0, 8.0),
        new(22.8, MchAction.FullMetalField, 5.0, 4.0),
        new(122.9, MchAction.AirAnchor, 2.0, 2.0),
        new(125.4, MchAction.ChainSaw, 2.0, 2.5),
        new(126.9, MchAction.Wildfire, 2.0, 3.0),
        new(127.9, MchAction.Excavator, 2.0, 8.0),
        new(129.0, MchAction.Hypercharge, 2.5, 3.5),
        new(140.3, MchAction.FullMetalField, 5.0, 4.0),
        new(243.5, MchAction.BarrelStabilizer, 3.0, 3.0),
        new(246.5, MchAction.ChainSaw, 3.0, 3.0),
        new(247.9, MchAction.Wildfire, 3.0, 3.0),
        new(249.1, MchAction.AirAnchor, 3.0, 3.0),
        new(250.3, MchAction.Hypercharge, 3.0, 3.0),
        new(259.1, MchAction.Excavator, 4.0, 6.0),
        new(264.1, MchAction.FullMetalField, 4.0, 4.0),
        new(363.5, MchAction.BarrelStabilizer, 3.0, 3.0),
        new(366.6, MchAction.ChainSaw, 3.0, 3.0),
        new(368.0, MchAction.Wildfire, 3.0, 3.0),
        new(369.1, MchAction.AirAnchor, 4.0, 5.0),
        new(367.3, MchAction.Hypercharge, 3.0, 3.5),
        new(374.0, MchAction.Excavator, 4.0, 4.0),
        new(484.4, MchAction.BarrelStabilizer, 3.0, 3.0),
        new(488.8, MchAction.ChainSaw, 3.0, 3.0),
        new(491.7, MchAction.Wildfire, 3.5, 3.5),
        new(492.6, MchAction.Hypercharge, 3.5, 3.5),
        new(492.0, MchAction.Excavator, 3.0, 5.0),
        new(503.1, MchAction.FullMetalField, 5.0, 5.0)
    ];

    public MchSimulationResult Run(MchScenario scenario)
    {
        var state = CreateInitialState(scenario);
        UseCountdownPotionIfNeeded(state);

        while (state.Time <= scenario.Duration)
        {
            ApplyTimeline(state);

            if (!state.InCombat || !state.Targetable)
            {
                state.AdvanceTo(Math.Min(scenario.Duration + SimulationStep, state.Time + SimulationStep));
                continue;
            }

            if (state.Time + 0.0001 >= state.GCDReadyAt)
            {
                var gcd = SelectGcd(state);
                if (gcd != MchAction.None)
                    UseGcd(state, gcd);
            }

            if (state.Time + 0.0001 >= state.AnimationLockUntil)
            {
                var ogcd = SelectOgcd(state);
                if (ogcd != MchAction.None)
                    UseOgcd(state, ogcd);
            }

            var next = Math.Min(state.Time + SimulationStep, scenario.Duration + SimulationStep);
            if (state.GCDReadyAt > state.Time)
                next = Math.Min(next, state.GCDReadyAt);
            if (state.AnimationLockUntil > state.Time)
                next = Math.Min(next, state.AnimationLockUntil);
            if (next <= state.Time + 0.00001)
                next = state.Time + SimulationStep;
            state.AdvanceTo(next);
        }

        return new(scenario, state, state.LastActions.ToArray());
    }

    private static MchState CreateInitialState(MchScenario scenario)
    {
        var state = new MchState
        {
            Scenario = scenario,
            Level = scenario.Level,
            TargetCount = scenario.InitialTargetCount,
            ConeTargetCount = scenario.InitialTargetCount,
            RangedSplashTargetCount = scenario.InitialTargetCount,
            ChainSawTargetCount = scenario.InitialTargetCount,
            Heat = scenario.Level >= 30 ? 50 : 0,
            Battery = 0,
            InCombat = true,
            Targetable = true,
            Random = new Random(scenario.Seed),
            TargetOverrideActive = scenario.TargetOverride
        };

        foreach (var action in MchActionModel.Definitions.Keys)
        {
            state.Cooldowns[action] = 0;
            state.InitializeCharges(action);
        }

        if (scenario.Name == "Potion_NotReadyAt2min_UseNextEven")
            state.PotionReadyAt = 180;

        return state;
    }

    private static void ApplyTimeline(MchState state)
    {
        var scenario = state.Scenario;
        var t = state.CombatTimer;
        var targetCount = scenario.InitialTargetCount;
        var targetable = true;

        switch (scenario.Kind)
        {
            case MchScenarioKind.SwitchTargetsEvery30s:
                targetCount = ((int)(t / 30) % 2 == 0) ? scenario.InitialTargetCount : (scenario.InitialTargetCount == 1 ? 6 : 1);
                break;
            case MchScenarioKind.RandomTargetSwitch:
            case MchScenarioKind.Random:
                targetCount = ((int)(t / 15 + scenario.Seed) % 3) switch { 0 => 1, 1 => 3, _ => 6 };
                break;
            case MchScenarioKind.Downtime:
                targetable = !IsDowntimeWindow(scenario, t);
                break;
            case MchScenarioKind.TargetLost:
                targetable = !IsTargetLostWindow(scenario, t);
                break;
            case MchScenarioKind.DancingMad:
                targetable = !IsDancingMadDowntime(scenario, t);
                if (scenario.Name.Contains("target_lost", StringComparison.OrdinalIgnoreCase) && t is >= 122 and <= 127)
                    targetable = false;
                break;
        }

        if (!targetable)
            targetCount = 0;

        state.Targetable = targetable;
        state.TargetCount = targetCount;
        state.ConeTargetCount = targetCount;
        state.RangedSplashTargetCount = targetCount == 3 && scenario.Name.Contains("split", StringComparison.OrdinalIgnoreCase) ? 2 : targetCount;
        state.ChainSawTargetCount = targetCount;
        if (targetable)
            state.LastTargetableTime = state.Time;
    }

    private static bool IsDowntimeWindow(MchScenario scenario, double time)
        => scenario.Name switch
        {
            "Downtime_30s_at_90s" => time is >= 90 and < 120,
            "Downtime_15s_before_2min" => time is >= 105 and < 120,
            "Downtime_10s_after_Wildfire" => time is >= 35 and < 45,
            "Downtime_during_Queen" => time is >= 60 and < 75,
            "Downtime_after_BarrelStabilizer" => time is >= 123 and < 138,
            "Downtime_before_FullMetalField" => time is >= 132 and < 142,
            _ => false
        };

    private static bool IsTargetLostWindow(MchScenario scenario, double time)
        => scenario.Name switch
        {
            "TargetLost_5s_random" => time is >= 55 and < 60 || time is >= 155 and < 160,
            "TargetLost_during_Hypercharge" => time is >= 8 and < 13,
            "TargetLost_before_Wildfire" => time is >= 118 and < 123,
            "TargetLost_after_ChainSaw" => time is >= 62 and < 67,
            "TargetLost_before_Queen" => time is >= 56 and < 61,
            _ => false
        };

    private static bool IsDancingMadDowntime(MchScenario scenario, double time)
        => scenario.Name switch
        {
            "DancingMad_P2_downtime_resume" => time is >= 197 and < 205,
            "DancingMad_final_phase" => time is >= 320 and < 430,
            _ => false
        };

    private static MchAction SelectGcd(MchState state)
    {
        if (!state.HasAnyTarget)
            return MchAction.None;

        var candidates = new List<MchCandidate>();
        if (state.Overheated && MchActionModel.IsUnlocked(MchAction.HeatBlast, state.Level))
            AddOverheatedGcds(state, candidates);
        else
        {
            AddToolGcds(state, candidates);
            AddFillerGcds(state, candidates);
        }

        return candidates.OrderByDescending(c => c.Priority).ThenBy(c => c.Order).FirstOrDefault()?.Action ?? MchAction.None;
    }

    private static void AddOverheatedGcds(MchState state, List<MchCandidate> candidates)
    {
        // Mirrors MCH.cs OverheatedGCDs(): Auto Crossbow if the target breakpoint is met, otherwise Blazing Shot / Heat Blast.
        if (ShouldUseAutoCrossbow(state) && MchActionModel.IsUnlocked(MchAction.AutoCrossbow, state.Level) && state.HasConeTarget)
            Add(candidates, MchAction.AutoCrossbow, 2);
        if (state.HasAnyTarget)
            Add(candidates, MchActionModel.BestHeatShot(state.Level), 2);
    }

    private static void AddToolGcds(MchState state, List<MchCandidate> candidates)
    {
        // Mirrors MCH.cs ToolGCDs(), including the Tools.Delay FMF escape hatch for Wildfire/FMF expiry.
        if (state.Scenario.ToolStrategy == MchToolStrategy.Delay)
        {
            if (ShouldSpendFullMetalFieldDuringToolDelay(state))
                Add(candidates, MchAction.FullMetalField, 2);
            return;
        }

        if (state.ExcavatorReadyLeft > state.GCDLength && MchActionModel.IsUnlocked(MchAction.Excavator, state.Level) && state.HasSplashTarget && !ShouldHoldForDancingMadAction(state, MchAction.Excavator))
            Add(candidates, MchAction.Excavator, 2);

        var forced = ForcedDancingMadGcd(state);
        if (forced != MchAction.None)
            Add(candidates, forced, MchActionModel.ToolPriorityHigh + 5);

        if (GcdReady(state, MchAction.AirAnchor) && MchActionModel.IsUnlocked(MchAction.AirAnchor, state.Level) && state.HasAnyTarget && !ShouldHoldForDancingMadAction(state, MchAction.AirAnchor))
            Add(candidates, MchAction.AirAnchor, AirAnchorPriority(state));

        if (GcdReady(state, MchAction.ChainSaw) && MchActionModel.IsUnlocked(MchAction.ChainSaw, state.Level) && state.HasChainSawTarget && !ShouldHoldForDancingMadAction(state, MchAction.ChainSaw))
            Add(candidates, MchAction.ChainSaw, MchActionModel.ToolPriorityChainSaw);

        if (GcdReady(state, MchAction.Bioblaster) && MchActionModel.IsUnlocked(MchAction.Bioblaster, state.Level) && state.TargetCount >= 3 && state.HasConeTarget && !ShouldHoldForDancingMadAction(state, MchAction.Bioblaster))
            Add(candidates, MchAction.Bioblaster, state.ChargeCapIn(MchAction.Bioblaster) <= state.GCDLength ? MchActionModel.ToolPriorityHigh : 2);

        if (GcdReady(state, MchAction.Drill) && MchActionModel.IsUnlocked(MchAction.Drill, state.Level) && state.HasAnyTarget && !ShouldHoldForDancingMadAction(state, MchAction.Drill))
            Add(candidates, MchAction.Drill, state.ChargeCapIn(MchAction.Drill) <= state.GCDLength ? MchActionModel.ToolPriorityHigh : 2);

        if (!MchActionModel.IsUnlocked(MchAction.AirAnchor, state.Level) && GcdReady(state, MchAction.HotShot) && MchActionModel.IsUnlocked(MchAction.HotShot, state.Level) && state.HasAnyTarget)
            Add(candidates, MchAction.HotShot, MchActionModel.ToolPriorityHigh);

        if (state.FullMetalMachinistLeft > state.GCDLength && state.ExcavatorReadyLeft == 0 && MchActionModel.IsUnlocked(MchAction.FullMetalField, state.Level) && state.HasSplashTarget && (!ShouldHoldForDancingMadAction(state, MchAction.FullMetalField) || ShouldForceFullMetalFieldSpend(state)))
            Add(candidates, MchAction.FullMetalField, 2);
    }

    private static void AddFillerGcds(MchState state, List<MchCandidate> candidates)
    {
        if (ShouldUseScattergun(state) && state.HasConeTarget)
        {
            Add(candidates, MchActionModel.BestSpreadShot(state.Level), 2);
            return;
        }

        if (!state.HasAnyTarget)
            return;

        Add(candidates, state.ComboLastMove switch
        {
            MchAction.SlugShot or MchAction.HeatedSlugShot => MchActionModel.BestCleanShot(state.Level),
            MchAction.SplitShot or MchAction.HeatedSplitShot => MchActionModel.BestSlugShot(state.Level),
            _ => MchActionModel.BestSplitShot(state.Level)
        }, 2);
    }

    private static MchAction SelectOgcd(MchState state)
    {
        if (!state.HasAnyTarget && state.LastOGCD != MchAction.Potion)
            return MchAction.None;
        if (state.OgcdsSinceLastGcd >= 2)
            return MchAction.None;

        if (ShouldUsePotion(state))
            return MchAction.Potion;
        if (GetWildfireTarget(state) && state.Scenario.WildfireStrategy != MchWildfireStrategy.Delay)
            return MchAction.Wildfire;
        if (state.WildfireLeft > 0 && ShouldHypercharge(state))
            return MchAction.Hypercharge;
        if (ShouldReassemble(state))
            return MchAction.Reassemble;
        if (ShouldStabilize(state))
            return MchAction.BarrelStabilizer;

        var charge = SelectChargeAction(state);
        if (charge != MchAction.None)
            return charge;

        if (ShouldMinion(state))
            return MchActionModel.IsUnlocked(MchAction.AutomatonQueen, state.Level) ? MchAction.AutomatonQueen : MchAction.RookAutoturret;
        if (state.WildfireLeft == 0 && ShouldHypercharge(state))
            return MchAction.Hypercharge;
        return MchAction.None;
    }

    private static void UseGcd(MchState state, MchAction action)
    {
        if (!MchActionModel.IsUnlocked(action, state.Level))
            return;

        if (state.ReassembleArmed && !MchActionModel.IsReassembleEligible(action))
            state.AddAction(action, "reassemble-invalid-target");
        else
            state.AddAction(action);

        state.LastGCD = action;
        state.LastGcdTime = state.Time;
        state.OgcdsSinceLastGcd = 0;
        state.GCDLength = MchActionModel.IsHeatGcd(action) ? MchActionModel.HeatGcd : MchActionModel.StandardGcd;
        state.GCDReadyAt = state.Time + state.GCDLength;
        state.AnimationLockUntil = state.Time + 0.1;

        if (MchActionModel.IsHeatGcd(action))
        {
            state.HeatGcdsInCurrentOverheat++;
            if (state.WildfireLeft > 0)
                state.HeatGcdsInCurrentWildfire++;
            state.ChargeFractions[MchAction.GaussRound] += 0.5;
            state.ChargeFractions[MchAction.Ricochet] += 0.5;
        }

        if (action is MchAction.HeatBlast or MchAction.BlazingShot or MchAction.AutoCrossbow && state.HeatGcdsInCurrentOverheat >= 5)
            state.OverheatedLeft = 0;

        if (MchActionModel.Definition(action).BatteryGain > 0)
            state.Battery = Math.Min(100, state.Battery + MchActionModel.Definition(action).BatteryGain);

        ApplyGcdCooldownsAndStatuses(state, action);
        UpdateCombo(state, action);
        state.ReassembleArmed = false;
    }

    private static void ApplyGcdCooldownsAndStatuses(MchState state, MchAction action)
    {
        switch (action)
        {
            case MchAction.Drill:
                SpendCharge(state, MchAction.Drill);
                break;
            case MchAction.Bioblaster:
                SpendCharge(state, MchAction.Bioblaster);
                break;
            case MchAction.AirAnchor:
                state.Cooldowns[MchAction.AirAnchor] = state.Time + AirAnchorCooldown;
                break;
            case MchAction.HotShot:
                state.Cooldowns[MchAction.HotShot] = state.Time + AirAnchorCooldown;
                break;
            case MchAction.ChainSaw:
                state.Cooldowns[MchAction.ChainSaw] = state.Time + ChainSawCooldown;
                if (MchActionModel.IsUnlocked(MchAction.Excavator, state.Level))
                    state.ExcavatorReadyLeft = 30;
                break;
            case MchAction.Excavator:
                state.ExcavatorReadyLeft = 0;
                break;
            case MchAction.FullMetalField:
                state.FullMetalMachinistLeft = 0;
                break;
        }
    }

    private static void UseOgcd(MchState state, MchAction action)
    {
        if (!MchActionModel.IsUnlocked(action, state.Level))
            return;

        state.AddAction(action);
        state.LastOGCD = action;
        state.LastOgcdTime = state.Time;
        state.OgcdsSinceLastGcd++;
        state.AnimationLockUntil = state.Time + OgcdLock;
        switch (action)
        {
            case MchAction.Potion:
                if (state.Time < 0)
                    state.PrePullPotionUses++;
                else
                    state.CombatPotionUses++;
                state.PotionReadyAt = state.Time + PotionCooldown;
                break;
            case MchAction.Wildfire:
                state.WildfireLeft = 10;
                state.HeatGcdsInCurrentWildfire = 0;
                state.Cooldowns[MchAction.Wildfire] = state.Time + WildfireCooldown;
                break;
            case MchAction.Reassemble:
                state.ReassembleLeft = 5;
                state.ReassembleArmed = true;
                state.Cooldowns[MchAction.Reassemble] = state.Time + ReassembleCooldown;
                break;
            case MchAction.BarrelStabilizer:
                state.Heat = Math.Min(100, state.Heat + 50);
                state.HyperchargedLeft = 30;
                if (MchActionModel.IsUnlocked(MchAction.FullMetalField, state.Level))
                    state.FullMetalMachinistLeft = 30;
                state.Cooldowns[MchAction.BarrelStabilizer] = state.Time + BarrelCooldown;
                break;
            case MchAction.GaussRound:
            case MchAction.DoubleCheck:
                SpendCharge(state, MchAction.GaussRound);
                break;
            case MchAction.Ricochet:
            case MchAction.Checkmate:
                SpendCharge(state, MchAction.Ricochet);
                break;
            case MchAction.AutomatonQueen:
            case MchAction.RookAutoturret:
                state.QueenActiveLeft = 15;
                state.Battery = 0;
                if (state.TargetOverrideActive)
                    state.QueenTargetFixed = true;
                break;
            case MchAction.Hypercharge:
                state.Heat = Math.Max(0, state.Heat - 50);
                state.OverheatedLeft = 10;
                state.HeatGcdsInCurrentOverheat = 0;
                state.Cooldowns[MchAction.Hypercharge] = state.Time + 10;
                break;
        }
    }

    private static void UseCountdownPotionIfNeeded(MchState state)
    {
        if (state.Scenario.PotionStrategy != MchPotionStrategy.OpenerAndEvenBursts || state.PotionReadyAt > 0)
            return;
        state.Time = -2;
        state.CombatTimer = 0;
        state.AddAction(MchAction.Potion, "countdown");
        state.PrePullPotionUses++;
        state.PotionReadyAt = state.Time + PotionCooldown;
        state.Time = 0;
    }

    private static bool ShouldUseAutoCrossbow(MchState state)
        => state.TargetCount >= MchActionModel.AutoCrossbowBreakpoint;

    private static bool ShouldUseScattergun(MchState state)
    {
        if (!MchActionModel.IsUnlocked(MchAction.SpreadShot, state.Level))
            return false;
        if (!MchActionModel.IsUnlocked(MchAction.Scattergun, state.Level))
            return state.TargetCount > 1;
        return state.TargetCount > 3 || state.TargetCount == 3 && state.RangedSplashTargetCount >= 3;
    }

    private static bool ShouldSpendFullMetalFieldDuringToolDelay(MchState state)
        => state.Scenario.ToolStrategy == MchToolStrategy.Delay
        && state.FullMetalMachinistLeft > state.GCDLength
        && state.ExcavatorReadyLeft == 0
        && ShouldForceFullMetalFieldSpend(state);

    private static bool ShouldForceFullMetalFieldSpend(MchState state)
        => state.FullMetalMachinistLeft < state.GCDLength * 2 || GetWildfireTargetIgnoringFullMetalField(state);

    private static int AirAnchorPriority(MchState state)
        => state.TargetCount >= 3 && state.ChargeCapIn(MchAction.AirAnchor) > state.GCDLength && state.Battery > 80 ? 1 : MchActionModel.ToolPriorityHigh;

    private static bool GcdReady(MchState state, MchAction action)
    {
        if (action is MchAction.Drill or MchAction.Bioblaster)
            return state.ChargeCount(action) > 0;
        return state.CooldownReady(action);
    }

    private static bool ShouldReassemble(MchState state)
    {
        if (!MchActionModel.IsUnlocked(MchAction.Reassemble, state.Level) || state.ReassembleLeft > 0 || state.Overheated || !state.HasAnyTarget)
            return false;
        if (state.ReadyIn(MchAction.Reassemble) > 0)
            return false;
        var nextGcd = SelectGcdWithoutReassemble(state);
        if (!MchActionModel.IsReassembleEligible(nextGcd))
            return false;
        if (state.ChargeCapIn(MchAction.Reassemble) <= state.GCDLength * 2)
            return true;
        if (ShouldHoldReassembleForBurst(state, nextGcd))
            return false;
        return nextGcd is MchAction.Drill or MchAction.AirAnchor or MchAction.HotShot or MchAction.ChainSaw or MchAction.Excavator;
    }

    private static MchAction SelectGcdWithoutReassemble(MchState state) => SelectGcd(state);

    private static bool ShouldHoldReassembleForBurst(MchState state, MchAction nextGcd)
        => IsEvenMinuteBurstWindow(state, 12, 4) && nextGcd is not MchAction.AirAnchor and not MchAction.HotShot and not MchAction.ChainSaw and not MchAction.Excavator;

    private static bool ShouldMinion(MchState state)
    {
        if ((!MchActionModel.IsUnlocked(MchAction.RookAutoturret, state.Level) && !MchActionModel.IsUnlocked(MchAction.AutomatonQueen, state.Level)) || !state.HasAnyTarget || state.QueenActiveLeft > 0 || state.Battery < 50 || GetWildfireTarget(state))
            return false;
        return state.Scenario.QueenStrategy switch
        {
            MchQueenStrategy.Never => false,
            MchQueenStrategy.FullGauge => state.Battery >= 100,
            MchQueenStrategy.RaidBuffsOnly => RaidBuffsLeft(state) > 10 || ShouldUseTwoMinuteQueen(state) || BatteryOvercapSoon(state),
            _ => ShouldUseAutomaticQueen(state)
        };
    }

    private static bool BatteryOvercapSoon(MchState state)
        => state.Battery >= 100 || state.Battery + MchActionModel.Definition(SelectGcd(state)).BatteryGain > 100;

    private static bool ShouldUseAutomaticQueen(MchState state)
    {
        if (BatteryOvercapSoon(state))
            return true;
        if (state.CombatTimer < 30)
            return state.Battery >= 60;
        if (state.CombatTimer is >= 50 and < 90)
            return state.Battery >= 90;
        if (ShouldUseTwoMinuteQueen(state))
            return true;
        var cycle = state.CombatTimer % 120;
        return state.CombatTimer >= 120 && cycle is >= 50 and <= 90 && state.Battery >= 50;
    }

    private static bool ShouldUseTwoMinuteQueen(MchState state)
        => state.Battery >= 100 && IsEvenMinuteBurstWindow(state, 8, 18) && state.LastGCD is MchAction.AirAnchor or MchAction.HotShot;

    private static bool ShouldHypercharge(MchState state)
    {
        if (!MchActionModel.IsUnlocked(MchAction.Hypercharge, state.Level) || state.Scenario.HyperchargeStrategy == MchOffensiveStrategy.Delay || state.Overheated || state.Heat < 50 || !state.HasAnyTarget)
            return false;
        if (state.WildfireLeft > 0)
            return state.Heat >= 50;
        if (state.ReassembleLeft > state.GCDLength)
            return false;
        if (state.Scenario.WildfireStrategy != MchWildfireStrategy.Delay && state.ReadyIn(MchAction.Wildfire) < 20 && !GetWildfireTarget(state))
            return false;
        if (state.FullMetalMachinistLeft > 0 && state.Gcd > 1.1)
            return false;
        if (state.Heat >= 100)
            return true;
        return (state.Scenario.ToolStrategy == MchToolStrategy.Delay || NextToolCap(state) > state.GCDLength + 7.5) && ChargesSafeForHypercharge(state);
    }

    private static bool ChargesSafeForHypercharge(MchState state)
    {
        if (ChargeCapSoon(state, MchAction.GaussRound) || ChargeCapSoon(state, MchAction.Ricochet))
            return true;
        if (MchActionModel.IsUnlocked(MchAction.DoubleCheck, state.Level) && state.ChargeCount(MchAction.GaussRound) >= 2)
            return false;
        if (MchActionModel.IsUnlocked(MchAction.Checkmate, state.Level) && state.ChargeCount(MchAction.Ricochet) >= 2)
            return false;
        return true;
    }

    private static bool GetWildfireTarget(MchState state)
    {
        if (!MchActionModel.IsUnlocked(MchAction.Wildfire, state.Level) || state.Scenario.WildfireStrategy == MchWildfireStrategy.Delay || state.ReadyIn(MchAction.Wildfire) > 0 || !state.HasAnyTarget)
            return false;
        if (state.Scenario.HyperchargeStrategy == MchOffensiveStrategy.Delay && !state.Overheated && !state.Hypercharged)
            return false;
        if (state.Scenario.DancingMad && !IsInDancingMadActionWindow(state, MchAction.Wildfire, 2.5, 2.5) && ShouldHoldForDancingMadAction(state, MchAction.Wildfire))
            return false;
        if (state.Scenario.DancingMad && IsInDancingMadActionWindow(state, MchAction.Wildfire, 2.5, 2.5))
            return WildfireBurstReady(state);
        if (state.Scenario.WildfireStrategy == MchWildfireStrategy.Hypercharge)
            return WildfireBurstReady(state);
        if (state.CombatTimer < 60)
            return NextToolCharge(state) > state.GCDLength && state.ExcavatorReadyLeft == 0 && state.FullMetalMachinistLeft == 0;
        return state.FullMetalMachinistLeft == 0 && WildfireBurstReady(state);
    }

    private static bool GetWildfireTargetIgnoringFullMetalField(MchState state)
    {
        if (!MchActionModel.IsUnlocked(MchAction.Wildfire, state.Level) || state.Scenario.WildfireStrategy == MchWildfireStrategy.Delay || state.ReadyIn(MchAction.Wildfire) > 0 || !state.HasAnyTarget)
            return false;
        if (state.Scenario.HyperchargeStrategy == MchOffensiveStrategy.Delay && !state.Overheated && !state.Hypercharged)
            return false;
        if (state.Scenario.DancingMad && !IsInDancingMadActionWindow(state, MchAction.Wildfire, 2.5, 2.5) && ShouldHoldForDancingMadAction(state, MchAction.Wildfire))
            return false;
        if (state.Scenario.DancingMad && IsInDancingMadActionWindow(state, MchAction.Wildfire, 2.5, 2.5))
            return WildfireBurstReady(state);
        if (state.Scenario.WildfireStrategy == MchWildfireStrategy.Hypercharge)
            return WildfireBurstReady(state);
        return state.CombatTimer >= 60 ? WildfireBurstReady(state) : NextToolCharge(state) > state.GCDLength && state.ExcavatorReadyLeft == 0;
    }

    private static bool WildfireBurstReady(MchState state)
        => state.ReassembleLeft <= state.GCDLength && (state.Overheated || state.Hypercharged || state.Heat >= 50 && (state.Scenario.ToolStrategy == MchToolStrategy.Delay || NextToolCap(state) > state.GCDLength + 7.5));

    private static bool ShouldStabilize(MchState state)
        => MchActionModel.IsUnlocked(MchAction.BarrelStabilizer, state.Level)
        && state.Scenario.BuffsStrategy != MchOffensiveStrategy.Delay
        && state.ReadyIn(MchAction.BarrelStabilizer) <= 0
        && state.HasAnyTarget
        && (state.Scenario.DancingMad && IsInDancingMadActionWindow(state, MchAction.BarrelStabilizer, 2.5, 2.5) || state.ReadyIn(MchAction.Drill) > 0);

    private static MchAction SelectChargeAction(MchState state)
    {
        var wfIn = state.ReadyIn(MchAction.Wildfire);
        var holdForBurst = wfIn <= 15 || RaidBuffsIn(state) < 15;
        var holdTwoForBurst = wfIn <= 6 || RaidBuffsIn(state) < 6;
        var gauss = ShouldUseCharge(state, MchAction.GaussRound, holdForBurst, holdTwoForBurst);
        var rico = ShouldUseCharge(state, MchAction.Ricochet, holdForBurst, holdTwoForBurst);
        if (gauss && rico)
            return state.ChargeCapIn(MchAction.GaussRound) <= state.ChargeCapIn(MchAction.Ricochet) ? MchActionModel.BestGaussRound(state.Level) : MchActionModel.BestRicochet(state.Level);
        if (gauss)
            return MchActionModel.BestGaussRound(state.Level);
        if (rico)
            return MchActionModel.BestRicochet(state.Level);
        return MchAction.None;
    }

    private static bool ShouldUseCharge(MchState state, MchAction action, bool holdForBurst, bool holdTwoForBurst)
    {
        if (!MchActionModel.IsUnlocked(action, state.Level) || state.ChargeCount(action) <= 0 || !state.HasAnyTarget)
            return false;
        if (state.ChargeCount(action) >= MchActionModel.Definition(action).MaxCharges || ChargeCapSoon(state, action) || state.WildfireLeft > 0 || RaidBuffsLeft(state) > 0 || state.Overheated)
            return true;
        if (holdTwoForBurst && state.ChargeCount(action) <= 2)
            return false;
        return !holdForBurst;
    }

    private static bool ShouldUsePotion(MchState state)
    {
        if (state.Scenario.PotionStrategy == MchPotionStrategy.Off || state.Time < state.PotionReadyAt || !state.HasAnyTarget)
            return false;
        if (state.CombatTimer < 30)
            return false;
        if (!IsEvenMinuteBurstWindow(state, 12, 6))
            return false;
        var next = SelectGcd(state);
        return next is MchAction.AirAnchor or MchAction.HotShot || GcdReady(state, MchAction.AirAnchor) || !MchActionModel.IsUnlocked(MchAction.AirAnchor, state.Level) && GcdReady(state, MchAction.HotShot);
    }

    private static MchAction ForcedDancingMadGcd(MchState state)
    {
        if (!state.Scenario.DancingMad || state.Overheated || !state.Targetable)
            return MchAction.None;
        foreach (var window in DancingMadWindows)
        {
            if (state.CombatTimer < window.Time - window.Before || state.CombatTimer > window.Time + window.After)
                continue;
            if (!MchActionModel.IsUnlocked(window.Action, state.Level))
                continue;
            if (window.Action is not MchAction.Excavator and not MchAction.FullMetalField and not MchAction.ChainSaw and not MchAction.AirAnchor and not MchAction.Drill)
                continue;
            if (window.Action == MchAction.Excavator && state.ExcavatorReadyLeft <= state.GCDLength)
                continue;
            if (window.Action == MchAction.FullMetalField && state.FullMetalMachinistLeft <= state.GCDLength)
                continue;
            if (window.Action is MchAction.ChainSaw or MchAction.AirAnchor or MchAction.Drill && !GcdReady(state, window.Action))
                continue;
            return window.Action;
        }
        return MchAction.None;
    }

    private static bool ShouldHoldForDancingMadAction(MchState state, MchAction action)
    {
        if (!state.Scenario.DancingMad || ActionCapSoon(state, action))
            return false;
        foreach (var window in DancingMadWindows)
            if (window.Action == action && state.CombatTimer >= window.Time - 10 && state.CombatTimer < window.Time - window.Before)
                return true;
        return false;
    }

    private static bool ActionCapSoon(MchState state, MchAction action)
        => action switch
        {
            MchAction.AirAnchor => state.ReadyIn(MchAction.AirAnchor) <= state.GCDLength,
            MchAction.ChainSaw => state.ReadyIn(MchAction.ChainSaw) <= state.GCDLength,
            MchAction.Drill => state.ChargeCapIn(MchAction.Drill) <= state.GCDLength,
            MchAction.Bioblaster => state.ChargeCapIn(MchAction.Bioblaster) <= state.GCDLength,
            MchAction.Excavator => state.ExcavatorReadyLeft > 0 && state.ExcavatorReadyLeft <= state.GCDLength * 2,
            MchAction.FullMetalField => state.FullMetalMachinistLeft > 0 && state.FullMetalMachinistLeft <= state.GCDLength * 2,
            MchAction.Hypercharge => state.Heat >= 100,
            MchAction.AutomatonQueen => state.Battery >= 100,
            _ => false
        };

    private static bool IsInDancingMadActionWindow(MchState state, MchAction action, double before, double after)
        => state.Scenario.DancingMad && DancingMadWindows.Any(w => w.Action == action && state.CombatTimer >= w.Time - Math.Max(before, w.Before) && state.CombatTimer <= w.Time + Math.Max(after, w.After));

    private static bool ChargeCapSoon(MchState state, MchAction action)
        => state.ChargeCapIn(action) <= state.GCDLength + 0.6 || state.ChargeCount(action) >= MchActionModel.Definition(action).MaxCharges;

    private static double NextToolCharge(MchState state)
        => Math.Min(ToolReadyIn(state, MchAction.Drill), Math.Min(ToolReadyIn(state, MchAction.ChainSaw), ToolReadyIn(state, MchAction.AirAnchor)));

    private static double NextToolCap(MchState state)
        => Math.Min(ToolCapIn(state, MchAction.Drill), Math.Min(ToolCapIn(state, MchAction.ChainSaw), ToolCapIn(state, MchAction.AirAnchor)));

    private static double ToolReadyIn(MchState state, MchAction action)
        => MchActionModel.IsUnlocked(action, state.Level) ? state.ReadyIn(action) : double.PositiveInfinity;

    private static double ToolCapIn(MchState state, MchAction action)
        => MchActionModel.IsUnlocked(action, state.Level) ? action == MchAction.Drill ? state.ChargeCapIn(action) : state.ReadyIn(action) : double.PositiveInfinity;

    private static bool IsEvenMinuteBurstWindow(MchState state, double before, double after)
    {
        if (state.CombatTimer < 100)
            return false;
        var cycle = state.CombatTimer % 120;
        return cycle <= after || 120 - cycle <= before;
    }

    private static double RaidBuffsLeft(MchState state)
    {
        var cycle = (state.CombatTimer - 7.8) % 120;
        if (cycle < 0)
            cycle += 120;
        return cycle <= 20 ? 20 - cycle : 0;
    }

    private static double RaidBuffsIn(MchState state)
    {
        var cycle = (state.CombatTimer - 7.8) % 120;
        if (cycle < 0)
            return -cycle;
        return cycle <= 20 ? 0 : 120 - cycle;
    }

    private static void SpendCharge(MchState state, MchAction action)
    {
        state.InitializeCharges(action);
        state.Charges[action] = Math.Max(0, state.Charges[action] - 1);
    }

    private static void UpdateCombo(MchState state, MchAction action)
    {
        state.ComboLastMove = action switch
        {
            MchAction.SplitShot or MchAction.HeatedSplitShot => action,
            MchAction.SlugShot or MchAction.HeatedSlugShot => action,
            MchAction.CleanShot or MchAction.HeatedCleanShot => MchAction.None,
            _ => state.ComboLastMove
        };
    }

    private static void Add(List<MchCandidate> candidates, MchAction action, int priority)
        => candidates.Add(new(action, priority, candidates.Count));
}

public sealed record MchSimulationResult(MchScenario Scenario, MchState FinalState, IReadOnlyList<MchActionUse> Actions);

public sealed record MchCandidate(MchAction Action, int Priority, int Order);

public sealed record MchDancingMadWindow(double Time, MchAction Action, double Before, double After);
