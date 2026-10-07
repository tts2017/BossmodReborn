using BossMod.MCH;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.xan.Custom;

public sealed class MCH(RotationModuleManager manager, Actor player) : Attackxan<AID, TraitID, MCH.Strategy>(manager, player, PotionType.Dexterity)
{
    public struct Strategy : IStrategyCommon
    {
        public Track<Targeting> Targeting;
        public Track<AOEStrategy> AOE;

        [Track("Opener", InternalName = "Opener")]
        public Track<OpenerStrategy> Opener;

        [Track("Barrel Stabilizer", MinLevel = 66, Action = AID.BarrelStabilizer)]
        public Track<OffensiveStrategy> Buffs;

        [Track("Queen", MinLevel = 40, Actions = [AID.AutomatonQueen, AID.RookAutoturret])]
        public Track<QueenStrategy> Queen;

        [Track("Wildfire", InternalName = "WF", MinLevel = 45, Actions = [AID.Wildfire, AID.Detonator])]
        public Track<WildfireStrategy> Wildfire;

        [Track("Potion", InternalName = "Potion")]
        public Track<MCHPotionStrategy> Potion;

        [Track(Action = AID.Hypercharge, MinLevel = 30)]
        public Track<OffensiveStrategy> Hypercharge;

        // not including tool-related actions that have a buff status instead of a cd group
        [Track(Actions = [AID.Drill, AID.HotShot, AID.AirAnchor, AID.ChainSaw, AID.Bioblaster])]
        public Track<ToolStrategy> Tools;

        [Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)]
        public Track<MechanicHintStrategy> MechanicHints;

        readonly Targeting IStrategyCommon.Targeting => Targeting.Value;
        readonly AOEStrategy IStrategyCommon.AOE => AOE.Value;
    }

    public enum OpenerStrategy
    {
        [Option("Normal burst")]
        NormalBurst,
        [Option("0-second burst")]
        ZeroSecondBurst
    }

    public enum QueenStrategy
    {
        [Option("Summon at 50+ gauge", Targets = ActionTargets.Hostile)]
        MinGauge,
        [Option("Summon at full gauge", Targets = ActionTargets.Hostile)]
        FullGauge,
        [Option("Only summon during raid buffs, regardless of gauge", Targets = ActionTargets.Hostile)]
        RaidBuffsOnly,
        [Option("Do not summon")]
        Never,
        [Option("Automatic", Targets = ActionTargets.Hostile)]
        Automatic
    }
    public enum WildfireStrategy
    {
        [Option("Use ASAP; delay in opener until tools are used", Targets = ActionTargets.Hostile)]
        ASAP,
        [Option("Do not use")]
        Delay,
        [Option("Delay until Hypercharge window", Targets = ActionTargets.Hostile)]
        Hypercharge,
        [Option("Automatic", Targets = ActionTargets.Hostile)]
        Automatic
    }

    public enum MCHPotionStrategy
    {
        [Option("Do not use")]
        Off,
        [Option("Use in opener and even-minute bursts", Targets = ActionTargets.Self)]
        OpenerAndEvenBursts,
        [Option("Use in even-minute bursts only", Targets = ActionTargets.Self)]
        EvenBursts
    }

    public enum ToolStrategy
    {
        [Option("Automatic", Targets = ActionTargets.Hostile)]
        Automatic,
        [Option("Do not use")]
        Delay
    }

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("xan MCH [Custom]", "Machinist", "Standard rotation (xan)|Ranged", "xan", RotationModuleQuality.Basic, BitMask.Build(Class.MCH), 100).WithStrategies<Strategy>();
    }

    public int Heat; // max 100
    public int Battery; // max 100
    public bool Overheated;
    public bool HasMinion;

    public float ReassembleLeft; // max 5s
    public float WildfireLeft; // max 10s
    public float HyperchargedLeft; // max 30s
    public float ExcavatorLeft; // max 30s
    public float FMFLeft; // max 30s

    public bool Flamethrower;

    public int NumAOETargets;
    public int NumRangedAOETargets;

    private Enemy? BestAOETarget;
    private Enemy? BestRangedAOETarget;
    private Enemy? BestChainsawTarget;

    private const int ToolPriorityHigh = 20;
    private const int ToolPriorityAirAnchor = 30;
    private const int ToolPriorityDrill = 20;
    private const int ToolPriorityChainSaw = 10;
    private const int ToolPriorityFullMetalField = 8;
    private const int AutoCrossbowBreakpoint = 6;
    private const int AutoCrossbowHighTargetBreakpoint = 8;
    private const float ZeroSecondBurstOpenerWindow = 10;
    private const float RotationResumeGapSeconds = 1.5f;
    private const float BurstResyncLeadTime = 20;
    private const float BurstResyncPostWindow = 20;
    private const float ZeroSecondWildfireGaugeReserveLeadTime = 40;
    private const float QueenRaidBuffLeadTime = 5;
    private const float GenericMitigationLeadTime = 5;
    private const float GenericOpenerStallSkipSeconds = 4;  // opener stage without progress whose action is not usable: move on
    private const float GenericOpenerFillerSkipSeconds = 8; // opener stage spending fillers without progress for this long: move on
    private const uint DancingMadCFCID = 1094;

    private enum GenericOpenerStage
    {
        None,
        NormalAirAnchor,
        NormalDrill,
        NormalChainSaw,
        NormalExcavator,
        NormalFullMetalField,
        NormalHypercharge,
        NormalHeat,
        ZeroDrill,
        ZeroAirAnchor,
        ZeroHypercharge,
        ZeroHeat,
        ZeroChainSaw,
        ZeroExcavator,
        ZeroFullMetalField,
        Complete,
        ZeroSecondDrill
    }

    private enum GenericCombatStartMode
    {
        FreshOpener,
        Recovery,
        NormalRotation
    }

    private bool _genericOpenerInitialized;
    private OpenerStrategy _genericOpenerMode;
    private GenericOpenerStage _genericOpenerStage;
    private DateTime _genericOpenerLastObservedCast;
    private DateTime _genericOpenerHyperchargeCast;
    private DateTime _genericOpenerLastProgressTime;
    private DateTime _genericOpenerFillerSince; // first filler weaponskill since the last stage progress (default: none)
    private DateTime _genericOpenerTargetLostTime;
    private AID _genericOpenerLastGCD;
    private int _genericOpenerWeavesAfterLastGCD;
    private int _genericOpenerHeatGCDs;
    private bool _genericOpenerUsedStabilizer;
    private bool _genericOpenerUsedWildfire;
    private bool _genericOpenerUsedQueen;
    private bool _genericOpenerUsedExcavator;
    private bool _genericOpenerUsedPreHyperchargeCharge;
    private AID _lastWeaponskill;
    private DateTime _lastWeaponskillObservedTime;
    private bool _wasInCombat;
    private GenericCombatStartMode _combatStartMode = GenericCombatStartMode.NormalRotation;
    private DateTime _recoveryStartedAt;
    private DateTime _recoveryBurstCommittedAt;
    private bool _recoveryBurstCommitted;
    private bool _recoveryWildfireUsed;
    private bool _recoveryStabilizerUsed;
    private bool _recoverySawOverheat;
    private DateTime _lastExecutionTime;
    private bool _executionObserved;
    private bool _dancingMadProfile;
    private bool _holdingOverheatedGCDForWildfire;

    private bool IsPausedForFlamethrower => Service.Config.Get<MCHConfig>().PauseForFlamethrower && Flamethrower;

    private AID GenericOpenerFirstAction(in Strategy strategy)
        => strategy.Opener.Value == OpenerStrategy.ZeroSecondBurst
            ? BestActionUnlocked(AID.ChainSaw, AID.Drill, AID.AirAnchor, AID.HotShot)
            : BestActionUnlocked(AID.AirAnchor, AID.HotShot, AID.Drill);

    private bool IsFreshOpenerState(in Strategy strategy)
    {
        var cast = Manager.LastCast.Data;
        var recentCast = cast != null && (World.CurrentTime - Manager.LastCast.Time).TotalSeconds <= 2;
        var expectedFirstAction = GenericOpenerFirstAction(strategy);
        var expectedFirstActionUsed = recentCast && expectedFirstAction != AID.None && cast!.IsSpell(expectedFirstAction);
        // the countdown presses Reassemble again once the first tool has used the pre-pull one, so combat can start with that as the last
        // cast: a first tool whose recast started in the last 3s has been used as well
        if (!expectedFirstActionUsed && expectedFirstAction is AID.AirAnchor or AID.HotShot or AID.ChainSaw
            && ActionDefinitions.Instance.Spell(expectedFirstAction) is { } firstDef && ReadyIn(expectedFirstAction) > firstDef.Cooldown - 3)
            expectedFirstActionUsed = true;

        var wildfireFresh = !Unlocked(AID.Wildfire) || ReadyIn(AID.Wildfire) <= GCD;
        var stabilizerFresh = !Unlocked(AID.BarrelStabilizer) || ReadyIn(AID.BarrelStabilizer) <= GCD;
        var chainSawFresh = !Unlocked(AID.ChainSaw)
            || ReadyIn(AID.ChainSaw) <= GCD
            || expectedFirstActionUsed && expectedFirstAction == AID.ChainSaw;
        var drillFresh = !Unlocked(AID.Drill)
            || ReadyIn(AID.Drill) <= GCD
            || expectedFirstActionUsed && expectedFirstAction == AID.Drill;
        var airAnchorFresh = (!Unlocked(AID.AirAnchor) || ReadyIn(AID.AirAnchor) <= GCD)
            && (!Unlocked(AID.HotShot) || ReadyIn(AID.HotShot) <= GCD)
            || expectedFirstActionUsed && expectedFirstAction is AID.AirAnchor or AID.HotShot;
        var reassembleFresh = !Unlocked(AID.Reassemble)
            || ChargeInfo(AID.Reassemble).Charges >= 1
            || ReassembleLeft > 0
            || expectedFirstActionUsed;
        var remainingBatteryGain = 0;
        if ((Unlocked(AID.AirAnchor) || Unlocked(AID.HotShot))
            && !(expectedFirstActionUsed && expectedFirstAction is AID.AirAnchor or AID.HotShot))
            remainingBatteryGain += 20;
        if (Unlocked(AID.ChainSaw) && !(expectedFirstActionUsed && expectedFirstAction == AID.ChainSaw))
            remainingBatteryGain += 20;
        if (Unlocked(AID.Excavator))
            remainingBatteryGain += 20;
        var gaugesFresh = (!Unlocked(AID.BarrelStabilizer) || Heat <= 50)
            && (!Unlocked(AID.RookAutoturret) || Battery + remainingBatteryGain <= 100)
            && !HasMinion;
        var chainSawOpenerProcs = expectedFirstActionUsed && expectedFirstAction == AID.ChainSaw;
        var transientBuffsClear = !Overheated
            && WildfireLeft <= 0
            && (ExcavatorLeft <= 0 && FMFLeft <= 0 || chainSawOpenerProcs);

        return wildfireFresh
            && stabilizerFresh
            && chainSawFresh
            && drillFresh
            && airAnchorFresh
            && reassembleFresh
            && gaugesFresh
            && transientBuffsClear;
    }

    private bool ObserveRotationExecution()
    {
        var resumed = !_executionObserved
            || _lastExecutionTime == default
            || (World.CurrentTime - _lastExecutionTime).TotalSeconds >= RotationResumeGapSeconds;
        _executionObserved = true;
        _lastExecutionTime = World.CurrentTime;
        return resumed;
    }

    private void ResetRecoveryTracking()
    {
        _recoveryStartedAt = World.CurrentTime;
        _recoveryBurstCommittedAt = default;
        _recoveryBurstCommitted = false;
        _recoveryWildfireUsed = false;
        _recoveryStabilizerUsed = false;
        _recoverySawOverheat = false;
    }

    private void StartRecoveryFromLiveState(in Strategy strategy)
    {
        ClearGenericOpenerState();
        _combatStartMode = GenericCombatStartMode.Recovery;
        ResetRecoveryTracking();

        var cast = Manager.LastCast.Data;
        var recentCast = cast != null && (World.CurrentTime - Manager.LastCast.Time).TotalSeconds <= 2;
        _recoveryWildfireUsed = WildfireLeft > 0
            || WildfireRecentlyUsed()
            || recentCast && cast!.IsSpell(AID.Wildfire);
        _recoveryStabilizerUsed = HyperchargedLeft > 0
            || Unlocked(AID.BarrelStabilizer) && ReadyIn(AID.BarrelStabilizer) > 110f
            || recentCast && cast!.IsSpell(AID.BarrelStabilizer);
        _recoverySawOverheat = Overheated;

        var wildfireRequired = Unlocked(AID.Wildfire) && strategy.Wildfire.Value != WildfireStrategy.Delay;
        var recoveryActionUsed = recentCast
            && !wildfireRequired
            && (cast!.IsSpell(AID.Hypercharge) || cast.IsSpell(AID.AutomatonQueen) || cast.IsSpell(AID.RookAutoturret));
        if (RecoveryBurstStarted(strategy) || recoveryActionUsed)
        {
            _recoveryBurstCommitted = true;
            _recoveryBurstCommittedAt = World.CurrentTime;
        }
    }

    private void UpdateCombatStartMode(in Strategy strategy, bool executionResumed)
    {
        // a short execution gap while a fresh opener is already in progress must not abort it into recovery
        var keepOpener = executionResumed
            && _wasInCombat
            && _combatStartMode == GenericCombatStartMode.FreshOpener
            && GenericOpenerInProgress(strategy);
        if (Player.InCombat && (executionResumed || !_wasInCombat) && !keepOpener)
        {
            if (ShouldUseDancingMadProfile())
            {
                ClearGenericOpenerState();
                _combatStartMode = GenericCombatStartMode.NormalRotation;
                ResetRecoveryTracking();
            }
            else if (CombatTimer <= 30f && IsFreshOpenerState(strategy))
            {
                _combatStartMode = GenericCombatStartMode.FreshOpener;
                ResetRecoveryTracking();
            }
            else
            {
                StartRecoveryFromLiveState(strategy);
            }
        }
        else if (!Player.InCombat && _wasInCombat)
        {
            ClearGenericOpenerState();
            _combatStartMode = GenericCombatStartMode.NormalRotation;
        }

        _wasInCombat = Player.InCombat;
    }

    private bool IsRecoveryMode()
        => _combatStartMode == GenericCombatStartMode.Recovery && !ShouldUseDancingMadProfile();

    private float RecoveryWildfireIn()
        => Unlocked(AID.Wildfire) ? ReadyIn(AID.Wildfire) : float.MaxValue;

    private float RecoveryStabilizerIn()
        => Unlocked(AID.BarrelStabilizer) ? ReadyIn(AID.BarrelStabilizer) : float.MaxValue;

    private float RecoveryRaidBuffIn()
        => RaidBuffsLeft > 0 ? 0 : RaidBuffsIn;

    private float WildfireBurstAnchorIn(in Strategy strategy)
    {
        if (!Unlocked(AID.Wildfire) || strategy.Wildfire.Value == WildfireStrategy.Delay)
            return float.MaxValue;

        return WildfireLeft > 0 || WildfireRecentlyUsed() ? 0 : ReadyIn(AID.Wildfire);
    }

    private float StandardBurstAnchorIn(in Strategy strategy)
    {
        if (ShouldUseDancingMadProfile() || CombatTimer < 60)
            return float.MaxValue;

        var wildfireIn = WildfireBurstAnchorIn(strategy);
        if (wildfireIn < float.MaxValue)
            return wildfireIn;

        if (RaidBuffsLeft > 0)
            return 0;

        var cycle = CombatTimer % 120f;
        var scheduledBurstIn = cycle <= BurstResyncPostWindow ? 0 : 120f - cycle;

        if (RaidBuffsIn is >= 0 and <= BurstResyncLeadTime)
            return RaidBuffsIn;

        return scheduledBurstIn;
    }

    private bool StandardBurstSoon(in Strategy strategy)
    {
        var burstIn = StandardBurstAnchorIn(strategy);
        return burstIn > GCD + 1f && burstIn <= BurstResyncLeadTime;
    }

    private bool StandardBurstWindow(in Strategy strategy)
        => StandardBurstAnchorIn(strategy) <= GCD + 1f;

    private bool StandardBurstPreparationWindow(in Strategy strategy)
        => StandardBurstAnchorIn(strategy) <= GCD + GCDLength * 3 + 1f;

    private bool StandardBurstToolOrderActive(in Strategy strategy)
        => !ShouldUseDancingMadProfile()
        && !GenericOpenerInProgress(strategy)
        && !IsRecoveryMode()
        && strategy.Opener.Value == OpenerStrategy.NormalBurst
        && strategy.Tools.Value == ToolStrategy.Automatic
        && StandardBurstPreparationWindow(strategy);

    // Hot Shot has its own recast group and is replaced by Air Anchor, so it never leaves "ready" once Air Anchor is unlocked: only judge it below
    // that level. Otherwise an Air Anchor that was used a few GCDs ago (or is still on cooldown when the burst window opens) never counted as
    // complete, Drill and Chain Saw sat out the whole preparation window and a capped Drill kept Full Metal Field and Hypercharge waiting
    private bool StandardBurstAirAnchorComplete()
        => LastWeaponskillWas(AID.AirAnchor, AID.HotShot)
        || !GCDReady(AID.AirAnchor) && (Unlocked(AID.AirAnchor) || !Unlocked(AID.HotShot) || !GCDReady(AID.HotShot));

    private bool StandardBurstDrillComplete()
        => LastWeaponskillWas(AID.Drill, AID.Bioblaster)
        || !Unlocked(AID.Drill) && !Unlocked(AID.Bioblaster);

    private bool StandardBurstStabilizerCommittedOrUnavailable(in Strategy strategy)
        => strategy.Buffs.Value == OffensiveStrategy.Delay
        || !Unlocked(AID.BarrelStabilizer)
        || GenericOpenerStabilizerCommitted()
        || ReadyIn(AID.BarrelStabilizer) > GCD
        || BarrelStabilizerWouldOvercapHeat();

    private bool ShouldHoldMajorCooldownForStandardBurst(in Strategy strategy)
    {
        var burstIn = StandardBurstAnchorIn(strategy);
        return burstIn < float.MaxValue && !StandardBurstPreparationWindow(strategy);
    }

    private float RecoveryBurstAnchorIn(in Strategy strategy)
    {
        var wildfireIn = WildfireBurstAnchorIn(strategy);
        var stabilizerIn = RecoveryStabilizerIn();
        var raidBuffIn = RecoveryRaidBuffIn();

        if (wildfireIn < float.MaxValue)
            return wildfireIn;

        if (raidBuffIn is >= 0 and <= 20f)
            return raidBuffIn;

        var standardBurstIn = StandardBurstAnchorIn(strategy);
        if (standardBurstIn < float.MaxValue)
            return standardBurstIn;

        return MathF.Min(stabilizerIn, raidBuffIn);
    }

    private bool RecoveryBurstSoon(in Strategy strategy)
        => IsRecoveryMode() && RecoveryBurstAnchorIn(strategy) <= 20f;

    private bool RecoveryBurstVerySoon(in Strategy strategy)
        => IsRecoveryMode() && RecoveryBurstAnchorIn(strategy) <= 8f;

    private bool RecoveryBurstWindow(in Strategy strategy)
        => IsRecoveryMode() && RecoveryBurstAnchorIn(strategy) <= GCD + 1f;

    private float BurstAlignmentIn(in Strategy strategy)
        => IsRecoveryMode() ? RecoveryBurstAnchorIn(strategy) : StandardBurstAnchorIn(strategy);

    private bool BurstAlignmentSoon(in Strategy strategy)
        => IsRecoveryMode()
            ? RecoveryBurstSoon(strategy) && !RecoveryBurstWindow(strategy)
            : StandardBurstSoon(strategy);

    private bool BurstAlignmentVerySoon(in Strategy strategy)
    {
        var burstIn = BurstAlignmentIn(strategy);
        return burstIn > GCD + 1f && burstIn <= 8f;
    }

    private bool BurstAlignmentWindow(in Strategy strategy)
        => IsRecoveryMode() ? RecoveryBurstWindow(strategy) : StandardBurstWindow(strategy);

    private bool RecoveryWildfireCommitted()
        => _recoveryWildfireUsed || WildfireLeft > 0 || WildfireRecentlyUsed();

    private bool RecoveryBurstReady(in Strategy strategy)
    {
        if (!RecoveryBurstWindow(strategy) || ReassembleLeft > GCD)
            return false;

        if (strategy.Tools.Value == ToolStrategy.Automatic)
        {
            if (GCDReady(AID.ChainSaw))
                return false;
            if ((ActionCapSoon(AID.Drill) || ActionCapSoon(AID.Bioblaster))
                && NextGCD is not AID.Drill and not AID.Bioblaster)
                return false;
        }

        var heatReady = Heat >= 50
            || HyperchargedLeft > 0
            || _recoveryStabilizerUsed
            || strategy.Buffs.Value != OffensiveStrategy.Delay && Unlocked(AID.BarrelStabilizer) && ReadyIn(AID.BarrelStabilizer) <= GCD;
        if (!heatReady)
            return false;

        return ExcavatorLeft <= GCD || NextGCD == AID.Excavator;
    }

    private bool ShouldHoldHyperchargeForBurstAlignment(in Strategy strategy)
    {
        if (!BurstAlignmentSoon(strategy) || BurstAlignmentWindow(strategy) || Heat >= 100 || WildfireLeft > 0 || Overheated)
            return false;

        if (HyperchargedLeft > 0)
            return HyperchargedLeft > BurstAlignmentIn(strategy) + GCD;

        return Heat >= 50;
    }

    private bool RecoveryBurstStarted(in Strategy strategy)
    {
        var wildfireRequired = Unlocked(AID.Wildfire) && strategy.Wildfire.Value != WildfireStrategy.Delay;
        return RecoveryWildfireCommitted() || !wildfireRequired && (Overheated || _recoveryStabilizerUsed);
    }

    private void UpdateRecoveryState(in Strategy strategy)
    {
        if (!IsRecoveryMode())
            return;

        if (WildfireLeft > 0 || WildfireRecentlyUsed())
            _recoveryWildfireUsed = true;
        if (HyperchargedLeft > 0 || Unlocked(AID.BarrelStabilizer) && ReadyIn(AID.BarrelStabilizer) > 110f)
            _recoveryStabilizerUsed = true;

        var cast = Manager.LastCast.Data;
        var recentCast = cast != null && (World.CurrentTime - Manager.LastCast.Time).TotalSeconds <= 2;
        if (recentCast && cast!.IsSpell(AID.Wildfire))
            _recoveryWildfireUsed = true;
        if (recentCast && cast!.IsSpell(AID.BarrelStabilizer))
            _recoveryStabilizerUsed = true;

        if (Overheated)
            _recoverySawOverheat = true;

        var wildfireRequired = Unlocked(AID.Wildfire) && strategy.Wildfire.Value != WildfireStrategy.Delay;
        var recoveryActionUsed = recentCast
            && !wildfireRequired
            && (cast!.IsSpell(AID.Hypercharge) || cast.IsSpell(AID.AutomatonQueen) || cast.IsSpell(AID.RookAutoturret));
        if (!_recoveryBurstCommitted && (RecoveryBurstStarted(strategy) || recoveryActionUsed))
        {
            _recoveryBurstCommitted = true;
            _recoveryBurstCommittedAt = World.CurrentTime;
        }

        var burstFinished = _recoveryBurstCommitted
            && (_recoverySawOverheat && !Overheated
                || _recoveryBurstCommittedAt != default && (World.CurrentTime - _recoveryBurstCommittedAt).TotalSeconds >= 15);
        if (burstFinished || (World.CurrentTime - _recoveryStartedAt).TotalSeconds >= 45)
            _combatStartMode = GenericCombatStartMode.NormalRotation;
    }

    private bool IsGenericOpener(in Strategy strategy)
        => !ShouldUseDancingMadProfile()
        && Player.InCombat
        && _combatStartMode == GenericCombatStartMode.FreshOpener
        && CombatTimer <= 30f;

    private bool GenericOpenerInProgress(in Strategy strategy)
        => IsGenericOpener(strategy)
        && _genericOpenerInitialized
        && _genericOpenerStage is not GenericOpenerStage.Complete and not GenericOpenerStage.None;

    private void ClearGenericOpenerState()
    {
        _genericOpenerInitialized = false;
        _genericOpenerStage = GenericOpenerStage.None;
        _genericOpenerLastObservedCast = default;
        _genericOpenerHyperchargeCast = default;
        _genericOpenerLastProgressTime = default;
        _genericOpenerFillerSince = default;
        _genericOpenerTargetLostTime = default;
        _genericOpenerLastGCD = AID.None;
        _genericOpenerWeavesAfterLastGCD = 0;
        _genericOpenerHeatGCDs = 0;
        _genericOpenerUsedStabilizer = false;
        _genericOpenerUsedWildfire = false;
        _genericOpenerUsedQueen = false;
        _genericOpenerUsedExcavator = false;
        _genericOpenerUsedPreHyperchargeCharge = false;
    }

    private void SetGenericOpenerStage(GenericOpenerStage stage)
    {
        if (_genericOpenerStage == stage)
            return;

        _genericOpenerStage = stage;
        _genericOpenerLastProgressTime = World.CurrentTime;
        _genericOpenerFillerSince = default;
    }

    private void InitializeGenericOpenerState(in Strategy strategy)
    {
        ClearGenericOpenerState();
        _genericOpenerInitialized = true;
        _genericOpenerMode = strategy.Opener.Value;
        _genericOpenerStage = strategy.Opener.Value == OpenerStrategy.NormalBurst
            ? GenericOpenerStage.NormalAirAnchor
            : GenericOpenerStage.ZeroChainSaw;
        _genericOpenerLastProgressTime = World.CurrentTime;
    }

    private void UpdateGenericOpenerState(in Strategy strategy)
    {
        if (!IsGenericOpener(strategy))
        {
            if (!Player.InCombat || CombatTimer > 30f || ShouldUseDancingMadProfile())
                ClearGenericOpenerState();
            return;
        }

        if (!_genericOpenerInitialized || _genericOpenerMode != strategy.Opener.Value)
            InitializeGenericOpenerState(strategy);

        var cast = Manager.LastCast.Data;
        if (cast == null
            || cast.SourceSequence == 0
            || Manager.LastCast.Time == _genericOpenerLastObservedCast
            || (World.CurrentTime - Manager.LastCast.Time).TotalSeconds > 5)
            return;

        _genericOpenerLastObservedCast = Manager.LastCast.Time;

        var action = cast.IsSpell(AID.AirAnchor) ? AID.AirAnchor
            : cast.IsSpell(AID.HotShot) ? AID.HotShot
            : cast.IsSpell(AID.Drill) ? AID.Drill
            : cast.IsSpell(AID.ChainSaw) ? AID.ChainSaw
            : cast.IsSpell(AID.Excavator) ? AID.Excavator
            : cast.IsSpell(AID.FullMetalField) ? AID.FullMetalField
            : cast.IsSpell(AID.BlazingShot) ? AID.BlazingShot
            : cast.IsSpell(AID.HeatBlast) ? AID.HeatBlast
            : cast.IsSpell(AID.AutoCrossbow) ? AID.AutoCrossbow
            : AID.None;

        if (action != AID.None)
        {
            _genericOpenerLastGCD = action;
            _genericOpenerWeavesAfterLastGCD = 0;
            _genericOpenerFillerSince = default;
            AdvanceGenericOpenerAfterGCD(action);
            return;
        }

        // a filler weaponskill while a stage waits for something else (Queen, Hypercharge weave): the first one starts the filler-stall clock
        if (_genericOpenerFillerSince == default
            && (cast.IsSpell(AID.SplitShot) || cast.IsSpell(AID.HeatedSplitShot) || cast.IsSpell(AID.SlugShot) || cast.IsSpell(AID.HeatedSlugShot)
                || cast.IsSpell(AID.CleanShot) || cast.IsSpell(AID.HeatedCleanShot) || cast.IsSpell(AID.SpreadShot) || cast.IsSpell(AID.Scattergun)))
        {
            _genericOpenerFillerSince = Manager.LastCast.Time;
            return;
        }

        if (cast.IsSpell(AID.BarrelStabilizer))
            _genericOpenerUsedStabilizer = true;
        else if (cast.IsSpell(AID.Wildfire))
            _genericOpenerUsedWildfire = true;
        else if (cast.IsSpell(AID.AutomatonQueen) || cast.IsSpell(AID.RookAutoturret))
            _genericOpenerUsedQueen = true;
        else if (cast.IsSpell(AID.Hypercharge))
        {
            _genericOpenerHyperchargeCast = Manager.LastCast.Time;
            SetGenericOpenerStage(_genericOpenerMode == OpenerStrategy.NormalBurst
                ? GenericOpenerStage.NormalHeat
                : GenericOpenerStage.ZeroHeat);
        }
        else if (_genericOpenerStage == GenericOpenerStage.ZeroHypercharge
            && (cast.IsSpell(AID.GaussRound) || cast.IsSpell(AID.Ricochet)))
        {
            _genericOpenerUsedPreHyperchargeCharge = true;
        }
        else if (!cast.IsSpell(AID.Reassemble)
            && !cast.IsSpell(AID.GaussRound)
            && !cast.IsSpell(AID.Ricochet))
        {
            return;
        }

        _genericOpenerLastProgressTime = World.CurrentTime;
        ++_genericOpenerWeavesAfterLastGCD;
    }

    private void AdvanceGenericOpenerAfterGCD(AID action)
    {
        if (_genericOpenerMode == OpenerStrategy.NormalBurst)
        {
            switch (_genericOpenerStage)
            {
                case GenericOpenerStage.NormalAirAnchor when action is AID.AirAnchor or AID.HotShot:
                    SetGenericOpenerStage(GenericOpenerStage.NormalDrill);
                    break;
                case GenericOpenerStage.NormalDrill when action == AID.Drill:
                    SetGenericOpenerStage(GenericOpenerStage.NormalChainSaw);
                    break;
                case GenericOpenerStage.NormalChainSaw when action == AID.ChainSaw:
                    SetGenericOpenerStage(GenericOpenerStage.NormalExcavator);
                    break;
                case GenericOpenerStage.NormalExcavator when action == AID.Excavator:
                    _genericOpenerUsedExcavator = true;
                    SetGenericOpenerStage(GenericOpenerStage.NormalFullMetalField);
                    break;
                case GenericOpenerStage.NormalFullMetalField when action == AID.FullMetalField:
                    SetGenericOpenerStage(GenericOpenerStage.NormalHypercharge);
                    break;
                case GenericOpenerStage.NormalHeat when action is AID.BlazingShot or AID.HeatBlast or AID.AutoCrossbow:
                    if (++_genericOpenerHeatGCDs >= 5)
                        SetGenericOpenerStage(GenericOpenerStage.Complete);
                    break;
            }
        }
        else
        {
            switch (_genericOpenerStage)
            {
                case GenericOpenerStage.ZeroChainSaw when action == AID.ChainSaw:
                    SetGenericOpenerStage(GenericOpenerStage.ZeroDrill);
                    break;
                case GenericOpenerStage.ZeroDrill when action == AID.Drill:
                    SetGenericOpenerStage(GenericOpenerStage.ZeroAirAnchor);
                    break;
                case GenericOpenerStage.ZeroAirAnchor when action is AID.AirAnchor or AID.HotShot:
                    SetGenericOpenerStage(GenericOpenerStage.ZeroHypercharge);
                    break;
                case GenericOpenerStage.ZeroHeat when action is AID.BlazingShot or AID.HeatBlast or AID.AutoCrossbow:
                    if (++_genericOpenerHeatGCDs >= 5)
                        SetGenericOpenerStage(GenericOpenerStage.ZeroExcavator);
                    break;
                case GenericOpenerStage.ZeroExcavator when action == AID.Excavator:
                    _genericOpenerUsedExcavator = true;
                    SetGenericOpenerStage(GenericOpenerStage.ZeroSecondDrill);
                    break;
                case GenericOpenerStage.ZeroSecondDrill when action == AID.Drill:
                    SetGenericOpenerStage(GenericOpenerStage.ZeroFullMetalField);
                    break;
                case GenericOpenerStage.ZeroFullMetalField when action == AID.FullMetalField:
                    SetGenericOpenerStage(GenericOpenerStage.Complete);
                    break;
            }
        }
    }

    private void RecoverGenericOpenerFromLastWeaponskill(in Strategy strategy)
    {
        if (!GenericOpenerInProgress(strategy))
            return;

        if (!_genericOpenerUsedWildfire && WildfireWasCommitted())
        {
            _genericOpenerUsedWildfire = true;
            _genericOpenerLastProgressTime = World.CurrentTime;
        }

        if (!_genericOpenerUsedStabilizer && GenericOpenerStabilizerCommitted())
        {
            _genericOpenerUsedStabilizer = true;
            _genericOpenerLastProgressTime = World.CurrentTime;
        }

        if (_genericOpenerMode == OpenerStrategy.NormalBurst)
        {
            if (_genericOpenerStage == GenericOpenerStage.NormalChainSaw && LastWeaponskillWas(AID.ChainSaw))
            {
                _genericOpenerLastGCD = AID.ChainSaw;
                SetGenericOpenerStage(GenericOpenerStage.NormalExcavator);
            }

            if (_genericOpenerStage == GenericOpenerStage.NormalExcavator && LastWeaponskillWas(AID.Excavator))
            {
                _genericOpenerLastGCD = AID.Excavator;
                _genericOpenerUsedExcavator = true;
                SetGenericOpenerStage(GenericOpenerStage.NormalFullMetalField);
            }
        }
        else
        {
            if (_genericOpenerStage == GenericOpenerStage.ZeroChainSaw && LastWeaponskillWas(AID.ChainSaw))
            {
                _genericOpenerLastGCD = AID.ChainSaw;
                SetGenericOpenerStage(GenericOpenerStage.ZeroDrill);
            }

            if (_genericOpenerStage == GenericOpenerStage.ZeroExcavator && LastWeaponskillWas(AID.Excavator))
            {
                _genericOpenerLastGCD = AID.Excavator;
                _genericOpenerUsedExcavator = true;
                SetGenericOpenerStage(GenericOpenerStage.ZeroSecondDrill);
            }
        }
    }

    private void UpdateGenericOpenerFallback(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!GenericOpenerInProgress(strategy))
            return;

        if (!IsUsableToolTarget(primaryTarget))
        {
            if (_genericOpenerTargetLostTime == default)
                _genericOpenerTargetLostTime = World.CurrentTime;
            else if ((World.CurrentTime - _genericOpenerTargetLostTime).TotalSeconds >= 3)
                SetGenericOpenerStage(GenericOpenerStage.Complete);
            return;
        }

        _genericOpenerTargetLostTime = default;
        if (_genericOpenerStage is GenericOpenerStage.NormalHeat or GenericOpenerStage.ZeroHeat)
            return;

        if (GenericOpenerWildfirePending(strategy))
            return;

        // the stall fallback skips a stage only when its own action cannot be used right now: after a stun, a blocked line of sight
        // or a forced move the tool is still ready and the stage picks it up on the next free GCD, so skipping it would push the
        // Excavator / Full Metal Field / Hypercharge of the burst out of the raid buffs. A stage that keeps spending filler GCDs
        // without progressing (something it waits for never arrives) is still skipped after a longer stall
        var stalled = (World.CurrentTime - _genericOpenerLastProgressTime).TotalSeconds;
        if (stalled > GenericOpenerStallSkipSeconds && !GenericOpenerStageActionAvailable(strategy)
            || _genericOpenerFillerSince != default && (World.CurrentTime - _genericOpenerFillerSince).TotalSeconds > GenericOpenerFillerSkipSeconds)
            AdvanceGenericOpenerFallback(strategy);
    }

    private bool GenericOpenerStageActionAvailable(in Strategy strategy) => _genericOpenerStage switch
    {
        GenericOpenerStage.NormalAirAnchor or GenericOpenerStage.ZeroAirAnchor => BestActionUnlocked(AID.AirAnchor, AID.HotShot) is var anchor && anchor != AID.None && GCDReady(anchor),
        GenericOpenerStage.NormalDrill or GenericOpenerStage.ZeroDrill => Unlocked(AID.Drill) && GCDReady(AID.Drill) && MaxChargesIn(AID.Drill) <= GCD,
        GenericOpenerStage.ZeroSecondDrill => Unlocked(AID.Drill) && GCDReady(AID.Drill),
        GenericOpenerStage.NormalChainSaw or GenericOpenerStage.ZeroChainSaw => Unlocked(AID.ChainSaw) && GCDReady(AID.ChainSaw),
        GenericOpenerStage.NormalExcavator or GenericOpenerStage.ZeroExcavator => OpenerExcavatorAvailable(),
        GenericOpenerStage.NormalFullMetalField or GenericOpenerStage.ZeroFullMetalField => Unlocked(AID.FullMetalField) && FMFLeft > GCD,
        GenericOpenerStage.NormalHypercharge or GenericOpenerStage.ZeroHypercharge => GenericOpenerHyperchargePossible(strategy),
        _ => false
    };

    private void AdvanceGenericOpenerFallback(in Strategy strategy)
    {
        if (GenericOpenerWildfirePending(strategy))
            return;

        SetGenericOpenerStage(_genericOpenerStage switch
        {
            GenericOpenerStage.NormalAirAnchor => GenericOpenerStage.NormalDrill,
            GenericOpenerStage.NormalDrill => GenericOpenerStage.NormalChainSaw,
            GenericOpenerStage.NormalChainSaw => GenericOpenerStage.NormalExcavator,
            GenericOpenerStage.NormalExcavator => GenericOpenerStage.NormalFullMetalField,
            GenericOpenerStage.NormalFullMetalField => GenericOpenerStage.NormalHypercharge,
            GenericOpenerStage.NormalHypercharge => GenericOpenerStage.Complete,
            GenericOpenerStage.ZeroChainSaw => GenericOpenerStage.ZeroDrill,
            GenericOpenerStage.ZeroDrill => GenericOpenerStage.ZeroAirAnchor,
            GenericOpenerStage.ZeroAirAnchor => GenericOpenerStage.ZeroHypercharge,
            GenericOpenerStage.ZeroHypercharge => GenericOpenerStage.ZeroExcavator,
            GenericOpenerStage.ZeroExcavator => GenericOpenerStage.ZeroSecondDrill,
            GenericOpenerStage.ZeroSecondDrill => GenericOpenerStage.ZeroFullMetalField,
            GenericOpenerStage.ZeroFullMetalField => GenericOpenerStage.Complete,
            _ => GenericOpenerStage.Complete
        });
    }

    private bool TryPrecombatGenericOpenerGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        if (CountdownRemaining != null
            || Player.InCombat
            || ShouldUseDancingMadProfile()
            || !IsUsableToolTarget(primaryTarget)
            || !IsFreshOpenerState(strategy))
            return false;

        var action = GenericOpenerFirstAction(strategy);
        if (action == AID.None || !GCDReady(action))
            return false;

        PushGCD(action, primaryTarget, ToolPriorityHigh + 10);
        return true;
    }

    // FightRemaining (value-of-information experiment): the fight end, when the shared estimate knows it, is treated as a target loss that
    // never returns, so every "spec rule" path (wind-down slots, Detonator, expiring Excavator / Full Metal Field / Hypercharged, Hypercharge
    // cut-offs) works for it (VoiFightEndLoss). Two refinements, each individually switchable for the experiment: Queen Overdrive timed to
    // the end (VoiFightEndOverdrive) and a Hypercharge dump for Heat that cannot be spent later (VoiFightEndHypercharge). The end used is the
    // time by which the estimate says the fight has ended with probability VoiEndProbability (FightTimeEstimate.Quantile, the inverse of
    // EndsWithinProbability): the dump rules fire when EndsWithinProbability(their window) >= 0.65. Measured on the noisy arms of the
    // combat matrix, 0.65 beat the 10th percentile (LowerBound) at every noise level and cut the worsened scenarios by about 2/3, and a
    // confidence or horizon gate did not help (see bmr_ttk2_out report). The Hypercharge dump runs while the end is at most GCD + 0.6 + 7.5 + 6 s away.
    private const float VoiEndProbability = 0.65f;
    private static readonly bool VoiFightEndLoss = true;
    private static readonly bool VoiFightEndOverdrive = true;
    private static readonly bool VoiFightEndHypercharge = true;
    private const float VoiHyperchargeSlack = 6f;
    private MechanicForecast _voiMechanic = MechanicForecast.None;
    private bool _voiFightEnd;
    private new MechanicForecast Mechanic => _voiMechanic;

    private void ApplyFightEnd()
    {
        _voiMechanic = base.Mechanic;
        _voiFightEnd = false;
        if (!VoiFightEndLoss || !Player.InCombat)
            return;
        var fr = Hints.FightRemaining;
        // blind (Known, but no information) and unknown leave the module exactly as it was
        if (!fr.Known || fr.UpperBound >= float.MaxValue / 2)
            return;
        var end = fr.Quantile(VoiEndProbability);
        var real = base.Mechanic;
        if (real.Enabled && real.TargetLossIn <= end)
            return; // a real, earlier loss is already handled by its own rules
        _voiMechanic = real with { Mode = real.Enabled ? real.Mode : MechanicHintStrategy.All, TargetLossIn = end, TargetReturnIn = 1e9f };
        _voiFightEnd = true;
    }

    public override void Exec(in Strategy strategy, Enemy? primaryTarget)
    {
        UpdateMechanicForecast(strategy.MechanicHints.Value);
        ApplyFightEnd();
        SelectPrimaryTarget(strategy, ref primaryTarget, range: 25);

        _dancingMadProfile = IsDancingMadUltimate() && Player.InCombat;
        _holdingOverheatedGCDForWildfire = false;

        var gauge = World.Client.GetGauge<MachinistGauge>();

        Heat = gauge.Heat;
        Battery = gauge.Battery;
        Overheated = (gauge.TimerActive & 1) != 0;
        HasMinion = (gauge.TimerActive & 2) != 0;
        if (HasMinion && !_voiHadMinion)
            _voiQueenAt = World.CurrentTime;
        _voiHadMinion = HasMinion;

        ReassembleLeft = StatusLeft(SID.Reassembled);
        WildfireLeft = StatusLeft(SID.WildfirePlayer);
        HyperchargedLeft = StatusLeft(SID.Hypercharged);
        ExcavatorLeft = StatusLeft(SID.ExcavatorReady);
        FMFLeft = StatusLeft(SID.FullMetalMachinist);

        Flamethrower = StatusLeft(SID.Flamethrower) > 0;

        (BestAOETarget, NumAOETargets) = SelectTarget(strategy, primaryTarget, 12, IsConeAOETarget);
        (BestRangedAOETarget, NumRangedAOETargets) = SelectTarget(strategy, primaryTarget, 25, IsSplashTarget);
        (BestChainsawTarget, _) = SelectTarget(strategy, primaryTarget, 25, Is25yRectTarget);

        var finisherTarget = primaryTarget;
        primaryTarget = GetSingleTargetFallback(primaryTarget);

        var executionResumed = ObserveRotationExecution();
        UpdateCombatStartMode(strategy, executionResumed);
        UpdateRecoveryState(strategy);

        // expiring buff: push at a priority above every other GCD, but keep going so OGCDs are still handled this frame
        if (GetExpiringFullMetalFieldTarget(strategy, primaryTarget) is { } expiringFullMetalFieldTarget)
            PushGCD(AID.FullMetalField, expiringFullMetalFieldTarget, ToolPriorityAirAnchor + 50);

        if (IsPausedForFlamethrower)
            return;

        if (CountdownRemaining > 0)
        {
            if (ShouldUseCountdownPotion(strategy))
                PushPotion();

            if (CountdownRemaining < 5 && ReassembleLeft == 0)
                PushOGCD(AID.Reassemble, Player);

            var openerAction = GenericOpenerFirstAction(strategy);
            if (CountdownRemaining < 1.15f && openerAction != AID.None && primaryTarget != null)
                PushGCD(openerAction, primaryTarget);

            return;
        }

        if (TryPrecombatGenericOpenerGCD(strategy, primaryTarget))
        {
            OGCD(strategy, primaryTarget);
            return;
        }

        UpdateLastWeaponskill();
        UpdateGenericOpenerState(strategy);
        RecoverGenericOpenerFromLastWeaponskill(strategy);
        UpdateGenericOpenerFallback(strategy, primaryTarget);

        // minion chooses target based on the first target hit with any non-autoattack action after summon
        // ideally the summon is early-weaved, so we can immediately leg graze and then switch back to chosen target
        // TODO: this obviously won't work properly if the next action is a GCD that is cdplanned to hit something else; a consistent solution would be to force a clip with Leg Graze, but that sounds really frustrating for users
        if (Manager.LastCast.Data?.Action.ID is (uint)AID.AutomatonQueen or (uint)AID.RookAutoturret)
        {
            if (ResolveTarget(strategy.Queen.TrackRaw) is { } target)
            {
                primaryTarget = Hints.FindEnemy(target);
                PushOGCD(AID.LegGraze, target);
            }
        }

        if (PushDyingTargetFinishers(finisherTarget))
            return;

        primaryTarget = GetSingleTargetFallback(primaryTarget);

        if (ShouldUsePalaceSoloSafetyGCD(primaryTarget))
        {
            FillerGCDs(primaryTarget);
            return;
        }

        if (primaryTarget != null)
        {
            var aoebreakpoint = Overheated && Unlocked(AID.AutoCrossbow)
                ? ShouldUseAutoCrossbow() ? AutoCrossbowBreakpoint : 50
                : 3;
            GoalZoneCombined(strategy, 25, Hints.GoalAOECone(primaryTarget.Actor, 12, 45.Degrees()), AID.SpreadShot, aoebreakpoint);
        }

        if (Overheated && Unlocked(AID.HeatBlast))
        {
            _holdingOverheatedGCDForWildfire = ShouldHoldOverheatedGCDForWildfire(strategy, primaryTarget);
            if (!_holdingOverheatedGCDForWildfire)
                OverheatedGCDs(primaryTarget);
        }
        else
        {
            if (!TryGenericOpenerGCD(strategy, primaryTarget))
            {
                MechanicWindDown(strategy, primaryTarget);
                ToolGCDs(strategy, primaryTarget);
                FillerGCDs(primaryTarget);
            }
        }

        OGCD(strategy, primaryTarget);
    }

    private bool IsDyingActor(Actor actor)
        => actor.IsDead || actor.PendingDead || PredictedHP(actor) == 0;

    // the actor carrying our Wildfire debuff (null if none / not visible)
    private Actor? FindWildfireTargetActor()
    {
        foreach (var enemy in Hints.PotentialTargets)
            if (enemy.Actor.FindStatus((uint)SID.WildfireTarget, Player.InstanceID) != null)
                return enemy.Actor;
        return null;
    }

    // the Queen / Rook moves on to the next enemy by itself when its target dies (replays: no attack ever landed on the dead target, and
    // the rest of the schedule went to another enemy while the player was still fighting one), so an Overdrive for a dying target only
    // throws away the attacks it has left while anything else is alive
    private bool HasOtherLivingEnemy(Actor dying)
    {
        foreach (var enemy in Hints.PotentialTargets)
            if (enemy.Priority >= 0 && enemy.Actor != dying && enemy.Actor.IsTargetable && !IsDyingActor(enemy.Actor))
                return true;
        return false;
    }

    // the actor our Queen / Rook is currently attacking; falls back to the target it was deployed on
    private Actor? FindMinionTargetActor(Enemy? primaryTarget)
    {
        var minion = World.Actors.FirstOrDefault(a => a.Type == ActorType.Pet && a.OwnerID == Player.InstanceID);
        return minion != null ? World.Actors.Find(minion.TargetID) ?? primaryTarget?.Actor : primaryTarget?.Actor;
    }

    // Spec rule 2b: before a long target loss, the last GCD slots go to the strongest tools whose recast is back by the return,
    // and the Gauss/Ricochet charges that would sit capped through the loss are spent in the last weave slots.
    private void MechanicWindDown(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!WindDown.Active(Mechanic, GCDLength) || primaryTarget == null)
            return;

        float P(AID aid) => WindDownPotency.Of(WindDownPotency.MCH, (uint)aid);
        var gcds = new WindDownCandidate[6];
        var n = 0;
        void Add(AID aid, float readyIn, float recovered)
        {
            if (Unlocked(aid))
                gcds[n++] = new(ActionID.MakeSpell(aid), [P(aid)], readyIn, recovered);
        }
        // tools only on their automatic setting: Delay and target overrides belong to the player
        if (strategy.Tools.Value == ToolStrategy.Automatic)
        {
            Add(AID.Drill, ReadyIn(AID.Drill), RecastRecoveredIn(AID.Drill));
            Add(AID.AirAnchor, ReadyIn(AID.AirAnchor), RecastRecoveredIn(AID.AirAnchor));
            Add(AID.ChainSaw, ReadyIn(AID.ChainSaw), RecastRecoveredIn(AID.ChainSaw));
            if (ExcavatorLeft > 0)
                Add(AID.Excavator, 0, 0);
            if (FMFLeft > 0)
                Add(AID.FullMetalField, 0, 0);
        }
        // five Blazing Shots at 1.5 s fill three GCD slots. A free Hypercharged may be cut by the loss; Heat is kept for after the
        // return unless all five shots fit (spec rule 2: no gauge dumps)
        var hyperchargeSlots = WindDown.SlotsBeforeLoss(Mechanic, GCD, GCDLength, 0.6f);
        if (Unlocked(AID.Hypercharge) && strategy.Hypercharge.Value != OffensiveStrategy.Delay && ReassembleLeft <= GCD
            && (HyperchargedLeft > 0 || Heat >= 50 && hyperchargeSlots >= 3)
            // the normal rotation holds Hypercharge for a Wildfire coming before the loss; the wind-down must not spend it ahead of that
            && !(Unlocked(AID.Wildfire) && strategy.Wildfire.Value != WildfireStrategy.Delay && WildfireLeft <= 0 && ReadyIn(AID.Wildfire) < Mechanic.TargetLossIn))
        {
            var perSlot = P(AID.BlazingShot) * GCDLength / 1.5f;
            gcds[n++] = new(ActionID.MakeSpell(AID.Hypercharge), [perSlot, perSlot, perSlot], ReadyIn(AID.Hypercharge), 0);
        }
        var pick = WindDown.SelectGcd(gcds.AsSpan(0, n), Mechanic, GCD, GCDLength, 0.6f, WindDownPotency.FillerMCH);
        if (pick >= 0)
        {
            var aid = (AID)gcds[pick].Action.ID;
            if (aid == AID.Hypercharge)
                PushOGCD(AID.Hypercharge, Player, 5);
            else
                PushGCD(aid, primaryTarget, 60);
        }

        var charges = new WindDownCandidate[2];
        var m = 0;
        if (Unlocked(AID.GaussRound))
            charges[m++] = new(ActionID.MakeSpell(AID.GaussRound), [P(AID.DoubleCheck)], ReadyIn(AID.GaussRound), RecastRecoveredIn(AID.GaussRound));
        if (Unlocked(AID.Ricochet))
            charges[m++] = new(ActionID.MakeSpell(AID.Ricochet), [P(AID.Checkmate)], ReadyIn(AID.Ricochet), RecastRecoveredIn(AID.Ricochet));
        var op = WindDown.SelectOgcd(charges.AsSpan(0, m), Mechanic, GCDLength);
        if (op >= 0)
        {
            var aid = (AID)charges[op].Action.ID;
            var target = aid == AID.GaussRound && !Unlocked(AID.DoubleCheck) ? primaryTarget : BestRangedAOETarget ?? primaryTarget;
            PushOGCD(aid, target, 3);
        }
    }

    // Queen attack times after the summon (measured on replays) and Overdrive's own two hits, as potency at 50 Battery
    private static float OverdriveValue(float end) => (end > 0.7f ? 340f : 0f) + (end > 2.7f ? 390f : 0f);
    private static float PendingQueenValue(float elapsed, float end)
    {
        (float At, float Potency)[] schedule = [(5.6f, 240f), (8.7f, 120f), (10.3f, 120f), (11.9f, 120f), (13.4f, 340f), (15.5f, 390f)];
        var sum = 0f;
        foreach (var (at, potency) in schedule)
            if (at > elapsed && at - elapsed < end - 0.2f)
                sum += potency;
        return sum;
    }

    private DateTime _voiQueenAt;
    private bool _voiHadMinion;

    private bool PushDyingTargetFinishers(Enemy? primaryTarget)
    {
        // spec rule 2: a Wildfire or Queen still running when the target leaves is detonated / overdriven just before the loss
        if (Mechanic.Enabled && !Mechanic.DowntimeNow && Mechanic.TargetLossIn < GCD + 0.6f)
        {
            if (WildfireLeft > Mechanic.TargetLossIn && Unlocked(AID.Detonator) && ReadyIn(AID.Detonator) <= GCD)
                PushOGCD(AID.Detonator, Player, priority: 2);
            if (HasMinion && !(_voiFightEnd && VoiFightEndOverdrive))
            {
                var overdrive = BestActionUnlocked(AID.QueenOverdrive, AID.RookOverdrive);
                if (ReadyIn(overdrive) <= GCD)
                    PushOGCD(overdrive, Player);
            }
        }

        // FightRemaining (value-of-information experiment): Overdrive turns what is left of the Queen into Pile Bunker (0.5 s) and Crowned Collider
        // (2.5 s) at once; it is pressed when that beats the attacks the normal schedule still lands before the end, and not later than the
        // last weave that keeps the Collider inside the fight
        if (_voiFightEnd && VoiFightEndOverdrive && HasMinion && Unlocked(AID.QueenOverdrive))
        {
            var overdrive = BestActionUnlocked(AID.QueenOverdrive, AID.RookOverdrive);
            var end = Mechanic.TargetLossIn;
            var elapsed = (float)(World.CurrentTime - _voiQueenAt).TotalSeconds;
            if (ReadyIn(overdrive) <= GCD && end > 0.7f && OverdriveValue(end) > PendingQueenValue(elapsed, end) + 1f
                && (end < 2.7f + GCDLength + 0.1f || OverdriveValue(end - GCDLength - 0.1f) < OverdriveValue(end)))
                PushOGCD(overdrive, Player);
        }

        var queued = false;
        if (WildfireLeft > 0 && Unlocked(AID.Detonator) && ReadyIn(AID.Detonator) <= GCD
            && FindWildfireTargetActor() is { } wildfireTarget
            && IsDyingActor(wildfireTarget))
        {
            PushOGCD(AID.Detonator, Player, priority: 2);
            queued = true;
        }

        if (HasMinion)
        {
            var overdrive = BestActionUnlocked(AID.QueenOverdrive, AID.RookOverdrive);
            if (ReadyIn(overdrive) <= GCD
                && FindMinionTargetActor(primaryTarget) is { } minionTarget
                && IsDyingActor(minionTarget)
                && !HasOtherLivingEnemy(minionTarget))
            {
                PushOGCD(overdrive, Player);
                queued = true;
            }
        }

        return queued;
    }

    // while the overheated GCD is deliberately held for Wildfire, the GCD may already be idle, so CanWeave is not a usable gate
    private bool CanWeaveWildfire()
        => CanWeave(AID.Wildfire) || _holdingOverheatedGCDForWildfire && ReadyIn(AID.Wildfire) <= Math.Max(GCD, 0f);

    private bool ShouldHoldOverheatedGCDForWildfire(in Strategy strategy, Enemy? primaryTarget)
    {
        // never hold when Wildfire can't be pressed before the GCD comes up: that would deadlock until Overheated expires
        if (ReadyIn(AID.Wildfire) > Math.Max(GCD, 0f))
            return false;

        // a lockout that refuses abilities (Amnesia, or a stun-type status; Basexan.Locks) makes the hold pointless: the Blazing Shots still go out, Wildfire follows when it lifts
        if (Locks.AbilitiesLocked)
            return false;

        var openerHold = _genericOpenerMode == OpenerStrategy.NormalBurst
            && _genericOpenerStage == GenericOpenerStage.NormalHeat
            && GenericOpenerWildfirePending(strategy);
        if (!openerHold)
        {
            var overrideTarget = ResolveEnemy(strategy.Wildfire);
            var wildfireTarget = IsUsableToolTarget(overrideTarget) ? overrideTarget : primaryTarget;
            var standardHold = StandardBurstHyperchargeBeforeWildfire(strategy)
                && WildfireLeft <= 0
                && !WildfireRecentlyUsed()
                && (strategy.Wildfire.Value != WildfireStrategy.Automatic || !IsDyingTrashTarget(wildfireTarget));
            if (!standardHold)
                return false;
        }

        // the hold is only worth anything while the Wildfire push itself passes the rotation's own gates this frame: an opener whose
        // Excavator / Full Metal Field was interrupted (stun, line of sight, Amnesia, target loss) used to hold the overheated GCD for a
        // Wildfire that could never be queued and lost all five stacks (a 10 s stall after every interrupted opener)
        return WildfireWouldBePushedWhileHolding(strategy, primaryTarget);
    }

    // evaluates the predicate the OGCD path uses to queue Wildfire, with the hold flag set the way it is once the hold is on
    private bool WildfireWouldBePushedWhileHolding(in Strategy strategy, Enemy? primaryTarget)
    {
        var saved = _holdingOverheatedGCDForWildfire;
        _holdingOverheatedGCDForWildfire = true;
        try
        {
            if (GenericOpenerInProgress(strategy))
                return primaryTarget != null
                    && IsUsableToolTarget(primaryTarget)
                    && NormalOpenerWildfireReady(strategy)
                    && GetGenericOpenerWildfireTarget(strategy, primaryTarget) != null;
            return GetWildfireTarget(strategy, primaryTarget) != null;
        }
        finally
        {
            _holdingOverheatedGCDForWildfire = saved;
        }
    }

    private void OverheatedGCDs(Enemy? primaryTarget)
    {
        if (ShouldUseAutoCrossbow() && IsUsableToolTarget(BestAOETarget))
        {
            PushGCD(AID.AutoCrossbow, BestAOETarget);
            return;
        }

        var singleTarget = IsUsableToolTarget(primaryTarget) ? primaryTarget
            : IsUsableToolTarget(BestRangedAOETarget) ? BestRangedAOETarget
            : IsUsableToolTarget(BestAOETarget) ? BestAOETarget
            : IsUsableToolTarget(BestChainsawTarget) ? BestChainsawTarget
            : null;
        if (singleTarget != null)
            PushGCD(BestActionUnlocked(AID.BlazingShot, AID.HeatBlast), singleTarget);
    }

    private bool TryGenericOpenerGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!GenericOpenerInProgress(strategy) || !IsUsableToolTarget(primaryTarget))
            return false;

        if (strategy.Tools.Value != ToolStrategy.Automatic)
        {
            SetGenericOpenerStage(GenericOpenerStage.Complete);
            return false;
        }

        var firstStage = _genericOpenerMode == OpenerStrategy.NormalBurst
            ? GenericOpenerStage.NormalAirAnchor
            : GenericOpenerStage.ZeroChainSaw;
        if (_genericOpenerStage == firstStage
            && _genericOpenerLastGCD == AID.None
            && ReassembleLeft == 0
            && ChargeInfo(AID.Reassemble).Charges > 0)
            return true;

        var toolTarget = ResolveTargetOverride(strategy.Tools);
        if (!IsUsableToolTarget(toolTarget))
            toolTarget = null;
        var singleTarget = toolTarget ?? primaryTarget;
        // AOE-shaped tools must respect SelectTarget's forbidden-target / ForceST result: no primary-target fallback
        var splashTarget = toolTarget ?? BestRangedAOETarget;
        var chainsawTarget = toolTarget ?? BestChainsawTarget;

        for (var fallback = 0; fallback < 8; ++fallback)
        {
            switch (_genericOpenerStage)
            {
                case GenericOpenerStage.NormalAirAnchor:
                {
                    var action = BestActionUnlocked(AID.AirAnchor, AID.HotShot);
                    if (action == AID.None)
                    {
                        SetGenericOpenerStage(GenericOpenerStage.NormalDrill);
                        continue;
                    }
                    if (!GCDReady(action))
                    {
                        SetGenericOpenerStage(GenericOpenerStage.NormalDrill);
                        continue;
                    }
                    PushGCD(action, singleTarget, ToolPriorityHigh + 10);
                    return true;
                }
                case GenericOpenerStage.NormalDrill:
                    if (!Unlocked(AID.Drill) || !GCDReady(AID.Drill) || MaxChargesIn(AID.Drill) > GCD)
                    {
                        SetGenericOpenerStage(GenericOpenerStage.NormalChainSaw);
                        continue;
                    }
                    PushGCD(AID.Drill, singleTarget, ToolPriorityHigh + 10);
                    return true;
                case GenericOpenerStage.NormalChainSaw:
                {
                    var stabilizerPending = strategy.Buffs.Value != OffensiveStrategy.Delay
                        && Unlocked(AID.BarrelStabilizer)
                        && !GenericOpenerStabilizerCommitted()
                        && ReadyIn(AID.BarrelStabilizer) <= GCD
                        && !BarrelStabilizerWouldOvercapHeat();
                    // hold the GCD only while Barrel Stabilizer can actually be woven in front of it: after a target loss the GCD may
                    // already be up (no weave slot) or a Reassemble may be waiting for this GCD, and holding then stalls both
                    if (stabilizerPending && ShouldUseGenericOpenerStabilizer(strategy, primaryTarget))
                        return true;

                    if (!Unlocked(AID.ChainSaw) || !GCDReady(AID.ChainSaw) || chainsawTarget == null)
                    {
                        SetGenericOpenerStage(GenericOpenerStage.NormalExcavator);
                        continue;
                    }
                    PushGCD(AID.ChainSaw, chainsawTarget, ToolPriorityHigh + 10);
                    return true;
                }
                case GenericOpenerStage.NormalExcavator:
                    if (!Unlocked(AID.Excavator))
                    {
                        SetGenericOpenerStage(GenericOpenerStage.NormalFullMetalField);
                        continue;
                    }

                    if (LastWeaponskillWas(AID.Excavator))
                    {
                        _genericOpenerUsedExcavator = true;
                        SetGenericOpenerStage(GenericOpenerStage.NormalFullMetalField);
                        continue;
                    }

                    if (OpenerExcavatorAvailable() && splashTarget != null)
                    {
                        PushGCD(AID.Excavator, splashTarget, ToolPriorityHigh + 10);
                        return true;
                    }

                    var normalChainSawWasUsed = _genericOpenerLastGCD == AID.ChainSaw
                        || LastWeaponskillWas(AID.ChainSaw)
                        || ReadyIn(AID.ChainSaw) > 50f;
                    if (normalChainSawWasUsed && (World.CurrentTime - _genericOpenerLastProgressTime).TotalSeconds <= 3)
                        return true;

                    SetGenericOpenerStage(GenericOpenerStage.NormalFullMetalField);
                    continue;
                case GenericOpenerStage.NormalFullMetalField:
                    // the Queen goes out before Full Metal Field; while abilities are refused (Amnesia) waiting for it only burns fillers
                    if (GenericOpenerQueenReady(strategy) && !_genericOpenerUsedQueen && !Locks.AbilitiesLocked)
                    {
                        var queen = BestActionUnlocked(AID.AutomatonQueen, AID.RookAutoturret);
                        if (!CanWeave(queen))
                            FillerGCDs(primaryTarget);
                        return true;
                    }

                    if (!Unlocked(AID.FullMetalField) || FMFLeft <= GCD || splashTarget == null)
                    {
                        SetGenericOpenerStage(GenericOpenerStage.NormalHypercharge);
                        continue;
                    }
                    if (ReassembleLeft > GCD)
                    {
                        FillerGCDs(primaryTarget);
                        return true;
                    }
                    PushGCD(AID.FullMetalField, splashTarget, ToolPriorityHigh + 10);
                    return true;
                case GenericOpenerStage.NormalHypercharge:
                    if (!GenericOpenerHyperchargePossible(strategy))
                    {
                        SetGenericOpenerStage(GenericOpenerStage.Complete);
                        return false;
                    }
                    if (!CanWeave(AID.Hypercharge))
                        FillerGCDs(primaryTarget);
                    return true;
                case GenericOpenerStage.NormalHeat:
                    if (Overheated || (World.CurrentTime - _genericOpenerHyperchargeCast).TotalSeconds <= 1)
                        return true;
                    SetGenericOpenerStage(GenericOpenerStage.Complete);
                    return false;
                case GenericOpenerStage.ZeroChainSaw:
                    if (!Unlocked(AID.ChainSaw) || !GCDReady(AID.ChainSaw) || chainsawTarget == null)
                    {
                        SetGenericOpenerStage(GenericOpenerStage.ZeroDrill);
                        continue;
                    }
                    PushGCD(AID.ChainSaw, chainsawTarget, ToolPriorityHigh + 10);
                    return true;
                case GenericOpenerStage.ZeroDrill:
                    if (!Unlocked(AID.Drill) || !GCDReady(AID.Drill) || MaxChargesIn(AID.Drill) > GCD)
                    {
                        SetGenericOpenerStage(GenericOpenerStage.ZeroAirAnchor);
                        continue;
                    }
                    PushGCD(AID.Drill, singleTarget, ToolPriorityHigh + 10);
                    return true;
                case GenericOpenerStage.ZeroAirAnchor:
                {
                    var stabilizerPending = strategy.Buffs.Value != OffensiveStrategy.Delay
                        && Unlocked(AID.BarrelStabilizer)
                        && !GenericOpenerStabilizerCommitted()
                        && ReadyIn(AID.BarrelStabilizer) <= GCD
                        && !BarrelStabilizerWouldOvercapHeat();
                    // hold the GCD only while Barrel Stabilizer can actually be woven in front of it: after a target loss the GCD may
                    // already be up (no weave slot) or a Reassemble may be waiting for this GCD, and holding then stalls both
                    if (stabilizerPending && ShouldUseGenericOpenerStabilizer(strategy, primaryTarget))
                        return true;

                    if (GenericOpenerWildfireRequired(strategy) && !WildfireWasCommitted())
                    {
                        if (!ZeroSecondWildfireReady(strategy) || !CanWeave(AID.Wildfire))
                            FillerGCDs(primaryTarget);
                        return true;
                    }

                    var action = BestActionUnlocked(AID.AirAnchor, AID.HotShot);
                    if (action == AID.None || !GCDReady(action))
                    {
                        SetGenericOpenerStage(GenericOpenerStage.ZeroHypercharge);
                        continue;
                    }
                    PushGCD(action, singleTarget, ToolPriorityHigh + 10);
                    return true;
                }
                case GenericOpenerStage.ZeroHypercharge:
                    if (GenericOpenerWildfirePending(strategy))
                    {
                        if (!ZeroSecondWildfireReady(strategy) || !CanWeave(AID.Wildfire))
                            FillerGCDs(primaryTarget);
                        return true;
                    }

                    if (strategy.Hypercharge.Value == OffensiveStrategy.Delay
                        || !Unlocked(AID.Hypercharge)
                        || ReadyIn(AID.Hypercharge) > GCD
                        || HyperchargedLeft == 0 && Heat < 50)
                    {
                        SetGenericOpenerStage(GenericOpenerStage.ZeroExcavator);
                        continue;
                    }
                    if (!CanWeave(AID.Hypercharge))
                        FillerGCDs(primaryTarget);
                    return true;
                case GenericOpenerStage.ZeroHeat:
                    if (Overheated || (World.CurrentTime - _genericOpenerHyperchargeCast).TotalSeconds <= 1)
                        return true;
                    SetGenericOpenerStage(GenericOpenerStage.ZeroExcavator);
                    continue;
                case GenericOpenerStage.ZeroExcavator:
                    if (!Unlocked(AID.Excavator))
                    {
                        SetGenericOpenerStage(GenericOpenerStage.ZeroSecondDrill);
                        continue;
                    }

                    if (LastWeaponskillWas(AID.Excavator))
                    {
                        _genericOpenerUsedExcavator = true;
                        SetGenericOpenerStage(GenericOpenerStage.ZeroSecondDrill);
                        continue;
                    }

                    if (OpenerExcavatorAvailable() && splashTarget != null)
                    {
                        PushGCD(AID.Excavator, splashTarget, ToolPriorityHigh + 10);
                        return true;
                    }

                    var zeroChainSawWasUsed = ReadyIn(AID.ChainSaw) > 40f;
                    if (zeroChainSawWasUsed && (World.CurrentTime - _genericOpenerLastProgressTime).TotalSeconds <= 3)
                        return true;

                    SetGenericOpenerStage(GenericOpenerStage.ZeroSecondDrill);
                    continue;
                case GenericOpenerStage.ZeroSecondDrill:
                    if (GenericOpenerQueenReady(strategy) && !_genericOpenerUsedQueen && !Locks.AbilitiesLocked)
                    {
                        var queen = BestActionUnlocked(AID.AutomatonQueen, AID.RookAutoturret);
                        if (!CanWeave(queen))
                            FillerGCDs(primaryTarget);
                        return true;
                    }

                    if (Unlocked(AID.Reassemble)
                        && ReassembleLeft == 0
                        && ChargeInfo(AID.Reassemble).Charges > 0)
                    {
                        if (!CanWeave(AID.Reassemble))
                            FillerGCDs(primaryTarget);
                        return true;
                    }

                    if (!Unlocked(AID.Drill) || !GCDReady(AID.Drill))
                    {
                        SetGenericOpenerStage(GenericOpenerStage.ZeroFullMetalField);
                        continue;
                    }
                    PushGCD(AID.Drill, singleTarget, ToolPriorityHigh + 10);
                    return true;
                case GenericOpenerStage.ZeroFullMetalField:
                    if (!Unlocked(AID.FullMetalField) || FMFLeft <= GCD || splashTarget == null)
                    {
                        SetGenericOpenerStage(GenericOpenerStage.Complete);
                        return false;
                    }
                    if (ReassembleLeft > GCD)
                    {
                        FillerGCDs(primaryTarget);
                        return true;
                    }
                    PushGCD(AID.FullMetalField, splashTarget, ToolPriorityHigh + 10);
                    return true;
                default:
                    return false;
            }
        }

        SetGenericOpenerStage(GenericOpenerStage.Complete);
        return false;
    }

    private bool ShouldHoldToolForBurstAlignment(in Strategy strategy, AID action)
    {
        if (IsZeroSecondBurstRecastRotation(strategy)
            && action is AID.AirAnchor or AID.HotShot or AID.ChainSaw)
            return false;

        var recoveryToolsReady = IsRecoveryMode()
            && action is AID.AirAnchor or AID.HotShot or AID.ChainSaw or AID.Drill or AID.Bioblaster
            && GCDReady(AID.ChainSaw)
            && GCDReady(AID.Drill)
            && (GCDReady(AID.AirAnchor) || !Unlocked(AID.AirAnchor) && GCDReady(AID.HotShot));
        var wildfireRequired = Unlocked(AID.Wildfire) && strategy.Wildfire.Value != WildfireStrategy.Delay;
        var burstReleased = IsRecoveryMode()
            && (wildfireRequired ? RecoveryWildfireCommitted() : _recoveryBurstCommitted);
        var standardBurstPreparing = !IsRecoveryMode() && StandardBurstPreparationWindow(strategy);
        if (recoveryToolsReady || burstReleased || !BurstAlignmentSoon(strategy) || BurstAlignmentWindow(strategy) || standardBurstPreparing || DowntimeIn <= GCD * 2)
            return false;

        var burstIn = BurstAlignmentIn(strategy);
        if (action is AID.AirAnchor or AID.HotShot or AID.ChainSaw)
            // only hold a ready tool when the burst is at most ~2 GCDs away; a longer hold drifts the tool for the rest of the fight
            return ReadyIn(action) <= GCD && burstIn <= GCD + GCDLength * 2;

        if (ActionCapSoon(action))
            return false;

        return action switch
        {
            AID.Drill or AID.Bioblaster => MaxChargesIn(action) > GCD + 8f,
            AID.Excavator => ExcavatorLeft > burstIn + GCD,
            AID.FullMetalField => FMFLeft > burstIn + GCD,
            _ => false
        };
    }

    private void ToolGCDs(in Strategy strategy, Enemy? primaryTarget)
    {
        if (Overheated)
            return;

        // spec rule 2: Excavator / Full Metal Field effects that would expire during a long target loss are spent before it
        if (strategy.Tools.Value == ToolStrategy.Automatic && primaryTarget != null && ExcavatorLeft > 0 && Mechanic.ExpiresDuringLoss(ExcavatorLeft))
            PushGCD(AID.Excavator, primaryTarget, 45);
        if (strategy.Tools.Value == ToolStrategy.Automatic && primaryTarget != null && FMFLeft > 0 && Mechanic.ExpiresDuringLoss(FMFLeft))
            PushGCD(AID.FullMetalField, primaryTarget, 45);

        var toolTarget = ResolveTargetOverride(strategy.Tools);
        if (!IsUsableToolTarget(toolTarget))
            toolTarget = null;

        if (strategy.Tools.Value != ToolStrategy.Automatic)
        {
            var delayFullMetalFieldTarget = toolTarget ?? BestRangedAOETarget;
            if (ShouldSpendFullMetalFieldDuringToolDelay(strategy, primaryTarget) && IsUsableToolTarget(delayFullMetalFieldTarget))
                PushGCD(AID.FullMetalField, delayFullMetalFieldTarget, ToolPriorityFullMetalField);
            return;
        }

        // AOE-shaped tools: no primary-target fallback, SelectTarget already decided whether the shape is allowed
        var excavatorTarget = toolTarget ?? BestRangedAOETarget;
        var standardBurstOrder = StandardBurstToolOrderActive(strategy);
        var zeroSecondRecast = IsZeroSecondBurstRecastRotation(strategy);
        if (ExcavatorLeft > GCD
            && !ShouldDelayExcavatorForOddQueen()
            && !ShouldHoldToolForBurstAlignment(strategy, AID.Excavator)
            && !ShouldHoldForDancingMadAction(AID.Excavator)
            && IsUsableToolTarget(excavatorTarget))
            PushGCD(AID.Excavator, excavatorTarget, standardBurstOrder ? ToolPriorityAirAnchor + 7 : ExcavatorLeft <= GCD * 2 + 0.5f ? ToolPriorityAirAnchor + 2 : ToolPriorityChainSaw - 1);

        PushDancingMadForcedGCD(primaryTarget);

        var singleTarget = toolTarget ?? primaryTarget;
        if (GCDReady(AID.AirAnchor)
            && !ShouldHoldToolForBurstAlignment(strategy, AID.AirAnchor)
            && !ShouldHoldForDancingMadAction(AID.AirAnchor)
            && IsUsableToolTarget(singleTarget))
        {
            if (standardBurstOrder || zeroSecondRecast)
                PushGCD(AID.AirAnchor, singleTarget, priority: ToolPriorityAirAnchor + 10);
            else if (AirAnchorLowPriority())
                // filler-level: must stay below ActionQueue.Priority.ManualOGCD (== High + 1), so a manually pressed OGCD always wins
                PushAction(AID.AirAnchor, singleTarget!.Actor, ActionQueue.Priority.High + 0.5f, 0);
            else
                PushGCD(AID.AirAnchor, singleTarget, priority: ToolPriorityAirAnchor);
        }

        var chainsawTarget = toolTarget ?? BestChainsawTarget;
        if (GCDReady(AID.ChainSaw)
            && (!standardBurstOrder || StandardBurstDrillComplete() && StandardBurstStabilizerCommittedOrUnavailable(strategy))
            && !ShouldHoldToolForBurstAlignment(strategy, AID.ChainSaw)
            && !ShouldHoldForDancingMadAction(AID.ChainSaw)
            && IsUsableToolTarget(chainsawTarget))
            PushGCD(AID.ChainSaw, chainsawTarget, standardBurstOrder ? ToolPriorityAirAnchor + 8 : zeroSecondRecast ? ToolPriorityAirAnchor + 9 : ToolPriorityChainSaw);

        var bioblasterTarget = toolTarget ?? BestAOETarget;
        var useBioblaster = Unlocked(AID.Bioblaster)
            && GCDReady(AID.Bioblaster)
            && NumAOETargets > 2
            && (!standardBurstOrder || StandardBurstAirAnchorComplete())
            && !ShouldHoldToolForBurstAlignment(strategy, AID.Bioblaster)
            && !ShouldHoldForDancingMadAction(AID.Bioblaster)
            && IsUsableToolTarget(bioblasterTarget)
            && StatusDetails(bioblasterTarget, SID.Bioblaster, Player.InstanceID, 15).Left <= GCD;
        if (useBioblaster)
            PushGCD(AID.Bioblaster, bioblasterTarget, priority: standardBurstOrder ? ToolPriorityAirAnchor + 9 : MaxChargesIn(AID.Bioblaster) <= GCD ? ToolPriorityAirAnchor + 1 : ToolPriorityDrill);

        if (!useBioblaster
            && GCDReady(AID.Drill)
            && (!standardBurstOrder || StandardBurstAirAnchorComplete())
            && !ShouldHoldToolForBurstAlignment(strategy, AID.Drill)
            && !ShouldHoldForDancingMadAction(AID.Drill)
            && IsUsableToolTarget(singleTarget))
            PushGCD(AID.Drill, singleTarget, priority: standardBurstOrder ? ToolPriorityAirAnchor + 9 : MaxChargesIn(AID.Drill) <= GCD ? ToolPriorityAirAnchor + 1 : ToolPriorityDrill);

        // different cdgroup fsr
        if (!Unlocked(AID.AirAnchor)
            && GCDReady(AID.HotShot)
            && !ShouldHoldToolForBurstAlignment(strategy, AID.HotShot)
            && IsUsableToolTarget(singleTarget))
            PushGCD(AID.HotShot, singleTarget, zeroSecondRecast ? ToolPriorityAirAnchor + 10 : ToolPriorityAirAnchor);

        // TODO work out priorities
        var fullMetalFieldTarget = toolTarget ?? BestRangedAOETarget;
        var fullMetalFieldExpiring = FullMetalFieldExpiringSoon();
        if (FMFLeft > GCD
            && ReassembleLeft == 0
            && ExcavatorLeft == 0
            && FullMetalFieldBurstReady(strategy, primaryTarget)
            && (!IsRecoveryMode()
                || !Unlocked(AID.Wildfire)
                || strategy.Wildfire.Value == WildfireStrategy.Delay
                || RecoveryWildfireCommitted()
                || GetWildfireTargetIgnoringFullMetalField(strategy, primaryTarget) != null
                || FMFLeft <= RecoveryBurstAnchorIn(strategy) + GCD)
            && !ShouldHoldToolForBurstAlignment(strategy, AID.FullMetalField)
            && (fullMetalFieldExpiring || !ShouldHoldToolCapBeforeFullMetalField())
            && (!ShouldHoldForDancingMadAction(AID.FullMetalField) || ShouldForceFullMetalFieldSpend(strategy, primaryTarget))
            && IsUsableToolTarget(fullMetalFieldTarget))
            PushGCD(AID.FullMetalField, fullMetalFieldTarget, standardBurstOrder ? ToolPriorityAirAnchor + 6 : fullMetalFieldExpiring ? ToolPriorityAirAnchor + 3 : ToolPriorityFullMetalField);
    }

    private void FillerGCDs(Enemy? primaryTarget)
    {
        if (ShouldUseScattergun() && IsUsableToolTarget(BestAOETarget))
            PushGCD(BestActionUnlocked(AID.Scattergun, AID.SpreadShot), BestAOETarget);
        else if (IsUsableToolTarget(primaryTarget) && ComboLastMove is AID.SlugShot or AID.HeatedSlugShot)
            PushGCD(BestActionUnlocked(AID.HeatedCleanShot, AID.CleanShot), primaryTarget);
        else if (IsUsableToolTarget(primaryTarget) && ComboLastMove is AID.SplitShot or AID.HeatedSplitShot)
            PushGCD(BestActionUnlocked(AID.HeatedSlugShot, AID.SlugShot), primaryTarget);
        else if (IsUsableToolTarget(primaryTarget))
            PushGCD(BestActionUnlocked(AID.HeatedSplitShot, AID.SplitShot), primaryTarget);
    }

    private bool ShouldUseAutoCrossbow()
    {
        if (NumAOETargets >= AutoCrossbowHighTargetBreakpoint)
            return true;

        if (NumAOETargets < AutoCrossbowBreakpoint)
            return false;

        var (_, gaussCharges, _) = ChargeInfo(AID.GaussRound);
        var (_, ricoCharges, _) = ChargeInfo(AID.Ricochet);
        return gaussCharges > 0 || ricoCharges > 0;
    }

    private bool ShouldUseScattergun()
    {
        if (!Unlocked(AID.SpreadShot))
            return false;

        if (!Unlocked(AID.Scattergun))
            return NumAOETargets > 1;

        return NumAOETargets > 3 || NumAOETargets == 3 && NumRangedAOETargets >= 3;
    }

    private bool ShouldSpendFullMetalFieldDuringToolDelay(in Strategy strategy, Enemy? primaryTarget)
        => strategy.Tools.Value == ToolStrategy.Delay
        && FMFLeft > GCD
        && ExcavatorLeft == 0
        && (!IsRecoveryMode()
            || !Unlocked(AID.Wildfire)
            || strategy.Wildfire.Value == WildfireStrategy.Delay
            || RecoveryWildfireCommitted()
            || FMFLeft <= RecoveryBurstAnchorIn(strategy) + GCD)
        && ShouldForceFullMetalFieldSpend(strategy, primaryTarget);

    private bool FullMetalFieldBurstReady(in Strategy strategy, Enemy? primaryTarget)
    {
        if (FullMetalFieldExpiringSoon() || ShouldUseDancingMadProfile())
            return true;

        if (IsRecoveryMode())
        {
            if (FMFLeft <= RecoveryBurstAnchorIn(strategy) + GCD)
                return true;

            return !Unlocked(AID.Wildfire)
                || strategy.Wildfire.Value == WildfireStrategy.Delay
                || RecoveryWildfireCommitted()
                || GetWildfireTargetIgnoringFullMetalField(strategy, primaryTarget) != null;
        }

        var standardBurstIn = StandardBurstAnchorIn(strategy);
        if (standardBurstIn < float.MaxValue)
        {
            if (FMFLeft <= standardBurstIn + GCD)
                return true;
            if (standardBurstIn > GCD + 1f)
                return false;
        }

        if (Unlocked(AID.Wildfire) && strategy.Wildfire.Value != WildfireStrategy.Delay)
            return WildfireLeft > 0
                || WildfireRecentlyUsed()
                || GetWildfireTargetIgnoringFullMetalField(strategy, primaryTarget) != null;

        return RaidBuffsLeft > 0 || StandardBurstWindow(strategy);
    }

    private bool ShouldForceFullMetalFieldSpend(in Strategy strategy, Enemy? primaryTarget)
    {
        if (FullMetalFieldExpiringSoon())
            return true;

        var burstIn = BurstAlignmentIn(strategy);
        if (burstIn < float.MaxValue && FMFLeft <= burstIn + GCD)
            return true;

        if (strategy.Wildfire.Value == WildfireStrategy.Delay)
            return GetWildfireTargetIgnoringFullMetalField(strategy, primaryTarget) != null;

        return WildfireLeft > 0 || WildfireRecentlyUsed();
    }

    private bool WildfireRecentlyUsed()
        => Unlocked(AID.Wildfire) && ReadyIn(AID.Wildfire) > 110;

    private bool FullMetalFieldExpiringSoon()
        => FMFLeft > GCD && FMFLeft <= GCD + GCDLength + 0.5f;

    private Enemy? GetExpiringFullMetalFieldTarget(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Player.InCombat || !FullMetalFieldExpiringSoon())
            return null;

        var overrideTarget = ResolveTargetOverride(strategy.Tools);
        return IsUsableToolTarget(overrideTarget) ? overrideTarget
            : IsUsableToolTarget(BestRangedAOETarget) ? BestRangedAOETarget
            : IsUsableToolTarget(primaryTarget) ? primaryTarget
            : null;
    }

    private bool ShouldHoldToolCapBeforeFullMetalField()
        => !FullMetalFieldExpiringSoon() && (ActionCapSoon(AID.Drill) || ActionCapSoon(AID.Bioblaster) || ActionCapSoon(AID.AirAnchor) || ActionCapSoon(AID.ChainSaw));

    // out-of-combat enemies get PriorityUndesirable (-3): still a valid single-target when it is the player's own target (pre-pull opener, manual pulls)
    private bool IsUsableToolTarget(Enemy? target)
        => target != null
        && (target.Priority >= 0 || target.Priority == Enemy.PriorityUndesirable && target.Actor.InstanceID == Player.TargetID)
        && target.Actor.IsTargetable
        && Player.DistanceToHitbox(target.Actor) <= 25;

    private Enemy? GetSingleTargetFallback(Enemy? primaryTarget)
        => IsUsableToolTarget(primaryTarget) ? primaryTarget
        : IsUsableToolTarget(BestRangedAOETarget) ? BestRangedAOETarget
        : IsUsableToolTarget(BestAOETarget) ? BestAOETarget
        : IsUsableToolTarget(BestChainsawTarget) ? BestChainsawTarget
        : null;

    private bool IsDyingTrashTarget(Enemy? target)
    {
        if (target == null)
            return false;

        var module = Bossmods.ActiveModule;
        var boss = module?.StateMachine.ActivePhase != null ? module.PrimaryActor : null;
        if (boss == null || target.Actor.InstanceID == boss.InstanceID)
            return false;

        return target.Priority == Enemy.PriorityPointless
            || target.Actor.PendingDead
            || PredictedHP(target.Actor) == 0
            || target.Actor.HPMP.MaxHP > 0 && PredictedHPRatio(target.Actor) <= 0.05f;
    }

    private bool ShouldUsePalaceSoloSafetyGCD(Enemy? primaryTarget)
    {
        if (World.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.POTD || World.DeepDungeon.Party.Count(p => p.EntityId > 0) > 1 || primaryTarget == null || Bossmods.ActiveModule?.Info?.GroupType != BossModuleInfo.GroupType.CFC)
            return false;

        if (Bossmods.ActiveModule.Info.GroupID == 216)
        {
            var boss = Bossmods.ActiveModule.PrimaryActor;
            var hp = boss.HPMP.CurHP / (float)boss.HPMP.MaxHP;
            return primaryTarget.Actor.InstanceID == boss.InstanceID && hp is > 0.158f and <= 0.30f;
        }

        return Bossmods.ActiveModule.Info.GroupID == 217 && primaryTarget.Actor.OID == 0x18F2;
    }

    // in heavy AOE with battery to spare, Air Anchor is worth less than the AOE filler
    private bool AirAnchorLowPriority()
        => NumAOETargets >= AutoCrossbowBreakpoint
            && MaxChargesIn(AID.AirAnchor) > GCD
            && Battery <= 80
            && DowntimeIn > GCD * 2;

    private bool OpenerExcavatorAvailable()
        => Unlocked(AID.Excavator) && ExcavatorLeft > GCD;

    private bool GenericOpenerWildfireRequired(in Strategy strategy)
        => Unlocked(AID.Wildfire)
        && strategy.Wildfire.Value != WildfireStrategy.Delay
        && strategy.Hypercharge.Value != OffensiveStrategy.Delay;

    private bool GenericOpenerWildfirePending(in Strategy strategy)
        => GenericOpenerInProgress(strategy)
        && _genericOpenerStage is GenericOpenerStage.NormalHeat or GenericOpenerStage.ZeroAirAnchor or GenericOpenerStage.ZeroHypercharge
        && GenericOpenerWildfireRequired(strategy)
        && !WildfireWasCommitted();

    private bool GenericOpenerStabilizerCommitted()
        => _genericOpenerUsedStabilizer
        || HyperchargedLeft > 0
        || Unlocked(AID.BarrelStabilizer) && ReadyIn(AID.BarrelStabilizer) > 110f;

    private bool BarrelStabilizerWouldOvercapHeat()
        => !Unlocked(TraitID.EnhancedBarrelStabilizer) && Heat > 50;

    private bool WildfireWasCommitted()
        => _genericOpenerUsedWildfire
        || WildfireLeft > 0
        || WildfireRecentlyUsed();

    private bool NormalOpenerWildfireReady(in Strategy strategy)
    {
        if (!IsGenericOpener(strategy)
            || strategy.Opener.Value != OpenerStrategy.NormalBurst
            || !GenericOpenerWildfireRequired(strategy)
            || WildfireWasCommitted()
            || _genericOpenerStage != GenericOpenerStage.NormalHeat
            || ReadyIn(AID.Wildfire) > GCD
            || !CanWeaveWildfire()
            || ReassembleLeft > GCD)
            return false;

        // the Excavator -> Full Metal Field -> Hypercharge order is only enforced until the Hypercharge is out: once Overheated, waiting
        // for a tool that an interruption pushed past this point would only let the five Blazing Shots run out of the Wildfire window
        if (!Overheated)
        {
            var excavatorComplete = _genericOpenerUsedExcavator
                || LastWeaponskillWas(AID.Excavator)
                || !Unlocked(AID.Excavator);
            if (!excavatorComplete)
                return false;

            if (Unlocked(AID.FullMetalField) && FMFLeft > GCD)
                return false;
        }

        return Overheated
            || HyperchargedLeft > 0
            || (World.CurrentTime - _genericOpenerHyperchargeCast).TotalSeconds <= 1;
    }

    private bool LowLevelOpenerWildfireReady(in Strategy strategy)
        => !ShouldUseDancingMadProfile()
        && CombatTimer < 60f
        && !Unlocked(AID.FullMetalField)
        && strategy.Wildfire.Value != WildfireStrategy.Delay
        && strategy.Hypercharge.Value != OffensiveStrategy.Delay
        && ReassembleLeft <= GCD
        && (HyperchargedLeft > 0 || Heat >= 50);

    private bool GenericOpenerHyperchargePossible(in Strategy strategy)
    {
        if (!GenericOpenerInProgress(strategy)
            || strategy.Hypercharge.Value == OffensiveStrategy.Delay
            || !Unlocked(AID.Hypercharge)
            || ReadyIn(AID.Hypercharge) > GCD
            || Overheated
            || ReassembleLeft > GCD
            || HyperchargedLeft == 0 && Heat < 50)
            return false;

        var wildfireReady = _genericOpenerMode == OpenerStrategy.NormalBurst
            || !GenericOpenerWildfireRequired(strategy)
            || WildfireWasCommitted();
        return wildfireReady && _genericOpenerStage is GenericOpenerStage.NormalHypercharge or GenericOpenerStage.ZeroHypercharge;
    }

    private bool GenericOpenerQueenReady(in Strategy strategy)
    {
        var batteryRequirement = GenericOpenerQueenBatteryRequirement(strategy);
        var excavatorWasUsed = _genericOpenerUsedExcavator || LastWeaponskillWas(AID.Excavator);
        if (excavatorWasUsed)
            _genericOpenerUsedExcavator = true;

        if (!IsGenericOpener(strategy)
            || !Unlocked(AID.RookAutoturret)
            || HasMinion
            || Battery < batteryRequirement
            || !excavatorWasUsed && Unlocked(AID.Excavator))
            return false;

        var queen = BestActionUnlocked(AID.AutomatonQueen, AID.RookAutoturret);
        if (queen == AID.None || ReadyIn(queen) > GCD)
            return false;

        return strategy.Opener.Value switch
        {
            OpenerStrategy.NormalBurst => _genericOpenerStage is GenericOpenerStage.NormalFullMetalField or GenericOpenerStage.NormalHypercharge or GenericOpenerStage.NormalHeat or GenericOpenerStage.Complete,
            OpenerStrategy.ZeroSecondBurst => !Overheated && _genericOpenerStage is (GenericOpenerStage.ZeroSecondDrill or GenericOpenerStage.Complete),
            _ => false
        };
    }

    private int GenericOpenerQueenBatteryRequirement(in Strategy strategy)
        => strategy.Queen.Value switch
        {
            QueenStrategy.Automatic => 60,
            QueenStrategy.MinGauge => 50,
            QueenStrategy.FullGauge => 100,
            QueenStrategy.RaidBuffsOnly => RaidBuffsLeft > 0 || RaidBuffsIn is > 0 and <= QueenRaidBuffLeadTime ? 60 : 101,
            _ => 101
        };

    private bool ShouldUseGenericOpenerStabilizer(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!GenericOpenerInProgress(strategy)
            || strategy.Buffs.Value == OffensiveStrategy.Delay
            || !Unlocked(AID.BarrelStabilizer)
            || ReassembleLeft > 0
            || strategy.Buffs.Value == OffensiveStrategy.Automatic && IsDyingTrashTarget(primaryTarget)
            || BarrelStabilizerWouldOvercapHeat()
            || !CanWeave(AID.BarrelStabilizer)
            || GenericOpenerStabilizerCommitted()
            || WildfireWasCommitted())
            return false;

        return strategy.Opener.Value switch
        {
            OpenerStrategy.NormalBurst => _genericOpenerStage == GenericOpenerStage.NormalChainSaw,
            OpenerStrategy.ZeroSecondBurst => _genericOpenerStage == GenericOpenerStage.ZeroAirAnchor,
            _ => false
        };
    }

    private bool PushOneGenericOpenerCharge(Enemy primaryTarget)
    {
        var (_, gaussCharges, _) = ChargeInfo(AID.GaussRound);
        var (_, ricoCharges, _) = ChargeInfo(AID.Ricochet);
        if (gaussCharges <= 0 && ricoCharges <= 0)
            return false;

        if (gaussCharges >= ricoCharges)
            UseGauss(primaryTarget, gaussCharges);
        else
            UseRicochet(primaryTarget, ricoCharges);
        return true;
    }

    private bool TryGenericOpenerOGCD(in Strategy strategy, Enemy primaryTarget)
    {
        if (!GenericOpenerInProgress(strategy) || !IsUsableToolTarget(primaryTarget))
            return false;

        if (IsDyingTrashTarget(primaryTarget)
            && (strategy.Buffs.Value == OffensiveStrategy.Automatic
                || strategy.Wildfire.Value == WildfireStrategy.Automatic
                || strategy.Hypercharge.Value == OffensiveStrategy.Automatic
                || strategy.Queen.Value == QueenStrategy.Automatic))
        {
            SetGenericOpenerStage(GenericOpenerStage.Complete);
            return false;
        }

        var firstStage = _genericOpenerMode == OpenerStrategy.NormalBurst
            ? GenericOpenerStage.NormalAirAnchor
            : GenericOpenerStage.ZeroChainSaw;
        if (_genericOpenerStage == firstStage
            && _genericOpenerLastGCD == AID.None
            && ReassembleLeft == 0
            && ChargeInfo(AID.Reassemble).Charges > 0)
        {
            PushOGCD(AID.Reassemble, Player, priority: 20);
            return true;
        }

        if (_genericOpenerMode == OpenerStrategy.NormalBurst)
        {
            if (ShouldUseGenericOpenerStabilizer(strategy, primaryTarget))
            {
                PushOGCD(AID.BarrelStabilizer, Player, priority: 20);
                return true;
            }

            if (_genericOpenerStage == GenericOpenerStage.NormalExcavator
                && _genericOpenerLastGCD == AID.ChainSaw
                && _genericOpenerWeavesAfterLastGCD == 0
                && ReassembleLeft == 0
                && ChargeInfo(AID.Reassemble).Charges > 0
                && CanWeave(AID.Reassemble))
            {
                PushOGCD(AID.Reassemble, Player, priority: 20);
                return true;
            }

            if (_genericOpenerStage == GenericOpenerStage.NormalFullMetalField)
            {
                if (GenericOpenerQueenReady(strategy) && !_genericOpenerUsedQueen)
                {
                    var queen = BestActionUnlocked(AID.AutomatonQueen, AID.RookAutoturret);
                    if (CanWeave(queen))
                    {
                        PushOGCD(queen, Player, priority: 20);
                        return true;
                    }
                }
            }

            if (_genericOpenerStage == GenericOpenerStage.NormalHypercharge
                && GenericOpenerHyperchargePossible(strategy)
                && CanWeave(AID.Hypercharge))
            {
                PushOGCD(AID.Hypercharge, Player, priority: 20);
                return true;
            }

            if (_genericOpenerStage == GenericOpenerStage.NormalHeat
                && !WildfireWasCommitted()
                && NormalOpenerWildfireReady(strategy)
                && GetGenericOpenerWildfireTarget(strategy, primaryTarget) is { } wildfireTarget)
            {
                PushOGCD(AID.Wildfire, wildfireTarget, priority: 30);
                return true;
            }

            // the Queen normally goes out before Full Metal Field; when an interruption carried the opener past that stage it is woven
            // here (after the Hypercharge / Wildfire of the same slot) instead of waiting for the opener to finish
            if (_genericOpenerStage is GenericOpenerStage.NormalHypercharge or GenericOpenerStage.NormalHeat
                && GenericOpenerQueenReady(strategy)
                && !_genericOpenerUsedQueen)
            {
                var queen = BestActionUnlocked(AID.AutomatonQueen, AID.RookAutoturret);
                if (CanWeave(queen))
                {
                    PushOGCD(queen, Player, priority: 20);
                    return true;
                }
            }

            if (_genericOpenerStage == GenericOpenerStage.NormalHeat
                && _genericOpenerLastGCD is AID.BlazingShot or AID.HeatBlast or AID.AutoCrossbow
                && _genericOpenerWeavesAfterLastGCD == 0)
                return PushOneGenericOpenerCharge(primaryTarget);
        }
        else
        {
            if (ShouldUseGenericOpenerStabilizer(strategy, primaryTarget))
            {
                PushOGCD(AID.BarrelStabilizer, Player, priority: 20);
                return true;
            }

            if (_genericOpenerStage == GenericOpenerStage.ZeroAirAnchor)
            {
                if (GenericOpenerWildfireRequired(strategy)
                    && !WildfireWasCommitted()
                    && ZeroSecondWildfireReady(strategy)
                    && GetGenericOpenerWildfireTarget(strategy, primaryTarget) is { } wildfireTarget)
                {
                    PushOGCD(AID.Wildfire, wildfireTarget, priority: 20);
                    return true;
                }
            }

            if (_genericOpenerStage == GenericOpenerStage.ZeroHypercharge)
            {
                if (GenericOpenerWildfireRequired(strategy) && !WildfireWasCommitted())
                {
                    if (ZeroSecondWildfireReady(strategy)
                        && GetGenericOpenerWildfireTarget(strategy, primaryTarget) is { } wildfireTarget)
                    {
                        PushOGCD(AID.Wildfire, wildfireTarget, priority: 20);
                        return true;
                    }

                    return false;
                }

                if (_genericOpenerWeavesAfterLastGCD == 0
                    && !_genericOpenerUsedPreHyperchargeCharge
                    && !ChargesSafeForHypercharge()
                    && PushOneGenericOpenerCharge(primaryTarget))
                    return true;

                if (GenericOpenerHyperchargePossible(strategy) && CanWeave(AID.Hypercharge))
                {
                    PushOGCD(AID.Hypercharge, Player, priority: 20);
                    return true;
                }
            }

            if (_genericOpenerStage == GenericOpenerStage.ZeroHeat
                && _genericOpenerLastGCD is AID.BlazingShot or AID.HeatBlast or AID.AutoCrossbow
                && _genericOpenerWeavesAfterLastGCD == 0)
                return PushOneGenericOpenerCharge(primaryTarget);

            if (_genericOpenerStage == GenericOpenerStage.ZeroSecondDrill
                && GenericOpenerQueenReady(strategy)
                && !_genericOpenerUsedQueen)
            {
                var queen = BestActionUnlocked(AID.AutomatonQueen, AID.RookAutoturret);
                if (CanWeave(queen))
                {
                    PushOGCD(queen, Player, priority: 20);
                    return true;
                }
            }

            if (_genericOpenerStage == GenericOpenerStage.ZeroSecondDrill
                && (!GenericOpenerQueenReady(strategy) || _genericOpenerUsedQueen)
                && ReassembleLeft == 0
                && ChargeInfo(AID.Reassemble).Charges > 0
                && CanWeave(AID.Reassemble))
            {
                PushOGCD(AID.Reassemble, Player, priority: 20);
                return true;
            }
        }

        return false;
    }

    private Enemy? GetGenericOpenerWildfireTarget(in Strategy strategy, Enemy primaryTarget)
    {
        var overrideTarget = ResolveTargetOverride(strategy.Wildfire);
        var target = IsUsableToolTarget(overrideTarget) ? overrideTarget : primaryTarget;
        return target is { Priority: >= 0 } && target.Actor.IsTargetable && CanWeaveWildfire() ? target : null;
    }

    private void OGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        if (CountdownRemaining == null && !Player.InCombat && Player.DistanceToHitbox(primaryTarget) <= 25 && ReassembleLeft == 0 && ShouldReassemble(strategy, primaryTarget))
            PushOGCD(AID.Reassemble, Player, priority: 50);

        if (!Player.InCombat || primaryTarget == null)
            return;

        if (TryGenericOpenerOGCD(strategy, primaryTarget))
            return;

        if (ShouldUseDancingMadMitigation(AID.Tactician, primaryTarget) || ShouldUseGenericMitigation(AID.Tactician, primaryTarget))
            PushOGCD(AID.Tactician, Player);

        if (ShouldUseDancingMadMitigation(AID.Dismantle, primaryTarget) || ShouldUseGenericMitigation(AID.Dismantle, primaryTarget))
            PushOGCD(AID.Dismantle, primaryTarget);

        if (!IsUsableToolTarget(primaryTarget))
            return;

        if (ShouldUsePotion(strategy, primaryTarget))
            PushPotion();

        if (GenericOpenerInProgress(strategy))
        {
            if (_genericOpenerStage is not GenericOpenerStage.NormalHypercharge
                and not GenericOpenerStage.NormalHeat
                and not GenericOpenerStage.ZeroHypercharge
                and not GenericOpenerStage.ZeroHeat)
            {
                UseCharges(strategy, primaryTarget);
            }
            return;
        }

        if (GetWildfireTarget(strategy, primaryTarget) is { } tar)
        {
            PushOGCD(AID.Wildfire, tar, priority: IsRecoveryMode() ? 4 : 1, delay: GCD - 0.8f);
            if (IsRecoveryMode())
                return;
        }

        if (WildfireLeft > 0 && ShouldHypercharge(strategy, primaryTarget))
            PushOGCD(AID.Hypercharge, Player);

        if (ShouldReassemble(strategy, primaryTarget))
            PushOGCD(AID.Reassemble, Player, priority: 3);

        if (ShouldStabilize(strategy, primaryTarget))
            PushOGCD(AID.BarrelStabilizer, Player, priority: 2);

        UseCharges(strategy, primaryTarget);

        if (ShouldMinion(strategy, primaryTarget))
            PushOGCD(AID.RookAutoturret, Player, priority: IsRecoveryMode() ? 3 : 1);

        if (WildfireLeft == 0 && ShouldHypercharge(strategy, primaryTarget))
            PushOGCD(AID.Hypercharge, Player);
    }

    private float NextToolCharge => Math.Min(ReadyIn(AID.Drill), Math.Min(ReadyIn(AID.ChainSaw), ReadyIn(AID.AirAnchor)));
    private float NextToolCap => Math.Min(MaxChargesIn(AID.Drill), Math.Min(MaxChargesIn(AID.ChainSaw), MaxChargesIn(AID.AirAnchor)));

    private float MaxGaussCD => MaxChargesIn(AID.GaussRound);
    private float MaxRicochetCD => MaxChargesIn(AID.Ricochet);

    private (float CapIn, int Charges, int MaxCharges) ChargeInfo(AID action)
    {
        if (!Unlocked(action))
            return (float.MaxValue, 0, 0);

        var definition = ActionDefinitions.Instance.Spell(action);
        if (definition == null)
            return (float.MaxValue, 0, 0);

        var maxCharges = definition.MaxChargesAtLevel(Player.Level);
        if (definition.MainCooldownGroup < 0)
            return (0, maxCharges, maxCharges);

        var cooldown = World.Client.Cooldowns[definition.ActualMainCooldownGroup(World.Client.DutyActions)];
        if (cooldown.Total <= 0)
            return (0, maxCharges, maxCharges);

        var capIn = definition.ChargeCapIn(World.Client.Cooldowns, World.Client.DutyActions, Player.Level);
        var singleChargeCooldown = cooldown.Total / definition.MaxChargesAtCap();
        var missingCharges = (int)MathF.Ceiling(capIn / singleChargeCooldown - 0.001f);
        var charges = Math.Clamp(maxCharges - missingCharges, 0, maxCharges);
        if (definition.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions) <= 0.05f)
            charges = Math.Max(charges, 1);

        return (capIn, charges, maxCharges);
    }

    private bool ChargeCapSoon(AID action)
    {
        var (capIn, charges, maxCharges) = ChargeInfo(action);
        return maxCharges > 0 && (charges >= maxCharges || capIn <= GCD + 0.6f);
    }

    private void UseCharges(in Strategy strategy, Enemy? primaryTarget)
    {
        var wfIn = ReadyIn(AID.Wildfire);
        var holdForWildfire = strategy.Wildfire.Value != WildfireStrategy.Delay;
        var holdForBurst = (holdForWildfire && wfIn <= 15) || RaidBuffsIn < 15 || BurstAlignmentSoon(strategy);
        var targetDying = primaryTarget?.Priority < 0;

        UseCharge(AID.GaussRound, primaryTarget, holdForBurst, targetDying);
        UseCharge(AID.Ricochet, primaryTarget, holdForBurst, targetDying);
    }

    private void UseCharge(AID action, Enemy? primaryTarget, bool holdForBurst, bool targetDying)
    {
        // Ricochet / Double Check are AOE: no primary-target fallback past SelectTarget's decision
        var target = action == AID.GaussRound && !Unlocked(AID.DoubleCheck) ? primaryTarget : BestRangedAOETarget;
        if (target == null)
            return;

        var (_, charges, maxCharges) = ChargeInfo(action);
        if (charges <= 0)
            return;

        var capNow = charges >= maxCharges;
        var capSoon = ChargeCapSoon(action);
        var spendForWindow = WildfireLeft > 0 || RaidBuffsLeft > 0 || Overheated;

        if (targetDying || capNow || capSoon || spendForWindow)
        {
            PushCharge(action, primaryTarget, charges);
            return;
        }

        if (holdForBurst)
            return;

        PushCharge(action, primaryTarget, charges);
    }

    private void PushCharge(AID action, Enemy? primaryTarget, int charges)
    {
        if (action == AID.GaussRound)
            UseGauss(primaryTarget, charges);
        else
            UseRicochet(primaryTarget, charges);
    }

    private void UseGauss(Enemy? primaryTarget, int charges) => Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.GaussRound), (Unlocked(AID.DoubleCheck) ? BestRangedAOETarget : primaryTarget)?.Actor, ActionQueue.Priority.Low - 50 + charges + (MaxChargesIn(AID.GaussRound) <= MaxChargesIn(AID.Ricochet) ? 0.1f : 0));
    private void UseRicochet(Enemy? primaryTarget, int charges) => Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Ricochet), BestRangedAOETarget?.Actor, ActionQueue.Priority.Low - 50 + charges + (MaxChargesIn(AID.Ricochet) < MaxChargesIn(AID.GaussRound) ? 0.1f : 0));

    private bool ShouldReassemble(in Strategy strategy, Enemy? primaryTarget)
    {
        if (ReassembleLeft > 0 || !Unlocked(AID.Reassemble) || Overheated || primaryTarget == null || primaryTarget?.Priority == Enemy.PriorityPointless)
            return false;

        if (!CanReassembleAction(NextGCD))
            return false;

        if (ReassembleCapSoon())
            return true;

        if (ShouldHoldReassembleForBurst(strategy))
            return false;

        return AlwaysReassemble(NextGCD);
    }

    private bool AlwaysReassemble(AID action) => action is AID.Drill or AID.AirAnchor or AID.ChainSaw or AID.Excavator;

    private bool CanReassembleAction(AID action) => action switch
    {
        AID.Drill or AID.AirAnchor or AID.HotShot or AID.ChainSaw or AID.Excavator => true,
        AID.CleanShot => !Unlocked(AID.Drill),
        _ => false,
    };

    private bool ReassembleCapSoon()
        => MaxChargesIn(AID.Reassemble) <= GCD * 2;

    private bool ShouldHoldReassembleForBurst(in Strategy strategy)
        => !ReassembleCapSoon()
        && ChargeInfo(AID.Reassemble).Charges <= 1
        && BurstAlignmentSoon(strategy)
        && !(IsRecoveryMode() ? RecoveryBurstWindow(strategy) : StandardBurstPreparationWindow(strategy));

    private int BatteryFromAction(AID action) => action switch
    {
        AID.ChainSaw or AID.AirAnchor or AID.Excavator or AID.HotShot => 20,
        AID.CleanShot or AID.HeatedCleanShot => 10,
        _ => 0
    };

    private bool ShouldMinion(in Strategy strategy, Enemy? primaryTarget)
    {
        if (strategy.Queen.Value == QueenStrategy.Never)
            return false;

        if (!Unlocked(AID.RookAutoturret) || primaryTarget == null || HasMinion || Battery < 50 || primaryTarget?.Priority < 0)
            return false;

        // spec rule 1: the Queen's attacks after the loss are wasted; battery does not grow during downtime, so waiting costs nothing unless it caps
        if (!_voiFightEnd && Mechanic.ReturnKnown && !Mechanic.DowntimeNow && Mechanic.TargetLossIn < Battery / 5f && !BatteryOvercapSoon())
            return false;

        if (strategy.Queen.Value == QueenStrategy.Automatic && IsDyingTrashTarget(primaryTarget))
            return false;

        if (GenericOpenerQueenReady(strategy))
            return true;

        if (strategy.Queen.Value == QueenStrategy.Automatic && IsRecoveryMode())
            return ShouldUseRecoveryQueen(strategy);

        if (strategy.Queen.Value == QueenStrategy.Automatic
            && Unlocked(AID.AutomatonQueen)
            && StandardBurstAnchorIn(strategy) <= BurstResyncLeadTime)
            return StandardBurstAnchorIn(strategy) <= QueenRaidBuffLeadTime;

        if (strategy.Queen.Value == QueenStrategy.Automatic && StandardBurstAnchorIn(strategy) <= BurstResyncLeadTime)
        {
            if (StandardBurstToolOrderActive(strategy) && !LastWeaponskillWas(AID.Excavator))
                return false;

            return BatteryOvercapSoon() || StandardBurstAnchorIn(strategy) <= QueenRaidBuffLeadTime;
        }

        if (GetWildfireTarget(strategy, primaryTarget) != null && !BatteryOvercapSoon() && !ShouldUseTwoMinuteQueen())
            return false;

        if (ShouldUseDancingMadProfile())
        {
            if (BatteryOvercapSoon())
                return true;
            if (IsInDancingMadActionWindow(AID.AutomatonQueen, 4, 4))
                return true;
            if (ShouldHoldForDancingMadAction(AID.AutomatonQueen))
                return false;
        }

        return strategy.Queen.Value switch
        {
            QueenStrategy.Automatic => ShouldUseAutomaticQueen(strategy),
            QueenStrategy.MinGauge => Battery >= 50,
            QueenStrategy.FullGauge => Battery >= 100,
            QueenStrategy.RaidBuffsOnly => ShouldUseRaidBuffQueen(),
            _ => false,
        };
    }

    private bool BatteryOvercapSoon()
        => Battery >= 100 || Battery + BatteryFromAction(NextGCD) > 100;

    private bool ShouldUseRecoveryQueen(in Strategy strategy)
    {
        if (!IsRecoveryMode())
            return false;

        if (RecoveryBurstVerySoon(strategy))
        {
            var wildfirePending = Unlocked(AID.Wildfire)
                && strategy.Wildfire.Value != WildfireStrategy.Delay
                && !RecoveryWildfireCommitted();
            return !wildfirePending && Battery >= 100;
        }

        if (BatteryOvercapSoon())
            return true;

        if (RecoveryBurstSoon(strategy))
            return false;

        return Battery >= 80;
    }

    private bool ShouldUseAutomaticQueen(in Strategy strategy)
    {
        if (BatteryOvercapSoon())
            return true;

        if (CombatTimer < 30)
            return GenericOpenerQueenReady(strategy);

        if (CombatTimer is >= 50 and < 90)
            return Battery >= 90;

        if (ShouldUseTwoMinuteQueen())
            return true;

        if (CombatTimer >= 120)
        {
            if (ShouldUseOddMinuteFirstQueen())
                return true;

            if (ShouldUseOddMinuteSecondQueen())
                return true;
        }

        return false;
    }

    private bool ShouldUseRaidBuffQueen()
        => RaidBuffsLeft > 10
        || RaidBuffsIn is > 0 and <= QueenRaidBuffLeadTime
        || ShouldUseTwoMinuteQueen()
        || BatteryOvercapSoon();

    private bool ShouldUseTwoMinuteQueen()
        => Battery >= 100 && IsEvenMinuteBurstWindow(8, 18) && LastWeaponskillWas(AID.AirAnchor, AID.HotShot);

    private bool ShouldUseOddMinuteFirstQueen()
    {
        var cycle = CombatTimer % 120f;
        return cycle is >= 30f and <= 55f && Battery >= 50;
    }

    private int OddMinuteSecondQueenTarget()
    {
        if (CombatTimer < 150f)
            return 0;

        var cycle = CombatTimer % 120f;
        if (cycle < 55f || cycle > 95f)
            return 0;

        var evenCycle = Math.Max(1, (int)(CombatTimer / 120f));
        return ((evenCycle - 1) % 3) switch
        {
            0 => 60,
            1 => 70,
            _ => 80
        };
    }

    private bool ShouldUseOddMinuteSecondQueen()
    {
        var target = OddMinuteSecondQueenTarget();
        if (target <= 0)
            return false;

        return Battery >= target;
    }

    private bool ShouldDelayExcavatorForOddQueen()
    {
        if (ShouldUseDancingMadProfile() || IsRecoveryMode() || ExcavatorLeft <= GCD || CombatTimer < 120)
            return false;

        var target = OddMinuteSecondQueenTarget();
        if (target <= 0 || Battery >= target)
            return false;

        var nextComboGain = ComboLastMove is AID.SlugShot or AID.HeatedSlugShot ? 10 : 0;
        return nextComboGain > 0
            && Battery + nextComboGain >= target
            && ExcavatorLeft > GCD * 2 + 0.5f;
    }

    private void UpdateLastWeaponskill()
    {
        if (!Player.InCombat)
        {
            _lastWeaponskill = AID.None;
            _lastWeaponskillObservedTime = Manager.LastCast.Time;
            return;
        }

        var cast = Manager.LastCast.Data;
        if (cast == null || Manager.LastCast.Time == _lastWeaponskillObservedTime)
            return;

        var action = cast.IsSpell(AID.SplitShot) ? AID.SplitShot
            : cast.IsSpell(AID.HeatedSplitShot) ? AID.HeatedSplitShot
            : cast.IsSpell(AID.SlugShot) ? AID.SlugShot
            : cast.IsSpell(AID.HeatedSlugShot) ? AID.HeatedSlugShot
            : cast.IsSpell(AID.CleanShot) ? AID.CleanShot
            : cast.IsSpell(AID.HeatedCleanShot) ? AID.HeatedCleanShot
            : cast.IsSpell(AID.SpreadShot) ? AID.SpreadShot
            : cast.IsSpell(AID.Scattergun) ? AID.Scattergun
            : cast.IsSpell(AID.HotShot) ? AID.HotShot
            : cast.IsSpell(AID.AirAnchor) ? AID.AirAnchor
            : cast.IsSpell(AID.Drill) ? AID.Drill
            : cast.IsSpell(AID.Bioblaster) ? AID.Bioblaster
            : cast.IsSpell(AID.ChainSaw) ? AID.ChainSaw
            : cast.IsSpell(AID.Excavator) ? AID.Excavator
            : cast.IsSpell(AID.FullMetalField) ? AID.FullMetalField
            : cast.IsSpell(AID.HeatBlast) ? AID.HeatBlast
            : cast.IsSpell(AID.BlazingShot) ? AID.BlazingShot
            : cast.IsSpell(AID.AutoCrossbow) ? AID.AutoCrossbow
            : AID.None;

        if (action == AID.None)
            return;

        _lastWeaponskill = action;
        _lastWeaponskillObservedTime = Manager.LastCast.Time;
    }

    // non-params overloads avoid an array allocation on every call from Exec
    private bool LastWeaponskillWas(AID action) => _lastWeaponskill == action;
    private bool LastWeaponskillWas(AID a, AID b) => _lastWeaponskill == a || _lastWeaponskill == b;
    private bool LastWeaponskillWas(AID a, AID b, AID c) => _lastWeaponskill == a || _lastWeaponskill == b || _lastWeaponskill == c;

    private bool LastWeaponskillWas(params AID[] actions)
    {
        foreach (var action in actions)
            if (_lastWeaponskill == action)
                return true;

        return false;
    }

    private bool StandardBurstHyperchargeBeforeWildfire(in Strategy strategy)
        => StandardBurstToolOrderActive(strategy)
        && strategy.Hypercharge.Value != OffensiveStrategy.Delay
        && strategy.Wildfire.Value != WildfireStrategy.Delay
        && Unlocked(AID.Hypercharge)
        && Unlocked(AID.Wildfire)
        && ReadyIn(AID.Wildfire) <= GCD
        && ReassembleLeft <= GCD
        && ExcavatorLeft <= GCD
        && FMFLeft <= GCD
        && StandardBurstStabilizerCommittedOrUnavailable(strategy);

    private bool ShouldHypercharge(in Strategy strategy, Enemy? primaryTarget)
    {
        // strategy-independent preconditions, hypercharge cannot be used at all in these cases
        if (!Unlocked(AID.Hypercharge) || HyperchargedLeft == 0 && Heat < 50 || Overheated)
            return false;

        // don't want to use reassemble on heat blast, even if strategy is Force, since presumably next GCD will be a tool charge
        if (ReassembleLeft > GCD)
            return false;

        // spec rule 2: a Hypercharged effect that would run out during a long target loss is spent before it, if its five shots fit
        if (strategy.Hypercharge.Value != OffensiveStrategy.Delay && HyperchargedLeft > 0 && Mechanic.ExpiresDuringLoss(HyperchargedLeft) && Mechanic.TargetLossIn >= GCD + 6)
            return true;

        // primary target is dying
        if (primaryTarget?.Priority < 0)
            return false;

        if (strategy.Hypercharge.Value == OffensiveStrategy.Automatic && IsDyingTrashTarget(primaryTarget))
            return false;

        // FightRemaining (value-of-information experiment): Heat that cannot be spent later is spent now, every hold ignored, as long as
        // at least three of the five Blazing Shots still land before the end (1.5 s each after the weave)
        if (_voiFightEnd && VoiFightEndHypercharge && strategy.Hypercharge.Value != OffensiveStrategy.Delay && !GenericOpenerInProgress(strategy)
            && Mechanic.TargetLossIn >= GCD + 0.6f + 4.5f && Mechanic.TargetLossIn <= GCD + 0.6f + 7.5f + VoiHyperchargeSlack)
            return true;

        if (GenericOpenerInProgress(strategy))
            return GenericOpenerHyperchargePossible(strategy);

        if (ShouldReserveHyperchargeForZeroSecondWildfire(strategy))
            return false;

        if (strategy.Hypercharge.Value == OffensiveStrategy.Automatic
            && ShouldHoldHyperchargeForBurstAlignment(strategy))
            return false;

        if (StandardBurstToolOrderActive(strategy) && (ExcavatorLeft > GCD || FMFLeft > GCD))
            return false;

        if (IsRecoveryMode())
        {
            var wildfireRequired = Unlocked(AID.Wildfire) && strategy.Wildfire.Value != WildfireStrategy.Delay;
            if (wildfireRequired
                && !RecoveryWildfireCommitted()
                && RecoveryBurstSoon(strategy)
                && GetWildfireTarget(strategy, primaryTarget) == null)
                return false;
        }

        if (!ShouldUseDancingMadProfile()
            && strategy.Tools.Value != ToolStrategy.Delay
            && ExcavatorLeft > GCD
            && !ShouldDelayExcavatorForOddQueen())
        {
            var safeDuringWildfire = WildfireLeft > 0 && ExcavatorLeft > GCD + 7.5f;
            var endingSoon = DowntimeIn < GCD + 6;
            if (!safeDuringWildfire && !endingSoon)
                return false;
        }

        // under Wildfire the Hypercharge goes first unless a charge is already capped: each of the five Blazing Shots leaves a weave slot
        // for the Double Check / Checkmate charges, while waiting for them pushes the last shot out of the Wildfire
        if (!ChargesSafeForHypercharge() && (WildfireLeft <= 0 || ChargesCapped()))
            return false;

        if (!ShouldUseDancingMadProfile()
            && strategy.Hypercharge.Value != OffensiveStrategy.Delay
            && Heat >= 100
            && FMFLeft <= GCD)
            return true;

        if (IsZeroSecondBurstOpener(strategy)
            && Unlocked(AID.Wildfire)
            && strategy.Wildfire.Value != WildfireStrategy.Delay
            && WildfireLeft == 0)
            return false;

        if (WildfireLeft > 0)
            return Heat >= 50 || HyperchargedLeft > 0;

        switch (strategy.Hypercharge.Value)
        {
            case OffensiveStrategy.Force:
                return true;
            case OffensiveStrategy.Delay:
                return false;
            default:
                break;
        }

        // avoid delaying wildfire
        // TODO figure out how long we actually need to wait to ensure enough heat
        if (strategy.Wildfire.Value != WildfireStrategy.Delay
            && Unlocked(AID.Wildfire)
            && ReadyIn(AID.Wildfire) < 20
            && !StandardBurstHyperchargeBeforeWildfire(strategy)
            && GetWildfireTarget(strategy, primaryTarget) == null)
            return false;

        // we can't early weave if the overheat window will contain a regular GCD, because then it will expire before last HB
        if (FMFLeft > 0 && GCD > 1.1f)
            return false;

        if (DowntimeIn < GCD + 6)
            return false;

        // spec rule 1: same cut-off for a predicted target loss (five Heat Blasts need about 6 s)
        if (Mechanic.Enabled && Mechanic.TargetLossIn < GCD + 6)
            return false;

        if (ShouldUseDancingMadProfile())
        {
            if (Heat >= 100)
                return true;
            if (!IsInDancingMadActionWindow(AID.Hypercharge, 2.5f, 2.5f) && ShouldHoldForDancingMadAction(AID.Hypercharge))
                return false;
        }

        /* A full segment of Hypercharge is exactly three GCDs worth of time, or 7.5 seconds. Because of this, you should never enter Hypercharge if Chainsaw, Drill or Air Anchor has less than eight seconds on their cooldown timers. Doing so will cause the Chainsaw, Drill or Air Anchor cooldowns to drift, which leads to a loss of DPS and will more than likely cause issues down the line in your rotation when you reach your rotational reset at Wildfire.
         */
        return strategy.Tools.Value == ToolStrategy.Delay || NextToolCap > GCD + 7.5f;
    }

    private bool ChargesCapped()
    {
        var (_, gaussCharges, gaussMax) = ChargeInfo(AID.GaussRound);
        var (_, ricoCharges, ricoMax) = ChargeInfo(AID.Ricochet);
        return gaussMax > 0 && gaussCharges >= gaussMax || ricoMax > 0 && ricoCharges >= ricoMax;
    }

    private bool ChargesSafeForHypercharge()
    {
        var (_, gaussCharges, _) = ChargeInfo(AID.GaussRound);
        var (_, ricoCharges, _) = ChargeInfo(AID.Ricochet);

        return gaussCharges < 2 && ricoCharges < 2;
    }

    private Enemy? GetWildfireTarget(in Strategy strategy, Enemy? primaryTarget)
    {
        var wf = strategy.Wildfire;
        var overrideTarget = ResolveEnemy(wf);
        var wfTarget = IsUsableToolTarget(overrideTarget) ? overrideTarget : primaryTarget;

        if (!Unlocked(AID.Wildfire) || !CanWeaveWildfire() || wf == WildfireStrategy.Delay)
            return null;

        // spec rule 1: Wildfire's 10 s window is held when a known target loss would cut it
        if (Mechanic.ShouldHoldWindow(10f, 120f, GCDLength))
            return null;

        if (!IsUsableToolTarget(wfTarget))
            return null;

        if (wf.Value == WildfireStrategy.Automatic && IsDyingTrashTarget(wfTarget))
            return null;

        if (strategy.Hypercharge == OffensiveStrategy.Delay && !Overheated && HyperchargedLeft == 0)
            return null;

        if (IsRecoveryMode())
        {
            if (ShouldDelayWildfireForBurstSetup(strategy, wfTarget))
                return null;

            return RecoveryBurstReady(strategy) && wfTarget?.Actor.IsTargetable == true ? wfTarget : null;
        }

        if (NormalOpenerWildfireReady(strategy))
            return wfTarget;

        if (LowLevelOpenerWildfireReady(strategy))
            return wfTarget;

        if (IsZeroSecondBurstOpener(strategy))
            return ZeroSecondWildfireReady(strategy) ? wfTarget : null;

        if (IsZeroSecondBurstRecastRotation(strategy))
        {
            if (ShouldDelayWildfireForBurstSetup(strategy, wfTarget))
                return null;

            return ZeroSecondWildfireGaugeReady() ? wfTarget : null;
        }

        if (ShouldUseDancingMadProfile() && !IsInDancingMadActionWindow(AID.Wildfire, 2.5f, 2.5f) && ShouldHoldForDancingMadAction(AID.Wildfire))
            return null;

        if (ShouldUseDancingMadProfile() && IsInDancingMadActionWindow(AID.Wildfire, 2.5f, 2.5f))
            return WildfireBurstReady(strategy) ? wfTarget : null;

        if (ShouldDelayWildfireForBurstSetup(strategy, wfTarget))
            return null;

        if (StandardBurstHyperchargeBeforeWildfire(strategy) && !Overheated && HyperchargedLeft == 0)
            return null;

        if (wf == WildfireStrategy.Hypercharge)
            return WildfireBurstReady(strategy) ? wfTarget : null;

        if (wf != WildfireStrategy.ASAP && wf != WildfireStrategy.Automatic)
            return null;

        if (wf == WildfireStrategy.Automatic && ShouldHoldMajorCooldownForStandardBurst(strategy))
            return null;

        // hack for opener - delay until all 4 tool charges are used
        if (CombatTimer < 60)
            return NextToolCharge > GCD && ExcavatorLeft == 0 && FMFLeft == 0 ? wfTarget : null;

        return (FMFLeft == 0 || FMFLeft > GCD && ExcavatorLeft == 0) && WildfireBurstReady(strategy) ? wfTarget : null;
    }

    private bool ShouldDelayWildfireForBurstSetup(in Strategy strategy, Enemy? primaryTarget)
    {
        if (IsZeroSecondBurstRecastRotation(strategy))
            return ReassembleLeft > GCD || !ZeroSecondWildfireGaugeReady();

        if (ReassembleLeft > GCD || ShouldReassemble(strategy, primaryTarget))
            return true;

        if (ShouldStabilize(strategy, primaryTarget))
            return true;

        return strategy.Tools.Value == ToolStrategy.Automatic
            && (ExcavatorLeft > GCD || FMFLeft > GCD && !StandardBurstToolOrderActive(strategy));
    }

    private Enemy? GetWildfireTargetIgnoringFullMetalField(in Strategy strategy, Enemy? primaryTarget)
    {
        var wf = strategy.Wildfire;
        var overrideTarget = ResolveTargetOverride(wf);
        var wfTarget = IsUsableToolTarget(overrideTarget) ? overrideTarget : primaryTarget;

        if (!Unlocked(AID.Wildfire) || !CanWeaveWildfire() || wf == WildfireStrategy.Delay)
            return null;

        if (Mechanic.ShouldHoldWindow(10f, 120f, GCDLength))
            return null;

        if (!IsUsableToolTarget(wfTarget))
            return null;

        if (wf.Value == WildfireStrategy.Automatic && IsDyingTrashTarget(wfTarget))
            return null;

        if (strategy.Hypercharge == OffensiveStrategy.Delay && !Overheated && HyperchargedLeft == 0)
            return null;

        if (IsRecoveryMode())
            return RecoveryBurstReady(strategy) && wfTarget?.Actor.IsTargetable == true ? wfTarget : null;

        if (NormalOpenerWildfireReady(strategy))
            return wfTarget;

        if (LowLevelOpenerWildfireReady(strategy))
            return wfTarget;

        if (IsZeroSecondBurstOpener(strategy))
            return ZeroSecondWildfireReady(strategy) ? wfTarget : null;

        if (IsZeroSecondBurstRecastRotation(strategy))
            return ReassembleLeft <= GCD && ZeroSecondWildfireGaugeReady() ? wfTarget : null;

        if (ShouldUseDancingMadProfile() && !IsInDancingMadActionWindow(AID.Wildfire, 2.5f, 2.5f) && ShouldHoldForDancingMadAction(AID.Wildfire))
            return null;

        if (ShouldUseDancingMadProfile() && IsInDancingMadActionWindow(AID.Wildfire, 2.5f, 2.5f))
            return WildfireBurstReady(strategy) ? wfTarget : null;

        if (wf == WildfireStrategy.Hypercharge)
            return WildfireBurstReady(strategy) ? wfTarget : null;

        if (wf != WildfireStrategy.ASAP && wf != WildfireStrategy.Automatic)
            return null;

        if (wf == WildfireStrategy.Automatic && ShouldHoldMajorCooldownForStandardBurst(strategy))
            return null;

        if (CombatTimer < 60)
            return NextToolCharge > GCD && ExcavatorLeft == 0 ? wfTarget : null;

        return WildfireBurstReady(strategy) ? wfTarget : null;
    }

    private bool WildfireBurstReady(in Strategy strategy)
        => ReassembleLeft <= GCD && (Overheated || HyperchargedLeft > 0 || Heat >= 50 && (strategy.Tools.Value == ToolStrategy.Delay || NextToolCap > GCD + 7.5f));

    private bool IsZeroSecondBurstOpener(in Strategy strategy)
        => IsGenericOpener(strategy)
        && strategy.Opener.Value == OpenerStrategy.ZeroSecondBurst
        && CombatTimer <= ZeroSecondBurstOpenerWindow;

    private bool IsZeroSecondBurstRecastRotation(in Strategy strategy)
        => !ShouldUseDancingMadProfile()
        && !GenericOpenerInProgress(strategy)
        && strategy.Opener.Value == OpenerStrategy.ZeroSecondBurst
        && CombatTimer > ZeroSecondBurstOpenerWindow;

    private bool ZeroSecondWildfireGaugeReady()
        => HyperchargedLeft > 0 || Heat >= 50;

    private bool ShouldReserveHyperchargeForZeroSecondWildfire(in Strategy strategy)
    {
        if (!IsZeroSecondBurstRecastRotation(strategy)
            || strategy.Wildfire.Value == WildfireStrategy.Delay
            || !Unlocked(AID.Wildfire)
            || WildfireLeft > 0
            || WildfireRecentlyUsed())
            return false;

        var wildfireIn = ReadyIn(AID.Wildfire);
        return wildfireIn > GCD
            && wildfireIn <= ZeroSecondWildfireGaugeReserveLeadTime
            && (HyperchargedLeft > 0 || Heat < 100);
    }

    private bool ZeroSecondWildfireReady(in Strategy strategy)
    {
        if (ReassembleLeft > GCD)
            return false;

        if (Overheated || HyperchargedLeft > 0 || Heat >= 50 || GenericOpenerStabilizerCommitted())
            return true;

        return strategy.Buffs.Value != OffensiveStrategy.Delay
            && Unlocked(AID.BarrelStabilizer)
            && ReadyIn(AID.BarrelStabilizer) <= GCD
            && CanWeave(AID.BarrelStabilizer);
    }

    private float PotionReadyIn()
    {
        var potion = ActionDefinitions.Instance[ActionDefinitions.IDPotionDex];
        return potion?.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions) ?? float.MaxValue;
    }

    private bool CanUsePotion()
        => PotionLeft <= 0
        && PotionReadyIn() <= 0.1f
        && World.Client.GetInventoryItemQuantity(ActionDefinitions.IDPotionDex.ID) > 0;

    private bool ShouldUseCountdownPotion(in Strategy strategy)
    {
        if (strategy.Potion.Value != MCHPotionStrategy.OpenerAndEvenBursts)
            return false;

        if (!CanUsePotion())
            return false;

        if (CountdownRemaining is not { } countdown)
            return false;

        return countdown is > 0.1f and <= 2.1f;
    }

    private bool ShouldUsePotion(in Strategy strategy, Enemy? primaryTarget)
    {
        if (strategy.Potion.Value == MCHPotionStrategy.Off)
            return false;

        if (!CanUsePotion() || primaryTarget == null || primaryTarget.Priority < 0)
            return false;

        if (IsRecoveryMode())
            return RecoveryBurstWindow(strategy);

        if (CombatTimer < 30)
            return false;

        if (!StandardBurstPreparationWindow(strategy))
            return false;

        return NextGCD is AID.AirAnchor or AID.HotShot
            || GCDReady(AID.AirAnchor)
            || !Unlocked(AID.AirAnchor) && GCDReady(AID.HotShot);
    }

    private void PushPotion()
        => Hints.ActionsToExecute.Push(ActionDefinitions.IDPotionDex, Player, ActionQueue.Priority.High + 50);

    private bool IsEvenMinuteBurstWindow(float before, float after)
    {
        if (CombatTimer < 100)
            return false;

        var cycle = CombatTimer % 120f;
        return cycle <= after || 120f - cycle <= before;
    }

    private bool ShouldStabilize(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Unlocked(AID.BarrelStabilizer) || !CanWeave(AID.BarrelStabilizer) || strategy.Buffs == OffensiveStrategy.Delay || primaryTarget?.Priority < 0)
            return false;

        if (ReassembleLeft > 0)
            return false;

        // spec rule 1: the Hypercharged / Full Metal Machinist it grants need about three GCDs before the loss
        if (Mechanic.ShouldHoldWindow(3 * GCDLength, 120f, GCDLength))
            return false;

        if (strategy.Buffs.Value == OffensiveStrategy.Automatic && IsDyingTrashTarget(primaryTarget))
            return false;

        if (BarrelStabilizerWouldOvercapHeat())
            return false;

        if (ShouldUseGenericOpenerStabilizer(strategy, primaryTarget))
            return true;

        if (IsRecoveryMode())
            return RecoveryBurstWindow(strategy);

        if (ShouldUseDancingMadProfile())
        {
            if (IsInDancingMadActionWindow(AID.BarrelStabilizer, 2.5f, 2.5f))
                return true;
            if (ShouldHoldForDancingMadAction(AID.BarrelStabilizer))
                return false;

            return OnCooldown(AID.Drill);
        }

        return ShouldUseStandardBurstStabilizer(strategy);
    }

    // Barrel Stabilizer goes out the moment its recast is up. Holding it for the standard burst order (Air Anchor -> Drill -> Barrel Stabilizer)
    // also held the Wildfire, which waits for it in ShouldDelayWildfireForBurstSetup, until the next Air Anchor cycle: on the default tracks the
    // first burst after the opener came ~30 s late and only a quarter of them overlapped the party windows. Harness (with the Air Anchor
    // completion fix above): combat matrix +0.86% (+1.00% without party buffs), with target-loss hints +2.08%; the burst now follows the recast.
    private bool ShouldUseStandardBurstStabilizer(in Strategy strategy)
        => !ShouldUseDancingMadProfile()
        && Unlocked(AID.BarrelStabilizer)
        && strategy.Buffs.Value != OffensiveStrategy.Delay;

    private readonly record struct DancingMadMCHActionWindow(float Time, AID Action, float Before, float After, bool Force, bool HoldBefore, string Note);
    private readonly record struct DancingMadMCHMitigationWindow(float Time, AID Action, float Lead, float Before, float After, bool TargetRequired, float MinUsageRate, string Note);

    private static readonly DancingMadMCHActionWindow[] DancingMadActionWindows =
    [
        new(0.3f, AID.Drill, 0.8f, 1.5f, true, false, "FFLogs top opener tool"),
        new(2.8f, AID.AirAnchor, 1.5f, 2.0f, true, true, "FFLogs top opener tool"),
        new(3.3f, AID.BarrelStabilizer, 1.5f, 2.0f, true, true, "FFLogs top opener burst"),
        new(4.3f, AID.Wildfire, 1.5f, 3.0f, true, true, "FFLogs top opener Wildfire"),
        new(5.3f, AID.ChainSaw, 1.5f, 2.5f, true, true, "FFLogs top opener burst"),
        new(6.9f, AID.Hypercharge, 2.0f, 3.0f, true, true, "FFLogs top opener Wildfire"),
        new(8.1f, AID.Excavator, 2.0f, 8.0f, true, true, "FFLogs top opener Chain Saw follow-up"),
        new(22.8f, AID.FullMetalField, 5.0f, 4.0f, true, true, "FFLogs top opener Full Metal Field"),

        new(42.8f, AID.AirAnchor, 2.0f, 2.0f, true, true, "FFLogs top tool loop"),
        new(58.6f, AID.AutomatonQueen, 6.0f, 8.0f, true, true, "FFLogs top odd-minute Queen"),
        new(65.3f, AID.ChainSaw, 2.5f, 3.0f, true, true, "FFLogs top tool loop"),
        new(67.8f, AID.Excavator, 2.5f, 8.0f, true, true, "FFLogs top Chain Saw follow-up"),
        new(82.9f, AID.AirAnchor, 2.0f, 2.0f, true, true, "FFLogs top tool loop"),

        new(122.9f, AID.AirAnchor, 2.0f, 2.0f, true, true, "FFLogs top two-minute tool"),
        new(123.4f, AID.BarrelStabilizer, 2.0f, 2.5f, true, true, "FFLogs top two-minute burst"),
        new(125.4f, AID.ChainSaw, 2.0f, 2.5f, true, true, "FFLogs top two-minute burst"),
        new(126.9f, AID.Wildfire, 2.0f, 3.0f, true, true, "FFLogs top two-minute Wildfire"),
        new(127.9f, AID.Excavator, 2.0f, 8.0f, true, true, "FFLogs top two-minute Chain Saw follow-up"),
        new(129.0f, AID.Hypercharge, 2.5f, 3.5f, true, true, "FFLogs top two-minute Wildfire"),
        new(133.9f, AID.AutomatonQueen, 6.0f, 6.0f, true, true, "FFLogs top two-minute Queen"),
        new(140.3f, AID.FullMetalField, 5.0f, 4.0f, true, true, "FFLogs top two-minute Full Metal Field"),
        new(162.9f, AID.AirAnchor, 2.0f, 2.0f, true, true, "FFLogs top tool loop"),
        new(186.1f, AID.ChainSaw, 2.5f, 3.0f, true, true, "FFLogs top pre-downtime tool"),
        new(188.5f, AID.Excavator, 2.5f, 4.0f, true, true, "FFLogs top pre-downtime follow-up"),

        new(205.0f, AID.AutomatonQueen, 7.0f, 7.0f, true, true, "FFLogs top phase resume Queen"),
        new(209.0f, AID.AirAnchor, 3.0f, 3.0f, true, true, "FFLogs top phase resume"),
        new(214.7f, AID.Hypercharge, 3.0f, 4.0f, true, true, "FFLogs top phase resume heat"),
        new(243.5f, AID.BarrelStabilizer, 3.0f, 3.0f, true, true, "FFLogs top four-minute burst"),
        new(246.5f, AID.ChainSaw, 3.0f, 3.0f, true, true, "FFLogs top four-minute burst"),
        new(247.9f, AID.Wildfire, 3.0f, 3.0f, true, true, "FFLogs top four-minute Wildfire"),
        new(249.1f, AID.AirAnchor, 3.0f, 3.0f, true, true, "FFLogs top four-minute tool"),
        new(250.3f, AID.Hypercharge, 3.0f, 3.0f, true, true, "FFLogs top four-minute Wildfire"),
        new(259.1f, AID.Excavator, 4.0f, 6.0f, true, true, "FFLogs top four-minute follow-up"),
        new(264.1f, AID.FullMetalField, 4.0f, 4.0f, true, true, "FFLogs top four-minute Full Metal Field"),
        new(289.1f, AID.AirAnchor, 3.0f, 3.0f, true, true, "FFLogs top tool loop"),
        new(304.6f, AID.AutomatonQueen, 7.0f, 7.0f, true, true, "FFLogs top Queen before transition"),
        new(306.6f, AID.ChainSaw, 3.0f, 3.0f, true, true, "FFLogs top pre-transition tool"),
        new(309.0f, AID.Excavator, 3.0f, 4.0f, true, true, "FFLogs top pre-transition follow-up"),
        new(329.1f, AID.AirAnchor, 4.0f, 4.0f, true, true, "FFLogs top transition tool"),

        new(363.5f, AID.BarrelStabilizer, 3.0f, 3.0f, true, true, "FFLogs top six-minute burst"),
        new(366.6f, AID.ChainSaw, 3.0f, 3.0f, true, true, "FFLogs top six-minute burst"),
        new(368.0f, AID.Wildfire, 3.0f, 3.0f, true, true, "FFLogs top six-minute Wildfire"),
        new(367.3f, AID.Hypercharge, 3.0f, 3.5f, true, true, "FFLogs top six-minute Wildfire"),
        new(369.1f, AID.AirAnchor, 4.0f, 5.0f, true, true, "FFLogs top six-minute tool"),
        new(374.0f, AID.Excavator, 4.0f, 4.0f, true, true, "FFLogs top six-minute follow-up"),

        new(424.9f, AID.AutomatonQueen, 7.0f, 7.0f, true, true, "FFLogs top final-phase Queen"),
        new(428.7f, AID.ChainSaw, 4.0f, 3.0f, true, true, "FFLogs top final-phase resume"),
        new(431.5f, AID.AirAnchor, 4.0f, 3.0f, true, true, "FFLogs top final-phase resume"),
        new(434.8f, AID.Hypercharge, 4.0f, 4.0f, true, true, "FFLogs top final-phase heat"),
        new(436.9f, AID.Excavator, 4.0f, 8.0f, true, true, "FFLogs top final-phase follow-up"),
        new(471.5f, AID.AirAnchor, 3.0f, 3.0f, true, true, "FFLogs top final-phase tool loop"),
        new(484.4f, AID.BarrelStabilizer, 3.0f, 3.0f, true, true, "FFLogs top eight-minute burst"),
        new(488.8f, AID.ChainSaw, 3.0f, 3.0f, true, true, "FFLogs top eight-minute burst"),
        new(491.7f, AID.Wildfire, 3.5f, 3.5f, true, true, "FFLogs top eight-minute Wildfire"),
        new(492.6f, AID.Hypercharge, 3.5f, 3.5f, true, true, "FFLogs top eight-minute Wildfire"),
        new(492.0f, AID.Excavator, 3.0f, 5.0f, true, true, "FFLogs top eight-minute follow-up"),
        new(503.1f, AID.FullMetalField, 5.0f, 5.0f, true, true, "FFLogs top eight-minute Full Metal Field"),
        new(511.6f, AID.AirAnchor, 3.0f, 3.0f, true, true, "FFLogs top final-phase tool loop"),
        new(547.6f, AID.AutomatonQueen, 7.0f, 7.0f, true, true, "FFLogs top final-phase Queen"),
        new(548.9f, AID.ChainSaw, 3.0f, 3.0f, true, true, "FFLogs top final-phase tool loop"),
        new(553.8f, AID.Excavator, 4.0f, 4.0f, true, true, "FFLogs top final-phase follow-up"),
        new(551.8f, AID.AirAnchor, 4.0f, 4.0f, true, true, "FFLogs top final-phase tool loop"),

        new(591.8f, AID.AirAnchor, 3.0f, 3.0f, true, true, "FFLogs top final-phase tool loop"),
        new(604.5f, AID.BarrelStabilizer, 3.0f, 3.0f, true, true, "FFLogs top ten-minute burst"),
        new(608.9f, AID.ChainSaw, 3.0f, 3.0f, true, true, "FFLogs top ten-minute burst"),
        new(612.1f, AID.Wildfire, 3.5f, 3.5f, true, true, "FFLogs top ten-minute Wildfire"),
        new(612.9f, AID.Hypercharge, 3.5f, 3.5f, true, true, "FFLogs top ten-minute Wildfire"),
        new(611.5f, AID.Excavator, 3.0f, 6.0f, true, true, "FFLogs top ten-minute follow-up"),
        new(624.2f, AID.FullMetalField, 5.0f, 5.0f, true, true, "FFLogs top ten-minute Full Metal Field"),
        new(631.8f, AID.AirAnchor, 3.0f, 3.0f, true, true, "FFLogs top final-phase tool loop"),
        new(664.8f, AID.AutomatonQueen, 7.0f, 7.0f, true, true, "FFLogs top final-phase Queen"),
        new(669.0f, AID.ChainSaw, 3.0f, 3.0f, true, true, "FFLogs top final-phase tool loop"),
        new(671.9f, AID.AirAnchor, 3.0f, 4.0f, true, true, "FFLogs top final-phase tool loop"),
        new(673.7f, AID.Excavator, 4.0f, 6.0f, true, true, "FFLogs top final-phase follow-up"),

        new(724.5f, AID.BarrelStabilizer, 3.0f, 3.5f, true, true, "FFLogs top twelve-minute burst"),
        new(732.9f, AID.AutomatonQueen, 7.0f, 7.0f, true, true, "FFLogs top twelve-minute Queen"),
        new(735.5f, AID.ChainSaw, 4.0f, 4.0f, true, true, "FFLogs top twelve-minute burst"),
        new(736.4f, AID.Wildfire, 4.0f, 4.0f, true, true, "FFLogs top twelve-minute Wildfire"),
        new(737.3f, AID.Hypercharge, 4.0f, 4.0f, true, true, "FFLogs top twelve-minute Wildfire"),
        new(744.7f, AID.FullMetalField, 5.0f, 5.0f, true, true, "FFLogs top twelve-minute Full Metal Field"),
        new(754.9f, AID.AirAnchor, 5.0f, 5.0f, true, true, "FFLogs top final-phase tool loop"),
        new(756.8f, AID.Excavator, 4.0f, 6.0f, true, true, "FFLogs top final-phase follow-up"),
        new(795.6f, AID.ChainSaw, 4.0f, 4.0f, true, true, "FFLogs top final-phase tool loop"),
        new(795.8f, AID.AirAnchor, 5.0f, 5.0f, true, true, "FFLogs top final-phase tool loop"),
        new(797.9f, AID.Excavator, 4.0f, 6.0f, true, true, "FFLogs top final-phase follow-up"),

        new(844.9f, AID.BarrelStabilizer, 4.0f, 4.0f, true, true, "FFLogs top fourteen-minute burst"),
        new(846.4f, AID.FullMetalField, 5.0f, 5.0f, true, true, "FFLogs top fourteen-minute Full Metal Field"),
        new(856.6f, AID.ChainSaw, 4.0f, 4.0f, true, true, "FFLogs top fourteen-minute burst"),
        new(856.4f, AID.Wildfire, 4.0f, 4.0f, true, true, "FFLogs top fourteen-minute Wildfire"),
        new(857.6f, AID.Hypercharge, 4.0f, 4.0f, true, true, "FFLogs top fourteen-minute Wildfire"),
        new(896.7f, AID.AutomatonQueen, 7.0f, 7.0f, true, true, "FFLogs top final-phase Queen"),
        new(896.8f, AID.AirAnchor, 5.0f, 6.0f, true, true, "FFLogs top final-phase tool loop"),
        new(916.7f, AID.ChainSaw, 4.0f, 4.0f, true, true, "FFLogs top final-phase tool loop"),
        new(918.4f, AID.Excavator, 4.0f, 8.0f, true, true, "FFLogs top final-phase follow-up"),

        new(964.9f, AID.BarrelStabilizer, 4.0f, 4.0f, true, true, "FFLogs top sixteen-minute burst"),
        new(967.0f, AID.FullMetalField, 5.0f, 6.0f, true, true, "FFLogs top sixteen-minute Full Metal Field"),
        new(976.7f, AID.ChainSaw, 4.0f, 4.0f, true, true, "FFLogs top sixteen-minute burst"),
        new(976.6f, AID.Wildfire, 4.0f, 4.0f, true, true, "FFLogs top sixteen-minute Wildfire"),
        new(977.9f, AID.Hypercharge, 4.0f, 4.0f, true, true, "FFLogs top sixteen-minute Wildfire"),
        new(988.4f, AID.AutomatonQueen, 7.0f, 7.0f, true, true, "FFLogs top final-phase Queen"),
        new(989.2f, AID.Excavator, 4.0f, 6.0f, true, true, "FFLogs top final-phase follow-up"),
        new(1036.9f, AID.ChainSaw, 4.0f, 4.0f, true, true, "FFLogs top final-phase tool loop"),
        new(1047.1f, AID.Excavator, 4.0f, 6.0f, true, true, "FFLogs top final-phase follow-up"),
        new(1085.0f, AID.BarrelStabilizer, 4.0f, 4.0f, true, true, "FFLogs top eighteen-minute burst"),
        new(1092.0f, AID.FullMetalField, 5.0f, 6.0f, true, true, "FFLogs top eighteen-minute Full Metal Field"),
        new(1096.5f, AID.AutomatonQueen, 7.0f, 7.0f, true, true, "FFLogs top eighteen-minute Queen"),
        new(1097.1f, AID.ChainSaw, 4.0f, 4.0f, true, true, "FFLogs top eighteen-minute burst"),
        new(1097.9f, AID.Wildfire, 4.0f, 4.0f, true, true, "FFLogs top eighteen-minute Wildfire"),
        new(1098.2f, AID.Hypercharge, 4.0f, 4.0f, true, true, "FFLogs top eighteen-minute Wildfire"),
        new(1098.9f, AID.Excavator, 4.0f, 8.0f, true, true, "FFLogs top eighteen-minute follow-up")
    ];

    private static readonly DancingMadMCHMitigationWindow[] DancingMadMitigationWindows =
    [
        new(28.4f, AID.Tactician, 0.0f, 2.5f, 3.0f, false, 0.5f, "FFLogs top mitigation option"),
        new(55.9f, AID.Dismantle, 0.0f, 3.0f, 6.0f, true, 0.5f, "FFLogs top boss mitigation option"),
        new(118.9f, AID.Tactician, 0.0f, 3.0f, 8.0f, false, 0.5f, "FFLogs top mitigation option"),
        new(223.1f, AID.Tactician, 0.0f, 3.0f, 10.0f, false, 0.5f, "FFLogs top mitigation option"),
        new(232.5f, AID.Dismantle, 0.0f, 4.0f, 4.0f, true, 0.5f, "FFLogs top boss mitigation option"),
        new(329.0f, AID.Tactician, 0.0f, 3.0f, 10.0f, false, 0.5f, "FFLogs top mitigation option"),
        new(368.6f, AID.Dismantle, 0.0f, 6.0f, 4.0f, true, 0.5f, "FFLogs top boss mitigation option"),
        new(512.3f, AID.Tactician, 0.0f, 4.0f, 4.0f, false, 0.5f, "FFLogs top mitigation option"),
        new(636.1f, AID.Dismantle, 0.0f, 4.0f, 4.0f, true, 0.5f, "FFLogs top boss mitigation option"),
        new(664.7f, AID.Tactician, 0.0f, 5.0f, 5.0f, false, 0.5f, "FFLogs top mitigation option"),
        new(763.4f, AID.Tactician, 0.0f, 5.0f, 14.0f, false, 0.5f, "FFLogs top mitigation option"),
        new(893.8f, AID.Tactician, 0.0f, 8.0f, 5.0f, false, 0.5f, "FFLogs top mitigation option"),
        new(902.6f, AID.Dismantle, 0.0f, 6.0f, 6.0f, true, 0.5f, "FFLogs top boss mitigation option"),
        new(985.4f, AID.Tactician, 0.0f, 7.0f, 5.0f, false, 0.5f, "FFLogs top mitigation option"),
        new(1062.9f, AID.Dismantle, 0.0f, 10.0f, 5.0f, true, 0.5f, "FFLogs top boss mitigation option"),
        new(1076.4f, AID.Tactician, 0.0f, 7.0f, 5.0f, false, 0.5f, "FFLogs top mitigation option")
    ];

    private bool IsDancingMadUltimate()
        => World.CurrentCFCID == DancingMadCFCID
        || Bossmods.ActiveModule?.Info?.GroupType == BossModuleInfo.GroupType.CFC && Bossmods.ActiveModule.Info.GroupID == DancingMadCFCID
        || Bossmods.ActiveModule?.GetType().FullName?.Contains(".Dawntrail.Ultimate.DMU.", StringComparison.Ordinal) == true
        || Bossmods.ActiveModule?.GetType().Name == "DMU";

    private float DancingMadTime() => CombatTimer;

    // evaluated once per Exec (the module-type name lookup is not free)
    private bool ShouldUseDancingMadProfile() => _dancingMadProfile;

    private bool IsInDancingMadActionWindow(AID action, float tolerance = 1.5f)
        => IsInDancingMadActionWindow(action, tolerance, tolerance);

    private bool IsInDancingMadActionWindow(AID action, float before, float after)
    {
        if (!ShouldUseDancingMadProfile())
            return false;

        var time = DancingMadTime();
        foreach (var window in DancingMadActionWindows)
            if (window.Action == action && time >= window.Time - Math.Max(before, window.Before) && time <= window.Time + Math.Max(after, window.After))
                return true;
        return false;
    }

    private bool ShouldHoldForDancingMadAction(AID action)
    {
        if (!ShouldUseDancingMadProfile())
            return false;

        if (ActionCapSoon(action))
            return false;

        var time = DancingMadTime();
        foreach (var window in DancingMadActionWindows)
            if (window.Action == action && window.HoldBefore && time >= window.Time - 10f && time < window.Time - window.Before)
                return true;
        return false;
    }

    private bool ActionCapSoon(AID action)
        => action switch
        {
            AID.AirAnchor => MaxChargesIn(AID.AirAnchor) <= GCD,
            AID.ChainSaw => MaxChargesIn(AID.ChainSaw) <= GCD,
            AID.Drill => MaxChargesIn(AID.Drill) <= GCD,
            AID.Bioblaster => MaxChargesIn(AID.Bioblaster) <= GCD,
            AID.Excavator => ExcavatorLeft > 0 && ExcavatorLeft <= GCD * 2,
            AID.FullMetalField => FullMetalFieldExpiringSoon(),
            AID.Hypercharge => Heat >= 100,
            AID.AutomatonQueen => Battery >= 100,
            _ => false
        };

    private void PushDancingMadForcedGCD(Enemy? primaryTarget)
    {
        if (!ShouldUseDancingMadProfile() || Overheated || !IsUsableToolTarget(primaryTarget))
            return;

        var time = DancingMadTime();
        DancingMadMCHActionWindow? selected = null;
        foreach (var window in DancingMadActionWindows)
        {
            if (!window.Force || time < window.Time - window.Before || time > window.Time + window.After)
                continue;

            var ready = window.Action switch
            {
                AID.Excavator => ExcavatorLeft > GCD,
                AID.FullMetalField => FMFLeft > GCD,
                AID.ChainSaw or AID.AirAnchor or AID.Drill => GCDReady(window.Action),
                _ => false
            };
            if (ready && (selected == null || window.Time < selected.Value.Time))
                selected = window;
        }

        if (selected is not { } forced)
            return;

        var target = forced.Action switch
        {
            AID.Excavator or AID.FullMetalField => BestRangedAOETarget,
            AID.ChainSaw => BestChainsawTarget,
            _ => primaryTarget
        };
        if (target != null)
            PushGCD(forced.Action, target, ToolPriorityAirAnchor + 5);
    }

    private bool ShouldUseDancingMadMitigation(AID action, Enemy? primaryTarget)
    {
        if (!ShouldUseDancingMadProfile())
            return false;

        if (action == AID.Tactician)
        {
            if (!CanWeave(AID.Tactician) || HasEquivalentPhysicalRangedMitigation())
                return false;
        }
        else if (action == AID.Dismantle)
        {
            if (!CanWeave(AID.Dismantle) || !IsBossTargetableForDismantle(primaryTarget))
                return false;
        }
        else
            return false;

        var time = DancingMadTime();
        foreach (var window in DancingMadMitigationWindows)
        {
            var useAt = window.Time - window.Lead;
            if (window.Action == action && time >= useAt - window.Before && time <= useAt + window.After)
                return !window.TargetRequired || primaryTarget != null;
        }

        return false;
    }

    private bool ShouldUseGenericMitigation(AID action, Enemy? primaryTarget)
    {
        if (ShouldUseDancingMadProfile() || !CanWeave(action))
            return false;

        if (action == AID.Tactician)
        {
            if (!Unlocked(AID.Tactician) || HasEquivalentPhysicalRangedMitigation())
                return false;
        }
        else if (action == AID.Dismantle)
        {
            if (!Unlocked(AID.Dismantle)
                || !IsBossTargetableForDismantle(primaryTarget)
                || primaryTarget!.Actor.FindStatus(SID.Dismantled) != null)
                return false;
        }
        else
            return false;

        var deadline = World.FutureTime(GenericMitigationLeadTime);
        foreach (var damage in Hints.PredictedDamage)
        {
            if (damage.Activation < World.CurrentTime || damage.Activation > deadline)
                continue;

            if (action == AID.Tactician && damage.Type is PredictedDamageType.Raidwide or PredictedDamageType.Shared)
                return true;
            if (action == AID.Dismantle && damage.Type is PredictedDamageType.Tankbuster or PredictedDamageType.Shared)
                return true;
        }

        return false;
    }

    private bool IsBossTargetableForDismantle(Enemy? primaryTarget)
        => primaryTarget is { Priority: >= 0 } && primaryTarget.Actor.IsTargetable;

    private bool HasEquivalentPhysicalRangedMitigation()
        => Player.FindStatus(SID.Tactician) != null
        || Player.FindStatus(BossMod.BRD.SID.Troubadour) != null
        || Player.FindStatus(BossMod.DNC.SID.ShieldSamba) != null;

    // Spread Shot / Scattergun / Auto Crossbow / Bioblaster: a 90-degree cone (replays: hits reach 45 degrees off the aim at the hitbox edge, misses start there)
    private PositionCheck IsConeAOETarget => (playerTarget, targetToTest) => TargetInAOECone(targetToTest, Player.Position, 12, Player.DirectionTo(playerTarget), 45f.Degrees());
}
