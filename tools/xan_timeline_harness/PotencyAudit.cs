using System;
using System.Globalization;
using System.Text;
using BossMod;
using BossMod.Autorotation;

namespace XanTimelineHarness;

// Checks WindDownPotency against the client's own action descriptions (ActionTransient, English). The descriptions carry level-dependent
// values as macros, e.g. "potency of <if([gnum68==30],<if([gnum72>=94],740,650)>,650)>": the first literal after the label is the
// max-level value, so the macro string is read instead of the rendered text. Prints the description of every mismatch.
internal static class PotencyAudit
{
    public static int Run()
    {
        var sheet = Service.LuminaSheet<Lumina.Excel.Sheets.ActionTransient>()!;
        // POTENCY_DUMP=<id>,<id>... prints the macro descriptions of arbitrary actions (to fill in the table), then exits
        if (Environment.GetEnvironmentVariable("POTENCY_DUMP") is { Length: > 0 } dump)
        {
            foreach (var id in dump.Split(','))
                Console.WriteLine($"{id}: {sheet.GetRow(uint.Parse(id, CultureInfo.InvariantCulture)).Description.ToMacroString()}");
            return 0;
        }
        // STATUS_DUMP=<id>,<id>... prints the name and description of status effects (what ends them, stack limits), then exits
        if (Environment.GetEnvironmentVariable("STATUS_DUMP") is { Length: > 0 } statusDump)
        {
            var statuses = Service.LuminaSheet<Lumina.Excel.Sheets.Status>()!;
            foreach (var id in statusDump.Split(','))
            {
                var row = statuses.GetRow(uint.Parse(id, CultureInfo.InvariantCulture));
                Console.WriteLine($"{id}: {row.Name} maxStacks={row.MaxStacks} :: {row.Description.ToMacroString()}");
            }
            return 0;
        }
        // ACTION_DUMP=<id>,<id>... prints how the action definitions see arbitrary actions (recast, cooldown groups, charges, cast), then exits
        if (Environment.GetEnvironmentVariable("ACTION_DUMP") is { Length: > 0 } actionDump)
        {
            foreach (var id in actionDump.Split(','))
            {
                var def = ActionDefinitions.Instance[new ActionID(ActionType.Spell, uint.Parse(id, CultureInfo.InvariantCulture))];
                Console.WriteLine(def == null ? $"{id}: not registered"
                    : FormattableString.Invariant($"{id}: {def.ID} gcd={def.IsGCD} cooldown={def.Cooldown} main={def.MainCooldownGroup} extra={def.ExtraCooldownGroup} charges={def.MaxChargesAtCap()} cast={def.CastTime} range={def.Range} minLevel={def.MinLevel}"));
            }
            return 0;
        }
        // PLD_DEFS=1 prints the Paladin action data the emulator relies on (recast, charges, cast, MP cost, combo behaviour), then exits
        if (Environment.GetEnvironmentVariable("PLD_DEFS") == "1")
        {
            PldPotencyAudit.DumpDefs();
            return 0;
        }
        var failures = 0;
        var checks = 0;
        foreach (var (job, table) in new (string, WindDownPotency.Entry[])[] { ("NIN", WindDownPotency.NIN), ("VPR", WindDownPotency.VPR), ("MCH", WindDownPotency.MCH), ("GNB", WindDownPotency.GNB), ("PLD", WindDownPotency.PLD) })
        {
            foreach (var e in table)
            {
                ++checks;
                var macro = sheet.GetRow(e.Action).Description.ToMacroString();
                var label = e.Label;
                var actual = ReadPotency(macro, label);
                if (actual != e.Direct)
                {
                    ++failures;
                    Console.WriteLine($"{job} {e.Action} ({label}): table={e.Direct} client={actual}\n    {macro}");
                }
            }
        }
        NinPotencyAudit.Run(sheet, ref checks, ref failures);
        DrgPotencyAudit.Run(sheet, ref checks, ref failures);
        SamPotencyAudit.Run(sheet, ref checks, ref failures);
        VprPotencyAudit.Run(sheet, ref checks, ref failures);
        MchPotencyAudit.Run(sheet, ref checks, ref failures);
        PldPotencyAudit.Run(sheet, ref checks, ref failures);
        Console.WriteLine($"potency_audit checks={checks} failures={failures}");
        return failures == 0 ? 0 : 3;
    }

    // First numeric literal after the label, skipping colour tags and the opening of level/class branches.
    public static float ReadPotency(string macro, string label)
    {
        var i = macro.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return -1;
        i += label.Length;
        var insideBranch = false;
        while (i < macro.Length)
        {
            if (char.IsWhiteSpace(macro[i]))
            {
                ++i;
            }
            else if (string.CompareOrdinal(macro, i, "<if(", 0, 4) == 0)
            {
                var cond = macro.IndexOf("],", i, StringComparison.Ordinal);
                if (cond < 0)
                    return -1;
                i = cond + 2;
                insideBranch = true;
            }
            else if (macro[i] == '<')
            {
                var close = macro.IndexOf('>', i);
                if (close < 0)
                    return -1;
                i = close + 1;
            }
            else
            {
                break;
            }
        }
        // thousands separators: "\," inside macro arguments (a bare ',' there separates branches), "," + 3 digits in plain text
        var digits = new StringBuilder();
        while (i < macro.Length)
        {
            if (char.IsDigit(macro[i]))
            {
                digits.Append(macro[i++]);
            }
            else if (macro[i] == '\\' && i + 1 < macro.Length && macro[i + 1] == ',')
            {
                i += 2;
            }
            else if (!insideBranch && macro[i] == ',' && i + 3 < macro.Length && char.IsDigit(macro[i + 1]) && char.IsDigit(macro[i + 2]) && char.IsDigit(macro[i + 3]))
            {
                ++i;
            }
            else
            {
                break;
            }
        }
        return digits.Length > 0 ? float.Parse(digits.ToString(), CultureInfo.InvariantCulture) : -1;
    }
}
