using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.SAM;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace BossMod.Autorotation;

// "SAM [Engine]": Samurai driven by the rotation engine (Custom/Engine). Level 30+ (Gekko and Higanbana): the definition is
// built for the player's level, and BMR recreates the module when the level changes (level sync). No potion, opener countdown
// (pre-pull Meikyo / Gekko), Enpi, Hagakure, Meditate or Gyoten / Yaten.
public sealed class SamEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => new("SAM [Engine]", "Samurai on the two-tier rotation engine (burst-window planning + short search). Experimental, level 30+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.SAM), 100, 30);

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        // the definition applies Fuka's haste itself: the base GCD is without it
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, 100, player.Level) : 2.5f;
        return new RotationEngine(SamDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? SamDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.05f, ReplanInterval = 8 };
    }

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        base.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        UpdatePositional(primaryTarget);
    }

    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    // Gekko wants the rear, Kasha the flank; True North when the next one would be missed
    private void UpdatePositional(Actor? target)
    {
        var next = LastDecision.NextGcd >= 0 ? Job.Skills[LastDecision.NextGcd].Name : "";
        var pos = next.StartsWith("Gekko") ? Positional.Rear : next.StartsWith("Kasha") ? Positional.Flank : Positional.Any;
        if (target == null || target.Omnidirectional || pos == Positional.Any || target.TargetID == Player.InstanceID && target.CastInfo == null && !target.IsStrikingDummy)
        {
            Hints.RecommendedPositional = (target, Positional.Any, false, true);
            return;
        }
        var trueNorth = StatusDetails(Player, ClassShared.SID.TrueNorth, Player.InstanceID).Left > GCD;
        var dot = target.Rotation.ToDirection().Dot((Player.Position - target.Position).Normalized());
        var correct = trueNorth || (pos == Positional.Flank ? MathF.Abs(dot) < 0.7071067f : dot < -0.7071068f);
        var imminent = !trueNorth && GCD < 2.5f;
        Hints.RecommendedPositional = (target, pos, imminent, correct);
        if (imminent && !correct)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.TrueNorth), Player, ActionQueue.Priority.Low + 2, delay: MathF.Max(0, GCD - 0.8f));
    }

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        var gauge = World.Client.GetGauge<SamuraiGauge>();
        s.Gauges[Job.GaugeIndex(SamDefinition.Kenki)] = gauge.Kenki;
        s.Gauges[Job.GaugeIndex(SamDefinition.Meditation)] = gauge.MeditationStacks;
        var setsu = gauge.SenFlags.HasFlag(SenFlags.Setsu) ? 1 : 0;
        var getsu = gauge.SenFlags.HasFlag(SenFlags.Getsu) ? 1 : 0;
        var ka = gauge.SenFlags.HasFlag(SenFlags.Ka) ? 1 : 0;
        s.Gauges[Job.GaugeIndex(SamDefinition.Setsu)] = (short)setsu;
        s.Gauges[Job.GaugeIndex(SamDefinition.Getsu)] = (short)getsu;
        s.Gauges[Job.GaugeIndex(SamDefinition.Ka)] = (short)ka;
        s.Gauges[Job.GaugeIndex(SamDefinition.SenCount)] = (short)(setsu + getsu + ka);
        if (gauge.Kaeshi == KaeshiAction.Namikiri)
        {
            var nr = Job.StatusIndex(SamDefinition.NamikiriReady);
            s.StatusLeft[nr] = 30;
            s.StatusStacks[nr] = 1;
        }

        ReadStatus(ref s, Job.StatusIndex(SamDefinition.Fugetsu), Player, (uint)SID.Fugetsu);
        ReadStatus(ref s, Job.StatusIndex(SamDefinition.Fuka), Player, (uint)SID.Fuka);
        ReadStatus(ref s, Job.StatusIndex(SamDefinition.Meikyo), Player, (uint)SID.MeikyoShisui);
        ReadStatus(ref s, Job.StatusIndex(SamDefinition.Tendo), Player, (uint)SID.Tendo);
        ReadStatus(ref s, Job.StatusIndex(SamDefinition.OgiReady), Player, (uint)SID.OgiNamikiriReady);
        ReadStatus(ref s, Job.StatusIndex(SamDefinition.ZanshinReady), Player, (uint)SID.ZanshinReady);
        ReadStatus(ref s, Job.StatusIndex(SamDefinition.KaeshiGoken), Player, (uint)SID.KaeshiGoken);
        ReadStatus(ref s, Job.StatusIndex(SamDefinition.KaeshiSetsugekka), Player, (uint)SID.KaeshiSetsugekka);
        ReadStatus(ref s, Job.StatusIndex(SamDefinition.TendoKaeshiGoken), Player, (uint)SID.TendoKaeshiGoken);
        ReadStatus(ref s, Job.StatusIndex(SamDefinition.TendoKaeshiSetsugekka), Player, (uint)SID.TendoKaeshiSetsugekka);
        ReadStatus(ref s, Job.StatusIndex(SamDefinition.Higanbana), primaryTarget, (uint)SID.Higanbana);

        ReadCooldown(ref s, Job.CooldownIndex(SamDefinition.MeikyoCD), ActionID.MakeSpell(AID.MeikyoShisui));
        ReadCooldown(ref s, Job.CooldownIndex(SamDefinition.IkishotenCD), ActionID.MakeSpell(AID.Ikishoten));
        ReadCooldown(ref s, Job.CooldownIndex(SamDefinition.SeneiCD), ActionID.MakeSpell(AID.HissatsuSenei));
        ReadCooldown(ref s, Job.CooldownIndex(SamDefinition.ShohaCD), ActionID.MakeSpell(AID.Shoha));
        ReadCombo(ref s);
    }
}
