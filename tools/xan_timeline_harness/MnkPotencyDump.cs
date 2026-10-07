using System;
using BossMod;
using AID = BossMod.MNK.AID;

namespace XanTimelineHarness;

// One-off helper: prints the in-game tooltip for every MNK action, including the raw SeString payloads, so the potency
// table in MnkPotencyScorer can be built from the client's own numbers instead of memory.
internal static class MnkPotencyDump
{
    public static void Run()
    {
        var sheet = Service.LuminaSheet<Lumina.Excel.Sheets.ActionTransient>();
        var actions = Service.LuminaSheet<Lumina.Excel.Sheets.Action>();
        if (sheet == null || actions == null)
        {
            Console.WriteLine("no sheets");
            return;
        }

        foreach (var aid in Enum.GetValues<AID>())
        {
            var id = (uint)aid;
            if (id is 0 or > 40000)
                continue;
            var row = sheet.GetRowOrDefault(id);
            if (row == null)
                continue;
            var act = actions.GetRowOrDefault(id);
            var name = act?.Name.ToString() ?? "?";
            var text = row.Value.Description.ExtractText().Replace("\r", " ").Replace("\n", " | ");

            Console.WriteLine(FormattableString.Invariant($"== {aid} ({id}) {name}"));
            if (act != null)
            {
                var a = act.Value;
                Console.WriteLine(FormattableString.Invariant(
                    $"   meta lvl={a.ClassJobLevel} recast={a.Recast100ms / 10.0} charges={a.MaxCharges} range={a.Range} effectRange={a.EffectRange} castType={a.CastType} cast={a.Cast100ms / 10.0} cdGroup={a.CooldownGroup}/{a.AdditionalCooldownGroup} combo={a.ActionCombo.RowId} preservesCombo={a.PreservesCombo}"));
            }
            Console.WriteLine(FormattableString.Invariant($"   text: {text}"));
            foreach (var payload in row.Value.Description)
            {
                if (payload.Type != Lumina.Text.ReadOnly.ReadOnlySePayloadType.Macro)
                    continue;
                if (payload.MacroCode.ToString() is "ColorType" or "EdgeColorType" or "NewLine")
                    continue;
                Console.Write(FormattableString.Invariant($"   macro={payload.MacroCode} expr="));
                foreach (var expr in payload)
                    Console.Write(FormattableString.Invariant($"[{expr}] "));
                Console.WriteLine();
            }
        }
    }
}

// Scans the whole Action sheet for anything the client assigns to PGL/MNK, so an action added by a patch that the
// AID enum does not know about still shows up.
internal static class MnkActionScan
{
    public static void Run()
    {
        var actions = Service.LuminaSheet<Lumina.Excel.Sheets.Action>();
        if (actions == null)
        {
            Console.WriteLine("no sheets");
            return;
        }
        var known = new System.Collections.Generic.HashSet<uint>();
        foreach (var aid in Enum.GetValues<AID>())
            known.Add((uint)aid);

        foreach (var row in actions)
        {
            var job = row.ClassJob.RowId;
            // 2 = PGL, 20 = MNK
            if (job is not (2 or 20))
                continue;
            if (!row.IsPlayerAction)
                continue;
            var mark = known.Contains(row.RowId) ? " " : "*NEW*";
            Console.WriteLine(FormattableString.Invariant(
                $"{mark} {row.RowId,6} {row.Name,-28} job={job} lvl={row.ClassJobLevel} recast={row.Recast100ms / 10.0} charges={row.MaxCharges} range={row.Range} er={row.EffectRange} castType={row.CastType} cdGroup={row.CooldownGroup}/{row.AdditionalCooldownGroup} isPvP={row.IsPvP}"));
        }
    }
}
