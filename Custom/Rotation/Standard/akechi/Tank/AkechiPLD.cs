using BossMod.PLD;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.akechi.Custom;

public sealed class AkechiPLD(RotationModuleManager manager, Actor player) : AkechiTools<AID, TraitID>(manager, player)
{
    public enum Track { Potion = SharedTrack.Potion, AOE = SharedTrack.Count, Atonement, BladeCombo, FightOrFlight, Requiescat, GoringBlade, Holy, Dash, Ranged, SpiritsWithin, CircleOfScorn, BladeOfHonor, RotationMode, MechanicHints }
    public enum AOEStrategy { AutoFinish, ForceSTFinish, ForceAOEFinish, AutoBreak, ForceSTBreak, ForceAOEBreak }
    public enum AtonementStrategy { Automatic, ForceAtonement, ForceSupplication, ForceSepulchre, Delay }
    public enum BladeComboStrategy { Automatic, ForceConfiteor, ForceFaith, ForceTruth, ForceValor, Delay }
    public enum GoringBladeStrategy { Automatic, Early, Late, Force, Delay }
    public enum HolyStrategy { Automatic, Early, Late, OnlySpirit, OnlyCircle, ForceSpirit, ForceCircle, Delay }
    public enum DashStrategy { Automatic, Force, Force1, GapClose, GapClose1, Delay }
    public enum BuffsStrategy { Automatic, Together, RaidBuffsOnly, Force, ForceWeave, Delay }
    public enum RangedStrategy { Automatic, OpenerRangedCast, OpenerCast, ForceCast, RangedCast, RangedCastStationary, OpenerRanged, Opener, Force, Ranged, Forbid }
    public enum RotationModeStrategy { FullMode, NormalAtonementHoly }
    public enum PotionStrategyPLD
    {
        Disabled = 0,
        AlignWithFoF = 1,
        UseAtCombatStart = 2,
        UseAtSixMinuteBurst = 3,
        Immediate = 4,
        UseBeforeEightMinuteBurst = 5,
        FinalBurst = 6
    }

    public static RotationModuleDefinition Definition()
    {
        var res = new RotationModuleDefinition("Akechi PLD [Custom]", "標準回しモジュール", "標準回し(Akechi)|タンク", "Akechi", RotationModuleQuality.Good, BitMask.Build((int)Class.GLA, (int)Class.PLD), 100);

        res.DefineTargeting();
        res.DefineHold();
        res.Define(Track.Potion).As<PotionStrategyPLD>("Potion", "薬品使用", 280)
            .AddOption(PotionStrategyPLD.Disabled, "薬を自動使用しない")
            .AddOption(PotionStrategyPLD.AlignWithFoF, "Fight or Flight 直前または効果中に薬を自動使用", supportedTargets: ActionTargets.Self)
            .AddOption(PotionStrategyPLD.UseAtCombatStart, "開幕の Fight or Flight に薬を自動使用", supportedTargets: ActionTargets.Self)
            .AddOption(PotionStrategyPLD.UseAtSixMinuteBurst, "6分付近の Fight or Flight に薬を自動使用", supportedTargets: ActionTargets.Self)
            .AddOption(PotionStrategyPLD.Immediate, "薬を即時使用", 270, 30, ActionTargets.Self)
            .AddOption(PotionStrategyPLD.UseBeforeEightMinuteBurst, "8分付近の Fight or Flight に薬を自動使用", supportedTargets: ActionTargets.Self)
            .AddOption(PotionStrategyPLD.FinalBurst, "戦闘終了前の最後の Fight or Flight に薬を自動使用", supportedTargets: ActionTargets.Self)
            .AddAssociatedAction(ActionDefinitions.IDPotionStr);

        res.Define(Track.AOE).As<AOEStrategy>("ST/AOE", "単体 / 範囲回し", 300)
            .AddOption(AOEStrategy.AutoFinish, "周囲の敵数に応じて最適な回しを自動選択する - 可能なら現在のコンボを完走する")
            .AddOption(AOEStrategy.ForceSTFinish, "周囲の敵数に関係なく単体回しを強制する - 可能なら現在のコンボを完走する")
            .AddOption(AOEStrategy.ForceAOEFinish, "周囲の敵数に関係なく範囲回しを強制する - 可能なら現在のコンボを完走する")
            .AddOption(AOEStrategy.AutoBreak, "周囲の敵数に応じて最適な回しを自動選択する - コンボ中なら中断する")
            .AddOption(AOEStrategy.ForceSTBreak, "周囲の敵数に関係なく単体回しを強制する - コンボ中なら中断する")
            .AddOption(AOEStrategy.ForceAOEBreak, "周囲の敵数に関係なく範囲回しを強制する - コンボ中なら中断する")
            .AddAssociatedActions(AID.FastBlade, AID.RiotBlade, AID.RageOfHalone, AID.RoyalAuthority, AID.TotalEclipse, AID.Prominence, AID.HolyCircle, AID.Imperator, AID.Confiteor, AID.BladeOfFaith, AID.BladeOfTruth, AID.BladeOfValor, AID.BladeOfHonor, AID.Expiacion);

        res.Define(Track.Atonement).As<AtonementStrategy>("Atones", "Atonement Combo", 155)
            .AddOption(AtonementStrategy.Automatic, "Atonement とそのコンボ連携を通常通り使う")
            .AddOption(AtonementStrategy.ForceAtonement, "Atonement を強制する", 0, 30, ActionTargets.Hostile, 76)
            .AddOption(AtonementStrategy.ForceSupplication, "Supplication を強制する", 0, 30, ActionTargets.Hostile, 76)
            .AddOption(AtonementStrategy.ForceSepulchre, "Sepulchre を強制する", 0, 0, ActionTargets.Hostile, 76)
            .AddOption(AtonementStrategy.Delay, "Atonement 連携を遅らせる", 0, 0, ActionTargets.None, 60)
            .AddAssociatedActions(AID.Atonement, AID.Supplication, AID.Sepulchre);

        res.Define(Track.BladeCombo).As<BladeComboStrategy>("Blades", "Confiteor + Blade Combo", 160)
            .AddOption(BladeComboStrategy.Automatic, "Confiteor と Blade 連携を通常通り使う")
            .AddOption(BladeComboStrategy.ForceConfiteor, "Confiteor を強制する", 0, 0, ActionTargets.Hostile, 80)
            .AddOption(BladeComboStrategy.ForceFaith, "Blade of Faith を強制する", 0, 0, ActionTargets.Hostile, 90)
            .AddOption(BladeComboStrategy.ForceTruth, "Blade of Truth を強制する", 0, 0, ActionTargets.Hostile, 90)
            .AddOption(BladeComboStrategy.ForceValor, "Blade of Valor を強制する", 0, 0, ActionTargets.Hostile, 90)
            .AddOption(BladeComboStrategy.Delay, "Confiteor / Blade 連携を遅らせる", 0, 0, ActionTargets.None, 80)
            .AddAssociatedActions(AID.Confiteor, AID.BladeOfFaith, AID.BladeOfTruth, AID.BladeOfValor);

        res.Define(Track.FightOrFlight).As<BuffsStrategy>("FoF", "Fight or Flight", 170)
            .AddOption(BuffsStrategy.Automatic, "Fight or Flight を通常通り使う")
            .AddOption(BuffsStrategy.Together, "Requiescat / Imperator と合わせるときだけ Fight or Flight を使う - ずれている場合は合わせるために遅らせる", 60, 20, ActionTargets.Self, 2)
            .AddOption(BuffsStrategy.RaidBuffsOnly, "レイドバフに合わせるときだけ Fight or Flight を使う - 合わせるために遅らせる", 60, 20, ActionTargets.Self, 2)
            .AddOption(BuffsStrategy.Force, "Fight or Flight を強制する", 60, 20, ActionTargets.Self, 2)
            .AddOption(BuffsStrategy.ForceWeave, "次の可能な挟み枠で Fight or Flight を強制する", 60, 20, ActionTargets.Self, 2)
            .AddOption(BuffsStrategy.Delay, "Fight or Flight を遅らせる", 0, 0, ActionTargets.None, 2)
            .AddAssociatedActions(AID.FightOrFlight);

        res.Define(Track.Requiescat).As<BuffsStrategy>("Req.", "Requiescat / Imperator", 165)
            .AddOption(BuffsStrategy.Automatic, "Requiescat / Imperator を通常通り使う")
            .AddOption(BuffsStrategy.Together, "Fight or Flight と合わせるときだけ Requiescat / Imperator を使う - 合わせるために遅らせる", 60, 20, ActionTargets.Hostile, 68)
            .AddOption(BuffsStrategy.RaidBuffsOnly, "レイドバフに合わせるときだけ Requiescat / Imperator を使う - 合わせるために遅らせる", 60, 20, ActionTargets.Hostile, 68)
            .AddOption(BuffsStrategy.Force, "Requiescat / Imperator を強制する", 60, 20, ActionTargets.Hostile, 68)
            .AddOption(BuffsStrategy.ForceWeave, "次の可能な挟み枠で Requiescat / Imperator を強制する", 60, 20, ActionTargets.Hostile, 68)
            .AddOption(BuffsStrategy.Delay, "Requiescat / Imperator を遅らせる", 0, 0, ActionTargets.None, 68)
            .AddAssociatedActions(AID.Requiescat, AID.Imperator);

        res.Define(Track.GoringBlade).As<GoringBladeStrategy>("GB", "Goring Blade", 135)
            .AddOption(GoringBladeStrategy.Automatic, "Goring Blade を通常通り使う")
            .AddOption(GoringBladeStrategy.Early, "Requiescat スタック消費前に Goring Blade を使う", 0, 0, ActionTargets.Hostile, 68)
            .AddOption(GoringBladeStrategy.Late, "Requiescat スタック消費後に Goring Blade を使う", 0, 0, ActionTargets.Hostile, 68)
            .AddOption(GoringBladeStrategy.Force, "Goring Blade を強制する", 0, 0, ActionTargets.Hostile, 54)
            .AddOption(GoringBladeStrategy.Delay, "Goring Blade を遅らせる", 0, 0, ActionTargets.None, 54)
            .AddAssociatedActions(AID.GoringBlade);

        res.Define(Track.Holy).As<HolyStrategy>("Holy", "Holy Spirit / Circle", 150)
            .AddOption(HolyStrategy.Automatic, "Divine Might 中は対象数に応じて最適な Holy 系を自動選択する")
            .AddOption(HolyStrategy.Early, "Divine Might 中の最適な Holy 系を最速で使う", 0, 0, ActionTargets.Hostile, 68)
            .AddOption(HolyStrategy.Late, "Atonement 連携後、または他に何も無ければ最適な Holy 系を使う", 0, 0, ActionTargets.Hostile, 68)
            .AddOption(HolyStrategy.OnlySpirit, "Holy Spirit のみを最適な Holy 技として使う", 0, 0, ActionTargets.Hostile, 64)
            .AddOption(HolyStrategy.OnlyCircle, "Holy Circle のみを最適な Holy 技として使う", 0, 0, ActionTargets.Hostile, 72)
            .AddOption(HolyStrategy.ForceSpirit, "Holy Spirit を強制する", 0, 0, ActionTargets.Hostile, 64)
            .AddOption(HolyStrategy.ForceCircle, "Holy Circle を強制する", 0, 0, ActionTargets.Hostile, 72)
            .AddOption(HolyStrategy.Delay, "Holy 系を遅らせる", 0, 0, ActionTargets.None, 64)
            .AddAssociatedActions(AID.HolySpirit, AID.HolyCircle);

        res.Define(Track.Dash).As<DashStrategy>("Dash", "Intervene", 95)
            .AddOption(DashStrategy.Automatic, "Intervene を通常通り使う")
            .AddOption(DashStrategy.Force, "Intervene を強制する", 30, 0, ActionTargets.Hostile, 66)
            .AddOption(DashStrategy.Force1, "Intervene を強制する - 1チャージは手動用に残す", 30, 0, ActionTargets.Hostile, 66)
            .AddOption(DashStrategy.GapClose, "近接範囲外なら gap closer として使う", 30, 0, ActionTargets.None, 66)
            .AddOption(DashStrategy.GapClose1, "近接範囲外なら gap closer として使う - 1チャージは手動用に残す", 30, 0, ActionTargets.None, 66)
            .AddOption(DashStrategy.Delay, "Intervene を遅らせる", 0, 0, ActionTargets.None, 66)
            .AddAssociatedActions(AID.Intervene);

        res.Define(Track.Ranged).As<RangedStrategy>("Ranged", "Shield Lob / Holy Spirit", 100)
            .AddOption(RangedStrategy.Automatic, "停止中は Holy Spirit、移動中は Shield Lob を使う")
            .AddOption(RangedStrategy.OpenerRangedCast, "戦闘開始時に近接範囲外なら Holy Spirit を使う", 0, 0, ActionTargets.Hostile, 64)
            .AddOption(RangedStrategy.OpenerCast, "戦闘開始時に距離に関係なく Holy Spirit を使う", 0, 0, ActionTargets.Hostile, 64)
            .AddOption(RangedStrategy.ForceCast, "Holy Spirit を強制する", 0, 0, ActionTargets.Hostile, 64)
            .AddOption(RangedStrategy.RangedCast, "近接範囲外なら遠隔択として Holy Spirit を使う", 0, 0, ActionTargets.Hostile, 64)
            .AddOption(RangedStrategy.RangedCastStationary, "近接範囲外かつ停止中なら遠隔択として Holy Spirit を使う", 0, 0, ActionTargets.Hostile, 64)
            .AddOption(RangedStrategy.OpenerRanged, "戦闘開始時に近接範囲外なら Shield Lob を使う", 0, 0, ActionTargets.Hostile, 15)
            .AddOption(RangedStrategy.Opener, "戦闘開始時に距離に関係なく Shield Lob を使う", 0, 0, ActionTargets.Hostile, 15)
            .AddOption(RangedStrategy.Force, "Shield Lob を強制する", 0, 0, ActionTargets.Hostile, 15)
            .AddOption(RangedStrategy.Ranged, "近接範囲外なら遠隔択として Shield Lob を使う", 0, 0, ActionTargets.Hostile, 15)
            .AddOption(RangedStrategy.Forbid, "遠隔攻撃をすべて禁止する", 0, 0, ActionTargets.Hostile, 15)
            .AddAssociatedActions(AID.ShieldLob, AID.HolySpirit);

        res.DefineOGCD(Track.SpiritsWithin, AID.SpiritsWithin, "SW", "Spirits Within / Expiacion", 145, 30, 0, ActionTargets.Hostile, 30).AddAssociatedActions(AID.SpiritsWithin, AID.Expiacion);
        res.DefineOGCD(Track.CircleOfScorn, AID.CircleOfScorn, "CoS", "Circle of Scorn", 140, 30, 15, ActionTargets.Self, 50).AddAssociatedActions(AID.CircleOfScorn);
        res.DefineOGCD(Track.BladeOfHonor, AID.BladeOfHonor, "BoH", "Blade of Honor", 130, 0, 0, ActionTargets.Hostile, 100).AddAssociatedActions(AID.BladeOfHonor);
        res.Define(Track.RotationMode).As<RotationModeStrategy>("RotationMode", "ローテーションモード", 320)
            .AddOption(RotationModeStrategy.FullMode, "フルモード")
            .AddOption(RotationModeStrategy.NormalAtonementHoly, "通常回し+Atonement+Holy");
        res.DefineMechanicHints(Track.MechanicHints);

        return res;
    }

    private int BladeComboStep;
    private (float Left, bool IsActive) DivineMight;
    private (float CD, float Left, bool IsReady, bool IsActive) FightOrFlight;
    private (float Left, bool IsReady, bool IsActive) GoringBlade;
    private (int Charges, bool IsReady) Intervene;
    private (float CD, float Left, bool IsReady, bool IsActive) Requiescat;
    private (float Left, bool IsReady, bool IsActive) Atonement;
    private (float Left, bool IsReady, bool IsActive) Supplication;
    private (float Left, bool IsReady, bool IsActive) Sepulchre;
    private (float Left, bool IsReady, bool IsActive) Confiteor;
    private (float Left, bool IsReady, bool IsActive) BladeOfHonor;
    private bool WantAOE;
    private bool Opener;
    private int NumSplashTargets;
    private Enemy? BestSplashTargets;
    private Enemy? BestSplashTarget;
    private bool ForceAOE;
    private float EstimatedFightEnd = float.MaxValue;
    private float PendingHPRatio;
    private float PendingHPTime;
    private float EstimatedHPDrain;
    private ulong FightEstimateTargetID;
    private float LastPotionQueuedAt = -9999f;

    private AID BestSpirits => Unlocked(AID.Expiacion) ? AID.Expiacion : AID.SpiritsWithin;
    private AID BestRequiescat => Unlocked(AID.Imperator) ? AID.Imperator : AID.Requiescat;
    private bool CanUseTotalEclipse => Unlocked(AID.TotalEclipse);
    private bool CanUseProminence => Unlocked(AID.Prominence);
    private AID BestAOEStarter => CanUseTotalEclipse ? AID.TotalEclipse : AID.FastBlade;
    private AID BestAOECombo => CanUseProminence ? AID.Prominence : BestAOEStarter;

    private bool IsSelfCenteredGCD(AID action) => action is AID.TotalEclipse or AID.Prominence or AID.HolyCircle;
    private Actor? TargetForGCD(AID action, Actor? enemyTarget) => IsSelfCenteredGCD(action) ? Player : enemyTarget;

    private bool InRangeForGCD(AID action, Actor? target)
    {
        if (target == null)
            return false;

        return action switch
        {
            AID.TotalEclipse or AID.Prominence or AID.HolyCircle => TargetsInAOECircle(5f, 1),
            AID.ShieldLob => In20y(target),
            AID.HolySpirit or AID.Confiteor or AID.BladeOfFaith or AID.BladeOfTruth or AID.BladeOfValor => In25y(target),
            _ => In3y(target)
        };
    }

    private bool CanStartPLDBurst(Actor? target)
    {
        if (!Player.InCombat || target == null)
            return false;

        return In3y(target) || (BestRequiescat == AID.Imperator && In25y(target));
    }

    private bool CanUseBestRequiescat(Actor? target)
    {
        if (!Player.InCombat || target == null)
            return false;

        return BestRequiescat == AID.Imperator ? In25y(target) : In3y(target);
    }

    private (bool, OGCDPriority) ShouldUseFightOrFlight(BuffsStrategy strategy, Actor? target, bool ready, bool together)
    {
        if (!ready)
            return (false, OGCDPriority.None);

        var minimal = CanStartPLDBurst(target);
        var downtimeOK = CanUseMajorBuffBeforeDowntime(strategy, 15f);
        return strategy switch
        {
            BuffsStrategy.Automatic => (minimal && Opener && downtimeOK, OGCDPriority.Severe),
            BuffsStrategy.Together => (minimal && together && downtimeOK, OGCDPriority.Severe),
            BuffsStrategy.RaidBuffsOnly => (minimal && downtimeOK && (RaidBuffsLeft > 0 || RaidBuffsIn <= 5f), OGCDPriority.Severe),
            BuffsStrategy.Force => (minimal, OGCDPriority.Forced),
            BuffsStrategy.ForceWeave => (minimal, OGCDPriority.Forced),
            _ => (false, OGCDPriority.None)
        };
    }

    private (bool, OGCDPriority) ShouldUseRequiescatOrImperator(BuffsStrategy strategy, Actor? target, bool ready, bool together)
    {
        if (!ready)
            return (false, OGCDPriority.None);

        var minimal = CanUseBestRequiescat(target);
        var downtimeOK = CanUseMajorBuffBeforeDowntime(strategy, 8f);
        return strategy switch
        {
            BuffsStrategy.Automatic => (minimal && together && downtimeOK, OGCDPriority.Severe),
            BuffsStrategy.Together => (minimal && together && downtimeOK, OGCDPriority.Severe),
            BuffsStrategy.RaidBuffsOnly => (minimal && downtimeOK && (RaidBuffsLeft > 0 || RaidBuffsIn <= 5f), OGCDPriority.Severe),
            BuffsStrategy.Force => (minimal, OGCDPriority.Forced),
            BuffsStrategy.ForceWeave => (minimal, OGCDPriority.Forced),
            _ => (false, OGCDPriority.None)
        };
    }
    private bool ShouldUsePotion(StrategyValues strategy, Actor? target, BuffsStrategy fofStrat)
    {
        var potion = strategy.Option(Track.Potion).As<PotionStrategyPLD>();
        if (potion != PotionStrategyPLD.Immediate && strategy.HoldCDs())
            return false;

        return potion switch
        {
            PotionStrategyPLD.Immediate => BasicPotionWindow(),
            PotionStrategyPLD.AlignWithFoF => FoFPotionWindow(target, fofStrat),
            PotionStrategyPLD.UseAtCombatStart => CombatTimer < 30f && FoFPotionWindow(target, fofStrat),
            PotionStrategyPLD.UseAtSixMinuteBurst => TimedFoFPotionWindow(360f, target, fofStrat),
            PotionStrategyPLD.UseBeforeEightMinuteBurst => TimedFoFPotionWindow(480f, target, fofStrat),
            PotionStrategyPLD.FinalBurst => FinalBurstPotionWindow(target, fofStrat),
            _ => false
        };
    }

    private bool BasicPotionWindow() => Player.InCombat && !Player.IsDead && CanWeaveIn;

    private bool FoFPotionWindow(Actor? target, BuffsStrategy fofStrat)
    {
        if (!BasicPotionWindow())
            return false;

        if (!CanStartPLDBurst(target))
            return false;

        if (FightOrFlight.IsActive)
            return CanUseMajorBuffBeforeDowntime(fofStrat, 8f);

        return FightOrFlight.CD <= 4f && CanUseMajorBuffBeforeDowntime(fofStrat, 25f);
    }

    private bool TimedFoFPotionWindow(float targetFoFAt, Actor? target, BuffsStrategy fofStrat)
    {
        var nextFoFAt = FightOrFlight.IsActive ? CombatTimer : CombatTimer + FightOrFlight.CD;
        return nextFoFAt >= targetFoFAt - 10f && nextFoFAt <= targetFoFAt + 10f && FoFPotionWindow(target, fofStrat);
    }

    private bool FinalBurstPotionWindow(Actor? target, BuffsStrategy fofStrat)
    {
        var fightLeft = EstimatedFightEnd - CombatTimer;
        return fightLeft is > 0f and <= 35f && FoFPotionWindow(target, fofStrat);
    }

    private void ResetFightEstimate()
    {
        EstimatedFightEnd = float.MaxValue;
        PendingHPRatio = 0f;
        PendingHPTime = 0f;
        EstimatedHPDrain = 0f;
        FightEstimateTargetID = 0ul;
        LastPotionQueuedAt = -9999f;
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
        if (elapsed < 2f)
            return;

        if (hpRatio < PendingHPRatio)
        {
            var hpDrain = (PendingHPRatio - hpRatio) / elapsed;
            EstimatedHPDrain = EstimatedHPDrain > 0f ? EstimatedHPDrain * 0.7f + hpDrain * 0.3f : hpDrain;
            EstimatedFightEnd = EstimatedHPDrain > 0.0001f ? CombatTimer + hpRatio / EstimatedHPDrain : float.MaxValue;
        }
        else
        {
            EstimatedFightEnd = float.MaxValue;
            EstimatedHPDrain = 0f;
        }

        PendingHPRatio = hpRatio;
        PendingHPTime = CombatTimer;
    }

    private AID BestEnder => Unlocked(AID.RoyalAuthority) ? AID.RoyalAuthority : Unlocked(AID.RageOfHalone) ? AID.RageOfHalone : AID.FastBlade;
    private bool FoFGCDScoreActive => FightOrFlight.IsActive;
    private bool IsBladeComboAction(AID action) => action is AID.Confiteor or AID.BladeOfFaith or AID.BladeOfTruth or AID.BladeOfValor;
    private GCDPriority BladeComboPriority(AID action) => !FoFGCDScoreActive
        ? GCDPriority.ModeratelyHigh
        : IsBladeComboAction(action) ? GCDPriority.VeryHigh : FoFHolyPriority(GCDPriority.ModeratelyHigh, false);
    private GCDPriority AtonementPriority(AID action) => FoFGCDScoreActive ? action switch
    {
        AID.Sepulchre => GCDPriority.High - 1,
        AID.Supplication => GCDPriority.SlightlyHigh - 2,
        AID.Atonement => GCDPriority.SlightlyHigh - 3,
        _ => GCDPriority.AboveAverage
    } : GCDPriority.AboveAverage;
    private GCDPriority FoFHolyPriority(GCDPriority defaultPriority, bool urgent) => FoFGCDScoreActive
        ? GCDPriority.SlightlyHigh + 1
        : urgent ? GCDPriority.SlightlyHigh : defaultPriority;
    private GCDPriority ComboPriority(AID action) => FoFGCDScoreActive && action == BestEnder ? GCDPriority.SlightlyHigh : GCDPriority.Low;
    // Spec rule 2b: before a long target loss the last GCD slots go to the strongest procs (Goring Blade, Divine Might Holy, the Atonement
    // and Confiteor chains) and the last weave slots to the oGCDs whose recast is back by the return. Chains already running keep their path.
    private void QueueMechanicWindDown(StrategyValues strategy, Actor? mainTarget)
    {
        if (!WindDown.Active(Mechanic, SkSGCDLength) || mainTarget == null || BladeComboStep != 0 || Supplication.IsActive || Sepulchre.IsActive)
            return;

        float P(AID aid) => WindDownPotency.Of(WindDownPotency.PLD, (uint)aid);
        var gcds = new WindDownCandidate[4];
        var n = 0;
        // per-action Delay settings belong to the player
        bool Allowed<T>(Track track, T delay) where T : Enum => !strategy.Option(track).As<T>().Equals(delay);
        if (GoringBlade.IsReady && Allowed(Track.GoringBlade, GoringBladeStrategy.Delay))
            gcds[n++] = new(ActionID.MakeSpell(AID.GoringBlade), [P(AID.GoringBlade)], 0, 0);
        if (DivineMight.IsActive && MP >= 1000 && Unlocked(AID.HolySpirit) && Allowed(Track.Holy, HolyStrategy.Delay))
            gcds[n++] = new(ActionID.MakeSpell(AID.HolySpirit), [P(AID.HolySpirit)], 0, 0);
        if (Atonement.IsReady && Allowed(Track.Atonement, AtonementStrategy.Delay))
            gcds[n++] = new(ActionID.MakeSpell(AID.Atonement), [P(AID.Atonement), P(AID.Supplication), P(AID.Sepulchre)], 0, 0);
        if (Confiteor.IsReady && Allowed(Track.BladeCombo, BladeComboStrategy.Delay))
            gcds[n++] = new(ActionID.MakeSpell(AID.Confiteor), [P(AID.Confiteor), P(AID.BladeOfFaith), P(AID.BladeOfTruth), P(AID.BladeOfValor)], 0, 0);
        var pick = WindDown.SelectGcd(gcds.AsSpan(0, n), Mechanic, GCD, SkSGCDLength, 0.6f, WindDownPotency.FillerPLD);
        if (pick >= 0)
            QueueGCD((AID)gcds[pick].Action.ID, mainTarget, GCDPriority.VeryHigh + 5);

        var ogcds = new WindDownCandidate[3];
        var m = 0;
        if (Unlocked(AID.CircleOfScorn) && Allowed(Track.CircleOfScorn, OGCDStrategy.Delay))
            ogcds[m++] = new(ActionID.MakeSpell(AID.CircleOfScorn), [P(AID.CircleOfScorn)], ReadyIn(AID.CircleOfScorn), RecastRecoveredIn(AID.CircleOfScorn));
        var spirits = Unlocked(AID.Expiacion) ? AID.Expiacion : AID.SpiritsWithin;
        if (Unlocked(spirits) && Allowed(Track.SpiritsWithin, OGCDStrategy.Delay))
            ogcds[m++] = new(ActionID.MakeSpell(spirits), [P(AID.Expiacion)], ReadyIn(spirits), RecastRecoveredIn(spirits));
        if (BladeOfHonor.IsReady && Allowed(Track.BladeOfHonor, OGCDStrategy.Delay))
            ogcds[m++] = new(ActionID.MakeSpell(AID.BladeOfHonor), [P(AID.BladeOfHonor)], 0, 0);
        var op = WindDown.SelectOgcd(ogcds.AsSpan(0, m), Mechanic, SkSGCDLength);
        if (op >= 0)
        {
            var aid = (AID)ogcds[op].Action.ID;
            QueueOGCD(aid, aid == AID.CircleOfScorn ? Player : mainTarget, OGCDPriority.Severe + 5);
        }
    }

    // spec rule 1: the boss module's downtime and the predicted target loss, whichever comes first
    // (a predicted loss without a known return does not count: holding for it could cost a whole use, spec rule 1)
    private float EffectiveDowntimeIn => Mechanic.Enabled && Mechanic.ReturnKnown ? Math.Min(DowntimeIn, Mechanic.TargetLossIn) : DowntimeIn;
    private bool HasKnownDowntime => Player.InCombat && EffectiveDowntimeIn >= 0f && EffectiveDowntimeIn < float.MaxValue && !float.IsNaN(EffectiveDowntimeIn) && !float.IsInfinity(EffectiveDowntimeIn);
    private bool ActionLikelyLandsBeforeDowntime(float safety = 0.8f) => !HasKnownDowntime || EffectiveDowntimeIn > safety;
    private bool CanUseMajorBuffBeforeDowntime(BuffsStrategy strategy, float requiredUptime)
        => strategy is BuffsStrategy.Force or BuffsStrategy.ForceWeave || !HasKnownDowntime || EffectiveDowntimeIn >= requiredUptime;
    private bool CanUseSingleHitBeforeDowntime(OGCDStrategy strategy) => strategy is not OGCDStrategy.Automatic || ActionLikelyLandsBeforeDowntime();

    public override void Execution(StrategyValues strategy, Enemy? primaryTarget)
    {
        UpdateMechanicForecast(strategy.Option(Track.MechanicHints).As<MechanicHintStrategy>());
        GetNextTarget(strategy, ref primaryTarget, 3);
        var mainTarget = primaryTarget?.Actor;

        var gauge = World.Client.GetGauge<PaladinGauge>();
        BladeComboStep = gauge.ConfiteorComboStep;
        DivineMight.Left = Status(SID.DivineMight, 30);
        DivineMight.IsActive = DivineMight.Left > 0f;
        FightOrFlight.CD = Cooldown(AID.FightOrFlight);
        FightOrFlight.Left = Status(SID.FightOrFlight, 20);
        FightOrFlight.IsActive = FightOrFlight.Left > 0f;
        FightOrFlight.IsReady = Unlocked(AID.FightOrFlight) && ActionReady(AID.FightOrFlight);
        GoringBlade.Left = Status(SID.GoringBladeReady, 30);
        GoringBlade.IsActive = GoringBlade.Left > 0f;
        GoringBlade.IsReady = Unlocked(AID.GoringBlade) && GoringBlade.IsActive;
        Intervene.Charges = Unlocked(AID.Intervene) ? Charges(AID.Intervene) : 0;
        Intervene.IsReady = Unlocked(AID.Intervene) && HasCharge(AID.Intervene);
        Requiescat.CD = Cooldown(BestRequiescat);
        Requiescat.Left = Status(SID.Requiescat, 30);
        Requiescat.IsActive = Requiescat.Left > 0;
        Requiescat.IsReady = Unlocked(BestRequiescat) && Requiescat.CD < 0.6f;
        Atonement.Left = Status(SID.AtonementReady, 30);
        Atonement.IsActive = Atonement.Left > 0;
        Atonement.IsReady = Unlocked(AID.Atonement) && Atonement.IsActive;
        Supplication.Left = Status(SID.SupplicationReady, 30);
        Supplication.IsActive = Supplication.Left > 0;
        Supplication.IsReady = Unlocked(AID.Supplication) && Supplication.IsActive;
        Sepulchre.Left = Status(SID.SepulchreReady, 30);
        Sepulchre.IsActive = Sepulchre.Left > 0;
        Sepulchre.IsReady = Unlocked(AID.Sepulchre) && Sepulchre.IsActive;
        Confiteor.Left = Status(SID.ConfiteorReady, 30);
        Confiteor.IsActive = Confiteor.Left > 0;
        Confiteor.IsReady = Unlocked(AID.Confiteor) && Confiteor.IsActive && MP >= 1000;
        BladeOfHonor.Left = Status(SID.BladeOfHonorReady, 30);
        BladeOfHonor.IsActive = BladeOfHonor.Left > 0;
        BladeOfHonor.IsReady = Unlocked(AID.BladeOfHonor) && BladeOfHonor.IsActive;
        (BestSplashTargets, NumSplashTargets) = GetBestTarget(strategy, primaryTarget, 25, IsSplashTarget);
        BestSplashTarget = NumSplashTargets > 1 || primaryTarget == null ? BestSplashTargets : primaryTarget;
        var rangedTarget = BestSplashTarget?.Actor;
        var burstTarget = mainTarget ?? rangedTarget;
        Opener = CombatTimer <= 10 ? ComboLastMove == BestEnder : ComboTimer > 10;
        UpdateFightEstimate(burstTarget);

        if (strategy.HoldEverything())
            return;

        var rotationMode = strategy.Option(Track.RotationMode).As<RotationModeStrategy>();
        var fullMode = rotationMode == RotationModeStrategy.FullMode;
        var aoe = strategy.Option(Track.AOE);
        var aoeStrat = aoe.As<AOEStrategy>();
        var forceST = aoeStrat is AOEStrategy.ForceSTFinish or AOEStrategy.ForceSTBreak;
        ForceAOE = aoeStrat is AOEStrategy.ForceAOEFinish or AOEStrategy.ForceAOEBreak;
        var useAOERotation = CanUseTotalEclipse && !forceST && (ForceAOE || TargetsInAOECircle(5f, 3));
        WantAOE = useAOERotation;
        var stFinish = ComboLastMove switch
        {
            AID.TotalEclipse => CanUseProminence ? AID.Prominence : BestAOEStarter,
            AID.RiotBlade => Unlocked(BestEnder) ? BestEnder : AID.FastBlade,
            AID.FastBlade => Unlocked(AID.RiotBlade) ? AID.RiotBlade : AID.FastBlade,
            _ => AID.FastBlade,
        };
        var stBreak = ComboLastMove switch
        {
            AID.RiotBlade => Unlocked(BestEnder) ? BestEnder : AID.FastBlade,
            AID.FastBlade => Unlocked(AID.RiotBlade) ? AID.RiotBlade : AID.FastBlade,
            _ => AID.FastBlade,
        };
        var aoeFinish = ComboLastMove switch
        {
            AID.TotalEclipse => BestAOECombo,
            _ => BestAOEStarter,
        };
        var aoeBreak = ComboLastMove is AID.TotalEclipse && CanUseProminence ? AID.Prominence : BestAOEStarter;
        var stTarget = SingleTargetChoice(mainTarget, aoe);
        var autoAction = WantAOE ? aoeFinish : stFinish;
        var autoTarget = TargetForGCD(autoAction, stTarget);
        var breakAction = WantAOE ? aoeBreak : stBreak;
        var (aoeAction, aoeTarget) = aoeStrat switch
        {
            AOEStrategy.AutoFinish => (autoAction, autoTarget),
            AOEStrategy.ForceSTFinish => (stFinish, TargetForGCD(stFinish, stTarget)),
            AOEStrategy.ForceAOEFinish => (aoeFinish, TargetForGCD(aoeFinish, stTarget)),
            AOEStrategy.AutoBreak => (breakAction, TargetForGCD(breakAction, stTarget)),
            AOEStrategy.ForceSTBreak => (stBreak, TargetForGCD(stBreak, stTarget)),
            AOEStrategy.ForceAOEBreak => (aoeBreak, TargetForGCD(aoeBreak, stTarget)),
            _ => (AID.None, null)
        };
        if (aoeAction != AID.None && aoeTarget != null && InRangeForGCD(aoeAction, aoeTarget))
            QueueGCD(aoeAction, aoeTarget, ComboPriority(aoeAction));

        if (fullMode && !strategy.HoldAbilities())
            QueueMechanicWindDown(strategy, mainTarget);

        var fof = strategy.Option(Track.FightOrFlight);
        var fofStrat = fof.As<BuffsStrategy>();
        var holdReqOpenerForGoring = false;
        if (fullMode && !strategy.HoldAbilities())
        {
            if (!strategy.HoldCDs())
            {
                if (!strategy.HoldBuffs())
                {
                    var (fofCondition, fofPrio) = ShouldUseFightOrFlight(fofStrat, burstTarget, FightOrFlight.IsReady, Requiescat.CD < 1f);
                    if (fofCondition)
                        QueueOGCD(AID.FightOrFlight, Player, fofPrio);

                    var req = strategy.Option(Track.Requiescat);
                    var reqStrat = req.As<BuffsStrategy>();
                    var reqTarget = BestRequiescat == AID.Imperator
                        ? AOETargetChoice(mainTarget, rangedTarget, req, strategy)
                        : SingleTargetChoice(mainTarget, req);
                    var reqTogether = FightOrFlight.IsActive || FightOrFlight.CD > 55f || fofCondition;
                    var (reqCondition, reqPrio) = ShouldUseRequiescatOrImperator(reqStrat, reqTarget, Requiescat.IsReady, reqTogether);
                    if (reqCondition && reqTarget != null)
                        QueueOGCD(BestRequiescat, reqTarget, reqPrio);
                }

                var cos = strategy.Option(Track.CircleOfScorn);
                var cosStrat = cos.As<OGCDStrategy>();
                if (Unlocked(AID.CircleOfScorn) && CanUseSingleHitBeforeDowntime(cosStrat) && ShouldUseOGCD(cosStrat, Player, ActionReady(AID.CircleOfScorn), TargetsInAOECircle(5f, 1) && FightOrFlight.CD is < 57.55f and > 12))
                    QueueOGCD(AID.CircleOfScorn, Player, OGCDPrio(cosStrat, OGCDPriority.AboveAverage));

                var sw = strategy.Option(Track.SpiritsWithin);
                var swStrat = sw.As<OGCDStrategy>();
                var swTarget = BestSpirits == AID.Expiacion
                    ? AOETargetChoice(mainTarget, rangedTarget, sw, strategy)
                    : SingleTargetChoice(mainTarget, sw);
                if (swTarget != null && Unlocked(BestSpirits) && CanUseSingleHitBeforeDowntime(swStrat) && ShouldUseOGCD(swStrat, swTarget, ActionReady(BestSpirits), In3y(swTarget) && FightOrFlight.CD is < 57.55f and > 12))
                    QueueOGCD(BestSpirits, swTarget, OGCDPrio(swStrat, OGCDPriority.Average));

                var dash = strategy.Option(Track.Dash);
                var dashStrat = dash.As<DashStrategy>();
                var dashTarget = SingleTargetChoice(mainTarget, dash);
                var (dashCondition, dashPrio) = dashStrat switch
                {
                    DashStrategy.Automatic => (Player.InCombat && dashTarget != null && !IsMoving && In3y(dashTarget) && Intervene.IsReady && FightOrFlight.IsActive, OGCDPriority.SlightlyLow),
                    DashStrategy.Force => (dashTarget != null && Intervene.IsReady, OGCDPriority.Forced),
                    DashStrategy.Force1 => (dashTarget != null && Intervene.Charges >= 2, OGCDPriority.Forced),
                    DashStrategy.GapClose => (dashTarget != null && Intervene.IsReady && !In3y(dashTarget), OGCDPriority.SlightlyLow),
                    DashStrategy.GapClose1 => (dashTarget != null && Intervene.Charges >= 2 && !In3y(dashTarget), OGCDPriority.SlightlyLow),
                    _ => (false, OGCDPriority.None)
                };
                if (dashCondition && dashTarget != null)
                    QueueOGCD(AID.Intervene, dashTarget, dashPrio);
            }

            var boh = strategy.Option(Track.BladeOfHonor);
            var bohStrat = boh.As<OGCDStrategy>();
            var bohTarget = AOETargetChoice(mainTarget, rangedTarget, boh, strategy);
            var bohBasePrio = FightOrFlight.IsActive ? OGCDPriority.AboveAverage : OGCDPriority.Low;
            var bohExpiresDuringLoss = bohStrat == OGCDStrategy.Automatic && BladeOfHonor.IsReady && Mechanic.ExpiresDuringLoss(BladeOfHonor.Left);
            if (bohTarget != null && (bohExpiresDuringLoss || CanUseSingleHitBeforeDowntime(bohStrat) && ShouldUseOGCD(bohStrat, bohTarget, BladeOfHonor.IsReady)))
                QueueOGCD(AID.BladeOfHonor, bohTarget, OGCDPrio(bohStrat, bohBasePrio));

            var gb = strategy.Option(Track.GoringBlade);
            var gbStrat = gb.As<GoringBladeStrategy>();
            var gbTarget = SingleTargetChoice(mainTarget, gb);
            var gbInRange = gbTarget != null && In3y(gbTarget) && GoringBlade.IsReady;
            var gbAutomaticMinimum = gbInRange && ActionLikelyLandsBeforeDowntime();
            var gbExpiringOutsideFoFAllowed = NumSplashTargets <= 3 && GoringBlade.Left is > 0f and <= 2.5f;
            var gbAutoUseNow = gbAutomaticMinimum && (FightOrFlight.IsActive || gbExpiringOutsideFoFAllowed);
            var gbForceMinimum = gbInRange;
            var (gbCondition, gbPrio) = gbStrat switch
            {
                GoringBladeStrategy.Automatic or GoringBladeStrategy.Early => (gbAutoUseNow, GCDPriority.High),
                GoringBladeStrategy.Late => (gbAutoUseNow && !Requiescat.IsActive, GCDPriority.SlightlyHigh),
                GoringBladeStrategy.Force => (gbForceMinimum, GCDPriority.Forced),
                _ => (false, GCDPriority.None)
            };
            if (gbCondition && gbTarget != null)
                QueueGCD(AID.GoringBlade, gbTarget, gbPrio);
            holdReqOpenerForGoring = gbCondition && gbTarget != null && BladeComboStep == 0;

            if (ShouldUsePotion(strategy, burstTarget, fofStrat) && CombatTimer - LastPotionQueuedAt > 5f)
            {
                Hints.ActionsToExecute.Push(ActionDefinitions.IDPotionStr, Player, ActionQueue.Priority.High + (int)OGCDPriority.VeryCritical, delay: MathF.Max(0f, GCD - 0.9f));
                LastPotionQueuedAt = CombatTimer;
            }
        }

        if (fullMode)
        {
            var boftv = strategy.Option(Track.BladeCombo);
            var boftvStrat = boftv.As<BladeComboStrategy>();
            var boftvEnemyTarget = AOETargetChoice(mainTarget, rangedTarget, boftv, strategy);
            var boftvAutoAction = BladeComboStep == 3 ? AID.BladeOfValor
                : BladeComboStep == 2 ? AID.BladeOfTruth
                : BladeComboStep == 1 && Unlocked(AID.BladeOfFaith) ? AID.BladeOfFaith
                : Confiteor.IsReady ? AID.Confiteor
                : WantAOE && Unlocked(AID.HolyCircle) ? AID.HolyCircle
                : AID.HolySpirit;
            var bladeComboHasMP = MP >= 1000;
            var (boftvCondition, boftvAction, boftvPrio) = boftvStrat switch
            {
                BladeComboStrategy.Automatic => ((Requiescat.IsActive && !holdReqOpenerForGoring || Mechanic.ExpiresDuringLoss(Requiescat.Left) || Mechanic.ExpiresDuringLoss(Confiteor.Left)) && BladeComboStep is 0 or 1 or 2 or 3, boftvAutoAction, BladeComboPriority(boftvAutoAction)),
                BladeComboStrategy.ForceConfiteor => (Confiteor.IsReady && BladeComboStep is 0, AID.Confiteor, GCDPriority.Forced),
                BladeComboStrategy.ForceFaith => (bladeComboHasMP && BladeComboStep is 1, AID.BladeOfFaith, GCDPriority.Forced),
                BladeComboStrategy.ForceTruth => (bladeComboHasMP && BladeComboStep is 2, AID.BladeOfTruth, GCDPriority.Forced),
                BladeComboStrategy.ForceValor => (bladeComboHasMP && BladeComboStep is 3, AID.BladeOfValor, GCDPriority.Forced),
                _ => (false, AID.None, GCDPriority.None)
            };
            var boftvTarget = boftvAction == AID.HolyCircle ? Player : boftvEnemyTarget;
            var boftvInRange = boftvAction == AID.HolyCircle ? TargetsInAOECircle(5f, 1) : boftvTarget != null && In25y(boftvTarget);
            if (boftvCondition && boftvTarget != null && bladeComboHasMP && boftvInRange)
                QueueGCD(boftvAction, boftvTarget, boftvPrio);
        }

        var buffActive = DivineMight.IsActive || Atonement.IsActive || Supplication.IsActive || Sepulchre.IsActive;
        var dmacSkipHold = ComboLastMove is AID.RiotBlade && FightOrFlight.CD < 0.6f;
        var dmacHold = fullMode && fofStrat != BuffsStrategy.Delay && !strategy.HoldBuffs() && buffActive && !dmacSkipHold &&
            ((ComboLastMove is AID.RoyalAuthority ? !CanFitSkSGCD(FightOrFlight.CD, 2) : ComboLastMove is AID.FastBlade ? !CanFitSkSGCD(FightOrFlight.CD, 1) : ComboLastMove is AID.RiotBlade && !CanFitSkSGCD(FightOrFlight.CD)));
        var comboWouldRefreshSingleTargetProcs = !useAOERotation && aoeAction == BestEnder;
        var comboWouldRefreshDivineMight = comboWouldRefreshSingleTargetProcs || useAOERotation && aoeAction == AID.Prominence;

        var atone = strategy.Option(Track.Atonement);
        var atoneStrat = atone.As<AtonementStrategy>();
        var aTarget = SingleTargetChoice(mainTarget, atone);
        var aInRange = aTarget != null && In3y(aTarget);
        var aAnyReady = Atonement.IsReady || Supplication.IsReady || Sepulchre.IsReady;
        // spec rule 2: procs that would expire during a long target loss are spent before it, even while holding them for Fight or Flight
        var atonementLeft = Math.Max(Atonement.Left, Math.Max(Supplication.Left, Sepulchre.Left));
        var atonementExpiring = Atonement.Left is > 0f and <= 2.5f || Supplication.Left is > 0f and <= 2.5f || Sepulchre.Left is > 0f and <= 2.5f || Mechanic.ExpiresDuringLoss(atonementLeft);
        var aAutoMinimum = aInRange && !useAOERotation && (!dmacHold || atonementExpiring || comboWouldRefreshSingleTargetProcs) && aAnyReady;
        var aForceMinimum = aInRange;
        var bestAtonement = Sepulchre.IsReady ? AID.Sepulchre : Supplication.IsReady ? AID.Supplication : AID.Atonement;
        var (aCondition, aAction, aPriority) = atoneStrat switch
        {
            AtonementStrategy.Automatic => (aAutoMinimum, bestAtonement, AtonementPriority(bestAtonement)),
            AtonementStrategy.ForceAtonement => (aForceMinimum && Atonement.IsReady, AID.Atonement, GCDPriority.Forced),
            AtonementStrategy.ForceSupplication => (aForceMinimum && Supplication.IsReady, AID.Supplication, GCDPriority.Forced),
            AtonementStrategy.ForceSepulchre => (aForceMinimum && Sepulchre.IsReady, AID.Sepulchre, GCDPriority.Forced),
            _ => (false, AID.None, GCDPriority.None)
        };
        if (aCondition && aTarget != null)
            QueueGCD(aAction, aTarget, aPriority);

        var dmh = strategy.Option(Track.Holy);
        var dmhStrat = dmh.As<HolyStrategy>();
        var holyForceBlockedByMode = !fullMode && (dmhStrat is HolyStrategy.ForceSpirit or HolyStrategy.ForceCircle);
        var forceCircle = dmhStrat is HolyStrategy.OnlyCircle || fullMode && dmhStrat is HolyStrategy.ForceCircle;
        var wantCircle = forceCircle || (WantAOE && Unlocked(AID.HolyCircle));
        var holyEnemyTarget = SingleTargetChoice(rangedTarget, dmh);
        var holyAction = wantCircle ? AID.HolyCircle : AID.HolySpirit;
        var holyTarget = holyAction == AID.HolyCircle ? Player : holyEnemyTarget;
        var divineMightExpiring = DivineMight.Left is > 0f and <= 2.5f || Mechanic.ExpiresDuringLoss(DivineMight.Left);
        var holyAutoAllowed = !dmacHold || divineMightExpiring || comboWouldRefreshDivineMight;
        var holyBaseMinimum = holyTarget != null && DivineMight.IsActive && MP >= 1000;
        var spiritMinimum = holyBaseMinimum && holyAction == AID.HolySpirit && holyEnemyTarget != null && In25y(holyEnemyTarget) && Unlocked(AID.HolySpirit);
        var circleMinimum = holyBaseMinimum && holyAction == AID.HolyCircle && TargetsInAOECircle(5f, 1) && Unlocked(AID.HolyCircle);
        var (dmhCondition, dmhAction, dmhPrio) = dmhStrat switch
        {
            HolyStrategy.Automatic => ((spiritMinimum || circleMinimum) && holyAutoAllowed, holyAction, GCDPriority.AboveAverage - 1),
            HolyStrategy.Early => ((spiritMinimum || circleMinimum) && holyAutoAllowed, holyAction, GCDPriority.AboveAverage + 1),
            HolyStrategy.Late => ((spiritMinimum || circleMinimum) && holyAutoAllowed, holyAction, GCDPriority.AboveAverage - 1),
            HolyStrategy.OnlySpirit => (holyEnemyTarget != null && DivineMight.IsActive && MP >= 1000 && In25y(holyEnemyTarget) && Unlocked(AID.HolySpirit) && holyAutoAllowed, AID.HolySpirit, GCDPriority.AboveAverage - 1),
            HolyStrategy.OnlyCircle => (DivineMight.IsActive && MP >= 1000 && TargetsInAOECircle(5f, 1) && Unlocked(AID.HolyCircle) && holyAutoAllowed, AID.HolyCircle, GCDPriority.AboveAverage - 1),
            HolyStrategy.ForceSpirit => (holyEnemyTarget != null && DivineMight.IsActive && MP >= 1000 && In25y(holyEnemyTarget) && Unlocked(AID.HolySpirit), AID.HolySpirit, GCDPriority.Forced),
            HolyStrategy.ForceCircle => (DivineMight.IsActive && MP >= 1000 && TargetsInAOECircle(5f, 1) && Unlocked(AID.HolyCircle), AID.HolyCircle, GCDPriority.Forced),
            _ => (false, AID.None, GCDPriority.None)
        };
        if (dmhCondition && !holyForceBlockedByMode)
        {
            var finalHolyTarget = dmhAction == AID.HolyCircle ? Player : holyEnemyTarget;
            if (finalHolyTarget != null)
            {
                var lastsecbuff = (fofStrat != BuffsStrategy.Delay && FightOrFlight.Left is <= 2.5f and >= 0.01f && !Supplication.IsActive && !Sepulchre.IsActive) || DivineMight.Left is <= 2.5f and > 0f;
                QueueGCD(dmhAction, finalHolyTarget, dmhPrio == GCDPriority.Forced ? dmhPrio : FoFHolyPriority(dmhPrio, lastsecbuff));
            }
        }

        if (fullMode)
        {
            var r = strategy.Option(Track.Ranged);
            var rStrat = r.As<RangedStrategy>();
            var rTarget = SingleTargetChoice(rangedTarget, r);
            var canShieldLob = rTarget != null && In20y(rTarget) && Unlocked(AID.ShieldLob);
            var holyInstant = DivineMight.IsActive || Requiescat.IsActive;
            var canHolySpirit = rTarget != null && In25y(rTarget) && Unlocked(AID.HolySpirit) && MP >= 1000 && (!IsMoving || holyInstant);
            var bestRangedCast = canHolySpirit ? AID.HolySpirit : canShieldLob ? AID.ShieldLob : AID.None;
            var (rCondition, rAction, rPriority) = rStrat switch
            {
                RangedStrategy.Automatic => (rTarget != null && !In3y(rTarget) && bestRangedCast != AID.None, bestRangedCast, GCDPriority.Low + 1),
                RangedStrategy.OpenerRanged => (canShieldLob && IsFirstGCD && !In3y(rTarget), AID.ShieldLob, GCDPriority.Low + 1),
                RangedStrategy.OpenerRangedCast => (rTarget != null && IsFirstGCD && !In3y(rTarget) && bestRangedCast != AID.None, bestRangedCast, GCDPriority.AboveAverage - 1),
                RangedStrategy.Opener => (canShieldLob && IsFirstGCD, AID.ShieldLob, GCDPriority.Low + 1),
                RangedStrategy.OpenerCast => (IsFirstGCD && bestRangedCast != AID.None, bestRangedCast, GCDPriority.AboveAverage - 1),
                RangedStrategy.ForceCast => (bestRangedCast != AID.None, bestRangedCast, GCDPriority.Forced),
                RangedStrategy.Force => (canShieldLob, AID.ShieldLob, GCDPriority.Forced),
                RangedStrategy.Ranged => (canShieldLob && !In3y(rTarget), AID.ShieldLob, GCDPriority.Low + 1),
                RangedStrategy.RangedCast => (rTarget != null && !In3y(rTarget) && bestRangedCast != AID.None, bestRangedCast, GCDPriority.AboveAverage - 1),
                RangedStrategy.RangedCastStationary => (rTarget != null && !In3y(rTarget) && bestRangedCast != AID.None && !IsMoving, bestRangedCast, GCDPriority.AboveAverage - 1),
                _ => (false, AID.None, GCDPriority.None)
            };
            if (rCondition && rTarget != null && rAction != AID.None)
            {
                // a hard-cast Holy Spirit must carry its cast time, or the forced-move cast cap (hints.MaxCastTime) cannot stop it
                var castTime = rAction == AID.HolySpirit && !holyInstant
                    ? Math.Max(0f, ActionDefinitions.Instance.Spell(AID.HolySpirit)!.CastTime * SpSGCDLength / 2.5f - 0.5f)
                    : 0f;
                QueueGCD(rAction, rTarget, rPriority, castTime: castTime);
            }
        }

        if (CanUseTotalEclipse)
            GoalZoneCombined(strategy, 3, Hints.GoalAOECircle(5), AID.TotalEclipse, 3, maximumActionRange: 20);
    }
}
