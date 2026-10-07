using System.IO;
using System.Threading;

namespace BossMod;

// Builds automatic timelines in the background: each finished recording is summarized once (ReplaySummaryCache), then every zone
// it touched is rebuilt from all summaries of that zone, passed through AutoTimelineGate and published under <timelines>\auto.
// One low-priority thread works through a de-duplicated FIFO queue, so a burst of requests costs one pass per replay and zone.
// Every file this class writes is written by that thread, one at a time: AutoTimelineGate.Publish rewrites the shared report.txt.
// While shouldPause says so (the player is in combat), the worker starts no new job: a parse costs seconds of CPU and memory.
public sealed class AutoTimelineExtractor : IDisposable
{
    public delegate IReadOnlyList<PullSummary>? Summarizer(string replayPath, CancellationToken cancel);

    public static AutoTimelineExtractor? Instance { get; set; }

    // A summary or timeline file can be held for a moment by something else (a virus scanner, an editor, a sync client), so a
    // read or write that fails is tried again this many times, this far apart, before its job counts as failed.
    private const int IoRetries = 3;
    private static readonly TimeSpan IoRetryDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PausePoll = TimeSpan.FromSeconds(1); // how often a paused worker asks whether it may go on

    private enum JobKind { Summarize, Rebuild }
    private readonly record struct Job(JobKind Kind, string Path, ushort Zone);

    private readonly Func<string?> _timelinesDir;
    private readonly Func<string?> _replayDir;
    private readonly Summarizer _summarize;
    private readonly Action _reloadStore;
    private readonly Func<bool>? _shouldPause;
    private readonly object _lock = new();
    private readonly Queue<Job> _queue = new();
    private readonly HashSet<Job> _pending = [];
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread _thread;
    private bool _disposed;
    private bool _paused; // the worker holds back the next job until shouldPause lets it go
    private string? _current; // the job in progress, null while the worker waits for one
    private string _retry = ""; // set while a read or write of that job waits to be tried again
    private string _last = "";
    // Replays whose summary is a failed parse (new or read back from the cache), since the last EnqueueAll or the start.
    private readonly HashSet<string> _failedReplays = new(StringComparer.OrdinalIgnoreCase);

    // reloadStore runs after a rebuild wrote or removed a zone file, so the store loads the new automatic timeline; null means
    // TimelineStore.ReloadInBackground. shouldPause is asked on the worker thread before each job; null never pauses.
    public AutoTimelineExtractor(Func<string?> timelinesDir, Func<string?> replayDir, Summarizer? summarizer = null, Action? reloadStore = null, Func<bool>? shouldPause = null)
    {
        _timelinesDir = timelinesDir;
        _replayDir = replayDir;
        _summarize = summarizer ?? SummarizeReplay;
        _reloadStore = reloadStore ?? TimelineStore.ReloadInBackground;
        _shouldPause = shouldPause;
        _thread = new(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "BMR auto timelines" };
        _thread.Start();
    }

    public bool Idle
    {
        get
        {
            lock (_lock)
                return _queue.Count == 0 && _current == null;
        }
    }

    public string Status
    {
        get
        {
            lock (_lock)
            {
                var status = _paused ? $"paused (in combat) ({_queue.Count} queued)" : _current == null ? "idle" : $"{_current} ({_queue.Count} queued){_retry}";
                if (_last.Length > 0)
                    status += $" | last: {_last}";
                if (_failedReplays.Count > 0)
                    status += $" | failed summaries: {_failedReplays.Count}";
                return status;
            }
        }
    }

    public void EnqueueReplay(string replayPath) => Enqueue(new(JobKind.Summarize, replayPath, 0));

    // Every replay in the folder, oldest first; fresh ones only cost a cache read. Starts a new count of failed summaries.
    public void EnqueueAll()
    {
        lock (_lock)
            _failedReplays.Clear();
        var dir = _replayDir();
        if (dir == null || !Directory.Exists(dir))
            return;
        List<FileInfo> files;
        try
        {
            files = [.. new DirectoryInfo(dir).EnumerateFiles("*.log").OrderBy(f => f.LastWriteTimeUtc)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Service.Log($"[AutoTimelines] Cannot list replays in '{dir}': {ex.Message}");
            return;
        }
        foreach (var file in files)
            EnqueueReplay(file.FullName);
    }

    // Cancels the job in progress and returns once the worker has stopped; every write is a temp file moved into place, so an
    // interrupted job leaves no half-written file.
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _cancel.Cancel();
        lock (_lock)
            Monitor.PulseAll(_lock);
        // A worker still running past the timeout keeps using the token source, so it is only disposed once the worker is gone.
        if (_thread.Join(TimeSpan.FromSeconds(10)))
            _cancel.Dispose();
    }

    public static IReadOnlyList<PullSummary>? SummarizeReplay(string replayPath, CancellationToken cancel)
    {
        var suppressed = ExternalPlannerTimeline.SuppressApply;
        ExternalPlannerTimeline.SuppressApply = true;
        try
        {
            var progress = 0f;
            var replay = ReplayParserLog.Parse(replayPath, ref progress, cancel);
            cancel.ThrowIfCancellationRequested();
            return replay.Ops.Count == 0 ? null : ReplayTimelineExtractor.FindPulls(replay).Select(p => PullSummary.Summarize(p)).ToList();
        }
        finally
        {
            ExternalPlannerTimeline.SuppressApply = suppressed;
        }
    }

    private void Enqueue(Job job)
    {
        lock (_lock)
        {
            if (_disposed || !_pending.Add(job))
                return;
            _queue.Enqueue(job);
            Monitor.PulseAll(_lock);
        }
    }

    private void Run()
    {
        // Nothing on this thread may run an imported timeline, whichever summarizer parses the replays.
        ExternalPlannerTimeline.SuppressApply = true;
        while (!_cancel.IsCancellationRequested)
        {
            Job job;
            lock (_lock)
            {
                while (_queue.Count == 0 && !_cancel.IsCancellationRequested)
                {
                    _current = null;
                    Monitor.Wait(_lock);
                }
                if (_cancel.IsCancellationRequested)
                    return;
            }
            // A job is waiting. In combat it waits longer, polled, and Dispose ends the wait at once.
            if (_shouldPause?.Invoke() == true)
            {
                lock (_lock)
                {
                    _paused = true;
                    _current = null;
                }
                if (_cancel.Token.WaitHandle.WaitOne(PausePoll))
                    return;
                continue;
            }
            lock (_lock)
            {
                _paused = false;
                job = _queue.Dequeue();
                _pending.Remove(job);
                _current = job.Kind == JobKind.Summarize ? $"summarizing {Path.GetFileName(job.Path)}" : $"rebuilding zone {job.Zone}";
            }
            try
            {
                if (job.Kind == JobKind.Summarize)
                    Summarize(job.Path);
                else
                    Rebuild(job.Zone);
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                var what = job.Kind == JobKind.Summarize ? $"summary of {Path.GetFileName(job.Path)}" : $"zone {job.Zone}";
                Service.Log($"[AutoTimelines] {what} failed: {ex.Message}");
                lock (_lock)
                    _last = $"{DateTime.Now:HH:mm} {what} failed: {ex.Message}";
            }
        }
    }

    private void Summarize(string replayPath)
    {
        var timelines = _timelinesDir();
        var replay = new FileInfo(replayPath);
        if (timelines == null || !replay.Exists)
            return; // deleted before its turn (MaxReplays): nothing to remember, a new file of that name is handled normally
        // The replay as it was before the parse (FileInfo keeps what Exists read; it is never refreshed here). A replay that changes
        // while or after it is read, such as a recording still being written, then no longer matches its summary and is summarized
        // again next time, instead of being kept as fresh (or as failed) for content that was never read.
        var (size, writeTicks) = (replay.Length, replay.LastWriteTimeUtc.Ticks);
        var cachePath = ReplaySummaryCache.CachePath(timelines, replayPath);
        var file = ReplaySummaryCache.ReadFresh(cachePath, replay);
        if (file == null)
        {
            IReadOnlyList<PullSummary>? pulls;
            try
            {
                pulls = _summarize(replayPath, _cancel.Token);
            }
            catch (Exception ex) when (!_cancel.IsCancellationRequested)
            {
                // A replay the extractor chokes on would throw again on every pass: it is remembered as failed, like an unreadable one.
                Service.Log($"[AutoTimelines] Cannot summarize {Path.GetFileName(replayPath)}: {ex.Message}");
                pulls = null;
            }
            _cancel.Token.ThrowIfCancellationRequested(); // before the summary is written: a cancelled parse leaves nothing behind
            // Written even if the replay was deleted after the parse: rebuilds read every summary, whether its replay exists or not.
            var summary = new ReplaySummaryFile(ReplaySummaryCache.Version, size, writeTicks, pulls == null, pulls ?? []);
            WithRetries(() => ReplaySummaryCache.Write(cachePath, summary));
            _zonesByCache.Remove(cachePath);
            file = summary;
        }
        if (file.Failed)
            lock (_lock)
                _failedReplays.Add(replayPath);
        foreach (var zone in file.Pulls.Select(p => p.Zone).Where(z => z != 0).Distinct())
            Enqueue(new(JobKind.Rebuild, "", zone));
    }

    // Which zones each cache file holds, so a rebuild only reads the summaries of its zone. Filled on the first rebuild, kept
    // current as summaries are written; a file changed behind our back is re-read when its write time moves. A failed or unusable
    // summary holds no zone. Only the worker thread touches these (Summarize and Rebuild both run there).
    private readonly Dictionary<string, (long WriteTicks, ushort[] Zones)> _zonesByCache = [];
    private readonly HashSet<string> _reportedUnusable = new(StringComparer.OrdinalIgnoreCase); // logged once per session

    private void Rebuild(ushort zone)
    {
        var timelines = _timelinesDir();
        if (timelines == null)
            return;
        var cacheDir = ReplaySummaryCache.CacheDirectory(timelines);
        List<PullSummary> pulls = [];
        if (Directory.Exists(cacheDir))
        {
            // Every summary counts, including those whose replay has since been deleted: the summary is all a rebuild needs. One
            // that cannot be read now is retried, and fails the job if it stays unreadable: a timeline built without it could lose
            // windows, or the whole zone file, that the full set of pulls validates.
            foreach (var path in Directory.EnumerateFiles(cacheDir, "*.json"))
            {
                _cancel.Token.ThrowIfCancellationRequested();
                long ticks = 0;
                ReplaySummaryFile? file = null;
                var known = false;
                WithRetries(() =>
                {
                    ticks = File.GetLastWriteTimeUtc(path).Ticks;
                    known = _zonesByCache.TryGetValue(path, out var zones) && zones.WriteTicks == ticks && !zones.Zones.Contains(zone);
                    if (!known)
                        file = ReplaySummaryCache.Read(path);
                });
                if (known)
                    continue;
                if (file == null)
                {
                    // Corrupt, incomplete or of another version: left out (its replay is summarized again when it is next enqueued).
                    if (File.Exists(path) && _reportedUnusable.Add(path))
                        Service.Log($"[AutoTimelines] Skipping unusable summary {Path.GetFileName(path)} (corrupt, incomplete or of another version)");
                    _zonesByCache[path] = (ticks, []);
                    continue;
                }
                _zonesByCache[path] = (ticks, file.Failed ? [] : file.Pulls.Select(p => p.Zone).Distinct().ToArray());
                if (!file.Failed)
                    pulls.AddRange(file.Pulls.Where(p => p.Zone == zone));
            }
        }
        var result = AutoTimelineGate.Evaluate(zone, pulls, AutoTimelineGate.DefaultMaxPullsPerSet, _cancel.Token);
        _cancel.Token.ThrowIfCancellationRequested();
        var lower = AutoTimelineGate.LowerRanked(zone);
        var line = "";
        try
        {
            WithRetries(() => line = AutoTimelineGate.Publish(timelines, zone, result, lower));
        }
        finally
        {
            // Publish writes or removes the zone file before it rewrites report.txt, so a publish that failed on the report has still
            // changed what the store should load. Skipped on Dispose only: the plugin is going away, and loads the folder on its next start.
            if (!_cancel.IsCancellationRequested)
                _reloadStore();
        }
        lock (_lock)
            _last = $"{DateTime.Now:HH:mm} {line}";
    }

    // Runs a read or write, trying again after an IO or access failure (see IoRetries); the last failure reaches the caller. The
    // waits end at once on Dispose. Writes go through a temp file, so a failed attempt leaves nothing to clean up.
    private void WithRetries(Action io)
    {
        try
        {
            for (var retry = 1; ; ++retry)
            {
                try
                {
                    io();
                    return;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && retry <= IoRetries)
                {
                    lock (_lock)
                        _retry = $", retrying ({retry}/{IoRetries}): {ex.Message}";
                    if (_cancel.Token.WaitHandle.WaitOne(IoRetryDelay))
                        throw new OperationCanceledException(_cancel.Token);
                }
            }
        }
        finally
        {
            lock (_lock)
                _retry = ""; // the write is over, done or given up: Status stops reporting its retries
        }
    }
}
