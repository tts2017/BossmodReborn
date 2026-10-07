from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
MNK_SOURCE = REPO_ROOT / "BossMod" / "Autorotation" / "Standard" / "xan" / "Melee" / "MNK.cs"


def method_body(source: str, signature: str) -> str:
    start = source.index(signature)
    brace = source.index("{", start)
    depth = 0
    for index in range(brace, len(source)):
        char = source[index]
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                return source[brace + 1:index]
    raise AssertionError(f"Could not find method body for {signature}")


def test_blitz_decision_is_single_source() -> None:
    source = MNK_SOURCE.read_text(encoding="utf-8")

    assert "private readonly record struct BlitzDecision(bool ConsumeFormlessFirst, bool Use, bool UrgentNow);" in source
    assert "private BlitzDecision EvaluateBlitzDecision(in Strategy strategy, AID currentBlitz, in BurstPlan burst)" in source

    evaluate = method_body(source, "private BlitzDecision EvaluateBlitzDecision")
    before_formless = method_body(source, "private bool ShouldUseBlitzBeforeFormlessGCD")
    use_blitz = method_body(source, "private void UseBlitz")

    assert "ShouldConsumeFormlessBeforePendingBlitz(strategy, currentBlitz, effectiveBlitzLeft)" in evaluate
    assert "return new(true, false, false);" in evaluate
    assert "var urgentNow = useCurrentPhantomRushNow || fightEndBurn || burstBuffActive || burstMeleeCompression || blitzExpiring || plannedPBContinuationBlitz || pbOvercapUnlockBlitz;" in evaluate
    assert "BlitzStrategy.Automatic => !holdAutomaticPhantomRushForBurst && urgentNow" in evaluate

    assert "var decision = EvaluateBlitzDecision(strategy, currentBlitz, burst);" in before_formless
    assert "return !decision.ConsumeFormlessFirst && decision.Use;" in before_formless
    assert "holdAutomaticPhantomRushForBurst" not in before_formless
    assert "strategy.Blitz.Value switch" not in before_formless

    assert "var decision = EvaluateBlitzDecision(strategy, currentBlitz, burst);" in use_blitz
    assert "if (decision.Use)" in use_blitz
    assert "strategy.Blitz.Value == BlitzStrategy.Force || decision.UrgentNow" in use_blitz
    assert "holdAutomaticPhantomRushForBurst" not in use_blitz
    assert "strategy.Blitz.Value switch" not in use_blitz


if __name__ == "__main__":
    test_blitz_decision_is_single_source()
    print("phase1 blitz decision refactor test passed")
