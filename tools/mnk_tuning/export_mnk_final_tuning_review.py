#!/usr/bin/env python3
"""Export formula-backed final review data for MNKTuningProfile candidates."""

from __future__ import annotations

import argparse
import json
import math
import sys
from typing import Any


FIELD_ORDER = (
    "MajorBurstMeleeSafeGCDs",
    "PBMeleeSafeGCDs",
    "EvenPreRoFPBStartThreshold",
    "RoFBrotherhoodResyncWindow",
    "TargetAvailableUptimeRequired",
    "PotionFutureWindowLeeway",
    "RoWTwoMinuteHoldWindow",
    "RoWUptimeRequired",
    "RoWEndBurnLeeway",
    "LastRoFWindowLeeway",
    "LastBrotherhoodWindowLeeway",
    "LastPotionWindowLeeway",
)

FORBIDDEN_TEXT = (
    "reportCode",
    "name",
    "server",
    "guild",
    "lodestoneID",
    "reportUrl",
    "reportURL",
    "client_secret",
    "access_token",
    "Authorization",
    "Bearer",
)


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError(f"input must be a JSON object: {path}")
    return payload


def write_json(path: str, payload: dict[str, Any]) -> None:
    text = json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True)
    found = [item for item in FORBIDDEN_TEXT if item in text]
    if found:
        raise RuntimeError(f"output contains forbidden text: {', '.join(found)}")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
        handle.write("\n")


def number_or_none(value: Any) -> float | int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return value
    return None


def dist(distributions: dict[str, Any], key: str) -> dict[str, Any]:
    value = distributions.get(key)
    return value if isinstance(value, dict) else {}


def evidence(distribution: dict[str, Any], keys: tuple[str, ...] = ("median", "p75", "p90")) -> dict[str, Any]:
    return {key: number_or_none(distribution.get(key)) for key in keys}


def ceil_or_none(value: Any) -> float | None:
    number = number_or_none(value)
    if number is None:
        return None
    return float(math.ceil(number))


def ceil_abs_window(distribution: dict[str, Any]) -> float | None:
    p10 = number_or_none(distribution.get("p10"))
    p90 = number_or_none(distribution.get("p90"))
    if p10 is None or p90 is None:
        return None
    return max(1.0, float(math.ceil(max(abs(float(p10)), abs(float(p90))))))


def candidate_item(candidates: dict[str, Any], field: str) -> dict[str, Any]:
    item = candidates.get(field)
    return item if isinstance(item, dict) else {}


def candidate_value(candidates: dict[str, Any], field: str) -> float | int | None:
    return number_or_none(candidate_item(candidates, field).get("value"))


def candidate_confidence(candidates: dict[str, Any], field: str) -> str:
    confidence = candidate_item(candidates, field).get("confidence")
    return confidence if isinstance(confidence, str) else "unmapped"


def direct_field(candidates: dict[str, Any], field: str, source: str) -> dict[str, Any]:
    value = candidate_value(candidates, field)
    return {
        "currentDefault": value,
        "candidateValue": value,
        "formula": "common direct",
        "evidence": {
            "source": source,
            "value": value,
        },
        "confidence": "direct",
        "applyRecommendation": "no_change",
        "reason": "Direct candidate can remain unchanged when it matches the current default profile.",
    }


def hold_field(
    candidates: dict[str, Any],
    field: str,
    candidate: float | int | None,
    formula: str,
    field_evidence: dict[str, Any],
    reason: str,
) -> dict[str, Any]:
    confidence = candidate_confidence(candidates, field)
    return {
        "currentDefault": candidate_value(candidates, field),
        "candidateValue": candidate,
        "formula": formula,
        "evidence": field_evidence,
        "confidence": confidence if confidence in {"direct", "candidate", "unmapped"} else "unmapped",
        "applyRecommendation": "hold",
        "reason": reason,
    }


def export_final_review(candidates_payload: dict[str, Any], distributions_payload: dict[str, Any]) -> dict[str, Any]:
    candidates = candidates_payload.get("candidates")
    if not isinstance(candidates, dict):
        raise RuntimeError("candidate input must contain a candidates object")

    distributions = distributions_payload.get("distributions")
    if not isinstance(distributions, dict):
        raise RuntimeError("distribution input must contain a distributions object")

    pb_before_rof = dist(distributions, "pbBeforeRoFDelta")
    rof_brotherhood = dist(distributions, "rofBrotherhoodDelta")

    fields = {
        "MajorBurstMeleeSafeGCDs": direct_field(candidates, "MajorBurstMeleeSafeGCDs", "common.MajorBurstSafeGCDs"),
        "PBMeleeSafeGCDs": direct_field(candidates, "PBMeleeSafeGCDs", "common.MinorBurstSafeGCDs"),
        "EvenPreRoFPBStartThreshold": hold_field(
            candidates,
            "EvenPreRoFPBStartThreshold",
            ceil_or_none(pb_before_rof.get("p90")),
            "ceil(p90(pbBeforeRoFDelta))",
            evidence(pb_before_rof),
            "Candidate derived from PB before RoF timing, but MNK.cs threshold semantics must be checked before applying.",
        ),
        "RoFBrotherhoodResyncWindow": hold_field(
            candidates,
            "RoFBrotherhoodResyncWindow",
            ceil_abs_window(rof_brotherhood),
            "max(1.0, ceil(max(abs(p10), abs(p90)) of rofBrotherhoodDelta))",
            evidence(rof_brotherhood, ("p10", "median", "p90")),
            "RoF/Brotherhood are intentionally close, but current MNK.cs window semantics must be checked.",
        ),
        "TargetAvailableUptimeRequired": direct_field(candidates, "TargetAvailableUptimeRequired", "common.TargetAvailableUptimeRequired"),
        "PotionFutureWindowLeeway": direct_field(candidates, "PotionFutureWindowLeeway", "common.PotionFutureWindowLeeway"),
        "RoWTwoMinuteHoldWindow": hold_field(
            candidates,
            "RoWTwoMinuteHoldWindow",
            candidate_value(candidates, "RoWTwoMinuteHoldWindow"),
            "common.DowntimeHoldWindow",
            {"value": candidate_value(candidates, "RoWTwoMinuteHoldWindow")},
            "DowntimeHoldWindow is common profile value, not RoW-specific hold validation.",
        ),
        "RoWUptimeRequired": hold_field(
            candidates,
            "RoWUptimeRequired",
            None,
            "unmapped",
            {},
            "Needs uptime/downtime data, not only cast timing.",
        ),
        "RoWEndBurnLeeway": hold_field(
            candidates,
            "RoWEndBurnLeeway",
            candidate_value(candidates, "RoWEndBurnLeeway"),
            "common.FightEndCommitWindow",
            {"value": candidate_value(candidates, "RoWEndBurnLeeway")},
            "Common fight-end commit value needs RoW end-burn validation before applying.",
        ),
        "LastRoFWindowLeeway": hold_field(
            candidates,
            "LastRoFWindowLeeway",
            candidate_value(candidates, "LastRoFWindowLeeway"),
            "common.LastMajorBuffWindowLeeway",
            {"value": candidate_value(candidates, "LastRoFWindowLeeway")},
            "Common major-buff leeway should be compared with current default before applying.",
        ),
        "LastBrotherhoodWindowLeeway": hold_field(
            candidates,
            "LastBrotherhoodWindowLeeway",
            None,
            "unmapped",
            evidence(dist(distributions, "lastBrotherhoodFromFightEnd")),
            "Current default differs from LastRoF; do not collapse without additional evidence.",
        ),
        "LastPotionWindowLeeway": direct_field(candidates, "LastPotionWindowLeeway", "common.LastPotionWindowLeeway"),
    }

    return {
        "job": "MNK",
        "source": "top200_public_logs_final_review",
        "sampleCount": candidates_payload.get("sampleCount") if isinstance(candidates_payload.get("sampleCount"), int) else 0,
        "fields": {field: fields[field] for field in FIELD_ORDER},
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Export formula-backed final review data for MNKTuningProfile candidates.")
    parser.add_argument("--candidates", required=True)
    parser.add_argument("--distributions", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_json(args.out, export_final_review(read_json(args.candidates), read_json(args.distributions)))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
