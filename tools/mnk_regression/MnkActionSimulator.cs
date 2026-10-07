namespace MnkRegression;

public sealed class MnkActionSimulator
{
    private const double PotionCooldown = 270.0;
    private const double RofCooldown = 60.0;
    private const double BrotherhoodCooldown = 120.0;
    private const double PBCooldown = 40.0;
    private const double RiddleOfEarthCooldown = 120.0;
    private const double RiddleOfEarthDuration = 10.0;
    private const double EarthsReplyDuration = 30.0;
    private const double RiddleOfWindCooldown = 90.0;
    private const double ReadyLeeway = 0.10;
    private const double SixSidedStarApplicationDelay = 0.62;
    private const double RepresentativePartyChakraPerSecond = 0.56;
    private const double RepresentativeCriticalHitRate = 0.20;

    private readonly BattleScenario _scenario;
    private readonly CandidateMode _candidate;
    private readonly List<double> _rofTimes;
    private readonly List<double> _brotherhoodTimes;
    private readonly List<double> _potionTimes;
    private readonly List<PbRequest> _pbRequests;
    private readonly List<double> _pbRequestTimes;

    private int Level => _scenario.StrategyProfile.LevelCap;
    private double Gcd => MnkPatch75Data.EffectiveGcd(_scenario.StrategyProfile.GcdSeconds, Level);

    public MnkActionSimulator(BattleScenario scenario, CandidateMode candidate = CandidateMode.Baseline)
    {
        _scenario = scenario;
        _candidate = candidate;
        var openerOffset = scenario.StrategyProfile.OpenerRoFOffset switch
        {
            OpenerRoFOffsetMode.ZeroSecondBurst => 0.0,
            OpenerRoFOffsetMode.Early58 => 5.8,
            OpenerRoFOffsetMode.PartyBurstAligned => 10.0,
            _ => 7.8
        };
        _rofTimes = BurstTimes(openerOffset, RofCooldown, scenario.DurationSeconds).ToList();
        _brotherhoodTimes = BurstTimes(openerOffset, BrotherhoodCooldown, scenario.DurationSeconds).ToList();
        _potionTimes = _brotherhoodTimes.ToList();
        _pbRequests = BuildPerfectBalanceRequests(scenario.Source, _rofTimes, _brotherhoodTimes, Gcd);
        _pbRequestTimes = _pbRequests.Select(r => r.Time).ToList();
    }

    public void Advance(BattleState state, double delta)
    {
        TickCooldown(state.Cooldowns.RiddleOfFire, delta);
        TickCooldown(state.Cooldowns.Brotherhood, delta);
        TickCooldown(state.Cooldowns.RiddleOfWind, delta);
        TickCooldown(state.Cooldowns.RiddleOfEarth, delta);
        TickCooldown(state.Cooldowns.Potion, delta);
        TickCooldown(state.Cooldowns.PerfectBalance, delta, PBCooldown, 2);
        TickCooldown(state.Cooldowns.Thunderclap, delta, 30, 3);
        TickCooldown(state.Cooldowns.TrueNorth, delta, 45, 2);

        state.GCDReadyIn = Math.Max(0, state.GCDReadyIn - delta);
        state.AnimationLock = Math.Max(0, state.AnimationLock - delta);
        state.AutoAttackReadyIn = Math.Max(0, state.AutoAttackReadyIn - delta);

        var gauge = state.PlayerGauge;
        if (Level >= 70 && gauge.BrotherhoodLeft > 0 && delta > 0)
            AddChakra(gauge, RepresentativePartyChakraPerSecond * delta);

        gauge.PerfectBalanceLeft = Math.Max(0, gauge.PerfectBalanceLeft - delta);
        gauge.RiddleOfFireLeft = Math.Max(0, gauge.RiddleOfFireLeft - delta);
        gauge.BrotherhoodLeft = Math.Max(0, gauge.BrotherhoodLeft - delta);
        gauge.RiddleOfWindLeft = Math.Max(0, gauge.RiddleOfWindLeft - delta);
        gauge.PotionLeft = Math.Max(0, gauge.PotionLeft - delta);
        gauge.RiddleOfEarthLeft = Math.Max(0, gauge.RiddleOfEarthLeft - delta);
        gauge.EarthsReplyLeft = Math.Max(0, gauge.EarthsReplyLeft - delta);
        gauge.WindsReplyLeft = Math.Max(0, gauge.WindsReplyLeft - delta);
        gauge.FiresReplyLeft = Math.Max(0, gauge.FiresReplyLeft - delta);
        gauge.FormlessFistLeft = Math.Max(0, gauge.FormlessFistLeft - delta);
        gauge.DisciplinedFistLeft = Math.Max(0, gauge.DisciplinedFistLeft - delta);
        gauge.LeadenFistLeft = Math.Max(0, gauge.LeadenFistLeft - delta);
        gauge.BlitzLeft = Math.Max(0, gauge.BlitzLeft - delta);
        if (gauge.BlitzLeft <= 0 && gauge.BeastChakra.Count >= 3)
        {
            gauge.BeastChakra.Clear();
            if (UnlockedBlitz && _scenario.StrategyProfile.BlitzStrategy != BlitzStrategyMode.Delay)
                state.HardFailCandidates.Add(HardFailCode.BlitzExpired);
        }
    }

    public void ExecuteFrame(BattleState state)
    {
        state.ResetFrameActions();
        QueueAutoAttack(state);

        if (state.GCDReadyIn <= ReadyLeeway)
        {
            QueueGcd(state);
            return;
        }

        if (state.AnimationLock <= 0)
            QueueOgcds(state);
    }

    private void QueueAutoAttack(BattleState state)
    {
        if (state.AutoAttackReadyIn > ReadyLeeway
            || !state.InCombat
            || !state.Targetable
            || !state.HaveTarget
            || !state.CanMelee
            || state.LookAwayActive)
            return;

        RecordAction(state, "AutoAttack", isGcd: false, []);
        state.AutoAttackReadyIn = MnkPatch75Data.EffectiveAutoAttackDelay(Level, state.PlayerGauge.RiddleOfWindLeft > 0);
    }

    private void QueueOgcds(BattleState state)
    {
        if (!CanUseAutomaticBurst(state))
        {
            if (_scenario.StrategyProfile.RoFStrategy == AutoForceDelayMode.Force && ShouldUseRiddleOfFire(state, state.Time) && UseOgcd(state, "RiddleOfFire", false))
                return;
            if (_scenario.StrategyProfile.BrotherhoodStrategy == AutoForceDelayMode.Force && ShouldUseBrotherhood(state, state.Time) && UseOgcd(state, "Brotherhood", false))
                return;
            if (_scenario.StrategyProfile.PBStrategy == PBStrategyMode.Force && ShouldUsePerfectBalance(state) && UseOgcd(state, "PerfectBalance", false))
                return;
            if (_scenario.StrategyProfile.RotationMode == RotationMode.Full && ShouldUseRiddleOfEarth(state) && UseOgcd(state, "RiddleOfEarth", false))
                return;
            if (ShouldUseChakra(state))
                UseOgcd(state, MnkPatch75Data.ChakraSpender(Level, state.NumAOETargets >= 3), true);
            return;
        }

        var time = state.Time;
        var zeroSecondOpener = _scenario.StrategyProfile.OpenerRoFOffset == OpenerRoFOffsetMode.ZeroSecondBurst
            && state.CombatTimer <= Gcd + MnkPatch75Data.AnimationLock
            && !state.ActionHistory.Any(a => a.Action == "PerfectBalance");
        if (zeroSecondOpener && ShouldUsePerfectBalance(state) && UseOgcd(state, "PerfectBalance", false))
            return;
        if (Due(time, _potionTimes) && state.Cooldowns.Potion.ReadyIn <= ReadyLeeway && UseOgcd(state, "Potion", false))
            return;
        if (ShouldUseRiddleOfFire(state, time) && UseOgcd(state, "RiddleOfFire", false))
            return;
        if (ShouldUseBrotherhood(state, time) && UseOgcd(state, "Brotherhood", false))
            return;
        if (ShouldUsePerfectBalance(state) && UseOgcd(state, "PerfectBalance", false))
            return;
        if (ShouldUseRiddleOfEarth(state) && UseOgcd(state, "RiddleOfEarth", false))
            return;
        if (ShouldUseRiddleOfWind(state) && UseOgcd(state, "RiddleOfWind", false))
            return;
        if (ShouldUseChakra(state) && UseOgcd(state, MnkPatch75Data.ChakraSpender(Level, state.NumAOETargets >= 3), true))
            return;
        if (_scenario.StrategyProfile.ThunderclapStrategy == ThunderclapStrategyMode.GapClose
            && UnlockedThunderclap
            && !state.CanMelee
            && state.HaveTarget
            && state.ThunderclapSafe
            && state.Cooldowns.Thunderclap.Charges >= 1
            && !state.ActionHistory.Any(a => a.Action == "Thunderclap" && state.Time - a.Time < 10))
            UseOgcd(state, "Thunderclap", false);
    }

    private bool ShouldUseRiddleOfFire(BattleState state, double time)
        => UnlockedRiddleOfFire
        && _scenario.StrategyProfile.RoFStrategy != AutoForceDelayMode.Delay
        && state.Cooldowns.RiddleOfFire.ReadyIn <= ReadyLeeway
        && (_scenario.StrategyProfile.RoFStrategy == AutoForceDelayMode.Force || ScheduledBurstDue(time, _rofTimes));

    private bool ShouldUseBrotherhood(BattleState state, double time)
        => UnlockedBrotherhood
        && _scenario.StrategyProfile.BrotherhoodStrategy != AutoForceDelayMode.Delay
        && state.Cooldowns.Brotherhood.ReadyIn <= ReadyLeeway
        && (_scenario.StrategyProfile.BrotherhoodStrategy == AutoForceDelayMode.Force || ScheduledBurstDue(time, _brotherhoodTimes));

    private bool ShouldUsePerfectBalance(BattleState state)
    {
        if (!UnlockedPerfectBalance || _scenario.StrategyProfile.PBStrategy == PBStrategyMode.Delay)
            return false;

        var gauge = state.PlayerGauge;
        if (gauge.PerfectBalanceLeft > 0 || gauge.BlitzLeft > 0 || gauge.BeastChakra.Count >= 3)
            return false;
        if (state.Cooldowns.PerfectBalance.Charges < 1 && state.Cooldowns.PerfectBalance.ReadyIn > ReadyLeeway)
            return false;
        if (_scenario.StrategyProfile.PBStrategy == PBStrategyMode.Force)
            return true;
        if (state.LookAwayActive || !state.HaveTarget || !state.Targetable || !state.CanMelee)
            return false;

        var request = PendingPbRequest(state.Time);
        if (request == null)
            return false;
        var doubleLunar = _scenario.StrategyProfile.NadiStrategy is NadiStrategyMode.Automatic or NadiStrategyMode.DoubleLunar;
        if (doubleLunar && !request.Even && gauge.LunarNadi && gauge.SolarNadi)
            return false;
        return true;
    }

    private bool ShouldUseRiddleOfEarth(BattleState state)
    {
        if (!UnlockedRiddleOfEarth
            || !state.InCombat
            || _scenario.StrategyProfile.RoEStrategy != AutoForceDelayMode.Automatic
            || state.Cooldowns.RiddleOfEarth.ReadyIn > ReadyLeeway
            || state.PlayerGauge.RiddleOfEarthLeft > 0
            || state.PlayerGauge.EarthsReplyLeft > 0)
            return false;

        var damage = state.PredictedDamage
            .Where(d => d.AppliesToSelf && d.Activation - state.Time is >= 0 and <= 10
                && d.Type is PredictedDamageKind.Raidwide or PredictedDamageKind.Shared or PredictedDamageKind.Tankbuster)
            .OrderBy(d => d.Activation)
            .FirstOrDefault();
        if (damage == null)
            return false;

        state.RiddleOfEarthReason = $"{damage.Type}@{damage.Activation:0.###}";
        return true;
    }

    private bool ShouldUseChakra(BattleState state)
    {
        if (!UnlockedChakra || !state.HaveTarget || !state.Targetable || state.LookAwayActive)
            return false;

        var gauge = state.PlayerGauge;
        if (gauge.Chakra < 5)
            return false;

        var nextCoreIn = Math.Min(
            Math.Min(NextDue(state.Time, _rofTimes), NextDue(state.Time, _brotherhoodTimes)),
            Math.Min(NextDue(state.Time, _potionTimes), NextDue(state.Time, _pbRequestTimes))) - state.Time;
        if (nextCoreIn >= 0 && nextCoreIn <= MnkPatch75Data.AnimationLock + ReadyLeeway)
            return false;
        if (_candidate == CandidateMode.ChakraOvercapOnlyOutsideBurst && gauge.BrotherhoodLeft <= 0 && gauge.Chakra < ChakraCap(gauge))
            return false;
        if (_candidate == CandidateMode.ChakraHoldForBrotherhood
            && gauge.BrotherhoodLeft <= 0
            && gauge.Chakra < ChakraCap(gauge)
            && NextDue(state.Time, _brotherhoodTimes) - state.Time is > 0 and <= 15)
            return false;
        return true;
    }

    private bool ShouldUseRiddleOfWind(BattleState state)
    {
        if (!UnlockedRiddleOfWind
            || _scenario.StrategyProfile.RoWStrategy == AutoForceDelayMode.Delay
            || state.Cooldowns.RiddleOfWind.ReadyIn > ReadyLeeway
            || !state.Targetable
            || !state.HaveTarget
            || state.LookAwayActive)
            return false;
        if (_scenario.StrategyProfile.RoWStrategy == AutoForceDelayMode.Force)
            return true;

        var burstActive = state.PlayerGauge.RiddleOfFireLeft > 0 || state.PlayerGauge.BrotherhoodLeft > 0;
        if (_candidate == CandidateMode.RiddleOfWindBurstOnly)
            return burstActive;
        if (state.CombatTimer < 15 || burstActive || Due(state.Time, _rofTimes) || Due(state.Time, _brotherhoodTimes))
            return true;

        var nextTwoMinute = NextDue(state.Time, _brotherhoodTimes);
        if (nextTwoMinute <= state.Time + Gcd + MnkPatch75Data.AnimationLock)
            return true;
        if (_candidate == CandidateMode.RiddleOfWindNoTwoMinuteHold)
            return true;
        if (state.CombatTimer >= 90
            && state.CombatTimer < 120
            && nextTwoMinute < double.MaxValue / 2
            && nextTwoMinute - state.Time <= 35)
        {
            var immediateUses = CountCooldownUses(state.Time, _scenario.DurationSeconds, RiddleOfWindCooldown);
            var delayedUses = CountCooldownUses(nextTwoMinute, _scenario.DurationSeconds, RiddleOfWindCooldown);
            return immediateUses > delayedUses;
        }
        return true;
    }

    private void QueueGcd(BattleState state)
        => UseGcd(state, SelectGcd(state));

    private string SelectGcd(BattleState state)
    {
        var gauge = state.PlayerGauge;
        if (!state.Targetable || !state.HaveTarget || state.LookAwayActive)
            return UnlockedMeditate ? "Meditate" : "FormShift";
        if (state.CanMelee && gauge.BlitzLeft > 0 && gauge.BeastChakra.Count >= 3 && ShouldUseBlitz(state))
            return ResolveBlitzAction(gauge);
        if (gauge.PerfectBalanceLeft > 0 && gauge.PerfectBalanceStacks > 0 && state.CanMelee)
            return NextPbGcd(gauge);

        var reply = SelectReply(state);
        if (reply != null)
            return reply;
        if (!state.CanMelee)
            return UnlockedFormShift ? "FormShift" : "Meditate";
        if (ShouldUseSixSidedStar(state))
            return "SixSidedStar";
        if (state.NumMeleeAOETargets >= 3)
            return NextAoeGcd(gauge);
        if (gauge.FormlessFistLeft > 0 || gauge.CurrentForm == "None")
            return OpoGcd(gauge);
        return NextComboGcd(gauge);
    }

    private string? SelectReply(BattleState state)
    {
        var gauge = state.PlayerGauge;
        if (gauge.PerfectBalanceLeft > 0 && gauge.PerfectBalanceStacks > 0)
            return null;
        if (Level >= 100 && gauge.FiresReplyLeft > 0
            && (!state.CanMelee || gauge.RiddleOfFireLeft > 0 || gauge.FiresReplyLeft <= Gcd * 2))
            return "FiresReply";
        if (Level >= 96 && gauge.WindsReplyLeft > 0
            && (!state.CanMelee || gauge.RiddleOfFireLeft > 0 || gauge.BrotherhoodLeft > 0 || gauge.WindsReplyLeft <= Gcd * 2))
            return "WindsReply";
        return null;
    }

    private string NextPbGcd(PlayerGauge gauge)
        => gauge.PerfectBalancePlan == "Lunar"
            ? OpoGcd(gauge)
            : gauge.BeastChakra.Count switch
            {
                0 => OpoGcd(gauge),
                1 => RaptorGcd(gauge),
                _ => CoeurlGcd(gauge)
            };

    private string NextComboGcd(PlayerGauge gauge)
        => gauge.CurrentForm switch
        {
            "Opo" => OpoGcd(gauge),
            "Raptor" => RaptorGcd(gauge),
            "Coeurl" => CoeurlGcd(gauge),
            _ => OpoGcd(gauge)
        };

    private string NextAoeGcd(PlayerGauge gauge)
        => gauge.CurrentForm switch
        {
            "Raptor" when Level >= 45 => "FourPointFury",
            "Coeurl" when Level >= 30 => "Rockbreaker",
            _ when Level >= 82 => "ShadowOfTheDestroyer",
            _ when Level >= 26 => "ArmOfTheDestroyer",
            _ => OpoGcd(gauge)
        };

    private string OpoGcd(PlayerGauge gauge)
        => Level >= 50 && gauge.OpoFury <= 0 ? "DragonKick" : Level >= 92 ? "LeapingOpo" : "Bootshine";

    private string RaptorGcd(PlayerGauge gauge)
        => Level >= 18 && gauge.RaptorFury <= 0 ? "TwinSnakes" : Level >= 92 ? "RisingRaptor" : "TrueStrike";

    private string CoeurlGcd(PlayerGauge gauge)
        => Level >= 30 && gauge.CoeurlFury <= 0 ? "Demolish" : Level >= 92 ? "PouncingCoeurl" : "SnapPunch";

    private string ResolveBlitzAction(PlayerGauge gauge)
    {
        if (gauge.LunarNadi && gauge.SolarNadi)
            return Level >= 90 ? "PhantomRush" : "TornadoKick";
        return gauge.BeastChakra.Distinct(StringComparer.Ordinal).Count() switch
        {
            <= 1 => Level >= 92 ? "ElixirBurst" : "ElixirField",
            2 => "CelestialRevolution",
            _ => Level >= 86 ? "RisingPhoenix" : "FlintStrike"
        };
    }

    private bool ShouldUseBlitz(BattleState state)
    {
        if (!UnlockedBlitz || _scenario.StrategyProfile.BlitzStrategy == BlitzStrategyMode.Delay)
            return false;

        var gauge = state.PlayerGauge;
        var action = ResolveBlitzAction(gauge);
        var bothNadi = gauge.LunarNadi && gauge.SolarNadi;
        var burstActive = gauge.RiddleOfFireLeft > 0 || gauge.BrotherhoodLeft > 0;
        var blitzExpiring = gauge.BlitzLeft <= Gcd + MnkPatch75Data.AnimationLock;
        var nextPbIn = NextDue(state.Time, _pbRequestTimes) - state.Time;
        var plannedPBContinuation = nextPbIn >= 0 && nextPbIn <= 2 * Gcd + MnkPatch75Data.AnimationLock;
        var pbOvercapUnlock = state.Cooldowns.PerfectBalance.Charges >= 2
            && state.ActionHistory.Any(a => a.Action == "PerfectBalance" && state.Time - a.Time <= MnkPatch75Data.PerfectBalanceDuration + MnkPatch75Data.BlitzDuration);
        var fightEndBurn = IsEndBurn(state);
        var nextBuffIn = NextBuffIn(state.Time);
        var canHoldForNextBuff = nextBuffIn > Gcd && nextBuffIn + Gcd < gauge.BlitzLeft;
        var holdAutomaticPhantomRush = MnkPatch75Data.IsPhantomRush(action)
            && bothNadi && !fightEndBurn && !blitzExpiring && !plannedPBContinuation && !pbOvercapUnlock && !burstActive && canHoldForNextBuff;

        return _scenario.StrategyProfile.BlitzStrategy switch
        {
            BlitzStrategyMode.Force => true,
            BlitzStrategyMode.RoF => fightEndBurn || gauge.RiddleOfFireLeft > 0 || !canHoldForNextBuff,
            BlitzStrategyMode.Multi => fightEndBurn || state.NumAOETargets > 1,
            BlitzStrategyMode.MultiRoF => fightEndBurn || state.NumAOETargets > 1 && (gauge.RiddleOfFireLeft > 0 || !canHoldForNextBuff),
            _ => _candidate switch
            {
                CandidateMode.PhantomRushNoBurstHold => true,
                CandidateMode.PhantomRushStrictBurstHold => !holdAutomaticPhantomRush && (burstActive || blitzExpiring || plannedPBContinuation || pbOvercapUnlock || fightEndBurn),
                _ => !holdAutomaticPhantomRush && (MnkPatch75Data.IsPhantomRush(action) || burstActive || blitzExpiring || plannedPBContinuation || pbOvercapUnlock || fightEndBurn)
            }
        };
    }

    private bool ShouldUseSixSidedStar(BattleState state)
    {
        if (!UnlockedSixSidedStar || !state.Targetable || !state.HaveTarget || !state.CanMelee || state.LookAwayActive)
            return false;
        var gauge = state.PlayerGauge;
        if (gauge.PerfectBalanceLeft > 0 || gauge.BlitzLeft > 0 || gauge.BeastChakra.Count > 0)
            return false;
        var nextLoss = NextMeleeLoss(state.Time);
        if (nextLoss.StartsIn <= 0 || nextLoss.StartsIn > Gcd + SixSidedStarApplicationDelay)
            return false;
        if (nextLoss.Kind is ScenarioEventType.TargetLost or ScenarioEventType.PhaseEnd or ScenarioEventType.FightEnd)
            return true;
        return _candidate != CandidateMode.SixSidedStarBeforeDowntime
            && nextLoss.Kind == ScenarioEventType.MeleeUnavailable
            && nextLoss.Duration >= Gcd;
    }

    private void UseGcd(BattleState state, string action)
    {
        var hardFails = ValidateAction(state, action, isGcd: true);
        var gauge = state.PlayerGauge;
        var guaranteedCrit = action is "Bootshine" or "LeapingOpo"
            && (gauge.CurrentForm == "Opo" || gauge.FormlessFistLeft > 0 || gauge.PerfectBalanceLeft > 0);
        state.LastGCDAction = action;
        state.GCDReadyIn = action == "SixSidedStar" ? 4.0 : Gcd;
        state.AnimationLock = MnkPatch75Data.WeaponskillAnimationLock;
        RecordAction(state, action, isGcd: true, hardFails, guaranteedCrit);
        state.HardFailCandidates.AddRange(hardFails);

        switch (action)
        {
            case "Meditate":
                AddChakra(gauge, 1);
                break;
            case "FormShift":
                gauge.FormlessFistLeft = 30;
                gauge.CurrentForm = "None";
                break;
            case "ElixirField":
            case "ElixirBurst":
            case "CelestialRevolution":
            case "FlintStrike":
            case "RisingPhoenix":
            case "TornadoKick":
            case "PhantomRush":
                ApplyBlitz(gauge, action);
                break;
            case "FiresReply":
                gauge.FiresReplyLeft = 0;
                gauge.FormlessFistLeft = 30;
                gauge.CurrentForm = "None";
                break;
            case "WindsReply":
                gauge.WindsReplyLeft = 0;
                break;
            case "SixSidedStar":
                gauge.Chakra = 0;
                gauge.ChakraProgress = 0;
                break;
            default:
                ApplyFormAction(gauge, action);
                break;
        }

        if (MnkPatch75Data.IsEnemyGcd(action) && !MnkPatch75Data.IsBlitz(action))
            AddWeaponskillChakra(gauge, guaranteedCrit);
    }

    private void ApplyFormAction(PlayerGauge gauge, string action)
    {
        var beast = action switch
        {
            "Bootshine" or "LeapingOpo" or "DragonKick" or "ArmOfTheDestroyer" or "ShadowOfTheDestroyer" => "Opo",
            "TrueStrike" or "RisingRaptor" or "TwinSnakes" or "FourPointFury" => "Raptor",
            "SnapPunch" or "PouncingCoeurl" or "Demolish" or "Rockbreaker" => "Coeurl",
            _ => ""
        };
        if (beast.Length == 0)
            return;

        switch (action)
        {
            case "DragonKick": gauge.OpoFury = 1; break;
            case "Bootshine":
            case "LeapingOpo": gauge.OpoFury = Math.Max(0, gauge.OpoFury - 1); break;
            case "TwinSnakes": gauge.RaptorFury = 1; break;
            case "TrueStrike":
            case "RisingRaptor": gauge.RaptorFury = Math.Max(0, gauge.RaptorFury - 1); break;
            case "Demolish": gauge.CoeurlFury = 2; break;
            case "SnapPunch":
            case "PouncingCoeurl": gauge.CoeurlFury = Math.Max(0, gauge.CoeurlFury - 1); break;
        }

        var underPerfectBalance = gauge.PerfectBalanceLeft > 0 && gauge.PerfectBalanceStacks > 0;
        if (underPerfectBalance)
        {
            gauge.PerfectBalanceStacks--;
            gauge.BeastChakra.Add(beast);
            if (gauge.BeastChakra.Count >= 3)
            {
                gauge.BlitzLeft = MnkPatch75Data.BlitzDuration;
                gauge.PerfectBalanceLeft = 0;
            }
        }
        else
        {
            gauge.CurrentForm = beast switch
            {
                "Opo" => "Raptor",
                "Raptor" => "Coeurl",
                _ => "Opo"
            };
        }
        if (gauge.FormlessFistLeft > 0)
            gauge.FormlessFistLeft = 0;
    }

    private static void ApplyBlitz(PlayerGauge gauge, string action)
    {
        gauge.BeastChakra.Clear();
        gauge.BlitzLeft = 0;
        gauge.PerfectBalanceLeft = 0;
        gauge.PerfectBalanceStacks = 0;
        gauge.FormlessFistLeft = 30;
        gauge.CurrentForm = "None";
        switch (action)
        {
            case "TornadoKick":
            case "PhantomRush":
                gauge.LunarNadi = false;
                gauge.SolarNadi = false;
                break;
            case "ElixirField":
            case "ElixirBurst":
                gauge.LunarNadi = true;
                break;
            case "FlintStrike":
            case "RisingPhoenix":
                gauge.SolarNadi = true;
                break;
            case "CelestialRevolution":
                if (!gauge.LunarNadi)
                    gauge.LunarNadi = true;
                else
                    gauge.SolarNadi = true;
                break;
        }
    }

    private bool UseOgcd(BattleState state, string action, bool enemyTargeted)
    {
        if (state.LastOGCDActions.Contains(action) || state.AnimationLock > ReadyLeeway)
            return false;
        var hardFails = ValidateAction(state, action, isGcd: false, enemyTargeted);
        state.LastOGCDActions.Add(action);
        state.AnimationLock = MnkPatch75Data.AnimationLock;
        RecordAction(state, action, isGcd: false, hardFails);
        state.HardFailCandidates.AddRange(hardFails);

        var gauge = state.PlayerGauge;
        switch (action)
        {
            case "PerfectBalance":
                gauge.PerfectBalanceLeft = MnkPatch75Data.PerfectBalanceDuration;
                gauge.PerfectBalanceStacks = 3;
                gauge.PerfectBalancePlan = DeterminePerfectBalancePlan(state);
                SpendCharge(state.Cooldowns.PerfectBalance, PBCooldown);
                break;
            case "RiddleOfFire":
                gauge.RiddleOfFireLeft = MnkPatch75Data.RiddleOfFireDuration;
                if (Level >= 100)
                    gauge.FiresReplyLeft = MnkPatch75Data.FiresReplyDuration;
                state.Cooldowns.RiddleOfFire.ReadyIn = RofCooldown;
                break;
            case "Brotherhood":
                gauge.BrotherhoodLeft = MnkPatch75Data.BrotherhoodDuration;
                state.Cooldowns.Brotherhood.ReadyIn = BrotherhoodCooldown;
                break;
            case "Potion":
                gauge.PotionLeft = MnkPatch75Data.PotionDuration;
                state.Cooldowns.Potion.ReadyIn = PotionCooldown;
                break;
            case "RiddleOfWind":
                gauge.RiddleOfWindLeft = MnkPatch75Data.RiddleOfWindDuration;
                if (Level >= 96)
                    gauge.WindsReplyLeft = MnkPatch75Data.WindsReplyDuration;
                state.Cooldowns.RiddleOfWind.ReadyIn = RiddleOfWindCooldown;
                break;
            case "RiddleOfEarth":
                gauge.RiddleOfEarthLeft = RiddleOfEarthDuration;
                gauge.EarthsReplyLeft = EarthsReplyDuration;
                state.Cooldowns.RiddleOfEarth.ReadyIn = RiddleOfEarthCooldown;
                break;
            case "Thunderclap":
                SpendCharge(state.Cooldowns.Thunderclap, 30);
                break;
            case "SteelPeak":
            case "ForbiddenChakra":
            case "HowlingFist":
            case "Enlightenment":
                gauge.Chakra = Math.Max(0, gauge.Chakra - 5);
                break;
        }
        return true;
    }

    private string DeterminePerfectBalancePlan(BattleState state)
    {
        var gauge = state.PlayerGauge;
        var request = PendingPbRequest(state.Time);
        var even = request?.Even ?? gauge.BrotherhoodLeft > 0;
        var useIndex = request?.UseIndex ?? 0;
        var mode = _scenario.StrategyProfile.NadiStrategy == NadiStrategyMode.Automatic
            ? NadiStrategyMode.DoubleLunar
            : _scenario.StrategyProfile.NadiStrategy;
        if (gauge.LunarNadi && gauge.SolarNadi)
            return "Lunar";
        return mode switch
        {
            NadiStrategyMode.Lunar => "Lunar",
            NadiStrategyMode.Solar => "Solar",
            NadiStrategyMode.LunarSolar => !gauge.LunarNadi ? "Lunar" : "Solar",
            NadiStrategyMode.DoubleLunar when even && gauge.LunarNadi && !gauge.SolarNadi => "Solar",
            NadiStrategyMode.DoubleLunar when even && gauge.SolarNadi && !gauge.LunarNadi => "Lunar",
            NadiStrategyMode.DoubleLunar when even && !gauge.LunarNadi && !gauge.SolarNadi => useIndex == 0 ? "Lunar" : "Solar",
            NadiStrategyMode.DoubleLunar => !gauge.LunarNadi ? "Lunar" : "Solar",
            _ => !gauge.LunarNadi ? "Lunar" : "Solar"
        };
    }

    private void AddWeaponskillChakra(PlayerGauge gauge, bool guaranteedCrit)
    {
        if (Level < 38)
            return;
        if (Level >= 88 && gauge.BrotherhoodLeft > 0)
        {
            AddChakra(gauge, 1);
            return;
        }
        var meditationChance = Level >= 74 ? 1.0 : 0.8;
        AddChakra(gauge, (guaranteedCrit ? 1.0 : RepresentativeCriticalHitRate) * meditationChance);
    }

    private void AddChakra(PlayerGauge gauge, double amount)
    {
        if (amount <= 0)
            return;
        gauge.ChakraProgress += amount;
        var cap = ChakraCap(gauge);
        while (gauge.ChakraProgress >= 1 && gauge.Chakra < cap)
        {
            gauge.Chakra++;
            gauge.ChakraProgress -= 1;
        }
        if (gauge.Chakra >= cap)
            gauge.ChakraProgress = 0;
    }

    private void RecordAction(BattleState state, string action, bool isGcd, IReadOnlyList<HardFailCode> hardFails, bool guaranteedCrit = false)
    {
        var gauge = state.PlayerGauge;
        var basePotency = MnkPatch75Data.PotencyEquivalent(action, gauge, state.NumAOETargets, Level, guaranteedCrit);
        var underRof = gauge.RiddleOfFireLeft > 0;
        var underBrotherhood = gauge.BrotherhoodLeft > 0;
        var underPotion = gauge.PotionLeft > 0;
        var effectivePotency = basePotency;
        if (underRof)
            effectivePotency *= 1.15;
        if (underBrotherhood)
            effectivePotency *= 1.05;
        if (underPotion)
            effectivePotency *= MnkPatch75Data.PotionPotencyMultiplier;
        state.ActionHistory.Add(new(
            Math.Round(state.Time, 3), action, isGcd, hardFails,
            Math.Round(basePotency, 3), Math.Round(effectivePotency, 3), state.NumAOETargets,
            underRof, underBrotherhood, underPotion));
    }

    private IReadOnlyList<HardFailCode> ValidateAction(BattleState state, string action, bool isGcd, bool enemyTargeted = true)
    {
        List<HardFailCode> failures = [];
        if (EnemyTargeted(action, isGcd, enemyTargeted))
        {
            if (!state.Targetable)
                failures.Add(HardFailCode.TargetableEnemyAction);
            if (!state.HaveTarget)
                failures.Add(HardFailCode.HaveTargetEnemyAction);
            if (state.LookAwayActive)
                failures.Add(isGcd ? HardFailCode.LookAwayEnemyGcd : HardFailCode.LookAwayEnemyOgcd);
        }
        if (isGcd && MnkPatch75Data.IsMeleeGcd(action) && !state.CanMelee)
            failures.Add(HardFailCode.MeleeGcdOutOfRange);
        if (action == "Thunderclap" && !state.ThunderclapSafe)
            failures.Add(HardFailCode.UnsafeThunderclap);
        if (action == "SixSidedStar" && (state.PlayerGauge.PerfectBalanceLeft > 0 || state.PlayerGauge.BlitzLeft > 0))
            failures.Add(HardFailCode.SixSidedStarOverPbOrBlitz);
        if (!MnkPatch75Data.IsUnlocked(action, Level))
            failures.Add(HardFailCode.UnlearnedAction);
        return failures;
    }

    private static bool EnemyTargeted(string action, bool isGcd, bool enemyTargeted)
        => isGcd ? MnkPatch75Data.IsEnemyGcd(action) : enemyTargeted;

    private bool CanUseAutomaticBurst(BattleState state)
        => _scenario.StrategyProfile.RotationMode == RotationMode.Full
        && state.InCombat && state.Targetable && state.HaveTarget && !state.LookAwayActive
        && state.EncounterHint is not (EncounterHintMode.Trash or EncounterHintMode.BossReturn or EncounterHintMode.HoldBurst);

    private bool IsEndBurn(BattleState state)
    {
        var horizon = new[] { state.EstimatedFightEnd, state.EstimatedPhaseEnd, state.EstimatedDowntimeStart }
            .Where(v => v.HasValue)
            .Select(v => v!.Value - state.Time)
            .DefaultIfEmpty(double.MaxValue)
            .Min();
        return horizon <= Gcd + MnkPatch75Data.AnimationLock;
    }

    private bool UnlockedPerfectBalance => Level >= 50;
    private bool UnlockedBlitz => Level >= 60;
    private bool UnlockedRiddleOfEarth => Level >= 64;
    private bool UnlockedRiddleOfFire => Level >= 68;
    private bool UnlockedBrotherhood => Level >= 70;
    private bool UnlockedRiddleOfWind => Level >= 72;
    private bool UnlockedSixSidedStar => Level >= 80;
    private bool UnlockedThunderclap => Level >= 35;
    private bool UnlockedChakra => Level >= 15;
    private bool UnlockedMeditate => Level >= 15;
    private bool UnlockedFormShift => Level >= 52;

    private static void TickCooldown(CooldownState cooldown, double delta, double recharge = 0, double maxCharges = 1)
    {
        if (recharge <= 0)
        {
            cooldown.ReadyIn = Math.Max(0, cooldown.ReadyIn - delta);
            return;
        }
        if (cooldown.Charges >= maxCharges)
        {
            cooldown.Charges = maxCharges;
            cooldown.ReadyIn = 0;
            return;
        }
        cooldown.ReadyIn -= delta;
        while (cooldown.ReadyIn <= 0 && cooldown.Charges < maxCharges)
        {
            cooldown.Charges++;
            cooldown.ReadyIn += recharge;
        }
        if (cooldown.Charges >= maxCharges)
            cooldown.ReadyIn = 0;
    }

    private static void SpendCharge(CooldownState cooldown, double recharge)
    {
        if (cooldown.Charges <= 0)
            return;
        cooldown.Charges--;
        if (cooldown.ReadyIn <= 0)
            cooldown.ReadyIn = recharge;
    }

    private static IEnumerable<double> BurstTimes(double first, double interval, double duration)
    {
        for (var t = first; t <= duration + 0.001; t += interval)
            yield return Math.Round(t, 3);
    }

    private static List<PbRequest> BuildPerfectBalanceRequests(ScenarioDefinition scenario, IReadOnlyList<double> rofTimes, IReadOnlyList<double> brotherhoodTimes, double gcd)
    {
        List<PbRequest> requests = [];
        foreach (var rof in rofTimes)
        {
            var even = brotherhoodTimes.Any(b => Math.Abs(b - rof) <= 6 || rof >= b - 3 && rof <= b + MnkPatch75Data.BrotherhoodDuration);
            if (scenario.OpenerRoFOffset == OpenerRoFOffsetMode.ZeroSecondBurst && rof == 0)
            {
                requests.Add(new(0, true, 0));
                requests.Add(new(gcd * 4 + MnkPatch75Data.AnimationLock, true, 1));
            }
            else if (even)
            {
                requests.Add(new(Math.Max(0, rof - gcd), true, 0));
                requests.Add(new(rof + gcd * 4 + MnkPatch75Data.AnimationLock, true, 1));
            }
            else
            {
                requests.Add(new(rof + gcd, false, 0));
            }
        }
        return requests
            .Where(r => r.Time <= scenario.DurationSeconds)
            .Select(r => r with { Time = Math.Round(r.Time, 3) })
            .OrderBy(r => r.Time)
            .ToList();
    }

    private static bool Due(double time, IReadOnlyList<double> times)
        => times.Any(t => time >= t - ReadyLeeway && time <= t + 3.0);

    private static bool ScheduledBurstDue(double time, IReadOnlyList<double> times)
        => times.Any(t => time >= t - ReadyLeeway && time <= t + 10.0);

    private PbRequest? PendingPbRequest(double time)
        => _pbRequests
            .Where(r => time >= r.Time - ReadyLeeway && time <= r.Time + 3)
            .OrderBy(r => Math.Abs(r.Time - time))
            .FirstOrDefault();

    private static int CountCooldownUses(double firstUseAt, double horizon, double cooldown)
        => firstUseAt <= horizon ? 1 + (int)Math.Floor((horizon - firstUseAt) / cooldown) : 0;

    private double NextBuffIn(double time)
    {
        var next = Math.Min(NextDue(time, _rofTimes), NextDue(time, _brotherhoodTimes));
        return next >= double.MaxValue / 2 ? double.MaxValue : next - time;
    }

    private (ScenarioEventType Kind, double StartsIn, double Duration) NextMeleeLoss(double time)
    {
        var bestKind = ScenarioEventType.MeleeUnavailable;
        var bestStart = double.MaxValue;
        var bestDuration = 0.0;
        foreach (var ev in _scenario.Source.EventList)
        {
            if (ev.Type is not (ScenarioEventType.TargetLost or ScenarioEventType.MeleeUnavailable or ScenarioEventType.PhaseEnd or ScenarioEventType.FightEnd)
                || ev.Start < time - ReadyLeeway || ev.Start >= bestStart)
                continue;
            bestKind = ev.Type;
            bestStart = ev.Start;
            bestDuration = ev.Type == ScenarioEventType.FightEnd
                ? Math.Max(0, _scenario.DurationSeconds - ev.Start)
                : Math.Max(0, ev.End - ev.Start);
        }
        return (bestKind, bestStart >= double.MaxValue / 2 ? double.MaxValue : bestStart - time, bestDuration);
    }

    private static double NextDue(double time, IReadOnlyList<double> times)
        => times.FirstOrDefault(t => t >= time - ReadyLeeway, double.MaxValue);

    private int ChakraCap(PlayerGauge gauge)
        => Level >= 88 && gauge.BrotherhoodLeft > 0 ? 10 : 5;

    private sealed record PbRequest(double Time, bool Even, int UseIndex);
}
