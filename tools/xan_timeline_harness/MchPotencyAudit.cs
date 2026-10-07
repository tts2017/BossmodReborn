using System;
using System.Globalization;
using System.Text.RegularExpressions;
using BossMod;
using AID = BossMod.MCH.AID;

namespace XanTimelineHarness;

// Checks MchPotency - the level-aware 7.5 table the MCH emulator scores with - and the gauge / status rules the emulator applies against
// the client's own action descriptions, the same way VprPotencyAudit does for VPR. The Automaton Queen / Rook Autoturret attacks are
// read from their own (hotbar-less) action rows, at the 50 Battery they are listed for and at the 100 Battery cap.
internal static class MchPotencyAudit
{
    private const int MachinistJob = 31;
    private static readonly int[] Levels = [100, 96, 94, 92, 90, 86, 84, 82, 80, 78, 76, 74, 70, 68, 64, 60, 54, 50, 40];

    private sealed record Check(AID Action, string Pattern, Func<int, float> Expected);

    private const string Potency = @"potency of ([\d,]+)";
    private const string Combo = @"Combo Potency: ([\d,]+)";

    private static float P(AID aid, int level, bool combo = false) => MchPotency.Of(aid, level, combo);

    private static readonly Check[] Checks =
    [
        new(AID.SplitShot, Potency, l => P(AID.SplitShot, l)),
        new(AID.SlugShot, Potency, l => P(AID.SlugShot, l)),
        new(AID.SlugShot, Combo, l => P(AID.SlugShot, l, true)),
        new(AID.CleanShot, Potency, l => P(AID.CleanShot, l)),
        new(AID.CleanShot, Combo, l => P(AID.CleanShot, l, true)),
        new(AID.HeatedSplitShot, Potency, l => P(AID.HeatedSplitShot, l)),
        new(AID.HeatedSlugShot, Potency, l => P(AID.HeatedSlugShot, l)),
        new(AID.HeatedSlugShot, Combo, l => P(AID.HeatedSlugShot, l, true)),
        new(AID.HeatedCleanShot, Potency, l => P(AID.HeatedCleanShot, l)),
        new(AID.HeatedCleanShot, Combo, l => P(AID.HeatedCleanShot, l, true)),
        new(AID.HotShot, Potency, l => P(AID.HotShot, l)),
        new(AID.GaussRound, Potency, l => P(AID.GaussRound, l)),
        new(AID.Ricochet, Potency, l => P(AID.Ricochet, l)),
        new(AID.DoubleCheck, Potency, l => P(AID.DoubleCheck, l)),
        new(AID.Checkmate, Potency, l => P(AID.Checkmate, l)),
        new(AID.SpreadShot, Potency, l => P(AID.SpreadShot, l)),
        new(AID.Scattergun, Potency, l => P(AID.Scattergun, l)),
        new(AID.AutoCrossbow, Potency, l => P(AID.AutoCrossbow, l)),
        new(AID.HeatBlast, Potency, l => P(AID.HeatBlast, l)),
        new(AID.BlazingShot, Potency, l => P(AID.BlazingShot, l)),
        new(AID.Drill, Potency, l => P(AID.Drill, l)),
        new(AID.AirAnchor, Potency, l => P(AID.AirAnchor, l)),
        new(AID.ChainSaw, Potency, l => P(AID.ChainSaw, l)),
        new(AID.Excavator, Potency, l => P(AID.Excavator, l)),
        new(AID.FullMetalField, Potency, l => P(AID.FullMetalField, l)),
        new(AID.Bioblaster, Potency, l => P(AID.Bioblaster, l)),
        new(AID.Bioblaster, @"Potency: (\d+)", _ => MchPotency.BioblasterTick),
        new(AID.Wildfire, @"Potency is increased by (\d+) for each", MchPotency.WildfirePerHit),
        new(AID.GaussRound, @"Maximum Charges: (\d)", l => l >= 74 ? 3 : 2),
        new(AID.Reassemble, @"Maximum Charges: (\d)", l => l >= 84 ? 2 : -1),
        new(AID.Drill, @"Maximum Charges: (\d)", l => l >= 94 ? 2 : -1),
        new(AID.SplitShot, @"Increases Heat Gauge by (\d+)", l => l >= 30 ? 5 : -1),
        new(AID.CleanShot, @"Increases Battery Gauge by (\d+)", l => l >= 40 ? 10 : -1),
        new(AID.HotShot, @"Increases Battery Gauge by (\d+)", l => l >= 40 ? 20 : -1),
        new(AID.Wildfire, @"Can be stacked up to (\d+) times", _ => MchPotency.WildfireMaxHits),
    ];

    // Values the emulator multiplies by or times with, read from the text of the action that grants them (level 100).
    private static readonly (uint Action, string Pattern, float Expected)[] Constants =
    [
        ((uint)AID.Hypercharge, @"Grants (\d) stacks of Overheated", 5),
        ((uint)AID.Hypercharge, @"Duration: (\d+)s", 10),
        ((uint)AID.Hypercharge, @"single-target weaponskills by (\d+)", MchPotency.OverheatedBonus),
        ((uint)AID.Hypercharge, @"Heat Gauge Cost: (\d+)", 50),
        ((uint)AID.BlazingShot, @"Double Check and Checkmate by (\d+)s", 15),
        ((uint)AID.HeatBlast, @"Gauss Round and Ricochet by (\d+)s", 15),
        ((uint)AID.Reassemble, @"Duration: (\d+)s", 5),
        ((uint)AID.Wildfire, @"Duration: (\d+)s", MchPotency.WildfireDuration),
        ((uint)AID.BarrelStabilizer, @"Duration: (\d+)s", 30),
        ((uint)AID.ChainSaw, @"Duration: (\d+)s", 30),
        ((uint)AID.ChainSaw, @"Increases Battery Gauge by (\d+)", 20),
        ((uint)AID.AirAnchor, @"Increases Battery Gauge by (\d+)", 20),
        ((uint)AID.Excavator, @"Increases Battery Gauge by (\d+)", 20),
        ((uint)AID.Scattergun, @"Increases Heat Gauge by (\d+)", 10),
        ((uint)AID.SpreadShot, @"Increases Heat Gauge by (\d+)", 5),
        ((uint)AID.AutomatonQueen, @"Duration: (\d+)s", 12),
        ((uint)AID.AutomatonQueen, @"Battery Gauge Cost: (\d+)", 50),
        ((uint)AID.RookAutoturret, @"Duration: (\d+)s", 9),
        ((uint)AID.Ricochet, @"(\d+)% less for all remaining enemies", (1 - MchPotency.Falloff(AID.Ricochet)) * 100),
        ((uint)AID.DoubleCheck, @"(\d+)% less for all remaining enemies", (1 - MchPotency.Falloff(AID.DoubleCheck)) * 100),
        ((uint)AID.Checkmate, @"(\d+)% less for all remaining enemies", (1 - MchPotency.Falloff(AID.Checkmate)) * 100),
        ((uint)AID.ChainSaw, @"(\d+)% less for all remaining enemies", (1 - MchPotency.Falloff(AID.ChainSaw)) * 100),
        ((uint)AID.Excavator, @"(\d+)% less for all remaining enemies", (1 - MchPotency.Falloff(AID.Excavator)) * 100),
        ((uint)AID.FullMetalField, @"(\d+)% less for all remaining enemies", (1 - MchPotency.Falloff(AID.FullMetalField)) * 100),
        ((uint)AID.Bioblaster, @"Duration: (\d+)s", MchPotency.BioblasterDuration),
        ((uint)AID.Flamethrower, @"Potency: (\d+)", MchPotency.FlamethrowerTick),
        ((uint)AID.Flamethrower, @"Duration: (\d+)s", MchPotency.FlamethrowerDuration),
        (MchPotency.ArmPunch, Potency, MchPotency.PetAttack(MchPotency.ArmPunch)),
        (MchPotency.ArmPunch, @"up to a maximum of ([\d,]+)", 2 * MchPotency.PetAttack(MchPotency.ArmPunch)),
        (MchPotency.PileBunker, Potency, MchPotency.PetAttack(MchPotency.PileBunker)),
        (MchPotency.PileBunker, @"up to a maximum of ([\d,]+)", 2 * MchPotency.PetAttack(MchPotency.PileBunker)),
        (MchPotency.RollerDash, Potency, MchPotency.PetAttack(MchPotency.RollerDash)),
        (MchPotency.RollerDash, @"up to a maximum of ([\d,]+)", 2 * MchPotency.PetAttack(MchPotency.RollerDash)),
        (MchPotency.CrownedCollider, Potency, MchPotency.PetAttack(MchPotency.CrownedCollider)),
        (MchPotency.CrownedCollider, @"up to a maximum of ([\d,]+)", 2 * MchPotency.PetAttack(MchPotency.CrownedCollider)),
        ((uint)AID.RookAutoturret, @"potency of (\d+)", MchPotency.PetAttack(MchPotency.VolleyFire)),
        ((uint)AID.RookAutoturret, @"up to a maximum of (\d+)", 2 * MchPotency.PetAttack(MchPotency.VolleyFire)),
        (MchPotency.RookOverload, Potency, MchPotency.PetAttack(MchPotency.RookOverload)),
        (MchPotency.RookOverload, @"up to a maximum of ([\d,]+)", 2 * MchPotency.PetAttack(MchPotency.RookOverload)),
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
                var text = NinPotencyAudit.Evaluate(sheet.GetRow((uint)check.Action).Description.ToMacroString(), MachinistJob, level);
                var match = Regex.Match(text, check.Pattern);
                var expected = check.Expected(level);
                var actual = match.Success ? float.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
                if (actual != expected)
                {
                    ++failures;
                    Console.WriteLine($"MCH L{level} {check.Action} '{check.Pattern}': table={expected} client={actual}\n    {text.Replace('\n', '|')}");
                }
            }
        }

        foreach (var (action, pattern, expected) in Constants)
        {
            ++checks;
            var text = NinPotencyAudit.Evaluate(sheet.GetRow(action).Description.ToMacroString(), MachinistJob, 100);
            var match = Regex.Match(text, pattern);
            var actual = match.Success ? float.Parse(match.Groups[1].Value.Replace(",", ""), CultureInfo.InvariantCulture) : -1;
            if (MathF.Abs(actual - expected) > 0.01f)
            {
                ++failures;
                Console.WriteLine($"MCH constant {action} '{pattern}': table={expected} client={actual}\n    {text.Replace('\n', '|')}");
            }
        }
    }
}
