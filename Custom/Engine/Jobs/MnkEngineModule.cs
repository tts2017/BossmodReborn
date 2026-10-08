using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.MNK;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;
using XanMNK = BossMod.Autorotation.xan.Custom.MNK;

namespace BossMod.Autorotation;

// "MNK [Engine]": Monk driven by the rotation engine (Custom/Engine). Level 70+ (the Perfect Balance / Phantom Rush rules need
// Riddle of Fire and Brotherhood): the definition is built for the player's level, and BMR recreates the module when the level
// changes (level sync). The strategy tracks are those of xan MNK [Custom]; Six-sided Star, Form Shift, Meditation, Thunderclap,
// the engage and Riddle of Earth are pressed by this module; settings for skills not learned at the player's level do nothing.
public sealed class MnkEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
        => WithComposition(new RotationModuleDefinition("MNK [Engine]", "Monk on the two-tier rotation engine (burst-window planning + short search). Experimental, level 70+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.MNK), 100, 70)
            .WithStrategies<XanMNK.Strategy>());

    // the first tier (EngineRotationModule.CompositionStrategy): the xan MNK module of the same strategy tracks
    protected override RotationModule CreateBaseline() => new XanMNK(Manager, Player);

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        // Greased Lightning: haste on weaponskills, 20% from level 76 (15% from 40, 10% from 20, 5% below)
        var greased = player.Level >= 76 ? 80 : player.Level >= 40 ? 85 : player.Level >= 20 ? 90 : 95;
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, Math.Min(stats.Haste, greased), player.Level) : 2.0f;
        return new RotationEngine(MnkDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? MnkDefinition.DefaultWeights(player.Level)) { FrameBudgetMs = FrameBudgetOverride ?? 0.03f, ReplanInterval = 8 };
    }

    private XanMNK.Strategy _strategy;

    protected override Actor? SelectTarget(StrategyValues strategy, Actor? primaryTarget)
    {
        _strategy = ValueConverter.FromValues<XanMNK.Strategy>(strategy);
        SetMechanicHints(_strategy.MechanicHints);
        UseFightEnd = _strategy.FightEnd.Value == XanMNK.FightEndStrategy.Automatic;
        return SelectTarget(_strategy.Targeting.Value, primaryTarget, 3);
    }

    private static readonly string[] Blitzes = ["ElixirBurstOpo", "ElixirBurstRaptor", "ElixirBurstCoeurl", "RisingPhoenix", "PhantomRush"];
    private static readonly string[] PerfectBalances = ["PerfectBalance", "PerfectBalanceOdd", "PerfectBalancePre"];

    protected override void ApplyStrategy(StrategyValues strategy, ref EngineState s, Actor? primaryTarget)
    {
        var st = _strategy;
        ApplyAoe(ref s, st.AOE);
        var inMelee = Player.DistanceToHitbox(primaryTarget) <= 3;

        // basic rotation + Chakra overcap: no Riddle of Fire / Brotherhood / Perfect Balance / Riddle of Wind / potion (the Chakra
        // spenders only fire at a full gauge anyway). Encounter hints that keep the burst (trash, before the boss returns, hold) do the same
        var basic = st.RotationMode.Value == XanMNK.RotationModeStrategy.BasicAndChakraOvercap;
        var holdBurst = basic || st.EncounterHint.Value is XanMNK.MNKEncounterHintStrategy.Trash or XanMNK.MNKEncounterHintStrategy.BossReturn or XanMNK.MNKEncounterHintStrategy.HoldBurst;
        // the potion in the Riddle of Fire + Brotherhood window (the opener and the even minutes), or at once
        var evenBurst = s.HasStatus(Job.StatusIndex(MnkDefinition.RiddleOfFire)) && s.HasStatus(Job.StatusIndex(MnkDefinition.Brotherhood));
        UsePotion(ActionDefinitions.IDPotionStr, !holdBurst && st.Pot.Value switch
        {
            XanMNK.PotionStrategy.OpenerAndEvenBursts => evenBurst,
            XanMNK.PotionStrategy.NonOpenerEvenBursts => evenBurst && CombatTime > 60,
            XanMNK.PotionStrategy.Now => true,
            _ => false
        });
        if (holdBurst)
        {
            Forbid(ref s, "RiddleOfFire");
            Forbid(ref s, "Brotherhood");
            Forbid(ref s, "BrotherhoodFirst");
            Forbid(ref s, "RiddleOfWind");
            foreach (var pb in PerfectBalances)
                Forbid(ref s, pb);
        }

        if (st.Brotherhood.Value == OffensiveStrategy.Delay)
        {
            Forbid(ref s, "Brotherhood");
            Forbid(ref s, "BrotherhoodFirst");
        }
        else if (st.Brotherhood.Value == OffensiveStrategy.Force)
        {
            Force("BrotherhoodFirst");
            Force("Brotherhood");
        }

        if (st.RoF.Value == XanMNK.RoFStrategy.Delay)
            Forbid(ref s, "RiddleOfFire");
        else if (st.RoF.Value is XanMNK.RoFStrategy.Force or XanMNK.RoFStrategy.ForceMidWeave)
            Force("RiddleOfFire");

        switch (st.FiresReply.Value)
        {
            case XanMNK.FRStrategy.Delay:
                Forbid(ref s, "FiresReply");
                break;
            case XanMNK.FRStrategy.Force:
                Force("FiresReply");
                break;
            case XanMNK.FRStrategy.Ranged:
                // kept for when the target cannot be reached in melee
                if (inMelee)
                    Forbid(ref s, "FiresReply");
                else
                    Force("FiresReply");
                break;
        }

        switch (st.RoW.Value)
        {
            case XanMNK.RoWStrategy.Delay:
                Forbid(ref s, "RiddleOfWind");
                break;
            case XanMNK.RoWStrategy.Force or XanMNK.RoWStrategy.OpenerCooldown:
                Force("RiddleOfWind");
                break;
            case XanMNK.RoWStrategy.RoFAligned:
                if (!s.HasStatus(Job.StatusIndex(MnkDefinition.RiddleOfFire)))
                    Forbid(ref s, "RiddleOfWind");
                break;
        }
        // below 96 Riddle of Wind only speeds up auto-attacks (no Wind's Reply, nothing the engine values): on cooldown, as the xan module
        if (!Job.HasSkill("WindsReply") && st.RoW.Value != XanMNK.RoWStrategy.Delay)
            Force("RiddleOfWind");

        if (st.WindsReply.Value == XanMNK.WRStrategy.Delay)
            Forbid(ref s, "WindsReply");
        else if (st.WindsReply.Value == XanMNK.WRStrategy.Force)
            Force("WindsReply");

        if (st.PB.Value == XanMNK.PBStrategy.Delay)
            foreach (var pb in PerfectBalances)
                Forbid(ref s, pb);

        switch (st.Nadi.Value)
        {
            case XanMNK.NadiStrategy.Lunar:
                Forbid(ref s, "RisingPhoenix");
                break;
            case XanMNK.NadiStrategy.Solar:
                Forbid(ref s, "ElixirBurstOpo");
                Forbid(ref s, "ElixirBurstRaptor");
                Forbid(ref s, "ElixirBurstCoeurl");
                break;
        }

        var rof = s.HasStatus(Job.StatusIndex(MnkDefinition.RiddleOfFire));
        var holdBlitz = st.Blitz.Value switch
        {
            XanMNK.BlitzStrategy.Delay => true,
            XanMNK.BlitzStrategy.RoF => !rof,
            XanMNK.BlitzStrategy.Multi => ShapeTargets(s, "PhantomRush") < 2,
            XanMNK.BlitzStrategy.MultiRoF => !rof || ShapeTargets(s, "PhantomRush") < 2,
            _ => false
        };
        foreach (var blitz in Blitzes)
        {
            if (holdBlitz)
                Forbid(ref s, blitz);
            else if (st.Blitz.Value == XanMNK.BlitzStrategy.Force)
                Force(blitz);
        }
    }

    protected override void ExecuteJob(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        base.ExecuteJob(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        var st = _strategy;
        UpdatePositional(Target, st.TrueNorth.Value);
        if (st.RoE.Value == XanMNK.RoEStrategy.Automatic)
            RiddleOfEarth();
        Utility(st);
    }

    // the GCDs and abilities outside the engine's definition
    private void Utility(in XanMNK.Strategy st)
    {
        var target = Target;
        var dist = Player.DistanceToHitbox(target);
        var inMelee = dist <= 3;
        var pb = SelfStatusLeft(SID.PerfectBalance) > 0;
        var gauge = World.Client.GetGauge<MonkGauge>();

        // Perfect Balance forced: any time a charge is up and no Perfect Balance runs (ForceOpo: after an Opo-opo GCD, i.e. in Raptor form;
        // ForceNoShift: not with Form Shift's Formless Fist up)
        if (Player.InCombat && CD(AID.PerfectBalance) <= GCD && !pb && st.PB.Value switch
        {
            XanMNK.PBStrategy.Force => true,
            XanMNK.PBStrategy.ForceOpo => SelfStatusLeft(SID.RaptorForm) > 0,
            XanMNK.PBStrategy.ForceNoShift => SelfStatusLeft(SID.FormlessFist) <= 0,
            _ => false
        })
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.PerfectBalance), Player, ActionQueue.Priority.Medium + 1);

        // Six-sided Star and Form Shift when forced (Automatic leaves the GCDs to the engine: a Six-sided Star before a downtime or a Form
        // Shift inside one lowered the 9-fight harness by 85 / 2,600 potency)
        if (st.SSS.Value == OffensiveStrategy.Force && target != null && inMelee && !pb && ActionUnlocked(AID.SixSidedStar))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.SixSidedStar), target, ActionQueue.Priority.High + 3);
        if (st.FormShift.Value == OffensiveStrategy.Force && !pb)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.FormShift), Player, ActionQueue.Priority.High + 3);

        // Meditation up to 5 Chakra: Safe out of combat, Greedy also in combat out of melee range, Force always
        if (gauge.Chakra < 5 && !(st.Meditate.Value != XanMNK.MeditationStrategy.Force && gauge.BlitzTimeRemaining > 0) && st.Meditate.Value switch
        {
            XanMNK.MeditationStrategy.Force => true,
            XanMNK.MeditationStrategy.Safe => !Player.InCombat,
            XanMNK.MeditationStrategy.Greedy => !Player.InCombat || !inMelee,
            _ => false
        })
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.SteeledMeditation), Player, st.Meditate.Value == XanMNK.MeditationStrategy.Force ? ActionQueue.Priority.High + 3 : ActionQueue.Priority.High + 1);

        if (target == null)
            return;
        // Thunderclap to close the gap
        if (st.TC.Value == XanMNK.TCStrategy.GapClose && Player.InCombat && dist is > 3 and <= 20)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Thunderclap), target, ActionQueue.Priority.Medium);

        // engage on the countdown: Thunderclap into melee (with GapClose) or Sprint
        if (World.Client.CountdownRemaining is { } countdown && !Player.InCombat)
        {
            if (st.Engage.Value == XanMNK.EngageStrategy.TC && st.TC.Value == XanMNK.TCStrategy.GapClose && countdown < 0.7f && dist > 3)
                Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Thunderclap), target, ActionQueue.Priority.High);
            else if (st.Engage.Value == XanMNK.EngageStrategy.Sprint && countdown < 10)
                Hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.Sprint), Player, ActionQueue.Priority.High);
        }
    }

    // mitigation outside the engine: Riddle of Earth when damage to us is predicted within 10 s (as the regular module)
    private void RiddleOfEarth()
    {
        if (!Player.InCombat || !ActionUnlocked(AID.RiddleOfEarth) || CD(AID.RiddleOfEarth) > 0.6f || SelfStatusLeft(SID.RiddleOfEarth) > 0 || SelfStatusLeft(SID.EarthsRumination) > 0)
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

    // Level sync (below 100): The Balance MNK Leveling / Basic Guides and Icy Veins, per band (docs/rebuild/engine-design.md section 20)
    protected override bool HasSyncedRules => true;

    protected override int SyncedOgcd(in EngineState s, in EngineTimeline tl)
    {
        // Brotherhood double-woven before the first Riddle of Fire, then both on cooldown (Brotherhood as close to Riddle of Fire as possible)
        if (FirstLegal(s, tl, "BrotherhoodFirst", "RiddleOfFire", "Brotherhood") is var buff and >= 0)
            return buff;
        // Perfect Balance only right after an Opo-opo GCD (Raptor form): two in the Brotherhood window, one in the other Riddle of Fire
        // windows while it builds a Nadi (with both Nadi the blitz would be Phantom Rush, which needs Brotherhood)
        if (StatusLeft(s, MnkDefinition.RaptorForm) > 0)
        {
            if (FirstLegal(s, tl, "PerfectBalancePre", "PerfectBalance") is var pb and >= 0)
                return pb;
            if (Gauge(s, MnkDefinition.NadiCount) <= 1 && Legal(s, tl, "PerfectBalanceOdd") is var odd and >= 0)
                return odd;
        }
        // Riddle of Wind (96+; below it the strategy presses it), Forbidden Chakra at 5 Chakra (Enlightenment / Howling Fist on 3+ targets)
        if (Legal(s, tl, "RiddleOfWind") is var wind and >= 0 && Job.HasSkill("WindsReply"))
            return wind;
        return FirstLegal(s, tl, ShapeTargets(s, "Enlightenment") >= 3 ? "Enlightenment" : "ForbiddenChakra", "ForbiddenChakra", "Enlightenment");
    }

    protected override int SyncedGcd(in EngineState s, in EngineTimeline tl)
    {
        var aoe = ShapeTargets(s, "Rockbreaker") >= 3;
        // a full Beast Chakra gauge: the blitz at once (Phantom Rush with both Nadi)
        if (FirstLegal(s, tl, "PhantomRush", "ElixirBurstOpo", "ElixirBurstRaptor", "ElixirBurstCoeurl", "RisingPhoenix") is var blitz and >= 0)
            return blitz;
        // Wind's Reply (96+) inside Riddle of Wind
        if (Legal(s, tl, "WindsReply") is var reply and >= 0)
            return reply;
        var opo = aoe ? "ShadowOfTheDestroyer" : Gauge(s, MnkDefinition.OpoFury) > 0 ? "LeapingOpo" : "DragonKick";
        var raptor = aoe ? "FourPointFury" : Gauge(s, MnkDefinition.RaptorFury) > 0 ? "RisingRaptor" : "TwinSnakes";
        var coeurl = aoe ? "Rockbreaker" : Gauge(s, MnkDefinition.CoeurlFury) > 0 ? "PouncingCoeurl" : "Demolish";
        if (StatusLeft(s, MnkDefinition.PerfectBalance) > 0)
        {
            // Perfect Balance: with both Nadi anything (Opo-opo GCDs) for Phantom Rush; else Lunar (three Opo-opo) and Solar (one of each).
            // Lunar first below 90; from 90 Solar first (the Solar Lunar opener)
            var lunar = Gauge(s, MnkDefinition.Lunar) > 0;
            var solar = Gauge(s, MnkDefinition.Solar) > 0;
            var both = lunar && solar;
            var beastOpo = Gauge(s, MnkDefinition.BeastOpo);
            // a mix already started is finished (a gauge that is neither three of a kind nor one of each has no blitz)
            var solarNext = !both && (Gauge(s, MnkDefinition.BeastRaptor) > 0 || Gauge(s, MnkDefinition.BeastCoeurl) > 0
                || beastOpo < 2 && (Disabled(s, "ElixirBurstOpo") || !Disabled(s, "RisingPhoenix") && (lunar || !solar && Player.Level >= 90)));
            if (solarNext)
            {
                var pick = Gauge(s, MnkDefinition.BeastRaptor) == 0 ? raptor : Gauge(s, MnkDefinition.BeastCoeurl) == 0 ? coeurl : opo;
                return FirstLegal(s, tl, pick, opo);
            }
            return FirstLegal(s, tl, opo, "LeapingOpo", "DragonKick", "Bootshine");
        }
        // Formless Fist (a blitz, Fire's Reply or Form Shift): the Opo-opo GCD for its guaranteed crit (ersharifst: always Leaping Opo / Dragon Kick)
        if (StatusLeft(s, MnkDefinition.Formless) > 0)
            return FirstLegal(s, tl, opo, "LeapingOpo", "DragonKick", "Bootshine");
        // the form loop: Opo-opo -> Raptor -> Coeurl, each spending its fury stack or building it
        if (StatusLeft(s, MnkDefinition.RaptorForm) > 0 && FirstLegal(s, tl, raptor, "TwinSnakes", "TrueStrike") is var r and >= 0)
            return r;
        if (StatusLeft(s, MnkDefinition.CoeurlForm) > 0 && FirstLegal(s, tl, coeurl, "Demolish", "SnapPunch") is var c and >= 0)
            return c;
        return FirstLegal(s, tl, opo, "LeapingOpo", "DragonKick", "Bootshine");
    }

    // Demolish / Snap Punch: rear / flank (as the regular modules); True North when the next one would be missed
    private void UpdatePositional(Actor? target, OffensiveStrategy trueNorthSetting)
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
        if (trueNorthSetting == OffensiveStrategy.Force && Player.InCombat && !trueNorth)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.TrueNorth), Player, ActionQueue.Priority.Medium);
        else if (trueNorthSetting == OffensiveStrategy.Automatic && imminent && !correct)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(ClassShared.AID.TrueNorth), Player, ActionQueue.Priority.Low + 2, delay: MathF.Max(0, GCD - 0.72f));
    }

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
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
