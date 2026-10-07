namespace BossMod.Autorotation.xan;

public static class SplatoonHintBridge
{
    public static bool PushCombatTimeHints(
        uint territoryId,
        uint contentId,
        DateTime now,
        float combatTimer,
        bool timelineStable,
        float moveCombatTime = float.MaxValue,
        float targetLossCombatTime = float.MaxValue,
        float targetReturnCombatTime = float.MaxValue,
        float leyLinesUnsafeCombatTime = float.MaxValue,
        bool holdBurst = false,
        bool forceBurst = false,
        bool trash = false,
        bool majorAdd = false)
    {
        var hints = new List<SplatoonHintEvent>();
        AddCombatTimeHint(hints, SplatoonHintSource.TagMove, moveCombatTime);
        AddCombatTimeHint(hints, SplatoonHintSource.TagTargetLoss, targetLossCombatTime);
        AddCombatTimeHint(hints, SplatoonHintSource.TagTargetReturn, targetReturnCombatTime);
        AddCombatTimeHint(hints, SplatoonHintSource.TagLeyLinesUnsafe, leyLinesUnsafeCombatTime);
        AddFlagHint(hints, SplatoonHintSource.TagHoldBurst, holdBurst, combatTimer);
        AddFlagHint(hints, SplatoonHintSource.TagForceBurst, forceBurst, combatTimer);
        AddFlagHint(hints, SplatoonHintSource.TagTrash, trash, combatTimer);
        AddFlagHint(hints, SplatoonHintSource.TagMajorAdd, majorAdd, combatTimer);

        return SplatoonHintSource.PushTaggedHints(hints, territoryId, contentId, now, combatTimer, timelineStable);
    }

    private static void AddCombatTimeHint(List<SplatoonHintEvent> hints, string tag, float combatTime)
    {
        if (ValidSeconds(combatTime))
            hints.Add(new SplatoonHintEvent(tag, EventCombatTime: combatTime));
    }

    private static void AddFlagHint(List<SplatoonHintEvent> hints, string tag, bool enabled, float combatTimer)
    {
        if (enabled)
            hints.Add(new SplatoonHintEvent(tag, EventCombatTime: combatTimer));
    }

    private static bool ValidSeconds(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value != float.MaxValue;
}
