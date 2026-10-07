#!/usr/bin/env python3
"""Export review-only NIN timing evidence."""

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

ORDER_FIELDS = (
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

EVIDENCE_FIELDS = (
    "dokumoriKunaiDelta",
    "kassatsuKunaiDelta",
    "kunaiHyoshoDelta",
    "tcjMeisuiDelta",
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


def number_or_none(value: Any) -> float | int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return value
    return None


def distribution(distributions: dict[str, Any], key: str) -> dict[str, Any]:
    value = distributions.get(key)
    return value if isinstance(value, dict) else {}


def evidence(distributions: dict[str, Any], key: str) -> dict[str, Any]:
    item = distribution(distributions, key)
    return {
        "median": number_or_none(item.get("median")),
        "p75": number_or_none(item.get("p75")),
        "p90": number_or_none(item.get("p90")),
    }


def observed_order(distributions: dict[str, Any]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for step, distribution_key in ORDER_FIELDS:
        first_use = number_or_none(distribution(distributions, distribution_key).get("median"))
        if first_use is not None:
            rows.append({
                "step": step,
                "firstUseMedian": first_use,
            })
    return sorted(rows, key=lambda row: float(row["firstUseMedian"]))


def format_seconds(value: Any) -> str:
    number = number_or_none(value)
    if number is None:
        return "unknown"
    return f"{float(number):.1f}s"


def export_timing_review(specific_payload: dict[str, Any], distributions_payload: dict[str, Any]) -> dict[str, Any]:
    distributions = distributions_payload.get("distributions")
    if not isinstance(distributions, dict):
        raise RuntimeError("distribution input must contain a distributions object")

    timing_evidence = {key: evidence(distributions, key) for key in EVIDENCE_FIELDS}

    return {
        "job": "NIN",
        "source": "top200_public_logs_nin_timing_review",
        "sampleCount": specific_payload.get("sampleCount") if isinstance(specific_payload.get("sampleCount"), int) else 0,
        "observedOrder": observed_order(distributions),
        "timingEvidence": timing_evidence,
        "review": {
            "dokumoriToKunai": {
                "recommendation": "late_but_before_core_followup",
                "applyRecommendation": "hold",
                "reason": f"Top200 median Kunai occurs about {format_seconds(timing_evidence['dokumoriKunaiDelta']['median'])} after Dokumori.",
            },
            "kassatsuToKunai": {
                "recommendation": "review_current_burst_state_machine",
                "applyRecommendation": "hold",
                "reason": f"Top200 median Kunai occurs about {format_seconds(timing_evidence['kassatsuKunaiDelta']['median'])} after Kassatsu.",
            },
            "kunaiToHyosho": {
                "recommendation": "keep_hyosho_immediately_after_kunai",
                "applyRecommendation": "hold",
                "reason": f"Top200 median Hyosho occurs about {format_seconds(timing_evidence['kunaiHyoshoDelta']['median'])} after Kunai.",
            },
            "tcjMeisui": {
                "recommendation": "keep_meisui_after_tcj",
                "applyRecommendation": "hold",
                "reason": f"Top200 median Meisui occurs about {format_seconds(timing_evidence['tcjMeisuiDelta']['median'])} after TCJ.",
            },
        },
        "applyRecommendation": "hold",
        "reason": "Do not auto-apply to NIN.cs until current NIN burst state machine semantics are reviewed.",
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Export review-only NIN timing evidence.")
    parser.add_argument("--specific", required=True)
    parser.add_argument("--distributions", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_json(args.out, export_timing_review(read_json(args.specific), read_json(args.distributions)))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
