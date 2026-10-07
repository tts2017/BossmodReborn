namespace NinRegression;

public static class NinScenarioGenerator
{
    public static IReadOnlyList<NinScenario> BuildAll()
    {
        var scenarios = new List<NinScenario>();
        scenarios.AddRange(BuildBasicSingleTarget());
        scenarios.AddRange(BuildOpeners());
        scenarios.AddRange(BuildEvenBurst());
        scenarios.AddRange(BuildKunaiDrift());
        scenarios.AddRange(BuildMudra());
        scenarios.AddRange(BuildKassatsu());
        scenarios.AddRange(BuildTcjMeisui());
        scenarios.AddRange(BuildAoe());
        scenarios.AddRange(BuildDowntime());
        scenarios.AddRange(BuildBasicComboOnly());
        scenarios.AddRange(BuildFuzz(1000, 900000));
        return scenarios;
    }

    public static IReadOnlyList<NinScenario> Select(string selector)
    {
        if (selector.Equals("all", StringComparison.OrdinalIgnoreCase))
            return BuildAll();

        var all = BuildAll();
        return all
            .Where(s => s.Name.Contains(selector, StringComparison.OrdinalIgnoreCase)
                || s.Category.ToString().Contains(selector, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static IReadOnlyList<NinScenario> BuildFuzz(int count, int seedBase = 100000)
    {
        var result = new List<NinScenario>(count);
        for (var i = 0; i < count; ++i)
        {
            var seed = seedBase + i;
            var rng = new Random(seed);
            var duration = rng.NextDouble() < 0.15 ? 720 : 360;
            var events = new List<NinScenarioEvent>();
            for (var e = 0; e < rng.Next(4, 12); ++e)
            {
                var time = Math.Round(rng.NextDouble() * (duration - 5), 1);
                switch (rng.Next(2))
                {
                    case 0:
                        events.Add(new NinScenarioEvent { Type = NinScenarioEventType.TargetLost, Time = time });
                        events.Add(new NinScenarioEvent { Type = NinScenarioEventType.TargetReturned, Time = Math.Min(duration - 1, time + rng.Next(2, 45)) });
                        break;
                    case 1:
                        events.Add(new NinScenarioEvent { Type = NinScenarioEventType.RangedAoeTargets, Time = time, Value = rng.Next(1, 5) });
                        events.Add(new NinScenarioEvent { Type = NinScenarioEventType.AoeTargets, Time = time, Value = rng.Next(1, 5) });
                        break;
                }
            }

            var gcdLength = Pick(rng, [2.12, 2.14, 2.48]);
            var level = Pick(rng, [35, 45, 50, 60, 66, 70, 76, 80, 90, 92, 96, 100]);
            var burstStyle = rng.NextDouble() < 0.25 ? BurstStyle.UltimateZeroSecond : BurstStyle.Normal;
            var potionStrategy = rng.NextDouble() < 0.5 ? PotionStrategy.None : PotionStrategy.EvenBurst;
            var rotationStrategy = rng.NextDouble() < 0.05 ? RotationStrategy.BasicComboOnly : RotationStrategy.Normal;
            var numAoeTargets = rng.Next(1, 5);
            var numRangedAoeTargets = rng.Next(1, 5);
            var initialNinki = QuantizeNinki(rng.Next(0, 101));
            if (level < 62)
                initialNinki = 0;

            result.Add(new NinScenario
            {
                Name = $"Fuzz_{seed}",
                Category = NinScenarioCategory.Fuzz,
                Seed = seed,
                Duration = duration,
                GcdLength = gcdLength,
                Level = level,
                BurstStyle = burstStyle,
                PotionStrategy = potionStrategy,
                RotationStrategy = rotationStrategy,
                NumAoeTargets = numAoeTargets,
                NumRangedAoeTargets = numRangedAoeTargets,
                InitialNinki = initialNinki,
                InitialMudraCharges = rng.Next(0, 3),
                InitialMugReadyIn = rng.Next(0, 120),
                InitialKunaiReadyIn = rng.Next(0, 60),
                InitialKassatsuReadyIn = rng.Next(0, 60),
                InitialTenChiJinReadyIn = rng.Next(0, 120),
                InitialMeisuiReadyIn = rng.Next(0, 120),
                Events = events.OrderBy(e => e.Time).ToList()
            });
        }

        return result;
    }

    private static IEnumerable<NinScenario> BuildBasicSingleTarget()
    {
        var levels = new[] { 35, 45, 50, 60, 66, 70, 76, 80, 90, 92, 96, 100 };
        var gcds = new[] { 2.12, 2.14, 2.48 };
        foreach (var level in levels)
        foreach (var gcd in gcds)
        foreach (var duration in new[] { 360.0, 720.0 })
        foreach (var potion in new[] { PotionStrategy.None, PotionStrategy.EvenBurst })
        foreach (var burst in new[] { BurstStyle.Normal, BurstStyle.UltimateZeroSecond })
            yield return Base($"Basic_ST_L{level}_G{gcd:0.00}_{duration:0}_{potion}_{burst}", NinScenarioCategory.BasicSingleTarget, level, gcd, duration) with
            {
                PotionStrategy = potion,
                BurstStyle = burst
            };
    }

    private static IEnumerable<NinScenario> BuildOpeners()
    {
        yield return Base("Opener_Normal_CountdownSuiton", NinScenarioCategory.Opener, 100, 2.12, 360) with { CountdownSuiton = true, Hidden = true };
        yield return Base("Opener_Normal_NoCountdown", NinScenarioCategory.Opener, 100, 2.12, 360) with { CountdownSuiton = false, Hidden = true };
        yield return Base("Opener_UltimateZeroSecond_0s", NinScenarioCategory.Opener, 100, 2.12, 360) with { BurstStyle = BurstStyle.UltimateZeroSecond, Hidden = true };
        yield return Base("Opener_Kassatsu_Dokumori_Kunai_3ogcd", NinScenarioCategory.Opener, 100, 2.12, 360) with { BurstStyle = BurstStyle.UltimateZeroSecond };
        yield return Base("Opener_TargetDelayed_0_5s", NinScenarioCategory.Opener, 100, 2.12, 360) with
        {
            TargetExists = false,
            Targetable = false,
            Events =
            [
                new NinScenarioEvent { Type = NinScenarioEventType.TargetReturned, Time = 0.5 }
            ]
        };
        yield return Base("Opener_HiddenOff", NinScenarioCategory.Opener, 100, 2.12, 360) with { Hidden = false };
    }

    private static IEnumerable<NinScenario> BuildEvenBurst()
    {
        foreach (var ninki in new[] { 0, 45, 50, 80, 90, 100 })
        {
            yield return Base($"EvenBurst_Normal_Ninki{ninki}", NinScenarioCategory.EvenBurst, 100, 2.12, 360) with
            {
                InitialNinki = ninki,
                InitialTargetMugLeft = 20,
                InitialTargetTrickLeft = 0,
                InitialKunaiReadyIn = 0,
                InitialKassatsuReadyIn = 0,
                InitialTenChiJinReadyIn = 0,
                InitialMeisuiReadyIn = 0,
                InitialShadowWalker = 20
            };
            yield return Base($"EvenBurst_UltimateZeroSecond_Ninki{ninki}", NinScenarioCategory.EvenBurst, 100, 2.12, 360) with
            {
                BurstStyle = BurstStyle.UltimateZeroSecond,
                InitialNinki = ninki,
                InitialTargetMugLeft = 20,
                InitialKunaiReadyIn = 0,
                InitialKassatsuReadyIn = 0,
                InitialTenChiJinReadyIn = 0,
                InitialMeisuiReadyIn = 0,
                InitialShadowWalker = 20
            };
        }
    }

    private static IEnumerable<NinScenario> BuildKunaiDrift()
    {
        yield return Base("KunaiDrift_OddAllowed_Mug60", NinScenarioCategory.KunaiDrift, 100, 2.12, 360) with { InitialMugReadyIn = 60, InitialKunaiReadyIn = 0, InitialShadowWalker = 20 };
        yield return Base("KunaiDrift_OddSkip_Mug54", NinScenarioCategory.KunaiDrift, 100, 2.12, 360) with { InitialMugReadyIn = 54, InitialKunaiReadyIn = 0, InitialShadowWalker = 20 };
        yield return Base("KunaiDrift_Alliance_5sLate", NinScenarioCategory.KunaiDrift, 100, 2.12, 360) with { InitialMugReadyIn = 55, InitialKunaiReadyIn = 0, InitialShadowWalker = 20 };
        yield return Base("KunaiDrift_Alliance_10sLate", NinScenarioCategory.KunaiDrift, 100, 2.12, 360) with { InitialMugReadyIn = 50, InitialKunaiReadyIn = 0, InitialShadowWalker = 20 };
        yield return Base("KunaiDrift_TargetLost_Resync", NinScenarioCategory.KunaiDrift, 100, 2.12, 360) with
        {
            InitialMugReadyIn = 58,
            InitialKunaiReadyIn = 0,
            InitialShadowWalker = 20,
            Events =
            [
                new NinScenarioEvent { Type = NinScenarioEventType.TargetLost, Time = 5 },
                new NinScenarioEvent { Type = NinScenarioEventType.TargetReturned, Time = 15 }
            ]
        };
    }

    private static IEnumerable<NinScenario> BuildMudra()
    {
        foreach (var charges in new[] { 0, 1, 2 })
            yield return Base($"Mudra_Charges{charges}", NinScenarioCategory.Mudra, 100, 2.12, 360) with { InitialMudraCharges = charges };
        foreach (var hold in new[] { 1, 2, 3, 4, 5 })
            yield return Base($"Mudra_TwoStackHold{hold}s", NinScenarioCategory.Mudra, 100, 2.12, 360) with { InitialMudraCharges = 2, InitialMugReadyIn = 20 - hold };
        foreach (var burst in new[] { 3, 5, 8, 12, 20 })
            yield return Base($"Mudra_BurstIn{burst}s", NinScenarioCategory.Mudra, 100, 2.12, 360) with { InitialMudraCharges = 2, InitialMugReadyIn = burst };
        foreach (var pending in new[] { PendingNinjutsu.Raiton, PendingNinjutsu.Suiton, PendingNinjutsu.Huton, PendingNinjutsu.HyoshoRanryu, PendingNinjutsu.GokaMekkyaku })
            yield return Base($"Mudra_Pending_{pending}", NinScenarioCategory.Mudra, 100, 2.12, 360) with { InitialPendingNinjutsu = pending, InitialMudraCharges = 1 };
        yield return Base("Mudra_AoeSwitchDuringMudra", NinScenarioCategory.Mudra, 100, 2.12, 360) with
        {
            Events =
            [
                new NinScenarioEvent { Type = NinScenarioEventType.RangedAoeTargets, Time = 4, Value = 3 },
                new NinScenarioEvent { Type = NinScenarioEventType.RangedAoeTargets, Time = 10, Value = 1 }
            ]
        };
        yield return Base("Mudra_TargetLostDuringMudra", NinScenarioCategory.Mudra, 100, 2.12, 360) with
        {
            Events =
            [
                new NinScenarioEvent { Type = NinScenarioEventType.TargetLost, Time = 3 },
                new NinScenarioEvent { Type = NinScenarioEventType.TargetReturned, Time = 10 }
            ]
        };
        yield return Base("Mudra_ManualRaiton", NinScenarioCategory.Mudra, 100, 2.12, 360) with
        {
            Events = [new NinScenarioEvent { Type = NinScenarioEventType.ManualRaiton, Time = 4 }]
        };
    }

    private static IEnumerable<NinScenario> BuildKassatsu()
    {
        yield return Base("Kassatsu_Single_Hyosho", NinScenarioCategory.Kassatsu, 100, 2.12, 360) with { InitialKassatsuReadyIn = 0, InitialTargetMugLeft = 20, InitialShadowWalker = 20 };
        yield return Base("Kassatsu_Aoe_Goka_2Targets", NinScenarioCategory.Kassatsu, 100, 2.12, 360) with { NumRangedAoeTargets = 2, InitialKassatsuReadyIn = 0, InitialTargetMugLeft = 20, InitialShadowWalker = 20 };
        yield return Base("Kassatsu_Aoe_Goka_3Targets", NinScenarioCategory.Kassatsu, 100, 2.12, 360) with { NumRangedAoeTargets = 3, InitialKassatsuReadyIn = 0, InitialTargetMugLeft = 20, InitialShadowWalker = 20 };
        yield return Base("Kassatsu_RaijuPresent", NinScenarioCategory.Kassatsu, 100, 2.12, 360) with { InitialRaiju = 1, InitialKassatsuReadyIn = 0, InitialTargetMugLeft = 20 };
        yield return Base("Kassatsu_PhantomReady", NinScenarioCategory.Kassatsu, 100, 2.12, 360) with { InitialPhantomKamaitachi = 45, InitialKassatsuReadyIn = 0, InitialTargetMugLeft = 20 };
        yield return Base("Kassatsu_TCJReady", NinScenarioCategory.Kassatsu, 100, 2.12, 360) with { InitialTenChiJinReadyIn = 0, InitialKassatsuReadyIn = 0, InitialTargetMugLeft = 20 };
        yield return Base("Kassatsu_TargetLostAfter", NinScenarioCategory.Kassatsu, 100, 2.12, 360) with
        {
            InitialKassatsuReadyIn = 0,
            InitialTargetMugLeft = 20,
            Events =
            [
                new NinScenarioEvent { Type = NinScenarioEventType.TargetLost, Time = 1.0 },
                new NinScenarioEvent { Type = NinScenarioEventType.TargetReturned, Time = 8.0 }
            ]
        };
    }

    private static IEnumerable<NinScenario> BuildTcjMeisui()
    {
        yield return Base("TCJMeisui_EvenOnly", NinScenarioCategory.TCJMeisui, 100, 2.12, 360) with { InitialTargetMugLeft = 20, InitialTenChiJinReadyIn = 0, InitialMeisuiReadyIn = 0, InitialMudraCharges = 0 };
        yield return Base("TCJMeisui_OddKunaiForbidden", NinScenarioCategory.TCJMeisui, 100, 2.12, 360) with { InitialTargetMugLeft = 0, InitialTargetTrickLeft = 15, InitialTenChiJinReadyIn = 0, InitialMeisuiReadyIn = 0, InitialShadowWalker = 20 };
        yield return Base("TCJMeisui_NormalForbidden", NinScenarioCategory.TCJMeisui, 100, 2.12, 360) with { InitialMugReadyIn = 70, InitialKunaiReadyIn = 30, InitialTenChiJinReadyIn = 0, InitialMeisuiReadyIn = 0, InitialShadowWalker = 20 };
        yield return Base("TCJMeisui_TCJReadySoonBlocksMeisui", NinScenarioCategory.TCJMeisui, 100, 2.12, 360) with { InitialTargetMugLeft = 20, InitialTenChiJinReadyIn = 8, InitialMeisuiReadyIn = 0, InitialShadowWalker = 20 };
        yield return Base("TCJMeisui_AoeProgression", NinScenarioCategory.TCJMeisui, 100, 2.12, 360) with { InitialTargetMugLeft = 20, InitialTenChiJinReadyIn = 0, InitialMudraCharges = 0, NumRangedAoeTargets = 3 };
    }

    private static IEnumerable<NinScenario> BuildAoe()
    {
        foreach (var targets in new[] { 1, 2, 3, 4 })
            yield return Base($"Aoe_{targets}Targets", NinScenarioCategory.Aoe, 100, 2.12, 360) with { NumAoeTargets = targets, NumRangedAoeTargets = targets };
        yield return Base("Aoe_1to3", NinScenarioCategory.Aoe, 100, 2.12, 360) with { Events = [new NinScenarioEvent { Type = NinScenarioEventType.RangedAoeTargets, Time = 60, Value = 3 }, new NinScenarioEvent { Type = NinScenarioEventType.AoeTargets, Time = 60, Value = 3 }] };
        yield return Base("Aoe_3to1", NinScenarioCategory.Aoe, 100, 2.12, 360) with { NumAoeTargets = 3, NumRangedAoeTargets = 3, Events = [new NinScenarioEvent { Type = NinScenarioEventType.RangedAoeTargets, Time = 60, Value = 1 }, new NinScenarioEvent { Type = NinScenarioEventType.AoeTargets, Time = 60, Value = 1 }] };
        yield return Base("Aoe_BestRangedNull", NinScenarioCategory.Aoe, 100, 2.12, 360) with { NumRangedAoeTargets = 3, BestRangedAoeTargetNull = true };
        yield return Base("Aoe_PrimaryNullBestAoe", NinScenarioCategory.Aoe, 100, 2.12, 360) with { NumRangedAoeTargets = 3, PrimaryTargetNull = true, BestRangedAoeTargetNull = false };
    }

    private static IEnumerable<NinScenario> BuildDowntime()
    {
        foreach (var down in new[] { 10, 20, 45, 60 })
            yield return Base($"Downtime_{down}s", NinScenarioCategory.Downtime, 100, 2.12, 360) with
            {
                Events =
                [
                    new NinScenarioEvent { Type = NinScenarioEventType.TargetLost, Time = 60 },
                    new NinScenarioEvent { Type = NinScenarioEventType.TargetReturned, Time = 60 + down }
                ]
            };
        yield return Base("Downtime_MugBeforeDisappear", NinScenarioCategory.Downtime, 100, 2.12, 360) with { InitialMugReadyIn = 5, Events = [new NinScenarioEvent { Type = NinScenarioEventType.TargetLost, Time = 4 }, new NinScenarioEvent { Type = NinScenarioEventType.TargetReturned, Time = 20 }] };
        yield return Base("Downtime_KunaiBeforeDisappear", NinScenarioCategory.Downtime, 100, 2.12, 360) with { InitialKunaiReadyIn = 5, InitialMugReadyIn = 60, InitialShadowWalker = 20, Events = [new NinScenarioEvent { Type = NinScenarioEventType.TargetLost, Time = 4 }, new NinScenarioEvent { Type = NinScenarioEventType.TargetReturned, Time = 20 }] };
        yield return Base("Downtime_TCJDuringDisappear", NinScenarioCategory.Downtime, 100, 2.12, 360) with { InitialTargetMugLeft = 20, InitialTenChiJinReadyIn = 0, Events = [new NinScenarioEvent { Type = NinScenarioEventType.TargetLost, Time = 2 }, new NinScenarioEvent { Type = NinScenarioEventType.TargetReturned, Time = 12 }] };
    }

    private static IEnumerable<NinScenario> BuildBasicComboOnly()
    {
        yield return Base("BasicComboOnly_ST", NinScenarioCategory.BasicComboOnly, 100, 2.12, 360) with { RotationStrategy = RotationStrategy.BasicComboOnly };
        yield return Base("BasicComboOnly_AOE", NinScenarioCategory.BasicComboOnly, 100, 2.12, 360) with { RotationStrategy = RotationStrategy.BasicComboOnly, NumAoeTargets = 3, NumRangedAoeTargets = 3 };
        yield return Base("BasicComboOnly_TargetLost", NinScenarioCategory.BasicComboOnly, 100, 2.12, 360) with { RotationStrategy = RotationStrategy.BasicComboOnly, Events = [new NinScenarioEvent { Type = NinScenarioEventType.TargetLost, Time = 30 }, new NinScenarioEvent { Type = NinScenarioEventType.TargetReturned, Time = 45 }] };
        foreach (var level in new[] { 35, 50, 66, 76, 92, 100 })
            yield return Base($"BasicComboOnly_Level{level}", NinScenarioCategory.BasicComboOnly, level, 2.12, 360) with { RotationStrategy = RotationStrategy.BasicComboOnly };
    }

    private static NinScenario Base(string name, NinScenarioCategory category, int level, double gcd, double duration)
        => new()
        {
            Name = name,
            Category = category,
            Seed = Math.Abs(name.GetHashCode(StringComparison.Ordinal)),
            Level = level,
            GcdLength = gcd,
            Duration = duration,
            InitialMudraCharges = level >= 30 ? 2 : 0,
            InitialMugReadyIn = 0,
            InitialKunaiReadyIn = 0,
            InitialKassatsuReadyIn = level >= 50 ? 0 : 9999,
            InitialTenChiJinReadyIn = level >= 70 ? 0 : 9999,
            InitialMeisuiReadyIn = level >= 72 ? 0 : 9999
        };

    private static T Pick<T>(Random rng, IReadOnlyList<T> values)
        => values[rng.Next(values.Count)];

    private static int QuantizeNinki(int value)
        => Math.Clamp(value, 0, 100) / 5 * 5;
}
