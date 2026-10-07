#!/usr/bin/env python3
"""Command dispatcher for tuning data helper scripts."""

from __future__ import annotations

import argparse
import sys
import urllib.error
from pathlib import Path

import analyze_even_pre_rof_pb_threshold
import analyze_even_pre_rof_pb_threshold_sweep
import analyze_blm_timing_distributions
import analyze_mnk_timing_distributions
import analyze_nin_timing_distributions
import analyze_vpr_timing_distributions
import build_job_samples_from_top_cache
import collect_public_top_jobs
import collect_report_events
import convert_cast_features_to_job_sample
import diagnose_cast_actor_mapping
import export_job_profiles
import export_blm_timing_review
import export_mnk_final_tuning_review
import export_mnk_profiles
import export_mnk_tuning_candidates
import export_nin_timing_review
import export_vpr_timing_review
import extract_cast_features
import extract_blm_specific_features
import extract_job_features
import extract_mnk_specific_features
import extract_mnk_features
import extract_nin_specific_features
import extract_vpr_specific_features
import filter_nin_pre_raiton_samples
from filter_fflogs_type_names import filter_type_names
from fflogs_client import fetch_access_token, graphql_request, read_credentials
from jobs import JOBS, fflogs_job_name, supported_jobs
import review_mnk_tuning_candidates
import review_nin_pre_raiton_timing
import run_job_public_pipeline
from summarize_character_rankings_probe import summarize_character_rankings_probe
import summarize_ability_usage
from summarize_json_shape import summarize_json_shape
from summarize_fflogs_schema_probe import read_json, summarize_probe_response, write_json
from summarize_zone_encounters import summarize_zone_encounters


def list_jobs() -> int:
    for job in supported_jobs():
        print(f"{job}\t{JOBS[job]['fflogs']}")
    return 0


def read_text(path: Path) -> str:
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def fetch_probe_access_token() -> str:
    client_id, client_secret = read_credentials()
    try:
        return fetch_access_token(client_id, client_secret)
    except urllib.error.HTTPError as error:
        if error.code == 401:
            raise RuntimeError("FF Logs authentication failed") from error
        raise


def run_graphql_probe(access_token: str, query_dir: Path, query_name: str) -> dict:
    try:
        return graphql_request(access_token, read_text(query_dir / query_name))
    except RuntimeError as error:
        raise RuntimeError(f"GraphQL probe failed for {query_name}: {error}") from error


def run_graphql_probe_with_variables(
    access_token: str,
    query_dir: Path,
    query_name: str,
    variables: dict,
) -> dict:
    try:
        return graphql_request(access_token, read_text(query_dir / query_name), variables)
    except RuntimeError as error:
        raise RuntimeError(f"GraphQL probe failed for {query_name}: {error}") from error


def run_schema_probe(out_dir: str) -> int:
    output_dir = Path(out_dir)
    tool_dir = Path(__file__).resolve().parent
    query_dir = tool_dir / "sample_graphql"
    output_dir.mkdir(parents=True, exist_ok=True)

    probes = (
        ("rate_limit_query.graphql", "rate_limit_response.json"),
        ("schema_probe_query.graphql", "schema_probe_response.json"),
        ("type_name_list_query.graphql", "type_name_list_response.json"),
    )

    access_token = fetch_probe_access_token()

    for query_name, output_name in probes:
        response = run_graphql_probe(access_token, query_dir, query_name)
        write_json(str(output_dir / output_name), response)

    type_name_response = read_json(str(output_dir / "type_name_list_response.json"))
    type_name_candidates = filter_type_names(type_name_response)
    write_json(str(output_dir / "type_name_candidates.json"), type_name_candidates)
    return 0


def run_ranking_type_probe(out_dir: str) -> int:
    output_dir = Path(out_dir)
    tool_dir = Path(__file__).resolve().parent
    query_dir = tool_dir / "sample_graphql"
    output_dir.mkdir(parents=True, exist_ok=True)

    access_token = fetch_probe_access_token()
    response = run_graphql_probe(access_token, query_dir, "ranking_type_probe_query.graphql")
    write_json(str(output_dir / "ranking_type_probe_response.json"), response)

    ranking_response = read_json(str(output_dir / "ranking_type_probe_response.json"))
    ranking_summary = summarize_probe_response(ranking_response)
    write_json(str(output_dir / "ranking_schema_summary.json"), ranking_summary)
    return 0


def run_refined_type_probe(out_dir: str) -> int:
    output_dir = Path(out_dir)
    tool_dir = Path(__file__).resolve().parent
    query_dir = tool_dir / "sample_graphql"
    output_dir.mkdir(parents=True, exist_ok=True)

    access_token = fetch_probe_access_token()
    response = run_graphql_probe(access_token, query_dir, "refined_type_probe_query.graphql")
    write_json(str(output_dir / "refined_type_probe_response.json"), response)

    refined_response = read_json(str(output_dir / "refined_type_probe_response.json"))
    refined_summary = summarize_probe_response(refined_response)
    write_json(str(output_dir / "refined_type_schema_summary.json"), refined_summary)
    return 0


def write_shape_probe_outputs(output_dir: Path, response_name: str, summary_name: str, response: dict) -> int:
    response_path = output_dir / response_name
    summary_path = output_dir / summary_name
    write_json(str(response_path), response)
    write_json(str(summary_path), summarize_json_shape(response))
    print(response_path.as_posix())
    print(summary_path.as_posix())
    return 0


def run_character_rankings_shape_probe(args: argparse.Namespace) -> int:
    output_dir = Path(args.out_dir)
    tool_dir = Path(__file__).resolve().parent
    query_dir = tool_dir / "sample_graphql"
    output_dir.mkdir(parents=True, exist_ok=True)

    spec_name = args.spec_name if args.spec_name is not None else fflogs_job_name(args.job)
    variables = {
        "encounterId": args.encounter_id,
        "difficulty": args.difficulty,
        "partition": args.partition,
        "metric": args.metric,
        "className": args.class_name,
        "specName": spec_name,
        "page": args.page,
        "size": args.size,
    }

    access_token = fetch_probe_access_token()
    response = run_graphql_probe_with_variables(
        access_token,
        query_dir,
        "character_rankings_shape_probe.graphql",
        variables,
    )
    response_path = output_dir / "character_rankings_shape_response.json"
    summary_path = output_dir / "character_rankings_shape_summary.json"
    diagnostic_path = output_dir / "character_rankings_diagnostic_summary.json"
    write_json(str(response_path), response)
    write_json(str(summary_path), summarize_json_shape(response))
    write_json(str(diagnostic_path), summarize_character_rankings_probe(response, {
        "encounterId": args.encounter_id,
        "difficulty": args.difficulty,
        "partition": args.partition,
        "metric": args.metric,
        "job": args.job,
        "className": args.class_name,
        "specName": spec_name,
        "page": args.page,
        "size": args.size,
    }))
    print(response_path.as_posix())
    print(summary_path.as_posix())
    print(diagnostic_path.as_posix())
    return 0


def run_report_events_shape_probe(args: argparse.Namespace) -> int:
    output_dir = Path(args.out_dir)
    tool_dir = Path(__file__).resolve().parent
    query_dir = tool_dir / "sample_graphql"
    output_dir.mkdir(parents=True, exist_ok=True)

    variables = {
        "code": args.code,
        "fightIDs": [args.fight_id] if args.fight_id is not None else None,
        "dataType": args.data_type,
        "startTime": args.start_time,
        "endTime": args.end_time,
        "sourceID": args.source_id,
        "targetID": args.target_id,
        "abilityID": args.ability_id,
        "limit": args.limit,
    }

    access_token = fetch_probe_access_token()
    response = run_graphql_probe_with_variables(
        access_token,
        query_dir,
        "report_events_shape_probe.graphql",
        variables,
    )
    return write_shape_probe_outputs(
        output_dir,
        "report_events_shape_response.json",
        "report_events_shape_summary.json",
        response,
    )


def run_zone_encounters_probe(args: argparse.Namespace) -> int:
    output_dir = Path(args.out_dir)
    tool_dir = Path(__file__).resolve().parent
    query_dir = tool_dir / "sample_graphql"
    output_dir.mkdir(parents=True, exist_ok=True)

    variables = {
        "expansionId": args.expansion_id,
    }

    access_token = fetch_probe_access_token()
    response = run_graphql_probe_with_variables(
        access_token,
        query_dir,
        "zone_encounters_probe.graphql",
        variables,
    )

    response_path = output_dir / "zone_encounters_response.json"
    summary_path = output_dir / "zone_encounters_summary.json"
    write_json(str(response_path), response)
    write_json(str(summary_path), summarize_zone_encounters(response))
    print(response_path.as_posix())
    print(summary_path.as_posix())
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="MNK and common job tuning tool dispatcher.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("list-jobs", help="List supported job short codes and FF Logs names.")

    probe = subparsers.add_parser("probe-schema", help="Run an FF Logs GraphQL schema probe.")
    probe.add_argument("--out-dir", default="tools/mnk_tuning")

    ranking_probe = subparsers.add_parser("probe-ranking-types", help="Run the explicit ranking type probe.")
    ranking_probe.add_argument("--out-dir", default="tools/mnk_tuning")

    refined_probe = subparsers.add_parser("probe-refined-types", help="Run the refined schema type probe.")
    refined_probe.add_argument("--out-dir", default="tools/mnk_tuning")

    ranking_shape = subparsers.add_parser("probe-character-rankings-shape", help="Probe characterRankings JSON shape.")
    ranking_shape.add_argument("--encounter-id", type=int, required=True)
    ranking_shape.add_argument("--difficulty", type=int, required=True)
    ranking_shape.add_argument("--partition", type=int, required=True)
    ranking_shape.add_argument("--metric", default="rdps")
    ranking_shape.add_argument("--job", required=True)
    ranking_shape.add_argument("--class-name")
    ranking_shape.add_argument("--spec-name")
    ranking_shape.add_argument("--page", type=int, default=1)
    ranking_shape.add_argument("--size", type=int, default=5)
    ranking_shape.add_argument("--out-dir", default="tools/mnk_tuning")

    events_shape = subparsers.add_parser("probe-report-events-shape", help="Probe Report.events JSON shape.")
    events_shape.add_argument("--code", required=True)
    events_shape.add_argument("--fight-id", type=int)
    events_shape.add_argument("--data-type", default="Casts")
    events_shape.add_argument("--start-time", type=float)
    events_shape.add_argument("--end-time", type=float)
    events_shape.add_argument("--source-id", type=int)
    events_shape.add_argument("--target-id", type=int)
    events_shape.add_argument("--ability-id", type=float)
    events_shape.add_argument("--limit", type=int, default=50)
    events_shape.add_argument("--out-dir", default="tools/mnk_tuning")

    zone_encounters = subparsers.add_parser("probe-zone-encounters", help="Probe public zone and encounter IDs.")
    zone_encounters.add_argument("--expansion-id", type=int)
    zone_encounters.add_argument("--out-dir", default="tools/mnk_tuning")

    zone_encounters_alias = subparsers.add_parser("summarize-zone-encounters", help="Probe and summarize public zone and encounter IDs.")
    zone_encounters_alias.add_argument("--expansion-id", type=int)
    zone_encounters_alias.add_argument("--out-dir", default="tools/mnk_tuning")

    events_collect = subparsers.add_parser("collect-report-events", help="Collect one report fight's event data.")
    events_collect.add_argument("--code", required=True)
    events_collect.add_argument("--fight-id", type=int, required=True)
    events_collect.add_argument("--data-type", default=collect_report_events.DEFAULT_DATA_TYPE)
    events_collect.add_argument("--page-limit", type=int, default=collect_report_events.DEFAULT_PAGE_LIMIT)
    events_collect.add_argument("--max-events", type=int, default=collect_report_events.DEFAULT_MAX_EVENTS)
    events_collect.add_argument("--start-time", type=float)
    events_collect.add_argument("--end-time", type=float)
    events_collect.add_argument("--source-id", type=int)
    events_collect.add_argument("--target-id", type=int)
    events_collect.add_argument("--ability-id", type=float)
    events_collect.add_argument("--out")
    events_collect.add_argument("--use-cache", action="store_true")

    collect = subparsers.add_parser("collect-public-top", help="Collect public top logs for a supported job.")
    collect.add_argument("--encounter-id", type=int, required=True)
    collect.add_argument("--partition", type=int, required=True)
    collect.add_argument("--metric", required=True)
    collect.add_argument("--difficulty", type=int, required=True)
    collect.add_argument("--job", required=True)
    collect.add_argument("--limit", type=int, default=collect_public_top_jobs.DEFAULT_LIMIT)
    collect.add_argument("--party-size", "--size", dest="party_size", type=int, default=8)
    collect.add_argument("--class-name")
    collect.add_argument("--spec-name")
    collect.add_argument("--out")
    collect.add_argument("--use-cache", action="store_true")

    run_public_pipeline = subparsers.add_parser("run-job-public-pipeline", help="Run the common public-log tuning pipeline for one job.")
    run_public_pipeline.add_argument("--job", required=True)
    run_public_pipeline.add_argument("--encounter-id", type=int, required=True)
    run_public_pipeline.add_argument("--difficulty", type=int, required=True)
    run_public_pipeline.add_argument("--partition", type=int, required=True)
    run_public_pipeline.add_argument("--metric", default="rdps")
    run_public_pipeline.add_argument("--party-size", type=int, default=8)
    run_public_pipeline.add_argument("--limit", type=int, default=run_job_public_pipeline.DEFAULT_LIMIT)
    run_public_pipeline.add_argument("--out-prefix", required=True)
    run_public_pipeline.add_argument("--class-name")
    run_public_pipeline.add_argument("--spec-name")
    run_public_pipeline.add_argument("--page-limit", type=int, default=collect_report_events.DEFAULT_PAGE_LIMIT)
    run_public_pipeline.add_argument("--max-events", type=int, default=collect_report_events.DEFAULT_MAX_EVENTS)
    run_public_pipeline.add_argument("--use-cache", action="store_true")
    run_public_pipeline.add_argument("--use-events-cache", action="store_true")

    extract = subparsers.add_parser("extract", help="Extract MNK-specific features.")
    extract.add_argument("--in", dest="input_path", required=True)
    extract.add_argument("--out", required=True)

    extract_cast = subparsers.add_parser("extract-cast-features", help="Extract one job's anonymized cast timing features.")
    extract_cast.add_argument("--job", required=True)
    extract_cast.add_argument("--in", dest="input_path", required=True)
    extract_cast.add_argument("--out", required=True)

    diagnose_cast = subparsers.add_parser("diagnose-cast-actor-mapping", help="Diagnose Casts event sourceID actor mapping.")
    diagnose_cast.add_argument("--job", required=True)
    diagnose_cast.add_argument("--in", dest="input_path", required=True)
    diagnose_cast.add_argument("--out", required=True)

    convert_cast = subparsers.add_parser("convert-cast-to-job-sample", help="Convert cast features into a common job sample.")
    convert_cast.add_argument("--job", required=True)
    convert_cast.add_argument("--cast-features", required=True)
    convert_cast.add_argument("--ranking-cache", required=True)
    convert_cast.add_argument("--source-rank", type=int, required=True)
    convert_cast.add_argument("--out", required=True)

    build_batch = subparsers.add_parser("build-job-samples-batch", help="Build a small common job sample batch from ranking cache rows.")
    build_batch.add_argument("--job", required=True)
    build_batch.add_argument("--ranking-cache", required=True)
    build_batch.add_argument("--limit", type=int, default=build_job_samples_from_top_cache.DEFAULT_LIMIT)
    build_batch.add_argument("--out", required=True)
    build_batch.add_argument("--summary-out", required=True)
    build_batch.add_argument("--page-limit", type=int, default=collect_report_events.DEFAULT_PAGE_LIMIT)
    build_batch.add_argument("--max-events", type=int, default=collect_report_events.DEFAULT_MAX_EVENTS)
    build_batch.add_argument("--use-events-cache", action="store_true")

    ability_usage = subparsers.add_parser("summarize-ability-usage", help="Summarize abilityGameID usage from job samples.")
    ability_usage.add_argument("--in", dest="input_path", required=True)
    ability_usage.add_argument("--out", required=True)

    mnk_specific = subparsers.add_parser("extract-mnk-specific-features", help="Extract MNK-specific timing columns.")
    mnk_specific.add_argument("--samples", required=True)
    mnk_specific.add_argument("--ability-map", required=True)
    mnk_specific.add_argument("--out", required=True)

    nin_specific = subparsers.add_parser("extract-nin-specific-features", help="Extract NIN-specific timing columns.")
    nin_specific.add_argument("--samples", required=True)
    nin_specific.add_argument("--ability-map", required=True)
    nin_specific.add_argument("--out", required=True)

    blm_specific = subparsers.add_parser("extract-blm-specific-features", help="Extract BLM-specific timing columns.")
    blm_specific.add_argument("--samples", required=True)
    blm_specific.add_argument("--ability-map", required=True)
    blm_specific.add_argument("--out", required=True)

    vpr_specific = subparsers.add_parser("extract-vpr-specific-features", help="Extract VPR-specific timing columns.")
    vpr_specific.add_argument("--samples", required=True)
    vpr_specific.add_argument("--ability-map", required=True)
    vpr_specific.add_argument("--out", required=True)

    vpr_distributions = subparsers.add_parser("analyze-vpr-timing-distributions", help="Analyze VPR-specific timing distribution statistics.")
    vpr_distributions.add_argument("--in", dest="input_path", required=True)
    vpr_distributions.add_argument("--out", required=True)

    vpr_timing_review = subparsers.add_parser("export-vpr-timing-review", help="Export review-only VPR timing evidence.")
    vpr_timing_review.add_argument("--specific", required=True)
    vpr_timing_review.add_argument("--distributions", required=True)
    vpr_timing_review.add_argument("--out-json", required=True)
    vpr_timing_review.add_argument("--out-md", required=True)

    blm_distributions = subparsers.add_parser("analyze-blm-timing-distributions", help="Analyze BLM-specific timing distribution statistics.")
    blm_distributions.add_argument("--in", dest="input_path", required=True)
    blm_distributions.add_argument("--out", required=True)

    blm_timing_review = subparsers.add_parser("export-blm-timing-review", help="Export review-only BLM timing evidence.")
    blm_timing_review.add_argument("--specific", required=True)
    blm_timing_review.add_argument("--distributions", required=True)
    blm_timing_review.add_argument("--out-json", required=True)
    blm_timing_review.add_argument("--out-md", required=True)

    nin_distributions = subparsers.add_parser("analyze-nin-timing-distributions", help="Analyze NIN-specific timing distribution statistics.")
    nin_distributions.add_argument("--in", dest="input_path", required=True)
    nin_distributions.add_argument("--out", required=True)

    nin_timing_review = subparsers.add_parser("export-nin-timing-review", help="Export review-only NIN timing evidence.")
    nin_timing_review.add_argument("--specific", required=True)
    nin_timing_review.add_argument("--distributions", required=True)
    nin_timing_review.add_argument("--out", required=True)

    nin_pre_raiton = subparsers.add_parser("filter-nin-pre-raiton-samples", help="Filter NIN samples to opener Raiton-before-Kunai rows.")
    nin_pre_raiton.add_argument("--samples", required=True)
    nin_pre_raiton.add_argument("--nin-specific", required=True)
    nin_pre_raiton.add_argument("--out", required=True)
    nin_pre_raiton.add_argument("--summary-out", required=True)
    nin_pre_raiton.add_argument("--opener-window", type=float, default=20.0)
    nin_pre_raiton.add_argument("--require-dokumori-before-raiton", action="store_true")
    nin_pre_raiton.add_argument("--require-kassatsu-before-kunai", action="store_true")

    nin_pre_raiton_review = subparsers.add_parser("review-nin-pre-raiton-timing", help="Render NIN pre-Raiton timing review JSON and Markdown.")
    nin_pre_raiton_review.add_argument("--filter-summary", required=True)
    nin_pre_raiton_review.add_argument("--distributions", required=True)
    nin_pre_raiton_review.add_argument("--timing-review", required=True)
    nin_pre_raiton_review.add_argument("--out-json", required=True)
    nin_pre_raiton_review.add_argument("--out-md", required=True)

    mnk_candidates = subparsers.add_parser("export-mnk-tuning-candidates", help="Export review-only MNK tuning candidates.")
    mnk_candidates.add_argument("--common-profile", required=True)
    mnk_candidates.add_argument("--mnk-specific", required=True)
    mnk_candidates.add_argument("--out", required=True)

    mnk_review = subparsers.add_parser("review-mnk-tuning-candidates", help="Render MNK tuning candidates as a review Markdown file.")
    mnk_review.add_argument("--in", dest="input_path", required=True)
    mnk_review.add_argument("--out", required=True)

    mnk_distributions = subparsers.add_parser("analyze-mnk-timing-distributions", help="Analyze MNK-specific timing distribution statistics.")
    mnk_distributions.add_argument("--in", dest="input_path", required=True)
    mnk_distributions.add_argument("--out", required=True)

    mnk_final_review = subparsers.add_parser("export-mnk-final-tuning-review", help="Export formula-backed final MNK tuning review JSON.")
    mnk_final_review.add_argument("--candidates", required=True)
    mnk_final_review.add_argument("--distributions", required=True)
    mnk_final_review.add_argument("--out", required=True)

    even_pb_threshold = subparsers.add_parser("analyze-even-pre-rof-pb-threshold", help="Analyze EvenPreRoFPBStartThreshold candidate impact.")
    even_pb_threshold.add_argument("--in", dest="input_path", required=True)
    even_pb_threshold.add_argument("--current", type=float, default=6.0)
    even_pb_threshold.add_argument("--candidate", type=float, default=5.0)
    even_pb_threshold.add_argument("--out", required=True)

    even_pb_threshold_sweep = subparsers.add_parser("analyze-even-pre-rof-pb-threshold-sweep", help="Compare EvenPreRoFPBStartThreshold candidate values.")
    even_pb_threshold_sweep.add_argument("--in", dest="input_path", required=True)
    even_pb_threshold_sweep.add_argument("--thresholds", required=True)
    even_pb_threshold_sweep.add_argument("--current", type=float, default=6.0)
    even_pb_threshold_sweep.add_argument("--out", required=True)

    export = subparsers.add_parser("export-profile", help="Export an MNK-specific profile.")
    export.add_argument("--in", dest="input_path", required=True)
    export.add_argument("--out", required=True)
    export.add_argument("--method", choices=("median", "trimmed-mean"), default="median")

    extract_job = subparsers.add_parser("extract-job", help="Extract common features for a supported job.")
    extract_job.add_argument("--job", required=True)
    extract_job.add_argument("--in", dest="input_path", required=True)
    extract_job.add_argument("--out", required=True)

    export_job = subparsers.add_parser("export-job-profile", help="Export a common profile for a supported job.")
    export_job.add_argument("--job", required=True)
    export_job.add_argument("--in", dest="input_path", required=True)
    export_job.add_argument("--out", required=True)
    export_job.add_argument("--method", choices=("median", "trimmed-mean"), default="median")

    args = parser.parse_args(argv)
    if args.command == "list-jobs":
        return list_jobs()
    if args.command == "probe-schema":
        return run_schema_probe(args.out_dir)
    if args.command == "probe-ranking-types":
        return run_ranking_type_probe(args.out_dir)
    if args.command == "probe-refined-types":
        return run_refined_type_probe(args.out_dir)
    if args.command == "probe-character-rankings-shape":
        return run_character_rankings_shape_probe(args)
    if args.command == "probe-report-events-shape":
        return run_report_events_shape_probe(args)
    if args.command == "probe-zone-encounters" or args.command == "summarize-zone-encounters":
        return run_zone_encounters_probe(args)
    if args.command == "collect-report-events":
        forwarded = [
            "--code", args.code,
            "--fight-id", str(args.fight_id),
            "--data-type", args.data_type,
            "--page-limit", str(args.page_limit),
            "--max-events", str(args.max_events),
        ]
        if args.start_time is not None:
            forwarded.extend(["--start-time", str(args.start_time)])
        if args.end_time is not None:
            forwarded.extend(["--end-time", str(args.end_time)])
        if args.source_id is not None:
            forwarded.extend(["--source-id", str(args.source_id)])
        if args.target_id is not None:
            forwarded.extend(["--target-id", str(args.target_id)])
        if args.ability_id is not None:
            forwarded.extend(["--ability-id", str(args.ability_id)])
        if args.out:
            forwarded.extend(["--out", args.out])
        if args.use_cache:
            forwarded.append("--use-cache")
        return collect_report_events.main(forwarded)
    if args.command == "collect-public-top":
        forwarded = [
            "--encounter-id", str(args.encounter_id),
            "--partition", str(args.partition),
            "--metric", args.metric,
            "--difficulty", str(args.difficulty),
            "--job", args.job,
            "--limit", str(args.limit),
            "--party-size", str(args.party_size),
        ]
        if args.class_name is not None:
            forwarded.extend(["--class-name", args.class_name])
        if args.spec_name is not None:
            forwarded.extend(["--spec-name", args.spec_name])
        if args.out:
            forwarded.extend(["--out", args.out])
        if args.use_cache:
            forwarded.append("--use-cache")
        return collect_public_top_jobs.main(forwarded)
    if args.command == "run-job-public-pipeline":
        forwarded = [
            "--job", args.job,
            "--encounter-id", str(args.encounter_id),
            "--difficulty", str(args.difficulty),
            "--partition", str(args.partition),
            "--metric", args.metric,
            "--party-size", str(args.party_size),
            "--limit", str(args.limit),
            "--out-prefix", args.out_prefix,
            "--page-limit", str(args.page_limit),
            "--max-events", str(args.max_events),
        ]
        if args.class_name is not None:
            forwarded.extend(["--class-name", args.class_name])
        if args.spec_name is not None:
            forwarded.extend(["--spec-name", args.spec_name])
        if args.use_cache:
            forwarded.append("--use-cache")
        if args.use_events_cache:
            forwarded.append("--use-events-cache")
        return run_job_public_pipeline.main(forwarded)
    if args.command == "extract":
        return extract_mnk_features.main(["--in", args.input_path, "--out", args.out])
    if args.command == "extract-cast-features":
        return extract_cast_features.main(["--job", args.job, "--in", args.input_path, "--out", args.out])
    if args.command == "diagnose-cast-actor-mapping":
        return diagnose_cast_actor_mapping.main(["--job", args.job, "--in", args.input_path, "--out", args.out])
    if args.command == "convert-cast-to-job-sample":
        return convert_cast_features_to_job_sample.main([
            "--job", args.job,
            "--cast-features", args.cast_features,
            "--ranking-cache", args.ranking_cache,
            "--source-rank", str(args.source_rank),
            "--out", args.out,
        ])
    if args.command == "build-job-samples-batch":
        forwarded = [
            "--job", args.job,
            "--ranking-cache", args.ranking_cache,
            "--limit", str(args.limit),
            "--out", args.out,
            "--summary-out", args.summary_out,
            "--page-limit", str(args.page_limit),
            "--max-events", str(args.max_events),
        ]
        if args.use_events_cache:
            forwarded.append("--use-events-cache")
        return build_job_samples_from_top_cache.main(forwarded)
    if args.command == "summarize-ability-usage":
        return summarize_ability_usage.main(["--in", args.input_path, "--out", args.out])
    if args.command == "extract-mnk-specific-features":
        return extract_mnk_specific_features.main([
            "--samples", args.samples,
            "--ability-map", args.ability_map,
            "--out", args.out,
        ])
    if args.command == "extract-nin-specific-features":
        return extract_nin_specific_features.main([
            "--samples", args.samples,
            "--ability-map", args.ability_map,
            "--out", args.out,
        ])
    if args.command == "extract-blm-specific-features":
        return extract_blm_specific_features.main([
            "--samples", args.samples,
            "--ability-map", args.ability_map,
            "--out", args.out,
        ])
    if args.command == "extract-vpr-specific-features":
        return extract_vpr_specific_features.main([
            "--samples", args.samples,
            "--ability-map", args.ability_map,
            "--out", args.out,
        ])
    if args.command == "analyze-vpr-timing-distributions":
        return analyze_vpr_timing_distributions.main(["--in", args.input_path, "--out", args.out])
    if args.command == "export-vpr-timing-review":
        return export_vpr_timing_review.main([
            "--specific", args.specific,
            "--distributions", args.distributions,
            "--out-json", args.out_json,
            "--out-md", args.out_md,
        ])
    if args.command == "analyze-blm-timing-distributions":
        return analyze_blm_timing_distributions.main(["--in", args.input_path, "--out", args.out])
    if args.command == "export-blm-timing-review":
        return export_blm_timing_review.main([
            "--specific", args.specific,
            "--distributions", args.distributions,
            "--out-json", args.out_json,
            "--out-md", args.out_md,
        ])
    if args.command == "analyze-nin-timing-distributions":
        return analyze_nin_timing_distributions.main(["--in", args.input_path, "--out", args.out])
    if args.command == "export-nin-timing-review":
        return export_nin_timing_review.main([
            "--specific", args.specific,
            "--distributions", args.distributions,
            "--out", args.out,
        ])
    if args.command == "filter-nin-pre-raiton-samples":
        forwarded = [
            "--samples", args.samples,
            "--nin-specific", args.nin_specific,
            "--out", args.out,
            "--summary-out", args.summary_out,
            "--opener-window", str(args.opener_window),
        ]
        if args.require_dokumori_before_raiton:
            forwarded.append("--require-dokumori-before-raiton")
        if args.require_kassatsu_before_kunai:
            forwarded.append("--require-kassatsu-before-kunai")
        return filter_nin_pre_raiton_samples.main(forwarded)
    if args.command == "review-nin-pre-raiton-timing":
        return review_nin_pre_raiton_timing.main([
            "--filter-summary", args.filter_summary,
            "--distributions", args.distributions,
            "--timing-review", args.timing_review,
            "--out-json", args.out_json,
            "--out-md", args.out_md,
        ])
    if args.command == "export-mnk-tuning-candidates":
        return export_mnk_tuning_candidates.main([
            "--common-profile", args.common_profile,
            "--mnk-specific", args.mnk_specific,
            "--out", args.out,
        ])
    if args.command == "review-mnk-tuning-candidates":
        return review_mnk_tuning_candidates.main(["--in", args.input_path, "--out", args.out])
    if args.command == "analyze-mnk-timing-distributions":
        return analyze_mnk_timing_distributions.main(["--in", args.input_path, "--out", args.out])
    if args.command == "export-mnk-final-tuning-review":
        return export_mnk_final_tuning_review.main([
            "--candidates", args.candidates,
            "--distributions", args.distributions,
            "--out", args.out,
        ])
    if args.command == "analyze-even-pre-rof-pb-threshold":
        return analyze_even_pre_rof_pb_threshold.main([
            "--in", args.input_path,
            "--current", str(args.current),
            "--candidate", str(args.candidate),
            "--out", args.out,
        ])
    if args.command == "analyze-even-pre-rof-pb-threshold-sweep":
        return analyze_even_pre_rof_pb_threshold_sweep.main([
            "--in", args.input_path,
            "--thresholds", args.thresholds,
            "--current", str(args.current),
            "--out", args.out,
        ])
    if args.command == "export-profile":
        return export_mnk_profiles.main(["--in", args.input_path, "--out", args.out, "--method", args.method])
    if args.command == "extract-job":
        return extract_job_features.main(["--job", args.job, "--in", args.input_path, "--out", args.out])
    if args.command == "export-job-profile":
        return export_job_profiles.main(["--job", args.job, "--in", args.input_path, "--out", args.out, "--method", args.method])

    parser.error(f"unsupported command: {args.command}")
    return 2


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
