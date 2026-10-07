namespace BossMod;

// Data the old fork stored as fields on upstream classes (AIHints, PlanExecution, BossModule), exposed as C# 14 extension members
// so the custom code keeps its syntax (hints.Disengage, PlanExecution.OverdueGraceSeconds, module.StateMachineFromTimeline)
// without editing the upstream types. Per-instance values live in a ConditionalWeakTable; the per-frame reset that AIHints.Clear()
// used to do is done by CustomPlugin.AfterHintsBuilt.
public static class CustomHintsExtensions
{
    private sealed class HintsData
    {
        public DisengageForecast Disengage = DisengageForecast.None;
        public FightTimeEstimate FightRemaining;
    }

    private static readonly ConditionalWeakTable<AIHints, HintsData> _hints = new();
    private static readonly ConditionalWeakTable<BossModule, object> _timelineModules = new();

    extension(AIHints hints)
    {
        // disengages predicted from telegraphed forbidden zones (see DisengageForecaster); filled after hints are gathered
        public DisengageForecast Disengage
        {
            get => _hints.GetValue(hints, static _ => new()).Disengage;
            set => _hints.GetValue(hints, static _ => new()).Disengage = value;
        }

        // how long the fight will still last, estimated from the priority targets' HP (see FightTimeEstimator)
        public FightTimeEstimate FightRemaining
        {
            get => _hints.GetValue(hints, static _ => new()).FightRemaining;
            set => _hints.GetValue(hints, static _ => new()).FightRemaining = value;
        }

        // what AIHints.Clear() reset in the old fork
        public void ResetCustomData()
        {
            var d = _hints.GetValue(hints, static _ => new());
            d.Disengage = DisengageForecast.None;
            d.FightRemaining = default;
        }
    }

    extension(Autorotation.PlanExecution)
    {
        // a state that outlasts its planned duration by more than this no longer predicts when the next transition happens
        public static float OverdueGraceSeconds => 2;
    }

    extension(BossModule module)
    {
        // true when ExternalPlannerTimeline rebuilt the module's state machine from an imported timeline.
        // Needs the BossModule constructor hook (not installed), so currently always false.
        public bool StateMachineFromTimeline => _timelineModules.TryGetValue(module, out _);
    }
}
