namespace DrgRegression;

public sealed class DrgSimulator
{
    private const double SimulationStep = 0.1;
    private const double OgcdLock = 0.65;
    private const double DotDuration = 24.0;
    private const double PowerSurgeDuration = 30.0;

    public DrgScenarioResult Run(DrgScenario scenario)
    {
        var state = CreateInitialState(scenario);
        var result = new DrgScenarioResult { Scenario = scenario };

        while (state.Time <= scenario.Duration)
        {
            TickState(scenario, state, state.Time);

            if (state.Time + 0.0001 >= state.GcdReadyAt)
            {
                var gcd = ChooseGcd(scenario, state);
                if (gcd == DrgAction.None)
                {
                    state.Actions.Add(Log(scenario, state, DrgActionKind.None, DrgAction.None, "none", "no gcd candidate"));
                    if (state.Targetable && state.TargetCount > 0)
                        result.HardFails.Add(new(state.Time, "GcdStop", "targetable frames should select a GCD", "None"));
                }
                else
                {
                    ApplyGcd(scenario, state, gcd);
                    state.Actions.Add(Log(scenario, state, DrgActionKind.GCD, gcd, TargetFor(gcd, state), GcdReason(gcd)));
                }

                for (var slot = 0; slot < 2; ++slot)
                {
                    var ogcd = ChooseOgcd(state);
                    if (ogcd == DrgAction.None)
                        continue;

                    ApplyOgcd(state, ogcd);
                    state.Actions.Add(Log(scenario, state, DrgActionKind.OGCD, ogcd, TargetFor(ogcd, state), OgcdReason(ogcd)));
                }

                state.GcdReadyAt = state.Time + scenario.GcdLength;
            }

            EvaluateStep(state, result);

            var next = Math.Min(state.Time + SimulationStep, scenario.Duration + SimulationStep);
            if (state.GcdReadyAt > state.Time)
                next = Math.Min(next, state.GcdReadyAt);
            if (state.AnimationLockUntil > state.Time)
                next = Math.Min(next, state.AnimationLockUntil);
            if (next <= state.Time + 0.00001)
                next = state.Time + SimulationStep;
            state.AdvanceTo(next);
        }

        result.Actions.AddRange(state.Actions);
        EvaluateFinal(state, result);
        return result;
    }

    private static DrgState CreateInitialState(DrgScenario scenario)
        => new()
        {
            Level = scenario.Level,
            TargetCount = scenario.InitialTargetCount,
            Targetable = true,
            Melee = scenario.Kind != DrgScenarioKind.RangedStart,
            Combo = scenario.InitialCombo,
            PowerSurge = scenario.InitialPowerSurge,
            DotLeft = [scenario.InitialDot0, scenario.InitialDot1],
            Focus = scenario.InitialFocus,
            Lotd = scenario.InitialLotd
        };

    private static void TickState(DrgScenario scenario, DrgState state, double now)
    {
        var delta = Math.Max(0, now - state.Time);
        Decay(ref state.PowerSurge, delta);
        Decay(ref state.DraconianFire, delta);
        Decay(ref state.LanceCharge, delta);
        Decay(ref state.BattleLitany, delta);
        Decay(ref state.LifeSurgeBuff, delta);
        Decay(ref state.Lotd, delta);
        Decay(ref state.DiveReady, delta);
        Decay(ref state.NastrondReady, delta);
        Decay(ref state.DragonsFlight, delta);
        Decay(ref state.StarcrossReady, delta);
        Decay(ref state.LanceChargeCd, delta);
        Decay(ref state.BattleLitanyCd, delta);
        Decay(ref state.GeirskogulCd, delta);
        Decay(ref state.HighJumpCd, delta);
        Decay(ref state.DragonfireDiveCd, delta);
        Decay(ref state.StardiverCd, delta);
        Decay(ref state.WyrmwindCd, delta);
        Decay(ref state.LifeSurgeCd, delta);

        for (var i = 0; i < state.DotLeft.Length; ++i)
            Decay(ref state.DotLeft[i], delta);

        if (state.LifeSurgeCharges < 2 && state.LifeSurgeCd <= 0)
        {
            ++state.LifeSurgeCharges;
            state.LifeSurgeCd = state.LifeSurgeCharges >= 2 ? 0 : 40;
        }

        ApplyTimeline(scenario, state, now);
    }

    private static void ApplyTimeline(DrgScenario scenario, DrgState state, double time)
    {
        state.Targetable = true;
        state.Melee = true;
        state.TargetCount = scenario.InitialTargetCount;

        switch (scenario.Kind)
        {
            case DrgScenarioKind.SingleTarget:
                state.TargetCount = 1;
                break;
            case DrgScenarioKind.TwoTargetDots:
                state.TargetCount = 2;
                break;
            case DrgScenarioKind.Aoe:
                state.TargetCount = Math.Max(3, scenario.InitialTargetCount);
                break;
            case DrgScenarioKind.TargetSwitch:
                state.TargetCount = ((int)(time / 20) % 2) == 0 ? 1 : 4;
                break;
            case DrgScenarioKind.TargetLost:
                state.Targetable = time < 45 || time >= 50 && time < 95 || time >= 100;
                if (!state.Targetable)
                    state.TargetCount = 0;
                break;
            case DrgScenarioKind.RangedStart:
                state.TargetCount = 1;
                state.Melee = time >= 7.5;
                break;
            case DrgScenarioKind.Burst:
                state.TargetCount = scenario.InitialTargetCount;
                break;
        }
    }

    private static DrgAction ChooseGcd(DrgScenario scenario, DrgState state)
    {
        if (!state.Targetable || state.TargetCount <= 0)
            return DrgAction.None;

        if (!state.Melee)
            return IsUnlocked(DrgAction.PiercingTalon, state.Level) ? DrgAction.PiercingTalon : DrgAction.None;

        if (state.TargetCount >= 3 && IsUnlocked(DrgAction.DoomSpike, state.Level))
        {
            return state.Combo switch
            {
                DrgAction.SonicThrust when IsUnlocked(DrgAction.CoerthanTorment, state.Level) => DrgAction.CoerthanTorment,
                DrgAction.DoomSpike or DrgAction.DraconianFury when IsUnlocked(DrgAction.SonicThrust, state.Level) => DrgAction.SonicThrust,
                _ => state.DraconianFire > scenario.GcdLength && IsUnlocked(DrgAction.DraconianFury, state.Level) ? DrgAction.DraconianFury : DrgAction.DoomSpike
            };
        }

        return state.Combo switch
        {
            DrgAction.WheelingThrust or DrgAction.FangAndClaw when IsUnlocked(DrgAction.Drakesbane, state.Level) => DrgAction.Drakesbane,
            DrgAction.ChaosThrust or DrgAction.ChaoticSpring when IsUnlocked(DrgAction.WheelingThrust, state.Level) => DrgAction.WheelingThrust,
            DrgAction.FullThrust or DrgAction.HeavensThrust when IsUnlocked(DrgAction.FangAndClaw, state.Level) => DrgAction.FangAndClaw,
            DrgAction.Disembowel or DrgAction.SpiralBlow => BestChaoticSpring(state.Level),
            DrgAction.VorpalThrust or DrgAction.LanceBarrage => BestHeavensThrust(state.Level),
            DrgAction.TrueThrust or DrgAction.RaidenThrust => ShouldStartDotCombo(scenario, state) ? BestDisembowel(state.Level) : BestVorpal(state.Level),
            _ => state.DraconianFire > scenario.GcdLength && IsUnlocked(DrgAction.RaidenThrust, state.Level) ? DrgAction.RaidenThrust : DrgAction.TrueThrust
        };
    }

    private static bool ShouldStartDotCombo(DrgScenario scenario, DrgState state)
    {
        if (state.PowerSurge < 10)
            return true;

        if (state.TargetCount == 2)
        {
            var lowestDot = state.DotLeft.Min();
            return state.GcdReadyAt + scenario.GcdLength * 7 >= lowestDot;
        }

        return false;
    }

    private static DrgAction ChooseOgcd(DrgState state)
    {
        if (!state.Targetable || state.TargetCount <= 0 || state.AnimationLockUntil > state.Time)
            return DrgAction.None;

        if (state.PowerSurge > 0 && state.LanceChargeCd <= 0 && IsUnlocked(DrgAction.LanceCharge, state.Level))
            return DrgAction.LanceCharge;
        if (state.LanceCharge > 0 && state.BattleLitanyCd <= 0 && IsUnlocked(DrgAction.BattleLitany, state.Level))
            return DrgAction.BattleLitany;
        if (state.LanceCharge > 0 && state.GeirskogulCd <= 0 && IsUnlocked(DrgAction.Geirskogul, state.Level))
            return DrgAction.Geirskogul;
        if (state.Focus >= 2 && state.WyrmwindCd <= 0 && IsUnlocked(DrgAction.WyrmwindThrust, state.Level))
            return DrgAction.WyrmwindThrust;
        if (state.LifeSurgeCharges >= 2 && NextGcdIsLifeSurgeTarget(state) && IsUnlocked(DrgAction.LifeSurge, state.Level))
            return DrgAction.LifeSurge;
        if (state.HighJumpCd <= 0 && IsUnlocked(DrgAction.HighJump, state.Level))
            return DrgAction.HighJump;
        if (state.DiveReady > 0 && IsUnlocked(DrgAction.MirageDive, state.Level))
            return DrgAction.MirageDive;
        if (state.LanceCharge > 0 && state.DragonfireDiveCd <= 0 && IsUnlocked(DrgAction.DragonfireDive, state.Level))
            return DrgAction.DragonfireDive;
        if (state.NastrondReady > 0 && IsUnlocked(DrgAction.Nastrond, state.Level))
            return DrgAction.Nastrond;
        if (state.Lotd > 0 && state.StardiverCd <= 0 && IsUnlocked(DrgAction.Stardiver, state.Level))
            return DrgAction.Stardiver;
        if (state.StarcrossReady > 0 && IsUnlocked(DrgAction.Starcross, state.Level))
            return DrgAction.Starcross;
        if (state.DragonsFlight > 0 && IsUnlocked(DrgAction.RiseOfTheDragon, state.Level))
            return DrgAction.RiseOfTheDragon;

        return DrgAction.None;
    }

    private static bool NextGcdIsLifeSurgeTarget(DrgState state)
        => state.Combo is DrgAction.VorpalThrust or DrgAction.LanceBarrage or DrgAction.FullThrust or DrgAction.HeavensThrust or DrgAction.WheelingThrust or DrgAction.FangAndClaw or DrgAction.SonicThrust;

    private static void ApplyGcd(DrgScenario scenario, DrgState state, DrgAction action)
    {
        switch (action)
        {
            case DrgAction.Disembowel:
            case DrgAction.SpiralBlow:
            case DrgAction.SonicThrust:
                state.PowerSurge = PowerSurgeDuration;
                break;
            case DrgAction.ChaosThrust:
            case DrgAction.ChaoticSpring:
                state.DotLeft[DotTarget(state)] = DotDuration;
                break;
            case DrgAction.FangAndClaw:
            case DrgAction.WheelingThrust:
            case DrgAction.CoerthanTorment:
                state.DraconianFire = 30;
                break;
            case DrgAction.RaidenThrust:
            case DrgAction.DraconianFury:
                state.DraconianFire = 0;
                state.Focus = Math.Min(2, state.Focus + 1);
                break;
        }

        if (state.LifeSurgeBuff > 0)
            state.LifeSurgeBuff = 0;

        state.Combo = action;
    }

    private static int DotTarget(DrgState state)
        => state.TargetCount == 2 && state.DotLeft[1] < state.DotLeft[0] ? 1 : 0;

    private static void ApplyOgcd(DrgState state, DrgAction action)
    {
        switch (action)
        {
            case DrgAction.LanceCharge:
                state.LanceCharge = 20;
                state.LanceChargeCd = 60;
                break;
            case DrgAction.BattleLitany:
                state.BattleLitany = 20;
                state.BattleLitanyCd = 120;
                break;
            case DrgAction.Geirskogul:
                state.Lotd = 20;
                state.NastrondReady = 20;
                state.GeirskogulCd = 60;
                break;
            case DrgAction.HighJump:
                state.DiveReady = 15;
                state.HighJumpCd = 30;
                break;
            case DrgAction.MirageDive:
                state.DiveReady = 0;
                break;
            case DrgAction.DragonfireDive:
                state.DragonsFlight = 15;
                state.DragonfireDiveCd = 120;
                break;
            case DrgAction.RiseOfTheDragon:
                state.DragonsFlight = 0;
                break;
            case DrgAction.Nastrond:
                state.NastrondReady = Math.Max(0, state.NastrondReady - 2);
                break;
            case DrgAction.Stardiver:
                state.StarcrossReady = 20;
                state.StardiverCd = 30;
                break;
            case DrgAction.Starcross:
                state.StarcrossReady = 0;
                break;
            case DrgAction.WyrmwindThrust:
                state.Focus = 0;
                state.WyrmwindCd = 10;
                break;
            case DrgAction.LifeSurge:
                if (state.LifeSurgeCharges > 0)
                {
                    --state.LifeSurgeCharges;
                    state.LifeSurgeBuff = 5;
                    if (state.LifeSurgeCd <= 0)
                        state.LifeSurgeCd = 40;
                }
                break;
        }

        state.AnimationLockUntil = state.Time + (action == DrgAction.Stardiver ? 1.5 : OgcdLock);
    }

    private static void EvaluateStep(DrgState state, DrgScenarioResult result)
    {
        if (state.Focus > 2)
            result.HardFails.Add(new(state.Time, "FocusOvercap", "Focus <= 2", state.Focus.ToString()));

        if (state.LifeSurgeCharges > 2)
            result.HardFails.Add(new(state.Time, "LifeSurgeOvercap", "Life Surge charges <= 2", state.LifeSurgeCharges.ToString()));

        if (state.TargetCount == 2 && state.Targetable && state.Time > 40 && state.DotLeft.Any(dot => dot <= 0))
            AddSoftOnce(result, state.Time, "TwoTargetDotGap", "both targets should have Chaotic/Chaos DoT after ramp", $"dot0={state.DotLeft[0]:f1}, dot1={state.DotLeft[1]:f1}");
    }

    private static void EvaluateFinal(DrgState state, DrgScenarioResult result)
    {
        if (result.Actions.Count == 0)
            result.HardFails.Add(new(state.Time, "NoActions", "scenario should produce actions", "0"));
    }

    private static void AddSoftOnce(DrgScenarioResult result, double time, string rule, string expected, string actual)
    {
        if (result.SoftFindings.Any(f => f.Rule == rule))
            return;
        result.SoftFindings.Add(new(time, rule, expected, actual));
    }

    public static bool IsUnlocked(DrgAction action, int level)
        => action switch
        {
            DrgAction.TrueThrust => level >= 1,
            DrgAction.VorpalThrust => level >= 4,
            DrgAction.LifeSurge => level >= 6,
            DrgAction.PiercingTalon => level >= 15,
            DrgAction.Disembowel => level >= 18,
            DrgAction.FullThrust => level >= 26,
            DrgAction.LanceCharge => level >= 30,
            DrgAction.HighJump => level >= 74,
            DrgAction.MirageDive => level >= 68,
            DrgAction.DoomSpike => level >= 40,
            DrgAction.WingedGlide => level >= 45,
            DrgAction.ChaosThrust => level >= 50,
            DrgAction.DragonfireDive => level >= 50,
            DrgAction.BattleLitany => level >= 52,
            DrgAction.FangAndClaw => level >= 56,
            DrgAction.WheelingThrust => level >= 58,
            DrgAction.Geirskogul => level >= 60,
            DrgAction.SonicThrust => level >= 62,
            DrgAction.Drakesbane => level >= 64,
            DrgAction.Nastrond => level >= 70,
            DrgAction.CoerthanTorment => level >= 72,
            DrgAction.RaidenThrust => level >= 76,
            DrgAction.Stardiver => level >= 80,
            DrgAction.DraconianFury => level >= 82,
            DrgAction.ChaoticSpring => level >= 86,
            DrgAction.HeavensThrust => level >= 86,
            DrgAction.WyrmwindThrust => level >= 90,
            DrgAction.RiseOfTheDragon => level >= 92,
            DrgAction.LanceBarrage => level >= 96,
            DrgAction.SpiralBlow => level >= 96,
            DrgAction.Starcross => level >= 100,
            _ => false
        };

    private static DrgAction BestDisembowel(int level) => IsUnlocked(DrgAction.SpiralBlow, level) ? DrgAction.SpiralBlow : DrgAction.Disembowel;
    private static DrgAction BestVorpal(int level) => IsUnlocked(DrgAction.LanceBarrage, level) ? DrgAction.LanceBarrage : DrgAction.VorpalThrust;
    private static DrgAction BestHeavensThrust(int level) => IsUnlocked(DrgAction.HeavensThrust, level) ? DrgAction.HeavensThrust : DrgAction.FullThrust;
    private static DrgAction BestChaoticSpring(int level) => IsUnlocked(DrgAction.ChaoticSpring, level) ? DrgAction.ChaoticSpring : DrgAction.ChaosThrust;

    private static DrgActionLog Log(DrgScenario scenario, DrgState state, DrgActionKind kind, DrgAction action, string target, string reason)
        => new(state.Time, kind, action, target, reason, state.Snapshot(scenario));

    private static string TargetFor(DrgAction action, DrgState state)
        => action is DrgAction.LanceCharge or DrgAction.BattleLitany or DrgAction.LifeSurge ? "self" :
            action is DrgAction.ChaosThrust or DrgAction.ChaoticSpring ? $"target{DotTarget(state)}" :
            state.TargetCount >= 3 ? "aoe" : "target0";

    private static string GcdReason(DrgAction action)
        => action switch
        {
            DrgAction.PiercingTalon => "ranged fallback",
            DrgAction.SpiralBlow or DrgAction.Disembowel => "Power Surge or two-target DoT path",
            DrgAction.DraconianFury or DrgAction.RaidenThrust => "Draconian Fire spender",
            _ => "combo"
        };

    private static string OgcdReason(DrgAction action)
        => action switch
        {
            DrgAction.WyrmwindThrust => "Focus spend",
            DrgAction.LifeSurge => "high-value GCD setup or charge cap",
            DrgAction.Geirskogul => "enter LotD",
            _ => "cooldown"
        };

    private static void Decay(ref double value, double delta)
    {
        if (value > 0)
            value = Math.Max(0, value - delta);
    }

    private sealed class DrgState
    {
        public int Level;
        public double Time;
        public double GcdReadyAt;
        public double AnimationLockUntil;
        public int TargetCount = 1;
        public bool Targetable = true;
        public bool Melee = true;
        public DrgAction Combo;
        public double PowerSurge;
        public double[] DotLeft = [0, 0];
        public double DraconianFire;
        public int Focus;
        public double Lotd;
        public double LanceCharge;
        public double BattleLitany;
        public double LifeSurgeBuff;
        public int LifeSurgeCharges = 2;
        public double DiveReady;
        public double NastrondReady;
        public double DragonsFlight;
        public double StarcrossReady;
        public double LanceChargeCd;
        public double BattleLitanyCd;
        public double GeirskogulCd;
        public double HighJumpCd;
        public double DragonfireDiveCd;
        public double StardiverCd;
        public double WyrmwindCd;
        public double LifeSurgeCd;
        public List<DrgActionLog> Actions { get; } = [];

        public void AdvanceTo(double next) => Time = next;

        public DrgStateSnapshot Snapshot(DrgScenario scenario) => new()
        {
            Time = Time,
            GcdReadyIn = Math.Max(0, GcdReadyAt - Time),
            Combo = Combo,
            TargetCount = TargetCount,
            Targetable = Targetable,
            Melee = Melee,
            PowerSurge = PowerSurge,
            Dot0 = DotLeft[0],
            Dot1 = DotLeft[1],
            DraconianFire = DraconianFire,
            Focus = Focus,
            Lotd = Lotd,
            LanceCharge = LanceCharge,
            BattleLitany = BattleLitany,
            LifeSurgeBuff = LifeSurgeBuff,
            LifeSurgeCharges = LifeSurgeCharges,
            NastrondReady = NastrondReady,
            DragonsFlight = DragonsFlight,
            StarcrossReady = StarcrossReady
        };
    }
}
