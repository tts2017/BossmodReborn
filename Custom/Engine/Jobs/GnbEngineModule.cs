using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using BossMod.GNB;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using Akechi = BossMod.Autorotation.akechi.Custom;
using AkechiGNB = BossMod.Autorotation.akechi.Custom.AkechiGNB;

namespace BossMod.Autorotation;

// "GNB [Engine]": Gunbreaker damage rotation on the rotation engine (Custom/Engine). Level 30+ (cartridges and Burst Strike): the
// definition is built for the player's level, and BMR recreates the module when the level changes (level sync).
// The strategy tracks are those of Akechi GNB [Custom]; Lightning Shot is pressed by this module. Settings for skills not learned
// at the player's level do nothing. No mitigation or tank stance handling.
public sealed class GnbEngineModule(RotationModuleManager manager, Actor player) : EngineRotationModule(manager, player, CreateEngine(manager, player))
{
    public static EngineWeights? WeightsOverride;
    public static float? FrameBudgetOverride;

    public static RotationModuleDefinition Definition()
    {
        var res = new RotationModuleDefinition("GNB [Engine]", "Gunbreaker damage on the two-tier rotation engine (burst-window planning + short search). Experimental, level 30+.", "Engine", "local", RotationModuleQuality.WIP, BitMask.Build((int)Class.GNB), 100, 30);
        res.Configs.AddRange(AkechiGNB.Definition().Configs);
        return res;
    }

    private static RotationEngine CreateEngine(RotationModuleManager manager, Actor player)
    {
        var stats = manager.WorldState.Client.PlayerStats;
        var gcd = stats.SkillSpeed > 0 ? ActionSpeed.GCDRounded(stats.SkillSpeed, stats.Haste, player.Level) : 2.5f;
        return new RotationEngine(GnbDefinition.Build(gcd, player.Level), WeightsOverride?.Clone() ?? GnbDefinition.DefaultWeights()) { FrameBudgetMs = FrameBudgetOverride ?? 0.05f, ReplanInterval = 8 };
    }

    protected override Actor? SelectTarget(StrategyValues strategy, Actor? primaryTarget)
    {
        SetMechanicHints(strategy.Option(AkechiGNB.Track.MechanicHints).As<MechanicHintStrategy>());
        UseFightEnd = strategy.Option(AkechiGNB.Track.FightEnd).As<AkechiGNB.FightEndStrategy>() == AkechiGNB.FightEndStrategy.SpendResources;
        return SelectTarget(strategy.Option(Akechi.SharedTrack.Targeting).As<Akechi.SoftTargetStrategy>(), primaryTarget, 3);
    }

    private static readonly string[] Buffs = ["NoMercy", "Bloodfest", "BloodfestAfter"];
    private static readonly string[] Cooldowns = ["NoMercy", "Bloodfest", "BloodfestAfter", "GnashingFang", "DoubleDown", "BlastingZone", "BowShock"];
    private static readonly string[] GaugeSpenders = ["BurstStrike", "FatedCircle", "GnashingFang", "DoubleDown"];
    private static readonly string[] BurstGcds = ["SonicBreak", "ReignOfBeasts", "NobleBlood", "LionHeart"];

    protected override void ApplyStrategy(StrategyValues strategy, ref EngineState s, Actor? primaryTarget)
    {
        var aoe = strategy.Option(AkechiGNB.Track.AOE).As<AkechiGNB.AOEStrategy>();
        if (aoe is AkechiGNB.AOEStrategy.ForceSTFinishWithOvercap or AkechiGNB.AOEStrategy.ForceSTFinishWithoutOvercap or AkechiGNB.AOEStrategy.ForceSTBreakWithOvercap or AkechiGNB.AOEStrategy.ForceSTBreakWithoutOvercap)
            ApplyAoe(ref s, AoeSetting.SingleTarget);
        else if (aoe is AkechiGNB.AOEStrategy.ForceAOEFinishWithOvercap or AkechiGNB.AOEStrategy.ForceAOEFinishWithoutOvercap or AkechiGNB.AOEStrategy.ForceAOEBreakWithOvercap or AkechiGNB.AOEStrategy.ForceAOEBreakWithoutOvercap)
            ApplyAoe(ref s, AoeSetting.ForceAoe);
        var ammo = s.Gauges[Job.GaugeIndex(GnbDefinition.Ammo)];
        var maxAmmo = Job.Gauges[Job.GaugeIndex(GnbDefinition.Ammo)].Max / 2; // the cartridge cap (2 below 88; the gauge doubles it for Bloodfest)
        // Fated Circle is learned at 72: below it the Fated Circle options fall back to Burst Strike (as the Akechi module)
        var fatedCircle = Job.HasSkill("FatedCircle");

        var hold = strategy.Option(Akechi.SharedTrack.Hold).As<Akechi.HoldStrategy>();
        if (hold == Akechi.HoldStrategy.HoldEverything)
        {
            ForbidAll(ref s);
            return;
        }

        // the potion inside No Mercy, at the minute marks the option names (Immediate: at once, the only one a cooldown hold lets through)
        var potion = strategy.Option(AkechiGNB.Track.Potion).As<AkechiGNB.PotionStrategyAkechi>();
        var t = CombatTime;
        var (raidBuffLeft, raidBuffIn) = EstimateRaidBuffTimings(primaryTarget);
        var noMercy = s.HasStatus(Job.StatusIndex(GnbDefinition.NoMercy));
        UsePotion(ActionDefinitions.IDPotionStr, potion == AkechiGNB.PotionStrategyAkechi.Immediate || noMercy && hold is not (Akechi.HoldStrategy.HoldCooldowns or Akechi.HoldStrategy.HoldAbilities) && potion switch
        {
            AkechiGNB.PotionStrategyAkechi.AlignWithBuffs => true,
            AkechiGNB.PotionStrategyAkechi.AlignWithRaidBuffs => raidBuffLeft > 0 || raidBuffIn < 5,
            AkechiGNB.PotionStrategyAkechi.EvenMinuteNMOnly => t % 120 < 30,
            AkechiGNB.PotionStrategyAkechi.SixMinuteNM3GCD => t is > 340 and < 400,
            AkechiGNB.PotionStrategyAkechi.EightMinuteNM3GCD => t is > 460 and < 520,
            AkechiGNB.PotionStrategyAkechi.TwoAndEightMinuteNM3GCD => t is > 100 and < 160 or > 460 and < 520,
            _ => false
        });

        if (hold is Akechi.HoldStrategy.HoldBuffs or Akechi.HoldStrategy.HoldAbilities)
            foreach (var sk in Buffs)
                Forbid(ref s, sk);
        if (hold is Akechi.HoldStrategy.HoldCooldowns or Akechi.HoldStrategy.HoldAbilities)
            foreach (var sk in Cooldowns)
                Forbid(ref s, sk);
        if (hold is Akechi.HoldStrategy.HoldGauge or Akechi.HoldStrategy.HoldAbilities)
            foreach (var sk in GaugeSpenders)
                Forbid(ref s, sk);

        // normal rotation + cartridge overcap: the combos, cartridges spent only at the cap, none of the burst
        var carts = strategy.Option(AkechiGNB.Track.Cartridges).As<AkechiGNB.CartridgeStrategy>();
        if (strategy.Option(AkechiGNB.Track.RotationMode).As<AkechiGNB.RotationModeStrategy>() == AkechiGNB.RotationModeStrategy.NormalOvercapOnly || carts == AkechiGNB.CartridgeStrategy.NormalOvercapOnly)
        {
            foreach (var sk in Cooldowns)
                Forbid(ref s, sk);
            foreach (var sk in BurstGcds)
                Forbid(ref s, sk);
            if (ammo < maxAmmo)
            {
                Forbid(ref s, "BurstStrike");
                Forbid(ref s, "FatedCircle");
            }
        }

        switch (carts)
        {
            case AkechiGNB.CartridgeStrategy.OnlyBS:
                Forbid(ref s, "FatedCircle");
                break;
            case AkechiGNB.CartridgeStrategy.OnlyFC:
                if (fatedCircle)
                    Forbid(ref s, "BurstStrike");
                break;
            case AkechiGNB.CartridgeStrategy.Delay:
                Forbid(ref s, "BurstStrike");
                Forbid(ref s, "FatedCircle");
                break;
            case AkechiGNB.CartridgeStrategy.ForceBS or AkechiGNB.CartridgeStrategy.ForceBS1:
            case AkechiGNB.CartridgeStrategy.ForceBS2 when ammo >= 2:
            case AkechiGNB.CartridgeStrategy.ForceBS3 when ammo >= 3:
                Force("BurstStrike");
                break;
            case AkechiGNB.CartridgeStrategy.ForceFC or AkechiGNB.CartridgeStrategy.ForceFC1:
            case AkechiGNB.CartridgeStrategy.ForceFC2 when ammo >= 2:
            case AkechiGNB.CartridgeStrategy.ForceFC3 when ammo >= 3:
                Force(fatedCircle ? "FatedCircle" : "BurstStrike");
                break;
        }

        var zone = strategy.Option(AkechiGNB.Track.Zone).As<Akechi.OGCDStrategy>();
        if (zone == Akechi.OGCDStrategy.Delay)
            Forbid(ref s, "BlastingZone");
        else if (zone != Akechi.OGCDStrategy.Automatic)
            Force("BlastingZone");

        var bow = strategy.Option(AkechiGNB.Track.BowShock).As<Akechi.OGCDStrategy>();
        if (bow == Akechi.OGCDStrategy.Delay)
            Forbid(ref s, "BowShock");
        else if (bow != Akechi.OGCDStrategy.Automatic)
            Force("BowShock");

        switch (strategy.Option(AkechiGNB.Track.NoMercy).As<AkechiGNB.NoMercyStrategy>())
        {
            case AkechiGNB.NoMercyStrategy.Delay:
                Forbid(ref s, "NoMercy");
                break;
            case AkechiGNB.NoMercyStrategy.Force or AkechiGNB.NoMercyStrategy.ForceW or AkechiGNB.NoMercyStrategy.ForceQW:
            case AkechiGNB.NoMercyStrategy.Force1 or AkechiGNB.NoMercyStrategy.Force1W or AkechiGNB.NoMercyStrategy.Force1QW when ammo >= 1:
            case AkechiGNB.NoMercyStrategy.Force2 or AkechiGNB.NoMercyStrategy.Force2W or AkechiGNB.NoMercyStrategy.Force2QW when ammo >= 2:
            case AkechiGNB.NoMercyStrategy.Force3 or AkechiGNB.NoMercyStrategy.Force3W or AkechiGNB.NoMercyStrategy.Force3QW when ammo >= 3:
                Force("NoMercy");
                break;
        }

        var sb = strategy.Option(AkechiGNB.Track.SonicBreak).As<AkechiGNB.SonicBreakStrategy>();
        if (sb == AkechiGNB.SonicBreakStrategy.Delay)
            Forbid(ref s, "SonicBreak");
        else if (sb == AkechiGNB.SonicBreakStrategy.Force)
            Force("SonicBreak");

        switch (strategy.Option(AkechiGNB.Track.GnashingFang).As<AkechiGNB.GnashingStrategy>())
        {
            case AkechiGNB.GnashingStrategy.Delay:
                Forbid(ref s, "GnashingFang");
                break;
            case AkechiGNB.GnashingStrategy.ForceGnash or AkechiGNB.GnashingStrategy.ForceGnash1:
            case AkechiGNB.GnashingStrategy.ForceGnash2 when ammo >= 2:
            case AkechiGNB.GnashingStrategy.ForceGnash3 when ammo >= 3:
                Force("GnashingFang");
                break;
            case AkechiGNB.GnashingStrategy.ForceClaw:
                Force("SavageClaw");
                break;
            case AkechiGNB.GnashingStrategy.ForceTalon:
                Force("WickedTalon");
                break;
        }

        switch (strategy.Option(AkechiGNB.Track.Bloodfest).As<AkechiGNB.BloodfestStrategy>())
        {
            case AkechiGNB.BloodfestStrategy.Delay:
                Forbid(ref s, "Bloodfest");
                Forbid(ref s, "BloodfestAfter");
                break;
            case AkechiGNB.BloodfestStrategy.Force or AkechiGNB.BloodfestStrategy.ForceW:
                _forceBloodfest = true;
                break;
        }

        switch (strategy.Option(AkechiGNB.Track.DoubleDown).As<AkechiGNB.DoubleDownStrategy>())
        {
            case AkechiGNB.DoubleDownStrategy.Delay:
                Forbid(ref s, "DoubleDown");
                break;
            case AkechiGNB.DoubleDownStrategy.Force:
            case AkechiGNB.DoubleDownStrategy.Force3 when ammo >= 3:
                Force("DoubleDown");
                break;
        }

        switch (strategy.Option(AkechiGNB.Track.Reign).As<AkechiGNB.ReignStrategy>())
        {
            case AkechiGNB.ReignStrategy.Delay:
                Forbid(ref s, "ReignOfBeasts");
                Forbid(ref s, "NobleBlood");
                Forbid(ref s, "LionHeart");
                break;
            case AkechiGNB.ReignStrategy.ForceReign:
                Force("ReignOfBeasts");
                break;
            case AkechiGNB.ReignStrategy.ForceNoble:
                Force("NobleBlood");
                break;
            case AkechiGNB.ReignStrategy.ForceLion:
                Force("LionHeart");
                break;
        }
    }

    private bool _forceBloodfest; // Bloodfest forced: pressed as soon as it is ready (the definition only allows it next to No Mercy)
    private bool _firstGcdDone;

    public override void Execute(StrategyValues strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        _forceBloodfest = false;
        base.Execute(strategy, primaryTarget, estimatedAnimLockDelay, isMoving);
        if (!Player.InCombat)
            _firstGcdDone = false;
        else if (GCD > 0)
            _firstGcdDone = true;
        if (Target == null || strategy.Option(Akechi.SharedTrack.Hold).As<Akechi.HoldStrategy>() == Akechi.HoldStrategy.HoldEverything)
            return;
        if (_forceBloodfest && Player.InCombat && ActionUnlocked(AID.Bloodfest) && ActionDefinitions.Instance.Spell(AID.Bloodfest)?.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions) <= GCD)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.Bloodfest), Target, ActionQueue.Priority.Medium + 1);

        // Lightning Shot: the pull's first GCD (from 0.8 s before the countdown ends) out of melee range (OpenerFar) or at any range
        // (OpenerForce), always (Force), or in combat out of melee range (Allow, below the engine's GCD)
        var outOfMelee = Player.DistanceToHitbox(Target) > 3;
        var pull = !_firstGcdDone && (Player.InCombat || World.Client.CountdownRemaining < 0.8f);
        var ls = strategy.Option(AkechiGNB.Track.LightningShot).As<AkechiGNB.LightningShotStrategy>();
        if (!ActionUnlocked(AID.LightningShot))
            return;
        if (Target.IsTargetable && (ls == AkechiGNB.LightningShotStrategy.OpenerFar && pull && outOfMelee || ls == AkechiGNB.LightningShotStrategy.OpenerForce && pull || ls == AkechiGNB.LightningShotStrategy.Force))
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.LightningShot), Target, ActionQueue.Priority.High + 3);
        else if (ls == AkechiGNB.LightningShotStrategy.Allow && Target.IsTargetable && Player.InCombat && outOfMelee)
            Hints.ActionsToExecute.Push(ActionID.MakeSpell(AID.LightningShot), Target, ActionQueue.Priority.High + 1);
    }

    // counted like the Akechi module (hitbox to hitbox), which is also how the AoE lands
    protected override byte CountTargets(Actor? primaryTarget) => (byte)Math.Max(1, Hints.NumPriorityTargetsInAOECircle(Player.Position, 5));

    // Level sync (below 100): The Balance GNB Leveling Guide's per-band priorities (docs/rebuild/engine-design.md section 20)
    // the 2-minute burst anchor the assumed raid-buff cycle follows on a pull without a countdown (EngineRotationModule.AssumedCycleStart)
    protected override string? MainAnchorCooldown => GnbDefinition.BloodfestCD;

    protected override bool HasSyncedRules => true;

    protected override int SyncedOgcd(in EngineState s, in EngineTimeline tl)
    {
        // Continuations first (any GCD drops them)
        if (FirstLegal(s, tl, "JugularRip", "AbdomenTear", "EyeGouge", "Hypervelocity", "FatedBrand") is var cont and >= 0)
            return cont;
        // No Mercy on cooldown; Bloodfest from 94 off cooldown next to it (the definition pairs them), below 94 inside No Mercy with the
        // gauge empty (anywhere in No Mercy: the definition's pairing only allows its first weave)
        if (Legal(s, tl, "NoMercy") is var noMercy and >= 0)
            return noMercy;
        if (Player.Level >= 94)
        {
            if (FirstLegal(s, tl, "Bloodfest", "BloodfestAfter") is var bloodfest and >= 0)
                return bloodfest;
        }
        else if (Job.TrySkillIndex("BloodfestAfter") is var after and >= 0 && !Disabled(s, "BloodfestAfter") && Charges(s, GnbDefinition.BloodfestCD) > 0
            && Gauge(s, GnbDefinition.Ammo) == 0 && StatusLeft(s, GnbDefinition.NoMercy) > 0 && !tl.InDowntime(s.Time))
        {
            return after;
        }
        // Danger Zone / Blasting Zone on cooldown; Bow Shock in every No Mercy
        if (Legal(s, tl, "BlastingZone") is var zone and >= 0)
            return zone;
        if ((StatusLeft(s, GnbDefinition.NoMercy) > 0 || Disabled(s, "NoMercy")) && Legal(s, tl, "BowShock") is var bow and >= 0)
            return bow;
        return -1;
    }

    protected override int SyncedGcd(in EngineState s, in EngineTimeline tl)
    {
        var ammo = Gauge(s, GnbDefinition.Ammo);
        var cap = Job.Gauges[Job.GaugeIndex(GnbDefinition.Ammo)].Max / 2;
        var bloodfest = StatusLeft(s, GnbDefinition.Bloodfest) > 0;
        var nm = StatusLeft(s, GnbDefinition.NoMercy);
        var aoe = ShapeTargets(s, "DemonSlice") >= (Job.HasSkill("DemonSlaughter") ? 2 : 3);
        // Gnashing Fang stops at 4 targets (3 from 94); Double Down at any count
        var fangOk = ShapeTargets(s, "DemonSlice") < (Player.Level >= 94 ? 3 : 4);
        var spender = Job.HasSkill("FatedCircle") && ShapeTargets(s, "FatedCircle") >= 2 ? "FatedCircle" : "BurstStrike";

        // the Gnashing Fang combo is never broken
        if (FirstLegal(s, tl, "SavageClaw", "WickedTalon") is var chain and >= 0)
            return chain;
        if (nm > 0)
        {
            // inside No Mercy: Double Down > Sonic Break > Gnashing Fang, then the cartridges left over beyond what Double Down / Gnashing
            // Fang coming back before it ends need
            if (Legal(s, tl, "DoubleDown") is var dd and >= 0)
                return dd;
            if (Legal(s, tl, "SonicBreak") is var sb and >= 0)
                return sb;
            if (fangOk && Legal(s, tl, "GnashingFang") is var gf and >= 0)
                return gf;
            var reserve = 0;
            if (Job.HasSkill("DoubleDown") && !Disabled(s, "DoubleDown") && ReadyIn(s, GnbDefinition.DoubleDownCD) < nm)
                reserve += 2;
            if (Job.HasSkill("GnashingFang") && fangOk && !Disabled(s, "GnashingFang") && ReadyIn(s, GnbDefinition.GnashingFangCD) < nm)
                reserve += 1;
            if (ammo > reserve && FirstLegal(s, tl, spender, "BurstStrike") is var burst and >= 0)
                return burst;
        }
        else
        {
            // Sonic Break left from No Mercy is not lost
            if (Legal(s, tl, "SonicBreak") is var sb and >= 0)
                return sb;
            // Double Down is held for No Mercy (unless No Mercy is off)
            if (Disabled(s, "NoMercy") && Legal(s, tl, "DoubleDown") is var dd and >= 0)
                return dd;
            // Gnashing Fang outside No Mercy only so it does not sit at two charges (or with No Mercy off)
            if (fangOk && (FullIn(s, GnbDefinition.GnashingFangCD) <= Job.BaseGcd || Disabled(s, "NoMercy")) && Legal(s, tl, "GnashingFang") is var gf and >= 0)
                return gf;
            // Bloodfest's cartridges beyond the cap are spent before it ends
            if (bloodfest && ammo > cap && FirstLegal(s, tl, spender, "BurstStrike") is var extra and >= 0)
                return extra;
        }
        // the combo; a cartridge is spent first when the finisher would overcap
        var finisher = ComboIs(s, "BrutalShell") ? Legal(s, tl, "SolidBarrel") : ComboIs(s, "DemonSlice") ? Legal(s, tl, "DemonSlaughter") : -1;
        if (finisher >= 0 && ammo >= (bloodfest ? cap * 2 : cap))
        {
            if (fangOk && Legal(s, tl, "GnashingFang") is var gf and >= 0)
                return gf;
            if (FirstLegal(s, tl, spender, "BurstStrike") is var overcap and >= 0)
                return overcap;
        }
        if (finisher >= 0)
            return finisher;
        if (ComboIs(s, "KeenEdge") && Legal(s, tl, "BrutalShell") is var brutal and >= 0)
            return brutal;
        return FirstLegal(s, tl, aoe ? "DemonSlice" : "KeenEdge", "KeenEdge");
    }

    protected override void ReadJobState(ref EngineState s, Actor? primaryTarget)
    {
        var gauge = World.Client.GetGauge<GunbreakerGauge>();
        s.Gauges[Job.GaugeIndex(GnbDefinition.Ammo)] = gauge.Ammo;
        var step = gauge.AmmoComboStep switch { 1 => GnbDefinition.SavageReady, 2 => GnbDefinition.TalonReady, 3 => GnbDefinition.NobleReady, 4 => GnbDefinition.LionReady, _ => null };
        if (step != null)
        {
            var i = Job.StatusIndex(step);
            s.StatusLeft[i] = 30;
            s.StatusStacks[i] = 1;
        }

        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.NoMercy), Player, (uint)SID.NoMercy);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.Bloodfest), Player, (uint)SID.Bloodfest);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToBreak), Player, (uint)SID.ReadyToBreak);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToReign), Player, (uint)SID.ReadyToReign);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToRip), Player, (uint)SID.ReadyToRip);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToTear), Player, (uint)SID.ReadyToTear);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToGouge), Player, (uint)SID.ReadyToGouge);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToBlast), Player, (uint)SID.ReadyToBlast);
        ReadStatus(ref s, Job.StatusIndex(GnbDefinition.ReadyToRaze), Player, (uint)SID.ReadyToRaze);

        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.NoMercyCD), ActionID.MakeSpell(AID.NoMercy));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.BloodfestCD), ActionID.MakeSpell(AID.Bloodfest));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.GnashingFangCD), ActionID.MakeSpell(AID.GnashingFang));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.DoubleDownCD), ActionID.MakeSpell(AID.DoubleDown));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.SonicBreakCD), ActionID.MakeSpell(AID.SonicBreak));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.ZoneCD), ActionID.MakeSpell(AID.BlastingZone));
        ReadCooldown(ref s, Job.CooldownIndex(GnbDefinition.BowShockCD), ActionID.MakeSpell(AID.BowShock));
        ReadCombo(ref s);
    }
}
