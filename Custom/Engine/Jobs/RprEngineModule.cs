using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.RPR;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanRPR = BossMod.Autorotation.xan.Custom.RPR;

namespace BossMod.Autorotation;

// "RPR [Engine]": Reaper driven by the rotation engine (Custom/Engine). Level 30+ (the full Slice combo): the definition is
// built for the player's level, and BMR recreates the module when the level changes (level sync). The strategy tracks are those
// of xan RPR [Custom]; below level 100 only the AOE / Targeting / MechanicHints settings act.
public sealed class RprEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    // harnesses: replaces the built-in weights for modules created afterwards
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => new RotationModuleDefinition("RPR [Engine]", "Reaper on the two-tier rotation engine (burst-window planning + short search). Experimental, level 30+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.RPR), 100, 30)
            .WithStrategies<XanRPR.Strategy>();

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, player.Level) : 2.5f;
        // the search runs in slices of FrameBudgetMs per frame (continuing on the next frames while the state is unchanged), so a
        // single frame never pays for the whole BudgetMs
        return new RotationEngine(RprDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? RprDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.08f };
    }

    private XanRPR.Strategy _strategy;

    protected override Actor? SelectTarget(StrategyValues strategy, Actor? primaryTarget)
    {
        _strategy = ValueConverter.FromValues<XanRPR.Strategy>(strategy);
        SetMechanicHints(_strategy.MechanicHints);
        return SelectTarget(_strategy.Targeting.Value, primaryTarget, 3);
    }

    protected override void ApplyStrategy(StrategyValues strategy, ref EngineState s, Actor? primaryTarget)
    {
        var st = _strategy;
        ApplyAoe(ref s, st.AOE);
        if (Player.Level < 100)
            return;

        // basic mode: the standard combo and gauge spending only (no Arcane Circle unless forced, no Gluttony, no potion)
        var full = st.RotationMode.Value == XanRPR.RotationModeStrategy.FullMode;
        if (!full)
        {
            if (st.Buffs.Value != OffensiveStrategy.Force)
                Forbid(ref s, "ArcaneCircle");
            Forbid(ref s, "Gluttony");
        }
        ReadPotion(ref s, ActionDefinitions.IDPotionStr, full && st.Potion.Value switch
        {
            XanRPR.PotionUseStrategy.OpenerAndEvenBurst => true,
            XanRPR.PotionUseStrategy.EvenBurstExceptOpener => CombatTime > 60,
            _ => false
        });

        if (st.Buffs.Value == OffensiveStrategy.Delay)
            Forbid(ref s, "ArcaneCircle");
        else if (st.Buffs.Value == OffensiveStrategy.Force)
            Force("ArcaneCircle");

        if (st.Enshroud.Value == OffensiveStrategy.Delay)
        {
            Forbid(ref s, "Enshroud");
            Forbid(ref s, "EnshroudIdeal");
        }
        else if (st.Enshroud.Value == OffensiveStrategy.Force)
        {
            Force("EnshroudIdeal");
            Force("Enshroud");
        }

        if (st.Communio.Value == EnabledByDefault.Disabled)
            Forbid(ref s, "Communio");

        switch (st.Slice.Value)
        {
            case XanRPR.SliceStrategy.Delay:
                Forbid(ref s, "SoulSlice");
                Forbid(ref s, "SoulScythe");
                break;
            case XanRPR.SliceStrategy.Force:
                if (s.Targets >= 3)
                    Force("SoulScythe");
                Force("SoulSlice");
                break;
        }

        switch (st.RedGauge.Value)
        {
            case XanRPR.RedGaugeStrategy.Delay:
                Forbid(ref s, "BloodStalk");
                Forbid(ref s, "GrimSwathe");
                Forbid(ref s, "Gluttony");
                break;
            case XanRPR.RedGaugeStrategy.ReserveGluttony:
                // Soul is kept for Gluttony: the minor spenders only stop it overcapping
                if (s.Gauges[Job.GaugeIndex(RprDefinition.Soul)] < 100)
                {
                    Forbid(ref s, "BloodStalk");
                    Forbid(ref s, "GrimSwathe");
                }
                break;
            case XanRPR.RedGaugeStrategy.Force:
                Force("Gluttony");
                if (s.Targets >= 3)
                    Force("GrimSwathe");
                Force("BloodStalk");
                break;
        }

        if (st.PH.Value == EnabledByDefault.Disabled)
            Forbid(ref s, "PlentifulHarvest");

        if (st.HM.Value == OffensiveStrategy.Delay)
            Forbid(ref s, "HarvestMoon");
        else if (st.HM.Value == OffensiveStrategy.Force)
            Force("HarvestMoon");

        switch (st.Perf.Value)
        {
            case XanRPR.PerfectioStrategy.Delay:
                Forbid(ref s, "Perfectio");
                break;
            case XanRPR.PerfectioStrategy.Ranged:
                // kept for when the target cannot be reached in melee
                if (Player.DistanceToHitbox(primaryTarget) <= 3)
                    Forbid(ref s, "Perfectio");
                else
                    Force("Perfectio");
                break;
        }

        // in combat with the setting off, Soulsow is kept for a downtime (no attackable target); out of combat it is always cast, see Execute
        if (st.Soulsow.Value == DisabledByDefault.Disabled && Player.InCombat && primaryTarget is { IsTargetable: true })
            Forbid(ref s, "Soulsow");
    }

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        base.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        if (Player.Level < 100)
            return;
        var st = _strategy;
        UpdatePositional(Target, st.TrueNorth.Value == XanRPR.TrueNorthStrategy.Auto);
        LastLemure(st);
        Harpe(st, isMoving);
        if (!Player.InCombat && SelfStatusLeft(SID.Soulsow) <= 0)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Soulsow), Player, ActionQueue.Priority.High + 2, castTime: 5);
        // Arcane Crest for damage to us predicted within 5 s
        if (st.AutoCrest.Value == EnabledByDefault.Enabled && Player.InCombat && PredictedDamageWithin(5))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.ArcaneCrest), Player, ActionQueue.Priority.Low);
    }

    // Communio turned off: the last Lemure goes into a reaping (the definition keeps it for Communio)
    private void LastLemure(in XanRPR.Strategy st)
    {
        if (st.Communio.Value == EnabledByDefault.Enabled || SelfStatusLeft(SID.Enshrouded) <= 0 || World.Client.GetGauge<ReaperGauge>().LemureShroud != 1)
            return;
        var aid = SelfStatusLeft(SID.EnhancedCrossReaping) > 0 ? AID.CrossReaping : AID.VoidReaping;
        Hints.ActionsToExecute.Push(ActionID.MakeSpell(aid), Target, ActionQueue.Priority.High + 3);
    }

    // Harpe (1.3 s cast, range 25): Automatic with Enhanced Harpe or while standing out of melee range, Ranged whenever out of melee range.
    // Below the engine's GCD, so it only goes off when that one cannot (out of range)
    private void Harpe(in XanRPR.Strategy st, bool isMoving)
    {
        if (st.Harpe.Value == XanRPR.HarpeStrategy.Forbid || Target == null || !Player.InCombat || SelfStatusLeft(SID.Enshrouded) > 0
            || SelfStatusLeft(SID.SoulReaver) > 0 || SelfStatusLeft(SID.Executioner) > 0 || Player.DistanceToHitbox(Target) > 25)
            return;
        var outOfReach = Player.DistanceToHitbox(Target) > 3;
        var use = st.Harpe.Value == XanRPR.HarpeStrategy.Ranged ? outOfReach : SelfStatusLeft(SID.EnhancedHarpe) > GCD || outOfReach && !isMoving;
        if (use)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Harpe), Target, ActionQueue.Priority.High + 1, castTime: SelfStatusLeft(SID.EnhancedHarpe) > GCD ? 0 : 1.3f);
    }

    // Gibbet wants the flank, Gallows the rear (as in the regular modules): publish the positional and use True North when it would be missed
    private void UpdatePositional(Actor? target, bool useTrueNorth)
    {
        var next = LastDecision.NextGcd >= 0 ? Job.Skills[LastDecision.NextGcd].Name : "";
        var pos = next is "Gibbet" or "ExecutionersGibbet" ? Positional.Flank : next is "Gallows" or "ExecutionersGallows" ? Positional.Rear : Positional.Any;
        if (target == null || target.Omnidirectional || pos == Positional.Any || target.TargetID == Player.InstanceID && target.CastInfo == null && !target.IsStrikingDummy)
        {
            Hints.RecommendedPositional = (target, Positional.Any, false, true);
            return;
        }
        var trueNorth = StatusDetails(Player, ClassShared.SID.TrueNorth, Player.InstanceID).Left > GCD;
        var dot = target.Rotation.ToDirection().Dot((Player.Position - target.Position).Normalized());
        var correct = trueNorth || (pos == Positional.Flank ? MathF.Abs(dot) < 0.7071067f : dot < -0.7071068f);
        var imminent = !trueNorth && GCD < 2.5f;
        Hints.RecommendedPositional = (target, pos, imminent, correct);
        if (useTrueNorth && imminent && !correct && Player.Level >= 50)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.TrueNorth), Player, ActionQueue.Priority.Low + 2, delay: MathF.Max(0, GCD - 0.8f));
    }

    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    protected override ActionID ActionFor(SkillDef skill) => skill.Name switch
    {
        "Potion" => ActionDefinitions.IDPotionStr,
        "BloodStalk" when Player.FindStatus(SID.EnhancedGibbet) != null => ActionID.MakeSpell(AID.UnveiledGibbet),
        "BloodStalk" when Player.FindStatus(SID.EnhancedGallows) != null => ActionID.MakeSpell(AID.UnveiledGallows),
        _ => base.ActionFor(skill)
    };

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        var gauge = World.Client.GetGauge<ReaperGauge>();
        s.Gauges[Job.GaugeIndex(RprDefinition.Soul)] = gauge.Soul;
        s.Gauges[Job.GaugeIndex(RprDefinition.Shroud)] = gauge.Shroud;
        s.Gauges[Job.GaugeIndex(RprDefinition.Lemure)] = gauge.LemureShroud;
        s.Gauges[Job.GaugeIndex(RprDefinition.Void)] = gauge.VoidShroud;

        ReadStatus(ref s, Job.StatusIndex(RprDefinition.DeathsDesign), primaryTarget, (uint)SID.DeathsDesign);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.ArcaneCircle), Player, (uint)SID.ArcaneCircle);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.SoulReaver), Player, (uint)SID.SoulReaver);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.Executioner), Player, (uint)SID.Executioner);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.EnhancedGibbet), Player, (uint)SID.EnhancedGibbet);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.EnhancedGallows), Player, (uint)SID.EnhancedGallows);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.EnhancedVoid), Player, (uint)SID.EnhancedVoidReaping);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.EnhancedCross), Player, (uint)SID.EnhancedCrossReaping);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.Enshrouded), Player, (uint)SID.Enshrouded);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.Oblatio), Player, (uint)SID.Oblatio);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.IdealHost), Player, (uint)SID.IdealHost);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.PerfectioOcculta), Player, (uint)SID.PerfectioOcculta);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.PerfectioParata), Player, (uint)SID.PerfectioParata);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.ImmortalSacrifice), Player, (uint)SID.ImmortalSacrifice, fromPlayer: false);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.Bloodsown), Player, (uint)SID.BloodsownCircle);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.Soulsow), Player, (uint)SID.Soulsow);

        ReadCooldown(ref s, Job.CooldownIndex(RprDefinition.SoulSliceCD), ActionID.MakeSpell(AID.SoulSlice));
        ReadCooldown(ref s, Job.CooldownIndex(RprDefinition.ArcaneCircleCD), ActionID.MakeSpell(AID.ArcaneCircle));
        ReadCooldown(ref s, Job.CooldownIndex(RprDefinition.GluttonyCD), ActionID.MakeSpell(AID.Gluttony));
        ReadCooldown(ref s, Job.CooldownIndex(RprDefinition.EnshroudCD), ActionID.MakeSpell(AID.Enshroud));
        var potion = Job.CooldownIndex(RprDefinition.PotionCD);
        s.Charges[potion] = 0;
        s.CdReadyIn[potion] = 10000;
        ReadCombo(ref s);
    }
}
