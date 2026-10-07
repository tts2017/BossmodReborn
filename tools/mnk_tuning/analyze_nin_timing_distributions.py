#!/usr/bin/env python3
"""Analyze NIN-specific timing distribution statistics."""

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
    "dokumoriKunaiDelta",
    "kassatsuKunaiDelta",
    "kunaiHyoshoDelta",
    "dokumoriTcjDelta",
    "tcjMeisuiDelta",
    "dokumoriFirstUseTime",
    "kunaisBaneFirstUseTime",
    "kassatsuFirstUseTime",
    "tenChiJinFirstUseTime",
    "meisuiFirstUseTime",
    "bunshinFirstUseTime",
    "phantomKamaitachiFirstUseTime",
    "raitonFirstUseTime",
    "hyoshoRanryuFirstUseTime",
    "lastDokumoriFromFightEnd",
    "lastKunaiFromFightEnd",
    "lastKassatsuFromFightEnd",
    "lastTenChiJinFromFightEnd",
    "lastMeisuiFromFightEnd",
)


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("NIN timing input must be a JSON object")
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
        dokumori = as_number_list(sample.get("dokumoriUseTimes"))
        kunai = as_number_list(sample.get("kunaisBaneUseTimes"))
        kassatsu = as_number_list(sample.get("kassatsuUseTimes"))
        tcj = as_number_list(sample.get("tenChiJinUseTimes"))
        meisui = as_number_list(sample.get("meisuiUseTimes"))
        bunshin = as_number_list(sample.get("bunshinUseTimes"))
        phantom = as_number_list(sample.get("phantomKamaitachiUseTimes"))
        raiton = as_number_list(sample.get("raitonUseTimes"))
        hyosho = as_number_list(sample.get("hyoshoRanryuUseTimes"))

        append_delta(values["dokumoriKunaiDelta"], dokumori, kunai)
        append_delta(values["kassatsuKunaiDelta"], kassatsu, kunai)
        append_delta(values["kunaiHyoshoDelta"], kunai, hyosho)
        append_delta(values["dokumoriTcjDelta"], dokumori, tcj)
        append_delta(values["tcjMeisuiDelta"], tcj, meisui)

        append_first(values["dokumoriFirstUseTime"], dokumori)
        append_first(values["kunaisBaneFirstUseTime"], kunai)
        append_first(values["kassatsuFirstUseTime"], kassatsu)
        append_first(values["tenChiJinFirstUseTime"], tcj)
        append_first(values["meisuiFirstUseTime"], meisui)
        append_first(values["bunshinFirstUseTime"], bunshin)
        append_first(values["phantomKamaitachiFirstUseTime"], phantom)
        append_first(values["raitonFirstUseTime"], raiton)
        append_first(values["hyoshoRanryuFirstUseTime"], hyosho)

        append_last_from_end(values["lastDokumoriFromFightEnd"], kill_time, dokumori)
        append_last_from_end(values["lastKunaiFromFightEnd"], kill_time, kunai)
        append_last_from_end(values["lastKassatsuFromFightEnd"], kill_time, kassatsu)
        append_last_from_end(values["lastTenChiJinFromFightEnd"], kill_time, tcj)
        append_last_from_end(values["lastMeisuiFromFightEnd"], kill_time, meisui)

    return {
        "job": payload.get("job", "NIN"),
        "sampleCount": len(sample_list),
        "distributions": {key: distribution(values[key]) for key in DISTRIBUTION_ORDER},
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Analyze NIN-specific timing distributions.")
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
