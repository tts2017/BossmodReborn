#!/usr/bin/env python3
"""Convert one cast-feature payload into the common job sample input shape."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any

from jobs import normalize_job


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


def number_or_none(value: Any) -> float | int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return value
    return None


def int_or_none(value: Any) -> int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    return None


def list_or_empty(value: Any) -> list[Any]:
    return value if isinstance(value, list) else []


def dict_or_empty(value: Any) -> dict[str, Any]:
    return value if isinstance(value, dict) else {}


def find_ranking_sample(ranking_cache: dict[str, Any], source_rank: int) -> dict[str, Any]:
    samples = ranking_cache.get("samples")
    if not isinstance(samples, list):
        raise RuntimeError("ranking cache must contain samples")

    for sample in samples:
        if isinstance(sample, dict) and sample.get("sourceRank") == source_rank:
            return sample

    raise RuntimeError(f"ranking cache does not contain sourceRank={source_rank}")


def duration_seconds(sample: dict[str, Any]) -> float:
    duration = number_or_none(sample.get("duration"))
    if duration is None:
        return 0.0
    return float(duration) / 1000.0


def build_job_sample_payload(
    job: str,
    cast_features: dict[str, Any],
    ranking_cache: dict[str, Any],
    source_rank: int,
) -> dict[str, Any]:
    normalized_job = normalize_job(job)
    cache_job = ranking_cache.get("job")
    if isinstance(cache_job, str) and normalize_job(cache_job) != normalized_job:
        raise RuntimeError("ranking cache job does not match requested job")

    cast_job = cast_features.get("job")
    if isinstance(cast_job, str) and normalize_job(cast_job) != normalized_job:
        raise RuntimeError("cast features job does not match requested job")

    ranking_sample = find_ranking_sample(ranking_cache, source_rank)
    seconds = duration_seconds(ranking_sample)

    sample = {
        "sourceRank": source_rank,
        "fightID": int_or_none(ranking_sample.get("fightID")),
        "killTime": seconds,
        "duration": seconds,
        "amount": number_or_none(ranking_sample.get("amount")) or 0.0,
        "rDPS": number_or_none(ranking_sample.get("rDPS")) or 0.0,
        "aDPS": number_or_none(ranking_sample.get("aDPS")) or 0.0,
        "nDPS": number_or_none(ranking_sample.get("nDPS")) or 0.0,
        "cDPS": number_or_none(ranking_sample.get("cDPS")) or 0.0,
        "pDPS": number_or_none(ranking_sample.get("pDPS")) or 0.0,
        "jobActorCount": int_or_none(cast_features.get("jobActorCount")) or 0,
        "jobActorIDs": list_or_empty(cast_features.get("jobActorIDs")),
        "fflogsActorSubTypes": list_or_empty(cast_features.get("fflogsActorSubTypes")),
        "castCount": int_or_none(cast_features.get("castCount")) or 0,
        "abilityUseTimes": dict_or_empty(cast_features.get("abilityUseTimes")),
        "burstUseTimes": [],
        "majorBuffUseTimes": [],
        "gaugeSpendTimes": [],
        "potionUseTimes": [],
        "downtimeStarts": [],
        "downtimeEnds": [],
        "profileCandidates": {},
    }

    return {
        "contentId": ranking_cache.get("contentId", 0) if isinstance(ranking_cache.get("contentId", 0), int) else 0,
        "encounterId": ranking_cache.get("encounterId", 0) if isinstance(ranking_cache.get("encounterId", 0), int) else 0,
        "difficulty": ranking_cache.get("difficulty"),
        "partition": ranking_cache.get("partition"),
        "metric": ranking_cache.get("metric"),
        "job": normalized_job,
        "source": "fflogs_public_rankings_casts_derived",
        "samples": [sample],
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Convert cast features into the common job sample input shape.")
    parser.add_argument("--job", required=True)
    parser.add_argument("--cast-features", required=True)
    parser.add_argument("--ranking-cache", required=True)
    parser.add_argument("--source-rank", type=int, required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    payload = build_job_sample_payload(
        args.job,
        read_json(args.cast_features),
        read_json(args.ranking_cache),
        args.source_rank,
    )
    write_json(args.out, payload)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
