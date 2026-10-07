using System;
using System.Collections.Generic;
using System.Linq;
using BossMod.Autorotation.xan;
using AID = BossMod.NIN.AID;

namespace XanTimelineHarness;

// Checks NinBurstPlanner on hand-built burst states: feasibility fallbacks, the shape of each variant's sequence, hint handling,
// hysteresis, and that no simulated weave would clip a GCD. NIN_PLANNER_TRACE=1 prints both sequences of every case.
internal static class NinPlannerSelfTest
{
    private static NinBurstContext EvenBurst() => new()
    {
        Level = 100,
        GCD = 0,
        GcdLength = 2.12f,
        AnimLock = 0,
        WeaveLock = 0.65f,
        MudraCharges = 2,
        MudraMax = 2,
        MudraNextCharge = 0,
        KassatsuReadyIn = 0,
        TenChiJinReadyIn = 0,
        DreamReadyIn = 0,
        MeisuiReadyIn = 0,
        BunshinReadyIn = 30,
        PhantomLeft = 30,
        Ninki = 30,
        Kazematoi = 2,
        ShadowWalkerLeft = 18,
        KunaiSincePress = float.MaxValue,
        KunaiReadyIn = 0,
        DokumoriSincePress = float.MaxValue,
        DokumoriReadyIn = 0,
        EvenBurst = true,
        PartyBuffIn = 2,
        TargetLossIn = float.MaxValue,
        TargetReturnIn = float.MaxValue,
        HyoshoUnlocked = true,
        RaijuUnlocked = true,
        TenChiJinUnlocked = true,
        TenriUnlocked = true,
        ZeshoUnlocked = true,
        PhantomUnlocked = true,
        MeisuiUnlocked = true,
        DreamUnlocked = true,
        ShukihoUnlocked = true
    };

    public static int Run()
    {
        var failures = 0;
        var printTraces = Environment.GetEnvironmentVariable("NIN_PLANNER_TRACE") == "1";
        var planner = new NinBurstPlanner();
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name} {detail}");
            if (!ok)
                ++failures;
        }
        List<NinPlannedStep> Trace(in NinBurstContext ctx, NinBurstVariant variant)
        {
            var trace = new List<NinPlannedStep>();
            planner.Simulate(ctx, variant, trace);
            if (printTraces)
                Console.WriteLine($"  {variant}: " + string.Join(' ', trace.Select(s => FormattableString.Invariant($"{s.Time:f2}:{s.Action}{(s.InKunai ? "*" : "")}"))));
            return trace;
        }

        {
            var ctx = EvenBurst();
            var eval = planner.Select(ctx, NinBurstVariant.None);
            Trace(ctx, NinBurstVariant.RaitonFirst);
            Trace(ctx, NinBurstVariant.KassatsuFirst);
            var best = eval.RaitonFirstValue > eval.KassatsuFirstValue ? NinBurstVariant.RaitonFirst : NinBurstVariant.KassatsuFirst;
            Check("even-pk-available", eval.RaitonFirstFeasible && eval.KassatsuFirstFeasible && float.IsFinite(eval.RaitonFirstValue) && float.IsFinite(eval.KassatsuFirstValue)
                && (eval.Choice == best || MathF.Abs(eval.RaitonFirstValue - eval.KassatsuFirstValue) < planner.SwitchMargin),
                FormattableString.Invariant($"rf={eval.RaitonFirstValue:f0} kf={eval.KassatsuFirstValue:f0} choice={eval.Choice} ({eval.Reason})"));
        }
        {
            var ctx = EvenBurst();
            ctx.MudraCharges = 0;
            ctx.MudraNextCharge = 10;
            var eval = planner.Select(ctx, NinBurstVariant.None);
            Check("even-no-charge", !eval.RaitonFirstFeasible && eval.Choice == NinBurstVariant.KassatsuFirst, $"choice={eval.Choice}");
        }
        {
            var ctx = EvenBurst();
            ctx.KassatsuReadyIn = 8;
            var eval = planner.Select(ctx, NinBurstVariant.None);
            Check("kassatsu-late", !eval.KassatsuFirstFeasible && eval.Choice == NinBurstVariant.RaitonFirst, $"choice={eval.Choice}");
        }
        {
            var ctx = EvenBurst();
            ctx.EvenBurst = false;
            ctx.DokumoriReadyIn = 60;
            ctx.TenChiJinReadyIn = 60;
            ctx.MeisuiReadyIn = 60;
            ctx.PhantomLeft = 0;
            var eval = planner.Select(ctx, NinBurstVariant.None);
            Trace(ctx, NinBurstVariant.RaitonFirst);
            Trace(ctx, NinBurstVariant.KassatsuFirst);
            Check("odd-window", eval.RaitonFirstFeasible && eval.KassatsuFirstFeasible && eval.Choice != NinBurstVariant.None,
                FormattableString.Invariant($"rf={eval.RaitonFirstValue:f0} kf={eval.KassatsuFirstValue:f0} choice={eval.Choice}"));
        }
        {
            // the previous minute's Kunai's Bane (48s ago, back in 12s) is not this burst's: both variants stay open and plan a new one
            var ctx = EvenBurst();
            ctx.EvenBurst = false;
            ctx.KunaiSincePress = 48;
            ctx.KunaiReadyIn = 12;
            ctx.KassatsuReadyIn = 11;
            ctx.DokumoriSincePress = 48;
            ctx.DokumoriReadyIn = 72;
            ctx.TenChiJinReadyIn = 60;
            ctx.MeisuiReadyIn = 60;
            ctx.PhantomLeft = 0;
            var eval = planner.Select(ctx, NinBurstVariant.None);
            var kf = Trace(ctx, NinBurstVariant.KassatsuFirst);
            var kunai = kf.Where(s => s.Action == AID.KunaisBane).Select(s => s.Time).DefaultIfEmpty(float.NaN).First();
            Check("odd-window-kunai-on-cooldown", eval.RaitonFirstFeasible && eval.KassatsuFirstFeasible && MathF.Abs(NinBurstFeasibility.EarliestKunai(ctx) - 12) < 0.01f && kunai >= 12 - 1e-3f,
                FormattableString.Invariant($"rf={eval.RaitonFirstFeasible} kf={eval.KassatsuFirstFeasible} earliest={NinBurstFeasibility.EarliestKunai(ctx):f2} kunai={kunai:f2}"));
        }
        {
            // the previous even burst's Dokumori (115s ago) does not anchor this one: Kunai's Bane follows the new Dokumori
            var ctx = EvenBurst();
            ctx.KunaiSincePress = 55;
            ctx.KunaiReadyIn = 5;
            ctx.DokumoriSincePress = 115;
            ctx.DokumoriReadyIn = 5;
            var eval = planner.Select(ctx, NinBurstVariant.None);
            var kf = Trace(ctx, NinBurstVariant.KassatsuFirst);
            var dokumori = kf.Where(s => s.Action == AID.Dokumori).Select(s => s.Time).DefaultIfEmpty(float.NaN).First();
            var kunai = kf.Where(s => s.Action == AID.KunaisBane).Select(s => s.Time).DefaultIfEmpty(float.NaN).First();
            Check("even-window-previous-dokumori", eval.RaitonFirstFeasible && eval.KassatsuFirstFeasible && NinBurstFeasibility.EarliestKunai(ctx) >= 5 + NinBurstTiming.KunaiAfterDokumori - 0.01f
                && dokumori >= 5 - 1e-3f && kunai >= dokumori + NinBurstTiming.KunaiAfterDokumori - 0.01f,
                FormattableString.Invariant($"rf={eval.RaitonFirstFeasible} kf={eval.KassatsuFirstFeasible} earliest={NinBurstFeasibility.EarliestKunai(ctx):f2} dokumori={dokumori:f2} kunai={kunai:f2}"));
        }
        {
            // Kassatsu already up before Kunai's Bane: a Raiton before it would be turned into Kassatsu's ninjutsu, so only Kassatsu first
            var ctx = EvenBurst();
            ctx.KassatsuLeft = 12;
            ctx.KassatsuReadyIn = 50;
            var eval = planner.Select(ctx, NinBurstVariant.RaitonFirst);
            Check("kassatsu-active", !eval.RaitonFirstFeasible && eval.Choice == NinBurstVariant.KassatsuFirst, $"choice={eval.Choice}");
        }
        {
            // Shadow Walker running out before the pair could finish: Raiton first cannot get Kunai's Bane out, Kassatsu first still can
            var ctx = EvenBurst();
            ctx.ShadowWalkerLeft = 3;
            var eval = planner.Select(ctx, NinBurstVariant.None);
            var kf = Trace(ctx, NinBurstVariant.KassatsuFirst);
            var kunai = kf.Where(s => s.Action == AID.KunaisBane).Select(s => s.Time).DefaultIfEmpty(float.NaN).First();
            Check("shadow-walker-short", !eval.RaitonFirstFeasible && eval.Choice == NinBurstVariant.KassatsuFirst && kunai < 3,
                FormattableString.Invariant($"rf={eval.RaitonFirstFeasible} choice={eval.Choice} kunai={kunai:f2}"));
        }
        {
            var ctx = EvenBurst();
            var clean = planner.Select(ctx, NinBurstVariant.None);
            ctx.TargetLossIn = 11;
            ctx.TargetReturnIn = 31;
            var loss = planner.Select(ctx, NinBurstVariant.None);
            Check("target-loss-mid", loss.RaitonFirstValue < clean.RaitonFirstValue && loss.KassatsuFirstValue < clean.KassatsuFirstValue,
                FormattableString.Invariant($"rf {clean.RaitonFirstValue:f0}->{loss.RaitonFirstValue:f0} kf {clean.KassatsuFirstValue:f0}->{loss.KassatsuFirstValue:f0} choice={loss.Choice}"));
        }
        {
            var ctx = EvenBurst();
            var sticky = new NinBurstPlanner { SwitchMargin = 1e9f };
            var a = sticky.Select(ctx, NinBurstVariant.RaitonFirst);
            var b = sticky.Select(ctx, NinBurstVariant.KassatsuFirst);
            Check("hysteresis", a.Choice == NinBurstVariant.RaitonFirst && b.Choice == NinBurstVariant.KassatsuFirst, $"{a.Choice}/{b.Choice}");
        }
        {
            // every simulated oGCD must leave its weave lock before the next GCD press
            var ctx = EvenBurst();
            ctx.WeaveLock = 0.72f;
            var ok = true;
            foreach (var variant in new[] { NinBurstVariant.RaitonFirst, NinBurstVariant.KassatsuFirst })
            {
                var trace = Trace(ctx, variant);
                var gcds = trace.Where(s => IsGcd(s.Action)).Select(s => s.Time).ToList();
                foreach (var step in trace.Where(s => !IsGcd(s.Action)))
                {
                    var next = gcds.Where(g => g > step.Time).DefaultIfEmpty(float.MaxValue).Min();
                    // ninjutsu press times sit after their mudra, so compare with the start of that GCD block
                    ok &= next - step.Time >= ctx.WeaveLock - 1e-3f || next == float.MaxValue || NextIsNinjutsuBlock(trace, step.Time, ctx.WeaveLock);
                }
            }
            Check("high-ping-feasibility", ok);
        }
        {
            var ctx = EvenBurst();
            ctx.Level = 90;
            ctx.TenriUnlocked = false;
            ctx.ZeshoUnlocked = false;
            var eval = planner.Select(ctx, NinBurstVariant.None);
            var trace = Trace(ctx, eval.Choice);
            Check("level-90", float.IsFinite(eval.RaitonFirstValue) && !trace.Any(s => s.Action is AID.TenriJindo or AID.ZeshoMeppo), $"choice={eval.Choice}");
        }
        {
            var ctx = EvenBurst();
            var rf = Trace(ctx, NinBurstVariant.RaitonFirst).Select(s => s.Action).ToList();
            var kf = Trace(ctx, NinBurstVariant.KassatsuFirst).Select(s => s.Action).ToList();
            var rfShape = rf.IndexOf(AID.Raiton) < rf.IndexOf(AID.KunaisBane) && rf.IndexOf(AID.PhantomKamaitachi) < rf.IndexOf(AID.KunaisBane)
                && rf.IndexOf(AID.HyoshoRanryu) > rf.IndexOf(AID.KunaisBane) && rf.IndexOf(AID.FumaTen) > rf.IndexOf(AID.HyoshoRanryu);
            var kfShape = kf.IndexOf(AID.KunaisBane) >= 0 && kf.IndexOf(AID.HyoshoRanryu) > kf.IndexOf(AID.KunaisBane)
                && !kf.Take(kf.IndexOf(AID.KunaisBane)).Any(a => a is AID.Raiton or AID.HyoshoRanryu) && kf.IndexOf(AID.FumaTen) > kf.IndexOf(AID.HyoshoRanryu);
            Check("trace-shape", rfShape && kfShape, $"rf=[{string.Join(',', rf.Take(10))}] kf=[{string.Join(',', kf.Take(10))}]");
        }

        Console.WriteLine($"nin_planner_selftest failures={failures}");
        return failures == 0 ? 0 : 3;
    }

    private static bool IsGcd(AID aid) => aid is AID.SpinningEdge or AID.Raiton or AID.HyoshoRanryu or AID.FleetingRaiju or AID.PhantomKamaitachi or AID.FumaTen or AID.TCJRaiton or AID.TCJSuiton;

    // a ninjutsu is recorded at its press, one second after its mudra started the GCD block
    private static bool NextIsNinjutsuBlock(List<NinPlannedStep> trace, float after, float weaveLock)
    {
        var next = trace.Where(s => s.Time > after && IsGcd(s.Action)).OrderBy(s => s.Time).FirstOrDefault();
        return next.Action is AID.Raiton or AID.HyoshoRanryu && next.Time - 1.0f - after >= weaveLock - 1e-3f;
    }
}
