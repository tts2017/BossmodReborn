#!/usr/bin/env python3
"""Extract anonymized cast timing features for one job from one fight cache."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any

from jobs import fflogs_actor_subtypes, fflogs_job_name, normalize_job
from public_data_cache import validate_cache_payload


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("casts input must be a JSON object")
    return payload


def write_json(path: str, payload: dict[str, Any]) -> None:
    validate_cache_payload(payload)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def number_or_none(value: Any) -> float | int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return value
    return None


def find_job_actor_ids(payload: dict[str, Any], actor_subtypes: tuple[str, ...]) -> list[int]:
    master_data = payload.get("masterData")
    if not isinstance(master_data, dict):
        raise RuntimeError("casts input must contain masterData")

    actors = master_data.get("actors")
    if not isinstance(actors, list):
        raise RuntimeError("casts input must contain masterData.actors")

    actor_ids: list[int] = []
    for actor in actors:
        if not isinstance(actor, dict):
            continue
        if actor.get("type") != "Player":
            continue
        if actor.get("subType") not in actor_subtypes:
            continue
        actor_id = actor.get("id")
        if isinstance(actor_id, int) and not isinstance(actor_id, bool):
            actor_ids.append(actor_id)

    if not actor_ids:
        raise RuntimeError(f"no player actors found for actor subTypes: {', '.join(actor_subtypes)}")

    return actor_ids


def extract_cast_events(payload: dict[str, Any], actor_ids: list[int]) -> list[dict[str, Any]]:
    events = payload.get("events")
    if not isinstance(events, list):
        raise RuntimeError("casts input must contain events")

    actor_id_set = set(actor_ids)
    extracted: list[dict[str, Any]] = []
    for event in events:
        if not isinstance(event, dict):
            continue

        source_id = event.get("sourceID")
        ability_game_id = number_or_none(event.get("abilityGameID"))
        timestamp = number_or_none(event.get("timestamp"))
        if source_id not in actor_id_set:
            continue
        if ability_game_id is None or timestamp is None:
            continue

        extracted.append({
            "timestamp": timestamp,
            "sourceID": source_id,
            "targetID": number_or_none(event.get("targetID")),
            "abilityGameID": ability_game_id,
            "type": event.get("type") if isinstance(event.get("type"), str) else None,
        })

    return extracted


def build_feature_payload(payload: dict[str, Any], job: str) -> dict[str, Any]:
    normalized_job = normalize_job(job)
    fflogs_name = fflogs_job_name(normalized_job)
    actor_subtypes = fflogs_actor_subtypes(normalized_job)
    actor_ids = find_job_actor_ids(payload, actor_subtypes)
    cast_events = extract_cast_events(payload, actor_ids)

    first_timestamp = cast_events[0]["timestamp"] if cast_events else None
    relative_events: list[dict[str, Any]] = []
    ability_use_times: dict[str, list[float]] = {}
    for event in cast_events:
        timestamp = event["timestamp"]
        relative_time = 0.0 if first_timestamp is None else (float(timestamp) - float(first_timestamp)) / 1000.0
        ability_id = str(event["abilityGameID"])
        relative_event = {
            "relativeTime": relative_time,
            "sourceID": event["sourceID"],
            "targetID": event["targetID"],
            "abilityGameID": event["abilityGameID"],
            "type": event["type"],
        }
        relative_events.append(relative_event)
        ability_use_times.setdefault(ability_id, []).append(relative_time)

    return {
        "job": normalized_job,
        "fflogsJobName": fflogs_name,
        "fflogsActorSubTypes": list(actor_subtypes),
        "fightID": payload.get("fightID"),
        "dataType": payload.get("dataType"),
        "jobActorCount": len(actor_ids),
        "jobActorIDs": actor_ids,
        "eventCount": payload.get("eventCount"),
        "castCount": len(relative_events),
        "firstCastTimestampOmitted": True,
        "castEvents": relative_events,
        "abilityUseTimes": ability_use_times,
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Extract anonymized cast timing features for one job.")
    parser.add_argument("--job", required=True)
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    features = build_feature_payload(read_json(args.input_path), args.job)
    write_json(args.out, features)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
