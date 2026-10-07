#!/usr/bin/env python3
"""Summarize JSON structure without preserving scalar values."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any


def read_json(path: str) -> Any:
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def merge_shapes(left: Any, right: Any) -> Any:
    if left == right:
        return left

    if isinstance(left, dict) and isinstance(right, dict):
        merged: dict[str, Any] = {}
        for key in sorted(set(left) | set(right)):
            if key in left and key in right:
                merged[key] = merge_shapes(left[key], right[key])
            elif key in left:
                merged[key] = left[key]
            else:
                merged[key] = right[key]
        return merged

    if isinstance(left, list) and isinstance(right, list):
        left_item = left[0] if left else None
        right_item = right[0] if right else None
        if left_item is None:
            return right
        if right_item is None:
            return left
        return [merge_shapes(left_item, right_item)]

    options: list[Any] = []
    for item in (left, right):
        if item not in options:
            options.append(item)
    return options


def summarize_shape(value: Any, max_list_items: int, max_depth: int, depth: int = 0) -> Any:
    if depth >= max_depth:
        return "max_depth"

    if isinstance(value, dict):
        return {
            str(key): summarize_shape(item, max_list_items, max_depth, depth + 1)
            for key, item in sorted(value.items())
        }

    if isinstance(value, list):
        item_shape: Any = None
        for item in value[:max_list_items]:
            shape = summarize_shape(item, max_list_items, max_depth, depth + 1)
            item_shape = shape if item_shape is None else merge_shapes(item_shape, shape)
        return {
            "length": len(value),
            "items": item_shape if item_shape is not None else "empty",
        }

    if isinstance(value, bool):
        return "bool"
    if isinstance(value, (int, float)):
        return "number"
    if isinstance(value, str):
        return "string"
    if value is None:
        return "null"

    return type(value).__name__


def summarize_json_shape(payload: Any, max_list_items: int = 3, max_depth: int = 8) -> dict[str, Any]:
    return {
        "shape": summarize_shape(payload, max_list_items, max_depth),
    }


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Summarize JSON shape without preserving scalar values.")
    parser.add_argument("--in", dest="input_path", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--max-list-items", type=int, default=3)
    parser.add_argument("--max-depth", type=int, default=8)
    args = parser.parse_args(argv)

    summary = summarize_json_shape(read_json(args.input_path), args.max_list_items, args.max_depth)
    write_json(args.out, summary)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
