using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace BossMod;

// The quality gate of automatic timelines: a window is published only if it would not be a false alarm in the pulls it was learned
// from. Each pull of a boss set is held out in turn, the set is rebuilt from the others, and what that rebuild would publish is
// compared with the held-out pull's real NoTarget windows, the way the hint harness scores a published loss: the prediction is
// confirmed when it lands in a real downtime (a hit) or a real downtime of at least MinConfirmSeconds starts shortly after it (the
// rotation still stopped for something, so the call was no false alarm). A window is kept when enough held-out pulls could show it,
// most of them confirm it and at least one hits it. Windows whose predictions most held-out pulls do not confirm (a deep dungeon's
// random trash, a fight that varies) or that never land on a real downtime are dropped; sync points and windows too short to
// publish are kept, since they only steer the clock.
// Publish then decides whether the zone gets an automatic file at all, and records every decision in auto\report.txt. Numbers in
// the report are culture-invariant: the file is parsed back on the next rebuild and read by people on any locale.
public static class AutoTimelineGate
{
    public const int MinPulls = 3; // fewer pulls leave nothing to hold out against: the set keeps its sync points only
    public const int MinEvaluated = 2; // a window needs this many held-out pulls that could show it
    public const int MinHits = 1; // and this many of them where it lands in a real downtime: short gaps alone never validate a window
    public const float HitSlackBefore = 5f; // same slack as the hint harness: a prediction this early still counts
    public const float FalseAlarmAfter = 30f; // the hint harness's false-alarm horizon: a real downtime starting this soon after a prediction confirms it
    // A real NoTarget gap confirms a prediction only if it lasts this long: the follower's ContradictionGrace (private in
    // ExternalTimelineHints), how long the fight may disagree with the timeline before the clock is dropped. A shorter blip (a
    // dodge, a target swap) is not the downtime the prediction announced.
    public const float MinConfirmSeconds = 3f;
    public const float MatchTolerance = 10f; // a held-out rebuild's window this close to the full one is the same window
    // The plugin builds each boss set from at most this many of its most recent pulls (Evaluate's maxPullsPerSet). No cap: on the
    // 97 Clyteum replays, keeping the 20, 30 or 50 most recent pulls per set left the same early predictions and raised the hint
    // harness's false alarms from 59 to 65, and changed nothing for the alliance raids (2026-09-29 results document).
    public const int DefaultMaxPullsPerSet = int.MaxValue;

    // KeptPublishable counts the publishable windows the gate kept in the zone the way CountPublishable counts them: a window both
    // siblings of a branch carry (the part before the decision point) counts once.
    public sealed record Result(ExternalPlannerTimeline.TimelineDefinition? Timeline, int KeptPublishable, IReadOnlyList<string> Report);

    public static bool Publishable(ExternalPlannerTimeline.TimelineWindow w)
        => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget && w.End is { } end && end - w.Start >= ExternalTimelineHints.MinPublishedLoss
            && w.Confidence >= ExternalTimelineHints.MinWindowConfidence;

    // maxPullsPerSet: each boss set is built and held out from its most recent pulls only (by start time), so a fight that changed
    // or a long history of old pulls does not outvote how it goes now. cancel is checked between the holdout rebuilds.
    public static Result Evaluate(ushort zone, IReadOnlyList<PullSummary> zonePulls, int maxPullsPerSet = int.MaxValue, CancellationToken cancel = default)
    {
        var pulls = zonePulls.Where(p => p.Zone == zone).ToList();
        if (pulls.Count == 0)
            return new(null, 0, [FormattableString.Invariant($"zone={zone} no pulls")]);
        List<string> report = [FormattableString.Invariant($"zone={zone} pulls={pulls.Count}")];
        List<ExternalPlannerTimeline.TimelineDefinition> sets = [];
        var kept = 0;
        foreach (var all in ReplayTimelineExtractor.BossSets(pulls))
        {
            var set = MostRecent(all, maxPullsPerSet);
            var full = ReplayTimelineExtractor.BuildBossSet(zone, set);
            var name = string.Join("+", set[0].BossOIDs.Select(o => o.ToString("X", CultureInfo.InvariantCulture)));
            var count = set.Count == all.Count ? FormattableString.Invariant($"pulls={set.Count}") : FormattableString.Invariant($"pulls={set.Count} (most recent of {all.Count})");
            if (set.Count < MinPulls)
            {
                report.Add(FormattableString.Invariant($"  set={name} {count}: fewer than {MinPulls} pulls, windows withheld"));
                sets.Add(full with { Sequences = full.Sequences.Select(s => s with { Windows = null }).ToList() });
                continue;
            }
            // A branched set lists its siblings one after the other, each window line naming the sibling it belongs to.
            if (full.Sequences.FirstOrDefault(s => s.Branch != null)?.Branch is { } branch)
            {
                var threshold = branch.HpThreshold is { } t ? t.ToString("f1", CultureInfo.InvariantCulture) : "-";
                report.Add(FormattableString.Invariant($"  set={name} {count} branched decision={branch.DecisionTime:f1} threshold={threshold}"));
            }
            else
            {
                report.Add(FormattableString.Invariant($"  set={name} {count}"));
            }
            List<(PullSummary Pull, ExternalPlannerTimeline.TimelineDefinition Rebuilt)> holdouts = [];
            for (var i = 0; i < set.Count; ++i)
            {
                cancel.ThrowIfCancellationRequested();
                holdouts.Add((set[i], ReplayTimelineExtractor.BuildBossSet(zone, set.Where((_, j) => j != i).ToList())));
            }
            List<ExternalPlannerTimeline.TimelineSequence> sequences = [];
            foreach (var sequence in full.Sequences)
            {
                var sibling = sequence.Branch is { } b ? (b.Below ? "early " : "late ") : "";
                List<ExternalPlannerTimeline.TimelineWindow> windows = [];
                foreach (var w in sequence.Windows ?? [])
                {
                    if (!Publishable(w))
                    {
                        windows.Add(w);
                        continue;
                    }
                    var (evaluated, confirmed, hits) = Score(w, sequence, holdouts);
                    var keep = evaluated >= MinEvaluated && confirmed * 3 >= evaluated * 2 && hits >= MinHits;
                    report.Add(FormattableString.Invariant($"    {sibling}window {w.Start:f1}-{w.End:f1} evaluated={evaluated} confirmed={confirmed} hits={hits} {(keep ? "kept" : "dropped")}"));
                    if (keep)
                    {
                        windows.Add(w);
                        if (CountsOnce(sequence, w))
                            ++kept;
                    }
                }
                sequences.Add(sequence with { Windows = windows });
            }
            sets.Add(full with { Sequences = sequences });
        }
        return new(ReplayTimelineExtractor.Assemble(zone, sets, ExternalPlannerTimeline.TimelineSource.AutoReplay), kept, report);
    }

    // The maxPullsPerSet most recent pulls of a boss set, in the set's own order.
    private static IReadOnlyList<PullSummary> MostRecent(IReadOnlyList<PullSummary> set, int maxPullsPerSet)
    {
        if (set.Count <= maxPullsPerSet)
            return set;
        var recent = set.Select((p, i) => (Pull: p, Index: i)).OrderByDescending(x => x.Pull.Start).Take(Math.Max(maxPullsPerSet, 1)).Select(x => x.Index).ToHashSet();
        return set.Where((_, i) => recent.Contains(i)).ToList();
    }

    // Both siblings of an HP-gated branch carry the windows before the decision point, each copy built from that sibling's own pulls
    // (so the two can differ a little): a window of a branched sequence counts in the early sibling, and in the late one only from
    // the decision point on.
    private static bool CountsOnce(ExternalPlannerTimeline.TimelineSequence sequence, ExternalPlannerTimeline.TimelineWindow w)
        => sequence.Branch is not { } branch || branch.Below || w.Start >= branch.DecisionTime;

    public static string AutoDirectory(string timelinesDir) => Path.Combine(timelinesDir, "auto");

    // The zone's best timeline below AutoReplay (FFLogs, Cactbot or EventTrigger) in the store: what an automatic file must not
    // cover less than (Decide).
    public static ExternalPlannerTimeline.TimelineDefinition? LowerRanked(ushort zone)
        => TimelineStore.CandidatesForZone(zone).FirstOrDefault(t => t.Source is ExternalPlannerTimeline.TimelineSource.FFLogs
            or ExternalPlannerTimeline.TimelineSource.Cactbot or ExternalPlannerTimeline.TimelineSource.EventTrigger);

    // Publishable windows of a timeline: measured windows through Publishable, legacy sequences through their untargetable markers.
    // The part before an HP-gated branch's decision point counts once (CountsOnce).
    public static int CountPublishable(ExternalPlannerTimeline.TimelineDefinition timeline)
    {
        var count = 0;
        foreach (var sequence in timeline.Sequences)
        {
            if (sequence.Windows is { Count: > 0 } windows)
            {
                foreach (var w in windows)
                    if (Publishable(w) && CountsOnce(sequence, w))
                        ++count;
                continue;
            }
            float? lossAt = null;
            foreach (var state in sequence.States.OrderBy(s => s.Time))
            {
                if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Untargetable)
                    lossAt ??= state.Time;
                else if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Targetable && lossAt is { } loss)
                {
                    if (state.Time - loss >= ExternalTimelineHints.MinPublishedLoss)
                        ++count;
                    lossAt = null;
                }
            }
        }
        return count;
    }

    // Spec 3.4: nothing validated, or a lower-ranked source that covers more of the zone, means no automatic file for it.
    public static (bool Write, string Reason) Decide(int keptPublishable, ExternalPlannerTimeline.TimelineDefinition? lowerRanked)
    {
        if (keptPublishable == 0)
            return (false, "no validated window");
        if (lowerRanked != null && CountPublishable(lowerRanked) is var lower && lower > keptPublishable)
            return (false, FormattableString.Invariant($"{lowerRanked.Source} timeline has {lower} windows, more than the {keptPublishable} validated"));
        return (true, FormattableString.Invariant($"{keptPublishable} validated window{(keptPublishable == 1 ? "" : "s")}"));
    }

    // Writes or removes <timelinesDir>\auto\<zone>-auto.json and replaces the zone's block in the report. Only files of the auto
    // folder are touched, never the hand-placed ones next to it. A failed write throws, and leaves no temp file behind.
    public static string Publish(string timelinesDir, ushort zone, Result result, ExternalPlannerTimeline.TimelineDefinition? lowerRanked)
    {
        var directory = AutoDirectory(timelinesDir);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FormattableString.Invariant($"{zone}-auto.json"));
        var (write, reason) = result.Timeline == null ? (false, "no pulls") : Decide(result.KeptPublishable, lowerRanked);
        if (write)
            ReplacingFile.WriteAllText(path, JsonSerializer.Serialize(new { Timelines = new[] { result.Timeline } }));
        else if (File.Exists(path))
            File.Delete(path);
        var line = FormattableString.Invariant($"zone={zone} {(write ? "written" : "not written")}: {reason}");
        UpdateReport(Path.Combine(directory, "report.txt"), zone, [line, .. result.Report.Skip(1)]);
        return line;
    }

    // report.txt holds one block per zone, each starting with "zone=<id> "; a rebuild replaces its zone's block and keeps the rest.
    private static void UpdateReport(string path, ushort zone, IReadOnlyList<string> block)
    {
        var blocks = new SortedDictionary<ushort, List<string>>();
        if (File.Exists(path))
        {
            List<string>? current = null;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("zone=", StringComparison.Ordinal)
                    && ushort.TryParse(line.AsSpan(5, line.IndexOf(' ') is var sp and > 5 ? sp - 5 : line.Length - 5), NumberStyles.None, CultureInfo.InvariantCulture, out var z))
                    blocks[z] = current = [];
                current?.Add(line);
            }
        }
        blocks[zone] = [.. block];
        ReplacingFile.WriteAllText(path, string.Concat(blocks.Values.SelectMany(b => b).Select(l => l + Environment.NewLine)));
    }

    // For each held-out pull that could show the window, the rebuild's prediction of it is scored against the pull's real NoTarget
    // windows. A hit is the harness's hit: the predicted start inside a real downtime long enough to publish (from HitSlackBefore
    // before it to its end). Confirmed is the harness's "no false alarm": a hit, or a real downtime of at least MinConfirmSeconds
    // starting from HitSlackBefore before the predicted start to FalseAlarmAfter after it. A window short enough to fall under the
    // publish bar in some pulls still stops the rotation in them, and predicting it there is no false alarm.
    private static (int Evaluated, int Confirmed, int Hits) Score(ExternalPlannerTimeline.TimelineWindow w, ExternalPlannerTimeline.TimelineSequence sequence,
        List<(PullSummary Pull, ExternalPlannerTimeline.TimelineDefinition Rebuilt)> holdouts)
    {
        int evaluated = 0, confirmed = 0, hits = 0;
        foreach (var (pull, rebuilt) in holdouts)
        {
            if (pull.Duration < w.Start)
                continue; // wiped before the window
            if (HoldoutSequence(rebuilt, pull, sequence, w.Start) is not { } predicted)
                continue;
            ExternalPlannerTimeline.TimelineWindow? match = null;
            foreach (var h in predicted.Windows ?? [])
                if (Publishable(h) && Math.Abs(h.Start - w.Start) <= MatchTolerance && (match == null || Math.Abs(h.Start - w.Start) < Math.Abs(match.Start - w.Start)))
                    match = h;
            if (match == null)
                continue; // the other pulls do not predict it for this one
            ++evaluated;
            var hit = pull.NoTarget.Any(a => a.End - a.Start >= ExternalTimelineHints.MinPublishedLoss && match.Start >= a.Start - HitSlackBefore && match.Start <= a.End);
            if (hit)
                ++hits;
            if (hit || pull.NoTarget.Any(a => a.End - a.Start >= MinConfirmSeconds && a.Start >= match.Start - HitSlackBefore && a.Start <= match.Start + FalseAlarmAfter))
                ++confirmed;
        }
        return (evaluated, confirmed, hits);
    }

    // Which sequence of the held-out rebuild predicts the window for this pull. Before an HP-gated branch point both siblings agree;
    // after it, the pull's primary boss HP at the decision point against the threshold picks the sibling, as the follower would, and a
    // pull that took the other sibling than the window's does not count. Without a threshold the part after the decision point is
    // not evaluated.
    private static ExternalPlannerTimeline.TimelineSequence? HoldoutSequence(ExternalPlannerTimeline.TimelineDefinition rebuilt, PullSummary pull,
        ExternalPlannerTimeline.TimelineSequence fullSequence, float windowStart)
    {
        var branch = fullSequence.Branch ?? rebuilt.Sequences.FirstOrDefault(s => s.Branch != null)?.Branch;
        if (branch == null || windowStart < branch.DecisionTime)
            return rebuilt.Sequences[0];
        if (branch.HpThreshold is not { } threshold || PullSummary.PrimaryBossHPPercent(pull, branch.DecisionTime) is not { } hp)
            return null;
        var below = hp < threshold;
        if (fullSequence.Branch != null && fullSequence.Branch.Below != below)
            return null;
        return rebuilt.Sequences.FirstOrDefault(s => s.Branch?.Below == below) ?? rebuilt.Sequences[0];
    }
}
