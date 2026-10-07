namespace BossMod;

// Short-horizon forecast of disengages that are not on the fight timeline: random AOEs, baited puddles, stacks or spreads away
// from the boss and so on. Boss modules publish these as forbidden zones with activation times as soon as they are telegraphed;
// the forecast turns them into timings a rotation can plan around, the way a player reads a cast bar or a marker.
// All values are seconds from now; float.MaxValue means nothing is predicted.
// - ForcedMoveIn: latest time to start moving and still leave every zone that will hit the current position.
// - ForcedMoveFor: how long that escape takes at the current move speed.
// - TargetLossIn: time until no safe position within the main attack range of the job remains around the target.
// - TargetReturnIn: time until such a position exists again (float.MaxValue if not within the horizon).
public readonly record struct DisengageForecast(float ForcedMoveIn, float ForcedMoveFor, float TargetLossIn, float TargetReturnIn)
{
    public static readonly DisengageForecast None = new(float.MaxValue, 0f, float.MaxValue, float.MaxValue);

    public bool Any => ForcedMoveIn < float.MaxValue || TargetLossIn < float.MaxValue;
}

public static class DisengageForecaster
{
    public const string HintNamespace = ExternalMechanicHintProvider.ForecastNamespace;
    public const float Horizon = 15f;
    private const float TimeStep = 0.1f;
    private const int Steps = 151; // Horizon / TimeStep + 1
    private const float ZoneCushion = 0.5f; // positions this close to a zone border count as inside
    private const float ReactionMargin = 0.3f; // leave slightly before the last possible moment
    private const float RangeMargin = 0.3f;
    private const float MeleeRange = 3f;
    private const float RangedRange = 25f;
    private const int RingSamples = 16;
    private static readonly float[] RingFractions = [0.35f, 0.7f, 0.95f];
    private const int MaxSamples = 1 + RingSamples * 3;
    // Shorter range losses are dodges, not downtime: rotations treat a mechanic hint target loss like a boss jump (holding bursts and
    // gauge spenders), which costs damage for a two-second step out. Only longer losses are published; hints.Disengage keeps all of them.
    public const float MinPublishedTargetLoss = 8.5f;

    private static bool _published;

    // Computes the forecast for the player and stores it in the hints. With capCastTime, the action queue stops starting casts that
    // cannot finish before the forced move (manual presses go through the same queue, so callers pass false when no rotation runs).
    // Long target losses are also published as a mechanic hint, see Publish.
    public static DisengageForecast Apply(WorldState ws, AIHints hints, Actor? player, bool capCastTime = true)
    {
        var forecast = player != null ? Compute(ws, hints, player, ResolveTarget(hints, player)) : DisengageForecast.None;
        hints.Disengage = forecast;
        hints.FightRemaining = hints.FightRemaining.WithDowntime(forecast.TargetLossIn, forecast.TargetReturnIn);
        if (capCastTime && forecast.ForcedMoveIn < float.MaxValue)
            hints.MaxCastTime = Math.Min(hints.MaxCastTime, forecast.ForcedMoveIn);
        Publish(ws, forecast);
        return forecast;
    }

    // Removes the published mechanic hint (feature disabled, or plugin shutting down).
    public static void Reset()
    {
        if (_published)
        {
            ExternalMechanicHintProvider.ClearNamespace(HintNamespace);
            _published = false;
        }
    }

    // The current target of the player if the hints consider it an enemy, otherwise the highest priority potential target.
    public static Actor? ResolveTarget(AIHints hints, Actor player)
    {
        Actor? best = null;
        var targets = hints.PotentialTargets;
        var count = targets.Count;
        for (var i = 0; i < count; ++i)
        {
            var enemy = targets[i];
            if (enemy.Actor.InstanceID == player.TargetID)
                return enemy.Actor;
            if (best == null && enemy.Priority >= 0)
                best = enemy.Actor;
        }
        return best;
    }

    public static DisengageForecast Compute(WorldState ws, AIHints hints, Actor player, Actor? target)
    {
        var zones = hints.ForbiddenZones;
        var count = zones.Count;
        if (count == 0)
            return DisengageForecast.None;

        // seconds until each zone hits: -1 for zones that are already active, NaN for zones beyond the horizon
        var now = ws.CurrentTime;
        Span<float> hitIn = count <= 128 ? stackalloc float[count] : new float[count];
        var anyUpcoming = false;
        for (var i = 0; i < count; ++i)
        {
            var activation = zones[i].activation;
            var t = activation == default ? 0f : (float)(activation - now).TotalSeconds;
            if (t <= 0f)
            {
                hitIn[i] = -1f;
            }
            else if (t > Horizon)
            {
                hitIn[i] = float.NaN;
            }
            else
            {
                hitIn[i] = t;
                anyUpcoming = true;
            }
        }
        // only telegraphed mechanics are forecast; standing in active zones is handled by the movement logic
        if (!anyUpcoming)
            return DisengageForecast.None;

        var speed = Math.Max(ws.Client.MoveSpeed, 1f);
        var (moveIn, moveFor) = ForecastForcedMove(zones, hitIn, player.Position, speed);
        var (lossIn, returnIn) = target != null ? ForecastTargetLoss(hints, zones, hitIn, player, target, speed) : (float.MaxValue, float.MaxValue);
        return new(moveIn, moveFor, lossIn, returnIn);
    }

    private static (float In, float For) ForecastForcedMove(List<(ShapeDistance shapeDistance, DateTime activation, ulong Source)> zones, ReadOnlySpan<float> hitIn, WPos position, float speed)
    {
        var moveIn = float.MaxValue;
        var moveFor = 0f;
        for (var i = 0; i < hitIn.Length; ++i)
        {
            var t = hitIn[i];
            if (!(t > 0f))
                continue;
            var d = zones[i].shapeDistance.Distance(position);
            if (d > ZoneCushion)
                continue;
            var escape = (Math.Max(-d, 0f) + ZoneCushion) / speed;
            var leaveIn = t - escape - ReactionMargin;
            if (leaveIn < moveIn)
            {
                moveIn = leaveIn;
                moveFor = escape;
            }
        }
        return moveIn < float.MaxValue ? (Math.Max(moveIn, 0f), moveFor) : (float.MaxValue, 0f);
    }

    private static (float LossIn, float ReturnIn) ForecastTargetLoss(AIHints hints, List<(ShapeDistance shapeDistance, DateTime activation, ulong Source)> zones, ReadOnlySpan<float> hitIn, Actor player, Actor target, float speed)
    {
        if (target == player || target.IsDead || !target.IsTargetable)
            return (float.MaxValue, float.MaxValue);

        // sample positions from which the target is within the main attack range of the job
        var range = player.Class.GetRole() is Role.Tank or Role.Melee ? MeleeRange : RangedRange;
        var reach = target.HitboxRadius + player.HitboxRadius + range - RangeMargin;
        var center = target.Position;
        var bounds = hints.PathfindMapBounds;
        var boundsCenter = hints.PathfindMapCenter;
        Span<WPos> samples = stackalloc WPos[MaxSamples];
        var n = 0;
        var playerPos = player.Position;
        if ((playerPos - center).LengthSq() <= reach * reach)
            samples[n++] = playerPos;
        for (var r = 0; r < RingFractions.Length; ++r)
        {
            var radius = reach * RingFractions[r];
            for (var k = 0; k < RingSamples; ++k)
            {
                var angle = (k + 0.5f * r) * (2f * MathF.PI / RingSamples);
                var sample = center + radius * angle.Radians().ToDirection();
                if (bounds.Contains(sample - boundsCenter))
                    samples[n++] = sample;
            }
        }

        // free[step]: some sampled position is safe at that time
        Span<bool> free = stackalloc bool[Steps];
        Span<bool> blocked = stackalloc bool[Steps];
        free.Clear();
        var anyStaticFree = false;
        for (var j = 0; j < n; ++j)
        {
            var sample = samples[j];
            blocked.Clear();
            var staticBlocked = false;
            for (var i = 0; i < hitIn.Length; ++i)
            {
                var t = hitIn[i];
                if (float.IsNaN(t))
                    continue;
                var d = zones[i].shapeDistance.Distance(sample);
                if (d > ZoneCushion)
                    continue;
                if (t < 0f)
                {
                    staticBlocked = true; // an active zone covers this position, with no known end
                    break;
                }
                // a player standing here has to leave before the hit and can come back once it resolves
                var escape = (Math.Max(-d, 0f) + ZoneCushion) / speed + ReactionMargin;
                var from = Math.Max((int)MathF.Floor((t - escape) / TimeStep), 0);
                var to = Math.Min((int)MathF.Ceiling((t + escape) / TimeStep), Steps - 1);
                for (var s = from; s <= to; ++s)
                    blocked[s] = true;
            }
            if (staticBlocked)
                continue;
            anyStaticFree = true;
            for (var s = 0; s < Steps; ++s)
                free[s] |= !blocked[s];
        }

        // if active zones alone already rule out every in-range position, there is nothing to forecast: the rotation sees it now
        if (!anyStaticFree)
            return (float.MaxValue, float.MaxValue);

        var lossStep = free.IndexOf(false);
        if (lossStep < 0)
            return (float.MaxValue, float.MaxValue);
        var returnOffset = free[(lossStep + 1)..].IndexOf(true);
        return (lossStep * TimeStep, returnOffset < 0 ? float.MaxValue : (lossStep + 1 + returnOffset) * TimeStep);
    }

    // Only long target losses go to the shared mechanic hint. Rotations read mechanic hints as rare, significant events (boss jumps,
    // Splatoon warnings): BLM holds Ley Lines for any forced move within 6 s, RPR holds bursts before any target loss. Dodges happen every
    // few dozen seconds, so publishing them would keep tripping those rules; the cast cap and hints.Disengage cover dodges instead.
    private static void Publish(WorldState ws, in DisengageForecast forecast)
    {
        var longLoss = forecast.TargetLossIn < float.MaxValue && forecast.TargetReturnIn - forecast.TargetLossIn >= MinPublishedTargetLoss;
        if (!longLoss)
        {
            Reset();
            return;
        }
        var zone = ws.CurrentZone;
        var cfc = ws.CurrentCFCID;
        _published |= ExternalMechanicHintProvider.PushSnapshot(new(HintNamespace, zone, cfc, ExternalZoneConfidence.NativeBossMod, ws.FutureTime(0.5d),
            float.MaxValue, forecast.TargetLossIn, forecast.TargetReturnIn, float.MaxValue, false), zone, cfc, ws.CurrentTime);
    }
}
