namespace MnkRegression;

public sealed record MnkStateSnapshot
{
    public required double Time { get; init; }
    public required double CombatTimer { get; init; }
    public required bool InCombat { get; init; }
    public required bool Targetable { get; init; }
    public required bool HaveTarget { get; init; }
    public required bool CanMelee { get; init; }
    public required int TargetCount { get; init; }
    public required int NumAOETargets { get; init; }
    public required int NumMeleeAOETargets { get; init; }
    public required EncounterHintMode EncounterHint { get; init; }
    public required bool LookAwayActive { get; init; }
    public required bool ForbiddenZoneActive { get; init; }
    public required bool ThunderclapSafe { get; init; }
    public string? SelectedGCD { get; init; }
    public IReadOnlyList<string> SelectedOGCDs { get; init; } = [];
    public required double GCDReadyIn { get; init; }
    public required double AnimationLock { get; init; }
    public required int Chakra { get; init; }
    public double ChakraProgress { get; init; }
    public required string BeastChakra { get; init; }
    public required string Nadi { get; init; }
    public required string CurrentForm { get; init; }
    public int OpoFury { get; init; }
    public int RaptorFury { get; init; }
    public int CoeurlFury { get; init; }
    public required double FormlessFistLeft { get; init; }
    public required double PerfectBalanceLeft { get; init; }
    public required int PerfectBalanceStacks { get; init; }
    public required double PerfectBalanceCharges { get; init; }
    public required double BlitzLeft { get; init; }
    public required double RiddleOfFireLeft { get; init; }
    public required double BrotherhoodLeft { get; init; }
    public double RiddleOfWindLeft { get; init; }
    public double PotionLeft { get; init; }
    public required double RiddleOfEarthLeft { get; init; }
    public required double EarthsReplyLeft { get; init; }
    public required double WindsReplyLeft { get; init; }
    public required double FiresReplyLeft { get; init; }
    public IReadOnlyList<PredictedDamageSnapshot> PredictedDamage { get; init; } = [];
    public IReadOnlyList<HardFailCode> HardFailCandidates { get; init; } = [];
    public string RiddleOfEarthReason { get; init; } = "";

    public static MnkStateSnapshot From(BattleState state)
    {
        var gauge = state.PlayerGauge;
        return new()
        {
            Time = Math.Round(state.Time, 3),
            CombatTimer = Math.Round(state.CombatTimer, 3),
            InCombat = state.InCombat,
            Targetable = state.Targetable,
            HaveTarget = state.HaveTarget,
            CanMelee = state.CanMelee,
            TargetCount = state.TargetCount,
            NumAOETargets = state.NumAOETargets,
            NumMeleeAOETargets = state.NumMeleeAOETargets,
            EncounterHint = state.EncounterHint,
            LookAwayActive = state.LookAwayActive,
            ForbiddenZoneActive = state.ForbiddenZoneActive,
            ThunderclapSafe = state.ThunderclapSafe,
            SelectedGCD = state.LastGCDAction,
            SelectedOGCDs = state.LastOGCDActions.ToList(),
            GCDReadyIn = Math.Round(state.GCDReadyIn, 3),
            AnimationLock = Math.Round(state.AnimationLock, 3),
            Chakra = gauge.Chakra,
            ChakraProgress = Math.Round(gauge.ChakraProgress, 3),
            BeastChakra = gauge.BeastText,
            Nadi = gauge.NadiText,
            CurrentForm = gauge.CurrentForm,
            OpoFury = gauge.OpoFury,
            RaptorFury = gauge.RaptorFury,
            CoeurlFury = gauge.CoeurlFury,
            FormlessFistLeft = Math.Round(gauge.FormlessFistLeft, 3),
            PerfectBalanceLeft = Math.Round(gauge.PerfectBalanceLeft, 3),
            PerfectBalanceStacks = gauge.PerfectBalanceStacks,
            PerfectBalanceCharges = Math.Round(state.Cooldowns.PerfectBalance.Charges, 3),
            BlitzLeft = Math.Round(gauge.BlitzLeft, 3),
            RiddleOfFireLeft = Math.Round(gauge.RiddleOfFireLeft, 3),
            BrotherhoodLeft = Math.Round(gauge.BrotherhoodLeft, 3),
            RiddleOfWindLeft = Math.Round(gauge.RiddleOfWindLeft, 3),
            PotionLeft = Math.Round(gauge.PotionLeft, 3),
            RiddleOfEarthLeft = Math.Round(gauge.RiddleOfEarthLeft, 3),
            EarthsReplyLeft = Math.Round(gauge.EarthsReplyLeft, 3),
            WindsReplyLeft = Math.Round(gauge.WindsReplyLeft, 3),
            FiresReplyLeft = Math.Round(gauge.FiresReplyLeft, 3),
            PredictedDamage = state.PredictedDamage.ToList(),
            HardFailCandidates = state.HardFailCandidates.ToList(),
            RiddleOfEarthReason = state.RiddleOfEarthReason
        };
    }
}
