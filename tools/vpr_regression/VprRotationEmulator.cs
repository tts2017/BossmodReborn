using System;
using System.Collections.Generic;
using System.Linq;

namespace VprRegression;

public enum EmulatedDreadCombo
{
    None,
    Dreadwinder,
    HuntersCoil,
    SwiftskinsCoil,
    PitOfDread,
    HuntersDen,
    SwiftskinsDen
}

public enum EmulatedAction
{
    None,
    SteelFangs,
    HuntersCoil,
    SwiftskinsCoil,
    HuntersDen,
    SwiftskinsDen,
    Reawaken,
    Generation,
    UncoiledFury,
    SerpentsIre,
    SerpentsTail,
    TwinFollowUp,
    TrueNorth
}

public sealed record VprEmulatorState
{
    public bool TargetAvailable { get; init; } = true;
    public bool InCombat { get; init; } = true;
    public float CombatTimer { get; init; } = 120;
    public float AttackGcd { get; init; } = 2.5f;
    public float SerpentsIreReadyIn { get; init; }
    public bool SerpentsIreAuto { get; init; } = true;
    public bool SerpentsIreForce { get; init; }
    public int Offering { get; init; }
    public int Coil { get; init; }
    public int CoilMax { get; init; } = 3;
    public EmulatedDreadCombo DreadCombo { get; init; }
    public int Anguine { get; init; }
    public bool ReawakenReady { get; init; }
    public bool ReawakenActive { get; init; }
    public bool PendingTail { get; init; }
    public bool PendingTwin { get; init; }
    public bool OutOfMeleeRange { get; init; }
    public bool NextMeleeBlocked { get; init; }
    public bool UncoiledFuryRangeAuto { get; init; } = true;
    public bool TrueNorthAuto { get; init; } = true;
    public bool TrueNorthActive { get; init; }
    public float TrueNorthReadyIn { get; init; }
    public bool NextPositionalImminent { get; init; }
    public bool NextPositionalCorrect { get; init; }
    public bool NextCoilPositional { get; init; }
}

public sealed record VprEmulatorDecision(EmulatedAction GCD, IReadOnlyList<EmulatedAction> OGCDs);

public sealed record VprEmulatorCheck(string Name, bool Passed, string Detail);

public static class VprRotationEmulator
{
    public static VprEmulatorDecision Decide(VprEmulatorState state)
    {
        if (!state.TargetAvailable)
            return new(EmulatedAction.None, []);

        var gcd = SelectGCD(state);
        var ogcds = SelectOGCDs(state);
        return new(gcd, ogcds);
    }

    public static IReadOnlyList<VprEmulatorCheck> RunSuite()
    {
        var checks = new List<VprEmulatorCheck>();
        AddCheck(checks, "dread_combo_finishes_before_even_burst", new VprEmulatorState
        {
            DreadCombo = EmulatedDreadCombo.Dreadwinder,
            SerpentsIreReadyIn = 0,
            Offering = 50
        }, decision => decision.GCD is EmulatedAction.HuntersCoil or EmulatedAction.SwiftskinsCoil && !decision.OGCDs.Contains(EmulatedAction.SerpentsIre));
        AddCheck(checks, "auto_ire_waits_for_tail", new VprEmulatorState
        {
            PendingTail = true,
            SerpentsIreReadyIn = 0,
            Offering = 50
        }, decision => decision.OGCDs.Contains(EmulatedAction.SerpentsTail) && !decision.OGCDs.Contains(EmulatedAction.SerpentsIre));
        AddCheck(checks, "auto_ire_waits_for_twin", new VprEmulatorState
        {
            PendingTwin = true,
            SerpentsIreReadyIn = 0,
            Offering = 50
        }, decision => decision.OGCDs.Contains(EmulatedAction.TwinFollowUp) && !decision.OGCDs.Contains(EmulatedAction.SerpentsIre));
        AddCheck(checks, "range_fury_off", new VprEmulatorState
        {
            Coil = 1,
            OutOfMeleeRange = true,
            NextMeleeBlocked = true,
            UncoiledFuryRangeAuto = false
        }, decision => decision.GCD != EmulatedAction.UncoiledFury);
        AddCheck(checks, "range_fury_auto", new VprEmulatorState
        {
            Coil = 1,
            OutOfMeleeRange = true,
            NextMeleeBlocked = true
        }, decision => decision.GCD == EmulatedAction.UncoiledFury);
        AddCheck(checks, "true_north_requires_incorrect_positional", new VprEmulatorState
        {
            NextCoilPositional = true,
            NextPositionalImminent = true,
            NextPositionalCorrect = true
        }, decision => !decision.OGCDs.Contains(EmulatedAction.TrueNorth));
        AddCheck(checks, "reawaken_sequence_is_not_interrupted", new VprEmulatorState
        {
            Anguine = 3,
            Coil = 3,
            OutOfMeleeRange = true,
            NextMeleeBlocked = true
        }, decision => decision.GCD == EmulatedAction.Generation);
        return checks;
    }

    private static EmulatedAction SelectGCD(VprEmulatorState state)
    {
        if (state.Anguine > 0 || state.ReawakenActive)
            return EmulatedAction.Generation;

        if (state.DreadCombo != EmulatedDreadCombo.None)
            return state.DreadCombo switch
            {
                EmulatedDreadCombo.Dreadwinder => EmulatedAction.HuntersCoil,
                EmulatedDreadCombo.HuntersCoil => EmulatedAction.SwiftskinsCoil,
                EmulatedDreadCombo.SwiftskinsCoil => EmulatedAction.HuntersCoil,
                EmulatedDreadCombo.PitOfDread => EmulatedAction.HuntersDen,
                EmulatedDreadCombo.HuntersDen => EmulatedAction.SwiftskinsDen,
                EmulatedDreadCombo.SwiftskinsDen => EmulatedAction.HuntersDen,
                _ => EmulatedAction.None
            };

        if (state.ReawakenReady || state.Offering >= 100)
            return EmulatedAction.Reawaken;

        if (state.UncoiledFuryRangeAuto && state.Coil > 0 && state.OutOfMeleeRange && state.NextMeleeBlocked)
            return EmulatedAction.UncoiledFury;

        return EmulatedAction.SteelFangs;
    }

    private static IReadOnlyList<EmulatedAction> SelectOGCDs(VprEmulatorState state)
    {
        List<EmulatedAction> actions = [];
        if (state.PendingTail)
            actions.Add(EmulatedAction.SerpentsTail);
        if (state.PendingTwin)
            actions.Add(EmulatedAction.TwinFollowUp);

        var canUseIre = state.SerpentsIreAuto
            && state.SerpentsIreReadyIn <= state.AttackGcd
            && state.Coil < state.CoilMax
            && state.DreadCombo == EmulatedDreadCombo.None
            && !state.PendingTail
            && !state.PendingTwin
            && state.Anguine == 0
            && !state.ReawakenActive;
        if (state.SerpentsIreForce || canUseIre)
            actions.Add(EmulatedAction.SerpentsIre);

        var canUseTrueNorth = state.TrueNorthAuto
            && !state.TrueNorthActive
            && state.TrueNorthReadyIn <= state.AttackGcd
            && state.NextCoilPositional
            && state.NextPositionalImminent
            && !state.NextPositionalCorrect;
        if (canUseTrueNorth)
            actions.Add(EmulatedAction.TrueNorth);

        return actions;
    }

    private static void AddCheck(List<VprEmulatorCheck> checks, string name, VprEmulatorState state, Func<VprEmulatorDecision, bool> predicate)
    {
        var decision = Decide(state);
        var passed = predicate(decision);
        checks.Add(new(name, passed, $"gcd={decision.GCD}; ogcd={string.Join(',', decision.OGCDs)}"));
    }
}
