#!/usr/bin/env python3
"""Collect Dancing Mad public FF Logs and export timeline-only tuning candidates.

The tool intentionally ignores MNK rotation details. It derives only common
encounter timing signals: targetability, boss damage gaps, phase boundaries,
party raid-buff clusters, and potion timing references.
"""

from __future__ import annotations

import argparse
import json
import math
import sys
import urllib.error
from dataclasses import dataclass, replace
from pathlib import Path
from typing import Any, Iterable

from fflogs_client import fetch_access_token, graphql_request, read_credentials


SCRIPT_DIR = Path(__file__).resolve().parent
DEFAULT_CACHE_DIR = SCRIPT_DIR / "cache" / "dancing_mad_timeline"
DEFAULT_TIMELINE_OUT = SCRIPT_DIR / "dancing_mad_timeline_profile.json"
DEFAULT_PROFILE_OUT = SCRIPT_DIR / "dancing_mad_mnk_profile_candidate.json"
DEFAULT_LIMIT = 200
DEFAULT_DIFFICULTY = None
DEFAULT_PARTITION = 1
DEFAULT_METRIC = "rdps"
DEFAULT_PAGE_SIZE = 8
DEFAULT_EVENT_LIMIT = 10000
DEFAULT_PHASE_GAP_MIN_SECONDS = 8.0
DEFAULT_MAJOR_GAP_MIN_SECONDS = 20.0
DEFAULT_BURST_CLUSTER_SECONDS = 8.0
DEFAULT_BURST_BEFORE_DOWNTIME_SECONDS = 20.0
DEFAULT_MIN_KILL_SECONDS = 300.0
DEFAULT_MAX_KILL_SECONDS = 1200.0
DANCING_MAD_ZONE_ID = 76
DANCING_MAD_NAMES = ("dancing mad", "dancing mad (ultimate)")

EVENT_TYPES = ("DamageDone", "DamageTaken", "Casts", "Buffs")
RAW_EVENT_TYPE = "Events"

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

RAID_BUFF_IDS = {
    118,
    3557,
    3559,
    7396,
    7520,
    1001210,
    16012,
    16536,
    18805,
    24405,
    25785,
    25862,
    25870,
    25882,
    34675,
    37011,
    37012,
    37013,
    34686,
    34687,
}

RAID_BUFF_NAME_PARTS = (
    "battle litany",
    "brotherhood",
    "chain stratagem",
    "dokumori",
    "mug",
    "arcane circle",
    "embolden",
    "searing light",
    "divination",
    "technical finish",
    "devilment",
    "battle voice",
    "radiant finale",
    "starry muse",
    "landscape motif",
    "桃園結義",
    "連環計",
    "ぶんどる",
    "アルケインサークル",
    "エンボルデン",
    "シアリングライト",
    "ディヴィネーション",
    "テクニカルフィニッシュ",
    "バトルボイス",
    "光神のフィナーレ",
)

POTION_NAME_PARTS = (
    "potion",
    "tincture",
    "薬",
    "強化薬",
)

TARGETABILITY_EVENT_TYPES = (
    "targetabilityupdate",
    "targetable",
    "untargetable",
)

SPAWN_DESPAWN_EVENT_TYPES = (
    "spawn",
    "death",
    "combatantinfo",
    "removecombatant",
    "actorupdate",
)

PHASE_EVENT_TYPES = (
    "phase",
    "phasechange",
    "phasestart",
    "phaseend",
)


@dataclass(frozen=True)
class Options:
    zone_id: int | None
    encounter_id: int
    difficulty: int | None
    partition: int
    metric: str
    limit: int
    page_size: int
    event_limit: int
    cache_dir: Path
    timeline_out: Path
    profile_out: Path
    use_cache: bool
    client_id: str | None
    client_secret: str | None
    phase_gap_min_seconds: float
    major_gap_min_seconds: float
    burst_cluster_seconds: float
    burst_before_downtime_seconds: float
    min_kill_seconds: float
    max_kill_seconds: float


@dataclass
class FightTimelineSample:
    kill: bool
    duration: float
    targetable_starts: list[float]
    targetable_ends: list[float]
    damage_gap_starts: list[float]
    damage_gap_ends: list[float]
    damage_gap_durations: list[float]
    major_gap_durations: list[float]
    p2_p3_gap: float | None
    raid_buff_clusters: list[float]
    potion_times: list[float]
    burst_before_downtime_count: int
    burst_after_reappearance_count: int
    downtime_before_next_burst: list[float]
    boss_damage_start: float | None
    boss_damage_end: float | None
    boss_source_phase_groups: list[dict[str, Any]]


class Exclusions:
    def __init__(self) -> None:
        self.by_reason: dict[str, int] = {}

    def add(self, reason: str) -> None:
        self.by_reason[reason] = self.by_reason.get(reason, 0) + 1

    @property
    def total(self) -> int:
        return sum(self.by_reason.values())


def build_rankings_query() -> str:
    return """
query CharacterRankingsCollect(
  $encounterId: Int!
  $difficulty: Int
  $partition: Int
  $metric: CharacterRankingMetricType
  $page: Int
  $partySize: Int
) {
  worldData {
    encounter(id: $encounterId) {
      characterRankings(
        difficulty: $difficulty
        partition: $partition
        metric: $metric
        page: $page
        size: $partySize
        includeCombatantInfo: false
        includeOtherPlayers: true
      )
    }
  }
}
""".strip()


def build_zone_encounters_query() -> str:
    return """
query ZoneEncounterResolve {
  worldData {
    zones {
      id
      name
      encounters {
        id
        name
      }
    }
  }
}
""".strip()


def build_report_events_query() -> str:
    return """
query ReportEventsForTimeline(
  $code: String!
  $fightIDs: [Int]
  $dataType: EventDataType
  $startTime: Float
  $endTime: Float
  $limit: Int
) {
  reportData {
    report(code: $code) {
      startTime
      fights(fightIDs: $fightIDs, translate: true) {
        id
        encounterID
        kill
        startTime
        endTime
      }
      masterData(translate: true) {
        actors {
          id
          gameID
          type
          subType
          name
        }
        abilities {
          gameID
          name
          type
        }
      }
      events(
        fightIDs: $fightIDs
        dataType: $dataType
        startTime: $startTime
        endTime: $endTime
        limit: $limit
        translate: true
        useAbilityIDs: true
        useActorIDs: true
      ) {
        data
        nextPageTimestamp
      }
    }
  }
}
""".strip()


def read_json(path: Path) -> Any:
    with open(path, "r", encoding="utf-8") as handle:
        return json.load(handle)


def write_json(path: Path, payload: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=False, indent=2, sort_keys=True)
        handle.write("\n")


def sanitize_path_part(value: str) -> str:
    return "".join(ch if ch.isalnum() or ch in "._-" else "_" for ch in value).strip("._") or "unknown"


def rankings_cache_path(options: Options) -> Path:
    return (
        options.cache_dir
        / "rankings"
        / f"encounter_{options.encounter_id}_difficulty_{options.difficulty}_partition_{options.partition}_{sanitize_path_part(options.metric)}_top{options.limit}.json"
    )


def event_cache_path(options: Options, report_code: str, fight_id: int, data_type: str) -> Path:
    return options.cache_dir / "events" / sanitize_path_part(report_code) / f"fight_{fight_id}" / f"{sanitize_path_part(data_type)}.json"


def number_or_none(value: Any) -> float | None:
    if isinstance(value, bool):
        return None
    if isinstance(value, (int, float)):
        return float(value)
    return None


def int_or_none(value: Any) -> int | None:
    if isinstance(value, bool):
        return None
    return value if isinstance(value, int) else None


def as_list(value: Any) -> list[Any]:
    return value if isinstance(value, list) else []


def sorted_numbers(values: Iterable[float | None]) -> list[float]:
    return sorted(value for value in values if value is not None and math.isfinite(value))


def nearest_rank(values: list[float], percentile: float) -> float | None:
    sorted_values = sorted_numbers(values)
    if not sorted_values:
        return None
    rank = max(1, math.ceil(percentile / 100.0 * len(sorted_values)))
    return sorted_values[rank - 1]


def distribution(values: list[float]) -> dict[str, Any]:
    sorted_values = sorted_numbers(values)
    if not sorted_values:
        return {
            "sample_count": 0,
            "min": None,
            "p10": None,
            "p25": None,
            "median": None,
            "p75": None,
            "p90": None,
            "p95": None,
            "max": None,
        }
    return {
        "sample_count": len(sorted_values),
        "min": round(sorted_values[0], 3),
        "p10": round(nearest_rank(sorted_values, 10) or 0, 3),
        "p25": round(nearest_rank(sorted_values, 25) or 0, 3),
        "median": round(nearest_rank(sorted_values, 50) or 0, 3),
        "p75": round(nearest_rank(sorted_values, 75) or 0, 3),
        "p90": round(nearest_rank(sorted_values, 90) or 0, 3),
        "p95": round(nearest_rank(sorted_values, 95) or 0, 3),
        "max": round(sorted_values[-1], 3),
    }


def text_contains_any(value: str | None, parts: tuple[str, ...]) -> bool:
    if not value:
        return False
    normalized = value.casefold()
    return any(part.casefold() in normalized for part in parts)


def event_type(event: dict[str, Any]) -> str:
    value = event.get("type")
    return value.casefold() if isinstance(value, str) else ""


def event_timestamp_seconds(event: dict[str, Any], fight_start_ms: float) -> float | None:
    timestamp = number_or_none(event.get("timestamp"))
    if timestamp is None:
        return None
    return (timestamp - fight_start_ms) / 1000.0


def event_ability_id(event: dict[str, Any]) -> int | None:
    ability_id = int_or_none(event.get("abilityGameID"))
    if ability_id is not None:
        return ability_id
    ability = event.get("ability")
    if isinstance(ability, dict):
        return int_or_none(ability.get("gameID"))
    return None


def event_ability_name(event: dict[str, Any], ability_names: dict[int, str]) -> str | None:
    ability = event.get("ability")
    if isinstance(ability, dict):
        name = ability.get("name")
        if isinstance(name, str):
            return name
    name = event.get("abilityName")
    if isinstance(name, str):
        return name
    ability_id = event_ability_id(event)
    if ability_id is not None:
        return ability_names.get(ability_id)
    return None


def source_id(event: dict[str, Any]) -> int | None:
    return int_or_none(event.get("sourceID"))


def target_id(event: dict[str, Any]) -> int | None:
    return int_or_none(event.get("targetID"))


def amount_done(event: dict[str, Any]) -> float | None:
    amount = number_or_none(event.get("amount"))
    if amount is not None:
        return amount
    return number_or_none(event.get("unmitigatedAmount"))


def abilities_from_master(master_data: dict[str, Any]) -> dict[int, str]:
    result: dict[int, str] = {}
    for ability in as_list(master_data.get("abilities")):
        if not isinstance(ability, dict):
            continue
        ability_id = int_or_none(ability.get("gameID"))
        name = ability.get("name")
        if ability_id is not None and isinstance(name, str):
            result[ability_id] = name
    return result


def actors_from_master(master_data: dict[str, Any]) -> list[dict[str, Any]]:
    return [actor for actor in as_list(master_data.get("actors")) if isinstance(actor, dict)]


def boss_actor_ids(master_data: dict[str, Any], damage_events: list[dict[str, Any]]) -> set[int]:
    actor_ids: set[int] = set()
    for actor in actors_from_master(master_data):
        actor_id = int_or_none(actor.get("id"))
        actor_type = actor.get("type")
        if actor_id is None or not isinstance(actor_type, str):
            continue
        if actor_type.casefold() in {"npc", "enemy"}:
            actor_ids.add(actor_id)

    target_counts: dict[int, int] = {}
    for event in damage_events:
        target = target_id(event)
        if target is None:
            continue
        target_counts[target] = target_counts.get(target, 0) + 1
    for target, _ in sorted(target_counts.items(), key=lambda item: item[1], reverse=True)[:12]:
        actor_ids.add(target)
    return actor_ids


def collect_public_rankings(access_token: str, options: Options) -> list[dict[str, Any]]:
    path = rankings_cache_path(options)
    if options.use_cache and path.is_file():
        payload = read_json(path)
        rankings = payload.get("rankings") if isinstance(payload, dict) else []
        return [item for item in as_list(rankings) if isinstance(item, dict)]

    rankings: list[dict[str, Any]] = []
    query = build_rankings_query()
    page = 1
    while len(rankings) < options.limit:
        response = graphql_request(
            access_token,
            query,
            {
                "encounterId": options.encounter_id,
                "difficulty": options.difficulty,
                "partition": options.partition,
                "metric": options.metric,
                "page": page,
                "partySize": options.page_size,
            },
        )
        encounter = response.get("data", {}).get("worldData", {}).get("encounter")
        if not isinstance(encounter, dict):
            raise RuntimeError("FF Logs response did not contain data.worldData.encounter")
        character_rankings = encounter.get("characterRankings")
        if not isinstance(character_rankings, dict):
            raise RuntimeError("FF Logs characterRankings was not an object")
        if character_rankings.get("error"):
            raise RuntimeError(f"FF Logs characterRankings returned error: {character_rankings.get('error')}")

        page_rankings = [item for item in as_list(character_rankings.get("rankings")) if isinstance(item, dict)]
        for ranking in page_rankings:
            if len(rankings) >= options.limit:
                break
            rankings.append(ranking)
        if not page_rankings or not character_rankings.get("hasMorePages"):
            break
        page += 1

    write_json(
        path,
        {
            "encounter_id": options.encounter_id,
            "difficulty": options.difficulty,
            "partition": options.partition,
            "metric": options.metric,
            "limit": options.limit,
            "rankings": rankings,
        },
    )
    return rankings


def zone_encounter_candidates(response: dict[str, Any], zone_id: int) -> list[dict[str, Any]]:
    zones = response.get("data", {}).get("worldData", {}).get("zones")
    candidates: list[dict[str, Any]] = []
    for zone in as_list(zones):
        if not isinstance(zone, dict):
            continue
        if int_or_none(zone.get("id")) != zone_id:
            continue
        zone_name = zone.get("name") if isinstance(zone.get("name"), str) else None
        for encounter in as_list(zone.get("encounters")):
            if not isinstance(encounter, dict):
                continue
            candidates.append(
                {
                    "zone_id": zone_id,
                    "zone_name": zone_name,
                    "encounter_id": int_or_none(encounter.get("id")),
                    "encounter_name": encounter.get("name") if isinstance(encounter.get("name"), str) else None,
                }
            )
    return candidates


def resolve_encounter_from_zone(access_token: str, options: Options) -> tuple[int | None, list[dict[str, Any]]]:
    if options.encounter_id > 0:
        return options.encounter_id, []
    if options.zone_id is None:
        return None, []

    response = graphql_request(access_token, build_zone_encounters_query())
    candidates = zone_encounter_candidates(response, options.zone_id)
    matches = [
        candidate for candidate in candidates
        if isinstance(candidate.get("encounter_name"), str)
        and candidate["encounter_name"].casefold() in DANCING_MAD_NAMES
        and isinstance(candidate.get("encounter_id"), int)
    ]
    if len(matches) == 1:
        return int(matches[0]["encounter_id"]), candidates
    return None, candidates


def unresolved_encounter_payload(options: Options, candidates: list[dict[str, Any]]) -> dict[str, Any]:
    return {
        "source": "fflogs_api_v2_zone_encounter_resolution",
        "status": "encounter_not_resolved",
        "fflogs_zone_id": options.zone_id,
        "fflogs_encounter_id": None,
        "partition": options.partition,
        "message": "Dancing Mad encounter id was not resolved; analysis was not run.",
        "encounter_candidates": candidates,
    }


def report_reference_from_ranking(ranking: dict[str, Any]) -> tuple[str | None, int | None]:
    report = ranking.get("report")
    if not isinstance(report, dict):
        return None, None
    code = report.get("code")
    fight_id = int_or_none(report.get("fightID"))
    return code if isinstance(code, str) and code else None, fight_id


def fetch_report_events_for_type(access_token: str, options: Options, report_code: str, fight_id: int, data_type: str) -> dict[str, Any]:
    path = event_cache_path(options, report_code, fight_id, data_type)
    if options.use_cache and path.is_file():
        cached = read_json(path)
        if isinstance(cached, dict):
            return cached

    query = build_report_events_query()
    variables: dict[str, Any] = {
        "code": report_code,
        "fightIDs": [fight_id],
        "dataType": None if data_type == RAW_EVENT_TYPE else data_type,
        "startTime": None,
        "endTime": None,
        "limit": options.event_limit,
    }
    all_events: list[dict[str, Any]] = []
    report_payload: dict[str, Any] | None = None
    next_page: float | None = None
    while True:
        if next_page is not None:
            variables["startTime"] = next_page
        response = graphql_request(access_token, query, variables)
        report = response.get("data", {}).get("reportData", {}).get("report")
        if not isinstance(report, dict):
            raise RuntimeError("FF Logs report response did not contain report")
        report_payload = report
        paginator = report.get("events")
        if not isinstance(paginator, dict):
            raise RuntimeError("FF Logs report response did not contain events paginator")
        for event in as_list(paginator.get("data")):
            if isinstance(event, dict):
                all_events.append(event)
        raw_next = number_or_none(paginator.get("nextPageTimestamp"))
        if raw_next is None:
            break
        if next_page is not None and raw_next <= next_page:
            break
        next_page = raw_next

    payload = {
        "data_type": data_type,
        "report": {
            "startTime": report_payload.get("startTime") if report_payload else None,
            "fights": report_payload.get("fights") if report_payload else [],
            "masterData": report_payload.get("masterData") if report_payload else {},
        },
        "events": all_events,
    }
    write_json(path, payload)
    return payload


def fetch_report_bundle(access_token: str, options: Options, report_code: str, fight_id: int) -> dict[str, Any]:
    bundle: dict[str, Any] = {"events_by_type": {}}
    for data_type in (RAW_EVENT_TYPE, *EVENT_TYPES):
        try:
            payload = fetch_report_events_for_type(access_token, options, report_code, fight_id, data_type)
        except Exception:
            if data_type == RAW_EVENT_TYPE:
                bundle["events_by_type"][data_type] = []
                continue
            raise
        if "report" not in bundle:
            bundle["report"] = payload.get("report")
        bundle["events_by_type"][data_type] = payload.get("events", [])
    return bundle


def fight_from_bundle(bundle: dict[str, Any]) -> dict[str, Any] | None:
    report = bundle.get("report")
    if not isinstance(report, dict):
        return None
    fights = [fight for fight in as_list(report.get("fights")) if isinstance(fight, dict)]
    return fights[0] if fights else None


def boss_damage_times(events: list[dict[str, Any]], fight_start_ms: float, boss_ids: set[int]) -> list[float]:
    times: list[float] = []
    for event in events:
        if boss_ids and target_id(event) not in boss_ids:
            continue
        amount = amount_done(event)
        if amount is not None and amount <= 0:
            continue
        timestamp = event_timestamp_seconds(event, fight_start_ms)
        if timestamp is not None and timestamp >= 0:
            times.append(round(timestamp, 3))
    return sorted(set(times))


def boss_source_ranges_from_damage_taken(
    damage_taken_events: list[dict[str, Any]],
    fight_start_ms: float,
    master_data: dict[str, Any],
) -> list[dict[str, Any]]:
    actors = {int_or_none(actor.get("id")): actor for actor in actors_from_master(master_data)}
    ranges: dict[int, list[float]] = {}
    for event in damage_taken_events:
        source = source_id(event)
        if source is None:
            continue
        actor = actors.get(source)
        if not isinstance(actor, dict):
            continue
        actor_type = actor.get("type")
        actor_subtype = actor.get("subType")
        if not isinstance(actor_type, str) or actor_type.casefold() != "npc":
            continue
        if not isinstance(actor_subtype, str) or actor_subtype.casefold() != "boss":
            continue
        amount = amount_done(event)
        if amount is not None and amount <= 0:
            continue
        timestamp = event_timestamp_seconds(event, fight_start_ms)
        if timestamp is None or timestamp < 0:
            continue
        ranges.setdefault(source, []).append(timestamp)

    result: list[dict[str, Any]] = []
    for actor_id, times in ranges.items():
        if len(times) < 5:
            continue
        actor = actors.get(actor_id, {})
        result.append(
            {
                "actor_id": actor_id,
                "actor_name": actor.get("name") if isinstance(actor.get("name"), str) else None,
                "actor_subtype": actor.get("subType") if isinstance(actor.get("subType"), str) else None,
                "start": min(times),
                "end": max(times),
                "event_count": len(times),
            }
        )
    return sorted(result, key=lambda item: (item["start"], item["end"]))


def group_boss_source_ranges(ranges: list[dict[str, Any]], start_window: float = 8.0) -> list[dict[str, Any]]:
    groups: list[list[dict[str, Any]]] = []
    for item in ranges:
        if not groups or item["start"] - groups[-1][0]["start"] > start_window:
            groups.append([item])
        else:
            groups[-1].append(item)

    result: list[dict[str, Any]] = []
    for index, group in enumerate(groups, start=1):
        starts = [float(item["start"]) for item in group]
        ends = [float(item["end"]) for item in group]
        names = sorted({item["actor_name"] for item in group if item.get("actor_name")})
        result.append(
            {
                "phase_index": index,
                "start": min(starts),
                "end": max(ends),
                "actor_names": names,
                "source_count": len(group),
            }
        )
    return result


def gaps_from_times(times: list[float], min_gap: float) -> list[tuple[float, float, float]]:
    gaps: list[tuple[float, float, float]] = []
    if not times:
        return gaps
    previous = times[0]
    for current in times[1:]:
        gap = current - previous
        if gap >= min_gap:
            gaps.append((previous, current, gap))
        previous = current
    return gaps


def infer_targetability_from_raw(raw_events: list[dict[str, Any]], fight_start_ms: float, boss_ids: set[int]) -> tuple[list[float], list[float]]:
    starts: list[float] = []
    ends: list[float] = []
    for event in raw_events:
        event_kind = event_type(event)
        if not any(kind in event_kind for kind in TARGETABILITY_EVENT_TYPES):
            continue
        actor = target_id(event) or source_id(event)
        if boss_ids and actor is not None and actor not in boss_ids:
            continue
        timestamp = event_timestamp_seconds(event, fight_start_ms)
        if timestamp is None:
            continue
        targetable = event.get("targetable")
        if targetable is True:
            starts.append(timestamp)
        elif targetable is False:
            ends.append(timestamp)
        elif "untargetable" in event_kind:
            ends.append(timestamp)
        else:
            starts.append(timestamp)
    return sorted(starts), sorted(ends)


def phase_markers_from_raw(raw_events: list[dict[str, Any]], fight_start_ms: float) -> list[float]:
    markers: list[float] = []
    for event in raw_events:
        event_kind = event_type(event)
        if not any(kind in event_kind for kind in PHASE_EVENT_TYPES):
            continue
        timestamp = event_timestamp_seconds(event, fight_start_ms)
        if timestamp is not None and timestamp >= 0:
            markers.append(timestamp)
    return sorted(set(round(marker, 3) for marker in markers))


def spawn_markers_from_raw(raw_events: list[dict[str, Any]], fight_start_ms: float, boss_ids: set[int]) -> tuple[list[float], list[float]]:
    spawns: list[float] = []
    despawns: list[float] = []
    for event in raw_events:
        event_kind = event_type(event)
        if not any(kind in event_kind for kind in SPAWN_DESPAWN_EVENT_TYPES):
            continue
        actor = target_id(event) or source_id(event)
        if boss_ids and actor is not None and actor not in boss_ids:
            continue
        timestamp = event_timestamp_seconds(event, fight_start_ms)
        if timestamp is None:
            continue
        if "spawn" in event_kind or event_kind == "combatantinfo":
            spawns.append(timestamp)
        elif "death" in event_kind or "remove" in event_kind:
            despawns.append(timestamp)
    return sorted(spawns), sorted(despawns)


def raid_buff_times(events: list[dict[str, Any]], fight_start_ms: float, ability_names: dict[int, str]) -> list[float]:
    times: list[float] = []
    for event in events:
        ability_id = event_ability_id(event)
        ability_name = event_ability_name(event, ability_names)
        if ability_id not in RAID_BUFF_IDS and not text_contains_any(ability_name, RAID_BUFF_NAME_PARTS):
            continue
        timestamp = event_timestamp_seconds(event, fight_start_ms)
        if timestamp is not None and timestamp >= 0:
            times.append(timestamp)
    return sorted(times)


def potion_times(events: list[dict[str, Any]], fight_start_ms: float, ability_names: dict[int, str]) -> list[float]:
    times: list[float] = []
    for event in events:
        if not text_contains_any(event_ability_name(event, ability_names), POTION_NAME_PARTS):
            continue
        timestamp = event_timestamp_seconds(event, fight_start_ms)
        if timestamp is not None and timestamp >= 0:
            times.append(timestamp)
    return sorted(times)


def cluster_times(times: list[float], window: float) -> list[float]:
    if not times:
        return []
    clusters: list[list[float]] = []
    current: list[float] = [times[0]]
    for time in times[1:]:
        if time - current[0] <= window:
            current.append(time)
        else:
            if len(current) >= 2:
                clusters.append(current)
            current = [time]
    if len(current) >= 2:
        clusters.append(current)
    return [round(sum(cluster) / len(cluster), 3) for cluster in clusters]


def count_bursts_before_downtime(clusters: list[float], gap_starts: list[float], window: float) -> int:
    count = 0
    for cluster in clusters:
        if any(0 <= gap_start - cluster <= window for gap_start in gap_starts):
            count += 1
    return count


def count_bursts_after_reappearance(clusters: list[float], gap_ends: list[float], window: float) -> int:
    count = 0
    for cluster in clusters:
        if any(0 <= cluster - gap_end <= window for gap_end in gap_ends):
            count += 1
    return count


def downtime_before_next_burst(clusters: list[float], gap_starts: list[float]) -> list[float]:
    values: list[float] = []
    for gap_start in gap_starts:
        later = [cluster for cluster in clusters if cluster > gap_start]
        if later:
            values.append(min(later) - gap_start)
    return values


def analyze_fight(bundle: dict[str, Any], options: Options, exclusions: Exclusions) -> FightTimelineSample | None:
    fight = fight_from_bundle(bundle)
    if fight is None:
        exclusions.add("missing_fight_metadata")
        return None
    fight_start_ms = number_or_none(fight.get("startTime"))
    fight_end_ms = number_or_none(fight.get("endTime"))
    if fight_start_ms is None or fight_end_ms is None:
        exclusions.add("missing_fight_time")
        return None
    duration = (fight_end_ms - fight_start_ms) / 1000.0
    if duration < options.min_kill_seconds or duration > options.max_kill_seconds:
        exclusions.add("abnormal_duration")
        return None

    report = bundle.get("report")
    master_data = report.get("masterData") if isinstance(report, dict) else {}
    master_data = master_data if isinstance(master_data, dict) else {}
    ability_names = abilities_from_master(master_data)
    events_by_type = bundle.get("events_by_type") if isinstance(bundle.get("events_by_type"), dict) else {}
    raw_events = [event for event in as_list(events_by_type.get(RAW_EVENT_TYPE)) if isinstance(event, dict)]
    damage_events = [event for event in as_list(events_by_type.get("DamageDone")) if isinstance(event, dict)]
    damage_taken_events = [event for event in as_list(events_by_type.get("DamageTaken")) if isinstance(event, dict)]
    cast_events = [event for event in as_list(events_by_type.get("Casts")) if isinstance(event, dict)]
    buff_events = [event for event in as_list(events_by_type.get("Buffs")) if isinstance(event, dict)]

    boss_ids = boss_actor_ids(master_data, damage_events)
    damage_times = boss_damage_times(damage_events, fight_start_ms, boss_ids)
    if not damage_times:
        exclusions.add("missing_boss_damage")
        return None
    boss_source_phase_groups = group_boss_source_ranges(boss_source_ranges_from_damage_taken(damage_taken_events, fight_start_ms, master_data))

    targetable_starts, targetable_ends = infer_targetability_from_raw(raw_events, fight_start_ms, boss_ids)
    phase_markers = phase_markers_from_raw(raw_events, fight_start_ms)
    spawn_starts, spawn_ends = spawn_markers_from_raw(raw_events, fight_start_ms, boss_ids)

    gaps = gaps_from_times(damage_times, options.phase_gap_min_seconds)
    gap_starts = [start for start, _, _ in gaps]
    gap_ends = [end for _, end, _ in gaps]
    gap_lengths = [gap for _, _, gap in gaps]
    major_gap_lengths = [gap for gap in gap_lengths if gap >= options.major_gap_min_seconds]

    targetable_starts = sorted(set(round(value, 3) for value in targetable_starts + gap_ends + phase_markers + spawn_starts if value >= 0))
    targetable_ends = sorted(set(round(value, 3) for value in targetable_ends + gap_starts + spawn_ends if value >= 0))

    buff_reference_events = cast_events + buff_events
    raid_clusters = cluster_times(raid_buff_times(buff_reference_events, fight_start_ms, ability_names), options.burst_cluster_seconds)
    potions = potion_times(cast_events + buff_events, fight_start_ms, ability_names)

    p2_p3_gap = None
    if major_gap_lengths:
        p2_p3_gap = max(major_gap_lengths)

    return FightTimelineSample(
        kill=fight.get("kill") is True,
        duration=duration,
        targetable_starts=targetable_starts,
        targetable_ends=targetable_ends,
        damage_gap_starts=gap_starts,
        damage_gap_ends=gap_ends,
        damage_gap_durations=gap_lengths,
        major_gap_durations=major_gap_lengths,
        p2_p3_gap=p2_p3_gap,
        raid_buff_clusters=raid_clusters,
        potion_times=potions,
        burst_before_downtime_count=count_bursts_before_downtime(raid_clusters, gap_starts, options.burst_before_downtime_seconds),
        burst_after_reappearance_count=count_bursts_after_reappearance(raid_clusters, gap_ends, 30.0),
        downtime_before_next_burst=downtime_before_next_burst(raid_clusters, gap_starts),
        boss_damage_start=damage_times[0],
        boss_damage_end=damage_times[-1],
        boss_source_phase_groups=boss_source_phase_groups,
    )


def flatten(samples: list[FightTimelineSample], attr: str) -> list[float]:
    values: list[float] = []
    for sample in samples:
        item = getattr(sample, attr)
        if isinstance(item, list):
            values.extend(value for value in item if isinstance(value, (int, float)))
        elif isinstance(item, (int, float)):
            values.append(float(item))
    return sorted_numbers(values)


def phase_boundary_summary(samples: list[FightTimelineSample]) -> list[dict[str, Any]]:
    max_boundaries = max((len(sample.damage_gap_starts) for sample in samples), default=0)
    result: list[dict[str, Any]] = []
    for index in range(max_boundaries):
        end_values = [sample.damage_gap_starts[index] for sample in samples if len(sample.damage_gap_starts) > index]
        start_values = [sample.damage_gap_ends[index] for sample in samples if len(sample.damage_gap_ends) > index]
        gap_values = [sample.damage_gap_durations[index] for sample in samples if len(sample.damage_gap_durations) > index]
        result.append(
            {
                "boundary_index": index + 1,
                "targetable_end_seconds": distribution(end_values),
                "targetable_start_seconds": distribution(start_values),
                "gap_seconds": distribution(gap_values),
                "next_phase_hint": "StartWithDowntime",
            }
        )
    return result


def boss_source_phase_summary(samples: list[FightTimelineSample]) -> list[dict[str, Any]]:
    max_phases = max((len(sample.boss_source_phase_groups) for sample in samples), default=0)
    result: list[dict[str, Any]] = []
    for phase_index in range(1, max_phases + 1):
        groups = [
            sample.boss_source_phase_groups[phase_index - 1]
            for sample in samples
            if len(sample.boss_source_phase_groups) >= phase_index
        ]
        starts = [float(group["start"]) for group in groups]
        ends = [float(group["end"]) for group in groups]
        names: dict[str, int] = {}
        for group in groups:
            for name in as_list(group.get("actor_names")):
                if isinstance(name, str):
                    names[name] = names.get(name, 0) + 1
        result.append(
            {
                "phase_index": phase_index,
                "source_actor_names": dict(sorted(names.items(), key=lambda item: (-item[1], item[0]))),
                "source_active_start_seconds": distribution(starts),
                "source_active_end_seconds": distribution(ends),
            }
        )
    return result


def boss_source_phase_table(phases: list[dict[str, Any]]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for phase in phases:
        actor_names = [
            name for name in phase.get("source_actor_names", {}).keys()
            if isinstance(name, str)
        ] if isinstance(phase.get("source_actor_names"), dict) else []
        start = phase.get("source_active_start_seconds")
        end = phase.get("source_active_end_seconds")
        start = start if isinstance(start, dict) else {}
        end = end if isinstance(end, dict) else {}
        rows.append(
            {
                "phase_index": phase.get("phase_index"),
                "actors": " + ".join(actor_names),
                "start_median": start.get("median"),
                "start_p90": start.get("p90"),
                "start_p95": start.get("p95"),
                "end_median": end.get("median"),
                "end_p90": end.get("p90"),
                "end_p95": end.get("p95"),
                "sample_count": end.get("sample_count"),
            }
        )
    return rows


def phase_boundary_table(boundaries: list[dict[str, Any]]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for boundary in boundaries:
        end = boundary.get("targetable_end_seconds")
        start = boundary.get("targetable_start_seconds")
        gap = boundary.get("gap_seconds")
        end = end if isinstance(end, dict) else {}
        start = start if isinstance(start, dict) else {}
        gap = gap if isinstance(gap, dict) else {}
        rows.append(
            {
                "boundary_index": boundary.get("boundary_index"),
                "downtime_start_median": end.get("median"),
                "downtime_start_p90": end.get("p90"),
                "downtime_start_p95": end.get("p95"),
                "downtime_end_median": start.get("median"),
                "downtime_end_p90": start.get("p90"),
                "downtime_end_p95": start.get("p95"),
                "gap_median": gap.get("median"),
                "gap_p90": gap.get("p90"),
                "gap_p95": gap.get("p95"),
                "next_phase_hint": boundary.get("next_phase_hint"),
                "sample_count": gap.get("sample_count"),
            }
        )
    return rows


def boss_source_phase_boundary_table(phase_rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    ordered = [
        row for row in phase_rows
        if isinstance(row.get("phase_index"), int)
    ]
    ordered.sort(key=lambda row: row["phase_index"])
    for previous, current in zip(ordered, ordered[1:]):
        previous_end = number_or_none(previous.get("end_median"))
        current_start = number_or_none(current.get("start_median"))
        previous_end_p90 = number_or_none(previous.get("end_p90"))
        current_start_p90 = number_or_none(current.get("start_p90"))
        previous_end_p95 = number_or_none(previous.get("end_p95"))
        current_start_p95 = number_or_none(current.get("start_p95"))
        if previous_end is None or current_start is None:
            continue
        rows.append(
            {
                "from_phase": previous.get("phase_index"),
                "to_phase": current.get("phase_index"),
                "from_actors": previous.get("actors"),
                "to_actors": current.get("actors"),
                "downtime_start_median": previous_end,
                "downtime_end_median": current_start,
                "gap_median": round(current_start - previous_end, 3),
                "downtime_start_p90": previous_end_p90,
                "downtime_end_p90": current_start_p90,
                "gap_p90": round(current_start_p90 - previous_end_p90, 3) if previous_end_p90 is not None and current_start_p90 is not None else None,
                "downtime_start_p95": previous_end_p95,
                "downtime_end_p95": current_start_p95,
                "gap_p95": round(current_start_p95 - previous_end_p95, 3) if previous_end_p95 is not None and current_start_p95 is not None else None,
                "next_phase_hint": "StartWithDowntime",
                "source": "boss_source_damage_taken",
            }
        )
    return rows


def recommended_snapshot_seconds(p2_p3_gaps: list[float]) -> float:
    p95 = nearest_rank(p2_p3_gaps, 95)
    if p95 is None:
        return 95.0
    return 105.0 if p95 >= 66.0 else 95.0


def csharp_float(value: float) -> str:
    if float(value).is_integer():
        return f"{int(value)}f"
    return f"{value:.1f}f"


def dancing_mad_profile(snapshot: float) -> dict[str, float]:
    return {
        "MajorBurstMeleeSafeGCDs": 6.0,
        "PBMeleeSafeGCDs": 4.0,
        "EvenPreRoFPBStartThreshold": 5.5,
        "RoFBrotherhoodResyncWindow": 90.0,
        "TargetAvailableUptimeRequired": 20.0,
        "PotionFutureWindowLeeway": 15.0,
        "RoWTwoMinuteHoldWindow": 45.0,
        "RoWUptimeRequired": 12.0,
        "RoWEndBurnLeeway": 20.0,
        "LastRoFWindowLeeway": 45.0,
        "LastBrotherhoodWindowLeeway": 105.0,
        "LastPotionWindowLeeway": 210.0,
    }


def build_csharp_profile_candidate(profile: dict[str, float], snapshot: float) -> str:
    lines = [
        f"private const float DancingMadCrossCombatPBBurstSnapshotSeconds = {csharp_float(snapshot)};",
        "",
        "private static readonly MNKTuningProfile DancingMadTuningProfile = DefaultTuningProfile with",
        "{",
    ]
    for field in FIELD_ORDER:
        lines.append(f"    {field} = {csharp_float(profile[field])},")
    lines.append("};")
    return "\n".join(lines)


def build_csharp_phase_hint_candidates(boundaries: list[dict[str, Any]]) -> list[str]:
    candidates: list[str] = []
    for boundary in boundaries:
        index = boundary["boundary_index"]
        end_median = boundary["targetable_end_seconds"].get("median")
        start_median = boundary["targetable_start_seconds"].get("median")
        if end_median is None or start_median is None:
            continue
        candidates.append(
            f"// Boundary {index}: targetable ends at ~{end_median:.1f}s, next phase starts targetable at ~{start_median:.1f}s; add .SetHint(StateMachine.PhaseHint.StartWithDowntime) to the following phase and DowntimeStart/DowntimeEnd state hints around this gap."
        )
    return candidates


def build_csharp_boss_source_phase_candidates(boundaries: list[dict[str, Any]]) -> list[str]:
    candidates: list[str] = []
    for boundary in boundaries:
        from_phase = boundary.get("from_phase")
        to_phase = boundary.get("to_phase")
        downtime_start = number_or_none(boundary.get("downtime_start_median"))
        downtime_end = number_or_none(boundary.get("downtime_end_median"))
        gap = number_or_none(boundary.get("gap_median"))
        if downtime_start is None or downtime_end is None or gap is None:
            continue
        candidates.append(
            f"// P{from_phase}->P{to_phase}: boss source activity gap ~{gap:.1f}s ({downtime_start:.1f}s to {downtime_end:.1f}s). If this is real downtime in the BossModule timeline, add DowntimeStart/DowntimeEnd state hints and .SetHint(StateMachine.PhaseHint.StartWithDowntime) to P{to_phase}."
        )
    return candidates


def safe_output_payload(payload: dict[str, Any]) -> None:
    text = json.dumps(payload, ensure_ascii=False, sort_keys=True)
    forbidden = (
        "reportCode",
        "report_code",
        "characterName",
        "playerName",
        "guildName",
        "serverName",
        "access_token",
        "client_secret",
        "Authorization",
        "Bearer ",
    )
    found = [item for item in forbidden if item in text]
    if found:
        raise RuntimeError(f"output contains forbidden private fields: {', '.join(found)}")


def build_outputs(samples: list[FightTimelineSample], exclusions: Exclusions) -> tuple[dict[str, Any], dict[str, Any]]:
    clear_samples = [sample for sample in samples if sample.kill]
    selected = clear_samples if clear_samples else samples
    p2_p3_gaps = sorted_numbers(sample.p2_p3_gap for sample in selected)
    snapshot = recommended_snapshot_seconds(p2_p3_gaps)
    profile = dancing_mad_profile(snapshot)
    boundaries = phase_boundary_summary(selected)
    boss_phases = boss_source_phase_summary(selected)
    boss_phase_rows = boss_source_phase_table(boss_phases)
    boss_phase_boundaries = boss_source_phase_boundary_table(boss_phase_rows)

    timeline = {
        "source": "fflogs_api_v2_public_reports",
        "encounter": "Dancing Mad (Ultimate)",
        "fflogs_zone_id": DANCING_MAD_ZONE_ID,
        "fflogs_encounter_id": None,
        "partition": None,
        "sample_count": len(selected),
        "clear_sample_count": len(clear_samples),
        "progress_sample_count": len(samples) - len(clear_samples),
        "excluded_count": exclusions.total,
        "excluded_by_reason": dict(sorted(exclusions.by_reason.items())),
        "selection_rule": "clear logs if available; otherwise high-progress public logs",
        "analysis_scope": [
            "targetabilityupdate",
            "boss/enemy damage gap",
            "boss actor spawn/despawn equivalent raw events",
            "phase start/end raw events",
            "party raid buff clusters",
            "potion timing reference only",
        ],
        "phase_boundaries": boundaries,
        "phase_boundary_table": phase_boundary_table(boundaries),
        "boss_source_phase_groups": boss_phases,
        "boss_source_phase_table": boss_phase_rows,
        "boss_source_phase_boundary_table": boss_phase_boundaries,
        "p2_p3_gap_stats": distribution(p2_p3_gaps),
        "targetable_start_stats": distribution(flatten(selected, "targetable_starts")),
        "targetable_end_stats": distribution(flatten(selected, "targetable_ends")),
        "damage_gap_stats": distribution(flatten(selected, "damage_gap_durations")),
        "major_damage_gap_stats": distribution(flatten(selected, "major_gap_durations")),
        "raid_buff_cluster_stats": {
            "cluster_time_seconds": distribution(flatten(selected, "raid_buff_clusters")),
            "bursts_before_downtime_count": sum(sample.burst_before_downtime_count for sample in selected),
            "bursts_after_reappearance_count": sum(sample.burst_after_reappearance_count for sample in selected),
            "downtime_to_next_burst_seconds": distribution(flatten(selected, "downtime_before_next_burst")),
        },
        "potion_reference_stats": {
            "potion_time_seconds": distribution(flatten(selected, "potion_times")),
        },
        "recommended_mnk_profile": {
            "DancingMadCrossCombatPBBurstSnapshotSeconds": snapshot,
            "DancingMadTuningProfile": profile,
        },
        "csharp_phase_hint_candidates": build_csharp_phase_hint_candidates(boundaries),
        "csharp_boss_source_phase_candidates": build_csharp_boss_source_phase_candidates(boss_phase_boundaries),
    }

    profile_output = {
        "source": "dancing_mad_timeline_profile",
        "encounter": "Dancing Mad (Ultimate)",
        "fflogs_zone_id": DANCING_MAD_ZONE_ID,
        "fflogs_encounter_id": None,
        "partition": None,
        "sample_count": len(selected),
        "recommended_snapshot_rule": "95s when P2->P3 p95 gap <= 65s; 105s when p95 gap is 66-75s",
        "DancingMadCrossCombatPBBurstSnapshotSeconds": snapshot,
        "DancingMadTuningProfile": profile,
        "csharp_candidate": build_csharp_profile_candidate(profile, snapshot),
    }
    safe_output_payload(timeline)
    safe_output_payload(profile_output)
    return timeline, profile_output


def attach_fflogs_metadata(payload: dict[str, Any], options: Options) -> dict[str, Any]:
    payload["fflogs_zone_id"] = options.zone_id
    payload["fflogs_encounter_id"] = options.encounter_id
    payload["partition"] = options.partition
    return payload


def parse_args(argv: list[str]) -> Options:
    parser = argparse.ArgumentParser(description="Collect Dancing Mad public FF Logs timeline signals for MNK profile tuning.")
    parser.add_argument("--zone-id", type=int, help="FF Logs zone ID. Use 76 for Dancing Mad (Ultimate).")
    parser.add_argument("--encounter-id", type=int, help="FF Logs encounter ID for Dancing Mad (Ultimate). Do not pass BossMod CFCID 1094.")
    parser.add_argument("--difficulty", type=int, default=DEFAULT_DIFFICULTY)
    parser.add_argument("--partition", type=int, default=DEFAULT_PARTITION)
    parser.add_argument("--metric", default=DEFAULT_METRIC)
    parser.add_argument("--limit", type=int, default=DEFAULT_LIMIT)
    parser.add_argument("--page-size", type=int, default=DEFAULT_PAGE_SIZE)
    parser.add_argument("--event-limit", type=int, default=DEFAULT_EVENT_LIMIT)
    parser.add_argument("--cache-dir", type=Path, default=DEFAULT_CACHE_DIR)
    parser.add_argument("--timeline-out", type=Path, default=DEFAULT_TIMELINE_OUT)
    parser.add_argument("--profile-out", type=Path, default=DEFAULT_PROFILE_OUT)
    parser.add_argument("--use-cache", action="store_true")
    parser.add_argument("--client-id")
    parser.add_argument("--client-secret")
    parser.add_argument("--phase-gap-min-seconds", type=float, default=DEFAULT_PHASE_GAP_MIN_SECONDS)
    parser.add_argument("--major-gap-min-seconds", type=float, default=DEFAULT_MAJOR_GAP_MIN_SECONDS)
    parser.add_argument("--burst-cluster-seconds", type=float, default=DEFAULT_BURST_CLUSTER_SECONDS)
    parser.add_argument("--burst-before-downtime-seconds", type=float, default=DEFAULT_BURST_BEFORE_DOWNTIME_SECONDS)
    parser.add_argument("--min-kill-seconds", type=float, default=DEFAULT_MIN_KILL_SECONDS)
    parser.add_argument("--max-kill-seconds", type=float, default=DEFAULT_MAX_KILL_SECONDS)
    args = parser.parse_args(argv)

    if args.encounter_id == 1094:
        raise RuntimeError("1094 is BossMod/game CFCID, not an FF Logs encounter id; use --zone-id 76 or a verified FF Logs encounter id")
    if args.encounter_id is None and args.zone_id is None:
        raise RuntimeError("specify either --encounter-id or --zone-id")
    if args.limit < 1 or args.limit > 300:
        raise RuntimeError("--limit must be between 1 and 300")
    if args.page_size < 1:
        raise RuntimeError("--page-size must be positive")
    if args.event_limit < 100:
        raise RuntimeError("--event-limit must be at least 100")

    return Options(
        zone_id=args.zone_id if args.zone_id is not None else DANCING_MAD_ZONE_ID,
        encounter_id=args.encounter_id if args.encounter_id is not None else 0,
        difficulty=args.difficulty,
        partition=args.partition,
        metric=args.metric,
        limit=args.limit,
        page_size=args.page_size,
        event_limit=args.event_limit,
        cache_dir=args.cache_dir,
        timeline_out=args.timeline_out,
        profile_out=args.profile_out,
        use_cache=args.use_cache,
        client_id=args.client_id,
        client_secret=args.client_secret,
        phase_gap_min_seconds=args.phase_gap_min_seconds,
        major_gap_min_seconds=args.major_gap_min_seconds,
        burst_cluster_seconds=args.burst_cluster_seconds,
        burst_before_downtime_seconds=args.burst_before_downtime_seconds,
        min_kill_seconds=args.min_kill_seconds,
        max_kill_seconds=args.max_kill_seconds,
    )


def resolve_credentials(options: Options) -> tuple[str, str]:
    if options.client_id and options.client_secret:
        return options.client_id, options.client_secret
    return read_credentials()


def main(argv: list[str]) -> int:
    options = parse_args(argv)
    client_id, client_secret = resolve_credentials(options)
    try:
        access_token = fetch_access_token(client_id, client_secret)
    except urllib.error.HTTPError as error:
        if error.code == 401:
            raise RuntimeError("FF Logs authentication failed") from error
        raise

    resolved_encounter_id, encounter_candidates = resolve_encounter_from_zone(access_token, options)
    if resolved_encounter_id is None:
        payload = unresolved_encounter_payload(options, encounter_candidates)
        write_json(options.timeline_out, payload)
        write_json(options.profile_out, payload)
        return 2
    options = replace(options, encounter_id=resolved_encounter_id)

    rankings = collect_public_rankings(access_token, options)
    exclusions = Exclusions()
    samples: list[FightTimelineSample] = []
    for ranking in rankings:
        report_code, fight_id = report_reference_from_ranking(ranking)
        if not report_code or fight_id is None:
            exclusions.add("missing_report_reference")
            continue
        try:
            bundle = fetch_report_bundle(access_token, options, report_code, fight_id)
            sample = analyze_fight(bundle, options, exclusions)
        except Exception:
            exclusions.add("event_fetch_or_parse_failed")
            continue
        if sample is not None:
            samples.append(sample)

    timeline, profile = build_outputs(samples, exclusions)
    attach_fflogs_metadata(timeline, options)
    attach_fflogs_metadata(profile, options)
    write_json(options.timeline_out, timeline)
    write_json(options.profile_out, profile)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except RuntimeError as error:
        print(error, file=sys.stderr)
        raise SystemExit(1)
