using BossMod.Data;
using BossMod.NIN;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.xan.Custom;

public sealed class NIN(RotationModuleManager manager, Actor player) : Attackxan<AID, TraitID, NIN.Strategy>(manager, player, PotionType.Dexterity)
{
    private enum MudraPushResult
    {
        None,
        MudraStep,
        FinalNinjutsu
    }

    private enum BurstNinjutsuPlan
    {
        None,
        KassatsuFirst,
        RaitonFirst
    }

    private enum BurstRecoveryState
    {
        None,
        Resyncing
    }

    public enum BurstStyle
    {
        [Option("Normal burst")]
        Normal,

        [Option("Ultimate 0s burst: Dokumori + Kunai together")]
        UltimateZeroSecond
    }

    public enum PotionStrategy
    {
        [Option("Do not use")]
        None,

        [Option("Use during even burst")]
        EvenBurst
    }

    public enum TrueNorthStrategy
    {
        [Option("Do not use")]
        None,

        [Option("Use automatically for positional GCDs")]
        Auto
    }

    public enum RotationStrategy
    {
        [Option("Normal")]
        Normal,

        [Option("Basic combo only")]
        BasicComboOnly
    }

    // Lower bounds for the opening burst: the pull's first Dokumori / Kunai's Bane are not pressed before this many of our GCDs
    // (weaponskills, ninjutsu and Ten Chi Jin ninjutsu; mudra and the countdown Suiton do not count). A variant may place Kunai's
    // Bane later than its bound (Raiton first puts two GCDs between it and Dokumori), never earlier.
    public enum OpenerDokumoriPosition
    {
        [Option("おすすめ: 2GCD後")]
        AfterSecondGCD,

        [Option("1GCD後")]
        AfterFirstGCD,

        [Option("3GCD後")]
        AfterThirdGCD,

        [Option("4GCD後")]
        AfterFourthGCD
    }

    public enum OpenerKunaiPosition
    {
        [Option("おすすめ: 3GCD後")]
        AfterThirdGCD,

        [Option("2GCD後")]
        AfterSecondGCD,

        [Option("4GCD後")]
        AfterFourthGCD,

        [Option("5GCD後")]
        AfterFifthGCD,

        [Option("6GCD後")]
        AfterSixthGCD
    }

    public struct Strategy : IStrategyCommon
    {
        public Track<Targeting> Targeting;
        public Track<AOEStrategy> AOE;

        [Track("Rotation", UiPriority = 501)]
        public Track<RotationStrategy> Rotation;

        [Track("Mug/Dokumori", Actions = [AID.Mug, AID.Dokumori])]
        public Track<OffensiveStrategy> Buffs;

        [Track("Burst style", Actions = [AID.Dokumori, AID.KunaisBane])]
        public Track<BurstStyle> BurstStyle;

        [Track("Potion", UiPriority = 58, Item = 1049234)]
        public Track<PotionStrategy> Potion;

        [Track("True North", MinLevel = 50, UiPriority = 48, Action = AID.TrueNorth)]
        public Track<TrueNorthStrategy> TrueNorth;

        [Track("Out of combat Hide")]
        public Track<EnabledByDefault> Hide;

        [Track("Forked Raiju", MinLevel = 90, Action = AID.ForkedRaiju)]
        public Track<EnabledByDefault> ForkedRaiju;

        // a filler for the GCDs spent out of melee range (a forced disengage, a target that cannot be reached); it does not break the combo
        [Track("Ranged attack: Throwing Dagger", InternalName = "ThrowingDagger", MinLevel = 15, Action = AID.ThrowingDagger)]
        public Track<EnabledByDefault> ThrowingDagger;

        [Track("Phantom Cannoneer: Use cannons on cooldown", InternalName = "PCAN", UiPriority = -10, MinLevel = 100, Targets = ActionTargets.Hostile, Actions = [PhantomID.PhantomFire, PhantomID.HolyCannon, PhantomID.DarkCannon, PhantomID.ShockCannon, PhantomID.SilverCannon])]
        public Track<EnabledByDefault> PhantomCannon;

        [Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)]
        public Track<MechanicHintStrategy> MechanicHints;

        // the opener positions only apply once Dokumori is learned (OpenerActive), so the tracks start there
        [Track("開幕 毒盛位置", InternalName = "OpenerDokumoriGCD", UiPriority = 57, MinLevel = 66, Actions = [AID.Mug, AID.Dokumori])]
        public Track<OpenerDokumoriPosition> OpenerDokumori;

        [Track("開幕 百雷銃位置", InternalName = "OpenerKunaiGCD", UiPriority = 56, MinLevel = 66, Actions = [AID.TrickAttack, AID.KunaisBane])]
        public Track<OpenerKunaiPosition> OpenerKunai;

        [Track("先雷遁/先活殺", InternalName = "BurstVariant", UiPriority = 55, MinLevel = 76, Actions = [AID.Kassatsu, AID.Raiton])]
        public Track<NinBurstMode> BurstVariant;

        readonly Targeting IStrategyCommon.Targeting => Targeting.Value;
        readonly AOEStrategy IStrategyCommon.AOE => AOE.Value;
    }

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("xan NIN [Custom]", "Ninja", "Standard rotation (xan)|Melee", "xan", RotationModuleQuality.Basic, BitMask.Build(Class.ROG, Class.NIN), 100).WithStrategies<Strategy>();
    }

    public int Ninki;
    public int Kazematoi;
    public (float Left, int Param) Mudra;
    public (float Left, int Param) TenChiJin;
    public bool HiddenStatus; // no max, ends when combat starts
    public float ShadowWalker; // max 20
    public float Kassatsu; // max 15
    public float PhantomKamaitachi; // max 45
    public float Meisui; // max 30
    public float Higi; // max 30
    public (float Left, int Stacks) Raiju;
    public float TenriJindo;

    public float TargetTrickLeft; // effective window left: 16.29 from the Kunai's Bane press
    public float TargetMugLeft; // effective window left: 21.07 from the Dokumori press

    public int NumAOETargets;
    public int NumRangedAOETargets;

    // 25y for hellfrog - ninjutsu have a range of 20y
    private Enemy? BestRangedAOETarget;
    private Enemy? BestNinkiAOETarget;
    private int NumNinkiAOETargets;
    private AID _pendingNinjutsu = AID.None;
    private bool _dokumoriPreKunaiActive;
    private int _dokumoriPreKunaiGCDs;
    private bool _dokumoriPreKunaiUsePhantom;
    private bool _dokumoriPreKunaiRaitonDone;
    private bool _dokumoriPreKunaiPhantomDone;
    private bool _dokumoriPreKunaiComboDone;
    private AID _dokumoriPreKunaiPendingNinjutsu = AID.None;
    private bool _dokumoriPreKunaiNinjutsuStarted;
    private AID _dokumoriPreKunaiPendingCombo = AID.None;
    private AID _dokumoriPreKunaiComboStateAtPush = AID.None;
    private bool _dokumoriPreKunaiPhantomPending;
    private BurstRecoveryState _burstRecoveryState;
    private DateTime _lastExecAt = DateTime.MinValue;
    private bool _hadTargetLastFrame;
    private bool _burstActiveLastFrame;
    private bool _wasInCombatLastFrame;
    private bool _basicComboOnlyLastFrame;
    private ulong _lastPrimaryTargetInstanceID;
    private bool _shukihoUnlocked;

    private const float MugDokumoriDebuffDuration = 20f;
    private const float TrickKunaiDebuffDuration = 15f;
    private const float KunaiCooldown = 60f;
    private const float MugCooldown = 120f;
    // An odd Kunai's Bane is skipped only when using it would put the next even one this far past its aligned time. A late even window
    // costs a few seconds of Dokumori overlap; a skipped one costs a whole window (harness: 2s skipped 743 windows, -0.7% rDPS; 5-20s
    // skipped none).
    private const float KunaiMugSyncTolerance = 10.0f;
    private const float BurstResyncTolerance = 10.0f;
    // The target debuffs show at the press and are re-applied when the server confirms them (Kunai's Bane +1.29s, Trick Attack +0.80s,
    // Dokumori +1.07s, Mug +0.54s - replay measurements and BossMod's application delays), so they cover 16.29s / 21.07s from the press
    // and damage checks them at the press. Kunai's Bane is placed so that its window ends with Dokumori's.
    private const float KunaiStartMugLeftForEndAlign = NinBurstTiming.KunaiEffective;
    private const float KunaiEndAlignTolerance = 0.25f;
    private const float TrueNorthLateWeaveWindow = 0.75f;
    private const float TrueNorthEmergencyWindow = 0.25f;
    private const float RaidBuffDamageMultiplier = 1.05f;
    private const float PotionDamageMultiplier = 1.06f;
    // Kassatsu first presses Kassatsu no earlier than this before Kunai's Bane, so Hyosho Ranryu (held for the window) stays inside
    // Kassatsu's 15s; a Kassatsu with no Kunai's Bane this close is held for it unless the next window is further than its recast allows
    private const float KassatsuLeadBeforeKunai = 10f;
    private const float KassatsuHoldForKunaiLimit = 45f;
    // Dream Within a Dream (60s, like Kunai's Bane) waits for the window when it opens within this long
    private const float DreamHoldForKunaiLimit = 20f;
    private const float TenChiJinSequenceTime = 3.5f;
    private const float OpenerWindow = 30f;
    private const float MudraChargeRecast = 20f;
    private const float MaxAllowedMudraCapHold = 4.0f;
    private const float DefaultAllowedMudraCapHold = 2.0f;
    private const float MudraHoldDecisionMargin = 25f;
    private const float RotationResumeGap = 0.75f;
    private const float SixtySecondRecoveryWindow = 55f;
    private const float BunshinRecoveryWindow = 85f;
    private const float MudraRecoveryReserveWindow = 20f;

    private DateTime _mudraCapStartedAt = DateTime.MinValue;
    private bool _kassatsuQueuedThisFrame;

    // ---- player action lock (ActionLocks, shared with the other modules; Basexan.Locks) ----
    // Stun-type statuses refuse every action, Pacification (6) the weaponskills, Silence (7) the spells, Amnesia (1092) the abilities - for
    // a ninja that is every mudra, every ninjutsu and Ten Chi Jin. A refused pick still costs the frame and is retried next frame, so a
    // mudra queued under Amnesia idled the GCD a weaponskill could have used (irregular harness: Ten refused 101 frames, GCD idle 51 frames
    // in one 7.6 s Amnesia). Every push goes through here: an action the client would refuse is not queued at all, so the frame goes to
    // one it accepts (the weaponskills under Amnesia, the ninjutsu under Pacification) instead of being spent on the refusal.
    protected override bool CanUse(AID action) => !IsActionLocked(action);

    // opening burst bounds: our GCDs since the pull, counted from the cast events the client reports
    private int _openerGCDs;
    private uint _openerLastSequence;
    private bool _openerDokumoriUsed;
    private bool _openerKunaiUsed;
    private int _openerDokumoriMin;
    private int _openerKunaiMin;

    // burst variant: re-evaluated every frame of the decision window, fixed once its first committing press is made
    private NinBurstVariant _tentativeVariant;
    private NinBurstVariant _lockedVariant;
    private NinBurstEvaluation _lastBurstEvaluation;
    private NinBurstContext _lastBurstContext;
    // the last evaluation made before any committing press changed the state it describes
    private NinBurstEvaluation _decisionEvaluation;
    private NinBurstContext _decisionContext;

    // read-only view of the burst decision (state description, harness tuning)
    public NinBurstEvaluation LastBurstEvaluation => _lastBurstEvaluation;
    public NinBurstContext LastBurstContext => _lastBurstContext;
    public NinBurstEvaluation DecisionEvaluation => _decisionEvaluation;
    public NinBurstContext DecisionContext => _decisionContext;
    public NinBurstVariant LockedBurstVariant => _lockedVariant;

    public override string DescribeState()
        => FormattableString.Invariant($"burst={_lockedVariant}/{_tentativeVariant} rf={_lastBurstEvaluation.RaitonFirstValue:f0} kf={_lastBurstEvaluation.KassatsuFirstValue:f0} ({_lastBurstEvaluation.Reason}) opener={_openerGCDs} rfpre={(_dokumoriPreKunaiActive ? _dokumoriPreKunaiGCDs : -1)}");
    private readonly NinBurstPlanner _burstPlanner = new();
    private readonly NinBurstRules _burstRules = new();
    private static readonly NinFixedSelector RaitonFirstSelector = new(NinBurstVariant.RaitonFirst);
    private static readonly NinFixedSelector KassatsuFirstSelector = new(NinBurstVariant.KassatsuFirst);

    // these aren't the same cdgroup :(
    public float AssassinateCD => ReadyIn(Unlocked(AID.DreamWithinADream) ? AID.DreamWithinADream : AID.Assassinate);

    private readonly record struct MudraSteps(int First, int Second, int Third)
    {
        public int Length => 3;
        public int this[int index] => index switch
        {
            0 => First,
            1 => Second,
            2 => Third,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };
    }

    // decoded on demand without allocating (used by list patterns below and by PickMudra)
    private MudraSteps Mudras => new(Mudra.Param & 3, (Mudra.Param >> 2) & 3, (Mudra.Param >> 4) & 3);

    private static readonly Dictionary<AID, (int Len, int Last)> Combos = new()
    {
        [AID.FumaShuriken] = (1, 0),
        [AID.Katon] = (2, 1),
        [AID.GokaMekkyaku] = (2, 1),
        [AID.Raiton] = (2, 2),
        [AID.Hyoton] = (2, 3),
        [AID.HyoshoRanryu] = (2, 3),
        [AID.Huton] = (3, 1),
        [AID.Doton] = (3, 2),
        [AID.Suiton] = (3, 3)
    };

    private AID CurrentNinjutsu => Mudras switch
    {
        [1 or 2 or 3, 0, 0] => AID.FumaShuriken,
        [_, 1, 0] => KassatsuActive && Unlocked(AID.GokaMekkyaku) ? AID.GokaMekkyaku : AID.Katon,
        [_, 2, 0] => AID.Raiton,
        [_, 3, 0] => KassatsuActive && Unlocked(AID.HyoshoRanryu) ? AID.HyoshoRanryu : AID.Hyoton,
        [_, _, 1] => AID.Huton,
        [_, _, 2] => AID.Doton,
        [_, _, 3] => AID.Suiton,
        _ => AID.Ninjutsu
    };

    private bool Hidden => HiddenStatus || ShadowWalker > AnimLock;

    private bool CanTrickInCombat => Unlocked(AID.Suiton);
    // counted from the mudra charge group the client reports (the same recast data the game's charge counter reads), so the
    // value is identical in game and outside it, where ActionManager.Instance() throws instead of returning null
    // Both mudra values read the cooldown table through an action definition lookup and are consulted dozens of times a frame;
    // the cooldowns only change between frames, so they are computed once at the top of Exec.
    private int MudraCharges => _mudraCharges;
    private float MudraNextChargeIn => _mudraNextChargeIn;
    private int _mudraCharges;
    private float _mudraNextChargeIn;
    // per-frame splash counts shared by the 20y and 25y target selections (same shape, so the same count per candidate)
    private readonly Dictionary<Actor, int> _splashCounts = [];

    private int ComputeMudraCharges
    {
        get
        {
            if (!Unlocked(AID.Ten1))
                return 0;

            var def = ActionDefinitions.Instance.Spell(AID.Ten1)!;
            var max = def.MaxChargesAtLevel(Player.Level);
            var cd = World.Client.Cooldowns[def.ActualMainCooldownGroup(World.Client.DutyActions)];
            if (cd.Total <= 0 || def.Cooldown <= 0)
                return max;
            return Math.Clamp((int)MathF.Floor(cd.Elapsed / def.Cooldown + 0.001f), 0, max);
        }
    }

    // seconds until the next mudra charge comes back; 0 when the charges are capped
    private float ComputeMudraNextChargeIn
    {
        get
        {
            if (!Unlocked(AID.Ten1) || MudraChargesCapped)
                return 0;

            var def = ActionDefinitions.Instance.Spell(AID.Ten1)!;
            var cd = World.Client.Cooldowns[def.ActualMainCooldownGroup(World.Client.DutyActions)];
            return cd.Total <= 0 || def.Cooldown <= 0 ? 0 : def.Cooldown - cd.Elapsed % def.Cooldown;
        }
    }
    private bool HasMudraCharge => MudraCharges > 0;
    private bool MudraChargesCapped => MudraCharges >= 2;

    private AID MugAction => Unlocked(AID.Dokumori) ? AID.Dokumori : AID.Mug;
    private AID TrickAction => Unlocked(AID.KunaisBane) ? AID.KunaisBane : AID.TrickAttack;
    private AID BhavacakraAction => Higi > 0 && Unlocked(AID.ZeshoMeppo) ? AID.ZeshoMeppo : AID.Bhavacakra;
    private AID HellfrogAction => Higi > 0 && Unlocked(AID.DeathfrogMedium) ? AID.DeathfrogMedium : AID.HellfrogMedium;
    private bool KassatsuActive => Kassatsu > 0;
    private bool KassatsuActiveOrQueued => KassatsuActive || _kassatsuQueuedThisFrame;
    private bool UseUltimateZeroSecondBurst(in Strategy strategy) => strategy.BurstStyle.Value == BurstStyle.UltimateZeroSecond;
    private bool UseBasicComboOnly(in Strategy strategy) => strategy.Rotation.Value == RotationStrategy.BasicComboOnly;
    private bool BurstRecoveryActive => _burstRecoveryState == BurstRecoveryState.Resyncing;

    private bool ShouldHoldCooldownForEvenBurstRecovery(float recoveryWindow)
    {
        if (!BurstRecoveryActive || TargetMugLeft > GCD)
            return false;

        var mugIn = ReadyIn(MugAction);
        return mugIn > GCD + AnimLock && mugIn < recoveryWindow;
    }

    private bool KunaiWouldBeReadyForNextMugIfUsedNow()
        => KunaiWouldBeReadyForNextMugIfUsedIn(0);

    private bool KunaiWouldBeReadyForNextMugIfUsedIn(float useIn)
    {
        var mugIn = ReadyIn(MugAction);

        if (mugIn <= useIn + GCD + AnimLock)
            return true;

        // kunai used at useIn is back at useIn + 60s; it only has to be back by the end-aligned start of the next window (mug + 5s)
        return mugIn - useIn >= KunaiCooldown - NinBurstTiming.KunaiAfterDokumori - KunaiMugSyncTolerance;
    }

    private bool ShouldSkipOddKunaiForMugSync()
    {
        if (TargetMugLeft > 0)
            return false;

        if (TargetTrickLeft > 0)
            return false;

        if (ShouldHoldCooldownForEvenBurstRecovery(SixtySecondRecoveryWindow))
            return true;

        if (ReadyIn(TrickAction) > GCD + AnimLock)
            return false;

        if (ReadyIn(MugAction) <= GCD + AnimLock)
            return false;

        return !KunaiWouldBeReadyForNextMugIfUsedNow();
    }

    private void UpdateBurstRecoveryState(in Strategy strategy, Enemy? primaryTarget)
    {
        var now = World.CurrentTime;
        var hasTarget = primaryTarget != null;
        var basicComboOnly = UseBasicComboOnly(strategy);
        var targetInstanceID = hasTarget ? (ulong)primaryTarget!.Actor.InstanceID : 0;
        var rotationResumed = _lastExecAt == DateTime.MinValue || (now - _lastExecAt).TotalSeconds >= RotationResumeGap;
        var rotationModeResumed = _basicComboOnlyLastFrame && !basicComboOnly;
        var combatResumedWithCooldowns = !_wasInCombatLastFrame && Player.InCombat
            && (ReadyIn(MugAction) > GCD + AnimLock || ReadyIn(TrickAction) > GCD + AnimLock);
        var burstWasActiveOrJustStarted = _burstActiveLastFrame
            || ReadyIn(MugAction) > KunaiCooldown * 2 - MugDokumoriDebuffDuration
            || ReadyIn(TrickAction) > KunaiCooldown - TrickKunaiDebuffDuration;
        var targetLostDuringBurst = _hadTargetLastFrame && !hasTarget && burstWasActiveOrJustStarted;
        var targetChangedDuringBurst = _hadTargetLastFrame && hasTarget && _lastPrimaryTargetInstanceID != 0
            && targetInstanceID != _lastPrimaryTargetInstanceID && burstWasActiveOrJustStarted;

        if (targetLostDuringBurst || targetChangedDuringBurst)
            ResetRaitonFirstPreKunaiState();

        if (!Player.InCombat)
        {
            // recovery is only meaningful while the current pull is running; the next pull re-measures the cooldowns
            _burstRecoveryState = BurstRecoveryState.None;
        }
        else if (!BurstRecoveryActive
            && (targetLostDuringBurst || targetChangedDuringBurst || rotationResumed || rotationModeResumed || combatResumedWithCooldowns)
            && IsEvenBurstMisaligned(strategy))
        {
            // these events are all benign on their own (dungeon pulls, brief exec gaps, target swaps); only enter
            // recovery when the 60s anchors are actually measured to be out of phase with the 120s anchor
            _burstRecoveryState = BurstRecoveryState.Resyncing;
            ResetRaitonFirstPreKunaiState();
        }

        // Once the next Dokumori/Mug and Kunai/Trick window is actually established,
        // the live cooldown/gauge snapshot is synchronized again.
        if (BurstRecoveryActive && TargetMugLeft > GCD && (TargetTrickLeft > GCD || !CanTrickInCombat))
            _burstRecoveryState = BurstRecoveryState.None;

        _lastExecAt = now;
        _hadTargetLastFrame = hasTarget;
        _lastPrimaryTargetInstanceID = targetInstanceID;
        _burstActiveLastFrame = TargetMugLeft > 0 || TargetTrickLeft > 0;
        _wasInCombatLastFrame = Player.InCombat;
        _basicComboOnlyLastFrame = basicComboOnly;
    }

    // Kunai/Trick and Kassatsu (60s) are expected to come back in phase with Mug/Dokumori (120s): together with it for the
    // ultimate 0s style, ~5s after it (end-aligned kunai) for the normal style. A phase error beyond the tolerance means the
    // odd/even structure was actually broken (death, long downtime, manual usage), which is what recovery is for.
    private bool IsEvenBurstMisaligned(in Strategy strategy)
    {
        if (!Unlocked(MugAction))
            return false;

        var expectedPhase = ReadyIn(MugAction) + (UseUltimateZeroSecondBurst(strategy) ? 0 : NinBurstTiming.KunaiAfterDokumori);

        if (CanTrickInCombat && CooldownPhaseDistance(ReadyIn(TrickAction), expectedPhase) > BurstResyncTolerance)
            return true;

        return Unlocked(AID.Kassatsu) && CooldownPhaseDistance(ReadyIn(AID.Kassatsu), expectedPhase) > BurstResyncTolerance;
    }

    private static float CooldownPhaseDistance(float readyIn, float expectedReadyIn)
    {
        var distance = MathF.Abs(readyIn % KunaiCooldown - expectedReadyIn % KunaiCooldown);
        return MathF.Min(distance, KunaiCooldown - distance);
    }

    // The status the client shows restarts its timer when the server confirms it; until then it under-reports the window by the
    // confirmation delay. Seconds since the press come from the action's own cooldown.
    private float EffectiveWindowLeft(float statusLeft, AID action, float cooldown, float confirmDelay, float duration)
    {
        if (statusLeft <= 0)
            return 0;
        // A pending status (predicted, not yet confirmed by the server) reads as 1000s. Within the confirmation delay the branch
        // below already bounds it; when the confirmation comes later than that, no real window lasts longer than its duration.
        statusLeft = MathF.Min(statusLeft, duration);
        var readyIn = ReadyIn(action);
        var sincePress = readyIn > 0 && readyIn < cooldown ? cooldown - readyIn : float.MaxValue;
        return sincePress < confirmDelay ? MathF.Min(duration + confirmDelay - sincePress, statusLeft + confirmDelay) : statusLeft;
    }

    private static int OpenerMinimum(OpenerDokumoriPosition position) => position switch
    {
        OpenerDokumoriPosition.AfterFirstGCD => 1,
        OpenerDokumoriPosition.AfterThirdGCD => 3,
        OpenerDokumoriPosition.AfterFourthGCD => 4,
        _ => 2
    };

    private static int OpenerMinimum(OpenerKunaiPosition position) => position switch
    {
        OpenerKunaiPosition.AfterSecondGCD => 2,
        OpenerKunaiPosition.AfterFourthGCD => 4,
        OpenerKunaiPosition.AfterFifthGCD => 5,
        OpenerKunaiPosition.AfterSixthGCD => 6,
        _ => 3
    };

    private static bool IsOpenerGCD(AID aid) => aid is AID.SpinningEdge or AID.GustSlash or AID.AeolianEdge or AID.ArmorCrush or AID.DeathBlossom or AID.HakkeMujinsatsu
        or AID.ThrowingDagger or AID.PhantomKamaitachi or AID.FleetingRaiju or AID.ForkedRaiju
        or AID.FumaShuriken or AID.Katon or AID.Raiton or AID.Hyoton or AID.Huton or AID.Doton or AID.Suiton or AID.HyoshoRanryu or AID.GokaMekkyaku or AID.RabbitMedium
        or AID.FumaTen or AID.FumaChi or AID.FumaJin or AID.TCJKaton or AID.TCJRaiton or AID.TCJHyoton or AID.TCJHuton or AID.TCJDoton or AID.TCJSuiton;

    // Counts our GCDs since the pull from the cast events, so the opening Dokumori / Kunai's Bane bounds mean the same in game and in the
    // harness. The countdown Suiton lands before the pull and is not counted.
    private void UpdateOpenerTracking(in Strategy strategy)
    {
        var cast = Manager.LastCast;
        if (!Player.InCombat)
        {
            _openerGCDs = 0;
            _openerDokumoriUsed = false;
            _openerKunaiUsed = false;
            _openerLastSequence = cast.Data?.SourceSequence ?? 0;
            // a lock left over from a pull that ended mid-window would otherwise force the next opener's variant
            ResetBurstVariant();
            return;
        }

        _openerDokumoriMin = OpenerMinimum(strategy.OpenerDokumori.Value);
        _openerKunaiMin = Math.Max(OpenerMinimum(strategy.OpenerKunai.Value), _openerDokumoriMin + 1);
        if (cast.Data is { } data && data.SourceSequence != _openerLastSequence)
        {
            _openerLastSequence = data.SourceSequence;
            if (data.Action.Type == ActionType.Spell && cast.Time >= Manager.CombatStart)
            {
                var aid = (AID)data.Action.ID;
                if (IsOpenerGCD(aid))
                    ++_openerGCDs;
                else if (aid is AID.Dokumori or AID.Mug)
                    _openerDokumoriUsed = true;
                else if (aid is AID.KunaisBane or AID.TrickAttack)
                    _openerKunaiUsed = true;
            }
        }
    }

    // the bounds are about Dokumori's gauge and the burst that follows it; before Dokumori, Mug and Trick Attack go on cooldown
    private bool OpenerActive => Player.InCombat && CombatTimer < OpenerWindow && Unlocked(AID.Dokumori) && !UseUltimateZeroSecondBurstCached;
    private bool OpenerDokumoriHeld => OpenerActive && !_openerDokumoriUsed && _openerGCDs < _openerDokumoriMin;
    private bool OpenerKunaiHeld => OpenerActive && !_openerKunaiUsed && _openerGCDs < _openerKunaiMin;
    private bool UseUltimateZeroSecondBurstCached;

    // Dokumori belongs to the coming Kunai's Bane window when it is already up or comes back before that window would open.
    private bool EvenBurstComing(float kunaiIn)
        => Unlocked(MugAction) && (TargetMugLeft > 0 || ReadyIn(MugAction) <= kunaiIn + GCDLength);

    // The window in which the variant is decided: from a little before the Kunai's Bane window (Dokumori's lead included) until the
    // first press that commits to one.
    private bool InBurstDecisionWindow(in Strategy strategy, float kunaiIn)
        => CanTrickInCombat && Unlocked(AID.Kassatsu) && TargetTrickLeft <= 0 && kunaiIn <= KassatsuLeadBeforeKunai + GCDLength;

    // FightRemaining (value-of-information experiment): the fight's end as the shared estimate reports it (AIHints.FightRemaining).
    // With a perfect estimate the end dump below gains +2.6% rDPS (real: no credit for unspent resources) over the combat matrix, +0.3-1.7% on
    // fights of 80 s and longer and +12-16% on fights of 20-80 s, and keeps +2.1% with the no-prior estimator's error (harness XAN_HARNESS_TTK).
    // The upper bound decides "the fight ends before X" (a resource would be wasted), the lower bound "there is time for X".
    // Unknown: false everywhere, i.e. the current behaviour.
    private const float FightEndDumpHorizon = 45f;
    private bool FightEndsWithin(float seconds) => Hints.FightRemaining is { Known: true } fr && fr.UpperBound <= seconds;
    private bool EndDump => FightEndsWithin(FightEndDumpHorizon);
    private bool FightHasAtLeast(float seconds) => Hints.FightRemaining is { Known: true } fr && fr.LowerBound >= seconds;

    private NinBurstVariant ResolveBurstVariant(in Strategy strategy, bool useNinjutsuAOE, bool useKassatsuNinjutsuAOE, Enemy? primaryTarget)
    {
        if (UseUltimateZeroSecondBurst(strategy))
            return NinBurstVariant.KassatsuFirst;
        if (!CanTrickInCombat || !Unlocked(AID.Raiton) || !Unlocked(AID.Kassatsu) || !Unlocked(AID.HyoshoRanryu))
        {
            _tentativeVariant = _lockedVariant = NinBurstVariant.None;
            return NinBurstVariant.None;
        }
        // no target: nothing to decide this frame, but a lock already taken stays (an add appearing or a brief loss must not re-decide)
        if (primaryTarget == null)
            return _lockedVariant;

        // the lock lasts until the Kunai's Bane window it was taken for is over
        if (_lockedVariant != NinBurstVariant.None)
        {
            var kunaiSince = ReadyIn(TrickAction) is var r && r > 0 ? KunaiCooldown - r : float.MaxValue;
            if (TargetTrickLeft <= 0 && kunaiSince > NinBurstTiming.KunaiEffective && ExpectedTrickActionIn(strategy, useNinjutsuAOE) > KassatsuLeadBeforeKunai + GCDLength)
                _lockedVariant = NinBurstVariant.None;
            else
                return _lockedVariant;
        }

        // a committing press since the last frame fixes the variant that was being followed before anything is re-evaluated: Kunai's
        // Bane (the decision window closes with it), or Kassatsu for Kassatsu first
        if (_tentativeVariant != NinBurstVariant.None && (TargetTrickLeft > 0 || _tentativeVariant == NinBurstVariant.KassatsuFirst && KassatsuActive))
        {
            _lockedVariant = _tentativeVariant;
            return _lockedVariant;
        }

        var kunaiIn = ExpectedTrickActionIn(strategy, useNinjutsuAOE);
        if (!InBurstDecisionWindow(strategy, kunaiIn))
        {
            _tentativeVariant = NinBurstVariant.None;
            return NinBurstVariant.None;
        }

        // a mudra sequence under way is not re-decided (neither variant can start one then): the tentative variant is followed, and
        // Raiton first's pre-Kunai mudra commits it
        if (Mudra.Left > 0 && _tentativeVariant != NinBurstVariant.None)
            return _tentativeVariant;

        var ctx = BuildBurstContext(strategy, kunaiIn);
        _lastBurstContext = ctx;
        INinBurstSelector selector = strategy.BurstVariant.Value switch
        {
            NinBurstMode.Planner => _burstPlanner,
            NinBurstMode.RaitonFirst => RaitonFirstSelector,
            NinBurstMode.KassatsuFirst => KassatsuFirstSelector,
            _ => _burstRules
        };
        _lastBurstEvaluation = selector.Select(ctx, _tentativeVariant);
        _tentativeVariant = _lastBurstEvaluation.Choice;
        if (!KassatsuActive && Mudra.Left <= 0 && TargetTrickLeft <= 0)
        {
            _decisionContext = ctx;
            _decisionEvaluation = _lastBurstEvaluation;
        }
        return _tentativeVariant;
    }

    // Fixes the tentative variant once a press commits to it: Raiton first's pre-Kunai Raiton mudra, Kassatsu first's Kassatsu before
    // Kunai's Bane, or Kunai's Bane itself.
    private void UpdateBurstLock()
    {
        if (_lockedVariant != NinBurstVariant.None || _tentativeVariant == NinBurstVariant.None)
            return;
        if (TargetTrickLeft > 0
            || _tentativeVariant == NinBurstVariant.RaitonFirst && _dokumoriPreKunaiActive && (Mudra.Left > 0 || _dokumoriPreKunaiGCDs > 0 || _dokumoriPreKunaiPendingCombo != AID.None || _dokumoriPreKunaiPhantomPending)
            || _tentativeVariant == NinBurstVariant.KassatsuFirst && KassatsuActive)
            _lockedVariant = _tentativeVariant;
    }

    private void ResetBurstVariant()
    {
        _tentativeVariant = NinBurstVariant.None;
        _lockedVariant = NinBurstVariant.None;
    }

    private NinBurstContext BuildBurstContext(in Strategy strategy, float kunaiIn)
    {
        var mugReadyIn = ReadyIn(MugAction);
        var kunaiReadyIn = ReadyIn(TrickAction);
        var comboStep = ComboLastMove == AID.SpinningEdge ? 1 : ComboLastMove == AID.GustSlash ? 2 : 0;
        var hintsOn = strategy.MechanicHints.Value != MechanicHintStrategy.Off;
        return new()
        {
            Level = Player.Level,
            GCD = GCD,
            GcdLength = GCDLength,
            AnimLock = AnimLock,
            WeaveLock = 0.6f + AnimationLockDelay,
            MudraCharges = MudraCharges,
            MudraMax = Unlocked(AID.Ten1) ? ActionDefinitions.Instance.Spell(AID.Ten1)!.MaxChargesAtLevel(Player.Level) : 0,
            MudraNextCharge = MudraNextChargeIn,
            MudraInProgress = Mudra.Left > 0,
            KassatsuLeft = Kassatsu,
            KassatsuReadyIn = ReadyIn(AID.Kassatsu),
            TenChiJinReadyIn = ReadyIn(AID.TenChiJin),
            DreamReadyIn = AssassinateCD,
            MeisuiReadyIn = ReadyIn(AID.Meisui),
            BunshinReadyIn = ReadyIn(AID.Bunshin),
            PhantomLeft = PhantomKamaitachi,
            RaijuStacks = Raiju.Stacks,
            Ninki = Ninki,
            Kazematoi = Kazematoi,
            ComboStep = comboStep,
            BunshinStacks = StatusStacks(SID.Bunshin),
            HigiLeft = Higi,
            MeisuiLeft = Meisui,
            TenriLeft = TenriJindo,
            ShadowWalkerLeft = HiddenStatus ? float.MaxValue : ShadowWalker,
            // only the window running on the target counts; the cooldown alone would also show the previous burst's press
            KunaiSincePress = TargetTrickLeft > 0 && kunaiReadyIn > 0 && kunaiReadyIn < KunaiCooldown ? KunaiCooldown - kunaiReadyIn : float.MaxValue,
            KunaiReadyIn = kunaiReadyIn,
            DokumoriSincePress = TargetMugLeft > 0 && mugReadyIn > 0 && mugReadyIn < MugCooldown ? MugCooldown - mugReadyIn : float.MaxValue,
            DokumoriReadyIn = mugReadyIn,
            EvenBurst = EvenBurstComing(kunaiIn),
            GCDsBeforeDokumori = OpenerDokumoriHeld ? _openerDokumoriMin - _openerGCDs : 0,
            GCDsBeforeKunai = OpenerKunaiHeld ? _openerKunaiMin - _openerGCDs : 0,
            PotionLeft = PotionLeft,
            PartyBuffLeft = RaidBuffsLeft,
            PartyBuffIn = RaidBuffsIn,
            // only a loss still ahead with a known return: a "downtime now" while the target is up is a stale state-machine estimate
            TargetLossIn = hintsOn && Mechanic.ReturnKnown && Mechanic.TargetLossIn > 0 ? Mechanic.TargetLossIn : float.MaxValue,
            TargetReturnIn = hintsOn && Mechanic.ReturnKnown && Mechanic.TargetLossIn > 0 ? Mechanic.TargetReturnIn : float.MaxValue,
            // a forecast provider has to be present: without one "no loss known" says nothing about the window (a native state machine
            // that knows the coming seconds is one, as is a published snapshot)
            HintsEnabled = hintsOn && (Mechanic.HasSnapshot || Mechanic.HasEncounter || Mechanic.StateForecast),
            HyoshoUnlocked = Unlocked(AID.HyoshoRanryu),
            RaijuUnlocked = Unlocked(TraitID.EnhancedRaiton),
            TenChiJinUnlocked = Unlocked(AID.TenChiJin),
            TenriUnlocked = Unlocked(TraitID.EnhancedTenChiJin),
            ZeshoUnlocked = Unlocked(AID.ZeshoMeppo),
            PhantomUnlocked = Unlocked(AID.PhantomKamaitachi),
            MeisuiUnlocked = Unlocked(AID.Meisui),
            DreamUnlocked = Unlocked(AID.DreamWithinADream),
            ShukihoUnlocked = _shukihoUnlocked
        };
    }

    private static readonly uint[] NoUnhideZones = [
        452
    ];

    public override void Exec(in Strategy strategy, Enemy? primaryTarget)
    {
        UpdateMechanicForecast(strategy.MechanicHints.Value);
        SelectPrimaryTarget(strategy, ref primaryTarget, range: 3);
        UseUltimateZeroSecondBurstCached = UseUltimateZeroSecondBurst(strategy);
        _mudraCharges = ComputeMudraCharges;
        _mudraNextChargeIn = ComputeMudraNextChargeIn;

        var gauge = World.Client.GetGauge<NinjaGauge>();
        Ninki = gauge.Ninki;
        Kazematoi = gauge.Kazematoi;

        Mudra = Status(SID.Mudra);
        ShadowWalker = StatusLeft(SID.ShadowWalker);
        Kassatsu = StatusLeft(SID.Kassatsu);
        PhantomKamaitachi = StatusLeft(SID.PhantomKamaitachiReady);
        HiddenStatus = StatusStacks(SID.Hidden) > 0;
        TargetTrickLeft = EffectiveWindowLeft(Math.Max(
            StatusDetails(primaryTarget, SID.TrickAttack, Player.InstanceID).Left,
            StatusDetails(primaryTarget, SID.KunaisBane, Player.InstanceID).Left
        ), TrickAction, KunaiCooldown, Unlocked(AID.KunaisBane) ? NinBurstTiming.KunaiEffective - TrickKunaiDebuffDuration : 0.80f, TrickKunaiDebuffDuration);
        TargetMugLeft = EffectiveWindowLeft(Math.Max(
            StatusDetails(primaryTarget, SID.VulnerabilityUp, Player.InstanceID).Left,
            StatusDetails(primaryTarget, SID.Dokumori, Player.InstanceID).Left
        ), MugAction, MugCooldown, Unlocked(AID.Dokumori) ? NinBurstTiming.DokumoriEffective - MugDokumoriDebuffDuration : 0.54f, MugDokumoriDebuffDuration);
        Raiju = Status(SID.RaijuReady);
        TenChiJin = Status(SID.TenChiJin);
        Meisui = StatusLeft(SID.Meisui);
        Higi = StatusLeft(SID.Higi);
        TenriJindo = StatusLeft(SID.TenriJindoReady);
        _shukihoUnlocked = Unlocked(TraitID.Shukiho);
        UpdateOpenerTracking(strategy);
        UpdateBurstRecoveryState(strategy, primaryTarget);
        UpdateMudraCapTracking();
        _kassatsuQueuedThisFrame = false;
        ConfirmRaitonFirstPreKunaiActions();

        if (Mudra.Left == 0 && Mudra.Param == 0)
            _pendingNinjutsu = AID.None;
        // Kassatsu ran out while the mudra waited for a target that was gone or hidden: they no longer form its ninjutsu, and pressing it is
        // refused by the client until the mudra time out (irregular harness: Hyosho Ranryu refused for 85-110 frames, 4-6 s of idle GCD). The
        // sequence is finished as what the mudra do form now (Hyoton for Ten-Jin) the moment the target is back.
        else if (Mudra.Left > 0 && _pendingNinjutsu is AID.HyoshoRanryu or AID.GokaMekkyaku && !KassatsuActive)
            _pendingNinjutsu = AID.None;

        if (HiddenStatus && !NoUnhideZones.Contains(World.CurrentCFCID))
            Hints.StatusesToCancel.Add(((uint)SID.Hidden, Player.InstanceID));

        _splashCounts.Clear();
        (BestRangedAOETarget, NumRangedAOETargets) = SelectTarget(strategy, primaryTarget, 20, IsSplashTarget, _splashCounts);
        (BestNinkiAOETarget, NumNinkiAOETargets) = SelectTarget(strategy, primaryTarget, 25, IsSplashTarget, _splashCounts);

        NumAOETargets = NumMeleeAOETargets(strategy);
        // Katon (350 per target) beats Raiton (740, plus the Raiju it grants) from 3 targets
        var useNinjutsuAOE = NumRangedAOETargets > 2;
        // Goka Mekkyaku (850 per target) beats Hyosho Ranryu (1300) from 2 targets; both get Kassatsu's 30%
        var useKassatsuNinjutsuAOE = NumRangedAOETargets > 1;
        var burstNinjutsuPlan = BurstNinjutsuPlan.None;
        if (UseUltimateZeroSecondBurst(strategy))
        {
            ResetRaitonFirstPreKunaiState();
            ResetBurstVariant();
            burstNinjutsuPlan = BurstNinjutsuPlan.KassatsuFirst;
        }
        else
        {
            burstNinjutsuPlan = ResolveBurstVariant(strategy, useNinjutsuAOE, useKassatsuNinjutsuAOE, primaryTarget) switch
            {
                NinBurstVariant.RaitonFirst => BurstNinjutsuPlan.RaitonFirst,
                NinBurstVariant.KassatsuFirst => BurstNinjutsuPlan.KassatsuFirst,
                _ => BurstNinjutsuPlan.None
            };
            UpdateRaitonFirstPreKunaiState(burstNinjutsuPlan, primaryTarget);
            UpdateBurstLock();
        }

        var pos = GetNextPositional(primaryTarget?.Actor);
        UpdatePositionals(primaryTarget, ref pos);

        if (UseBasicComboOnly(strategy))
        {
            PushBasicComboOnlyUtilityOGCDs(strategy, primaryTarget);

            if (CountdownRemaining > 0)
                return;

            GoalZoneCombined(strategy, 3, Hints.GoalAOECircle(5), AID.DeathBlossom, minAoe: MeleeAOEMinTargets, maximumActionRange: 20);
            PushBasicComboOnlyNinjutsu(strategy, primaryTarget, useNinjutsuAOE);
            PushRangedFiller(strategy, primaryTarget, basicComboOnly: true);
            PushBasicComboOnlyGCD(strategy, primaryTarget);
            return;
        }

        OGCD(strategy, primaryTarget, burstNinjutsuPlan);

        if (CountdownRemaining > 0)
        {
            // finish the pre-pull suiton just before the pull so it doesn't become the first in-combat GCD
            if (CountdownRemaining < 6 && Unlocked(AID.Suiton))
                UseMudra(AID.Suiton, NinjutsuTarget(false, primaryTarget), endCondition: CountdownRemaining < 1);

            return;
        }

        // spec rule 4: during a downtime with a known return, prepare Suiton the way the pre-pull does - mudras first, the Ninjutsu
        // itself once the target is back. Only with nothing to hit: adds during the downtime keep the normal rotation, and a frame
        // that pushed no mudra falls through to it
        if (primaryTarget == null && Mechanic.DowntimeNow && Mechanic.ReturnKnown && Mechanic.TargetReturnIn < 6 && Unlocked(AID.Suiton) && ShadowWalker == 0 && TenChiJin.Left == 0
            && UseMudra(AID.Suiton, NinjutsuTarget(false, primaryTarget), endCondition: Mechanic.TargetReturnIn < 1, allowUntargetedSteps: true))
            return;

        GoalZoneCombined(strategy, 3, Hints.GoalAOECircle(5), AID.DeathBlossom, minAoe: MeleeAOEMinTargets, maximumActionRange: 20);

        if (TenChiJin.Left > GCD)
        {
            var aoe = NumRangedAOETargets > 2;
            var tcjTarget = aoe ? BestRangedAOETarget ?? primaryTarget : primaryTarget ?? BestRangedAOETarget;
            if (tcjTarget != null)
                PushGCD(NextTenChiJinAction(TenChiJin.Param, aoe), tcjTarget);
            return;
        }

        if (Mudra.Left == 0 && strategy.PhantomCannon.IsEnabled())
        {
            var cannonTarget = ResolveEnemy(strategy.PhantomCannon) ?? primaryTarget;

            if (DutyActionGCDReady(PhantomID.SilverCannon))
                PushGCD((AID)PhantomID.SilverCannon, cannonTarget, 10);
            if (DutyActionReadyIn(PhantomID.ShockCannon) <= GCD)
                PushGCD((AID)PhantomID.ShockCannon, cannonTarget, 9);
            if (DutyActionReadyIn(PhantomID.DarkCannon) <= GCD)
                PushGCD((AID)PhantomID.DarkCannon, cannonTarget, 8);
            if (DutyActionReadyIn(PhantomID.HolyCannon) <= GCD)
                PushGCD((AID)PhantomID.HolyCannon, cannonTarget, 7);
            if (DutyActionReadyIn(PhantomID.PhantomFire) <= GCD)
                PushGCD((AID)PhantomID.PhantomFire, cannonTarget, 6);
        }

        MechanicWindDown(primaryTarget);

        var pushedShadowWalkerPrepGCD = Mudra.Left == 0 && ShouldPrepareShadowWalkerForMugWindow(strategy) && UseMudra(useNinjutsuAOE ? AID.Huton : AID.Suiton, NinjutsuTarget(useNinjutsuAOE, primaryTarget));
        var pushedRaitonFirstPreKunaiGCD = !UseUltimateZeroSecondBurst(strategy) && !pushedShadowWalkerPrepGCD && TryPushRaitonFirstPreKunaiGCD(strategy, useNinjutsuAOE, primaryTarget);
        // Kassatsu's ninjutsu is kept for the Kunai's Bane window; while it is kept, only non-mudra GCDs run (a mudra would spend Kassatsu)
        // Amnesia refuses the mudra: Phantom Kamaitachi and the Raiju are not held for a ninjutsu that cannot start
        var kassatsuNinjutsuNow = KassatsuActive && !Locks.AbilitiesLocked && ShouldUseKassatsuNinjutsuNow(strategy, useNinjutsuAOE);
        // two charges in the window: Raiton before the Raiju it would otherwise wait behind, so the recharge keeps running
        var raitonBeforeRaiju = TargetTrickLeft > GCD && MudraChargesCapped && !KassatsuActiveOrQueued && !Locks.AbilitiesLocked && Mudra.Left == 0 && ShouldUseAttackNinjutsuNow(strategy, useNinjutsuAOE);

        if (!pushedShadowWalkerPrepGCD && !pushedRaitonFirstPreKunaiGCD && !kassatsuNinjutsuNow && PhantomKamaitachi > GCD && Mudra.Left == 0 && ShouldPK(BestRangedAOETarget) && !ShouldHoldPhantomKamaitachiForMugWindow(strategy, burstNinjutsuPlan))
            PushGCD(AID.PhantomKamaitachi, BestRangedAOETarget ?? primaryTarget);

        // spec rule 2: Raiju stacks that would expire during a long target loss bypass the holds and are spent before it
        var raijuExpiresDuringLoss = Raiju.Stacks > 0 && Mechanic.ExpiresDuringLoss(Raiju.Left);
        if (!pushedShadowWalkerPrepGCD && !pushedRaitonFirstPreKunaiGCD && !kassatsuNinjutsuNow && Raiju.Stacks > 0 && Mudra.Left == 0 && (raijuExpiresDuringLoss || !ShouldHoldRaijuForRaitonFirstPreKunai() && !(raitonBeforeRaiju && Raiju.Stacks < 3)))
        {
            if (strategy.ForkedRaiju.IsEnabled() && Player.DistanceToHitbox(primaryTarget) is > 3 and <= 20)
                PushGCD(AID.ForkedRaiju, primaryTarget);

            if (Player.DistanceToHitbox(primaryTarget) <= 3 || !strategy.ForkedRaiju.IsEnabled())
                PushGCD(AID.FleetingRaiju, primaryTarget);
        }

        if (!pushedShadowWalkerPrepGCD && !pushedRaitonFirstPreKunaiGCD && Unlocked(AID.Raiton))
        {
            if (Mudra.Left > 0)
            {
                var ninjutsu = _pendingNinjutsu != AID.None ? _pendingNinjutsu : KassatsuActive ? KassatsuAttackNinjutsu(useKassatsuNinjutsuAOE) : MudraFallbackNinjutsu(strategy, useNinjutsuAOE, useKassatsuNinjutsuAOE);
                UseMudra(ninjutsu, TargetForNinjutsu(ninjutsu, primaryTarget));
            }
            else if (kassatsuNinjutsuNow)
            {
                // ahead of Phantom Kamaitachi and Raiju, which stay available for the GCDs after it
                var ninjutsu = KassatsuSequenceNinjutsu(useKassatsuNinjutsuAOE, useNinjutsuAOE);
                UseMudra(ninjutsu, NinjutsuTarget(ninjutsu is AID.GokaMekkyaku or AID.Katon, primaryTarget), priority: 3);
            }
            else if (!KassatsuActive && ShouldUseAttackNinjutsuNow(strategy, useNinjutsuAOE))
            {
                UseMudra(NormalAttackNinjutsu(useNinjutsuAOE), NinjutsuTarget(useNinjutsuAOE, primaryTarget));
            }
        }
        else if (!pushedShadowWalkerPrepGCD && !pushedRaitonFirstPreKunaiGCD)
            UseMudra(AID.FumaShuriken, primaryTarget);

        PushRangedFiller(strategy, primaryTarget);

        var useMeleeAOE = NumAOETargets >= MeleeAOEMinTargets;

        if (!pushedShadowWalkerPrepGCD && !pushedRaitonFirstPreKunaiGCD && useMeleeAOE && Unlocked(AID.DeathBlossom))
        {
            if (ComboLastMove == AID.DeathBlossom && Unlocked(AID.HakkeMujinsatsu))
                PushGCD(AID.HakkeMujinsatsu, Player);

            PushGCD(AID.DeathBlossom, Player);
        }
        else if (!pushedShadowWalkerPrepGCD && !pushedRaitonFirstPreKunaiGCD)
        {
            if (ComboLastMove == AID.GustSlash && primaryTarget != null)
                PushGCD(GetComboEnder(primaryTarget.Actor), primaryTarget);

            if (ComboLastMove == AID.SpinningEdge)
                PushGCD(AID.GustSlash, primaryTarget);

            PushGCD(AID.SpinningEdge, primaryTarget);
        }
    }

    // Ten Chi Jin: the status parameter holds the mudra already spent (2 bits per step, like the mudra status). Single target runs
    // Fuma (Ten) -> Raiton (Chi) -> Suiton (Jin); AoE runs Fuma (Chi) -> Katon (Ten) -> Suiton (Jin), which keeps the Shadow Walker
    // Meisui needs and never reaches Doton. After a switch between the two mid-sequence, the step takes whichever mudra is left.
    private static AID NextTenChiJinAction(int param, bool aoe)
    {
        var first = param & 3;
        var second = (param >> 2) & 3;
        var step = first == 0 ? 0 : second == 0 ? 1 : 2;
        bool Used(int mudra) => first == mudra || second == mudra;
        if (step == 0)
            return aoe ? AID.FumaChi : AID.FumaTen;
        if (step == 1)
        {
            // Ten -> Katon, Chi -> Raiton, Jin -> Hyoton
            ReadOnlySpan<int> order = aoe ? [1, 2, 3] : [2, 1, 3];
            foreach (var mudra in order)
                if (!Used(mudra))
                    return mudra switch { 1 => AID.TCJKaton, 2 => AID.TCJRaiton, _ => AID.TCJHyoton };
        }
        // the one mudra left: Ten -> Huton, Chi -> Doton, Jin -> Suiton
        return !Used(3) ? AID.TCJSuiton : !Used(1) ? AID.TCJHuton : AID.TCJDoton;
    }

    // Kassatsu's ninjutsu is pressed inside the Kunai's Bane window, or now when Kassatsu would run out before that window opens
    // (two mudra, 1s, go before it lands).
    private bool ShouldUseKassatsuNinjutsuNow(in Strategy strategy, bool useNinjutsuAOE)
    {
        if (!KassatsuActive)
            return false;
        // without Hyosho Ranryu the ninjutsu is a 30% Raiton: not worth idling the mudra charges for the window (the low-level rule
        // already presses Kassatsu inside a buff window)
        if (TargetTrickLeft > 0 || !CanTrickInCombat || !Unlocked(AID.HyoshoRanryu) || UseUltimateZeroSecondBurst(strategy))
            return true;
        const float mudra = 2 * NinBurstTiming.MudraStep;
        if (Kassatsu <= GCD + mudra + 0.5f || Mechanic.ExpiresDuringLoss(Kassatsu))
            return true;
        // FightRemaining (value-of-information experiment): end dump, no wait for Kunai's Bane
        if (EndDump)
            return true;
        var kunaiIn = ExpectedTrickActionIn(strategy, useNinjutsuAOE);
        return kunaiIn == float.MaxValue || kunaiIn + GCDLength + mudra > Kassatsu;
    }

    private bool ShouldPrepareShadowWalkerForMugWindow(in Strategy strategy)
    {
        if (!CanTrickInCombat)
            return false;

        if (ShadowWalker > GCD)
            return false;

        if (Mudra.Left > 0 || _pendingNinjutsu != AID.None)
            return false;

        if (KassatsuActiveOrQueued)
            return false;

        if (TargetTrickLeft > 0)
            return false;

        var expectedTrickIn = ExpectedTrickActionIn(strategy, NumRangedAOETargets > 2);
        if (expectedTrickIn == float.MaxValue)
            return false;

        return expectedTrickIn <= DesiredShadowWalkerPrepLead();
    }

    private bool ShouldPrepareShadowWalkerForMugWindowDuringMudra(in Strategy strategy)
    {
        if (!CanTrickInCombat)
            return false;

        if (ShadowWalker > GCD)
            return false;

        if (KassatsuActiveOrQueued)
            return false;

        if (TargetTrickLeft > 0)
            return false;

        var expectedTrickIn = ExpectedTrickActionIn(strategy, NumRangedAOETargets > 2);
        if (expectedTrickIn == float.MaxValue)
            return false;

        return expectedTrickIn <= DesiredShadowWalkerPrepLead();
    }

    private float ExpectedTrickActionIn(in Strategy strategy, bool useNinjutsuAOE)
    {
        if (TargetTrickLeft > 0)
            return 0;

        var trickReadyIn = ReadyIn(TrickAction);
        var mugReadyIn = ReadyIn(MugAction);

        if (TargetMugLeft > 0)
        {
            if (trickReadyIn > TargetMugLeft + 0.1f)
                return float.MaxValue;

            if (UseUltimateZeroSecondBurst(strategy))
                return trickReadyIn;

            return OpenerBoundKunai(MathF.Max(trickReadyIn, MathF.Max(0, TargetMugLeft - KunaiStartMugLeftForEndAlign)));
        }

        if (!KunaiWouldBeReadyForNextMugIfUsedIn(trickReadyIn))
            return OpenerBoundKunai(UseUltimateZeroSecondBurst(strategy) ? mugReadyIn : mugReadyIn + NinBurstTiming.KunaiAfterDokumori);

        if (UseUltimateZeroSecondBurst(strategy))
        {
            if (mugReadyIn <= 20)
                return mugReadyIn;

            return trickReadyIn;
        }

        if (mugReadyIn <= 20 && OpenerDokumoriHeld)
            return OpenerBoundKunai(MathF.Max(mugReadyIn, GCD + (_openerDokumoriMin - _openerGCDs - 1) * GCDLength) + NinBurstTiming.KunaiAfterDokumori);

        if (mugReadyIn <= 20)
            return OpenerBoundKunai(mugReadyIn + NinBurstTiming.KunaiAfterDokumori);

        return OpenerBoundKunai(trickReadyIn);
    }

    // the opening Kunai's Bane also waits for its GCD bound (and for Dokumori's, which it follows)
    private float OpenerBoundKunai(float kunaiIn)
    {
        if (!OpenerKunaiHeld)
            return kunaiIn;
        var gcdsLeft = _openerKunaiMin - _openerGCDs;
        return MathF.Max(kunaiIn, GCD + (gcdsLeft - 1) * GCDLength);
    }

    private float DesiredShadowWalkerPrepLead()
        => MathF.Min(12, MathF.Max(10, 20 - GCDLength * 4));

    // Death Blossom / Hakke Mujinsatsu against the single-target combo with its Kazematoi, positionals and ninki: with the 90+ potencies
    // the combo holds up to three targets (harness: 3 targets ST +5.2% at 100 / +1.6% at 90, 4 targets AoE +0.2%), below 90 AoE wins at
    // three (+1.2% at 80, +3.1% at 70)
    private int MeleeAOEMinTargets => Player.Level >= 90 ? 4 : 3;

    private AID MudraFallbackNinjutsu(in Strategy strategy, bool useNinjutsuAOE, bool useKassatsuNinjutsuAOE)
    {
        if (ShouldPrepareShadowWalkerForMugWindowDuringMudra(strategy))
            return useNinjutsuAOE ? AID.Huton : AID.Suiton;

        if (KassatsuActive)
            return KassatsuAttackNinjutsu(useKassatsuNinjutsuAOE);

        if (CurrentNinjutsu != AID.Ninjutsu && CurrentNinjutsu != AID.FumaShuriken)
            return CurrentNinjutsu;

        return NormalAttackNinjutsu(useNinjutsuAOE);
    }

    private bool ShouldUseAttackNinjutsuNow(in Strategy strategy, bool useNinjutsuAOE)
    {
        if (Mudra.Left > 0)
            return true;

        // FightRemaining (value-of-information experiment): charges that would be held for a later window are spent before the end
        if (EndDump && HasMudraCharge && !KassatsuActive && !_kassatsuQueuedThisFrame)
            return true;

        if (ShouldPrepareShadowWalkerForMugWindow(strategy))
            return false;

        // a mudra pressed under Kassatsu becomes Kassatsu's ninjutsu, which has its own timing
        if (KassatsuActive || _kassatsuQueuedThisFrame)
            return false;

        if (ShouldHoldNormalAttackNinjutsuForKunaiEndAlign(strategy))
            return false;

        if (!UseUltimateZeroSecondBurst(strategy) && _dokumoriPreKunaiActive && TargetTrickLeft == 0)
            return false;

        if (TargetMugLeft <= GCD && TargetTrickLeft <= GCD && ShouldReserveMudraForEvenBurstRecovery())
            return false;

        if (TargetTrickLeft > GCD || !CanTrickInCombat && (TargetMugLeft > GCD || RaidBuffsLeft > GCD))
            return true;

        // the next Kunai's Bane window gets the charges: before it only what would otherwise sit capped is spent (Raiton first spends
        // its pre-Kunai charge through its own sequence); a charge spent in other buffs has to be back by then
        var kunaiIn = ExpectedTrickActionIn(strategy, useNinjutsuAOE);
        // Pacification refuses the weaponskills but not the ninjutsu (abilities): a charge that is back before the window fills a GCD
        // that would otherwise idle
        if (Locks.WeaponskillsLocked && HasMudraCharge && kunaiIn > MudraChargeRecast + GCD)
            return true;
        if ((TargetMugLeft > GCD || RaidBuffsLeft > GCD) && kunaiIn > MudraChargeRecast + GCD)
            return true;

        return ShouldSpendNormalNinjutsuToAvoidMudraOvercap(strategy, useNinjutsuAOE);
    }

    private bool ShouldHoldNormalAttackNinjutsuForKunaiEndAlign(in Strategy strategy)
        => !UseUltimateZeroSecondBurst(strategy)
        && TargetMugLeft > 0
        && TargetTrickLeft <= 0
        && TargetMugLeft <= KunaiStartMugLeftForEndAlign + GCDLength;


    private bool ShouldSpendNormalNinjutsuToAvoidMudraOvercap(in Strategy strategy, bool useNinjutsuAOE)
    {
        if (!Unlocked(AID.Raiton))
            return Unlocked(AID.FumaShuriken) && HasMudraCharge;

        if (Mudra.Left > 0)
            return true;

        if (!HasMudraCharge)
            return false;

        if (ShouldReserveMudraForEvenBurstRecovery())
            return false;

        if (!MudraChargesCapped && MaxChargesIn(AID.Ten1) > GCD + 0.1f)
            return false;

        if (ShouldHoldMudraForBurstByDPS(strategy, useNinjutsuAOE))
            return false;

        if (MudraChargesCapped)
            return true;

        return MaxChargesIn(AID.Ten1) <= GCD + 0.1f;
    }

    private bool ShouldReserveMudraForEvenBurstRecovery()
    {
        if (!BurstRecoveryActive || TargetMugLeft > GCD)
            return false;

        var mugIn = ReadyIn(MugAction);
        if (mugIn <= GCD + AnimLock || mugIn > MudraRecoveryReserveWindow)
            return false;

        var projectedCharges = ProjectedMudraChargesAt(mugIn);
        var shadowWalkerCoversBurst = ShadowWalker > mugIn + GCDLength;
        var requiredCharges = shadowWalkerCoversBurst ? 1 : 2;
        return projectedCharges <= requiredCharges;
    }

    private int ProjectedMudraChargesAt(float seconds)
    {
        var currentCharges = MudraCharges;
        if (currentCharges >= 2 || seconds <= 0)
            return currentCharges;

        var timeToNextCharge = currentCharges == 0
            ? MathF.Max(0, MaxChargesIn(AID.Ten1) - MudraChargeRecast)
            : MaxChargesIn(AID.Ten1);
        if (timeToNextCharge > seconds)
            return currentCharges;

        var regeneratedCharges = 1 + Math.Max(0, (int)MathF.Floor((seconds - timeToNextCharge) / MudraChargeRecast));
        return Math.Min(2, currentCharges + regeneratedCharges);
    }

    private bool MudraAtCap()
    {
        if (!Unlocked(AID.Ten1))
            return false;

        if (Mudra.Left > 0)
            return false;

        return MudraChargesCapped;
    }

    private void UpdateMudraCapTracking()
    {
        if (MudraAtCap())
        {
            if (_mudraCapStartedAt == DateTime.MinValue)
                _mudraCapStartedAt = World.CurrentTime;
        }
        else
        {
            _mudraCapStartedAt = DateTime.MinValue;
        }
    }

    private float MudraCapHoldTime()
    {
        if (_mudraCapStartedAt == DateTime.MinValue)
            return 0;

        return MathF.Max(0, (float)(World.CurrentTime - _mudraCapStartedAt).TotalSeconds);
    }

    private float NextNinjutsuBurstWindowIn(in Strategy strategy, bool useNinjutsuAOE)
    {
        if (TargetMugLeft > GCD || TargetTrickLeft > GCD || RaidBuffsLeft > GCD)
            return 0;

        var mugIn = ReadyIn(MugAction);
        var trickIn = ReadyIn(TrickAction);
        var raidIn = RaidBuffsIn;

        var expectedTrickIn = float.MaxValue;
        if (CanTrickInCombat)
            expectedTrickIn = ExpectedTrickActionIn(strategy, useNinjutsuAOE);

        return MathF.Min(MathF.Min(mugIn, trickIn), MathF.Min(expectedTrickIn, raidIn));
    }

    private float NormalNinjutsuPotency(bool useNinjutsuAOE)
    {
        if (useNinjutsuAOE)
            return NinPotency.Of(AID.Katon, Player.Level) * MathF.Max(1, NumRangedAOETargets);

        var potency = NinPotency.Of(AID.Raiton, Player.Level);

        if (Unlocked(AID.ForkedRaiju))
            potency += NinPotency.Of(AID.FleetingRaiju, Player.Level);

        return potency;
    }

    private bool ShouldUsePotionSoonForNinjutsuHold(in Strategy strategy)
    {
        if (strategy.Potion.Value != PotionStrategy.EvenBurst)
            return false;

        if (PotionLeft > GCD)
            return true;

        if (PotionCD > 5)
            return false;

        if (UseUltimateZeroSecondBurst(strategy))
            return ReadyIn(MugAction) <= 5 || TargetMugLeft > GCD;

        return TargetMugLeft > GCD || ReadyIn(MugAction) <= 5;
    }

    private float BurstNinjutsuMultiplier(in Strategy strategy)
    {
        var multiplier = 1.0f;

        if (TargetMugLeft > GCD || ReadyIn(MugAction) <= 5)
            multiplier *= NinPotency.DokumoriBonus;

        if (TargetTrickLeft > GCD || ReadyIn(TrickAction) <= 5)
            multiplier *= NinPotency.KunaiBonus;

        if (RaidBuffsLeft > GCD || RaidBuffsIn <= 5)
            multiplier *= RaidBuffDamageMultiplier;

        if (PotionLeft > GCD || ShouldUsePotionSoonForNinjutsuHold(strategy))
            multiplier *= PotionDamageMultiplier;

        return multiplier;
    }

    private float MudraCapOpportunityLoss(float holdSeconds, bool useNinjutsuAOE)
    {
        if (holdSeconds <= 0)
            return 0;

        var basePotency = NormalNinjutsuPotency(useNinjutsuAOE);
        return basePotency * holdSeconds / MudraChargeRecast;
    }

    private float AllowedMudraCapHoldForBurst(in Strategy strategy, float burstIn)
    {
        var allowed = DefaultAllowedMudraCapHold;

        if (UseUltimateZeroSecondBurst(strategy))
            allowed = 3.0f;

        if (PotionLeft > GCD || PotionCD <= burstIn + 1)
            allowed += 0.5f;

        if (RaidBuffsIn <= burstIn + 1 || RaidBuffsLeft > GCD)
            allowed += 0.5f;

        return MathF.Min(MaxAllowedMudraCapHold, allowed);
    }

    private bool ShouldHoldMudraForBurstByDPS(in Strategy strategy, bool useNinjutsuAOE)
    {
        if (!Unlocked(AID.Raiton))
            return false;

        if (Mudra.Left > 0)
            return false;

        if (!MudraChargesCapped)
            return false;

        if (TargetMugLeft > GCD || TargetTrickLeft > GCD || RaidBuffsLeft > GCD)
            return false;

        if (ShouldPrepareShadowWalkerForMugWindow(strategy))
            return false;

        var burstIn = NextNinjutsuBurstWindowIn(strategy, useNinjutsuAOE);
        if (burstIn == float.MaxValue)
            return false;

        if (burstIn > 10)
            return false;

        var capHold = MudraCapHoldTime();
        var allowedHold = AllowedMudraCapHoldForBurst(strategy, burstIn);

        if (capHold >= allowedHold)
            return false;

        var normalValue = NormalNinjutsuPotency(useNinjutsuAOE);
        var burstValue = normalValue * BurstNinjutsuMultiplier(strategy);
        var opportunityLoss = MudraCapOpportunityLoss(capHold + MathF.Min(burstIn, allowedHold), useNinjutsuAOE);

        return burstValue - normalValue - opportunityLoss > MudraHoldDecisionMargin;
    }

    private bool ShouldHoldPhantomKamaitachiForRaitonFirst(BurstNinjutsuPlan plan)
        => plan == BurstNinjutsuPlan.RaitonFirst && !RaitonFirstPreKunaiComplete() && TargetTrickLeft <= GCD;

    private bool ShouldHoldPhantomKamaitachiForMugWindow(in Strategy strategy, BurstNinjutsuPlan plan)
        => !CanFitGCD(PhantomKamaitachi, 1) ? false
        : TargetMugLeft > GCD || TargetTrickLeft > GCD ? false
        : ShouldPrepareShadowWalkerForMugWindow(strategy) || ShouldHoldPhantomKamaitachiForRaitonFirst(plan);

    private bool ShouldDelayTrickForRaitonFirst(BurstNinjutsuPlan plan)
    {
        if (ShouldSkipOddKunaiForMugSync())
            return false;

        if (plan != BurstNinjutsuPlan.RaitonFirst)
            return false;

        if (TargetTrickLeft > 0)
            return false;

        if (ReadyIn(TrickAction) > GCD)
            return false;

        if (ShadowWalker <= GCD)
            return false;

        if (Mudra.Left > 0 || _pendingNinjutsu != AID.None)
            return true;

        var atOrPastAlignedStart = TargetMugLeft > 0 && TargetMugLeft <= KunaiStartMugLeftForEndAlign + KunaiEndAlignTolerance;

        if (!RaitonFirstPreKunaiComplete())
            return !atOrPastAlignedStart && TargetMugLeft > GCD + AnimLock;

        if (TargetMugLeft > KunaiStartMugLeftForEndAlign + KunaiEndAlignTolerance)
            return true;

        return false;
    }

    private bool RaitonFirstOpeningDone()
        => TargetTrickLeft > 0 || Raiju.Stacks > 0 || ReadyIn(AID.Ten1) > GCD;

    private void ResetRaitonFirstPreKunaiState()
    {
        _dokumoriPreKunaiActive = false;
        _dokumoriPreKunaiGCDs = 0;
        _dokumoriPreKunaiUsePhantom = false;
        _dokumoriPreKunaiRaitonDone = false;
        _dokumoriPreKunaiPhantomDone = false;
        _dokumoriPreKunaiComboDone = false;
        _dokumoriPreKunaiPendingNinjutsu = AID.None;
        _dokumoriPreKunaiNinjutsuStarted = false;
        _dokumoriPreKunaiPendingCombo = AID.None;
        _dokumoriPreKunaiComboStateAtPush = AID.None;
        _dokumoriPreKunaiPhantomPending = false;
    }

    private void ConfirmRaitonFirstPreKunaiActions()
    {
        if (_dokumoriPreKunaiPendingNinjutsu != AID.None && Mudra.Left > 0)
            _dokumoriPreKunaiNinjutsuStarted = true;

        if (_dokumoriPreKunaiPendingNinjutsu != AID.None && _dokumoriPreKunaiNinjutsuStarted && Mudra.Left <= 0 && Mudra.Param == 0)
        {
            _dokumoriPreKunaiPendingNinjutsu = AID.None;
            _dokumoriPreKunaiNinjutsuStarted = false;
            MarkRaitonFirstRaitonDone();
        }

        // the combo state only ever *changes* when a combo weaponskill lands: a starter/second step becomes the last move,
        // a finisher resets it to None (so waiting for ComboLastMove == finisher would never complete)
        if (_dokumoriPreKunaiPendingCombo != AID.None && ComboLastMove != _dokumoriPreKunaiComboStateAtPush)
        {
            _dokumoriPreKunaiPendingCombo = AID.None;
            _dokumoriPreKunaiComboStateAtPush = AID.None;
            MarkRaitonFirstComboDone();
        }

        if (_dokumoriPreKunaiPhantomPending && PhantomKamaitachi <= 0)
        {
            _dokumoriPreKunaiPhantomPending = false;
            MarkRaitonFirstPhantomDone();
        }
    }

    private void UpdateRaitonFirstPreKunaiState(BurstNinjutsuPlan plan, Enemy? primaryTarget)
    {
        if (ShouldSkipOddKunaiForMugSync())
        {
            ResetRaitonFirstPreKunaiState();
            return;
        }

        if (!Player.InCombat || primaryTarget == null || TargetTrickLeft > 0 || TargetMugLeft <= 0 && TargetTrickLeft <= 0 || TargetMugLeft <= 0 && ReadyIn(MugAction) > 20)
        {
            ResetRaitonFirstPreKunaiState();
            return;
        }

        if (_dokumoriPreKunaiActive)
        {
            // a GCD past the aligned start with the pair unfinished and no mudra underway: give up, Kunai's Bane goes now
            if (TargetTrickLeft == 0 && TargetMugLeft <= KunaiStartMugLeftForEndAlign - GCDLength && Mudra.Left <= 0 && !RaitonFirstPreKunaiComplete())
                ResetRaitonFirstPreKunaiState();

            return;
        }

        if (!HasTimeForRaitonFirstPreKunai())
            return;

        // the pair starts with the Dokumori press once the burst variant says Raiton first and a charge is there for it
        // Shadow Walker has to last until Kunai's Bane goes after the pair
        if (plan == BurstNinjutsuPlan.RaitonFirst && TargetMugLeft > 0 && TargetTrickLeft == 0 && (HiddenStatus || ShadowWalker > GCD + 2 * GCDLength)
            && (HasMudraCharge || MudraNextChargeIn <= GCD + GCDLength))
        {
            _dokumoriPreKunaiActive = true;
            _dokumoriPreKunaiGCDs = 0;
            _dokumoriPreKunaiUsePhantom = HasPhantomForRaitonFirst();
            _dokumoriPreKunaiRaitonDone = false;
            _dokumoriPreKunaiPhantomDone = false;
            _dokumoriPreKunaiComboDone = false;
            _dokumoriPreKunaiPendingNinjutsu = AID.None;
            _dokumoriPreKunaiNinjutsuStarted = false;
            _dokumoriPreKunaiPendingCombo = AID.None;
            _dokumoriPreKunaiComboStateAtPush = AID.None;
            _dokumoriPreKunaiPhantomPending = false;
        }
    }

    private bool HasPhantomForRaitonFirst()
        => Unlocked(AID.PhantomKamaitachi) && PhantomKamaitachi > GCD && CanFitGCD(PhantomKamaitachi, 1);

    private bool HasTimeForRaitonFirstPreKunai()
    {
        if (TargetMugLeft <= 0)
            return false;

        var timeUntilAlignedKunai = TargetMugLeft - KunaiStartMugLeftForEndAlign;
        return timeUntilAlignedKunai >= GCDLength * 2 - 0.25f;
    }

    private bool RaitonFirstPreKunaiComplete()
    {
        if (TargetTrickLeft > 0)
            return true;

        if (!_dokumoriPreKunaiActive)
            return RaitonFirstOpeningDone();

        if (_dokumoriPreKunaiUsePhantom)
            return _dokumoriPreKunaiGCDs >= 2 && _dokumoriPreKunaiRaitonDone && _dokumoriPreKunaiPhantomDone;

        return _dokumoriPreKunaiGCDs >= 2 && _dokumoriPreKunaiComboDone && _dokumoriPreKunaiRaitonDone;
    }

    private bool ShouldHoldRaijuForRaitonFirstPreKunai()
        => _dokumoriPreKunaiActive && TargetTrickLeft == 0;

    private void MarkRaitonFirstRaitonDone()
    {
        if (_dokumoriPreKunaiRaitonDone)
            return;

        _dokumoriPreKunaiRaitonDone = true;
        _dokumoriPreKunaiGCDs++;
    }

    private void MarkRaitonFirstPhantomDone()
    {
        if (_dokumoriPreKunaiPhantomDone)
            return;

        _dokumoriPreKunaiPhantomDone = true;
        _dokumoriPreKunaiGCDs++;
    }

    private void MarkRaitonFirstComboDone()
    {
        if (_dokumoriPreKunaiComboDone)
            return;

        _dokumoriPreKunaiComboDone = true;
        _dokumoriPreKunaiGCDs++;
    }

    private bool TryPushRaitonFirstPreKunaiGCD(in Strategy strategy, bool useNinjutsuAOE, Enemy? primaryTarget)
    {
        if (!_dokumoriPreKunaiActive || TargetTrickLeft > 0 || TargetMugLeft <= 0 || RaitonFirstPreKunaiComplete())
            return false;

        if (ShadowWalker <= GCD)
            return false;

        if (ShouldPrepareShadowWalkerForMugWindow(strategy))
            return false;

        if (Mudra.Left > 0 || KassatsuActiveOrQueued)
            return false;

        // The pair's Raiton and Phantom Kamaitachi go before Kunai's Bane on purpose, so the hold that keeps an ordinary Raiton for the
        // window does not apply (it stopped the pair's second GCD, which comes after that hold starts). Once the aligned start has
        // passed, Kunai's Bane goes first.
        if (TargetMugLeft <= KunaiStartMugLeftForEndAlign)
            return false;

        var ninjutsu = NormalAttackNinjutsu(useNinjutsuAOE);
        if (_dokumoriPreKunaiUsePhantom)
        {
            if (_dokumoriPreKunaiRaitonDone && !_dokumoriPreKunaiPhantomDone)
            {
                if (TargetMugLeft <= KunaiStartMugLeftForEndAlign + KunaiEndAlignTolerance)
                {
                    ResetRaitonFirstPreKunaiState();
                    return false;
                }

                if (PhantomKamaitachi <= GCD || !ShouldPK(BestRangedAOETarget))
                {
                    ResetRaitonFirstPreKunaiState();
                    return false;
                }
            }

            if (!_dokumoriPreKunaiRaitonDone)
            {
                var result = UseMudraDetailed(ninjutsu, NinjutsuTarget(useNinjutsuAOE, primaryTarget));
                if (result != MudraPushResult.None)
                    _dokumoriPreKunaiPendingNinjutsu = ninjutsu;

                return result != MudraPushResult.None;
            }

            if (_dokumoriPreKunaiRaitonDone && !_dokumoriPreKunaiPhantomDone && PhantomKamaitachi > GCD && ShouldPK(BestRangedAOETarget))
            {
                PushGCD(AID.PhantomKamaitachi, BestRangedAOETarget ?? primaryTarget, 20);
                _dokumoriPreKunaiPhantomPending = true;
                return true;
            }

            return false;
        }

        if (!_dokumoriPreKunaiComboDone)
            return TryPushRaitonFirstComboGCD(primaryTarget);

        if (!_dokumoriPreKunaiRaitonDone)
        {
            var result = UseMudraDetailed(ninjutsu, NinjutsuTarget(useNinjutsuAOE, primaryTarget));
            if (result != MudraPushResult.None)
                _dokumoriPreKunaiPendingNinjutsu = ninjutsu;

            return result != MudraPushResult.None;
        }

        return false;
    }

    private bool TryPushRaitonFirstComboGCD(Enemy? primaryTarget)
    {
        if (primaryTarget == null)
            return false;

        if (_dokumoriPreKunaiPendingCombo == AID.None)
        {
            _dokumoriPreKunaiComboStateAtPush = ComboLastMove;
            _dokumoriPreKunaiPendingCombo = ComboLastMove == AID.GustSlash
                ? GetComboEnder(primaryTarget.Actor)
                : ComboLastMove == AID.SpinningEdge
                    ? AID.GustSlash
                    : AID.SpinningEdge;
        }

        PushGCD(_dokumoriPreKunaiPendingCombo, primaryTarget, 20);
        return true;
    }

    private AID NormalAttackNinjutsu(bool useNinjutsuAOE)
        => useNinjutsuAOE ? AID.Katon : AID.Raiton;

    private AID KassatsuAttackNinjutsu(bool useNinjutsuAOE)
        => useNinjutsuAOE && Unlocked(AID.GokaMekkyaku) ? AID.GokaMekkyaku : Unlocked(AID.HyoshoRanryu) ? AID.HyoshoRanryu : NormalAttackNinjutsu(useNinjutsuAOE);

    // Kassatsu's ninjutsu needs Kassatsu at its press, two mudra after the sequence starts. When Kassatsu runs out before that (an
    // interruption delayed the sequence), the mudra are still free but form the normal ninjutsu: Raiton with its Raiju rather than the
    // Hyoton that Hyosho Ranryu's Ten-Jin would fall back to.
    private AID KassatsuSequenceNinjutsu(bool useKassatsuNinjutsuAOE, bool useNinjutsuAOE)
        => Kassatsu <= GCD + 2 * NinBurstTiming.MudraStep + 0.05f ? NormalAttackNinjutsu(useNinjutsuAOE) : KassatsuAttackNinjutsu(useKassatsuNinjutsuAOE);

    private bool KassatsuAttackNinjutsuUnlocked(bool useNinjutsuAOE)
        => useNinjutsuAOE ? Unlocked(AID.GokaMekkyaku) : Unlocked(AID.HyoshoRanryu);

    private Enemy? NinjutsuTarget(bool useNinjutsuAOE, Enemy? primaryTarget)
        => useNinjutsuAOE ? BestRangedAOETarget ?? primaryTarget : primaryTarget ?? BestRangedAOETarget;

    private Enemy? TargetForNinjutsu(AID ninjutsu, Enemy? primaryTarget)
        => ninjutsu is AID.Katon or AID.GokaMekkyaku or AID.Huton or AID.Doton or AID.TCJKaton or AID.TCJHuton or AID.TCJDoton ? BestRangedAOETarget ?? primaryTarget : primaryTarget ?? BestRangedAOETarget;

    private AID NormalizeNinjutsu(AID ninjutsu) => ninjutsu switch
    {
        AID.GokaMekkyaku => Unlocked(AID.GokaMekkyaku) ? AID.GokaMekkyaku : Unlocked(AID.Katon) ? AID.Katon : AID.FumaShuriken,
        AID.HyoshoRanryu => Unlocked(AID.HyoshoRanryu) ? AID.HyoshoRanryu : Unlocked(AID.Hyoton) ? AID.Hyoton : Unlocked(AID.Raiton) ? AID.Raiton : AID.FumaShuriken,
        AID.Huton => Unlocked(AID.Huton) ? AID.Huton : Unlocked(AID.Katon) ? AID.Katon : Unlocked(AID.Raiton) ? AID.Raiton : AID.FumaShuriken,
        AID.Doton => Unlocked(AID.Doton) ? AID.Doton : Unlocked(AID.Katon) ? AID.Katon : Unlocked(AID.Raiton) ? AID.Raiton : AID.FumaShuriken,
        AID.Suiton => Unlocked(AID.Suiton) ? AID.Suiton : Unlocked(AID.Raiton) ? AID.Raiton : AID.FumaShuriken,
        AID.Katon => Unlocked(AID.Katon) ? AID.Katon : AID.FumaShuriken,
        AID.Raiton => Unlocked(AID.Raiton) ? AID.Raiton : AID.FumaShuriken,
        AID.Hyoton => Unlocked(AID.Hyoton) ? AID.Hyoton : Unlocked(AID.Raiton) ? AID.Raiton : AID.FumaShuriken,
        AID.FumaShuriken => Unlocked(AID.FumaShuriken) ? AID.FumaShuriken : AID.None,
        _ => Combos.ContainsKey(ninjutsu) && Unlocked(ninjutsu) ? ninjutsu : AID.None
    };

    // Spec rule 2b: before a long target loss the last GCD slots go to the strongest candidates whose recast is back by the return: a
    // Raiton (mudra charge, ~1 s of mudras before it), Fleeting Raiju, Phantom Kamaitachi. Mudra sequences and Ten Chi Jin keep their path.
    private void MechanicWindDown(Enemy? primaryTarget)
    {
        if (!WindDown.Active(Mechanic, GCDLength) || Mudra.Left > 0 || TenChiJin.Left > 0 || primaryTarget == null)
            return;

        float P(AID aid) => WindDownPotency.Of(WindDownPotency.NIN, (uint)aid);
        var gcds = new WindDownCandidate[3];
        var n = 0;
        if (Unlocked(AID.Raiton) && HasMudraCharge)
            gcds[n++] = new(ActionID.MakeSpell(AID.Raiton), [P(AID.Raiton)], ReadyIn(AID.Ten1), RecastRecoveredIn(AID.Ten1), ExtraTime: 1.0f);
        if (Raiju.Stacks > 0)
            gcds[n++] = new(ActionID.MakeSpell(AID.FleetingRaiju), [P(AID.FleetingRaiju)], 0, 0);
        if (PhantomKamaitachi > 0)
            gcds[n++] = new(ActionID.MakeSpell(AID.PhantomKamaitachi), [P(AID.PhantomKamaitachi)], 0, 0);
        var pick = WindDown.SelectGcd(gcds.AsSpan(0, n), Mechanic, GCD, GCDLength, 0.6f, WindDownPotency.FillerNIN);
        if (pick < 0)
            return;
        var aid = (AID)gcds[pick].Action.ID;
        if (aid == AID.Raiton)
            UseMudra(AID.Raiton, NinjutsuTarget(false, primaryTarget), startCondition: true, priority: 30);
        else
            PushGCD(aid, aid == AID.PhantomKamaitachi ? BestRangedAOETarget ?? primaryTarget : primaryTarget, 30);
    }

    private bool ShouldPK(Enemy? primaryTarget)
    {
        // spec rule 2: a Phantom Kamaitachi Ready that would expire during a long target loss is used before it
        if (Mechanic.ExpiresDuringLoss(PhantomKamaitachi))
            return true;

        if (RaidBuffsLeft > GCD || TargetTrickLeft > GCD || TargetMugLeft > GCD)
            return true;

        if (!CanFitGCD(PhantomKamaitachi, 1))
            return true;

        return Player.DistanceToHitbox(primaryTarget) is > 3 and <= 20;
    }

    private AID GetComboEnder(Actor primaryTarget)
    {
        if (!Unlocked(AID.AeolianEdge))
            return AID.SpinningEdge;

        if (!Unlocked(AID.ArmorCrush))
            return AID.AeolianEdge;

        if (Kazematoi == 0)
            return AID.ArmorCrush;

        if (Kazematoi >= 4)
            return AID.AeolianEdge;

        return primaryTarget.Omnidirectional || GetCurrentPositional(primaryTarget) == Positional.Rear ? AID.AeolianEdge : AID.ArmorCrush;
    }

    // Throwing Dagger while the target is out of melee range but within 20y. Pushed below every other GCD (priority 1 against the
    // combo's and the Raiju's 2): the queue skips candidates that are out of range, so it only runs when nothing else can. Not during
    // mudra or Ten Chi Jin, where a weaponskill would cut the sequence.
    // Basic combo only: its mudra valve is a ranged GCD too (Raiton + Raiju, worth far more than a dagger), so the dagger does not take the
    // GCD when that ninjutsu is about to come due; the harness measured the dagger at -0.3..-0.5% there otherwise.
    private void PushRangedFiller(in Strategy strategy, Enemy? primaryTarget, bool basicComboOnly = false)
    {
        if (!strategy.ThrowingDagger.IsEnabled() || !Unlocked(AID.ThrowingDagger) || primaryTarget == null)
            return;

        if (Mudra.Left > 0 || TenChiJin.Left > 0 || _pendingNinjutsu != AID.None)
            return;

        if (basicComboOnly && Unlocked(AID.Ten1) && HasMudraCharge && MaxChargesIn(AID.Ten1) <= GCD + GCDLength)
            return;

        // a Raiton that has just landed turns into a Raiju a moment later (its status shows up after the hit); the dagger would take the
        // GCD that Raiju needs
        if (RaijuIncoming() || Raiju.Stacks > 0 && basicComboOnly)
            return;

        if (Player.DistanceToHitbox(primaryTarget) is > 3 and <= 20)
            PushGCD(AID.ThrowingDagger, primaryTarget, 1);
    }

    private bool RaijuIncoming()
        => Unlocked(AID.FleetingRaiju) && Raiju.Stacks == 0 && Manager.LastCast.Data is { } last && (AID)last.Action.ID is AID.Raiton or AID.TCJRaiton
        && (World.CurrentTime - Manager.LastCast.Time).TotalSeconds < 3;

    private void PushBasicComboOnlyGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        var useMeleeAOE = NumAOETargets >= MeleeAOEMinTargets;

        if (useMeleeAOE && Unlocked(AID.DeathBlossom))
        {
            if (ComboLastMove == AID.DeathBlossom && Unlocked(AID.HakkeMujinsatsu))
                PushGCD(AID.HakkeMujinsatsu, Player);

            PushGCD(AID.DeathBlossom, Player);
            return;
        }

        if (primaryTarget == null)
            return;

        if (ComboLastMove == AID.GustSlash)
            PushGCD(GetComboEnder(primaryTarget.Actor), primaryTarget);

        if (ComboLastMove == AID.SpinningEdge)
            PushGCD(AID.GustSlash, primaryTarget);

        PushGCD(AID.SpinningEdge, primaryTarget);
    }

    private void PushBasicComboOnlyUtilityOGCDs(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Player.InCombat)
        {
            if (strategy.Hide.IsEnabled()
                && Mudra.Left == 0
                && GCD == 0
                && Unlocked(AID.Ten1)
                && OnCooldown(AID.Ten1))
                PushOGCD(AID.Hide, Player);

            return;
        }

        // no burst to line anything up with: the gauge is only spent to keep it from overcapping (the normal rotation's outside-burst rule)
        if (primaryTarget != null && CanSpendNinki() && Ninki > 85)
            PushNinkiSpender(primaryTarget, 1);

        if (ShouldUseTrueNorthNow(strategy, primaryTarget, basicComboOnly: true))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.TrueNorth), Player, ActionQueue.Priority.Low + 0.5f);
    }

    // Basic combo only has no burst, so it never builds Shadow Walker (no Suiton/Huton, also not before the pull). Ninjutsu is only the
    // valve for mudra charges that would overcap: Raiton (Katon on 3+ targets, Fuma Shuriken before Raiton is learned), plus the Raiju
    // Raiton grants, which would otherwise expire unused.
    private void PushBasicComboOnlyNinjutsu(in Strategy strategy, Enemy? primaryTarget, bool useNinjutsuAOE)
    {
        if (!Unlocked(AID.Ten1) || KassatsuActive)
            return;

        if (Mudra.Left > 0)
        {
            // finish the ninjutsu that was started
            var ninjutsu = _pendingNinjutsu != AID.None ? _pendingNinjutsu : Unlocked(AID.Raiton) ? NormalAttackNinjutsu(useNinjutsuAOE) : AID.FumaShuriken;
            UseMudra(ninjutsu, TargetForNinjutsu(ninjutsu, primaryTarget));
            return;
        }

        if (primaryTarget == null)
            return;

        if (Raiju.Stacks > 0)
        {
            if (strategy.ForkedRaiju.IsEnabled() && Player.DistanceToHitbox(primaryTarget) is > 3 and <= 20)
                PushGCD(AID.ForkedRaiju, primaryTarget);

            if (Player.DistanceToHitbox(primaryTarget) <= 3 || !strategy.ForkedRaiju.IsEnabled())
                PushGCD(AID.FleetingRaiju, primaryTarget);
        }

        // a charge that is capped, or would be by the next GCD; under Pacification (weaponskills refused, ninjutsu not) any charge
        if (HasMudraCharge && (MudraChargesCapped || MaxChargesIn(AID.Ten1) <= GCD + 0.1f || Locks.WeaponskillsLocked))
        {
            if (Unlocked(AID.Raiton))
                UseMudra(NormalAttackNinjutsu(useNinjutsuAOE), NinjutsuTarget(useNinjutsuAOE, primaryTarget));
            else
                UseMudra(AID.FumaShuriken, primaryTarget);
        }
    }

    private Positional PositionalForAction(AID action) => action switch
    {
        AID.AeolianEdge => Positional.Rear,
        AID.ArmorCrush => Positional.Flank,
        _ => Positional.Any
    };

    private AID NextMeleeGCDForTrueNorth(Enemy? primaryTarget)
    {
        if (primaryTarget == null)
            return AID.None;

        if (ComboLastMove == AID.GustSlash)
            return GetComboEnder(primaryTarget.Actor);

        return AID.None;
    }

    private bool IsLateEnoughForTrueNorth()
    {
        if (GCD <= 0)
            return false;

        return GCD <= MathF.Max(TrueNorthEmergencyWindow, TrueNorthLateWeaveWindow);
    }

    private bool ShouldUseTrueNorthNow(in Strategy strategy, Enemy? primaryTarget, bool basicComboOnly = false, BurstNinjutsuPlan burstNinjutsuPlan = BurstNinjutsuPlan.None)
    {
        if (strategy.TrueNorth.Value != TrueNorthStrategy.Auto)
            return false;

        if (!Unlocked(AID.TrueNorth) || Locks.AbilitiesLocked)
            return false;

        if (TrueNorthLeft > GCD)
            return false;

        if (ReadyIn(AID.TrueNorth) > AnimLock)
            return false;

        if (primaryTarget == null)
            return false;

        if (Player.DistanceToHitbox(primaryTarget) > 3)
            return false;

        if (Mudra.Left > 0 || TenChiJin.Left > 0 || _pendingNinjutsu != AID.None)
            return false;

        if (!basicComboOnly && WillUseNonPositionalGCDBeforeCombo(strategy, primaryTarget, burstNinjutsuPlan))
            return false;

        if (!IsLateEnoughForTrueNorth())
            return false;

        var nextAction = NextMeleeGCDForTrueNorth(primaryTarget);
        var required = PositionalForAction(nextAction);
        if (required == Positional.Any)
            return false;

        if (primaryTarget.Actor.Omnidirectional)
            return false;

        var current = GetCurrentPositional(primaryTarget.Actor);
        return current != required;
    }

    private bool WillUseNonPositionalGCDBeforeCombo(in Strategy strategy, Enemy? primaryTarget, BurstNinjutsuPlan burstNinjutsuPlan)
    {
        if (KassatsuActive && ShouldUseKassatsuNinjutsuNow(strategy, NumRangedAOETargets > 2) || _kassatsuQueuedThisFrame)
            return true;

        if (strategy.PhantomCannon.IsEnabled()
            && (DutyActionReadyIn(PhantomID.SilverCannon) <= GCD
                || DutyActionReadyIn(PhantomID.ShockCannon) <= GCD
                || DutyActionReadyIn(PhantomID.DarkCannon) <= GCD
                || DutyActionReadyIn(PhantomID.HolyCannon) <= GCD
                || DutyActionReadyIn(PhantomID.PhantomFire) <= GCD))
            return true;

        if (ShouldPrepareShadowWalkerForMugWindow(strategy))
            return true;

        if (_dokumoriPreKunaiActive && !RaitonFirstPreKunaiComplete())
            return _dokumoriPreKunaiUsePhantom || _dokumoriPreKunaiComboDone;

        if (PhantomKamaitachi > GCD
            && ShouldPK(BestRangedAOETarget)
            && !ShouldHoldPhantomKamaitachiForMugWindow(strategy, burstNinjutsuPlan))
            return true;

        if (Raiju.Stacks > 0 && !ShouldHoldRaijuForRaitonFirstPreKunai())
            return true;

        return Unlocked(AID.Raiton) && !KassatsuActive && ShouldUseAttackNinjutsuNow(strategy, NumRangedAOETargets > 2);
    }

    private static int NinjutsuMudraCount(AID ninjutsu) => Combos.TryGetValue(ninjutsu, out var combo) ? combo.Len : 2;

    private bool UseMudra(AID requestedNinjutsu, Enemy? target, bool startCondition = true, bool endCondition = true, int priority = 2, bool allowUntargetedSteps = false)
        => UseMudraDetailed(requestedNinjutsu, target, startCondition, endCondition, priority, allowUntargetedSteps) != MudraPushResult.None;

    // allowUntargetedSteps: press the mudra steps even without a target (preparing Suiton before a known return); the final Ninjutsu still needs one
    private MudraPushResult UseMudraDetailed(AID requestedNinjutsu, Enemy? target, bool startCondition = true, bool endCondition = true, int priority = 2, bool allowUntargetedSteps = false)
    {
        var ninjutsu = NormalizeNinjutsu(Mudra.Left > 0 && _pendingNinjutsu != AID.None ? _pendingNinjutsu : requestedNinjutsu);
        if (ninjutsu == AID.None)
            return MudraPushResult.None;

        var resolvedTarget = TargetForNinjutsu(ninjutsu, target);
        // a known target loss before the ninjutsu itself could be pressed would leave the mudra to time out and waste the charge;
        // the downtime Suiton preparation (untargeted steps) is exempt, it waits for the return on purpose. Only a loss still ahead
        // counts: a boss module whose HP-gated phase has outrun its expected duration reports its downtime as starting now while the
        // target is plainly still there, and trusting that would block every mudra until the phase ends.
        if (Mudra.Param == 0 && !allowUntargetedSteps && target != null && Mechanic.TargetLossIn > 0
            && Mechanic.LossWithin(GCD + NinjutsuMudraCount(ninjutsu) * NinBurstTiming.MudraStep + 0.1f))
            return MudraPushResult.None;
        (var aid, var tar) = PickMudra(ninjutsu, resolvedTarget, startCondition, endCondition, allowUntargetedSteps);
        if (aid == AID.None)
            return MudraPushResult.None;

        var finalNinjutsu = aid != AID.Ninjutsu ? AID.None
            : ninjutsu is AID.HyoshoRanryu or AID.GokaMekkyaku ? ninjutsu
            : CurrentNinjutsu != AID.Ninjutsu ? CurrentNinjutsu : ninjutsu;
        // refused by a status on the player (Amnesia refuses every mudra and ninjutsu): nothing is queued and the caller falls through to a
        // weaponskill; a sequence under way keeps its mudra for when the lock ends
        if (IsActionLocked(aid == AID.Ninjutsu ? finalNinjutsu : aid))
            return MudraPushResult.None;

        if (Mudra.Param == 0 || _pendingNinjutsu == AID.None)
            _pendingNinjutsu = ninjutsu;

        if (aid == AID.Ninjutsu)
        {
            // The pending Ninjutsu stays until the mudra is gone (reset at the top of Exec): a push is not a cast, and clearing it here
            // let the next frame's caller (the Kassatsu path after a downtime Suiton, say) finish these mudras as something they are not.
            PushGCD(finalNinjutsu, TargetForNinjutsu(finalNinjutsu, tar), priority);
            return MudraPushResult.FinalNinjutsu;
        }
        else
        {
            PushGCD(aid, tar, priority);
            return MudraPushResult.MudraStep;
        }
    }

    private (AID action, Enemy? target) PickMudra(AID mudra, Enemy? target, bool startCondition, bool endCondition, bool allowUntargetedSteps = false)
    {
        if (target != null)
            return PickMudraCore(mudra, target, startCondition, endCondition);
        if (!allowUntargetedSteps)
            return (AID.None, null);

        // without a target only the mudra steps can be pressed; the final Ninjutsu waits for the target to come back
        var (step, _) = PickMudraCore(mudra, null, startCondition, endCondition: false);
        return step == AID.Ninjutsu ? (AID.None, null) : (step, null);
    }

    private (AID action, Enemy? target) PickMudraCore(AID mudra, Enemy? target, bool startCondition, bool endCondition)
    {
        if (!Unlocked(mudra))
            return (AID.None, null);

        // no charges remaining and no kassatsu = we can't use it
        if (Mudra.Param == 0 && !HasMudraCharge && Kassatsu == 0)
            return (AID.None, null);

        // do nothing if start condition failed - since this could be something like checking ninjutsu CD, we skip it otherwise
        // (since ninjutsu goes on CD as soon as you press the first one)
        if (Mudra.Param == 0 && !startCondition)
            return (AID.None, null);

        // unrecognized action - this really shouldn't happen
        if (!Combos.TryGetValue(mudra, out var combo))
            return (AID.None, null);

        var (len, last) = combo;

        var ten1 = KassatsuActive ? AID.Ten2 : AID.Ten1;
        var jin1 = KassatsuActive ? AID.Jin2 : AID.Jin1;
        var chi1 = KassatsuActive ? AID.Chi2 : AID.Chi1;

        if (len == 1)
        {
            if (Mudras[0] == 0)
                return (ten1, null);
            else if (endCondition)
                return (AID.Ninjutsu, target);
        }

        if (len == 2)
        {
            // early exit
            if (Mudras[0] == last)
                return (AID.Ninjutsu, target);

            if (Mudras[0] == 0)
                return (last == 1 ? (Unlocked(jin1) ? jin1 : chi1) : ten1, null);

            if (Mudras[1] == 0)
                return (last == 1 ? AID.Ten2 : last == 2 ? AID.Chi2 : AID.Jin2, null);
            else if (endCondition)
                return (AID.Ninjutsu, target);
        }

        if (len == 3)
        {
            // early exit
            if (Mudras[0] == last || Mudras[1] == last)
                return (AID.Ninjutsu, target);

            if (Mudras[0] == 0)
                return (last == 1 ? jin1 : ten1, null);

            if (Mudras[1] == 0)
                return (Mudras[0] switch
                {
                    1 => last == 3 ? AID.Chi2 : AID.Jin2,
                    2 => last == 3 ? AID.Ten2 : AID.Jin2,
                    3 => last == 1 ? AID.Chi2 : AID.Ten2,
                    _ => AID.None
                }, null);

            if (Mudras[2] == 0)
                return (last == 1 ? AID.Ten2 : last == 2 ? AID.Chi2 : AID.Jin2, null);
            else if (endCondition)
                return (AID.Ninjutsu, target);
        }

        return (AID.None, null);
    }

    private void OGCD(in Strategy strategy, Enemy? primaryTarget, BurstNinjutsuPlan burstNinjutsuPlan)
    {
        if (!Player.InCombat)
        {
            if (strategy.Hide.IsEnabled()
                && Mudra.Left == 0
                && GCD == 0
                && Unlocked(AID.Ten1)
                && OnCooldown(AID.Ten1))
                PushOGCD(AID.Hide, Player);

            return;
        }

        if (primaryTarget == null)
            return;

        var buffsOk = strategy.Buffs != OffensiveStrategy.Delay;
        var ninkiSpendQueued = false;
        // both are pure functions of this frame's snapshot; evaluate them once and reuse below
        var spendNinkiBeforeDokumori = ShouldSpendNinkiBeforeDokumori(buffsOk);
        var spendNinkiBeforeMeisui = ShouldSpendNinkiBeforeMeisui(strategy);

        if (ShouldAllowUltimateZeroSecondBurstOGCDs(strategy))
            PushOpeningZeroSecondBurstOGCDs(primaryTarget);

        if (spendNinkiBeforeDokumori || spendNinkiBeforeMeisui)
        {
            // ahead of Dokumori (priority 100) when its 40 would overcap: both fit the same weave window, the spender has a 1s recast
            PushNinkiSpender(primaryTarget, spendNinkiBeforeDokumori ? 110 : 50);
            ninkiSpendQueued = true;
        }

        // spec rule 1: Dokumori's 20 s window is held when a known target loss would cut it; the opening one waits for its GCD bound
        if (buffsOk && TargetMugLeft == 0 && !Mechanic.ShouldHoldWindow(20f, 120f, GCDLength) && !OpenerDokumoriHeld)
            PushOGCD(MugAction, primaryTarget, priority: 100);

        if (ShouldUseTrickActionNow(strategy, buffsOk, burstNinjutsuPlan))
            // late weave trick during 2min windows with mug/dokumori active; otherwise use on cooldown.
            // Above every other oGCD but Dokumori and the spender that keeps Dokumori's ninki from overcapping: the queue's sort does
            // not keep the push order between equal priorities (its three-entry network moves the first entry past an equal one), so
            // at the default priority Kassatsu, Dream Within a Dream or a ninki spender queued in the same frame took the weave slot
            // GCD after GCD while Shadow Walker ran out and Kunai's Bane was never pressed (irregular harness a10n downtime=1 warmup=180
            // seed 1 all: Kunai's Bane queued and in range 136.7-138.9 s, lost to Zesho Meppo and Bhavacakra, pressed 13 s late)
            PushOGCD(TrickAction, primaryTarget, priority: 90, delay: TrickActionDelay(strategy));

        if (ShouldUsePotionNow(strategy, primaryTarget))
            PushPotion(strategy);

        // Bunshin (five shadow hits and Phantom Kamaitachi) is worth far more than a spender: the ordinary spend waits while Bunshin is
        // up and the gauge cannot pay for both (the action queue orders equal priorities arbitrarily)
        var bunshinNeedsNinki = buffsOk && Ninki < 2 * NinkiSpendCost && ShouldUseBunshinNow(strategy);

        if (!ninkiSpendQueued && !bunshinNeedsNinki && ShouldSpendHighNinkiDuringBurst())
        {
            PushNinkiSpender(primaryTarget, 50);
            ninkiSpendQueued = true;
        }

        if (ShouldUseDWaDNow(strategy, buffsOk))
            PushOGCD(Unlocked(AID.DreamWithinADream) ? AID.DreamWithinADream : AID.Assassinate, primaryTarget);

        if (ShouldUseKassatsuNow(strategy, burstNinjutsuPlan))
        {
            PushOGCD(AID.Kassatsu, Player);
            _kassatsuQueuedThisFrame = true;
        }

        // TCJ/Meisui are gated on the kunai/trick window (see InEvenBurstWindowForTenChiJinAndMeisui), so they keep working when buffs are delayed
        if (ShouldUseTenChiJinNow(strategy))
            PushOGCD(AID.TenChiJin, Player);

        if (!ninkiSpendQueued && spendNinkiBeforeMeisui)
        {
            PushNinkiSpender(primaryTarget, 50);
            ninkiSpendQueued = true;
        }

        if (!spendNinkiBeforeMeisui && CanUseMeisuiNowIgnoringNinki(strategy))
            PushOGCD(AID.Meisui, Player);

        if (ShouldUseTenriJindoNow())
        {
            var tenriTarget = NumRangedAOETargets > 1 ? BestRangedAOETarget ?? primaryTarget : primaryTarget ?? BestRangedAOETarget;
            if (tenriTarget != null)
                PushOGCD(AID.TenriJindo, tenriTarget);
        }

        if (buffsOk && ShouldUseBunshinNow(strategy))
            PushOGCD(AID.Bunshin, Player);

        if (!ninkiSpendQueued && !bunshinNeedsNinki && ShouldBhava(spendNinkiBeforeDokumori, spendNinkiBeforeMeisui))
            PushNinkiSpender(primaryTarget, Meisui > 0 ? 50 : 1);

        if (ShouldUseTrueNorthNow(strategy, primaryTarget, burstNinjutsuPlan: burstNinjutsuPlan))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.TrueNorth), Player, ActionQueue.Priority.Low + 0.5f);
    }

    private bool ShouldUseTrickActionNow(in Strategy strategy, bool buffsOk, BurstNinjutsuPlan plan)
    {
        if (!Hidden || TargetTrickLeft > 0)
            return false;

        // spec rule 1: Kunai's Bane's 15 s window is held when a known target loss would cut it
        if (Mechanic.ShouldHoldWindow(15f, 60f, GCDLength))
            return false;

        if (Mudra.Left > 0 || _pendingNinjutsu != AID.None)
            return false;

        if (OpenerKunaiHeld)
            return false;

        // FightRemaining (value-of-information experiment): end dump, no alignment with Dokumori or the next window
        if (EndDump)
            return true;

        // buffs delayed: mug/dokumori isn't being used, so there's no window to align to - use kunai/trick on cooldown (as upstream does)
        if (!buffsOk)
            return true;

        if (ShouldSkipOddKunaiForMugSync())
            return false;

        if (UseUltimateZeroSecondBurst(strategy))
        {
            if (TargetMugLeft > 0)
                return true;

            if (ReadyIn(MugAction) > 10)
                return KunaiWouldBeReadyForNextMugIfUsedNow();

            return false;
        }

        // Raiton first's two pre-Kunai GCDs come before it, unless the aligned time has already passed
        if (TargetMugLeft > 0)
            return !ShouldDelayTrickForRaitonFirst(plan) && ShouldUseTrickActionForMugEndAlign();

        if (ShouldDelayTrickForRaitonFirst(plan))
            return false;

        if (ReadyIn(MugAction) > 10)
            return KunaiWouldBeReadyForNextMugIfUsedNow();

        return false;
    }

    private bool ShouldUseTrickActionForMugEndAlign()
    {
        if (TargetMugLeft <= 0)
            return false;

        if (TargetTrickLeft > 0)
            return false;

        if (!Hidden)
            return false;

        if (Mudra.Left > 0 || _pendingNinjutsu != AID.None)
            return false;

        var atOrPastAlignedStart = TargetMugLeft <= KunaiStartMugLeftForEndAlign + KunaiEndAlignTolerance;
        var emergencyLate = TargetMugLeft <= GCD + AnimLock || ShadowWalkerRunsOutBeforeAlignment;
        return atOrPastAlignedStart || emergencyLate;
    }

    // Waiting for the aligned time only works while Shadow Walker lasts: once it would run out before the aligned time comes around
    // (a Suiton prepared for a window that a downtime or a late Dokumori pushed back), Kunai's Bane goes now rather than not at all.
    // The margin after the aligned time is a whole GCD: the delayed press can slip to the far side of the GCD that is rolling when the
    // aligned time comes (the queue does not weave into its last 0.65 s). It is a constant, so the verdict is the same on every frame of a
    // GCD: with the remaining GCD and the animation lock in it (as before), the verdict was true right after a weaponskill, when nothing
    // can be weaved, and false by the frame the lock cleared - the frame in which Kassatsu and the ninki spender, queued at the same
    // priority, took the slot instead, GCD after GCD, until Shadow Walker was gone and Kunai's Bane with it (irregular harness a11s
    // downtime=1 return warmup=120, seed 2 lockout: Kunai's Bane queued 134.7-139.1 s and never pressed, -5.6% on the scenario).
    private bool ShadowWalkerRunsOutBeforeAlignment
        => !HiddenStatus && ShadowWalker > 0 && ShadowWalker <= TargetMugLeft - KunaiStartMugLeftForEndAlign + GCDLength;

    private bool ShouldAllowUltimateZeroSecondBurstOGCDs(in Strategy strategy)
    {
        if (!UseUltimateZeroSecondBurst(strategy))
            return false;

        if (ShouldHoldCooldownForEvenBurstRecovery(SixtySecondRecoveryWindow))
            return false;

        if (CountdownRemaining > 0)
            return false;

        var openingWindow = CombatTimer <= 8;
        var evenBurstWindow = TargetMugLeft > 0 || ReadyIn(MugAction) <= GCD + AnimLock;
        if (!openingWindow && !evenBurstWindow)
            return false;

        return CanFitOpeningZeroSecondOGCDs();
    }

    private bool CanFitOpeningZeroSecondOGCDs()
    {
        if (GCD <= 0)
            return true;

        var totalAnimLock = 0f;

        if (Unlocked(AID.Kassatsu) && !KassatsuActiveOrQueued && ReadyIn(AID.Kassatsu) <= AnimLock)
        {
            var definition = ActionDefinitions.Instance.Spell(AID.Kassatsu);
            if (definition == null)
                return false;
            totalAnimLock += definition.InstantAnimLock;
        }

        if (TargetMugLeft <= 0 && ReadyIn(MugAction) <= AnimLock)
        {
            var definition = ActionDefinitions.Instance.Spell(MugAction);
            if (definition == null)
                return false;
            totalAnimLock += definition.InstantAnimLock;
        }

        if (Hidden && TargetTrickLeft <= 0 && ReadyIn(TrickAction) <= AnimLock)
        {
            var definition = ActionDefinitions.Instance.Spell(TrickAction);
            if (definition == null)
                return false;
            totalAnimLock += definition.InstantAnimLock;
        }

        return totalAnimLock <= 0 || CanWeave(0, totalAnimLock);
    }

    private void PushOpeningZeroSecondBurstOGCDs(Enemy? primaryTarget)
    {
        if (primaryTarget == null)
            return;

        if (Unlocked(AID.Kassatsu) && !KassatsuActiveOrQueued && ReadyIn(AID.Kassatsu) <= AnimLock)
        {
            PushOGCD(AID.Kassatsu, Player, priority: 3100);
            _kassatsuQueuedThisFrame = true;
        }

        if (TargetMugLeft <= 0 && ReadyIn(MugAction) <= AnimLock)
            PushOGCD(MugAction, primaryTarget, priority: 3099);

        if (Hidden && TargetTrickLeft <= 0 && ReadyIn(TrickAction) <= AnimLock)
            PushOGCD(TrickAction, primaryTarget, priority: 3098);
    }

    private float TrickActionDelay(in Strategy strategy)
    {
        if (UseUltimateZeroSecondBurst(strategy))
            return 0;

        if (TargetMugLeft <= 0 || EndDump)
            return 0;

        // Shadow Walker would be gone by the aligned time: now, not at a time that no weave slot before its expiry can reach
        if (ShadowWalkerRunsOutBeforeAlignment)
            return 0;

        var delay = TargetMugLeft - KunaiStartMugLeftForEndAlign;
        if (delay <= 0)
            return 0;

        return MathF.Min(MathF.Max(delay, 0), MathF.Max(0, GCD - 0.1f));
    }

    private bool ShouldUsePotionNow(in Strategy strategy, Enemy? primaryTarget)
    {
        if (strategy.Potion.Value != PotionStrategy.EvenBurst)
            return false;

        if (PotionType == PotionType.None)
            return false;

        if (PotionCD > AnimLock)
            return false;

        if (PotionLeft > 0)
            return false;

        if (primaryTarget == null)
            return false;

        if (TenChiJin.Left > 0)
            return false;

        return ShouldUsePotionInEvenBurst(strategy, primaryTarget);
    }

    private bool InEvenBurstWindow()
        => TargetMugLeft > GCD || ReadyIn(MugAction) <= GCD + AnimLock;

    private bool InEvenBurstWindowForTenChiJinAndMeisui()
    {
        // kunai/trick is the stronger buff and is the one that is still used when mug/dokumori is delayed
        return TargetTrickLeft > GCD || TargetMugLeft > GCD;
    }

    private bool IsEvenBurstTiming()
    {
        if (CombatTimer < 15)
            return true;

        var cycle = CombatTimer % 120f;
        return cycle <= 15f || cycle >= 115f;
    }

    private bool ShouldUsePotionInEvenBurst(in Strategy strategy, Enemy? primaryTarget)
    {
        if (primaryTarget == null)
            return false;

        if (BurstRecoveryActive && !InEvenBurstWindow())
            return false;

        if (!IsEvenBurstTiming() && !InEvenBurstWindow())
            return false;

        if (ShouldPrepareShadowWalkerForMugWindow(strategy))
            return false;

        if (Mudra.Left > 0 || _pendingNinjutsu != AID.None)
            return false;

        if (UseUltimateZeroSecondBurst(strategy))
            return ShouldUsePotionInUltimateZeroSecondBurst(primaryTarget);

        return ShouldUsePotionInNormalEvenBurst(primaryTarget);
    }

    private bool ShouldUsePotionInUltimateZeroSecondBurst(Enemy? primaryTarget)
    {
        if (primaryTarget == null)
            return false;

        if (!Hidden)
            return false;

        if (TargetTrickLeft > GCD || TargetMugLeft > GCD && ReadyIn(TrickAction) > AnimLock)
            return true;

        if (ReadyIn(MugAction) <= GCD + AnimLock && ReadyIn(TrickAction) <= GCD + AnimLock && ReadyIn(MugAction) > AnimLock && ReadyIn(TrickAction) > AnimLock)
            return true;

        return false;
    }

    // The potion's 30s start a GCD before Dokumori, so they cover the GCDs before Kunai's Bane, Kunai's Bane itself and the opening
    // ninjutsu of its window (pressed with Kunai's Bane it missed those and ran on into filler: harness +1.0% rDPS with potions).
    private bool ShouldUsePotionInNormalEvenBurst(Enemy? primaryTarget)
    {
        if (primaryTarget == null)
            return false;
        if (TargetMugLeft > 0)
            return true;
        // the opening Dokumori waits for its GCD bound: the potion follows that expected time, not the cooldown
        var mugIn = OpenerDokumoriHeld ? MathF.Max(ReadyIn(MugAction), GCD + (_openerDokumoriMin - _openerGCDs - 1) * GCDLength) : ReadyIn(MugAction);
        return mugIn <= GCD + GCDLength;
    }

    private void PushPotion(in Strategy strategy)
    {
        if (strategy.Potion.Value != PotionStrategy.EvenBurst)
            return;

        // a stun-type lock refuses items too; Amnesia does not
        if (PotionCD > AnimLock || PotionLeft > 0 || Locks.AllLocked)
            return;

        Hints.ActionsToExecute.Push(ActionDefinitions.IDPotionDex, Player, ActionQueue.Priority.Low + 5);
    }

    private bool ShouldUseKassatsuNow(in Strategy strategy, BurstNinjutsuPlan plan)
    {
        // refused right now (Amnesia): not queued, and not marked as queued for this frame either
        if (Locks.AbilitiesLocked)
            return false;

        // spec rule 1: Kassatsu (15 s) is held when a known target loss would cut it
        if (Mechanic.ShouldHoldWindow(15f, 60f, GCDLength))
            return false;

        if (ShouldAllowUltimateZeroSecondBurstOGCDs(strategy))
            return Unlocked(AID.Kassatsu) && !KassatsuActiveOrQueued && ReadyIn(AID.Kassatsu) <= AnimLock;

        if (!Unlocked(AID.Kassatsu) || KassatsuActiveOrQueued || ReadyIn(AID.Kassatsu) > AnimLock || Mudra.Left > 0)
            return false;

        // FightRemaining (value-of-information experiment): end dump, no wait for Shadow Walker / Kunai's Bane
        if (EndDump && TenChiJin.Left <= 0 && _pendingNinjutsu == AID.None)
            return true;

        if (ShouldPrepareShadowWalkerForMugWindow(strategy))
            return false;

        if (ShouldHoldCooldownForEvenBurstRecovery(SixtySecondRecoveryWindow))
            return false;

        if (ShouldSkipOddKunaiForMugSync())
            return false;

        // Kassatsu turns the next mudra into its ninjutsu, so it never goes in during a mudra sequence or Ten Chi Jin
        if (TenChiJin.Left > 0)
            return false;

        // without Hyosho Ranryu / Goka Mekkyaku it only makes a normal ninjutsu free and 30% stronger: use it in the buff windows
        if (!Unlocked(AID.HyoshoRanryu) || !CanTrickInCombat)
            return TargetMugLeft > GCD || TargetTrickLeft > GCD || ReadyIn(MugAction) > 15 && ReadyIn(TrickAction) > 15;

        if (TargetTrickLeft > 0)
            return true;

        // Raiton first presses it only once Kunai's Bane is up
        if (plan == BurstNinjutsuPlan.RaitonFirst)
            return false;

        var kunaiIn = ExpectedTrickActionIn(strategy, NumRangedAOETargets > 2);
        if (kunaiIn > KassatsuHoldForKunaiLimit)
            return true;

        // Kassatsu first: shortly before Kunai's Bane, once Shadow Walker for it is up (a Suiton still to come would spend Kassatsu)
        return plan == BurstNinjutsuPlan.KassatsuFirst && kunaiIn <= KassatsuLeadBeforeKunai && (HiddenStatus || ShadowWalker > kunaiIn);
    }

    private bool ShouldUseTenChiJinNow(in Strategy strategy)
    {
        // inside the Kunai's Bane window (its ninjutsu get the 10%); with buffs delayed Dokumori's window still counts. Only the first
        // ninjutsu has to fit: holding Ten Chi Jin (120s) for the next window delays every later use, which costs more than the 10% on
        // the ones that fall outside (harness: requiring all three +0.2% worse)
        // FightRemaining (value-of-information experiment): end dump, outside any window when the three ninjutsu still fit
        if (!(TargetTrickLeft > GCD || !CanTrickInCombat && TargetMugLeft > GCD || EndDump && FightHasAtLeast(3 * GCDLength)))
            return false;

        // spec rule 3: only a target loss inside the three GCDs breaks the sequence. A forced move does not: all three ninjutsu are
        // instant casts with a range of 20-25 (game data), so they can be pressed while dodging. Holding for a predicted forced move
        // delayed the burst by several seconds on every dodge (random-disengage matrix, forecast on: +0.09% / +0.10% without the hold)
        if (Mechanic.Enabled && Mechanic.LossWithin(3 * GCDLength))
            return false;
        if (Mechanic.ShouldHoldWindow(6f, 120f, GCDLength))
            return false;

        if (!Unlocked(AID.TenChiJin) || ReadyIn(AID.TenChiJin) > AnimLock)
            return false;

        // two charges would sit capped through the three Ten Chi Jin GCDs: a Raiton goes first
        if (MudraChargesCapped)
            return false;

        if (Mudra.Left > 0 || _pendingNinjutsu != AID.None)
            return false;

        // Hyosho Ranryu first: Kassatsu cannot be active during Ten Chi Jin, and one that is back within this window is used before it
        if (KassatsuActiveOrQueued)
            return false;

        if (CanTrickInCombat && Unlocked(AID.HyoshoRanryu) && ReadyIn(AID.Kassatsu) < TargetTrickLeft - TenChiJinSequenceTime - GCD)
            return false;

        if (TenChiJin.Left > 0)
            return false;

        if (ShouldPrepareShadowWalkerForMugWindow(strategy))
            return false;

        return true;
    }

    private bool ShouldUseTenriJindoNow()
    {
        if (TenriJindo <= 0 || TenChiJin.Left > 0)
            return false;

        // spec rule 2: a Tenri Jindo Ready that would expire during a long target loss is used before it
        if (Mechanic.ExpiresDuringLoss(TenriJindo))
            return true;

        if (TenriJindo <= GCD + AnimLock)
            return true;

        if (TargetMugLeft > GCD || TargetTrickLeft > GCD)
            return true;

        if (ReadyIn(MugAction) <= 10)
            return false;

        return true;
    }

    private bool ShouldUseBunshinNow(in Strategy strategy)
    {
        if (!Unlocked(AID.Bunshin) || ReadyIn(AID.Bunshin) > AnimLock || StatusLeft(SID.Bunshin) > 0)
            return false;

        // spec rule 1: Bunshin's 30 s window is held when a known target loss would cut it
        if (Mechanic.ShouldHoldWindow(30f, 90f, GCDLength))
            return false;

        if (Ninki < 50)
            return false;

        // FightRemaining (value-of-information experiment): end dump; five shadow hits and Phantom Kamaitachi need about five GCDs
        if (EndDump && FightHasAtLeast(4 * GCDLength))
            return true;

        if (ShouldPrepareShadowWalkerForMugWindow(strategy) && Ninki <= 85)
            return false;

        if (TargetMugLeft > GCD || TargetTrickLeft > GCD)
            return true;

        if (ShouldHoldCooldownForEvenBurstRecovery(BunshinRecoveryWindow))
            return false;

        if (Ninki > 85)
            return true;

        if (ReadyIn(MugAction) > 20)
            return true;

        return false;
    }

    private bool ShouldUseDWaDNow(in Strategy strategy, bool buffsOk)
    {
        if (AssassinateCD > AnimLock)
            return false;

        // FightRemaining (value-of-information experiment): end dump
        if (EndDump)
            return true;

        // buffs delayed: nothing to align to, use on cooldown (upstream: Hidden && (MugOnCD || !buffsOk))
        if (!buffsOk)
            return true;

        if (ShouldPrepareShadowWalkerForMugWindow(strategy))
            return false;

        if (ShouldHoldCooldownForEvenBurstRecovery(SixtySecondRecoveryWindow))
            return false;

        if (!CanTrickInCombat)
            return true;

        // inside the Kunai's Bane window; it shares the 60s recast, so it waits for a window that is close
        if (TargetTrickLeft > 0)
            return true;

        return ExpectedTrickActionIn(strategy, NumRangedAOETargets > 2) > DreamHoldForKunaiLimit;
    }

    private const int MaxNinki = 100;
    private const int NinkiSpendCost = 50;
    private const int DokumoriNinkiGain = 40;
    private const int MeisuiNinkiGain = 50;
    private const int PhantomKamaitachiNinkiGain = 10;
    private const int RaijuNinkiGain = 5;
    private const int ShukihoNinkiGainPerWeaponskill = 5;
    private const int HighNinkiBurstSpendThreshold = 95;

    private bool CanSpendNinki()
        => Ninki >= NinkiSpendCost;

    private bool WillOvercapNinki(int gain)
        => Ninki + gain > MaxNinki;

    private int EstimateNinkiGainBefore(float seconds)
    {
        var gain = 0;

        if (seconds <= 0)
            return gain;

        var gcds = Math.Max(0, (int)MathF.Floor(seconds / MathF.Max(GCDLength, 0.1f)));

        if (gcds > 0 && PhantomKamaitachi > GCD && CanFitGCD(seconds, 0))
        {
            gain += PhantomKamaitachiNinkiGain;
            gcds--;
        }

        if (gcds > 0 && Raiju.Stacks > 0 && CanFitGCD(seconds, 0))
        {
            gain += RaijuNinkiGain;
            gcds--;
        }

        if (_shukihoUnlocked && gcds > 0)
            gain += EstimateComboWeaponskillNinkiGain(gcds);

        return gain;
    }

    private int EstimateComboWeaponskillNinkiGain(int gcds)
    {
        if (gcds <= 0)
            return 0;

        if (NumAOETargets >= MeleeAOEMinTargets)
            return gcds * ShukihoNinkiGainPerWeaponskill;

        var finisherGain = Player.Level >= 84 ? 15 : Player.Level >= 78 ? 10 : ShukihoNinkiGainPerWeaponskill;
        var nextComboStep = ComboLastMove == AID.SpinningEdge ? 1 : ComboLastMove == AID.GustSlash ? 2 : 0;
        var gain = 0;

        for (var i = 0; i < gcds; ++i)
        {
            gain += nextComboStep == 2 ? finisherGain : ShukihoNinkiGainPerWeaponskill;
            nextComboStep = (nextComboStep + 1) % 3;
        }

        return gain;
    }

    private bool WouldUsePhantomBefore(float seconds)
        => PhantomKamaitachi > GCD
        && MathF.Floor(seconds / MathF.Max(GCDLength, 0.1f)) >= 1
        && CanFitGCD(seconds, 0);

    private bool WouldUseRaijuBefore(float seconds)
    {
        if (Raiju.Stacks <= 0 || !CanFitGCD(seconds, 0))
            return false;

        var availableGCDs = Math.Max(0, (int)MathF.Floor(seconds / MathF.Max(GCDLength, 0.1f)));
        if (WouldUsePhantomBefore(seconds))
            availableGCDs--;

        return availableGCDs > 0;
    }

    private bool ShouldSpendNinkiBeforeDokumori(bool buffsOk)
    {
        if (!buffsOk)
            return false;

        if (MugAction != AID.Dokumori)
            return false;

        if (!CanSpendNinki())
            return false;

        if (TargetMugLeft > 0)
            return false;

        var mugIn = ReadyIn(MugAction);
        if (mugIn > GCD + GCDLength + AnimLock)
            return false;

        var projectedGain = EstimateNinkiGainBeforeNextPostDokumoriSpend(mugIn);
        return WillOvercapNinki(projectedGain);
    }

    private int EstimateNinkiGainBeforeNextPostDokumoriSpend(float mugIn)
    {
        var gain = DokumoriNinkiGain + EstimateNinkiGainBefore(mugIn);

        if (CanWeaveNinkiSpenderAfterDokumori(mugIn))
            return gain;

        if (PhantomKamaitachi > GCD && !WouldUsePhantomBefore(mugIn))
            return gain + PhantomKamaitachiNinkiGain;

        if (Raiju.Stacks > 0 && !WouldUseRaijuBefore(mugIn))
            return gain + RaijuNinkiGain;

        if (_shukihoUnlocked && MudraCharges == 0 && Mudra.Left <= 0 && TenChiJin.Left <= 0 && !KassatsuActiveOrQueued && _pendingNinjutsu == AID.None)
            return gain + ShukihoNinkiGainPerWeaponskill;

        return gain;
    }

    private bool CanWeaveNinkiSpenderAfterDokumori(float mugIn)
    {
        var action = ShouldUseNinkiAOE() ? HellfrogAction : BhavacakraAction;
        var dokumori = ActionDefinitions.Instance.Spell(MugAction);
        var spender = ActionDefinitions.Instance.Spell(action);
        return dokumori != null && spender != null && CanWeave(MathF.Max(mugIn, ReadyIn(action)), dokumori.InstantAnimLock + spender.InstantAnimLock);
    }

    private bool CanUseMeisuiNowIgnoringNinki(in Strategy strategy)
    {
        var recoveringCompletedTenChiJin = ShadowWalker > 0 && Unlocked(AID.TenChiJin) && ReadyIn(AID.TenChiJin) > 60;
        if (!InEvenBurstWindowForTenChiJinAndMeisui() && !recoveringCompletedTenChiJin)
            return false;

        if (!Unlocked(AID.Meisui))
            return false;

        if (TenChiJin.Left > 0)
            return false;

        if (ReadyIn(AID.Meisui) > AnimLock)
            return false;

        if (Unlocked(AID.TenChiJin) && ReadyIn(AID.TenChiJin) <= 10)
            return false;

        if (ShadowWalker <= 0 || ReadyIn(TrickAction) <= ShadowWalker)
            return false;

        if (InEvenBurstWindowForTenChiJinAndMeisui() && ShouldUseTenChiJinNow(strategy))
            return false;

        return true;
    }

    private bool ShouldSpendNinkiBeforeMeisui(in Strategy strategy)
    {
        if (!CanSpendNinki())
            return false;

        if (!CanUseMeisuiNowIgnoringNinki(strategy))
            return false;

        var projectedGain = EstimateNinkiGainBeforeNextPostMeisuiSpend();
        return WillOvercapNinki(projectedGain) || Ninki + projectedGain == MaxNinki && !CanWeaveNinkiSpenderAfterMeisui();
    }

    private int EstimateNinkiGainBeforeNextPostMeisuiSpend()
    {
        var meisuiIn = ReadyIn(AID.Meisui);
        var gain = MeisuiNinkiGain + EstimateNinkiGainBefore(meisuiIn);
        var firstPostMeisuiSpendIn = meisuiIn + AnimLock;

        if (GCD > firstPostMeisuiSpendIn)
            return gain;

        if (PhantomKamaitachi > GCD && !WouldUsePhantomBefore(meisuiIn))
            return gain + PhantomKamaitachiNinkiGain;

        if (Raiju.Stacks > 0 && !WouldUseRaijuBefore(meisuiIn))
            return gain + RaijuNinkiGain;

        if (_shukihoUnlocked && MudraCharges == 0 && Mudra.Left <= 0 && TenChiJin.Left <= 0 && !KassatsuActiveOrQueued && _pendingNinjutsu == AID.None)
            return gain + ShukihoNinkiGainPerWeaponskill;

        return gain;
    }

    private bool CanWeaveNinkiSpenderAfterMeisui()
    {
        var action = ShouldUseNinkiAOE() ? HellfrogAction : BhavacakraAction;
        var meisui = ActionDefinitions.Instance.Spell(AID.Meisui);
        var spender = ActionDefinitions.Instance.Spell(action);
        return meisui != null && spender != null && CanWeave(ReadyIn(action), meisui.InstantAnimLock + spender.InstantAnimLock);
    }

    private bool ShouldSpendHighNinkiDuringBurst()
    {
        if (!CanSpendNinki())
            return false;

        if (Ninki < HighNinkiBurstSpendThreshold)
            return false;

        return TargetMugLeft > AnimLock || TargetTrickLeft > AnimLock;
    }

    private void PushNinkiSpender(Enemy? primaryTarget, int priority = 1)
    {
        if (ShouldUseNinkiAOE())
            PushOGCD(HellfrogAction, BestNinkiAOETarget ?? primaryTarget, priority);
        else
            PushOGCD(BhavacakraAction, primaryTarget, priority);
    }

    private bool ShouldUseNinkiAOE()
    {
        if (!Unlocked(AID.Bhavacakra))
            return true;

        // hellfrog (160/300 per target) only beats bhavacakra/zesho meppo (380/700) from 3 targets
        return NumNinkiAOETargets > 2;
    }

    private bool ShouldBhava(bool spendNinkiBeforeDokumori, bool spendNinkiBeforeMeisui)
    {
        if (!CanSpendNinki())
            return false;

        if (Meisui > 0)
            return true;

        // FightRemaining (value-of-information experiment): end dump
        if (EndDump)
            return true;

        if (TargetMugLeft > AnimLock || TargetTrickLeft > AnimLock)
            return true;

        // outside burst only spend to avoid overcapping (immediately, or from the upcoming dokumori/meisui gain)
        return spendNinkiBeforeMeisui || spendNinkiBeforeDokumori || Ninki > 85;
    }

    private (Positional, bool) GetNextPositional(Actor? primaryTarget)
    {
        if (!Unlocked(AID.AeolianEdge) || primaryTarget == null)
            return (Positional.Any, false);

        return (GetComboEnder(primaryTarget) == AID.AeolianEdge ? Positional.Rear : Positional.Flank, ComboLastMove == AID.GustSlash);
    }
}
