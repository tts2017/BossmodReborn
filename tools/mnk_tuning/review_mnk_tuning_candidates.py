#!/usr/bin/env python3
"""Render MNK tuning candidates as a human-review Markdown report."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any


FIELD_ORDER = (
    "MajorBurstMeleeSafeGCDs",
    "PBMeleeSafeGCDs",
    "EvenPreRoFPBStartThreshold",
    "RoFBrotherhoodResyncWindow",
    "TargetAvailableUptimeRequired",
    "PotionFutureWindowLeeway",
    "RoWTwoMinuteHoldWindow",
    "RoWUptimeRequired",
    "RoWEndBurnLeeway",
    "LastRoFWindowLeeway",
    "LastBrotherhoodWindowLeeway",
    "LastPotionWindowLeeway",
)

CANDIDATE_REASONS = {
    "RoWTwoMinuteHoldWindow": "Needs RoW-specific validation",
    "RoWEndBurnLeeway": "Needs RoW end-burn validation",
    "LastRoFWindowLeeway": "Candidate, but compare with current default before applying",
}

UNMAPPED_REQUIREMENTS = {
    "EvenPreRoFPBStartThreshold": "needs PB/RoF threshold semantics",
    "RoFBrotherhoodResyncWindow": "needs conversion from delta distribution to allowed window",
    "RoWUptimeRequired": "needs uptime/downtime observation, not just cast timing",
    "LastBrotherhoodWindowLeeway": "cannot reuse LastMajorBuffWindowLeeway because current default differs significantly",
}

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


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("candidate input must be a JSON object")
    return payload


def format_value(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, bool):
        return str(value)
    if isinstance(value, (int, float)):
        return f"{float(value):.6g}"
    return escape_markdown(str(value))


def escape_markdown(value: str) -> str:
    return value.replace("|", "\\|").replace("\n", " ")


def table(headers: list[str], rows: list[list[str]]) -> list[str]:
    result = [
        "| " + " | ".join(headers) + " |",
        "| " + " | ".join("---" for _ in headers) + " |",
    ]
    for row in rows:
        result.append("| " + " | ".join(row) + " |")
    return result


def candidates_by_confidence(candidates: dict[str, Any], confidence: str) -> list[tuple[str, dict[str, Any]]]:
    result: list[tuple[str, dict[str, Any]]] = []
    for field in FIELD_ORDER:
        item = candidates.get(field)
        if isinstance(item, dict) and item.get("confidence") == confidence:
            result.append((field, item))
    return result


def render_review(payload: dict[str, Any]) -> str:
    candidates = payload.get("candidates")
    if not isinstance(candidates, dict):
        raise RuntimeError("candidate input must contain a candidates object")

    observed = payload.get("observed")
    observed_values = observed if isinstance(observed, dict) else {}

    lines: list[str] = [
        "# MNK Tuning Candidate Review",
        "",
        "## Summary",
        f"- job: {escape_markdown(str(payload.get('job', '')))}",
        f"- sampleCount: {format_value(payload.get('sampleCount'))}",
        f"- source: {escape_markdown(str(payload.get('source', '')))}",
        "",
        "## Direct candidates",
    ]

    direct_rows = []
    for field, item in candidates_by_confidence(candidates, "direct"):
        direct_rows.append([
            field,
            format_value(item.get("value")),
            escape_markdown(str(item.get("source", ""))),
            "Safe but no behavior change if equal to Default",
        ])
    lines.extend(table(["MNKTuningProfile field", "value", "source", "decision"], direct_rows))

    lines.extend(["", "## Candidate only"])
    candidate_rows = []
    for field, item in candidates_by_confidence(candidates, "candidate"):
        candidate_rows.append([
            field,
            format_value(item.get("value")),
            escape_markdown(str(item.get("source", ""))),
            CANDIDATE_REASONS.get(field, "Needs explicit validation before applying"),
        ])
    lines.extend(table(["MNKTuningProfile field", "value", "source", "reason to hold"], candidate_rows))

    lines.extend(["", "## Unmapped"])
    unmapped_rows = []
    for field, item in candidates_by_confidence(candidates, "unmapped"):
        unmapped_rows.append([
            field,
            escape_markdown(str(item.get("source", ""))),
            format_value(item.get("observedMedianSeconds")),
            UNMAPPED_REQUIREMENTS.get(field, "needs additional feature data"),
        ])
    lines.extend(table(["MNKTuningProfile field", "observed source", "observed value if any", "missing requirement"], unmapped_rows))

    lines.extend(["", "## Observed MNK timings"])
    observed_rows = []
    for key in sorted(observed_values):
        observed_rows.append([escape_markdown(str(key)), format_value(observed_values.get(key))])
    lines.extend(table(["metric", "value"], observed_rows))

    lines.extend([
        "",
        "## Recommendation",
        "- Do not apply candidate JSON directly to MNK.cs yet.",
        "- Direct values currently match default profile and can remain unchanged.",
        "- Add MNK-specific conversion logic before changing candidate/unmapped fields.",
        "- Next step: export MNK-specific derived thresholds with explicit formulas.",
        "",
    ])

    return "\n".join(lines)


def write_text(path: str, content: str) -> None:
    found = [item for item in FORBIDDEN_TEXT if item in content]
    if found:
        raise RuntimeError(f"review output contains forbidden text: {', '.join(found)}")
    with open(path, "w", encoding="utf-8") as handle:
        handle.write(content)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Render MNK tuning candidates as a Markdown review.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_text(args.out, render_review(read_json(args.input_path)))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
