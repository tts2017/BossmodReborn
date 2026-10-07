using BossMod.SCH;
using FFXIVClientStructs.FFXIV.Client.Game.Gauge;
using static BossMod.AIHints;

namespace BossMod.Autorotation.akechi.Custom;

public sealed class AkechiSCH(RotationModuleManager manager, Actor player) : AkechiTools<AID, TraitID>(manager, player)
{
    public enum Track { AOE = SharedTrack.Count, Bio, EnergyDrain, ChainStratagem, Aetherflow }
    public enum AOEStrategy { Auto, AutoNoRuin, ForceST, ForceRuin, ForceBroil, ForceArtOfWar }
    public enum BioStrategy { Bio3, Bio6, Bio9, Bio0, Force, Delay }
    public enum EnergyStrategy { Use3, Use2, Use1, Force, ForceWeave, Delay }

    public static RotationModuleDefinition Definition()
    {
        var res = new RotationModuleDefinition("Akechi SCH [Custom]", "標準回しモジュール", "標準回し(Akechi)|ヒーラー", "Akechi", RotationModuleQuality.Ok, BitMask.Build((int)Class.SCH), 100);

        res.DefineTargeting();
        res.DefineHold();
        res.DefinePotion(ActionDefinitions.IDPotionMnd);

        res.Define(Track.AOE).As<AOEStrategy>("ST/AOE", "単体 / 範囲回し", 300)
            .AddOption(AOEStrategy.Auto, "周囲の敵数に応じて最適な攻撃を自動使用する - 停止中は Broil、移動中は Ruin、十分な敵数なら Art of War")
            .AddOption(AOEStrategy.AutoNoRuin, "周囲の敵数に応じて最適な攻撃を自動使用する - Ruin は使わない")
            .AddOption(AOEStrategy.ForceST, "周囲の敵数に関係なく単体攻撃を強制する - 停止中は Broil、移動中は Ruin")
            .AddOption(AOEStrategy.ForceRuin, "周囲の敵数に関係なく Ruin のみを強制する")
            .AddOption(AOEStrategy.ForceBroil, "周囲の敵数に関係なく Broil のみを強制する")
            .AddOption(AOEStrategy.ForceArtOfWar, "周囲の敵数に関係なく Art of War のみを強制する")
            .AddAssociatedActions(AID.Ruin1, AID.Ruin2, AID.Broil1, AID.Broil2, AID.Broil3, AID.Broil4, AID.ArtOfWar1, AID.ArtOfWar2);

        res.Define(Track.Bio).As<BioStrategy>("Bio", "Biolysis (DoT)", 190)
            .AddOption(BioStrategy.Bio3, "対象の DoT 残りが3秒以下なら Biolysis を使う", 0, 30, ActionTargets.Hostile, 2)
            .AddOption(BioStrategy.Bio6, "対象の DoT 残りが6秒以下なら Biolysis を使う", 0, 30, ActionTargets.Hostile, 2)
            .AddOption(BioStrategy.Bio9, "対象の DoT 残りが9秒以下なら Biolysis を使う", 0, 30, ActionTargets.Hostile, 2)
            .AddOption(BioStrategy.Bio0, "対象に DoT が無いときだけ Biolysis を使う", 0, 30, ActionTargets.Hostile, 2)
            .AddOption(BioStrategy.Force, "DoT 状況に関係なく Biolysis を強制する", 0, 30, ActionTargets.Hostile, 2)
            .AddOption(BioStrategy.Delay, "Biolysis を遅らせる", 0, 0, ActionTargets.Hostile, 2)
            .AddAssociatedActions(AID.Bio1, AID.Bio2, AID.Biolysis);

        res.Define(Track.EnergyDrain).As<EnergyStrategy>("E.Drain", "Energy Drain", 150)
            .AddOption(EnergyStrategy.Use3, "Aetherflow をすべて Energy Drain に使う - 手動用の温存はしない", 0, 0, ActionTargets.Hostile, 45)
            .AddOption(EnergyStrategy.Use2, "Aetherflow を2スタックまで Energy Drain に使う - 1スタックは手動用に残すが、Aetherflow 再使用が近ければ残りも消費する", 0, 0, ActionTargets.Hostile, 45)
            .AddOption(EnergyStrategy.Use1, "Aetherflow を1スタックまで Energy Drain に使う - 2スタックは手動用に残すが、Aetherflow 再使用が近ければ残りも消費する", 0, 0, ActionTargets.Hostile, 45)
            .AddOption(EnergyStrategy.Force, "Aetherflow があれば Energy Drain を強制する", 0, 0, ActionTargets.Hostile, 45)
            .AddOption(EnergyStrategy.ForceWeave, "Aetherflow があれば次の挟み枠で Energy Drain を強制する", 0, 0, ActionTargets.Hostile, 45)
            .AddOption(EnergyStrategy.Delay, "Energy Drain を遅らせる", 0, 0, ActionTargets.None, 45)
            .AddAssociatedActions(AID.EnergyDrain);

        res.DefineOGCD(Track.ChainStratagem, AID.ChainStratagem, "C.Strat", "Chain Stratagem", 170, 120, 20, ActionTargets.Hostile, 66);
        res.DefineOGCD(Track.Aetherflow, AID.Aetherflow, "AF", "Aetherflow", 160, 60, 10, ActionTargets.Self, 45);

        return res;
    }

    private AID BestBroil => Unlocked(AID.Broil4) ? AID.Broil4 : Unlocked(AID.Broil3) ? AID.Broil3 : Unlocked(AID.Broil2) ? AID.Broil2 : AID.Broil1;
    private AID BestRuin => Unlocked(AID.Ruin2) ? AID.Ruin2 : AID.Ruin1;
    private AID BestBio => Unlocked(AID.Biolysis) ? AID.Biolysis : Unlocked(AID.Bio2) ? AID.Bio2 : AID.Bio1;
    private SID BestDOT => Unlocked(AID.Biolysis) ? SID.Biolysis : Unlocked(AID.Bio2) ? SID.Bio2 : SID.Bio1;
    private AID BestST => Unlocked(AID.Broil1) ? BestBroil : BestRuin;
    private AID BestAOE => Unlocked(AID.ArtOfWar2) ? AID.ArtOfWar2 : AID.ArtOfWar1;

    private static readonly SID[] DotStatuses = [SID.Bio1, SID.Bio2, SID.Biolysis];

    // called for every candidate target each frame: a plain loop over a static list (the first of our Bio statuses still running)
    private float BioRemaining(Actor? target)
    {
        if (target == null)
            return float.MaxValue;
        foreach (var sid in DotStatuses)
        {
            var left = StatusDetails(target, (uint)sid, Player.InstanceID).Left;
            if (left > 0)
                return left;
        }
        return 0;
    }

    public override void Execution(StrategyValues strategy, Enemy? primaryTarget)
    {
        var gauge = World.Client.GetGauge<ScholarGauge>();
        var mainTarget = primaryTarget?.Actor;
        var aetherflow = gauge.Aetherflow;
        var CanED = Unlocked(AID.EnergyDrain) && aetherflow > 0;
        var CanAF = ActionReady(AID.Aetherflow) && aetherflow == 0;
        var (dotTargets, BioLeft) = GetDOTTarget(primaryTarget, BioRemaining, TargetsInAOECircle(5f, 4) ? 3 : 4);
        var BestDOTTarget = Unlocked(AID.Bio1) ? dotTargets : primaryTarget;

        var aoe = strategy.Option(Track.AOE);
        var aoeStrat = aoe.As<AOEStrategy>();
        var stTarget = SingleTargetChoice(mainTarget, aoe);
        var wantAOE = aoeStrat == AOEStrategy.ForceArtOfWar || TargetsInAOECircle(5f, 2);
        var bestTarget = wantAOE ? Player : stTarget;
        var (aoeAction, aoeTarget) = aoeStrat switch
        {
            AOEStrategy.Auto => (wantAOE ? BestAOE : (IsMoving ? BestRuin : BestST), bestTarget),
            AOEStrategy.AutoNoRuin => (wantAOE ? BestAOE : BestST, bestTarget),
            AOEStrategy.ForceST => (IsMoving ? BestRuin : BestST, stTarget),
            AOEStrategy.ForceRuin => (BestRuin, stTarget),
            AOEStrategy.ForceBroil => (BestST, stTarget),
            AOEStrategy.ForceArtOfWar => (BestAOE, Player),
            _ => (AID.None, null)
        };
        if (InCombat(aoeTarget) && aoeAction != AID.None)
            QueueGCD(aoeAction, aoeTarget, GCDPriority.Low);

        if (Unlocked(AID.Bio1))
        {
            var b = strategy.Option(Track.Bio);
            var bStrat = b.As<BioStrategy>();
            var bTarget = AOETargetChoice(mainTarget, BestDOTTarget?.Actor, b, strategy);
            if (InCombat(bTarget) && In25y(bTarget) && bStrat switch
            {
                BioStrategy.Bio3 => BioLeft <= 3,
                BioStrategy.Bio6 => BioLeft <= 6,
                BioStrategy.Bio9 => BioLeft <= 9,
                BioStrategy.Bio0 => BioLeft == 0,
                BioStrategy.Force => true,
                _ => false
            })
                QueueGCD(BestBio, bTarget, GCDPriority.Average);
        }

        if (HasStatus(SID.ImpactImminent))
            QueueOGCD(AID.BanefulImpaction, aoeTarget, OGCDPriority.VeryHigh);

        var cs = strategy.Option(Track.ChainStratagem);
        var csStrat = cs.As<OGCDStrategy>();
        var csTarget = SingleTargetChoice(mainTarget, cs);
        if (ShouldUseOGCD(csStrat, csTarget, ActionReady(AID.ChainStratagem), InCombat(csTarget) && CanWeaveIn && !HasStatus(SID.ChainStratagem)))
            QueueOGCD(AID.ChainStratagem, csTarget, OGCDPrio(csStrat, OGCDPriority.High + 1));

        var afStrat = strategy.Option(Track.Aetherflow).As<OGCDStrategy>();
        if (ShouldUseOGCD(afStrat, mainTarget, ActionReady(AID.Aetherflow), InCombat(mainTarget) && CanWeaveIn && aetherflow == 0))
            QueueOGCD(AID.Aetherflow, Player, OGCDPrio(afStrat, OGCDPriority.High));

        if (Unlocked(AID.EnergyDrain))
        {
            var ed = strategy.Option(Track.EnergyDrain);
            var edStrat = ed.As<EnergyStrategy>();
            var edTarget = SingleTargetChoice(mainTarget, ed);
            var normal = aetherflow > 0 && InCombat(edTarget) && In25y(edTarget) && CanWeaveIn;
            var needTimer = Cooldown(AID.Aetherflow) <= 5;
            var need = aetherflow > 0 && needTimer;
            var (edCondition, edPrio) = edStrat switch
            {
                EnergyStrategy.Use3 => (normal, OGCDPriority.Average),
                EnergyStrategy.Use2 => (normal && ((aetherflow > 1 && !needTimer) || need), OGCDPriority.Average),
                EnergyStrategy.Use1 => (normal && ((aetherflow > 2 && !needTimer) || need), OGCDPriority.Average),
                EnergyStrategy.Force => (aetherflow > 0, OGCDPriority.Average + 2000),
                EnergyStrategy.ForceWeave => (aetherflow > 0 && CanWeaveIn, OGCDPriority.Average + 1000),
                _ => (false, OGCDPriority.None)
            };
            if (edCondition)
                QueueOGCD(AID.EnergyDrain, edTarget, edPrio);
        }

        if (ActionReady(AID.LucidDreaming) && MP <= 9000 && CanWeaveIn)
            QueueOGCD(AID.LucidDreaming, Player, OGCDPriority.Average - 1);

        if (strategy.Potion() switch
        {
            PotionStrategy.AlignWithBuffs => Player.InCombat && Cooldown(AID.ChainStratagem) <= 4f,
            PotionStrategy.AlignWithRaidBuffs => Player.InCombat && (RaidBuffsIn <= 5f || RaidBuffsLeft > 0), // seconds, as in DRK and BLM
            PotionStrategy.Immediate => true,
            _ => false
        })
            QueuePotMND();

        AnyGoalZoneCombined(25, Hints.GoalAOECircle(5), AID.ArtOfWar1, Unlocked(AID.Broil1) ? 2 : 1);
    }
}
