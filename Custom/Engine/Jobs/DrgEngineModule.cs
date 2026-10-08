using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.Data;
using BossMod.DRG;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanDRG = BossMod.Autorotation.xan.Custom.DRG;

namespace BossMod.Autorotation;

// "DRG [Engine]": Dragoon driven by the rotation engine (Custom/Engine). Level 30+ (True Thrust -> Vorpal Thrust -> Full Thrust and
// Disembowel's Power Surge, Lance Charge, Jump): the definition is built for the player's level, and BMR recreates the module when the
// level changes (level sync). The strategy tracks are those of xan DRG [Custom]; Piercing Talon, the countdown opener (True Thrust
// landing on the pull / Winged Glide), True North and the Phantom Samurai actions are pressed by this module; settings for skills not
// learned at the player's level do nothing. No potion (xan DRG has no potion track), no Elusive Jump.
public sealed class DrgEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => WithComposition(new RotationModuleDefinition("DRG [Engine]", "Dragoon on the two-tier rotation engine (burst-window planning + short search). Experimental, level 30+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.DRG, (int)Class.LNC), 100, 30)
            .WithStrategies<XanDRG.Strategy>());

    // the first tier (EngineRotationModule.CompositionStrategy): the xan DRG module of the same strategy tracks
    protected override RotationModule CreateBaseline() => new XanDRG(Manager, Player);

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, player.Level) : 2.5f;
        // a 0.5 ms frame slice: a replan (every 8 s) no longer pushes the search to the next frame, which made live play drift from the deterministic
        // result (9 fights: 617k with 0.05 ms, 625k with 0.5 ms, 634k deterministic)
        return new RotationEngine(DrgDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? DrgDefinition.DefaultWeights(player.Level)) { FrameBudgetMs = FrameBudgetOverride ?? 0.5f, ReplanInterval = 8 };
    }

    private XanDRG.Strategy _strategy;

    protected override Actor? SelectTarget(StrategyValues strategy, Actor? primaryTarget)
    {
        _strategy = ValueConverter.FromValues<XanDRG.Strategy>(strategy);
        SetMechanicHints(_strategy.MechanicHints);
        return SelectTarget(_strategy.Targeting.Value, primaryTarget, 3);
    }

    private static readonly string[] ComboStarters = ["TrueThrust", "RaidenThrust", "DoomSpike", "DraconianFury"];

    protected override void ApplyStrategy(StrategyValues strategy, ref EngineState s, Actor? primaryTarget)
    {
        var st = _strategy;
        ApplyAoe(ref s, st.AOE);

        // Battle Litany: on cooldown (the xan module waits for the first Power Surge; the engine plans it), Delay / Force
        if (st.Buffs.Value == OffensiveStrategy.Delay)
            Forbid(ref s, "BattleLitany");
        else if (st.Buffs.Value == OffensiveStrategy.Force)
            Force("BattleLitany");

        if (st.LanceCharge.Value == XanDRG.LanceChargeStrategy.Delay)
            Forbid(ref s, "LanceCharge");
        else if (st.LanceCharge.Value == XanDRG.LanceChargeStrategy.Force)
            Force("LanceCharge");

        // dives: NoMove keeps the position-changing dives, NoLock also the jump (its 0.8 s lock)
        if (st.Dive.Value != XanDRG.DiveStrategy.Allow)
        {
            Forbid(ref s, "DragonfireDive");
            Forbid(ref s, "Stardiver");
        }
        if (st.Dive.Value == XanDRG.DiveStrategy.NoLock)
            Forbid(ref s, "HighJump");

        // High Jump / Mirage Dive (as the xan module): AfterBuffs = High Jump once Lance Charge is on cooldown, Mirage Dive under Power
        // Surge; HoldMD = High Jump at once, Mirage Dive inside Lance Charge or when Dive Ready is about to run out
        var lanceUp = s.HasStatus(Job.StatusIndex(DrgDefinition.LanceCharge));
        var lanceCd = Job.CooldownIndex(DrgDefinition.LanceChargeCD);
        switch (st.HJMD.Value)
        {
            case XanDRG.HJMDStrategy.AfterBuffs:
                if (Job.HasSkill("LanceCharge") && !Disabled(s, "LanceCharge") && s.Charges[lanceCd] > 0)
                    Forbid(ref s, "HighJump");
                if (!s.HasStatus(Job.StatusIndex(DrgDefinition.PowerSurge)))
                    Forbid(ref s, "MirageDive");
                break;
            case XanDRG.HJMDStrategy.HoldMD:
                if (!lanceUp && s.StatusLeft[Job.StatusIndex(DrgDefinition.DiveReady)] >= GCD + 0.6f + 0.1f)
                    Forbid(ref s, "MirageDive");
                break;
            case XanDRG.HJMDStrategy.Delay:
                Forbid(ref s, "HighJump");
                Forbid(ref s, "MirageDive");
                break;
            case XanDRG.HJMDStrategy.Force:
                Force("HighJump");
                Force("MirageDive");
                break;
        }

        // the two combos alternate (the guides from 64; the xan module at every level): the buffing combo only with under 10 s of Power Surge
        // left (or the dot about to run out: under 3 GCDs, it lands one GCD after Disembowel), so the search does not keep refreshing it for
        // its dot and buff remainder
        if (s.StatusLeft[Job.StatusIndex(DrgDefinition.PowerSurge)] >= 10 && s.StatusLeft[Job.StatusIndex(DrgDefinition.ChaoticSpring)] >= 3 * Job.BaseGcd)
            Forbid(ref s, "Disembowel");

        // Hold GCD: no weaponskill while the plan entry lasts
        if (st.HoldGCD.Value == XanDRG.DelayStrategy.Delay)
            foreach (var sk in Job.Skills)
                if (sk.IsGcd)
                    s.DisabledSkills |= 1UL << sk.Index;

        // HP-locked target (priority "pointless"): only the combo starters, so the next real GCD grants Power Surge
        if (st.Filler.Value == XanDRG.FillerStrategy.ForceTT && Hints.FindEnemy(primaryTarget)?.Priority == AIHints.Enemy.PriorityPointless)
            foreach (var sk in Job.Skills)
                if (sk.IsGcd && Array.IndexOf(ComboStarters, sk.Name) < 0)
                    s.DisabledSkills |= 1UL << sk.Index;
    }

    protected override void ExecuteJob(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        // countdown opener: True Thrust landing on the pull (its 0.76 s application delay), Winged Glide to close the gap just before it
        if (!Player.InCombat && World.Client.CountdownRemaining is { } countdown && countdown > 0)
        {
            _strategy = ValueConverter.FromValues<XanDRG.Strategy>(strategy);
            var target = SelectTarget(_strategy.Targeting.Value, primaryTarget, 3);
            if (target != null)
            {
                if (Player.DistanceToHitbox(target) <= 3)
                {
                    if (countdown < 0.76f)
                        Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.TrueThrust), target, ActionQueue.Priority.High + 2);
                }
                else if (countdown < 0.7f && ActionUnlocked(AID.WingedGlide))
                    Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.WingedGlide), target, ActionQueue.Priority.High);
            }
            return;
        }
        base.ExecuteJob(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        var st = _strategy;
        UpdatePositional(Target);
        PiercingTalon(st);
        Phantom(st);
    }

    // Piercing Talon (range 20): out of melee range (Automatic, or EnhancedOnly with Enhanced Piercing Talon) and whenever Enhanced.
    // Below the engine's GCD, so it only goes off when that one cannot (out of range)
    private void PiercingTalon(in XanDRG.Strategy st)
    {
        if (st.Talon.Value == XanDRG.TalonStrategy.Forbid || Target == null || !Player.InCombat || !ActionUnlocked(AID.PiercingTalon))
            return;
        var dist = Player.DistanceToHitbox(Target);
        var enhanced = SelfStatusLeft(SID.EnhancedPiercingTalon) > GCD;
        if (dist > 3 && dist <= 20 && (st.Talon.Value == XanDRG.TalonStrategy.Automatic || enhanced))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.PiercingTalon), Target, ActionQueue.Priority.High + 1);
        else if (enhanced && dist <= 20)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.PiercingTalon), Target, ActionQueue.Priority.High + 1);
    }

    // Occult Crescent (as the xan module): Zeninage with Life of the Dragon, Power Surge and Lance Charge up; Iainuki on cooldown
    private void Phantom(in XanDRG.Strategy st)
    {
        if (Target == null || !Player.InCombat || SelfStatusLeft(SID.DraconianFire) > GCD)
            return;
        var gauge = World.Client.GetGauge<DragoonGauge>();
        var lotd = gauge.LotdTimer * 0.001f;
        if (st.Zeninage.Value == EnabledByDefault.Enabled && lotd > GCD && SelfStatusLeft(SID.PowerSurge) > GCD && SelfStatusLeft(SID.LanceCharge) > GCD && PhantomActionReady(PhantomID.Zeninage))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(PhantomID.Zeninage), Target, ActionQueue.Priority.High + 3.3f);
        if (st.Iainuki.Value == EnabledByDefault.Enabled && PhantomActionReady(PhantomID.Iainuki) && DutyActionCD(ActionID.MakeSpell(PhantomID.Zeninage)) > GCD)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(PhantomID.Iainuki), Target, ActionQueue.Priority.High + 3.2f);
    }

    // Chaotic Spring / Wheeling Thrust want the rear, Fang and Claw the flank: publish the positional and use True North when it would be
    // missed (the xan module has no True North track: always automatic, below every other ability)
    private void UpdatePositional(Actor? target)
    {
        var next = LastDecision.NextGcd >= 0 ? Job.Skills[LastDecision.NextGcd].Name : "";
        var pos = next is "ChaoticSpring" or "WheelingThrust" ? Positional.Rear : next == "FangAndClaw" ? Positional.Flank : Positional.Any;
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
        if (imminent && !correct && Player.InCombat && ActionUnlocked(ClassShared.AID.TrueNorth))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.TrueNorth), Player, ActionQueue.Priority.Low - 20, delay: MathF.Max(0, GCD - 0.8f));
    }

    // the combo GCDs are a 10y line towards the target
    protected override byte CountTargets(Actor? primaryTarget)
        => primaryTarget == null ? (byte)1 : (byte)Math.Max(1, Hints.NumPriorityTargetsInAOERect(Player.Position, Player.DirectionTo(primaryTarget), 10, 2));

    // Level sync (below 100): The Balance DRG Leveling Guide (1-17 / 18-25 / 26-49 / 50-55 / 56-57 / 58-63 / 64-100) and the xan module's
    // weave rules (docs/rebuild/engine-design.md section 25)
    protected override bool HasSyncedRules => true;

    protected override int SyncedOgcd(in EngineState s, in EngineTimeline tl)
    {
        var lance = StatusLeft(s, DrgDefinition.LanceCharge) > 0;
        var lanceSkill = Job.HasSkill("LanceCharge") && !Disabled(s, "LanceCharge");
        // Lance Charge on cooldown once Power Surge is up (the guides: every buff on cooldown); Battle Litany and Geirskogul inside it (the
        // burst opens with the three together), on cooldown when Lance Charge is off
        if ((StatusLeft(s, DrgDefinition.PowerSurge) > 0 || !Job.HasSkill("Disembowel")) && Legal(s, tl, "LanceCharge") is var lc and >= 0)
            return lc;
        var lanceWindow = lance || !lanceSkill || ReadyIn(s, DrgDefinition.LanceChargeCD) > 20;
        if (lanceWindow && Legal(s, tl, "BattleLitany") is var litany and >= 0)
            return litany;
        if (lanceWindow && StatusLeft(s, DrgDefinition.NastrondReady) <= 0 && Legal(s, tl, "Geirskogul") is var geirskogul and >= 0)
            return geirskogul;
        // Wyrmwind Thrust at 2 Focus: under Life of the Dragon, or when the next GCD (Raiden Thrust / Draconian Fury) would overcap it
        var raidenNext = StatusLeft(s, DrgDefinition.DraconianFire) > 0 && s.ComboSkill == EngineLimits.NoCombo;
        if (Gauge(s, DrgDefinition.Focus) >= 2 && (StatusLeft(s, DrgDefinition.LifeOfTheDragon) > 0 || raidenNext) && Legal(s, tl, "WyrmwindThrust") is var wyrmwind and >= 0)
            return wyrmwind;
        // the jumps on cooldown (the strategy track gates them), Mirage Dive from Dive Ready
        if (FirstLegal(s, tl, "HighJump", "MirageDive") is var jump and >= 0)
            return jump;
        // Life Surge before Heavens' Thrust / Drakesbane (Coerthan Torment in AoE): inside Lance Charge, or whenever the charges would sit full
        if ((lance || FullIn(s, DrgDefinition.LifeSurgeCD) <= Job.BaseGcd) && FirstLegal(s, tl, "LifeSurgeAoe", "LifeSurgeDrakesbane", "LifeSurge") is var surge and >= 0)
            return surge;
        // Dragonfire Dive inside Lance Charge (or on cooldown without it), then the Life of the Dragon abilities: Nastrond, Stardiver, Starcross, Rise of the Dragon
        if (lanceWindow && Legal(s, tl, "DragonfireDive") is var dive and >= 0)
            return dive;
        return FirstLegal(s, tl, "Nastrond", "Stardiver", "Starcross", "RiseOfTheDragon");
    }

    protected override int SyncedGcd(in EngineState s, in EngineTimeline tl)
    {
        var aoe = ShapeTargets(s, "DoomSpike") >= 3 && Job.HasSkill("DoomSpike") && !Disabled(s, "DoomSpike");
        if (aoe)
        {
            // Doom Spike / Draconian Fury -> Sonic Thrust -> Coerthan Torment; below Sonic Thrust (62) Power Surge still comes from Disembowel
            if (ComboIs(s, "SonicThrust") && Legal(s, tl, "CoerthanTorment") is var torment and >= 0)
                return torment;
            if ((ComboIs(s, "DoomSpike") || ComboIs(s, "DraconianFury")) && Legal(s, tl, "SonicThrust") is var sonic and >= 0)
                return sonic;
            if (!Job.HasSkill("SonicThrust") && StatusLeft(s, DrgDefinition.PowerSurge) <= Job.BaseGcd)
            {
                if ((ComboIs(s, "TrueThrust") || ComboIs(s, "RaidenThrust")) && Legal(s, tl, "Disembowel") is var disembowel and >= 0)
                    return disembowel;
                if (FirstLegal(s, tl, "RaidenThrust", "TrueThrust") is var start and >= 0)
                    return start;
            }
            if (FirstLegal(s, tl, "DraconianFury", "DoomSpike") is var spike and >= 0)
                return spike;
        }
        // the single-target combos, alternating so Power Surge (and the dot) never drop: the fifth step, the fourth, the third, then the
        // second: the buffing combo (Disembowel) with under 10 s of Power Surge left or the dot under 3 GCDs, else the damage combo
        if ((ComboIs(s, "FangAndClaw") || ComboIs(s, "WheelingThrust")) && Legal(s, tl, "Drakesbane") is var drakesbane and >= 0)
            return drakesbane;
        if (ComboIs(s, "ChaoticSpring") && Legal(s, tl, "WheelingThrust") is var wheeling and >= 0)
            return wheeling;
        if (ComboIs(s, "HeavensThrust") && Legal(s, tl, "FangAndClaw") is var fang and >= 0)
            return fang;
        if (ComboIs(s, "Disembowel") && Legal(s, tl, "ChaoticSpring") is var chaos and >= 0)
            return chaos;
        if (ComboIs(s, "VorpalThrust") && Legal(s, tl, "HeavensThrust") is var heavens and >= 0)
            return heavens;
        if (ComboIs(s, "TrueThrust") || ComboIs(s, "RaidenThrust"))
        {
            if ((StatusLeft(s, DrgDefinition.PowerSurge) < 10 || Job.HasSkill("ChaoticSpring") && StatusLeft(s, DrgDefinition.ChaoticSpring) < 3 * Job.BaseGcd) && Legal(s, tl, "Disembowel") is var disembowel and >= 0)
                return disembowel;
            if (FirstLegal(s, tl, "VorpalThrust", "Disembowel") is var second and >= 0)
                return second;
        }
        return FirstLegal(s, tl, "RaidenThrust", "TrueThrust");
    }

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        var gauge = World.Client.GetGauge<DragoonGauge>();
        s.Gauges[Job.GaugeIndex(DrgDefinition.Focus)] = gauge.FirstmindsFocusCount;
        var lotd = gauge.LotdTimer * 0.001f;
        if (lotd > 0)
        {
            var i = Job.StatusIndex(DrgDefinition.LifeOfTheDragon);
            s.StatusLeft[i] = lotd;
            s.StatusStacks[i] = 1;
        }

        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.PowerSurge), Player, (uint)SID.PowerSurge);
        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.LanceCharge), Player, (uint)SID.LanceCharge);
        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.BattleLitany), Player, (uint)SID.BattleLitany);
        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.LifeSurge), Player, (uint)SID.LifeSurge);
        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.NastrondReady), Player, (uint)SID.NastrondReady);
        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.DiveReady), Player, (uint)SID.DiveReady);
        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.DraconianFire), Player, (uint)SID.DraconianFire);
        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.DragonsFlight), Player, (uint)SID.DragonsFlight);
        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.StarcrossReady), Player, (uint)SID.StarcrossReady);
        // the dot on the target: Chaotic Spring from 86, Chaos Thrust before
        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.ChaoticSpring), primaryTarget, (uint)SID.ChaoticSpring);
        ReadStatus(ref s, Job.StatusIndex(DrgDefinition.ChaoticSpring), primaryTarget, (uint)SID.ChaosThrust);

        ReadCooldown(ref s, Job.CooldownIndex(DrgDefinition.LifeSurgeCD), ActionID.MakeSpell(AID.LifeSurge));
        ReadCooldown(ref s, Job.CooldownIndex(DrgDefinition.LanceChargeCD), ActionID.MakeSpell(AID.LanceCharge));
        ReadCooldown(ref s, Job.CooldownIndex(DrgDefinition.LitanyCD), ActionID.MakeSpell(AID.BattleLitany));
        ReadCooldown(ref s, Job.CooldownIndex(DrgDefinition.JumpCD), ActionID.MakeSpell(Player.Level >= 74 ? AID.HighJump : AID.Jump));
        ReadCooldown(ref s, Job.CooldownIndex(DrgDefinition.GeirskogulCD), ActionID.MakeSpell(AID.Geirskogul));
        ReadCooldown(ref s, Job.CooldownIndex(DrgDefinition.DragonfireCD), ActionID.MakeSpell(AID.DragonfireDive));
        ReadCooldown(ref s, Job.CooldownIndex(DrgDefinition.StardiverCD), ActionID.MakeSpell(AID.Stardiver));
        ReadCooldown(ref s, Job.CooldownIndex(DrgDefinition.WyrmwindCD), ActionID.MakeSpell(AID.WyrmwindThrust));
        ReadCombo(ref s);
        // the fourth-step window (Drakesbane / its Life Surge): the combo is Fang and Claw or Wheeling Thrust
        if (s.ComboSkill != EngineLimits.NoCombo && Job.Skills[s.ComboSkill].Name is "FangAndClaw" or "WheelingThrust")
            s.Gauges[Job.GaugeIndex(DrgDefinition.FourthStep)] = 1;
    }
}
