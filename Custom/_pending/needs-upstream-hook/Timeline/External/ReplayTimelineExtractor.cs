using System.Collections.Frozen;
using System.Runtime.InteropServices;

namespace BossMod;

// Builds imported-timeline data from replays recorded on this client. A pull is the unit: the encounter when a module ran,
// otherwise the span from the first boss cast or ability effect until the boss-sized enemies were gone. Sync points come from the boss casts and ability effects,
// windows from what was actually targetable, and several pulls of the same fight are merged by median.
public static class ReplayTimelineExtractor
{
    public sealed record Pull(ushort Zone, DateTime Start, DateTime End, IReadOnlyList<uint> BossOIDs, IReadOnlyList<Replay.Participant> Enemies)
    {
        // Ability effects cast by the enemies of this pull, inside its time span; Build turns the boss ones into AbilityUsed sync points.
        public IReadOnlyList<Replay.Action> Actions { get; init; } = [];

        public float Seconds(DateTime t) => (float)(t - Start).TotalSeconds;
    }
    public sealed record Window(float Start, float End);

    // Auto-attacks shared by every enemy: they repeat every few seconds and align with anything, so neither the extractor nor the
    // follower treats them as sync points.
    public static readonly FrozenSet<uint> GenericActionIDs = FrozenSet.ToFrozenSet<uint>([7, 8, 870, 871, 872, 873]);

    // An id that makes up more than this share of a pull's boss casts and effects is a boss-specific repeater (its own auto-attack
    // or a filler every few seconds): it would match the clock at every period, so it is not a sync point either.
    public const float DominantSyncShare = 0.25f;

    public const float MinPullSeconds = 20f;
    private const float PullGapSeconds = 5f; // enemies whose existence overlaps or nearly touches belong to one pull
    private const float BossHPShare = 0.5f; // an enemy with at least this share of the biggest max HP in the pull is a boss

    public static IReadOnlyList<Pull> FindPulls(Replay replay)
    {
        // An enemy that was hostile at some point takes part; a momentary ally flag (a boss recorded as friendly for a tick at spawn)
        // is handled per probe inside the window builders, not by dropping the whole participant.
        var enemies = replay.Participants.Where(p => p.Type == ActorType.Enemy && (p.AllyHistory.Count == 0 || p.AllyHistory.Any(kv => !kv.Value)) && p.WorldExistence.Count > 0 && (p.HasAnyActions || p.IsTargetOfAnyActions)).ToList();
        List<Pull> result = [];
        if (replay.Encounters.Count > 0)
        {
            foreach (var enc in replay.Encounters.Where(e => e.Time.End > e.Time.Start))
            {
                var end = EncounterEnd(replay, enc);
                if (end != enc.Time.End && (end - enc.Time.Start).TotalSeconds < MinPullSeconds)
                    continue;
                var inside = enemies.Where(p => p.WorldExistence.Any(r => r.Start < end && r.End > enc.Time.Start)).ToList();
                result.Add(new(enc.Zone, enc.Time.Start, end, [enc.OID], inside) { Actions = ActionsOf(replay, inside, enc.Time.Start, end) });
            }
            return result;
        }

        // No module ran: cluster enemy lifetimes into pulls.
        var spans = enemies.SelectMany(p => p.WorldExistence.Select(r => (Start: r.Start, End: EndOfLife(p, r), Enemy: p))).OrderBy(s => s.Start).ToList();
        var index = 0;
        while (index < spans.Count)
        {
            var start = spans[index].Start;
            var end = spans[index].End;
            List<Replay.Participant> members = [spans[index].Enemy];
            ++index;
            while (index < spans.Count && (spans[index].Start - end).TotalSeconds <= PullGapSeconds)
            {
                if (spans[index].End > end)
                    end = spans[index].End;
                if (!members.Contains(spans[index].Enemy))
                    members.Add(spans[index].Enemy);
                ++index;
            }
            var maxHP = members.Max(p => MaxHP(p));
            if (maxHP == 0)
                continue;
            var bosses = members.Where(p => MaxHP(p) >= maxHP * BossHPShare).Select(p => p.OID).Distinct().Order().ToList();
            var actions = ActionsOf(replay, members, start, end);

            // Spawn time drifts with the walk-up, so the pull is anchored at the first thing a boss did; a cluster where no boss
            // ever cast or hit anything is not a fight.
            var firstBossEvent = members.Where(p => bosses.Contains(p.OID))
                .SelectMany(p => p.Casts.Where(c => c.Time.Start >= start && c.Time.Start <= end).Select(c => c.Time.Start))
                .Concat(actions.Where(a => bosses.Contains(a.Source.OID)).Select(a => a.Timestamp))
                .DefaultIfEmpty(DateTime.MaxValue).Min();
            if (firstBossEvent == DateTime.MaxValue)
                continue;
            start = firstBossEvent;
            if ((end - start).TotalSeconds < MinPullSeconds)
                continue;
            var zone = (ushort)(members[0].ZoneID is > 0 and <= ushort.MaxValue ? members[0].ZoneID : 0);
            result.Add(new(zone, start, end, bosses, members) { Actions = actions.Where(a => a.Timestamp >= start).ToList() });
        }
        return result;
    }

    // Only one module per boss OID is loaded at a time, so a later record of the same boss starting inside this one means the recorder
    // never saw this one end. Its module had been moved to pending (the dead player was reported far away), and the boss manager drops
    // a pending module once its primary is dead or destroyed without firing an unload, so the replay closed the record at its own end,
    // across the next attempts. The primary leaving the world stands in for that missed unload; the record ends there, and at the
    // latest when the next record of the same boss began.
    private static DateTime EncounterEnd(Replay replay, Replay.Encounter enc)
    {
        var next = replay.Encounters.Where(o => o.OID == enc.OID && o.Time.Start > enc.Time.Start && o.Time.Start < enc.Time.End).Select(o => o.Time.Start).DefaultIfEmpty(DateTime.MaxValue).Min();
        if (next == DateTime.MaxValue)
            return enc.Time.End;
        var left = replay.Participants.Where(p => p.InstanceID == enc.InstanceID && p.OID == enc.OID).SelectMany(p => p.WorldExistence)
            .Where(r => r.Start < next).Select(r => r.End).DefaultIfEmpty(next).Max();
        return left < next ? left : next;
    }

    private static List<Replay.Action> ActionsOf(Replay replay, List<Replay.Participant> enemies, DateTime start, DateTime end)
    {
        var sources = new HashSet<Replay.Participant>(enemies);
        return replay.Actions.Where(a => a.Timestamp >= start && a.Timestamp <= end && sources.Contains(a.Source)).ToList();
    }

    private static DateTime EndOfLife(Replay.Participant p, Replay.TimeRange existence)
    {
        var death = p.DeadHistory.FirstOrDefault(kv => kv.Value && kv.Key >= existence.Start && kv.Key <= existence.End);
        return death.Key != default ? death.Key : existence.End;
    }

    internal static uint MaxHP(Replay.Participant p) => p.HPMPHistory.Count == 0 ? 0 : p.HPMPHistory.Values.Max(hp => hp.MaxHP);

    // Nothing attackable: every enemy is gone, dead, allied or untargetable.
    public static IReadOnlyList<Window> NoTargetWindows(Pull pull)
    {
        var edges = new SortedSet<DateTime> { pull.Start, pull.End };
        foreach (var p in pull.Enemies)
        {
            foreach (var r in p.WorldExistence) { edges.Add(r.Start); edges.Add(r.End); }
            foreach (var t in p.TargetableHistory.Keys) edges.Add(t);
            foreach (var t in p.DeadHistory.Keys) edges.Add(t);
            foreach (var t in p.AllyHistory.Keys) edges.Add(t);
        }
        List<Window> result = [];
        float? openAt = null;
        foreach (var edge in edges.Where(e => e >= pull.Start && e <= pull.End))
        {
            var probe = edge.AddTicks(1);
            var attackable = edge < pull.End && pull.Enemies.Any(p => p.ExistsInWorldAt(probe) && p.TargetableAt(probe) && !p.DeadAt(probe) && !p.AllyAt(probe));
            if (!attackable && openAt == null)
                openAt = pull.Seconds(edge);
            else if (attackable && openAt is { } start)
            {
                result.Add(new(start, pull.Seconds(edge)));
                openAt = null;
            }
        }
        return result;
    }

    public static IReadOnlyList<Window> BossUntargetableWindows(Pull pull)
    {
        List<Window> result = [];
        foreach (var boss in pull.Enemies.Where(p => pull.BossOIDs.Contains(p.OID)))
        {
            float? openAt = null;
            foreach (var (t, targetable) in boss.TargetableHistory)
            {
                if (t < pull.Start || t > pull.End)
                    continue;
                if (!targetable && openAt == null)
                    openAt = pull.Seconds(t);
                else if (targetable && openAt is { } start)
                {
                    result.Add(new(start, pull.Seconds(t)));
                    openAt = null;
                }
            }
        }
        return result.OrderBy(w => w.Start).ToList();
    }

    public static float Confidence(IReadOnlyList<float> samples)
    {
        if (samples.Count <= 1)
            return 0.6f;
        var sorted = samples.Order().ToList();
        var q1 = sorted[(int)Math.Floor((sorted.Count - 1) * 0.25)];
        var q3 = sorted[(int)Math.Ceiling((sorted.Count - 1) * 0.75)];
        return Math.Clamp(1f - (q3 - q1) / 20f, 0.2f, 1f);
    }

    private static float Median(IReadOnlyList<float> samples)
    {
        var sorted = samples.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
    }

    // Raw pulls are summarized first; the sync points of every pull come from the first pull's bosses.
    public static ExternalPlannerTimeline.TimelineDefinition? Build(ushort zone, IReadOnlyList<Pull> pulls)
        => pulls.Count == 0 ? null : Build(zone, pulls.Select(p => PullSummary.Summarize(p, pulls[0].BossOIDs)).ToList());

    public static ExternalPlannerTimeline.TimelineDefinition? Build(ushort zone, IReadOnlyList<PullSummary> pulls)
    {
        if (pulls.Count == 0)
            return null;
        var bossOIDs = pulls[0].BossOIDs.ToList();
        var durations = pulls.Select(p => p.Duration).ToList();

        // Sync points: the k-th occurrence of an action id in a pull is matched to the k-th occurrence in every other pull.
        Dictionary<(uint ID, int Ordinal, ExternalPlannerTimeline.ExternalStateKind Kind), List<float>> samples = [];
        foreach (var pull in pulls)
        {
            Dictionary<(uint, ExternalPlannerTimeline.ExternalStateKind), int> seen = [];
            var events = pull.Events;
            // A repeater must actually repeat: with a handful of events one occurrence already exceeds the share.
            var dominant = events.GroupBy(e => e.ID).Where(g => g.Count() > 1 && g.Count() > events.Count * DominantSyncShare).Select(g => g.Key).ToHashSet();
            foreach (var e in events.Where(e => !dominant.Contains(e.ID)).OrderBy(e => e.Time))
            {
                var ordinal = seen.GetValueOrDefault((e.ID, e.Kind));
                seen[(e.ID, e.Kind)] = ordinal + 1;
                ref var times = ref CollectionsMarshal.GetValueRefOrAddDefault(samples, (e.ID, ordinal, e.Kind), out _);
                (times ??= []).Add(e.Time);
            }
        }
        List<ExternalPlannerTimeline.TimelineState> states = [];
        foreach (var (key, times) in samples)
        {
            var time = Median(times);
            if (times.Count >= RequiredSamples(durations, time))
                states.Add(new(time, "", key.Kind, [key.ID], 0));
        }
        states.Sort((a, b) => a.Time.CompareTo(b.Time));

        List<ExternalPlannerTimeline.TimelineWindow> windows = [];
        windows.AddRange(MergeWindows(pulls.Select(p => p.NoTarget).ToList(), durations, ExternalPlannerTimeline.TimelineWindowKind.NoTarget));
        var bossWindows = MergeWindows(pulls.Select(p => p.BossUntargetable).ToList(), durations, ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable).ToList();
        windows.AddRange(bossWindows);
        // While the boss is away, whatever is not a NoTarget stretch is time spent on adds: subtract the NoTarget windows from each
        // boss window and keep every remaining piece.
        var noTarget = windows.Where(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget).OrderBy(w => w.Start).ToList();
        foreach (var boss in bossWindows)
        {
            var bossEnd = boss.End!.Value;
            var cursor = boss.Start;
            foreach (var gap in noTarget.Where(w => w.End!.Value > boss.Start && w.Start < bossEnd))
            {
                if (gap.Start > cursor)
                    windows.Add(new(ExternalPlannerTimeline.TimelineWindowKind.AddsPresent, cursor, gap.Start, boss.Confidence));
                cursor = Math.Max(cursor, gap.End!.Value);
            }
            if (cursor < bossEnd)
                windows.Add(new(ExternalPlannerTimeline.TimelineWindowKind.AddsPresent, cursor, bossEnd, boss.Confidence));
        }
        windows.Sort((a, b) => a.Start.CompareTo(b.Start));

        var sequence = new ExternalPlannerTimeline.TimelineSequence(0, 0, states, null, bossOIDs, windows);
        var confidence = windows.Count == 0 ? (states.Count == 0 ? 0.2f : 0.6f) : windows.Average(w => w.Confidence);
        return new(zone, $"replay:{string.Join("+", bossOIDs.Select(o => o.ToString("X")))}", true, [sequence], ExternalPlannerTimeline.TimelineSource.Replay, confidence);
    }

    // What a pull's bosses did that can be a sync point: their cast starts and ability effects inside the pull, auto-attacks left out.
    internal static List<(DateTime Time, uint ID, ExternalPlannerTimeline.ExternalStateKind Kind)> BossEvents(Pull pull, List<uint> bossOIDs)
    {
        var bosses = pull.Enemies.Where(p => bossOIDs.Contains(p.OID)).ToList();
        var casts = bosses.SelectMany(p => p.Casts.Where(c => c.Time.Start >= pull.Start && c.Time.Start <= pull.End && !GenericActionIDs.Contains(c.ID.ID))
            .Select(c => (Time: c.Time.Start, ID: c.ID.ID, Kind: ExternalPlannerTimeline.ExternalStateKind.CastStart)));
        var abilities = pull.Actions.Where(a => bossOIDs.Contains(a.Source.OID) && !GenericActionIDs.Contains(a.ID.ID))
            .Select(a => (Time: a.Timestamp, ID: a.ID.ID, Kind: ExternalPlannerTimeline.ExternalStateKind.AbilityUsed));
        return casts.Concat(abilities).ToList();
    }

    // A pull only votes on a moment it lasted to. Wipes and fast kills end pulls early, and in a fight that loops most pulls may
    // never reach the second loop: counted against it, they dropped every second-loop sync point and window, and the follower then
    // aligned the second loop's casts to the first loop's (a backward jump) and predicted the first loop's windows again, after the
    // boss was already dead. So a sync point or window needs a majority of the pulls that lasted to its median time (durations are
    // the pulls' lengths in the same seconds), at least 2 samples, and never more than a majority of all pulls, as before: nothing
    // that majority kept is dropped, and one or two pulls behave as they always did.
    private static int RequiredSamples(IReadOnlyList<float> durations, float medianTime)
    {
        var eligible = durations.Count(d => d >= medianTime);
        return Math.Min((durations.Count + 1) / 2, Math.Max(2, (eligible + 1) / 2));
    }

    // Windows are matched by index: the i-th window of one pull is the i-th of another. Wipes cut pulls short, so only windows
    // seen in a majority of the pulls that lasted to them survive (RequiredSamples).
    private static IEnumerable<ExternalPlannerTimeline.TimelineWindow> MergeWindows(IReadOnlyList<IReadOnlyList<Window>> perPull, IReadOnlyList<float> durations, ExternalPlannerTimeline.TimelineWindowKind kind)
    {
        var maxCount = perPull.Max(w => w.Count);
        for (var i = 0; i < maxCount; ++i)
        {
            var starts = perPull.Where(w => w.Count > i).Select(w => w[i].Start).ToList();
            var ends = perPull.Where(w => w.Count > i).Select(w => w[i].End).ToList();
            var start = Median(starts);
            if (starts.Count < RequiredSamples(durations, start))
                continue;
            yield return new(kind, start, Median(ends), Math.Min(Confidence(starts), Confidence(ends)));
        }
    }

    // Only NoTarget windows of at least this length can mark a branch: shorter gaps are dodges or blips, the same bar the follower
    // uses before publishing a loss.
    public const float BranchMinWindowSeconds = ExternalTimelineHints.MinPublishedLoss;
    public const float BranchGapSeconds = 5f; // window starts further apart than this belong to different branches
    // The gap between the two clusters must be at least this many times the spread of the wider one: window starts that merely
    // scatter with the party's damage leave gaps comparable to their own spread, a real branch leaves one far wider.
    public const float BranchSeparationRatio = 3f;
    public const int MinBranchPulls = 2; // each branch needs this many pulls, so one odd pull does not make a branch
    public const int MinBranchGroupPulls = 4; // a boss set with fewer pulls is never split
    public const float BranchMaxLeadSeconds = 30f; // an entry further ahead of the early window than this is not what announces it
    // The follower takes the divergence cast within its sync tolerance of DecisionTime, so the late sibling must not show that id
    // up to this far past the early window start.
    public const float BranchCastExclusionSeconds = ExternalTimelineHints.SyncTolerance;
    // A divergence entry must be something (nearly) every early pull did: the id within this many seconds of the entry's time in
    // at least BranchPresencePercent of the early pulls.
    public const float BranchPresenceSeconds = 2f;
    public const int BranchPresencePercent = 80;

    private static List<Window> LongWindows(IReadOnlyList<Window> windows) => windows.Where(w => w.End - w.Start >= BranchMinWindowSeconds).ToList();

    // HP-gated branches: some fights go away at one of two times depending on the boss HP at a decision point. Matching windows by
    // index would merge the two times into one vague window, so the long NoTarget windows are compared across pulls in order, and
    // the first index where their starts fall into exactly two clusters (more than BranchGapSeconds apart), each with at least
    // MinBranchPulls pulls, is the branch point. WindowIndex counts long windows only; Early and Late are the pull indices of each
    // cluster (pulls that ended before that window are in neither). An index where one cluster is a single pull is not a branch
    // point. Nothing is returned when the pulls are not a clean two-way branch: three or more clusters, two clusters that together
    // hold no majority of the pulls, two clusters whose gap is not BranchSeparationRatio times the wider one's spread, or one side
    // that also went away when the other did.
    public static (int WindowIndex, int[] Early, int[] Late)? DetectBranch(IReadOnlyList<IReadOnlyList<Window>> perPullWindows)
    {
        if (perPullWindows.Count < MinBranchGroupPulls)
            return null;
        var longWindows = perPullWindows.Select(LongWindows).ToList();
        var maxCount = longWindows.Max(w => w.Count);
        for (var i = 0; i < maxCount; ++i)
        {
            var starts = longWindows.Select((w, pull) => (Pull: pull, Start: w.Count > i ? w[i].Start : float.NaN)).Where(s => !float.IsNaN(s.Start)).OrderBy(s => s.Start).ToList();
            List<List<(int Pull, float Start)>> clusters = [[starts[0]]];
            for (var k = 1; k < starts.Count; ++k)
            {
                if (starts[k].Start - starts[k - 1].Start > BranchGapSeconds)
                    clusters.Add([]);
                clusters[^1].Add(starts[k]);
            }
            if (clusters.Count > 2)
                return null;
            if (clusters.Count == 2 && clusters.All(c => c.Count >= MinBranchPulls))
            {
                // Most pulls wiped before this window: the split describes too little of the fight to be trusted.
                if ((clusters[0].Count + clusters[1].Count) * 2 <= perPullWindows.Count)
                    return null;
                // Starts spread by damage rather than split by a decision: the gap is no wider than the scatter on either side.
                var gap = clusters[1][0].Start - clusters[0][^1].Start;
                var spread = Math.Max(clusters[0][^1].Start - clusters[0][0].Start, clusters[1][^1].Start - clusters[1][0].Start);
                if (gap < BranchSeparationRatio * spread)
                    return null;
                // A branch means each side was still attackable when the other went away. When most pulls of one side have a window
                // of their own starting where the other side's does, the index is misaligned (a window shorter than the bar was
                // skipped, or an extra window only some pulls have came first) and the pulls do not disagree at all: not a branch.
                var early = clusters[0].Select(s => s.Pull).ToList();
                var late = clusters[1].Select(s => s.Pull).ToList();
                var earlyStart = Median(clusters[0].Select(s => s.Start).ToList());
                var lateStart = Median(clusters[1].Select(s => s.Start).ToList());
                if (MostGoAwayAt(perPullWindows, late, earlyStart) || MostGoAwayAt(perPullWindows, early, lateStart))
                    return null;
                return (i, early.Order().ToArray(), late.Order().ToArray());
            }
        }
        return null;
    }

    private static bool MostGoAwayAt(IReadOnlyList<IReadOnlyList<Window>> perPullWindows, List<int> pulls, float start)
        => pulls.Count(pull => perPullWindows[pull].Any(w => Math.Abs(w.Start - start) <= BranchGapSeconds)) * 2 > pulls.Count;

    // The first thing that shows which way the fight went: the early sibling's first entry within maxLead before its window whose id
    // the late sibling does not show at any time up to `margin` past that window start, and that `accept` (when given) takes. An id
    // the late sibling has before that bound, even at a different time, decides nothing, since a late pull would show it while the
    // branch is still open. Null when there is no such entry; the decision then falls on the early window start, and never after it
    // (a window starting before DecisionTime counts as shared).
    public static float? DivergenceTime(ExternalPlannerTimeline.TimelineSequence early, ExternalPlannerTimeline.TimelineSequence late, float earlyWindowStart, float margin, float maxLead,
        Func<ExternalPlannerTimeline.TimelineState, bool>? accept = null)
    {
        foreach (var state in early.States.OrderBy(s => s.Time))
        {
            if (state.Time > earlyWindowStart)
                break;
            if (state.Time >= earlyWindowStart - maxLead && !late.States.Any(other => other.Time <= earlyWindowStart + margin && other.IDs.Any(state.IDs.Contains))
                && (accept == null || accept(state)))
                return state.Time;
        }
        return null;
    }

    // The raw-pull test of a divergence candidate, which the merged siblings cannot make (each keeps only what most of its own pulls
    // did, so an id a few late pulls show vanishes from the late sibling): the candidate's id must occur within
    // BranchPresenceSeconds of its time in at least BranchPresencePercent of the early pulls, and in no late pull at any time up to
    // exclusionEnd (the early window start plus the follower's sync tolerance). Each pull is its boss events as (pull seconds, id).
    public static bool AcceptDivergence(ExternalPlannerTimeline.TimelineState candidate, IReadOnlyList<IReadOnlyList<(float Time, uint ID)>> earlyPulls,
        IReadOnlyList<IReadOnlyList<(float Time, uint ID)>> latePulls, float exclusionEnd)
    {
        if (earlyPulls.Count == 0)
            return false;
        var present = earlyPulls.Count(events => events.Any(e => Math.Abs(e.Time - candidate.Time) <= BranchPresenceSeconds && candidate.IDs.Contains(e.ID)));
        if (present * 100 < earlyPulls.Count * BranchPresencePercent)
            return false;
        return !latePulls.Any(events => events.Any(e => e.Time <= exclusionEnd && candidate.IDs.Contains(e.ID)));
    }

    // The boss HP% at the decision point separates the branches when every early pull is under every late pull: the threshold is
    // the midpoint of that gap. Otherwise, or with fewer than MinBranchPulls samples on a side, HP does not tell them apart.
    public static float? LearnThreshold(IReadOnlyList<float> hpEarly, IReadOnlyList<float> hpLate)
    {
        if (hpEarly.Count < MinBranchPulls || hpLate.Count < MinBranchPulls)
            return null;
        var earlyMax = hpEarly.Max();
        var lateMin = hpLate.Min();
        return earlyMax < lateMin ? (earlyMax + lateMin) / 2f : null;
    }

    // HP% of the primary boss `seconds` into the pull: of the listed bosses that exist at that time, the one with the largest max HP.
    // Null when none exists then or its HP is not recorded; an actor already gone would only offer its last, stale HP.
    public static float? PrimaryBossHPPercent(Pull pull, float seconds)
    {
        var t = pull.Start.AddSeconds(seconds);
        var boss = pull.Enemies.Where(p => pull.BossOIDs.Contains(p.OID) && p.ExistsInWorldAt(t) && MaxHP(p) > 0).OrderByDescending(MaxHP).FirstOrDefault();
        if (boss == null)
            return null;
        var hp = boss.HPMPAt(t);
        return hp.MaxHP > 0 ? 100f * hp.CurHP / hp.MaxHP : null;
    }

    // One boss set: a single sequence, or the early and late siblings of an HP-gated branch. Branch.Group is filled in by Assemble,
    // once the sequence numbers are known.
    public static ExternalPlannerTimeline.TimelineDefinition BuildBossSet(ushort zone, IReadOnlyList<PullSummary> pulls)
    {
        var perPull = pulls.Select(p => p.NoTarget).ToList();
        if (DetectBranch(perPull) is not { } branch)
            return Build(zone, pulls)!;

        var early = Build(zone, branch.Early.Select(i => pulls[i]).ToList())!;
        var late = Build(zone, branch.Late.Select(i => pulls[i]).ToList())!;
        var earlySequence = early.Sequences[0];
        var lateSequence = late.Sequences[0];
        var earlyWindowStart = Median(branch.Early.Select(i => LongWindows(perPull[i])[branch.WindowIndex].Start).ToList());
        List<IReadOnlyList<(float Time, uint ID)>> Events(int[] indices)
            => indices.Select(i => (IReadOnlyList<(float, uint)>)pulls[i].Events.Select(e => (e.Time, e.ID)).ToList()).ToList();
        var earlyEvents = Events(branch.Early);
        var lateEvents = Events(branch.Late);
        var exclusionEnd = earlyWindowStart + BranchCastExclusionSeconds;
        var decision = DivergenceTime(earlySequence, lateSequence, earlyWindowStart, BranchCastExclusionSeconds, BranchMaxLeadSeconds,
            state => AcceptDivergence(state, earlyEvents, lateEvents, exclusionEnd)) ?? earlyWindowStart;
        List<float> HPs(int[] indices) => indices.Select(i => PullSummary.PrimaryBossHPPercent(pulls[i], decision)).OfType<float>().ToList();
        var threshold = LearnThreshold(HPs(branch.Early), HPs(branch.Late));
        return new(zone, early.SourceFile, true,
            [earlySequence with { Branch = new(0, decision, threshold, true) }, lateSequence with { Branch = new(0, decision, threshold, false) }],
            ExternalPlannerTimeline.TimelineSource.Replay, (early.Confidence + late.Confidence) / 2f);
    }

    // Each replay is summarized pull by pull as it comes, so a caller that does not keep the replays can drop each one once read.
    public static IReadOnlyDictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> Extract(IEnumerable<Replay> replays)
        => Extract(replays.SelectMany(r => FindPulls(r).Select(p => PullSummary.Summarize(p))));

    public static IReadOnlyDictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> Extract(IEnumerable<PullSummary> pulls)
    {
        Dictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> result = [];
        foreach (var group in pulls.Where(p => p.Zone != 0).GroupBy(p => p.Zone))
            result[group.Key] = Assemble(group.Key, BossSets(group).Select(set => BuildBossSet(group.Key, set)).ToList(), ExternalPlannerTimeline.TimelineSource.Replay);
        return result;
    }

    // Dungeons have several bosses per zone: each boss set becomes its own sequence, in the order the bosses were first met.
    public static IReadOnlyList<IReadOnlyList<PullSummary>> BossSets(IEnumerable<PullSummary> zonePulls)
        => zonePulls.GroupBy(p => string.Join(",", p.BossOIDs)).OrderBy(g => g.Min(p => p.Start)).Select(g => (IReadOnlyList<PullSummary>)g.ToList()).ToList();

    // A branching boss set contributes its two siblings next to each other; they share the early sibling's number as Group.
    public static ExternalPlannerTimeline.TimelineDefinition Assemble(ushort zone, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> sets, ExternalPlannerTimeline.TimelineSource source)
    {
        List<ExternalPlannerTimeline.TimelineSequence> sequences = [];
        foreach (var set in sets)
        {
            var first = sequences.Count;
            foreach (var sequence in set.Sequences)
                sequences.Add(sequence with { Index = sequences.Count, Branch = sequence.Branch is { } b ? b with { Group = first } : null });
        }
        return new(zone, string.Join(";", sets.Select(s => s.SourceFile)), true, sequences, source, sets.Average(s => s.Confidence));
    }

    // One line per branch group of the timeline, for the extraction tools: the group, the bosses, the decision point, the threshold
    // ("-" when HP does not separate the pulls) and where each sibling goes away (its first long NoTarget window from the decision
    // point on, "-" when it has none).
    public static IEnumerable<string> DescribeBranches(ExternalPlannerTimeline.TimelineDefinition timeline)
    {
        static string Start(ExternalPlannerTimeline.TimelineSequence? sibling, float decision)
            => sibling != null && ExternalTimelineHints.EarlyWindowStart(sibling, decision) is var start && start != float.MaxValue ? $"{start:f2}" : "-";
        foreach (var early in timeline.Sequences.Where(s => s.Branch is { Below: true }))
        {
            var branch = early.Branch!;
            var late = timeline.Sequences.FirstOrDefault(s => s.Branch is { Below: false } b && b.Group == branch.Group);
            var threshold = branch.HpThreshold is { } t ? $"{t:f2}" : "-";
            yield return $"branch group={branch.Group} boss={string.Join(",", (early.BossOIDs ?? []).Select(o => $"0x{o:X}"))} decision={branch.DecisionTime:f2} threshold={threshold} early={Start(early, branch.DecisionTime)} late={Start(late, branch.DecisionTime)}";
        }
    }
}
