namespace BossMod.Autorotation.Engine.Jobs;

// Black Mage (level 100) for the rotation engine: data only, no BossMod dependency (also compiled by tools/blm_engine_eval).
// Mechanics follow tools/xan_timeline_harness/BlmCombatState.cs (the user's replay-checked model):
// - damage = potency x element multiplier of the element the spell is cast under x Enochian (1.27 while an element is up);
// - Astral Fire doubles fire MP costs unless an Umbral Heart absorbs it; ice spells landing in Umbral Ice refill MP;
// - fire spells cast in Umbral Ice III and ice spells cast in Astral Fire III take half the cast time;
// - the element never expires on its own (7.x), Polyglot comes every 30 s while an element is up.
// Element stance, MP and hearts are Flat gauges; a state-dependent cost or cast time is a separate skill variant with its
// own conditions (only one variant of an action is legal at a time). ActionFor maps every variant to its game action.
public static class BlmDefinition
{
    // gauges
    public const string MP = "MP";
    public const string AstralFire = "AstralFire";
    public const string UmbralIce = "UmbralIce";
    public const string Hearts = "Hearts";
    public const string Polyglot = "Polyglot";
    public const string AstralSoul = "AstralSoul";
    public const string Paradox = "Paradox";

    // statuses
    public const string Firestarter = "Firestarter";
    public const string Thunderhead = "Thunderhead";
    public const string Thunder = "Thunder";          // the DoT on the primary target
    public const string LeyLines = "LeyLines";        // standing in Circle of Power
    public const string Swiftcast = "Swiftcast";
    public const string Triplecast = "Triplecast";
    public const string PolyglotTimer = "PolyglotTimer";

    // cooldown groups
    public const string TransposeCD = "TransposeCD";
    public const string ManafontCD = "ManafontCD";
    public const string AmplifierCD = "AmplifierCD";
    public const string LeyLinesCD = "LeyLinesCD";
    public const string TriplecastCD = "TriplecastCD";
    public const string SwiftcastCD = "SwiftcastCD";

    public const float Enochian = 1.27f;

    // game action ids (BossMod.BLM.AID)
    public const uint AidFire3 = 152, AidFire4 = 3577, AidDespair = 16505, AidFlareStar = 36989, AidFlare = 162, AidHighFire2 = 25794,
        AidParadox = 25797, AidBlizzard3 = 154, AidBlizzard4 = 3576, AidFreeze = 159, AidHighBlizzard2 = 25795, AidHighThunder = 36986,
        AidHighThunder2 = 36987, AidXenoglossy = 16507, AidFoul = 7422, AidUmbralSoul = 16506, AidTranspose = 149, AidManafont = 158,
        AidAmplifier = 25796, AidLeyLines = 3573, AidTriplecast = 7421, AidSwiftcast = 7561;

    public static JobDefinition Build(float gcd = 2.5f)
    {
        var k = gcd / 2.5f; // spell speed scales cast times like the GCD
        var b = new JobBuilder("BLM", gcd)
            .Gauge(MP, 10000, flat: true).Gauge(AstralFire, 3, flat: true).Gauge(UmbralIce, 3, flat: true).Gauge(Hearts, 3, flat: true)
            .Gauge(Polyglot, 3).Gauge(AstralSoul, 6).Gauge(Paradox, 1)
            // definition order matters for instant casts: Swiftcast is used before a Triplecast stack
            .Status(Swiftcast, 10, castTimeMultiplier: 0, consumedByCast: true)
            .Status(Triplecast, 15, maxStacks: 3, castTimeMultiplier: 0, consumedByCast: true)
            .Status(LeyLines, 20, gcdRecastMultiplier: 0.85f, castTimeMultiplier: 0.85f)
            .Status(Firestarter, 30)
            .Status(Thunderhead, 30)
            .Status(Thunder, 30)
            .Status(PolyglotTimer, 30)
            .Periodic(PolyglotTimer, Polyglot, 1)
            .Cooldown(TransposeCD, 5).Cooldown(ManafontCD, 100).Cooldown(AmplifierCD, 120).Cooldown(LeyLinesCD, 120, 2)
            .Cooldown(TriplecastCD, 60, 2).Cooldown(SwiftcastCD, 40)
            // the element / MP / hearts economy spans a whole fire + ice cycle: valued by the cycle model (its rate is the filler rate)
            .CycleGauge(MP, 200).CycleGauge(AstralFire, 1).CycleGauge(UmbralIce, 1).CycleGauge(Hearts, 1).CycleGauge(Paradox, 1).CycleGauge(AstralSoul, 1)
            .CycleStatus(Firestarter).CycleCooldown(TransposeCD);

        // ---- fire phase ----
        Fire(b.Gcd("Fire4", 300, AidFire4).Cast(2.0f * k), 300)
            .RequiresGauge(AstralFire, 3).RequiresGauge(Hearts, 1).SpendGauge(MP, 800).GainGauge(Hearts, -1).GainGauge(AstralSoul, 1);
        Fire(b.Gcd("Fire4NoHeart", 300, AidFire4).Cast(2.0f * k), 300)
            .RequiresGauge(AstralFire, 3).RequiresGaugeAtMost(Hearts, 0).SpendGauge(MP, 1600).GainGauge(AstralSoul, 1);
        Fire(b.Gcd("Despair", 350, AidDespair), 350)
            .RequiresGauge(AstralFire, 1).RequiresGauge(MP, 800).SetGauge(MP, 0).SetGauge(AstralFire, 3);
        Fire(b.Gcd("FlareStar", 500, AidFlareStar).Cast(2.0f * k).AoeFalloff(500 * 0.35f), 500)
            .RequiresGauge(AstralFire, 1).SpendGauge(AstralSoul, 6);
        Fire(b.Gcd("Flare", 240, AidFlare).Cast(2.0f * k).AoeFalloff(240 * 0.7f), 240)
            .RequiresGauge(AstralFire, 1).RequiresGauge(MP, 800)
            .IfGaugeAtLeast(Hearts, 1).ScaleGauge(MP, 1 / 3f).IfGaugeAtMost(Hearts, 0).SetGauge(MP, 0)
            .SetGauge(Hearts, 0).SetGauge(AstralFire, 3).GainGauge(AstralSoul, 3);
        Plain(b.Gcd("Paradox", 540, AidParadox), 540)
            .RequiresGauge(AstralFire, 1).SpendGauge(Paradox, 1).SpendGauge(MP, 1600).ApplyStatus(Firestarter, 30);
        Plain(b.Gcd("ParadoxIce", 540, AidParadox), 540)
            .RequiresGauge(UmbralIce, 1).SpendGauge(Paradox, 1);

        // Fire III: every variant enters Astral Fire III
        EnterFire(Fire(b.Gcd("Fire3Proc", 290, AidFire3), 290).RequiresStatus(Firestarter).RemoveStatus(Firestarter));
        EnterFire(Fire(b.Gcd("Fire3", 290, AidFire3).Cast(3.5f * 0.5f * k), 290).RequiresGauge(UmbralIce, 3).ForbidStatus(Firestarter));
        EnterFire(Fire(b.Gcd("Fire3LowIce", 290, AidFire3).Cast(3.5f * k), 290).RequiresGauge(UmbralIce, 1).RequiresGaugeAtMost(UmbralIce, 2).ForbidStatus(Firestarter));
        EnterFire(Fire(b.Gcd("Fire3Cold", 290, AidFire3).Cast(3.5f * k), 290).RequiresGaugeAtMost(AstralFire, 0).RequiresGaugeAtMost(UmbralIce, 0).ForbidStatus(Firestarter).SpendGauge(MP, 2000));
        EnterFire(Fire(b.Gcd("HighFire2", 100, AidHighFire2).Cast(3.0f * 0.5f * k).AoeFalloff(100), 100).RequiresGauge(UmbralIce, 3));

        // ---- ice phase ----
        EnterIce(Ice(b.Gcd("Blizzard3", 290, AidBlizzard3).Cast(3.5f * 0.5f * k), 290).RequiresGauge(AstralFire, 3));
        EnterIce(Ice(b.Gcd("Blizzard3LowFire", 290, AidBlizzard3).Cast(3.5f * k), 290).RequiresGauge(AstralFire, 1).RequiresGaugeAtMost(AstralFire, 2));
        EnterIce(Ice(b.Gcd("Blizzard3Cold", 290, AidBlizzard3).Cast(3.5f * k), 290).RequiresGaugeAtMost(AstralFire, 0).RequiresGaugeAtMost(UmbralIce, 0).SpendGauge(MP, 800));
        EnterIce(Ice(b.Gcd("HighBlizzard2", 100, AidHighBlizzard2).Cast(3.0f * 0.5f * k).AoeFalloff(100), 100).RequiresGauge(AstralFire, 3));
        EnterIce(Ice(b.Gcd("HighBlizzard2Cold", 100, AidHighBlizzard2).Cast(3.0f * k).AoeFalloff(100), 100).RequiresGaugeAtMost(AstralFire, 2).RequiresGaugeAtMost(UmbralIce, 0));
        IceMp(Ice(b.Gcd("Blizzard4", 300, AidBlizzard4).Cast(2.0f * k), 300).RequiresGauge(UmbralIce, 1).SetGauge(Hearts, 3));
        IceMp(Ice(b.Gcd("Freeze", 120, AidFreeze).Cast(2.0f * k).AoeFalloff(120), 120).RequiresGauge(UmbralIce, 1).SetGauge(Hearts, 3));
        IceMp(b.Gcd("UmbralSoul", 0, AidUmbralSoul).NoTarget().RequiresGauge(UmbralIce, 1).GainGauge(UmbralIce, 1).GainGauge(Hearts, 1));

        // ---- element-neutral GCDs ----
        Plain(b.Gcd("HighThunder", 150, AidHighThunder), 150)
            .RequiresStatus(Thunderhead).RemoveStatus(Thunderhead).Dot(Thunder, 30, 60 * Enochian);
        Plain(b.Gcd("HighThunder2", 100, AidHighThunder2).AoeFalloff(100), 100)
            .RequiresStatus(Thunderhead).RemoveStatus(Thunderhead).Dot(Thunder, 24, 40 * Enochian, aoe: true);
        Plain(b.Gcd("Xenoglossy", 890, AidXenoglossy), 890).SpendGauge(Polyglot, 1);
        Plain(b.Gcd("Foul", 600, AidFoul).AoeFalloff(600 * 0.75f), 600).SpendGauge(Polyglot, 1);

        // ---- abilities ----
        EnterElementSide(b.Ogcd("Transpose", 0, TransposeCD, AidTranspose).RequiresGauge(AstralFire, 1).ApplyStatus(Thunderhead, 30)
            .IfGaugeAtLeast(AstralFire, 3).SetGauge(Paradox, 1)
            .SetGauge(AstralFire, 0).SetGauge(UmbralIce, 1).SetGauge(AstralSoul, 0));
        EnterElementSide(b.Ogcd("TransposeIce", 0, TransposeCD, AidTranspose).RequiresGauge(UmbralIce, 1).ApplyStatus(Thunderhead, 30)
            .IfGaugeAtLeast(UmbralIce, 3).IfGaugeAtLeast(Hearts, 3).SetGauge(Paradox, 1)
            .SetGauge(UmbralIce, 0).SetGauge(AstralFire, 1));
        b.Ogcd("Manafont", 0, ManafontCD, AidManafont).NeedsUptime(0.01f).RequiresGauge(AstralFire, 1)
            .SetGauge(MP, 10000).SetGauge(AstralFire, 3).SetGauge(Hearts, 3).SetGauge(Paradox, 1).ApplyStatus(Thunderhead, 30);
        b.Ogcd("Amplifier", 0, AmplifierCD, AidAmplifier).RequiresGauge(AstralFire, 1).GainGauge(Polyglot, 1);
        b.Ogcd("AmplifierIce", 0, AmplifierCD, AidAmplifier).RequiresGauge(UmbralIce, 1).GainGauge(Polyglot, 1);
        b.Ogcd("LeyLines", 0, LeyLinesCD, AidLeyLines).NeedsUptime(12.5f).NeedsStanding(6.5f).ForbidStatus(LeyLines).ApplyStatus(LeyLines, 20);
        b.Ogcd("Triplecast", 0, TriplecastCD, AidTriplecast).ForbidStatus(Triplecast).ApplyStatus(Triplecast, 15, 3);
        b.Ogcd("Swiftcast", 0, SwiftcastCD, AidSwiftcast).ForbidStatus(Swiftcast).ApplyStatus(Swiftcast, 10);
        return b.Build();
    }

    // fire spell: element multiplier of the stance it is cast under (x Enochian while an element is up)
    private static JobBuilder.SkillBuilder Fire(JobBuilder.SkillBuilder s, float p) => s
        .PotencyIfGauge(AstralFire, 3, p * 1.8f * Enochian).PotencyIfGauge(AstralFire, 2, p * 1.6f * Enochian).PotencyIfGauge(AstralFire, 1, p * 1.4f * Enochian)
        .PotencyIfGauge(UmbralIce, 3, p * 0.7f * Enochian).PotencyIfGauge(UmbralIce, 2, p * 0.8f * Enochian).PotencyIfGauge(UmbralIce, 1, p * 0.9f * Enochian);

    private static JobBuilder.SkillBuilder Ice(JobBuilder.SkillBuilder s, float p) => s
        .PotencyIfGauge(AstralFire, 3, p * 0.7f * Enochian).PotencyIfGauge(AstralFire, 2, p * 0.8f * Enochian).PotencyIfGauge(AstralFire, 1, p * 0.9f * Enochian)
        .PotencyIfGauge(UmbralIce, 1, p * Enochian);

    // no element multiplier, only Enochian
    private static JobBuilder.SkillBuilder Plain(JobBuilder.SkillBuilder s, float p) => s
        .PotencyIfGauge(AstralFire, 1, p * Enochian).PotencyIfGauge(UmbralIce, 1, p * Enochian);

    // effects of entering Astral Fire III (conditions are checked on the state before the spell)
    private static JobBuilder.SkillBuilder EnterFire(JobBuilder.SkillBuilder s) => EnterElementSide(s.IfGaugeAtMost(AstralFire, 0).ApplyStatus(Thunderhead, 30)
        .IfGaugeAtLeast(UmbralIce, 3).IfGaugeAtLeast(Hearts, 3).SetGauge(Paradox, 1)
        .SetGauge(AstralFire, 3).SetGauge(UmbralIce, 0));

    // entering Umbral Ice III: Astral Soul is lost, MP refills
    private static JobBuilder.SkillBuilder EnterIce(JobBuilder.SkillBuilder s) => EnterElementSide(s.IfGaugeAtMost(UmbralIce, 0).ApplyStatus(Thunderhead, 30)
        .IfGaugeAtLeast(AstralFire, 3).SetGauge(Paradox, 1)
        .SetGauge(UmbralIce, 3).SetGauge(AstralFire, 0).SetGauge(AstralSoul, 0).SetGauge(MP, 10000));

    // entering an element from neutral starts the Polyglot timer (conditions see the stance before the spell)
    private static JobBuilder.SkillBuilder EnterElementSide(JobBuilder.SkillBuilder s) => s
        .IfGaugeAtMost(AstralFire, 0).IfGaugeAtMost(UmbralIce, 0).ApplyStatus(PolyglotTimer, 30);

    // an ice spell landing in Umbral Ice restores 2500 / 5000 / 10000 MP at UI1 / 2 / 3
    private static JobBuilder.SkillBuilder IceMp(JobBuilder.SkillBuilder s) => s
        .IfGaugeAtLeast(UmbralIce, 3).GainGauge(MP, 10000)
        .IfGaugeAtLeast(UmbralIce, 2).IfGaugeAtMost(UmbralIce, 2).GainGauge(MP, 5000)
        .IfGaugeAtLeast(UmbralIce, 1).IfGaugeAtMost(UmbralIce, 1).GainGauge(MP, 2500);

    // game action of a skill (variants share one action)
    public static uint ActionOf(SkillDef skill) => skill.ActionId;

    public static EngineWeights DefaultWeights() => EngineWeights.Parse(DefaultWeightsJson);

    // CMA-ES on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-BLM-v5.json), BudgetMs set for live play.
    // A 3-GCD horizon: with 4, a sixth of the live searches (casts make long lines) ran out of the budget before the last iteration.
    public const string DefaultWeightsJson = """
    {
      "OverCap": 1.2312877,
      "Combo": 0,
      "LambdaScale": 0,
      "TargetPull": 0,
      "SwitchMargin": 3.8592908,
      "FillerScale": 1.025866,
      "BurstBias": 2.453738,
      "StatusRemainder": 0,
      "CycleScale": 1.0244133,
      "CooldownLambdaScale": 0,
      "ForecastSelfBuffs": 0,
      "UnlockScale": 0.24033335,
      "StatusValue": { "Thunderhead": 79.50191, "Firestarter": 10 },
      "CooldownValue": { "LeyLinesCD": 1074.4264, "TriplecastCD": 163.94995, "SwiftcastCD": 873.7974 },
      "GaugeValue": {},
      "HorizonGcds": 3,
      "BudgetMs": 0.8
    }
    """;
}
