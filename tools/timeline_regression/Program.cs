using System.Numerics;
using BossMod;

var failures = new List<string>();
var tests = 0;
void Check(string name, Action test)
{
    ++tests;
    try { test(); }
    catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
}
void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
DateTime At(float seconds) => t0.AddSeconds(seconds);

// An enemy that exists from `from` to `to`, targetable except inside `gaps`, with the given max HP.
Replay.Participant Enemy(ulong id, uint oid, float from, float to, uint maxHP, params (float start, float end)[] gaps)
{
    var p = new Replay.Participant(id) { OID = oid, Type = ActorType.Enemy, ZoneID = 1000 };
    p.WorldExistence.Add(new(At(from), At(to)));
    p.EffectiveExistence = new(At(from), At(to));
    p.HPMPHistory.Add(At(from), new(maxHP, maxHP, 0, 0, 0));
    p.TargetableHistory.Add(At(from), true);
    foreach (var (s, e) in gaps)
    {
        p.TargetableHistory.Add(At(s), false);
        p.TargetableHistory.Add(At(e), true);
    }
    p.DeadHistory.Add(At(to), true);
    p.HasAnyActions = true;
    return p;
}

Replay WithEnemies(params Replay.Participant[] enemies)
{
    var r = new Replay();
    r.Participants.AddRange(enemies);
    return r;
}

Replay.Cast Cast(uint action, float start, float duration)
    => new(new(ActionType.Spell, action), duration, null, default, default, false) { Time = new(At(start), At(start + duration)) };

// An ability effect from `source` at `at`; a module-less pull is anchored at the first boss cast or ability, so tests that
// reason in spawn-relative seconds add one of these at 0.
Replay.Action Ability(uint action, float at, Replay.Participant source)
    => new(new(ActionType.Spell, action), At(at), source, null, default, 0.6f, 0, 0, default);

Check("pull without encounter is anchored on the first boss cast", () =>
{
    var boss = Enemy(1, 0x100, 10, 130, 1_000_000);
    boss.Casts.Add(Cast(0x1000, 15, 3));
    var pulls = ReplayTimelineExtractor.FindPulls(WithEnemies(boss));
    Require(pulls.Count == 1, $"pulls={pulls.Count}");
    Require(pulls[0].BossOIDs.SequenceEqual([0x100u]), "boss oid");
    Require(Math.Abs((pulls[0].Start - At(15)).TotalSeconds) < 0.01, "pull start is the first cast, not the spawn");
    Require(Math.Abs((pulls[0].End - At(130)).TotalSeconds) < 0.01, "pull end is the death");
});
Check("pull whose boss never acts is dropped", () =>
{
    var idle = Enemy(1, 0x100, 10, 130, 1_000_000);
    Require(ReplayTimelineExtractor.FindPulls(WithEnemies(idle)).Count == 0, "idle boss kept");
});
Check("short trash pull is dropped", () =>
{
    var trash = Enemy(2, 0x200, 10, 25, 50_000);
    Require(ReplayTimelineExtractor.FindPulls(WithEnemies(trash)).Count == 0, "trash kept");
});
Check("encounter wins over heuristics", () =>
{
    var boss = Enemy(1, 0x100, 10, 130, 1_000_000);
    var r = WithEnemies(boss);
    r.Encounters.Add(new(1, 0x100, 1234) { Time = new(At(12), At(128)) });
    var pulls = ReplayTimelineExtractor.FindPulls(r);
    Require(pulls.Count == 1 && pulls[0].Zone == 1234 && Math.Abs((pulls[0].Start - At(12)).TotalSeconds) < 0.01, "encounter pull");
});
bool SameTime(DateTime t, float seconds) => Math.Abs((t - At(seconds)).TotalSeconds) < 0.01;
string Span(ReplayTimelineExtractor.Pull p) => $"{(p.Start - t0).TotalSeconds:f1}-{(p.End - t0).TotalSeconds:f1}";
// The Clyteum shape: the first attempt's boss despawns after the wipe, but its record stays open to the end of the replay
// (the module had gone pending and was disposed without an unload); the next attempt is a new actor of the same boss with its own record.
Replay LeftOpen(float firstLeaves)
{
    var first = Enemy(1, 0x100, 10, firstLeaves, 1_000_000);
    first.DeadHistory.Clear();
    var second = Enemy(2, 0x100, 140, 300, 1_000_000);
    var r = WithEnemies(first, second);
    r.Encounters.Add(new(1, 0x100, 1234) { Time = new(At(12), At(600)) });
    r.Encounters.Add(new(2, 0x100, 1234) { Time = new(At(150), At(300)) });
    return r;
}
Check("an encounter record left open into the next attempt of its boss ends when its boss left", () =>
{
    var r = LeftOpen(130);
    var pulls = ReplayTimelineExtractor.FindPulls(r);
    Require(pulls.Count == 2, $"pulls={pulls.Count}: {string.Join(" ", pulls.Select(Span))}");
    Require(SameTime(pulls[0].Start, 12) && SameTime(pulls[0].End, 130), $"first attempt {Span(pulls[0])}");
    Require(SameTime(pulls[1].Start, 150) && SameTime(pulls[1].End, 300), $"second attempt {Span(pulls[1])}");
    Require(pulls.All(p => p.Zone == 1234 && p.BossOIDs.SequenceEqual([0x100u])), "zone and boss come from the records");
    Require(pulls[0].Enemies.Count == 1 && pulls[0].Enemies[0].InstanceID == 1, "the first attempt does not take in the next attempt's boss");
});
Check("an encounter record left open ends at the next attempt's record when its boss is still there", () =>
{
    var r = LeftOpen(600);
    var pulls = ReplayTimelineExtractor.FindPulls(r);
    Require(pulls.Count == 2 && SameTime(pulls[0].Start, 12) && SameTime(pulls[0].End, 150), $"pulls {string.Join(" ", pulls.Select(Span))}");
});
Check("an encounter record left open whose boss left within the minimum pull length is dropped", () =>
{
    var r = LeftOpen(25);
    var pulls = ReplayTimelineExtractor.FindPulls(r);
    Require(pulls.Count == 1 && SameTime(pulls[0].Start, 150) && SameTime(pulls[0].End, 300), $"pulls {string.Join(" ", pulls.Select(Span))}");
});
Check("an encounter record that outlives its boss with no later record of that boss inside it is unchanged", () =>
{
    // A multi-boss module: its primary leaves at 50 and the fight goes on with another boss until the record closes at 190;
    // a new actor of the first boss fights again later under its own record, after this one closed.
    var first = Enemy(1, 0x100, 10, 50, 1_000_000);
    var other = Enemy(2, 0x200, 45, 200, 1_000_000);
    var again = Enemy(3, 0x100, 240, 400, 1_000_000);
    var r = WithEnemies(first, other, again);
    r.Encounters.Add(new(1, 0x100, 1234) { Time = new(At(12), At(190)) });
    r.Encounters.Add(new(3, 0x100, 1234) { Time = new(At(250), At(390)) });
    var pulls = ReplayTimelineExtractor.FindPulls(r);
    Require(pulls.Count == 2, $"pulls={pulls.Count}");
    Require(SameTime(pulls[0].Start, 12) && SameTime(pulls[0].End, 190) && pulls[0].Enemies.Count == 2, $"first record {Span(pulls[0])}");
    Require(SameTime(pulls[1].Start, 250) && SameTime(pulls[1].End, 390), $"second record {Span(pulls[1])}");
});
Check("an encounter record left open ends at the last time its boss left under the same instance id", () =>
{
    // The boss leaves and comes back under the same instance id (10-60, 70-130), then a new actor of it starts a record at 150.
    var first = Enemy(1, 0x100, 10, 60, 1_000_000);
    first.DeadHistory.Clear();
    first.WorldExistence.Add(new(At(70), At(130)));
    var second = Enemy(2, 0x100, 140, 300, 1_000_000);
    var r = WithEnemies(first, second);
    r.Encounters.Add(new(1, 0x100, 1234) { Time = new(At(12), At(600)) });
    r.Encounters.Add(new(2, 0x100, 1234) { Time = new(At(150), At(300)) });
    var pulls = ReplayTimelineExtractor.FindPulls(r);
    Require(pulls.Count == 2 && SameTime(pulls[0].Start, 12) && SameTime(pulls[0].End, 130), $"pulls {string.Join(" ", pulls.Select(Span))}");
});
Check("a record of another boss starting inside an encounter record does not cut it", () =>
{
    var first = Enemy(1, 0x100, 10, 130, 1_000_000);
    first.DeadHistory.Clear();
    var other = Enemy(2, 0x200, 140, 300, 1_000_000);
    var r = WithEnemies(first, other);
    r.Encounters.Add(new(1, 0x100, 1234) { Time = new(At(12), At(600)) });
    r.Encounters.Add(new(2, 0x200, 1234) { Time = new(At(150), At(300)) });
    var pulls = ReplayTimelineExtractor.FindPulls(r);
    Require(pulls.Count == 2 && SameTime(pulls[0].Start, 12) && SameTime(pulls[0].End, 600), $"pulls {string.Join(" ", pulls.Select(Span))}");
});
Check("no-target window ignores boss gaps covered by adds", () =>
{
    var boss = Enemy(1, 0x100, 0, 200, 1_000_000, (50, 80), (120, 140));
    var add = Enemy(2, 0x101, 55, 75, 100_000);
    var r = WithEnemies(boss, add);
    r.Actions.Add(Ability(0xF00, 0, boss));
    var pull = ReplayTimelineExtractor.FindPulls(r).Single();
    var noTarget = ReplayTimelineExtractor.NoTargetWindows(pull);
    Require(noTarget.Count == 3, $"windows={noTarget.Count}: {string.Join(",", noTarget)}");
    Require(noTarget.Any(w => Math.Abs(w.Start - 120) < 0.01 && Math.Abs(w.End - 140) < 0.01), "second boss gap is a NoTarget window");
    Require(noTarget.Any(w => Math.Abs(w.Start - 50) < 0.01 && Math.Abs(w.End - 55) < 0.01), "gap before the add spawns");
    Require(noTarget.Any(w => Math.Abs(w.Start - 75) < 0.01 && Math.Abs(w.End - 80) < 0.01), "gap after the add dies");
    var bossWindows = ReplayTimelineExtractor.BossUntargetableWindows(pull);
    Require(bossWindows.Count == 2, "boss windows");
});
Check("build subtracts no-target windows from boss windows and keeps the rest as AddsPresent", () =>
{
    var boss = Enemy(1, 0x100, 0, 200, 1_000_000, (50, 80), (120, 140));
    var add = Enemy(2, 0x101, 55, 75, 100_000);
    var r = WithEnemies(boss, add);
    r.Actions.Add(Ability(0xF00, 0, boss));
    var pull = ReplayTimelineExtractor.FindPulls(r).Single();
    var windows = ReplayTimelineExtractor.Build(1000, [pull])!.Sequences.Single().Windows!;
    bool Has(ExternalPlannerTimeline.TimelineWindowKind kind, float start, float end)
        => windows.Any(w => w.Kind == kind && Math.Abs(w.Start - start) < 0.01 && Math.Abs(w.End!.Value - end) < 0.01);
    var adds = windows.Where(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.AddsPresent).ToList();
    Require(adds.Count == 1, $"adds windows={adds.Count}: {string.Join(",", adds)}");
    Require(Has(ExternalPlannerTimeline.TimelineWindowKind.AddsPresent, 55, 75), "adds phase is the boss gap minus the no-target stretches");
    Require(Has(ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable, 50, 80), "first boss window");
    Require(Has(ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable, 120, 140), "second boss window");
    Require(windows.Count(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget) == 3, "no-target windows");
    Require(Has(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 50, 55) && Has(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 75, 80), "no-target stretches around the adds");
    Require(Has(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 120, 140), "a boss gap with no adds is only NoTarget");
    Require(adds[0].Confidence == windows.First(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable).Confidence, "adds window carries the boss window confidence");
});
Check("build merges pulls by median and keeps majority sync points", () =>
{
    Replay Fight(ulong id, float shift)
    {
        var b = Enemy(id, 0x100, 0, 200, 1_000_000, (50 + shift, 80 + shift));
        b.Casts.Add(Cast(0x1000, 10 + shift, 3));
        b.Casts.Add(Cast(0x1001, 30 + shift, 3));
        var r = WithEnemies(b);
        r.Actions.Add(Ability(0xF00, 0, b)); // anchors every pull at 0 so the shifts stay measurable
        return r;
    }
    var pulls = new[] { 0f, 2f, 4f }.Select(shift => ReplayTimelineExtractor.FindPulls(Fight(1, shift)).Single()).ToList();
    pulls[0].Enemies[0].Casts.Add(Cast(0x1002, 40, 3)); // only in one pull: dropped
    var def = ReplayTimelineExtractor.Build(1000, pulls)!;
    Require(def.Source == ExternalPlannerTimeline.TimelineSource.Replay, "source");
    var seq = def.Sequences.Single();
    Require(seq.BossOIDs!.SequenceEqual([0x100u]), "boss oids");
    Require(seq.States.Count(s => s.Kind == ExternalPlannerTimeline.ExternalStateKind.CastStart) == 2, "majority sync points");
    Require(Math.Abs(seq.States.First(s => s.IDs[0] == 0x1000).Time - 12) < 0.01, "median time");
    var window = seq.Windows!.Single(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget);
    Require(Math.Abs(window.Start - 52) < 0.01 && Math.Abs(window.End!.Value - 82) < 0.01, "median window");
    Require(window.Confidence > 0.7f, $"tight spread keeps confidence high, got {window.Confidence}");
});
// Pull summaries of boss 0x100 lasting `duration` s: filler casts every 10 s from 20 to 90 s in every pull (so no id is a
// dominant repeater), the given extra casts, and the given NoTarget windows; casts and windows past the pull's end are left out.
PullSummary Lasting(float duration, (uint ID, float At)[] casts, params (float Start, float End)[] noTarget)
{
    var events = Enumerable.Range(0, 8).Select(i => (ID: 0x1100u + (uint)i, At: 20f + 10 * i)).Concat(casts).Where(c => c.At <= duration).OrderBy(c => c.At)
        .Select(c => new PullSummary.BossEvent(c.At, c.ID, ExternalPlannerTimeline.ExternalStateKind.CastStart)).ToList();
    return new(1000, t0, duration, [0x100u], events, noTarget.Where(w => w.End <= duration).Select(w => new ReplayTimelineExtractor.Window(w.Start, w.End)).ToList(), [], []);
}
bool HasState(ExternalPlannerTimeline.TimelineSequence seq, uint id, float at) => seq.States.Any(s => s.IDs[0] == id && Math.Abs(s.Time - at) < 0.01f);
bool HasNoTarget(ExternalPlannerTimeline.TimelineSequence seq, float start, float end)
    => seq.Windows!.Any(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget && Math.Abs(w.Start - start) < 0.01f && Math.Abs(w.End!.Value - end) < 0.01f);
string Describe(ExternalPlannerTimeline.TimelineSequence seq)
    => $"states={string.Join(",", seq.States.Select(s => $"{s.IDs[0]:X}@{s.Time}"))} windows={string.Join(",", seq.Windows!.Select(w => $"{w.Kind}:{w.Start}-{w.End}"))}";
Check("a late state and window only the pulls that lasted to them can vote on are kept", () =>
{
    // A looping fight: 0x1000 at 10 s and again at 140 s in the second loop, a shared NoTarget window at 30-40 and a second-loop one
    // at 120-135. Two pulls last 160 s; four end at 100 s, before the second loop. Three of six is the majority of all pulls, but
    // only the two long pulls reached 140 s and both show it.
    List<PullSummary> pulls = [.. Enumerable.Range(0, 2).Select(_ => Lasting(160, [(0x1000, 10), (0x1000, 140)], (30, 40), (120, 135))),
        .. Enumerable.Range(0, 4).Select(_ => Lasting(100, [(0x1000, 10), (0x1000, 140)], (30, 40), (120, 135)))];
    var seq = ReplayTimelineExtractor.Build(1000, pulls)!.Sequences.Single();
    Require(HasState(seq, 0x1000, 10) && HasState(seq, 0x1000, 140), $"second-loop state dropped: {Describe(seq)}");
    Require(HasNoTarget(seq, 30, 40) && HasNoTarget(seq, 120, 135), $"second-loop window dropped: {Describe(seq)}");
});
Check("a state or window most pulls that lasted past it do not show is still dropped", () =>
{
    // Six pulls all last 160 s; only two cast 0x3000 at 50 s and go away at 120-135: two of six eligible pulls is no majority.
    List<PullSummary> pulls = [.. Enumerable.Range(0, 2).Select(_ => Lasting(160, [(0x3000, 50)], (30, 40), (120, 135))),
        .. Enumerable.Range(0, 4).Select(_ => Lasting(160, [], (30, 40)))];
    var seq = ReplayTimelineExtractor.Build(1000, pulls)!.Sequences.Single();
    Require(!seq.States.Any(s => s.IDs[0] == 0x3000), $"minority state kept: {Describe(seq)}");
    Require(HasNoTarget(seq, 30, 40) && !HasNoTarget(seq, 120, 135), $"minority window kept or shared window lost: {Describe(seq)}");
});
Check("a late state or window seen in a single surviving pull is dropped", () =>
{
    // One pull lasts 160 s; five end at 100 s. The only pull that reached 140 s shows the state and the window, but one sample is
    // never enough.
    List<PullSummary> pulls = [Lasting(160, [(0x1000, 10), (0x1000, 140)], (30, 40), (120, 135)),
        .. Enumerable.Range(0, 5).Select(_ => Lasting(100, [(0x1000, 10)], (30, 40)))];
    var seq = ReplayTimelineExtractor.Build(1000, pulls)!.Sequences.Single();
    Require(HasState(seq, 0x1000, 10) && !HasState(seq, 0x1000, 140), $"single-sample state kept: {Describe(seq)}");
    Require(HasNoTarget(seq, 30, 40) && !HasNoTarget(seq, 120, 135), $"single-sample window kept: {Describe(seq)}");
});
Check("confidence drops with spread", () =>
{
    Require(ReplayTimelineExtractor.Confidence([10, 11, 12]) >= 0.9f, "tight");
    Require(ReplayTimelineExtractor.Confidence([10, 20, 30, 40]) < 0.5f, "wide");
    Require(ReplayTimelineExtractor.Confidence([10]) == 0.6f, "single sample");
    Require(ReplayTimelineExtractor.Confidence([0, 100, 200, 300]) == 0.2f, "floor");
});
Check("ability events become AbilityUsed sync points", () =>
{
    var boss = Enemy(1, 0x100, 0, 200, 1_000_000);
    var r = WithEnemies(boss);
    r.Actions.Add(Ability(0xF00, 0, boss));
    r.Actions.Add(new(new(ActionType.Spell, 0x2000), At(15), boss, null, default, 0.6f, 0, 0, default));
    var pull = ReplayTimelineExtractor.FindPulls(r).Single();
    var def = ReplayTimelineExtractor.Build(1000, [pull])!;
    Require(def.Sequences[0].States.Any(s => s.Kind == ExternalPlannerTimeline.ExternalStateKind.AbilityUsed && s.IDs[0] == 0x2000 && Math.Abs(s.Time - 15) < 0.01), "ability sync point");
});
Check("extract groups pulls by zone, drops zone 0 and orders boss sets by first pull", () =>
{
    var first = Enemy(1, 0x100, 0, 100, 1_000_000);
    first.Casts.Add(Cast(0x1000, 5, 3));
    var second = Enemy(2, 0x200, 200, 300, 1_000_000); // well past the 5 s clustering gap: its own pull
    second.Casts.Add(Cast(0x1001, 205, 3));
    var unzoned = Enemy(3, 0x300, 0, 100, 1_000_000);
    unzoned.ZoneID = 0;
    unzoned.Casts.Add(Cast(0x1002, 5, 3));
    var result = ReplayTimelineExtractor.Extract([WithEnemies(first, second), WithEnemies(unzoned)]);
    Require(result.Count == 1 && result.ContainsKey(1000), $"zones={string.Join(",", result.Keys)}");
    var def = result[1000];
    Require(def.Source == ExternalPlannerTimeline.TimelineSource.Replay, "source");
    Require(def.Sequences.Count == 2, $"sequences={def.Sequences.Count}");
    Require(def.Sequences[0].Index == 0 && def.Sequences[0].BossOIDs!.SequenceEqual([0x100u]), "first boss set is sequence 0");
    Require(def.Sequences[1].Index == 1 && def.Sequences[1].BossOIDs!.SequenceEqual([0x200u]), "second boss set is sequence 1");
    Require(def.Sequences[0].States.Any(s => s.IDs[0] == 0x1000 && Math.Abs(s.Time) < 0.01), "sequence 0 is anchored on its own first cast");
});

Check("boss flagged ally for a moment at spawn still makes a pull with its windows", () =>
{
    var boss = Enemy(1, 0x100, 0, 200, 1_000_000, (50, 80));
    boss.AllyHistory.Add(At(0), true);
    boss.AllyHistory.Add(At(0.1f), false);
    var r = WithEnemies(boss);
    r.Actions.Add(Ability(0xF00, 0, boss));
    var pulls = ReplayTimelineExtractor.FindPulls(r);
    Require(pulls.Count == 1, $"pulls={pulls.Count}");
    var windows = ReplayTimelineExtractor.NoTargetWindows(pulls[0]);
    Require(windows.Any(w => Math.Abs(w.Start - 50) < 0.01 && Math.Abs(w.End - 80) < 0.01), $"windows={string.Join(",", windows)}");
});
Check("generic auto-attacks are not sync points", () =>
{
    var boss = Enemy(1, 0x100, 0, 200, 1_000_000);
    var r = WithEnemies(boss);
    r.Actions.Add(Ability(0xF00, 0, boss));
    boss.Casts.Add(Cast(870, 10, 3));
    boss.Casts.Add(Cast(0x1000, 20, 3));
    r.Actions.Add(Ability(872, 30, boss));
    var states = ReplayTimelineExtractor.Build(1000, [ReplayTimelineExtractor.FindPulls(r).Single()])!.Sequences.Single().States;
    Require(states.All(s => !ReplayTimelineExtractor.GenericActionIDs.Contains(s.IDs[0])), $"generic ids kept: {string.Join(",", states.Select(s => s.IDs[0]))}");
    Require(states.Any(s => s.IDs[0] == 0x1000 && s.Kind == ExternalPlannerTimeline.ExternalStateKind.CastStart), "real cast dropped");
});

// Follower pure functions (Task 7): alignment on ability effects and downtime selection from windows.
ExternalPlannerTimeline.TimelineSequence Seq(List<ExternalPlannerTimeline.TimelineState> states, List<ExternalPlannerTimeline.TimelineWindow>? windows = null)
    => new(0, 0, states, null, null, windows);
ExternalPlannerTimeline.TimelineState St(float t, ExternalPlannerTimeline.ExternalStateKind kind, uint id) => new(t, "", kind, [id], 0);
ExternalPlannerTimeline.TimelineWindow Win(ExternalPlannerTimeline.TimelineWindowKind kind, float s, float? e, float c = 1f) => new(kind, s, e, c);

Check("ability sync points count toward alignment when enabled", () =>
{
    var seq = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, 1), St(20, ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, 2), St(30, ExternalPlannerTimeline.ExternalStateKind.CastStart, 3)]);
    var history = new List<(float, uint, bool)> { (20f, 1u, false), (10f, 2u, false), (0f, 3u, true) };
    Require(ExternalTimelineHints.AlignmentScore(seq, 30, history, true) == 3, "all three agree");
    Require(ExternalTimelineHints.AlignmentScore(seq, 30, history, false) == 1, "only the cast counts without ability sync");
});
Check("generic auto-attack in history does not count toward alignment", () =>
{
    var seq = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, 870), St(20, ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, 2)]);
    var history = new List<(float, uint, bool)> { (10f, 870u, false), (0f, 2u, false) };
    Require(ExternalTimelineHints.AlignmentScore(seq, 20, history, true) == 1, "auto-attack counted");
});
Check("a periodic id corroborates once however many times it matches", () =>
{
    var seq = Seq(Enumerable.Range(0, 20).Select(i => St(i * 3, ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, 19923)).ToList());
    var history = new List<(float, uint, bool)> { (6f, 19923u, false), (3f, 19923u, false), (0f, 19923u, false) };
    Require(ExternalTimelineHints.AlignmentScore(seq, 30, history, true) == 1, "repeated id counted per entry");
    history.Add((1f, 5u, false));
    seq.States.Add(St(29, ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, 5));
    Require(ExternalTimelineHints.AlignmentScore(seq, 30, history, true) == 2, "a second distinct id adds one");
});
Check("dominant repeater is dropped from sync points", () =>
{
    var boss = Enemy(1, 0x100, 0, 200, 1_000_000);
    boss.Casts.Add(Cast(0x1000, 5, 3));
    boss.Casts.Add(Cast(0x1000, 50, 3));
    for (var i = 0; i < 10; ++i)
        boss.Casts.Add(Cast(0x2000, 10 + i * 4, 2));
    var states = ReplayTimelineExtractor.Build(1000, [ReplayTimelineExtractor.FindPulls(WithEnemies(boss)).Single()])!.Sequences.Single().States;
    Require(states.Count == 2 && states.All(s => s.IDs[0] == 0x1000), $"states={string.Join(",", states.Select(s => $"{s.IDs[0]:X}@{s.Time}"))}");
});
Check("short window does not mask long window", () =>
{
    var seq = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 12, 14), Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 20, 40)]);
    var (loss, ret) = ExternalTimelineHints.SelectDowntime(seq, 10, 25, 8.5f, 0.5f);
    Require(Math.Abs(loss - 10) < 0.01 && Math.Abs(ret - 30) < 0.01, $"expected the 20-40 window, got {loss}/{ret}");
});
Check("low confidence window not published", () =>
{
    var seq = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 20, 40, 0.3f)]);
    Require(ExternalTimelineHints.SelectDowntime(seq, 10, 25, 8.5f, 0.5f).LossIn == float.MaxValue, "published low confidence");
});
Check("adds-present window is not downtime; legacy states fall back", () =>
{
    var withAdds = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable, 20, 40), Win(ExternalPlannerTimeline.TimelineWindowKind.AddsPresent, 20, 40)]);
    Require(ExternalTimelineHints.SelectDowntime(withAdds, 10, 25, 8.5f, 0.5f).LossIn == float.MaxValue, "adds window published");
    var legacy = Seq([St(20, ExternalPlannerTimeline.ExternalStateKind.Untargetable, 0), St(40, ExternalPlannerTimeline.ExternalStateKind.Targetable, 0)]);
    var (loss, ret) = ExternalTimelineHints.SelectDowntime(legacy, 10, 25, 8.5f, 0.5f);
    Require(Math.Abs(loss - 10) < 0.01 && Math.Abs(ret - 30) < 0.01, "legacy fallback");
});
Check("world check honors window confidence", () =>
{
    var low = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 20, 40, 0.3f)]);
    Require(!ExternalTimelineHints.PredictedNoTargetAt(low, 30, 0.5f), "low confidence window predicted no target");
    var high = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 20, 40, 0.8f)]);
    Require(ExternalTimelineHints.PredictedNoTargetAt(high, 30, 0.5f), "confident window ignored");
    Require(!ExternalTimelineHints.PredictedNoTargetAt(high, 45, 0.5f), "outside the window");
    var legacy = Seq([St(20, ExternalPlannerTimeline.ExternalStateKind.Untargetable, 0), St(40, ExternalPlannerTimeline.ExternalStateKind.Targetable, 0)]);
    Require(ExternalTimelineHints.PredictedNoTargetAt(legacy, 30, 0.5f) && !ExternalTimelineHints.PredictedNoTargetAt(legacy, 45, 0.5f), "legacy markers");
});
Check("targetable edges match boss windows within tolerance and confidence", () =>
{
    ExternalPlannerTimeline.TimelineSequence Boss(float confidence) => new(0, 0, [], null, [0x100], [Win(ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable, 50, 80, confidence)]);
    var start = ExternalTimelineHints.MatchTargetableEdge(Boss(1f), 52, false, 20, 0.5f);
    Require(start is { } s && Math.Abs(s - 50) < 0.01, $"untargetable edge -> window start, got {start}");
    Require(ExternalTimelineHints.MatchTargetableEdge(Boss(0.3f), 52, false, 20, 0.5f) == null, "low confidence window matched");
    var end = ExternalTimelineHints.MatchTargetableEdge(Boss(1f), 79, true, 20, 0.5f);
    Require(end is { } e && Math.Abs(e - 80) < 0.01, $"targetable edge -> window end, got {end}");
    Require(ExternalTimelineHints.MatchTargetableEdge(Boss(1f), 120, false, 20, 0.5f) == null, "beyond tolerance matched");
});
Check("window beyond horizon is not published", () =>
{
    var seq = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 60, 80)]);
    Require(ExternalTimelineHints.SelectDowntime(seq, 10, 25, 8.5f, 0.5f).LossIn == float.MaxValue, "beyond horizon");
});
Check("sequence is exhausted past its tail plus tolerance", () =>
{
    var seq = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 1), St(100, ExternalPlannerTimeline.ExternalStateKind.CastStart, 2)]);
    Require(!ExternalTimelineHints.IsExhausted(seq, 50, 6), "before the tail");
    Require(!ExternalTimelineHints.IsExhausted(seq, 105, 6), "inside the tolerance");
    Require(ExternalTimelineHints.IsExhausted(seq, 107, 6), "past the tail plus tolerance");
    var withWindow = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 1), St(100, ExternalPlannerTimeline.ExternalStateKind.CastStart, 2)], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 120, 140)]);
    Require(!ExternalTimelineHints.IsExhausted(withWindow, 130, 6), "a window ending after the last state extends the sequence");
    Require(ExternalTimelineHints.IsExhausted(withWindow, 147, 6), "past the window end plus tolerance");
    var openWindow = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 1)], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 20, null)]);
    Require(ExternalTimelineHints.IsExhausted(openWindow, 30, 6), "an open window does not extend the sequence");
});
Check("pull start is the first enemy in combat or a listed boss, never the followed boss re-entering", () =>
{
    Require(ExternalTimelineHints.IsPullStart(true, false, false, false), "first enemy entering combat");
    Require(!ExternalTimelineHints.IsPullStart(true, true, false, false), "an unlisted enemy joining a running fight");
    Require(ExternalTimelineHints.IsPullStart(true, true, true, false), "a listed boss engaging while its adds already fight");
    Require(!ExternalTimelineHints.IsPullStart(false, false, true, false), "leaving combat is never a pull start");
    Require(!ExternalTimelineHints.IsPullStart(true, true, true, true), "the followed boss re-entering combat while synced continues the fight");
    Require(!ExternalTimelineHints.IsPullStart(true, false, true, true), "the followed boss re-entering combat alone still continues the fight");
    Require(ExternalTimelineHints.IsPullStart(true, false, true, false), "a listed boss engaging without a synced clock on it starts the pull");
});
Check("a followed boss continues the fight only within the continuation gap", () =>
{
    Require(ExternalTimelineHints.IsContinuation(true, false, true, 1, 3), "back in combat 1 s after leaving");
    Require(ExternalTimelineHints.IsContinuation(true, false, true, 3, 3), "exactly at the gap");
    Require(!ExternalTimelineHints.IsContinuation(true, false, true, 40, 3), "a 40 s gap is a re-pull");
    Require(!ExternalTimelineHints.IsContinuation(true, false, true, float.MaxValue, 3), "an unbounded gap is a re-pull");
    Require(ExternalTimelineHints.IsContinuation(true, false, true, float.NaN, 3), "a boss that never left combat is a flicker");
    Require(!ExternalTimelineHints.IsContinuation(false, false, true, 1, 3), "no synced clock");
    Require(!ExternalTimelineHints.IsContinuation(true, true, true, 1, 3), "an exhausted clock");
    Require(!ExternalTimelineHints.IsContinuation(true, false, false, 1, 3), "a boss the current sequence does not name");
});
Check("re-alignment score depends on confirmation and jump direction", () =>
{
    Require(ExternalTimelineHints.RequiredScore(false, true, 100, 60, 40, 6, 30) == 2, "unconfirmed");
    Require(ExternalTimelineHints.RequiredScore(true, true, 100, 104, 4, 6, 30) == 1, "confirmed, same sequence, small drift");
    Require(ExternalTimelineHints.RequiredScore(true, true, 100, 150, 50, 6, 30) == 2, "confirmed forward jump");
    Require(ExternalTimelineHints.RequiredScore(true, true, 100, 60, 40, 6, 30) == 3, "confirmed backward jump beyond the limit");
    Require(ExternalTimelineHints.RequiredScore(true, true, 100, 80, 20, 6, 30) == 2, "confirmed backward jump within the limit");
    Require(ExternalTimelineHints.RequiredScore(false, false, 100, 60, float.MaxValue, 6, 30) == 2, "unconfirmed, different sequence");
    Require(ExternalTimelineHints.RequiredScore(true, false, 100, 60, float.MaxValue, 6, 30) == 3, "confirmed, different sequence, candidate far behind");
    Require(ExternalTimelineHints.RequiredScore(true, false, 100, 90, float.MaxValue, 6, 30) == 2, "confirmed, different sequence, candidate only slightly behind");
    Require(ExternalTimelineHints.RequiredScore(true, true, float.NaN, 60, float.MaxValue, 6, 30) == 2, "confirmed without a running clock");
});

// HP-gated branches: detection on per-pull NoTarget windows, the decision point, the HP threshold, and extraction end to end.
IReadOnlyList<ReplayTimelineExtractor.Window> Wins(params float[] starts) => starts.Select(s => new ReplayTimelineExtractor.Window(s, s + 20)).ToList();
string Branched((int WindowIndex, int[] Early, int[] Late)? found)
    => found is { } f ? $"index={f.WindowIndex} early=[{string.Join(",", f.Early)}] late=[{string.Join(",", f.Late)}]" : "none";

Check("branch detection needs exactly two clusters of at least two pulls", () =>
{
    var found = ReplayTimelineExtractor.DetectBranch([Wins(100.5f), Wins(118), Wins(100.7f), Wins(118.2f), Wins(101), Wins(118.5f), Wins(100.9f), Wins(117.9f), Wins(100.6f), Wins(118.4f)]);
    Require(found is { } f && f.WindowIndex == 0 && f.Early.SequenceEqual([0, 2, 4, 6, 8]) && f.Late.SequenceEqual([1, 3, 5, 7, 9]), $"5/5 split: {Branched(found)}");
    var one = ReplayTimelineExtractor.DetectBranch([Wins(100), Wins(101), Wins(102), Wins(100.5f), Wins(101.5f)]);
    Require(one == null, $"one cluster branched: {Branched(one)}");
    var three = ReplayTimelineExtractor.DetectBranch([Wins(100), Wins(100.5f), Wins(118), Wins(118.5f), Wins(140), Wins(140.5f)]);
    Require(three == null, $"three clusters branched: {Branched(three)}");
    var lone = ReplayTimelineExtractor.DetectBranch([Wins(100), Wins(100.5f), Wins(101), Wins(100.2f), Wins(118)]);
    Require(lone == null, $"a one-pull cluster branched: {Branched(lone)}");
});
Check("window starts scattered by damage are not a branch", () =>
{
    // Two clusters by the 5 s gap (104 -> 110), but the gap of 6 s is less than three times the late cluster's spread (8 s).
    var spread = ReplayTimelineExtractor.DetectBranch([Wins(100), Wins(110), Wins(101), Wins(112), Wins(103), Wins(115), Wins(104), Wins(118)]);
    Require(spread == null, $"a damage spread branched: {Branched(spread)}");
    // Exactly three times the wider spread still branches: 100-102 | 108-110, gap 6 = 3 x 2.
    var edge = ReplayTimelineExtractor.DetectBranch([Wins(100), Wins(108), Wins(102), Wins(110)]);
    Require(edge is { } e && e.Early.SequenceEqual([0, 2]) && e.Late.SequenceEqual([1, 3]), $"gap of three spreads: {Branched(edge)}");
    // The Ultima spread (early 100.7-100.8, late 118.2-119.3) is far inside the ratio.
    var ultima = ReplayTimelineExtractor.DetectBranch([Wins(100.8f), Wins(118.2f), Wins(119.3f), Wins(100.7f), Wins(118.3f), Wins(100.7f), Wins(118.3f), Wins(118.2f), Wins(100.7f), Wins(118.5f), Wins(100.7f), Wins(118.3f), Wins(118.2f)]);
    Require(ultima is { } u && u.Early.SequenceEqual([0, 3, 5, 8, 10]) && u.Late.Length == 8, $"Ultima spread: {Branched(ultima)}");
});
Check("branch detection skips shared windows and gaps shorter than a published loss", () =>
{
    IReadOnlyList<ReplayTimelineExtractor.Window> Pull(float branchStart, bool blip) => blip
        ? [new(10, 13), new(20, 40), new(branchStart, branchStart + 26)]
        : [new(20, 40), new(branchStart, branchStart + 26)];
    var found = ReplayTimelineExtractor.DetectBranch([Pull(100, true), Pull(118, false), Pull(101, false), Pull(118.5f, true)]);
    Require(found is { } f && f.WindowIndex == 1 && f.Early.SequenceEqual([0, 2]) && f.Late.SequenceEqual([1, 3]), $"split after the shared window: {Branched(found)}");
});
Check("a window whose length straddles the long-loss bar is not a branch", () =>
{
    // Windurst 0x4DA6: the second gap lasts 5.8-10.2 s across pulls, so the long-window index shifts in the pulls where it is short
    // and the third gap (85 s) lines up against the second (40 s) of the others. Those pulls did go away at 40 s too.
    IReadOnlyList<ReplayTimelineExtractor.Window> Pull(float secondLength) => [new(1, 20.5f), new(40, 40 + secondLength), new(85, 93.6f)];
    var found = ReplayTimelineExtractor.DetectBranch([Pull(9.6f), Pull(7.8f), Pull(9.8f), Pull(9.8f), Pull(5.8f), Pull(8.3f), Pull(9.4f), Pull(10.2f)]);
    Require(found == null, $"length-filter split branched: {Branched(found)}");
});
Check("a long window only some pulls have is not a branch; the Ultima shape still is", () =>
{
    // An extra intro/adds window in some pulls puts their shared 60 s window one index later: at index 0 they cluster at 5 s and
    // the others at 60 s. The early pulls went away at 60 s too, so this is no branch.
    IReadOnlyList<ReplayTimelineExtractor.Window> Pull(bool intro) => intro ? [new(5, 20), new(60, 85)] : [new(60, 85)];
    var extra = ReplayTimelineExtractor.DetectBranch([Pull(true), Pull(false), Pull(true), Pull(false), Pull(true), Pull(false)]);
    Require(extra == null, $"extra window branched: {Branched(extra)}");
    // Ultima: the early pulls are still away (since 100.7) when the late ones go at 118.2, which is not a window of their own.
    IReadOnlyList<ReplayTimelineExtractor.Window> Ultima(float start) => [new(start, start + 26.5f)];
    var ultima = ReplayTimelineExtractor.DetectBranch([Ultima(100.7f), Ultima(118.2f), Ultima(118.3f), Ultima(100.7f), Ultima(100.8f), Ultima(118.5f)]);
    Require(ultima is { } u && u.Early.SequenceEqual([0, 3, 4]) && u.Late.SequenceEqual([1, 2, 5]), $"Ultima shape: {Branched(ultima)}");
});
Check("a branch needs a majority of the boss set's pulls in its two clusters", () =>
{
    // Ten pulls, six wiped before the window: a 2/2 split describes too little of the fight.
    List<IReadOnlyList<ReplayTimelineExtractor.Window>> pulls = [Wins(100), Wins(100.5f), Wins(118), Wins(118.5f)];
    pulls.AddRange(Enumerable.Range(0, 6).Select(_ => (IReadOnlyList<ReplayTimelineExtractor.Window>)[]));
    var few = ReplayTimelineExtractor.DetectBranch(pulls);
    Require(few == null, $"2/2 of 10 branched: {Branched(few)}");
    // Four wiped: 3/3 is six of ten, a majority.
    List<IReadOnlyList<ReplayTimelineExtractor.Window>> more = [Wins(100), Wins(100.5f), Wins(101), Wins(118), Wins(118.5f), Wins(119)];
    more.AddRange(Enumerable.Range(0, 4).Select(_ => (IReadOnlyList<ReplayTimelineExtractor.Window>)[]));
    var enough = ReplayTimelineExtractor.DetectBranch(more);
    Require(enough is { } e && e.Early.SequenceEqual([0, 1, 2]) && e.Late.SequenceEqual([3, 4, 5]), $"3/3 of 10: {Branched(enough)}");
});
Check("branch threshold is the midpoint of separable HP, otherwise none", () =>
{
    var separable = ReplayTimelineExtractor.LearnThreshold([40, 44, 46.1f], [46.7f, 50, 55]);
    Require(separable is { } t && Math.Abs(t - 46.4f) < 0.001f, $"separable: {separable}");
    Require(ReplayTimelineExtractor.LearnThreshold([40, 48], [47, 50]) == null, "overlapping HP learned a threshold");
    Require(ReplayTimelineExtractor.LearnThreshold([], [47]) == null, "no early samples learned a threshold");
    Require(ReplayTimelineExtractor.LearnThreshold([40], [47, 50]) == null, "one early sample learned a threshold");
    Require(ReplayTimelineExtractor.LearnThreshold([40, 44], [47]) == null, "one late sample learned a threshold");
    Require(ReplayTimelineExtractor.LearnThreshold([40, 44], [47, 50]) is { } two && Math.Abs(two - 45.5f) < 0.001f, "two samples on each side are enough");
});
Check("branch decision time is the early sibling's first own sync point", () =>
{
    var early = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1000), St(20, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1001), St(30, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x2000)]);
    var late = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1000), St(20, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1001), St(45, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x2000)]);
    // Early window at 36 s: the late sibling's 0x2000 comes at 45, after the early sibling went away (plus the follower's margin).
    var decision = ReplayTimelineExtractor.DivergenceTime(early, late, 36, 6, 30);
    Require(decision is { } d && Math.Abs(d - 30) < 0.001f, $"decision={decision}");
    Require(ReplayTimelineExtractor.DivergenceTime(early, early, 36, 6, 30) == null, "identical siblings diverge");
    var shifted = Seq([St(11.5f, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1000), St(18.5f, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1001), St(31.9f, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x2000)]);
    Require(ReplayTimelineExtractor.DivergenceTime(early, shifted, 36, 6, 30) == null, "sync points within the tolerance counted as own");
});
Check("a divergence the late sibling also shows before the early window, or far ahead of it, is no divergence", () =>
{
    var early = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1000), St(30, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x2000)]);
    // The late sibling casts 0x2000 5 s earlier: outside 2 s, but a late pull would still show it while the branch is undecided.
    var earlier = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1000), St(25, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x2000)]);
    var sameId = ReplayTimelineExtractor.DivergenceTime(early, earlier, 36, 2, 30);
    Require(sameId == null, $"an id the late sibling has before the early window diverged at {sameId}");
    var lateOnly = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1000)]);
    var farAhead = ReplayTimelineExtractor.DivergenceTime(early, lateOnly, 70, 2, 30);
    Require(farAhead == null, $"a divergence 40 s before the early window counted: {farAhead}");
    var inLead = ReplayTimelineExtractor.DivergenceTime(early, lateOnly, 55, 2, 30);
    Require(inLead is { } l && Math.Abs(l - 30) < 0.001f, $"a divergence 25 s before the early window: {inLead}");
    // An early-only entry long before the window (one sibling's majority noise) is passed over for the one inside the lead.
    var noisy = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1000), St(15, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x4000), St(60, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x2000)]);
    var lateTransition = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1000), St(90, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x2000)]);
    var transition = ReplayTimelineExtractor.DivergenceTime(noisy, lateTransition, 66, 6, 30);
    Require(transition is { } x && Math.Abs(x - 60) < 0.001f, $"transition inside the lead: {transition}");
});
Check("a divergence candidate must show in most early pulls and in no late pull before the bound", () =>
{
    // Early window at 36, bound 36 + 6 = 42. Five early pulls, eight late ones.
    IReadOnlyList<(float Time, uint ID)> Events(params (float, uint)[] events) => events;
    var candidate = St(30, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x3000);
    List<IReadOnlyList<(float Time, uint ID)>> Late(int showing, float at)
        => Enumerable.Range(0, 8).Select(i => i < showing ? Events((10, 0x1000), (at, 0x3000)) : Events((10, 0x1000))).ToList();
    List<IReadOnlyList<(float Time, uint ID)>> Early(int showing, float at)
        => Enumerable.Range(0, 5).Select(i => i < showing ? Events((10, 0x1000), (at, 0x3000)) : Events((10, 0x1000))).ToList();
    Require(ReplayTimelineExtractor.AcceptDivergence(candidate, Early(5, 30.4f), Late(0, 0), 42), "every early pull, no late pull: rejected");
    Require(ReplayTimelineExtractor.AcceptDivergence(candidate, Early(4, 31.9f), Late(0, 0), 42), "four of five early pulls (80%) within 2 s: rejected");
    Require(!ReplayTimelineExtractor.AcceptDivergence(candidate, Early(3, 30), Late(0, 0), 42), "three of five early pulls (60%) accepted");
    Require(!ReplayTimelineExtractor.AcceptDivergence(candidate, Early(5, 32.5f), Late(0, 0), 42), "early pulls 2.5 s off the candidate accepted");
    Require(!ReplayTimelineExtractor.AcceptDivergence(candidate, Early(5, 30), Late(3, 30), 42), "an id three of eight late pulls show at 30 s accepted");
    Require(!ReplayTimelineExtractor.AcceptDivergence(candidate, Early(5, 30), Late(1, 5), 42), "an id one late pull shows long before accepted");
    Require(!ReplayTimelineExtractor.AcceptDivergence(candidate, Early(5, 30), Late(1, 42), 42), "an id a late pull shows exactly at the bound accepted");
    Require(ReplayTimelineExtractor.AcceptDivergence(candidate, Early(5, 30), Late(8, 50), 42), "an id every late pull shows only past the bound: rejected");
    Require(!ReplayTimelineExtractor.AcceptDivergence(candidate, [], Late(0, 0), 42), "no early pulls accepted");

    // The merged late sibling lost 0x2F00 (three of eight is no majority), so the sibling test alone takes it at 28; the raw pulls
    // reject it and the next candidate, 0x3000 at 30, is the decision point.
    var early = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1000), St(28, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x2F00), candidate]);
    var late = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x1000), St(50, ExternalPlannerTimeline.ExternalStateKind.CastStart, 0x3000)]);
    var earlyPulls = Enumerable.Range(0, 5).Select(_ => Events((10, 0x1000), (28, 0x2F00), (30, 0x3000))).ToList();
    var latePulls = Enumerable.Range(0, 8).Select(i => i < 3 ? Events((10, 0x1000), (28.3f, 0x2F00), (50, 0x3000)) : Events((10, 0x1000), (50, 0x3000))).ToList();
    Require(ReplayTimelineExtractor.DivergenceTime(early, late, 36, 6, 30) is { } merged && Math.Abs(merged - 28) < 0.001f, "the sibling test alone takes 0x2F00");
    var raw = ReplayTimelineExtractor.DivergenceTime(early, late, 36, 6, 30, s => ReplayTimelineExtractor.AcceptDivergence(s, earlyPulls, latePulls, 42));
    Require(raw is { } r && Math.Abs(r - 30) < 0.001f, $"raw pulls: {raw}");
    var none = ReplayTimelineExtractor.DivergenceTime(early, late, 36, 6, 30, _ => false);
    Require(none == null, $"no accepted candidate still diverged at {none}");
});
Check("primary boss HP comes from a boss that exists at that time", () =>
{
    // Two actors share the boss OID: a bigger one gone at 20 s (last at 10%) and the fight's boss, at 70% at 30 s.
    var gone = Enemy(1, 0x100, 0, 20, 2_000_000);
    gone.HPMPHistory[At(19)] = new(200_000, 2_000_000, 0, 0, 0);
    var boss = Enemy(2, 0x100, 0, 200, 1_000_000);
    boss.HPMPHistory[At(30)] = new(700_000, 1_000_000, 0, 0, 0);
    var pull = new ReplayTimelineExtractor.Pull(1000, At(0), At(200), [0x100u], [gone, boss]);
    var hp = ReplayTimelineExtractor.PrimaryBossHPPercent(pull, 30);
    Require(hp is { } h && Math.Abs(h - 70) < 0.01f, $"hp={hp}");
    Require(ReplayTimelineExtractor.PrimaryBossHPPercent(pull, 250) == null, $"no boss exists at 250 s but got {ReplayTimelineExtractor.PrimaryBossHPPercent(pull, 250)}");
});

// One pull of boss 0x100 whose HP falls by `ratePerSecond` percent per second: common casts at 10 and 20 s, the transition cast
// 0x3000 at `castAt`, then 24 s untargetable from 6 s after it. `extra` adds casts.
Replay BranchFight(ulong id, float castAt, float ratePerSecond, float jitter, params (uint Action, float At)[] extra)
{
    var boss = Enemy(id, 0x100, 0, 200, 1_000_000, (castAt + 6, castAt + 30));
    for (var t = 1; t <= 200; ++t)
        boss.HPMPHistory[At(t)] = new((uint)(1_000_000 * Math.Max(0.01f, 1 - ratePerSecond * t / 100)), 1_000_000, 0, 0, 0);
    boss.Casts.Add(Cast(0x1000, 10 + jitter, 3));
    boss.Casts.Add(Cast(0x1001, 20 + jitter, 3));
    boss.Casts.Add(Cast(0x3000, castAt, 3));
    foreach (var (action, at) in extra)
        boss.Casts.Add(Cast(action, at, 3));
    var r = WithEnemies(boss);
    r.Actions.Add(Ability(0xF00, 0, boss)); // anchors the pull at 0
    return r;
}
// Another boss set met before the branching one, so the siblings are not sequence 0.
Replay Opener()
{
    var boss = Enemy(99, 0x200, -300, -200, 1_000_000);
    boss.Casts.Add(Cast(0x5000, -290, 3));
    return WithEnemies(boss);
}
float HPAt(float ratePerSecond, float t) => 100 * (1 - ratePerSecond * t / 100);

Check("extract splits an HP-gated boss into two sibling sequences", () =>
{
    float[] fast = [1.0f, 1.1f, 1.2f], slow = [0.6f, 0.65f, 0.7f];
    List<Replay> replays = [Opener()];
    for (var i = 0; i < 3; ++i)
    {
        replays.Add(BranchFight((ulong)(10 + i), 30 + 0.2f * i, fast[i], 0.2f * i));
        replays.Add(BranchFight((ulong)(20 + i), 50 + 0.2f * i, slow[i], 0.2f * i));
    }
    var result = ReplayTimelineExtractor.Extract(replays);
    Require(result.Count == 1 && result.ContainsKey(1000), $"zones={string.Join(",", result.Keys)}");
    var sequences = result[1000].Sequences;
    Require(sequences.Count == 3, $"sequences={sequences.Count}");
    Require(sequences.Select(s => s.Index).SequenceEqual([0, 1, 2]), "sequences renumbered in order");
    Require(sequences[0].Branch == null && sequences[0].BossOIDs!.SequenceEqual([0x200u]), "the boss set met first has no branch");
    var (early, late) = (sequences[1], sequences[2]);
    Require(early.Branch is { Group: 1, Below: true } && late.Branch is { Group: 1, Below: false }, $"siblings: {early.Branch} / {late.Branch}");
    Require(early.BossOIDs!.SequenceEqual([0x100u]) && late.BossOIDs!.SequenceEqual([0x100u]), "siblings keep the boss set");
    var earlyWindow = early.Windows!.Single(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget);
    var lateWindow = late.Windows!.Single(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget);
    Require(Math.Abs(earlyWindow.Start - 36) < 0.5f && Math.Abs(lateWindow.Start - 56) < 0.5f, $"windows early={earlyWindow} late={lateWindow}");
    var branch = early.Branch!;
    Require(Math.Abs(branch.DecisionTime - 30) < 0.5f && late.Branch!.DecisionTime == branch.DecisionTime, $"decision={branch.DecisionTime}/{late.Branch!.DecisionTime}");
    var fastMax = fast.Max(rate => HPAt(rate, 30));
    var slowMin = slow.Min(rate => HPAt(rate, 30));
    Require(branch.HpThreshold is { } threshold && threshold > fastMax && threshold < slowMin && late.Branch!.HpThreshold == threshold, $"threshold={branch.HpThreshold} fast<={fastMax} slow>={slowMin}");
    var lines = ReplayTimelineExtractor.DescribeBranches(result[1000]).ToList();
    var expected = $"branch group=1 boss=0x100 decision={branch.DecisionTime:f2} threshold={branch.HpThreshold:f2} early={earlyWindow.Start:f2} late={lateWindow.Start:f2}";
    Require(lines.Count == 1 && lines[0] == expected, $"branch lines: {string.Join(" | ", lines)}; expected {expected}");
});
Check("extract passes over a divergence some late pulls show and takes the next candidate", () =>
{
    // Four early pulls cast 0x2F00 at 28 s and the transition 0x3000 at 30 s; three of the eight late pulls cast 0x2F00 at 28 s
    // too. Three of eight is no majority, so the merged late sibling lost 0x2F00 and the siblings alone would decide at 28 s.
    List<Replay> replays = [Opener()];
    for (var i = 0; i < 4; ++i)
        replays.Add(BranchFight((ulong)(10 + i), 30 + 0.2f * i, 1.0f + 0.05f * i, 0.2f * i, (0x2F00, 28 + 0.2f * i)));
    for (var i = 0; i < 8; ++i)
        replays.Add(i < 3 ? BranchFight((ulong)(20 + i), 50 + 0.2f * i, 0.6f + 0.02f * i, 0.2f * i, (0x2F00, 28 + 0.2f * i)) : BranchFight((ulong)(20 + i), 50 + 0.2f * i, 0.6f + 0.02f * i, 0.2f * i));
    var result = ReplayTimelineExtractor.Extract(replays)[1000];
    Require(result.Sequences.Count == 3, $"sequences={result.Sequences.Count}");
    var (early, late) = (result.Sequences[1], result.Sequences[2]);
    Require(early.Branch is { Below: true } && late.Branch is { Below: false }, $"siblings: {early.Branch} / {late.Branch}");
    Require(early.States.Any(s => s.IDs[0] == 0x2F00 && Math.Abs(s.Time - 28.3f) < 0.01f), "the early sibling lost 0x2F00");
    Require(!late.States.Any(s => s.IDs[0] == 0x2F00), "the late sibling kept 0x2F00");
    Require(Math.Abs(early.Branch!.DecisionTime - 30.3f) < 0.01f && late.Branch!.DecisionTime == early.Branch.DecisionTime, $"decision={early.Branch.DecisionTime}");
    Require(early.Branch.HpThreshold != null, "HP at the transition cast separates the pulls but no threshold was learned");
    Require(ReplayTimelineExtractor.DescribeBranches(result).Single().Contains("decision=30.30 ", StringComparison.Ordinal), $"branch line: {ReplayTimelineExtractor.DescribeBranches(result).Single()}");
});
Check("extract leaves boss sets without a clean split as one sequence, as before", () =>
{
    // Three pulls (two fast, one slow): no branch, and the output is exactly what Build gave before branches existed.
    List<Replay> replays = [Opener(), BranchFight(10, 30, 1.0f, 0), BranchFight(11, 30.2f, 1.1f, 0.2f), BranchFight(20, 50, 0.6f, 0)];
    var result = ReplayTimelineExtractor.Extract(replays)[1000];
    Require(result.Sequences.Count == 2 && result.Sequences.All(s => s.Branch == null), $"sequences={result.Sequences.Count} branches={result.Sequences.Count(s => s.Branch != null)}");
    var pulls = replays.SelectMany(ReplayTimelineExtractor.FindPulls).ToList();
    var opener = ReplayTimelineExtractor.Build(1000, pulls.Where(p => p.BossOIDs.SequenceEqual([0x200u])).ToList())!;
    var boss = ReplayTimelineExtractor.Build(1000, pulls.Where(p => p.BossOIDs.SequenceEqual([0x100u])).ToList())!;
    var expected = new ExternalPlannerTimeline.TimelineDefinition(1000, $"{opener.SourceFile};{boss.SourceFile}", true,
        [opener.Sequences[0] with { Index = 0 }, boss.Sequences[0] with { Index = 1 }], ExternalPlannerTimeline.TimelineSource.Replay, (opener.Confidence + boss.Confidence) / 2);
    var json = System.Text.Json.JsonSerializer.Serialize(result);
    Require(json == System.Text.Json.JsonSerializer.Serialize(expected), "output differs from the pre-branch extraction");
    Require(!json.Contains("Branch", StringComparison.Ordinal), "a timeline without branches writes a Branch key");
    Require(!ReplayTimelineExtractor.DescribeBranches(result).Any(), "a timeline without branches prints a branch line");
});

// Pull summaries: one pull of boss 0x100 anchored at 0, casts 0x1000 and 0x1001 and an ability 0x2000 shifted by `shift`, 30 s
// untargetable from 50 + shift, and HP falling 0.4% per second.
Replay Fight(ulong id, float shift)
{
    var boss = Enemy(id, 0x100, 0, 200, 1_000_000, (50 + shift, 80 + shift));
    for (var t = 1; t <= 200; ++t)
        boss.HPMPHistory[At(t)] = new((uint)(1_000_000 * (1 - 0.004f * t)), 1_000_000, 0, 0, 0);
    boss.Casts.Add(Cast(0x1000, 10 + shift, 3));
    boss.Casts.Add(Cast(0x1001, 30 + shift, 3));
    var r = WithEnemies(boss);
    r.Actions.Add(Ability(0xF00, 0, boss));
    r.Actions.Add(Ability(0x2000, 20 + shift, boss));
    return r;
}
Replay InZone(Replay r, uint zone)
{
    foreach (var p in r.Participants)
        p.ZoneID = zone;
    return r;
}
Check("summaries give the same timeline as replays", () =>
{
    // Several pulls of one fight with shifted timings plus a second boss, and an HP-gated boss in another zone: the replay path and
    // the summary path must serialize identically, and exactly as the extractor did before it read summaries (golden captured from
    // the replay-only extractor on these replays).
    const string golden = """{"1000":{"ZoneID":1000,"SourceFile":"replay:200;replay:100","AutomaticFallback":true,"Sequences":[{"Index":0,"StartTime":0,"States":[{"Time":0,"Name":"","Kind":1,"IDs":[20480],"Hint":0}],"PredictionEndTime":null,"BossOIDs":[512],"Windows":[]},{"Index":1,"StartTime":0,"States":[{"Time":0,"Name":"","Kind":5,"IDs":[3840],"Hint":0},{"Time":12,"Name":"","Kind":1,"IDs":[4096],"Hint":0},{"Time":22,"Name":"","Kind":5,"IDs":[8192],"Hint":0},{"Time":32,"Name":"","Kind":1,"IDs":[4097],"Hint":0}],"PredictionEndTime":null,"BossOIDs":[256],"Windows":[{"Kind":1,"Start":52,"End":82,"Confidence":0.8},{"Kind":0,"Start":52,"End":82,"Confidence":0.8}]}],"Source":2,"Confidence":0.70000005},"1001":{"ZoneID":1001,"SourceFile":"replay:100","AutomaticFallback":true,"Sequences":[{"Index":0,"StartTime":0,"States":[{"Time":0,"Name":"","Kind":5,"IDs":[3840],"Hint":0},{"Time":10.1,"Name":"","Kind":1,"IDs":[4096],"Hint":0},{"Time":20.1,"Name":"","Kind":1,"IDs":[4097],"Hint":0},{"Time":30.1,"Name":"","Kind":1,"IDs":[12288],"Hint":0}],"PredictionEndTime":null,"BossOIDs":[256],"Windows":[{"Kind":1,"Start":36.1,"End":60.1,"Confidence":0.98999995},{"Kind":0,"Start":36.1,"End":60.1,"Confidence":0.98999995}],"Branch":{"Group":0,"DecisionTime":30.1,"HpThreshold":75.25,"Below":true}},{"Index":1,"StartTime":0,"States":[{"Time":0,"Name":"","Kind":5,"IDs":[3840],"Hint":0},{"Time":10.1,"Name":"","Kind":1,"IDs":[4096],"Hint":0},{"Time":20.1,"Name":"","Kind":1,"IDs":[4097],"Hint":0},{"Time":50.1,"Name":"","Kind":1,"IDs":[12288],"Hint":0}],"PredictionEndTime":null,"BossOIDs":[256],"Windows":[{"Kind":1,"Start":56.1,"End":80.1,"Confidence":0.98999995},{"Kind":0,"Start":56.1,"End":80.1,"Confidence":0.98999995}],"Branch":{"Group":0,"DecisionTime":30.1,"HpThreshold":75.25,"Below":false}}],"Source":2,"Confidence":0.98999995}}""";
    List<Replay> replays = [Opener(), .. new[] { 0f, 2f, 4f }.Select(shift => Fight(1, shift)),
        InZone(BranchFight(10, 30, 1.0f, 0), 1001), InZone(BranchFight(11, 30.2f, 1.1f, 0.2f), 1001), InZone(BranchFight(20, 50, 0.6f, 0), 1001), InZone(BranchFight(21, 50.2f, 0.65f, 0.2f), 1001)];
    var viaReplays = ReplayTimelineExtractor.Extract(replays);
    var summaries = replays.SelectMany(r => ReplayTimelineExtractor.FindPulls(r).Select(p => PullSummary.Summarize(p))).ToList();
    var viaSummaries = ReplayTimelineExtractor.Extract(summaries);
    Require(viaReplays.Count == viaSummaries.Count, "zone count differs");
    foreach (var (zone, timeline) in viaReplays)
        Require(System.Text.Json.JsonSerializer.Serialize(timeline) == System.Text.Json.JsonSerializer.Serialize(viaSummaries[zone]), $"zone {zone} differs");
    Require(System.Text.Json.JsonSerializer.Serialize(viaReplays) == golden, $"replay path differs from the golden: {System.Text.Json.JsonSerializer.Serialize(viaReplays)}");
    Require(System.Text.Json.JsonSerializer.Serialize(viaSummaries) == golden, $"summary path differs from the golden: {System.Text.Json.JsonSerializer.Serialize(viaSummaries)}");
});
Check("summaries survive a JSON round trip", () =>
{
    var pull = ReplayTimelineExtractor.FindPulls(Fight(1, 0)).Single();
    var summary = PullSummary.Summarize(pull);
    var back = System.Text.Json.JsonSerializer.Deserialize<PullSummary>(System.Text.Json.JsonSerializer.Serialize(summary))!;
    Require(System.Text.Json.JsonSerializer.Serialize(ReplayTimelineExtractor.Extract([summary])) == System.Text.Json.JsonSerializer.Serialize(ReplayTimelineExtractor.Extract([back])), "round trip changed the extraction");
    Require(back.Start == summary.Start && back.Events.Count == summary.Events.Count, "round trip lost data");
});
Check("summary HP lookup matches the replay", () =>
{
    // The fight's falling HP, and two actors sharing the boss OID where the bigger one is gone at 20 s (as in the primary boss check).
    var gone = Enemy(1, 0x100, 0, 20, 2_000_000);
    gone.HPMPHistory[At(19)] = new(200_000, 2_000_000, 0, 0, 0);
    var boss = Enemy(2, 0x100, 0, 200, 1_000_000);
    boss.HPMPHistory[At(30)] = new(700_000, 1_000_000, 0, 0, 0);
    foreach (var pull in new[] { ReplayTimelineExtractor.FindPulls(Fight(1, 0)).Single(), new ReplayTimelineExtractor.Pull(1000, At(0), At(200), [0x100u], [gone, boss]) })
    {
        var summary = PullSummary.Summarize(pull);
        var known = 0;
        foreach (var s in new[] { -1f, 0f, 3.3f, 10f, 19f, 20f, 20.5f, 30f, 200f, 1000f })
        {
            var hp = ReplayTimelineExtractor.PrimaryBossHPPercent(pull, s);
            Require(hp == PullSummary.PrimaryBossHPPercent(summary, s), $"HP differs at {s}: {hp} vs {PullSummary.PrimaryBossHPPercent(summary, s)}");
            known += hp != null ? 1 : 0;
        }
        Require(known >= 5, $"only {known} probes had an HP");
    }
});
Check("branch round-trips through a user timeline file", () =>
{
    var def = new ExternalPlannerTimeline.TimelineDefinition(1304, "replay:4919", true, [
        new(0, 0, [St(94.6f, ExternalPlannerTimeline.ExternalStateKind.CastStart, 44306)], null, [0x4919], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 100.7f, 127.2f)], new(0, 94.6f, 46.4f, true)),
        new(1, 0, [St(112, ExternalPlannerTimeline.ExternalStateKind.CastStart, 44306)], null, [0x4919], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 118.2f, 144.7f)], new(0, 94.6f, null, false)),
        new(2, 0, [St(5, ExternalPlannerTimeline.ExternalStateKind.CastStart, 1)], null, [0x100])],
        ExternalPlannerTimeline.TimelineSource.Replay, 0.8f);
    var path = Path.Combine(Path.GetTempPath(), $"timeline_regression_branch_{Environment.ProcessId}.json");
    try
    {
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Timelines = new[] { def } }));
        var back = TimelineStore.LoadUserFile(path);
        Require(back.Count == 1 && back[0].Sequences.Count == 3, $"loaded {back.Count} timelines");
        Require(back[0].Sequences[0].Branch == def.Sequences[0].Branch, $"branch with threshold: {back[0].Sequences[0].Branch}");
        Require(back[0].Sequences[1].Branch is { Group: 0, HpThreshold: null, Below: false } b && b.DecisionTime == 94.6f, $"branch without threshold: {back[0].Sequences[1].Branch}");
        Require(back[0].Sequences[2].Branch == null, "a sequence without a branch gained one");
        Require(!System.Text.Json.JsonSerializer.Serialize(def.Sequences[2]).Contains("Branch", StringComparison.Ordinal), "a sequence without a branch writes a Branch key");
    }
    finally
    {
        File.Delete(path);
    }
});

// The follower's branch choice: decision point at 30 s, threshold 50%, 8 s extrapolation limit, 3 pt margin, 1 s cast grace.
int Decide(float now, float? threshold, float hp, float slope, bool seen) => ExternalTimelineHints.DecideBranch(now, 30, threshold, hp, slope, seen, 8, 3, 1);
Check("branch decision: divergence cast, cast grace, then the HP forecast within the extrapolation limit", () =>
{
    Require(Decide(29.5f, 50, 90, 0, true) == 0, "a seen divergence cast picks the early sibling whatever the HP says");
    Require(Decide(10, null, float.NaN, float.NaN, true) == 0, "a seen divergence cast decides without a threshold or HP");
    Require(Decide(31.5f, 50, 50, 0, false) == 1, "grace passed without the divergence cast picks the late sibling");
    Require(Decide(31, 50, 50, 0, false) == -1, "exactly at the end of the grace is still undecided");
    Require(Decide(10, 50, 20, -2, false) == -1, "far before the decision nothing is forecast");
    Require(Decide(21.9f, 50, 56, -2, false) == -1, "just over the extrapolation limit nothing is forecast");
    Require(Decide(22, 50, 56, -2, false) == 0, "8 s out, the forecast (40) is clearly below the threshold");
    Require(Decide(22, 50, 70, 0, false) == 1, "8 s out, the forecast (70) is clearly above the threshold");
    Require(Decide(22, 50, 67, -2, false) == -1, "8 s out, the forecast (51) is within the margin");
    Require(Decide(22, 50, 63, -2, false) == 0, "forecast 47 is exactly the margin below and decides early");
    Require(Decide(22, 50, 69, -2, false) == 1, "forecast 53 is exactly the margin above and decides late");
    Require(Decide(25, 50, 51, 0, false) == -1, "a forecast within the margin stays undecided");
    Require(Decide(25, 50, 48, 0, false) == -1, "a forecast within the margin below stays undecided");
    Require(Decide(29.5f, 50, 48, -4, false) == 0, "before the decision point the forecast extrapolates forward: 48 - 4 * 0.5 = 46, not the current 48");
    Require(Decide(30.75f, 50, 46, -4, false) == -1, "past the decision point HP never decides early: an early pull shows its cast or vanish instead");
    Require(Decide(30.75f, 50, 40, 0, false) == -1, "past the decision point even an HP far below the threshold leaves it undecided");
    Require(Decide(30.75f, 50, 53, 4, false) == 1, "past the decision point the current HP (53) confirms the late sibling, not extrapolated backwards to 50");
    Require(Decide(30.75f, 50, 52, 0, false) == -1, "past the decision point an HP within the margin stays undecided");
    Require(Decide(30.5f, 50, 60, float.NaN, false) == 1, "past the decision point a clearly late HP decides late without a slope");
    Require(Decide(30.5f, 50, float.NaN, float.NaN, false) == -1, "past the decision point no boss HP stays undecided");
    Require(Decide(25, null, 20, -2, false) == -1, "no threshold: HP never decides");
    Require(Decide(31.5f, null, float.NaN, float.NaN, false) == 1, "no threshold: grace still decides late");
    Require(Decide(25, 50, float.NaN, -2, false) == -1, "no boss HP stays undecided");
    Require(Decide(25, 50, 40, float.NaN, false) == -1, "no HP slope stays undecided");
});
Check("downtime selection stops at the window start limit", () =>
{
    var seq = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 20, 40)]);
    var free = ExternalTimelineHints.SelectDowntime(seq, 10, 25, 8.5f, 0.5f);
    Require(Math.Abs(free.LossIn - 10) < 0.01f && Math.Abs(free.ReturnIn - 30) < 0.01f, $"no limit: {free}");
    var before = ExternalTimelineHints.SelectDowntime(seq, 10, 25, 8.5f, 0.5f, 30);
    Require(Math.Abs(before.LossIn - 10) < 0.01f && Math.Abs(before.ReturnIn - 30) < 0.01f, $"a window starting before the limit: {before}");
    Require(ExternalTimelineHints.SelectDowntime(seq, 10, 25, 8.5f, 0.5f, 20).LossIn == float.MaxValue, "a window starting exactly at the limit was published");
    Require(ExternalTimelineHints.SelectDowntime(seq, 10, 25, 8.5f, 0.5f, 15).LossIn == float.MaxValue, "a window starting after the limit was published");
    var straddling = ExternalTimelineHints.SelectDowntime(Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 25, 50)]), 10, 25, 8.5f, 0.5f, 30);
    Require(Math.Abs(straddling.LossIn - 15) < 0.01f, $"a window starting before the limit and ending after it is shared and published: {straddling}");
    var shortThenLong = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 12, 14), Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 36, 60)]);
    Require(Math.Abs(ExternalTimelineHints.SelectDowntime(shortThenLong, 12, 25, 8.5f, 0.5f).LossIn - 24) < 0.01f, "without a limit the long window behind the short one is published");
    Require(ExternalTimelineHints.SelectDowntime(shortThenLong, 12, 25, 8.5f, 0.5f, 30).LossIn == float.MaxValue, "a long window past the limit was published behind a short one");
    var legacy = Seq([St(20, ExternalPlannerTimeline.ExternalStateKind.Untargetable, 0), St(40, ExternalPlannerTimeline.ExternalStateKind.Targetable, 0)]);
    var legacyBefore = ExternalTimelineHints.SelectDowntime(legacy, 10, 25, 8.5f, 0.5f, 30);
    Require(Math.Abs(legacyBefore.LossIn - 10) < 0.01f, $"legacy markers before the limit: {legacyBefore}");
    Require(ExternalTimelineHints.SelectDowntime(legacy, 10, 25, 8.5f, 0.5f, 20).LossIn == float.MaxValue, "legacy markers at the limit were published");
});
Check("divergence states are the early sibling's entries at the decision point that the late sibling does not show", () =>
{
    const ExternalPlannerTimeline.ExternalStateKind cast = ExternalPlannerTimeline.ExternalStateKind.CastStart;
    const ExternalPlannerTimeline.ExternalStateKind ability = ExternalPlannerTimeline.ExternalStateKind.AbilityUsed;
    // Decision point 30 s, early window 36-60 (a 2 s blip at 31 is too short to be it), so the late sibling is checked up to 36 + 6.
    // At the decision point: 0x3000 is the early sibling's own; 0x3001 the late sibling has at 40 (<= 42, shared); 0x3002 the late
    // sibling has only at 50 (> 42, still own); 0x3003 is 0.5 s off the decision point; 0x3004 is a timeout, not observable.
    List<ExternalPlannerTimeline.TimelineState> earlyStates = [St(10, cast, 0x1000), St(30, cast, 0x3001), St(30, cast, 0x3000), St(30.05f, ability, 0x3002),
        St(30.5f, cast, 0x3003), St(30, ExternalPlannerTimeline.ExternalStateKind.Timeout, 0x3004)];
    var early = Seq(earlyStates, [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 31, 33), Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 36, 60)]);
    var late = Seq([St(10, cast, 0x1000), St(40, ability, 0x3001), St(50, cast, 0x3002), St(20, cast, 0x3003)], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 56, 80)]);
    string Ids(List<ExternalPlannerTimeline.TimelineState> states) => string.Join(",", states.Select(s => s.IDs[0].ToString("X")));
    Require(ExternalTimelineHints.EarlyWindowStart(early, 30) == 36, $"early window start {ExternalTimelineHints.EarlyWindowStart(early, 30)}");
    List<ExternalPlannerTimeline.TimelineState> found = [];
    ExternalTimelineHints.CollectDivergenceStates(early, late, 30, found);
    Require(Ids(found) == "3000,3002", $"divergence states {Ids(found)}");
    // Without an early window to bound the check, any occurrence in the late sibling makes the id shared.
    var unbounded = Seq(earlyStates);
    Require(ExternalTimelineHints.EarlyWindowStart(unbounded, 30) == float.MaxValue, "an early window was found where there is none");
    ExternalTimelineHints.CollectDivergenceStates(unbounded, late, 30, found);
    Require(Ids(found) == "3000", $"divergence states without a window {Ids(found)}");
    // A capped decision point (the early window start itself, a hair later than the window) has no divergence state.
    var capped = Seq([St(10, cast, 0x1000)], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 35.98f, 60)]);
    Require(ExternalTimelineHints.EarlyWindowStart(capped, 36) == 35.98f, $"capped early window start {ExternalTimelineHints.EarlyWindowStart(capped, 36)}");
    ExternalTimelineHints.CollectDivergenceStates(capped, late, 36, found);
    Require(found.Count == 0, $"capped divergence states {Ids(found)}");
    // At observation time a late sibling showing the id near the clock disqualifies the match.
    Require(ExternalTimelineHints.LateShowsIdNear(late, 0x3002, 45, 6), "0x3002 at 50 is within 6 s of a 45 s clock");
    Require(!ExternalTimelineHints.LateShowsIdNear(late, 0x3002, 30, 6), "0x3002 at 50 is not within 6 s of a 30 s clock");
    Require(!ExternalTimelineHints.LateShowsIdNear(late, 0x3000, 30, 6), "the late sibling has no 0x3000");
});

foreach (var failure in failures)
    Console.Error.WriteLine(failure);
Console.WriteLine($"tests={tests} passed={tests - failures.Count} failed={failures.Count}; source=synthetic; replay=none");
return failures.Count == 0 ? 0 : 1;
