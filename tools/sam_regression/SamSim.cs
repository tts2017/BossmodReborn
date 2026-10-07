namespace SamRegression;

public enum SamActionKind
{
    GCD,
    OGCD,
    Potion,
    None
}

public enum SamAction
{
    None,
    Hakaze,
    Gyofu,
    Jinpu,
    Shifu,
    Yukikaze,
    Gekko,
    Kasha,
    Fuga,
    Fuko,
    Mangetsu,
    Oka,
    Enpi,
    Higanbana,
    TenkaGoken,
    MidareSetsugekka,
    TendoGoken,
    TendoSetsugekka,
    KaeshiGoken,
    KaeshiSetsugekka,
    TendoKaeshiGoken,
    TendoKaeshiSetsugekka,
    MeikyoShisui,
    Ikishoten,
    OgiNamikiri,
    KaeshiNamikiri,
    HissatsuShinten,
    HissatsuKyuten,
    HissatsuGuren,
    HissatsuSenei,
    Zanshin,
    Shoha,
    Hagakure,
    TrueNorth,
    Potion
}

public enum SamRepeat
{
    None,
    Goken,
    Setsugekka,
    TendoGoken,
    TendoSetsugekka
}

[Flags]
public enum SamSen
{
    None = 0,
    Setsu = 1,
    Getsu = 2,
    Ka = 4
}

public sealed record SamActionLog(double Time, SamActionKind Kind, SamAction Action, string Target, string Reason, SamStateSnapshot State);

public sealed record SamStateSnapshot
{
    public double Time { get; init; }
    public double GcdReadyIn { get; init; }
    public SamAction Combo { get; init; }
    public int Kenki { get; init; }
    public int Meditation { get; init; }
    public SamSen Sen { get; init; }
    public int MeikyoStacks { get; init; }
    public int MeikyoCharges { get; init; }
    public double DamageBuff { get; init; }
    public double HasteBuff { get; init; }
    public double Dot { get; init; }
    public double OgiReady { get; init; }
    public double KaeshiNamikiriReady { get; init; }
    public double TsubameReady { get; init; }
    public SamRepeat TsubameAction { get; init; }
    public double TendoReady { get; init; }
    public double ZanshinReady { get; init; }
    public bool Targetable { get; init; }
    public bool Melee { get; init; }
    public bool RangedOnly { get; init; }
    public int EnemyCount { get; init; }
    public double RaidBuffsIn { get; init; }
    public double RaidBuffsLeft { get; init; }
}

public sealed class SamState
{
    public double Time;
    public double GcdReadyAt;
    public SamAction Combo = SamAction.None;
    public int Kenki;
    public int Meditation;
    public SamSen Sen;
    public double DamageBuff;
    public double HasteBuff;
    public double HiganbanaDot;
    public double MeikyoLeft;
    public int MeikyoStacks;
    public int MeikyoCharges = 2;
    public double MeikyoNextChargeAt = double.PositiveInfinity;
    public double IkishotenCd;
    public double SeneiGurenCd;
    public double TsubameLeft;
    public SamRepeat TsubameAction;
    public double OgiLeft;
    public double KaeshiNamikiriLeft;
    public double TendoLeft;
    public double ZanshinLeft;
    public double PotionLeft;
    public double PotionCd;
    public bool PotionUsed;
    public bool Targetable = true;
    public bool Melee = true;
    public bool RangedOnly;
    public int EnemyCount = 1;
    public double LastGcdAt = -100;
    public SamAction LastGcd = SamAction.None;
    public int SameInvalidActions;
    public double DamageBuffMissingSince = -1;
    public double HasteBuffMissingSince = -1;
    public double DotMissingSince = -1;
    public List<SamActionLog> Actions { get; } = [];

    public int SenCount
    {
        get
        {
            var count = 0;
            if (Sen.HasFlag(SamSen.Setsu))
                count++;
            if (Sen.HasFlag(SamSen.Getsu))
                count++;
            if (Sen.HasFlag(SamSen.Ka))
                count++;
            return count;
        }
    }

    public SamStateSnapshot Snapshot(SamScenario scenario) => new()
    {
        Time = Time,
        GcdReadyIn = Math.Max(0, GcdReadyAt - Time),
        Combo = Combo,
        Kenki = Kenki,
        Meditation = Meditation,
        Sen = Sen,
        MeikyoStacks = MeikyoStacks,
        MeikyoCharges = MeikyoCharges,
        DamageBuff = DamageBuff,
        HasteBuff = HasteBuff,
        Dot = HiganbanaDot,
        OgiReady = OgiLeft,
        KaeshiNamikiriReady = KaeshiNamikiriLeft,
        TsubameReady = TsubameLeft,
        TsubameAction = TsubameAction,
        TendoReady = TendoLeft,
        ZanshinReady = ZanshinLeft,
        Targetable = Targetable,
        Melee = Melee,
        RangedOnly = RangedOnly,
        EnemyCount = EnemyCount,
        RaidBuffsIn = SamTimeline.RaidBuffsIn(scenario, Time),
        RaidBuffsLeft = SamTimeline.RaidBuffsLeft(scenario, Time)
    };
}

public sealed class SamSim
{
    // Optional external policy (engine evaluation): built per scenario; called with slot -1 for the GCD and 0 / 1 for the weave slots;
    // returns the action (None = nothing) or null for the built-in policy. Actions go through ApplyGcd / ApplyOgcd.
    public static Func<SamScenario, Func<SamState, int, SamAction?>?>? PolicyFactory;

    public SamScenarioResult Run(SamScenario scenario)
    {
        var state = new SamState();
        var policy = PolicyFactory?.Invoke(scenario);
        var result = new SamScenarioResult { Scenario = scenario };

        while (state.Time <= scenario.Duration)
        {
            TickState(scenario, state, state.Time);
            if (state.Time + 0.001 >= state.GcdReadyAt)
            {
                var gcd = policy?.Invoke(state, -1) ?? ChooseGcd(scenario, state);
                if (gcd == SamAction.None)
                {
                    state.SameInvalidActions++;
                    state.Actions.Add(Log(scenario, state, SamActionKind.None, SamAction.None, "none", "no gcd candidate"));
                }
                else
                {
                    state.SameInvalidActions = 0;
                    ApplyGcd(scenario, state, gcd);
                    state.Actions.Add(Log(scenario, state, SamActionKind.GCD, gcd, TargetFor(gcd, state), GcdReason(gcd)));
                }

                for (var slot = 0; slot < 2; ++slot)
                {
                    var ogcd = policy?.Invoke(state, slot) ?? ChooseOgcd(scenario, state, slot);
                    if (ogcd == SamAction.None)
                        continue;

                    ApplyOgcd(scenario, state, ogcd);
                    state.Actions.Add(Log(scenario, state, ogcd == SamAction.Potion ? SamActionKind.Potion : SamActionKind.OGCD, ogcd, TargetFor(ogcd, state), OgcdReason(ogcd)));
                }

                state.GcdReadyAt = state.Time + scenario.GcdLength;
            }

            SamChecks.EvaluateStep(scenario, state, result);
            state.Time = Math.Min(scenario.Duration + 0.001, state.GcdReadyAt);
        }

        result.Actions.AddRange(state.Actions);
        SamChecks.EvaluateFinal(scenario, state, result);
        return result;
    }

    private static SamActionLog Log(SamScenario scenario, SamState state, SamActionKind kind, SamAction action, string target, string reason)
        => new(state.Time, kind, action, target, reason, state.Snapshot(scenario));

    private static void TickState(SamScenario scenario, SamState state, double now)
    {
        var delta = Math.Max(0, now - state.Time);
        Decay(ref state.DamageBuff, delta);
        Decay(ref state.HasteBuff, delta);
        Decay(ref state.HiganbanaDot, delta);
        Decay(ref state.MeikyoLeft, delta);
        Decay(ref state.IkishotenCd, delta);
        Decay(ref state.SeneiGurenCd, delta);
        Decay(ref state.TsubameLeft, delta);
        Decay(ref state.OgiLeft, delta);
        Decay(ref state.KaeshiNamikiriLeft, delta);
        Decay(ref state.TendoLeft, delta);
        Decay(ref state.ZanshinLeft, delta);
        Decay(ref state.PotionLeft, delta);
        Decay(ref state.PotionCd, delta);

        if (state.MeikyoLeft <= 0)
            state.MeikyoStacks = 0;

        if (state.TsubameLeft <= 0)
            state.TsubameAction = SamRepeat.None;

        while (state.MeikyoCharges < 2 && state.MeikyoNextChargeAt <= now)
        {
            state.MeikyoCharges++;
            state.MeikyoNextChargeAt += 55;
        }

        if (state.MeikyoCharges >= 2)
            state.MeikyoNextChargeAt = double.PositiveInfinity;

        ApplyTargets(scenario, state, now);
    }

    private static void ApplyTargets(SamScenario scenario, SamState state, double now)
    {
        var downtime = scenario.Downtimes.Any(d => d.Contains(now));
        state.Targetable = !downtime;
        state.Melee = !downtime;
        state.RangedOnly = false;
        state.EnemyCount = scenario.TargetPattern switch
        {
            SamTargetPattern.TwoTargets => 2,
            SamTargetPattern.AoeThreePlus => 4,
            SamTargetPattern.SingleToAoeToSingle => now is >= 75 and < 150 ? 4 : 1,
            _ => 1
        };

        switch (scenario.TargetPattern)
        {
            case SamTargetPattern.RangedOnly:
                state.Targetable = true;
                state.Melee = false;
                state.RangedOnly = true;
                break;
            case SamTargetPattern.TargetLost:
                if (now >= 60)
                {
                    state.Targetable = false;
                    state.Melee = false;
                }
                break;
            case SamTargetPattern.TargetLostReturn:
                if (now is >= 60 and < 80)
                {
                    state.Targetable = false;
                    state.Melee = false;
                }
                break;
        }
    }

    private static void Decay(ref double value, double delta)
    {
        if (value > 0)
            value = Math.Max(0, value - delta);
    }

    private static SamAction ChooseGcd(SamScenario scenario, SamState state)
    {
        if (!state.Targetable)
            return SamAction.None;

        if (!state.Melee)
            return Unlocked(scenario, SamAction.Enpi) ? SamAction.Enpi : SamAction.None;

        var fast208OpenerAction = ChooseFast208OpenerGcd(scenario, state);
        if (fast208OpenerAction != SamAction.None)
            return fast208OpenerAction;

        var fast208BurstAction = ChooseFast208BurstGcd(scenario, state);
        if (fast208BurstAction != SamAction.None)
            return fast208BurstAction;

        if (Unlocked(scenario, SamAction.Hagakure) && ShouldUseHagakureForRecovery(scenario, state))
            return SamAction.Hagakure;

        if (state.KaeshiNamikiriLeft > 0 && ShouldUseNamikiri(scenario, state, repeat: true))
            return SamAction.KaeshiNamikiri;

        if (state.TsubameLeft > 0 && state.TsubameAction != SamRepeat.None && ShouldUseTsubame(scenario, state))
            return state.TsubameAction switch
            {
                SamRepeat.Goken => SamAction.KaeshiGoken,
                SamRepeat.Setsugekka => SamAction.KaeshiSetsugekka,
                SamRepeat.TendoGoken => SamAction.TendoKaeshiGoken,
                SamRepeat.TendoSetsugekka => SamAction.TendoKaeshiSetsugekka,
                _ => SamAction.None
            };

        if (CanUseHiganbana(scenario, state))
            return SamAction.Higanbana;

        if (state.OgiLeft > 0 && ShouldUseNamikiri(scenario, state, repeat: false))
            return SamAction.OgiNamikiri;

        if (state.SenCount == 3)
            return state.TendoLeft > 0 && Unlocked(scenario, SamAction.TendoSetsugekka) ? SamAction.TendoSetsugekka : SamAction.MidareSetsugekka;

        if (state.SenCount == 2 && state.EnemyCount >= 3)
            return state.TendoLeft > 0 && Unlocked(scenario, SamAction.TendoGoken) ? SamAction.TendoGoken : SamAction.TenkaGoken;

        if (state.MeikyoStacks > 0)
            return ChooseMeikyoGcd(scenario, state);

        return ChooseComboGcd(scenario, state);
    }

    private static SamAction ChooseFast208OpenerGcd(SamScenario scenario, SamState state)
    {
        if (scenario.GcdRoute != SamGcdRoute.GCD208 || scenario.Level < 100 || scenario.TargetPattern != SamTargetPattern.SingleTarget || state.Time >= 35)
            return SamAction.None;

        var index = state.Actions.Count(a => a.Kind == SamActionKind.GCD);
        var sequence = new[]
        {
            SamAction.Gekko,
            SamAction.Kasha,
            SamAction.Yukikaze,
            SamAction.TendoSetsugekka,
            SamAction.TendoKaeshiSetsugekka,
            SamAction.Gekko,
            SamAction.Higanbana,
            SamAction.OgiNamikiri,
            SamAction.KaeshiNamikiri,
            SamAction.Kasha,
            SamAction.Gekko,
            SamAction.Gyofu,
            SamAction.Yukikaze,
            SamAction.TendoSetsugekka,
            SamAction.TendoKaeshiSetsugekka
        };

        if (index >= sequence.Length)
            return SamAction.None;

        var action = sequence[index];
        if (action == SamAction.Higanbana && scenario.Higanbana == SamHiganbanaStrategy.Delay)
            return SamAction.None;
        if (action is SamAction.OgiNamikiri or SamAction.KaeshiNamikiri && scenario.Namikiri == SamNamikiriStrategy.Delay)
            return SamAction.None;
        if (action is SamAction.TendoKaeshiSetsugekka or SamAction.TendoKaeshiGoken && scenario.Tsubame == SamTsubameStrategy.Delay)
            return SamAction.None;

        return Unlocked(scenario, action) ? action : SamAction.None;
    }

    private static SamAction ChooseFast208BurstGcd(SamScenario scenario, SamState state)
    {
        if (!ShouldUseFast208ReferenceSequence(scenario))
            return SamAction.None;

        if (state.Time >= 56 && state.Time < 92)
            return PickFast208SequenceAction(state, 56,
            [
                SamAction.KaeshiSetsugekka,
                SamAction.TendoSetsugekka,
                SamAction.Gekko,
                SamAction.Higanbana,
                SamAction.TendoKaeshiSetsugekka,
                SamAction.Kasha,
                SamAction.Gekko,
                SamAction.Gyofu,
                SamAction.Yukikaze,
                SamAction.MidareSetsugekka
            ]);

        if (state.Time >= 116 && state.Time < 150)
            return PickFast208SequenceAction(state, 116,
            [
                SamAction.KaeshiSetsugekka,
                SamAction.TendoSetsugekka,
                SamAction.TendoKaeshiSetsugekka,
                SamAction.Gekko,
                SamAction.Higanbana,
                SamAction.OgiNamikiri,
                SamAction.KaeshiNamikiri,
                SamAction.Kasha,
                SamAction.Gekko,
                SamAction.Gyofu,
                SamAction.Yukikaze,
                SamAction.MidareSetsugekka
            ]);

        return SamAction.None;
    }

    private static bool ShouldUseFast208ReferenceSequence(SamScenario scenario)
        => scenario.GcdRoute == SamGcdRoute.GCD208
            && scenario.Level >= 100
            && scenario.OpenerBurst == SamOpenerBurst.Normal
            && scenario.TargetPattern == SamTargetPattern.SingleTarget
            && scenario.Higanbana == SamHiganbanaStrategy.Auto
            && scenario.Tsubame == SamTsubameStrategy.Auto
            && scenario.Namikiri == SamNamikiriStrategy.Auto
            && scenario.Meikyo == SamMeikyoStrategy.Auto
            && scenario.Downtimes.Count == 0;

    private static SamAction PickFast208SequenceAction(SamState state, double windowStart, IReadOnlyList<SamAction> sequence)
    {
        var index = state.Actions.Count(a => a.Kind == SamActionKind.GCD && a.Time >= windowStart);
        return index < sequence.Count ? sequence[index] : SamAction.None;
    }

    private static SamAction ChooseComboGcd(SamScenario scenario, SamState state)
    {
        if (state.EnemyCount >= 3 && Unlocked(scenario, SamAction.Fuga))
        {
            if (state.Combo is SamAction.Fuga or SamAction.Fuko)
            {
                if (!state.Sen.HasFlag(SamSen.Getsu) && Unlocked(scenario, SamAction.Mangetsu))
                    return SamAction.Mangetsu;
                if (!state.Sen.HasFlag(SamSen.Ka) && Unlocked(scenario, SamAction.Oka))
                    return SamAction.Oka;
                return state.DamageBuff <= state.HasteBuff && Unlocked(scenario, SamAction.Mangetsu) ? SamAction.Mangetsu : SamAction.Oka;
            }

            return Unlocked(scenario, SamAction.Fuko) ? SamAction.Fuko : SamAction.Fuga;
        }

        return state.Combo switch
        {
            SamAction.Jinpu when Unlocked(scenario, SamAction.Gekko) && !state.Sen.HasFlag(SamSen.Getsu) => SamAction.Gekko,
            SamAction.Shifu when Unlocked(scenario, SamAction.Kasha) && !state.Sen.HasFlag(SamSen.Ka) => SamAction.Kasha,
            SamAction.Hakaze or SamAction.Gyofu => ChooseHakazeBranch(scenario, state),
            _ => Unlocked(scenario, SamAction.Gyofu) ? SamAction.Gyofu : SamAction.Hakaze
        };
    }

    private static SamAction ChooseHakazeBranch(SamScenario scenario, SamState state)
    {
        if (Unlocked(scenario, SamAction.Yukikaze) && !state.Sen.HasFlag(SamSen.Setsu))
            return SamAction.Yukikaze;
        if (Unlocked(scenario, SamAction.Jinpu) && (!state.Sen.HasFlag(SamSen.Getsu) || state.DamageBuff < 8))
            return SamAction.Jinpu;
        if (Unlocked(scenario, SamAction.Shifu) && (!state.Sen.HasFlag(SamSen.Ka) || state.HasteBuff < 8))
            return SamAction.Shifu;
        return Unlocked(scenario, SamAction.Yukikaze) ? SamAction.Yukikaze : SamAction.Hakaze;
    }

    private static SamAction ChooseMeikyoGcd(SamScenario scenario, SamState state)
    {
        if (state.EnemyCount >= 3)
        {
            if (!state.Sen.HasFlag(SamSen.Getsu) && Unlocked(scenario, SamAction.Mangetsu))
                return SamAction.Mangetsu;
            if (!state.Sen.HasFlag(SamSen.Ka) && Unlocked(scenario, SamAction.Oka))
                return SamAction.Oka;
            return Unlocked(scenario, SamAction.Mangetsu) ? SamAction.Mangetsu : SamAction.None;
        }

        if (!state.Sen.HasFlag(SamSen.Setsu) && Unlocked(scenario, SamAction.Yukikaze))
            return SamAction.Yukikaze;
        if (!state.Sen.HasFlag(SamSen.Getsu) && Unlocked(scenario, SamAction.Gekko))
            return SamAction.Gekko;
        if (!state.Sen.HasFlag(SamSen.Ka) && Unlocked(scenario, SamAction.Kasha))
            return SamAction.Kasha;
        return SamAction.None;
    }

    private static bool CanUseHiganbana(SamScenario scenario, SamState state)
    {
        if (!Unlocked(scenario, SamAction.Higanbana) || scenario.Higanbana == SamHiganbanaStrategy.Delay || state.SenCount != 1)
            return false;
        if (scenario.Higanbana == SamHiganbanaStrategy.Force)
            return true;
        return state.HiganbanaDot <= 2.0 || state.HiganbanaDot <= 10 && SamTimeline.RaidBuffsLeft(scenario, state.Time) > 0;
    }

    private static bool ShouldUseNamikiri(SamScenario scenario, SamState state, bool repeat)
    {
        if (scenario.Namikiri == SamNamikiriStrategy.Delay)
            return false;
        if (scenario.Namikiri == SamNamikiriStrategy.Force)
            return true;
        var raid = SamTimeline.RaidBuffsLeft(scenario, state.Time) > 0;
        var expiring = (repeat ? state.KaeshiNamikiriLeft : state.OgiLeft) <= scenario.GcdLength * 2;
        return scenario.Namikiri == SamNamikiriStrategy.Auto ? raid || expiring || SamTimeline.DowntimeSoon(scenario, state.Time, 6) : raid || expiring;
    }

    private static bool ShouldUseTsubame(SamScenario scenario, SamState state)
    {
        if (scenario.Tsubame == SamTsubameStrategy.Delay)
            return false;
        if (scenario.Tsubame == SamTsubameStrategy.Force)
            return true;
        var raid = SamTimeline.RaidBuffsLeft(scenario, state.Time) > 0;
        var expiring = state.TsubameLeft <= scenario.GcdLength * 2;
        return scenario.Tsubame == SamTsubameStrategy.Auto ? raid || expiring || SamTimeline.DowntimeSoon(scenario, state.Time, 6) : raid || expiring;
    }

    private static bool ShouldUseHagakureForRecovery(SamScenario scenario, SamState state)
    {
        if (state.SenCount == 0 || state.SenCount == 1 && state.HiganbanaDot <= scenario.GcdLength * 4)
            return false;
        if (state.SenCount == 3 && (state.TsubameLeft > 0 || state.OgiLeft > 0 || state.KaeshiNamikiriLeft > 0))
            return false;
        var burstIn = Math.Min(SamTimeline.NextOneMinuteBurstIn(scenario, state.Time), SamTimeline.NextTwoMinuteBurstIn(scenario, state.Time));
        return burstIn is > 0 and <= 10 && state.SenCount != 3 && state.MeikyoCharges > 0;
    }

    private static SamAction ChooseOgcd(SamScenario scenario, SamState state, int weaveSlot)
    {
        var burst = SamTimeline.RaidBuffsLeft(scenario, state.Time) > 0 || scenario.OpenerBurst == SamOpenerBurst.ZeroSecond && state.Time < 20;
        if (weaveSlot == 0 && scenario.Potion == SamPotionStrategy.TwoMinuteBurst && !state.PotionUsed && state.Time < 6)
            return SamAction.Potion;

        if (ShouldUseMeikyo(scenario, state))
            return SamAction.MeikyoShisui;

        if (Unlocked(scenario, SamAction.Ikishoten) && state.IkishotenCd <= 0 && state.Kenki <= 50 && (burst || SamTimeline.NextTwoMinuteBurstIn(scenario, state.Time) <= 4))
            return SamAction.Ikishoten;

        if (Unlocked(scenario, SamAction.HissatsuSenei) && state.Kenki >= 25 && state.SeneiGurenCd <= 0 && (burst || SamTimeline.DowntimeSoon(scenario, state.Time, 6)))
            return state.EnemyCount >= 2 && Unlocked(scenario, SamAction.HissatsuGuren) ? SamAction.HissatsuGuren : SamAction.HissatsuSenei;

        if (Unlocked(scenario, SamAction.Zanshin) && state.ZanshinLeft > 0 && state.Kenki >= 50 && (burst || state.ZanshinLeft <= 4))
            return SamAction.Zanshin;

        if (Unlocked(scenario, SamAction.Shoha) && state.Meditation >= 3 && (burst || state.Meditation >= 3))
            return SamAction.Shoha;

        if (ShouldSpendBeforeIkishoten(scenario, state)
            || state.Kenki >= 90
            || state.Kenki >= 25 && !burst && !SamTimeline.BurstSoon(scenario, state.Time, 12))
            return state.EnemyCount >= 3 && Unlocked(scenario, SamAction.HissatsuKyuten) ? SamAction.HissatsuKyuten : SamAction.HissatsuShinten;

        return SamAction.None;
    }

    private static bool ShouldSpendBeforeIkishoten(SamScenario scenario, SamState state)
        => Unlocked(scenario, SamAction.Ikishoten)
            && state.IkishotenCd <= 0
            && state.OgiLeft <= 0
            && state.Kenki is > 50 and >= 75
            && (SamTimeline.RaidBuffsLeft(scenario, state.Time) > 0 || SamTimeline.NextTwoMinuteBurstIn(scenario, state.Time) <= 4);

    private static bool ShouldUseMeikyo(SamScenario scenario, SamState state)
    {
        if (!Unlocked(scenario, SamAction.MeikyoShisui) || scenario.Meikyo == SamMeikyoStrategy.Delay || state.MeikyoCharges <= 0 || state.MeikyoLeft > 0)
            return false;
        if (scenario.Meikyo == SamMeikyoStrategy.Force)
            return true;
        if (scenario.Meikyo == SamMeikyoStrategy.HoldOne)
            return state.MeikyoCharges >= 2;
        var burstSoon = SamTimeline.BurstSoon(scenario, state.Time, scenario.GcdRoute == SamGcdRoute.GCD208 ? 8 : 15);
        return scenario.Meikyo == SamMeikyoStrategy.Cooldown ? !burstSoon || SamTimeline.RaidBuffsLeft(scenario, state.Time) > 0 : state.MeikyoCharges >= 2 || burstSoon || state.Time < 5;
    }

    private static void ApplyGcd(SamScenario scenario, SamState state, SamAction action)
    {
        state.LastGcdAt = state.Time;
        state.LastGcd = action;
        switch (action)
        {
            case SamAction.Hakaze:
            case SamAction.Gyofu:
            case SamAction.Fuga:
            case SamAction.Fuko:
            case SamAction.Jinpu:
            case SamAction.Shifu:
                state.Combo = action;
                GainKenki(scenario, state, action is SamAction.Fuga or SamAction.Fuko ? 10 : 5);
                if (action == SamAction.Jinpu)
                    state.DamageBuff = 40;
                if (action == SamAction.Shifu)
                    state.HasteBuff = 40;
                break;
            case SamAction.Yukikaze:
                GrantSen(state, SamSen.Setsu);
                GainKenki(scenario, state, 10);
                EndCombo(state);
                break;
            case SamAction.Gekko:
            case SamAction.Mangetsu:
                GrantSen(state, SamSen.Getsu);
                state.DamageBuff = 40;
                GainKenki(scenario, state, 10);
                EndCombo(state);
                break;
            case SamAction.Kasha:
            case SamAction.Oka:
                GrantSen(state, SamSen.Ka);
                state.HasteBuff = 40;
                GainKenki(scenario, state, 10);
                EndCombo(state);
                break;
            case SamAction.Enpi:
                GainKenki(scenario, state, 10);
                break;
            case SamAction.Higanbana:
                state.HiganbanaDot = 60;
                ConsumeSen(state);
                GrantMeditation(state);
                break;
            case SamAction.TenkaGoken:
            case SamAction.MidareSetsugekka:
            case SamAction.TendoGoken:
            case SamAction.TendoSetsugekka:
                state.TsubameLeft = 30;
                state.TsubameAction = action switch
                {
                    SamAction.TenkaGoken => SamRepeat.Goken,
                    SamAction.MidareSetsugekka => SamRepeat.Setsugekka,
                    SamAction.TendoGoken => SamRepeat.TendoGoken,
                    SamAction.TendoSetsugekka => SamRepeat.TendoSetsugekka,
                    _ => SamRepeat.None
                };
                if (action is SamAction.TendoGoken or SamAction.TendoSetsugekka)
                    state.TendoLeft = 0;
                ConsumeSen(state);
                GrantMeditation(state);
                break;
            case SamAction.KaeshiGoken:
            case SamAction.KaeshiSetsugekka:
            case SamAction.TendoKaeshiGoken:
            case SamAction.TendoKaeshiSetsugekka:
                state.TsubameLeft = 0;
                state.TsubameAction = SamRepeat.None;
                GrantMeditation(state);
                break;
            case SamAction.OgiNamikiri:
                state.OgiLeft = 0;
                state.KaeshiNamikiriLeft = 30;
                GrantMeditation(state);
                break;
            case SamAction.KaeshiNamikiri:
                state.KaeshiNamikiriLeft = 0;
                GrantMeditation(state);
                break;
            case SamAction.Hagakure:
                GainKenki(scenario, state, state.SenCount * 10);
                ConsumeSen(state);
                break;
        }
    }

    private static void ApplyOgcd(SamScenario scenario, SamState state, SamAction action)
    {
        switch (action)
        {
            case SamAction.Potion:
                state.PotionUsed = true;
                state.PotionLeft = 30;
                state.PotionCd = 270;
                break;
            case SamAction.MeikyoShisui:
                state.MeikyoCharges--;
                if (state.MeikyoCharges < 2 && double.IsPositiveInfinity(state.MeikyoNextChargeAt))
                    state.MeikyoNextChargeAt = state.Time + 55;
                state.MeikyoLeft = 20;
                state.MeikyoStacks = 3;
                if (Unlocked(scenario, SamAction.TendoSetsugekka))
                    state.TendoLeft = 30;
                break;
            case SamAction.Ikishoten:
                GainKenkiRaw(state, 50);
                state.IkishotenCd = 120;
                if (Unlocked(scenario, SamAction.OgiNamikiri))
                    state.OgiLeft = 30;
                if (Unlocked(scenario, SamAction.Zanshin))
                    state.ZanshinLeft = 30;
                break;
            case SamAction.HissatsuSenei:
            case SamAction.HissatsuGuren:
                SpendKenki(state, 25);
                state.SeneiGurenCd = 120;
                break;
            case SamAction.Zanshin:
                SpendKenki(state, 50);
                state.ZanshinLeft = 0;
                break;
            case SamAction.Shoha:
                state.Meditation = 0;
                break;
            case SamAction.HissatsuShinten:
            case SamAction.HissatsuKyuten:
                SpendKenki(state, 25);
                break;
        }
    }

    private static void EndCombo(SamState state)
    {
        state.Combo = SamAction.None;
        if (state.MeikyoStacks > 0)
            state.MeikyoStacks--;
    }

    private static void GrantSen(SamState state, SamSen sen) => state.Sen |= sen;
    private static void ConsumeSen(SamState state) => state.Sen = SamSen.None;
    private static void GrantMeditation(SamState state) => state.Meditation = Math.Min(3, state.Meditation + 1);
    private static void GainKenki(SamScenario scenario, SamState state, int value)
    {
        if (scenario.Level >= 52)
            GainKenkiRaw(state, value);
    }

    private static void GainKenkiRaw(SamState state, int value) => state.Kenki = Math.Min(100, state.Kenki + value);
    private static void SpendKenki(SamState state, int value) => state.Kenki = Math.Max(0, state.Kenki - value);

    private static bool Unlocked(SamScenario scenario, SamAction action) => action switch
    {
        SamAction.Gyofu => scenario.Level >= 86,
        SamAction.Fuga => scenario.Level >= 26,
        SamAction.Fuko => scenario.Level >= 86,
        SamAction.Jinpu or SamAction.Shifu => scenario.Level >= 18,
        SamAction.Yukikaze => scenario.Level >= 30,
        SamAction.Gekko or SamAction.Kasha => scenario.Level >= 40,
        SamAction.Mangetsu or SamAction.Oka => scenario.Level >= 35,
        SamAction.Higanbana or SamAction.TenkaGoken or SamAction.MidareSetsugekka => scenario.Level >= 30,
        SamAction.MeikyoShisui => scenario.Level >= 50,
        SamAction.Hagakure => scenario.Level >= 68,
        SamAction.HissatsuShinten => scenario.Level >= 52,
        SamAction.HissatsuKyuten => scenario.Level >= 62,
        SamAction.HissatsuGuren or SamAction.Ikishoten => scenario.Level >= 70,
        SamAction.HissatsuSenei => scenario.Level >= 72,
        SamAction.Shoha => scenario.Level >= 80,
        SamAction.KaeshiGoken or SamAction.KaeshiSetsugekka or SamAction.TendoKaeshiGoken or SamAction.TendoKaeshiSetsugekka => scenario.Level >= 76,
        SamAction.OgiNamikiri or SamAction.KaeshiNamikiri => scenario.Level >= 90,
        SamAction.Zanshin => scenario.Level >= 96,
        SamAction.TendoGoken or SamAction.TendoSetsugekka => scenario.Level >= 100,
        _ => true
    };

    private static string TargetFor(SamAction action, SamState state)
        => action is SamAction.Fuko or SamAction.Mangetsu or SamAction.Oka or SamAction.HissatsuKyuten or SamAction.Zanshin ? "self/aoe"
            : state.RangedOnly ? "ranged" : state.Targetable ? "primary" : "none";

    private static string GcdReason(SamAction action)
        => action switch
        {
            SamAction.Higanbana => "dot refresh",
            SamAction.OgiNamikiri or SamAction.KaeshiNamikiri => "namikiri ready",
            SamAction.MidareSetsugekka or SamAction.TendoSetsugekka or SamAction.TenkaGoken or SamAction.TendoGoken => "iaijutsu",
            SamAction.Hagakure => "burst recovery",
            SamAction.Enpi => "ranged fallback",
            _ => "combo"
        };

    private static string OgcdReason(SamAction action)
        => action switch
        {
            SamAction.MeikyoShisui => "meikyo timing",
            SamAction.Ikishoten => "burst resource",
            SamAction.HissatsuSenei or SamAction.HissatsuGuren => "kenki burst priority",
            SamAction.Zanshin => "zanshin ready",
            SamAction.HissatsuShinten or SamAction.HissatsuKyuten => "kenki spend",
            SamAction.Potion => "potion timing",
            _ => "ogcd"
        };
}

public static class SamTimeline
{
    public static double RaidBuffsLeft(SamScenario scenario, double time)
    {
        if (scenario.RaidBuffProfile == SamRaidBuffProfile.None)
            return 0;

        var interval = scenario.RaidBuffProfile switch
        {
            SamRaidBuffProfile.ZeroSecond => 120,
            SamRaidBuffProfile.SixtySecond => 60,
            _ => 120
        };
        var first = scenario.RaidBuffProfile == SamRaidBuffProfile.ZeroSecond ? 0 : interval;
        var elapsed = time - (first + scenario.RaidBuffOffset);
        if (elapsed < 0)
            return 0;
        var inCycle = elapsed % interval;
        return inCycle < 20 ? 20 - inCycle : 0;
    }

    public static double RaidBuffsIn(SamScenario scenario, double time)
        => Math.Min(NextOneMinuteBurstIn(scenario, time), NextTwoMinuteBurstIn(scenario, time));

    public static double NextOneMinuteBurstIn(SamScenario scenario, double time)
        => NextBurstIn(scenario, time, scenario.RaidBuffProfile == SamRaidBuffProfile.ZeroSecond ? 0 : 60, 60);

    public static double NextTwoMinuteBurstIn(SamScenario scenario, double time)
        => NextBurstIn(scenario, time, scenario.RaidBuffProfile == SamRaidBuffProfile.ZeroSecond ? 0 : 120, 120);

    public static bool BurstSoon(SamScenario scenario, double time, double window)
        => RaidBuffsLeft(scenario, time) > 0 || RaidBuffsIn(scenario, time) <= window;

    public static bool DowntimeSoon(SamScenario scenario, double time, double window)
        => scenario.Downtimes.Any(d => d.Start > time && d.Start - time <= window);

    private static double NextBurstIn(SamScenario scenario, double time, double first, double interval)
    {
        if (scenario.RaidBuffProfile == SamRaidBuffProfile.None)
            return double.PositiveInfinity;
        first += scenario.RaidBuffOffset;
        if (time <= first)
            return first - time;
        var cycles = Math.Ceiling((time - first) / interval);
        return first + cycles * interval - time;
    }
}
