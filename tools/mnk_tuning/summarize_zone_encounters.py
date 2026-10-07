#!/usr/bin/env python3
"""Summarize public FF Logs zone and encounter metadata."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("zone encounter input must be a JSON object")
    return payload


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def summarize_zone_encounters(response: dict[str, Any]) -> dict[str, list[dict[str, Any]]]:
    if response.get("errors"):
        raise RuntimeError("zone encounter response contained GraphQL errors")

    data = response.get("data")
    if not isinstance(data, dict):
        raise RuntimeError("zone encounter response must contain a JSON object at data")

    world_data = data.get("worldData")
    if not isinstance(world_data, dict):
        raise RuntimeError("zone encounter response must contain data.worldData")

    zones = world_data.get("zones")
    if not isinstance(zones, list):
        raise RuntimeError("zone encounter response must contain data.worldData.zones")

    summarized_zones: list[dict[str, Any]] = []
    for zone in zones:
        if not isinstance(zone, dict):
            continue

        encounters = zone.get("encounters")
        summarized_encounters: list[dict[str, Any]] = []
        if isinstance(encounters, list):
            for encounter in encounters:
                if not isinstance(encounter, dict):
                    continue
                summarized_encounters.append({
                    "id": encounter.get("id"),
                    "name": encounter.get("name"),
                })

        summarized_zones.append({
            "id": zone.get("id"),
            "name": zone.get("name"),
            "encounters": summarized_encounters,
        })

    return {"zones": summarized_zones}


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Summarize FF Logs zones and encounters.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_json(args.out, summarize_zone_encounters(read_json(args.input_path)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
