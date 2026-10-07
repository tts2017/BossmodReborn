#!/usr/bin/env python3
"""Analyze MNK-specific timing distribution statistics."""

from __future__ import annotations

import argparse
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

DISTRIBUTION_ORDER = (
    "rofBrotherhoodDelta",
    "pbBeforeRoFDelta",
    "rowBeforeRoFDelta",
    "lastRoFFromFightEnd",
    "lastBrotherhoodFromFightEnd",
    "lastPotionFromFightEnd",
    "lastRoWFromFightEnd",
    "firstRiddleOfFireUseTime",
    "firstBrotherhoodUseTime",
    "firstPerfectBalanceUseTime",
    "firstRiddleOfWindUseTime",
    "firstPotionUseTime",
)


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("MNK timing input must be a JSON object")
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


def distribution(values: list[float]) -> dict[str, Any]:
    sorted_values = sorted(values)
    if not sorted_values:
        return {
            "sampleCount": 0,
            "min": None,
            "p10": None,
            "p25": None,
            "median": None,
            "p75": None,
            "p90": None,
            "max": None,
        }

    return {
        "sampleCount": len(sorted_values),
        "min": sorted_values[0],
        "p10": nearest_rank(sorted_values, 10),
        "p25": nearest_rank(sorted_values, 25),
        "median": nearest_rank(sorted_values, 50),
        "p75": nearest_rank(sorted_values, 75),
        "p90": nearest_rank(sorted_values, 90),
        "max": sorted_values[-1],
    }


def closest_before_delta(anchor: list[float], candidates: list[float]) -> float | None:
    if not anchor:
        return None
    first_anchor = anchor[0]
    before = [time for time in candidates if time < first_anchor]
    if not before:
        return None
    return first_anchor - max(before)


def append_if_number(values: list[float], value: float | None) -> None:
    if value is not None:
        values.append(value)


def analyze_distributions(payload: dict[str, Any]) -> dict[str, Any]:
    samples = payload.get("samples")
    sample_list = [sample for sample in samples if isinstance(sample, dict)] if isinstance(samples, list) else []

    values: dict[str, list[float]] = {key: [] for key in DISTRIBUTION_ORDER}
    for sample in sample_list:
        kill_time = as_number(sample.get("killTime"))
        rof = as_number_list(sample.get("riddleOfFireUseTimes"))
        brotherhood = as_number_list(sample.get("brotherhoodUseTimes"))
        pb = as_number_list(sample.get("perfectBalanceUseTimes"))
        row = as_number_list(sample.get("riddleOfWindUseTimes"))
        potion = as_number_list(sample.get("potionUseTimes"))

        if rof:
            values["firstRiddleOfFireUseTime"].append(rof[0])
        if brotherhood:
            values["firstBrotherhoodUseTime"].append(brotherhood[0])
        if pb:
            values["firstPerfectBalanceUseTime"].append(pb[0])
        if row:
            values["firstRiddleOfWindUseTime"].append(row[0])
        if potion:
            values["firstPotionUseTime"].append(potion[0])
        if rof and brotherhood:
            values["rofBrotherhoodDelta"].append(brotherhood[0] - rof[0])

        append_if_number(values["pbBeforeRoFDelta"], closest_before_delta(rof, pb))
        append_if_number(values["rowBeforeRoFDelta"], closest_before_delta(rof, row))

        if kill_time is not None:
            if rof:
                values["lastRoFFromFightEnd"].append(kill_time - rof[-1])
            if brotherhood:
                values["lastBrotherhoodFromFightEnd"].append(kill_time - brotherhood[-1])
            if potion:
                values["lastPotionFromFightEnd"].append(kill_time - potion[-1])
            if row:
                values["lastRoWFromFightEnd"].append(kill_time - row[-1])

    return {
        "job": payload.get("job", "MNK"),
        "sampleCount": len(sample_list),
        "distributions": {key: distribution(values[key]) for key in DISTRIBUTION_ORDER},
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Analyze MNK-specific timing distributions.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_json(args.out, analyze_distributions(read_json(args.input_path)))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
