using BossMod.VPR;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.xan.Custom;

public sealed class VPR(RotationModuleManager manager, Actor player) : Attackxan<AID, TraitID, VPR.Strategy>(manager, player, PotionType.Dexterity)
{
    public struct Strategy : IStrategyCommon
    {
        public Track<Targeting> Targeting;
        public Track<AOEStrategy> AOE;
        [Track("Reawaken", Action = AID.Reawaken, MinLevel = 90)]
        public Track<OffensiveStrategy> Buffs;

        [Track("開幕バースト", InternalName = "OpenerBurst", Action = AID.Reawaken, MinLevel = 90)]
        public Track<OpenerBurstStrategy> OpenerBurst;

        [Track("蛇の霊気", InternalName = "SerpentsIre", Action = AID.SerpentsIre, MinLevel = 86)]
        public Track<SerpentsIreStrategy> SerpentsIre;

        [Track("Potion", InternalName = "Potion")]
        public Track<PotionStrategy> Potion;

        [Track("飛蛇の牙", InternalName = "WrithingSnap", Action = AID.WrithingSnap, MinLevel = 15)]
        public Track<SnapStrategy> Snap;

        [Track("飛蛇の尾(射程外)", InternalName = "UncoiledFuryRange", Action = AID.UncoiledFury, MinLevel = 82)]
        public Track<UncoiledFuryRangeStrategy> UncoiledFuryRange;

        [Track("蛇行", InternalName = "Slither", Action = AID.Slither, MinLevel = 40)]
        public Track<SlitherStrategy> Slither;

        [Track("True North", Action = AID.TrueNorth, MinLevel = 50)]
        public Track<TrueNorthStrategy> TrueNorth;

        [Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)]
        public Track<MechanicHintStrategy> MechanicHints;

        readonly Targeting IStrategyCommon.Targeting => Targeting.Value;
        readonly AOEStrategy IStrategyCommon.AOE => AOE.Value;
    }

    public enum SnapStrategy
    {
        [Option("使用しない")]
        None,
        [Option("自動", Targets = ActionTargets.Hostile)]
        Ranged
    }

    public enum UncoiledFuryRangeStrategy
    {
        [Option("自動")]
        Auto,

        [Option("使用しない")]
        Off
    }

    public enum SlitherStrategy
    {
        [Option("開幕")]
        Opener,

        [Option("開幕+バースト復帰")]
        OpenerAndBurstRecovery,

        [Option("使用しない")]
        Off
    }

    public enum TrueNorthStrategy
    {
        [Option("Auto")]
        Auto,

        [Option("Off")]
        Off
    }

    public enum SerpentsIreStrategy
    {
        [Option("自動")]
        Auto,

        [Option("使用しない")]
        Off,

        [Option("強制")]
        Force
    }

    public enum OpenerBurstStrategy
    {
        [Option("通常")]
        Standard,

        [Option("7.5 0秒バースト")]
        Patch75ZeroSecond
    }

    public enum PotionStrategy
    {
        [Option("開幕→偶数バースト")]
        OpenerAndEven,

        [Option("開幕以外、偶数バースト")]
        EvenOnly,

        [Option("使用しない")]
        Off
    }

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("xan VPR [Custom]", "Viper", "Standard rotation (xan)|Melee", "xan", RotationModuleQuality.Basic, BitMask.Build(Class.VPR), 100).WithStrategies<Strategy>();
    }

    public enum TwinType
    {
        None,
        SingleTarget,
        AOE,
        Coil
    }

    public DreadCombo DreadCombo;
    public int Coil; // max 3
    public int Offering; // max 100
    public int Anguine; // 0-4
    public AID CurSerpentsTail; // adjusted during reawaken and after basic combos
    public int TwinStacks; // max 2, granted by using "coil" or "den" gcds
    public TwinType TwinCombo;

    public float Swiftscaled;
    public float Instinct;
    public float FlankstungVenom;
    public float FlanksbaneVenom;
    public float HindstungVenom;
    public float HindsbaneVenom;
    public float HuntersVenom;
    public float SwiftskinsVenom;
    public float FellhuntersVenom;
    public float FellskinsVenom;
    public float GrimhuntersVenom;
    public float GrimskinsVenom;
    public float PoisedForTwinfang;
    public float PoisedForTwinblood;
    public float ReawakenReady;
    public float ReawakenLeft;
    public float HonedReavers;
    public float HonedSteel;

    public int NumAOETargets;
    public int NumRangedAOETargets;

    private Enemy? BestRangedAOETarget;
    private Enemy? BestGenerationTarget;
    private Enemy? BestLegacyTarget;
    private readonly Dictionary<Actor, int> _splashTargetCounts = [];

    private int CoilMax => Unlocked(TraitID.EnhancedVipersRattle) ? 3 : Unlocked(AID.UncoiledFury) ? 2 : 0;
    private const int GCDPriorityReawaken = 30;
    private const int GCDPriorityDreadCombo = 15;
    private const int GCDPriorityDreadComboBeforeEvenBurst = 18;
    private const int GCDPriorityRaidBuffCoil = 16;
    private const int GCDPriorityCoil = 12;
    private const int GCDPriorityVice = 10;
    private const int OGCDPriorityTrueNorth = 18;
    private const int OGCDPriorityTrueNorthBehindFollowUps = 9;
    private const float HoldViceBeforeSerpentsIre = 10.0f;
    private const float CoreBuffMaxDuration = 40.0f;
    private const float EvenBurstPrepBuffer = 1.0f;
    private const float ViceNoStartBeforeSerpentsIre = 8.0f;
    private const float WrithingSnapCurrentGCDBlocked = 0.10f;
    private const float WrithingSnapNextMeleeLikelyBlockedDistance = 5.0f;
    private const float CoilBuffEmergency = 6.0f;
    private const float Patch75ZeroOpenerEnd = 25.0f;
    private const int SerpentsIreReawakenGoal = 2;
    private const int GCDPriorityPatch75Opener = 25;
    private float _openerSlitherDelay = -1f;
    private bool _openerSlitherDelayArmed;
    private bool _openerSlitherConsumed;
    private int _serpentsIreReawakenGoal;
    private int _serpentsIreReawakenStarted;
    private float _lastReawakenLeft;
    // per-frame caches (see Exec): evaluated once instead of through nested Should* chains
    private float _ireReadyIn;
    private float _viceReadyIn;
    private float _evenBurstIn;
    private bool _planVice;
    private bool _wantPotionNow;

    // FightRemaining (value-of-information experiment): two separable rules, only active while Hints.FightRemaining is Known
    // 1. do not keep Offering for the even-minute Serpent's Ire double Reawaken when UpperBound says the fight ends before that burst could finish
    private static readonly bool VoiSpendOfferingBeforeEnd = true;
    // 2. in the last GCDs, spend Rattling Coils (Uncoiled Fury) whatever it costs the combo or the core buffs
    private static readonly bool VoiSpendCoilsBeforeEnd = true;

    // FightRemaining (value-of-information experiment): the fight ends before the even-minute burst (Serpent's Ire + the Reawaken it enables) could finish
    private bool VoiFightEndsBeforeEvenBurst()
    {
        var fr = Hints.FightRemaining;
        return fr.Known && fr.UpperBound < _ireReadyIn + AttackGCDLength + ReawakenSequenceDuration();
    }

    // FightRemaining (value-of-information experiment): few enough GCDs left that every unspent Coil must go now
    private bool VoiInCoilDumpWindow()
    {
        var fr = Hints.FightRemaining;
        if (!fr.Known)
            return false;

        var need = AttackGCDLength * (Coil + 1);
        if (ReawakenReady > 0 || Offering >= 50 || Anguine > 0 || ReawakenLeft > 0)
            need += ReawakenSequenceDuration();

        return fr.UpperBound <= need;
    }

    // Per-category action locks (ActionLocks): Pacification refuses weaponskills, Silence spells, Amnesia abilities, a stun-type status
    // everything. The client refuses after the queue has picked, so a locked candidate at the top of the queue is resubmitted every
    // frame while what is below it never runs. Every push goes through PushAction -> CanUse, so filtering there keeps the queue to
    // what the client accepts and the rotation falls back on its own (weaponskills under Amnesia, abilities under Pacification).
    protected override bool CanUse(AID action) => !IsActionLocked(action) && !DelaysWeaponskillResume(action);

    public override void Exec(in Strategy strategy, Enemy? primaryTarget)
    {
        UpdateMechanicForecast(strategy.MechanicHints.Value);
        SelectPrimaryTarget(strategy, ref primaryTarget, 3);

        var gauge = World.Client.GetGauge<ViperGauge>();
        DreadCombo = gauge.DreadCombo;
        Coil = gauge.RattlingCoilStacks;
        Offering = gauge.SerpentOffering;
        Anguine = gauge.AnguineTribute;

        CurSerpentsTail = gauge.SerpentCombo switch
        {
            SerpentCombo.DeathRattle => AID.DeathRattle,
            SerpentCombo.LastLash => AID.LastLash,
            SerpentCombo.FirstLegacy => AID.FirstLegacy,
            SerpentCombo.SecondLegacy => AID.SecondLegacy,
            SerpentCombo.ThirdLegacy => AID.ThirdLegacy,
            SerpentCombo.FourthLegacy => AID.FourthLegacy,
            _ => AID.SerpentsTail,
        };
        // this doesn't really matter because the GCDs grant unique statuses, but might as well track regardless
        TwinCombo = (byte)gauge.SerpentCombo switch
        {
            7 => TwinType.SingleTarget,
            8 => TwinType.AOE,
            9 => TwinType.Coil,
            _ => TwinType.None
        };
        TwinStacks = (byte)gauge.SerpentCombo & 3;

        FlanksbaneVenom = StatusLeft(SID.FlanksbaneVenom);
        FlankstungVenom = StatusLeft(SID.FlankstungVenom);
        HindsbaneVenom = StatusLeft(SID.HindsbaneVenom);
        HindstungVenom = StatusLeft(SID.HindstungVenom);
        Swiftscaled = StatusLeft(SID.Swiftscaled);
        Instinct = StatusLeft(SID.HuntersInstinct);
        HuntersVenom = StatusLeft(SID.HuntersVenom);
        SwiftskinsVenom = StatusLeft(SID.SwiftskinsVenom);
        FellhuntersVenom = StatusLeft(SID.FellhuntersVenom);
        FellskinsVenom = StatusLeft(SID.FellskinsVenom);
        GrimhuntersVenom = StatusLeft(SID.GrimhuntersVenom);
        GrimskinsVenom = StatusLeft(SID.GrimskinsVenom);
        PoisedForTwinfang = StatusLeft(SID.PoisedForTwinfang);
        PoisedForTwinblood = StatusLeft(SID.PoisedForTwinblood);
        ReawakenReady = StatusLeft(SID.ReawakenReady);
        ReawakenLeft = StatusLeft(SID.Reawakened);
        HonedReavers = StatusLeft(SID.HonedReavers);
        HonedSteel = StatusLeft(SID.HonedSteel);

        _splashTargetCounts.Clear();
        (BestRangedAOETarget, NumRangedAOETargets) = SelectTarget(strategy, primaryTarget, 20, IsSplashTarget, _splashTargetCounts);
        BestGenerationTarget = SelectTarget(strategy, primaryTarget, 3, IsSplashTarget, _splashTargetCounts).Best;
        BestLegacyTarget = SelectTarget(strategy, primaryTarget, 5, IsSplashTarget, _splashTargetCounts).Best;
        NumAOETargets = NumMeleeAOETargets(strategy);

        UpdateSerpentsIreDoubleReawakenState();

        _ireReadyIn = ReadyIn(AID.SerpentsIre);
        _viceReadyIn = ReadyIn(AID.Vicewinder);
        _evenBurstIn = TimeToEvenBurstWindow(strategy);
        _planVice = ShouldPlanViceForCoreBuffOrOffering(strategy, primaryTarget, Offering);
        _wantPotionNow = false;

        // once the countdown is over the opener Slither state is spent; resetting it before the target check keeps an aborted
        // countdown followed by a frame without a target from carrying it into the next countdown
        if (!(CountdownRemaining > 0) && _openerSlitherDelayArmed)
            ResetOpenerSlitherDelay();

        if (primaryTarget == null)
            return;

        if (CountdownRemaining > 0)
        {
            if (Player.DistanceToHitbox(primaryTarget) > 3)
            {
                if ((strategy.Slither.Value is SlitherStrategy.Opener or SlitherStrategy.OpenerAndBurstRecovery) && !_openerSlitherConsumed)
                {
                    var slitherThreshold = 0.7f - OpenerSlitherDelay();
                    if (CountdownRemaining < slitherThreshold && Unlocked(AID.Slither))
                    {
                        PushGCD(AID.Slither, primaryTarget);
                        // the push can be dropped (range/queue); only mark the opener Slither consumed once we actually saw it cast
                        if (Manager.LastCast.Data?.Action == ActionID.MakeSpell(AID.Slither) && (World.CurrentTime - Manager.LastCast.Time).TotalSeconds < 2)
                            _openerSlitherConsumed = true;
                    }
                }
            }
            else
            {
                if (_openerSlitherDelayArmed)
                    _openerSlitherConsumed = true;

                if (CountdownRemaining < 1.16f)
                {
                    if (ShouldUsePatch75ZeroSecondCountdownOpener(strategy))
                    {
                        var first = Patch75ZeroSecondFirstGCD();
                        if (first != AID.None)
                            PushGCD(first, primaryTarget);
                    }
                    else if (Unlocked(AID.SteelFangs))
                    {
                        PushGCD(AID.SteelFangs, primaryTarget);
                    }
                }
            }

            return;
        }

        // potion: decide once per frame whether we want it before the upcoming burst; the same flag gates the burst holds and the potion push
        var shouldReawaken = ShouldReawaken(strategy, primaryTarget);
        _wantPotionNow = CanUsePotion() && WantsPotionBeforeBurst(strategy);
        if (_wantPotionNow && shouldReawaken)
            shouldReawaken = ShouldReawaken(strategy, primaryTarget);
        var aoeBreakpoint = DreadCombo switch
        {
            DreadCombo.Dreadwinder or DreadCombo.HuntersCoil or DreadCombo.SwiftskinsCoil => 50,
            DreadCombo.HuntersDen or DreadCombo.SwiftskinsDen or DreadCombo.PitOfDread => 1,
            _ => Anguine > 0 ? 50 : 3
        };

        if (ShouldUsePatch75ZeroSecondOpener(strategy))
            QueuePatch75ZeroSecondOpenerGCD(primaryTarget);

        if (shouldReawaken)
            PushGCD(AID.Reawaken, Player, GCDPriorityReawaken);

        var dreadComboPriority = ShouldFinishDreadComboBeforeEvenBurst(strategy) ? GCDPriorityDreadComboBeforeEvenBurst : GCDPriorityDreadCombo;

        if (Anguine == 0 && DreadCombo == DreadCombo.HuntersCoil && Unlocked(AID.SwiftskinsCoil))
            PushGCD(AID.SwiftskinsCoil, primaryTarget, dreadComboPriority);

        if (Anguine == 0 && DreadCombo == DreadCombo.SwiftskinsCoil && Unlocked(AID.HuntersCoil))
            PushGCD(AID.HuntersCoil, primaryTarget, dreadComboPriority);

        if (Anguine == 0 && DreadCombo == DreadCombo.Dreadwinder)
        {
            if (ShouldPreferHuntersCoilAfterDreadwinder(strategy, primaryTarget))
            {
                if (Unlocked(AID.HuntersCoil))
                    PushGCD(AID.HuntersCoil, primaryTarget, dreadComboPriority);
                else if (Unlocked(AID.SwiftskinsCoil))
                    PushGCD(AID.SwiftskinsCoil, primaryTarget, dreadComboPriority);
            }
            else
            {
                if (Unlocked(AID.SwiftskinsCoil))
                    PushGCD(AID.SwiftskinsCoil, primaryTarget, dreadComboPriority);
                else if (Unlocked(AID.HuntersCoil))
                    PushGCD(AID.HuntersCoil, primaryTarget, dreadComboPriority);
            }
        }

        // if no target, no buff
        if (Anguine == 0 && DreadCombo == DreadCombo.HuntersDen && Unlocked(AID.SwiftskinsDen))
            PushGCD(AID.SwiftskinsDen, Player, dreadComboPriority);

        if (Anguine == 0 && DreadCombo == DreadCombo.SwiftskinsDen && Unlocked(AID.HuntersDen))
            PushGCD(AID.HuntersDen, Player, dreadComboPriority);

        if (Anguine == 0 && DreadCombo == DreadCombo.PitOfDread)
        {
            if (Swiftscaled < Instinct)
            {
                if (Unlocked(AID.SwiftskinsDen))
                    PushGCD(AID.SwiftskinsDen, Player, dreadComboPriority);
                else if (Unlocked(AID.HuntersDen))
                    PushGCD(AID.HuntersDen, Player, dreadComboPriority);
            }
            else
            {
                if (Unlocked(AID.HuntersDen))
                    PushGCD(AID.HuntersDen, Player, dreadComboPriority);
                else if (Unlocked(AID.SwiftskinsDen))
                    PushGCD(AID.SwiftskinsDen, Player, dreadComboPriority);
            }
        }

        if (Anguine > 0 && Unlocked(AID.FirstGeneration))
        {
            var max = Unlocked(TraitID.EnhancedSerpentsLineage) && Unlocked(AID.Ouroboros) ? 5 : 4;
            var generation = (max - Anguine) switch
            {
                0 => AID.FirstGeneration,
                1 => AID.SecondGeneration,
                2 => AID.ThirdGeneration,
                3 => AID.FourthGeneration,
                4 when Unlocked(AID.Ouroboros) => AID.Ouroboros,
                _ => AID.None
            };

            if (generation != AID.None && Unlocked(generation))
                PushGCD(generation, BestGenerationTarget ?? primaryTarget, GCDPriorityReawaken);

            // pushed out of melee mid-sequence: Uncoiled Fury does not break the Generation combo (in replays the Generation after it
            // lands with its combo potency), so a coil fills the GCD the sequence would otherwise idle
            if (ShouldUseUncoiledFuryForRangeDuringReawaken(strategy, BestGenerationTarget ?? primaryTarget))
                PushGCD(AID.UncoiledFury, BestRangedAOETarget ?? primaryTarget, GCDPriorityCoil);
        }

        // hold the GCD for pending oGCD follow-ups / Serpent's Ire only while there is still GCD time left;
        // once the GCD is (almost) up, fall through so the rotation can never stall on this wait
        if (GCD > 0.5f && (ShouldWaitForSecondSerpentsIreReawakenFollowUp() || ShouldWaitForSerpentsIreEvenBurstStart(strategy, shouldReawaken)))
        {
            var waitPos = GetPositional(strategy, primaryTarget);
            UpdatePositionals(primaryTarget, ref waitPos);
            OGCD(strategy, primaryTarget, shouldReawaken);
            GoalZoneCombined(strategy, 3, Hints.GoalAOECircle(5), AID.SteelMaw, aoeBreakpoint, 20);
            return;
        }

        if (Anguine == 0)
        {
            if (ShouldUseUncoiledFuryInRaidBuff(strategy, primaryTarget, shouldReawaken))
                PushGCD(AID.UncoiledFury, BestRangedAOETarget ?? primaryTarget, GCDPriorityRaidBuffCoil);
            else if (ShouldCoil(strategy, primaryTarget, shouldReawaken))
                PushGCD(AID.UncoiledFury, BestRangedAOETarget ?? primaryTarget, GCDPriorityCoil);

            // 123 combos
            // 1. 34606 steel fangs (left)
            //    34607 dread fangs (right)
            //   use right to refresh debuff, otherwise left
            //
            // 2. 34608 hunter (left) damage buff
            //    34609 swiftskin (right) haste buff
            //   pick one based on buff timer, if both are 0 then choose your favorite
            //
            // 3. 34610 flank strike (left) (combos from hunter)
            //    34612 hind strike (left) (combos from swift)
            //    34611 flank fang (right) (combos from hunter)
            //    34613 hind fang (right) (combos from swift)
            //   each action buffs the next one in a loop

            var useAOERotation = ShouldUseAOERotation();
            if (useAOERotation)
            {
                if (ShouldVice(strategy, primaryTarget, shouldReawaken, AID.Vicepit))
                    PushGCD(AID.Vicepit, Player, GCDPriorityVice);
                else if (!Unlocked(AID.Vicepit) && ShouldVice(strategy, primaryTarget, shouldReawaken, AID.Vicewinder))
                    PushGCD(AID.Vicewinder, primaryTarget, GCDPriorityVice);

                if (ComboLastMove is AID.HuntersBite or AID.SwiftskinsBite)
                {
                    if (GrimskinsVenom > GCD && Unlocked(AID.BloodiedMaw))
                        PushGCD(AID.BloodiedMaw, Player);

                    if (Unlocked(AID.JaggedMaw))
                        PushGCD(AID.JaggedMaw, Player);
                }

                if (ComboLastMove is AID.SteelMaw or AID.ReavingMaw)
                {
                    var next = SelectAOESecondComboGCD();
                    if (next != AID.None)
                        PushGCD(next, Player);
                }

                if (HonedSteel == 0 && Unlocked(AID.ReavingMaw))
                    PushGCD(AID.ReavingMaw, Player);

                if (Unlocked(AID.SteelMaw))
                    PushGCD(AID.SteelMaw, Player);
            }
            else
            {
                if (ShouldVice(strategy, primaryTarget, shouldReawaken, AID.Vicewinder))
                    PushGCD(AID.Vicewinder, primaryTarget, GCDPriorityVice);

                if (ComboLastMove is AID.HuntersSting)
                {
                    if (FlankstungVenom > GCD && Unlocked(AID.FlankstingStrike))
                        PushGCD(AID.FlankstingStrike, primaryTarget);

                    if (Unlocked(AID.FlanksbaneFang))
                        PushGCD(AID.FlanksbaneFang, primaryTarget);
                }

                if (ComboLastMove is AID.SwiftskinsSting)
                {
                    if (HindstungVenom > GCD && Unlocked(AID.HindstingStrike))
                        PushGCD(AID.HindstingStrike, primaryTarget);

                    if (Unlocked(AID.HindsbaneFang))
                        PushGCD(AID.HindsbaneFang, primaryTarget);
                }

                if (ComboLastMove is AID.SteelFangs or AID.ReavingFangs)
                {
                    var next = SelectSingleSecondComboGCD();
                    if (next != AID.None)
                        PushGCD(next, primaryTarget);
                }

                if (HonedSteel == 0 && Unlocked(AID.ReavingFangs))
                    PushGCD(AID.ReavingFangs, primaryTarget);

                if (Unlocked(AID.SteelFangs))
                    PushGCD(AID.SteelFangs, primaryTarget);
            }

            // fallback for out of range
            if (ShouldUseUncoiledFuryForRange(strategy, primaryTarget, shouldReawaken))
                PushGCD(AID.UncoiledFury, BestRangedAOETarget ?? primaryTarget, GCDPriorityCoil);

            // fallback 2 for out of range
            if (ShouldUseWrithingSnapForRange(strategy, primaryTarget))
                PushGCD(AID.WrithingSnap, ResolveTargetOverride(strategy.Snap) ?? primaryTarget);
        }

        var pos = GetPositional(strategy, primaryTarget);
        UpdatePositionals(primaryTarget, ref pos);

        OGCD(strategy, primaryTarget, shouldReawaken);

        GoalZoneCombined(strategy, 3, Hints.GoalAOECircle(5), AID.SteelMaw, aoeBreakpoint, 20);
    }

    private float OpenerSlitherDelay()
    {
        if (!_openerSlitherDelayArmed)
        {
            // seeded from the frame index like Basexan's countdown wait, so a replay reproduces the same delay
            _openerSlitherDelay = 0.2f + new Random((int)World.Frame.Index).NextSingle() * 0.1f;
            _openerSlitherDelayArmed = true;
        }

        return _openerSlitherDelay;
    }

    private bool ShouldUsePatch75ZeroSecondOpener(in Strategy strategy)
    {
        return strategy.OpenerBurst == OpenerBurstStrategy.Patch75ZeroSecond
            && strategy.Buffs != OffensiveStrategy.Delay
            && strategy.SerpentsIre != SerpentsIreStrategy.Off
            && Unlocked(AID.Reawaken)
            && Unlocked(AID.SerpentsIre)
            && Player.InCombat
            && CombatTimer <= Patch75ZeroOpenerEnd;
    }

    private bool ShouldUsePatch75ZeroSecondCountdownOpener(in Strategy strategy)
    {
        return strategy.OpenerBurst == OpenerBurstStrategy.Patch75ZeroSecond
            && strategy.Buffs != OffensiveStrategy.Delay
            && strategy.SerpentsIre != SerpentsIreStrategy.Off
            && Unlocked(AID.Reawaken)
            && Unlocked(AID.SerpentsIre)
            && Unlocked(AID.Vicewinder);
    }

    private AID Patch75ZeroSecondFirstGCD()
    {
        if (Unlocked(AID.Vicewinder))
            return AID.Vicewinder;

        return AID.None;
    }

    private void QueuePatch75ZeroSecondOpenerGCD(Enemy primaryTarget)
    {
        if (ReawakenLeft > 0 || Anguine > 0)
            return;

        if (DreadCombo == 0 && ComboLastMove == AID.None)
        {
            if (Unlocked(AID.Vicewinder) && _viceReadyIn <= GCD)
                PushGCD(AID.Vicewinder, primaryTarget, GCDPriorityPatch75Opener);
        }

        if (DreadCombo == DreadCombo.Dreadwinder)
        {
            if (Unlocked(AID.SwiftskinsCoil))
                PushGCD(AID.SwiftskinsCoil, primaryTarget, GCDPriorityPatch75Opener);
            else if (Unlocked(AID.HuntersCoil))
                PushGCD(AID.HuntersCoil, primaryTarget, GCDPriorityPatch75Opener);
        }

        if (DreadCombo == DreadCombo.SwiftskinsCoil)
        {
            if (Unlocked(AID.HuntersCoil))
                PushGCD(AID.HuntersCoil, primaryTarget, GCDPriorityPatch75Opener);
        }
    }

    private void ResetOpenerSlitherDelay()
    {
        _openerSlitherDelay = -1f;
        _openerSlitherDelayArmed = false;
        _openerSlitherConsumed = false;
    }

    private void ResetSerpentsIreDoubleReawaken()
    {
        _serpentsIreReawakenGoal = 0;
        _serpentsIreReawakenStarted = 0;
    }

    private void UpdateSerpentsIreDoubleReawakenState()
    {
        if (!Player.InCombat)
        {
            ResetSerpentsIreDoubleReawaken();
            _lastReawakenLeft = ReawakenLeft;
            return;
        }

        // Serpent's Ire 由来の Ready to Reawaken を検知したら、
        // このバーストでは2回Reawakenを狙う。
        // ReawakenReady は Serpent's Ire 後のfree Reawaken条件として扱う。
        if (Unlocked(AID.Reawaken) && ReawakenReady > 0 && _serpentsIreReawakenGoal == 0)
        {
            _serpentsIreReawakenGoal = SerpentsIreReawakenGoal;
            _serpentsIreReawakenStarted = 0;
        }

        // Reawakened が 0 -> >0 になった瞬間を Reawaken開始として数える。
        if (_serpentsIreReawakenGoal > 0 && _lastReawakenLeft <= 0 && ReawakenLeft > 0)
        {
            ++_serpentsIreReawakenStarted;

            if (_serpentsIreReawakenStarted >= _serpentsIreReawakenGoal)
                ResetSerpentsIreDoubleReawaken();
        }

        // Ready to Reawaken expired before the first Reawaken started: drop the stale reservation
        // (skip the frame right after a Reawaken cast, where the Reawakened status may not have arrived yet).
        if (_serpentsIreReawakenGoal > 0
            && _serpentsIreReawakenStarted == 0
            && ReawakenReady <= 0
            && ReawakenLeft <= 0
            && Anguine == 0
            && Manager.LastCast.Data?.Action != ActionID.MakeSpell(AID.Reawaken))
            ResetSerpentsIreDoubleReawaken();

        // 2回目に必要なOfferingがなく、Readyもなく、Reawaken中でもないなら古い予約を捨てる。
        // ただし1回目完走直後にOffering>=50なら予約を残す。
        if (_serpentsIreReawakenGoal > 0 && ReawakenReady <= 0 && Offering < 50 && ReawakenLeft <= 0 && Anguine == 0)
            ResetSerpentsIreDoubleReawaken();

        _lastReawakenLeft = ReawakenLeft;
    }

    private bool HasPendingSerpentsTailOrTwinFollowUp()
    {
        return CurSerpentsTail != AID.SerpentsTail
            || TwinCombo != TwinType.None
            || PoisedForTwinfang > 0
            || PoisedForTwinblood > 0;
    }

    private bool ShouldWaitForSecondSerpentsIreReawakenFollowUp()
    {
        return _serpentsIreReawakenGoal > 0
            && _serpentsIreReawakenStarted == 1
            && ReawakenReady <= 0
            && Offering >= 50
            && ReawakenLeft <= 0
            && Anguine == 0
            && DreadCombo == 0
            && HasPendingSerpentsTailOrTwinFollowUp();
    }

    private bool ShouldForceSecondSerpentsIreReawaken()
    {
        return _serpentsIreReawakenGoal > 0
            && _serpentsIreReawakenStarted == 1
            && ReawakenReady <= 0
            && Offering >= 50
            && ReawakenLeft <= 0
            && Anguine == 0
            && DreadCombo == 0
            && !HasPendingSerpentsTailOrTwinFollowUp();
    }

    private bool CanLandMeleeGCDForOfferingPrediction(in Strategy strategy, Enemy? primaryTarget)
    {
        if (primaryTarget == null)
            return false;

        if (!IsOutOfMeleeRange(primaryTarget))
            return true;

        if (CannotResumeMeleeWithinOneGCD(primaryTarget))
            return strategy.Slither == SlitherStrategy.OpenerAndBurstRecovery
                && Unlocked(AID.Slither)
                && Player.InCombat
                && ReadyIn(AID.Slither) <= GCD;

        return false;
    }

    private int PredictedOfferingGainFromNextGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Unlocked(AID.Reawaken))
            return 0;

        if (ReawakenLeft > 0 || Anguine > 0)
            return 0;

        // DreadCombo中は既存方針通り Reawaken で割り込まない。
        // ここでは通常コンボ側の次GCD overcap だけを見る。
        if (DreadCombo != 0)
            return 0;

        if (!CanLandMeleeGCDForOfferingPrediction(strategy, primaryTarget))
            return 0;

        if (NextGCD is AID.HuntersCoil or AID.SwiftskinsCoil)
            return 5;

        // 通常単体3段目: +10
        if (ComboLastMove == AID.HuntersSting)
        {
            if (FlankstungVenom > GCD && Unlocked(AID.FlankstingStrike))
                return 10;

            if (Unlocked(AID.FlanksbaneFang))
                return 10;
        }

        if (ComboLastMove == AID.SwiftskinsSting)
        {
            if (HindstungVenom > GCD && Unlocked(AID.HindstingStrike))
                return 10;

            if (Unlocked(AID.HindsbaneFang))
                return 10;
        }

        // 通常範囲3段目: +10
        if (ComboLastMove is AID.HuntersBite or AID.SwiftskinsBite)
        {
            if (GrimskinsVenom > GCD && Unlocked(AID.BloodiedMaw))
                return 10;

            if (Unlocked(AID.JaggedMaw))
                return 10;
        }

        return 0;
    }

    private int PredictedOfferingGainWithinNextGCDs(in Strategy strategy, Enemy? primaryTarget, int gcdCount)
    {
        if (gcdCount <= 0)
            return 0;

        var gain = PredictedOfferingGainFromNextGCD(strategy, primaryTarget);
        if (gain > 0 || gcdCount == 1)
            return gain;

        if (!CanLandMeleeGCDForOfferingPrediction(strategy, primaryTarget))
            return 0;

        if (ComboLastMove is AID.SteelFangs or AID.ReavingFangs)
            return gcdCount >= 2 && (Unlocked(AID.HuntersSting) || Unlocked(AID.SwiftskinsSting)) ? 10 : 0;

        if (ComboLastMove is AID.None or AID.FlankstingStrike or AID.FlanksbaneFang or AID.HindstingStrike or AID.HindsbaneFang)
            return gcdCount >= 3 && Unlocked(AID.SteelFangs) && (Unlocked(AID.HuntersSting) || Unlocked(AID.SwiftskinsSting)) ? 10 : 0;

        if (ComboLastMove is AID.SteelMaw or AID.ReavingMaw)
            return gcdCount >= 2 && (Unlocked(AID.HuntersBite) || Unlocked(AID.SwiftskinsBite)) ? 10 : 0;

        return 0;
    }

    private bool WillOfferingOvercapOnNextGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        var gain = PredictedOfferingGainFromNextGCD(strategy, primaryTarget);
        return gain > 0 && Offering + gain > 100;
    }

    private bool WillOfferingOvercapSoon(in Strategy strategy, Enemy? primaryTarget, int gcdCount)
    {
        var gain = PredictedOfferingGainWithinNextGCDs(strategy, primaryTarget, gcdCount);
        return gain > 0 && Offering + gain > 100;
    }

    private bool ShouldHoldReawakenForSerpentsIre(in Strategy strategy)
    {
        if (strategy.SerpentsIre == SerpentsIreStrategy.Off)
            return false;

        if (!Unlocked(AID.SerpentsIre) || !Unlocked(AID.Reawaken))
            return false;

        if (ReawakenReady > 0)
            return false;

        if (Offering < 50)
            return false;

        // hold only when Serpent's Ire is about to come up (its free Reawaken beats spending Offering now);
        // the old tail (CanUseSerpentsIreWithoutOverflow) also required Ire ready, so it could never be reached with a different result
        return _ireReadyIn <= GCD
            && DreadCombo == 0
            && ReawakenLeft <= 0
            && Anguine == 0;
    }

    private bool CanSpendOfferingWithoutBreakingEvenDoubleReawaken(in Strategy strategy, Enemy? primaryTarget)
    {
        if (ReawakenReady > 0)
            return true;

        if (Offering < 50)
            return false;

        if (_serpentsIreReawakenGoal > 0)
            return ShouldForceSecondSerpentsIreReawaken();

        if (Offering >= 100)
            return true;

        var evenBurstIn = _evenBurstIn;
        if (evenBurstIn == float.MaxValue)
            return true;

        return PredictedOfferingAtSerpentsIre(strategy, primaryTarget, spendReawakenNow: true) >= 50;
    }

    private int PredictedOfferingGainUntilSerpentsIreWithoutPlannedVice(in Strategy strategy, Enemy? primaryTarget)
    {
        var serpentsIreIn = _evenBurstIn;
        if (serpentsIreIn == float.MaxValue || serpentsIreIn <= GCD)
            return 0;

        if (!CanLandMeleeGCDForOfferingPrediction(strategy, primaryTarget))
            return 0;

        var gcdCount = Math.Max(0, (int)MathF.Floor((serpentsIreIn - 0.25f) / AttackGCDLength));
        if (gcdCount <= 0)
            return 0;

        if (DreadCombo != 0)
        {
            var remaining = Math.Min(gcdCount, DreadComboGCDsRemaining());
            return remaining * 5;
        }

        return PredictedOfferingGainWithinNextGCDs(strategy, primaryTarget, gcdCount);
    }

    private int PredictedViceOfferingGainBeforeSerpentsIre(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!CanUseViceForPreEvenBuffRefreshTiming(strategy, primaryTarget))
            return 0;

        var serpentsIreIn = _evenBurstIn;
        var gcdCount = Math.Max(0, (int)MathF.Floor((serpentsIreIn - 0.25f) / AttackGCDLength));
        if (gcdCount < 3)
            return 0;

        var gain = 10;
        if (gcdCount >= 6)
            gain += 10;

        return gain;
    }

    private bool ShouldPlanViceForCoreBuffOrOffering(in Strategy strategy, Enemy? primaryTarget, int startingOffering)
    {
        if (!CanUseViceForPreEvenBuffRefreshTiming(strategy, primaryTarget))
            return false;

        var normalGain = PredictedOfferingGainUntilSerpentsIreWithoutPlannedVice(strategy, primaryTarget);
        var viceGain = PredictedViceOfferingGainBeforeSerpentsIre(strategy, primaryTarget);
        if (viceGain <= 0)
            return false;

        var withoutVice = Math.Clamp(startingOffering + normalGain, 0, 100);
        var withVice = Math.Clamp(startingOffering + Math.Max(normalGain, viceGain), 0, 100);
        var needsCoreBuffRefresh = !WillCoreBuffsLastThroughEvenDoubleReawaken(strategy);
        var needsOfferingPreparation = withoutVice < 50 && withVice >= 50;

        return withVice >= 50 && (needsCoreBuffRefresh || needsOfferingPreparation);
    }

    private int PredictedOfferingGainUntilSerpentsIre(in Strategy strategy, Enemy? primaryTarget, int startingOffering)
    {
        var normalGain = PredictedOfferingGainUntilSerpentsIreWithoutPlannedVice(strategy, primaryTarget);
        if (!ShouldPlanViceForCoreBuffOrOffering(strategy, primaryTarget, startingOffering))
            return normalGain;

        return Math.Max(normalGain, PredictedViceOfferingGainBeforeSerpentsIre(strategy, primaryTarget));
    }

    private int PredictedOfferingAtSerpentsIre(in Strategy strategy, Enemy? primaryTarget, bool spendReawakenNow)
    {
        var predicted = Offering;
        if (spendReawakenNow && ReawakenReady <= 0)
            predicted -= 50;

        predicted += PredictedOfferingGainUntilSerpentsIre(strategy, primaryTarget, predicted);
        return Math.Clamp(predicted, 0, 100);
    }

    private bool HasTrueNorthShortageForUpcomingPositional(in Strategy strategy, Enemy? primaryTarget, float fillerDuration)
    {
        if (strategy.TrueNorth != TrueNorthStrategy.Auto)
            return false;

        if (primaryTarget == null)
            return false;

        if (TrueNorthLeft > 0)
            return false;

        if (!Unlocked(AID.TrueNorth))
            return false;

        // NextGCD / NextPositionalImminent are only valid after this frame's GCD pushes (Basexan resets them every frame),
        // so derive the upcoming positional from the combo state and check our current position directly
        var target = primaryTarget.Actor;
        if (target.Omnidirectional || primaryTarget.Priority < 0 || target.TargetID == Player.InstanceID && target.CastInfo == null && !target.IsStrikingDummy)
            return false;

        var timeToPositional = float.MaxValue;
        var (upcomingPos, upcomingImm) = PositionalFromComboState(strategy, primaryTarget);
        if (upcomingImm && upcomingPos != Positional.Any && GetCurrentPositional(target) != upcomingPos)
            timeToPositional = GCD;

        var viceReadySoon = (Unlocked(AID.Vicewinder) && _viceReadyIn <= AttackGCDLength)
            || (Unlocked(AID.Vicepit) && ReadyIn(AID.Vicepit) <= AttackGCDLength);
        if (viceReadySoon
            && !IsViceForbiddenNearEvenBurst(strategy)
            && Unlocked(AID.HuntersCoil)
            && Unlocked(AID.SwiftskinsCoil))
            timeToPositional = MathF.Min(timeToPositional, AttackGCDLength);

        if (timeToPositional == float.MaxValue)
            return false;

        // a filler only helps if it actually lets True North come back before the positional; otherwise we would just burn resources every GCD while it is on cooldown
        var trueNorthIn = ReadyIn(AID.TrueNorth);
        return trueNorthIn > timeToPositional && trueNorthIn <= timeToPositional + fillerDuration;
    }

    private bool CanUseReawakenForTrueNorthShortageWithoutDroppingCoreBuffs()
    {
        var required = ReawakenSequenceDuration() + AttackGCDLength * 3.0f + 0.5f;
        return Instinct > required && Swiftscaled > required;
    }

    private bool ShouldUseReawakenForTrueNorthShortage(in Strategy strategy, Enemy? primaryTarget)
    {
        if (ShouldForceSecondSerpentsIreReawaken() || ShouldWaitForSecondSerpentsIreReawakenFollowUp())
            return false;

        if (!HasTrueNorthShortageForUpcomingPositional(strategy, primaryTarget, ReawakenSequenceDuration()))
            return false;

        if (_planVice)
            return false;

        if (ShouldForceSerpentsIreForDoubleReawaken(strategy))
            return false;

        if (!Unlocked(AID.Reawaken) || !Unlocked(AID.TrueNorth))
            return false;

        if (ReawakenReady <= 0 && Offering < 50)
            return false;

        if (ReawakenLeft > 0 || Anguine > 0 || DreadCombo != 0)
            return false;

        if (WillComboDropIfDelay(ReawakenSequenceDuration() + 0.25f))
            return false;

        if (!CanUseReawakenForTrueNorthShortageWithoutDroppingCoreBuffs())
            return false;

        return CanSpendOfferingWithoutBreakingEvenDoubleReawaken(strategy, primaryTarget);
    }

    private bool ShouldWaitForSerpentsIreEvenBurstStart(in Strategy strategy, bool shouldReawaken)
    {
        if (strategy.SerpentsIre == SerpentsIreStrategy.Off)
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay)
            return false;

        if (Offering < 50 || ReawakenReady > 0 || ReawakenLeft > 0 || Anguine > 0 || DreadCombo != 0)
            return false;

        if (shouldReawaken)
            return false;

        if (HasPendingSerpentsTailOrTwinFollowUp())
            return false;

        // (ShouldClearCoilBeforeSerpentsIre is false whenever Ire is ready, so it cannot change the result here)
        return CombatTimer > 20 && CanUseSerpentsIreNow();
    }

    private int DreadComboGCDsRemaining()
    {
        return DreadCombo switch
        {
            DreadCombo.Dreadwinder or DreadCombo.PitOfDread => 2,
            DreadCombo.HuntersCoil or DreadCombo.SwiftskinsCoil or DreadCombo.HuntersDen or DreadCombo.SwiftskinsDen => 1,
            _ => 0
        };
    }

    private bool ShouldFinishDreadComboBeforeEvenBurst(in Strategy strategy)
    {
        if (strategy.SerpentsIre == SerpentsIreStrategy.Off)
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay)
            return false;

        if (!Unlocked(AID.SerpentsIre) || !Unlocked(AID.Reawaken))
            return false;

        if (CombatTimer <= 20)
            return false;

        if (ReawakenLeft > 0 || Anguine > 0)
            return false;

        var remaining = DreadComboGCDsRemaining();
        if (remaining <= 0)
            return false;

        var evenBurstIn = _evenBurstIn;
        if (evenBurstIn == float.MaxValue)
            return false;

        var finishTime = remaining * AttackGCDLength + 0.25f;
        return evenBurstIn <= finishTime;
    }

    private bool ShouldReawaken(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Unlocked(AID.Reawaken))
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay)
            return false;

        if (ReawakenReady == 0 && Offering < 50)
            return false;

        if (ReawakenLeft > 0 || Anguine > 0)
            return false;

        if (DreadCombo > 0)
            return false;

        if (NumAOETargets == 0)
            return false;

        // spec rule 3: a short out-of-melee dodge would cut the sequence
        if (Mechanic.Enabled && Mechanic.RangeLossIn < ReawakenSequenceDuration())
            return false;

        // FightRemaining (value-of-information experiment): the even burst will not happen, so spend the Offering now
        if (VoiSpendOfferingBeforeEnd && VoiFightEndsBeforeEvenBurst()
            && Instinct >= MathF.Min(ReawakenSequenceDuration(), Hints.FightRemaining.UpperBound)
            && Swiftscaled >= MathF.Min(ReawakenSequenceDuration(), Hints.FightRemaining.UpperBound))
            return true;

        if (ShouldForceSecondSerpentsIreReawaken())
            return true;

        if (ShouldWaitForSecondSerpentsIreReawakenFollowUp())
            return false;

        if (ReawakenReady <= 0 && ShouldForceSerpentsIreForDoubleReawaken(strategy))
            return false;

        var willOfferingOvercap = WillOfferingOvercapOnNextGCD(strategy, primaryTarget);
        var offeringHardCap = Offering >= 100;
        var canSpendOffering = CanSpendOfferingWithoutBreakingEvenDoubleReawaken(strategy, primaryTarget);

        if (offeringHardCap || ((willOfferingOvercap || WillOfferingOvercapSoon(strategy, primaryTarget, 3)) && canSpendOffering))
            return true;

        if (ShouldClearCoilBeforeSerpentsIre(strategy))
            return false;

        if (_planVice)
            return false;

        if (ShouldUseReawakenForTrueNorthShortage(strategy, primaryTarget))
            return true;

        var actual = ReawakenSequenceDuration();
        if (Instinct < actual || Swiftscaled < actual)
            return false;

        if (_wantPotionNow)
            return false;

        if (strategy.Buffs != OffensiveStrategy.Force && ShouldHoldReawakenForSerpentsIre(strategy))
            return false;

        if (strategy.Buffs == OffensiveStrategy.Force)
            return true;

        return ReawakenReady > 0;
    }

    private float ReawakenSequenceDuration()
    {
        // Reawaken plus the four Generation GCDs; Ouroboros is added for Enhanced Serpent's Lineage.
        var baseDuration = 8.2f;
        if (Unlocked(TraitID.EnhancedSerpentsLineage) && Unlocked(AID.Ouroboros))
            baseDuration += 2;

        return baseDuration * AttackGCDLength / 2.5f;
    }

    private float TimeToEvenBurstWindow(in Strategy strategy)
    {
        if (strategy.Buffs == OffensiveStrategy.Delay)
            return float.MaxValue;

        if (!Unlocked(AID.Reawaken) || !Unlocked(AID.SerpentsIre))
            return float.MaxValue;

        if (CombatTimer <= 20)
            return float.MaxValue;

        var serpentsIreIn = strategy.SerpentsIre == SerpentsIreStrategy.Off
            ? float.MaxValue
            : _ireReadyIn;

        return serpentsIreIn;
    }

    private float RequiredCoreBuffDurationThroughEvenDoubleReawaken()
    {
        return AttackGCDLength * 2.0f + ReawakenSequenceDuration() * 2.0f + EvenBurstPrepBuffer;
    }

    private bool WillCoreBuffsLastThroughEvenDoubleReawaken(in Strategy strategy)
    {
        var evenBurstIn = _evenBurstIn;
        if (evenBurstIn == float.MaxValue)
            return true;

        var required = RequiredCoreBuffDurationThroughEvenDoubleReawaken();
        return Instinct >= evenBurstIn + required
            && Swiftscaled >= evenBurstIn + required;
    }

    private bool CanUseUncoiledFuryWithoutDroppingCoreBuffs()
    {
        var required = AttackGCDLength * 3.0f + 0.5f;
        return Instinct > required && Swiftscaled > required;
    }

    // core buffs (Hunter's Instinct / Swiftscaled) run out within the next few GCDs: refresh takes priority over coil spending
    // (single threshold; the former "now" variant at 2 GCDs was a strict subset of this one)
    private bool ShouldPrioritizeCoreBuffRefresh()
    {
        if (ReawakenLeft > 0 || Anguine > 0 || DreadCombo != 0)
            return false;

        var required = AttackGCDLength * 3.0f + 0.5f;
        if (Instinct > required && Swiftscaled > required)
            return false;

        if (Unlocked(AID.Vicewinder) && _viceReadyIn <= GCD)
            return true;

        return ComboLastMove is AID.None
            or AID.SteelFangs
            or AID.ReavingFangs
            or AID.HuntersSting
            or AID.SwiftskinsSting;
    }

    private bool CanDelayViceOneGCDForTrueNorthCluster(in Strategy strategy, Enemy? primaryTarget)
    {
        if (_planVice)
            return false;

        var required = AttackGCDLength * 4.0f + 1.0f;
        return Instinct > required && Swiftscaled > required;
    }

    private AID SelectSingleThirdComboGCD()
    {
        if (ComboLastMove == AID.HuntersSting)
        {
            if (FlankstungVenom > GCD && Unlocked(AID.FlankstingStrike))
                return AID.FlankstingStrike;

            if (Unlocked(AID.FlanksbaneFang))
                return AID.FlanksbaneFang;
        }

        if (ComboLastMove == AID.SwiftskinsSting)
        {
            if (HindstungVenom > GCD && Unlocked(AID.HindstingStrike))
                return AID.HindstingStrike;

            if (Unlocked(AID.HindsbaneFang))
                return AID.HindsbaneFang;
        }

        return AID.None;
    }

    private bool ShouldFinishBasicFangPositionalBeforeViceForTrueNorthCluster(in Strategy strategy, Enemy? primaryTarget, bool shouldReawaken)
    {
        if (ShouldUseAOERotation())
            return false;

        if (shouldReawaken || Anguine > 0 || ReawakenLeft > 0 || DreadCombo != 0)
            return false;

        if (ShouldForceSecondSerpentsIreReawaken() || ShouldWaitForSecondSerpentsIreReawakenFollowUp())
            return false;

        if (_planVice)
            return false;

        if (_wantPotionNow || ShouldUseSerpentsIre(strategy))
            return false;

        if (!CanDelayViceOneGCDForTrueNorthCluster(strategy, primaryTarget))
            return false;

        if (Instinct <= CoilBuffEmergency || Swiftscaled <= CoilBuffEmergency)
            return false;

        var third = SelectSingleThirdComboGCD();
        if (third is not (AID.FlanksbaneFang or AID.FlankstingStrike or AID.HindsbaneFang or AID.HindstingStrike))
            return false;

        if (ComboLastMove == AID.HuntersSting && FlankstungVenom > 0 && FlankstungVenom <= AttackGCDLength)
            return false;

        if (ComboLastMove == AID.SwiftskinsSting && HindstungVenom > 0 && HindstungVenom <= AttackGCDLength)
            return false;

        var viceReadySoon = Unlocked(AID.Vicewinder) && _viceReadyIn <= AttackGCDLength;
        if (!viceReadySoon)
            return false;

        if (!Unlocked(AID.HuntersCoil) || !Unlocked(AID.SwiftskinsCoil))
            return false;

        if (TrueNorthLeft > 0)
            return true;

        return strategy.TrueNorth == TrueNorthStrategy.Auto
            && Unlocked(AID.TrueNorth)
            && ReadyIn(AID.TrueNorth) <= GCD
            && GCD <= 2.20f
            && GCD >= 0.45f;
    }

    private bool IsViceForbiddenNearEvenBurst(in Strategy strategy)
    {
        var evenBurstIn = _evenBurstIn;
        if (evenBurstIn == float.MaxValue)
            return false;

        if (evenBurstIn > ViceNoStartBeforeSerpentsIre)
            return false;

        return ReawakenLeft <= 0
            && Anguine == 0
            && DreadCombo == 0;
    }

    private bool CanUseViceForPreEvenBuffRefreshTiming(in Strategy strategy, Enemy? primaryTarget)
    {
        if (_serpentsIreReawakenGoal > 0 && _serpentsIreReawakenStarted > 0)
            return false;

        if (!Unlocked(AID.Vicewinder) || !Unlocked(AID.HuntersCoil) || !Unlocked(AID.SwiftskinsCoil))
            return false;

        if (_viceReadyIn > GCD)
            return false;

        if (ReawakenLeft > 0 || Anguine > 0 || DreadCombo != 0)
            return false;

        if (ShouldForceSerpentsIreForDoubleReawaken(strategy))
            return false;

        if (!CanLandMeleeGCDForOfferingPrediction(strategy, primaryTarget))
            return false;

        var evenBurstIn = _evenBurstIn;
        if (evenBurstIn == float.MaxValue || evenBurstIn <= GCD)
            return false;

        var required = RequiredCoreBuffDurationThroughEvenDoubleReawaken();
        var maxViceStartBeforeIre = CoreBuffMaxDuration - required + AttackGCDLength;
        var viceChainDuration = AttackGCDLength * 3.0f + 0.5f;
        var minViceStartBeforeIre = MathF.Max(ViceNoStartBeforeSerpentsIre, viceChainDuration + 0.5f);

        return evenBurstIn > minViceStartBeforeIre && evenBurstIn <= maxViceStartBeforeIre;
    }

    private bool ShouldVice(in Strategy strategy, Enemy? primaryTarget, bool shouldReawaken, AID action)
    {
        if (!Unlocked(action))
            return false;

        if (ShouldForceSecondSerpentsIreReawaken())
            return false;

        if (WillComboDropIfDelay(AttackGCDLength))
            return false;

        if (DreadCombo != 0 || Anguine > 0 || shouldReawaken || ReadyIn(action) > GCD)
            return false;

        // spec rule 1: Vicewinder and its two Coils need three GCDs before the loss
        if (Mechanic.ReturnKnown && !Mechanic.DowntimeNow && Mechanic.TargetLossIn < 3 * AttackGCDLength)
            return false;

        if (action is AID.Vicewinder or AID.Vicepit)
        {
            if (IsViceForbiddenNearEvenBurst(strategy) || ShouldForceSerpentsIreForDoubleReawaken(strategy))
                return false;

            if (_planVice)
                return true;

            if (ShouldFinishBasicFangPositionalBeforeViceForTrueNorthCluster(strategy, primaryTarget, shouldReawaken))
                return false;

            if (ShouldHoldViceForSerpentsIre(strategy))
                return false;
        }

        return true;
    }

    private bool ShouldHoldViceForSerpentsIre(in Strategy strategy)
    {
        if (strategy.SerpentsIre == SerpentsIreStrategy.Off)
            return false;

        if (!Unlocked(AID.SerpentsIre) || !Unlocked(AID.Reawaken))
            return false;

        if (_ireReadyIn > HoldViceBeforeSerpentsIre)
            return false;

        if (Offering < 50)
            return false;

        return true;
    }

    private bool ShouldPreferHuntersCoilAfterDreadwinder(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Unlocked(AID.HuntersCoil))
            return false;

        if (!Unlocked(AID.SwiftskinsCoil))
            return true;

        if (_planVice)
        {
            if (Instinct < Swiftscaled)
                return true;

            if (Swiftscaled < Instinct)
                return false;
        }

        if (CombatTimer <= 20 || RaidBuffsLeft > GCD)
            return true;

        if (Instinct <= CoilBuffEmergency && Swiftscaled > CoilBuffEmergency)
            return true;

        if (Swiftscaled <= CoilBuffEmergency && Instinct > CoilBuffEmergency)
            return false;

        // standing on one side, open with the coil for the other: its True North goes into the free Vicewinder window and still covers
        // the second coil, instead of taking a twin's weave slot between the coils
        if (TrueNorthLeft <= 0 && primaryTarget?.Actor is { Omnidirectional: false } target && target.TargetID != Player.InstanceID)
        {
            var dot = target.Rotation.ToDirection().Dot((Player.Position - target.Position).Normalized());
            var onFlank = MathF.Abs(dot) < 0.7071067f;
            var onRear = dot < -0.7071068f;
            if (onFlank != onRear)
                return onRear;
        }

        return true;
    }

    private bool ShouldCoil(in Strategy strategy, Enemy? primaryTarget, bool shouldReawaken)
    {
        if (!Unlocked(AID.UncoiledFury) || Coil == 0 || Anguine > 0)
            return false;

        if (ShouldForceSecondSerpentsIreReawaken() || ShouldForceSerpentsIreForDoubleReawaken(strategy))
            return false;

        if (ShouldClearCoilBeforeSerpentsIre(strategy))
            return true;

        // FightRemaining (value-of-information experiment): the last GCDs: a Coil is worth more than a filler, whatever it costs the combo or the buffs
        if (VoiSpendCoilsBeforeEnd && !shouldReawaken && DreadCombo == 0 && VoiInCoilDumpWindow())
            return true;

        if (WillComboDropIfDelay(AttackGCDLength))
            return false;

        if (ShouldUseUncoiledFuryForTrueNorthShortage(strategy, primaryTarget, shouldReawaken))
            return true;

        if (HasPendingUncoiledFuryFollowUp())
            return false;

        if (Swiftscaled <= GCD)
            return false;

        if (shouldReawaken)
            return false;

        if (_planVice)
            return false;

        if (ShouldPrioritizeCoreBuffRefresh())
            return false;

        if (!CanUseUncoiledFuryWithoutDroppingCoreBuffs())
            return false;

        if (ShouldSpendCoilBeforeFutureCoilGain(strategy, primaryTarget))
            return true;

        return Coil == CoilMax;
    }

    private bool WillCoilOvercapSoon(in Strategy strategy)
    {
        if (!Unlocked(AID.UncoiledFury) || CoilMax <= 0)
            return false;

        if (Coil >= CoilMax)
            return true;

        var serpentsIreSoon = strategy.SerpentsIre != SerpentsIreStrategy.Off
            && Unlocked(AID.SerpentsIre)
            && _ireReadyIn <= AttackGCDLength * 2.0f;
        if (serpentsIreSoon && Coil >= CoilMax - 1)
            return true;

        var viceSoon = (Unlocked(AID.Vicewinder) && _viceReadyIn <= AttackGCDLength)
            || (Unlocked(AID.Vicepit) && ReadyIn(AID.Vicepit) <= AttackGCDLength);
        return viceSoon && Coil >= CoilMax - 1;
    }

    private bool ShouldSpendCoilBeforeFutureCoilGain(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!WillCoilOvercapSoon(strategy))
            return false;

        if (ReawakenLeft > 0 || Anguine > 0 || DreadCombo != 0)
            return false;

        if (ShouldForceSecondSerpentsIreReawaken() || ShouldWaitForSecondSerpentsIreReawakenFollowUp())
            return false;

        if (ShouldForceSerpentsIreForDoubleReawaken(strategy))
            return false;

        if (_planVice)
            return false;

        return CanUseUncoiledFuryWithoutDroppingCoreBuffs();
    }

    private bool ShouldUseUncoiledFuryForTrueNorthShortage(in Strategy strategy, Enemy? primaryTarget, bool shouldReawaken)
    {
        if (!Unlocked(AID.UncoiledFury) || Coil <= 0)
            return false;

        if (shouldReawaken || ReawakenLeft > 0 || Anguine > 0 || DreadCombo != 0)
            return false;

        if (ShouldForceSecondSerpentsIreReawaken() || ShouldWaitForSecondSerpentsIreReawakenFollowUp())
            return false;

        if (ShouldForceSerpentsIreForDoubleReawaken(strategy))
            return false;

        if (!HasTrueNorthShortageForUpcomingPositional(strategy, primaryTarget, AttackGCDLength))
            return false;

        if (_planVice)
            return false;

        if (!CanUseUncoiledFuryWithoutDroppingCoreBuffs())
            return false;

        if (HasPendingUncoiledFuryFollowUp())
            return false;

        return true;
    }

    // ComboLastMove never holds Uncoiled Fury (it is not a combo-chain weaponskill); a just-used Uncoiled Fury shows up as the
    // coil twin combo state / Poised statuses until its oGCD follow-ups are spent
    private bool HasPendingUncoiledFuryFollowUp()
    {
        return TwinCombo == TwinType.Coil || PoisedForTwinfang > 0 || PoisedForTwinblood > 0;
    }

    private bool IsOutOfMeleeRange(Enemy primaryTarget)
    {
        return Player.DistanceToHitbox(primaryTarget) > 3;
    }

    private bool ShouldUseSlitherForBurstRecovery(in Strategy strategy, Enemy primaryTarget, bool shouldReawaken)
    {
        if (strategy.Slither != SlitherStrategy.OpenerAndBurstRecovery)
            return false;

        if (!Unlocked(AID.Slither))
            return false;

        if (!Player.InCombat)
            return false;

        if (ReadyIn(AID.Slither) > GCD)
            return false;

        var evenBurstReawakenEntry = CombatTimer > 20
            && strategy.Buffs != OffensiveStrategy.Delay
            && DreadCombo == 0
            && ReawakenLeft <= 0
            && Anguine == 0
            && (ReawakenReady > 0 || shouldReawaken);

        if (Anguine <= 0 && ReawakenLeft <= 0 && !ShouldUseSlitherForPatch75ReawakenEntry(strategy, primaryTarget) && !evenBurstReawakenEntry)
            return false;

        if (!IsOutOfMeleeRange(primaryTarget))
            return false;

        return true;
    }

    private bool ShouldUseSlitherForPatch75ReawakenEntry(in Strategy strategy, Enemy primaryTarget)
    {
        if (strategy.Slither != SlitherStrategy.OpenerAndBurstRecovery)
            return false;

        if (!ShouldUsePatch75ZeroSecondOpener(strategy))
            return false;

        if (!Unlocked(AID.Slither))
            return false;

        if (ReadyIn(AID.Slither) > GCD)
            return false;

        if (ReawakenReady <= 0)
            return false;

        if (DreadCombo != 0)
            return false;

        if (ReawakenLeft > 0 || Anguine > 0)
            return false;

        if (!IsOutOfMeleeRange(primaryTarget))
            return false;

        return true;
    }

    private bool IsMeleeGCDBlockedNow(Enemy primaryTarget)
    {
        if (!IsOutOfMeleeRange(primaryTarget))
            return false;

        // 現在GCDが戻っている/ほぼ戻っているのに近接が届かない場合だけ、
        // 現在の近接GCDが止まっている扱いにする。
        return GCD <= WrithingSnapCurrentGCDBlocked;
    }

    private bool IsNextMeleeGCDLikelyBlocked(Enemy primaryTarget)
    {
        if (!IsOutOfMeleeRange(primaryTarget))
            return false;

        // このファイルには未来位置予測がないため、
        // 近接範囲外に加えて、3y境界からさらに離れている場合だけ
        // 次の近接GCDも止まりそうと近似する。
        return Player.DistanceToHitbox(primaryTarget) > WrithingSnapNextMeleeLikelyBlockedDistance;
    }

    private bool CannotResumeMeleeWithinOneGCD(Enemy primaryTarget)
    {
        return IsMeleeGCDBlockedNow(primaryTarget) && IsNextMeleeGCDLikelyBlocked(primaryTarget);
    }

    private bool ShouldUseWrithingSnapForRange(in Strategy strategy, Enemy primaryTarget)
    {
        if (strategy.Snap != SnapStrategy.Ranged)
            return false;

        if (!Unlocked(AID.WrithingSnap))
            return false;

        if (ShouldForceSecondSerpentsIreReawaken())
            return false;

        // 飛蛇の魂がある場合は、既存の UncoiledFury fallback に任せる。
        // 飛蛇の牙は Coil がない時の最後の ranged fallback。
        // (only when that fallback is actually enabled; otherwise coils must not block the last ranged option)
        if (Coil > 0 && strategy.UncoiledFuryRange == UncoiledFuryRangeStrategy.Auto)
            return false;

        // Reawaken sequence 中は割り込ませない。
        if (Anguine > 0 || ReawakenLeft > 0)
            return false;

        if (ShouldUsePatch75ZeroSecondOpener(strategy)
            && ReawakenReady > 0
            && DreadCombo == 0
            && ReawakenLeft <= 0
            && Anguine == 0)
            return false;

        if (!CannotResumeMeleeWithinOneGCD(primaryTarget))
            return false;

        return true;
    }

    private bool ShouldUseUncoiledFuryInRaidBuff(in Strategy strategy, Enemy? primaryTarget, bool shouldReawaken)
    {
        if (!Unlocked(AID.UncoiledFury) || Coil == 0)
            return false;

        if (ShouldForceSecondSerpentsIreReawaken() || ShouldForceSerpentsIreForDoubleReawaken(strategy))
            return false;

        if (shouldReawaken || Anguine > 0 || ReawakenLeft > 0)
            return false;

        if (DreadCombo != 0)
            return false;

        if (WillComboDropIfDelay(AttackGCDLength))
            return false;

        if (HasPendingUncoiledFuryFollowUp())
            return false;

        if (_planVice)
            return false;

        if (ShouldPrioritizeCoreBuffRefresh())
            return false;

        if (!CanUseUncoiledFuryWithoutDroppingCoreBuffs())
            return false;

        return RaidBuffsLeft > GCD;
    }

    private bool ShouldUseUncoiledFuryForRange(in Strategy strategy, Enemy primaryTarget, bool shouldReawaken)
    {
        if (strategy.UncoiledFuryRange != UncoiledFuryRangeStrategy.Auto)
            return false;

        if (!Unlocked(AID.UncoiledFury) || Coil == 0)
            return false;

        if (ShouldForceSecondSerpentsIreReawaken() || ShouldForceSerpentsIreForDoubleReawaken(strategy))
            return false;

        if (shouldReawaken || Anguine > 0 || ReawakenLeft > 0)
            return false;

        if (ShouldUsePatch75ZeroSecondOpener(strategy)
            && ReawakenReady > 0
            && DreadCombo == 0
            && ReawakenLeft <= 0
            && Anguine == 0)
            return false;

        if (!IsOutOfMeleeRange(primaryTarget))
            return false;

        if (!CannotResumeMeleeWithinOneGCD(primaryTarget))
            return false;

        return true;
    }

    private bool ShouldUseUncoiledFuryForRangeDuringReawaken(in Strategy strategy, Enemy generationTarget)
    {
        if (strategy.UncoiledFuryRange != UncoiledFuryRangeStrategy.Auto)
            return false;

        if (!Unlocked(AID.UncoiledFury) || Coil == 0)
            return false;

        // the weaponskill would replace a pending Legacy (or twin): weave it first
        if (HasPendingSerpentsTailOrTwinFollowUp())
            return false;

        // the rest of the sequence (and Uncoiled Fury's longer recast) must still fit in Reawakened
        if (ReawakenLeft < Anguine * AttackGCDLength + AttackGCDLength * 1.4f + 1.0f)
            return false;

        return CannotResumeMeleeWithinOneGCD(generationTarget);
    }

    private bool IsAOEComboInProgress()
    {
        return ComboLastMove is AID.SteelMaw or AID.ReavingMaw or AID.HuntersBite or AID.SwiftskinsBite;
    }

    private bool IsSingleTargetComboInProgress()
    {
        return ComboLastMove is AID.SteelFangs or AID.ReavingFangs or AID.HuntersSting or AID.SwiftskinsSting;
    }

    private AID SelectSingleSecondComboGCD()
    {
        if (!Unlocked(AID.HuntersSting))
            return Unlocked(AID.SwiftskinsSting) ? AID.SwiftskinsSting : AID.None;

        if (!Unlocked(AID.SwiftskinsSting))
            return AID.HuntersSting;

        // 現在の強化フィニッシャーに繋がる側を優先する。
        // Flank系Venomがあるなら HuntersSting 側。
        if (FlankstungVenom > GCD || FlanksbaneVenom > GCD)
            return AID.HuntersSting;

        // Hind/Rear系Venomがあるなら SwiftskinsSting 側。
        if (HindstungVenom > GCD || HindsbaneVenom > GCD)
            return AID.SwiftskinsSting;

        // Venomが無い、または初回など自由選択時だけバフ残りで選ぶ。
        return Instinct < Swiftscaled ? AID.HuntersSting : AID.SwiftskinsSting;
    }

    private AID SelectAOESecondComboGCD()
    {
        if (!Unlocked(AID.HuntersBite))
            return Unlocked(AID.SwiftskinsBite) ? AID.SwiftskinsBite : AID.None;

        if (!Unlocked(AID.SwiftskinsBite))
            return AID.HuntersBite;

        return Instinct < Swiftscaled ? AID.HuntersBite : AID.SwiftskinsBite;
    }

    private bool CanUseAOERotation()
    {
        return Unlocked(AID.SteelMaw);
    }

    private bool ShouldUseAOERotation()
    {
        if (!CanUseAOERotation())
            return false;

        if (IsAOEComboInProgress())
            return true;

        if (IsSingleTargetComboInProgress())
            return false;

        return NumAOETargets >= 3;
    }

    private bool IsComboActive()
    {
        return World.Client.ComboState.Remaining > 0 && ComboLastMove != AID.None;
    }

    private bool WillComboDropIfDelay(float delaySeconds)
    {
        return IsComboActive() && World.Client.ComboState.Remaining <= delaySeconds;
    }

    private float PotionReadyIn()
    {
        var potion = ActionDefinitions.Instance[ActionDefinitions.IDPotionDex];
        return potion?.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions) ?? float.MaxValue;
    }

    // a potion can only be planned for this burst if we actually carry one and it is neither active nor on cooldown
    // (ReadyIn is 0 for a never-used item, so the inventory check is what keeps the burst holds from waiting forever)
    private bool CanUsePotion()
    {
        if (PotionLeft > 0)
            return false;

        if (World.Client.GetInventoryItemQuantity(ActionDefinitions.IDPotionDex.ID) == 0)
            return false;

        return PotionReadyIn() <= 0.1f;
    }

    private bool CanUseSerpentsIreNow()
    {
        return Unlocked(AID.SerpentsIre) && _ireReadyIn <= GCD;
    }

    private bool CanUseSerpentsIreWithoutOverflow()
    {
        return CanUseSerpentsIreNow()
            && CoilMax > 0
            && Coil < CoilMax;
    }

    private bool ShouldClearCoilBeforeSerpentsIre(in Strategy strategy)
    {
        if (_serpentsIreReawakenGoal > 0 && _serpentsIreReawakenStarted > 0)
            return false;

        if (strategy.SerpentsIre == SerpentsIreStrategy.Off)
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay)
            return false;

        if (!Unlocked(AID.UncoiledFury) || !Unlocked(AID.SerpentsIre))
            return false;

        if (Coil <= 0 || CoilMax <= 0 || Coil < CoilMax)
            return false;

        if (_ireReadyIn <= GCD)
            return false;

        if (_ireReadyIn > AttackGCDLength * 2.0f)
            return false;

        if (DreadCombo != 0)
            return false;

        return Anguine <= 0 && ReawakenLeft <= 0;
    }

    private bool ShouldForceSerpentsIreForDoubleReawaken(in Strategy strategy)
    {
        if (strategy.SerpentsIre == SerpentsIreStrategy.Off)
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay)
            return false;

        if (!Unlocked(AID.SerpentsIre) || !Unlocked(AID.Reawaken))
            return false;

        if (_ireReadyIn > GCD)
            return false;

        if (ReawakenReady > 0 || ReawakenLeft > 0 || Anguine > 0)
            return false;

        if (DreadCombo != 0)
            return false;

        if (Offering < 50)
            return false;

        return CombatTimer > 20;
    }

    private bool ShouldUseSerpentsIre(in Strategy strategy)
    {
        if (strategy.SerpentsIre == SerpentsIreStrategy.Off)
            return false;

        if (_wantPotionNow)
            return false;

        // spec rule 1: the Reawaken it enables needs its whole sequence before the loss
        if (Mechanic.ShouldHoldWindow(ReawakenSequenceDuration() + AttackGCDLength, 120f, AttackGCDLength))
            return false;

        var forceDoubleReawakenIre = ShouldForceSerpentsIreForDoubleReawaken(strategy);
        if (forceDoubleReawakenIre && CanUseSerpentsIreNow())
            return true;

        if (!CanUseSerpentsIreWithoutOverflow())
            return false;

        if (ShouldUsePatch75ZeroSecondOpener(strategy)
            && CombatTimer <= 5.0f
            && DreadCombo == DreadCombo.Dreadwinder
            && ReawakenReady <= 0
            && ReawakenLeft <= 0
            && Anguine == 0)
            return true;

        if (strategy.SerpentsIre == SerpentsIreStrategy.Force)
            return true;

        if (ShouldFinishDreadComboBeforeEvenBurst(strategy))
            return false;

        if (HasPendingSerpentsTailOrTwinFollowUp())
            return false;

        return strategy.Buffs.Value != OffensiveStrategy.Delay;
    }

    private bool ShouldUsePotion()
    {
        // same per-frame decision as the burst holds, restricted to the weave window
        return _wantPotionNow && GCD is >= 0.75f and <= 1.50f;
    }

    private bool WantsOpenerPotionWindow(in Strategy strategy)
    {
        if (strategy.Potion != PotionStrategy.OpenerAndEven)
            return false;

        if (CombatTimer > 20)
            return false;

        if (strategy.OpenerBurst == OpenerBurstStrategy.Patch75ZeroSecond)
        {
            if (!Unlocked(AID.Reawaken))
                return false;

            if (NumAOETargets == 0)
                return false;

            return DreadCombo == 0
                && ReawakenReady > 0
                && ReawakenLeft <= 0
                && Anguine == 0;
        }

        if (!Unlocked(AID.Reawaken) || !Unlocked(AID.SerpentsIre))
            return false;

        if (!Unlocked(AID.Vicewinder))
            return false;

        if (Anguine > 0 || ReawakenLeft > 0)
            return false;

        return DreadCombo == DreadCombo.Dreadwinder && _ireReadyIn > 100;
    }

    private bool WantsEvenBurstPotionWindow(in Strategy strategy)
    {
        if (strategy.Potion == PotionStrategy.Off)
            return false;

        if (strategy.Potion != PotionStrategy.OpenerAndEven && strategy.Potion != PotionStrategy.EvenOnly)
            return false;

        if (strategy.OpenerBurst == OpenerBurstStrategy.Patch75ZeroSecond && CombatTimer <= 20)
            return false;

        if (!Unlocked(AID.Reawaken) || !Unlocked(AID.SerpentsIre))
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay)
            return false;

        if (Anguine > 0 || ReawakenLeft > 0)
            return false;

        var canUseSerpentsIreNow = strategy.SerpentsIre != SerpentsIreStrategy.Off
            && CanUseSerpentsIreNow();
        var serpentsIreSoon = strategy.SerpentsIre != SerpentsIreStrategy.Off
            && Unlocked(AID.SerpentsIre)
            && CoilMax > 0
            && Coil < CoilMax
            && _ireReadyIn <= AttackGCDLength * 2;

        var alreadyReadyDouble = ReawakenReady > 0 && Offering >= 50;
        var aboutToIreDouble = canUseSerpentsIreNow && Offering >= 50;
        var prepIreDouble = serpentsIreSoon && Offering >= 50;

        // only the double Reawaken of the even (Serpent's Ire) burst: any Reawaken used to count, so a potion coming off
        // cooldown before an odd-minute Reawaken was spent there and missed the next even burst
        return alreadyReadyDouble || aboutToIreDouble || prepIreDouble;
    }

    private bool WantsPotionBeforeBurst(in Strategy strategy)
    {
        if (strategy.Potion == PotionStrategy.Off)
            return false;

        if (strategy.Buffs == OffensiveStrategy.Delay)
            return false;

        return WantsOpenerPotionWindow(strategy) || WantsEvenBurstPotionWindow(strategy);
    }

    private bool IsNextPositionalSecondSerpent()
    {
        return NextGCD is AID.HuntersCoil or AID.SwiftskinsCoil;
    }

    private bool IsNextBasicFangThirdPositional()
    {
        return NextGCD is AID.FlanksbaneFang or AID.FlankstingStrike or AID.HindsbaneFang or AID.HindstingStrike;
    }

    private bool IsNextHighValueTrueNorthPositional()
    {
        return IsNextPositionalSecondSerpent();
    }

    private bool IsBasicFangTrueNorthClusterCandidate(in Strategy strategy, Enemy? primaryTarget)
    {
        if (primaryTarget == null)
            return false;

        if (!IsNextBasicFangThirdPositional())
            return false;

        if (ReawakenLeft > 0 || Anguine > 0 || DreadCombo != 0)
            return false;

        if (!Unlocked(AID.HuntersCoil) || !Unlocked(AID.SwiftskinsCoil))
            return false;

        var viceReadySoon = (Unlocked(AID.Vicewinder) && _viceReadyIn <= AttackGCDLength)
            || (Unlocked(AID.Vicepit) && ReadyIn(AID.Vicepit) <= AttackGCDLength);
        if (!viceReadySoon)
            return false;

        if (IsViceForbiddenNearEvenBurst(strategy) || _planVice)
            return false;

        if (_wantPotionNow || ShouldUseSerpentsIre(strategy))
            return false;

        if (ShouldForceSecondSerpentsIreReawaken() || ShouldWaitForSecondSerpentsIreReawakenFollowUp())
            return false;

        if (!CanDelayViceOneGCDForTrueNorthCluster(strategy, primaryTarget))
            return false;

        return Instinct > CoilBuffEmergency && Swiftscaled > CoilBuffEmergency;
    }

    private bool ShouldUseTrueNorthForCurrentGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        return IsNextHighValueTrueNorthPositional()
            || IsBasicFangTrueNorthClusterCandidate(strategy, primaryTarget);
    }

    private bool ShouldUseTrueNorth(in Strategy strategy, Enemy? primaryTarget)
    {
        if (strategy.TrueNorth != TrueNorthStrategy.Auto)
            return false;

        if (!Player.InCombat || primaryTarget == null)
            return false;

        if (!Unlocked(AID.TrueNorth))
            return false;

        if (TrueNorthLeft > 0)
            return false;

        if (ReadyIn(AID.TrueNorth) > GCD)
            return false;

        if (!NextPositionalImminent || NextPositionalCorrect)
            return false;

        if (!ShouldUseTrueNorthForCurrentGCD(strategy, primaryTarget))
            return false;

        const float trueNorthEarliest = 2.20f;
        const float trueNorthLatest = 0.45f;

        return GCD <= trueNorthEarliest && GCD >= trueNorthLatest;
    }

    private void OGCD(in Strategy strategy, Enemy? primaryTarget, bool shouldReawaken)
    {
        if (!Player.InCombat || primaryTarget == null)
            return;

        if (CurSerpentsTail != AID.SerpentsTail && Unlocked(CurSerpentsTail))
        {
            var serpentsTailTarget = CurSerpentsTail switch
            {
                AID.FirstLegacy or AID.SecondLegacy or AID.ThirdLegacy or AID.FourthLegacy => (BestLegacyTarget ?? primaryTarget)?.Actor,
                AID.LastLash => Player,
                _ => primaryTarget.Actor
            };

            PushOGCD(CurSerpentsTail, serpentsTailTarget, 20);
        }

        switch (TwinCombo)
        {
            case TwinType.Coil:
                if (PoisedForTwinfang > 0 && Unlocked(AID.UncoiledTwinfang))
                    PushOGCD(AID.UncoiledTwinfang, BestRangedAOETarget ?? primaryTarget, 10);

                if (PoisedForTwinblood > 0 && PoisedForTwinfang <= 0 && Unlocked(AID.UncoiledTwinblood))
                    PushOGCD(AID.UncoiledTwinblood, BestRangedAOETarget ?? primaryTarget, 10);
                break;

            case TwinType.AOE:
                if (FellhuntersVenom > 0 && Unlocked(AID.TwinfangThresh))
                    PushOGCD(AID.TwinfangThresh, Player, 10);

                if (FellskinsVenom > 0 && Unlocked(AID.TwinbloodThresh))
                    PushOGCD(AID.TwinbloodThresh, Player, 10);
                break;

            case TwinType.SingleTarget:
                if (ShouldUsePatch75ZeroSecondOpener(strategy) && CombatTimer <= 15)
                {
                    if (DreadCombo == DreadCombo.SwiftskinsCoil)
                    {
                        if (SwiftskinsVenom > 0 && Unlocked(AID.TwinbloodBite))
                            PushOGCD(AID.TwinbloodBite, primaryTarget, 11);

                        if (HuntersVenom > 0 && Unlocked(AID.TwinfangBite))
                            PushOGCD(AID.TwinfangBite, primaryTarget, 10);
                    }
                    else
                    {
                        if (HuntersVenom > 0 && Unlocked(AID.TwinfangBite))
                            PushOGCD(AID.TwinfangBite, primaryTarget, 11);

                        if (SwiftskinsVenom > 0 && Unlocked(AID.TwinbloodBite))
                            PushOGCD(AID.TwinbloodBite, primaryTarget, 10);
                    }
                }
                else
                {
                    if (HuntersVenom > 0 && Unlocked(AID.TwinfangBite))
                        PushOGCD(AID.TwinfangBite, primaryTarget, 10);

                    if (SwiftskinsVenom > 0 && Unlocked(AID.TwinbloodBite))
                        PushOGCD(AID.TwinbloodBite, primaryTarget, 10);
                }
                break;
        }

        if (ShouldUseSlitherForBurstRecovery(strategy, primaryTarget, shouldReawaken))
            PushOGCD(AID.Slither, primaryTarget, 30);

        if (ShouldUsePotion())
        {
            Hints.ActionsToExecute.Push(ActionDefinitions.IDPotionDex, Player, ActionQueue.Priority.Low + 5);
            return;
        }

        if (ShouldUseSerpentsIre(strategy))
            PushOGCD(AID.SerpentsIre, Player, 25);

        // a pending Serpent's Tail or twin follow-up is lost to the next weaponskill, a missed positional only costs 50-60 potency: in the
        // 2.55s coil windows there is no third weave slot, so True North goes behind them
        if (ShouldUseTrueNorth(strategy, primaryTarget))
            PushOGCD(AID.TrueNorth, Player, CurSerpentsTail != AID.SerpentsTail || TwinCombo != TwinType.None ? OGCDPriorityTrueNorthBehindFollowUps : OGCDPriorityTrueNorth);
    }

    // upcoming positional derived from gauge/combo state (valid before this frame's GCD pushes as well)
    private (Positional, bool) PositionalFromComboState(in Strategy strategy, Enemy? primaryTarget)
    {
        if (!Unlocked(AID.FlankstingStrike))
            return (Positional.Any, false);

        if (NextGCD == AID.HuntersCoil)
            return (Positional.Flank, true);

        if (NextGCD == AID.SwiftskinsCoil)
            return (Positional.Rear, true);

        if (DreadCombo == DreadCombo.Dreadwinder)
            return (ShouldPreferHuntersCoilAfterDreadwinder(strategy, primaryTarget) ? Positional.Flank : Positional.Rear, true);

        if (DreadCombo == DreadCombo.HuntersCoil)
            return (Positional.Rear, true);

        if (DreadCombo == DreadCombo.SwiftskinsCoil)
            return (Positional.Flank, true);

        if (DreadCombo is DreadCombo.HuntersDen or DreadCombo.SwiftskinsDen or DreadCombo.PitOfDread)
            return (Positional.Any, false);

        if (ShouldUseAOERotation())
            return (Positional.Any, false);

        return ComboLastMove switch
        {
            AID.HuntersSting => (Positional.Flank, true),
            AID.SwiftskinsSting => (Positional.Rear, true),
            _ => SelectSingleSecondComboGCD() switch
            {
                AID.HuntersSting => (Positional.Flank, false),
                AID.SwiftskinsSting => (Positional.Rear, false),
                _ => (Positional.Any, false)
            }
        };
    }

    private (Positional, bool) GetPositional(in Strategy strategy, Enemy? primaryTarget)
    {
        var (pos, imm) = PositionalFromComboState(strategy, primaryTarget);

        if (NextGCD is not (AID.FlanksbaneFang or AID.FlankstingStrike or AID.HindsbaneFang or AID.HindstingStrike or AID.HuntersCoil or AID.SwiftskinsCoil))
            imm = false;

        return (pos, imm);
    }
}
