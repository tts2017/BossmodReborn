#!/usr/bin/env python3
"""Analyze BLM-specific timing distribution statistics."""

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
    "leyLinesManafontDelta",
    "leyLinesAmplifierDelta",
    "leyLinesTriplecastDelta",
    "manafontDespairDelta",
    "fireIVDespairDelta",
    "despairFlareStarDelta",
    "leyLinesFirstUseTime",
    "manafontFirstUseTime",
    "amplifierFirstUseTime",
    "triplecastFirstUseTime",
    "swiftcastFirstUseTime",
    "xenoglossyFirstUseTime",
    "foulFirstUseTime",
    "fireIVFirstUseTime",
    "despairFirstUseTime",
    "flareStarFirstUseTime",
    "highThunderFirstUseTime",
    "lastLeyLinesFromFightEnd",
    "lastManafontFromFightEnd",
    "lastAmplifierFromFightEnd",
    "lastTriplecastFromFightEnd",
    "lastXenoglossyFromFightEnd",
    "lastDespairFromFightEnd",
    "lastFlareStarFromFightEnd",
)


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("BLM timing input must be a JSON object")
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


def append_first_after_delta(values: list[float], anchor_role: list[float], later_role: list[float]) -> None:
    if not anchor_role:
        return
    first_anchor = anchor_role[0]
    later = [time for time in later_role if time >= first_anchor]
    if later:
        values.append(later[0] - first_anchor)


def append_last_from_end(values: list[float], kill_time: float | None, times: list[float]) -> None:
    if kill_time is not None and times:
        values.append(kill_time - times[-1])


def analyze_distributions(payload: dict[str, Any]) -> dict[str, Any]:
    samples = payload.get("samples")
    sample_list = [sample for sample in samples if isinstance(sample, dict)] if isinstance(samples, list) else []

    values: dict[str, list[float]] = {key: [] for key in DISTRIBUTION_ORDER}
    for sample in sample_list:
        kill_time = as_number(sample.get("killTime"))
        ley_lines = as_number_list(sample.get("leyLinesUseTimes"))
        manafont = as_number_list(sample.get("manafontUseTimes"))
        amplifier = as_number_list(sample.get("amplifierUseTimes"))
        triplecast = as_number_list(sample.get("triplecastUseTimes"))
        swiftcast = as_number_list(sample.get("swiftcastUseTimes"))
        xenoglossy = as_number_list(sample.get("xenoglossyUseTimes"))
        foul = as_number_list(sample.get("foulUseTimes"))
        fire_iv = as_number_list(sample.get("fireIVUseTimes"))
        despair = as_number_list(sample.get("despairUseTimes"))
        flare_star = as_number_list(sample.get("flareStarUseTimes"))
        high_thunder = as_number_list(sample.get("highThunderUseTimes"))

        append_delta(values["leyLinesManafontDelta"], ley_lines, manafont)
        append_delta(values["leyLinesAmplifierDelta"], ley_lines, amplifier)
        append_delta(values["leyLinesTriplecastDelta"], ley_lines, triplecast)
        append_first_after_delta(values["manafontDespairDelta"], manafont, despair)
        append_first_after_delta(values["fireIVDespairDelta"], fire_iv, despair)
        append_first_after_delta(values["despairFlareStarDelta"], despair, flare_star)

        append_first(values["leyLinesFirstUseTime"], ley_lines)
        append_first(values["manafontFirstUseTime"], manafont)
        append_first(values["amplifierFirstUseTime"], amplifier)
        append_first(values["triplecastFirstUseTime"], triplecast)
        append_first(values["swiftcastFirstUseTime"], swiftcast)
        append_first(values["xenoglossyFirstUseTime"], xenoglossy)
        append_first(values["foulFirstUseTime"], foul)
        append_first(values["fireIVFirstUseTime"], fire_iv)
        append_first(values["despairFirstUseTime"], despair)
        append_first(values["flareStarFirstUseTime"], flare_star)
        append_first(values["highThunderFirstUseTime"], high_thunder)

        append_last_from_end(values["lastLeyLinesFromFightEnd"], kill_time, ley_lines)
        append_last_from_end(values["lastManafontFromFightEnd"], kill_time, manafont)
        append_last_from_end(values["lastAmplifierFromFightEnd"], kill_time, amplifier)
        append_last_from_end(values["lastTriplecastFromFightEnd"], kill_time, triplecast)
        append_last_from_end(values["lastXenoglossyFromFightEnd"], kill_time, xenoglossy)
        append_last_from_end(values["lastDespairFromFightEnd"], kill_time, despair)
        append_last_from_end(values["lastFlareStarFromFightEnd"], kill_time, flare_star)

    return {
        "job": payload.get("job", "BLM"),
        "sampleCount": len(sample_list),
        "distributions": {key: distribution(values[key]) for key in DISTRIBUTION_ORDER},
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Analyze BLM-specific timing distributions.")
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
