using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.BLM;
using BossMod.Data;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanBLM = BossMod.Autorotation.xan.Custom.BLM;

namespace BossMod.Autorotation;

// "BLM [Engine]": Black Mage driven by the rotation engine (Custom/Engine). Level 60+ (the Fire IV / Umbral Heart loop): the
// definition is built for the player's level, and BMR recreates the module when the level changes (level sync). The strategy
// tracks are those of xan BLM [Custom]; Scathe and the Occult Crescent actions are pressed by this module. Settings for skills not
// learned at the player's level do nothing. No Lucid Dreaming or Ley Lines repositioning (Retrace / Between the Lines:
// the xan module has no setting that uses them either).
public sealed class BlmEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    // harnesses: replaces the built-in weights for modules created afterwards
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => WithComposition(new RotationModuleDefinition("BLM [Engine]", "Black Mage on the two-tier rotation engine (burst-window planning + short search). Experimental, level 60+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.BLM), 100, 60)
            .WithStrategies<XanBLM.Strategy>());

    // the first tier (EngineRotationModule.CompositionStrategy): the xan BLM module of the same strategy tracks
    protected override RotationModule CreateBaseline() => new XanBLM(Manager, Player);

    // the engine is built on a worker thread (EngineRotationModule): its cycle model takes about 0.8 s on the first build for a GCD
    private static Func<RotationEngine> CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var level = player.Level;
        var gcd = stats.SpellSpeed > 0 ? ActionSpeed.GCDRounded(stats.SpellSpeed, stats.Haste, level) : 2.5f;
        var weights = WeightsOverride?.Clone() ?? BlmDefinition.DefaultWeights(level);
        var frameBudget = FrameBudgetOverride ?? 0.08f;
        return () => new RotationEngine(BlmDefinition.Build(gcd, level), weights) { FrameBudgetMs = frameBudget };
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
        // Umbral Soul (a 0-potency GCD) keeps the ice phase going while there is nothing to attack, or as the instant while moving; with the
        // target up and standing it only replaced a cast (section 31)
        if (Player.Level >= 100 && primaryTarget is { IsTargetable: true } && !_moving)
            Forbid(ref s, "UmbralSoul");
        // Flare is the AoE fire spender (three or more targets, as the old module); on one or two targets it only cut the Fire IV phase short
        if (Player.Level >= 100 && ShapeTargets(s, "Flare") < 3)
            Forbid(ref s, "Flare");
        // Freeze likewise is the AoE ice spell; on one or two targets the ice phase uses Blizzard IV
        if (Player.Level >= 100 && ShapeTargets(s, "Freeze") < 3)
            Forbid(ref s, "Freeze");

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
                if (ShapeTargets(s, "HighThunder2") >= 3)
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

        // in a fight where the party has used a raid buff, Astral Fire is not left for ice while Manafont comes off cooldown within 5 s:
        // the fire phase runs into Manafont (the xan harness with party buffs: +0.44% on the 9 fights, -0.83% without them). Only with
        // Flare Star (100): the fire phase it was measured on (at level 90 with party buffs it cost 0.5%; below 90 nothing may be left
        // to cast in Astral Fire while waiting)
        if (st.Manafont.Value != OffensiveStrategy.Delay && Job.HasSkill("FlareStar") && Bossmods.RaidCooldowns.NextDamageBuffIn2() != null
            && s.Gauges[Job.GaugeIndex(BlmDefinition.AstralFire)] > 0 && s.CdReadyIn[Job.CooldownIndex(BlmDefinition.ManafontCD)] <= 5)
            foreach (var exit in AstralFireExits)
                Forbid(ref s, exit);
    }

    private static readonly string[] AstralFireExits = ["Blizzard3", "Blizzard3LowFire", "HighBlizzard2", "HighBlizzard2Cold", "Transpose"];

    protected override void ExecuteJob(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        _moving = isMoving;
        base.ExecuteJob(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        if (Target == null || !Player.InCombat)
            return;
        var st = _strategy;
        // Scathe: the instant filler while moving with no instant cast available (below the engine's GCD)
        if (st.Scathe.Value == XanBLM.ScatheStrategy.Allow && isMoving && ActionUnlocked(AID.Scathe) && SelfStatusLeft(ClassShared.SID.Swiftcast) <= 0 && SelfStatusLeft(SID.Triplecast) <= 0 && Player.HPMP.CurMP >= 800)
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

    // Level sync (below 100): The Balance BLM Leveling Guide's per-band loops (docs/rebuild/engine-design.md section 20)
    protected override bool HasSyncedRules => true;
    private bool _downtimeIce; // a downtime since the last Astral Fire

    protected override int SyncedOgcd(in EngineState s, in EngineTimeline tl)
    {
        var fire = Gauge(s, BlmDefinition.AstralFire);
        var ice = Gauge(s, BlmDefinition.UmbralIce);
        var mp = Gauge(s, BlmDefinition.MP);
        var aoe = ShapeTargets(s, "HighFire2") >= 3;
        // the swaps, Manafont and the movement instant casts decide the next GCD: they go first when there is no weave window left
        SyncedOgcdFirst = true;
        // nothing to attack: out of Astral Fire (Umbral Soul keeps the ice phase going)
        if (fire > 0 && tl.InDowntime(s.Time) && Legal(s, tl, "Transpose") is var down and >= 0)
            return down;
        // Manafont at the end of the fire phase (after the last spender)
        if (fire > 0 && mp < (aoe ? 800 : Job.HasSkill("Despair") ? 800 : 1600) && Legal(s, tl, "Manafont") is var manafont and >= 0)
            return manafont;
        // AoE (3+ targets): Transpose out of Umbral Ice with full hearts, and out of Astral Fire once Flare cannot be cast
        if (aoe)
        {
            if (ice > 0 && Gauge(s, BlmDefinition.Hearts) >= 3 && Legal(s, tl, "TransposeIce") is var toFire and >= 0)
                return toFire;
            if (fire > 0 && mp < 800 && Legal(s, tl, "Transpose") is var toIce and >= 0)
                return toIce;
        }
        // moving with no instant GCD up: Triplecast, else Swiftcast
        var instantUp = StatusLeft(s, BlmDefinition.Triplecast) > 0 || StatusLeft(s, BlmDefinition.Swiftcast) > 0;
        if (_moving && !instantUp && InstantGcd(s, tl) < 0 && FirstLegal(s, tl, "Triplecast", "Swiftcast") is var instant and >= 0)
            return instant;
        // back from a downtime in Umbral Ice with full hearts: the Fire III into Astral Fire instant (Swiftcast, else a Triplecast charge)
        if (tl.InDowntime(s.Time))
            _downtimeIce = true;
        else if (fire > 0)
            _downtimeIce = false;
        if (_downtimeIce && ice > 0 && Gauge(s, BlmDefinition.Hearts) >= 3 && !tl.InDowntime(s.Time) && !instantUp && StatusLeft(s, BlmDefinition.Firestarter) <= 0
            && FirstLegal(s, tl, "Swiftcast", "Triplecast") is var resume and >= 0)
            return resume;
        SyncedOgcdFirst = false;
        // Ley Lines and Amplifier (86+, not into a full Polyglot gauge) on cooldown
        if (Legal(s, tl, "LeyLines") is var leyLines and >= 0)
            return leyLines;
        if (Gauge(s, BlmDefinition.Polyglot) < Job.Gauges[Job.GaugeIndex(BlmDefinition.Polyglot)].Max && FirstLegal(s, tl, "Amplifier", "AmplifierIce") is var amplifier and >= 0)
            return amplifier;
        // Triplecast in Astral Fire when its charges are full
        if (fire > 0 && !instantUp && Charges(s, BlmDefinition.TriplecastCD) >= Job.Cooldowns[Job.CooldownIndex(BlmDefinition.TriplecastCD)].MaxCharges
            && Legal(s, tl, "Triplecast") is var triplecast and >= 0)
            return triplecast;
        return -1;
    }

    // an instant GCD worth casting: Polyglot, Paradox, Firestarter, Thunder
    private int InstantGcd(in EngineState s, in EngineTimeline tl)
        => FirstLegal(s, tl, ShapeTargets(s, "Foul") >= 3 ? "Foul" : "Xenoglossy", "Xenoglossy", "Foul", "Paradox", "ParadoxIce", "Fire3Proc", ShapeTargets(s, "HighThunder2") >= 3 ? "HighThunder2" : "HighThunder", "HighThunder");

    protected override int SyncedGcd(in EngineState s, in EngineTimeline tl)
    {
        var fire = Gauge(s, BlmDefinition.AstralFire);
        var ice = Gauge(s, BlmDefinition.UmbralIce);
        var mp = Gauge(s, BlmDefinition.MP);
        var hearts = Gauge(s, BlmDefinition.Hearts);
        var aoe = ShapeTargets(s, "HighFire2") >= 3;
        var polyglot = Gauge(s, BlmDefinition.Polyglot);
        var spender = aoe || !Job.HasSkill("Xenoglossy") ? "Foul" : "Xenoglossy";

        // Thunder only when its DoT has under 3 s left (Thunder II / IV / High Thunder II on 3+ targets)
        if (StatusLeft(s, BlmDefinition.Thunder) < 3 && tl.FightEndIn - s.Time > 10 && FirstLegal(s, tl, aoe ? "HighThunder2" : "HighThunder", "HighThunder") is var thunder and >= 0)
            return thunder;
        // Polyglot never overcapped: spent when the gauge is full and the next stack is about to come
        if (polyglot >= Job.Gauges[Job.GaugeIndex(BlmDefinition.Polyglot)].Max && StatusLeft(s, BlmDefinition.PolyglotTimer) < Job.BaseGcd * 3
            && FirstLegal(s, tl, spender, "Xenoglossy", "Foul") is var poly and >= 0)
            return poly;
        // Polyglot before the fight ends
        if (polyglot > 0 && tl.FightEndIn - s.Time < Job.BaseGcd * polyglot && FirstLegal(s, tl, spender, "Xenoglossy", "Foul") is var last and >= 0)
            return last;

        int pick;
        if (aoe)
        {
            // (from Umbral Ice) Freeze > Foul / Thunder / Freeze > Transpose > Flare x2 > Transpose
            if (ice > 0)
                pick = hearts < 3 ? FirstLegal(s, tl, "Freeze") : FirstLegal(s, tl, "Foul", "Freeze");
            else if (fire > 0)
                pick = FirstLegal(s, tl, "Flare");
            else
                pick = FirstLegal(s, tl, "HighBlizzard2Cold", "Blizzard3Cold");
        }
        else if (fire > 0)
        {
            // Astral Fire: Fire IV while the MP leaves Despair (72+) its 800; Paradox (90+) once the hearts are spent; Firestarter only
            // when it is about to run out (kept for the next Umbral Ice); then Despair, then Blizzard III
            var keep = Job.HasSkill("Despair") ? 800 : 0;
            var f4Cost = hearts > 0 ? 800 : 1600;
            pick = -1;
            if (StatusLeft(s, BlmDefinition.Firestarter) is > 0 and < 5)
                pick = Legal(s, tl, "Fire3Proc");
            if (pick < 0 && hearts == 0 && mp - 1600 >= keep)
                pick = Legal(s, tl, "Paradox");
            if (pick < 0 && mp - f4Cost >= keep)
                pick = FirstLegal(s, tl, "Fire4", "Fire4NoHeart");
            if (pick < 0)
                pick = FirstLegal(s, tl, "Despair", "Blizzard3", "Blizzard3LowFire");
        }
        else if (ice > 0)
        {
            // Umbral Ice: Blizzard IV for the hearts, Paradox (90+), the Polyglot stacks, then Fire III (Firestarter's instant one first)
            pick = hearts < 3 ? FirstLegal(s, tl, "Blizzard4") : -1;
            if (pick < 0)
                pick = Legal(s, tl, "ParadoxIce");
            if (pick < 0 && polyglot > 0)
                pick = FirstLegal(s, tl, spender, "Xenoglossy", "Foul");
            // nothing to attack: Umbral Soul up to Umbral Ice III and full hearts (no GCD after that, ready for the target's return)
            if (pick < 0 && tl.InDowntime(s.Time))
                return ice < 3 || hearts < 3 ? Legal(s, tl, "UmbralSoul") : -1;
            if (pick < 0)
                pick = FirstLegal(s, tl, "Fire3Proc", "Fire3", "Fire3LowIce");
        }
        else
        {
            // no element: the loop starts from Blizzard III
            pick = FirstLegal(s, tl, "Blizzard3Cold", "Fire3Cold");
        }
        // moving (no cast possible): an instant GCD instead
        return pick >= 0 ? pick : InstantGcd(s, tl);
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
