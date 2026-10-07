#!/usr/bin/env python3
"""Summarize characterRankings probe diagnostics without exposing private values."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any

from fflogs_client import summarize_graphql_errors


RANKINGS_LIKE_KEYS = {
    "rankings",
    "entries",
    "data",
    "results",
}

PAGINATION_LIKE_KEYS = {
    "page",
    "hasMorePages",
    "total",
    "count",
    "pagination",
}


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("character rankings probe input must be a JSON object")
    return payload


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def object_keys(value: Any) -> list[str]:
    if not isinstance(value, dict):
        return []
    return sorted(str(key) for key in value.keys())


def summarize_character_rankings_probe(response: dict[str, Any], probe: dict[str, Any]) -> dict[str, Any]:
    data = response.get("data")
    world_data = data.get("worldData") if isinstance(data, dict) else None
    encounter = world_data.get("encounter") if isinstance(world_data, dict) else None
    character_rankings = encounter.get("characterRankings") if isinstance(encounter, dict) else None
    character_rankings_keys = object_keys(character_rankings)

    error = None
    if isinstance(character_rankings, dict):
        ranking_error = character_rankings.get("error")
        if isinstance(ranking_error, str):
            error = ranking_error

    summary: dict[str, Any] = {
        "probe": {
            "encounterId": probe.get("encounterId"),
            "difficulty": probe.get("difficulty"),
            "partition": probe.get("partition"),
            "metric": probe.get("metric"),
            "job": probe.get("job"),
            "className": probe.get("className"),
            "specName": probe.get("specName"),
            "page": probe.get("page"),
            "size": probe.get("size"),
        },
        "encounterPresent": isinstance(encounter, dict),
        "characterRankingsPresent": character_rankings is not None,
        "characterRankingsKeys": character_rankings_keys,
        "error": error,
        "rankingsLikeKeys": [key for key in character_rankings_keys if key in RANKINGS_LIKE_KEYS],
        "paginationLikeKeys": [key for key in character_rankings_keys if key in PAGINATION_LIKE_KEYS],
    }

    if response.get("errors"):
        summary["graphqlErrors"] = summarize_graphql_errors(response.get("errors"))

    return summary


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Summarize characterRankings probe diagnostics.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--encounter-id", type=int, required=True)
    parser.add_argument("--difficulty", type=int, required=True)
    parser.add_argument("--partition", type=int, required=True)
    parser.add_argument("--metric", required=True)
    parser.add_argument("--job", required=True)
    parser.add_argument("--page", type=int, required=True)
    parser.add_argument("--size", type=int, required=True)
    parser.add_argument("--class-name", required=True)
    parser.add_argument("--spec-name")
    args = parser.parse_args(argv)

    probe = {
        "encounterId": args.encounter_id,
        "difficulty": args.difficulty,
        "partition": args.partition,
        "metric": args.metric,
        "job": args.job,
        "className": args.class_name,
        "specName": args.spec_name,
        "page": args.page,
        "size": args.size,
    }
    summary = summarize_character_rankings_probe(read_json(args.input_path), probe)
    write_json(args.out, summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
