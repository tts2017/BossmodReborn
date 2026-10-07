using System;
using System.Collections.Generic;
using System.Reflection;
using BossMod;
using BossMod.Autorotation;
using EncounterTimeline;

namespace XanTimelineHarness;

// XAN_HARNESS_SM_WINDOWS=1: a boss module whose state machine carries only the scenario's target-unavailable windows, as
// DowntimeStart / DowntimeEnd transitions on a fixed clock started at the pull. The rotation then learns about the same windows
// the way a fight with a native state machine tells it (the planner's DowntimeIn / UptimeIn and MechanicForecast's state-machine
// path) instead of through a --target-loss-hints snapshot (the imported-timeline follower's path), so the two can be compared.
internal sealed class WindowStateMachineModule : BossModule
{
    private WindowStateMachineModule(WorldState ws, Actor primary) : base(ws, primary, primary.Position, new ArenaBoundsCircle(30)) { }

    public static WindowStateMachineModule Create(WorldState world, BossModuleManager bossmods, Actor target, RotationModuleManager manager,
        IReadOnlyList<EventTriggerTimelineWindow> windows, DateTime pull)
    {
        var module = new WindowStateMachineModule(world, target);
        typeof(BossModule).GetField(nameof(StateMachine), BindingFlags.Public | BindingFlags.Instance)!.SetValue(module, Build(windows));
        bossmods.ActiveModule = module;
        manager.Planner = new PlanExecution(module, null);
        module.StateMachine.Start(pull);
        return module;
    }

    private static StateMachine Build(IReadOnlyList<EventTriggerTimelineWindow> windows)
    {
        // touching or overlapping windows are one downtime
        List<(float Start, float End)> merged = [];
        foreach (var w in windows)
        {
            if (merged.Count > 0 && w.Start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, w.End));
            else
                merged.Add((w.Start, w.End));
        }

        List<StateMachine.State> states = [];
        var hint = StateMachine.PhaseHint.None;
        var t = 0f;
        uint id = 0;
        foreach (var (start, end) in merged)
        {
            if (end <= 0)
                continue;
            if (start > t)
                Add(start - t, StateMachine.StateHint.DowntimeStart, "Downtime");
            else
                hint = StateMachine.PhaseHint.StartWithDowntime;
            Add(end - Math.Max(t, start), StateMachine.StateHint.DowntimeEnd, "Return");
            t = end;
        }
        var last = new StateMachine.State { ID = ++id, Duration = 10000, Name = "End" };
        if (states.Count > 0)
            states[^1].NextStates = [last];
        states.Add(last);
        return new([new(states[0], "Windows") { Hint = hint }]);

        void Add(float duration, StateMachine.StateHint endHint, string name)
        {
            var state = new StateMachine.State { ID = ++id, Duration = Math.Max(duration, 0.01f), Name = name, EndHint = endHint };
            state.Update = elapsed => elapsed >= state.Duration ? 0 : -1;
            if (states.Count > 0)
                states[^1].NextStates = [state];
            states.Add(state);
        }
    }
}
