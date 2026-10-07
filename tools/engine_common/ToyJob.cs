using BossMod.Autorotation.Engine;

namespace EngineTools;

// A small job that exercises every engine feature the tests check: a 3-step combo that builds a gauge, a gauge spender
// worth holding for burst, a 2-minute self buff to align with the raid window, and a 2-charge oGCD.
public static class ToyJob
{
    public static JobDefinition Build() => new JobBuilder("TOY", baseGcd: 2.5f)
        .Gauge("Heat", max: 100)
        .Status("Fury", maxDuration: 20, damageMultiplier: 1.2f)
        .Cooldown("BurstCD", recast: 120)
        .Cooldown("StrikeCD", recast: 30, maxCharges: 2)
        .Gcd("Slash", potency: 200).GainGauge("Heat", 5).StartsCombo()
        .Gcd("Cut", potency: 150).ComboFrom("Slash", potency: 300).GainGauge("Heat", 5)
        .Gcd("Finish", potency: 150).ComboFrom("Cut", potency: 400).GainGauge("Heat", 10).EndsCombo()
        .Gcd("Blast", potency: 600).SpendGauge("Heat", 50).ComboNeutral()
        .Ogcd("Burst", potency: 0, cooldown: "BurstCD").ApplyStatus("Fury", 20).NoTarget()
        .Ogcd("Strike", potency: 300, cooldown: "StrikeCD")
        .Build();

    public static EngineWeights DefaultWeights() => new();
}
