#!/usr/bin/env python3
"""Extract job-agnostic tuning features from anonymized public-log samples."""

from __future__ import annotations

import argparse
import json
import statistics
import sys
from typing import Any

from jobs import normalize_job
from public_data_cache import forbidden_key_paths


LIST_FEATURE_KEYS = {
    "burstUseTimes": "burstUseTimesMedian",
    "majorBuffUseTimes": "majorBuffUseTimesMedian",
    "gaugeSpendTimes": "gaugeSpendTimesMedian",
    "potionUseTimes": "potionUseTimesMedian",
    "downtimeStarts": "downtimeStartsMedian",
    "downtimeEnds": "downtimeEndsMedian",
}


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


def median(values: list[float]) -> float:
    return float(statistics.median(values)) if values else 0.0


def median_by_index(samples: list[dict[str, Any]], key: str) -> list[float]:
    lists = [as_number_list(sample.get(key)) for sample in samples]
    max_len = max((len(values) for values in lists), default=0)
    result: list[float] = []
    for index in range(max_len):
        values = [items[index] for items in lists if index < len(items)]
        result.append(median(values))
    return result


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


def profile_candidates(samples: list[dict[str, Any]]) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for sample in samples:
        candidates = sample.get("profileCandidates")
        if isinstance(candidates, dict):
            result.append(candidates)
    return result


def extract_job_features(raw: dict[str, Any], job: str) -> dict[str, Any]:
    normalized_job = normalize_job(job)
    samples = raw.get("samples")
    sample_list = [sample for sample in samples if isinstance(sample, dict)] if isinstance(samples, list) else []
    kill_times = [number for sample in sample_list if (number := as_number(sample.get("killTime"))) is not None]

    result: dict[str, Any] = {
        "contentId": raw.get("contentId", 0) if isinstance(raw.get("contentId", 0), int) else 0,
        "encounterId": raw.get("encounterId", 0) if isinstance(raw.get("encounterId", 0), int) else 0,
        "job": normalized_job,
        "sampleCount": len(sample_list),
        "killTimeMedian": median(kill_times),
        "burstUseTimesMedian": [],
        "majorBuffUseTimesMedian": [],
        "gaugeSpendTimesMedian": [],
        "potionUseTimesMedian": [],
        "downtimeStartsMedian": [],
        "downtimeEndsMedian": [],
        "profileCandidates": profile_candidates(sample_list),
    }

    for input_key, output_key in LIST_FEATURE_KEYS.items():
        result[output_key] = median_by_index(sample_list, input_key)

    return result


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Extract common tuning features for a supported job.")
    parser.add_argument("--job", required=True)
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_json(args.out, extract_job_features(read_json(args.input_path), args.job))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

