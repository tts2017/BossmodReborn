#!/usr/bin/env python3
"""Run the common public-log tuning pipeline for one supported job."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path
from typing import Any

import build_job_samples_from_top_cache
import collect_public_top_jobs
import collect_report_events
import export_job_profiles
import extract_job_features
from jobs import normalize_job


DEFAULT_LIMIT = 10


def output_paths(out_prefix: str) -> dict[str, Path]:
    prefix = Path(out_prefix)
    return {
        "samples": prefix.parent / f"{prefix.name}_job_samples_batch.json",
        "summary": prefix.parent / f"{prefix.name}_job_samples_batch_summary.json",
        "features": prefix.parent / f"{prefix.name}_job_features.json",
        "profile": prefix.parent / f"{prefix.name}_job_profile.json",
    }


def build_collect_options(args: argparse.Namespace, job: str) -> collect_public_top_jobs.PublicTopOptions:
    return collect_public_top_jobs.PublicTopOptions(
        encounter_id=args.encounter_id,
        partition=args.partition,
        metric=args.metric,
        difficulty=args.difficulty,
        job=job,
        limit=args.limit,
        party_size=args.party_size,
        class_name=args.class_name,
        spec_name=args.spec_name,
        out=None,
        use_cache=args.use_cache,
    )


def run_pipeline(args: argparse.Namespace) -> dict[str, Any]:
    job = normalize_job(args.job)
    paths = output_paths(args.out_prefix)
    collect_options = build_collect_options(args, job)

    ranking_payload = collect_public_top_jobs.collect_public_top(collect_options)
    collect_public_top_jobs.write_output(collect_options, ranking_payload)
    ranking_cache_path = collect_public_top_jobs.output_path_for(collect_options)

    samples_payload, summary_payload = build_job_samples_from_top_cache.build_batch_payload(
        job,
        ranking_payload,
        args.limit,
        args.page_limit,
        args.max_events,
        args.use_events_cache,
    )
    build_job_samples_from_top_cache.write_json(str(paths["samples"]), samples_payload)
    build_job_samples_from_top_cache.write_json(str(paths["summary"]), summary_payload)

    succeeded = summary_payload.get("succeeded", 0)
    if not isinstance(succeeded, int) or succeeded <= 0:
        raise RuntimeError("job public pipeline produced no successful samples")

    features_payload = extract_job_features.extract_job_features(samples_payload, job)
    extract_job_features.write_json(str(paths["features"]), features_payload)

    profile_payload = export_job_profiles.export_job_profile(features_payload, job, "median")
    export_job_profiles.write_json(str(paths["profile"]), profile_payload)

    return {
        "job": job,
        "rankingCachePath": str(ranking_cache_path),
        "sampleOutputPath": str(paths["samples"]),
        "summaryOutputPath": str(paths["summary"]),
        "featuresOutputPath": str(paths["features"]),
        "profileOutputPath": str(paths["profile"]),
        "requestedLimit": args.limit,
        "succeeded": succeeded,
        "failed": summary_payload.get("failed", 0),
    }


def print_summary(summary: dict[str, Any]) -> None:
    print(f"job={summary['job']}")
    print(f"rankingCachePath={summary['rankingCachePath']}")
    print(f"sampleOutputPath={summary['sampleOutputPath']}")
    print(f"summaryOutputPath={summary['summaryOutputPath']}")
    print(f"featuresOutputPath={summary['featuresOutputPath']}")
    print(f"profileOutputPath={summary['profileOutputPath']}")
    print(f"requestedLimit={summary['requestedLimit']}")
    print(f"succeeded={summary['succeeded']}")
    print(f"failed={summary['failed']}")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Run the common public-log tuning pipeline for one job.")
    parser.add_argument("--job", required=True)
    parser.add_argument("--encounter-id", type=int, required=True)
    parser.add_argument("--difficulty", type=int, required=True)
    parser.add_argument("--partition", type=int, required=True)
    parser.add_argument("--metric", default="rdps")
    parser.add_argument("--party-size", type=int, default=8)
    parser.add_argument("--limit", type=int, default=DEFAULT_LIMIT)
    parser.add_argument("--out-prefix", required=True)
    parser.add_argument("--class-name")
    parser.add_argument("--spec-name")
    parser.add_argument("--page-limit", type=int, default=collect_report_events.DEFAULT_PAGE_LIMIT)
    parser.add_argument("--max-events", type=int, default=collect_report_events.DEFAULT_MAX_EVENTS)
    parser.add_argument("--use-cache", action="store_true")
    parser.add_argument("--use-events-cache", action="store_true")
    args = parser.parse_args(argv)

    print_summary(run_pipeline(args))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
