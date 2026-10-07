using System.Diagnostics;
using System.Text.RegularExpressions;
using BossMod.Autorotation.Engine;
using EngineTools;

namespace BlmEngineEval;

// CMA-ES on the xan timeline harness: each candidate weight set is written to a file and the harness runs the engine module
// with ENGINE_WEIGHTS=<file>; the fitness is the summed potency over its timeline matrix (the harness's own damage model).
//   tune-xan --harness <exe> --job blm-engine [--args "timeline-matrix --scenario-limit 4"] [--gens 20] [--pop 10] [--out weights.json]
public static partial class XanTuner
{
    [GeneratedRegex(@"^job=\S+ .*? potency=(\d+) terminal=(\d+)", RegexOptions.Multiline)]
    private static partial Regex ResultLine();

    public static double Evaluate(string harness, string job, string harnessArgs, EngineWeights w, string workDir)
    {
        var file = Path.Combine(workDir, $"w-{Guid.NewGuid():N}.json");
        File.WriteAllText(file, w.ToJson());
        try
        {
            var psi = new ProcessStartInfo(harness, $"{harnessArgs} --job {job}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.Environment["ENGINE_WEIGHTS"] = file;
            psi.Environment["ENGINE_FRAME_MS"] = "1000"; // deterministic: every search runs to completion
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            var total = 0.0;
            foreach (Match m in ResultLine().Matches(stdout.Result))
                total += double.Parse(m.Groups[1].Value) + double.Parse(m.Groups[2].Value);
            return total;
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static int Run(string[] args)
    {
        var harness = Program.Arg(args, "--harness", @"..\xan_timeline_harness\bin\Release\net10.0-windows10.0.26100.0\XanTimelineHarness.exe");
        var job = Program.Arg(args, "--job", "blm-engine");
        var hargs = Program.Arg(args, "--args", "event-timeline --duration 300");
        var gens = int.Parse(Program.Arg(args, "--gens", "20"));
        var pop = int.Parse(Program.Arg(args, "--pop", "10"));
        var outPath = Program.Arg(args, "--out", "tuned/weights-BLM.json");
        var paramSpec = Program.Arg(args, "--params", "");
        var w0 = Program.Weights(args);
        var work = Path.Combine(Path.GetTempPath(), "blm-tune");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);

        // name:lo:hi,...
        var parameters = paramSpec.Length > 0
            ? paramSpec.Split(',').Select(p => p.Split(':')).Select(p => new TunedParameter(p[0], float.Parse(p[1]), float.Parse(p[2]))).ToArray()
            : Tuning.DefaultParameters;
        var scenarios = new[] { hargs };
        var (best, fit, baseline) = Tuning.Tune(w0, parameters, scenarios, (w, a) => Evaluate(harness, job, a, w, work), gens, 1, pop, Console.WriteLine);
        File.WriteAllText(outPath, best.ToJson());
        Console.WriteLine($"baseline={baseline:f0} best={fit:f0} -> {outPath}");
        return 0;
    }
}
