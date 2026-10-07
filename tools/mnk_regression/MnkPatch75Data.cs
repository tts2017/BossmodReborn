namespace MnkRegression;

public static class MnkPatch75Data
{
    public const double AnimationLock = 0.65;
    public const double WeaponskillAnimationLock = 0.60;
    public const double PerfectBalanceDuration = 20.0;
    public const double BlitzDuration = 20.0;
    public const double RiddleOfFireDuration = 20.0;
    public const double BrotherhoodDuration = 20.0;
    public const double RiddleOfWindDuration = 15.0;
    public const double FiresReplyDuration = 20.0;
    public const double WindsReplyDuration = 15.0;
    public const double PotionDuration = 30.0;
    public const double PotionPotencyMultiplier = 1.10;
    public const double EvenPreBurstPbLead = 5.5;
    public const double AutoAttackWeaponDelay = 2.56;
    public const double AutoAttackPotencyEquivalent = 85.333;
    public const double DefaultLevel100Gcd = 1.94;
    public const double LegacyScenarioReferenceGcd = 2.49;

    public static int GreasedLightningHasteModifier(int level)
        => level switch
        {
            < 20 => 95,
            < 40 => 90,
            < 76 => 85,
            _ => 80
        };

    public static double EffectiveGcd(double configuredGcd, int level)
    {
        var level100Gcd = configuredGcd > 2.10
            ? Math.Round(DefaultLevel100Gcd * configuredGcd / LegacyScenarioReferenceGcd, 2, MidpointRounding.AwayFromZero)
            : configuredGcd;
        var preTraitGcd = level100Gcd / 0.80;
        return Math.Floor(preTraitGcd * GreasedLightningHasteModifier(level) + 0.0001) / 100.0;
    }

    public static double EffectiveAutoAttackDelay(int level, bool riddleOfWindActive)
    {
        var delay = AutoAttackWeaponDelay * GreasedLightningHasteModifier(level) / 100.0;
        if (riddleOfWindActive)
            delay *= 0.5;
        return Math.Floor(delay * 1000 + 0.0001) / 1000.0;
    }

    public static double EvenPbTrackingLead => EvenPreBurstPbLead + AnimationLock + 0.20;

    public static string ChakraSpender(int level, bool aoe)
        => aoe
            ? level >= 74 ? "Enlightenment" : "HowlingFist"
            : level >= 54 ? "ForbiddenChakra" : "SteelPeak";

    public static bool IsBlitz(string? action)
        => action is "ElixirField" or "ElixirBurst" or "CelestialRevolution" or "FlintStrike" or "RisingPhoenix" or "TornadoKick" or "PhantomRush";

    public static bool IsPhantomRush(string? action)
        => action is "TornadoKick" or "PhantomRush";

    public static bool IsEnemyGcd(string? action)
        => action is not null and not "Meditate" and not "FormShift";

    public static bool IsMeleeGcd(string? action)
        => action is "Bootshine" or "LeapingOpo" or "DragonKick"
            or "TrueStrike" or "RisingRaptor" or "TwinSnakes"
            or "SnapPunch" or "PouncingCoeurl" or "Demolish"
            or "ArmOfTheDestroyer" or "ShadowOfTheDestroyer" or "FourPointFury" or "Rockbreaker"
            or "SixSidedStar" or "ElixirField" or "ElixirBurst" or "CelestialRevolution"
            or "FlintStrike" or "RisingPhoenix" or "TornadoKick" or "PhantomRush";

    public static bool IsUnlocked(string action, int level)
        => action switch
        {
            "SteelPeak" => level >= 15,
            "HowlingFist" => level >= 40,
            "ForbiddenChakra" => level >= 54,
            "Enlightenment" => level >= 74,
            "ArmOfTheDestroyer" => level >= 26,
            "ShadowOfTheDestroyer" => level >= 82,
            "Rockbreaker" => level >= 30,
            "FourPointFury" => level >= 45,
            "DragonKick" => level >= 50,
            "LeapingOpo" or "RisingRaptor" or "PouncingCoeurl" => level >= 92,
            "PerfectBalance" => level >= 50,
            "ElixirField" or "FlintStrike" or "TornadoKick" or "CelestialRevolution" => level >= 60,
            "RiddleOfEarth" => level >= 64,
            "RiddleOfFire" => level >= 68,
            "Brotherhood" => level >= 70,
            "RiddleOfWind" => level >= 72,
            "SixSidedStar" => level >= 80,
            "RisingPhoenix" => level >= 86,
            "PhantomRush" => level >= 90,
            "ElixirBurst" => level >= 92,
            "WindsReply" => level >= 96,
            "FiresReply" => level >= 100,
            _ => true
        };

    public static double PotencyEquivalent(string action, PlayerGauge gauge, int targets, int level, bool guaranteedCrit)
    {
        targets = Math.Max(1, targets);
        var primary = action switch
        {
            "AutoAttack" => AutoAttackPotencyEquivalent,
            "Bootshine" => gauge.OpoFury > 0 ? 420 : 220,
            "LeapingOpo" => gauge.OpoFury > 0 ? 460 : 260,
            "DragonKick" => 320,
            "TrueStrike" => gauge.RaptorFury > 0 ? 500 : 300,
            "RisingRaptor" => gauge.RaptorFury > 0 ? 540 : 340,
            "TwinSnakes" => 420,
            "SnapPunch" => gauge.CoeurlFury > 0 ? 480 : 330,
            "PouncingCoeurl" => gauge.CoeurlFury > 0 ? 520 : 370,
            "Demolish" => 420,
            "ArmOfTheDestroyer" => 110,
            "ShadowOfTheDestroyer" => 120,
            "FourPointFury" => 140,
            "Rockbreaker" => 150,
            "ElixirField" => 800,
            "ElixirBurst" => 900,
            "CelestialRevolution" => 600,
            "FlintStrike" => 800,
            "RisingPhoenix" => 900,
            "TornadoKick" => 1200,
            "PhantomRush" => 1500,
            "SixSidedStar" => 780 + gauge.Chakra * 80,
            "SteelPeak" => 180,
            "ForbiddenChakra" => 400,
            "HowlingFist" => 100,
            "Enlightenment" => 160,
            "WindsReply" => 1040,
            "FiresReply" => 1400,
            _ => 0
        };

        if (primary <= 0)
            return 0;

        var potency = action switch
        {
            "ArmOfTheDestroyer" or "ShadowOfTheDestroyer" or "FourPointFury" or "Rockbreaker" or "HowlingFist" or "Enlightenment"
                => primary * targets,
            "ElixirField" or "ElixirBurst" or "FlintStrike" or "RisingPhoenix" or "TornadoKick" or "PhantomRush" or "WindsReply" or "FiresReply"
                => primary * (1 + (targets - 1) * 0.65),
            _ => primary
        };

        if (guaranteedCrit)
            potency *= 1.40;
        return potency;
    }
}
