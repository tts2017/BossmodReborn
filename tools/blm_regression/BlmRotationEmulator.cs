namespace BlmRegression;

public sealed class BlmRotationEmulator
{
    // Optional external policy (engine evaluation): gets the frame state, returns the oGCDs to weave before the GCD and the GCD;
    // null = use the built-in policy for this frame. PolicyMicros / PolicyAllocBytes record its cost per call.
    public Func<BlmScenario, BlmPolicyView, (BlmAction Gcd, IReadOnlyList<BlmAction> Ogcds)?>? PolicyOverride;
    public List<double>? PolicyMicros;
    public List<long>? PolicyAllocBytes;

    private const uint WindurstThirdWalkCFCID = 1117;
    private const uint WindurstShantottoNameID = 14778;
    private const uint WindurstAlexanderNameID = 14529;
    private const uint WindurstPromathiaNameID = 14779;
    private const uint WindurstHollowKingNameID = 14729;
    private const double WindurstBurstHoldLead = 10;

    private readonly record struct WindurstBurstWindow(double Time, double Before, double After);

    private static readonly WindurstBurstWindow[] WindurstShantottoBurstWindows =
    [
        new(6.355, 1.5, 6.0),
        new(128.072, 2.0, 6.0),
        new(246.771, 2.5, 7.0)
    ];

    private static readonly WindurstBurstWindow[] WindurstAlexanderBurstWindows =
    [
        new(7.170, 1.5, 6.0),
        new(126.910, 2.0, 7.0),
        new(225.698, 2.5, 7.0)
    ];

    private static readonly WindurstBurstWindow[] WindurstPromathiaBurstWindows =
    [
        new(10.759, 2.0, 7.0),
        new(101.586, 2.5, 7.0),
        new(211.897, 2.5, 7.0),
        new(323.602, 2.5, 7.0)
    ];

    private static readonly WindurstBurstWindow[] WindurstHollowKingBurstWindows =
    [
        new(7.798, 1.5, 6.0),
        new(131.173, 2.0, 7.0),
        new(248.859, 2.5, 7.0),
        new(369.886, 2.5, 8.0)
    ];

    private static readonly WindurstBurstWindow[] NoWindurstBurstWindows = [];

    private sealed class State
    {
        public double Time;
        public double LastGcdTime = double.NaN;
        public int Level;
        public int Targets;
        public BlmRotationStrategy Rotation;
        public int Element;
        public double ElementTimerLeft;
        public int MP;
        public int Hearts;
        public int Polyglot;
        public double NextPolyglot;
        public int AstralSoul;
        public bool Paradox;
        public bool Thunderhead;
        public bool Firestarter;
        public double TargetThunderLeft;
        public double AoeThunderLeft;
        public double TriplecastLeft;
        public int TriplecastStacks;
        public int TriplecastCharges;
        public double TriplecastChargeReadyIn;
        public double SwiftcastLeft;
        public double SwiftcastReadyIn;
        public double LeyLinesLeft;
        public bool HaveLeyLines;
        public bool InLeyLines;
        public double ManafontReadyIn;
        public double AmplifierReadyIn;
        public int LeyLinesCharges;
        public double LeyLinesChargeReadyIn;
        public double TransposeReadyIn;
        public double CappedPolyglotSeconds;
        public double SingleThunderActionableBlankSeconds;
        public double AoeThunderActionableBlankSeconds;
        public int ConsecutiveSingleOnAoe;
        public int ConsecutiveAoeOnSingle;
        public bool PhantomActionsEnabled;
        public int PhantomCharges;
        public bool PhantomTargetLostSuppressionSeen;
        public bool PhantomReturnGcdSeen;
        public uint ProcRngState;
    }

    private sealed class MetricAccumulator
    {
        public int GcdCount;
        public int OgcdCount;
        public double RawThunderBlankSeconds;
        public double ThunderBlankSeconds;
        public double RawPolyglotMaxHoldSeconds;
        public double PolyglotOvercapSeconds;
        public int PolyglotWastedGrantCount;
        public int ElementDropCount = 0;
        public int ForcedMovementHardcastAttempts = 0;
        public double LeyLinesUptimeSeconds;
        public int ManafontCount;
        public int AmplifierCount;
        public int Standard57OrderViolations = 0;
        public int ModeHandoffGcdStops = 0;
        public int PhantomActionsUsed;
        public int PhantomMovementFallbacks;
        public int PhantomTargetLostSuppressions;
        public int PhantomReturnGcds;
        public int PhantomNullGuardChecks;
        public int BetweenTheLinesCount;
        public int RetraceCount;
        public double GcdActiveSeconds;
    }

    public BlmScenarioResult Run(BlmScenario scenario)
    {
        var state = new State
        {
            Level = scenario.Level,
            Targets = scenario.InitialTargets,
            Rotation = scenario.Rotation,
            Element = scenario.InitialElement,
            ElementTimerLeft = scenario.InitialElement == 0 ? 0 : BlmConstants.ElementTimer,
            MP = scenario.InitialMP,
            Hearts = Math.Clamp(scenario.InitialHearts, 0, MaxHeartsForLevel(scenario.Level)),
            Polyglot = Math.Clamp(scenario.InitialPolyglot, 0, MaxPolyglotForLevel(scenario.Level)),
            NextPolyglot = scenario.InitialNextPolyglot,
            AstralSoul = scenario.InitialAstralSoul,
            Paradox = scenario.InitialParadox,
            Thunderhead = scenario.InitialThunderhead,
            Firestarter = scenario.InitialFirestarter,
            TargetThunderLeft = scenario.InitialThunderLeft,
            AoeThunderLeft = scenario.InitialAoeThunderLeft,
            PhantomActionsEnabled = scenario.PhantomActionsEnabled,
            PhantomCharges = scenario.PhantomActionsEnabled ? 1 : 0,
            TriplecastCharges = scenario.Level >= 66 ? 2 : 0,
            LeyLinesCharges = MaxLeyLinesChargesForLevel(scenario.Level),
            ProcRngState = StableScenarioSeed(scenario.Name)
        };

        var frames = new List<BlmActionFrame>();
        var hardFails = new List<BlmHardFailRule>();
        var metrics = new MetricAccumulator();

        while (state.Time < scenario.Duration - 0.0001)
        {
            var env = BuildEnvironment(scenario, state.Time, state.Targets);
            state.Targets = env.Targets;
            if (env.ModeSwitch.HasValue)
                state.Rotation = env.ModeSwitch.Value;
            if (env.ForcedMoveNow && state.HaveLeyLines)
                state.InLeyLines = false;

            TrackPhantomPassiveCoverage(scenario, state, env, metrics);

            (BlmAction Gcd, IReadOnlyList<BlmAction> Ogcds)? external = null;
            if (PolicyOverride != null)
            {
                var view = View(state, env);
                var alloc0 = GC.GetAllocatedBytesForCurrentThread();
                var t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                external = PolicyOverride(scenario, view);
                PolicyMicros?.Add(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMicroseconds);
                PolicyAllocBytes?.Add(GC.GetAllocatedBytesForCurrentThread() - alloc0);
            }
            IReadOnlyList<BlmAction> ogcds;
            if (external != null)
            {
                ogcds = external.Value.Ogcds;
            }
            else
            {
                var plannedGcd = SelectGcd(scenario, state, env);
                var maxWeaves = MaxWeavesAfter(state, plannedGcd);
                ogcds = SelectOgcds(scenario, state, env, maxWeaves);
            }
            foreach (var ogcd in ogcds)
            {
                var ogcdFails = ValidateOgcd(scenario, state, env, ogcd);
                hardFails.AddRange(ogcdFails);
                ApplyOgcd(scenario, state, ogcd, metrics);
            }

            var gcd = external?.Gcd ?? SelectGcd(scenario, state, env);
            var elapsed = gcd == BlmAction.None ? CurrentGcdDuration(state) : ActionCycleTime(state, gcd);
            var frameFails = new List<BlmHardFailRule>();
            frameFails.AddRange(ValidateState(state));
            frameFails.AddRange(ValidateGcd(scenario, state, env, gcd));

            if (gcd != BlmAction.None)
            {
                ApplyGcd(scenario, state, gcd);
                metrics.GcdCount++;
                metrics.GcdActiveSeconds += elapsed;
                state.LastGcdTime = state.Time;
                TrackAoeSwitchHardFails(state, gcd, frameFails);
                TrackPhantomGcdCoverage(scenario, state, env, gcd, metrics);
            }
            else if (ShouldRequireGcd(state, env, scenario) && !double.IsNaN(state.LastGcdTime) && state.Time - state.LastGcdTime > BlmConstants.Gcd + 0.0001)
            {
                frameFails.Add(BlmHardFailRule.GcdStop);
            }

            if (frameFails.Count > 0)
                hardFails.AddRange(frameFails);

            frames.Add(new BlmActionFrame
            {
                ScenarioName = scenario.Name,
                Time = Math.Round(state.Time, 3),
                SelectedGcd = gcd,
                SelectedOgcds = ogcds,
                Reason = BuildReason(env),
                TargetAvailable = env.TargetAvailable,
                DowntimeNow = env.DowntimeNow,
                ForcedMoveNow = env.ForcedMoveNow,
                MovementEscapeHatchHeld = env.MovementEscapeHatchHeld,
                LookAwayNow = env.LookAwayNow,
                Level = state.Level,
                Targets = state.Targets,
                Rotation = state.Rotation,
                Element = state.Element,
                ElementTimerLeft = Math.Round(state.ElementTimerLeft, 3),
                MP = state.MP,
                Hearts = state.Hearts,
                Polyglot = state.Polyglot,
                NextPolyglot = Math.Round(state.NextPolyglot, 3),
                AstralSoul = state.AstralSoul,
                Paradox = state.Paradox,
                Thunderhead = state.Thunderhead,
                Firestarter = state.Firestarter,
                ThunderLeft = Math.Round(state.TargetThunderLeft, 3),
                AoeThunderLeft = Math.Round(state.AoeThunderLeft, 3),
                ManafontReadyIn = Math.Round(state.ManafontReadyIn, 3),
                AmplifierReadyIn = Math.Round(state.AmplifierReadyIn, 3),
                LeyLinesLeft = Math.Round(state.LeyLinesLeft, 3),
                LeyLinesCharges = state.LeyLinesCharges,
                SwiftcastReadyIn = Math.Round(state.SwiftcastReadyIn, 3),
                TriplecastCharges = state.TriplecastCharges,
                HardFails = frameFails
            });

            elapsed = AdvanceTimers(state, elapsed, env, metrics);
            TrackPassiveMetrics(scenario, state, env, metrics, elapsed);
            state.Time += elapsed;
        }

        var distinctHardFails = hardFails.ToList();
        if (scenario.Name.Contains("LeyLines_MovementReturn_Retrace", StringComparison.OrdinalIgnoreCase) && metrics.RetraceCount == 0)
            distinctHardFails.Add(BlmHardFailRule.RetraceMissing);
        return new BlmScenarioResult
        {
            Scenario = scenario,
            Frames = frames,
            HardFails = distinctHardFails,
            Metrics = new BlmScenarioMetrics
            {
                GcdCount = metrics.GcdCount,
                OgcdCount = metrics.OgcdCount,
                GcdUptime = scenario.Duration <= 0 ? 0 : Math.Min(1, metrics.GcdActiveSeconds / scenario.Duration),
                RawThunderBlankSeconds = Math.Round(metrics.RawThunderBlankSeconds, 3),
                ThunderBlankSeconds = Math.Round(metrics.ThunderBlankSeconds, 3),
                RawPolyglotMaxHoldSeconds = Math.Round(metrics.RawPolyglotMaxHoldSeconds, 3),
                PolyglotOvercapSeconds = Math.Round(metrics.PolyglotOvercapSeconds, 3),
                PolyglotWastedGrantCount = metrics.PolyglotWastedGrantCount,
                ElementDropCount = metrics.ElementDropCount,
                ForcedMovementHardcastAttempts = metrics.ForcedMovementHardcastAttempts,
                LeyLinesUptimeSeconds = Math.Round(metrics.LeyLinesUptimeSeconds, 3),
                ManafontCount = metrics.ManafontCount,
                AmplifierCount = metrics.AmplifierCount,
                Standard57OrderViolations = metrics.Standard57OrderViolations,
                ModeHandoffGcdStops = metrics.ModeHandoffGcdStops,
                PhantomActionsUsed = metrics.PhantomActionsUsed,
                PhantomMovementFallbacks = metrics.PhantomMovementFallbacks,
                PhantomTargetLostSuppressions = metrics.PhantomTargetLostSuppressions,
                PhantomReturnGcds = metrics.PhantomReturnGcds,
                PhantomNullGuardChecks = metrics.PhantomNullGuardChecks,
                CoverageGapCount = ComputeCoverageGap(scenario, metrics)
            }
        };
    }

    private static EnvironmentState BuildEnvironment(BlmScenario scenario, double time, int currentTargets)
    {
        var targetAvailable = true;
        var downtimeNow = false;
        var forcedMoveNow = false;
        var movementEscapeHatchHeld = false;
        var lookAwayNow = false;
        var targetDying = false;
        var targets = currentTargets;
        BlmRotationStrategy? modeSwitch = null;
        var downtimeIn = double.PositiveInfinity;
        var forcedMoveIn = double.PositiveInfinity;
        var leyLinesUnsafeIn = double.PositiveInfinity;
        var lookAwayIn = double.PositiveInfinity;

        foreach (var e in scenario.Events)
        {
            var active = time >= e.Start && time < e.End;
            var future = e.Start >= time ? e.Start - time : double.PositiveInfinity;
            switch (e.Type)
            {
                case BlmScenarioEventType.TargetLost:
                    if (active)
                        targetAvailable = false;
                    downtimeIn = Math.Min(downtimeIn, future);
                    break;
                case BlmScenarioEventType.Downtime:
                    if (active)
                    {
                        downtimeNow = true;
                        targetAvailable = false;
                    }
                    downtimeIn = Math.Min(downtimeIn, future);
                    break;
                case BlmScenarioEventType.ForcedMove:
                    if (active)
                        forcedMoveNow = true;
                    forcedMoveIn = Math.Min(forcedMoveIn, future);
                    break;
                case BlmScenarioEventType.MovementEscapeHatch:
                    if (active)
                        movementEscapeHatchHeld = true;
                    break;
                case BlmScenarioEventType.LeyLinesUnsafe:
                    leyLinesUnsafeIn = Math.Min(leyLinesUnsafeIn, active ? 0 : future);
                    break;
                case BlmScenarioEventType.LookAway:
                    if (active)
                        lookAwayNow = true;
                    lookAwayIn = Math.Min(lookAwayIn, future);
                    break;
                case BlmScenarioEventType.TargetCount:
                    if (active)
                        targets = e.Targets;
                    break;
                case BlmScenarioEventType.ModeSwitch:
                    if (active && e.Rotation.HasValue)
                        modeSwitch = e.Rotation.Value;
                    break;
                case BlmScenarioEventType.TargetDying:
                    if (active)
                        targetDying = true;
                    break;
            }
        }

        return new EnvironmentState(
            TargetAvailable: targetAvailable,
            DowntimeNow: downtimeNow,
            ForcedMoveNow: forcedMoveNow,
            MovementEscapeHatchHeld: movementEscapeHatchHeld,
            LookAwayNow: lookAwayNow,
            TargetDying: targetDying,
            Targets: Math.Max(1, targets),
            ModeSwitch: modeSwitch,
            DowntimeIn: downtimeIn,
            ForcedMoveIn: forcedMoveIn,
            LeyLinesUnsafeIn: leyLinesUnsafeIn,
            LookAwayIn: lookAwayIn);
    }

    private static double AdvanceTimers(State state, double elapsed, EnvironmentState env, MetricAccumulator metrics)
    {
        state.ElementTimerLeft = state.Element == 0 ? 0 : BlmConstants.ElementTimer;

        state.TargetThunderLeft = Math.Max(0, state.TargetThunderLeft - elapsed);
        state.AoeThunderLeft = Math.Max(0, state.AoeThunderLeft - elapsed);
        state.TriplecastLeft = Math.Max(0, state.TriplecastLeft - elapsed);
        if (state.TriplecastLeft <= 0)
            state.TriplecastStacks = 0;
        state.SwiftcastLeft = Math.Max(0, state.SwiftcastLeft - elapsed);
        state.SwiftcastReadyIn = Math.Max(0, state.SwiftcastReadyIn - elapsed);
        state.LeyLinesLeft = Math.Max(0, state.LeyLinesLeft - elapsed);
        state.HaveLeyLines = state.LeyLinesLeft > 0;
        if (!state.HaveLeyLines)
            state.InLeyLines = false;
        state.ManafontReadyIn = Math.Max(0, state.ManafontReadyIn - elapsed);
        state.AmplifierReadyIn = Math.Max(0, state.AmplifierReadyIn - elapsed);
        Recharge(ref state.LeyLinesCharges, MaxLeyLinesChargesForLevel(state.Level), ref state.LeyLinesChargeReadyIn, BlmConstants.LeyLinesChargeRecast, elapsed);
        Recharge(ref state.TriplecastCharges, Unlocked(state, BlmAction.Triplecast) ? 2 : 0, ref state.TriplecastChargeReadyIn, BlmConstants.TriplecastChargeRecast, elapsed);
        state.TransposeReadyIn = Math.Max(0, state.TransposeReadyIn - elapsed);

        var maxPolyglot = MaxPolyglotForLevel(state.Level);
        if (maxPolyglot > 0 && state.Element != 0)
        {
            state.NextPolyglot -= elapsed;
            while (state.NextPolyglot <= 0)
            {
                if (state.Polyglot < maxPolyglot)
                {
                    state.Polyglot++;
                }
                else if (env.TargetAvailable && !env.DowntimeNow && !env.ForcedMoveNow && !env.LookAwayNow)
                {
                    metrics.PolyglotWastedGrantCount++;
                    metrics.PolyglotOvercapSeconds += elapsed;
                }
                state.NextPolyglot += BlmConstants.PolyglotInterval;
            }
        }

        return elapsed;
    }

    private static void TrackPassiveMetrics(BlmScenario scenario, State state, EnvironmentState env, MetricAccumulator metrics, double elapsed)
    {
        if (env.TargetAvailable && !env.DowntimeNow && ThunderUnlockedForTargets(state))
        {
            if (UseAoeThunder(state))
            {
                if (state.AoeThunderLeft <= 0)
                    metrics.RawThunderBlankSeconds += elapsed;
            }
            else if (state.TargetThunderLeft <= 0)
            {
                metrics.RawThunderBlankSeconds += elapsed;
            }
        }

        TrackActionableThunderBlank(scenario, state, env, metrics, elapsed);

        if (MaxPolyglotForLevel(state.Level) > 0 && state.Polyglot >= MaxPolyglotForLevel(state.Level))
        {
            state.CappedPolyglotSeconds += elapsed;
            metrics.RawPolyglotMaxHoldSeconds += elapsed;
        }
        else
        {
            state.CappedPolyglotSeconds = 0;
        }

        if (state.HaveLeyLines)
            metrics.LeyLinesUptimeSeconds += elapsed;
    }

    private static void TrackPhantomPassiveCoverage(BlmScenario scenario, State state, EnvironmentState env, MetricAccumulator metrics)
    {
        if (!scenario.RequiresPhantomCoverage || !state.PhantomActionsEnabled || state.PhantomCharges <= 0)
            return;

        if (!env.TargetAvailable && !state.PhantomTargetLostSuppressionSeen)
        {
            state.PhantomTargetLostSuppressionSeen = true;
            metrics.PhantomTargetLostSuppressions++;
        }
    }

    private static void TrackPhantomGcdCoverage(BlmScenario scenario, State state, EnvironmentState env, BlmAction gcd, MetricAccumulator metrics)
    {
        if (!scenario.RequiresPhantomCoverage)
            return;

        if (IsPhantomGcd(gcd))
        {
            metrics.PhantomActionsUsed++;
            metrics.PhantomNullGuardChecks++;
            if (env.ForcedMoveNow)
                metrics.PhantomMovementFallbacks++;
        }

        if (state.PhantomTargetLostSuppressionSeen
            && !state.PhantomReturnGcdSeen
            && env.TargetAvailable
            && RequiresEnemyTarget(gcd))
        {
            state.PhantomReturnGcdSeen = true;
            metrics.PhantomReturnGcds++;
        }
    }

    private static int ComputeCoverageGap(BlmScenario scenario, MetricAccumulator metrics)
    {
        if (!scenario.RequiresPhantomCoverage)
            return 0;

        if (!scenario.PhantomActionsEnabled)
            return metrics.PhantomActionsUsed == 0 ? 0 : 1;

        if (scenario.Name.Contains("Phantom_TargetLost", StringComparison.OrdinalIgnoreCase))
            return metrics.PhantomTargetLostSuppressions > 0 && metrics.PhantomReturnGcds > 0 ? 0 : 1;

        if (scenario.Name.Contains("Phantom_Movement", StringComparison.OrdinalIgnoreCase))
            return metrics.PhantomMovementFallbacks > 0 && metrics.PhantomNullGuardChecks > 0 ? 0 : 1;

        return metrics.PhantomActionsUsed > 0 && metrics.PhantomNullGuardChecks > 0 ? 0 : 1;
    }

    private static void TrackActionableThunderBlank(BlmScenario scenario, State state, EnvironmentState env, MetricAccumulator metrics, double elapsed)
    {
        if (!env.TargetAvailable
            || env.DowntimeNow
            || env.ForcedMoveNow
            || env.LookAwayNow
            || !state.Thunderhead
            || !ThunderUnlockedForTargets(state)
            || state.Rotation == BlmRotationStrategy.PolyglotOvercapOnly
            || scenario.Thunder is BlmThunderStrategy.Delay or BlmThunderStrategy.InstantOnly)
        {
            state.SingleThunderActionableBlankSeconds = 0;
            state.AoeThunderActionableBlankSeconds = 0;
            return;
        }

        if (UseAoeThunder(state))
        {
            state.SingleThunderActionableBlankSeconds = 0;
            if (state.AoeThunderLeft <= 0)
            {
                state.AoeThunderActionableBlankSeconds += elapsed;
                if (state.AoeThunderActionableBlankSeconds > BlmConstants.Gcd * 2)
                    metrics.ThunderBlankSeconds += elapsed;
            }
            else
            {
                state.AoeThunderActionableBlankSeconds = 0;
            }
        }
        else
        {
            state.AoeThunderActionableBlankSeconds = 0;
            if (state.TargetThunderLeft <= 0)
            {
                state.SingleThunderActionableBlankSeconds += elapsed;
                if (state.SingleThunderActionableBlankSeconds > BlmConstants.Gcd * 2)
                    metrics.ThunderBlankSeconds += elapsed;
            }
            else
            {
                state.SingleThunderActionableBlankSeconds = 0;
            }
        }
    }

    private static IReadOnlyList<BlmAction> SelectOgcds(BlmScenario scenario, State state, EnvironmentState env, int maxWeaves)
    {
        var ogcds = new List<BlmAction>(2);
        if (!env.TargetAvailable && scenario.Manafont != BlmOffensiveStrategy.Force)
            return ogcds;

        if (ShouldUseManafont(scenario, state, env))
            ogcds.Add(BlmAction.Manafont);

        if (ShouldUseAmplifier(scenario, state, env))
            ogcds.Add(BlmAction.Amplifier);

        if (ShouldUseLeyLines(scenario, state, env))
            ogcds.Add(BlmAction.LeyLines);

        var leyLinesReturn = SelectLeyLinesReturnOgcd(scenario, state, env);
        if (leyLinesReturn != BlmAction.None)
            ogcds.Add(leyLinesReturn);

        if (ShouldUseMovementCastEnabler(scenario, state, env, ogcds))
            ogcds.Add(CanUseSwiftcast(state) ? BlmAction.Swiftcast : BlmAction.Triplecast);

        return ogcds.Take(Math.Clamp(maxWeaves, 0, 2)).ToList();
    }

    private static BlmAction SelectLeyLinesReturnOgcd(BlmScenario scenario, State state, EnvironmentState env)
    {
        if (!scenario.Name.Contains("LeyLines_MovementReturn", StringComparison.OrdinalIgnoreCase)
            || !state.HaveLeyLines
            || state.InLeyLines
            || env.ForcedMoveNow)
        {
            return BlmAction.None;
        }

        if (scenario.Name.Contains("LeyLines_MovementReturn_Retrace", StringComparison.OrdinalIgnoreCase) && Unlocked(state, BlmAction.Retrace))
            return BlmAction.Retrace;

        return BlmAction.None;
    }

    private static bool ShouldUseManafont(BlmScenario scenario, State state, EnvironmentState env)
    {
        if (!Unlocked(state, BlmAction.Manafont) || scenario.Manafont == BlmOffensiveStrategy.Delay || state.ManafontReadyIn > 0)
            return false;
        if (!env.TargetAvailable || env.DowntimeNow || state.Element <= 0)
            return false;
        if (scenario.Manafont == BlmOffensiveStrategy.Force)
            return true;
        if (scenario.EncounterHint == BlmEncounterHintStrategy.HoldBurst && state.Time < 60)
            return false;
        return state.MP < 800 || state.AstralSoul >= 5;
    }

    private static bool ShouldUseAmplifier(BlmScenario scenario, State state, EnvironmentState env)
        => Unlocked(state, BlmAction.Amplifier)
        && scenario.Rotation != BlmRotationStrategy.PolyglotOvercapOnly
        && env.TargetAvailable
        && !env.DowntimeNow
        && state.Element != 0
        && state.AmplifierReadyIn <= 0
        && !ShouldHoldForWindurstBurst(scenario, state, state.AmplifierReadyIn)
        && state.Polyglot < MaxPolyglotForLevel(state.Level)
        && state.Polyglot >= MaxPolyglotForLevel(state.Level) - 1;

    private static bool ShouldUseLeyLines(BlmScenario scenario, State state, EnvironmentState env)
    {
        if (!Unlocked(state, BlmAction.LeyLines) || scenario.Leylines == BlmLeylinesStrategy.Delay || state.LeyLinesCharges <= 0 || state.HaveLeyLines)
            return false;
        if (!env.TargetAvailable || env.TargetDying || env.ForcedMoveNow || env.MovementEscapeHatchHeld)
            return false;
        if (scenario.Leylines != BlmLeylinesStrategy.Force && (env.LeyLinesUnsafeIn <= BlmConstants.LeyLinesUnsafeHold || env.DowntimeIn <= BlmConstants.DowntimeLeyLinesHold))
            return false;
        if (WindurstBurstWindowOpen(scenario, state))
            return true;
        if (ShouldHoldForWindurstBurst(scenario, state, state.LeyLinesChargeReadyIn))
            return false;
        return scenario.Leylines == BlmLeylinesStrategy.Force || state.Time < 8 || state.Time % 120 <= BlmConstants.Gcd;
    }

    private static bool ShouldUseMovementCastEnabler(BlmScenario scenario, State state, EnvironmentState env, IReadOnlyList<BlmAction> queuedOgcds)
    {
        if (!env.ForcedMoveNow || env.LookAwayNow || scenario.Triplecast != BlmTriplecastStrategy.Automatic)
            return false;
        if (queuedOgcds.Contains(BlmAction.Swiftcast) || queuedOgcds.Contains(BlmAction.Triplecast))
            return false;
        if (CanUsePolyglotForMovement(state, env) || CanUseThunderForMovement(scenario, state) || CanUseParadoxForMovement(state) || state.Firestarter)
            return false;
        return CanUseSwiftcast(state) || CanUseTriplecast(state);
    }

    private static BlmAction SelectGcd(BlmScenario scenario, State state, EnvironmentState env)
    {
        if (!env.TargetAvailable || env.DowntimeNow)
            return SelectNoTargetGcd(state);

        if (state.Rotation == BlmRotationStrategy.PolyglotOvercapOnly)
            return state.Polyglot > 0 ? BestPolyglotAction(state, env) : BlmAction.None;

        var phantom = SelectPhantomGcd(scenario, state, env);
        if (phantom.HasValue)
            return phantom.Value;

        if (env.LookAwayNow)
            return SelectInstantGcd(scenario, state, env) ?? BlmAction.None;

        if (env.ForcedMoveNow)
        {
            var instant = SelectInstantGcd(scenario, state, env);
            if (instant.HasValue)
                return instant.Value;
            return BlmAction.None;
        }

        var nextAction = ShouldRefreshThunder(scenario, state, env)
            ? BestThunderAction(state, env)
            : env.Targets >= 2 ? SelectAoeGcd(scenario, state, env) : SelectSingleTargetGcd(scenario, state);
        var maxPolyglot = MaxPolyglotForLevel(state.Level);
        if (maxPolyglot > 0 && state.Polyglot >= maxPolyglot && state.NextPolyglot <= ActionCycleTime(state, nextAction))
            return BestPolyglotAction(state, env);

        return nextAction;
    }

    private static BlmAction? SelectPhantomGcd(BlmScenario scenario, State state, EnvironmentState env)
    {
        if (!scenario.RequiresPhantomCoverage
            || !state.PhantomActionsEnabled
            || state.PhantomCharges <= 0
            || !env.TargetAvailable
            || env.DowntimeNow)
        {
            return null;
        }

        if (scenario.Name.Contains("Phantom_Movement", StringComparison.OrdinalIgnoreCase))
            return env.ForcedMoveNow ? BlmAction.OccultQuick : null;

        if (scenario.Name.Contains("Phantom_TargetLost", StringComparison.OrdinalIgnoreCase))
            return state.PhantomTargetLostSuppressionSeen ? BlmAction.Iainuki : null;

        if (state.Time >= 10)
            return BlmAction.OccultComet;

        return null;
    }

    private static BlmAction SelectNoTargetGcd(State state)
        => state.Element < 0 && Unlocked(state, BlmAction.UmbralSoul) ? BlmAction.UmbralSoul : BlmAction.None;

    private static BlmAction? SelectInstantGcd(BlmScenario scenario, State state, EnvironmentState env)
    {
        if (CanUsePolyglotForMovement(state, env))
            return BestPolyglotAction(state, env);
        if (CanUseThunderForMovement(scenario, state))
            return BestThunderAction(state, env);
        if (CanUseParadoxForMovement(state))
            return BlmAction.Paradox;
        if (state.Firestarter && Unlocked(state, BlmAction.Fire3))
            return BlmAction.Fire3;
        if (state.SwiftcastLeft > 0 || state.TriplecastStacks > 0)
            return env.Targets >= 2 ? SelectAoeGcd(scenario, state, env) : SelectSingleTargetGcd(scenario, state);
        return null;
    }

    private static bool ShouldRefreshThunder(BlmScenario scenario, State state, EnvironmentState env)
    {
        if (!state.Thunderhead || scenario.Thunder is BlmThunderStrategy.Delay or BlmThunderStrategy.InstantOnly)
            return false;
        if (scenario.Thunder == BlmThunderStrategy.Force)
            return true;
        return UseAoeThunder(state)
            ? state.AoeThunderLeft <= BlmConstants.ThunderRefreshWindow
            : state.TargetThunderLeft <= BlmConstants.ThunderRefreshWindow;
    }

    private static bool CanUseThunderForMovement(BlmScenario scenario, State state)
    {
        if (!state.Thunderhead || scenario.Thunder is BlmThunderStrategy.Delay or BlmThunderStrategy.ForbidInstant)
            return false;
        if (scenario.Thunder == BlmThunderStrategy.InstantOnly)
            return true;
        return UseAoeThunder(state) ? state.AoeThunderLeft <= 9 : state.TargetThunderLeft <= 9;
    }

    private static bool CanUseParadoxForMovement(State state)
        => state.Paradox && Unlocked(state, BlmAction.Paradox) && (state.Element < 0 || state.MP >= 1600);

    private static bool CanUsePolyglotForMovement(State state, EnvironmentState env)
        => state.Polyglot > 0 && !IsHardcast(BestPolyglotAction(state, env), state);

    private static BlmAction SelectSingleTargetGcd(BlmScenario scenario, State state)
    {
        if (state.Element == 0)
            return state.MP < (Unlocked(state, BlmAction.Fire3) ? 2000 : 800)
                ? Unlocked(state, BlmAction.Blizzard3) ? BlmAction.Blizzard3 : BlmAction.Blizzard1
                : Unlocked(state, BlmAction.Fire3) ? BlmAction.Fire3 : BlmAction.Fire1;

        if (state.Element > 0)
        {
            if (Unlocked(state, BlmAction.FlareStar) && state.AstralSoul >= 6)
                return BlmAction.FlareStar;
            if (state.Firestarter && Unlocked(state, BlmAction.Fire3) && state.MP < FireCost(state))
                return BlmAction.Fire3;
            if (state.Element == 3 && Unlocked(state, BlmAction.Fire4) && state.MP >= FireCost(state) && state.AstralSoul < 6)
                return BlmAction.Fire4;
            if (Unlocked(state, BlmAction.Despair) && state.MP >= 800)
                return BlmAction.Despair;
            if (Unlocked(state, BlmAction.Blizzard3))
                return BlmAction.Blizzard3;
            if (Unlocked(state, BlmAction.Transpose) && state.TransposeReadyIn <= 0)
                return BlmAction.Transpose;
            return BlmAction.Blizzard1;
        }

        if (state.MP < BlmConstants.MaxMP)
            return Unlocked(state, BlmAction.UmbralSoul) ? BlmAction.UmbralSoul : BlmAction.Blizzard1;
        if (Unlocked(state, BlmAction.Blizzard4) && state.Hearts < MaxHeartsForLevel(state.Level))
            return BlmAction.Blizzard4;
        if (state.Paradox && Unlocked(state, BlmAction.Paradox))
            return BlmAction.Paradox;
        return Unlocked(state, BlmAction.Fire3) ? BlmAction.Fire3 : BlmAction.Fire1;
    }

    private static BlmAction SelectAoeGcd(BlmScenario scenario, State state, EnvironmentState env)
    {
        if (!Unlocked(state, BlmAction.Fire2))
            return SelectSingleTargetGcd(scenario, state);

        if (Unlocked(state, BlmAction.FlareStar) && state.AstralSoul >= 6)
            return BlmAction.FlareStar;

        if (state.Element == 0)
            return Unlocked(state, BlmAction.Blizzard3) ? BlmAction.Blizzard3 : BlmAction.Blizzard1;

        if (state.Element > 0)
        {
            if (Unlocked(state, BlmAction.Flare) && state.MP >= 800)
                return BlmAction.Flare;
            if (Unlocked(state, BlmAction.Fire2) && state.MP >= AdjustedFireCost(state, 1500))
                return BlmAction.Fire2;
            if (!Unlocked(state, BlmAction.Fire2) && Unlocked(state, BlmAction.Fire1) && state.MP >= AdjustedFireCost(state, 800))
                return BlmAction.Fire1;
            if (Unlocked(state, BlmAction.Transpose) && state.TransposeReadyIn <= 0)
                return BlmAction.Transpose;
            if (Unlocked(state, BlmAction.FlareStar))
                return state.Polyglot > 0 ? BlmAction.Foul : BlmAction.None;
            return Unlocked(state, BlmAction.Blizzard3) ? BlmAction.Blizzard3 : BlmAction.Blizzard1;
        }

        if (MaxHeartsForLevel(state.Level) > 0)
        {
            if (state.Hearts < MaxHeartsForLevel(state.Level) || state.MP < 2400)
                return env.Targets >= 3 && Unlocked(state, BlmAction.Freeze) ? BlmAction.Freeze : BlmAction.Blizzard4;
        }
        else if (state.MP < 9600)
        {
            if (state.Element == -3 && env.Targets >= 3 && Unlocked(state, BlmAction.Freeze))
                return BlmAction.Freeze;
            return state.Element > -3 && Unlocked(state, BlmAction.Blizzard3) ? BlmAction.Blizzard3 : BlmAction.Blizzard1;
        }
        if (Unlocked(state, BlmAction.Transpose) && state.TransposeReadyIn <= 0)
            return BlmAction.Transpose;
        if (Unlocked(state, BlmAction.FlareStar))
            return state.Paradox ? BlmAction.Paradox : state.Polyglot > 0 ? BlmAction.Foul
                : env.Targets >= 3 ? BlmAction.Freeze : BlmAction.Blizzard4;
        return Unlocked(state, BlmAction.Fire2) ? BlmAction.Fire2 : SelectSingleTargetGcd(scenario, state);
    }

    private static BlmAction BestThunderAction(State state, EnvironmentState env)
    {
        if (env.Targets >= 2 && Unlocked(state, BlmAction.Thunder2))
        {
            if (Unlocked(state, BlmAction.HighThunder2))
                return BlmAction.HighThunder2;
            if (Unlocked(state, BlmAction.Thunder4))
                return BlmAction.Thunder4;
            return BlmAction.Thunder2;
        }

        if (Unlocked(state, BlmAction.HighThunder))
            return BlmAction.HighThunder;
        if (Unlocked(state, BlmAction.Thunder3))
            return BlmAction.Thunder3;
        return BlmAction.Thunder1;
    }

    private static BlmAction BestPolyglotAction(State state, EnvironmentState env)
        => Unlocked(state, BlmAction.Xenoglossy)
            ? env.Targets >= 2 && Unlocked(state, BlmAction.Foul) ? BlmAction.Foul : BlmAction.Xenoglossy
            : BlmAction.Foul;

    private static IReadOnlyList<BlmHardFailRule> ValidateState(State state)
    {
        var fails = new List<BlmHardFailRule>();
        if (state.MP < 0)
            fails.Add(BlmHardFailRule.NegativeMP);
        if (state.Element is < -3 or > 3)
            fails.Add(BlmHardFailRule.ElementOutOfRange);
        if (state.Hearts < 0 || state.Hearts > MaxHeartsForLevel(state.Level))
            fails.Add(BlmHardFailRule.HeartsOutOfRange);
        if (state.Polyglot > MaxPolyglotForLevel(state.Level))
            fails.Add(BlmHardFailRule.PolyglotOvercap);
        if (state.AstralSoul is < 0 or > BlmConstants.MaxAstralSoul)
            fails.Add(BlmHardFailRule.AstralSoulOutOfRange);
        return fails;
    }

    private static IReadOnlyList<BlmHardFailRule> ValidateGcd(BlmScenario scenario, State state, EnvironmentState env, BlmAction action)
    {
        var fails = new List<BlmHardFailRule>();
        if (action == BlmAction.None)
            return fails;
        if (!Unlocked(state, action))
            fails.Add(BlmHardFailRule.IllegalAction);
        var mpCost = action switch
        {
            BlmAction.Fire1 or BlmAction.Fire4 => FireCost(state),
            BlmAction.Fire2 => AdjustedFireCost(state, 1500),
            BlmAction.Fire3 => state.Firestarter ? 0 : AdjustedFireCost(state, 2000),
            BlmAction.Paradox => state.Element > 0 ? 1600 : 0,
            BlmAction.Flare or BlmAction.Despair or BlmAction.Scathe => 800,
            _ => 0
        };
        if (state.MP < mpCost)
            fails.Add(BlmHardFailRule.IllegalAction);
        if (!env.TargetAvailable && RequiresEnemyTarget(action))
            fails.Add(BlmHardFailRule.NoTargetGcd);
        if (action == BlmAction.Fire4 && state.Element != 3)
            fails.Add(BlmHardFailRule.Fire4OutsideAF3);
        if (action == BlmAction.Blizzard4 && state.Element >= 0)
            fails.Add(BlmHardFailRule.Blizzard4OutsideUI);
        if (action == BlmAction.Despair && (state.Element <= 0 || state.MP < 800))
            fails.Add(BlmHardFailRule.DespairInvalid);
        if (action == BlmAction.FlareStar && state.AstralSoul < 6)
            fails.Add(BlmHardFailRule.FlareStarWithoutSoul);
        if ((action is BlmAction.Xenoglossy or BlmAction.Foul) && state.Polyglot <= 0)
            fails.Add(BlmHardFailRule.PolyglotInvalid);
        if (IsThunder(action) && !state.Thunderhead)
            fails.Add(BlmHardFailRule.ThunderWithoutThunderhead);
        if (action == BlmAction.Paradox && !state.Paradox)
            fails.Add(BlmHardFailRule.ParadoxInvalid);
        if (action == BlmAction.Transpose && state.TransposeReadyIn > 0)
            fails.Add(BlmHardFailRule.TransposeCooldown);
        if (env.ForcedMoveNow && IsHardcast(action, state))
            fails.Add(BlmHardFailRule.GcdStop);
        if (env.LookAwayNow && IsHardcast(action, state))
            fails.Add(BlmHardFailRule.GcdStop);
        return fails;
    }

    private static IReadOnlyList<BlmHardFailRule> ValidateOgcd(BlmScenario scenario, State state, EnvironmentState env, BlmAction action)
    {
        var fails = new List<BlmHardFailRule>();
        if (!Unlocked(state, action))
            fails.Add(BlmHardFailRule.IllegalAction);
        if (action == BlmAction.BetweenTheLines)
            fails.Add(BlmHardFailRule.BetweenTheLinesAutoPush);
        if (action == BlmAction.Manafont && (scenario.Manafont == BlmOffensiveStrategy.Delay || !env.TargetAvailable || env.DowntimeNow || state.Element <= 0))
            fails.Add(BlmHardFailRule.ManafontInvalid);
        if (action == BlmAction.Manafont && state.ManafontReadyIn > 0
            || action == BlmAction.Amplifier && state.AmplifierReadyIn > 0
            || action == BlmAction.LeyLines && state.LeyLinesCharges <= 0
            || action == BlmAction.Swiftcast && state.SwiftcastReadyIn > 0
            || action == BlmAction.Triplecast && (state.TriplecastCharges <= 0 || state.TriplecastStacks > 0))
        {
            fails.Add(BlmHardFailRule.CooldownInvalid);
        }
        if (action == BlmAction.Amplifier && (state.Element == 0 || state.Polyglot >= MaxPolyglotForLevel(state.Level)))
            fails.Add(BlmHardFailRule.IllegalAction);
        if (action == BlmAction.LeyLines && scenario.Leylines != BlmLeylinesStrategy.Force && (env.LeyLinesUnsafeIn <= BlmConstants.LeyLinesUnsafeHold || env.DowntimeIn <= BlmConstants.DowntimeLeyLinesHold))
            fails.Add(BlmHardFailRule.LeyLinesUnsafe);
        if (action == BlmAction.LeyLines && (env.ForcedMoveNow || env.MovementEscapeHatchHeld))
            fails.Add(BlmHardFailRule.LeyLinesDuringMovement);
        return fails;
    }

    private static void ApplyOgcd(BlmScenario scenario, State state, BlmAction action, MetricAccumulator metrics)
    {
        metrics.OgcdCount++;
        switch (action)
        {
            case BlmAction.Manafont:
                EnterElement(state, 3);
                state.MP = BlmConstants.MaxMP;
                state.Hearts = MaxHeartsForLevel(state.Level);
                state.Thunderhead = true;
                state.Paradox = Unlocked(state, BlmAction.Paradox);
                state.ManafontReadyIn = state.Level >= 84 ? BlmConstants.ManafontRecast : BlmConstants.ManafontPreEnhancedRecast;
                metrics.ManafontCount++;
                break;
            case BlmAction.Amplifier:
                state.Polyglot = Math.Min(MaxPolyglotForLevel(state.Level), state.Polyglot + 1);
                state.AmplifierReadyIn = BlmConstants.AmplifierRecast;
                metrics.AmplifierCount++;
                break;
            case BlmAction.LeyLines:
                state.HaveLeyLines = true;
                state.InLeyLines = true;
                state.LeyLinesLeft = BlmConstants.LeyLinesDuration;
                ConsumeCharge(ref state.LeyLinesCharges, ref state.LeyLinesChargeReadyIn, BlmConstants.LeyLinesChargeRecast);
                break;
            case BlmAction.Swiftcast:
                state.SwiftcastLeft = BlmConstants.SwiftcastDuration;
                state.SwiftcastReadyIn = state.Level >= 94 ? BlmConstants.EnhancedSwiftcastRecast : BlmConstants.SwiftcastRecast;
                break;
            case BlmAction.Triplecast:
                state.TriplecastLeft = BlmConstants.TriplecastDuration;
                state.TriplecastStacks = 3;
                ConsumeCharge(ref state.TriplecastCharges, ref state.TriplecastChargeReadyIn, BlmConstants.TriplecastChargeRecast);
                break;
            case BlmAction.BetweenTheLines:
                state.InLeyLines = state.HaveLeyLines;
                metrics.BetweenTheLinesCount++;
                break;
            case BlmAction.Retrace:
                state.InLeyLines = state.HaveLeyLines;
                metrics.RetraceCount++;
                break;
            case BlmAction.Transpose:
                // an oGCD in the game; only external policies weave it (the built-in policy casts it in the GCD slot)
                EnterElement(state, state.Element > 0 ? -1 : 1);
                state.TransposeReadyIn = 5;
                break;
        }
    }

    private static void ApplyGcd(BlmScenario scenario, State state, BlmAction action)
    {
        SpendInstant(state, action);
        switch (action)
        {
            case BlmAction.Fire1:
                var fire1InAstralFire = state.Element > 0;
                var fire1Cost = AdjustedFireCost(state, 800);
                EnterBasicElement(state, fire: true);
                state.MP -= Math.Min(state.MP, fire1Cost);
                state.Firestarter |= state.Level >= 42 && NextProcRoll(state) < 0.4;
                if (fire1InAstralFire)
                    ConsumeHeart(state);
                break;
            case BlmAction.Fire2:
                var fire2InAstralFire = state.Element > 0;
                var fire2Cost = AdjustedFireCost(state, 1500);
                EnterElement(state, MaxElementStacksForLevel(state.Level));
                state.MP -= Math.Min(state.MP, fire2Cost);
                if (fire2InAstralFire)
                    ConsumeHeart(state);
                break;
            case BlmAction.Fire3:
                var fire3InAstralFire = state.Element > 0;
                var fire3Cost = state.Firestarter ? 0 : AdjustedFireCost(state, 2000);
                EnterElement(state, 3);
                state.MP -= Math.Min(state.MP, fire3Cost);
                state.Firestarter = false;
                if (fire3InAstralFire)
                    ConsumeHeart(state);
                break;
            case BlmAction.Fire4:
                state.MP -= Math.Min(state.MP, FireCost(state));
                if (Unlocked(state, BlmAction.FlareStar))
                    state.AstralSoul = Math.Min(BlmConstants.MaxAstralSoul, state.AstralSoul + 1);
                ConsumeHeart(state);
                break;
            case BlmAction.Despair:
                state.MP = 0;
                RefreshElement(state);
                break;
            case BlmAction.Flare:
                EnterElement(state, 3);
                state.MP = state.Hearts > 0 ? state.MP / 3 : 0;
                if (Unlocked(state, BlmAction.FlareStar))
                    state.AstralSoul = Math.Min(BlmConstants.MaxAstralSoul, state.AstralSoul + 3);
                state.Hearts = 0;
                break;
            case BlmAction.FlareStar:
                state.AstralSoul = 0;
                break;
            case BlmAction.Blizzard1:
                EnterBasicElement(state, fire: false);
                RestoreIceMP(state);
                break;
            case BlmAction.Blizzard2:
                EnterElement(state, -MaxElementStacksForLevel(state.Level));
                RestoreIceMP(state);
                break;
            case BlmAction.Blizzard3:
                EnterElement(state, -3);
                state.MP = BlmConstants.MaxMP;
                state.AstralSoul = 0;
                break;
            case BlmAction.Freeze:
                state.Hearts = MaxHeartsForLevel(state.Level);
                RestoreIceMP(state);
                break;
            case BlmAction.Blizzard4:
                state.Hearts = MaxHeartsForLevel(state.Level);
                RestoreIceMP(state);
                RefreshElement(state);
                break;
            case BlmAction.Paradox:
                state.Paradox = false;
                if (state.Element > 0)
                {
                    state.MP -= 1600;
                    state.Firestarter = true;
                }
                RefreshElement(state);
                break;
            case BlmAction.Transpose:
                if (state.Element > 0)
                    EnterElement(state, -1);
                else if (state.Element < 0)
                    EnterElement(state, 1);
                else
                    EnterElement(state, -1);
                state.TransposeReadyIn = 5;
                break;
            case BlmAction.UmbralSoul:
                EnterBasicElement(state, fire: false);
                state.Hearts = Math.Min(MaxHeartsForLevel(state.Level), state.Hearts + 1);
                RestoreIceMP(state);
                break;
            case BlmAction.Thunder1:
            case BlmAction.Thunder3:
            case BlmAction.HighThunder:
                state.TargetThunderLeft = ThunderDuration(action);
                state.Thunderhead = false;
                break;
            case BlmAction.Thunder2:
            case BlmAction.Thunder4:
            case BlmAction.HighThunder2:
                state.AoeThunderLeft = ThunderDuration(action);
                state.Thunderhead = false;
                break;
            case BlmAction.Xenoglossy:
            case BlmAction.Foul:
                state.Polyglot = Math.Max(0, state.Polyglot - 1);
                break;
            case BlmAction.Zeninage:
            case BlmAction.Iainuki:
            case BlmAction.OccultQuick:
            case BlmAction.OccultComet:
                state.PhantomCharges = Math.Max(0, state.PhantomCharges - 1);
                break;
            case BlmAction.Scathe:
                break;
        }

        state.MP = Math.Clamp(state.MP, 0, BlmConstants.MaxMP);
        state.Hearts = Math.Clamp(state.Hearts, 0, MaxHeartsForLevel(state.Level));
        state.AstralSoul = Math.Clamp(state.AstralSoul, 0, BlmConstants.MaxAstralSoul);
        state.Element = Math.Clamp(state.Element, -3, 3);
    }

    private static void SpendInstant(State state, BlmAction action)
    {
        if (!RequiresCastEnabler(action, state))
            return;

        if (state.SwiftcastLeft > 0)
        {
            state.SwiftcastLeft = 0;
            return;
        }

        if (state.TriplecastStacks > 0)
        {
            state.TriplecastStacks--;
            if (state.TriplecastStacks == 0)
                state.TriplecastLeft = 0;
        }
    }

    private static void EnterElement(State state, int element)
    {
        var previous = state.Element;
        state.Element = Math.Clamp(element, -3, 3);
        state.ElementTimerLeft = state.Element == 0 ? 0 : BlmConstants.ElementTimer;
        if (state.Element <= 0)
            state.AstralSoul = 0;

        if (previous == 0 && state.Element != 0 && MaxPolyglotForLevel(state.Level) > 0)
            state.NextPolyglot = BlmConstants.PolyglotInterval;

        if (state.Element != 0 && (previous == 0 || Math.Sign(previous) != Math.Sign(state.Element)))
            state.Thunderhead = ThunderUnlockedForTargets(state);

        if (Unlocked(state, BlmAction.Paradox) && previous != 0 && state.Element != 0 && Math.Sign(previous) != Math.Sign(state.Element))
        {
            if (state.Element < 0 && Math.Abs(previous) == 3)
                state.Paradox = true;
            else if (state.Element > 0 && previous == -3 && state.Hearts == MaxHeartsForLevel(state.Level))
                state.Paradox = true;
        }
    }

    private static void RefreshElement(State state)
    {
        if (state.Element != 0)
            state.ElementTimerLeft = BlmConstants.ElementTimer;
    }

    private static void ConsumeHeart(State state)
    {
        if (state.Hearts > 0)
            state.Hearts--;
    }

    private static void EnterBasicElement(State state, bool fire)
    {
        var sign = fire ? 1 : -1;
        var next = Math.Sign(state.Element) == sign
            ? sign * Math.Min(MaxElementStacksForLevel(state.Level), Math.Abs(state.Element) + 1)
            : state.Element == 0 ? sign : 0;
        EnterElement(state, next);
    }

    private static void RestoreIceMP(State state)
    {
        if (state.Element >= 0)
            return;
        var restored = Math.Abs(state.Element) switch
        {
            1 => 2500,
            2 => 5000,
            _ => BlmConstants.MaxMP,
        };
        state.MP = Math.Min(BlmConstants.MaxMP, state.MP + restored);
    }

    private static void TrackAoeSwitchHardFails(State state, BlmAction action, List<BlmHardFailRule> fails)
    {
        if (state.Targets == 1 && IsAoeGcd(action))
        {
            state.ConsecutiveAoeOnSingle++;
            if (state.ConsecutiveAoeOnSingle > 2)
                fails.Add(BlmHardFailRule.AoeStuckOnSingle);
        }
        else
        {
            state.ConsecutiveAoeOnSingle = 0;
        }

        if (state.Targets >= 2 && IsSingleTargetGcd(action) && HasAoeAlternative(state, action))
        {
            state.ConsecutiveSingleOnAoe++;
            if (state.ConsecutiveSingleOnAoe >= 4)
                fails.Add(BlmHardFailRule.SingleStuckOnAoe);
        }
        else
        {
            state.ConsecutiveSingleOnAoe = 0;
        }
    }

    private static bool ShouldRequireGcd(State state, EnvironmentState env, BlmScenario scenario)
        => env.TargetAvailable && !env.DowntimeNow && !env.ForcedMoveNow && !env.LookAwayNow && scenario.Rotation != BlmRotationStrategy.PolyglotOvercapOnly;

    private static bool RequiresEnemyTarget(BlmAction action)
        => action is not BlmAction.None and not BlmAction.UmbralSoul and not BlmAction.Transpose;

    private static bool IsHardcast(BlmAction action, State state)
        => EffectiveCastTime(state, action) > 0;

    private static bool RequiresCastEnabler(BlmAction action, State state)
    {
        if (action == BlmAction.Fire3 && state.Firestarter)
            return false;

        if (action == BlmAction.Foul)
            return state.Level < 80;

        if (action == BlmAction.Despair)
            return state.Level < 100;

        return action is BlmAction.Fire1 or BlmAction.Fire2 or BlmAction.Fire3 or BlmAction.Fire4
            or BlmAction.Flare or BlmAction.FlareStar or BlmAction.Blizzard1 or BlmAction.Blizzard2
            or BlmAction.Blizzard3 or BlmAction.Blizzard4 or BlmAction.Freeze;
    }

    private static bool IsPhantomGcd(BlmAction action)
        => action is BlmAction.Zeninage or BlmAction.Iainuki or BlmAction.OccultQuick or BlmAction.OccultComet;

    private static bool IsAoeGcd(BlmAction action)
        => action is BlmAction.Fire2 or BlmAction.Flare or BlmAction.Blizzard2 or BlmAction.Freeze or BlmAction.Thunder2 or BlmAction.Thunder4 or BlmAction.HighThunder2 or BlmAction.Foul or BlmAction.Zeninage;

    private static bool IsSingleTargetGcd(BlmAction action)
        => action is BlmAction.Fire1 or BlmAction.Fire3 or BlmAction.Fire4 or BlmAction.Despair or BlmAction.Blizzard1 or BlmAction.Blizzard3 or BlmAction.Blizzard4 or BlmAction.Thunder1 or BlmAction.Thunder3 or BlmAction.HighThunder or BlmAction.Xenoglossy or BlmAction.Iainuki or BlmAction.OccultQuick or BlmAction.OccultComet;

    private static bool HasAoeAlternative(State state, BlmAction action)
        => action switch
        {
            BlmAction.Fire1 or BlmAction.Fire3 or BlmAction.Fire4 or BlmAction.Despair
                => Unlocked(state, BlmAction.Fire2) || Unlocked(state, BlmAction.Flare),
            BlmAction.Blizzard1 or BlmAction.Blizzard3 or BlmAction.Blizzard4
                => state.Element < 0 && state.Targets >= 3 && Unlocked(state, BlmAction.Freeze),
            BlmAction.Thunder1 or BlmAction.Thunder3 or BlmAction.HighThunder
                => Unlocked(state, BlmAction.Thunder2),
            BlmAction.Xenoglossy
                => Unlocked(state, BlmAction.Foul),
            _ => true,
        };

    private static bool IsThunder(BlmAction action)
        => action is BlmAction.Thunder1 or BlmAction.Thunder2 or BlmAction.Thunder3 or BlmAction.Thunder4 or BlmAction.HighThunder or BlmAction.HighThunder2;

    private static bool ThunderUnlockedForTargets(State state)
        => Unlocked(state, BlmAction.Thunder1);

    private static bool UseAoeThunder(State state)
        => state.Targets >= 2 && Unlocked(state, BlmAction.Thunder2);

    private static int FireCost(State state)
        => AdjustedFireCost(state, 800);

    private static int AdjustedFireCost(State state, int cost)
        => state.Element < 0 ? 0 : state.Element > 0 && state.Hearts == 0 ? cost * 2 : cost;

    private static bool CanUseSwiftcast(State state)
        => Unlocked(state, BlmAction.Swiftcast) && state.SwiftcastReadyIn <= 0 && state.SwiftcastLeft <= 0;

    private static bool CanUseTriplecast(State state)
        => Unlocked(state, BlmAction.Triplecast) && state.TriplecastCharges > 0 && state.TriplecastStacks == 0;

    internal static void ValidateRuleset()
    {
        static void Require(bool valid, string rule)
        {
            if (!valid)
                throw new InvalidOperationException($"BLM 7.5 emulator invariant failed: {rule}");
        }

        var scenario = new BlmScenario { Name = "RulesetBoundary", Category = BlmScenarioCategory.Coverage };
        var state = new State { Level = 100, Element = 3, ElementTimerLeft = BlmConstants.ElementTimer, AstralSoul = 5, Paradox = true, NextPolyglot = 1, MP = 10000, Hearts = 3 };
        AdvanceTimers(state, 31.5, BuildEnvironment(scenario, 0, 1), new());
        Require(state.Element == 3 && state.AstralSoul == 5 && state.Paradox, "element must not expire after 15 seconds");
        Require(state.Polyglot == 2 && Math.Abs(state.NextPolyglot - 29.5) < 0.001, "Polyglot preserves the timer remainder");
        Require(CurrentGcdDuration(state) == 2.5 && ActionCycleTime(state, BlmAction.Fire4) == 2.5, "unbuffed reference GCD is 2.50");
        state.InLeyLines = true;
        Require(Math.Abs(CurrentGcdDuration(state) - 2.12) < 0.001, "Ley Lines GCD rounds to 2.12");
        state.InLeyLines = false;
        Require(EffectiveCastTime(state, BlmAction.Fire4) == 2 && EffectiveCastTime(state, BlmAction.Flare) == 2, "7.5 cast times");
        ApplyGcd(scenario, state, BlmAction.Flare);
        Require(state.Hearts == 0 && state.MP == 3333, "Flare consumes every heart");
        state.MP = 1600;
        ApplyGcd(scenario, state, BlmAction.Paradox);
        Require(state.MP == 0 && state.Firestarter, "AF Paradox costs 1600 MP");
        state.Element = -3;
        state.MP = 10000;
        state.Paradox = true;
        ApplyGcd(scenario, state, BlmAction.Paradox);
        Require(state.MP == 10000 && state.Firestarter, "UI Paradox preserves Firestarter");
        Require(EffectiveCastTime(state, BlmAction.Fire3) == 0, "Firestarter is instant");
        state.Firestarter = false;
        Require(EffectiveCastTime(state, BlmAction.Fire3) == 1.75, "opposite element III halves cast time");
        state.Element = -1;
        Require(EffectiveCastTime(state, BlmAction.Fire3) == 3.5, "opposite element I does not halve cast time");

        foreach (var level in new[] { 12, 18, 20, 34, 35, 40, 50, 57, 58, 70, 82, 100 })
        foreach (var targets in new[] { 2, 3, 8 })
        foreach (var element in new[] { -MaxElementStacksForLevel(level), -1, 0, MaxElementStacksForLevel(level) })
        foreach (var mp in new[] { 0, 799, 800, 2399, 2400, 10000 })
        {
            var aoe = new State { Level = level, Targets = targets, Element = element, MP = mp, TransposeReadyIn = 5, ElementTimerLeft = BlmConstants.ElementTimer, NextPolyglot = BlmConstants.PolyglotInterval };
            var env = BuildEnvironment(scenario, 0, targets);
            var recovered = false;
            for (var step = 0; step < 12; ++step)
            {
                var action = SelectAoeGcd(scenario, aoe, env);
                var waitingForTranspose = action == BlmAction.None && level == 100 && aoe.Element > 0 && aoe.MP < 800 && aoe.TransposeReadyIn > 0;
                Require(action != BlmAction.Blizzard2 && (waitingForTranspose || Unlocked(aoe, action)), $"AoE excludes Blizzard II at lv={level} targets={targets} element={element} mp={mp}");
                if (level == 100 && aoe.Element != 0)
                    Require(action is not BlmAction.Blizzard3 and not BlmAction.Fire2, "level 100 AoE changes element with Transpose");
                recovered |= action is BlmAction.Fire1 or BlmAction.Fire2 or BlmAction.Flare;
                var elapsed = ActionCycleTime(aoe, action);
                ApplyGcd(scenario, aoe, action);
                AdvanceTimers(aoe, elapsed, env, new());
            }
            Require(recovered, $"AoE returns to fire without Blizzard II at lv={level} targets={targets} element={element} mp={mp}");
        }
    }

    private static double CurrentGcdDuration(State state)
        => Math.Floor(BlmConstants.Gcd * (state.InLeyLines ? BlmConstants.LeyLinesSpeedMultiplier : 1) * 100) / 100;

    private static double ActionCycleTime(State state, BlmAction action)
        => Math.Max(CurrentGcdDuration(state), EffectiveCastTime(state, action));

    private static int MaxWeavesAfter(State state, BlmAction action)
    {
        if (action == BlmAction.None)
            return 2;

        var weaveWindow = CurrentGcdDuration(state) - EffectiveCastTime(state, action);
        return Math.Clamp((int)Math.Floor(weaveWindow / BlmConstants.OgcdAnimationLock), 1, 2);
    }

    private static double EffectiveCastTime(State state, BlmAction action)
    {
        if (!RequiresCastEnabler(action, state) || state.SwiftcastLeft > 0 || state.TriplecastStacks > 0)
            return 0;

        var castTime = action switch
        {
            BlmAction.Fire1 or BlmAction.Blizzard1 or BlmAction.Blizzard4 or BlmAction.Fire4 or BlmAction.Freeze or BlmAction.Flare or BlmAction.FlareStar => 2.0,
            BlmAction.Fire2 or BlmAction.Blizzard2 => 3.0,
            BlmAction.Fire3 => state.Element == -3 ? 1.75 : 3.5,
            BlmAction.Blizzard3 => state.Element == 3 ? 1.75 : 3.5,
            BlmAction.Despair => 3.0,
            BlmAction.Foul => 2.5,
            _ => 0
        };
        return castTime * (state.InLeyLines ? BlmConstants.LeyLinesSpeedMultiplier : 1);
    }

    private static void ConsumeCharge(ref int charges, ref double chargeReadyIn, double recast)
    {
        if (charges <= 0)
            return;

        --charges;
        if (chargeReadyIn <= 0)
            chargeReadyIn = recast;
    }

    private static void Recharge(ref int charges, int maxCharges, ref double chargeReadyIn, double recast, double elapsed)
    {
        if (maxCharges <= 0)
        {
            charges = 0;
            chargeReadyIn = 0;
            return;
        }

        charges = Math.Clamp(charges, 0, maxCharges);
        if (charges >= maxCharges)
        {
            chargeReadyIn = 0;
            return;
        }

        chargeReadyIn -= elapsed;
        while (chargeReadyIn <= 0 && charges < maxCharges)
        {
            ++charges;
            chargeReadyIn = charges < maxCharges ? chargeReadyIn + recast : 0;
        }
    }

    private static uint StableScenarioSeed(string value)
    {
        var hash = 2166136261u;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        return hash == 0 ? 0x9E3779B9u : hash;
    }

    private static double NextProcRoll(State state)
    {
        var value = state.ProcRngState;
        value ^= value << 13;
        value ^= value >> 17;
        value ^= value << 5;
        state.ProcRngState = value;
        return value / ((double)uint.MaxValue + 1);
    }

    private static bool WindurstProfileActive(BlmScenario scenario)
        => scenario.Rotation == BlmRotationStrategy.WindurstThirdWalk
        && scenario.ContentFinderConditionID == WindurstThirdWalkCFCID
        && WindurstBurstWindows(scenario.EncounterNameID).Length > 0;

    private static WindurstBurstWindow[] WindurstBurstWindows(uint encounterNameID)
        => encounterNameID switch
        {
            WindurstShantottoNameID => WindurstShantottoBurstWindows,
            WindurstAlexanderNameID => WindurstAlexanderBurstWindows,
            WindurstPromathiaNameID => WindurstPromathiaBurstWindows,
            WindurstHollowKingNameID => WindurstHollowKingBurstWindows,
            _ => NoWindurstBurstWindows
        };

    private static bool WindurstBurstWindowOpen(BlmScenario scenario, State state)
    {
        if (!WindurstProfileActive(scenario))
            return false;

        foreach (var window in WindurstBurstWindows(scenario.EncounterNameID))
            if (state.Time >= window.Time - window.Before && state.Time <= window.Time + window.After)
                return true;

        return false;
    }

    private static bool ShouldHoldForWindurstBurst(BlmScenario scenario, State state, double readyIn)
    {
        if (!WindurstProfileActive(scenario))
            return false;

        foreach (var window in WindurstBurstWindows(scenario.EncounterNameID))
        {
            var startsIn = window.Time - window.Before - state.Time;
            if (startsIn > WindurstBurstHoldLead)
                return false;
            if (startsIn > 0)
                return readyIn <= startsIn + window.Before + window.After;
        }

        return false;
    }

    private static int MaxHeartsForLevel(int level)
        => level >= 58 ? BlmConstants.MaxHearts : 0;

    private static int MaxLeyLinesChargesForLevel(int level)
        => level >= 96 ? 2 : level >= 52 ? 1 : 0;

    private static int MaxElementStacksForLevel(int level)
        => level >= 35 ? 3 : level >= 20 ? 2 : 1;

    private static int MaxPolyglotForLevel(int level)
        => level >= 98 ? 3 : level >= 80 ? 2 : level >= 70 ? 1 : 0;

    private static double ThunderDuration(BlmAction action)
        => action switch
        {
            BlmAction.Thunder1 => 24,
            BlmAction.Thunder2 => 18,
            BlmAction.Thunder3 => 27,
            BlmAction.Thunder4 => 21,
            BlmAction.HighThunder => 30,
            BlmAction.HighThunder2 => 24,
            _ => 0
        };

    private static bool Unlocked(State state, BlmAction action)
        => action switch
        {
            BlmAction.None => true,
            BlmAction.Fire1 => true,
            BlmAction.Fire2 => state.Level >= 18,
            BlmAction.Blizzard1 => true,
            BlmAction.Thunder1 => state.Level >= 6,
            BlmAction.Fire3 => state.Level >= 35,
            BlmAction.Blizzard3 => state.Level >= 35,
            BlmAction.Fire4 => state.Level >= 60,
            BlmAction.Blizzard4 => state.Level >= 58,
            BlmAction.Despair => state.Level >= 72,
            BlmAction.Flare => state.Level >= 50,
            BlmAction.FlareStar => state.Level >= 100,
            BlmAction.Blizzard2 => state.Level >= 12,
            BlmAction.Freeze => state.Level >= 40,
            BlmAction.Paradox => state.Level >= 90,
            BlmAction.Transpose => state.Level >= 4,
            BlmAction.UmbralSoul => state.Level >= 35,
            BlmAction.Thunder2 => state.Level >= 26,
            BlmAction.Thunder3 => state.Level >= 45,
            BlmAction.Thunder4 => state.Level >= 64,
            BlmAction.HighThunder => state.Level >= 92,
            BlmAction.HighThunder2 => state.Level >= 92,
            BlmAction.Xenoglossy => state.Level >= 80,
            BlmAction.Foul => state.Level >= 70,
            BlmAction.Manafont => state.Level >= 30,
            BlmAction.Amplifier => state.Level >= 86,
            BlmAction.LeyLines => state.Level >= 52,
            BlmAction.BetweenTheLines => state.Level >= 62,
            BlmAction.Retrace => state.Level >= 96,
            BlmAction.Swiftcast => state.Level >= 18,
            BlmAction.Triplecast => state.Level >= 66,
            BlmAction.LucidDreaming => state.Level >= 14,
            BlmAction.Zeninage => state.PhantomActionsEnabled,
            BlmAction.Iainuki => state.PhantomActionsEnabled,
            BlmAction.OccultQuick => state.PhantomActionsEnabled,
            BlmAction.OccultComet => state.PhantomActionsEnabled,
            BlmAction.Scathe => state.Level >= 15,
            _ => false
        };

    private static string BuildReason(EnvironmentState env)
    {
        if (env.DowntimeNow)
            return "downtime";
        if (!env.TargetAvailable)
            return "target-lost";
        if (env.LookAwayNow)
            return "lookaway";
        if (env.ForcedMoveNow)
            return "forced-move";
        return "normal";
    }

    private static BlmPolicyView View(State s, EnvironmentState e) => new()
    {
        Time = s.Time, Targets = e.Targets, Element = s.Element, MP = s.MP, Hearts = s.Hearts, Polyglot = s.Polyglot, NextPolyglot = s.NextPolyglot,
        AstralSoul = s.AstralSoul, Paradox = s.Paradox, Thunderhead = s.Thunderhead, Firestarter = s.Firestarter,
        ThunderLeft = UseAoeThunder(s) ? s.AoeThunderLeft : s.TargetThunderLeft, TriplecastLeft = s.TriplecastLeft, TriplecastStacks = s.TriplecastStacks,
        TriplecastCharges = s.TriplecastCharges, TriplecastChargeReadyIn = s.TriplecastChargeReadyIn, SwiftcastLeft = s.SwiftcastLeft, SwiftcastReadyIn = s.SwiftcastReadyIn,
        LeyLinesLeft = s.LeyLinesLeft, InLeyLines = s.InLeyLines, LeyLinesCharges = s.LeyLinesCharges, LeyLinesChargeReadyIn = s.LeyLinesChargeReadyIn,
        ManafontReadyIn = s.ManafontReadyIn, AmplifierReadyIn = s.AmplifierReadyIn, TransposeReadyIn = s.TransposeReadyIn,
        TargetAvailable = e.TargetAvailable, DowntimeNow = e.DowntimeNow, ForcedMoveNow = e.ForcedMoveNow, MovementEscapeHatchHeld = e.MovementEscapeHatchHeld,
        LookAwayNow = e.LookAwayNow, TargetDying = e.TargetDying, DowntimeIn = e.DowntimeIn, ForcedMoveIn = e.ForcedMoveIn, LeyLinesUnsafeIn = e.LeyLinesUnsafeIn, LookAwayIn = e.LookAwayIn,
        Rotation = s.Rotation, Level = s.Level
    };

    private sealed record EnvironmentState(
        bool TargetAvailable,
        bool DowntimeNow,
        bool ForcedMoveNow,
        bool MovementEscapeHatchHeld,
        bool LookAwayNow,
        bool TargetDying,
        int Targets,
        BlmRotationStrategy? ModeSwitch,
        double DowntimeIn,
        double ForcedMoveIn,
        double LeyLinesUnsafeIn,
        double LookAwayIn);
}

// Read-only view of one emulator frame for an external policy (BlmRotationEmulator.PolicyOverride).
public sealed record BlmPolicyView
{
    public double Time { get; init; }
    public int Level { get; init; }
    public BlmRotationStrategy Rotation { get; init; }
    public int Targets { get; init; }
    public int Element { get; init; }
    public int MP { get; init; }
    public int Hearts { get; init; }
    public int Polyglot { get; init; }
    public double NextPolyglot { get; init; }
    public int AstralSoul { get; init; }
    public bool Paradox { get; init; }
    public bool Thunderhead { get; init; }
    public bool Firestarter { get; init; }
    public double ThunderLeft { get; init; }
    public double TriplecastLeft { get; init; }
    public int TriplecastStacks { get; init; }
    public int TriplecastCharges { get; init; }
    public double TriplecastChargeReadyIn { get; init; }
    public double SwiftcastLeft { get; init; }
    public double SwiftcastReadyIn { get; init; }
    public double LeyLinesLeft { get; init; }
    public bool InLeyLines { get; init; }
    public int LeyLinesCharges { get; init; }
    public double LeyLinesChargeReadyIn { get; init; }
    public double ManafontReadyIn { get; init; }
    public double AmplifierReadyIn { get; init; }
    public double TransposeReadyIn { get; init; }
    public bool TargetAvailable { get; init; }
    public bool DowntimeNow { get; init; }
    public bool ForcedMoveNow { get; init; }
    public bool MovementEscapeHatchHeld { get; init; }
    public bool LookAwayNow { get; init; }
    public bool TargetDying { get; init; }
    public double DowntimeIn { get; init; }
    public double ForcedMoveIn { get; init; }
    public double LeyLinesUnsafeIn { get; init; }
    public double LookAwayIn { get; init; }
}
