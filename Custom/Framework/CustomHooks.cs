namespace BossMod;

// Static entry points called from the few lines added to upstream files. Each one forwards to the running CustomPlugin
// and is a no-op when it does not exist (replay analysis, offline harnesses).
public static class CustomHooks
{
    // AIHintsBuilder.Update, right before hints.Normalize(): add hint sources from outside the boss modules
    public static void HintsGathered(AIHints hints, WorldState ws) => CustomPlugin.Instance?.HintsGathered(hints, ws);

    // ActionManagerEx.FinishActionGather, right after FindBest: lets the local rotation AI replace the chosen action
    public static ActionQueue.Entry SelectAutoQueue(ActionQueue.Entry baseline, WorldState ws, Actor player, AIHints hints, float animationLock, float instantAnimLockDelay, bool allowDismount)
        => CustomPlugin.Instance?.SelectAutoQueue(baseline, ws, player, hints, animationLock, instantAnimLockDelay, allowDismount) ?? baseline;

    // ManualActionQueueTweak.Push: how long a manually pressed action stays queued. Ley Lines is kept for 6 s so a press during a
    // GCD-heavy stretch is not lost (the old fork also kept it through emergency mode; that part needs a larger hook and is not done).
    public static float ManualQueueLifetime(ActionID action, float defaultLifetime)
        => action == ActionID.MakeSpell(BLM.AID.LeyLines) ? Math.Max(defaultLifetime, 6.0f) : defaultLifetime;

    // ManualActionQueueTweak.ResolveTarget: ground-targeted action auto-cast at an actor. Shukuchi lands on the edge of the target's
    // hitbox circle (capped by the action range) instead of its centre.
    public static Vector3 GroundTargetAtActor(ActionDefinition def, Actor player, Actor target)
    {
        if (def.ID.ID != (uint)NIN.AID.Shukuchi)
            return target.PosRot.XYZ();
        var toTarget = target.Position - player.Position;
        var centerDistance = toTarget.Length();
        if (centerDistance <= 0.001f)
            return player.PosRot.XYZ();
        var direction = toTarget / centerDistance;
        var moveDistance = MathF.Min(MathF.Max(0, def.Range), MathF.Max(0, centerDistance - target.HitboxRadius));
        return (player.Position + direction * moveDistance).ToVec3(player.PosRot.Y);
    }
}