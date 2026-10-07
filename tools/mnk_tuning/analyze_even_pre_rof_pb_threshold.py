#!/usr/bin/env python3
"""Review impact of changing EvenPreRoFPBStartThreshold."""

from __future__ import annotations

import argparse
import json
import math
import sys
from typing import Any


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
        raise RuntimeError("threshold review input must be a JSON object")
    return payload


def write_json(path: str, payload: dict[str, Any]) -> None:
    text = json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True)
    found = [item for item in FORBIDDEN_TEXT if item in text]
    if found:
        raise RuntimeError(f"output contains forbidden text: {', '.join(found)}")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
        handle.write("\n")


def as_number(value: Any) -> float | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    return None


def as_number_list(value: Any) -> list[float]:
    if not isinstance(value, list):
        return []
    result: list[float] = []
    for item in value:
        number = as_number(item)
        if number is not None:
            result.append(number)
    return result


def nearest_rank(sorted_values: list[float], percentile: float) -> float | None:
    if not sorted_values:
        return None
    rank = max(1, math.ceil(percentile / 100.0 * len(sorted_values)))
    return sorted_values[rank - 1]


def delta_stats(values: list[float]) -> dict[str, Any]:
    sorted_values = sorted(values)
    if not sorted_values:
        return {
            "min": None,
            "p10": None,
            "p25": None,
            "median": None,
            "p75": None,
            "p90": None,
            "max": None,
        }

    return {
        "min": sorted_values[0],
        "p10": nearest_rank(sorted_values, 10),
        "p25": nearest_rank(sorted_values, 25),
        "median": nearest_rank(sorted_values, 50),
        "p75": nearest_rank(sorted_values, 75),
        "p90": nearest_rank(sorted_values, 90),
        "max": sorted_values[-1],
    }


def recommendation(affected_percent: float) -> str:
    if affected_percent == 0:
        return "safe_candidate"
    if affected_percent <= 5:
        return "possible_after_review"
    return "hold"


def nearest_pre_rof_pb_delta(sample: dict[str, Any]) -> tuple[float, float, float] | None:
    rof_times = as_number_list(sample.get("riddleOfFireUseTimes"))
    pb_times = as_number_list(sample.get("perfectBalanceUseTimes"))
    if not rof_times:
        return None

    first_rof = rof_times[0]
    pre_rof_pbs = [time for time in pb_times if time < first_rof]
    if not pre_rof_pbs:
        return None

    pre_rof_pb = max(pre_rof_pbs)
    return first_rof - pre_rof_pb, first_rof, pre_rof_pb


def analyze_threshold(payload: dict[str, Any], current: float, candidate: float) -> dict[str, Any]:
    samples = payload.get("samples")
    sample_list = [sample for sample in samples if isinstance(sample, dict)] if isinstance(samples, list) else []
    deltas: list[float] = []
    allowed_by_current = 0
    allowed_by_candidate = 0
    affected_samples: list[dict[str, Any]] = []

    for sample in sample_list:
        result = nearest_pre_rof_pb_delta(sample)
        if result is None:
            continue

        delta, first_rof, pre_rof_pb = result
        deltas.append(delta)
        if delta <= current:
            allowed_by_current += 1
        if delta <= candidate:
            allowed_by_candidate += 1
        if candidate < delta <= current and len(affected_samples) < 20:
            affected_samples.append({
                "sourceRank": sample.get("sourceRank") if isinstance(sample.get("sourceRank"), int) else 0,
                "killTime": as_number(sample.get("killTime")) or 0.0,
                "delta": delta,
                "firstRoF": first_rof,
                "preRoFPB": pre_rof_pb,
            })

    affected_by_candidate = allowed_by_current - allowed_by_candidate
    with_rof_and_pre_pb = len(deltas)
    affected_percent = affected_by_candidate / with_rof_and_pre_pb * 100.0 if with_rof_and_pre_pb else 0.0

    return {
        "job": payload.get("job", "MNK"),
        "sampleCount": len(sample_list),
        "currentThreshold": current,
        "candidateThreshold": candidate,
        "withRoFAndPreRoFPB": with_rof_and_pre_pb,
        "allowedByCurrent": allowed_by_current,
        "allowedByCandidate": allowed_by_candidate,
        "affectedByCandidate": affected_by_candidate,
        "affectedPercent": affected_percent,
        "deltaStats": delta_stats(deltas),
        "affectedSamples": affected_samples,
        "recommendation": recommendation(affected_percent),
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Analyze EvenPreRoFPBStartThreshold candidate impact.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--current", type=float, default=6.0)
    parser.add_argument("--candidate", type=float, default=5.0)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_json(args.out, analyze_threshold(read_json(args.input_path), args.current, args.candidate))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
