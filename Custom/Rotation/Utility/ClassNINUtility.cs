namespace BossMod.Autorotation.Custom;

public sealed class ClassNINUtility(RotationModuleManager manager, Actor player) : RoleMeleeUtility(manager, player)
{
    public enum Track { ShadeShift = SharedTrack.Count, Shukuchi }
    public enum DashStrategy { None, GapClose, GapCloseHold1 }

    public static readonly ActionID IDLimitBreak3 = ActionID.MakeSpell(NIN.AID.Chimatsuri);

    public static RotationModuleDefinition Definition()
    {
        var res = new RotationModuleDefinition("Utility: NIN [Custom]", "Cooldown Planner support for Utility Actions.\nNOTE: This is NOT a rotation preset! All Utility modules are STRICTLY for cooldown-planning usage.", "Utility for planner", "Akechi", RotationModuleQuality.Good, BitMask.Build((int)Class.NIN), 100);
        DefineShared(res, IDLimitBreak3);

        DefineSimpleConfig(res, Track.ShadeShift, "Shade", "", 400, NIN.AID.ShadeShift, 20);

        res.Define(Track.Shukuchi).As<DashStrategy>("Shukuchi", "Dash", 20)
            .AddOption(DashStrategy.None, "No use.")
            .AddOption(DashStrategy.GapClose, "Use as gapcloser if outside melee range", 60, 0, ActionTargets.Area, 45)
            .AddOption(DashStrategy.GapCloseHold1, "Use as gapcloser if outside melee range; conserves 1 charge for manual usage", 60, 0, ActionTargets.Area, 74)
            .AddAssociatedActions(NIN.AID.Shukuchi);

        return res;
    }

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        ExecuteShared(strategy, IDLimitBreak3, primaryTarget);
        ExecuteSimple(strategy.Option(Track.ShadeShift), NIN.AID.ShadeShift, Player);

        var dash = strategy.Option(Track.Shukuchi);
        var dashStrategy = strategy.Option(Track.Shukuchi).As<DashStrategy>();
        // an explicit point target is the landing spot itself (point kinds resolve to no actor, so without this
        // the primary target's hitbox edge would be used for both the landing position and the range gate)
        var dashToPoint = dash.Value.Target is StrategyTarget.PointAbsolute or StrategyTarget.PointCenter or StrategyTarget.PointWaymark;
        var dashTarget = dashToPoint ? null : ResolveTarget(dash.Value) ?? primaryTarget;
        var dashTargetPos = ShukuchiTargetPosition(dash.Value, dashTarget);
        var distance = dashTarget != null ? Player.DistanceToHitbox(dashTarget) : Player.DistanceToPoint(dashTargetPos);
        var cd = World.Client.Cooldowns[ActionDefinitions.Instance.Spell(NIN.AID.Shukuchi)!.MainCooldownGroup].Remaining;
        var shouldDash = dashStrategy switch
        {
            DashStrategy.None => false,
            DashStrategy.GapClose => distance is > 3f and <= 20f,
            DashStrategy.GapCloseHold1 => distance is > 3f and <= 20f && cd < 0.6f,
            _ => false,
        };
        if (shouldDash)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(NIN.AID.Shukuchi), null, dash.Priority(), dash.Value.ExpireIn, targetPos: dashTargetPos.ToVec3(Player.PosRot.Y));
    }

    private WPos ShukuchiTargetPosition(in StrategyValueTrack strategy, Actor? target)
    {
        if (target == null)
            return ResolveTargetLocation(strategy);

        var dir = Player.Position - target.Position;
        return dir.LengthSq() > 0.01f
            ? target.Position + dir.Normalized() * (target.HitboxRadius + Player.HitboxRadius)
            : target.Position;
    }
}
