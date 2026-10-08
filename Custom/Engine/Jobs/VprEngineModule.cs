using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.VPR;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using XanVPR = BossMod.Autorotation.xan.Custom.VPR;

namespace BossMod.Autorotation;

// "VPR [Engine]": Viper driven by the rotation engine (Custom/Engine). Level 30+ (the dual-wield combo with its four finishers and venoms):
// the definition is built for the player's level, and BMR recreates the module when the level changes (level sync). The strategy tracks
// are those of xan VPR [Custom]; Writhing Snap, Slither, the countdown opener (Steel Fangs landing on the pull, Vicewinder for the 7.5
// zero-second opener), the potion and True North are pressed by this module; settings for skills not learned at the player's level do
// nothing. The Legacies are one engine skill, pressed by the gauge's combo state.
public sealed class VprEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => new RotationModuleDefinition("VPR [Engine]", "Viper on the two-tier rotation engine (burst-window planning + short search). Experimental, level 30+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.VPR), 100, 30)
            .WithStrategies<XanVPR.Strategy>();

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        // the definition applies Swiftscaled's haste itself: the base GCD is without it
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, 100, player.Level) : 2.5f;
        return new RotationEngine(VprDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? VprDefinition.DefaultWeights(player.Level)) { FrameBudgetMs = FrameBudgetOverride ?? 0.05f, ReplanInterval = 8 };
    }

    private XanVPR.Strategy _strategy;

    protected override Actor? SelectTarget(StrategyValues strategy, Actor? primaryTarget)
    {
        _strategy = ValueConverter.FromValues<XanVPR.Strategy>(strategy);
        SetMechanicHints(_strategy.MechanicHints);
        return SelectTarget(_strategy.Targeting.Value, primaryTarget, 3);
    }

    private static readonly string[] Reawakens = ["ReawakenReady", "Reawaken"];

    // Reawaken plus the four Generations (and Ouroboros from 96) at the current weaponskill recast
    private float ReawakenSequence(in EngineState s)
    {
        var haste = s.HasStatus(Job.StatusIndex(VprDefinition.Swiftscaled)) ? 0.85f : 1f;
        return (Job.HasSkill("Ouroboros") ? 2.2f + 4 * 2.0f + 3.0f : 2.2f + 4 * 2.0f) * Job.BaseGcd / 2.5f * haste;
    }

    protected override void ApplyStrategy(StrategyValues strategy, ref EngineState s, Actor? primaryTarget)
    {
        var st = _strategy;
        ApplyAoe(ref s, st.AOE);
        var dist = Player.DistanceToHitbox(primaryTarget);

        // Reawaken: Delay / Force; never started with Hunter's Instinct or Swiftscaled about to run out under it (the xan module's rule:
        // the buffs are refreshed by the coils first)
        var instinct = s.StatusLeft[Job.StatusIndex(VprDefinition.HuntersInstinct)];
        var swift = s.StatusLeft[Job.StatusIndex(VprDefinition.Swiftscaled)];
        var sequence = ReawakenSequence(s);
        foreach (var r in Reawakens)
        {
            if (st.Buffs.Value == OffensiveStrategy.Delay || st.Buffs.Value != OffensiveStrategy.Force && (instinct < sequence || swift < sequence))
                Forbid(ref s, r);
            else if (st.Buffs.Value == OffensiveStrategy.Force)
                Force(r);
        }

        if (st.SerpentsIre.Value == XanVPR.SerpentsIreStrategy.Off)
            Forbid(ref s, "SerpentsIre");
        else if (st.SerpentsIre.Value == XanVPR.SerpentsIreStrategy.Force)
            Force("SerpentsIre");

        // Uncoiled Fury out of melee range (Auto): the coil is spent rather than waiting in range of nothing
        if (st.UncoiledFuryRange.Value == XanVPR.UncoiledFuryRangeStrategy.Auto && dist > 3 && dist <= 20 && Player.InCombat)
            Force("UncoiledFury");

        // the second combo step (as the xan module): the side whose venom is held, else the buff with less time left
        var venom = s.Gauges[Job.GaugeIndex(VprDefinition.Venom)];
        if (Job.HasSkill("HuntersSting") && Job.HasSkill("SwiftskinsSting"))
        {
            var hunter = venom is 1 or 2 || venom == 0 && instinct < swift;
            Forbid(ref s, hunter ? "SwiftskinsSting" : "HuntersSting");
        }

        // the potion before the even-minute double Reawaken: Serpent's Ire just used (Ready to Reawaken up) or the first Reawaken of it running
        var ire = Job.CooldownIndex(VprDefinition.IreCD);
        var evenBurst = s.HasStatus(Job.StatusIndex(VprDefinition.ReawakenReady))
            || s.HasStatus(Job.StatusIndex(VprDefinition.Reawakened)) && s.Charges[ire] == 0 && s.CdReadyIn[ire] > Job.Cooldowns[ire].Recast - 30;
        UsePotion(ActionDefinitions.IDPotionDex, st.Buffs.Value != OffensiveStrategy.Delay && st.Potion.Value switch
        {
            XanVPR.PotionStrategy.OpenerAndEven => evenBurst,
            XanVPR.PotionStrategy.EvenOnly => evenBurst && CombatTime > 60,
            _ => false
        });
    }

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        // countdown opener: Steel Fangs (Vicewinder for the 7.5 zero-second opener) landing on the pull, Slither into melee just before it
        if (!Player.InCombat && World.Client.CountdownRemaining is { } countdown && countdown > 0)
        {
            _strategy = ValueConverter.FromValues<XanVPR.Strategy>(strategy);
            var target = SelectTarget(_strategy.Targeting.Value, primaryTarget, 3);
            if (target != null)
            {
                if (Player.DistanceToHitbox(target) > 3)
                {
                    if (_strategy.Slither.Value != XanVPR.SlitherStrategy.Off && countdown < 0.45f && ActionUnlocked(AID.Slither))
                        Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Slither), target, ActionQueue.Priority.High);
                }
                else if (countdown < 1.16f)
                {
                    var zero = _strategy.OpenerBurst.Value == XanVPR.OpenerBurstStrategy.Patch75ZeroSecond && _strategy.Buffs.Value != OffensiveStrategy.Delay
                        && _strategy.SerpentsIre.Value != XanVPR.SerpentsIreStrategy.Off && ActionUnlocked(AID.Reawaken) && ActionUnlocked(AID.SerpentsIre) && ActionUnlocked(AID.Vicewinder);
                    Hints.ActionsToExecute.Push(ActionID.MakeSpell(zero ? AID.Vicewinder : AID.SteelFangs), target, ActionQueue.Priority.High + 2);
                }
            }
            return;
        }
        base.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        var st = _strategy;
        UpdatePositional(Target, st.TrueNorth.Value == XanVPR.TrueNorthStrategy.Auto);
        WrithingSnap(st);
        Slither(st);
    }

    // Writhing Snap (range 20): the last ranged filler, out of melee range with no coil (or the Uncoiled Fury fallback off), not during
    // a Reawaken. Below the engine's GCD, so it only goes off when that one cannot (out of range)
    private void WrithingSnap(in XanVPR.Strategy st)
    {
        if (st.Snap.Value != XanVPR.SnapStrategy.Ranged || Target == null || !Player.InCombat || !ActionUnlocked(AID.WrithingSnap) || SelfStatusLeft(SID.Reawakened) > 0)
            return;
        var gauge = World.Client.GetGauge<ViperGauge>();
        if (gauge.RattlingCoilStacks > 0 && st.UncoiledFuryRange.Value == XanVPR.UncoiledFuryRangeStrategy.Auto && ActionUnlocked(AID.UncoiledFury))
            return;
        var dist = Player.DistanceToHitbox(Target);
        if (dist > 3 && dist <= 20)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.WrithingSnap), Target, ActionQueue.Priority.High + 1);
    }

    // Slither for the burst recovery: out of melee range with a Reawaken ready or running
    private void Slither(in XanVPR.Strategy st)
    {
        if (st.Slither.Value != XanVPR.SlitherStrategy.OpenerAndBurstRecovery || Target == null || !Player.InCombat || !ActionUnlocked(AID.Slither))
            return;
        var dist = Player.DistanceToHitbox(Target);
        if (dist > 3 && dist <= 20 && (SelfStatusLeft(SID.ReawakenReady) > 0 || SelfStatusLeft(SID.Reawakened) > 0))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Slither), Target, ActionQueue.Priority.Medium);
    }

    // Flanksting / Flanksbane / Hunter's Coil want the flank, Hindsting / Hindsbane / Swiftskin's Coil the rear: publish the positional and
    // use True North when it would be missed (behind the follow-up abilities, which the next weaponskill would drop)
    private void UpdatePositional(Actor? target, bool useTrueNorth)
    {
        var next = LastDecision.NextGcd >= 0 ? Job.Skills[LastDecision.NextGcd].Name : "";
        var pos = next is "FlankstingStrike" or "FlanksbaneFang" or "HuntersCoil" ? Positional.Flank : next is "HindstingStrike" or "HindsbaneFang" or "SwiftskinsCoil" ? Positional.Rear : Positional.Any;
        if (target == null || target.Omnidirectional || pos == Positional.Any || target.TargetID == Player.InstanceID && target.CastInfo == null && !target.IsStrikingDummy)
        {
            Hints.RecommendedPositional = (target, Positional.Any, false, true);
            return;
        }
        var trueNorth = StatusDetails(Player, ClassShared.SID.TrueNorth, Player.InstanceID).Left > GCD;
        var dot = target.Rotation.ToDirection().Dot((Player.Position - target.Position).Normalized());
        var correct = trueNorth || (pos == Positional.Flank ? MathF.Abs(dot) < 0.7071067f : dot < -0.7071068f);
        var imminent = !trueNorth && GCD < 2.2f;
        Hints.RecommendedPositional = (target, pos, imminent, correct);
        if (useTrueNorth && imminent && !correct && Player.InCombat && ActionUnlocked(ClassShared.AID.TrueNorth))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.TrueNorth), Player, ActionQueue.Priority.Low, delay: MathF.Max(0, GCD - 0.8f));
    }

    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    protected override ActionID ActionFor(SkillDef skill)
    {
        switch (skill.Name)
        {
            case "TwinbloodBiteSwift":
                return ActionID.MakeSpell(AID.TwinbloodBite);
            case "TwinfangBiteSwift":
                return ActionID.MakeSpell(AID.TwinfangBite);
            case "TwinbloodThreshSwift":
                return ActionID.MakeSpell(AID.TwinbloodThresh);
            case "TwinfangThreshSwift":
                return ActionID.MakeSpell(AID.TwinfangThresh);
            case "Legacy":
                return ActionID.MakeSpell(World.Client.GetGauge<ViperGauge>().SerpentCombo switch
                {
                    SerpentCombo.SecondLegacy => AID.SecondLegacy,
                    SerpentCombo.ThirdLegacy => AID.ThirdLegacy,
                    SerpentCombo.FourthLegacy => AID.FourthLegacy,
                    _ => AID.FirstLegacy
                });
            default:
                return base.ActionFor(skill);
        }
    }

    // Level sync (below 100): The Balance VPR Leveling Guide (filler rules, 70-73 / 74-81 / 82-89 / 90-91 / 92-100) and the ersharifst
    // 7.5 guide (docs/rebuild/engine-design.md section 26)
    protected override bool HasSyncedRules => true;

    protected override int SyncedOgcd(in EngineState s, in EngineTimeline tl)
    {
        // the follow-ups at once (the next weaponskill drops them): Serpent's Tail, then the twins, the buffed one first
        if (FirstLegal(s, tl, "Legacy", "DeathRattle", "LastLash") is var tail and >= 0)
            return tail;
        // (the definition's pairs are in the buffed order: the first of a pair before the second)
        if (FirstLegal(s, tl, "UncoiledTwinfang", "UncoiledTwinblood", "TwinfangThresh", "TwinbloodThresh", "TwinbloodThreshSwift", "TwinfangThreshSwift", "TwinfangBite", "TwinbloodBite", "TwinbloodBiteSwift", "TwinfangBiteSwift") is var twin and >= 0)
            return twin;
        // Serpent's Ire on cooldown while the coils are not full (a Reawaken follows from 90; the guide: 50 Offering banked, two Reawakens back to back)
        var coilMax = Job.Gauges[Job.GaugeIndex(VprDefinition.Coil)].Max;
        if (Gauge(s, VprDefinition.Coil) < coilMax && (!Job.HasSkill("Reawaken") || Gauge(s, VprDefinition.Offering) >= 50 || StatusLeft(s, VprDefinition.Reawakened) > 0 || ReadyIn(s, VprDefinition.IreCD) > 0)
            && Legal(s, tl, "SerpentsIre") is var ire and >= 0)
            return ire;
        return -1;
    }

    protected override int SyncedGcd(in EngineState s, in EngineTimeline tl)
    {
        var aoe = ShapeTargets(s, "SteelMaw") >= 3 && Job.HasSkill("SteelMaw") && !Disabled(s, "SteelMaw");
        var step = Gauge(s, VprDefinition.Step);
        var instinct = StatusLeft(s, VprDefinition.HuntersInstinct);
        var swift = StatusLeft(s, VprDefinition.Swiftscaled);
        // the Reawaken sequence in order
        if (StatusLeft(s, VprDefinition.Reawakened) > 0 && FirstLegal(s, tl, "FirstGeneration", "SecondGeneration", "ThirdGeneration", "FourthGeneration", "Ouroboros") is var gen and >= 0)
            return gen;
        // the dread chain is always finished: the coil of the buff with less time left first
        if (StatusLeft(s, VprDefinition.HunterCoilOk) > 0 || StatusLeft(s, VprDefinition.SwiftCoilOk) > 0)
            return FirstLegal(s, tl, instinct <= swift ? "HuntersCoil" : "SwiftskinsCoil", "HuntersCoil", "SwiftskinsCoil");
        if (StatusLeft(s, VprDefinition.HunterDenOk) > 0 || StatusLeft(s, VprDefinition.SwiftDenOk) > 0)
            return FirstLegal(s, tl, instinct <= swift ? "HuntersDen" : "SwiftskinsDen", "HuntersDen", "SwiftskinsDen");
        var gcdLen = Job.BaseGcd * (swift > 0 ? 0.85f : 1f);
        var sequence = ReawakenSequence(s);
        var buffsHold = instinct >= sequence && swift >= sequence;
        var ireIn = Job.HasSkill("SerpentsIre") && !Disabled(s, "SerpentsIre") ? ReadyIn(s, VprDefinition.IreCD) : float.MaxValue;
        var offering = Gauge(s, VprDefinition.Offering);
        // Reawaken: from Serpent's Ire at once (then the second one from the 50 Offering banked for it), or one between the even-minute
        // windows while the Offering would otherwise overcap (the guide: one Reawaken between windows, the two of the window back to back)
        if (buffsHold && StatusLeft(s, VprDefinition.ReawakenReady) > 0 && Legal(s, tl, "ReawakenReady") is var ready and >= 0)
            return ready;
        // 50 Offering is banked for the window: a Reawaken between windows only when the finishers until Serpent's Ire (10 per three GCDs) rebuild it
        var afterIre = Job.HasSkill("SerpentsIre") && ReadyIn(s, VprDefinition.IreCD) > Job.Cooldowns[Job.CooldownIndex(VprDefinition.IreCD)].Recast - 30;
        var gainUntilIre = ireIn == float.MaxValue ? 1000 : ireIn * 10 / (3 * gcdLen);
        if (buffsHold && (offering >= 100 || afterIre || offering >= 50 && offering - 50 + gainUntilIre >= 50) && Legal(s, tl, "Reawaken") is var reawaken and >= 0)
            return reawaken;
        // Uncoiled Fury: with the coils full and another one coming (Vicewinder / Vicepit charge or Serpent's Ire within two GCDs), under raid
        // buffs with nothing else to do with them, or spent before a disengage; never when it would let a buff drop (3 GCDs + 0.5 s)
        var coil = Gauge(s, VprDefinition.Coil);
        var coilMax = Job.Gauges[Job.GaugeIndex(VprDefinition.Coil)].Max;
        var viceSoon = Job.HasSkill("Vicewinder") && ReadyIn(s, VprDefinition.ViceCD) <= gcdLen;
        var buffsSafe = instinct > gcdLen * 3 + 0.5f && swift > gcdLen * 3 + 0.5f;
        var raidBuff = tl.BuffMultiplier(s.Time) > 1 && tl.BuffMultiplier(s.Time + gcdLen) > 1;
        if (coil > 0 && buffsSafe && (coil >= coilMax && (viceSoon || ireIn <= gcdLen * 2) || raidBuff && !(buffsHold && offering >= 50)) && Legal(s, tl, "UncoiledFury") is var fury and >= 0)
            return fury;
        // Vicewinder / Vicepit on cooldown (a charge up), held for the even-minute window when Serpent's Ire is within 10 s with the Offering
        // banked, and with the combo about to drop; a charge about to sit full is never held
        var holdVice = Job.HasSkill("Reawaken") && ireIn <= 10 && offering >= 50 && Charges(s, VprDefinition.ViceCD) < 2;
        var comboDrop = s.ComboSkill != EngineLimits.NoCombo && s.ComboLeft <= gcdLen * (aoe ? 3 : 3) + 0.5f;
        if (!holdVice && !comboDrop && FirstLegal(s, tl, aoe ? "Vicepit" : "Vicewinder", "Vicewinder") is var vice and >= 0)
            return vice;
        if (aoe)
        {
            // Maw -> Bite (the buff with less time left) -> Jagged / Bloodied Maw (the one with its grim venom)
            var grim = Gauge(s, VprDefinition.GrimVenom);
            if (FirstLegal(s, tl, grim == 2 ? "BloodiedMaw" : "JaggedMaw", "JaggedMaw", "BloodiedMaw") is var maw3 and >= 0)
                return maw3;
            if (step == 4 && FirstLegal(s, tl, instinct <= swift ? "HuntersBite" : "SwiftskinsBite", "HuntersBite", "SwiftskinsBite") is var bite and >= 0)
                return bite;
            // the honed maw (Reaving after Steel and back)
            if (FirstLegal(s, tl, StatusLeft(s, VprDefinition.HonedSteel) > 0 ? "SteelMaw" : "ReavingMaw", "SteelMaw") is var maw and >= 0)
                return maw;
        }
        // Fangs -> Sting (the venom's side, else the buff with less time left) -> the finisher with the venom
        var venom = Gauge(s, VprDefinition.Venom);
        if (step == 2 && FirstLegal(s, tl, venom == 1 ? "FlankstingStrike" : "FlanksbaneFang", "FlanksbaneFang", "FlankstingStrike") is var flank and >= 0)
            return flank;
        if (step == 3 && FirstLegal(s, tl, venom == 3 ? "HindstingStrike" : "HindsbaneFang", "HindsbaneFang", "HindstingStrike") is var hind and >= 0)
            return hind;
        if (step == 1)
        {
            var hunter = venom is 1 or 2 || venom == 0 && instinct <= swift;
            if (FirstLegal(s, tl, hunter ? "HuntersSting" : "SwiftskinsSting", "HuntersSting", "SwiftskinsSting") is var sting and >= 0)
                return sting;
        }
        // the honed fangs (Reaving after Steel and back)
        return FirstLegal(s, tl, StatusLeft(s, VprDefinition.HonedSteel) > 0 ? "SteelFangs" : "ReavingFangs", "SteelFangs");
    }

    private void SetOk(ref EngineState s, string status, bool ok)
    {
        if (!ok)
            return;
        var i = Job.StatusIndex(status);
        s.StatusLeft[i] = 60;
        s.StatusStacks[i] = 1;
    }

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        var gauge = World.Client.GetGauge<ViperGauge>();
        s.Gauges[Job.GaugeIndex(VprDefinition.Offering)] = gauge.SerpentOffering;
        s.Gauges[Job.GaugeIndex(VprDefinition.Coil)] = (short)Math.Min(gauge.RattlingCoilStacks, Job.Gauges[Job.GaugeIndex(VprDefinition.Coil)].Max);
        s.Gauges[Job.GaugeIndex(VprDefinition.Anguine)] = gauge.AnguineTribute;
        // the finisher venoms (never together) and the grim venoms
        s.Gauges[Job.GaugeIndex(VprDefinition.Venom)] = (short)(SelfStatusLeft(SID.FlankstungVenom) > 0 ? 1 : SelfStatusLeft(SID.FlanksbaneVenom) > 0 ? 2 : SelfStatusLeft(SID.HindstungVenom) > 0 ? 3 : SelfStatusLeft(SID.HindsbaneVenom) > 0 ? 4 : 0);
        s.Gauges[Job.GaugeIndex(VprDefinition.GrimVenom)] = (short)(SelfStatusLeft(SID.GrimhuntersVenom) > 0 ? 1 : SelfStatusLeft(SID.GrimskinsVenom) > 0 ? 2 : 0);
        // the Serpent's Tail / twin windows from the gauge's combo state (7 / 8 / 9: twins after a coil / den / Uncoiled Fury, the low two bits
        // the twin uses left); the side the window opened on from the venoms it granted
        var serpent = (int)gauge.SerpentCombo;
        var twinsLeft = gauge.SerpentComboState & 3;
        s.Gauges[Job.GaugeIndex(VprDefinition.Tail)] = (short)(serpent switch
        {
            (int)SerpentCombo.DeathRattle => 1,
            (int)SerpentCombo.LastLash => 2,
            (int)SerpentCombo.FirstLegacy or (int)SerpentCombo.SecondLegacy or (int)SerpentCombo.ThirdLegacy or (int)SerpentCombo.FourthLegacy => 3,
            _ => 0
        });
        var huntersVenom = SelfStatusLeft(SID.HuntersVenom) > 0;
        var swiftskinsVenom = SelfStatusLeft(SID.SwiftskinsVenom) > 0;
        var fellhunters = SelfStatusLeft(SID.FellhuntersVenom) > 0;
        var fellskins = SelfStatusLeft(SID.FellskinsVenom) > 0;
        var poisedFang = SelfStatusLeft(SID.PoisedForTwinfang) > 0;
        var poisedBlood = SelfStatusLeft(SID.PoisedForTwinblood) > 0;
        var window = serpent switch
        {
            7 => huntersVenom || !swiftskinsVenom ? 1 : 2,
            8 => fellhunters || !fellskins ? 3 : 4,
            9 => 5,
            _ => 0
        };
        s.Gauges[Job.GaugeIndex(VprDefinition.TwinWindow)] = (short)window;
        if (window > 0 && twinsLeft > 0)
        {
            // with both uses left both are ready; with one left the one whose venom (or poise) is held
            var fangReady = twinsLeft >= 2 || (window <= 2 ? huntersVenom : window <= 4 ? fellhunters : poisedFang);
            var bloodReady = twinsLeft >= 2 || (window <= 2 ? swiftskinsVenom : window <= 4 ? fellskins : poisedBlood);
            if (fangReady)
            {
                var i = Job.StatusIndex(VprDefinition.TwinfangReady);
                s.StatusLeft[i] = 30;
                s.StatusStacks[i] = 1;
            }
            if (bloodReady)
            {
                var i = Job.StatusIndex(VprDefinition.TwinbloodReady);
                s.StatusLeft[i] = 30;
                s.StatusStacks[i] = 1;
            }
        }
        // the combo step from the client's combo state
        var combo = (AID)World.Client.ComboState.Action;
        if (World.Client.ComboState.Remaining > 0)
            s.Gauges[Job.GaugeIndex(VprDefinition.Step)] = (short)(combo switch
            {
                AID.SteelFangs or AID.ReavingFangs => 1,
                AID.HuntersSting => 2,
                AID.SwiftskinsSting => 3,
                AID.SteelMaw or AID.ReavingMaw => 4,
                AID.HuntersBite => 5,
                AID.SwiftskinsBite => 6,
                _ => 0
            });
        // the dread chains: which coil / den is still to come
        var dread = gauge.DreadCombo;
        SetOk(ref s, VprDefinition.HunterCoilOk, dread is DreadCombo.Dreadwinder or DreadCombo.SwiftskinsCoil);
        SetOk(ref s, VprDefinition.SwiftCoilOk, dread is DreadCombo.Dreadwinder or DreadCombo.HuntersCoil);
        SetOk(ref s, VprDefinition.HunterDenOk, dread is DreadCombo.PitOfDread or DreadCombo.SwiftskinsDen);
        SetOk(ref s, VprDefinition.SwiftDenOk, dread is DreadCombo.PitOfDread or DreadCombo.HuntersDen);

        ReadStatus(ref s, Job.StatusIndex(VprDefinition.HuntersInstinct), Player, (uint)SID.HuntersInstinct);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.Swiftscaled), Player, (uint)SID.Swiftscaled);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.HonedSteel), Player, (uint)SID.HonedSteel);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.HonedReavers), Player, (uint)SID.HonedReavers);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.HuntersVenom), Player, (uint)SID.HuntersVenom);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.SwiftskinsVenom), Player, (uint)SID.SwiftskinsVenom);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.FellhuntersVenom), Player, (uint)SID.FellhuntersVenom);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.FellskinsVenom), Player, (uint)SID.FellskinsVenom);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.PoisedForTwinfang), Player, (uint)SID.PoisedForTwinfang);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.PoisedForTwinblood), Player, (uint)SID.PoisedForTwinblood);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.ReawakenReady), Player, (uint)SID.ReawakenReady);
        ReadStatus(ref s, Job.StatusIndex(VprDefinition.Reawakened), Player, (uint)SID.Reawakened);

        ReadCooldown(ref s, Job.CooldownIndex(VprDefinition.ViceCD), ActionID.MakeSpell(AID.Vicewinder));
        ReadCooldown(ref s, Job.CooldownIndex(VprDefinition.IreCD), ActionID.MakeSpell(AID.SerpentsIre));
        ReadCombo(ref s);
    }
}
