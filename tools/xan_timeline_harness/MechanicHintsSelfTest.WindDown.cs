using System;
using BossMod;
using BossMod.Autorotation;

namespace XanTimelineHarness;

internal static partial class MechanicHintsSelfTest
{
    private static MechanicForecast Loss(float lossIn, float returnIn)
        => MechanicForecast.None with { Mode = MechanicHintStrategy.All, TargetLossIn = lossIn, TargetReturnIn = returnIn };

    private static WindDownCandidate C(uint id, float[] steps, float readyIn = 0, float recoveredIn = 0) => new(ActionID.MakeSpell((BossMod.MCH.AID)id), steps, readyIn, recoveredIn);

    private static partial void RunWindDownTests()
    {
        // 3 slots before a loss at 7.9 s (GCD 2.5, lock 0.6): starts 0, 2.5, 5.0 finish by 7.4
        var m = Loss(7.9f, 40);
        Check(WindDown.SlotsBeforeLoss(m, 0, 2.5f, 0.6f) == 3, "winddown-slots", $"slots={WindDown.SlotsBeforeLoss(m, 0, 2.5f, 0.6f)}");

        // two singles (600, 900) in three slots: filler first, 600, then 900 in the last slot
        WindDownCandidate[] two = [C(1, [600]), C(2, [900])];
        var plan = WindDown.Plan(two, m, 0, 2.5f, 0.6f, 300);
        Check(plan.Length == 3 && plan[0].Candidate == -1 && plan[1].Candidate == 0 && plan[2].Candidate == 1, "winddown-highest-last", Describe(plan));
        Check(WindDown.SelectGcd(two, m, 0, 2.5f, 0.6f, 300) == -1, "winddown-filler-first", "first slot belongs to the normal rotation");

        // a 3-step chain (500,560,620) vs a 1200 single with only 2 slots: single 1200 + first chain step beats two chain steps; the cut
        // chain goes last (its continuation would otherwise displace the single)
        var m2 = Loss(5.4f, 40);
        WindDownCandidate[] chain = [C(3, [500, 560, 620]), C(4, [1200])];
        var p2 = WindDown.Plan(chain, m2, 0, 2.5f, 0.6f, 380);
        Check(p2.Length == 2 && p2[0].Candidate == 1 && p2[1].Candidate == 0 && p2[1].Steps == 1, "winddown-partial-chain", Describe(p2));

        // recast rule: a candidate whose charge is back only after return + 1 GCD is not used
        WindDownCandidate[] recast = [C(5, [800], recoveredIn: 60)];
        Check(WindDown.SelectGcd(recast, Loss(3.0f, 40), 0, 2.5f, 0.6f, 300) == -1, "winddown-recast-rule", "60 s recast is not back by 42.5 s");

        Check(WindDown.SelectGcd(two, Loss(7.9f, float.MaxValue), 0, 2.5f, 0.6f, 300) == -1, "winddown-requires-return", "unknown return: no wind-down");
        Check(WindDown.SelectGcd(two, Loss(0, 30), 0, 2.5f, 0.6f, 300) == -1, "winddown-downtime-now", "already in downtime: no wind-down");

        // stable over frames: as the loss approaches, the plan keeps the same candidate in the last slot
        var last = -2;
        var flips = 0;
        for (var t = 0f; t < 2.5f; t += 0.05f)
        {
            var pick = WindDown.Plan(two, Loss(7.9f - t, 40 - t), 2.5f - t, 2.5f, 0.6f, 300);
            var tail = pick.Length > 0 ? pick[^1].Candidate : -1;
            if (last != -2 && tail != last)
                ++flips;
            last = tail;
        }
        Check(flips == 0, "winddown-stable-over-frames", $"flips={flips}");

        // oGCD: more eligible oGCDs than weave slots -> the strongest take the slots (one slot: 800, not 450)
        WindDownCandidate[] ogcds = [C(6, [800]), C(7, [450])];
        Check(WindDown.SelectOgcd(ogcds, Loss(2.0f, 40), 2.5f) == 0, "winddown-ogcd-strongest-fit", "one weave slot: the 800 one");
        // exactly as many eligible as weave slots (3) -> lowest first, strongest last
        WindDownCandidate[] three = [C(6, [800]), C(7, [450]), C(8, [600])];
        Check(WindDown.SelectOgcd(three, Loss(4.0f, 40), 2.5f) == 1, "winddown-ogcd-lowest-first", "450 first, 800 last");
        Check(WindDown.SelectOgcd(ogcds, Loss(20f, 40), 2.5f) == -1, "winddown-ogcd-plenty-slots", "plenty of weave slots: normal rotation decides");

        // a chain cut by the loss can only be the last thing before it: DD first, the two chain steps that fit after it
        WindDownCandidate[] cut = [C(9, [660, 760, 860]), C(10, [1000])];
        var p3 = WindDown.Plan(cut, Loss(7.9f, 40), 0, 2.5f, 0.6f, 380);
        Check(p3.Length == 2 && p3[0].Candidate == 1 && p3[1].Candidate == 0 && p3[1].Steps == 2, "winddown-cut-chain-last", Describe(p3));
    }

    private static string Describe(WindDownSlot[] plan) => string.Join(",", Array.ConvertAll(plan, s => $"{s.Candidate}x{s.Steps}"));
}
