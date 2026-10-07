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
            if (f.SelectedGcd is "Communio" or "Perfectio" or "HarvestMoon" && !f.BestRangedAoeTargetAvailable) yield return $"ranged_no_target:{f.SelectedGcd} t={f.Time:f1} {r.Scenario.Name}";
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
            var red = Math.Min(100, f.RedGauge + (f.SelectedGcd switch { "SoulSlice" or "SoulScythe" => 50, "Slice" or "SpinningScythe" or "Harpe" or "HarvestMoon" => 10, "WaxingSlice" when f.ComboLast == "Slice" => 10, "InfernalSlice" when f.ComboLast == "WaxingSlice" => 10, "NightmareScythe" when f.ComboLast == "SpinningScythe" => 10, _ => 0 }));
            if (f.SelectedOgcds.Any(a => a is "Gluttony" or "BloodStalk" or "UnveiledGibbet" or "UnveiledGallows" or "GrimSwathe") && red < 50) yield return $"soul_spender_below_50: gcd {f.SelectedGcd} red {f.RedGauge} combo {f.ComboLast} t={f.Time:f1} {r.Scenario.Name}";
            if (f.SelectedOgcds.Contains("Potion") && f.PotionReadyIn > 0.1) yield return "potion_on_cd";
            if (!f.FallbackTargetAvailable && f.SelectedGcd is not (null or "Soulsow" or "HarvestMoon" or "Communio" or "Perfectio" or "PlentifulHarvest" or "SpinningScythe" or "NightmareScythe" or "WhorlOfDeath" or "SoulScythe" or "Guillotine" or "ExecutionersGuillotine" or "GrimReaping")) yield return $"single_target_without_target: {f.SelectedGcd} t={f.Time:f1} {r.Scenario.Name}";
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

public static class DiagnoseBuckets
{
    public static IEnumerable<string> Dd(ScenarioResult r)
    {
        foreach (var f in r.Frames)
        {
            var ctx = f.BlueSouls > 0 ? "enshroud" : f.ArcaneCircleLeft > 0 ? "ac" : null;
            if (ctx != null && f.TargetAvailable && f.DeathsDesignLeft <= 0 && f.SelectedGcd is not ("ShadowOfDeath" or "WhorlOfDeath"))
                yield return $"dd_down_in_{ctx} (gcd {f.SelectedGcd}, reaver {f.ReaverState}, t={f.Time:f0})";
            if (f.SelectedGcd is "ShadowOfDeath" or "WhorlOfDeath" && f.BlueSouls > 0)
                yield return "dd_refresh_in_enshroud";
        }
    }

    public static IEnumerable<string> Burst(ScenarioResult r)
    {
        var sc = r.Scenario;
        var evenAc = r.Frames.FirstOrDefault(f => f.SelectedOgcds.Contains("ArcaneCircle") && f.Time >= 110);
        if (sc.InitialMode == RotationMode.Full && sc.KillTime >= 130 && evenAc == null && !sc.Events.Any(e => e.Type == ScenarioEventType.TargetLost && e.Start <= 120 && e.End >= 120))
            yield return $"burst_no_even_ac (first AC uses: {string.Join(",", r.Frames.Where(f => f.SelectedOgcds.Contains("ArcaneCircle")).Select(f => f.Time.ToString("f0")))})";
        foreach (var f in r.Frames)
        {
            if (f.SelectedOgcds.Contains("Potion") && !f.SelectedOgcds.Contains("ArcaneCircle") && f.ArcaneCircleLeft <= 0 && f.ArcaneCircleReadyIn > sc.Gcd * 3)
                yield return "burst_potion_without_ac";
            if (f.PerfectioParata && f.SelectedGcd == null && f.TargetAvailable)
                yield return "burst_parata_idle";
        }
        var anchor = evenAc?.Time ?? 120;
        var ev = r.Frames.Where(f => f.Time >= anchor - 45 && f.Time <= anchor + 40).ToList();
        if (sc.KillTime >= 150)
        {
            if (sc.ExpectDoubleEnshroud && ev.Count(f => f.SelectedOgcds.Contains("Enshroud")) < 2) yield return $"burst_even_double_enshroud (got {ev.Count(f => f.SelectedOgcds.Contains("Enshroud"))})";
            if (sc.ExpectTwoCommunio && ev.Count(f => f.SelectedGcd == "Communio") < 2) yield return "burst_even_two_communio";
            if (sc.ExpectPerfectio && ev.All(f => f.SelectedGcd != "Perfectio")) yield return "burst_even_perfectio";
            if (sc.ExpectLemure && ev.All(f => !f.SelectedOgcds.Any(a => a is "LemuresSlice" or "LemuresScythe"))) yield return "burst_even_lemure";
            if (sc.ExpectSacrificium && ev.All(f => !f.SelectedOgcds.Contains("Sacrificium"))) yield return "burst_even_sacrificium";
        }
    }

    // mirrors the main generic rules of the harness' gauge / drift / DD checks, one line per triggering frame kind
    public static IEnumerable<string> Gauge(ScenarioResult r)
    {
        foreach (var f in r.Frames)
        {
            if (f.TargetAvailable && f.FallbackTargetAvailable && (f.MeleeAvailable || f.BestConeTargetAvailable) && f.RedGauge >= 100 && f.SelectedOgcds.Count == 0 && f.RotationMode == RotationMode.Full && f.ReaverState == ReaverState.None && f.BlueSouls == 0 && f.Level >= 50 && f.ArcaneCircleReadyIn > 10)
                yield return $"gauge_red100_no_spend (gcd {f.SelectedGcd}, ac in {f.ArcaneCircleReadyIn:f0}, gl {f.ComboLast})";
            if (f.SelectedGcd is "SoulSlice" or "SoulScythe" && f.RedGauge > 50)
                yield return $"gauge_soulslice_overcaps (red {f.RedGauge})";
            if (f.TargetAvailable && f.Level >= 78 && f.SoulSliceCharges >= 2 && f.SelectedGcd is not ("SoulSlice" or "SoulScythe" or "ShadowOfDeath" or "WhorlOfDeath") && f.RotationMode == RotationMode.Full && f.ReaverState == ReaverState.None && f.BlueSouls == 0 && !f.PerfectioParata)
                yield return $"gauge_soulslice_charge_cap (gcd {f.SelectedGcd}, red {f.RedGauge})";
        }
        if (r.Scenario.ExpectSecondEnshroudBlueGauge)
        {
            var pre = r.Frames.Where(f => f.Time >= 118 && f.Time <= 135 + r.Scenario.Gcd).ToList();
            if (pre.Count > 0 && pre.All(f => f.BlueGauge < 50 && !f.SelectedOgcds.Contains("Enshroud") && !f.IdealHost))
                yield return "gauge_second_even_enshroud_blue_short";
        }
    }
}
