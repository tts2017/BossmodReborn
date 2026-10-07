namespace RprRegression;

public static class RealRprHarness
{
    private static readonly double[] Gcds = [2.47, 2.48, 2.49, 2.50];
    private static readonly OpenerBurstMode[] Openers = [OpenerBurstMode.TwoGcd, OpenerBurstMode.TwoPointFiveSecond, OpenerBurstMode.ZeroSecond];
    private static readonly PotionMode[] Potions = [PotionMode.OpenerAndEvenBurst, PotionMode.EvenBurstExceptOpener, PotionMode.Off];
    private static readonly RotationMode[] Modes = [RotationMode.Full, RotationMode.Full, RotationMode.Full, RotationMode.Basic];
    private static readonly TrueNorthMode[] TrueNorthModes = [TrueNorthMode.Auto, TrueNorthMode.Off];
    private static readonly int[] Levels = [1, 5, 10, 15, 25, 30, 35, 45, 50, 55, 60, 65, 70, 72, 76, 78, 80, 82, 84, 86, 88, 90, 92, 94, 96, 98, 100];
    private static readonly int[] TargetCounts = [1, 1, 2, 3, 4, 5];
    private static readonly int[] RedGauges = [0, 40, 50, 60, 90, 100];
    private static readonly int[] BlueGauges = [0, 40, 50, 90, 100];
    private static readonly double[] DeathsDesignLefts = [0, 3, 6, 10, 15, 24, 29, 35, 45, 60];
    private static readonly double[] SoulSliceCharges = [0, 0.5, 0.95, 1, 1.5, 1.9, 2];
    private static readonly ReaverState[] ReaverStates = [ReaverState.None, ReaverState.None, ReaverState.None, ReaverState.SoulReaver, ReaverState.Executioner];

    public static IReadOnlyList<ScenarioDefinition> Build(int patternCount, int seed)
    {
        var scenarios = new List<ScenarioDefinition>(patternCount);
        var random = new Random(seed);

        for (var i = 0; i < patternCount; ++i)
        {
            var gcd = Gcds[i % Gcds.Length];
            var opener = Openers[(i / Gcds.Length) % Openers.Length];
            var potion = Potions[(i / (Gcds.Length * Openers.Length)) % Potions.Length];
            var level = Levels[(i / (Gcds.Length * Openers.Length * Potions.Length)) % Levels.Length];
            var mode = Modes[(i + random.Next(Modes.Length)) % Modes.Length];
            var targetCount = TargetCounts[(i + random.Next(TargetCounts.Length)) % TargetCounts.Length];
            var killTime = 120 + random.Next(0, 18) * 30;
            var redGauge = RedGauges[(i + random.Next(RedGauges.Length)) % RedGauges.Length];
            var blueGauge = BlueGauges[(i / 7 + random.Next(BlueGauges.Length)) % BlueGauges.Length];
            if (level < 80)
                blueGauge = 0;
            var ddLeft = DeathsDesignLefts[(i / 11 + random.Next(DeathsDesignLefts.Length)) % DeathsDesignLefts.Length];
            var soulSliceCharges = level < 60
                ? 0
                : Math.Min(level >= 78 ? 2 : 1, SoulSliceCharges[(i / 13 + random.Next(SoulSliceCharges.Length)) % SoulSliceCharges.Length]);
            var initialReaver = level >= 70 ? ReaverStates[(i / 17 + random.Next(ReaverStates.Length)) % ReaverStates.Length] : ReaverState.None;
            if (level < 96 && initialReaver == ReaverState.Executioner)
                initialReaver = ReaverState.SoulReaver;
            var events = BuildEvents(random, killTime, targetCount, mode, i);
            var cleanLevel100Full = level >= 100
                && mode == RotationMode.Full
                && killTime >= 240
                && blueGauge >= 50
                && initialReaver == ReaverState.None
                && !events.Any(e => (e.Type is ScenarioEventType.TargetLost or ScenarioEventType.ModeSwitch) && e.Start <= 140);

            scenarios.Add(new ScenarioDefinition
            {
                Name = $"real_{i:00000}_g{gcd:0.00}_{opener}_{potion}_lv{level}_kt{killTime}_t{targetCount}",
                Category = ScenarioCategory.RealHarness,
                Seed = StableSeed(seed, i),
                KillTime = killTime,
                Gcd = gcd,
                Opener = opener,
                Potion = potion,
                InitialMode = mode,
                TrueNorth = TrueNorthModes[(i / 19 + random.Next(TrueNorthModes.Length)) % TrueNorthModes.Length],
                PositionalCorrect = i % 3 != 0,
                Level = level,
                DeathsDesignLeft = ddLeft,
                RedGauge = redGauge,
                BlueGauge = blueGauge,
                SoulSliceCharges = soulSliceCharges,
                AoeTargets = targetCount,
                ConeTargets = targetCount,
                InitialReaver = initialReaver,
                EvenBurstOneRefreshEnough = cleanLevel100Full && ddLeft >= 20 && i % 3 == 0,
                EvenBurstTwoRefreshNeeded = cleanLevel100Full && ddLeft <= 6 && i % 3 == 1,
                ArcaneCircleFourSecondDisplay = level >= 72 && i % 23 == 0,
                GluttonyReadyAtStart = level >= 76 && i % 29 == 0,
                InitialGluttonyReadyIn = level >= 76 && i % 29 != 0 ? random.NextDouble() * 60 : 0,
                ForceBlueGaugeBuild = level >= 80 && i % 31 == 0,
                FinalTwoGcdKill = i % 37 == 0,
                ExpectDoubleEnshroud = cleanLevel100Full,
                ExpectTwoCommunio = cleanLevel100Full,
                ExpectPerfectio = cleanLevel100Full,
                ExpectLemure = cleanLevel100Full,
                ExpectSacrificium = cleanLevel100Full,
                ExpectSecondEnshroudBlueGauge = cleanLevel100Full,
                Events = events
            });
        }

        return scenarios;
    }

    private static IReadOnlyList<ScenarioEvent> BuildEvents(Random random, double killTime, int initialTargets, RotationMode initialMode, int index)
    {
        var events = new List<ScenarioEvent>();
        AddTargetCountEvents(events, random, killTime, initialTargets, index);
        AddTargetLossEvents(events, random, killTime, index);
        AddMeleeLossEvents(events, random, killTime, index);
        AddModeSwitchEvents(events, random, killTime, initialMode, index);
        AddTargetNullEvents(events, random, killTime, index);

        return events
            .Where(e => e.Start >= 0 && e.End > e.Start && e.Start < killTime)
            .OrderBy(e => e.Start)
            .ThenBy(e => e.Type)
            .ToList();
    }

    private static void AddTargetCountEvents(List<ScenarioEvent> events, Random random, double killTime, int initialTargets, int index)
    {
        var changes = index % 5 == 0 ? 3 : index % 3 == 0 ? 2 : random.Next(0, 2);
        var current = initialTargets;
        for (var n = 0; n < changes; ++n)
        {
            var start = RandomTime(random, killTime, 20, 90);
            var end = Math.Min(killTime, start + 20 + random.Next(0, 80));
            var next = TargetCounts[random.Next(TargetCounts.Length)];
            if (next == current)
                next = next >= 3 ? 1 : 3;
            current = next;
            events.Add(Event(ScenarioEventType.AoeTargets, start, end, current));
        }
    }

    private static void AddTargetLossEvents(List<ScenarioEvent> events, Random random, double killTime, int index)
    {
        if (index % 4 != 0)
            return;

        var losses = index % 16 == 0 ? 2 : 1;
        for (var n = 0; n < losses; ++n)
        {
            var start = RandomTime(random, killTime, 5, 40);
            var duration = index % 8 == 0 ? random.Next(2, 7) : random.Next(5, 18);
            if (index % 8 == 0)
                events.Add(Event(ScenarioEventType.TargetKilled, start, Math.Min(killTime, start + 0.001)));
            events.Add(Event(ScenarioEventType.TargetLost, start, Math.Min(killTime, start + duration)));
        }
    }

    private static void AddMeleeLossEvents(List<ScenarioEvent> events, Random random, double killTime, int index)
    {
        if (index % 5 != 1)
            return;

        var start = RandomTime(random, killTime, 8, 80);
        events.Add(Event(ScenarioEventType.MeleeUnavailable, start, Math.Min(killTime, start + random.Next(3, 15))));
    }

    private static void AddModeSwitchEvents(List<ScenarioEvent> events, Random random, double killTime, RotationMode initialMode, int index)
    {
        if (index % 6 != 2)
            return;

        var start = RandomTime(random, killTime, 30, 120);
        var end = Math.Min(killTime, start + random.Next(20, 140));
        events.Add(Mode(start, end, initialMode == RotationMode.Full ? RotationMode.Basic : RotationMode.Full));
    }

    private static void AddTargetNullEvents(List<ScenarioEvent> events, Random random, double killTime, int index)
    {
        if (index % 7 == 3)
        {
            var start = RandomTime(random, killTime, 10, 90);
            events.Add(Event(ScenarioEventType.PrimaryTargetNull, start, Math.Min(killTime, start + random.Next(2, 8))));
        }
        if (index % 11 == 4)
        {
            var start = RandomTime(random, killTime, 10, 90);
            events.Add(Event(ScenarioEventType.EmptyPriorityTargets, start, Math.Min(killTime, start + random.Next(2, 8))));
        }
        if (index % 13 == 5)
        {
            var start = RandomTime(random, killTime, 10, 120);
            events.Add(Event(ScenarioEventType.NullBestConeTarget, start, Math.Min(killTime, start + random.Next(5, 25))));
        }
        if (index % 17 == 6)
        {
            var start = RandomTime(random, killTime, 10, 120);
            events.Add(Event(ScenarioEventType.NullBestRangedAoeTarget, start, Math.Min(killTime, start + random.Next(5, 25))));
        }
    }

    private static double RandomTime(Random random, double killTime, double earliest, double latestPadding)
    {
        var latest = Math.Max(earliest, killTime - latestPadding);
        if (latest <= earliest)
            return earliest;

        return Math.Round(earliest + random.NextDouble() * (latest - earliest), 2);
    }

    private static ScenarioEvent Event(ScenarioEventType type, double start, double end, double value = 0)
        => new() { Type = type, Start = start, End = end, Value = value };

    private static ScenarioEvent Mode(double start, double end, RotationMode mode)
        => new() { Type = ScenarioEventType.ModeSwitch, Start = start, End = end, Mode = mode };

    private static int StableSeed(int seed, int index)
    {
        unchecked
        {
            var hash = 17;
            hash = hash * 31 + seed;
            hash = hash * 31 + index;
            return Math.Abs(hash);
        }
    }
}
