#!/usr/bin/env python3
"""Collect public FF Logs MNK ranking inputs for offline tuning.

This script intentionally does not persist raw report URLs, report codes,
character names, or player names in its final output. The encounter-specific
GraphQL query shape is left explicit and unimplemented until the exact FF Logs
schema fields for the desired rankings workflow are finalized.
"""

from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass
from typing import Any

from fflogs_client import fetch_access_token, graphql_request, read_credentials

JOB_NAME = "Monk"


@dataclass(frozen=True)
class CollectionOptions:
    encounter_id: int
    partition: int
    metric: str
    difficulty: int
    limit: int
    out: str


def build_top_mnk_query() -> str:
    raise NotImplementedError("FF Logs top-MNK ranking GraphQL query is not finalized")


def collect_top_mnk_public_logs(options: CollectionOptions, client_id: str, client_secret: str) -> dict[str, Any]:
    access_token = fetch_access_token(client_id, client_secret)
    query = build_top_mnk_query()
    variables = {
        "encounterId": options.encounter_id,
        "partition": options.partition,
        "metric": options.metric,
        "difficulty": options.difficulty,
        "limit": options.limit,
        "job": JOB_NAME,
    }
    return graphql_request(access_token, query, variables)


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def parse_args(argv: list[str]) -> CollectionOptions:
    parser = argparse.ArgumentParser(description="Collect public top-MNK FF Logs inputs for tuning.")
    parser.add_argument("--encounter-id", type=int, required=True)
    parser.add_argument("--partition", type=int, required=True)
    parser.add_argument("--metric", required=True)
    parser.add_argument("--difficulty", type=int, required=True)
    parser.add_argument("--limit", type=int, default=300)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)
    return CollectionOptions(
        encounter_id=args.encounter_id,
        partition=args.partition,
        metric=args.metric,
        difficulty=args.difficulty,
        limit=args.limit,
        out=args.out,
    )


def main(argv: list[str]) -> int:
    options = parse_args(argv)
    client_id, client_secret = read_credentials()
    payload = collect_top_mnk_public_logs(options, client_id, client_secret)
    write_json(options.out, payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
