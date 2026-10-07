using System;
using System.Globalization;
using System.IO;
using System.Linq;
using BossMod.Autorotation.xan;
using XanNIN = BossMod.Autorotation.xan.Custom.NIN;

namespace XanTimelineHarness;

// NIN_DECISIONS_CSV=<file>: one row per burst lock with the context the selector saw and both simulated values, so the pre-tuned
// rule selector can be fitted against the planner's judgement (and its misses inspected) without touching the rotation.
internal static class NinDecisionLog
{
    private static readonly StreamWriter? Writer = Open();
    private static XanNIN? _module;
    private static NinBurstVariant _lastLocked;

    private static StreamWriter? Open()
    {
        var path = Environment.GetEnvironmentVariable("NIN_DECISIONS_CSV");
        if (string.IsNullOrEmpty(path))
            return null;
        var writer = new StreamWriter(path) { AutoFlush = true };
        writer.WriteLine("zone,duration,time,locked,reason,rf_value,kf_value,rf_ok,kf_ok,even,charges,next_charge,kassatsu_left,kassatsu_in,kunai_in,dokumori_since,dokumori_in,gcd,gcd_length,phantom,raiju,ninki,kazematoi,combo,bunshin,tcj_in,party_left,party_in,loss_in,return_in,gcds_before_dokumori,gcds_before_kunai");
        return writer;
    }

    // NIN_DECISIONS_TRACE=1: also print every change of the locked/tentative variant (with the module's state description)
    private static readonly bool TraceChanges = Environment.GetEnvironmentVariable("NIN_DECISIONS_TRACE") is "1" or "2";
    // NIN_DECISIONS_TRACE=2: also print the planner's simulated sequences for both variants at every lock
    private static readonly bool TracePlans = Environment.GetEnvironmentVariable("NIN_DECISIONS_TRACE") == "2";
    private static string _lastState = "";
    private static NinBurstEvaluation _lastEvaluation;
    private static float _decisionTime;

    public static void Observe(XanNIN module, int zone, float duration, float time)
    {
        if (TraceChanges)
        {
            var state = module.DescribeState();
            var parts = state.Split(' ');
            var head = parts[0] + " " + parts[^1];
            if (head != _lastState)
            {
                Console.WriteLine(FormattableString.Invariant($"nin_burst z={zone} d={duration:f1} t={time:f2} {state}"));
                _lastState = head;
            }
        }
        if (!ReferenceEquals(module, _module))
        {
            _module = module;
            _lastLocked = NinBurstVariant.None;
        }
        var locked = module.LockedBurstVariant;
        // the decision context is refreshed every frame of the decision window; remember when it last changed
        if (!module.DecisionEvaluation.Equals(_lastEvaluation))
        {
            _lastEvaluation = module.DecisionEvaluation;
            _decisionTime = time;
        }
        if (TracePlans && locked != NinBurstVariant.None && _lastLocked == NinBurstVariant.None)
        {
            var planner = new NinBurstPlanner();
            var ctx = module.DecisionContext;
            foreach (var variant in new[] { NinBurstVariant.RaitonFirst, NinBurstVariant.KassatsuFirst })
            {
                var steps = new System.Collections.Generic.List<NinPlannedStep>();
                var value = planner.Simulate(ctx, variant, steps);
                Console.WriteLine(FormattableString.Invariant($"nin_plan z={zone} decided={_decisionTime:f2} locked_at={time:f2} locked={locked} {variant} value={value:f0}: ") + string.Join(' ', steps.Select(st => FormattableString.Invariant($"{_decisionTime + st.Time:f2}:{st.Action}{(st.InKunai ? "*" : "")}"))));
            }
        }
        if (locked != NinBurstVariant.None && _lastLocked == NinBurstVariant.None)
        {
            var e = module.DecisionEvaluation;
            var c = module.DecisionContext;
            static string F(float v) => v >= float.MaxValue / 2 ? "inf" : v.ToString("f2", CultureInfo.InvariantCulture);
            Writer?.WriteLine(string.Join(',',
                zone, F(duration), F(time), locked, e.Reason, F(e.RaitonFirstValue), F(e.KassatsuFirstValue), e.RaitonFirstFeasible ? 1 : 0, e.KassatsuFirstFeasible ? 1 : 0, c.EvenBurst ? 1 : 0,
                c.MudraCharges, F(c.MudraNextCharge), F(c.KassatsuLeft), F(c.KassatsuReadyIn), F(c.KunaiReadyIn), F(c.DokumoriSincePress), F(c.DokumoriReadyIn), F(c.GCD), F(c.GcdLength),
                F(c.PhantomLeft), c.RaijuStacks, c.Ninki, c.Kazematoi, c.ComboStep, c.BunshinStacks, F(c.TenChiJinReadyIn), F(c.PartyBuffLeft), F(c.PartyBuffIn), F(c.TargetLossIn), F(c.TargetReturnIn),
                c.GCDsBeforeDokumori, c.GCDsBeforeKunai));
        }
        _lastLocked = locked;
    }
}
