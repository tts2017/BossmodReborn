namespace BossMod.Autorotation.Engine.Jobs;

// Dragoon for the rotation engine (any level; the weights were tuned at level 100): data only, no BossMod dependency (also compiled by the engine tools).
// Mechanics and potencies follow tools/xan_timeline_harness/DrgCombatState.cs + DrgPotencyScorer.cs (positionals credited; Power Surge
// x1.10, Lance Charge x1.10, Life of the Dragon x1.15 (Blood of the Dragon x1.10 below 70), Battle Litany +10% critical rate =
// x1.0522 on everything, Life Surge = a guaranteed critical hit on the next weaponskill = x1.3913 of its expected potency; the Chaotic
// Spring dot credited at the press for the seconds it newly covers).
// Life Surge is split by the combo step it is pressed before (the game lets it go anywhere; the guides use it on Heavens' Thrust and
// Drakesbane only, Coerthan Torment in AoE): its potency bonus is a conditional potency on those weaponskills, and every weaponskill
// uses the status up. The two combos: True / Raiden Thrust -> Vorpal Thrust (Lance Barrage) -> Heavens' Thrust -> Fang and Claw ->
// Drakesbane and -> Disembowel (Spiral Blow) -> Chaotic Spring -> Wheeling Thrust -> Drakesbane; AoE Doom Spike / Draconian Fury ->
// Sonic Thrust -> Coerthan Torment. Piercing Talon is pressed by the module (out of melee range) and is not in the definition.
public static class DrgDefinition
{
    // gauges
    public const string Focus = "Focus"; // Firstminds' Focus (90+)
    public const string FourthStep = "FourthStep"; // 1 right after Fang and Claw / Wheeling Thrust (Drakesbane combos from either; cleared by every other weaponskill)

    // statuses
    public const string PowerSurge = "PowerSurge", LanceCharge = "LanceCharge", BattleLitany = "BattleLitany", LifeSurge = "LifeSurge";
    public const string LifeOfTheDragon = "LifeOfTheDragon", NastrondReady = "NastrondReady", DiveReady = "DiveReady", DraconianFire = "DraconianFire";
    public const string DragonsFlight = "DragonsFlight", StarcrossReady = "StarcrossReady", ChaoticSpring = "ChaoticSpring";

    // cooldown groups
    public const string LifeSurgeCD = "LifeSurgeCD", LanceChargeCD = "LanceChargeCD", LitanyCD = "BattleLitanyCD", JumpCD = "JumpCD",
        GeirskogulCD = "GeirskogulCD", DragonfireCD = "DragonfireDiveCD", StardiverCD = "StardiverCD", WyrmwindCD = "WyrmwindThrustCD";

    // the expected-crit model of DrgPotencyScorer (25% rate, 160% damage): a guaranteed crit over a normal hit, Battle Litany's crit rate
    public const float CritRate = 0.25f, CritDamage = 1.60f;
    public const float LifeSurgeRatio = CritDamage / (1 + CritRate * (CritDamage - 1)); // 1.3913
    public const float LitanyMultiplier = (1 + (CritRate + 0.10f) * (CritDamage - 1)) / (1 + CritRate * (CritDamage - 1)); // 1.0522
    // the harness's party (1450 potency/s) over the 20 s of Battle Litany
    public const float LitanyPartyValue = 1450f * 20f * (LitanyMultiplier - 1);

    // game action ids (BossMod.DRG.AID)
    public const uint AidTrueThrust = 75, AidVorpalThrust = 78, AidLifeSurge = 83, AidDisembowel = 87, AidFullThrust = 84, AidLanceCharge = 85,
        AidJump = 92, AidDoomSpike = 86, AidChaosThrust = 88, AidDragonfireDive = 96, AidBattleLitany = 3557, AidFangAndClaw = 3554,
        AidWheelingThrust = 3556, AidGeirskogul = 3555, AidSonicThrust = 7397, AidDrakesbane = 36952, AidMirageDive = 7399, AidNastrond = 7400,
        AidCoerthanTorment = 16477, AidHighJump = 16478, AidRaidenThrust = 16479, AidStardiver = 16480, AidDraconianFury = 25770,
        AidChaoticSpring = 25772, AidHeavensThrust = 25771, AidWyrmwindThrust = 25773, AidRiseOfTheDragon = 36953, AidLanceBarrage = 36954,
        AidSpiralBlow = 36955, AidStarcross = 36956;

    // level: the player's (synced) level. Skills not unlocked there are left out; potencies follow DrgPotencyScorer.cs (l76 / l94 branches,
    // Drakesbane 86, Geirskogul / Nastrond 90, Jump 54). Replaced actions keep their skill name and change id / potency: Vorpal Thrust ->
    // Lance Barrage (96), Full Thrust -> Heavens' Thrust (86), Disembowel -> Spiral Blow (96), Chaos Thrust -> Chaotic Spring (86),
    // Jump -> High Jump (74). Raiden Thrust (76) and Draconian Fury (82) are their own skills (they need Draconian Fire).
    public static JobDefinition Build(float gcd = 2.5f, int level = 100)
    {
        var l76 = level >= 76;
        var l94 = level >= 94;
        var b = new JobBuilder("DRG", gcd)
            .Gauge(Focus, 2).Gauge(FourthStep, 1, flat: true)
            .Status(PowerSurge, 30, damageMultiplier: 1.10f, upkeep: true)
            .Status(LanceCharge, 20, damageMultiplier: 1.10f)
            .Status(BattleLitany, 20, damageMultiplier: LitanyMultiplier)
            .Status(LifeSurge, 5)
            .Status(LifeOfTheDragon, 20, damageMultiplier: level >= 70 ? 1.15f : 1.10f)
            .Status(NastrondReady, 20).Status(DiveReady, 15).Status(DraconianFire, 30).Status(DragonsFlight, 30).Status(StarcrossReady, 20)
            .Status(ChaoticSpring, 24)
            .Cooldown(LifeSurgeCD, 40, level >= 88 ? 2 : 1) // Enhanced Life Surge (88)
            .Cooldown(LanceChargeCD, 60).Cooldown(LitanyCD, 120).Cooldown(JumpCD, 30).Cooldown(GeirskogulCD, 60).Cooldown(DragonfireCD, 120)
            .Cooldown(StardiverCD, 30).Cooldown(WyrmwindCD, 10);
        // the combo loop's average potency per GCD (the free-filler analysis stops at Raiden Thrust, which needs Draconian Fire): the two
        // five-step combos from 64, the shorter ones before
        b.FillerPotency(FillerPerGcd(level));

        // ---- single-target combos ----
        var trueThrust = Ws(b.Gcd("TrueThrust", l76 ? 230 : 170, AidTrueThrust).StartsCombo());
        if (l76)
        {
            trueThrust.ForbidStatus(DraconianFire); // replaced by Raiden Thrust while Draconian Fire is up
            var raiden = Ws(b.Gcd("RaidenThrust", l94 ? 320 : 280, AidRaidenThrust).StartsCombo().RequiresStatus(DraconianFire).RemoveStatus(DraconianFire));
            if (level >= 90)
                raiden.GainGauge(Focus, 1);
        }
        if (level >= 4)
        {
            var vorpal = Ws(b.Gcd("VorpalThrust", level >= 96 ? 130 : l76 ? 130 : 100, level >= 96 ? AidLanceBarrage : AidVorpalThrust)
                .ComboFrom("TrueThrust", level >= 96 ? 340 : l76 ? 280 : 250));
            if (l76)
                vorpal.ComboFrom("RaidenThrust", level >= 96 ? 340 : 280);
        }
        if (level >= 26)
        {
            // Full Thrust until Heavens' Thrust (86), only as combo (the game refuses it otherwise); Life Surge's guaranteed crit listed first
            var ht = level >= 86 ? l94 ? 460 : 400 : 380;
            var heavens = Ws(b.Gcd("HeavensThrust", ht, level >= 86 ? AidHeavensThrust : AidFullThrust).PotencyIfStatus(LifeSurge, ht * LifeSurgeRatio).RequiresCombo("VorpalThrust"));
            if (level < 56)
                heavens.EndsCombo();
        }
        if (level >= 56)
        {
            var fang = Ws(b.Gcd("FangAndClaw", l94 ? 340 : 300, AidFangAndClaw).RequiresCombo("HeavensThrust")).SetGauge(FourthStep, 1);
            if (level < 64)
                fang.EndsCombo();
        }
        if (level >= 18)
        {
            var disembowel = Ws(b.Gcd("Disembowel", level >= 96 ? 140 : l76 ? 140 : 100, level >= 96 ? AidSpiralBlow : AidDisembowel)
                .ComboFrom("TrueThrust", level >= 96 ? 300 : l76 ? 250 : 210).IfCombo("TrueThrust").ApplyStatus(PowerSurge, 30));
            if (l76)
                disembowel.ComboFrom("RaidenThrust", level >= 96 ? 300 : 250).IfCombo("RaidenThrust").ApplyStatus(PowerSurge, 30);
            if (level < 50)
                disembowel.EndsCombo();
        }
        if (level >= 50)
        {
            // Chaos Thrust until Chaotic Spring (86): rear positional included, only as combo; the dot (24 s, 40 / 45 per tick)
            var chaos = Ws(b.Gcd("ChaoticSpring", level >= 86 ? l94 ? 340 : 300 : 260, level >= 86 ? AidChaoticSpring : AidChaosThrust)
                .RequiresCombo("Disembowel").Dot(ChaoticSpring, 24, level >= 86 ? 45 : 40));
            if (level < 58)
                chaos.EndsCombo();
        }
        if (level >= 58)
        {
            var wheeling = Ws(b.Gcd("WheelingThrust", l94 ? 340 : 300, AidWheelingThrust).RequiresCombo("ChaoticSpring")).SetGauge(FourthStep, 1);
            if (level < 64)
                wheeling.EndsCombo();
        }
        if (level >= 64)
        {
            // Drakesbane only as the fifth step of either combo (the game refuses it otherwise: the FourthStep gauge), Draconian Fire from 76
            var db = l94 ? 460 : level >= 86 ? 400 : 380;
            var drakesbane = Ws(b.Gcd("Drakesbane", db, AidDrakesbane).PotencyIfStatus(LifeSurge, db * LifeSurgeRatio).RequiresGauge(FourthStep, 1).ComboFrom("FangAndClaw", db).ComboFrom("WheelingThrust", db));
            drakesbane.EndsCombo();
            if (l76)
                drakesbane.ApplyStatus(DraconianFire, 30);
        }

        // ---- AoE combo (10y line: every target takes the full potency) ----
        if (level >= 40)
        {
            var doom = Ws(b.Gcd("DoomSpike", 110, AidDoomSpike).Shape(AoeShape.Line, 10, 2, 10).AoeFalloff(110).StartsCombo());
            if (level >= 82)
            {
                doom.ForbidStatus(DraconianFire);
                var fury = Ws(b.Gcd("DraconianFury", 130, AidDraconianFury).Shape(AoeShape.Line, 10, 2, 10).AoeFalloff(130).StartsCombo().RequiresStatus(DraconianFire).RemoveStatus(DraconianFire));
                if (level >= 90)
                    fury.GainGauge(Focus, 1);
            }
        }
        if (level >= 62)
        {
            var sonic = Ws(b.Gcd("SonicThrust", 100, AidSonicThrust).Shape(AoeShape.Line, 10, 2, 10).AoeFalloff(100).ComboFrom("DoomSpike", 120).IfCombo("DoomSpike").ApplyStatus(PowerSurge, 30));
            if (level >= 82)
                sonic.ComboFrom("DraconianFury", 120).IfCombo("DraconianFury").ApplyStatus(PowerSurge, 30);
            if (level < 72)
                sonic.EndsCombo();
        }
        if (level >= 72)
        {
            var torment = Ws(b.Gcd("CoerthanTorment", 150, AidCoerthanTorment).Shape(AoeShape.Line, 10, 2, 10).AoeFalloff(150).PotencyIfStatus(LifeSurge, 150 * LifeSurgeRatio).RequiresCombo("SonicThrust"));
            torment.EndsCombo();
            if (level >= 82)
                torment.ApplyStatus(DraconianFire, 30);
        }

        // ---- abilities ----
        // Life Surge before the weaponskill it is meant for (the combo step before it): Heavens' Thrust / Full Thrust (Vorpal Thrust below 26),
        // Drakesbane (from either fourth step), Coerthan Torment (Sonic Thrust / Doom Spike before it) on 3+ targets
        if (level >= 6)
        {
            var lsTarget = level >= 26 ? "VorpalThrust" : "TrueThrust";
            LifeSurgeSkill(b, "LifeSurge").RequiresCombo(lsTarget).ComboNeutral(); // RequiresCombo would make the ability continue the combo
            if (level >= 64)
                LifeSurgeSkill(b, "LifeSurgeDrakesbane").RequiresGauge(FourthStep, 1);
            if (level >= 72)
                LifeSurgeSkill(b, "LifeSurgeAoe").RequiresCombo("SonicThrust").ComboNeutral().RequiresTargets(3);
            else if (level >= 62)
                LifeSurgeSkill(b, "LifeSurgeAoe").RequiresCombo("DoomSpike").ComboNeutral().RequiresTargets(3);
        }
        if (level >= 30)
            b.Ogcd("LanceCharge", 0, LanceChargeCD, AidLanceCharge).NoTarget().NeedsUptime(2).ApplyStatus(LanceCharge, 20);
        if (level >= 52)
            b.Ogcd("BattleLitany", 0, LitanyCD, AidBattleLitany).NoTarget().NeedsUptime(2).PartyValue(LitanyPartyValue).ApplyStatus(BattleLitany, 20);
        if (level >= 30)
        {
            // Jump until High Jump (74); Dive Ready from 68
            var jump = b.Ogcd("HighJump", level >= 74 ? 400 : level >= 54 ? 320 : 250, JumpCD, level >= 74 ? AidHighJump : AidJump).Lock(0.8f);
            if (level >= 68)
                jump.ApplyStatus(DiveReady, 15);
        }
        if (level >= 68)
            b.Ogcd("MirageDive", 380, actionId: AidMirageDive).RequiresStatus(DiveReady).RemoveStatus(DiveReady);
        if (level >= 60)
        {
            // Life of the Dragon (70; Blood of the Dragon's x1.10 before it) and Nastrond Ready
            var geirskogul = b.Ogcd("Geirskogul", level >= 90 ? 280 : 200, GeirskogulCD, AidGeirskogul).Shape(AoeShape.Line, 15, 2, 15).AoeFalloff((level >= 90 ? 280 : 200) * 0.5f)
                .ApplyStatus(LifeOfTheDragon, 20);
            if (level >= 70)
                geirskogul.ApplyStatus(NastrondReady, 20);
        }
        if (level >= 70)
            b.Ogcd("Nastrond", level >= 90 ? 720 : 600, actionId: AidNastrond).Shape(AoeShape.Line, 15, 2, 15).AoeFalloff((level >= 90 ? 720 : 600) * 0.5f).RequiresStatus(NastrondReady).RemoveStatus(NastrondReady);
        if (level >= 50)
        {
            var dive = b.Ogcd("DragonfireDive", 500, DragonfireCD, AidDragonfireDive).Shape(AoeShape.TargetCircle, 5, 0, 20).AoeFalloff(250).Lock(0.8f);
            if (level >= 92)
                dive.ApplyStatus(DragonsFlight, 30);
        }
        if (level >= 92)
            b.Ogcd("RiseOfTheDragon", 550, actionId: AidRiseOfTheDragon).Shape(AoeShape.TargetCircle, 5, 0, 20).AoeFalloff(275).RequiresStatus(DragonsFlight).RemoveStatus(DragonsFlight);
        if (level >= 80)
        {
            // 1.5 s lock: alone in its weave slot (the search counts the slot by lock time)
            var stardiver = b.Ogcd("Stardiver", l94 ? 840 : 720, StardiverCD, AidStardiver).Shape(AoeShape.TargetCircle, 5, 0, 20).AoeFalloff((l94 ? 840 : 720) * 0.6f).Lock(1.5f).RequiresStatus(LifeOfTheDragon);
            if (level >= 100)
                stardiver.ApplyStatus(StarcrossReady, 20);
        }
        if (level >= 100)
            b.Ogcd("Starcross", 1000, actionId: AidStarcross).Shape(AoeShape.TargetCircle, 5, 0, 20).AoeFalloff(600).RequiresStatus(StarcrossReady).RequiresStatus(LifeOfTheDragon).RemoveStatus(StarcrossReady);
        if (level >= 90)
            b.Ogcd("WyrmwindThrust", l94 ? 440 : 420, WyrmwindCD, AidWyrmwindThrust).Shape(AoeShape.Line, 15, 2, 15).AoeFalloff((l94 ? 440 : 420) * 0.5f).SpendGauge(Focus, 2);
        return b.Build();
    }

    // the average combo GCD at the level (the potencies of DrgPotencyScorer, the two combos alternating, positionals credited)
    private static float FillerPerGcd(int level)
    {
        var l76 = level >= 76;
        var l94 = level >= 94;
        var start = l76 ? (230 + (l94 ? 320 : 280)) / 2f : level >= 76 ? 230 : 170;
        var vorpal = level >= 96 ? 340 : l76 ? 280 : 250;
        var heavens = level >= 86 ? l94 ? 460 : 400 : 380;
        var fourth = l94 ? 340 : 300;
        var drakesbane = l94 ? 460 : level >= 86 ? 400 : 380;
        var disembowel = level >= 96 ? 300 : l76 ? 250 : 210;
        var chaos = level >= 86 ? l94 ? 340 : 300 : 260;
        if (level >= 64)
            return (2 * start + vorpal + heavens + fourth + drakesbane + disembowel + chaos + fourth) / 10f;
        if (level >= 58)
            return (2 * start + vorpal + heavens + fourth + disembowel + chaos + fourth) / 8f;
        if (level >= 56)
            return (2 * start + vorpal + heavens + fourth + disembowel + chaos) / 7f;
        if (level >= 50)
            return (2 * start + vorpal + heavens + disembowel + chaos) / 6f;
        if (level >= 26)
            return (2 * start + vorpal + heavens + disembowel) / 5f;
        return (2 * start + vorpal + disembowel) / 4f;
    }

    // a weaponskill: uses up Life Surge and ends the fourth-step window
    private static JobBuilder.SkillBuilder Ws(JobBuilder.SkillBuilder s) => s.Weaponskill().RemoveStatus(LifeSurge).SetGauge(FourthStep, 0);

    private static JobBuilder.SkillBuilder LifeSurgeSkill(JobBuilder b, string name)
        => b.Ogcd(name, 0, LifeSurgeCD, AidLifeSurge).NoTarget().ForbidStatus(LifeSurge).ApplyStatus(LifeSurge, 5);

    public static EngineWeights DefaultWeights(int level = 100) => EngineWeights.Parse(DefaultWeightsJson);

    // CMA-ES on the xan timeline harness at a 5-GCD horizon (9 fights with --party-buffs 7.8, deterministic search; tools/blm_engine_eval
    // tuned/weights-DRG-v2.json, from the v1 set at horizon 4). MinNodes / SliceNodes: every search (at most about 25,000 nodes in the 9 fights,
    // the opener) completes and frame slices are counted in nodes, so live play does not depend on the machine's timing (as MNK); BudgetMs only
    // caps a search larger than MinNodes.
    public const string DefaultWeightsJson = """
    {
      "OverCap": 0.5, "Combo": 0, "LambdaScale": 0.94642204, "TargetPull": 0, "SwitchMargin": 8.126431, "FillerScale": 1.5, "BurstBias": 0,
      "StatusRemainder": 0, "CycleScale": 1, "CooldownLambdaScale": 0.32816863, "ForecastSelfBuffs": 1, "UnlockScale": 0.8823015,
      "StatusValue": {}, "CooldownValue": { "LanceChargeCD": 0, "LifeSurgeCD": 15.592682 }, "GaugeValue": {},
      "HorizonGcds": 5,
      "BudgetMs": 60,
      "MinNodes": 30000,
      "SliceNodes": 625
    }
    """;
}
