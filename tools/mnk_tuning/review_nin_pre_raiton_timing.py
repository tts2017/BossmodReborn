#!/usr/bin/env python3
"""Render NIN pre-Raiton timing review JSON and Markdown."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any


FORBIDDEN_TEXT = (
    "reportCode",
    "name",
    "server",
    "guild",
    "lodestoneID",
    "reportUrl",
    "reportURL",
    "client_secret",
    "access_token",
    "Authorization",
    "Bearer",
)

FIRST_USE_DISTRIBUTIONS = (
    ("kassatsu", "kassatsuFirstUseTime"),
    ("dokumori", "dokumoriFirstUseTime"),
    ("bunshin", "bunshinFirstUseTime"),
    ("phantomKamaitachi", "phantomKamaitachiFirstUseTime"),
    ("kunaisBane", "kunaisBaneFirstUseTime"),
    ("hyoshoRanryu", "hyoshoRanryuFirstUseTime"),
    ("raiton", "raitonFirstUseTime"),
    ("tenChiJin", "tenChiJinFirstUseTime"),
    ("meisui", "meisuiFirstUseTime"),
)


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError(f"input must be a JSON object: {path}")
    return payload


def check_forbidden_text(text: str) -> None:
    found = [item for item in FORBIDDEN_TEXT if item in text]
    if found:
        raise RuntimeError(f"output contains forbidden text: {', '.join(found)}")


def write_json(path: str, payload: dict[str, Any]) -> None:
    text = json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=True)
    check_forbidden_text(text)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
        handle.write("\n")


def write_text(path: str, text: str) -> None:
    check_forbidden_text(text)
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)
        if not text.endswith("\n"):
            handle.write("\n")


def number_or_none(value: Any) -> float | int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return value
    return None


def distribution(distributions: dict[str, Any], key: str) -> dict[str, Any]:
    value = distributions.get(key)
    return value if isinstance(value, dict) else {}


def median_from_distribution(distributions: dict[str, Any], key: str) -> float | int | None:
    return number_or_none(distribution(distributions, key).get("median"))


def observed_order(timing_review: dict[str, Any], distributions: dict[str, Any]) -> list[dict[str, Any]]:
    raw_order = timing_review.get("observedOrder")
    if isinstance(raw_order, list) and raw_order:
        result: list[dict[str, Any]] = []
        for item in raw_order:
            if not isinstance(item, dict):
                continue
            step = item.get("step")
            median = number_or_none(item.get("firstUseMedian"))
            if isinstance(step, str) and median is not None:
                result.append({"step": step, "median": median})
        if result:
            return sorted(result, key=lambda item: float(item["median"]))

    result = []
    for step, distribution_key in FIRST_USE_DISTRIBUTIONS:
        median = median_from_distribution(distributions, distribution_key)
        if median is not None:
            result.append({"step": step, "median": median})
    return sorted(result, key=lambda item: float(item["median"]))


def key_timing(filter_summary: dict[str, Any], distributions: dict[str, Any]) -> dict[str, Any]:
    selected_stats = filter_summary.get("selectedTimingStats")
    selected_timing = selected_stats if isinstance(selected_stats, dict) else {}
    raiton_to_kunai = selected_timing.get("raitonToKunaiDelta")
    raiton_to_kunai_stats = raiton_to_kunai if isinstance(raiton_to_kunai, dict) else {}

    return {
        "raitonToKunaiDeltaMedian": number_or_none(raiton_to_kunai_stats.get("median")),
        "kassatsuKunaiDeltaMedian": median_from_distribution(distributions, "kassatsuKunaiDelta"),
        "kunaiHyoshoDeltaMedian": median_from_distribution(distributions, "kunaiHyoshoDelta"),
        "dokumoriKunaiDeltaMedian": median_from_distribution(distributions, "dokumoriKunaiDelta"),
        "tcjMeisuiDeltaMedian": median_from_distribution(distributions, "tcjMeisuiDelta"),
    }


def recommendations() -> list[dict[str, Any]]:
    return [
        {
            "id": "pre_raiton_subset_only",
            "recommendation": "Use pre-Raiton subset for NIN tuning, not full top200.",
            "applyToNINcs": False,
        },
        {
            "id": "preserve_pre_raiton_before_kunai",
            "recommendation": "For pre-Raiton route, Raiton should occur before Kunai's Bane.",
            "applyToNINcs": "review_only",
        },
        {
            "id": "kassatsu_immediately_before_kunai",
            "recommendation": "Kassatsu appears close before Kunai's Bane in filtered data.",
            "applyToNINcs": "review_only",
        },
        {
            "id": "hyosho_after_kunai",
            "recommendation": "Hyosho is not immediate first GCD after Kunai in filtered data; median Kunai to Hyosho is about 3.9s.",
            "applyToNINcs": "review_only",
        },
    ]


def build_review(filter_summary: dict[str, Any], distributions_payload: dict[str, Any], timing_review: dict[str, Any]) -> dict[str, Any]:
    distributions = distributions_payload.get("distributions")
    distribution_map = distributions if isinstance(distributions, dict) else {}
    selected_sample_count = filter_summary.get("selectedSampleCount")
    selected_percent = filter_summary.get("selectedPercent")

    return {
        "job": "NIN",
        "source": "top200_public_logs_pre_raiton_filtered",
        "selectedSampleCount": selected_sample_count if isinstance(selected_sample_count, int) else 0,
        "selectedPercent": number_or_none(selected_percent) or 0.0,
        "observedOrder": observed_order(timing_review, distribution_map),
        "keyTiming": key_timing(filter_summary, distribution_map),
        "recommendations": recommendations(),
        "applyRecommendation": "hold",
        "reason": "Review only. Current NIN burst state machine must be checked before applying.",
    }


def format_value(value: Any) -> str:
    number = number_or_none(value)
    if number is None:
        return "n/a"
    return f"{float(number):.3f}".rstrip("0").rstrip(".")


def render_markdown(review: dict[str, Any]) -> str:
    lines = [
        "# NIN Pre-Raiton Timing Review",
        "",
        "## Summary",
        f"- selectedSampleCount: {review['selectedSampleCount']}",
        f"- selectedPercent: {format_value(review['selectedPercent'])}",
        "- applyRecommendation: hold",
        "",
        "## Observed Order",
        "| step | median |",
        "| --- | ---: |",
    ]
    for item in review["observedOrder"]:
        lines.append(f"| {item['step']} | {format_value(item['median'])} |")

    lines.extend([
        "",
        "## Key Timing",
        "| field | median |",
        "| --- | ---: |",
    ])
    for key, value in review["keyTiming"].items():
        lines.append(f"| {key} | {format_value(value)} |")

    lines.extend([
        "",
        "## Recommendations",
        "| id | recommendation | applyToNINcs |",
        "| --- | --- | --- |",
    ])
    for item in review["recommendations"]:
        lines.append(f"| {item['id']} | {item['recommendation']} | {item['applyToNINcs']} |")

    lines.extend([
        "",
        "## Decision",
        "Do not auto-apply to NIN.cs.",
        review["reason"],
        "",
    ])
    return "\n".join(lines)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Render NIN pre-Raiton timing review JSON and Markdown.")
    parser.add_argument("--filter-summary", required=True)
    parser.add_argument("--distributions", required=True)
    parser.add_argument("--timing-review", required=True)
    parser.add_argument("--out-json", required=True)
    parser.add_argument("--out-md", required=True)
    args = parser.parse_args(argv)

    review = build_review(
        read_json(args.filter_summary),
        read_json(args.distributions),
        read_json(args.timing_review),
    )
    write_json(args.out_json, review)
    write_text(args.out_md, render_markdown(review))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
