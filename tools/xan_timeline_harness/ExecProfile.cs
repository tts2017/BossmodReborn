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
    }

    private static readonly Dictionary<string, Job> _jobs = [];

    public static void Reset() => _jobs.Clear();

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
        if (j.Worst.Count < Slowest || us > j.Worst[^1].Us)
        {
            j.Worst.Add((us, ClientReject.Scenario, time));
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
            foreach (var (us, scenario, t) in j.Worst)
                output.WriteLine(FormattableString.Invariant($"  slow us={us:f0} t={t:f2} scenario={scenario}"));
        }
    }
}
