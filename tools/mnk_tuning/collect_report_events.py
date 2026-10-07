#!/usr/bin/env python3
"""Collect one report fight's public FF Logs event data into a local cache."""

from __future__ import annotations

import argparse
import sys
import urllib.error
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from fflogs_client import fetch_access_token, graphql_request, read_credentials
from public_data_cache import cache_exists, events_cache_path, read_cache, write_cache


DEFAULT_DATA_TYPE = "Casts"
DEFAULT_PAGE_LIMIT = 1000
DEFAULT_MAX_EVENTS = 5000


@dataclass(frozen=True)
class ReportEventsOptions:
    code: str
    fight_id: int
    data_type: str
    page_limit: int
    max_events: int
    start_time: float | None
    end_time: float | None
    source_id: int | None
    target_id: int | None
    ability_id: float | None
    out: str | None
    use_cache: bool


def build_report_events_query() -> str:
    query_path = Path(__file__).resolve().parent / "sample_graphql" / "report_events_collect.graphql"
    with open(query_path, "r", encoding="utf-8") as handle:
        return handle.read()


def output_path_for(options: ReportEventsOptions) -> Path:
    if options.out:
        return Path(options.out)
    return events_cache_path(options.code, options.fight_id, options.data_type)


def number_or_none(value: Any) -> float | int | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return value
    return None


def string_or_none(value: Any) -> str | None:
    return value if isinstance(value, str) else None


def summarize_actor(actor: Any) -> dict[str, Any] | None:
    if not isinstance(actor, dict):
        return None
    return {
        "id": number_or_none(actor.get("id")),
        "gameID": number_or_none(actor.get("gameID")),
        "type": string_or_none(actor.get("type")),
        "subType": string_or_none(actor.get("subType")),
    }


def summarize_ability(ability: Any) -> dict[str, Any] | None:
    if not isinstance(ability, dict):
        return None
    return {
        "gameID": number_or_none(ability.get("gameID")),
        "type": string_or_none(ability.get("type")),
    }


def summarize_event(event: Any) -> dict[str, Any] | None:
    if not isinstance(event, dict):
        return None
    return {
        "timestamp": number_or_none(event.get("timestamp")),
        "type": string_or_none(event.get("type")),
        "sourceID": number_or_none(event.get("sourceID")),
        "targetID": number_or_none(event.get("targetID")),
        "abilityGameID": number_or_none(event.get("abilityGameID")),
        "fight": number_or_none(event.get("fight")),
    }


def extract_report_payload(response: dict[str, Any]) -> tuple[dict[str, Any], dict[str, Any], list[Any], float | None]:
    data = response.get("data")
    if not isinstance(data, dict):
        raise RuntimeError("report events response must contain a JSON object at data")

    report_data = data.get("reportData")
    if not isinstance(report_data, dict):
        raise RuntimeError("report events response must contain data.reportData")

    report = report_data.get("report")
    if report is None:
        raise RuntimeError("FF Logs reportData.report returned null")
    if not isinstance(report, dict):
        raise RuntimeError("FF Logs reportData.report was not a JSON object")

    master_data = report.get("masterData")
    if not isinstance(master_data, dict):
        raise RuntimeError("FF Logs report.masterData was not a JSON object")

    events = report.get("events")
    if not isinstance(events, dict):
        raise RuntimeError("FF Logs report.events was not a JSON object")

    event_data = events.get("data")
    if not isinstance(event_data, list):
        raise RuntimeError("FF Logs report.events.data was not a list")

    next_page_timestamp = events.get("nextPageTimestamp")
    if next_page_timestamp is not None and not isinstance(next_page_timestamp, (int, float)):
        raise RuntimeError("FF Logs report.events.nextPageTimestamp was not numeric or null")

    return report, master_data, event_data, next_page_timestamp


def build_master_data(master_data: dict[str, Any]) -> dict[str, list[dict[str, Any]]]:
    actors: list[dict[str, Any]] = []
    raw_actors = master_data.get("actors")
    if isinstance(raw_actors, list):
        for actor in raw_actors:
            summarized = summarize_actor(actor)
            if summarized is not None:
                actors.append(summarized)

    abilities: list[dict[str, Any]] = []
    raw_abilities = master_data.get("abilities")
    if isinstance(raw_abilities, list):
        for ability in raw_abilities:
            summarized = summarize_ability(ability)
            if summarized is not None:
                abilities.append(summarized)

    return {
        "actors": actors,
        "abilities": abilities,
    }


def collect_report_events(options: ReportEventsOptions) -> dict[str, Any]:
    path = output_path_for(options)
    if options.use_cache and cache_exists(path):
        cached = read_cache(path)
        if not isinstance(cached, dict):
            raise RuntimeError("cached report events payload must be a JSON object")
        return cached

    client_id, client_secret = read_credentials()
    try:
        access_token = fetch_access_token(client_id, client_secret)
    except urllib.error.HTTPError as error:
        if error.code == 401:
            raise RuntimeError("FF Logs authentication failed") from error
        raise

    query = build_report_events_query()
    start_time = options.start_time
    pages_fetched = 0
    next_page_timestamp: float | None = None
    master_data_summary: dict[str, list[dict[str, Any]]] | None = None
    events: list[dict[str, Any]] = []

    while len(events) < options.max_events:
        variables = {
            "code": options.code,
            "fightIDs": [options.fight_id],
            "dataType": options.data_type,
            "startTime": start_time,
            "endTime": options.end_time,
            "sourceID": options.source_id,
            "targetID": options.target_id,
            "abilityID": options.ability_id,
            "limit": options.page_limit,
        }
        response = graphql_request(access_token, query, variables)
        _, master_data, event_data, next_page_timestamp = extract_report_payload(response)
        pages_fetched += 1

        if master_data_summary is None:
            master_data_summary = build_master_data(master_data)

        for event in event_data:
            if len(events) >= options.max_events:
                break
            summarized = summarize_event(event)
            if summarized is not None:
                events.append(summarized)

        if next_page_timestamp is None:
            break
        if not event_data:
            break
        if start_time == next_page_timestamp:
            break
        start_time = float(next_page_timestamp)

    return {
        "reportCode": options.code,
        "fightID": options.fight_id,
        "dataType": options.data_type,
        "pageLimit": options.page_limit,
        "maxEvents": options.max_events,
        "eventCount": len(events),
        "pagesFetched": pages_fetched,
        "nextPageTimestamp": next_page_timestamp,
        "masterData": master_data_summary or {"actors": [], "abilities": []},
        "events": events,
    }


def parse_args(argv: list[str]) -> ReportEventsOptions:
    parser = argparse.ArgumentParser(description="Collect one FF Logs report fight's event data.")
    parser.add_argument("--code", required=True)
    parser.add_argument("--fight-id", type=int, required=True)
    parser.add_argument("--data-type", default=DEFAULT_DATA_TYPE)
    parser.add_argument("--page-limit", type=int, default=DEFAULT_PAGE_LIMIT)
    parser.add_argument("--max-events", type=int, default=DEFAULT_MAX_EVENTS)
    parser.add_argument("--start-time", type=float)
    parser.add_argument("--end-time", type=float)
    parser.add_argument("--source-id", type=int)
    parser.add_argument("--target-id", type=int)
    parser.add_argument("--ability-id", type=float)
    parser.add_argument("--out")
    parser.add_argument("--use-cache", action="store_true")
    args = parser.parse_args(argv)
    return ReportEventsOptions(
        code=args.code,
        fight_id=args.fight_id,
        data_type=args.data_type,
        page_limit=args.page_limit,
        max_events=args.max_events,
        start_time=args.start_time,
        end_time=args.end_time,
        source_id=args.source_id,
        target_id=args.target_id,
        ability_id=args.ability_id,
        out=args.out,
        use_cache=args.use_cache,
    )


def main(argv: list[str]) -> int:
    options = parse_args(argv)
    payload = collect_report_events(options)
    path = output_path_for(options)
    write_cache(path, payload)
    print(f"output={path.name}")
    print(f"eventCount={payload.get('eventCount')}")
    print(f"pagesFetched={payload.get('pagesFetched')}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
