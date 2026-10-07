using RprRegression;
using ScenarioResult = RprRegression.ScenarioResult;

namespace RprEngineEval;

// which of the harness' illegal-action / reaver checks fire (a subset mirrored from HardFailRules for diagnosis)
public static class Diagnose
{
    public static IEnumerable<string> Illegal(ScenarioResult r)
    {
        foreach (var f in r.Frames)
        {
            if (f.SelectedGcd != null && !RprRotationEmulator.Unlocked(f.Level, f.SelectedGcd)) yield return "locked:" + f.SelectedGcd;
            foreach (var o in f.SelectedOgcds) if (!RprRotationEmulator.Unlocked(f.Level, o)) yield return "locked:" + o;
            if (f.SelectedGcd == "PlentifulHarvest" && !f.BestLineTargetAvailable) yield return "ph_no_line_target";
            if (f.SelectedGcd is "Communio" or "Perfectio" or "HarvestMoon" && !f.BestRangedAoeTargetAvailable) yield return "ranged_no_target:" + f.SelectedGcd;
            if (f.BlueSouls == 0 && f.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" or "Communio") yield return "reaping_without_enshroud";
            if (f.ReaverState == ReaverState.None && f.SelectedGcd is "Gibbet" or "Gallows" or "Guillotine" or "ExecutionersGibbet" or "ExecutionersGallows" or "ExecutionersGuillotine") yield return "reaver_gcd_without_reaver";
            var purple = f.PurpleSouls + (f.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" ? 1 : 0);
            var blue = f.BlueSouls - (f.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping" ? 1 : 0);
            if (f.SelectedOgcds.Any(a => a is "LemuresSlice" or "LemuresScythe") && (purple < 2 || blue <= 0)) yield return "lemures_illegal";
            if (HardFailRulesAccess.AvatarDuringEnshroud(f)) yield return "avatar_during_enshroud";
            var ens = f.SelectedOgcds.ToList().IndexOf("Enshroud");
            var sac = f.SelectedOgcds.ToList().IndexOf("Sacrificium");
            if (sac >= 0 && (!f.Oblatio && !(ens >= 0 && sac > ens) || blue <= 0 && !(ens >= 0 && sac > ens))) yield return "sacrificium_illegal";
            if (f.TargetAvailable && f.FallbackTargetAvailable && f.MeleeAvailable && f.ReaverState != ReaverState.None && !(f.ConeTargets > 3 && f.BestConeTargetAvailable))
            {
                var ex = f.ReaverState == ReaverState.Executioner && f.Level >= 96;
                var expected = f.EnhancedGallowsLeft > 0 ? ex ? "ExecutionersGallows" : "Gallows" : f.EnhancedGibbetLeft > 0 ? ex ? "ExecutionersGibbet" : "Gibbet" : null;
                if (expected != null && f.SelectedGcd != expected) yield return $"reaver_seq: got {f.SelectedGcd ?? "none"} expected {expected} (gibbet {f.EnhancedGibbetLeft:f0} gallows {f.EnhancedGallowsLeft:f0} cone {f.ConeTargets})";
            }
            if (f.SelectedGcd == "PlentifulHarvest" && !f.PlentifulHarvestReady) yield return "ph_not_ready";
            if (f.SelectedGcd == "Perfectio" && !f.PerfectioParata) yield return "perfectio_not_ready";
            if (f.SelectedGcd is "SoulSlice" or "SoulScythe" && f.SoulSliceCharges < 1) yield return "soulslice_no_charge";
            if (f.SelectedOgcds.Contains("Enshroud") && f.BlueGauge < 50 && !f.IdealHost && f.SelectedGcd != "PlentifulHarvest") yield return "enshroud_no_gauge";
        }
    }
}

internal static class HardFailRulesAccess
{
    public static bool AvatarDuringEnshroud(ActionFrame f)
    {
        var enshrouded = f.BlueSouls > 0 && f.SelectedGcd != "Communio" && !(f.BlueSouls == 1 && f.SelectedGcd is "VoidReaping" or "CrossReaping" or "GrimReaping");
        foreach (var a in f.SelectedOgcds)
        {
            if (a == "Enshroud") enshrouded = true;
            else if (enshrouded && a is "Gluttony" or "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe") return true;
        }
        return false;
    }
}
