using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.NIN;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;

namespace BossMod.Autorotation;

// "NIN [Engine]": Ninja driven by the rotation engine (Custom/Engine). Level 100 only; below that it does nothing, so use it
// next to (not instead of) a regular NIN module when levelling. The engine plans whole ninjutsu (mudra sequences) and the Ten Chi
// Jin steps as single skills; this module presses their mudras / steps one by one and keeps the engine out while a sequence
// runs. No potion, Hide, Throwing Dagger, Doton / Huton or Trick Attack / Mug (level 100 uses Kunai's Bane / Dokumori).
public sealed class NinEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;
    public static float? ReplanOverride;

    public static RotationModuleDefinition Definition()
        => new("NIN [Engine]", "Ninja on the two-tier rotation engine (burst-window planning + short search). Experimental, level 100.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.NIN), 100, 100);

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        // Increase Attack Speed (level 45) is 15% haste on weaponskills
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, Math.Min(stats.Haste, 85), player.Level) : 2.12f;
        return new RotationEngine(NinDefinition.Build(gcd), WeightsOverride?.Clone() ?? NinDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.03f, ReplanInterval = ReplanOverride ?? 8 };
    }

    private string? _sequence; // ninjutsu skill whose mudras are being pressed
    private int _prevMudraCharges = -1;
    private DateTime _sequenceLockedAt; // a mudra charge was just spent: keep _sequence until the Mudra status shows up

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        var tcj = SelfStatusDetails(SID.TenChiJin);
        if (tcj.Left > 0)
        {
            Hints.ActionsToExecute.Push(ActionID.MakeSpell((AID)NextTenChiJinStep(tcj.Stacks, CountTargets(primaryTarget) >= 3)), primaryTarget, ActionQueue.Priority.High + 2);
            return;
        }
        var scratch = EngineState.Create(Job);
        ReadCooldown(ref scratch, Job.CooldownIndex(NinDefinition.MudraCD), ActionID.MakeSpell(AID.Ten1));
        var charges = scratch.Charges[Job.CooldownIndex(NinDefinition.MudraCD)];
        if (_prevMudraCharges >= 0 && charges < _prevMudraCharges)
            _sequenceLockedAt = World.CurrentTime;
        _prevMudraCharges = charges;
        var mudra = SelfStatusDetails(SID.Mudra);
        if (mudra.Left > 0 && mudra.Stacks != 0 && _sequence != null)
        {
            var seq = NinDefinition.MudraSequence(_sequence, false);
            var done = (mudra.Stacks & 3) == 0 ? 0 : ((mudra.Stacks >> 2) & 3) == 0 ? 1 : ((mudra.Stacks >> 4) & 3) == 0 ? 2 : 3;
            var aid = done < seq.Length ? seq[done] : Job.Skills[Job.SkillIndex(_sequence)].ActionId;
            Hints.ActionsToExecute.Push(ActionID.MakeSpell((AID)aid), done < seq.Length ? Player : primaryTarget, ActionQueue.Priority.High + 2);
            return;
        }
        // the first mudra went off but its status is not visible yet: wait instead of re-planning (a different ninjutsu would not match it)
        if (_sequence != null && (World.CurrentTime - _sequenceLockedAt).TotalSeconds < 0.5)
            return;
        base.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        UpdatePositional(primaryTarget);
    }

    // Ten Chi Jin steps: Fuma Ten -> Raiton -> Suiton (Fuma Chi -> Katon -> Suiton on 3+ targets)
    private static uint NextTenChiJinStep(int param, bool aoe)
    {
        var first = param & 3;
        var second = (param >> 2) & 3;
        return first == 0 ? (aoe ? NinDefinition.AidFumaChi : NinDefinition.AidFumaTen)
            : second == 0 ? (aoe ? NinDefinition.AidTCJKaton : NinDefinition.AidTCJRaiton)
            : NinDefinition.AidTCJSuiton;
    }

    protected override ActionID ActionFor(SkillDef skill)
    {
        var seq = NinDefinition.MudraSequence(skill.Name, Player.FindStatus(SID.Kassatsu) != null);
        if (seq.Length > 0)
        {
            _sequence = skill.Name;
            return ActionID.MakeSpell((AID)seq[0]);
        }
        return skill.Name switch
        {
            "TCJCombo" => ActionID.MakeSpell(AID.FumaTen),
            "AeolianEdgeBare" => ActionID.MakeSpell(AID.AeolianEdge),
            _ => base.ActionFor(skill)
        };
    }

    protected override Actor? TargetFor(SkillDef skill, Actor? primaryTarget)
        => NinDefinition.MudraSequence(skill.Name, false).Length > 0 ? Player : base.TargetFor(skill, primaryTarget);

    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    // Aeolian Edge wants the rear, Armor Crush the flank: publish the positional and use True North when it would be missed
    private void UpdatePositional(Actor? target)
    {
        var next = LastDecision.NextGcd >= 0 ? Job.Skills[LastDecision.NextGcd].Name : "";
        var pos = next is "AeolianEdge" or "AeolianEdgeBare" ? Positional.Rear : next == "ArmorCrush" ? Positional.Flank : Positional.Any;
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

        var gauge = World.Client.GetGauge<NinjaGauge>();
        s.Gauges[Job.GaugeIndex(NinDefinition.Ninki)] = gauge.Ninki;
        s.Gauges[Job.GaugeIndex(NinDefinition.Kazematoi)] = gauge.Kazematoi;
        s.Gauges[Job.GaugeIndex(NinDefinition.Raiju)] = (short)SelfStatusDetails(SID.RaijuReady).Stacks;

        ReadStatus(ref s, Job.StatusIndex(NinDefinition.Kassatsu), Player, (uint)SID.Kassatsu);
        ReadStatus(ref s, Job.StatusIndex(NinDefinition.ShadowWalker), Player, (uint)SID.ShadowWalker);
        ReadStatus(ref s, Job.StatusIndex(NinDefinition.Meisui), Player, (uint)SID.Meisui);
        ReadStatus(ref s, Job.StatusIndex(NinDefinition.Higi), Player, (uint)SID.Higi);
        ReadStatus(ref s, Job.StatusIndex(NinDefinition.TenriReady), Player, (uint)SID.TenriJindoReady);
        ReadStatus(ref s, Job.StatusIndex(NinDefinition.Bunshin), Player, (uint)SID.Bunshin);
        ReadStatus(ref s, Job.StatusIndex(NinDefinition.PhantomReady), Player, (uint)SID.PhantomKamaitachiReady);
        ReadStatus(ref s, Job.StatusIndex(NinDefinition.KunaisBane), primaryTarget, (uint)SID.KunaisBane);
        ReadStatus(ref s, Job.StatusIndex(NinDefinition.Dokumori), primaryTarget, (uint)SID.Dokumori);

        ReadCooldown(ref s, Job.CooldownIndex(NinDefinition.MudraCD), ActionID.MakeSpell(AID.Ten1));
        ReadCooldown(ref s, Job.CooldownIndex(NinDefinition.KassatsuCD), ActionID.MakeSpell(AID.Kassatsu));
        ReadCooldown(ref s, Job.CooldownIndex(NinDefinition.TenChiJinCD), ActionID.MakeSpell(AID.TenChiJin));
        ReadCooldown(ref s, Job.CooldownIndex(NinDefinition.DokumoriCD), ActionID.MakeSpell(AID.Dokumori));
        ReadCooldown(ref s, Job.CooldownIndex(NinDefinition.KunaisBaneCD), ActionID.MakeSpell(AID.KunaisBane));
        ReadCooldown(ref s, Job.CooldownIndex(NinDefinition.DreamCD), ActionID.MakeSpell(AID.DreamWithinADream));
        ReadCooldown(ref s, Job.CooldownIndex(NinDefinition.MeisuiCD), ActionID.MakeSpell(AID.Meisui));
        ReadCooldown(ref s, Job.CooldownIndex(NinDefinition.BunshinCD), ActionID.MakeSpell(AID.Bunshin));
        ReadCombo(ref s);
    }
}
