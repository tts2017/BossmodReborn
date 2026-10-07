using System.Globalization;

namespace MchRegression;

public sealed class MchState
{
    public double Time { get; set; }
    public double GCDReadyAt { get; set; }
    public double AnimationLockUntil { get; set; }
    public double GCDLength { get; set; } = MchActionModel.StandardGcd;
    public double CombatTimer { get; set; }
    public bool Targetable { get; set; } = true;
    public int TargetCount { get; set; } = 1;
    public int RangedSplashTargetCount { get; set; } = 1;
    public int ChainSawTargetCount { get; set; } = 1;
    public int ConeTargetCount { get; set; } = 1;
    public bool InCombat { get; set; } = true;
    public int Level { get; set; } = 100;
    public int Heat { get; set; }
    public int Battery { get; set; }
    public MchAction ComboLastMove { get; set; } = MchAction.None;
    public double OverheatedLeft { get; set; }
    public double HyperchargedLeft { get; set; }
    public double ReassembleLeft { get; set; }
    public double WildfireLeft { get; set; }
    public double ExcavatorReadyLeft { get; set; }
    public double FullMetalMachinistLeft { get; set; }
    public double FlamethrowerLeft { get; set; }
    public double QueenActiveLeft { get; set; }
    public Dictionary<MchAction, double> Cooldowns { get; } = [];
    public Dictionary<MchAction, int> Charges { get; } = [];
    public Dictionary<MchAction, double> ChargeFractions { get; } = [];
    public double PotionReadyAt { get; set; }
    public MchAction LastGCD { get; set; } = MchAction.None;
    public MchAction LastOGCD { get; set; } = MchAction.None;
    public List<MchTimelineEvent> TimelineEvents { get; } = [];
    public List<MchActionUse> LastActions { get; } = [];
    public MchScenario Scenario { get; set; } = MchScenario.Default;
    public int HeatGcdsInCurrentOverheat { get; set; }
    public int HeatGcdsInCurrentWildfire { get; set; }
    public int OgcdsSinceLastGcd { get; set; }
    public double LastGcdTime { get; set; }
    public double LastOgcdTime { get; set; }
    public double LastTargetableTime { get; set; }
    public double LastOgcdOpportunity { get; set; }
    public bool ReassembleArmed { get; set; }
    public int PrePullPotionUses { get; set; }
    public int CombatPotionUses { get; set; }
    public bool TargetOverrideActive { get; set; }
    public bool QueenTargetFixed { get; set; }
    public Random Random { get; set; } = new(1);

    public bool Overheated => OverheatedLeft > 0;
    public bool Hypercharged => HyperchargedLeft > 0;
    public bool HasAnyTarget => Targetable && TargetCount > 0;
    public bool HasConeTarget => Targetable && ConeTargetCount > 0;
    public bool HasSplashTarget => Targetable && RangedSplashTargetCount > 0;
    public bool HasChainSawTarget => Targetable && ChainSawTargetCount > 0;
    public double Gcd => Math.Max(0, GCDReadyAt - Time);
    public double AnimLock => Math.Max(0, AnimationLockUntil - Time);

    public bool CooldownReady(MchAction action) => ReadyIn(action) <= 0.0001;

    public double ReadyIn(MchAction action)
        => Math.Max(0, Cooldowns.TryGetValue(action, out var readyAt) ? readyAt - Time : 0);

    public int ChargeCount(MchAction action)
    {
        InitializeCharges(action);
        return Charges[action];
    }

    public double ChargeCapIn(MchAction action)
    {
        InitializeCharges(action);
        var definition = MchActionModel.Definition(action);
        if (definition.MaxCharges <= 1)
            return ReadyIn(action);
        var missing = definition.MaxCharges - Charges[action];
        if (missing <= 0)
            return 0;
        return Math.Max(0, ReadyIn(action) + (missing - 1) * definition.Recast);
    }

    public void InitializeCharges(MchAction action)
    {
        if (!MchActionModel.Definitions.TryGetValue(action, out var definition))
            return;
        if (definition.MaxCharges <= 1)
        {
            Charges.TryAdd(action, CooldownReady(action) ? 1 : 0);
            ChargeFractions.TryAdd(action, 0);
            return;
        }
        Charges.TryAdd(action, definition.MaxCharges);
        ChargeFractions.TryAdd(action, 0);
    }

    public void AdvanceTo(double newTime)
    {
        if (newTime < Time)
            return;

        var delta = newTime - Time;
        Time = newTime;
        CombatTimer = Math.Max(0, CombatTimer + delta);
        OverheatedLeft = Math.Max(0, OverheatedLeft - delta);
        HyperchargedLeft = Math.Max(0, HyperchargedLeft - delta);
        ReassembleLeft = Math.Max(0, ReassembleLeft - delta);
        WildfireLeft = Math.Max(0, WildfireLeft - delta);
        ExcavatorReadyLeft = Math.Max(0, ExcavatorReadyLeft - delta);
        FullMetalMachinistLeft = Math.Max(0, FullMetalMachinistLeft - delta);
        FlamethrowerLeft = Math.Max(0, FlamethrowerLeft - delta);
        QueenActiveLeft = Math.Max(0, QueenActiveLeft - delta);
        RegenerateCharges(delta);
    }

    public void AddAction(MchAction action, string note = "")
    {
        var use = new MchActionUse(Time, action, TargetCount, Heat, Battery, OverheatedLeft, HyperchargedLeft, WildfireLeft, ReassembleLeft, ExcavatorReadyLeft, FullMetalMachinistLeft, CooldownSummary(), ChargeSummary(), note);
        LastActions.Add(use);
        if (LastActions.Count > 5000)
            LastActions.RemoveRange(0, LastActions.Count - 5000);
    }

    public string Summary()
        => string.Create(CultureInfo.InvariantCulture, $"t={Time:F1} lvl={Level} targetable={Targetable} targets={TargetCount}/{ConeTargetCount}/{RangedSplashTargetCount}/{ChainSawTargetCount} heat={Heat} battery={Battery} oh={OverheatedLeft:F1} wf={WildfireLeft:F1} fmf={FullMetalMachinistLeft:F1} exc={ExcavatorReadyLeft:F1}");

    public string CooldownSummary()
    {
        var tracked = new[] { MchAction.Drill, MchAction.Bioblaster, MchAction.AirAnchor, MchAction.ChainSaw, MchAction.BarrelStabilizer, MchAction.Wildfire, MchAction.Hypercharge };
        return string.Join(';', tracked.Select(a => $"{MchActionModel.Name(a)}:{ReadyIn(a):F1}"));
    }

    public string ChargeSummary()
    {
        var tracked = new[] { MchAction.Drill, MchAction.Bioblaster, MchAction.GaussRound, MchAction.Ricochet };
        return string.Join(';', tracked.Select(a => $"{MchActionModel.Name(a)}:{ChargeCount(a)}/{MchActionModel.Definition(a).MaxCharges}"));
    }

    public IEnumerable<MchActionUse> LastActionWindow(int count)
        => LastActions.Skip(Math.Max(0, LastActions.Count - count));

    public MchState CloneForScenario(MchScenario scenario)
    {
        var clone = new MchState
        {
            Time = Time,
            GCDReadyAt = GCDReadyAt,
            AnimationLockUntil = AnimationLockUntil,
            GCDLength = GCDLength,
            CombatTimer = CombatTimer,
            Targetable = Targetable,
            TargetCount = TargetCount,
            RangedSplashTargetCount = RangedSplashTargetCount,
            ChainSawTargetCount = ChainSawTargetCount,
            ConeTargetCount = ConeTargetCount,
            InCombat = InCombat,
            Level = Level,
            Heat = Heat,
            Battery = Battery,
            ComboLastMove = ComboLastMove,
            OverheatedLeft = OverheatedLeft,
            HyperchargedLeft = HyperchargedLeft,
            ReassembleLeft = ReassembleLeft,
            WildfireLeft = WildfireLeft,
            ExcavatorReadyLeft = ExcavatorReadyLeft,
            FullMetalMachinistLeft = FullMetalMachinistLeft,
            FlamethrowerLeft = FlamethrowerLeft,
            QueenActiveLeft = QueenActiveLeft,
            PotionReadyAt = PotionReadyAt,
            LastGCD = LastGCD,
            LastOGCD = LastOGCD,
            Scenario = scenario,
            TargetOverrideActive = TargetOverrideActive,
            QueenTargetFixed = QueenTargetFixed,
            Random = new Random(scenario.Seed)
        };
        foreach (var (key, value) in Cooldowns)
            clone.Cooldowns[key] = value;
        foreach (var (key, value) in Charges)
            clone.Charges[key] = value;
        foreach (var (key, value) in ChargeFractions)
            clone.ChargeFractions[key] = value;
        return clone;
    }

    private void RegenerateCharges(double delta)
    {
        foreach (var action in new[] { MchAction.Drill, MchAction.Bioblaster, MchAction.GaussRound, MchAction.Ricochet })
        {
            InitializeCharges(action);
            var definition = MchActionModel.Definition(action);
            if (Level < definition.UnlockLevel || definition.MaxCharges <= 1 || Charges[action] >= definition.MaxCharges)
                continue;

            ChargeFractions[action] += delta / definition.Recast;
            while (ChargeFractions[action] >= 1 && Charges[action] < definition.MaxCharges)
            {
                ChargeFractions[action] -= 1;
                Charges[action]++;
            }
            if (Charges[action] >= definition.MaxCharges)
                ChargeFractions[action] = 0;
        }
    }
}

public sealed record MchActionUse(
    double Time,
    MchAction Action,
    int TargetCount,
    int Heat,
    int Battery,
    double OverheatLeft,
    double HyperchargedLeft,
    double WildfireLeft,
    double ReassembleLeft,
    double ExcavatorLeft,
    double FmfLeft,
    string Cooldowns,
    string Charges,
    string Note);

public sealed record MchTimelineEvent(double Time, MchTimelineEventKind Kind, int TargetCount, bool Targetable, string Note);

public enum MchTimelineEventKind
{
    Targets,
    Downtime,
    Uptime,
    RaidBuff,
    DancingMadWindow,
    TargetOverride
}
