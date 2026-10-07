#!/usr/bin/env python3
"""Common public top-log collection entry point for all supported jobs."""

from __future__ import annotations

import argparse
import json
import sys
import urllib.error
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from fflogs_client import fetch_access_token, graphql_request, read_credentials
from jobs import fflogs_ranking_spec_name, normalize_job
from public_data_cache import cache_exists, cache_path, read_cache, write_cache


DEFAULT_LIMIT = 200


@dataclass(frozen=True)
class PublicTopOptions:
    encounter_id: int
    partition: int
    metric: str
    difficulty: int
    job: str
    limit: int
    party_size: int
    class_name: str | None
    spec_name: str | None
    out: str | None
    use_cache: bool


def build_public_top_query() -> str:
    query_path = Path(__file__).resolve().parent / "sample_graphql" / "character_rankings_collect.graphql"
    with open(query_path, "r", encoding="utf-8") as handle:
        return handle.read()


def cache_matches_options(payload: dict[str, Any], options: PublicTopOptions, job: str, class_name: str | None, spec_name: str | None) -> bool:
    return (
        payload.get("encounterId") == options.encounter_id
        and payload.get("partition") == options.partition
        and payload.get("difficulty") == options.difficulty
        and payload.get("metric") == options.metric
        and payload.get("job") == job
        and payload.get("limit") == options.limit
        and payload.get("partySize") == options.party_size
        and payload.get("className") == class_name
        and payload.get("specName") == spec_name
    )


def number_or_none(value: Any) -> float | int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return value
    return None


def string_or_none(value: Any) -> str | None:
    return value if isinstance(value, str) else None


def int_or_none(value: Any) -> int | None:
    if isinstance(value, bool):
        return None
    return value if isinstance(value, int) else None


def build_sample(ranking: dict[str, Any], source_rank: int, page: int, page_index: int) -> dict[str, Any]:
    report = ranking.get("report")
    if not isinstance(report, dict):
        report = {}

    return {
        "sourceRank": source_rank,
        "page": page,
        "pageIndex": page_index,
        "duration": number_or_none(ranking.get("duration")),
        "startTime": number_or_none(ranking.get("startTime")),
        "amount": number_or_none(ranking.get("amount")),
        "rDPS": number_or_none(ranking.get("rDPS")),
        "aDPS": number_or_none(ranking.get("aDPS")),
        "nDPS": number_or_none(ranking.get("nDPS")),
        "cDPS": number_or_none(ranking.get("cDPS")),
        "pDPS": number_or_none(ranking.get("pDPS")),
        "class": string_or_none(ranking.get("class")),
        "spec": string_or_none(ranking.get("spec")),
        "reportCode": string_or_none(report.get("code")),
        "fightID": int_or_none(report.get("fightID")),
        "reportStartTime": number_or_none(report.get("startTime")),
    }


def rankings_payload_from_response(response: dict[str, Any]) -> dict[str, Any]:
    data = response.get("data")
    if not isinstance(data, dict):
        raise RuntimeError("character rankings response must contain a JSON object at data")

    world_data = data.get("worldData")
    if not isinstance(world_data, dict):
        raise RuntimeError("character rankings response must contain data.worldData")

    encounter = world_data.get("encounter")
    if encounter is None:
        raise RuntimeError("FF Logs worldData.encounter returned null")
    if not isinstance(encounter, dict):
        raise RuntimeError("FF Logs worldData.encounter was not a JSON object")

    character_rankings = encounter.get("characterRankings")
    if not isinstance(character_rankings, dict):
        raise RuntimeError("FF Logs characterRankings was not a JSON object")

    ranking_error = character_rankings.get("error")
    if isinstance(ranking_error, str) and ranking_error:
        raise RuntimeError(f"FF Logs characterRankings returned error: {ranking_error}")

    rankings = character_rankings.get("rankings")
    if not isinstance(rankings, list):
        raise RuntimeError("FF Logs characterRankings.rankings was not a list")

    return character_rankings


def collect_public_top(options: PublicTopOptions) -> dict[str, Any]:
    job = normalize_job(options.job)
    class_name = options.class_name
    spec_name = options.spec_name if options.spec_name is not None else fflogs_ranking_spec_name(job)
    path = Path(options.out) if options.out else cache_path(
        options.encounter_id,
        options.partition,
        options.difficulty,
        options.metric,
        job,
        options.limit,
    )

    if options.use_cache and cache_exists(path):
        cached = read_cache(path)
        if not isinstance(cached, dict):
            raise RuntimeError("cached public top payload must be a JSON object")
        if cache_matches_options(cached, options, job, class_name, spec_name):
            return cached

    client_id, client_secret = read_credentials()
    try:
        access_token = fetch_access_token(client_id, client_secret)
    except urllib.error.HTTPError as error:
        if error.code == 401:
            raise RuntimeError("FF Logs authentication failed") from error
        raise

    query = build_public_top_query()
    samples: list[dict[str, Any]] = []
    page = 1
    pages_fetched = 0
    has_more_pages = False

    while len(samples) < options.limit:
        variables = {
            "encounterId": options.encounter_id,
            "difficulty": options.difficulty,
            "partition": options.partition,
            "metric": options.metric,
            "className": class_name,
            "specName": spec_name,
            "page": page,
            "partySize": options.party_size,
        }
        response = graphql_request(access_token, query, variables)
        character_rankings = rankings_payload_from_response(response)
        rankings = character_rankings["rankings"]
        pages_fetched += 1
        has_more_pages = bool(character_rankings.get("hasMorePages"))

        for page_index, ranking in enumerate(rankings):
            if len(samples) >= options.limit:
                break
            if not isinstance(ranking, dict):
                continue
            samples.append(build_sample(ranking, len(samples) + 1, page, page_index))

        if not has_more_pages:
            break
        if not rankings:
            break
        page += 1

    return {
        "contentId": 0,
        "encounterId": options.encounter_id,
        "difficulty": options.difficulty,
        "partition": options.partition,
        "metric": options.metric,
        "job": job,
        "className": class_name,
        "specName": spec_name,
        "partySize": options.party_size,
        "limit": options.limit,
        "source": "fflogs_character_rankings_public",
        "sampleCount": len(samples),
        "pagesFetched": pages_fetched,
        "hasMorePages": has_more_pages,
        "samples": samples,
    }


def output_path_for(options: PublicTopOptions) -> Path:
    job = normalize_job(options.job)
    if options.out:
        return Path(options.out)
    return cache_path(options.encounter_id, options.partition, options.difficulty, options.metric, job, options.limit)


def write_output(options: PublicTopOptions, payload: dict[str, Any]) -> None:
    write_cache(output_path_for(options), payload)


def parse_args(argv: list[str]) -> PublicTopOptions:
    parser = argparse.ArgumentParser(description="Collect public top FF Logs data for a supported job.")
    parser.add_argument("--encounter-id", type=int, required=True)
    parser.add_argument("--partition", type=int, required=True)
    parser.add_argument("--metric", required=True)
    parser.add_argument("--difficulty", type=int, required=True)
    parser.add_argument("--job", required=True)
    parser.add_argument("--limit", type=int, default=DEFAULT_LIMIT)
    parser.add_argument("--party-size", "--size", dest="party_size", type=int, default=8)
    parser.add_argument("--class-name")
    parser.add_argument("--spec-name")
    parser.add_argument("--out")
    parser.add_argument("--use-cache", action="store_true")
    args = parser.parse_args(argv)
    job = normalize_job(args.job)
    return PublicTopOptions(
        encounter_id=args.encounter_id,
        partition=args.partition,
        metric=args.metric,
        difficulty=args.difficulty,
        job=job,
        limit=args.limit,
        party_size=args.party_size,
        class_name=args.class_name,
        spec_name=args.spec_name,
        out=args.out,
        use_cache=args.use_cache,
    )


def main(argv: list[str]) -> int:
    options = parse_args(argv)
    payload = collect_public_top(options)
    write_output(options, payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
