#!/usr/bin/env python3
"""Export anonymized MNKTuningProfile candidate JSON from feature summaries."""

from __future__ import annotations

import argparse
import json
import statistics
import sys
from typing import Any


PROFILE_KEYS = (
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

DEFAULT_PROFILE = {
    "MajorBurstMeleeSafeGCDs": 5.0,
    "PBMeleeSafeGCDs": 3.0,
    "EvenPreRoFPBStartThreshold": 6.0,
    "RoFBrotherhoodResyncWindow": 45.0,
    "TargetAvailableUptimeRequired": 20.0,
    "PotionFutureWindowLeeway": 5.0,
    "RoWTwoMinuteHoldWindow": 45.0,
    "RoWUptimeRequired": 15.0,
    "RoWEndBurnLeeway": 20.0,
    "LastRoFWindowLeeway": 70.0,
    "LastBrotherhoodWindowLeeway": 135.0,
    "LastPotionWindowLeeway": 285.0,
}


def as_number(value: Any) -> float | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    return None


def median(values: list[float]) -> float:
    return float(statistics.median(values)) if values else 0.0


def trimmed_mean(values: list[float], trim_ratio: float = 0.1) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    trim_count = int(len(ordered) * trim_ratio)
    if trim_count > 0 and trim_count * 2 < len(ordered):
        ordered = ordered[trim_count:-trim_count]
    return float(sum(ordered) / len(ordered))


def aggregate(values: list[float], method: str) -> float:
    if method == "trimmed-mean":
        return trimmed_mean(values)
    return median(values)


def profile_candidates(features: dict[str, Any]) -> list[dict[str, Any]]:
    candidates = features.get("profileCandidates")
    if isinstance(candidates, list):
        return [candidate for candidate in candidates if isinstance(candidate, dict)]

    profile = features.get("profile")
    if isinstance(profile, dict):
        return [profile]

    return []


def aggregate_profile(features: dict[str, Any], method: str) -> dict[str, float]:
    candidates = profile_candidates(features)
    result: dict[str, float] = {}
    for key in PROFILE_KEYS:
        values = [number for candidate in candidates if (number := as_number(candidate.get(key))) is not None]
        result[key] = aggregate(values, method) if values else DEFAULT_PROFILE[key]
    return result


def export_mnk_profile(features: dict[str, Any], method: str) -> dict[str, Any]:
    return {
        "contentId": int(features.get("contentId", 0)) if isinstance(features.get("contentId", 0), int) else 0,
        "job": "MNK",
        "source": "top300_public_logs_aggregated",
        "sampleCount": int(features.get("sampleCount", 0)) if isinstance(features.get("sampleCount", 0), int) else 0,
        "aggregation": method,
        "profile": aggregate_profile(features, method),
    }


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("feature input must be a JSON object")
    return payload


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Export anonymized MNKTuningProfile candidate values.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--method", choices=("median", "trimmed-mean"), default="median")
    args = parser.parse_args(argv)

    output = export_mnk_profile(read_json(args.input_path), args.method)
    write_json(args.out, output)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

