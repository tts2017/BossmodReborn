#!/usr/bin/env python3
"""Summarize abilityGameID usage from common job sample batches."""

from __future__ import annotations

import argparse
import json
import statistics
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


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("ability usage input must be a JSON object")
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


def median(values: list[float]) -> float:
    return float(statistics.median(values)) if values else 0.0


def min_or_zero(values: list[float]) -> float:
    return min(values) if values else 0.0


def max_or_zero(values: list[float]) -> float:
    return max(values) if values else 0.0


def ability_times(value: Any) -> list[float]:
    if not isinstance(value, list):
        return []
    result: list[float] = []
    for item in value:
        number = as_number(item)
        if number is not None:
            result.append(number)
    return result


def summarize_ability_usage(payload: dict[str, Any]) -> dict[str, Any]:
    samples = payload.get("samples")
    sample_list = [sample for sample in samples if isinstance(sample, dict)] if isinstance(samples, list) else []
    ability_stats: dict[int, dict[str, list[float]]] = {}

    for sample in sample_list:
        ability_use_times = sample.get("abilityUseTimes")
        if not isinstance(ability_use_times, dict):
            continue

        for ability_key, raw_times in ability_use_times.items():
            try:
                ability_id = int(ability_key)
            except ValueError:
                continue

            times = ability_times(raw_times)
            if not times:
                continue

            stats = ability_stats.setdefault(ability_id, {
                "firstUses": [],
                "usesPerSample": [],
            })
            stats["firstUses"].append(min(times))
            stats["usesPerSample"].append(float(len(times)))

    abilities: list[dict[str, Any]] = []
    for ability_id, stats in ability_stats.items():
        first_uses = stats["firstUses"]
        uses_per_sample = stats["usesPerSample"]
        abilities.append({
            "abilityGameID": ability_id,
            "sampleCount": len(uses_per_sample),
            "totalUses": int(sum(uses_per_sample)),
            "firstUseMedian": median(first_uses),
            "firstUseMin": min_or_zero(first_uses),
            "firstUseMax": max_or_zero(first_uses),
            "usesPerSampleMedian": median(uses_per_sample),
            "usesPerSampleMin": int(min_or_zero(uses_per_sample)),
            "usesPerSampleMax": int(max_or_zero(uses_per_sample)),
        })

    abilities.sort(key=lambda item: (-item["sampleCount"], -item["totalUses"], item["abilityGameID"]))

    return {
        "job": payload.get("job"),
        "sampleCount": len(sample_list),
        "abilities": abilities,
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Summarize abilityGameID usage from common job samples.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_json(args.out, summarize_ability_usage(read_json(args.input_path)))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
