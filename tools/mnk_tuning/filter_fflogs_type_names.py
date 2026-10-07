#!/usr/bin/env python3
"""Filter FF Logs GraphQL schema type names for ranking query planning."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any


KEYWORDS = (
    "World",
    "Encounter",
    "Report",
    "Fight",
    "Event",
    "Ranking",
    "Rank",
    "Character",
    "Zone",
    "Partition",
    "Table",
    "Actor",
    "Master",
)

FORBIDDEN_TERMS = (
    "client_secret",
    "access_token",
    "Authorization",
    "Bearer",
)


def contains_forbidden_term(value: Any) -> bool:
    if isinstance(value, dict):
        for key, item in value.items():
            if any(term.lower() in str(key).lower() for term in FORBIDDEN_TERMS):
                return True
            if contains_forbidden_term(item):
                return True
        return False

    if isinstance(value, list):
        return any(contains_forbidden_term(item) for item in value)

    if isinstance(value, str):
        return any(term.lower() in value.lower() for term in FORBIDDEN_TERMS)

    return False


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("type name input must be a JSON object")
    if contains_forbidden_term(payload):
        raise RuntimeError("type name input contains a forbidden secret-related term")
    return payload


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def filter_type_names(response: dict[str, Any]) -> dict[str, list[dict[str, Any]]]:
    data = response.get("data")
    if not isinstance(data, dict):
        raise RuntimeError("type name response must contain a JSON object at data")

    schema = data.get("__schema")
    if not isinstance(schema, dict):
        raise RuntimeError("type name response must contain data.__schema")

    types = schema.get("types")
    if not isinstance(types, list):
        raise RuntimeError("type name response must contain data.__schema.types")

    candidates: list[dict[str, Any]] = []
    for type_data in types:
        if not isinstance(type_data, dict):
            continue

        name = type_data.get("name")
        kind = type_data.get("kind")
        if not isinstance(name, str) or not isinstance(kind, str):
            continue
        if any(keyword.lower() in name.lower() for keyword in KEYWORDS):
            candidates.append({"name": name, "kind": kind})

    return {"candidates": candidates}


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Filter FF Logs GraphQL schema type names.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_json(args.out, filter_type_names(read_json(args.input_path)))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
