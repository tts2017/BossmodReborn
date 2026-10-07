using BossMod.MCH;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.akechi.Custom;

//TODO: cleanup this file - it works ok now (i think?), but it's a fucking mess to look at
public sealed class AkechiMCH(RotationModuleManager manager, Actor player) : AkechiTools<AID, TraitID>(manager, player)
{
    public enum Track { Potion = SharedTrack.Potion, AOE = SharedTrack.Count, Opener, Heat, Battery, Reassemble, Hypercharge, Drill, Wildfire, BarrelStabilizer, AirAnchor, ChainSaw, GaussRound, DoubleCheck, Ricochet, Checkmate, Flamethrower, Excavator, FullMetalField, RotationMode }
    public enum RotationModeStrategy { Full, NormalOvercapOnly }
    public enum MCHPotionStrategy { Manual, OpenerAndEvenBursts, EvenBursts }
    public enum AOEStrategy { AutoFinish, AutoBreak, ForceST, ForceAOEFinish, ForceAOEBreak }
    public enum OpenerOption { AirAnchor, Drill, ChainSaw }
    public enum HeatOption { Automatic, OnlyHeatBlast, OnlyAutoCrossbow }
    public enum BatteryStrategy { Automatic, Fifty, Hundred, RaidBuffs, End, Delay }
    public enum ReassembleStrategy { Automatic, Any, HoldOne, Force, ForceWeave, Delay }
    public enum HyperchargeStrategy { Automatic, ASAP, Full, Delay }
    public enum DrillStrategy { Automatic, OnlyDrill, OnlyBioblaster, ForceDrill, ForceBioblaster, Delay }
    public enum WildfireStrategy { Automatic, AlignWithBurst, Force, ForceWeave, End, Delay }

    public static RotationModuleDefinition Definition()
    {
        var res = new RotationModuleDefinition("Akechi MCH [Custom]", "標準回しモジュール", "標準回し(Akechi)|DPS", "Akechi", RotationModuleQuality.Basic, BitMask.Build((int)Class.MCH), 100);

        res.DefineTargeting();
        res.DefineHold();
        res.Define(Track.Potion).As<MCHPotionStrategy>("Potion", "薬品使用", 280)
            .AddOption(MCHPotionStrategy.Manual, "使用しない")
            .AddOption(MCHPotionStrategy.OpenerAndEvenBursts, "開幕・偶数バースト", supportedTargets: ActionTargets.Self)
            .AddOption(MCHPotionStrategy.EvenBursts, "偶数バースト", supportedTargets: ActionTargets.Self)
            .AddAssociatedAction(ActionDefinitions.IDPotionDex);

        res.Define(Track.AOE).As<AOEStrategy>("ST/AOE", "単体 / 範囲回し", 300)
            .AddOption(AOEStrategy.AutoFinish, "周囲の敵数に応じて最適な回しを自動選択する - 可能なら現在のコンボを完走する")
            .AddOption(AOEStrategy.AutoBreak, "周囲の敵数に応じて最適な回しを自動選択する - コンボ中なら中断する")
            .AddOption(AOEStrategy.ForceST, "周囲の敵数に関係なく単体回しを強制する")
            .AddOption(AOEStrategy.ForceAOEFinish, "周囲の敵数に関係なく範囲回しを強制する - 可能なら現在のコンボを完走する")
            .AddOption(AOEStrategy.ForceAOEBreak, "周囲の敵数に関係なく範囲回しを強制する - コンボ中なら中断する")
            .AddAssociatedActions(
                AID.SplitShot, AID.SlugShot, AID.CleanShot,
                AID.HeatedSplitShot, AID.HeatedSlugShot, AID.HeatedCleanShot,
                AID.SpreadShot, AID.Scattergun);

        res.Define(Track.Opener).As<OpenerOption>("Opener", "開幕", 199)
            .AddOption(OpenerOption.AirAnchor, "開幕1本目のツールを Hot Shot / Air Anchor にする", minLevel: 4)
            .AddOption(OpenerOption.Drill, "開幕1本目のツールを Drill にする", minLevel: 58)
            .AddOption(OpenerOption.ChainSaw, "開幕1本目のツールを Chain Saw にする", minLevel: 90);

        res.Define(Track.Heat).As<HeatOption>("Heat Option", "Heat", 198)
            .AddOption(HeatOption.Automatic, "周囲の敵数に応じて Heat Blast または Auto Crossbow を自動使用する")
            .AddOption(HeatOption.OnlyHeatBlast, "対象数に関係なく Heat Blast のみを使う", 0, 0, ActionTargets.Hostile, 35)
            .AddOption(HeatOption.OnlyAutoCrossbow, "対象数に関係なく Auto Crossbow のみを使う", 0, 0, ActionTargets.Hostile, 52)
            .AddAssociatedActions(AID.HeatBlast, AID.AutoCrossbow, AID.BlazingShot);

        res.Define(Track.Battery).As<BatteryStrategy>("Battery", "バッテリー", 189)
            .AddOption(BatteryStrategy.Automatic, "最適なタイミングで Battery 系を使う")
            .AddOption(BatteryStrategy.Fifty, "Battery Gauge が50以上なら Battery 系を最速で使う", minLevel: 40)
            .AddOption(BatteryStrategy.Hundred, "Battery Gauge が100なら Battery 系を最速で使う", minLevel: 40)
            .AddOption(BatteryStrategy.RaidBuffs, "レイドバフ中に Battery 系を最速で使う", minLevel: 40)
            .AddOption(BatteryStrategy.End, "現在展開中なら Overdrive で Battery 系を早めに終了する", minLevel: 40)
            .AddOption(BatteryStrategy.Delay, "Battery 系を遅らせる", minLevel: 40)
            .AddAssociatedActions(AID.RookAutoturret, AID.RookOverdrive, AID.AutomatonQueen, AID.QueenOverdrive);

        res.Define(Track.Reassemble).As<ReassembleStrategy>("Reassemble", "Reassemble", 184)
            .AddOption(ReassembleStrategy.Automatic, "最適なタイミングで Reassemble を使う")
            .AddOption(ReassembleStrategy.Any, "任意のツールが使用可能なら Reassemble を使う - 2チャージとも使う")
            .AddOption(ReassembleStrategy.HoldOne, "任意のツールが使用可能なら Reassemble を使う - 1チャージは手動用に残す")
            .AddOption(ReassembleStrategy.Force, "挟み状況に関係なく Reassemble を強制する", 55, 5, ActionTargets.Self, 10)
            .AddOption(ReassembleStrategy.ForceWeave, "次の可能な挟み枠で Reassemble を強制する", 55, 5, ActionTargets.Self, 10)
            .AddOption(ReassembleStrategy.Delay, "Reassemble を遅らせる", minLevel: 10)
            .AddAssociatedActions(AID.Reassemble);

        res.Define(Track.Hypercharge).As<HyperchargeStrategy>("HC", "Hypercharge", 190)
            .AddOption(HyperchargeStrategy.Automatic, "最適なタイミングで Hypercharge を使う")
            .AddOption(HyperchargeStrategy.ASAP, "Heat Gauge があれば Hypercharge を最速で使う", 0, 10, ActionTargets.Self, 30)
            .AddOption(HyperchargeStrategy.Full, "Heat Gauge が最大、または最大になりそうなときに Hypercharge を使う", 0, 10, ActionTargets.Self, 30)
            .AddOption(HyperchargeStrategy.Delay, "Hypercharge を遅らせる", minLevel: 30)
            .AddAssociatedActions(AID.Hypercharge);

        res.Define(Track.Drill).As<DrillStrategy>("Drill", "ドリル", 179)
            .AddOption(DrillStrategy.Automatic, "周囲の敵数に応じて Drill または Bioblaster を自動使用する - 2チャージとも使う")
            .AddOption(DrillStrategy.OnlyDrill, "対象数に関係なく Drill のみを使う", minLevel: 58)
            .AddOption(DrillStrategy.OnlyBioblaster, "対象数に関係なく Bioblaster のみを使う", minLevel: 72)
            .AddOption(DrillStrategy.ForceDrill, "Drill を強制する", 20, 0, ActionTargets.Hostile, 58)
            .AddOption(DrillStrategy.ForceBioblaster, "Bioblaster を強制する", 20, 15, ActionTargets.Hostile, 72)
            .AddOption(DrillStrategy.Delay, "Drill / Bioblaster を遅らせる", minLevel: 58)
            .AddAssociatedActions(AID.Drill, AID.Bioblaster);

        res.Define(Track.Wildfire).As<WildfireStrategy>("WF", "Wildfire", 183)
            .AddOption(WildfireStrategy.Automatic, "最適なタイミングで Wildfire を使う")
            .AddOption(WildfireStrategy.AlignWithBurst, "最適なタイミングで使いつつ、burst 窓との整列を優先する")
            .AddOption(WildfireStrategy.Force, "挟み状況に関係なく Wildfire を強制する", 120, 10, ActionTargets.Hostile, 45)
            .AddOption(WildfireStrategy.ForceWeave, "次の可能な挟み枠で Wildfire を強制する", 120, 10, ActionTargets.Hostile, 45)
            .AddOption(WildfireStrategy.End, "Detonator で Wildfire を早めに終了する", 0, 0, ActionTargets.Hostile, 45)
            .AddOption(WildfireStrategy.Delay, "Wildfire を遅らせる", minLevel: 45)
            .AddAssociatedActions(AID.Wildfire, AID.Detonator);

        res.DefineOGCD(Track.BarrelStabilizer, AID.BarrelStabilizer, "Barrel Stabilizer", "Barrel Stabilizer", 185, 120, 30, ActionTargets.Self, 66).AddAssociatedActions(AID.BarrelStabilizer);
        res.DefineGCD(Track.AirAnchor, AID.AirAnchor, "AA", "Air Anchor", 180, 40, 0, ActionTargets.Hostile, 76).AddAssociatedActions(AID.AirAnchor);
        res.DefineGCD(Track.ChainSaw, AID.ChainSaw, "CS", "Chain Saw", 178, 60, 30, ActionTargets.Hostile, 90).AddAssociatedActions(AID.ChainSaw);
        res.DefineOGCD(Track.GaussRound, AID.GaussRound, "Gauss Round", "Gauss Round", 145, 30, 0, ActionTargets.Hostile, 15, 91).AddAssociatedActions(AID.GaussRound);
        res.DefineOGCD(Track.DoubleCheck, AID.DoubleCheck, "Double Check", "Double Check", 144, 30, 0, ActionTargets.Hostile, 92).AddAssociatedActions(AID.DoubleCheck);
        res.DefineOGCD(Track.Ricochet, AID.Ricochet, "Ricochet", "", 141, 30, 0, ActionTargets.Hostile, 50, 91).AddAssociatedActions(AID.Ricochet);
        res.DefineOGCD(Track.Checkmate, AID.Checkmate, "Checkmate", "Checkmate", 140, 30, 0, ActionTargets.Hostile, 92).AddAssociatedActions(AID.Checkmate);
        res.DefineAllow(Track.Flamethrower, AID.Flamethrower, "フレイム", "Flamethrower", -1, 60, 0, ActionTargets.Self, 70).AddAssociatedActions(AID.Flamethrower);
        res.DefineGCD(Track.Excavator, AID.Excavator, "Excav.", "Excavator", 177, 0, 0, ActionTargets.Hostile, 96).AddAssociatedActions(AID.Excavator);
        res.DefineGCD(Track.FullMetalField, AID.FullMetalField, "FMF", "Full Metal Field", 176, 0, 0, ActionTargets.Hostile, 100).AddAssociatedActions(AID.FullMetalField);

        res.Define(Track.RotationMode).As<RotationModeStrategy>("RotationMode", "ローテーションモード", 310)
            .AddOption(RotationModeStrategy.Full, "フルモード")
            .AddOption(RotationModeStrategy.NormalOvercapOnly, "通常回し+チェックメイトとダブルチェックとドリル溢れ");

        return res;
    }

    private int Heat;
    private int Battery;
    private bool OverheatActive;
    private bool MinionActive;
    private bool WantAOE;
    private bool ShouldUseRangedAOE;
    private bool ShouldUseSaw;
    private bool ShouldFlamethrower;
    private int NumConeTargets;
    private int NumSplashTargets;
    private int NumChainSawTargets;
    private int NumFlamethrowerTargets;
    private Enemy? BestConeTargets;
    private Enemy? BestSplashTargets;
    private Enemy? BestChainSawTargets;
    private Enemy? BestConeTarget;
    private Enemy? BestSplashTarget;
    private Enemy? BestChainSawTarget;
    private Enemy? BestFlamethrowerTarget;
    private bool ForceAOE;
    private DateTime BalanceOpenerLastObservedCast;
    private AID BalanceOpenerLastGCD;
    private int BalanceOpenerWeavesAfterLastGCD;
    private int BalanceOpenerBlazingShots;
    private int BalanceOpenerPostHeatDrills;
    private bool BalanceOpenerUsedExcavator;
    private bool BalanceOpenerUsedPreFMFDrill;

    public float RAleft => Status(SID.Reassembled);
    public float HCleft => Status(SID.Hypercharged);
    public float WFleft => Status(SID.WildfirePlayer);
    public float EVleft => Status(SID.ExcavatorReady);
    public float FMFleft => Status(SID.FullMetalMachinist);
    public float FTleft => Status(SID.Flamethrower);
    public float BScd => Cooldown(AID.BarrelStabilizer);
    public float Drillcd => Cooldown(AID.Drill);
    public float AAcd => Unlocked(AID.AirAnchor) ? Cooldown(AID.AirAnchor) : Cooldown(AID.HotShot);
    public float CScd => Cooldown(AID.ChainSaw);
    public bool Drillsafe => !Unlocked(AID.Drill) || !ChargeOvercapSoon(AID.Drill, 8f);
    public bool AAsafe => !Unlocked(BestAirAnchor) || AAcd >= 8;
    public bool CSsafe => !Unlocked(AID.ChainSaw) || CScd >= 8;
    public bool EVsafe => !Unlocked(AID.Excavator) || EVleft == 0;
    public bool FMFsafe => !Unlocked(AID.FullMetalField) || FMFleft == 0;
    public bool CanHC => ActionReady(AID.Hypercharge) && (Heat >= 50 || HCleft > GCD);
    public bool CanHB => Unlocked(AID.HeatBlast) && OverheatActive;
    public bool CanSummon => Unlocked(AID.RookAutoturret) && Battery >= 50 && !MinionActive;
    public bool CanWF => ActionReady(AID.Wildfire);
    public bool CanBS => ActionReady(AID.BarrelStabilizer);
    public bool CanRA => HasCharge(AID.Reassemble) && !OverheatActive && RAleft == 0;
    public bool CanDrill => Unlocked(AID.Drill) && HasUsableCharge(AID.Drill);
    public bool CanBB => Unlocked(AID.Bioblaster) && HasUsableCharge(AID.Bioblaster);
    public bool CanAA => ActionReady(BestAirAnchor);
    public bool CanCS => ActionReady(AID.ChainSaw);
    public bool CanEV => Unlocked(AID.Excavator) && EVleft > 0;
    public bool CanFMF => Unlocked(AID.FullMetalField) && FMFleft > 0;
    public bool CanFT => ActionReady(AID.Flamethrower) && !OverheatActive && FTleft == 0 && NumFlamethrowerTargets >= 2;

    private AID AutoFinish => ComboLastMove switch
    {
        AID.SlugShot or AID.HeatedSlugShot => BestCleanShot,
        AID.SplitShot or AID.HeatedSplitShot => BestSlugShot,
        AID.CleanShot or AID.HeatedCleanShot or AID.Scattergun or AID.SpreadShot or _ => AutoBreak,
    };
    private AID ST => ComboLastMove switch
    {
        AID.SlugShot or AID.HeatedSlugShot => BestCleanShot,
        AID.SplitShot or AID.HeatedSplitShot => BestSlugShot,
        AID.CleanShot or AID.HeatedCleanShot or AID.Scattergun or AID.SpreadShot or _ => BestSplitShot,
    };
    private AID AutoBreak => WantAOE ? BestSpreadShot : ST;
    private AID AOEFinish => ComboLastMove switch
    {
        AID.SlugShot or AID.HeatedSlugShot => BestCleanShot,
        AID.SplitShot or AID.HeatedSplitShot => BestSlugShot,
        AID.CleanShot or AID.HeatedCleanShot or AID.Scattergun or AID.SpreadShot or _ => BestSpreadShot,
    };

    //private bool BreakCombo => ComboLastMove == AID.HeatedSlugShot ? NumConeTargets > 3 : ComboLastMove == AID.HeatedSplitShot ? NumConeTargets > 2 : NumConeTargets > 1;

    private AID BestSplitShot => Unlocked(AID.HeatedSplitShot) ? AID.HeatedSplitShot : AID.SplitShot;
    private AID BestSlugShot => Unlocked(AID.HeatedSlugShot) ? AID.HeatedSlugShot : AID.SlugShot;
    private AID BestCleanShot => Unlocked(AID.HeatedCleanShot) ? AID.HeatedCleanShot : AID.CleanShot;
    private AID BestDrill => WantAOE && Unlocked(AID.Bioblaster) ? AID.Bioblaster : AID.Drill;
    private AID BestHeat => NumConeTargets > 3 && Unlocked(AID.AutoCrossbow) ? AID.AutoCrossbow : BestHeatBlast;
    private AID BestSpreadShot => Unlocked(AID.Scattergun) ? AID.Scattergun : AID.SpreadShot;
    private AID BestHeatBlast => Unlocked(AID.BlazingShot) ? AID.BlazingShot : Unlocked(AID.HeatBlast) ? AID.HeatBlast : ST;
    private AID BestGauss => Unlocked(AID.DoubleCheck) ? AID.DoubleCheck : AID.GaussRound;
    private AID BestRicochet => Unlocked(AID.Checkmate) ? AID.Checkmate : AID.Ricochet;
    private AID BestAirAnchor => Unlocked(AID.AirAnchor) ? AID.AirAnchor : AID.HotShot;
    private bool NextGCDConsumesReassemble => NextGCD is AID.Drill or AID.AirAnchor or AID.HotShot or AID.ChainSaw or AID.Excavator;

    private bool ShouldAddGCDBeforeEvenBurstTools
    {
        get
        {
            if (CombatTimer < 60 || !Unlocked(AID.Wildfire) || EVleft <= 0)
                return false;

            var wfIn = Cooldown(AID.Wildfire);
            if (wfIn is <= 6f or > 20f)
                return false;

            return EVleft > wfIn + SkSGCDLength + 0.5f;
        }
    }
    private float BatteryLoop120 => CombatTimer % 120f;

    private bool InEvenMinuteQueenWindow
    {
        get
        {
            var loop = BatteryLoop120;
            return CombatTimer >= 110f && (loop >= 110f || loop <= 20f);
        }
    }

    private bool BalanceOpenerActive(OpenerOption opt) => opt == OpenerOption.AirAnchor && Unlocked(AID.FullMetalField) && Player.InCombat && CombatTimer < 35f && !WantAOE && !BalanceOpenerStalled;
    private bool BalanceOpenerStalled => BalanceOpenerLastGCD != default && (World.CurrentTime - BalanceOpenerLastObservedCast).TotalSeconds > 6f; // tracked sequence stopped advancing (target swap, downtime) - hand over to the normal rotation

    private readonly Dictionary<AID, (int MaxCharges, int CurrentCharges, float CapIn)> ChargeInfoCache = new();

    private (int MaxCharges, int CurrentCharges, float CapIn) ChargeInfo(AID aid)
    {
        if (ChargeInfoCache.TryGetValue(aid, out var cached))
            return cached;

        var raw = DebugActionChargesRaw(aid);
        var max = raw.MaxCharges;
        var capIn = raw.CurrentCharges >= max ? 0 : raw.RecastActive ? Math.Max(raw.RecastTotal - raw.RecastElapsed, 0) : Cooldown(aid);
        var result = max <= 0 ? (0, 0, 0f) : (max, raw.CurrentCharges, capIn);
        ChargeInfoCache[aid] = result;
        return result;
    }

    private bool ChargeOvercapSoon(AID aid, float within = 0.75f)
    {
        var (max, charges, capIn) = ChargeInfo(aid);
        return max > 0 && (charges >= max || (charges == max - 1 && capIn <= within));
    }

    private bool HasUsableCharge(AID aid)
    {
        var (max, charges, _) = ChargeInfo(aid);
        return max <= 1 ? ActionReady(aid) : charges > 0;
    }

    private int OddMinuteSecondQueenTarget()
    {
        if (CombatTimer < 150f)
            return 0;

        var loop = BatteryLoop120;
        if (loop < 55f || loop > 90f)
            return 0;

        var evenCycle = Math.Max(1, (int)(CombatTimer / 120f));
        return ((evenCycle - 1) % 3) switch
        {
            0 => 60,
            1 => 70,
            _ => 80
        };
    }

    private int BatteryGainFromAction(AID aid) => aid switch
    {
        AID.HotShot or AID.AirAnchor or AID.ChainSaw or AID.Excavator => 20,
        AID.CleanShot or AID.HeatedCleanShot => 10,
        _ => 0
    };

    private bool BatteryOvercapSoon()
    {
        var nextGain = BatteryGainFromAction(NextGCD);
        return Battery >= 100 || Battery + nextGain > 100;
    }

    private bool ShouldDelayExcavatorForOddMinuteQueen()
    {
        if (!Unlocked(AID.Excavator) || EVleft <= 0)
            return false;

        var target = OddMinuteSecondQueenTarget();
        if (target <= 0 || Battery >= target)
            return false;

        var nextCleanShot =
            NextGCD is AID.CleanShot or AID.HeatedCleanShot ||
            ComboLastMove is AID.SlugShot or AID.HeatedSlugShot;

        return nextCleanShot && Battery + 10 >= target;
    }

    private bool CanUseExcavatorNowForReassemble(Actor? target)
        => InCombat(target)
        && !OverheatActive
        && CanEV
        && !ShouldAddGCDBeforeEvenBurstTools
        && !ShouldDelayExcavatorForOddMinuteQueen();

    private void ClearBalanceOpenerState()
    {
        BalanceOpenerLastObservedCast = default;
        BalanceOpenerLastGCD = default;
        BalanceOpenerWeavesAfterLastGCD = 0;
        BalanceOpenerBlazingShots = 0;
        BalanceOpenerPostHeatDrills = 0;
        BalanceOpenerUsedExcavator = false;
        BalanceOpenerUsedPreFMFDrill = false;
    }

    private void UpdateBalanceOpenerState(bool active)
    {
        if (!active)
        {
            if (!Player.InCombat || CombatTimer > 35f)
                ClearBalanceOpenerState();
            return;
        }

        var cast = Manager.LastCast.Data;
        if (cast == null || cast.SourceSequence == 0 || Manager.LastCast.Time == BalanceOpenerLastObservedCast)
            return;

        BalanceOpenerLastObservedCast = Manager.LastCast.Time;
        if (cast.IsSpell(BestAirAnchor) || cast.IsSpell(AID.Drill) || cast.IsSpell(AID.ChainSaw) || cast.IsSpell(AID.Excavator) || cast.IsSpell(AID.FullMetalField) || cast.IsSpell(BestHeatBlast))
        {
            BalanceOpenerLastGCD = cast.IsSpell(BestAirAnchor) ? BestAirAnchor
                : cast.IsSpell(AID.Drill) ? AID.Drill
                : cast.IsSpell(AID.ChainSaw) ? AID.ChainSaw
                : cast.IsSpell(AID.Excavator) ? AID.Excavator
                : cast.IsSpell(AID.FullMetalField) ? AID.FullMetalField
                : BestHeatBlast;
            BalanceOpenerWeavesAfterLastGCD = 0;

            if (BalanceOpenerLastGCD == BestHeatBlast)
                ++BalanceOpenerBlazingShots;
            if (BalanceOpenerLastGCD == AID.Excavator)
                BalanceOpenerUsedExcavator = true;
            if (BalanceOpenerLastGCD == AID.Drill && BalanceOpenerUsedExcavator && BalanceOpenerBlazingShots == 0)
                BalanceOpenerUsedPreFMFDrill = true;
            else if (BalanceOpenerLastGCD == AID.Drill && BalanceOpenerBlazingShots >= 5)
                ++BalanceOpenerPostHeatDrills;
        }
        else if (BalanceOpenerLastGCD != default &&
            (cast.IsSpell(AID.BarrelStabilizer) || cast.IsSpell(AID.Reassemble) || cast.IsSpell(BestGauss) || cast.IsSpell(BestRicochet) ||
            cast.IsSpell(AID.Hypercharge) || cast.IsSpell(AID.Wildfire) || cast.IsSpell(AID.AutomatonQueen) || cast.IsSpell(AID.RookAutoturret)))
        {
            ++BalanceOpenerWeavesAfterLastGCD;
        }
    }

    #region Buffs

    private bool ReadyForWildfireBeforeFullMetalField(Actor? target)
        => CanWF
        && CanWeaveIn
        && InCombat(target)
        && (Unlocked(AID.FullMetalField)
            ? FMFleft > 0 && EVleft == 0 && AAsafe && CSsafe && Drillsafe
            : (CombatTimer >= 60 && EVleft == 0 && AAsafe && CSsafe && Drillsafe) || LastActionUsed(AID.Hypercharge) || OverheatActive); // below L100 there is no FMF to wait for

    private (bool, OGCDPriority) ShouldUseWildfire(WildfireStrategy strategy, Actor? target)
    {
        var beforeFullMetalField = ReadyForWildfireBeforeFullMetalField(target);
        return strategy switch
        {
            WildfireStrategy.Automatic => (beforeFullMetalField, ChangePriority(-200, 999)),
            WildfireStrategy.AlignWithBurst => (BScd > 90 && beforeFullMetalField, ChangePriority(-200, 999)),
            WildfireStrategy.End => (HasStatus(SID.WildfirePlayer), OGCDPriority.Max),
            WildfireStrategy.Force => (CanWF, OGCDPriority.Forced),
            WildfireStrategy.ForceWeave => (CanWF && CanWeaveIn, OGCDPriority.Forced),
            WildfireStrategy.Delay or _ => (false, OGCDPriority.None),
        };
    }
    private (bool, OGCDPriority) ShouldUseBarrelStabilizer(OGCDStrategy strategy, Actor? target)
    {
        if (!CanBS)
            return (false, OGCDPriority.None);
        return strategy switch
        {
            OGCDStrategy.Automatic => (Player.InCombat && CanWeaveIn, OGCDPriority.Max - 1),
            OGCDStrategy.AnyWeave => (CanWeaveIn, OGCDPriority.Forced),
            OGCDStrategy.EarlyWeave => (CanEarlyWeaveIn, OGCDPriority.Forced),
            OGCDStrategy.LateWeave => (CanLateWeaveIn, OGCDPriority.Forced),
            OGCDStrategy.Force => (true, OGCDPriority.Forced),
            OGCDStrategy.Delay or _ => (false, OGCDPriority.None),
        };
    }
    private (bool, OGCDPriority) ShouldUseReassemble(ReassembleStrategy strategy, Actor? target)
    {
        if (!CanRA)
            return (false, OGCDPriority.None);
        var reassembleCharges = Charges(AID.Reassemble);
        var canReassembleNextTool =
            NextGCDConsumesReassemble ||
            AAcd <= GCD ||
            CScd <= GCD ||
            CanUseExcavatorNowForReassemble(target) ||
            NextGCD == AID.Drill;
        var opti = Player.InCombat && CanWeaveIn && canReassembleNextTool;
        var any = Player.InCombat && CanWeaveIn && canReassembleNextTool;
        var risk = any && (reassembleCharges >= 2 || ReadyIn(AID.Reassemble) < 15f);
        return strategy switch
        {
            ReassembleStrategy.Automatic => (opti || risk, OGCDPriority.High),
            ReassembleStrategy.Any => (any || risk, OGCDPriority.High),
            ReassembleStrategy.HoldOne => (reassembleCharges >= 2 && any, OGCDPriority.High),
            ReassembleStrategy.Force or ReassembleStrategy.ForceWeave => (true, OGCDPriority.Forced),
            ReassembleStrategy.Delay or _ => (false, OGCDPriority.None),
        };
    }
    #endregion

    #region Tools
    private void Opener(OpenerOption opt, Actor? target)
    {
        if (CountdownRemaining == null || CombatTimer == 0 || !Player.InCombat)
        {
            var openerAction = opt switch
            {
                OpenerOption.ChainSaw => AID.ChainSaw,
                OpenerOption.Drill => AID.Drill,
                _ => BestAirAnchor,
            };
            QueueGCD(openerAction, target, GCDPriority.Max);
            return;
        }
        if (CountdownRemaining > 0)
        {
            if (CountdownRemaining < 1.15f)
            {
                if (opt == OpenerOption.AirAnchor || (opt == OpenerOption.ChainSaw && !Unlocked(AID.ChainSaw)) || (opt == OpenerOption.Drill && !Unlocked(AID.Drill)))
                    QueueGCD(BestAirAnchor, target, GCDPriority.VerySevere);
                if (opt == OpenerOption.Drill)
                    QueueGCD(AID.Drill, target, GCDPriority.VerySevere);
            }

            if (CountdownRemaining < 1.05f && opt == OpenerOption.ChainSaw)
                QueueGCD(AID.ChainSaw, target, GCDPriority.VerySevere);

            return;
        }
    }
    private (bool, GCDPriority) ShouldUseAirAnchor(GCDStrategy strategy, Actor? target) => strategy switch
    {
        GCDStrategy.Automatic => (InCombat(target) && CanAA, GCDPriority.ExtremelyHigh + 10),
        GCDStrategy.Force => (CanAA, GCDPriority.Forced),
        GCDStrategy.Delay or _ => (false, GCDPriority.None),
    };
    private (bool, GCDPriority) ShouldUseDrill(DrillStrategy strategy, Actor? target)
    {
        var st = InCombat(target) && CanDrill && In25y(target) && !OverheatActive;
        var aoe = InCombat(target) && CanBB && In12y(target) && !OverheatActive;
        var prio = Cooldown(AID.Drill) <= GCD ? GCDPriority.ExtremelyHigh + 9 : CanFitSkSGCD(WFleft) && FMFleft == 0 ? GCDPriority.High + 2 : GCDPriority.High;
        return strategy switch
        {
            DrillStrategy.Automatic => (WantAOE ? aoe : st, prio),
            DrillStrategy.OnlyDrill => (st, prio),
            DrillStrategy.OnlyBioblaster => (aoe, prio),
            DrillStrategy.ForceDrill => (CanDrill && In25y(target), GCDPriority.Forced),
            DrillStrategy.ForceBioblaster => (CanBB && In12y(target), GCDPriority.Forced),
            DrillStrategy.Delay or _ => (false, GCDPriority.None),
        };
    }
    private (bool, GCDPriority) ShouldUseChainSaw(GCDStrategy strategy, Actor? target) => strategy switch
    {
        GCDStrategy.Automatic => (InCombat(target) && CanCS, GCDPriority.ExtremelyHigh + 2),
        GCDStrategy.Force => (CanCS, GCDPriority.Forced),
        GCDStrategy.Delay or _ => (false, GCDPriority.None),
    };
    private (bool, GCDPriority) ShouldUseExcavator(GCDStrategy strategy, Actor? target) => strategy switch
    {
        GCDStrategy.Automatic => (InCombat(target) && !OverheatActive && CanEV && !ShouldAddGCDBeforeEvenBurstTools && !ShouldDelayExcavatorForOddMinuteQueen(), GCDPriority.ExtremelyHigh + 5),
        GCDStrategy.Force => (CanEV, GCDPriority.Forced),
        GCDStrategy.Delay or _ => (false, GCDPriority.None),
    };
    private bool FullMetalFieldAllowedByWildfire(WildfireStrategy wildfireStrategy)
    {
        if (wildfireStrategy == WildfireStrategy.Delay || !Unlocked(AID.Wildfire))
            return true;

        if (WFleft > 0 || LastActionUsed(AID.Wildfire))
            return true;

        if (FMFleft > 0 && FMFleft <= GCD + 0.5f)
            return true;

        return false;
    }

    private (bool, GCDPriority) ShouldUseFullMetalField(GCDStrategy strategy, WildfireStrategy wildfireStrategy, Actor? target) => strategy switch
    {
        GCDStrategy.Automatic => (InCombat(target) && CanFMF && !OverheatActive && RAleft == 0 && EVleft == 0 && AAsafe && CSsafe && Drillsafe && FullMetalFieldAllowedByWildfire(wildfireStrategy), GCDPriority.High + 1),
        GCDStrategy.Force => (CanFMF, GCDPriority.Forced),
        GCDStrategy.Delay or _ => (false, GCDPriority.None),
    };
    #endregion

    #region Gauge
    private bool ChargesSafeForHypercharge()
    {
        var (_, gaussCharges, _) = ChargeInfo(BestGauss);
        var (_, ricoCharges, _) = ChargeInfo(BestRicochet);

        if (Unlocked(AID.DoubleCheck) && gaussCharges >= 2)
            return false;

        if (Unlocked(AID.Checkmate) && ricoCharges >= 2)
            return false;

        return true;
    }

    private (bool, OGCDPriority) ShouldUseHypercharge(HyperchargeStrategy strategy, Actor? target)
    {
        if (!CanHC || RAleft > 0 || OverheatActive || target == null)
            return (false, OGCDPriority.None);
        var chargesSafe = ChargesSafeForHypercharge();
        var ok = AAsafe && CSsafe && Drillsafe && FMFsafe && EVsafe && (chargesSafe || Heat >= 100);
        var odd = LastActionUsed(AID.Excavator) || (CScd > 50 && EVleft == 0);
        var even = LastActionUsed(AID.FullMetalField) || (BScd > 90 && FMFleft == 0) || (WFleft > 0 && FMFleft == 0);
        var off = !Unlocked(AID.Wildfire) || (Unlocked(AID.Wildfire) && (Cooldown(AID.Wildfire) > 40 || (Cooldown(AID.Wildfire) <= 2f && FMFleft == 0) || WFleft > 0));
        var risk = Heat == 100 && ((Unlocked(BestAirAnchor) && AAcd > GCD) || (Unlocked(AID.ChainSaw) && CScd > GCD) || (Unlocked(AID.Drill) && Drillcd > GCD) || (Unlocked(AID.Excavator) && EVleft == 0) || (Unlocked(AID.FullMetalField) && FMFleft == 0));
        var ct = (CombatTimer <= 30 || ComboTimer == 0 || (ComboLastMove is AID.HeatedCleanShot or AID.Scattergun)) ? ComboTimer >= 0 : ComboTimer >= 7.6f;
        var evenDoubleHypercharge = even && (Heat >= 50 || HCleft > GCD) && (WFleft > 0 || BScd > 90 || Cooldown(AID.Wildfire) > 90);
        return strategy switch
        {
            HyperchargeStrategy.Automatic => (((ok || (WFleft > 0 && target?.PendingHPRatio <= 0.05f)) && ct && (odd || evenDoubleHypercharge || off || risk)), OGCDPriority.Severe + 1),
            HyperchargeStrategy.ASAP => (true, OGCDPriority.Forced),
            HyperchargeStrategy.Full => (Heat == 100, OGCDPriority.Forced),
            HyperchargeStrategy.Delay or _ => (false, OGCDPriority.None),
        };
    }
    private bool ShouldUseHeat(HeatOption strategy, Actor? target)
    {
        if (!CanHB)
            return false;
        return strategy switch
        {
            HeatOption.Automatic => InCombat(target) && (WantAOE ? In12y(target) : In25y(target)),
            HeatOption.OnlyHeatBlast => In25y(target),
            HeatOption.OnlyAutoCrossbow => In12y(target),
            _ => false,
        };
    }
    private bool ShouldUseBattery(BatteryStrategy strategy)
    {
        if (strategy == BatteryStrategy.Delay)
            return false;

        if (strategy == BatteryStrategy.End)
            return MinionActive && CanWeaveIn;

        if (!CanSummon || !CanWeaveIn)
            return false;

        var afterEV = LastActionUsed(AID.Excavator) || (CScd > 50 && EVleft == 0);
        var afterAA = LastActionUsed(BestAirAnchor) || AAcd > 36;
        var openerQueen = CombatTimer < 35f && Battery >= 60 && afterEV;
        var oneMinuteQueen = CombatTimer is >= 55f and < 95f && Battery >= 90 && afterAA;
        var evenMinuteQueen = InEvenMinuteQueenWindow && Battery >= 100 && afterAA;
        var loop = BatteryLoop120;
        var firstOddSplitQueen = CombatTimer >= 150f && loop is >= 30f and <= 55f && Battery >= 50;
        var oddTarget = OddMinuteSecondQueenTarget();
        var secondOddSplitQueen = oddTarget > 0 && Battery >= oddTarget && (afterEV || afterAA || ComboLastMove is AID.CleanShot or AID.HeatedCleanShot);
        var overcapRisk = BatteryOvercapSoon();

        return strategy switch
        {
            BatteryStrategy.Automatic => openerQueen || oneMinuteQueen || evenMinuteQueen || firstOddSplitQueen || secondOddSplitQueen || overcapRisk,
            BatteryStrategy.Fifty => Battery >= 50,
            BatteryStrategy.Hundred => Battery >= 100,
            BatteryStrategy.RaidBuffs => RaidBuffsLeft > GCD || RaidBuffsIn < 5f || evenMinuteQueen || overcapRisk,
            _ => false
        };
    }
    #endregion

    #region Other
    private (bool, OGCDPriority) ShouldUseOGCD(OGCDStrategy strategy, Actor? target, bool unlocked, float cooldown)
    {
        if (!unlocked)
            return (false, OGCDPriority.None);

        var condition =
            CanWeaveIn &&
            In25y(target) &&
            cooldown <= 30.6f &&
            (WFleft > 0 || //Wildfire active
            RaidBuffsLeft > 0 || //raid buffs active
            OverheatActive || //Overheat active
            target?.PendingHPRatio < 0.05f || //target low HP
            ((!Unlocked(AID.Wildfire) || Cooldown(AID.Wildfire) > 1f) || cooldown <= 0.6f) || //hold for Wildfire window if we have less than max charges
            Cooldown(AID.Wildfire) > 90); //spend all in 2m window, else hold 1

        var prio = CMDCPriority(cooldown, target);

        return strategy switch
        {
            OGCDStrategy.Automatic => (condition, prio),
            OGCDStrategy.AnyWeave => (CanWeaveIn, prio + 900),
            OGCDStrategy.EarlyWeave => (CanEarlyWeaveIn, prio + 900),
            OGCDStrategy.LateWeave => (CanLateWeaveIn, prio + 900),
            OGCDStrategy.Force => (true, prio + 900),
            _ => (false, OGCDPriority.None),
        };
    }
    private (bool, OGCDPriority) ShouldUseDoubleCheck(OGCDStrategy strategy, Actor? target)
    {
        return ShouldUseChargeOGCD(strategy, target, BestGauss);
    }
    private (bool, OGCDPriority) ShouldUseCheckmate(OGCDStrategy strategy, Actor? target)
    {
        return ShouldUseChargeOGCD(strategy, target, BestRicochet);
    }
    private (bool, OGCDPriority) ShouldUseChargeOGCD(OGCDStrategy strategy, Actor? target, AID aid)
    {
        var (maxCharges, charges, capIn) = ChargeInfo(aid);
        if (charges <= 0 || maxCharges <= 0)
            return (false, OGCDPriority.None);

        var overcapSoon = ChargeOvercapSoon(aid);
        var holdForUpcomingBurst = (Cooldown(AID.Wildfire) is > 0 and <= 15f || RaidBuffsIn < 15f) && WFleft == 0 && RaidBuffsLeft == 0;
        var reserveCharges = Cooldown(AID.Wildfire) <= 6f || RaidBuffsIn < 6f ? 2 : 1;
        var shouldHold = holdForUpcomingBurst && charges <= Math.Min(reserveCharges, maxCharges - 1) && !overcapSoon;
        var burstActive = WFleft > 0 || RaidBuffsLeft > 0 || OverheatActive || Cooldown(AID.Wildfire) > 90;
        var makeRoomForHypercharge = charges >= 2 && CanHC && RAleft == 0 && !OverheatActive; // ChargesSafeForHypercharge blocks HC at 2+ charges - spend down so HC is not starved outside bursts
        var condition = CanWeaveIn && In25y(target) && !shouldHold && (overcapSoon || burstActive || makeRoomForHypercharge || target?.PendingHPRatio <= 0.05f);
        var prio = CMDCPriority(overcapSoon ? 0 : capIn, target);

        return strategy switch
        {
            OGCDStrategy.Automatic => (condition, prio),
            OGCDStrategy.AnyWeave => (CanWeaveIn, prio + 900),
            OGCDStrategy.EarlyWeave => (CanEarlyWeaveIn, prio + 900),
            OGCDStrategy.LateWeave => (CanLateWeaveIn, prio + 900),
            OGCDStrategy.Force => (true, prio + 900),
            _ => (false, OGCDPriority.None),
        };
    }
    private bool ShouldUseDoubleCheckOvercapOnly(Actor? target)
        => CanWeaveIn && In25y(target) && ChargeOvercapSoon(BestGauss);

    private bool ShouldUseCheckmateOvercapOnly(Actor? target)
        => CanWeaveIn && In25y(target) && ChargeOvercapSoon(BestRicochet);

    private bool ShouldUseDrillOvercapOnly(Actor? target)
        => InCombat(target) && !OverheatActive && ChargeOvercapSoon(AID.Drill) && (WantAOE ? CanBB && In12y(target) : CanDrill && In25y(target));

    private OGCDPriority CMDCPriority(float cooldown, Actor? target)
    {
        //max prio
        if (RaidBuffsLeft > 0 || //raid buffs active
            (WFleft > 0 && OverheatActive) || //2m active
            target?.PendingHPRatio < 0.05f || //send all before death
            cooldown <= 0.6f) //overcap
            return OGCDPriority.High;

        //high prio
        if (Cooldown(AID.Wildfire) > 90 ? cooldown <= 30.6f : cooldown <= 60.6f)
            return OGCDPriority.Average;

        return OGCDPriority.Low;
    }

    private bool ShouldUseFlamethrower(AllowOrForbid strategy, Actor? target)
    {
        if (!CanFT)
            return false;
        return strategy switch
        {
            AllowOrForbid.Allow => InCombat(target) && ShouldFlamethrower && In12y(target),
            AllowOrForbid.Force => true,
            AllowOrForbid.Forbid or _ => false,
        };
    }
    private bool StopForFlamethrower => Service.Config.Get<MCHConfig>().PauseForFlamethrower && FTleft > 0;
    private float PotionReadyIn()
    {
        var potion = ActionDefinitions.Instance[ActionDefinitions.IDPotionDex];
        return potion?.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions) ?? float.MaxValue;
    }

    private bool IsEvenBurstPotionTime(float time)
    {
        if (time < 110f)
            return false;

        var loop = time % 120f;
        return loop >= 100f || loop <= 25f; // 45s wide: a 40s Air Anchor recast always lands in it
    }

    private bool EvenBurstPotionWindow()
    {
        if (!Player.InCombat || !CanWeaveIn)
            return false;

        if (PotionReadyIn() > GCD + 0.1f)
            return false;

        if (AAcd > GCD + 0.8f)
            return false;

        var airAnchorAt = CombatTimer + Math.Max(AAcd, 0f);
        if (!IsEvenBurstPotionTime(airAnchorAt))
            return false;

        return true;
    }

    private bool WantsOpenerPotion(MCHPotionStrategy strategy)
        => strategy == MCHPotionStrategy.OpenerAndEvenBursts;

    private bool WantsEvenBurstPotion(MCHPotionStrategy strategy)
        => strategy is MCHPotionStrategy.OpenerAndEvenBursts or MCHPotionStrategy.EvenBursts;

    private bool OpenerPotionWindow()
    {
        if (PotionReadyIn() > GCD + 0.1f)
            return false;

        if (CountdownRemaining is > 0f and <= 2.2f)
            return true;

        return Player.InCombat && CombatTimer <= 5f && CanWeaveIn;
    }

    private bool ShouldUsePotion(MCHPotionStrategy strategy)
    {
        if (WantsOpenerPotion(strategy) && OpenerPotionWindow())
            return true;

        if (WantsEvenBurstPotion(strategy) && EvenBurstPotionWindow())
            return true;

        return false;
    }

    private bool QueuePotionIfNeeded(MCHPotionStrategy strategy)
    {
        if (!ShouldUsePotion(strategy) || World.Client.GetInventoryItemQuantity(ActionDefinitions.IDPotionDex.ID) == 0)
            return false;

        Hints.ActionsToExecute.Push(ActionDefinitions.IDPotionDex, Player, ActionQueue.Priority.High);
        return true;
    }
    #endregion

    public override void Execution(StrategyValues strategy, Enemy? primaryTarget)
    {
        #region Variables
        ChargeInfoCache.Clear();
        var gauge = World.Client.GetGauge<MachinistGauge>();
        Heat = gauge.Heat;
        Battery = gauge.Battery;
        OverheatActive = Player.FindStatus(SID.Overheated) != null;
        MinionActive = gauge.SummonTimeRemaining != 0;
        (BestConeTargets, NumConeTargets) = GetBestTarget(primaryTarget, 12, Is12yConeTarget);
        (BestSplashTargets, NumSplashTargets) = !strategy.ManualTarget() ? GetBestTarget(primaryTarget, 25, IsSplashTarget) : (primaryTarget, 0);
        (BestChainSawTargets, NumChainSawTargets) = !strategy.ManualTarget() ? GetBestTarget(primaryTarget, 25, Is25yRectTarget) : (primaryTarget, 0);
        NumFlamethrowerTargets = Hints.NumPriorityTargetsInAOECone(Player.Position, 12, Player.Rotation.ToDirection(), 45.Degrees());
        var mainTarget = primaryTarget?.Actor;
        var aoe = strategy.Option(Track.AOE);
        var aoeStrat = aoe.As<AOEStrategy>();
        ForceAOE = aoeStrat is AOEStrategy.ForceAOEFinish or AOEStrategy.ForceAOEBreak;
        WantAOE = Unlocked(AID.SpreadShot) && (strategy.AutoTarget() ? (NumConeTargets > 1 || ForceAOE) : strategy.ManualTarget() ? (NumFlamethrowerTargets > 1 || ForceAOE) : mainTarget != null);
        ShouldUseRangedAOE = Unlocked(AID.Ricochet) && NumSplashTargets > 1;
        ShouldUseSaw = Unlocked(AID.ChainSaw) && NumChainSawTargets > 1;
        ShouldFlamethrower = Unlocked(AID.Flamethrower) && NumFlamethrowerTargets >= 2 && !CanHC && AAsafe && CSsafe && Drillsafe && EVsafe && FMFsafe && (!Unlocked(AID.BarrelStabilizer) || BScd > 11f) && (!Unlocked(AID.Wildfire) || Cooldown(AID.Wildfire) > 11f);
        BestConeTarget = WantAOE ? BestConeTargets : primaryTarget;
        BestSplashTarget = ShouldUseRangedAOE ? BestSplashTargets : primaryTarget;
        BestChainSawTarget = ShouldUseSaw ? BestChainSawTargets : primaryTarget;
        BestFlamethrowerTarget = ShouldFlamethrower ? BestConeTarget : primaryTarget;

        #region Strategy Definitions
        var opener = strategy.Option(Track.Opener);
        var openerOpt = opener.As<OpenerOption>();
        var assemble = strategy.Option(Track.Reassemble);
        var assembleStrat = assemble.As<ReassembleStrategy>();
        var gauss = strategy.Option(Track.GaussRound);
        var gaussStrat = gauss.As<OGCDStrategy>();
        var dc = strategy.Option(Track.DoubleCheck);
        var dcStrat = dc.As<OGCDStrategy>();
        var ricochet = strategy.Option(Track.Ricochet);
        var ricochetStrat = ricochet.As<OGCDStrategy>();
        var cm = strategy.Option(Track.Checkmate);
        var cmStrat = cm.As<OGCDStrategy>();
        var ft = strategy.Option(Track.Flamethrower);
        var ftStrat = ft.As<AllowOrForbid>();
        var ev = strategy.Option(Track.Excavator);
        var evStrat = ev.As<GCDStrategy>();
        var fmf = strategy.Option(Track.FullMetalField);
        var fmfStrat = fmf.As<GCDStrategy>();
        var cs = strategy.Option(Track.ChainSaw);
        var csStrat = cs.As<GCDStrategy>();
        var aa = strategy.Option(Track.AirAnchor);
        var aaStrat = aa.As<GCDStrategy>();
        var drill = strategy.Option(Track.Drill);
        var drillStrat = drill.As<DrillStrategy>();
        var hsp = strategy.Option(Track.Heat);
        var hspOpt = hsp.As<HeatOption>();
        var hc = strategy.Option(Track.Hypercharge);
        var hcStrat = hc.As<HyperchargeStrategy>();
        var battery = strategy.Option(Track.Battery);
        var batteryStrat = battery.As<BatteryStrategy>();
        var wf = strategy.Option(Track.Wildfire);
        var wfStrat = wf.As<WildfireStrategy>();
        var bs = strategy.Option(Track.BarrelStabilizer);
        var bsStrat = bs.As<OGCDStrategy>();
        var potStrat = strategy.Option(Track.Potion).As<MCHPotionStrategy>();
        var rotationMode = strategy.Option(Track.RotationMode).As<RotationModeStrategy>();
        var normalOvercapOnly = rotationMode == RotationModeStrategy.NormalOvercapOnly;
        var balanceOpener = BalanceOpenerActive(openerOpt);
        UpdateBalanceOpenerState(balanceOpener);

        bool QueueBalanceOpenerDoubleCheck()
        {
            if ((Unlocked(AID.DoubleCheck) ? dcStrat : gaussStrat) != OGCDStrategy.Delay && HasCharge(BestGauss))
            {
                QueueOGCD(BestGauss, AOETargetChoice(mainTarget, Unlocked(AID.DoubleCheck) ? BestSplashTarget?.Actor : mainTarget, Unlocked(AID.DoubleCheck) ? dc : gauss, strategy), OGCDPriority.Forced);
                return true;
            }

            return false;
        }

        bool QueueBalanceOpenerCheckmate()
        {
            if ((Unlocked(AID.Checkmate) ? cmStrat : ricochetStrat) != OGCDStrategy.Delay && HasCharge(BestRicochet))
            {
                QueueOGCD(BestRicochet, AOETargetChoice(mainTarget, BestSplashTarget?.Actor, Unlocked(AID.Checkmate) ? cm : ricochet, strategy), OGCDPriority.Forced);
                return true;
            }

            return false;
        }

        void QueueBalanceOpenerOGCDs(bool allowBuffs, bool allowGauge)
        {
            if (!balanceOpener || !CanWeaveIn)
                return;

            if (BalanceOpenerLastGCD == BestAirAnchor)
            {
                if (BalanceOpenerWeavesAfterLastGCD == 0)
                    QueueBalanceOpenerCheckmate();
                else if (BalanceOpenerWeavesAfterLastGCD == 1)
                    QueueBalanceOpenerDoubleCheck();
            }
            else if (BalanceOpenerLastGCD == AID.Drill && BalanceOpenerBlazingShots == 0 && BalanceOpenerPostHeatDrills == 0)
            {
                if (!BalanceOpenerUsedPreFMFDrill && BalanceOpenerWeavesAfterLastGCD == 0 && allowBuffs && bsStrat != OGCDStrategy.Delay && CanBS)
                    QueueOGCD(AID.BarrelStabilizer, Player, OGCDPriority.Forced);
                else if (BalanceOpenerUsedPreFMFDrill)
                {
                    if (BalanceOpenerWeavesAfterLastGCD == 0)
                    {
                        if (QueueBalanceOpenerCheckmate())
                            return;

                        if (allowBuffs && wfStrat is not WildfireStrategy.Delay and not WildfireStrategy.End && CanWF)
                            QueueOGCD(AID.Wildfire, SingleTargetChoice(mainTarget, wf), OGCDPriority.Forced);
                    }
                    else if (BalanceOpenerWeavesAfterLastGCD == 1 && allowBuffs && wfStrat is not WildfireStrategy.Delay and not WildfireStrategy.End && CanWF)
                    {
                        QueueOGCD(AID.Wildfire, SingleTargetChoice(mainTarget, wf), OGCDPriority.Forced);
                    }
                }
            }
            else if (BalanceOpenerLastGCD == AID.ChainSaw)
            {
                if (BalanceOpenerWeavesAfterLastGCD == 0 && allowBuffs && assembleStrat != ReassembleStrategy.Delay && CanRA)
                    QueueOGCD(AID.Reassemble, Player, OGCDPriority.Forced);
            }
            else if (BalanceOpenerLastGCD == AID.Excavator)
            {
                var allowOpenerQueen = batteryStrat switch
                {
                    BatteryStrategy.Automatic or BatteryStrategy.RaidBuffs => Battery >= 60,
                    BatteryStrategy.Fifty => Battery >= 60,
                    BatteryStrategy.Hundred => Battery >= 100,
                    _ => false
                };

                if (BalanceOpenerWeavesAfterLastGCD == 0 && allowGauge && allowOpenerQueen && CanSummon)
                    QueueOGCD(Unlocked(AID.AutomatonQueen) ? AID.AutomatonQueen : AID.RookAutoturret, Player, OGCDPriority.Forced);
            }
            else if (BalanceOpenerLastGCD == AID.FullMetalField)
            {
                if (BalanceOpenerWeavesAfterLastGCD == 0)
                {
                    if (QueueBalanceOpenerDoubleCheck())
                        return;

                    if (allowGauge && hcStrat != HyperchargeStrategy.Delay && CanHC && RAleft == 0 && !OverheatActive)
                        QueueOGCD(AID.Hypercharge, Player, OGCDPriority.Forced);
                }
                else if (BalanceOpenerWeavesAfterLastGCD == 1 && allowGauge && hcStrat != HyperchargeStrategy.Delay && CanHC && RAleft == 0 && !OverheatActive)
                {
                    QueueOGCD(AID.Hypercharge, Player, OGCDPriority.Forced);
                }
            }
            else if (BalanceOpenerLastGCD == BestHeatBlast)
            {
                if (BalanceOpenerWeavesAfterLastGCD == 0)
                {
                    if (BalanceOpenerBlazingShots is 1 or 3 or 5)
                        QueueBalanceOpenerCheckmate();
                    else if (BalanceOpenerBlazingShots is 2 or 4)
                        QueueBalanceOpenerDoubleCheck();
                }
            }
            else if (BalanceOpenerLastGCD == AID.Drill && BalanceOpenerBlazingShots >= 5)
            {
                if (BalanceOpenerWeavesAfterLastGCD == 0)
                {
                    if (BalanceOpenerPostHeatDrills == 1)
                        QueueBalanceOpenerDoubleCheck();
                    else if (BalanceOpenerPostHeatDrills == 2)
                        QueueBalanceOpenerCheckmate();
                }
            }
        }
        #endregion

        #endregion

        #region Full Rotation Execution

        if (strategy.HoldEverything())
            return;

        #region Opener / Other
        //Stop all for Flamethrower
        if (StopForFlamethrower &&
            strategy.Option(Track.Flamethrower).As<AllowOrForbid>() != AllowOrForbid.Forbid &&
            LastActionUsed(AID.Flamethrower))
            return;

        if (!normalOvercapOnly && CountdownRemaining == null)
        {
            if (!Player.InCombat && In25y(mainTarget))
            {
                if (RAleft == 0 && ActionReady(AID.Reassemble)) //RA first
                    QueueOGCD(AID.Reassemble, Player, OGCDPriority.Forced);
                if (RAleft > 0)
                    Opener(openerOpt, mainTarget);
            }
        }
        if (CountdownRemaining > 0)
        {
            if (normalOvercapOnly)
                return;

            if (!strategy.HoldCDs() && !strategy.HoldBuffs())
                QueuePotionIfNeeded(potStrat);

            if (CountdownRemaining < 5 && RAleft == 0 && ActionReady(AID.Reassemble))
                QueueOGCD(AID.Reassemble, Player, OGCDPriority.Forced);
            if (CountdownRemaining < 1.15f)
                Opener(openerOpt, mainTarget);
            return;
        }
        if (!normalOvercapOnly && !strategy.HoldCDs() && !strategy.HoldBuffs())
            QueuePotionIfNeeded(potStrat);

        #endregion

        #region Standard Rotation
        if (!OverheatActive)
        {
            var aoesTarget = AOETargetChoice(mainTarget, BestConeTarget?.Actor, aoe, strategy);
            var stTarget = SingleTargetChoice(mainTarget, aoe);
            var bestTarget = WantAOE ? aoesTarget : stTarget;
            var (aoeAction, aoeTarget) = aoeStrat switch
            {
                AOEStrategy.AutoFinish => (AutoFinish, bestTarget),
                AOEStrategy.AutoBreak => (AutoBreak, bestTarget),
                AOEStrategy.ForceST => (ST, stTarget),
                AOEStrategy.ForceAOEFinish => (AOEFinish, aoesTarget),
                AOEStrategy.ForceAOEBreak => (BestSpreadShot, aoesTarget),
                _ => (AID.None, null)
            };
            QueueGCD(aoeAction, aoeTarget, CombatTimer > 90 && ComboTimer is < 8f and not 0 ? GCDPriority.High + 1 : GCDPriority.Low);
        }
        #endregion

        #region Cooldowns
        if (!strategy.HoldAbilities())
        {
            if (!strategy.HoldCDs())
            {
                if (normalOvercapOnly)
                {
                    if (ShouldUseDrillOvercapOnly(mainTarget))
                        QueueGCD(BestDrill, WantAOE ? AOETargetChoice(mainTarget, BestConeTarget?.Actor, drill, strategy) : SingleTargetChoice(mainTarget, drill), GCDPriority.ExtremelyHigh + 9);
                    if (ShouldUseDoubleCheckOvercapOnly(mainTarget))
                        QueueOGCD(BestGauss, AOETargetChoice(mainTarget, Unlocked(AID.DoubleCheck) ? BestSplashTarget?.Actor : mainTarget, Unlocked(AID.DoubleCheck) ? dc : gauss, strategy), OGCDPriority.High);
                    if (ShouldUseCheckmateOvercapOnly(mainTarget))
                        QueueOGCD(BestRicochet, AOETargetChoice(mainTarget, BestSplashTarget?.Actor, Unlocked(AID.Checkmate) ? cm : ricochet, strategy), OGCDPriority.High);
                }
                else if (balanceOpener)
                    QueueBalanceOpenerOGCDs(!strategy.HoldBuffs(), !strategy.HoldGauge());

                if (!normalOvercapOnly && !strategy.HoldBuffs())
                {
                    if (!balanceOpener)
                    {
                        var (wfCondition, wfPrio) = ShouldUseWildfire(wfStrat, mainTarget);
                        if (wfCondition)
                        {
                            if (wfStrat == WildfireStrategy.End)
                                QueueOGCD(AID.Detonator, Player, wfPrio);
                            else
                                QueueOGCD(AID.Wildfire, SingleTargetChoice(mainTarget, wf), wfPrio);
                        }

                        var (bsCondition, bsPrio) = ShouldUseBarrelStabilizer(bsStrat, mainTarget);
                        if (bsCondition)
                            QueueOGCD(AID.BarrelStabilizer, Player, bsPrio);
                    }
                }
                if (!normalOvercapOnly)
                {
                    var (aaCondition, aaPrio) = ShouldUseAirAnchor(aaStrat, mainTarget);
                    if (aaCondition)
                        QueueGCD(BestAirAnchor, SingleTargetChoice(mainTarget, aa) ?? mainTarget, aaPrio);

                    var (csCondition, csPrio) = ShouldUseChainSaw(csStrat, mainTarget);
                    if (csCondition)
                        QueueGCD(AID.ChainSaw, AOETargetChoice(mainTarget, BestChainSawTarget?.Actor, cs, strategy) ?? BestChainSawTarget?.Actor, csPrio);

                    var (drillCondition, drillPrio) = ShouldUseDrill(drillStrat, mainTarget);
                    if (drillCondition)
                    {
                        if (drillStrat is DrillStrategy.Automatic)
                            QueueGCD(BestDrill, WantAOE ? AOETargetChoice(mainTarget, BestConeTarget?.Actor, drill, strategy) : SingleTargetChoice(mainTarget, drill), drillPrio);
                        if (drillStrat is DrillStrategy.OnlyDrill or DrillStrategy.ForceDrill)
                            QueueGCD(AID.Drill, SingleTargetChoice(mainTarget, drill), drillPrio);
                        if (drillStrat is DrillStrategy.OnlyBioblaster or DrillStrategy.ForceBioblaster)
                            QueueGCD(AID.Bioblaster, AOETargetChoice(mainTarget, BestConeTarget?.Actor, drill, strategy), drillPrio);
                    }

                    var (evCondition, evPrio) = ShouldUseExcavator(evStrat, mainTarget);
                    if (evCondition)
                        QueueGCD(AID.Excavator, AOETargetChoice(mainTarget, BestSplashTarget?.Actor, ev, strategy), evStrat is GCDStrategy.Force ? GCDPriority.Forced : evPrio);

                    var (fmfCondition, fmfPrio) = ShouldUseFullMetalField(fmfStrat, wfStrat, mainTarget);
                    if (fmfCondition && !(balanceOpener && BalanceOpenerLastGCD == AID.Excavator && !BalanceOpenerUsedPreFMFDrill))
                        QueueGCD(AID.FullMetalField, AOETargetChoice(mainTarget, BestSplashTarget?.Actor, fmf, strategy), fmfStrat is GCDStrategy.Force ? GCDPriority.Forced : fmfPrio);
                }

                if (!normalOvercapOnly && !strategy.HoldBuffs() && !balanceOpener)
                {
                    // after the tool GCDs so NextGCD already reflects them
                    var (raCondition, raPrio) = ShouldUseReassemble(assembleStrat, mainTarget);
                    if (raCondition)
                        QueueOGCD(AID.Reassemble, Player, raPrio);
                }

                if (!normalOvercapOnly && !balanceOpener)
                {
                    var (dcCondition, dcPrio) = ShouldUseDoubleCheck(dcStrat, mainTarget);
                    var (grCondition, grPrio) = ShouldUseDoubleCheck(gaussStrat, mainTarget);
                    if (Unlocked(AID.DoubleCheck) ? dcCondition : grCondition)
                        QueueOGCD(BestGauss, AOETargetChoice(mainTarget, Unlocked(AID.DoubleCheck) ? BestSplashTarget?.Actor : mainTarget, Unlocked(AID.DoubleCheck) ? dc : gauss, strategy), Unlocked(AID.DoubleCheck) ? dcPrio : grPrio);
                    var (cmCondition, cmPrio) = ShouldUseCheckmate(cmStrat, mainTarget);
                    var (ricochetCondition, ricoPrio) = ShouldUseCheckmate(ricochetStrat, mainTarget);
                    if (Unlocked(AID.Checkmate) ? cmCondition : ricochetCondition)
                        QueueOGCD(BestRicochet, AOETargetChoice(mainTarget, BestSplashTarget?.Actor, Unlocked(AID.Checkmate) ? cm : ricochet, strategy), Unlocked(AID.Checkmate) ? cmPrio : ricoPrio);
                }

                if (!normalOvercapOnly && ShouldUseFlamethrower(ftStrat, mainTarget))
                    QueueGCD(AID.Flamethrower, AOETargetChoice(mainTarget, BestFlamethrowerTarget?.Actor, ft, strategy), ftStrat is AllowOrForbid.Force ? GCDPriority.Forced : GCDPriority.ModeratelyLow);
            }
            if (!normalOvercapOnly && !strategy.HoldGauge())
            {
                if (!balanceOpener)
                {
                    var (hcCondition, hcPrio) = ShouldUseHypercharge(hcStrat, mainTarget);
                    if (hcCondition)
                        QueueOGCD(AID.Hypercharge, Player, hcPrio);
                    if (ShouldUseBattery(batteryStrat))
                    {
                        if (batteryStrat is BatteryStrategy.Automatic or BatteryStrategy.Fifty or BatteryStrategy.Hundred or BatteryStrategy.RaidBuffs)
                            QueueOGCD(Unlocked(AID.AutomatonQueen) ? AID.AutomatonQueen : AID.RookAutoturret, Player, OGCDPriority.Severe);

                        if (batteryStrat == BatteryStrategy.End)
                            QueueOGCD(Unlocked(AID.QueenOverdrive) ? AID.QueenOverdrive : AID.RookOverdrive, Player, OGCDPriority.Critical);
                    }
                }
            }
            if (ShouldUseHeat(hspOpt, mainTarget))
            {
                if (hspOpt == HeatOption.Automatic)
                    QueueGCD(BestHeat, AOETargetChoice(mainTarget, BestConeTarget?.Actor, hsp, strategy), GCDPriority.High);
                if (hspOpt == HeatOption.OnlyHeatBlast)
                    QueueGCD(BestHeatBlast, SingleTargetChoice(mainTarget, hsp), GCDPriority.High);
                if (hspOpt == HeatOption.OnlyAutoCrossbow)
                    QueueGCD(Unlocked(AID.AutoCrossbow) ? AID.AutoCrossbow : BestHeatBlast, AOETargetChoice(mainTarget, BestConeTarget?.Actor, hsp, strategy), GCDPriority.High);
            }
        }
        #endregion
        #endregion

        #region AI
        if (primaryTarget != null)
        {
            var aoebreakpoint = OverheatActive && Unlocked(AID.AutoCrossbow) ? 3 : 2;
            GoalZoneCombined(strategy, 25, Hints.GoalAOECone(primaryTarget.Actor, 12, 60.Degrees()), AID.SpreadShot, aoebreakpoint);
        }
        #endregion
    }
}
