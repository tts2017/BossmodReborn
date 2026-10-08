using BossMod.Autorotation.Engine;
using Xunit;

namespace EngineTests;

// AoeTargeting: the shape tests, the hit count and the target choice per Targeting mode (pure functions; the adapter feeds them the
// hints' enemies). Enemies have a 2 y hitbox like the harness adds; the player stands at the origin.
public sealed class AoeTargetingTests
{
    private static readonly ShapeDef SelfCircle5 = new(AoeShape.SelfCircle, 5, 0, 3);
    private static readonly ShapeDef Splash5 = new(AoeShape.TargetCircle, 5, 0, 20);
    private static readonly ShapeDef Line10 = new(AoeShape.Line, 10, 2, 10);
    private static readonly ShapeDef Cone8 = new(AoeShape.Cone, 8, 60, 8);

    private static AoeCandidate Enemy(float x, float z, bool forbidden = false, bool undesirable = false, bool counted = true) => new(x, z, 2, forbidden, undesirable, counted);

    [Fact]
    public void SelfCircleCountsAroundThePlayerWhateverTheTarget()
    {
        Assert.True(AoeTargeting.InShape(SelfCircle5, 0, 0, 30, 0, 6.5f, 0, 2));   // 6.5 - 2 = 4.5 <= 5
        Assert.False(AoeTargeting.InShape(SelfCircle5, 0, 0, 30, 0, 7.5f, 0, 2));  // 5.5 > 5
    }

    [Fact]
    public void TargetCircleCountsAroundTheCandidate()
    {
        Assert.True(AoeTargeting.InShape(Splash5, 0, 0, 10, 0, 16.5f, 0, 2));
        Assert.False(AoeTargeting.InShape(Splash5, 0, 0, 10, 0, 17.5f, 0, 2));
        Assert.False(AoeTargeting.InShape(Splash5, 0, 0, 10, 0, 0, 0, 2)); // the player's own spot is 10 y from the candidate
    }

    [Fact]
    public void LineRunsFromThePlayerTowardTheCandidate()
    {
        // toward +x: length 10, half width 2, enemies with hitbox 2
        Assert.True(AoeTargeting.InShape(Line10, 0, 0, 3, 0, 9, 3.9f, 2));   // beside the line: 3.9 - 2 < 2
        Assert.False(AoeTargeting.InShape(Line10, 0, 0, 3, 0, 9, 4.1f, 2));
        Assert.True(AoeTargeting.InShape(Line10, 0, 0, 3, 0, 11.9f, 0, 2));  // past the end by less than the hitbox
        Assert.False(AoeTargeting.InShape(Line10, 0, 0, 3, 0, 12.1f, 0, 2));
        Assert.False(AoeTargeting.InShape(Line10, 0, 0, 3, 0, -3, 0, 2));    // behind the player
        // the direction follows the candidate: the same enemy toward -x
        Assert.True(AoeTargeting.InShape(Line10, 0, 0, -3, 0, -9, 0, 2));
    }

    [Fact]
    public void ConeOpensTowardTheCandidate()
    {
        Assert.True(AoeTargeting.InShape(Cone8, 0, 0, 3, 0, 6, 2, 2));     // 18 degrees off the axis
        Assert.False(AoeTargeting.InShape(Cone8, 0, 0, 3, 0, 1, 7, 2));    // 82 degrees off: beyond the 60 degree half-angle plus the hitbox
        Assert.False(AoeTargeting.InShape(Cone8, 0, 0, 3, 0, 11, 0, 2));   // beyond the radius plus the hitbox
        Assert.True(AoeTargeting.InShape(Cone8, 0, 0, 3, 0, 9.9f, 0, 2));
        Assert.False(AoeTargeting.InShape(Cone8, 0, 0, 3, 0, -5, 0, 2));
    }

    [Fact]
    public void HitsCountPriorityTargetsAndRefuseForbiddenOnes()
    {
        ReadOnlySpan<AoeCandidate> enemies = [Enemy(3, 0), Enemy(5, 2), Enemy(30, 0)];
        Assert.Equal(2, AoeTargeting.Hits(Splash5, AoeSetting.Auto, 0, 0, enemies, 0, 0));
        Assert.Equal(1, AoeTargeting.Hits(Splash5, AoeSetting.Auto, 0, 0, enemies, 2, 0)); // the far one alone: a targeted action always hits its target
        ReadOnlySpan<AoeCandidate> withForbidden = [Enemy(3, 0), Enemy(5, 2, forbidden: true)];
        Assert.Equal(0, AoeTargeting.Hits(Splash5, AoeSetting.Auto, 0, 0, withForbidden, 0, 0));
        // the player's own out-of-combat target, hit alone, still counts as one
        ReadOnlySpan<AoeCandidate> outOfCombat = [Enemy(3, 0, forbidden: true, undesirable: true)];
        Assert.Equal(1, AoeTargeting.Hits(Splash5, AoeSetting.Auto, 0, 0, outOfCombat, 0, 0));
        Assert.Equal(0, AoeTargeting.Hits(Splash5, AoeSetting.Auto, 0, 0, outOfCombat, 0, -1)); // not the player's target: forbidden
        // the AOE setting adjusts the count
        Assert.Equal(1, AoeTargeting.Hits(Splash5, AoeSetting.SingleTarget, 0, 0, enemies, 0, 0));
        Assert.Equal(10, AoeTargeting.Hits(Splash5, AoeSetting.ForceAoe, 0, 0, enemies, 0, 0));
        Assert.Equal(0, AoeTargeting.Hits(Splash5, AoeSetting.ForceSingleTarget, 0, 0, enemies, 0, 0));
    }

    [Fact]
    public void ManualKeepsThePlayersTarget()
    {
        // the player's target (index 0) hits itself only; index 1 would hit two
        ReadOnlySpan<AoeCandidate> enemies = [Enemy(3, 0), Enemy(12, 0), Enemy(16, 0)];
        Assert.Equal(0, AoeTargeting.Select(Splash5, TargetMode.Manual, AoeSetting.Auto, 0, 0, enemies, 0, out var hits));
        Assert.Equal(1, hits);
        Assert.Equal(-1, AoeTargeting.Select(Splash5, TargetMode.Manual, AoeSetting.Auto, 0, 0, enemies, -1, out hits));
        Assert.Equal(0, hits);
    }

    [Fact]
    public void AutoPicksTheEnemyWhoseShapeHitsTheMost()
    {
        ReadOnlySpan<AoeCandidate> enemies = [Enemy(3, 0), Enemy(12, 0), Enemy(16, 0)];
        Assert.Equal(1, AoeTargeting.Select(Splash5, TargetMode.Auto, AoeSetting.Auto, 0, 0, enemies, 0, out var hits));
        Assert.Equal(2, hits);
        // equal counts: the player's target stays
        ReadOnlySpan<AoeCandidate> spread = [Enemy(3, 0), Enemy(30, 0)];
        Assert.Equal(0, AoeTargeting.Select(Splash5, TargetMode.Auto, AoeSetting.Auto, 0, 0, spread, 0, out hits));
        Assert.Equal(1, hits);
        // no player's target: the first in range that hits the most
        Assert.Equal(1, AoeTargeting.Select(Splash5, TargetMode.Auto, AoeSetting.Auto, 0, 0, enemies, -1, out hits));
        Assert.Equal(2, hits);
        // out of range candidates are skipped (range 20 + hitbox 2 + 0.5)
        ReadOnlySpan<AoeCandidate> far = [Enemy(3, 0), Enemy(23, 0), Enemy(27, 0)];
        Assert.Equal(0, AoeTargeting.Select(Splash5, TargetMode.Auto, AoeSetting.Auto, 0, 0, far, 0, out hits));
        Assert.Equal(1, hits);
        // ST setting: every candidate scores 1, the player's target stays
        Assert.Equal(0, AoeTargeting.Select(Splash5, TargetMode.Auto, AoeSetting.SingleTarget, 0, 0, enemies, 0, out hits));
        Assert.Equal(1, hits);
    }

    [Fact]
    public void AutoPrimaryNeverLeavesThePlayersTarget()
    {
        // index 1 hits two (1 and 2) but not the player's target 0; index 3 hits 0, 3 and 4; index 4 hits 3 and 4 but not 0
        ReadOnlySpan<AoeCandidate> enemies = [Enemy(3, 0), Enemy(12, 0), Enemy(16, 0), Enemy(3, 6), Enemy(3, 12)];
        Assert.Equal(3, AoeTargeting.Select(Splash5, TargetMode.AutoPrimary, AoeSetting.Auto, 0, 0, enemies, 0, out var hits));
        Assert.Equal(3, hits);
        // Auto would also take index 3 here (three hits beat two)
        Assert.Equal(3, AoeTargeting.Select(Splash5, TargetMode.Auto, AoeSetting.Auto, 0, 0, enemies, 0, out hits));
        Assert.Equal(3, hits);
        Assert.Equal(-1, AoeTargeting.Select(Splash5, TargetMode.AutoPrimary, AoeSetting.Auto, 0, 0, enemies, -1, out hits));
        Assert.Equal(0, hits);
        // the player's target in neither hint list (not counted) is still the candidate it is aimed at
        ReadOnlySpan<AoeCandidate> uncounted = [Enemy(12, 0), Enemy(16, 0), Enemy(3, 0, counted: false)];
        Assert.Equal(2, AoeTargeting.Select(Splash5, TargetMode.AutoPrimary, AoeSetting.Auto, 0, 0, uncounted, 2, out hits));
        Assert.Equal(1, hits);
    }

    [Fact]
    public void ForbiddenTargetsBlockTheShape()
    {
        // the player's target would splash a forbidden enemy: no target for the shape (the adapter then disables its skills)
        ReadOnlySpan<AoeCandidate> enemies = [Enemy(3, 0), Enemy(6, 0, forbidden: true)];
        Assert.Equal(-1, AoeTargeting.Select(Splash5, TargetMode.Manual, AoeSetting.Auto, 0, 0, enemies, 0, out var hits));
        Assert.Equal(0, hits);
        // Auto moves to an enemy whose shape is clean
        ReadOnlySpan<AoeCandidate> alternative = [Enemy(3, 0), Enemy(6, 0, forbidden: true), Enemy(15, 0)];
        Assert.Equal(2, AoeTargeting.Select(Splash5, TargetMode.Auto, AoeSetting.Auto, 0, 0, alternative, 0, out hits));
        Assert.Equal(1, hits);
    }

    [Fact]
    public void JobBuilderDedupesShapesAndRemapsTargetConditions()
    {
        var job = new JobBuilder("SHAPES", 2.5f)
            .Gcd("Single", 100)
            .Gcd("Circle", 0).Aoe(100, 3).RequiresTargets(3).Shape(AoeShape.SelfCircle, 5, 0, 3)
            .Gcd("Circle2", 0).Aoe(120, 3).RequiresTargets(3).Shape(AoeShape.SelfCircle, 5, 0, 3)
            .Gcd("Cone", 0).Aoe(140, 3).RequiresTargets(3).Shape(AoeShape.Cone, 8, 90, 8)
            .Build();
        Assert.Equal(2, job.Shapes.Length);
        Assert.Equal(-1, job.Skills[job.SkillIndex("Single")].Shape);
        Assert.Equal(0, job.Skills[job.SkillIndex("Circle")].Shape);
        Assert.Equal(0, job.Skills[job.SkillIndex("Circle2")].Shape);
        Assert.Equal(1, job.Skills[job.SkillIndex("Cone")].Shape);
        var cone = job.Skills[job.SkillIndex("Cone")];
        Assert.Equal(ConditionKind.TargetsAtLeast, cone.Conditions[0].Kind);
        Assert.Equal(2, cone.Conditions[0].Index);

        // the cone count legalizes the cone skill, the main count the single-target one; potency follows the shape count
        var s = EngineState.Create(job);
        s.Targets = 1;
        s.ShapeTargets[1] = 4;
        var tl = EngineTimeline.Open();
        Assert.True(Simulator.IsLegal(job, s, tl, cone));
        Assert.False(Simulator.IsLegal(job, s, tl, job.Skills[job.SkillIndex("Circle")]));
        Assert.Equal(140 * 4, Simulator.Potency(job, s, cone));
        Assert.Equal(4, s.TargetsOf(1));
        Assert.Equal(1, s.TargetsOf(0));
        Assert.Equal(1, s.TargetsOf(-1));
        // the hash only sees shape counts that differ from the main count
        var same = EngineState.Create(job);
        same.Targets = 1;
        Assert.NotEqual(same.Hash(job), s.Hash(job));
        same.ShapeTargets[1] = 1;
        var plain = EngineState.Create(job);
        plain.Targets = 1;
        Assert.Equal(plain.Hash(job), same.Hash(job));
    }
}
