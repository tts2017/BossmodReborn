#!/usr/bin/env python3
"""Extract MNK-specific timing columns from anonymized common job samples."""

from __future__ import annotations

import argparse
import json
import statistics
import sys
from pathlib import Path
from typing import Any

from jobs import normalize_job


ROLE_OUTPUT_KEYS = {
    "riddleOfFire": "riddleOfFireUseTimes",
    "brotherhood": "brotherhoodUseTimes",
    "perfectBalance": "perfectBalanceUseTimes",
    "riddleOfWind": "riddleOfWindUseTimes",
    "potion": "potionUseTimes",
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


def closest_before_delta(anchor: list[float], candidates: list[float]) -> float | None:
    if not anchor:
        return None
    first_anchor = anchor[0]
    before = [time for time in candidates if time < first_anchor]
    if not before:
        return None
    return first_anchor - max(before)


def last_from_fight_end(kill_time: float | None, times: list[float]) -> float | None:
    if kill_time is None or not times:
        return None
    return kill_time - times[-1]


def summarize_samples(samples: list[dict[str, Any]]) -> dict[str, float]:
    first_rof: list[float] = []
    first_brotherhood: list[float] = []
    first_pb: list[float] = []
    first_row: list[float] = []
    first_potion: list[float] = []
    rof_brotherhood_delta: list[float] = []
    pb_before_rof_delta: list[float] = []
    row_before_rof_delta: list[float] = []
    last_rof_from_end: list[float] = []
    last_brotherhood_from_end: list[float] = []
    last_potion_from_end: list[float] = []
    last_row_from_end: list[float] = []

    for sample in samples:
        kill_time = as_number(sample.get("killTime"))
        rof = as_number_list(sample.get("riddleOfFireUseTimes"))
        brotherhood = as_number_list(sample.get("brotherhoodUseTimes"))
        pb = as_number_list(sample.get("perfectBalanceUseTimes"))
        row = as_number_list(sample.get("riddleOfWindUseTimes"))
        potion = as_number_list(sample.get("potionUseTimes"))

        if rof:
            first_rof.append(rof[0])
        if brotherhood:
            first_brotherhood.append(brotherhood[0])
        if pb:
            first_pb.append(pb[0])
        if row:
            first_row.append(row[0])
        if potion:
            first_potion.append(potion[0])
        if rof and brotherhood:
            rof_brotherhood_delta.append(brotherhood[0] - rof[0])

        if (delta := closest_before_delta(rof, pb)) is not None:
            pb_before_rof_delta.append(delta)
        if (delta := closest_before_delta(rof, row)) is not None:
            row_before_rof_delta.append(delta)
        if (delta := last_from_fight_end(kill_time, rof)) is not None:
            last_rof_from_end.append(delta)
        if (delta := last_from_fight_end(kill_time, brotherhood)) is not None:
            last_brotherhood_from_end.append(delta)
        if (delta := last_from_fight_end(kill_time, potion)) is not None:
            last_potion_from_end.append(delta)
        if (delta := last_from_fight_end(kill_time, row)) is not None:
            last_row_from_end.append(delta)

    return {
        "riddleOfFireFirstUseMedian": median(first_rof),
        "brotherhoodFirstUseMedian": median(first_brotherhood),
        "perfectBalanceFirstUseMedian": median(first_pb),
        "riddleOfWindFirstUseMedian": median(first_row),
        "potionFirstUseMedian": median(first_potion),
        "rofBrotherhoodDeltaMedian": median(rof_brotherhood_delta),
        "pbBeforeRoFDeltaMedian": median(pb_before_rof_delta),
        "rowBeforeRoFDeltaMedian": median(row_before_rof_delta),
        "lastRoFFromFightEndMedian": median(last_rof_from_end),
        "lastBrotherhoodFromFightEndMedian": median(last_brotherhood_from_end),
        "lastPotionFromFightEndMedian": median(last_potion_from_end),
        "lastRoWFromFightEndMedian": median(last_row_from_end),
    }


def extract_mnk_specific_features(
    samples_payload: dict[str, Any],
    ability_map: dict[str, Any],
    ability_map_path: str,
) -> dict[str, Any]:
    normalized_job = normalize_job(str(samples_payload.get("job", "MNK")))
    if normalized_job != "MNK":
        raise RuntimeError("MNK-specific feature extraction requires MNK samples")

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
        "job": "MNK",
        "sampleCount": len(output_samples),
        "abilityMapSource": Path(ability_map_path).name,
        "summary": summarize_samples(output_samples),
        "samples": output_samples,
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Extract MNK-specific timing columns from common job samples.")
    parser.add_argument("--samples", required=True)
    parser.add_argument("--ability-map", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    payload = extract_mnk_specific_features(
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
