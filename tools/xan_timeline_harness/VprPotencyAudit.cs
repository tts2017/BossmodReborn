using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BossMod;
using AID = BossMod.VPR.AID;

namespace XanTimelineHarness;

// Checks VprPotency - the level-aware 7.5 table the VPR emulator scores with - and the gauge / status rules the emulator applies against
// the client's own action descriptions, the same way SamPotencyAudit does for SAM. The positional actions are stored with the
// positional bonus included, so their checks read the flank / rear numbers.
internal static class VprPotencyAudit
{
    private const int ViperJob = 41;
    private static readonly int[] Levels = [100, 96, 94, 92, 90, 88, 86, 84, 82, 80, 76, 74, 70, 66, 60, 50];

    private sealed record Check(AID Action, string Pattern, Func<int, float> Expected);

    private const string Potency = @"potency of ([\d,]+)";

    private static float P(AID aid, int level) => VprPotency.Of(aid, level);

    private static readonly Check[] Checks =
    [
        new(AID.SteelFangs, Potency, l => P(AID.SteelFangs, l)),
        new(AID.SteelFangs, @"Honed Steel Potency: ([\d,]+)", l => P(AID.SteelFangs, l) + VprPotency.HonedFangsBonus),
        new(AID.ReavingFangs, Potency, l => P(AID.ReavingFangs, l)),
        new(AID.ReavingFangs, @"Honed Reavers Potency: ([\d,]+)", l => P(AID.ReavingFangs, l) + VprPotency.HonedFangsBonus),
        new(AID.HuntersSting, Potency, l => P(AID.HuntersSting, l)),
        new(AID.SwiftskinsSting, Potency, l => P(AID.SwiftskinsSting, l)),
        new(AID.FlankstingStrike, @"([\d,]+) when executed from a target's flank", l => P(AID.FlankstingStrike, l)),
        new(AID.FlanksbaneFang, @"([\d,]+) when executed from a target's flank", l => P(AID.FlanksbaneFang, l)),
        new(AID.HindstingStrike, @"([\d,]+) when executed from a target's rear", l => P(AID.HindstingStrike, l)),
        new(AID.HindsbaneFang, @"([\d,]+) when executed from a target's rear", l => P(AID.HindsbaneFang, l)),
        new(AID.FlankstingStrike, Potency, l => P(AID.FlankstingStrike, l) - VprPotency.FinisherPositionalBonus),
        new(AID.HindsbaneFang, Potency, l => P(AID.HindsbaneFang, l) - VprPotency.FinisherPositionalBonus),
        new(AID.FlankstingStrike, @"Potencies are increased by (\d+)", _ => VprPotency.VenomBonus),
        new(AID.HindsbaneFang, @"Potencies are increased by (\d+)", _ => VprPotency.VenomBonus),
        new(AID.WrithingSnap, Potency, l => P(AID.WrithingSnap, l)),
        new(AID.SteelMaw, Potency, l => P(AID.SteelMaw, l)),
        new(AID.SteelMaw, @"Honed Steel Potency: ([\d,]+)", l => P(AID.SteelMaw, l) + VprPotency.HonedMawBonus),
        new(AID.ReavingMaw, Potency, l => P(AID.ReavingMaw, l)),
        new(AID.ReavingMaw, @"Honed Reavers Potency: ([\d,]+)", l => P(AID.ReavingMaw, l) + VprPotency.HonedMawBonus),
        new(AID.HuntersBite, Potency, l => P(AID.HuntersBite, l)),
        new(AID.SwiftskinsBite, Potency, l => P(AID.SwiftskinsBite, l)),
        new(AID.JaggedMaw, Potency, l => P(AID.JaggedMaw, l)),
        new(AID.JaggedMaw, @"Potencies are increased by (\d+)", _ => VprPotency.GrimBonus),
        new(AID.BloodiedMaw, Potency, l => P(AID.BloodiedMaw, l)),
        new(AID.DeathRattle, Potency, l => P(AID.DeathRattle, l)),
        new(AID.LastLash, Potency, l => P(AID.LastLash, l)),
        new(AID.Vicewinder, Potency, l => P(AID.Vicewinder, l)),
        new(AID.HuntersCoil, @"([\d,]+) when executed from a target's flank", l => P(AID.HuntersCoil, l)),
        new(AID.SwiftskinsCoil, @"([\d,]+) when executed from a target's rear", l => P(AID.SwiftskinsCoil, l)),
        new(AID.HuntersCoil, Potency, l => P(AID.HuntersCoil, l) - VprPotency.CoilPositionalBonus),
        new(AID.SwiftskinsCoil, Potency, l => P(AID.SwiftskinsCoil, l) - VprPotency.CoilPositionalBonus),
        new(AID.Vicepit, Potency, l => P(AID.Vicepit, l)),
        new(AID.HuntersDen, Potency, l => P(AID.HuntersDen, l)),
        new(AID.SwiftskinsDen, Potency, l => P(AID.SwiftskinsDen, l)),
        new(AID.TwinfangBite, Potency, l => P(AID.TwinfangBite, l)),
        new(AID.TwinfangBite, @"Hunter's Venom Potency: ([\d,]+)", l => P(AID.TwinfangBite, l) + VprPotency.TwinBonus),
        new(AID.TwinbloodBite, Potency, l => P(AID.TwinbloodBite, l)),
        new(AID.TwinbloodBite, @"Swiftskin's Venom Potency: ([\d,]+)", l => P(AID.TwinbloodBite, l) + VprPotency.TwinBonus),
        new(AID.TwinfangThresh, Potency, l => P(AID.TwinfangThresh, l)),
        new(AID.TwinfangThresh, @"Fellhunter's Venom Potency: ([\d,]+)", l => P(AID.TwinfangThresh, l) + VprPotency.ThreshBonus),
        new(AID.TwinbloodThresh, Potency, l => P(AID.TwinbloodThresh, l)),
        new(AID.TwinbloodThresh, @"Fellskin's Venom Potency: ([\d,]+)", l => P(AID.TwinbloodThresh, l) + VprPotency.ThreshBonus),
        new(AID.UncoiledFury, Potency, l => P(AID.UncoiledFury, l)),
        new(AID.UncoiledTwinfang, Potency, l => P(AID.UncoiledTwinfang, l)),
        new(AID.UncoiledTwinfang, @"Poised for Twinfang Potency: ([\d,]+)", l => P(AID.UncoiledTwinfang, l) + VprPotency.TwinBonus),
        new(AID.UncoiledTwinblood, Potency, l => P(AID.UncoiledTwinblood, l)),
        new(AID.UncoiledTwinblood, @"Poised for Twinblood Potency: ([\d,]+)", l => P(AID.UncoiledTwinblood, l) + VprPotency.TwinBonus),
        new(AID.FirstGeneration, Potency, l => P(AID.FirstGeneration, l)),
        new(AID.FirstGeneration, @"Potency is increased to ([\d,]+)", _ => VprPotency.GenerationInSequence),
        new(AID.SecondGeneration, @"Potency is increased to ([\d,]+)", _ => VprPotency.GenerationInSequence),
        new(AID.ThirdGeneration, @"Potency is increased to ([\d,]+)", _ => VprPotency.GenerationInSequence),
        new(AID.FourthGeneration, @"Potency is increased to ([\d,]+)", _ => VprPotency.GenerationInSequence),
        new(AID.Reawaken, Potency, l => P(AID.Reawaken, l)),
        new(AID.Reawaken, @"Grants (\d) stacks of Anguine Tribute", l => l >= 96 ? 5 : 4),
        new(AID.Ouroboros, Potency, l => P(AID.Ouroboros, l)),
        new(AID.FirstLegacy, Potency, l => P(AID.FirstLegacy, l)),
        new(AID.FourthLegacy, Potency, l => P(AID.FourthLegacy, l)),
        // no Rattling Coil before 82 (the clause is absent)
        new(AID.Vicewinder, @"up to a maximum of (\d)", l => l >= 88 ? 3 : l >= 82 ? 2 : -1),
    ];

    // Values the emulator multiplies by or times with, read from the text of the action that grants them (level 100).
    private static readonly (AID Action, string Pattern, float Expected)[] Constants =
    [
        (AID.HuntersSting, @"Increases damage dealt by (\d+)%", 10),
        (AID.HuntersSting, @"Duration: (\d+)s", 40),
        (AID.SwiftskinsSting, @"auto-attack delay by (\d+)%", 15),
        (AID.SwiftskinsSting, @"Duration: (\d+)s", 40),
        (AID.SteelFangs, @"Duration: (\d+)s", 60),
        (AID.FlankstingStrike, @"Duration: (\d+)s", 60),
        (AID.FlankstingStrike, @"Increases Serpent Offerings Gauge by (\d+)", 10),
        (AID.JaggedMaw, @"Increases Serpent Offerings Gauge by (\d+)", 10),
        (AID.HuntersCoil, @"Increases Serpent Offerings Gauge by (\d+)", 5),
        (AID.HuntersDen, @"Increases Serpent Offerings Gauge by (\d+)", 5),
        (AID.HuntersCoil, @"Hunter's Venom Effect: Increases potency of Twinfang Bite by 50\s*\nDuration: (\d+)s", 30),
        (AID.UncoiledFury, @"Duration: (\d+)s", 60),
        (AID.SerpentsIre, @"Duration: (\d+)s", 30),
        (AID.Reawaken, @"Duration: (\d+)s", 30),
        (AID.Reawaken, @"Serpent Offerings Gauge Cost: (\d+)", 50),
        (AID.UncoiledFury, @"(\d+)% less for all remaining enemies", (1 - VprPotency.FalloffMultiplier) * 100),
        (AID.FirstGeneration, @"(\d+)% less for all remaining enemies", (1 - VprPotency.FalloffMultiplier) * 100),
        (AID.Reawaken, @"(\d+)% less for all remaining enemies", (1 - VprPotency.FalloffMultiplier) * 100),
        (AID.Ouroboros, @"(\d+)% less for all remaining enemies", (1 - VprPotency.FalloffMultiplier) * 100),
        (AID.FirstLegacy, @"(\d+)% less for all remaining enemies", (1 - VprPotency.FalloffMultiplier) * 100),
        (AID.UncoiledTwinfang, @"(\d+)% less for all remaining enemies", (1 - VprPotency.FalloffMultiplier) * 100),
    ];

    public static void Run(Lumina.Excel.ExcelSheet<Lumina.Excel.Sheets.ActionTransient> sheet, ref int checks, ref int failures)
    {
        foreach (var level in Levels)
        {
            foreach (var check in Checks)
            {
                var def = ActionDefinitions.Instance.Spell(check.Action);
                if (def == null || level < def.MinLevel)
                    continue;
                ++checks;
                var text = NinPotencyAudit.Evaluate(sheet.GetRow((uint)check.Action).Description.ToMacroString(), ViperJob, level);
                var match = Regex.Match(text, check.Pattern);
                var expected = check.Expected(level);
                var actual = match.Success ? float.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
                if (actual != expected)
                {
                    ++failures;
                    Console.WriteLine($"VPR L{level} {check.Action} '{check.Pattern}': table={expected} client={actual}\n    {text.Replace('\n', '|')}");
                }
            }
        }

        foreach (var (action, pattern, expected) in Constants)
        {
            ++checks;
            var text = NinPotencyAudit.Evaluate(sheet.GetRow((uint)action).Description.ToMacroString(), ViperJob, 100);
            var match = Regex.Match(text, pattern);
            var actual = match.Success ? float.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
            if (MathF.Abs(actual - expected) > 0.01f)
            {
                ++failures;
                Console.WriteLine($"VPR constant {action} '{pattern}': table={expected} client={actual}\n    {text.Replace('\n', '|')}");
            }
        }
    }
}
