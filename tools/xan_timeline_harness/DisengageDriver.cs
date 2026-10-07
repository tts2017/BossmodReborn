using System;
using System.Collections.Generic;
using System.Numerics;
using BossMod;
using EncounterTimeline;

namespace XanTimelineHarness;

// Random telegraphed mechanics that are not on the fight timeline (chariots, baited puddles, stacks away from the boss), used to
// measure how rotations cope with short disengages. Events depend only on the seed and the scenario, never on the rotation, so runs
// with and without the disengage forecast face the same mechanics. The player dodges as late as possible in a straight line at
// move speed, like a player who reads the telegraph, and melee players walk back to the target once the mechanic resolves.
internal sealed class DisengageDriver
{
    private enum Kind { Chariot, Puddle, FarStack }

    private sealed class Event(Kind kind, float telegraph, float activation, float radius, float angle, float distance)
    {
        public readonly Kind Kind = kind;
        public readonly float Telegraph = telegraph;
        public readonly float Activation = activation;
        public readonly float Radius = radius;
        public readonly float Angle = angle;
        public readonly float Distance = distance;
        public ShapeDistance? Shape; // fixed when telegraphed; puddles are baited on the player position at that moment
    }

    private const float Speed = 6f;
    private const float DodgeMargin = 0.3f; // start moving this long before the last possible moment
    private const float SafetyCushion = 0.6f;
    private const float ArenaHalfSize = 28f; // stay inside the default 30y pathfinding square
    private const int Directions = 24;

    private readonly List<Event> _events = [];
    private readonly bool _melee;
    private readonly WPos _home;
    private WPos _position;
    private WPos _from;
    private WPos _to;
    private float _moveStart;
    private float _moveDuration = -1f; // negative while standing still
    private int _next; // first event that has not resolved yet

    public int EventCount => _events.Count;
    public int MovingFrames { get; private set; }

    public DisengageDriver(int seed, string scenarioKey, float duration, IReadOnlyList<EventTriggerTimelineWindow> unavailable, bool melee, WPos home)
    {
        _melee = melee;
        _home = _position = home;
        var rng = new Random(StableHash(seed, scenarioKey));
        var telegraph = 12f + 10f * (float)rng.NextDouble();
        while (true)
        {
            // always draw the same numbers per event so the sequence only depends on the seed and the scenario
            var lead = 3f + 4f * (float)rng.NextDouble();
            var roll = rng.NextDouble();
            var size = rng.Next(3);
            var angle = 2f * MathF.PI * (float)rng.NextDouble();
            var distance = 12f + 6f * (float)rng.NextDouble();
            var gap = 15f + 20f * (float)rng.NextDouble();
            var activation = telegraph + lead;
            if (activation + 6f > duration)
                break;
            var kind = roll < 0.4 ? Kind.Chariot : roll < 0.75 ? Kind.Puddle : Kind.FarStack;
            var radius = kind switch
            {
                Kind.Chariot => 8f + 2f * size,
                Kind.Puddle => 4f + size,
                _ => 3f
            };
            if (!Overlaps(unavailable, telegraph - 1f, activation + 6f))
                _events.Add(new(kind, telegraph, activation, radius, angle, distance));
            telegraph = activation + gap;
        }
    }

    // Moves the player for this frame and returns whether the player is moving.
    public bool Update(WorldState world, Actor player, Actor target, float time)
    {
        for (var i = _next; i < _events.Count; ++i)
        {
            var e = _events[i];
            if (time < e.Telegraph)
                break;
            e.Shape ??= e.Kind switch
            {
                Kind.Chariot => new SDCircle(target.Position, e.Radius),
                Kind.Puddle => new SDCircle(_position, e.Radius),
                _ => new SDInvertedCircle(target.Position + e.Distance * e.Angle.Radians().ToDirection(), e.Radius)
            };
        }
        while (_next < _events.Count && time >= _events[_next].Activation)
            ++_next;

        if (_moveDuration >= 0f)
        {
            var progress = _moveDuration > 0f ? Math.Clamp((time - _moveStart) / _moveDuration, 0f, 1f) : 1f;
            _position = _from + (_to - _from) * progress;
            if (progress >= 1f)
                _moveDuration = -1f;
        }

        if (_moveDuration < 0f)
        {
            var current = _next < _events.Count && _events[_next].Shape != null ? _events[_next] : null;
            if (current != null)
            {
                if (current.Shape!.Distance(_position) <= SafetyCushion)
                {
                    var destination = FindDestination(current.Shape, target);
                    var travel = (destination - _position).Length() / Speed;
                    if (time >= current.Activation - travel - DodgeMargin)
                        StartMove(destination, time, travel);
                }
            }
            else if (_melee && (_position - _home).LengthSq() > 0.01f)
            {
                StartMove(_home, time, (_home - _position).Length() / Speed);
            }
        }

        world.Execute(new ActorState.OpMove(player.InstanceID, new Vector4(_position.X, player.PosRot.Y, _position.Z, player.PosRot.W)));
        var moving = _moveDuration >= 0f;
        if (moving)
            ++MovingFrames;
        return moving;
    }

    // Adds the telegraphed, unresolved mechanics to the hints the way a boss module would.
    public void AddZones(AIHints hints, WorldState world, float time)
    {
        for (var i = _next; i < _events.Count; ++i)
        {
            var e = _events[i];
            if (e.Shape == null)
                break;
            hints.AddForbiddenZone(e.Shape, world.FutureTime(e.Activation - time));
        }
    }

    private void StartMove(WPos destination, float time, float travel)
    {
        _from = _position;
        _to = destination;
        _moveStart = time;
        _moveDuration = travel;
    }

    private WPos FindDestination(ShapeDistance shape, Actor target)
    {
        // melee players keep the target in range when some safe spot allows it
        if (_melee)
        {
            var best = _position;
            var bestDistSq = float.MaxValue;
            var inner = target.HitboxRadius + 0.5f;
            for (var ring = 0; ring < 3; ++ring)
            {
                var radius = inner + 0.5f + ring;
                for (var k = 0; k < Directions; ++k)
                {
                    var candidate = target.Position + radius * (2f * MathF.PI * k / Directions).Radians().ToDirection();
                    var distSq = (candidate - _position).LengthSq();
                    if (distSq < bestDistSq && Safe(shape, candidate))
                    {
                        best = candidate;
                        bestDistSq = distSq;
                    }
                }
            }
            if (bestDistSq < float.MaxValue)
                return best;
        }

        // otherwise the closest safe spot around the player, preferring spots near the target on ties
        for (var r = 1; r <= 40; ++r)
        {
            var best = _position;
            var bestTargetDistSq = float.MaxValue;
            for (var k = 0; k < Directions; ++k)
            {
                var candidate = _position + r * (2f * MathF.PI * k / Directions).Radians().ToDirection();
                var targetDistSq = (candidate - target.Position).LengthSq();
                if (targetDistSq < bestTargetDistSq && Safe(shape, candidate))
                {
                    best = candidate;
                    bestTargetDistSq = targetDistSq;
                }
            }
            if (bestTargetDistSq < float.MaxValue)
                return best;
        }
        return _position;
    }

    private static bool Safe(ShapeDistance shape, WPos position)
        => shape.Distance(position) > SafetyCushion && Math.Abs(position.X) <= ArenaHalfSize && Math.Abs(position.Z) <= ArenaHalfSize;

    private static bool Overlaps(IReadOnlyList<EventTriggerTimelineWindow> windows, float start, float end)
    {
        foreach (var window in windows)
            if (window.Start < end && window.End > start)
                return true;
        return false;
    }

    // FNV-1a: string.GetHashCode is randomized per process, which would change the events between runs
    private static int StableHash(int seed, string key)
    {
        var hash = 2166136261u ^ (uint)seed;
        foreach (var c in key)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return (int)(hash & 0x7FFFFFFF);
    }
}
