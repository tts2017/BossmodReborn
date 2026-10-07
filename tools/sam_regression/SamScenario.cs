namespace SamRegression;

public enum SamOpenerBurst
{
    Normal,
    ZeroSecond
}

public enum SamGcdRoute
{
    GCD208,
    GCD214
}

public enum SamPotionStrategy
{
    None,
    TwoMinuteBurst
}

public enum SamTrueNorthStrategy
{
    Auto,
    None
}

public enum SamHiganbanaStrategy
{
    Auto,
    Delay,
    Force
}

public enum SamTsubameStrategy
{
    Auto,
    Hold,
    Delay,
    Force
}

public enum SamNamikiriStrategy
{
    Auto,
    Hold,
    Delay,
    Force
}

public enum SamMeikyoStrategy
{
    Auto,
    Cooldown,
    HoldOne,
    Delay,
    Force
}

public enum SamTargetPattern
{
    SingleTarget,
    TwoTargets,
    AoeThreePlus,
    SingleToAoeToSingle,
    RangedOnly,
    TargetLost,
    TargetLostReturn
}

public enum SamRaidBuffProfile
{
    None,
    ZeroSecond,
    SixtySecond,
    OneTwentySecond
}

public sealed record SamDowntimeWindow(double Start, double End)
{
    public bool Contains(double time) => time >= Start && time < End;
}

public sealed record SamScenario
{
    public required string Name { get; init; }
    public int Seed { get; init; }
    public double Duration { get; init; } = 360;
    public int Level { get; init; } = 100;
    public SamOpenerBurst OpenerBurst { get; init; } = SamOpenerBurst.Normal;
    public SamGcdRoute GcdRoute { get; init; } = SamGcdRoute.GCD214;
    public SamTargetPattern TargetPattern { get; init; } = SamTargetPattern.SingleTarget;
    public SamRaidBuffProfile RaidBuffProfile { get; init; } = SamRaidBuffProfile.OneTwentySecond;
    public double RaidBuffOffset { get; init; }
    public SamPotionStrategy Potion { get; init; } = SamPotionStrategy.TwoMinuteBurst;
    public SamTrueNorthStrategy TrueNorth { get; init; } = SamTrueNorthStrategy.Auto;
    public SamHiganbanaStrategy Higanbana { get; init; } = SamHiganbanaStrategy.Auto;
    public SamTsubameStrategy Tsubame { get; init; } = SamTsubameStrategy.Auto;
    public SamNamikiriStrategy Namikiri { get; init; } = SamNamikiriStrategy.Auto;
    public SamMeikyoStrategy Meikyo { get; init; } = SamMeikyoStrategy.Auto;
    public IReadOnlyList<SamDowntimeWindow> Downtimes { get; init; } = [];

    public double GcdLength => GcdRoute == SamGcdRoute.GCD208 ? 2.08 : 2.14;
}

public static class SamScenarioCatalog
{
    private const int DefaultScenarioCount = 20_000;

    public static IReadOnlyList<SamScenario> Select(string pattern, int seed)
        => BuildAll(seed).Where(s => s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)).ToList();

    public static IReadOnlyList<SamScenario> BuildAll(int seed)
    {
        var scenarios = new List<SamScenario>();
        var baseScenario = new SamScenario
        {
            Name = "baseline_6m_lv100_gcd214_normal_single",
            Seed = seed,
            Duration = 360,
            Level = 100,
            GcdRoute = SamGcdRoute.GCD214,
            OpenerBurst = SamOpenerBurst.Normal,
            TargetPattern = SamTargetPattern.SingleTarget
        };

        foreach (var duration in new[] { 360, 480, 600, 720 })
            scenarios.Add(baseScenario with { Name = $"dummy_{duration / 60}m", Duration = duration });

        foreach (var opener in Enum.GetValues<SamOpenerBurst>())
        foreach (var route in Enum.GetValues<SamGcdRoute>())
            scenarios.Add(baseScenario with { Name = $"opener_{opener}_{route}", OpenerBurst = opener, GcdRoute = route });

        foreach (var target in Enum.GetValues<SamTargetPattern>())
            scenarios.Add(baseScenario with { Name = $"target_{target}", TargetPattern = target, Duration = target == SamTargetPattern.TargetLostReturn ? 300 : 240 });

        foreach (var length in new[] { 5, 8, 12, 20, 60 })
            scenarios.Add(baseScenario with { Name = $"downtime_{length}s", Downtimes = [new(90, 90 + length)], Duration = 240 });

        scenarios.Add(baseScenario with { Name = "downtime_around_1m_short_loss", Downtimes = [new(58, 66)], Duration = 180 });
        scenarios.Add(baseScenario with { Name = "downtime_before_2m_burst", Downtimes = [new(112, 120)], Duration = 240 });
        scenarios.Add(baseScenario with { Name = "downtime_during_2m_burst", Downtimes = [new(121, 133)], Duration = 240 });
        scenarios.Add(baseScenario with { Name = "p2_197_loss_206_return", Downtimes = [new(197, 206)], Duration = 300 });

        foreach (var level in new[] { 50, 60, 70, 80, 90, 100 })
            scenarios.Add(baseScenario with { Name = $"level_sync_{level}", Level = level, Duration = 240 });

        foreach (var profile in Enum.GetValues<SamRaidBuffProfile>())
            scenarios.Add(baseScenario with { Name = $"raidbuff_{profile}", RaidBuffProfile = profile, Duration = 240 });

        foreach (var offset in new[] { -5, -2, 0, 2, 5, 10 })
            scenarios.Add(baseScenario with { Name = $"raidbuff_offset_{offset:+#;-#;0}", RaidBuffOffset = offset, Duration = 240 });

        foreach (var potion in Enum.GetValues<SamPotionStrategy>())
            scenarios.Add(baseScenario with { Name = $"potion_{potion}", Potion = potion, Duration = 240 });

        foreach (var trueNorth in Enum.GetValues<SamTrueNorthStrategy>())
            scenarios.Add(baseScenario with { Name = $"truenorth_{trueNorth}", TrueNorth = trueNorth, Duration = 180 });

        foreach (var higanbana in Enum.GetValues<SamHiganbanaStrategy>())
            scenarios.Add(baseScenario with { Name = $"higanbana_{higanbana}", Higanbana = higanbana, Duration = 240 });

        foreach (var tsubame in Enum.GetValues<SamTsubameStrategy>())
            scenarios.Add(baseScenario with { Name = $"tsubame_{tsubame}", Tsubame = tsubame, Duration = 240 });

        foreach (var namikiri in Enum.GetValues<SamNamikiriStrategy>())
            scenarios.Add(baseScenario with { Name = $"namikiri_{namikiri}", Namikiri = namikiri, Duration = 240 });

        foreach (var meikyo in Enum.GetValues<SamMeikyoStrategy>())
            scenarios.Add(baseScenario with { Name = $"meikyo_{meikyo}", Meikyo = meikyo, Duration = 240 });

        scenarios.AddRange(BuildSeededMatrix(seed, baseScenario, Math.Max(0, DefaultScenarioCount - scenarios.Count)));
        return scenarios;
    }

    private static IEnumerable<SamScenario> BuildSeededMatrix(int seed, SamScenario baseScenario, int count)
    {
        var rng = new Random(seed);
        var levels = new[] { 50, 60, 70, 80, 90, 100 };
        var durations = new[] { 360, 480, 600, 720 };
        var offsets = new[] { -5, -2, 0, 2, 5, 10 };
        var targetPatterns = Enum.GetValues<SamTargetPattern>();
        var scenarios = new List<SamScenario>();

        for (var i = 0; i < count; ++i)
        {
            var downtimeStart = rng.NextDouble() < 0.45 ? 40 + rng.NextDouble() * 180 : -1;
            var downtimeLength = new[] { 5, 8, 12, 20, 60 }[rng.Next(5)];
            scenarios.Add(baseScenario with
            {
                Name = $"seeded_{i:00000}",
                Seed = seed + i,
                Duration = durations[rng.Next(durations.Length)],
                Level = levels[rng.Next(levels.Length)],
                OpenerBurst = rng.Next(2) == 0 ? SamOpenerBurst.Normal : SamOpenerBurst.ZeroSecond,
                GcdRoute = rng.Next(2) == 0 ? SamGcdRoute.GCD208 : SamGcdRoute.GCD214,
                TargetPattern = targetPatterns[rng.Next(targetPatterns.Length)],
                RaidBuffProfile = (SamRaidBuffProfile)rng.Next(Enum.GetValues<SamRaidBuffProfile>().Length),
                RaidBuffOffset = offsets[rng.Next(offsets.Length)],
                Potion = rng.Next(2) == 0 ? SamPotionStrategy.None : SamPotionStrategy.TwoMinuteBurst,
                TrueNorth = rng.Next(2) == 0 ? SamTrueNorthStrategy.Auto : SamTrueNorthStrategy.None,
                Higanbana = (SamHiganbanaStrategy)rng.Next(Enum.GetValues<SamHiganbanaStrategy>().Length),
                Tsubame = (SamTsubameStrategy)rng.Next(Enum.GetValues<SamTsubameStrategy>().Length),
                Namikiri = (SamNamikiriStrategy)rng.Next(Enum.GetValues<SamNamikiriStrategy>().Length),
                Meikyo = (SamMeikyoStrategy)rng.Next(Enum.GetValues<SamMeikyoStrategy>().Length),
                Downtimes = downtimeStart >= 0 ? [new(downtimeStart, downtimeStart + downtimeLength)] : []
            });
        }

        return scenarios;
    }
}
