using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BossMod;

namespace XanTimelineHarness;

// Replays every imported per-content timeline against ExternalTimelineHints and reports how well the tracker follows it: whether a
// boss cast ever places the clock, how early the untargetable windows are announced, and how often a window is announced that the
// timeline never reaches. Ground truth is the timeline itself, so what is measured is the tracker, not the imported data.
internal static class TimelineHintsValidation
{
    private const float FrameStep = 0.05f;
    private const float LeadRequirement = 3f; // a prediction is useful when it arrives at least this long before the window
    private const float MatchTolerance = 2f;
    private const float NearMissTolerance = 8f; // a window announced this early or late is the same window on a fight that ran off time
    private static readonly DateTime BaseTime = new(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);
    private const ulong EnemyID = 0x40000001;
    private static bool Debug;

    // drift stretches the replayed fight against the imported times, the way a faster or slower kill does in practice
    public static int Run(int? zoneFilter, bool verbose, float drift = 0f)
    {
        Service.Config.Get<BossModuleConfig>().UseExternalTimelineHints = true;
        Debug = verbose && zoneFilter != null;
        var timelines = ExternalPlannerTimeline.AllTimelines()
            .Where(timeline => zoneFilter == null || timeline.ZoneID == zoneFilter)
            .OrderBy(timeline => timeline.ZoneID)
            .ToArray();

        int zonesWithDowntime = 0, zonesSynced = 0, windows = 0, announced = 0, announcedEarly = 0, falseAlarms = 0, nearMisses = 0;
        var errors = new List<float>();
        foreach (var timeline in timelines)
        {
            var sequence = timeline.Sequences.FirstOrDefault(candidate => candidate.States.Count > 0);
            if (sequence == null)
                continue;
            var truth = DowntimeWindows(sequence);
            if (truth.Count == 0)
                continue;

            ++zonesWithDowntime;
            var result = Replay(timeline, sequence, truth, drift);
            if (result.Synced)
                ++zonesSynced;
            windows += truth.Count;
            announced += result.Announced;
            announcedEarly += result.AnnouncedEarly;
            falseAlarms += result.FalseAlarms;
            nearMisses += result.NearMisses;
            errors.AddRange(result.Errors);
            if (verbose)
                Console.WriteLine(FormattableString.Invariant($"zone={timeline.ZoneID} file={timeline.SourceFile} windows={truth.Count} announced={result.Announced} early={result.AnnouncedEarly} false={result.FalseAlarms} synced={result.Synced}"));
        }

        Console.WriteLine(FormattableString.Invariant($"timelines={timelines.Length} with_downtime={zonesWithDowntime} synced={zonesSynced}"));
        Console.WriteLine(FormattableString.Invariant($"downtime_windows={windows} announced={announced} announced_{LeadRequirement:f0}s_ahead={announcedEarly} near_misses={nearMisses} false_alarms={falseAlarms}"));
        Console.WriteLine(FormattableString.Invariant($"loss_time_error_mean={(errors.Count > 0 ? errors.Average() : 0):f3} max={(errors.Count > 0 ? errors.Max() : 0):f3}"));
        return 0;
    }

    private static List<(float Start, float End)> DowntimeWindows(ExternalPlannerTimeline.TimelineSequence sequence)
    {
        List<(float, float)> result = [];
        var end = sequence.PredictionEndTime ?? float.MaxValue;
        var start = float.MaxValue;
        foreach (var state in sequence.States)
        {
            if (state.Time > end)
                break;
            if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Untargetable && start == float.MaxValue)
                start = state.Time;
            else if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Targetable && start < float.MaxValue)
            {
                if (state.Time - start >= ExternalTimelineHints.MinPublishedLoss)
                    result.Add((start, state.Time));
                start = float.MaxValue;
            }
        }
        return result;
    }

    private static Result Replay(ExternalPlannerTimeline.TimelineDefinition timeline, ExternalPlannerTimeline.TimelineSequence sequence, List<(float Start, float End)> truth, float drift)
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "timeline-hints-validation");
        world.Execute(new WorldState.OpFrameStart(new(BaseTime, 0, 0, 0, 0, 1), default, default, default));
        world.Execute(new WorldState.OpZoneChange((ushort)timeline.ZoneID, 0));
        world.Execute(new ActorState.OpCreate(EnemyID, 0x1234, 1, 0, "Boss", 0, ActorType.Enemy, Class.None, 100, new Vector4(0, 0, 0, 0), 2f, new(1000000, 1000000, 0, 10000, 10000), true, false, default, default, 0));

        using var hints = new ExternalTimelineHints(world);
        var result = new Result();
        var announcedWindows = new bool[truth.Count];
        var earlyWindows = new bool[truth.Count];
        var stateIndex = 0;
        var castUntil = float.MinValue;
        var pulled = false;
        var scale = 1f + drift;
        var start = Math.Min(sequence.StartTime, sequence.States[0].Time) - 5f;
        truth = truth.Select(window => (window.Start * scale, window.End * scale)).ToList();
        var last = truth[^1].Item2 + 10f;
        for (var time = start; time <= last; time += FrameStep)
        {
            world.Execute(new WorldState.OpFrameStart(new(BaseTime.AddSeconds(time - start), (ulong)((time - start) / FrameStep), 0, FrameStep, FrameStep, 1), TimeSpan.FromSeconds(FrameStep), default, default));

            if (!pulled && time >= sequence.StartTime)
            {
                pulled = true;
                world.Execute(new ActorState.OpCombat(EnemyID, true));
            }

            while (stateIndex < sequence.States.Count && sequence.States[stateIndex].Time * scale <= time)
            {
                var state = sequence.States[stateIndex++];
                switch (state.Kind)
                {
                    case ExternalPlannerTimeline.ExternalStateKind.CastStart when state.IDs.Count > 0:
                        world.Execute(new ActorState.OpCastInfo(EnemyID, new() { Action = new(ActionType.Spell, state.IDs[0]), TargetID = 0, TotalTime = 3f }));
                        castUntil = time + 0.5f;
                        break;
                    case ExternalPlannerTimeline.ExternalStateKind.Untargetable:
                        world.Execute(new ActorState.OpTargetable(EnemyID, false));
                        break;
                    case ExternalPlannerTimeline.ExternalStateKind.Targetable:
                        world.Execute(new ActorState.OpTargetable(EnemyID, true));
                        break;
                }
            }
            if (time > castUntil && world.Actors.Find(EnemyID)?.CastInfo != null)
                world.Execute(new ActorState.OpCastInfo(EnemyID, null));

            hints.Update(null);
            result.Synced |= hints.Synced;
            var (lossIn, returnIn) = hints.NextDowntime();
            if (lossIn >= float.MaxValue || returnIn - lossIn < ExternalTimelineHints.MinPublishedLoss)
                continue;

            var predictedStart = time + lossIn;
            // while a downtime is already running the prediction describes the window we are inside, not one that is about to start
            var index = lossIn <= 0.05f
                ? truth.FindIndex(window => time >= window.Start - MatchTolerance && time <= window.End + MatchTolerance)
                : truth.FindIndex(window => Math.Abs(window.Start - predictedStart) <= MatchTolerance);
            if (index < 0)
            {
                // count distinct wrong predictions, not the frames they are held for, and separate a late fight from a wrong phase
                if (result.WrongStarts.Add(MathF.Round(predictedStart)))
                {
                    var nearest = truth.Count > 0 ? truth.Min(window => Math.Abs(window.Start - predictedStart)) : float.MaxValue;
                    if (nearest <= NearMissTolerance)
                        ++result.NearMisses;
                    else
                        ++result.FalseAlarms;
                    if (Debug)
                        Console.WriteLine(FormattableString.Invariant($"  wrong at t={time:f1}: loss_in={lossIn:f1} return_in={returnIn:f1} predicted_start={predictedStart:f1} nearest_truth_gap={nearest:f1}"));
                }
                continue;
            }
            if (!announcedWindows[index])
            {
                announcedWindows[index] = true;
                ++result.Announced;
                if (lossIn > 0.05f)
                    result.Errors.Add(Math.Abs(truth[index].Start - predictedStart));
            }
            if (!earlyWindows[index] && truth[index].Start - time >= LeadRequirement)
            {
                earlyWindows[index] = true;
                ++result.AnnouncedEarly;
            }
        }
        return result;
    }

    private sealed class Result
    {
        public bool Synced;
        public int Announced;
        public int AnnouncedEarly;
        public int FalseAlarms;
        public int NearMisses;
        public List<float> Errors = [];
        public HashSet<float> WrongStarts = [];
    }
}
