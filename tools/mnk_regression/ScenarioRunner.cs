namespace MnkRegression;

public sealed class ScenarioRunner
{
    public RegressionResult Run(IEnumerable<ScenarioDefinition> scenarios)
        => new BattleEmulator().Run(scenarios.Select(ScenarioCatalog.ToBattleScenario));
}
