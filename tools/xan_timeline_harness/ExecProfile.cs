using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace XanTimelineHarness;

// XAN_HARNESS_EXEC_PROFILE=1: the cost of every rotation module Execute call, per job - a 1 us histogram (percentiles), the
// maximum, managed bytes allocated per call, and the slowest calls with their fight and time. The plugin calls Execute once per
// rendered frame, so these per-call numbers are what a player pays every frame (the harness itself steps at 20 Hz).
internal static class ExecProfile
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("XAN_HARNESS_EXEC_PROFILE") == "1";

    private const int Buckets = 100_000; // 1 us each, up to 100 ms; slower calls land in the last bucket
    private const int Slowest = 8;

    private sealed class Job
    {
        public readonly long[] Histogram = new long[Buckets];
        public long Calls;
        public long TotalTicks;
        public long MaxTicks;
        public long AllocatedBytes;
        public long AllocatingCalls;
        public readonly List<(double Us, string Scenario, float T)> Worst = [];
        // calls that show as a hitch: over 4 ms (a quarter of a 60 Hz frame) / 16 ms (a whole frame); the first 10 s of each pull (the
        // countdown included); "warm" = every run after the job's first, so the JIT's first calls are counted apart
        public long Over4, Over16, WarmCalls, WarmOver4, WarmOver16;
        public double First10MaxUs, WarmMaxUs, WarmFirst10MaxUs;
        public int Runs;
        public int LastRun = -1;
        public double RunMaxUs, RunFirst10MaxUs;
        public long RunOver4, RunOver16;
        public readonly List<string> RunLines = [];
    }

    private static readonly Dictionary<string, Job> _jobs = [];
    private static int _run;
    private static readonly bool _perRun = Environment.GetEnvironmentVariable("XAN_HARNESS_EXEC_PROFILE_RUNS") == "1";

    public static void Reset() => _jobs.Clear();

    // module construction (the plugin builds the module on the frame a preset selects it, so a slow one is a hitch of its own)
    private static readonly Dictionary<string, List<double>> _creates = [];
    public static void RecordCreate(string job, long ticks)
    {
        if (!Enabled)
            return;
        if (!_creates.TryGetValue(job, out var list))
            _creates[job] = list = [];
        list.Add(ticks * 1_000_000.0 / Stopwatch.Frequency);
    }

    // a new pull (ContinuousTimelineRunner.Run): the per-run maxima restart
    public static void BeginRun() => ++_run;

    private static void CloseRun(Job j)
    {
        if (j.LastRun >= 0)
            j.RunLines.Add(FormattableString.Invariant($"  run {j.Runs} max_us={j.RunMaxUs:f0} first10_max_us={j.RunFirst10MaxUs:f0} over_4ms={j.RunOver4} over_16ms={j.RunOver16}"));
    }

    public static void Record(string job, long ticks, long allocated, float time)
    {
        if (!_jobs.TryGetValue(job, out var j))
            _jobs[job] = j = new();
        ++j.Calls;
        j.TotalTicks += ticks;
        j.MaxTicks = Math.Max(j.MaxTicks, ticks);
        if (allocated > 0)
        {
            j.AllocatedBytes += allocated;
            ++j.AllocatingCalls;
        }
        var us = ticks * 1_000_000.0 / Stopwatch.Frequency;
        ++j.Histogram[Math.Min(Buckets - 1, (int)us)];
        if (j.LastRun != _run)
        {
            CloseRun(j);
            j.LastRun = _run;
            ++j.Runs;
            j.RunMaxUs = j.RunFirst10MaxUs = 0;
            j.RunOver4 = j.RunOver16 = 0;
        }
        var first10 = time < 10;
        j.RunMaxUs = Math.Max(j.RunMaxUs, us);
        if (first10)
        {
            j.First10MaxUs = Math.Max(j.First10MaxUs, us);
            j.RunFirst10MaxUs = Math.Max(j.RunFirst10MaxUs, us);
        }
        if (us > 4000)
        {
            ++j.Over4;
            ++j.RunOver4;
        }
        if (us > 16000)
        {
            ++j.Over16;
            ++j.RunOver16;
        }
        if (j.Runs > 1)
        {
            ++j.WarmCalls;
            j.WarmMaxUs = Math.Max(j.WarmMaxUs, us);
            if (first10)
                j.WarmFirst10MaxUs = Math.Max(j.WarmFirst10MaxUs, us);
            if (us > 4000)
                ++j.WarmOver4;
            if (us > 16000)
                ++j.WarmOver16;
        }
        if (j.Worst.Count < Slowest || us > j.Worst[^1].Us)
        {
            j.Worst.Add((us, $"{ClientReject.Scenario}#run{j.Runs}", time));
            j.Worst.Sort((a, b) => b.Us.CompareTo(a.Us));
            if (j.Worst.Count > Slowest)
                j.Worst.RemoveAt(j.Worst.Count - 1);
        }
    }

    public static void Print(TextWriter output)
    {
        if (!Enabled)
            return;
        foreach (var (name, j) in _jobs.OrderBy(kv => kv.Key))
        {
            double Percentile(double p)
            {
                var target = (long)Math.Ceiling(p * j.Calls);
                long seen = 0;
                for (var i = 0; i < Buckets; ++i)
                {
                    seen += j.Histogram[i];
                    if (seen >= target)
                        return i + 1; // upper edge of the bucket
                }
                return Buckets;
            }
            var meanUs = j.Calls > 0 ? j.TotalTicks * 1_000_000.0 / Stopwatch.Frequency / j.Calls : 0;
            var over1ms = j.Histogram.Skip(1000).Sum();
            output.WriteLine(FormattableString.Invariant($"exec_profile job={name} calls={j.Calls} mean_us={meanUs:f1} p50_us<={Percentile(0.50):f0} p95_us<={Percentile(0.95):f0} p99_us<={Percentile(0.99):f0} p999_us<={Percentile(0.999):f0} max_us={j.MaxTicks * 1_000_000.0 / Stopwatch.Frequency:f0} over_1ms={over1ms} alloc_bytes_per_call={(j.Calls > 0 ? (double)j.AllocatedBytes / j.Calls : 0):f0} allocating_calls={j.AllocatingCalls}"));
            if (_creates.TryGetValue(name, out var creates) && creates.Count > 0)
                output.WriteLine(FormattableString.Invariant($"exec_create job={name} first_us={creates[0]:f0} later_max_us={(creates.Count > 1 ? creates.Skip(1).Max() : 0):f0} count={creates.Count}"));
            output.WriteLine(FormattableString.Invariant($"exec_frames job={name} runs={j.Runs} over_4ms={j.Over4} over_16ms={j.Over16} first10_max_us={j.First10MaxUs:f0} warm_calls={j.WarmCalls} warm_max_us={j.WarmMaxUs:f0} warm_first10_max_us={j.WarmFirst10MaxUs:f0} warm_over_4ms={j.WarmOver4} warm_over_16ms={j.WarmOver16}"));
            foreach (var (us, scenario, t) in j.Worst)
                output.WriteLine(FormattableString.Invariant($"  slow us={us:f0} t={t:f2} scenario={scenario}"));
            if (_perRun)
            {
                CloseRun(j);
                j.LastRun = -1;
                foreach (var line in j.RunLines)
                    output.WriteLine(line);
            }
        }
    }
}
