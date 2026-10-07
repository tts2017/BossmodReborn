#!/usr/bin/env python3
"""Summarize FF Logs GraphQL type probe output for query planning."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any


NAMED_TYPE_KINDS = {"SCALAR", "OBJECT", "ENUM", "INPUT_OBJECT", "INTERFACE", "UNION"}


def strip_named_type_prefix(rendered: str) -> str:
    for kind in NAMED_TYPE_KINDS:
        prefix = f"{kind} "
        if rendered.startswith(prefix):
            return rendered[len(prefix):]
    return rendered


def render_type_ref(type_ref: Any, bare: bool = False) -> str | None:
    if not isinstance(type_ref, dict):
        return None

    kind = type_ref.get("kind")
    name = type_ref.get("name")
    of_type = type_ref.get("ofType")

    if kind == "NON_NULL":
        inner = render_type_ref(of_type, bare=False)
        if inner is None:
            return "NON_NULL !" if not bare else "!"
        inner = strip_named_type_prefix(inner)
        return f"{inner}!" if bare else f"NON_NULL {inner}!"

    if kind == "LIST":
        inner = render_type_ref(of_type, bare=True)
        inner_text = inner if inner is not None else "Unknown"
        return f"[{inner_text}]" if bare else f"LIST [{inner_text}]"

    if isinstance(kind, str) and isinstance(name, str):
        return name if bare else f"{kind} {name}"

    if isinstance(kind, str):
        return kind if bare else kind

    return None


def summarize_args(args: Any) -> list[dict[str, Any]]:
    if not isinstance(args, list):
        return []

    result: list[dict[str, Any]] = []
    for arg in args:
        if not isinstance(arg, dict):
            continue
        result.append({
            "name": arg.get("name"),
            "type": render_type_ref(arg.get("type")),
        })
    return result


def summarize_fields(fields: Any) -> list[dict[str, Any]]:
    if not isinstance(fields, list):
        return []

    result: list[dict[str, Any]] = []
    for field in fields:
        if not isinstance(field, dict):
            continue
        result.append({
            "name": field.get("name"),
            "args": summarize_args(field.get("args")),
            "type": render_type_ref(field.get("type")),
        })
    return result


def summarize_enum_values(enum_values: Any) -> list[str]:
    if not isinstance(enum_values, list):
        return []

    result: list[str] = []
    for enum_value in enum_values:
        if not isinstance(enum_value, dict):
            continue
        name = enum_value.get("name")
        if isinstance(name, str):
            result.append(name)
    return result


def summarize_type(type_data: Any) -> dict[str, Any]:
    if not isinstance(type_data, dict):
        return {
            "name": None,
            "kind": None,
            "fields": [],
            "enumValues": [],
        }

    return {
        "name": type_data.get("name"),
        "kind": type_data.get("kind"),
        "fields": summarize_fields(type_data.get("fields")),
        "enumValues": summarize_enum_values(type_data.get("enumValues")),
    }


def summarize_probe_response(response: dict[str, Any]) -> dict[str, Any]:
    data = response.get("data")
    if not isinstance(data, dict):
        raise RuntimeError("schema probe response must contain a JSON object at data")

    return {
        "types": {
            alias: summarize_type(type_data)
            for alias, type_data in data.items()
        }
    }


def read_json(path: str) -> dict[str, Any]:
    with open(path, "r", encoding="utf-8") as handle:
        payload = json.load(handle)
    if not isinstance(payload, dict):
        raise RuntimeError("schema probe input must be a JSON object")
    return payload


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Summarize an FF Logs GraphQL schema probe response.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    summary = summarize_probe_response(read_json(args.input_path))
    write_json(args.out, summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
