#!/usr/bin/env python3
"""Build a small batch of common job samples from top ranking cache rows."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any

import collect_report_events
import convert_cast_features_to_job_sample
import extract_cast_features
from jobs import normalize_job
from public_data_cache import write_cache


DEFAULT_LIMIT = 3


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError(f"input must be a JSON object: {path}")
    return payload


def write_json(path: str, payload: dict[str, Any]) -> None:
    forbidden = convert_cast_features_to_job_sample.forbidden_key_paths(payload)
    if forbidden:
        raise RuntimeError(f"output contains forbidden keys: {', '.join(forbidden)}")
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def int_or_none(value: Any) -> int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return value
    return None


def ranking_samples(ranking_cache: dict[str, Any], limit: int) -> list[dict[str, Any]]:
    samples = ranking_cache.get("samples")
    if not isinstance(samples, list):
        raise RuntimeError("ranking cache must contain samples")

    valid_samples = [sample for sample in samples if isinstance(sample, dict)]
    return sorted(
        valid_samples,
        key=lambda sample: int_or_none(sample.get("sourceRank")) if int_or_none(sample.get("sourceRank")) is not None else 10**9,
    )[:limit]


def build_events_options(
    report_code: str,
    fight_id: int,
    page_limit: int,
    max_events: int,
    use_events_cache: bool,
) -> collect_report_events.ReportEventsOptions:
    return collect_report_events.ReportEventsOptions(
        code=report_code,
        fight_id=fight_id,
        data_type=collect_report_events.DEFAULT_DATA_TYPE,
        page_limit=page_limit,
        max_events=max_events,
        start_time=None,
        end_time=None,
        source_id=None,
        target_id=None,
        ability_id=None,
        out=None,
        use_cache=use_events_cache,
    )


def process_one_sample(
    job: str,
    ranking_cache: dict[str, Any],
    ranking_sample: dict[str, Any],
    page_limit: int,
    max_events: int,
    use_events_cache: bool,
) -> dict[str, Any]:
    source_rank = int_or_none(ranking_sample.get("sourceRank"))
    if source_rank is None:
        raise RuntimeError("ranking sample is missing numeric sourceRank")

    report_code = ranking_sample.get("reportCode")
    if not isinstance(report_code, str) or not report_code:
        raise RuntimeError("ranking sample is missing reportCode")

    fight_id = int_or_none(ranking_sample.get("fightID"))
    if fight_id is None:
        raise RuntimeError("ranking sample is missing numeric fightID")

    options = build_events_options(report_code, fight_id, page_limit, max_events, use_events_cache)
    event_payload = collect_report_events.collect_report_events(options)
    write_cache(collect_report_events.output_path_for(options), event_payload)

    cast_features = extract_cast_features.build_feature_payload(event_payload, job)
    job_sample_payload = convert_cast_features_to_job_sample.build_job_sample_payload(
        job,
        cast_features,
        ranking_cache,
        source_rank,
    )
    samples = job_sample_payload.get("samples")
    if not isinstance(samples, list) or not samples or not isinstance(samples[0], dict):
        raise RuntimeError("converted job sample payload did not contain a sample")
    return samples[0]


def build_batch_payload(
    job: str,
    ranking_cache: dict[str, Any],
    limit: int,
    page_limit: int,
    max_events: int,
    use_events_cache: bool,
) -> tuple[dict[str, Any], dict[str, Any]]:
    normalized_job = normalize_job(job)
    selected = ranking_samples(ranking_cache, limit)
    samples: list[dict[str, Any]] = []
    failures: list[dict[str, Any]] = []

    for ranking_sample in selected:
        source_rank = int_or_none(ranking_sample.get("sourceRank")) or 0
        fight_id = int_or_none(ranking_sample.get("fightID")) or 0
        try:
            samples.append(process_one_sample(
                normalized_job,
                ranking_cache,
                ranking_sample,
                page_limit,
                max_events,
                use_events_cache,
            ))
        except RuntimeError as error:
            failures.append({
                "sourceRank": source_rank,
                "fightID": fight_id,
                "reason": str(error),
            })

    payload = {
        "contentId": ranking_cache.get("contentId", 0) if isinstance(ranking_cache.get("contentId", 0), int) else 0,
        "encounterId": ranking_cache.get("encounterId", 0) if isinstance(ranking_cache.get("encounterId", 0), int) else 0,
        "difficulty": ranking_cache.get("difficulty"),
        "partition": ranking_cache.get("partition"),
        "metric": ranking_cache.get("metric"),
        "job": normalized_job,
        "source": "fflogs_public_rankings_casts_derived_batch",
        "sampleCount": len(samples),
        "samples": samples,
    }
    summary = {
        "job": normalized_job,
        "requestedLimit": limit,
        "processed": len(selected),
        "succeeded": len(samples),
        "failed": len(failures),
        "failures": failures,
    }
    return payload, summary


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Build a small common job sample batch from ranking cache rows.")
    parser.add_argument("--job", required=True)
    parser.add_argument("--ranking-cache", required=True)
    parser.add_argument("--limit", type=int, default=DEFAULT_LIMIT)
    parser.add_argument("--out", required=True)
    parser.add_argument("--summary-out", required=True)
    parser.add_argument("--page-limit", type=int, default=collect_report_events.DEFAULT_PAGE_LIMIT)
    parser.add_argument("--max-events", type=int, default=collect_report_events.DEFAULT_MAX_EVENTS)
    parser.add_argument("--use-events-cache", action="store_true")
    args = parser.parse_args(argv)

    payload, summary = build_batch_payload(
        args.job,
        read_json(args.ranking_cache),
        args.limit,
        args.page_limit,
        args.max_events,
        args.use_events_cache,
    )
    write_json(args.out, payload)
    write_json(args.summary_out, summary)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
