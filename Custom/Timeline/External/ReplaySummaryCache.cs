using System.IO;
using System.Text.Json;

namespace BossMod;

// One replay's pull summaries, remembered next to the automatic timelines so a replay is parsed once. The source size and write
// time tell whether the replay changed since; a failed parse is remembered too, so a broken file is not retried on every rebuild.
public sealed record ReplaySummaryFile(int Version, long SourceSize, long SourceWriteTicks, bool Failed, IReadOnlyList<PullSummary> Pulls);

public static class ReplaySummaryCache
{
    // Bump whenever PullSummary, its nested records or the ExternalStateKind values change: fields are stored by name and the kind as
    // its integer value, so an older file would read back with fields missing, defaulted or misnamed instead of being rejected.
    // Bump as well whenever ReplayTimelineExtractor.FindPulls or PullSummary.Summarize would produce something else for the same
    // replay (pull boundaries, events, windows, HP samples): a cached summary is only re-made when its replay file changes, so
    // without a bump users would keep building timelines from the pulls of the old extraction.
    public const int Version = 1;

    public static string CacheDirectory(string timelinesDir) => Path.Combine(timelinesDir, "auto", "cache");
    public static string CachePath(string timelinesDir, string replayPath) => Path.Combine(CacheDirectory(timelinesDir), Path.GetFileName(replayPath) + ".json");

    public static ReplaySummaryFile ForReplay(FileInfo replay, IReadOnlyList<PullSummary> pulls, bool failed)
        => new(Version, replay.Length, replay.LastWriteTimeUtc.Ticks, failed, pulls);

    // Null when the file is missing, of another version, not valid JSON or incomplete: the caller treats it as never summarized.
    // A file that exists but cannot be read right now (held by another process, access denied, a failing disk) throws the IO or
    // access error instead: a rebuild must not mistake a summary it could not open for one that does not exist.
    public static ReplaySummaryFile? Read(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;
            using var stream = File.OpenRead(cachePath);
            var file = JsonSerializer.Deserialize<ReplaySummaryFile>(stream);
            return file is { Version: Version, Pulls: not null } && file.Pulls.All(Complete) ? file : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FileNotFoundException or DirectoryNotFoundException)
        {
            return null; // corrupt, or deleted between the check and the open
        }
    }

    // A summary of this replay as it is now, or null. A cache that cannot be read now is not fresh either: the replay is summarized
    // again, and writing that summary retries (and fails the job) if the file stays held.
    public static ReplaySummaryFile? ReadFresh(string cachePath, FileInfo replay)
    {
        ReplaySummaryFile? file;
        try
        {
            file = Read(cachePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        return file != null && file.SourceSize == replay.Length && file.SourceWriteTicks == replay.LastWriteTimeUtc.Ticks ? file : null;
    }

    public static void Write(string cachePath, ReplaySummaryFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        ReplacingFile.WriteAllText(cachePath, JsonSerializer.Serialize(file));
    }

    // System.Text.Json leaves a missing constructor argument or a null list element as null, so a file of the right version can still
    // have a hole anywhere in a pull; a consumer would throw on it at every rebuild, so it reads as never summarized instead.
    private static bool Complete(PullSummary? pull)
        => pull is { BossOIDs: not null, Events: not null, NoTarget: not null, BossUntargetable: not null, Bosses: not null }
        && pull.Events.All(e => e != null) && pull.NoTarget.All(w => w != null) && pull.BossUntargetable.All(w => w != null)
        && pull.Bosses.All(b => b is { Existence: not null, History: not null } && b.Existence.All(r => r != null) && b.History.All(h => h != null));
}
