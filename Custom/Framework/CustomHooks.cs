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
}
