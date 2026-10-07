using BossMod.GNB;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.akechi.Custom;

public sealed class AkechiGNB(RotationModuleManager manager, Actor player) : AkechiTools<AID, TraitID>(manager, player)
{
    public enum Track { Potion = SharedTrack.Potion, AOE = SharedTrack.Count, Cartridges, LightningShot, Zone, NoMercy, SonicBreak, GnashingFang, BowShock, Continuation, Bloodfest, DoubleDown, Reign, FightEnd, RotationMode, MechanicHints }
    public enum RotationModeStrategy { Full, NormalOvercapOnly }
    public enum AOEStrategy
    {
        AutoFinishWithOvercap, AutoFinishWithoutOvercap,
        ForceSTFinishWithOvercap, ForceSTFinishWithoutOvercap,
        ForceAOEFinishWithOvercap, ForceAOEFinishWithoutOvercap,
        AutoBreakWithOvercap, AutoBreakWithoutOvercap,
        ForceSTBreakWithOvercap, ForceSTBreakWithoutOvercap,
        ForceAOEBreakWithOvercap, ForceAOEBreakWithoutOvercap
    }
    public enum CartridgeStrategy
    {
        Automatic, OnlyBS, OnlyFC,
        ForceBS, ForceBS1, ForceBS2, ForceBS3,
        ForceFC, ForceFC1, ForceFC2, ForceFC3,
        Delay,
        NormalOvercapOnly
    }
    public enum LightningShotStrategy { OpenerFar, OpenerForce, Force, Allow, Forbid }
    public enum NoMercyStrategy
    {
        Automatic, BurstReady, Together,
        Force, ForceW, ForceQW,
        Force1, Force1W, Force1QW,
        Force2, Force2W, Force2QW,
        Force3, Force3W, Force3QW,
        Delay,
        SynergyOptimized
    }
    public enum SonicBreakStrategy { Automatic, Early, Late, Force, Delay }
    public enum GnashingStrategy { Automatic, ForceGnash, ForceGnash1, ForceGnash2, ForceGnash3, ForceClaw, ForceTalon, Delay }
    public enum ContinuationStrategy { Automatic, Early, Late }
    public enum BloodfestStrategy { Automatic, Together, Force, ForceW, Delay }
    public enum DoubleDownStrategy { Automatic, Force, Force3, Delay }
    public enum ReignStrategy { Automatic, ForceReign, ForceNoble, ForceLion, Delay }
    public enum FightEndStrategy { Disabled, SpendResources }
    public enum PotionStrategyAkechi
    {
        Manual,
        AlignWithBuffs,
        AlignWithRaidBuffs,
        EvenMinuteNMOnly,
        Immediate,
        SixMinuteNM3GCD,
        EightMinuteNM3GCD,
        TwoAndEightMinuteNM3GCD
    }

    public static RotationModuleDefinition Definition()
    {
        var res = new RotationModuleDefinition("Akechi GNB [Custom]", "標準ローテーションモジュール", "標準ローテーション (Akechi)|タンク", "Akechi", RotationModuleQuality.Excellent, BitMask.Build((int)Class.GNB), 100);

        res.DefineTargeting();
        res.DefineHold();
        res.Define(Track.Potion).As<PotionStrategyAkechi>("Potion", "薬品使用", 280)
            .AddOption(PotionStrategyAkechi.Manual, "薬を自動使用しない")
            .AddOption(PotionStrategyAkechi.AlignWithBuffs, "ブラッドソイル効果中に薬を自動使用")
            .AddOption(PotionStrategyAkechi.AlignWithRaidBuffs, "ブラッドソイル効果中かつシナジーバフが近い、または有効な時に薬を自動使用")
            .AddOption(PotionStrategyAkechi.EvenMinuteNMOnly, "偶数分ブラッドソイル効果中に薬を自動使用")
            .AddOption(PotionStrategyAkechi.Immediate, "ブラッドソイル効果中なら薬を即時強制使用", 270, 30)
            .AddOption(PotionStrategyAkechi.SixMinuteNM3GCD, "6分付近のブラッドソイル効果中に薬を自動使用", supportedTargets: ActionTargets.Self)
            .AddOption(PotionStrategyAkechi.EightMinuteNM3GCD, "8分付近のブラッドソイル効果中に薬を自動使用", supportedTargets: ActionTargets.Self)
            .AddOption(PotionStrategyAkechi.TwoAndEightMinuteNM3GCD, "2分と8分付近のブラッドソイル効果中に薬を自動使用", supportedTargets: ActionTargets.Self)
            .AddAssociatedAction(ActionDefinitions.IDPotionStr);

        res.Define(Track.AOE).As<AOEStrategy>("ST/AOE", "単体・範囲ローテーション", 300)
            .AddOption(AOEStrategy.AutoFinishWithOvercap, "対象数で単体/範囲を自動選択。現在のコンボを可能なら完走し、ソイル溢れを防ぐ")
            .AddOption(AOEStrategy.AutoFinishWithoutOvercap, "対象数で単体/範囲を自動選択。現在のコンボを可能なら完走するが、ソイル溢れは防がない")
            .AddOption(AOEStrategy.ForceSTFinishWithOvercap, "単体回しを強制。現在のコンボを可能なら完走し、ソイル溢れを防ぐ")
            .AddOption(AOEStrategy.ForceSTFinishWithoutOvercap, "単体回しを強制。現在のコンボを可能なら完走するが、ソイル溢れは防がない")
            .AddOption(AOEStrategy.ForceAOEFinishWithOvercap, "範囲回しを強制。現在のコンボを可能なら完走し、ソイル溢れを防ぐ")
            .AddOption(AOEStrategy.ForceAOEFinishWithoutOvercap, "範囲回しを強制。現在のコンボを可能なら完走するが、ソイル溢れは防がない")
            .AddOption(AOEStrategy.AutoBreakWithOvercap, "対象数で単体/範囲を自動選択。必要なら現在のコンボを中断し、ソイル溢れを防ぐ")
            .AddOption(AOEStrategy.AutoBreakWithoutOvercap, "対象数で単体/範囲を自動選択。必要なら現在のコンボを中断し、ソイル溢れは防がない")
            .AddOption(AOEStrategy.ForceSTBreakWithOvercap, "単体回しを強制。必要なら現在のコンボを中断し、ソイル溢れを防ぐ")
            .AddOption(AOEStrategy.ForceSTBreakWithoutOvercap, "単体回しを強制。必要なら現在のコンボを中断し、ソイル溢れは防がない")
            .AddOption(AOEStrategy.ForceAOEBreakWithOvercap, "範囲回しを強制。必要なら現在のコンボを中断し、ソイル溢れを防ぐ")
            .AddOption(AOEStrategy.ForceAOEBreakWithoutOvercap, "範囲回しを強制。必要なら現在のコンボを中断し、ソイル溢れは防がない")
            .AddAssociatedActions(AID.KeenEdge, AID.BrutalShell, AID.SolidBarrel, AID.DemonSlice, AID.DemonSlaughter);

        res.Define(Track.Cartridges).As<CartridgeStrategy>("Carts", "ソイル消費", 199)
            .AddOption(CartridgeStrategy.Automatic, "対象数に応じてBurst Strike/Fated Circleを自動使用", 0, 0, ActionTargets.Hostile, 30)
            .AddOption(CartridgeStrategy.OnlyBS, "対象数に関係なくBurst Strikeでソイルを消費", 0, 0, ActionTargets.Hostile, 30)
            .AddOption(CartridgeStrategy.OnlyFC, "対象数に関係なくFated Circleでソイルを消費", 0, 0, ActionTargets.Hostile, 72)
            .AddOption(CartridgeStrategy.ForceBS, "ソイル数に関係なくBurst Strikeを強制使用", 0, 0, ActionTargets.Hostile, 30)
            .AddOption(CartridgeStrategy.ForceBS1, "ソイル1以上でBurst Strikeを強制使用", 0, 0, ActionTargets.Hostile, 30)
            .AddOption(CartridgeStrategy.ForceBS2, "ソイル2以上でBurst Strikeを強制使用", 0, 0, ActionTargets.Hostile, 30)
            .AddOption(CartridgeStrategy.ForceBS3, "ソイル3以上でBurst Strikeを強制使用", 0, 0, ActionTargets.Hostile, 30)
            .AddOption(CartridgeStrategy.ForceFC, "ソイルがあればFated Circleを強制使用", 0, 0, ActionTargets.Self, 72)
            .AddOption(CartridgeStrategy.ForceFC1, "ソイル1以上でFated Circleを強制使用", 0, 0, ActionTargets.Self, 72)
            .AddOption(CartridgeStrategy.ForceFC2, "ソイル2以上でFated Circleを強制使用", 0, 0, ActionTargets.Self, 72)
            .AddOption(CartridgeStrategy.ForceFC3, "ソイル3以上でFated Circleを強制使用", 0, 0, ActionTargets.Self, 72)
            .AddOption(CartridgeStrategy.Delay, "Burst Strike/Fated Circleを使用しない", minLevel: 30)
            .AddOption(CartridgeStrategy.NormalOvercapOnly, "旧設定互換: 通常回し+ソイル溢れ。現在はローテーションモード側を使用", minLevel: 30)
            .AddAssociatedActions(AID.BurstStrike, AID.FatedCircle);

        res.Define(Track.LightningShot).As<LightningShotStrategy>("Ranged", "Lightning Shot", 180)
            .AddOption(LightningShotStrategy.OpenerFar, "開幕前、近接範囲外ならLightning Shotを自動使用", supportedTargets: ActionTargets.Hostile)
            .AddOption(LightningShotStrategy.OpenerForce, "開幕前、距離に関係なくLightning Shotを自動使用", supportedTargets: ActionTargets.Hostile)
            .AddOption(LightningShotStrategy.Force, "距離に関係なくLightning Shotを強制使用", supportedTargets: ActionTargets.Hostile)
            .AddOption(LightningShotStrategy.Allow, "近接範囲外ならLightning Shotを許可", supportedTargets: ActionTargets.Hostile)
            .AddOption(LightningShotStrategy.Forbid, "Lightning Shotを使用しない")
            .AddAssociatedActions(AID.LightningShot);

        res.DefineOGCD(Track.Zone, AID.DangerZone, "Zone", "Danger / Blasting Zone", 193, 30, 0, ActionTargets.Hostile, 18).AddAssociatedActions(AID.BlastingZone, AID.DangerZone);

        res.Define(Track.NoMercy).As<NoMercyStrategy>("NM", "No Mercy設定", 197)
            .AddOption(NoMercyStrategy.Automatic, "No Mercyを自動使用", supportedTargets: ActionTargets.Self)
            .AddOption(NoMercyStrategy.BurstReady, "フルバースト準備完了時にNo Mercyを自動使用。必要なら待機", supportedTargets: ActionTargets.Self)
            .AddOption(NoMercyStrategy.Together, "バースト状況に関係なくBloodfestと合わせてNo Mercyを自動使用。必要なら待機", supportedTargets: ActionTargets.Self)
            .AddOption(NoMercyStrategy.Force, "No Mercyを即時強制使用", 0, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.ForceW, "次の可能なweave枠でNo Mercyを強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.ForceQW, "次の可能な最終weave枠でNo Mercyを強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.Force1, "ソイル1以上でNo Mercyを即時強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.Force1W, "ソイル1以上で次の可能なweave枠にNo Mercyを強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.Force1QW, "ソイル1以上で次の可能な最終weave枠にNo Mercyを強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.Force2, "ソイル2以上でNo Mercyを即時強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.Force2W, "ソイル2以上で次の可能なweave枠にNo Mercyを強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.Force2QW, "ソイル2以上で次の可能な最終weave枠にNo Mercyを強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.Force3, "ソイル3以上でNo Mercyを即時強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.Force3W, "ソイル3以上で次の可能なweave枠にNo Mercyを強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.Force3QW, "ソイル3以上で次の可能な最終weave枠にNo Mercyを強制使用", 60, 20, ActionTargets.Self, 2)
            .AddOption(NoMercyStrategy.Delay, "No Mercyを遅延")
            .AddOption(NoMercyStrategy.SynergyOptimized, "複数シナジー窓の実質威力が最大になるようNo Mercy開始を最大3GCDまで調整", supportedTargets: ActionTargets.Self)
            .AddAssociatedActions(AID.NoMercy);

        res.Define(Track.SonicBreak).As<SonicBreakStrategy>("SB", "Sonic Break設定", 195)
            .AddOption(SonicBreakStrategy.Automatic, "Sonic Breakを自動使用", supportedTargets: ActionTargets.Hostile, minLevel: 54)
            .AddOption(SonicBreakStrategy.Early, "No Mercy中の最初のGCDとしてSonic Breakを自動使用", supportedTargets: ActionTargets.Hostile, minLevel: 54)
            .AddOption(SonicBreakStrategy.Late, "No Mercy中の最後のGCDとしてSonic Breakを自動使用", supportedTargets: ActionTargets.Hostile, minLevel: 54)
            .AddOption(SonicBreakStrategy.Force, "Sonic Breakを即時強制使用", 0, 30, ActionTargets.Hostile, 54)
            .AddOption(SonicBreakStrategy.Delay, "Sonic Breakを遅延", minLevel: 54)
            .AddAssociatedActions(AID.SonicBreak);

        res.Define(Track.GnashingFang).As<GnashingStrategy>("GF", "Gnashing Fangコンボ設定", 196)
            .AddOption(GnashingStrategy.Automatic, "Gnashing Fangとコンボを自動使用", supportedTargets: ActionTargets.Hostile, minLevel: 60)
            .AddOption(GnashingStrategy.ForceGnash, "Gnashing Fangを即時強制使用", 0, 0, ActionTargets.Hostile, 60)
            .AddOption(GnashingStrategy.ForceGnash1, "ソイル1以上でGnashing Fangを即時強制使用", 30, 0, ActionTargets.Hostile, 60)
            .AddOption(GnashingStrategy.ForceGnash2, "ソイル2以上でGnashing Fangを即時強制使用", 30, 0, ActionTargets.Hostile, 60)
            .AddOption(GnashingStrategy.ForceGnash3, "ソイル3以上でGnashing Fangを即時強制使用", 30, 0, ActionTargets.Hostile, 60)
            .AddOption(GnashingStrategy.ForceClaw, "Savage Clawを即時強制使用", 0, 0, ActionTargets.Hostile, 60)
            .AddOption(GnashingStrategy.ForceTalon, "Wicked Talonを即時強制使用", 0, 0, ActionTargets.Hostile, 60)
            .AddOption(GnashingStrategy.Delay, "Gnashing Fangを遅延", 0, 0, ActionTargets.None, 60)
            .AddAssociatedActions(AID.GnashingFang, AID.SavageClaw, AID.WickedTalon);

        res.DefineOGCD(Track.BowShock, AID.BowShock, "BS", "Bow Shock", 194, 60, 15, ActionTargets.Self, 62).AddAssociatedActions(AID.BowShock);

        res.Define(Track.Continuation).As<ContinuationStrategy>("Cont.", "Continuation設定", 190)
            .AddOption(ContinuationStrategy.Automatic, "Continuation派生を自動使用", supportedTargets: ActionTargets.Hostile, minLevel: 70)
            .AddOption(ContinuationStrategy.Early, "Continuation派生をできるだけ早く使用", supportedTargets: ActionTargets.Hostile, minLevel: 70)
            .AddOption(ContinuationStrategy.Late, "Continuation派生をできるだけ遅く使用", supportedTargets: ActionTargets.Hostile, minLevel: 70)
            .AddAssociatedActions(AID.EyeGouge, AID.AbdomenTear, AID.JugularRip, AID.Hypervelocity, AID.FatedBrand);

        res.Define(Track.Bloodfest).As<BloodfestStrategy>("BF", "Bloodfest設定", 198)
            .AddOption(BloodfestStrategy.Automatic, "Bloodfestを自動使用", supportedTargets: ActionTargets.Hostile, minLevel: 80)
            .AddOption(BloodfestStrategy.Together, "No Mercyと合わせてBloodfestを自動使用。必要なら待機", supportedTargets: ActionTargets.Hostile, minLevel: 80)
            .AddOption(BloodfestStrategy.Force, "Bloodfestを即時強制使用", 60, 0, ActionTargets.Hostile, 80)
            .AddOption(BloodfestStrategy.ForceW, "次の可能なweave枠でBloodfestを強制使用", 60, 0, ActionTargets.Hostile, 80)
            .AddOption(BloodfestStrategy.Delay, "Bloodfestを遅延", minLevel: 80)
            .AddAssociatedActions(AID.Bloodfest);

        res.Define(Track.DoubleDown).As<DoubleDownStrategy>("DD", "Double Down設定", 196)
            .AddOption(DoubleDownStrategy.Automatic, "Double Downを自動使用", supportedTargets: ActionTargets.Self, minLevel: 90)
            .AddOption(DoubleDownStrategy.Force, "Double Downを即時強制使用", 60, 0, ActionTargets.Self, 90)
            .AddOption(DoubleDownStrategy.Force3, "ソイル3以上でDouble Downを即時強制使用", 60, 0, ActionTargets.Self, 90)
            .AddOption(DoubleDownStrategy.Delay, "Double Downを遅延", minLevel: 90)
            .AddAssociatedActions(AID.DoubleDown);

        res.Define(Track.Reign).As<ReignStrategy>("Reign", "Reign of Beastsコンボ設定", 194)
            .AddOption(ReignStrategy.Automatic, "Reign of Beastsとコンボを自動使用", supportedTargets: ActionTargets.Hostile, minLevel: 100)
            .AddOption(ReignStrategy.ForceReign, "Reign of Beastsを即時強制使用", supportedTargets: ActionTargets.Hostile, minLevel: 100)
            .AddOption(ReignStrategy.ForceNoble, "Noble Bloodを即時強制使用", supportedTargets: ActionTargets.Hostile, minLevel: 100)
            .AddOption(ReignStrategy.ForceLion, "Lion Heartを即時強制使用", supportedTargets: ActionTargets.Hostile, minLevel: 100)
            .AddOption(ReignStrategy.Delay, "Reign of Beastsを遅延", minLevel: 100)
            .AddAssociatedActions(AID.ReignOfBeasts, AID.NobleBlood, AID.LionHeart);

        res.Define(Track.FightEnd).As<FightEndStrategy>("FightEnd", "戦闘終了前リソース放出", 50)
            .AddOption(FightEndStrategy.Disabled, "戦闘終了予測によるリソース消費をしない")
            .AddOption(FightEndStrategy.SpendResources, "対象が倒れそうな時にBloodfest、ソイル、Gnashing Fang、Double Down、Reignを消費");

        res.Define(Track.RotationMode).As<RotationModeStrategy>("RotationMode", "ローテーションモード", 310)
            .AddOption(RotationModeStrategy.Full, "フルモード")
            .AddOption(RotationModeStrategy.NormalOvercapOnly, "通常回し+ソイル溢れ");
        res.DefineMechanicHints(Track.MechanicHints);

        return res;
    }

    #region Tunables / Constants

    // Game-rule durations and recast anchors. These mirror GNB/status rules and should not be tuned for scoring.
    private const float GameRuleNoMercyDuration = 20f;
    private const float GameRuleNoMercyCycle = 60f;
    private const float GameRuleBloodfestDuration = 30f;
    private const float GameRuleReadyToBreakDuration = 30f;
    private const float GameRuleReadyToReignDuration = 30f;
    private const float GameRuleBattleVoiceDuration = 15f;
    private const float GameRuleSearingLightDuration = 20f;
    private const float GameRuleGnashingFangChargeTime = 30f;
    private const float GameRuleZoneCooldown = 30f;
    private const float GameRuleBowShockCooldown = 60f;
    private const int GameRuleBaseMaxCartridgesHigh = 3;
    private const int GameRuleBaseMaxCartridgesLow = 2;
    private const int GameRuleBloodfestMaxCartridgesHigh = 6;
    private const int GameRuleBloodfestMaxCartridgesLow = 4;
    private const int GameRuleBloodfestCartridgesGranted = 3;
    private const int GameRuleMaxGFCharges = 2;
    private const int GameRuleNoCartridges = 0;
    private const int GameRuleDoubleDownCartridgeCost = 2;
    private const float GamePotencyKeenEdge = 300f;
    private const float GamePotencyBrutalShell = 380f;
    private const float GamePotencySolidBarrel = 460f;
    private const float GamePotencyBurstStrike = 420f;
    private const float GamePotencyDemonSlice = 100f;
    private const float GamePotencyDemonSlaughter = 160f;
    private const float GamePotencyFatedCircle = 320f;
    private const float GamePotencyGnashingFang = 440f;
    private const float GamePotencySavageClaw = 500f;
    private const float GamePotencyWickedTalon = 560f;
    private const float GamePotencyDoubleDown = 1000f;
    private const float GamePotencySonicBreak = 940f;
    private const float GamePotencyReignOfBeasts = 800f;
    private const float GamePotencyNobleBlood = 900f;
    private const float GamePotencyLionHeart = 1000f;
    private const float GamePotencyBlastingZone = 800f;
    private const float GamePotencyDangerZone = 250f;
    private const float GamePotencyBowShock = 450f;
    private const float GamePotencyJugularRip = 240f;
    private const float GamePotencyAbdomenTear = 280f;
    private const float GamePotencyEyeGouge = 320f;
    private const float GamePotencyHypervelocity = 200f;
    private const float GamePotencyFatedBrand = 120f;

    // Timing slack and weave windows. These are execution tolerances, not potency tuning.
    private const float TimingNoMercyFirstWeaveMinGCD = 0.85f;
    private const float TimingFastGCDThreshold = 2.5f;
    private const float TimingWeaveSlotLead = 0.65f;
    private const float TimingLateWeaveReserve = 0.85f;
    private const float TimingReadyEpsilon = 0.1f;
    private const float TimingPotionDelayBeforeGCD = 0.9f;
    private const float TimingContinuationWeaveMinGCD = 0.6f;
    private const float TimingContinuationLateMaxGCD = 1.25f;
    private const float TimingContinuationNoMercyClearance = 1f;
    // hold the GCD for a pending Continuation when the ability lock (Amnesia) ends within this many seconds; the proc is lost otherwise
    private const float TimingLockedContinuationHoldMax = 1f;
    private const float TimingTargetWindowPastPadding = 2f;
    private const float TimingTargetWindowFuturePadding = 2f;
    private const float TimingTargetWindowActiveBeforeGCDScale = 0.5f;
    private const float TimingTargetWindowActiveAfter = 0.2f;
    private const float TimingLearnedWindowMinDuration = 1f;
    private const float TimingLearnedWindowLookback = 2f;
    private const float TimingLearnedWindowLookahead = 65f;
    // a repeat of a learned downtime counts as the same window when it starts within this many seconds of the old one
    private const float TimingLearnedWindowMatchTolerance = 2.5f;
    private const float TimingCurrentAddHintGCDs = 2f;
    private const float TimingComboUnsafeRemaining = 0.5f;
    private const float TimingFightEstimateMinSample = 2f;
    private const float TimingFightEstimateBlendOld = 0.7f;
    private const float TimingFightEstimateBlendNew = 0.3f;
    private const float TimingFightEstimateMinDrain = 0.0001f;
    private const float TimingFightEndBurnWindow = 20f;
    private const float TimingOpenerEnd = 30f;
    private const float TimingDeathRecoveryFirstRelevant = 50f;
    private const float TimingBurstScheduleNudge = 0.01f;
    private const float TimingFightEndLossBuffer = 2f;
    private const float TimingFightEndSlotBuffer = 0.25f;
    private const float TimingLateBurstGFHoldMaxCooldown = 0.6f;
    private const float TimingBloodfestOvercapStatusLeft = 20f;
    private const float TimingSonicBreakLateStatusLeft = 12.500f;
    private const float TimingReignEmergencyStatusLeft = 2.5f;
    private const float TimingNoMercyLateGougeReserve = 17f;
    private const float TimingOpeningDelayedSolidNMReadyGCDs = 2.5f;
    private const float TimingOpeningDelayedSolidBFReadyGCDs = 1.5f;
    private const float TimingFullModeResumeGCDs = 4f;
    private const float TimingNormalGFAfterComboSafetyGCDs = 4f;
    private const float TimingGFOvercapSoonGCDs = 1.5f;
    private const float TimingRaidBuffLookaheadGCDs = 4f;
    private const float TimingGnashingFangBurstReadyCooldown = 32f;
    private const float TimingDoubleDownBurstReadyCooldown = 4f;
    private const float TimingBloodfestTogetherCooldown = 45f;
    private const float TimingZoneNoMercyAlignCooldown = 15f;
    private const float TimingZoneNoMercyAlignMaxWait = 15f;

    // Planner horizon, slot positions, and beam-search dimensions.
    private const int Planner74HorizonSlots = 24;
    private const int Planner74BeamWidth = 48;
    private const int Planner74FarSlot = 9999;
    private const int Planner74NoMercySlots = 8;
    private const int Planner74NoMercyFastSlots = 9;
    private const float Planner74NoMercyReliableFastGCD = 2.47f;
    private const int Planner74BloodfestSlots = 12;
    private const int Planner74Cooldown60Slots = 24;
    private const int Planner74SynergyMaxDelaySlots = 3;
    private const float Planner74SynergyScoreScale = 10f;
    private const int Planner74PreNMGFOffsetSlots = 3;
    private const int Planner74BloodfestWeaveOffsetSlots = 2;
    private const int Planner74NoMercyWeaveOffsetSlots = 1;
    private const int Planner74ComboTimerSlots = 12;
    private const int Planner74NodeInitialMultiplier = 8;
    private const int Planner74NextNodeInitialMultiplier = 10;
    private const int Planner74CandidateInitialCapacity = 10;
    private const float Planner74NoNodeScore = -999999f;
    private const float Planner74SynergySlotSampleOffset = 0.35f;
    private const int Planner74DebugSlotDigits = 2;
    private const bool Planner74DebugEnabled = false;
    private const int Planner74BurstOrderDDDelaySlots = 2;

    // Planner scoring weights. These are intentionally separated from real potency constants.
    private const float ScoreNoMercyMultiplier = 1.2f;
    private const float ScoreSynergyStandardBuff = 0.05f;
    private const float ScoreSynergyDivination = 0.06f;
    private const float ScoreSynergyBattleLitany = 0.025f;
    private const float ScoreSynergyArcaneCircle = 0.03f;
    private const float ScorePreNMGFBase = 4500f;
    private const float PenaltyPreNMGFDistance = 900f;
    private const float ScorePreNMSavageClaw = 1200f;
    private const float ScorePreNMWickedTalon = 1800f;
    private const float ScoreBuffedDoubleDown = 3500f;
    private const float ScoreBuffedReignCombo = 2800f;
    private const float ScoreBuffedSonicBreak = 1800f;
    private const float ScoreBuffedGnashingFang = 1200f;
    private const float ScoreAutoBloodfestWeave = 3000f;
    private const float ScoreAutoNoMercyWeave = 6000f;
    private const float ScoreNoMercyContinuationAnchor = 1500f;
    private const float ScoreBalanceEarlyReign = 5600f;
    private const float ScoreBalanceDDBeforeReignContinuation = 5200f;
    private const float ScoreBalanceSonicBeforeReignContinuation = 4400f;
    private const float ScoreBalanceNobleBeforeDDOrSonic = 7600f;
    private const float ScoreBalanceLateNoble = 4200f;
    private const float ScoreBalanceLionHeart = 7600f;
    private const float ScoreBalanceGnashingAfterReign = 2400f;
    private const float ScoreEffectiveBurstStrikeWithContinuation = 600f;
    private const float ScoreEffectiveFatedCircleWithContinuation = 520f;
    private const float ScoreEffectiveGnashingFangWithContinuation = 680f;
    private const float ScoreEffectiveSavageClawWithContinuation = 780f;
    private const float ScoreEffectiveWickedTalonWithContinuation = 880f;
    private const float PenaltyUnbuffedBurstGCDNearNM = 7000f;
    private const float PenaltyBrokenKeenCombo = 800f;
    private const float PenaltyBrokenBrutalCombo = 2500f;
    private const float PenaltyBrokenSolidCombo = 3000f;
    private const float PenaltyCartridgeOvercap = 2500f;
    private const float PenaltyMissingCartridgeSpend = 5000f;
    private const float PenaltyInvalidGFStart = 8000f;
    private const float PenaltyInvalidGaugeCombo = 10000f;
    private const float PenaltyInvalidNoMercyWeave = 30000f;
    private const float PenaltyMissedPreNMGF = 3500f;
    private const float PenaltyGFChargeOvercapBeforeNM = 600f;
    private const float PenaltyUnspentDoubleDownInNM = 900f;
    private const float PenaltyUnfinishedReignComboInNM = 4500f;
    private const float PenaltyOvercapBurstStrikeNeeded = 700f;
    private const float PenaltyComboDropped = 2500f;
    private const float PenaltyBloodfestAmmoTrim = 1500f;
    private const float PenaltyNoMercyDelayPerSlot = 220f;
    private const float PenaltyNoMercyLostToFightEnd = 50000f;

    // AOE and target-count thresholds.
    private const float AOEMeleeRadius = 5f;
    private const float AOESplashRadius = 3.5f;
    private const int AOEFatedCircleTargetThreshold = 2;
    private const int AOENormalHighLevelTargetThreshold = 3;
    private const int AOENormalLowLevelTargetThreshold = 2;
    private const int AOEGoalTargetFallback = 2;
    private const float AOEGoalMaxActionRange = 20f;

    // Potion timing gates. Potion permission remains Bloodfest-based; these only classify windows.
    private const float PotionBloodfestWindowTolerance = 10f;
    private const float PotionEvenMinuteEarliest = 110f;
    private const float PotionEvenMinuteCycle = 120f;
    private const float PotionTwoMinuteTarget = 120f;
    private const float PotionSixMinuteTarget = 360f;
    private const float PotionEightMinuteTarget = 480f;
    private const float PotionPostBurstMinNextNoMercy = 25f;
    private const float PotionPostBurstMinStatusLeft = 0.6f;

    // Hint-bus and timeline capacities/priorities for targetable window prediction.
    private const int HintLearnedWindowCapacity = 64;
    // a window is only trusted as a prediction once a later pull reproduced it; misses walk the count back down
    private const int HintLearnedWindowTrustCount = 1;
    private const int HintLearnedWindowMaxConfirmations = 3;
    private const int HintPriorityBossPlannerLearned = 80;
    private const int HintPriorityBossPlannerFallback = 90;
    private const int HintPriorityBossTimelineDowntime = 130;
    private const int HintPriorityBossTimelineUptime = 131;
    private const int HintPriorityCurrentAddTarget = 132;
    private const int HintSynergyWindowInitialCapacity = 16;

    #endregion

    private float NMstatus;
    private float SBstatus;
    private float Rstatus;
    private int NumSplashTargets;
    private Enemy? BestSplashTargets;
    private Enemy? BestSplashTarget;
    private Enemy? BestDOTTarget;
    private bool ForceAOE;
    private bool WantAOE;
    private bool WantNormalAOE;
    private bool WantFatedCircleAOE;
    private bool UsedGFComboInNM;
    private bool OpeningBurstGFUsedInNM;
    private bool WasNoMercyActive;
    private bool PostNoMercyGFDumpPending;
    private bool WasDead;
    private bool AlignBurstAfterDeath;
    private bool DeathStartedWithNM;
    private float DeadSince;
    private float DelayedBurstAt;
    private float EstimatedFightEnd = float.MaxValue;
    private float PendingHPRatio;
    private float PendingHPTime;
    private float EstimatedHPDrain;
    private ulong FightEstimateTargetID;
    private static readonly System.Collections.Generic.List<Planner74LearnedTargetWindow> Planner74LearnedTargetLossWindows = new(HintLearnedWindowCapacity);
    private static ushort Planner74LearnedZoneID;
    private static bool Planner74LearnedPullActive;
    private bool Planner74PrevTargetable = true;
    private float Planner74TargetLossStartedAt = -1f;
    private uint Planner74LearningBossOID;
    private Planner74Result Planner74LastPlan;
    private Planner74Input Planner74LastPlanInput;
    private bool Planner74LastPlanValid;
    // per-frame scratch for the planner inputs: the inputs are rebuilt every frame in combat, but an array is only allocated
    // when its contents differ from the cached plan's input (identical contents reuse that array, so nothing changes downstream)
    private readonly bool[] Planner74TargetableScratch = new bool[Planner74HorizonSlots];
    private readonly System.Collections.Generic.List<AkechiGNBPlanner74TargetWindow> Planner74HintScratch = new(64);
    private readonly System.Collections.Generic.List<Planner74SynergyWindow> Planner74SynergyScratch = new(HintSynergyWindowInitialCapacity);
    private readonly System.Collections.Generic.List<DamageCooldownSnapshot> Planner74DamageCooldownScratch = [];
    private float ReservedBurstNoMercyAt;
    private float NoMercyReadySince = -1f;
    private RotationModeStrategy LastRotationMode = RotationModeStrategy.Full;
    private float FullModeBurstResumeUntil;

    private int GFCharges => Charges(AID.GnashingFang);
    private bool HasGF1 => GFCharges >= 1;

    private GunbreakerGauge Gauge => World.Client.GetGauge<GunbreakerGauge>();
    private byte Ammo => Gauge.Ammo; //cartridges
    private byte GunComboStep => Gauge.AmmoComboStep; //Gauge combo - GF & Reign
    private float NMcd => Cooldown(AID.NoMercy);
    private float BFcd => Cooldown(AID.Bloodfest);
    private bool HasNM => NMcd is >= 40f and <= GameRuleNoMercyCycle;
    private bool HasBF => HasStatus(SID.Bloodfest);
    private bool HasReign => HasStatus(SID.ReadyToReign);
    private bool HasBlast => Unlocked(AID.Hypervelocity) && HasStatus(SID.ReadyToBlast) && !LastActionUsed(AID.Hypervelocity);
    private bool HasRaze => Unlocked(AID.FatedBrand) && HasStatus(SID.ReadyToRaze) && !LastActionUsed(AID.FatedBrand);
    private bool HasRip => Unlocked(AID.JugularRip) && HasStatus(SID.ReadyToRip) && !LastActionUsed(AID.JugularRip);
    private bool HasTear => Unlocked(AID.AbdomenTear) && HasStatus(SID.ReadyToTear) && !LastActionUsed(AID.AbdomenTear);
    private bool HasGouge => Unlocked(AID.EyeGouge) && HasStatus(SID.ReadyToGouge) && !LastActionUsed(AID.EyeGouge);
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
    private bool CanNoMercyFirstWeaveIn => GCD >= TimingNoMercyFirstWeaveMinGCD;

    #region Action category locks
    // ActionLocks (shared with the other modules), read once per Execution. Status sheet, category-2 debuffs: Pacification(6) "Unable to
    // use weaponskills.", Silence(7) "A stifling magic is preventing casts.", Amnesia(1092) "Unable to use abilities.". The client refuses
    // such a pick only after the queue has chosen it and the frame is spent (ActionManagerEx retries next frame), so a refused entry that
    // outranks the GCD stalls the GCD for the whole lock: Continuation is raised above every GCD once GCD <= 0.5s, and under Amnesia Eye
    // Gouge was refused 133 frames in a row while the GCD idled 5.75s inside No Mercy. Entries of a locked category are dropped before
    // the queue sees them, and nothing withholds the GCD for an ability that cannot be pressed before the GCD rolls off. Only the three
    // category locks' remaining seconds are used here (CategoryLeft); stun-type statuses that refuse everything are left to the client.
    private ActionLockState Locks;
    private Predicate<ActionQueue.Entry>? LockedQueuedEntry;

    // the lock ends early enough for an ability (No Mercy, Continuation) / a weaponskill to still fit before the GCD rolls off
    private bool AbilityUsableBeforeGCD => Locks.AbilityLeft <= MathF.Max(0f, GCD - TimingReadyEpsilon);
    private bool WeaponskillUsableBeforeGCD => Locks.WeaponskillLeft <= MathF.Max(0f, GCD - TimingReadyEpsilon);

    private static bool IsContinuationAction(ActionID action) => action.Type == ActionType.Spell
        && (AID)action.ID is AID.JugularRip or AID.AbdomenTear or AID.EyeGouge or AID.Hypervelocity or AID.FatedBrand;

    private void DropLockedQueuedActions()
    {
        if (Locks.WeaponskillLeft <= 0f && Locks.SpellLeft <= 0f && Locks.AbilityLeft <= 0f)
            return;
        // a Continuation proc dies with the next weaponskill, so when the ability lock is about to end it is worth holding the GCD for it
        LockedQueuedEntry ??= entry => ActionDefinitions.Instance[entry.Action] is { } definition
            && Locks.CategoryLeft(definition.Category) > (IsContinuationAction(entry.Action) ? TimingLockedContinuationHoldMax : 0f);
        Hints.ActionsToExecute.Entries.RemoveAll(LockedQueuedEntry);
    }
    #endregion

    private bool Slow => SkSGCDLength >= TimingFastGCDThreshold;
    private bool Fast => SkSGCDLength < TimingFastGCDThreshold;
    private bool HasDeathDelayedBurst => AlignBurstAfterDeath && DelayedBurstAt > 0f && Player.InCombat;
    // Normal 1-2-3 / AoE combo: abandon continuation when >= 29.5s has elapsed since the previous combo step.
    private bool UnsafeNormalComboContinue => ActualComboTimer is > 0 and <= TimingComboUnsafeRemaining;
    private bool NoMercyAtLeast1GCDElapsed => HasNM && NMstatus <= MathF.Max(0f, GameRuleNoMercyDuration - SkSGCDLength);
    private bool NoMercyAtLeast2GCDsElapsed => HasNM && NMstatus <= MathF.Max(0f, GameRuleNoMercyDuration - SkSGCDLength * Planner74BurstOrderDDDelaySlots);
    private int MaxCartridges
            => Unlocked(TraitID.CartridgeChargeII) ? HasStatus(SID.Bloodfest) ? GameRuleBloodfestMaxCartridgesHigh : GameRuleBaseMaxCartridgesHigh
            : Unlocked(TraitID.CartridgeCharge) ? HasStatus(SID.Bloodfest) ? GameRuleBloodfestMaxCartridgesLow : GameRuleBaseMaxCartridgesLow
            : GameRuleNoCartridges;

    private AID ContinueST(AID last, bool overcap) => last switch
    {
        AID.BrutalShell =>
            UnsafeNormalComboContinue ? (overcap && Ammo == MaxCartridges && Unlocked(AID.BurstStrike) ? AID.BurstStrike : AID.KeenEdge)
            : overcap && Ammo == MaxCartridges && Unlocked(AID.BurstStrike) ? AID.BurstStrike
            : Unlocked(AID.SolidBarrel) ? AID.SolidBarrel
            : AID.KeenEdge,

        AID.KeenEdge => UnsafeNormalComboContinue ? AID.KeenEdge : Unlocked(AID.BrutalShell) ? AID.BrutalShell : AID.KeenEdge,
        _ => AID.KeenEdge,
    };
    private AID ContinueAOE(AID last, bool overcap)
        => overcap && MaxCartridges > 0 && Ammo == MaxCartridges && Unlocked(AID.BurstStrike) ? (Unlocked(AID.FatedCircle) ? AID.FatedCircle : AID.BurstStrike)
            : last == AID.DemonSlice && !UnsafeNormalComboContinue ? (Unlocked(AID.DemonSlaughter) ? AID.DemonSlaughter : Unlocked(AID.DemonSlice) ? AID.DemonSlice : AID.KeenEdge)
            : Unlocked(AID.DemonSlice) ? AID.DemonSlice : AID.KeenEdge;

    private AID ContinueSTNoOvercap(AID last) => ContinueST(last, overcap: false);
    private AID ContinueSTWithOvercap(AID last) => ContinueST(last, overcap: true);
    private AID ContinueAOENoOvercap(AID last) => ContinueAOE(last, overcap: false);
    private AID ContinueAOEWithOvercap(AID last) => ContinueAOE(last, overcap: true);

    private AID Finish(bool overcap,
        Func<AID, AID> with,
        Func<AID, AID> without)
    {
        var gnash = ComboLastMove switch
        {
            AID.GnashingFang when Unlocked(AID.SavageClaw) => AID.SavageClaw,
            AID.SavageClaw when Unlocked(AID.WickedTalon) => AID.WickedTalon,
            AID.ReignOfBeasts when Unlocked(AID.NobleBlood) => AID.NobleBlood,
            AID.NobleBlood when Unlocked(AID.LionHeart) => AID.LionHeart,
            _ => AID.None,
        };
        return gnash != AID.None ? gnash //finish Gauge combos first
            : overcap ? with(ComboLastMove) //finish combo with overcap protection
            : without(ComboLastMove); //just finish combo
    }
    private AID STFinish(bool overcap) => Finish(overcap, ContinueSTWithOvercap, ContinueSTNoOvercap);
    private AID AOEFinish(bool overcap) => Finish(overcap, ContinueAOEWithOvercap, ContinueAOENoOvercap);

    //we dont care about finishing combos with these methods, so we just send it
    private AID STBreak(bool overcap) => overcap ? ContinueSTWithOvercap(ComboLastMove) : ContinueSTNoOvercap(ComboLastMove);
    private AID AOEBreak(bool overcap) => overcap ? ContinueAOEWithOvercap(ComboLastMove) : ContinueAOENoOvercap(ComboLastMove);

    private int NormalAOEThreshold() => Unlocked(AID.FatedCircle) ? AOENormalHighLevelTargetThreshold : AOENormalLowLevelTargetThreshold;
    private bool AnyTargetIn5y(Actor? fallbackTarget) => TargetsInAOECircle(AOEMeleeRadius, 1) || In5y(fallbackTarget);
    private int CountPriorityTargetsInAOECircle(float radius)
    {
        var count = 0;
        foreach (var target in Hints.PriorityTargetsSpan)
        {
            var actor = target.Actor;
            if (actor == null || actor.IsDeadOrDestroyed)
                continue;

            if (Player.DistanceToHitbox(actor) <= radius)
                ++count;
        }

        return count;
    }

    private enum Planner74GCD
    {
        None,
        KeenEdge,
        BrutalShell,
        SolidBarrel,
        BurstStrike,
        DemonSlice,
        DemonSlaughter,
        FatedCircle,
        GnashingFang,
        SavageClaw,
        WickedTalon,
        DoubleDown,
        SonicBreak,
        ReignOfBeasts,
        NobleBlood,
        LionHeart,
    }

    private struct Planner74LearnedTargetWindow(ushort zoneID, uint bossOID, float start, float end, string reason)
    {
        public ushort ZoneID = zoneID;
        public uint BossOID = bossOID;
        public float Start = start;
        public float End = end;
        public string Reason = reason ?? "";
        // how many later pulls reproduced this downtime at the same combat time; below HintLearnedWindowTrustCount
        // the window is still being verified and is not fed to the planner
        public int Confirmations = 0;
        // per-pull verification state, cleared when combat starts
        public bool SeenThisPull = false;
        public bool CheckedThisPull = false;
    }

    private readonly struct Planner74SynergyWindow(float startSlot, float endSlot, float weight)
    {
        public readonly float StartSlot = startSlot;
        public readonly float EndSlot = endSlot;
        public readonly float Weight = weight;

        public bool Covers(int slot) => slot + Planner74SynergySlotSampleOffset >= StartSlot && slot + Planner74SynergySlotSampleOffset <= EndSlot;
    }

    private struct Planner74Input
    {
        public int ComboStep;
        public int AOEComboStep;
        public int ComboTimerSlotsLeft;
        public int GFStep;
        public int ReignStep;
        public int Ammo;
        public int MaxAmmo;
        public int GFCharges;
        public int GFNextChargeSlot;
        public int GFChargeSlots;
        public int NoMercyReadyWeaveSlot;
        public int BloodfestReadyWeaveSlot;
        public int DoubleDownReadyGCDSlot;
        public int SonicBreakReadyGCDSlot;
        public int ZoneReadyWeaveSlot;
        public int BowShockReadyWeaveSlot;
        public float ZonePotency;
        public float BowShockPotency;
        public bool ReignReady;
        public int NoMercySlotsLeft;
        public int NoMercyDurationSlots;
        public int BloodfestSlotsLeft;
        public int TargetsIn5y;
        public int NormalAOEThreshold;
        public bool NoMercyUnlocked;
        public bool BloodfestUnlocked;
        public bool BurstStrikeUnlocked;
        public bool DemonSliceUnlocked;
        public bool GnashingFangUnlocked;
        public bool SonicBreakUnlocked;
        public bool DoubleDownUnlocked;
        public bool ReignUnlocked;
        public bool DemonSlaughterUnlocked;
        public bool FatedCircleUnlocked;
        public int NextNoMercyFirstBuffedSlot;
        public bool OpeningDelayedSolidBurst;
        public bool OpeningBurstGFUsed;
        public bool SmoothFullModeBurstEntry;
        public bool[]? TargetableBySlot;
        public bool OptimizeRaidSynergy;
        public Planner74SynergyWindow[]? SynergyWindows;
        public float CombatTime;
        public float GCDLength;
        public float EstimatedFightEnd;
        // GCD slots whose weave window still lies inside an ability lock (Amnesia): no No Mercy / Bloodfest / Zone / Bow Shock weave
        // there, and a proc-granting GCD placed there loses its Continuation (the proc dies with the next weaponskill)
        public int AbilityLockedSlots;
    }

    private struct Planner74Decision
    {
        public Planner74GCD GCD;
        public bool WeaveBloodfest;
        public bool WeaveNoMercy;
        public bool WeaveZone;
        public bool WeaveBowShock;
        public bool ForbidZoneBowAfterThisGCD;
        public bool PreferContinuationAfterNoMercy;
        public float Score;
    }

    private struct Planner74Result
    {
        public Planner74Decision First;
        public Planner74Decision[] Plan;
        public float Score;
        public string Debug;
    }

    private struct Planner74State
    {
        public int ComboStep;
        public int AOEComboStep;
        public int ComboTimerSlotsLeft;
        public int GFStep;
        public int ReignStep;
        public int Ammo;
        public int MaxAmmo;
        public int GFCharges;
        public int GFNextChargeSlot;
        public int GFChargeSlots;
        public int NoMercyReadyWeaveSlot;
        public int BloodfestReadyWeaveSlot;
        public int DoubleDownReadyGCDSlot;
        public int SonicBreakReadyGCDSlot;
        public int ZoneReadyWeaveSlot;
        public int BowShockReadyWeaveSlot;
        public float ZonePotency;
        public float BowShockPotency;
        public bool ReignReady;
        public int NoMercySlotsLeft;
        public int NoMercyDurationSlots;
        public int BloodfestSlotsLeft;
        public bool OpeningDelayedSolidBurst;
        public bool OpeningDelayedSolidGFComplete;
        public bool OpeningBurstGFUsed;
    }

    private struct Planner74Node
    {
        public Planner74State State;
        public int Parent; // index in the previous slot's node list, -1 for the root
        public Planner74Decision Decision;
        public float Score;
    }

    private uint Planner74GetBossOID(Actor? mainTarget) => mainTarget?.OID ?? 0u;
    private bool Planner74CurrentTargetable(Actor? mainTarget) => mainTarget != null && !mainTarget.IsDeadOrDestroyed && mainTarget.IsTargetable;

    private void Planner74UpdateTargetableLearning(Actor? mainTarget)
    {
        if (Planner74LearnedZoneID != World.CurrentZone)
        {
            Planner74LearnedZoneID = World.CurrentZone;
            Planner74LearnedTargetLossWindows.Clear();
        }

        if (!Player.InCombat)
        {
            Planner74PrevTargetable = true;
            Planner74TargetLossStartedAt = -TimingLearnedWindowMinDuration;
            Planner74LearningBossOID = 0;
            Planner74LearnedPullActive = false;
            return;
        }

        // a new pull starts every learned window over as "not yet seen this time"
        if (!Planner74LearnedPullActive)
        {
            Planner74LearnedPullActive = true;
            for (var i = 0; i < Planner74LearnedTargetLossWindows.Count; ++i)
            {
                var w = Planner74LearnedTargetLossWindows[i];
                w.SeenThisPull = false;
                w.CheckedThisPull = false;
                Planner74LearnedTargetLossWindows[i] = w;
            }
        }

        var bossOID = Planner74GetBossOID(mainTarget);
        var targetable = Planner74CurrentTargetable(mainTarget);

        if (Planner74LearningBossOID == 0 && bossOID != 0)
            Planner74LearningBossOID = bossOID;

        if (Planner74PrevTargetable && !targetable)
        {
            Planner74TargetLossStartedAt = CombatTimer;
            // mark on the loss STARTING, not on it ending: a downtime longer than the match tolerance would
            // otherwise be judged a miss while it is still running
            Planner74MarkLearnedWindowSeen(CombatTimer);
        }

        if (!Planner74PrevTargetable && targetable)
        {
            if (Planner74TargetLossStartedAt >= 0f)
            {
                var start = Planner74TargetLossStartedAt;
                var end = CombatTimer;
                if (end - start >= TimingLearnedWindowMinDuration && Planner74LearningBossOID != 0)
                    Planner74RecordLearnedWindow(start, end);
            }
            Planner74TargetLossStartedAt = -TimingLearnedWindowMinDuration;
        }

        Planner74ExpireMissedLearnedWindows();
        Planner74PrevTargetable = targetable;
    }

    // A target loss beginning at this combat time is the evidence a stored window was waiting for, so record it
    // before the expiry pass can judge the window missing.
    private void Planner74MarkLearnedWindowSeen(float start)
    {
        if (Planner74LearningBossOID == 0)
            return;

        for (var i = 0; i < Planner74LearnedTargetLossWindows.Count; ++i)
        {
            var w = Planner74LearnedTargetLossWindows[i];
            if (w.ZoneID != World.CurrentZone || w.BossOID != Planner74LearningBossOID || MathF.Abs(w.Start - start) > TimingLearnedWindowMatchTolerance)
                continue;
            // the end is still unknown here, so this only suspends the expiry pass; the record step re-checks the
            // full shape and leaves a window whose length disagrees to expire on the next pull
            w.SeenThisPull = true;
            Planner74LearnedTargetLossWindows[i] = w;
        }
    }

    // Two downtimes are the same event only when both ends line up. Matching on the start alone let a 2s loss
    // confirm a 12s window that merely began at the same time, and the rotation then held for the longer one.
    private bool Planner74LearnedWindowMatches(in Planner74LearnedTargetWindow w, float start, float end)
        => w.ZoneID == World.CurrentZone && w.BossOID == Planner74LearningBossOID
            && MathF.Abs(w.Start - start) <= TimingLearnedWindowMatchTolerance && MathF.Abs(w.End - end) <= TimingLearnedWindowMatchTolerance;

    // A downtime observed at the same combat time as a stored window confirms it; a brand new one starts unverified,
    // so the very first pull of a fight never feeds a prediction it has no evidence for.
    private void Planner74RecordLearnedWindow(float start, float end)
    {
        for (var i = 0; i < Planner74LearnedTargetLossWindows.Count; ++i)
        {
            var w = Planner74LearnedTargetLossWindows[i];
            if (!Planner74LearnedWindowMatches(w, start, end))
                continue;
            w.Confirmations = Math.Min(w.Confirmations + 1, HintLearnedWindowMaxConfirmations);
            // converge on what was just observed, both ends. Keeping the older start made the rotation begin holding
            // up to a tolerance early, which costs a GCD when the downtime it is waiting for is only a couple seconds.
            w.Start = start;
            w.End = end;
            w.SeenThisPull = true;
            w.CheckedThisPull = true;
            Planner74LearnedTargetLossWindows[i] = w;
            return;
        }

        Planner74LearnedTargetLossWindows.Add(new(World.CurrentZone, Planner74LearningBossOID, start, end, "Learned target loss") { SeenThisPull = true, CheckedThisPull = true });
        if (Planner74LearnedTargetLossWindows.Count > HintLearnedWindowCapacity)
            Planner74LearnedTargetLossWindows.RemoveAt(0);
    }

    // Once this pull is past a stored window's start and no downtime happened there, the window mispredicted: walk
    // its confirmation count back down and drop it entirely when it runs out, so a stale time stops costing GCDs.
    private void Planner74ExpireMissedLearnedWindows()
    {
        if (Planner74LearningBossOID == 0)
            return;

        for (var i = Planner74LearnedTargetLossWindows.Count - 1; i >= 0; --i)
        {
            var w = Planner74LearnedTargetLossWindows[i];
            if (w.SeenThisPull || w.CheckedThisPull || w.ZoneID != World.CurrentZone || w.BossOID != Planner74LearningBossOID)
                continue;
            if (CombatTimer <= w.Start + TimingLearnedWindowMatchTolerance)
                continue;

            w.CheckedThisPull = true;
            if (--w.Confirmations < 0)
                Planner74LearnedTargetLossWindows.RemoveAt(i);
            else
                Planner74LearnedTargetLossWindows[i] = w;
        }
    }

    private void Planner74FeedLearnedTimelineHints(Actor? mainTarget)
    {
        if (!Player.InCombat)
            return;

        // match the id the windows were stored under, not the momentary main target (an add during downtime would drop every learned window)
        var bossOID = Planner74LearningBossOID != 0 ? Planner74LearningBossOID : Planner74GetBossOID(mainTarget);
        if (bossOID == 0)
            return;

        for (var i = 0; i < Planner74LearnedTargetLossWindows.Count; ++i)
        {
            var w = Planner74LearnedTargetLossWindows[i];
            if (w.ZoneID != World.CurrentZone || w.BossOID != bossOID)
                continue;
            // still being verified: one sighting is not evidence the fight repeats it
            if (w.Confirmations < HintLearnedWindowTrustCount)
                continue;
            if (w.End < CombatTimer - TimingLearnedWindowLookback || w.Start > CombatTimer + TimingLearnedWindowLookahead)
                continue;

            AkechiGNBPlanner74HintBus.PushWindowAbsolute(w.Start, w.End, targetable: false, AkechiGNBPlanner74HintSource.BossPlanner, w.Reason, priority: HintPriorityBossPlannerLearned);
        }
    }

    // the predicted target loss (MechanicForecast) as targetable windows with their real length, above the boss timeline's own windows
    private void Planner74FeedMechanicForecastHints()
    {
        AkechiGNBPlanner74HintBus.ClearSource(AkechiGNBPlanner74HintSource.MechanicForecast);
        // a loss without a known return is not fed: the planner would treat the whole horizon as downtime and defer every burst (spec rule 1)
        if (!Player.InCombat || !Mechanic.Enabled || Mechanic.DowntimeNow || !Mechanic.ReturnKnown || SkSGCDLength <= 0f)
            return;
        var horizon = SkSGCDLength * Planner74HorizonSlots + TimingTargetWindowFuturePadding;
        if (Mechanic.TargetLossIn > horizon)
            return;
        var returnAt = MathF.Min(horizon, Mechanic.TargetReturnIn);
        AkechiGNBPlanner74HintBus.PushWindowAbsolute(CombatTimer + Mechanic.TargetLossIn, CombatTimer + returnAt, targetable: false,
            AkechiGNBPlanner74HintSource.MechanicForecast, "Mechanic forecast: target loss", priority: HintPriorityBossTimelineDowntime + 10);
        if (Mechanic.ReturnKnown && Mechanic.TargetReturnIn < horizon)
            AkechiGNBPlanner74HintBus.PushWindowAbsolute(CombatTimer + Mechanic.TargetReturnIn, CombatTimer + horizon, targetable: true,
                AkechiGNBPlanner74HintSource.MechanicForecast, "Mechanic forecast: target return", priority: HintPriorityBossTimelineDowntime + 10);
    }

    // Spec rule 2b: before a long target loss, the last weave slots go to the strongest oGCDs whose recast is back by the return.
    // The GCD side is left to the 7.4 planner, which already sees the loss window through the mechanic forecast hints.
    private void QueueMechanicWindDown(StrategyValues strategy, Actor? mainTarget)
    {
        if (!WindDown.Active(Mechanic, SkSGCDLength) || mainTarget == null)
            return;

        float P(AID aid) => WindDownPotency.Of(WindDownPotency.GNB, (uint)aid);
        var ogcds = new WindDownCandidate[2];
        var m = 0;
        var zone = Unlocked(AID.BlastingZone) ? AID.BlastingZone : AID.DangerZone;
        if (Unlocked(zone) && strategy.Option(Track.Zone).As<OGCDStrategy>() != OGCDStrategy.Delay)
            ogcds[m++] = new(ActionID.MakeSpell(zone), [P(AID.BlastingZone)], ReadyIn(zone), RecastRecoveredIn(zone));
        if (Unlocked(AID.BowShock) && strategy.Option(Track.BowShock).As<OGCDStrategy>() != OGCDStrategy.Delay)
            ogcds[m++] = new(ActionID.MakeSpell(AID.BowShock), [P(AID.BowShock)], ReadyIn(AID.BowShock), RecastRecoveredIn(AID.BowShock));
        var op = WindDown.SelectOgcd(ogcds.AsSpan(0, m), Mechanic, SkSGCDLength);
        if (op >= 0)
            QueueOGCD((AID)ogcds[op].Action.ID, (AID)ogcds[op].Action.ID == AID.BowShock ? Player : mainTarget, OGCDPriority.Severe + 5);
    }

    private void Planner74FeedCooldownPlannerTimelineHints()
    {
        AkechiGNBPlanner74HintBus.ClearSource(AkechiGNBPlanner74HintSource.BossTimeline);

        if (!Player.InCombat || SkSGCDLength <= 0f)
            return;

        var planner = Manager.Planner;
        if (planner == null)
            return;

        var horizon = SkSGCDLength * Planner74HorizonSlots + TimingTargetWindowFuturePadding;
        var (downtimeActive, transitionIn) = planner.EstimateTimeToNextDowntime();
        if (float.IsNaN(transitionIn) || float.IsInfinity(transitionIn) || transitionIn < 0f || transitionIn > horizon)
            return;

        if (downtimeActive)
        {
            AkechiGNBPlanner74HintBus.PushWindowAbsolute(
                CombatTimer,
                CombatTimer + transitionIn,
                targetable: false,
                AkechiGNBPlanner74HintSource.BossTimeline,
                "BossMod CD Planner: downtime",
                priority: HintPriorityBossTimelineDowntime);

            AkechiGNBPlanner74HintBus.PushWindowAbsolute(
                CombatTimer + transitionIn,
                CombatTimer + horizon,
                targetable: true,
                AkechiGNBPlanner74HintSource.BossTimeline,
                "BossMod CD Planner: uptime",
                priority: HintPriorityBossTimelineUptime);
        }
        else
        {
            AkechiGNBPlanner74HintBus.PushWindowAbsolute(
                CombatTimer,
                CombatTimer + transitionIn,
                targetable: true,
                AkechiGNBPlanner74HintSource.BossTimeline,
                "BossMod CD Planner: uptime",
                priority: HintPriorityBossTimelineUptime);

            // the downtime lasts until the state machine's DowntimeEnd when it is known, not a fixed two GCDs
            var downtimeEnd = Bossmods.ActiveModule?.StateMachine.NextTransitionWithFlag(StateMachine.StateHint.DowntimeEnd) ?? DateTime.MaxValue;
            var downtimeEndIn = downtimeEnd == DateTime.MaxValue ? float.MaxValue : (float)(downtimeEnd - World.CurrentTime).TotalSeconds;
            var upcomingEnd = downtimeEndIn > transitionIn && downtimeEndIn < float.MaxValue
                ? MathF.Min(horizon, downtimeEndIn)
                : MathF.Min(horizon, transitionIn + SkSGCDLength * TimingCurrentAddHintGCDs);
            AkechiGNBPlanner74HintBus.PushWindowAbsolute(
                CombatTimer + transitionIn,
                CombatTimer + upcomingEnd,
                targetable: false,
                AkechiGNBPlanner74HintSource.BossTimeline,
                "BossMod CD Planner: upcoming downtime",
                priority: HintPriorityBossTimelineDowntime);
        }
    }

    private void Planner74FeedCurrentAddTargetHints(Actor? mainTarget)
    {
        if (!Player.InCombat || SkSGCDLength <= 0f || !Planner74CurrentTargetable(mainTarget))
            return;

        var planner = Manager.Planner;
        if (planner == null)
            return;

        var (downtimeActive, _) = planner.EstimateTimeToNextDowntime();
        if (!downtimeActive)
            return;

        AkechiGNBPlanner74HintBus.PushWindowAbsolute(
            CombatTimer,
            CombatTimer + SkSGCDLength * TimingCurrentAddHintGCDs,
            targetable: true,
            AkechiGNBPlanner74HintSource.BossPlanner,
            "Current add target: attackable during boss downtime",
            priority: HintPriorityCurrentAddTarget);
    }

    private void Planner74FeedUnsupportedBossFallbackHints(Actor? mainTarget)
    {
        if (!Player.InCombat)
            return;

        Planner74FeedLearnedTimelineHints(mainTarget);

        if (!Planner74CurrentTargetable(mainTarget))
        {
            AkechiGNBPlanner74HintBus.PushWindowAbsolute(
                CombatTimer,
                CombatTimer + SkSGCDLength,
                targetable: false,
                AkechiGNBPlanner74HintSource.BossPlanner,
                "Fallback: currently untargetable",
                priority: HintPriorityBossPlannerFallback);
        }
    }

    private bool[] Planner74BuildTargetableBySlotFromHints(Actor? mainTarget)
    {
        var result = Planner74TargetableScratch;
        var baseTargetable = Planner74CurrentTargetable(mainTarget);
        for (var slot = 0; slot < result.Length; ++slot)
            result[slot] = baseTargetable;

        var windows = Planner74HintScratch;
        AkechiGNBPlanner74HintBus.Snapshot(CombatTimer - TimingTargetWindowPastPadding, CombatTimer + SkSGCDLength * Planner74HorizonSlots + TimingTargetWindowFuturePadding, windows);
        for (var slot = 0; slot < result.Length; ++slot)
        {
            var at = CombatTimer + SkSGCDLength * slot;
            var bestPrio = int.MinValue;
            var targetable = result[slot];
            for (var i = 0; i < windows.Count; ++i)
            {
                var w = windows[i];
                if (!w.ActiveAt(at, SkSGCDLength * TimingTargetWindowActiveBeforeGCDScale, TimingTargetWindowActiveAfter) || w.Priority < bestPrio)
                    continue;
                bestPrio = w.Priority;
                targetable = w.Targetable;
            }
            result[slot] = targetable;
        }

        var last = Planner74LastPlanValid ? Planner74LastPlanInput.TargetableBySlot : null;
        return last != null && last.AsSpan().SequenceEqual(result) ? last : (bool[])result.Clone();
    }

    private Planner74SynergyWindow[] Planner74BuildSynergyWindows(Actor? mainTarget)
    {
        if (!Player.InCombat || CombatTimer < TimingOpenerEnd || SkSGCDLength <= 0f)
            return [];

        var list = Planner74SynergyScratch;
        list.Clear();
        Planner74AddActiveSynergyWindows(list, Player);
        if (mainTarget != null)
            Planner74AddActiveSynergyWindows(list, mainTarget);

        var horizon = SkSGCDLength * (Planner74HorizonSlots + Planner74SynergyMaxDelaySlots + TimingTargetWindowFuturePadding);
        Bossmods.RaidCooldowns.DamageCooldowns(Planner74DamageCooldownScratch);
        foreach (var cd in Planner74DamageCooldownScratch)
        {
            if (cd.AvailableIn > horizon)
                continue;

            var weight = Planner74SynergyWeight(cd.Action);
            if (weight <= 0f)
                continue;

            var start = cd.AvailableIn / SkSGCDLength;
            var end = (cd.AvailableIn + Planner74SynergyDuration(cd.Action)) / SkSGCDLength;
            list.Add(new(start, end, weight));
        }

        var last = Planner74LastPlanValid ? Planner74LastPlanInput.SynergyWindows : null;
        if (last != null && last.Length == list.Count)
        {
            var same = true;
            for (var i = 0; i < last.Length && same; ++i)
                same = last[i].StartSlot == list[i].StartSlot && last[i].EndSlot == list[i].EndSlot && last[i].Weight == list[i].Weight;
            if (same)
                return last;
        }
        return list.ToArray();
    }

    private void Planner74AddActiveSynergyWindows(System.Collections.Generic.List<Planner74SynergyWindow> list, Actor actor)
    {
        foreach (var status in actor.Statuses)
        {
            var weight = Planner74SynergyWeight(status.ID);
            if (weight <= 0f)
                continue;

            var left = (float)(status.ExpireAt - World.CurrentTime).TotalSeconds;
            if (left <= 0f)
                continue;

            list.Add(new(0f, left / SkSGCDLength, weight));
        }
    }

    private static float Planner74SynergyDuration(ActionID action) => action.ID switch
    {
        (uint)BRD.AID.BattleVoice => GameRuleBattleVoiceDuration,
        (uint)SMN.AID.SearingLight => GameRuleSearingLightDuration,
        _ => GameRuleNoMercyDuration,
    };

    private static float Planner74SynergyWeight(ActionID action) => action.ID switch
    {
        (uint)AST.AID.Divination => ScoreSynergyDivination,
        (uint)DRG.AID.BattleLitany => ScoreSynergyBattleLitany,
        (uint)RPR.AID.ArcaneCircle => ScoreSynergyArcaneCircle,
        (uint)MNK.AID.Brotherhood => ScoreSynergyStandardBuff,
        (uint)NIN.AID.Mug or (uint)NIN.AID.Dokumori => ScoreSynergyStandardBuff,
        (uint)BRD.AID.BattleVoice => ScoreSynergyBattleLitany,
        (uint)DNC.AID.QuadrupleTechnicalFinish => ScoreSynergyStandardBuff,
        (uint)SMN.AID.SearingLight => ScoreSynergyStandardBuff,
        (uint)RDM.AID.Embolden => ScoreSynergyStandardBuff,
        (uint)PCT.AID.StarryMuse => ScoreSynergyStandardBuff,
        (uint)SCH.AID.ChainStratagem => ScoreSynergyBattleLitany,
        _ => 0f,
    };

    private static float Planner74SynergyWeight(uint statusID) => statusID switch
    {
        (uint)AST.SID.Divination => ScoreSynergyDivination,
        (uint)DRG.SID.BattleLitany => ScoreSynergyBattleLitany,
        (uint)RPR.SID.ArcaneCircle => ScoreSynergyArcaneCircle,
        (uint)MNK.SID.Brotherhood => ScoreSynergyStandardBuff,
        (uint)NIN.SID.Dokumori or (uint)NIN.SID.VulnerabilityUp => ScoreSynergyStandardBuff,
        (uint)BRD.SID.BattleVoice => ScoreSynergyBattleLitany,
        (uint)DNC.SID.TechnicalFinish => ScoreSynergyStandardBuff,
        (uint)SMN.SID.SearingLight => ScoreSynergyStandardBuff,
        (uint)RDM.SID.Embolden => ScoreSynergyStandardBuff,
        (uint)PCT.SID.StarryMuse => ScoreSynergyStandardBuff,
        (uint)SCH.SID.ChainStratagem => ScoreSynergyBattleLitany,
        _ => 0f,
    };

    private bool Planner74ShouldUseOpeningDelayedSolidBurst(int comboStep)
    {
        if (!Unlocked(AID.ReignOfBeasts) || !Player.InCombat || CombatTimer >= TimingOpenerEnd || GunComboStep != 0)
            return false;

        if (!HasNM)
            return comboStep is 0 or 1 or 2 && NMcd <= SkSGCDLength * TimingOpeningDelayedSolidNMReadyGCDs && BFcd <= SkSGCDLength * TimingOpeningDelayedSolidBFReadyGCDs;

        return comboStep == Planner74BloodfestWeaveOffsetSlots && HasBF && ActualComboTimer > SkSGCDLength * TimingNormalGFAfterComboSafetyGCDs;
    }

    private bool AllowOpeningDelayedSolidGF()
        => Unlocked(AID.ReignOfBeasts) &&
            Player.InCombat &&
            CombatTimer < TimingOpenerEnd &&
            HasNM &&
            HasBF &&
            GunComboStep == 0 &&
            ComboLastMove == AID.BrutalShell &&
            ActualComboTimer > SkSGCDLength * TimingNormalGFAfterComboSafetyGCDs;

    private bool IsOpeningDelayedSolidGFStart()
        => Unlocked(AID.ReignOfBeasts) &&
            Player.InCombat &&
            CombatTimer < TimingOpenerEnd &&
            HasNM &&
            HasBF &&
            ActualComboTimer > SkSGCDLength * Planner74PreNMGFOffsetSlots;

    private Planner74Result Planner74BuildPlanForCurrentState(Actor? mainTarget, bool optimizeRaidSynergy, bool smoothFullModeBurstEntry, bool holdBuffs = false)
    {
        var rawGF = DebugActionChargesRaw(AID.GnashingFang);
        var gfNextCharge = rawGF.CurrentCharges >= GameRuleMaxGFCharges || !rawGF.RecastActive ? Planner74FarSlot : Planner74CDToGCDSlot(MathF.Max(0f, rawGF.RecastTotal - rawGF.RecastElapsed), SkSGCDLength);
        var comboStep = ComboLastMove switch
        {
            AID.KeenEdge => 1,
            AID.BrutalShell => 2,
            _ => 0,
        };
        var aoeComboStep = ComboLastMove == AID.DemonSlice ? 1 : 0;

        var openingDelayedSolidBurst = Planner74ShouldUseOpeningDelayedSolidBurst(comboStep);
        var zoneAction = Unlocked(AID.BlastingZone) ? AID.BlastingZone : AID.DangerZone;
        // an ability lock delays every ability weave the same way a cooldown would, so it is folded into the ready slots
        var abilityLock = Locks.AbilityLeft;
        var input = new Planner74Input
        {
            ComboStep = comboStep,
            AOEComboStep = aoeComboStep,
            ComboTimerSlotsLeft = comboStep > 0 || aoeComboStep > 0 ? Planner74BuffLeftToSlots(ActualComboTimer, SkSGCDLength) : 0,
            GFStep = GunComboStep is 1 or 2 ? GunComboStep : 0,
            ReignStep = GunComboStep is 3 or 4 ? GunComboStep - 2 : 0,
            Ammo = Ammo,
            MaxAmmo = MaxCartridges,
            GFCharges = rawGF.CurrentCharges,
            GFNextChargeSlot = gfNextCharge,
            GFChargeSlots = Planner74CDToGCDSlot(GameRuleGnashingFangChargeTime, SkSGCDLength),
            // with the buffs held (Hold=HoldBuffs) the rotation never presses No Mercy or Bloodfest, so the plan must not count on them:
            // it used to plan the burst and queue its Sonic Break, which the game refuses without Ready to Break
            NoMercyReadyWeaveSlot = holdBuffs ? Planner74FarSlot : Planner74CDToLateWeaveSlot(MathF.Max(NMcd, abilityLock), SkSGCDLength),
            BloodfestReadyWeaveSlot = holdBuffs ? Planner74FarSlot : Planner74CDToWeaveSlot(MathF.Max(BFcd, abilityLock), SkSGCDLength),
            DoubleDownReadyGCDSlot = Planner74CDToGCDSlot(Cooldown(AID.DoubleDown), SkSGCDLength),
            // Sonic Break has no recast of its own since 7.0 (client data: 2.5 s, the GCD group): No Mercy grants Ready to Break.
            // Without the status the next chance is the next No Mercy, which is what the planner's 60 s model stands for.
            SonicBreakReadyGCDSlot = HasStatus(SID.ReadyToBreak) ? 0 : holdBuffs ? Planner74FarSlot : Planner74CDToGCDSlot(NMcd, SkSGCDLength),
            ZoneReadyWeaveSlot = Unlocked(AID.DangerZone) ? Planner74CDToWeaveSlot(MathF.Max(Cooldown(zoneAction), abilityLock), SkSGCDLength) : Planner74FarSlot,
            BowShockReadyWeaveSlot = Unlocked(AID.BowShock) ? Planner74CDToWeaveSlot(MathF.Max(Cooldown(AID.BowShock), abilityLock), SkSGCDLength) : Planner74FarSlot,
            ZonePotency = Unlocked(AID.BlastingZone) ? GamePotencyBlastingZone : Unlocked(AID.DangerZone) ? GamePotencyDangerZone : 0f,
            BowShockPotency = Unlocked(AID.BowShock) ? GamePotencyBowShock : 0f,
            ReignReady = HasReign,
            NoMercySlotsLeft = Planner74BuffLeftToSlots(NMstatus, SkSGCDLength),
            NoMercyDurationSlots = Planner74NoMercyDurationSlotsForGCD(SkSGCDLength),
            BloodfestSlotsLeft = Planner74BuffLeftToSlots(Status(SID.Bloodfest), SkSGCDLength),
            TargetsIn5y = CountPriorityTargetsInAOECircle(AOEMeleeRadius),
            NormalAOEThreshold = NormalAOEThreshold(),
            NoMercyUnlocked = Unlocked(AID.NoMercy),
            BloodfestUnlocked = Unlocked(AID.Bloodfest),
            BurstStrikeUnlocked = Unlocked(AID.BurstStrike),
            DemonSliceUnlocked = Unlocked(AID.DemonSlice),
            GnashingFangUnlocked = Unlocked(AID.GnashingFang),
            SonicBreakUnlocked = Unlocked(AID.SonicBreak),
            DoubleDownUnlocked = Unlocked(AID.DoubleDown),
            ReignUnlocked = Unlocked(AID.ReignOfBeasts),
            DemonSlaughterUnlocked = Unlocked(AID.DemonSlaughter),
            FatedCircleUnlocked = Unlocked(AID.FatedCircle),
            NextNoMercyFirstBuffedSlot = openingDelayedSolidBurst ? comboStep switch
            {
                0 => 2,
                1 => 1,
                2 => 1,
                _ => 0,
            } : 0,
            OpeningDelayedSolidBurst = openingDelayedSolidBurst,
            OpeningBurstGFUsed = OpeningBurstGFUsedInNM,
            SmoothFullModeBurstEntry = smoothFullModeBurstEntry,
            TargetableBySlot = Planner74BuildTargetableBySlotFromHints(mainTarget),
            OptimizeRaidSynergy = optimizeRaidSynergy,
            SynergyWindows = optimizeRaidSynergy ? Planner74BuildSynergyWindows(mainTarget) : null,
            CombatTime = CombatTimer,
            GCDLength = SkSGCDLength,
            EstimatedFightEnd = EstimatedFightEnd,
            AbilityLockedSlots = Planner74CDToWeaveSlot(abilityLock, SkSGCDLength),
        };

        // the beam search is expensive; reuse the last plan until the slot-quantized inputs actually change
        if (Planner74LastPlanValid && Planner74SameInput(in input, in Planner74LastPlanInput))
            return Planner74LastPlan;

        var plan = Planner74Build24GCDPlan(input);
        Planner74LastPlanInput = input;
        Planner74LastPlanValid = true;
        return plan;
    }

    private static int Planner74FightEndQuarterSlots(in Planner74Input input)
        => input.EstimatedFightEnd == float.MaxValue || input.GCDLength <= 0f ? int.MaxValue : (int)MathF.Floor((input.EstimatedFightEnd - input.CombatTime) / input.GCDLength * 4f);

    private static bool Planner74SameTargetable(bool[]? a, bool[]? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; ++i)
            if (a[i] != b[i])
                return false;
        return true;
    }

    private static bool Planner74SameSynergy(Planner74SynergyWindow[]? a, Planner74SynergyWindow[]? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null || a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; ++i)
        {
            if (a[i].Weight != b[i].Weight)
                return false;
            if ((int)MathF.Floor(a[i].StartSlot * 4f) != (int)MathF.Floor(b[i].StartSlot * 4f))
                return false;
            if ((int)MathF.Floor(a[i].EndSlot * 4f) != (int)MathF.Floor(b[i].EndSlot * 4f))
                return false;
        }
        return true;
    }

    private static bool Planner74SameInput(in Planner74Input a, in Planner74Input b)
        => a.ComboStep == b.ComboStep &&
            a.AOEComboStep == b.AOEComboStep &&
            a.ComboTimerSlotsLeft == b.ComboTimerSlotsLeft &&
            a.GFStep == b.GFStep &&
            a.ReignStep == b.ReignStep &&
            a.Ammo == b.Ammo &&
            a.MaxAmmo == b.MaxAmmo &&
            a.GFCharges == b.GFCharges &&
            a.GFNextChargeSlot == b.GFNextChargeSlot &&
            a.GFChargeSlots == b.GFChargeSlots &&
            a.NoMercyReadyWeaveSlot == b.NoMercyReadyWeaveSlot &&
            a.BloodfestReadyWeaveSlot == b.BloodfestReadyWeaveSlot &&
            a.DoubleDownReadyGCDSlot == b.DoubleDownReadyGCDSlot &&
            a.SonicBreakReadyGCDSlot == b.SonicBreakReadyGCDSlot &&
            a.ZoneReadyWeaveSlot == b.ZoneReadyWeaveSlot &&
            a.BowShockReadyWeaveSlot == b.BowShockReadyWeaveSlot &&
            a.ZonePotency == b.ZonePotency &&
            a.BowShockPotency == b.BowShockPotency &&
            a.ReignReady == b.ReignReady &&
            a.NoMercySlotsLeft == b.NoMercySlotsLeft &&
            a.NoMercyDurationSlots == b.NoMercyDurationSlots &&
            a.BloodfestSlotsLeft == b.BloodfestSlotsLeft &&
            a.TargetsIn5y == b.TargetsIn5y &&
            a.NormalAOEThreshold == b.NormalAOEThreshold &&
            a.NoMercyUnlocked == b.NoMercyUnlocked &&
            a.BloodfestUnlocked == b.BloodfestUnlocked &&
            a.BurstStrikeUnlocked == b.BurstStrikeUnlocked &&
            a.DemonSliceUnlocked == b.DemonSliceUnlocked &&
            a.GnashingFangUnlocked == b.GnashingFangUnlocked &&
            a.SonicBreakUnlocked == b.SonicBreakUnlocked &&
            a.DoubleDownUnlocked == b.DoubleDownUnlocked &&
            a.ReignUnlocked == b.ReignUnlocked &&
            a.DemonSlaughterUnlocked == b.DemonSlaughterUnlocked &&
            a.FatedCircleUnlocked == b.FatedCircleUnlocked &&
            a.NextNoMercyFirstBuffedSlot == b.NextNoMercyFirstBuffedSlot &&
            a.OpeningDelayedSolidBurst == b.OpeningDelayedSolidBurst &&
            a.OpeningBurstGFUsed == b.OpeningBurstGFUsed &&
            a.SmoothFullModeBurstEntry == b.SmoothFullModeBurstEntry &&
            a.OptimizeRaidSynergy == b.OptimizeRaidSynergy &&
            a.GCDLength == b.GCDLength &&
            a.AbilityLockedSlots == b.AbilityLockedSlots &&
            Planner74FightEndQuarterSlots(in a) == Planner74FightEndQuarterSlots(in b) &&
            Planner74SameTargetable(a.TargetableBySlot, b.TargetableBySlot) &&
            Planner74SameSynergy(a.SynergyWindows, b.SynergyWindows);

    private static bool Planner74AOEStrategyIsAutomatic(AOEStrategy aoeStrat)
        => aoeStrat is AOEStrategy.AutoFinishWithOvercap or AOEStrategy.AutoFinishWithoutOvercap or AOEStrategy.AutoBreakWithOvercap or AOEStrategy.AutoBreakWithoutOvercap;

    // returns true when a GCD was actually queued; non-automatic tracks are left to their own (Force*/Only*/Early/Late) handling
    private bool Planner74QueuePlannedGCD(Planner74Decision next, Actor? mainTarget, StrategyValues strategy)
    {
        var prio = GCDPriority.VeryHigh;
        var aoeAutomatic = Planner74AOEStrategyIsAutomatic(strategy.Option(Track.AOE).As<AOEStrategy>());
        var cartsStrat = strategy.Option(Track.Cartridges).As<CartridgeStrategy>();
        var cartsAutomatic = cartsStrat is CartridgeStrategy.Automatic or CartridgeStrategy.OnlyBS or CartridgeStrategy.OnlyFC or CartridgeStrategy.NormalOvercapOnly;
        var gfAutomatic = strategy.Option(Track.GnashingFang).As<GnashingStrategy>() == GnashingStrategy.Automatic;
        switch (next.GCD)
        {
            case Planner74GCD.KeenEdge:
                if (!aoeAutomatic)
                    break;
                QueueGCD(AID.KeenEdge, mainTarget, prio);
                return true;
            case Planner74GCD.BrutalShell:
                if (!aoeAutomatic)
                    break;
                QueueGCD(AID.BrutalShell, mainTarget, prio);
                return true;
            case Planner74GCD.SolidBarrel:
                if (!aoeAutomatic)
                    break;
                QueueGCD(AID.SolidBarrel, mainTarget, prio);
                return true;
            case Planner74GCD.BurstStrike:
                if (!cartsAutomatic || !Unlocked(AID.BurstStrike))
                    break;
                if (cartsStrat != CartridgeStrategy.OnlyBS && Unlocked(AID.FatedCircle) && (WantFatedCircleAOE || cartsStrat == CartridgeStrategy.OnlyFC))
                    QueueGCD(AID.FatedCircle, Player, GCDPriority.VeryHigh + 1);
                else
                    QueueGCD(AID.BurstStrike, mainTarget, GCDPriority.VeryHigh + 1);
                return true;
            case Planner74GCD.FatedCircle:
                if (!cartsAutomatic)
                    break;
                if (cartsStrat != CartridgeStrategy.OnlyBS && Unlocked(AID.FatedCircle))
                    QueueGCD(AID.FatedCircle, Player, GCDPriority.VeryHigh + 1);
                else if (Unlocked(AID.BurstStrike))
                    QueueGCD(AID.BurstStrike, mainTarget, GCDPriority.VeryHigh + 1);
                else
                    break;
                return true;
            case Planner74GCD.DemonSlice:
                if (!aoeAutomatic)
                    break;
                QueueGCD(Unlocked(AID.DemonSlice) ? AID.DemonSlice : AID.KeenEdge, Unlocked(AID.DemonSlice) ? Player : mainTarget, prio);
                return true;
            case Planner74GCD.DemonSlaughter:
                if (!aoeAutomatic)
                    break;
                QueueGCD(Unlocked(AID.DemonSlaughter) ? AID.DemonSlaughter : Unlocked(AID.DemonSlice) ? AID.DemonSlice : AID.KeenEdge, Unlocked(AID.DemonSlice) ? Player : mainTarget, prio);
                return true;
            case Planner74GCD.GnashingFang:
                if (!gfAutomatic || !Unlocked(AID.GnashingFang))
                    break;
                QueueGCD(AID.GnashingFang, mainTarget, GCDPriority.VeryHigh + 2);
                return true;
            case Planner74GCD.SavageClaw:
                if (!gfAutomatic || !Unlocked(AID.SavageClaw))
                    break;
                QueueGCD(AID.SavageClaw, mainTarget, GCDPriority.VeryHigh + 2);
                return true;
            case Planner74GCD.WickedTalon:
                if (!gfAutomatic || !Unlocked(AID.WickedTalon))
                    break;
                QueueGCD(AID.WickedTalon, mainTarget, GCDPriority.VeryHigh + 2);
                return true;
            case Planner74GCD.DoubleDown:
                // a 5 y circle around the player: with nothing inside it (a forced disengage) it spends two cartridges and its recast
                // on nothing. The path below the planner checks the same before it queues Double Down.
                if (strategy.Option(Track.DoubleDown).As<DoubleDownStrategy>() != DoubleDownStrategy.Automatic || !Unlocked(AID.DoubleDown) || !AnyTargetIn5y(mainTarget))
                    break;
                QueueGCD(AID.DoubleDown, Player, GCDPriority.VeryHigh + 3);
                return true;
            case Planner74GCD.SonicBreak:
                if (strategy.Option(Track.SonicBreak).As<SonicBreakStrategy>() != SonicBreakStrategy.Automatic || !Unlocked(AID.SonicBreak))
                    break;
                QueueGCD(AID.SonicBreak, mainTarget, GCDPriority.VeryHigh + 1);
                return true;
            case Planner74GCD.ReignOfBeasts:
            case Planner74GCD.NobleBlood:
            case Planner74GCD.LionHeart:
                if (strategy.Option(Track.Reign).As<ReignStrategy>() != ReignStrategy.Automatic || !Unlocked(AID.ReignOfBeasts))
                    break;
                var reignAction = next.GCD switch
                {
                    Planner74GCD.NobleBlood => AID.NobleBlood,
                    Planner74GCD.LionHeart => AID.LionHeart,
                    _ => AID.ReignOfBeasts,
                };
                if (!Unlocked(reignAction))
                    break;
                QueueGCD(reignAction, mainTarget, GCDPriority.VeryHigh + 2);
                return true;
        }
        return false;
    }

    private static Planner74Result Planner74Build24GCDPlan(in Planner74Input input)
    {
        var start = new Planner74State
        {
            ComboStep = Planner74Clamp(input.ComboStep, 0, 2),
            AOEComboStep = Planner74Clamp(input.AOEComboStep, 0, 1),
            ComboTimerSlotsLeft = input.ComboStep > 0 || input.AOEComboStep > 0 ? Planner74Clamp(input.ComboTimerSlotsLeft, 1, Planner74ComboTimerSlots) : 0,
            GFStep = Planner74Clamp(input.GFStep, 0, 2),
            ReignStep = Planner74Clamp(input.ReignStep, 0, 2),
            MaxAmmo = Planner74Clamp(input.MaxAmmo, 0, GameRuleBloodfestMaxCartridgesHigh),
            Ammo = Planner74Clamp(input.Ammo, 0, Planner74Clamp(input.MaxAmmo, 0, GameRuleBloodfestMaxCartridgesHigh)),
            GFCharges = Planner74Clamp(input.GFCharges, 0, GameRuleMaxGFCharges),
            GFNextChargeSlot = input.GFCharges >= GameRuleMaxGFCharges ? Planner74FarSlot : Planner74Clamp(input.GFNextChargeSlot, 0, Planner74FarSlot),
            GFChargeSlots = Planner74Clamp(input.GFChargeSlots <= 0 ? Planner74ComboTimerSlots : input.GFChargeSlots, 1, Planner74FarSlot),
            NoMercyReadyWeaveSlot = Planner74Clamp(input.NoMercyReadyWeaveSlot, 0, Planner74FarSlot),
            BloodfestReadyWeaveSlot = Planner74Clamp(input.BloodfestReadyWeaveSlot, 0, Planner74FarSlot),
            DoubleDownReadyGCDSlot = Planner74Clamp(input.DoubleDownReadyGCDSlot, 0, Planner74FarSlot),
            SonicBreakReadyGCDSlot = Planner74Clamp(input.SonicBreakReadyGCDSlot, 0, Planner74FarSlot),
            ZoneReadyWeaveSlot = Planner74Clamp(input.ZoneReadyWeaveSlot, 0, Planner74FarSlot),
            BowShockReadyWeaveSlot = Planner74Clamp(input.BowShockReadyWeaveSlot, 0, Planner74FarSlot),
            ZonePotency = MathF.Max(0f, input.ZonePotency),
            BowShockPotency = MathF.Max(0f, input.BowShockPotency),
            ReignReady = input.ReignReady,
            NoMercyDurationSlots = Planner74Clamp(input.NoMercyDurationSlots <= 0 ? Planner74NoMercySlots : input.NoMercyDurationSlots, Planner74NoMercySlots, Planner74NoMercyFastSlots),
            BloodfestSlotsLeft = Planner74Clamp(input.BloodfestSlotsLeft, 0, Planner74BloodfestSlots),
            OpeningDelayedSolidBurst = input.OpeningDelayedSolidBurst,
            OpeningBurstGFUsed = input.OpeningBurstGFUsed,
        };
        start.NoMercySlotsLeft = Planner74Clamp(input.NoMercySlotsLeft, 0, start.NoMercyDurationSlots);

        if (input.BloodfestUnlocked && start.BloodfestSlotsLeft > 0)
            start.MaxAmmo = GameRuleBloodfestMaxCartridgesHigh;

        var nmFirstBuffedSlot = input.NoMercySlotsLeft > 0 ? 0 : input.NextNoMercyFirstBuffedSlot > 0 ? input.NextNoMercyFirstBuffedSlot : start.NoMercyReadyWeaveSlot + 1;
        nmFirstBuffedSlot = Planner74Clamp(nmFirstBuffedSlot, 0, Planner74HorizonSlots + 1);
        if (input.SmoothFullModeBurstEntry && input.NoMercySlotsLeft <= 0 && input.NoMercyReadyWeaveSlot <= 1 && start.ReignStep == 0)
        {
            var canStartPreGFNow = start.GFStep == 0 && start.AOEComboStep == 0 && start.ComboStep == 0 && start.GFCharges > 0 && start.Ammo >= 1;
            var canPreBloodfest = input.BloodfestUnlocked && start.BloodfestSlotsLeft <= 0 && start.BloodfestReadyWeaveSlot <= Planner74NoMercyWeaveOffsetSlots;
            if (canStartPreGFNow)
                nmFirstBuffedSlot = Math.Max(nmFirstBuffedSlot, Planner74PreNMGFOffsetSlots);
            else if (canPreBloodfest)
                nmFirstBuffedSlot = Math.Max(nmFirstBuffedSlot, Planner74BloodfestWeaveOffsetSlots);
            nmFirstBuffedSlot = Planner74Clamp(nmFirstBuffedSlot, 0, Planner74HorizonSlots + 1);
        }

        var bestResult = Planner74Build24GCDPlanForNM(input, start, nmFirstBuffedSlot);
        var canOptimizeSynergy =
            input.OptimizeRaidSynergy &&
            input.NoMercySlotsLeft <= 0 &&
            !input.OpeningDelayedSolidBurst &&
            input.SynergyWindows is { Length: > 0 } &&
            nmFirstBuffedSlot > 0 &&
            nmFirstBuffedSlot < Planner74HorizonSlots;
        if (!canOptimizeSynergy)
            return bestResult;

        var maxCandidate = Planner74Clamp(nmFirstBuffedSlot + Planner74SynergyMaxDelaySlots, nmFirstBuffedSlot, Planner74HorizonSlots);
        for (var candidate = nmFirstBuffedSlot + 1; candidate <= maxCandidate; ++candidate)
        {
            var result = Planner74Build24GCDPlanForNM(input, start, candidate);
            if (result.Score > bestResult.Score)
                bestResult = result;
        }

        return bestResult;
    }

    // The beam search runs on every plan rebuild, i.e. several times per GCD. Nodes keep their parent's index and their own
    // decision instead of a copy of the whole plan, and the per-slot lists are reused, so a rebuild allocates only the winning
    // plan (it used to copy every prefix for every candidate: about 195 KB per rotation frame on average). The node order, the
    // scores and the sort are unchanged, so the chosen plan is the same.
    [System.ThreadStatic] private static System.Collections.Generic.List<Planner74Node>[]? Planner74Layers;
    [System.ThreadStatic] private static System.Collections.Generic.List<Planner74GCD>? Planner74CandidateBuffer;

    private static Planner74Result Planner74Build24GCDPlanForNM(in Planner74Input input, Planner74State start, int nmFirstBuffedSlot)
    {
        var layers = Planner74Layers;
        if (layers == null)
        {
            layers = new System.Collections.Generic.List<Planner74Node>[Planner74HorizonSlots + 1];
            for (var l = 0; l < layers.Length; ++l)
                layers[l] = new(Planner74BeamWidth * (l == 0 ? Planner74NodeInitialMultiplier : Planner74NextNodeInitialMultiplier));
            Planner74Layers = layers;
        }
        var nodes = layers[0];
        nodes.Clear();
        nodes.Add(new() { State = start, Parent = -1, Score = 0 });

        for (var slot = 0; slot < Planner74HorizonSlots; ++slot)
        {
            var next = layers[slot + 1];
            next.Clear();
            for (var i = 0; i < nodes.Count; ++i)
            {
                var node = nodes[i];
                var state = node.State;
                Planner74NormalizeGFCharges(ref state, slot);
                var canHitTarget = Planner74SlotTargetable(input.TargetableBySlot, slot);
                var candidates = Planner74BuildGCDCandidates(input, state, slot, nmFirstBuffedSlot, canHitTarget);

                for (var c = 0; c < candidates.Count; ++c)
                {
                    var s2 = state;
                    var decision = new Planner74Decision { GCD = candidates[c] };
                    var delta = Planner74ApplyGCD(input, ref s2, ref decision, slot, nmFirstBuffedSlot, canHitTarget);
                    delta += Planner74ApplyAutoWeaves(input, ref s2, ref decision, slot, nmFirstBuffedSlot);
                    delta += Planner74SlotEndPenalty(s2, slot, nmFirstBuffedSlot);
                    delta += Planner74AdvanceAfterSlot(ref s2);
                    decision.Score = delta;
                    next.Add(new() { State = s2, Parent = i, Decision = decision, Score = node.Score + delta });
                }
            }

            next.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (next.Count > Planner74BeamWidth)
                next.RemoveRange(Planner74BeamWidth, next.Count - Planner74BeamWidth);
            nodes = next;
        }

        if (nodes.Count == 0)
            return new() { First = new() { GCD = Planner74GCD.None }, Plan = [], Score = Planner74NoNodeScore, Debug = Planner74DebugEnabled ? "Planner failed: no nodes." : "" };

        // walk back from the best leaf; every slot added exactly one decision, as the copied prefixes did
        var plan = new Planner74Decision[Planner74HorizonSlots];
        var index = 0;
        for (var layer = Planner74HorizonSlots; layer > 0; --layer)
        {
            var n = layers[layer][index];
            plan[layer - 1] = n.Decision;
            index = n.Parent;
        }
        var best = nodes[0];
        var score = best.Score - Planner74NoMercyDelayPenalty(input, nmFirstBuffedSlot);
        return new() { First = plan.Length > 0 ? plan[0] : new() { GCD = Planner74GCD.None }, Plan = plan, Score = score, Debug = Planner74DebugEnabled ? Planner74FormatDebug(plan, score, nmFirstBuffedSlot) : "" };
    }

    // returns a shared buffer: the caller reads it before asking for the next node's candidates
    private static System.Collections.Generic.List<Planner74GCD> Planner74BuildGCDCandidates(in Planner74Input input, in Planner74State s, int slot, int nmFirstBuffedSlot, bool canHitTarget)
    {
        var list = Planner74CandidateBuffer ??= new(Planner74CandidateInitialCapacity);
        list.Clear();
        if (!canHitTarget)
        {
            list.Add(Planner74GCD.None);
            return list;
        }

        if (s.GFStep == 1)
        {
            list.Add(Planner74GCD.SavageClaw);
            return list;
        }

        if (s.GFStep == 2)
        {
            list.Add(Planner74GCD.WickedTalon);
            return list;
        }

        var inNM = s.NoMercySlotsLeft > 0;
        var delayOpeningSolid = s.OpeningDelayedSolidBurst && s.ComboStep == 2 && !s.OpeningDelayedSolidGFComplete;
        var gfPreSlot = nmFirstBuffedSlot - Planner74PreNMGFOffsetSlots;
        var tooManyForGF = Planner74ShouldAvoidGFForAOE(input);
        if (inNM)
        {
            var canUseDoubleDown = input.DoubleDownUnlocked && Planner74CanUseDoubleDownAfterDelay(s, slot);
            var canUseSonicBreak = input.SonicBreakUnlocked && Planner74CanUseSonicBreakAfterDelay(s, slot);
            var preserveAmmoForDoubleDown = Planner74ShouldPreserveAmmoForDoubleDown(s, slot);
            var shortWindow = Planner74ShortNoMercyWindow(input, s, slot);
            if (input.GnashingFangUnlocked && !tooManyForGF && Planner74ShouldStartOpeningBurstWithGF(s))
            {
                list.Add(Planner74GCD.GnashingFang);
                return list;
            }

            if (s.ReignStep == 1 && !canUseDoubleDown && !canUseSonicBreak)
            {
                list.Add(Planner74GCD.NobleBlood);
                return list;
            }

            if (s.OpeningDelayedSolidBurst && s.ReignStep == 1 && canUseDoubleDown)
            {
                list.Add(Planner74GCD.DoubleDown);
                return list;
            }

            if (s.OpeningDelayedSolidBurst && s.ReignStep == 1 && canUseSonicBreak)
            {
                list.Add(Planner74GCD.SonicBreak);
                return list;
            }

            if (s.ReignStep == 2)
            {
                list.Add(Planner74GCD.LionHeart);
                return list;
            }

            if (input.ReignUnlocked && Planner74CanUseReign(s) && !shortWindow)
            {
                list.Add(Planner74GCD.ReignOfBeasts);
                return list;
            }

            if (canUseDoubleDown && !shortWindow)
            {
                list.Add(Planner74GCD.DoubleDown);
                return list;
            }

            if (input.GnashingFangUnlocked && !tooManyForGF && !Planner74BlockOpeningBurstGFStart(s) && s.OpeningDelayedSolidBurst && delayOpeningSolid && !s.ReignReady && s.ReignStep == 0 && Planner74CanUseGF(s, allowPendingNormalCombo: true))
            {
                list.Add(Planner74GCD.GnashingFang);
                return list;
            }

            if (canUseDoubleDown)
                Planner74AddUnique(list, Planner74GCD.DoubleDown);
            if (input.ReignUnlocked && Planner74CanUseReign(s))
                Planner74AddUnique(list, Planner74GCD.ReignOfBeasts);
            if (canUseSonicBreak)
                Planner74AddUnique(list, Planner74GCD.SonicBreak);
            if (s.ReignStep == 1)
                Planner74AddUnique(list, Planner74GCD.NobleBlood);
            if (s.ReignStep == 2)
                Planner74AddUnique(list, Planner74GCD.LionHeart);
            if (input.GnashingFangUnlocked && !tooManyForGF && !Planner74BlockOpeningBurstGFStart(s) && Planner74CanUseGF(s, delayOpeningSolid))
                Planner74AddUnique(list, Planner74GCD.GnashingFang);
            if (input.GnashingFangUnlocked && !tooManyForGF && Planner74ShouldForceLateBurstGF(s))
            {
                Planner74AddUnique(list, Planner74GCD.GnashingFang);
                return list;
            }
            if (Planner74ShouldHoldForLateBurstGF(s, slot))
            {
                Planner74AddUnique(list, Planner74GCD.None);
                return list;
            }
            if (input.BurstStrikeUnlocked && s.Ammo >= 1 && !preserveAmmoForDoubleDown)
                Planner74AddUnique(list, Planner74WantsFatedCircleAOE(input) ? Planner74GCD.FatedCircle : Planner74GCD.BurstStrike);
            if (!delayOpeningSolid)
                Planner74AddUnique(list, preserveAmmoForDoubleDown ? Planner74NextNormalGCD(s) : Planner74NextFillerGCD(input, s, slot, nmFirstBuffedSlot));
            else if (list.Count == 0)
                Planner74AddUnique(list, Planner74GCD.SolidBarrel);
            return list;
        }

        if (s.ReignStep == 1)
        {
            list.Add(Planner74GCD.NobleBlood);
            return list;
        }

        if (s.ReignStep == 2)
        {
            list.Add(Planner74GCD.LionHeart);
            return list;
        }

        if (s.OpeningDelayedSolidBurst && s.ComboStep == 2 && slot == nmFirstBuffedSlot - Planner74NoMercyWeaveOffsetSlots)
        {
            if (input.ReignUnlocked && Planner74CanUseReign(s))
                list.Add(Planner74GCD.ReignOfBeasts);
            else if (input.DoubleDownUnlocked && Planner74CanUseDoubleDown(s, slot))
                list.Add(Planner74GCD.DoubleDown);
            else if (input.SonicBreakUnlocked && slot >= s.SonicBreakReadyGCDSlot)
                list.Add(Planner74GCD.SonicBreak);
            else if (input.GnashingFangUnlocked && Planner74CanUseGF(s, allowPendingNormalCombo: true))
                list.Add(Planner74GCD.GnashingFang);
            else if (input.BurstStrikeUnlocked && s.Ammo >= 1)
                list.Add(Planner74GCD.BurstStrike);
            else
                list.Add(Planner74GCD.SolidBarrel);
            return list;
        }

        var latestPreGFSlot = nmFirstBuffedSlot - Planner74NoMercyWeaveOffsetSlots;
        if (input.BurstStrikeUnlocked && slot < gfPreSlot && Planner74ShouldUseBurstStrikeForPreNMGFAlignment(s, slot, nmFirstBuffedSlot))
        {
            Planner74AddUnique(list, Planner74WantsFatedCircleAOE(input) ? Planner74GCD.FatedCircle : Planner74GCD.BurstStrike);
            return list;
        }

        if (slot >= gfPreSlot && slot <= latestPreGFSlot)
        {
            if (s.ComboStep != 0)
            {
                if (input.BurstStrikeUnlocked && (Planner74ShouldUseBurstStrikeForBrokenPreNMFallback(s, slot, nmFirstBuffedSlot) || Planner74ShouldUseBurstStrikeBeforeSolidBarrel(s, slot, nmFirstBuffedSlot)))
                    Planner74AddUnique(list, Planner74WantsFatedCircleAOE(input) ? Planner74GCD.FatedCircle : Planner74GCD.BurstStrike);
                else
                    Planner74AddUnique(list, Planner74NextFillerGCD(input, s, slot, nmFirstBuffedSlot));
                return list;
            }

            if (input.GnashingFangUnlocked && !tooManyForGF && Planner74CanStartPreNMGF(s))
                Planner74AddUnique(list, Planner74GCD.GnashingFang);
            else if (input.BurstStrikeUnlocked && slot == latestPreGFSlot && Planner74HasExtraBurstAmmo(s))
                Planner74AddUnique(list, Planner74WantsFatedCircleAOE(input) ? Planner74GCD.FatedCircle : Planner74GCD.BurstStrike);

            if (list.Count == 0)
                Planner74AddUnique(list, Planner74NextFillerGCD(input, s, slot, nmFirstBuffedSlot));
            return list;
        }
        if (input.BurstStrikeUnlocked && Planner74ShouldUseBurstStrikeBeforeSolidBarrel(s, slot, nmFirstBuffedSlot))
            Planner74AddUnique(list, Planner74WantsFatedCircleAOE(input) ? Planner74GCD.FatedCircle : Planner74GCD.BurstStrike);
        if (input.GnashingFangUnlocked && !tooManyForGF && slot < gfPreSlot && Planner74CanUseNormalGFWithoutBreakingNextNM(s, slot, nmFirstBuffedSlot))
            Planner74AddUnique(list, Planner74GCD.GnashingFang);

        if (Planner74ShouldUseBasicBurstGapGuard(s, slot, nmFirstBuffedSlot))
        {
            if (input.BurstStrikeUnlocked && list.Count == 0 && Planner74HasExtraBurstAmmo(s) && s.ComboStep == 0)
                Planner74AddUnique(list, Planner74WantsFatedCircleAOE(input) ? Planner74GCD.FatedCircle : Planner74GCD.BurstStrike);
            if (list.Count == 0)
                Planner74AddUnique(list, Planner74NextFillerGCD(input, s, slot, nmFirstBuffedSlot));
            return list;
        }

        Planner74AddUnique(list, Planner74NextFillerGCD(input, s, slot, nmFirstBuffedSlot));
        return list;
    }

    private static float Planner74ApplyGCD(in Planner74Input input, ref Planner74State s, ref Planner74Decision d, int slot, int nmFirstBuffedSlot, bool canHitTarget)
    {
        if (!canHitTarget || d.GCD == Planner74GCD.None)
        {
            // time still passes in a slot without a GCD: No Mercy keeps running out
            if (s.NoMercySlotsLeft > 0)
                s.NoMercySlotsLeft = Planner74Clamp(s.NoMercySlotsLeft - 1, 0, s.NoMercyDurationSlots);
            return 0f;
        }

        var buffed = s.NoMercySlotsLeft > 0;
        var score = Planner74ScoreValue(input, d.GCD) * (buffed ? ScoreNoMercyMultiplier : 1f);
        if (slot < input.AbilityLockedSlots)
            score -= Planner74ContinuationPotency(d.GCD) * (buffed ? ScoreNoMercyMultiplier : 1f);
        score += Planner74SynergyScore(input.SynergyWindows, d.GCD, slot);
        var gfPreSlot = nmFirstBuffedSlot - Planner74PreNMGFOffsetSlots;
        var latestPreGFSlot = nmFirstBuffedSlot - Planner74NoMercyWeaveOffsetSlots;
        if (d.GCD == Planner74GCD.GnashingFang && slot >= gfPreSlot && slot <= latestPreGFSlot)
            score += ScorePreNMGFBase - PenaltyPreNMGFDistance * (slot - gfPreSlot);
        if (slot == nmFirstBuffedSlot - Planner74BloodfestWeaveOffsetSlots && d.GCD == Planner74GCD.SavageClaw)
            score += ScorePreNMSavageClaw;
        if (slot == nmFirstBuffedSlot - Planner74NoMercyWeaveOffsetSlots && d.GCD == Planner74GCD.WickedTalon)
            score += ScorePreNMWickedTalon;
        score += Planner74BalanceBurstOrderBonus(s, d.GCD, slot, nmFirstBuffedSlot);
        if (buffed)
            score += d.GCD switch
            {
                Planner74GCD.DoubleDown => ScoreBuffedDoubleDown,
                Planner74GCD.ReignOfBeasts or Planner74GCD.NobleBlood or Planner74GCD.LionHeart => ScoreBuffedReignCombo,
                Planner74GCD.SonicBreak => ScoreBuffedSonicBreak,
                Planner74GCD.GnashingFang => ScoreBuffedGnashingFang,
                _ => 0f,
            };

        var slotsToNM = nmFirstBuffedSlot - slot;
        if (!buffed && slotsToNM is >= 0 and <= Planner74NoMercySlots && d.GCD is Planner74GCD.DoubleDown or Planner74GCD.SonicBreak or Planner74GCD.ReignOfBeasts)
            score -= PenaltyUnbuffedBurstGCDNearNM;

        switch (d.GCD)
        {
            case Planner74GCD.KeenEdge:
                if (s.ComboStep != 0)
                    score -= PenaltyBrokenKeenCombo;
                s.ComboStep = 1;
                s.AOEComboStep = 0;
                s.ComboTimerSlotsLeft = Planner74ComboTimerSlots;
                break;
            case Planner74GCD.BrutalShell:
                if (s.ComboStep != 1)
                    score -= PenaltyBrokenBrutalCombo;
                s.ComboStep = 2;
                s.AOEComboStep = 0;
                s.ComboTimerSlotsLeft = Planner74ComboTimerSlots;
                break;
            case Planner74GCD.SolidBarrel:
                if (s.ComboStep != 2)
                    score -= PenaltyBrokenSolidCombo;
                s.ComboStep = 0;
                s.AOEComboStep = 0;
                s.ComboTimerSlotsLeft = 0;
                s.OpeningDelayedSolidBurst = false;
                s.OpeningDelayedSolidGFComplete = false;
                if (s.MaxAmmo > 0)
                {
                    var before = s.Ammo;
                    s.Ammo = Planner74Clamp(s.Ammo + 1, 0, s.MaxAmmo);
                    if (before + 1 > s.MaxAmmo)
                        score -= PenaltyCartridgeOvercap;
                }
                break;
            case Planner74GCD.BurstStrike:
                if (s.Ammo < 1)
                    score -= PenaltyMissingCartridgeSpend;
                else
                    s.Ammo--;
                break;
            case Planner74GCD.FatedCircle:
                if (s.Ammo < 1)
                    score -= PenaltyMissingCartridgeSpend;
                else
                    s.Ammo--;
                break;
            case Planner74GCD.DemonSlice:
                s.ComboStep = 0;
                s.AOEComboStep = 1;
                s.ComboTimerSlotsLeft = Planner74ComboTimerSlots;
                break;
            case Planner74GCD.DemonSlaughter:
                if (s.AOEComboStep != 1)
                    score -= PenaltyBrokenBrutalCombo;
                s.ComboStep = 0;
                s.AOEComboStep = 0;
                s.ComboTimerSlotsLeft = 0;
                if (s.MaxAmmo > 0)
                {
                    var beforeAOE = s.Ammo;
                    s.Ammo = Planner74Clamp(s.Ammo + 1, 0, s.MaxAmmo);
                    if (beforeAOE + 1 > s.MaxAmmo)
                        score -= PenaltyCartridgeOvercap;
                }
                break;
            case Planner74GCD.GnashingFang:
                if (!input.GnashingFangUnlocked || !Planner74CanUseGF(s, s.OpeningDelayedSolidBurst && s.ComboStep == 2 && !s.OpeningDelayedSolidGFComplete) && !Planner74CanUseNormalGFWithSafePendingCombo(s) && !Planner74ShouldForceLateBurstGF(s))
                    score -= PenaltyInvalidGFStart;
                else
                {
                    s.ComboStep = 0;
                    s.AOEComboStep = 0;
                    s.ComboTimerSlotsLeft = 0;
                    s.Ammo--;
                    Planner74SpendGFCharge(ref s, slot);
                    s.GFStep = 1;
                    if (s.OpeningDelayedSolidBurst && s.NoMercySlotsLeft > 0)
                        s.OpeningBurstGFUsed = true;
                }
                break;
            case Planner74GCD.SavageClaw:
                if (s.GFStep != 1)
                    score -= PenaltyInvalidGaugeCombo;
                s.GFStep = 2;
                break;
            case Planner74GCD.WickedTalon:
                if (s.GFStep != 2)
                    score -= PenaltyInvalidGaugeCombo;
                s.GFStep = 0;
                if (s.OpeningDelayedSolidBurst && s.ComboStep == 2)
                    s.OpeningDelayedSolidGFComplete = true;
                break;
            case Planner74GCD.DoubleDown:
                if (!input.DoubleDownUnlocked || !Planner74CanUseDoubleDown(s, slot))
                    score -= PenaltyInvalidGaugeCombo;
                else
                {
                    s.Ammo -= GameRuleDoubleDownCartridgeCost;
                    s.DoubleDownReadyGCDSlot = slot + Planner74Cooldown60Slots;
                }
                break;
            case Planner74GCD.SonicBreak:
                if (!input.SonicBreakUnlocked || slot < s.SonicBreakReadyGCDSlot)
                    score -= PenaltyInvalidGFStart;
                else
                    s.SonicBreakReadyGCDSlot = slot + Planner74Cooldown60Slots;
                break;
            case Planner74GCD.ReignOfBeasts:
                if (!input.ReignUnlocked || !Planner74CanUseReign(s))
                    score -= PenaltyInvalidGFStart;
                else
                {
                    s.ReignReady = false;
                    s.ReignStep = 1;
                }
                break;
            case Planner74GCD.NobleBlood:
                if (s.ReignStep != 1)
                    score -= PenaltyInvalidGaugeCombo;
                s.ReignStep = 2;
                break;
            case Planner74GCD.LionHeart:
                if (s.ReignStep != 2)
                    score -= PenaltyInvalidGaugeCombo;
                s.ReignStep = 0;
                break;
        }

        if (buffed)
            s.NoMercySlotsLeft = Planner74Clamp(s.NoMercySlotsLeft - 1, 0, s.NoMercyDurationSlots);
        return score;
    }

    private static float Planner74ApplyAutoWeaves(in Planner74Input input, ref Planner74State s, ref Planner74Decision d, int slot, int nmFirstBuffedSlot)
    {
        var score = 0f;
        if (nmFirstBuffedSlot <= 0)
            return score;

        var fixedBFSlot = slot == nmFirstBuffedSlot - Planner74BloodfestWeaveOffsetSlots;
        if (input.BloodfestUnlocked && fixedBFSlot && s.BloodfestSlotsLeft <= 0 && slot >= s.BloodfestReadyWeaveSlot)
        {
            d.WeaveBloodfest = true;
            s.BloodfestReadyWeaveSlot = slot + Planner74Cooldown60Slots;
            s.BloodfestSlotsLeft = Planner74BloodfestSlots + Planner74NoMercyWeaveOffsetSlots;
            s.MaxAmmo = GameRuleBloodfestMaxCartridgesHigh;
            s.Ammo = Planner74Clamp(s.Ammo + GameRuleBloodfestCartridgesGranted, 0, GameRuleBloodfestMaxCartridgesHigh);
            s.ReignReady = true;
            score += ScoreAutoBloodfestWeave;
        }

        if (slot == nmFirstBuffedSlot - Planner74NoMercyWeaveOffsetSlots)
        {
            d.ForbidZoneBowAfterThisGCD = true;
            if (input.NoMercyUnlocked && slot >= s.NoMercyReadyWeaveSlot)
            {
                d.WeaveNoMercy = true;
                s.NoMercyReadyWeaveSlot = slot + Planner74Cooldown60Slots;
                s.NoMercySlotsLeft = s.NoMercyDurationSlots;
                score += ScoreAutoNoMercyWeave;
                if (d.GCD == Planner74GCD.WickedTalon)
                {
                    // No Mercy should be the first weave after Wicked Talon, so the Continuation proc is NM-buffed.
                    d.PreferContinuationAfterNoMercy = true;
                    score += ScoreNoMercyContinuationAnchor;
                }
            }
            else
            {
                score -= PenaltyInvalidNoMercyWeave;
            }
        }

        var reservedBurstWeaveSlot = fixedBFSlot || slot == nmFirstBuffedSlot - Planner74NoMercyWeaveOffsetSlots;
        var noMercyGCDsElapsed = s.NoMercySlotsLeft > 0 ? s.NoMercyDurationSlots - s.NoMercySlotsLeft : 0;
        var noMercyAtLeast2GCDsElapsed = s.NoMercySlotsLeft > 0 && noMercyGCDsElapsed >= 2;
        if (!reservedBurstWeaveSlot && s.ZonePotency > 0f && slot >= s.ZoneReadyWeaveSlot && (s.NoMercySlotsLeft <= 0 || noMercyAtLeast2GCDsElapsed))
        {
            d.WeaveZone = true;
            s.ZoneReadyWeaveSlot = slot + Planner74CDToGCDSlot(GameRuleZoneCooldown, input.GCDLength);
            score += s.ZonePotency * (s.NoMercySlotsLeft > 0 ? ScoreNoMercyMultiplier : 1f);
            score += Planner74SynergyScore(input.SynergyWindows, s.ZonePotency, slot);
        }

        if (s.BowShockPotency > 0f && slot >= s.BowShockReadyWeaveSlot && noMercyAtLeast2GCDsElapsed)
        {
            d.WeaveBowShock = true;
            s.BowShockReadyWeaveSlot = slot + Planner74CDToGCDSlot(GameRuleBowShockCooldown, input.GCDLength);
            score += s.BowShockPotency * ScoreNoMercyMultiplier;
            score += Planner74SynergyScore(input.SynergyWindows, s.BowShockPotency, slot);
        }

        return score;
    }

    private static float Planner74SlotEndPenalty(Planner74State s, int slot, int nmFirstBuffedSlot)
    {
        var penalty = 0f;
        var gfPreSlot = nmFirstBuffedSlot - Planner74PreNMGFOffsetSlots;
        if (slot == gfPreSlot && s.GFStep == 0)
            penalty -= PenaltyMissedPreNMGF;
        if (s.GFCharges >= GameRuleMaxGFCharges && slot < gfPreSlot && slot + s.GFChargeSlots <= gfPreSlot)
            penalty -= PenaltyGFChargeOvercapBeforeNM;
        if (s.NoMercySlotsLeft > 0 && s.Ammo >= GameRuleDoubleDownCartridgeCost && slot >= s.DoubleDownReadyGCDSlot)
            penalty -= PenaltyUnspentDoubleDownInNM;
        if (s.NoMercySlotsLeft > 0 && s.ReignStep == 1 && s.NoMercySlotsLeft <= 2) // Noble Blood + Lion Heart still pending
            penalty -= PenaltyUnfinishedReignComboInNM;
        if (s.NoMercySlotsLeft > 0 && s.ReignStep == 2 && s.NoMercySlotsLeft <= 1) // Lion Heart still pending
            penalty -= PenaltyUnfinishedReignComboInNM;
        if (Planner74NeedOvercapBurstStrike(s))
            penalty -= PenaltyOvercapBurstStrikeNeeded;
        return penalty;
    }

    private static float Planner74AdvanceAfterSlot(ref Planner74State s)
    {
        var penalty = 0f;
        if (s.ComboStep != 0 || s.AOEComboStep != 0)
        {
            s.ComboTimerSlotsLeft = Planner74Clamp(s.ComboTimerSlotsLeft - 1, 0, Planner74ComboTimerSlots);
            if (s.ComboTimerSlotsLeft <= 0)
            {
                s.ComboStep = 0;
                s.AOEComboStep = 0;
                penalty -= PenaltyComboDropped;
            }
        }

        if (s.BloodfestSlotsLeft > 0)
        {
            s.BloodfestSlotsLeft = Planner74Clamp(s.BloodfestSlotsLeft - 1, 0, Planner74BloodfestSlots + Planner74NoMercyWeaveOffsetSlots);
            if (s.BloodfestSlotsLeft <= 0)
            {
                s.MaxAmmo = GameRuleBaseMaxCartridgesHigh;
                if (s.Ammo > GameRuleBaseMaxCartridgesHigh)
                {
                    s.Ammo = GameRuleBaseMaxCartridgesHigh;
                    penalty -= PenaltyBloodfestAmmoTrim;
                }
            }
        }

        return penalty;
    }

    private static void Planner74NormalizeGFCharges(ref Planner74State s, int slot)
    {
        while (s.GFCharges < GameRuleMaxGFCharges && slot >= s.GFNextChargeSlot)
        {
            s.GFCharges++;
            s.GFNextChargeSlot = s.GFCharges < GameRuleMaxGFCharges ? s.GFNextChargeSlot + s.GFChargeSlots : Planner74FarSlot;
        }
        s.GFCharges = Planner74Clamp(s.GFCharges, 0, GameRuleMaxGFCharges);
    }

    private static void Planner74SpendGFCharge(ref Planner74State s, int slot)
    {
        if (s.GFCharges <= 0)
            return;

        var wasCapped = s.GFCharges >= GameRuleMaxGFCharges;
        s.GFCharges--;
        if (wasCapped || s.GFNextChargeSlot >= Planner74FarSlot)
            s.GFNextChargeSlot = slot + s.GFChargeSlots;
    }

    private static bool Planner74CanUseGF(Planner74State s, bool allowPendingNormalCombo = false)
        => s.GFCharges > 0 && s.Ammo >= 1 && s.GFStep == 0 && s.AOEComboStep == 0 && (s.ComboStep == 0 || (allowPendingNormalCombo && s.ComboStep == 2));
    private static bool Planner74CanUseLateBurstGF(Planner74State s)
        => s.GFCharges > 0 && s.Ammo >= 1 && s.GFStep == 0 && s.AOEComboStep == 0;
    private static bool Planner74ShouldStartOpeningBurstWithGF(Planner74State s)
        => s.OpeningDelayedSolidBurst &&
            s.NoMercySlotsLeft > 0 &&
            !s.OpeningBurstGFUsed &&
            s.GFStep == 0 &&
            s.ReignStep == 0 &&
            Planner74CanUseGF(s, allowPendingNormalCombo: true) &&
            !s.OpeningDelayedSolidGFComplete;
    private static bool Planner74BlockOpeningBurstGFStart(Planner74State s)
        => s.NoMercySlotsLeft > 0 && s.OpeningBurstGFUsed;
    private static bool Planner74CanUseNormalGFWithSafePendingCombo(Planner74State s)
        => Planner74CanUseGF(s);
    private static bool Planner74CanUseReign(Planner74State s) => s.ReignReady && s.ReignStep == 0;
    private static bool Planner74CanUseDoubleDown(Planner74State s, int slot) => s.Ammo >= GameRuleDoubleDownCartridgeCost && slot >= s.DoubleDownReadyGCDSlot;
    private static bool Planner74CanUseDoubleDownAfterDelay(Planner74State s, int slot)
        => Planner74CanUseDoubleDown(s, slot) && s.NoMercySlotsLeft > 0 && s.NoMercyDurationSlots - s.NoMercySlotsLeft >= Planner74BurstOrderDDDelaySlots;
    private static bool Planner74CanUseSonicBreakAfterDelay(Planner74State s, int slot)
        => slot >= s.SonicBreakReadyGCDSlot && s.NoMercySlotsLeft > 0 && s.NoMercyDurationSlots - s.NoMercySlotsLeft >= 1;
    private static bool Planner74ShouldPreserveAmmoForDoubleDown(Planner74State s, int slot)
        => s.NoMercySlotsLeft > 0 &&
            s.Ammo >= GameRuleDoubleDownCartridgeCost &&
            s.DoubleDownReadyGCDSlot > slot &&
            s.DoubleDownReadyGCDSlot <= slot + 1;
    private static bool Planner74NeedOvercapBurstStrike(Planner74State s) => s.MaxAmmo > 0 && (s.ComboStep == 2 || s.AOEComboStep == 1) && s.Ammo >= s.MaxAmmo;
    private static bool Planner74WouldWasteSolidBarrelCartridge(Planner74State s) => s.MaxAmmo > 0 && s.ComboStep == 2 && s.Ammo >= s.MaxAmmo;
    private static bool Planner74WouldWasteDemonSlaughterCartridge(Planner74State s) => s.MaxAmmo > 0 && s.AOEComboStep == 1 && s.Ammo >= s.MaxAmmo;
    private static bool Planner74HasExtraBurstAmmo(Planner74State s) => s.Ammo >= 4;
    private static bool Planner74WantsNormalAOE(in Planner74Input input) => input.TargetsIn5y >= Math.Max(2, input.NormalAOEThreshold);
    private static bool Planner74WantsFatedCircleAOE(in Planner74Input input) => input.FatedCircleUnlocked && input.TargetsIn5y >= 2;
    private static bool Planner74ShouldAvoidGFForAOE(in Planner74Input input) => input.TargetsIn5y >= 3;
    private static bool Planner74ShouldUseBurstStrikeBeforeSolidBarrel(Planner74State s, int slot, int nmFirstBuffedSlot)
    {
        if (!Planner74WouldWasteSolidBarrelCartridge(s))
            return false;

        if (s.NoMercySlotsLeft > 0 || nmFirstBuffedSlot <= 0)
            return true;

        var latestPreGFSlot = nmFirstBuffedSlot - 1;
        if (slot >= latestPreGFSlot)
            return Planner74HasExtraBurstAmmo(s);

        return slot < latestPreGFSlot;
    }
    private static bool Planner74ShouldUseBurstStrikeForBrokenPreNMFallback(Planner74State s, int slot, int nmFirstBuffedSlot)
    {
        if (nmFirstBuffedSlot <= 0 || s.NoMercySlotsLeft > 0 || s.GFStep != 0 || s.ReignStep != 0 || !Planner74HasExtraBurstAmmo(s))
            return false;

        var latestPreGFSlot = nmFirstBuffedSlot - 1;
        return slot >= latestPreGFSlot && !Planner74CanStartPreNMGF(s);
    }
    private static int Planner74NormalComboGCDsToFinish(Planner74State s) => s.ComboStep switch
    {
        1 => 2,
        2 => 1,
        _ => 3,
    };
    private static bool Planner74ShouldUseBurstStrikeForPreNMGFAlignment(Planner74State s, int slot, int nmFirstBuffedSlot)
    {
        if (nmFirstBuffedSlot <= 0 || s.NoMercySlotsLeft > 0 || s.GFStep != 0 || s.ReignStep != 0 || s.Ammo <= 0)
            return false;

        var gfPreSlot = nmFirstBuffedSlot - 3;
        var slotsUntilPreGF = gfPreSlot - slot;
        if (slotsUntilPreGF is <= 0 or > 6)
            return false;

        var gcdsToFinishCombo = Planner74NormalComboGCDsToFinish(s);
        if (slot + gcdsToFinishCombo > gfPreSlot)
            return s.ComboStep == 0 && s.Ammo - slotsUntilPreGF >= 1;

        return s.MaxAmmo > 0 && s.Ammo >= s.MaxAmmo && slot + 1 + gcdsToFinishCombo <= gfPreSlot;
    }
    private static bool Planner74CanStartPreNMGF(Planner74State s) => Planner74CanUseNormalGFWithSafePendingCombo(s);
    private static bool Planner74ShouldForceLateBurstGF(Planner74State s)
        => s.NoMercySlotsLeft is >= 1 and <= 3 &&
            !s.OpeningBurstGFUsed &&
            !s.ReignReady &&
            s.ReignStep == 0 &&
            Planner74CanUseLateBurstGF(s);
    private static bool Planner74ShouldHoldForLateBurstGF(Planner74State s, int slot)
    {
        if (s.NoMercySlotsLeft <= 0 || s.GFStep != 0 || s.ReignStep != 0 || s.ReignReady || s.ComboStep != 0 || s.Ammo < 1 || s.GFCharges > 0)
            return false;

        var slotsUntilGF = s.GFNextChargeSlot - slot;
        return slotsUntilGF is > 0 and <= 1 && s.NoMercySlotsLeft - slotsUntilGF >= 1;
    }
    private static bool Planner74ShouldUseBasicBurstGapGuard(Planner74State s, int slot, int nmFirstBuffedSlot)
    {
        if (nmFirstBuffedSlot <= 0 || slot >= nmFirstBuffedSlot || s.GFStep != 0 || s.ReignStep != 0)
            return false;

        var gfPreSlot = nmFirstBuffedSlot - 3;
        var latestPreGFSlot = nmFirstBuffedSlot - 1;
        if (slot >= latestPreGFSlot)
            return !Planner74CanStartPreNMGF(s);

        var gcdsToFinishCombo = Planner74NormalComboGCDsToFinish(s);
        return slot + gcdsToFinishCombo > latestPreGFSlot;
    }

    private static bool Planner74CanUseNormalGFWithoutBreakingNextNM(Planner74State s, int slot, int nmFirstBuffedSlot)
    {
        if (!Planner74CanUseNormalGFWithSafePendingCombo(s) || nmFirstBuffedSlot <= 0)
            return false;

        var gfPreSlot = nmFirstBuffedSlot - 3;
        if (slot >= gfPreSlot)
            return false;

        var sim = s;
        Planner74SpendGFCharge(ref sim, slot);
        var lateSecondGFStart = nmFirstBuffedSlot + Math.Max(0, s.NoMercyDurationSlots - 3);
        var lateSecondGFPrepDeadline = lateSecondGFStart - s.GFChargeSlots;

        if (slot >= lateSecondGFPrepDeadline && sim.GFCharges <= 0)
            return false;

        for (var t = slot + 1; t <= gfPreSlot; ++t)
        {
            Planner74NormalizeGFCharges(ref sim, t);
            if (t >= lateSecondGFPrepDeadline && sim.GFCharges <= 0)
                return false;
        }

        if (sim.GFCharges <= 0)
            return false;

        Planner74SpendGFCharge(ref sim, gfPreSlot);

        for (var t = gfPreSlot + 1; t <= lateSecondGFStart; ++t)
            Planner74NormalizeGFCharges(ref sim, t);

        return sim.GFCharges > 0;
    }

    private static bool Planner74ShortNoMercyWindow(in Planner74Input input, in Planner74State s, int slot)
    {
        if (s.NoMercySlotsLeft <= 0)
            return false;

        var targetableSlotsLeft = Planner74TargetableSlotsLeft(input.TargetableBySlot, slot, s.NoMercySlotsLeft);
        if (targetableSlotsLeft < 3)
            return true;

        if (input.EstimatedFightEnd == float.MaxValue || input.GCDLength <= 0f)
            return false;

        return input.CombatTime + input.GCDLength * (slot + Planner74PreNMGFOffsetSlots) > input.EstimatedFightEnd - TimingFightEndSlotBuffer;
    }

    private static int Planner74TargetableSlotsLeft(bool[]? targetableBySlot, int startSlot, int maxSlots)
    {
        if (targetableBySlot == null || targetableBySlot.Length == 0)
            return maxSlots;

        var count = 0;
        var limit = Math.Min(targetableBySlot.Length, startSlot + Math.Max(0, maxSlots));
        for (var slot = startSlot; slot < limit; ++slot)
        {
            if (!targetableBySlot[slot])
                break;
            ++count;
        }
        return count;
    }

    private static float Planner74BalanceBurstOrderBonus(Planner74State s, Planner74GCD gcd, int slot, int nmFirstBuffedSlot)
    {
        if (s.NoMercySlotsLeft <= 0)
            return 0f;

        var nmSlot = slot - nmFirstBuffedSlot;
        return gcd switch
        {
            Planner74GCD.ReignOfBeasts when nmSlot <= Planner74NoMercyWeaveOffsetSlots => ScoreBalanceEarlyReign,
            Planner74GCD.DoubleDown when s.ReignStep == 1 && nmSlot <= Planner74BurstOrderDDDelaySlots => ScoreBalanceDDBeforeReignContinuation,
            Planner74GCD.SonicBreak when s.ReignStep == 1 && nmSlot <= Planner74PreNMGFOffsetSlots => ScoreBalanceSonicBeforeReignContinuation,
            Planner74GCD.NobleBlood when s.ReignStep == 1 && slot < s.DoubleDownReadyGCDSlot && slot < s.SonicBreakReadyGCDSlot => ScoreBalanceNobleBeforeDDOrSonic,
            Planner74GCD.NobleBlood when s.ReignStep == 1 && nmSlot >= Planner74BurstOrderDDDelaySlots => ScoreBalanceLateNoble,
            Planner74GCD.LionHeart when s.ReignStep == 2 => ScoreBalanceLionHeart,
            Planner74GCD.GnashingFang when !s.ReignReady && s.ReignStep == 0 => ScoreBalanceGnashingAfterReign,
            _ => 0f,
        };
    }

    private static Planner74GCD Planner74NextNormalGCD(Planner74State s) => s.ComboStep == 1 ? Planner74GCD.BrutalShell : s.ComboStep == 2 ? Planner74GCD.SolidBarrel : Planner74GCD.KeenEdge;
    private static Planner74GCD Planner74NextNormalGCDWithOvercapProtection(in Planner74Input input, in Planner74State s, int slot, int nmFirstBuffedSlot)
        => input.BurstStrikeUnlocked && Planner74ShouldUseBurstStrikeBeforeSolidBarrel(s, slot, nmFirstBuffedSlot) ? Planner74GCD.BurstStrike : Planner74NextNormalGCD(s);
    private static Planner74GCD Planner74NextAOEGCD(in Planner74Input input, in Planner74State s) => !input.DemonSliceUnlocked ? Planner74NextNormalGCD(s) : s.AOEComboStep == 1 && input.DemonSlaughterUnlocked ? Planner74GCD.DemonSlaughter : Planner74GCD.DemonSlice;
    private static Planner74GCD Planner74NextFillerGCD(in Planner74Input input, in Planner74State s, int slot, int nmFirstBuffedSlot)
    {
        if ((Planner74WantsNormalAOE(input) && s.ComboStep == 0) || s.AOEComboStep > 0)
        {
            if (input.BurstStrikeUnlocked && Planner74WouldWasteDemonSlaughterCartridge(s) && Planner74WantsFatedCircleAOE(input))
                return Planner74GCD.FatedCircle;
            return Planner74NextAOEGCD(input, s);
        }

        return Planner74NextNormalGCDWithOvercapProtection(input, s, slot, nmFirstBuffedSlot);
    }

    private static float Planner74BasePotency(Planner74GCD gcd) => gcd switch
    {
        Planner74GCD.KeenEdge => GamePotencyKeenEdge,
        Planner74GCD.BrutalShell => GamePotencyBrutalShell,
        Planner74GCD.SolidBarrel => GamePotencySolidBarrel,
        Planner74GCD.BurstStrike => GamePotencyBurstStrike,
        Planner74GCD.DemonSlice => GamePotencyDemonSlice,
        Planner74GCD.DemonSlaughter => GamePotencyDemonSlaughter,
        Planner74GCD.FatedCircle => GamePotencyFatedCircle,
        Planner74GCD.GnashingFang => GamePotencyGnashingFang,
        Planner74GCD.SavageClaw => GamePotencySavageClaw,
        Planner74GCD.WickedTalon => GamePotencyWickedTalon,
        Planner74GCD.DoubleDown => GamePotencyDoubleDown,
        Planner74GCD.SonicBreak => GamePotencySonicBreak,
        Planner74GCD.ReignOfBeasts => GamePotencyReignOfBeasts,
        Planner74GCD.NobleBlood => GamePotencyNobleBlood,
        Planner74GCD.LionHeart => GamePotencyLionHeart,
        _ => 0f,
    };

    // the Continuation a GCD grants; it is lost when no ability can be weaved before the next weaponskill
    private static float Planner74ContinuationPotency(Planner74GCD gcd) => gcd switch
    {
        Planner74GCD.GnashingFang => GamePotencyJugularRip,
        Planner74GCD.SavageClaw => GamePotencyAbdomenTear,
        Planner74GCD.WickedTalon => GamePotencyEyeGouge,
        Planner74GCD.BurstStrike => GamePotencyHypervelocity,
        Planner74GCD.FatedCircle => GamePotencyFatedBrand,
        _ => 0f,
    };

    private static float Planner74ScoreValue(in Planner74Input input, Planner74GCD gcd)
    {
        var basePotency = Planner74BasePotency(gcd);
        var targets = Math.Max(1, input.TargetsIn5y);
        return gcd is Planner74GCD.DemonSlice or Planner74GCD.DemonSlaughter or Planner74GCD.FatedCircle or Planner74GCD.DoubleDown
            ? basePotency * targets
            : basePotency;
    }

    private static float Planner74EffectiveSynergyPotency(Planner74GCD gcd) => gcd switch
    {
        Planner74GCD.BurstStrike => ScoreEffectiveBurstStrikeWithContinuation,
        Planner74GCD.FatedCircle => ScoreEffectiveFatedCircleWithContinuation,
        Planner74GCD.GnashingFang => ScoreEffectiveGnashingFangWithContinuation,
        Planner74GCD.SavageClaw => ScoreEffectiveSavageClawWithContinuation,
        Planner74GCD.WickedTalon => ScoreEffectiveWickedTalonWithContinuation,
        _ => Planner74BasePotency(gcd),
    };

    private static float Planner74SynergyScore(Planner74SynergyWindow[]? windows, Planner74GCD gcd, int slot)
        => Planner74SynergyScore(windows, Planner74EffectiveSynergyPotency(gcd), slot);

    private static float Planner74SynergyScore(Planner74SynergyWindow[]? windows, float potency, int slot)
    {
        if (windows == null || windows.Length == 0)
            return 0f;

        var weight = 0f;
        for (var i = 0; i < windows.Length; ++i)
            if (windows[i].Covers(slot))
                weight += windows[i].Weight;

        return potency * weight * Planner74SynergyScoreScale;
    }

    private static float Planner74NoMercyDelayPenalty(in Planner74Input input, int nmFirstBuffedSlot)
    {
        if (!input.OptimizeRaidSynergy || input.NoMercySlotsLeft > 0 || input.NextNoMercyFirstBuffedSlot > 0 || input.GCDLength <= 0f)
            return 0f;

        var earliest = Planner74Clamp(input.NoMercyReadyWeaveSlot + 1, 0, Planner74HorizonSlots + 1);
        var delaySlots = Math.Max(0, nmFirstBuffedSlot - earliest);
        var penalty = delaySlots * PenaltyNoMercyDelayPerSlot;
        if (delaySlots <= 0 || input.EstimatedFightEnd == float.MaxValue)
            return penalty;

        var earliestNextNM = input.CombatTime + (earliest - Planner74NoMercyWeaveOffsetSlots) * input.GCDLength + GameRuleNoMercyCycle;
        var delayedNextNM = input.CombatTime + (nmFirstBuffedSlot - Planner74NoMercyWeaveOffsetSlots) * input.GCDLength + GameRuleNoMercyCycle;
        if (earliestNextNM <= input.EstimatedFightEnd - TimingFightEndLossBuffer && delayedNextNM > input.EstimatedFightEnd - TimingFightEndLossBuffer)
            penalty += PenaltyNoMercyLostToFightEnd;

        return penalty;
    }

    private static void Planner74AddUnique(System.Collections.Generic.List<Planner74GCD> list, Planner74GCD gcd)
    {
        for (var i = 0; i < list.Count; ++i)
            if (list[i] == gcd)
                return;
        list.Add(gcd);
    }

    private static int Planner74Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;
    private static int Planner74CDToGCDSlot(float cd, float gcd) => cd <= 0 ? 0 : Planner74Clamp((int)MathF.Ceiling(cd / gcd), 0, Planner74FarSlot);
    private static int Planner74CDToWeaveSlot(float cd, float gcd) => cd <= 0 ? 0 : Planner74Clamp((int)MathF.Ceiling((cd - TimingWeaveSlotLead) / gcd), 0, Planner74FarSlot);
    private static int Planner74CDToLateWeaveSlot(float cd, float gcd) => cd <= 0 ? 0 : Planner74Clamp((int)MathF.Ceiling((cd - gcd + TimingLateWeaveReserve) / gcd), 0, Planner74FarSlot);
    private static int Planner74BuffLeftToSlots(float buffLeft, float gcd) => buffLeft <= 0 ? 0 : Planner74Clamp((int)MathF.Ceiling(buffLeft / gcd), 0, Planner74ComboTimerSlots);
    private static int Planner74NoMercyDurationSlotsForGCD(float gcd) => gcd <= Planner74NoMercyReliableFastGCD ? Planner74NoMercyFastSlots : Planner74NoMercySlots;
    private static bool Planner74SlotTargetable(bool[]? targetableBySlot, int slot) => targetableBySlot == null || targetableBySlot.Length == 0 ? true : targetableBySlot[Planner74Clamp(slot, 0, targetableBySlot.Length - 1)];

    private static string Planner74FormatDebug(Planner74Decision[] plan, float score, int nmFirstBuffedSlot)
    {
        var sb = new System.Text.StringBuilder();
        var gfPreSlot = nmFirstBuffedSlot - Planner74PreNMGFOffsetSlots;
        var bloodfestWeaveSlot = nmFirstBuffedSlot - Planner74BloodfestWeaveOffsetSlots;
        var noMercyWeaveSlot = nmFirstBuffedSlot - Planner74NoMercyWeaveOffsetSlots;
        var selectedGCD = plan.Length > 0 ? plan[0].GCD : Planner74GCD.None;
        sb.Append("24GCD Planner score=").Append(score.ToString("F1"))
            .Append(", NMFirstBuffedSlot=").Append(nmFirstBuffedSlot)
            .Append(", gfPreSlot=").Append(gfPreSlot)
            .Append(", BloodfestWeaveSlot=").Append(bloodfestWeaveSlot)
            .Append(", NoMercyWeaveSlot=").Append(noMercyWeaveSlot)
            .Append(", selectedGCD=").Append(selectedGCD)
            .AppendLine();
        var count = Math.Min(plan.Length, Planner74HorizonSlots);
        for (var i = 0; i < count; ++i)
        {
            sb.Append("S").Append(i.ToString($"D{Planner74DebugSlotDigits}")).Append(": ").Append(plan[i].GCD);
            if (plan[i].WeaveBloodfest)
                sb.Append(" +BF");
            if (plan[i].WeaveNoMercy)
                sb.Append(" +NM");
            if (plan[i].WeaveZone)
                sb.Append(" +Zone");
            if (plan[i].WeaveBowShock)
                sb.Append(" +Bow");
            if (plan[i].PreferContinuationAfterNoMercy)
                sb.Append(" +NM->Continuation");
            if (plan[i].ForbidZoneBowAfterThisGCD)
                sb.Append(" [No Zone/Bow]");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private float BloodfestStartedAt()
    {
        if (HasBF)
            return CombatTimer - MathF.Max(0f, GameRuleBloodfestDuration - Status(SID.Bloodfest));

        return LastActionUsed(AID.Bloodfest) ? CombatTimer : float.NaN;
    }

    private static bool BloodfestInEvenMinuteWindow(float bloodfestAt)
    {
        if (bloodfestAt < PotionEvenMinuteEarliest)
            return false;

        var mod120 = bloodfestAt % PotionEvenMinuteCycle;
        return mod120 <= PotionBloodfestWindowTolerance || mod120 >= PotionEvenMinuteEarliest;
    }

    private bool BloodfestPotionWindow(float? targetBloodfestAt = null, bool requireEvenMinute = false, float? forcedBloodfestAt = null)
    {
        if (!Player.InCombat || Player.IsDead || !CanWeaveIn || (!HasBF && !LastActionUsed(AID.Bloodfest)))
            return false;

        var bloodfestAt = forcedBloodfestAt ?? BloodfestStartedAt();
        if (float.IsNaN(bloodfestAt))
            return false;

        if (targetBloodfestAt is float target && (bloodfestAt < target - PotionBloodfestWindowTolerance || bloodfestAt > target + PotionBloodfestWindowTolerance))
            return false;

        return !requireEvenMinute || BloodfestInEvenMinuteWindow(bloodfestAt);
    }

    private bool TimedBloodfestPotionWindow(float targetBloodfestAt) => BloodfestPotionWindow(targetBloodfestAt);

    private void UpdateDeathDelayedBurst()
    {
        var raisePenalty = HasRaiseOrWeaknessPenalty;

        if (!Player.InCombat)
        {
            WasDead = Player.IsDead;
            AlignBurstAfterDeath = false;
            DeathStartedWithNM = false;
            DeadSince = 0f;
            DelayedBurstAt = 0f;
            return;
        }

        if (Player.IsDead && !WasDead)
        {
            DeadSince = CombatTimer;
            DeathStartedWithNM = HasNM;
            if (DeathStartedWithNM)
            {
                AlignBurstAfterDeath = true;
                DelayedBurstAt = NextMinuteBurstAfter(CombatTimer);
            }
        }

        var deathStart = DeadSince > 0f ? DeadSince : CombatTimer;
        var missedBurstAt = (Player.IsDead || raisePenalty) && !HasNM && !DeathStartedWithNM ? MissedBurstNoMercyAt(deathStart, CombatTimer) : 0f;
        if (missedBurstAt > 0f)
        {
            AlignBurstAfterDeath = true;
            DelayedBurstAt = missedBurstAt + GameRuleNoMercyCycle;
        }

        if (HasDeathDelayedBurst)
        {
            while (DelayedBurstAt <= CombatTimer)
                DelayedBurstAt += GameRuleNoMercyCycle;
        }

        WasDead = Player.IsDead;

        if (HasDeathDelayedBurst && HasNM && CombatTimer >= DelayedBurstAt)
        {
            AlignBurstAfterDeath = false;
            DeathStartedWithNM = false;
            DeadSince = 0f;
            DelayedBurstAt = 0f;
        }
        else if (!Player.IsDead && !raisePenalty && !HasDeathDelayedBurst)
        {
            DeathStartedWithNM = false;
            DeadSince = 0f;
        }
    }

    private static float NextMinuteBurstAfter(float combatTime)
        => MathF.Ceiling((combatTime + TimingBurstScheduleNudge) / GameRuleNoMercyCycle) * GameRuleNoMercyCycle;

    private float MissedBurstNoMercyAt(float deathStart, float now)
    {
        if (now < TimingDeathRecoveryFirstRelevant)
            return 0f;

        var firstRelevantBurst = MathF.Max(GameRuleNoMercyCycle, deathStart - SkSGCDLength * Planner74PreNMGFOffsetSlots);
        var burstAt = MathF.Ceiling(firstRelevantBurst / GameRuleNoMercyCycle) * GameRuleNoMercyCycle;
        return burstAt <= now + SkSGCDLength ? burstAt : 0f;
    }

    private void ResetFightEstimate()
    {
        EstimatedFightEnd = float.MaxValue;
        PendingHPRatio = 0f;
        PendingHPTime = 0f;
        EstimatedHPDrain = 0f;
        FightEstimateTargetID = 0ul;
    }

    private void UpdateFightEstimate(Actor? target)
    {
        if (!Player.InCombat || target == null || target.IsDead || target.HPMP.MaxHP == 0)
        {
            ResetFightEstimate();
            return;
        }

        var hpRatio = (float)target.HPMP.CurHP / target.HPMP.MaxHP;
        if (FightEstimateTargetID != target.InstanceID || PendingHPTime <= 0f)
        {
            FightEstimateTargetID = target.InstanceID;
            PendingHPRatio = hpRatio;
            PendingHPTime = CombatTimer;
            EstimatedFightEnd = float.MaxValue;
            EstimatedHPDrain = 0f;
            return;
        }

        var elapsed = CombatTimer - PendingHPTime;
        if (elapsed < TimingFightEstimateMinSample)
            return;

        if (hpRatio < PendingHPRatio)
        {
            var hpDrain = (PendingHPRatio - hpRatio) / elapsed;
            EstimatedHPDrain = EstimatedHPDrain > 0f ? EstimatedHPDrain * TimingFightEstimateBlendOld + hpDrain * TimingFightEstimateBlendNew : hpDrain;
            EstimatedFightEnd = EstimatedHPDrain > TimingFightEstimateMinDrain ? CombatTimer + hpRatio / EstimatedHPDrain : float.MaxValue;
        }
        else
        {
            EstimatedFightEnd = float.MaxValue;
            EstimatedHPDrain = 0f;
        }

        PendingHPRatio = hpRatio;
        PendingHPTime = CombatTimer;
    }

    private bool EndBurnWindow() => EstimatedFightEnd - CombatTimer is > 0f and <= TimingFightEndBurnWindow;

    private bool FightEndBurnEnabled(StrategyValues strategy)
        => strategy.Option(Track.FightEnd).As<FightEndStrategy>() == FightEndStrategy.SpendResources && EndBurnWindow();

    private bool ShouldHoldNormalGCDForBasicBurstGap()
    {
        if (HasNM || !Player.InCombat || WantAOE || GunComboStep != 0 || Ammo <= 0)
            return false;

        var nmFirstBuffedSlot = Planner74CDToLateWeaveSlot(NMcd, SkSGCDLength) + 1;
        var gfPreSlot = nmFirstBuffedSlot - Planner74PreNMGFOffsetSlots;
        if (gfPreSlot < 0)
            return true;

        var comboStep = ComboLastMove switch
        {
            AID.KeenEdge => 1,
            AID.BrutalShell => 2,
            _ => 0,
        };
        var gcdsToFinishCombo = comboStep switch
        {
            1 => 2,
            2 => 1,
            _ => 3,
        };

        return gcdsToFinishCombo > gfPreSlot;
    }

    private float NextReservedNoMercyCombatTime()
    {
        if (HasDeathDelayedBurst)
            return DelayedBurstAt;

        if (HasNM)
            return CombatTimer;

        if (NMcd > 0f)
            return CombatTimer + NMcd;

        return CombatTimer;
    }

    private void UpdateBurstGFReservation()
    {
        ReservedBurstNoMercyAt = NextReservedNoMercyCombatTime();
    }

    private bool CanSpendGFWithoutBreakingBurstReservation(float noMercyAtCombatTime)
    {
        if (NMstatus > 0f || !Player.InCombat)
            return false;

        var noMercyIn = noMercyAtCombatTime - CombatTimer;
        if (noMercyIn <= 0f)
            return false;

        var nmFirstBuffedSlot = Planner74CDToLateWeaveSlot(noMercyIn, SkSGCDLength) + 1;
        var rawGF = DebugActionChargesRaw(AID.GnashingFang);
        var gfNextCharge = rawGF.CurrentCharges >= GameRuleMaxGFCharges || !rawGF.RecastActive ? Planner74FarSlot : Planner74CDToGCDSlot(MathF.Max(0f, rawGF.RecastTotal - rawGF.RecastElapsed), SkSGCDLength);
        var comboStep = ComboLastMove switch
        {
            AID.KeenEdge => 1,
            AID.BrutalShell => 2,
            _ => 0,
        };
        var state = new Planner74State
        {
            ComboStep = comboStep,
            ComboTimerSlotsLeft = comboStep > 0 ? Planner74BuffLeftToSlots(ActualComboTimer, SkSGCDLength) : 0,
            GFStep = GunComboStep is 1 or 2 ? GunComboStep : 0,
            ReignStep = GunComboStep is 3 or 4 ? GunComboStep - 2 : 0,
            Ammo = Ammo,
            MaxAmmo = MaxCartridges,
            GFCharges = rawGF.CurrentCharges,
            GFNextChargeSlot = gfNextCharge,
            GFChargeSlots = Planner74CDToGCDSlot(GameRuleGnashingFangChargeTime, SkSGCDLength),
            NoMercyDurationSlots = Planner74NoMercyDurationSlotsForGCD(SkSGCDLength),
        };

        Planner74NormalizeGFCharges(ref state, 0);
        return Planner74CanUseNormalGFWithoutBreakingNextNM(state, 0, nmFirstBuffedSlot);
    }

    private bool CanUseNormalGFOvercapWithoutBreakingNextNMBurst()
        => CanSpendGFWithoutBreakingBurstReservation(ReservedBurstNoMercyAt > 0f ? ReservedBurstNoMercyAt : NextReservedNoMercyCombatTime());

    private bool ShouldSpendGFOvercapBeforeReservedBurst(bool allowDuringBurstHold)
    {
        if (HasNM || Player.IsDead || !Player.InCombat)
            return false;

        var reservedNoMercyAt = ReservedBurstNoMercyAt > 0f ? ReservedBurstNoMercyAt : NextReservedNoMercyCombatTime();
        if (reservedNoMercyAt <= CombatTimer || CombatTimer >= reservedNoMercyAt - SkSGCDLength * Planner74PreNMGFOffsetSlots)
            return false;

        var rawGF = DebugActionChargesRaw(AID.GnashingFang);
        var nextChargeIn = rawGF.RecastActive ? MathF.Max(0f, rawGF.RecastTotal - rawGF.RecastElapsed) : float.MaxValue;
        var willOvercapSoon = rawGF.CurrentCharges >= GameRuleMaxGFCharges || rawGF.CurrentCharges == 1 && nextChargeIn <= SkSGCDLength * TimingGFOvercapSoonGCDs;
        return willOvercapSoon && (allowDuringBurstHold || !HasDeathDelayedBurst) && CanSpendGFWithoutBreakingBurstReservation(reservedNoMercyAt);
    }

    private bool PotionAfterBurstSpendWindow()
    {
        if (HasNM || HasBF || Player.IsDead || !Player.InCombat || CombatTimer < TimingOpenerEnd || PotionStatusLeft() <= MathF.Max(PotionPostBurstMinStatusLeft, GCD + TimingReadyEpsilon))
            return false;

        var nextNoMercyAt = NextReservedNoMercyCombatTime();
        var nextNoMercyIn = nextNoMercyAt - CombatTimer;
        if (nextNoMercyIn < PotionPostBurstMinNextNoMercy)
            return false;

        return BFcd <= nextNoMercyIn + SkSGCDLength;
    }

    private bool ShouldUsePotion(StrategyValues strategy)
    {
        var potion = strategy.Option(Track.Potion).As<PotionStrategyAkechi>();
        if (strategy.Option(Track.RotationMode).As<RotationModeStrategy>() == RotationModeStrategy.NormalOvercapOnly)
            return false;

        return potion switch
        {
            PotionStrategyAkechi.Manual => false,
            PotionStrategyAkechi.Immediate => BloodfestPotionWindow(),
            PotionStrategyAkechi.AlignWithBuffs => BloodfestPotionWindow(),
            PotionStrategyAkechi.AlignWithRaidBuffs => BloodfestPotionWindow() && (RaidBuffsIn <= SkSGCDLength * TimingRaidBuffLookaheadGCDs || RaidBuffsLeft > GCD),
            PotionStrategyAkechi.EvenMinuteNMOnly => BloodfestPotionWindow(requireEvenMinute: true),
            PotionStrategyAkechi.SixMinuteNM3GCD => TimedBloodfestPotionWindow(PotionSixMinuteTarget),
            PotionStrategyAkechi.EightMinuteNM3GCD => TimedBloodfestPotionWindow(PotionEightMinuteTarget),
            PotionStrategyAkechi.TwoAndEightMinuteNM3GCD => TimedBloodfestPotionWindow(PotionTwoMinuteTarget) || TimedBloodfestPotionWindow(PotionEightMinuteTarget),
            _ => false
        };
    }

    private OGCDPriority ContinuationPrio()
    {
        var prio = ChangePriority(200, 749);
        //standard procs
        if (HasRip || HasTear || HasGouge)
            return prio;

        //these require some tinkering because of the synergy with No Mercy & SkS
        if (HasBlast || HasRaze)
        {
            //if slow, send after NM - else just send it
            return Slow ? prio : prio + 500;
        }
        return OGCDPriority.None;
    }
    private OGCDPriority SkSPrio(OGCDPriority slow, OGCDPriority notslow) => Slow ? slow : Fast ? notslow : OGCDPriority.None;
    private OGCDPriority BowPrio => SkSPrio(OGCDPriority.High, OGCDPriority.ModeratelyHigh);
    private OGCDPriority ZonePrio => SkSPrio(OGCDPriority.ModeratelyHigh, OGCDPriority.High);
    private OGCDPriority ZonePrioInNM => OGCDPriority.VeryHigh;

    public override void Execution(StrategyValues strategy, Enemy? primaryTarget)
    {
        UpdateMechanicForecast(strategy.Option(Track.MechanicHints).As<MechanicHintStrategy>());
        Locks = ActionLocks.Read(Player, World.CurrentTime);
        // Apply the shared Targeting setting before any target-dependent rotation logic.
        GetNextTarget(strategy, ref primaryTarget, 3f); // melee range, not a target-count threshold

        UpdateDeathDelayedBurst();
        NMstatus = Status(SID.NoMercy, GameRuleNoMercyDuration);
        // track how long No Mercy has been sitting ready but unused (BurstReady/Together/synergy waits), so Zone alignment can give up instead of starving
        if (HasNM || NMcd > 0f || !Player.InCombat)
            NoMercyReadySince = -1f;
        else if (NoMercyReadySince < 0f)
            NoMercyReadySince = CombatTimer;
        SBstatus = Status(SID.ReadyToBreak, GameRuleReadyToBreakDuration);
        Rstatus = Status(SID.ReadyToReign, GameRuleReadyToReignDuration);
        var nmActive = NMstatus > 0;
        if (!Player.InCombat || Player.IsDead || nmActive || !HasBF)
            PostNoMercyGFDumpPending = false;
        else if (WasNoMercyActive && HasGF1 && GFCharges == 1)
            PostNoMercyGFDumpPending = true;
        else if (LastActionUsed(AID.GnashingFang) || !HasGF1 || GFCharges != 1)
            PostNoMercyGFDumpPending = false;
        WasNoMercyActive = nmActive;

        if (!Player.InCombat || Player.IsDead || CombatTimer >= TimingOpenerEnd || !nmActive)
            OpeningBurstGFUsedInNM = false;
        else if (LastActionUsed(AID.GnashingFang) || GunComboStep is 1 or 2 || ComboLastMove is AID.GnashingFang or AID.SavageClaw or AID.WickedTalon)
            OpeningBurstGFUsedInNM = true;

        if (!nmActive)
            UsedGFComboInNM = false;
        else if (LastActionUsed(AID.GnashingFang) && !IsOpeningDelayedSolidGFStart())
            UsedGFComboInNM = true;
        (BestSplashTargets, NumSplashTargets) = GetBestTarget(strategy, primaryTarget, AOESplashRadius, IsSplashTarget);
        BestSplashTarget = Unlocked(AID.ReignOfBeasts) && NumSplashTargets > 1 ? BestSplashTargets : primaryTarget;
        BestDOTTarget = null;
        var bestDOTTargetHP = default(float);
        var targets = Hints.PriorityTargetsSpan;
        var len = targets.Length;
        for (var i = 0; i < len; ++i)
        {
            var target = targets[i];
            if (Player.DistanceToHitbox(target.Actor) > AOESplashRadius)
                continue;

            var hp = (float)target.Actor.HPMP.CurHP / target.Actor.HPMP.MaxHP;
            if (BestDOTTarget == null || hp.CompareTo(bestDOTTargetHP) > 0)
            {
                BestDOTTarget = target;
                bestDOTTargetHP = hp;
            }
        }
        var aoe = strategy.Option(Track.AOE);
        var aoeStrat = aoe.As<AOEStrategy>();
        var cartsStratSetting = strategy.Option(Track.Cartridges).As<CartridgeStrategy>();
        if (cartsStratSetting == CartridgeStrategy.NormalOvercapOnly)
            cartsStratSetting = CartridgeStrategy.Automatic;
        var rotationMode = strategy.Option(Track.RotationMode).As<RotationModeStrategy>();
        var normalOvercapOnly = rotationMode == RotationModeStrategy.NormalOvercapOnly;
        if (normalOvercapOnly || !Player.InCombat || Player.IsDead || HasNM)
            FullModeBurstResumeUntil = 0f;
        else if (LastRotationMode == RotationModeStrategy.NormalOvercapOnly)
            FullModeBurstResumeUntil = CombatTimer + SkSGCDLength * TimingFullModeResumeGCDs;
        LastRotationMode = rotationMode;
        ForceAOE = aoeStrat is AOEStrategy.ForceAOEFinishWithoutOvercap or AOEStrategy.ForceAOEBreakWithoutOvercap or AOEStrategy.ForceAOEFinishWithOvercap or AOEStrategy.ForceAOEBreakWithOvercap;
        WantNormalAOE = Unlocked(AID.DemonSlice) && (TargetsInAOECircle(AOEMeleeRadius, NormalAOEThreshold()) || ForceAOE);
        WantFatedCircleAOE = Unlocked(AID.FatedCircle) && (TargetsInAOECircle(AOEMeleeRadius, AOEFatedCircleTargetThreshold) || ForceAOE);
        WantAOE = WantNormalAOE;
        var openerReady =
            Unlocked(AID.ReignOfBeasts) ? ComboLastMove is AID.None or AID.LightningShot or AID.KeenEdge or AID.BrutalShell
            : Unlocked(AID.SolidBarrel) ? ComboLastMove is AID.SolidBarrel
            : ComboLastMove is AID.None or AID.KeenEdge or AID.BrutalShell;
        var open = (CombatTimer < TimingOpenerEnd && openerReady) || CombatTimer >= TimingOpenerEnd;
        var mainTarget = primaryTarget?.Actor;
        AkechiGNBPlanner74IpcBridge.UpdateClock(CombatTimer, Player.InCombat);
        UpdateFightEstimate(mainTarget);
        Planner74UpdateTargetableLearning(mainTarget);
        Planner74FeedCooldownPlannerTimelineHints();
        // BossPlanner windows (current add + learned target loss) are re-pushed every frame by the two feeders below; drop last frame's copies first
        AkechiGNBPlanner74HintBus.ClearSource(AkechiGNBPlanner74HintSource.BossPlanner);
        Planner74FeedCurrentAddTargetHints(mainTarget);
        Planner74FeedUnsupportedBossFallbackHints(mainTarget);
        Planner74FeedMechanicForecastHints();
        var targetableNow = Planner74CurrentTargetable(mainTarget);
        var noMercyStrategy = strategy.Option(Track.NoMercy).As<NoMercyStrategy>();
        var smoothFullModeBurstEntry =
            !normalOvercapOnly &&
            !HasNM &&
            NMcd <= SkSGCDLength * Planner74PreNMGFOffsetSlots &&
            targetableNow &&
            InCombat(mainTarget) &&
            FullModeBurstResumeUntil > CombatTimer;
        var allowPlanner74 = Unlocked(AID.GnashingFang) && Player.InCombat;
        if (allowPlanner74)
            Planner74LastPlan = Planner74BuildPlanForCurrentState(mainTarget, noMercyStrategy == NoMercyStrategy.SynergyOptimized, smoothFullModeBurstEntry, strategy.HoldBuffs());
        else
        {
            Planner74LastPlanValid = false;
            Planner74LastPlan = new() { First = new() { GCD = Planner74GCD.None }, Plan = [], Score = 0f, Debug = "" };
        }
        UpdateBurstGFReservation();
        var planner74Next = Planner74LastPlan.First;
        var fightEndBurn = FightEndBurnEnabled(strategy);
        var bfStratSetting = strategy.Option(Track.Bloodfest).As<BloodfestStrategy>();
        var bfAutoAligned = bfStratSetting is BloodfestStrategy.Automatic or BloodfestStrategy.Together;
        var delayedBurstPrepReady = !HasDeathDelayedBurst || CombatTimer >= DelayedBurstAt - SkSGCDLength * Planner74PreNMGFOffsetSlots;
        var delayedBloodfestReady = !HasDeathDelayedBurst || CombatTimer >= DelayedBurstAt - SkSGCDLength * Planner74BloodfestWeaveOffsetSlots;
        var delayedBurstReady = !HasDeathDelayedBurst || CombatTimer >= DelayedBurstAt;
        var delayedNMReady = delayedBurstReady || nmActive;
        var holdFailedBurstResources = HasDeathDelayedBurst && !delayedBurstPrepReady;
        var bfReadyForFullBurst =
            bfAutoAligned &&
            ActionReady(AID.Bloodfest) &&
            !HasBF &&
            Player.InCombat &&
            open &&
            targetableNow &&
            (!WantAOE || smoothFullModeBurstEntry || HasNM) &&
            delayedBloodfestReady;
        var resumePreGFFirst =
            smoothFullModeBurstEntry &&
            strategy.Option(Track.GnashingFang).As<GnashingStrategy>() != GnashingStrategy.Delay &&
            GunComboStep == 0 &&
            ComboLastMove is not AID.KeenEdge and not AID.BrutalShell and not AID.DemonSlice &&
            HasGF1 &&
            Ammo >= 1 &&
            !TargetsInAOECircle(AOEMeleeRadius, AOENormalHighLevelTargetThreshold);
        var bfPendingForNM =
            bfReadyForFullBurst &&
            !HasNM &&
            (!WantAOE || smoothFullModeBurstEntry);
        var pendingNoMercyAfterWickedTalon =
            noMercyStrategy != NoMercyStrategy.Delay &&
            !strategy.HoldCDs() &&
            !strategy.HoldBuffs() &&
            delayedNMReady &&
            targetableNow &&
            InCombat(mainTarget) &&
            open &&
            !HasNM &&
            (noMercyStrategy != NoMercyStrategy.SynergyOptimized || planner74Next.WeaveNoMercy) &&
            LastActionUsed(AID.WickedTalon) &&
            HasGouge &&
            // Holding the normal combo only makes sense while a weave slot still exists. If the GCD has already
            // rolled off - which is what a downtime landing on Wicked Talon does - withholding it deadlocks:
            // No Mercy needs a weave window, and the only thing that opens one is the GCD being withheld. The
            // rotation then idles until Ready to Gouge expires, losing the burst and the continuation with it.
            CanNoMercyFirstWeaveIn &&
            // same deadlock when an ability lock (Amnesia) outlasts the weave window: No Mercy cannot be pressed, so the GCD must not wait for it
            AbilityUsableBeforeGCD &&
            NMcd <= MathF.Max(0f, GCD - TimingReadyEpsilon);
        var canStartNMWithBS =
            Player.InCombat &&
            open &&
            targetableNow &&
            !HasNM &&
            NMcd < 1 &&
            !WantAOE &&
            GunComboStep == 0 &&
            Unlocked(AID.BurstStrike) &&
            Ammo >= 4 &&
            In3y(mainTarget) &&
            !bfPendingForNM &&
            !pendingNoMercyAfterWickedTalon &&
            delayedBurstReady;
        var nmBurstWeave =
            Player.InCombat &&
            open &&
            CanNoMercyFirstWeaveIn &&
            LastActionUsed(AID.BurstStrike) &&
            (!Unlocked(AID.Hypervelocity) || HasBlast);
        var fixedNoMercyWeave = planner74Next.WeaveNoMercy && CanNoMercyFirstWeaveIn;
        var mustBloodfestBeforeNoMercy =
            bfReadyForFullBurst &&
            !HasNM &&
            !HasBF &&
            (smoothFullModeBurstEntry || planner74Next.WeaveBloodfest || NMcd <= SkSGCDLength * Planner74BloodfestWeaveOffsetSlots);
        var weaveNoMercyBeforeGouge =
            pendingNoMercyAfterWickedTalon &&
            CanNoMercyFirstWeaveIn &&
            ActionReady(AID.NoMercy);
        var holdGCDForLateBurstGF =
            !strategy.HoldGauge() &&
            strategy.Option(Track.GnashingFang).As<GnashingStrategy>() != GnashingStrategy.Delay &&
            targetableNow &&
            InCombat(mainTarget) &&
            In3y(mainTarget) &&
            HasNM &&
            !HasReign &&
            GunComboStep == 0 &&
            ActualComboTimer == 0 &&
            Ammo >= 1 &&
            !HasGF1 &&
            Cooldown(AID.GnashingFang) is > 0f and <= TimingLateBurstGFHoldMaxCooldown && // never idle the GCD for more than a weave-length inside No Mercy
            NMstatus - Cooldown(AID.GnashingFang) >= SkSGCDLength * Planner74PreNMGFOffsetSlots;
        var currentLateBurstGFReady =
            !strategy.HoldGauge() &&
            strategy.Option(Track.GnashingFang).As<GnashingStrategy>() != GnashingStrategy.Delay &&
            targetableNow &&
            InCombat(mainTarget) &&
            In3y(mainTarget) &&
            HasNM &&
            !HasReign &&
            GunComboStep == 0 &&
            ActualComboTimer == 0 &&
            Ammo >= 1 &&
            HasGF1 &&
            Planner74BuffLeftToSlots(NMstatus, SkSGCDLength) is >= 1 and <= 3;
        var potionAfterBurstSpend = !normalOvercapOnly && !holdFailedBurstResources && PotionAfterBurstSpendWindow();

        if (strategy.HoldEverything())
            return;

        if (targetableNow && !holdFailedBurstResources && !strategy.HoldCDs() && !strategy.HoldGauge())
            QueueMechanicWindDown(strategy, mainTarget);

        var plannerQueuedGCD = allowPlanner74 && !normalOvercapOnly && !strategy.HoldCDs() && !strategy.HoldGauge() && targetableNow && !holdFailedBurstResources && !pendingNoMercyAfterWickedTalon &&
            Planner74QueuePlannedGCD(planner74Next, mainTarget, strategy);
        // only withhold the 1-2-3 combo when the planner actually supplied a replacement GCD, otherwise the GCD would idle (below L60, HoldCDs/HoldGauge, post-death hold, ...)
        var holdNormalForBasicBurstGap = plannerQueuedGCD && ShouldHoldNormalGCDForBasicBurstGap();

        if (!normalOvercapOnly && !strategy.HoldCDs())
        {
            if (!strategy.HoldBuffs())
            {
                //Bloodfest
                if (ActionReady(AID.Bloodfest))
                {
                    var bf = strategy.Option(Track.Bloodfest);
                    var bfStrat = bfStratSetting;
                    var bfTarget = SingleTargetChoice(mainTarget, bf);
                    var bfBeforeNM = InCombat(bfTarget) && CanWeaveIn && bfPendingForNM && planner74Next.WeaveBloodfest;
                    var bfResumeNow = InCombat(bfTarget) && CanWeaveIn && bfPendingForNM && smoothFullModeBurstEntry && !resumePreGFFirst && NMcd <= SkSGCDLength * Planner74PreNMGFOffsetSlots;
                    var bfFullBurstNow =
                        InCombat(bfTarget) &&
                        CanWeaveIn &&
                        bfReadyForFullBurst &&
                        !holdFailedBurstResources &&
                        (HasNM || planner74Next.WeaveNoMercy || NMcd <= SkSGCDLength * Planner74BloodfestWeaveOffsetSlots);
                    var bfFightEnd = InCombat(bfTarget) && targetableNow && CanWeaveIn && fightEndBurn && !HasBF && open && !WantAOE;
                    var (bfCondition, bfPrio) = bfStrat switch
                    {
                        BloodfestStrategy.Automatic => (bfBeforeNM || bfResumeNow || bfFullBurstNow || bfFightEnd, OGCDPriority.Severe + 1),
                        BloodfestStrategy.Together => (bfBeforeNM || bfResumeNow || bfFullBurstNow || bfFightEnd, OGCDPriority.Severe + 1),
                        BloodfestStrategy.Force => (true, OGCDPriority.Severe + 1 + 2000),
                        BloodfestStrategy.ForceW => (CanWeaveIn, OGCDPriority.Severe + 1 + 1000),
                        _ => (false, OGCDPriority.None),
                    };
                    if (bfCondition)
                        QueueOGCD(AID.Bloodfest, bfTarget, bfPrio);
                }

                //No Mercy
                if (ActionReady(AID.NoMercy))
                {
                    var nm = strategy.Option(Track.NoMercy);
                    var nmStrat = noMercyStrategy;
                    var slow = Slow && CanWeaveIn;
                    var fast = Fast && CanQuarterWeaveIn;
                    var speed = slow || fast;
                    var noCartridge = MaxCartridges <= 0;
                    var lv1to89 = speed && (noCartridge || Ammo >= 1);
                    var lv90plus = speed && Ammo >= 2;
                    var cartCheck = Unlocked(AID.DoubleDown) ? lv90plus : lv1to89;
                    var burst = noCartridge ? speed : cartCheck &&
                            ((!Unlocked(AID.DoubleDown) || Ammo > GameRuleDoubleDownCartridgeCost && Cooldown(AID.DoubleDown) <= TimingDoubleDownBurstReadyCooldown) ||
                            (!Unlocked(AID.GnashingFang) || Ammo > 1 && (HasGF1 || Cooldown(AID.GnashingFang) <= TimingGnashingFangBurstReadyCooldown)));
                    var together = noCartridge ? speed : cartCheck && (HasBF || BFcd > TimingBloodfestTogetherCooldown);
                    var commonBase = InCombat(mainTarget) && targetableNow && open;
                    var openingNMTooEarly = CombatTimer < TimingOpenerEnd && ComboLastMove == AID.KeenEdge && planner74Next.GCD == Planner74GCD.BrutalShell;
                    // below Gnashing Fang level there is no planner to anchor the weave, so fall back to a plain weave window
                    var nonPlannerNoMercyWeave = !allowPlanner74 && CanWeaveIn;
                    // a weaponskill lock (Pacification) that outlasts the weave window would burn the start of the No Mercy window with no GCD to buff
                    var common = commonBase && delayedNMReady && !holdFailedBurstResources && !mustBloodfestBeforeNoMercy && (fixedNoMercyWeave || weaveNoMercyBeforeGouge || nonPlannerNoMercyWeave) && !openingNMTooEarly && (!canStartNMWithBS || nmBurstWeave || planner74Next.GCD != Planner74GCD.BurstStrike) && WeaponskillUsableBeforeGCD;

                    var (nmCondition, nmPrio) = nmStrat switch
                    {
                        NoMercyStrategy.Automatic => (common, OGCDPriority.Severe),
                        NoMercyStrategy.SynergyOptimized => (common, OGCDPriority.Severe),
                        NoMercyStrategy.BurstReady => (common && (burst || nmBurstWeave || weaveNoMercyBeforeGouge), OGCDPriority.Severe),
                        NoMercyStrategy.Together => (common && (together || nmBurstWeave || weaveNoMercyBeforeGouge), OGCDPriority.Severe),
                        NoMercyStrategy.Force => (true, OGCDPriority.Severe + 2000),
                        NoMercyStrategy.ForceW => (CanWeaveIn, OGCDPriority.Severe + 1000),
                        NoMercyStrategy.ForceQW => (CanQuarterWeaveIn, OGCDPriority.Severe + 1000),
                        NoMercyStrategy.Force1 => (Ammo >= 1, OGCDPriority.Severe + 2000),
                        NoMercyStrategy.Force1W => (CanWeaveIn && Ammo >= 1, OGCDPriority.Severe + 1000),
                        NoMercyStrategy.Force1QW => (CanQuarterWeaveIn && Ammo >= 1, OGCDPriority.Severe + 1000),
                        NoMercyStrategy.Force2 => (Ammo >= 2, OGCDPriority.Severe + 2000),
                        NoMercyStrategy.Force2W => (CanWeaveIn && Ammo >= 2, OGCDPriority.Severe + 1000),
                        NoMercyStrategy.Force2QW => (CanQuarterWeaveIn && Ammo >= 2, OGCDPriority.Severe + 1000),
                        NoMercyStrategy.Force3 => (Ammo >= 3, OGCDPriority.Severe + 2000),
                        NoMercyStrategy.Force3W => (CanWeaveIn && Ammo >= 3, OGCDPriority.Severe + 1000),
                        NoMercyStrategy.Force3QW => (CanQuarterWeaveIn && Ammo >= 3, OGCDPriority.Severe + 1000),
                        _ => (false, OGCDPriority.None),
                    };
                    if (nmCondition)
                        QueueOGCD(AID.NoMercy, Player, nmPrio + (nmBurstWeave ? 500 : 0) + 2);
                }
            }

            if (!strategy.HoldGauge())
            {
                var ddStratSetting = strategy.Option(Track.DoubleDown).As<DoubleDownStrategy>();
                var ddShouldReserveCartridges =
                    ddStratSetting != DoubleDownStrategy.Delay &&
                    Unlocked(AID.DoubleDown) &&
                    Ammo >= 2 &&
                    !holdFailedBurstResources &&
                    targetableNow &&
                    HasNM &&
                    InCombat(mainTarget) &&
                    Cooldown(AID.DoubleDown) <= SkSGCDLength;
                var normalComboFinishedForGF = ActualComboTimer == 0 || ComboLastMove is AID.SolidBarrel or AID.DemonSlaughter;
                var canFitGFBeforeNormalComboExpires = ActualComboTimer >= SkSGCDLength * TimingNormalGFAfterComboSafetyGCDs || ActualComboTimer == 0;
                var openingBurstSecondGFBlocked = CombatTimer < TimingOpenerEnd && nmActive && OpeningBurstGFUsedInNM;
                var lateBurstGFCanBreakNormalCombo =
                    nmActive &&
                    CombatTimer >= TimingOpenerEnd &&
                    !openingBurstSecondGFBlocked &&
                    !HasReign &&
                    Planner74BuffLeftToSlots(NMstatus, SkSGCDLength) is >= 1 and <= 3;
                var normalComboSafeForGF = normalComboFinishedForGF || AllowOpeningDelayedSolidGF() || lateBurstGFCanBreakNormalCombo;
                var gfReadyForBurst =
                    strategy.Option(Track.GnashingFang).As<GnashingStrategy>() != GnashingStrategy.Delay &&
                    targetableNow &&
                    InCombat(mainTarget) &&
                    In3y(mainTarget) &&
                    HasNM &&
                    !HasReign &&
                    HasGF1 &&
                    Ammo >= 1 &&
                    GunComboStep == 0 &&
                    Planner74BuffLeftToSlots(NMstatus, SkSGCDLength) >= 1 &&
                    !UsedGFComboInNM &&
                    !openingBurstSecondGFBlocked &&
                    normalComboSafeForGF;
                var allowSecondGFInNMAfterCombo =
                    nmActive &&
                    !openingBurstSecondGFBlocked &&
                    UsedGFComboInNM &&
                    (normalComboFinishedForGF || lateBurstGFCanBreakNormalCombo) &&
                    !HasReign &&
                    Planner74BuffLeftToSlots(NMstatus, SkSGCDLength) >= 1;
                if (allowSecondGFInNMAfterCombo &&
                    strategy.Option(Track.GnashingFang).As<GnashingStrategy>() != GnashingStrategy.Delay &&
                    targetableNow &&
                    InCombat(mainTarget) &&
                    In3y(mainTarget) &&
                    HasGF1 &&
                    Ammo >= 1 &&
                    GunComboStep == 0)
                    gfReadyForBurst = true;
                //Gnashing Fang + combo
                if (Unlocked(AID.GnashingFang))
                {
                    var gf = strategy.Option(Track.GnashingFang);
                    var gfStrat = gf.As<GnashingStrategy>();
                    var gfTarget = SingleTargetChoice(mainTarget, gf);
                    var gfRepeatBlockedInNM = nmActive && UsedGFComboInNM && !allowSecondGFInNMAfterCombo;
                    var canfit = canFitGFBeforeNormalComboExpires; //dont use if combo timer will expire (or if combo timer is 0, just send)
                    var gfBase = targetableNow && gfTarget != null && In3y(gfTarget) && HasGF1 && Ammo >= 1 && GunComboStep == 0 && !gfRepeatBlockedInNM && !openingBurstSecondGFBlocked && normalComboSafeForGF;
                    var gfMinimum = gfBase;
                    var st = gfMinimum && !TargetsInAOECircle(AOEMeleeRadius, AOENormalHighLevelTargetThreshold) && !holdFailedBurstResources;
                    var gfFightEnd = gfBase && fightEndBurn && !TargetsInAOECircle(AOEMeleeRadius, AOENormalHighLevelTargetThreshold) && !holdFailedBurstResources;
                    var canFinishGFInCurrentNM = HasNM && Planner74BuffLeftToSlots(NMstatus, SkSGCDLength) >= 1;
                    var burst = st && canFinishGFInCurrentNM && !HasReign; //if Lv100 & not opener, we send after Reign combo
                    var reservedBurstOvercap = gfBase && !TargetsInAOECircle(AOEMeleeRadius, AOENormalHighLevelTargetThreshold) && !nmActive && canfit && ShouldSpendGFOvercapBeforeReservedBurst(allowDuringBurstHold: holdFailedBurstResources);
                    var normalOvercap = st && !nmActive && canfit && CanUseNormalGFOvercapWithoutBreakingNextNMBurst();
                    var potionAfterBurstGF = st && !nmActive && canfit && potionAfterBurstSpend && CanSpendGFWithoutBreakingBurstReservation(NextReservedNoMercyCombatTime());
                    var postNoMercyDump = st && HasBF && PostNoMercyGFDumpPending && !nmActive && GFCharges == 1 && normalComboFinishedForGF;
                    var filler = normalOvercap || reservedBurstOvercap || potionAfterBurstGF || postNoMercyDump;
                    var overcap = normalOvercap || reservedBurstOvercap;
                    var (gfCondition, gfAction, gfPrio) = gfStrat switch
                    {
                        GnashingStrategy.Automatic => (burst || filler || overcap || gfFightEnd, AID.GnashingFang, postNoMercyDump ? GCDPriority.VeryHigh + 2 : gfFightEnd ? GCDPriority.SlightlyHigh + 9 : overcap ? GCDPriority.SlightlyHigh + 9 : potionAfterBurstGF ? GCDPriority.SlightlyHigh + 6 : burst ? GCDPriority.SlightlyHigh + 7 : GCDPriority.SlightlyHigh),
                        GnashingStrategy.ForceGnash => (gfMinimum, AID.GnashingFang, GCDPriority.Forced),
                        GnashingStrategy.ForceGnash1 => (gfMinimum && Ammo >= 1, AID.GnashingFang, GCDPriority.Forced),
                        GnashingStrategy.ForceGnash2 => (gfMinimum && Ammo >= 2, AID.GnashingFang, GCDPriority.Forced),
                        GnashingStrategy.ForceGnash3 => (gfMinimum && Ammo >= 3, AID.GnashingFang, GCDPriority.Forced),
                        GnashingStrategy.ForceClaw => (GunComboStep == 1 && In3y(gfTarget), AID.SavageClaw, GCDPriority.Forced),
                        GnashingStrategy.ForceTalon => (GunComboStep == 2 && In3y(gfTarget), AID.WickedTalon, GCDPriority.Forced),
                        _ => (false, AID.None, GCDPriority.None),
                    };
                    if (gfCondition)
                        QueueGCD(gfAction, gfTarget, gfPrio);
                }

                //Double Down
                if (ActionReady(AID.DoubleDown) && Ammo >= 2)
                {
                    var dd = strategy.Option(Track.DoubleDown);
                    var ddStrat = ddStratSetting;
                    var ddTargetInRange = AnyTargetIn5y(mainTarget);
                    var ddFightEnd = fightEndBurn && InCombat(mainTarget) && ddTargetInRange;
                    var ddBeforeReignContinuation = HasNM && GunComboStep is 3 or 4;
                    var (ddCondition, ddPrio) = ddStrat switch
                    {
                        DoubleDownStrategy.Automatic => (!holdFailedBurstResources && targetableNow && InCombat(mainTarget) && ddTargetInRange && ((HasNM && NoMercyAtLeast2GCDsElapsed) || ddFightEnd), HasNM || ddBeforeReignContinuation ? GCDPriority.VeryHigh + 3 : GCDPriority.SlightlyHigh + 6),
                        DoubleDownStrategy.Force => (true, GCDPriority.Forced),
                        DoubleDownStrategy.Force3 => (Ammo >= 3, GCDPriority.Forced),
                        _ => (false, GCDPriority.None),
                    };
                    if (ddCondition)
                        QueueGCD(AID.DoubleDown, Player, ddPrio);
                }

                //Burst Strike & Fated Fircle
                if (Unlocked(AID.BurstStrike) && Ammo >= 1)
                {
                    var carts = strategy.Option(Track.Cartridges);
                    var cartsStrat = cartsStratSetting;
                    var onlybs = cartsStrat == CartridgeStrategy.OnlyBS;
                    var onlyfc = cartsStrat == CartridgeStrategy.OnlyFC;
                    var fc = Unlocked(AID.FatedCircle) ? AID.FatedCircle : AID.BurstStrike;
                    var bsTarget = SingleTargetChoice(mainTarget, carts);
                    var useAOE = WantFatedCircleAOE;
                    var lv30to71 = !Unlocked(AID.FatedCircle) && (!TargetsInAOECircle(AOEMeleeRadius, AOENormalHighLevelTargetThreshold) && In3y(mainTarget)); //Before Lv72 - if more than 2 targets are present, we skip Burst Strike entirely
                    var lv72plus = Unlocked(AID.FatedCircle) && (useAOE ? AnyTargetIn5y(mainTarget) : In3y(mainTarget)); //After Lv72 - if more than 1 target is present, we choose Fated Circle over Burst Strike
                    var prepBSForNM = canStartNMWithBS && bsTarget != null;
                    var spendForFightEnd = fightEndBurn && GunComboStep == 0;
                    var (bsfcCondition, bsfcAction, bsfcTarget, bsfcPrio) = cartsStrat switch
                    {
                        CartridgeStrategy.Automatic or CartridgeStrategy.OnlyBS or CartridgeStrategy.OnlyFC
                            => ((!ddShouldReserveCartridges && !gfReadyForBurst && !holdGCDForLateBurstGF && !currentLateBurstGFReady && !holdFailedBurstResources && !pendingNoMercyAfterWickedTalon && targetableNow && InCombat(mainTarget) && (Unlocked(AID.FatedCircle) ? lv72plus : lv30to71) && GunComboStep == 0 &&
                            (HasNM || //if we have No Mercy, spend as much as possible after all combos (if we can)
                            prepBSForNM || //if No Mercy is ready in ST, start with Burst Strike so the weave becomes BS>NM>HV
                            spendForFightEnd ||
                            potionAfterBurstSpend ||
                            (Ammo > GameRuleBaseMaxCartridgesHigh && Status(SID.Bloodfest) is <= TimingBloodfestOvercapStatusLeft and not 0 && !HasNM))), //if we have extra Bloodfest carts after NM, spend them asap
                            prepBSForNM ? AID.BurstStrike : onlyfc ? fc : onlybs ? AID.BurstStrike : (useAOE ? Unlocked(AID.FatedCircle) ? AID.FatedCircle : AID.BurstStrike : AID.BurstStrike),
                            prepBSForNM ? bsTarget : useAOE ? Player : bsTarget,
                            prepBSForNM ? GCDPriority.SlightlyHigh + 11 : spendForFightEnd ? GCDPriority.SlightlyHigh + 9 : GCDPriority.SlightlyHigh + 2),
                        CartridgeStrategy.ForceBS => (true, AID.BurstStrike, bsTarget, GCDPriority.Forced),
                        CartridgeStrategy.ForceBS1 => (Ammo >= 1, AID.BurstStrike, bsTarget, GCDPriority.Forced),
                        CartridgeStrategy.ForceBS2 => (Ammo >= 2, AID.BurstStrike, bsTarget, GCDPriority.Forced),
                        CartridgeStrategy.ForceBS3 => (Ammo >= 3, AID.BurstStrike, bsTarget, GCDPriority.Forced),
                        CartridgeStrategy.ForceFC => (Unlocked(AID.FatedCircle), fc, Player, GCDPriority.Forced),
                        CartridgeStrategy.ForceFC1 => (Unlocked(AID.FatedCircle) && Ammo >= 1, fc, Player, GCDPriority.Forced),
                        CartridgeStrategy.ForceFC2 => (Unlocked(AID.FatedCircle) && Ammo >= 2, fc, Player, GCDPriority.Forced),
                        CartridgeStrategy.ForceFC3 => (Unlocked(AID.FatedCircle) && Ammo >= 3, fc, Player, GCDPriority.Forced),
                        _ => (false, AID.None, null, GCDPriority.None)
                    };
                    if (bsfcCondition)
                        QueueGCD(bsfcAction, bsfcTarget, bsfcPrio);
                }
            }

            //Sonic Break
            if (Unlocked(AID.SonicBreak) && HasStatus(SID.ReadyToBreak))
            {
                var sb = strategy.Option(Track.SonicBreak);
                var sbStrat = sb.As<SonicBreakStrategy>();
                var sbTarget = AOETargetChoice(mainTarget, BestDOTTarget?.Actor, sb, strategy);
                var ddShouldGoBeforeSonic =
                    strategy.Option(Track.DoubleDown).As<DoubleDownStrategy>() != DoubleDownStrategy.Delay &&
                    ActionReady(AID.DoubleDown) &&
                    Ammo >= 2 &&
                    HasNM &&
                    targetableNow &&
                    InCombat(mainTarget) &&
                    AnyTargetIn5y(mainTarget);
                var sonicBurstPrio = HasNM && !ddShouldGoBeforeSonic ? GCDPriority.VeryHigh + 4 : GCDPriority.SlightlyHigh + 5;
                var (sbCondition, sbPrio) = sbStrat switch
                {
                    SonicBreakStrategy.Automatic => (!holdFailedBurstResources && targetableNow && InCombat(sbTarget) && In3y(sbTarget) && (!HasNM || NoMercyAtLeast1GCDElapsed || Mechanic.ExpiresDuringLoss(SBstatus)), sonicBurstPrio),
                    SonicBreakStrategy.Force => (true, GCDPriority.Forced),
                    SonicBreakStrategy.Early => (!holdFailedBurstResources && targetableNow && HasNM && NoMercyAtLeast1GCDElapsed, sonicBurstPrio),
                    SonicBreakStrategy.Late => (!holdFailedBurstResources && targetableNow && SBstatus is <= TimingSonicBreakLateStatusLeft and not 0, GCDPriority.SlightlyHigh + 10),
                    _ => (false, GCDPriority.None)
                };
                if (sbCondition)
                    QueueGCD(AID.SonicBreak, sbTarget, sbPrio);
            }

            //Zone
            if (Unlocked(AID.DangerZone))
            {
                var zone = strategy.Option(Track.Zone);
                var zoneStrat = zone.As<OGCDStrategy>();
                var zoneAction = Unlocked(AID.BlastingZone) ? AID.BlastingZone : AID.DangerZone;
                // keep Zone aligned with No Mercy: don't burn it right before NM comes off cooldown (unless NM is delayed / fight is ending), and keep the NM weave slot free
                var noMercyWaitTooLong = NoMercyReadySince >= 0f && CombatTimer - NoMercyReadySince > TimingZoneNoMercyAlignMaxWait;
                var zoneNoMercyAligned = HasNM || fightEndBurn || noMercyStrategy == NoMercyStrategy.Delay || strategy.HoldBuffs() || NMcd > TimingZoneNoMercyAlignCooldown || noMercyWaitTooLong;
                if (ShouldUseOGCD(zoneStrat, mainTarget, ActionReady(zoneAction),
                    !holdFailedBurstResources && targetableNow && InCombat(mainTarget) && In3y(mainTarget) && CanWeaveIn && zoneNoMercyAligned && !planner74Next.ForbidZoneBowAfterThisGCD && (!HasNM || NoMercyAtLeast2GCDsElapsed)))
                    QueueOGCD(zoneAction, SingleTargetChoice(mainTarget, zone), OGCDPrio(zoneStrat, nmActive ? ZonePrioInNM : ZonePrio));
            }

            //Bow Shock
            if (ActionReady(AID.BowShock))
            {
                var bowStrat = strategy.Option(Track.BowShock).As<OGCDStrategy>();
                if (ShouldUseOGCD(bowStrat, mainTarget, ActionReady(AID.BowShock),
                    !holdFailedBurstResources && targetableNow && InCombat(mainTarget) && AnyTargetIn5y(mainTarget) && CanWeaveIn && !planner74Next.ForbidZoneBowAfterThisGCD && NoMercyAtLeast2GCDsElapsed))
                    QueueOGCD(AID.BowShock, Player, OGCDPrio(bowStrat, BowPrio));
            }
        }

        //Reign of Beasts + combo
        if (!normalOvercapOnly && Unlocked(AID.ReignOfBeasts))
        {
            var r = strategy.Option(Track.Reign);
            var rStrat = r.As<ReignStrategy>();
            var rTarget = AOETargetChoice(mainTarget, BestSplashTarget?.Actor, r, strategy);
            var hold = strategy.HoldCDs();
            var rFightEnd = fightEndBurn && (HasReign || GunComboStep is 3 or 4);
            var (rCondition, rAction, rPrio) = rStrat switch
            {
                ReignStrategy.Automatic => (
                    !holdFailedBurstResources && targetableNow && InCombat(rTarget) && In3y(rTarget) && !hold && ((HasReign && (Rstatus is <= TimingReignEmergencyStatusLeft and not 0 || (HasNM && GunComboStep == 0) || Mechanic.ExpiresDuringLoss(Rstatus))) || GunComboStep is 3 or 4 || rFightEnd),
                    GunComboStep == 4 ? AID.LionHeart : GunComboStep == 3 ? AID.NobleBlood : AID.ReignOfBeasts,
                    NMstatus is <= 8 and not 0 || HasReign || rFightEnd ? GCDPriority.SlightlyHigh + 8 : GCDPriority.SlightlyHigh + 4
                    ),
                ReignStrategy.ForceReign => (HasReign, AID.ReignOfBeasts, GCDPriority.Forced),
                ReignStrategy.ForceNoble => (GunComboStep == 3, AID.NobleBlood, GCDPriority.Forced),
                ReignStrategy.ForceLion => (GunComboStep == 4, AID.LionHeart, GCDPriority.Forced),
                _ => (false, AID.None, GCDPriority.None),
            };
            if (rCondition)
                QueueGCD(rAction, rTarget, rPrio);
        }

        //GF combo in filler
        if (!normalOvercapOnly && targetableNow)
        {
            var gfComboAction = GunComboStep switch
            {
                2 when Unlocked(AID.WickedTalon) => AID.WickedTalon,
                1 when Unlocked(AID.SavageClaw) => AID.SavageClaw,
                _ => AID.None,
            };
            QueueGCD(gfComboAction, mainTarget, GCDPriority.SlightlyHigh + 3);
        }

        //Continuation procs
        //Hypervelocity, Fated Brand, Jugular Rip, Abdomen Tear, & Eye Gouge
        if (Unlocked(AID.Continuation))
        {
            var c = strategy.Option(Track.Continuation);
            var cStrat = c.As<ContinuationStrategy>();
            var cTarget = SingleTargetChoice(mainTarget, c);
            var nmDelayActive = strategy.Option(Track.NoMercy).As<NoMercyStrategy>() == NoMercyStrategy.Delay;
            var hvNormalWeaveOK = (NMcd > TimingContinuationNoMercyClearance && GCD >= TimingContinuationWeaveMinGCD) || GCD < TimingContinuationWeaveMinGCD;
            var hvDelayWeaveOK = nmDelayActive && !HasNM && CanWeaveIn;
            var hvWindowOK = hvDelayWeaveOK || hvNormalWeaveOK;
            var waitGougeForLateWeaveAfterNoMercy = HasGouge && HasNM && NMstatus > TimingNoMercyLateGougeReserve && CanWeaveIn && !CanLateWeaveIn;
            var cMinimum = targetableNow && InCombat(cTarget) && !pendingNoMercyAfterWickedTalon && !waitGougeForLateWeaveAfterNoMercy && (((HasBlast || HasRaze) && hvWindowOK) || HasRip || HasTear || HasGouge);
            var cAction = HasGouge ? AID.EyeGouge : HasTear ? AID.AbdomenTear : HasRip ? AID.JugularRip : HasRaze ? AID.FatedBrand : HasBlast ? AID.Hypervelocity : AID.Continuation;
            var (cCondition, cPrio) = cStrat switch
            {
                ContinuationStrategy.Automatic or ContinuationStrategy.Early => (cMinimum, ContinuationPrio()),
                ContinuationStrategy.Late => (cMinimum && (hvDelayWeaveOK || GCD <= TimingContinuationLateMaxGCD), ContinuationPrio()),
                _ => (false, OGCDPriority.None),
            };
            if (cCondition)
                QueueOGCD(cAction, cTarget, cPrio);
        }

        //Lightning Shot
        if (Unlocked(AID.LightningShot))
        {
            var ls = strategy.Option(Track.LightningShot);
            var lsStrat = ls.As<LightningShotStrategy>();
            var lsTarget = SingleTargetChoice(mainTarget, ls);
            var (lsCondition, lsPrio) = lsStrat switch
            {
                LightningShotStrategy.OpenerFar => (targetableNow && (Player.InCombat || World.Client.CountdownRemaining < 0.8f) && IsFirstGCD && !In3y(lsTarget), GCDPriority.Forced),
                LightningShotStrategy.OpenerForce => (targetableNow && (Player.InCombat || World.Client.CountdownRemaining < 0.8f) && IsFirstGCD, GCDPriority.Forced),
                LightningShotStrategy.Force => (true, GCDPriority.Forced),
                LightningShotStrategy.Allow => (targetableNow && (WantAOE ? !In5y(lsTarget) : !In3y(lsTarget)), GCDPriority.Low + 2),
                _ => (false, GCDPriority.None),
            };
            if (lsCondition)
                QueueGCD(AID.LightningShot, lsTarget, lsPrio);
        }

        //Strength Pot
        var noMercyWeaveCollision =
            noMercyStrategy != NoMercyStrategy.Delay &&
            !HasNM &&
            ActionReady(AID.NoMercy) &&
            CanNoMercyFirstWeaveIn &&
            (fixedNoMercyWeave || weaveNoMercyBeforeGouge || NMcd <= MathF.Max(0f, GCD - TimingReadyEpsilon));
        if (!noMercyWeaveCollision && ShouldUsePotion(strategy))
            Hints.ActionsToExecute.Push(ActionDefinitions.IDPotionStr, Player, ActionQueue.Priority.VeryHigh + (int)OGCDPriority.VeryCritical, delay: MathF.Max(0f, GCD - TimingPotionDelayBeforeGCD));

        var justusedST =
            ComboLastMove is
                AID.KeenEdge or AID.BrutalShell or //st
                AID.GnashingFang or AID.SavageClaw or AID.ReignOfBeasts or AID.NobleBlood; //Gauge
        var justusedAOE = ComboLastMove is AID.DemonSlice;
        var stTarget = SingleTargetChoice(mainTarget, aoe);
        var autoTarget = !WantAOE || justusedST ? stTarget : Player;
        var (aoeAction, aoeTarget) = normalOvercapOnly
            ? (WantAOE ? AOEFinish(true) : STFinish(true), autoTarget)
            : aoeStrat switch
            {
                AOEStrategy.AutoFinishWithOvercap => (WantAOE ? AOEFinish(true) : STFinish(true), autoTarget),
                AOEStrategy.AutoFinishWithoutOvercap => (WantAOE ? AOEFinish(false) : STFinish(false), autoTarget),
                AOEStrategy.AutoBreakWithOvercap => (WantAOE ? AOEBreak(true) : STBreak(true), autoTarget),
                AOEStrategy.AutoBreakWithoutOvercap => (WantAOE ? AOEBreak(false) : STBreak(false), autoTarget),
                AOEStrategy.ForceSTFinishWithOvercap => (STFinish(true), justusedAOE ? Player : stTarget),
                AOEStrategy.ForceSTFinishWithoutOvercap => (STFinish(false), justusedAOE ? Player : stTarget),
                AOEStrategy.ForceAOEFinishWithOvercap => (AOEFinish(true), justusedST ? stTarget : Player),
                AOEStrategy.ForceAOEFinishWithoutOvercap => (AOEFinish(false), justusedST ? stTarget : Player),
                AOEStrategy.ForceSTBreakWithOvercap => (STBreak(true), stTarget),
                AOEStrategy.ForceSTBreakWithoutOvercap => (STBreak(false), stTarget),
                AOEStrategy.ForceAOEBreakWithOvercap => (AOEBreak(true), Player),
                AOEStrategy.ForceAOEBreakWithoutOvercap => (AOEBreak(false), Player),
                _ => (AID.None, null),
            };

        var normalBlocked = !normalOvercapOnly && (holdNormalForBasicBurstGap || holdGCDForLateBurstGF || pendingNoMercyAfterWickedTalon);
        if (!normalBlocked && targetableNow && (WantAOE ? In5y(aoeTarget) : In3y(aoeTarget)) && aoeTarget != null)
        {
            var normalComboPrio = nmActive && CombatTimer >= TimingOpenerEnd ? GCDPriority.Minimal : GCDPriority.Low;
            QueueGCD(aoeAction, aoeTarget, normalComboPrio);
        }

        DropLockedQueuedActions();

        if (Unlocked(AID.DemonSlice))
            GoalZoneCombined(strategy, AOENormalHighLevelTargetThreshold, Hints.GoalAOECircle(AOEMeleeRadius), AID.DemonSlice, Unlocked(AID.FatedCircle) && Ammo > 0 ? AOEGoalTargetFallback : NormalAOEThreshold(), maximumActionRange: AOEGoalMaxActionRange);
    }
}

internal enum AkechiGNBPlanner74HintSource
{
    BossTimeline = 10,
    BossPlanner = 20,
    ExternalEncounter = 25,
    Splatoon = 30,
    MechanicForecast = 35,
    Manual = 40,
}

internal readonly struct AkechiGNBPlanner74TargetWindow(float startCombatTime, float endCombatTime, bool targetable, int priority, AkechiGNBPlanner74HintSource source, string reason)
{
    public readonly float StartCombatTime = startCombatTime;
    public readonly float EndCombatTime = endCombatTime;
    public readonly bool Targetable = targetable;
    public readonly int Priority = priority;
    public readonly AkechiGNBPlanner74HintSource Source = source;
    public readonly string Reason = reason ?? "";

    public bool ActiveAt(float combatTime, float prePad, float postPad) => combatTime >= StartCombatTime - prePad && combatTime < EndCombatTime + postPad;
}

internal static class AkechiGNBPlanner74HintBus
{
    private const int HintTargetWindowInitialCapacity = 64;
    private const int HintTargetWindowMaxCount = 128;
    private const int HintTargetWindowTrimCount = 128;
    private const float HintTargetWindowPrunePadding = 5f;

    private static readonly object LockObj = new();
    private static readonly System.Collections.Generic.List<AkechiGNBPlanner74TargetWindow> Windows = new(HintTargetWindowInitialCapacity);

    public static void Clear()
    {
        lock (LockObj)
            Windows.Clear();
    }

    public static void ClearSource(AkechiGNBPlanner74HintSource source)
    {
        // called several times per frame: an in-place compaction instead of RemoveAll with a capturing lambda
        lock (LockObj)
        {
            var kept = 0;
            for (var i = 0; i < Windows.Count; ++i)
                if (Windows[i].Source != source)
                    Windows[kept++] = Windows[i];
            Windows.RemoveRange(kept, Windows.Count - kept);
        }
    }

    public static void PushDowntime(float nowCombatTime, float startsIn, float duration, AkechiGNBPlanner74HintSource source, string reason, int priority = 100)
    {
        if (duration <= 0f)
            return;

        var start = nowCombatTime + MathF.Max(0f, startsIn);
        PushWindowAbsolute(start, start + duration, targetable: false, source, reason, priority);
    }

    public static void PushUptime(float nowCombatTime, float startsIn, float duration, AkechiGNBPlanner74HintSource source, string reason, int priority = 90)
    {
        if (duration <= 0f)
            return;

        var start = nowCombatTime + MathF.Max(0f, startsIn);
        PushWindowAbsolute(start, start + duration, targetable: true, source, reason, priority);
    }

    public static void PushWindowAbsolute(float startCombatTime, float endCombatTime, bool targetable, AkechiGNBPlanner74HintSource source, string reason, int priority = 100)
    {
        if (endCombatTime <= startCombatTime)
            return;

        lock (LockObj)
        {
            Windows.Add(new(startCombatTime, endCombatTime, targetable, priority, source, reason));
            if (Windows.Count > HintTargetWindowMaxCount)
                Windows.RemoveRange(0, Windows.Count - HintTargetWindowTrimCount);
        }
    }

    // fills the caller's list (cleared first) with the windows overlapping [minTime, maxTime], after pruning expired ones
    public static void Snapshot(float minTime, float maxTime, System.Collections.Generic.List<AkechiGNBPlanner74TargetWindow> result)
    {
        result.Clear();
        lock (LockObj)
        {
            var pruneBefore = minTime - HintTargetWindowPrunePadding;
            var kept = 0;
            for (var i = 0; i < Windows.Count; ++i)
                if (!(Windows[i].EndCombatTime < pruneBefore))
                    Windows[kept++] = Windows[i];
            Windows.RemoveRange(kept, Windows.Count - kept);

            for (var i = 0; i < Windows.Count; ++i)
            {
                var w = Windows[i];
                if (w.EndCombatTime >= minTime && w.StartCombatTime <= maxTime)
                    result.Add(w);
            }
        }
    }
}

internal enum AkechiGNBPlanner74PhaseSource
{
    BossTimeline = 10,
    BossPlanner = 20,
    Splatoon = 30,
    Manual = 40,
}

internal readonly struct AkechiGNBPlanner74PhaseHint(int phase, float phaseStartCombatTime, AkechiGNBPlanner74PhaseSource source, string reason, int priority)
{
    public readonly int Phase = phase;
    public readonly float PhaseStartCombatTime = phaseStartCombatTime;
    public readonly AkechiGNBPlanner74PhaseSource Source = source;
    public readonly string Reason = reason ?? "";
    public readonly int Priority = priority;
}

internal static class AkechiGNBPlanner74PhaseBus
{
    private const int HintPhaseInitialCapacity = 16;
    private const int HintPhaseMaxCount = 32;

    private static readonly object LockObj = new();
    private static readonly System.Collections.Generic.List<AkechiGNBPlanner74PhaseHint> Phases = new(HintPhaseInitialCapacity);
    private static float CurrentCombatTime;

    public static void UpdateClock(float combatTime)
    {
        lock (LockObj)
            CurrentCombatTime = combatTime;
    }

    public static void Clear()
    {
        lock (LockObj)
            Phases.Clear();
    }

    public static void PushPhaseNow(int phase, AkechiGNBPlanner74PhaseSource source, string reason, int priority)
        => PushPhaseAbsolute(phase, CurrentCombatTime, source, reason, priority);

    public static void PushPhaseAbsolute(int phase, float phaseStartCombatTime, AkechiGNBPlanner74PhaseSource source, string reason, int priority)
    {
        if (phase <= 0)
            return;

        lock (LockObj)
        {
            Phases.RemoveAll(p => p.Source == source && p.Phase == phase);
            Phases.Add(new(phase, phaseStartCombatTime, source, reason, priority));
            if (Phases.Count > HintPhaseMaxCount)
                Phases.RemoveRange(0, Phases.Count - HintPhaseMaxCount);
        }
    }

    public static AkechiGNBPlanner74PhaseHint[] Snapshot()
    {
        lock (LockObj)
            return [.. Phases];
    }
}

internal static class AkechiGNBPlanner74IpcBridge
{
    private static readonly bool Enabled = true;
    private const float HintSplatoonLookahead = 90f;
    private const float HintSplatoonWindowDuration = 30f;
    private const float HintSplatoonPastTolerance = 1f;
    private const int HintSplatoonDefaultPriority = 140;
    private const int HintSplatoonMaxPriority = 180;
    private const long HintClockFreshMs = 2500;

    public const string PushDowntimeRelName = "AkechiGNB74.PushDowntimeRel";
    public const string PushUptimeRelName = "AkechiGNB74.PushUptimeRel";
    public const string PushTargetWindowAbsName = "AkechiGNB74.PushTargetWindowAbs";
    public const string PushPhaseNowName = "AkechiGNB74.PushPhaseNow";
    public const string PushPhaseAbsName = "AkechiGNB74.PushPhaseAbs";
    public const string ClearSplatoonHintsName = "AkechiGNB74.ClearSplatoonHints";

    private static readonly object LockObj = new();

    private static float CurrentCombatTime;
    private static bool InCombat;
    private static long LastClockUpdateMs;

    private static ICallGateProvider<float, float, string, int, bool>? PushDowntimeRelProvider;
    private static ICallGateProvider<float, float, string, int, bool>? PushUptimeRelProvider;
    private static ICallGateProvider<float, float, bool, string, int, bool>? PushTargetWindowAbsProvider;
    private static ICallGateProvider<int, string, int, bool>? PushPhaseNowProvider;
    private static ICallGateProvider<int, float, string, int, bool>? PushPhaseAbsProvider;
    private static ICallGateProvider<bool>? ClearSplatoonHintsProvider;

    public static void Register(IDalamudPluginInterface pi)
    {
        Unregister();
        if (!Enabled)
            return;

        PushDowntimeRelProvider = pi.GetIpcProvider<float, float, string, int, bool>(PushDowntimeRelName);
        PushDowntimeRelProvider.RegisterFunc(PushDowntimeRel);

        PushUptimeRelProvider = pi.GetIpcProvider<float, float, string, int, bool>(PushUptimeRelName);
        PushUptimeRelProvider.RegisterFunc(PushUptimeRel);

        PushTargetWindowAbsProvider = pi.GetIpcProvider<float, float, bool, string, int, bool>(PushTargetWindowAbsName);
        PushTargetWindowAbsProvider.RegisterFunc(PushTargetWindowAbs);

        PushPhaseNowProvider = pi.GetIpcProvider<int, string, int, bool>(PushPhaseNowName);
        PushPhaseNowProvider.RegisterFunc(PushPhaseNow);

        PushPhaseAbsProvider = pi.GetIpcProvider<int, float, string, int, bool>(PushPhaseAbsName);
        PushPhaseAbsProvider.RegisterFunc(PushPhaseAbs);

        ClearSplatoonHintsProvider = pi.GetIpcProvider<bool>(ClearSplatoonHintsName);
        ClearSplatoonHintsProvider.RegisterFunc(ClearSplatoonHints);
    }

    public static void Unregister()
    {
        PushDowntimeRelProvider?.UnregisterFunc();
        PushDowntimeRelProvider = null;

        PushUptimeRelProvider?.UnregisterFunc();
        PushUptimeRelProvider = null;

        PushTargetWindowAbsProvider?.UnregisterFunc();
        PushTargetWindowAbsProvider = null;

        PushPhaseNowProvider?.UnregisterFunc();
        PushPhaseNowProvider = null;

        PushPhaseAbsProvider?.UnregisterFunc();
        PushPhaseAbsProvider = null;

        ClearSplatoonHintsProvider?.UnregisterFunc();
        ClearSplatoonHintsProvider = null;
    }

    public static void UpdateClock(float combatTime, bool inCombat)
    {
        lock (LockObj)
        {
            var wasInCombat = InCombat;
            CurrentCombatTime = combatTime;
            InCombat = inCombat;
            LastClockUpdateMs = System.Environment.TickCount64;
            AkechiGNBPlanner74PhaseBus.UpdateClock(combatTime);

            if (wasInCombat && !inCombat)
            {
                AkechiGNBPlanner74HintBus.ClearSource(AkechiGNBPlanner74HintSource.Splatoon);
                AkechiGNBPlanner74PhaseBus.Clear();
            }
        }
    }

    private static bool ClockFresh()
    {
        lock (LockObj)
        {
            if (!InCombat)
                return false;

            return System.Environment.TickCount64 - LastClockUpdateMs <= HintClockFreshMs;
        }
    }

    private static float GetCurrentCombatTime()
    {
        lock (LockObj)
            return CurrentCombatTime;
    }

    private static int ClampPriority(int priority) => priority <= 0 ? HintSplatoonDefaultPriority : Math.Clamp(priority, 1, HintSplatoonMaxPriority);
    private static int ClampUptimePriority(int priority) => Math.Min(HintSplatoonMaxPriority, ClampPriority(priority) + 1);
    private static string CleanReason(string reason) => string.IsNullOrWhiteSpace(reason) ? "Splatoon IPC" : reason;

    private static bool ValidRelativeWindow(float startsIn, float duration)
    {
        if (!ClockFresh())
            return false;

        if (startsIn is < -HintSplatoonPastTolerance or > HintSplatoonLookahead)
            return false;

        return duration is > 0f and <= HintSplatoonWindowDuration;
    }

    private static bool ValidAbsoluteWindow(float startCombatTime, float endCombatTime)
    {
        if (!ClockFresh())
            return false;

        var now = GetCurrentCombatTime();
        if (startCombatTime < now - HintSplatoonPastTolerance || startCombatTime > now + HintSplatoonLookahead)
            return false;

        return endCombatTime > startCombatTime && endCombatTime - startCombatTime <= HintSplatoonWindowDuration;
    }

    private static bool ValidAbsoluteTimestamp(float combatTime)
    {
        if (!ClockFresh())
            return false;

        var now = GetCurrentCombatTime();
        return combatTime >= now - HintSplatoonPastTolerance && combatTime <= now + HintSplatoonLookahead;
    }

    private static bool PushDowntimeRel(float startsIn, float duration, string reason, int priority)
    {
        if (!ValidRelativeWindow(startsIn, duration))
            return false;

        AkechiGNBPlanner74HintBus.PushDowntime(GetCurrentCombatTime(), startsIn, duration, AkechiGNBPlanner74HintSource.Splatoon, CleanReason(reason), ClampPriority(priority));
        return true;
    }

    private static bool PushUptimeRel(float startsIn, float duration, string reason, int priority)
    {
        if (!ValidRelativeWindow(startsIn, duration))
            return false;

        AkechiGNBPlanner74HintBus.PushUptime(GetCurrentCombatTime(), startsIn, duration, AkechiGNBPlanner74HintSource.Splatoon, CleanReason(reason), ClampUptimePriority(priority));
        return true;
    }

    private static bool PushTargetWindowAbs(float startCombatTime, float endCombatTime, bool targetable, string reason, int priority)
    {
        if (!ValidAbsoluteWindow(startCombatTime, endCombatTime))
            return false;

        var p = targetable ? ClampUptimePriority(priority) : ClampPriority(priority);

        AkechiGNBPlanner74HintBus.PushWindowAbsolute(startCombatTime, endCombatTime, targetable, AkechiGNBPlanner74HintSource.Splatoon, CleanReason(reason), p);
        return true;
    }

    private static bool PushPhaseNow(int phase, string reason, int priority)
    {
        if (!ClockFresh() || phase <= 0)
            return false;

        AkechiGNBPlanner74PhaseBus.PushPhaseNow(phase, AkechiGNBPlanner74PhaseSource.Splatoon, CleanReason(reason), ClampPriority(priority));
        return true;
    }

    private static bool PushPhaseAbs(int phase, float phaseStartCombatTime, string reason, int priority)
    {
        if (phase <= 0 || !ValidAbsoluteTimestamp(phaseStartCombatTime))
            return false;

        AkechiGNBPlanner74PhaseBus.PushPhaseAbsolute(phase, phaseStartCombatTime, AkechiGNBPlanner74PhaseSource.Splatoon, CleanReason(reason), ClampPriority(priority));
        return true;
    }

    private static bool ClearSplatoonHints()
    {
        AkechiGNBPlanner74HintBus.ClearSource(AkechiGNBPlanner74HintSource.Splatoon);
        AkechiGNBPlanner74PhaseBus.Clear();
        return true;
    }
}
