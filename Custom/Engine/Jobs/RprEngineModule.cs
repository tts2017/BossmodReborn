using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.RPR;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanRPR = BossMod.Autorotation.xan.Custom.RPR;

namespace BossMod.Autorotation;

// "RPR [Engine]": Reaper driven by the rotation engine (Custom/Engine). Level 30+ (the full Slice combo): the definition is
// built for the player's level, and BMR recreates the module when the level changes (level sync). The strategy tracks are those
// of xan RPR [Custom]; settings for skills not learned at the player's level do nothing.
public sealed class RprEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    // harnesses: replaces the built-in weights for modules created afterwards
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => WithComposition(new RotationModuleDefinition("RPR [Engine]", "Reaper on the two-tier rotation engine (burst-window planning + short search). Experimental, level 30+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.RPR), 100, 30)
            .WithStrategies<XanRPR.Strategy>());

    // the first tier (EngineRotationModule.CompositionStrategy): the xan RPR module of the same strategy tracks
    protected override RotationModule CreateBaseline() => new XanRPR(Manager, Player);

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, player.Level) : 2.5f;
        // the search runs in slices of FrameBudgetMs per frame (continuing on the next frames while the state is unchanged), so a
        // single frame never pays for the whole BudgetMs
        return new RotationEngine(RprDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? RprDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.08f };
    }

    private XanRPR.Strategy _strategy;

    protected override Actor? SelectTarget(StrategyValues strategy, Actor? primaryTarget)
    {
        _strategy = ValueConverter.FromValues<XanRPR.Strategy>(strategy);
        SetMechanicHints(_strategy.MechanicHints);
        return SelectTarget(_strategy.Targeting.Value, primaryTarget, 3);
    }

    protected override void ApplyStrategy(StrategyValues strategy, ref EngineState s, Actor? primaryTarget)
    {
        var st = _strategy;
        ApplyAoe(ref s, st.AOE);

        // basic mode: the standard combo and gauge spending only (no Arcane Circle unless forced, no Gluttony, no potion)
        var full = st.RotationMode.Value == XanRPR.RotationModeStrategy.FullMode;
        if (!full)
        {
            if (st.Buffs.Value != OffensiveStrategy.Force)
                Forbid(ref s, "ArcaneCircle");
            Forbid(ref s, "Gluttony");
        }
        ReadPotion(ref s, ActionDefinitions.IDPotionStr, full && st.Potion.Value switch
        {
            XanRPR.PotionUseStrategy.OpenerAndEvenBurst => true,
            XanRPR.PotionUseStrategy.EvenBurstExceptOpener => CombatTime > 60,
            _ => false
        });

        if (st.Buffs.Value == OffensiveStrategy.Delay)
            Forbid(ref s, "ArcaneCircle");
        else if (st.Buffs.Value == OffensiveStrategy.Force)
            Force("ArcaneCircle");

        if (st.Enshroud.Value == OffensiveStrategy.Delay)
        {
            Forbid(ref s, "Enshroud");
            Forbid(ref s, "EnshroudIdeal");
        }
        else if (st.Enshroud.Value == OffensiveStrategy.Force)
        {
            Force("EnshroudIdeal");
            Force("Enshroud");
        }
        else if (Job.HasSkill("Perfectio") && st.Buffs.Value != OffensiveStrategy.Delay)
        {
            // 50 Shroud kept for the raid buffs (ersharifst 7.5: Soul 50 / Shroud 50 before the burst, the Shroud 50 Enshroud and the Ideal
            // Host one both inside it): a plain Enshroud up to 12 s before them goes off right before the buffs
            var (raidBuffLeft, raidBuffIn) = RaidBuffTimings();
            if (raidBuffLeft <= 0 && raidBuffIn is > 0 and <= 12)
                Forbid(ref s, "Enshroud");
        }

        if (st.Communio.Value == EnabledByDefault.Disabled)
            Forbid(ref s, "Communio");

        switch (st.Slice.Value)
        {
            case XanRPR.SliceStrategy.Delay:
                Forbid(ref s, "SoulSlice");
                Forbid(ref s, "SoulScythe");
                break;
            case XanRPR.SliceStrategy.Force:
                if (ShapeTargets(s, "SoulScythe") >= 3)
                    Force("SoulScythe");
                Force("SoulSlice");
                break;
        }

        switch (st.RedGauge.Value)
        {
            case XanRPR.RedGaugeStrategy.Delay:
                Forbid(ref s, "BloodStalk");
                Forbid(ref s, "GrimSwathe");
                Forbid(ref s, "Gluttony");
                break;
            case XanRPR.RedGaugeStrategy.ReserveGluttony:
                // Soul is kept for Gluttony: the minor spenders only stop it overcapping
                if (s.Gauges[Job.GaugeIndex(RprDefinition.Soul)] < 100)
                {
                    Forbid(ref s, "BloodStalk");
                    Forbid(ref s, "GrimSwathe");
                }
                break;
            case XanRPR.RedGaugeStrategy.Force:
                Force("Gluttony");
                if (ShapeTargets(s, "GrimSwathe") >= 3)
                    Force("GrimSwathe");
                Force("BloodStalk");
                break;
        }

        if (st.PH.Value == EnabledByDefault.Disabled)
            Forbid(ref s, "PlentifulHarvest");

        if (st.HM.Value == OffensiveStrategy.Delay)
            Forbid(ref s, "HarvestMoon");
        else if (st.HM.Value == OffensiveStrategy.Force)
            Force("HarvestMoon");

        switch (st.Perf.Value)
        {
            case XanRPR.PerfectioStrategy.Delay:
                Forbid(ref s, "Perfectio");
                break;
            case XanRPR.PerfectioStrategy.Ranged:
                // kept for when the target cannot be reached in melee
                if (Player.DistanceToHitbox(primaryTarget) <= 3)
                    Forbid(ref s, "Perfectio");
                else
                    Force("Perfectio");
                break;
        }

        // in combat with the setting off, Soulsow is kept for a downtime (no attackable target); out of combat it is always cast, see Execute
        if (st.Soulsow.Value == DisabledByDefault.Disabled && Player.InCombat && primaryTarget is { IsTargetable: true })
            Forbid(ref s, "Soulsow");
    }

    protected override void ExecuteJob(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        base.ExecuteJob(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        var st = _strategy;
        UpdatePositional(Target, st.TrueNorth.Value == XanRPR.TrueNorthStrategy.Auto);
        LastLemure(st);
        Harpe(st, isMoving);
        if (!Player.InCombat && SelfStatusLeft(SID.Soulsow) <= 0 && ActionUnlocked(AID.Soulsow))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Soulsow), Player, ActionQueue.Priority.High + 2, castTime: 5);
        // Arcane Crest for damage to us predicted within 5 s
        if (st.AutoCrest.Value == EnabledByDefault.Enabled && Player.InCombat && ActionUnlocked(AID.ArcaneCrest) && PredictedDamageWithin(5))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.ArcaneCrest), Player, ActionQueue.Priority.Low);
    }

    // Communio turned off: the last Lemure goes into a reaping (the definition keeps it for Communio)
    private void LastLemure(in XanRPR.Strategy st)
    {
        if (st.Communio.Value == EnabledByDefault.Enabled || SelfStatusLeft(SID.Enshrouded) <= 0 || World.Client.GetGauge<ReaperGauge>().LemureShroud != 1)
            return;
        var aid = SelfStatusLeft(SID.EnhancedCrossReaping) > 0 ? AID.CrossReaping : AID.VoidReaping;
        Hints.ActionsToExecute.Push(ActionID.MakeSpell(aid), Target, ActionQueue.Priority.High + 3);
    }

    // Harpe (1.3 s cast, range 25): Automatic with Enhanced Harpe or while standing out of melee range, Ranged whenever out of melee range.
    // Below the engine's GCD, so it only goes off when that one cannot (out of range)
    private void Harpe(in XanRPR.Strategy st, bool isMoving)
    {
        if (st.Harpe.Value == XanRPR.HarpeStrategy.Forbid || Target == null || !Player.InCombat || !ActionUnlocked(AID.Harpe) || SelfStatusLeft(SID.Enshrouded) > 0
            || SelfStatusLeft(SID.SoulReaver) > 0 || SelfStatusLeft(SID.Executioner) > 0 || Player.DistanceToHitbox(Target) > 25)
            return;
        var outOfReach = Player.DistanceToHitbox(Target) > 3;
        var use = st.Harpe.Value == XanRPR.HarpeStrategy.Ranged ? outOfReach : SelfStatusLeft(SID.EnhancedHarpe) > GCD || outOfReach && !isMoving;
        if (use)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Harpe), Target, ActionQueue.Priority.High + 1, castTime: SelfStatusLeft(SID.EnhancedHarpe) > GCD ? 0 : 1.3f);
    }

    // Gibbet wants the flank, Gallows the rear (as in the regular modules): publish the positional and use True North when it would be missed
    private void UpdatePositional(Actor? target, bool useTrueNorth)
    {
        var next = LastDecision.NextGcd >= 0 ? Job.Skills[LastDecision.NextGcd].Name : "";
        var pos = next is "Gibbet" or "ExecutionersGibbet" ? Positional.Flank : next is "Gallows" or "ExecutionersGallows" ? Positional.Rear : Positional.Any;
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
        if (useTrueNorth && imminent && !correct && Player.Level >= 50)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.TrueNorth), Player, ActionQueue.Priority.Low + 2, delay: MathF.Max(0, GCD - 0.8f));
    }

    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    // Level sync (below 100): The Balance RPR Leveling Guide's per-band priorities (docs/rebuild/engine-design.md section 20)
    // the 2-minute burst anchor the assumed raid-buff cycle follows on a pull without a countdown (EngineRotationModule.AssumedCycleStart)
    protected override string? MainAnchorCooldown => RprDefinition.ArcaneCircleCD;

    protected override bool HasSyncedRules => true;

    protected override int SyncedOgcd(in EngineState s, in EngineTimeline tl)
    {
        // Arcane Circle on cooldown, once Death's Design is up (Shadow of Death > Arcane Circle); the potion inside it
        if (StatusLeft(s, RprDefinition.DeathsDesign) > 0 && Legal(s, tl, "ArcaneCircle") is var ac and >= 0)
            return ac;
        if (Legal(s, tl, "Potion") is var potion and >= 0)
            return potion;
        // Enshroud: Sacrificium (92+), Lemure's Slice / Scythe with 2 Void Shroud (after every second reaping)
        if (FirstLegal(s, tl, "Sacrificium", "LemuresScythe", "LemuresSlice") is var lemure and >= 0)
            return lemure;
        var soul = Gauge(s, RprDefinition.Soul);
        // Gluttony on cooldown at 50 Soul
        if (Legal(s, tl, "Gluttony") is var gluttony and >= 0)
            return gluttony;
        // Enshroud at 50 Shroud or with Ideal Host, unless Soul Slice is about to sit at two charges or Gluttony is about to come back
        // (10 s; 13 s from 90) while it could be spent; never held when the next reaver GCDs would overcap Shroud
        var enshroud = FirstLegal(s, tl, "EnshroudIdeal", "Enshroud");
        if (enshroud >= 0)
        {
            var shroud = Gauge(s, RprDefinition.Shroud);
            var sliceCapping = Job.HasSkill("SoulSlice") && soul <= 50 && Job.Cooldowns[Job.CooldownIndex(RprDefinition.SoulSliceCD)].MaxCharges > 1
                && (Charges(s, RprDefinition.SoulSliceCD) >= 2 || Charges(s, RprDefinition.SoulSliceCD) == 1 && s.CdReadyIn[Job.CooldownIndex(RprDefinition.SoulSliceCD)] <= 10);
            var gluttonySoon = Job.HasSkill("Gluttony") && soul >= 50 && ReadyIn(s, RprDefinition.GluttonyCD) < (Job.HasSkill("Communio") ? 13 : 10);
            if (!(sliceCapping || gluttonySoon) || shroud + 20 > 100)
                return enshroud;
        }
        // Blood Stalk / Unveiled (Grim Swathe on 3+ targets) at 50 Soul with no reaver, keeping 50 for a Gluttony back within 10 s
        if (soul >= 50 && (!Job.HasSkill("Gluttony") || ReadyIn(s, RprDefinition.GluttonyCD) > 10 || soul >= 100))
            return FirstLegal(s, tl, "GrimSwathe", "BloodStalk");
        return -1;
    }

    protected override int SyncedGcd(in EngineState s, in EngineTimeline tl)
    {
        // Enshroud: Communio (90+) with the last Lemure, else the reapings, alternating for the enhanced one
        if (StatusLeft(s, RprDefinition.Enshrouded) > 0)
            return FirstLegal(s, tl, "Communio", "GrimReaping", StatusLeft(s, RprDefinition.EnhancedCross) > 0 ? "CrossReaping" : "VoidReaping", "VoidReaping", "CrossReaping");
        // reavers from Gluttony / Blood Stalk at once (Gibbet / Gallows alternate through the enhanced statuses; Guillotine on 3+ targets)
        if (FirstLegal(s, tl, "ExecutionersGuillotine", "ExecutionersGibbet", "ExecutionersGallows", "Guillotine", "Gibbet", "Gallows") is var reaver and >= 0)
            return reaver;
        // Death's Design: refreshed before it runs out, before an Enshroud that needs 13 s of it, and with Arcane Circle coming up below 30 s
        var dd = StatusLeft(s, RprDefinition.DeathsDesign);
        var enshroudNext = (Gauge(s, RprDefinition.Shroud) >= 50 || StatusLeft(s, RprDefinition.IdealHost) > 0) && Job.HasSkill("Enshroud") && ReadyIn(s, RprDefinition.EnshroudCD) <= Job.BaseGcd && dd < 15;
        var circleNext = Job.HasSkill("ArcaneCircle") && !Disabled(s, "ArcaneCircle") && ReadyIn(s, RprDefinition.ArcaneCircleCD) <= Job.BaseGcd && dd < 30;
        if (dd < 5 || enshroudNext || circleNext)
            if (FirstLegal(s, tl, "WhorlOfDeath", "ShadowOfDeath") is var sod and >= 0)
                return sod;
        // Plentiful Harvest (88+) with Immortal Sacrifice and no Bloodsown Circle
        if (Legal(s, tl, "PlentifulHarvest") is var harvest and >= 0)
            return harvest;
        // Soul Slice / Soul Scythe at 50 Soul or less
        if (FirstLegal(s, tl, "SoulScythe", "SoulSlice") is var slice and >= 0)
            return slice;
        // Harvest Moon inside Arcane Circle (the pre-pull Soulsow), or before the fight ends
        if ((StatusLeft(s, RprDefinition.ArcaneCircle) > 0 || tl.FightEndIn - s.Time < 5) && Legal(s, tl, "HarvestMoon") is var moon and >= 0)
            return moon;
        // the combo (Spinning Scythe -> Nightmare Scythe on 3+ targets)
        var combo = ComboIs(s, "WaxingSlice") ? Legal(s, tl, "InfernalSlice")
            : ComboIs(s, "Slice") ? Legal(s, tl, "WaxingSlice")
            : ComboIs(s, "SpinningScythe") ? Legal(s, tl, "NightmareScythe") : -1;
        if (combo >= 0)
            return combo;
        if (FirstLegal(s, tl, "SpinningScythe", "Slice") is var start and >= 0)
            return start;
        // nothing to attack: Soulsow (the track keeps it to downtime)
        return Legal(s, tl, "Soulsow");
    }

    protected override ActionID ActionFor(SkillDef skill) => skill.Name switch
    {
        "Potion" => ActionDefinitions.IDPotionStr,
        "BloodStalk" when Player.FindStatus(SID.EnhancedGibbet) != null => ActionID.MakeSpell(AID.UnveiledGibbet),
        "BloodStalk" when Player.FindStatus(SID.EnhancedGallows) != null => ActionID.MakeSpell(AID.UnveiledGallows),
        _ => base.ActionFor(skill)
    };

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        var gauge = World.Client.GetGauge<ReaperGauge>();
        s.Gauges[Job.GaugeIndex(RprDefinition.Soul)] = gauge.Soul;
        s.Gauges[Job.GaugeIndex(RprDefinition.Shroud)] = gauge.Shroud;
        s.Gauges[Job.GaugeIndex(RprDefinition.Lemure)] = gauge.LemureShroud;
        s.Gauges[Job.GaugeIndex(RprDefinition.Void)] = gauge.VoidShroud;

        ReadStatus(ref s, Job.StatusIndex(RprDefinition.DeathsDesign), primaryTarget, (uint)SID.DeathsDesign);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.ArcaneCircle), Player, (uint)SID.ArcaneCircle);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.SoulReaver), Player, (uint)SID.SoulReaver);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.Executioner), Player, (uint)SID.Executioner);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.EnhancedGibbet), Player, (uint)SID.EnhancedGibbet);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.EnhancedGallows), Player, (uint)SID.EnhancedGallows);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.EnhancedVoid), Player, (uint)SID.EnhancedVoidReaping);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.EnhancedCross), Player, (uint)SID.EnhancedCrossReaping);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.Enshrouded), Player, (uint)SID.Enshrouded);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.Oblatio), Player, (uint)SID.Oblatio);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.IdealHost), Player, (uint)SID.IdealHost);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.PerfectioOcculta), Player, (uint)SID.PerfectioOcculta);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.PerfectioParata), Player, (uint)SID.PerfectioParata);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.ImmortalSacrifice), Player, (uint)SID.ImmortalSacrifice, fromPlayer: false);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.Bloodsown), Player, (uint)SID.BloodsownCircle);
        ReadStatus(ref s, Job.StatusIndex(RprDefinition.Soulsow), Player, (uint)SID.Soulsow);

        ReadCooldown(ref s, Job.CooldownIndex(RprDefinition.SoulSliceCD), ActionID.MakeSpell(AID.SoulSlice));
        ReadCooldown(ref s, Job.CooldownIndex(RprDefinition.ArcaneCircleCD), ActionID.MakeSpell(AID.ArcaneCircle));
        ReadCooldown(ref s, Job.CooldownIndex(RprDefinition.GluttonyCD), ActionID.MakeSpell(AID.Gluttony));
        ReadCooldown(ref s, Job.CooldownIndex(RprDefinition.EnshroudCD), ActionID.MakeSpell(AID.Enshroud));
        var potion = Job.CooldownIndex(RprDefinition.PotionCD);
        s.Charges[potion] = 0;
        s.CdReadyIn[potion] = 10000;
        ReadCombo(ref s);
    }
}
