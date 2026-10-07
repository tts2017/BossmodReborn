namespace BossMod;

// Builds the FightTimeEstimator prior of one fight (one zone + boss set) from the replay summaries the auto-timeline feature already keeps
// (timelines\auto\cache, see ReplaySummaryCache). A summary contributes when its bosses ended at zero HP (a kill; wipes and abandoned pulls
// do not); its track is, once a second, the biggest boss that existed at that moment and its HP fraction, the same signal the live
// estimator aligns on. Nothing calls this yet: the plugin does not load priors.
public static class FightPriorBuilder
{
    public static FightPrior FromSummaries(IEnumerable<PullSummary> pulls)
    {
        var prior = new FightPrior();
        foreach (var pull in pulls)
            if (KillOf(pull) is { } kill)
                prior.Add(kill);
        return prior;
    }

    public static FightPrior.Kill? KillOf(PullSummary pull)
    {
        var bosses = pull.Bosses.Where(b => b.MaxHP > 0 && b.History.Count > 0).ToList();
        if (bosses.Count == 0)
            return null;
        // kill time: when the last boss went to zero (start of its final zero-HP run); every boss must have ended at zero
        long killTicks = 0;
        foreach (var b in bosses)
        {
            if (b.History[^1].CurHP != 0)
                return null;
            var i = b.History.Count - 1;
            while (i > 0 && b.History[i - 1].CurHP == 0)
                --i;
            killTicks = Math.Max(killTicks, b.History[i].Ticks);
        }
        var killSeconds = (float)TimeSpan.FromTicks(killTicks - pull.Start.Ticks).TotalSeconds;
        if (killSeconds < 1)
            return null;

        var len = (int)MathF.Floor(killSeconds) + 1;
        var oid = new uint[len];
        var frac = new float[len];
        uint lastOid = 0;
        var lastFrac = 1f;
        for (var s = 0; s < len; ++s)
        {
            var t = pull.Start.AddSeconds(s).Ticks;
            PullSummary.BossHP? best = null;
            foreach (var b in bosses)
                if (b.Existence.Any(r => t >= r.Start && t <= r.End) && (best == null || b.MaxHP > best.MaxHP || b.MaxHP == best.MaxHP && b.OID < best.OID)) // ties by OID, as the estimator picks its biggest target
                    best = b;
            if (best != null)
            {
                PullSummary.HPPoint? point = null;
                foreach (var p in best.History)
                {
                    if (p.Ticks > t)
                        break;
                    point = p;
                }
                if (point is { MaxHP: > 0, CurHP: > 0 })
                {
                    lastOid = best.OID;
                    lastFrac = (float)point.CurHP / point.MaxHP;
                }
            }
            oid[s] = lastOid;
            frac[s] = lastFrac;
        }
        return new FightPrior.Kill(killSeconds, oid, frac);
    }
}
