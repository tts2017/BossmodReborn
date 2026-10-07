using System;
using BossMod.Autorotation;

namespace XanTimelineHarness;

internal static partial class MechanicHintsSelfTest
{
    private static readonly (string Job, Func<RotationModuleDefinition> Definition)[] HintedModules =
    [
        ("blm", BossMod.Autorotation.xan.Custom.BLM.Definition),
        ("rpr", BossMod.Autorotation.xan.Custom.RPR.Definition),
        ("mnk", BossMod.Autorotation.xan.Custom.MNK.Definition),
        ("nin", BossMod.Autorotation.xan.Custom.NIN.Definition),
        ("vpr", BossMod.Autorotation.xan.Custom.VPR.Definition),
        ("mch", BossMod.Autorotation.xan.Custom.MCH.Definition),
        ("gnb", BossMod.Autorotation.akechi.Custom.AkechiGNB.Definition),
        ("pld", BossMod.Autorotation.akechi.Custom.AkechiPLD.Definition),
    ];

    private static partial void RunJobTests()
    {
        foreach (var (job, definition) in HintedModules)
        {
            var configs = definition().Configs;
            var track = configs.Find(c => c.InternalName == "MechanicHints") as StrategyConfigTrack;
            Check(track != null && track.Options.Count == 4 && track.Options[0].InternalName == "All" && track.Options[3].InternalName == "Off", $"track-{job}", "MechanicHints track with All..Off must exist");
        }
        RunJobDecisionTests();
    }

    private static partial void RunJobDecisionTests();
}
