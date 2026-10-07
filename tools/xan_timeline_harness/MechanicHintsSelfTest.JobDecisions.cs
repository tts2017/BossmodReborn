using System;
using System.Numerics;
using BossMod;
using BossMod.Autorotation;

namespace XanTimelineHarness;

// Per-job decision tests: a single frame of a real rotation module in a minimal world (player + one enemy 2.5 y away), with the
// mechanic prediction pushed as an external snapshot, checking what the module queues.
internal static partial class MechanicHintsSelfTest
{
    private const ulong PlayerID = 0x10000001;
    private const ulong TargetID = 0x40000001;

    private sealed class Combat : IDisposable
    {
        public readonly WorldState World;
        public readonly Actor Player;
        public readonly Actor Target;
        public readonly AIHints Hints = new();
        public readonly BossModuleManager Bossmods;
        public readonly RotationModuleManager Manager;

        public Combat(Class job, float targetX = 2.5f)
        {
            World = NewWorld();
            World.Execute(new ActorState.OpCreate(PlayerID, 0, 0, 0, "Player", 0, ActorType.Player, job, 100, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
            World.Execute(new PartyState.OpModify(PartyState.PlayerSlot, new(1, PlayerID, false)));
            World.Execute(new ActorState.OpCreate(TargetID, 0x1234, 2, 0, "Target", 0, ActorType.Enemy, Class.None, 100, new Vector4(targetX, 0, 0, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), true, false, default, default, 0));
            World.Execute(new ActorState.OpCombat(TargetID, true));
            World.Execute(new ActorState.OpCombat(PlayerID, true));
            World.Execute(new ClientState.OpPlayerStatsChange(new(400, 400, 100)));
            var levels = new short[ClientState.NumClassLevels];
            Array.Fill(levels, (short)100);
            World.Execute(new ClientState.OpClassJobLevelsChange(levels));
            World.Execute(new ClientState.OpComboChange(default));
            World.Execute(new ClientState.OpCooldown(true, []));
            Player = World.Actors.Find(PlayerID)!;
            Target = World.Actors.Find(TargetID)!;
            Bossmods = new BossModuleManager(World);
            var db = new RotationDatabase(new System.IO.DirectoryInfo("tools/xan_timeline_harness/.autorotation"), new System.IO.FileInfo("BossMod/DefaultRotationPresets.json"));
            Manager = new RotationModuleManager(db, Bossmods, Hints) { CombatStart = BaseTime.AddSeconds(-60) };
        }

        public void Cooldown<AID>(AID action, float remaining) where AID : Enum
            => World.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.Instance.Spell(action)!.ActualMainCooldownGroup(World.Client.DutyActions), new(0, remaining))]));

        public void RefreshTargets()
        {
            Hints.Clear();
            var enemy = new AIHints.Enemy(Target, 1, false);
            Hints.Enemies[Target.CharacterSpawnIndex] = enemy;
            Hints.PotentialTargets.Add(enemy);
            Hints.HighestPotentialTargetPriority = 1;
        }

        public void Dispose()
        {
            Manager.Dispose();
            Bossmods.Dispose();
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);
        }
    }

    private static StrategyValues Strategy(RotationModuleDefinition definition, MechanicHintStrategy mode)
    {
        var strategy = new StrategyValues(definition.Configs);
        ((StrategyValueTrack)strategy.Values[strategy.Configs.FindIndex(c => c.InternalName == "MechanicHints")]).Option = (int)mode;
        return strategy;
    }

    private static void SetOption(StrategyValues strategy, string track, string option)
    {
        var index = strategy.Configs.FindIndex(c => c.InternalName == track);
        ((StrategyValueTrack)strategy.Values[index]).Option = ((StrategyConfigTrack)strategy.Configs[index]).Options.FindIndex(o => o.InternalName == option);
    }

    // writes one gauge byte field (FFXIVClientStructs layout: the payload's low word holds struct bytes 8..15)
    private static void SetGaugeByte<T>(WorldState world, string field, byte value) where T : unmanaged
    {
        var offset = (int)System.Runtime.InteropServices.Marshal.OffsetOf<T>(field) - 8;
        var low = world.Client.GaugePayload.Low & ~(0xFFul << (offset * 8)) | (ulong)value << (offset * 8);
        world.Client.GaugePayload = new(low, world.Client.GaugePayload.High);
    }

    private static bool Queued(AIHints hints, ActionID action, float minPriority = 0)
        => hints.ActionsToExecute.Entries.Exists(e => e.Action == action && e.Priority >= minPriority);

    private static partial void RunJobDecisionTests()
    {
        GnbTests();
        MchTests();
        VprTests();
        UserSettingAndGaugeTests();
        // NIN has no decision tests: xan NIN reads mudra charges from the native ActionManager, which does not exist headless
        PldTests();
    }

    // the wind-down must respect per-action Delay settings and must not dump gauge a partial sequence would waste
    private static void UserSettingAndGaugeTests()
    {
        {
            using var c = new Combat(Class.MCH);
            var module = new BossMod.Autorotation.xan.MCH(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.xan.MCH.Definition(), MechanicHintStrategy.All);
            SetOption(strategy, "Tools", "Delay");
            c.RefreshTargets();
            Push(c.World, TestNamespace, 2.6f, 40);
            module.Execute(strategy, c.Target, 0.1f, false);
            Check(!Queued(c.Hints, ActionID.MakeSpell(BossMod.MCH.AID.Drill), ActionQueue.Priority.High + 60), "mch-winddown-tools-delay", "Tools=Delay: no Drill wind-down");
        }
        {
            // Heat-funded Hypercharge cut to one slot would waste 50 Heat (4 of 5 Blazing Shots lost to the loss)
            using var c = new Combat(Class.MCH);
            var module = new BossMod.Autorotation.xan.MCH(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.xan.MCH.Definition(), MechanicHintStrategy.All);
            SetGaugeByte<FFXIVClientStructs.FFXIV.Client.Game.Gauge.MachinistGauge>(c.World, "Heat", 50);
            c.Cooldown(BossMod.MCH.AID.Drill, 40);
            c.Cooldown(BossMod.MCH.AID.AirAnchor, 40);
            c.Cooldown(BossMod.MCH.AID.ChainSaw, 60);
            c.RefreshTargets();
            Push(c.World, TestNamespace, 2.6f, 40);
            module.Execute(strategy, c.Target, 0.1f, false);
            Check(!Queued(c.Hints, ActionID.MakeSpell(BossMod.MCH.AID.Hypercharge)), "mch-winddown-no-partial-heat", "no Heat-funded Hypercharge cut by the loss");
        }
        {
            // the last three slots before the loss, which a Heat-funded Hypercharge fills exactly, but Wildfire is back in 5 s: the normal
            // rotation holds Hypercharge for it, and so must the wind-down
            // (spending it first left the Wildfire without its Blazing Shots: combat matrix -0.6% with hints)
            using var c = new Combat(Class.MCH);
            var module = new BossMod.Autorotation.xan.MCH(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.xan.MCH.Definition(), MechanicHintStrategy.All);
            SetGaugeByte<FFXIVClientStructs.FFXIV.Client.Game.Gauge.MachinistGauge>(c.World, "Heat", 50);
            c.Cooldown(BossMod.MCH.AID.Drill, 40);
            c.Cooldown(BossMod.MCH.AID.AirAnchor, 40);
            c.Cooldown(BossMod.MCH.AID.ChainSaw, 60);
            c.Cooldown(BossMod.MCH.AID.Wildfire, 5);
            c.RefreshTargets();
            Push(c.World, TestNamespace, 7.5f, 50);
            module.Execute(strategy, c.Target, 0.1f, false);
            Check(!Queued(c.Hints, ActionID.MakeSpell(BossMod.MCH.AID.Hypercharge)), "mch-winddown-hypercharge-waits-for-wildfire", "Hypercharge spent ahead of a Wildfire coming before the loss");
        }
        {
            using var c = new Combat(Class.VPR);
            var module = new BossMod.Autorotation.xan.VPR(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.xan.VPR.Definition(), MechanicHintStrategy.All);
            SetOption(strategy, "Buffs", "Delay");
            c.World.Execute(new ActorState.OpStatus(PlayerID, 0, new((uint)BossMod.VPR.SID.ReawakenReady, 0, c.World.FutureTime(30), PlayerID)));
            c.Cooldown(BossMod.VPR.AID.Vicewinder, 40);
            c.RefreshTargets();
            Push(c.World, TestNamespace, 2.6f, 70);
            module.Execute(strategy, c.Target, 0.1f, false);
            Check(!Queued(c.Hints, ActionID.MakeSpell(BossMod.VPR.AID.Reawaken), ActionQueue.Priority.High + 40), "vpr-winddown-buffs-delay", "Reawaken=Delay: no Reawaken wind-down");
        }
        {
            // Offering-funded Reawaken cut to one slot would waste 50 Offering (paid back only after the return)
            using var c = new Combat(Class.VPR);
            var module = new BossMod.Autorotation.xan.VPR(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.xan.VPR.Definition(), MechanicHintStrategy.All);
            SetGaugeByte<FFXIVClientStructs.FFXIV.Client.Game.Gauge.ViperGauge>(c.World, "SerpentOffering", 50);
            c.Cooldown(BossMod.VPR.AID.Vicewinder, 40);
            c.RefreshTargets();
            Push(c.World, TestNamespace, 2.6f, 70);
            module.Execute(strategy, c.Target, 0.1f, false);
            Check(!Queued(c.Hints, ActionID.MakeSpell(BossMod.VPR.AID.Reawaken), ActionQueue.Priority.High + 40), "vpr-winddown-no-partial-offering", "no Offering-funded Reawaken cut by the loss");
        }
        {
            using var c = new Combat(Class.PLD);
            var module = new BossMod.Autorotation.akechi.AkechiPLD(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.akechi.AkechiPLD.Definition(), MechanicHintStrategy.All);
            SetOption(strategy, "GB", "Delay");
            c.World.Execute(new ActorState.OpStatus(PlayerID, 0, new((uint)BossMod.PLD.SID.GoringBladeReady, 0, c.World.FutureTime(30), PlayerID)));
            c.RefreshTargets();
            Push(c.World, TestNamespace, 2.6f, 70);
            module.Execute(strategy, c.Target, 0.1f, false);
            Check(!Queued(c.Hints, ActionID.MakeSpell(BossMod.PLD.AID.GoringBlade), ActionQueue.Priority.High + 655), "pld-winddown-goring-delay", "Goring Blade=Delay: no wind-down");
        }
    }

    private static void PldTests()
    {
        // a hard-cast Holy Spirit (no Divine Might, standing still, target 10 y away) must carry its cast time into the queue, so the
        // forced-move cast cap can stop it; this is a bug fix that applies with every option
        foreach (var mode in new[] { MechanicHintStrategy.All, MechanicHintStrategy.Off })
        {
            using var c = new Combat(Class.PLD, targetX: 10f);
            var module = new BossMod.Autorotation.akechi.AkechiPLD(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.akechi.AkechiPLD.Definition(), mode);
            SetOption(strategy, "Ranged", "RangedCast");
            c.RefreshTargets();
            module.Execute(strategy, c.Target, 0.1f, false);
            var holy = c.Hints.ActionsToExecute.Entries.Find(e => e.Action == ActionID.MakeSpell(BossMod.PLD.AID.HolySpirit));
            Check(holy.Action == ActionID.MakeSpell(BossMod.PLD.AID.HolySpirit) && holy.CastTime > 0, $"pld-holy-casttime-{mode}", $"queued={holy.Action} castTime={holy.CastTime}");
        }

        // Fight or Flight (20 s window, 60 s recast) is held when a loss 10 s away with a 40 s return would cut it (60 > 40)
        foreach (var (name, mode, ret, expect) in new (string, MechanicHintStrategy, float, bool)[]
        {
            ("pld-fof-off", MechanicHintStrategy.Off, 40f, true),
            ("pld-fof-hold", MechanicHintStrategy.All, 40f, false),
            ("pld-fof-unknown-return", MechanicHintStrategy.All, float.MaxValue, true), // spec rule 1: no hold without a known return
        })
        {
            using var c = new Combat(Class.PLD);
            var module = new BossMod.Autorotation.akechi.AkechiPLD(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.akechi.AkechiPLD.Definition(), mode);
            c.World.Execute(new ClientState.OpComboChange(new((uint)BossMod.PLD.AID.FastBlade, 25)));
            c.RefreshTargets();
            Push(c.World, TestNamespace, 10, ret);
            module.Execute(strategy, c.Target, 0.1f, false);
            var fof = Queued(c.Hints, ActionID.MakeSpell(BossMod.PLD.AID.FightOrFlight));
            Check(fof == expect, name, $"Fight or Flight queued={fof}");
        }
    }

    private static void VprTests()
    {
        // Ready to Reawaken with both core buffs up: the normal rotation starts Reawaken. A short out-of-melee dodge 4 s away (inside the
        // ~9 s sequence, Generations are 3 y) must stop it; Off keeps the old behaviour.
        foreach (var (name, mode, rangeLoss, expect) in new (string, MechanicHintStrategy, float, bool)[]
        {
            ("vpr-reawaken-off", MechanicHintStrategy.Off, 4f, true),
            ("vpr-reawaken-range-loss", MechanicHintStrategy.All, 4f, false),
            ("vpr-reawaken-no-loss", MechanicHintStrategy.All, float.MaxValue, true),
        })
        {
            using var c = new Combat(Class.VPR);
            var module = new BossMod.Autorotation.xan.VPR(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.xan.VPR.Definition(), mode);
            c.World.Execute(new ActorState.OpStatus(PlayerID, 0, new((uint)BossMod.VPR.SID.ReawakenReady, 0, c.World.FutureTime(30), PlayerID)));
            c.World.Execute(new ActorState.OpStatus(PlayerID, 1, new((uint)BossMod.VPR.SID.HuntersInstinct, 0, c.World.FutureTime(40), PlayerID)));
            c.World.Execute(new ActorState.OpStatus(PlayerID, 2, new((uint)BossMod.VPR.SID.Swiftscaled, 0, c.World.FutureTime(40), PlayerID)));
            c.RefreshTargets();
            c.Hints.Disengage = new DisengageForecast(float.MaxValue, 0, rangeLoss, rangeLoss + 2);
            module.Execute(strategy, c.Target, 0.1f, false);
            var reawaken = Queued(c.Hints, ActionID.MakeSpell(BossMod.VPR.AID.Reawaken));
            Check(reawaken == expect, name, $"Reawaken queued={reawaken}");
        }

        // VPR has no wind-down: planning the last slots before a loss cost 0.4% on the combat matrix with hints (it broke Vicewinder chains
        // and pulled Reawaken forward), so one GCD slot before a 70 s loss nothing is queued at the wind-down priority, hints or not
        foreach (var (name, mode, expect) in new (string, MechanicHintStrategy, bool)[]
        {
            ("vpr-winddown-reawaken", MechanicHintStrategy.All, false),
            ("vpr-winddown-off", MechanicHintStrategy.Off, false),
        })
        {
            using var c = new Combat(Class.VPR);
            var module = new BossMod.Autorotation.xan.VPR(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.xan.VPR.Definition(), mode);
            c.World.Execute(new ActorState.OpStatus(PlayerID, 0, new((uint)BossMod.VPR.SID.ReawakenReady, 0, c.World.FutureTime(30), PlayerID)));
            c.Cooldown(BossMod.VPR.AID.Vicewinder, 40);
            c.RefreshTargets();
            Push(c.World, TestNamespace, 2.6f, 70);
            module.Execute(strategy, c.Target, 0.1f, false);
            var reawaken = Queued(c.Hints, ActionID.MakeSpell(BossMod.VPR.AID.Reawaken), ActionQueue.Priority.High + 40);
            Check(reawaken == expect, name, $"Reawaken wind-down queued={reawaken}");
        }
    }

    private static void MchTests()
    {
        // (Wildfire / Hypercharge holds need a full burst state to be observable in one frame; they are checked on harness traces instead)
        // one GCD slot left before a 40 s loss: Drill (660, charge back in 20 s) is the last GCD, pushed by the wind-down at priority 60
        foreach (var (name, mode, expect) in new (string, MechanicHintStrategy, bool)[]
        {
            ("mch-drill-last", MechanicHintStrategy.All, true),
            ("mch-drill-off", MechanicHintStrategy.Off, false),
        })
        {
            using var c = new Combat(Class.MCH);
            var module = new BossMod.Autorotation.xan.MCH(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.xan.MCH.Definition(), mode);
            c.RefreshTargets();
            Push(c.World, TestNamespace, 2.6f, 40);
            module.Execute(strategy, c.Target, 0.1f, false);
            var drill = Queued(c.Hints, ActionID.MakeSpell(BossMod.MCH.AID.Drill), ActionQueue.Priority.High + 60);
            Check(drill == expect, name, $"Drill wind-down queued={drill}");
        }
    }

    private static void GnbTests()
    {
        // one weave slot left before a 70 s loss: Blasting Zone (800, recast 30 s back by the return) beats Bow Shock (450) for it and
        // is queued at Severe+5; with Zone=Delay the slot goes to Bow Shock instead. The GCD side is left to the 7.4 planner, so Double
        // Down (held for No Mercy, which is on cooldown) is never pushed by the wind-down.
        foreach (var (name, loss, ret, mode, zoneDelay, expectZone, expectBow) in new (string, float, float, MechanicHintStrategy, bool, bool, bool)[]
        {
            ("gnb-zone-before-loss", 1.0f, 70f, MechanicHintStrategy.All, false, true, false),
            ("gnb-zone-off", 1.0f, 70f, MechanicHintStrategy.Off, false, false, false),
            ("gnb-zone-unknown-return", 1.0f, float.MaxValue, MechanicHintStrategy.All, false, false, false),
            ("gnb-zone-delay", 1.0f, 70f, MechanicHintStrategy.All, true, false, true),
        })
        {
            using var c = new Combat(Class.GNB);
            var module = new BossMod.Autorotation.akechi.AkechiGNB(c.Manager, c.Player);
            var strategy = Strategy(BossMod.Autorotation.akechi.AkechiGNB.Definition(), mode);
            if (zoneDelay)
                SetOption(strategy, "Zone", "Delay");
            c.World.Client.GaugePayload = new(2ul, 0); // 2 cartridges
            c.Cooldown(BossMod.GNB.AID.NoMercy, 40);
            c.Cooldown(BossMod.GNB.AID.Bloodfest, 90);
            c.RefreshTargets();
            if (loss < float.MaxValue)
                Push(c.World, TestNamespace, loss, ret);
            module.Execute(strategy, c.Target, 0.1f, false);
            var zone = Queued(c.Hints, ActionID.MakeSpell(BossMod.GNB.AID.BlastingZone), ActionQueue.Priority.Low + 755);
            var bow = Queued(c.Hints, ActionID.MakeSpell(BossMod.GNB.AID.BowShock), ActionQueue.Priority.Low + 755);
            var dd = Queued(c.Hints, ActionID.MakeSpell(BossMod.GNB.AID.DoubleDown), ActionQueue.Priority.High + 655);
            Check(zone == expectZone && bow == expectBow && !dd, name, $"zone={zone} bow={bow} dd={dd}");
        }
    }
}
