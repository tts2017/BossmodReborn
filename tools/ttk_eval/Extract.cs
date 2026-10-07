using System.Globalization;
using System.Text;
using BossMod;

// Reads replays and writes, per pull, the "feed" a rotation module would have seen: every 0.5 s the hostile, targetable, living enemies
// of the pull with their HP. Pulls that ended in a kill are flagged with the kill time; the survey command dumps the evidence.
internal static class Extract
{
    public const float GridSeconds = 0.5f;

    public static int Run(string[] args)
    {
        var survey = args[0] == "survey";
        var outPath = args[1];
        var inputs = new List<string>();
        int shard = 0, shards = 1;
        for (var i = 2; i < args.Length; ++i)
        {
            if (args[i] == "--shard")
            {
                var parts = args[++i].Split('/');
                shard = int.Parse(parts[0]);
                shards = int.Parse(parts[1]);
            }
            else inputs.Add(args[i]);
        }
        var files = inputs.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.log", SearchOption.AllDirectories) : [p]).Order().ToList();
        files = files.Where((_, i) => i % shards == shard).ToList();
        if (survey)
            File.WriteAllText(outPath, string.Join(",", SurveyHeader) + "\n");
        else
            Directory.CreateDirectory(outPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var file in files)
        {
            try
            {
                var progress = 0f;
                var replay = ReplayParserLog.Parse(file, ref progress, CancellationToken.None);
                var pulls = ReplayTimelineExtractor.FindPulls(replay);
                var sb = new StringBuilder();
                var records = new List<PullRecord>();
                for (var i = 0; i < pulls.Count; ++i)
                {
                    var rec = Analyze(replay, pulls[i], Path.GetFileName(file), i);
                    if (rec == null)
                        continue;
                    sb.AppendLine(rec.SurveyRow());
                    records.Add(rec);
                }
                if (survey)
                    File.AppendAllText(outPath, sb.ToString());
                else
                    PullRecord.WriteFile(Path.Combine(outPath, Path.GetFileNameWithoutExtension(file) + ".ttk"), records);
                Console.WriteLine($"{Path.GetFileName(file)}: ops={replay.Ops.Count} pulls={pulls.Count} kept={records.Count} kills={records.Count(r => r.KillTime >= 0)} t={sw.Elapsed.TotalSeconds:f0}s");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{file}: {ex}");
            }
        }
        return 0;
    }

    public static readonly string[] SurveyHeader = ["replay", "idx", "zone", "cfc", "cfc_name", "ctype", "highend", "members", "bosses", "module", "duration", "n_enemies", "max_feed", "boss_last_hp_pct", "boss_zero_t", "boss_dead_t", "kill_t", "kill_basis", "du_victory_t", "du_wipe_t", "du_ids_end", "no_feed_frames"];

    // Start of the final run of zero-HP / dead records of a participant that ends the pull, as seconds from the pull start; null when it
    // is not at zero / dead at the end of the pull.
    private static float? FinalDeathTime(Replay.Participant p, DateTime start, DateTime end)
    {
        var probe = end.AddSeconds(1);
        var hp = p.HPMPAt(probe);
        var zeroEnd = hp.MaxHP > 0 && hp.CurHP == 0;
        var deadEnd = p.DeadAt(probe);
        if (!zeroEnd && !deadEnd)
            return null;
        DateTime? t0 = null;
        if (zeroEnd)
        {
            for (var i = p.HPMPHistory.Count - 1; i >= 0; --i)
            {
                if (p.HPMPHistory.Keys[i] > probe)
                    continue;
                if (p.HPMPHistory.Values[i].CurHP != 0)
                    break;
                t0 = p.HPMPHistory.Keys[i];
            }
        }
        if (deadEnd)
        {
            DateTime? d0 = null;
            for (var i = p.DeadHistory.Count - 1; i >= 0; --i)
            {
                if (p.DeadHistory.Keys[i] > probe)
                    continue;
                if (!p.DeadHistory.Values[i])
                    break;
                d0 = p.DeadHistory.Keys[i];
            }
            if (d0 != null && (t0 == null || d0 < t0))
                t0 = d0;
        }
        return t0 == null ? 0 : (float)(t0.Value - start).TotalSeconds;
    }

    // Returns null for pulls that cannot be evaluated (no HP data at all).
    internal static PullRecord? Analyze(Replay replay, ReplayTimelineExtractor.Pull pull, string replayName, int index)
    {
        var enemies = pull.Enemies.Where(p => p.HPMPHistory.Count > 0).ToList();
        if (enemies.Count == 0)
            return null;
        var rec = new PullRecord
        {
            Replay = replayName, Index = index, Zone = pull.Zone, Start = pull.Start,
            Duration = pull.Seconds(pull.End), Module = replay.Encounters.Count > 0,
            BossOIDs = pull.BossOIDs.ToList(),
        };
        var anyEnemy = pull.Enemies.FirstOrDefault(p => pull.BossOIDs.Contains(p.OID)) ?? pull.Enemies.FirstOrDefault();
        rec.CFC = anyEnemy?.CFCID ?? 0;
        var cfc = rec.CFC != 0 ? Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(rec.CFC) : null;
        rec.CFCName = cfc?.Name.ToString() ?? "";
        rec.ContentType = cfc?.ContentType.RowId ?? 0;
        rec.HighEnd = cfc?.HighEndDuty ?? false;
        rec.Members = cfc?.ContentMemberType.RowId ?? 0;

        // Director updates around the end of the pull: victory / wipe markers.
        var lo = pull.Start;
        var hi = pull.End.AddSeconds(20);
        var ends = new Dictionary<uint, int>();
        foreach (var du in replay.DirectorUpdates)
        {
            if (du.Timestamp < lo || du.Timestamp > hi)
                continue;
            if (du.UpdateID == 0x40000003u && rec.VictoryT < 0)
                rec.VictoryT = (float)(du.Timestamp - pull.Start).TotalSeconds;
            if (du.UpdateID == 0x40000005u && rec.WipeT < 0)
                rec.WipeT = (float)(du.Timestamp - pull.Start).TotalSeconds;
            if (du.Timestamp >= pull.End.AddSeconds(-5))
                ends[du.UpdateID] = ends.GetValueOrDefault(du.UpdateID) + 1;
        }
        rec.DuIdsEnd = string.Join("|", ends.Select(kv => $"{kv.Key:X}:{kv.Value}"));

        // Boss-sized enemies: all must be at zero HP / dead when the pull ends for a boss-zero kill.
        var bosses = enemies.Where(p => pull.BossOIDs.Contains(p.OID)).ToList();
        if (bosses.Count == 0)
            bosses = enemies;
        var allKilled = true;
        float lastDeath = -1;
        float lastPct = -1;
        foreach (var b in bosses)
        {
            var death = FinalDeathTime(b, pull.Start, pull.End);
            if (death == null)
                allKilled = false;
            else
                lastDeath = Math.Max(lastDeath, death.Value);
            var hp = b.HPMPAt(pull.End.AddSeconds(1));
            if (hp.MaxHP > 0)
                lastPct = Math.Max(lastPct, 100f * hp.CurHP / hp.MaxHP);
        }
        rec.BossLastHPPct = lastPct;
        rec.AllBossesZero = allKilled;
        rec.BossZeroT = lastDeath;
        rec.KillCandidateT = lastDeath;

        // Frames
        var frames = new List<PullRecord.Frame>();
        var n = (int)Math.Floor(rec.Duration / GridSeconds);
        var noFeed = 0;
        var maxFeed = 0;
        for (var k = 0; k <= n; ++k)
        {
            var t = pull.Start.AddSeconds(k * GridSeconds);
            var targets = new List<PullRecord.Target>();
            foreach (var p in enemies)
            {
                if (!p.ExistsInWorldAt(t) || !p.TargetableAt(t) || p.DeadAt(t) || p.AllyAt(t))
                    continue;
                var hp = p.HPMPAt(t);
                if (hp.MaxHP == 0 || hp.CurHP == 0)
                    continue;
                targets.Add(new(p.InstanceID, p.OID, hp.CurHP, hp.MaxHP, pull.BossOIDs.Contains(p.OID)));
            }
            if (targets.Count == 0)
                ++noFeed;
            maxFeed = Math.Max(maxFeed, targets.Count);
            frames.Add(new(k * GridSeconds, targets));
        }
        rec.Frames = frames;
        rec.NEnemies = enemies.Count;
        rec.MaxFeed = maxFeed;
        rec.NoFeedFrames = noFeed;
        KillRule.Apply(rec);
        return rec;
    }
}

// Kill rule (see report.md): a pull is a kill when every boss-sized enemy is at zero HP / dead at its end, or the duty victory update
// arrives within a few seconds of its end (alliance fights end by script with the boss above 0 HP), and no wipe update came first.
// KillTime is when the feed (hostile, targetable, living enemies) goes empty for good, which is when a rotation loses its target.
internal static class KillRule
{
    public static void Apply(PullRecord rec)
    {
        rec.KillTime = -1;
        rec.KillBasis = "none";
        var victory = rec.VictoryT >= 0 && rec.VictoryT >= rec.Duration - 2f && rec.VictoryT <= rec.Duration + 10f;
        var zero = rec.AllBossesZero && rec.BossZeroT > 0;
        if (!victory && !zero)
            return;
        if (rec.WipeT >= 0 && !victory && rec.WipeT < rec.BossZeroT)
            return;
        var lastNonEmpty = -1;
        for (var i = rec.Frames.Count - 1; i >= 0; --i)
            if (rec.Frames[i].Targets.Count > 0) { lastNonEmpty = i; break; }
        if (lastNonEmpty < 0)
            return;
        var feedEnd = rec.Frames[lastNonEmpty].T + Extract.GridSeconds;
        if (lastNonEmpty == rec.Frames.Count - 1) // the feed was still alive when the record ended: use the marker
            feedEnd = zero ? Math.Max(feedEnd, rec.BossZeroT) : Math.Max(feedEnd, rec.VictoryT);
        rec.KillTime = feedEnd;
        rec.KillBasis = zero ? "boss_zero" : "victory";
    }
}
