using System;
using System.Numerics;
using BossMod;
using BossMod.Autorotation.xan;
using AID = BossMod.NIN.AID;
using SID = BossMod.NIN.SID;

namespace XanTimelineHarness;

// Drives NinCombatState with hand-written press sequences and checks the client rules it has to reproduce (tooltips plus the replay
// measurements in NinReplayScan). Every NIN number the harness reports rests on these.
internal static class NinEmulatorSelfTest
{
    private sealed class Sim
    {
        private static readonly DateTime Start = new(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        private const float Step = 0.05f;
        public readonly WorldState World;
        public readonly Actor Player;
        public readonly Actor Target;
        public readonly NinCombatState Combat;
        private readonly AIHints _hints = new();
        private ulong _frame;
        public float LastPotency;
        public AID LastAction;

        public Sim(int level = 100, int ninki = 0)
        {
            World = new WorldState(TimeSpan.TicksPerSecond, "nin-selftest");
            World.Execute(new WorldState.OpFrameStart(new(Start, 0, 0, 0, 0, 1), default, default, default));
            World.Execute(new ActorState.OpCreate(0x10000001, 0, 0, 0, "Player", 0, ActorType.Player, Class.NIN, level, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
            World.Execute(new PartyState.OpModify(PartyState.PlayerSlot, new(1, 0x10000001, false)));
            World.Execute(new ActorState.OpCreate(0x40000001, 0x1234, 2, 0, "Target", 0, ActorType.Enemy, Class.None, 100, new Vector4(2.5f, 0, 0, MathF.PI), 2.0f, new(1000000, 1000000, 0, 10000, 10000), true, false, default, default, 0));
            World.Execute(new ActorState.OpCombat(0x10000001, true));
            World.Execute(new ActorState.OpCombat(0x40000001, true));
            World.Execute(new ClientState.OpPlayerStatsChange(new(400, 400, 100)));
            var levels = new short[ClientState.NumClassLevels];
            Array.Fill(levels, (short)level);
            World.Execute(new ClientState.OpClassJobLevelsChange(levels));
            World.Execute(new ClientState.OpCooldown(true, []));
            Player = World.Actors.Find(0x10000001)!;
            Target = World.Actors.Find(0x40000001)!;
            Combat = new(World, Player, Step, (action, _, _, potency) =>
            {
                LastPotency = potency;
                LastAction = (AID)action.ID;
            })
            {
                BaseTime = Start
            };
            Combat.Initialize(ninki);
            Combat.Advance();
        }

        public float Now => (float)(World.CurrentTime - Start).TotalSeconds;

        public void Wait(float seconds)
        {
            var frames = (int)MathF.Round(seconds / Step);
            for (var i = 0; i < frames; ++i)
            {
                ++_frame;
                World.Execute(new WorldState.OpFrameStart(new(World.CurrentTime.AddSeconds(Step), _frame, (uint)_frame, Step, Step, 1), TimeSpan.FromSeconds(Step), World.Client.GaugePayload, default));
                Combat.Advance();
            }
        }

        // waits until the action is ready (GCD / animation lock / charges) for at most 5s, then presses it; true if it executed
        public bool Press(AID aid, bool selfTarget = false)
        {
            for (var i = 0; i < 100; ++i)
            {
                _hints.Clear();
                _hints.ActionsToExecute.Push(ActionID.MakeSpell(aid), selfTarget ? Player : Target, ActionQueue.Priority.High);
                LastAction = AID.None;
                Combat.ExecuteBestAction(_hints);
                if (LastAction != AID.None)
                    return true;
                Wait(Step);
            }
            return false;
        }

        // waits out the GCD and animation lock, then tries the action exactly once; true if it executed
        public bool PressOnce(AID aid, bool selfTarget = false)
        {
            while (World.Client.Cooldowns[ActionDefinitions.GCDGroup].Remaining > 0 || World.Client.AnimationLock > 0)
                Wait(Step);
            _hints.Clear();
            _hints.ActionsToExecute.Push(ActionID.MakeSpell(aid), selfTarget ? Player : Target, ActionQueue.Priority.High);
            LastAction = AID.None;
            Combat.ExecuteBestAction(_hints);
            return LastAction != AID.None;
        }

        public float GCD => World.Client.Cooldowns[ActionDefinitions.GCDGroup].Total;
        public int Stacks(SID sid) => Player.FindStatus((uint)sid, Player.InstanceID) is ActorStatus s && s.ExpireAt > World.CurrentTime ? s.Extra & 0xFF : 0;
        public float Left(SID sid) => Player.FindStatus((uint)sid, Player.InstanceID) is ActorStatus s ? MathF.Max(0, (float)(s.ExpireAt - World.CurrentTime).TotalSeconds) : 0;
        public float TargetLeft(SID sid) => Target.FindStatus((uint)sid, Player.InstanceID) is ActorStatus s ? MathF.Max(0, (float)(s.ExpireAt - World.CurrentTime).TotalSeconds) : 0;
        public int Charges()
        {
            var def = ActionDefinitions.Instance.Spell(AID.Ten1)!;
            var cd = World.Client.Cooldowns[def.MainCooldownGroup];
            return cd.Total <= 0 ? 2 : (int)MathF.Floor(cd.Elapsed / def.Cooldown + 0.001f);
        }
    }

    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name} {detail}");
            if (!ok)
                ++failures;
        }

        {
            var s = new Sim();
            var ok = s.Press(AID.Ten1);
            var g1 = s.GCD;
            ok &= s.Press(AID.Chi2);
            var g2 = s.GCD;
            ok &= s.Press(AID.Raiton);
            Check("raiton", ok && s.LastAction == AID.Raiton && s.Stacks(SID.RaijuReady) == 1 && s.Charges() == 1 && g1 == 0.5f && g2 == 0.5f && s.GCD == 1.5f && MathF.Abs(s.LastPotency - 740) < 0.5f,
                $"raiju={s.Stacks(SID.RaijuReady)} charges={s.Charges()} gcd={g1}/{g2}/{s.GCD} potency={s.LastPotency}");
        }
        {
            var s = new Sim();
            s.Press(AID.Chi1);
            s.Press(AID.Ten2);
            var rejected = !s.Press(AID.Raiton);
            var katon = s.Press(AID.Katon) && s.LastAction == AID.Katon;
            var t = new Sim();
            t.Press(AID.Ten1);
            t.Press(AID.Ten2);
            var rabbit = t.Press(AID.Raiton) && t.Combat.Rabbits == 1 && t.LastPotency == 0;
            Check("mudra-validation", rejected && katon && rabbit && s.Combat.NinjutsuRejectedFrames > 0, $"rejected={rejected} katon={katon} rabbit={rabbit}");
        }
        {
            var s = new Sim();
            s.Press(AID.Kassatsu, true);
            s.Press(AID.Chi2);
            s.Press(AID.Jin2);
            var ok = s.Press(AID.HyoshoRanryu);
            Check("kassatsu-hyosho", ok && s.Charges() == 2 && s.Left(SID.Kassatsu) == 0 && MathF.Abs(s.LastPotency - 1690) < 0.5f, $"charges={s.Charges()} potency={s.LastPotency}");
        }
        {
            var s = new Sim();
            s.Press(AID.Kassatsu, true);
            Check("tcj-blocked-by-kassatsu", !s.Press(AID.TenChiJin, true));
        }
        {
            var s = new Sim();
            s.Press(AID.Ten1);
            s.Press(AID.Chi2);
            s.Press(AID.Raiton);
            s.Press(AID.SpinningEdge);
            var lost = s.Combat.RaijuLost == 1 && s.Stacks(SID.RaijuReady) == 0;
            var t = new Sim(ninki: 50);
            t.Press(AID.Ten1);
            t.Press(AID.Chi2);
            t.Press(AID.Raiton);
            t.Press(AID.Bunshin, true);
            t.Press(AID.PhantomKamaitachi);
            var kept = t.Stacks(SID.RaijuReady) == 1 && t.Stacks(SID.Bunshin) == 5;
            Check("raiju-melee-and-pk", lost && kept, $"lost={lost} kept={kept} bunshin={t.Stacks(SID.Bunshin)}");
        }
        {
            var s = new Sim(ninki: 50);
            s.Press(AID.Bunshin, true);
            var pk = s.Left(SID.PhantomKamaitachiReady) > 44;
            var total = 0f;
            for (var i = 0; i < 5; ++i)
            {
                s.Press(AID.SpinningEdge);
                total += s.LastPotency;
            }
            // five uncomboed Spinning Edges (300) plus five shadow hits (160); ninki 0 + 5 x (5 shukiho + 5 shadow)
            Check("bunshin", pk && s.Stacks(SID.Bunshin) == 0 && MathF.Abs(total - 5 * 460) < 1 && s.Combat.Ninki == 50, $"pk={pk} stacks={s.Stacks(SID.Bunshin)} potency={total} ninki={s.Combat.Ninki}");
        }
        {
            var s = new Sim();
            s.Press(AID.TenChiJin, true);
            var tenri = s.Left(SID.TenriJindoReady) > 29;
            s.Press(AID.FumaTen);
            var g1 = s.GCD;
            s.Press(AID.TCJRaiton);
            var g2 = s.GCD;
            var invalid = !s.PressOnce(AID.TCJKaton);
            s.Press(AID.TCJSuiton);
            var g3 = s.GCD;
            Check("tcj", tenri && g1 == 1.0f && g2 == 1.0f && g3 == 1.5f && invalid && s.Left(SID.ShadowWalker) > 19 && s.Left(SID.TenChiJin) == 0 && s.Stacks(SID.RaijuReady) == 1 && s.Combat.TenChiJinIncomplete == 0,
                $"tenri={tenri} gcd={g1}/{g2}/{g3} invalidRejected={invalid} sw={s.Left(SID.ShadowWalker)}");
        }
        {
            var s = new Sim();
            s.Press(AID.Ten1);
            s.Press(AID.Chi2);
            s.Press(AID.Jin2);
            s.Press(AID.Suiton);
            var sw = s.Left(SID.ShadowWalker) > 19;
            var pressed = s.Press(AID.KunaisBane);
            var immediate = s.TargetLeft(SID.KunaisBane);
            s.Wait(1.35f);
            var refreshed = s.TargetLeft(SID.KunaisBane);
            Check("kunai", sw && pressed && s.Left(SID.ShadowWalker) == 0 && MathF.Abs(immediate - 15) < 0.06f && MathF.Abs(refreshed - (16.29f - 1.35f)) < 0.06f,
                $"immediate={immediate:f2} after1.35={refreshed:f2}");
        }
        {
            var s = new Sim();
            s.Press(AID.Dokumori);
            var doku = s.TargetLeft(SID.Dokumori) > 19.9f && s.Left(SID.Higi) > 29 && s.Combat.Ninki == 40;
            s.Wait(2.5f);
            s.Combat.Initialize(60);
            var zesho = s.Press(AID.ZeshoMeppo) && s.Left(SID.Higi) == 0 && s.Combat.Ninki == 10;
            Check("dokumori-higi", doku && zesho, $"doku={doku} zesho={zesho} ninki={s.Combat.Ninki}");
        }
        {
            var s = new Sim(ninki: 50);
            s.Press(AID.Ten1);
            s.Press(AID.Chi2);
            s.Press(AID.Jin2);
            s.Press(AID.Suiton);
            var meisui = s.Press(AID.Meisui, true) && s.Left(SID.ShadowWalker) == 0 && s.Left(SID.Meisui) > 29 && s.Combat.Ninki == 100;
            var bhava = s.Press(AID.Bhavacakra) && MathF.Abs(s.LastPotency - 550) < 0.5f && s.Left(SID.Meisui) == 0;
            Check("meisui", meisui && bhava, $"meisui={meisui} bhava={bhava} potency={s.LastPotency}");
        }
        {
            var s = new Sim(ninki: 90);
            s.Press(AID.Dokumori);
            Check("ninki-overcap", s.Combat.NinkiOvercap == 30 && s.Combat.Ninki == 100, $"overcap={s.Combat.NinkiOvercap}");
        }
        {
            // measured from replays: a hit pressed 0.5s after Kunai's Bane is +10%, one 16.25s after still is, one 16.5s after is not
            var s = new Sim();
            s.Press(AID.Ten1);
            s.Press(AID.Chi2);
            s.Press(AID.Jin2);
            s.Press(AID.Suiton);
            s.Wait(1.5f);
            s.Press(AID.KunaisBane);
            s.Wait(0.5f);
            s.Press(AID.SpinningEdge);
            var early = s.LastPotency;
            var t = new Sim();
            t.Press(AID.Ten1);
            t.Press(AID.Chi2);
            t.Press(AID.Jin2);
            t.Press(AID.Suiton);
            t.Wait(1.5f);
            t.Press(AID.KunaisBane);
            var kunaiAt = t.Now;
            t.Wait(16.2f);
            t.Press(AID.SpinningEdge);
            var late = t.LastPotency;
            var lateAt = t.Now - kunaiAt;
            t.Wait(0.4f);
            t.Press(AID.ThrowingDagger);
            var after = t.LastPotency;
            Check("kunai-window", MathF.Abs(early - 330) < 0.5f && MathF.Abs(late - 330) < 0.5f && MathF.Abs(after - 200) < 0.5f, $"early={early} late={late}@{lateAt:f2} after={after}");
        }

        Console.WriteLine($"nin_emulator_selftest failures={failures}");
        return failures == 0 ? 0 : 3;
    }
}
