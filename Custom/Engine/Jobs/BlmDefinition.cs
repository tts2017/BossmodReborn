namespace BossMod.Autorotation.Engine.Jobs;

// Black Mage for the rotation engine (any level; weights tuned at level 100 and per level band, see DefaultWeights): data only, no BossMod dependency (also compiled by tools/blm_engine_eval).
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
        AidAmplifier = 25796, AidLeyLines = 3573, AidTriplecast = 7421, AidSwiftcast = 7561,
        AidFire2 = 147, AidBlizzard2 = 25793, AidThunder1 = 144, AidThunder2 = 7447, AidThunder3 = 153, AidThunder4 = 7420;

    // Enochian's damage bonus while an element is up, by level (BlmCombatState.GlobalMultiplier)
    public static float EnochianAt(int level) => level >= 96 ? Enochian : level >= 86 ? 1.22f : level >= 78 ? 1.15f : level >= 70 ? 1.10f : level >= 56 ? 1.05f : 1f;

    // level: the player's (synced) level. Skills not unlocked there are left out; Umbral Hearts from 58, Paradox from 90, Astral Soul /
    // Flare Star and an instant Despair at 100, an instant Foul from 80; Polyglot holds 1 / 2 / 3 from 70 / 80 / 98. Fire II / Blizzard II
    // before High Fire II / High Blizzard II (82); Thunder / Thunder III and Thunder II / Thunder IV before High Thunder (II) (92).
    // Manafont 100 s from 84 (120 before), Swiftcast 40 s from 94 (60 before), Ley Lines' second charge from 96.
    public static JobDefinition Build(float gcd = 2.5f, int level = 100)
    {
        var k = gcd / 2.5f; // spell speed scales cast times like the GCD
        var e = EnochianAt(level);
        var hearts = level >= 58;
        var paradox = level >= 90;
        var astralSoul = level >= 100;
        var polyglot = level >= 98 ? 3 : level >= 80 ? 2 : 1;
        var b = new JobBuilder("BLM", gcd)
            .Gauge(MP, 10000, flat: true).Gauge(AstralFire, 3, flat: true).Gauge(UmbralIce, 3, flat: true).Gauge(Hearts, 3, flat: true)
            .Gauge(Polyglot, polyglot).Gauge(AstralSoul, 6).Gauge(Paradox, 1)
            // definition order matters for instant casts: Swiftcast is used before a Triplecast stack
            .Status(Swiftcast, 10, castTimeMultiplier: 0, consumedByCast: true)
            .Status(Triplecast, 15, maxStacks: 3, castTimeMultiplier: 0, consumedByCast: true)
            .Status(LeyLines, 20, gcdRecastMultiplier: 0.85f, castTimeMultiplier: 0.85f)
            .Status(Firestarter, 30)
            .Status(Thunderhead, 30)
            .Status(Thunder, 30)
            .Status(PolyglotTimer, 30);
        // Polyglot from 70 (Enochian's timer)
        if (level >= 70)
            b.Periodic(PolyglotTimer, Polyglot, 1);
        b.Cooldown(TransposeCD, 5).Cooldown(ManafontCD, level >= 84 ? 100 : 120).Cooldown(AmplifierCD, 120).Cooldown(LeyLinesCD, 120, level >= 96 ? 2 : 1)
            .Cooldown(TriplecastCD, 60, 2).Cooldown(SwiftcastCD, level >= 94 ? 40 : 60)
            // the element / MP / hearts economy spans a whole fire + ice cycle: valued by the cycle model (its rate is the filler rate)
            .CycleGauge(MP, 200).CycleGauge(AstralFire, 1).CycleGauge(UmbralIce, 1).CycleGauge(Hearts, 1).CycleGauge(Paradox, 1).CycleGauge(AstralSoul, 1)
            .CycleStatus(Firestarter).CycleCooldown(TransposeCD);

        // ---- fire phase ----
        if (level >= 60)
        {
            var fire4 = Fire(b.Gcd("Fire4", 300, AidFire4).Cast(2.0f * k), 300, e)
                .RequiresGauge(AstralFire, 3).RequiresGauge(Hearts, 1).SpendGauge(MP, 800).GainGauge(Hearts, -1);
            var fire4NoHeart = Fire(b.Gcd("Fire4NoHeart", 300, AidFire4).Cast(2.0f * k), 300, e)
                .RequiresGauge(AstralFire, 3).RequiresGaugeAtMost(Hearts, 0).SpendGauge(MP, 1600);
            if (astralSoul)
            {
                fire4.GainGauge(AstralSoul, 1);
                fire4NoHeart.GainGauge(AstralSoul, 1);
            }
        }
        if (level >= 72)
        {
            var despair = Fire(b.Gcd("Despair", 350, AidDespair), 350, e)
                .RequiresGauge(AstralFire, 1).RequiresGauge(MP, 800).SetGauge(MP, 0).SetGauge(AstralFire, 3);
            if (!astralSoul)
                despair.Cast(3.0f * k);
        }
        if (astralSoul)
            Fire(b.Gcd("FlareStar", 500, AidFlareStar).Shape(AoeShape.TargetCircle, 5, 0, 25).Cast(2.0f * k).AoeFalloff(500 * 0.35f), 500, e)
                .RequiresGauge(AstralFire, 1).SpendGauge(AstralSoul, 6);
        if (level >= 50)
        {
            var flare = Fire(b.Gcd("Flare", 240, AidFlare).Shape(AoeShape.TargetCircle, 5, 0, 25).Cast(2.0f * k).AoeFalloff(240 * 0.7f), 240, e)
                .RequiresGauge(AstralFire, 1).RequiresGauge(MP, 800)
                .IfGaugeAtLeast(Hearts, 1).ScaleGauge(MP, 1 / 3f).IfGaugeAtMost(Hearts, 0).SetGauge(MP, 0)
                .SetGauge(Hearts, 0).SetGauge(AstralFire, 3);
            if (astralSoul)
                flare.GainGauge(AstralSoul, 3);
        }
        if (paradox)
        {
            Plain(b.Gcd("Paradox", 540, AidParadox), 540, e)
                .RequiresGauge(AstralFire, 1).SpendGauge(Paradox, 1).SpendGauge(MP, 1600).ApplyStatus(Firestarter, 30);
            Plain(b.Gcd("ParadoxIce", 540, AidParadox), 540, e)
                .RequiresGauge(UmbralIce, 1).SpendGauge(Paradox, 1);
        }

        // Fire III: every variant enters Astral Fire III
        if (level >= 35)
        {
            EnterFire(Fire(b.Gcd("Fire3Proc", 290, AidFire3), 290, e).RequiresStatus(Firestarter).RemoveStatus(Firestarter), paradox);
            EnterFire(Fire(b.Gcd("Fire3", 290, AidFire3).Cast(3.5f * 0.5f * k), 290, e).RequiresGauge(UmbralIce, 3).ForbidStatus(Firestarter), paradox);
            EnterFire(Fire(b.Gcd("Fire3LowIce", 290, AidFire3).Cast(3.5f * k), 290, e).RequiresGauge(UmbralIce, 1).RequiresGaugeAtMost(UmbralIce, 2).ForbidStatus(Firestarter), paradox);
            EnterFire(Fire(b.Gcd("Fire3Cold", 290, AidFire3).Cast(3.5f * k), 290, e).RequiresGaugeAtMost(AstralFire, 0).RequiresGaugeAtMost(UmbralIce, 0).ForbidStatus(Firestarter).SpendGauge(MP, 2000), paradox);
        }
        if (level >= 82)
            EnterFire(Fire(b.Gcd("HighFire2", 100, AidHighFire2).Shape(AoeShape.TargetCircle, 5, 0, 25).Cast(3.0f * 0.5f * k).AoeFalloff(100), 100, e).RequiresGauge(UmbralIce, 3), paradox);
        else if (level >= 35)
            EnterFire(Fire(b.Gcd("HighFire2", 80, AidFire2).Shape(AoeShape.TargetCircle, 5, 0, 25).Cast(3.0f * 0.5f * k).AoeFalloff(80), 80, e).RequiresGauge(UmbralIce, 3), paradox);

        // ---- ice phase ----
        if (level >= 35)
        {
            EnterIce(Ice(b.Gcd("Blizzard3", 290, AidBlizzard3).Cast(3.5f * 0.5f * k), 290, e).RequiresGauge(AstralFire, 3), paradox);
            EnterIce(Ice(b.Gcd("Blizzard3LowFire", 290, AidBlizzard3).Cast(3.5f * k), 290, e).RequiresGauge(AstralFire, 1).RequiresGaugeAtMost(AstralFire, 2), paradox);
            EnterIce(Ice(b.Gcd("Blizzard3Cold", 290, AidBlizzard3).Cast(3.5f * k), 290, e).RequiresGaugeAtMost(AstralFire, 0).RequiresGaugeAtMost(UmbralIce, 0).SpendGauge(MP, 800), paradox);
            var blizzard2 = level >= 82 ? 100 : 80;
            var blizzard2Aid = level >= 82 ? AidHighBlizzard2 : AidBlizzard2;
            EnterIce(Ice(b.Gcd("HighBlizzard2", blizzard2, blizzard2Aid).Shape(AoeShape.TargetCircle, 5, 0, 25).Cast(3.0f * 0.5f * k).AoeFalloff(blizzard2), blizzard2, e).RequiresGauge(AstralFire, 3), paradox);
            EnterIce(Ice(b.Gcd("HighBlizzard2Cold", blizzard2, blizzard2Aid).Shape(AoeShape.TargetCircle, 5, 0, 25).Cast(3.0f * k).AoeFalloff(blizzard2), blizzard2, e).RequiresGaugeAtMost(AstralFire, 2).RequiresGaugeAtMost(UmbralIce, 0), paradox);
        }
        if (level >= 58)
            IceMp(Ice(b.Gcd("Blizzard4", 300, AidBlizzard4).Cast(2.0f * k), 300, e).RequiresGauge(UmbralIce, 1).SetGauge(Hearts, 3));
        if (level >= 40)
        {
            var freeze = Ice(b.Gcd("Freeze", 120, AidFreeze).Shape(AoeShape.TargetCircle, 5, 0, 25).Cast(2.0f * k).AoeFalloff(120), 120, e).RequiresGauge(UmbralIce, 1);
            if (hearts)
                freeze.SetGauge(Hearts, 3);
            IceMp(freeze);
        }
        if (level >= 35)
        {
            var umbralSoul = b.Gcd("UmbralSoul", 0, AidUmbralSoul).NoTarget().RequiresGauge(UmbralIce, 1).GainGauge(UmbralIce, 1);
            if (hearts)
                umbralSoul.GainGauge(Hearts, 1);
            IceMp(umbralSoul);
        }

        // ---- element-neutral GCDs ----
        if (level >= 92)
        {
            Plain(b.Gcd("HighThunder", 150, AidHighThunder), 150, e)
                .RequiresStatus(Thunderhead).RemoveStatus(Thunderhead).Dot(Thunder, 30, 60 * e);
            Plain(b.Gcd("HighThunder2", 100, AidHighThunder2).Shape(AoeShape.TargetCircle, 5, 0, 25).AoeFalloff(100), 100, e)
                .RequiresStatus(Thunderhead).RemoveStatus(Thunderhead).Dot(Thunder, 24, 40 * e, aoe: true);
        }
        else
        {
            // Thunder III (45) / Thunder (6); Thunder IV (64) / Thunder II (26)
            var thunder3 = level >= 45;
            if (level >= 6)
                Plain(b.Gcd("HighThunder", thunder3 ? 120 : 100, thunder3 ? AidThunder3 : AidThunder1), thunder3 ? 120 : 100, e)
                    .RequiresStatus(Thunderhead).RemoveStatus(Thunderhead).Dot(Thunder, thunder3 ? 27 : 24, (thunder3 ? 50 : 45) * e);
            var thunder4 = level >= 64;
            if (level >= 26)
                Plain(b.Gcd("HighThunder2", thunder4 ? 80 : 60, thunder4 ? AidThunder4 : AidThunder2).Shape(AoeShape.TargetCircle, 5, 0, 25).AoeFalloff(thunder4 ? 80 : 60), thunder4 ? 80 : 60, e)
                    .RequiresStatus(Thunderhead).RemoveStatus(Thunderhead).Dot(Thunder, thunder4 ? 21 : 18, (thunder4 ? 35 : 30) * e, aoe: true);
        }
        if (level >= 80)
            Plain(b.Gcd("Xenoglossy", 890, AidXenoglossy), 890, e).SpendGauge(Polyglot, 1);
        if (level >= 70)
        {
            var foul = Plain(b.Gcd("Foul", 600, AidFoul).Shape(AoeShape.TargetCircle, 5, 0, 25).AoeFalloff(600 * 0.75f), 600, e).SpendGauge(Polyglot, 1);
            if (level < 80)
                foul.Cast(2.5f * k);
        }

        // ---- abilities ----
        if (level >= 4)
        {
            var transpose = b.Ogcd("Transpose", 0, TransposeCD, AidTranspose).RequiresGauge(AstralFire, 1).ApplyStatus(Thunderhead, 30);
            if (paradox)
                transpose.IfGaugeAtLeast(AstralFire, 3).SetGauge(Paradox, 1);
            EnterElementSide(transpose.SetGauge(AstralFire, 0).SetGauge(UmbralIce, 1).SetGauge(AstralSoul, 0));
            var transposeIce = b.Ogcd("TransposeIce", 0, TransposeCD, AidTranspose).RequiresGauge(UmbralIce, 1).ApplyStatus(Thunderhead, 30);
            if (paradox)
                transposeIce.IfGaugeAtLeast(UmbralIce, 3).IfGaugeAtLeast(Hearts, 3).SetGauge(Paradox, 1);
            EnterElementSide(transposeIce.SetGauge(UmbralIce, 0).SetGauge(AstralFire, 1));
        }
        if (level >= 30)
        {
            var manafont = b.Ogcd("Manafont", 0, ManafontCD, AidManafont).NeedsUptime(0.01f).RequiresGauge(AstralFire, 1)
                .SetGauge(MP, 10000).SetGauge(AstralFire, 3);
            if (hearts)
                manafont.SetGauge(Hearts, 3);
            if (paradox)
                manafont.SetGauge(Paradox, 1);
            manafont.ApplyStatus(Thunderhead, 30);
        }
        if (level >= 86)
        {
            b.Ogcd("Amplifier", 0, AmplifierCD, AidAmplifier).RequiresGauge(AstralFire, 1).GainGauge(Polyglot, 1);
            b.Ogcd("AmplifierIce", 0, AmplifierCD, AidAmplifier).RequiresGauge(UmbralIce, 1).GainGauge(Polyglot, 1);
        }
        if (level >= 52)
            b.Ogcd("LeyLines", 0, LeyLinesCD, AidLeyLines).NeedsUptime(12.5f).NeedsStanding(6.5f).ForbidStatus(LeyLines).ApplyStatus(LeyLines, 20);
        if (level >= 66)
            b.Ogcd("Triplecast", 0, TriplecastCD, AidTriplecast).ForbidStatus(Triplecast).ApplyStatus(Triplecast, 15, 3);
        if (level >= 18)
            b.Ogcd("Swiftcast", 0, SwiftcastCD, AidSwiftcast).ForbidStatus(Swiftcast).ApplyStatus(Swiftcast, 10);
        return b.Build();
    }

    // fire spell: element multiplier of the stance it is cast under (x Enochian while an element is up)
    private static JobBuilder.SkillBuilder Fire(JobBuilder.SkillBuilder s, float p, float e) => s
        .PotencyIfGauge(AstralFire, 3, p * 1.8f * e).PotencyIfGauge(AstralFire, 2, p * 1.6f * e).PotencyIfGauge(AstralFire, 1, p * 1.4f * e)
        .PotencyIfGauge(UmbralIce, 3, p * 0.7f * e).PotencyIfGauge(UmbralIce, 2, p * 0.8f * e).PotencyIfGauge(UmbralIce, 1, p * 0.9f * e);

    private static JobBuilder.SkillBuilder Ice(JobBuilder.SkillBuilder s, float p, float e) => s
        .PotencyIfGauge(AstralFire, 3, p * 0.7f * e).PotencyIfGauge(AstralFire, 2, p * 0.8f * e).PotencyIfGauge(AstralFire, 1, p * 0.9f * e)
        .PotencyIfGauge(UmbralIce, 1, p * e);

    // no element multiplier, only Enochian
    private static JobBuilder.SkillBuilder Plain(JobBuilder.SkillBuilder s, float p, float e) => s
        .PotencyIfGauge(AstralFire, 1, p * e).PotencyIfGauge(UmbralIce, 1, p * e);

    // effects of entering Astral Fire III (conditions are checked on the state before the spell); paradox: the swap from Umbral Ice III with
    // full hearts grants Paradox (level 90)
    private static JobBuilder.SkillBuilder EnterFire(JobBuilder.SkillBuilder s, bool paradox)
    {
        s.IfGaugeAtMost(AstralFire, 0).ApplyStatus(Thunderhead, 30);
        if (paradox)
            s.IfGaugeAtLeast(UmbralIce, 3).IfGaugeAtLeast(Hearts, 3).SetGauge(Paradox, 1);
        return EnterElementSide(s.SetGauge(AstralFire, 3).SetGauge(UmbralIce, 0));
    }

    // entering Umbral Ice III: Astral Soul is lost, MP refills; paradox: the swap from Astral Fire III grants Paradox (level 90)
    private static JobBuilder.SkillBuilder EnterIce(JobBuilder.SkillBuilder s, bool paradox)
    {
        s.IfGaugeAtMost(UmbralIce, 0).ApplyStatus(Thunderhead, 30);
        if (paradox)
            s.IfGaugeAtLeast(AstralFire, 3).SetGauge(Paradox, 1);
        return EnterElementSide(s.SetGauge(UmbralIce, 3).SetGauge(AstralFire, 0).SetGauge(AstralSoul, 0).SetGauge(MP, 10000));
    }

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

    // level sync: the set tuned at the lowest level of the player's band (90-99 L90, 80-89 L80, 70-79 L70, 60-69 L60); the Lv100 set at 100 and in the other bands
    public static EngineWeights DefaultWeights(int level = 100) => EngineWeights.Parse(level switch
    {
        >= 100 => DefaultWeightsJson,
        >= 90 => WeightsL90Json,
        >= 80 => WeightsL80Json,
        >= 70 => WeightsL70Json,
        >= 60 => WeightsL60Json,
        _ => DefaultWeightsJson
    });

    // CMA-ES on the xan timeline harness at a 4-GCD horizon (9 fights with --party-buffs 7.8, deterministic search; tools/blm_engine_eval
    // tuned/weights-BLM-v6.json, from the v5 set at horizon 3). MinNodes: the full 4-GCD search is at most about 5,100 nodes on the harness
    // fights, so every search completes and live play matches the deterministic search (a BLM node costs about 1.5 us: 32 skill variants);
    // BudgetMs only caps a search larger than MinNodes. SliceNodes: the frame slice is counted in nodes, so which frame a search finishes on
    // does not depend on the machine either.
    public const string DefaultWeightsJson = """
    {
      "OverCap": 0.17453705,
      "Combo": 0,
      "LambdaScale": 0,
      "TargetPull": 0,
      "SwitchMargin": 5.21513,
      "FillerScale": 0.9387101,
      "BurstBias": 3.7759147,
      "StatusRemainder": 0,
      "CycleScale": 0.90034443,
      "CooldownLambdaScale": 0,
      "ForecastSelfBuffs": 0,
      "UnlockScale": 0.49027303,
      "StatusValue": { "Thunderhead": 55.06597, "Firestarter": 10 },
      "CooldownValue": { "LeyLinesCD": 517.76056, "TriplecastCD": 327.97345, "SwiftcastCD": 1119.0126 },
      "GaugeValue": {},
      "HorizonGcds": 4,
      "BudgetMs": 12,
      "MinNodes": 6000,
      "SliceNodes": 100
    }
    """;

    // CMA-ES at level 90 on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-BLM-L90.json),
    // search settings as the Lv100 set
    public const string WeightsL90Json = """
    {
      "OverCap": 0, "Combo": 0, "LambdaScale": 0, "TargetPull": 0, "SwitchMargin": 1.9716182, "FillerScale": 0.8699005, "BurstBias": 2.4193878,
      "StatusRemainder": 0, "CycleScale": 0.6435867, "CooldownLambdaScale": 0, "ForecastSelfBuffs": 0, "UnlockScale": 0.26489314,
      "StatusValue": { "Thunderhead": 87.77913, "Firestarter": 10 }, "CooldownValue": { "LeyLinesCD": 1007.45233, "TriplecastCD": 178.22011, "SwiftcastCD": 1088.014 }, "GaugeValue": {},
      "HorizonGcds": 3,
      "BudgetMs": 0.8,
      "MinNodes": 1000,
      "SliceNodes": 50
    }
    """;

    // CMA-ES at level 80 on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-BLM-L80.json),
    // search settings as the Lv100 set
    public const string WeightsL80Json = """
    {
      "OverCap": 1.2192621, "Combo": 0, "LambdaScale": 0, "TargetPull": 0, "SwitchMargin": 0, "FillerScale": 0.9708093, "BurstBias": 3.3788342,
      "StatusRemainder": 0, "CycleScale": 0.9925814, "CooldownLambdaScale": 0, "ForecastSelfBuffs": 0, "UnlockScale": 0.7071904,
      "StatusValue": { "Thunderhead": 58.281296, "Firestarter": 10 }, "CooldownValue": { "LeyLinesCD": 1112.7119, "TriplecastCD": 111.60762, "SwiftcastCD": 923.65674 }, "GaugeValue": {},
      "HorizonGcds": 3,
      "BudgetMs": 0.8,
      "MinNodes": 1000,
      "SliceNodes": 50
    }
    """;

    // CMA-ES at level 70 on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-BLM-L70.json),
    // search settings as the Lv100 set
    public const string WeightsL70Json = """
    {
      "OverCap": 1.1988604, "Combo": 0, "LambdaScale": 0, "TargetPull": 0, "SwitchMargin": 12.460077, "FillerScale": 1.2005736, "BurstBias": 2.7290595,
      "StatusRemainder": 0, "CycleScale": 1.2019205, "CooldownLambdaScale": 0, "ForecastSelfBuffs": 0, "UnlockScale": 0,
      "StatusValue": { "Thunderhead": 150, "Firestarter": 10 }, "CooldownValue": { "LeyLinesCD": 545.7163, "TriplecastCD": 87.32902, "SwiftcastCD": 1500 }, "GaugeValue": {},
      "HorizonGcds": 3,
      "BudgetMs": 0.8,
      "MinNodes": 1000,
      "SliceNodes": 50
    }
    """;

    // CMA-ES at level 60 on the xan timeline harness (9 fights, deterministic search; tools/blm_engine_eval tuned/weights-BLM-L60.json),
    // search settings as the Lv100 set
    public const string WeightsL60Json = """
    {
      "OverCap": 0.16836789, "Combo": 0, "LambdaScale": 0, "TargetPull": 0, "SwitchMargin": 3.0059433, "FillerScale": 1.2867589, "BurstBias": 0.080096856,
      "StatusRemainder": 0, "CycleScale": 0.5, "CooldownLambdaScale": 0, "ForecastSelfBuffs": 0, "UnlockScale": 0.2444121,
      "StatusValue": { "Thunderhead": 122.93776, "Firestarter": 10 }, "CooldownValue": { "LeyLinesCD": 702.895, "TriplecastCD": 113.461815, "SwiftcastCD": 776.13776 }, "GaugeValue": {},
      "HorizonGcds": 3,
      "BudgetMs": 0.8,
      "MinNodes": 1000,
      "SliceNodes": 50
    }
    """;
}
