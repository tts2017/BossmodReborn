# Tuning data tool

This directory is for offline tooling that can collect public FF Logs data,
extract anonymous job features, and export candidate tuning values. It started
as an MNK helper, but the shared tools now support all standard combat jobs
except BLU.

The intended flow is:

1. Query FF Logs API v2 through GraphQL.
2. Read `FFLOGS_CLIENT_ID` and `FFLOGS_CLIENT_SECRET` from environment variables.
3. Use the public client credentials API only.
4. Target the top 200 unique public players for a selected encounter and job.
5. Extract aggregate features instead of redistributing raw logs.
6. Export anonymized JSON with numeric candidate values only.
7. Manually copy selected values into job rotation code after review.

The runtime rotations must not call FF Logs, run AI inference, or read JSON.
This tool is outside the Dalamud plugin path by design. BossMod, Dalamud, and
`MNK.cs` do not perform API communication at runtime. Do not include API
credentials, character names, player names, report URLs, or report codes in
final profile output.

Intermediate cache files are local-only and must not be redistributed. They may
need report identifiers for later event collection, but final profile JSON must
not contain personal names, report URLs, or report codes.

Generated cache data, `*_response.json`, `*_features.json`, and
`*_profile.json` files are local artifacts and should not be committed. Local
intermediate cache can contain identifiers such as `reportCode` or `fightID`
needed to continue collection, so do not redistribute it. Final profile output
must still exclude personal names, report URLs, and report codes.

FF Logs API notes:

- API version: FF Logs API v2.
- API shape: GraphQL.
- Public endpoint: `https://www.fflogs.com/api/v2/client`.
- Authentication: OAuth client credentials flow.

## Schema and connectivity probes

Set credentials in the shell before running probes:

```powershell
$env:FFLOGS_CLIENT_ID = "<client id>"
$env:FFLOGS_CLIENT_SECRET = "<client secret>"
```

Run the safe schema probe first:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py probe-schema
```

This generates:

```text
tools/mnk_tuning/rate_limit_response.json
tools/mnk_tuning/schema_probe_response.json
tools/mnk_tuning/type_name_list_response.json
tools/mnk_tuning/type_name_candidates.json
```

`probe-schema` intentionally stops at rate limit, root `Query`, schema type name
list, and filtered type candidates. It does not run
`ranking_type_probe_query.graphql`, because that query contains guessed type
names and can trigger an FF Logs internal server error.

After reviewing `type_name_candidates.json`, run the refined type probe:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py probe-refined-types
```

`probe-refined-types` uses only type names that were confirmed by the schema
candidate list. It generates:

```text
tools/mnk_tuning/refined_type_probe_response.json
tools/mnk_tuning/refined_type_schema_summary.json
```

Review `refined_type_schema_summary.json` before implementing the GraphQL query
body in `collect_public_top_jobs.py`.

If the candidate type names look safe, the older explicit ranking type probe can
be run manually:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py probe-ranking-types
```

This generates `ranking_type_probe_response.json` and
`ranking_schema_summary.json` only if the query succeeds. If FF Logs returns an
internal server error, review `type_name_candidates.json` and rebuild
`ranking_type_probe_query.graphql` using only type names confirmed by the
schema. This older command is normally not needed after the refined probe exists.

The single-query inspection tool remains available when a specific GraphQL file
needs to be tested:

```powershell
py -3 tools/mnk_tuning/inspect_fflogs_schema.py `
  --query-file tools/mnk_tuning/sample_graphql/rate_limit_query.graphql `
  --out tools/mnk_tuning/rate_limit_response.json
```

Use `type_name_candidates.json` and `refined_type_schema_summary.json` to
implement `build_top_mnk_query()` only after the required ranking, report, and
event fields are clear. Real ranking collection responses can contain personal
names and report codes, so those fields must not be copied into final profile
output.

`Encounter.characterRankings(...)`, `Report.rankings(...)`, and
`Report.events(...).data` return JSON scalars, so their inner shapes are not
visible from schema introspection. Before implementing top-200 collection, run
small shape probes and inspect only redacted/type-summary output.

If `worldData.encounter(id)` returns `null`, list public zones and encounters
first:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py probe-zone-encounters
```

Optionally narrow the zone list by expansion:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py probe-zone-encounters `
  --expansion-id 68
```

Use `zone_encounters_summary.json` to pick a valid encounter id, then rerun the
`characterRankings` shape probe. Do not implement top-200 collection until the
`characterRankings` JSON shape has been confirmed.

Probe a small `characterRankings` page:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py probe-character-rankings-shape `
  --encounter-id 0 `
  --difficulty 0 `
  --partition 0 `
  --metric rdps `
  --job MNK `
  --spec-name Monk `
  --page 1 `
  --size 5
```

FFXIV ranking rows commonly report `class = Global` and job-specific values in
`spec`, such as `Monk`, `Ninja`, or `BlackMage`. For job filtering, the shape
probe defaults to `className = null` and `specName = <FF Logs job name>`.
Override `--class-name` or `--spec-name` only when checking a specific FF Logs
filter combination.

If `characterRankings` returns only an `error` key, inspect
`character_rankings_diagnostic_summary.json`. It records only safe probe
variables and the `characterRankings.error` message. It does not include API
secrets, tokens, report codes, character names, player names, or report URLs.
Use that diagnostic output to adjust `difficulty`, `partition`, `metric`,
`className`, or `specName`, then rerun the shape probe. Do not implement
top-200 collection until the normal `characterRankings` shape is confirmed.

Probe a small `Report.events` page:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py probe-report-events-shape `
  --code "<report code>" `
  --fight-id 1 `
  --data-type Casts `
  --limit 50
```

`--ability-id` is passed as a Float because FF Logs exposes `Report.events`
`abilityID` with the GraphQL `Float` type.

These commands write raw probe responses only to local `*_response.json` files
and write redacted shape summaries to `*_summary.json` files. Shape summaries
replace string, number, boolean, and null values with type labels. Do not print
report codes, character names, player names, or report URLs to stdout, and do
not include them in final profile output.

Only after reviewing the schema response should `collect_mnk_top300.py`
implement `build_top_mnk_query()`. The ranking, report, and event query shapes
should come from schema output rather than guesses.

Future collection work should add cache, queue, and `rateLimitData` checks
before larger pulls, so the tool can stay within FF Logs API quota.

## Common job workflow

List supported jobs:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py list-jobs
```

Extract common features from the example input:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py extract-job `
  --job MNK `
  --in tools/mnk_tuning/job_input_example.json `
  --out tools/mnk_tuning/job_features.json
```

Export a common profile candidate:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py export-job-profile `
  --job MNK `
  --in tools/mnk_tuning/job_features.json `
  --out tools/mnk_tuning/job_profile.json `
  --method median
```

The shared collector defaults to top 200 public logs:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py collect-public-top `
  --encounter-id 101 `
  --partition 1 `
  --metric rdps `
  --difficulty 101 `
  --job MNK `
  --party-size 8 `
  --limit 200 `
  --use-cache
```

`Encounter.characterRankings.size` is the party size, not the number of ranking
rows to return. Use `--party-size 8` for standard eight-player savage-style
content. The collector uses `--limit` for the requested sample count and advances
the `page` argument until enough public ranking rows are collected or FF Logs
reports no more pages.

For FFXIV `characterRankings`, job filtering uses `specName`. The normal
collector request sends `className = null` and `specName = <FF Logs job name>`.
Rows can still return `class = Global` with job-specific values in `spec`, such
as `Monk`, `Ninja`, or `BlackMage`. `--class-name` and `--spec-name` are
available only for verification or special FF Logs filter checks; normal use
does not need them.

Ranking metadata collection saves only the fields needed for later event
collection and aggregation. It keeps local intermediate identifiers such as
`reportCode` and `fightID`, but it does not save character names, player names,
server data, Lodestone IDs, guild data, or report URLs. `reportCode` and
`fightID` must not be copied into final profile output.

`collect-public-top` can use local cache files under:

```text
tools/mnk_tuning/cache/public_top/
  encounter_{encounter_id}/
    partition_{partition}/
      difficulty_{difficulty}/
        metric_{metric}/
          {JOB}_top{limit}.json
```

`--party-size`, `className`, and `specName` are stored in the payload but are not
part of the cache path. If an existing cache file has different request metadata,
the collector treats it as a cache miss and fetches fresh ranking metadata. After
changing from old mixed `className=Monk/specName=null` cache data to the
`className=null/specName=Monk` filter, delete the old cache or rerun without
`--use-cache` to force a fresh pull.

Event collection is intentionally not implemented yet. It should be added only
after the ranking metadata payload has been reviewed and the required report
events shape is confirmed.

Collect one fight's Casts events from a previously collected ranking sample:

```powershell
$top = Get-Content tools/mnk_tuning/cache/public_top/encounter_101/partition_1/difficulty_101/metric_rdps/MNK_top200.json -Raw | ConvertFrom-Json
$sample = $top.samples[0]
$ReportCode = $sample.reportCode
$FightId = [int]$sample.fightID

py -3 tools/mnk_tuning/mnk_tuning_tool.py collect-report-events `
  --code $ReportCode `
  --fight-id $FightId `
  --data-type Casts `
  --page-limit 1000 `
  --max-events 5000
```

`Report.events` pagination uses `nextPageTimestamp`; the collector passes it
back as the next `startTime` until no next page remains or `--max-events` is
reached. This first events collector handles only one fight at a time and is
intended for Casts validation before any top-200 batch events collection.

Events cache files are local-only under:

```text
tools/mnk_tuning/cache/report_events/
  report_{safe_report_code}/
    fight_{fight_id}/
      Casts.json
```

The events cache keeps `reportCode` and `fightID` only as local intermediate
identifiers needed for continued collection. It stores actor metadata as
`id/gameID/type/subType`, ability metadata as `gameID/type`, and event rows as
`timestamp/type/sourceID/targetID/abilityGameID/fight`. It does not store actor
names, ability names, server data, guild data, report URLs, Lodestone IDs, API
secrets, or tokens. Do not redistribute events cache files, and do not copy
`reportCode` or `fightID` into final profile output.

Extract one job's anonymized cast timing features from a Casts cache:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py extract-cast-features `
  --job MNK `
  --in tools/mnk_tuning/cache/report_events/report_xxx/fight_123/Casts.json `
  --out tools/mnk_tuning/mnk_cast_features.json
```

`extract-cast-features` selects actors where `type == "Player"` and `subType`
matches the FF Logs job name or a known actor class alias. For example, MNK
matches both `Monk` and `Pugilist`, because `masterData.actors[].subType` can be
reported as the base class. BLM can be returned as `BlackMage`, so BLM actor
matching checks `Black Mage`, `BlackMage`, and `Thaumaturge`. If multiple same-job actors are present, it keeps
all matching `sourceID` values instead of guessing one. Feature output stores
relative cast times by `abilityGameID` and omits the original absolute
timestamp, `reportCode`, actor names, ability names, server data, guild data,
report URLs, and Lodestone IDs. Top-200 batch events collection is still not
implemented.

If `extract-cast-features` returns `castCount=0`, diagnose whether
`events.sourceID` corresponds to `masterData.actors[].id` or
`masterData.actors[].gameID` before changing the extraction axis:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py diagnose-cast-actor-mapping `
  --job MNK `
  --in tools/mnk_tuning/cache/report_events/report_xxx/fight_123/Casts.json `
  --out tools/mnk_tuning/cast_actor_mapping_summary.json
```

The diagnostic summary lists job actor `id/gameID/type/subType`, event sourceID
counts, and the top sourceID-to-actor candidates. It does not include
`reportCode`, actor names, server data, guild data, report URLs, Lodestone IDs,
API secrets, or tokens. Do not guess a single actor from this output; inspect
the mapping before changing `extract-cast-features`.

Convert one fight's anonymized cast features into the common job sample format:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py convert-cast-to-job-sample `
  --job MNK `
  --cast-features tools/mnk_tuning/mnk_cast_features.json `
  --ranking-cache tools/mnk_tuning/cache/public_top/encounter_101/partition_1/difficulty_101/metric_rdps/MNK_top200.json `
  --source-rank 1 `
  --out tools/mnk_tuning/mnk_job_sample.json
```

The converted sample can be passed to the common feature extractor:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py extract-job `
  --job MNK `
  --in tools/mnk_tuning/mnk_job_sample.json `
  --out tools/mnk_tuning/mnk_job_features.json
```

This conversion joins one ranking row with one `extract-cast-features` output.
It keeps `abilityUseTimes` keyed by numeric `abilityGameID` and does not convert
ability IDs to skill names. `burstUseTimes`, `majorBuffUseTimes`,
`gaugeSpendTimes`, `potionUseTimes`, and downtime arrays are intentionally empty
until later feature extraction stages fill them. `reportCode` is not included in
the converted sample.

Before expanding to all top-200 entries, build a small batch from the first N
ranking rows:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py build-job-samples-batch `
  --job MNK `
  --ranking-cache tools/mnk_tuning/cache/public_top/encounter_101/partition_1/difficulty_101/metric_rdps/MNK_top200.json `
  --limit 3 `
  --out tools/mnk_tuning/mnk_job_samples_batch.json `
  --summary-out tools/mnk_tuning/mnk_job_samples_batch_summary.json `
  --use-events-cache
```

The batch command runs one-fight `collect-report-events`,
`extract-cast-features`, and `convert-cast-to-job-sample` for the selected
ranking rows. It stores only successful samples in the batch output and writes
per-sample failures to the summary without `reportCode`. The batch sample can be
passed to `extract-job` and then `export-job-profile` like the one-fight sample.
Top-200 batch events collection remains unimplemented.

For non-MNK jobs, use the common one-command pipeline first. It runs
`collect-public-top`, `build-job-samples-batch`, `extract-job`, and
`export-job-profile` in sequence. Start with `--limit 3`, then expand to 10, 50,
and 200 after each batch succeeds. The output profile is job-neutral; job-specific
ability maps and job-specific tuning profiles are separate later work.
`Encounter.characterRankings` uses ranking `specName` values that can differ from
FF Logs display names: for example, `Black Mage` uses `BlackMage`, `Red Mage`
uses `RedMage`, `Dark Knight` uses `DarkKnight`, and `White Mage` uses
`WhiteMage`. The tool chooses the ranking spec name automatically; use
`--spec-name` only when a manual override is needed. `--class-name` is normally
left unset.

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py run-job-public-pipeline `
  --job NIN `
  --encounter-id 101 `
  --difficulty 101 `
  --partition 1 `
  --metric rdps `
  --party-size 8 `
  --limit 10 `
  --out-prefix tools/mnk_tuning/nin `
  --use-cache `
  --use-events-cache
```

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py run-job-public-pipeline `
  --job BLM `
  --encounter-id 101 `
  --difficulty 101 `
  --partition 1 `
  --metric rdps `
  --party-size 8 `
  --limit 10 `
  --out-prefix tools/mnk_tuning/blm `
  --use-cache `
  --use-events-cache
```

With `--out-prefix tools/mnk_tuning/nin`, the generated files are
`nin_job_samples_batch.json`, `nin_job_samples_batch_summary.json`,
`nin_job_features.json`, and `nin_job_profile.json`. Ranking and report-event
caches use the existing cache paths. `reportCode` remains only in local
intermediate cache data and is not included in batch samples, features, or final
profile output.

MNK-specific profiles need a manually verified `abilityGameID` mapping before
they can derive RoF, Brotherhood, Perfect Balance, Riddle of Wind, or potion
timing. Start by summarizing observed ability IDs:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py summarize-ability-usage `
  --in tools/mnk_tuning/mnk_job_samples_batch200.json `
  --out tools/mnk_tuning/mnk_ability_usage_summary.json
```

Use the summary to inspect ability frequency and first-use timing, then copy
`mnk_ability_map_example.json` and replace the placeholder IDs with manually
confirmed real IDs. The summary does not include skill names, report codes,
actor names, server data, guild data, report URLs, Lodestone IDs, API secrets,
or tokens. Extracting RoF/Brotherhood/PB/RoW/potion timing columns is a later
step.

After the ability IDs are checked against repository action definitions and
observed usage timing, keep the candidate role mapping in
`mnk_ability_map_candidates.json`. Then extract MNK-specific timing columns:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py extract-mnk-specific-features `
  --samples tools/mnk_tuning/mnk_job_samples_batch200.json `
  --ability-map tools/mnk_tuning/mnk_ability_map_candidates.json `
  --out tools/mnk_tuning/mnk_specific_features.json
```

This produces RoF, Brotherhood, Perfect Balance, Riddle of Wind, and potion use
time arrays plus median timing summaries. It still does not generate an
`MNKTuningProfile` and does not update `MNK.cs`; profile conversion and manual
runtime review remain separate later steps.

NIN-specific timing follows the same review-only pattern after the common
top200 pipeline has generated `nin_job_samples_batch.json`. First summarize the
observed ability IDs:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py summarize-ability-usage `
  --in tools/mnk_tuning/nin_job_samples_batch.json `
  --out tools/mnk_tuning/nin_ability_usage_summary.json
```

Use the summary and repository action definitions to maintain
`nin_ability_map_candidates.json`, then extract NIN-specific timing columns:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py extract-nin-specific-features `
  --samples tools/mnk_tuning/nin_job_samples_batch.json `
  --ability-map tools/mnk_tuning/nin_ability_map_candidates.json `
  --out tools/mnk_tuning/nin_specific_features.json
```

This extracts Dokumori, Kunai's Bane, Kassatsu, Ten Chi Jin, Meisui, Bunshin,
Phantom Kamaitachi, Raiton, Hyosho Ranryu, Raiju, Bhavacakra, Hellfrog Medium,
Zesho Meppo, Tenri Jindo, and Dream Within a Dream timing columns. It does not
generate NIN tuning candidates and does not update `NIN.cs`.

Create NIN timing distributions and a review-only JSON before comparing the
data with the current NIN burst state machine:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py analyze-nin-timing-distributions `
  --in tools/mnk_tuning/nin_specific_features.json `
  --out tools/mnk_tuning/nin_timing_distributions.json

py -3 tools/mnk_tuning/mnk_tuning_tool.py export-nin-timing-review `
  --specific tools/mnk_tuning/nin_specific_features.json `
  --distributions tools/mnk_tuning/nin_timing_distributions.json `
  --out tools/mnk_tuning/nin_timing_review.json
```

The NIN timing review records observed burst order and selected delta evidence
only. It does not change `NIN.cs`; use it as review material before any later
manual tuning change.

NIN top200 data can contain multiple opener variants. To analyze only
Raiton-before-Kunai samples, filter the common job samples first:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py filter-nin-pre-raiton-samples `
  --samples tools/mnk_tuning/nin_job_samples_batch.json `
  --nin-specific tools/mnk_tuning/nin_specific_features.json `
  --out tools/mnk_tuning/nin_pre_raiton_job_samples.json `
  --summary-out tools/mnk_tuning/nin_pre_raiton_filter_summary.json `
  --opener-window 20.0
```

The base filter requires the first Raiton to occur before the first Kunai's
Bane and Hyosho Ranryu within the opener window. Add
`--require-dokumori-before-raiton` or `--require-kassatsu-before-kunai` when a
stricter subset is needed. Run `extract-job`, `export-job-profile`,
`extract-nin-specific-features`, `analyze-nin-timing-distributions`, and
`export-nin-timing-review` again on the filtered sample file before comparing
the subset against `NIN.cs`.

After the filtered timing review is generated, render a final review packet:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py review-nin-pre-raiton-timing `
  --filter-summary tools/mnk_tuning/nin_pre_raiton_filter_summary.json `
  --distributions tools/mnk_tuning/nin_pre_raiton_timing_distributions.json `
  --timing-review tools/mnk_tuning/nin_pre_raiton_timing_review.json `
  --out-json tools/mnk_tuning/nin_pre_raiton_final_review.json `
  --out-md tools/mnk_tuning/nin_pre_raiton_final_review.md
```

The selected subset is small, so this final review must not be applied directly
to `NIN.cs`. Compare it manually against the current NIN burst state machine
before making any separate code change.

BLM-specific timing follows the same review-only pattern after the common
top200 pipeline has generated `blm_job_samples_batch.json`. First summarize the
observed ability IDs:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py summarize-ability-usage `
  --in tools/mnk_tuning/blm_job_samples_batch.json `
  --out tools/mnk_tuning/blm_ability_usage_summary.json
```

Use the summary and repository action definitions to maintain
`blm_ability_map_candidates.json`, then extract BLM-specific timing columns:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py extract-blm-specific-features `
  --samples tools/mnk_tuning/blm_job_samples_batch.json `
  --ability-map tools/mnk_tuning/blm_ability_map_candidates.json `
  --out tools/mnk_tuning/blm_specific_features.json
```

This extracts Ley Lines, Manafont, Amplifier, Triplecast, Swiftcast,
Xenoglossy, Foul, Fire III, Fire IV, Despair, Flare Star, Blizzard III,
Blizzard IV, Paradox, Thunder, High Thunder, Transpose, Between the Lines, and
Retrace timing columns. It does not generate BLM tuning candidates and does not
update `BLM.cs`.

Create BLM timing distributions and a review-only JSON/Markdown packet before
comparing the data with the current BLM planner:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py analyze-blm-timing-distributions `
  --in tools/mnk_tuning/blm_specific_features.json `
  --out tools/mnk_tuning/blm_timing_distributions.json

py -3 tools/mnk_tuning/mnk_tuning_tool.py export-blm-timing-review `
  --specific tools/mnk_tuning/blm_specific_features.json `
  --distributions tools/mnk_tuning/blm_timing_distributions.json `
  --out-json tools/mnk_tuning/blm_timing_review.json `
  --out-md tools/mnk_tuning/blm_timing_review.md
```

The BLM timing review records observed order and selected Ley Lines, Manafont,
Amplifier, Triplecast, Despair, and Flare Star timing evidence only. It does
not change `BLM.cs`; use it as review material before any later manual planner
change.

VPR-specific timing follows the same review-only pattern after the common
top200 pipeline has generated `vpr_job_samples_batch.json`. First summarize the
observed ability IDs:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py summarize-ability-usage `
  --in tools/mnk_tuning/vpr_job_samples_batch.json `
  --out tools/mnk_tuning/vpr_ability_usage_summary.json
```

Use the summary and repository action definitions to maintain
`vpr_ability_map_candidates.json`, then extract VPR-specific timing columns:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py extract-vpr-specific-features `
  --samples tools/mnk_tuning/vpr_job_samples_batch.json `
  --ability-map tools/mnk_tuning/vpr_ability_map_candidates.json `
  --out tools/mnk_tuning/vpr_specific_features.json
```

This extracts Reawaken, Serpent's Ire, Vicewinder, Vicepit, Uncoiled Fury,
Uncoiled Twinfang, Uncoiled Twinblood, Ouroboros, Generation, Legacy, coil,
Death Rattle, and Last Lash timing columns. It does not generate VPR tuning
candidates and does not update `VPR.cs`.

Analyze the extracted VPR timing columns and render a review packet:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py analyze-vpr-timing-distributions `
  --in tools/mnk_tuning/vpr_specific_features.json `
  --out tools/mnk_tuning/vpr_timing_distributions.json

py -3 tools/mnk_tuning/mnk_tuning_tool.py export-vpr-timing-review `
  --specific tools/mnk_tuning/vpr_specific_features.json `
  --distributions tools/mnk_tuning/vpr_timing_distributions.json `
  --out-json tools/mnk_tuning/vpr_timing_review.json `
  --out-md tools/mnk_tuning/vpr_timing_review.md
```

The VPR timing review records observed order and selected Reawaken, Serpent's
Ire, Vicewinder, Ouroboros, Generation, Legacy, and Uncoiled Fury timing
evidence only. It does not change `VPR.cs`; use it as review material before
comparing the current VPR planner in a separate step.

### BLM top200 timing review result

The BLM top200 common pipeline completed with `succeeded=200` and `failed=0`.
BLM-specific features were generated, and `blm_timing_review.json` /
`blm_timing_review.md` were generated with `applyRecommendation=hold`.

Observed first-use order:

| Step | Median time |
| --- | ---: |
| High Thunder | 0.624 |
| Amplifier | 1.967 |
| Swiftcast | 2.228 |
| Triplecast | 2.318 |
| Fire IV | 3.074 |
| Ley Lines | 4.407 |
| Xenoglossy | 13.862 |
| Manafont | 14.522 |
| Flare Star | 18.023 |
| Despair | 38.680 |

Comparison with current `BLM.cs`:

- The Standard57 opener broadly matches the observed order.
- Amplifier being used before Ley Lines matches the observed timing.
- The roughly 10 second Ley Lines to Manafont distribution does not conflict
  with the current planner's Astral Fire progression, Xenoglossy handling, and
  Manafont window.
- Triplecast is state-dependent and should not be applied as a fixed opener
  slot from this review alone.
- Flare Star and Despair depend on Astral Fire, MP, and Astral Soul state, so
  timing data alone is not enough to change them.
- Movement instant priority is path-dependent, so this review does not justify
  a global movement-priority change.

Current decision:

- Do not change `BLM.cs` from this review.
- The current Standard57 opener and planner do not materially conflict with the
  top200 timing review.
- If more BLM work is needed, review explicit opener handling for Triplecast or
  movement instant priority as separate topics.
- BLM-specific profile generation needs stateful data for Astral Fire, MP,
  Astral Soul, Thunderhead, and Polyglot before code changes are considered.

Generate review-only candidates for the current `MNKTuningProfile` fields:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py export-mnk-tuning-candidates `
  --common-profile tools/mnk_tuning/mnk_job_profile_batch200.json `
  --mnk-specific tools/mnk_tuning/mnk_specific_features.json `
  --out tools/mnk_tuning/mnk_tuning_candidates.json
```

`export-mnk-tuning-candidates` writes `direct`, `candidate`, and `unmapped`
entries for the 12 MNK tuning fields. It does not write to `MNK.cs`. Unmapped
entries need either more features or a manual check of the MNK.cs threshold
semantics. Review this JSON manually before a later, separate change adds a
fixed content profile to `ResolveContentTuningProfile`.

Render the candidate JSON into a review Markdown file:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py review-mnk-tuning-candidates `
  --in tools/mnk_tuning/mnk_tuning_candidates.json `
  --out tools/mnk_tuning/mnk_tuning_review.md
```

The review separates `direct`, `candidate`, and `unmapped` fields for human
decision making. It still does not update `MNK.cs`; use it only to decide what
can be applied in a later fixed-profile change.

Before choosing MNK-specific thresholds, inspect distribution statistics:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py analyze-mnk-timing-distributions `
  --in tools/mnk_tuning/mnk_specific_features.json `
  --out tools/mnk_tuning/mnk_timing_distributions.json
```

This reports nearest-rank `p10`, `p25`, `median`, `p75`, and `p90` values for
RoF/Brotherhood/PB/RoW/potion timing deltas and fight-end distances. Use p75 or
p90 as review material for threshold candidates. This stage still does not
modify `MNK.cs`.

Combine the review candidates and timing distributions into a formula-backed
final review JSON:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py export-mnk-final-tuning-review `
  --candidates tools/mnk_tuning/mnk_tuning_candidates.json `
  --distributions tools/mnk_tuning/mnk_timing_distributions.json `
  --out tools/mnk_tuning/mnk_final_tuning_review.json
```

The final review adds `formula`, `evidence`, and `applyRecommendation` to each
MNKTuningProfile field. `applyRecommendation` is `no_change` only for direct
values that should remain unchanged; all other fields are `hold` and must not be
applied automatically. Review this JSON before deciding any manual fixed-profile
change for `MNK.cs`.

## Current MNK top200 result

The MNK top200 public data pipeline has been completed through these stages:

- `collect-public-top`
- `collect-report-events`
- `extract-cast-features`
- `build-job-samples-batch`
- `extract-job`
- `export-job-profile`
- `extract-mnk-specific-features`
- `export-mnk-tuning-candidates`
- `analyze-mnk-timing-distributions`
- `export-mnk-final-tuning-review`
- `review-mnk-tuning-candidates`

Current result: `sampleCount=200`, `succeeded=200`, `failed=0`. Final review
outputs do not include API secrets, tokens, report codes, report URLs, player
names, character names, server data, guild data, or Lodestone IDs.

## MNK.cs application policy

Do not apply these results to `MNK.cs` automatically. The safe-no-change fields
currently match the default profile, so applying them would not change behavior.
`RoFBrotherhoodResyncWindow=1.0` is unsafe and must not be applied: that field
controls schedule resync and anchor-search tolerance, not only observed RoF/BH
cast delta. `EvenPreRoFPBStartThreshold=5.0` is a candidate, but it narrows the
current `6.0` window and needs separate validation. `RoWUptimeRequired` and
`LastBrotherhoodWindowLeeway` need more data before tuning.

Before changing `EvenPreRoFPBStartThreshold`, count how many top200 samples fall
in the current 5-6 second band that would be excluded by the candidate:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py analyze-even-pre-rof-pb-threshold `
  --in tools/mnk_tuning/mnk_specific_features.json `
  --current 6.0 `
  --candidate 5.0 `
  --out tools/mnk_tuning/even_pre_rof_pb_threshold_review.json
```

If `affectedByCandidate` is high, do not apply the candidate to `MNK.cs`. This
analysis only reads extracted timing features and does not modify runtime code.

To compare several threshold candidates before combat testing, run the sweep
review:

```powershell
py -3 tools/mnk_tuning/mnk_tuning_tool.py analyze-even-pre-rof-pb-threshold-sweep `
  --in tools/mnk_tuning/mnk_specific_features.json `
  --thresholds 5.0,5.5,6.0 `
  --out tools/mnk_tuning/even_pre_rof_pb_threshold_sweep_review.json
```

The sweep reports allowed, blocked, and additional blocked samples compared with
the current `6.0` threshold. It still does not modify `MNK.cs`.

Current classification:

- `safe_no_change`: `MajorBurstMeleeSafeGCDs`, `PBMeleeSafeGCDs`,
  `TargetAvailableUptimeRequired`, `PotionFutureWindowLeeway`,
  `RoWTwoMinuteHoldWindow`, `RoWEndBurnLeeway`, `LastRoFWindowLeeway`,
  `LastPotionWindowLeeway`
- `possible_after_review`: `EvenPreRoFPBStartThreshold`
- `unsafe_hold`: `RoFBrotherhoodResyncWindow`
- `needs_more_data`: `RoWUptimeRequired`, `LastBrotherhoodWindowLeeway`

Next steps:

- Validate only `EvenPreRoFPBStartThreshold=5.0` in a separate review.
- Add downtime and uptime event extraction before tuning `RoWUptimeRequired`.
- Add fight-end Brotherhood analysis before tuning `LastBrotherhoodWindowLeeway`.
- Reuse the same pipeline for other jobs after MNK review is stable.

## Completed Job Pipeline Status

Current external-data pipeline status:

- `MNK`: top200 completed with `succeeded=200` and `failed=0`. MNK-specific
  features, timing distributions, and final tuning review were generated.
  `EvenPreRoFPBStartThreshold` was reviewed separately, and only that value was
  later changed in `MNK.cs` from `6.0` to `5.5`. Other MNK tuning fields remain
  `hold` or `no_change`.
- `NIN`: top200 completed with `succeeded=200` and `failed=0`. NIN-specific
  features were generated. The pre-Raiton subset selected `17` samples
  (`8.5%`). Opener/even burst broadly matched the observed pre-Raiton order;
  odd burst is not applicable to that subset. `NIN.cs` was not changed from
  this review.
- `BLM`: top200 completed with `succeeded=200` and `failed=0`. BLM-specific
  features and timing review were generated. The Standard57 opener broadly
  matches the observed order. `BLM.cs` was not changed from this review.
- `VPR`: top200 completed with `succeeded=200` and `failed=0`. VPR-specific
  features and timing review were generated. The review remains `review_only`
  / `hold`. `VPR.cs` was not changed from this review.

Safety policy:

- External data is used for review and candidate generation.
- Rotation files are not automatically rewritten.
- Final profile outputs do not contain report codes, player names, server data,
  guild data, Lodestone IDs, report URLs, API tokens, or secrets.
- Any gameplay logic change must be a separate minimal patch after review.

Next possible jobs:

- Extend the same pipeline to `RPR`, `SAM`, `DRG`, `MCH`, `PLD`, and other jobs.
- Add job-specific ability maps one job at a time.
- Do not run all jobs blindly; validate each job with `limit 10`, then `50`,
  then `200`.
