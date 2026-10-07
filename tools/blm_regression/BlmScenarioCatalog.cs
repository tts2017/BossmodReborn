namespace BlmRegression;

public static class BlmScenarioCatalog
{
    private static readonly (string Name, BlmScenarioCategory Category)[] BaseScenarios =
    [
        ("FullUptime_100_6min", BlmScenarioCategory.FullUptime),
        ("FullUptime_100_10min", BlmScenarioCategory.FullUptime),
        ("FullUptime_90_6min", BlmScenarioCategory.FullUptime),
        ("FullUptime_80_6min", BlmScenarioCategory.FullUptime),
        ("FullUptime_70_6min", BlmScenarioCategory.FullUptime),
        ("FullUptime_60_6min", BlmScenarioCategory.FullUptime),
        ("FullUptime_50_6min", BlmScenarioCategory.FullUptime),
        ("Standard57Opener_FullUptime", BlmScenarioCategory.OpenerBurstResource),
        ("Standard57Opener_TargetLossAfterManafont", BlmScenarioCategory.OpenerBurstResource),
        ("Standard57Opener_ForcedMoveBeforeManafont", BlmScenarioCategory.OpenerBurstResource),
        ("NearManafont_AF_LowMP", BlmScenarioCategory.OpenerBurstResource),
        ("NearManafont_DowntimeSoon", BlmScenarioCategory.OpenerBurstResource),
        ("Amplifier_PolyglotNearCap", BlmScenarioCategory.OpenerBurstResource),
        ("Polyglot_Overcap_ThreeStacks", BlmScenarioCategory.OpenerBurstResource),
        ("Manafont_Force", BlmScenarioCategory.OpenerBurstResource),
        ("Manafont_HoldBurstEncounter", BlmScenarioCategory.OpenerBurstResource),
        ("Manafont_BossReturn", BlmScenarioCategory.OpenerBurstResource),
        ("TargetLost_Short_2s_AF", BlmScenarioCategory.TargetLostDowntime),
        ("TargetLost_Medium_8s_AF", BlmScenarioCategory.TargetLostDowntime),
        ("TargetLost_Long_20s_AF", BlmScenarioCategory.TargetLostDowntime),
        ("TargetLost_WithAstralSoul6", BlmScenarioCategory.TargetLostDowntime),
        ("TargetLost_DuringIce", BlmScenarioCategory.TargetLostDowntime),
        ("BossReturn_After10s", BlmScenarioCategory.TargetLostDowntime),
        ("BossReturn_After60s", BlmScenarioCategory.TargetLostDowntime),
        ("PhaseEnd_15sRemaining", BlmScenarioCategory.TargetLostDowntime),
        ("FightEnd_8sRemaining", BlmScenarioCategory.TargetLostDowntime),
        ("ForcedMove_3s_DuringAF", BlmScenarioCategory.Movement),
        ("ForcedMove_5s_DuringAF", BlmScenarioCategory.Movement),
        ("ForcedMove_8s_DuringAF", BlmScenarioCategory.Movement),
        ("ForcedMove_DuringIce", BlmScenarioCategory.Movement),
        ("ForcedMove_WithNoSwiftTriple", BlmScenarioCategory.Movement),
        ("ForcedMove_WithThunderhead", BlmScenarioCategory.Movement),
        ("LookAway_DuringHardcast", BlmScenarioCategory.Movement),
        ("LookAway_DuringInstant", BlmScenarioCategory.Movement),
        ("Movement_ModeHandoff_ManualToFuturePlanner", BlmScenarioCategory.Movement),
        ("LeyLines_FullUptime_EvenBurst", BlmScenarioCategory.LeyLines),
        ("LeyLines_UnsafeSoon_Hold", BlmScenarioCategory.LeyLines),
        ("LeyLines_UnsafeAfterUse_ReturnBlocked", BlmScenarioCategory.LeyLines),
        ("LeyLines_MovementReturn_BetweenTheLines", BlmScenarioCategory.LeyLines),
        ("LeyLines_MovementReturn_Retrace", BlmScenarioCategory.LeyLines),
        ("LeyLines_DyingTarget_Hold", BlmScenarioCategory.LeyLines),
        ("LeyLines_Force_IgnoresHold", BlmScenarioCategory.LeyLines),
        ("LeyLines_Force_DuringMovement_Hold", BlmScenarioCategory.LeyLines),
        ("LeyLines_Force_EscapeHatch_Hold", BlmScenarioCategory.LeyLines),
        ("AOE_2Targets_60s", BlmScenarioCategory.Aoe),
        ("AOE_3Targets_60s", BlmScenarioCategory.Aoe),
        ("AOE_5Targets_60s", BlmScenarioCategory.Aoe),
        ("SingleToAOE_1to3", BlmScenarioCategory.Aoe),
        ("AOEToSingle_3to1", BlmScenarioCategory.Aoe),
        ("AOEToSingle_WithAstralSoulBridge", BlmScenarioCategory.Aoe),
        ("Trash_HoldBurst", BlmScenarioCategory.Aoe),
        ("AllianceTrash_AllowLongAOE", BlmScenarioCategory.Aoe),
        ("MajorAdd_AllowBurst", BlmScenarioCategory.Aoe),
        ("DyingAdds_HoldLeyLines", BlmScenarioCategory.Aoe),
        ("Thunder_Automatic_Refresh6s", BlmScenarioCategory.Thunder),
        ("Thunder_Delay_NoRefresh", BlmScenarioCategory.Thunder),
        ("Thunder_Force_ASAP", BlmScenarioCategory.Thunder),
        ("Thunder_InstantOnly_Movement", BlmScenarioCategory.Thunder),
        ("Thunder_ForbidInstant_RefreshOnly", BlmScenarioCategory.Thunder),
        ("AOEThunder_2Targets", BlmScenarioCategory.Thunder),
        ("Thunder_FallbackTarget_WhenAoENotWorth", BlmScenarioCategory.Thunder),
        ("RotationMode_AutomaticToFuturePlanner", BlmScenarioCategory.ModeExternal),
        ("RotationMode_PolyglotOnlyToFuturePlanner", BlmScenarioCategory.ModeExternal),
        ("ExternalHints_Off", BlmScenarioCategory.ModeExternal),
        ("ExternalHints_MechanicOnly", BlmScenarioCategory.ModeExternal),
        ("ExternalHints_Full", BlmScenarioCategory.ModeExternal),
        ("EncounterHint_HoldBurst", BlmScenarioCategory.ModeExternal),
        ("EncounterHint_ForceBurst", BlmScenarioCategory.ModeExternal),
        ("RecoveryAI_ShadowLogOnly_NoActionChange", BlmScenarioCategory.ModeExternal),
        ("RecoveryAI_AssistRecoveryOnly_NoDeadlock", BlmScenarioCategory.ModeExternal),
        ("Phantom_Disabled", BlmScenarioCategory.Coverage),
        ("Phantom_Enabled", BlmScenarioCategory.Coverage),
        ("Phantom_TargetLost", BlmScenarioCategory.Coverage),
        ("Phantom_Movement", BlmScenarioCategory.Coverage)
    ];

    public static List<BlmScenario> BuildAll()
    {
        var scenarios = new List<BlmScenario>(BaseScenarios.Length * 2 + 1 + 24000);
        foreach (var (name, category) in BaseScenarios)
        {
            scenarios.Add(BuildScenario(name, category, BlmRotationStrategy.Automatic));
            scenarios.Add(BuildScenario(name, category, BlmRotationStrategy.FuturePlanner));
        }

        scenarios.Add(BuildScenario("RotationMode_PolyglotOnlyToFuturePlanner_PolyglotOvercapOnly", BlmScenarioCategory.ModeExternal, BlmRotationStrategy.PolyglotOvercapOnly));
        scenarios.AddRange(BuildWindurstThirdWalkScenarios());
        scenarios.AddRange(BuildCombatMatrixScenarios());
        scenarios.AddRange(BuildHighEndPreflightScenarios());
        return scenarios;
    }

    public static List<BlmScenario> BuildHighEndPreflight()
        => BuildHighEndPreflightScenarios().ToList();

    public static List<BlmScenario> Select(string pattern)
        => BuildAll()
            .Where(s => s.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || s.Category.ToString().Contains(pattern, StringComparison.OrdinalIgnoreCase)
                || s.Rotation.ToString().Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static IEnumerable<BlmScenario> BuildCombatMatrixScenarios()
    {
        var levels = new[] { 12, 18, 30, 35, 50, 60, 70, 80, 90, 92, 100 };
        var targetCounts = new[] { 1, 2, 3, 5 };
        var states = new[]
        {
            new MatrixState("Neutral_FullMP", Element: 0, MP: 10000, Hearts: 3, Polyglot: 0, NextPolyglot: 8d, Soul: 0, ThunderLeft: 0d, AoeThunderLeft: 0d, Paradox: false, Firestarter: false),
            new MatrixState("Neutral_LowMP", Element: 0, MP: 1800, Hearts: 0, Polyglot: 0, NextPolyglot: 8d, Soul: 0, ThunderLeft: 0d, AoeThunderLeft: 0d, Paradox: false, Firestarter: false),
            new MatrixState("AF3_HighMP", Element: 3, MP: 7600, Hearts: 3, Polyglot: 1, NextPolyglot: 10d, Soul: 3, ThunderLeft: 12d, AoeThunderLeft: 12d, Paradox: false, Firestarter: false),
            new MatrixState("AF3_LowMP", Element: 3, MP: 700, Hearts: 1, Polyglot: 1, NextPolyglot: 3d, Soul: 5, ThunderLeft: 5d, AoeThunderLeft: 5d, Paradox: false, Firestarter: false),
            new MatrixState("AF3_NoHeartLowMP", Element: 3, MP: 900, Hearts: 0, Polyglot: 1, NextPolyglot: 2d, Soul: 4, ThunderLeft: 4d, AoeThunderLeft: 4d, Paradox: false, Firestarter: false),
            new MatrixState("AF3_Soul6", Element: 3, MP: 2400, Hearts: 0, Polyglot: 1, NextPolyglot: 6d, Soul: 6, ThunderLeft: 9d, AoeThunderLeft: 9d, Paradox: false, Firestarter: false),
            new MatrixState("UI3_Reentry", Element: -3, MP: 10000, Hearts: 3, Polyglot: 1, NextPolyglot: 8d, Soul: 0, ThunderLeft: 8d, AoeThunderLeft: 8d, Paradox: true, Firestarter: true),
            new MatrixState("UI1_PartialMP", Element: -1, MP: 6800, Hearts: 1, Polyglot: 0, NextPolyglot: 12d, Soul: 0, ThunderLeft: 6d, AoeThunderLeft: 6d, Paradox: false, Firestarter: false),
            new MatrixState("Unstable_AF1", Element: 1, MP: 2400, Hearts: 1, Polyglot: 1, NextPolyglot: 4d, Soul: 2, ThunderLeft: 4d, AoeThunderLeft: 4d, Paradox: true, Firestarter: false)
        };
        var events = new[]
        {
            new MatrixEvent("Uptime", []),
            new MatrixEvent("TargetLostShort", [new() { Type = BlmScenarioEventType.TargetLost, Start = 20, End = 24 }]),
            new MatrixEvent("TargetLostLong", [new() { Type = BlmScenarioEventType.TargetLost, Start = 12, End = 27 }]),
            new MatrixEvent("DowntimeSoon", [new() { Type = BlmScenarioEventType.Downtime, Start = 18, End = 32 }]),
            new MatrixEvent("ForcedMove", [new() { Type = BlmScenarioEventType.ForcedMove, Start = 12, End = 18 }]),
            new MatrixEvent("LookAway", [new() { Type = BlmScenarioEventType.LookAway, Start = 12, End = 17 }]),
            new MatrixEvent("LeyUnsafe", [new() { Type = BlmScenarioEventType.LeyLinesUnsafe, Start = 8, End = 22 }]),
            new MatrixEvent("TargetSwap", [new() { Type = BlmScenarioEventType.TargetCount, Start = 16, End = 36, Targets = 1 }]),
            new MatrixEvent("DyingTarget", [new() { Type = BlmScenarioEventType.TargetDying, Start = 0, End = 30 }])
        };
        var rotations = new[] { BlmRotationStrategy.Automatic, BlmRotationStrategy.FuturePlanner, BlmRotationStrategy.WindurstThirdWalk };
        var profiles = new[]
        {
            new MatrixProfile("Default", BlmThunderStrategy.Automatic, BlmLeylinesStrategy.EvenBurst, BlmTriplecastStrategy.Automatic, BlmOffensiveStrategy.Automatic, BlmEncounterHintStrategy.Boss, BlmExternalHintStrategy.Full),
            new MatrixProfile("BurstForce", BlmThunderStrategy.Force, BlmLeylinesStrategy.Force, BlmTriplecastStrategy.Automatic, BlmOffensiveStrategy.Force, BlmEncounterHintStrategy.ForceBurst, BlmExternalHintStrategy.Full),
            new MatrixProfile("Conservative", BlmThunderStrategy.ForbidInstant, BlmLeylinesStrategy.Delay, BlmTriplecastStrategy.Delay, BlmOffensiveStrategy.Delay, BlmEncounterHintStrategy.HoldBurst, BlmExternalHintStrategy.MechanicOnly)
        };

        foreach (var level in levels)
        {
            foreach (var targets in targetCounts)
            {
                foreach (var state in states)
                {
                    foreach (var ev in events)
                    {
                        foreach (var rotation in rotations)
                        {
                            foreach (var profile in profiles)
                            {
                                var adjustedEvents = AdjustMatrixEvents(ev.Events, targets);
                                yield return new BlmScenario
                                {
                                    Name = $"Matrix_L{level}_T{targets}_{state.Name}_{ev.Name}_{rotation}_{profile.Name}",
                                    Category = BlmScenarioCategory.CombatMatrix,
                                    Level = level,
                                    Duration = 30,
                                    InitialTargets = targets,
                                    InitialElement = state.Element,
                                    InitialMP = state.MP,
                                    InitialHearts = Math.Min(state.Hearts, level >= 58 ? BlmConstants.MaxHearts : 0),
                                    InitialPolyglot = InitialMatrixPolyglot(level, state, ev.Name),
                                    InitialAstralSoul = Math.Min(state.Soul, level >= 100 ? BlmConstants.MaxAstralSoul : 0),
                                    InitialThunderLeft = targets >= 2 ? 0 : state.ThunderLeft,
                                    InitialAoeThunderLeft = targets >= 2 ? state.AoeThunderLeft : 0,
                                    InitialNextPolyglot = ev.Name == "Uptime" ? state.NextPolyglot : Math.Min(state.NextPolyglot, 3),
                                    InitialParadox = state.Paradox && level >= 90,
                                    InitialThunderhead = targets >= 2 ? level >= 26 : level >= 6,
                                    InitialFirestarter = state.Firestarter && level >= 42,
                                    Rotation = rotation,
                                    EncounterHint = targets >= 3 ? BlmEncounterHintStrategy.Trash : profile.EncounterHint,
                                    ExternalHints = profile.ExternalHints,
                                    Thunder = profile.Thunder,
                                    Leylines = profile.Leylines,
                                    Triplecast = profile.Triplecast,
                                    Manafont = profile.Manafont,
                                    Events = adjustedEvents
                                };
                            }
                        }
                    }
                }
            }
        }

        foreach (var level in new[] { 80, 90, 100 })
        {
            yield return new BlmScenario
            {
                Name = $"Matrix_L{level}_PolyglotOnly_MaxStacks_PolyglotOvercapOnly",
                Category = BlmScenarioCategory.CombatMatrix,
                Level = level,
                Duration = 60,
                InitialTargets = 1,
                InitialElement = 3,
                InitialMP = 3200,
                InitialHearts = BlmConstants.MaxHearts,
                InitialPolyglot = level >= 98 ? 3 : level >= 80 ? 2 : 1,
                InitialAstralSoul = level >= 100 ? 4 : 0,
                InitialThunderLeft = 12,
                InitialNextPolyglot = 2,
                InitialThunderhead = true,
                Rotation = BlmRotationStrategy.PolyglotOvercapOnly,
                EncounterHint = BlmEncounterHintStrategy.Boss,
                ExternalHints = BlmExternalHintStrategy.Full,
                Thunder = BlmThunderStrategy.Automatic,
                Leylines = BlmLeylinesStrategy.EvenBurst,
                Triplecast = BlmTriplecastStrategy.Automatic,
                Manafont = BlmOffensiveStrategy.Automatic
            };
        }
    }

    private static IEnumerable<BlmScenario> BuildHighEndPreflightScenarios()
    {
        var levels = new[] { 80, 90, 100 };
        var states = new[]
        {
            new MatrixState("Neutral_FullMP", Element: 0, MP: 10000, Hearts: 3, Polyglot: 1, NextPolyglot: 5d, Soul: 0, ThunderLeft: 4d, AoeThunderLeft: 4d, Paradox: false, Firestarter: false),
            new MatrixState("AF3_LowMP", Element: 3, MP: 700, Hearts: 1, Polyglot: 1, NextPolyglot: 3d, Soul: 5, ThunderLeft: 5d, AoeThunderLeft: 5d, Paradox: false, Firestarter: false),
            new MatrixState("AF3_NoHeartLowMP", Element: 3, MP: 900, Hearts: 0, Polyglot: 1, NextPolyglot: 2d, Soul: 4, ThunderLeft: 4d, AoeThunderLeft: 4d, Paradox: false, Firestarter: false),
            new MatrixState("AF3_Soul6", Element: 3, MP: 2400, Hearts: 0, Polyglot: 1, NextPolyglot: 4d, Soul: 6, ThunderLeft: 7d, AoeThunderLeft: 7d, Paradox: false, Firestarter: false),
            new MatrixState("UI3_Reentry", Element: -3, MP: 10000, Hearts: 3, Polyglot: 1, NextPolyglot: 4d, Soul: 0, ThunderLeft: 6d, AoeThunderLeft: 6d, Paradox: true, Firestarter: true),
            new MatrixState("Unstable_AF1", Element: 1, MP: 2400, Hearts: 1, Polyglot: 1, NextPolyglot: 2d, Soul: 2, ThunderLeft: 4d, AoeThunderLeft: 4d, Paradox: true, Firestarter: false)
        };
        var profiles = new[]
        {
            new MatrixProfile("Default", BlmThunderStrategy.Automatic, BlmLeylinesStrategy.EvenBurst, BlmTriplecastStrategy.Automatic, BlmOffensiveStrategy.Automatic, BlmEncounterHintStrategy.Boss, BlmExternalHintStrategy.Full),
            new MatrixProfile("BurstForce", BlmThunderStrategy.Force, BlmLeylinesStrategy.Force, BlmTriplecastStrategy.Automatic, BlmOffensiveStrategy.Force, BlmEncounterHintStrategy.ForceBurst, BlmExternalHintStrategy.Full)
        };
        var patterns = new[]
        {
            new HighEndPattern("ForcedMove_LookAway", [
                new() { Type = BlmScenarioEventType.ForcedMove, Start = 10, End = 17.5 },
                new() { Type = BlmScenarioEventType.LookAway, Start = 12.5, End = 17.5 }
            ]),
            new HighEndPattern("TargetLost_ForcedMove_Return", [
                new() { Type = BlmScenarioEventType.TargetLost, Start = 10, End = 22.5 },
                new() { Type = BlmScenarioEventType.ForcedMove, Start = 20, End = 27.5 }
            ]),
            new HighEndPattern("Downtime_ForcedMove_Handoff", [
                new() { Type = BlmScenarioEventType.Downtime, Start = 10, End = 25 },
                new() { Type = BlmScenarioEventType.ForcedMove, Start = 22.5, End = 30 },
                new() { Type = BlmScenarioEventType.ModeSwitch, Start = 25, End = 26, Rotation = BlmRotationStrategy.FuturePlanner }
            ]),
            new HighEndPattern("LeyUnsafe_ForcedMove", [
                new() { Type = BlmScenarioEventType.LeyLinesUnsafe, Start = 8, End = 26 },
                new() { Type = BlmScenarioEventType.ForcedMove, Start = 14, End = 20 }
            ]),
            new HighEndPattern("TargetDying_TargetSwap", [
                new() { Type = BlmScenarioEventType.TargetDying, Start = 0, End = 20 },
                new() { Type = BlmScenarioEventType.TargetCount, Start = 15, End = 45 }
            ]),
            new HighEndPattern("ThunderPolyglot_ForcedMove", [
                new() { Type = BlmScenarioEventType.ForcedMove, Start = 5, End = 12.5 },
                new() { Type = BlmScenarioEventType.TargetCount, Start = 12.5, End = 45 }
            ]),
            new HighEndPattern("AOEToSingle_ForcedMove", [
                new() { Type = BlmScenarioEventType.TargetCount, Start = 12.5, End = 45, Targets = 1 },
                new() { Type = BlmScenarioEventType.ForcedMove, Start = 12.5, End = 17.5 }
            ], RequiresAoeStart: true),
            new HighEndPattern("SingleToAOE_LookAway", [
                new() { Type = BlmScenarioEventType.TargetCount, Start = 10, End = 45, Targets = 2 },
                new() { Type = BlmScenarioEventType.LookAway, Start = 12.5, End = 17.5 }
            ], RequiresSingleStart: true)
        };

        foreach (var level in levels)
        {
            foreach (var initialTargets in new[] { 1, 2, 3 })
            {
                foreach (var state in states)
                {
                    foreach (var rotation in new[] { BlmRotationStrategy.Automatic, BlmRotationStrategy.FuturePlanner, BlmRotationStrategy.WindurstThirdWalk })
                    {
                        foreach (var profile in profiles)
                        {
                            foreach (var pattern in patterns)
                            {
                                if (pattern.RequiresAoeStart && initialTargets < 2 || pattern.RequiresSingleStart && initialTargets != 1)
                                    continue;

                                var events = AdjustHighEndEvents(pattern.Events, initialTargets);
                                yield return new BlmScenario
                                {
                                    Name = $"HighEnd_L{level}_T{initialTargets}_{state.Name}_{pattern.Name}_{rotation}_{profile.Name}",
                                    Category = BlmScenarioCategory.HighEndPreflight,
                                    Level = level,
                                    Duration = 45,
                                    InitialTargets = initialTargets,
                                    InitialElement = state.Element,
                                    InitialMP = state.MP,
                                    InitialHearts = Math.Min(state.Hearts, level >= 58 ? BlmConstants.MaxHearts : 0),
                                    InitialPolyglot = InitialMatrixPolyglot(level, state, pattern.Name),
                                    InitialAstralSoul = Math.Min(state.Soul, level >= 100 ? BlmConstants.MaxAstralSoul : 0),
                                    InitialThunderLeft = initialTargets >= 2 ? 0 : state.ThunderLeft,
                                    InitialAoeThunderLeft = initialTargets >= 2 ? state.AoeThunderLeft : 0,
                                    InitialNextPolyglot = Math.Min(state.NextPolyglot, 3),
                                    InitialParadox = state.Paradox && level >= 90,
                                    InitialThunderhead = initialTargets >= 2 ? level >= 26 : level >= 6,
                                    InitialFirestarter = state.Firestarter && level >= 42,
                                    Rotation = rotation,
                                    EncounterHint = initialTargets >= 3 ? BlmEncounterHintStrategy.Trash : profile.EncounterHint,
                                    ExternalHints = profile.ExternalHints,
                                    Thunder = profile.Thunder,
                                    Leylines = profile.Leylines,
                                    Triplecast = profile.Triplecast,
                                    Manafont = profile.Manafont,
                                    Events = events
                                };
                            }
                        }
                    }
                }
            }
        }
    }

    private static IReadOnlyList<BlmScenarioEvent> AdjustMatrixEvents(IReadOnlyList<BlmScenarioEvent> source, int initialTargets)
    {
        if (source.Count == 0)
            return [];

        var events = new List<BlmScenarioEvent>(source.Count);
        foreach (var e in source)
        {
            if (e.Type == BlmScenarioEventType.TargetCount)
            {
                events.Add(e with { Targets = initialTargets == 1 ? 3 : 1 });
                continue;
            }

            events.Add(e);
        }

        return events;
    }

    private static IReadOnlyList<BlmScenarioEvent> AdjustHighEndEvents(IReadOnlyList<BlmScenarioEvent> source, int initialTargets)
    {
        var events = new List<BlmScenarioEvent>(source.Count);
        foreach (var e in source)
        {
            if (e.Type == BlmScenarioEventType.TargetCount && e.Targets == 0)
                events.Add(e with { Targets = initialTargets == 1 ? 2 : 1 });
            else
                events.Add(e);
        }

        return events;
    }

    private static int InitialMatrixPolyglot(int level, MatrixState state, string eventName)
    {
        var max = level >= 98 ? 3 : level >= 80 ? 2 : level >= 70 ? 1 : 0;
        if (max == 0)
            return 0;
        if (state.Polyglot > 0)
            return Math.Min(max, state.Polyglot);
        if (eventName is "ForcedMove" or "LookAway")
            return Math.Min(max, 1);
        if (state.Name.Contains("LowMP", StringComparison.OrdinalIgnoreCase))
            return max;
        return Math.Min(max, 1);
    }

    private static BlmScenario BuildScenario(string baseName, BlmScenarioCategory category, BlmRotationStrategy rotation)
    {
        var name = $"{baseName}_{rotation}";
        var level = ParseLevel(baseName);
        var duration = baseName.Contains("10min", StringComparison.OrdinalIgnoreCase) ? 600 :
            baseName.Contains("60s", StringComparison.OrdinalIgnoreCase) ? 60 :
            baseName.Contains("FightEnd", StringComparison.OrdinalIgnoreCase) ? 45 :
            360;
        var targets = InitialTargets(baseName);
        var events = new List<BlmScenarioEvent>();
        var thunder = BlmThunderStrategy.Automatic;
        var leylines = BlmLeylinesStrategy.EvenBurst;
        var triplecast = BlmTriplecastStrategy.Automatic;
        var manafont = BlmOffensiveStrategy.Automatic;
        var encounter = BlmEncounterHintStrategy.Boss;
        var external = BlmExternalHintStrategy.Full;
        var initialElement = 0;
        var initialMP = BlmConstants.MaxMP;
        var initialHearts = level >= 58 ? 3 : 0;
        var initialPolyglot = 0;
        var initialSoul = 0;
        var initialThunderLeft = 0d;
        var initialAoeThunderLeft = 0d;
        var initialNextPoly = 12d;
        var initialParadox = false;
        var initialThunderhead = true;
        var initialFirestarter = false;
        var requiresPhantomCoverage = baseName.StartsWith("Phantom_", StringComparison.OrdinalIgnoreCase);
        var phantomActionsEnabled = requiresPhantomCoverage && !baseName.Contains("Phantom_Disabled", StringComparison.OrdinalIgnoreCase);

        if (baseName.Contains("TargetLost_Short", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.TargetLost, Start = 45, End = 47 });
        if (baseName.Contains("TargetLost_Medium", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.TargetLost, Start = 45, End = 53 });
        if (baseName.Contains("TargetLost_Long", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.TargetLost, Start = 45, End = 65 });
        if (baseName.Contains("TargetLost_WithAstralSoul6", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new() { Type = BlmScenarioEventType.TargetLost, Start = 35, End = 45 });
            initialElement = 3;
            initialSoul = 6;
        }
        if (baseName.Contains("TargetLost_DuringIce", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new() { Type = BlmScenarioEventType.TargetLost, Start = 35, End = 43 });
            initialElement = -3;
        }
        if (baseName.Contains("BossReturn_After10s", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.TargetLost, Start = 20, End = 30 });
        if (baseName.Contains("BossReturn_After60s", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.TargetLost, Start = 20, End = 80 });
        if (baseName.Contains("PhaseEnd_15sRemaining", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.Downtime, Start = duration - 15, End = duration });
        if (baseName.Contains("FightEnd_8sRemaining", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.TargetDying, Start = duration - 8, End = duration });

        if (baseName.Contains("ForcedMove_3s", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.ForcedMove, Start = 42, End = 45 });
        if (baseName.Contains("ForcedMove_5s", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.ForcedMove, Start = 42, End = 47 });
        if (baseName.Contains("ForcedMove_8s", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.ForcedMove, Start = 42, End = 50 });
        if (baseName.Contains("ForcedMove_DuringIce", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new() { Type = BlmScenarioEventType.ForcedMove, Start = 18, End = 23 });
            initialElement = -3;
        }
        if (baseName.Contains("ForcedMove_WithNoSwiftTriple", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.ForcedMove, Start = 6, End = 14 });
        if (baseName.Contains("ForcedMove_WithThunderhead", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new() { Type = BlmScenarioEventType.ForcedMove, Start = 12, End = 17 });
            initialThunderLeft = 7;
        }
        if (baseName.Contains("LookAway_DuringHardcast", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.LookAway, Start = 25, End = 30 });
        if (baseName.Contains("LookAway_DuringInstant", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new() { Type = BlmScenarioEventType.LookAway, Start = 25, End = 30 });
            initialPolyglot = 1;
        }
        if (baseName.Contains("ModeHandoff", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.ModeSwitch, Start = 15, End = 16, Rotation = BlmRotationStrategy.FuturePlanner });

        if (baseName.Contains("LeyLines_UnsafeSoon", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.LeyLinesUnsafe, Start = 6, End = 20 });
        if (baseName.Contains("LeyLines_UnsafeAfterUse", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.LeyLinesUnsafe, Start = 24, End = 40 });
        if (baseName.Contains("LeyLines_MovementReturn", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.ForcedMove, Start = 12, End = 14 });
        if (baseName.Contains("LeyLines_DyingTarget", StringComparison.OrdinalIgnoreCase) || baseName.Contains("DyingAdds", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.TargetDying, Start = 0, End = 30 });
        if (baseName.Contains("LeyLines_Force", StringComparison.OrdinalIgnoreCase))
            leylines = BlmLeylinesStrategy.Force;
        if (baseName.Contains("LeyLines_Force_DuringMovement", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.ForcedMove, Start = 0, End = 15 });
        if (baseName.Contains("LeyLines_Force_EscapeHatch", StringComparison.OrdinalIgnoreCase))
        {
            events.Add(new() { Type = BlmScenarioEventType.MovementEscapeHatch, Start = 0, End = 15 });
            events.Add(new() { Type = BlmScenarioEventType.ForcedMove, Start = 0, End = 15 });
        }

        if (baseName.Contains("SingleToAOE", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.TargetCount, Start = 20, End = duration, Targets = 3 });
        if (baseName.Contains("AOEToSingle", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.TargetCount, Start = 25, End = duration, Targets = 1 });
        if (baseName.Contains("Trash", StringComparison.OrdinalIgnoreCase))
            encounter = BlmEncounterHintStrategy.Trash;
        if (baseName.Contains("AllianceTrash", StringComparison.OrdinalIgnoreCase))
            encounter = BlmEncounterHintStrategy.AllianceTrash;
        if (baseName.Contains("MajorAdd", StringComparison.OrdinalIgnoreCase))
            encounter = BlmEncounterHintStrategy.MajorAdd;

        if (baseName.Contains("Thunder_Delay", StringComparison.OrdinalIgnoreCase))
            thunder = BlmThunderStrategy.Delay;
        if (baseName.Contains("Thunder_Force", StringComparison.OrdinalIgnoreCase))
            thunder = BlmThunderStrategy.Force;
        if (baseName.Contains("Thunder_InstantOnly", StringComparison.OrdinalIgnoreCase))
            thunder = BlmThunderStrategy.InstantOnly;
        if (baseName.Contains("Thunder_ForbidInstant", StringComparison.OrdinalIgnoreCase))
            thunder = BlmThunderStrategy.ForbidInstant;
        if (baseName.Contains("Thunder_Automatic_Refresh6s", StringComparison.OrdinalIgnoreCase))
            initialThunderLeft = 5.8;
        if (baseName.Contains("AOEThunder_2Targets", StringComparison.OrdinalIgnoreCase))
        {
            targets = 2;
            initialAoeThunderLeft = 5.8;
        }
        if (baseName.Contains("Thunder_FallbackTarget", StringComparison.OrdinalIgnoreCase))
        {
            targets = 2;
            initialThunderLeft = 4;
            initialAoeThunderLeft = 20;
        }

        if (baseName.Contains("NearManafont_AF_LowMP", StringComparison.OrdinalIgnoreCase))
        {
            initialElement = 3;
            initialMP = 400;
            initialSoul = 5;
        }
        if (baseName.Contains("NearManafont_DowntimeSoon", StringComparison.OrdinalIgnoreCase))
        {
            initialElement = 3;
            initialMP = 400;
            events.Add(new() { Type = BlmScenarioEventType.Downtime, Start = 6, End = 40 });
        }
        if (baseName.Contains("Amplifier_PolyglotNearCap", StringComparison.OrdinalIgnoreCase))
        {
            initialPolyglot = 2;
            initialNextPoly = 2;
        }
        if (baseName.Contains("Polyglot_Overcap_ThreeStacks", StringComparison.OrdinalIgnoreCase))
        {
            initialPolyglot = 3;
            initialNextPoly = 1;
        }
        if (baseName.Contains("Manafont_Force", StringComparison.OrdinalIgnoreCase))
        {
            manafont = BlmOffensiveStrategy.Force;
            initialElement = 3;
            initialMP = 600;
        }
        if (baseName.Contains("Manafont_HoldBurstEncounter", StringComparison.OrdinalIgnoreCase))
            encounter = BlmEncounterHintStrategy.HoldBurst;
        if (baseName.Contains("Manafont_BossReturn", StringComparison.OrdinalIgnoreCase))
            encounter = BlmEncounterHintStrategy.BossReturn;

        if (baseName.Contains("ExternalHints_Off", StringComparison.OrdinalIgnoreCase))
            external = BlmExternalHintStrategy.Off;
        if (baseName.Contains("ExternalHints_MechanicOnly", StringComparison.OrdinalIgnoreCase))
            external = BlmExternalHintStrategy.MechanicOnly;
        if (baseName.Contains("ExternalHints_Full", StringComparison.OrdinalIgnoreCase))
            external = BlmExternalHintStrategy.Full;
        if (baseName.Contains("EncounterHint_HoldBurst", StringComparison.OrdinalIgnoreCase))
            encounter = BlmEncounterHintStrategy.HoldBurst;
        if (baseName.Contains("EncounterHint_ForceBurst", StringComparison.OrdinalIgnoreCase))
            encounter = BlmEncounterHintStrategy.ForceBurst;

        if (baseName.Contains("PolyglotOnlyToFuturePlanner", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.ModeSwitch, Start = 20, End = 21, Rotation = BlmRotationStrategy.FuturePlanner });
        if (baseName.Contains("Phantom_TargetLost", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.TargetLost, Start = 20, End = 26 });
        if (baseName.Contains("Phantom_Movement", StringComparison.OrdinalIgnoreCase))
            events.Add(new() { Type = BlmScenarioEventType.ForcedMove, Start = 20, End = 26 });

        return new BlmScenario
        {
            Name = name,
            Category = category,
            Level = level,
            Duration = duration,
            InitialTargets = targets,
            InitialElement = initialElement,
            InitialMP = initialMP,
            InitialHearts = initialHearts,
            InitialPolyglot = initialPolyglot,
            InitialAstralSoul = initialSoul,
            InitialThunderLeft = initialThunderLeft,
            InitialAoeThunderLeft = initialAoeThunderLeft,
            InitialNextPolyglot = initialNextPoly,
            InitialParadox = initialParadox,
            InitialThunderhead = initialThunderhead,
            InitialFirestarter = initialFirestarter,
            Rotation = rotation,
            EncounterHint = encounter,
            ExternalHints = external,
            Thunder = thunder,
            Leylines = leylines,
            Triplecast = triplecast,
            Manafont = manafont,
            RequiresPhantomCoverage = requiresPhantomCoverage,
            PhantomActionsEnabled = phantomActionsEnabled,
            Events = events
        };
    }

    private static int ParseLevel(string name)
    {
        if (name.Contains("_90_", StringComparison.OrdinalIgnoreCase))
            return 90;
        if (name.Contains("_80_", StringComparison.OrdinalIgnoreCase))
            return 80;
        if (name.Contains("_70_", StringComparison.OrdinalIgnoreCase))
            return 70;
        if (name.Contains("_60_", StringComparison.OrdinalIgnoreCase))
            return 60;
        if (name.Contains("_50_", StringComparison.OrdinalIgnoreCase))
            return 50;
        return 100;
    }

    private static IEnumerable<BlmScenario> BuildWindurstThirdWalkScenarios()
    {
        foreach (var (name, encounterNameID, duration) in new (string Name, uint EncounterNameID, double Duration)[]
        {
            ("Shantotto", 14778, 270),
            ("Alexander", 14529, 250),
            ("Promathia", 14779, 350),
            ("HollowKing", 14729, 400)
        })
        {
            yield return new BlmScenario
            {
                Name = $"WindurstThirdWalk_{name}",
                Category = BlmScenarioCategory.OpenerBurstResource,
                Level = 100,
                Duration = duration,
                InitialTargets = 1,
                InitialHearts = BlmConstants.MaxHearts,
                InitialThunderhead = true,
                InitialNextPolyglot = 10,
                Rotation = BlmRotationStrategy.WindurstThirdWalk,
                EncounterHint = BlmEncounterHintStrategy.Boss,
                ExternalHints = BlmExternalHintStrategy.Full,
                Thunder = BlmThunderStrategy.Automatic,
                Leylines = BlmLeylinesStrategy.EvenBurst,
                Triplecast = BlmTriplecastStrategy.Automatic,
                Manafont = BlmOffensiveStrategy.Automatic,
                ContentFinderConditionID = 1117,
                EncounterNameID = encounterNameID
            };
        }
    }

    private static int InitialTargets(string name)
    {
        if (name.Contains("2Targets", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (name.Contains("3Targets", StringComparison.OrdinalIgnoreCase)
            || name.Contains("1to3", StringComparison.OrdinalIgnoreCase)
            || name.Contains("3to1", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Trash", StringComparison.OrdinalIgnoreCase)
            || name.Contains("DyingAdds", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (name.Contains("5Targets", StringComparison.OrdinalIgnoreCase)
            || name.Contains("AllianceTrash", StringComparison.OrdinalIgnoreCase))
            return 5;
        return 1;
    }

    private sealed record MatrixState(string Name, int Element, int MP, int Hearts, int Polyglot, double NextPolyglot, int Soul, double ThunderLeft, double AoeThunderLeft, bool Paradox, bool Firestarter);

    private sealed record MatrixEvent(string Name, IReadOnlyList<BlmScenarioEvent> Events);

    private sealed record MatrixProfile(
        string Name,
        BlmThunderStrategy Thunder,
        BlmLeylinesStrategy Leylines,
        BlmTriplecastStrategy Triplecast,
        BlmOffensiveStrategy Manafont,
        BlmEncounterHintStrategy EncounterHint,
        BlmExternalHintStrategy ExternalHints);

    private sealed record HighEndPattern(
        string Name,
        IReadOnlyList<BlmScenarioEvent> Events,
        bool RequiresAoeStart = false,
        bool RequiresSingleStart = false);
}
