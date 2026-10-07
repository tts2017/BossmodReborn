using System;
using System.Collections.Generic;
using BossMod;
using BossMod.Autorotation;

namespace XanTimelineHarness;

// Decision tests for the shared mechanic hint layer (MechanicForecast, WindDown) and for the per-job reactions built on it.
internal static partial class MechanicHintsSelfTest
{
    private static readonly DateTime BaseTime = new(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);
    private const string TestNamespace = "bossmod.external.splatoon.mechanic-hints-test";
    private static readonly List<string> Failures = [];
    private static int Checks;

    private static void Check(bool condition, string name, string message)
    {
        ++Checks;
        if (!condition)
            Failures.Add($"{name}: {message}");
    }

    public static int Run()
    {
        RunForecastTests();
        RunWindDownTests();
        RunJobTests();
        Console.WriteLine($"mechanic_hints checks={Checks} failures={Failures.Count}");
        foreach (var f in Failures)
            Console.WriteLine(f);
        return Failures.Count == 0 ? 0 : 3;
    }

    private static WorldState NewWorld()
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "mechanic-hints-test");
        world.Execute(new WorldState.OpFrameStart(new(BaseTime, 0, 0, 0, 0, 1), default, default, default));
        world.Execute(new WorldState.OpZoneChange(1, 0));
        return world;
    }

    private static void Push(WorldState world, string ns, float lossIn, float returnIn, float forcedMoveIn = float.MaxValue)
        => ExternalMechanicHintProvider.PushSnapshot(new(ns, world.CurrentZone, world.CurrentCFCID, ExternalZoneConfidence.TrustedSplatoonScript, world.FutureTime(1), forcedMoveIn, lossIn, returnIn, float.MaxValue, false), world.CurrentZone, world.CurrentCFCID, world.CurrentTime);

    private static void RunForecastTests()
    {
        var world = NewWorld();
        var hints = new AIHints();
        try
        {
            Push(world, TestNamespace, 5, 40);
            var f = MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null);
            Check(f.TargetLossIn == 5 && f.TargetReturnIn == 40, "external-loss", $"loss={f.TargetLossIn} return={f.TargetReturnIn}");
            Check(MechanicForecast.Build(MechanicHintStrategy.TimelineOnly, world, hints, null).TargetLossIn == float.MaxValue, "external-filtered-timeline", "external hint must not reach TimelineOnly");
            Check(MechanicForecast.Build(MechanicHintStrategy.ForecastOnly, world, hints, null).TargetLossIn == float.MaxValue, "external-filtered-forecast", "external hint must not reach ForecastOnly");
            Check(MechanicForecast.Build(MechanicHintStrategy.Off, world, hints, null) == MechanicForecast.None, "off-is-none", "Off must return None");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            Push(world, ExternalMechanicHintProvider.TimelineNamespace, 6, 30);
            Check(MechanicForecast.Build(MechanicHintStrategy.TimelineOnly, world, hints, null).TargetLossIn == 6, "timeline-source", "timeline namespace must reach TimelineOnly");
            Check(MechanicForecast.Build(MechanicHintStrategy.ForecastOnly, world, hints, null).TargetLossIn == float.MaxValue, "timeline-filtered-forecast", "timeline namespace must not reach ForecastOnly");
            ExternalMechanicHintProvider.ClearNamespace(ExternalMechanicHintProvider.TimelineNamespace);

            Push(world, TestNamespace, 3, 6); // 3 s loss: a dodge, not a downtime
            Check(MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null).TargetLossIn == float.MaxValue, "transient-ignored", "losses shorter than 8.5 s must be ignored");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            Push(world, TestNamespace, 4, float.MaxValue);
            var unknown = MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null);
            Check(unknown.TargetLossIn == 4 && !unknown.ReturnKnown, "unknown-return", "loss with unknown return is kept, return unknown");
            Check(!unknown.ShouldHoldWindow(20, 120, 2.5f), "hold-requires-return", "no hold without a known return");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            Push(world, TestNamespace, 0, 30);
            var now = MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null);
            Check(now.DowntimeNow && !now.ShouldHoldWindow(20, 120, 2.5f), "downtime-now", "during downtime nothing is held");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            // the last seconds of a running downtime: every producer reports loss 0, so the return must survive the long-loss filter
            Push(world, TestNamespace, 0, 4);
            var ending = MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null);
            Check(ending.DowntimeNow && ending.TargetReturnIn == 4, "downtime-ending", $"loss={ending.TargetLossIn} return={ending.TargetReturnIn}");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            Push(world, TestNamespace, 10, 40);
            var hold = MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null);
            Check(hold.ShouldHoldWindow(20, 120, 2.5f), "hold-cut-window", "a 20 s window cut at 10 s with a 120 s recast must be held");
            Check(!hold.ShouldHoldWindow(20, 30, 2.5f), "no-hold-recast-back", "a 30 s recast is back by the 40 s return: use it now");
            Check(!hold.ShouldHoldWindow(11, 120, 2.5f), "no-hold-fits", "a window losing at most one GCD is not held");
            Check(hold.ExpiresDuringLoss(15) && !hold.ExpiresDuringLoss(8) && !hold.ExpiresDuringLoss(45), "expires-during-loss", "15 s left expires inside [10,40]; 8 s runs out before; 45 s survives");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            hints.Disengage = new DisengageForecast(2, 1, 3, 5);
            var fc = MechanicForecast.Build(MechanicHintStrategy.ForecastOnly, world, hints, null);
            Check(fc.ForcedMoveIn == 2 && fc.ForcedMoveFor == 1 && fc.RangeLossIn == 3 && fc.RangeReturnIn == 5, "disengage-forecast", "forecast fields must come from hints.Disengage");
            Check(MechanicForecast.Build(MechanicHintStrategy.TimelineOnly, world, hints, null).ForcedMoveIn == float.MaxValue, "disengage-filtered", "TimelineOnly must ignore hints.Disengage");
        }
        finally
        {
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);
            ExternalMechanicHintProvider.ClearNamespace(ExternalMechanicHintProvider.TimelineNamespace);
        }
    }

    private static partial void RunWindDownTests();
    private static partial void RunJobTests();
}
