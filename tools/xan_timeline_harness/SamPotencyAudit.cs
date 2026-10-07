using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BossMod;
using AID = BossMod.SAM.AID;

namespace XanTimelineHarness;

// Checks SamPotency - the level-aware 7.5 table the SAM emulator scores with - against the client's own action descriptions, the
// same way DrgPotencyAudit does for DRG. The positional actions are stored with the positional bonus included, so their checks read
// the rear / flank numbers.
internal static class SamPotencyAudit
{
    private const int SamuraiJob = 34;
    private static readonly int[] Levels = [100, 96, 94, 92, 90, 86, 84, 82, 80, 76, 72, 70, 66, 64, 60, 52];

    private sealed record Check(AID Action, bool Combo, string Pattern);

    private const string Potency = @"potency of ([\d,]+)";
    private const string Combo = @"(?<!Rear |Flank )Combo Potency: ([\d,]+)";

    private static readonly Check[] Checks =
    [
        new(AID.Hakaze, false, Potency),
        new(AID.Gyofu, false, Potency),
        new(AID.Jinpu, false, Potency),
        new(AID.Jinpu, true, Combo),
        new(AID.Shifu, false, Potency),
        new(AID.Shifu, true, Combo),
        new(AID.Gekko, false, @"([\d,]+) when executed from a target's rear"),
        new(AID.Gekko, true, @"Rear Combo Potency: ([\d,]+)"),
        new(AID.Kasha, false, @"([\d,]+) when executed from a target's flank"),
        new(AID.Kasha, true, @"Flank Combo Potency: ([\d,]+)"),
        new(AID.Yukikaze, false, Potency),
        new(AID.Yukikaze, true, Combo),
        new(AID.Enpi, false, Potency),
        new(AID.Fuga, false, Potency),
        new(AID.Fuko, false, Potency),
        new(AID.Mangetsu, false, Potency),
        new(AID.Mangetsu, true, Combo),
        new(AID.Oka, false, Potency),
        new(AID.Oka, true, Combo),
        new(AID.Higanbana, false, Potency),
        new(AID.TenkaGoken, false, Potency),
        new(AID.MidareSetsugekka, false, Potency),
        new(AID.KaeshiSetsugekka, false, Potency),
        new(AID.KaeshiGoken, false, Potency),
        new(AID.TendoGoken, false, Potency),
        new(AID.TendoKaeshiGoken, false, Potency),
        new(AID.TendoSetsugekka, false, Potency),
        new(AID.TendoKaeshiSetsugekka, false, Potency),
        new(AID.OgiNamikiri, false, Potency),
        new(AID.KaeshiNamikiri, false, Potency),
        new(AID.Zanshin, false, Potency),
        new(AID.Shoha, false, Potency),
        new(AID.HissatsuShinten, false, Potency),
        new(AID.HissatsuKyuten, false, Potency),
        new(AID.HissatsuGyoten, false, Potency),
        new(AID.HissatsuYaten, false, Potency),
        new(AID.HissatsuSenei, false, Potency),
        new(AID.HissatsuGuren, false, Potency),
    ];

    // Values the emulator multiplies by or times with, read from the text of the action that grants them (level 100).
    private static readonly (AID Action, string Pattern, float Expected)[] Constants =
    [
        (AID.Jinpu, @"Increases damage dealt by (\d+)%", 13),
        (AID.Jinpu, @"Duration: (\d+)s", 40),
        (AID.Shifu, @"Reduces weaponskill cast time and recast time, spell cast time and recast time, and auto-attack delay by (\d+)%", 13),
        (AID.Shifu, @"Duration: (\d+)s", 40),
        (AID.Higanbana, @"Potency: (\d+)\s*\nDuration", SamPotency.HiganbanaTick(100)),
        (AID.Higanbana, @"Duration: (\d+)s", SamPotency.HiganbanaDuration),
        (AID.Enpi, @"Enhanced Enpi Potency: ([\d,]+)", SamPotency.EnhancedEnpi(100)),
        (AID.OgiNamikiri, @"(\d+)% less for all remaining enemies", (1 - SamPotency.Falloff(AID.OgiNamikiri)) * 100),
        (AID.KaeshiNamikiri, @"(\d+)% less for all remaining enemies", (1 - SamPotency.Falloff(AID.KaeshiNamikiri)) * 100),
        (AID.Zanshin, @"(\d+)% less for all remaining enemies", (1 - SamPotency.Falloff(AID.Zanshin)) * 100),
        (AID.Shoha, @"(\d+)% less for all remaining enemies", (1 - SamPotency.Falloff(AID.Shoha)) * 100),
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
                var text = NinPotencyAudit.Evaluate(sheet.GetRow((uint)check.Action).Description.ToMacroString(), SamuraiJob, level);
                var match = Regex.Match(text, check.Pattern);
                var expected = SamPotency.Of(check.Action, level, check.Combo);
                var actual = match.Success ? float.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
                if (actual != expected)
                {
                    ++failures;
                    Console.WriteLine($"SAM L{level} {check.Action}/{(check.Combo ? "combo" : "base")}: table={expected} client={actual}\n    {text.Replace('\n', '|')}");
                }
            }
        }

        foreach (var (action, pattern, expected) in Constants)
        {
            ++checks;
            var text = NinPotencyAudit.Evaluate(sheet.GetRow((uint)action).Description.ToMacroString(), SamuraiJob, 100);
            var match = Regex.Match(text, pattern);
            var actual = match.Success ? float.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
            if (MathF.Abs(actual - expected) > 0.01f)
            {
                ++failures;
                Console.WriteLine($"SAM constant {action} '{pattern}': table={expected} client={actual}\n    {text.Replace('\n', '|')}");
            }
        }
    }
}
