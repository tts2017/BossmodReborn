using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.BLM;
using BossMod.Data;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanBLM = BossMod.Autorotation.xan.Custom.BLM;

namespace BossMod.Autorotation;

// "BLM [Engine]": Black Mage driven by the rotation engine (Custom/Engine). Level 60+ (the Fire IV / Umbral Heart loop): the
// definition is built for the player's level, and BMR recreates the module when the level changes (level sync). The strategy
// tracks are those of xan BLM [Custom]; Scathe and the Occult Crescent actions are pressed by this module. Below level 100 only
// the AOE / Targeting / MechanicHints settings act. No Lucid Dreaming or Ley Lines repositioning (Retrace / Between the Lines:
// the xan module has no setting that uses them either).
public sealed class BlmEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    // harnesses: replaces the built-in weights for modules created afterwards
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => new RotationModuleDefinition("BLM [Engine]", "Black Mage on the two-tier rotation engine (burst-window planning + short search). Experimental, level 60+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.BLM), 100, 60)
            .WithStrategies<XanBLM.Strategy>();

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var gcd = stats.SpellSpeed > 0 ? ActionSpeed.GCDRounded(stats.SpellSpeed, stats.Haste, player.Level) : 2.5f;
        return new RotationEngine(BlmDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? BlmDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.08f };
    }

    private XanBLM.Strategy _strategy;
    private bool _moving;

    // Double Transpose (Transpose out of Astral Fire, Triplecast in the ice phase, Firestarter kept, Transpose back): in a fight where
    // the party has used a raid buff, the Umbral Ice Paradox is skipped (the xan harness with party buffs: +0.49% on the 9 fights,
    // -0.49% without them). The marker carries over to the fire phase.
    private int _prevElement;
    private bool _dtrIce; // Umbral Ice I entered straight from Astral Fire (Transpose)
    private bool _dtrIceTriplecast; // Triplecast was up during that ice phase

    // Transpose is the only swap that lands on Umbral Ice I from Astral Fire
    private void SkipDoubleTransposeIceParadox(ref EngineState s)
    {
        int element = World.Client.GetGauge<BlackMageGauge>().ElementStance;
        var prev = _prevElement;
        _prevElement = element;
        if (prev > 0 && element == -1)
        {
            _dtrIce = true;
            _dtrIceTriplecast = false;
        }
        else if (element >= 0)
        {
            _dtrIce = false;
        }
        if (element < 0 && _dtrIce && SelfStatusLeft(SID.Triplecast) > 0)
            _dtrIceTriplecast = true;

        if (Player.Level < 90 || Bossmods.RaidCooldowns.NextDamageBuffIn2() == null) // no Paradox before 90; no party raid buff seen in this fight
            return;
        // the engine casts the ice Paradox before it weaves Triplecast: a Triplecast charge that is ready counts as the one the ice phase will use
        var triplecastCharge = s.Charges[Job.CooldownIndex(BlmDefinition.TriplecastCD)] > 0;
        if (element < 0 && _dtrIce && (_dtrIceTriplecast || triplecastCharge) && SelfStatusLeft(SID.Firestarter) > 0)
            Forbid(ref s, "ParadoxIce");
    }

    protected override Actor? SelectTarget(StrategyValues strategy, Actor? primaryTarget)
    {
        _strategy = ValueConverter.FromValues<XanBLM.Strategy>(strategy);
        SetMechanicHints(_strategy.MechanicHints);
        return SelectTarget(_strategy.Targeting.Value, primaryTarget, 25);
    }

    private static readonly string[] Thunders = ["HighThunder", "HighThunder2"];

    protected override void ApplyStrategy(StrategyValues strategy, ref EngineState s, Actor? primaryTarget)
    {
        var st = _strategy;
        ApplyAoe(ref s, st.AOE);
        SkipDoubleTransposeIceParadox(ref s);
        if (Player.Level < 100)
            return;

        // manual control: the player casts, only Polyglot is spent against overcapping (and Manafont follows its own setting)
        if (st.Rotation.Value == XanBLM.RotationStrategy.PolyglotOvercapOnly)
        {
            var polyglotCapping = s.Gauges[Job.GaugeIndex(BlmDefinition.Polyglot)] >= Job.Gauges[Job.GaugeIndex(BlmDefinition.Polyglot)].Max
                && s.StatusLeft[Job.StatusIndex(BlmDefinition.PolyglotTimer)] <= GCD + Job.BaseGcd;
            foreach (var sk in Job.Skills)
                if (!(sk.Name is "Xenoglossy" or "Foul" && polyglotCapping || sk.Name == "Manafont" && st.Manafont.Value != OffensiveStrategy.Delay))
                    s.DisabledSkills |= 1UL << sk.Index;
        }

        switch (st.Thunder.Value)
        {
            case XanBLM.ThunderStrategy.Delay:
                foreach (var sk in Thunders)
                    Forbid(ref s, sk);
                break;
            case XanBLM.ThunderStrategy.Force:
                if (s.Targets >= 3)
                    Force("HighThunder2");
                Force("HighThunder");
                break;
            case XanBLM.ThunderStrategy.InstantOnly:
                // not kept up: only as the instant cast while moving
                if (!_moving)
                    foreach (var sk in Thunders)
                        Forbid(ref s, sk);
                break;
        }

        switch (st.Leylines.Value)
        {
            case XanBLM.LeylinesStrategy.Delay:
                Forbid(ref s, "LeyLines");
                break;
            case XanBLM.LeylinesStrategy.OpenerOnly:
                if (CombatTime > 30)
                    Forbid(ref s, "LeyLines");
                break;
            case XanBLM.LeylinesStrategy.Force:
                Force("LeyLines");
                break;
        }
        if (st.LLMove.Value == DisabledByDefault.Disabled && _moving)
            Forbid(ref s, "LeyLines");

        if (st.Triplecast.Value == XanBLM.TriplecastStrategy.Delay)
            Forbid(ref s, "Triplecast");
        else if (st.Triplecast.Value == XanBLM.TriplecastStrategy.Force)
            Force("Triplecast");

        if (st.Swiftcast.Value == XanBLM.SwiftcastStrategy.Delay)
            Forbid(ref s, "Swiftcast");
        else if (st.Swiftcast.Value == XanBLM.SwiftcastStrategy.Force)
            Force("Swiftcast");

        if (st.Manafont.Value == OffensiveStrategy.Delay)
            Forbid(ref s, "Manafont");
        else if (st.Manafont.Value == OffensiveStrategy.Force)
            Force("Manafont");
    }

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        _moving = isMoving;
        base.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        if (Player.Level < 100 || Target == null || !Player.InCombat)
            return;
        var st = _strategy;
        // Scathe: the instant filler while moving with no instant cast available (below the engine's GCD)
        if (st.Scathe.Value == XanBLM.ScatheStrategy.Allow && isMoving && SelfStatusLeft(ClassShared.SID.Swiftcast) <= 0 && SelfStatusLeft(SID.Triplecast) <= 0 && Player.HPMP.CurMP >= 800)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Scathe), Target, ActionQueue.Priority.High + 1);
        // Occult Crescent: Zeninage under raid buffs, Iainuki and the Time Mage actions after the first 10 s (or under raid buffs; Occult
        // Comet only with an instant cast up)
        var raidBuffs = EstimateRaidBuffTimings(Target).Left > GCD;
        var warm = CombatTime > 10 || raidBuffs;
        if (st.Zeninage.Value == EnabledByDefault.Enabled && raidBuffs && PhantomActionReady(PhantomID.Zeninage))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(PhantomID.Zeninage), Target, ActionQueue.Priority.High + 3.3f);
        if (st.Iainuki.Value == EnabledByDefault.Enabled && warm && PhantomActionReady(PhantomID.Iainuki))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(PhantomID.Iainuki), Target, ActionQueue.Priority.High + 3.2f);
        if (st.AutoTimeMage.Value == EnabledByDefault.Enabled)
        {
            if (PhantomActionReady(PhantomID.OccultQuick) && (SelfStatusLeft(SID.CircleOfPower) > 0 || CombatTime > 10))
                Hints.ActionsToExecute.Push(ActionID.MakeSpell(PhantomID.OccultQuick), Player, ActionQueue.Priority.High + 3.1f);
            if (warm && PhantomActionReady(PhantomID.OccultComet) && (SelfStatusLeft(ClassShared.SID.Swiftcast) > 0 || SelfStatusLeft(SID.Triplecast) > 0))
                Hints.ActionsToExecute.Push(ActionID.MakeSpell(PhantomID.OccultComet), Target, ActionQueue.Priority.High + 3f);
        }
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
