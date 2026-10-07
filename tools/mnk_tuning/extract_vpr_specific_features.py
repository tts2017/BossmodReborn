#!/usr/bin/env python3
"""Extract VPR-specific timing columns from anonymized common job samples."""

from __future__ import annotations

import argparse
import json
import statistics
import sys
from pathlib import Path
from typing import Any

from jobs import normalize_job


ROLE_OUTPUT_KEYS = {
    "reawaken": "reawakenUseTimes",
    "serpentsIre": "serpentsIreUseTimes",
    "vicewinder": "vicewinderUseTimes",
    "vicepit": "vicepitUseTimes",
    "uncoiledFury": "uncoiledFuryUseTimes",
    "uncoiledTwinfang": "uncoiledTwinfangUseTimes",
    "uncoiledTwinblood": "uncoiledTwinbloodUseTimes",
    "ouroboros": "ouroborosUseTimes",
    "firstGeneration": "firstGenerationUseTimes",
    "secondGeneration": "secondGenerationUseTimes",
    "thirdGeneration": "thirdGenerationUseTimes",
    "fourthGeneration": "fourthGenerationUseTimes",
    "firstLegacy": "firstLegacyUseTimes",
    "secondLegacy": "secondLegacyUseTimes",
    "thirdLegacy": "thirdLegacyUseTimes",
    "fourthLegacy": "fourthLegacyUseTimes",
    "dreadwinder": "dreadwinderUseTimes",
    "pitOfDread": "pitOfDreadUseTimes",
    "huntersCoil": "huntersCoilUseTimes",
    "swiftskinsCoil": "swiftskinsCoilUseTimes",
    "deathRattle": "deathRattleUseTimes",
    "lastLash": "lastLashUseTimes",
}

FIRST_USE_SUMMARY_ROLES = (
    "reawaken",
    "serpentsIre",
    "vicewinder",
    "vicepit",
    "uncoiledFury",
    "ouroboros",
    "firstGeneration",
    "firstLegacy",
)

DELTA_SUMMARIES = {
    "reawakenOuroborosDeltaMedian": ("reawaken", "ouroboros"),
    "serpentsIreReawakenDeltaMedian": ("serpentsIre", "reawaken"),
    "vicewinderReawakenDeltaMedian": ("vicewinder", "reawaken"),
    "reawakenFirstGenerationDeltaMedian": ("reawaken", "firstGeneration"),
    "reawakenFirstLegacyDeltaMedian": ("reawaken", "firstLegacy"),
    "firstGenerationOuroborosDeltaMedian": ("firstGeneration", "ouroboros"),
}

LAST_FROM_END_SUMMARY_ROLES = {
    "reawaken": "lastReawakenFromFightEndMedian",
    "serpentsIre": "lastSerpentsIreFromFightEndMedian",
    "vicewinder": "lastVicewinderFromFightEndMedian",
    "ouroboros": "lastOuroborosFromFightEndMedian",
    "uncoiledFury": "lastUncoiledFuryFromFightEndMedian",
}

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


def role_ability_ids(ability_map: dict[str, Any]) -> dict[str, list[str]]:
    abilities = ability_map.get("abilities")
    if not isinstance(abilities, dict):
        raise RuntimeError("ability map must contain an abilities object")

    result: dict[str, list[str]] = {role: [] for role in ROLE_OUTPUT_KEYS}
    for ability_id, metadata in abilities.items():
        if not isinstance(metadata, dict):
            continue
        role = metadata.get("role")
        if isinstance(role, str) and role in result:
            result[role].append(str(ability_id))

    return result


def role_use_times(sample: dict[str, Any], role_ids: dict[str, list[str]]) -> dict[str, list[float]]:
    ability_use_times = sample.get("abilityUseTimes")
    ability_times = ability_use_times if isinstance(ability_use_times, dict) else {}
    result: dict[str, list[float]] = {}

    for role, ability_ids in role_ids.items():
        times: list[float] = []
        for ability_id in ability_ids:
            times.extend(as_number_list(ability_times.get(ability_id)))
        result[role] = sorted(times)

    return result


def first_delta(first_role: list[float], second_role: list[float]) -> float | None:
    if not first_role or not second_role:
        return None
    return second_role[0] - first_role[0]


def last_from_fight_end(kill_time: float | None, times: list[float]) -> float | None:
    if kill_time is None or not times:
        return None
    return kill_time - times[-1]


def first_summary_key(role: str) -> str:
    return f"{role}FirstUseMedian"


def summarize_samples(samples: list[dict[str, Any]]) -> dict[str, float]:
    first_use_values: dict[str, list[float]] = {role: [] for role in FIRST_USE_SUMMARY_ROLES}
    delta_values: dict[str, list[float]] = {key: [] for key in DELTA_SUMMARIES}
    last_from_end_values: dict[str, list[float]] = {key: [] for key in LAST_FROM_END_SUMMARY_ROLES.values()}

    for sample in samples:
        kill_time = as_number(sample.get("killTime"))
        role_times = {
            role: as_number_list(sample.get(output_key))
            for role, output_key in ROLE_OUTPUT_KEYS.items()
        }

        for role in FIRST_USE_SUMMARY_ROLES:
            if role_times[role]:
                first_use_values[role].append(role_times[role][0])

        for summary_key, (first_role_name, second_role_name) in DELTA_SUMMARIES.items():
            if (delta := first_delta(role_times[first_role_name], role_times[second_role_name])) is not None:
                delta_values[summary_key].append(delta)

        for role, summary_key in LAST_FROM_END_SUMMARY_ROLES.items():
            if (delta := last_from_fight_end(kill_time, role_times[role])) is not None:
                last_from_end_values[summary_key].append(delta)

    result: dict[str, float] = {}
    for role, values in first_use_values.items():
        result[first_summary_key(role)] = median(values)
    for summary_key, values in delta_values.items():
        result[summary_key] = median(values)
    for summary_key, values in last_from_end_values.items():
        result[summary_key] = median(values)
    return result


def extract_vpr_specific_features(
    samples_payload: dict[str, Any],
    ability_map: dict[str, Any],
    ability_map_path: str,
) -> dict[str, Any]:
    normalized_job = normalize_job(str(samples_payload.get("job", "VPR")))
    if normalized_job != "VPR":
        raise RuntimeError("VPR-specific feature extraction requires VPR samples")

    samples = samples_payload.get("samples")
    sample_list = [sample for sample in samples if isinstance(sample, dict)] if isinstance(samples, list) else []
    role_ids = role_ability_ids(ability_map)

    output_samples: list[dict[str, Any]] = []
    for sample in sample_list:
        times_by_role = role_use_times(sample, role_ids)
        output_sample: dict[str, Any] = {
            "sourceRank": sample.get("sourceRank") if isinstance(sample.get("sourceRank"), int) else 0,
            "killTime": as_number(sample.get("killTime")) or 0.0,
        }
        for role, output_key in ROLE_OUTPUT_KEYS.items():
            output_sample[output_key] = times_by_role[role]
        output_samples.append(output_sample)

    return {
        "job": "VPR",
        "sampleCount": len(output_samples),
        "abilityMapSource": Path(ability_map_path).name,
        "summary": summarize_samples(output_samples),
        "samples": output_samples,
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Extract VPR-specific timing columns from common job samples.")
    parser.add_argument("--samples", required=True)
    parser.add_argument("--ability-map", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    payload = extract_vpr_specific_features(
        read_json(args.samples),
        read_json(args.ability_map),
        args.ability_map,
    )
    write_json(args.out, payload)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
