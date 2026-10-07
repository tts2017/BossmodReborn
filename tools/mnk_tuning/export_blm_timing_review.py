#!/usr/bin/env python3
"""Export review-only BLM timing evidence as JSON and Markdown."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any


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

FORBIDDEN_TEXT = (
    "reportCode",
    "reportUrl",
    "reportURL",
    "client_secret",
    "access_token",
    "Authorization",
    "Bearer",
)

ORDER_FIELDS = (
    ("amplifier", "amplifierFirstUseTime"),
    ("fireIV", "fireIVFirstUseTime"),
    ("leyLines", "leyLinesFirstUseTime"),
    ("manafont", "manafontFirstUseTime"),
    ("triplecast", "triplecastFirstUseTime"),
    ("swiftcast", "swiftcastFirstUseTime"),
    ("highThunder", "highThunderFirstUseTime"),
    ("xenoglossy", "xenoglossyFirstUseTime"),
    ("despair", "despairFirstUseTime"),
    ("flareStar", "flareStarFirstUseTime"),
    ("foul", "foulFirstUseTime"),
)

EVIDENCE_FIELDS = (
    "leyLinesManafontDelta",
    "leyLinesAmplifierDelta",
    "leyLinesTriplecastDelta",
    "manafontDespairDelta",
    "fireIVDespairDelta",
    "despairFlareStarDelta",
    "lastLeyLinesFromFightEnd",
    "lastManafontFromFightEnd",
    "lastAmplifierFromFightEnd",
)


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


def write_text(path: str, text: str) -> None:
    hits = [term for term in FORBIDDEN_TEXT if term in text]
    if hits:
        raise RuntimeError(f"output contains forbidden text: {', '.join(hits)}")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(text)


def number_or_none(value: Any) -> float | int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return value
    return None


def distribution(distributions: dict[str, Any], key: str) -> dict[str, Any]:
    value = distributions.get(key)
    return value if isinstance(value, dict) else {}


def observed_order(distributions: dict[str, Any]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for step, distribution_key in ORDER_FIELDS:
        item = distribution(distributions, distribution_key)
        sample_count = number_or_none(item.get("sampleCount"))
        median = number_or_none(item.get("median"))
        if sample_count and median is not None:
            rows.append({
                "step": step,
                "median": median,
            })
    return sorted(rows, key=lambda row: float(row["median"]))


def evidence(distributions: dict[str, Any], key: str) -> dict[str, Any]:
    item = distribution(distributions, key)
    return {
        "sampleCount": number_or_none(item.get("sampleCount")),
        "median": number_or_none(item.get("median")),
        "p75": number_or_none(item.get("p75")),
        "p90": number_or_none(item.get("p90")),
    }


def format_value(value: Any) -> str:
    number = number_or_none(value)
    if number is None:
        return "null"
    return f"{float(number):.3f}"


def export_timing_review(specific_payload: dict[str, Any], distributions_payload: dict[str, Any]) -> dict[str, Any]:
    distributions = distributions_payload.get("distributions")
    if not isinstance(distributions, dict):
        raise RuntimeError("distribution input must contain a distributions object")

    return {
        "job": "BLM",
        "source": "top200_public_logs_blm_timing_review",
        "sampleCount": specific_payload.get("sampleCount") if isinstance(specific_payload.get("sampleCount"), int) else 0,
        "observedOrder": observed_order(distributions),
        "timingEvidence": {key: evidence(distributions, key) for key in EVIDENCE_FIELDS},
        "review": {
            "leyLinesTiming": {
                "recommendation": "review_only",
                "reason": "Top200 median Ley Lines timing should be compared with BLM opener planner.",
            },
            "manafontDespair": {
                "recommendation": "review_only",
                "reason": "Manafont to Despair delta should be compared with burst planner.",
            },
            "flareStar": {
                "recommendation": "review_only",
                "reason": "Flare Star timing requires BLM AF/MP state context.",
            },
        },
        "applyRecommendation": "hold",
        "reason": "Do not auto-apply to BLM.cs until current BLM planner semantics are reviewed.",
    }


def markdown_table(headers: tuple[str, ...], rows: list[tuple[Any, ...]]) -> str:
    lines = [
        "| " + " | ".join(headers) + " |",
        "| " + " | ".join("---" for _ in headers) + " |",
    ]
    for row in rows:
        lines.append("| " + " | ".join(str(item) for item in row) + " |")
    return "\n".join(lines)


def export_markdown(review: dict[str, Any]) -> str:
    observed_rows = [
        (row.get("step", ""), format_value(row.get("median")))
        for row in review.get("observedOrder", [])
        if isinstance(row, dict)
    ]
    evidence_payload = review.get("timingEvidence")
    evidence = evidence_payload if isinstance(evidence_payload, dict) else {}
    evidence_rows = [
        (
            key,
            format_value(item.get("median") if isinstance(item, dict) else None),
            format_value(item.get("p75") if isinstance(item, dict) else None),
            format_value(item.get("p90") if isinstance(item, dict) else None),
        )
        for key, item in evidence.items()
    ]
    review_payload = review.get("review")
    review_items = review_payload if isinstance(review_payload, dict) else {}
    review_rows = [
        (
            key,
            item.get("recommendation", "") if isinstance(item, dict) else "",
            item.get("reason", "") if isinstance(item, dict) else "",
        )
        for key, item in review_items.items()
    ]

    sections = [
        "# BLM Timing Review",
        "",
        f"- sampleCount: {review.get('sampleCount', 0)}",
        f"- applyRecommendation: {review.get('applyRecommendation', '')}",
        "",
        "## Observed order",
        markdown_table(("step", "median"), observed_rows),
        "",
        "## Key timing",
        markdown_table(("distribution", "median", "p75", "p90"), evidence_rows),
        "",
        "## Review notes",
        markdown_table(("item", "recommendation", "reason"), review_rows),
        "",
        "## Recommendation",
        "- Do not auto-apply this review to BLM.cs.",
        "- Compare the timing evidence with the current BLM planner before any separate code change.",
        "",
    ]
    return "\n".join(sections)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Export review-only BLM timing evidence.")
    parser.add_argument("--specific", required=True)
    parser.add_argument("--distributions", required=True)
    parser.add_argument("--out-json", required=True)
    parser.add_argument("--out-md", required=True)
    args = parser.parse_args(argv)

    review = export_timing_review(read_json(args.specific), read_json(args.distributions))
    write_json(args.out_json, review)
    write_text(args.out_md, export_markdown(review))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
