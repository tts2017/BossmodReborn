using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using BossMod;
using EncounterTimeline;

namespace XanTimelineHarness;

[Flags]
internal enum IrregularKind
{
    None = 0,
    Lockout = 1,     // a status on the player refuses an action category (stun, sleep, down for the count, pacification, silence, amnesia)
    LineOfSight = 2, // the target's Visibility is Blocked, so the queue skips everything that RequiresLineOfSight
    Range = 4,       // knockback / pull: the player is held out of range and only walks back when the hold ends
    Loss = 8,        // the target is untargetable outside the timeline windows, with no hint
    All = Lockout | LineOfSight | Range | Loss
}

// --irregular <seed>: unpredicted interruptions that stop or shift the GCD, independent of --random-disengage. Process-wide state
// mirrors ClientReject: with Seed null every hook returns at once, so default runs stay byte-identical.
internal static class Irregular
{
    public static int? Seed;
    public static float RatePerMinute = 2f;
    public static IrregularKind Kinds = IrregularKind.All;
    public static bool Enabled => Seed != null;
    // irregular-compare runs the baseline through the same range/movement enforcement as the irregular runs, so the only
    // difference between the two is the events themselves
    public static bool EnforceBaseline;
    // oracle-search: keep exactly one episode per fight (see IrregularDriver.KeepSingleEpisode)
    public static bool SingleEpisode;
    public static float SingleEpisodeRecovery = 25f;
    // the episode the last single-episode driver kept (null when none was eligible)
    public static IrregularDriver.Episode? LastSingle;
    // oracle-search validation: rebuild this episode instead of drawing the schedule (see the driver constructor)
    public static IrregularDriver.Episode? ReplayTemplate;
    // burst oracle: rebuild this whole list of episodes (kind, start, length, status, distance) instead of drawing the schedule, so the same schedule can be replayed in a
    // longer fight or with other episode lengths (the seeded schedule depends on the fight length)
    public static List<IrregularDriver.Episode>? ReplayList;
    // the episodes the last driver built (any path), kept for the oracle to copy
    public static IReadOnlyList<IrregularDriver.Episode> LastSchedule = [];
    // the driver of the run in progress; null outside irregular runs
    public static IrregularDriver? Current;

    public static IrregularKind ParseKinds(string text)
    {
        var kinds = IrregularKind.None;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            kinds |= part.ToLowerInvariant() switch
            {
                "lockout" => IrregularKind.Lockout,
                "los" or "lineofsight" => IrregularKind.LineOfSight,
                "range" => IrregularKind.Range,
                "loss" => IrregularKind.Loss,
                "all" => IrregularKind.All,
                _ => throw new ArgumentException($"Unknown irregular kind '{part}' (lockout, los, range, loss, all).")
            };
        }
        return kinds == IrregularKind.None ? throw new ArgumentException("--irregular-kinds needs at least one kind.") : kinds;
    }

    public static string FormatKinds(IrregularKind kinds) => kinds == IrregularKind.All ? "all"
        : string.Join('+', new[] { (IrregularKind.Lockout, "lockout"), (IrregularKind.LineOfSight, "los"), (IrregularKind.Range, "range"), (IrregularKind.Loss, "loss") }.Where(k => kinds.HasFlag(k.Item1)).Select(k => k.Item2));

    // Post-pick refusal, the way the client does it: ActionManager.GetActionStatus fails for a locked category, ActionManagerEx logs
    // "Can't execute ... status" and tries again next frame (ActionManagerEx.cs), so the frame is spent. The job emulators call this
    // after the shared queue chose a candidate, which is where the real client finds out.
    public static bool Refuses(string job, in ActionQueue.Entry entry, ActionDefinition definition, WorldState world, Actor player)
    {
        if (Current == null)
            return false;
        var reason = Current.LockReason(definition, player);
        if (reason == null)
            return false;
        ClientReject.Record(job, entry.Action, world.CurrentTime, player);
        Current.NoteRefusal(world.CurrentTime, reason.Value);
        return true;
    }
}

// Seed-deterministic, unpredicted episodes for one run. Timing, kind and length depend only on the seed and the scenario key, so every
// rotation and route faces the same events. Nothing is announced: no forbidden zones, no forecast, no external hint. The player sees
// exactly what the live game shows - a status on itself, Visibility.Blocked on the target, its own position, or no target.
internal sealed class IrregularDriver
{
    // Status sheet (probed 2026-10-03): 2 Stun / 3 Sleep / 625 Down for the Count carry LockActions with StatusCategory 2 ("Unable to execute
    // actions"), 6 Pacification "Unable to use weaponskills." (ＷＳ不可), 7 Silence "A stifling magic is preventing casts." (沈黙),
    // 1092 Amnesia "Unable to use abilities." (アビリティ不可). Bind (13) and Heavy (14) only touch movement and are not lockouts.
    public enum LockStatus : uint { Stun = 2, Sleep = 3, Pacification = 6, Silence = 7, DownForTheCount = 625, Amnesia = 1092 }

    public enum Reason { AllActions, Weaponskill, Spell, Ability }

    public sealed class Episode(IrregularKind kind, float start, float hold, LockStatus status, float far)
    {
        public readonly IrregularKind Kind = kind;
        public readonly float Start = start;
        public readonly float Hold = hold;          // lockout/los/loss: the whole episode; range: the time the pull holds the player away
        public readonly LockStatus Status = status; // lockout only
        public readonly float Far = far;            // range only: distance from the target's center while held
        public float Nominal;                       // role-independent length used for scheduling (range: hold + 3)
        public float End;                           // when the player can act on the target again (range: back in range after the walk)
        public float FlyEnd;                        // range only: knockback travel ends, the hold begins
        public float HoldEnd;                       // range only: the hold ends, the walk back begins
        public bool Started, Ended;
        public bool BlocksGCD;                      // false for a lockout whose category is not this job's GCD category (Silence on a melee, Amnesia)
        public int Refusals;                        // frames the client refused a pick while this episode was active
        public int GCDsInside;                      // GCDs that still went out during the episode (ranged fillers, self-targets)
        public float ResumeLatency = -1f;           // seconds from End to the first GCD after it; -1 when none came before the next episode / fight end
        public float ResumeExcess = -1f;            // the same minus the recast that was still rolling at End (a filler pressed just before)
        public float GcdRemainingAtEnd;
        public int IdleFrames;                      // GCD-ready frames with a target and nothing pressed, inside the episode
        public int StatusSlot = -1;

        public string Describe()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var detail = Kind switch
            {
                IrregularKind.Lockout => Status.ToString(),
                IrregularKind.Range => string.Create(inv, $"far={Far:f1}y hold_end={HoldEnd:f2}"),
                _ => ""
            };
            return string.Create(inv, $"{KindName(Kind)},{Start:f2},{End:f2},{End - Start:f2},{detail},blocks_gcd={(BlocksGCD ? 1 : 0)},refused={Refusals},gcd_in={GCDsInside},idle_in={IdleFrames},resume={ResumeLatency:f2},resume_excess={ResumeExcess:f2}");
        }
    }

    public readonly record struct Stats(int Episodes, float Seconds, float LockoutSeconds, float LosSeconds, float RangeSeconds, float LossSeconds,
        int IdleIn, int IdleOut, int GCDsInside, float ResumeSum, float ResumeMax, int ResumeCount, int NoResume, int RefusedIn, int RefusedOut, int CastInterrupts,
        float BlockSeconds, float ResumeExcessSum, float ResumeExcessMax)
    {
        public static readonly Stats Empty = default;
        public float ResumeAverage => ResumeCount > 0 ? ResumeSum / ResumeCount : 0f;
        public float ResumeExcessAverage => ResumeCount > 0 ? ResumeExcessSum / ResumeCount : 0f;
        public Stats Add(Stats o) => new(Episodes + o.Episodes, Seconds + o.Seconds, LockoutSeconds + o.LockoutSeconds, LosSeconds + o.LosSeconds, RangeSeconds + o.RangeSeconds, LossSeconds + o.LossSeconds,
            IdleIn + o.IdleIn, IdleOut + o.IdleOut, GCDsInside + o.GCDsInside, ResumeSum + o.ResumeSum, MathF.Max(ResumeMax, o.ResumeMax), ResumeCount + o.ResumeCount, NoResume + o.NoResume, RefusedIn + o.RefusedIn, RefusedOut + o.RefusedOut, CastInterrupts + o.CastInterrupts,
            BlockSeconds + o.BlockSeconds, ResumeExcessSum + o.ResumeExcessSum, MathF.Max(ResumeExcessMax, o.ResumeExcessMax));
        // appended at the end of summary.csv; zeros for runs without the option
        public static string CsvHeader => "irr_episodes,irr_seconds,irr_lockout_s,irr_los_s,irr_range_s,irr_loss_s,irr_idle_in,irr_idle_out,irr_gcd_in,irr_resume_avg,irr_resume_max,irr_no_resume,irr_refused_in,irr_refused_out,irr_cast_interrupts,irr_block_s,irr_resume_excess_avg,irr_resume_excess_max";
        public string Csv() => FormattableString.Invariant($"{Episodes},{Seconds:f2},{LockoutSeconds:f2},{LosSeconds:f2},{RangeSeconds:f2},{LossSeconds:f2},{IdleIn},{IdleOut},{GCDsInside},{ResumeAverage:f3},{ResumeMax:f3},{NoResume},{RefusedIn},{RefusedOut},{CastInterrupts},{BlockSeconds:f2},{ResumeExcessAverage:f3},{ResumeExcessMax:f3}");
        public string Format() => FormattableString.Invariant($"episodes={Episodes} seconds={Seconds:f1} block_s={BlockSeconds:f1} lockout_s={LockoutSeconds:f1} los_s={LosSeconds:f1} range_s={RangeSeconds:f1} loss_s={LossSeconds:f1} idle_in={IdleIn} idle_out={IdleOut} gcd_in={GCDsInside} resume_avg={ResumeAverage:f3} resume_max={ResumeMax:f2} resume_excess_avg={ResumeExcessAverage:f3} resume_excess_max={ResumeExcessMax:f2} no_resume={NoResume} refused_in={RefusedIn} refused_out={RefusedOut} cast_interrupts={CastInterrupts}");
    }

    private const float WalkSpeed = 6f;       // same as DisengageDriver
    private const float KnockbackSpeed = 20f; // a 15y knockback takes about 0.75s in game
    private const float MinimumGapAfter = 2f; // episodes never overlap, so each one's recovery can be attributed
    private const float EndGuard = 3f;        // every episode ends at least this long before the fight ends, so the resume can be seen
    private const float ReturnGuard = 4.5f;   // no episode starts this soon after a timeline target return (resume check 3.5s + a GCD)

    private readonly List<Episode> _episodes = [];
    private readonly Actor _player;
    private readonly Actor _target;
    private readonly IReadOnlyList<Actor> _enemies;
    private readonly bool _ranged;
    private readonly ActionCategory _gcdCategory; // Spell for casters, Weaponskill otherwise: decides which lockouts stop this job's GCD
    private readonly float _baseRange;    // 3 for melee and tanks, 25 for ranged and casters
    private readonly float _hitboxes;     // player + target hitbox radius: the queue's range check adds both
    private readonly WPos _home;
    private readonly Action _interruptCast;
    private WPos _position;
    private WPos _written;
    private int _refusedOutside;
    private int _idleOutside;
    private int _castInterrupts;

    public IReadOnlyList<Episode> Episodes => _episodes;
    public int EventCount => _episodes.Count;
    public bool Active { get; private set; }
    public bool Moving { get; private set; }
    public bool TargetLost { get; private set; }
    private Episode? _current;

    public IrregularDriver(int seed, string scenarioKey, float duration, IReadOnlyList<EventTriggerTimelineWindow> unavailable, float ratePerMinute, IrregularKind kinds,
        Actor player, Actor target, IReadOnlyList<Actor> enemies, bool ranged, ActionCategory gcdCategory, Action interruptCast)
    {
        _player = player;
        _target = target;
        _enemies = enemies;
        _ranged = ranged;
        _gcdCategory = gcdCategory;
        _baseRange = ranged ? 25f : 3f;
        _hitboxes = player.HitboxRadius + target.HitboxRadius;
        _home = _position = _written = player.Position;
        _interruptCast = interruptCast;

        var enabled = new List<IrregularKind>();
        foreach (var kind in new[] { IrregularKind.Lockout, IrregularKind.LineOfSight, IrregularKind.Range, IrregularKind.Loss })
            if (kinds.HasFlag(kind))
                enabled.Add(kind);
        var rng = new Random(StableHash(seed, scenarioKey));
        var mean = 60f / MathF.Max(0.1f, ratePerMinute);
        var start = 5f + mean * (float)rng.NextDouble();
        while (true)
        {
            // always draw the same numbers per event so the sequence depends only on the seed and the scenario
            var kindRoll = rng.NextDouble();
            var lengthRoll = (float)rng.NextDouble();
            var statusRoll = rng.NextDouble();
            var farRoll = (float)rng.NextDouble();
            var gapRoll = (float)rng.NextDouble();
            var kind = enabled[Math.Min(enabled.Count - 1, (int)(kindRoll * enabled.Count))];
            var hold = kind switch
            {
                IrregularKind.Lockout => 0.5f + 7.5f * lengthRoll,
                IrregularKind.Range => 1f + 14f * lengthRoll,
                _ => 1f + 9f * lengthRoll
            };
            var status = statusRoll switch
            {
                < 0.30 => LockStatus.Stun,
                < 0.40 => LockStatus.Sleep,
                < 0.55 => LockStatus.DownForTheCount,
                < 0.70 => LockStatus.Pacification,
                < 0.85 => LockStatus.Silence,
                _ => LockStatus.Amnesia
            };
            // held just outside the job's basic range: melee keeps its 20y fillers, ranged/casters lose everything
            var far = _baseRange + _hitboxes + 2f + 4f * farRoll;
            var episode = new Episode(kind, start, hold, status, far)
            {
                Nominal = kind == IrregularKind.Range ? hold + 3f : hold,
                BlocksGCD = kind != IrregularKind.Lockout || status is LockStatus.Stun or LockStatus.Sleep or LockStatus.DownForTheCount
                    || status == LockStatus.Pacification && gcdCategory == ActionCategory.Weaponskill || status == LockStatus.Silence && gcdCategory == ActionCategory.Spell
            };
            // the schedule itself must not depend on the job: a pull's flight and walk back differ between melee and ranged, so the
            // spacing uses a role-independent bound (fly <= 1.7s, walk <= 1.2s) instead of the actual End
            var nominal = kind == IrregularKind.Range ? hold + 3f : hold;
            if (kind == IrregularKind.Range)
            {
                episode.FlyEnd = start + far / KnockbackSpeed;
                episode.HoldEnd = episode.FlyEnd + hold;
                // the player walks back only until the target is in range and stops there: End is the moment an action (and, for
                // casters, a hard cast) becomes possible, so every job measures its resume from the same instant; melee then walk on
                // to their positional
                episode.End = episode.HoldEnd + (far - RestDistance) / WalkSpeed;
            }
            else
            {
                episode.End = start + hold;
            }
            if (start + nominal + EndGuard > duration)
                break;
            // keep clear of the timeline windows, including the harness's 3.5s resume check after each return: an episode that
            // starts 1s after a return would trip "no GCD within 3.50s after target return" by itself
            if (!Overlaps(unavailable, start - 1f, start + nominal + 4f) && !Overlaps(unavailable, start - ReturnGuard, start))
                _episodes.Add(episode);
            start += MathF.Max(nominal + MinimumGapAfter, mean * (0.5f + gapRoll));
        }
        if (Irregular.ReplayList is { } replayList)
        {
            _episodes.Clear();
            foreach (var source in replayList)
            {
                var copy = new Episode(source.Kind, source.Start, source.Hold, source.Status, source.Far) { Nominal = source.Nominal, BlocksGCD = source.BlocksGCD };
                if (copy.Kind == IrregularKind.Range)
                {
                    copy.FlyEnd = copy.Start + copy.Far / KnockbackSpeed;
                    copy.HoldEnd = copy.FlyEnd + copy.Hold;
                    copy.End = copy.HoldEnd + (copy.Far - RestDistance) / WalkSpeed;
                }
                else
                {
                    copy.End = copy.Start + copy.Hold;
                }
                _episodes.Add(copy);
            }
        }
        else if (Irregular.ReplayTemplate is { } template)
        {
            // oracle-search validation: the same episode (kind, start, length, status, distance) in a fight of a different length, whose seeded schedule would differ
            _episodes.Clear();
            var copy = new Episode(template.Kind, template.Start, template.Hold, template.Status, template.Far) { Nominal = template.Nominal, BlocksGCD = template.BlocksGCD };
            if (copy.Kind == IrregularKind.Range)
            {
                copy.FlyEnd = copy.Start + copy.Far / KnockbackSpeed;
                copy.HoldEnd = copy.FlyEnd + copy.Hold;
                copy.End = copy.HoldEnd + (copy.Far - RestDistance) / WalkSpeed;
            }
            else
            {
                copy.End = copy.Start + copy.Hold;
            }
            _episodes.Add(copy);
            Irregular.LastSingle = copy;
        }
        else if (Irregular.SingleEpisode)
            KeepSingleEpisode(seed, scenarioKey, duration);
        Irregular.LastSchedule = [.. _episodes];
    }

    // oracle-search: a fight carries exactly one episode, so the search can never learn about a later one. The episode is chosen by seed and
    // scenario among those that leave recovery observable (start >= 15% into the fight, over Irregular.SingleEpisodeRecovery seconds before the end);
    // none eligible leaves the fight without episodes and the caller skips it. The choice does not depend on the job: it only uses the nominal length.
    private void KeepSingleEpisode(int seed, string scenarioKey, float duration)
    {
        var eligible = _episodes.Where(e => e.Start >= 0.15f * duration && e.Start + e.Nominal + Irregular.SingleEpisodeRecovery <= duration).ToList();
        _episodes.Clear();
        if (eligible.Count > 0)
            _episodes.Add(eligible[StableHash(seed + 7919, scenarioKey) % eligible.Count]);
        Irregular.LastSingle = _episodes.Count > 0 ? _episodes[0] : null;
    }

    // casters: the resume is the first cast start after an episode, not its completion; the runner reports cast starts frame by frame
    private readonly List<float> _castStarts = [];
    public void NoteCastStart(float time) => _castStarts.Add(time);

    // Distance from the target (centre to centre) at which a pulled player stops walking back: just inside the basic range
    // (the queue's check is strict, so the hair of margin keeps rounding from putting the player one frame out)
    private float RestDistance => _baseRange + _hitboxes - 0.01f;

    // Applies the episode state for this frame: statuses, visibility, position, target loss. Call before the module runs.
    public void Update(WorldState world, float time)
    {
        var finishedRange = _current is { Kind: IrregularKind.Range } && time >= _current.End ? _current : null;
        if (_current != null && time >= _current.End)
        {
            EndEpisode(world, _current);
            _current = null;
        }
        if (_current == null)
        {
            foreach (var episode in _episodes)
            {
                if (episode.Started)
                    continue;
                if (time < episode.Start)
                    break;
                StartEpisode(world, episode);
                _current = episode;
                break;
            }
        }

        Active = _current != null;
        TargetLost = _current is { Kind: IrregularKind.Loss };
        Moving = false;
        if (_current is { Kind: IrregularKind.Range } range)
        {
            var direction = (_home - _target.Position).Normalized();
            if (time < range.FlyEnd)
            {
                var progress = Math.Clamp((time - range.Start) / (range.FlyEnd - range.Start), 0f, 1f);
                _position = _home + direction * ((range.Far - (_home - _target.Position).Length()) * progress);
                Moving = true;
            }
            else if (time < range.HoldEnd)
            {
                _position = _target.Position + direction * range.Far;
            }
            else
            {
                _position = _target.Position + direction * (range.Far - WalkSpeed * (time - range.HoldEnd));
                Moving = true;
            }
        }
        else if (finishedRange != null && _ranged)
        {
            // the frame that crosses End: the walk's last step is analytic, so the player arrives exactly at End instead of one frame later
            _position = _target.Position + (_home - _target.Position).Normalized() * RestDistance;
        }
        else if ((_position - _home).LengthSq() > 0.0001f)
        {
            // after a pull the melee walks all the way home (same positional as the baseline); ranged stop where the walk ended
            var direction = (_home - _target.Position).Normalized();
            var rest = _ranged ? _target.Position + direction * RestDistance : _home;
            if ((_position - rest).LengthSq() > 0.0001f && (_position - _target.Position).Length() > (rest - _target.Position).Length())
            {
                var step = WalkSpeed * 0.05f;
                var toRest = rest - _position;
                _position = toRest.Length() <= step ? rest : _position + toRest.Normalized() * step;
                Moving = true;
            }
        }
        if ((_position - _written).LengthSq() > 0f)
        {
            world.Execute(new ActorState.OpMove(_player.InstanceID, new Vector4(_position.X, _player.PosRot.Y, _position.Z, _player.PosRot.W)));
            _written = _position;
        }
    }

    private void StartEpisode(WorldState world, Episode episode)
    {
        episode.Started = true;
        switch (episode.Kind)
        {
            case IrregularKind.Lockout:
                // the status is inflicted by the boss and expires on its own, exactly as the client would show it
                var slot = Array.FindIndex(_player.Statuses, s => s.ID == 0);
                if (slot < 0)
                    throw new InvalidOperationException("Irregular driver ran out of status slots.");
                episode.StatusSlot = slot;
                world.Execute(new ActorState.OpStatus(_player.InstanceID, slot, new((uint)episode.Status, 0, world.CurrentTime.AddSeconds(episode.End - episode.Start), _target.InstanceID)));
                // a cast of the locked category is interrupted the way a knockback interrupts it (recast refunded)
                if (_player.CastInfo is { } cast && ActionDefinitions.Instance[cast.Action] is { } casting && LockReasonFor(episode.Status, casting) != null)
                {
                    ++_castInterrupts;
                    _interruptCast();
                }
                break;
            case IrregularKind.LineOfSight:
                foreach (var enemy in _enemies)
                    world.Execute(new ActorState.OpVisibility(enemy.InstanceID, Visibility.Blocked));
                break;
        }
    }

    private void EndEpisode(WorldState world, Episode episode)
    {
        episode.Ended = true;
        episode.GcdRemainingAtEnd = MathF.Max(0f, world.Client.Cooldowns[ActionDefinitions.GCDGroup].Remaining);
        switch (episode.Kind)
        {
            case IrregularKind.Lockout:
                // the emulators already clear statuses whose ExpireAt passed; clear only if ours is still there
                if (episode.StatusSlot >= 0 && _player.Statuses[episode.StatusSlot].ID == (uint)episode.Status)
                    world.Execute(new ActorState.OpStatus(_player.InstanceID, episode.StatusSlot, default));
                break;
            case IrregularKind.LineOfSight:
                foreach (var enemy in _enemies)
                    world.Execute(new ActorState.OpVisibility(enemy.InstanceID, Visibility.Unknown)); // the harness default
                break;
        }
    }

    // Which lock refuses this action right now, or null when the client would accept it.
    public Reason? LockReason(ActionDefinition definition, Actor player)
    {
        foreach (ref readonly var status in player.Statuses.AsSpan())
        {
            if (status.ID == 0)
                continue;
            var reason = LockReasonFor((LockStatus)status.ID, definition);
            if (reason != null)
                return reason;
        }
        return null;
    }

    private static Reason? LockReasonFor(LockStatus status, ActionDefinition definition) => status switch
    {
        LockStatus.Stun or LockStatus.Sleep or LockStatus.DownForTheCount => Reason.AllActions,
        LockStatus.Pacification when definition.Category == ActionCategory.Weaponskill => Reason.Weaponskill,
        LockStatus.Silence when definition.Category == ActionCategory.Spell => Reason.Spell,
        LockStatus.Amnesia when definition.Category == ActionCategory.Ability => Reason.Ability,
        _ => null
    };

    public void NoteRefusal(DateTime now, Reason reason)
    {
        if (_current != null)
            ++_current.Refusals;
        else
            ++_refusedOutside;
    }

    // a GCD-ready frame with a target and nothing pressed (the runner's gcd_idle condition), split by episode
    public void NoteIdle()
    {
        if (_current != null)
            ++_current.IdleFrames;
        else
            ++_idleOutside;
    }

    // Fills the per-episode resume figures from the executed actions and sums everything up.
    public Stats Finish(IEnumerable<(float Time, bool GCD)> actions, float duration)
    {
        // instants are recorded when pressed; casts when they complete, so their starts are merged in for the resume figure
        var gcds = actions.Where(a => a.GCD).Select(a => a.Time).Concat(_castStarts).OrderBy(t => t).ToArray();
        var stats = new Stats(0, 0, 0, 0, 0, 0, 0, _idleOutside, 0, 0, 0, 0, 0, 0, _refusedOutside, _castInterrupts, 0, 0, 0);
        for (var i = 0; i < _episodes.Count; ++i)
        {
            var e = _episodes[i];
            if (!e.Started)
                continue;
            e.GCDsInside = gcds.Count(t => t >= e.Start - 0.001f && t < e.End - 0.001f);
            var limit = i + 1 < _episodes.Count ? _episodes[i + 1].Start : duration;
            var first = gcds.FirstOrDefault(t => t >= e.End - 0.001f, float.NaN);
            var resumed = !float.IsNaN(first) && first < limit;
            e.ResumeLatency = resumed ? first - e.End : -1f;
            e.ResumeExcess = resumed ? MathF.Max(0f, e.ResumeLatency - e.GcdRemainingAtEnd) : -1f;
            var seconds = e.End - e.Start;
            stats = stats with
            {
                Episodes = stats.Episodes + 1,
                Seconds = stats.Seconds + seconds,
                BlockSeconds = stats.BlockSeconds + (e.BlocksGCD ? seconds : 0),
                ResumeExcessSum = stats.ResumeExcessSum + (resumed ? e.ResumeExcess : 0),
                ResumeExcessMax = resumed ? MathF.Max(stats.ResumeExcessMax, e.ResumeExcess) : stats.ResumeExcessMax,
                LockoutSeconds = stats.LockoutSeconds + (e.Kind == IrregularKind.Lockout ? seconds : 0),
                LosSeconds = stats.LosSeconds + (e.Kind == IrregularKind.LineOfSight ? seconds : 0),
                RangeSeconds = stats.RangeSeconds + (e.Kind == IrregularKind.Range ? seconds : 0),
                LossSeconds = stats.LossSeconds + (e.Kind == IrregularKind.Loss ? seconds : 0),
                IdleIn = stats.IdleIn + e.IdleFrames,
                GCDsInside = stats.GCDsInside + e.GCDsInside,
                RefusedIn = stats.RefusedIn + e.Refusals,
                ResumeSum = stats.ResumeSum + (resumed ? e.ResumeLatency : 0),
                ResumeMax = resumed ? MathF.Max(stats.ResumeMax, e.ResumeLatency) : stats.ResumeMax,
                ResumeCount = stats.ResumeCount + (resumed ? 1 : 0),
                // a missing resume only counts when there was room for one before the next episode or the end
                NoResume = stats.NoResume + (!resumed && limit - e.End >= EndGuard ? 1 : 0)
            };
        }
        return stats;
    }

    public static string KindName(IrregularKind kind) => kind switch
    {
        IrregularKind.Lockout => "lockout",
        IrregularKind.LineOfSight => "los",
        IrregularKind.Range => "range",
        IrregularKind.Loss => "loss",
        _ => kind.ToString()
    };

    private static bool Overlaps(IReadOnlyList<EventTriggerTimelineWindow> windows, float start, float end)
    {
        foreach (var window in windows)
            if (window.Start < end && window.End > start)
                return true;
        return false;
    }

    // FNV-1a over a seed and the scenario key (string.GetHashCode is randomized per process); a different salt from DisengageDriver so
    // the two drivers never draw the same stream
    private static int StableHash(int seed, string key)
    {
        var hash = 0x9E3779B9u ^ (uint)seed;
        foreach (var c in key)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return (int)(hash & 0x7FFFFFFF);
    }
}
