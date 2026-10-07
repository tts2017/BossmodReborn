#!/usr/bin/env python3
"""Export common job tuning profile candidates from extracted features."""

from __future__ import annotations

import argparse
import json
import statistics
import sys
from typing import Any

from jobs import normalize_job
from public_data_cache import forbidden_key_paths


PROFILE_KEYS = (
    "MajorBurstSafeGCDs",
    "MinorBurstSafeGCDs",
    "TargetAvailableUptimeRequired",
    "PotionFutureWindowLeeway",
    "FightEndCommitWindow",
    "DowntimeHoldWindow",
    "LastMajorBuffWindowLeeway",
    "LastPotionWindowLeeway",
)

DEFAULT_PROFILE = {
    "MajorBurstSafeGCDs": 5.0,
    "MinorBurstSafeGCDs": 3.0,
    "TargetAvailableUptimeRequired": 20.0,
    "PotionFutureWindowLeeway": 5.0,
    "FightEndCommitWindow": 20.0,
    "DowntimeHoldWindow": 45.0,
    "LastMajorBuffWindowLeeway": 70.0,
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


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("job feature input must be a JSON object")
    forbidden = forbidden_key_paths(payload)
    if forbidden:
        raise RuntimeError(f"input contains forbidden keys: {', '.join(forbidden)}")
    return payload


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def aggregate_profile(features: dict[str, Any], method: str) -> dict[str, float]:
    candidates = features.get("profileCandidates")
    candidate_list = [candidate for candidate in candidates if isinstance(candidate, dict)] if isinstance(candidates, list) else []
    result: dict[str, float] = {}
    for key in PROFILE_KEYS:
        values = [number for candidate in candidate_list if (number := as_number(candidate.get(key))) is not None]
        result[key] = aggregate(values, method) if values else DEFAULT_PROFILE[key]
    return result


def export_job_profile(features: dict[str, Any], job: str, method: str) -> dict[str, Any]:
    normalized_job = normalize_job(job)
    return {
        "contentId": features.get("contentId", 0) if isinstance(features.get("contentId", 0), int) else 0,
        "job": normalized_job,
        "source": "top200_public_logs_aggregated",
        "sampleCount": features.get("sampleCount", 0) if isinstance(features.get("sampleCount", 0), int) else 0,
        "aggregation": method,
        "profile": aggregate_profile(features, method),
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Export common profile candidate values for a supported job.")
    parser.add_argument("--job", required=True)
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--method", choices=("median", "trimmed-mean"), default="median")
    args = parser.parse_args(argv)

    write_json(args.out, export_job_profile(read_json(args.input_path), args.job, args.method))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

