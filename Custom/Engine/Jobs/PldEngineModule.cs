using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.PLD;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using Akechi = BossMod.Autorotation.akechi.Custom;
using AkechiPLD = BossMod.Autorotation.akechi.Custom.AkechiPLD;

namespace BossMod.Autorotation;

// "PLD [Engine]": Paladin damage rotation on the rotation engine (Custom/Engine). Level 30+ (the full combo and Spirits Within): the
// definition is built for the player's level, and BMR recreates the module when the level changes (level sync).
// The strategy tracks are those of Akechi PLD [Custom]; Intervene and the ranged GCDs (Shield Lob / Holy Spirit out of melee range) are
// pressed by this module; settings for skills not learned at the player's level do nothing. No mitigation, Clemency (no setting
// of the Akechi module uses it) or tank stance handling.
public sealed class PldEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
    {
        var res = new RotationModuleDefinition("PLD [Engine]", "Paladin damage on the two-tier rotation engine (burst-window planning + short search). Experimental, level 30+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.PLD), 100, 30);
        res.Configs.AddRange(AkechiPLD.Definition().Configs);
        return res;
    }

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, player.Level) : 2.5f;
        return new RotationEngine(PldDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? PldDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.05f, ReplanInterval = 8 };
    }

    protected override Actor? SelectTarget(StrategyValues strategy, Actor? primaryTarget)
    {
        SetMechanicHints(strategy.Option(AkechiPLD.Track.MechanicHints).As<MechanicHintStrategy>());
        return SelectTarget(strategy.Option(Akechi.SharedTrack.Targeting).As<Akechi.SoftTargetStrategy>(), primaryTarget, 3);
    }

    private static readonly string[] Abilities = ["FightOrFlight", "Imperator", "BladeOfHonor", "Expiacion", "CircleOfScorn"];
    private static readonly string[] Cooldowns = ["FightOrFlight", "Imperator", "Expiacion", "CircleOfScorn"];
    private static readonly string[] Atonements = ["Atonement", "Supplication", "Sepulchre"];
    private static readonly string[] Blades = ["Confiteor", "BladeOfFaith", "BladeOfTruth", "BladeOfValor"];
    private static readonly string[] HolySpirits = ["HolySpiritDM", "HolySpiritReq", "HolySpirit"];
    private static readonly string[] HolyCircles = ["HolyCircleDM", "HolyCircleReq"];

    protected override void ApplyStrategy(StrategyValues strategy, ref EngineState s, Actor? primaryTarget)
    {
        var aoe = strategy.Option(AkechiPLD.Track.AOE).As<AkechiPLD.AOEStrategy>();
        if (aoe is AkechiPLD.AOEStrategy.ForceSTFinish or AkechiPLD.AOEStrategy.ForceSTBreak)
            s.Targets = 1;
        else if (aoe is AkechiPLD.AOEStrategy.ForceAOEFinish or AkechiPLD.AOEStrategy.ForceAOEBreak)
            ForceAoeTargets(ref s);

        var hold = strategy.Option(Akechi.SharedTrack.Hold).As<Akechi.HoldStrategy>();
        if (hold == Akechi.HoldStrategy.HoldEverything)
        {
            ForbidAll(ref s);
            return;
        }

        // the potion with Fight or Flight up or about to be pressed, at the burst the option names (Immediate: at once, the only one a
        // cooldown hold lets through)
        var potion = strategy.Option(AkechiPLD.Track.Potion).As<AkechiPLD.PotionStrategyPLD>();
        var t = CombatTime;
        var fight = Hints.FightRemaining;
        var fofCd = Job.CooldownIndex(PldDefinition.FightOrFlightCD);
        var fof = s.HasStatus(Job.StatusIndex(PldDefinition.FightOrFlight)) || s.Charges[fofCd] > 0 || s.CdReadyIn[fofCd] <= GCD;
        UsePotion(ActionDefinitions.IDPotionStr, potion == AkechiPLD.PotionStrategyPLD.Immediate || fof && hold is not (Akechi.HoldStrategy.HoldCooldowns or Akechi.HoldStrategy.HoldAbilities) && potion switch
        {
            AkechiPLD.PotionStrategyPLD.AlignWithFoF => true,
            AkechiPLD.PotionStrategyPLD.UseAtCombatStart => t < 60,
            AkechiPLD.PotionStrategyPLD.UseAtSixMinuteBurst => t is > 340 and < 400,
            AkechiPLD.PotionStrategyPLD.UseBeforeEightMinuteBurst => t is > 460 and < 520,
            AkechiPLD.PotionStrategyPLD.FinalBurst => fight.Known && fight.RemainingSeconds < 60,
            _ => false
        });

        if (hold is Akechi.HoldStrategy.HoldBuffs)
            Forbid(ref s, "FightOrFlight");
        if (hold is Akechi.HoldStrategy.HoldCooldowns)
            foreach (var sk in Cooldowns)
                Forbid(ref s, sk);
        if (hold is Akechi.HoldStrategy.HoldAbilities)
            foreach (var sk in Abilities)
                Forbid(ref s, sk);

        // normal rotation + Atonement + Holy: no burst (Fight or Flight, Imperator and its blades, Goring Blade, the abilities)
        if (strategy.Option(AkechiPLD.Track.RotationMode).As<AkechiPLD.RotationModeStrategy>() == AkechiPLD.RotationModeStrategy.NormalAtonementHoly)
        {
            foreach (var sk in Abilities)
                Forbid(ref s, sk);
            foreach (var sk in Blades)
                Forbid(ref s, sk);
            Forbid(ref s, "GoringBlade");
        }

        switch (strategy.Option(AkechiPLD.Track.Atonement).As<AkechiPLD.AtonementStrategy>())
        {
            case AkechiPLD.AtonementStrategy.Delay:
                foreach (var sk in Atonements)
                    Forbid(ref s, sk);
                break;
            case AkechiPLD.AtonementStrategy.ForceAtonement:
                Force("Atonement");
                break;
            case AkechiPLD.AtonementStrategy.ForceSupplication:
                Force("Supplication");
                break;
            case AkechiPLD.AtonementStrategy.ForceSepulchre:
                Force("Sepulchre");
                break;
        }

        switch (strategy.Option(AkechiPLD.Track.BladeCombo).As<AkechiPLD.BladeComboStrategy>())
        {
            case AkechiPLD.BladeComboStrategy.Delay:
                foreach (var sk in Blades)
                    Forbid(ref s, sk);
                break;
            case AkechiPLD.BladeComboStrategy.ForceConfiteor:
                Force("Confiteor");
                break;
            case AkechiPLD.BladeComboStrategy.ForceFaith:
                Force("BladeOfFaith");
                break;
            case AkechiPLD.BladeComboStrategy.ForceTruth:
                Force("BladeOfTruth");
                break;
            case AkechiPLD.BladeComboStrategy.ForceValor:
                Force("BladeOfValor");
                break;
        }

        // Together: only next to the other buff (ready or running); RaidBuffsOnly: only with party raid buffs up or about to start
        var (raidBuffLeft, raidBuffIn) = EstimateRaidBuffTimings(primaryTarget);
        var raidBuffs = raidBuffLeft > 0 || raidBuffIn < 5;
        var imperator = Job.CooldownIndex(PldDefinition.ImperatorCD);
        Buff(ref s, "FightOrFlight", strategy.Option(AkechiPLD.Track.FightOrFlight).As<AkechiPLD.BuffsStrategy>(),
            s.Charges[imperator] > 0 || s.CdReadyIn[imperator] <= GCD || s.HasStatus(Job.StatusIndex(PldDefinition.Requiescat)), raidBuffs);
        Buff(ref s, "Imperator", strategy.Option(AkechiPLD.Track.Requiescat).As<AkechiPLD.BuffsStrategy>(),
            fof, raidBuffs);

        var gb = strategy.Option(AkechiPLD.Track.GoringBlade).As<AkechiPLD.GoringBladeStrategy>();
        if (gb == AkechiPLD.GoringBladeStrategy.Delay)
            Forbid(ref s, "GoringBlade");
        else if (gb == AkechiPLD.GoringBladeStrategy.Force)
            Force("GoringBlade");

        switch (strategy.Option(AkechiPLD.Track.Holy).As<AkechiPLD.HolyStrategy>())
        {
            case AkechiPLD.HolyStrategy.Delay:
                foreach (var sk in HolySpirits)
                    Forbid(ref s, sk);
                foreach (var sk in HolyCircles)
                    Forbid(ref s, sk);
                break;
            case AkechiPLD.HolyStrategy.OnlySpirit:
                foreach (var sk in HolyCircles)
                    Forbid(ref s, sk);
                break;
            case AkechiPLD.HolyStrategy.OnlyCircle:
                foreach (var sk in HolySpirits)
                    Forbid(ref s, sk);
                break;
            case AkechiPLD.HolyStrategy.ForceSpirit:
                foreach (var sk in HolySpirits)
                    Force(sk);
                break;
            case AkechiPLD.HolyStrategy.ForceCircle:
                foreach (var sk in HolyCircles)
                    Force(sk);
                break;
        }

        OgcdTrack(ref s, AkechiPLD.Track.SpiritsWithin, "Expiacion");
        OgcdTrack(ref s, AkechiPLD.Track.CircleOfScorn, "CircleOfScorn");
        OgcdTrack(ref s, AkechiPLD.Track.BladeOfHonor, "BladeOfHonor");

        void OgcdTrack(ref EngineState s, AkechiPLD.Track track, string skill)
        {
            var o = strategy.Option(track).As<Akechi.OGCDStrategy>();
            if (o == Akechi.OGCDStrategy.Delay)
                Forbid(ref s, skill);
            else if (o != Akechi.OGCDStrategy.Automatic)
                Force(skill);
        }
    }

    private void Buff(ref EngineState s, string skill, AkechiPLD.BuffsStrategy setting, bool otherBuffReady, bool raidBuffs)
    {
        if (setting == AkechiPLD.BuffsStrategy.Delay || setting == AkechiPLD.BuffsStrategy.Together && !otherBuffReady || setting == AkechiPLD.BuffsStrategy.RaidBuffsOnly && !raidBuffs)
            Forbid(ref s, skill);
        else if (setting is AkechiPLD.BuffsStrategy.Force or AkechiPLD.BuffsStrategy.ForceWeave)
            Force(skill);
    }

    private bool _firstGcdDone;

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        base.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        if (!Player.InCombat)
            _firstGcdDone = false;
        else if (GCD > 0)
            _firstGcdDone = true;
        if (Target == null || !Target.IsTargetable || strategy.Option(Akechi.SharedTrack.Hold).As<Akechi.HoldStrategy>() == Akechi.HoldStrategy.HoldEverything)
            return;
        var dist = Player.DistanceToHitbox(Target);
        var outOfMelee = dist > 3;

        // Intervene: Force (one charge kept with Force1), or as a gap closer out of melee range (GapClose1 keeps one charge)
        var charges = ActionDefinitions.Instance.Spell(AID.Intervene) is { } intervene
            ? intervene.MaxChargesAtLevel(Player.Level) - (int)MathF.Ceiling(intervene.ChargeCapIn(World.Client.Cooldowns, World.Client.DutyActions, Player.Level) / intervene.Cooldown - 1e-3f) : 0;
        var dash = strategy.Option(AkechiPLD.Track.Dash).As<AkechiPLD.DashStrategy>();
        if (Player.InCombat && dist <= 20 && ActionUnlocked(AID.Intervene) && dash switch
        {
            AkechiPLD.DashStrategy.Force => charges >= 1,
            AkechiPLD.DashStrategy.Force1 => charges >= 2,
            AkechiPLD.DashStrategy.GapClose => charges >= 1 && outOfMelee,
            AkechiPLD.DashStrategy.GapClose1 => charges >= 2 && outOfMelee,
            _ => false
        })
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Intervene), Target, dash is AkechiPLD.DashStrategy.Force or AkechiPLD.DashStrategy.Force1 ? ActionQueue.Priority.Medium + 1 : ActionQueue.Priority.Low + 2);

        // ranged GCDs (the full rotation mode only): Holy Spirit when it can be cast (standing, or instant with Divine Might / Requiescat),
        // else Shield Lob. The pull options act on the first GCD of the fight, Force* always, the others out of melee range (below the engine's GCD)
        if (strategy.Option(AkechiPLD.Track.RotationMode).As<AkechiPLD.RotationModeStrategy>() != AkechiPLD.RotationModeStrategy.FullMode)
            return;
        var holyInstant = SelfStatusLeft(SID.DivineMight) > 0 || SelfStatusLeft(SID.Requiescat) > 0;
        var canHoly = dist <= 25 && Player.HPMP.CurMP >= 1000 && (!isMoving || holyInstant) && ActionUnlocked(AID.HolySpirit);
        var canLob = dist <= 20 && ActionUnlocked(AID.ShieldLob);
        var bestCast = canHoly ? AID.HolySpirit : canLob ? AID.ShieldLob : default;
        var first = !_firstGcdDone && Player.InCombat;
        var (use, aid, prio) = strategy.Option(AkechiPLD.Track.Ranged).As<AkechiPLD.RangedStrategy>() switch
        {
            AkechiPLD.RangedStrategy.Automatic or AkechiPLD.RangedStrategy.RangedCast => (outOfMelee && bestCast != default, bestCast, ActionQueue.Priority.High + 1),
            AkechiPLD.RangedStrategy.RangedCastStationary => (outOfMelee && bestCast != default && !isMoving, bestCast, ActionQueue.Priority.High + 1),
            AkechiPLD.RangedStrategy.OpenerRangedCast => (first && outOfMelee && bestCast != default, bestCast, ActionQueue.Priority.High + 3),
            AkechiPLD.RangedStrategy.OpenerCast => (first && bestCast != default, bestCast, ActionQueue.Priority.High + 3),
            AkechiPLD.RangedStrategy.ForceCast => (bestCast != default, bestCast, ActionQueue.Priority.High + 3),
            AkechiPLD.RangedStrategy.OpenerRanged => (first && outOfMelee && canLob, AID.ShieldLob, ActionQueue.Priority.High + 3),
            AkechiPLD.RangedStrategy.Opener => (first && canLob, AID.ShieldLob, ActionQueue.Priority.High + 3),
            AkechiPLD.RangedStrategy.Force => (canLob, AID.ShieldLob, ActionQueue.Priority.High + 3),
            AkechiPLD.RangedStrategy.Ranged => (outOfMelee && canLob, AID.ShieldLob, ActionQueue.Priority.High + 1),
            _ => (false, default(AID), 0f)
        };
        if (use)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(aid), Target, prio, castTime: aid == AID.HolySpirit && !holyInstant ? 1.5f : 0);
    }

    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        // whole 1000s only (what a spell costs): natural regen would otherwise change the state, and start a new search, every tick
        s.Gauges[Job.GaugeIndex(PldDefinition.MP)] = (short)(Math.Min(10000, Player.HPMP.CurMP) / 1000 * 1000);
        var step = World.Client.GetGauge<PaladinGauge>().ConfiteorComboStep switch { 1 => PldDefinition.FaithReady, 2 => PldDefinition.TruthReady, 3 => PldDefinition.ValorReady, _ => null };
        if (step != null)
        {
            var i = Job.StatusIndex(step);
            s.StatusLeft[i] = 30;
            s.StatusStacks[i] = 1;
        }

        ReadStatus(ref s, Job.StatusIndex(PldDefinition.FightOrFlight), Player, (uint)SID.FightOrFlight);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.GoringReady), Player, (uint)SID.GoringBladeReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.Requiescat), Player, (uint)SID.Requiescat);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.ConfiteorReady), Player, (uint)SID.ConfiteorReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.HonorReady), Player, (uint)SID.BladeOfHonorReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.AtonementReady), Player, (uint)SID.AtonementReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.SupplicationReady), Player, (uint)SID.SupplicationReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.SepulchreReady), Player, (uint)SID.SepulchreReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.DivineMight), Player, (uint)SID.DivineMight);

        ReadCooldown(ref s, Job.CooldownIndex(PldDefinition.FightOrFlightCD), ActionID.MakeSpell(AID.FightOrFlight));
        ReadCooldown(ref s, Job.CooldownIndex(PldDefinition.ImperatorCD), ActionID.MakeSpell(ActionUnlocked(AID.Imperator) ? AID.Imperator : AID.Requiescat)); // Requiescat below 96
        ReadCooldown(ref s, Job.CooldownIndex(PldDefinition.ExpiacionCD), ActionID.MakeSpell(AID.Expiacion));
        ReadCooldown(ref s, Job.CooldownIndex(PldDefinition.CircleOfScornCD), ActionID.MakeSpell(AID.CircleOfScorn));
        ReadCombo(ref s);
    }
}
