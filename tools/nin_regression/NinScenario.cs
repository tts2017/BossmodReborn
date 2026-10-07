namespace NinRegression;

public enum NinScenarioCategory
{
    BasicSingleTarget,
    Opener,
    EvenBurst,
    KunaiDrift,
    Mudra,
    Kassatsu,
    TCJMeisui,
    Aoe,
    Downtime,
    BasicComboOnly,
    Fuzz
}

public enum NinScenarioEventType
{
    TargetLost,
    TargetReturned,
    PrimaryTargetNull,
    BestRangedAoeNull,
    AoeTargets,
    RangedAoeTargets,
    Ninki,
    MudraCharges,
    MugDrift,
    KunaiDrift,
    KassatsuReady,
    TenChiJinReady,
    MeisuiReady,
    ManualRaiton,
    PendingNinjutsu,
    Hidden,
    ShadowWalker,
    Raiju,
    PhantomKamaitachi,
    TenriJindo,
    RotationMode
}

public sealed record NinScenarioEvent
{
    public required NinScenarioEventType Type { get; init; }
    public double Time { get; init; }
    public double End { get; init; }
    public double Value { get; init; }
    public RotationStrategy? RotationMode { get; init; }
    public PendingNinjutsu PendingNinjutsu { get; init; } = PendingNinjutsu.None;
}

public sealed record NinScenario
{
    public required string Name { get; init; }
    public required NinScenarioCategory Category { get; init; }
    public int Seed { get; init; }
    public double Duration { get; init; } = 360;
    public double GcdLength { get; init; } = 2.12;
    public int Level { get; init; } = 100;
    public BurstStyle BurstStyle { get; init; } = BurstStyle.Normal;
    public PotionStrategy PotionStrategy { get; init; } = PotionStrategy.None;
    public RotationStrategy RotationStrategy { get; init; } = RotationStrategy.Normal;
    public bool CountdownSuiton { get; init; }
    public bool Hidden { get; init; } = true;
    public bool TargetExists { get; init; } = true;
    public bool Targetable { get; init; } = true;
    public bool PrimaryTargetNull { get; init; }
    public bool BestRangedAoeTargetNull { get; init; }
    public int NumAoeTargets { get; init; } = 1;
    public int NumRangedAoeTargets { get; init; } = 1;
    public int InitialNinki { get; init; }
    public int InitialKazematoi { get; init; }
    public int InitialMudraCharges { get; init; } = 2;
    public PendingNinjutsu InitialPendingNinjutsu { get; init; } = PendingNinjutsu.None;
    public double InitialMugReadyIn { get; init; }
    public double InitialKunaiReadyIn { get; init; }
    public double InitialKassatsuReadyIn { get; init; }
    public double InitialTenChiJinReadyIn { get; init; }
    public double InitialMeisuiReadyIn { get; init; }
    public double InitialTargetMugLeft { get; init; }
    public double InitialTargetTrickLeft { get; init; }
    public double InitialShadowWalker { get; init; }
    public int InitialRaiju { get; init; }
    public double InitialPhantomKamaitachi { get; init; }
    public double InitialTenriJindo { get; init; }
    public IReadOnlyList<NinScenarioEvent> Events { get; init; } = [];
}

public sealed record NinScenarioResult
{
    public required NinScenario Scenario { get; init; }
    public List<NinActionLog> Actions { get; init; } = [];
    public List<NinFailure> HardFailures { get; init; } = [];
    public List<NinFailure> SoftRegressions { get; init; } = [];
    public List<string> CoverageGaps { get; init; } = [];
    public bool Passed => HardFailures.Count == 0;
}

public sealed record NinRegressionSummary
{
    public string ToolVersion { get; init; } = "nin-regression-v1";
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Scenarios { get; init; }
    public int Passed { get; init; }
    public int HardFail { get; init; }
    public int SoftRegression { get; init; }
    public int CoverageGap { get; init; }
}

public sealed record NinRegressionOutput
{
    public NinRegressionSummary Summary { get; init; } = new();
    public List<NinScenarioResult> Results { get; init; } = [];
}
