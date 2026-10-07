#!/usr/bin/env python3
"""Filter NIN common samples to opener Raiton-before-Kunai samples."""

from __future__ import annotations

import argparse
import copy
import json
import math
import sys
from typing import Any


FORBIDDEN_KEYS = {
    "client_secret",
    "access_token",
    "authorization",
    "bearer",
    "reportcode",
    "reporturl",
    "charactername",
    "playername",
    "guildname",
    "servername",
    "name",
    "server",
    "guild",
    "lodestoneid",
}

REASON_KEYS = (
    "selected",
    "missingRaiton",
    "missingKunai",
    "missingHyosho",
    "raitonAfterOrAtKunai",
    "raitonAfterOrAtHyosho",
    "raitonAfterOpenerWindow",
    "dokumoriAfterRaiton",
    "kassatsuAfterOrAtKunai",
)


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError(f"input must be a JSON object: {path}")
    return payload


def forbidden_key_paths(payload: Any, prefix: str = "$") -> list[str]:
    found: list[str] = []
    if isinstance(payload, dict):
        for key, value in payload.items():
            key_text = str(key)
            path = f"{prefix}.{key_text}"
            if key_text.casefold() in FORBIDDEN_KEYS:
                found.append(path)
            found.extend(forbidden_key_paths(value, path))
    elif isinstance(payload, list):
        for index, value in enumerate(payload):
            found.extend(forbidden_key_paths(value, f"{prefix}[{index}]"))
    return found


def write_json(path: str, payload: dict[str, Any]) -> None:
    forbidden = forbidden_key_paths(payload)
    if forbidden:
        raise RuntimeError(f"output contains forbidden keys: {', '.join(forbidden)}")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def sanitized(value: Any) -> Any:
    if isinstance(value, dict):
        result: dict[str, Any] = {}
        for key, child in value.items():
            if str(key).casefold() in FORBIDDEN_KEYS:
                continue
            result[str(key)] = sanitized(child)
        return result
    if isinstance(value, list):
        return [sanitized(item) for item in value]
    return copy.deepcopy(value)


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
    return sorted(result)


def source_rank(sample: dict[str, Any]) -> int | None:
    value = sample.get("sourceRank")
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def nearest_rank(sorted_values: list[float], percentile: float) -> float | None:
    if not sorted_values:
        return None
    rank = max(1, math.ceil(percentile / 100.0 * len(sorted_values)))
    return sorted_values[rank - 1]


def stats(values: list[float]) -> dict[str, Any]:
    sorted_values = sorted(values)
    if not sorted_values:
        return {
            "min": None,
            "p25": None,
            "median": None,
            "p75": None,
            "p90": None,
            "max": None,
        }
    return {
        "min": sorted_values[0],
        "p25": nearest_rank(sorted_values, 25),
        "median": nearest_rank(sorted_values, 50),
        "p75": nearest_rank(sorted_values, 75),
        "p90": nearest_rank(sorted_values, 90),
        "max": sorted_values[-1],
    }


def first_time(specific_sample: dict[str, Any], key: str) -> float | None:
    times = as_number_list(specific_sample.get(key))
    return times[0] if times else None


def classify_sample(
    specific_sample: dict[str, Any],
    opener_window: float,
    require_dokumori_before_raiton: bool,
    require_kassatsu_before_kunai: bool,
) -> tuple[bool, str, dict[str, float]]:
    first_raiton = first_time(specific_sample, "raitonUseTimes")
    first_kunai = first_time(specific_sample, "kunaisBaneUseTimes")
    first_hyosho = first_time(specific_sample, "hyoshoRanryuUseTimes")
    first_dokumori = first_time(specific_sample, "dokumoriUseTimes")
    first_kassatsu = first_time(specific_sample, "kassatsuUseTimes")

    if first_raiton is None:
        return False, "missingRaiton", {}
    if first_kunai is None:
        return False, "missingKunai", {}
    if first_hyosho is None:
        return False, "missingHyosho", {}
    if first_raiton >= first_kunai:
        return False, "raitonAfterOrAtKunai", {}
    if first_raiton >= first_hyosho:
        return False, "raitonAfterOrAtHyosho", {}
    if first_raiton > opener_window:
        return False, "raitonAfterOpenerWindow", {}
    if require_dokumori_before_raiton and (first_dokumori is None or first_dokumori > first_raiton):
        return False, "dokumoriAfterRaiton", {}
    if require_kassatsu_before_kunai and (first_kassatsu is None or first_kassatsu >= first_kunai):
        return False, "kassatsuAfterOrAtKunai", {}

    return True, "selected", {
        "firstRaiton": first_raiton,
        "firstKunai": first_kunai,
        "firstHyosho": first_hyosho,
        "raitonToKunaiDelta": first_kunai - first_raiton,
        "raitonToHyoshoDelta": first_hyosho - first_raiton,
    }


def filter_samples(
    samples_payload: dict[str, Any],
    nin_specific_payload: dict[str, Any],
    opener_window: float,
    require_dokumori_before_raiton: bool,
    require_kassatsu_before_kunai: bool,
) -> tuple[dict[str, Any], dict[str, Any]]:
    raw_samples = samples_payload.get("samples")
    sample_list = [sample for sample in raw_samples if isinstance(sample, dict)] if isinstance(raw_samples, list) else []

    specific_samples = nin_specific_payload.get("samples")
    specific_by_rank = {
        rank: sample
        for sample in specific_samples
        if isinstance(sample, dict) and (rank := source_rank(sample)) is not None
    } if isinstance(specific_samples, list) else {}

    reason_counts = {key: 0 for key in REASON_KEYS}
    selected_samples: list[dict[str, Any]] = []
    timing_values: dict[str, list[float]] = {
        "firstRaiton": [],
        "firstKunai": [],
        "firstHyosho": [],
        "raitonToKunaiDelta": [],
        "raitonToHyoshoDelta": [],
    }

    for sample in sample_list:
        rank = source_rank(sample)
        specific_sample = specific_by_rank.get(rank) if rank is not None else None
        if specific_sample is None:
            reason_counts["missingRaiton"] += 1
            continue

        selected, reason, timings = classify_sample(
            specific_sample,
            opener_window,
            require_dokumori_before_raiton,
            require_kassatsu_before_kunai,
        )
        reason_counts[reason] += 1
        if not selected:
            continue

        selected_samples.append(sanitized(sample))
        for key, value in timings.items():
            timing_values[key].append(value)

    selected_count = len(selected_samples)
    input_count = len(sample_list)
    filter_metadata = {
        "type": "pre_raiton",
        "openerWindow": opener_window,
        "requireDokumoriBeforeRaiton": require_dokumori_before_raiton,
        "requireKassatsuBeforeKunai": require_kassatsu_before_kunai,
    }
    output_payload = {
        "contentId": samples_payload.get("contentId", 0),
        "encounterId": samples_payload.get("encounterId", 0),
        "difficulty": samples_payload.get("difficulty", 0),
        "partition": samples_payload.get("partition", 0),
        "metric": samples_payload.get("metric", "rdps"),
        "job": "NIN",
        "source": "fflogs_public_rankings_casts_derived_pre_raiton_filtered",
        "sampleCount": selected_count,
        "filter": filter_metadata,
        "samples": selected_samples,
    }
    summary_payload = {
        "job": "NIN",
        "inputSampleCount": input_count,
        "selectedSampleCount": selected_count,
        "selectedPercent": (selected_count / input_count * 100.0) if input_count else 0.0,
        "openerWindow": opener_window,
        "reasonCounts": reason_counts,
        "selectedTimingStats": {key: stats(values) for key, values in timing_values.items()},
    }
    return output_payload, summary_payload


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Filter NIN samples to opener Raiton-before-Kunai rows.")
    parser.add_argument("--samples", required=True)
    parser.add_argument("--nin-specific", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--summary-out", required=True)
    parser.add_argument("--opener-window", type=float, default=20.0)
    parser.add_argument("--require-dokumori-before-raiton", action="store_true")
    parser.add_argument("--require-kassatsu-before-kunai", action="store_true")
    args = parser.parse_args(argv)

    output_payload, summary_payload = filter_samples(
        read_json(args.samples),
        read_json(args.nin_specific),
        args.opener_window,
        args.require_dokumori_before_raiton,
        args.require_kassatsu_before_kunai,
    )
    write_json(args.out, output_payload)
    write_json(args.summary_out, summary_payload)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
