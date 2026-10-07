using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.MNK;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation;

// "MNK [Engine]": Monk driven by the rotation engine (Custom/Engine). Level 100 only; below that it does nothing, so use it next
// to (not instead of) a regular MNK module when levelling. No potion, Six-sided Star, Form Shift, Meditation, Thunderclap or
// Riddle of Earth.
public sealed class MnkEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => new("MNK [Engine]", "Monk on the two-tier rotation engine (burst-window planning + short search). Experimental, level 100.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.MNK), 100, 100);

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        // Greased Lightning (level 76+): 20% haste on weaponskills
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, Math.Min(stats.Haste, 80), player.Level) : 2.0f;
        return new RotationEngine(MnkDefinition.Build(gcd), WeightsOverride?.Clone() ?? MnkDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.03f, ReplanInterval = 8 };
    }

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        base.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        UpdatePositional(primaryTarget);
        RiddleOfEarth();
    }

    // mitigation outside the engine: Riddle of Earth when damage to us is predicted within 10 s (as the regular module)
    private void RiddleOfEarth()
    {
        if (!Player.InCombat || Player.Level < 64 || CD(AID.RiddleOfEarth) > 0.6f || SelfStatusLeft(SID.RiddleOfEarth) > 0 || SelfStatusLeft(SID.EarthsRumination) > 0)
            return;
        foreach (var damage in Hints.PredictedDamage)
        {
            if (!damage.Players[PartyState.PlayerSlot] || damage.Type is not (PredictedDamageType.Raidwide or PredictedDamageType.Shared or PredictedDamageType.Tankbuster))
                continue;
            var damageIn = (float)(damage.Activation - World.CurrentTime).TotalSeconds;
            if (damageIn >= 0 && damageIn <= 10)
            {
                Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.RiddleOfEarth), Player, ActionQueue.Priority.Low);
                return;
            }
        }
    }

    private float CD(AID aid) => ActionDefinitions.Instance[ActionID.MakeSpell(aid)]?.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions) ?? 1000;

    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    // Demolish / Snap Punch: rear / flank (as the regular modules); True North when the next one would be missed
    private void UpdatePositional(Actor? target)
    {
        var next = LastDecision.NextGcd >= 0 ? Job.Skills[LastDecision.NextGcd].Name : "";
        var pos = next == "Demolish" ? Positional.Rear : next is "PouncingCoeurl" or "SnapPunch" ? Positional.Flank : Positional.Any;
        if (target == null || target.Omnidirectional || pos == Positional.Any || target.TargetID == Player.InstanceID && target.CastInfo == null && !target.IsStrikingDummy)
        {
            Hints.RecommendedPositional = (target, Positional.Any, false, true);
            return;
        }
        var trueNorth = StatusDetails(Player, ClassShared.SID.TrueNorth, Player.InstanceID).Left > GCD;
        var dot = target.Rotation.ToDirection().Dot((Player.Position - target.Position).Normalized());
        var correct = trueNorth || (pos == Positional.Flank ? MathF.Abs(dot) < 0.7071067f : dot < -0.7071068f);
        var imminent = !trueNorth && GCD < 2.0f;
        Hints.RecommendedPositional = (target, pos, imminent, correct);
        if (imminent && !correct)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.TrueNorth), Player, ActionQueue.Priority.Low + 2, delay: MathF.Max(0, GCD - 0.72f));
    }

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

        var gauge = World.Client.GetGauge<MonkGauge>();
        s.Gauges[Job.GaugeIndex(MnkDefinition.OpoFury)] = (short)gauge.OpoOpoStacks;
        s.Gauges[Job.GaugeIndex(MnkDefinition.RaptorFury)] = (short)gauge.RaptorStacks;
        s.Gauges[Job.GaugeIndex(MnkDefinition.CoeurlFury)] = (short)gauge.CoeurlStacks;
        int opo = 0, raptor = 0, coeurl = 0;
        void Beast(BeastChakraType t)
        {
            if (t == BeastChakraType.OpoOpo) ++opo;
            else if (t == BeastChakraType.Raptor) ++raptor;
            else if (t == BeastChakraType.Coeurl) ++coeurl;
        }
        Beast(gauge.BeastChakra1);
        Beast(gauge.BeastChakra2);
        Beast(gauge.BeastChakra3);
        s.Gauges[Job.GaugeIndex(MnkDefinition.BeastOpo)] = (short)opo;
        s.Gauges[Job.GaugeIndex(MnkDefinition.BeastRaptor)] = (short)raptor;
        s.Gauges[Job.GaugeIndex(MnkDefinition.BeastCoeurl)] = (short)coeurl;
        s.Gauges[Job.GaugeIndex(MnkDefinition.BeastTotal)] = (short)(opo + raptor + coeurl);
        var lunar = (gauge.Nadi & NadiFlags.Lunar) != 0 ? 1 : 0;
        var solar = (gauge.Nadi & NadiFlags.Solar) != 0 ? 1 : 0;
        s.Gauges[Job.GaugeIndex(MnkDefinition.Lunar)] = (short)lunar;
        s.Gauges[Job.GaugeIndex(MnkDefinition.Solar)] = (short)solar;
        s.Gauges[Job.GaugeIndex(MnkDefinition.NadiCount)] = (short)(lunar + solar);
        s.Gauges[Job.GaugeIndex(MnkDefinition.ChakraQ)] = (short)Math.Min(40, gauge.Chakra * 4);
        if (opo + raptor + coeurl >= 3)
        {
            var blitz = Job.StatusIndex(MnkDefinition.BlitzReady);
            s.StatusLeft[blitz] = MathF.Max(0.1f, gauge.BlitzTimeRemaining / 1000f);
            s.StatusStacks[blitz] = 1;
        }

        ReadStatus(ref s, Job.StatusIndex(MnkDefinition.OpoForm), Player, (uint)SID.OpoOpoForm);
        ReadStatus(ref s, Job.StatusIndex(MnkDefinition.RaptorForm), Player, (uint)SID.RaptorForm);
        ReadStatus(ref s, Job.StatusIndex(MnkDefinition.CoeurlForm), Player, (uint)SID.CoeurlForm);
        ReadStatus(ref s, Job.StatusIndex(MnkDefinition.Formless), Player, (uint)SID.FormlessFist);
        ReadStatus(ref s, Job.StatusIndex(MnkDefinition.PerfectBalance), Player, (uint)SID.PerfectBalance);
        ReadStatus(ref s, Job.StatusIndex(MnkDefinition.RiddleOfFire), Player, (uint)SID.RiddleOfFire);
        ReadStatus(ref s, Job.StatusIndex(MnkDefinition.Brotherhood), Player, (uint)SID.Brotherhood);
        ReadStatus(ref s, Job.StatusIndex(MnkDefinition.MeditativeBrotherhood), Player, (uint)SID.MeditativeBrotherhood);
        ReadStatus(ref s, Job.StatusIndex(MnkDefinition.FiresRumination), Player, (uint)SID.FiresRumination);
        ReadStatus(ref s, Job.StatusIndex(MnkDefinition.WindsRumination), Player, (uint)SID.WindsRumination);

        ReadCooldown(ref s, Job.CooldownIndex(MnkDefinition.PerfectBalanceCD), ActionID.MakeSpell(AID.PerfectBalance));
        ReadCooldown(ref s, Job.CooldownIndex(MnkDefinition.RiddleOfFireCD), ActionID.MakeSpell(AID.RiddleOfFire));
        ReadCooldown(ref s, Job.CooldownIndex(MnkDefinition.BrotherhoodCD), ActionID.MakeSpell(AID.Brotherhood));
        ReadCooldown(ref s, Job.CooldownIndex(MnkDefinition.RiddleOfWindCD), ActionID.MakeSpell(AID.RiddleOfWind));
    }
}
