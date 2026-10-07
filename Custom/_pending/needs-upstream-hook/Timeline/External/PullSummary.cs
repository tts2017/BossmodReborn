namespace BossMod;

// Everything the timeline extractor reads from one pull, detached from the replay: once a replay is summarized it can be dropped,
// and the summaries of every replay of a zone are enough to rebuild its timeline. Times are seconds from the pull start, except the
// boss HP history and existence, which keep absolute ticks so the HP lookup at a decision point matches the replay lookup exactly.
public sealed record PullSummary(ushort Zone, DateTime Start, float Duration, IReadOnlyList<uint> BossOIDs, IReadOnlyList<PullSummary.BossEvent> Events,
    IReadOnlyList<ReplayTimelineExtractor.Window> NoTarget, IReadOnlyList<ReplayTimelineExtractor.Window> BossUntargetable, IReadOnlyList<PullSummary.BossHP> Bosses)
{
    public sealed record BossEvent(float Time, uint ID, ExternalPlannerTimeline.ExternalStateKind Kind);
    public sealed record TickRange(long Start, long End);
    public sealed record HPPoint(long Ticks, uint CurHP, uint MaxHP);
    public sealed record BossHP(uint OID, uint MaxHP, IReadOnlyList<TickRange> Existence, IReadOnlyList<HPPoint> History);

    // eventBossOIDs: whose casts and effects count as sync points. Build takes them from the first pull of a boss set, so the
    // wrapper that builds from raw pulls passes that list; a pull summarized on its own uses its own bosses.
    public static PullSummary Summarize(ReplayTimelineExtractor.Pull pull, IReadOnlyList<uint>? eventBossOIDs = null)
    {
        var events = ReplayTimelineExtractor.BossEvents(pull, (eventBossOIDs ?? pull.BossOIDs).ToList())
            .OrderBy(e => e.Time) // stable: ties keep the cast-then-effect order Build saw before
            .Select(e => new BossEvent(pull.Seconds(e.Time), e.ID, e.Kind))
            .ToList();
        var bosses = pull.Enemies.Where(p => pull.BossOIDs.Contains(p.OID))
            .Select(p => new BossHP(p.OID, ReplayTimelineExtractor.MaxHP(p),
                p.WorldExistence.Select(r => new TickRange(r.Start.Ticks, r.End.Ticks)).ToList(),
                p.HPMPHistory.Select(kv => new HPPoint(kv.Key.Ticks, kv.Value.CurHP, kv.Value.MaxHP)).ToList()))
            .ToList();
        return new(pull.Zone, pull.Start, pull.Seconds(pull.End), pull.BossOIDs.ToList(), events,
            ReplayTimelineExtractor.NoTargetWindows(pull).ToList(), ReplayTimelineExtractor.BossUntargetableWindows(pull).ToList(), bosses);
    }

    // Same as ReplayTimelineExtractor.PrimaryBossHPPercent(Pull, seconds): of the bosses existing at that moment, the one with the
    // largest max HP (first in pull order on ties), and its latest HP entry at or before the moment.
    public static float? PrimaryBossHPPercent(PullSummary pull, float seconds)
    {
        var t = pull.Start.AddSeconds(seconds).Ticks;
        BossHP? boss = null;
        foreach (var b in pull.Bosses)
            if (b.MaxHP > 0 && b.Existence.Any(r => t >= r.Start && t <= r.End) && (boss == null || b.MaxHP > boss.MaxHP))
                boss = b;
        if (boss == null)
            return null;
        HPPoint? hp = null;
        foreach (var point in boss.History)
        {
            if (point.Ticks > t)
                break;
            hp = point;
        }
        return hp is { MaxHP: > 0 } ? 100f * hp.CurHP / hp.MaxHP : null;
    }
}
