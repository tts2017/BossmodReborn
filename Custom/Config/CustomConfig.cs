namespace BossMod;

// Settings that the local fork used to add to upstream config nodes (BossModuleConfig, ActionTweaksConfig).
// Kept in a node of our own so upstream config files stay untouched; serialized under its own key in the config file.
// Note: values saved by the old fork under BossModuleConfig/ActionTweaksConfig are not migrated automatically.
[ConfigDisplay(Name = "Custom (local fork)", Order = 90)]
public sealed class CustomConfig : ConfigNode
{
    // --- formerly BossModuleConfig ---
    [PropertyDisplay("Use external planner timelines for modules without a state machine (experimental)", tooltip: "Replaces the trivial state machine of matching modules with a timeline learned from external data. Can produce overdue-transition errors and synthetic downtime hints when a fight diverges from the recorded timeline.")]
    public bool UseExternalPlannerTimelines = false;

    [PropertyDisplay("Tell rotations about downtime from imported fight timelines", tooltip: "Follows the imported timeline of the current duty by matching boss casts, and tells rotations when the target will be untargetable and when it returns. Only applies where the loaded module has no state machine of its own, and only for downtime of 8.5 s or more while the timeline is confirmed by a recent cast.")]
    public bool UseExternalTimelineHints = true;

    [PropertyDisplay("Folder with extra fight timelines", tooltip: "JSON files in this folder are loaded next to the built-in timelines. Replay extractions land here. Empty = <plugin config>/timelines.", depends: nameof(UseExternalTimelineHints))]
    public string TimelineUserDirectory = "";

    [PropertyDisplay("Build fight timelines from your replays automatically", tooltip: "After each recorded duty, summarizes the replay in the background (waiting while you are in combat) and rebuilds that content's timeline from all your replays of it. Only windows that most of your pulls reproduce are used; hand-placed timeline files always win.", depends: nameof(UseExternalTimelineHints))]
    public bool AutoExtractTimelines = true;

    [PropertyDisplay("Avoid what Splatoon is drawing", tooltip: "Reads the shapes Splatoon has on screen right now over its plugin interface, so its own layouts, triggers and scripts decide when a shape applies. Tagged layouts keeps the shapes whose layout or element name contains the tag below; danger coloured keeps the red shapes presets use for danger; everything also avoids safe spots, markers and helper shapes.")]
    public SplatoonLiveZoneMode SplatoonLiveZones = SplatoonLiveZoneMode.Off;

    [PropertyDisplay("Tag that marks Splatoon layouts to avoid", tooltip: "Used by the tagged layouts mode: put this text anywhere in the name of a Splatoon layout, element or script to have its shapes avoided.", depends: nameof(SplatoonLiveZones))]
    public string SplatoonLiveZoneTag = "bmr";

    [PropertyDisplay("Predict disengages from telegraphed mechanics for autorotation", tooltip: "Turns telegraphed AOEs, baited puddles, stacks and other forbidden zones into predictions of when you have to move and when the target will be out of your attack range. While an autorotation preset is active, the action queue stops starting casts that cannot finish before you have to move. Disengages long enough to count as downtime (8.5 s or more) are also passed to rotations that read mechanic hints (BLM, RPR).")]
    public bool PredictDisengage = true;

    // --- formerly ActionTweaksConfig ---
    [PropertyDisplay("Enable in-process rotation AI second opinion (experimental)", tooltip: "Lets the local rotation AI (LocalRotationAI, MchRealtimeValuePlanner, FunctionGemma supervisor) override the action selected by the queue. Off by default; models and endpoints are configured with BOSSMOD_* process environment variables.")]
    public bool EnableLocalRotationAI = false;
}
