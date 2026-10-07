using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BossMod;
using AID = BossMod.PLD.AID;

namespace XanTimelineHarness;

// Checks PldPotency - the level-aware 7.5 table the Paladin emulator scores with - against the client's own action descriptions, the same
// way DrgPotencyAudit does for DRG, plus the constants the emulator multiplies by or counts with (Fight or Flight 25%, the 60% splash
// falloff, the Circle of Scorn dot, the stack counts and durations of the statuses). PLD_DEFS=1 prints the action data the emulator
// relies on (recast, charges, cast, cooldown groups, MP cost, whether the action keeps the combo) instead.
internal static class PldPotencyAudit
{
    private const int PaladinJob = 19;
    private static readonly int[] Levels = [100, 94, 90, 84, 80, 76, 72, 68, 64, 60, 54, 40];

    private enum Variant { Base, Combo, DivineMight, Requiescat }

    private sealed record Check(AID Action, Variant Variant, string Pattern, int MinLevel = 0);

    private const string Potency = @"potency of ([\d,]+)";
    private const string Combo = @"Combo Potency: ([\d,]+)";
    private const string DivineMight = @"Divine Might Potency: ([\d,]+)";
    private const string Requiescat = @"Requiescat Potency: ([\d,]+)";

    private static readonly Check[] Checks =
    [
        new(AID.FastBlade, Variant.Base, Potency),
        new(AID.RiotBlade, Variant.Base, Potency),
        new(AID.RiotBlade, Variant.Combo, Combo),
        new(AID.RageOfHalone, Variant.Base, Potency),
        new(AID.RageOfHalone, Variant.Combo, Combo),
        new(AID.RoyalAuthority, Variant.Base, Potency),
        new(AID.RoyalAuthority, Variant.Combo, Combo),
        new(AID.TotalEclipse, Variant.Base, Potency),
        new(AID.Prominence, Variant.Base, Potency),
        new(AID.Prominence, Variant.Combo, Combo),
        new(AID.HolySpirit, Variant.Base, Potency),
        new(AID.HolySpirit, Variant.DivineMight, DivineMight, 64),
        new(AID.HolySpirit, Variant.Requiescat, Requiescat, 68),
        new(AID.HolyCircle, Variant.Base, Potency),
        new(AID.HolyCircle, Variant.DivineMight, DivineMight),
        new(AID.HolyCircle, Variant.Requiescat, Requiescat),
        new(AID.Atonement, Variant.Base, Potency),
        new(AID.Supplication, Variant.Base, Potency),
        new(AID.Sepulchre, Variant.Base, Potency),
        new(AID.Confiteor, Variant.Base, Potency),
        new(AID.Confiteor, Variant.Requiescat, Requiescat),
        new(AID.BladeOfFaith, Variant.Base, Potency),
        new(AID.BladeOfFaith, Variant.Requiescat, Requiescat),
        new(AID.BladeOfTruth, Variant.Base, Potency),
        new(AID.BladeOfTruth, Variant.Requiescat, Requiescat),
        new(AID.BladeOfValor, Variant.Base, Potency),
        new(AID.BladeOfValor, Variant.Requiescat, Requiescat),
        new(AID.BladeOfHonor, Variant.Base, Potency),
        new(AID.Imperator, Variant.Base, Potency),
        new(AID.Requiescat, Variant.Base, Potency),
        new(AID.GoringBlade, Variant.Base, Potency),
        new(AID.SpiritsWithin, Variant.Base, Potency),
        new(AID.Expiacion, Variant.Base, Potency),
        new(AID.CircleOfScorn, Variant.Base, Potency),
        new(AID.Intervene, Variant.Base, Potency),
        new(AID.ShieldLob, Variant.Base, Potency),
    ];

    // Values the emulator multiplies by or counts with, read from the text of the action that grants them (level 100).
    private static readonly (AID Action, string Pattern, float Expected)[] Constants =
    [
        (AID.FightOrFlight, @"Increases damage dealt by (\d+)%", PldPotency.FightOrFlightMultiplier * 100 - 100),
        (AID.FightOrFlight, @"Duration: (\d+)s\n", 20),
        (AID.FightOrFlight, @"Goring Blade Ready\n?Duration: (\d+)s", 30),
        (AID.Confiteor, @"(\d+)% less for all remaining enemies", 100 - PldPotency.SplashFalloff * 100),
        (AID.BladeOfFaith, @"(\d+)% less for all remaining enemies", 100 - PldPotency.SplashFalloff * 100),
        (AID.BladeOfTruth, @"(\d+)% less for all remaining enemies", 100 - PldPotency.SplashFalloff * 100),
        (AID.BladeOfValor, @"(\d+)% less for all remaining enemies", 100 - PldPotency.SplashFalloff * 100),
        (AID.BladeOfHonor, @"(\d+)% less for all remaining enemies", 100 - PldPotency.SplashFalloff * 100),
        (AID.Imperator, @"(\d+)% less for all remaining enemies", 100 - PldPotency.SplashFalloff * 100),
        (AID.Expiacion, @"(\d+)% less for all remaining enemies", 100 - PldPotency.SplashFalloff * 100),
        (AID.CircleOfScorn, @"Potency: (\d+)\s*\nDuration", PldPotency.CircleOfScornDotTick),
        (AID.CircleOfScorn, @"Duration: (\d+)s", PldPotency.CircleOfScornDotTicks * 3),
        (AID.Imperator, @"Grants (\d+) stacks of", 4),
        (AID.Requiescat, @"Grants (\d+) stacks of", 4),
        (AID.Imperator, @"Duration: (\d+)s", 30),
        (AID.RoyalAuthority, @"Atonement Ready\s*\nDuration: (\d+)s", 30),
        (AID.RoyalAuthority, @"Divine Might\s*\n(?:.*\n)?Duration: (\d+)s", 30),
        (AID.Atonement, @"Supplication Ready\s*\nDuration: (\d+)s", 30),
        (AID.Supplication, @"Sepulchre Ready\s*\nDuration: (\d+)s", 30),
        (AID.BladeOfValor, @"Blade of Honor Ready\s*\nDuration: (\d+)s", 30),
    ];

    public static void Run(Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.ActionTransient> sheet, ref int checks, ref int failures)
    {
        foreach (var level in Levels)
        {
            foreach (var check in Checks)
            {
                var def = ActionDefinitions.Instance.Spell(check.Action);
                if (def == null || level < def.MinLevel || level < check.MinLevel)
                    continue;
                ++checks;
                var text = NinPotencyAudit.Evaluate(sheet.GetRow((uint)check.Action).Description.ToMacroString(), PaladinJob, level);
                var match = Regex.Match(text, check.Pattern);
                var expected = PldPotency.Of(check.Action, level, check.Variant == Variant.Combo, check.Variant == Variant.DivineMight, check.Variant == Variant.Requiescat);
                var actual = match.Success ? float.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
                // the text of a level that does not have the variant yet has no such line; that is not a mismatch of the table
                if (!match.Success && check.Variant != Variant.Base && check.Variant != Variant.Combo)
                    continue;
                if (actual != expected)
                {
                    ++failures;
                    Console.WriteLine($"PLD L{level} {check.Action}/{check.Variant}: table={expected} client={actual}\n    {text.Replace('\n', '|')}");
                }
            }
        }

        foreach (var (action, pattern, expected) in Constants)
        {
            ++checks;
            var text = NinPotencyAudit.Evaluate(sheet.GetRow((uint)action).Description.ToMacroString(), PaladinJob, 100);
            var match = Regex.Match(text, pattern);
            var actual = match.Success ? float.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
            if (MathF.Abs(actual - expected) > 0.01f)
            {
                ++failures;
                Console.WriteLine($"PLD constant {action} '{pattern}': table={expected} client={actual}\n    {text.Replace('\n', '|')}");
            }
        }
    }

    // PLD_DEFS=1: the action data behind the emulator (checked against the logs of other players in the report)
    public static void DumpDefs()
    {
        var actions = Service.LuminaSheet<Lumina.Excel.Sheets.Action>()!;
        foreach (var aid in Enum.GetValues<AID>())
        {
            var id = (uint)aid;
            if (id is 0 or > 40000 || actions.GetRowOrDefault(id) is not { } a)
                continue;
            var def = ActionDefinitions.Instance.Spell(aid);
            Console.WriteLine(FormattableString.Invariant(
                $"{aid,-16} id={id,5} lvl={a.ClassJobLevel,3} recast={a.Recast100ms / 10.0,5:f1} charges={a.MaxCharges} range={a.Range,3} effectRange={a.EffectRange,2} cast={a.Cast100ms / 10.0:f1} cdGroup={a.CooldownGroup}/{a.AdditionalCooldownGroup} costType={a.PrimaryCostType} cost={a.PrimaryCostValue} preservesCombo={a.PreservesCombo} cat={a.ActionCategory.RowId} registered={def != null}"));
        }
    }
}
