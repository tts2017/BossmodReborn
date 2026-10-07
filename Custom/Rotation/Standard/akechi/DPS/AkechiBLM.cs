using BossMod.BLM;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.akechi.Custom;

//TODO: cleanup this file - it works ok now, but it's a fucking mess to look at
public sealed class AkechiBLM(RotationModuleManager manager, Actor player) : AkechiTools<AID, TraitID>(manager, player)
{
    public enum Track { AOE = SharedTrack.Count, Thunder, Ender, Polyglot, Manafont, Triplecast, Swiftcast, LeyLines, TPUS, Movement, Casting, Amplifier, Retrace, BTL }
    public enum AOEStrategy { Automatic, ForceST, ForceAOE }
    public enum ThunderStrategy { Allow3, Allow6, Allow9, AllowNoDOT, ForceAny, ForceST, ForceAOE, Forbid }
    public enum EnderStrategy { Automatic, OnlyDespair, OnlyFlare, ForceDespair, ForceFlare }
    public enum PolyglotStrategy { AutoSpendAll, AutoHold1, AutoHold2, AutoHold3, XenoSpendAll, XenoHold1, XenoHold2, XenoHold3, FoulSpendAll, FoulHold1, FoulHold2, FoulHold3, ForceAny, ForceXeno, ForceFoul, Delay }
    public enum CommonStrategy { Automatic, Force, ForceWeave, Delay }
    public enum TPUSStrategy { Allow, OOConly, Forbid }
    public enum MovementStrategy { Allow, AllowNoScathe, OnlyGCDs, OnlyGCDsNoScathe, OnlyOGCDs, OnlyScathe, Forbid }
    public enum CastingOption { Forbid, Allow }
    public enum CommonOption { Forbid, Allow, AllowNoMoving }

    public static RotationModuleDefinition Definition()
    {
        var res = new RotationModuleDefinition("Akechi BLM [Custom]", "標準回しモジュール", "標準回し(Akechi)|DPS", "Akechi", RotationModuleQuality.Basic, BitMask.Build(Class.THM, Class.BLM), 100);

        res.DefineTargeting();
        res.DefineHold();
        res.DefinePotion(ActionDefinitions.IDPotionInt);

        res.Define(Track.AOE).As<AOEStrategy>("ST/AOE", "単体 / 範囲回し", 300)
            .AddOption(AOEStrategy.Automatic, "周囲の敵数に応じて最適な回しを自動使用する")
            .AddOption(AOEStrategy.ForceST, "周囲の敵数に関係なく単体回しを強制する")
            .AddOption(AOEStrategy.ForceAOE, "周囲の敵数に関係なく範囲回しを強制する")
            .AddAssociatedActions(
                AID.Fire1, AID.Fire3, AID.Fire4, AID.Despair, AID.FlareStar,
                AID.Fire2, AID.HighFire2, AID.Flare,
                AID.Blizzard1, AID.Blizzard3, AID.Blizzard4,
                AID.Blizzard2, AID.HighBlizzard2, AID.Freeze);

        res.Define(Track.Thunder).As<ThunderStrategy>("DOT", "Thunder", 200)
            .AddOption(ThunderStrategy.Allow3, "対象の DoT 残りが3秒以下なら Thunder を許可する", 0, 0, ActionTargets.Hostile, 6)
            .AddOption(ThunderStrategy.Allow6, "対象の DoT 残りが6秒以下なら Thunder を許可する", 0, 0, ActionTargets.Hostile, 6)
            .AddOption(ThunderStrategy.Allow9, "対象の DoT 残りが9秒以下なら Thunder を許可する", 0, 0, ActionTargets.Hostile, 6)
            .AddOption(ThunderStrategy.AllowNoDOT, "対象に DoT が無いときだけ Thunder を許可する", 0, 0, ActionTargets.Hostile, 6)
            .AddOption(ThunderStrategy.ForceAny, "DoT 状況に関係なく最適な Thunder を強制する", 0, 30, ActionTargets.Hostile, 6)
            .AddOption(ThunderStrategy.ForceST, "DoT 状況に関係なく単体 Thunder を強制する", 0, 30, ActionTargets.Hostile, 6)
            .AddOption(ThunderStrategy.ForceAOE, "DoT 状況に関係なく範囲 Thunder を強制する", 0, 24, ActionTargets.Hostile, 26)
            .AddOption(ThunderStrategy.Forbid, "Thunder を手動 / 戦術用に温存する", 0, 0, ActionTargets.Hostile, 6)
            .AddAssociatedActions(
                AID.Thunder1, AID.Thunder3, AID.HighThunder,
                AID.Thunder2, AID.Thunder4, AID.HighThunder2);

        res.Define(Track.Ender).As<EnderStrategy>("AF.Ender", "Despair / Flare", 194)
            .AddOption(EnderStrategy.Automatic, "周囲の敵数に応じて Fire フェーズを Despair または Flare で締める", 0, 0, ActionTargets.Hostile, 50)
            .AddOption(EnderStrategy.OnlyDespair, "対象数に関係なく Despair だけで Fire フェーズを締める", 0, 0, ActionTargets.Hostile, 72)
            .AddOption(EnderStrategy.OnlyFlare, "対象数に関係なく Flare だけで Fire フェーズを締める", 0, 0, ActionTargets.Hostile, 50)
            .AddOption(EnderStrategy.ForceDespair, "可能なら Despair を強制する", 0, 0, ActionTargets.Hostile, 72)
            .AddOption(EnderStrategy.ForceFlare, "可能なら Flare を強制する", 0, 0, ActionTargets.Hostile, 50)
            .AddAssociatedActions(AID.Despair, AID.Flare);

        res.Define(Track.Polyglot).As<PolyglotStrategy>("Polyglot", "Xenoglossy / Foul", 197)
            .AddOption(PolyglotStrategy.AutoSpendAll, "対象数に応じて最適な Polyglot 技を選び、すべて早めに使う", 0, 0, ActionTargets.Hostile, 70)
            .AddOption(PolyglotStrategy.AutoHold1, "対象数に応じて最適な Polyglot 技を選ぶ - 1つ温存する", 0, 0, ActionTargets.Hostile, 70)
            .AddOption(PolyglotStrategy.AutoHold2, "対象数に応じて最適な Polyglot 技を選ぶ - 2つ温存する", 0, 0, ActionTargets.Hostile, 70)
            .AddOption(PolyglotStrategy.AutoHold3, "対象数に応じて最適な Polyglot 技を選ぶ - 可能な限りすべて温存する", 0, 0, ActionTargets.Hostile, 70)
            .AddOption(PolyglotStrategy.XenoSpendAll, "対象数に関係なく Xenoglossy を Polyglot 消費技として使い切る", 0, 0, ActionTargets.Hostile, 80)
            .AddOption(PolyglotStrategy.XenoHold1, "対象数に関係なく Xenoglossy を Polyglot 消費技として使う - 1つ温存する", 0, 0, ActionTargets.Hostile, 80)
            .AddOption(PolyglotStrategy.XenoHold2, "対象数に関係なく Xenoglossy を Polyglot 消費技として使う - 2つ温存する", 0, 0, ActionTargets.Hostile, 80)
            .AddOption(PolyglotStrategy.XenoHold3, "対象数に関係なく Xenoglossy を Polyglot 消費技として使う - 可能な限りすべて温存する", 0, 0, ActionTargets.Hostile, 80)
            .AddOption(PolyglotStrategy.FoulSpendAll, "対象数に関係なく Foul を Polyglot 消費技として使う", 0, 0, ActionTargets.Hostile, 70)
            .AddOption(PolyglotStrategy.FoulHold1, "対象数に関係なく Foul を Polyglot 消費技として使う - 1つ温存する", 0, 0, ActionTargets.Hostile, 70)
            .AddOption(PolyglotStrategy.FoulHold2, "対象数に関係なく Foul を Polyglot 消費技として使う - 2つ温存する", 0, 0, ActionTargets.Hostile, 70)
            .AddOption(PolyglotStrategy.FoulHold3, "対象数に関係なく Foul を Polyglot 消費技として使う - 可能な限りすべて温存する", 0, 0, ActionTargets.Hostile, 70)
            .AddOption(PolyglotStrategy.ForceAny, "対象数に応じて最適な Polyglot 技を強制する", 0, 0, ActionTargets.Hostile, 70)
            .AddOption(PolyglotStrategy.ForceXeno, "Xenoglossy を強制する", 0, 0, ActionTargets.Hostile, 80)
            .AddOption(PolyglotStrategy.ForceFoul, "Foul を強制する", 0, 0, ActionTargets.Hostile, 70)
            .AddOption(PolyglotStrategy.Delay, "Polyglot 技を手動 / 戦術用に温存する", 0, 0, ActionTargets.Hostile, 70)
            .AddAssociatedActions(AID.Xenoglossy, AID.Foul);

        res.Define(Track.Manafont).As<CommonStrategy>("MF", "Manafont", 196)
            .AddOption(CommonStrategy.Automatic, "Manafont を自動使用する", 0, 0, ActionTargets.Self, 30)
            .AddOption(CommonStrategy.Force, "Manafont を最速で強制する", 100, 0, ActionTargets.Self, 30, 83)
            .AddOption(CommonStrategy.ForceWeave, "次の可能な挟み枠で Manafont を強制する", 100, 0, ActionTargets.Self, 30, 83)
            .AddOption(CommonStrategy.Delay, "Manafont を遅らせる", 0, 0, ActionTargets.Self, 30)
            .AddAssociatedActions(AID.Manafont);

        res.Define(Track.Triplecast).As<CommonStrategy>("3xCast", "Triplecast", 195)
            .AddOption(CommonStrategy.Automatic, "チャージがあれば、Fire フェーズ後に Blizzard III を即詠唱するため Triplecast を自動使用する (例: Despair->Transpose->Triplecast->B3)", 0, 0, ActionTargets.Self, 66)
            .AddOption(CommonStrategy.Force, "Triplecast を最速で強制する", 0, 15, ActionTargets.Self, 66)
            .AddOption(CommonStrategy.ForceWeave, "次の可能な挟み枠で Triplecast を強制する", 0, 15, ActionTargets.Self, 66)
            .AddOption(CommonStrategy.Delay, "Triplecast を遅らせる", 0, 0, ActionTargets.Self, 66)
            .AddAssociatedActions(AID.Triplecast);

        res.Define(Track.Swiftcast).As<CommonStrategy>("Swift", "Swiftcast", 195)
            .AddOption(CommonStrategy.Automatic, "使用可能なら、Fire フェーズ後に Blizzard III を即詠唱するため Swiftcast を自動使用する (例: Despair->Transpose->Swiftcast->B3)", 0, 0, ActionTargets.Self, 66)
            .AddOption(CommonStrategy.Force, "Swiftcast を最速で強制する", 0, 10, ActionTargets.Self, 18, 93)
            .AddOption(CommonStrategy.ForceWeave, "次の可能な挟み枠で Swiftcast を強制する", 0, 10, ActionTargets.Self, 18, 93)
            .AddOption(CommonStrategy.Delay, "戦術用に Swiftcast を遅らせる", 0, 0, ActionTargets.Self, 18)
            .AddAssociatedActions(AID.Swiftcast);

        res.Define(Track.LeyLines).As<CommonStrategy>("LL", "Ley Lines", 197)
            .AddOption(CommonStrategy.Automatic, "Ley Lines を自動使用する", 0, 0, ActionTargets.Self, 52)
            .AddOption(CommonStrategy.Force, "Ley Lines を最速で強制する", 0, 20, ActionTargets.Self, 52)
            .AddOption(CommonStrategy.ForceWeave, "次の可能な挟み枠で Ley Lines を強制する", 0, 20, ActionTargets.Self, 52)
            .AddOption(CommonStrategy.Delay, "Ley Lines を遅らせる", 0, 0, ActionTargets.Self, 52)
            .AddAssociatedActions(AID.LeyLines);

        res.Define(Track.TPUS).As<TPUSStrategy>("TP/US", "Transpose & Umbral Soul", 198)
            .AddOption(TPUSStrategy.Allow, "非戦闘時、または攻撃可能な敵が近くにいないときに Transpose + Umbral Soul を許可する", minLevel: 35)
            .AddOption(TPUSStrategy.OOConly, "完全に非戦闘時だけ Transpose + Umbral Soul を許可する", minLevel: 35)
            .AddOption(TPUSStrategy.Forbid, "Transpose + Umbral Soul を禁止する", minLevel: 35)
            .AddAssociatedActions(AID.Transpose, AID.UmbralSoul);

        res.Define(Track.Movement).As<MovementStrategy>("Move", "移動オプション", 199)
            .AddOption(MovementStrategy.Allow, "移動中に適切なアクションをすべて許可する - Swiftcast->Triplecast->Polyglot->Thunder->Scathe")
            .AddOption(MovementStrategy.AllowNoScathe, "Scathe を除く移動用アクションをすべて許可する - Swiftcast->Triplecast->Polyglot->Thunder")
            .AddOption(MovementStrategy.OnlyGCDs, "移動中はインスタント GCD のみを使う - Polyglot->Thunder->Scathe")
            .AddOption(MovementStrategy.OnlyGCDsNoScathe, "移動中は Scathe を除くインスタント GCD のみを使う - Polyglot->Thunder")
            .AddOption(MovementStrategy.OnlyOGCDs, "移動中は OGCD のみを使う - Swiftcast->Triplecast")
            .AddOption(MovementStrategy.OnlyScathe, "移動中は Scathe のみを使う (非推奨)")
            .AddOption(MovementStrategy.Forbid, "移動中のアクション使用を禁止する");

        res.Define(Track.Casting).As<CastingOption>("Cast", "移動中詠唱", 199)
            .AddOption(CastingOption.Forbid, "移動中の詠唱を禁止する")
            .AddOption(CastingOption.Allow, "移動中の詠唱を許可する");

        res.DefineOGCD(Track.Amplifier, AID.Amplifier, "Amplifier", "Amplifier", 196, 120, 0, ActionTargets.Self, 86);

        res.Define(Track.Retrace).As<CommonOption>("Rt", "Retrace", 197)
            .AddOption(CommonOption.Forbid, "Retrace を禁止する")
            .AddOption(CommonOption.Allow, "Retrace を許可する")
            .AddOption(CommonOption.AllowNoMoving, "停止中のみ Retrace を許可する")
            .AddAssociatedActions(AID.Retrace);

        res.Define(Track.BTL).As<CommonOption>("BTL", "Between The Lines", 197)
            .AddOption(CommonOption.Forbid, "Between the Lines を禁止する")
            .AddOption(CommonOption.Allow, "Between the Lines を許可する")
            .AddOption(CommonOption.AllowNoMoving, "停止中のみ Between the Lines を許可する")
            .AddAssociatedActions(AID.BetweenTheLines);

        return res;
    }

    private bool WantAOE;
    private bool ShouldUseSTDOT;
    private bool WantAOEDOT;
    private int NumSplashTargets;
    private Enemy? BestSplashTargets;
    private Enemy? BestDOTTargets;
    private Enemy? BestSplashTarget;
    private Enemy? BestDOTTarget;
    private bool ForceST;
    private bool ForceAOE;

    private BlackMageGauge Gauge => World.Client.GetGauge<BlackMageGauge>();
    private sbyte ElementStance => Gauge.ElementStance;
    private int MaxAspect => Unlocked(TraitID.AspectMastery3) ? 3 :
                             Unlocked(TraitID.AspectMastery2) ? 2 : 1;
    private bool NoUIorAF => ElementStance is 0 and not (1 or 2 or 3 or -1 or -2 or -3);
    private bool InAF => ElementStance is (1 or 2 or 3) and not (0 or -1 or -2 or -3);
    private bool InUI => ElementStance is (-1 or -2 or -3) and not (0 or 1 or 2 or 3);
    private int AF => Gauge.AstralStacks;
    private bool MaxAF => InAF && AF == MaxAspect;
    private int Souls => Gauge.AstralSoulStacks;
    private int MaxSouls => Unlocked(AID.FlareStar) ? 6 : 0;
    private bool HasMaxSouls => Souls == MaxSouls;
    private int UI => Gauge.UmbralStacks;
    private byte Hearts => Gauge.UmbralHearts;
    private int MaxHearts => Unlocked(TraitID.UmbralHeart) ? 3 : 0;
    private bool HasMaxHearts => Hearts == MaxHearts;
    private bool MaxUI => InUI && UI == MaxAspect && HasMaxHearts && MP == MaxMP;
    private int Polyglots => Gauge.PolyglotStacks;
    private int MaxPolyglots => Unlocked(TraitID.EnhancedPolyglotII) ? 3 :
                                Unlocked(TraitID.EnhancedPolyglot) ? 2 :
                                Unlocked(AID.Foul) ? 1 : 0;
    private bool HasMaxPolyglots => Polyglots == MaxPolyglots;
    private int PolyglotTimer => Gauge.EnochianTimer;
    private bool HasParadox => Gauge.ParadoxActive;
    private bool CanFoul => Unlocked(AID.Foul) && Polyglots > 0;
    private bool CanXG => Unlocked(AID.Xenoglossy) && Polyglots > 0;
    private bool CanParadox => Unlocked(AID.Paradox) && HasParadox && MP >= 1600;
    private bool CanLL => HasCharge(AID.LeyLines) &&
                          !HasStatus(SID.LeyLines);
    private bool CanAmplify => ActionReady(AID.Amplifier);
    private bool CanTC => HasCharge(AID.Triplecast) &&
                          !HasStatus(SID.Triplecast);
    private bool CanRetrace => ActionReady(AID.Retrace) &&
                               HasStatus(SID.LeyLines) &&
                               !HasStatus(SID.CircleOfPower);
    private bool CanBTL => ActionReady(AID.BetweenTheLines) &&
                           HasStatus(SID.LeyLines) &&
                           !HasStatus(SID.CircleOfPower);
    private bool HasThunderhead => HasStatus(SID.Thunderhead);
    private bool HasFirestarter => Unlocked(TraitID.Firestarter) &&
                                   HasStatus(SID.Firestarter);

    private AID BestThunderST => Unlocked(AID.HighThunder) ? AID.HighThunder : Unlocked(AID.Thunder3) ? AID.Thunder3 : AID.Thunder1;
    private AID BestThunderAOE => Unlocked(AID.HighThunder2) ? AID.HighThunder2 : Unlocked(AID.Thunder4) ? AID.Thunder4 : Unlocked(AID.Thunder2) ? AID.Thunder2 : AID.Thunder1;
    private AID BestThunder => WantAOE ? BestThunderAOE : BestThunderST;
    private AID BestPolyglot => WantAOE ? AID.Foul : BestXenoglossy;
    private AID BestXenoglossy => Unlocked(AID.Xenoglossy) ? AID.Xenoglossy : AID.Foul;
    private AID BestBlizzardAOE => Unlocked(AID.HighBlizzard2) ? AID.HighBlizzard2 : Unlocked(AID.Blizzard2) ? AID.Blizzard2 : AID.Blizzard1;
    private AID BestDespair => Unlocked(AID.Despair) ? AID.Despair : AID.Flare;

    private bool HasInstants(StrategyValues strategy, Actor? target)
    {
        var canUseGaugeInstant = !strategy.HoldGauge();
        return HasSwiftcast
            || HasStatus(SID.Triplecast)
            || (Unlocked(AID.Fire3) && HasFirestarter && ((InAF && AF < 3) || MaxUI))
            || (canUseGaugeInstant && WantParadox)
            || (canUseGaugeInstant && WantThunder(strategy, target).Condition)
            || (canUseGaugeInstant && WantPolyglot(strategy, target).Condition)
            || (Unlocked(AID.Despair) && Unlocked(AID.FlareStar) && InAF && MP < 1600 && Souls == 0);
    }
    private static readonly SID[] ThunderStatuses = { SID.Thunder, SID.ThunderII, SID.ThunderIII, SID.ThunderIV, SID.HighThunder, SID.HighThunderII };
    private float ThunderRemaining(Actor? target)
    {
        if (target == null)
            return float.MaxValue;

        // called many times per frame - plain loop, no LINQ/closure allocation
        var left = 0f;
        foreach (var status in ThunderStatuses)
        {
            var statusLeft = StatusDetails(target, (uint)status, Player.InstanceID).Left;
            if (statusLeft > left)
                left = statusLeft;
        }
        return left;
    }
    private bool WantParadox => CanParadox && !WantAOE && ((InAF && !HasFirestarter) || (InUI && Hearts == MaxHearts) || (InAF && Souls == 0 && MP < 3200));
    private (bool Condition, AID Action, GCDPriority Priority) WantThunder(StrategyValues strategy, Actor? target)
    {
        var th = strategy.Option(Track.Thunder);
        var thStrat = th.As<ThunderStrategy>();
        if (target == null || !HasThunderhead || !In25y(target))
            return (false, AID.None, GCDPriority.None);

        var dotLeft = ThunderRemaining(target);
        return thStrat switch
        {
            ThunderStrategy.Allow3 => (Player.InCombat && dotLeft <= 3, BestThunder, GCDPriority.High),
            ThunderStrategy.Allow6 => (Player.InCombat && dotLeft <= 6, BestThunder, GCDPriority.High),
            ThunderStrategy.Allow9 => (Player.InCombat && dotLeft <= 9, BestThunder, GCDPriority.High),
            ThunderStrategy.AllowNoDOT => (Player.InCombat && dotLeft <= 0, BestThunder, GCDPriority.ExtremelyHigh),
            ThunderStrategy.ForceAny => (true, BestThunder, GCDPriority.Forced),
            ThunderStrategy.ForceST => (true, BestThunderST, GCDPriority.Forced),
            ThunderStrategy.ForceAOE => (true, BestThunderAOE, GCDPriority.Forced),
            _ => (false, AID.None, GCDPriority.None)
        };
    }
    private (bool Condition, AID Action, GCDPriority Priority) WantPolyglot(StrategyValues strategy, Actor? target)
    {
        if (!Unlocked(AID.Foul) || Polyglots == 0 || target == null || !In25y(target))
            return (false, AID.None, GCDPriority.None);

        var pg = strategy.Option(Track.Polyglot);
        var pgStrat = pg.As<PolyglotStrategy>();
        var veryclose = PolyglotTimer <= 4000f && HasMaxPolyglots;
        var overcap = PolyglotTimer <= 8000 && HasMaxPolyglots;
        var ezTPSC = !Unlocked(AID.FlareStar) && Unlocked(AID.Xenoglossy) && ((InAF && MP == 0) || LastActionUsed(AID.Despair)); //Lv80-L99 - for easy weaving of TP+SC
        var condition = Polyglots > 0 && InCombat(target) && ((Unlocked(AID.Xenoglossy) && InAF &&
                (WantAOE ? HasParadox : !HasParadox) && MP < 800) || //MF prep
                (Unlocked(AID.Amplifier) && Cooldown(AID.Amplifier) < 1f)); //Amp prep
        var prio = veryclose ? GCDPriority.ExtremelyHigh + 1 : ((WantAOE || ForceAOE) ? GCDPriority.Average : GCDPriority.SlightlyHigh);
        var shouldSpend = InCombat(target) && (condition || ezTPSC || RaidBuffsLeft > 0 || veryclose);
        bool CanSpendAfterHolding(int hold) => Polyglots > hold || (HasMaxPolyglots && overcap);
        var spendAll = shouldSpend && CanSpendAfterHolding(0);
        var hold1 = shouldSpend && CanSpendAfterHolding(1);
        var hold2 = shouldSpend && CanSpendAfterHolding(2);
        var hold3 = HasMaxPolyglots && overcap;
        return pgStrat switch
        {
            PolyglotStrategy.AutoSpendAll => (spendAll, BestPolyglot, prio),
            PolyglotStrategy.XenoSpendAll => (spendAll, BestXenoglossy, prio),
            PolyglotStrategy.FoulSpendAll => (spendAll, AID.Foul, prio),
            PolyglotStrategy.AutoHold1 => (hold1, BestPolyglot, prio),
            PolyglotStrategy.XenoHold1 => (hold1, BestXenoglossy, prio),
            PolyglotStrategy.FoulHold1 => (hold1, AID.Foul, prio),
            PolyglotStrategy.AutoHold2 => (hold2, BestPolyglot, prio),
            PolyglotStrategy.XenoHold2 => (hold2, BestXenoglossy, prio),
            PolyglotStrategy.FoulHold2 => (hold2, AID.Foul, prio),
            PolyglotStrategy.AutoHold3 => (hold3, BestPolyglot, prio),
            PolyglotStrategy.XenoHold3 => (hold3, BestXenoglossy, prio),
            PolyglotStrategy.FoulHold3 => (hold3, AID.Foul, prio),
            PolyglotStrategy.ForceAny => (CanFoul, BestPolyglot, GCDPriority.Forced),
            PolyglotStrategy.ForceXeno => (CanXG, BestXenoglossy, GCDPriority.Forced),
            PolyglotStrategy.ForceFoul => (CanFoul, AID.Foul, GCDPriority.Forced),
            _ => (false, AID.None, GCDPriority.None),
        };
    }

    private void ST(StrategyValues strategy, Actor? target, GCDPriority prio)
    {
        if (WantAOE || ForceAOE)
            return;

        var sc = strategy.Option(Track.Swiftcast).As<CommonStrategy>();
        var tc = strategy.Option(Track.Triplecast).As<CommonStrategy>();
        var movementAllowsOGCD = !IsMoving || strategy.Option(Track.Movement).As<MovementStrategy>() is MovementStrategy.Allow or MovementStrategy.AllowNoScathe or MovementStrategy.OnlyOGCDs;
        if (Player.InCombat && NoUIorAF && target != null)
        {
            if (movementAllowsOGCD && CanSwiftcast && !HasStatus(SID.Triplecast) && sc != CommonStrategy.Delay)
                QueueOGCD(AID.Swiftcast, Player, OGCDPriority.High);

            QueueGCD(AID.Blizzard3, target, prio);
        }

        if (InAF)
        {
            if (MaxAF && MP >= (Hearts > 0 ? 800 : 1600) && (!Unlocked(AID.FlareStar) || Souls != 6))
                QueueGCD(Unlocked(AID.Fire4) ? AID.Fire4 : AID.Fire1, target, Status(SID.Triplecast) is <= 4.5f and not 0 ? prio + 500 : prio + 2);

            //if we end up in a situation where we can't use FlareStar but we can end, we will use Flare to save rotation
            if (Unlocked(AID.FlareStar) && MP is < 2400 and >= 800 && Souls is < 6 and >= 3)
                QueueGCD(AID.Flare, target, prio + 1);

            //if we so happen to not have AF3 on swap, then we instant-cast F3 for it
            if (Unlocked(AID.Fire3) && !MaxAF && MP >= 2000 && !HasFirestarter)
            {
                if (target != null && CanSwiftcast && !HasStatus(SID.Triplecast) && sc != CommonStrategy.Delay)
                    QueueOGCD(AID.Swiftcast, Player, OGCDPriority.High + 3);

                else if (target != null && CanTC && !HasStatus(SID.Swiftcast) && tc != CommonStrategy.Delay)
                    QueueOGCD(AID.Triplecast, Player, OGCDPriority.High + 2);

                QueueGCD(AID.Fire3, target, prio + 1);
            }

            if (Unlocked(AID.Blizzard3) &&
                (Unlocked(AID.FlareStar) ? MP < 800 && Souls != 6 :
                (Unlocked(AID.Flare) ? (MP < 800 || LastActionUsed(AID.Flare) || LastActionUsed(AID.Despair)) :
                MP < 1600)))
                QueueGCD(AID.Blizzard3, target, prio);
        }
        if (InUI)
        {
            if (LastActionUsed(AID.Blizzard3) || (Unlocked(TraitID.UmbralHeart) ? (UI == 3 && !HasMaxHearts) : MP != MaxMP))
                QueueGCD(Unlocked(AID.Blizzard4) ? AID.Blizzard4 : AID.Blizzard1, target, prio);

            //if we so happen to not have UI3 on swap, then we instant-cast B3 for it
            if (Unlocked(AID.Blizzard3) && UI != 3)
            {
                if (target != null && CanSwiftcast && !HasStatus(SID.Triplecast) && sc != CommonStrategy.Delay)
                    QueueOGCD(AID.Swiftcast, Player, OGCDPriority.High + 3);

                else if (target != null && CanTC && !HasStatus(SID.Swiftcast) && tc != CommonStrategy.Delay)
                    QueueOGCD(AID.Triplecast, Player, OGCDPriority.High + 2);

                QueueGCD(AID.Blizzard3, target, prio + 1);
            }

            //below Lv90 - we do not have access to Paradox, so we have to rawdog F3 after B4
            if (!HasParadox && Unlocked(AID.Fire3) && MaxUI)
                QueueGCD(AID.Fire3, target, prio);
        }
    }
    private void AOE(StrategyValues strategy, Actor? target, GCDPriority prio)
    {
        if (!WantAOE || ForceST)
            return;

        var sc = strategy.Option(Track.Swiftcast).As<CommonStrategy>();
        var movementAllowsOGCD = !IsMoving || strategy.Option(Track.Movement).As<MovementStrategy>() is MovementStrategy.Allow or MovementStrategy.AllowNoScathe or MovementStrategy.OnlyOGCDs;
        if (Player.InCombat && NoUIorAF && target != null)
        {
            if (movementAllowsOGCD && CanSwiftcast && !HasStatus(SID.Triplecast) && sc != CommonStrategy.Delay)
                QueueOGCD(AID.Swiftcast, Player, OGCDPriority.High);

            QueueGCD(BestBlizzardAOE, target, prio);
        }

        if (Unlocked(TraitID.UmbralHeart))
        {
            if (InUI)
            {
                if (MP < 2400 && !HasMaxHearts)
                    QueueGCD(AID.Freeze, target, prio);

                if (HasMaxHearts)
                {
                    if (!strategy.HoldGauge())
                    {
                        var (thCondition, thAction, thPrio) = WantThunder(strategy, target);
                        if (thCondition)
                            QueueGCD(thAction, target, thPrio);

                        var (pgCondition, pgAction, pgPrio) = WantPolyglot(strategy, target);
                        if (pgCondition)
                            QueueGCD(pgAction, target, pgPrio);
                    }

                    if (ActionReady(AID.Transpose) && MP >= 2400)
                        QueueOGCD(AID.Transpose, Player, OGCDPriority.High);
                }
            }
            if (InAF)
            {
                if (MP >= 800)
                    QueueGCD(AID.Flare, target, prio);

                if (MP < 800 && (!Unlocked(AID.FlareStar) || Souls < 6))
                {
                    if (!strategy.HoldGauge())
                    {
                        var (thCondition, thAction, thPrio) = WantThunder(strategy, target);
                        if (thCondition)
                            QueueGCD(thAction, target, thPrio);

                        var (pgCondition, pgAction, pgPrio) = WantPolyglot(strategy, target);
                        if (pgCondition)
                            QueueGCD(pgAction, target, pgPrio);
                    }

                    if (ActionReady(AID.Transpose) && Souls == 0)
                        QueueOGCD(AID.Transpose, Player, OGCDPriority.High);
                }
            }
        }
        if (!Unlocked(TraitID.UmbralHeart))
        {
            if (InAF)
            {
                if (Unlocked(AID.Fire2) && ((InUI && (MP == 10000 && UI == 3)) || (InAF && (Unlocked(TraitID.UmbralHeart) ? MP > 5500 : MP >= 3000))))
                    QueueGCD(Unlocked(AID.HighFire2) ? AID.HighFire2 : AID.Fire2, target, prio);

                if (Unlocked(AID.Flare) && MP < 3000 && MP >= 800)
                    QueueGCD(AID.Flare, target, prio);
            }

            if (InUI)
            {
                if (Unlocked(AID.Blizzard2) && ((InUI && UI < 3) || (InAF && MP < 800)))
                    QueueGCD(BestBlizzardAOE, target, prio);

                if (!LastActionUsed(AID.Freeze) && UI == 3 && MP < 10000)
                    QueueGCD(Unlocked(AID.Freeze) ? AID.Freeze : BestBlizzardAOE, target, prio);
            }
        }
    }

    public override void Execution(StrategyValues strategy, Enemy? primaryTarget)
    {
        var aoe = strategy.Option(Track.AOE);
        var aoeStrat = aoe.As<AOEStrategy>();
        ForceST = aoeStrat == AOEStrategy.ForceST;
        ForceAOE = aoeStrat == AOEStrategy.ForceAOE;
        (BestSplashTargets, NumSplashTargets) = GetBestTarget(primaryTarget, 25, IsSplashTarget);
        WantAOE = ForceAOE || (!ForceST && Unlocked(AID.Blizzard2) && NumSplashTargets > 2);
        ShouldUseSTDOT = Unlocked(AID.Thunder1) && (ForceST || NumSplashTargets <= 2);
        WantAOEDOT = !ForceST && (ForceAOE || (Unlocked(AID.Thunder2) && NumSplashTargets > 2));
        BestSplashTarget = WantAOE && NumSplashTargets > 1 ? BestSplashTargets : primaryTarget;
        (BestDOTTargets, _) = GetDOTTarget(primaryTarget, ThunderRemaining, 2);
        BestDOTTarget = WantAOEDOT ? BestSplashTargets : ShouldUseSTDOT ? BestDOTTargets : primaryTarget;
        var mainTarget = primaryTarget?.Actor;

        if (strategy.HoldEverything())
            return;

        GoalZoneSingle(25);

        var casting = strategy.Option(Track.Casting).As<CastingOption>();
        var movement = strategy.Option(Track.Movement).As<MovementStrategy>();
        var movementAllowsInstantGCD = !IsMoving || movement is MovementStrategy.Allow or MovementStrategy.AllowNoScathe or MovementStrategy.OnlyGCDs or MovementStrategy.OnlyGCDsNoScathe;
        var movementAllowsOGCD = !IsMoving || movement is MovementStrategy.Allow or MovementStrategy.AllowNoScathe or MovementStrategy.OnlyOGCDs;
        var movementAllowsScathe = IsMoving && (movement is MovementStrategy.Allow or MovementStrategy.OnlyScathe);
        var canHardcast = casting == CastingOption.Allow || !IsMoving;
        var hasCastInstantStatus = HasStatus(SID.Swiftcast) || HasStatus(SID.Triplecast);
        var canUseInstantGCD = canHardcast || (movementAllowsInstantGCD && HasInstants(strategy, mainTarget));
        var canRunNormalGCD = canHardcast || (movementAllowsInstantGCD && hasCastInstantStatus);
        if (!strategy.HoldAbilities())
        {
            if (mainTarget != null && NoUIorAF && CountdownRemaining is null or > 0 and < 3.7f)
                QueueGCD(WantAOE ? BestBlizzardAOE : MP < 8000 ? AID.Blizzard3 : AID.Fire3, mainTarget, GCDPriority.SlightlyHigh);

            var tpusStrat = strategy.Option(Track.TPUS).As<TPUSStrategy>();
            var tpusOk = (tpusStrat == TPUSStrategy.Allow &&
                (!Player.InCombat || (Player.InCombat && Hints.NumPriorityTargetsInAOECircle(Player.Position, 35) == 0))) ||
                (tpusStrat == TPUSStrategy.OOConly && !Player.InCombat);
            if (tpusOk && ((strategy.AutoTarget() && BestSplashTarget?.Actor == null) || (strategy.ManualTarget() && mainTarget == null)))
            {
                if (MaxUI && HasThunderhead)
                    return;

                if (ActionReady(AID.Transpose))
                {
                    var canFS = (Souls == 6 || (Souls >= 3 && MP >= 800));
                    if ((InAF && Unlocked(AID.FlareStar) ? !canFS : MP < MaxMP) || (InUI && !HasThunderhead))
                        QueueOGCD(AID.Transpose, Player, OGCDPriority.Max);
                }
                if (Unlocked(AID.UmbralSoul) &&
                    InUI && (UI < 3 || Hearts < MaxHearts || MP < MaxMP))
                    QueueGCD(AID.UmbralSoul, Player, GCDPriority.Minimal);
            }

            if (!strategy.HoldCDs())
            {
                var decreasePrio = Unlocked(AID.FlareStar) || HasInstants(strategy, mainTarget);
                if (!strategy.HoldBuffs())
                {
                    var mf = strategy.Option(Track.Manafont);
                    var mfStrat = mf.As<CommonStrategy>();
                    var mfMinimum = ActionReady(AID.Manafont) && InAF;
                    var (mfCondition, mfPrio) = mfStrat switch
                    {
                        CommonStrategy.Automatic => (InCombat(mainTarget) && InAF && (!Unlocked(AID.Paradox) || (WantAOE ? HasParadox : !HasParadox)) && MP < 800, OGCDPriority.ModeratelyLow + 4),
                        CommonStrategy.Force => (true, OGCDPriority.Forced),
                        CommonStrategy.ForceWeave => (CanWeaveIn, OGCDPriority.Forced),
                        _ => (false, OGCDPriority.None)
                    };
                    if (mfMinimum && mfCondition)
                        QueueOGCD(AID.Manafont, Player, mfPrio);

                    var ll = strategy.Option(Track.LeyLines);
                    var llStrat = ll.As<CommonStrategy>();
                    var (llCondition, llPrio) = llStrat switch
                    {
                        CommonStrategy.Automatic => (InCombat(mainTarget) && !IsMoving && (RaidBuffsLeft > 10f || RaidBuffsIn < 100), OGCDPriority.ModeratelyLow + 1),
                        CommonStrategy.Force => (true, OGCDPriority.Forced + 1500),
                        CommonStrategy.ForceWeave => (CanWeaveIn, OGCDPriority.Forced),
                        _ => (false, OGCDPriority.None)
                    };
                    if (CanLL && llCondition)
                        QueueOGCD(AID.LeyLines, Player, decreasePrio ? llPrio : llPrio + 2001);

                    var amp = strategy.Option(Track.Amplifier);
                    var ampStrat = amp.As<OGCDStrategy>();
                    if (ShouldUseOGCD(ampStrat, Player, CanAmplify, (InUI || InAF) && !HasMaxPolyglots))
                        QueueOGCD(AID.Amplifier, Player, OGCDPrio(ampStrat, OGCDPriority.ModeratelyLow));
                }

                var sc = strategy.Option(Track.Swiftcast);
                var scStrat = sc.As<CommonStrategy>();
                var (scCondition, scPrio) = scStrat switch
                {
                    CommonStrategy.Automatic => (InCombat(mainTarget) && !HasStatus(SID.Swiftcast) && !HasStatus(SID.Triplecast) && MP == 0 && Souls == 0, OGCDPriority.ModeratelyLow + 3),
                    CommonStrategy.Force => (true, OGCDPriority.Forced),
                    CommonStrategy.ForceWeave => (CanWeaveIn, OGCDPriority.Forced),
                    _ => (false, OGCDPriority.None)
                };

                var tc = strategy.Option(Track.Triplecast);
                var tcStrat = tc.As<CommonStrategy>();
                var (tcCondition, tcPrio) = tcStrat switch
                {
                    CommonStrategy.Automatic => (InCombat(mainTarget) && !HasStatus(SID.Swiftcast) && !HasStatus(SID.Triplecast) && InAF && MP is <= 1600, OGCDPriority.ModeratelyLow + 2),
                    CommonStrategy.Force => (true, OGCDPriority.Forced),
                    CommonStrategy.ForceWeave => (CanWeaveIn, OGCDPriority.Forced),
                    _ => (false, OGCDPriority.None)
                };
                if (movementAllowsOGCD && CanSwiftcast && !WantAOE && scCondition)
                    QueueOGCD(AID.Swiftcast, Player, decreasePrio ? scPrio : scPrio + 2003);
                else if (movementAllowsOGCD && CanTC && !WantAOE && tcCondition)
                    QueueOGCD(AID.Triplecast, Player, decreasePrio ? tcPrio : tcPrio + 2001);

                if (ActionReady(AID.Transpose) &&
                    ((MaxUI && Unlocked(AID.Paradox) && HasFirestarter && !HasParadox) ||
                    (MaxAF && (HasStatus(SID.Triplecast) || HasStatus(SID.Swiftcast)) &&
                    (Unlocked(AID.Flare) ? (LastActionUsed(AID.Flare) || LastActionUsed(AID.Despair) || (MP == 0 && Souls == 0)) : (LastActionUsed(AID.Fire1) && MP < 1600)))))
                    QueueOGCD(AID.Transpose, Player, OGCDPriority.High);
            }
            if (!strategy.HoldGauge())
            {
                var th = strategy.Option(Track.Thunder);
                var thTarget = AOETargetChoice(mainTarget, BestDOTTarget?.Actor, th, strategy);
                var thMinimum = InCombat(thTarget) && In25y(thTarget) && HasThunderhead;
                var (thCondition, thAction, thPrio) = WantThunder(strategy, thTarget);
                if (canUseInstantGCD && movementAllowsInstantGCD && thMinimum && thCondition)
                    QueueGCD(thAction, thTarget, thPrio);

                if (Unlocked(AID.Fire3) && InCombat(mainTarget) && !WantAOE &&
                    (HasFirestarter ? canUseInstantGCD && movementAllowsInstantGCD && ((InAF && AF < 3) || (InUI && MaxUI)) : canHardcast && InUI && MaxUI))
                    QueueGCD(AID.Fire3, mainTarget, GCDPriority.Average);

                if (canUseInstantGCD && movementAllowsInstantGCD && InCombat(mainTarget) && WantParadox)
                    QueueGCD(AID.Paradox, mainTarget, InUI ? GCDPriority.Average + 1 : GCDPriority.Average);

                var pgTarget = AOETargetChoice(mainTarget, BestSplashTarget?.Actor, strategy.Option(Track.Polyglot), strategy);
                var (pgCondition, pgAction, pgPrio) = WantPolyglot(strategy, pgTarget);
                if (canUseInstantGCD && movementAllowsInstantGCD && pgCondition)
                    QueueGCD(pgAction, pgTarget, pgPrio);
            }

            if (IsMoving && InCombat(mainTarget) && (!HasInstants(strategy, mainTarget) || !movementAllowsInstantGCD))
            {
                if (movementAllowsOGCD)
                {
                    var scMove = strategy.Option(Track.Swiftcast).As<CommonStrategy>();
                    var tcMove = strategy.Option(Track.Triplecast).As<CommonStrategy>();
                    if (CanSwiftcast && !HasStatus(SID.Triplecast) && scMove != CommonStrategy.Delay)
                        QueueOGCD(AID.Swiftcast, Player, OGCDPriority.Severe + 1);

                    else if (CanTC && !HasStatus(SID.Swiftcast) && tcMove != CommonStrategy.Delay)
                        QueueOGCD(AID.Triplecast, Player, OGCDPriority.Severe);
                }

                if (!strategy.HoldGauge() && (movement is MovementStrategy.Allow or MovementStrategy.AllowNoScathe or MovementStrategy.OnlyGCDs or MovementStrategy.OnlyGCDsNoScathe))
                {
                    var th = strategy.Option(Track.Thunder);
                    var thTarget = AOETargetChoice(mainTarget, BestDOTTarget?.Actor, th, strategy);
                    var (thCondition, thAction, thPrio) = WantThunder(strategy, thTarget);
                    if (thCondition)
                        QueueGCD(thAction, thTarget, thPrio);

                    var pg = strategy.Option(Track.Polyglot);
                    var pgTarget = AOETargetChoice(mainTarget, BestSplashTarget?.Actor, pg, strategy);
                    var (pgCondition, pgAction, pgPrio) = WantPolyglot(strategy, pgTarget);
                    if (pgCondition)
                        QueueGCD(pgAction, pgTarget, pgPrio);
                }

                if (movementAllowsScathe && Unlocked(AID.Scathe) && MP >= 800)
                    QueueGCD(AID.Scathe, SingleTargetChoice(mainTarget, aoe), GCDPriority.ExtremelyLow);
            }

            if (strategy.Potion() switch
            {
                PotionStrategy.AlignWithBuffs or PotionStrategy.AlignWithRaidBuffs => Player.InCombat && (RaidBuffsIn <= 5f || RaidBuffsLeft > 0),
                PotionStrategy.Immediate => true,
                _ => false
            })
                QueuePotINT();

            if (CanRetrace && strategy.Option(Track.Retrace).As<CommonOption>() switch
            {
                CommonOption.Allow => true,
                CommonOption.AllowNoMoving => !IsMoving,
                _ => false
            })
                QueueOGCD(AID.Retrace, Player, OGCDPriority.Forced);

            var zone = World.Actors.FirstOrDefault(x => x.OID == 0x179 && x.OwnerID == Player.InstanceID);
            if (zone != null && CanBTL && strategy.Option(Track.BTL).As<CommonOption>() switch
            {
                CommonOption.Allow => true,
                CommonOption.AllowNoMoving => !IsMoving,
                _ => false
            })
                Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.BetweenTheLines), Player, (int)ActionQueue.Priority.Medium, targetPos: zone.PosRot.XYZ());
        }

        if (canRunNormalGCD)
        {
            var fsTarget = strategy.AutoTarget() ? BestSplashTarget?.Actor : mainTarget;
            if (InCombat(fsTarget) && Unlocked(AID.FlareStar) && HasMaxSouls)
                QueueGCD(AID.FlareStar, fsTarget, GCDPriority.Average + 6);

            var e = strategy.Option(Track.Ender);
            var eStrat = e.As<EnderStrategy>();
            var fMinimum = Unlocked(AID.Flare) && MP >= 800 && InAF;
            var dMinimum = Unlocked(AID.Despair) && MP >= 800 && InAF;
            var fST = !Unlocked(AID.FlareStar) ? Unlocked(AID.Flare) && !Unlocked(AID.Despair) && MP is < 2400 and >= 800 && InAF :
                      ((Souls is >= 3 and < 6 && MP is < 2400 and >= 800 && InAF) || (DowntimeIn < 8 && Souls >= 3 && MP >= 800));
            var eTarget = AOETargetChoice(mainTarget, BestSplashTarget?.Actor, e, strategy);
            var eST = MP is <= 1600 and >= 800 || fST;
            var eAOE = Unlocked(AID.FlareStar) ? (Souls != 6 && MP >= 800) : (Unlocked(TraitID.UmbralHeart) ? (Hearts > 0 || (Hearts == 0 && MP >= 800)) : MP is < 2400 and >= 800);
            var eMinimum = fMinimum && In25y(eTarget) && (WantAOE ? eAOE : eST);
            var (eCondition, eAction, ePrio) = eStrat switch
            {
                EnderStrategy.Automatic => (eMinimum, fST ? AID.Flare : AID.Despair, GCDPriority.Average),
                EnderStrategy.OnlyDespair => (fMinimum, BestDespair, GCDPriority.Average),
                EnderStrategy.OnlyFlare => (fMinimum, AID.Flare, GCDPriority.Average),
                EnderStrategy.ForceDespair => (dMinimum, BestDespair, GCDPriority.Average),
                EnderStrategy.ForceFlare => (fMinimum, AID.Flare, GCDPriority.Average),
                _ => (false, AID.None, GCDPriority.None)
            };
            if (eCondition)
                QueueGCD(eAction, eTarget, ePrio);

            var aoePrio = Status(SID.Triplecast) is < 5f and > 2.5f ? GCDPriority.Average : Status(SID.Triplecast) is < 2.5f and > 0f ? GCDPriority.High + 1 : GCDPriority.Low;
            var aoeTarget = AOETargetChoice(mainTarget, BestSplashTarget?.Actor, aoe, strategy);
            if (aoeStrat is AOEStrategy.Automatic)
            {
                if (WantAOE)
                    AOE(strategy, aoeTarget, aoePrio + 201);
                else
                    ST(strategy, aoeTarget, aoePrio);
            }

            if (ForceAOE)
                AOE(strategy, aoeTarget, aoePrio + 201);

            if (ForceST)
                ST(strategy, aoeTarget, aoePrio);
        }
    }
}
