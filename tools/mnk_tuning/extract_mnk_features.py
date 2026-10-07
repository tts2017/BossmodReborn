#!/usr/bin/env python3
"""Extract anonymous MNK feature summaries from collected input JSON."""

from __future__ import annotations

import argparse
import json
import statistics
import sys
from typing import Any


LIST_FEATURE_KEYS = (
    "rofUseTimes",
    "brotherhoodUseTimes",
    "perfectBalanceUseTimes",
    "potionUseTimes",
    "downtimeStarts",
    "downtimeEnds",
)

OUTPUT_LIST_KEYS = {
    "rofUseTimes": "rofUseTimesMedian",
    "brotherhoodUseTimes": "brotherhoodUseTimesMedian",
    "perfectBalanceUseTimes": "perfectBalanceUseTimesMedian",
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
    medians: list[float] = []
    for index in range(max_len):
        values = [items[index] for items in lists if index < len(items)]
        medians.append(median(values))
    return medians


def anonymous_samples(raw: Any) -> list[dict[str, Any]]:
    if isinstance(raw, dict) and isinstance(raw.get("samples"), list):
        return [item for item in raw["samples"] if isinstance(item, dict)]
    if isinstance(raw, list):
        return [item for item in raw if isinstance(item, dict)]
    return []


def extract_mnk_features(raw: Any) -> dict[str, Any]:
    root = raw if isinstance(raw, dict) else {}
    samples = anonymous_samples(raw)
    kill_times = [number for sample in samples if (number := as_number(sample.get("killTime"))) is not None]

    result: dict[str, Any] = {
        "contentId": int(root.get("contentId", 0)) if isinstance(root.get("contentId", 0), int) else 0,
        "encounterId": int(root.get("encounterId", 0)) if isinstance(root.get("encounterId", 0), int) else 0,
        "job": "MNK",
        "sampleCount": len(samples),
        "killTimeMedian": median(kill_times),
        "rofUseTimesMedian": [],
        "brotherhoodUseTimesMedian": [],
        "perfectBalanceUseTimesMedian": [],
        "potionUseTimesMedian": [],
        "downtimeStartsMedian": [],
        "downtimeEndsMedian": [],
    }

    for input_key, output_key in OUTPUT_LIST_KEYS.items():
        result[output_key] = median_by_index(samples, input_key)

    return result


def read_json(path: str) -> Any:
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Extract anonymized MNK feature medians.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    features = extract_mnk_features(read_json(args.input_path))
    write_json(args.out, features)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

