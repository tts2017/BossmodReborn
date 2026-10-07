using BossMod.MNK;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.xan.Custom;

public sealed class MNK(RotationModuleManager manager, Actor player) : Attackxan<AID, TraitID, MNK.Strategy>(manager, player, PotionType.Strength)
{

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("xan MNK [Custom]", "Monk", "Standard rotation (xan)|Melee", "xan", RotationModuleQuality.Good, BitMask.Build(Class.MNK, Class.PGL), 100).WithStrategies<Strategy>();
    }
    public (float Left, int Stacks) PerfectBalance;

    protected override float GetCastTime(AID aid) => 0;

    public int AOEBreakpoint
    {
        get
        {
            if (BossReturnSingleTargetPrep || !Unlocked(AID.ArmOfTheDestroyer))
                return int.MaxValue;

            if (TransitionToSingleSoon())
                return FullAOEBreakpoint;

            return EffectiveForm switch
            {
                Form.OpoOpo when Unlocked(AID.ShadowOfTheDestroyer) && OpoStacks == 0 => ThreeTargetAOEBreakpoint,
                Form.Coeurl when Unlocked(AID.Rockbreaker) && CoeurlStacks == 0 && AOEShouldLastSeveralGCDs() => ThreeTargetAOEBreakpoint,
                _ => FullAOEBreakpoint
            };
        }
    }

    private (Positional, bool) NextPositional
    {
        get
        {
            if (UseAOE || !Unlocked(AID.SnapPunch))
                return (Positional.Any, false);

            var pos = Unlocked(AID.Demolish) && CoeurlStacks == 0 ? Positional.Rear : Positional.Flank;
            var imm = NextGCD is AID.Demolish or AID.SnapPunch or AID.PouncingCoeurl;

            return (pos, imm);
        }
    }

    private new float GetApplicationDelay(AID action) => action switch
    {
        AID.SixSidedStar => SixSidedStarApplicationDelay,
        AID.DragonKick => DragonKickApplicationDelay,
        AID.ForbiddenChakra => ForbiddenChakraApplicationDelay,
        AID.Demolish => DemolishApplicationDelay,
        // add more if needed; everything else (Bootshine, Leaping Opo, ...) uses the shared application delay table
        _ => base.GetApplicationDelay(action)
    };

    private AID ChakraSingleTargetAction => Unlocked(AID.ForbiddenChakra) ? AID.ForbiddenChakra : AID.SteelPeak;
    private AID ChakraLineAction => Unlocked(AID.Enlightenment) ? AID.Enlightenment : AID.HowlingFist;
    private bool ShouldUseChakraLineAction()
    {
        if (!Unlocked(AID.HowlingFist))
            return false;

        if (Unlocked(AID.Enlightenment))
            return NumLineTargets >= 3;

        if (Unlocked(AID.ForbiddenChakra))
            return NumLineTargets >= 5;

        return NumLineTargets >= 2;
    }

    public override string DescribeState() => $"F={BuffedGCDsLeft}, PB={PBGCDsLeft}";
    private bool BurstHorizonIsPhaseEnd
        => EstimatedBurstHorizon is float burstHorizon
        && EstimatedFightEnd is float fightEnd
        && burstHorizon < fightEnd - AnimationLockDelay;
    private bool IgnoreDancingMadP1MechanicActionHold
        => CurrentTuningContentId == 1094
        && CombatTimer < DancingMadP1P2DowntimeStart;

    private bool ShouldConsumeFormlessBeforeOddAutomaticPB(in Strategy strategy, in BurstPlan burst)
    {
        if (FormShiftLeft <= GCD || CurrentForm != Form.None)
            return false;

        if (strategy.PB.Value != PBStrategy.Automatic
            || PerfectBalanceLeft > GCD
            || BeastGaugeBlocksPB
            || ShouldBlockPBForPendingReply(strategy))
            return false;

        if (burst.EvenWindow
            || burst.AutomaticPBTargetUses != 1
            || burst.AutomaticPBUsesInBurst > 0
            || burst.UsePBForNadiRepairNow)
            return false;

        if (!burst.UsePBAutomaticNow && !burst.PBPreferred)
            return false;

        if (burst.PBDeadline || burst.LastPBWindow)
            return false;

        if (MaxChargesIn(AID.PerfectBalance) <= PBNearOvercapWindow)
            return false;

        if (PhaseEndResourceBurnWindow(strategy, AttackGCDLength * 5 + AnimationLockDelay))
            return false;

        if (!CanFitGCD(EffectiveMeleeBurstUptimeIn, 5))
            return false;

        var effectiveFireLeft = ResourceHorizonClamp(strategy, FireLeft);
        if (effectiveFireLeft > GCD && !CanFitGCD(effectiveFireLeft, 5))
            return false;

        var effectiveBrotherhoodLeft = ResourceHorizonClamp(strategy, BrotherhoodLeft);
        if (effectiveBrotherhoodLeft > GCD && !CanFitGCD(effectiveBrotherhoodLeft, 5))
            return false;

        if (strategy.OpenerRoFOffset.Value == OpenerRoFOffsetStrategy.ZeroSecondBurst
            && Player.InCombat
            && CombatTimer <= ZeroSecondBurstOpenerWindow
            && _activatedRoFBursts == 0)
            return false;

        if (TryNextEvenBurstIn(CurrentRoFBurstIndex, burst.NextRoFIn, out var nextEvenBurstIn)
            && EstimatedPBMaxChargesInAfterSpendingOne() + AttackGCDLength > nextEvenBurstIn + PBUseSpacing + AnimationLockDelay)
            return false;

        return true;
    }

    private static float OddPBGCDValue(int index)
        => index switch
        {
            0 => 1.0f,
            1 => 1.2f,
            _ => 1.4f
        };

    private bool OddDelayedPBGCDIsLeapingOpo(int index)
        => Unlocked(AID.LeapingOpo)
        && OpoStacks > 0
        && CurrentForm switch
        {
            Form.OpoOpo => index == 0,
            Form.None when FormShiftLeft > GCD => index == 0,
            Form.Coeurl => index == 1,
            _ => false
        };

    private float EvaluateOddPBWindowValue(float startDelay, int delayedGCDsBeforePB, float fireLeft, float brotherhoodLeft)
    {
        var value = 0f;
        for (var i = 0; i < delayedGCDsBeforePB; ++i)
        {
            if (!OddDelayedPBGCDIsLeapingOpo(i))
                continue;

            var hitAt = AttackGCDLength * (i + 1);
            if (hitAt <= fireLeft)
                value += 1.3f;
            if (hitAt <= brotherhoodLeft)
                value += 0.7f;
        }

        for (var i = 0; i < 3; ++i)
        {
            var hitAt = startDelay + AttackGCDLength * (i + 1);
            var gcdValue = OddPBGCDValue(i);
            if (hitAt <= fireLeft)
                value += gcdValue * 1.5f;
            if (hitAt <= brotherhoodLeft)
                value += gcdValue * 0.5f;
        }

        var blitzAt = startDelay + AttackGCDLength * 4;
        var blitzValue = HaveBothNadi ? 5f : 3.5f;
        if (blitzAt <= fireLeft)
            value += blitzValue * 1.5f;
        if (blitzAt <= brotherhoodLeft)
            value += blitzValue * 0.5f;

        return value;
    }

    private static float EvenPBGCDValue(int index)
        => index switch
        {
            0 => 1.2f,
            1 => 1.35f,
            _ => 1.5f
        };

    private bool EvenDelayedPBGCDIsLeapingOpo(int index)
        => Unlocked(AID.LeapingOpo)
        && OpoStacks > 0
        && CurrentForm switch
        {
            Form.OpoOpo => index == 0,
            Form.None when FormShiftLeft > GCD => index == 0,
            Form.Coeurl => index == 1,
            _ => false
        };

    private float EvaluateEvenPBWindowValue(float startDelay, int delayedGCDsBeforePB, float fireLeft, float brotherhoodLeft, bool firstPBInEvenBurst)
    {
        var value = 0f;

        for (var i = 0; i < delayedGCDsBeforePB; ++i)
        {
            if (!EvenDelayedPBGCDIsLeapingOpo(i))
                continue;

            var hitAt = AttackGCDLength * (i + 1);
            if (hitAt <= fireLeft)
                value += 2.2f;
            if (hitAt <= brotherhoodLeft)
                value += 1.0f;
        }

        for (var i = 0; i < 3; ++i)
        {
            var hitAt = startDelay + AttackGCDLength * (i + 1);
            var gcdValue = EvenPBGCDValue(i);
            if (hitAt <= fireLeft)
                value += gcdValue * 2.0f;
            if (hitAt <= brotherhoodLeft)
                value += gcdValue * 1.0f;
        }

        var blitzAt = startDelay + AttackGCDLength * 4;
        var blitzValue = HaveBothNadi ? 12f : firstPBInEvenBurst ? 7f : 9f;
        if (blitzAt <= fireLeft)
            value += blitzValue * 2.0f;
        if (blitzAt <= brotherhoodLeft)
            value += blitzValue * 1.0f;

        return value;
    }

    private bool ShouldDelayEvenAutomaticPBForBurstValue(in Strategy strategy, in BurstPlan burst)
    {
        if (strategy.PB.Value != PBStrategy.Automatic
            || PerfectBalanceLeft > GCD
            || BeastGaugeBlocksPB
            || ShouldBlockPBForPendingReply(strategy))
            return false;

        if (!burst.EvenWindow
            || burst.AutomaticPBTargetUses != 2
            || burst.AutomaticPBUsesInBurst >= 2)
            return false;

        if (!burst.UsePBAutomaticNow && !burst.PBPreferred && !burst.UsePBForNadiRepairNow)
            return false;

        if (burst.PBDeadline || burst.LastPBWindow)
            return false;

        if (MaxChargesIn(AID.PerfectBalance) <= PBNearOvercapWindow)
            return false;

        if (PhaseEndResourceBurnWindow(strategy, AttackGCDLength * 8 + AnimationLockDelay))
            return false;

        var gcdsBeforePB = CurrentForm switch
        {
            Form.OpoOpo => 1,
            Form.Coeurl => 2,
            Form.None when FormShiftLeft > GCD => 1,
            _ => 0
        };
        if (gcdsBeforePB <= 0)
            return false;

        var remainingPBUses = Math.Max(1, burst.AutomaticPBTargetUses - burst.AutomaticPBUsesInBurst);
        var requiredGCDs = gcdsBeforePB + 4 * remainingPBUses;
        if (!CanFitGCD(EffectiveMeleeBurstUptimeIn, requiredGCDs))
            return false;

        var effectiveFireLeft = ResourceHorizonClamp(strategy, FireLeft);
        if (effectiveFireLeft > GCD && !CanFitGCD(effectiveFireLeft, requiredGCDs))
            return false;

        var effectiveBrotherhoodLeft = ResourceHorizonClamp(strategy, BrotherhoodLeft);
        if (effectiveBrotherhoodLeft > GCD && !CanFitGCD(effectiveBrotherhoodLeft, requiredGCDs))
            return false;

        var firstPBInEvenBurst = burst.AutomaticPBUsesInBurst == 0;
        var delayedStart = AttackGCDLength * gcdsBeforePB;

        return EvaluateEvenPBWindowValue(delayedStart, gcdsBeforePB, effectiveFireLeft, effectiveBrotherhoodLeft, firstPBInEvenBurst)
            > EvaluateEvenPBWindowValue(0, 0, effectiveFireLeft, effectiveBrotherhoodLeft, firstPBInEvenBurst);
    }

    private bool ShouldDelayOddAutomaticPBUntilOpoGCD(in Strategy strategy, in BurstPlan burst)
    {
        if (strategy.PB.Value != PBStrategy.Automatic
            || PerfectBalanceLeft > GCD
            || BeastGaugeBlocksPB
            || ShouldBlockPBForPendingReply(strategy))
            return false;

        // nadi repair PB has its own timing; never hold it for an Opo GCD
        if (burst.UsePBForNadiRepairNow)
            return false;

        if (burst.EvenWindow
            || burst.AutomaticPBTargetUses == 2
            || burst.AutomaticPBUsesInBurst > 0)
            return false;

        if (!burst.UsePBAutomaticNow && !burst.PBPreferred && !burst.UsePBForNadiRepairNow)
            return false;

        if (burst.LastPBWindow)
            return false;

        if (MaxChargesIn(AID.PerfectBalance) <= PBNearOvercapWindow)
            return false;

        if (PhaseEndResourceBurnWindow(strategy, AttackGCDLength * 5 + AnimationLockDelay))
            return false;

        if (strategy.OpenerRoFOffset.Value == OpenerRoFOffsetStrategy.ZeroSecondBurst
            && Player.InCombat
            && CombatTimer <= ZeroSecondBurstOpenerWindow
            && _activatedRoFBursts == 0)
            return false;

        if (FireLeft <= GCD)
            return true;

        var gcdsBeforePB = CurrentForm switch
        {
            Form.OpoOpo => 1,
            Form.Coeurl => 2,
            Form.None when FormShiftLeft > GCD => 1,
            _ => 0
        };
        if (gcdsBeforePB <= 0)
            return false;

        var requiredGCDs = gcdsBeforePB + 4;
        if (!CanFitGCD(EffectiveMeleeBurstUptimeIn, requiredGCDs))
            return false;

        var effectiveFireLeft = ResourceHorizonClamp(strategy, FireLeft);
        if (effectiveFireLeft > GCD && !CanFitGCD(effectiveFireLeft, requiredGCDs))
            return false;

        var effectiveBrotherhoodLeft = ResourceHorizonClamp(strategy, BrotherhoodLeft);
        if (effectiveBrotherhoodLeft > GCD && !CanFitGCD(effectiveBrotherhoodLeft, requiredGCDs))
            return false;

        if (TryNextEvenBurstIn(CurrentRoFBurstIndex, burst.NextRoFIn, out var nextEvenBurstIn)
            && EstimatedPBMaxChargesInAfterSpendingOne() + AttackGCDLength * gcdsBeforePB > nextEvenBurstIn + PBUseSpacing + AnimationLockDelay)
            return false;

        var delayedStart = AttackGCDLength * gcdsBeforePB;
        return EvaluateOddPBWindowValue(delayedStart, gcdsBeforePB, effectiveFireLeft, effectiveBrotherhoodLeft)
            > EvaluateOddPBWindowValue(0, 0, effectiveFireLeft, effectiveBrotherhoodLeft);
    }

    private bool ShouldUseDowntimeFormShiftForLeapingOpo(in Strategy strategy, in BurstPlan burst, Enemy? rangedTarget, Enemy? meleeTarget)
    {
        if (strategy.FormShift.Value != OffensiveStrategy.Automatic)
            return false;

        if (!Player.InCombat
            || !HaveTarget
            || HaveMeleeTarget
            || rangedTarget == null
            || meleeTarget != null)
            return false;

        if (UseAOE
            || !Unlocked(AID.FormShift)
            || !Unlocked(AID.LeapingOpo)
            || OpoStacks <= 0
            || FormShiftLeft > GCD)
            return false;

        if (PerfectBalanceLeft > GCD
            || BlitzLeft > GCD
            || BeastGaugeBlocksPB
            || FireLeft > GCD
            || BrotherhoodLeft > GCD
            || WindsReplyLeft > GCD
            || FiresReplyLeft > GCD
            || EarthsReplyLeft > GCD
            || ShouldBlockPBForPendingReply(strategy))
            return false;

        if (burst.EvenWindow
            || burst.UseRoFNow
            || burst.UseBrotherhoodNow
            || burst.UsePBAutomaticNow
            || burst.PBPreferred
            || burst.UsePBForNadiRepairNow
            || burst.PBDeadline
            || burst.LastPBWindow)
            return false;

        if (MaxChargesIn(AID.PerfectBalance) <= PBNearOvercapWindow)
            return false;

        if (strategy.OpenerRoFOffset.Value == OpenerRoFOffsetStrategy.ZeroSecondBurst
            && CombatTimer <= ZeroSecondBurstOpenerWindow
            && _activatedRoFBursts == 0)
            return false;

        if (burst.NextRoFIn <= AttackGCDLength * 2 + AnimationLockDelay)
            return false;

        if (UptimeIn is float uptimeIn
            && uptimeIn > 0
            && uptimeIn <= GCD + AnimationLockDelay)
            return false;

        if (_meleePrediction.MeleeResumeIn > 0
            && _meleePrediction.MeleeResumeIn <= GCD + AnimationLockDelay)
            return false;

        if (strategy.TC.Value == TCStrategy.GapClose
            && Unlocked(AID.Thunderclap)
            && Player.DistanceToHitbox(rangedTarget) is > MeleeRange and <= GapCloseMaxRange
            && CanThunderclapSafely(rangedTarget, Math.Max(GCD, AnimationLockDelay)))
            return false;

        return true;
    }

    private void DowntimeFormShiftForLeapingOpo(in Strategy strategy, in BurstPlan burst, Enemy? rangedTarget, Enemy? meleeTarget)
    {
        if (ShouldUseDowntimeFormShiftForLeapingOpo(strategy, burst, rangedTarget, meleeTarget))
            PushGCD(AID.FormShift, Player, GCDPriority.Basic);
    }

    private static bool IsActionableEnemy(Enemy? target)
        => target != null
        && target.Priority is not Enemy.PriorityInvincible and not Enemy.PriorityForbidden and not Enemy.PriorityPointless;

    // automatic acquisition must additionally skip PriorityUndesirable (enemies out of combat, or in combat with another
    // party) - the framework treats those as forbidden targets, they are only legal when the player picks them by hand.
    // PotentialTargets stays sorted by descending priority, so the first match is also the best one.
    private Enemy? ActionableTargetInRange(float range)
    {
        var targets = Hints.PotentialTargets;
        var count = targets.Count;
        for (var i = 0; i < count; ++i)
        {
            var target = targets[i];
            if (target.Priority > Enemy.PriorityUndesirable && IsActionableEnemy(target) && Player.DistanceToHitbox(target) <= range)
                return target;
        }

        return null;
    }

    // Per-category action locks (ActionLocks): Pacification refuses weaponskills, Silence spells, Amnesia abilities, a stun-type status
    // everything. The client refuses after the queue has picked, so a locked candidate at the top of the queue is resubmitted every
    // frame while what is below it never runs. Every push goes through PushAction -> CanUse, so filtering there keeps the queue to
    // what the client accepts and the rotation falls back on its own (weaponskills under Amnesia, abilities under Pacification).
    protected override bool CanUse(AID action) => !IsActionLocked(action) && !DelaysWeaponskillResume(action);

    public override void Exec(in Strategy strategy, Enemy? primaryTarget)
    {
        _useMechanicAIHints = !DancingMadTopLogProfileEnabled(strategy);
        UpdateMechanicForecast(strategy.MechanicHints.Value);
        ExecCore(strategy, primaryTarget);
        // captured on every path (including the countdown early-return) so PendingGCDForReplyCheck has a value
        _previousNextGCD = NextGCD;
    }

    private void ExecCore(in Strategy strategy, Enemy? primaryTarget)
    {
        var rangedTarget = primaryTarget;
        SelectPrimaryTarget(strategy, ref rangedTarget, range: TargetAcquisitionRange);
        if (!IsActionableEnemy(rangedTarget))
            rangedTarget = ActionableTargetInRange(TargetAcquisitionRange);
        if (Player.DistanceToHitbox(rangedTarget) > TargetAcquisitionRange)
            rangedTarget = null;
        HaveTarget = rangedTarget != null && Player.InCombat;

        var meleeTarget = rangedTarget;
        SelectPrimaryTarget(strategy, ref meleeTarget, range: MeleeRange);
        if (!IsActionableEnemy(meleeTarget) || Player.DistanceToHitbox(meleeTarget) > MeleeRange)
            meleeTarget = null;
        HaveMeleeTarget = meleeTarget != null && Player.InCombat;
        UpdateClosingOnTarget(rangedTarget?.Actor);

        UpdateFightEstimate(rangedTarget);

        var gauge = World.Client.GetGauge<MonkGauge>();

        Chakra = gauge.Chakra;
        // the gauge's BeastChakra property builds a new array on every read: copy the three slots into our buffer instead
        BeastChakra[0] = gauge.BeastChakra1;
        BeastChakra[1] = gauge.BeastChakra2;
        BeastChakra[2] = gauge.BeastChakra3;
        _beastCount = (BeastChakra[0] != BeastChakraType.None ? 1 : 0) + (BeastChakra[1] != BeastChakraType.None ? 1 : 0) + (BeastChakra[2] != BeastChakraType.None ? 1 : 0);
        BlitzLeft = gauge.BlitzTimeRemaining / 1000f;
        Nadi = gauge.Nadi;

        OpoStacks = gauge.OpoOpoStacks;
        RaptorStacks = gauge.RaptorStacks;
        CoeurlStacks = gauge.CoeurlStacks;

        PerfectBalance = Status(SID.PerfectBalance);
        FormShiftLeft = StatusLeft(SID.FormlessFist);
        FireLeft = StatusLeft(SID.RiddleOfFire);
        EarthLeft = StatusLeft(SID.RiddleOfEarth);
        WindsReplyLeft = StatusLeft(SID.WindsRumination);
        FiresReplyLeft = StatusLeft(SID.FiresRumination);
        EarthsReplyLeft = StatusLeft(SID.EarthsRumination);
        BrotherhoodLeft = StatusLeft(SID.Brotherhood);
        OwnBrotherhoodLeft = StatusDetails(Player, SID.Brotherhood, Player.InstanceID).Left;
        _hasMechanicHint = false;
        if (UseMechanicAIHints())
        {
            _hasMechanicHint = Mechanic.HasSnapshot;
            _mechanicHint = Mechanic.Snapshot;
        }
        _meleePrediction = GetCachedMeleeUptimePrediction(strategy, rangedTarget?.Actor);
        // NumMeleeAOETargets intentionally sees the raw configured hint before ResolveEncounterHint consumes NumAOETargets.
        _encounterHint = strategy.EncounterHint.Value;
        (var currentBlitz, var currentBlitzIsTargeted) = GetCurrentBlitz();
        if (!HasPendingMasterfulBlitz(currentBlitz))
            _prioritizePendingBlitzAfterMechanicHold = false;
        CountAutomaticPBBlitzCompletionFromLastCast();
        NumAOETargets = NumMeleeAOETargets(strategy);
        _encounterHint = ResolveEncounterHint(strategy, rangedTarget);
        ResumeAutomaticBurstScheduleFromRotationMode(strategy);
        UpdateAutomaticPBBurstTracking(strategy, rangedTarget);

        if (BlitzLeft > GCD)
        {
            if (currentBlitzIsTargeted)
                (BestBlitzTarget, NumBlitzTargets) = SelectTarget(strategy, meleeTarget, 3, IsSplashTarget);
            else
            {
                BestBlitzTarget = null;
                NumBlitzTargets = NumAOETargets;
            }
        }
        else
        {
            BestBlitzTarget = null;
            NumBlitzTargets = 0;
        }

        (CurrentForm, FormLeft) = DetermineForm();

        BestRangedTarget = SelectTarget(strategy, rangedTarget, RangedTargetRange, IsSplashTarget).Best;
        (BestLineTarget, NumLineTargets) = SelectTarget(strategy, rangedTarget, LineTargetRange, EnlightenmentTargetCheck);

        var holdForLookAway = ShouldHoldForLookAway(rangedTarget?.Actor);
        var mechanicHoldReason = ResolveMechanicHoldReason(holdForLookAway, rangedTarget, meleeTarget);
        UpdateMechanicHoldState(strategy, mechanicHoldReason);

        var burst = BuildBurstPlan(strategy, rangedTarget);
        var basicAndChakraOvercapOnly = BasicAndChakraOvercapOnly(strategy);

        EffectiveForm = GetEffectiveForm(strategy, burst);

        if (!basicAndChakraOvercapOnly)
        {
            Meditate(strategy, meleeTarget, rangedTarget?.Actor);
            FormShift(strategy, meleeTarget);
            DowntimeFormShiftForLeapingOpo(strategy, burst, rangedTarget, meleeTarget);
        }

        if (CountdownRemaining > 0)
        {
            if (CountdownRemaining is > 3 and < 15 && FormShiftLeft == 0)
                PushGCD(AID.FormShift, Player);

            if (strategy.Pot.Value == PotionStrategy.OpenerAndEvenBursts
                && CountdownRemaining <= 1.5f
                && PotionReadyIn() <= AnimationLockDelay
                && (HaveTarget || rangedTarget != null)
                && EncounterHintAllowsAutomaticBurst(strategy)
                && !ShouldHoldDancingMadTopLogPotion(strategy))
            {
                var potionPrio = strategy.Pot.Priority(ActionQueue.Priority.Low + 100 + (float)OGCDPriority.Potion);
                Potion(Math.Max(potionPrio, BurstPotionPriority), lateWeave: false);
            }

            SmartEngage(strategy, rangedTarget);

            // prepull forced row/bh/tn plans
            ForcedPrepullOGCD(strategy, meleeTarget);
            return;
        }

        var dancingMadTopLogPBWindow = IsInDancingMadTopLogActionWindow(strategy, AID.PerfectBalance);
        var consumeFormlessBeforeOddPB = !dancingMadTopLogPBWindow && ShouldConsumeFormlessBeforeOddAutomaticPB(strategy, burst);
        var delayOddPBUntilOpoGCD = !dancingMadTopLogPBWindow && ShouldDelayOddAutomaticPBUntilOpoGCD(strategy, burst);
        var delayEvenPBForBurstValue = !dancingMadTopLogPBWindow && ShouldDelayEvenAutomaticPBForBurstValue(strategy, burst);
        var holdAutomaticPBForPredictedGCD = consumeFormlessBeforeOddPB || delayOddPBUntilOpoGCD || delayEvenPBForBurstValue;

        // the blitz / reply GCDs that the formless branch below is allowed to defer to only exist when this block runs
        var coreBurstGCDsQueued = !basicAndChakraOvercapOnly && !holdForLookAway && HaveTarget;
        if (coreBurstGCDsQueued)
        {
            UseBlitz(strategy, currentBlitz, burst);
            FiresReply(strategy, burst);
            WindsReply(strategy);
        }

        if (!holdForLookAway && HaveMeleeTarget && UseAOE)
        {
            var aoeAction = BestAOEGCD();
            if (aoeAction != AID.None)
                PushGCD(aoeAction, Player, GCDPriority.AOE);
        }

        GCDPriority prioBuffed(int balls) => HaveMeleeTarget && balls > 0 && meleeTarget?.Priority >= 0 ? GCDPriority.BasicSpender : GCDPriority.Basic;

        if (!holdForLookAway && meleeTarget != null)
        {
            switch (EffectiveForm)
            {
                case Form.Coeurl:
                {
                    var action = BestCoeurlGCD();
                    var isBuffed = IsBuffedCoeurlGCD(action);
                    PushGCD(action, meleeTarget, isBuffed ? prioBuffed(CoeurlStacks) : GCDPriority.BasicSaver, useOnDyingTarget: !isBuffed);
                    break;
                }
                case Form.Raptor:
                {
                    var action = BestRaptorGCD();
                    var isBuffed = IsBuffedRaptorGCD(action);
                    PushGCD(action, meleeTarget, isBuffed ? prioBuffed(RaptorStacks) : GCDPriority.BasicSaver, useOnDyingTarget: !isBuffed);
                    break;
                }
                case Form.OpoOpo:
                {
                    var action = BestOpoGCD();
                    var isBuffed = IsBuffedOpoGCD(action);
                    PushGCD(action, meleeTarget, isBuffed ? prioBuffed(OpoStacks) : GCDPriority.BasicSaver, useOnDyingTarget: !isBuffed);
                    break;
                }
                default:
                {
                    var action = BestOpoGCD();
                    var isBuffed = IsBuffedOpoGCD(action);
                    // yielding the window to a blitz / reply that was never queued (basic+chakra mode, or no ranged
                    // target while a melee one exists) would leave this GCD window empty
                    if (holdAutomaticPBForPredictedGCD || !coreBurstGCDsQueued || !CoreBurstGCDTakesPriorityOverFormless(strategy, currentBlitz, burst))
                        PushGCD(action, meleeTarget, isBuffed && FormShiftLeft > GCD ? prioBuffed(OpoStacks) : isBuffed ? GCDPriority.Basic : GCDPriority.BasicSaver, useOnDyingTarget: !isBuffed);
                    break;
                }
            }

            if (!basicAndChakraOvercapOnly)
            {
                switch (strategy.SSS.Value)
                {
                    case OffensiveStrategy.Force:
                        PushGCD(AID.SixSidedStar, meleeTarget, GCDPriority.SSS);
                        break;
                    case OffensiveStrategy.Automatic:
                        if (PBOrBlitzCompletionTakesPriority())
                            break;

                        var mechanicSSS = ShouldUseMechanicSixSidedStar();
                        var downtimeSSS = !MeaningfulForcedOut
                            && EffectiveDowntimeIn > 0
                            && !CanFitGCD(EffectiveDowntimeIn, 1);
                        var fightEndSSS = TryGetResourceBurnHorizon(strategy, out var sssHorizon)
                            && sssHorizon > CombatTimer
                            && !CanFitGCD(sssHorizon - CombatTimer, 1);
                        if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.SSS))
                            LogGuide73("SSS", $"t={CombatTimer:f1} use={mechanicSSS || downtimeSSS || fightEndSSS} mechanic={mechanicSSS} downtime={downtimeSSS} fightEnd={fightEndSSS} fire={FireLeft:f2} bh={BrotherhoodLeft:f2} forced={MeaningfulForcedOut} loss={_meleePrediction.MeleeLossIn:f2} duration={_meleePrediction.ForcedOutDuration:f2} effDown={EffectiveDowntimeIn:f2} fightLeft={FightTimeRemaining:f2} canFit={CanFitGCD(EffectiveDowntimeIn, 1)}");
                        if (mechanicSSS || downtimeSSS || fightEndSSS)
                            PushGCD(AID.SixSidedStar, meleeTarget, GCDPriority.SSS, useOnDyingTarget: false);
                        break;
                }
            }
        }

        // downtime PB prep needs no target (PB itself is queued targetless), so it lives outside the melee-target guard
        if (!basicAndChakraOvercapOnly && !holdForLookAway)
            Prep(strategy);

        var pos = NextPositional;

        UpdatePositionals(meleeTarget, ref pos);

        GoalZoneCombined(strategy, GoalZoneMeleeRange, Hints.GoalAOECircle(GoalZoneCircleRadius), AID.ArmOfTheDestroyer, AOEBreakpoint, maximumActionRange: GoalZoneMaxRange);

        if (Player.InCombat)
            OGCD(strategy, rangedTarget, meleeTarget, burst, holdAutomaticPBForPredictedGCD);
        else
            ForcedPrepullOGCD(strategy, meleeTarget);
    }

    // out-of-combat subset of OGCD: only explicitly forced buffs (the automatic planners require combat state)
    private void ForcedPrepullOGCD(in Strategy strategy, Enemy? meleeTarget)
    {
        if (BasicAndChakraOvercapOnly(strategy))
            return;

        if (strategy.Pot.Value == PotionStrategy.Now)
            Potion(strategy.Pot.Priority(ActionQueue.Priority.Low + 100 + (float)OGCDPriority.Potion), lateWeave: false);

        if (strategy.Brotherhood.Value == OffensiveStrategy.Force)
            PushOGCD(AID.Brotherhood, Player, OGCDPriority.Brotherhood);

        if (strategy.RoF.Value is RoFStrategy.Force or RoFStrategy.ForceMidWeave)
            PushOGCD(AID.RiddleOfFire, Player, OGCDPriority.RiddleOfFire);

        if (strategy.RoW.Value == RoWStrategy.Force)
            PushOGCD(AID.RiddleOfWind, Player, OGCDPriority.RiddleOfWind);

        UseTN(strategy, meleeTarget);
    }

    private void OGCD(in Strategy strategy, Enemy? rangedTarget, Enemy? meleeTarget, in BurstPlan burst, bool holdAutomaticPBForPredictedGCD)
    {
        var fightEndBurn = EndBurnWindow(strategy, GCD + 1);
        var holdForLookAway = ShouldHoldForLookAway(rangedTarget?.Actor);
        if (BasicAndChakraOvercapOnly(strategy))
        {
            var forcedPBQueued = strategy.PB.Value is PBStrategy.Force or PBStrategy.ForceNoShift
                ? QueuePB(strategy, meleeTarget, burst)
                : false;

            if (!holdForLookAway)
                QueueChakra(strategy, rangedTarget, fightEndBurn, coreBurstWeaveQueued: forcedPBQueued, overcapOnly: true);
            return;
        }

        var potionPrio = strategy.Pot.Priority(ActionQueue.Priority.Low + 100 + (float)OGCDPriority.Potion);
        var burstPotionPrio = Math.Max(potionPrio, BurstPotionPriority);
        switch (strategy.Pot.Value)
        {
            case PotionStrategy.Now:
                Potion(potionPrio, lateWeave: false);
                break;
            case PotionStrategy.OpenerAndEvenBursts:
                if (!holdForLookAway && ShouldUseEvenBurstPotion(strategy, burst, includeOpener: true))
                    Potion(burstPotionPrio);
                break;
            case PotionStrategy.NonOpenerEvenBursts:
                if (!holdForLookAway && ShouldUseEvenBurstPotion(strategy, burst, includeOpener: false))
                    Potion(burstPotionPrio);
                break;
        }

        var zeroSecondOpenerFastPack = !DancingMadTopLogProfileEnabled(strategy)
            && strategy.OpenerRoFOffset.Value == OpenerRoFOffsetStrategy.ZeroSecondBurst
            && Player.InCombat
            && CombatTimer <= ZeroSecondBurstOpenerWindow
            && _activatedRoFBursts == 0
            && HaveTarget
            && EncounterHintAllowsAutomaticBurst(strategy)
            && (CurrentForm == Form.Raptor || PerfectBalanceLeft > 0 || BrotherhoodLeft > GCD);
        var coreBurstFastPackQueued = !holdForLookAway && zeroSecondOpenerFastPack && QueueCoreBurstFastPack(strategy, burst);
        if (coreBurstFastPackQueued)
            return;

        if (!holdForLookAway && zeroSecondOpenerFastPack && QueueZeroSecondOpenerBurst(strategy, burst))
            return;

        var (useRof, rofLate) = ShouldRoF(strategy, burst);
        if (holdForLookAway && strategy.RoF.Value == RoFStrategy.Automatic && !burst.UseRoFRecastNow)
            useRof = false;
        var rofQueued = useRof;

        if (useRof)
        {
            var rofPriority = burst.UseRoFFixedTimingNow
                ? (int)(ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low + 1)
                : (int)OGCDPriority.RiddleOfFire;
            var rofDelay = burst.UseRoFFixedTimingNow ? 0 : rofLate ? Math.Max(0, GCD - EarliestRoF(AnimationLockDelay)) : 0;
            PushOGCD(AID.RiddleOfFire, Player, rofPriority, rofDelay);
        }

        var brotherhoodQueued = !holdForLookAway || strategy.Brotherhood.Value != OffensiveStrategy.Automatic
            ? Brotherhood(strategy, burst)
            : false;
        var pbQueued = (!holdForLookAway || strategy.PB.Value != PBStrategy.Automatic)
            && !holdAutomaticPBForPredictedGCD
            ? QueuePB(strategy, meleeTarget, burst)
            : false;

        if ((!holdForLookAway || strategy.RoW.Value == RoWStrategy.Force) && ShouldRoW(strategy, burst))
            PushOGCD(AID.RiddleOfWind, Player, OGCDPriority.RiddleOfWind);

        RiddleOfEarth(strategy);

        EarthsReply(strategy);

        UseTN(strategy, meleeTarget);

        if (!holdForLookAway)
            QueueChakra(strategy, rangedTarget, fightEndBurn, rofQueued || brotherhoodQueued || pbQueued, overcapOnly: false);

        var tc = strategy.TC;
        if (!holdForLookAway && tc.Value == TCStrategy.GapClose)
        {
            var tcOverrideTarget = ResolveTargetOverride(tc);
            var manualOrMacroThunderclap = tcOverrideTarget != null;
            var tcTarget = tcOverrideTarget ?? rangedTarget;
            var canThunderclapSafely = CanThunderclapSafely(tcTarget, Math.Max(GCD, AnimationLockDelay));
            var mechanicAllowsGapClose = canThunderclapSafely;
            if (!mechanicAllowsGapClose && ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.MeleeSafe))
                LogTuning("MeleeSafe", $"t={CombatTimer:f1} tc=false forced={MeaningfulForcedOut} target={tcTarget != null} dist={Player.DistanceToHitbox(tcTarget):f2} canTC={canThunderclapSafely} loss={_meleePrediction.MeleeLossIn:f2} resume={_meleePrediction.MeleeResumeIn:f2}");
            if (Unlocked(AID.Thunderclap) && Player.DistanceToHitbox(tcTarget) is > MeleeRange and <= GapCloseMaxRange && mechanicAllowsGapClose)
            {
                var automaticBurstReturnThunderclap = !manualOrMacroThunderclap
                    && !HaveMeleeTarget
                    && CanThunderclapForAutomaticBurstStart(strategy, tcTarget)
                    && (burst.EvenWindow || burst.UseBrotherhoodNow || burst.UseRoFNow || burst.UsePBAutomaticNow);
                var priority = manualOrMacroThunderclap
                    ? (int)(ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low + 3)
                    : automaticBurstReturnThunderclap
                        ? (int)(ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low + 3)
                        : HaveMeleeTarget
                            ? (int)OGCDPriority.TrueNorth
                            : (int)OGCDPriority.RiddleOfWind - 1;
                PushOGCD(AID.Thunderclap, tcTarget, priority);
            }
        }

        if (!ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.Priority))
            return;

        var rofPriorityLog = burst.UseRoFFixedTimingNow ? ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low + 1 : (float)OGCDPriority.RiddleOfFire;
        var brotherhoodPriorityLog = burst.UseBrotherhoodFixedTimingNow ? ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low : (float)OGCDPriority.Brotherhood;
        LogTuning("Priority", $"t={CombatTimer:f1} rof={rofQueued}/{rofPriorityLog:f0} bh={brotherhoodQueued}/{brotherhoodPriorityLog:f0} pb={pbQueued}/{OGCDPriority.PerfectBalance} pot={potionPrio:f0}/{burstPotionPrio:f1} row={OGCDPriority.RiddleOfWind} tfc={OGCDPriority.TFC}/{OGCDPriority.TFCBrotherhood}/{OGCDPriority.TFCOvercap}");
    }

    private const float RiddleOfEarthAutoDamageWindow = 10f;

    private float ConfirmedSelfDamageIn()
    {
        var best = float.MaxValue;
        var now = World.CurrentTime;

        foreach (var damage in Hints.PredictedDamage)
        {
            if (!damage.Players[PartyState.PlayerSlot])
                continue;

            if (damage.Type is not (PredictedDamageType.Raidwide or PredictedDamageType.Shared or PredictedDamageType.Tankbuster))
                continue;

            var damageIn = (float)(damage.Activation - now).TotalSeconds;
            if (damageIn < 0 || damageIn > RiddleOfEarthAutoDamageWindow)
                continue;

            best = Math.Min(best, damageIn);
        }

        return best;
    }

    private bool ShouldUseRiddleOfEarth(in Strategy strategy)
    {
        if (strategy.RoE.Value != RoEStrategy.Automatic)
            return false;

        if (!Player.InCombat
            || !Unlocked(AID.RiddleOfEarth)
            || ReadyIn(AID.RiddleOfEarth) > AnimationLockDelay)
            return false;

        if (EarthLeft > 0 || EarthsReplyLeft > 0)
            return false;

        return ConfirmedSelfDamageIn() <= RiddleOfEarthAutoDamageWindow;
    }

    private void RiddleOfEarth(in Strategy strategy)
    {
        if (ShouldUseRiddleOfEarth(strategy))
            PushOGCD(AID.RiddleOfEarth, Player, OGCDPriority.RiddleOfEarth);
    }

    private bool PartyAllDamaged()
    {
        var party = World.Party.WithoutSlot(excludeAlliance: true, excludeNPCs: true);
        var count = 0;
        foreach (var member in party)
        {
            if (member.IsDead || member.HPMP.MaxHP <= 0)
                continue;

            ++count;
            if (member.HPMP.CurHP >= member.HPMP.MaxHP)
                return false;
        }

        return count > 0;
    }

    private void EarthsReply(in Strategy strategy)
    {
        if (!Player.InCombat
            || BasicAndChakraOvercapOnly(strategy)
            || !Unlocked(AID.EarthsReply)
            || EarthsReplyLeft <= 0)
            return;

        var expiryWindow = Math.Max(GCD, AnimationLockDelay + 0.3f);
        var expiringSoon = EarthsReplyLeft <= expiryWindow;
        if (ReadyIn(AID.EarthsReply) > AnimationLockDelay || !expiringSoon && !PartyAllDamaged())
            return;

        PushOGCD(AID.EarthsReply, Player, (int)OGCDPriority.TrueNorth - 1);
    }

    private void QueueChakra(in Strategy strategy, Enemy? rangedTarget, bool fightEndBurn, bool coreBurstWeaveQueued, bool overcapOnly)
    {
        var chakraThreshold = overcapOnly ? ChakraOvercapThreshold : 5;
        if (!HaveTarget || Chakra < chakraThreshold)
            return;

        var chakraPriority = (int)(Chakra >= 10
            ? OGCDPriority.TFCOvercap
            : BrotherhoodLeft > 0 && Chakra >= 8
                ? OGCDPriority.TFCBrotherhood
                : OGCDPriority.TFC);
        if (coreBurstWeaveQueued)
            chakraPriority = Math.Min(chakraPriority, (int)OGCDPriority.ManualOGCD);

        var useLineChakra = ShouldUseChakraLineAction();
        var (chakraAction, chakraTarget) = useLineChakra
            ? (ChakraLineAction, BestLineTarget)
            : (ChakraSingleTargetAction, rangedTarget);
        if (chakraTarget?.Priority is not Enemy.PriorityInvincible and not Enemy.PriorityForbidden
            && (fightEndBurn || chakraTarget?.Priority is not Enemy.PriorityPointless))
        {
            if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.Chakra))
                LogGuide73("Chakra", $"t={CombatTimer:f1} chakra={Chakra} threshold={chakraThreshold} action={chakraAction} prio={chakraPriority} bh={BrotherhoodLeft:f2} coreQueued={coreBurstWeaveQueued} overcapOnly={overcapOnly} lineTargets={NumLineTargets}");
            PushOGCD(chakraAction, chakraTarget?.Actor, chakraPriority);
        }
    }

    // Running back to the target (a knockback, a forced step out of melee) and arriving within one GCD: a Meditation pressed now would still
    // be recasting on arrival and hold the first melee weaponskill back, which is worth more than the one chakra it grants. Only a target
    // we are visibly closing on counts (distance shrinking frame over frame), so a player who stands off or runs away still meditates.
    // Harness (irregular range): +0.58% (4,067 scenarios better, 1,232 worse of 17,196), nothing else changes. Needs no downtime knowledge:
    // a known downtime (UptimeIn > GCD + 1) or the Force setting still meditate.
    private const float RunSpeed = 6f;
    private ulong _closingTargetID;
    private float _closingDistance;
    private bool _closingOnTarget;

    private void UpdateClosingOnTarget(Actor? target)
    {
        var distance = target != null ? Player.DistanceToHitbox(target) : float.MaxValue;
        _closingOnTarget = target != null && target.InstanceID == _closingTargetID && distance < _closingDistance - 0.001f;
        _closingTargetID = target?.InstanceID ?? 0;
        _closingDistance = distance;
    }

    private bool ArrivingInMeleeWithinGCD(Actor? target)
        => _closingOnTarget && target != null && Player.DistanceToHitbox(target) is var distance && distance > MeleeRange && (distance - MeleeRange) / RunSpeed < GCDLength;

    private void Meditate(in Strategy strategy, Enemy? primaryTarget, Actor? approachTarget = null)
    {
        if (Chakra >= 5 || !Unlocked(AID.SteeledMeditation))
            return;

        if (strategy.Meditate.Value != MeditationStrategy.Force
            && strategy.Blitz.Value == BlitzStrategy.Automatic
            && BeastCount == 3
            && BlitzLeft > GCD)
            return;

        var prio = GCDPriority.None;
        var inMeleeRange = primaryTarget != null && Player.DistanceToHitbox(primaryTarget) <= MeleeRange;

        switch (strategy.Meditate.Value)
        {
            case MeditationStrategy.Force:
                prio = GCDPriority.MeditateForce;
                break;
            case MeditationStrategy.Safe:
                if (!Player.InCombat)
                    prio = GCDPriority.Meditate;

                if (!inMeleeRange && (UptimeIn > GCD + 1 || (UptimeIn ?? 0) == 0 && primaryTarget == null))
                    prio = GCDPriority.Meditate;
                break;
            case MeditationStrategy.Greedy:
                if (!inMeleeRange)
                    prio = GCDPriority.Meditate;
                break;
        }

        if (strategy.Meditate.Value != MeditationStrategy.Force && !(UptimeIn > GCD + 1) && ArrivingInMeleeWithinGCD(approachTarget))
            return;

        PushGCD(AID.SteeledMeditation, Player, prio);
    }

    private const float StandardOpenerRoFOffset = 7.8f;
    private const float EarlyOpenerRoFOffset = 5.8f;
    private const float ZeroSecondBurstOpenerWindow = 10f;
    private const float OpenerPartyBurstSearchWindow = 30f;

    // Tuning constants: behavior-preserving names for numeric gates that should be compared by logs before changing.
    private const float BurstImmediateWindowGCDs = 2f;
    private const float RoFCooldown = 60f;
    private const float BrotherhoodCooldown = 120f;
    private const float MajorBurstMeleeSafeGCDs = 5f;
    private const float PBMeleeSafeGCDs = 3f;
    private const float PBPrepDeadlineBuffer = 0.5f;
    private const float PBUseSpacing = 10f;
    private const float PerfectBalanceCooldown = 40f;
    private const float CoreBurstFastReadyLeeway = 0.15f;
    private const float LateOddRoFSkipThreshold = 10f;
    private const float EvenPreRoFPBMinThreshold = 2.0f;
    private const float PBNearOvercapWindow = 30f;
    private const float CurrentBurstScheduleOverlapWindow = 30f;
    private const float EarlyBrotherhoodCombatDelay = 10f;
    private const float LastPBWindowGCDs = 3f;
    private const float EvenPreRoFPBStartThreshold = 6.0f;
    private const float RoFBrotherhoodResyncWindow = 45f;
    private const float SynergyPlannerCycle = 120f;
    private const int SynergyPlannerMaxCycles = 6;
    private const float SynergyResyncScoreGainThreshold = 0.01f;
    private const float BurstPotionPriority = ActionQueue.Priority.VeryHigh + 1.5f;
    private const float TargetAvailableUptimeRequired = 20f;
    private const float LastRoFWindowLeeway = 70f;
    private const float LastBrotherhoodWindowLeeway = 135f;
    private const float LastPotionWindowLeeway = 285f;
    private const float PotionLateWeaveDelay = 0.9f;
    private const float PotionOpenerWindow = 30f;
    private const float PotionFutureWindowLeeway = 5f;
    private const float RoWTwoMinuteStart = 90f;
    private const float RoWTwoMinuteEnd = 120f;
    private const float RoWTwoMinuteHoldWindow = 45f;
    private const float RoWCooldown = 90f;
    private const float RoWOpenerWindow = 30f;
    private const float RoWUptimeRequired = 15f;
    private const float RoWEndBurnLeeway = 20f;

    private const float FightEstimateInitialGrace = 2f;
    private const float FightEstimateHPRiseReset = 0.05f;
    private const float FightEstimateMinDamageDelta = 0.001f;
    private const float FightEstimateMinStableDrain = 0.0005f;
    private const float FightEstimateDrainVarianceTolerance = 0.35f;
    private const float FightEstimateDrainSmoothing = 0.35f;
    private const float FightEstimateHealResetDelta = -0.02f;
    private const float FightEstimateHealDrainDecay = 0.5f;
    private const float FightEstimateMaxClamp = 1800f;
    private const float FightEstimateStableLowHP = 0.25f;
    private const float FightEstimateStableCloseEndWindow = 45f;
    private const int FightEstimateStableSampleCount = 3;
    private const float MeleePredictionHorizon = 20f;
    private const float MeleePredictionSampleStep = 0.25f;
    private const float MeleePredictionCacheSeconds = 0.25f;
    private const float MovementSafetyBuffer = 0.35f;
    private const float MeleeRange = 3f;
    private const float GapCloseMaxRange = 20f;
    private const float SafeMeleeInnerRadiusOffset = 1.2f;
    private const float SafeMeleeMiddleRadiusOffset = 2.2f;
    private const float SafeMeleeOuterRadiusOffset = 2.8f;
    private const int SafeMeleeSamples = 24;
    private const int PathSafetySteps = 4;
    private const float ShortAOETransitionGCDs = 2f;
    private const float AOELastsSeveralGCDs = 3f;
    private const int ThreeTargetAOEBreakpoint = 3;
    private const int FullAOEBreakpoint = 4;
    private const float GoalZoneMeleeRange = 3f;
    private const float GoalZoneCircleRadius = 5f;
    private const float GoalZoneMaxRange = 20f;
    private const float RangedTargetRange = 20f;
    private const float LineTargetRange = 10f;
    private const float LineAOEHalfWidth = 2f;
    private const float TargetAcquisitionRange = 25f;
    private const float DesiredFireWindowGCDs = 10f;
    private const float RoFLateWeaveExtraDelay = 0.8f;
    private const float RoFWindowBudget = 20.6f;
    private const float TrueNorthLateWeaveDelay = 0.72f;
    private const float ThunderclapZeroLandingCountdown = 0.7f;
    private const float ThunderclapRandomOffsetMax = 0.3f;
    private const float UltimateCrossCombatPBBurstSnapshotSeconds = 55f;
    private const float DancingMadCrossCombatPBBurstSnapshotSeconds = 95f;
    // P1 becomes untargetable at 197.3s and P2 resumes at 207.6s; MNK only needs the disappearance time for resource burn decisions.
    private const float DancingMadP1P2DowntimeStart = 197.3f;
    private const float AllianceCrossCombatPBBurstSnapshotSeconds = 25f;
    private const float EngageMovementSpeedEstimate = 7.8f;
    private const float EngageMovementBuffer = 0.5f;
    private const float SixSidedStarApplicationDelay = 0.62f;
    private const float DragonKickApplicationDelay = 1.29f;
    private const float ForbiddenChakraApplicationDelay = 1.48f;
    private const float DemolishApplicationDelay = 1.60f;
    private const float TuningLogThrottleSeconds = 1.0f;
    private const float StaleScheduledAnchorSeconds = 3600f;
    private const int MaxScheduledAnchorCatchUpPeriods = 128;
    private const float RoFBrotherhoodSyncCacheSeconds = 0.5f;

    public struct Strategy : IStrategyCommon
    {
        [Track(UiPriority = 500)]
        public Track<Targeting> Targeting;
        [Track(UiPriority = 499)]
        public Track<AOEStrategy> AOE;

        [Track(InternalName = "BH", MinLevel = 70, UiPriority = 99, Action = AID.Brotherhood)]
        public Track<OffensiveStrategy> Brotherhood;

        [Track("Burst Timing", MinLevel = 68, UiPriority = 98)]
        public Track<BurstTimingStrategy> BurstTiming;
        [Track("スキル回しモード", InternalName = "SkillRotation", UiPriority = 97)]
        public Track<SkillRotationMode> SkillRotation;

        // RoF
        [Track("Riddle of Fire", MinLevel = 68, UiPriority = 96, Action = AID.RiddleOfFire)]
        public Track<RoFStrategy> RoF;
        [Track("Opener RoF Offset", MinLevel = 68, UiPriority = 95)]
        public Track<OpenerRoFOffsetStrategy> OpenerRoFOffset;
        [Track("Fire's Reply", MinLevel = 100, UiPriority = 94, Action = AID.FiresReply)]
        public Track<FRStrategy> FiresReply;

        // RoW
        [Track("Riddle of Wind", UiPriority = 93, MinLevel = 72, Action = AID.RiddleOfWind)]
        public Track<RoWStrategy> RoW;
        [Track("Riddle of Earth", InternalName = "RoE", MinLevel = 64, UiPriority = 91, Action = AID.RiddleOfEarth)]
        public Track<RoEStrategy> RoE;
        [Track("Wind's Reply", UiPriority = 92, MinLevel = 96, Action = AID.WindsReply)]
        public Track<WRStrategy> WindsReply;

        // blitz/nadi stuff
        [Track("Perfect Balance", UiPriority = 89, Action = AID.PerfectBalance)]
        public Track<PBStrategy> PB;
        [Track(UiPriority = 88, MinLevel = 60)]
        public Track<NadiStrategy> Nadi;
        [Track(UiPriority = 87, MinLevel = 60, Actions = [AID.ElixirField, AID.FlintStrike, AID.TornadoKick, AID.ElixirBurst, AID.RisingPhoenix, AID.PhantomRush])]
        public Track<BlitzStrategy> Blitz;

        // downtime
        [Track("Six-Sided Star", InternalName = "SixSidedStar", MinLevel = 80, UiPriority = 79, Action = AID.SixSidedStar)]
        public Track<OffensiveStrategy> SSS;
        [Track("Form Shift", MinLevel = 52, UiPriority = 78, Action = AID.FormShift)]
        public Track<OffensiveStrategy> FormShift;
        [Track("Meditate", UiPriority = 77, Actions = [AID.SteeledMeditation, AID.ForbiddenMeditation, AID.InspiritedMeditation, AID.EnlightenedMeditation])]
        public Track<MeditationStrategy> Meditate;

        // other
        [Track("Thunderclap", MinLevel = 35, UiPriority = 69, Action = AID.Thunderclap)]
        public Track<TCStrategy> TC;
        [Track("ポーション", UiPriority = 59, Item = 1049234)]
        public Track<PotionStrategy> Pot;
        [Track("Fight End", UiPriority = 58)]
        public Track<FightEndStrategy> FightEnd;
        [Track(UiPriority = 49)]
        public Track<EngageStrategy> Engage;
        [Track("True North", MinLevel = 50, UiPriority = 48, Action = AID.TrueNorth)]
        public Track<OffensiveStrategy> TrueNorth;
        [Track("Rotation Mode", UiPriority = 498)]
        public Track<RotationModeStrategy> RotationMode;
        [Track("Encounter Hint", UiPriority = 57)]
        public Track<MNKEncounterHintStrategy> EncounterHint;
        [Track("Tuning Log", UiPriority = 56)]
        public Track<MNKTuningLogStrategy> TuningLog;

        [Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)]
        public Track<MechanicHintStrategy> MechanicHints;

        readonly Targeting IStrategyCommon.Targeting => Targeting.Value;
        readonly AOEStrategy IStrategyCommon.AOE => AOE.Value;
    }

    public enum PotionStrategy
    {
        [Option("自動使用しない")]
        Manual,
        [Option("おすすめ: 開幕、偶数バースト")]
        OpenerAndEvenBursts,
        [Option("開幕以外、偶数バースト")]
        NonOpenerEvenBursts,
        [Option("即使用")]
        Now
    }
    public enum RotationModeStrategy
    {
        [Option("おすすめ: 通常運用")]
        Automatic,
        [Option("通常ローテ + 闘気溢れのみ")]
        BasicAndChakraOvercap
    }
    public enum SkillRotationMode
    {
        [Option("おすすめ: 通常")]
        Normal,
        [Option("絶妖星乱舞: トップログ由来のバースト・ポーション時刻を優先")]
        DancingMad
    }
    public enum FightEndStrategy
    {
        [Option("おすすめ: 予測戦闘終了に合わせてリソーススキルを吐き切る")]
        Automatic,
        [Option("戦闘終了予測を使わない")]
        Ignore
    }
    public enum BurstTimingStrategy
    {
        [Option("おすすめ: シナジー固定: 120秒/Planner/PTバーストに合わせ、床予兆では固定RoF/BHをずらさない")]
        SynergyFixed,
        [Option("近接安全: 120秒/Planner/PTバーストに合わせるが、1GCD以上殴れない時は固定RoF/BHを遅らせる")]
        MeleeSafe,
        [Option("リキャスト優先: PTバースト再同期をせず、開幕設定とリキャスト周期を優先する")]
        Cooldown
    }
    public enum MNKEncounterHintStrategy
    {
        [Option("おすすめ: 自動判別: 戦闘状況からボス/雑魚/復帰前を推定")]
        Automatic,
        [Option("ボス戦: 通常通りバーストを許可")]
        Boss,
        [Option("雑魚フェーズ: 範囲回しは行い、RoF/BH/PB/RoW/薬は温存")]
        Trash,
        [Option("アライアンス雑魚: 3体以上で長く残るならバーストを許可")]
        AllianceTrash,
        [Option("大型雑魚/中ボス: ボス同様にバーストを許可")]
        MajorAdd,
        [Option("ボス復帰前: 範囲→単体移行を優先し、RoF/BH/PB/RoW/薬は温存")]
        BossReturn,
        [Option("バースト禁止: RoF/BH/PB/RoW/薬を温存")]
        HoldBurst,
        [Option("バースト許可: 雑魚/復帰ヒントを無視して自動バーストを許可")]
        ForceBurst,
        [Option("OFF: エンカウンターヒントを使わない")]
        Off
    }
    public enum MNKTuningLogStrategy
    {
        [Option("OFF: 調整ログを出さない")]
        Off,
        [Option("Burst: RoF/BH/PB窓判定")]
        Burst,
        [Option("PB: 踏鳴判定")]
        PB,
        [Option("Potion: 薬判定")]
        Potion,
        [Option("RoW: Riddle of Wind判定")]
        RoW,
        [Option("MeleeSafe: 近接安全判定")]
        MeleeSafe,
        [Option("FightEnd: 終盤推定")]
        FightEnd,
        [Option("Priority: 優先度調査")]
        Priority,
        [Option("ALL: 全調整ログ")]
        All,
        [Option("Chakra: 闘気使用判定")]
        Chakra,
        [Option("SSS: 六合星導脚判定")]
        SSS
    }
    public enum MeditationStrategy
    {
        [Option("おすすめ: 非戦闘中、または近くに敵がいなければ使う")]
        Safe,
        [Option("非戦闘中、または近接範囲に敵がいなければ使う")]
        Greedy,
        [Option("即使用")]
        Force,
        [Option("使わない")]
        Delay
    }
    public enum FRStrategy
    {
        [Option("おすすめ: OpoフォームGCD後に使う", Targets = ActionTargets.Hostile)]
        Automatic,
        [Option("遠隔攻撃が必要になるまで温存", Targets = ActionTargets.Hostile)]
        Ranged,
        [Option("即使用", Targets = ActionTargets.Hostile)]
        Force,
        [Option("使わない")]
        Delay
    }
    public enum RoWStrategy
    {
        [Option("おすすめ: 自動: 原則リキャスト毎に使用。2分合わせで過剰に保持しない")]
        Automatic,
        [Option("使わない")]
        Delay,
        [Option("即使用")]
        Force,
        [Option("開幕、2分: 開幕で使い、次は2分のRoF/BHまで待機し、その後はRoFに合わせて使う")]
        OpenerTwoMinuteThenCooldown,
        [Option("開幕からリキャスト毎: 開幕からリキャスト毎に使う")]
        OpenerCooldown,
        [Option("開幕、2分待機、以後は戦闘時間に応じて90秒即撃ち/120秒RoF・BH合わせを選ぶ")]
        OpenerTwoMinuteAdaptive,
        [Option("RoFに合わせて使用")]
        RoFAligned
    }
    public enum RoEStrategy
    {
        [Option("Use on confirmed self damage within 10s")]
        Automatic,

        [Option("Do not use")]
        Delay
    }
    public enum WRStrategy
    {
        [Option("おすすめ: 遠隔攻撃が必要になるまで温存", Targets = ActionTargets.Hostile)]
        Automatic,
        [Option("即使用", Targets = ActionTargets.Hostile)]
        Force,
        [Option("次のダウンタイムの2GCD以上前までに使う", Targets = ActionTargets.Hostile)]
        PreDowntime,
        [Option("使わない")]
        Delay
    }
    public enum NadiStrategy
    {
        [Option("おすすめ: 推定キルタイムでSolar Lunar / Double Lunarを自動選択する")]
        Automatic,
        [Option("月 -> 日サイクル; 最初のPRを1分バーストに合わせる", Color = 0xFFE7D7A5)]
        LunarSolar,
        [Option("月のナディ", Color = 0xFFDB8BCA)]
        Lunar,
        [Option("日のナディ", Color = 0xFF8EE6FA)]
        Solar,
        [Option("開幕・偶数バーストはダブルルナー、奇数バーストはソーラー", Color = 0xFFE7D7A5)]
        DoubleLunar
    }
    public enum RoFStrategy
    {
        [Option("おすすめ: バースト中に自動使用")]
        Automatic,
        [Option("即使用(早挟み)")]
        Force,
        [Option("即使用(遅挟み)")]
        ForceMidWeave,
        [Option("使わない")]
        Delay,
    }
    public enum OpenerRoFOffsetStrategy
    {
        [Option("おすすめ: 標準: 戦闘開始 + 7.8秒")]
        Standard78,
        [Option("早め: 戦闘開始 + 5.8秒")]
        Early58,
        [Option("PTバースト推定に合わせる")]
        PartyBurstAligned,
        [Option("開幕0秒バースト")]
        ZeroSecondBurst
    }
    public enum PBStrategy
    {
        [Option("おすすめ: 標準運用: OpoフォームGCD後、RoF前またはRoF中に使う", MinLevel = 50)]
        Automatic,
        [Option("次のOpo GCD後に使う", MinLevel = 50)]
        ForceOpo,
        [Option("即使用", MinLevel = 50)]
        Force,
        [Option("使わない", MinLevel = 50)]
        Delay,
        [Option("ダウンタイム準備: 日", MinLevel = 60, Effect = 39)]
        DowntimeSolar,
        [Option("ダウンタイム準備: 月", MinLevel = 60, Effect = 39)]
        DowntimeLunar,
        [Option("Form Shiftの効果がない時に即使用", MinLevel = 50)]
        ForceNoShift
    }
    public enum TCStrategy
    {
        [Option("使わない")]
        None,
        [Option("おすすめ: 近接範囲外にいる時に使う", Targets = ActionTargets.Party | ActionTargets.Hostile)]
        GapClose
    }
    public enum BlitzStrategy
    {
        [Option("おすすめ: 自動使用: RoF中優先、RoFに入らない場合は期限直前まで温存")]
        Automatic,
        [Option("RoFが有効になるまで温存")]
        RoF,
        [Option("複数対象に当たるまで温存")]
        Multi,
        [Option("RoF中かつ複数対象に当たるまで温存")]
        MultiRoF,
        [Option("使わない")]
        Delay,
        [Option("即使用")]
        Force
    }
    public enum EngageStrategy
    {
        [Option("おすすめ: 対象にThunderclap")]
        TC,
        [Option("Sprintで近接入り")]
        Sprint,
        [Option("近接からDragon Kick先釣り")]
        FacepullDK,
        [Option("近接からDemolish先釣り")]
        FacepullDemo,
        [Option("何もしない")]
        None
    }

    public enum Form { None, OpoOpo, Raptor, Coeurl }
    private readonly record struct MNKTuningProfile(
        float MajorBurstMeleeSafeGCDs,
        float PBMeleeSafeGCDs,
        float EvenPreRoFPBStartThreshold,
        float RoFBrotherhoodResyncWindow,
        float TargetAvailableUptimeRequired,
        float PotionFutureWindowLeeway,
        float RoWTwoMinuteHoldWindow,
        float RoWUptimeRequired,
        float RoWEndBurnLeeway,
        float LastRoFWindowLeeway,
        float LastBrotherhoodWindowLeeway,
        float LastPotionWindowLeeway);
    private enum MNKContentClass
    {
        Default,
        DancingMad,
        Ultimate,
        Alliance
    }

    public enum GCDPriority
    {
        None = 0,
        Meditate = 1,
        WindRanged = 100,
        FireRanged = 200,
        Basic = 300,
        BasicSaver = 310,
        BasicSpender = 320,
        AOE = 400,
        SSS = 500,
        Blitz = 600,
        FiresReply = 700,
        WindsReply = 800,
        PR = 900,
        MeditateForce = 950,
        BlitzNow = 1100,
        // an expiring reply must outrank an urgent blitz: the blitz keeps its window, the reply buff does not
        FRExpire = 1110,
        WRExpire = 1120,
    }

    // some monk OGCDs will be queued with higher prio than what user presses manually - the rotation is very drift-sensitive and monk has much less time to weave than other classes do
    public enum OGCDPriority
    {
        None = 0,
        TrueNorth = 100,
        TFC = 150,
        Potion = 200,
        RiddleOfEarth = 250,
        RiddleOfWind = 300,
        TFCBrotherhood = 2000,
        ManualOGCD = 2001, // included for reference, not used here - actual value is 2001 + Low (2000) = 4001
        RiddleOfFire = 2002,
        Brotherhood = 2003,
        PerfectBalance = 2004,
        TFCOvercap = 2005
    }

    private enum BurstPhase
    {
        None,
        HoldForBurst,
        BurstReady,
        BurstNow,
        RecoverBurst
    }

    private enum NadiCyclePhase
    {
        ForcedLunar,
        AutomaticSecondLunar,
        ForcedSolar,
        LunarSolar
    }

    private enum FightEndEstimateConfidence
    {
        None,
        WeakHP,
        StableHP,
        PlannerPhase,
        PlannerFinal
    }

    private readonly record struct MeleeUptimePrediction(
        bool CanMeleeNow,
        bool CanMeleeNextGCD,
        bool CanReachSafeMeleeNextGCD,
        float MeleeLossIn,
        float MeleeResumeIn,
        float ForcedOutDuration,
        bool ForcedOutSoon);

    private readonly record struct CrossCombatPBBurstSnapshot(
        DateTime EndedAt,
        bool HaveLunar,
        bool HaveSolar,
        int BeastCount,
        int AutomaticPBBurstIndex,
        int AutomaticPBUsesInBurst,
        int ActivatedRoFBursts,
        float RoFReadyIn,
        float BrotherhoodReadyIn,
        float PBReadyIn,
        uint ContentId);

    private readonly record struct KilltimeModel(
        float? PhaseEnd,
        float? FightEnd,
        float? BurstHorizon,
        bool FinalPhase,
        bool PhaseEndsWithDowntime,
        FightEndEstimateConfidence Confidence);

    private readonly record struct TargetKilltimeModel(
        float? FightEnd,
        FightEndEstimateConfidence Confidence);

    private readonly record struct BurstPlan(
        BurstPhase Phase,
        bool EvenWindow,
        bool RoFLate,
        bool PBPreferred,
        bool PBDeadline,
        int AutomaticPBTargetUses,
        int AutomaticPBUsesInBurst,
        int AutomaticPBBurstIndex,
        bool UseBrotherhoodNow,
        bool UseRoFNow,
        bool UseBrotherhoodFixedTimingNow,
        bool UseRoFFixedTimingNow,
        bool UseRoFRecastNow,
        bool UsePBAutomaticNow,
        bool UsePBForNadiRepairNow,
        NadiCyclePhase PBNadiRepairPhase,
        float NextRoFIn,
        float NextBrotherhoodIn,
        float? EstimatedFightEnd,
        bool LastPBWindow,
        bool LastBrotherhoodWindow,
        bool LastRoFWindow,
        bool LastPotionWindow);

    private readonly record struct BlitzDecision(bool ConsumeFormlessFirst, bool Use, bool UrgentNow);

    private enum MechanicHoldReason
    {
        None,
        LookAway,
        ForcedOutOfMelee,
        TargetLost,
        TargetNotMeleeReachable
    }

    private static readonly SmartRotationConfig _smartRotationConfig = Service.Config.Get<SmartRotationConfig>();

    public int Chakra; // 0-5 (0-10 during Brotherhood)
    public readonly BeastChakraType[] BeastChakra = new BeastChakraType[3];
    private int _beastCount;
    public int OpoStacks; // 0-1
    public int RaptorStacks; // 0-1
    public int CoeurlStacks; // 0-2
    public NadiFlags Nadi;

    public Form CurrentForm;
    public Form EffectiveForm;
    public float FormLeft; // 0 if no form, 30 max

    public float BlitzLeft; // 20 max
    public float PerfectBalanceLeft => PerfectBalance.Left;
    public float FormShiftLeft; // 30 max
    public float BrotherhoodLeft; // 20 max - from ANY monk in the party, so this is the damage buff
    // our own cast only: another monk's Brotherhood lands on us too and must not be mistaken for our own cooldown
    public float OwnBrotherhoodLeft;
    public float FireLeft; // 20 max
    public float EarthLeft; // 10 max
    public float EarthsReplyLeft; // 30 max, probably doesnt belong in autorotation
    public float FiresReplyLeft; // 20 max
    public float WindsReplyLeft; // 15 max

    public int NumBlitzTargets;
    public int NumAOETargets;
    public int NumLineTargets;
    public float? EstimatedTargetEnd;
    public float? EstimatedPhaseEnd;
    public float? EstimatedDowntimeStart;
    public float? EstimatedFightEnd;
    public float? EstimatedBurstHorizon;

    private Enemy? BestBlitzTarget;
    private Enemy? BestRangedTarget; // fire's reply
    private Enemy? BestLineTarget; // enlightenment, wind's reply
    private ulong _fightEstimateTargetID;
    private float _fightEstimateSampleTime;
    private float _fightEstimateSampleHP = 1;
    private float _fightEstimateHPDrainPerSecond;
    private int _fightEstimateStableSamples;
    private object? _fightEstimatePlannerModuleRef;
    private object? _fightEstimatePlannerPlanRef;
    private int _fightEstimatePlannerTimingHash;
    private global::BossMod.StateMachineTree? _fightEstimatePlannerTree;
    private int _activatedRoFBursts;
    private int _automaticPBBurstIndex = -1;
    private int _automaticPBUsesInBurst;
    private DateTime _nextScheduledRoFBurstAt;
    private DateTime _nextScheduledBrotherhoodBurstAt;
    private DateTime _openerRoFOffsetProtectedUntil;
    private float _openerThunderclapLandingOffset = -1;
    private bool _wasDeadForBurstAlignment;
    private bool _hadRaisePenaltyForBurstAlignment;
    private bool _wasBasicAndChakraOvercapOnly;
    private bool _wasInCombatForPBBurstTracking;
    private float _previousFireLeft;
    private float _previousBrotherhoodLeft;
    private float _previousOwnBrotherhoodLeft;
    private float _previousPerfectBalanceLeft;
    private int _automaticPBBurstStartNadiIndex = -1;
    private NadiFlags _automaticPBBurstStartNadi;
    private AID _countedAutomaticPBBlitz = AID.None;
    private AID _previousNextGCD;
    private MechanicHoldReason _mechanicHoldReason;
    private bool _prioritizePendingBlitzAfterMechanicHold;
    private float _mechanicHoldStartFireLeft;
    private float _mechanicHoldStartBrotherhoodLeft;
    private float _mechanicHoldStartPerfectBalanceLeft;
    private float _mechanicHoldStartBlitzLeft;
    private int _mechanicHoldStartBeastCount;
    private NadiFlags _mechanicHoldStartNadi;
    private Form _mechanicHoldStartForm;
    private float _mechanicHoldStartFormLeft;
    private DateTime _nextMeleePredictionRefreshAt;
    private DateTime _nextRoFBrotherhoodSyncAt;
    private DateTime _roFBrotherhoodSyncRoFAnchor;
    private DateTime _roFBrotherhoodSyncBrotherhoodAnchor;
    private ulong _roFBrotherhoodSyncTargetID;
    private bool _roFBrotherhoodSyncAllowLongRange;
    private int _roFBrotherhoodSyncActivatedRoFBursts;
    private bool _synergyDoubleLunarMode;
    private ulong _meleePredictionTargetID;
    private int _meleePredictionForbiddenZoneCount;
    private int _meleePredictionObstacleCount;
    private NadiCyclePhase? _activePBNadiCyclePhase;
    private bool _forcePBOnRotationModeResume;
    private bool _forceEvenPhantomRushAfterDeath;
    private int _skippedOddRoFStandalonePBBurstIndex = -1;
    private DateTime _skippedOddRoFStandalonePBExpiresAt;
    private CrossCombatPBBurstSnapshot? _crossCombatPBBurstSnapshot;
    private MNKEncounterHintStrategy _encounterHint;
    private FightEndEstimateConfidence EstimatedFightEndConfidence;
    private MeleeUptimePrediction _meleePrediction;
    private ExternalMechanicHintSnapshot _mechanicHint;
    private bool _hasMechanicHint;

    public bool HaveLunar => (Nadi & NadiFlags.Lunar) != 0;
    public bool HaveSolar => (Nadi & NadiFlags.Solar) != 0;
    public bool HaveBothNadi => HaveLunar && HaveSolar;
    // status scans run several times a frame in combat: plain loops (LINQ boxed an enumerator per call)
    private bool HasRaiseOrWeaknessPenalty
    {
        get
        {
            foreach (var s in Player.Statuses)
                if (s.ID is 43u or 44u or 418u or 2648u)
                    return true;
            return false;
        }
    }

    private bool HasTranscendentStatus()
    {
        foreach (var s in Player.Statuses)
            if (s.ID is 418u or 2648u)
                return true;
        return false;
    }

    public float EffectiveDowntimeIn => Math.Max(0, EffectiveMeleeUptimeIn - GetApplicationDelay(AID.SixSidedStar));

    public bool BasicAndChakraOvercapOnly(in Strategy strategy) => strategy.RotationMode.Value == RotationModeStrategy.BasicAndChakraOvercap;
    // The normal rotation spends chakra at 5, so 5 is also the point at which holding it any longer can only overflow.
    // The previous "10 while Brotherhood is up" made RotationMode.BasicAndChakraOvercap sit on chakra for the whole
    // Brotherhood window - the one stretch where it builds fastest. 5 is correct if the cap is 5 and merely early
    // (never lossy - Forbidden Chakra inside Brotherhood is always worth pressing) if it really is 10 there.
    private const int ChakraOvercapThreshold = 5;

    public int BuffedGCDsLeft => FireLeft > GCD ? (int)MathF.Floor((FireLeft - GCD) / AttackGCDLength) + 1 : 0;

    private bool HaveTarget;
    private bool HaveMeleeTarget;

    public bool TransitionToSingleSoon()
    {
        if (_encounterHint == MNKEncounterHintStrategy.BossReturn)
            return true;
        if (NumAOETargets > 3)
            return false;

        return ShortAOERemaining() is float remaining
            && remaining <= AttackGCDLength * ShortAOETransitionGCDs + AnimationLockDelay;
    }

    private bool AOEShouldLastSeveralGCDs()
        => _encounterHint is MNKEncounterHintStrategy.Trash or MNKEncounterHintStrategy.AllianceTrash or MNKEncounterHintStrategy.MajorAdd
            || ShortAOERemaining() is float remaining && remaining > AttackGCDLength * AOELastsSeveralGCDs + AnimationLockDelay;

    private float? ShortAOERemaining()
    {
        float? remaining = null;
        if (EstimatedDowntimeStart is float downtimeStart)
            remaining = Math.Min(remaining ?? float.MaxValue, downtimeStart - CombatTimer);
        if (FightEndBurnEstimateTrusted && EstimatedTargetEnd is float targetEnd)
            remaining = Math.Min(remaining ?? float.MaxValue, targetEnd - CombatTimer);
        if (DowntimeIn > 0 && DowntimeIn < float.MaxValue / 2)
            remaining = Math.Min(remaining ?? float.MaxValue, DowntimeIn);

        return remaining;
    }
    public bool UseAOE => NumAOETargets >= AOEBreakpoint;
    private bool AllianceTrashBurstLikely => NumAOETargets >= 3 && AOEShouldLastSeveralGCDs();

    private bool IsEnlightenmentTarget(Actor primary, Actor other) => TargetInAOERect(other, Player.Position, Player.DirectionTo(primary), LineTargetRange, LineAOEHalfWidth);
    private PositionCheck? _enlightenmentTargetCheck; // the method group allocated a delegate every frame
    private PositionCheck EnlightenmentTargetCheck => _enlightenmentTargetCheck ??= IsEnlightenmentTarget;

    private AID BestOpoGCD()
    {
        if (Unlocked(AID.DragonKick) && OpoStacks == 0)
            return AID.DragonKick;

        return AID.Bootshine;
    }

    private AID BestRaptorGCD()
    {
        if (Unlocked(AID.TrueStrike) && RaptorStacks > 0)
            return AID.TrueStrike;

        if (Unlocked(AID.TwinSnakes))
            return AID.TwinSnakes;

        return BestOpoGCD();
    }

    private AID BestCoeurlGCD()
    {
        if (Unlocked(AID.SnapPunch) && CoeurlStacks > 0)
            return AID.SnapPunch;

        if (Unlocked(AID.Demolish))
            return AID.Demolish;

        if (Unlocked(AID.SnapPunch))
            return AID.SnapPunch;

        return BestRaptorGCD();
    }

    private AID BestAOEGCD()
        => EffectiveForm switch
        {
            Form.Coeurl when Unlocked(AID.Rockbreaker) => AID.Rockbreaker,
            Form.Raptor when Unlocked(AID.FourPointFury) => AID.FourPointFury,
            _ when Unlocked(AID.ArmOfTheDestroyer) => AID.ArmOfTheDestroyer,
            _ => AID.None
        };

    private AID BestPBGCDForBeast(BeastChakraType beast)
        => beast switch
        {
            BeastChakraType.Coeurl => Unlocked(AID.Rockbreaker) ? AID.Rockbreaker : AID.None,
            BeastChakraType.Raptor => Unlocked(AID.FourPointFury) ? AID.FourPointFury : AID.None,
            _ => Unlocked(AID.ArmOfTheDestroyer) ? AID.ArmOfTheDestroyer : AID.None
        };

    private static bool IsBuffedOpoGCD(AID action) => action is AID.Bootshine or AID.LeapingOpo;
    private static bool IsBuffedRaptorGCD(AID action) => action is AID.TrueStrike or AID.RisingRaptor;
    private static bool IsBuffedCoeurlGCD(AID action) => action is AID.SnapPunch or AID.PouncingCoeurl;

    private void FormShift(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Unlocked(AID.FormShift) || PerfectBalanceLeft > 0)
            return;

        var prio = GCDPriority.None;

        switch (strategy.FormShift.Value)
        {
            case OffensiveStrategy.Force:
                prio = GCDPriority.MeditateForce;
                break;
            case OffensiveStrategy.Automatic:
                if (UptimeIn > Math.Max(GCD + AttackGCDLength, FormShiftLeft) && UptimeIn < 25)
                    prio = GCDPriority.Meditate;
                break;
        }

        PushGCD(AID.FormShift, Player, prio);
    }

    private void UseTN(in Strategy strategy, Enemy? primaryTarget)
    {
        switch (strategy.TrueNorth.Value)
        {
            case OffensiveStrategy.Automatic:
                if (primaryTarget == null || TrueNorthLeft > 0)
                    return;

                if (NextPositionalImminent && !NextPositionalCorrect && Player.DistanceToHitbox(primaryTarget) < 6)
                    PushOGCD(AID.TrueNorth, Player, OGCDPriority.TrueNorth, Math.Max(0, GCD - TrueNorthLateWeaveDelay));
                break;
            case OffensiveStrategy.Force:
                if (TrueNorthLeft == 0)
                    PushOGCD(AID.TrueNorth, Player, OGCDPriority.TrueNorth);
                break;
        }
    }

    private (Form, float) DetermineForm()
    {
        if (PerfectBalanceLeft > 0)
            return (Form.None, 0);

        var s = StatusLeft(SID.OpoOpoForm);
        if (s > 0)
            return (Form.OpoOpo, s);
        s = StatusLeft(SID.RaptorForm);
        if (s > 0)
            return (Form.Raptor, s);
        s = StatusLeft(SID.CoeurlForm);

        return s > 0 ? (Form.Coeurl, s) : (Form.None, 0);
    }

    private bool BossReturnSingleTargetPrep => _encounterHint == MNKEncounterHintStrategy.BossReturn;
    private bool EncounterHintAllowsAutomaticBurst(in Strategy strategy) => _encounterHint switch
    {
        MNKEncounterHintStrategy.Trash or MNKEncounterHintStrategy.BossReturn or MNKEncounterHintStrategy.HoldBurst => false,
        MNKEncounterHintStrategy.AllianceTrash => AllianceTrashBurstLikely,
        MNKEncounterHintStrategy.Off => true,
        _ => true
    };

    private MNKEncounterHintStrategy ResolveEncounterHint(in Strategy strategy, Enemy? primaryTarget)
    {
        var configured = strategy.EncounterHint.Value;
        if (configured != MNKEncounterHintStrategy.Automatic || !Player.InCombat)
            return configured;

        var plannerDowntimeIn = EstimatedDowntimeStart is float downtimeStart && PhaseBoundaryEstimateTrusted
            ? Math.Max(0, downtimeStart - CombatTimer)
            : float.MaxValue;

        if (HaveTarget && plannerDowntimeIn <= AttackGCDLength * ShortAOETransitionGCDs + AnimationLockDelay)
        {
            if (TryGetResourceBurnHorizon(strategy, out var resourceBurnHorizon) && ResourceBurnHorizonIsPhaseEnd(resourceBurnHorizon))
                return MNKEncounterHintStrategy.Boss;

            return MNKEncounterHintStrategy.BossReturn;
        }

        var activeBossModule = Bossmods.ActiveModule?.StateMachine.ActivePhase != null;
        var primaryActor = Bossmods.ActiveModule?.PrimaryActor;
        var moduleBossTargetable = primaryActor is { IsTargetable: true, IsDead: false, PendingDead: false };
        var moduleBossMissing = primaryActor != null && !moduleBossTargetable;
        var targetIsModuleBoss = primaryTarget?.Actor != null && primaryActor != null && primaryTarget.Actor.InstanceID == primaryActor.InstanceID;
        var relevantTargets = 0;
        var priorityTargets = 0;
        foreach (var target in Hints.PotentialTargets)
        {
            if (!target.Actor.IsTargetable || target.Actor.IsDead || target.Priority < 0)
                continue;

            ++relevantTargets;
            if (target.Priority > 0)
                ++priorityTargets;
        }

        if (activeBossModule)
        {
            if (targetIsModuleBoss)
                return MNKEncounterHintStrategy.Boss;

            if (moduleBossMissing)
            {
                if (NumAOETargets >= 3 && priorityTargets >= 3)
                    return MNKEncounterHintStrategy.AllianceTrash;

                if (primaryTarget?.Priority > 0)
                    return MNKEncounterHintStrategy.MajorAdd;

                return MNKEncounterHintStrategy.HoldBurst;
            }

            if (NumAOETargets >= 3 || relevantTargets >= 3)
                return MNKEncounterHintStrategy.AllianceTrash;

            if (primaryTarget?.Priority >= 0)
                return MNKEncounterHintStrategy.MajorAdd;

            return MNKEncounterHintStrategy.HoldBurst;
        }

        if (NumAOETargets >= 3 || relevantTargets >= 3)
            return MNKEncounterHintStrategy.Trash;

        return primaryTarget?.Priority >= 0 ? MNKEncounterHintStrategy.Boss : MNKEncounterHintStrategy.Automatic;
    }

    private static readonly MNKTuningProfile DefaultTuningProfile = new(
        MajorBurstMeleeSafeGCDs,
        PBMeleeSafeGCDs,
        EvenPreRoFPBStartThreshold,
        RoFBrotherhoodResyncWindow,
        TargetAvailableUptimeRequired,
        PotionFutureWindowLeeway,
        RoWTwoMinuteHoldWindow,
        RoWUptimeRequired,
        RoWEndBurnLeeway,
        LastRoFWindowLeeway,
        LastBrotherhoodWindowLeeway,
        LastPotionWindowLeeway);
    private static readonly MNKTuningProfile UltimateTuningProfile = DefaultTuningProfile with
    {
        TargetAvailableUptimeRequired = 12f,
        PotionFutureWindowLeeway = 15f,
        RoWTwoMinuteHoldWindow = 25f,
        RoWUptimeRequired = 10f,
        LastRoFWindowLeeway = 45f,
        LastBrotherhoodWindowLeeway = 90f,
        LastPotionWindowLeeway = 180f
    };
    private static readonly MNKTuningProfile DancingMadTuningProfile = DefaultTuningProfile with
    {
        MajorBurstMeleeSafeGCDs = 6f,
        PBMeleeSafeGCDs = 4f,
        EvenPreRoFPBStartThreshold = 5.5f,
        RoFBrotherhoodResyncWindow = 210f,
        TargetAvailableUptimeRequired = 20f,
        PotionFutureWindowLeeway = 15f,
        RoWTwoMinuteHoldWindow = 45f,
        RoWUptimeRequired = 12f,
        RoWEndBurnLeeway = 20f,
        LastRoFWindowLeeway = 45f,
        LastBrotherhoodWindowLeeway = 105f,
        LastPotionWindowLeeway = 210f
    };
    private readonly record struct DancingMadTopLogActionWindow(float Time, AID Action);
    private static readonly DancingMadTopLogActionWindow[] DancingMadTopLogActionWindows =
    [
        new(1.834f, AID.Brotherhood),
        new(2.634f, AID.RiddleOfFire),
        new(3.348f, AID.PerfectBalance),
        new(4.729f, AID.RiddleOfWind),
        new(19.245f, AID.PerfectBalance),
        new(57.860f, AID.PerfectBalance),
        new(62.632f, AID.RiddleOfFire),
        new(95.246f, AID.RiddleOfWind),
        new(120.601f, AID.PerfectBalance),
        new(121.849f, AID.Brotherhood),
        new(122.606f, AID.RiddleOfFire),
        new(137.242f, AID.PerfectBalance),
        new(182.632f, AID.RiddleOfFire),
        new(183.392f, AID.PerfectBalance),
        new(210.121f, AID.RiddleOfWind),
        new(241.253f, AID.PerfectBalance),
        new(243.262f, AID.Brotherhood),
        new(244.066f, AID.RiddleOfFire),
        new(255.419f, AID.PerfectBalance),
        new(300.208f, AID.RiddleOfWind),
        new(303.828f, AID.PerfectBalance),
        new(304.542f, AID.RiddleOfFire),
        new(363.311f, AID.Brotherhood),
        new(364.604f, AID.RiddleOfFire),
        new(424.560f, AID.RiddleOfFire),
        new(426.037f, AID.RiddleOfWind),
        new(429.251f, AID.PerfectBalance),
        new(479.561f, AID.PerfectBalance),
        new(483.451f, AID.Brotherhood),
        new(484.565f, AID.RiddleOfFire),
        new(493.805f, AID.PerfectBalance),
        new(518.099f, AID.RiddleOfWind),
        new(540.032f, AID.PerfectBalance),
        new(544.590f, AID.RiddleOfFire),
        new(600.518f, AID.PerfectBalance),
        new(604.803f, AID.Brotherhood),
        new(605.562f, AID.RiddleOfFire),
        new(608.953f, AID.RiddleOfWind),
        new(617.523f, AID.PerfectBalance),
        new(660.650f, AID.PerfectBalance),
        new(666.240f, AID.RiddleOfFire),
        new(751.960f, AID.PerfectBalance),
        new(755.840f, AID.Brotherhood),
        new(756.597f, AID.RiddleOfFire),
        new(758.070f, AID.RiddleOfWind),
        new(768.527f, AID.PerfectBalance),
        new(812.862f, AID.PerfectBalance),
        new(818.270f, AID.RiddleOfFire),
        new(849.149f, AID.RiddleOfWind),
        new(900.206f, AID.PerfectBalance),
        new(906.015f, AID.Brotherhood),
        new(906.730f, AID.RiddleOfFire),
        new(914.184f, AID.PerfectBalance),
        new(939.384f, AID.RiddleOfWind),
        new(960.839f, AID.PerfectBalance),
        new(966.738f, AID.RiddleOfFire),
        new(1029.934f, AID.RiddleOfFire),
        new(1034.539f, AID.RiddleOfWind),
        new(1036.238f, AID.PerfectBalance),
        new(1090.510f, AID.PerfectBalance),
        new(1094.492f, AID.Brotherhood),
        new(1095.207f, AID.RiddleOfFire),
        new(1104.623f, AID.PerfectBalance)
    ];
    private static readonly float[] DancingMadTopLogPotionWindows = [114.756f, 475.404f, 748.074f, 1088.587f];
    private const float DancingMadTopLogActionWindowBefore = 0.5f;
    private const float DancingMadTopLogActionWindowAfter = 4f;
    private const float DancingMadTopLogActionHoldWindow = 10f;
    private const float DancingMadTopLogPotionWindowBefore = 4f;
    private const float DancingMadTopLogPotionWindowAfter = 4f;
    private const float DancingMadTopLogPotionHoldWindow = 20f;
    private const float DancingMadTopLogPotionCooldown = 270f;
    private static readonly MNKTuningProfile AllianceTuningProfile = DefaultTuningProfile with
    {
        TargetAvailableUptimeRequired = 12f,
        PotionFutureWindowLeeway = 10f,
        RoWTwoMinuteHoldWindow = 30f,
        RoWUptimeRequired = 10f,
        LastRoFWindowLeeway = 45f,
        LastBrotherhoodWindowLeeway = 90f,
        LastPotionWindowLeeway = 180f
    };

    private MNKTuningProfile ActiveTuningProfile => ResolveActiveTuningProfile();
    private MNKTuningProfile ResolveActiveTuningProfile()
    {
        return ResolveContentTuningProfile(CurrentTuningContentId);
    }

    private uint CurrentTuningContentId => World.CurrentCFCID;
    private bool DancingMadTopLogProfileEnabled(in Strategy strategy)
        => strategy.SkillRotation.Value == SkillRotationMode.DancingMad
        && CurrentTuningContentId == 1094;

    private float DancingMadTopLogTime => Math.Max(0, CombatTimer);

    private bool TryGetDancingMadTopLogActionWindow(in Strategy strategy, AID action, out DancingMadTopLogActionWindow activeWindow)
    {
        activeWindow = default;
        if (!DancingMadTopLogProfileEnabled(strategy))
            return false;

        var time = DancingMadTopLogTime;
        foreach (var window in DancingMadTopLogActionWindows)
            if (window.Action == action
                && time >= window.Time - DancingMadTopLogActionWindowBefore
                && time <= window.Time + DancingMadTopLogActionWindowAfter)
            {
                activeWindow = window;
                return true;
            }

        return false;
    }

    private bool IsInDancingMadTopLogActionWindow(in Strategy strategy, AID action)
        => TryGetDancingMadTopLogActionWindow(strategy, action, out _);

    private static float DancingMadTopLogActionCooldown(AID action)
        => action switch
        {
            AID.PerfectBalance => PerfectBalanceCooldown,
            AID.RiddleOfFire => RoFCooldown,
            AID.Brotherhood => BrotherhoodCooldown,
            AID.RiddleOfWind => RoWCooldown,
            _ => 0
        };

    private static int DancingMadTopLogRoFBurstIndexAt(float actionTime)
    {
        var bestBurstIndex = -1;
        var bestDistance = float.MaxValue;
        var burstIndex = 0;
        foreach (var window in DancingMadTopLogActionWindows)
        {
            if (window.Action != AID.RiddleOfFire)
                continue;

            var distance = Math.Abs(window.Time - actionTime);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestBurstIndex = burstIndex;
            }

            ++burstIndex;
        }

        return bestBurstIndex;
    }

    private bool TryGetDancingMadTopLogPBPlan(in Strategy strategy, out int burstIndex, out int targetUses)
    {
        burstIndex = -1;
        targetUses = 0;
        if (!TryGetDancingMadTopLogActionWindow(strategy, AID.PerfectBalance, out var pbWindow))
            return false;

        burstIndex = DancingMadTopLogRoFBurstIndexAt(pbWindow.Time);
        if (burstIndex < 0)
            return false;

        foreach (var window in DancingMadTopLogActionWindows)
            if (window.Action == AID.PerfectBalance && DancingMadTopLogRoFBurstIndexAt(window.Time) == burstIndex)
                ++targetUses;

        targetUses = Math.Clamp(targetUses, 1, 2);
        return true;
    }

    private bool ShouldHoldDancingMadTopLogAction(in Strategy strategy, AID action)
    {
        if (!DancingMadTopLogProfileEnabled(strategy))
            return false;

        var time = DancingMadTopLogTime;
        DancingMadTopLogActionWindow? previousWindow = null;
        foreach (var window in DancingMadTopLogActionWindows)
        {
            if (window.Action != action)
                continue;

            if (time > window.Time + DancingMadTopLogActionWindowAfter)
            {
                previousWindow = window;
                continue;
            }

            var holdStart = Math.Max(0, window.Time - DancingMadTopLogActionHoldWindow);
            var cooldown = DancingMadTopLogActionCooldown(action);
            if (previousWindow is { } previous && cooldown > 0)
                holdStart = Math.Min(holdStart, Math.Max(0, previous.Time + cooldown - DancingMadTopLogActionWindowBefore));
            else if (previousWindow == null)
                holdStart = 0;

            return time >= holdStart
                && time < window.Time - DancingMadTopLogActionWindowBefore;
        }

        return false;
    }

    private bool IsInDancingMadTopLogPotionWindow(in Strategy strategy)
    {
        if (!DancingMadTopLogProfileEnabled(strategy))
            return false;

        var time = DancingMadTopLogTime;
        foreach (var window in DancingMadTopLogPotionWindows)
            if (time >= window - DancingMadTopLogPotionWindowBefore
                && time <= window + DancingMadTopLogPotionWindowAfter)
                return true;

        return false;
    }

    private bool ShouldHoldDancingMadTopLogPotion(in Strategy strategy)
    {
        if (!DancingMadTopLogProfileEnabled(strategy))
            return false;

        var time = DancingMadTopLogTime;
        for (var index = 0; index < DancingMadTopLogPotionWindows.Length; ++index)
        {
            var window = DancingMadTopLogPotionWindows[index];
            if (time > window + DancingMadTopLogPotionWindowAfter)
                continue;

            var holdStart = index == 0
                ? 0
                : Math.Min(
                    window - DancingMadTopLogPotionHoldWindow,
                    DancingMadTopLogPotionWindows[index - 1] + DancingMadTopLogPotionCooldown - DancingMadTopLogPotionWindowBefore);
            return time >= holdStart && time < window - DancingMadTopLogPotionWindowBefore;
        }

        return false;
    }

    private static MNKContentClass ClassifyContent(uint contentId)
    {
        return contentId switch
        {
            1094 => MNKContentClass.DancingMad,
            280 or 539 or 694 or 788 or 908 or 1006 => MNKContentClass.Ultimate,
            120 or 168 or 220 or 281 or 550 or 636 or 700 or 736 or 779 or 866 or 911 or 962 or 1015 or 1058 or 1117 => MNKContentClass.Alliance,
            _ => MNKContentClass.Default
        };
    }

    private static MNKTuningProfile ResolveContentTuningProfile(uint contentId)
    {
        return ClassifyContent(contentId) switch
        {
            MNKContentClass.DancingMad => DancingMadTuningProfile,
            MNKContentClass.Ultimate => UltimateTuningProfile,
            MNKContentClass.Alliance => AllianceTuningProfile,
            _ => DefaultTuningProfile
        };
    }

    private static float CrossCombatPBBurstSnapshotSeconds(uint contentId)
    {
        return ClassifyContent(contentId) switch
        {
            MNKContentClass.DancingMad => DancingMadCrossCombatPBBurstSnapshotSeconds,
            MNKContentClass.Ultimate => UltimateCrossCombatPBBurstSnapshotSeconds,
            MNKContentClass.Alliance => AllianceCrossCombatPBBurstSnapshotSeconds,
            _ => 0f
        };
    }

    private readonly DateTime[] _nextTuningLogAt = new DateTime[(int)MNKTuningLogStrategy.SSS + 1];

    private static bool TuningLogEnabled(MNKTuningLogStrategy selected, MNKTuningLogStrategy category)
        => selected == MNKTuningLogStrategy.All || selected == category;

    private static string TuningValue(float? value) => value is float v ? v.ToString("f1") : "-";

    private bool ShouldEmitTuningLog(in Strategy strategy, MNKTuningLogStrategy category)
    {
        var index = (int)category;
        if (!TuningLogEnabled(strategy.TuningLog.Value, category) || World.CurrentTime < _nextTuningLogAt[index])
            return false;

        _nextTuningLogAt[index] = World.CurrentTime.AddSeconds(TuningLogThrottleSeconds);
        return true;
    }

    // callers guard with ShouldEmitTuningLog(...) so the interpolated message (and no closure) is only built when it will be emitted
    private static void LogTuning(string tag, string message) => Service.Log($"[MNK][Tuning][{tag}] {message}");

    private static void LogGuide73(string tag, string message) => Service.Log($"[MNK][Guide73][{tag}] {message}");

    // FightRemaining (value-of-information experiment): switch for the rule below; the shared estimate (AIHints.FightRemaining) is
    // only published into the fight-end estimate once the fight is likely (EndsWithinProbability >= SharedPGate) to end within
    // SharedFightRemainingCap seconds, so the existing "last window" / end-of-fight burn consumers (their leeways go up to 70 s for
    // Riddle of Fire) see it, and nothing earlier changes. The end published is the time by which the fight has ended with probability
    // SharedPEnd (FightTimeEstimate.Quantile); 0.65 was the best of 0.1 / 0.25 / 0.35 / 0.5 / 0.65 / 0.8 on the noisy arms of the
    // combat matrix (the 10th percentile, LowerBound, dumped too early under noise and worsened a third of the scenarios).
    private const float SharedPEnd = 0.65f;
    private const float SharedPGate = 0.1f;
    private static bool UseSharedFightRemaining = true;
    private const float SharedFightRemainingCap = 70f;
    // true while EstimatedFightEnd comes from the shared estimate: the Riddle of Wind alignment counts keep their old inputs then
    // (counting RoW uses up to a shared end made things slightly worse in the experiment)
    private bool _fightEndFromShared;

    public float FightTimeRemaining => EstimatedFightEnd is float fightEnd ? Math.Max(0, fightEnd - CombatTimer) : float.MaxValue;

    private void ResetFightEstimate(Actor? target)
    {
        _fightEstimateTargetID = target?.InstanceID ?? 0;
        _fightEstimateSampleTime = CombatTimer;
        _fightEstimateSampleHP = target?.PendingHPRatio ?? 1;
        _fightEstimateHPDrainPerSecond = 0;
        _fightEstimateStableSamples = 0;
    }

    private static int PlannerTimingHash(global::BossMod.Autorotation.PlanExecution planner)
    {
        var hash = new HashCode();
        hash.Add(planner.Module.StateMachine.Phases.Count);
        if (planner.Plan?.PhaseDurations != null)
            foreach (var duration in planner.Plan.PhaseDurations)
                hash.Add(duration);
        return hash.ToHashCode();
    }

    private KilltimeModel EstimatePlannerKilltime()
    {
        var planner = Manager.Planner;
        if (planner == null || planner.Module.StateMachine.Phases.Count == 0)
            return default;

        var timingHash = PlannerTimingHash(planner);
        if (_fightEstimatePlannerTree == null || !ReferenceEquals(_fightEstimatePlannerModuleRef, planner.Module) || !ReferenceEquals(_fightEstimatePlannerPlanRef, planner.Plan) || _fightEstimatePlannerTimingHash != timingHash)
        {
            _fightEstimatePlannerTree = new(planner.Module.StateMachine);
            _fightEstimatePlannerTree.ApplyTimings(planner.Plan?.PhaseDurations);
            _fightEstimatePlannerModuleRef = planner.Module;
            _fightEstimatePlannerPlanRef = planner.Plan;
            _fightEstimatePlannerTimingHash = timingHash;
        }

        var tree = _fightEstimatePlannerTree;
        if (tree == null || tree.Phases.Count == 0)
            return default;

        var currentState = planner.FindCurrentStateData();
        var virtualTime = planner.GetVirtualTime(currentState);
        var fightEnd = CombatTimer + Math.Max(0, tree.TotalMaxTime - virtualTime);
        var phaseIndex = planner.Module.StateMachine.ActivePhaseIndex;
        if (phaseIndex < 0 || phaseIndex >= tree.Phases.Count)
            phaseIndex = tree.FindPhaseAtTime(Math.Clamp(virtualTime, 0f, tree.TotalMaxTime));

        var phaseEndTime = tree.Phases[phaseIndex].StartTime + tree.Phases[phaseIndex].Duration;
        var phaseEnd = CombatTimer + Math.Max(0, phaseEndTime - virtualTime);
        var finalPhase = phaseIndex + 1 >= tree.Phases.Count;
        var nextPhaseStartsWithDowntime = !finalPhase && planner.Module.StateMachine.Phases[phaseIndex + 1].Hint.HasFlag(global::BossMod.StateMachine.PhaseHint.StartWithDowntime);
        var burstHorizon = nextPhaseStartsWithDowntime ? Math.Min(fightEnd, phaseEnd) : fightEnd;

        return new(phaseEnd, fightEnd, burstHorizon, finalPhase, nextPhaseStartsWithDowntime, finalPhase ? FightEndEstimateConfidence.PlannerFinal : FightEndEstimateConfidence.PlannerPhase);
    }

    private TargetKilltimeModel EstimateTargetKilltime(Enemy? primaryTarget)
    {
        var target = primaryTarget?.Actor;
        if (target == null || target.HPMP.MaxHP == 0)
        {
            ResetFightEstimate(null);
            return default;
        }

        if (target.PendingDead)
        {
            // A dying target says nothing about when the fight ends: read as "the fight ends now" it made the last mob of a
            // dungeon pull (a Boss hint outside a module) trigger the end-of-fight burn and spend RoF, Brotherhood and a potion
            // on it. Nothing gains from a burst that lands on a target already dead.
            ResetFightEstimate(target);
            return default;
        }

        var currentHP = Math.Clamp(target.PendingHPRatio, 0, 1);
        if (_fightEstimateTargetID != target.InstanceID || CombatTimer < FightEstimateInitialGrace || currentHP > _fightEstimateSampleHP + FightEstimateHPRiseReset)
        {
            ResetFightEstimate(target);
            return default;
        }

        var dt = CombatTimer - _fightEstimateSampleTime;
        if (dt >= 1)
        {
            var delta = _fightEstimateSampleHP - currentHP;
            if (delta > FightEstimateMinDamageDelta)
            {
                var observedDrain = delta / dt;
                var previousDrain = _fightEstimateHPDrainPerSecond;
                if (previousDrain > 0 && Math.Abs(observedDrain - previousDrain) <= Math.Max(FightEstimateMinStableDrain, previousDrain * FightEstimateDrainVarianceTolerance))
                    ++_fightEstimateStableSamples;
                else
                    _fightEstimateStableSamples = 0;

                _fightEstimateHPDrainPerSecond = _fightEstimateHPDrainPerSecond > 0
                    ? _fightEstimateHPDrainPerSecond + (observedDrain - _fightEstimateHPDrainPerSecond) * FightEstimateDrainSmoothing
                    : observedDrain;
            }
            else if (delta < FightEstimateHealResetDelta)
            {
                _fightEstimateHPDrainPerSecond *= FightEstimateHealDrainDecay;
                _fightEstimateStableSamples = 0;
            }
            else
            {
                _fightEstimateStableSamples = 0;
            }

            _fightEstimateSampleTime = CombatTimer;
            _fightEstimateSampleHP = currentHP;
        }

        if (_fightEstimateHPDrainPerSecond <= FightEstimateMinStableDrain)
            return default;

        var fightEnd = CombatTimer + Math.Clamp(currentHP / _fightEstimateHPDrainPerSecond, 0, FightEstimateMaxClamp);
        var stable = _fightEstimateStableSamples >= FightEstimateStableSampleCount;
        var lowHP = currentHP <= FightEstimateStableLowHP;
        var closeEnd = fightEnd <= CombatTimer + FightEstimateStableCloseEndWindow;
        var confidence = stable && lowHP && closeEnd ? FightEndEstimateConfidence.StableHP : FightEndEstimateConfidence.WeakHP;
        return new(fightEnd, confidence);
    }

    private void UpdateFightEstimate(Enemy? primaryTarget)
    {
        if (!Player.InCombat)
        {
            ResetFightEstimate(null);
            EstimatedTargetEnd = null;
            EstimatedPhaseEnd = null;
            EstimatedDowntimeStart = null;
            EstimatedFightEnd = null;
            EstimatedBurstHorizon = null;
            EstimatedFightEndConfidence = FightEndEstimateConfidence.None;
            return;
        }

        var targetKilltime = EstimateTargetKilltime(primaryTarget);
        var targetEnd = targetKilltime.FightEnd;
        var plannerKilltime = EstimatePlannerKilltime();

        // Planner phase timings define the encounter-level routing; only tighten them with
        // per-target HP drain once we know we're already in the final phase.
        var fightEnd = plannerKilltime.FightEnd;
        var burstHorizon = plannerKilltime.BurstHorizon;
        var confidence = plannerKilltime.Confidence;
        if (plannerKilltime.FinalPhase && targetEnd is float finalTargetEnd && targetKilltime.Confidence == FightEndEstimateConfidence.StableHP)
        {
            fightEnd = fightEnd is float plannedFightEnd ? Math.Min(plannedFightEnd, finalTargetEnd) : finalTargetEnd;
            burstHorizon = burstHorizon is float plannedBurstEnd ? Math.Min(plannedBurstEnd, finalTargetEnd) : finalTargetEnd;
        }
        else if (fightEnd == null && targetEnd != null)
        {
            fightEnd = targetEnd;
            confidence = targetKilltime.Confidence;
        }

        // FightRemaining (value-of-information experiment): LowerBound ("at least this long") so that noise makes the dump earlier, not later.
        // Unknown / no finite point value (a Known estimate that says nothing reports float.MaxValue, with a LowerBound of 0 that must not
        // read as "the fight ends now") / still far from the end leaves the estimate exactly as it was.
        _fightEndFromShared = false;
        if (UseSharedFightRemaining && Hints.FightRemaining.Known && Hints.FightRemaining.RemainingSeconds < float.MaxValue / 2
            && Hints.FightRemaining.EndsWithinProbability(SharedFightRemainingCap) >= SharedPGate)
        {
            var sharedEnd = CombatTimer + Math.Max(0, Hints.FightRemaining.Quantile(SharedPEnd));
            if (fightEnd is not float existingEnd || sharedEnd < existingEnd)
            {
                fightEnd = sharedEnd;
                confidence = FightEndEstimateConfidence.PlannerFinal;
                _fightEndFromShared = true;
            }
            burstHorizon = burstHorizon is float existingHorizon ? Math.Min(existingHorizon, sharedEnd) : sharedEnd;
        }

        EstimatedTargetEnd = targetEnd;
        EstimatedPhaseEnd = plannerKilltime.PhaseEnd;
        EstimatedDowntimeStart = plannerKilltime.PhaseEndsWithDowntime ? plannerKilltime.PhaseEnd : null;
        EstimatedFightEnd = fightEnd;
        EstimatedBurstHorizon = burstHorizon ?? EstimatedFightEnd;
        EstimatedFightEndConfidence = EstimatedFightEnd != null ? confidence : FightEndEstimateConfidence.None;
    }

    private bool FightEndBurnEnabled(in Strategy strategy) => strategy.FightEnd.Value == FightEndStrategy.Automatic;
    private bool FightEndBurnEstimateTrusted => EstimatedFightEndConfidence == FightEndEstimateConfidence.PlannerFinal
        || EstimatedFightEndConfidence == FightEndEstimateConfidence.StableHP
        && _encounterHint is MNKEncounterHintStrategy.Boss or MNKEncounterHintStrategy.ForceBurst;
    private bool NadiProjectionEstimateTrusted => EstimatedFightEndConfidence is FightEndEstimateConfidence.StableHP or FightEndEstimateConfidence.PlannerPhase or FightEndEstimateConfidence.PlannerFinal;
    private bool PhaseBoundaryEstimateTrusted => EstimatedFightEndConfidence is FightEndEstimateConfidence.StableHP or FightEndEstimateConfidence.PlannerPhase or FightEndEstimateConfidence.PlannerFinal;
    private bool BurstHorizonEstimateTrusted => EstimatedFightEndConfidence is FightEndEstimateConfidence.StableHP or FightEndEstimateConfidence.PlannerPhase or FightEndEstimateConfidence.PlannerFinal;
    private float BurstHorizonRemaining => EstimatedBurstHorizon is float burstHorizon ? Math.Max(0, burstHorizon - CombatTimer) : float.MaxValue;
    private float PlannerDowntimeIn => EstimatedDowntimeStart is float downtimeStart && PhaseBoundaryEstimateTrusted ? Math.Max(0, downtimeStart - CombatTimer) : float.MaxValue;

    private const float TransientTargetLossSeconds = 8.5f;
    private static bool ValidHintTime(float value) => !float.IsNaN(value) && value >= 0 && value < float.MaxValue / 2;

    // A brief untargetable window (a jump, a short transition) outlives nothing we hold: forms, fury, beast chakra and
    // both Rumination procs all survive it. Only a loss long enough to break a sequence is worth planning around.
    private static bool IsTransientTargetLoss(in ExternalMechanicHintSnapshot hint)
        => ValidHintTime(hint.TargetReturnIn)
        && hint.TargetReturnIn - Math.Max(0, hint.TargetLossIn) <= TransientTargetLossSeconds;

    private float MechanicTargetLossIn()
        => _hasMechanicHint && ValidHintTime(_mechanicHint.TargetLossIn) && !IsTransientTargetLoss(_mechanicHint)
            ? _mechanicHint.TargetLossIn
            : float.MaxValue;

    private float EffectiveUptimeIn => Math.Min(Math.Min(DowntimeIn, PlannerDowntimeIn), MechanicTargetLossIn());
    private float EffectiveBurstUptimeIn => Math.Min(EffectiveUptimeIn, BurstHorizonIsPhaseEnd && BurstHorizonEstimateTrusted ? BurstHorizonRemaining : float.MaxValue);

    private bool TryGetPlannerPhaseBurnHorizon(out float horizon)
    {
        horizon = float.MaxValue;
        if (!Player.InCombat
            || !BurstHorizonEstimateTrusted
            || EstimatedBurstHorizon is not float burstHorizon
            || EstimatedDowntimeStart is not float downtimeStart
            || Math.Abs(burstHorizon - downtimeStart) > AnimationLockDelay)
            return false;

        if (EstimatedFightEnd is float fightEnd && burstHorizon >= fightEnd - AnimationLockDelay)
            return false;

        if (EstimatedFightEndConfidence is not FightEndEstimateConfidence.PlannerPhase and not FightEndEstimateConfidence.PlannerFinal
            && !FightEndBurnEstimateTrusted)
            return false;

        horizon = burstHorizon;
        return true;
    }

    private bool IsDancingMadP2ToP3Downtime(in Strategy strategy)
    {
        if (CurrentTuningContentId != 1094
            || HaveTarget
            || strategy.EncounterHint.Value != MNKEncounterHintStrategy.Automatic)
            return false;

        var stateMachine = Bossmods.ActiveModule?.StateMachine;
        return stateMachine?.ActivePhaseIndex == 2
            && stateMachine.ActivePhase?.Hint.HasFlag(global::BossMod.StateMachine.PhaseHint.StartWithDowntime) == true;
    }

    private bool TryGetDancingMadP1P2FallbackBurnHorizon(in Strategy strategy, out float horizon)
    {
        horizon = DancingMadP1P2DowntimeStart;
        if (CurrentTuningContentId != 1094
            || strategy.OpenerRoFOffset.Value != OpenerRoFOffsetStrategy.ZeroSecondBurst
            || !Player.InCombat
            || CombatTimer >= DancingMadP1P2DowntimeStart)
            return false;

        var remaining = DancingMadP1P2DowntimeStart - CombatTimer;
        return remaining <= RoFWindowBudget + ActiveTuningProfile.EvenPreRoFPBStartThreshold + AnimationLockDelay;
    }

    private bool ResourceBurnHorizonIsPhaseEnd(float horizon)
        => EstimatedFightEnd is not float fightEnd || horizon < fightEnd - AnimationLockDelay;

    private bool PhaseEndResourceBurnWindow(in Strategy strategy, float leeway)
        => TryGetResourceBurnHorizon(strategy, out var horizon)
        && ResourceBurnHorizonIsPhaseEnd(horizon)
        && horizon <= CombatTimer + leeway;

    private bool TryGetResourceBurnHorizon(in Strategy strategy, out float horizon)
    {
        horizon = float.MaxValue;
        if (!FightEndBurnEnabled(strategy) || !Player.InCombat)
            return false;

        var found = false;
        if (FightEndBurnEstimateTrusted && EstimatedFightEnd is float fightEnd)
        {
            horizon = fightEnd;
            found = true;
        }

        if (TryGetPlannerPhaseBurnHorizon(out var phaseBurnHorizon))
        {
            horizon = found ? Math.Min(horizon, phaseBurnHorizon) : phaseBurnHorizon;
            found = true;
        }
        else if (TryGetDancingMadP1P2FallbackBurnHorizon(strategy, out var fallbackHorizon))
        {
            horizon = found ? Math.Min(horizon, fallbackHorizon) : fallbackHorizon;
            found = true;
        }

        return found;
    }

    private bool EndBurnWindow(in Strategy strategy, float leeway = 0)
        => TryGetResourceBurnHorizon(strategy, out var horizon)
        && horizon <= CombatTimer + leeway;

    private float ResourceHorizonClamp(in Strategy strategy, float remaining)
    {
        if (TryGetResourceBurnHorizon(strategy, out var horizon))
            remaining = Math.Min(remaining, Math.Max(0, horizon - CombatTimer));

        // a Reply or a Blitz held past the target going away is a Reply or a Blitz lost, exactly like one held past
        // the end of the fight - so the announced loss clamps the same resources the burn horizon does
        var targetLossIn = MechanicTargetLossIn();
        if (targetLossIn < float.MaxValue / 2)
            remaining = Math.Min(remaining, targetLossIn);

        return remaining;
    }

    private bool TryGetNadiProjectionHorizon(out float horizon)
    {
        horizon = float.MaxValue;
        if (!NadiProjectionEstimateTrusted)
            return false;

        var found = false;
        if (EstimatedFightEnd is float fightEnd)
        {
            horizon = fightEnd;
            found = true;
        }

        if (TryGetPlannerPhaseBurnHorizon(out var phaseBurnHorizon))
        {
            horizon = found ? Math.Min(horizon, phaseBurnHorizon) : phaseBurnHorizon;
            found = true;
        }

        return found;
    }

    private bool MeaningfulForcedOut => _meleePrediction.ForcedOutSoon
        && (_meleePrediction.ForcedOutDuration >= AttackGCDLength || _meleePrediction.MeleeResumeIn >= float.MaxValue / 2);
    private float MechanicMeleeLossIn => MeaningfulForcedOut ? _meleePrediction.MeleeLossIn : float.MaxValue;
    private float EffectiveMeleeUptimeIn => Math.Min(EffectiveUptimeIn, MechanicMeleeLossIn);
    private float EffectiveMeleeBurstUptimeIn => Math.Min(EffectiveBurstUptimeIn, MechanicMeleeLossIn);

    private bool ShouldUseFullMeleePrediction(in Strategy strategy)
        => UseMechanicAIHints()
        && (strategy.BurstTiming.Value == BurstTimingStrategy.MeleeSafe
            || FireLeft > GCD
            || BrotherhoodLeft > GCD
            || PerfectBalanceLeft > GCD
            || ReadyIn(AID.RiddleOfFire) <= AttackGCDLength * 2 + AnimationLockDelay
            || ReadyIn(AID.Brotherhood) <= AttackGCDLength * 2 + AnimationLockDelay
            || strategy.TuningLog.Value is MNKTuningLogStrategy.MeleeSafe or MNKTuningLogStrategy.All);

    // The log-derived Dancing Mad script times its own holds, so mechanic hints stay out of it; every other rotation, in
    // Dancing Mad too, reads them like the rest of the jobs (this used to switch them off for the whole duty, whatever the track said).
    private bool _useMechanicAIHints = true;
    private bool UseMechanicAIHints() => _useMechanicAIHints;

    private MeleeUptimePrediction PredictCheapMeleeUptime(Actor target)
    {
        var playerInMelee = Player.DistanceToHitbox(target) <= MeleeRange;
        return new(playerInMelee, playerInMelee, playerInMelee, float.MaxValue, float.MaxValue, 0, false);
    }

    private MeleeUptimePrediction GetCachedMeleeUptimePrediction(in Strategy strategy, Actor? target)
    {
        if (target == null)
            return new(true, true, true, float.MaxValue, float.MaxValue, 0, false);

        if (!ShouldUseFullMeleePrediction(strategy))
            return PredictCheapMeleeUptime(target);

        var zoneCount = Hints.ForbiddenZones.Count;
        var obstacleCount = Hints.TemporaryObstacles.Count;
        if (_meleePredictionTargetID == target.InstanceID
            && _meleePredictionForbiddenZoneCount == zoneCount
            && _meleePredictionObstacleCount == obstacleCount
            && World.CurrentTime < _nextMeleePredictionRefreshAt)
            return _meleePrediction;

        var prediction = PredictMeleeUptime(target);
        _meleePrediction = prediction;
        _meleePredictionTargetID = target.InstanceID;
        _meleePredictionForbiddenZoneCount = zoneCount;
        _meleePredictionObstacleCount = obstacleCount;
        _nextMeleePredictionRefreshAt = World.CurrentTime.AddSeconds(MeleePredictionCacheSeconds);
        return prediction;
    }

    private MeleeUptimePrediction PredictMeleeUptime(Actor? target, float horizon = MeleePredictionHorizon)
    {
        if (target == null || Hints.ForbiddenZones.Count == 0 && Hints.TemporaryObstacles.Count == 0)
            return new(true, true, true, float.MaxValue, float.MaxValue, 0, false);

        var nextGCDAt = Math.Max(GCD, AnimationLockDelay);
        var playerInMelee = Player.DistanceToHitbox(target) <= MeleeRange;
        var canMeleeNow = playerInMelee && !ForbiddenAt(Player.Position, 0);
        var canReachSafeMeleeNextGCD = CanReachSafeMeleePointAt(target, nextGCDAt);
        var canMeleeNextGCD = playerInMelee && !ForbiddenAt(Player.Position, nextGCDAt) || canReachSafeMeleeNextGCD;
        var meleeLossIn = float.MaxValue;
        for (var t = 0f; t <= horizon; t += MeleePredictionSampleStep)
        {
            if (!CanReachSafeMeleePointAt(target, t + MovementSafetyBuffer))
            {
                meleeLossIn = t;
                break;
            }
        }

        var forcedOutSoon = meleeLossIn < float.MaxValue / 2;
        var meleeResumeIn = float.MaxValue;
        if (forcedOutSoon)
        {
            for (var t = meleeLossIn + MeleePredictionSampleStep; t <= horizon; t += MeleePredictionSampleStep)
            {
                if (CanReachSafeMeleePointAt(target, t))
                {
                    meleeResumeIn = t;
                    break;
                }
            }
        }

        var forcedOutDuration = forcedOutSoon && meleeResumeIn < float.MaxValue / 2 ? Math.Max(0, meleeResumeIn - meleeLossIn) : float.MaxValue;
        return new(canMeleeNow, canMeleeNextGCD, canReachSafeMeleeNextGCD, meleeLossIn, meleeResumeIn, forcedOutDuration, forcedOutSoon);
    }

    private bool CanReachSafeMeleePointAt(Actor target, float at)
    {
        if (Player.DistanceToHitbox(target) <= MeleeRange && !ForbiddenAt(Player.Position, at))
            return true;

        var moveTime = Math.Max(0, at - MovementSafetyBuffer);
        var maxMove = World.Client.MoveSpeed * moveTime;
        var maxMoveSq = maxMove * maxMove;
        // The prediction samples up to 80 times a frame and each sample tries the same 72 points around the target; the points are
        // built once per target position, and the cheap reach test runs before the zone scan (all three tests are pure, so the
        // order of the conjuncts does not change the answer).
        foreach (var point in MeleeCandidatePoints(target))
            if ((point - Player.Position).LengthSq() <= maxMoveSq && !ForbiddenAt(point, at) && PathIsSafe(Player.Position, point, at))
                return true;

        return false;
    }

    private readonly WPos[] _meleeCandidatePoints = new WPos[3 * SafeMeleeSamples];
    private ulong _meleeCandidateTarget;
    private WPos _meleeCandidateCenter;
    private float _meleeCandidateHitbox = -1;

    // the same points, in the same order, as the inner/middle/outer rings of SafeMeleeSamples each
    private ReadOnlySpan<WPos> MeleeCandidatePoints(Actor target)
    {
        if (target.InstanceID != _meleeCandidateTarget || target.Position != _meleeCandidateCenter || target.HitboxRadius != _meleeCandidateHitbox)
        {
            _meleeCandidateTarget = target.InstanceID;
            _meleeCandidateCenter = target.Position;
            _meleeCandidateHitbox = target.HitboxRadius;
            ReadOnlySpan<float> radiusOffsets = [SafeMeleeInnerRadiusOffset, SafeMeleeMiddleRadiusOffset, SafeMeleeOuterRadiusOffset];
            var n = 0;
            foreach (var radiusOffset in radiusOffsets)
            {
                var radius = target.HitboxRadius + radiusOffset;
                for (var i = 0; i < SafeMeleeSamples; ++i)
                {
                    var angle = MathF.PI * 2 * i / SafeMeleeSamples;
                    _meleeCandidatePoints[n++] = target.Position + new WDir(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                }
            }
        }
        return _meleeCandidatePoints;
    }

    private bool PathIsSafe(WPos from, WPos to, float at)
    {
        var delta = to - from;
        for (var i = 1; i <= PathSafetySteps; ++i)
        {
            var progress = i / (float)PathSafetySteps;
            if (ForbiddenAt(from + delta * progress, at * progress))
                return false;
        }

        return true;
    }

    private bool CanThunderclapSafely(Enemy? target, float at)
    {
        if (target == null)
            return false;

        var distance = Player.DistanceToHitbox(target);
        if (distance is <= MeleeRange or > GapCloseMaxRange)
            return false;

        var landingPosition = Player.Position + Player.AngleTo(target.Actor).ToDirection() * distance;
        return !ForbiddenAt(landingPosition, at)
            && PathIsSafe(Player.Position, landingPosition, at);
    }

    private bool CanThunderclapForAutomaticBurstStart(in Strategy strategy, Enemy? target)
        => strategy.TC.Value == TCStrategy.GapClose
        && Unlocked(AID.Thunderclap)
        && ReadyIn(AID.Thunderclap) <= AnimationLockDelay
        && CanThunderclapSafely(target, Math.Max(GCD, AnimationLockDelay));

    private bool CanStartAutomaticBurstAtMelee(in Strategy strategy, Enemy? target)
        => HaveMeleeTarget
        || _meleePrediction.CanMeleeNextGCD
        || CanThunderclapForAutomaticBurstStart(strategy, target);

    private bool ShouldHoldForLookAway(Actor? target)
    {
        if (target == null)
            return false;

        if (!UseMechanicAIHints())
            return false;

        if (!_smartRotationConfig.Enabled)
            return false;

        if (!_smartRotationConfig.AvoidGazes)
            return false;

        var targetDirection = Player.AngleTo(target);
        var deadline = World.FutureTime(_smartRotationConfig.MinTimeToAvoid);
        foreach (var forbiddenDirection in Hints.ForbiddenDirections)
            if ((forbiddenDirection.activation == default || forbiddenDirection.activation <= deadline)
                && targetDirection.AlmostEqual(forbiddenDirection.center, forbiddenDirection.halfWidth.Rad))
                return true;

        return false;
    }

    private MechanicHoldReason ResolveMechanicHoldReason(bool holdForLookAway, Enemy? rangedTarget, Enemy? meleeTarget)
    {
        var bestReason = MechanicHoldReason.None;
        var bestScore = 0f;
        ReadOnlySpan<MechanicHoldReason> reasons =
        [
            MechanicHoldReason.LookAway,
            MechanicHoldReason.TargetLost,
            MechanicHoldReason.ForcedOutOfMelee,
            MechanicHoldReason.TargetNotMeleeReachable
        ];

        foreach (var reason in reasons)
        {
            var score = MechanicHoldScore(reason, holdForLookAway, rangedTarget, meleeTarget);
            if (score > bestScore)
            {
                bestScore = score;
                bestReason = reason;
            }
        }

        return bestReason;
    }

    private float MechanicHoldScore(MechanicHoldReason reason, bool holdForLookAway, Enemy? rangedTarget, Enemy? meleeTarget)
        => reason switch
        {
            MechanicHoldReason.LookAway => holdForLookAway ? 100f : 0f,
            MechanicHoldReason.TargetLost => Player.InCombat && !HaveTarget ? 95f : 0f,
            MechanicHoldReason.ForcedOutOfMelee => UseMechanicAIHints() && HaveTarget && MeaningfulForcedOut ? 70f + MathF.Min(10f, Math.Max(0, GCD + AnimationLockDelay - _meleePrediction.MeleeLossIn)) : 0f,
            MechanicHoldReason.TargetNotMeleeReachable => HaveTarget && (meleeTarget == null || rangedTarget != null && Player.DistanceToHitbox(rangedTarget) > MeleeRange) ? 60f : 0f,
            _ => 0f
        };

    private void UpdateMechanicHoldState(in Strategy strategy, MechanicHoldReason reason)
    {
        if (_mechanicHoldReason == MechanicHoldReason.None && reason != MechanicHoldReason.None)
        {
            _mechanicHoldStartFireLeft = FireLeft;
            _mechanicHoldStartBrotherhoodLeft = BrotherhoodLeft;
            _mechanicHoldStartPerfectBalanceLeft = PerfectBalanceLeft;
            _mechanicHoldStartBlitzLeft = BlitzLeft;
            _mechanicHoldStartBeastCount = BeastCount;
            _mechanicHoldStartNadi = Nadi;
            _mechanicHoldStartForm = CurrentForm;
            _mechanicHoldStartFormLeft = FormLeft;
        }
        else if (_mechanicHoldReason != MechanicHoldReason.None && reason == MechanicHoldReason.None)
        {
            RecoverFromMechanicHold(strategy);
        }

        _mechanicHoldReason = reason;
    }

    private void RecoverFromMechanicHold(in Strategy strategy)
    {
        var activeRoFWindow = FireLeft > GCD;
        var activeBrotherhoodWindow = BrotherhoodLeft > GCD;
        var pbWasActive = _mechanicHoldStartPerfectBalanceLeft > GCD;
        var pbStillActive = PerfectBalanceLeft > GCD;
        var pbExpiredDuringHold = pbWasActive && !pbStillActive;
        var blitzWasPending = _mechanicHoldStartBlitzLeft > GCD;
        var blitzStillPending = BlitzLeft > GCD;
        var blitzExpiredDuringHold = blitzWasPending && !blitzStillPending;
        var burstBuffExpiredDuringHold = (_mechanicHoldStartFireLeft > GCD || _mechanicHoldStartBrotherhoodLeft > GCD) && !activeRoFWindow && !activeBrotherhoodWindow;
        var nadiChanged = _mechanicHoldStartNadi != Nadi;
        var formChanged = _mechanicHoldStartForm != CurrentForm || _mechanicHoldStartFormLeft > FormLeft + AnimationLockDelay;
        var beastChanged = _mechanicHoldStartBeastCount != BeastCount;

        StopChasingIncompleteEvenBurstAfterBuffs(activeRoFWindow, activeBrotherhoodWindow);

        if (!pbStillActive && (pbExpiredDuringHold || burstBuffExpiredDuringHold || nadiChanged || formChanged || beastChanged))
            _activePBNadiCyclePhase = null;

        if (blitzExpiredDuringHold)
            _countedAutomaticPBBlitz = AID.None;

        _prioritizePendingBlitzAfterMechanicHold = blitzWasPending && blitzStillPending;

        if (!pbStillActive && (nadiChanged || formChanged || beastChanged))
            _forcePBOnRotationModeResume &= CanSpendOnePBBeforeNextEvenWithoutBreakingTwoPB(strategy, NextPlannedTwoMinuteBurstIn());

        ClearMechanicHoldSnapshot();
    }

    private void ClearMechanicHoldSnapshot()
    {
        _mechanicHoldStartFireLeft = 0;
        _mechanicHoldStartBrotherhoodLeft = 0;
        _mechanicHoldStartPerfectBalanceLeft = 0;
        _mechanicHoldStartBlitzLeft = 0;
        _mechanicHoldStartBeastCount = 0;
        _mechanicHoldStartNadi = default;
        _mechanicHoldStartForm = Form.None;
        _mechanicHoldStartFormLeft = 0;
    }

    private float MechanicRecoveryActionScore(AID action, MechanicHoldReason reason)
    {
        if (!UseMechanicAIHints())
            return 0;

        return action switch
        {
            AID.FiresReply => reason != MechanicHoldReason.LookAway && FiresReplyLeft > GCD ? 90f : 0f,
            AID.WindsReply => reason != MechanicHoldReason.LookAway && WindsReplyLeft > GCD ? 85f : 0f,
            AID.SixSidedStar => reason != MechanicHoldReason.LookAway
                && _meleePrediction.ForcedOutSoon
                && _meleePrediction.MeleeLossIn <= GCD + AnimationLockDelay
                && _meleePrediction.ForcedOutDuration >= AttackGCDLength ? 75f : 0f,
            _ => 0f
        };
    }

    private bool PBOrBlitzCompletionTakesPriority()
    {
        if (BeastCount == 3 && BlitzLeft > GCD)
            return true;

        if (PerfectBalanceLeft <= GCD)
            return false;

        if (BeastCount is > 0 and < 3)
            return true;

        if ((FireLeft > GCD || BrotherhoodLeft > GCD) && EffectiveMeleeBurstUptimeIn > AttackGCDLength + AnimationLockDelay)
            return true;

        return false;
    }

    private bool ShouldUseMechanicSixSidedStar()
        => !PBOrBlitzCompletionTakesPriority()
        && MechanicRecoveryActionScore(AID.SixSidedStar, _mechanicHoldReason) > 0;

    private bool ForbiddenAt(WPos position, float at)
    {
        if (!UseMechanicAIHints())
            return false;

        var checkAt = World.CurrentTime.AddSeconds(at);
        foreach (var zone in Hints.ForbiddenZones)
        {
            if (zone.activation != default && zone.activation > checkAt)
                continue;
            if (zone.shapeDistance.Contains(position))
                return true;
        }

        foreach (var obstacle in Hints.TemporaryObstacles)
            if (obstacle.Contains(position))
                return true;

        return false;
    }

    private void SmartEngage(in Strategy strategy, Enemy? primaryTarget)
    {
        if (primaryTarget == null)
            return;
        var facepullAction = AID.None;
        var engageOpoAction = BestOpoGCD();
        var holdForLookAway = ShouldHoldForLookAway(primaryTarget.Actor);

        // invariant: countdown is > 0
        switch (strategy.Engage.Value)
        {
            case EngageStrategy.TC:
                if (_openerThunderclapLandingOffset < 0)
                    _openerThunderclapLandingOffset = new Random((int)World.Frame.Index).NextSingle() * ThunderclapRandomOffsetMax; // frame-seeded: replays reproduce it

                if (!holdForLookAway && strategy.TC.Value == TCStrategy.GapClose && Unlocked(AID.Thunderclap) && ReadyIn(AID.Thunderclap) <= AnimationLockDelay && CountdownRemaining < ThunderclapZeroLandingCountdown - _openerThunderclapLandingOffset && Player.DistanceToHitbox(primaryTarget) > MeleeRange)
                {
                    PushOGCD(AID.Thunderclap, primaryTarget);
                    return;
                }

                if (!holdForLookAway && CountdownRemaining < GetApplicationDelay(engageOpoAction))
                    PushGCD(engageOpoAction, primaryTarget);
                return;

            case EngageStrategy.Sprint:
                if (CountdownRemaining < 10)
                    PushOGCD(AID.Sprint, Player);

                var distToMelee = Player.DistanceToHitbox(primaryTarget) - MeleeRange;
                var secToMelee = distToMelee / EngageMovementSpeedEstimate;
                // TODO account for acceleration
                if (!holdForLookAway && CountdownRemaining < secToMelee + EngageMovementBuffer)
                {
                    if (primaryTarget.Actor != null)
                        Hints.ForcedMovement = Player.DirectionTo(primaryTarget.Actor).ToVec3();
                    PushGCD(engageOpoAction, primaryTarget);
                }

                return;

            case EngageStrategy.FacepullDK:
                facepullAction = engageOpoAction;
                break;
            case EngageStrategy.FacepullDemo:
                facepullAction = Unlocked(AID.Demolish) ? AID.Demolish : engageOpoAction;
                break;
            case EngageStrategy.None:
                return;
        }

        if (facepullAction == default)
            return;

        if (holdForLookAway)
            return;

        if (Player.DistanceToHitbox(primaryTarget) > 3 && primaryTarget.Actor != null)
            Hints.ForcedMovement = Player.DirectionTo(primaryTarget.Actor).ToVec3();

        if (CountdownRemaining < GetApplicationDelay(facepullAction))
            PushGCD(facepullAction, primaryTarget);
    }

    private float PotionReadyIn()
    {
        var potion = ActionDefinitions.Instance[ActionDefinitions.IDPotionStr];
        return potion?.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions) ?? float.MaxValue;
    }

    private float DesiredFireWindow => GCDLength * DesiredFireWindowGCDs;
    private float EarliestRoF(float estimatedDelay) => Math.Max(estimatedDelay + RoFLateWeaveExtraDelay, RoFWindowBudget - DesiredFireWindow);

    private void Potion(float priority, bool lateWeave = true)
        => Hints.ActionsToExecute.Push(ActionDefinitions.IDPotionStr, Player, priority, delay: lateWeave ? Math.Max(0, GCD - PotionLateWeaveDelay) : 0);

    private bool ShouldPotionPreBuffs(in Strategy strategy, in BurstPlan burst, float preBuffWindowGCDs)
    {
        var tuning = ActiveTuningProfile;
        var hintAllowsBurst = EncounterHintAllowsAutomaticBurst(strategy);
        var lastPotionWindow = burst.LastPotionWindow;
        var potionStrategy = strategy.Pot.Value;
        if (!HaveTarget || !hintAllowsBurst || !lastPotionWindow && EffectiveBurstUptimeIn <= AnimLock + tuning.TargetAvailableUptimeRequired)
        {
            if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.Potion))
                LogTuning("Potion", $"t={CombatTimer:f1} prebuff=false ready={PotionReadyIn():f2} target={HaveTarget} hint={hintAllowsBurst} uptime={EffectiveBurstUptimeIn:f2} required={AnimLock + tuning.TargetAvailableUptimeRequired:f2}");
            return false;
        }

        var potionMeleeSafe = MajorBurstMeleeSafe(strategy, lastPotionWindow);
        if (!potionMeleeSafe)
        {
            if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.Potion))
                LogTuning("Potion", $"t={CombatTimer:f1} prebuff=false ready={PotionReadyIn():f2} meleeSafe=false effMeleeBurst={EffectiveMeleeBurstUptimeIn:f2} meaningful={MeaningfulForcedOut} last={lastPotionWindow}");
            return false;
        }

        var potionReadyIn = PotionReadyIn();
        if (BrotherhoodLeft > GCD && potionReadyIn <= GCD)
        {
            if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.Potion))
                LogTuning("Potion", $"t={CombatTimer:f1} prebuff=true reason=activeBH ready={potionReadyIn:f2} bhLeft={BrotherhoodLeft:f2} strategy={potionStrategy} last={lastPotionWindow}");
            return true;
        }

        var nextBrotherhoodIn = burst.NextBrotherhoodIn;
        var preBuffWindow = AttackGCDLength * preBuffWindowGCDs + AnimationLockDelay;
        var weaveWindowGCDs = Math.Max(1, (int)MathF.Ceiling(preBuffWindowGCDs));
        var use = nextBrotherhoodIn < float.MaxValue / 2
            && nextBrotherhoodIn <= preBuffWindow
            && CanWeave(AID.Brotherhood, weaveWindowGCDs);
        if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.Potion))
            LogTuning("Potion", $"t={CombatTimer:f1} prebuff={use} ready={potionReadyIn:f2} nextBH={nextBrotherhoodIn:f2} window={preBuffWindow:f2} strategy={potionStrategy} last={lastPotionWindow}");
        return use;
    }

    private bool ShouldUseEvenBurstPotion(in Strategy strategy, in BurstPlan burst, bool includeOpener)
    {
        // Started inside Brotherhood the potion only covers the tail of the window and pushes the next use out by 4.5
        // minutes, so normally we would rather wait for the next window - unless there is no next window to wait for.
        // That end-of-fight case is what ShouldPotionPreBuffs' activeBH branch and BurstPlan.LastPotionWindow's
        // activeBrotherhoodWindow clause were written for; blanket-rejecting BrotherhoodLeft > GCD made both dead.
        var lastWindowDuringBrotherhood = BrotherhoodLeft > GCD && burst.LastBrotherhoodWindow && burst.LastPotionWindow;
        if (BrotherhoodLeft > GCD && !lastWindowDuringBrotherhood)
            return false;

        if (DancingMadTopLogProfileEnabled(strategy) && !burst.LastPotionWindow)
        {
            if (IsInDancingMadTopLogPotionWindow(strategy))
                return ShouldPotionPreBuffs(strategy, burst, BurstImmediateWindowGCDs + 1f);

            if (ShouldHoldDancingMadTopLogPotion(strategy))
                return false;
        }

        // an active Brotherhood is never an "even window" (its own recast is ~2min out), so the last-window burn
        // has to bypass this gate to reach ShouldPotionPreBuffs
        if (!burst.EvenWindow && !lastWindowDuringBrotherhood)
            return false;

        var nextBrotherhoodIn = burst.NextBrotherhoodIn;
        if (nextBrotherhoodIn >= float.MaxValue / 2)
            return false;

        var nextBrotherhoodAt = CombatTimer + nextBrotherhoodIn;
        var openerWindow = _activatedRoFBursts == 0 && nextBrotherhoodAt <= PotionOpenerWindow;

        if (!includeOpener && openerWindow)
            return false;

        var preBuffWindowGCDs = openerWindow
            ? BurstImmediateWindowGCDs
            : BurstImmediateWindowGCDs + 1f;

        return ShouldPotionPreBuffs(strategy, burst, preBuffWindowGCDs);
    }

    private bool ShouldRoWReady(in Strategy strategy)
    {
        var tuning = ActiveTuningProfile;
        return EncounterHintAllowsAutomaticBurst(strategy)
            && HaveTarget
            && CanWeave(AID.RiddleOfWind)
            && (EffectiveBurstUptimeIn > AnimLock + tuning.RoWUptimeRequired || EndBurnWindow(strategy, tuning.RoWEndBurnLeeway));
    }

    private bool ShouldRoWAlignedWithRoF(in Strategy strategy, in BurstPlan burst)
        => ShouldRoWReady(strategy)
        && (FireLeft > GCD || burst.UseRoFNow);

    private bool ShouldRoWAlignedWithBurst(in Strategy strategy, in BurstPlan burst)
        => ShouldRoWReady(strategy)
        && (FireLeft > GCD || BrotherhoodLeft > GCD || burst.UseRoFNow || burst.UseBrotherhoodNow);

    private bool HoldRoWForTwoMinuteRoF()
    {
        var nextBurstIn = Math.Min(NextPlannedRoFBurstIn(), NextPlannedBrotherhoodBurstIn());
        return CombatTimer is >= RoWTwoMinuteStart and < RoWTwoMinuteEnd
            && nextBurstIn > GCD
            && nextBurstIn <= ActiveTuningProfile.RoWTwoMinuteHoldWindow;
    }

    private float NextPlannedTwoMinuteBurstIn()
    {
        var nextBrotherhoodIn = Unlocked(AID.Brotherhood) ? NextPlannedBrotherhoodBurstIn() : float.MaxValue;
        return nextBrotherhoodIn < float.MaxValue / 2 ? nextBrotherhoodIn : NextPlannedRoFBurstIn();
    }

    private static int CountRoWUsesUntil(float firstUseAt, float horizon)
    {
        return firstUseAt <= horizon ? 1 + (int)MathF.Floor((horizon - firstUseAt) / RoWCooldown) : 0;
    }

    private bool ShouldRoWOpenerTwoMinuteAdaptive(in Strategy strategy, in BurstPlan burst)
    {
        var tuning = ActiveTuningProfile;
        if (!ShouldRoWReady(strategy))
            return false;

        if (CombatTimer < RoWOpenerWindow)
            return true;

        if (EndBurnWindow(strategy, tuning.RoWEndBurnLeeway))
            return true;

        var nextTwoMinuteIn = NextPlannedTwoMinuteBurstIn();
        if (CombatTimer is >= RoWTwoMinuteStart and < RoWTwoMinuteEnd && nextTwoMinuteIn > GCD && nextTwoMinuteIn <= tuning.RoWTwoMinuteHoldWindow)
            return false;

        if (ShouldRoWAlignedWithBurst(strategy, burst))
            return true;

        if (nextTwoMinuteIn <= GCD + AnimationLockDelay)
            return true;

        var horizon = _fightEndFromShared ? null : EstimatedBurstHorizon ?? EstimatedFightEnd;
        if (horizon is not float estimatedEnd || nextTwoMinuteIn == float.MaxValue)
            return false;

        var immediateAt = CombatTimer + Math.Max(0, ReadyIn(AID.RiddleOfWind));
        var alignedAt = CombatTimer + nextTwoMinuteIn;
        return CountRoWUsesUntil(immediateAt, estimatedEnd) > CountRoWUsesUntil(alignedAt, estimatedEnd);
    }

    private bool ShouldRoWAutomaticMaxDPS(in Strategy strategy, in BurstPlan burst)
    {
        var tuning = ActiveTuningProfile;
        if (!ShouldRoWReady(strategy))
            return false;

        if (CombatTimer < RoWOpenerWindow)
            return true;

        if (EndBurnWindow(strategy, tuning.RoWEndBurnLeeway))
            return true;

        if (FireLeft > GCD
            || BrotherhoodLeft > GCD
            || burst.UseRoFNow
            || burst.UseBrotherhoodNow)
            return true;

        var nextTwoMinuteIn = NextPlannedTwoMinuteBurstIn();
        if (nextTwoMinuteIn <= GCD + AnimationLockDelay)
            return true;

        if (CombatTimer is >= RoWTwoMinuteStart and < RoWTwoMinuteEnd
            && nextTwoMinuteIn > GCD
            && nextTwoMinuteIn <= tuning.RoWTwoMinuteHoldWindow)
        {
            var horizon = _fightEndFromShared ? null : EstimatedBurstHorizon ?? EstimatedFightEnd;
            if (horizon is not float estimatedEnd || nextTwoMinuteIn >= float.MaxValue / 2)
                return true;

            var immediateAt = CombatTimer + Math.Max(0, ReadyIn(AID.RiddleOfWind));
            var alignedAt = CombatTimer + nextTwoMinuteIn;
            return CountRoWUsesUntil(immediateAt, estimatedEnd) > CountRoWUsesUntil(alignedAt, estimatedEnd);
        }

        return true;
    }

    private bool ShouldRoWAutomaticDancingMadTopLog(in Strategy strategy, in BurstPlan burst)
    {
        if (EndBurnWindow(strategy, ActiveTuningProfile.RoWEndBurnLeeway))
            return ShouldRoWReady(strategy);

        if (IsInDancingMadTopLogActionWindow(strategy, AID.RiddleOfWind))
            return ShouldRoWReady(strategy);

        if (ShouldHoldDancingMadTopLogAction(strategy, AID.RiddleOfWind))
            return false;

        return ShouldRoWAutomaticMaxDPS(strategy, burst);
    }

    private bool ShouldRoW(in Strategy strategy, in BurstPlan burst)
    {
        var use = strategy.RoW.Value switch
        {
            RoWStrategy.Automatic => DancingMadTopLogProfileEnabled(strategy)
                ? ShouldRoWAutomaticDancingMadTopLog(strategy, burst)
                : ShouldRoWAutomaticMaxDPS(strategy, burst),
            RoWStrategy.OpenerTwoMinuteThenCooldown => ShouldRoWReady(strategy) && (EndBurnWindow(strategy, ActiveTuningProfile.RoWEndBurnLeeway) || ShouldRoWAlignedWithRoF(strategy, burst) && !HoldRoWForTwoMinuteRoF()),
            RoWStrategy.OpenerCooldown => ShouldRoWReady(strategy),
            RoWStrategy.OpenerTwoMinuteAdaptive => ShouldRoWOpenerTwoMinuteAdaptive(strategy, burst),
            RoWStrategy.RoFAligned => ShouldRoWAlignedWithRoF(strategy, burst),
            RoWStrategy.Force => true,
            _ => false
        };
        var rowStrategy = strategy.RoW.Value;
        var automaticRecast = rowStrategy == RoWStrategy.Automatic;
        if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.RoW))
            LogGuide73("RoW", $"t={CombatTimer:f1} use={use} strategy={rowStrategy} ready={ReadyIn(AID.RiddleOfWind):f2} next2m={NextPlannedTwoMinuteBurstIn():f2} fire={FireLeft:f2} bh={BrotherhoodLeft:f2} uptime={EffectiveBurstUptimeIn:f2} horizon={TuningValue(EstimatedBurstHorizon)} automaticRecast={automaticRecast}");
        return use;
    }

    // Execute() clears NextGCD before Exec runs, so every caller that fires before the GCD pushes (BuildBurstPlan, the
    // PB delay helpers) would read None and the guard would silently do nothing. Within one GCD window the pick is
    // stable, so fall back to the previous frame's; a reply that was already spent fails the *Left > GCD test below.
    private AID PendingGCDForReplyCheck => NextGCD != AID.None ? NextGCD : _previousNextGCD;

    private bool ShouldBlockPBForPendingReply(in Strategy strategy)
        => PendingGCDForReplyCheck switch
        {
            AID.FiresReply => strategy.FiresReply.Value != FRStrategy.Delay && FiresReplyLeft > GCD,
            AID.WindsReply => strategy.WindsReply.Value != WRStrategy.Delay && WindsReplyLeft > GCD,
            _ => false
        };

    private void FiresReply(in Strategy strategy, in BurstPlan burst)
    {
        if (FiresReplyLeft <= GCD)
            return;

        if (strategy.FiresReply.Value == FRStrategy.Delay)
            return;

        var effectiveFiresReplyLeft = ResourceHorizonClamp(strategy, FiresReplyLeft);
        var effectiveFireLeft = ResourceHorizonClamp(strategy, FireLeft);
        var effectiveBrotherhoodLeft = ResourceHorizonClamp(strategy, BrotherhoodLeft);
        var gcdsUntilIdeal = CurrentForm switch
        {
            Form.Raptor => 0,
            Form.OpoOpo or Form.None => 1,
            Form.Coeurl => 2,
            _ => 1
        };
        var canHoldForIdeal = gcdsUntilIdeal > 0 && CanFitGCD(effectiveFiresReplyLeft, gcdsUntilIdeal);
        var replyExpiring = !CanFitGCD(effectiveFiresReplyLeft, 1);
        // A reply that only looks expiring because the target is about to vanish must not take a Perfect Balance GCD:
        // losing the beast chakra costs the whole Blitz plus the charge, while the reply is one GCD. Only the buff
        // genuinely running out overrides PB completion, so this checks the raw duration, not the clamped one.
        var replyBuffExpiring = !CanFitGCD(FiresReplyLeft, 1);
        var protectPBGCDs = PBOrBlitzCompletionTakesPriority() && !replyBuffExpiring;
        var prio = strategy.FiresReply.Value switch
        {
            FRStrategy.Automatic => gcdsUntilIdeal == 0 || !canHoldForIdeal ? GCDPriority.FiresReply : GCDPriority.None,
            FRStrategy.Ranged => CanFitGCD(effectiveFiresReplyLeft, 1) ? GCDPriority.FireRanged : GCDPriority.FiresReply,
            FRStrategy.Force => GCDPriority.FiresReply,
            _ => GCDPriority.None
        };

        if (protectPBGCDs && strategy.FiresReply.Value != FRStrategy.Force && prio > GCDPriority.FireRanged)
            prio = GCDPriority.FireRanged;

        if (!protectPBGCDs && effectiveFireLeft > GCD && !CanFitGCD(effectiveFireLeft, gcdsUntilIdeal))
            prio = GCDPriority.FiresReply;

        var burstBuffActive = effectiveFireLeft > GCD || effectiveBrotherhoodLeft > GCD || burst.UseRoFNow || burst.UseBrotherhoodNow;
        var forcedOutBeforeIdeal = MeaningfulForcedOut
            && gcdsUntilIdeal > 0
            && !CanFitGCD(EffectiveMeleeBurstUptimeIn, gcdsUntilIdeal + 1);
        if (!protectPBGCDs && burstBuffActive && forcedOutBeforeIdeal)
            prio = GCDPriority.FiresReply;

        if (!protectPBGCDs && !HaveMeleeTarget && HaveTarget)
            prio = GCDPriority.FiresReply;

        if (!protectPBGCDs && MechanicRecoveryActionScore(AID.FiresReply, _mechanicHoldReason) > 0 && !HaveMeleeTarget)
            prio = GCDPriority.FiresReply;

        // FRExpire outranks an urgent blitz, so it must not fire on a reply that is only "expiring" because the
        // target is about to vanish while a Perfect Balance still needs its beast chakra
        if (replyExpiring && !protectPBGCDs)
            prio = GCDPriority.FRExpire;

        PushGCD(AID.FiresReply, ResolveTargetOverride(strategy.FiresReply) ?? BestRangedTarget, prio);
    }

    private void WindsReply(in Strategy strategy)
    {
        if (WindsReplyLeft <= GCD)
            return;

        if (strategy.WindsReply.Value == WRStrategy.Delay)
            return;

        var effectiveWindsReplyLeft = ResourceHorizonClamp(strategy, WindsReplyLeft);
        var effectiveFireLeft = ResourceHorizonClamp(strategy, FireLeft);
        var effectiveBrotherhoodLeft = ResourceHorizonClamp(strategy, BrotherhoodLeft);
        var burstActive = effectiveFireLeft > GCD || effectiveBrotherhoodLeft > GCD;
        // always queue with low prio, this lets us fallback to winds reply when out of range for melee GCDs
        var prio = GCDPriority.WindRanged;
        var replyExpiring = !CanFitGCD(effectiveWindsReplyLeft, 1);
        // see FiresReply: only the buff itself running out may interrupt a Perfect Balance
        var replyBuffExpiring = !CanFitGCD(WindsReplyLeft, 1);
        var protectPBGCDs = PBOrBlitzCompletionTakesPriority() && !replyBuffExpiring;

        if (burstActive && !protectPBGCDs)
            prio = GCDPriority.WindsReply;

        if (!protectPBGCDs && !HaveMeleeTarget && HaveTarget)
            prio = GCDPriority.WindsReply;

        if (!protectPBGCDs && MechanicRecoveryActionScore(AID.WindsReply, _mechanicHoldReason) > 0 && !HaveMeleeTarget)
            prio = GCDPriority.WindsReply;

        switch (strategy.WindsReply.Value)
        {
            case WRStrategy.Force:
                prio = GCDPriority.WindsReply;
                break;
            case WRStrategy.PreDowntime:
                if (!protectPBGCDs && EffectiveBurstUptimeIn < WindsReplyLeft && !CanFitGCD(EffectiveBurstUptimeIn, 2))
                    prio = GCDPriority.WindsReply;
                break;
        }

        // If the buff itself is about to expire, use it even outside burst and ahead of an urgent blitz.
        // see FiresReply: never let a clamp-induced expiry take the last Perfect Balance GCD
        if (replyExpiring && !protectPBGCDs)
            prio = GCDPriority.WRExpire;

        PushGCD(AID.WindsReply, ResolveTargetOverride(strategy.WindsReply) ?? BestLineTarget, prio);
    }

    private float ScheduledBurstReadyIn(DateTime scheduledAt, float cooldownReadyIn)
    {
        if (scheduledAt == default)
            return cooldownReadyIn;

        var scheduledReadyIn = (float)(scheduledAt - World.CurrentTime).TotalSeconds;
        return Math.Max(cooldownReadyIn, Math.Max(0, scheduledReadyIn));
    }

    private float ScheduledAnchorReadyIn(DateTime scheduledAt, float cooldownReadyIn)
        => scheduledAt == default
            ? Math.Max(0, cooldownReadyIn)
            : Math.Max(Math.Max(0, cooldownReadyIn), Math.Max(0, (float)(scheduledAt - World.CurrentTime).TotalSeconds));

    private static bool BurstTimingAllowsPartyAlignment(in Strategy strategy)
        => strategy.BurstTiming.Value != BurstTimingStrategy.Cooldown;

    private static bool IsFixedOpenerRoFOffset(in Strategy strategy)
        => strategy.OpenerRoFOffset.Value is OpenerRoFOffsetStrategy.Standard78
            or OpenerRoFOffsetStrategy.Early58
            or OpenerRoFOffsetStrategy.ZeroSecondBurst;

    private static bool IsDelayedFixedOpenerRoFOffset(in Strategy strategy)
        => strategy.OpenerRoFOffset.Value is OpenerRoFOffsetStrategy.Standard78
            or OpenerRoFOffsetStrategy.Early58;

    private bool ShouldUseOpenerOffsetSchedule(in Strategy strategy)
        => _activatedRoFBursts == 0
        && IsFixedOpenerRoFOffset(strategy)
        && _nextScheduledRoFBurstAt != default;

    private bool ShouldPairDelayedOpenerBrotherhoodWithRoF(in Strategy strategy)
        => _activatedRoFBursts == 0
        && IsDelayedFixedOpenerRoFOffset(strategy)
        && ShouldUseOpenerOffsetSchedule(strategy)
        && strategy.RoF.Value == RoFStrategy.Automatic
        && strategy.Brotherhood.Value == OffensiveStrategy.Automatic
        && Unlocked(AID.Brotherhood);

    private bool ShouldTrackDelayedOpenerDoubleLunarPBBurst(in Strategy strategy, float scheduledNextRoFIn)
        => _activatedRoFBursts == 0
        && IsDelayedFixedOpenerRoFOffset(strategy)
        && ShouldUseOpenerOffsetSchedule(strategy)
        && strategy.RoF.Value == RoFStrategy.Automatic
        && strategy.Brotherhood.Value == OffensiveStrategy.Automatic
        && strategy.PB.Value == PBStrategy.Automatic
        && PBUnlocked
        && Unlocked(AID.Brotherhood)
        && IsDoubleLunarMode(strategy, 2, 0)
        && scheduledNextRoFIn >= 0
        && scheduledNextRoFIn <= ActiveTuningProfile.EvenPreRoFPBStartThreshold + AnimationLockDelay;

    private bool ShouldUseScheduledRoFAnchor(in Strategy strategy)
        => strategy.RoF.Value == RoFStrategy.Automatic
        && (BurstTimingAllowsPartyAlignment(strategy) || ShouldUseOpenerOffsetSchedule(strategy));

    private bool ShouldUseScheduledBrotherhoodAnchor(in Strategy strategy)
        => strategy.Brotherhood.Value == OffensiveStrategy.Automatic
        && (BurstTimingAllowsPartyAlignment(strategy) || ShouldUseOpenerOffsetSchedule(strategy));

    private static bool BurstTimingUsesMechanicSafeFixedTiming(in Strategy strategy)
        => strategy.BurstTiming.Value == BurstTimingStrategy.MeleeSafe;

    private bool MajorBurstMeleeSafe(in Strategy strategy, bool lastWindow)
        => lastWindow
            || strategy.BurstTiming.Value == BurstTimingStrategy.SynergyFixed && IgnoreDancingMadP1MechanicActionHold
            || !MeaningfulForcedOut
            || EffectiveMeleeBurstUptimeIn > AttackGCDLength * ActiveTuningProfile.MajorBurstMeleeSafeGCDs + AnimationLockDelay;

    private bool OpenerRoFOffsetPending()
        => _activatedRoFBursts == 0
        && FireLeft <= GCD
        && _openerRoFOffsetProtectedUntil != default
        && _openerRoFOffsetProtectedUntil > World.CurrentTime.AddSeconds(AnimationLockDelay);

    private void ProtectOpenerRoFOffsetSchedule()
    {
        if (!OpenerRoFOffsetPending())
            return;

        _nextScheduledRoFBurstAt = _openerRoFOffsetProtectedUntil;
        if (_nextScheduledBrotherhoodBurstAt != default && _nextScheduledBrotherhoodBurstAt < _openerRoFOffsetProtectedUntil)
            _nextScheduledBrotherhoodBurstAt = _openerRoFOffsetProtectedUntil;
    }

    private void SynchronizeRoFBrotherhoodBurstSchedule(in Strategy strategy, Actor? target, bool allowLongRange = false)
    {
        if (strategy.RoF.Value != RoFStrategy.Automatic
            || strategy.Brotherhood.Value != OffensiveStrategy.Automatic
            || !BurstTimingAllowsPartyAlignment(strategy)
            || !EncounterHintAllowsAutomaticBurst(strategy)
            || !Unlocked(AID.Brotherhood)
            || !Player.InCombat
            || Player.IsDead
            || HasTranscendentStatus())
            return;

        if (_activatedRoFBursts % 2 != 0)
            return;

        if (OpenerRoFOffsetPending())
            return;

        var roFReadyIn = Math.Max(0, ReadyIn(AID.RiddleOfFire));
        var brotherhoodReadyIn = Math.Max(0, ReadyIn(AID.Brotherhood));
        var roFAnchorIn = ScheduledAnchorReadyIn(_nextScheduledRoFBurstAt, roFReadyIn);
        var brotherhoodAnchorIn = ScheduledAnchorReadyIn(_nextScheduledBrotherhoodBurstAt, brotherhoodReadyIn);
        var earlyAnchorIn = Math.Min(roFAnchorIn, brotherhoodAnchorIn);
        var lateAnchorIn = Math.Max(roFAnchorIn, brotherhoodAnchorIn);
        var earliestSharedAnchorIn = Math.Max(roFReadyIn, brotherhoodReadyIn);

        if (lateAnchorIn >= float.MaxValue / 2
            || earliestSharedAnchorIn >= float.MaxValue / 2
            || !allowLongRange && lateAnchorIn > ActiveTuningProfile.RoFBrotherhoodResyncWindow)
            return;

        if (lateAnchorIn <= AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay)
            return;

        // the synergy planner below is expensive; recompute only every RoFBrotherhoodSyncCacheSeconds or when its inputs change
        var targetID = target?.InstanceID ?? 0;
        if (World.CurrentTime < _nextRoFBrotherhoodSyncAt
            && _roFBrotherhoodSyncRoFAnchor == _nextScheduledRoFBurstAt
            && _roFBrotherhoodSyncBrotherhoodAnchor == _nextScheduledBrotherhoodBurstAt
            && _roFBrotherhoodSyncTargetID == targetID
            && _roFBrotherhoodSyncAllowLongRange == allowLongRange
            && _roFBrotherhoodSyncActivatedRoFBursts == _activatedRoFBursts)
            return;

        _nextRoFBrotherhoodSyncAt = World.CurrentTime.AddSeconds(RoFBrotherhoodSyncCacheSeconds);
        _roFBrotherhoodSyncRoFAnchor = _nextScheduledRoFBurstAt;
        _roFBrotherhoodSyncBrotherhoodAnchor = _nextScheduledBrotherhoodBurstAt;
        _roFBrotherhoodSyncTargetID = targetID;
        _roFBrotherhoodSyncAllowLongRange = allowLongRange;
        _roFBrotherhoodSyncActivatedRoFBursts = _activatedRoFBursts;

        var sharedAnchorIn = BestSynergyBurstAnchorIn(strategy, earliestSharedAnchorIn, lateAnchorIn, target, allowLongRange);
        if (Math.Abs(sharedAnchorIn - lateAnchorIn) <= AnimationLockDelay && lateAnchorIn - earlyAnchorIn <= AnimationLockDelay)
            return;

        if (roFReadyIn <= sharedAnchorIn + AnimationLockDelay)
            _nextScheduledRoFBurstAt = World.CurrentTime.AddSeconds(sharedAnchorIn);
    }

    private float BestSynergyBurstAnchorIn(in Strategy strategy, float earliestAnchorIn, float fallbackAnchorIn, Actor? target, bool allowLongRange)
    {
        // hoisted: constant for the whole planner run, but consulted per candidate by the rotation-preservation checks
        _synergyDoubleLunarMode = IsDoubleLunarMode(strategy, 2, 0);
        var windows = Bossmods.RaidCooldowns.DamageBuffWindows(Player, target);
        var maxExternalWindowStartIn = allowLongRange
            ? float.MaxValue
            : ActiveTuningProfile.RoFBrotherhoodResyncWindow + RoFWindowBudget + AnimationLockDelay;
        var usableWindows = Array.FindAll(windows, window => IsUsableExternalSynergyWindow(window, maxExternalWindowStartIn));
        if (usableWindows.Length == 0)
            return SynergyCandidatePreservesRotation(strategy, earliestAnchorIn, allowLongRange)
                ? earliestAnchorIn
                : fallbackAnchorIn;

        var candidateAnchors = BuildSynergyBurstAnchorCandidates(earliestAnchorIn, fallbackAnchorIn, usableWindows, allowLongRange);
        var memo = new Dictionary<(int Cycle, int PreviousIndex), (float Score, float FirstAnchorIn)>();
        var plannerStrategy = strategy;

        (float Score, float FirstAnchorIn) solve(int cycle, int previousIndex)
        {
            if (cycle >= candidateAnchors.Length)
                return (0, fallbackAnchorIn);

            var key = (cycle, previousIndex);
            if (memo.TryGetValue(key, out var cached))
                return cached;

            var previousAnchorIn = cycle == 0 || previousIndex < 0
                ? earliestAnchorIn - SynergyPlannerCycle
                : candidateAnchors[cycle - 1][previousIndex];
            var fallbackForCycle = fallbackAnchorIn + SynergyPlannerCycle * cycle;
            var bestScore = float.NegativeInfinity;
            var bestFirstAnchorIn = Math.Clamp(fallbackForCycle, candidateAnchors[cycle][0], candidateAnchors[cycle][^1]);

            for (var i = 0; i < candidateAnchors[cycle].Length; ++i)
            {
                var candidate = candidateAnchors[cycle][i];
                if (candidate < previousAnchorIn + SynergyPlannerCycle - AnimationLockDelay)
                    continue;

                if (cycle == 0 && !SynergyCandidatePreservesRotation(plannerStrategy, candidate, allowLongRange))
                    continue;

                var next = solve(cycle + 1, i);
                var score = BurstSynergyScore(plannerStrategy, candidate, usableWindows) + next.Score;
                var firstAnchorIn = cycle == 0 ? candidate : next.FirstAnchorIn;
                if (score > bestScore + SynergyResyncScoreGainThreshold
                    || Math.Abs(score - bestScore) <= SynergyResyncScoreGainThreshold && Math.Abs(firstAnchorIn - fallbackAnchorIn) < Math.Abs(bestFirstAnchorIn - fallbackAnchorIn))
                {
                    bestScore = score;
                    bestFirstAnchorIn = firstAnchorIn;
                }
            }

            return memo[key] = (bestScore, bestFirstAnchorIn);
        }

        return solve(0, -1).FirstAnchorIn;
    }

    private bool IsUsableExternalSynergyWindow(DamageBuffWindowSnapshot window, float maxStartsIn)
    {
        if (window.Action.ID == (uint)AID.Brotherhood)
            return false;

        if (float.IsNaN(window.StartsIn) || float.IsInfinity(window.StartsIn) || window.StartsIn >= float.MaxValue / 2)
            return false;

        if (float.IsNaN(window.Duration) || window.Duration <= 0)
            return false;

        if (window.StartsIn + window.Duration < -AnimationLockDelay)
            return false;

        if (window.StartsIn > maxStartsIn)
            return false;

        return true;
    }

    private bool SynergyCandidatePreservesRotation(in Strategy strategy, float anchorIn, bool allowLongRange)
    {
        if (anchorIn < 0 || anchorIn >= float.MaxValue / 2)
            return false;

        var tuning = ActiveTuningProfile;
        if (!allowLongRange && anchorIn > tuning.RoFBrotherhoodResyncWindow + AnimationLockDelay)
            return false;

        if (TryGetResourceBurnHorizon(strategy, out var resourceBurnHorizon)
            && ResourceBurnHorizonIsPhaseEnd(resourceBurnHorizon)
            && CombatTimer + anchorIn + tuning.LastRoFWindowLeeway >= resourceBurnHorizon)
            return true;

        if (ReadyIn(AID.RiddleOfFire) > anchorIn + AnimationLockDelay
            || ReadyIn(AID.Brotherhood) > anchorIn + AnimationLockDelay)
            return false;

        if (_nextScheduledRoFBurstAt != default)
        {
            var scheduledRoFLateBy = (float)(World.CurrentTime - _nextScheduledRoFBurstAt).TotalSeconds;
            if (ShouldSkipLateOddRoFAfterPhaseResume(strategy, scheduledRoFLateBy, _activatedRoFBursts, phaseEndRoFBurnWindow: false)
                && anchorIn <= GCD + AnimationLockDelay)
                return false;
        }

        var automaticBurstAllowed = EncounterHintAllowsAutomaticBurst(strategy);
        if (!NextEvenBurstGuaranteedForOddPhantomRushHold(anchorIn, anchorIn, automaticBurstAllowed, tuning))
            return false;

        if (!PBUnlocked || !BlitzUnlocked || !_synergyDoubleLunarMode)
            return true;

        if (MaxChargesIn(AID.PerfectBalance) > anchorIn + PBUseSpacing + AnimationLockDelay)
            return false;

        if (HaveLunar || HaveSolar)
            return true;

        return CanSpendOnePBBeforeNextEvenWithoutBreakingTwoPB(strategy, anchorIn);
    }

    private float[][] BuildSynergyBurstAnchorCandidates(float earliestAnchorIn, float fallbackAnchorIn, DamageBuffWindowSnapshot[] windows, bool allowLongRange)
    {
        var tuning = ActiveTuningProfile;
        var cycles = SynergyPlannerCycleCount(fallbackAnchorIn);
        var candidatesByCycle = new List<float>[cycles];
        for (var cycle = 0; cycle < cycles; ++cycle)
        {
            candidatesByCycle[cycle] = [];
            var cycleMin = earliestAnchorIn + SynergyPlannerCycle * cycle;
            var cycleFallback = fallbackAnchorIn + SynergyPlannerCycle * cycle;
            var cycleMax = cycle == 0 && !allowLongRange ? tuning.RoFBrotherhoodResyncWindow : cycleFallback + tuning.RoFBrotherhoodResyncWindow;

            AddSynergyBurstAnchorCandidate(candidatesByCycle[cycle], cycleMin, cycleMin, cycleMax);
            AddSynergyBurstAnchorCandidate(candidatesByCycle[cycle], cycleFallback, cycleMin, cycleMax);
            AddSynergyBurstAnchorCandidate(candidatesByCycle[cycle], cycleMax, cycleMin, cycleMax);

            foreach (var window in windows)
            {
                if (window.Action.ID == (uint)AID.Brotherhood)
                    continue;

                var windowStart = window.StartsIn;
                while (windowStart + window.Duration < cycleMin - AnimationLockDelay)
                    windowStart += SynergyPlannerCycle;

                for (; windowStart <= cycleMax + AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay; windowStart += SynergyPlannerCycle)
                {
                    AddSynergyBurstAnchorCandidate(candidatesByCycle[cycle], windowStart, cycleMin, cycleMax);
                    AddSynergyBurstAnchorCandidate(candidatesByCycle[cycle], windowStart - AttackGCDLength, cycleMin, cycleMax);
                    AddSynergyBurstAnchorCandidate(candidatesByCycle[cycle], windowStart - AttackGCDLength * 2, cycleMin, cycleMax);
                }
            }

            candidatesByCycle[cycle].Sort();
        }

        for (var cycle = 1; cycle < cycles; ++cycle)
        {
            var cycleMin = earliestAnchorIn + SynergyPlannerCycle * cycle;
            var cycleFallback = fallbackAnchorIn + SynergyPlannerCycle * cycle;
            var cycleMax = cycleFallback + tuning.RoFBrotherhoodResyncWindow;
            foreach (var previousCandidate in candidatesByCycle[cycle - 1])
                AddSynergyBurstAnchorCandidate(candidatesByCycle[cycle], previousCandidate + SynergyPlannerCycle, cycleMin, cycleMax);

            candidatesByCycle[cycle].Sort();
        }

        var result = new float[cycles][];
        for (var cycle = 0; cycle < cycles; ++cycle)
            result[cycle] = [.. candidatesByCycle[cycle]];

        return result;
    }

    private int SynergyPlannerCycleCount(float fallbackAnchorIn)
    {
        var horizon = _fightEndFromShared ? null : EstimatedBurstHorizon ?? EstimatedFightEnd;
        var remaining = horizon is float estimatedEnd ? Math.Max(0, estimatedEnd - CombatTimer) : SynergyPlannerCycle * SynergyPlannerMaxCycles;
        return Math.Clamp(1 + (int)MathF.Floor(Math.Max(0, remaining - fallbackAnchorIn) / SynergyPlannerCycle), 1, SynergyPlannerMaxCycles);
    }

    private void AddSynergyBurstAnchorCandidate(List<float> candidates, float candidate, float min, float max)
    {
        if (candidate < min - AnimationLockDelay || candidate > max + AnimationLockDelay)
            return;

        candidate = Math.Clamp(candidate, min, max);
        foreach (var existing in candidates)
            if (Math.Abs(existing - candidate) <= AnimationLockDelay)
                return;

        candidates.Add(candidate);
    }

    private float BurstSynergyScore(in Strategy strategy, float anchorIn, DamageBuffWindowSnapshot[] windows)
        => ScoreConstrainedRaidContributionBurstAnchor(strategy, anchorIn, windows);

    private float ScoreConstrainedRaidContributionBurstAnchor(in Strategy strategy, float anchorIn, DamageBuffWindowSnapshot[] windows)
    {
        var score = 0f;
        foreach (var window in windows)
        {
            if (window.Action.ID == (uint)AID.Brotherhood)
                continue;

            var windowStart = window.StartsIn;
            while (windowStart + window.Duration < anchorIn - AnimationLockDelay)
                windowStart += SynergyPlannerCycle;

            for (; windowStart <= anchorIn + RoFWindowBudget + AnimationLockDelay; windowStart += SynergyPlannerCycle)
            {
                var overlapStart = Math.Max(anchorIn, windowStart);
                var overlapEnd = Math.Min(anchorIn + RoFWindowBudget, windowStart + window.Duration);
                if (overlapEnd > overlapStart)
                    score += (overlapEnd - overlapStart) * window.Weight * 2f;

                var clusterDistance = Math.Abs(windowStart - anchorIn);
                var clusterWindow = AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay;
                if (clusterDistance <= clusterWindow)
                    score += (clusterWindow - clusterDistance) * window.Weight;
            }
        }

        score += SynergySelfRotationMaintenanceScore(strategy, anchorIn);
        return score;
    }

    private float SynergySelfRotationMaintenanceScore(in Strategy strategy, float anchorIn)
    {
        var score = 0f;
        if (EffectiveBurstUptimeIn > anchorIn + RoFWindowBudget)
            score += 8f;

        if (EffectiveMeleeBurstUptimeIn > anchorIn + AttackGCDLength * ActiveTuningProfile.MajorBurstMeleeSafeGCDs + AnimationLockDelay)
            score += 4f;

        if (PBUnlocked && MaxChargesIn(AID.PerfectBalance) <= anchorIn + PBUseSpacing + AnimationLockDelay)
            score += 4f;

        if (!_synergyDoubleLunarMode
            || HaveLunar
            || HaveSolar
            || CanSpendOnePBBeforeNextEvenWithoutBreakingTwoPB(strategy, anchorIn))
            score += 6f;

        if (PotionReadyIn() <= anchorIn + ActiveTuningProfile.PotionFutureWindowLeeway)
            score += 1f;

        return score;
    }

    // Number of whole intervals to add so that scheduledAt lands after threshold (or at/after it when !advancePastThreshold).
    // Computed arithmetically and bounded: a default/stale anchor (e.g. CombatStart never observed) must never spin a catch-up loop.
    private static int ScheduledAnchorCatchUpPeriods(DateTime scheduledAt, DateTime threshold, float intervalSeconds, bool advancePastThreshold)
    {
        if (scheduledAt == default || intervalSeconds <= 0)
            return 0;

        var behind = (threshold - scheduledAt).TotalSeconds;
        if (advancePastThreshold ? behind < 0 : behind <= 0)
            return 0;

        var periods = advancePastThreshold ? Math.Floor(behind / intervalSeconds) + 1 : Math.Ceiling(behind / intervalSeconds);
        return (int)Math.Min(periods, MaxScheduledAnchorCatchUpPeriods);
    }

    private bool ScheduledAnchorIsStale(DateTime scheduledAt)
        => scheduledAt != default && scheduledAt < World.CurrentTime.AddSeconds(-StaleScheduledAnchorSeconds);

    private void ClearStaleScheduledBurstAnchors()
    {
        if (ScheduledAnchorIsStale(_nextScheduledRoFBurstAt))
            _nextScheduledRoFBurstAt = default;

        if (ScheduledAnchorIsStale(_nextScheduledBrotherhoodBurstAt))
            _nextScheduledBrotherhoodBurstAt = default;
    }

    private DateTime AdvanceScheduledBurstAnchor(DateTime scheduledAt, float intervalSeconds)
    {
        if (scheduledAt == default || ScheduledAnchorIsStale(scheduledAt))
            return World.CurrentTime.AddSeconds(intervalSeconds);

        var periods = Math.Max(1, ScheduledAnchorCatchUpPeriods(scheduledAt, World.CurrentTime.AddSeconds(GCD), intervalSeconds, advancePastThreshold: true));
        return scheduledAt.AddSeconds(periods * intervalSeconds);
    }

    private void AdvanceMissedBurstAnchors(DateTime missedBy)
    {
        ClearStaleScheduledBurstAnchors();
        var roFPeriods = ScheduledAnchorCatchUpPeriods(_nextScheduledRoFBurstAt, missedBy, RoFCooldown, advancePastThreshold: true);
        if (roFPeriods > 0)
        {
            _activatedRoFBursts += roFPeriods;
            _nextScheduledRoFBurstAt = _nextScheduledRoFBurstAt.AddSeconds(roFPeriods * RoFCooldown);
        }

        var brotherhoodPeriods = ScheduledAnchorCatchUpPeriods(_nextScheduledBrotherhoodBurstAt, missedBy, BrotherhoodCooldown, advancePastThreshold: true);
        if (brotherhoodPeriods > 0)
            _nextScheduledBrotherhoodBurstAt = _nextScheduledBrotherhoodBurstAt.AddSeconds(brotherhoodPeriods * BrotherhoodCooldown);
    }

    private void AdvanceStaleDelayedBurstAnchors(float delayLeeway, bool advanceRoF, bool advanceBrotherhood)
    {
        ClearStaleScheduledBurstAnchors();
        var staleBefore = World.CurrentTime.AddSeconds(-delayLeeway);
        var roFPeriods = advanceRoF ? ScheduledAnchorCatchUpPeriods(_nextScheduledRoFBurstAt, staleBefore, RoFCooldown, advancePastThreshold: false) : 0;
        if (roFPeriods > 0)
        {
            _activatedRoFBursts += roFPeriods;
            _nextScheduledRoFBurstAt = _nextScheduledRoFBurstAt.AddSeconds(roFPeriods * RoFCooldown);
        }

        var brotherhoodPeriods = advanceBrotherhood ? ScheduledAnchorCatchUpPeriods(_nextScheduledBrotherhoodBurstAt, staleBefore, BrotherhoodCooldown, advancePastThreshold: false) : 0;
        if (brotherhoodPeriods > 0)
            _nextScheduledBrotherhoodBurstAt = _nextScheduledBrotherhoodBurstAt.AddSeconds(brotherhoodPeriods * BrotherhoodCooldown);
    }

    private bool ShouldSkipLateOddRoFAfterPhaseResume(in Strategy strategy, float scheduledRoFLateBy, int burstIndex, bool phaseEndRoFBurnWindow)
        => strategy.RoF.Value == RoFStrategy.Automatic
        && strategy.Brotherhood.Value == OffensiveStrategy.Automatic
        && BurstTimingAllowsPartyAlignment(strategy)
        && Unlocked(AID.Brotherhood)
        && burstIndex % 2 == 1
        && !phaseEndRoFBurnWindow
        && scheduledRoFLateBy >= LateOddRoFSkipThreshold;

    private bool SkipLateOddRoFAfterPhaseResume(in Strategy strategy, bool phaseEndRoFBurnWindow)
    {
        if (!Player.InCombat
            || !HaveTarget
            || FireLeft > GCD
            || _nextScheduledRoFBurstAt == default)
            return false;

        if (strategy.RoF.Value == RoFStrategy.Automatic
            && EncounterHintAllowsAutomaticBurst(strategy)
            && ReadyIn(AID.RiddleOfFire) <= AnimationLockDelay)
            return false;

        var scheduledRoFLateBy = (float)(World.CurrentTime - _nextScheduledRoFBurstAt).TotalSeconds;
        if (!ShouldSkipLateOddRoFAfterPhaseResume(strategy, scheduledRoFLateBy, _activatedRoFBursts, phaseEndRoFBurnWindow))
            return false;

        var skippedOddBurstIndex = _activatedRoFBursts;
        _nextScheduledRoFBurstAt = _nextScheduledRoFBurstAt.AddSeconds(RoFCooldown);
        ++_activatedRoFBursts;
        _automaticPBBurstIndex = -1;
        _automaticPBUsesInBurst = 0;
        _activePBNadiCyclePhase = null;
        _countedAutomaticPBBlitz = AID.None;
        ClearAutomaticPBBurstStartNadi();
        _skippedOddRoFStandalonePBBurstIndex = skippedOddBurstIndex;
        _skippedOddRoFStandalonePBExpiresAt = World.CurrentTime.AddSeconds(RoFWindowBudget);
        return true;
    }

    private void DiscardBurstForDeath(in Strategy strategy, bool roFActiveBeforeDeath, bool brotherhoodActiveBeforeDeath, bool pbActiveBeforeDeath)
    {
        if (!roFActiveBeforeDeath && !brotherhoodActiveBeforeDeath && !pbActiveBeforeDeath)
            return;

        var pbWasQueuedForUpcomingBurst = pbActiveBeforeDeath
            && !roFActiveBeforeDeath
            && _automaticPBBurstIndex == _activatedRoFBursts;

        _automaticPBBurstIndex = -1;
        _automaticPBUsesInBurst = 0;
        _activePBNadiCyclePhase = null;
        _countedAutomaticPBBlitz = AID.None;
        ClearAutomaticPBBurstStartNadi();
        if (strategy.Nadi.Value is NadiStrategy.Automatic or NadiStrategy.DoubleLunar)
            _forceEvenPhantomRushAfterDeath = true;

        if (pbWasQueuedForUpcomingBurst)
            AdvanceMissedBurstAnchors(World.CurrentTime.AddSeconds(ActiveTuningProfile.EvenPreRoFPBStartThreshold + AnimationLockDelay));
    }

    private float OpenerRoFOffset(in Strategy strategy)
    {
        return strategy.OpenerRoFOffset.Value switch
        {
            OpenerRoFOffsetStrategy.ZeroSecondBurst => 0f,
            OpenerRoFOffsetStrategy.Early58 => EarlyOpenerRoFOffset,
            OpenerRoFOffsetStrategy.PartyBurstAligned when BurstTimingAllowsPartyAlignment(strategy) => PartyBurstAlignedOpenerRoFOffset(),
            _ => StandardOpenerRoFOffset
        };
    }

    private bool TryPartyBurstAlignedOpenerRoFOffset(out float offset)
    {
        if (RaidBuffsLeft > GCD)
        {
            offset = CombatTimer;
            return offset is >= 0 and <= OpenerPartyBurstSearchWindow;
        }

        if (!float.IsNaN(RaidBuffsIn) && RaidBuffsIn < float.MaxValue / 2)
        {
            offset = CombatTimer + RaidBuffsIn;
            return offset is >= 0 and <= OpenerPartyBurstSearchWindow;
        }

        offset = default;
        return false;
    }

    private float PartyBurstAlignedOpenerRoFOffset()
    {
        return TryPartyBurstAlignedOpenerRoFOffset(out var offset) ? offset : StandardOpenerRoFOffset;
    }

    private void InitializeAutomaticBurstSchedule(in Strategy strategy)
    {
        if (_nextScheduledRoFBurstAt != default)
            return;

        var openerOffset = OpenerRoFOffset(strategy);
        var fixedOpenerOffset = strategy.OpenerRoFOffset.Value is OpenerRoFOffsetStrategy.Standard78
            or OpenerRoFOffsetStrategy.Early58
            or OpenerRoFOffsetStrategy.ZeroSecondBurst;
        // no countdown branch: out of combat the tracking is reset again in the same UpdateAutomaticPBBurstTracking call
        // CombatStart stays default until an in-combat transition was observed (e.g. module loaded mid-fight): no anchor then
        if (Player.InCombat && Manager.CombatStart != default)
        {
            var openerAt = Manager.CombatStart.AddSeconds(openerOffset);
            if (fixedOpenerOffset
                && openerOffset > AnimationLockDelay
                && openerAt <= World.CurrentTime.AddSeconds(AnimationLockDelay)
                && _activatedRoFBursts == 0
                && FireLeft <= GCD)
                openerAt = World.CurrentTime.AddSeconds(openerOffset);

            _nextScheduledRoFBurstAt = openerAt;
            _nextScheduledBrotherhoodBurstAt = openerAt;
            _openerRoFOffsetProtectedUntil = fixedOpenerOffset && openerOffset > AnimationLockDelay ? openerAt : default;
        }
    }

    private void RefreshPartyBurstAlignedOpenerSchedule(in Strategy strategy)
    {
        if (strategy.OpenerRoFOffset.Value != OpenerRoFOffsetStrategy.PartyBurstAligned
            || !BurstTimingAllowsPartyAlignment(strategy)
            || !Player.InCombat
            || _activatedRoFBursts > 0
            || FireLeft > GCD
            || PerfectBalanceLeft > 0
            || _activePBNadiCyclePhase != null
            || _automaticPBBurstIndex == 0 && _automaticPBUsesInBurst > 0
            || _nextScheduledRoFBurstAt == default
            || Manager.CombatStart == default
            || !TryPartyBurstAlignedOpenerRoFOffset(out var openerOffset))
            return;

        var openerAt = Manager.CombatStart.AddSeconds(openerOffset);
        if (openerAt < World.CurrentTime.AddSeconds(-GCD))
            return;

        _nextScheduledRoFBurstAt = openerAt;
        _nextScheduledBrotherhoodBurstAt = openerAt;
    }

    private void ResumeAutomaticBurstScheduleFromRotationMode(in Strategy strategy)
    {
        if (!_wasBasicAndChakraOvercapOnly
            || BasicAndChakraOvercapOnly(strategy)
            || !Player.InCombat
            || Player.IsDead
            || HasTranscendentStatus())
            return;

        if (OpenerRoFOffsetPending())
            return;

        var roFReadyIn = strategy.RoF.Value == RoFStrategy.Automatic
            ? Math.Max(0, ReadyIn(AID.RiddleOfFire))
            : float.MaxValue;
        var brotherhoodReadyIn = strategy.Brotherhood.Value == OffensiveStrategy.Automatic && Unlocked(AID.Brotherhood)
            ? Math.Max(0, ReadyIn(AID.Brotherhood))
            : float.MaxValue;
        if (brotherhoodReadyIn < float.MaxValue / 2 && _nextScheduledBrotherhoodBurstAt == default)
            _nextScheduledBrotherhoodBurstAt = World.CurrentTime.AddSeconds(brotherhoodReadyIn);

        var alignToSharedBurst = BurstTimingAllowsPartyAlignment(strategy)
            && roFReadyIn < float.MaxValue / 2
            && brotherhoodReadyIn < float.MaxValue / 2;
        var keepCalculatedSharedSchedule = alignToSharedBurst
            && _nextScheduledRoFBurstAt != default
            && _nextScheduledBrotherhoodBurstAt != default;
        var sharedBurstIn = keepCalculatedSharedSchedule
            ? Math.Max(ScheduledAnchorReadyIn(_nextScheduledRoFBurstAt, roFReadyIn), ScheduledAnchorReadyIn(_nextScheduledBrotherhoodBurstAt, brotherhoodReadyIn))
            : alignToSharedBurst
                ? Math.Max(roFReadyIn, brotherhoodReadyIn)
                : float.MaxValue;

        if (roFReadyIn < float.MaxValue / 2)
            _nextScheduledRoFBurstAt = World.CurrentTime.AddSeconds(alignToSharedBurst ? sharedBurstIn : roFReadyIn);

        _automaticPBBurstIndex = -1;
        _automaticPBUsesInBurst = 0;
        _activePBNadiCyclePhase = null;
        _countedAutomaticPBBlitz = AID.None;
        ClearAutomaticPBBurstStartNadi();
        var nextRoFForEven = strategy.RoF.Value == RoFStrategy.Automatic
            ? ScheduledAnchorReadyIn(_nextScheduledRoFBurstAt, roFReadyIn)
            : roFReadyIn;
        _forcePBOnRotationModeResume = strategy.PB.Value == PBStrategy.Automatic
            && TryNextEvenBurstIn(CurrentRoFBurstIndex, nextRoFForEven, out var nextEvenBurstIn)
            && CanSpendOnePBBeforeNextEvenWithoutBreakingTwoPB(strategy, nextEvenBurstIn);
    }

    private float ProjectBrotherhoodInForBurst(float nextBrotherhoodIn, float burstIn)
    {
        if (!Unlocked(AID.Brotherhood) || nextBrotherhoodIn >= float.MaxValue / 2 || burstIn >= float.MaxValue / 2)
            return nextBrotherhoodIn;

        var projected = nextBrotherhoodIn;
        var alignmentWindow = AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay;
        while (projected + alignmentWindow < burstIn)
            projected += BrotherhoodCooldown;

        return projected;
    }

    private bool QueueZeroSecondOpenerBurst(in Strategy strategy, in BurstPlan burst)
    {
        if (strategy.OpenerRoFOffset.Value != OpenerRoFOffsetStrategy.ZeroSecondBurst
            || !Player.InCombat
            || CombatTimer > ZeroSecondBurstOpenerWindow
            || _activatedRoFBursts > 0
            || !HaveTarget
            || !EncounterHintAllowsAutomaticBurst(strategy)
            || CurrentForm != Form.Raptor && PerfectBalanceLeft <= 0 && BrotherhoodLeft <= GCD)
            return false;

        if (strategy.PB.Value == PBStrategy.Automatic && PerfectBalanceLeft <= 0)
        {
            if (strategy.Brotherhood.Value == OffensiveStrategy.Automatic
                && Unlocked(AID.Brotherhood)
                && ReadyIn(AID.Brotherhood) > GCD + AnimationLockDelay)
                return false;

            if (strategy.RoF.Value == RoFStrategy.Automatic
                && ReadyIn(AID.RiddleOfFire) > GCD + AnimationLockDelay)
                return false;

            if (PBUnlocked
                && !BeastGaugeBlocksPB
                && !ShouldBlockPBForPendingReply(strategy)
                && ReadyIn(AID.PerfectBalance) <= GCD + AnimationLockDelay
                && CanWeave(AID.PerfectBalance))
            {
                PushPerfectBalanceForCycle(strategy, burst.AutomaticPBTargetUses, burst.AutomaticPBUsesInBurst, burst.AutomaticPBBurstIndex);
                return true;
            }

            return false;
        }

        if (strategy.Brotherhood.Value == OffensiveStrategy.Automatic && Unlocked(AID.Brotherhood) && BrotherhoodLeft <= GCD)
        {
            if (CanWeave(AID.Brotherhood))
            {
                PushOGCD(AID.Brotherhood, Player, OGCDPriority.Brotherhood);
                return true;
            }

            return false;
        }

        if (strategy.RoF.Value == RoFStrategy.Automatic && FireLeft <= GCD)
        {
            if (Unlocked(AID.RiddleOfFire) && CanWeave(AID.RiddleOfFire))
            {
                PushOGCD(AID.RiddleOfFire, Player, OGCDPriority.RiddleOfFire);
                return true;
            }

            return false;
        }

        return false;
    }

    private bool CanQueueCoreBurstActionFast(AID action)
        => ReadyIn(action) <= AnimationLockDelay + CoreBurstFastReadyLeeway;

    private bool QueueCoreBurstFastPack(in Strategy strategy, in BurstPlan burst)
    {
        var zeroSecondOpener = strategy.OpenerRoFOffset.Value == OpenerRoFOffsetStrategy.ZeroSecondBurst
            && Player.InCombat
            && CombatTimer <= ZeroSecondBurstOpenerWindow
            && _activatedRoFBursts == 0
            && HaveTarget
            && EncounterHintAllowsAutomaticBurst(strategy)
            && (CurrentForm == Form.Raptor || PerfectBalanceLeft > 0 || BrotherhoodLeft > GCD);

        var wantsPB = strategy.PB.Value == PBStrategy.Automatic
            && PBUnlocked
            && !BeastGaugeBlocksPB
            && !ShouldBlockPBForPendingReply(strategy)
            && PerfectBalanceLeft <= 0
            && !HasPendingMasterfulBlitz(GetCurrentBlitz().action)
            && (zeroSecondOpener || burst.UsePBAutomaticNow || burst.UsePBForNadiRepairNow);
        var wantsBrotherhood = strategy.Brotherhood.Value == OffensiveStrategy.Automatic
            && Unlocked(AID.Brotherhood)
            && BrotherhoodLeft <= GCD
            && (zeroSecondOpener || burst.UseBrotherhoodNow);
        var wantsRoF = strategy.RoF.Value == RoFStrategy.Automatic
            && Unlocked(AID.RiddleOfFire)
            && FireLeft <= GCD
            && (zeroSecondOpener || burst.UseRoFNow);

        if (!wantsPB && !wantsBrotherhood && !wantsRoF)
            return false;

        if (zeroSecondOpener && wantsPB)
        {
            if (strategy.Brotherhood.Value == OffensiveStrategy.Automatic
                && Unlocked(AID.Brotherhood)
                && ReadyIn(AID.Brotherhood) > GCD + AnimationLockDelay)
                return false;

            if (strategy.RoF.Value == RoFStrategy.Automatic
                && ReadyIn(AID.RiddleOfFire) > GCD + AnimationLockDelay)
                return false;
        }

        var queued = false;

        if (wantsPB)
        {
            if (CanQueueCoreBurstActionFast(AID.PerfectBalance))
            {
                if (burst.UsePBForNadiRepairNow)
                    PushPerfectBalanceForNadiRepair(burst.PBNadiRepairPhase, (int)(ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low + 3));
                else
                    PushPerfectBalanceForCycle(strategy, burst.AutomaticPBTargetUses, burst.AutomaticPBUsesInBurst, burst.AutomaticPBBurstIndex, (int)(ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low + 3));
                queued = true;
            }
            else if (zeroSecondOpener)
            {
                return false;
            }
        }

        if (wantsBrotherhood && CanQueueCoreBurstActionFast(AID.Brotherhood))
        {
            PushOGCD(AID.Brotherhood, Player, (int)(ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low + 2), delay: 0);
            queued = true;
        }

        if (wantsRoF && CanQueueCoreBurstActionFast(AID.RiddleOfFire))
        {
            PushOGCD(AID.RiddleOfFire, Player, (int)(ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low + 1), delay: 0);
            queued = true;
        }

        return queued;
    }

    private bool Brotherhood(in Strategy strategy, in BurstPlan burst)
    {
        switch (strategy.Brotherhood.Value)
        {
            case OffensiveStrategy.Automatic:
                if (burst.UseBrotherhoodNow)
                {
                    var priority = burst.UseBrotherhoodFixedTimingNow
                        ? (int)(ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low)
                        : (int)OGCDPriority.Brotherhood;
                    PushOGCD(AID.Brotherhood, Player, priority);
                    return true;
                }
                return false;
            case OffensiveStrategy.Force:
                PushOGCD(AID.Brotherhood, Player, OGCDPriority.Brotherhood);
                return true;
            default:
                return false;
        }
    }

    private BurstPlan BuildBurstPlan(in Strategy strategy, Enemy? target)
    {
        ProtectOpenerRoFOffsetSchedule();

        var tuning = ActiveTuningProfile;
        var automaticBurstAllowed = EncounterHintAllowsAutomaticBurst(strategy);
        var fightEndBurn = FightEndBurnEnabled(strategy);
        var trustedFightEndBurn = fightEndBurn && FightEndBurnEstimateTrusted;
        var trustedResourceBurn = TryGetResourceBurnHorizon(strategy, out var resourceBurnHorizon);
        var automaticBurstStartAllowed = CanStartAutomaticBurstAtMelee(strategy, target);
        var normalBurstTargetAvailable = HaveTarget && automaticBurstStartAllowed && EffectiveBurstUptimeIn > AnimLock + tuning.TargetAvailableUptimeRequired;
        var normalBurstTargetable = normalBurstTargetAvailable && GCD > 0;
        var resourceBurnIsPhaseEnd = trustedResourceBurn && ResourceBurnHorizonIsPhaseEnd(resourceBurnHorizon);
        var resourceBurnRemaining = trustedResourceBurn ? Math.Max(0, resourceBurnHorizon - CombatTimer) : float.MaxValue;
        var phaseEndBurstTargetable = resourceBurnIsPhaseEnd
            && HaveTarget
            && automaticBurstStartAllowed
            && GCD > 0
            && CanFitGCD(resourceBurnRemaining, 4);
        var endBurnTargetable = HaveTarget && GCD > 0 && trustedResourceBurn && (!resourceBurnIsPhaseEnd || phaseEndBurstTargetable);
        var burstTargetable = normalBurstTargetable || endBurnTargetable;
        var fixedTimingTargetable = automaticBurstAllowed && HaveTarget && automaticBurstStartAllowed && GCD > 0;
        var targetable = normalBurstTargetable || phaseEndBurstTargetable;
        var roFCooldownReadyIn = ReadyIn(AID.RiddleOfFire);
        var alignBurstTiming = BurstTimingAllowsPartyAlignment(strategy);
        var cooldownBurstTiming = strategy.BurstTiming.Value == BurstTimingStrategy.Cooldown;
        if (alignBurstTiming)
            AdvanceStaleDelayedBurstAnchors(
                tuning.RoFBrotherhoodResyncWindow,
                strategy.RoF.Value == RoFStrategy.Automatic,
                strategy.Brotherhood.Value == OffensiveStrategy.Automatic && Unlocked(AID.Brotherhood));

        var useScheduledRoFAnchor = ShouldUseScheduledRoFAnchor(strategy);
        var useScheduledBrotherhoodAnchor = ShouldUseScheduledBrotherhoodAnchor(strategy);

        var nextRoFIn = useScheduledRoFAnchor
            ? ScheduledBurstReadyIn(_nextScheduledRoFBurstAt, roFCooldownReadyIn)
            : roFCooldownReadyIn;
        var nextBrotherhoodIn = Unlocked(AID.Brotherhood)
            ? useScheduledBrotherhoodAnchor
                ? ScheduledBurstReadyIn(_nextScheduledBrotherhoodBurstAt, ReadyIn(AID.Brotherhood))
                : ReadyIn(AID.Brotherhood)
            : float.MaxValue;
        var potionReadyIn = PotionReadyIn();
        var activeRoFWindow = FireLeft > GCD;
        var activeBrotherhoodWindow = BrotherhoodLeft > GCD;
        var upcomingRoFBurstIsEven = _activatedRoFBursts % 2 == 0;
        var nextRoFWindowAt = activeRoFWindow ? 0 : nextRoFIn;
        var nextBrotherhoodWindowAt = activeBrotherhoodWindow ? 0 : nextBrotherhoodIn;
        var nextPlannedBurstWindowAt = Math.Min(nextRoFWindowAt, nextBrotherhoodWindowAt);
        var potionFutureWindow = nextBrotherhoodWindowAt + AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay + tuning.PotionFutureWindowLeeway;
        var lastRoFWindow = automaticBurstAllowed
            && trustedResourceBurn
            && burstTargetable
            && CombatTimer + nextRoFWindowAt + tuning.LastRoFWindowLeeway >= resourceBurnHorizon;
        var lastBrotherhoodWindow = automaticBurstAllowed
            && trustedResourceBurn
            && burstTargetable
            && nextBrotherhoodWindowAt < float.MaxValue / 2
            && CombatTimer + nextBrotherhoodWindowAt + tuning.LastBrotherhoodWindowLeeway >= resourceBurnHorizon;
        var lastPotionWindow = automaticBurstAllowed
            && trustedFightEndBurn
            && burstTargetable
            && EstimatedFightEnd is float estimatedPotionEnd
            && nextBrotherhoodWindowAt < float.MaxValue / 2
            && ((activeBrotherhoodWindow && potionReadyIn <= GCD) || potionReadyIn <= potionFutureWindow)
            && CombatTimer + Math.Max(nextBrotherhoodWindowAt, potionReadyIn) + tuning.LastPotionWindowLeeway >= estimatedPotionEnd;
        var lastPBWindow = automaticBurstAllowed
            && trustedResourceBurn
            && burstTargetable
            && nextPlannedBurstWindowAt < float.MaxValue / 2
            && CombatTimer + nextPlannedBurstWindowAt + AttackGCDLength * LastPBWindowGCDs + AnimationLockDelay >= resourceBurnHorizon;
        var roFMeleeSafe = MajorBurstMeleeSafe(strategy, lastRoFWindow);
        var brotherhoodMeleeSafe = MajorBurstMeleeSafe(strategy, lastBrotherhoodWindow);
        var mechanicSafeBurstTiming = BurstTimingUsesMechanicSafeFixedTiming(strategy);
        var roFBurstWindowMeleeSafe = !mechanicSafeBurstTiming || roFMeleeSafe;
        var brotherhoodMeleeGate = !mechanicSafeBurstTiming || brotherhoodMeleeSafe;
        var roFImmediateWindow = automaticBurstAllowed && targetable && roFBurstWindowMeleeSafe && strategy.RoF.Value == RoFStrategy.Automatic && nextRoFIn <= AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay;
        var evenPreRoFPBWindow = automaticBurstAllowed && targetable && roFBurstWindowMeleeSafe && strategy.RoF.Value == RoFStrategy.Automatic && EvenPreRoFPBWindow(nextRoFIn, nextBrotherhoodIn);
        var brotherhoodWindow = automaticBurstAllowed
            && targetable
            && brotherhoodMeleeGate
            && strategy.Brotherhood.Value == OffensiveStrategy.Automatic
            && nextBrotherhoodIn <= AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay;
        var cooldownRoFWindow = cooldownBurstTiming && roFImmediateWindow;
        var cooldownBrotherhoodWindow = cooldownBurstTiming && brotherhoodWindow;
        var cooldownRoFBrotherhoodWindow = cooldownBurstTiming
            && (activeRoFWindow || cooldownRoFWindow)
            && (activeBrotherhoodWindow || cooldownBrotherhoodWindow);
        var cooldownPBWindow = cooldownBurstTiming && (cooldownRoFWindow || cooldownBrotherhoodWindow);
        var burstActive = activeRoFWindow || activeBrotherhoodWindow;
        var dancingMadTopLogProfile = DancingMadTopLogProfileEnabled(strategy);
        var dancingMadTopLogPBWindow = TryGetDancingMadTopLogPBPlan(strategy, out var dancingMadTopLogPBBurstIndex, out var dancingMadTopLogPBTargetUses);
        if (!Unlocked(AID.Brotherhood))
            dancingMadTopLogPBTargetUses = Math.Min(dancingMadTopLogPBTargetUses, 1);
        var dancingMadTopLogEvenPBWindow = dancingMadTopLogPBWindow && dancingMadTopLogPBTargetUses == 2;
        var pairDelayedOpenerBrotherhoodWithRoF = ShouldPairDelayedOpenerBrotherhoodWithRoF(strategy);
        var openerRoFOffsetBlocksRecast = OpenerRoFOffsetPending()
            && strategy.OpenerRoFOffset.Value != OpenerRoFOffsetStrategy.ZeroSecondBurst
            && !dancingMadTopLogProfile;
        var dancingMadP2ToP3RoFAircast = IsDancingMadP2ToP3Downtime(strategy);
        var holdDancingMadTopLogRoF = !lastRoFWindow
            && !dancingMadP2ToP3RoFAircast
            && ShouldHoldDancingMadTopLogAction(strategy, AID.RiddleOfFire);
        var automaticRoFRecastReady = strategy.RoF.Value == RoFStrategy.Automatic
            && Unlocked(AID.RiddleOfFire)
            && (automaticBurstAllowed || dancingMadP2ToP3RoFAircast)
            && Player.InCombat
            && !Player.IsDead
            && (HaveTarget || dancingMadP2ToP3RoFAircast)
            && (GCD > 0 || dancingMadP2ToP3RoFAircast)
            && FireLeft <= GCD
            && roFCooldownReadyIn <= AnimationLockDelay
            && (!openerRoFOffsetBlocksRecast || dancingMadP2ToP3RoFAircast);
        var scheduledRoFFixedTimingNow = automaticBurstAllowed
            && fixedTimingTargetable
            && roFBurstWindowMeleeSafe
            && strategy.RoF.Value == RoFStrategy.Automatic
            && nextRoFIn <= AnimationLockDelay;
        var roFFixedTimingReady = !holdDancingMadTopLogRoF && (automaticRoFRecastReady || scheduledRoFFixedTimingNow);
        var delayedOpenerBrotherhoodCanUseThisGCD = !pairDelayedOpenerBrotherhoodWithRoF
            || activeRoFWindow
            || roFFixedTimingReady;
        var dancingMadTopLogBrotherhoodWindow = dancingMadTopLogProfile
            && automaticBurstAllowed
            && HaveTarget
            && GCD > 0
            && Unlocked(AID.Brotherhood)
            && strategy.Brotherhood.Value == OffensiveStrategy.Automatic
            && BrotherhoodLeft <= GCD
            && ReadyIn(AID.Brotherhood) <= AnimationLockDelay
            && IsInDancingMadTopLogActionWindow(strategy, AID.Brotherhood);
        var holdDancingMadTopLogBrotherhood = !lastBrotherhoodWindow
            && ShouldHoldDancingMadTopLogAction(strategy, AID.Brotherhood);
        var useBrotherhoodFixedTimingNow = !holdDancingMadTopLogBrotherhood
            && (automaticBurstAllowed
                && fixedTimingTargetable
                && brotherhoodMeleeGate
                && strategy.Brotherhood.Value == OffensiveStrategy.Automatic
                && nextBrotherhoodIn <= AnimationLockDelay
                && delayedOpenerBrotherhoodCanUseThisGCD
                || dancingMadTopLogBrotherhoodWindow);
        var zeroSecondOpenerPBWindow = !dancingMadTopLogProfile
            && strategy.OpenerRoFOffset.Value == OpenerRoFOffsetStrategy.ZeroSecondBurst
            && Player.InCombat
            && CombatTimer <= ZeroSecondBurstOpenerWindow
            && _activatedRoFBursts == 0
            && automaticBurstAllowed
            && (targetable || fixedTimingTargetable)
            && strategy.PB.Value == PBStrategy.Automatic
            && PBUnlocked
            && (activeRoFWindow || roFFixedTimingReady)
            && (!Unlocked(AID.Brotherhood) || activeBrotherhoodWindow || useBrotherhoodFixedTimingNow);
        var evenPBStartWindow = evenPreRoFPBWindow || zeroSecondOpenerPBWindow;
        var openerDoubleLunarTwoPBWindow = _activatedRoFBursts == 0
            && Unlocked(AID.Brotherhood)
            && strategy.PB.Value == PBStrategy.Automatic
            && PBUnlocked
            && automaticBurstAllowed
            && IsDoubleLunarMode(strategy, 2, 0)
            && (evenPBStartWindow || activeRoFWindow || cooldownPBWindow);
        var evenWindow = brotherhoodWindow || evenPBStartWindow || openerDoubleLunarTwoPBWindow || dancingMadTopLogEvenPBWindow;
        var evenPBBurstWindow = Unlocked(AID.Brotherhood)
            && (evenPBStartWindow || openerDoubleLunarTwoPBWindow || dancingMadTopLogEvenPBWindow || activeRoFWindow && activeBrotherhoodWindow || cooldownRoFBrotherhoodWindow);
        var pbAutoAvailable = PBUnlocked
            && strategy.PB.Value == PBStrategy.Automatic
            && !BeastGaugeBlocksPB
            && !ShouldBlockPBForPendingReply(strategy)
            && PerfectBalanceLeft == 0;
        var pbNearOvercap = PBUnlocked && MaxChargesIn(AID.PerfectBalance) <= PBNearOvercapWindow;
        var pbRoFWindow = activeRoFWindow || evenPBStartWindow || cooldownPBWindow || dancingMadTopLogPBWindow || lastPBWindow;
        var pbBurstIndex = dancingMadTopLogPBWindow
            ? dancingMadTopLogPBBurstIndex
            : AutomaticPBBurstIndex(activeRoFWindow, pbRoFWindow);
        var automaticPBTargetUses = dancingMadTopLogPBWindow
            ? dancingMadTopLogPBTargetUses
            : pbRoFWindow ? evenPBBurstWindow ? 2 : 1 : 0;
        var pbUsesInBurst = AutomaticPBUsesForBurst(pbBurstIndex);
        var oddPBWindow = activeRoFWindow && pbRoFWindow && automaticPBTargetUses == 1 && !evenPBBurstWindow;
        var holdOddPhantomRushForEven = oddPBWindow
            && HoldOddPhantomRushForEvenBurst(strategy, automaticPBTargetUses, pbUsesInBurst, pbBurstIndex, nextRoFIn, nextBrotherhoodIn, automaticBurstAllowed, pbNearOvercap, tuning);
        var evenSecondPBWindow = activeRoFWindow && automaticPBTargetUses == 2 && pbUsesInBurst > 0;
        var evenSecondPBBlitzFitsRoF = evenSecondPBWindow && CanFitGCD(FireLeft, 3);
        var evenSecondPBLatestForRoFBlitz = evenSecondPBBlitzFitsRoF && !CanFitGCD(FireLeft, 4);
        var evenSecondPBRecover = evenSecondPBWindow && !evenSecondPBBlitzFitsRoF;
        var pbPreferred = dancingMadTopLogPBWindow
            || (evenPBStartWindow && pbUsesInBurst == 0
                ? EvenPBPreferredForm(nextRoFIn)
                : CurrentForm == Form.Raptor);
        var pbDeadline = evenSecondPBLatestForRoFBlitz
            || evenSecondPBRecover
            || (FireLeft > GCD && !CanFitGCD(FireLeft, 3))
            || lastBrotherhoodWindow && nextBrotherhoodWindowAt <= AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay;
        var pbBurstMustStartNow = BurstPBShouldIgnorePreferredForm(
            pbPreferred,
            pbDeadline,
            activeRoFWindow,
            activeBrotherhoodWindow,
            evenPreRoFPBWindow,
            automaticPBTargetUses,
            pbUsesInBurst,
            nextRoFIn,
            nextBrotherhoodIn,
            ResourceHorizonClamp(strategy, EffectiveBurstUptimeIn));
        var activeBurstPBRecoveryTargetable = (burstActive || cooldownPBWindow)
            && HaveTarget
            && GCD > 0
            && automaticBurstStartAllowed
            && pbRoFWindow
            && automaticPBTargetUses > 0
            && pbUsesInBurst < automaticPBTargetUses;
        var pbCanReachBlitzBeforeMeleeLoss = !MeaningfulForcedOut || CanFitGCD(EffectiveMeleeBurstUptimeIn, 4);
        var pbMeleeSafe = pbCanReachBlitzBeforeMeleeLoss
            && (lastPBWindow
                || EffectiveMeleeBurstUptimeIn > AttackGCDLength * tuning.PBMeleeSafeGCDs + AnimationLockDelay
                || activeBurstPBRecoveryTargetable);
        var evenPreRoFPBTimingAllowed = evenPBStartWindow && pbUsesInBurst == 0
            && (EvenPreRoFPBShouldStartNow(nextRoFIn, pbPreferred, pbDeadline) || pbBurstMustStartNow);
        var pbTimingAllowed = dancingMadTopLogPBWindow
            || (oddPBWindow
                ? !holdOddPhantomRushForEven && (pbDeadline || pbBurstMustStartNow || OddPBTimingAllowsNow(pbNearOvercap))
                : evenPBStartWindow && pbUsesInBurst == 0
                    ? evenPreRoFPBTimingAllowed
                    : pbPreferred || pbDeadline || pbBurstMustStartNow);
        var pbTargetable = targetable
            || lastPBWindow && endBurnTargetable
            || dancingMadTopLogPBWindow && automaticBurstAllowed && HaveTarget && automaticBurstStartAllowed && GCD > 0
            || activeBurstPBRecoveryTargetable;
        var resumePBStart = _forcePBOnRotationModeResume
            && automaticBurstAllowed
            && targetable
            && pbAutoAvailable
            && pbMeleeSafe
            && pbRoFWindow
            && pbTimingAllowed
            && ReadyIn(AID.PerfectBalance) <= GCD + AnimationLockDelay;
        var usePBAutomaticNow = resumePBStart
            || automaticBurstAllowed
            && pbTargetable
            && pbMeleeSafe
            && pbAutoAvailable
            && pbTimingAllowed
            && pbRoFWindow
            && pbUsesInBurst < automaticPBTargetUses;
        var pbNadiRepairPhase = NadiCyclePhase.LunarSolar;
        var nadiRepairPBWindow = pbRoFWindow
            && pbTargetable
            && pbMeleeSafe
            && pbAutoAvailable
            && pbUsesInBurst < automaticPBTargetUses;
        if (_skippedOddRoFStandalonePBBurstIndex >= 0 && World.CurrentTime > _skippedOddRoFStandalonePBExpiresAt)
            ClearSkippedOddRoFStandalonePBWindow();
        var skippedOddRoFStandalonePBWindow = false;
        if (_skippedOddRoFStandalonePBBurstIndex >= 0
            && World.CurrentTime <= _skippedOddRoFStandalonePBExpiresAt
            && !pbRoFWindow
            && targetable
            && pbMeleeSafe
            && pbAutoAvailable
            && TryNextEvenBurstIn(CurrentRoFBurstIndex, nextRoFIn, out var skippedOddNextRoFEvenBurstIn))
        {
            var skippedOddNextEvenBurstIn = nextBrotherhoodIn < float.MaxValue / 2
                ? Math.Min(skippedOddNextRoFEvenBurstIn, nextBrotherhoodIn)
                : skippedOddNextRoFEvenBurstIn;
            skippedOddRoFStandalonePBWindow = CanSpendOnePBBeforeNextEvenWithoutBreakingTwoPB(strategy, skippedOddNextEvenBurstIn)
                && NextEvenBurstGuaranteedForOddPhantomRushHold(skippedOddNextEvenBurstIn, nextBrotherhoodIn, automaticBurstAllowed, tuning);
        }
        var usePBForNadiRepairNow = false;
        if (!usePBAutomaticNow)
        {
            usePBForNadiRepairNow = (nadiRepairPBWindow || skippedOddRoFStandalonePBWindow)
                && ShouldRepairDoubleLunarNadiForEvenPhantomRush(
                    strategy,
                    nextRoFIn,
                    nextBrotherhoodIn,
                    automaticBurstAllowed,
                    pbNearOvercap,
                    tuning,
                    out pbNadiRepairPhase);
        }

        var useBrotherhoodNow = strategy.Brotherhood.Value switch
        {
            OffensiveStrategy.Automatic => !holdDancingMadTopLogBrotherhood
                && automaticBurstAllowed
                && (useBrotherhoodFixedTimingNow
                || lastBrotherhoodWindow
                    && endBurnTargetable
                    && brotherhoodMeleeGate
                    && CanWeave(AID.Brotherhood)
                || targetable
                    && brotherhoodMeleeGate
                    && CanWeave(AID.Brotherhood)
                    && brotherhoodWindow
                    && delayedOpenerBrotherhoodCanUseThisGCD
                    && (CombatTimer > EarlyBrotherhoodCombatDelay || BeastCount >= 2 || _activatedRoFBursts > 0)),
            OffensiveStrategy.Force => true,
            _ => false
        };

        var roFBrotherhoodSyncRequired = Unlocked(AID.Brotherhood)
            && strategy.RoF.Value == RoFStrategy.Automatic
            && strategy.Brotherhood.Value == OffensiveStrategy.Automatic
            && (alignBurstTiming || ShouldUseOpenerOffsetSchedule(strategy))
            && upcomingRoFBurstIsEven;
        var brotherhoodFreshEnoughForRoF = BrotherhoodLeft > RoFWindowBudget - AttackGCDLength - AnimationLockDelay;
        var roFBrotherhoodSyncReady = !roFBrotherhoodSyncRequired
            || useBrotherhoodNow
            || brotherhoodFreshEnoughForRoF;
        var automaticRoFRecastNow = !holdDancingMadTopLogRoF
            && automaticRoFRecastReady
            && (dancingMadP2ToP3RoFAircast || !roFBrotherhoodSyncRequired || roFBrotherhoodSyncReady);
        var useRoFFixedTimingNow = automaticRoFRecastNow || !holdDancingMadTopLogRoF && scheduledRoFFixedTimingNow;
        var useRoFNow = strategy.RoF.Value switch
        {
            RoFStrategy.Automatic => !holdDancingMadTopLogRoF
                && (automaticRoFRecastNow
                || automaticBurstAllowed
                && roFBrotherhoodSyncReady
                && (useRoFFixedTimingNow
                || lastRoFWindow
                    && endBurnTargetable
                    && roFMeleeSafe
                    && CanWeave(AID.RiddleOfFire)
                || targetable
                    && roFMeleeSafe
                    && CanWeave(AID.RiddleOfFire)
                    && roFImmediateWindow)),
            RoFStrategy.Force => true,
            RoFStrategy.ForceMidWeave => true,
            _ => false
        };

        var phase = burstActive
            ? BurstPhase.RecoverBurst
            : useBrotherhoodNow || useRoFNow || usePBAutomaticNow || usePBForNadiRepairNow
                ? BurstPhase.BurstNow
                : evenWindow
                    ? BurstPhase.HoldForBurst
                    : pbAutoAvailable || BeastCount > 0
                        ? BurstPhase.BurstReady
                        : BurstPhase.None;

        if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.Burst))
            LogGuide73("Burst", $"t={CombatTimer:f1} gcd={GCD:f2}/{AttackGCDLength:f2} lock={AnimationLockDelay:f2}/{AnimLock:f2} rof={nextRoFIn:f2} bh={nextBrotherhoodIn:f2} activeRoF={activeRoFWindow} activeBH={activeBrotherhoodWindow} useRoF={useRoFNow} useBH={useBrotherhoodNow} immediate={roFImmediateWindow} evenPB={evenPreRoFPBWindow} target={targetable} uptime={EffectiveBurstUptimeIn:f2}");
        if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.PB))
            LogGuide73("PB", $"t={CombatTimer:f1} form={CurrentForm}/{EffectiveForm} use={usePBAutomaticNow} pref={pbPreferred} deadline={pbDeadline} timing={pbTimingAllowed} targetUses={automaticPBTargetUses} uses={pbUsesInBurst} pbLeft={PerfectBalanceLeft:f2} stacks={PerfectBalance.Stacks} beast={BeastCount} lunar={HaveLunar} solar={HaveSolar} wr={WindsReplyLeft:f2} fr={FiresReplyLeft:f2} meleeSafe={pbMeleeSafe} rofIn={nextRoFIn:f2}");
        if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.MeleeSafe))
            LogTuning("MeleeSafe", $"t={CombatTimer:f1} meaningful={MeaningfulForcedOut} loss={_meleePrediction.MeleeLossIn:f2} resume={_meleePrediction.MeleeResumeIn:f2} duration={_meleePrediction.ForcedOutDuration:f2} canNow={_meleePrediction.CanMeleeNow} canNext={_meleePrediction.CanMeleeNextGCD} reachNext={_meleePrediction.CanReachSafeMeleeNextGCD} effBurst={EffectiveBurstUptimeIn:f2} effMeleeBurst={EffectiveMeleeBurstUptimeIn:f2}");
        if (ShouldEmitTuningLog(strategy, MNKTuningLogStrategy.FightEnd))
            LogTuning("FightEnd", $"t={CombatTimer:f1} targetEnd={TuningValue(EstimatedTargetEnd)} phaseEnd={TuningValue(EstimatedPhaseEnd)} downtime={TuningValue(EstimatedDowntimeStart)} fightEnd={TuningValue(EstimatedFightEnd)} horizon={TuningValue(EstimatedBurstHorizon)} conf={EstimatedFightEndConfidence} lastRoF={lastRoFWindow} lastBH={lastBrotherhoodWindow} lastPB={lastPBWindow} lastPot={lastPotionWindow}");

        return new(
            phase,
            evenWindow,
            strategy.RoF.Value is RoFStrategy.Automatic or RoFStrategy.ForceMidWeave,
            pbPreferred,
            pbDeadline,
            automaticPBTargetUses,
            pbUsesInBurst,
            pbBurstIndex,
            useBrotherhoodNow,
            useRoFNow,
            useBrotherhoodFixedTimingNow,
            useRoFFixedTimingNow,
            automaticRoFRecastNow,
            usePBAutomaticNow,
            usePBForNadiRepairNow,
            pbNadiRepairPhase,
            nextRoFIn,
            nextBrotherhoodIn,
            EstimatedFightEnd,
            lastPBWindow,
            lastBrotherhoodWindow,
            lastRoFWindow,
            lastPotionWindow);
    }

    private (bool Use, bool LateWeave) ShouldRoF(in Strategy strategy, in BurstPlan burst)
    {
        return strategy.RoF.Value switch
        {
            RoFStrategy.Automatic => (burst.UseRoFNow, burst.RoFLate),
            RoFStrategy.Force => (true, false),
            RoFStrategy.ForceMidWeave => (true, true),
            _ => (false, false)
        };
    }

    private float NextPlannedRoFBurstIn()
    {
        var scheduledAt = _nextScheduledRoFBurstAt;
        if (scheduledAt == default || ScheduledAnchorIsStale(scheduledAt))
            return float.MaxValue;

        var periods = ScheduledAnchorCatchUpPeriods(scheduledAt, World.CurrentTime.AddSeconds(GCD), RoFCooldown, advancePastThreshold: true);
        scheduledAt = scheduledAt.AddSeconds(periods * RoFCooldown);
        return Math.Max(0, (float)(scheduledAt - World.CurrentTime).TotalSeconds);
    }

    private float NextPlannedBrotherhoodBurstIn()
    {
        var scheduledAt = _nextScheduledBrotherhoodBurstAt;
        if (scheduledAt == default || ScheduledAnchorIsStale(scheduledAt))
            return float.MaxValue;

        var periods = ScheduledAnchorCatchUpPeriods(scheduledAt, World.CurrentTime, BrotherhoodCooldown, advancePastThreshold: false);
        scheduledAt = scheduledAt.AddSeconds(periods * BrotherhoodCooldown);
        return Math.Max(0, (float)(scheduledAt - World.CurrentTime).TotalSeconds);
    }

    private (AID action, bool isTargeted) GetCurrentBlitz()
    {
        if (BeastCount != 3 || !BlitzUnlocked)
            return (AID.None, false);

        if (HaveBothNadi)
            return (Unlocked(AID.PhantomRush) ? AID.PhantomRush : AID.TornadoKick, true);

        var firstBeast = BeastAt(0);
        var secondBeast = BeastAt(1);
        var thirdBeast = BeastAt(2);
        if (firstBeast == secondBeast && secondBeast == thirdBeast)
            return (Unlocked(AID.ElixirBurst) ? AID.ElixirBurst : AID.ElixirField, false);
        if (firstBeast != secondBeast && secondBeast != thirdBeast && firstBeast != thirdBeast)
            return (Unlocked(AID.RisingPhoenix) ? AID.RisingPhoenix : AID.FlintStrike, false);
        return (AID.CelestialRevolution, true);
    }

    private bool HasPendingMasterfulBlitz(AID currentBlitz)
        => currentBlitz != AID.None;

    private static bool IsMasterfulBlitzAction(AID action)
        => action is AID.ElixirField
            or AID.FlintStrike
            or AID.CelestialRevolution
            or AID.TornadoKick
            or AID.ElixirBurst
            or AID.RisingPhoenix
            or AID.PhantomRush;

    private bool PBUnlocked => Unlocked(AID.PerfectBalance);
    private bool BlitzUnlocked => Unlocked(AID.ElixirField) || Unlocked(AID.ElixirBurst);
    private bool NadiUnlocked => Unlocked(AID.ElixirField) || Unlocked(AID.FlintStrike) || Unlocked(AID.ElixirBurst) || Unlocked(AID.RisingPhoenix);
    private bool BeastGaugeBlocksPB => BlitzUnlocked && BeastCount > 0;

    private BeastChakraType BeastAt(int index)
        => BeastChakra != null && (uint)index < (uint)BeastChakra.Length ? BeastChakra[index] : BeastChakraType.None;

    public int BeastCount => _beastCount; // read ~30 times a frame, counted once when the gauge is read
    public bool ForcedLunar => BeastCount > 1 && BeastAt(0) == BeastAt(1) && !HaveBothNadi;
    public bool ForcedSolar => BeastCount > 1 && BeastAt(0) != BeastAt(1) && !HaveBothNadi;
    public int PBGCDsLeft => PerfectBalance.Stacks + (PBUnlocked && ReadyIn(AID.PerfectBalance) <= GCD ? 3 : 0);

    private bool ShouldSkipFormlessForDancingMadP2FinalEvenBurst(in BurstPlan burst)
    {
        if (CurrentTuningContentId != 1094
            || burst.AutomaticPBTargetUses != 2
            || burst.AutomaticPBUsesInBurst >= 2)
            return false;

        var stateMachine = Bossmods.ActiveModule?.StateMachine;
        if (stateMachine?.ActivePhaseIndex != 1 || !TryGetPlannerPhaseBurnHorizon(out var phaseHorizon))
            return false;

        var remainingBlitzes = burst.AutomaticPBTargetUses - burst.AutomaticPBUsesInBurst;
        var gcdsToFinish = 1 + 4 * (remainingBlitzes - 1);
        var phaseRemaining = Math.Max(0, phaseHorizon - CombatTimer);
        return CanFitGCD(phaseRemaining, gcdsToFinish - 1)
            && !CanFitGCD(phaseRemaining, gcdsToFinish);
    }

    private bool ShouldConsumeFormlessBeforePendingBlitz(in Strategy strategy, AID currentBlitz, float effectiveBlitzLeft, in BurstPlan burst)
    {
        if (ShouldSkipFormlessForDancingMadP2FinalEvenBurst(burst))
            return false;

        if (FormShiftLeft <= GCD)
            return false;

        if (CurrentForm != Form.None)
            return false;

        if (PerfectBalanceLeft > GCD)
            return false;

        if (!BlitzUnlocked || currentBlitz == AID.None || NumBlitzTargets <= 0)
            return false;

        if (strategy.Blitz.Value == BlitzStrategy.Force)
            return false;

        if (!CanFitGCD(effectiveBlitzLeft, 1))
            return false;

        var activeBuffLeft = Math.Max(ResourceHorizonClamp(strategy, FireLeft), ResourceHorizonClamp(strategy, BrotherhoodLeft));
        if (activeBuffLeft > GCD && !CanFitGCD(activeBuffLeft, 1))
            return false;

        return true;
    }

    private bool ShouldUseBlitzBeforeFormlessGCD(in Strategy strategy, AID currentBlitz, in BurstPlan burst)
    {
        var decision = EvaluateBlitzDecision(strategy, currentBlitz, burst);
        return !decision.ConsumeFormlessFirst && decision.Use;
    }

    private bool CoreBurstGCDTakesPriorityOverFormless(in Strategy strategy, AID currentBlitz, in BurstPlan burst)
    {
        if (FormShiftLeft <= GCD)
            return false;

        if (PerfectBalanceLeft > GCD && BeastCount < 3)
            return false;

        if (ShouldUseBlitzBeforeFormlessGCD(strategy, currentBlitz, burst))
            return true;

        if (strategy.FiresReply.Value != FRStrategy.Delay
            && FiresReplyLeft > GCD
            && !CanFitGCD(ResourceHorizonClamp(strategy, FiresReplyLeft), 1))
            return true;

        if (strategy.WindsReply.Value != WRStrategy.Delay
            && WindsReplyLeft > GCD
            && !CanFitGCD(ResourceHorizonClamp(strategy, WindsReplyLeft), 1))
            return true;

        return false;
    }

    private bool ShouldAllowAutomaticPBNow(in Strategy strategy, in BurstPlan burst)
    {
        if (strategy.PB.Value != PBStrategy.Automatic)
            return true;

        if (FireLeft > GCD
            || BrotherhoodLeft > GCD
            || burst.UseRoFNow
            || burst.UseBrotherhoodNow
            || burst.EvenWindow
            || burst.AutomaticPBTargetUses == 2)
            return true;

        if (burst.UsePBForNadiRepairNow
            || burst.PBDeadline
            || burst.LastPBWindow
            || MaxChargesIn(AID.PerfectBalance) <= PBNearOvercapWindow)
            return true;

        return strategy.OpenerRoFOffset.Value == OpenerRoFOffsetStrategy.ZeroSecondBurst
            && Player.InCombat
            && CombatTimer <= ZeroSecondBurstOpenerWindow
            && _activatedRoFBursts == 0;
    }

    private bool ShouldHoldAutomaticPhantomRushForBurst(in Strategy strategy, AID currentBlitz, in BurstPlan burst, bool fightEndBurn, bool blitzExpiring, bool plannedPBContinuationBlitz, bool pbOvercapUnlockBlitz, float effectiveBlitzLeft)
    {
        if (strategy.Blitz.Value == BlitzStrategy.Force)
            return false;

        if (currentBlitz is not (AID.TornadoKick or AID.PhantomRush) || !HaveBothNadi)
            return false;

        if (fightEndBurn
            || blitzExpiring
            || plannedPBContinuationBlitz
            || pbOvercapUnlockBlitz
            || FireLeft > GCD
            || BrotherhoodLeft > GCD
            || burst.UseRoFNow
            || burst.UseBrotherhoodNow
            || burst.EvenWindow
            || burst.AutomaticPBTargetUses == 2)
            return false;

        if (_automaticPBBurstIndex < 0 || _automaticPBUsesInBurst <= 0)
            return false;

        var nextBuffIn = Math.Min(burst.NextRoFIn, burst.NextBrotherhoodIn);
        return nextBuffIn > GCD
            && nextBuffIn + GCD < effectiveBlitzLeft;
    }

    private BlitzDecision EvaluateBlitzDecision(in Strategy strategy, AID currentBlitz, in BurstPlan burst)
    {
        if (!BlitzUnlocked || currentBlitz == AID.None || NumBlitzTargets <= 0)
            return default;

        var effectiveBlitzLeft = ResourceHorizonClamp(strategy, BlitzLeft);
        if (ShouldConsumeFormlessBeforePendingBlitz(strategy, currentBlitz, effectiveBlitzLeft, burst))
            return new(true, false, false);

        var fightEndBurn = effectiveBlitzLeft < BlitzLeft;
        var blitzExpiring = !CanFitGCD(effectiveBlitzLeft, 1);
        var canHoldForRoF = burst.NextRoFIn > 0 && burst.NextRoFIn + GCD < effectiveBlitzLeft;
        var nextBuffIn = Math.Min(burst.NextRoFIn, burst.NextBrotherhoodIn);
        var burstBuffActive = FireLeft > GCD || BrotherhoodLeft > GCD;
        var currentPhantomRushReady = (currentBlitz is AID.TornadoKick or AID.PhantomRush) && BeastCount == 3 && HaveBothNadi;
        var canHoldCurrentPhantomRushForBuff = currentPhantomRushReady
            && !burstBuffActive
            && (burst.UseRoFNow || burst.UseBrotherhoodNow || nextBuffIn > 0 && nextBuffIn + GCD < effectiveBlitzLeft);
        var useCurrentPhantomRushNow = currentPhantomRushReady && !canHoldCurrentPhantomRushForBuff;
        var holdForQueuedRoF = burst.UseRoFNow && FireLeft <= GCD && CanFitGCD(effectiveBlitzLeft, 1);
        var holdForPlannedRoF = !fightEndBurn
            && FireLeft <= GCD
            && (holdForQueuedRoF || canHoldForRoF && burst.Phase is BurstPhase.HoldForBurst or BurstPhase.BurstNow);
        var dancingMadTopLogPBContinuationBlitz = DancingMadTopLogProfileEnabled(strategy)
            && PerfectBalanceLeft <= GCD
            && BeastCount == 3
            && currentBlitz is not (AID.TornadoKick or AID.PhantomRush);
        var plannedPBContinuationBlitz = burst.AutomaticPBTargetUses > 1 && burst.AutomaticPBTargetUses > burst.AutomaticPBUsesInBurst
            || dancingMadTopLogPBContinuationBlitz;
        var pbOvercapUnlockBlitz = ShouldUseBlitzToUnlockPBOvercap(strategy, currentBlitz, burst);
        var holdAutomaticPhantomRushForBurst = ShouldHoldAutomaticPhantomRushForBurst(strategy, currentBlitz, burst, fightEndBurn, blitzExpiring, plannedPBContinuationBlitz, pbOvercapUnlockBlitz, effectiveBlitzLeft);
        var burstMeleeCompression = MeaningfulForcedOut
            && burstBuffActive
            && !CanFitGCD(EffectiveMeleeBurstUptimeIn, 2);
        // Nothing to hold the blitz for when no buff window lands before it expires. Below level 68 that is always the
        // case - there is no Riddle of Fire yet, and AutomaticPBTargetUses is gated on pbRoFWindow, so *no* urgency
        // flag ever fires. The blitz then sat until blitzExpiring, whose one-GCD margin it kept missing: measured at
        // level 60-67 that was 0 blitzes used and every beast chakra dropped.
        var noBuffWindowBeforeBlitzExpires = !burstBuffActive
            && (nextBuffIn >= float.MaxValue / 2 || nextBuffIn + GCD >= effectiveBlitzLeft);
        var urgentNow = _prioritizePendingBlitzAfterMechanicHold || useCurrentPhantomRushNow || fightEndBurn || burstBuffActive || burstMeleeCompression || blitzExpiring || plannedPBContinuationBlitz || pbOvercapUnlockBlitz || noBuffWindowBeforeBlitzExpires;
        var use = strategy.Blitz.Value switch
        {
            BlitzStrategy.Automatic => !holdAutomaticPhantomRushForBurst && urgentNow,
            BlitzStrategy.RoF => fightEndBurn || FireLeft > GCD || !holdForPlannedRoF && !canHoldForRoF,
            BlitzStrategy.Multi => fightEndBurn || NumBlitzTargets > 1,
            BlitzStrategy.MultiRoF => fightEndBurn || NumBlitzTargets > 1 && (FireLeft > GCD || !holdForPlannedRoF && !canHoldForRoF),
            BlitzStrategy.Force => true,
            _ => false
        };

        return new(false, use, urgentNow);
    }

    private int AutomaticPBBurstIndex(bool activeRoFWindow, bool upcomingRoFWindow)
        => activeRoFWindow
            ? Math.Max(0, _activatedRoFBursts - 1)
            : upcomingRoFWindow
                ? _activatedRoFBursts
                : -1;

    private int AutomaticPBUsesForBurst(int burstIndex)
        => burstIndex >= 0 && _automaticPBBurstIndex == burstIndex ? _automaticPBUsesInBurst : 0;

    private bool EvenPreRoFPBWindow(float nextRoFIn, float nextBrotherhoodIn)
        => nextBrotherhoodIn < float.MaxValue / 2
        && Math.Abs(nextBrotherhoodIn - nextRoFIn) <= AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay
        && nextRoFIn >= EvenPreRoFPBMinThreshold
        && nextRoFIn <= ActiveTuningProfile.EvenPreRoFPBStartThreshold;

    private bool EvenPBPreferredForm(float nextRoFIn)
    {
        if (CurrentForm == Form.Raptor)
            return true;

        // 7.3 guide fallback: if the normal Opo-after PB misses the RoF/BH timing,
        // allow PB after the second combo step so RoF/BH do not drift.
        return CurrentForm == Form.Coeurl
            && nextRoFIn <= AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay
            && nextRoFIn > AnimationLockDelay;
    }

    private bool EvenPreRoFPBShouldStartNow(float nextRoFIn, bool pbPreferred, bool pbDeadline)
    {
        if (pbDeadline)
            return true;

        return pbPreferred && nextRoFIn <= ActiveTuningProfile.EvenPreRoFPBStartThreshold;
    }

    private float EstimatedPBMaxChargesInAfterSpendingOne()
    {
        var maxChargesIn = MaxChargesIn(AID.PerfectBalance);
        return maxChargesIn <= AnimationLockDelay
            ? PerfectBalanceCooldown
            : maxChargesIn + PerfectBalanceCooldown;
    }

    private bool CanSpendOnePBBeforeNextEvenWithoutBreakingTwoPB(in Strategy strategy, float nextEvenBurstIn)
    {
        if (!PBUnlocked || BeastGaugeBlocksPB || PerfectBalanceLeft > 0 || ShouldBlockPBForPendingReply(strategy))
            return false;

        if (ReadyIn(AID.PerfectBalance) > GCD + AnimationLockDelay)
            return false;

        return EstimatedPBMaxChargesInAfterSpendingOne() <= nextEvenBurstIn + PBUseSpacing + AnimationLockDelay;
    }

    private void ClearSkippedOddRoFStandalonePBWindow()
    {
        _skippedOddRoFStandalonePBBurstIndex = -1;
        _skippedOddRoFStandalonePBExpiresAt = default;
    }

    private void StopChasingIncompleteEvenBurstAfterBuffs(bool activeRoFWindow, bool activeBrotherhoodWindow)
    {
        if (activeRoFWindow
            || activeBrotherhoodWindow
            || PerfectBalanceLeft > 0
            || BlitzLeft > GCD
            || _automaticPBBurstIndex < 0
            || _automaticPBBurstIndex % 2 != 0
            || _automaticPBUsesInBurst >= 2)
            return;

        _automaticPBBurstIndex = -1;
        _automaticPBUsesInBurst = 0;
        _activePBNadiCyclePhase = null;
        _countedAutomaticPBBlitz = AID.None;
        ClearAutomaticPBBurstStartNadi();
        _forcePBOnRotationModeResume = false;
        ClearSkippedOddRoFStandalonePBWindow();
    }

    private void ResetAutomaticPBBurstTracking()
    {
        _activatedRoFBursts = 0;
        _automaticPBBurstIndex = -1;
        _automaticPBUsesInBurst = 0;
        _nextScheduledRoFBurstAt = default;
        _nextScheduledBrotherhoodBurstAt = default;
        _openerRoFOffsetProtectedUntil = default;
        _openerThunderclapLandingOffset = -1;
        _wasDeadForBurstAlignment = false;
        _hadRaisePenaltyForBurstAlignment = false;
        _wasBasicAndChakraOvercapOnly = false;
        _wasInCombatForPBBurstTracking = false;
        _activePBNadiCyclePhase = null;
        _countedAutomaticPBBlitz = AID.None;
        ClearAutomaticPBBurstStartNadi();
        _forcePBOnRotationModeResume = false;
        _forceEvenPhantomRushAfterDeath = false;
        _previousNextGCD = AID.None;
        ClearSkippedOddRoFStandalonePBWindow();
    }

    private void ClearCrossCombatPBBurstSnapshot()
    {
        _crossCombatPBBurstSnapshot = null;
    }

    private void CaptureCrossCombatPBBurstSnapshot()
    {
        var contentId = CurrentTuningContentId;
        if (CrossCombatPBBurstSnapshotSeconds(contentId) <= 0)
        {
            ClearCrossCombatPBBurstSnapshot();
            return;
        }

        _crossCombatPBBurstSnapshot = new(
            World.CurrentTime,
            HaveLunar,
            HaveSolar,
            BeastCount,
            _automaticPBBurstIndex,
            _automaticPBUsesInBurst,
            _activatedRoFBursts,
            ReadyIn(AID.RiddleOfFire),
            Unlocked(AID.Brotherhood) ? ReadyIn(AID.Brotherhood) : float.MaxValue,
            ReadyIn(AID.PerfectBalance),
            contentId);
    }

    private bool CurrentGaugeMatchesCrossCombatPBBurstSnapshot(in CrossCombatPBBurstSnapshot snapshot)
        => HaveLunar == snapshot.HaveLunar
        && HaveSolar == snapshot.HaveSolar
        && BeastCount == snapshot.BeastCount;

    private void ClearAutomaticPBBurstStartNadi()
    {
        _automaticPBBurstStartNadiIndex = -1;
        _automaticPBBurstStartNadi = default;
    }

    private void CaptureAutomaticPBBurstStartNadi(int burstIndex)
    {
        if (burstIndex < 0)
            return;

        _automaticPBBurstStartNadiIndex = burstIndex;
        _automaticPBBurstStartNadi = Nadi;
    }

    private bool AutomaticPBBurstStartedWithLunarOnly(int burstIndex)
        => burstIndex >= 0
        && _automaticPBBurstStartNadiIndex == burstIndex
        && _automaticPBBurstStartNadi.HasFlag(NadiFlags.Lunar)
        && !_automaticPBBurstStartNadi.HasFlag(NadiFlags.Solar);

    private bool CrossCombatPBBurstSnapshotUsable()
    {
        if (_crossCombatPBBurstSnapshot is not CrossCombatPBBurstSnapshot snapshot)
            return false;

        var snapshotSeconds = CrossCombatPBBurstSnapshotSeconds(snapshot.ContentId);
        if (Player.IsDead
            || HasRaiseOrWeaknessPenalty
            || HasTranscendentStatus()
            || snapshot.ContentId != CurrentTuningContentId
            || snapshotSeconds <= 0
            || (World.CurrentTime - snapshot.EndedAt).TotalSeconds > snapshotSeconds
            || !CurrentGaugeMatchesCrossCombatPBBurstSnapshot(snapshot))
        {
            ClearCrossCombatPBBurstSnapshot();
            return false;
        }

        return PBUnlocked
            && !BeastGaugeBlocksPB
            && PerfectBalanceLeft == 0
            && ReadyIn(AID.PerfectBalance) <= GCD + AnimationLockDelay;
    }

    private void UpdateAutomaticPBBurstTracking(in Strategy strategy, Enemy? target)
    {
        ClearStaleScheduledBurstAnchors();
        InitializeAutomaticBurstSchedule(strategy);
        ProtectOpenerRoFOffsetSchedule();

        var raiseOrWeakness = HasRaiseOrWeaknessPenalty;
        var transcendent = HasTranscendentStatus();
        if (Player.IsDead || raiseOrWeakness || transcendent || _crossCombatPBBurstSnapshot is { } snapshot && snapshot.ContentId != CurrentTuningContentId)
            ClearCrossCombatPBBurstSnapshot();

        if (!Player.InCombat && !Player.IsDead && !transcendent)
        {
            if (!raiseOrWeakness && _wasInCombatForPBBurstTracking)
                CaptureCrossCombatPBBurstSnapshot();

            ResetAutomaticPBBurstTracking();
        }
        else
        {
            var tuning = ActiveTuningProfile;
            if (Player.IsDead && !_wasDeadForBurstAlignment)
                DiscardBurstForDeath(strategy, _previousFireLeft > 0, _previousBrotherhoodLeft > 0, _previousPerfectBalanceLeft > 0);

            var automaticBurstAllowed = EncounterHintAllowsAutomaticBurst(strategy);
            var trustedResourceBurn = TryGetResourceBurnHorizon(strategy, out var resourceBurnHorizon);
            // must match BuildBurstPlan's window gates, otherwise a PB started while melee is unreachable gets filed
            // against a burst index that BuildBurstPlan does not believe in
            var automaticBurstStartAllowed = CanStartAutomaticBurstAtMelee(strategy, target);
            var normalBurstTargetable = automaticBurstAllowed && HaveTarget && automaticBurstStartAllowed && EffectiveBurstUptimeIn > AnimLock + tuning.TargetAvailableUptimeRequired && GCD > 0;
            var resourceBurnIsPhaseEnd = trustedResourceBurn && ResourceBurnHorizonIsPhaseEnd(resourceBurnHorizon);
            var resourceBurnRemaining = trustedResourceBurn ? Math.Max(0, resourceBurnHorizon - CombatTimer) : float.MaxValue;
            var phaseEndBurstTargetable = automaticBurstAllowed
                && resourceBurnIsPhaseEnd
                && HaveTarget
                && automaticBurstStartAllowed
                && GCD > 0
                && CanFitGCD(resourceBurnRemaining, 4);
            var endBurnTargetable = automaticBurstAllowed && HaveTarget && GCD > 0 && trustedResourceBurn && (!resourceBurnIsPhaseEnd || phaseEndBurstTargetable);
            var targetable = normalBurstTargetable || endBurnTargetable;
            var activeRoFWindow = FireLeft > GCD;
            var activeBrotherhoodWindow = BrotherhoodLeft > GCD;
            StopChasingIncompleteEvenBurstAfterBuffs(activeRoFWindow, activeBrotherhoodWindow);
            RefreshPartyBurstAlignedOpenerSchedule(strategy);
            var burstUnavailableFromDeath = Player.IsDead || transcendent;
            if (burstUnavailableFromDeath)
                AdvanceMissedBurstAnchors(World.CurrentTime.AddSeconds(GCD));
            else
            {
                SynchronizeRoFBrotherhoodBurstSchedule(strategy, target?.Actor, BasicAndChakraOvercapOnly(strategy));
                if (BurstTimingAllowsPartyAlignment(strategy))
                    AdvanceStaleDelayedBurstAnchors(
                        tuning.RoFBrotherhoodResyncWindow,
                        strategy.RoF.Value == RoFStrategy.Automatic,
                        strategy.Brotherhood.Value == OffensiveStrategy.Automatic && Unlocked(AID.Brotherhood));
            }

            var nextRoFIn = ReadyIn(AID.RiddleOfFire);
            var alignBurstTiming = BurstTimingAllowsPartyAlignment(strategy);
            var useScheduledRoFAnchor = ShouldUseScheduledRoFAnchor(strategy);
            var useScheduledBrotherhoodAnchor = ShouldUseScheduledBrotherhoodAnchor(strategy);
            var scheduledNextRoFIn = useScheduledRoFAnchor
                ? ScheduledBurstReadyIn(_nextScheduledRoFBurstAt, nextRoFIn)
                : nextRoFIn;
            var nextBrotherhoodIn = Unlocked(AID.Brotherhood) ? ReadyIn(AID.Brotherhood) : float.MaxValue;
            var scheduledNextBrotherhoodIn = useScheduledBrotherhoodAnchor
                ? ScheduledBurstReadyIn(_nextScheduledBrotherhoodBurstAt, nextBrotherhoodIn)
                : nextBrotherhoodIn;
            var phaseEndRoFBurnWindow = resourceBurnIsPhaseEnd
                && trustedResourceBurn
                && targetable
                && CombatTimer + (activeRoFWindow ? 0 : scheduledNextRoFIn) + tuning.LastRoFWindowLeeway >= resourceBurnHorizon;
            if (SkipLateOddRoFAfterPhaseResume(strategy, phaseEndRoFBurnWindow))
                scheduledNextRoFIn = useScheduledRoFAnchor
                    ? ScheduledBurstReadyIn(_nextScheduledRoFBurstAt, nextRoFIn)
                    : nextRoFIn;

            var lastRoFWindow = trustedResourceBurn
                && targetable
                && CombatTimer + (activeRoFWindow ? 0 : scheduledNextRoFIn) + tuning.LastRoFWindowLeeway >= resourceBurnHorizon;
            var roFMeleeSafe = MajorBurstMeleeSafe(strategy, lastRoFWindow);
            var mechanicSafeBurstTiming = BurstTimingUsesMechanicSafeFixedTiming(strategy);
            var roFBurstWindowMeleeSafe = !mechanicSafeBurstTiming || roFMeleeSafe;
            var roFImmediateWindow = targetable
                && roFBurstWindowMeleeSafe
                && strategy.RoF.Value == RoFStrategy.Automatic
                && scheduledNextRoFIn <= AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay;
            var evenPreRoFPBWindow = targetable
                && roFBurstWindowMeleeSafe
                && strategy.RoF.Value == RoFStrategy.Automatic
                && EvenPreRoFPBWindow(scheduledNextRoFIn, scheduledNextBrotherhoodIn);
            var zeroSecondOpenerPBWindow = strategy.OpenerRoFOffset.Value == OpenerRoFOffsetStrategy.ZeroSecondBurst
                && Player.InCombat
                && CombatTimer <= ZeroSecondBurstOpenerWindow
                && _activatedRoFBursts == 0
                && automaticBurstAllowed
                && HaveTarget
                && GCD > 0
                && strategy.PB.Value == PBStrategy.Automatic
                && PBUnlocked
                && (activeRoFWindow || scheduledNextRoFIn <= AnimationLockDelay)
                && (!Unlocked(AID.Brotherhood) || activeBrotherhoodWindow || scheduledNextBrotherhoodIn <= AnimationLockDelay);
            var delayedOpenerDoubleLunarPBWindow = ShouldTrackDelayedOpenerDoubleLunarPBBurst(strategy, scheduledNextRoFIn);
            var dancingMadTopLogPBWindow = TryGetDancingMadTopLogPBPlan(strategy, out var dancingMadTopLogPBBurstIndex, out _);
            var upcomingRoFWindow = automaticBurstAllowed
                && !activeRoFWindow
                && (roFImmediateWindow || evenPreRoFPBWindow || zeroSecondOpenerPBWindow || delayedOpenerDoubleLunarPBWindow || dancingMadTopLogPBWindow || lastRoFWindow);

            if (_previousFireLeft <= GCD && activeRoFWindow)
            {
                var startedRoFBurstIndex = _activatedRoFBursts;
                var startedRoFAnchorAt = _nextScheduledRoFBurstAt;
                ++_activatedRoFBursts;
                _openerRoFOffsetProtectedUntil = default;
                ClearSkippedOddRoFStandalonePBWindow();
                var keepRoFAnchor = strategy.RoF.Value == RoFStrategy.Automatic
                    && _nextScheduledRoFBurstAt != default
                    && _nextScheduledRoFBurstAt <= World.CurrentTime.AddSeconds(GCD);
                var nextRoFAnchorAt = keepRoFAnchor
                    ? AdvanceScheduledBurstAnchor(startedRoFAnchorAt, RoFCooldown)
                    : World.CurrentTime.AddSeconds(RoFCooldown);
                var shouldShiftLateOddRoFAnchor = strategy.RoF.Value == RoFStrategy.Automatic
                    && strategy.Brotherhood.Value == OffensiveStrategy.Automatic
                    && BurstTimingAllowsPartyAlignment(strategy)
                    && Unlocked(AID.Brotherhood)
                    && startedRoFBurstIndex % 2 == 1
                    && startedRoFAnchorAt != default
                    && _nextScheduledBrotherhoodBurstAt != default;
                var roFDelay = shouldShiftLateOddRoFAnchor ? (float)(World.CurrentTime - startedRoFAnchorAt).TotalSeconds : 0;
                if (roFDelay >= AttackGCDLength)
                    nextRoFAnchorAt = nextRoFAnchorAt.AddSeconds(roFDelay);

                _nextScheduledRoFBurstAt = nextRoFAnchorAt;
            }

            // only OUR Brotherhood may advance the anchor: a second monk's buff landing on us used to look like our
            // own cast and pushed the schedule 120s out, which cost 61% of our Brotherhood uses in a party with one
            if (_previousOwnBrotherhoodLeft <= GCD && OwnBrotherhoodLeft > GCD)
            {
                var keepBrotherhoodAnchor = strategy.Brotherhood.Value == OffensiveStrategy.Automatic
                    && _nextScheduledBrotherhoodBurstAt != default
                    && _nextScheduledBrotherhoodBurstAt <= World.CurrentTime.AddSeconds(GCD);
                _nextScheduledBrotherhoodBurstAt = keepBrotherhoodAnchor
                    ? AdvanceScheduledBurstAnchor(_nextScheduledBrotherhoodBurstAt, BrotherhoodCooldown)
                    : World.CurrentTime.AddSeconds(BrotherhoodCooldown);
            }

            if (_previousPerfectBalanceLeft <= 0 && PerfectBalanceLeft > 0)
            {
                var burstIndex = dancingMadTopLogPBWindow
                    ? dancingMadTopLogPBBurstIndex
                    : AutomaticPBBurstIndex(activeRoFWindow, upcomingRoFWindow);
                if (burstIndex >= 0)
                {
                    if (_automaticPBBurstIndex != burstIndex)
                    {
                        _automaticPBBurstIndex = burstIndex;
                        _automaticPBUsesInBurst = 0;
                        _countedAutomaticPBBlitz = AID.None;
                        CaptureAutomaticPBBurstStartNadi(burstIndex);
                    }

                    _countedAutomaticPBBlitz = AID.None;
                }
            }

            if (PerfectBalanceLeft <= 0)
                _activePBNadiCyclePhase = null;
        }

        _wasDeadForBurstAlignment = Player.IsDead;
        _hadRaisePenaltyForBurstAlignment = raiseOrWeakness;
        _wasBasicAndChakraOvercapOnly = BasicAndChakraOvercapOnly(strategy);
        _previousFireLeft = FireLeft;
        _previousBrotherhoodLeft = BrotherhoodLeft;
        _previousOwnBrotherhoodLeft = OwnBrotherhoodLeft;
        _previousPerfectBalanceLeft = PerfectBalanceLeft;
        _wasInCombatForPBBurstTracking = Player.InCombat;
    }

    private void Prep(in Strategy strategy)
    {
        bool lunar;
        switch (strategy.PB.Value)
        {
            case PBStrategy.DowntimeSolar:
                lunar = false;
                break;
            case PBStrategy.DowntimeLunar:
                lunar = true;
                break;
            default:
                return;
        }

        if (PerfectBalanceLeft == 0)
            return;

        var deadlineAll = Math.Min(UptimeIn ?? float.MaxValue, PerfectBalanceLeft) - PBPrepDeadlineBuffer;
        var gcdsLeft = 3 - BeastCount;
        var deadlineNext = deadlineAll - gcdsLeft * AttackGCDLength;
        var prepAction = lunar
            ? BestPBGCDForBeast(BeastChakraType.OpoOpo)
            : BestPBGCDForBeast(BeastCount switch
            {
                0 => BeastChakraType.Coeurl,
                1 => BeastChakraType.Raptor,
                _ => BeastChakraType.OpoOpo
            });
        if (prepAction != AID.None)
            PushGCD(prepAction, Player, GCDPriority.Basic, deadlineNext);
    }

    private Form GetEffectiveForm(in Strategy strategy, in BurstPlan burst)
    {
        if (PerfectBalanceLeft == 0)
        {
            if (Unlocked(AID.SnapPunch))
                return CurrentForm;

            if (Unlocked(AID.TrueStrike))
                return CurrentForm == Form.Raptor ? Form.Raptor : Form.OpoOpo;

            return Form.OpoOpo;
        }

        var cyclePhase = _activePBNadiCyclePhase ?? DetermineNadiCyclePhase(strategy, burst);
        if (cyclePhase is NadiCyclePhase.ForcedLunar or NadiCyclePhase.AutomaticSecondLunar)
            return Form.OpoOpo;
        var forcedSolar = cyclePhase == NadiCyclePhase.ForcedSolar;

        var canCoeurl = forcedSolar;
        var canRaptor = forcedSolar;
        var canOpo = true;

        // BeastAt, not a direct walk of BeastChakra: every other reader goes through the bounds/null-safe helper
        for (var i = 0; i < 3; ++i)
        {
            var chak = BeastAt(i);
            canCoeurl &= chak != BeastChakraType.Coeurl;
            canRaptor &= chak != BeastChakraType.Raptor;
            if (forcedSolar)
                canOpo &= chak != BeastChakraType.OpoOpo;
        }

        // nice conditional
        return canOpo && OpoStacks == 0
            ? Form.OpoOpo
            : canRaptor && RaptorStacks == 0
                ? Form.Raptor
                : canCoeurl
                    ? Form.Coeurl
                    : canRaptor
                        ? Form.Raptor
                        : Form.OpoOpo;
    }

    private bool AutomaticNadiShouldUseLunarSolar(in Strategy strategy, int automaticPBTargetUses, int automaticPBUsesInBurst)
    {
        if (!TryGetNadiProjectionHorizon(out var estimatedEnd))
            return false;

        var remaining = estimatedEnd - CombatTimer;
        if (remaining <= AttackGCDLength * 3 + AnimationLockDelay)
            return false;

        return ProjectedPhantomRushCount(lunarSolarMode: true, remaining, automaticPBTargetUses, automaticPBUsesInBurst)
            > ProjectedPhantomRushCount(lunarSolarMode: false, remaining, automaticPBTargetUses, automaticPBUsesInBurst);
    }

    private int ProjectedPhantomRushCount(bool lunarSolarMode, float remaining, int automaticPBTargetUses, int automaticPBUsesInBurst, int? currentBurstIndexOverride = null)
    {
        var lunar = HaveLunar;
        var solar = HaveSolar;
        var count = 0;
        void applyPB(float pbAt, int targetUses, int useIndex)
        {
            var blitzAt = pbAt + AttackGCDLength * 3 + AnimationLockDelay;
            if (blitzAt > remaining)
                return;

            if (lunar && solar)
            {
                ++count;
                lunar = false;
                solar = false;
                return;
            }

            var useSolar = lunarSolarMode
                ? lunar && !solar
                : targetUses == 1 || lunar && !solar && !(targetUses == 2 && useIndex > 0);

            if (useSolar)
                solar = true;
            else
                lunar = true;
        }

        if (BeastCount == 3 && lunar && solar && GCD <= remaining)
        {
            ++count;
            lunar = false;
            solar = false;
        }

        for (var i = automaticPBUsesInBurst; i < automaticPBTargetUses; ++i)
            applyPB((i - automaticPBUsesInBurst) * PBUseSpacing, automaticPBTargetUses, i);

        var nextBurstIn = NextPlannedRoFBurstIn();
        var activeRoFWindow = FireLeft > GCD;
        var currentBurstIndex = currentBurstIndexOverride ?? (activeRoFWindow
            ? Math.Max(0, _activatedRoFBursts - 1)
            : automaticPBTargetUses > 0
                ? _activatedRoFBursts
                : -1);
        if (currentBurstIndex >= 0 && !activeRoFWindow && nextBurstIn < CurrentBurstScheduleOverlapWindow)
            nextBurstIn += RoFCooldown;

        var burstIndex = currentBurstIndex >= 0 ? currentBurstIndex + 1 : _activatedRoFBursts;
        for (var burstAt = nextBurstIn; burstAt <= remaining; burstAt += RoFCooldown, ++burstIndex)
        {
            var targetUses = burstIndex % 2 == 0 ? 2 : 1;
            for (var i = 0; i < targetUses; ++i)
                applyPB(burstAt + i * PBUseSpacing, targetUses, i);
        }

        return count;
    }

    private int BestProjectedPhantomRushCount(float remaining, int automaticPBTargetUses, int automaticPBUsesInBurst, int? currentBurstIndexOverride = null)
        => Math.Max(
            ProjectedPhantomRushCount(lunarSolarMode: true, remaining, automaticPBTargetUses, automaticPBUsesInBurst, currentBurstIndexOverride),
            ProjectedPhantomRushCount(lunarSolarMode: false, remaining, automaticPBTargetUses, automaticPBUsesInBurst, currentBurstIndexOverride));

    private bool ProjectedPhantomRushCountPreservedByHoldingOddPB(in Strategy strategy, int automaticPBTargetUses, int automaticPBUsesInBurst, int automaticPBBurstIndex)
    {
        if (!TryGetNadiProjectionHorizon(out var estimatedEnd))
            return false;

        var remaining = estimatedEnd - CombatTimer;
        if (remaining <= AttackGCDLength * 3 + AnimationLockDelay)
            return false;

        return BestProjectedPhantomRushCount(remaining, 0, 0, automaticPBBurstIndex)
            >= BestProjectedPhantomRushCount(remaining, automaticPBTargetUses, automaticPBUsesInBurst);
    }

    // index of the burst RoF is currently running, or of the upcoming one when no RoF window is active.
    // _activatedRoFBursts is incremented the moment RoF goes up, so during a window it already names the *next* burst;
    // handing that to TryNextEvenBurstIn (which advances by one itself while RoF is up) would skip a whole 60s cycle.
    private int CurrentRoFBurstIndex => FireLeft > GCD ? Math.Max(0, _activatedRoFBursts - 1) : _activatedRoFBursts;

    private bool TryNextEvenBurstIn(int currentBurstIndex, float nextRoFIn, out float nextEvenBurstIn)
    {
        nextEvenBurstIn = float.MaxValue;
        if (currentBurstIndex < 0 || nextRoFIn >= float.MaxValue / 2)
            return false;

        var burstIndex = FireLeft > GCD ? currentBurstIndex + 1 : currentBurstIndex;
        var burstIn = nextRoFIn;
        while (burstIndex % 2 != 0)
        {
            ++burstIndex;
            burstIn += RoFCooldown;
        }

        nextEvenBurstIn = burstIn;
        return nextEvenBurstIn > GCD;
    }

    private bool NextEvenBurstGuaranteedForOddPhantomRushHold(float nextEvenBurstIn, float nextBrotherhoodIn, bool automaticBurstAllowed, in MNKTuningProfile tuning)
    {
        var fullBuffWindow = nextEvenBurstIn + RoFWindowBudget;
        var projectedBrotherhoodIn = ProjectBrotherhoodInForBurst(nextBrotherhoodIn, nextEvenBurstIn);
        var brotherhoodAligned = !Unlocked(AID.Brotherhood)
            || projectedBrotherhoodIn < float.MaxValue / 2
            && Math.Abs(projectedBrotherhoodIn - nextEvenBurstIn) <= AttackGCDLength * BurstImmediateWindowGCDs + AnimationLockDelay;

        return automaticBurstAllowed
            && HaveTarget
            && brotherhoodAligned
            && EffectiveBurstUptimeIn > fullBuffWindow
            && EffectiveMeleeBurstUptimeIn > nextEvenBurstIn + AttackGCDLength * tuning.PBMeleeSafeGCDs + AnimationLockDelay
            && EffectiveMeleeBurstUptimeIn > fullBuffWindow;
    }

    private bool ShouldRepairDoubleLunarNadiForEvenPhantomRush(
        in Strategy strategy,
        float nextRoFIn,
        float nextBrotherhoodIn,
        bool automaticBurstAllowed,
        bool pbNearOvercap,
        in MNKTuningProfile tuning,
        out NadiCyclePhase repairPhase)
    {
        repairPhase = NadiCyclePhase.LunarSolar;
        if (!PBUnlocked
            || !BlitzUnlocked
            || strategy.PB.Value != PBStrategy.Automatic
            || strategy.Blitz.Value != BlitzStrategy.Automatic
            || !IsDoubleLunarMode(strategy, 0, 0)
            // the repair buys both nadi for the *next* even burst; a charge spent inside a running Riddle of Fire
            // belongs to the burst happening now, and measured over the combat matrix it only trades Perfect Balances
            // and dropped beast chakra for no extra Phantom Rush
            || FireLeft > GCD
            || PerfectBalanceLeft > 0
            || ShouldBlockPBForPendingReply(strategy)
            || BeastGaugeBlocksPB
            || !HaveTarget
            || GCD <= 0
            || !automaticBurstAllowed
            || PhaseEndResourceBurnWindow(strategy, RoFWindowBudget + tuning.EvenPreRoFPBStartThreshold + AnimationLockDelay)
            || !TryDoubleLunarNadiRepairPhase(out repairPhase)
            || !TryNextEvenBurstIn(CurrentRoFBurstIndex, nextRoFIn, out var nextEvenBurstIn))
            return false;

        if (nextEvenBurstIn < PBNearOvercapWindow)
            return false;

        if (!NextEvenBurstGuaranteedForOddPhantomRushHold(nextEvenBurstIn, nextBrotherhoodIn, automaticBurstAllowed, tuning))
            return false;

        var pbMaxChargesIn = MaxChargesIn(AID.PerfectBalance);
        if (!pbNearOvercap && pbMaxChargesIn > nextEvenBurstIn + PBUseSpacing + AnimationLockDelay)
            return false;

        return true;
    }

    private bool HoldOddPhantomRushForEvenBurst(in Strategy strategy, int automaticPBTargetUses, int automaticPBUsesInBurst, int automaticPBBurstIndex, float nextRoFIn, float nextBrotherhoodIn, bool automaticBurstAllowed, bool pbNearOvercap, in MNKTuningProfile tuning)
        => HaveBothNadi
        && BeastCount == 0
        && automaticPBTargetUses == 1
        && automaticPBUsesInBurst == 0
        && automaticPBBurstIndex % 2 == 1
        && !PhaseEndResourceBurnWindow(strategy, RoFWindowBudget + ActiveTuningProfile.EvenPreRoFPBStartThreshold + AnimationLockDelay)
        && (!IsDoubleLunarMode(strategy, automaticPBTargetUses, automaticPBUsesInBurst)
            || CanReachEvenPhantomRushFromNadiState(HaveLunar, HaveSolar, automaticPBBurstIndex, nextRoFIn, nextBrotherhoodIn, automaticBurstAllowed, tuning))
        && TryNextEvenBurstIn(automaticPBBurstIndex, nextRoFIn, out var nextEvenBurstIn)
        && ProjectedPhantomRushCountPreservedByHoldingOddPB(strategy, automaticPBTargetUses, automaticPBUsesInBurst, automaticPBBurstIndex)
        && !pbNearOvercap
        && MaxChargesIn(AID.PerfectBalance) > nextEvenBurstIn + AnimationLockDelay
        && NextEvenBurstGuaranteedForOddPhantomRushHold(nextEvenBurstIn, nextBrotherhoodIn, automaticBurstAllowed, tuning);

    private bool CurrentCombatRecoversOddPBFromIncompleteEven(int automaticPBBurstIndex)
        => automaticPBBurstIndex > 0
        && automaticPBBurstIndex % 2 == 1
        && _automaticPBBurstIndex == automaticPBBurstIndex - 1
        && _automaticPBBurstIndex % 2 == 0
        && _automaticPBUsesInBurst == 1;

    private bool CrossCombatSnapshotRecoversOddPBFromIncompleteEven()
    {
        if (!CrossCombatPBBurstSnapshotUsable() || _crossCombatPBBurstSnapshot is not CrossCombatPBBurstSnapshot snapshot)
            return false;

        return snapshot.AutomaticPBBurstIndex >= 0
            && snapshot.AutomaticPBBurstIndex % 2 == 0
            && snapshot.AutomaticPBUsesInBurst == 1;
    }

    private bool RecoveringOddPBFromIncompleteEven(int automaticPBTargetUses, int automaticPBUsesInBurst, int automaticPBBurstIndex)
    {
        if (automaticPBTargetUses != 1 || automaticPBUsesInBurst != 0)
            return false;

        return CurrentCombatRecoversOddPBFromIncompleteEven(automaticPBBurstIndex)
            || CrossCombatSnapshotRecoversOddPBFromIncompleteEven();
    }

    private bool IsDoubleLunarMode(in Strategy strategy, int automaticPBTargetUses, int automaticPBUsesInBurst)
    {
        if (strategy.Nadi.Value == NadiStrategy.DoubleLunar)
            return true;

        if (strategy.Nadi.Value != NadiStrategy.Automatic)
            return false;

        return !AutomaticNadiShouldUseLunarSolar(strategy, automaticPBTargetUses, automaticPBUsesInBurst);
    }

    private bool TryDoubleLunarNadiRepairPhase(out NadiCyclePhase repairPhase)
    {
        if (HaveBothNadi)
        {
            repairPhase = NadiCyclePhase.LunarSolar;
            return false;
        }

        if (HaveLunar && !HaveSolar)
        {
            repairPhase = NadiCyclePhase.ForcedSolar;
            return true;
        }

        repairPhase = NadiCyclePhase.ForcedLunar;
        return true;
    }

    private void ProjectNadiAfterBlitz(AID currentBlitz, out bool lunar, out bool solar)
    {
        lunar = HaveLunar;
        solar = HaveSolar;
        switch (currentBlitz)
        {
            case AID.PhantomRush or AID.TornadoKick:
                lunar = false;
                solar = false;
                break;
            case AID.ElixirBurst or AID.ElixirField:
                lunar = true;
                break;
            case AID.RisingPhoenix or AID.FlintStrike:
                solar = true;
                break;
            case AID.CelestialRevolution:
                if (!lunar)
                    lunar = true;
                else
                    solar = true;
                break;
        }
    }

    private bool CanReachEvenPhantomRushFromNadiState(
        bool lunar,
        bool solar,
        int currentBurstIndex,
        float nextRoFIn,
        float nextBrotherhoodIn,
        bool automaticBurstAllowed,
        in MNKTuningProfile tuning)
    {
        if (!TryNextEvenBurstIn(currentBurstIndex, nextRoFIn, out var nextEvenBurstIn)
            || !NextEvenBurstGuaranteedForOddPhantomRushHold(nextEvenBurstIn, nextBrotherhoodIn, automaticBurstAllowed, tuning)
            || MaxChargesIn(AID.PerfectBalance) > nextEvenBurstIn + PBUseSpacing + AnimationLockDelay)
            return false;

        return lunar || solar;
    }

    private bool CanReachEvenPhantomRushAfterUnlockBlitz(in Strategy strategy, AID currentBlitz, in BurstPlan burst)
    {
        if (!IsDoubleLunarMode(strategy, burst.AutomaticPBTargetUses, burst.AutomaticPBUsesInBurst))
            return true;

        var automaticBurstAllowed = EncounterHintAllowsAutomaticBurst(strategy);
        ProjectNadiAfterBlitz(currentBlitz, out var lunar, out var solar);
        if (CanReachEvenPhantomRushFromNadiState(lunar, solar, CurrentRoFBurstIndex, burst.NextRoFIn, burst.NextBrotherhoodIn, automaticBurstAllowed, ActiveTuningProfile))
            return true;

        if (lunar || solar || !TryNextEvenBurstIn(CurrentRoFBurstIndex, burst.NextRoFIn, out var nextEvenBurstIn))
            return false;

        return nextEvenBurstIn >= PBNearOvercapWindow
            && CanReachEvenPhantomRushFromNadiState(true, false, CurrentRoFBurstIndex, burst.NextRoFIn, burst.NextBrotherhoodIn, automaticBurstAllowed, ActiveTuningProfile);
    }

    private NadiCyclePhase DetermineNadiCyclePhase(in Strategy strategy, int automaticPBTargetUses, int automaticPBUsesInBurst, int automaticPBBurstIndex)
    {
        if (!NadiUnlocked)
            return NadiCyclePhase.LunarSolar;

        var nadi = strategy.Nadi.Value;
        if (ForcedLunar || nadi == NadiStrategy.Lunar)
            return NadiCyclePhase.ForcedLunar;

        var useLunarSolarAutomatic = nadi == NadiStrategy.Automatic && AutomaticNadiShouldUseLunarSolar(strategy, automaticPBTargetUses, automaticPBUsesInBurst);
        var lunarSolarMode = nadi == NadiStrategy.LunarSolar || useLunarSolarAutomatic;
        var doubleLunarMode = IsDoubleLunarMode(strategy, automaticPBTargetUses, automaticPBUsesInBurst);
        var recoveringOddPB = doubleLunarMode && RecoveringOddPBFromIncompleteEven(automaticPBTargetUses, automaticPBUsesInBurst, automaticPBBurstIndex);
        if (recoveringOddPB)
        {
            if (HaveLunar && !HaveSolar)
                return NadiCyclePhase.ForcedSolar;

            if (HaveSolar && !HaveLunar)
                return NadiCyclePhase.ForcedLunar;

            if (HaveBothNadi)
                return NadiCyclePhase.LunarSolar;
        }

        if (_forceEvenPhantomRushAfterDeath
            && doubleLunarMode
            && automaticPBTargetUses == 2
            && automaticPBUsesInBurst == 0
            && !HaveLunar
            && !HaveSolar)
            _forceEvenPhantomRushAfterDeath = false;

        var phantomRushRecovery = _forceEvenPhantomRushAfterDeath
            && doubleLunarMode
            && automaticPBTargetUses == 2;
        var secondPBOfTwoUseBurst = automaticPBUsesInBurst > 0;
        var twoPBBurstStartedWithLunarOnly = AutomaticPBBurstStartedWithLunarOnly(automaticPBBurstIndex);
        var doubleLunarFirstPBMissingLunarRepair = doubleLunarMode
            && automaticPBTargetUses == 2
            && !secondPBOfTwoUseBurst
            && HaveSolar
            && !HaveLunar;
        if (doubleLunarFirstPBMissingLunarRepair)
            return NadiCyclePhase.ForcedLunar;

        var automaticSecondLunar = doubleLunarMode
            && !phantomRushRecovery
            && automaticPBTargetUses == 2
            && secondPBOfTwoUseBurst
            && (HaveSolar && !HaveLunar || HaveLunar && !HaveSolar && !twoPBBurstStartedWithLunarOnly);
        var doubleLunarOddMissingLunarRepair = doubleLunarMode
            && automaticPBTargetUses == 1
            && HaveSolar
            && !HaveLunar;
        if (doubleLunarOddMissingLunarRepair)
            return NadiCyclePhase.ForcedLunar;

        var forcedSolar = nadi == NadiStrategy.Solar
            || ForcedSolar
            || doubleLunarMode && automaticPBTargetUses == 1
            || phantomRushRecovery && HaveLunar && !HaveSolar
            || lunarSolarMode && HaveLunar && !HaveSolar
            || doubleLunarMode && HaveLunar && !HaveSolar && !automaticSecondLunar;

        return forcedSolar
            ? NadiCyclePhase.ForcedSolar
            : automaticSecondLunar
                ? NadiCyclePhase.AutomaticSecondLunar
                : NadiCyclePhase.LunarSolar;
    }

    private NadiCyclePhase DetermineNadiCyclePhase(in Strategy strategy, in BurstPlan burst)
        => DetermineNadiCyclePhase(strategy, burst.AutomaticPBTargetUses, burst.AutomaticPBUsesInBurst, burst.AutomaticPBBurstIndex);

    private int GCDsUntilPBPreferredForm()
        => CurrentForm switch
        {
            Form.Raptor => 0,
            Form.OpoOpo or Form.None => 1,
            Form.Coeurl => 2,
            _ => 1
        };

    private bool BurstPBShouldIgnorePreferredForm(
        bool pbPreferred,
        bool pbDeadline,
        bool activeRoFWindow,
        bool activeBrotherhoodWindow,
        bool evenPreRoFPBWindow,
        int automaticPBTargetUses,
        int pbUsesInBurst,
        float nextRoFIn,
        float nextBrotherhoodIn,
        float effectiveBurstUptimeIn)
    {
        if (pbPreferred || pbDeadline)
            return false;

        if (automaticPBTargetUses <= 0 || pbUsesInBurst >= automaticPBTargetUses)
            return false;

        var burstWindow = activeRoFWindow || activeBrotherhoodWindow || evenPreRoFPBWindow;
        if (!burstWindow)
            return false;

        var gcdsUntilPreferred = GCDsUntilPBPreferredForm();
        if (gcdsUntilPreferred <= 0)
            return false;

        var waitToPreferred = AttackGCDLength * gcdsUntilPreferred + AnimationLockDelay;
        var evenFirstPBOfTwoUseBurst = automaticPBTargetUses == 2
            && pbUsesInBurst == 0;
        if (evenFirstPBOfTwoUseBurst)
        {
            var secondPBBlitzAfterImmediatePB = PBUseSpacing + AttackGCDLength * 3 + AnimationLockDelay;
            var secondPBBlitzAfterPreferredPB = waitToPreferred + secondPBBlitzAfterImmediatePB;
            var rofDeadline = activeRoFWindow
                ? FireLeft
                : nextRoFIn < float.MaxValue / 2
                    ? nextRoFIn + RoFWindowBudget
                    : float.MaxValue;
            var brotherhoodDeadline = activeBrotherhoodWindow
                ? BrotherhoodLeft
                : nextBrotherhoodIn < float.MaxValue / 2
                    ? nextBrotherhoodIn + RoFWindowBudget
                    : float.MaxValue;
            var secondPBDeadline = Math.Min(effectiveBurstUptimeIn, Math.Min(rofDeadline, brotherhoodDeadline));

            if (secondPBBlitzAfterImmediatePB <= secondPBDeadline
                && secondPBBlitzAfterPreferredPB > secondPBDeadline)
                return true;
        }

        if (activeRoFWindow && !CanFitGCD(FireLeft, gcdsUntilPreferred + 3))
            return true;

        if (activeBrotherhoodWindow && !CanFitGCD(BrotherhoodLeft, gcdsUntilPreferred + 3))
            return true;

        if (!CanFitGCD(effectiveBurstUptimeIn, gcdsUntilPreferred + 3))
            return true;

        if (evenPreRoFPBWindow)
        {
            if (nextRoFIn <= waitToPreferred)
                return true;

            if (nextBrotherhoodIn <= waitToPreferred)
                return true;
        }

        return false;
    }

    // Only asked inside an active Riddle of Fire window (the odd PB window requires one), so there is no pre-RoF case: the odd PB
    // goes on a Raptor GCD, or at once when a charge is about to cap and fewer than 3 GCDs of Fire remain.
    private bool OddPBTimingAllowsNow(bool nearOvercap)
        => CurrentForm == Form.Raptor || nearOvercap && !CanFitGCD(FireLeft, 3);

    private void PushPerfectBalanceForCycle(in Strategy strategy, int automaticPBTargetUses, int automaticPBUsesInBurst, int automaticPBBurstIndex, int priority = (int)OGCDPriority.PerfectBalance)
    {
        var usesCrossCombatSnapshot = automaticPBTargetUses == 1
            && automaticPBUsesInBurst == 0
            && !CurrentCombatRecoversOddPBFromIncompleteEven(automaticPBBurstIndex)
            && CrossCombatSnapshotRecoversOddPBFromIncompleteEven();
        _activePBNadiCyclePhase = DetermineNadiCyclePhase(strategy, automaticPBTargetUses, automaticPBUsesInBurst, automaticPBBurstIndex);
        if (usesCrossCombatSnapshot)
            ClearCrossCombatPBBurstSnapshot();

        PushOGCD(AID.PerfectBalance, Player, priority);
    }

    private void PushPerfectBalanceForNadiRepair(NadiCyclePhase repairPhase, int priority = (int)OGCDPriority.PerfectBalance)
    {
        _activePBNadiCyclePhase = repairPhase;
        ClearSkippedOddRoFStandalonePBWindow();
        PushOGCD(AID.PerfectBalance, Player, priority);
    }

    private bool ShouldHoldAutomaticPBForRoF(in Strategy strategy, in BurstPlan burst)
    {
        if (strategy.PB.Value != PBStrategy.Automatic)
            return false;

        if (!Unlocked(AID.RiddleOfFire))
            return false;

        if (strategy.RoF.Value == RoFStrategy.Delay)
            return false;

        if (FireLeft > GCD || burst.LastPBWindow)
            return false;

        return burst.NextRoFIn > ActiveTuningProfile.EvenPreRoFPBStartThreshold;
    }

    private bool QueuePB(in Strategy strategy, Enemy? primaryTarget, in BurstPlan burst)
    {
        var pbstrat = strategy.PB.Value;

        if (!PBUnlocked || BeastGaugeBlocksPB || ShouldBlockPBForPendingReply(strategy) || pbstrat == PBStrategy.Delay || PerfectBalanceLeft > 0)
            return false;

        if (HasPendingMasterfulBlitz(GetCurrentBlitz().action))
            return false;

        if (pbstrat == PBStrategy.Force
            || pbstrat is PBStrategy.DowntimeSolar or PBStrategy.DowntimeLunar && primaryTarget is null
            || pbstrat == PBStrategy.ForceNoShift && FormShiftLeft == 0)
        {
            PushPerfectBalanceForCycle(strategy, burst.AutomaticPBTargetUses, burst.AutomaticPBUsesInBurst, burst.AutomaticPBBurstIndex);
            return true;
        }

        var lowLevelAutomaticPB = pbstrat == PBStrategy.Automatic && !Unlocked(AID.RiddleOfFire);
        if (pbstrat == PBStrategy.ForceOpo || lowLevelAutomaticPB)
        {
            if (CurrentForm == Form.Raptor)
            {
                PushPerfectBalanceForCycle(strategy, burst.AutomaticPBTargetUses, burst.AutomaticPBUsesInBurst, burst.AutomaticPBBurstIndex);
                return true;
            }
            return false;
        }

        if (pbstrat != PBStrategy.Automatic)
            return false;

        if (!ShouldAllowAutomaticPBNow(strategy, burst))
            return false;

        var dancingMadTopLogPBWindow = IsInDancingMadTopLogActionWindow(strategy, AID.PerfectBalance);
        if (!dancingMadTopLogPBWindow && ShouldDelayOddAutomaticPBUntilOpoGCD(strategy, burst))
            return false;

        var preservePBDuringDancingMadTopLogHold = burst.UsePBForNadiRepairNow
            || burst.PBDeadline
            || burst.LastPBWindow
            || _activatedRoFBursts > 0 && MaxChargesIn(AID.PerfectBalance) <= PBNearOvercapWindow;
        if (!preservePBDuringDancingMadTopLogHold && ShouldHoldDancingMadTopLogAction(strategy, AID.PerfectBalance))
            return false;

        if (burst.UsePBForNadiRepairNow)
        {
            PushPerfectBalanceForNadiRepair(burst.PBNadiRepairPhase);
            return true;
        }

        if (!dancingMadTopLogPBWindow && ShouldHoldAutomaticPBForRoF(strategy, burst))
            return false;

        var resumePBNow = _forcePBOnRotationModeResume && burst.UsePBAutomaticNow;
        var fightEndPBNow = burst.LastPBWindow
            && (burst.PBPreferred
                || burst.PBDeadline
                || FightTimeRemaining <= AttackGCDLength * LastPBWindowGCDs + AnimationLockDelay);
        if (!resumePBNow && !fightEndPBNow && !burst.UsePBAutomaticNow && !burst.PBDeadline && !burst.PBPreferred)
            return false;

        if (fightEndPBNow || burst.UsePBAutomaticNow)
        {
            var priority = _forcePBOnRotationModeResume
                ? (int)(ActionQueue.Priority.VeryHigh - ActionQueue.Priority.Low + 2)
                : (int)OGCDPriority.PerfectBalance;
            PushPerfectBalanceForCycle(strategy, burst.AutomaticPBTargetUses, burst.AutomaticPBUsesInBurst, burst.AutomaticPBBurstIndex, priority);
            _forcePBOnRotationModeResume = false;
            return true;
        }

        return false;
    }

    private bool ShouldUseBlitzToUnlockPBOvercap(in Strategy strategy, AID currentBlitz, in BurstPlan burst)
    {
        if (!PBUnlocked
            || !BlitzUnlocked
            || strategy.PB.Value != PBStrategy.Automatic
            || strategy.Blitz.Value != BlitzStrategy.Automatic
            || PerfectBalanceLeft > 0
            || !BeastGaugeBlocksPB
            || BeastCount != 3
            || currentBlitz == AID.None
            || NumBlitzTargets <= 0)
            return false;

        if (HaveBothNadi && currentBlitz is AID.PhantomRush or AID.TornadoKick)
            return false;

        var pbMaxChargesIn = MaxChargesIn(AID.PerfectBalance);
        var doubleLunarRepairBlocked = IsDoubleLunarMode(strategy, burst.AutomaticPBTargetUses, burst.AutomaticPBUsesInBurst)
            && !HaveBothNadi
            && TryNextEvenBurstIn(CurrentRoFBurstIndex, burst.NextRoFIn, out var nextEvenBurstIn)
            && nextEvenBurstIn >= PBNearOvercapWindow
            && pbMaxChargesIn <= nextEvenBurstIn + PBUseSpacing + AnimationLockDelay;
        if (!CanReachEvenPhantomRushAfterUnlockBlitz(strategy, currentBlitz, burst))
            return false;

        if (pbMaxChargesIn > PBNearOvercapWindow && !doubleLunarRepairBlocked)
            return false;

        if (FireLeft > GCD || BrotherhoodLeft > GCD)
            return false;

        var nextBuffIn = Math.Min(burst.NextRoFIn, burst.NextBrotherhoodIn);
        var canSafelyHoldForBuff = nextBuffIn > 0
            && nextBuffIn + GCD < pbMaxChargesIn
            && CanFitGCD(pbMaxChargesIn, 2);
        if (HaveBothNadi && currentBlitz == AID.PhantomRush && canSafelyHoldForBuff)
            return false;

        return !canSafelyHoldForBuff;
    }

    private void CountAutomaticPBBlitzCompletionFromLastCast()
    {
        var lastCast = Manager.LastCast.Data;
        var action = lastCast != null ? (AID)lastCast.Action.ID : AID.None;
        if (_automaticPBBurstIndex < 0
            || !IsMasterfulBlitzAction(action)
            || _countedAutomaticPBBlitz == action
            || _automaticPBUsesInBurst >= 2)
            return;

        ++_automaticPBUsesInBurst;
        _countedAutomaticPBBlitz = action;
        _activePBNadiCyclePhase = null;
        if (action is AID.TornadoKick or AID.PhantomRush)
            _forceEvenPhantomRushAfterDeath = false;
    }

    private void UseBlitz(in Strategy strategy, AID currentBlitz, in BurstPlan burst)
    {
        var decision = EvaluateBlitzDecision(strategy, currentBlitz, burst);

        if (decision.Use)
        {
            var priority = strategy.Blitz.Value == BlitzStrategy.Force || decision.UrgentNow
                ? GCDPriority.BlitzNow
                : currentBlitz is AID.TornadoKick or AID.PhantomRush ? GCDPriority.PR : GCDPriority.Blitz;
            PushGCD(currentBlitz, BestBlitzTarget, priority);
            _prioritizePendingBlitzAfterMechanicHold = false;
        }
    }
}
