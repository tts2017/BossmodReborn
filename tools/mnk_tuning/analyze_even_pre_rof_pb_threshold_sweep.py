#!/usr/bin/env python3
"""Compare EvenPreRoFPBStartThreshold candidates against MNK timing samples."""

from __future__ import annotations

import argparse
import json
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
        raise RuntimeError("threshold sweep input must be a JSON object")
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


def parse_thresholds(value: str) -> list[float]:
    thresholds: set[float] = set()
    for item in value.split(","):
        item = item.strip()
        if not item:
            continue
        thresholds.add(float(item))
    if not thresholds:
        raise RuntimeError("at least one threshold is required")
    return sorted(thresholds)


def nearest_pre_rof_pb_delta(sample: dict[str, Any]) -> float | None:
    rof_times = as_number_list(sample.get("riddleOfFireUseTimes"))
    pb_times = as_number_list(sample.get("perfectBalanceUseTimes"))
    if not rof_times:
        return None

    first_rof = rof_times[0]
    pre_rof_pbs = [time for time in pb_times if time < first_rof]
    if not pre_rof_pbs:
        return None

    return first_rof - max(pre_rof_pbs)


def threshold_label(value: float) -> str:
    text = f"{value:.1f}"
    return text.replace(".", "_")


def recommendation(rows: list[dict[str, Any]], current: float) -> tuple[str, str]:
    current_row = next((row for row in rows if row["threshold"] == current), None)
    if current_row is None:
        return (
            "hold",
            "Current threshold was not included in the sweep, so compare the generated rows manually before changing MNK.cs.",
        )

    changed_rows = [row for row in rows if row["additionalBlockedVsCurrent"] > 0]
    if not changed_rows:
        return (
            "safe_candidate",
            "All swept thresholds allow the same samples as the current threshold in this data set.",
        )

    first_changed = changed_rows[0]
    if first_changed["additionalBlockedVsCurrentPercent"] <= 5.0:
        threshold = threshold_label(first_changed["threshold"])
        return (
            f"hold_or_test_{threshold}",
            f"{first_changed['threshold']:.1f} affects only {first_changed['additionalBlockedVsCurrent']} current-allowed sample, but should be combat-tested before changing MNK.cs.",
        )

    return (
        "hold",
        "At least one candidate blocks more than 5% of samples that the current threshold allows.",
    )


def analyze_sweep(payload: dict[str, Any], thresholds: list[float], current: float) -> dict[str, Any]:
    samples = payload.get("samples")
    sample_list = [sample for sample in samples if isinstance(sample, dict)] if isinstance(samples, list) else []
    deltas = [delta for sample in sample_list if (delta := nearest_pre_rof_pb_delta(sample)) is not None]
    with_rof_and_pre_pb = len(deltas)
    allowed_by_current = sum(1 for delta in deltas if delta <= current)

    rows: list[dict[str, Any]] = []
    for threshold in thresholds:
        allowed = sum(1 for delta in deltas if delta <= threshold)
        blocked = with_rof_and_pre_pb - allowed
        additional_blocked = allowed_by_current - allowed
        rows.append({
            "threshold": threshold,
            "allowed": allowed,
            "blocked": blocked,
            "blockedPercent": blocked / with_rof_and_pre_pb * 100.0 if with_rof_and_pre_pb else 0.0,
            "additionalBlockedVsCurrent": additional_blocked,
            "additionalBlockedVsCurrentPercent": additional_blocked / with_rof_and_pre_pb * 100.0 if with_rof_and_pre_pb else 0.0,
        })

    result, reason = recommendation(rows, current)
    return {
        "job": payload.get("job", "MNK"),
        "sampleCount": len(sample_list),
        "withRoFAndPreRoFPB": with_rof_and_pre_pb,
        "thresholds": rows,
        "currentThreshold": current,
        "recommendation": result,
        "reason": reason,
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Sweep EvenPreRoFPBStartThreshold candidates.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--thresholds", required=True)
    parser.add_argument("--current", type=float, default=6.0)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_json(args.out, analyze_sweep(read_json(args.input_path), parse_thresholds(args.thresholds), args.current))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
