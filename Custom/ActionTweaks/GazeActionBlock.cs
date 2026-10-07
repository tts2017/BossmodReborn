namespace BossMod;

// Blocks the auto-queued action when executing it would require facing into an imminent gaze and no safe facing
// within 45 degrees of the target exists (the old fork's SmartRotationTweak.ShouldBlockActionForGaze, applied from
// CustomPlugin.SelectAutoQueue instead of inside ActionManagerEx).
public sealed class GazeActionBlock(WorldState ws, AIHints hints)
{
    private static readonly SmartRotationConfig _config = Service.Config.Get<SmartRotationConfig>();
    private static readonly Angle _minWindow = 5f.Degrees();
    private static readonly Angle _preferredHalfWidth = 45f.Degrees();
    private readonly SmartRotationTweak _orientation = new(ws, hints); // used only for its stateless GetSpellOrientation
    private readonly DisjointSegmentList _forbidden = new();

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "NormalizeActionForQueue")]
    private static extern ActionID NormalizeActionForQueue(ActionManagerEx amex, ActionID action);

    public bool ShouldBlock(ActionManagerEx amex, in ActionQueue.Entry entry, Actor player)
    {
        if (!entry.Action || entry.Priority >= ActionQueue.Priority.ManualEmergency)
            return false;
        var aiEnabled = AI.AIManager.Instance?.Beh != null || Autorotation.MiscAI.NormalMovement.Instance != null;
        if (!_config.AvoidGazes && !aiEnabled)
            return false;

        var action = NormalizeActionForQueue(amex, entry.Action);
        var targetIsSelf = entry.Target?.InstanceID == player.InstanceID;
        var ideal = _orientation.GetSpellOrientation(amex.GetSpellIdForAction(action), player.Position, targetIsSelf, entry.Target?.Position, new WPos(entry.TargetPos.XZ()));
        if (ideal == null)
            return false;

        return BuildForbiddenDirections(ideal.Value, ws.FutureTime(_config.MinTimeToAvoid)) && !HasSafePreferredDirection();
    }

    private bool BuildForbiddenDirections(Angle midpoint, DateTime deadline)
    {
        _forbidden.Clear();
        foreach (var d in hints.ForbiddenDirections)
        {
            if (d.activation > deadline)
                continue;
            var center = (d.center - midpoint).Normalized();
            var min = center - d.halfWidth;
            if (min.Rad < -MathF.PI)
            {
                _forbidden.Add(min.Rad + Angle.DoublePI, MathF.PI);
                min = -MathF.PI.Radians();
            }
            var max = center + d.halfWidth;
            if (max.Rad > MathF.PI)
            {
                _forbidden.Add(-MathF.PI, max.Rad - Angle.DoublePI);
                max = MathF.PI.Radians();
            }
            _forbidden.Add(min.Rad, max.Rad);
        }
        return _forbidden.Count != 0;
    }

    private bool HasSafePreferredDirection()
    {
        var coneMin = -_preferredHalfWidth.Rad;
        var coneMax = _preferredHalfWidth.Rad;
        var intersection = _forbidden.Intersect(coneMin, coneMax);
        if (intersection.count == 0)
            return true;
        var previousMax = coneMin;
        for (var i = 0; i < intersection.count; ++i)
        {
            var segment = _forbidden[intersection.first + i];
            if (Math.Min(segment.Min, coneMax) - previousMax >= _minWindow.Rad)
                return true;
            previousMax = Math.Max(previousMax, segment.Max);
        }
        return coneMax - Math.Min(previousMax, coneMax) >= _minWindow.Rad;
    }
}
