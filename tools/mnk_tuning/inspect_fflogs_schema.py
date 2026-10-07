#!/usr/bin/env python3
"""Run a small FF Logs GraphQL probe and save the JSON response."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any

from fflogs_client import fetch_access_token, graphql_request, read_credentials


def read_text(path: str) -> str:
    with open(path, "r", encoding="utf-8") as handle:
        return handle.read()


def write_json(path: str, payload: dict[str, Any]) -> None:
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def run_query_file(query_file: str) -> dict[str, Any]:
    client_id, client_secret = read_credentials()
    access_token = fetch_access_token(client_id, client_secret)
    return graphql_request(access_token, read_text(query_file))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Run an FF Logs GraphQL probe query.")
    parser.add_argument("--query-file", required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args(argv)

    write_json(args.out, run_query_file(args.query_file))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

