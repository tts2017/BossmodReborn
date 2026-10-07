using System.Diagnostics;
using System.Reflection;
using BossMod;
using BossMod.Autorotation;
using BossMod.Pathfinding;
using Dalamud.Plugin.Services;

// Replays a BMR replay log headlessly and times each stage that Plugin.DrawUI runs per frame.
// usage: ReplayProfiler <replay.log> [--config <BossModReborn.json>] [--autorot <dir>] [--preset <name>] [--nav off|par|seq] [--from <sec>] [--to <sec>] [--record <dir>]
internal static class Program
{
    private const int NStages = 8;
    private static readonly string[] StageNames = ["ws.ops", "bmm.Update", "zone.Update", "hints.Update", "riskBorder", "rotation", "nav(wall)", "nav(cpu)"];

    private static int Main(string[] args)
    {
        var replayPath = args[0];
        string? configPath = null, autorotDir = null, presetName = null, recordDir = null;
        var nav = "par";
        float from = 0, to = float.MaxValue;
        for (var i = 1; i < args.Length; ++i)
        {
            switch (args[i])
            {
                case "--config": configPath = args[++i]; break;
                case "--autorot": autorotDir = args[++i]; break;
                case "--preset": presetName = args[++i]; break;
                case "--nav": nav = args[++i]; break;
                case "--from": from = float.Parse(args[++i]); break;
                case "--to": to = float.Parse(args[++i]); break;
                case "--record": recordDir = args[++i]; break;
            }
        }

        Service.LuminaGameData = new Lumina.GameData(@"C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
        Service.Config.Initialize();
        if (configPath != null)
            Service.Config.LoadFromFile(new FileInfo(configPath));
        var scannerProperty = typeof(Service).GetProperty(nameof(Service.SigScanner), BindingFlags.Public | BindingFlags.Static)!;
        scannerProperty.SetValue(null, DispatchProxy.Create<ISigScanner, NullSigScanner>());
        var pi = DispatchProxy.Create<Dalamud.Plugin.IDalamudPluginInterface, FakePluginInterface>();
        typeof(Service).GetProperty(nameof(Service.PluginInterface), BindingFlags.Public | BindingFlags.Static)!.SetValue(null, pi);
        var logCount = 0;
        Service.LogHandlerDebug = _ => ++logCount;
        Service.LogHandlerVerbose = _ => { };

        var sw = Stopwatch.StartNew();
        var progress = 0f;
        var replay = ReplayParserLog.Parse(replayPath, ref progress, CancellationToken.None);
        Console.WriteLine($"parsed {replay.Ops.Count} ops in {sw.Elapsed.TotalSeconds:f1}s");

        var player = new ReplayPlayer(replay);
        var ws = player.WorldState;
        if (args.Contains("--ingame"))
            return InGame(player);
        var hints = new AIHints();
        var bmm = new BossModuleManager(ws);
        var zmm = new ZoneModuleManager(ws);
        var hb = new AIHintsBuilder(ws, bmm, zmm, null);
        RotationModuleManager? rmm = null;
        Preset? preset = null;
        if (autorotDir != null)
        {
            var bmrDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "BossMod"));
            var db = new RotationDatabase(new DirectoryInfo(autorotDir), new FileInfo(bmrDir + "/RebornPresets.json"), new FileInfo(bmrDir + "/DefaultRotationPresets.json"));
            rmm = new RotationModuleManager(db, bmm, hints);
            if (presetName != null)
            {
                preset = db.Presets.FindPresetByName(presetName);
                Console.WriteLine($"preset '{presetName}' -> {(preset != null ? "found" : "MISSING")}");
            }
        }
        ReplayRecorder? recorder = null;
        var naviCtx = new NavigationDecision.Context();
        var nextNav = DateTime.MinValue;

        var frameTimes = new List<double>[NStages];
        for (var i = 0; i < NStages; ++i)
            frameTimes[i] = [];
        var totals = new List<(double total, DateTime t, string module, int fz, int players, int enemies)>();
        var perModule = new Dictionary<string, (int frames, double[] sum)>();
        var disabled = new bool[NStages];
        var start = replay.Ops.Count > 0 ? replay.Ops[0].Timestamp : default;
        var proc = Process.GetCurrentProcess();
        var t = new double[NStages];
        var tickToMs = 1000.0 / Stopwatch.Frequency;
        int frames = 0, navRuns = 0;

        while (true)
        {
            var nextTs = player.NextTimestamp();
            if (nextTs == default)
                break;
            var rel = (float)(nextTs - start).TotalSeconds;
            var measure = rel >= from && rel <= to;
            if (rel > to)
                break;

            Array.Clear(t);
            var s0 = Stopwatch.GetTimestamp();
            player.TickForward();
            t[0] = (Stopwatch.GetTimestamp() - s0) * tickToMs;
            if (recordDir != null && recorder == null && ws.CurrentZone != 0)
                recorder = new ReplayRecorder(ws, ReplayLogFormat.TextCondensed, true, new DirectoryInfo(recordDir), "prof", false);

            Stage(1, () => bmm.Update());
            Stage(2, () => zmm.ActiveModule?.Update());
            Stage(3, () => hb.Update(hints, PartyState.PlayerSlot, false));
            var pc = ws.Party[PartyState.PlayerSlot];
            Stage(4, () =>
            {
                var m = bmm.ActiveModule;
                if (m != null && pc != null && !pc.IsDead)
                    m.CalculateHintsForRaidMember(PartyState.PlayerSlot, pc);
            });
            if (rmm != null)
            {
                Stage(5, () =>
                {
                    if (preset != null && pc != null && pc.InCombat)
                        rmm.Preset = preset;
                    rmm.Update(0.1f, false, false);
                });
            }
            if (nav != "off" && pc != null && !disabled[6] && (ws.CurrentTime >= nextNav || hints.ForbiddenZones.Count != 0))
            {
                nextNav = ws.CurrentTime.AddMilliseconds(100);
                var cpu0 = proc.TotalProcessorTime;
                var n0 = Stopwatch.GetTimestamp();
                try
                {
                    NavigationDecision.Build(naviCtx, ws.CurrentTime, hints, pc, ws.Client.MoveSpeed > 0 ? ws.Client.MoveSpeed : 6f, 0, nav == "seq");
                    ++navRuns;
                }
                catch (Exception e)
                {
                    Console.WriteLine($"nav disabled: {e.Message}");
                    disabled[6] = true;
                }
                t[6] = (Stopwatch.GetTimestamp() - n0) * tickToMs;
                proc.Refresh();
                t[7] = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
            }

            if (!measure)
                continue;
            ++frames;
            var total = 0.0;
            for (var i = 0; i < NStages; ++i)
            {
                frameTimes[i].Add(t[i]);
                if (i != 7)
                    total += t[i];
            }
            var modName = bmm.ActiveModule?.GetType().Name ?? (zmm.ActiveModule?.GetType().Name ?? "<none>");
            if (bmm.ActiveModule != null && bmm.ActiveModule.StateMachine.ActivePhase == null)
                modName += "(idle)";
            var enemies = 0;
            foreach (var a in ws.Actors)
                if (a.Type == ActorType.Enemy && a.IsTargetable && !a.IsDead)
                    ++enemies;
            totals.Add((total, ws.CurrentTime, modName, hints.ForbiddenZones.Count, ws.Party.WithoutSlot(false, true, true).Length, enemies));
            if (!perModule.TryGetValue(modName, out var pm))
                pm = (0, new double[NStages]);
            pm.frames++;
            for (var i = 0; i < NStages; ++i)
                pm.sum[i] += t[i];
            perModule[modName] = pm;
        }

        recorder?.Dispose();
        Console.WriteLine($"frames={frames} navRuns={navRuns} logLines={logCount} gcCount0={GC.CollectionCount(0)} gc1={GC.CollectionCount(1)} gc2={GC.CollectionCount(2)} alloc={GC.GetTotalAllocatedBytes() / 1048576.0:f0}MB");
        Console.WriteLine($"{"stage",-14}{"mean",9}{"p50",9}{"p95",9}{"p99",9}{"max",9}  (ms/frame)");
        for (var i = 0; i < NStages; ++i)
        {
            var arr = frameTimes[i].ToArray();
            if (arr.Length == 0)
                continue;
            Array.Sort(arr);
            Console.WriteLine($"{StageNames[i],-14}{arr.Average(),9:f3}{P(arr, .5),9:f3}{P(arr, .95),9:f3}{P(arr, .99),9:f3}{arr[^1],9:f2}");
        }
        var tot = totals.Select(x => x.total).ToArray();
        Array.Sort(tot);
        Console.WriteLine($"{"TOTAL(wall)",-14}{tot.Average(),9:f3}{P(tot, .5),9:f3}{P(tot, .95),9:f3}{P(tot, .99),9:f3}{tot[^1],9:f2}");

        Console.WriteLine("\nper module (mean ms/frame):");
        Console.WriteLine($"{"module",-36}{"frames",8}" + string.Concat(StageNames.Select(n => $"{n,12}")));
        foreach (var (name, pm) in perModule.OrderByDescending(kv => kv.Value.frames))
            Console.WriteLine($"{name,-36}{pm.frames,8}" + string.Concat(pm.sum.Select(s => $"{s / pm.frames,12:f3}")));

        Console.WriteLine("\nworst frames:");
        foreach (var f in totals.OrderByDescending(x => x.total).Take(15))
            Console.WriteLine($"  {f.total,8:f2}ms t={(f.t - start).TotalSeconds,8:f1}s {f.module} fz={f.fz} party={f.players} enemies={f.enemies}");
        return 0;

        void Stage(int idx, Action a)
        {
            if (disabled[idx])
                return;
            var s = Stopwatch.GetTimestamp();
            try
            {
                a();
            }
            catch (Exception e)
            {
                Console.WriteLine($"stage {StageNames[idx]} disabled: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                disabled[idx] = true;
            }
            t[idx] = (Stopwatch.GetTimestamp() - s) * tickToMs;
        }
    }

    private static double P(double[] sorted, double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];

    // reads the in-game measurements stored in the replay: BMR's own DrawUI duration (prev frame) and the real frame duration
    private static int InGame(ReplayPlayer player)
    {
        var ws = player.WorldState;
        var bmm = new BossModuleManager(ws);
        var samples = new List<(double bmr, double frame, string key)>();
        var spikes = new List<string>();
        var start = player.Replay.Ops[0].Timestamp;
        var prevOps = new List<string>();
        var curOps = new List<string>();
        ws.Modified.Subscribe(op =>
        {
            if (op is WorldState.OpFrameStart)
            {
                (prevOps, curOps) = (curOps, prevOps);
                curOps.Clear();
            }
            else if (op is ActorState.OpCastInfo or ActorState.OpCreate or ActorState.OpEventState or WorldState.OpMapEffect or WorldState.OpDirectorUpdate or ActorState.OpStatus or ActorState.OpIcon or ActorState.OpTether or ActorState.OpEventObjectAnimation)
                curOps.Add(op.GetType().Name + (op is ActorState.OpCastInfo ci ? $"({ci.Value?.Action})" : op is ActorState.OpCreate cr ? $"({cr.OID:X}/{cr.Type})" : ""));
        });
        ws.FrameStarted.Subscribe(op =>
        {
            var pc = ws.Party.Player();
            var mod = bmm.ActiveModule;
            var key = mod == null ? (pc?.InCombat == true ? "nomodule-combat" : "nomodule-idle") : mod.GetType().Name + (mod.StateMachine.ActivePhase != null ? "" : "(idle)");
            samples.Add((op.PrevUpdateTime.TotalMilliseconds, op.Frame.DurationRaw * 1000.0, key));
            if (op.PrevUpdateTime.TotalMilliseconds > 15)
                spikes.Add($"  {op.PrevUpdateTime.TotalMilliseconds,7:f1}ms t={(ws.CurrentTime - start).TotalSeconds,7:f1}s {key} state='{mod?.StateMachine.ActiveState?.Name}' prevFrameOps=[{string.Join(", ", prevOps.GroupBy(x => x).Select(g => g.Count() > 1 ? $"{g.Key}x{g.Count()}" : g.Key).Take(12))}]");
        });
        while (player.TickForward())
            bmm.Update();
        Console.WriteLine($"spikes >15ms: {spikes.Count}");
        foreach (var s in spikes.Take(40))
            Console.WriteLine(s);
        void Report(string name, IEnumerable<(double bmr, double frame, string key)> src)
        {
            var arr = src.ToArray();
            if (arr.Length == 0)
                return;
            var b = arr.Select(x => x.bmr).OrderBy(x => x).ToArray();
            var f = arr.Select(x => x.frame).OrderBy(x => x).ToArray();
            var share = b.Sum() / f.Sum() * 100;
            Console.WriteLine($"{name,-30}{arr.Length,8}  bmr mean={b.Average(),6:f2} p50={P(b, .5),6:f2} p95={P(b, .95),6:f2} p99={P(b, .99),6:f2} max={b[^1],7:f1}ms | frame mean={f.Average(),6:f2}ms ({1000 / f.Average(),5:f1}fps) p95={P(f, .95),6:f2} | bmr share={share,5:f1}%");
        }
        Report("ALL", samples);
        foreach (var g in samples.GroupBy(s => s.key).OrderByDescending(g => g.Count()))
            Report(g.Key, g);
        return 0;
    }

    public class FakePluginInterface : DispatchProxy
    {
        public static readonly DirectoryInfo ConfigDir = new(Path.Combine(AppContext.BaseDirectory, "fakecfg"));
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == "get_ConfigDirectory")
                return ConfigDir;
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    private class NullSigScanner : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(void))
                return null;
            if (targetMethod?.ReturnType == typeof(IntPtr))
                return IntPtr.Zero;
            if (targetMethod?.ReturnType == typeof(bool))
                return false;
            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }
}
