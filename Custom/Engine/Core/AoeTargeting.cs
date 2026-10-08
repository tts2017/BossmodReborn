using System;

namespace BossMod.Autorotation.Engine;

// How the adapter chooses the target of an AoE skill (the UI Targeting setting, as the xan modules read it):
//  Manual      - the player's target only;
//  Auto        - the enemy in range whose shape hits the most priority targets;
//  AutoPrimary - as Auto, but only enemies whose shape also hits the player's target (never leaves it).
// AutoTryPrimary (AutoPrimary while the player's target is within the shape's range, else Auto) is resolved by the adapter per shape.
public enum TargetMode : byte { Manual, Auto, AutoPrimary }

// the AOE strategy track as the target scorer sees it (xan AOEStrategy: AOE / ST / ForceST / ForceAOE)
public enum AoeSetting : byte { Auto, SingleTarget, ForceSingleTarget, ForceAoe }

// an enemy as the scorer sees it: position and hitbox radius, and the hint flags the old modules' scorer reads.
//  Forbidden:   a forbidden target (priority <= undesirable): a shape that would hit it scores 0;
//  Undesirable: the player's target is out of combat (undesirable): hitting it alone still counts 1;
//  Counted:     a priority target or a forbidden one (false for a player's target in neither list: it is a candidate but hits nothing extra)
public readonly record struct AoeCandidate(float X, float Z, float Radius, bool Forbidden = false, bool Undesirable = false, bool Counted = true);

// Pure geometry and target scoring (no BossMod dependency): the same circle / rectangle / cone tests as AIHints.TargetInAOE* and the
// same scoring as Basexan.AOETargetScorer / FindBetterTargetByScorer, so the [Engine] modules choose the targets the old modules chose.
public static class AoeTargeting
{
    // whether the enemy circle (ox, oz, oRadius) is inside `shape` used by the player at (px, pz) on the candidate target at (cx, cz)
    public static bool InShape(in ShapeDef shape, float px, float pz, float cx, float cz, float ox, float oz, float oRadius)
    {
        switch (shape.Kind)
        {
            case AoeShape.SelfCircle:
                return InCircle(ox - px, oz - pz, shape.Size + oRadius);
            case AoeShape.TargetCircle:
                return InCircle(ox - cx, oz - cz, shape.Size + oRadius);
            case AoeShape.Line:
            {
                Direction(px, pz, cx, cz, out var dx, out var dz);
                var half = shape.Size * 0.5f;
                return CircleRect(ox - (px + dx * half), oz - (pz + dz * half), oRadius, dx, dz, shape.Width, half);
            }
            case AoeShape.Cone:
            {
                Direction(px, pz, cx, cz, out var dx, out var dz);
                return CircleCone(ox - px, oz - pz, oRadius, shape.Size, dx, dz, shape.Width * (MathF.PI / 180));
            }
            default:
                return false;
        }
    }

    // whether the candidate may be aimed at: within the shape's range of the player (hitbox and the old modules' 0.5 y slack included)
    public static bool InRange(in ShapeDef shape, float px, float pz, in AoeCandidate c) => InCircle(c.X - px, c.Z - pz, shape.Range + c.Radius + 0.5f);

    // the old modules' AdjustNumTargets: the AOE setting applied to a hit count
    public static int Adjust(AoeSetting aoe, int reported) => reported == 0 ? 0 : aoe switch
    {
        AoeSetting.Auto => reported,
        AoeSetting.SingleTarget => 1,
        AoeSetting.ForceAoe => 10,
        _ => 0,
    };

    // how many priority targets the shape aimed at enemies[candidate] hits (0 when it would hit a forbidden target, at least 1 otherwise:
    // a targeted action always hits its target), after the AOE setting
    public static int Hits(in ShapeDef shape, AoeSetting aoe, float px, float pz, ReadOnlySpan<AoeCandidate> enemies, int candidate, int primary)
    {
        var c = enemies[candidate];
        int forbidden = 0, ok = 0;
        for (var i = 0; i < enemies.Length; ++i)
        {
            var e = enemies[i];
            if (!e.Counted || !InShape(shape, px, pz, c.X, c.Z, e.X, e.Z, e.Radius))
                continue;
            if (e.Forbidden)
                ++forbidden;
            else
                ++ok;
        }
        var outOfCombat = candidate == primary && c.Undesirable;
        var n = outOfCombat && forbidden == 1 && ok == 0 ? 1 : forbidden > 0 ? 0 : Math.Max(1, ok);
        return Adjust(aoe, n);
    }

    // the enemy to aim the shape at (index into enemies, -1 for none) and how many targets it hits there.
    // Manual: the player's target. Auto: the player's target when in range, replaced by any priority target in range whose shape hits
    // strictly more. AutoPrimary: as Auto among the enemies whose shape also hits the player's target (none without a player's target).
    public static int Select(in ShapeDef shape, TargetMode mode, AoeSetting aoe, float px, float pz, ReadOnlySpan<AoeCandidate> enemies, int primary, out int hits)
    {
        hits = 0;
        if (mode == TargetMode.Manual)
        {
            if (primary < 0)
                return -1;
            hits = Hits(shape, aoe, px, pz, enemies, primary, primary);
            return hits > 0 ? primary : -1;
        }
        if (mode == TargetMode.AutoPrimary && primary < 0)
            return -1;
        var initial = primary >= 0 && InRange(shape, px, pz, enemies[primary]) ? primary : -1;
        var best = initial;
        var bestHits = initial >= 0 ? Hits(shape, aoe, px, pz, enemies, initial, primary) : 0;
        for (var i = 0; i < enemies.Length; ++i)
        {
            var e = enemies[i];
            if (i == initial || e.Forbidden || !e.Counted || !InRange(shape, px, pz, e))
                continue;
            if (mode == TargetMode.AutoPrimary && !InShape(shape, px, pz, e.X, e.Z, enemies[primary].X, enemies[primary].Z, enemies[primary].Radius))
                continue;
            var h = Hits(shape, aoe, px, pz, enemies, i, primary);
            if (h > bestHits)
            {
                bestHits = h;
                best = i;
            }
        }
        hits = bestHits;
        return bestHits > 0 ? best : -1;
    }

    private static bool InCircle(float dx, float dz, float radius) => dx * dx + dz * dz <= radius * radius;

    private static void Direction(float px, float pz, float cx, float cz, out float dx, out float dz)
    {
        dx = cx - px;
        dz = cz - pz;
        var len = MathF.Sqrt(dx * dx + dz * dz);
        if (len > 0)
        {
            dx /= len;
            dz /= len;
        }
        else
        {
            dx = 0;
            dz = 1;
        }
    }

    // Intersect.CircleRect: offset from the rectangle's center, rotated into its frame (dir = the rectangle's Z axis)
    private static bool CircleRect(float ox, float oz, float radius, float dx, float dz, float halfWidth, float halfLength)
    {
        // WDir.Rotate(dir.MirrorX()): (x, z) -> (x * dz - z * dx, z * dz + x * dx)
        var rx = MathF.Abs(ox * dz - oz * dx);
        var rz = MathF.Abs(oz * dz + ox * dx);
        var cx = rx - halfWidth;
        var cz = rz - halfLength;
        if (cx > radius || cz > radius)
            return false;
        if (cx <= 0 || cz <= 0)
            return true;
        return cx * cx + cz * cz <= radius * radius;
    }

    // Intersect.CircleCone: offset from the cone's origin
    private static bool CircleCone(float ox, float oz, float radius, float coneRadius, float dx, float dz, float halfAngle)
    {
        var lsq = ox * ox + oz * oz;
        var rsq = radius * radius;
        if (lsq <= rsq)
            return true;
        var rsum = radius + coneRadius;
        if (lsq > rsum * rsum)
            return false;
        if (halfAngle >= MathF.PI)
            return true;
        var correctSide = ox * dx + oz * dz > 0;
        // normal = dir.OrthoL() = (dz, -dx)
        float nx = dz, nz = -dx;
        var sin = (float)Math.Sin(halfAngle);
        var distFromAxis = ox * nx + oz * nz;
        var originInCone = (halfAngle - MathF.PI / 2) switch
        {
            < 0 => correctSide && distFromAxis * distFromAxis <= lsq * sin * sin,
            > 0 => correctSide || distFromAxis * distFromAxis >= lsq * sin * sin,
            _ => correctSide,
        };
        if (originInCone)
            return true;
        if (distFromAxis < 0)
        {
            nx = -nx;
            nz = -nz;
        }
        var cos = (float)Math.Cos(halfAngle);
        var sx = dx * cos + nx * sin;
        var sz = dz * cos + nz * sin;
        var distFromSide = MathF.Abs(ox * sz - oz * sx);
        if (distFromSide > radius)
            return false;
        var distAlongSide = ox * sx + oz * sz;
        if (distAlongSide < 0)
            return false;
        if (distAlongSide <= coneRadius)
            return true;
        var kx = ox - sx * coneRadius;
        var kz = oz - sz * coneRadius;
        return kx * kx + kz * kz <= rsq;
    }
}
