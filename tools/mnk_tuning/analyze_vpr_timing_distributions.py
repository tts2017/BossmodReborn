#!/usr/bin/env python3
"""Analyze VPR-specific timing distribution statistics."""

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
    "serpentsIreReawakenDelta",
    "vicewinderReawakenDelta",
    "reawakenFirstGenerationDelta",
    "reawakenFirstLegacyDelta",
    "reawakenOuroborosDelta",
    "firstGenerationOuroborosDelta",
    "serpentsIreFirstUseTime",
    "vicewinderFirstUseTime",
    "reawakenFirstUseTime",
    "firstGenerationFirstUseTime",
    "firstLegacyFirstUseTime",
    "ouroborosFirstUseTime",
    "uncoiledFuryFirstUseTime",
    "lastReawakenFromFightEnd",
    "lastSerpentsIreFromFightEnd",
    "lastVicewinderFromFightEnd",
    "lastOuroborosFromFightEnd",
    "lastUncoiledFuryFromFightEnd",
)


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("VPR timing input must be a JSON object")
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


def append_first(values: list[float], times: list[float]) -> None:
    if times:
        values.append(times[0])


def append_delta(values: list[float], first_role: list[float], second_role: list[float]) -> None:
    if first_role and second_role:
        values.append(second_role[0] - first_role[0])


def append_last_from_end(values: list[float], kill_time: float | None, times: list[float]) -> None:
    if kill_time is not None and times:
        values.append(kill_time - times[-1])


def analyze_distributions(payload: dict[str, Any]) -> dict[str, Any]:
    samples = payload.get("samples")
    sample_list = [sample for sample in samples if isinstance(sample, dict)] if isinstance(samples, list) else []

    values: dict[str, list[float]] = {key: [] for key in DISTRIBUTION_ORDER}
    for sample in sample_list:
        kill_time = as_number(sample.get("killTime"))
        serpents_ire = as_number_list(sample.get("serpentsIreUseTimes"))
        vicewinder = as_number_list(sample.get("vicewinderUseTimes"))
        reawaken = as_number_list(sample.get("reawakenUseTimes"))
        first_generation = as_number_list(sample.get("firstGenerationUseTimes"))
        first_legacy = as_number_list(sample.get("firstLegacyUseTimes"))
        ouroboros = as_number_list(sample.get("ouroborosUseTimes"))
        uncoiled_fury = as_number_list(sample.get("uncoiledFuryUseTimes"))

        append_delta(values["serpentsIreReawakenDelta"], serpents_ire, reawaken)
        append_delta(values["vicewinderReawakenDelta"], vicewinder, reawaken)
        append_delta(values["reawakenFirstGenerationDelta"], reawaken, first_generation)
        append_delta(values["reawakenFirstLegacyDelta"], reawaken, first_legacy)
        append_delta(values["reawakenOuroborosDelta"], reawaken, ouroboros)
        append_delta(values["firstGenerationOuroborosDelta"], first_generation, ouroboros)

        append_first(values["serpentsIreFirstUseTime"], serpents_ire)
        append_first(values["vicewinderFirstUseTime"], vicewinder)
        append_first(values["reawakenFirstUseTime"], reawaken)
        append_first(values["firstGenerationFirstUseTime"], first_generation)
        append_first(values["firstLegacyFirstUseTime"], first_legacy)
        append_first(values["ouroborosFirstUseTime"], ouroboros)
        append_first(values["uncoiledFuryFirstUseTime"], uncoiled_fury)

        append_last_from_end(values["lastReawakenFromFightEnd"], kill_time, reawaken)
        append_last_from_end(values["lastSerpentsIreFromFightEnd"], kill_time, serpents_ire)
        append_last_from_end(values["lastVicewinderFromFightEnd"], kill_time, vicewinder)
        append_last_from_end(values["lastOuroborosFromFightEnd"], kill_time, ouroboros)
        append_last_from_end(values["lastUncoiledFuryFromFightEnd"], kill_time, uncoiled_fury)

    return {
        "job": payload.get("job", "VPR"),
        "sampleCount": len(sample_list),
        "distributions": {key: distribution(values[key]) for key in DISTRIBUTION_ORDER},
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Analyze VPR-specific timing distributions.")
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
