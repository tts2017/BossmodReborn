using System.IO;
using System.Reflection;
using System.Text.Json;

namespace BossMod;

// Every imported fight timeline we know about, keyed by zone. The embedded manifest ships the event-trigger and cactbot imports;
// the user directory adds replay extractions and hand edits without a rebuild, and its auto subfolder the plugin's own automatic
// extractions. A zone can have several candidates, and consumers get them ranked: what was measured on this client beats what was
// written for another.
public static class TimelineStore
{
    private const string ResourceName = "BossMod.ExternalTimelines.json";
    private static readonly object Lock = new();
    private static Dictionary<ushort, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition>> _byZone = [];
    private static readonly Dictionary<ushort, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition>> _testOverrides = [];
    private static bool _loaded;
    private static bool _backgroundRunning; // a pool thread is working through reload requests
    private static bool _backgroundPending; // a request arrived after that thread last started a load

    public static string? UserDirectory { get; set; }

    // Bumped at the end of every successful Reload, so a consumer holding a timeline instance can tell that the store it came from
    // has been replaced (a user file edited and re-read) and resolve again instead of following a stale copy.
    public static int Generation { get; private set; }

    // ForZone is called from the world-update path, so a reload that throws must not leave the store un-loaded: EnsureLoaded
    // would retry, and rethrow, on every lookup. Whatever happens in the body, the store counts as loaded afterwards.
    public static void Reload()
    {
        try
        {
            var byZone = LoadCandidates();
            lock (Lock)
            {
                _byZone = byZone;
                ++Generation;
            }
        }
        finally
        {
            lock (Lock)
            {
                if (!_loaded && _byZone.Count == 0)
                    Service.Log("[TimelineStore] Initial load failed: no timelines are available until the next reload");
                _loaded = true;
            }
        }
    }

    // For the draw thread: the reload button, and the folder setting, which fires on every keystroke. Deserializing the embedded
    // manifest alone takes about 125 ms, a visible freeze there. Requests that arrive while a load runs collapse into one more load
    // after it, so the store ends on the latest UserDirectory; consumers notice the swap through Generation.
    public static void ReloadInBackground()
    {
        lock (Lock)
        {
            _backgroundPending = true;
            if (_backgroundRunning)
                return;
            _backgroundRunning = true;
        }
        Task.Run(RunBackgroundReloads);
    }

    // True while a background reload is queued or running (tests wait on it).
    public static bool BackgroundReloadActive
    {
        get
        {
            lock (Lock)
                return _backgroundRunning;
        }
    }

    private static void RunBackgroundReloads()
    {
        while (true)
        {
            lock (Lock)
            {
                if (!_backgroundPending)
                {
                    _backgroundRunning = false;
                    return;
                }
                _backgroundPending = false;
            }
            try
            {
                Reload();
            }
            catch (Exception ex)
            {
                Service.Log($"[TimelineStore] Background reload failed: {ex.Message}");
            }
        }
    }

    private static Dictionary<ushort, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition>> LoadCandidates()
    {
        var candidates = new List<ExternalPlannerTimeline.TimelineDefinition>(LoadEmbedded());
        var directory = UserDirectory;
        try
        {
            // LoadUserFile covers a broken file; the enumeration itself can still fail (unreadable folder, or one removed
            // between the Exists check and the lazy enumeration). Keep the embedded set in that case.
            if (directory != null && Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                    candidates.AddRange(LoadUserFile(path));
            // Timelines the plugin extracted by itself sit one level down; they rank below the hand-placed ones (Priority). Only
            // that folder's own files are read: the replay summaries in its cache subfolder are no timelines.
            var autoDirectory = directory != null ? AutoTimelineGate.AutoDirectory(directory) : null;
            if (autoDirectory != null && Directory.Exists(autoDirectory))
                foreach (var path in Directory.EnumerateFiles(autoDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                    candidates.AddRange(LoadUserFile(path));
        }
        catch (Exception ex)
        {
            Service.Log($"[TimelineStore] Cannot enumerate '{directory}': {ex.Message}");
        }
        // The follower scans windows in Start order and stops at the first one past its horizon; the generators write them sorted,
        // but a hand-edited user file need not, so the order is established here rather than trusted.
        foreach (var timeline in candidates)
            foreach (var sequence in timeline.Sequences)
                sequence.Windows?.Sort((a, b) => a.Start.CompareTo(b.Start));
        return candidates
            .Where(t => t.ZoneID is > 0 and <= ushort.MaxValue)
            .GroupBy(t => (ushort)t.ZoneID)
            .ToDictionary(g => g.Key, g => Rank(g));
    }

    public static ExternalPlannerTimeline.TimelineDefinition? ForZone(ushort zone) => CandidatesForZone(zone).FirstOrDefault();

    public static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> CandidatesForZone(ushort zone)
    {
        EnsureLoaded();
        lock (Lock)
        {
            if (_testOverrides.TryGetValue(zone, out var overridden))
                return overridden;
            return _byZone.TryGetValue(zone, out var list) ? list : [];
        }
    }

    public static IEnumerable<ExternalPlannerTimeline.TimelineDefinition> AllTimelines()
    {
        EnsureLoaded();
        // Snapshot the ranked lists once; an override may be empty, and override-only zones are listed too.
        List<IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition>> lists;
        lock (Lock)
            lists = _byZone.Keys.Union(_testOverrides.Keys).Select(z => _testOverrides.TryGetValue(z, out var overridden) ? overridden : _byZone[z]).ToList();
        return lists.Where(l => l.Count > 0).Select(l => l[0]).ToList();
    }

    public static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> Rank(IEnumerable<ExternalPlannerTimeline.TimelineDefinition> candidates)
        => candidates.OrderByDescending(t => Priority(t.Source)).ThenByDescending(t => t.Confidence).ToList();

    // Hand-placed replay extractions and edits first, then the automatic extraction (validated on this client's own pulls, but by
    // rules rather than a person), then FFLogs, cactbot and the event-trigger imports.
    private static int Priority(ExternalPlannerTimeline.TimelineSource source) => source switch
    {
        ExternalPlannerTimeline.TimelineSource.Replay => 5,
        ExternalPlannerTimeline.TimelineSource.User => 4,
        ExternalPlannerTimeline.TimelineSource.AutoReplay => 3,
        ExternalPlannerTimeline.TimelineSource.FFLogs => 2,
        ExternalPlannerTimeline.TimelineSource.Cactbot => 1,
        _ => 0
    };

    public static void SetCandidatesForTesting(ushort zone, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition>? candidates)
    {
        lock (Lock)
        {
            if (candidates == null)
                _testOverrides.Remove(zone);
            else
                _testOverrides[zone] = candidates;
        }
    }

    public static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> LoadUserFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var timelines = JsonSerializer.Deserialize<Manifest>(stream)?.Timelines ?? [];
            var sane = new List<ExternalPlannerTimeline.TimelineDefinition>(timelines.Count);
            foreach (var timeline in timelines)
                if (Sanitize(timeline) is { } t)
                    sane.Add(t);
            if (sane.Count != timelines.Count)
                Service.Log($"[TimelineStore] '{path}': skipped {timelines.Count - sane.Count} timeline(s) with missing sequences or states");
            return sane;
        }
        catch (Exception ex)
        {
            Service.Log($"[TimelineStore] Skipping '{path}': {ex.Message}");
            return [];
        }
    }

    // A user file is hand-edited, and the deserializer leaves a missing list or name null even where the record says it cannot be.
    // Those nulls would throw later in the store's own sort (emptying every zone, embedded ones included) or in the follower's
    // cast handlers, so such parts are dropped here: a timeline whose sequences are missing, or all broken, is skipped whole.
    // An explicitly empty list is kept, as older files have it.
    private static ExternalPlannerTimeline.TimelineDefinition? Sanitize(ExternalPlannerTimeline.TimelineDefinition? timeline)
    {
        if (timeline?.Sequences == null)
            return null;

        var sequences = new List<ExternalPlannerTimeline.TimelineSequence>(timeline.Sequences.Count);
        foreach (var sequence in timeline.Sequences)
        {
            if (sequence?.States == null)
                continue;
            var states = new List<ExternalPlannerTimeline.TimelineState>(sequence.States.Count);
            foreach (var state in sequence.States)
                if (state?.IDs != null)
                    states.Add(state.Name == null ? state with { Name = "" } : state);
            var windows = sequence.Windows?.Where(w => w != null).ToList();
            sequences.Add(sequence with { States = states, Windows = windows });
        }

        return sequences.Count == 0 && timeline.Sequences.Count > 0 ? null : timeline with { Sequences = sequences, SourceFile = timeline.SourceFile ?? "" };
    }

    private static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> LoadEmbedded()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            return stream != null ? JsonSerializer.Deserialize<Manifest>(stream)?.Timelines ?? [] : [];
        }
        catch (Exception ex)
        {
            Service.Log($"[TimelineStore] Cannot load the embedded manifest: {ex.Message}");
            return [];
        }
    }

    private static void EnsureLoaded()
    {
        bool loaded;
        lock (Lock)
            loaded = _loaded;
        if (!loaded)
            Reload();
    }

    private sealed record Manifest(List<ExternalPlannerTimeline.TimelineDefinition> Timelines);
}
