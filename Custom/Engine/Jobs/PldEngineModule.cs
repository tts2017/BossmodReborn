using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.PLD;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace BossMod.Autorotation;

// "PLD [Engine]": Paladin damage rotation on the rotation engine (Custom/Engine). Level 100 only; below that it does nothing.
// Damage only: no mitigation, Shield Lob, Clemency, potion or tank stance handling.
public sealed class PldEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => new("PLD [Engine]", "Paladin damage on the two-tier rotation engine (burst-window planning + short search). Experimental, level 100.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.PLD), 100, 100);

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, player.Level) : 2.5f;
        return new RotationEngine(PldDefinition.Build(gcd), WeightsOverride?.Clone() ?? PldDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.05f, ReplanInterval = 8 };
    }

    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        if (Player.Level < 100)
        {
            for (var c = 0; c < Job.Cooldowns.Length; ++c)
            {
                s.Charges[c] = 0;
                s.CdReadyIn[c] = 10000;
            }
            for (var i = 0; i < Job.Skills.Length; ++i)
                s.DisabledSkills |= 1UL << i;
            return;
        }

        // whole 1000s only (what a spell costs): natural regen would otherwise change the state, and start a new search, every tick
        s.Gauges[Job.GaugeIndex(PldDefinition.MP)] = (short)(Math.Min(10000, Player.HPMP.CurMP) / 1000 * 1000);
        var step = World.Client.GetGauge<PaladinGauge>().ConfiteorComboStep switch { 1 => PldDefinition.FaithReady, 2 => PldDefinition.TruthReady, 3 => PldDefinition.ValorReady, _ => null };
        if (step != null)
        {
            var i = Job.StatusIndex(step);
            s.StatusLeft[i] = 30;
            s.StatusStacks[i] = 1;
        }

        ReadStatus(ref s, Job.StatusIndex(PldDefinition.FightOrFlight), Player, (uint)SID.FightOrFlight);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.GoringReady), Player, (uint)SID.GoringBladeReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.Requiescat), Player, (uint)SID.Requiescat);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.ConfiteorReady), Player, (uint)SID.ConfiteorReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.HonorReady), Player, (uint)SID.BladeOfHonorReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.AtonementReady), Player, (uint)SID.AtonementReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.SupplicationReady), Player, (uint)SID.SupplicationReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.SepulchreReady), Player, (uint)SID.SepulchreReady);
        ReadStatus(ref s, Job.StatusIndex(PldDefinition.DivineMight), Player, (uint)SID.DivineMight);

        ReadCooldown(ref s, Job.CooldownIndex(PldDefinition.FightOrFlightCD), ActionID.MakeSpell(AID.FightOrFlight));
        ReadCooldown(ref s, Job.CooldownIndex(PldDefinition.ImperatorCD), ActionID.MakeSpell(AID.Imperator));
        ReadCooldown(ref s, Job.CooldownIndex(PldDefinition.ExpiacionCD), ActionID.MakeSpell(AID.Expiacion));
        ReadCooldown(ref s, Job.CooldownIndex(PldDefinition.CircleOfScornCD), ActionID.MakeSpell(AID.CircleOfScorn));
        ReadCombo(ref s);
    }
}
