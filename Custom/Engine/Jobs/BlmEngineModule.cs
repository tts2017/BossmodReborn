using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.BLM;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace BossMod.Autorotation;

// "BLM [Engine]": Black Mage driven by the rotation engine (Custom/Engine). Level 60+ (the Fire IV / Umbral Heart loop): the
// definition is built for the player's level, and BMR recreates the module when the level changes (level sync). No potion, Lucid
// Dreaming or Ley Lines repositioning (Retrace / Between the Lines) yet.
public sealed class BlmEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    // harnesses: replaces the built-in weights for modules created afterwards
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => new("BLM [Engine]", "Black Mage on the two-tier rotation engine (burst-window planning + short search). Experimental, level 60+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.BLM), 100, 60);

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var gcd = stats.SpellSpeed > 0 ? ActionSpeed.GCDRounded(stats.SpellSpeed, stats.Haste, player.Level) : 2.5f;
        return new RotationEngine(BlmDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? BlmDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.08f };
    }

    // no target: assume it stays away for a while (no buffs or Triplecast stacks wasted into the gap; Umbral Soul keeps the ice phase going)
    protected override float UnknownDowntime => 10;

    // AoE spells hit everything within 5y of the primary target
    protected override byte CountTargets(Actor? primaryTarget)
        => primaryTarget == null ? (byte)1 : (byte)Math.Clamp(Hints.NumPriorityTargetsInAOECircle(primaryTarget.Position, 5), 1, 255);

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        var gauge = World.Client.GetGauge<BlackMageGauge>();
        s.Gauges[Job.GaugeIndex(BlmDefinition.MP)] = (short)Math.Min(10000, Player.HPMP.CurMP);
        s.Gauges[Job.GaugeIndex(BlmDefinition.AstralFire)] = (short)Math.Max(0, (int)gauge.ElementStance);
        s.Gauges[Job.GaugeIndex(BlmDefinition.UmbralIce)] = (short)Math.Max(0, -(int)gauge.ElementStance);
        s.Gauges[Job.GaugeIndex(BlmDefinition.Hearts)] = gauge.UmbralHearts;
        s.Gauges[Job.GaugeIndex(BlmDefinition.Polyglot)] = gauge.PolyglotStacks;
        s.Gauges[Job.GaugeIndex(BlmDefinition.AstralSoul)] = (short)gauge.AstralSoulStacks;
        s.Gauges[Job.GaugeIndex(BlmDefinition.Paradox)] = (short)(gauge.ParadoxActive ? 1 : 0);
        if (gauge.ElementStance != 0)
        {
            var timer = Job.StatusIndex(BlmDefinition.PolyglotTimer);
            s.StatusLeft[timer] = gauge.EnochianTimer > 0 ? gauge.EnochianTimer * 0.001f : 30;
            s.StatusStacks[timer] = 1;
        }

        ReadStatus(ref s, Job.StatusIndex(BlmDefinition.Swiftcast), Player, (uint)SID.Swiftcast);
        ReadStatus(ref s, Job.StatusIndex(BlmDefinition.Triplecast), Player, (uint)SID.Triplecast);
        ReadStatus(ref s, Job.StatusIndex(BlmDefinition.LeyLines), Player, (uint)SID.CircleOfPower);
        ReadStatus(ref s, Job.StatusIndex(BlmDefinition.Firestarter), Player, (uint)SID.Firestarter);
        ReadStatus(ref s, Job.StatusIndex(BlmDefinition.Thunderhead), Player, (uint)SID.Thunderhead);
        var dot = Job.StatusIndex(BlmDefinition.Thunder);
        // the DoTs of the level's Thunder spells (High Thunder (II) from 92, Thunder III from 45, Thunder IV from 64)
        var level = Player.Level;
        ReadStatus(ref s, dot, primaryTarget, level >= 92 ? (uint)SID.HighThunder : level >= 45 ? (uint)SID.ThunderIII : (uint)SID.Thunder);
        if (s.StatusLeft[dot] <= 0)
            ReadStatus(ref s, dot, primaryTarget, level >= 92 ? (uint)SID.HighThunderII : level >= 64 ? (uint)SID.ThunderIV : (uint)SID.ThunderII);

        ReadCooldown(ref s, Job.CooldownIndex(BlmDefinition.TransposeCD), ActionID.MakeSpell(AID.Transpose));
        ReadCooldown(ref s, Job.CooldownIndex(BlmDefinition.ManafontCD), ActionID.MakeSpell(AID.Manafont));
        ReadCooldown(ref s, Job.CooldownIndex(BlmDefinition.AmplifierCD), ActionID.MakeSpell(AID.Amplifier));
        ReadCooldown(ref s, Job.CooldownIndex(BlmDefinition.LeyLinesCD), ActionID.MakeSpell(AID.LeyLines));
        ReadCooldown(ref s, Job.CooldownIndex(BlmDefinition.TriplecastCD), ActionID.MakeSpell(AID.Triplecast));
        ReadCooldown(ref s, Job.CooldownIndex(BlmDefinition.SwiftcastCD), ActionID.MakeSpell(ClassShared.AID.Swiftcast));
    }
}
