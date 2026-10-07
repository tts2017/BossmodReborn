using System.IO;
using System.Threading;

namespace BossMod;

// The earlier kills the FightTimeEstimator aligns a running fight with, per zone and boss module OID, built from the replay summaries the
// auto-timeline feature keeps (timelines\auto\cache, see ReplaySummaryCache and FightPriorBuilder). Loading never runs on the caller's
// thread: RefreshIfStale starts one background load when the cache folder changed since the last one, and the finished table replaces the old
// one atomically. A missing folder, an unreadable file or a failed load leaves the previous table (initially empty), so every fight without a
// prior behaves exactly as before.
public sealed class FightPriorStore(Func<string?> timelinesDirectory)
{
    public const int MaxKillsPerFight = 64; // the most recent ones: bounds memory and the alignment cost per estimate

    private volatile Dictionary<(ushort Zone, uint OID), FightPrior> _priors = [];
    private int _loading;
    private string _signature = "";

    // Counts finished loads, so a consumer that picked its prior earlier can tell that a better table has arrived.
    public int Generation { get; private set; }
    public int FightCount => _priors.Count;
    public bool Loading => Volatile.Read(ref _loading) != 0;

    // The prior of the fight whose boss module has this OID in this zone; null when there is no earlier kill (or fewer than two).
    public FightPrior? Find(ushort zone, uint oid) => _priors.TryGetValue((zone, oid), out var prior) ? prior : null;

    // Cheap and non-blocking: starts a background load unless one is running or the folder looks unchanged.
    public void RefreshIfStale()
    {
        if (Interlocked.CompareExchange(ref _loading, 1, 0) != 0)
            return;
        var thread = new Thread(() =>
        {
            try
            {
                var directory = timelinesDirectory();
                if (string.IsNullOrEmpty(directory))
                    return;
                var cache = ReplaySummaryCache.CacheDirectory(directory);
                if (!Directory.Exists(cache))
                    return;
                var signature = Signature(cache);
                if (signature == _signature)
                    return;
                var table = Build(cache);
                _priors = table;
                _signature = signature;
                ++Generation;
            }
            catch (Exception ex)
            {
                Service.Log($"[FightPriorStore] load failed, keeping the previous priors: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _loading, 0);
            }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "BMR fight priors" };
        thread.Start();
    }

    // Synchronous load (tools and tests).
    public void Load(string cacheDirectory)
    {
        _priors = Build(cacheDirectory);
        _signature = Signature(cacheDirectory);
        ++Generation;
    }

    private static string Signature(string cacheDirectory)
    {
        var count = 0;
        long newest = 0;
        foreach (var file in new DirectoryInfo(cacheDirectory).EnumerateFiles("*.json"))
        {
            ++count;
            newest = Math.Max(newest, file.LastWriteTimeUtc.Ticks);
        }
        return $"{count}:{newest}";
    }

    public static Dictionary<(ushort Zone, uint OID), FightPrior> Build(string cacheDirectory)
    {
        List<(ushort Zone, uint OID, DateTime Start, FightPrior.Kill Kill)> kills = [];
        foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*.json"))
        {
            ReplaySummaryFile? summaries;
            try
            {
                summaries = ReplaySummaryCache.Read(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // held by another process right now: this file's kills are missing from this load, the others still count
            }
            if (summaries == null || summaries.Failed)
                continue;
            foreach (var pull in summaries.Pulls)
                if (pull.Zone != 0 && pull.BossOIDs.Count == 1 && FightPriorBuilder.KillOf(pull) is { } kill)
                    kills.Add((pull.Zone, pull.BossOIDs[0], pull.Start, kill));
        }
        Dictionary<(ushort, uint), FightPrior> table = [];
        foreach (var group in kills.GroupBy(k => (k.Zone, k.OID)))
        {
            var recent = group.OrderByDescending(k => k.Start).Take(MaxKillsPerFight).ToList();
            if (recent.Count < 2)
                continue;
            var prior = new FightPrior();
            foreach (var k in recent)
                prior.Add(k.Kill);
            table[group.Key] = prior;
        }
        return table;
    }
}