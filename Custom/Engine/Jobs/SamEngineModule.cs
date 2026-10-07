using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.SAM;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanSAM = BossMod.Autorotation.xan.Custom.SAM;

namespace BossMod.Autorotation;

// "SAM [Engine]": Samurai driven by the rotation engine (Custom/Engine). Level 30+ (Gekko and Higanbana): the definition is
// built for the player's level, and BMR recreates the module when the level changes (level sync). The strategy tracks are those
// of xan SAM [Custom]; the countdown opener (pre-pull Meikyo Shisui / Gekko or Kasha), Enpi and Meditate are pressed by this
// module; settings for skills not learned at the player's level do nothing. No Hagakure or Gyoten / Yaten.
public sealed class SamEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => new RotationModuleDefinition("SAM [Engine]", "Samurai on the two-tier rotation engine (burst-window planning + short search). Experimental, level 30+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.SAM), 100, 30)
            .WithStrategies<XanSAM.Strategy>();

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        // the definition applies Fuka's haste itself: the base GCD is without it
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, 100, player.Level) : 2.5f;
        return new RotationEngine(SamDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? SamDefinition.DefaultWeights(player.Level)) { FrameBudgetMs = FrameBudgetOverride ?? 0.05f, ReplanInterval = 8 };
    }

    private XanSAM.Strategy _strategy;
    private DateTime _nothingToAttackSince;

    protected override Actor? SelectTarget(StrategyValues strategy, Actor? primaryTarget)
    {
        _strategy = ValueConverter.FromValues<XanSAM.Strategy>(strategy);
        SetMechanicHints(_strategy.MechanicHints);
        return SelectTarget(_strategy.Targeting.Value, primaryTarget, 3);
    }

    private static readonly string[] BurstSkills = ["Ikishoten", "HissatsuSenei", "HissatsuGuren", "Zanshin", "Shoha"];
    private static readonly string[] Tsubame = ["KaeshiGoken", "KaeshiSetsugekka", "TendoKaeshiGoken", "TendoKaeshiSetsugekka"];
    private static readonly string[] Namikiri = ["OgiNamikiri", "KaeshiNamikiri"];

    protected override void ApplyStrategy(StrategyValues strategy, ref EngineState s, Actor? primaryTarget)
    {
        var st = _strategy;
        ApplyAoe(ref s, st.AOE);
        if (st.Buffs.Value == OffensiveStrategy.Delay)
        {
            foreach (var sk in BurstSkills)
                Forbid(ref s, sk);
        }
        else if (st.Buffs.Value == OffensiveStrategy.Force)
        {
            Force("Ikishoten");
            if (s.Targets >= 3)
                Force("HissatsuGuren");
            Force("HissatsuSenei");
            Force("Zanshin");
            Force("Shoha");
        }

        // the two-minute burst: Ikishoten's Ogi Namikiri / Zanshin are up. Before Ogi Namikiri (90) the 30 s after Ikishoten (68), the
        // window Ogi Namikiri Ready would last; before Ikishoten there is none
        var ikishoten = Job.CooldownIndex(SamDefinition.IkishotenCD);
        var burst = Job.HasSkill("OgiNamikiri")
            ? s.HasStatus(Job.StatusIndex(SamDefinition.OgiReady)) || s.HasStatus(Job.StatusIndex(SamDefinition.NamikiriReady)) || s.HasStatus(Job.StatusIndex(SamDefinition.ZanshinReady))
            : Job.HasSkill("Ikishoten") && s.Charges[ikishoten] == 0 && s.CdReadyIn[ikishoten] > Job.Cooldowns[ikishoten].Recast - 30;
        UsePotion(ActionDefinitions.IDPotionStr, st.Potion.Value == XanSAM.SamPotionStrategy.TwoMinuteBurst && burst);
        foreach (var sk in Tsubame)
            HoldOrForce(ref s, sk, st.Tsubame.Value, burst);
        foreach (var sk in Namikiri)
            HoldOrForce(ref s, sk, st.Namikiri.Value == XanSAM.NamikiriStrategy.Auto ? XanSAM.TsubameStrategy.Auto
                : st.Namikiri.Value == XanSAM.NamikiriStrategy.Force ? XanSAM.TsubameStrategy.Force
                : st.Namikiri.Value == XanSAM.NamikiriStrategy.Hold ? XanSAM.TsubameStrategy.Hold : XanSAM.TsubameStrategy.Delay, burst);

        if (st.Higanbana.Value == XanSAM.BanaStrategy.Delay)
            Forbid(ref s, "Higanbana");
        else if (st.Higanbana.Value == XanSAM.BanaStrategy.Force)
            Force("Higanbana");

        var meikyo = Job.CooldownIndex(SamDefinition.MeikyoCD);
        switch (st.Meikyo.Value)
        {
            case XanSAM.MeikyoStrategy.Delay:
                Forbid(ref s, "MeikyoShisui");
                break;
            case XanSAM.MeikyoStrategy.Force or XanSAM.MeikyoStrategy.Cooldown:
                Force("MeikyoShisui");
                break;
            case XanSAM.MeikyoStrategy.HoldOne:
                // one charge kept: used only when the second one is about to come back
                if (s.Charges[meikyo] < 2 && s.CdReadyIn[meikyo] > Job.BaseGcd)
                    Forbid(ref s, "MeikyoShisui");
                break;
        }
    }

    // Tsubame-gaeshi / Namikiri: Hold keeps them for the two-minute burst
    private void HoldOrForce(ref EngineState s, string skill, XanSAM.TsubameStrategy setting, bool burst)
    {
        if (setting == XanSAM.TsubameStrategy.Delay || setting == XanSAM.TsubameStrategy.Hold && !burst)
            Forbid(ref s, skill);
        else if (setting == XanSAM.TsubameStrategy.Force)
            Force(skill);
    }

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        // the countdown opener needs Meikyo Shisui (the combo finisher on the pull); without it the engine plans the pull
        if (ActionUnlocked(AID.MeikyoShisui) && !Player.InCombat && World.Client.CountdownRemaining > 0)
        {
            Prepull(ValueConverter.FromValues<XanSAM.Strategy>(strategy), primaryTarget);
            return;
        }
        base.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        var st = _strategy;
        UpdatePositional(Target, st.TrueNorth.Value == XanSAM.TrueNorthStrategy.Auto);
        Enpi(st);
        Meditate(st, isMoving);
    }

    // countdown opener: Meikyo Shisui at 14 s (11 s for the openers with an early Higanbana), then Gekko (Kasha for the haste-first
    // openers) landing on the pull
    private void Prepull(in XanSAM.Strategy st, Actor? target)
    {
        var countdown = World.Client.CountdownRemaining ?? 0;
        var opener = st.Opener.Value;
        var earlyBana = opener is XanSAM.OpenerStrategy.GekkoBana or XanSAM.OpenerStrategy.KashaBana;
        var earlyKasha = opener is XanSAM.OpenerStrategy.KashaStandard or XanSAM.OpenerStrategy.KashaBana;
        var meikyoLeft = SelfStatusLeft(SID.MeikyoShisui);
        if (st.Meikyo.Value != XanSAM.MeikyoStrategy.Delay && meikyoLeft <= 0 && countdown < (earlyBana ? 11 : 14))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.MeikyoShisui), Player, ActionQueue.Priority.High);
        if (meikyoLeft > countdown && countdown < 0.76f && target != null)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(earlyKasha ? AID.Kasha : AID.Gekko), target, ActionQueue.Priority.High + 2);
    }

    // Enpi (range 20): Enhanced with Enhanced Enpi, or out of melee range with no combo to lose; Ranged whenever out of melee range.
    // Below the engine's GCD, so it only goes off when that one cannot (out of range)
    private void Enpi(in XanSAM.Strategy st)
    {
        if (st.Enpi.Value == XanSAM.EnpiStrategy.None || Target == null || !Player.InCombat || !ActionUnlocked(AID.Enpi) || Player.DistanceToHitbox(Target) > 20)
            return;
        var outOfReach = Player.DistanceToHitbox(Target) > 3;
        var use = st.Enpi.Value == XanSAM.EnpiStrategy.Ranged ? outOfReach
            : SelfStatusLeft(SID.EnhancedEnpi) > GCD || outOfReach && World.Client.ComboState.Remaining <= 0;
        if (use)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Enpi), Target, ActionQueue.Priority.High + 1);
    }

    // Meditate while there is nothing to attack (in combat, standing): after 2.5 s without a target, Kenki / Meditation not full
    private void Meditate(in XanSAM.Strategy st, bool isMoving)
    {
        var nothingToAttack = Target == null || !Target.IsTargetable;
        if (!nothingToAttack)
            _nothingToAttackSince = default;
        else if (_nothingToAttackSince == default)
            _nothingToAttackSince = World.CurrentTime;
        var gauge = World.Client.GetGauge<SamuraiGauge>();
        if (st.Meditate.Value == EnabledByDefault.Disabled || !Player.InCombat || isMoving || !nothingToAttack || !ActionUnlocked(AID.Meditate) || SelfStatusLeft(SID.Meditate) > 0
            || gauge.Kenki >= 100 && (gauge.MeditationStacks >= 3 || !ActionUnlocked(AID.Shoha)) || (World.CurrentTime - _nothingToAttackSince).TotalSeconds < 2.5)
            return;
        // Meditate puts the GCD on recast: not when the target is known to return before its first tick
        if (Manager.Planner?.EstimateTimeToNextDowntime() is (true, var returnIn) && returnIn < 3.2f
            || Hints.Disengage.TargetLossIn <= 0 && Hints.Disengage.TargetReturnIn < 3.2f)
            return;
        Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Meditate), Player, ActionQueue.Priority.High + 1);
    }

    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    // Gekko wants the rear, Kasha the flank; True North when the next one would be missed
    private void UpdatePositional(Actor? target, bool useTrueNorth)
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
        if (useTrueNorth && imminent && !correct && ActionUnlocked(ClassShared.AID.TrueNorth))
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
