using BossMod.Data;
using BossMod.DRG;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.xan.Custom;

public sealed class DRG(RotationModuleManager manager, Actor player) : Attackxan<AID, TraitID, DRG.Strategy>(manager, player, PotionType.Strength)
{
    public struct Strategy : IStrategyCommon
    {
        public Track<Targeting> Targeting;
        public Track<AOEStrategy> AOE;
        [Track(Action = AID.BattleLitany)]
        public Track<OffensiveStrategy> Buffs;

        [Track(UiPriority = -10, Actions = [AID.Jump, AID.HighJump, AID.MirageDive, AID.DragonfireDive, AID.Stardiver])]
        public Track<DiveStrategy> Dive;

        [Track("Phantom Samurai: Use Iainuki on cooldown", UiPriority = -10, MinLevel = 100, Action = PhantomID.Iainuki)]
        public Track<EnabledByDefault> Iainuki;

        [Track("Phantom Samurai: Use Zeninage under raid buffs (coffer required)", UiPriority = -10, MinLevel = 100, Action = PhantomID.Zeninage)]
        public Track<EnabledByDefault> Zeninage;

        [Track("Lance Charge", InternalName = "LC", Action = AID.LanceCharge)]
        public Track<LanceChargeStrategy> LanceCharge;

        [Track("High Jump/Mirage Dive", Actions = [AID.Jump, AID.HighJump, AID.MirageDive])]
        public Track<HJMDStrategy> HJMD;

        [Track("Piercing Talon", InternalName = "Talon", Action = AID.PiercingTalon)]
        public Track<TalonStrategy> Talon;

        [Track("Hold GCD", Context = StrategyContext.Plan)]
        public Track<DelayStrategy> HoldGCD;

        [Track("Behavior for HP-locked targets")]
        public Track<FillerStrategy> Filler;

        [Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)]
        public Track<MechanicHintStrategy> MechanicHints;

        readonly Targeting IStrategyCommon.Targeting => Targeting.Value;
        readonly AOEStrategy IStrategyCommon.AOE => AOE.Value;
    }

    public enum TalonStrategy
    {
        [Option("Use as a filler while out of melee range (up to 20y), and whenever Enhanced Piercing Talon is active")]
        Automatic,
        [Option("Only use while Enhanced Piercing Talon is active")]
        EnhancedOnly,
        [Option("Do not use")]
        Forbid
    }

    public enum DiveStrategy
    {
        [Option("Use dives according to standard rotation")]
        Allow,
        [Option("Only use dives that do not change your position")]
        NoMove,
        [Option("Only use dives that do not pause your movement (i.e., only Mirage Dive)")]
        NoLock
    }

    public enum LanceChargeStrategy
    {
        [Option("Use on cooldown, once Power Surge is active")]
        Automatic,
        [Option("Don't use", Cooldown = 20)] // so plan UI shows how long it will last if we use it at the end of the window
        Delay,
        [Option("Use ASAP", Effect = 20, Cooldown = 60)]
        Force
    }

    public enum HJMDStrategy
    {
        [Option("Use ASAP if buffs are active, or if Lance Charge is on cooldown", Targets = ActionTargets.Hostile)]
        AfterBuffs,
        [Option("Use High Jump ASAP; hold Mirage Dive until buffs", Effect = 15, Cooldown = 30, Targets = ActionTargets.Hostile)]
        HoldMD,
        [Option("Do not use either")]
        Delay,
        [Option("Use ASAP", Effect = 15, Cooldown = 30, Targets = ActionTargets.Hostile)]
        Force
    }

    public enum DelayStrategy
    {
        [Option("Keep GCD rolling")]
        None,
        [Option("Delay GCD until end of current plan entry", Cooldown = 30)] // so plan UI shows how long the combo timer will last
        Delay
    }

    public enum FillerStrategy
    {
        [Option("Use standard rotation")]
        None,
        [Option("Only use combo starter (True Thrust, Raiden Thrust, Doom Spike, Draconian Fury) so that next non-filler GCD will grant Power Surge")]
        ForceTT
    }

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("xan DRG [Custom]", "Dragoon", "Standard rotation (xan)|Melee", "xan", RotationModuleQuality.Basic, BitMask.Build(Class.DRG, Class.LNC), 100).WithStrategies<Strategy>();
    }

    public int Eyes;
    public int Focus;
    public float LotD;
    public float PowerSurge;
    public float LanceCharge;
    public float DiveReady;
    public float NastrondReady;
    public float LifeSurge;
    public float DraconianFire;
    public float DragonsFlight;
    public float StarcrossReady;
    public float EnhancedTalon;

    public float TargetDotLeft;

    public int NumAOETargets; // standard combo (10x4 rect)
    public int NumLongAOETargets; // GSK, nastrond (15x4 rect)
    public int NumDiveTargets; // dragonfire, stardiver, etc

    // Lance Charge has been used (or is on cooldown) in this fight; see StrategyOk
    private bool _lanceChargeUsed;
    private Enemy? BestAOETarget;
    private Enemy? BestLongAOETarget;
    private Enemy? BestDiveTarget;
    private Enemy? BestDotTarget;

    // Per-category action locks (ActionLocks): Pacification refuses weaponskills, Silence spells, Amnesia abilities, a stun-type status
    // everything. The client refuses after the queue has picked, so a locked candidate at the top of the queue is resubmitted every
    // frame while what is below it never runs. Every push goes through PushAction -> CanUse, so filtering there keeps the queue to
    // what the client accepts and the rotation falls back on its own (weaponskills under Amnesia, abilities under Pacification).
    protected override bool CanUse(AID action) => !IsActionLocked(action) && !DelaysWeaponskillResume(action);

    public override void Exec(in Strategy strategy, Enemy? primaryTarget)
    {
        SelectPrimaryTarget(strategy, ref primaryTarget, 3);
        UpdateMechanicForecast(strategy.MechanicHints.Value);

        var gauge = World.Client.GetGauge<DragoonGauge>();

        Eyes = gauge.EyeCount;
        Focus = gauge.FirstmindsFocusCount;
        LotD = gauge.LotdTimer * 0.001f;

        PowerSurge = StatusLeft(SID.PowerSurge, 30);
        DiveReady = StatusLeft(SID.DiveReady);
        NastrondReady = StatusLeft(SID.NastrondReady);
        LifeSurge = StatusLeft(SID.LifeSurge, 5);
        LanceCharge = StatusLeft(SID.LanceCharge, 20);
        if (!Player.InCombat)
            _lanceChargeUsed = false;
        else if (LanceCharge > 0 || OnCooldown(AID.LanceCharge))
            _lanceChargeUsed = true;
        DraconianFire = StatusLeft(SID.DraconianFire);
        DragonsFlight = StatusLeft(SID.DragonsFlight);
        StarcrossReady = StatusLeft(SID.StarcrossReady);
        EnhancedTalon = StatusLeft(SID.EnhancedPiercingTalon);
        (BestAOETarget, NumAOETargets) = SelectTarget(strategy, primaryTarget, 10, (primary, other) => TargetInAOERect(other, Player.Position, Player.DirectionTo(primary), 10, 2));
        (BestLongAOETarget, NumLongAOETargets) = SelectTarget(strategy, primaryTarget, 15, (primary, other) => TargetInAOERect(other, Player.Position, Player.DirectionTo(primary), 15, 2));
        (BestDiveTarget, NumDiveTargets) = SelectTarget(strategy, primaryTarget, 20, IsSplashTarget);
        (BestDotTarget, TargetDotLeft) = SelectDotTarget(strategy, primaryTarget, DotLeft, 2);
        if (BestDotTarget != null && Player.DistanceToHitbox(BestDotTarget) > 3) // Disembowel/Chaotic Spring are melee - do not chase a dot target we cannot hit
            (BestDotTarget, TargetDotLeft) = (primaryTarget, DotLeft(primaryTarget?.Actor));

        var positionalTarget = ComboLastMove is AID.Disembowel or AID.SpiralBlow ? BestDotTarget ?? primaryTarget : primaryTarget;
        var pos = GetPositional(strategy, positionalTarget);
        UpdatePositionals(positionalTarget, ref pos);

        var gcdDelay = strategy.HoldGCD.Value == DelayStrategy.Delay ? strategy.HoldGCD.ExpireIn : 0;

        if (CountdownRemaining > 0)
        {
            if (Player.DistanceToHitbox(primaryTarget) <= 3)
            {
                if (CountdownRemaining < GetApplicationDelay(AID.TrueThrust))
                    PushGCD(AID.TrueThrust, primaryTarget);
            }
            else if (CountdownRemaining < 0.7f)
                PushOGCD(AID.WingedGlide, primaryTarget);

            return;
        }

        if (primaryTarget != null)
            GoalZoneCombined(strategy, 3, Hints.GoalAOERect(primaryTarget.Actor, 10, 2), AID.DoomSpike, minAoe: 3, maximumActionRange: 20);

        if (LotD > GCD && PowerSurge > GCD && LanceCharge > GCD && strategy.Zeninage.IsEnabled() && DutyActionGCDReady(PhantomID.Zeninage) && DraconianFire <= GCD)
            PushGCD((AID)(uint)PhantomID.Zeninage, primaryTarget, priority: 100);

        if (strategy.Iainuki.IsEnabled() && DutyActionGCDReady(PhantomID.Iainuki) && DutyActionReadyIn(PhantomID.Zeninage) > GCD && DraconianFire <= GCD)
            PushGCD((AID)(uint)PhantomID.Iainuki, primaryTarget, priority: 90);

        var spam1 = strategy.Filler.Value == FillerStrategy.ForceTT && primaryTarget?.Priority == Enemy.PriorityPointless;

        if (NumAOETargets > 2)
        {
            switch (ComboLastMove)
            {
                case AID.SonicThrust:
                    PushGCD(AID.CoerthanTorment, BestAOETarget, delay: gcdDelay, setRotation: true);
                    break;
                case AID.DoomSpike:
                case AID.DraconianFury:
                    PushGCD(AID.SonicThrust, BestAOETarget, delay: gcdDelay, setRotation: true);
                    break;
            }

            // lol
            if (!Unlocked(AID.SonicThrust) && PowerSurge <= GCD)
            {
                if (ComboLastMove == AID.TrueThrust)
                    PushGCD(AID.Disembowel, primaryTarget, delay: gcdDelay);

                PushGCD(AID.TrueThrust, primaryTarget, delay: gcdDelay);
            }

            PushGCD(DraconianFire > GCD ? AID.DraconianFury : AID.DoomSpike, BestAOETarget, spam1 ? 100 : 2, delay: gcdDelay, setRotation: true);
        }
        else
        {
            switch (ComboLastMove)
            {
                case AID.WheelingThrust:
                case AID.FangAndClaw:
                    PushGCD(AID.Drakesbane, primaryTarget, delay: gcdDelay);
                    break;
                case AID.ChaosThrust:
                case AID.ChaoticSpring:
                    PushGCD(AID.WheelingThrust, primaryTarget, delay: gcdDelay);
                    break;
                case AID.FullThrust:
                case AID.HeavensThrust:
                    PushGCD(AID.FangAndClaw, primaryTarget, delay: gcdDelay);
                    break;
                case AID.Disembowel:
                case AID.SpiralBlow:
                    PushGCD(BestActionUnlocked(AID.ChaoticSpring, AID.ChaosThrust), BestDotTarget ?? primaryTarget, delay: gcdDelay);
                    break;
                case AID.VorpalThrust:
                case AID.LanceBarrage:
                    PushGCD(BestActionUnlocked(AID.HeavensThrust, AID.FullThrust), primaryTarget, delay: gcdDelay);
                    break;
                case AID.TrueThrust:
                case AID.RaidenThrust:
                    if (PowerSurge < 10 || NumAOETargets == 2 && BestDotTarget != null && !CanFitGCD(TargetDotLeft, 7))
                        PushGCD(BestActionUnlocked(AID.SpiralBlow, AID.Disembowel), BestDotTarget ?? primaryTarget, delay: gcdDelay);
                    PushGCD(BestActionUnlocked(AID.LanceBarrage, AID.VorpalThrust), primaryTarget, delay: gcdDelay);
                    break;
            }
        }

        PushGCD(DraconianFire > GCD ? AID.RaidenThrust : AID.TrueThrust, primaryTarget, spam1 ? 100 : 2, delay: gcdDelay);
        var nextGCDRange = ActionDefinitions.Instance.Spell(NextGCD)?.Range ?? 3;
        var primaryTargetDistance = Player.DistanceToHitbox(primaryTarget);
        var talon = strategy.Talon.Value;
        if (talon != TalonStrategy.Forbid)
        {
            if (primaryTarget != null && primaryTargetDistance > nextGCDRange && primaryTargetDistance <= 20 && (talon == TalonStrategy.Automatic || EnhancedTalon > GCD))
                PushGCD(AID.PiercingTalon, primaryTarget, 3, delay: gcdDelay);
            else if (EnhancedTalon > GCD)
                PushGCD(AID.PiercingTalon, primaryTarget, delay: gcdDelay);
        }

        OGCD(strategy, primaryTarget);
    }

    // Weave priorities (the action queue takes the higher one first, equal ones in push order): the burst openers first - Battle
    // Litany and Geirskogul, whose windows everything else lands in - then, once Stardiver leads to Starcross (100), that pair, which
    // only Life of the Dragon allows. With everything at the same priority a loss or a crowded window cost Stardiver (1.5s lock) and
    // its Starcross first. Below 100 Stardiver alone is not worth moving ahead of Nastrond and the jumps, and Nastrond ahead of the
    // jumps loses more than it keeps. Harness at 100: combat matrix +1.34% rDPS (Stardivers 6,589 -> 8,420), dmu +0.22%; 70-90 unchanged.
    private const int BurstOpenerPriority = 4;
    private const int StarcrossPairPriority = 3;
    private const int FocusOvercapPriority = 3;

    private void OGCD(in Strategy strategy, Enemy? primaryTarget)
    {
        var moveOk = MoveOk(strategy);

        if (StrategyOk(strategy.LanceCharge.Cast<OffensiveStrategy>(), primaryTarget))
            PushOGCD(AID.LanceCharge, Player);

        if (StrategyOk(strategy.Buffs, primaryTarget, extraCondition: LanceCharge > AnimLock))
            PushOGCD(AID.BattleLitany, Player, BurstOpenerPriority);

        if (NastrondReady == 0 && LanceCharge > AnimLock)
            PushOGCD(AID.Geirskogul, BestLongAOETarget, BurstOpenerPriority, setRotation: NumLongAOETargets > 1);

        HJMD(strategy, primaryTarget);

        // pushed straight onto the queue, so it does not pass CanUse: under Amnesia / an all-action lock the client refuses it every frame
        if (NextPositionalImminent && !NextPositionalCorrect && !IsActionLocked(AID.TrueNorth))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.TrueNorth), Player, ActionQueue.Priority.Low - 20, delay: GCD - 0.8f);

        // ok to use WT outside of buffs, otherwise we might overcap and waste one; when the next GCD grants a Focus it goes ahead of the
        // other weaves, or the Focus is lost (it was on 23% of the combat matrix runs)
        if (ShouldWT(strategy))
            PushOGCD(AID.WyrmwindThrust, BestLongAOETarget, Focus == 2 && NextGCD is AID.RaidenThrust or AID.DraconianFury ? FocusOvercapPriority : 1, setRotation: NumLongAOETargets > 1);

        // mechanic hints: Life Surge (5s) is not pressed when a known loss starts before the weaponskill it is for. Only a loss still
        // ahead counts: the boss-module forecast can sit at 0 through an HP-gated phase change with the target still up
        if ((LanceCharge > GCD || MaxChargesIn(AID.LifeSurge) <= GCD) && ShouldLifeSurge() && !(Mechanic.TargetLossIn > 0 && Mechanic.LossWithin(GCD + 0.5f)))
            PushOGCD(AID.LifeSurge, Player);

        if (moveOk && LanceCharge > AnimLock)
            PushOGCD(AID.DragonfireDive, BestDiveTarget);

        if (NastrondReady > 0)
            PushOGCD(AID.Nastrond, BestLongAOETarget, setRotation: NumLongAOETargets > 1);

        if (LotD > AnimLock && moveOk)
        {
            // stardiver: 1.5 + delay
            // regular GCD: 0.6 + delay
            // some conditions like DD haste (and maybe bozja?) can reduce GCD to 2.1s or lower, making stardiver weave impossible
            if (GCDLength > 2.1f + 2 * AnimationLockDelay)
                PushOGCD(AID.Stardiver, BestDiveTarget, Unlocked(AID.Starcross) ? StarcrossPairPriority : 1);
            else if (GCD > 0)
                PushGCD(AID.Stardiver, BestDiveTarget, 3);
        }

        if (StarcrossReady > 0)
            PushOGCD(AID.Starcross, BestDiveTarget, StarcrossPairPriority);

        if (DragonsFlight > 0)
            PushOGCD(AID.RiseOfTheDragon, BestDiveTarget);
    }

    // The opener still waits for Power Surge (Disembowel first): dropping that too looks +1.7% on the short combat-matrix slices but is -0.05%
    // on full-length fights. After the opener Lance Charge and Battle Litany go out the moment their recast is up: waiting for the next Power
    // Surge refresh (after a downtime, say) cost them a few GCDs of their 20 s windows. Harness: combat matrix +0.41% (+0.20% with target-loss
    // hints, +0.08% with random disengages, irregular events +0.31%), Lv90 +0.42%, Lv80 +0.33%; full-length fights +0.01% (noise).
    private bool StrategyOk(OffensiveStrategy t, Enemy? primaryTarget, bool extraCondition = true) => t switch
    {
        OffensiveStrategy.Force => true,
        OffensiveStrategy.Automatic => primaryTarget?.Priority >= 0 && Player.InCombat && (_lanceChargeUsed || PowerSurge > GCD) && extraCondition,
        _ => false
    };

    private void HJMD(Strategy strategy, Enemy? primaryTarget)
    {
        var target = ResolveEnemy(strategy.HJMD) ?? primaryTarget;

        if (target == null)
            return;

        var haveTarget = target.Priority >= 0;

        var opt = strategy.HJMD.Value;

        var hjOk = DiveReady == 0 && PosLockOk(strategy) && opt switch
        {
            HJMDStrategy.AfterBuffs => OnCooldown(AID.LanceCharge) && haveTarget,
            HJMDStrategy.HoldMD or HJMDStrategy.Force => true,
            _ => false
        };

        var mdOk = DiveReady > AnimLock && opt switch
        {
            HJMDStrategy.Force => true,
            HJMDStrategy.AfterBuffs => PowerSurge > AnimLock,
            HJMDStrategy.HoldMD => LanceCharge > AnimLock || DiveReady < GCD + 0.6f + AnimationLockDelay,
            _ => false
        };

        if (hjOk)
            PushOGCD(AID.Jump, target);
        if (mdOk)
            PushOGCD(AID.MirageDive, target);
    }

    private bool ShouldLifeSurge()
    {
        if (LifeSurge > 0)
            return false;

        return NextGCD switch
        {
            // highest potency at max level (full thrust is still highest potency before it gets upgraded)
            AID.CoerthanTorment or AID.Drakesbane or AID.HeavensThrust or AID.FullThrust => true,

            // highest potency before Full Thrust is unlocked at 26
            AID.VorpalThrust => !Unlocked(AID.FullThrust),

            // fallbacks for AOE rotation
            AID.SonicThrust => !Unlocked(AID.CoerthanTorment),
            AID.DoomSpike => !Unlocked(AID.SonicThrust),
            _ => false,
        };
    }

    private bool ShouldWT(Strategy strategy)
        => Focus == 2 && (LotD > AnimLock || NextGCD is AID.RaidenThrust or AID.DraconianFury);

    private float DotLeft(Actor? target) => target == null ? float.MaxValue : Math.Max(
        StatusDetails(target, SID.ChaosThrust, Player.InstanceID).Left,
        StatusDetails(target, SID.ChaoticSpring, Player.InstanceID).Left
    );

    private bool MoveOk(Strategy strategy) => strategy.Dive == DiveStrategy.Allow;
    private bool PosLockOk(Strategy strategy) => strategy.Dive != DiveStrategy.NoLock;

    private (Positional, bool) GetPositional(in Strategy strategy, Enemy? primaryTarget)
    {
        // no positional
        if (NumAOETargets > 2 && Unlocked(AID.DoomSpike) || !Unlocked(AID.ChaosThrust) || primaryTarget == null)
            return (Positional.Any, false);

        if (!Unlocked(AID.FangAndClaw))
            return (Positional.Rear, ComboLastMove == AID.Disembowel);

        (Positional, bool) predictNext(int gcdsBeforeTrueThrust)
        {
            var buffsUp = CanFitGCD(TargetDotLeft, gcdsBeforeTrueThrust + 3) && CanFitGCD(PowerSurge, gcdsBeforeTrueThrust + 2);
            return (buffsUp ? Positional.Flank : Positional.Rear, false);
        }

        return ComboLastMove switch
        {
            AID.ChaosThrust => Unlocked(AID.WheelingThrust) ? (Positional.Rear, true) : predictNext(0),
            AID.ChaoticSpring => (Positional.Rear, true), // wheeling thrust is unlocked
            AID.Disembowel or AID.SpiralBlow => (Positional.Rear, true),
            AID.TrueThrust or AID.RaidenThrust => predictNext(-1),
            AID.VorpalThrust or AID.LanceBarrage => (Positional.Flank, false),
            AID.HeavensThrust or AID.FullThrust => (Positional.Flank, true),
            AID.WheelingThrust or AID.FangAndClaw => predictNext(Unlocked(AID.Drakesbane) ? 1 : 0),
            // last action is AOE, or nothing, or drakesbane - loop reset
            _ => predictNext(0)
        };
    }
}
