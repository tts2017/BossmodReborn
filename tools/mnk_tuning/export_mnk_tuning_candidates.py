#!/usr/bin/env python3
"""Export review-only MNKTuningProfile candidate values."""

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

OBSERVED_KEYS = (
    "riddleOfFireFirstUseMedian",
    "brotherhoodFirstUseMedian",
    "perfectBalanceFirstUseMedian",
    "riddleOfWindFirstUseMedian",
    "potionFirstUseMedian",
    "rofBrotherhoodDeltaMedian",
    "pbBeforeRoFDeltaMedian",
    "rowBeforeRoFDeltaMedian",
    "lastRoFFromFightEndMedian",
    "lastBrotherhoodFromFightEndMedian",
    "lastPotionFromFightEndMedian",
    "lastRoWFromFightEndMedian",
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


def profile_value(profile: dict[str, Any], key: str) -> float | int | None:
    return number_or_none(profile.get(key))


def observed_value(summary: dict[str, Any], key: str) -> float | int | None:
    return number_or_none(summary.get(key))


def direct(value: float | int | None, source: str) -> dict[str, Any]:
    return {
        "value": value,
        "confidence": "direct",
        "source": source,
    }


def candidate(value: float | int | None, source: str, note: str) -> dict[str, Any]:
    return {
        "value": value,
        "confidence": "candidate",
        "source": source,
        "note": note,
    }


def unmapped(source: str, observed: float | int | None, note: str) -> dict[str, Any]:
    return {
        "value": None,
        "confidence": "unmapped",
        "source": source,
        "observedMedianSeconds": observed,
        "note": note,
    }


def export_candidates(common_profile: dict[str, Any], mnk_specific: dict[str, Any]) -> dict[str, Any]:
    profile = common_profile.get("profile")
    if not isinstance(profile, dict):
        raise RuntimeError("common profile must contain a profile object")

    summary = mnk_specific.get("summary")
    if not isinstance(summary, dict):
        raise RuntimeError("MNK specific features must contain a summary object")

    common_count = common_profile.get("sampleCount")
    mnk_count = mnk_specific.get("sampleCount")
    if isinstance(common_count, int) and isinstance(mnk_count, int) and common_count != mnk_count:
        raise RuntimeError("common profile and MNK specific sample counts do not match")
    sample_count = common_count if isinstance(common_count, int) else mnk_count

    observed = {key: observed_value(summary, key) for key in OBSERVED_KEYS}

    return {
        "job": "MNK",
        "source": "top200_public_logs_candidate_review",
        "sampleCount": sample_count if isinstance(sample_count, int) else 0,
        "candidates": {
            "MajorBurstMeleeSafeGCDs": direct(
                profile_value(profile, "MajorBurstSafeGCDs"),
                "common.MajorBurstSafeGCDs",
            ),
            "PBMeleeSafeGCDs": direct(
                profile_value(profile, "MinorBurstSafeGCDs"),
                "common.MinorBurstSafeGCDs",
            ),
            "EvenPreRoFPBStartThreshold": unmapped(
                "mnk.pbBeforeRoFDeltaMedian",
                observed["pbBeforeRoFDeltaMedian"],
                "Needs MNK.cs threshold semantics before conversion.",
            ),
            "RoFBrotherhoodResyncWindow": unmapped(
                "mnk.rofBrotherhoodDeltaMedian",
                observed["rofBrotherhoodDeltaMedian"],
                "Observed first-use delta is not equivalent to the resync window threshold.",
            ),
            "TargetAvailableUptimeRequired": direct(
                profile_value(profile, "TargetAvailableUptimeRequired"),
                "common.TargetAvailableUptimeRequired",
            ),
            "PotionFutureWindowLeeway": direct(
                profile_value(profile, "PotionFutureWindowLeeway"),
                "common.PotionFutureWindowLeeway",
            ),
            "RoWTwoMinuteHoldWindow": candidate(
                profile_value(profile, "DowntimeHoldWindow"),
                "common.DowntimeHoldWindow",
                "Candidate only; verify RoW hold semantics before MNK.cs use.",
            ),
            "RoWUptimeRequired": unmapped(
                "unmapped",
                None,
                "Needs a RoW-specific uptime feature before conversion.",
            ),
            "RoWEndBurnLeeway": candidate(
                profile_value(profile, "FightEndCommitWindow"),
                "common.FightEndCommitWindow",
                "Candidate only; verify fight-end RoW burn semantics before MNK.cs use.",
            ),
            "LastRoFWindowLeeway": candidate(
                profile_value(profile, "LastMajorBuffWindowLeeway"),
                "common.LastMajorBuffWindowLeeway",
                "Candidate only; common major-buff leeway does not distinguish RoF from Brotherhood.",
            ),
            "LastBrotherhoodWindowLeeway": unmapped(
                "common.LastMajorBuffWindowLeeway",
                observed["lastBrotherhoodFromFightEndMedian"],
                "Do not copy common major-buff leeway directly; current default differs from LastRoFWindowLeeway.",
            ),
            "LastPotionWindowLeeway": direct(
                profile_value(profile, "LastPotionWindowLeeway"),
                "common.LastPotionWindowLeeway",
            ),
        },
        "observed": observed,
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Export review-only MNKTuningProfile candidate values.")
    parser.add_argument("--common-profile", required=True)
    parser.add_argument("--mnk-specific", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    payload = export_candidates(read_json(args.common_profile), read_json(args.mnk_specific))
    write_json(args.out, payload)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
