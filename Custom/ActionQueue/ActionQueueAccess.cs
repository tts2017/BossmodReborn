namespace BossMod;

// Access to upstream internals without editing upstream files.
// UnsafeAccessor binds to the private member at JIT time; if upstream renames or changes the signature,
// the call throws MissingMethodException at runtime (not a build error), so keep the signature in sync.
internal static class ActionQueueAccess
{
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CanExecute")]
    private static extern bool CanExecuteImpl(ActionQueue queue, in ActionQueue.Entry entry, ActionDefinition? def, WorldState ws, Actor player, AIHints hints, bool allowDismount);

    public static bool CanExecuteEx(this ActionQueue queue, in ActionQueue.Entry entry, ActionDefinition? def, WorldState ws, Actor player, AIHints hints, bool allowDismount)
        => CanExecuteImpl(queue, in entry, def, ws, player, hints, allowDismount);
}
