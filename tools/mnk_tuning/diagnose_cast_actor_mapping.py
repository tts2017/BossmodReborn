#!/usr/bin/env python3
"""Diagnose actor/sourceID mapping for one Casts event cache."""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter
from typing import Any

from jobs import fflogs_actor_subtypes, normalize_job
from public_data_cache import validate_cache_payload


MAX_CANDIDATE_SOURCE_IDS = 20


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


def int_or_none(value: Any) -> int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    return None


def actor_summary(actor: dict[str, Any]) -> dict[str, Any]:
    return {
        "id": int_or_none(actor.get("id")),
        "gameID": int_or_none(actor.get("gameID")),
        "type": actor.get("type") if isinstance(actor.get("type"), str) else None,
        "subType": actor.get("subType") if isinstance(actor.get("subType"), str) else None,
    }


def read_actors(payload: dict[str, Any]) -> list[dict[str, Any]]:
    master_data = payload.get("masterData")
    if not isinstance(master_data, dict):
        raise RuntimeError("casts input must contain masterData")

    actors = master_data.get("actors")
    if not isinstance(actors, list):
        raise RuntimeError("casts input must contain masterData.actors")

    return [actor for actor in actors if isinstance(actor, dict)]


def read_events(payload: dict[str, Any]) -> list[dict[str, Any]]:
    events = payload.get("events")
    if not isinstance(events, list):
        raise RuntimeError("casts input must contain events")
    return [event for event in events if isinstance(event, dict)]


def unique_actor_by_game_id(actors: list[dict[str, Any]]) -> dict[int, dict[str, Any]]:
    grouped: dict[int, list[dict[str, Any]]] = {}
    for actor in actors:
        game_id = int_or_none(actor.get("gameID"))
        if game_id is None:
            continue
        grouped.setdefault(game_id, []).append(actor)
    return {game_id: matches[0] for game_id, matches in grouped.items() if len(matches) == 1}


def count_event_source_ids(events: list[dict[str, Any]]) -> Counter[int]:
    counts: Counter[int] = Counter()
    for event in events:
        source_id = int_or_none(event.get("sourceID"))
        if source_id is not None:
            counts[source_id] += 1
    return counts


def build_diagnostic_payload(payload: dict[str, Any], job: str) -> dict[str, Any]:
    normalized_job = normalize_job(job)
    actor_subtypes = fflogs_actor_subtypes(normalized_job)
    actors = read_actors(payload)
    events = read_events(payload)

    player_actors = [actor for actor in actors if actor.get("type") == "Player"]
    job_actors = [
        actor
        for actor in player_actors
        if actor.get("subType") in actor_subtypes
    ]
    actor_by_id = {
        actor_id: actor
        for actor in actors
        if (actor_id := int_or_none(actor.get("id"))) is not None
    }
    unique_by_game_id = unique_actor_by_game_id(actors)
    event_source_counts = count_event_source_ids(events)

    matched_actor_id_count = sum(
        count for source_id, count in event_source_counts.items()
        if source_id in actor_by_id
    )
    matched_actor_game_id_count = sum(
        count for source_id, count in event_source_counts.items()
        if source_id in unique_by_game_id
    )

    job_actor_id_counts: dict[str, int] = {}
    job_actor_game_id_counts: dict[str, int] = {}
    for actor in job_actors:
        actor_id = int_or_none(actor.get("id"))
        game_id = int_or_none(actor.get("gameID"))
        if actor_id is not None:
            job_actor_id_counts[str(actor_id)] = event_source_counts.get(actor_id, 0)
        if game_id is not None:
            job_actor_game_id_counts[str(game_id)] = event_source_counts.get(game_id, 0)

    candidates: list[dict[str, Any]] = []
    for source_id, count in event_source_counts.most_common(MAX_CANDIDATE_SOURCE_IDS):
        actor_id_match = actor_by_id.get(source_id)
        game_id_match = unique_by_game_id.get(source_id)
        candidates.append({
            "sourceID": source_id,
            "eventCount": count,
            "actorByID": actor_summary(actor_id_match) if actor_id_match is not None else None,
            "actorByGameID": actor_summary(game_id_match) if game_id_match is not None else None,
        })

    return {
        "job": normalized_job,
        "fflogsActorSubTypes": list(actor_subtypes),
        "jobActors": [actor_summary(actor) for actor in job_actors],
        "eventSourceIDs": {
            str(source_id): event_source_counts[source_id]
            for source_id in sorted(event_source_counts)
        },
        "eventSourceIDMatchedActorIDCount": matched_actor_id_count,
        "eventSourceIDMatchedActorGameIDCount": matched_actor_game_id_count,
        "jobActorIDEventCount": job_actor_id_counts,
        "jobActorGameIDEventCount": job_actor_game_id_counts,
        "playerActorsByID": [
            actor_summary(actor)
            for actor in sorted(
                player_actors,
                key=lambda actor: int_or_none(actor.get("id")) if int_or_none(actor.get("id")) is not None else -1,
            )
        ],
        "candidateEventSourceActors": candidates,
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Diagnose Casts event sourceID to actor mapping.")
    parser.add_argument("--job", required=True)
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    diagnostic = build_diagnostic_payload(read_json(args.input_path), args.job)
    write_json(args.out, diagnostic)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
