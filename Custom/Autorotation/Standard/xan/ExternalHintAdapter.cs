namespace BossMod.Autorotation.xan;

// Thin adapter used by in-process hint sources (e.g. SplatoonHintSource) to feed the shared BossMod external hint providers,
// which are the same stores that IPC and SplatoonSafeImport write to and that rotation modules read from.
public static class ExternalHintAdapter
{
    private const string NamespacePrefix = "bossmod.external.splatoon.xan.";
    private const float MaxMechanicLifetime = 5f;
    private const float MaxEncounterLifetime = 2f;

    public static bool PushMechanicHint(
        string source,
        uint territoryId,
        uint contentId,
        DateTime now,
        float validForSeconds,
        float forcedMoveIn = float.MaxValue,
        float targetLossIn = float.MaxValue,
        float targetReturnIn = float.MaxValue,
        float leyLinesUnsafeIn = float.MaxValue,
        bool safeSpotAvailable = false)
    {
        if (!ValidSource(source) || !ValidLifetime(validForSeconds))
            return false;

        var snapshot = new ExternalMechanicHintSnapshot(
            NamespaceFor(source),
            (ushort)territoryId,
            (ushort)contentId,
            ExternalZoneConfidence.TrustedSplatoonScript,
            now.AddSeconds(Math.Min(validForSeconds, MaxMechanicLifetime)),
            forcedMoveIn,
            targetLossIn,
            targetReturnIn,
            leyLinesUnsafeIn,
            safeSpotAvailable);

        return ExternalMechanicHintProvider.PushSnapshot(snapshot, (ushort)territoryId, (ushort)contentId, now);
    }

    public static bool PushEncounterHint(
        string source,
        uint territoryId,
        uint contentId,
        DateTime now,
        float validForSeconds,
        bool isTrashPhase = false,
        bool isMajorAddPhase = false,
        bool shouldHoldBurst = false,
        bool forceBurst = false,
        bool targetLostWithin5s = false,
        bool targetReturnsWithin10s = false)
    {
        if (!ValidSource(source) || !ValidLifetime(validForSeconds))
            return false;

        var snapshot = new ExternalEncounterHintSnapshot(
            NamespaceFor(source),
            (ushort)territoryId,
            (ushort)contentId,
            ExternalZoneConfidence.TrustedSplatoonScript,
            now.AddSeconds(Math.Min(validForSeconds, MaxEncounterLifetime)),
            isTrashPhase,
            isMajorAddPhase,
            shouldHoldBurst,
            ShouldHoldDokumori: false,
            ShouldForbidTCJ: false,
            forceBurst,
            targetLostWithin5s,
            targetReturnsWithin10s,
            TargetableThroughTCJ: true,
            TargetableDuringKunai: true);

        return ExternalEncounterHintProvider.PushSnapshot(snapshot, (ushort)territoryId, (ushort)contentId, now);
    }

    private static string NamespaceFor(string source) => NamespacePrefix + source;

    private static bool ValidSource(string source)
        => !string.IsNullOrWhiteSpace(source);

    private static bool ValidLifetime(float validForSeconds)
        => !float.IsNaN(validForSeconds) && !float.IsInfinity(validForSeconds) && validForSeconds > 0;
}
