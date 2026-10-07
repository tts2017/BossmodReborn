using System.Globalization;
using System.Text;

namespace MchRegression;

public static class MchReport
{
    public static void Print(IReadOnlyList<MchScenarioVerdict> verdicts)
    {
        var hard = verdicts.SelectMany(v => v.HardFails).ToArray();
        var soft = verdicts.SelectMany(v => v.SoftFails).ToArray();
        var coverage = verdicts.SelectMany(v => v.CoverageGaps.Select(g => (v.Scenario.Name, Gap: g))).ToArray();
        Console.WriteLine($"Scenario count: {verdicts.Count}");
        Console.WriteLine($"Passed: {verdicts.Count(v => v.HardFails.Count == 0 && v.CoverageGaps.Count == 0)}");
        Console.WriteLine($"HardFail count: {hard.Length}");
        Console.WriteLine($"SoftFail count: {soft.Length}");
        Console.WriteLine($"CoverageGap count: {coverage.Length}");

        if (hard.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("HardFail details:");
            foreach (var finding in hard.Take(50))
                PrintFinding(finding);
            if (hard.Length > 50)
                Console.WriteLine($"... {hard.Length - 50} more HardFail entries omitted");
        }

        if (soft.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("SoftFail details:");
            foreach (var finding in soft.Take(50))
                PrintFinding(finding);
            if (soft.Length > 50)
                Console.WriteLine($"... {soft.Length - 50} more SoftFail entries omitted");
        }

        if (coverage.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("CoverageGap details:");
            foreach (var gap in coverage.Take(50))
                Console.WriteLine($"{gap.Name}: {gap.Gap}");
            if (coverage.Length > 50)
                Console.WriteLine($"... {coverage.Length - 50} more CoverageGap entries omitted");
        }
    }

    public static void DumpCsv(string path, IReadOnlyList<MchScenarioVerdict> verdicts)
    {
        var builder = new StringBuilder();
        builder.AppendLine("scenario,time,action,targetCount,heat,battery,overheatLeft,hyperchargedLeft,wildfireLeft,reassembleLeft,excavatorLeft,fmfLeft,cooldowns,charges,note");
        foreach (var verdict in verdicts)
        {
            foreach (var action in verdict.Simulation.Actions)
            {
                builder.Append(Escape(verdict.Scenario.Name)).Append(',')
                    .Append(action.Time.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(Escape(MchActionModel.Name(action.Action))).Append(',')
                    .Append(action.TargetCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(action.Heat.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(action.Battery.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(action.OverheatLeft.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(action.HyperchargedLeft.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(action.WildfireLeft.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(action.ReassembleLeft.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(action.ExcavatorLeft.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(action.FmfLeft.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                    .Append(Escape(action.Cooldowns)).Append(',')
                    .Append(Escape(action.Charges)).Append(',')
                    .Append(Escape(action.Note))
                    .AppendLine();
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        File.WriteAllText(path, builder.ToString());
    }

    private static void PrintFinding(MchRegressionFinding finding)
    {
        Console.WriteLine($"{finding.Rule} | {finding.Scenario} @ {finding.Time:F1}");
        Console.WriteLine($"  State: {finding.StateSummary}");
        Console.WriteLine($"  Expected: {finding.Expected}");
        Console.WriteLine($"  Actual: {finding.Actual}");
        if (finding.LastActions.Count > 0)
            Console.WriteLine($"  Last 10 actions: {string.Join(" > ", finding.LastActions.Select(a => $"{a.Time:F1}:{MchActionModel.Name(a.Action)}"))}");
    }

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n'))
            return value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
