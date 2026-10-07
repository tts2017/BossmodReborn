"""Local-only cache helpers for public FF Logs tuning inputs."""

from __future__ import annotations

import json
import re
from pathlib import Path
from typing import Any

from jobs import normalize_job


CACHE_ROOT = Path(__file__).resolve().parent / "cache" / "public_top"
EVENTS_CACHE_ROOT = Path(__file__).resolve().parent / "cache" / "report_events"
FORBIDDEN_KEYS = {
    "client_secret",
    "access_token",
    "authorization",
    "bearer",
    "reporturl",
    "reportURL".casefold(),
    "charactername",
    "playername",
    "guildname",
    "servername",
    "name",
    "server",
    "guild",
    "lodestoneid",
}


def sanitize_path_part(value: str) -> str:
    sanitized = re.sub(r"[^A-Za-z0-9_.-]+", "_", value.strip())
    return sanitized or "unknown"


def cache_key(encounter_id: int, partition: int, difficulty: int, metric: str, job: str, limit: int) -> dict[str, Any]:
    return {
        "encounter_id": encounter_id,
        "partition": partition,
        "difficulty": difficulty,
        "metric": metric,
        "job": normalize_job(job),
        "limit": limit,
    }


def cache_path(encounter_id: int, partition: int, difficulty: int, metric: str, job: str, limit: int, root: Path | None = None) -> Path:
    key = cache_key(encounter_id, partition, difficulty, metric, job, limit)
    base = root or CACHE_ROOT
    return (
        base
        / f"encounter_{key['encounter_id']}"
        / f"partition_{key['partition']}"
        / f"difficulty_{key['difficulty']}"
        / f"metric_{sanitize_path_part(str(key['metric']))}"
        / f"{key['job']}_top{key['limit']}.json"
    )


def events_cache_path(report_code: str, fight_id: int, data_type: str, root: Path | None = None) -> Path:
    base = root or EVENTS_CACHE_ROOT
    return (
        base
        / f"report_{sanitize_path_part(report_code)}"
        / f"fight_{fight_id}"
        / f"{sanitize_path_part(data_type)}.json"
    )


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


def validate_cache_payload(payload: Any) -> None:
    forbidden = forbidden_key_paths(payload)
    if forbidden:
        raise RuntimeError(f"cache payload contains forbidden keys: {', '.join(forbidden)}")


def read_cache(path: Path | str) -> Any:
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def write_cache(path: Path | str, payload: Any) -> None:
    validate_cache_payload(payload)
    target = Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with open(target, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def cache_exists(path: Path | str) -> bool:
    return Path(path).is_file()
