using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BossMod;

namespace XanTimelineHarness;

// XAN_HARNESS_CLIENT_REJECT=1: the job emulators check job resources and statuses after the shared queue picks a candidate, as
// the real client does, and a refused pick costs the frame (ActionManagerEx logs "Can't execute" and tries again next frame).
// By default they drop infeasible candidates before the pick instead, which hides pushes the game would refuse.
// Refusals are counted per job and action: frames, episodes (runs of consecutive refused frames) and the first few examples.
internal static class ClientReject
{
    // settable because --irregular and irregular-compare imply it (their refusals go through the same path)
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("XAN_HARNESS_CLIENT_REJECT") == "1";
    // =1 also prints every episode as it starts, with the player's statuses (use with --scenario-filter)
    public static readonly bool LogEpisodes = Environment.GetEnvironmentVariable("XAN_HARNESS_CLIENT_REJECT_LOG") == "1";
    public static DateTime BaseTime;
    public static string Scenario = "";

    private const double EpisodeGapSeconds = 0.25;
    private const int MaxExamples = 5;

    private sealed class Counter
    {
        public long Frames;
        public long Episodes;
        public readonly List<(string Scenario, DateTime At)> Examples = [];
    }

    private static readonly Dictionary<(string Job, ActionID Action), Counter> _counters = [];
    private static (string Job, ActionID Action) _lastKey;
    private static DateTime _lastAt;
    private static string _lastScenario = "";

    public static void Reset()
    {
        _counters.Clear();
        _lastKey = default;
        _lastScenario = "";
    }

    public static long TotalFrames => _counters.Values.Sum(c => c.Frames);

    public static void Record(string job, ActionID action, DateTime now, Actor? player = null)
    {
        var key = (job, action);
        if (!_counters.TryGetValue(key, out var counter))
            _counters[key] = counter = new();
        ++counter.Frames;
        var continues = _lastKey == key && _lastScenario == Scenario && (now - _lastAt).TotalSeconds <= EpisodeGapSeconds;
        if (!continues)
        {
            ++counter.Episodes;
            if (counter.Examples.Count < MaxExamples)
                counter.Examples.Add((Scenario, now));
            if (LogEpisodes)
            {
                var statuses = player == null ? "" : string.Join(" ", player.Statuses.Where(st => st.ID != 0).Select(st => FormattableString.Invariant($"{st.ID}:{Service.LuminaRow<Lumina.Excel.Sheets.Status>(st.ID)?.Name}:{(st.ExpireAt - now).TotalSeconds:f1}")));
                Console.WriteLine(FormattableString.Invariant($"client_reject_episode job={job} action={action} t={(now - BaseTime).TotalSeconds:f2} scenario={Scenario} statuses=[{statuses}]"));
            }
        }
        _lastKey = key;
        _lastAt = now;
        _lastScenario = Scenario;
    }

    public static void Print(TextWriter output, DateTime baseTime)
    {
        if (!Enabled)
            return;
        output.WriteLine($"client_reject total_frames={TotalFrames} actions={_counters.Count}");
        foreach (var ((job, action), counter) in _counters.OrderByDescending(kv => kv.Value.Frames))
        {
            var name = action.Type == ActionType.Spell ? Service.LuminaRow<Lumina.Excel.Sheets.Action>(action.ID)?.Name.ToString() ?? "?" : action.ToString();
            output.WriteLine(FormattableString.Invariant($"client_reject job={job} action={name}({action.ID}) frames={counter.Frames} episodes={counter.Episodes}"));
            foreach (var (scenario, at) in counter.Examples)
                output.WriteLine(FormattableString.Invariant($"  example t={(at - baseTime).TotalSeconds:f2} scenario={scenario}"));
        }
    }
}
