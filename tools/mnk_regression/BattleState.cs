namespace MnkRegression;

public sealed record ActionRecord(
    double Time,
    string Action,
    bool IsGcd,
    IReadOnlyList<HardFailCode> HardFails,
    double BasePotency = 0,
    double EffectivePotency = 0,
    int TargetCount = 0,
    bool UnderRiddleOfFire = false,
    bool UnderBrotherhood = false,
    bool UnderPotion = false);

public sealed class PlayerGauge
{
    public int Chakra { get; set; }
    public double ChakraProgress { get; set; }
    public List<string> BeastChakra { get; } = [];
    public bool LunarNadi { get; set; }
    public bool SolarNadi { get; set; }
    public string CurrentForm { get; set; } = "Opo";
    public int OpoFury { get; set; }
    public int RaptorFury { get; set; }
    public int CoeurlFury { get; set; }
    public string PerfectBalancePlan { get; set; } = "Lunar";
    public double OpoOpoFormLeft { get; set; }
    public double RaptorFormLeft { get; set; }
    public double CoeurlFormLeft { get; set; }
    public double FormlessFistLeft { get; set; }
    public double DisciplinedFistLeft { get; set; }
    public double LeadenFistLeft { get; set; }
    public double PerfectBalanceLeft { get; set; }
    public int PerfectBalanceStacks { get; set; }
    public double RiddleOfFireLeft { get; set; }
    public double BrotherhoodLeft { get; set; }
    public double RiddleOfWindLeft { get; set; }
    public double PotionLeft { get; set; }
    public double RiddleOfEarthLeft { get; set; }
    public double EarthsReplyLeft { get; set; }
    public double WindsReplyLeft { get; set; }
    public double FiresReplyLeft { get; set; }
    public double BlitzLeft { get; set; }

    public string NadiText => LunarNadi && SolarNadi ? "Both" : LunarNadi ? "Lunar" : SolarNadi ? "Solar" : "None";
    public string BeastText => BeastChakra.Count == 0 ? "None" : string.Join("+", BeastChakra);
}

public sealed class CooldownState
{
    public double Charges { get; set; }
    public double ReadyIn { get; set; }
}

public sealed class BattleCooldowns
{
    public CooldownState PerfectBalance { get; } = new() { Charges = 2 };
    public CooldownState RiddleOfFire { get; } = new();
    public CooldownState Brotherhood { get; } = new();
    public CooldownState RiddleOfWind { get; } = new();
    public CooldownState RiddleOfEarth { get; } = new();
    public CooldownState Potion { get; } = new();
    public CooldownState Thunderclap { get; } = new() { Charges = 3 };
    public CooldownState TrueNorth { get; } = new() { Charges = 2 };
}

public sealed class BattleState
{
    public double Time { get; set; }
    public double CombatTimer { get; set; }
    public bool InCombat { get; set; } = true;
    public bool Targetable { get; set; } = true;
    public bool HaveTarget { get; set; } = true;
    public bool CanMelee { get; set; } = true;
    public int TargetCount { get; set; } = 1;
    public int NumAOETargets { get; set; } = 1;
    public int NumMeleeAOETargets { get; set; } = 1;
    public EncounterHintMode EncounterHint { get; set; } = EncounterHintMode.Boss;
    public bool LookAwayActive { get; set; }
    public bool ForbiddenZoneActive { get; set; }
    public bool SafeMeleeAvailable { get; set; } = true;
    public bool ThunderclapSafe { get; set; } = true;
    public List<PredictedDamageSnapshot> PredictedDamage { get; } = [];
    public double? EstimatedDowntimeStart { get; set; }
    public double? EstimatedPhaseEnd { get; set; }
    public double? EstimatedFightEnd { get; set; }
    public double GCDReadyIn { get; set; }
    public double AnimationLock { get; set; }
    public double AutoAttackReadyIn { get; set; }
    public double GcdIdleTime { get; set; }
    public string? LastGCDAction { get; set; }
    public List<string> LastOGCDActions { get; } = [];
    public List<ActionRecord> ActionHistory { get; } = [];
    public PlayerGauge PlayerGauge { get; } = new();
    public BattleCooldowns Cooldowns { get; } = new();
    public Dictionary<string, double> Statuses { get; } = [];
    public List<HardFailCode> HardFailCandidates { get; } = [];
    public string RiddleOfEarthReason { get; set; } = "";

    public void ResetFrameActions()
    {
        LastGCDAction = null;
        LastOGCDActions.Clear();
        HardFailCandidates.Clear();
    }
}
