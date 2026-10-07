using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.GNB;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace BossMod.Autorotation;

// "GNB [Engine]": Gunbreaker damage rotation on the rotation engine (Custom/Engine). Level 30+ (cartridges and Burst Strike): the
// definition is built for the player's level, and BMR recreates the module when the level changes (level sync).
// Damage only: no mitigation, Lightning Shot, potion or tank stance handling.
public sealed class GnbEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => new("GNB [Engine]", "Gunbreaker damage on the two-tier rotation engine (burst-window planning + short search). Experimental, level 30+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.GNB), 100, 30);

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, player.Level) : 2.5f;
        return new RotationEngine(GnbDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? GnbDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.05f, ReplanInterval = 8 };
    }

    // counted like the Akechi module (hitbox to hitbox), which is also how the AoE lands
    protected override byte CountTargets(Actor? primaryTarget) => CountTargetsByHitbox(5);

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        var gauge = World.Client.GetGauge<GunbreakerGauge>();
        s.Gauges[Job.GaugeIndex(GnbDefinition.Ammo)] = gauge.Ammo;
        var step = gauge.AmmoComboStep switch { 1 => GnbDefinition.SavageReady, 2 => GnbDefinition.TalonReady, 3 => GnbDefinition.NobleReady, 4 => GnbDefinition.LionReady, _ => null };
        if (step != null)
        {
            var i = Job.StatusIndex(step);
            s.StatusLeft[i] = 30;
            s.StatusStacks[i] = 1;
        }

        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.NoMercy), Player, (uint)SID.NoMercy);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.Bloodfest), Player, (uint)SID.Bloodfest);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToBreak), Player, (uint)SID.ReadyToBreak);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToReign), Player, (uint)SID.ReadyToReign);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToRip), Player, (uint)SID.ReadyToRip);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToTear), Player, (uint)SID.ReadyToTear);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToGouge), Player, (uint)SID.ReadyToGouge);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToBlast), Player, (uint)SID.ReadyToBlast);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToRaze), Player, (uint)SID.ReadyToRaze);

        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.NoMercyCD), ActionID.MakeSpell(AID.NoMercy));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.BloodfestCD), ActionID.MakeSpell(AID.Bloodfest));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.GnashingFangCD), ActionID.MakeSpell(AID.GnashingFang));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.DoubleDownCD), ActionID.MakeSpell(AID.DoubleDown));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.SonicBreakCD), ActionID.MakeSpell(AID.SonicBreak));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.ZoneCD), ActionID.MakeSpell(AID.BlastingZone));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.BowShockCD), ActionID.MakeSpell(AID.BowShock));
        ReadCombo(ref s);
    }
}
