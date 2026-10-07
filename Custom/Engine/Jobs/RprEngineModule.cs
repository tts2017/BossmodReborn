using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.RPR;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace BossMod.Autorotation;

// "RPR [Engine]": Reaper driven by the rotation engine (Custom/Engine). Level 100 only; below that it does nothing,
// so use it next to (not instead of) a regular RPR module when levelling. No potion use yet (the potion cooldown is
// reported as unavailable).
public sealed class RprEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    // harnesses: replaces the built-in weights for modules created afterwards
    public static EngineWeights? WeightsOverride;

    public static RotationModuleDefinition Definition()
        => new("RPR [Engine]", "Reaper on the two-tier rotation engine (burst-window planning + short search). Experimental, level 100.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.RPR), 100, 100);

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, player.Level) : 2.5f;
        return new RotationEngine(RprDefinition.Build(gcd), WeightsOverride?.Clone() ?? RprDefinition.DefaultWeights());
    }

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        base.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        UpdatePositional(primaryTarget);
    }

    // Gibbet wants the flank, Gallows the rear (as in the regular modules): publish the positional and use True North when it would be missed
    private void UpdatePositional(Actor? target)
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
        if (imminent && !correct && Player.Level >= 50)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.TrueNorth), Player, ActionQueue.Priority.Low + 2, delay: MathF.Max(0, GCD - 0.8f));
    }

    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    protected override ActionID ActionFor(SkillDef skill) => skill.Name switch
    {
        "Potion" => default,
        "BloodStalk" when Player.FindStatus(SID.EnhancedGibbet) != null => ActionID.MakeSpell(AID.UnveiledGibbet),
        "BloodStalk" when Player.FindStatus(SID.EnhancedGallows) != null => ActionID.MakeSpell(AID.UnveiledGallows),
        _ => base.ActionFor(skill)
    };

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        if (Player.Level < 100)
        {
            // outside the definition's coverage: make every skill unusable (cooldowns unavailable, no gauge, no combo)
            for (var c = 0; c < Job.Cooldowns.Length; ++c)
            {
                s.Charges[c] = 0;
                s.CdReadyIn[c] = 10000;
            }
            return;
        }

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
