using System;
using System.Collections.Generic;

namespace BossMod.Autorotation.xan;

public readonly record struct SplatoonHintEvent(
    string Tag,
    DateTime? EventTime = null,
    float EventCombatTime = float.MaxValue);

public static class SplatoonHintSource
{
    public const string SourceMove = "Splatoon:Move";
    public const string SourceTarget = "Splatoon:Target";
    public const string SourceLeyLines = "Splatoon:LeyLines";
    public const string SourceEncounter = "Splatoon:Encounter";

    public const string TagMove = "BLM_HINT_MOVE";
    public const string TagTargetLoss = "BLM_HINT_TARGET_LOSS";
    public const string TagTargetReturn = "BLM_HINT_TARGET_RETURN";
    public const string TagLeyLinesUnsafe = "BLM_HINT_LL_UNSAFE";
    public const string TagSafeSpot = "BLM_HINT_SAFE_SPOT";
    public const string TagHoldBurst = "BLM_HINT_HOLD_BURST";
    public const string TagForceBurst = "BLM_HINT_FORCE_BURST";
    public const string TagTrash = "BLM_HINT_TRASH";
    public const string TagMajorAdd = "BLM_HINT_MAJOR_ADD";

    private const float RealtimeHintLifetimeSeconds = 0.75f;
    private const float TimelineHintLifetimeSeconds = 1.5f;
    private const float MaxHintLifetimeSeconds = 2.0f;
    private const float MovePushWindowSeconds = 10;
    private const float LeyLinesUnsafePushWindowSeconds = 15;
    private const float TargetPushWindowSeconds = 30;

    public static bool PushTaggedHints(IEnumerable<SplatoonHintEvent> hints, uint territoryId, uint contentId, DateTime now, float combatTimer, bool timelineStable = false)
        => PushTaggedHints(hints, territoryId, contentId, now, combatTimer, timelineStable ? TimelineHintLifetimeSeconds : RealtimeHintLifetimeSeconds);

    public static bool PushTaggedHints(IEnumerable<SplatoonHintEvent> hints, uint territoryId, uint contentId, DateTime now, float combatTimer, float validForSeconds)
    {
        if (hints == null)
            return false;

        var ttl = NormalizeLifetime(validForSeconds);
        var forcedMoveIn = float.MaxValue;
        var targetLossIn = float.MaxValue;
        var targetReturnIn = float.MaxValue;
        var leyLinesUnsafeIn = float.MaxValue;
        var safeSpotAvailable = false;
        var shouldHoldBurst = false;
        var forceBurst = false;
        var isTrashPhase = false;
        var isMajorAddPhase = false;

        foreach (var hint in hints)
        {
            if (string.IsNullOrWhiteSpace(hint.Tag))
                continue;

            var seconds = SecondsUntil(hint, now, combatTimer);
            switch (hint.Tag.Trim())
            {
                case TagMove:
                    if (Within(seconds, MovePushWindowSeconds))
                        forcedMoveIn = Math.Min(forcedMoveIn, seconds);
                    break;
                case TagTargetLoss:
                    if (Within(seconds, TargetPushWindowSeconds))
                        targetLossIn = Math.Min(targetLossIn, seconds);
                    break;
                case TagTargetReturn:
                    if (Within(seconds, TargetPushWindowSeconds))
                        targetReturnIn = Math.Min(targetReturnIn, seconds);
                    break;
                case TagLeyLinesUnsafe:
                    if (Within(seconds, LeyLinesUnsafePushWindowSeconds))
                        leyLinesUnsafeIn = Math.Min(leyLinesUnsafeIn, seconds);
                    break;
                case TagSafeSpot:
                    safeSpotAvailable = true;
                    break;
                case TagHoldBurst:
                    shouldHoldBurst = true;
                    break;
                case TagForceBurst:
                    forceBurst = true;
                    break;
                case TagTrash:
                    isTrashPhase = true;
                    break;
                case TagMajorAdd:
                    isMajorAddPhase = true;
                    break;
            }
        }

        var pushed = false;
        if (forcedMoveIn != float.MaxValue || safeSpotAvailable)
            pushed |= ExternalHintAdapter.PushMechanicHint(SourceMove, territoryId, contentId, now, ttl, forcedMoveIn: forcedMoveIn, safeSpotAvailable: safeSpotAvailable);
        if (targetLossIn != float.MaxValue || targetReturnIn != float.MaxValue)
            pushed |= ExternalHintAdapter.PushMechanicHint(SourceTarget, territoryId, contentId, now, ttl, targetLossIn: targetLossIn, targetReturnIn: targetReturnIn);
        if (leyLinesUnsafeIn != float.MaxValue)
            pushed |= ExternalHintAdapter.PushMechanicHint(SourceLeyLines, territoryId, contentId, now, ttl, leyLinesUnsafeIn: leyLinesUnsafeIn);

        var targetLostWithin5s = targetLossIn <= 5f;
        var targetReturnsWithin10s = targetReturnIn <= 10f;
        if (shouldHoldBurst || forceBurst || isTrashPhase || isMajorAddPhase || targetLostWithin5s || targetReturnsWithin10s)
        {
            pushed |= ExternalHintAdapter.PushEncounterHint(
                source: SourceEncounter,
                territoryId: territoryId,
                contentId: contentId,
                now: now,
                validForSeconds: ttl,
                isTrashPhase: isTrashPhase,
                isMajorAddPhase: isMajorAddPhase,
                shouldHoldBurst: shouldHoldBurst,
                forceBurst: forceBurst,
                targetLostWithin5s: targetLostWithin5s,
                targetReturnsWithin10s: targetReturnsWithin10s);
        }

        return pushed;
    }

    private static float SecondsUntil(SplatoonHintEvent hint, DateTime now, float combatTimer)
    {
        if (hint.EventTime is { } eventTime)
            return Math.Max(0, (float)(eventTime - now).TotalSeconds);

        if (ValidSeconds(hint.EventCombatTime) && ValidSeconds(combatTimer))
            return Math.Max(0, hint.EventCombatTime - combatTimer);

        return float.MaxValue;
    }

    private static bool Within(float seconds, float limit)
        => ValidSeconds(seconds) && seconds <= limit;

    private static bool ValidSeconds(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 && value != float.MaxValue;

    private static float NormalizeLifetime(float validForSeconds)
        => ValidSeconds(validForSeconds)
            ? Math.Clamp(validForSeconds, 0.05f, MaxHintLifetimeSeconds)
            : RealtimeHintLifetimeSeconds;
}
