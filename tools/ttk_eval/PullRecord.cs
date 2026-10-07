using System.Globalization;

// One pull as the evaluator sees it. Binary format (.ttk): see WriteFile/ReadFile.
internal sealed class PullRecord
{
    public readonly record struct Target(ulong ID, uint OID, uint CurHP, uint MaxHP, bool Boss);
    public sealed record Frame(float T, List<Target> Targets);

    public string Replay = "";
    public int Index;
    public ushort Zone;
    public DateTime Start;
    public float Duration;
    public bool Module;
    public List<uint> BossOIDs = [];
    public uint CFC;
    public string CFCName = "";
    public uint ContentType;
    public bool HighEnd;
    public uint Members;
    public float VictoryT = -1;
    public float WipeT = -1;
    public string DuIdsEnd = "";
    public float BossLastHPPct;
    public float BossZeroT = -1;
    public float BossDeadT = -1;
    public float KillCandidateT = -1;
    public bool AllBossesZero;
    public int NEnemies;
    public int MaxFeed;
    public int NoFeedFrames;
    public float KillTime = -1; // seconds from pull start, -1 when not a kill
    public string KillBasis = "none";
    public List<Frame> Frames = [];

    public string Key => string.Join(",", BossOIDs.Order()); // boss set within a zone
    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Q(string s) => "\"" + s.Replace('"', '\'') + "\"";

    public string SurveyRow() => string.Join(",", [
        Q(Replay), Index.ToString(), Zone.ToString(), CFC.ToString(), Q(CFCName), ContentType.ToString(), HighEnd ? "1" : "0", Members.ToString(),
        Q(string.Join("|", BossOIDs.Select(o => o.ToString("X")))), Module ? "1" : "0", F(Duration), NEnemies.ToString(), MaxFeed.ToString(),
        F(BossLastHPPct), F(BossZeroT), F(BossDeadT), F(KillTime), KillBasis, F(VictoryT), F(WipeT), Q(DuIdsEnd), NoFeedFrames.ToString()]);

    public static void WriteFile(string path, List<PullRecord> records)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x31545454); // "TTT1"
        w.Write(records.Count);
        foreach (var r in records)
        {
            w.Write(r.Replay); w.Write(r.Index); w.Write(r.Zone); w.Write(r.Start.Ticks); w.Write(r.Duration); w.Write(r.Module);
            w.Write(r.BossOIDs.Count);
            foreach (var o in r.BossOIDs) w.Write(o);
            w.Write(r.CFC); w.Write(r.CFCName); w.Write(r.ContentType); w.Write(r.HighEnd); w.Write(r.Members);
            w.Write(r.VictoryT); w.Write(r.WipeT); w.Write(r.DuIdsEnd); w.Write(r.BossLastHPPct); w.Write(r.BossZeroT); w.Write(r.BossDeadT);
            w.Write(r.KillCandidateT); w.Write(r.AllBossesZero); w.Write(r.NEnemies); w.Write(r.MaxFeed); w.Write(r.NoFeedFrames);
            w.Write(r.KillTime); w.Write(r.KillBasis);
            w.Write(r.Frames.Count);
            foreach (var f in r.Frames)
            {
                w.Write(f.T); w.Write((byte)Math.Min(255, f.Targets.Count));
                foreach (var t in f.Targets.Take(255)) { w.Write(t.ID); w.Write(t.OID); w.Write(t.CurHP); w.Write(t.MaxHP); w.Write(t.Boss); }
            }
        }
    }

    public static List<PullRecord> ReadFile(string path)
    {
        using var r = new BinaryReader(File.OpenRead(path));
        if (r.ReadInt32() != 0x31545454)
            throw new InvalidDataException(path);
        var count = r.ReadInt32();
        List<PullRecord> result = [];
        for (var i = 0; i < count; ++i)
        {
            var p = new PullRecord { Replay = r.ReadString(), Index = r.ReadInt32(), Zone = r.ReadUInt16(), Start = new(r.ReadInt64()), Duration = r.ReadSingle(), Module = r.ReadBoolean() };
            var nb = r.ReadInt32();
            for (var j = 0; j < nb; ++j) p.BossOIDs.Add(r.ReadUInt32());
            p.CFC = r.ReadUInt32(); p.CFCName = r.ReadString(); p.ContentType = r.ReadUInt32(); p.HighEnd = r.ReadBoolean(); p.Members = r.ReadUInt32();
            p.VictoryT = r.ReadSingle(); p.WipeT = r.ReadSingle(); p.DuIdsEnd = r.ReadString(); p.BossLastHPPct = r.ReadSingle(); p.BossZeroT = r.ReadSingle(); p.BossDeadT = r.ReadSingle();
            p.KillCandidateT = r.ReadSingle(); p.AllBossesZero = r.ReadBoolean(); p.NEnemies = r.ReadInt32(); p.MaxFeed = r.ReadInt32(); p.NoFeedFrames = r.ReadInt32();
            p.KillTime = r.ReadSingle(); p.KillBasis = r.ReadString();
            var nf = r.ReadInt32();
            p.Frames = new(nf);
            for (var k = 0; k < nf; ++k)
            {
                var t = r.ReadSingle();
                var nt = r.ReadByte();
                var targets = new List<Target>(nt);
                for (var j = 0; j < nt; ++j) targets.Add(new(r.ReadUInt64(), r.ReadUInt32(), r.ReadUInt32(), r.ReadUInt32(), r.ReadBoolean()));
                p.Frames.Add(new(t, targets));
            }
            result.Add(p);
        }
        return result;
    }
}
