using BossMod.RPR;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.xan.Custom;

public sealed class RPR(RotationModuleManager manager, Actor player) : Attackxan<AID, TraitID, RPR.Strategy>(manager, player, PotionType.Strength)
{
    public struct Strategy : IStrategyCommon
    {
        [Track("ターゲット選択")]
        public Track<Targeting> Targeting;
        [Track("範囲攻撃")]
        public Track<AOEStrategy> AOE;
        [Track("ローテーションモード")]
        public Track<RotationModeStrategy> RotationMode;

        [Track("スキル回しモード")]
        public Track<SkillRotationStrategy> SkillRotation;

        [Track("開幕バースト", MinLevel = 72, Action = AID.ArcaneCircle)]
        public Track<OpenerBurstStrategy> OpenerBurst;

        [Track("アルケインサークル", MinLevel = 72, Action = AID.ArcaneCircle)]
        public Track<OffensiveStrategy> Buffs;

        [Track("POT", InternalName = "POT", Item = 1049234)]
        public Track<PotionUseStrategy> Potion;

        [Track("レムールシュラウド", Action = AID.Enshroud, MinLevel = 80)]
        public Track<OffensiveStrategy> Enshroud;

        [Track("コムニオ", Action = AID.Communio, MinLevel = 90, Targets = ActionTargets.Hostile)]
        public Track<EnabledByDefault> Communio;

        [Track("ソウルスライス", Actions = [AID.SoulSlice, AID.SoulScythe], MinLevel = 60)]
        public Track<SliceStrategy> Slice;

        [Track("ソウルゲージ", Actions = [AID.BloodStalk, AID.GrimSwathe, AID.Gluttony], MinLevel = 50)]
        public Track<RedGaugeStrategy> RedGauge;

        [Track("ソウルリーバー", Actions = [AID.Gallows, AID.Guillotine, AID.ExecutionersGallows, AID.ExecutionersGuillotine, AID.Gibbet, AID.ExecutionersGibbet], MinLevel = 70)]
        public Track<SoulReaverStrategy> Reaver;

        [Track("プレンティフルハーベスト", Action = AID.PlentifulHarvest, MinLevel = 88, Targets = ActionTargets.Hostile)]
        public Track<EnabledByDefault> PH;

        [Track("ハーベストムーン", Action = AID.HarvestMoon, MinLevel = 82, Targets = ActionTargets.Hostile)]
        public Track<OffensiveStrategy> HM;

        [Track("ペルフェクティオ", Action = AID.Perfectio, MinLevel = 100, Targets = ActionTargets.Hostile)]
        public Track<PerfectioStrategy> Perf;

        [Track("ハルパー", Action = AID.Harpe)]
        public Track<HarpeStrategy> Harpe;

        [Track("トゥルーノース", Action = AID.TrueNorth, MinLevel = 50)]
        public Track<TrueNorthStrategy> TrueNorth;

        [Track("戦闘中ソウルソウ", Action = AID.Soulsow, MinLevel = 82)]
        public Track<DisabledByDefault> Soulsow;

        [Track("アルケインクレスト自動", InternalName = "Crest", Action = AID.ArcaneCrest, MinLevel = 40)]
        public Track<EnabledByDefault> AutoCrest;

        [Track("AIHINTS", InternalName = "AIHints")]
        public Track<AIHintsStrategy> AIHints;

        [Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)]
        public Track<MechanicHintStrategy> MechanicHints;

        readonly Targeting IStrategyCommon.Targeting => Targeting.Value;
        readonly AOEStrategy IStrategyCommon.AOE => AOE.Value;
    }

    public enum RotationModeStrategy
    {
        [Option("おすすめ: フルモード")]
        FullMode,
        [Option("基本モード")]
        BasicMode
    }

    public enum SkillRotationStrategy
    {
        [Option("通常")]
        Normal,
        [Option("絶妖星乱舞")]
        DancingMad,
        [Option("ウィンダス・ザ・サードウォーク（上位）")]
        WindurstThirdWalk
    }

    public enum AIHintsStrategy
    {
        [Option("使用する")]
        Use,
        [Option("使用しない")]
        Disable
    }

    public enum OpenerBurstStrategy
    {
        [Option("おすすめ: 2GCDバースト")]
        Normal,
        [Option("2.5秒バースト")]
        TwoPointFiveSecondBurst,
        [Option("0秒バースト")]
        ZeroSecondBurst
    }

    public enum TrueNorthStrategy
    {
        [Option("おすすめ: 自動")]
        Auto,
        [Option("オフ")]
        Off
    }

    public enum PotionUseStrategy
    {
        [Option("おすすめ: 開幕、偶数バースト")]
        OpenerAndEvenBurst,
        [Option("開幕以外、偶数バースト")]
        EvenBurstExceptOpener,
        [Option("使用しない")]
        Off
    }

    public enum SliceStrategy
    {
        [Option("おすすめ: 自動", Targets = ActionTargets.Hostile)]
        Automatic,
        [Option("使用しない")]
        Delay,
        [Option("強制使用", Targets = ActionTargets.Hostile)]
        Force
    }

    public enum RedGaugeStrategy
    {
        [Option("おすすめ: 自動", Targets = ActionTargets.Hostile)]
        Automatic,
        [Option("グラトニー温存", Targets = ActionTargets.Hostile, MinLevel = 76)]
        ReserveGluttony,
        [Option("強制使用", Targets = ActionTargets.Hostile)]
        Force,
        [Option("使用しない")]
        Delay
    }

    public enum SoulReaverStrategy
    {
        [Option("おすすめ: ソウルリーバー中は通常GCDを止める")]
        Automatic,
        [Option("通常GCDでソウルリーバーを破棄")]
        ForceBreak
    }

    public enum HarpeStrategy
    {
        [Option("強化ハルパー時のみ自動", Targets = ActionTargets.Hostile)]
        Automatic,
        [Option("使用しない")]
        Forbid,
        [Option("射程外で使用", Targets = ActionTargets.Hostile)]
        Ranged,
    }

    public enum PerfectioStrategy
    {
        [Option("おすすめ: シナジー中に使用", Targets = ActionTargets.Hostile)]
        Automatic,
        [Option("近接不可用に温存", Targets = ActionTargets.Hostile)]
        Ranged,
        [Option("使用しない")]
        Delay,
    }

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("xan RPR [Custom]", "Reaper", "Standard rotation (xan)|Melee", "xan", RotationModuleQuality.Basic, BitMask.Build(Class.RPR), 100).WithStrategies<Strategy>();
    }

    public int RedGauge;
    public int BlueGauge;
    public bool Soulsow;
    public float EnshroudLeft;
    public int BlueSouls;
    public int PurpleSouls;
    public bool SoulReaverActive;
    public bool Executioner;
    public int ReaverStacks;
    public float EnhancedGallows;
    public float EnhancedGibbet;
    public (float Left, int Stacks) ImmortalSacrifice;
    public float BloodsownCircle;
    public float ArcaneCircleLeft;
    public float IdealHost;
    public float EnhancedCrossReaping;
    public float EnhancedHarpe;
    public float Oblatio;
    public float PerfectioParata;

    public float TargetDDLeft;
    public float ShortestNearbyDDLeft;

    public int NumAOETargets; // melee
    public int NumConeTargets; // grim swathe, guillotine

    private Enemy? BestRangedAOETarget;
    private Enemy? BestConeTarget;
    private Enemy? BestLineTarget;
    private Enemy? _currentFallbackTarget;
    private bool _reaverGate; // ReaverOrExecutioner unless the strategy forces the reaver to be broken
    private bool _manualTargeting;
    private bool _hasMechanicHint;
    private ExternalMechanicHintSnapshot _mechanicHint;
    private bool _hasEncounterHint;
    private ExternalEncounterHintSnapshot _encounterHint;
    private Type? _finalDutyBossModuleType;
    private uint _finalDutyBossOID;
    private bool _finalDutyBossResult;
    private readonly List<TargetableWindow> _fallbackTargetableWindows = new(1);
    // one scratch list per call site of the planner's targetable windows (sampled several times a frame), so none aliases another
    private readonly List<TargetableWindow> _timelineBurstWindows = [];
    private readonly List<TargetableWindow> _dancingMadP4Windows = [];
    private readonly List<TargetableWindow> _evenBurstRebuildWindows = [];
    private readonly List<DamageCooldownSnapshot> _damageCooldownScratch = [];
    private PositionCheck? _lineTargetCheck;
    private PositionCheck? _coneTargetCheck;
    private PositionCheck? _selfCircleTargetCheck;
    private (int Level, int SkillSpeed, int SpellSpeed, int Haste) _gcdLengthsKey;
    private float _cachedAttackGCDLength;
    private float _cachedSpellGCDLength;

    // ActionSpeed reads the level sheet; reuse both results until an actual input changes.
    private new float GCDLength
    {
        get
        {
            UpdateGCDLengths();
            return _cachedAttackGCDLength;
        }
    }

    private new float SpellGCDLength
    {
        get
        {
            UpdateGCDLengths();
            return _cachedSpellGCDLength;
        }
    }

    private void UpdateGCDLengths()
    {
        var stats = World.Client.PlayerStats;
        var key = (Player.Level, stats.SkillSpeed, stats.SpellSpeed, stats.Haste);
        if (_gcdLengthsKey == key && _cachedAttackGCDLength > 0)
            return;

        _gcdLengthsKey = key;
        _cachedAttackGCDLength = ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, Player.Level);
        _cachedSpellGCDLength = ActionSpeed.GCDRounded(stats.SpellSpeed, stats.Haste, Player.Level);
    }

    public new bool CanFitGCD(float duration, int extraGCDs = 0) => GCD + GCDLength * extraGCDs < duration;

    private PositionCheck LineTargetCheck => _lineTargetCheck ??= LineTarget;
    private PositionCheck ConeTargetCheck => _coneTargetCheck ??= ConeTarget;
    private PositionCheck SelfCircleTargetCheck => _selfCircleTargetCheck ??= SelfCircleTarget;

    public enum GCDPriority
    {
        None = 0,
        Soulsow = 1,
        Harpe = 50,
        EnhancedHarpe = 100,
        HarvestMoon = 150,
        PerfectioRanged = 175,
        Filler = 300,
        FillerAOE = 301,
        SoulSlice = 500,
        DDExtend = 650,
        ComboPreserve = 700,
        Harvest = 750,
        LateBurstSoulSliceBeforePH = 760,
        // Force-only Harvest Moon: above normal fillers, below burst-critical GCDs.
        HarvestMoonForce = 800,
        SoulSliceChargeCap = 825,
        Perfectio = 850,
        DDExpiring = 900,
        ComboRecovery = 910,
        PerfectioExpiring = 920,
        HarvestExpiring = 930,
        EnshroudMove = 940,
        Lemure = 950,
        DDPreArcaneEnshroud = 955,
        Communio = 960,
        Reaver = 970,
        BurstProcExpiring = 980,
        Max = 990
    }

    public enum OGCDPriority
    {
        Default = 1,
        ArcaneCrest = 5,
        TrueNorth = 10,
        BloodStalk = 50,
        Gluttony = 60,
        Enshroud = 70,
        BloodStalkBeforeEnshroud = 75,
        Lemure = 80,
        Sacrificium = 90,
        ArcaneCircle = 100,
        Potion = 110,
        ArcaneCircleBeforePotion = 120,
        EnshroudBeforeSoulOvercap = 130
    }

    private enum TerminalWeaveAction
    {
        None,
        Potion,
        ArcaneCircle,
        Sacrificium,
        LemuresScythe,
        LemuresSlice
    }

    private enum DancingMadOpenerStage
    {
        Inactive,
        DeathsDesign,
        SoulSlice,
        Gluttony,
        ExecutionersGallows,
        ExecutionersGibbet,
        PlentifulHarvest,
        Enshroud,
        Sacrificium,
        VoidReaping1,
        CrossReaping1,
        LemuresSlice1,
        VoidReaping2,
        CrossReaping2,
        LemuresSlice2,
        Communio,
        Perfectio,
        FollowupSoulSlice,
        FollowupStalk,
        FollowupGallows,
        FollowupDeathsDesign,
        Complete
    }

    private enum EvenBurstPlentifulHarvestState
    {
        Inactive,
        Required,
        Used
    }

    private const float OpenerHarpeStartIn = 1.7f;
    private const float NormalOpenerLateArcaneCircleCoverage = 21.0f; // 20 s buff + the weave slot after Soul Slice
    private const float BurstReferenceGCDLength = 2.49f;
    private const float PreArcaneEnshroudEntryWindow = 4.0f;
    private const float PreArcaneEnshroudWeaveSafety = 0.1f;
    private const float EnshroudGCDLength = 1.5f;
    private const float PostPerfectioEnshroudWindow = 8.5f;
    private const float EvenBurstDDSecondRefreshSafety = 1.5f;
    private const float EvenBurstRemainingAfterArcaneCircle = 22.0f;
    private const float EvenBurstRemainingFromPreArcaneEnshroud = 20.0f;
    private const float EndingDutyTargetHPRatio = 0.08f;
    private const float DyingTrashEnshroudRequiredUptime = 10.0f;
    private const float TargetReturnSafety = 1.0f;
    private const float EnshroudDurationSeconds = 30.0f;
    // The entry-now Communio estimate tracks the harness within ~0.05 s; the margin only absorbs return-time jitter.
    private const float EnterNowCommunioMargin = 0.25f;
    private const float TransientTargetLossSeconds = 8.5f;
    private const int DyingTrashLifeCacheSize = 100;
    private const float DyingTrashLifeWarmup = 2.5f;
    private const float DyingTrashLifeSampleInterval = 1.0f;
    private const float DyingTrashLifeStableDelta = 0.001f;
    private const float DyingTrashLifeStableToleranceMin = 0.0005f;
    private const float DyingTrashLifeStableToleranceScale = 0.35f;
    private const float DyingTrashLifeDrainSmoothing = 0.35f;
    private const float DyingTrashLifeHealResetDelta = -0.02f;
    private const float DyingTrashLifeRatioResetTolerance = 0.05f;
    private const float DyingTrashLifeMinimumDrain = 0.0005f;
    private const float ArcaneStartDeathsDesignCoverage = 20.0f;
    private const float TimelineBurstRequiredUptime = 20.0f;
    private const float TimelineBurstLookahead = 360.0f;
    private const float ArcaneCircleRecast = 120.0f;
    private const float ArcaneCircleRaidBuffAlignmentMaxHold = 8.0f; // longest we hold a ready AC for the party window
    private const float ArcaneCircleRaidBuffLead = 0.7f; // fire AC this long before the party buffs land so its 3% covers their first GCD
    private const float GluttonyDriftLimit = 3.0f;
    private const float NormalCooldownWaitMaximum = 0.35f;
    private const float OpeningTwoMinuteGluttonyDriftLimit = 20.0f;
    private const float EvenBurstRecoverySetupWindow = 10.0f;
    private const int EvenBurstRecoveryMinimumShroud = 30;
    private const int EvenBurstRequiredShroud = 50;
    private const float EvenBurstShroudPlanLead = 25.0f;
    private const int EvenBurstShroudPlanSafetyGCDs = 2;
    private const float RotationResumeGapSeconds = 0.5f;
    private const int AOEThresholdTargets = 3;
    private const uint DancingMadCFCID = 1094;
    private const float DancingMadArcaneCircleHoldLead = 10.0f;
    private const float DancingMadArcaneCrestLead = 4.0f;
    private const float DancingMadP4EnshroudLead = 1.2f;
    private const float DancingMadP4ArcaneCircleLead = 0.6f;
    private const float DancingMadP4OpenerWindow = 10.0f;
    private const float DancingMadMeleePlanHorizon = 20.0f;
    private const float DancingMadMeleePlanSampleStep = 0.25f;
    private const float DancingMadMeleePlanCacheSeconds = 0.25f;
    private const float DancingMadMovementSafetyBuffer = 0.35f;
    private const float DancingMadMeleeRange = 3.0f;
    private const float DancingMadConeRange = 8.0f;
    private const float DancingMadLineRange = 15.0f;
    private const float DancingMadRangedRange = 25.0f;
    private const int DancingMadSafePointSamples = 24;
    private const int DancingMadPathSafetySteps = 4;
    private const uint WindurstThirdWalkCFCID = 1117;
    private const uint WindurstShantottoNameID = 14778;
    private const uint WindurstAlexanderNameID = 14529;
    private const uint WindurstPromathiaNameID = 14779;
    private const uint WindurstHollowKingNameID = 14729;
    private const float WindurstArcaneCircleHoldLead = 10.0f;

    private readonly record struct DancingMadBurstWindow(float Time, float Before, float After);
    private readonly record struct DancingMadEnshroudBudget(int Burst, int Normal);
    private readonly record struct DancingMadMeleePlan(
        bool AssignmentRevealed,
        bool CanMeleeNow,
        bool CanConeNow,
        bool CanLineNow,
        bool CanRangedNow,
        float MeleeLossIn,
        float MeleeResumeIn,
        float ConeLossIn,
        float ConeResumeIn,
        float LineLossIn,
        float LineResumeIn,
        float RangedLossIn,
        float RangedResumeIn)
    {
        public float ConeForcedOutDuration => ConeLossIn < float.MaxValue / 2 && ConeResumeIn < float.MaxValue / 2
            ? Math.Max(0, ConeResumeIn - ConeLossIn)
            : float.MaxValue;
    }
    private readonly record struct WindurstBurstWindow(float Time, float Before, float After);
    private readonly record struct EvenBurstShroudPlan(bool Active, int RequiredShroud, int ProjectedShroud, int AvailableGCDs, int RequiredGCDs, bool ResourcePathAvailable)
    {
        public bool NeedsBuild => Active && ProjectedShroud < RequiredShroud;
        public bool CanMeetDeadline => !NeedsBuild || ResourcePathAvailable && RequiredGCDs <= AvailableGCDs;
        public bool Urgent => NeedsBuild && CanMeetDeadline && AvailableGCDs <= RequiredGCDs + EvenBurstShroudPlanSafetyGCDs;
        public bool CanRecoverWithin(int availableGCDs) => NeedsBuild && ResourcePathAvailable && RequiredGCDs <= availableGCDs;
    }

    private enum DancingMadEnshroudPolicy
    {
        StateBased,
        Hold,
        Release
    }

    // FFLogs top RPR anchors; state-based rotation remains the fallback outside each window.
    private static readonly DancingMadBurstWindow[] DancingMadArcaneCircleWindows =
    [
        new(118.1f, 1.0f, 6.0f),
        new(238.1f, 1.0f, 6.0f),
        new(358.1f, 1.0f, 6.0f),
        new(479.4f, 1.5f, 7.0f),
        new(603.7f, 1.5f, 7.0f),
        new(728.0f, 0.5f, 7.0f),
        new(844.2f, 1.5f, 7.0f),
        new(965.0f, 1.5f, 7.0f),
        new(1085.0f, 1.5f, 7.0f)
    ];

    private static readonly DancingMadBurstWindow[] DancingMadPotionWindows =
    [
        new(113.1f, 2.0f, 7.0f),
        new(472.0f, 2.5f, 9.0f),
        new(743.4f, 2.5f, 9.0f),
        new(1075.0f, 2.5f, 9.0f)
    ];

    // FFLogs top-20 joint phase modes: burst-window Enshrouds / normal Enshrouds.
    // P2 is tied at 3/2 and 4/1; 4/1 preserves the safer cap-pressure path.
    // Move the late-P3 Enshroud into the P4 opener; the shifted AC cooldown makes the P4-end Enshroud physically unable to finish before downtime.
    private static readonly DancingMadEnshroudBudget[] DancingMadEnshroudBudgets =
    [
        new(3, 1),
        new(4, 1),
        new(5, 1),
        new(3, 1),
        new(4, 2)
    ];

    // FFLogs top RPR anchors from 20 fast public kills per boss; state-based rotation is the fallback outside each window.
    private static readonly WindurstBurstWindow[] WindurstShantottoArcaneCircleWindows =
    [
        new(6.355f, 1.5f, 6.0f),
        new(128.072f, 2.0f, 6.0f),
        new(246.771f, 2.5f, 7.0f)
    ];

    private static readonly WindurstBurstWindow[] WindurstAlexanderArcaneCircleWindows =
    [
        new(7.170f, 1.5f, 6.0f),
        new(126.910f, 2.0f, 7.0f),
        new(225.698f, 2.5f, 7.0f)
    ];

    private static readonly WindurstBurstWindow[] WindurstPromathiaArcaneCircleWindows =
    [
        new(10.759f, 2.0f, 7.0f),
        new(101.586f, 2.5f, 7.0f),
        new(211.897f, 2.5f, 7.0f),
        new(323.602f, 2.5f, 7.0f)
    ];

    private static readonly WindurstBurstWindow[] WindurstHollowKingArcaneCircleWindows =
    [
        new(7.798f, 1.5f, 6.0f),
        new(131.173f, 2.0f, 7.0f),
        new(248.859f, 2.5f, 7.0f),
        new(369.886f, 2.5f, 8.0f)
    ];

    private static readonly WindurstBurstWindow[] WindurstShantottoPotionWindows =
    [
        new(6.355f, 2.0f, 6.0f)
    ];

    private static readonly WindurstBurstWindow[] WindurstHollowKingPotionWindows =
    [
        new(7.798f, 2.0f, 6.0f),
        new(369.886f, 2.5f, 8.0f)
    ];

    private static readonly WindurstBurstWindow[] NoWindurstWindows = [];

    private float _gluttonyReadySince = float.NaN;
    private float _arcaneCircleReadyIn;
    private float _gluttonyReadyIn;
    private float _previousArcaneCircleReadyIn = float.NaN;
    private float _evenBurstRecoveryStartedAt = float.NaN;
    private float _lastInCombatTimer = float.NaN;
    private float _potionAfterArcaneCircleUntil = float.NaN;
    private DateTime _lastExecutionTime;
    private DateTime _arcaneCircleGCDHoldStartedAt;
    private bool _arcaneCircleCycleStarted;
    private bool _resumedCombatCycle;
    private AID _normalCooldownWaitAction;
    private DateTime _normalCooldownWaitStartedAt;
    private DateTime _normalCooldownWaitExpiresAt;
    private ulong _normalCooldownWaitTargetID;
    private bool _deferredPreArcaneEnshroud;
    private EvenBurstPlentifulHarvestState _evenBurstPlentifulHarvestState;
    private bool _recoveringEvenBurst;
    private bool _degradedEvenBurstFallback;
    private bool _holdArcaneCircleForTimelineDowntime;
    private uint _burstHoldDutyContentID;
    private bool _burstHoldDungeonOrAllianceContent;
    private float _postPerfectioPriorityStartedAt = float.NaN;
    private int _arcaneCircleUses;
    private DancingMadOpenerStage _dancingMadOpenerStage;
    private int _dancingMadEnshroudPhase = -1;
    private int _dancingMadBurstEnshrouds;
    private int _dancingMadNormalEnshrouds;
    private bool _dancingMadEnshroudBudgetActive;
    private DateTime _dancingMadLastObservedEnshroudCast;
    private DateTime _nextDancingMadMeleePlanRefreshAt;
    private ulong _dancingMadMeleePlanTargetID;
    private int _dancingMadMeleePlanForbiddenZoneCount;
    private int _dancingMadMeleePlanObstacleCount;
    private DancingMadMeleePlan _dancingMadMeleePlan;
    private bool _wasInCombat;
    private bool _hadCombatTarget;
    private readonly ulong[] _dyingTrashLifeActorIDs = new ulong[DyingTrashLifeCacheSize];
    private readonly float[] _dyingTrashLifeFirstSeenTimes = new float[DyingTrashLifeCacheSize];
    private readonly float[] _dyingTrashLifeSampleTimes = new float[DyingTrashLifeCacheSize];
    private readonly float[] _dyingTrashLifeSampleRatios = new float[DyingTrashLifeCacheSize];
    private readonly float[] _dyingTrashLifeDrainPerSecond = new float[DyingTrashLifeCacheSize];
    private readonly int[] _dyingTrashLifeStableSamples = new int[DyingTrashLifeCacheSize];

    // Action definitions never change after startup; resolve them once instead of a dictionary lookup per call.
    private readonly ActionDefinition _sliceDefinition = ActionDefinitions.Instance.Spell(AID.Slice)!;
    private readonly ActionDefinition _soulSliceDefinition = ActionDefinitions.Instance.Spell(AID.SoulSlice)!;
    private readonly ActionDefinition _bloodStalkDefinition = ActionDefinitions.Instance.Spell(AID.BloodStalk)!;
    private readonly ActionDefinition _gluttonyDefinition = ActionDefinitions.Instance.Spell(AID.Gluttony)!;
    private readonly ActionDefinition _enshroudDefinition = ActionDefinitions.Instance.Spell(AID.Enshroud)!;
    private readonly ActionDefinition _voidReapingDefinition = ActionDefinitions.Instance.Spell(AID.VoidReaping)!;
    private readonly ActionDefinition _lemuresSliceDefinition = ActionDefinitions.Instance.Spell(AID.LemuresSlice)!;
    private readonly ActionDefinition _sacrificiumDefinition = ActionDefinitions.Instance.Spell(AID.Sacrificium)!;
    private readonly ActionDefinition _communioDefinition = ActionDefinitions.Instance.Spell(AID.Communio)!;
    private readonly ActionDefinition _arcaneCircleDefinition = ActionDefinitions.Instance.Spell(AID.ArcaneCircle)!;
    private readonly ActionDefinition _plentifulHarvestDefinition = ActionDefinitions.Instance.Spell(AID.PlentifulHarvest)!;

    // Per-frame snapshot of predicates that are evaluated dozens of times per Exec. Each stage is filled at the point in
    // the Exec header after which its inputs no longer change; before that point (and outside Exec) the accessor
    // computes the value directly. Nothing cached here may depend on NextGCD or on actions queued later in the frame.
    private enum FrameCacheStage { None, Basics, DancingMadProfile, Targets, TargetPredicates, CycleState }
    private FrameCacheStage _frameCacheStage;
    private MasteryTraits _frameMasteryTraits;
    private bool _frameDancingMadUltimate;
    private bool _frameDancingMadProfile;
    private Enemy? _frameGluttonyTarget;
    private Enemy? _framePlentifulHarvestTarget;
    private bool _framePlentifulHarvestReady;
    private bool _frameQueueablePerfectio;
    private bool _frameEvenBurstPlanningWindow;
    private bool _framePostPerfectioAutomaticPriorityActive;
    private float _frameDowntimeIn = float.MaxValue;
    private float EffectiveDowntimeIn => _frameDowntimeIn;

    private float BurstPlanGCDLength => BurstReferenceGCDLength;
    private int NormalAOEComboTargetCount => AOEThresholdTargets;
    private bool IsInitialOpener => !_arcaneCircleCycleStarted && !_recoveringEvenBurst;
    private bool Enshrouded => BlueSouls > 0;
    private bool CanSpendSoulReaver() => Unlocked(AID.Gibbet) && Unlocked(AID.Gallows);
    private bool CanUseExecutionerActions() => Unlocked(AID.ExecutionersGibbet) && Unlocked(AID.ExecutionersGallows);
    private bool CanUseEnshroudActions() => Unlocked(AID.VoidReaping) || Unlocked(AID.CrossReaping) || Unlocked(AID.GrimReaping);
    private bool PerfectioAvailable => Unlocked(AID.Perfectio) && PerfectioParata > GCD;
    private bool QueueablePerfectio(in Strategy strategy)
        => _frameCacheStage >= FrameCacheStage.TargetPredicates ? _frameQueueablePerfectio : ComputeQueueablePerfectio(strategy);
    private bool ComputeQueueablePerfectio(in Strategy strategy)
        => PerfectioAvailable
        && HostileAOEAllowed(strategy)
        && strategy.Perf.Value != PerfectioStrategy.Delay
        && TargetOverrideOrDefault(strategy.Perf, BestRangedAOETarget, 25, IsSplashTarget) != null;
    private bool ReaverOrExecutioner => SoulReaverActive && CanSpendSoulReaver() || Executioner && (CanUseExecutionerActions() || CanSpendSoulReaver());

    // ----- Action-category lockouts (ActionLocks, shared with the other modules; Basexan.Locks) -----
    // The client checks a lockout status only after the queue has chosen an action, and the refused frame is lost (ActionManagerEx:
    // "Can't execute ... status", retried next frame). A locked candidate at the top of the queue therefore starves everything below
    // it for the whole status, so this frame's candidates of a locked category are dropped (DropLockedCandidates).
    private const float IdleHarpeMinimumWeaponskillLock = 1.0f; // below this a pacified GCD is better spent waiting (300 vs ~200/s of GCD time)
    private const float IdleHarpeStillDelay = 0.5f;             // stand still out of reach this long first: a stop before walking back in is not a hold
    private const float OutOfReachHoldDelay = 0.5f;         // out of reach this long (moving or not) before melee-bound resources are kept for the return
    private const float OutOfReachHoldMax = 20f;            // ...and no longer than this: a target that stays out of reach gets the ranged conversions after all
    private float _outOfReachStillSince = float.NaN;   // CombatTimer when we last came to a stop out of melee reach; NaN while moving or in reach
    private float _outOfReachSince = float.NaN;        // CombatTimer when the melee target left reach (moving or not); NaN in reach or without a target

    private void UpdateOutOfReachStill(Enemy? fallbackTarget)
    {
        if (!Player.InCombat || IsMoving || TargetInMeleeRange(fallbackTarget))
            _outOfReachStillSince = float.NaN;
        else if (float.IsNaN(_outOfReachStillSince))
            _outOfReachStillSince = CombatTimer;

        if (!Player.InCombat || fallbackTarget == null || TargetInMeleeRange(fallbackTarget))
            _outOfReachSince = float.NaN;
        else if (float.IsNaN(_outOfReachSince))
            _outOfReachSince = CombatTimer;
    }

    // The single-target resource conversions that have an out-of-reach substitute (Enshroud into Grim Reaping, Gluttony and Grim
    // Swathe into Guillotine, Soul Scythe for Soul Slice) turn a 500-560 potency GCD into a 200-ish one, while the resource itself
    // keeps. Once the melee target has been out of reach for a moment, hold them for the return and let Harpe fill the GCDs
    // meanwhile. After OutOfReachHoldMax the substitutes go as before, and an overflowing gauge or charge never waits. Real AoE
    // (3+ targets) uses the same actions at full value and is not held. The Dancing Mad and Windurst profiles plan their own
    // ranged fallbacks and are left alone.
    private bool HoldForMeleeReturn(in Strategy strategy)
    {
        if (float.IsNaN(_outOfReachSince))
            return false;
        var outFor = CombatTimer - _outOfReachSince;
        if (outFor < OutOfReachHoldDelay || outFor > OutOfReachHoldMax)
            return false;
        return !ShouldUseDancingMadProfile(strategy) && !ShouldUseWindurstThirdWalkProfile(strategy);
    }

    // Drops this frame's candidates that the client would refuse for a lockout status, so the queue falls through to what can
    // execute: a pacified Shadow of Death no longer keeps Gluttony from weaving, a silenced Communio no longer keeps Lemure's Slice
    // and Sacrificium from weaving (and the last Lemure stack stays for Communio once the silence ends). Entries queued before this
    // module ran (manual presses, planned actions) are left alone, and so is a queued potion: only weaponskills, spells and abilities
    // are dropped (under a stun-type lock the potion is left to the client's own refusal, as before).
    private void DropLockedCandidates(int queueStart)
    {
        if (!Locks.AnyLocked)
            return;
        var entries = Hints.ActionsToExecute.Entries;
        for (var i = entries.Count - 1; i >= queueStart; --i)
            if (ActionDefinitions.Instance[entries[i].Action] is { Category: ActionCategory.Weaponskill or ActionCategory.Spell or ActionCategory.Ability } definition && Locks.IsLocked(definition.Category))
                entries.RemoveAt(i);
    }

    // A GCD nothing else can use: the melee target has been out of reach while we stand still for a moment (held out by a knockback,
    // a tether or a mechanic, not a stop before walking back in), or weaponskills are locked for longer than the cast pays back.
    // Harpe's 300 beats an empty GCD, and every melee GCD outranks it in the queue, so it never displaces one that can execute.
    // Casting while moving is left out: the player's own movement would interrupt it. The Dancing Mad profile plans its own
    // ranged fallback.
    private bool ShouldUseIdleHarpe(in Strategy strategy)
    {
        if (!Player.InCombat || IsMoving || Locks.SpellsLocked || ShouldUseDancingMadProfile(strategy))
            return false;
        if (Locks.WeaponskillsLocked)
            return Locks.Left(ActionCategory.Weaponskill) > IdleHarpeMinimumWeaponskillLock;
        return !float.IsNaN(_outOfReachStillSince) && CombatTimer - _outOfReachStillSince >= IdleHarpeStillDelay;
    }

    public override void Exec(in Strategy strategy, Enemy? primaryTarget)
    {
        var queueStart = Hints.ActionsToExecute.Entries.Count;
        ExecQueue(strategy, primaryTarget, queueStart);
        DropLockedCandidates(queueStart);
    }

    private void ExecQueue(in Strategy strategy, Enemy? primaryTarget, int queueStart)
    {
        UpdateMechanicForecast(strategy.MechanicHints.Value);
        _manualTargeting = strategy.Targeting.Value == Targeting.Manual;
        _frameCacheStage = FrameCacheStage.None;
        _frameMasteryTraits = ComputeMasteryTraits();
        _frameDancingMadUltimate = ComputeDancingMadUltimate();
        _frameCacheStage = FrameCacheStage.Basics;
        _frameDancingMadProfile = DancingMadProfileSelected(strategy) && Player.InCombat;
        _frameCacheStage = FrameCacheStage.DancingMadProfile;
        _hasMechanicHint = Mechanic.HasSnapshot;
        _mechanicHint = Mechanic.Snapshot;
        _hasEncounterHint = Mechanic.HasEncounter;
        _encounterHint = Mechanic.Encounter;
        var rangedPrimaryTarget = primaryTarget;
        SelectPrimaryTarget(strategy, ref rangedPrimaryTarget, 25);
        if (strategy.Targeting.Value == Targeting.Auto)
        {
            var betterRangedTarget = FirstUsablePriorityTarget(25);
            if (betterRangedTarget != null
                && (rangedPrimaryTarget == null
                    || rangedPrimaryTarget.Priority is Enemy.PriorityInvincible or Enemy.PriorityForbidden
                    || betterRangedTarget.Priority > rangedPrimaryTarget.Priority))
                rangedPrimaryTarget = betterRangedTarget;
        }

        rangedPrimaryTarget = UsableTarget(rangedPrimaryTarget, 25);
        if (rangedPrimaryTarget == null && strategy.Targeting.Value == Targeting.AutoTryPri)
            rangedPrimaryTarget = FirstUsablePriorityTarget(25);

        SelectPrimaryTarget(strategy, ref primaryTarget, 3);
        var fullMode = strategy.RotationMode == RotationModeStrategy.FullMode;

        var gauge = World.Client.GetGauge<ReaperGauge>();

        RedGauge = gauge.Soul;
        BlueGauge = gauge.Shroud;
        EnshroudLeft = gauge.EnshroudedTimeRemaining * 0.001f;
        BlueSouls = gauge.LemureShroud;
        PurpleSouls = gauge.VoidShroud;
        _arcaneCircleReadyIn = ReadyIn(AID.ArcaneCircle);
        _arcaneCircleReadyIn = AlignArcaneCircleWithRaidBuffs(strategy, fullMode, _arcaneCircleReadyIn);
        _gluttonyReadyIn = ReadyIn(AID.Gluttony);

        Soulsow = Player.FindStatus(SID.Soulsow) != null;
        var executionerStatus = Status(SID.Executioner);
        var soulReaverStatus = Status(SID.SoulReaver);
        Executioner = executionerStatus.Left > GCD;
        SoulReaverActive = soulReaverStatus.Left > GCD;
        ReaverStacks = Executioner ? executionerStatus.Stacks : soulReaverStatus.Stacks;
        EnhancedGallows = StatusLeft(SID.EnhancedGallows);
        EnhancedGibbet = StatusLeft(SID.EnhancedGibbet);
        ImmortalSacrifice = Status(SID.ImmortalSacrifice);
        BloodsownCircle = StatusLeft(SID.BloodsownCircle);
        ArcaneCircleLeft = SelfStatusLeft(SID.ArcaneCircle);
        IdealHost = StatusLeft(SID.IdealHost);
        EnhancedCrossReaping = StatusLeft(SID.EnhancedCrossReaping);
        EnhancedHarpe = StatusLeft(SID.EnhancedHarpe);
        Oblatio = StatusLeft(SID.Oblatio);
        PerfectioParata = StatusLeft(SID.PerfectioParata);

        ShortestNearbyDDLeft = float.MaxValue;

        var breakReaver = strategy.Reaver == SoulReaverStrategy.ForceBreak && ReaverOrExecutioner;
        _reaverGate = ReaverOrExecutioner && !breakReaver;

        switch (strategy.AOE.Value)
        {
            case AOEStrategy.AOE:
            case AOEStrategy.ForceAOE:
                if (!DeathsDesignAOESafe())
                    break;

                var minNeeded = strategy.AOE.Value == AOEStrategy.ForceAOE ? 1 : AOEThresholdTargets;
                var nearbyCount = 0;
                var shortestNearby = float.MaxValue;
                var targets = Hints.PriorityTargetsSpan;
                var len = targets.Length;
                for (var i = 0; i < len; ++i)
                {
                    var target = targets[i];
                    if (!TargetInAOECircle(target.Actor, Player.Position, 5f))
                        continue;

                    var left = DDLeft(target);
                    if (left < 30)
                    {
                        ++nearbyCount;
                        shortestNearby = Math.Min(shortestNearby, left);
                    }
                }
                if (nearbyCount >= minNeeded)
                    ShortestNearbyDDLeft = shortestNearby;
                break;
        }

        NumAOETargets = NumMeleeAOETargets(strategy);
        (BestLineTarget, _) = SelectTarget(strategy, primaryTarget, 15, LineTargetCheck);
        (BestConeTarget, NumConeTargets) = SelectTarget(strategy, primaryTarget, 8, ConeTargetCheck);
        (BestRangedAOETarget, _) = SelectTarget(strategy, primaryTarget, 25, IsSplashTarget);
        BestLineTarget = UsableTarget(BestLineTarget, 15);
        BestConeTarget = UsableTarget(BestConeTarget, 8);
        BestRangedAOETarget = UsableTarget(BestRangedAOETarget, 25);
        _frameGluttonyTarget = ComputeGluttonyTarget(strategy);
        _framePlentifulHarvestTarget = ComputePlentifulHarvestTarget(strategy);
        _frameCacheStage = FrameCacheStage.Targets;
        _framePlentifulHarvestReady = ComputePlentifulHarvestReady(strategy);
        _frameQueueablePerfectio = ComputeQueueablePerfectio(strategy);
        _frameCacheStage = FrameCacheStage.TargetPredicates;
        var burstCleanupMode = ReaverOrExecutioner || Enshrouded || QueueablePerfectio(strategy) || PlentifulHarvestReady(strategy) || IdealHost > 0;

        var fallbackTarget = FallbackTarget(primaryTarget);
        _currentFallbackTarget = fallbackTarget;
        UpdateOutOfReachStill(fallbackTarget);
        UpdateDyingTrashLifeEstimates();
        TargetDDLeft = DDLeft(fallbackTarget);
        // A planner that reports "downtime now" while a usable enemy stands in front of us is desynced (missed
        // transition, add phase modeled as downtime). Trust the target: without this, every downtime-gated decision
        // (Death's Design, Soul Slice, Gluttony, Enshroud) stays suppressed for the whole desynced stretch.
        _frameDowntimeIn = DowntimeIn <= 0f && fallbackTarget is { } && fallbackTarget.Priority is not Enemy.PriorityPointless ? float.MaxValue : DowntimeIn;
        _holdArcaneCircleForTimelineDowntime = ShouldHoldArcaneCircleForUpcomingDowntime(strategy, fullMode);
        var executionResumed = RotationWasResumed();
        var combatRestarted = Player.InCombat
            && _wasInCombat
            && !float.IsNaN(_lastInCombatTimer)
            && CombatTimer + 0.5f < _lastInCombatTimer;
        if (combatRestarted)
        {
            ResetCombatCycleState();
            _wasInCombat = false;
            _hadCombatTarget = false;
        }

        if (executionResumed || combatRestarted || !_wasInCombat && Player.InCombat)
            ReassessBurstStateFromCurrentResources(strategy, fullMode);

        _dancingMadMeleePlan = GetDancingMadMeleePlan(strategy, (fallbackTarget ?? rangedPrimaryTarget)?.Actor);

        UpdateEvenBurstRecoveryState(strategy, fullMode, fallbackTarget);
        UpdateDancingMadEnshroudBudget(strategy, fullMode);
        UpdatePreArcaneEnshroudDeferral(strategy, fullMode);
        UpdateEvenBurstPlentifulHarvestState(strategy, fullMode);
        UpdateGluttonyReadySince(strategy, fullMode);
        UpdatePostPerfectioPriorityState(strategy, fullMode);
        // Cycle-state fields are only written by the header above; predicates over them are stable from here on.
        _frameEvenBurstPlanningWindow = ComputeEvenBurstPlanningWindow(strategy, fullMode);
        _framePostPerfectioAutomaticPriorityActive = ComputePostPerfectioAutomaticPriorityActive(strategy, fullMode);
        _frameCacheStage = FrameCacheStage.CycleState;

        var pos = GetNextPositional(fallbackTarget, breakReaver);
        UpdatePositionals(fallbackTarget, ref pos);

        // The scripted Dancing Mad openers own every frame until they complete (up to ~40s) and skip OGCD(), which is where the
        // automatic Arcane Crest lives, so they queue it themselves (lowest weave priority, it only takes an otherwise idle slot).
        if (TryDancingMadPhase4Opener(strategy, fallbackTarget, rangedPrimaryTarget, fullMode))
        {
            if (Player.InCombat && ShouldQueueArcaneCrest(strategy))
                PushOGCD(AID.ArcaneCrest, Player, OGCDPriority.ArcaneCrest);
            return;
        }

        if (TryDancingMadFixedOpener(strategy, fallbackTarget, rangedPrimaryTarget, fullMode))
        {
            if (Player.InCombat && ShouldQueueArcaneCrest(strategy))
                PushOGCD(AID.ArcaneCrest, Player, OGCDPriority.ArcaneCrest);
            return;
        }

        if (CountdownRemaining > 0)
        {
            var openerHarpeStartIn = EnhancedHarpe > GCD ? 0 : OpenerHarpeStartIn;
            if (Unlocked(AID.Soulsow) && CountdownRemaining > openerHarpeStartIn + GCDLength + 0.05f && !Soulsow)
                PushGCD(AID.Soulsow, Player, GCDPriority.Soulsow);

            if (strategy.Harpe.Value != HarpeStrategy.Forbid && Unlocked(AID.Harpe) && CountdownRemaining <= openerHarpeStartIn)
            {
                var target = HarpeTarget(strategy, rangedPrimaryTarget);
                if (target != null)
                    PushGCD(AID.Harpe, target, GCDPriority.Harpe);
            }

            return;
        }

        if (ShouldHoldReadyGCDForCriticalWeave(strategy, fallbackTarget, fullMode))
        {
            OGCD(strategy, fallbackTarget, fullMode);
            return;
        }

        if (ShouldPrioritizeRequiredPlentifulHarvest(strategy, fullMode)
            || ShouldPrioritizePlentifulHarvestBeforeSecondEvenBurstEnshroud(strategy, fullMode))
        {
            DDRefresh(strategy, fallbackTarget, fullMode);
            PlentifulHarvest(strategy, fallbackTarget, fullMode);
            return;
        }

        if (Unlocked(AID.SpinningScythe))
            GoalZoneCombined(strategy, 3, Hints.GoalAOECircle(5), AID.SpinningScythe, NormalAOEGoalTargetCount(), maximumActionRange: 25);

        if (TryPrioritizeThirdPotionEnshroud(strategy, fallbackTarget, fullMode))
            return;

        if (ReaverOrExecutioner && !breakReaver)
        {
            var useExecutioner = Executioner && CanUseExecutionerActions();
            var gib = useExecutioner ? AID.ExecutionersGibbet : AID.Gibbet;
            var gal = useExecutioner ? AID.ExecutionersGallows : AID.Gallows;
            var gui = useExecutioner ? AID.ExecutionersGuillotine : AID.Guillotine;

            if (BestConeTarget != null && (ShouldUseReaverAOE(NumConeTargets) || !TargetInMeleeRange(fallbackTarget)) && Unlocked(gui))
            {
                PushGCD(gui, BestConeTarget, GCDPriority.Reaver);
            }
            else if (fallbackTarget != null && TargetInMeleeRange(fallbackTarget) && Unlocked(gib) && Unlocked(gal))
            {
                if (EnhancedGallows > GCD)
                    PushGCD(gal, fallbackTarget, GCDPriority.Reaver);
                else if (EnhancedGibbet > GCD)
                    PushGCD(gib, fallbackTarget, GCDPriority.Reaver);
                else if (useExecutioner && IsNormalOpenerExecutionerSequence(strategy))
                    PushGCD(gib, fallbackTarget, GCDPriority.Reaver);
                else if (GetCurrentPositional(fallbackTarget.Actor) == Positional.Rear)
                    PushGCD(gal, fallbackTarget, GCDPriority.Reaver);
                else
                    PushGCD(gib, fallbackTarget, GCDPriority.Reaver);
            }
        }

        if (!Player.InCombat)
        {
            // if we exit combat while casting, cancel it so we get instant cast instead
            if (Player.CastInfo?.Action.ID == (uint)AID.Soulsow)
                Hints.ForceCancelCastOther = true;
        }

        if (!Enshrouded && (!ReaverOrExecutioner || breakReaver))
        {
            switch (strategy.Harpe.Value)
            {
                case HarpeStrategy.Automatic:
                    if (Unlocked(AID.Harpe) && (EnhancedHarpe > GCD || ShouldUseDancingMadPlannerHarpe(strategy, rangedPrimaryTarget) || ShouldUseIdleHarpe(strategy)))
                    {
                        var target = HarpeTarget(strategy, rangedPrimaryTarget);
                        if (target != null)
                            PushGCD(AID.Harpe, target, EnhancedHarpe > GCD ? GCDPriority.EnhancedHarpe : GCDPriority.Harpe);
                    }
                    break;
                case HarpeStrategy.Ranged:
                    if (Unlocked(AID.Harpe))
                    {
                        var target = HarpeTarget(strategy, rangedPrimaryTarget);
                        if (target != null)
                            PushGCD(AID.Harpe, target, GCDPriority.Harpe);
                    }
                    break;
            }
        }

        HarvestMoon(strategy, fallbackTarget);
        DDRefresh(strategy, fallbackTarget, fullMode);
        if (fullMode || burstCleanupMode)
            Perfectio(strategy, fallbackTarget, fullMode);
        EnshroudGCDs(strategy, fallbackTarget);
        if (fullMode || PlentifulHarvestReady(strategy))
            PlentifulHarvest(strategy, fallbackTarget, fullMode);
        Sow(strategy);

        // other GCDs are all disabled during enshroud; normal GCDs break soul reaver
        if (!Enshrouded && (!ReaverOrExecutioner || breakReaver))
        {
            if (!breakReaver)
                Slice(strategy, fallbackTarget, fullMode);

            var preserveComboBeforeBurst = ShouldPreserveNormalComboBeforeBurst(strategy, fullMode);
            var recoverComboAfterPerfectio = ShouldPrioritizeComboAfterPerfectio(strategy, fullMode);
            if (preserveComboBeforeBurst || recoverComboAfterPerfectio)
            {
                var priority = recoverComboAfterPerfectio ? GCDPriority.ComboRecovery : GCDPriority.ComboPreserve;
                if (ComboLastMove == AID.WaxingSlice && TargetInMeleeRange(fallbackTarget) && Unlocked(AID.InfernalSlice))
                    PushGCD(AID.InfernalSlice, fallbackTarget, priority);

                if (ComboLastMove == AID.Slice && TargetInMeleeRange(fallbackTarget) && Unlocked(AID.WaxingSlice))
                    PushGCD(AID.WaxingSlice, fallbackTarget, priority);
            }

            if (ShouldUseNormalAOECombo(NumAOETargets) && Unlocked(AID.SpinningScythe))
            {
                if (ComboLastMove == AID.SpinningScythe && Unlocked(AID.NightmareScythe))
                {
                    PushGCD(AID.NightmareScythe, Player, GCDPriority.FillerAOE);
                }
                else
                {
                    PushGCD(AID.SpinningScythe, Player, GCDPriority.FillerAOE);
                }
            }

            if (ComboLastMove == AID.WaxingSlice && TargetInMeleeRange(fallbackTarget) && Unlocked(AID.InfernalSlice))
            {
                PushGCD(AID.InfernalSlice, fallbackTarget, GCDPriority.Filler);
            }
            else if (ComboLastMove == AID.Slice && TargetInMeleeRange(fallbackTarget) && Unlocked(AID.WaxingSlice))
            {
                PushGCD(AID.WaxingSlice, fallbackTarget, GCDPriority.Filler);
            }
            else if (TargetInMeleeRange(fallbackTarget) && Unlocked(AID.Slice))
            {
                PushGCD(AID.Slice, fallbackTarget, GCDPriority.Filler);
            }
        }

        if (!TryQueueNormalCooldownWait(strategy, fallbackTarget, fullMode, queueStart))
            OGCD(strategy, fallbackTarget, fullMode);
    }

    private bool ShouldPreserveNormalComboBeforeBurst(in Strategy strategy, bool fullMode)
    {
        if (!fullMode)
            return false;

        if (Enshrouded || ReaverOrExecutioner || QueueablePerfectio(strategy) || PlentifulHarvestReady(strategy))
            return false;

        if (ComboLastMove is not AID.Slice and not AID.WaxingSlice)
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay || !Unlocked(AID.ArcaneCircle))
            return false;

        if (ShouldQueueArcaneCircle(strategy, fullMode))
            return false;

        if (_arcaneCircleReadyIn > BurstPlanGCDLength * 3f)
            return false;

        if (RedGauge < 50 && Unlocked(AID.SoulSlice) && GCDReady(AID.SoulSlice))
            return false;

        return true;
    }

    private bool ShouldPrioritizeComboAfterPerfectio(in Strategy strategy, bool fullMode)
    {
        if (!PostPerfectioAutomaticPriorityActive(strategy, fullMode)
            || Enshrouded
            || ReaverOrExecutioner
            || QueueablePerfectio(strategy)
            || PlentifulHarvestReady(strategy)
            || ComboLastMove is not (AID.Slice or AID.WaxingSlice)
            || !TargetInMeleeRange(_currentFallbackTarget)
            || ComboLastMove == AID.Slice && !Unlocked(AID.WaxingSlice)
            || ComboLastMove == AID.WaxingSlice && !Unlocked(AID.InfernalSlice)
            || World.Client.ComboState.Remaining <= 0)
            return false;

        return World.Client.ComboState.Remaining <= GCD + PostPerfectioEnshroudWindow;
    }

    private void UpdatePostPerfectioPriorityState(in Strategy strategy, bool fullMode)
    {
        if (!Player.InCombat
            || !fullMode
            || strategy.SkillRotation.Value != SkillRotationStrategy.Normal
            || strategy.Enshroud != OffensiveStrategy.Automatic
            || ShouldUseDancingMadProfile(strategy))
        {
            _postPerfectioPriorityStartedAt = float.NaN;
            return;
        }

        if (float.IsNaN(_postPerfectioPriorityStartedAt)
            && LastActionUsedRecently(AID.Perfectio, GCDLength + 1f))
            _postPerfectioPriorityStartedAt = CombatTimer;

        if (!float.IsNaN(_postPerfectioPriorityStartedAt)
            && (CombatTimer - _postPerfectioPriorityStartedAt > 15f
                || LastActionUsedRecently(AID.Gluttony, GCDLength + 1f)
                || LastActionUsedRecently(AID.Gibbet, GCDLength + 1f)
                || LastActionUsedRecently(AID.Gallows, GCDLength + 1f)
                || LastActionUsedRecently(AID.Guillotine, GCDLength + 1f)))
            _postPerfectioPriorityStartedAt = float.NaN;
    }

    private bool PostPerfectioPriorityActive
        => !float.IsNaN(_postPerfectioPriorityStartedAt);

    private bool PostPerfectioAutomaticPriorityActive(in Strategy strategy, bool fullMode)
        => _frameCacheStage >= FrameCacheStage.CycleState ? _framePostPerfectioAutomaticPriorityActive : ComputePostPerfectioAutomaticPriorityActive(strategy, fullMode);
    private bool ComputePostPerfectioAutomaticPriorityActive(in Strategy strategy, bool fullMode)
        => PostPerfectioPriorityActive
        && fullMode
        && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
        && strategy.Enshroud == OffensiveStrategy.Automatic
        && !ShouldUseDancingMadProfile(strategy);

    private bool IsNormalOpenerPostPerfectioSequence(in Strategy strategy)
        => PostPerfectioPriorityActive
        && !_resumedCombatCycle
        && !ShouldUseDancingMadProfile(strategy)
        && strategy.OpenerBurst.Value == OpenerBurstStrategy.Normal
        && CombatTimer < 40f;

    private bool LastActionUsedRecently(AID aid, float maxAge)
    {
        if (Manager.LastCast.Data is not { } cast)
            return false;

        var age = (float)(World.CurrentTime - Manager.LastCast.Time).TotalSeconds;
        return age >= 0 && age <= maxAge && cast.IsSpell(aid);
    }

    private bool ShouldPrioritizeDeathsDesignAfterPerfectio(in Strategy strategy, bool fullMode)
    {
        if (!PostPerfectioAutomaticPriorityActive(strategy, fullMode)
            || ShouldPrioritizeComboAfterPerfectio(strategy, fullMode)
            || Enshrouded
            || ReaverOrExecutioner
            || QueueablePerfectio(strategy)
            || PlentifulHarvestReady(strategy))
            return false;

        var useAOE = Unlocked(AID.WhorlofDeath) && ShortestNearbyDDLeft < float.MaxValue;
        var timer = useAOE ? ShortestNearbyDDLeft : TargetDDLeft;
        var target = useAOE ? BestRangedAOETarget ?? BestConeTarget ?? BestLineTarget ?? _currentFallbackTarget ?? FirstPriorityTargetOrDefault() : _currentFallbackTarget;
        return timer <= GCD + PostPerfectioEnshroudWindow
            && ShouldRefreshDeathsDesignNow(strategy, target, useAOE, urgent: true);
    }

    private bool ShouldPrioritizeSoulSliceAfterPerfectio(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
        => PostPerfectioAutomaticPriorityActive(strategy, fullMode)
        && !ShouldPrioritizeComboAfterPerfectio(strategy, fullMode)
        && !ShouldPrioritizeDeathsDesignAfterPerfectio(strategy, fullMode)
        && !TargetWillDieWithinTwoGCDs(primaryTarget)
        && strategy.Slice.Value != SliceStrategy.Delay
        && !Enshrouded
        && !ReaverOrExecutioner
        && !QueueablePerfectio(strategy)
        && !PlentifulHarvestReady(strategy)
        && RedGauge <= 50
        && MaxChargesIn(AID.SoulSlice) <= GCD + PostPerfectioEnshroudWindow
        && (SoulSliceReadyAtCurrentTarget(strategy)
            || NumAOETargets >= AOEThresholdTargets && SoulScytheReadyAtCurrentTarget(strategy));

    private bool PostPerfectioHigherPriorityGCDQueued(in Strategy strategy, bool fullMode)
        => PostPerfectioAutomaticPriorityActive(strategy, fullMode)
        && (ShouldPrioritizeComboAfterPerfectio(strategy, fullMode)
                && NextGCD is AID.WaxingSlice or AID.InfernalSlice
            || ShouldPrioritizeDeathsDesignAfterPerfectio(strategy, fullMode)
                && NextGCD is AID.ShadowofDeath or AID.WhorlofDeath
            || ShouldPrioritizeSoulSliceAfterPerfectio(strategy, _currentFallbackTarget, fullMode)
                && NextGCD is AID.SoulSlice or AID.SoulScythe);

    private bool ShouldPrioritizeGluttonyAfterPerfectio(in Strategy strategy, bool fullMode)
        => PostPerfectioAutomaticPriorityActive(strategy, fullMode)
        && !PostPerfectioHigherPriorityGCDQueued(strategy, fullMode)
        && strategy.RedGauge.Value == RedGaugeStrategy.Automatic
        && Unlocked(AID.Gluttony)
        && GluttonyTarget(strategy) != null
        && RedGauge >= 50
        && BlueGauge <= 80
        && _gluttonyReadyIn <= 0.1f
        && CanWeave(AID.Gluttony)
        && !ShouldHoldNewBurstForEndingDutyTarget(_currentFallbackTarget);

    private bool TryPrioritizeThirdPotionEnshroud(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        if (!Player.InCombat
            || !PostPerfectioAutomaticPriorityActive(strategy, fullMode)
            || CombatTimer < 40f
            || _evenBurstPlentifulHarvestState != EvenBurstPlentifulHarvestState.Used
            || strategy.Buffs != OffensiveStrategy.Automatic
            || strategy.Perf.Value != PerfectioStrategy.Automatic
            || strategy.RedGauge.Value == RedGaugeStrategy.Force
            || strategy.Slice.Value == SliceStrategy.Force
            || strategy.HM.Value == OffensiveStrategy.Force
            || !strategy.Communio.IsEnabled()
            || !Unlocked(AID.Enshroud)
            || !Unlocked(AID.Communio)
            || Enshrouded
            || ReaverOrExecutioner
            || QueueablePerfectio(strategy)
            || IdealHost > 0
            || BlueGauge < 50
            || PotionLeft <= 0
            || Locks.AbilitiesLocked
            || ReadyIn(AID.Enshroud) > Math.Max(GCD, AnimLock)
            || !CanStartEnshroudAtCurrentTarget(strategy)
            || !HostileAOEAllowed(strategy)
            || TargetOverrideOrDefault(strategy.Communio, BestRangedAOETarget, 25, IsSplashTarget) == null
            || ShouldHoldNewBurstForEndingDutyTarget(primaryTarget, DyingTrashEnshroudRequiredUptime))
            return false;

        var enshroudLock = _enshroudDefinition.InstantAnimLock + AnimationLockDelay;
        var firstReapingIn = Math.Max(GCD, Math.Max(AnimLock, ReadyIn(AID.Enshroud)) + enshroudLock);
        var reapingLock = _voidReapingDefinition.InstantAnimLock + AnimationLockDelay;
        var lemureLock = _lemuresSliceDefinition.InstantAnimLock + AnimationLockDelay;
        var sacrificiumLock = _sacrificiumDefinition.InstantAnimLock + AnimationLockDelay;
        var weaveClipping = 2 * Math.Max(0, reapingLock + lemureLock - EnshroudGCDLength)
            + Math.Max(0, reapingLock + sacrificiumLock - EnshroudGCDLength);
        var enshroudDuration = EnshroudGCDLength * 4 + GetCastTime(AID.Communio) + weaveClipping + 0.1f;
        var communioIn = firstReapingIn + enshroudDuration;
        if (PotionLeft <= communioIn
            || ValidDeathsDesignHintTime(EffectiveDowntimeIn) && EffectiveDowntimeIn <= communioIn)
            return false;

        // Keep Perfectio before this sequence. Only spend spare potion time on the existing maintenance order.
        var deathsDesignLeft = Math.Min(TargetDDLeft, ShortestNearbyDDLeft);
        var communioAfterMaintenance = Math.Max(GCD, AnimLock)
            + Math.Max(GCDLength, reapingLock + enshroudLock) + enshroudDuration;
        var maintenanceDDRefresh = deathsDesignLeft <= communioAfterMaintenance ? GCDLength : 0;
        var canFitMaintenanceGCD = PotionLeft > communioAfterMaintenance + maintenanceDDRefresh;
        if (canFitMaintenanceGCD && ShouldPrioritizeComboAfterPerfectio(strategy, fullMode))
        {
            PushGCD(ComboLastMove == AID.Slice ? AID.WaxingSlice : AID.InfernalSlice, primaryTarget, GCDPriority.ComboRecovery);
            return true;
        }

        // Losing Death's Design also weakens every remaining burst hit, even if Communio still fits the potion.
        if (ShouldPrioritizeDeathsDesignAfterPerfectio(strategy, fullMode)
            || ShouldRefreshDeathsDesignBeforeEnshroud(strategy, fullMode))
        {
            DDRefresh(strategy, primaryTarget, fullMode);
            return NextGCD != AID.None;
        }

        if (canFitMaintenanceGCD && ShouldPrioritizeSoulSliceAfterPerfectio(strategy, primaryTarget, fullMode))
        {
            Slice(strategy, primaryTarget, fullMode);
            return NextGCD != AID.None;
        }

        // Gluttony commits two follow-up GCDs, not just its own animation lock.
        var communioAfterGluttony = communioIn + GCDLength * 2;
        var gluttonyDDRefresh = deathsDesignLeft <= communioAfterGluttony ? GCDLength : 0;
        if (PotionLeft > communioAfterGluttony + gluttonyDDRefresh && ShouldPrioritizeGluttonyAfterPerfectio(strategy, fullMode))
        {
            PushOGCD(AID.Gluttony, GluttonyTarget(strategy), OGCDPriority.Gluttony);
            return true;
        }

        PushOGCD(AID.Enshroud, Player, OGCDPriority.Enshroud);
        return true;
    }

    private bool FastGCDMovesLateSoulSliceBeforePH()
        => GCDLength < 2.47f;

    private bool ShouldUseLateBurstSoulSliceBeforePlentifulHarvest(in Strategy strategy, bool fullMode)
    {
        if (!fullMode || !FastGCDMovesLateSoulSliceBeforePH())
            return false;

        if (strategy.Slice.Value == SliceStrategy.Delay)
            return false;

        if (Enshrouded || ReaverOrExecutioner || QueueablePerfectio(strategy))
            return false;

        if (!PlentifulHarvestReady(strategy) || BlueGauge >= 50 || IdealHost > 0)
            return false;

        if (RaidBuffsLeft <= 0 && _arcaneCircleReadyIn > 10f)
            return false;

        var canSoulSlice = Unlocked(AID.SoulSlice) && GCDReady(AID.SoulSlice);
        var canSoulScythe = Unlocked(AID.SoulScythe) && GCDReady(AID.SoulScythe) && NumAOETargets >= AOEThresholdTargets;
        if (!canSoulSlice && !canSoulScythe)
            return false;

        if (RedGauge > 50)
            return false;

        if (Math.Min(TargetDDLeft, ShortestNearbyDDLeft) <= GCD + 3.0f)
            return false;

        return true;
    }

    private void UpdateGluttonyReadySince(in Strategy strategy, bool fullMode)
    {
        if (!Player.InCombat || !fullMode || strategy.RedGauge == RedGaugeStrategy.Delay || !Unlocked(AID.Gluttony))
        {
            _gluttonyReadySince = float.NaN;
            return;
        }

        if (_gluttonyReadyIn > 0 || float.IsNaN(_gluttonyReadySince))
            _gluttonyReadySince = CombatTimer + Math.Max(0, _gluttonyReadyIn);
    }

    private bool GluttonyReadyDelayExpired(in Strategy strategy, bool fullMode)
    {
        if (!Player.InCombat || !fullMode || strategy.RedGauge == RedGaugeStrategy.Delay || !Unlocked(AID.Gluttony) || _gluttonyReadyIn > 0.1f)
            return false;

        if (float.IsNaN(_gluttonyReadySince))
            return false;

        var openingTwoMinuteBurst = CombatTimer < ArcaneCircleRecast * 2f
            && _arcaneCircleCycleStarted
            && (_arcaneCircleReadyIn <= 20f || _arcaneCircleReadyIn >= 100f || RaidBuffsLeft > 0 || IdealHost > 0);
        var delayLimit = openingTwoMinuteBurst ? OpeningTwoMinuteGluttonyDriftLimit : GluttonyDriftLimit;
        return CombatTimer - _gluttonyReadySince >= delayLimit;
    }

    // A resume is a fresh module instance or a gap in world time while we were not in combat last frame; a frame hitch
    // mid-fight must never trigger a burst-state reassessment.
    private bool RotationWasResumed()
    {
        var now = World.CurrentTime;
        var resumed = _lastExecutionTime == default
            || !_wasInCombat && (now - _lastExecutionTime).TotalSeconds >= RotationResumeGapSeconds;
        _lastExecutionTime = now;
        return resumed;
    }

    private void ReassessBurstStateFromCurrentResources(in Strategy strategy, bool fullMode)
    {
        var arcaneCircleOnCooldown = Unlocked(AID.ArcaneCircle) && _arcaneCircleReadyIn > GCD + 1.0f;
        // Only evidence of *this* fight counts: leftover gauge or recharging cooldowns from a previous pull must not disable the opener.
        var hasBurstContinuation = Enshrouded
            || ReaverOrExecutioner
            || PerfectioAvailable
            || BloodsownCircle > 0
            || ImmortalSacrifice.Left > 0
            || IdealHost > 0;
        var resumedDuringCombat = Player.InCombat && CombatTimer > 1.0f;
        var rotationAlreadyProgressed = _arcaneCircleCycleStarted
            || _recoveringEvenBurst
            || hasBurstContinuation
            || resumedDuringCombat;

        if (!rotationAlreadyProgressed)
            return;

        _arcaneCircleCycleStarted = true;
        _resumedCombatCycle = true;
        _gluttonyReadySince = float.NaN;
        if (arcaneCircleOnCooldown)
            _arcaneCircleUses = Math.Max(_arcaneCircleUses, 1);

        var arcaneBurstActive = BloodsownCircle > 0 || ImmortalSacrifice.Left > 0 || IdealHost > 0;
        if (arcaneBurstActive)
        {
            _recoveringEvenBurst = false;
            _evenBurstRecoveryStartedAt = float.NaN;
            return;
        }

        if (fullMode
            && strategy.Buffs != OffensiveStrategy.Delay
            && _arcaneCircleReadyIn <= 25f)
        {
            _recoveringEvenBurst = true;
            _evenBurstRecoveryStartedAt = float.NaN;
        }
    }

    private void UpdateEvenBurstRecoveryState(in Strategy strategy, bool fullMode, Enemy? target)
    {
        if (_wasInCombat && !Player.InCombat)
        {
            ResetCombatCycleState();
            _wasInCombat = false;
            _hadCombatTarget = false;
            return;
        }

        var hasCombatTarget = Player.InCombat
            && target != null
            && !TargetIsPredictedDead(target.Actor)
            && target.Priority is not Enemy.PriorityInvincible and not Enemy.PriorityForbidden;

        var arcaneCircleUsed = !float.IsNaN(_previousArcaneCircleReadyIn)
            && _arcaneCircleReadyIn > 60f
            && (_previousArcaneCircleReadyIn <= GCD + 0.5f
                || _arcaneCircleReadyIn > _previousArcaneCircleReadyIn + 30f);

        if (_arcaneCircleReadyIn > GCD + 1.0f || arcaneCircleUsed || BloodsownCircle > 0 || ImmortalSacrifice.Left > 0 || IdealHost > 0)
            _arcaneCircleCycleStarted = true;

        if (arcaneCircleUsed)
        {
            ++_arcaneCircleUses;
            _recoveringEvenBurst = false;
            _evenBurstRecoveryStartedAt = float.NaN;
        }

        var burstWasInterrupted = _hadCombatTarget && !hasCombatTarget;
        if (burstWasInterrupted
            && fullMode
            && strategy.Buffs != OffensiveStrategy.Delay)
        {
            _arcaneCircleCycleStarted = true;
            _recoveringEvenBurst = _arcaneCircleReadyIn <= EvenBurstShroudPlanLead && ArcaneCircleLeft <= 0;
            _evenBurstRecoveryStartedAt = float.NaN;
        }

        if (_recoveringEvenBurst && _arcaneCircleReadyIn > EvenBurstShroudPlanLead)
        {
            _recoveringEvenBurst = false;
            _evenBurstRecoveryStartedAt = float.NaN;
        }

        var shroudPlan = BuildEvenBurstShroudPlan(strategy, fullMode);
        var unresolvedEvenBurstSetup = shroudPlan.NeedsBuild
            && _arcaneCircleReadyIn <= GCD + 0.2f
            && ArcaneCircleLeft <= 0
            && !Enshrouded
            && !ReaverOrExecutioner
            && !QueueablePerfectio(strategy)
            && !PlentifulHarvestReady(strategy);
        if (unresolvedEvenBurstSetup)
        {
            if (shroudPlan.CanRecoverWithin(EvenBurstRecoveryAvailableGCDs()))
                _recoveringEvenBurst = true;
            else
            {
                _recoveringEvenBurst = false;
                _degradedEvenBurstFallback = true;
            }
        }

        if (_degradedEvenBurstFallback
            && !arcaneCircleUsed
            && ArcaneCircleLeft <= 0
            && _arcaneCircleReadyIn > GCD + 1.0f)
            _degradedEvenBurstFallback = false;

        var recoverySetupReady = _recoveringEvenBurst
            && hasCombatTarget
            && _arcaneCircleReadyIn <= GCD + 0.2f;
        if (recoverySetupReady)
        {
            if (float.IsNaN(_evenBurstRecoveryStartedAt) || CombatTimer < _evenBurstRecoveryStartedAt)
                _evenBurstRecoveryStartedAt = CombatTimer;
        }
        else
            _evenBurstRecoveryStartedAt = float.NaN;

        _previousArcaneCircleReadyIn = _arcaneCircleReadyIn;
        _wasInCombat = Player.InCombat;
        _hadCombatTarget = hasCombatTarget;
        _lastInCombatTimer = Player.InCombat ? CombatTimer : float.NaN;
    }

    private void ResetCombatCycleState()
    {
        _arcaneCircleCycleStarted = false;
        _resumedCombatCycle = false;
        _arcaneCircleGCDHoldStartedAt = default;
        _normalCooldownWaitAction = AID.None;
        _deferredPreArcaneEnshroud = false;
        _evenBurstPlentifulHarvestState = EvenBurstPlentifulHarvestState.Inactive;
        _recoveringEvenBurst = false;
        _degradedEvenBurstFallback = false;
        _holdArcaneCircleForTimelineDowntime = false;
        _gluttonyReadySince = float.NaN;
        _previousArcaneCircleReadyIn = float.NaN;
        _evenBurstRecoveryStartedAt = float.NaN;
        _lastInCombatTimer = float.NaN;
        _potionAfterArcaneCircleUntil = float.NaN;
        _postPerfectioPriorityStartedAt = float.NaN;
        _arcaneCircleUses = 0;
        _dancingMadOpenerStage = DancingMadOpenerStage.Inactive;
        _dancingMadMeleePlan = NoDancingMadMeleePlan(Player, null);
        _dancingMadMeleePlanTargetID = 0;
        _dancingMadMeleePlanForbiddenZoneCount = 0;
        _dancingMadMeleePlanObstacleCount = 0;
        _nextDancingMadMeleePlanRefreshAt = default;
        ResetDancingMadEnshroudBudget();
    }

    private bool EvenBurstRecoverySetupWindowOpen()
    {
        if (!_recoveringEvenBurst || float.IsNaN(_evenBurstRecoveryStartedAt))
            return false;

        var elapsed = CombatTimer - _evenBurstRecoveryStartedAt;
        return elapsed >= 0 && elapsed <= EvenBurstRecoverySetupWindow;
    }

    private int EvenBurstRecoveryAvailableGCDs()
        => Math.Max(0, (int)Math.Floor((EvenBurstRecoverySetupWindow + 0.1f) / GCDLength));

    private bool DegradedEvenBurstFallbackActive(in Strategy strategy, bool fullMode)
        => _degradedEvenBurstFallback
        && fullMode
        && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
        && strategy.Buffs != OffensiveStrategy.Delay
        && strategy.Enshroud == OffensiveStrategy.Automatic
        && ArcaneCircleLeft > 0;

    private bool ShouldBuildShroudDuringDegradedEvenBurst(in Strategy strategy, bool fullMode)
        => DegradedEvenBurstFallbackActive(strategy, fullMode)
        && strategy.RedGauge.Value == RedGaugeStrategy.Automatic
        && ProjectedShroudFromCommittedActions() < EvenBurstRequiredShroud
        && IdealHost <= 0
        && !Enshrouded
        && !ReaverOrExecutioner
        && !QueueablePerfectio(strategy)
        && !PlentifulHarvestReady(strategy);

    private bool ShouldUseSoulSliceDuringDegradedEvenBurst(in Strategy strategy, bool fullMode)
        => ShouldBuildShroudDuringDegradedEvenBurst(strategy, fullMode)
        && RedGauge < 50
        && strategy.Slice.Value == SliceStrategy.Automatic
        && (Unlocked(AID.SoulSlice) && GCDReady(AID.SoulSlice)
            || Unlocked(AID.SoulScythe) && GCDReady(AID.SoulScythe));

    private bool SoulSliceReadyAtCurrentTarget(in Strategy strategy)
    {
        if (strategy.Slice.Value == SliceStrategy.Delay || !Unlocked(AID.SoulSlice) || !GCDReady(AID.SoulSlice))
            return false;

        var sliceOverride = ResolveTargetOverride(strategy.Slice);
        return sliceOverride != null
            ? UsableTarget(sliceOverride, 3) != null
            : FallbackTargetInRange(null, 3) != null;
    }

    private bool SoulScytheReadyAtCurrentTarget(in Strategy strategy)
    {
        if (strategy.Slice.Value == SliceStrategy.Delay || !HostileAOEAllowed(strategy) || !Unlocked(AID.SoulScythe) || !GCDReady(AID.SoulScythe))
            return false;

        var sliceOverride = ResolveTargetOverride(strategy.Slice);
        return sliceOverride != null
            ? UsableTarget(sliceOverride, 5) != null && AOETargetSafe(sliceOverride, SelfCircleTargetCheck)
            : NumAOETargets > 0;
    }

    private bool PreArcaneEnshroudResourcesReady()
        => BlueGauge >= EvenBurstRequiredShroud;

    private bool PreArcaneEnshroudResourcesReadyAfterNextGCD()
        => ProjectedShroudFromCommittedActions() >= EvenBurstRequiredShroud;

    private int ProjectedShroudFromCommittedActions()
    {
        var committedReaverStacks = ReaverOrExecutioner ? Math.Max(1, ReaverStacks) : 0;
        return Math.Min(100, BlueGauge + committedReaverStacks * 10);
    }

    private EvenBurstShroudPlan BuildEvenBurstShroudPlan(in Strategy strategy, bool fullMode)
    {
        var active = fullMode
            && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
            && strategy.Buffs != OffensiveStrategy.Delay
            && strategy.Enshroud == OffensiveStrategy.Automatic
            && _arcaneCircleCycleStarted
            && Unlocked(AID.ArcaneCircle)
            && Unlocked(AID.Enshroud)
            && ArcaneCircleLeft <= 0
            && _arcaneCircleReadyIn >= 0
            && _arcaneCircleReadyIn <= EvenBurstShroudPlanLead;
        if (!active)
            return default;

        var requiredShroud = _recoveringEvenBurst && CanUseAdaptiveBurst(strategy, fullMode)
            ? EvenBurstRequiredShroud
            : RequiredShroudForNextEvenBurst(strategy);
        var projectedShroud = ProjectedShroudFromCommittedActions();
        var missingShroud = Math.Max(0, requiredShroud - projectedShroud);
        var deadline = Math.Max(0, _arcaneCircleReadyIn - PreArcaneEnshroudEntryWindow);
        var availableGCDs = Math.Max(0, (int)Math.Floor(Math.Max(0, deadline - GCD) / GCDLength));
        var gluttonyAvailableByDeadline = strategy.RedGauge.Value == RedGaugeStrategy.Automatic
            && Unlocked(AID.Gluttony)
            && GluttonyTarget(strategy) != null
            && _gluttonyReadyIn <= deadline;
        var firstSoulSpendShroud = gluttonyAvailableByDeadline ? 20 : 10;
        var requiredSoulSpends = missingShroud <= 0
            ? 0
            : 1 + Math.Max(0, missingShroud - firstSoulSpendShroud + 9) / 10;
        var requiredRedGauge = requiredSoulSpends * 50;
        var soulGenerationGCDs = Math.Max(0, requiredRedGauge - RedGauge + 49) / 50;
        var requiredShroudGCDs = (missingShroud + 9) / 10;
        var minorSoulSpenderAvailable = CanUseMinorSoulSpenderForRecovery(strategy);
        var soulSpenderAvailable = requiredSoulSpends == 0
            || gluttonyAvailableByDeadline && (requiredSoulSpends == 1 || minorSoulSpenderAvailable)
            || minorSoulSpenderAvailable;
        var soulGeneratorAvailable = strategy.Slice.Value == SliceStrategy.Automatic
            && (SoulSliceReadyAtCurrentTarget(strategy) || SoulScytheReadyAtCurrentTarget(strategy));
        var availableRedGauge = RedGauge + (soulGeneratorAvailable ? 50 : 0);
        var resourcePathAvailable = strategy.RedGauge.Value == RedGaugeStrategy.Automatic
            && soulSpenderAvailable
            && requiredRedGauge <= availableRedGauge;
        return new(true, requiredShroud, projectedShroud, availableGCDs, soulGenerationGCDs + requiredShroudGCDs, resourcePathAvailable);
    }

    private int RequiredShroudForNextEvenBurst(in Strategy strategy)
        => strategy.Buffs == OffensiveStrategy.Automatic
        && strategy.Enshroud == OffensiveStrategy.Automatic
        && strategy.Perf.Value == PerfectioStrategy.Automatic
        && strategy.PH.IsEnabled()
        && strategy.Communio.IsEnabled()
        && strategy.Potion.Value != PotionUseStrategy.Off
        && Unlocked(AID.Perfectio)
        && World.Client.GetInventoryItemQuantity(ActionDefinitions.IDPotionStr.ID) > 0
        && PotionReadyIn() <= _arcaneCircleReadyIn
            ? 100
            : EvenBurstRequiredShroud;

    private bool CanBuildShroudBeforeEvenBurst(in Strategy strategy, bool fullMode, int reaverGCDs)
    {
        if (!fullMode
            || strategy.SkillRotation.Value != SkillRotationStrategy.Normal
            || strategy.Buffs != OffensiveStrategy.Automatic
            || strategy.Enshroud != OffensiveStrategy.Automatic
            || strategy.Perf.Value != PerfectioStrategy.Automatic
            || !strategy.PH.IsEnabled()
            || !strategy.Communio.IsEnabled()
            || !Unlocked(AID.Perfectio)
            || strategy.RedGauge.Value != RedGaugeStrategy.Automatic
            || strategy.Reaver.Value != SoulReaverStrategy.Automatic
            || strategy.Slice.Value == SliceStrategy.Force
            || strategy.HM == OffensiveStrategy.Force
            || !_arcaneCircleCycleStarted
            || ArcaneCircleLeft > 0
            || RaidBuffsLeft > 0
            || _recoveringEvenBurst
            || _degradedEvenBurstFallback
            || _deferredPreArcaneEnshroud
            || RedGauge < 50
            || BlueGauge >= 100
            || BlueGauge + reaverGCDs * 10 > 100)
            return false;

        var canUseSingleTarget = CanSpendSoulReaver() && TargetInMeleeRange(_currentFallbackTarget);
        var canUseAOE = HostileAOEAllowed(strategy) && Unlocked(AID.Guillotine) && BestConeTarget != null;
        if (!canUseSingleTarget && !canUseAOE
            || !CanFitGCD(Math.Min(TargetDDLeft, ShortestNearbyDDLeft), reaverGCDs)
            || ComboLastMove is AID.Slice or AID.WaxingSlice && !CanFitGCD(World.Client.ComboState.Remaining, reaverGCDs))
            return false;

        if (reaverGCDs == 2
            ? !CanWeave(AID.Gluttony) || !CanExecuteGluttonyRecoveryFollowups(strategy, GluttonyTarget(strategy))
            : !CanWeave(AID.BloodStalk) || !CanUseMinorSoulSpenderForRecovery(strategy))
            return false;

        var reaverLock = ActionDefinitions.Instance.Spell(canUseSingleTarget ? AID.Gibbet : AID.Guillotine)!.InstantAnimLock + AnimationLockDelay;
        var finishIn = GCD + GCDLength * (reaverGCDs - 1) + reaverLock + PreArcaneEnshroudWeaveSafety;
        return finishIn < Math.Min(_arcaneCircleReadyIn - PreArcaneEnshroudEntryWindow, DeathsDesignTargetLossIn());
    }

    private bool ShouldUseSoulSliceForEvenBurstShroudPlan(in Strategy strategy, bool fullMode)
    {
        var plan = BuildEvenBurstShroudPlan(strategy, fullMode);
        return plan.Urgent
            && RedGauge < 50
            && strategy.Slice.Value != SliceStrategy.Delay
            && Unlocked(AID.SoulSlice)
            && GCDReady(AID.SoulSlice);
    }

    private bool CanRecoverShroudBeforeArcaneCircle(in Strategy strategy)
    {
        if (BlueGauge < EvenBurstRecoveryMinimumShroud)
            return false;

        var soulSliceReady = SoulSliceReadyAtCurrentTarget(strategy);
        var soulScytheReady = SoulScytheReadyAtCurrentTarget(strategy);
        if (BlueGauge >= 50)
            return RedGauge < 50 && (soulSliceReady || soulScytheReady);

        if (strategy.RedGauge == RedGaugeStrategy.Delay)
            return false;

        var canSupplySoul = RedGauge >= 50 || soulSliceReady || soulScytheReady;
        if (!canSupplySoul)
            return false;

        var canUseMinorSoulSpender = CanUseMinorSoulSpenderForRecovery(strategy);
        var gluttonyTarget = GluttonyTarget(strategy);
        var canUseGluttony = Unlocked(AID.Gluttony)
            && CanExecuteGluttonyRecoveryFollowups(strategy, gluttonyTarget)
            && _gluttonyReadyIn <= 0.1f;

        if (BlueGauge >= 40 && strategy.RedGauge == RedGaugeStrategy.ReserveGluttony)
            return RedGauge == 100 && canUseMinorSoulSpender;

        if (BlueGauge >= 40)
            return canUseMinorSoulSpender || canUseGluttony;

        return strategy.RedGauge.Value is RedGaugeStrategy.Automatic or RedGaugeStrategy.Force
            && canUseGluttony;
    }

    private bool ShouldHoldArcaneCircleForEvenBurstRecovery(in Strategy strategy, bool fullMode)
    {
        var plan = BuildEvenBurstShroudPlan(strategy, fullMode);
        var committedRecovery = CanUseAdaptiveBurst(strategy, fullMode)
            && ReaverOrExecutioner
            && BlueGauge < EvenBurstRequiredShroud
            && ProjectedShroudFromCommittedActions() >= EvenBurstRequiredShroud;
        var setupGCDs = committedRecovery ? Math.Max(1, ReaverStacks) : plan.RequiredGCDs;
        return fullMode
        && EvenBurstRecoverySetupWindowOpen()
        && (committedRecovery || plan.CanRecoverWithin(EvenBurstRecoveryAvailableGCDs()))
        && strategy.Buffs != OffensiveStrategy.Delay
        && _arcaneCircleReadyIn <= GCD + 0.2f
        && ArcaneCircleLeft <= 0
        && IdealHost <= 0
        && BlueGauge < EvenBurstRequiredShroud
        && !Enshrouded
        && (!ReaverOrExecutioner || committedRecovery)
        && !QueueablePerfectio(strategy)
        && !PlentifulHarvestReady(strategy)
        && (committedRecovery || CanRecoverShroudBeforeArcaneCircle(strategy))
        && (!CanUseAdaptiveBurst(strategy, fullMode)
            || CanFitRecoveryBurst(setupGCDs * GCDLength));
    }

    private bool ShouldUseSoulSliceForEvenBurstRecovery(in Strategy strategy, bool fullMode)
        => ShouldHoldArcaneCircleForEvenBurstRecovery(strategy, fullMode)
        && RedGauge < 50
        && strategy.Slice.Value != SliceStrategy.Delay
        && Unlocked(AID.SoulSlice)
        && GCDReady(AID.SoulSlice);

    private bool ShouldUseGluttonyForEvenBurstRecovery(in Strategy strategy, bool fullMode)
    {
        var gluttonyTarget = GluttonyTarget(strategy);
        return fullMode
            && EvenBurstRecoverySetupWindowOpen()
            && strategy.Buffs != OffensiveStrategy.Delay
            && strategy.RedGauge.Value is RedGaugeStrategy.Automatic or RedGaugeStrategy.Force
            && _arcaneCircleReadyIn <= GCD + 0.2f
            && BlueGauge >= EvenBurstRecoveryMinimumShroud
            && BlueGauge < 50
            && (BlueGauge < 40 || !CanUseMinorSoulSpenderForRecovery(strategy))
            && RedGaugeAfterNextGCD() >= 50
            && Unlocked(AID.Gluttony)
            && CanExecuteGluttonyRecoveryFollowups(strategy, gluttonyTarget)
            && _gluttonyReadyIn <= 0.1f;
    }

    private bool CanExecuteGluttonyRecoveryFollowups(in Strategy strategy, Enemy? gluttonyTarget)
    {
        var setupGCD = RedGauge < 50 ? GCD : 0;
        if (gluttonyTarget == null || TargetWillDieWithinTwoGCDs(gluttonyTarget, setupGCD))
            return false;

        var canUseSingleTarget = CanSpendSoulReaver() && TargetInMeleeRange(_currentFallbackTarget);
        var canUseAOE = HostileAOEAllowed(strategy)
            && BestConeTarget != null
            && (Unlocked(AID.Guillotine) || Unlocked(AID.ExecutionersGuillotine));
        return canUseSingleTarget || canUseAOE;
    }

    private bool CanUseMinorSoulSpenderForRecovery(in Strategy strategy)
    {
        var targetOverride = ResolveTargetOverride(strategy.RedGauge);
        var bloodStalkTarget = targetOverride != null
            ? UsableTarget(targetOverride, 3)
            : FallbackTargetInRange(null, 3);
        var grimSwatheTarget = HostileAOEAllowed(strategy)
            ? TargetOverrideOrDefault(strategy.RedGauge, BestConeTarget, 8, ConeTargetCheck)
            : null;
        return Unlocked(AID.BloodStalk) && bloodStalkTarget != null
            || Unlocked(AID.GrimSwathe) && grimSwatheTarget != null;
    }

    private bool WaitingForArcaneCircle(in Strategy strategy, bool fullMode, out bool gluttonyReadyDelayExpired, out bool arcaneCircleQueuedThisGCD)
    {
        gluttonyReadyDelayExpired = GluttonyReadyDelayExpired(strategy, fullMode);
        arcaneCircleQueuedThisGCD = ShouldQueueArcaneCircle(strategy, fullMode);
        // FightRemaining (value-of-information experiment): no Soul is held for a window that cannot finish
        if (VoiReleaseActive(VoiReleaseSoul))
            return false;
        return fullMode
            && strategy.Buffs != OffensiveStrategy.Delay
            && Unlocked(AID.ArcaneCircle)
            && ArcaneCircleLeft <= 0
            && (_arcaneCircleReadyIn <= 10f || arcaneCircleQueuedThisGCD);
    }

    private void OGCD(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        var fallbackTarget = FallbackTarget(primaryTarget);
        if (!Player.InCombat)
            return;

        if (fallbackTarget == null && BestConeTarget == null && BestRangedAOETarget == null)
        {
            if (strategy.Buffs == OffensiveStrategy.Force && ShouldQueueArcaneCircle(strategy, fullMode))
                PushOGCD(AID.ArcaneCircle, Player, OGCDPriority.ArcaneCircle, delay: ArcaneCircleWeaveDelay(strategy));

            if (strategy.Enshroud == OffensiveStrategy.Force && ShouldEnshroud(strategy, fullMode))
                PushOGCD(AID.Enshroud, Player, OGCDPriority.Enshroud);

            return;
        }

        if ((ShouldStartRecoveryEnshroud(strategy, fullMode) || ShouldPreferGaugeEnshroudBeforeHarvest(strategy, fullMode))
            && ShouldEnshroud(strategy, fullMode))
        {
            PushOGCD(AID.Enshroud, Player, OGCDPriority.EnshroudBeforeSoulOvercap);
            return;
        }

        var preserveArcaneCircleTiming = ShouldPreserveArcaneCircleTimingForEvenBurst(strategy, fullMode);
        if (ShouldPrioritizeArcaneCircleOverGCD(strategy, fallbackTarget, fullMode))
        {
            if (CanPlanPotionAfterArcaneCircle(strategy, fallbackTarget, fullMode))
                _potionAfterArcaneCircleUntil = CombatTimer + Math.Max(0, _arcaneCircleReadyIn) + GCDLength + 1.0f;

            PushOGCD(AID.ArcaneCircle, Player, OGCDPriority.ArcaneCircle, delay: Math.Max(0, _arcaneCircleReadyIn));
            return;
        }

        var onTimeArcaneCircleQueuedThisGCD = preserveArcaneCircleTiming && ShouldQueueArcaneCircle(strategy, fullMode);
        if (_deferredPreArcaneEnshroud && !onTimeArcaneCircleQueuedThisGCD && ArcaneCircleLeft <= 0 && !Enshrouded && ShouldEnshroud(strategy, fullMode))
        {
            PushOGCD(AID.Enshroud, Player, OGCDPriority.EnshroudBeforeSoulOvercap);
            return;
        }

        var terminalWeaveAction = SelectTerminalEnshroudWeave(strategy, fallbackTarget, fullMode, out var commitPotionAfterArcaneCircle);
        if (terminalWeaveAction != TerminalWeaveAction.None)
        {
            QueueTerminalEnshroudWeave(terminalWeaveAction, fallbackTarget, commitPotionAfterArcaneCircle);
            return;
        }

        var potionQueuedThisGCD = ShouldUsePotion(strategy, fallbackTarget, fullMode);
        if (potionQueuedThisGCD)
            Hints.ActionsToExecute.Push(ActionDefinitions.IDPotionStr, Player, ActionQueue.Priority.Low + (int)OGCDPriority.Potion, delay: PotionWeaveDelay());

        var arcaneCircleQueuedThisGCD = ShouldQueueArcaneCircle(strategy, fullMode);
        if (arcaneCircleQueuedThisGCD)
        {
            // The first potion burst uses Potion -> Arcane Circle; later potion bursts use Arcane Circle -> Potion.
            var arcaneCircleBeforePotion = (potionQueuedThisGCD
                    || ShouldDelayPotionForTripleEnshroud(strategy, fullMode) && CanPlanPotionAfterArcaneCircle(strategy, fallbackTarget, fullMode))
                && (IsLaterPotionBurst() || preserveArcaneCircleTiming);
            if (arcaneCircleBeforePotion)
                _potionAfterArcaneCircleUntil = CombatTimer + GCDLength + 1.0f;

            var priority = arcaneCircleBeforePotion ? OGCDPriority.ArcaneCircleBeforePotion : OGCDPriority.ArcaneCircle;
            PushOGCD(AID.ArcaneCircle, Player, priority, delay: ArcaneCircleWeaveDelay(strategy, preserveArcaneCircleTiming));
        }

        if (!(potionQueuedThisGCD && arcaneCircleQueuedThisGCD) && strategy.TrueNorth == TrueNorthStrategy.Auto && Unlocked(AID.TrueNorth) && NextPositionalImminent && !NextPositionalCorrect && StatusLeft(SID.TrueNorth) <= 0 && CanWeave(AID.TrueNorth))
            PushOGCD(AID.TrueNorth, Player, OGCDPriority.TrueNorth, delay: GCD - 0.8f);

        var lemuresScytheQueuedThisGCD = PurpleSouls > 1
            && Unlocked(AID.LemuresScythe)
            && BestConeTarget != null
            && (NumConeTargets > 2 || !TargetInMeleeRange(fallbackTarget));
        var lemuresSliceQueuedThisGCD = PurpleSouls > 1
            && !lemuresScytheQueuedThisGCD
            && Unlocked(AID.LemuresSlice)
            && TargetInMeleeRange(fallbackTarget);
        var lemureQueuedThisGCD = lemuresScytheQueuedThisGCD || lemuresSliceQueuedThisGCD;
        var arcaneCircleReturnsSafelyDuringEnshroud = ShouldPlanArcaneCircle(strategy, fullMode)
            && _arcaneCircleReadyIn > 0
            && _arcaneCircleReadyIn < EnshroudLeft - 1.8f;
        var spendLemuresBeforeSacrificium = _resumedCombatCycle && lemureQueuedThisGCD
            && BlueSouls > 1 && Oblatio > GCD + EnshroudGCDLength;
        var sacrificiumTarget = BestRangedAOETarget;
        var sacrificiumQueuedThisGCD = HostileAOEAllowed(strategy) && sacrificiumTarget != null && Unlocked(AID.Sacrificium) && Enshrouded && Oblatio > 0
            && !spendLemuresBeforeSacrificium
            && !ShouldHoldArcaneCircleForRecoveryEnshroud(strategy, fullMode)
            && (RaidBuffsLeft > 0 || !arcaneCircleQueuedThisGCD && !arcaneCircleReturnsSafelyDuringEnshroud);
        if (sacrificiumQueuedThisGCD && sacrificiumTarget != null)
            PushOGCD(AID.Sacrificium, sacrificiumTarget, OGCDPriority.Sacrificium);

        if (lemureQueuedThisGCD
            && !(potionQueuedThisGCD && arcaneCircleQueuedThisGCD)
            && !(arcaneCircleQueuedThisGCD && sacrificiumQueuedThisGCD)
            && !(potionQueuedThisGCD && sacrificiumQueuedThisGCD))
        {
            if (lemuresScytheQueuedThisGCD)
                PushOGCD(AID.LemuresScythe, BestConeTarget, OGCDPriority.Lemure);
            else if (lemuresSliceQueuedThisGCD)
                PushOGCD(AID.LemuresSlice, fallbackTarget, OGCDPriority.Lemure);
        }

        var enshroudQueuedThisGCD = ShouldEnshroud(strategy, fullMode, arcaneCircleQueuedThisGCD);
        if (enshroudQueuedThisGCD)
        {
            var priority = arcaneCircleQueuedThisGCD
                ? OGCDPriority.Enshroud
                : NextGCDWouldOverflowRedAtFullShroud(strategy, fullMode)
                ? OGCDPriority.EnshroudBeforeSoulOvercap
                : OGCDPriority.Enshroud;
            PushOGCD(AID.Enshroud, Player, priority);
        }

        var ddTimer = Math.Min(TargetDDLeft, ShortestNearbyDDLeft);
        var reservePreArcaneEnshroud = enshroudQueuedThisGCD
            && ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(strategy, fullMode, ddTimer);
        if (!reservePreArcaneEnshroud)
            UseSoul(strategy, fallbackTarget, fullMode, potionQueuedThisGCD, arcaneCircleQueuedThisGCD, enshroudQueuedThisGCD);

        if (ShouldQueueArcaneCrest(strategy))
            PushOGCD(AID.ArcaneCrest, Player, OGCDPriority.ArcaneCrest);
    }

    private bool ShouldQueueArcaneCrest(in Strategy strategy)
    {
        if (!Unlocked(AID.ArcaneCrest) || !strategy.AutoCrest.IsEnabled())
            return false;

        var dancingMad = IsDancingMadUltimate();
        var advance = dancingMad ? DancingMadArcaneCrestLead : 5.0f;
        var now = World.CurrentTime;
        var deadline = World.FutureTime(advance);
        var crestReadyIn = ReadyIn(AID.ArcaneCrest);
        var predictions = Hints.PredictedDamage;
        var count = predictions.Count;
        for (var i = 0; i < count; ++i)
        {
            var damage = predictions[i];
            if (damage.Players[PartyState.PlayerSlot]
                && damage.Activation > now
                && damage.Activation <= deadline
                && crestReadyIn <= (float)(damage.Activation - now).TotalSeconds
                && (!dancingMad || damage.Type is PredictedDamageType.Raidwide or PredictedDamageType.Shared))
                return true;
        }

        return false;
    }

    private bool ShouldQueueArcaneCircle(in Strategy strategy, bool fullMode)
        => ShouldPlanArcaneCircle(strategy, fullMode)
        && (CanQueueArcaneCircleThisGCD()
            || ShouldPreserveArcaneCircleTimingForEvenBurst(strategy, fullMode) && CanWeave(AID.ArcaneCircle))
        && (strategy.Buffs == OffensiveStrategy.Force
            || ShouldPreserveArcaneCircleTimingForEvenBurst(strategy, fullMode)
            || !ShouldHoldArcaneCircleForPreArcaneEnshroud(strategy, fullMode)
                && !ShouldHoldArcaneCircleForFirstReapingDeathsDesign(strategy, fullMode));

    private bool ShouldHoldArcaneCircleForFirstReapingDeathsDesign(in Strategy strategy, bool fullMode)
        => Enshrouded
        && BlueSouls >= 4
        && ArcaneCircleLeft <= 0
        && ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(strategy, fullMode, Math.Min(TargetDDLeft, ShortestNearbyDDLeft));

    private bool ShouldHoldArcaneCircleForPreArcaneEnshroud(in Strategy strategy, bool fullMode)
        => fullMode
        && _deferredPreArcaneEnshroud
        && !ShouldPreserveArcaneCircleTimingForEvenBurst(strategy, fullMode)
        && ArcaneCircleLeft <= 0
        && !Enshrouded
        && !ReaverOrExecutioner
        && !QueueablePerfectio(strategy)
        && !PlentifulHarvestReady(strategy)
        && IdealHost == 0
        && _arcaneCircleReadyIn <= GCD - 0.8f
        && CanStartPreArcaneEnshroudThisGCD()
        && (ShouldEnshroud(strategy, fullMode) || LastActionUsedRecently(AID.Enshroud, 1f));

    private void UpdatePreArcaneEnshroudDeferral(in Strategy strategy, bool fullMode)
    {
        if (_deferredPreArcaneEnshroud && Enshrouded && ArcaneCircleLeft <= 0 && _arcaneCircleReadyIn <= PreArcaneEnshroudEntryWindow)
            return;

        var eligible = IsEvenBurstPlanningWindow(strategy, fullMode)
            && Player.InCombat
            && !ShouldUseDancingMadProfile(strategy)
            && strategy.Enshroud != OffensiveStrategy.Delay
            && ArcaneCircleLeft <= 0
            && !Enshrouded
            && !ReaverOrExecutioner
            && !QueueablePerfectio(strategy)
            && !PlentifulHarvestReady(strategy)
            && IdealHost == 0
            && PreArcaneEnshroudResourcesReady();

        if (!eligible || _arcaneCircleReadyIn <= 0 || _arcaneCircleReadyIn > PreArcaneEnshroudEntryWindow)
        {
            _deferredPreArcaneEnshroud = false;
            return;
        }

        if (_arcaneCircleReadyIn > PreArcaneEnshroudWeaveSafety)
            _deferredPreArcaneEnshroud = true;
    }

    private void UpdateEvenBurstPlentifulHarvestState(in Strategy strategy, bool fullMode)
    {
        var automaticSequence = Player.InCombat
            && fullMode
            && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
            && strategy.Buffs == OffensiveStrategy.Automatic
            && strategy.Enshroud == OffensiveStrategy.Automatic
            && strategy.Perf.Value == PerfectioStrategy.Automatic
            && strategy.PH.IsEnabled()
            && Unlocked(AID.ArcaneCircle)
            && Unlocked(AID.Enshroud)
            && Unlocked(AID.PlentifulHarvest)
            && Unlocked(AID.Perfectio);
        if (!automaticSequence)
        {
            _evenBurstPlentifulHarvestState = EvenBurstPlentifulHarvestState.Inactive;
            return;
        }

        var arcaneCircleRecentlyUsed = LastActionUsedRecently(AID.ArcaneCircle, GCDLength + 1f);
        var burstEnded = ArcaneCircleLeft <= 0
            && BloodsownCircle <= 0
            && ImmortalSacrifice.Left <= GCD
            && IdealHost <= 0
            && !Enshrouded
            && !arcaneCircleRecentlyUsed
            && (_evenBurstPlentifulHarvestState != EvenBurstPlentifulHarvestState.Used || PotionLeft <= 0)
            && _arcaneCircleReadyIn > 45f;
        if (burstEnded)
        {
            _evenBurstPlentifulHarvestState = EvenBurstPlentifulHarvestState.Inactive;
            return;
        }

        if (IdealHost > 0 || LastActionUsedRecently(AID.PlentifulHarvest, GCDLength + 1f))
        {
            _evenBurstPlentifulHarvestState = EvenBurstPlentifulHarvestState.Used;
            return;
        }

        var firstEvenBurstEnshroudStarted = _evenBurstPlentifulHarvestState == EvenBurstPlentifulHarvestState.Inactive
            && _arcaneCircleUses > 0
            && Enshrouded
            && (ArcaneCircleLeft > 0 || _arcaneCircleReadyIn <= PreArcaneEnshroudEntryWindow || arcaneCircleRecentlyUsed);
        var firstEvenBurstCommunioCompleted = _evenBurstPlentifulHarvestState == EvenBurstPlentifulHarvestState.Inactive
            && _arcaneCircleUses > 0
            && !Enshrouded
            && LastActionUsedRecently(AID.Communio, GCDLength + 1f)
            && (ArcaneCircleLeft > 0 || BloodsownCircle > 0 || ImmortalSacrifice.Left > GCD);
        if (firstEvenBurstEnshroudStarted || firstEvenBurstCommunioCompleted)
            _evenBurstPlentifulHarvestState = EvenBurstPlentifulHarvestState.Required;
    }

    private bool ShouldPlanArcaneCircle(in Strategy strategy, bool fullMode)
    {
        if (!Unlocked(AID.ArcaneCircle)
            || strategy.Buffs == OffensiveStrategy.Delay
            || !(fullMode || strategy.Buffs == OffensiveStrategy.Force))
            return false;

        if (strategy.Buffs == OffensiveStrategy.Force
            || ShouldPreserveArcaneCircleTimingForEvenBurst(strategy, fullMode))
            return true;

        if (ShouldHoldNewBurstForEndingDutyTarget(FallbackTarget(null), TimelineBurstRequiredUptime))
            return false;

        if (ShouldUseDancingMadProfile(strategy))
        {
            if (IsInitialOpener && CombatTimer < 10f || IsInDancingMadWindow(DancingMadArcaneCircleWindows))
                return true;

            if (ShouldHoldForDancingMadArcaneCircle())
                return false;
        }

        if (ShouldUseWindurstThirdWalkProfile(strategy))
        {
            var windows = WindurstArcaneCircleWindows();
            if (IsInWindurstWindow(windows))
                return true;

            if (ShouldHoldForWindurstArcaneCircle(windows))
                return false;
        }

        if (_holdArcaneCircleForTimelineDowntime)
            return false;

        if (IsZeroSecondOpener(strategy))
            return true;

        if (IsTwoPointFiveSecondOpener(strategy))
            return true;

        if (IsNormalOpenerSoulSliceBurstGCD(strategy))
            return true;

        if (ShouldHoldNormalOpenerArcaneCircleForSoulSlice(strategy))
            return NormalOpenerTargetLostBeforeLateArcaneCircleEnds();

        if (ShouldHoldArcaneCircleForRecoveryEnshroud(strategy, fullMode))
            return false;

        if (ShouldHoldArcaneCircleForEvenBurstRecovery(strategy, fullMode)
            && !ShouldPreserveArcaneCircleTimingForEvenBurst(strategy, fullMode))
            return false;

        if (fullMode && _recoveringEvenBurst)
            return true;

        return OnCooldown(AID.SoulSlice) || CombatTimer > 10;
    }

    private bool ShouldHoldArcaneCircleForUpcomingDowntime(in Strategy strategy, bool fullMode)
    {
        if (!fullMode
            || IsInitialOpener
            || Enshrouded
            || strategy.Potion.Value != PotionUseStrategy.Off
            || ShouldUseDancingMadProfile(strategy)
            || !ValidDeathsDesignHintTime(EffectiveDowntimeIn)
            || EffectiveDowntimeIn > TimelineBurstRequiredUptime)
            return false;

        if (Manager.Planner is not { } planner)
            return false;
        var windows = _timelineBurstWindows;
        planner.EstimateTargetableWindows(TimelineBurstLookahead, windows);
        if (windows.Count < 3 || !windows[0].Targetable || windows[0].StartIn > 0.1f)
            return false;

        var downtimeIndex = windows.FindIndex(1, window => !window.Targetable && window.EndIn > window.StartIn);
        if (downtimeIndex < 0 || windows[downtimeIndex].StartIn > TimelineBurstRequiredUptime)
            return false;

        var nextTargetableIndex = windows.FindIndex(downtimeIndex + 1, window => window.Targetable && window.EndIn > window.StartIn);
        if (nextTargetableIndex < 0 || windows[^1].EndIn >= TimelineBurstLookahead - 0.1f)
            return false;

        var effectiveRecast = ArcaneCircleRecast - Math.Max(0, GCDLength - 0.8f);
        var remaining = windows[^1].EndIn;
        var delayedBy = windows[nextTargetableIndex].StartIn;
        if (Unlocked(AID.Gluttony) && _gluttonyReadyIn <= delayedBy)
            return false;

        var usesNow = 1 + (int)MathF.Floor(Math.Max(0, remaining - 0.1f) / effectiveRecast);
        var usesAfterDowntime = 1 + (int)MathF.Floor(Math.Max(0, remaining - delayedBy - 0.1f) / effectiveRecast);
        return usesAfterDowntime >= usesNow;
    }

    private bool IsZeroSecondOpener(in Strategy strategy)
        => (strategy.OpenerBurst.Value == OpenerBurstStrategy.ZeroSecondBurst || ShouldUseDancingMadProfile(strategy))
        && IsInitialOpener
        && CombatTimer < 10f
        && Player.InCombat
        && Unlocked(AID.ArcaneCircle)
        && _arcaneCircleReadyIn <= GCD;

    private bool ShouldUseZeroSecondOpenerSoulSliceBeforeDeathsDesign(in Strategy strategy, bool fullMode)
        => IsZeroSecondOpener(strategy)
        && fullMode
        && strategy.Slice.Value != SliceStrategy.Delay
        && Unlocked(AID.SoulSlice)
        && GCDReady(AID.SoulSlice)
        && RedGauge <= 50
        && BlueGauge < 50
        && !Enshrouded
        && !ReaverOrExecutioner
        && !QueueablePerfectio(strategy)
        && !PlentifulHarvestReady(strategy);

    private bool IsTwoPointFiveSecondOpener(in Strategy strategy)
    {
        if (ShouldUseDancingMadProfile(strategy))
            return false;

        if (strategy.OpenerBurst.Value != OpenerBurstStrategy.TwoPointFiveSecondBurst)
            return false;

        if (!IsInitialOpener)
            return false;

        if (CombatTimer >= 10f || !Player.InCombat)
            return false;

        if (!Unlocked(AID.ArcaneCircle) || _arcaneCircleReadyIn > GCD)
            return false;

        if (Unlocked(AID.SoulSlice) && OnCooldown(AID.SoulSlice))
            return false;

        return TargetDDLeft > GCD || CombatTimer >= 1.0f;
    }

    private bool ShouldHoldNormalOpenerArcaneCircleForSoulSlice(in Strategy strategy)
        => !ShouldUseDancingMadProfile(strategy)
        && strategy.OpenerBurst.Value == OpenerBurstStrategy.Normal
        && IsInitialOpener
        && Player.InCombat
        && CombatTimer < 20f
        && Unlocked(AID.SoulSlice)
        && strategy.Slice.Value != SliceStrategy.Delay
        && RedGauge <= 50
        && BlueGauge < 50
        && !Enshrouded
        && !ReaverOrExecutioner
        && !QueueablePerfectio(strategy)
        && !PlentifulHarvestReady(strategy)
        && !OnCooldown(AID.SoulSlice);

    // Holding Arcane Circle until after Soul Slice only pays if its 20 s then still fit on the target. When a target loss
    // is predicted before that, casting it now keeps more of the buff on the remaining uptime.
    private bool NormalOpenerTargetLostBeforeLateArcaneCircleEnds()
    {
        var lossIn = DeathsDesignTargetLossIn();
        return ValidDeathsDesignHintTime(lossIn) && lossIn < GCD + NormalOpenerLateArcaneCircleCoverage;
    }

    // The 2GCD opener (The Balance "2nd GCD AC", and the regression rule for it) weaves Arcane Circle, then Gluttony,
    // in the window AFTER Soul Slice: the charge is spent and its Soul is not yet converted. Testing NextGCD == Soul Slice
    // instead selected the window before it and moved Arcane Circle one GCD early.
    private bool IsNormalOpenerSoulSliceBurstGCD(in Strategy strategy)
        => IsNormalOpenerArcaneCircleAfterSoulSlice(strategy)
        && Unlocked(AID.Gluttony);

    // 2GCD opener potion goes in the window AFTER Shadow of Death (The Balance, and the regression rule for it): Death's
    // Design is on the target and Soul Slice is not yet spent. Testing NextGCD == Shadow of Death selected the window before it.
    private bool IsNormalOpenerDeathsDesignGCD(in Strategy strategy)
        => !ShouldUseDancingMadProfile(strategy)
        && strategy.OpenerBurst.Value == OpenerBurstStrategy.Normal
        && IsInitialOpener
        && Player.InCombat
        && CombatTimer < 10f
        && TargetDDLeft > GCD
        && Unlocked(AID.SoulSlice)
        && !OnCooldown(AID.SoulSlice)
        && !Enshrouded
        && !ReaverOrExecutioner;

    private bool IsNormalOpenerExecutionerSequence(in Strategy strategy)
        => !ShouldUseDancingMadProfile(strategy)
        && strategy.OpenerBurst.Value == OpenerBurstStrategy.Normal
        && Player.InCombat
        && CombatTimer < 20f
        && _arcaneCircleReadyIn > 100f;

    private bool IsNormalOpenerArcaneCircleAfterSoulSlice(in Strategy strategy)
        => !ShouldUseDancingMadProfile(strategy)
        && strategy.OpenerBurst.Value == OpenerBurstStrategy.Normal
        && IsInitialOpener
        && Player.InCombat
        && CombatTimer < 20f
        && Unlocked(AID.SoulSlice)
        && OnCooldown(AID.SoulSlice)
        && _arcaneCircleReadyIn <= GCD
        && ArcaneCircleLeft <= 0
        && RedGauge >= 50
        && !Enshrouded
        && !ReaverOrExecutioner;

    private bool IsEarlyOpenerArcaneCircle(in Strategy strategy)
        => IsInitialOpener
        && CombatTimer < 10f
        && _arcaneCircleReadyIn <= GCD
        && (ShouldUseDancingMadProfile(strategy)
            || strategy.OpenerBurst.Value == OpenerBurstStrategy.ZeroSecondBurst
            || strategy.OpenerBurst.Value == OpenerBurstStrategy.TwoPointFiveSecondBurst);

    private bool IsDancingMadUltimate()
        => _frameCacheStage >= FrameCacheStage.Basics ? _frameDancingMadUltimate : ComputeDancingMadUltimate();

    private bool ComputeDancingMadUltimate()
        => World.CurrentCFCID == DancingMadCFCID
        || Bossmods.ActiveModule?.Info?.GroupType == BossModuleInfo.GroupType.CFC && Bossmods.ActiveModule.Info.GroupID == DancingMadCFCID
        || Bossmods.ActiveModule?.GetType().FullName?.Contains(".Dawntrail.Ultimate.DMU.", StringComparison.Ordinal) == true
        || Bossmods.ActiveModule?.GetType().Name == "DMU";

    private bool DancingMadProfileSelected(in Strategy strategy)
        => strategy.SkillRotation.Value == SkillRotationStrategy.DancingMad
        && IsDancingMadUltimate();

    private bool ShouldUseDancingMadProfile(in Strategy strategy)
        => _frameCacheStage >= FrameCacheStage.DancingMadProfile ? _frameDancingMadProfile : DancingMadProfileSelected(strategy) && Player.InCombat;

    private static DancingMadMeleePlan NoDancingMadMeleePlan(Actor? player, Actor? target)
    {
        var distance = player != null && target != null ? player.DistanceToHitbox(target) : 0;
        return new(
            false,
            target == null || distance <= DancingMadMeleeRange,
            target == null || distance <= DancingMadConeRange,
            target == null || distance <= DancingMadLineRange,
            target == null || distance <= DancingMadRangedRange,
            float.MaxValue,
            float.MaxValue,
            float.MaxValue,
            float.MaxValue,
            float.MaxValue,
            float.MaxValue,
            float.MaxValue,
            float.MaxValue);
    }

    private DancingMadMeleePlan GetDancingMadMeleePlan(in Strategy strategy, Actor? target)
    {
        if (strategy.AIHints.Value == AIHintsStrategy.Disable || !ShouldUseDancingMadProfile(strategy) || target == null)
            return NoDancingMadMeleePlan(Player, target);

        var zoneCount = Hints.ForbiddenZones.Count;
        var obstacleCount = Hints.TemporaryObstacles.Count;
        if (zoneCount == 0 && obstacleCount == 0)
            return NoDancingMadMeleePlan(Player, target);

        if (_dancingMadMeleePlanTargetID == target.InstanceID
            && _dancingMadMeleePlanForbiddenZoneCount == zoneCount
            && _dancingMadMeleePlanObstacleCount == obstacleCount
            && World.CurrentTime < _nextDancingMadMeleePlanRefreshAt)
            return _dancingMadMeleePlan;

        // Only the cone and ranged windows are consumed; the melee/line predictions are skipped to save the sampling cost.
        var (coneLossIn, coneResumeIn) = PredictDancingMadRangeWindow(target, DancingMadConeRange);
        var (rangedLossIn, rangedResumeIn) = PredictDancingMadRangeWindow(target, DancingMadRangedRange);
        var distance = Player.DistanceToHitbox(target);
        var hereSafeNow = !DancingMadForbiddenAt(Player.Position, 0);
        var plan = new DancingMadMeleePlan(
            true,
            distance <= DancingMadMeleeRange && hereSafeNow,
            distance <= DancingMadConeRange && hereSafeNow,
            distance <= DancingMadLineRange && hereSafeNow,
            distance <= DancingMadRangedRange && hereSafeNow,
            float.MaxValue,
            float.MaxValue,
            coneLossIn,
            coneResumeIn,
            float.MaxValue,
            float.MaxValue,
            rangedLossIn,
            rangedResumeIn);

        _dancingMadMeleePlanTargetID = target.InstanceID;
        _dancingMadMeleePlanForbiddenZoneCount = zoneCount;
        _dancingMadMeleePlanObstacleCount = obstacleCount;
        _nextDancingMadMeleePlanRefreshAt = World.CurrentTime.AddSeconds(DancingMadMeleePlanCacheSeconds);
        return plan;
    }

    private (float LossIn, float ResumeIn) PredictDancingMadRangeWindow(Actor target, float actionRange)
    {
        var lossIn = float.MaxValue;
        for (var at = 0f; at <= DancingMadMeleePlanHorizon; at += DancingMadMeleePlanSampleStep)
        {
            if (!CanReachDancingMadSafePointAt(target, actionRange, at + DancingMadMovementSafetyBuffer))
            {
                lossIn = at;
                break;
            }
        }

        if (lossIn >= float.MaxValue / 2)
            return (float.MaxValue, float.MaxValue);

        for (var at = lossIn + DancingMadMeleePlanSampleStep; at <= DancingMadMeleePlanHorizon; at += DancingMadMeleePlanSampleStep)
            if (CanReachDancingMadSafePointAt(target, actionRange, at))
                return (lossIn, at);

        return (lossIn, float.MaxValue);
    }

    private bool CanReachDancingMadSafePointAt(Actor target, float actionRange, float at)
    {
        if (Player.DistanceToHitbox(target) <= actionRange && !DancingMadForbiddenAt(Player.Position, at))
            return true;

        var moveTime = Math.Max(0, at - DancingMadMovementSafetyBuffer);
        var maxMove = World.Client.MoveSpeed * moveTime;
        var maxMoveSq = maxMove * maxMove;
        var innerOffset = Math.Min(1.2f, actionRange - 0.2f);
        var middleOffset = Math.Max(innerOffset, actionRange * 0.65f);
        var outerOffset = Math.Max(middleOffset, actionRange - 0.2f);
        ReadOnlySpan<float> radiusOffsets = stackalloc float[] { innerOffset, middleOffset, outerOffset };
        foreach (var radiusOffset in radiusOffsets)
        {
            var radius = target.HitboxRadius + radiusOffset;
            for (var i = 0; i < DancingMadSafePointSamples; ++i)
            {
                var angle = MathF.PI * 2 * i / DancingMadSafePointSamples;
                var point = target.Position + new WDir(MathF.Cos(angle), MathF.Sin(angle)) * radius;
                if (!DancingMadForbiddenAt(point, at)
                    && (point - Player.Position).LengthSq() <= maxMoveSq
                    && DancingMadPathIsSafe(Player.Position, point, at))
                    return true;
            }
        }

        return false;
    }

    private bool DancingMadPathIsSafe(WPos from, WPos to, float at)
    {
        var delta = to - from;
        for (var i = 1; i <= DancingMadPathSafetySteps; ++i)
        {
            var progress = i / (float)DancingMadPathSafetySteps;
            if (DancingMadForbiddenAt(from + delta * progress, at * progress))
                return false;
        }

        return true;
    }

    private bool DancingMadForbiddenAt(WPos position, float at)
    {
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

    private bool ShouldHoldDancingMadEnshroudForMeleePlan(in Strategy strategy)
    {
        if (strategy.Enshroud.Value != OffensiveStrategy.Automatic
            || !ShouldUseDancingMadProfile(strategy)
            || !_dancingMadMeleePlan.AssignmentRevealed)
            return false;

        if (!_dancingMadMeleePlan.CanConeNow)
            return !CanStartDancingMadEnshroudBeforeConeReturn(strategy);

        if (_dancingMadMeleePlan.ConeLossIn >= float.MaxValue / 2)
            return false;

        var coneResumeIn = _dancingMadMeleePlan.ConeResumeIn;
        for (var reaping = 0; reaping < 4; ++reaping)
        {
            var reapingAt = BurstReferenceGCDLength + EnshroudGCDLength * reaping;
            if (reapingAt + DancingMadMovementSafetyBuffer >= _dancingMadMeleePlan.ConeLossIn
                && (coneResumeIn >= float.MaxValue / 2 || reapingAt < coneResumeIn + DancingMadMovementSafetyBuffer))
                return true;
        }

        return false;
    }

    private bool CanStartDancingMadEnshroudBeforeConeReturn(in Strategy strategy)
        => strategy.Enshroud.Value == OffensiveStrategy.Automatic
        && ShouldUseDancingMadProfile(strategy)
        && HostileAOEAllowed(strategy)
        && Unlocked(AID.GrimReaping)
        && _dancingMadMeleePlan.AssignmentRevealed
        && !_dancingMadMeleePlan.CanConeNow
        && _dancingMadMeleePlan.ConeResumeIn < BurstReferenceGCDLength - DancingMadMovementSafetyBuffer;

    private bool ShouldUseDancingMadPlannerHarpe(in Strategy strategy, Enemy? rangedTarget)
        => ShouldUseDancingMadProfile(strategy)
        && _dancingMadMeleePlan.AssignmentRevealed
        && !_dancingMadMeleePlan.CanConeNow
        && _dancingMadMeleePlan.CanRangedNow
        && rangedTarget != null
        && Player.DistanceToHitbox(rangedTarget.Actor) > DancingMadConeRange
        && !IsMoving
        && !DancingMadForbiddenAt(Player.Position, 0);

    private static int DancingMadFallbackPhaseIndex(float combatTimer)
        => combatTimer switch
        {
            < 209f => 0,
            < 429f => 1,
            < 728f => 2,
            < 890f => 3,
            _ => 4
        };

    private bool TryDancingMadPhase4Opener(in Strategy strategy, Enemy? meleeTarget, Enemy? rangedTarget, bool fullMode)
    {
        var stateMachine = Bossmods.ActiveModule?.StateMachine;
        if (!DancingMadProfileSelected(strategy)
            || !fullMode
            || !Player.InCombat
            || stateMachine?.ActivePhaseIndex != 3
            || stateMachine.TimeSincePhaseEnter > DancingMadP4OpenerWindow)
            return false;

        var enshroudCommitted = Enshrouded || DancingMadP4ActionCommitted(AID.Enshroud);
        var arcaneCircleCommitted = ArcaneCircleLeft > 0 || DancingMadP4ActionCommitted(AID.ArcaneCircle);
        var targetAvailable = rangedTarget != null;
        if (!targetAvailable)
        {
            var targetableIn = DancingMadP4TargetableIn(stateMachine);
            if (targetableIn > DancingMadP4ArcaneCircleLead
                && targetableIn <= DancingMadP4EnshroudLead
                && !enshroudCommitted
                && CanUseDancingMadP4Enshroud(strategy))
            {
                PushOGCD(AID.Enshroud, Player, OGCDPriority.EnshroudBeforeSoulOvercap);
                return true;
            }

            if (targetableIn <= DancingMadP4ArcaneCircleLead
                && !arcaneCircleCommitted
                && CanUseDancingMadP4ArcaneCircle(strategy))
            {
                PushOGCD(AID.ArcaneCircle, Player, OGCDPriority.ArcaneCircle);
                return true;
            }

            if (targetableIn <= DancingMadP4ArcaneCircleLead
                && arcaneCircleCommitted
                && !enshroudCommitted
                && CanUseDancingMadP4Enshroud(strategy))
            {
                PushOGCD(AID.Enshroud, Player, OGCDPriority.EnshroudBeforeSoulOvercap);
                return true;
            }

            return targetableIn < float.MaxValue / 2 && (enshroudCommitted || arcaneCircleCommitted);
        }

        var deathsDesignCommitted = TargetDDLeft > GCD || LastActionUsedRecently(AID.ShadowofDeath, GCDLength + 1f);
        if (!deathsDesignCommitted && Unlocked(AID.ShadowofDeath) && meleeTarget is { ForbidDOTs: false })
        {
            PushGCD(AID.ShadowofDeath, meleeTarget, GCDPriority.Max);
            if (!enshroudCommitted && CanUseDancingMadP4Enshroud(strategy))
                PushOGCD(AID.Enshroud, Player, OGCDPriority.EnshroudBeforeSoulOvercap);
            else if (!arcaneCircleCommitted && CanUseDancingMadP4ArcaneCircle(strategy))
                PushOGCD(AID.ArcaneCircle, Player, OGCDPriority.ArcaneCircle);
            return true;
        }

        if (!enshroudCommitted && CanUseDancingMadP4Enshroud(strategy))
        {
            PushOGCD(AID.Enshroud, Player, OGCDPriority.EnshroudBeforeSoulOvercap);
            return true;
        }

        if (!arcaneCircleCommitted && CanUseDancingMadP4ArcaneCircle(strategy))
        {
            PushOGCD(AID.ArcaneCircle, Player, OGCDPriority.ArcaneCircle);
            return true;
        }

        return false;
    }

    private float DancingMadP4TargetableIn(StateMachine stateMachine)
    {
        if (Manager.Planner is { } planner)
        {
            var windows = _dancingMadP4Windows;
            planner.EstimateTargetableWindows(5f, windows);
            foreach (var window in windows)
                if (window.Targetable && window.EndIn > window.StartIn)
                    return window.StartIn;
        }

        var activeState = stateMachine.ActiveState;
        return activeState != null && activeState.EndHint.HasFlag(StateMachine.StateHint.DowntimeEnd)
            ? Math.Max(0, activeState.Duration - stateMachine.TimeSinceTransition)
            : float.MaxValue;
    }

    private bool DancingMadP4ActionCommitted(AID action)
    {
        if (LastActionUsedRecently(action, 1f))
            return true;

        var request = Manager.LastActionRequest;
        return request.Data.Action == ActionID.MakeSpell(action)
            && (World.CurrentTime - request.Time).TotalSeconds is >= 0 and <= 1;
    }

    private bool CanUseDancingMadP4Enshroud(in Strategy strategy)
        => strategy.Enshroud.Value != OffensiveStrategy.Delay
        && Unlocked(AID.Enshroud)
        && ReadyIn(AID.Enshroud) <= 0.1f
        && BlueGauge >= 50
        && !Enshrouded
        && !ReaverOrExecutioner;

    private bool CanUseDancingMadP4ArcaneCircle(in Strategy strategy)
        => strategy.Buffs.Value != OffensiveStrategy.Delay
        && Unlocked(AID.ArcaneCircle)
        && _arcaneCircleReadyIn <= 0.1f
        && ArcaneCircleLeft <= 0;

    private int DancingMadPhaseIndex()
    {
        var phaseIndex = Bossmods.ActiveModule?.StateMachine.ActivePhaseIndex ?? -1;
        return phaseIndex is >= 0 and < 5 ? phaseIndex : DancingMadFallbackPhaseIndex(CombatTimer);
    }

    private bool DancingMadPhaseJustStarted(int phaseIndex)
    {
        var stateMachine = Bossmods.ActiveModule?.StateMachine;
        if (stateMachine?.ActivePhaseIndex == phaseIndex)
            return stateMachine.TimeSincePhaseEnter <= 5f;

        var phaseStart = phaseIndex switch
        {
            0 => 0f,
            1 => 209f,
            2 => 429f,
            3 => 728f,
            4 => 890f,
            _ => float.NegativeInfinity
        };
        return CombatTimer >= phaseStart && CombatTimer <= phaseStart + 5f;
    }

    private static bool DancingMadBurstEnshroudWindowOpen(float combatTimer)
    {
        if (combatTimer < 40f)
            return true;

        foreach (var window in DancingMadArcaneCircleWindows)
            if (combatTimer >= window.Time - 20f && combatTimer <= window.Time + 40f)
                return true;

        return false;
    }

    private void ResetDancingMadEnshroudBudget()
    {
        _dancingMadEnshroudPhase = -1;
        _dancingMadBurstEnshrouds = 0;
        _dancingMadNormalEnshrouds = 0;
        _dancingMadEnshroudBudgetActive = false;
        _dancingMadLastObservedEnshroudCast = default;
    }

    private void UpdateDancingMadEnshroudBudget(in Strategy strategy, bool fullMode)
    {
        if (!ShouldUseDancingMadProfile(strategy) || !fullMode)
        {
            ResetDancingMadEnshroudBudget();
            return;
        }

        var phaseIndex = DancingMadPhaseIndex();
        if (phaseIndex != _dancingMadEnshroudPhase)
        {
            _dancingMadEnshroudPhase = phaseIndex;
            _dancingMadBurstEnshrouds = 0;
            _dancingMadNormalEnshrouds = 0;
            _dancingMadEnshroudBudgetActive = DancingMadPhaseJustStarted(phaseIndex);
            _dancingMadLastObservedEnshroudCast = Manager.LastCast.Time;
        }

        var cast = Manager.LastCast.Data;
        if (!_dancingMadEnshroudBudgetActive
            || cast == null
            || cast.SourceSequence == 0
            || Manager.LastCast.Time == _dancingMadLastObservedEnshroudCast
            || (World.CurrentTime - Manager.LastCast.Time).TotalSeconds is < 0 or > 5
            || !cast.IsSpell(AID.Enshroud))
            return;

        _dancingMadLastObservedEnshroudCast = Manager.LastCast.Time;
        if (DancingMadBurstEnshroudWindowOpen(CombatTimer))
            ++_dancingMadBurstEnshrouds;
        else
            ++_dancingMadNormalEnshrouds;
    }

    private DancingMadEnshroudPolicy DancingMadEnshroudUsePolicy(in Strategy strategy, bool fullMode)
    {
        if (!ShouldUseDancingMadProfile(strategy)
            || !fullMode
            || !_dancingMadEnshroudBudgetActive
            || _dancingMadEnshroudPhase is < 0 or >= 5)
            return DancingMadEnshroudPolicy.StateBased;

        var budget = DancingMadEnshroudBudgets[_dancingMadEnshroudPhase];
        var burstWindow = DancingMadBurstEnshroudWindowOpen(CombatTimer);
        var used = burstWindow ? _dancingMadBurstEnshrouds : _dancingMadNormalEnshrouds;
        var target = burstWindow ? budget.Burst : budget.Normal;
        return used < target ? DancingMadEnshroudPolicy.Release : DancingMadEnshroudPolicy.Hold;
    }

    private void AdvanceDancingMadOpenerStage()
    {
        var recent = GCDLength + 1f;
        _dancingMadOpenerStage = _dancingMadOpenerStage switch
        {
            DancingMadOpenerStage.DeathsDesign when LastActionUsedRecently(AID.ShadowofDeath, recent) => DancingMadOpenerStage.SoulSlice,
            DancingMadOpenerStage.SoulSlice when LastActionUsedRecently(AID.SoulSlice, recent) => DancingMadOpenerStage.Gluttony,
            DancingMadOpenerStage.Gluttony when Executioner || LastActionUsedRecently(AID.Gluttony, recent) => DancingMadOpenerStage.ExecutionersGallows,
            DancingMadOpenerStage.ExecutionersGallows when LastActionUsedRecently(AID.ExecutionersGallows, recent) => DancingMadOpenerStage.ExecutionersGibbet,
            DancingMadOpenerStage.ExecutionersGibbet when LastActionUsedRecently(AID.ExecutionersGibbet, recent) => DancingMadOpenerStage.PlentifulHarvest,
            DancingMadOpenerStage.PlentifulHarvest when IdealHost > 0 || LastActionUsedRecently(AID.PlentifulHarvest, recent) => DancingMadOpenerStage.Enshroud,
            DancingMadOpenerStage.Enshroud when Enshrouded => DancingMadOpenerStage.Sacrificium,
            DancingMadOpenerStage.Sacrificium when LastActionUsedRecently(AID.Sacrificium, recent) => DancingMadOpenerStage.VoidReaping1,
            DancingMadOpenerStage.VoidReaping1 when BlueSouls == 4 && PurpleSouls == 1 => DancingMadOpenerStage.CrossReaping1,
            DancingMadOpenerStage.CrossReaping1 when BlueSouls == 3 && PurpleSouls == 2 => DancingMadOpenerStage.LemuresSlice1,
            DancingMadOpenerStage.LemuresSlice1 when BlueSouls == 3 && PurpleSouls == 0 => DancingMadOpenerStage.VoidReaping2,
            DancingMadOpenerStage.VoidReaping2 when BlueSouls == 2 && PurpleSouls == 1 => DancingMadOpenerStage.CrossReaping2,
            DancingMadOpenerStage.CrossReaping2 when BlueSouls == 1 && PurpleSouls == 2 => DancingMadOpenerStage.LemuresSlice2,
            DancingMadOpenerStage.LemuresSlice2 when BlueSouls == 1 && PurpleSouls == 0 => DancingMadOpenerStage.Communio,
            DancingMadOpenerStage.Communio when LastActionUsedRecently(AID.Communio, recent) => DancingMadOpenerStage.Perfectio,
            DancingMadOpenerStage.Perfectio when LastActionUsedRecently(AID.Perfectio, recent) => DancingMadOpenerStage.FollowupSoulSlice,
            DancingMadOpenerStage.FollowupSoulSlice when LastActionUsedRecently(AID.SoulSlice, recent) => DancingMadOpenerStage.FollowupStalk,
            DancingMadOpenerStage.FollowupStalk when LastActionUsedRecently(AID.BloodStalk, recent)
                || LastActionUsedRecently(AID.UnveiledGibbet, recent)
                || LastActionUsedRecently(AID.UnveiledGallows, recent)
                || LastActionUsedRecently(AID.GrimSwathe, recent) => DancingMadOpenerStage.FollowupGallows,
            DancingMadOpenerStage.FollowupGallows when LastActionUsedRecently(AID.Gallows, recent)
                || LastActionUsedRecently(AID.Guillotine, recent) => DancingMadOpenerStage.FollowupDeathsDesign,
            DancingMadOpenerStage.FollowupDeathsDesign when LastActionUsedRecently(AID.ShadowofDeath, recent) => DancingMadOpenerStage.Complete,
            _ => _dancingMadOpenerStage
        };
    }

    private bool TryDancingMadFixedOpener(in Strategy strategy, Enemy? meleeTarget, Enemy? rangedTarget, bool fullMode)
    {
        if (!DancingMadProfileSelected(strategy) || !fullMode)
        {
            if (!Player.InCombat)
                _dancingMadOpenerStage = DancingMadOpenerStage.Inactive;
            return false;
        }

        if (!CanUseDancingMadFixedOpener(strategy))
        {
            _dancingMadOpenerStage = DancingMadOpenerStage.Complete;
            return false;
        }

        if (CountdownRemaining > 0)
        {
            _dancingMadOpenerStage = DancingMadOpenerStage.DeathsDesign;
            var harpeStartIn = EnhancedHarpe > GCD ? 0 : OpenerHarpeStartIn;
            if (CountdownRemaining <= harpeStartIn && strategy.Harpe.Value != HarpeStrategy.Forbid && Unlocked(AID.Harpe))
            {
                var target = HarpeTarget(strategy, rangedTarget);
                if (target != null)
                    PushGCD(AID.Harpe, target, GCDPriority.Max);
            }
            return true;
        }

        if (!Player.InCombat)
        {
            _dancingMadOpenerStage = DancingMadOpenerStage.Inactive;
            return false;
        }

        if (_dancingMadOpenerStage == DancingMadOpenerStage.Inactive)
            _dancingMadOpenerStage = CombatTimer < 5f ? DancingMadOpenerStage.DeathsDesign : DancingMadOpenerStage.Complete;

        if (CombatTimer >= 40f)
            _dancingMadOpenerStage = DancingMadOpenerStage.Complete;

        AdvanceDancingMadOpenerStage();
        if (_dancingMadOpenerStage == DancingMadOpenerStage.Complete)
            return false;

        if (CombatTimer < 10f && Unlocked(AID.ArcaneCircle) && ArcaneCircleLeft <= 0 && _arcaneCircleReadyIn <= GCD)
            PushOGCD(AID.ArcaneCircle, Player, OGCDPriority.ArcaneCircle, delay: ArcaneCircleWeaveDelay(strategy));

        var melee = UsableTarget(meleeTarget, 3);
        switch (_dancingMadOpenerStage)
        {
            case DancingMadOpenerStage.DeathsDesign:
                if (Unlocked(AID.ShadowofDeath) && melee != null)
                    PushGCD(AID.ShadowofDeath, melee, GCDPriority.Max);
                break;
            case DancingMadOpenerStage.SoulSlice:
            case DancingMadOpenerStage.FollowupSoulSlice:
                if (Unlocked(AID.SoulSlice) && GCDReady(AID.SoulSlice) && melee != null)
                    PushGCD(AID.SoulSlice, melee, GCDPriority.Max);
                break;
            case DancingMadOpenerStage.Gluttony:
            {
                var target = TargetOverrideOrDefault(strategy.RedGauge, BestRangedAOETarget, 25, IsSplashTarget);
                if (Unlocked(AID.Gluttony) && RedGauge >= 50 && target != null)
                    PushOGCD(AID.Gluttony, target, OGCDPriority.Gluttony);
                break;
            }
            case DancingMadOpenerStage.ExecutionersGallows:
                if (Executioner && Unlocked(AID.ExecutionersGallows) && melee != null)
                    PushGCD(AID.ExecutionersGallows, melee, GCDPriority.Max);
                break;
            case DancingMadOpenerStage.ExecutionersGibbet:
                if (Executioner && Unlocked(AID.ExecutionersGibbet) && melee != null)
                    PushGCD(AID.ExecutionersGibbet, melee, GCDPriority.Max);
                break;
            case DancingMadOpenerStage.PlentifulHarvest:
            {
                var target = TargetOverrideOrDefault(strategy.PH, BestLineTarget, 15, LineTargetCheck);
                if (Unlocked(AID.PlentifulHarvest) && ImmortalSacrifice.Left > GCD && BloodsownCircle <= GCD && target != null)
                    PushGCD(AID.PlentifulHarvest, target, GCDPriority.Max);
                break;
            }
            case DancingMadOpenerStage.Enshroud:
                if (Unlocked(AID.Enshroud) && !Enshrouded && IdealHost > 0 && !ShouldHoldDancingMadEnshroudForMeleePlan(strategy))
                    PushOGCD(AID.Enshroud, Player, OGCDPriority.EnshroudBeforeSoulOvercap);
                break;
            case DancingMadOpenerStage.Sacrificium:
            {
                var target = TargetOverrideOrDefault(strategy.RedGauge, BestRangedAOETarget, 25, IsSplashTarget);
                if (Unlocked(AID.Sacrificium) && Enshrouded && Oblatio > 0 && target != null)
                    PushOGCD(AID.Sacrificium, target, OGCDPriority.Sacrificium);
                break;
            }
            case DancingMadOpenerStage.VoidReaping1:
            case DancingMadOpenerStage.VoidReaping2:
                if (Enshrouded && Unlocked(AID.VoidReaping) && melee != null)
                    PushGCD(AID.VoidReaping, melee, GCDPriority.Max);
                else if (Enshrouded && HostileAOEAllowed(strategy) && Unlocked(AID.GrimReaping) && BestConeTarget != null)
                    PushGCD(AID.GrimReaping, BestConeTarget, GCDPriority.Max);
                break;
            case DancingMadOpenerStage.CrossReaping1:
            case DancingMadOpenerStage.CrossReaping2:
                if (Enshrouded && Unlocked(AID.CrossReaping) && melee != null)
                    PushGCD(AID.CrossReaping, melee, GCDPriority.Max);
                else if (Enshrouded && HostileAOEAllowed(strategy) && Unlocked(AID.GrimReaping) && BestConeTarget != null)
                    PushGCD(AID.GrimReaping, BestConeTarget, GCDPriority.Max);
                break;
            case DancingMadOpenerStage.LemuresSlice1:
            case DancingMadOpenerStage.LemuresSlice2:
                if (Enshrouded && PurpleSouls > 1 && Unlocked(AID.LemuresSlice) && melee != null)
                    PushOGCD(AID.LemuresSlice, melee, OGCDPriority.Lemure);
                else if (Enshrouded && PurpleSouls > 1 && HostileAOEAllowed(strategy) && Unlocked(AID.LemuresScythe) && BestConeTarget != null)
                    PushOGCD(AID.LemuresScythe, BestConeTarget, OGCDPriority.Lemure);
                break;
            case DancingMadOpenerStage.Communio:
            {
                var target = TargetOverrideOrDefault(strategy.Communio, BestRangedAOETarget, 25, IsSplashTarget);
                if (BlueSouls == 1 && Unlocked(AID.Communio) && target != null)
                    PushGCD(AID.Communio, target, GCDPriority.Max);
                break;
            }
            case DancingMadOpenerStage.Perfectio:
            {
                var target = TargetOverrideOrDefault(strategy.Perf, BestRangedAOETarget, 25, IsSplashTarget);
                if (PerfectioAvailable && target != null)
                    PushGCD(AID.Perfectio, target, GCDPriority.Max);
                break;
            }
            case DancingMadOpenerStage.FollowupStalk:
            {
                var targetOverride = ResolveTargetOverride(strategy.RedGauge);
                var target = targetOverride != null ? UsableTarget(targetOverride, 3) : melee;
                var action = EnhancedGallows > GCD && Unlocked(AID.UnveiledGallows) ? AID.UnveiledGallows : AID.BloodStalk;
                if (Unlocked(action) && RedGauge >= 50 && target != null)
                    PushOGCD(action, target, OGCDPriority.BloodStalk);
                else if (RedGauge >= 50 && HostileAOEAllowed(strategy) && Unlocked(AID.GrimSwathe) && BestConeTarget != null)
                    PushOGCD(AID.GrimSwathe, BestConeTarget, OGCDPriority.BloodStalk);
                break;
            }
            case DancingMadOpenerStage.FollowupGallows:
                if (SoulReaverActive && Unlocked(AID.Gallows) && melee != null)
                    PushGCD(AID.Gallows, melee, GCDPriority.Max);
                else if (SoulReaverActive && HostileAOEAllowed(strategy) && Unlocked(AID.Guillotine) && BestConeTarget != null)
                    PushGCD(AID.Guillotine, BestConeTarget, GCDPriority.Max);
                break;
            case DancingMadOpenerStage.FollowupDeathsDesign:
                if (Unlocked(AID.ShadowofDeath) && melee != null)
                    PushGCD(AID.ShadowofDeath, melee, GCDPriority.Max);
                break;
        }

        if (_dancingMadMeleePlan.AssignmentRevealed && !_dancingMadMeleePlan.CanConeNow)
        {
            HarvestMoon(strategy, rangedTarget);
            if (strategy.Harpe.Value == HarpeStrategy.Automatic
                && Unlocked(AID.Harpe)
                && ShouldUseDancingMadPlannerHarpe(strategy, rangedTarget))
            {
                var target = HarpeTarget(strategy, rangedTarget);
                if (target != null)
                    PushGCD(AID.Harpe, target, GCDPriority.Harpe);
            }
        }

        return true;
    }

    private bool CanUseDancingMadFixedOpener(in Strategy strategy)
        => strategy.Buffs.Value != OffensiveStrategy.Delay
        && strategy.Slice.Value != SliceStrategy.Delay
        && strategy.RedGauge.Value is RedGaugeStrategy.Automatic or RedGaugeStrategy.Force
        && strategy.Enshroud.Value != OffensiveStrategy.Delay
        && strategy.PH.IsEnabled()
        && strategy.Communio.IsEnabled()
        && strategy.Perf.Value == PerfectioStrategy.Automatic
        && HostileAOEAllowed(strategy);

    private bool IsInDancingMadWindow(DancingMadBurstWindow[] windows)
    {
        foreach (var window in windows)
            if (CombatTimer >= window.Time - window.Before && CombatTimer <= window.Time + window.After)
                return true;

        return false;
    }

    private bool ShouldHoldForDancingMadArcaneCircle()
    {
        foreach (var window in DancingMadArcaneCircleWindows)
        {
            var startsIn = window.Time - window.Before - CombatTimer;
            if (startsIn > DancingMadArcaneCircleHoldLead)
                return false;

            if (startsIn > 0)
                return _arcaneCircleReadyIn <= startsIn + window.Before + window.After;
        }

        return false;
    }

    private bool DancingMadPotionWindowOpen(in Strategy strategy)
        => ShouldUseDancingMadProfile(strategy)
        && IsInDancingMadWindow(DancingMadPotionWindows)
        && (RaidBuffsLeft > 0 || PotionBeforeArcaneCircleWindow(strategy) || ShouldQueueArcaneCircle(strategy, fullMode: true));

    private uint WindurstEncounterNameID()
    {
        var info = Bossmods.ActiveModule?.Info;
        return info?.GroupType == BossModuleInfo.GroupType.CFC && info.GroupID == WindurstThirdWalkCFCID
            ? info.NameID
            : 0;
    }

    private bool IsWindurstThirdWalk()
        => World.CurrentCFCID == WindurstThirdWalkCFCID
        || Bossmods.ActiveModule?.Info?.GroupType == BossModuleInfo.GroupType.CFC && Bossmods.ActiveModule.Info.GroupID == WindurstThirdWalkCFCID;

    private bool ShouldUseWindurstThirdWalkProfile(in Strategy strategy)
        => strategy.SkillRotation.Value == SkillRotationStrategy.WindurstThirdWalk
        && IsWindurstThirdWalk()
        && WindurstEncounterNameID() != 0
        && Player.InCombat;

    private WindurstBurstWindow[] WindurstArcaneCircleWindows()
        => WindurstEncounterNameID() switch
        {
            WindurstShantottoNameID => WindurstShantottoArcaneCircleWindows,
            WindurstAlexanderNameID => WindurstAlexanderArcaneCircleWindows,
            WindurstPromathiaNameID => WindurstPromathiaArcaneCircleWindows,
            WindurstHollowKingNameID => WindurstHollowKingArcaneCircleWindows,
            _ => NoWindurstWindows
        };

    private WindurstBurstWindow[] WindurstPotionWindows()
        => WindurstEncounterNameID() switch
        {
            WindurstShantottoNameID => WindurstShantottoPotionWindows,
            WindurstHollowKingNameID => WindurstHollowKingPotionWindows,
            _ => NoWindurstWindows
        };

    private bool IsInWindurstWindow(WindurstBurstWindow[] windows)
    {
        foreach (var window in windows)
            if (CombatTimer >= window.Time - window.Before && CombatTimer <= window.Time + window.After)
                return true;

        return false;
    }

    private bool ShouldHoldForWindurstArcaneCircle(WindurstBurstWindow[] windows)
    {
        foreach (var window in windows)
        {
            var startsIn = window.Time - window.Before - CombatTimer;
            if (startsIn > WindurstArcaneCircleHoldLead)
                return false;

            if (startsIn > 0)
                return _arcaneCircleReadyIn <= startsIn + window.Before + window.After;
        }

        return false;
    }

    private bool WindurstPotionWindowOpen(in Strategy strategy)
        => ShouldUseWindurstThirdWalkProfile(strategy)
        && IsInWindurstWindow(WindurstPotionWindows())
        && (RaidBuffsLeft > 0 || PotionBeforeArcaneCircleWindow(strategy) || ShouldQueueArcaneCircle(strategy, fullMode: true));

    private bool CanQueueArcaneCircleThisGCD()
    {
        var latestSafeWeave = GCD - 0.8f;
        if (latestSafeWeave < 0)
            latestSafeWeave = 0;

        return _arcaneCircleReadyIn <= latestSafeWeave;
    }

    // rDPS: outside the opener, a ready Arcane Circle waits (briefly) for the party's raid-buff window so both the party's
    // burst and our own Enshroud/Perfectio land inside it. The planned time replaces the raw cooldown for every AC decision.
    private float AlignArcaneCircleWithRaidBuffs(in Strategy strategy, bool fullMode, float readyIn)
    {
        if (!fullMode
            || strategy.Buffs != OffensiveStrategy.Automatic
            || strategy.SkillRotation.Value != SkillRotationStrategy.Normal
            || ShouldUseDancingMadProfile(strategy)
            || ShouldUseWindurstThirdWalkProfile(strategy)
            || !Player.InCombat
            || CombatTimer < 30f
            || RaidBuffsLeft > 0)
            return readyIn;

        // RaidBuffsIn counts our own Arcane Circle as a raid buff; look at the party's other damage buffs only.
        var buffsIn = float.MaxValue;
        Bossmods.RaidCooldowns.DamageCooldowns(_damageCooldownScratch);
        foreach (var cooldown in _damageCooldownScratch)
            if (cooldown.Action.ID != (uint)AID.ArcaneCircle)
                buffsIn = Math.Min(buffsIn, cooldown.AvailableIn);
        if (buffsIn <= 0 || buffsIn >= float.MaxValue / 2)
            return readyIn;

        var alignedIn = buffsIn - ArcaneCircleRaidBuffLead;
        if (alignedIn <= readyIn || alignedIn - readyIn > ArcaneCircleRaidBuffAlignmentMaxHold)
            return readyIn;

        // Holding is only worth it if the aligned burst still fits before the next known target loss.
        var targetLossIn = DeathsDesignTargetLossIn();
        if (ValidDeathsDesignHintTime(targetLossIn) && alignedIn + TimelineBurstRequiredUptime > targetLossIn)
            return readyIn;

        // Even a short untargetable window inside the aligned burst stretches it past the party window: keep the natural timing.
        if (_hasMechanicHint
            && ValidDeathsDesignHintTime(_mechanicHint.TargetLossIn)
            && _mechanicHint.TargetLossIn < alignedIn + TimelineBurstRequiredUptime)
            return readyIn;

        return alignedIn;
    }

    private float ArcaneCircleWeaveDelay(in Strategy strategy, bool preserveArcaneCircleTiming = false)
    {
        if (preserveArcaneCircleTiming)
            return Math.Max(0, _arcaneCircleReadyIn);

        var firstWeaveDelay = GCD - 1.6f;
        if (firstWeaveDelay < 0)
            firstWeaveDelay = 0;

        if (_degradedEvenBurstFallback)
        {
            var lateWeaveDelay = GCD - 0.8f;
            if (lateWeaveDelay > firstWeaveDelay)
                firstWeaveDelay = lateWeaveDelay;
        }

        // 2GCD opener: take the first weave slot after Soul Slice, so Gluttony still fits in the second one and Bloodsown
        // Circle (6 s plus status latency) has fallen before Plentiful Harvest three GCDs later. A delay measured from
        // "now" would slide forward every frame and never fire.
        if (IsNormalOpenerArcaneCircleAfterSoulSlice(strategy))
            firstWeaveDelay = 0;

        if (!preserveArcaneCircleTiming && _arcaneCircleUses == 1 && !IsLaterPotionBurst() && _deferredPreArcaneEnshroud)
        {
            var lateWeaveDelay = GCD - 0.8f;
            if (lateWeaveDelay > firstWeaveDelay)
                firstWeaveDelay = lateWeaveDelay;
        }

        return _arcaneCircleReadyIn > firstWeaveDelay ? _arcaneCircleReadyIn : firstWeaveDelay;
    }

    private float PotionWeaveDelay()
    {
        var firstWeaveDelay = GCD - 1.6f;
        return firstWeaveDelay < 0 ? 0 : firstWeaveDelay;
    }

    private bool IsLaterPotionBurst()
        => _arcaneCircleUses >= 2 || CombatTimer >= ArcaneCircleRecast * 2;

    private bool ShouldDelayPotionForTripleEnshroud(in Strategy strategy, bool fullMode)
        => IsEvenBurstPlanningWindow(strategy, fullMode)
        && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
        && strategy.Buffs == OffensiveStrategy.Automatic
        && strategy.Enshroud == OffensiveStrategy.Automatic
        && ArcaneCircleLeft <= 0
        && _arcaneCircleReadyIn <= EvenBurstShroudPlanLead
        && strategy.Perf.Value == PerfectioStrategy.Automatic
        && strategy.Communio.IsEnabled()
        && Unlocked(AID.Perfectio)
        && BlueGauge >= (Enshrouded ? 50 : 100);

    private bool PotionAfterArcaneCircleWindow()
        => !float.IsNaN(_potionAfterArcaneCircleUntil)
        && CombatTimer <= _potionAfterArcaneCircleUntil
        && ArcaneCircleLeft > 0;

    private bool PotionBeforeArcaneCircleWindow(in Strategy strategy)
    {
        if (IsEarlyOpenerArcaneCircle(strategy))
            return true;

        if (Enshrouded
            && BlueSouls == 4
            && _deferredPreArcaneEnshroud
            && ArcaneCircleLeft <= 0
            && (LastActionUsedRecently(AID.ShadowofDeath, GCDLength + 1f) || LastActionUsedRecently(AID.WhorlofDeath, GCDLength + 1f))
            && ShouldQueueArcaneCircle(strategy, fullMode: true))
            return true;

        var ddTimer = Math.Min(TargetDDLeft, ShortestNearbyDDLeft);
        if (Enshrouded
            && BlueSouls is > 1 and <= 4
            && ArcaneCircleLeft <= 0
            && ShouldUseTwoDeathsDesignRefreshesForEvenBurst(strategy, fullMode: true, ddTimer))
            return ShouldQueueArcaneCircle(strategy, fullMode: true);

        if (ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(strategy, fullMode: true, ddTimer))
            return Enshrouded && ShouldQueueArcaneCircle(strategy, fullMode: true);

        if (ShouldDeferDeathsDesignRefreshToPreArcaneEnshroud(strategy, fullMode: true, ddTimer))
            return false;

        var earliest = GCD + BurstPlanGCDLength * 0.5f;
        var latest = GCD + BurstPlanGCDLength * 1.5f;
        return OpenerPotionBeforeArcaneCircleWindow(strategy) || _arcaneCircleReadyIn > earliest && _arcaneCircleReadyIn <= latest;
    }

    private bool OpenerPotionBeforeArcaneCircleWindow(in Strategy strategy)
    {
        return IsNormalOpenerDeathsDesignGCD(strategy)
            || IsInitialOpener
            && CombatTimer < 30
            && _arcaneCircleReadyIn <= 0.5f
            && TargetDDLeft > GCD
            && Unlocked(AID.SoulSlice)
            && !OnCooldown(AID.SoulSlice)
            && !Enshrouded
            && !ReaverOrExecutioner;
    }

    private bool ShouldUsePotion(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        if (!fullMode || primaryTarget == null || !Player.InCombat)
            return false;

        if (!CanUsePotion())
            return false;

        if (!Unlocked(AID.ArcaneCircle) || strategy.Buffs == OffensiveStrategy.Delay)
            return false;

        if (strategy.Potion.Value == PotionUseStrategy.Off)
            return false;

        var opener = CombatTimer < 30f;
        if (strategy.Potion.Value == PotionUseStrategy.EvenBurstExceptOpener && opener)
            return false;

        if (ShouldHoldNewBurstForEndingDutyTarget(primaryTarget, TimelineBurstRequiredUptime))
            return false;

        var afterArcaneCircle = PotionAfterArcaneCircleWindow();

        if (ShouldUseDancingMadProfile(strategy))
            return afterArcaneCircle || DancingMadPotionWindowOpen(strategy);

        if (ShouldUseWindurstThirdWalkProfile(strategy))
            return afterArcaneCircle || WindurstPotionWindowOpen(strategy);

        // A full-gauge even burst needs the potion tail for Perfectio -> third Communio.
        if (ShouldDelayPotionForTripleEnshroud(strategy, fullMode))
            return false;

        var recoveredEvenBurstWindow = _recoveringEvenBurst
            && ShouldQueueArcaneCircle(strategy, fullMode);
        if (!PotionBeforeArcaneCircleWindow(strategy) && !recoveredEvenBurstWindow && !afterArcaneCircle)
            return false;

        return strategy.Potion.Value switch
        {
            PotionUseStrategy.OpenerAndEvenBurst => true,
            PotionUseStrategy.EvenBurstExceptOpener => true,
            _ => false
        };
    }

    private readonly record struct NormalCooldownForecast(
        bool Valid, float Potency, int Soul, int Shroud, int Combo, int Executioners, int Reavers,
        int SoulSliceUses, int GluttonyUses, int GCDUses, float GCDReadyAt, float SoulSliceCapAt, float GluttonyReadyAt);

    private readonly record struct NormalCooldownValues(float Soul, float Shroud, float Filler, float Reaver, float Executioner, float SoulSlice, float Gluttony);

    private bool TryQueueNormalCooldownWait(in Strategy strategy, Enemy? target, bool fullMode, int queueStart)
    {
        if (!Player.InCombat || !fullMode || !Unlocked(AID.Perfectio)
            || strategy.SkillRotation.Value != SkillRotationStrategy.Normal
            || strategy.Slice.Value != SliceStrategy.Automatic || strategy.RedGauge.Value != RedGaugeStrategy.Automatic
            || strategy.Enshroud != OffensiveStrategy.Automatic || strategy.Buffs != OffensiveStrategy.Automatic
            || strategy.HM == OffensiveStrategy.Force || strategy.Reaver.Value != SoulReaverStrategy.Automatic
            || !strategy.Communio.IsEnabled()
            || target == null || !TargetInMeleeRange(target) || IsMoving || NumAOETargets != 1
            || target.Priority is Enemy.PriorityInvincible or Enemy.PriorityForbidden or Enemy.PriorityPointless
            || Enshrouded || ReaverOrExecutioner || PerfectioParata > 0 || IdealHost > 0
            || ImmortalSacrifice.Left > 0 || BloodsownCircle > 0 || EnhancedHarpe > 0
            || ArcaneCircleLeft > 0 || RaidBuffsLeft > 0 || PotionLeft > 0
            || _recoveringEvenBurst || _degradedEvenBurstFallback || _deferredPreArcaneEnshroud || PostPerfectioPriorityActive
            || _arcaneCircleReadyIn <= EvenBurstShroudPlanLead || BlueGauge >= 50
            || TargetWillDieWithinTwoGCDs(target) || ShouldHoldNewBurstForEndingDutyTarget(target)
            || ShouldQueueArcaneCrest(strategy)
            || Locks.AnyLocked // the wait is for a weaponskill or an ability that a lock would refuse
            || NextGCD is not (AID.Slice or AID.WaxingSlice or AID.InfernalSlice or AID.SoulSlice)
            || GluttonyTarget(strategy)?.Actor.InstanceID != target.Actor.InstanceID
            || ResolveTargetOverride(strategy.Slice) is { } sliceOverride && sliceOverride.Actor.InstanceID != target.Actor.InstanceID)
        {
            _normalCooldownWaitAction = AID.None;
            return false;
        }

        // Another provider's imminent action can change the weave budget; keep its normal queue contract.
        for (var i = 0; i < queueStart; ++i)
        {
            var entry = Hints.ActionsToExecute.Entries[i];
            if (entry.Manual || entry.Force
                || ActionDefinitions.Instance[entry.Action] is { } definition
                    && Math.Max(entry.Delay, definition.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions)) <= 1f)
            {
                _normalCooldownWaitAction = AID.None;
                return false;
            }
        }

        if (_normalCooldownWaitAction != AID.None
            && (_normalCooldownWaitTargetID != target.Actor.InstanceID
                || World.CurrentTime >= _normalCooldownWaitExpiresAt
                || _normalCooldownWaitAction == AID.SoulSlice && RedGauge > 50
                || _normalCooldownWaitAction == AID.Gluttony && RedGauge < 50
                || Manager.LastCast.Time >= _normalCooldownWaitStartedAt && Manager.LastCast.Data?.IsSpell(_normalCooldownWaitAction) == true))
            _normalCooldownWaitAction = AID.None;

        if (_normalCooldownWaitAction == AID.None)
        {
            var gluttonyLock = _gluttonyDefinition.InstantAnimLock + AnimationLockDelay;
            if (GCD > NormalCooldownWaitMaximum + gluttonyLock || AnimLock > 0.05f)
                return false;

            var soulSliceDelay = ReadyIn(AID.SoulSlice) - GCD;
            var gluttonyDelay = Math.Max(AnimLock, _gluttonyReadyIn) + gluttonyLock - GCD;
            var considerSoulSlice = GCD <= 0.05f && RedGauge <= 50
                && soulSliceDelay > 0.05f && soulSliceDelay <= NormalCooldownWaitMaximum;
            var considerGluttony = RedGauge >= 50 && _gluttonyReadyIn <= NormalCooldownWaitMaximum
                && gluttonyDelay > 0.05f && gluttonyDelay <= NormalCooldownWaitMaximum;
            if (!considerSoulSlice && !considerGluttony)
                return false;

            var horizon = Math.Min(GCD + GCDLength * 6, Math.Min(_arcaneCircleReadyIn - EvenBurstShroudPlanLead,
                Math.Min(TargetDDLeft - 1f, Math.Min(DeathsDesignTargetLossIn() - 1f, RaidBuffsIn))));
            if (horizon < GCDLength * 3 || ComboLastMove is AID.Slice or AID.WaxingSlice && World.Client.ComboState.Remaining <= horizon + 1f)
                return false;

            // A single cutoff can reward moving one GCD across its boundary. Require a gain at
            // three common times, with one forward simulation per candidate and no heap buffers.
            ReadOnlySpan<float> horizons = [horizon * (2f / 3f), horizon * (5f / 6f), horizon];
            Span<NormalCooldownForecast> baseline = stackalloc NormalCooldownForecast[3];
            Span<NormalCooldownForecast> candidate = stackalloc NormalCooldownForecast[3];
            ForecastNormalCooldownWindows(AID.None, horizons, baseline);
            if (!baseline[^1].Valid)
                return false;

            var values = NormalCooldownResourceValues();
            Span<float> baselineScores = stackalloc float[3];
            for (var i = 0; i < horizons.Length; ++i)
                baselineScores[i] = ScoreNormalCooldownForecast(baseline[i], baseline[i], horizons[i], values);
            var bestGain = 1f;
            var bestAction = AID.None;
            foreach (var action in (ReadOnlySpan<AID>)[AID.SoulSlice, AID.Gluttony])
            {
                if (action == AID.SoulSlice ? !considerSoulSlice : !considerGluttony)
                    continue;

                ForecastNormalCooldownWindows(action, horizons, candidate);
                if (!candidate[^1].Valid || candidate[^1].SoulSliceUses < baseline[^1].SoulSliceUses || candidate[^1].GluttonyUses < baseline[^1].GluttonyUses)
                    continue;
                var gain = float.MaxValue;
                for (var i = 0; i < horizons.Length; ++i)
                    gain = Math.Min(gain, ScoreNormalCooldownForecast(candidate[i], baseline[i], horizons[i], values) - baselineScores[i]);
                if (gain > bestGain)
                {
                    bestAction = action;
                    bestGain = gain;
                }
            }
            if (bestAction == AID.None)
                return false;

            _normalCooldownWaitAction = bestAction;
            _normalCooldownWaitTargetID = target.Actor.InstanceID;
            _normalCooldownWaitStartedAt = World.CurrentTime;
            _normalCooldownWaitExpiresAt = World.FutureTime(ReadyIn(bestAction) + 0.25f);
        }

        // Replace only this module's candidates; manual and planned actions remain untouched.
        Hints.ActionsToExecute.Entries.RemoveRange(queueStart, Hints.ActionsToExecute.Entries.Count - queueStart);
        NextGCD = AID.None;
        NextGCDPrio = 0;
        if (_normalCooldownWaitAction == AID.SoulSlice)
            PushGCD(AID.SoulSlice, target, GCDPriority.SoulSlice, delay: Math.Max(0, ReadyIn(AID.SoulSlice)));
        else
            PushOGCD(AID.Gluttony, target, OGCDPriority.Gluttony, delay: Math.Max(0, _gluttonyReadyIn));
        return true;
    }

    private void ForecastNormalCooldownWindows(AID firstAction, ReadOnlySpan<float> horizons, Span<NormalCooldownForecast> results)
    {
        results.Clear();
        if (horizons.IsEmpty || results.Length != horizons.Length)
            return;

        var horizon = horizons[^1];
        var checkpoint = 0;
        var gcdLength = GCDLength;
        var soul = RedGauge;
        var shroud = BlueGauge;
        var combo = ComboLastMove == AID.Slice ? 1 : ComboLastMove == AID.WaxingSlice ? 2 : 0;
        var executioners = 0;
        var reavers = 0;
        var soulSlices = 0;
        var gluttonies = 0;
        var gcds = 0;
        var potency = 0f;
        var gcdAt = GCD;
        var lockAt = AnimLock;
        var sliceCapAt = MaxChargesIn(AID.SoulSlice);
        var gluttonyAt = _gluttonyReadyIn;
        var minorAt = ReadyIn(AID.BloodStalk);
        var enhancedUntil = Math.Max(EnhancedGibbet, EnhancedGallows);
        var gcdLock = _sliceDefinition.InstantAnimLock + AnimationLockDelay;
        var gluttonyLock = _gluttonyDefinition.InstantAnimLock + AnimationLockDelay;
        var minorLock = _bloodStalkDefinition.InstantAnimLock + AnimationLockDelay;
        var sliceCooldown = _soulSliceDefinition.Cooldown;
        var gluttonyCooldown = _gluttonyDefinition.Cooldown;
        var traits = CurrentMasteryTraits();
        var slicePotency = RprPotency.Slice(traits.DeathScythe1, traits.DeathScythe2, traits.MeleeMastery3);
        var waxingSlicePotency = RprPotency.WaxingSliceCombo(traits.DeathScythe1, traits.DeathScythe2, traits.MeleeMastery3);
        var infernalSlicePotency = RprPotency.InfernalSliceCombo(traits.DeathScythe1, traits.DeathScythe2, traits.MeleeMastery3);
        var soulSlicePotency = RprPotency.SoulSlice(traits.MeleeMastery3);
        var unveiledPotency = RprPotency.UnveiledGibbet(traits.MeleeMastery3);

        for (var step = 0; step < 48; ++step)
        {
            var nextGCDAt = Math.Max(gcdAt, lockAt);
            // The normal forecast ends before a possible Enshroud; never invent its burst policy.
            if (shroud >= 50 && executioners == 0 && reavers == 0)
                return;

            var sliceCapSoon = sliceCapAt <= gcdAt + gcdLength + 0.1f;
            var nextGCD = executioners > 0 ? AID.ExecutionersGibbet : reavers > 0 ? AID.Gibbet
                : soul <= 50 && sliceCapAt - sliceCooldown < nextGCDAt + 0.05f ? AID.SoulSlice
                : combo == 1 ? AID.WaxingSlice : combo == 2 ? AID.InfernalSlice : AID.Slice;
            var gluttonyUseAt = Math.Max(lockAt, gluttonyAt);
            var minorUseAt = Math.Max(lockAt, minorAt);
            var nextSoul = Math.Min(100, soul + (nextGCD == AID.SoulSlice ? 50 : nextGCD is AID.Slice or AID.WaxingSlice or AID.InfernalSlice ? 10 : 0));
            var gluttonySoon = gluttonyAt + gluttonyLock <= gcdAt + gcdLength * 5;
            var canGluttony = executioners == 0 && reavers == 0 && soul >= 50;
            var canMinor = executioners == 0 && reavers == 0 && soul >= 50
                && (combo == 0 || nextSoul == 100 || (sliceCapSoon || _arcaneCircleCycleStarted) && !gluttonySoon)
                && (nextGCD == AID.SoulSlice || nextSoul == 100 || !gluttonySoon);

            var useGluttony = firstAction == AID.Gluttony
                || canGluttony && gluttonyUseAt < horizon && gluttonyUseAt < nextGCDAt && gluttonyUseAt + gluttonyLock <= nextGCDAt;
            var useMinor = !useGluttony && firstAction == AID.None && canMinor
                && minorUseAt < horizon && minorUseAt < nextGCDAt && minorUseAt + minorLock <= nextGCDAt;
            if (!useGluttony && !useMinor)
            {
                if (firstAction == AID.SoulSlice)
                    nextGCD = AID.SoulSlice;
                if (nextGCD == AID.SoulSlice)
                    nextGCDAt = Math.Max(nextGCDAt, sliceCapAt - sliceCooldown);
            }
            var nextActionAt = useGluttony ? gluttonyUseAt : useMinor ? minorUseAt : nextGCDAt;
            while (checkpoint < horizons.Length && nextActionAt >= horizons[checkpoint])
                results[checkpoint++] = new(true, potency, soul, shroud, combo, executioners, reavers, soulSlices, gluttonies, gcds, gcdAt, sliceCapAt, gluttonyAt);
            if (checkpoint == horizons.Length)
                return;

            if (useGluttony)
            {
                if (gluttonyUseAt >= horizon || !canGluttony)
                    return;
                potency += RprPotency.Gluttony;
                // Clipping Gluttony leaves no weave for True North before the first Executioner.
                if (firstAction == AID.Gluttony && _currentFallbackTarget is { } positionalTarget
                    && TrueNorthLeft <= Math.Max(gcdAt, gluttonyUseAt + gluttonyLock))
                {
                    var positional = GetNextPositional(positionalTarget, false).Item1;
                    var current = GetCurrentPositional(positionalTarget.Actor);
                    if (positional != Positional.Any && current != Positional.Any && positional != current)
                        potency -= RprPotency.MissedPositionalLoss;
                }
                soul -= 50;
                executioners = 2;
                ++gluttonies;
                gluttonyAt = gluttonyUseAt + gluttonyCooldown;
                lockAt = gluttonyUseAt + gluttonyLock;
                firstAction = AID.None;
                continue;
            }
            if (useMinor)
            {
                potency += enhancedUntil > minorUseAt ? unveiledPotency : RprPotency.BloodStalk;
                soul -= 50;
                reavers = 1;
                minorAt = minorUseAt + 1f;
                lockAt = minorUseAt + minorLock;
                continue;
            }

            switch (nextGCD)
            {
                case AID.ExecutionersGibbet:
                    potency += RprPotency.ExecutionersGibbet + (enhancedUntil > nextGCDAt ? RprPotency.EnhancedReaverBonus : 0f);
                    --executioners;
                    shroud += 10;
                    enhancedUntil = nextGCDAt + RprPotency.EnhancedReaverDuration;
                    break;
                case AID.Gibbet:
                    potency += RprPotency.Gibbet + (enhancedUntil > nextGCDAt ? RprPotency.EnhancedReaverBonus : 0f);
                    --reavers;
                    shroud += 10;
                    enhancedUntil = nextGCDAt + RprPotency.EnhancedReaverDuration;
                    break;
                case AID.SoulSlice:
                    potency += soulSlicePotency;
                    soul = Math.Min(100, soul + 50);
                    sliceCapAt = Math.Max(nextGCDAt, sliceCapAt) + sliceCooldown;
                    ++soulSlices;
                    break;
                default:
                    potency += combo == 1 ? waxingSlicePotency : combo == 2 ? infernalSlicePotency : slicePotency;
                    combo = (combo + 1) % 3;
                    soul = Math.Min(100, soul + 10);
                    break;
            }
            ++gcds;
            gcdAt = nextGCDAt + gcdLength;
            lockAt = nextGCDAt + gcdLock;
            firstAction = AID.None;
        }
    }

    private NormalCooldownValues NormalCooldownResourceValues()
    {
        // Marginal resource values subtract the filler GCDs displaced when spending each resource.
        var traits = CurrentMasteryTraits();
        var filler = RprPotency.ComboAverage(traits.DeathScythe1, traits.DeathScythe2, traits.MeleeMastery3);
        var unveiled = RprPotency.UnveiledGibbet(traits.MeleeMastery3);
        var enhancedReaver = RprPotency.Gibbet + RprPotency.EnhancedReaverBonus;
        var enhancedExecutioner = RprPotency.ExecutionersGibbet + RprPotency.EnhancedReaverBonus;
        var enshroudFillerGCDs = EnshroudGCDSequenceDuration() / GCDLength;
        var shroudValue = (RprPotency.EnshroudSequenceValue - enshroudFillerGCDs * filler
            - enshroudFillerGCDs * 10f * (unveiled + enhancedReaver - filler) / 60f) / (50f + enshroudFillerGCDs * 100f / 60f);
        var soulValue = (unveiled + enhancedReaver + 10f * shroudValue - filler) / 60f;
        var fillerValue = filler + 10f * soulValue;
        var reaverValue = enhancedReaver + 10f * shroudValue - fillerValue;
        var executionerValue = enhancedExecutioner + 10f * shroudValue - fillerValue;
        var soulSliceValue = RprPotency.SoulSlice(traits.MeleeMastery3) + 50f * soulValue - fillerValue;
        var gluttonyValue = RprPotency.Gluttony + 2f * executionerValue - 50f * soulValue;
        return new(soulValue, shroudValue, fillerValue, reaverValue, executionerValue, soulSliceValue, gluttonyValue);
    }

    private float ScoreNormalCooldownForecast(in NormalCooldownForecast result, in NormalCooldownForecast baseline, float horizon, in NormalCooldownValues values)
    {
        var traits = CurrentMasteryTraits();
        var filler = RprPotency.ComboAverage(traits.DeathScythe1, traits.DeathScythe2, traits.MeleeMastery3);
        var comboValue = result.Combo == 1
            ? filler - RprPotency.Slice(traits.DeathScythe1, traits.DeathScythe2, traits.MeleeMastery3)
            : result.Combo == 2
                ? RprPotency.InfernalSliceCombo(traits.DeathScythe1, traits.DeathScythe2, traits.MeleeMastery3) - filler
                : 0;
        return result.Potency + result.Soul * values.Soul + result.Shroud * values.Shroud
            + result.Reavers * values.Reaver + result.Executioners * values.Executioner + comboValue
            - Math.Max(0, result.GCDReadyAt - horizon) / GCDLength * values.Filler
            // Earlier cooldowns have no value by themselves: extra uses must occur in the forecast.
            - Math.Max(0, Math.Max(result.SoulSliceCapAt, baseline.SoulSliceCapAt) - horizon) / _soulSliceDefinition.Cooldown * values.SoulSlice
            - Math.Max(0, Math.Max(result.GluttonyReadyAt, baseline.GluttonyReadyAt) - horizon) / _gluttonyDefinition.Cooldown * values.Gluttony;
    }

    private bool ShouldHoldReadyGCDForCriticalWeave(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        if (!Player.InCombat)
            return false;

        if (ShouldPrioritizeArcaneCircleOverGCD(strategy, primaryTarget, fullMode))
            return true;

        if (GCD > 0.1f)
            return false;

        var abilitiesUsable = !Locks.AbilitiesLocked;
        // Enshroud is an ability: holding the GCD for it under Amnesia (or a full lock) would stall the GCD for the whole status.
        if (abilitiesUsable
            && (ShouldStartRecoveryEnshroud(strategy, fullMode) || ShouldPreferGaugeEnshroudBeforeHarvest(strategy, fullMode))
            && ShouldEnshroud(strategy, fullMode))
            return true;

        var perfectioSafeAfterWeaves = !Enshrouded
            && !ReaverOrExecutioner
            && QueueablePerfectio(strategy)
            && PerfectioParata > GCD + 2.0f;
        var arcaneCircleReadyImmediately = abilitiesUsable
            && ArcaneCircleLeft <= 0
            && _arcaneCircleReadyIn <= 0.1f
            && ShouldPlanArcaneCircle(strategy, fullMode);
        var holdGCDForPotion = PotionAfterArcaneCircleWindow()
            && ShouldUsePotion(strategy, primaryTarget, fullMode)
            && (perfectioSafeAfterWeaves
                || Enshrouded && EnshroudLeft > GCD + 2.0f
                    && 0.6f + ActionDefinitions.Instance[ActionDefinitions.IDPotionStr]!.InstantAnimLock + AnimationLockDelay * 2 > EnshroudGCDLength);
        if (perfectioSafeAfterWeaves && arcaneCircleReadyImmediately || holdGCDForPotion)
            return true;

        return SelectTerminalEnshroudWeave(strategy, primaryTarget, fullMode, out _) != TerminalWeaveAction.None;
    }

    private bool ShouldPrioritizeArcaneCircleOverGCD(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        if (!Player.InCombat
            || primaryTarget == null
            || Locks.AbilitiesLocked
            || !ShouldPreserveArcaneCircleTimingForEvenBurst(strategy, fullMode)
            || ShouldUseDancingMadProfile(strategy)
            || ShouldUseWindurstThirdWalkProfile(strategy))
            return ClearArcaneCircleGCDHold();

        var arcaneCircleAnimationLock = _arcaneCircleDefinition.InstantAnimLock + AnimationLockDelay;
        if (GCD > 0.1f && _arcaneCircleReadyIn > arcaneCircleAnimationLock)
            return ClearArcaneCircleGCDHold();

        var arcaneCircleLock = Math.Max(AnimLock, _arcaneCircleReadyIn) + arcaneCircleAnimationLock;
        if (Enshrouded && EnshroudLeft <= arcaneCircleLock + 0.1f
            || ReaverOrExecutioner && StatusLeft(Executioner ? SID.Executioner : SID.SoulReaver) <= arcaneCircleLock + 0.1f
            || QueueablePerfectio(strategy) && PerfectioParata <= arcaneCircleLock + 0.1f
            || PlentifulHarvestReady(strategy) && ImmortalSacrifice.Left <= arcaneCircleLock + 0.1f)
            return ClearArcaneCircleGCDHold();

        var gcdLock = 0.6f + AnimationLockDelay;
        if (Enshrouded
            && (BlueSouls == 1 || !CanFitEnshroudGCD(EnshroudLeft, 1))
            && HostileAOEAllowed(strategy)
            && Unlocked(AID.Communio)
            && strategy.Communio.IsEnabled()
            && TargetOverrideOrDefault(strategy.Communio, BestRangedAOETarget, 25, IsSplashTarget) != null)
            gcdLock = GetCastTime(AID.Communio) + _communioDefinition.CastAnimLock + AnimationLockDelay;

        var hold = !CanWeave(AID.ArcaneCircle)
            && _arcaneCircleReadyIn <= Math.Max(GCD, AnimLock) + gcdLock;
        if (!hold)
            return ClearArcaneCircleGCDHold();

        // Escape hatch: if Arcane Circle keeps being refused, stop holding the GCD after roughly one GCD length.
        if (_arcaneCircleGCDHoldStartedAt == default)
            _arcaneCircleGCDHoldStartedAt = World.CurrentTime;
        return (World.CurrentTime - _arcaneCircleGCDHoldStartedAt).TotalSeconds < GCDLength;
    }

    private bool ClearArcaneCircleGCDHold()
    {
        _arcaneCircleGCDHoldStartedAt = default;
        return false;
    }

    private TerminalWeaveAction SelectTerminalEnshroudWeave(in Strategy strategy, Enemy? primaryTarget, bool fullMode, out bool commitPotionAfterArcaneCircle)
    {
        commitPotionAfterArcaneCircle = false;
        if (!Enshrouded
            || GCD > 0.1f
            || BlueSouls > 1 && CanFitEnshroudGCD(EnshroudLeft, 1)
            || !CanExecuteTerminalEnshroudGCD(strategy, primaryTarget))
            return TerminalWeaveAction.None;

        var fallbackTarget = FallbackTarget(primaryTarget);
        var abilitiesUsable = !Locks.AbilitiesLocked;
        Span<TerminalWeaveAction> urgent = stackalloc TerminalWeaveAction[3];
        var urgentCount = 0;
        if (abilitiesUsable && HostileAOEAllowed(strategy) && BestRangedAOETarget != null && Unlocked(AID.Sacrificium) && Oblatio > 0)
            urgent[urgentCount++] = TerminalWeaveAction.Sacrificium;

        var lemureAction = abilitiesUsable && PurpleSouls > 1
            && Unlocked(AID.LemuresScythe)
            && BestConeTarget != null
            && (NumConeTargets > 2 || !TargetInMeleeRange(fallbackTarget))
                ? TerminalWeaveAction.LemuresScythe
                : abilitiesUsable && PurpleSouls > 1 && Unlocked(AID.LemuresSlice) && TargetInMeleeRange(fallbackTarget)
                    ? TerminalWeaveAction.LemuresSlice
                    : TerminalWeaveAction.None;
        var lemureUses = lemureAction == TerminalWeaveAction.None ? 0 : Math.Min(2, PurpleSouls / 2);
        for (var i = 0; i < lemureUses; ++i)
            urgent[urgentCount++] = lemureAction;

        var potionPending = ShouldUsePotion(strategy, fallbackTarget, fullMode);
        var arcaneCircleQueued = abilitiesUsable && ShouldQueueArcaneCircle(strategy, fullMode);
        var arcaneCircleReturnsWithinCurrentLock = abilitiesUsable
            && !arcaneCircleQueued
            && ArcaneCircleLeft <= 0
            && ShouldPlanArcaneCircle(strategy, fullMode)
            && _arcaneCircleReadyIn > 0
            && _arcaneCircleReadyIn <= Math.Max(0.1f, AnimLock);
        var arcaneCirclePending = arcaneCircleQueued || arcaneCircleReturnsWithinCurrentLock;
        var arcaneCircleBeforePotion = IsLaterPotionBurst()
            || ShouldPreserveArcaneCircleTimingForEvenBurst(strategy, fullMode);
        var potionPlannedAfterArcaneCircle = arcaneCirclePending
            && arcaneCircleBeforePotion
            && CanPlanPotionAfterArcaneCircle(strategy, fallbackTarget, fullMode);
        var potionInSetup = potionPending || potionPlannedAfterArcaneCircle;

        Span<TerminalWeaveAction> setup = stackalloc TerminalWeaveAction[2];
        var setupCount = 0;
        if (arcaneCircleBeforePotion)
        {
            if (arcaneCirclePending)
                setup[setupCount++] = TerminalWeaveAction.ArcaneCircle;
            if (potionInSetup)
                setup[setupCount++] = TerminalWeaveAction.Potion;
        }
        else
        {
            if (potionPending)
                setup[setupCount++] = TerminalWeaveAction.Potion;
            if (arcaneCirclePending)
                setup[setupCount++] = TerminalWeaveAction.ArcaneCircle;
        }

        Span<TerminalWeaveAction> fullPlan = stackalloc TerminalWeaveAction[5];
        var fullPlanCount = 0;
        for (var i = 0; i < setupCount; ++i)
            fullPlan[fullPlanCount++] = setup[i];
        for (var i = 0; i < urgentCount; ++i)
            fullPlan[fullPlanCount++] = urgent[i];

        var selected = TerminalWeaveAction.None;
        if (fullPlanCount > 0 && TerminalWeavePlanFits(fullPlan[..fullPlanCount]))
            selected = fullPlan[0];
        else
        {
            for (var i = 0; i < urgentCount; ++i)
            {
                if (!TerminalWeavePlanFits(urgent.Slice(i, 1)))
                    continue;

                selected = urgent[i];
                break;
            }

            for (var i = 0; selected == TerminalWeaveAction.None && i < setupCount; ++i)
            {
                if (setup[i] == TerminalWeaveAction.Potion && arcaneCircleBeforePotion && arcaneCirclePending)
                    continue;
                if (!TerminalWeavePlanFits(setup.Slice(i, 1)))
                    continue;

                selected = setup[i];
                break;
            }
        }

        commitPotionAfterArcaneCircle = selected == TerminalWeaveAction.ArcaneCircle && potionInSetup;
        return selected;
    }

    private bool CanPlanPotionAfterArcaneCircle(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        if (!fullMode
            || primaryTarget == null
            || !Player.InCombat
            || !CanUsePotion()
            || !Unlocked(AID.ArcaneCircle)
            || strategy.Buffs == OffensiveStrategy.Delay
            || strategy.Potion.Value == PotionUseStrategy.Off
            || strategy.Potion.Value == PotionUseStrategy.EvenBurstExceptOpener && CombatTimer < 30f
            || ShouldHoldNewBurstForEndingDutyTarget(primaryTarget, TimelineBurstRequiredUptime))
            return false;

        if (ShouldUseDancingMadProfile(strategy))
            return IsInDancingMadWindow(DancingMadPotionWindows);

        if (ShouldUseWindurstThirdWalkProfile(strategy))
            return IsInWindurstWindow(WindurstPotionWindows());

        return IsLaterPotionBurst()
            || ShouldPreserveArcaneCircleTimingForEvenBurst(strategy, fullMode);
    }

    private bool TerminalWeavePlanFits(ReadOnlySpan<TerminalWeaveAction> plan)
    {
        var end = Math.Max(GCD, AnimLock);
        var lastLemureStart = float.NegativeInfinity;
        foreach (var action in plan)
        {
            var aid = TerminalWeaveAID(action);
            var definition = action == TerminalWeaveAction.Potion
                ? ActionDefinitions.Instance[ActionDefinitions.IDPotionStr]
                : ActionDefinitions.Instance.Spell(aid);
            if (definition == null)
                return false;

            var readyIn = action == TerminalWeaveAction.Potion ? PotionReadyIn() : ReadyIn(aid);
            var start = Math.Max(end, readyIn);
            if (action is TerminalWeaveAction.LemuresScythe or TerminalWeaveAction.LemuresSlice && !float.IsNegativeInfinity(lastLemureStart))
                start = Math.Max(start, lastLemureStart + definition.Cooldown);
            if (action == TerminalWeaveAction.Sacrificium && Oblatio <= start + 0.1f)
                return false;

            end = start + definition.InstantAnimLock + AnimationLockDelay;
            if (action is TerminalWeaveAction.LemuresScythe or TerminalWeaveAction.LemuresSlice)
                lastLemureStart = start;
        }

        return EnshroudLeft > end + 0.1f;
    }

    private bool CanExecuteTerminalEnshroudGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        if (HostileAOEAllowed(strategy)
            && Unlocked(AID.Communio)
            && strategy.Communio.IsEnabled()
            && TargetOverrideOrDefault(strategy.Communio, BestRangedAOETarget, 25, IsSplashTarget) != null)
            return true;

        var fallbackTarget = FallbackTarget(primaryTarget);
        if (Unlocked(AID.GrimReaping)
            && BestConeTarget != null
            && (NumConeTargets > 2 || !TargetInMeleeRange(fallbackTarget)))
            return true;

        if ((Unlocked(AID.VoidReaping) || Unlocked(AID.CrossReaping)) && TargetInMeleeRange(fallbackTarget))
            return true;

        return HostileAOEAllowed(strategy)
            && strategy.HM.Value != OffensiveStrategy.Delay
            && Unlocked(AID.HarvestMoon)
            && Soulsow
            && TargetOverrideOrDefault(strategy.HM, BestRangedAOETarget, 25, IsSplashTarget) != null;
    }

    private static AID TerminalWeaveAID(TerminalWeaveAction action)
        => action switch
        {
            TerminalWeaveAction.ArcaneCircle => AID.ArcaneCircle,
            TerminalWeaveAction.Sacrificium => AID.Sacrificium,
            TerminalWeaveAction.LemuresScythe => AID.LemuresScythe,
            TerminalWeaveAction.LemuresSlice => AID.LemuresSlice,
            _ => AID.None
        };

    private void QueueTerminalEnshroudWeave(TerminalWeaveAction action, Enemy? fallbackTarget, bool commitPotionAfterArcaneCircle)
    {
        switch (action)
        {
            case TerminalWeaveAction.Potion:
                Hints.ActionsToExecute.Push(ActionDefinitions.IDPotionStr, Player, ActionQueue.Priority.Low + (int)OGCDPriority.Potion);
                break;
            case TerminalWeaveAction.ArcaneCircle:
                if (commitPotionAfterArcaneCircle)
                    _potionAfterArcaneCircleUntil = CombatTimer + GCDLength + 1.0f;
                PushOGCD(AID.ArcaneCircle, Player, commitPotionAfterArcaneCircle ? OGCDPriority.ArcaneCircleBeforePotion : OGCDPriority.ArcaneCircle);
                break;
            case TerminalWeaveAction.Sacrificium:
                PushOGCD(AID.Sacrificium, BestRangedAOETarget, OGCDPriority.Sacrificium);
                break;
            case TerminalWeaveAction.LemuresScythe:
                PushOGCD(AID.LemuresScythe, BestConeTarget, OGCDPriority.Lemure);
                break;
            case TerminalWeaveAction.LemuresSlice:
                PushOGCD(AID.LemuresSlice, fallbackTarget, OGCDPriority.Lemure);
                break;
        }
    }

    private void HarvestMoon(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!HostileAOEAllowed(strategy) || !Unlocked(AID.HarvestMoon) || !Soulsow || Enshrouded || _reaverGate)
            return;

        var prio = strategy.HM.Value switch
        {
            OffensiveStrategy.Force => GCDPriority.HarvestMoonForce,
            OffensiveStrategy.Automatic => GCDPriority.HarvestMoon,
            _ => GCDPriority.None
        };

        var target = TargetOverrideOrDefault(strategy.HM, BestRangedAOETarget ?? primaryTarget, 25, IsSplashTarget);
        if (target != null)
            PushGCD(AID.HarvestMoon, target, prio);
    }

    private void DDRefresh(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        if (_reaverGate || !Unlocked(AID.ShadowofDeath) && !Unlocked(AID.WhorlofDeath))
            return;

        if (ShouldUseZeroSecondOpenerSoulSliceBeforeDeathsDesign(strategy, fullMode))
            return;

        var useAOE = Unlocked(AID.WhorlofDeath) && ShortestNearbyDDLeft < float.MaxValue;
        var action = useAOE ? AID.WhorlofDeath : AID.ShadowofDeath;
        var target = useAOE ? null : primaryTarget;
        var targetForCheck = useAOE ? BestRangedAOETarget ?? BestConeTarget ?? BestLineTarget ?? primaryTarget ?? FirstPriorityTargetOrDefault() : primaryTarget;
        var timer = useAOE ? ShortestNearbyDDLeft : TargetDDLeft;

        if (!Unlocked(action))
            return;

        if (!useAOE && !TargetInMeleeRange(primaryTarget))
            return;

        // The normal 2-GCD opener can defer DD unless it would expire during the next Enshroud.
        if (IsNormalOpenerPostPerfectioSequence(strategy)
            && !ShouldPrioritizeDeathsDesignAfterPerfectio(strategy, fullMode)
            && timer > GCD + BurstPlanGCDLength)
            return;

        var preArcaneEnshroudRefresh = ShouldRefreshDeathsDesignDuringPreArcaneEnshroud(strategy, fullMode, timer);
        var anyEnshroudRefresh = ShouldRefreshDeathsDesignDuringAnyEnshroud(strategy, fullMode, timer);
        if (Enshrouded)
        {
            if ((preArcaneEnshroudRefresh || anyEnshroudRefresh) && ShouldRefreshDeathsDesignNow(strategy, targetForCheck, useAOE, urgent: true))
                PushGCD(action, target, GCDPriority.DDPreArcaneEnshroud);

            return;
        }

        // Keep the final Perfectio inside the current raid-buff window when a DD GCD would push it out.
        if (ShouldPrioritizePerfectioBeforeDeathsDesignRefresh(strategy))
            return;

        var requiredGCDs = DeathsDesignRequiredFitGCDsForUrgentRefresh(strategy, fullMode);
        var burstCoverageRefresh = DeathsDesignWillFallDuringBurstCommit(strategy, fullMode, timer);
        var postPerfectioEnshroudRefresh = ShouldPrioritizeDeathsDesignAfterPerfectio(strategy, fullMode);
        var arcaneStartCoverageRefresh = ArcaneCircleLeft <= 0
            && ShouldQueueArcaneCircle(strategy, fullMode)
            && timer <= GCD + ArcaneStartDeathsDesignCoverage;
        var emergencyDDRefresh = ShouldEmergencyRefreshDeathsDesign(timer);
        var emergencyBurstCoverageRefresh = burstCoverageRefresh && timer <= GCD + 8.5f;
        var refreshDeathsDesignAfterFirstReaping = ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(strategy, fullMode, timer);
        var preAnyEnshroudDDRefresh = !refreshDeathsDesignAfterFirstReaping && ShouldRefreshDeathsDesignBeforeAnyEnshroud(strategy, fullMode, timer);
        var preEnshroudDDRefresh = !refreshDeathsDesignAfterFirstReaping && ShouldRefreshDeathsDesignBeforeEnshroud(strategy, fullMode);
        if (!refreshDeathsDesignAfterFirstReaping
            && (postPerfectioEnshroudRefresh || preAnyEnshroudDDRefresh || preEnshroudDDRefresh || burstCoverageRefresh || arcaneStartCoverageRefresh || !CanFitGCD(timer, requiredGCDs) || emergencyDDRefresh)
            && ShouldRefreshDeathsDesignNow(strategy, targetForCheck, useAOE, urgent: true)
            && (postPerfectioEnshroudRefresh || preAnyEnshroudDDRefresh || preEnshroudDDRefresh || arcaneStartCoverageRefresh || emergencyDDRefresh || emergencyBurstCoverageRefresh || DeathsDesignBeatsFiller(strategy, targetForCheck, useAOE, urgent: true, fullMode)))
            PushGCD(action, target, GCDPriority.DDExpiring);

        var preRefreshBeforeArcane = ShouldPreRefreshDeathsDesignBeforeArcaneCircle(strategy, fullMode, timer);
        var deferRefreshToPreArcaneEnshroud = ShouldDeferDeathsDesignRefreshToPreArcaneEnshroud(strategy, fullMode, timer);
        var willFallDuringBurst = DeathsDesignWillFallDuringBurstCommit(strategy, fullMode, timer);
        var twoRefreshEvenBurst = ShouldUseTwoDeathsDesignRefreshesForEvenBurst(strategy, fullMode, timer);
        var oneRefreshEvenBurst = !twoRefreshEvenBurst && ShouldUseOneDeathsDesignRefreshForEvenBurst(strategy, fullMode, timer);
        var forcePreBurstDDRefresh = preRefreshBeforeArcane && (willFallDuringBurst || twoRefreshEvenBurst || oneRefreshEvenBurst);
        if (!refreshDeathsDesignAfterFirstReaping
            && !deferRefreshToPreArcaneEnshroud
            && preRefreshBeforeArcane
            && ShouldRefreshDeathsDesignNow(strategy, targetForCheck, useAOE, urgent: false)
            && (forcePreBurstDDRefresh || DeathsDesignBeatsFiller(strategy, targetForCheck, useAOE, urgent: false, fullMode)))
            PushGCD(action, target, willFallDuringBurst || twoRefreshEvenBurst ? GCDPriority.DDExpiring : GCDPriority.DDExtend);
    }

    private bool ShouldEmergencyRefreshDeathsDesign(float timer)
        => timer <= GCD + 0.3f;

    private bool ShouldRefreshDeathsDesignDuringPreArcaneEnshroud(in Strategy strategy, bool fullMode, float timer)
    {
        if (!fullMode || !Enshrouded || ReaverOrExecutioner)
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay || !Unlocked(AID.ArcaneCircle))
            return false;

        if (ArcaneCircleLeft > 0 || IdealHost > 0 || PerfectioAvailable)
            return false;

        if (BlueSouls <= 1)
            return false;

        if (BlueSouls > 4)
            return false;

        if (_arcaneCircleReadyIn > BurstPlanGCDLength * 1.5f)
            return false;

        return ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(strategy, fullMode, timer);
    }

    private bool ShouldRefreshDeathsDesignDuringAnyEnshroud(in Strategy strategy, bool fullMode, float timer)
    {
        if (!fullMode || !Enshrouded || ReaverOrExecutioner)
            return false;

        if (RaidBuffsLeft > 0 || IdealHost > 0 || PerfectioAvailable)
            return false;

        if (!IsInitialOpener || CombatTimer >= 30f)
            return false;

        if (!Unlocked(AID.Communio) || Unlocked(AID.Sacrificium))
            return false;

        if (IsEvenBurstPlanningWindow(strategy, fullMode))
            return false;

        if (BlueSouls <= 1 || BlueSouls > 4)
            return false;

        if (ShouldRefreshDeathsDesignDuringPreArcaneEnshroud(strategy, fullMode, timer))
            return false;

        var required = GCD + BurstPlanGCDLength * BlueSouls + 1.0f;
        return timer <= required;
    }

    private float DeathsDesignLeftAfterOneRefresh(float timer)
        => Math.Min(60f, Math.Max(0, timer) + 30f);

    private bool IsEvenBurstPlanningWindow(in Strategy strategy, bool fullMode)
        => _frameCacheStage >= FrameCacheStage.CycleState ? _frameEvenBurstPlanningWindow : ComputeEvenBurstPlanningWindow(strategy, fullMode);

    private bool ComputeEvenBurstPlanningWindow(in Strategy strategy, bool fullMode)
    {
        if (!fullMode || strategy.Buffs == OffensiveStrategy.Delay || !Unlocked(AID.ArcaneCircle))
            return false;

        if (!_arcaneCircleCycleStarted)
            return false;

        return _arcaneCircleReadyIn <= 25f
            || _arcaneCircleReadyIn >= 95f
            || RaidBuffsLeft > 0
            || IdealHost > 0;
    }

    private bool ShouldUseSoulSliceBeforeEvenBurstChargeCap(in Strategy strategy, bool fullMode)
        => IsEvenBurstPlanningWindow(strategy, fullMode)
        && ArcaneCircleLeft <= 0
        && strategy.Slice.Value != SliceStrategy.Delay
        && Unlocked(AID.SoulSlice)
        && GCDReady(AID.SoulSlice)
        && BlueGauge >= 50
        && _arcaneCircleReadyIn > BurstPlanGCDLength * 2.5f
        && _arcaneCircleReadyIn <= 20f
        && MaxChargesIn(AID.SoulSlice) <= _arcaneCircleReadyIn + EvenBurstRemainingFromPreArcaneEnshroud;

    private bool ShouldSpendRedForPreBurstSoulSlice(in Strategy strategy, bool fullMode)
        => ShouldUseSoulSliceBeforeEvenBurstChargeCap(strategy, fullMode)
        && strategy.RedGauge.Value != RedGaugeStrategy.Delay
        && RedGauge > 50;

    private bool ShouldSpendCappedRedBeforeEvenBurstEnshroud(in Strategy strategy, bool fullMode)
        => IsEvenBurstPlanningWindow(strategy, fullMode)
        && ArcaneCircleLeft <= 0
        && strategy.RedGauge.Value != RedGaugeStrategy.Delay
        && Unlocked(AID.BloodStalk)
        && RedGaugeAfterNextGCD() == 100
        && BlueGauge == 100
        && _arcaneCircleReadyIn > BurstPlanGCDLength * 2.5f
        && _arcaneCircleReadyIn <= BurstPlanGCDLength * 5.0f
        && MaxChargesIn(AID.SoulSlice) > _arcaneCircleReadyIn + GCD + 9.0f;

    private bool ShouldUseTwoDeathsDesignRefreshesForEvenBurst(in Strategy strategy, bool fullMode, float timer)
    {
        if (!IsEvenBurstPlanningWindow(strategy, fullMode))
            return false;

        if (Enshrouded)
        {
            var required = _arcaneCircleReadyIn + EvenBurstRemainingFromPreArcaneEnshroud + EvenBurstDDSecondRefreshSafety;
            return timer <= required;
        }

        var afterOneRefresh = DeathsDesignLeftAfterOneRefresh(timer);
        var requiredBeforeBurst = _arcaneCircleReadyIn + EvenBurstRemainingAfterArcaneCircle + EvenBurstDDSecondRefreshSafety;
        return afterOneRefresh <= requiredBeforeBurst;
    }

    private bool ShouldUseOneDeathsDesignRefreshForEvenBurst(in Strategy strategy, bool fullMode, float timer)
        => IsEvenBurstPlanningWindow(strategy, fullMode)
        && !Enshrouded
        && timer < 30f
        && !ShouldUseTwoDeathsDesignRefreshesForEvenBurst(strategy, fullMode, timer);

    private bool ShouldRefreshDeathsDesignBeforeAnyEnshroud(in Strategy strategy, bool fullMode, float timer)
    {
        if (!fullMode)
            return false;

        if (Enshrouded || ReaverOrExecutioner)
            return false;

        if (!Unlocked(AID.Enshroud) || strategy.Enshroud == OffensiveStrategy.Delay)
            return false;

        if (strategy.Enshroud != OffensiveStrategy.Force && IdealHost == 0 && ShouldHoldNewBurstForEndingDutyTarget(FallbackTarget(null), DyingTrashEnshroudRequiredUptime))
            return false;

        if (IsEvenBurstPlanningWindow(strategy, fullMode))
            return false;

        if (QueueablePerfectio(strategy) || PlentifulHarvestReady(strategy))
            return false;

        var canStartEnshroud = BlueGauge >= 50 || IdealHost > 0 || strategy.Enshroud == OffensiveStrategy.Force;
        if (!canStartEnshroud)
            return false;

        var required = GCD + BurstPlanGCDLength * 5.0f + 1.0f;
        return timer <= required;
    }

    private bool ShouldRefreshDeathsDesignBeforeEnshroud(in Strategy strategy, bool fullMode)
    {
        if (!fullMode)
            return false;

        if (Enshrouded || ReaverOrExecutioner)
            return false;

        if (!Unlocked(AID.ShadowofDeath) && !Unlocked(AID.WhorlofDeath))
            return false;

        if (strategy.Enshroud != OffensiveStrategy.Force && IdealHost == 0 && ShouldHoldNewBurstForEndingDutyTarget(FallbackTarget(null), DyingTrashEnshroudRequiredUptime))
            return false;

        var timer = Math.Min(TargetDDLeft, ShortestNearbyDDLeft);
        if (timer > GCD + 9.0f)
            return false;

        var useAOE = Unlocked(AID.WhorlofDeath) && ShortestNearbyDDLeft < float.MaxValue;
        var action = useAOE ? AID.WhorlofDeath : AID.ShadowofDeath;
        if (!Unlocked(action))
            return false;

        var targetForCheck = useAOE
            ? BestRangedAOETarget ?? BestConeTarget ?? BestLineTarget ?? FallbackTarget(null)
            : FallbackTarget(null);

        return ShouldRefreshDeathsDesignNow(strategy, targetForCheck, useAOE, urgent: true);
    }

    private bool ShouldPreRefreshDeathsDesignBeforeArcaneCircle(in Strategy strategy, bool fullMode, float timer, bool includeBurstCommit = true)
    {
        if (!fullMode)
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay || !Unlocked(AID.ArcaneCircle))
            return false;

        if (Enshrouded || ReaverOrExecutioner)
            return false;

        var preBurstWindow = _arcaneCircleReadyIn > BurstPlanGCDLength && _arcaneCircleReadyIn <= BurstPlanGCDLength * 4.0f;
        if (!preBurstWindow)
            return false;

        if (timer < 30f)
            return true;

        return includeBurstCommit && DeathsDesignWillFallDuringBurstCommit(strategy, fullMode, timer);
    }

    private bool DeathsDesignWillFallDuringBurstCommit(in Strategy strategy, bool fullMode, float timer)
    {
        if (timer <= 0)
            return true;

        if (Enshrouded || ReaverOrExecutioner)
            return false;

        var arcaneCircleQueuedThisGCD = ShouldQueueArcaneCircle(strategy, fullMode);

        if (ShouldHoldArcaneCircleForRecoveryEnshroud(strategy, fullMode)
            || ShouldPreferGaugeEnshroudBeforeHarvest(strategy, fullMode))
            return timer <= Math.Min(BurstPerfectioIn(spendGaugeFirst: true), DeathsDesignTargetLossIn()) + 1f;

        if (PlentifulHarvestReady(strategy)
            && Unlocked(AID.Perfectio)
            && strategy.Communio.IsEnabled()
            && strategy.Perf.Value != PerfectioStrategy.Delay)
            return timer <= BurstPerfectioIn(spendGaugeFirst: false) + 1f;

        if (PlentifulHarvestReady(strategy) || IdealHost > 0)
            return timer <= GCD + 10.0f;

        if (QueueablePerfectio(strategy))
            return timer <= GCD + BurstPlanGCDLength;

        if (ShouldEnshroud(strategy, fullMode))
            return timer <= GCD + 8.5f;

        if ((arcaneCircleQueuedThisGCD || RaidBuffsLeft > 0)
            && fullMode
            && strategy.RedGauge.Value is RedGaugeStrategy.Automatic or RedGaugeStrategy.Force
            && Unlocked(AID.Gluttony)
            && GluttonyTarget(strategy) != null
            && RedGauge >= 50)
            return timer <= GCD + BurstPlanGCDLength * 2.5f;

        return false;
    }

    private int DeathsDesignRequiredFitGCDsForUrgentRefresh(in Strategy strategy, bool fullMode)
    {
        if (!fullMode)
            return 1;

        if (!Unlocked(AID.Gluttony) || GluttonyTarget(strategy) == null)
            return 1;

        if (strategy.RedGauge.Value is not (RedGaugeStrategy.Automatic or RedGaugeStrategy.Force))
            return 1;

        if (RedGauge < 50)
            return 1;

        if (Enshrouded || ReaverOrExecutioner)
            return 1;

        if (QueueablePerfectio(strategy) || PlentifulHarvestReady(strategy))
            return 1;

        var waitingForArcaneCircle = WaitingForArcaneCircle(strategy, fullMode, out var gluttonyReadyDelayExpired, out _)
            && !gluttonyReadyDelayExpired;

        if (waitingForArcaneCircle)
            return 1;

        return CanWeave(AID.Gluttony) ? 2 : 1;
    }

    private bool ShouldRefreshDeathsDesignNow(in Strategy strategy, Enemy? target, bool isAOE, bool urgent)
    {
        if (!isAOE && (target?.Priority is Enemy.PriorityInvincible or Enemy.PriorityForbidden || target?.ForbidDOTs == true))
            return false;

        var targetLossIn = DeathsDesignTargetLossIn();
        if (targetLossIn <= (urgent ? 3f : 5f))
            return false;

        if (targetLossIn <= GCDLength * (urgent ? 1.5f : 2.5f))
            return false;

        if (ValidDeathsDesignHintTime(EffectiveDowntimeIn) && EffectiveDowntimeIn <= (urgent ? 5f : 8f))
            return false;

        if (isAOE)
            return UsefulDeathsDesignAOETargets(urgent) >= (strategy.AOE.Value == AOEStrategy.ForceAOE ? 1 : AOEThresholdTargets);

        if (target == null || TargetIsPredictedDead(target.Actor))
            return false;

        return target.Priority is not Enemy.PriorityPointless;
    }

    // FightRemaining (value-of-information experiment): rule switches (private statics so a harness can flip each rule on its own).
    private static bool VoiFightEndAsLoss = true;   // the fight end counts as a target loss that never returns (Death's Design refresh suppression, dying-target dumps)
    private static bool VoiReleaseEnshroud = true;  // stop holding Shroud for a burst that cannot finish before the end; enter Enshroud while part of the sequence still fits
    private static bool VoiReleaseSoul = true;      // stop holding Soul for Gluttony / the Arcane Circle window; spend it with Blood Stalk when Gluttony cannot finish
    private static bool VoiSoulSliceAtEnd = true;   // keep Soul Slice in the last GCDs unless the combo finisher is the better GCD
    private static bool VoiUsePointEstimate = true; // plan with RemainingSeconds; false plans with UpperBound
    private const float VoiBurstFitSeconds = 9f;
    private const float VoiMaxHorizon = 60f;

    // seconds until the fight end that this decision plans with; float.MaxValue when unknown or blind
    private float VoiFightEnd()
    {
        var f = Hints.FightRemaining;
        if (!f.Known)
            return float.MaxValue;
        return VoiUsePointEstimate ? f.RemainingSeconds : f.UpperBound;
    }

    // a hold for the next burst window is pointless when the window cannot finish before the fight ends
    private bool VoiReleaseActive(bool enabled)
    {
        if (!enabled)
            return false;
        var end = VoiFightEnd();
        return ValidDeathsDesignHintTime(end) && end < VoiMaxHorizon && end < _arcaneCircleReadyIn + VoiBurstFitSeconds;
    }

    // a Death's Design refresh is refused by the fight end (same thresholds as ShouldRefreshDeathsDesignNow), so nothing may wait for it
    private bool VoiRefreshSuppressed()
    {
        var end = VoiFightEnd();
        return ValidDeathsDesignHintTime(end) && (end <= 3f || end <= GCDLength * 1.5f);
    }

    // a refresh pays back only when Death's Design would otherwise expire about five GCDs before the end
    private bool VoiRefreshNotWorthIt(float debuffLeft)
    {
        var end = VoiFightEnd();
        return ValidDeathsDesignHintTime(end) && (VoiRefreshSuppressed() || end - debuffLeft < 10f);
    }

    // Gluttony is pressed in a weave slot before GCD m (once it is ready) and its first Executioner GCD is that GCD
    private bool VoiGluttonyCanFinish(in Strategy strategy)
    {
        if (!Unlocked(AID.Gluttony) || GluttonyTarget(strategy) == null)
            return false;
        var end = VoiFightEnd();
        if (!ValidDeathsDesignHintTime(end))
            return true;
        var m = Math.Max(0, (int)MathF.Ceiling((_gluttonyReadyIn + 0.8f - GCD) / GCDLength));
        return GCD + m * GCDLength < end - 0.3f;
    }

    private bool VoiEnshroudFits(in Strategy strategy, bool wholeSequence, float extraDelay = 0)
    {
        var f = Hints.FightRemaining;
        if (!f.Known)
            return false;
        var enshroudLock = _enshroudDefinition.InstantAnimLock + AnimationLockDelay;
        var firstReapingIn = extraDelay + Math.Max(GCD, Math.Max(AnimLock, ReadyIn(AID.Enshroud)) + enshroudLock);
        // part of the sequence is enough (each Reaping beats a filler GCD per second); the whole sequence must fit LowerBound only to skip the Death's Design refresh
        if (!wholeSequence)
            return VoiFightEnd() > firstReapingIn + EnshroudGCDLength * 1.5f;
        return f.LowerBound >= firstReapingIn + HeldEnshroudSequenceDuration();
    }

    private float DeathsDesignTargetLossIn()
    {
        var targetLossIn = ValidDeathsDesignHintTime(EffectiveDowntimeIn) ? EffectiveDowntimeIn : float.MaxValue;

        if (_hasMechanicHint && ValidDeathsDesignHintTime(_mechanicHint.TargetLossIn) && !IsTransientTargetLoss(_mechanicHint))
            targetLossIn = Math.Min(targetLossIn, _mechanicHint.TargetLossIn);

        if (_hasEncounterHint && _encounterHint.TargetLostWithin5s)
            targetLossIn = Math.Min(targetLossIn, 5f);

        // FightRemaining (value-of-information experiment): the fight end is a target loss that never returns
        if (VoiFightEndAsLoss)
        {
            var end = VoiFightEnd();
            if (ValidDeathsDesignHintTime(end))
                targetLossIn = Math.Min(targetLossIn, end);
        }

        return targetLossIn;
    }

    // A brief untargetable window (jump, short transition) expires nothing we track: Death's Design, Soul Reaver stacks,
    // an Enshroud in progress and the burst procs all outlast it. Only a loss long enough to break a sequence counts.
    private static bool IsTransientTargetLoss(in ExternalMechanicHintSnapshot hint)
        => ValidDeathsDesignHintTime(hint.TargetReturnIn)
        && hint.TargetReturnIn - Math.Max(0, hint.TargetLossIn) <= TransientTargetLossSeconds;

    private bool TargetWillDieWithinTwoGCDs(Enemy? target, float additionalRequiredUptime = 0)
    {
        if (target == null)
            return false;

        if (TargetIsPredictedDead(target.Actor))
            return true;

        var requiredUptime = GCDLength * 2.2f + additionalRequiredUptime;
        var targetLossIn = DeathsDesignTargetLossIn();
        if (ValidDeathsDesignHintTime(targetLossIn) && targetLossIn <= requiredUptime)
            return true;

        if (ValidDeathsDesignHintTime(EffectiveDowntimeIn) && EffectiveDowntimeIn <= requiredUptime)
            return true;

        return false;
    }

    private void UpdateDyingTrashLifeEstimates()
    {
        foreach (var enemy in Hints.PriorityTargetsSpan)
            UpdateDyingTrashLifeEstimate(enemy.Actor);
    }

    private void UpdateDyingTrashLifeEstimate(Actor actor)
    {
        var index = actor.CharacterSpawnIndex;
        if (index < 0 || index >= DyingTrashLifeCacheSize)
            return;

        if (!Player.InCombat || actor.PendingDead || actor.HPMP.MaxHP == 0)
        {
            ResetDyingTrashLifeEstimate(index);
            return;
        }

        var ratio = Math.Clamp(actor.PendingHPRatio, 0, 1);
        if (_dyingTrashLifeActorIDs[index] != actor.InstanceID
            || CombatTimer < _dyingTrashLifeSampleTimes[index]
            || ratio > _dyingTrashLifeSampleRatios[index] + DyingTrashLifeRatioResetTolerance)
        {
            _dyingTrashLifeActorIDs[index] = actor.InstanceID;
            _dyingTrashLifeFirstSeenTimes[index] = CombatTimer;
            _dyingTrashLifeSampleTimes[index] = CombatTimer;
            _dyingTrashLifeSampleRatios[index] = ratio;
            _dyingTrashLifeDrainPerSecond[index] = 0;
            _dyingTrashLifeStableSamples[index] = 0;
            return;
        }

        var elapsed = CombatTimer - _dyingTrashLifeSampleTimes[index];
        if (elapsed < DyingTrashLifeSampleInterval)
            return;

        var delta = _dyingTrashLifeSampleRatios[index] - ratio;
        if (delta > DyingTrashLifeStableDelta)
        {
            var observedDrain = delta / elapsed;
            var previousDrain = _dyingTrashLifeDrainPerSecond[index];
            if (previousDrain > 0 && Math.Abs(observedDrain - previousDrain) <= Math.Max(DyingTrashLifeStableToleranceMin, previousDrain * DyingTrashLifeStableToleranceScale))
                ++_dyingTrashLifeStableSamples[index];
            else
                _dyingTrashLifeStableSamples[index] = 0;

            _dyingTrashLifeDrainPerSecond[index] = previousDrain > 0
                ? previousDrain + (observedDrain - previousDrain) * DyingTrashLifeDrainSmoothing
                : observedDrain;
        }
        else if (delta < DyingTrashLifeHealResetDelta)
        {
            _dyingTrashLifeDrainPerSecond[index] = 0;
            _dyingTrashLifeStableSamples[index] = 0;
        }
        else
        {
            _dyingTrashLifeStableSamples[index] = 0;
        }

        _dyingTrashLifeSampleTimes[index] = CombatTimer;
        _dyingTrashLifeSampleRatios[index] = ratio;
    }

    private void ResetDyingTrashLifeEstimate(int index)
    {
        _dyingTrashLifeActorIDs[index] = 0;
        _dyingTrashLifeFirstSeenTimes[index] = 0;
        _dyingTrashLifeSampleTimes[index] = 0;
        _dyingTrashLifeSampleRatios[index] = 0;
        _dyingTrashLifeDrainPerSecond[index] = 0;
        _dyingTrashLifeStableSamples[index] = 0;
    }

    private float EstimatedDyingTrashLife(Actor actor)
    {
        if (actor.PendingDead || PredictedHP(actor) == 0)
            return 0;

        var index = actor.CharacterSpawnIndex;
        if (index < 0 || index >= DyingTrashLifeCacheSize || _dyingTrashLifeActorIDs[index] != actor.InstanceID)
            return float.MaxValue;

        var drain = _dyingTrashLifeDrainPerSecond[index];
        return drain > DyingTrashLifeMinimumDrain && _dyingTrashLifeStableSamples[index] > 0
            ? Math.Max(0, Math.Clamp(actor.PendingHPRatio, 0, 1) / drain)
            : float.MaxValue;
    }

    private bool DyingTrashLifeEstimateWarmingUp(Actor actor)
    {
        var index = actor.CharacterSpawnIndex;
        return index >= 0
            && index < DyingTrashLifeCacheSize
            && _dyingTrashLifeActorIDs[index] == actor.InstanceID
            && CombatTimer - _dyingTrashLifeFirstSeenTimes[index] < DyingTrashLifeWarmup;
    }

    private bool IsActiveBossActor(Actor actor)
    {
        var module = Bossmods.ActiveModule;
        return module?.StateMachine.ActivePhase != null && module.PrimaryActor.InstanceID == actor.InstanceID;
    }

    private bool ShouldHoldNewBurstForEndingDutyTarget(Enemy? target, float requiredTargetLife = 0)
    {
        if (!Player.InCombat)
            return false;

        if (_hasEncounterHint)
        {
            if (_encounterHint.ShouldHoldBurst || _encounterHint.TargetLostWithin5s)
                return true;
            if (_encounterHint.ForceBurst)
                return false;
        }

        if (!IsDungeonOrAllianceContent())
            return false;

        var relevantTargets = 0;
        foreach (var enemy in Hints.PriorityTargetsSpan)
        {
            var actor = enemy.Actor;
            if (!actor.IsTargetable || actor.IsDead || enemy.Priority is Enemy.PriorityInvincible or Enemy.PriorityForbidden)
                continue;

            ++relevantTargets;
            if (actor.PendingDead || PredictedHP(actor) == 0 || actor.PendingHPRatio <= EndingDutyTargetHPRatio)
                continue;

            if (requiredTargetLife <= 0 || IsActiveBossActor(actor))
                return false;

            var estimatedLife = EstimatedDyingTrashLife(actor);
            if (estimatedLife > requiredTargetLife && !DyingTrashLifeEstimateWarmingUp(actor))
                return false;
        }

        return relevantTargets > 0 && !IsFinalDutyBoss(target);
    }

    private bool IsDungeonOrAllianceContent()
    {
        if (_burstHoldDutyContentID != World.CurrentCFCID)
        {
            _burstHoldDutyContentID = World.CurrentCFCID;
            _burstHoldDungeonOrAllianceContent = BossModuleRegistry.RegisteredModules.Values.Any(info =>
                info.GroupType == BossModuleInfo.GroupType.CFC
                && info.GroupID == World.CurrentCFCID
                && info.Category is BossModuleInfo.Category.Dungeon or BossModuleInfo.Category.Alliance);
        }

        return _burstHoldDungeonOrAllianceContent;
    }

    private bool IsFinalDutyBoss(Enemy? target)
    {
        var module = Bossmods.ActiveModule;
        var info = module?.Info;
        if (target == null
            || module == null
            || info == null
            || module.PrimaryActor.InstanceID != target.Actor.InstanceID
            || info.GroupType != BossModuleInfo.GroupType.CFC
            || info.Category is not (BossModuleInfo.Category.Dungeon or BossModuleInfo.Category.Alliance))
            return false;

        // The registry scan is expensive; the answer only depends on the module type and the boss OID.
        var moduleType = module.GetType();
        var oid = target.Actor.OID;
        if (_finalDutyBossModuleType == moduleType && _finalDutyBossOID == oid)
            return _finalDutyBossResult;

        _finalDutyBossModuleType = moduleType;
        _finalDutyBossOID = oid;
        _finalDutyBossResult = !BossModuleRegistry.RegisteredModules.Values.Any(candidate =>
            candidate.GroupType == info.GroupType
            && candidate.GroupID == info.GroupID
            && candidate.Category == info.Category
            && candidate.SortOrder > info.SortOrder);
        return _finalDutyBossResult;
    }

    private int UsefulDeathsDesignAOETargets(bool urgent)
    {
        if (!DeathsDesignAOESafe())
            return 0;

        var useful = 0;
        foreach (var enemy in Hints.PriorityTargetsSpan)
        {
            if (enemy.ForbidDOTs || enemy.Priority is Enemy.PriorityInvincible or Enemy.PriorityForbidden || !TargetInAOECircle(enemy.Actor, Player.Position, 5f) || TargetIsPredictedDead(enemy.Actor))
                continue;

            if (urgent || enemy.Priority is not Enemy.PriorityPointless)
                ++useful;
        }

        return useful;
    }

    private bool DeathsDesignBeatsFiller(in Strategy strategy, Enemy? target, bool isAOE, bool urgent, bool fullMode)
    {
        var targets = isAOE ? UsefulDeathsDesignAOETargets(urgent) : target == null ? 0 : 1;
        if (targets == 0)
            return false;

        var ddActionPotency = isAOE ? RprPotency.WhorlOfDeathPerTarget * targets : RprPotency.ShadowOfDeath;
        var fillerPotency = EstimatedDeathsDesignAlternativeGCDPotency(strategy, targets);
        var expectedRemainingPotency = fillerPotency * ExpectedDeathsDesignRemainingGCDs(target, isAOE) + EstimatedDeathsDesignOGCDPotency(strategy, fullMode, targets);
        var expectedSoulGainValue = DeathsDesignExpectedSoulGainValue(targets);
        var ddGain = ddActionPotency + expectedRemainingPotency * 0.10f + expectedSoulGainValue;

        if (urgent)
            ddGain += 75f;
        else
            fillerPotency *= 1.15f;

        return ddGain > fillerPotency;
    }

    private float EstimatedDeathsDesignAlternativeGCDPotency(in Strategy strategy, int targets)
    {
        var meleeMasteryIII = CurrentMasteryTraits().MeleeMastery3;

        if (NextGCD is AID.Perfectio || QueueablePerfectio(strategy))
            return PotencyWithAdditionalTargets(RprPotency.Perfectio, targets, RprPotency.PerfectioFalloff);

        if (NextGCD is AID.PlentifulHarvest || PlentifulHarvestReady(strategy))
            return PotencyWithAdditionalTargets(RprPotency.PlentifulHarvest(ImmortalSacrifice.Stacks), targets, RprPotency.PlentifulHarvestFalloff);

        if (NextGCD is AID.HarvestMoon)
            return PotencyWithAdditionalTargets(RprPotency.HarvestMoon(meleeMasteryIII), targets, RprPotency.HarvestMoonFalloff);

        var sliceOverride = ResolveTargetOverride(strategy.Slice);
        var soulSliceReady = strategy.Slice.Value != SliceStrategy.Delay
            && Unlocked(AID.SoulSlice)
            && GCDReady(AID.SoulSlice)
            && (sliceOverride != null
                ? UsableTarget(sliceOverride, 3) != null
                : FallbackTargetInRange(_currentFallbackTarget, 3) != null);
        var soulScytheReady = strategy.Slice.Value != SliceStrategy.Delay
            && HostileAOEAllowed(strategy)
            && Unlocked(AID.SoulScythe)
            && GCDReady(AID.SoulScythe)
            && (sliceOverride != null
                ? UsableTarget(sliceOverride, 5) != null && AOETargetSafe(sliceOverride, SelfCircleTargetCheck)
                : NumAOETargets > 0);
        var soulSlicePotency = RprPotency.SoulSlice(meleeMasteryIII);
        if (NextGCD == AID.SoulScythe)
            return RprPotency.SoulScythePerTarget * targets;
        if (NextGCD == AID.SoulSlice)
            return soulSlicePotency;
        if (sliceOverride != null && soulSliceReady)
            return soulSlicePotency;
        if (soulScytheReady && (sliceOverride != null || NumAOETargets >= AOEThresholdTargets || !soulSliceReady))
            return RprPotency.SoulScythePerTarget * targets;
        if (soulSliceReady)
            return soulSlicePotency;

        return ShouldUseNormalAOECombo(targets) ? NormalAOEComboPotency(targets) : NormalSTComboPotency();
    }

    private static float PotencyWithAdditionalTargets(float primaryPotency, int targets, float additionalTargetMultiplier)
        => primaryPotency * (1f + Math.Max(0, targets - 1) * additionalTargetMultiplier);

    private readonly record struct MasteryTraits(bool DeathScythe1, bool DeathScythe2, bool MeleeMastery3);

    // Trait lookups hit the Lumina sheet; the frame snapshot resolves them once per Exec.
    private MasteryTraits CurrentMasteryTraits()
        => _frameCacheStage >= FrameCacheStage.Basics ? _frameMasteryTraits : ComputeMasteryTraits();

    private MasteryTraits ComputeMasteryTraits()
        => new(Unlocked(TraitID.DeathScytheMastery1), Unlocked(TraitID.DeathScytheMastery2), Unlocked(TraitID.MeleeMasteryIII));

    private int ExpectedDeathsDesignRemainingGCDs(Enemy? target, bool isAOE)
    {
        var remaining = DeathsDesignTargetLossIn();
        if (ValidDeathsDesignHintTime(EffectiveDowntimeIn))
            remaining = Math.Min(remaining, EffectiveDowntimeIn);

        if (!ValidDeathsDesignHintTime(remaining))
            return DeathsDesignExpectedRemainingGCDsAfterDeathCheck(target, isAOE, 8);

        var remainingGCDs = Math.Clamp((int)Math.Floor(Math.Max(0, remaining - GCD) / GCDLength), 0, 8);
        return DeathsDesignExpectedRemainingGCDsAfterDeathCheck(target, isAOE, remainingGCDs);
    }

    private int DeathsDesignExpectedRemainingGCDsAfterDeathCheck(Enemy? target, bool isAOE, int remainingGCDs)
    {
        if (remainingGCDs <= 0)
            return 0;

        if (isAOE || target == null)
            return remainingGCDs;

        if (TargetIsPredictedDead(target.Actor))
            return 0;

        return remainingGCDs;
    }

    private float EstimatedDeathsDesignOGCDPotency(in Strategy strategy, bool fullMode, int targets)
    {
        if (strategy.RedGauge.Value == RedGaugeStrategy.Delay)
            return 0f;

        if (CanCountGluttonyForDeathsDesignEstimate(strategy, fullMode))
            return PotencyWithAdditionalTargets(RprPotency.Gluttony, targets, RprPotency.GluttonyFalloff);

        var unveiledAvailable = (EnhancedGibbet > GCD && Unlocked(AID.UnveiledGibbet))
            || (EnhancedGallows > GCD && Unlocked(AID.UnveiledGallows));
        var unveiledPotency = RprPotency.UnveiledGibbet(CurrentMasteryTraits().MeleeMastery3);
        var redGaugeOverride = ResolveTargetOverride(strategy.RedGauge);
        var bloodStalkTarget = redGaugeOverride != null
            ? UsableTarget(redGaugeOverride, 3)
            : FallbackTargetInRange(_currentFallbackTarget, 3);
        var grimSwatheTarget = HostileAOEAllowed(strategy)
            ? TargetOverrideOrDefault(strategy.RedGauge, BestConeTarget, 8, ConeTargetCheck)
            : null;
        var grimSwatheRangeFallback = grimSwatheTarget != null && !TargetInMeleeRange(redGaugeOverride ?? _currentFallbackTarget);

        if (Unlocked(AID.GrimSwathe)
            && RedGauge >= 50
            && grimSwatheTarget != null
            && (redGaugeOverride != null && !TargetInMeleeRange(redGaugeOverride)
                || redGaugeOverride == null && (NumConeTargets >= AOEThresholdTargets || grimSwatheRangeFallback))
            && CanWeave(AID.GrimSwathe))
            return RprPotency.GrimSwathePerTarget * targets;

        if (Unlocked(AID.BloodStalk) && RedGauge >= 50 && bloodStalkTarget != null && CanWeave(AID.BloodStalk))
            return unveiledAvailable ? unveiledPotency : RprPotency.BloodStalk;

        return 0f;
    }

    private bool CanCountGluttonyForDeathsDesignEstimate(in Strategy strategy, bool fullMode)
    {
        if (!fullMode || !Unlocked(AID.Gluttony) || RedGauge < 50 || GluttonyTarget(strategy) == null)
            return false;

        if (Enshrouded || ReaverOrExecutioner || QueueablePerfectio(strategy) || PlentifulHarvestReady(strategy))
            return false;

        if (strategy.RedGauge.Value is not (RedGaugeStrategy.Automatic or RedGaugeStrategy.Force))
            return false;

        var waitingForArcaneCircle = WaitingForArcaneCircle(strategy, fullMode, out var gluttonyReadyDelayExpired, out var arcaneCircleQueuedThisGCD)
            && (!gluttonyReadyDelayExpired || arcaneCircleQueuedThisGCD);

        if (waitingForArcaneCircle)
            return false;

        return CanWeave(AID.Gluttony);
    }

    private float DeathsDesignExpectedSoulGainValue(int targets)
    {
        var remaining = DeathsDesignTargetLossIn();
        if (ValidDeathsDesignHintTime(EffectiveDowntimeIn))
            remaining = Math.Min(remaining, EffectiveDowntimeIn);

        if (ValidDeathsDesignHintTime(remaining) && remaining <= 8f)
            return 0f;

        return Math.Min(targets, 3) * 20f;
    }

    private static bool ValidDeathsDesignHintTime(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value != float.MaxValue;

    private bool TargetIsPredictedDead(Actor target)
        => !target.IsStrikingDummy && (target.PendingDead || PredictedHP(target) == 0);

    private void Perfectio(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        var opt = strategy.Perf;
        var expiring = PerfectioAvailable && !CanFitGCD(PerfectioParata, 1);
        var replaceLastReaver = _reaverGate && ReaverStacks == 1 && expiring;
        if (!HostileAOEAllowed(strategy) || !PerfectioAvailable || opt == PerfectioStrategy.Delay || Enshrouded || _reaverGate && !replaceLastReaver)
            return;

        var holdForDD = ShouldHoldPerfectioForDeathsDesignRefresh(strategy, primaryTarget, fullMode);
        var arcaneCircleEnding = ArcaneCircleLeft > GCD && !CanFitGCD(ArcaneCircleLeft, 1);
        var prio = replaceLastReaver ? GCDPriority.BurstProcExpiring : opt.Value switch
        {
            PerfectioStrategy.Automatic when expiring || arcaneCircleEnding => GCDPriority.PerfectioExpiring,
            PerfectioStrategy.Automatic when holdForDD => GCDPriority.PerfectioRanged,
            PerfectioStrategy.Automatic => GCDPriority.Perfectio,
            PerfectioStrategy.Ranged => expiring ? GCDPriority.PerfectioExpiring : GCDPriority.PerfectioRanged,
            _ => GCDPriority.None
        };

        var target = TargetOverrideOrDefault(opt, BestRangedAOETarget, 25, IsSplashTarget);
        if (target != null)
            PushGCD(AID.Perfectio, target, prio);
    }

    private bool ShouldPrioritizePlentifulHarvestBeforeSecondEvenBurstEnshroud(in Strategy strategy, bool fullMode)
        => fullMode
        && strategy.Enshroud == OffensiveStrategy.Automatic
        && Unlocked(AID.Perfectio)
        && ArcaneCircleLeft > 0
        && !Enshrouded
        && !ReaverOrExecutioner
        && !QueueablePerfectio(strategy)
        && IdealHost <= 0
        && BlueGauge >= 50
        && Math.Min(TargetDDLeft, ShortestNearbyDDLeft) > GCD + 9f
        && PlentifulHarvestReady(strategy)
        && !ShouldPreferGaugeEnshroudBeforeHarvest(strategy, fullMode)
        && CanStartEnshroudAtCurrentTarget(strategy);

    private bool ShouldRequirePlentifulHarvestBeforeSecondEvenBurstEnshroud(in Strategy strategy, bool fullMode)
        => _evenBurstPlentifulHarvestState == EvenBurstPlentifulHarvestState.Required
        && fullMode
        && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
        && strategy.Buffs == OffensiveStrategy.Automatic
        && strategy.Enshroud == OffensiveStrategy.Automatic
        && strategy.Perf.Value == PerfectioStrategy.Automatic
        && strategy.PH.IsEnabled()
        && Unlocked(AID.PlentifulHarvest)
        && PlentifulHarvestTarget(strategy) != null
        && IdealHost <= 0
        && (ArcaneCircleLeft > 0
            || BloodsownCircle > 0
            || ImmortalSacrifice.Left > GCD
            || _arcaneCircleReadyIn <= PreArcaneEnshroudEntryWindow);

    private bool ShouldHoldThirdEvenBurstEnshroudWithoutPotion(in Strategy strategy, bool fullMode)
        => _evenBurstPlentifulHarvestState == EvenBurstPlentifulHarvestState.Used
        && fullMode
        && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
        && strategy.Enshroud == OffensiveStrategy.Automatic
        && ArcaneCircleLeft > 0
        && PotionLeft <= 0
        && IdealHost <= 0
        && BlueGauge >= 50;

    private bool ShouldPrioritizeRequiredPlentifulHarvest(in Strategy strategy, bool fullMode)
        => !Enshrouded
        && !ReaverOrExecutioner
        && !QueueablePerfectio(strategy)
        && ShouldRequirePlentifulHarvestBeforeSecondEvenBurstEnshroud(strategy, fullMode)
        && PlentifulHarvestReady(strategy);

    private bool ShouldPrioritizePerfectioBeforeDeathsDesignRefresh(in Strategy strategy)
        => HostileAOEAllowed(strategy)
        && strategy.Perf.Value == PerfectioStrategy.Automatic
        && QueueablePerfectio(strategy)
        && CanFitGCD(TargetDDLeft)
        && CanFitGCD(ArcaneCircleLeft)
        && !CanFitGCD(ArcaneCircleLeft, 1);

    private bool ShouldHoldPerfectioForDeathsDesignRefresh(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        if (!QueueablePerfectio(strategy) || primaryTarget == null)
            return false;

        if (ShouldPrioritizePerfectioBeforeDeathsDesignRefresh(strategy))
            return false;

        if (!CanFitGCD(PerfectioParata, 1))
            return false;

        if (TargetDDLeft > GCD)
            return false;

        return ShouldRefreshDeathsDesignNow(strategy, primaryTarget, isAOE: false, urgent: true)
            && DeathsDesignBeatsFiller(strategy, primaryTarget, isAOE: false, urgent: true, fullMode);
    }

    private void PlentifulHarvest(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        var ready = PlentifulHarvestReady(strategy);
        var expiring = ready && !CanFitGCD(ImmortalSacrifice.Left, 1);
        if (!ready || Enshrouded || _reaverGate && !expiring
            || QueueablePerfectio(strategy) && !expiring
            || ShouldSpendShroudBeforePlentifulHarvest(strategy, fullMode)
            || ShouldPreferGaugeEnshroudBeforeHarvest(strategy, fullMode))
            return;

        var target = PlentifulHarvestTarget(strategy);
        if (target != null)
            PushGCD(AID.PlentifulHarvest, target, expiring ? GCDPriority.BurstProcExpiring : GCDPriority.Harvest);
    }

    private void Sow(in Strategy strategy)
    {
        if (!Unlocked(AID.Soulsow) || Soulsow || Enshrouded || _reaverGate || !(strategy.Soulsow.IsEnabled() || !Player.InCombat))
            return;
        // In combat the cast takes 5 s: started just before a known return it holds the first GCD after it for up to the whole cast.
        if (Player.InCombat && KnownTargetReturnIn() < GetCastTime(AID.Soulsow) + 0.5f)
            return;
        PushGCD(AID.Soulsow, Player, Player.InCombat ? GCDPriority.Soulsow : GCDPriority.Max);
    }

    // seconds until a downtime that is already running ends, from the boss module or the mechanic hints; MaxValue when unknown
    private float KnownTargetReturnIn()
    {
        var returnIn = float.MaxValue;
        if (DowntimeIn <= 0 && UptimeIn is float uptimeIn && uptimeIn > 0)
            returnIn = uptimeIn;
        if (_hasMechanicHint && _mechanicHint.TargetLossIn <= 0 && _mechanicHint.TargetReturnIn < returnIn)
            returnIn = _mechanicHint.TargetReturnIn;
        return returnIn;
    }

    private bool PlentifulHarvestReady(in Strategy strategy)
        => _frameCacheStage >= FrameCacheStage.TargetPredicates ? _framePlentifulHarvestReady : ComputePlentifulHarvestReady(strategy);
    private bool ComputePlentifulHarvestReady(in Strategy strategy)
        => PlentifulHarvestTarget(strategy) != null
        && Unlocked(AID.PlentifulHarvest)
        && ImmortalSacrifice.Left > GCD
        && BloodsownCircle <= GCD
        && strategy.PH.IsEnabled();

    private bool PlentifulHarvestPending(in Strategy strategy, bool arcaneCircleQueuedThisGCD = false)
        => (ArcaneCircleLeft > 0 || arcaneCircleQueuedThisGCD)
        && IdealHost == 0
        && Unlocked(AID.PlentifulHarvest)
        && strategy.PH.IsEnabled()
        && (arcaneCircleQueuedThisGCD || BloodsownCircle > GCD || ImmortalSacrifice.Left > GCD)
        && PlentifulHarvestTarget(strategy) != null;

    private bool ShouldSpendShroudBeforePlentifulHarvest(in Strategy strategy, bool fullMode)
    {
        if (!fullMode
            || strategy.Enshroud != OffensiveStrategy.Automatic
            || strategy.RedGauge.Value == RedGaugeStrategy.Force
            || ArcaneCircleLeft > 0
            || _arcaneCircleReadyIn <= 45f
            || IdealHost > 0
            || BlueGauge <= 50
            || Enshrouded
            || ReaverOrExecutioner
            || QueueablePerfectio(strategy)
            || !PlentifulHarvestReady(strategy)
            || IsNormalOpenerPostPerfectioSequence(strategy)
            || !CanStartEnshroudAtCurrentTarget(strategy)
            || !CanWeave(AID.Enshroud, 1)
            || ShouldHoldNewBurstForEndingDutyTarget(FallbackTarget(null), DyingTrashEnshroudRequiredUptime))
            return false;

        var requiredPlentifulHarvestLifetime = GCD + EnshroudGCDLength * 5f + GCDLength * 2f + 1f;
        return ImmortalSacrifice.Left > requiredPlentifulHarvestLifetime
            && (strategy.SkillRotation.Value != SkillRotationStrategy.Normal || CanRebuildEvenBurstShroudAfterNormalEnshroud(strategy, fullMode));
    }

    private Enemy? FallbackTarget(Enemy? primaryTarget)
        => FallbackTargetInRange(primaryTarget, 25);

    private Enemy? FallbackTargetInRange(Enemy? primaryTarget, float range)
        => _manualTargeting
        ? UsableTarget(primaryTarget, range)
        : UsableTarget(primaryTarget, range)
        ?? UsableTarget(BestRangedAOETarget, range)
        ?? UsableTarget(BestLineTarget, range)
        ?? UsableTarget(BestConeTarget, range)
        ?? FirstUsablePriorityTarget(range);

    private bool HostileAOEAllowed(in Strategy strategy)
        => strategy.AOE.Value != AOEStrategy.ForceST;

    private Enemy? GluttonyTarget(in Strategy strategy)
        => _frameCacheStage >= FrameCacheStage.Targets ? _frameGluttonyTarget : ComputeGluttonyTarget(strategy);
    private Enemy? ComputeGluttonyTarget(in Strategy strategy)
        => HostileAOEAllowed(strategy) ? TargetOverrideOrDefault(strategy.RedGauge, BestRangedAOETarget, 25, IsSplashTarget) : null;

    private Enemy? PlentifulHarvestTarget(in Strategy strategy)
        => _frameCacheStage >= FrameCacheStage.Targets ? _framePlentifulHarvestTarget : ComputePlentifulHarvestTarget(strategy);
    private Enemy? ComputePlentifulHarvestTarget(in Strategy strategy)
        => HostileAOEAllowed(strategy) ? TargetOverrideOrDefault(strategy.PH, BestLineTarget, 15, LineTargetCheck) : null;

    private bool CanStartPreArcaneEnshroudThisGCD()
        => Unlocked(AID.Perfectio)
        && _arcaneCircleReadyIn > 0
        && _arcaneCircleReadyIn <= PreArcaneEnshroudEntryWindow;

    private bool CanUseAdaptiveBurst(in Strategy strategy, bool fullMode)
        => fullMode
        && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
        && strategy.Buffs == OffensiveStrategy.Automatic
        && strategy.Enshroud == OffensiveStrategy.Automatic
        && strategy.RedGauge.Value != RedGaugeStrategy.Force
        && strategy.Perf.Value == PerfectioStrategy.Automatic
        && strategy.PH.IsEnabled()
        && strategy.Communio.IsEnabled()
        && Unlocked(AID.Perfectio)
        && PlentifulHarvestTarget(strategy) != null;

    private float EnshroudGCDSequenceDuration()
    {
        var reapingLock = _voidReapingDefinition.InstantAnimLock + AnimationLockDelay;
        var lemureLock = _lemuresSliceDefinition.InstantAnimLock + AnimationLockDelay;
        var sacrificiumLock = _sacrificiumDefinition.InstantAnimLock + AnimationLockDelay;
        var clipping = 2 * Math.Max(0, reapingLock + lemureLock - EnshroudGCDLength)
            + Math.Max(0, reapingLock + sacrificiumLock - EnshroudGCDLength);
        return EnshroudGCDLength * 4 + SpellGCDLength + clipping;
    }

    private float BurstPerfectioIn(bool spendGaugeFirst)
    {
        var enshroud = _enshroudDefinition;
        var enshroudLock = enshroud.InstantAnimLock + AnimationLockDelay;
        var enshroudGCDs = EnshroudGCDSequenceDuration();
        var enshroudAt = Math.Max(AnimLock, ReadyIn(AID.Enshroud));
        var firstReapingAt = Math.Max(GCD, enshroudAt + enshroudLock);
        if (IdealHost > 0)
            return firstReapingAt + enshroudGCDs;

        var harvestAt = Math.Max(Math.Max(GCD, AnimLock), BloodsownCircle);
        if (spendGaugeFirst)
        {
            harvestAt = Math.Max(harvestAt, firstReapingAt + enshroudGCDs);
            enshroudAt += enshroud.Cooldown;
        }
        var harvestLock = _plentifulHarvestDefinition.InstantAnimLock + AnimationLockDelay;
        var freeReapingAt = Math.Max(harvestAt + GCDLength, Math.Max(harvestAt + harvestLock, enshroudAt) + enshroudLock);
        return freeReapingAt + enshroudGCDs;
    }

    private bool ShouldHoldArcaneCircleForRecoveryEnshroud(in Strategy strategy, bool fullMode)
    {
        if (!CanUseAdaptiveBurst(strategy, fullMode)
            || !Player.InCombat
            || !EvenBurstRecoverySetupWindowOpen()
            || ArcaneCircleLeft > 0
            || _arcaneCircleReadyIn > 0.1f
            || ReaverOrExecutioner
            || PerfectioAvailable
            || PlentifulHarvestReady(strategy)
            || IdealHost > 0
            || !CanStartEnshroudAtCurrentTarget(strategy))
            return false;

        // Keep AC behind the first Reaping, including the brief Enshroud status-application gap.
        if (Enshrouded)
            return BlueSouls == 5;
        if (LastActionUsedRecently(AID.Enshroud, 1f))
            return true;
        if (BlueGauge < EvenBurstRequiredShroud || ReadyIn(AID.Enshroud) > Math.Max(GCD, AnimLock))
            return false;

        return CanFitRecoveryBurst(0);
    }

    private bool CanFitRecoveryBurst(float setupIn)
    {
        var targetLossIn = DeathsDesignTargetLossIn();
        var finishIn = BurstPerfectioIn(spendGaugeFirst: true) + setupIn;
        var refreshIn = Math.Min(TargetDDLeft, ShortestNearbyDDLeft) <= Math.Min(finishIn, targetLossIn) + 1f ? GCDLength : 0;
        if (finishIn + refreshIn + 1f < targetLossIn)
            return true;

        // If waiting for Bloodsown Circle would lose Communio, finish the paid Enshroud first.
        var firstReapingIn = Math.Max(GCD + setupIn, Math.Max(AnimLock, ReadyIn(AID.Enshroud))
            + _enshroudDefinition.InstantAnimLock + AnimationLockDelay);
        var communioIn = firstReapingIn + EnshroudGCDSequenceDuration() - SpellGCDLength + GetCastTime(AID.Communio);
        var harvestFirstCommunioIn = Math.Max(GCD, AnimLock + 6f) + GCDLength
            + EnshroudGCDLength * 4 + GetCastTime(AID.Communio);
        return communioIn + refreshIn + PreArcaneEnshroudWeaveSafety < targetLossIn && harvestFirstCommunioIn >= targetLossIn;
    }

    private bool ShouldStartRecoveryEnshroud(in Strategy strategy, bool fullMode)
        => !Enshrouded
        && ShouldHoldArcaneCircleForRecoveryEnshroud(strategy, fullMode)
        && Math.Min(TargetDDLeft, ShortestNearbyDDLeft) > Math.Min(BurstPerfectioIn(spendGaugeFirst: true), DeathsDesignTargetLossIn()) + 1f;

    private bool ShouldPreferGaugeEnshroudBeforeHarvest(in Strategy strategy, bool fullMode)
    {
        if (!CanUseAdaptiveBurst(strategy, fullMode)
            || Enshrouded
            || ReaverOrExecutioner
            || PerfectioAvailable
            || IdealHost > 0
            || BlueGauge < EvenBurstRequiredShroud
            || _evenBurstPlentifulHarvestState != EvenBurstPlentifulHarvestState.Inactive
            || !PlentifulHarvestPending(strategy)
            || !CanStartEnshroudAtCurrentTarget(strategy))
            return false;

        var finishIn = BurstPerfectioIn(spendGaugeFirst: true);
        var refreshIn = Math.Min(TargetDDLeft, ShortestNearbyDDLeft) <= finishIn + 1f ? GCDLength : 0;
        var buffLeft = ArcaneCircleLeft > 0 ? ArcaneCircleLeft : RaidBuffsLeft;
        var harvestIn = Math.Max(GCD, Math.Max(AnimLock, ReadyIn(AID.Enshroud)) + 0.6f + AnimationLockDelay)
            + EnshroudGCDLength * 4 + SpellGCDLength + refreshIn;
        return finishIn + refreshIn + PreArcaneEnshroudWeaveSafety < Math.Min(buffLeft, DeathsDesignTargetLossIn())
            && (ImmortalSacrifice.Left <= 0 || ImmortalSacrifice.Left > harvestIn + PreArcaneEnshroudWeaveSafety);
    }

    private bool IsEvenBurstPlentifulHarvestSequence(in Strategy strategy, bool fullMode)
        => fullMode
        && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
        && strategy.Buffs == OffensiveStrategy.Automatic
        && strategy.Enshroud == OffensiveStrategy.Automatic
        && strategy.Perf.Value == PerfectioStrategy.Automatic
        && strategy.PH.IsEnabled()
        && Unlocked(AID.ArcaneCircle)
        && Unlocked(AID.Enshroud)
        && Unlocked(AID.PlentifulHarvest)
        && Unlocked(AID.Perfectio)
        && PlentifulHarvestTarget(strategy) != null
        && IdealHost <= 0
        && (ArcaneCircleLeft > 0 || _arcaneCircleReadyIn <= PreArcaneEnshroudEntryWindow);

    private bool ShouldPreserveArcaneCircleTimingForEvenBurst(in Strategy strategy, bool fullMode)
        => IsEvenBurstPlanningWindow(strategy, fullMode)
        && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
        && strategy.Buffs == OffensiveStrategy.Automatic
        && strategy.Enshroud == OffensiveStrategy.Automatic
        && Unlocked(AID.Enshroud)
        && ArcaneCircleLeft <= 0
        && _arcaneCircleReadyIn <= PreArcaneEnshroudEntryWindow
        && !ShouldHoldArcaneCircleForRecoveryEnshroud(strategy, fullMode)
        && !ShouldHoldArcaneCircleForEvenBurstRecovery(strategy, fullMode);

    private bool CanStartEnshroudAtCurrentTarget(in Strategy strategy)
        => CanUseEnshroudActions()
        && ((Unlocked(AID.VoidReaping) || Unlocked(AID.CrossReaping)) && FallbackTargetInRange(_currentFallbackTarget, 3) != null
            || HostileAOEAllowed(strategy) && Unlocked(AID.GrimReaping) && BestConeTarget != null && !(NumConeTargets < AOEThresholdTargets && HoldForMeleeReturn(strategy))
            || CanStartDancingMadEnshroudBeforeConeReturn(strategy));

    private bool CanFitEnshroudGCD(float duration, int extraGCDs = 0)
        => GCD + EnshroudGCDLength * extraGCDs < duration;

    private bool CanRebuildEvenBurstShroudAfterNormalEnshroud(in Strategy strategy, bool fullMode)
    {
        if (!fullMode
            || strategy.SkillRotation.Value != SkillRotationStrategy.Normal
            || strategy.Enshroud != OffensiveStrategy.Automatic)
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay || !Unlocked(AID.ArcaneCircle))
            return true;

        if (!_arcaneCircleCycleStarted || ArcaneCircleLeft > 0)
            return false;

        var rebuildDeadline = _arcaneCircleReadyIn - PreArcaneEnshroudEntryWindow;
        var useCommunio = Unlocked(AID.Communio) && strategy.Communio.IsEnabled();
        var enshroudEndsIn = GCD + EnshroudGCDLength * 4 + (useCommunio ? GetCastTime(AID.Communio) : EnshroudGCDLength);
        var enshroudEndLock = useCommunio ? _communioDefinition.CastAnimLock : 0.6f;
        if (enshroudEndsIn + enshroudEndLock + AnimationLockDelay >= rebuildDeadline
            || Math.Max(AnimLock, ReadyIn(AID.Enshroud)) + _enshroudDefinition.Cooldown > rebuildDeadline)
            return false;

        List<TargetableWindow>? targetableWindows = null;
        if (Manager.Planner is { } planner)
        {
            targetableWindows = _evenBurstRebuildWindows;
            planner.EstimateTargetableWindows(rebuildDeadline, targetableWindows);
        }
        if (targetableWindows == null || targetableWindows.Count == 0)
        {
            _fallbackTargetableWindows.Clear();
            _fallbackTargetableWindows.Add(new(0, ValidDeathsDesignHintTime(EffectiveDowntimeIn) ? Math.Min(EffectiveDowntimeIn, rebuildDeadline) : rebuildDeadline, true));
            targetableWindows = _fallbackTargetableWindows;
        }
        if (!targetableWindows[0].Targetable || targetableWindows[0].StartIn > 0.1f || targetableWindows[0].EndIn <= enshroudEndsIn)
            return false;

        var missingShroud = Math.Max(0, RequiredShroudForNextEvenBurst(strategy) - (BlueGauge - 50));
        if (missingShroud == 0)
            return true;
        if (strategy.RedGauge.Value != RedGaugeStrategy.Automatic)
            return false;

        // Reserve two GCDs for DD/combo maintenance and effect application before the AC-4s entry.
        var safetyGCDs = EvenBurstShroudPlanSafetyGCDs;
        var finalEnshroudGCDLength = useCommunio ? SpellGCDLength : EnshroudGCDLength;
        var nextGCDAt = GCD + EnshroudGCDLength * 4 + finalEnshroudGCDLength;
        if (PlentifulHarvestReady(strategy))
        {
            nextGCDAt += GCDLength + EnshroudGCDLength * 4 + finalEnshroudGCDLength;
            if (targetableWindows[0].EndIn < nextGCDAt)
                return false;
        }
        if (nextGCDAt >= rebuildDeadline)
            return false;

        var sliceOverride = ResolveTargetOverride(strategy.Slice);
        var canSoulSlice = Unlocked(AID.SoulSlice)
            && (sliceOverride != null ? UsableTarget(sliceOverride, 3) != null : FallbackTargetInRange(null, 3) != null);
        var canSoulScythe = HostileAOEAllowed(strategy)
            && Unlocked(AID.SoulScythe)
            && (sliceOverride != null ? UsableTarget(sliceOverride, 5) != null && AOETargetSafe(sliceOverride, SelfCircleTargetCheck) : NumAOETargets > 0);
        var soulSlice = strategy.Slice.Value == SliceStrategy.Automatic && (canSoulSlice || canSoulScythe)
            ? ActionDefinitions.Instance.Spell(canSoulSlice ? AID.SoulSlice : AID.SoulScythe)
            : null;
        var soulSliceCapAt = soulSlice != null ? MaxChargesIn(canSoulSlice ? AID.SoulSlice : AID.SoulScythe) : float.MaxValue;
        var soulSliceCharges = soulSlice?.MaxChargesAtLevel(Player.Level) ?? 0;
        var canGenerateSoul = FallbackTargetInRange(null, 3) != null
            || HostileAOEAllowed(strategy) && NumAOETargets > 0
            || strategy.Harpe.Value != HarpeStrategy.Forbid && Unlocked(AID.Harpe) && !IsMoving && HarpeTarget(strategy, _currentFallbackTarget) != null;
        var canUseMinorSpender = CanUseMinorSoulSpenderForRecovery(strategy);
        var canUseGluttony = Unlocked(AID.Gluttony) && CanExecuteGluttonyRecoveryFollowups(strategy, GluttonyTarget(strategy));
        var gluttonyReadyAt = _gluttonyReadyIn;
        var soul = RedGauge;
        var reaverStacks = 0;
        var reaverExpiresAt = 0f;
        var weaveTime = 0.6f + AnimationLockDelay;

        // Advance in targetable GCD slots so a late charge or Gluttony cannot fund an earlier follow-up.
        foreach (var window in targetableWindows)
        {
            if (!window.Targetable)
                continue;

            nextGCDAt = Math.Max(nextGCDAt, window.StartIn + weaveTime);
            var windowEnd = Math.Min(window.EndIn, rebuildDeadline);
            for (; nextGCDAt + weaveTime < windowEnd; nextGCDAt += GCDLength)
            {
                if (safetyGCDs > 0)
                {
                    --safetyGCDs;
                    continue;
                }

                if (nextGCDAt >= reaverExpiresAt)
                    reaverStacks = 0;

                if (reaverStacks == 0 && soul >= 50)
                {
                    if (canUseGluttony && gluttonyReadyAt <= nextGCDAt - weaveTime)
                    {
                        soul -= 50;
                        reaverStacks = 2;
                        reaverExpiresAt = nextGCDAt - weaveTime + 30f;
                        gluttonyReadyAt = nextGCDAt - weaveTime + _gluttonyDefinition.Cooldown;
                    }
                    else if (canUseMinorSpender)
                    {
                        soul -= 50;
                        reaverStacks = 1;
                        reaverExpiresAt = nextGCDAt - weaveTime + 30f;
                    }
                }

                if (reaverStacks > 0)
                {
                    --reaverStacks;
                    missingShroud -= 10;
                    if (missingShroud <= 0)
                        return true;
                }
                else if (soul <= 50 && soulSlice != null && soulSliceCapAt - soulSlice.Cooldown * (soulSliceCharges - 1) <= nextGCDAt)
                {
                    soul += 50;
                    soulSliceCapAt = Math.Max(nextGCDAt, soulSliceCapAt) + soulSlice.Cooldown;
                }
                else if (canGenerateSoul)
                    soul = Math.Min(100, soul + 10);
            }
        }

        return false;
    }

    private bool ShouldEnshroud(in Strategy strategy, bool fullMode, bool arcaneCircleQueuedThisGCD = false)
    {
        var plentifulHarvestQueuedThisGCD = NextGCD == AID.PlentifulHarvest && PlentifulHarvestReady(strategy);
        var idealHostAvailable = IdealHost > 0 || plentifulHarvestQueuedThisGCD;
        var recoveryEnshroudEntry = ShouldStartRecoveryEnshroud(strategy, fullMode);
        var gaugeBeforeHarvest = ShouldPreferGaugeEnshroudBeforeHarvest(strategy, fullMode);
        var evenBurstShroudPlan = BuildEvenBurstShroudPlan(strategy, fullMode);
        var evenBurstPlentifulHarvestSequence = IsEvenBurstPlentifulHarvestSequence(strategy, fullMode);

        // hard requirements
        if (!Unlocked(AID.Enshroud) || ReadyIn(AID.Enshroud) > Math.Max(0, GCD - 0.8f) || Enshrouded || BlueGauge < 50 && !idealHostAvailable || strategy.Enshroud == OffensiveStrategy.Delay || ReaverOrExecutioner)
            return false;

        // Force presses it as soon as it can be pressed, which does not include the Ideal Host a queued Plentiful Harvest will only
        // grant after it lands: pushed now, the game refuses it for the whole weave window
        if (strategy.Enshroud == OffensiveStrategy.Force)
            return BlueGauge >= 50 || IdealHost > 0;

        if (!CanStartEnshroudAtCurrentTarget(strategy))
            return false;

        if (!idealHostAvailable && (PostPerfectioHigherPriorityGCDQueued(strategy, fullMode) || ShouldPrioritizeGluttonyAfterPerfectio(strategy, fullMode)))
            return false;

        if (ShouldHoldDancingMadEnshroudForMeleePlan(strategy))
            return false;

        if (ShouldHoldThirdEvenBurstEnshroudWithoutPotion(strategy, fullMode) && !VoiReleaseActive(VoiReleaseEnshroud))
            return false;

        if (ShouldRequirePlentifulHarvestBeforeSecondEvenBurstEnshroud(strategy, fullMode))
            return false;

        if (plentifulHarvestQueuedThisGCD && IdealHost <= 0)
            return false;

        // A late entry must leave room for both Communios, Plentiful Harvest, and Perfectio.
        if (!plentifulHarvestQueuedThisGCD
            && PlentifulHarvestPending(strategy, arcaneCircleQueuedThisGCD)
            && (!evenBurstPlentifulHarvestSequence
                || strategy.Communio.IsEnabled()
                    && ArcaneCircleLeft > 0
                    && !gaugeBeforeHarvest))
            return false;

        if (PerfectioParata > 0)
            return false;

        if (strategy.Enshroud == OffensiveStrategy.Automatic && strategy.RedGauge.Value == RedGaugeStrategy.Force && RedGauge >= 50)
            return false;

        var spendShroudBeforePlentifulHarvest = ShouldSpendShroudBeforePlentifulHarvest(strategy, fullMode);
        if (PlentifulHarvestReady(strategy) && !plentifulHarvestQueuedThisGCD && !spendShroudBeforePlentifulHarvest && !gaugeBeforeHarvest)
            return false;

        if (!idealHostAvailable && IsNormalOpenerPostPerfectioSequence(strategy))
            return false;

        if (!fullMode && strategy.Enshroud != OffensiveStrategy.Force && !idealHostAvailable)
            return false;

        // FightRemaining (value-of-information experiment): the burst this Shroud was held for will not finish; the holds below stop
        // (the Death's Design ordering before an Enshroud stays)
        var voiRelease = VoiReleaseActive(VoiReleaseEnshroud) && VoiEnshroudFits(strategy, false) && !ShouldHoldEnshroudForTargetLoss(strategy);

        if (!idealHostAvailable
            && !voiRelease
            && evenBurstShroudPlan.Active
            && !recoveryEnshroudEntry
            && !CanStartPreArcaneEnshroudThisGCD()
            && BlueGauge - 50 < evenBurstShroudPlan.RequiredShroud)
            return false;

        if (!idealHostAvailable && strategy.Enshroud != OffensiveStrategy.Force && ShouldHoldNewBurstForEndingDutyTarget(FallbackTarget(null), DyingTrashEnshroudRequiredUptime))
            return false;

        if (!idealHostAvailable
            && ShouldUseDancingMadProfile(strategy)
            && ValidDeathsDesignHintTime(EffectiveDowntimeIn)
            && EffectiveDowntimeIn < DyingTrashEnshroudRequiredUptime)
            return false;

        if (ShouldHoldEnshroudForTargetLoss(strategy))
            return false;

        if (spendShroudBeforePlentifulHarvest)
            return true;

        if (plentifulHarvestQueuedThisGCD && !PerfectioAvailable)
            return true;

        var ddTimer = Math.Min(TargetDDLeft, ShortestNearbyDDLeft);
        var refreshDeathsDesignAfterFirstReaping = ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(strategy, fullMode, ddTimer);
        var useFourSecondEnshroudEntry = Unlocked(AID.Perfectio) && IsEvenBurstPlanningWindow(strategy, fullMode) && !ShouldUseDancingMadProfile(strategy);
        var preArcaneEnshroudEntry = useFourSecondEnshroudEntry && CanStartPreArcaneEnshroudThisGCD();
        if (preArcaneEnshroudEntry && !voiRelease && !PreArcaneEnshroudResourcesReadyAfterNextGCD())
            return false;

        var refreshDeathsDesignOnEnshroudEntry = preArcaneEnshroudEntry && ShouldRefreshDeathsDesignBeforeEnshroud(strategy, fullMode);
        var normalOpenerPostSequenceDeathsDesignEntry = !ShouldUseDancingMadProfile(strategy)
            && !_resumedCombatCycle
            && strategy.OpenerBurst.Value == OpenerBurstStrategy.Normal
            && CombatTimer < 35f
            && NextGCD is AID.ShadowofDeath or AID.WhorlofDeath
            && LastActionUsedRecently(AID.Gibbet, GCDLength + 1f);
        // FightRemaining (value-of-information experiment): a Death's Design refresh in front of the Enshroud is skipped when it would push the sequence past the end
        var voiSkipDDFirst = voiRelease && !VoiEnshroudFits(strategy, true, GCDLength + 0.2f);
        if (!voiSkipDDFirst && ShouldRefreshDeathsDesignBeforeAnyEnshroud(strategy, fullMode, ddTimer) && !refreshDeathsDesignAfterFirstReaping && !refreshDeathsDesignOnEnshroudEntry && !normalOpenerPostSequenceDeathsDesignEntry)
            return false;

        if (!voiSkipDDFirst && ShouldRefreshDeathsDesignBeforeEnshroud(strategy, fullMode) && !refreshDeathsDesignAfterFirstReaping && !refreshDeathsDesignOnEnshroudEntry && !normalOpenerPostSequenceDeathsDesignEntry)
            return false;

        if (voiRelease)
            return true;

        if (recoveryEnshroudEntry || gaugeBeforeHarvest)
            return ddTimer > Math.Min(BurstPerfectioIn(spendGaugeFirst: true), DeathsDesignTargetLossIn()) + 1f;

        var dancingMadPolicy = DancingMadEnshroudUsePolicy(strategy, fullMode);
        if (dancingMadPolicy == DancingMadEnshroudPolicy.Release)
            return true;
        if (dancingMadPolicy == DancingMadEnshroudPolicy.Hold)
            return IdealHost > 0 || AutomaticShroudCapPressure(strategy, fullMode);

        var preparingDoubleEnshroud =
            fullMode
            && strategy.Buffs != OffensiveStrategy.Delay
            && BlueGauge >= 50
            && (!ShouldRefreshDeathsDesignBeforeEnshroud(strategy, fullMode) || refreshDeathsDesignAfterFirstReaping || refreshDeathsDesignOnEnshroudEntry)
            && (!ShouldPreRefreshDeathsDesignBeforeArcaneCircle(strategy, fullMode, ddTimer, includeBurstCommit: false) || refreshDeathsDesignAfterFirstReaping || refreshDeathsDesignOnEnshroudEntry)
            && (useFourSecondEnshroudEntry
                ? preArcaneEnshroudEntry
                : _arcaneCircleReadyIn > GCD - 0.8f && _arcaneCircleReadyIn <= BurstPlanGCDLength * 2.5f)
            && !PlentifulHarvestReady(strategy)
            && IdealHost == 0
            && !QueueablePerfectio(strategy);

        // use early for double enshroud, so we have room for 2 communio + 1 perfectio
        if (preparingDoubleEnshroud)
            return true;

        if (IdealHost > 0)
            return true;

        if (strategy.SkillRotation.Value == SkillRotationStrategy.Normal
            && ArcaneCircleLeft <= 0
            && !CanRebuildEvenBurstShroudAfterNormalEnshroud(strategy, fullMode))
            return false;

        if (AutomaticShroudCapPressure(strategy, fullMode))
            return true;

        // Single Enshrouds are somewhat more complicated than Doubles because of the Enshroud that could or could not precede them. General rule of thumb is to not enter Enshroud if Gluttony <13s on its cooldown.
        if (strategy.Enshroud == OffensiveStrategy.Automatic && Unlocked(AID.Gluttony) && GluttonyTarget(strategy) != null && _gluttonyReadyIn < 13)
            return false;

        // 4 reaping GCDs at 1.5s each = 6 seconds
        // maximum communio cast time = 1.3 seconds
        if (RaidBuffsLeft > GCD + 7.3f)
            return true;

        if (BlueGauge == 100
            && fullMode
            && strategy.Enshroud == OffensiveStrategy.Automatic
            && _arcaneCircleReadyIn > 45f
            && _gluttonyReadyIn >= 13f)
            return true;

        return strategy.SkillRotation.Value == SkillRotationStrategy.Normal ? ArcaneCircleLeft <= 0 : _arcaneCircleReadyIn > 65;
    }

    // Time from the first Enshroud GCD until the Communio cast completes: four Reapings plus the Communio cast, with the
    // Lemure/Sacrificium weave clipping (EnshroudGCDSequenceDuration counts the Communio recast instead).
    private float HeldEnshroudSequenceDuration()
    {
        var reapingLock = _voidReapingDefinition.InstantAnimLock + AnimationLockDelay;
        var weaveClipping = 2 * Math.Max(0, reapingLock + _lemuresSliceDefinition.InstantAnimLock + AnimationLockDelay - EnshroudGCDLength)
            + Math.Max(0, reapingLock + _sacrificiumDefinition.InstantAnimLock + AnimationLockDelay - EnshroudGCDLength);
        return EnshroudGCDLength * 4 + GetCastTime(AID.Communio) + weaveClipping + 0.1f;
    }

    // Enshroud cannot start under Soul Reaver or Executioner, so reaver stacks carried across a loss are spent first on
    // the return. True when newStacks more of them would let a held Ideal Host expire before the Enshroud, or push the
    // Communio of a Perfectio the hold would otherwise keep past Perfectio Occulta.
    private bool HeldEnshroudTimersMissedByReaver(int newStacks)
    {
        if (IdealHost <= 0 || !_hasMechanicHint)
            return false;
        var heldStacks = ReaverOrExecutioner ? Math.Max(1, ReaverStacks) : 0;
        var returnIn = _mechanicHint.TargetReturnIn;
        // Enshroud weaves right after the last reaver GCD, so the Ideal Host only has to outlast all but one of them.
        if (IdealHost <= returnIn + Math.Max(0, heldStacks + newStacks - 1) * GCDLength + TargetReturnSafety)
            return true;
        var occulta = StatusLeft(SID.PerfectioOcculta);
        var communioAt = returnIn + GCDLength + heldStacks * GCDLength + HeldEnshroudSequenceDuration() + TargetReturnSafety;
        return occulta > communioAt && occulta <= communioAt + newStacks * GCDLength;
    }

    // A known, non-transient target loss before Communio strands the rest of the sequence behind the loss, where it
    // outranks the Death's Design refresh on the return. Keep the resource for after the loss when it survives it: the
    // Shroud gauge always does, Ideal Host only when it outlasts the known return. Only a known return makes holding safe
    // (a loss that lasts to the end of the fight would strand the resource), and the FFLogs-anchored profiles keep their
    // own Enshroud plans.
    private bool ShouldHoldEnshroudForTargetLoss(in Strategy strategy)
    {
        if (ShouldUseDancingMadProfile(strategy) || ShouldUseWindurstThirdWalkProfile(strategy))
            return false;

        var lossIn = DeathsDesignTargetLossIn();
        if (!ValidDeathsDesignHintTime(lossIn)
            || !_hasMechanicHint
            || !ValidDeathsDesignHintTime(_mechanicHint.TargetReturnIn)
            || _mechanicHint.TargetLossIn > lossIn + 0.05f)
            return false;

        var sequence = HeldEnshroudSequenceDuration();
        if (lossIn >= Math.Max(GCD, Math.Max(AnimLock, ReadyIn(AID.Enshroud)) + _enshroudDefinition.InstantAnimLock + AnimationLockDelay) + sequence)
            return false;

        // Entering now only moves the stranded GCDs behind the loss, even when the 30 s Enshroud outlives it, so the Shroud
        // gauge waits for the return. A held Ideal Host also carries Perfectio Occulta: its Communio (after one refresh GCD
        // on the return) must still convert it, or the Perfectio is lost.
        if (IdealHost <= 0)
            return BlueGauge >= 50;
        var returnIn = _mechanicHint.TargetReturnIn;
        if (IdealHost <= returnIn + TargetReturnSafety)
            return false;
        var occulta = StatusLeft(SID.PerfectioOcculta);
        if (occulta <= 0 || occulta > returnIn + GCDLength + sequence + TargetReturnSafety)
            return true;
        // Holding loses the Perfectio. Entering now only helps when its Communio, after the Reapings that fit before the
        // loss, still lands inside Perfectio Occulta and the Enshroud itself; otherwise keep the Death's Design refresh.
        var firstGCDAt = Math.Max(GCD, Math.Max(AnimLock, ReadyIn(AID.Enshroud)) + _enshroudDefinition.InstantAnimLock + AnimationLockDelay);
        var reapingsBeforeLoss = lossIn > firstGCDAt ? Math.Min(4, (int)MathF.Ceiling((lossIn - firstGCDAt) / EnshroudGCDLength)) : 0;
        var enterNowCommunioAt = returnIn + sequence - reapingsBeforeLoss * EnshroudGCDLength;
        var enteringNowKeepsPerfectio = occulta > enterNowCommunioAt + EnterNowCommunioMargin
            && Math.Max(AnimLock, ReadyIn(AID.Enshroud)) + EnshroudDurationSeconds > enterNowCommunioAt + EnterNowCommunioMargin;
        return !enteringNowKeepsPerfectio;
    }

    private bool AutomaticShroudCapPressure(in Strategy strategy, bool fullMode)
    {
        if (!fullMode
            || strategy.Enshroud != OffensiveStrategy.Automatic
            || strategy.RedGauge.Value == RedGaugeStrategy.Force)
            return false;

        var gluttonyWouldOverflow = BlueGauge > 80
            && strategy.RedGauge.Value == RedGaugeStrategy.Automatic
            && RedGaugeAfterNextGCD() >= 50
            && Unlocked(AID.Gluttony)
            && _gluttonyReadyIn <= 0.1f
            && GluttonyTarget(strategy) != null;
        return NextGCDWouldOverflowRedAtFullShroud(strategy, fullMode)
            || gluttonyWouldOverflow
            || BlueGauge == 100 && _arcaneCircleReadyIn > 45f;
    }

    private int NextGCDRedGaugeGain()
        => NextGCD switch
        {
            AID.SoulSlice or AID.SoulScythe => 50,
            AID.Slice or AID.WaxingSlice or AID.InfernalSlice or AID.SpinningScythe or AID.NightmareScythe or AID.Harpe or AID.HarvestMoon => 10,
            _ => 0
        };

    private int RedGaugeAfterNextGCD()
        => Math.Min(100, RedGauge + NextGCDRedGaugeGain());

    private bool NextGCDWouldOverflowRedAtFullShroud(in Strategy strategy, bool fullMode)
    {
        if (!fullMode
            || strategy.Enshroud != OffensiveStrategy.Automatic
            || strategy.RedGauge.Value == RedGaugeStrategy.Force
            || BlueGauge != 100)
            return false;

        var nextGCDRedGaugeGain = NextGCDRedGaugeGain();
        return nextGCDRedGaugeGain > 0
            && RedGauge + nextGCDRedGaugeGain > 100
            && CanWeave(AID.Enshroud);
    }

    private bool ShouldRefreshDeathsDesignAfterFirstReapingBeforeArcaneCircle(in Strategy strategy, bool fullMode, float timer)
        => fullMode
        && IsEvenBurstPlanningWindow(strategy, fullMode)
        && strategy.Buffs != OffensiveStrategy.Delay
        && strategy.Enshroud != OffensiveStrategy.Delay
        && Unlocked(AID.ArcaneCircle)
        && Unlocked(AID.Enshroud)
        && Unlocked(AID.Perfectio)
        && (Unlocked(AID.ShadowofDeath) || Unlocked(AID.WhorlofDeath))
        && !ReaverOrExecutioner
        && !QueueablePerfectio(strategy)
        && !PlentifulHarvestReady(strategy)
        && IdealHost == 0
        && ArcaneCircleLeft <= 0
        && (Enshrouded || BlueGauge >= 50)
        && _deferredPreArcaneEnshroud
        && _arcaneCircleReadyIn <= PreArcaneEnshroudEntryWindow
        && timer > 0
        && timer <= 30f
        && (Enshrouded || timer > GCD + 1.8f);

    private bool ShouldDeferDeathsDesignRefreshToPreArcaneEnshroud(in Strategy strategy, bool fullMode, float timer)
        => fullMode
        && strategy.Buffs != OffensiveStrategy.Delay
        && strategy.Enshroud != OffensiveStrategy.Delay
        && Unlocked(AID.ArcaneCircle)
        && Unlocked(AID.Enshroud)
        && (Unlocked(AID.ShadowofDeath) || Unlocked(AID.WhorlofDeath))
        && !Enshrouded
        && !ReaverOrExecutioner
        && !QueueablePerfectio(strategy)
        && !PlentifulHarvestReady(strategy)
        && IdealHost == 0
        && BlueGauge >= 50
        && ArcaneCircleLeft <= 0
        && _arcaneCircleReadyIn > BurstPlanGCDLength * 2.5f
        && (_arcaneCircleReadyIn <= BurstPlanGCDLength * 3.5f
            || _arcaneCircleReadyIn <= BurstPlanGCDLength * 4.0f && BlueGauge == 100 && RedGauge <= 50 && GCDReady(AID.SoulSlice))
        && timer > GCD + BurstPlanGCDLength + 1.8f;

    private void UseSoul(in Strategy strategy, Enemy? primaryTarget, bool fullMode, bool potionQueuedThisGCD, bool arcaneCircleQueuedThisGCD, bool enshroudQueuedThisGCD)
    {
        var fallbackTarget = primaryTarget;
        if (fallbackTarget == null)
            return;

        var nextGCDRedGaugeGain = NextGCDRedGaugeGain();
        var redGaugeAfterNextGCD = Math.Min(100, RedGauge + nextGCDRedGaugeGain);
        var nextGCDSoulSlice = NextGCD is AID.SoulSlice or AID.SoulScythe;
        var normalOpenerArcaneCircleGluttony = IsNormalOpenerSoulSliceBurstGCD(strategy)
            && arcaneCircleQueuedThisGCD
            && !potionQueuedThisGCD;
        var postPerfectioFollowupActive = PostPerfectioPriorityActive || LastActionUsedRecently(AID.Perfectio, GCDLength + 1f);
        var prioritizeGluttonyAfterPerfectio = ShouldPrioritizeGluttonyAfterPerfectio(strategy, fullMode);
        var postPerfectioHigherPriorityGCDQueued = PostPerfectioHigherPriorityGCDQueued(strategy, fullMode);
        var forceRedGauge = strategy.RedGauge.Value == RedGaugeStrategy.Force;
        var evenBurstShroudPlan = BuildEvenBurstShroudPlan(strategy, fullMode);
        // FightRemaining (value-of-information experiment): no Shroud is built for a burst that cannot finish before the end
        var voiBurstGone = VoiReleaseActive(VoiReleaseSoul);
        var degradedEvenBurstBlueGaugeBuild = !voiBurstGone && ShouldBuildShroudDuringDegradedEvenBurst(strategy, fullMode);
        var earlyBlueGaugeBuild = !voiBurstGone && CanBuildShroudBeforeEvenBurst(strategy, fullMode, 1);
        var plannedBlueGaugeBuild = (!voiBurstGone && evenBurstShroudPlan.Urgent || degradedEvenBurstBlueGaugeBuild || earlyBlueGaugeBuild)
            && redGaugeAfterNextGCD >= 50;

        // hard requirements
        // The plan below looks one GCD ahead, but a spender pushed before the gauge can pay for it is still picked by the queue
        // (which does not know job gauges) and refused by the game for the whole weave window, blocking every lower-priority oGCD
        // there (True North, Arcane Crest). Once the next GCD lands, the following frame re-plans with the real gauge.
        if (RedGauge < 50 || Enshrouded || ReaverOrExecutioner || strategy.RedGauge == RedGaugeStrategy.Delay || !Unlocked(AID.BloodStalk) && !Unlocked(AID.Gluttony))
            return;

        // PH's free Enshroud must start before a hypothetical filler overcap can reserve another avatar GCD.
        if (!forceRedGauge && enshroudQueuedThisGCD && IdealHost > 0 && _evenBurstPlentifulHarvestState == EvenBurstPlentifulHarvestState.Used)
            return;

        // Out of reach only Gluttony and Grim Swathe can spend the Soul, and their reaver GCDs would be Guillotines (see HoldForMeleeReturn).
        if (!forceRedGauge
            && redGaugeAfterNextGCD < 100
            && NumConeTargets < AOEThresholdTargets
            && !TargetInMeleeRange(fallbackTarget)
            && ResolveTargetOverride(strategy.RedGauge) == null
            && HoldForMeleeReturn(strategy))
            return;

        if (!forceRedGauge && BlueGauge >= 50
            && (PlentifulHarvestPending(strategy, arcaneCircleQueuedThisGCD)
                || ShouldHoldArcaneCircleForRecoveryEnshroud(strategy, fullMode)))
            return;

        var nextGCDImportant = NextGCD is AID.PlentifulHarvest or AID.Perfectio || QueueablePerfectio(strategy) || PlentifulHarvestReady(strategy);
        if (!forceRedGauge && nextGCDImportant)
            return;

        var comboProtected = ComboLastMove is AID.Slice or AID.WaxingSlice;
        var soulSliceChargeCapImminent = MaxChargesIn(AID.SoulSlice) <= GCD + GCDLength + 0.1f;
        var preBurstSoulSliceChargeProtection = ShouldSpendRedForPreBurstSoulSlice(strategy, fullMode);
        var preBurstCappedRedSpend = ShouldSpendCappedRedBeforeEvenBurstEnshroud(strategy, fullMode);
        var targetDyingSoon = TargetWillDieWithinTwoGCDs(primaryTarget);
        // FightRemaining (value-of-information experiment): Gluttony and its two Executioner GCDs cannot finish before the end: the Soul goes into Blood Stalk now
        var voiSpendSoul = VoiReleaseSoul
            && ValidDeathsDesignHintTime(VoiFightEnd())
            && VoiFightEnd() < VoiMaxHorizon
            && !VoiGluttonyCanFinish(strategy);
        var holdNewBurst = RaidBuffsLeft <= 0
            && !forceRedGauge
            && ShouldHoldNewBurstForEndingDutyTarget(primaryTarget);
        var imminentDeathsDesignSoulGain = 0;
        var potentialTargets = Hints.PotentialTargets;
        var potentialTargetCount = potentialTargets.Count;
        for (var i = 0; i < potentialTargetCount; ++i)
        {
            var enemy = potentialTargets[i];
            if (StatusDetails(enemy.Actor, SID.DeathsDesign, Player.InstanceID, 30).Left > 0 && TargetIsPredictedDead(enemy.Actor))
                imminentDeathsDesignSoulGain += 10;
        }
        var predictedRedGaugeGain = nextGCDRedGaugeGain + imminentDeathsDesignSoulGain;
        var redGaugeOvercapImminent = predictedRedGaugeGain > 0 && RedGauge + predictedRedGaugeGain > 100;
        var basicModeRedGaugeCapProtection = !fullMode
            && strategy.RedGauge.Value == RedGaugeStrategy.Automatic
            && (RedGauge == 100 || redGaugeOvercapImminent);
        var spendRedBeforeEnshroud = strategy.Enshroud == OffensiveStrategy.Automatic
            && enshroudQueuedThisGCD
            && redGaugeOvercapImminent
            && BlueGauge <= 90;
        var waitingForArcaneCircle = WaitingForArcaneCircle(strategy, fullMode, out var gluttonyReadyDelayExpired, out _)
            && (!gluttonyReadyDelayExpired || arcaneCircleQueuedThisGCD)
            && !normalOpenerArcaneCircleGluttony;
        var recoveryGluttony = ShouldUseGluttonyForEvenBurstRecovery(strategy, fullMode);
        var burstAdjustmentActive = strategy.Buffs != OffensiveStrategy.Delay
            && (evenBurstShroudPlan.Active
                || degradedEvenBurstBlueGaugeBuild
                || waitingForArcaneCircle
                || _recoveringEvenBurst
                || _degradedEvenBurstFallback
                || _deferredPreArcaneEnshroud
                || ArcaneCircleLeft > 0
                || RaidBuffsLeft > 0);
        var normalSoulSpendWindow = fullMode
            && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
            && strategy.RedGauge.Value == RedGaugeStrategy.Automatic
            && !postPerfectioFollowupActive
            && !burstAdjustmentActive;
        var normalGluttonyReady = normalSoulSpendWindow
            && RedGauge >= 50;
        var comboGCDsRemaining = ComboLastMove switch
        {
            AID.Slice => 2,
            AID.WaxingSlice => 1,
            _ => 0
        };
        var comboContinuesThisGCD = ComboLastMove == AID.Slice && NextGCD == AID.WaxingSlice
            || ComboLastMove == AID.WaxingSlice && NextGCD == AID.InfernalSlice;
        var canDeferComboForGluttony = !comboProtected
            || comboContinuesThisGCD
            || CanFitGCD(World.Client.ComboState.Remaining, 2 + comboGCDsRemaining);
        var gluttonyDriftUrgent = gluttonyReadyDelayExpired && !arcaneCircleQueuedThisGCD;
        var gluttonyTarget = GluttonyTarget(strategy);
        var enshroudReadyToExecute = enshroudQueuedThisGCD && ReadyIn(AID.Enshroud) <= 0.1f;
        // While an Enshroud is held for after a known loss (the main rotation would be inside it here), a Gluttony whose
        // Executioner stacks cannot be spent before the loss would be spent first on the return and push the held Enshroud
        // past its Ideal Host or Perfectio Occulta timer.
        var enshroudHeldForLoss = Unlocked(AID.Enshroud) && ReadyIn(AID.Enshroud) <= GCD && ShouldHoldEnshroudForTargetLoss(strategy);
        var lossIn = DeathsDesignTargetLossIn();
        var gluttonyStrandedByHeldEnshroud = enshroudHeldForLoss && lossIn < GCD + GCDLength + 0.1f && HeldEnshroudTimersMissedByReaver(2);
        var canUseGluttony = strategy.RedGauge.Value is RedGaugeStrategy.Automatic or RedGaugeStrategy.Force or RedGaugeStrategy.ReserveGluttony
            && (fullMode || forceRedGauge)
            && Unlocked(AID.Gluttony)
            && gluttonyTarget != null
            && _gluttonyReadyIn <= 0.1f
            && !holdNewBurst
            && (forceRedGauge || !enshroudReadyToExecute)
            && (forceRedGauge || BlueGauge <= 80 || enshroudReadyToExecute)
            && (!earlyBlueGaugeBuild || CanBuildShroudBeforeEvenBurst(strategy, fullMode, 2))
            && (forceRedGauge || !gluttonyStrandedByHeldEnshroud)
            && (forceRedGauge
                || prioritizeGluttonyAfterPerfectio
                || !postPerfectioHigherPriorityGCDQueued
                    && (normalGluttonyReady || canDeferComboForGluttony || gluttonyDriftUrgent || targetDyingSoon || recoveryGluttony)
                    && (!waitingForArcaneCircle || recoveryGluttony || plannedBlueGaugeBuild));

        var gluttonySoon = fullMode && gluttonyTarget != null && Unlocked(AID.Gluttony) && CanWeave(AID.Gluttony, 5);
        var postBurstBlueGaugeBuild = fullMode
            && strategy.SkillRotation.Value == SkillRotationStrategy.Normal
            && strategy.RedGauge.Value == RedGaugeStrategy.Automatic
            && _arcaneCircleCycleStarted
            && ArcaneCircleLeft <= 0
            && RaidBuffsLeft <= 0
            && _arcaneCircleReadyIn > EvenBurstShroudPlanLead
            && !_recoveringEvenBurst
            && !_degradedEvenBurstFallback
            && !_deferredPreArcaneEnshroud
            && BlueGauge < RequiredShroudForNextEvenBurst(strategy)
            && !gluttonySoon;
        // Fill to 100 when safe regardless of potions; retain the existing minimum-gauge fallback.
        var spendEarly = RaidBuffsLeft > 0 || BlueGauge < 50 || plannedBlueGaugeBuild || postBurstBlueGaugeBuild;
        var normalSoulSliceChargeProtection = normalSoulSpendWindow
            && soulSliceChargeCapImminent
            && !gluttonySoon;
        // Optional Shroud gains still reserve Soul for an imminent Gluttony.
        var reserveUpcomingGluttony = earlyBlueGaugeBuild
            && gluttonySoon
            && _gluttonyReadyIn > 0.1f
            && BlueGauge <= 80
            && !evenBurstShroudPlan.Urgent
            && !degradedEvenBurstBlueGaugeBuild
            && !waitingForArcaneCircle;
        var preArcaneBlueGaugeBuild = plannedBlueGaugeBuild
            && !reserveUpcomingGluttony
            && (!gluttonySoon || !canUseGluttony || waitingForArcaneCircle);
        var canBreakComboForBlueGaugeBuild = preArcaneBlueGaugeBuild || postBurstBlueGaugeBuild || preBurstSoulSliceChargeProtection || preBurstCappedRedSpend || normalSoulSliceChargeProtection;
        var bloodStalkWouldOverflowShroud = Unlocked(AID.Gallows) && BlueGauge > 90;
        var canUseBloodStalk = Unlocked(AID.BloodStalk)
            && (forceRedGauge || basicModeRedGaugeCapProtection || !bloodStalkWouldOverflowShroud || enshroudReadyToExecute)
            && (forceRedGauge || basicModeRedGaugeCapProtection || redGaugeOvercapImminent || !comboProtected || redGaugeAfterNextGCD == 100 || canBreakComboForBlueGaugeBuild || voiSpendSoul);

        // before 70, blood stalk IS our dps output from red gauge, so we don't want to waste it on dying targets
        var haveBlueGauge = Unlocked(AID.Gallows);
        var targetOverride = ResolveTargetOverride(strategy.RedGauge);
        var bloodStalkTarget = targetOverride != null
            ? UsableTarget(targetOverride, 3)
            : FallbackTargetInRange(fallbackTarget, 3);
        var grimSwatheTarget = HostileAOEAllowed(strategy)
            ? TargetOverrideOrDefault(strategy.RedGauge, BestConeTarget, 8, ConeTargetCheck)
            : null;

        void useBloodStalk()
        {
            if (ReaverOrExecutioner || !canUseBloodStalk)
                return;

            var unveiledAction = EnhancedGibbet > GCD && Unlocked(AID.UnveiledGibbet)
                ? AID.UnveiledGibbet
                : EnhancedGallows > GCD && Unlocked(AID.UnveiledGallows)
                    ? AID.UnveiledGallows
                    : AID.None;
            var grimSwatheRangeFallback = grimSwatheTarget != null && !TargetInMeleeRange(targetOverride ?? fallbackTarget);
            if (Unlocked(AID.GrimSwathe)
                && grimSwatheTarget != null
                && (targetOverride != null && !TargetInMeleeRange(targetOverride)
                    || targetOverride == null && (NumConeTargets >= AOEThresholdTargets || grimSwatheRangeFallback)))
            {
                PushOGCD(AID.GrimSwathe, grimSwatheTarget, spendRedBeforeEnshroud ? OGCDPriority.BloodStalkBeforeEnshroud : OGCDPriority.BloodStalk);
                return;
            }

            var action = unveiledAction != AID.None ? unveiledAction : AID.BloodStalk;
            if (bloodStalkTarget != null)
                PushOGCD(action, bloodStalkTarget, spendRedBeforeEnshroud ? OGCDPriority.BloodStalkBeforeEnshroud : OGCDPriority.BloodStalk, useOnDyingTarget: haveBlueGauge || forceRedGauge);
        }

        if (spendRedBeforeEnshroud)
        {
            useBloodStalk();
            return;
        }

        if (postPerfectioHigherPriorityGCDQueued || !forceRedGauge && enshroudQueuedThisGCD)
            return;

        // Only Death's Design already on the target counts. A Shadow of Death queued as the next GCD does not cover the
        // spender's reaver GCDs: Executioner and Soul Reaver outrank the refresh (Reaver > DDExpiring), so spending in this
        // window pushes the refresh behind them. Waiting one window lets the refresh land first.
        var debuffLeft = Math.Min(TargetDDLeft, ShortestNearbyDDLeft);
        // FightRemaining (value-of-information experiment): a refresh that the fight end refuses cannot be waited for
        if (VoiReleaseSoul && VoiRefreshNotWorthIt(debuffLeft))
            debuffLeft = float.MaxValue;
        if (nextGCDSoulSlice && redGaugeAfterNextGCD >= 50)
        {
            if (canUseGluttony
                && (forceRedGauge || CanWeave(AID.Gluttony))
                && (forceRedGauge || CanFitGCD(debuffLeft, 2)))
            {
                PushOGCD(AID.Gluttony, gluttonyTarget, OGCDPriority.Gluttony);
                return;
            }

            if (forceRedGauge || CanFitGCD(debuffLeft, 1) || redGaugeOvercapImminent)
                useBloodStalk();

            return;
        }

        switch (strategy.RedGauge.Value)
        {
            case RedGaugeStrategy.Automatic:
                if (AutomaticShroudCapPressure(strategy, fullMode) && enshroudQueuedThisGCD && !redGaugeOvercapImminent && !postPerfectioFollowupActive && !preBurstSoulSliceChargeProtection && !preBurstCappedRedSpend)
                    return;

                if (canUseGluttony && CanFitGCD(debuffLeft, 2))
                {
                    PushOGCD(AID.Gluttony, gluttonyTarget, OGCDPriority.Gluttony);
                    return;
                }

                if ((!recoveryGluttony || redGaugeOvercapImminent) && (CanFitGCD(debuffLeft, 1) || redGaugeOvercapImminent) && (redGaugeOvercapImminent || voiSpendSoul || redGaugeAfterNextGCD == 100 || normalSoulSliceChargeProtection || preArcaneBlueGaugeBuild || preBurstSoulSliceChargeProtection || preBurstCappedRedSpend || spendEarly && !gluttonySoon))
                    useBloodStalk();
                break;
            case RedGaugeStrategy.ReserveGluttony:
                if (fullMode && BlueGauge == 100 && enshroudQueuedThisGCD)
                    return;

                // the Soul is kept for Gluttony, so Gluttony itself goes as in automatic; only an overflow is spent elsewhere
                if (canUseGluttony && CanFitGCD(debuffLeft, 2))
                {
                    PushOGCD(AID.Gluttony, gluttonyTarget, OGCDPriority.Gluttony);
                    return;
                }

                if (CanFitGCD(debuffLeft, 1) && redGaugeAfterNextGCD == 100)
                    useBloodStalk();
                break;
            case RedGaugeStrategy.Force:
                if (canUseGluttony)
                {
                    PushOGCD(AID.Gluttony, gluttonyTarget, OGCDPriority.Gluttony);
                    return;
                }

                useBloodStalk();
                break;
        }
    }

    private void Slice(in Strategy strategy, Enemy? primaryTarget, bool fullMode)
    {
        var forceSlice = strategy.Slice.Value == SliceStrategy.Force;
        var normalOpenerSoulSliceBeforeArcane = ShouldHoldNormalOpenerArcaneCircleForSoulSlice(strategy);
        var zeroSecondOpenerSoulSliceBeforeDD = ShouldUseZeroSecondOpenerSoulSliceBeforeDeathsDesign(strategy, fullMode);
        var recoverySoulSliceBeforeArcane = ShouldUseSoulSliceForEvenBurstRecovery(strategy, fullMode);
        var plannedSoulSliceBeforeArcane = ShouldUseSoulSliceForEvenBurstShroudPlan(strategy, fullMode);
        var degradedEvenBurstSoulSlice = ShouldUseSoulSliceDuringDegradedEvenBurst(strategy, fullMode);
        var preBurstSoulSliceChargeProtection = ShouldUseSoulSliceBeforeEvenBurstChargeCap(strategy, fullMode);
        var lateBurstSoulSliceBeforePH = ShouldUseLateBurstSoulSliceBeforePlentifulHarvest(strategy, fullMode);
        var postPerfectioSoulSlice = ShouldPrioritizeSoulSliceAfterPerfectio(strategy, primaryTarget, fullMode);
        var postPerfectioGluttonyReady = ShouldPrioritizeGluttonyAfterPerfectio(strategy, fullMode);
        if (Enshrouded || ReaverOrExecutioner || !forceSlice && RedGauge > 50)
            return;

        var canSoulSlice = Unlocked(AID.SoulSlice) && GCDReady(AID.SoulSlice);
        var canSoulScythe = Unlocked(AID.SoulScythe) && GCDReady(AID.SoulScythe);
        if (!canSoulSlice && !canSoulScythe)
            return;

        var soulSliceChargeCapImminent = MaxChargesIn(canSoulSlice ? AID.SoulSlice : AID.SoulScythe) <= GCD + GCDLength + 0.1f;

        if (!forceSlice && QueueablePerfectio(strategy))
            return;

        if (!forceSlice
            && !soulSliceChargeCapImminent
            && !plannedSoulSliceBeforeArcane
            && !degradedEvenBurstSoulSlice
            && !preBurstSoulSliceChargeProtection
            && (!lateBurstSoulSliceBeforePH && PlentifulHarvestReady(strategy)
                // Raid buffs only justify skipping Soul Slice when the GCD that replaces it hits harder: with the reaver,
                // Perfectio and Harvest cases already returned above, that is only the combo finisher.
                || !lateBurstSoulSliceBeforePH && !postPerfectioSoulSlice && !degradedEvenBurstSoulSlice && RaidBuffsLeft > 0 && ComboLastMove == AID.WaxingSlice
                || !normalOpenerSoulSliceBeforeArcane && !zeroSecondOpenerSoulSliceBeforeDD && !recoverySoulSliceBeforeArcane && !plannedSoulSliceBeforeArcane && !degradedEvenBurstSoulSlice && !preBurstSoulSliceChargeProtection && !lateBurstSoulSliceBeforePH && !postPerfectioSoulSlice && ShouldQueueArcaneCircle(strategy, fullMode)))
            return;

        if (!forceSlice && !postPerfectioSoulSlice && !soulSliceChargeCapImminent && postPerfectioGluttonyReady)
            return;

        if (!forceSlice && TargetWillDieWithinTwoGCDs(primaryTarget)
            && !(VoiSoulSliceAtEnd && ValidDeathsDesignHintTime(VoiFightEnd()) && ComboLastMove != AID.WaxingSlice))
            return;

        var burstSoon = fullMode && strategy.Buffs != OffensiveStrategy.Delay && _arcaneCircleReadyIn <= 6f;
        if (!forceSlice && !normalOpenerSoulSliceBeforeArcane && !zeroSecondOpenerSoulSliceBeforeDD && !recoverySoulSliceBeforeArcane && !plannedSoulSliceBeforeArcane && !degradedEvenBurstSoulSlice && !preBurstSoulSliceChargeProtection && !lateBurstSoulSliceBeforePH && !postPerfectioSoulSlice && burstSoon && !soulSliceChargeCapImminent)
            return;

        var shouldUse = strategy.Slice.Value switch
        {
            SliceStrategy.Automatic => postPerfectioSoulSlice || recoverySoulSliceBeforeArcane || plannedSoulSliceBeforeArcane || degradedEvenBurstSoulSlice || preBurstSoulSliceChargeProtection || RedGauge <= 50 || soulSliceChargeCapImminent,
            SliceStrategy.Force => true,
            _ => false
        };

        if (!shouldUse)
            return;

        var priority = forceSlice
            ? GCDPriority.Max
            : postPerfectioSoulSlice || soulSliceChargeCapImminent || plannedSoulSliceBeforeArcane || degradedEvenBurstSoulSlice || preBurstSoulSliceChargeProtection
                ? GCDPriority.SoulSliceChargeCap
                : lateBurstSoulSliceBeforePH
                    ? GCDPriority.LateBurstSoulSliceBeforePH
                    : GCDPriority.SoulSlice;
        var sliceTarget = ResolveTargetOverride(strategy.Slice);
        var fallbackTarget = FallbackTarget(primaryTarget);

        if (sliceTarget != null)
        {
            if (canSoulSlice && UsableTarget(sliceTarget, 3) != null)
                PushGCD(AID.SoulSlice, sliceTarget, priority);
            else if (canSoulScythe
                && HostileAOEAllowed(strategy)
                && UsableTarget(sliceTarget, 5) != null
                && AOETargetSafe(sliceTarget, SelfCircleTargetCheck))
                PushGCD(AID.SoulScythe, Player, priority);

            return;
        }

        if (canSoulScythe
            && (NumAOETargets >= AOEThresholdTargets
                || NumAOETargets > 0 && !TargetInMeleeRange(fallbackTarget) && (forceSlice || soulSliceChargeCapImminent || !HoldForMeleeReturn(strategy))))
        {
            PushGCD(AID.SoulScythe, Player, priority);
            return;
        }

        if (canSoulSlice && TargetInMeleeRange(fallbackTarget))
            PushGCD(AID.SoulSlice, fallbackTarget, priority);
    }

    private void EnshroudGCDs(in Strategy strategy, Enemy? primaryTarget)
    {
        if (BlueSouls == 0 || !CanUseEnshroudActions() && !Unlocked(AID.Communio))
            return;

        var fallbackTarget = FallbackTarget(primaryTarget);

        // Without Communio, fall through to a Reaping action that consumes the last Lemure stack.
        if ((BlueSouls == 1 || !CanFitEnshroudGCD(EnshroudLeft, 1)) && HostileAOEAllowed(strategy) && Unlocked(AID.Communio) && strategy.Communio.IsEnabled())
        {
            var target = TargetOverrideOrDefault(strategy.Communio, BestRangedAOETarget, 25, IsSplashTarget);
            if (target != null)
            {
                PushGCD(AID.Communio, target, GCDPriority.Communio);
                return;
            }
        }

        if (Unlocked(AID.GrimReaping)
            && BestConeTarget != null
            && (NumConeTargets > 2 || !TargetInMeleeRange(fallbackTarget)))
        {
            PushGCD(AID.GrimReaping, BestConeTarget, GCDPriority.EnshroudMove);
            return;
        }

        var reaping = EnhancedCrossReaping > GCD && Unlocked(AID.CrossReaping)
            ? AID.CrossReaping
            : Unlocked(AID.VoidReaping)
                ? AID.VoidReaping
                : Unlocked(AID.CrossReaping)
                    ? AID.CrossReaping
                    : AID.None;

        if (reaping != AID.None && TargetInMeleeRange(fallbackTarget))
        {
            PushGCD(reaping, fallbackTarget, GCDPriority.Lemure);
            return;
        }

        if (HostileAOEAllowed(strategy) && strategy.HM.Value != OffensiveStrategy.Delay && Unlocked(AID.HarvestMoon) && Soulsow)
        {
            var target = TargetOverrideOrDefault(strategy.HM, BestRangedAOETarget, 25, IsSplashTarget);
            if (target != null)
                PushGCD(AID.HarvestMoon, target, GCDPriority.EnshroudMove);
        }
    }

    protected override float GetCastTime(AID aid)
    {
        if (aid == AID.Harpe && EnhancedHarpe > GCD)
            return 0;

        if (aid == AID.Soulsow && !Player.InCombat)
            return 0;

        var definition = ActionDefinitions.Instance.Spell(aid);
        if (definition is not { Category: ActionCategory.Spell })
            return base.GetCastTime(aid);

        if (SwiftcastLeft > GCD)
            return 0;

        var stats = World.Client.PlayerStats;
        var castTimeMS = (int)(definition.CastTime * 1000);
        return castTimeMS * ActionSpeed.SpeedStatToModifier(stats.SpellSpeed, Player.Level) / 1000 * stats.Haste / 100 * 0.001f;
    }

    private float PotionReadyIn()
    {
        var potion = ActionDefinitions.Instance[ActionDefinitions.IDPotionStr];
        if (potion == null)
            return float.MaxValue;

        // Items can have zero charges in action data; their recast is a single shared cooldown.
        var group = potion.ActualMainCooldownGroup(World.Client.DutyActions);
        return Math.Max(group >= 0 ? World.Client.Cooldowns[group].Remaining : 0, potion.ExtraReadyIn(World.Client.Cooldowns));
    }

    private bool CanUsePotion()
        => PotionLeft <= 0
        && World.Client.GetInventoryItemQuantity(ActionDefinitions.IDPotionStr.ID) > 0
        && PotionReadyIn() <= 0.1f;

    private bool TargetInRange(Enemy? target, float range)
        => target != null && Player.DistanceToHitbox(target.Actor) <= range;

    private bool TargetInMeleeRange(Enemy? target)
        => TargetInRange(target, 3);

    private bool SelfCircleTarget(Actor primary, Actor other)
        => TargetInAOECircle(other, Player.Position, 5f);

    private bool ConeTarget(Actor primary, Actor other)
        => TargetInAOECone(other, Player.Position, 8f, Player.DirectionTo(primary), 90f.Degrees());

    private bool LineTarget(Actor primary, Actor other)
        => TargetInAOERect(other, Player.Position, Player.DirectionTo(primary), 15f, 2f);

    private Enemy? UsableTarget(Enemy? target, float range)
        => target != null
            && target.Actor.IsTargetable
            && !target.Actor.IsDead
            && target.Priority is not Enemy.PriorityInvincible and not Enemy.PriorityForbidden
            && !TargetIsPredictedDead(target.Actor)
            && TargetInRange(target, range)
            ? target
            : null;

    private Enemy? FirstUsablePriorityTarget(float range)
    {
        foreach (var target in Hints.PriorityTargetsSpan)
            if (UsableTarget(target, range) != null)
                return target;
        return null;
    }

    private Enemy? FirstPriorityTargetOrDefault()
        => Hints.PriorityTargetsSpan.Length > 0 ? Hints.PriorityTargetsSpan[0] : null;

    private bool DeathsDesignAOESafe()
    {
        foreach (var enemy in Hints.ForbiddenTargetsSpan)
            if (TargetInAOECircle(enemy.Actor, Player.Position, 5f))
                return false;
        return true;
    }

    private bool AOETargetSafe(Enemy target, PositionCheck isInAOE)
    {
        Enemy? forbiddenHit = null;
        var forbiddenHits = 0;
        foreach (var enemy in Hints.ForbiddenTargetsSpan)
        {
            if (isInAOE(target.Actor, enemy.Actor))
            {
                forbiddenHit = enemy;
                ++forbiddenHits;
            }
        }
        if (forbiddenHits == 0)
            return true;

        if (target.Priority != Enemy.PriorityUndesirable || forbiddenHits != 1 || forbiddenHit!.Actor != target.Actor)
            return false;
        foreach (var enemy in Hints.PriorityTargetsSpan)
            if (isInAOE(target.Actor, enemy.Actor))
                return false;
        return true;
    }

    private Enemy? HarpeTarget(in Strategy strategy, Enemy? rangedPrimaryTarget)
        => TargetOverrideOrDefault(strategy.Harpe, rangedPrimaryTarget, 25);

    private Enemy? TargetOverrideOrDefault<T>(in Track<T> track, Enemy? defaultTarget, float range, PositionCheck? isInAOE = null) where T : struct
    {
        var overrideTarget = ResolveTargetOverride(track);
        var target = UsableTarget(overrideTarget ?? defaultTarget, range);
        if (target == null)
            return null;

        return isInAOE == null || AOETargetSafe(target, isInAOE) ? target : null;
    }

    private float NormalAOEComboPotency(int targets)
    {
        if (targets <= 0 || !Unlocked(AID.SpinningScythe))
            return 0;

        var meleeMastery = CurrentMasteryTraits().DeathScythe1;
        var potency = ComboLastMove == AID.SpinningScythe && Unlocked(AID.NightmareScythe)
            ? RprPotency.NightmareScytheCombo(meleeMastery)
            : RprPotency.SpinningScythe(meleeMastery);
        return potency * targets;
    }

    private float NormalSTComboPotency()
    {
        var traits = CurrentMasteryTraits();
        return ComboLastMove switch
        {
            AID.WaxingSlice => RprPotency.InfernalSliceCombo(traits.DeathScythe1, traits.DeathScythe2, traits.MeleeMastery3),
            AID.Slice => RprPotency.WaxingSliceCombo(traits.DeathScythe1, traits.DeathScythe2, traits.MeleeMastery3),
            _ => RprPotency.Slice(traits.DeathScythe1, traits.DeathScythe2, traits.MeleeMastery3)
        };
    }

    // Shared 3-target AOE threshold (combo, Soul Scythe, Guillotine, Whorl of Death, Grim Swathe), matching upstream.
    private bool ShouldUseNormalAOECombo(int targets)
        => targets >= AOEThresholdTargets && Unlocked(AID.SpinningScythe);

    private int NormalAOEGoalTargetCount() => NormalAOEComboTargetCount;

    private (Positional, bool) GetNextPositional(Enemy? primaryTarget, bool breakReaver)
    {
        if (primaryTarget == null
            || breakReaver
            || !Unlocked(AID.Gibbet)
            || BestConeTarget != null && (ShouldUseReaverAOE(NumConeTargets) || !TargetInMeleeRange(primaryTarget)))
            return (Positional.Any, false);

        Positional nextPos;

        if (EnhancedGallows > GCD)
            nextPos = Positional.Rear;
        else if (EnhancedGibbet > GCD)
            nextPos = Positional.Flank;
        else
        {
            var closest = GetCurrentPositional(primaryTarget.Actor);
            nextPos = closest == Positional.Front ? Positional.Flank : closest;
        }

        return (nextPos, ReaverOrExecutioner);
    }

    private static bool ShouldUseReaverAOE(int targets) => targets >= AOEThresholdTargets;

    private float DDLeft(Enemy? target)
        => (target?.ForbidDOTs ?? false)
            ? float.MaxValue
            : StatusDetails(target?.Actor, SID.DeathsDesign, Player.InstanceID, 30).Left;
}
