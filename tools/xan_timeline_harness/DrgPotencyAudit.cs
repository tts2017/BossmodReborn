using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BossMod;
using AID = BossMod.DRG.AID;

namespace XanTimelineHarness;

// Checks DrgPotency - the level-aware 7.5 table the DRG emulator scores with - against the client's own action descriptions, the
// same way NinPotencyAudit does for NIN. The positional actions are stored with the positional bonus included, so their checks read
// the rear / flank numbers.
internal static class DrgPotencyAudit
{
    private const int DragoonJob = 22;
    private static readonly int[] Levels = [100, 96, 94, 92, 90, 86, 82, 80, 76, 70, 64];

    private sealed record Check(AID Action, bool Combo, string Pattern);

    private const string Potency = @"potency of ([\d,]+)";
    private const string Combo = @"(?<!Rear |Flank )Combo Potency: ([\d,]+)";

    private static readonly Check[] Checks =
    [
        new(AID.TrueThrust, false, Potency),
        new(AID.RaidenThrust, false, Potency),
        new(AID.VorpalThrust, false, Potency),
        new(AID.VorpalThrust, true, Combo),
        new(AID.LanceBarrage, false, Potency),
        new(AID.LanceBarrage, true, Combo),
        new(AID.FullThrust, false, Potency),
        new(AID.FullThrust, true, Combo),
        new(AID.HeavensThrust, false, Potency),
        new(AID.HeavensThrust, true, Combo),
        new(AID.Disembowel, false, Potency),
        new(AID.Disembowel, true, Combo),
        new(AID.SpiralBlow, false, Potency),
        new(AID.SpiralBlow, true, Combo),
        new(AID.ChaosThrust, false, @"([\d,]+) when executed from a target's rear"),
        new(AID.ChaosThrust, true, @"Rear Combo Potency: ([\d,]+)"),
        new(AID.ChaoticSpring, false, @"([\d,]+) when executed from a target's rear"),
        new(AID.ChaoticSpring, true, @"Rear Combo Potency: ([\d,]+)"),
        new(AID.FangAndClaw, false, @"([\d,]+) when executed from a target's flank"),
        new(AID.FangAndClaw, true, @"Flank Combo Potency: ([\d,]+)"),
        new(AID.WheelingThrust, false, @"([\d,]+) when executed from a target's rear"),
        new(AID.WheelingThrust, true, @"Rear Combo Potency: ([\d,]+)"),
        new(AID.Drakesbane, false, Potency),
        new(AID.PiercingTalon, false, Potency),
        new(AID.DoomSpike, false, Potency),
        new(AID.SonicThrust, false, Potency),
        new(AID.SonicThrust, true, Combo),
        new(AID.CoerthanTorment, false, Potency),
        new(AID.CoerthanTorment, true, Combo),
        new(AID.DraconianFury, false, Potency),
        new(AID.Jump, false, Potency),
        new(AID.HighJump, false, Potency),
        new(AID.MirageDive, false, Potency),
        new(AID.Geirskogul, false, Potency),
        new(AID.Nastrond, false, Potency),
        new(AID.Stardiver, false, Potency),
        new(AID.Starcross, false, Potency),
        new(AID.DragonfireDive, false, Potency),
        new(AID.RiseOfTheDragon, false, Potency),
        new(AID.WyrmwindThrust, false, Potency),
    ];

    // Values the emulator multiplies by, read from the text of the action that grants them (level 100).
    private static readonly (AID Action, string Pattern, float Expected)[] Constants =
    [
        (AID.SpiralBlow, @"Power Surge Effect: Increases damage dealt by (\d+)%", 10),
        (AID.LanceCharge, @"Increases damage dealt by (\d+)%", 10),
        (AID.Geirskogul, @"Life of the Dragon Effect: Increases damage dealt by (\d+)%", 15),
        (AID.BattleLitany, @"critical hit rate of self and nearby party members by (\d+)%", 10),
        (AID.ChaoticSpring, @"Potency: (\d+)\s*\nDuration", DrgPotency.DotTickPotency(AID.ChaoticSpring)),
        (AID.ChaoticSpring, @"Duration: (\d+)s", DrgPotency.DotDuration),
        (AID.SpiralBlow, @"Duration: (\d+)s", 30),
        (AID.Geirskogul, @"(\d+)% less for all remaining enemies", 50),
        (AID.Stardiver, @"(\d+)% less for all remaining enemies", 40),
        (AID.Starcross, @"(\d+)% less for all remaining enemies", 40),
        (AID.DragonfireDive, @"(\d+)% less for all remaining enemies", 50),
        (AID.RiseOfTheDragon, @"(\d+)% less for all remaining enemies", 50),
        (AID.WyrmwindThrust, @"(\d+)% less for all remaining enemies", 50),
        (AID.Nastrond, @"(\d+)% less for all remaining enemies", 50),
        (AID.PiercingTalon, @"Enhanced Piercing Talon Potency: ([\d,]+)", DrgPotency.EnhancedTalon(100)),
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
                var text = NinPotencyAudit.Evaluate(sheet.GetRow((uint)check.Action).Description.ToMacroString(), DragoonJob, level);
                var match = Regex.Match(text, check.Pattern);
                var expected = DrgPotency.Of(check.Action, level, check.Combo);
                var actual = match.Success ? float.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
                if (actual != expected)
                {
                    ++failures;
                    Console.WriteLine($"DRG L{level} {check.Action}/{(check.Combo ? "combo" : "base")}: table={expected} client={actual}\n    {text.Replace('\n', '|')}");
                }
            }
        }

        foreach (var (action, pattern, expected) in Constants)
        {
            ++checks;
            var text = NinPotencyAudit.Evaluate(sheet.GetRow((uint)action).Description.ToMacroString(), DragoonJob, 100);
            var match = Regex.Match(text, pattern);
            var actual = match.Success ? float.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
            if (MathF.Abs(actual - expected) > 0.01f)
            {
                ++failures;
                Console.WriteLine($"DRG constant {action} '{pattern}': table={expected} client={actual}\n    {text.Replace('\n', '|')}");
            }
        }
    }
}
