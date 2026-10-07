using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using BossMod;
using AID = BossMod.PLD.AID;
using SID = BossMod.PLD.SID;

namespace XanTimelineHarness;

// pld-replay-check <replay.log | directory>...: runs the Paladin emulator in the shadow of other players' logged Paladin actions and compares
// what it predicts with what the game did. For every Paladin in the logs a private emulator world receives their casts at the time the
// effect resolved (a hard cast at its end), and every status gain / loss, every MP change and every press is checked against the emulator:
//   action      the emulator would have refused the press (a status, gauge step or MP the log shows was there; a cooldown not yet back)
//   gain/lose   a logged status change of the modeled statuses (Fight or Flight, Goring Blade Ready, Requiescat and its stacks, Confiteor
//               Ready, Atonement / Supplication / Sepulchre Ready, Divine Might, Blade of Honor Ready) that the emulator does not agree with.
//               A loss logged together with at least 3 other statuses of the player is a death / zone change, counted apart.
//   predicted   a status change the emulator makes that the log never shows (within 1 s)
//   mp          a logged MP value that the emulator's MP does not take at any moment within 0.2 s of it (mp-exact), and the same with differences
//               of one regeneration tick (200) set aside (mp). The emulator's tick uses the phase the log's own +200 steps show (the server tick is
//               not aligned with the pull); costs and restores are the emulator's.
//   cast-time   a logged Holy Spirit / Holy Circle that was hard-cast although the emulator had Divine Might or Requiescat (or the reverse)
// The first 35 s of each player are not compared (statuses that began before the log did), and the emulator starts from the first logged MP.
// The log is read once into per-player event lists (the tick phase needs all of a player's MP changes), then each list is replayed.
internal static class PldReplayCheck
{
    private static readonly SID[] Modeled = [SID.FightOrFlight, SID.GoringBladeReady, SID.Requiescat, SID.ConfiteorReady, SID.AtonementReady, SID.SupplicationReady, SID.SepulchreReady, SID.DivineMight, SID.BladeOfHonorReady];
    private static readonly HashSet<AID> CooldownChecked = [AID.FightOrFlight, AID.Imperator, AID.Requiescat, AID.SpiritsWithin, AID.Expiacion, AID.CircleOfScorn, AID.Intervene];
    private const float WarmUp = 35f;
    private const float MpWindow = 0.2f;
    private const float PredictedWindow = 1.0f;
    private const float MassLossWindow = 0.3f;
    private const ulong PlayerID = 0x10000001;
    private const ulong TargetID = 0x40000001;

    private enum Kind { Cast, Gain, Lose, Mp, Combat, AnyLose }

    private readonly record struct Ev(DateTime At, Kind Kind, int A, int B = 0, bool Flag = false);

    private sealed class Tally
    {
        public readonly SortedDictionary<string, (long Checks, long Mismatches)> Counts = [];
        public readonly SortedDictionary<string, List<string>> Examples = [];

        public void Add(string key, bool mismatch, string? example = null)
        {
            Counts.TryGetValue(key, out var c);
            Counts[key] = (c.Checks + 1, c.Mismatches + (mismatch ? 1 : 0));
            if (mismatch && example != null)
            {
                if (!Examples.TryGetValue(key, out var list))
                    Examples[key] = list = [];
                if (list.Count < 3)
                    list.Add(example);
            }
        }

        public void Merge(Tally other)
        {
            foreach (var (key, (checks, mismatches)) in other.Counts)
            {
                Counts.TryGetValue(key, out var c);
                Counts[key] = (c.Checks + checks, c.Mismatches + mismatches);
            }
            foreach (var (key, list) in other.Examples)
            {
                if (!Examples.TryGetValue(key, out var mine))
                    Examples[key] = mine = [];
                foreach (var example in list)
                    if (mine.Count < 3)
                        mine.Add(example);
            }
        }
    }

    private sealed class PlayerLog(string name, int level)
    {
        public readonly string Name = name;
        public readonly int Level = level;
        public readonly List<Ev> Events = [];
    }

    private sealed class Shadow
    {
        public readonly WorldState World;
        public readonly Actor Player;
        public readonly Actor Target;
        public readonly PldCombatState Emulator;
        public readonly DateTime Start;
        public readonly DateTime ReplayStart;
        public readonly string Name;
        public readonly Tally Tally = new();
        public readonly List<(DateTime At, SID Status, bool Gain, int Stacks, bool Matched)> Predicted = [];
        public readonly List<(DateTime At, int Mp)> MpTrace = [];
        public readonly List<(DateTime At, int Mp)> Pending = [];
        public readonly List<(DateTime At, SID Status, bool Held, bool Matched)> Losses = [];
        public readonly List<DateTime> AnyLosses = [];
        public ulong Frame;
        public DateTime Now;
        public int Presses;

        public Shadow(string name, DateTime replayStart, DateTime start, int level, int firstMp, DateTime regenAnchor)
        {
            Name = name;
            ReplayStart = replayStart;
            Start = start;
            Now = start;
            World = new WorldState(TimeSpan.TicksPerSecond, "pld-replay-check");
            World.Execute(new WorldState.OpFrameStart(new(start, 0, 0, 0, 0, 1), default, default, default));
            World.Execute(new WorldState.OpZoneChange(0, 0));
            World.Execute(new ActorState.OpCreate(PlayerID, 0, 0, 0, "Player", 0, ActorType.Player, Class.PLD, (byte)level, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
            World.Execute(new ActorState.OpCreate(TargetID, 0x1234, 2, 0, "Target", 0, ActorType.Enemy, Class.None, 100, new Vector4(2.5f, 0, 0, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), true, false, default, default, 0));
            World.Execute(new ActorState.OpCombat(PlayerID, true));
            World.Execute(new ActorState.OpCombat(TargetID, true));
            World.Execute(new ClientState.OpPlayerStatsChange(new(400, 400, 100)));
            var levels = new short[ClientState.NumClassLevels];
            Array.Fill(levels, (short)level);
            World.Execute(new ClientState.OpClassJobLevelsChange(levels));
            World.Execute(new ClientState.OpComboChange(default));
            World.Execute(new ClientState.OpCooldown(true, []));
            Player = World.Actors.Find(PlayerID)!;
            Target = World.Actors.Find(TargetID)!;
            Emulator = new PldCombatState(World, Player, 0.05f, (_, _, _, _) => { })
            {
                BaseTime = start,
                StatusTrace = [],
                MpTrace = MpTrace,
                RegenAnchor = regenAnchor
            };
            Emulator.InitMp(firstMp);
        }

        // monotonic: the emulator world only moves forward, in one step of the real elapsed time (cooldowns and statuses follow it)
        public void AdvanceTo(DateTime time)
        {
            if (time <= Now)
                return;
            var elapsed = (float)(time - Now).TotalSeconds;
            ++Frame;
            World.Execute(new WorldState.OpFrameStart(new(time, Frame, (uint)Frame, elapsed, elapsed, 1), TimeSpan.FromSeconds(elapsed), World.Client.GaugePayload, default));
            Now = time;
            Emulator.Advance();
            Harvest();
        }

        // what the emulator did to statuses since the last call, kept for the predicted-versus-logged matching
        public void Harvest()
        {
            var trace = Emulator.StatusTrace!;
            foreach (var entry in trace)
            {
                // the log can show a loss up to 0.3 s before the action that causes it: match it to a logged loss that is already there
                var matched = false;
                if (!entry.Gain)
                    for (var i = Losses.Count - 1; i >= 0 && !matched; --i)
                        if (Losses[i].Status == entry.Status && !Losses[i].Matched && Math.Abs((Losses[i].At - entry.At).TotalSeconds) <= 0.3)
                        {
                            Losses[i] = Losses[i] with { Matched = true };
                            matched = true;
                        }
                Predicted.Add((entry.At, entry.Status, entry.Gain, entry.Stacks, matched));
            }
            trace.Clear();
        }

        public bool Counting(DateTime at) => (at - Start).TotalSeconds >= WarmUp;
        public double LogT(DateTime at) => (at - ReplayStart).TotalSeconds;
    }

    public static int Run(List<string> paths)
    {
        var total = new Tally();
        var players = 0;
        var presses = 0;
        var verbose = Environment.GetEnvironmentVariable("PLD_CHECK_VERBOSE") == "1";
        foreach (var path in paths)
        {
            try
            {
                var (n, p, tally, lines) = CheckReplay(path);
                players += n;
                presses += p;
                total.Merge(tally);
                if (verbose)
                    foreach (var line in lines)
                        Console.WriteLine(line);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{path}: {ex.Message}");
            }
        }

        Console.WriteLine($"pld_replay_check files={paths.Count} players={players} presses={presses}");
        foreach (var (key, (checks, mismatches)) in total.Counts)
        {
            Console.WriteLine(FormattableString.Invariant($"pld_check {key} checks={checks} mismatches={mismatches} rate={(checks > 0 ? (double)mismatches / checks : 0):f4}"));
            if (total.Examples.TryGetValue(key, out var examples))
                foreach (var example in examples)
                    Console.WriteLine($"    e.g. {example}");
        }
        return 0;
    }

    private static (int Players, int Presses, Tally Tally, List<string> Lines) CheckReplay(string path)
    {
        var progress = 0f;
        var replay = ReplayParserLog.Parse(path, ref progress, CancellationToken.None);
        if (replay.Ops.Count == 0)
            return (0, 0, new(), []);
        var name = Path.GetFileNameWithoutExtension(path);
        var replayStart = replay.Ops[0].Timestamp;
        var player = new ReplayPlayer(replay);
        var ws = player.WorldState;
        Dictionary<ulong, PlayerLog> logs = [];
        Dictionary<ulong, (AID Action, DateTime At)> castStarts = [];

        bool IsPaladin(Actor actor) => actor.Type == ActorType.Player && actor.Class == Class.PLD;
        PlayerLog Log(Actor actor)
        {
            if (!logs.TryGetValue(actor.InstanceID, out var log))
                logs[actor.InstanceID] = log = new(name + ":" + actor.InstanceID.ToString("X"), actor.Level);
            return log;
        }

        ws.Actors.HPMPChanged.Subscribe(actor =>
        {
            if (IsPaladin(actor))
                Log(actor).Events.Add(new(ws.CurrentTime, Kind.Mp, (int)actor.HPMP.CurMP));
        });
        ws.Actors.InCombatChanged.Subscribe(actor =>
        {
            if (IsPaladin(actor))
                Log(actor).Events.Add(new(ws.CurrentTime, Kind.Combat, actor.InCombat ? 1 : 0));
        });
        ws.Actors.CastStarted.Subscribe(actor =>
        {
            if (IsPaladin(actor) && actor.CastInfo != null)
                castStarts[actor.InstanceID] = ((AID)actor.CastInfo.Action.ID, ws.CurrentTime);
        });
        ws.Actors.CastEvent.Subscribe((actor, ev) =>
        {
            if (!IsPaladin(actor) || ev.Action.Type != ActionType.Spell)
                return;
            var aid = (AID)ev.Action.ID;
            var hard = aid is AID.HolySpirit or AID.HolyCircle or AID.Clemency && castStarts.TryGetValue(actor.InstanceID, out var start) && start.Action == aid && (ws.CurrentTime - start.At).TotalSeconds <= 3.0;
            Log(actor).Events.Add(new(ws.CurrentTime, Kind.Cast, (int)ev.Action.ID, 0, hard));
        });
        ws.Actors.StatusGain.Subscribe((actor, index) =>
        {
            var s = actor.Statuses[index];
            if (IsPaladin(actor) && s.SourceID == actor.InstanceID && Modeled.Contains((SID)s.ID))
                Log(actor).Events.Add(new(ws.CurrentTime, Kind.Gain, (int)s.ID, s.Extra & 0xFF));
        });
        ws.Actors.StatusLose.Subscribe((actor, index) =>
        {
            var s = actor.Statuses[index];
            if (!IsPaladin(actor))
                return;
            if (s.SourceID == actor.InstanceID && Modeled.Contains((SID)s.ID))
                Log(actor).Events.Add(new(ws.CurrentTime, Kind.Lose, (int)s.ID));
            Log(actor).Events.Add(new(ws.CurrentTime, Kind.AnyLose, (int)s.ID));
        });
        while (player.TickForward())
        {
        }

        var tally = new Tally();
        var presses = 0;
        var lines = new List<string>();
        var count = 0;
        foreach (var log in logs.Values)
        {
            if (log.Events.Count(e => e.Kind == Kind.Cast) < 50 || !log.Events.Any(e => e.Kind == Kind.Mp))
                continue;
            var shadow = Evaluate(log, replayStart);
            tally.Merge(shadow.Tally);
            presses += shadow.Presses;
            ++count;
            lines.Add(FormattableString.Invariant($"player {shadow.Name} presses={shadow.Presses} mismatches={shadow.Tally.Counts.Where(kv => !kv.Key.StartsWith("mp", StringComparison.Ordinal) && !kv.Key.StartsWith("mp-diff", StringComparison.Ordinal)).Sum(kv => kv.Value.Mismatches)}"));
        }
        return (count, presses, tally, lines);
    }

    // phase of the server's MP regeneration tick (every 3.0 s): the mode of the log times (mod 3) of the +200 steps, refined by the mean around it
    private static double TickPhase(PlayerLog log, DateTime origin)
    {
        var bins = new int[150];
        var steps = new List<double>();
        int? previous = null;
        foreach (var e in log.Events.Where(e => e.Kind == Kind.Mp))
        {
            if (previous is { } p && e.A - p == 200)
            {
                var phase = (e.At - origin).TotalSeconds % 3.0;
                steps.Add(phase);
                ++bins[Math.Min(149, (int)(phase / 0.02))];
            }
            previous = e.A;
        }
        if (steps.Count == 0)
            return 0;
        var best = Array.IndexOf(bins, bins.Max());
        var center = (best + 0.5) * 0.02;
        var near = steps.Where(x => Math.Abs(x - center) < 0.1 || Math.Abs(x - center) > 2.9).ToList();
        return near.Count > 0 ? near.Average(x => x > center + 1.5 ? x - 3.0 : x) : center;
    }

    private static Shadow Evaluate(PlayerLog log, DateTime replayStart)
    {
        var first = log.Events[0].At;
        var firstMp = log.Events.First(e => e.Kind == Kind.Mp).A;
        var phase = TickPhase(log, replayStart);
        // the last tick before the first event, so that the next one is the first tick the emulator takes
        var offset = ((first - replayStart).TotalSeconds - phase) % 3.0;
        if (offset < 0)
            offset += 3.0;
        var anchor = first.AddSeconds(-offset);
        var shadow = new Shadow(log.Name, replayStart, first, log.Level, firstMp, anchor);

        // an MP check waits until the emulator has seen everything within MpWindow after the logged change
        void Flush(DateTime now, bool final = false)
        {
            while (shadow.Pending.Count > 0 && (final || (now - shadow.Pending[0].At).TotalSeconds > MpWindow + 0.001))
            {
                var (at, mp) = shadow.Pending[0];
                shadow.Pending.RemoveAt(0);
                shadow.AdvanceTo(at.AddSeconds(MpWindow));
                if (!shadow.Counting(at))
                    continue;
                var ok = false;
                var best = int.MaxValue;
                // the emulator's MP at the start of the window and after every change inside it
                var current = shadow.MpTrace.LastOrDefault(e => e.At <= at.AddSeconds(-MpWindow)).Mp;
                if (shadow.MpTrace.All(e => e.At > at.AddSeconds(-MpWindow)))
                    current = shadow.MpTrace.Count > 0 ? shadow.MpTrace[0].Mp : mp;
                foreach (var candidate in shadow.MpTrace.Where(e => e.At > at.AddSeconds(-MpWindow) && e.At <= at.AddSeconds(MpWindow)).Select(e => e.Mp).Prepend(current))
                {
                    best = Math.Min(best, Math.Abs(candidate - mp));
                    if (candidate == mp)
                        ok = true;
                }
                // a difference of exactly one regeneration tick (200) is the order of a tick against a cost or a restore within the ~0.1 s the log
                // times of both can be off, and it then stays until the next time MP hits the cap; it is counted apart from a real difference
                shadow.Tally.Add("mp-exact", !ok, FormattableString.Invariant($"{shadow.Name} log_t={shadow.LogT(at):f2} logged={mp} nearest_diff={best}"));
                shadow.Tally.Add("mp", best >= 400 && best < 2000, FormattableString.Invariant($"{shadow.Name} log_t={shadow.LogT(at):f2} logged={mp} nearest_diff={best}"));
                if (best >= 400 && Environment.GetEnvironmentVariable("PLD_CHECK_DEBUG") == "1")
                    Console.WriteLine(FormattableString.Invariant($"mp_mismatch {shadow.Name} log_t={shadow.LogT(at):f3} logged={mp} emulator_around=[{string.Join(" ", shadow.MpTrace.Where(e => Math.Abs((e.At - at).TotalSeconds) <= 1.5).Select(e => FormattableString.Invariant($"{shadow.LogT(e.At):f2}:{e.Mp}")))}]"));
                if (best >= 400)
                    shadow.Tally.Add(FormattableString.Invariant($"mp-diff-{Math.Clamp(best, 0, 2000) / 200 * 200:D4}"), true);
                if (best >= 2000)
                {
                    // an MP change the emulator has no rule for (a death and its raise, a heal other than Clemency, a limit break): count it once and
                    // continue from the logged value instead of counting every later MP step as a mismatch
                    shadow.Emulator.InitMp(mp);
                    shadow.Tally.Add("mp-resync(unmodeled MP change)", false);
                }
            }
            if (shadow.MpTrace.Count > 400)
                shadow.MpTrace.RemoveRange(0, shadow.MpTrace.Count - 200);
        }

        foreach (var e in log.Events)
        {
            Flush(e.At);
            shadow.AdvanceTo(e.At);
            var counting = shadow.Counting(e.At);
            switch (e.Kind)
            {
                case Kind.Mp:
                    shadow.Pending.Add((e.At, e.A));
                    break;

                case Kind.Combat:
                    shadow.World.Execute(new ActorState.OpCombat(PlayerID, e.A != 0));
                    break;

                case Kind.Cast:
                {
                    var action = new ActionID(ActionType.Spell, (uint)e.A);
                    var aid = (AID)e.A;
                    if (aid is AID.HolySpirit or AID.HolyCircle or AID.Clemency && counting)
                    {
                        var needsCast = aid == AID.Clemency ? !shadow.Emulator.Holds(SID.Requiescat) : !shadow.Emulator.Holds(SID.DivineMight) && !shadow.Emulator.Holds(SID.Requiescat);
                        shadow.Tally.Add("cast-time", e.Flag != needsCast, FormattableString.Invariant($"{shadow.Name} log_t={shadow.LogT(e.At):f2} {aid} logged_hardcast={e.Flag} emulator_needs_cast={needsCast}"));
                    }
                    if (counting && IsModuleAction(aid))
                    {
                        ++shadow.Presses;
                        var reason = shadow.Emulator.RefusalReason(action);
                        // the cooldown test only applies to the oGCDs the module tracks; GCDs have no recast of their own beyond the shared one
                        if (reason == "cooldown" && !CooldownChecked.Contains(aid))
                            reason = null;
                        shadow.Tally.Add("action:" + aid, reason != null, FormattableString.Invariant($"{shadow.Name} log_t={shadow.LogT(e.At):f2} {aid} refused={reason}"));
                    }
                    shadow.Emulator.ResolveLogged(action, shadow.Target, e.Flag);
                    shadow.Harvest();
                    break;
                }

                case Kind.Gain:
                {
                    var sid = (SID)e.A;
                    for (var i = 0; i < shadow.Predicted.Count; ++i)
                    {
                        var p = shadow.Predicted[i];
                        if (!p.Matched && p.Status == sid && p.Gain && (e.At - p.At).TotalSeconds is >= -0.2 and <= PredictedWindow)
                        {
                            shadow.Predicted[i] = p with { Matched = true };
                            break;
                        }
                    }
                    if (!counting)
                        break;
                    var holds = shadow.Emulator.Holds(sid);
                    shadow.Tally.Add("gain:" + sid, !holds, FormattableString.Invariant($"{shadow.Name} log_t={shadow.LogT(e.At):f2} logged_gain {sid} emulator_holds={holds}"));
                    if (holds && sid == SID.Requiescat)
                    {
                        var emulated = shadow.Emulator.StackCount(sid);
                        shadow.Tally.Add("stacks:Requiescat", emulated != e.B, FormattableString.Invariant($"{shadow.Name} log_t={shadow.LogT(e.At):f2} logged={e.B} emulator={emulated}"));
                    }
                    break;
                }

                case Kind.Lose:
                {
                    var sid = (SID)e.A;
                    var wasPredicted = false;
                    for (var i = 0; i < shadow.Predicted.Count && !wasPredicted; ++i)
                    {
                        var p = shadow.Predicted[i];
                        if (!p.Matched && p.Status == sid && !p.Gain && (e.At - p.At).TotalSeconds is >= -1.0 and <= PredictedWindow)
                        {
                            shadow.Predicted[i] = p with { Matched = true };
                            wasPredicted = true;
                        }
                    }
                    if (counting)
                        shadow.Losses.Add((e.At, sid, shadow.Emulator.Holds(sid), wasPredicted));
                    break;
                }

                case Kind.AnyLose:
                    shadow.AnyLosses.Add(e.At);
                    break;
            }
        }
        Flush(log.Events[^1].At, final: true);
        var end = log.Events[^1].At;
        shadow.AdvanceTo(end.AddSeconds(PredictedWindow));

        // losses: three or more modeled statuses lost at once while the emulator holds two of them is a death or a zone change (the game strips
        // every status), counted apart; any other loss the emulator still holds is a mismatch
        // (the log also shows every other status of the player going at that instant: Iron Will, food, regens)
        var massWindows = new List<(DateTime From, DateTime To)>();
        var lastMass = DateTime.MinValue;
        foreach (var g in shadow.Losses)
        {
            var burst = shadow.AnyLosses.Count(a => Math.Abs((a - g.At).TotalSeconds) <= MassLossWindow);
            if (burst >= 4)
            {
                massWindows.Add((g.At.AddSeconds(-MassLossWindow), g.At.AddSeconds(MassLossWindow)));
                if ((g.At - lastMass).TotalSeconds > 1.0)
                    shadow.Tally.Add("lose:mass-removal(death/zone)", false);
                lastMass = g.At;
                continue;
            }
            // matched: the emulator removes it too (the order of the two events in the log can differ by a few tenths of a second)
            shadow.Tally.Add("lose:" + g.Status, g.Held && !g.Matched, FormattableString.Invariant($"{shadow.Name} log_t={shadow.LogT(g.At):f2} logged_loss {g.Status} emulator_holds={g.Held}"));
        }

        // predicted changes the log never showed (checked once the window after them has passed); those next to a mass removal are the same event
        foreach (var p in shadow.Predicted)
        {
            if (!shadow.Counting(p.At) || (end - p.At).TotalSeconds <= PredictedWindow)
                continue;
            if (!p.Matched && massWindows.Any(w => p.At >= w.From.AddSeconds(-PredictedWindow) && p.At <= w.To.AddSeconds(PredictedWindow)))
                continue;
            shadow.Tally.Add("predicted:" + (p.Gain ? "gain " : "loss ") + p.Status, !p.Matched, FormattableString.Invariant($"{shadow.Name} log_t={shadow.LogT(p.At):f2} {(p.Gain ? "gain" : "loss")} {p.Status} not in log"));
        }
        return shadow;
    }

    // the actions the module can press (everything else a Paladin does is defence or movement, which the emulator does not model)
    private static bool IsModuleAction(AID aid) => aid is AID.FastBlade or AID.RiotBlade or AID.RageOfHalone or AID.RoyalAuthority or AID.TotalEclipse or AID.Prominence
        or AID.HolySpirit or AID.HolyCircle or AID.Atonement or AID.Supplication or AID.Sepulchre or AID.Confiteor or AID.BladeOfFaith or AID.BladeOfTruth or AID.BladeOfValor
        or AID.BladeOfHonor or AID.Imperator or AID.Requiescat or AID.GoringBlade or AID.SpiritsWithin or AID.Expiacion or AID.CircleOfScorn or AID.Intervene or AID.FightOrFlight;
}
