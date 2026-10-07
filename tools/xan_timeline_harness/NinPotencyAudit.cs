using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BossMod;
using BossMod.Autorotation.xan;
using AID = BossMod.NIN.AID;

namespace XanTimelineHarness;

// Checks NinPotency - the level-aware 7.5 table the NIN burst planner scores with - against the client's own action descriptions.
// The descriptions branch on job (gnum68) and level (gnum72), so each one is evaluated for NIN at every audited level and the number
// after each label is compared with the table. A mismatch prints the evaluated text, so a patch that moves a value shows up here.
internal static class NinPotencyAudit
{
    private const int NinjaJob = 30;
    private static readonly int[] Levels = [100, 94, 93, 90, 88, 84, 80, 78, 74, 70];

    // Optional checks fall back to the Base value when the label is absent at that level (e.g. Meisui's bonus before level 88).
    private sealed record Check(AID Action, NinPotencyVariant Variant, string Pattern, float Scale = 1, bool Optional = false);

    private const string Potency = @"potency of ([\d,]+)";
    private const string Combo = @"(?<!Rear |Flank )Combo Potency: ([\d,]+)";

    private static readonly Check[] Checks =
    [
        new(AID.SpinningEdge, NinPotencyVariant.Base, Potency),
        new(AID.GustSlash, NinPotencyVariant.Base, Potency),
        new(AID.GustSlash, NinPotencyVariant.Combo, Combo),
        new(AID.AeolianEdge, NinPotencyVariant.Base, Potency),
        new(AID.AeolianEdge, NinPotencyVariant.Positional, @"([\d,]+) when executed from a target's rear"),
        new(AID.AeolianEdge, NinPotencyVariant.Combo, Combo),
        new(AID.AeolianEdge, NinPotencyVariant.ComboPositional, @"Rear Combo Potency: ([\d,]+)"),
        new(AID.ArmorCrush, NinPotencyVariant.Base, Potency),
        new(AID.ArmorCrush, NinPotencyVariant.Positional, @"([\d,]+) when executed from a target's flank"),
        new(AID.ArmorCrush, NinPotencyVariant.Combo, Combo),
        new(AID.ArmorCrush, NinPotencyVariant.ComboPositional, @"Flank Combo Potency: ([\d,]+)"),
        new(AID.DeathBlossom, NinPotencyVariant.Base, Potency),
        new(AID.HakkeMujinsatsu, NinPotencyVariant.Base, Potency),
        new(AID.HakkeMujinsatsu, NinPotencyVariant.Combo, Combo),
        new(AID.ThrowingDagger, NinPotencyVariant.Base, Potency),
        new(AID.Mug, NinPotencyVariant.Base, Potency),
        new(AID.Dokumori, NinPotencyVariant.Base, Potency),
        new(AID.TrickAttack, NinPotencyVariant.Base, Potency),
        new(AID.TrickAttack, NinPotencyVariant.Positional, @"([\d,]+) when executed from a target's rear"),
        new(AID.KunaisBane, NinPotencyVariant.Base, Potency),
        new(AID.FumaShuriken, NinPotencyVariant.Base, Potency),
        new(AID.FumaTen, NinPotencyVariant.Base, Potency),
        new(AID.Raiton, NinPotencyVariant.Base, Potency),
        new(AID.TCJRaiton, NinPotencyVariant.Base, Potency),
        new(AID.Katon, NinPotencyVariant.Base, Potency),
        new(AID.TCJKaton, NinPotencyVariant.Base, Potency),
        new(AID.Hyoton, NinPotencyVariant.Base, Potency),
        new(AID.Huton, NinPotencyVariant.Base, Potency),
        new(AID.TCJHuton, NinPotencyVariant.Base, Potency),
        new(AID.Suiton, NinPotencyVariant.Base, Potency),
        new(AID.TCJSuiton, NinPotencyVariant.Base, Potency),
        new(AID.Doton, NinPotencyVariant.Base, Potency),
        new(AID.HyoshoRanryu, NinPotencyVariant.Base, Potency),
        new(AID.GokaMekkyaku, NinPotencyVariant.Base, Potency),
        new(AID.Assassinate, NinPotencyVariant.Base, Potency),
        new(AID.DreamWithinADream, NinPotencyVariant.Base, @"each hit with a potency of ([\d,]+)", Scale: 3),
        new(AID.HellfrogMedium, NinPotencyVariant.Base, Potency),
        new(AID.Bhavacakra, NinPotencyVariant.Base, Potency),
        new(AID.Bhavacakra, NinPotencyVariant.Meisui, @"Potency is increased to ([\d,]+) when under the effect of Meisui", Optional: true),
        new(AID.DeathfrogMedium, NinPotencyVariant.Base, Potency),
        new(AID.ZeshoMeppo, NinPotencyVariant.Base, Potency),
        new(AID.ZeshoMeppo, NinPotencyVariant.Meisui, @"Meisui Bonus: Potency is increased to ([\d,]+)"),
        new(AID.TenriJindo, NinPotencyVariant.Base, Potency),
        new(AID.PhantomKamaitachi, NinPotencyVariant.Base, Potency),
        new(AID.FleetingRaiju, NinPotencyVariant.Base, Potency),
        new(AID.ForkedRaiju, NinPotencyVariant.Base, Potency),
    ];

    // Constants the planner multiplies by, read from the text of the action that grants them.
    private static readonly (AID Action, string Pattern, float Expected)[] Constants =
    [
        (AID.Kassatsu, @"Increases damage for the next ninjutsu action by (\d+)%", (NinPotency.KassatsuBonus - 1) * 100),
        (AID.KunaisBane, @"Increases damage you deal target by (\d+)%", (NinPotency.KunaiBonus - 1) * 100),
        (AID.TrickAttack, @"Increases damage you deal target by (\d+)%", (NinPotency.KunaiBonus - 1) * 100),
        (AID.Dokumori, @"Increases target's damage taken by (\d+)%", (NinPotency.DokumoriBonus - 1) * 100),
        (AID.AeolianEdge, @"Potencies are increased by (\d+) while under the effect of Kazematoi", NinPotency.KazematoiBonus),
        (AID.Bunshin, @"Melee Attack Potency: (\d+)", NinPotency.BunshinMelee),
        (AID.Bunshin, @"Area Attack Potency: (\d+)", NinPotency.BunshinArea),
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
                var text = Evaluate(sheet.GetRow((uint)check.Action).Description.ToMacroString(), NinjaJob, level);
                var match = Regex.Match(text, check.Pattern);
                var expected = NinPotency.Of(check.Action, level, check.Variant);
                var actual = match.Success ? ParseNumber(match.Groups[1].Value) * check.Scale
                    : check.Optional ? NinPotency.Of(check.Action, level, NinPotencyVariant.Base) : -1;
                if (actual != expected)
                {
                    ++failures;
                    Console.WriteLine($"NIN L{level} {check.Action}/{check.Variant}: table={expected} client={actual}\n    {text.Replace('\n', '|')}");
                }
            }
        }

        foreach (var (action, pattern, expected) in Constants)
        {
            ++checks;
            var text = Evaluate(sheet.GetRow((uint)action).Description.ToMacroString(), NinjaJob, 100);
            var match = Regex.Match(text, pattern);
            var actual = match.Success ? ParseNumber(match.Groups[1].Value) : -1;
            if (MathF.Abs(actual - expected) > 0.01f)
            {
                ++failures;
                Console.WriteLine($"NIN constant {action} '{pattern}': table={expected} client={actual}\n    {text.Replace('\n', '|')}");
            }
        }
    }

    private static float ParseNumber(string s) => float.Parse(s.Replace(",", ""), CultureInfo.InvariantCulture);

    // Evaluates the <if([gnumNN op value],A,B)> branches of a description for one job and level, turns <br> into newlines and drops the
    // other tags. "\," inside a branch is an escaped literal comma; a bare comma separates the branches.
    public static string Evaluate(string macro, int job, int level)
    {
        var i = 0;
        return EvaluateSequence(macro, ref i, job, level, insideBranch: false);
    }

    private static string EvaluateSequence(string s, ref int i, int job, int level, bool insideBranch)
    {
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            if (insideBranch && (s[i] == ',' || string.CompareOrdinal(s, i, ")>", 0, 2) == 0))
                break;
            if (string.CompareOrdinal(s, i, "<if(", 0, 4) == 0)
            {
                i += 4;
                if (s[i] != '[')
                    throw new FormatException($"expected '[' at {i}: {s}");
                var conditionEnd = s.IndexOf(']', i);
                var condition = s.Substring(i + 1, conditionEnd - i - 1);
                i = conditionEnd + 1;
                Expect(s, ref i, ",");
                var whenTrue = EvaluateSequence(s, ref i, job, level, insideBranch: true);
                Expect(s, ref i, ",");
                var whenFalse = EvaluateSequence(s, ref i, job, level, insideBranch: true);
                Expect(s, ref i, ")>");
                sb.Append(EvaluateCondition(condition, job, level) ? whenTrue : whenFalse);
            }
            else if (s[i] == '\\' && i + 1 < s.Length)
            {
                sb.Append(s[i + 1]);
                i += 2;
            }
            else if (s[i] == '<')
            {
                var close = s.IndexOf('>', i);
                if (close < 0)
                    throw new FormatException($"unterminated tag at {i}: {s}");
                if (string.CompareOrdinal(s, i, "<br>", 0, 4) == 0)
                    sb.Append('\n');
                i = close + 1;
            }
            else
            {
                sb.Append(s[i++]);
            }
        }
        return sb.ToString();
    }

    private static void Expect(string s, ref int i, string token)
    {
        if (string.CompareOrdinal(s, i, token, 0, token.Length) != 0)
            throw new FormatException($"expected '{token}' at {i}: {s}");
        i += token.Length;
    }

    private static bool EvaluateCondition(string condition, int job, int level)
    {
        var m = Regex.Match(condition, @"^gnum(\d+)\s*(==|!=|>=|<=|>|<)\s*(\d+)$");
        if (!m.Success)
            throw new FormatException($"unsupported condition '{condition}'");
        var value = m.Groups[1].Value switch
        {
            "68" => job,
            "72" => level,
            _ => throw new FormatException($"unsupported variable in '{condition}'")
        };
        var rhs = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch
        {
            "==" => value == rhs,
            "!=" => value != rhs,
            ">=" => value >= rhs,
            "<=" => value <= rhs,
            ">" => value > rhs,
            _ => value < rhs
        };
    }
}
