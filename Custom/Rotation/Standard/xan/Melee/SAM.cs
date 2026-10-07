using BossMod.SAM;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.xan.Custom;

public sealed class SAM(RotationModuleManager manager, Actor player) : Attackxan<AID, TraitID, SAM.Strategy>(manager, player, PotionType.Strength)
{
    public struct Strategy : IStrategyCommon
    {
        public Track<Targeting> Targeting;
        public Track<AOEStrategy> AOE;
        [Track("バースト/剣気", Actions = [AID.Ikishoten, AID.HissatsuSenei, AID.HissatsuGuren, AID.Zanshin, AID.Shoha], MinLevel = 68)]
        public Track<OffensiveStrategy> Buffs;

        [Track("薬")]
        public Track<SamPotionStrategy> Potion;

        [Track("返し", Actions = [AID.KaeshiSetsugekka, AID.KaeshiGoken, AID.TendoKaeshiSetsugekka, AID.TendoKaeshiGoken], MinLevel = 74)]
        public Track<TsubameStrategy> Tsubame;

        [Track("奥義/返し波切", Actions = [AID.OgiNamikiri, AID.KaeshiNamikiri], MinLevel = 90)]
        public Track<NamikiriStrategy> Namikiri;

        [Track(Action = AID.Higanbana)]
        public Track<BanaStrategy> Higanbana;

        [Track(Action = AID.Enpi)]
        public Track<EnpiStrategy> Enpi;

        [Track(Action = AID.MeikyoShisui)]
        public Track<MeikyoStrategy> Meikyo;

        [Track(Action = AID.TrueNorth)]
        public Track<TrueNorthStrategy> TrueNorth;

        public Track<OpenerStrategy> Opener;
        [Track("開幕バースト")]
        public Track<OpenerBurstStrategy> OpenerBurst;
        [Track("GCD")]
        public Track<GCDRouteStrategy> GCDRoute;

        [Track("ダウンタイム黙想", Action = AID.Meditate, MinLevel = 60)]
        public Track<EnabledByDefault> Meditate;

        [Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)]
        public Track<MechanicHintStrategy> MechanicHints;

        readonly Targeting IStrategyCommon.Targeting => Targeting.Value;
        readonly AOEStrategy IStrategyCommon.AOE => AOE.Value;
    }

    public enum BanaStrategy
    {
        [Option("Refresh every 60s according to standard rotation", Targets = ActionTargets.Hostile)]
        Automatic,
        [Option("Don't apply")]
        Delay,
        [Option("Apply to target ASAP, regardless of remaining duration", Targets = ActionTargets.Hostile)]
        Force
    }

    public enum EnpiStrategy
    {
        [Option("Use if Enhanced Enpi is active, or as a filler while a mechanic keeps you out of melee range and no combo is in progress", Targets = ActionTargets.Hostile)]
        Enhanced,
        [Option("Do not use")]
        None,
        [Option("Use when out of range", Targets = ActionTargets.Hostile)]
        Ranged
    }

    public enum MeikyoStrategy
    {
        [Option("自動: バースト/チャージ溢れ防止")]
        Auto,

        [Option("リキャスト使用: 可能なら55秒周期で使用")]
        Cooldown,

        [Option("使用しない")]
        Delay,

        [Option("即使用")]
        Force,

        [Option("1チャージ保持: 溢れそうな時だけ使用")]
        HoldOne
    }

    public enum TrueNorthStrategy
    {
        [Option("自動")]
        Auto,

        [Option("使用しない")]
        None
    }

    public enum SamPotionStrategy
    {
        [Option("使用しない")]
        None,

        [Option("2分バースト")]
        TwoMinuteBurst
    }

    public enum OpenerStrategy
    {
        [Option("Standard opener: Gekko (damage), Kasha (haste), Midare, Higanbana, Ogi")]
        Standard,
        [Option("Standard opener, with haste buff before damage buff")]
        KashaStandard,
        [Option("Gekko (damage) -> immediate Higanbana")]
        GekkoBana,
        [Option("Kasha (haste) -> immediate Higanbana")]
        KashaBana
    }

    public enum OpenerBurstStrategy
    {
        [Option("通常")]
        Normal,

        [Option("0秒バースト")]
        ZeroSecond
    }

    public enum GCDRouteStrategy
    {
        [Option("2.08ルート")]
        GCD208,

        [Option("2.14ルート")]
        GCD214
    }

    public enum TsubameStrategy
    {
        [Option("自動")]
        Auto,

        [Option("即使用")]
        Force,

        [Option("バーストまで保持")]
        Hold,

        [Option("使用しない")]
        Delay
    }

    public enum NamikiriStrategy
    {
        [Option("自動")]
        Auto,

        [Option("即使用")]
        Force,

        [Option("バーストまで保持")]
        Hold,

        [Option("使用しない")]
        Delay
    }

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("xan SAM [Custom]", "Samurai", "Standard rotation (xan)|Melee", "xan", RotationModuleQuality.Basic, BitMask.Build(Class.SAM), 100).WithStrategies<Strategy>();
    }

    public enum IaiRepeat
    {
        None,
        Goken,
        Setsugekka,
        TendoGoken,
        TendoSetsugekka
    }

    private enum SharifBanaPlan
    {
        None,
        Natural27To26,
        Meikyo24To18,
        LateKaeshiAfterBana,
        EarlyKaeshiBeforeBana
    }

    private enum SharifBurstEntryPlan
    {
        None,
        Dot24To22,
        Dot20To18,
        Dot16To14
    }

    public byte Kenki;
    public byte Meditation;
    public SenFlags Sen;
    public (float Left, IaiRepeat Action) Tsubame;

    public bool OgiRepeat;

    public float DamageUpLeft; // damage buff, max 40s
    public float HasteLeft; // haste buff, max 40s
    public (float Left, int Stacks) Meikyo; // max 20s/3
    public float OgiLeft; // max 30s
    public float TsubameLeft; // max 30s
    public float EnhancedEnpi; // max 15s
    public float Zanshin; // max 30s
    public float Tendo; // max 30s

    public int NumAOECircleTargets; // 5y circle around self, but if fuko isn't unlocked, then...
    public int NumAOETargets; // 8y/120deg cone if we don't have fuko
    public int NumTenkaTargets; // 8y circle instead of 5
    public int NumLineTargets; // shoha+guren
    public int NumOgiTargets; // 8y/120deg cone

    public AID AOEStarter => Unlocked(AID.Fuko) ? AID.Fuko : AID.Fuga;
    public AID STStarter => Unlocked(AID.Gyofu) ? AID.Gyofu : AID.Hakaze;

    private Enemy? BestAOETarget; // null if fuko is unlocked since it's self-targeted
    private Enemy? BestLineTarget;
    private Enemy? BestOgiTarget;
    private Enemy? BestDotTarget;
    private Enemy? BestRangedTarget;
    private Enemy? BestIaijutsuTarget;

    private float TargetDotLeft;

    protected override float GetCastTime(AID aid)
    {
        var c = base.GetCastTime(aid);
        return c > 0 && Unlocked(TraitID.EnhancedIaijutsu) ? 1.3f : c;
    }

    private int NumStickers => (Ice ? 1 : 0) + (Moon ? 1 : 0) + (Flower ? 1 : 0);

    private bool Ice => (Sen & SenFlags.Setsu) != 0;
    private bool Moon => (Sen & SenFlags.Getsu) != 0;
    private bool Flower => (Sen & SenFlags.Ka) != 0;
    private const float BlinkRecognitionWindow = 1.0f;

    // derived from the world-state cooldown snapshot (same source as ReadyIn/MaxChargesIn) so it stays consistent with replays and the rest of the module
    private int MeikyoCharges
    {
        get
        {
            if (!Unlocked(AID.MeikyoShisui))
                return 0;

            var def = ActionDefinitions.Instance.Spell(AID.MeikyoShisui)!;
            var maxCharges = def.MaxChargesAtLevel(Player.Level);
            if (def.MainCooldownGroup < 0)
                return maxCharges;

            var cd = World.Client.Cooldowns[def.MainCooldownGroup];
            if (cd.Total <= 0)
                return maxCharges;

            // total is always cd * max-at-cap; elapsed counts up from 0 when the first charge is spent (see ActionDefinition.MaxChargesAtCap)
            var singleChargeCD = cd.Total / def.MaxChargesAtCap();
            var chargeCapIn = MathF.Max(0, maxCharges * singleChargeCD - cd.Elapsed);
            return Math.Clamp(maxCharges - (int)MathF.Ceiling(chargeCapIn / singleChargeCD), 0, maxCharges);
        }
    }

    // fugetsu needs to cover end of iaijutsu cast
    private bool HaveDmg => DamageUpLeft > GCD + GetCastTime(AID.Higanbana);
    private bool HaveDmgForOGCD => DamageUpLeft > AnimLock;
    // haste doesn't really, it takes effect at start of GCD
    private bool HaveHaste => HasteLeft > GCD;
    private bool EnhancedEnpiReady => EnhancedEnpi > GCD || LastActionUsedRecently(AID.HissatsuYaten, BlinkRecognitionWindow);

    private bool LastActionUsedRecently(AID aid, float maxAge)
    {
        if (Manager.LastCast.Data is not { } data)
            return false;

        var age = (float)(World.CurrentTime - Manager.LastCast.Time).TotalSeconds;
        return age >= 0 && age <= maxAge && data.IsSpell(aid);
    }

    private bool RecentBlinkActionUsed()
        => LastActionUsedRecently(AID.HissatsuGyoten, BlinkRecognitionWindow) || LastActionUsedRecently(AID.HissatsuYaten, BlinkRecognitionWindow);

    private DateTime _lastBlinkCastTime;
    private int _kenkiAtBlinkCast = -1;

    // gyoten/yaten cost 10 kenki, but the gauge only reflects it once the server acks the cast; compensate until the gauge update is observed
    private int KenkiAfterRecentBlinkAction()
    {
        if (!RecentBlinkActionUsed())
        {
            _kenkiAtBlinkCast = -1;
            return Kenki;
        }

        var castTime = Manager.LastCast.Time;
        if (castTime != _lastBlinkCastTime)
        {
            _lastBlinkCastTime = castTime;
            _kenkiAtBlinkCast = Kenki;
        }

        return Kenki == _kenkiAtBlinkCast ? Math.Max(0, Kenki - 10) : Kenki;
    }

    public enum GCDPriority
    {
        None = 0,
        Enpi = 50,
        Standard = 100,
        Combo = 150,
        ComboEnd = 200,
        Hagakure = 650,
        Iaijutsu = 700,
        Tsubame = 750,
        Ogi1 = 800,
        PreDotRefresh = 830,
        Ogi2 = 850,
        Higanbana = 900
    }

    // only the values consumed by the iaijutsu/kenki comparisons below are kept
    private static class Potency
    {
        public const float Enpi = 100;

        public const float Fuga = 90;
        public const float Fuko = 100;
        public const float MangetsuCombo = 120;
        public const float OkaCombo = 120;

        public const float HiganbanaInitial = 200;
        public const float HiganbanaDotTick = 50;
        public const float HiganbanaDuration = 60.0f;
        public const float DotTickInterval = 3.0f;

        public const float TenkaGoken = 300;
        public const float MidareSetsugekka = 680;
        public const float KaeshiGoken = 300;
        public const float KaeshiSetsugekka = 680;
        public const float TendoGoken = 410;
        public const float TendoSetsugekka = 1100;
        public const float TendoKaeshiGoken = 410;
        public const float TendoKaeshiSetsugekka = 1100;

        public const float OgiNamikiri = 1000;
        public const float KaeshiNamikiri = 1000;
        public const float Zanshin = 940;

        public const float HissatsuShinten = 250;
        public const float HissatsuKyuten = 100;
        public const float HissatsuGuren = 400;
        public const float HissatsuSenei = 800;
        public const float Shoha = 640;

        private static float FullAOE(float potency, int targets) => targets <= 0 ? 0 : potency * targets;

        private static float FalloffAOE(float primaryPotency, float falloffMultiplier, int targets)
            => targets <= 0 ? 0 : primaryPotency + primaryPotency * falloffMultiplier * (targets - 1);

        public static float ExpectedHiganbanaPotency(float expectedUptimeSeconds)
        {
            if (expectedUptimeSeconds <= 0)
                return 0;

            var uptime = Math.Clamp(expectedUptimeSeconds, 0, HiganbanaDuration);
            var tickCount = MathF.Floor(uptime / DotTickInterval);
            return HiganbanaInitial + HiganbanaDotTick * tickCount;
        }

        public static float ExpectedAOEPotency(AID aid, int targets) => aid switch
        {
            AID.Fuga => FullAOE(Fuga, targets),
            AID.Fuko => FullAOE(Fuko, targets),
            AID.Mangetsu => FullAOE(MangetsuCombo, targets),
            AID.Oka => FullAOE(OkaCombo, targets),
            AID.TenkaGoken => FullAOE(TenkaGoken, targets),
            AID.KaeshiGoken => FullAOE(KaeshiGoken, targets),
            AID.TendoGoken => FullAOE(TendoGoken, targets),
            AID.TendoKaeshiGoken => FullAOE(TendoKaeshiGoken, targets),
            AID.OgiNamikiri => FalloffAOE(OgiNamikiri, 0.6f, targets),
            AID.KaeshiNamikiri => FalloffAOE(KaeshiNamikiri, 0.6f, targets),
            AID.Zanshin => FalloffAOE(Zanshin, 0.6f, targets),
            AID.HissatsuKyuten => FullAOE(HissatsuKyuten, targets),
            AID.HissatsuGuren => FullAOE(HissatsuGuren, targets),
            AID.Shoha => FalloffAOE(Shoha, 0.6f, targets),
            _ => 0
        };

        public static float ExpectedSingleTargetPotency(AID aid) => aid switch
        {
            AID.Higanbana => ExpectedHiganbanaPotency(HiganbanaDuration),
            AID.MidareSetsugekka => MidareSetsugekka,
            AID.KaeshiSetsugekka => KaeshiSetsugekka,
            AID.TendoSetsugekka => TendoSetsugekka,
            AID.TendoKaeshiSetsugekka => TendoKaeshiSetsugekka,
            AID.OgiNamikiri => OgiNamikiri,
            AID.KaeshiNamikiri => KaeshiNamikiri,
            AID.Zanshin => Zanshin,
            AID.HissatsuShinten => HissatsuShinten,
            AID.HissatsuSenei => HissatsuSenei,
            AID.Shoha => Shoha,
            AID.Enpi => Enpi,
            _ => 0
        };
    }

    private (float Left, IaiRepeat Action) GetTsubameAction()
    {
        var goken = StatusLeft(SID.KaeshiGoken);
        if (goken > 0)
            return (goken, IaiRepeat.Goken);
        var sets = StatusLeft(SID.KaeshiSetsugekka);
        if (sets > 0)
            return (sets, IaiRepeat.Setsugekka);
        var tgoken = StatusLeft(SID.TendoKaeshiGoken);
        if (tgoken > 0)
            return (tgoken, IaiRepeat.TendoGoken);
        var tsets = StatusLeft(SID.TendoKaeshiSetsugekka);
        if (tsets > 0)
            return (tsets, IaiRepeat.TendoSetsugekka);
        return (0, IaiRepeat.None);
    }

    // Per-category action locks (ActionLocks): Pacification refuses weaponskills, Silence spells, Amnesia abilities, a stun-type status
    // everything. The client refuses after the queue has picked, so a locked candidate at the top of the queue is resubmitted every
    // frame while what is below it never runs. Every push goes through PushAction -> CanUse, so filtering there keeps the queue to
    // what the client accepts and the rotation falls back on its own (weaponskills under Amnesia, abilities under Pacification).
    protected override bool CanUse(AID action) => !IsActionLocked(action) && !DelaysWeaponskillResume(action);

    // TODO: fix GCD priorities - use kaeshi as fallback action (during forced movement, etc)
    // use kaeshi goken asap in aoe?
    public override void Exec(in Strategy strategy, Enemy? primaryTarget)
    {
        UpdateMechanicForecast(strategy.MechanicHints.Value);
        if (primaryTarget != null && (!primaryTarget.Actor.IsTargetable || primaryTarget.Actor.IsDead))
            primaryTarget = null;

        SelectPrimaryTarget(strategy, ref primaryTarget, range: 3);
        var rangedTarget = primaryTarget;
        if (Player.DistanceToHitbox(primaryTarget) > 3)
            primaryTarget = null;

        SelectPrimaryTarget(strategy, ref rangedTarget, range: 15);
        if (rangedTarget == null || Player.DistanceToHitbox(rangedTarget) > 15)
            rangedTarget = null;

        var gauge = World.Client.GetGauge<SamuraiGauge>();
        Tsubame = GetTsubameAction();
        // the opener's Tendo iaijutsu has been cast: Senei is no longer held for it (its Kaeshi can come out before Senei's turn)
        if (!Player.InCombat)
            _openerTendoCast = false;
        else if (Tsubame.Action is IaiRepeat.TendoSetsugekka or IaiRepeat.TendoGoken)
            _openerTendoCast = true;
        OgiRepeat = gauge.Kaeshi == KaeshiAction.Namikiri;
        Kenki = gauge.Kenki;
        Meditation = gauge.MeditationStacks;
        Sen = gauge.SenFlags;

        DamageUpLeft = Status(SID.Fugetsu, 40).Left;
        HasteLeft = Status(SID.Fuka, 40).Left;
        Meikyo = Status(SID.MeikyoShisui);
        OgiLeft = StatusLeft(SID.OgiNamikiriReady);
        EnhancedEnpi = StatusLeft(SID.EnhancedEnpi);
        Zanshin = StatusLeft(SID.ZanshinReady);
        Tendo = StatusLeft(SID.Tendo);

        // enpi and the single-target iaijutsu (midare/tendo setsugekka, kaeshi) must not be redirected to an add by the splash heuristic
        BestRangedTarget = rangedTarget;
        BestIaijutsuTarget = SelectIaijutsuTarget(strategy, primaryTarget, rangedTarget);
        var ogiTargetSeed = primaryTarget ?? BestRangedTarget;
        (BestOgiTarget, NumOgiTargets) = SelectTarget(strategy, ogiTargetSeed, 8, InConeAOECheck);

        NumAOECircleTargets = NumMeleeAOETargets(strategy);
        if (Unlocked(AID.Fuko))
            (BestAOETarget, NumAOETargets) = (null, NumAOECircleTargets);
        else
            (BestAOETarget, NumAOETargets) = (BestOgiTarget, NumOgiTargets);

        NumTenkaTargets = NumNearbyTargets(strategy, 8);
        (BestLineTarget, NumLineTargets) = SelectTarget(strategy, primaryTarget ?? BestRangedTarget, 10, InLineAOECheck);

        SampleEnemyHP();
        (BestDotTarget, TargetDotLeft) = SelectBestHiganbanaTarget(strategy, primaryTarget);

        var opener = strategy.Opener.Value;

        var meikyoCutoff = opener.EarlyBana() ? 11 : 14;

        if (CountdownRemaining > 0)
        {
            if (strategy.Meikyo.Value != MeikyoStrategy.Delay && Unlocked(AID.MeikyoShisui) && Meikyo.Left == 0 && CountdownRemaining < meikyoCutoff)
                PushOGCD(AID.MeikyoShisui, Player);

            if (Meikyo.Left > CountdownRemaining && CountdownRemaining < 0.76f)
            {
                var prepullAction = opener.EarlyKasha() && Unlocked(AID.Kasha) ? AID.Kasha : Unlocked(AID.Gekko) ? AID.Gekko : AID.Hakaze;
                if (Unlocked(prepullAction))
                    PushGCD(prepullAction, primaryTarget, GCDPriority.ComboEnd);
            }

            return;
        }

        var emergencyMeikyoQueued = EmergencyMeikyo(strategy, primaryTarget);
        UseTsubame(strategy, BestIaijutsuTarget);
        var preserveKaeshiNamikiri = ShouldPreserveKaeshiNamikiri(strategy);

        // queued above every other ogcd (Low + 1500 lands in the "can't be delayed" band); iaijutsu are already held while this is planned, so the rest of the frame can proceed normally
        // Hagakure turns each Sen into 10 Kenki and can overcap it by up to 30. Spending a Shinten first was measured (2026-09-26)
        // and lost: it removed up to 16% of the harness's kenki overcap but delayed the recovery, and full-length fights came out
        // -0.01% (-0.02% when the delay was limited to GCDs with room for both weaves). The recovery keeps the priority.
        if (ShouldUseHagakureForBurstRecovery(strategy, primaryTarget) && CanUseOGCD(AID.Hagakure))
            PushOGCD(AID.Hagakure, Player, 1500);

        UseIaijutsu(strategy, BestIaijutsuTarget);

        var zeroSecondOpenerBurst = InZeroSecondOpenerBurst(strategy);
        var useOgiBeforeDowntime = ShouldUseOgiNamikiriBeforeDowntime();
        var ogiExpiresSoon = !CanFitGCD(OgiLeft, 1);
        var allowOgiWithoutDmg = ogiExpiresSoon || useOgiBeforeDowntime;
        var allowOgiWithoutHaste = ogiExpiresSoon || useOgiBeforeDowntime || PlannedTwoMinuteBurstActive(strategy);

        if (Unlocked(AID.OgiNamikiri)
            && !OgiRepeat
            && OgiLeft > GCD
            && BestOgiTarget != null
            && (!OgiWaitsForHiganbana(strategy) || zeroSecondOpenerBurst || useOgiBeforeDowntime || ogiExpiresSoon)
            && (HaveDmg || allowOgiWithoutDmg)
            && (HaveHaste || allowOgiWithoutHaste)
            && !ShouldDelayOgiForStandardOpener(strategy)
            && ShouldUseOgiNamikiriNow(strategy))
        {
            // technically the remaining duration we need is ((1 + stacks) * GCD) + (application delay for next GCD) but that's at the mercy of network latency
            if (Meikyo.Left == 0 || CanFitGCD(Meikyo.Left, 2 + Meikyo.Stacks))
                PushGCD(AID.OgiNamikiri, BestOgiTarget, GCDPriority.Ogi1, setRotation: NumOgiTargets > 1);
        }

        if (!preserveKaeshiNamikiri && Meikyo.Left > GCD && Unlocked(AID.MeikyoShisui))
        {
            var act = GetMeikyoAction(strategy, primaryTarget);
            var target = ShouldUseAOERotation() ? null : primaryTarget;
            if (act != AID.None && Unlocked(act) && (target != null || ShouldUseAOERotation()))
                PushGCD(act, target, GCDPriority.Standard);
        }

        if (!preserveKaeshiNamikiri && ComboLastMove == AOEStarter && ShouldUseAOERotation())
        {
            var act = GetAOEComboEndAction();
            if (act != AID.None && Unlocked(act) && ShouldAllowDuplicateSenForBuffRefresh(act) && !ShouldSuppressSenGeneratingComboForHeldTsubame(strategy, primaryTarget, act))
                PushGCD(act, Player, GCDPriority.Standard);
        }

        var comboEndPrio = ShouldPrepareHiganbanaRefresh() ? GCDPriority.PreDotRefresh : GCDPriority.Standard;

        if (!preserveKaeshiNamikiri && primaryTarget != null && ComboLastMove == AID.Jinpu && Unlocked(AID.Gekko) && ShouldAllowDuplicateSenForBuffRefresh(AID.Gekko) && !ShouldSuppressSenGeneratingComboForHeldTsubame(strategy, primaryTarget, AID.Gekko))
            PushGCD(AID.Gekko, primaryTarget, comboEndPrio);
        if (!preserveKaeshiNamikiri && primaryTarget != null && ComboLastMove == AID.Shifu && Unlocked(AID.Kasha) && ShouldAllowDuplicateSenForBuffRefresh(AID.Kasha) && !ShouldSuppressSenGeneratingComboForHeldTsubame(strategy, primaryTarget, AID.Kasha))
            PushGCD(AID.Kasha, primaryTarget, comboEndPrio);

        if (!preserveKaeshiNamikiri && primaryTarget != null && ComboLastMove == STStarter)
        {
            var act = GetHakazeComboAction(strategy, primaryTarget);
            if (act != AID.None && Unlocked(act) && ShouldAllowDuplicateSenForBuffRefresh(act) && !ShouldSuppressSenGeneratingComboForHeldTsubame(strategy, primaryTarget, act))
            {
                var actPrio = act == AID.Yukikaze && NumStickers != 3 ? comboEndPrio : GCDPriority.Standard;
                PushGCD(act, primaryTarget, actPrio);
            }
        }

        // note that this is intentionally checking for number of "nearby" targets even if our AOE starter is a cone AOE
        var suppressHeldTsubameFallback = ShouldSuppressFallbackForHeldTsubame(strategy, primaryTarget);
        var heldTsubameEnpiTarget = ResolveTargetOverride(strategy.Enpi) ?? BestRangedTarget;
        var enpiprio = strategy.Enpi.Value switch
        {
            EnpiStrategy.Enhanced => EnhancedEnpiReady ? GCDPriority.Enpi : GCDPriority.None,
            EnpiStrategy.Ranged => GCDPriority.Enpi,
            _ => GCDPriority.None,
        };

        if (!preserveKaeshiNamikiri && Unlocked(AID.Enpi) && ShouldUseEnpiForHeldTsubameFallback(strategy, primaryTarget) && heldTsubameEnpiTarget != null)
            PushGCD(AID.Enpi, heldTsubameEnpiTarget, GCDPriority.Standard);

        if (!preserveKaeshiNamikiri && Unlocked(AID.Enpi) && primaryTarget == null && BestRangedTarget != null && enpiprio != GCDPriority.None)
            PushGCD(AID.Enpi, ResolveTargetOverride(strategy.Enpi) ?? BestRangedTarget, GCDPriority.Standard);

        if (!preserveKaeshiNamikiri && !suppressHeldTsubameFallback)
        {
            if (ShouldUseAOERotation() && Unlocked(AOEStarter))
                PushGCD(AOEStarter, BestAOETarget, GCDPriority.Standard);
            else if (primaryTarget != null && Unlocked(STStarter))
                PushGCD(STStarter, primaryTarget, GCDPriority.Standard);
        }

        if (!preserveKaeshiNamikiri && Unlocked(AID.Enpi))
            PushGCD(AID.Enpi, ResolveTargetOverride(strategy.Enpi) ?? primaryTarget ?? BestRangedTarget, enpiprio);

        // out of melee with nothing else in range, the GCD would otherwise sit idle; Enpi breaks the combo, so only when there is none to lose
        if (!preserveKaeshiNamikiri && Unlocked(AID.Enpi) && strategy.Enpi.Value == EnpiStrategy.Enhanced && OutOfMeleeWithRangedTarget(primaryTarget) && !ComboInProgress() && ForcedOutOfMelee())
            PushGCD(AID.Enpi, ResolveTargetOverride(strategy.Enpi) ?? BestRangedTarget, GCDPriority.Enpi);

        if (ShouldMeditateNow(strategy, primaryTarget))
            PushGCD(AID.Meditate, Player, GCDPriority.Enpi);

        var pos = GetNextPositional(strategy);
        UpdatePositionals(primaryTarget, ref pos);

        OGCD(strategy, primaryTarget, emergencyMeikyoQueued);

        GoalZoneCombined(strategy, 3, Hints.GoalAOECircle(NumStickers == 2 ? 8 : 5), AOEStarter, 3, 20);
    }

    // NumAOETargets is already the circle count when fuko is unlocked and the cone count otherwise
    private bool ShouldUseAOERotation()
        => NumAOETargets > 2;

    // single-target iaijutsu target: the actual primary target, falling back to the player's ranged target (or, for automatic targeting, the nearest enemy) within iaijutsu range
    private Enemy? SelectIaijutsuTarget(in Strategy strategy, Enemy? primaryTarget, Enemy? rangedTarget)
    {
        if (primaryTarget != null)
            return primaryTarget;

        if (rangedTarget != null && Player.DistanceToHitbox(rangedTarget.Actor) <= 6)
            return rangedTarget;

        if (strategy.Targeting.Value is not (Targeting.Auto or Targeting.AutoTryPri))
            return null;

        Enemy? nearest = null;
        var nearestDistance = float.MaxValue;
        foreach (var enemy in Hints.PriorityTargetsSpan)
        {
            var distance = Player.DistanceToHitbox(enemy.Actor);
            if (distance <= 6 && distance < nearestDistance)
            {
                nearest = enemy;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    private bool ShouldUseTrueNorthNow(in Strategy strategy, Enemy? primaryTarget)
    {
        if (strategy.TrueNorth.Value != TrueNorthStrategy.Auto)
            return false;

        if (!CanUseOGCD(AID.TrueNorth) || TrueNorthLeft > 0)
            return false;

        if (primaryTarget == null || primaryTarget.Actor.Omnidirectional)
            return false;

        if (!NextPositionalImminent || NextPositionalCorrect)
            return false;

        return GCD <= 0.72f;
    }

    private bool BurstPrepActive(in Strategy strategy)
        => PlannedOneMinuteBurstActive(strategy) || PlannedOneMinuteBurstInWithExternal() <= 20;

    private bool UseGCD208Route(in Strategy strategy)
        => strategy.GCDRoute.Value == GCDRouteStrategy.GCD208;

    private bool UseGCD214Route(in Strategy strategy)
        => strategy.GCDRoute.Value == GCDRouteStrategy.GCD214;

    private float SelectedRouteGCDLength(in Strategy strategy)
        => UseGCD208Route(strategy) ? 2.08f : 2.14f;

    private float EffectiveAttackGCDLength()
        => AttackGCDLength > 0 ? AttackGCDLength : 2.5f;

    private bool UseFast208Route(in Strategy strategy)
        => strategy.Opener.Value == OpenerStrategy.Standard && !ShouldUseAOERotation() && UseGCD208Route(strategy);

    private bool UseSharif214Route(in Strategy strategy)
        => strategy.Opener.Value == OpenerStrategy.Standard && !ShouldUseAOERotation() && UseGCD214Route(strategy);

    private bool InSharif214OddMinute(in Strategy strategy)
        => UseSharif214Route(strategy) && !PlannedTwoMinuteBurstActive(strategy) && (PlannedOneMinuteBurstActive(strategy) || PlannedOneMinuteBurstInWithExternal() <= 30);

    private bool InSharif214EvenMinute(in Strategy strategy)
        => UseSharif214Route(strategy) && (PlannedTwoMinuteBurstActive(strategy) || PlannedTwoMinuteBurstInWithExternal() <= 30);

    private float SharifBanaActionTime(in Strategy strategy)
        => GetCastTime(AID.Higanbana) + SelectedRouteGCDLength(strategy);

    private bool SharifNeedBanaBeforeKaeshi(in Strategy strategy)
        => TargetDotLeft <= SharifBanaActionTime(strategy) + HiganbanaRefreshLead();

    private bool SharifCanDelayKaeshiForBana()
        => Tsubame.Left > GCD && CanFitGCD(Tsubame.Left, 2);

    private SharifBanaPlan GetSharifHiganbanaPlan(in Strategy strategy)
    {
        if (!UseSharif214Route(strategy) || strategy.Higanbana.Value == BanaStrategy.Delay || BestDotTarget == null)
            return SharifBanaPlan.None;

        if (ShouldRefreshHiganbanaNow())
            return SharifBanaPlan.LateKaeshiAfterBana;

        if (TargetDotLeft >= 26 && TargetDotLeft <= 27)
            return SharifBanaPlan.Natural27To26;

        if (TargetDotLeft >= 18 && TargetDotLeft <= 24)
            return SharifBanaPlan.Meikyo24To18;

        if (TargetDotLeft <= SharifBanaActionTime(strategy) + SelectedRouteGCDLength(strategy))
            return SharifBanaPlan.LateKaeshiAfterBana;

        if (TargetDotLeft <= SharifBanaActionTime(strategy) + SelectedRouteGCDLength(strategy) * 2)
            return SharifBanaPlan.EarlyKaeshiBeforeBana;

        return SharifBanaPlan.None;
    }

    private SharifBurstEntryPlan GetSharifBurstEntryPlan(in Strategy strategy)
    {
        if (!InSharif214EvenMinute(strategy) || strategy.Higanbana.Value == BanaStrategy.Delay || BestDotTarget == null)
            return SharifBurstEntryPlan.None;

        return TargetDotLeft switch
        {
            >= 22 and <= 24 => SharifBurstEntryPlan.Dot24To22,
            >= 18 and < 22 => SharifBurstEntryPlan.Dot20To18,
            >= 14 and < 18 => SharifBurstEntryPlan.Dot16To14,
            _ => SharifBurstEntryPlan.None
        };
    }

    private bool InFast208Opener(in Strategy strategy)
        => UseFast208Route(strategy) && CombatTimer < 35;

    private bool InFast208OddBurst(in Strategy strategy)
        => UseFast208Route(strategy) && !InFast208Opener(strategy) && PlannedOneMinuteBurstActive(strategy) && !PlannedTwoMinuteBurstActive(strategy);

    private bool InFast208EvenBurst(in Strategy strategy)
        => UseFast208Route(strategy) && !InFast208Opener(strategy) && PlannedTwoMinuteBurstActive(strategy);

    private bool InFast208Burst(in Strategy strategy)
        => InFast208OddBurst(strategy) || InFast208EvenBurst(strategy);

    private bool Fast208BurstEntryReady(in Strategy strategy)
        => UseFast208Route(strategy)
            && !InFast208Opener(strategy)
            && InFast208Burst(strategy)
            && Tsubame.Action == IaiRepeat.Setsugekka
            && Tsubame.Left > GCD
            && CanFitGCD(Tsubame.Left, 1)
            && NumStickers == 3
            && Tendo <= GCD
            && Meikyo.Left <= GCD
            && MeikyoCharges > 0
            && Unlocked(AID.MeikyoShisui);

    private bool ShouldPreserveMeikyoForFast208Burst(in Strategy strategy)
    {
        if (!UseFast208Route(strategy) || InFast208Opener(strategy) || !Unlocked(AID.MeikyoShisui))
            return false;

        var burstIn = PlannedOneMinuteBurstInWithExternal();
        return burstIn > GCD
            && burstIn <= 8
            && Tsubame.Action == IaiRepeat.Setsugekka
            && Tsubame.Left > burstIn
            && CanFitGCD(Tsubame.Left - burstIn, 1)
            && NumStickers == 3
            && Tendo <= GCD
            && Meikyo.Left <= GCD
            && MeikyoCharges <= 1
            && !CanWeave(MaxChargesIn(AID.MeikyoShisui), 0.6f);
    }

    private float BurstWindowEndIn(in Strategy strategy)
    {
        if (InZeroSecondOpenerBurst(strategy))
            return MathF.Max(GCD, 20 - CombatTimer);

        if (RaidBuffsLeft > GCD)
            return RaidBuffsLeft;

        var burstIn = PlannedOneMinuteBurstInWithExternal();
        return burstIn == 0 ? PlannedOneMinuteBurstLeft() : burstIn + 20;
    }

    private int BurstPrepGCDs(in Strategy strategy)
        => Math.Clamp((int)MathF.Ceiling(BurstWindowEndIn(strategy) / SelectedRouteGCDLength(strategy)), 2, 16);

    private int DesiredBurstSenCount()
        => ShouldUseAOERotation() ? 2 : 3;

    private bool HasDesiredBurstSen()
        => NumStickers >= DesiredBurstSenCount();

    private bool ShouldPrepareSenForBurst(in Strategy strategy)
        => BurstPrepActive(strategy) && !HasDesiredBurstSen();

    private bool ShouldPrepareTendoForBurst(in Strategy strategy)
    {
        if (!Unlocked(AID.MeikyoShisui) || Tendo > GCD)
            return false;

        if (InZeroSecondOpenerBurst(strategy))
            return true;

        var oneMinuteBurstIn = PlannedOneMinuteBurstInWithExternal();
        return oneMinuteBurstIn > GCD && oneMinuteBurstIn <= 8;
    }

    private bool ShouldPrepareOgiForBurst(in Strategy strategy)
    {
        if (!Unlocked(AID.Ikishoten))
            return false;

        if (OgiLeft > GCD || OgiRepeat)
            return false;

        if (InZeroSecondOpenerBurst(strategy))
            return true;

        var twoMinuteBurstIn = PlannedTwoMinuteBurstInWithExternal();
        return twoMinuteBurstIn > GCD && twoMinuteBurstIn <= 20;
    }

    private bool ShouldUseHeldKaeshiSetsugekkaForSharifBurstNow(in Strategy strategy)
        => UseSharif214Route(strategy)
            && Tsubame.Action == IaiRepeat.Setsugekka
            && PlannedOneMinuteBurstActive(strategy)
            && !ShouldDelayHeldKaeshiUntilAfterHiganbana(strategy);

    private bool ShouldHoldKaeshiSetsugekkaForSharifBurst(in Strategy strategy)
    {
        if (!UseSharif214Route(strategy) || Tsubame.Action != IaiRepeat.Setsugekka)
            return false;

        if (ShouldUseHeldKaeshiSetsugekkaForSharifBurstNow(strategy))
            return false;

        if (ShouldRefreshHiganbanaNow())
            return false;

        if (!CanFitGCD(Tsubame.Left, 1))
            return false;

        if (HasKnownDowntime() && !CanFitGCD(EffectiveDowntimeIn, 1))
            return false;

        var burstIn = PlannedOneMinuteBurstInWithExternal();
        return burstIn > GCD && burstIn <= 30 && CanFitGCD(Tsubame.Left - burstIn, 1);
    }

    private bool ShouldDelayHeldKaeshiUntilAfterHiganbana(in Strategy strategy)
        => UseSharif214Route(strategy)
            && Tsubame.Action is IaiRepeat.Setsugekka or IaiRepeat.TendoSetsugekka
            && BestDotTarget != null
            && strategy.Higanbana.Value != BanaStrategy.Delay
            && SharifNeedBanaBeforeKaeshi(strategy)
            && SharifCanDelayKaeshiForBana();

    private bool ShouldDelayFast208TendoKaeshiUntilAfterHiganbana(in Strategy strategy)
        => InFast208OddBurst(strategy)
            && Tsubame.Action == IaiRepeat.TendoSetsugekka
            && BestDotTarget != null
            && strategy.Higanbana.Value != BanaStrategy.Delay
            && ShouldRefreshHiganbanaNow()
            && CanFitGCD(Tsubame.Left, 2);

    // Out of combat CombatTimer counts from an unset combat start (~6e10 s), where the float remainder is noise: pre-pull frames
    // read as the start of the cycle, which is what the first frame of the pull sees.
    private float PlannedCycleElapsed => Player.InCombat ? MathF.Max(0, CombatTimer) : 0;

    private float PlannedCycleBurstIn(float cycle)
    {
        var elapsed = PlannedCycleElapsed;
        var intoCycle = elapsed % cycle;
        return intoCycle < 20 ? 0 : cycle - intoCycle;
    }

    private float PlannedCycleBurstLeft(float cycle)
    {
        var elapsed = PlannedCycleElapsed;
        var intoCycle = elapsed % cycle;
        return intoCycle < 20 ? 20 - intoCycle : 0;
    }

    private float PlannedOneMinuteBurstIn()
        => PlannedCycleBurstIn(60);

    private float PlannedOneMinuteBurstLeft()
        => PlannedCycleBurstLeft(60);

    private float PlannedTwoMinuteBurstIn()
        => PlannedCycleBurstIn(120);

    private float PlannedTwoMinuteBurstLeft()
        => PlannedCycleBurstLeft(120);

    private float PlannedOneMinuteBurstInWithExternal()
    {
        if (RaidBuffsLeft > GCD || PlannedOneMinuteBurstLeft() > GCD)
            return 0;

        return RaidBuffsIn > 0 && RaidBuffsIn < 1000 ? MathF.Min(RaidBuffsIn, PlannedOneMinuteBurstIn()) : PlannedOneMinuteBurstIn();
    }

    private float PlannedTwoMinuteBurstInWithExternal()
    {
        if (PlannedTwoMinuteBurstLeft() > GCD || RaidBuffsLeft > GCD && HasTwoMinuteBurstPayload())
            return 0;

        return RaidBuffsIn > 0 && RaidBuffsIn < 1000 && HasTwoMinuteBurstPayload() ? MathF.Min(RaidBuffsIn, PlannedTwoMinuteBurstIn()) : PlannedTwoMinuteBurstIn();
    }

    private bool PlannedOneMinuteBurstActive(in Strategy strategy)
        => InZeroSecondOpenerBurst(strategy) || RaidBuffsLeft > GCD || PlannedOneMinuteBurstLeft() > GCD;

    private bool PlannedTwoMinuteBurstActive(in Strategy strategy)
        => InZeroSecondOpenerBurst(strategy) || PlannedTwoMinuteBurstLeft() > GCD || RaidBuffsLeft > GCD && HasTwoMinuteBurstPayload();

    private bool HasTwoMinuteBurstPayload()
        => Unlocked(AID.OgiNamikiri) && (OgiLeft > GCD || OgiRepeat)
            || Unlocked(AID.Zanshin) && Zanshin > GCD
            || Unlocked(AID.Ikishoten) && ReadyIn(AID.Ikishoten) <= 5;

    private bool ShouldUsePotionNow(in Strategy strategy)
    {
        if (strategy.Potion.Value != SamPotionStrategy.TwoMinuteBurst)
            return false;

        if (PotionLeft > AnimLock)
            return false;

        if (!Player.InCombat)
            return false;

        var zeroSecondOpenerBurst = InZeroSecondOpenerBurst(strategy);
        var damageBuffReady = HaveDmg || HaveDmgForOGCD;
        if (!damageBuffReady)
            return false;

        var ikishotenReadyOrSoon = Unlocked(AID.Ikishoten) && ReadyIn(AID.Ikishoten) <= 5;
        var ogiPrepWithIkishotenSoon = ShouldPrepareOgiForBurst(strategy) && ikishotenReadyOrSoon;
        var ogiReadyOrSoon = OgiLeft > GCD || OgiRepeat || ogiPrepWithIkishotenSoon;
        var zanshinReady = Zanshin > GCD;
        var seneiGurenReadyOrSoon = SeneiGurenReadyIn() <= 5;
        var tendoReady = Tendo > GCD;
        var tsubameReady = Tsubame.Left > GCD && Tsubame.Action != IaiRepeat.None;
        var sharifHeldKaeshiReady = ShouldUseHeldKaeshiSetsugekkaForSharifBurstNow(strategy) || ShouldHoldKaeshiSetsugekkaForSharifBurst(strategy);

        var twoMinuteBurstSignal = zeroSecondOpenerBurst
            || ogiReadyOrSoon
            || zanshinReady
            || seneiGurenReadyOrSoon
            || ikishotenReadyOrSoon
            || tendoReady
            || tsubameReady
            || sharifHeldKaeshiReady;

        return twoMinuteBurstSignal && (PlannedTwoMinuteBurstActive(strategy) || PlannedTwoMinuteBurstInWithExternal() <= 3);
    }

    private void UsePotion(in Strategy strategy)
    {
        if (ShouldUsePotionNow(strategy))
            Hints.ActionsToExecute.Push(ActionDefinitions.IDPotionStr, Player, ActionQueue.Priority.Low + 5);
    }

    private bool ActionGrantsHeldSen(AID action) => action switch
    {
        AID.Yukikaze => Ice,
        AID.Gekko or AID.Mangetsu => Moon,
        AID.Kasha or AID.Oka => Flower,
        _ => false
    };

    private bool ActionGrantsMissingSen(AID action) => action switch
    {
        AID.Yukikaze => !Ice,
        AID.Gekko or AID.Mangetsu => !Moon,
        AID.Kasha or AID.Oka => !Flower,
        _ => false
    };

    private bool ShouldAllowDuplicateSenForBuffRefresh(AID action)
    {
        if (!ActionGrantsHeldSen(action))
            return true;

        if (NumStickers == 3 && (Tsubame.Left > 0 || OgiRepeat))
            return true;

        var missingSenRouteAvailable = !Ice && Unlocked(AID.Yukikaze)
            || !Moon && (Unlocked(AID.Gekko) || Unlocked(AID.Mangetsu))
            || !Flower && (Unlocked(AID.Kasha) || Unlocked(AID.Oka));

        if (!missingSenRouteAvailable)
            return true;

        return action switch
        {
            AID.Gekko or AID.Mangetsu => !CanFitGCD(DamageUpLeft, 1),
            AID.Kasha or AID.Oka => !CanFitGCD(HasteLeft, 1),
            _ => false
        };
    }

    private Positional CurrentStablePositional(Enemy? primaryTarget)
    {
        if (TrueNorthLeft > 0 || primaryTarget == null || primaryTarget.Actor.Omnidirectional)
            return Positional.Any;

        if (primaryTarget.Actor.TargetID == Player.InstanceID && primaryTarget.Actor.CastInfo == null && !primaryTarget.Actor.IsStrikingDummy)
            return Positional.Any;

        return GetCurrentPositional(primaryTarget.Actor);
    }

    private AID SelectGekkoKashaByCurrentPosition(Enemy? primaryTarget)
    {
        if (!Unlocked(AID.Gekko) || !Unlocked(AID.Kasha) || Moon || Flower)
            return AID.None;

        return CurrentStablePositional(primaryTarget) switch
        {
            Positional.Rear => AID.Gekko,
            Positional.Flank => AID.Kasha,
            _ => AID.None
        };
    }

    private AID SelectJinpuShifuByCurrentPosition(Enemy? primaryTarget)
    {
        if (!Unlocked(AID.Jinpu) || !Unlocked(AID.Shifu) || !Unlocked(AID.Gekko) || !Unlocked(AID.Kasha) || Moon || Flower)
            return AID.None;

        return CurrentStablePositional(primaryTarget) switch
        {
            Positional.Rear => AID.Jinpu,
            Positional.Flank => AID.Shifu,
            _ => AID.None
        };
    }

    private AID GetBurstPrepHakazeComboAction(in Strategy strategy)
    {
        if (!ShouldPrepareSenForBurst(strategy) && !ShouldPrepareSenForHiganbana(strategy))
            return AID.None;

        if (!Ice && Unlocked(AID.Yukikaze))
            return AID.Yukikaze;

        if (!Flower && Unlocked(AID.Shifu))
            return AID.Shifu;

        if (!Moon && Unlocked(AID.Jinpu))
            return AID.Jinpu;

        return AID.None;
    }

    private AID GetBurstPrepMeikyoAction(in Strategy strategy)
    {
        if (!ShouldPrepareSenForBurst(strategy) && !ShouldPrepareTendoForBurst(strategy) && !ShouldPrepareSenForHiganbana(strategy))
            return AID.None;

        if (ShouldUseAOERotation())
        {
            if (ActionGrantsMissingSen(AID.Mangetsu) && Unlocked(AID.Mangetsu))
                return AID.Mangetsu;

            if (ActionGrantsMissingSen(AID.Oka) && Unlocked(AID.Oka))
                return AID.Oka;

            if (!CanFitGCD(DamageUpLeft, 1) && Unlocked(AID.Mangetsu))
                return AID.Mangetsu;

            if (!CanFitGCD(HasteLeft, 1) && Unlocked(AID.Oka))
                return AID.Oka;

            return AID.None;
        }

        if (ActionGrantsMissingSen(AID.Yukikaze) && Unlocked(AID.Yukikaze))
            return AID.Yukikaze;

        if (ActionGrantsMissingSen(AID.Kasha) && Unlocked(AID.Kasha))
            return AID.Kasha;

        if (ActionGrantsMissingSen(AID.Gekko) && Unlocked(AID.Gekko))
            return AID.Gekko;

        return AID.None;
    }

    private AID GetFast208HakazeComboAction(in Strategy strategy)
    {
        if (!UseFast208Route(strategy))
            return AID.None;

        // buff refresh takes precedence over the sen order (mirrors the meikyo counterpart); the generic refresh checks in GetHakazeComboAction run after this
        if (Unlocked(AID.Jinpu) && !CanFitGCD(DamageUpLeft, 2))
            return AID.Jinpu;

        if (Unlocked(AID.Shifu) && !CanFitGCD(HasteLeft, 2))
            return AID.Shifu;

        if (!Ice && Unlocked(AID.Yukikaze))
            return AID.Yukikaze;

        if (!Moon && Unlocked(AID.Jinpu))
            return AID.Jinpu;

        if (!Flower && Unlocked(AID.Shifu))
            return AID.Shifu;

        return AID.None;
    }

    private AID GetFast208MeikyoAction(in Strategy strategy)
    {
        if (!InFast208Opener(strategy) && !InFast208Burst(strategy))
            return AID.None;

        if (BestDotTarget != null && ShouldRefreshHiganbanaNow() && NumStickers == 0 && Unlocked(AID.Gekko) && !Moon)
            return AID.Gekko;

        if (!HaveDmg && Unlocked(AID.Gekko) && !Moon)
            return AID.Gekko;

        if (!HaveHaste && Unlocked(AID.Kasha) && !Flower)
            return AID.Kasha;

        if (!ShouldRefreshHiganbanaNow())
        {
            if (!Flower && Unlocked(AID.Kasha))
                return AID.Kasha;

            if (Flower && !Moon && Unlocked(AID.Gekko))
                return AID.Gekko;
        }

        if (Unlocked(AID.Yukikaze) && !Ice)
            return AID.Yukikaze;

        return AID.None;
    }

    private AID GetSharif214MeikyoAction(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!UseSharif214Route(strategy))
            return AID.None;

        var banaPlan = GetSharifHiganbanaPlan(strategy);
        var burstPlan = GetSharifBurstEntryPlan(strategy);
        var needsSharifPrep = banaPlan != SharifBanaPlan.None
            || burstPlan != SharifBurstEntryPlan.None
            || ShouldPrepareSenForBurst(strategy)
            || ShouldPrepareTendoForBurst(strategy)
            || ShouldPrepareSenForHiganbana(strategy);

        if (!needsSharifPrep)
            return AID.None;

        var positionalAction = SelectGekkoKashaByCurrentPosition(primaryTarget);
        if (positionalAction == AID.Gekko && ActionGrantsMissingSen(AID.Gekko) && Unlocked(AID.Gekko))
            return AID.Gekko;

        if (positionalAction == AID.Kasha && ActionGrantsMissingSen(AID.Kasha) && Unlocked(AID.Kasha))
            return AID.Kasha;

        if (ActionGrantsMissingSen(AID.Gekko) && Unlocked(AID.Gekko))
            return AID.Gekko;

        if (ActionGrantsMissingSen(AID.Kasha) && Unlocked(AID.Kasha))
            return AID.Kasha;

        if (ActionGrantsMissingSen(AID.Yukikaze) && Unlocked(AID.Yukikaze) && (ShouldPrepareTendoForBurst(strategy) || ShouldPrepareSenForBurst(strategy) || banaPlan != SharifBanaPlan.None || burstPlan != SharifBurstEntryPlan.None))
            return AID.Yukikaze;

        return AID.None;
    }

    private AID GetSharif214ComboAction(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!UseSharif214Route(strategy))
            return AID.None;

        var banaPlan = GetSharifHiganbanaPlan(strategy);
        var burstPlan = GetSharifBurstEntryPlan(strategy);
        var needsSnowForBurst = (InSharif214EvenMinute(strategy) || banaPlan is SharifBanaPlan.Meikyo24To18 or SharifBanaPlan.Natural27To26 || burstPlan != SharifBurstEntryPlan.None)
            && !Ice
            && Unlocked(AID.Yukikaze);

        if (needsSnowForBurst)
            return AID.Yukikaze;

        // no sen and the refresh is due right after the next GCD: Yukikaze makes the sen in one GCD, the positional second step would
        // need two and the refresh would land late (the same GCDs are used either way, only their order changes)
        if (!Ice && Unlocked(AID.Yukikaze) && ShouldPrepareSenForHiganbana(strategy) && HiganbanaGCDsBeforeDue() <= 1)
            return AID.Yukikaze;

        var positionalRoute = SelectJinpuShifuByCurrentPosition(primaryTarget);
        if (positionalRoute != AID.None)
            return positionalRoute;

        if (!Moon && Unlocked(AID.Jinpu))
            return AID.Jinpu;

        if (!Flower && Unlocked(AID.Shifu))
            return AID.Shifu;

        return AID.None;
    }

    private AID GetAOEComboEndAction()
    {
        var canMangetsu = Unlocked(AID.Mangetsu);
        var canOka = Unlocked(AID.Oka);
        var allowMangetsu = canMangetsu && ShouldAllowDuplicateSenForBuffRefresh(AID.Mangetsu);
        var allowOka = canOka && ShouldAllowDuplicateSenForBuffRefresh(AID.Oka);

        if (!allowMangetsu && !allowOka)
            return AID.None;

        if (!allowMangetsu)
            return AID.Oka;

        if (!allowOka)
            return AID.Mangetsu;

        if (ActionGrantsHeldSen(AID.Mangetsu) && !CanFitGCD(DamageUpLeft, 1))
            return AID.Mangetsu;

        if (ActionGrantsHeldSen(AID.Oka) && !CanFitGCD(HasteLeft, 1))
            return AID.Oka;

        if (ActionGrantsMissingSen(AID.Mangetsu) && !ActionGrantsMissingSen(AID.Oka))
            return AID.Mangetsu;

        if (!ActionGrantsMissingSen(AID.Mangetsu) && ActionGrantsMissingSen(AID.Oka))
            return AID.Oka;

        if (ActionGrantsMissingSen(AID.Mangetsu) && ActionGrantsMissingSen(AID.Oka))
            return DamageUpLeft <= HasteLeft ? AID.Mangetsu : AID.Oka;

        if (!CanFitGCD(DamageUpLeft, 1))
            return AID.Mangetsu;

        if (!CanFitGCD(HasteLeft, 1))
            return AID.Oka;

        return DamageUpLeft <= HasteLeft ? AID.Mangetsu : AID.Oka;
    }

    private AID GetHakazeComboAction(in Strategy strategy, Enemy? primaryTarget)
    {
        var burstPrep = BurstPrepActive(strategy);

        var fast208Action = GetFast208HakazeComboAction(strategy);
        if (fast208Action != AID.None && Unlocked(fast208Action))
            return fast208Action;

        if (!burstPrep)
        {
            if (Unlocked(AID.Jinpu) && !CanFitGCD(DamageUpLeft, 2))
                return AID.Jinpu;

            if (Unlocked(AID.Shifu) && !CanFitGCD(HasteLeft, 2))
                return AID.Shifu;
        }
        else
        {
            if (Unlocked(AID.Jinpu) && !CanFitGCD(DamageUpLeft, 2))
                return AID.Jinpu;

            if (Unlocked(AID.Shifu) && !CanFitGCD(HasteLeft, 2))
                return AID.Shifu;

            var sharifAction = GetSharif214ComboAction(strategy, primaryTarget);
            if (sharifAction != AID.None && Unlocked(sharifAction))
                return sharifAction;

            var burstPrepAction = GetBurstPrepHakazeComboAction(strategy);
            if (burstPrepAction != AID.None && Unlocked(burstPrepAction))
                return burstPrepAction;

            var burstPrepGCDs = BurstPrepGCDs(strategy);
            var damageNeedsRefreshForBurst = Unlocked(AID.Jinpu) && !CanFitGCD(DamageUpLeft, burstPrepGCDs);
            var hasteNeedsRefreshForBurst = Unlocked(AID.Shifu) && !CanFitGCD(HasteLeft, burstPrepGCDs);

            if (damageNeedsRefreshForBurst && hasteNeedsRefreshForBurst)
                return DamageUpLeft <= HasteLeft ? AID.Jinpu : AID.Shifu;

            if (damageNeedsRefreshForBurst)
                return AID.Jinpu;

            if (hasteNeedsRefreshForBurst)
                return AID.Shifu;
        }

        var normalSharifAction = GetSharif214ComboAction(strategy, primaryTarget);
        if (normalSharifAction != AID.None && Unlocked(normalSharifAction))
            return normalSharifAction;

        // TODO: pick a longer combo route if we need to delay at least 4 more GCDs for bana refresh
        if (Unlocked(AID.Yukikaze) && !Ice)
            return AID.Yukikaze;

        var positionalRoute = SelectJinpuShifuByCurrentPosition(primaryTarget);
        if (positionalRoute != AID.None)
            return positionalRoute;

        if (Unlocked(AID.Shifu) && !Flower)
            return AID.Shifu;

        if (Unlocked(AID.Jinpu) && !Moon)
            return AID.Jinpu;

        // fallback if we are full on sen but can't use midare bc of movement restrictions or w/e
        return Unlocked(AID.Yukikaze) && ShouldAllowDuplicateSenForBuffRefresh(AID.Yukikaze) ? AID.Yukikaze : AID.None;
    }

    private AID GetMeikyoAction(in Strategy strategy, Enemy? primaryTarget)
    {
        var opener = strategy.Opener.Value;

        bool canUseMeikyoAction(AID action)
        {
            if (!Unlocked(action))
                return false;

            if (ActionGrantsMissingSen(action))
                return true;

            return action switch
            {
                AID.Gekko or AID.Mangetsu => !CanFitGCD(DamageUpLeft, 1),
                AID.Kasha or AID.Oka => !CanFitGCD(HasteLeft, 1),
                _ => false
            };
        }

        AID unlockedAction(AID preferred, AID fallback)
            => canUseMeikyoAction(preferred) ? preferred : canUseMeikyoAction(fallback) ? fallback : AID.None;

        var fast208Action = GetFast208MeikyoAction(strategy);
        if (fast208Action != AID.None && canUseMeikyoAction(fast208Action))
            return fast208Action;

        if (CombatTimer < 10 && DamageUpLeft == 0 && HasteLeft == 0 && opener.EarlyKasha() && canUseMeikyoAction(AID.Kasha))
            return AID.Kasha;

        var sharifAction = GetSharif214MeikyoAction(strategy, primaryTarget);
        if (sharifAction != AID.None && canUseMeikyoAction(sharifAction))
            return sharifAction;

        var burstPrep = BurstPrepActive(strategy);

        if (ShouldUseAOERotation() && (Unlocked(AID.Mangetsu) || Unlocked(AID.Oka)))
        {
            // priority 0: damage buff
            if (DamageUpLeft == 0)
                return unlockedAction(AID.Mangetsu, AID.Oka);

            if (Unlocked(AID.Mangetsu) && !CanFitGCD(DamageUpLeft, 1))
                return unlockedAction(AID.Mangetsu, AID.Oka);

            if (Unlocked(AID.Oka) && !CanFitGCD(HasteLeft, 1))
                return unlockedAction(AID.Oka, AID.Mangetsu);

            if (burstPrep)
            {
                var burstPrepAction = GetBurstPrepMeikyoAction(strategy);
                if (burstPrepAction != AID.None && Unlocked(burstPrepAction))
                    return burstPrepAction;

                var burstPrepGCDs = BurstPrepGCDs(strategy);
                var damageNeedsRefreshForBurst = Unlocked(AID.Mangetsu) && !CanFitGCD(DamageUpLeft, burstPrepGCDs);
                var hasteNeedsRefreshForBurst = Unlocked(AID.Oka) && !CanFitGCD(HasteLeft, burstPrepGCDs);

                if (damageNeedsRefreshForBurst && hasteNeedsRefreshForBurst)
                    return DamageUpLeft <= HasteLeft ? unlockedAction(AID.Mangetsu, AID.Oka) : unlockedAction(AID.Oka, AID.Mangetsu);

                if (damageNeedsRefreshForBurst)
                    return unlockedAction(AID.Mangetsu, AID.Oka);

                if (hasteNeedsRefreshForBurst)
                    return unlockedAction(AID.Oka, AID.Mangetsu);
            }

            return (Moon, Flower) switch
            {
                // refresh buff running out first
                (false, false) => DamageUpLeft <= HasteLeft ? unlockedAction(AID.Mangetsu, AID.Oka) : unlockedAction(AID.Oka, AID.Mangetsu),
                (true, false) => unlockedAction(AID.Oka, AID.Mangetsu),
                _ => unlockedAction(AID.Mangetsu, AID.Oka),
            };
        }
        else
        {
            // priority 0: damage buff
            if (!CanFitGCD(DamageUpLeft, 1) && canUseMeikyoAction(AID.Gekko))
                return AID.Gekko;

            if (!CanFitGCD(HasteLeft, 1) && canUseMeikyoAction(AID.Kasha))
                return AID.Kasha;

            if (burstPrep)
            {
                var burstPrepAction = GetBurstPrepMeikyoAction(strategy);
                if (burstPrepAction != AID.None && Unlocked(burstPrepAction))
                    return burstPrepAction;

                var burstPrepGCDs = BurstPrepGCDs(strategy);
                var damageNeedsRefreshForBurst = Unlocked(AID.Gekko) && !CanFitGCD(DamageUpLeft, burstPrepGCDs);
                var hasteNeedsRefreshForBurst = Unlocked(AID.Kasha) && !CanFitGCD(HasteLeft, burstPrepGCDs);

                if (damageNeedsRefreshForBurst && hasteNeedsRefreshForBurst)
                    return DamageUpLeft <= HasteLeft ? unlockedAction(AID.Gekko, AID.Kasha) : unlockedAction(AID.Kasha, AID.Gekko);

                if (damageNeedsRefreshForBurst)
                    return unlockedAction(AID.Gekko, AID.Kasha);

                if (hasteNeedsRefreshForBurst)
                    return unlockedAction(AID.Kasha, AID.Gekko);
            }

            var positionalAction = SelectGekkoKashaByCurrentPosition(primaryTarget);
            if (positionalAction != AID.None && canUseMeikyoAction(positionalAction))
                return positionalAction;

            if (Unlocked(AID.Kasha) && ActionGrantsMissingSen(AID.Kasha))
                return AID.Kasha;

            if (Unlocked(AID.Gekko) && ActionGrantsMissingSen(AID.Gekko))
                return AID.Gekko;

            return Unlocked(AID.Yukikaze) && ActionGrantsMissingSen(AID.Yukikaze) ? AID.Yukikaze : AID.None;
        }
    }

    private void UseTsubame(in Strategy strategy, Enemy? primaryTarget)
    {
        // namikiri combo is broken by all GCDs EXCEPT for non-tsubame iaijutsu, meaning we can use e.g. ogi 1 -> bana -> ogi 2 for alignment
        // TODO rotation does not currently do this
        if (OgiRepeat)
        {
            if (Unlocked(AID.KaeshiNamikiri) && BestOgiTarget != null && !ShouldDelayNamikiriForStandardOpener(strategy) && !ShouldDelayNamikiriForFast208OpenerHiganbana(strategy) && ShouldUseKaeshiNamikiriNow(strategy))
                PushGCD(AID.KaeshiNamikiri, BestOgiTarget, GCDPriority.Ogi2, setRotation: NumOgiTargets > 1);

            return;
        }

        var (aid, target) = TsubameAction(primaryTarget, Tsubame.Action);
        if (aid == default || !Unlocked(aid))
            return;

        if (CanUseTsubameAction(aid, target) && ShouldUseTsubameNow(strategy))
            PushGCD(aid, target, GCDPriority.Tsubame);
    }

    private bool CanUseTsubameAction(AID aid, Enemy? target)
        => aid switch
        {
            AID.KaeshiGoken or AID.TendoKaeshiGoken => NumTenkaTargets > 0,
            AID.KaeshiSetsugekka or AID.TendoKaeshiSetsugekka => target != null,
            _ => false
        };

    private bool ShouldPreserveKaeshiNamikiri(in Strategy strategy)
        => OgiRepeat
            && strategy.Namikiri.Value != NamikiriStrategy.Delay
            && Unlocked(AID.KaeshiNamikiri)
            && BestOgiTarget == null
            && BestRangedTarget == null;

    private bool InZeroSecondOpenerBurst(in Strategy strategy)
        => strategy.OpenerBurst.Value == OpenerBurstStrategy.ZeroSecond && CombatTimer < 20;

    private bool BurstOGCDsAllowed(in Strategy strategy)
        => strategy.Buffs != OffensiveStrategy.Delay;

    private bool OutOfMeleeWithRangedTarget(Enemy? primaryTarget)
        => primaryTarget == null && BestRangedTarget != null;

    private bool ComboInProgress()
        => ComboLastMove is AID.Hakaze or AID.Gyofu or AID.Jinpu or AID.Shifu or AID.Fuga or AID.Fuko;

    // a mechanic keeps us out of melee (no safe spot next to the target) for long enough that idling would cost more than a weak GCD
    private bool ForcedOutOfMelee()
        => Hints.Disengage.TargetLossIn <= 0 && Hints.Disengage.TargetReturnIn >= EnpiReturnFraction * EffectiveAttackGCDLength();

    private const float EnpiReturnFraction = 0.5f;

    // nothing at all to attack: no enemy in melee, iaijutsu, ogi or enpi range, and no priority target anywhere
    private bool NothingToAttack(Enemy? primaryTarget)
        => primaryTarget == null && BestRangedTarget == null && BestIaijutsuTarget == null && BestOgiTarget == null && Hints.PriorityTargetsSpan.Length == 0;

    private bool MeditateChanneling(Enemy? primaryTarget)
        => StatusLeft(SID.Meditate) > 0 && NothingToAttack(primaryTarget);

    // Meditate channels for up to 15s, granting 10 kenki (and a meditation stack from 80) every 3s; any action or movement ends it.
    // During a downtime the GCD has nothing else to do, so a tick is free value; a known return sooner than the first tick skips it.
    private bool ShouldMeditateNow(in Strategy strategy, Enemy? primaryTarget)
    {
        var nothingToAttack = NothingToAttack(primaryTarget);
        if (!nothingToAttack)
            _nothingToAttackSince = default;
        else if (_nothingToAttackSince == default)
            _nothingToAttackSince = World.CurrentTime;

        if (!strategy.Meditate.IsEnabled() || !Unlocked(AID.Meditate) || !Player.InCombat || CountdownRemaining > 0 || IsMoving)
            return false;

        if (StatusLeft(SID.Meditate) > 0 || !nothingToAttack)
            return false;

        if (Kenki >= KenkiCap && (Meditation >= 3 || !Unlocked(AID.Shoha)))
            return false;

        // Meditate puts the GCD on recast: a return before the first tick costs GCD time. With a known return, require one tick
        // before it; otherwise wait out short target losses (jumps, brief untargetable casts) before committing.
        if (UptimeIn is float uptimeIn && uptimeIn > 0)
            return uptimeIn >= MeditateTickInterval + 0.2f;

        if (Mechanic.DowntimeNow && Mechanic.ReturnKnown)
            return Mechanic.TargetReturnIn >= MeditateTickInterval + 0.2f;

        return (World.CurrentTime - _nothingToAttackSince).TotalSeconds >= MeditateUnknownReturnGrace;
    }

    private DateTime _nothingToAttackSince;
    private const float MeditateTickInterval = 3f;
    private const float MeditateUnknownReturnGrace = 2.5f;

    // The planner's DowntimeIn only comes from a boss module's own state machine. A long loss announced by the imported-timeline follower
    // or an external source reaches the rotation through MechanicForecast instead, so the downtime rules read both (as AkechiPLD does).
    private float BaseDowntimeIn => Mechanic.Enabled && Mechanic.ReturnKnown ? Math.Min(DowntimeIn, Mechanic.TargetLossIn) : DowntimeIn;

    // FightRemaining (value-of-information experiment): two separable rules that read the shared fight-end estimate.
    // Unknown, or the uninformative "blind" placeholder (no finite bound), gives exactly the previous behaviour.
    //  1. UseFightEndDump: the end of the fight is a downtime that never returns, so everything that already dumps before a downtime
    //     (Ogi Namikiri, Zanshin, Shoha, Senei/Guren, Shinten, Ikishoten, Tsubame, Hagakure recovery) dumps before the fight end.
    //     Uses the late bound (UpperBound): "will this resource be wasted if I hold it?"
    //  2. UseFightEndMeikyo: a Meikyo Shisui charge is not held for the next planned burst when the fight (late bound) ends within
    //     FightEndMeikyoWindow and at least three GCDs remain to spend the three stacks. Skipped when a downtime is already known before
    //     that end: the estimate counts wall seconds, not seconds with a target, and the downtime rules decide then.
    private static readonly bool UseFightEndDump = true;
    private static readonly bool UseFightEndMeikyo = true;
    private const float FightEndMeikyoWindow = 45;

    private float FightEndUpper => Hints.FightRemaining is { Known: true } fr && fr.RemainingSeconds < 1e6f && fr.UpperBound < 1e6f ? fr.UpperBound : float.MaxValue;

    private float EffectiveDowntimeIn => UseFightEndDump && FightEndUpper is var end && end < float.MaxValue ? MathF.Min(BaseDowntimeIn, end) : BaseDowntimeIn;

    private bool DowntimeSoon()
        => !float.IsNaN(EffectiveDowntimeIn)
            && !float.IsInfinity(EffectiveDowntimeIn)
            && EffectiveDowntimeIn >= 0
            && EffectiveDowntimeIn != float.MaxValue
            && EffectiveDowntimeIn <= 8;

    private bool HasKnownDowntime()
        => !float.IsNaN(EffectiveDowntimeIn)
            && !float.IsInfinity(EffectiveDowntimeIn)
            && EffectiveDowntimeIn >= 0
            && EffectiveDowntimeIn != float.MaxValue;

    private bool RaidBuffBeforeDowntime()
        => HasKnownDowntime() && RaidBuffsIn > 0 && RaidBuffsIn < EffectiveDowntimeIn;

    private bool ShouldUseOgiNamikiriBeforeDowntime()
        => HasKnownDowntime() && !RaidBuffBeforeDowntime() && !CanFitGCD(EffectiveDowntimeIn, 2);

    private bool InNormalOpenerOrderWindow(in Strategy strategy)
        => CombatTimer < 25 && strategy.Opener.Value is OpenerStrategy.Standard or OpenerStrategy.KashaStandard;

    private bool ShouldPrepareSenForHiganbana(in Strategy strategy)
        => strategy.Higanbana.Value != BanaStrategy.Delay
            && Unlocked(AID.Higanbana)
            && BestDotTarget != null
            && NumStickers == 0
            && ShouldPrepareHiganbanaRefresh()
            && Potency.ExpectedHiganbanaPotency(ExpectedHiganbanaUptimeForTarget(BestDotTarget)) >= MinimumHiganbanaAutoPotency();

    private float HiganbanaRefreshLead()
        => Math.Clamp(GetCastTime(AID.Higanbana) + 0.35f, 1.5f, 2.0f);

    private bool ShouldRefreshHiganbanaNow()
        => TargetDotLeft <= HiganbanaRefreshLead() || !CanFitGCD(TargetDotLeft, 1);

    // Ogi Namikiri gives way to a Higanbana refresh only when that refresh will actually happen. On a target about to die (or before
    // a downtime) the refresh is skipped as not worth its sen, and Ogi used to wait for it until Ogi Namikiri Ready nearly ran out.
    private bool OgiWaitsForHiganbana(in Strategy strategy)
        => ShouldRefreshHiganbanaNow()
            && BestDotTarget != null
            && Unlocked(AID.Higanbana)
            && strategy.Higanbana.Value != BanaStrategy.Delay
            && (strategy.Higanbana.Value == BanaStrategy.Force || Potency.ExpectedHiganbanaPotency(ExpectedHiganbanaUptimeForTarget(BestDotTarget)) >= MinimumHiganbanaAutoPotency());

    private bool ShouldPrepareHiganbanaRefresh()
        => TargetDotLeft <= HiganbanaRefreshLead() + EffectiveAttackGCDLength() * 4;

    // --- Higanbana sen plan ---
    // GCDs that can still start before the Higanbana refresh is due (0: the refresh is the next GCD, which is what ShouldRefreshHiganbanaNow sees)
    private int HiganbanaGCDsBeforeDue()
    {
        if (BestDotTarget == null || TargetDotLeft <= HiganbanaRefreshLead())
            return 0;

        var k = 0;
        while (k < 6 && CanFitGCD(TargetDotLeft, k + 1))
            ++k;
        return k;
    }

    private bool ShouldDelayHiganbanaForRaidBuff(in Strategy strategy)
    {
        if (strategy.Higanbana.Value != BanaStrategy.Automatic)
            return false;

        if (ShouldRefreshHiganbanaNow())
            return false;

        var burstIn = PlannedOneMinuteBurstInWithExternal();
        if (UseSharif214Route(strategy) && GetSharifHiganbanaPlan(strategy) != SharifBanaPlan.None)
        {
            if (TargetDotLeft <= SharifBanaActionTime(strategy) + HiganbanaRefreshLead())
                return false;

            return burstIn > 0 && burstIn <= SelectedRouteGCDLength(strategy) * 2 && TargetDotLeft >= burstIn;
        }

        return burstIn > 0 && burstIn <= EffectiveAttackGCDLength() * 2 && TargetDotLeft >= burstIn;
    }

    private bool ShouldDelayOgiForStandardOpener(in Strategy strategy)
        => (ShouldDelayNamikiriForStandardOpener(strategy) || ShouldDelayNamikiriForFast208OpenerHiganbana(strategy))
            && CanFitGCD(OgiLeft, 1)
            && !ShouldUseOgiNamikiriBeforeDowntime();

    private bool ShouldDelayNamikiriForFast208OpenerHiganbana(in Strategy strategy)
        => InFast208Opener(strategy)
            && strategy.Higanbana.Value != BanaStrategy.Delay
            && Unlocked(AID.Higanbana)
            && BestDotTarget != null
            && (ShouldRefreshHiganbanaNow() || HiganbanaLeft(BestDotTarget.Actor) <= HiganbanaRefreshLead());

    private bool ShouldDelayNamikiriForStandardOpener(in Strategy strategy)
        => InNormalOpenerOrderWindow(strategy)
            && strategy.Higanbana.Value != BanaStrategy.Delay
            && Unlocked(AID.Higanbana)
            && BestDotTarget != null
            && ShouldRefreshHiganbanaNow()
            && (NumStickers > 0 || CombatTimer < 18);

    private bool ShouldDelaySeneiGurenForStandardOpener(in Strategy strategy)
    {
        if (!InNormalOpenerOrderWindow(strategy))
            return false;

        if (InZeroSecondOpenerBurst(strategy))
            return false;

        if (!Unlocked(AID.TendoSetsugekka) && !Unlocked(AID.TendoGoken))
            return false;

        if (ShouldUseSeneiGurenForStandardOpenerNow(strategy))
            return false;

        return true;
    }

    // Senei goes after the opener's Tendo iaijutsu. The Tendo Kaeshi usually comes out first (a 1.3s cast leaves no weave slot before it),
    // so the Tendo being pending is not enough: once it was cast, Senei is due (it used to wait until combat time 25 otherwise, which also
    // put every later Senei out of the raid buff windows)
    private bool _openerTendoCast;

    private bool ShouldUseSeneiGurenForStandardOpenerNow(in Strategy strategy)
        => InNormalOpenerOrderWindow(strategy)
            && (_openerTendoCast || Tsubame.Left > GCD && Tsubame.Action is IaiRepeat.TendoSetsugekka or IaiRepeat.TendoGoken)
            && (Unlocked(AID.HissatsuSenei) && ReadyIn(AID.HissatsuSenei) <= AnimLock
                || Unlocked(AID.HissatsuGuren) && ReadyIn(AID.HissatsuGuren) <= AnimLock);

    private bool ShouldDelayMeikyoForStandardOpener(in Strategy strategy)
        => InNormalOpenerOrderWindow(strategy)
            && Tsubame.Left > GCD
            && Tsubame.Action is IaiRepeat.TendoSetsugekka or IaiRepeat.TendoGoken;

    private bool ShouldDelayZanshinForStandardOpener(in Strategy strategy)
        => InFast208Opener(strategy)
            && strategy.Higanbana.Value != BanaStrategy.Delay
            && Unlocked(AID.Higanbana)
            && BestDotTarget != null
            && ShouldRefreshHiganbanaNow()
            && NextGCD != AID.Higanbana;

    // The opener spends the pre-pull Meikyo Shisui stacks on Gekko, Kasha and Yukikaze before the Tendo Setsugekka. On the Sharif 2.14
    // route the target has no DoT yet, so its Higanbana plan used to cut in between them: the last stack expired unused and the
    // opener fell to Tendo Goken. Both standard routes hold Higanbana out of the first five GCDs.
    private bool ShouldDelayHiganbanaForOpenerOrder(in Strategy strategy)
        => (InFast208Opener(strategy) || UseSharif214Route(strategy))
            && strategy.Higanbana.Value == BanaStrategy.Automatic
            && CombatTimer < SelectedRouteGCDLength(strategy) * 5;

    private float MinimumHiganbanaAutoPotency()
        => 680;

    private float ExpectedHiganbanaUptimeForTarget(Enemy? target)
    {
        if (target == null)
            return 0;

        if (!target.Actor.IsTargetable || target.Actor.IsDead)
            return 0;

        var uptime = MathF.Min(Potency.HiganbanaDuration, EstimatedTimeToKill(target.Actor));
        // the fight-end rule does not shorten the DoT estimate (measured: skipping Higanbana near the end does not pay), only the real downtime does
        var baseDowntime = BaseDowntimeIn;
        if (!float.IsNaN(baseDowntime) && !float.IsInfinity(baseDowntime) && baseDowntime >= 0 && baseDowntime != float.MaxValue)
            uptime = MathF.Min(uptime, baseDowntime);

        return uptime;
    }

    // Time to kill from the target's HP drop over the last few seconds (float.MaxValue while unknown, not dropping, or too short a
    // history). A dot on something about to die is worth far less than the sen it costs; the uptime above feeds the same potency
    // threshold the downtime already does.
    private const float TimeToKillWindow = 5f;
    private const float TimeToKillMinSpan = 2f;
    private const float TimeToKillMaxStepDrop = 0.3f;
    private readonly Dictionary<ulong, List<(DateTime T, uint Hp)>> _hpSamples = [];
    private DateTime _nextHpSample;

    private void SampleEnemyHP()
    {
        var now = World.CurrentTime;
        if (now < _nextHpSample)
            return;
        _nextHpSample = now.AddSeconds(0.5);

        foreach (var enemy in Hints.PriorityTargetsSpan)
        {
            var actor = enemy.Actor;
            if (actor.HPMP.MaxHP == 0)
                continue;
            if (!_hpSamples.TryGetValue(actor.InstanceID, out var samples))
                _hpSamples[actor.InstanceID] = samples = [];
            // a scripted HP change (phase set, instant kill mechanic) is not a damage rate: start the history over
            if (samples.Count > 0 && (float)samples[^1].Hp - actor.HPMP.CurHP > TimeToKillMaxStepDrop * actor.HPMP.MaxHP)
                samples.Clear();
            samples.Add((now, actor.HPMP.CurHP));
            var keepFrom = now.AddSeconds(-TimeToKillWindow);
            var stale = -1;
            for (var i = 0; i < samples.Count; ++i)
                if (samples[i].T >= keepFrom)
                {
                    stale = i;
                    break;
                }
            if (stale > 1)
                samples.RemoveRange(0, stale - 1); // keep one sample at or just beyond the window edge
        }

        if (_hpSamples.Count > 32)
            foreach (var id in _hpSamples.Where(kv => (now - kv.Value[^1].T).TotalSeconds > 10).Select(kv => kv.Key).ToList())
                _hpSamples.Remove(id);
    }

    private float EstimatedTimeToKill(Actor target)
    {
        if (!_hpSamples.TryGetValue(target.InstanceID, out var samples) || samples.Count < 2)
            return float.MaxValue;

        var first = samples[0];
        var span = (float)(World.CurrentTime - first.T).TotalSeconds;
        var dropped = (float)first.Hp - target.HPMP.CurHP;
        if (span < TimeToKillMinSpan || dropped <= 0)
            return float.MaxValue;

        return target.HPMP.CurHP / (dropped / span);
    }

    private bool CanUseIaijutsuOnTarget(Enemy? target)
        => target != null && Player.DistanceToHitbox(target.Actor) <= 6;

    private bool ShouldSelectHiganbanaTarget(float dotLeft)
        => dotLeft <= HiganbanaRefreshLead() || !CanFitGCD(dotLeft, 1);

    private (Enemy? Target, float DotLeft) SelectBestHiganbanaTarget(in Strategy strategy, Enemy? primaryTarget)
    {
        var forcedTarget = ResolveTargetOverride(strategy.Higanbana);

        if (strategy.Higanbana.Value == BanaStrategy.Delay)
            return (null, float.MaxValue);

        if (strategy.Higanbana.Value == BanaStrategy.Force)
        {
            var target = forcedTarget ?? BestIaijutsuTarget ?? primaryTarget;
            return CanUseIaijutsuOnTarget(target) ? (target, 0) : (null, float.MaxValue);
        }

        var seed = forcedTarget ?? primaryTarget;
        var (defaultTarget, defaultDotLeft) = SelectDotTarget(strategy, seed, HiganbanaLeftFunc, 2);
        if (!CanUseIaijutsuOnTarget(defaultTarget))
            (defaultTarget, defaultDotLeft) = (null, float.MaxValue);

        if (forcedTarget != null)
            return (defaultTarget, defaultDotLeft);

        // the extended scan below follows the same rules as SelectDotTarget: automatic targeting only (AutoTryPri with a player target already returned it),
        // no dots in packs of more than 2 (aoe rotation should reach tenka goken instead), and no ForbidDOTs targets
        var automaticScan = strategy.Targeting.Value == Targeting.Auto || strategy.Targeting.Value == Targeting.AutoTryPri && seed == null;
        if (!automaticScan || ShouldUseAOERotation() || Hints.CountPriorityTargets(e => !e.ForbidDOTs) > 2)
            return (defaultTarget, defaultDotLeft);

        var bestTarget = defaultTarget;
        var bestDotLeft = defaultDotLeft;
        var bestScore = float.MinValue;
        var minPotency = MinimumHiganbanaAutoPotency();

        foreach (var enemy in Hints.PotentialTargets)
        {
            if (!enemy.Actor.IsTargetable || enemy.Actor.IsDead || enemy.Priority < 0 || enemy.ForbidDOTs)
                continue;

            if (!CanUseIaijutsuOnTarget(enemy))
                continue;

            var dotLeft = HiganbanaLeft(enemy.Actor);
            if (!ShouldSelectHiganbanaTarget(dotLeft))
                continue;

            var expectedUptime = ExpectedHiganbanaUptimeForTarget(enemy);
            var expectedPotency = Potency.ExpectedHiganbanaPotency(expectedUptime);
            if (expectedPotency < minPotency)
                continue;

            var score = expectedPotency - MathF.Max(0, dotLeft) * 10;
            if (score > bestScore)
            {
                bestScore = score;
                bestTarget = enemy;
                bestDotLeft = dotLeft;
            }
        }

        return (bestTarget, bestDotLeft);
    }

    private bool ShouldUseOgiNamikiriNow(in Strategy strategy)
    {
        var zeroSecondOpenerBurst = InZeroSecondOpenerBurst(strategy);
        var twoMinuteBurstActive = PlannedTwoMinuteBurstActive(strategy);
        var twoMinuteBurstIn = PlannedTwoMinuteBurstInWithExternal();
        var expiresSoon = !CanFitGCD(OgiLeft, 1);
        var potionExpiresSoon = PotionLeft > GCD && !CanFitGCD(PotionLeft, 1);
        var beforeDowntime = ShouldUseOgiNamikiriBeforeDowntime();
        var canHoldForTwoMinute = twoMinuteBurstIn > GCD && CanFitGCD(OgiLeft - twoMinuteBurstIn, 1);

        switch (strategy.Namikiri.Value)
        {
            case NamikiriStrategy.Force:
                return true;

            case NamikiriStrategy.Delay:
                return false;

            case NamikiriStrategy.Hold:
                return zeroSecondOpenerBurst
                    || twoMinuteBurstActive
                    || expiresSoon
                    || potionExpiresSoon
                    || beforeDowntime;

            default:
                break;
        }

        return zeroSecondOpenerBurst
            || twoMinuteBurstActive
            || expiresSoon
            || potionExpiresSoon
            || beforeDowntime
            || !canHoldForTwoMinute;
    }

    // kaeshi namikiri readiness has no status or gauge timer we can read (ogi namikiri ready is consumed by the ogi cast), so it cannot be held safely
    // and is used immediately unless explicitly delayed; opener-specific ordering is handled by the ShouldDelayNamikiri* checks at the call site
    private bool ShouldUseKaeshiNamikiriNow(in Strategy strategy)
        => strategy.Namikiri.Value != NamikiriStrategy.Delay;

    private bool ShouldUseIkishotenByTiming(in Strategy strategy)
    {
        if (!Unlocked(AID.Ikishoten))
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay)
            return false;

        if (OgiLeft > GCD || OgiRepeat)
            return false;

        var zeroSecondOpenerBurst = InZeroSecondOpenerBurst(strategy);
        if (InNormalOpenerOrderWindow(strategy) && (!HaveDmg || !HaveHaste))
            return false;

        if (InFast208Opener(strategy) && HaveDmg && HaveHaste)
            return true;

        var twoMinuteBurstActive = PlannedTwoMinuteBurstActive(strategy);
        var prepForRaidBuff = ShouldPrepareOgiForBurst(strategy);
        var beforeDowntime = HasKnownDowntime()
            && EffectiveDowntimeIn <= 25
            && !RaidBuffBeforeDowntime()
            && CanFitGCD(EffectiveDowntimeIn, 2);

        return zeroSecondOpenerBurst
            || twoMinuteBurstActive
            || prepForRaidBuff
            || beforeDowntime;
    }

    private bool ShouldUseIkishotenNow(in Strategy strategy)
        => ShouldUseIkishotenNow(strategy, Kenki);

    private bool ShouldUseIkishotenNow(in Strategy strategy, int plannedKenki)
        => plannedKenki <= 50 && CanUseOGCD(AID.Ikishoten) && ShouldUseIkishotenByTiming(strategy);

    private bool CanUseOGCD(AID action)
        => Unlocked(action) && (ReadyIn(action) <= AnimLock || CanWeave(action));

    private bool ShouldUseZanshinNow(in Strategy strategy)
    {
        if (!BurstOGCDsAllowed(strategy))
            return false;

        if (!Unlocked(AID.Zanshin) || Zanshin <= 0)
            return false;

        if (ShouldDelayZanshinForStandardOpener(strategy))
            return false;

        var zeroSecondOpenerBurst = InZeroSecondOpenerBurst(strategy);
        var twoMinuteBurstActive = PlannedTwoMinuteBurstActive(strategy);
        var twoMinuteBurstIn = PlannedTwoMinuteBurstInWithExternal();
        var expiresSoon = !CanFitGCD(Zanshin, 1);
        var potionExpiresSoon = PotionLeft > GCD && !CanFitGCD(PotionLeft, 1);
        var beforeDowntime = HasKnownDowntime() && !RaidBuffBeforeDowntime() && !CanFitGCD(EffectiveDowntimeIn, 1);
        var canHoldForTwoMinute = twoMinuteBurstIn > AnimLock && CanFitGCD(Zanshin - twoMinuteBurstIn, 1);

        return zeroSecondOpenerBurst
            || twoMinuteBurstActive
            || expiresSoon
            || potionExpiresSoon
            || beforeDowntime
            || !canHoldForTwoMinute;
    }

    private float SeneiGurenReadyIn()
    {
        var seneiReadyIn = Unlocked(AID.HissatsuSenei) ? ReadyIn(AID.HissatsuSenei) : float.MaxValue;
        var gurenReadyIn = Unlocked(AID.HissatsuGuren) ? ReadyIn(AID.HissatsuGuren) : float.MaxValue;
        return MathF.Min(seneiReadyIn, gurenReadyIn);
    }

    private bool ShouldUseFast208GyotenNow(in Strategy strategy, Enemy? primaryTarget, int plannedKenki)
        => InFast208Opener(strategy)
            && primaryTarget != null
            && Unlocked(AID.HissatsuGyoten)
            && plannedKenki >= 35
            && ComboLastMove == STStarter
            && NextGCD == AID.Yukikaze
            && SeneiGurenReadyIn() >= 10
            && Zanshin <= 0
            && CanWeave(AID.HissatsuGyoten);

    private const int KenkiCap = 100;
    private const int KenkiOvercapLookaheadGCDs = 4;

    private int EstimateKenkiGain(AID action) => action switch
    {
        AID.Hakaze or AID.Gyofu or AID.Jinpu or AID.Shifu => Unlocked(TraitID.KenkiMastery) ? 5 : 0,
        AID.Fuga or AID.Fuko or AID.Enpi => Unlocked(TraitID.KenkiMastery) ? 10 : 0,
        // current job guide: yukikaze combo grants 15 kenki, gekko/kasha/mangetsu/oka combos grant 10 (all governed by kenki mastery II)
        // TODO: the trait enum in ActionQueue/Melee/SAM.cs does not document per-level values; the pre-L62 fallback of 5 is unverified
        AID.Yukikaze => Unlocked(TraitID.KenkiMastery2) ? 15 : Unlocked(TraitID.KenkiMastery) ? 5 : 0,
        AID.Gekko or AID.Kasha or AID.Mangetsu or AID.Oka => Unlocked(TraitID.KenkiMastery2) ? 10 : Unlocked(TraitID.KenkiMastery) ? 5 : 0,
        _ => 0
    };

    private bool CanPredictKenkiGain(AID action, Enemy? primaryTarget)
        => action switch
        {
            AID.Fuga or AID.Fuko or AID.Mangetsu or AID.Oka => true,
            AID.Enpi => primaryTarget != null || BestRangedTarget != null,
            AID.Hakaze or AID.Gyofu or AID.Jinpu or AID.Shifu or AID.Yukikaze or AID.Gekko or AID.Kasha => primaryTarget != null,
            _ => false
        };

    private static bool IsPredictableZeroKenkiGCD(AID action)
        => action is AID.Higanbana
            or AID.TenkaGoken
            or AID.MidareSetsugekka
            or AID.KaeshiGoken
            or AID.KaeshiSetsugekka
            or AID.TendoGoken
            or AID.TendoSetsugekka
            or AID.TendoKaeshiGoken
            or AID.TendoKaeshiSetsugekka
            or AID.OgiNamikiri
            or AID.KaeshiNamikiri;

    private static bool ConsumesPredictedSen(AID action)
        => action is AID.Higanbana
            or AID.TenkaGoken
            or AID.MidareSetsugekka
            or AID.TendoGoken
            or AID.TendoSetsugekka;

    private static void ApplyPredictedSen(AID action, ref bool ice, ref bool moon, ref bool flower)
    {
        switch (action)
        {
            case AID.Yukikaze:
                ice = true;
                break;
            case AID.Gekko:
            case AID.Mangetsu:
                moon = true;
                break;
            case AID.Kasha:
            case AID.Oka:
                flower = true;
                break;
        }
    }

    private AID PredictNextMeikyoKenkiGCD(bool ice, bool moon, bool flower, Enemy? primaryTarget)
    {
        if (ShouldUseAOERotation())
        {
            if (!moon && Unlocked(AID.Mangetsu))
                return AID.Mangetsu;

            if (!flower && Unlocked(AID.Oka))
                return AID.Oka;

            return AID.None;
        }

        if (primaryTarget == null)
            return AID.None;

        if (!ice && Unlocked(AID.Yukikaze))
            return AID.Yukikaze;

        if (!flower && Unlocked(AID.Kasha))
            return AID.Kasha;

        if (!moon && Unlocked(AID.Gekko))
            return AID.Gekko;

        return AID.None;
    }

    private AID PredictNextKenkiGCD(AID comboLastMove, bool ice, bool moon, bool flower, int meikyoStacks, Enemy? primaryTarget)
    {
        if (meikyoStacks > 0)
        {
            var meikyoAction = PredictNextMeikyoKenkiGCD(ice, moon, flower, primaryTarget);
            if (meikyoAction != AID.None)
                return meikyoAction;
        }

        if (ShouldUseAOERotation())
        {
            if (comboLastMove == AOEStarter)
            {
                if (!moon && Unlocked(AID.Mangetsu))
                    return AID.Mangetsu;

                if (!flower && Unlocked(AID.Oka))
                    return AID.Oka;

                if (Unlocked(AID.Mangetsu))
                    return AID.Mangetsu;

                if (Unlocked(AID.Oka))
                    return AID.Oka;
            }

            return Unlocked(AOEStarter) ? AOEStarter : AID.None;
        }

        if (primaryTarget == null)
            return AID.None;

        if (comboLastMove == AID.Jinpu && Unlocked(AID.Gekko))
            return AID.Gekko;

        if (comboLastMove == AID.Shifu && Unlocked(AID.Kasha))
            return AID.Kasha;

        if (comboLastMove == STStarter)
        {
            if (!ice && Unlocked(AID.Yukikaze))
                return AID.Yukikaze;

            if (!moon && Unlocked(AID.Jinpu))
                return AID.Jinpu;

            if (!flower && Unlocked(AID.Shifu))
                return AID.Shifu;

            return Unlocked(AID.Yukikaze) ? AID.Yukikaze : AID.None;
        }

        return Unlocked(STStarter) ? STStarter : AID.None;
    }

    private int ProjectKenkiAfterUpcomingGCDs(in Strategy strategy, Enemy? primaryTarget, int plannedKenki, int gcdCount)
    {
        var projectedKenki = plannedKenki;
        var predictedComboLastMove = ComboLastMove;
        var predictedIce = Ice;
        var predictedMoon = Moon;
        var predictedFlower = Flower;
        var predictedMeikyoStacks = Meikyo.Left > GCD ? Meikyo.Stacks : 0;

        for (var i = 0; i < gcdCount; ++i)
        {
            var action = i == 0 ? NextGCD : PredictNextKenkiGCD(predictedComboLastMove, predictedIce, predictedMoon, predictedFlower, predictedMeikyoStacks, primaryTarget);
            if (action == AID.None)
                break;

            var predictsKenkiGain = CanPredictKenkiGain(action, primaryTarget);
            if (!predictsKenkiGain && !IsPredictableZeroKenkiGCD(action))
                break;

            if (predictsKenkiGain)
                projectedKenki += EstimateKenkiGain(action);

            ApplyPredictedSen(action, ref predictedIce, ref predictedMoon, ref predictedFlower);
            if (ConsumesPredictedSen(action))
                predictedIce = predictedMoon = predictedFlower = false;

            if (predictedMeikyoStacks > 0 && action is AID.Yukikaze or AID.Gekko or AID.Kasha or AID.Mangetsu or AID.Oka)
                --predictedMeikyoStacks;
            predictedComboLastMove = action;
        }

        return projectedKenki;
    }

    private int AvailableKenkiAfterReserve(in Strategy strategy, int plannedKenki, bool zanshinQueued, bool seneiGurenQueued)
        => plannedKenki - ReservedKenkiForBurst(strategy, zanshinQueued, seneiGurenQueued);

    private bool WillOvercapKenkiSoon(in Strategy strategy, Enemy? primaryTarget, int plannedKenki)
        => ProjectKenkiAfterUpcomingGCDs(strategy, primaryTarget, plannedKenki, KenkiOvercapLookaheadGCDs) > KenkiCap;

    private bool ShouldSpendKenkiForOvercap(in Strategy strategy, Enemy? primaryTarget, int plannedKenki, bool zanshinQueued, bool seneiGurenQueued)
        => plannedKenki >= 25
            && WillOvercapKenkiSoon(strategy, primaryTarget, plannedKenki)
            && AvailableKenkiAfterReserve(strategy, plannedKenki, zanshinQueued, seneiGurenQueued) >= 25;

    private bool ShouldSpendKenkiBeforeIkishotenOvercap(in Strategy strategy, Enemy? primaryTarget, int plannedKenki, bool zanshinQueued, bool seneiGurenQueued, bool ikishotenQueued)
    {
        if (AvailableKenkiAfterReserve(strategy, plannedKenki, zanshinQueued, seneiGurenQueued) < 25)
            return false;

        if (ikishotenQueued)
            return plannedKenki >= KenkiCap;

        if (!ShouldUseIkishotenByTiming(strategy))
            return false;

        var gcdsUntilIkishoten = GCDsUntil(ReadyIn(AID.Ikishoten));
        if (gcdsUntilIkishoten > KenkiOvercapLookaheadGCDs)
            return false;

        var projectedKenki = ProjectKenkiAfterUpcomingGCDs(strategy, primaryTarget, plannedKenki, gcdsUntilIkishoten);
        return projectedKenki + 50 > KenkiCap;
    }

    private bool ShouldUseShohaNow(in Strategy strategy)
    {
        if (!BurstOGCDsAllowed(strategy))
            return false;

        if (!Unlocked(AID.Shoha) || Meditation < 3)
            return false;

        if (InFast208Opener(strategy) && Unlocked(AID.OgiNamikiri) && OgiLeft > GCD && !OgiRepeat)
            return false;

        var zeroSecondOpenerBurst = InZeroSecondOpenerBurst(strategy);
        var oneMinuteBurstActive = PlannedOneMinuteBurstActive(strategy);
        var oneMinuteBurstSoon = PlannedOneMinuteBurstInWithExternal() > AnimLock && PlannedOneMinuteBurstInWithExternal() <= 15;
        var overcapOnNextGCD = GrantsMeditation(NextGCD);
        var beforeDowntime = HasKnownDowntime() && !RaidBuffBeforeDowntime() && !CanFitGCD(EffectiveDowntimeIn, 1);

        return zeroSecondOpenerBurst
            || oneMinuteBurstActive
            || overcapOnNextGCD
            || beforeDowntime
            || !oneMinuteBurstSoon;
    }

    private bool ShohaWouldOvercapNextGCD()
        => GrantsMeditation(NextGCD);

    private bool ShouldDelayShintenKyutenForFast208Opener(in Strategy strategy)
    {
        if (!InFast208Opener(strategy))
            return false;

        if (ShouldUseSeneiGurenForStandardOpenerNow(strategy))
            return true;

        if (ShouldDelayZanshinForStandardOpener(strategy) || Zanshin > 0 && NextGCD == AID.Higanbana)
            return true;

        if (OgiLeft > GCD || OgiRepeat)
            return true;

        return NextGCD is AID.Kasha or AID.Gekko or AID.Higanbana or AID.OgiNamikiri or AID.KaeshiNamikiri or AID.TendoSetsugekka;
    }

    private bool ShouldProjectIkishotenForBurst(in Strategy strategy)
    {
        if (!Unlocked(AID.Ikishoten) || Kenki > 50)
            return false;

        if (OgiLeft > GCD || OgiRepeat)
            return false;

        if (InNormalOpenerOrderWindow(strategy) && (!HaveDmg || !HaveHaste))
            return false;

        return ReadyIn(AID.Ikishoten) <= BurstWindowEndIn(strategy)
            && (ShouldUseIkishotenNow(strategy) || ShouldPrepareOgiForBurst(strategy) || PlannedTwoMinuteBurstActive(strategy));
    }

    private int ProjectedKenkiForBurst(in Strategy strategy)
        => Math.Min(100, Kenki + (ShouldProjectIkishotenForBurst(strategy) ? 50 : 0));

    private bool ShouldReserveKenkiForZanshin(in Strategy strategy, bool zanshinQueued = false)
        => !zanshinQueued && Unlocked(AID.Zanshin) && (Zanshin > 0 || ShouldProjectIkishotenForBurst(strategy));

    private bool ShouldReserveKenkiForSeneiOrGuren(in Strategy strategy, bool seneiGurenQueued = false)
    {
        if (seneiGurenQueued)
            return false;

        return SeneiGurenReadyIn() <= BurstWindowEndIn(strategy);
    }

    private int ReservedKenkiForBurst(in Strategy strategy, bool zanshinQueued = false, bool seneiGurenQueued = false)
    {
        var reserve = 0;

        if (ShouldReserveKenkiForSeneiOrGuren(strategy, seneiGurenQueued))
            reserve += 25;

        if (ShouldReserveKenkiForZanshin(strategy, zanshinQueued))
            reserve += 50;

        return reserve;
    }

    private int AvailableKenkiAfterBurstReserve(in Strategy strategy)
        => ProjectedKenkiForBurst(strategy) - ReservedKenkiForBurst(strategy);

    private int AvailableKenkiAfterBurstReserve(in Strategy strategy, int plannedKenki, bool zanshinQueued, bool seneiGurenQueued)
        => plannedKenki - ReservedKenkiForBurst(strategy, zanshinQueued, seneiGurenQueued);

    private bool ShouldUseShintenOrKyutenWithKenkiBudget(in Strategy strategy)
        => AvailableKenkiAfterBurstReserve(strategy) >= 25;

    private bool ShouldUseShintenOrKyutenWithKenkiBudget(in Strategy strategy, int plannedKenki, bool zanshinQueued, bool seneiGurenQueued)
        => AvailableKenkiAfterBurstReserve(strategy, plannedKenki, zanshinQueued, seneiGurenQueued) >= 25;

    private bool ShouldSaveKenkiForBurst(in Strategy strategy)
    {
        var burstSoon = PlannedOneMinuteBurstInWithExternal() <= 15 || PlannedTwoMinuteBurstInWithExternal() <= 15 || InZeroSecondOpenerBurst(strategy);
        var seneiGurenSoon = SeneiGurenReadyIn() < 10;
        var zanshinReady = Zanshin > 0;
        var ogiSoon = OgiLeft > GCD || OgiRepeat || ShouldPrepareOgiForBurst(strategy);

        return burstSoon || seneiGurenSoon || zanshinReady || ogiSoon;
    }

    private bool ShouldUseTsubameNow(in Strategy strategy)
    {
        var zeroSecondOpenerBurst = InZeroSecondOpenerBurst(strategy);
        var oneMinuteBurstActive = PlannedOneMinuteBurstActive(strategy);
        var expiresSoon = !CanFitGCD(Tsubame.Left, 1);
        var potionExpiresSoon = PotionLeft > GCD && !CanFitGCD(PotionLeft, 1);
        var downtimeSoon = HasKnownDowntime() && !CanFitGCD(EffectiveDowntimeIn, 1);

        switch (strategy.Tsubame.Value)
        {
            case TsubameStrategy.Force:
                return true;

            case TsubameStrategy.Delay:
                return false;

            case TsubameStrategy.Hold:
                return zeroSecondOpenerBurst
                    || oneMinuteBurstActive
                    || expiresSoon
                    || potionExpiresSoon
                    || downtimeSoon;

            default:
                break;
        }

        // Tsubame-gaeshi Ready blocks the next Iaijutsu, so holding Kaeshi: Setsugekka for the one-minute burst delays the whole sen
        // cycle, turns sen-granting combo finishers into Gyofu fillers and lets some of the held Kaeshi expire - more than the burst adds
        // (-2.3% on the 2.08 route, -6.4% on the Sharif 2.14 route, with or without party buffs). Auto only keeps the 2.08 odd-burst order
        // Tendo Setsugekka -> Higanbana -> Tendo Kaeshi Setsugekka, which is worth it.
        return !ShouldDelayFast208TendoKaeshiUntilAfterHiganbana(strategy);
    }

    private bool ShouldHoldNonBanaIaijutsuForTsubame(in Strategy strategy, Enemy? primaryTarget)
    {
        if (strategy.Tsubame.Value == TsubameStrategy.Delay)
            return false;

        if (Tsubame.Left <= 0 || Tsubame.Action == IaiRepeat.None)
            return false;

        var (a, target) = TsubameAction(primaryTarget, Tsubame.Action);
        return a != default && Unlocked(a) && CanUseTsubameAction(a, target) && !ShouldUseTsubameNow(strategy);
    }

    private bool ShouldSuppressSenGeneratingComboForHeldTsubame(in Strategy strategy, Enemy? primaryTarget, AID action)
    {
        if (NumStickers != 3 || !ShouldHoldNonBanaIaijutsuForTsubame(strategy, primaryTarget))
            return false;

        return action switch
        {
            AID.Yukikaze => true,
            AID.Gekko or AID.Mangetsu => CanFitGCD(DamageUpLeft, 1),
            AID.Kasha or AID.Oka => CanFitGCD(HasteLeft, 1),
            _ => false
        };
    }

    private bool ShouldUseEnpiForHeldTsubameFallback(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Unlocked(AID.Enpi))
            return false;

        if (NumStickers != 3 || !ShouldHoldNonBanaIaijutsuForTsubame(strategy, primaryTarget) || !OutOfMeleeWithRangedTarget(primaryTarget))
            return false;

        return strategy.Enpi.Value switch
        {
            EnpiStrategy.Ranged => true,
            EnpiStrategy.Enhanced => EnhancedEnpiReady,
            _ => false
        };
    }

    private bool ShouldSuppressFallbackForHeldTsubame(in Strategy strategy, Enemy? primaryTarget)
    {
        if (NumStickers != 3 || !ShouldHoldNonBanaIaijutsuForTsubame(strategy, primaryTarget))
            return false;

        if (OutOfMeleeWithRangedTarget(primaryTarget))
            return true;

        return RaidBuffsIn > 0 && RaidBuffsIn <= GCD;
    }

    private int GCDsUntil(float seconds)
    {
        if (float.IsNaN(seconds) || float.IsInfinity(seconds) || seconds == float.MaxValue)
            return int.MaxValue;

        return seconds <= 0 ? 0 : Math.Max(1, (int)MathF.Ceiling(seconds / EffectiveAttackGCDLength()));
    }

    private float NextBurstIn()
        => Math.Min(PlannedOneMinuteBurstInWithExternal(), PlannedTwoMinuteBurstInWithExternal());

    private bool CanRebuildAndSpendMidareAfterHagakure(in Strategy strategy, Enemy? primaryTarget, int gcdsAvailable)
    {
        var gcdsToThreeSen = GCDsToCreateThreeSenNormallyAfterHagakure(primaryTarget);
        return gcdsToThreeSen < UnreachableGCDs && gcdsToThreeSen + 1 <= gcdsAvailable;
    }

    private bool CanRebuildAndSpendAOEIaijutsuAfterHagakure(int gcdsAvailable)
    {
        if (!ShouldUseAOERotation() || !Unlocked(AOEStarter) || !Unlocked(AID.Mangetsu) || !Unlocked(AID.Oka) || !Unlocked(AID.TenkaGoken))
            return false;

        var gcdsToMoonFlower = Meikyo.Left > GCD && Meikyo.Stacks >= 2 ? 2 : ComboLastMove == AOEStarter ? 3 : 4;
        return gcdsAvailable >= gcdsToMoonFlower + 1;
    }

    private bool HasDesiredBurstSenComposition()
        => ShouldUseAOERotation() ? Moon && Flower && NumStickers == 2 : Ice && Moon && Flower;

    private bool CanUseHiganbanaNow(in Strategy strategy)
    {
        if (!Unlocked(AID.Higanbana) || NumStickers != 1 || BestDotTarget == null)
            return false;

        var expectedHiganbanaPotency = Potency.ExpectedHiganbanaPotency(ExpectedHiganbanaUptimeForTarget(BestDotTarget));
        var higanbanaWorthUsing = strategy.Higanbana.Value == BanaStrategy.Force || expectedHiganbanaPotency >= MinimumHiganbanaAutoPotency();
        return ShouldRefreshHiganbanaNow()
            && higanbanaWorthUsing
            && !ShouldDelayHiganbanaForOpenerOrder(strategy)
            && !ShouldDelayHiganbanaForRaidBuff(strategy)
            && HaveDmg;
    }

    private bool CanUseThreeSenIaijutsuNow(Enemy? primaryTarget)
    {
        if (!HaveDmg || NumStickers != 3 || primaryTarget == null)
            return false;

        var iai = Tendo > GCD && Unlocked(AID.TendoSetsugekka) ? AID.TendoSetsugekka : AID.MidareSetsugekka;
        return Unlocked(iai);
    }

    private bool ShouldPreserveSenForHiganbana()
        => Unlocked(AID.Higanbana)
            && NumStickers == 1
            && BestDotTarget != null
            && ShouldPrepareHiganbanaRefresh()
            && Potency.ExpectedHiganbanaPotency(ExpectedHiganbanaUptimeForTarget(BestDotTarget)) >= MinimumHiganbanaAutoPotency();

    private bool CanUseNonBanaIaijutsuNow(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!HaveDmg)
            return false;

        if (NumStickers == 3 && !ShouldHoldNonBanaIaijutsuForTsubame(strategy, primaryTarget))
        {
            var iai = Tendo > GCD && Unlocked(AID.TendoSetsugekka) ? AID.TendoSetsugekka : AID.MidareSetsugekka;
            return primaryTarget != null && Unlocked(iai);
        }

        if (NumStickers != 2)
            return false;

        var aoeIai = Tendo > GCD && Unlocked(AID.TendoGoken) ? AID.TendoGoken : AID.TenkaGoken;
        var stIai = Tendo > GCD && Unlocked(AID.TendoSetsugekka) ? AID.TendoSetsugekka : AID.MidareSetsugekka;
        return Unlocked(aoeIai)
            && NumTenkaTargets > 0
            && (Potency.ExpectedAOEPotency(aoeIai, NumTenkaTargets) > Potency.ExpectedSingleTargetPotency(stIai)
                || ShouldFallbackToExpiringTendoGoken(primaryTarget, aoeIai))
            && !ShouldHoldNonBanaIaijutsuForTsubame(strategy, primaryTarget);
    }

    private bool ShouldFallbackToExpiringTendoGoken(Enemy? primaryTarget, AID aoeIai)
    {
        if (aoeIai != AID.TendoGoken || Tendo <= GCD || NumStickers != 2 || NumTenkaTargets <= 0)
            return false;

        var gcdsToMissingSen = GCDsToCreateAnySenNormally(primaryTarget);
        return gcdsToMissingSen == UnreachableGCDs || gcdsToMissingSen + 1 > GCDsUntil(Tendo);
    }

    private bool CanUseKaeshiNow(in Strategy strategy, Enemy? primaryTarget)
    {
        if (OgiRepeat)
            return Unlocked(AID.KaeshiNamikiri) && BestOgiTarget != null && ShouldUseKaeshiNamikiriNow(strategy);

        if (Tsubame.Left <= 0 || Tsubame.Action == IaiRepeat.None)
            return false;

        var (aid, target) = TsubameAction(primaryTarget, Tsubame.Action);
        return aid != default && Unlocked(aid) && CanUseTsubameAction(aid, target) && ShouldUseTsubameNow(strategy);
    }

    private bool CanUseOgiNamikiriNow(in Strategy strategy)
    {
        var useOgiBeforeDowntime = ShouldUseOgiNamikiriBeforeDowntime();
        var ogiExpiresSoon = !CanFitGCD(OgiLeft, 1);
        var allowOgiWithoutDmg = ogiExpiresSoon || useOgiBeforeDowntime;
        var allowOgiWithoutHaste = ogiExpiresSoon || useOgiBeforeDowntime || PlannedTwoMinuteBurstActive(strategy);
        return Unlocked(AID.OgiNamikiri)
            && !OgiRepeat
            && OgiLeft > GCD
            && BestOgiTarget != null
            && (!OgiWaitsForHiganbana(strategy) || InZeroSecondOpenerBurst(strategy) || useOgiBeforeDowntime || ogiExpiresSoon)
            && (HaveDmg || allowOgiWithoutDmg)
            && (HaveHaste || allowOgiWithoutHaste)
            && !ShouldDelayOgiForStandardOpener(strategy)
            && ShouldUseOgiNamikiriNow(strategy)
            && (Meikyo.Left == 0 || CanFitGCD(Meikyo.Left, 2 + Meikyo.Stacks));
    }

    private float ExpectedTsubamePotency(AID aid)
        => aid is AID.KaeshiGoken or AID.TendoKaeshiGoken
            ? Potency.ExpectedAOEPotency(aid, NumTenkaTargets)
            : Potency.ExpectedSingleTargetPotency(aid);

    private bool ShouldPrioritizeTsubameOverHiganbana(in Strategy strategy, Enemy? primaryTarget, float expectedHiganbanaPotency)
    {
        if (Tsubame.Left <= 0 || Tsubame.Action == IaiRepeat.None)
            return false;

        var (aid, target) = TsubameAction(primaryTarget, Tsubame.Action);
        if (aid == default || !Unlocked(aid) || !CanUseTsubameAction(aid, target) || !ShouldUseTsubameNow(strategy))
            return false;

        if (CanFitGCD(Tsubame.Left, 1))
            return false;

        return TargetDotLeft > 0 || ExpectedTsubamePotency(aid) >= expectedHiganbanaPotency;
    }

    private const int UnreachableGCDs = 999;

    private bool MeikyoCanCreateSen(AID action)
        => Meikyo.Left > GCD && Meikyo.Stacks > 0 && Unlocked(action) && ActionGrantsMissingSen(action);

    private int GCDsToCreateAnySenNormally(Enemy? primaryTarget)
    {
        if (primaryTarget != null)
        {
            if (MeikyoCanCreateSen(AID.Yukikaze) || MeikyoCanCreateSen(AID.Gekko) || MeikyoCanCreateSen(AID.Kasha))
                return 1;

            if (ComboLastMove == AID.Jinpu && Unlocked(AID.Gekko) && ActionGrantsMissingSen(AID.Gekko))
                return 1;

            if (ComboLastMove == AID.Shifu && Unlocked(AID.Kasha) && ActionGrantsMissingSen(AID.Kasha))
                return 1;

            if (ComboLastMove == STStarter && Unlocked(AID.Yukikaze) && ActionGrantsMissingSen(AID.Yukikaze))
                return 1;

            if (Unlocked(STStarter) && Unlocked(AID.Yukikaze) && ActionGrantsMissingSen(AID.Yukikaze))
                return 2;

            if (Unlocked(STStarter) && Unlocked(AID.Jinpu) && Unlocked(AID.Gekko) && ActionGrantsMissingSen(AID.Gekko))
                return 3;

            if (Unlocked(STStarter) && Unlocked(AID.Shifu) && Unlocked(AID.Kasha) && ActionGrantsMissingSen(AID.Kasha))
                return 3;
        }

        if (ShouldUseAOERotation())
        {
            if (MeikyoCanCreateSen(AID.Mangetsu) || MeikyoCanCreateSen(AID.Oka))
                return 1;

            if (ComboLastMove == AOEStarter)
            {
                if (Unlocked(AID.Mangetsu) && ActionGrantsMissingSen(AID.Mangetsu))
                    return 1;

                if (Unlocked(AID.Oka) && ActionGrantsMissingSen(AID.Oka))
                    return 1;
            }

            if (Unlocked(AOEStarter))
            {
                if (Unlocked(AID.Mangetsu) && ActionGrantsMissingSen(AID.Mangetsu))
                    return 2;

                if (Unlocked(AID.Oka) && ActionGrantsMissingSen(AID.Oka))
                    return 2;
            }
        }

        return UnreachableGCDs;
    }

    private int GCDsToCreateDesiredAOESenNormally()
    {
        var needMoon = !Moon;
        var needFlower = !Flower;
        if (!needMoon && !needFlower)
            return 0;

        var canCreateMoon = Unlocked(AID.Mangetsu) || Unlocked(AID.Gekko);
        var canCreateFlower = Unlocked(AID.Oka) || Unlocked(AID.Kasha);
        if (Meikyo.Left > GCD && Meikyo.Stacks > 0)
        {
            var missing = (needMoon ? 1 : 0) + (needFlower ? 1 : 0);
            if (Meikyo.Stacks >= missing && (!needMoon || canCreateMoon) && (!needFlower || canCreateFlower))
                return missing;
        }

        if (!ShouldUseAOERotation() || !Unlocked(AOEStarter))
            return UnreachableGCDs;

        if (ComboLastMove == AOEStarter)
        {
            if (needMoon && !needFlower && Unlocked(AID.Mangetsu))
                return 1;

            if (!needMoon && needFlower && Unlocked(AID.Oka))
                return 1;

            if (needMoon && needFlower && Unlocked(AID.Mangetsu) && Unlocked(AID.Oka))
                return 3;
        }

        if (needMoon && !needFlower && Unlocked(AID.Mangetsu))
            return 2;

        if (!needMoon && needFlower && Unlocked(AID.Oka))
            return 2;

        if (needMoon && needFlower && Unlocked(AID.Mangetsu) && Unlocked(AID.Oka))
            return 4;

        return UnreachableGCDs;
    }

    private int GCDsToCreateThreeSenNormally(Enemy? primaryTarget)
    {
        if (primaryTarget == null || !Unlocked(STStarter) || !Unlocked(AID.Yukikaze) || !Unlocked(AID.Jinpu) || !Unlocked(AID.Gekko) || !Unlocked(AID.Shifu) || !Unlocked(AID.Kasha))
            return UnreachableGCDs;

        if (Ice && Moon && Flower)
            return 0;

        var missing = (Ice ? 0 : 1) + (Moon ? 0 : 1) + (Flower ? 0 : 1);
        if (Meikyo.Left > GCD && Meikyo.Stacks >= missing)
            return missing;

        var gcds = 0;
        var combo = ComboLastMove;
        var ice = Ice;
        var moon = Moon;
        var flower = Flower;

        if (!moon && combo == AID.Jinpu)
        {
            gcds += 1;
            moon = true;
            combo = AID.None;
        }

        if (!flower && combo == AID.Shifu)
        {
            gcds += 1;
            flower = true;
            combo = AID.None;
        }

        if (!ice && combo is AID.Hakaze or AID.Gyofu)
        {
            gcds += 1;
            ice = true;
            combo = AID.None;
        }

        if (!ice)
        {
            gcds += 2;
            ice = true;
        }

        if (!moon)
        {
            gcds += combo is AID.Hakaze or AID.Gyofu ? 2 : 3;
            moon = true;
            combo = AID.None;
        }

        if (!flower)
        {
            gcds += combo is AID.Hakaze or AID.Gyofu ? 2 : 3;
            flower = true;
        }

        return ice && moon && flower ? gcds : UnreachableGCDs;
    }

    private bool CanReachDesiredBurstSenNormallyBeforeDeadline(int gcdsAvailable)
    {
        if (gcdsAvailable <= 0)
            return false;

        if (NumStickers > DesiredBurstSenCount())
            return false;

        if (HasDesiredBurstSenComposition())
            return true;

        if (!ShouldUseAOERotation())
            return false;

        var gcdsToDesiredSen = GCDsToCreateDesiredAOESenNormally();
        return gcdsToDesiredSen < UnreachableGCDs && gcdsToDesiredSen <= gcdsAvailable;
    }

    private bool CanReachAndSpendDesiredIaijutsuNormallyBeforeDeadline(Enemy? primaryTarget, int gcdsAvailable)
    {
        if (gcdsAvailable <= 0)
            return false;

        if (NumStickers > DesiredBurstSenCount())
            return false;

        if (ShouldUseAOERotation())
        {
            var gcdsToDesiredSen = GCDsToCreateDesiredAOESenNormally();
            return gcdsToDesiredSen < UnreachableGCDs && gcdsToDesiredSen + 1 <= gcdsAvailable;
        }

        var gcdsToThreeSen = GCDsToCreateThreeSenNormally(primaryTarget);
        return gcdsToThreeSen < UnreachableGCDs && gcdsToThreeSen + 1 <= gcdsAvailable;
    }

    private int GCDsToCreateThreeSenNormallyAfterHagakure(Enemy? primaryTarget)
    {
        if (primaryTarget == null || !Unlocked(STStarter) || !Unlocked(AID.Yukikaze) || !Unlocked(AID.Jinpu) || !Unlocked(AID.Gekko) || !Unlocked(AID.Shifu) || !Unlocked(AID.Kasha))
            return UnreachableGCDs;

        var gcds = 0;
        var createsIce = false;
        var createsMoon = false;
        var createsFlower = false;

        switch (ComboLastMove)
        {
            case AID.Jinpu:
                gcds += 1;
                createsMoon = true;
                break;
            case AID.Shifu:
                gcds += 1;
                createsFlower = true;
                break;
            case AID.Hakaze:
            case AID.Gyofu:
                gcds += 1;
                createsIce = true;
                break;
        }

        if (!createsIce)
            gcds += 2;

        if (!createsMoon)
            gcds += 3;

        if (!createsFlower)
            gcds += 3;

        return gcds;
    }

    private bool CanRebuildFast208BurstEntryAfterHagakure(in Strategy strategy, Enemy? primaryTarget, float burstIn, int gcdsUntilBurst)
    {
        if (!UseFast208Route(strategy) || InFast208Opener(strategy))
            return false;

        if (burstIn <= GCD || gcdsUntilBurst is < 5 or > 8)
            return false;

        if (ShouldRefreshHiganbanaNow() || ShouldPreserveSenForHiganbana())
            return false;

        if (Tsubame.Action != IaiRepeat.Setsugekka || Tsubame.Left <= burstIn || !CanFitGCD(Tsubame.Left - burstIn, 1))
            return false;

        if (Tendo > GCD || Meikyo.Left > GCD || !Unlocked(AID.MeikyoShisui))
            return false;

        if (NumStickers == 3)
            return false;

        var gcdsToThreeSen = GCDsToCreateThreeSenNormallyAfterHagakure(primaryTarget);
        return gcdsToThreeSen < UnreachableGCDs && gcdsToThreeSen <= gcdsUntilBurst;
    }

    private bool ShouldUseHagakureForBurstRecovery(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Unlocked(AID.Hagakure) || NumStickers == 0 || ReadyIn(AID.Hagakure) > GCD)
            return false;

        var iaijutsuTarget = BestIaijutsuTarget ?? primaryTarget;
        var allowAOEWrongSenRecovery = ShouldUseAOERotation() && NumStickers > DesiredBurstSenCount();
        if (CanUseHiganbanaNow(strategy)
            || !allowAOEWrongSenRecovery && CanUseThreeSenIaijutsuNow(iaijutsuTarget)
            || !allowAOEWrongSenRecovery && CanUseNonBanaIaijutsuNow(strategy, iaijutsuTarget)
            || CanUseOgiNamikiriNow(strategy)
            || CanUseKaeshiNow(strategy, iaijutsuTarget))
            return false;

        if (ShouldPreserveSenForHiganbana())
            return false;

        var nextBurstIn = NextBurstIn();
        var gcdsUntilBurst = GCDsUntil(nextBurstIn);
        var downtimeGCDs = HasKnownDowntime() ? GCDsUntil(EffectiveDowntimeIn) : int.MaxValue;
        var burstDeadlineGCDs = Math.Min(gcdsUntilBurst, downtimeGCDs);
        var burstRecoveryWindow = nextBurstIn > 0 && gcdsUntilBurst is >= 5 and <= 7;
        var canRebuildForBurst = ShouldUseAOERotation()
            ? CanRebuildAndSpendAOEIaijutsuAfterHagakure(burstDeadlineGCDs)
            : CanRebuildAndSpendMidareAfterHagakure(strategy, primaryTarget, burstDeadlineGCDs);
        var canReachBurstSenNormally = CanReachDesiredBurstSenNormallyBeforeDeadline(burstDeadlineGCDs);
        var canSpendBeforeDowntimeNormally = CanReachAndSpendDesiredIaijutsuNormallyBeforeDeadline(primaryTarget, downtimeGCDs);
        var fast208BurstRecovery = CanRebuildFast208BurstEntryAfterHagakure(strategy, primaryTarget, nextBurstIn, gcdsUntilBurst);
        var wrongSenForBurst = NumStickers > DesiredBurstSenCount() || ShouldUseAOERotation() && NumStickers > 0 && !HasDesiredBurstSenComposition();
        var heldTsubameStall = NumStickers == 3
            && ShouldHoldNonBanaIaijutsuForTsubame(strategy, primaryTarget)
            && !CanUseKaeshiNow(strategy, primaryTarget)
            && canRebuildForBurst;
        var downtimeRecovery = HasKnownDowntime()
            && !RaidBuffBeforeDowntime()
            && downtimeGCDs >= 5
            && !HasDesiredBurstSenComposition()
            && !canSpendBeforeDowntimeNormally
            && (ShouldUseAOERotation()
                ? CanRebuildAndSpendAOEIaijutsuAfterHagakure(downtimeGCDs)
                : CanRebuildAndSpendMidareAfterHagakure(strategy, primaryTarget, downtimeGCDs));

        // No Hagakure for a Higanbana refresh blocked by two sen: the natural route (third sen, Midare, one sen, Higanbana) is 4-5
        // GCDs and only a few ticks late, while two Hagakure'd sen are most of a Midare. Measured 2026-10-03 in 1200s fights: the
        // recovery cost 0.9% on the 2.08 route and 0.2% on the 2.14 route, and it never paid off in any shorter scenario.
        return fast208BurstRecovery
            || burstRecoveryWindow && canRebuildForBurst && (wrongSenForBurst && !canReachBurstSenNormally || heldTsubameStall)
            || downtimeRecovery;
    }

    private (AID, Enemy?) TsubameAction(Enemy? primaryTarget, IaiRepeat k) => k switch
    {
        IaiRepeat.Setsugekka => (AID.KaeshiSetsugekka, primaryTarget),
        IaiRepeat.TendoSetsugekka => (AID.TendoKaeshiSetsugekka, primaryTarget),
        IaiRepeat.Goken => (AID.KaeshiGoken, null),
        IaiRepeat.TendoGoken => (AID.TendoKaeshiGoken, null),
        _ => (default, null)
    };

    private void UseIaijutsu(in Strategy strategy, Enemy? primaryTarget)
    {
        var currentStrategy = strategy;
        var expectedHiganbanaUptime = ExpectedHiganbanaUptimeForTarget(BestDotTarget);
        var expectedHiganbanaPotency = Potency.ExpectedHiganbanaPotency(expectedHiganbanaUptime);
        var higanbanaWorthUsing = strategy.Higanbana.Value == BanaStrategy.Force || expectedHiganbanaPotency >= MinimumHiganbanaAutoPotency();

        bool holdNonBanaIaijutsuForTsubame()
            => ShouldHoldNonBanaIaijutsuForTsubame(currentStrategy, primaryTarget);

        bool holdNonBanaIaijutsuForHagakure()
            => ShouldUseHagakureForBurstRecovery(currentStrategy, primaryTarget);

        if (Unlocked(AID.Higanbana)
            && NumStickers == 1
            && BestDotTarget != null
            && ShouldRefreshHiganbanaNow() // dot expiring
            && higanbanaWorthUsing
            && !ShouldDelayHiganbanaForOpenerOrder(strategy)
            && !ShouldDelayHiganbanaForRaidBuff(strategy)
            && HaveDmg)
        {
            if (ShouldPrioritizeTsubameOverHiganbana(strategy, primaryTarget, expectedHiganbanaPotency))
                return;

            PushGCD(AID.Higanbana, BestDotTarget, GCDPriority.Higanbana);
        }

        // we don't cast any other iaijutsu without having fugetsu up first, since it takes max 2 GCDs to apply
        if (!HaveDmg)
            return;

        if (NumStickers == 2)
        {
            var aoeIai = Tendo > GCD && Unlocked(AID.TendoGoken) ? AID.TendoGoken : AID.TenkaGoken;
            var stIai = Tendo > GCD && Unlocked(AID.TendoSetsugekka) ? AID.TendoSetsugekka : AID.MidareSetsugekka;
            var aoePotency = Potency.ExpectedAOEPotency(aoeIai, NumTenkaTargets);
            var stPotency = Potency.ExpectedSingleTargetPotency(stIai);
            var tendoGokenFallback = ShouldFallbackToExpiringTendoGoken(primaryTarget, aoeIai);

            if (Tsubame.Left > 0 && Tsubame.Action != IaiRepeat.None)
            {
                var aoeKaeshi = Tendo > GCD ? AID.TendoKaeshiGoken : AID.KaeshiGoken;
                var stKaeshi = Tendo > GCD ? AID.TendoKaeshiSetsugekka : AID.KaeshiSetsugekka;
                if (Unlocked(aoeKaeshi))
                    aoePotency += Potency.ExpectedAOEPotency(aoeKaeshi, NumTenkaTargets);
                if (Unlocked(stKaeshi))
                    stPotency += Potency.ExpectedSingleTargetPotency(stKaeshi);
            }

            if (Unlocked(aoeIai) && NumTenkaTargets > 0 && (aoePotency > stPotency || tendoGokenFallback) && !holdNonBanaIaijutsuForTsubame() && !holdNonBanaIaijutsuForHagakure())
            {
                PushGCD(aoeIai, Player, GCDPriority.Iaijutsu);
            }
        }

        if (NumStickers == 3 && primaryTarget != null && !holdNonBanaIaijutsuForTsubame() && !holdNonBanaIaijutsuForHagakure())
        {
            var iai = Tendo > GCD && Unlocked(AID.TendoSetsugekka) ? AID.TendoSetsugekka : AID.MidareSetsugekka;
            if (Unlocked(iai))
                PushGCD(iai, primaryTarget, GCDPriority.Iaijutsu);
        }
    }

    private bool EmergencyMeikyo(in Strategy strategy, Enemy? primaryTarget)
    {
        // special case for if we got thrust into combat with no prep
        // pushed at gcd priority (as upstream does) so it executes before gyofu on an unprepared pull instead of being weaved after it
        if (strategy.Meikyo.Value != MeikyoStrategy.Delay && CanUseOGCD(AID.MeikyoShisui) && Meikyo.Left == 0 && !HaveDmg && CombatTimer < 5 && primaryTarget != null)
        {
            PushGCD(AID.MeikyoShisui, Player, GCDPriority.Higanbana);
            return true;
        }

        return false;
    }

    private (Positional, bool) GetNextPositional(in Strategy strategy)
    {
        if (NumAOETargets > 2 || !Unlocked(AID.Gekko))
            return (Positional.Any, false);

        switch (NextGCD)
        {
            case AID.Gekko:
                return (Positional.Rear, true);
            case AID.Kasha:
                return (Positional.Flank, true);
            case AID.Jinpu:
                return (Positional.Rear, false);
            case AID.Shifu:
                return (Positional.Flank, false);
            default:
                if (Unlocked(AID.Kasha) && !Flower)
                    return (Positional.Flank, false);
                if (Unlocked(AID.Gekko) && !Moon)
                    return (Positional.Rear, false);
                return (Positional.Any, false);
        }
    }

    private void OGCD(in Strategy strategy, Enemy? primaryTarget, bool emergencyMeikyoQueued)
    {
        if (!Player.InCombat)
            return;

        // any action ends Meditate; while it channels through a downtime, nothing here is worth the lost ticks
        if (MeditateChanneling(primaryTarget))
            return;

        var hasAttackableTarget = primaryTarget != null || BestRangedTarget != null;
        if (hasAttackableTarget)
            UsePotion(strategy);

        var zeroSecondOpenerBurst = InZeroSecondOpenerBurst(strategy);
        var saveKenkiForBurst = ShouldSaveKenkiForBurst(strategy);
        var downtimeSoon = DowntimeSoon();
        var raidBuffBeforeDowntime = downtimeSoon && RaidBuffsIn > 0 && RaidBuffsIn < EffectiveDowntimeIn;
        var spendKenkiBeforeDowntime = downtimeSoon && !raidBuffBeforeDowntime && Zanshin <= 0 && SeneiGurenReadyIn() >= 10;
        var zanshinExpiresSoon = !CanFitGCD(Zanshin, 1);
        var allowZanshinWithoutDmg = zanshinExpiresSoon || downtimeSoon;
        var canUsePrimaryTargetOGCDs = primaryTarget != null && HaveDmgForOGCD;

        // most important ogcd for alignment
        if (!emergencyMeikyoQueued)
            UseMeikyo(strategy);

        var shohaQueued = false;
        var shohaOvercapNextGCD = ShohaWouldOvercapNextGCD();
        // accidentally overcapping shoha will probably cause the second one to fall out of buffs, resulting in huge potency loss
        if ((HaveDmgForOGCD || shohaOvercapNextGCD) && BestLineTarget != null && CanUseOGCD(AID.Shoha) && ShouldUseShohaNow(strategy) && shohaOvercapNextGCD)
        {
            PushOGCD(AID.Shoha, BestLineTarget, setRotation: NumLineTargets > 1);
            shohaQueued = true;
        }

        int plannedKenki = KenkiAfterRecentBlinkAction();
        var useIkishoten = ShouldUseIkishotenNow(strategy, plannedKenki);

        if (useIkishoten)
        {
            PushOGCD(AID.Ikishoten, Player);
            plannedKenki = Math.Min(100, plannedKenki + 50);
        }

        var zanshinQueued = false;
        var seneiGurenQueued = false;
        var useSeneiGurenForStandardOpener = ShouldUseSeneiGurenForStandardOpenerNow(strategy);

        if (plannedKenki >= 25
            && BurstOGCDsAllowed(strategy)
            && HaveDmgForOGCD
            && !ShouldDelaySeneiGurenForStandardOpener(strategy)
            && (useSeneiGurenForStandardOpener || zeroSecondOpenerBurst || spendKenkiBeforeDowntime || PlannedTwoMinuteBurstActive(strategy) || PlannedTwoMinuteBurstInWithExternal() > (Unlocked(TraitID.EnhancedHissatsu) ? 40 : 100)))
        {
            if (CanUseOGCD(AID.HissatsuGuren) && BestLineTarget != null && NumLineTargets > 1)
            {
                PushOGCD(AID.HissatsuGuren, BestLineTarget, setRotation: true);
                plannedKenki -= 25;
                seneiGurenQueued = true;
            }
            else if (CanUseOGCD(AID.HissatsuSenei) && primaryTarget != null)
            {
                PushOGCD(AID.HissatsuSenei, primaryTarget);
                plannedKenki -= 25;
                seneiGurenQueued = true;
            }
            else if (CanUseOGCD(AID.HissatsuGuren) && BestLineTarget != null)
            {
                PushOGCD(AID.HissatsuGuren, BestLineTarget, setRotation: NumLineTargets > 1);
                plannedKenki -= 25;
                seneiGurenQueued = true;
            }
        }

        if ((HaveDmgForOGCD || allowZanshinWithoutDmg) && CanUseOGCD(AID.Zanshin) && ShouldUseZanshinNow(strategy) && plannedKenki >= 50 && BestOgiTarget != null)
        {
            PushOGCD(AID.Zanshin, BestOgiTarget, setRotation: NumOgiTargets > 1);
            plannedKenki -= 50;
            zanshinQueued = true;
        }

        if (ShouldUseTrueNorthNow(strategy, primaryTarget))
            PushOGCD(AID.TrueNorth, Player, -10);

        var burstActive = zeroSecondOpenerBurst || PlannedOneMinuteBurstActive(strategy) || PlannedTwoMinuteBurstActive(strategy);
        var saveKenki = !burstActive && (((!zeroSecondOpenerBurst && !PlannedOneMinuteBurstActive(strategy) && PlannedOneMinuteBurstInWithExternal() <= 15) || saveKenkiForBurst) && !spendKenkiBeforeDowntime);
        var fast208GyotenNow = ShouldUseFast208GyotenNow(strategy, primaryTarget, plannedKenki);
        var canUseShintenKyuten = !ShouldDelayShintenKyutenForFast208Opener(strategy)
            && (spendKenkiBeforeDowntime && plannedKenki >= 25
                || burstActive && ShouldUseShintenOrKyutenWithKenkiBudget(strategy, plannedKenki, zanshinQueued, seneiGurenQueued)
                || ShouldSpendKenkiForOvercap(strategy, primaryTarget, plannedKenki, zanshinQueued, seneiGurenQueued)
                || ShouldSpendKenkiBeforeIkishotenOvercap(strategy, primaryTarget, plannedKenki, zanshinQueued, seneiGurenQueued, useIkishoten)
                || !saveKenki && ShouldUseShintenOrKyutenWithKenkiBudget(strategy, plannedKenki, zanshinQueued, seneiGurenQueued));

        if (!shohaQueued && HaveDmgForOGCD && BestLineTarget != null && CanUseOGCD(AID.Shoha) && ShouldUseShohaNow(strategy))
        {
            PushOGCD(AID.Shoha, BestLineTarget, setRotation: NumLineTargets > 1);
            shohaQueued = true;
        }

        if (!fast208GyotenNow && HaveDmgForOGCD && canUseShintenKyuten)
        {
            if (CanUseOGCD(AID.HissatsuKyuten) && NumAOECircleTargets > 2)
            {
                PushOGCD(AID.HissatsuKyuten, Player);
                plannedKenki -= 25;
            }
            else if (CanUseOGCD(AID.HissatsuShinten) && canUsePrimaryTargetOGCDs)
            {
                PushOGCD(AID.HissatsuShinten, primaryTarget);
                plannedKenki -= 25;
            }
        }

        if (!useIkishoten && ShouldUseIkishotenNow(strategy, plannedKenki))
        {
            PushOGCD(AID.Ikishoten, Player);
            plannedKenki = Math.Min(KenkiCap, plannedKenki + 50);
        }

        if (fast208GyotenNow && plannedKenki >= 10)
        {
            PushOGCD(AID.HissatsuGyoten, primaryTarget);
            plannedKenki -= 10;
        }
    }

    private bool GrantsMeditation(AID aid) => aid is AID.MidareSetsugekka or AID.TenkaGoken or AID.Higanbana or AID.TendoSetsugekka or AID.TendoGoken or AID.OgiNamikiri;

    private bool ShouldUseMeikyoForSharifHiganbanaRefresh(in Strategy strategy)
    {
        if (!UseSharif214Route(strategy) || strategy.Higanbana.Value == BanaStrategy.Delay)
            return false;

        var banaPlan = GetSharifHiganbanaPlan(strategy);
        var burstPlan = GetSharifBurstEntryPlan(strategy);
        if (banaPlan == SharifBanaPlan.Meikyo24To18 || burstPlan is SharifBurstEntryPlan.Dot20To18 or SharifBurstEntryPlan.Dot16To14)
            return true;

        return ShouldPrepareHiganbanaRefresh() && (!Moon || !Flower) && TargetDotLeft <= 24;
    }

    private bool ShouldUseMeikyoForFast208OpenerHiganbana(in Strategy strategy)
        => InFast208Opener(strategy)
            && Tsubame.Action is not IaiRepeat.TendoSetsugekka and not IaiRepeat.TendoGoken
            && BestDotTarget != null
            && ShouldRefreshHiganbanaNow()
            && NumStickers == 0
            && Unlocked(AID.Gekko)
            && !Moon;

    private bool ShouldUseMeikyoForFast208Burst(in Strategy strategy)
        => Fast208BurstEntryReady(strategy);

    private void UseMeikyo(in Strategy strategy)
    {
        if (!CanUseOGCD(AID.MeikyoShisui) || Meikyo.Left > GCD)
            return;

        var midCombo = ComboLastMove is AID.Jinpu or AID.Shifu or AID.Hakaze or AID.Gyofu or AID.Fuga or AID.Fuko;
        var zeroSecondOpenerBurst = InZeroSecondOpenerBurst(strategy);
        var oneMinuteBurstIn = PlannedOneMinuteBurstInWithExternal();
        var twoMinuteBurstIn = PlannedTwoMinuteBurstInWithExternal();
        var waitForRaidBuff = !zeroSecondOpenerBurst && RaidBuffsLeft <= AnimLock && (oneMinuteBurstIn > 0 && oneMinuteBurstIn <= 15 || twoMinuteBurstIn > 0 && twoMinuteBurstIn <= 15);
        var capSoon = MeikyoCharges >= 2 || CanWeave(MaxChargesIn(AID.MeikyoShisui), 0.6f);
        var canUseMeikyoSafely = Tendo == 0 && (!midCombo || capSoon);
        var delayForStandardOpener = ShouldDelayMeikyoForStandardOpener(strategy);
        var prepareTendoForBurst = ShouldPrepareTendoForBurst(strategy);
        var prepareSenForBurst = ShouldPrepareSenForBurst(strategy);
        var sharifHiganbanaRefresh = ShouldUseMeikyoForSharifHiganbanaRefresh(strategy);
        var fast208OpenerHiganbana = ShouldUseMeikyoForFast208OpenerHiganbana(strategy);
        var fast208Burst = ShouldUseMeikyoForFast208Burst(strategy);
        var preserveMeikyoForFast208Burst = ShouldPreserveMeikyoForFast208Burst(strategy);

        // FightRemaining (value-of-information experiment): a charge that the fight will not outlive is not worth holding for a burst
        var fightEndMeikyo = UseFightEndMeikyo && FightEndUpper is var end && end <= FightEndMeikyoWindow && CanFitGCD(end, 3) && !(BaseDowntimeIn <= end);

        var use = strategy.Meikyo.Value switch
        {
            MeikyoStrategy.Auto => !delayForStandardOpener && canUseMeikyoSafely && (zeroSecondOpenerBurst || capSoon || fast208Burst || fightEndMeikyo || !preserveMeikyoForFast208Burst && (prepareTendoForBurst || sharifHiganbanaRefresh || prepareSenForBurst && oneMinuteBurstIn > GCD && oneMinuteBurstIn <= 8 || fast208OpenerHiganbana)),
            MeikyoStrategy.Cooldown => !delayForStandardOpener && canUseMeikyoSafely && (fast208Burst || !preserveMeikyoForFast208Burst && (!waitForRaidBuff || prepareTendoForBurst || sharifHiganbanaRefresh)),
            MeikyoStrategy.HoldOne => !delayForStandardOpener && canUseMeikyoSafely && capSoon,
            MeikyoStrategy.Force => true,
            _ => false
        };

        if (use)
            PushOGCD(AID.MeikyoShisui, Player);
    }

    private float HiganbanaLeft(Actor? p) => p == null ? float.MaxValue : StatusDetails(p, SID.Higanbana, Player.InstanceID).Left;

    private bool InConeAOE(Actor primary, Actor other) => TargetInAOECone(other, Player.Position, 8, Player.DirectionTo(primary), 60.Degrees());
    private bool InLineAOE(Actor primary, Actor other) => TargetInAOERect(other, Player.Position, Player.DirectionTo(primary), 10, 4);

    // instance method groups allocate a new delegate on every conversion; these run every frame, so the delegates are kept
    private PositionCheck? _inConeAOECheck;
    private PositionCheck? _inLineAOECheck;
    private Func<Actor?, float>? _higanbanaLeftFunc;
    private PositionCheck InConeAOECheck => _inConeAOECheck ??= InConeAOE;
    private PositionCheck InLineAOECheck => _inLineAOECheck ??= InLineAOE;
    private Func<Actor?, float> HiganbanaLeftFunc => _higanbanaLeftFunc ??= HiganbanaLeft;
}

internal static class StrategyExt
{
    public static bool EarlyBana(this SAM.OpenerStrategy strat) => strat is SAM.OpenerStrategy.GekkoBana or SAM.OpenerStrategy.KashaBana;
    public static bool EarlyKasha(this SAM.OpenerStrategy strat) => strat is SAM.OpenerStrategy.KashaBana or SAM.OpenerStrategy.KashaStandard;
}
