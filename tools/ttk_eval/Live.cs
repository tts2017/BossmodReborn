using System.Reflection;
using BossMod;
using Dalamud.Plugin.Services;

// End-to-end check of the plugin path: a replay is played through WorldState -> AIHintsBuilder (which feeds the estimator from the priority
// targets and publishes AIHints.FightRemaining) and the published value is compared with the offline evaluation of the same pulls
// (feed rebuilt from the replay's participants, no prior).
//   TtkEval live <replay.log>... [--csv out.csv]
internal static class Live
{
    private readonly record struct Frame(DateTime Time, bool Known, float Remaining);

    public static int Run(string[] args)
    {
        var files = new List<string>();
        string? csv = null;
        for (var i = 1; i < args.Length; ++i)
        {
            if (args[i] == "--csv") csv = args[++i];
            else files.Add(args[i]);
        }
        Service.LuminaGameData = new Lumina.GameData(@"C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
        Service.Config.Initialize();
        typeof(Service).GetProperty(nameof(Service.SigScanner), BindingFlags.Public | BindingFlags.Static)!.SetValue(null, DispatchProxy.Create<ISigScanner, NullProxy>());
        typeof(Service).GetProperty(nameof(Service.PluginInterface), BindingFlags.Public | BindingFlags.Static)!.SetValue(null, DispatchProxy.Create<Dalamud.Plugin.IDalamudPluginInterface, FakePluginInterface>());
        Service.LogHandlerDebug = _ => { };
        Service.LogHandlerVerbose = _ => { };

        var lines = new List<string> { "replay,pull,t,truth,live_known,live_est,off_known,off_est" };
        var liveErr = new List<double>();
        var offErr = new List<double>();
        var agree = new List<double>();
        int n = 0, liveKnown = 0, offKnown = 0;
        foreach (var file in files)
        {
            var progress = 0f;
            var replay = ReplayParserLog.Parse(file, ref progress, CancellationToken.None);
            var name = Path.GetFileName(file);
            var pulls = ReplayTimelineExtractor.FindPulls(replay);
            var recs = new List<PullRecord>();
            for (var i = 0; i < pulls.Count; ++i)
                if (Extract.Analyze(replay, pulls[i], name, i) is { KillTime: > 0 } rec)
                    recs.Add(rec);

            var player = new ReplayPlayer(replay);
            var ws = player.WorldState;
            var hints = new AIHints();
            var bmm = new BossModuleManager(ws);
            var zmm = new ZoneModuleManager(ws);
            var hb = new AIHintsBuilder(ws, bmm, zmm, null);
            var series = new List<Frame>();
            while (player.TickForward())
            {
                bmm.Update();
                zmm.ActiveModule?.Update();
                hb.Update(hints, PartyState.PlayerSlot, false);
                series.Add(new(ws.CurrentTime, hints.FightRemaining.Known, hints.FightRemaining.RemainingSeconds));
            }

            var data = new Eval.Data { Kills = recs, Class = recs.Select(Eval.ClassOf).ToArray() };
            var rows = Eval.SimulateAll(data, new Eval.ModelSpec("live_ref", FightTimeConfig.Recommended), 20, 5, out _);
            foreach (var r in rows)
            {
                var rec = recs[r.Pull];
                var at = rec.Start.AddSeconds(r.T);
                var idx = series.FindLastIndex(f => f.Time <= at); // linear, fine for a diagnostic
                var live = idx >= 0 ? series[idx] : default;
                ++n;
                if (live.Known) ++liveKnown;
                if (r.Known) ++offKnown;
                if (live.Known) liveErr.Add(Math.Abs(live.Remaining - r.Truth));
                if (r.Known) offErr.Add(Math.Abs(r.Est - r.Truth));
                if (live.Known && r.Known) agree.Add(Math.Abs(Math.Log(live.Remaining / r.Est)));
                lines.Add($"{name},{rec.Index},{r.T:0.#},{r.Truth:0.#},{(live.Known ? 1 : 0)},{(live.Known ? live.Remaining.ToString("0.#") : "")},{(r.Known ? 1 : 0)},{(r.Known ? r.Est.ToString("0.#") : "")}");
            }
            Console.WriteLine($"{name}: kill pulls={recs.Count} frames={series.Count}");
        }
        static double Med(List<double> v) { if (v.Count == 0) return double.NaN; var s = v.Order().ToList(); return s[s.Count / 2]; }
        Console.WriteLine($"samples={n} live known={liveKnown / (double)n:P1} offline known={offKnown / (double)n:P1}");
        Console.WriteLine($"median |err| live={Med(liveErr):f2}s offline={Med(offErr):f2}s; both known: median |ln(live/offline)|={Med(agree):f3} (n={agree.Count}), within 10%: {agree.Count(a => a < 0.095) / (double)Math.Max(1, agree.Count):P0}, within 25%: {agree.Count(a => a < 0.223) / (double)Math.Max(1, agree.Count):P0}");
        if (csv != null)
            File.WriteAllLines(csv, lines);
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

    public class NullProxy : DispatchProxy
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
