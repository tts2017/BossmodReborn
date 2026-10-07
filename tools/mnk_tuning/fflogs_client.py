"""Minimal FF Logs API v2 client helpers for offline MNK tuning tools."""

from __future__ import annotations

import base64
import json
import os
import urllib.parse
import urllib.request
from typing import Any


TOKEN_URL = "https://www.fflogs.com/oauth/token"
GRAPHQL_URL = "https://www.fflogs.com/api/v2/client"


def require_env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise RuntimeError(f"missing required environment variable: {name}")
    return value


def read_credentials() -> tuple[str, str]:
    return require_env("FFLOGS_CLIENT_ID"), require_env("FFLOGS_CLIENT_SECRET")


def fetch_access_token(client_id: str, client_secret: str) -> str:
    body = urllib.parse.urlencode({"grant_type": "client_credentials"}).encode("ascii")
    basic = base64.b64encode(f"{client_id}:{client_secret}".encode("utf-8")).decode("ascii")
    request = urllib.request.Request(
        TOKEN_URL,
        data=body,
        headers={
            "Authorization": f"Basic {basic}",
            "Content-Type": "application/x-www-form-urlencoded",
            "Accept": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(request) as response:
        payload = json.load(response)

    token = payload.get("access_token")
    if not isinstance(token, str) or not token:
        raise RuntimeError("FF Logs token response did not contain access_token")
    return token


def summarize_graphql_errors(errors: Any) -> list[dict[str, Any]]:
    if not isinstance(errors, list):
        return [{"message": str(errors), "path": None, "locations": None}]

    safe_errors: list[dict[str, Any]] = []
    for error in errors:
        if not isinstance(error, dict):
            safe_errors.append({"message": str(error), "path": None, "locations": None})
            continue

        safe_errors.append(
            {
                "message": error.get("message"),
                "path": error.get("path"),
                "locations": error.get("locations"),
            }
        )
    return safe_errors


def graphql_request(access_token: str, query: str, variables: dict[str, Any] | None = None) -> dict[str, Any]:
    body = json.dumps({"query": query, "variables": variables or {}}, separators=(",", ":")).encode("utf-8")
    request = urllib.request.Request(
        GRAPHQL_URL,
        data=body,
        headers={
            "Authorization": f"Bearer {access_token}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
        method="POST",
    )
    with urllib.request.urlopen(request) as response:
        payload = json.load(response)

    if not isinstance(payload, dict):
        raise RuntimeError("FF Logs GraphQL response was not a JSON object")
    if payload.get("errors"):
        safe_errors = summarize_graphql_errors(payload.get("errors"))
        raise RuntimeError(f"FF Logs GraphQL response contained errors: {safe_errors}")
    return payload
