namespace NinRegression;

public enum NinAction
{
    None,
    SpinningEdge,
    GustSlash,
    AeolianEdge,
    ArmorCrush,
    DeathBlossom,
    HakkeMujinsatsu,
    FumaShuriken,
    Raiton,
    Katon,
    Hyoton,
    HyoshoRanryu,
    GokaMekkyaku,
    Suiton,
    Huton,
    Ninjutsu,
    Ten,
    Chi,
    Jin,
    Kassatsu,
    Mug,
    Dokumori,
    TrickAttack,
    KunaisBane,
    TenChiJin,
    TCJFuma,
    TCJRaiton,
    TCJKaton,
    TCJHyoton,
    TCJSuiton,
    TCJDoton,
    Meisui,
    Bunshin,
    PhantomKamaitachi,
    ForkedRaiju,
    FleetingRaiju,
    Bhavacakra,
    ZeshoMeppo,
    HellfrogMedium,
    DeathfrogMedium,
    DreamWithinADream,
    Assassinate,
    TenriJindo,
    Potion,
    TrueNorth,
    Doton,
    RabbitMedium
}

public enum NinActionKind
{
    None,
    Gcd,
    Ogcd,
    Item
}

public enum NinTargetKind
{
    None,
    Primary,
    RangedAoe,
    Player
}

public enum PendingNinjutsu
{
    None,
    Raiton,
    Katon,
    HyoshoRanryu,
    GokaMekkyaku,
    Suiton,
    Huton
}

public enum BurstStyle
{
    Normal,
    UltimateZeroSecond
}

public enum PotionStrategy
{
    None,
    EvenBurst
}

public enum RotationStrategy
{
    Normal,
    BasicComboOnly
}

public enum HardFailRule
{
    GcdFreeze,
    ReturnKilledGcd,
    RabbitOrMudraFailure,
    KassatsuFailure,
    KunaiMugSyncFailure,
    TenChiJinMeisuiFailure,
    DotonForbidden,
    MudraStackFailure,
    NinkiFailure,
    BasicComboOnlyFailure,
    TargetNullSafetyFailure
}

public enum SoftRegressionRule
{
    KunaiEndAlignDrift,
    MugDelay,
    KassatsuCarry,
    RaitonFirstIncomplete,
    PhantomExpired,
    RaijuExpired,
    TenriExpired,
    NinkiNearOvercap,
    ShadowWalkerWasted,
    TrueNorthEarly,
    AoeStWeakAction
}

public sealed record NinActionLog
{
    public double Time { get; init; }
    public NinAction Action { get; init; }
    public NinTargetKind TargetKind { get; init; }
    public NinActionKind Kind { get; init; }
    public string Reason { get; init; } = "";
    public NinAction ComboState { get; init; }
    public int Ninki { get; init; }
    public int MudraCharges { get; init; }
    public PendingNinjutsu PendingNinjutsu { get; init; }
    public double Kassatsu { get; init; }
    public bool KassatsuQueuedThisFrame { get; init; }
    public double ShadowWalker { get; init; }
    public double TargetMugLeft { get; init; }
    public double TargetTrickLeft { get; init; }
    public double TenChiJin { get; init; }
    public double Meisui { get; init; }
    public int Raiju { get; init; }
    public double Phantom { get; init; }
    public bool Targetable { get; init; }
    public bool TargetExists { get; init; }
    public int AoeTargetCount { get; init; }
    public int RangedAoeTargetCount { get; init; }
}

public sealed record NinFailure
{
    public required string ScenarioName { get; init; }
    public int Seed { get; init; }
    public double Time { get; init; }
    public required string StateSummary { get; init; }
    public IReadOnlyList<NinActionLog> Last20Actions { get; init; } = [];
    public required string ExpectedInvariant { get; init; }
    public required string ActualAction { get; init; }
    public required string SuspectedFunctionName { get; init; }
    public required string Severity { get; init; }
    public string Rule { get; init; } = "";
}
