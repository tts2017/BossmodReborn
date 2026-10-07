# FFLogs ソース追加 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** FFLogs の公開レポートから、自分のリプレイが無いコンテンツのタイムライン(`Source = FFLogs`)を生成し、ユーザーフォルダ経由でローテ予告に使えるようにする。

**Architecture:** `TimelineSource.FFLogs` を追加し順位に組み込む。新ツール `tools/fflogs_timeline_extract` が GraphQL でレポートを取り、FFLogs イベントを合成 `Replay` に変換して既存の `ReplayTimelineExtractor.Extract` に渡す。ハーネスに `--timelines <dir>` を足して FFLogs 由来を自リプレイ真値で測る。

**Tech Stack:** C# / .NET 10、HttpClient + System.Text.Json、Dalamud dev ライブラリ参照(既存ツールと同じ csproj 形)、Check/Require 方式の自己テスト。

**Spec:** `docs/superpowers/specs/2026-09-26-fflogs-source-design.md`(親: `2026-09-26-unified-timeline-design.md`)

## Global Constraints

- 作業場所 `F:\bossmodreborn\.claude\worktrees\kind-bassi-424582`、ブランチ `claude/timeline-deploy-20260926`(nin + upstream + 統合タイムライン)。`git push` 禁止、`git add -A` 禁止。
- 本体ビルド `dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64`。ツールは `BossMod/bin/x64/Debug/BossModReborn.dll` を参照。
- 認証情報は環境変数 `FFLOGS_CLIENT_ID` / `FFLOGS_CLIENT_SECRET` からのみ読む。値をログ・レポート・コミットに出さない。トークンはメモリ内のみ(ファイルに書かない)。
- API 呼び出しは `--self-test` では一切行わない。実行は 1 ゾーンあたり `--reports 10 --max-fights 20` 以内(約 100 pt)。
- 合成 `Replay` の規則は spec §3 のとおり(`Encounters` は作らない、Boss は MaxHP 1,000,000、その他 10,000)。
- 既存の regression ツール(external_timeline_regression、timeline_regression)は全件合格を維持。マニフェスト JSON は再生成しない(生成器は enum 追加のみ)。
- コメントは英語。文書は通常の日本語。

## Review Focus

1. **レポートに fight が無い / 全 fight が wipe**: ツールはエラーで落ちず `fights=0` を出力し、ファイルを書かない。→ Task 2 の self-test「no kill fights → no output」。
2. **同一 actorID の instance 再利用(`targetInstance` 2 以上)**: 別 Participant として扱い、死亡時刻が混ざらないこと。→ Task 2 の self-test「instances are separate participants」。
3. **targetabilityupdate が無い敵**: プレイヤーのダメージを受けた actor(とボス)は存在区間中ずっと targetable として NoTarget 計算に入ること。一度もダメージを受けていない非ボス actor はヘルパーであり、targetable では**ない**(出現時から untargetable、NoTarget 窓を塞がない)。→ Task 2 の self-test「no updates means targetable」「an enemy that never took player damage is an untargetable helper」「a boss that never took damage is still attackable」。
4. **ボスが複数(ダンジョン 3 ボス)の 1 fight**: 抽出器がボスごとに pull を分け、`Extract` が sequence 3 本を返すこと。→ Task 2 の self-test「dungeon fight splits into boss pulls」。
5. **FFLogs と自リプレイの両方がある zone**: 順位で Replay が勝つこと。→ Task 1 の Check。

---

### Task 1: `TimelineSource.FFLogs` と順位

**Files:**
- Modify: `BossMod/Timeline/External/ExternalPlannerTimeline.cs:178`(enum)
- Modify: `BossMod/Timeline/External/TimelineStore.cs:101-107`(Priority)
- Modify: `tools/encounter_timeline/ExternalTimelineManifestGenerator.cs:21`(mirror enum)
- Modify: `tools/external_timeline_regression/Program.cs:261-266`(Check)

**Interfaces:**
- Produces: `ExternalPlannerTimeline.TimelineSource { EventTrigger, Cactbot, Replay, User, FFLogs }`(FFLogs = 4)。順位 Replay 4 > User 3 > FFLogs 2 > Cactbot 1 > EventTrigger 0。

- [ ] **Step 1: 失敗する Check** — `tools/external_timeline_regression/Program.cs` の "priority order" Check を次に置き換える:

```csharp
    Check("priority order: replay > user > fflogs > cactbot > event trigger, then confidence", () =>
    {
        ExternalPlannerTimeline.TimelineDefinition Make(ExternalPlannerTimeline.TimelineSource source, float confidence) => new(5, source.ToString(), true, [], source, confidence);
        var ranked = TimelineStore.Rank([Make(ExternalPlannerTimeline.TimelineSource.EventTrigger, 1f), Make(ExternalPlannerTimeline.TimelineSource.Cactbot, 1f), Make(ExternalPlannerTimeline.TimelineSource.FFLogs, 1f), Make(ExternalPlannerTimeline.TimelineSource.Replay, 0.3f), Make(ExternalPlannerTimeline.TimelineSource.Replay, 0.8f), Make(ExternalPlannerTimeline.TimelineSource.User, 1f)]);
        Require(ranked.Select(t => t.Source).SequenceEqual([ExternalPlannerTimeline.TimelineSource.Replay, ExternalPlannerTimeline.TimelineSource.Replay, ExternalPlannerTimeline.TimelineSource.User, ExternalPlannerTimeline.TimelineSource.FFLogs, ExternalPlannerTimeline.TimelineSource.Cactbot, ExternalPlannerTimeline.TimelineSource.EventTrigger]), "rank order");
        Require(ranked[0].Confidence == 0.8f, "confidence tie-break");
    });
```

- [ ] **Step 2: ビルドして失敗確認** — `dotnet build tools/external_timeline_regression -c Debug` → `FFLogs` が無くエラー。
- [ ] **Step 3: 実装** — enum を `{ EventTrigger, Cactbot, Replay, User, FFLogs }` に(本体と生成器の mirror `ExternalTimelineSource` の両方、末尾追加)。`TimelineStore.Priority` を `Replay => 4, User => 3, FFLogs => 2, Cactbot => 1, _ => 0` に。
- [ ] **Step 4: ビルド・実行** — 本体 Debug x64 → regression 2 本とも `failed=0`。
- [ ] **Step 5: Commit** — `feat(timeline): add the FFLogs timeline source ranked below own replays and user edits`

---

### Task 2: `tools/fflogs_timeline_extract`(クライアント・変換・自己テスト・CLI)

**Files:**
- Create: `tools/fflogs_timeline_extract/FFLogsTimelineExtract.csproj`(`tools/timeline_regression/TimelineRegression.csproj` をコピー、`<AssemblyName>` 不要)
- Create: `tools/fflogs_timeline_extract/FFLogsClient.cs`
- Create: `tools/fflogs_timeline_extract/FFLogsReplayBuilder.cs`
- Create: `tools/fflogs_timeline_extract/Program.cs`

**Interfaces:**
- Consumes: `ReplayTimelineExtractor.Extract(IEnumerable<Replay>)`, `ReplayTimelineExtractor.FindPulls`, `TimelineStore.LoadUserFile`, `Replay`/`Replay.Participant`/`Replay.Cast`/`Replay.Action`(`BossMod/Replay/Replay.cs`)、`ActionID(ActionType.Spell, id)`、`ActorHPMP(cur, max, shield, curMP, maxMP)`。
- Produces:
  ```csharp
  // FFLogsClient.cs
  public sealed class FFLogsClient(string clientId, string clientSecret) : IDisposable
  {
      public Task<string> TokenAsync();                                   // client-credentials, cached in memory
      public Task<JsonElement> QueryAsync(string query, object variables); // returns "data"; throws on "errors"
  }
  // FFLogsReplayBuilder.cs — pure, no network
  public sealed record FFLogsFight(string Code, int Id, long ReportStartMs, double StartMs, double EndMs, ushort Zone, bool Kill);
  public sealed record FFLogsActor(int Id, uint GameId, string Name, bool IsBoss);
  public static class FFLogsReplayBuilder
  {
      // events: the raw JSON arrays returned by the three event queries (Casts, Deaths, targetabilityupdate)
      public static Replay Build(FFLogsFight fight, IReadOnlyList<FFLogsActor> actors, JsonElement casts, JsonElement deaths, JsonElement targetability);
  }
  ```

- [ ] **Step 1: 自己テストを書く(失敗する)** — `Program.cs` に `--self-test` 分岐を作り、次の Check を置く(fixture は文字列リテラルの JSON):

```csharp
static int SelfTest()
{
    var failures = new List<string>(); var tests = 0;
    void Check(string name, Action test) { ++tests; try { test(); } catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); } }
    void Require(bool c, string m) { if (!c) throw new InvalidOperationException(m); }
    var fight = new FFLogsFight("TEST", 1, 1_700_000_000_000, 10_000, 400_000, 1203, true);
    var actors = new List<FFLogsActor> { new(58, 0x4234, "Barreltender", true), new(65, 0x41BE, "Anthracite", true), new(70, 0x4B78, "Petromole", false) };
    JsonElement J(string s) => JsonDocument.Parse(s).RootElement;
    // two bosses in one fight: boss A acts 20-100 s, boss B 200-380 s; trash 12-18 s; boss B vanishes 250-270 (targetability), trash instance 2 dies at 300
    var casts = J("""[
      {"timestamp":22000,"type":"begincast","sourceID":58,"abilityGameID":1000,"duration":3000},{"timestamp":25000,"type":"cast","sourceID":58,"abilityGameID":1000},
      {"timestamp":60000,"type":"begincast","sourceID":58,"abilityGameID":1001,"duration":2000},{"timestamp":62000,"type":"cast","sourceID":58,"abilityGameID":1001},
      {"timestamp":205000,"type":"begincast","sourceID":65,"abilityGameID":2000,"duration":3000},{"timestamp":208000,"type":"cast","sourceID":65,"abilityGameID":2000},
      {"timestamp":300000,"type":"begincast","sourceID":65,"abilityGameID":2001,"duration":3000},{"timestamp":303000,"type":"cast","sourceID":65,"abilityGameID":2001},
      {"timestamp":13000,"type":"cast","sourceID":70,"sourceInstance":1,"abilityGameID":7},
      {"timestamp":290000,"type":"cast","sourceID":70,"sourceInstance":2,"abilityGameID":7}]""");
    var deaths = J("""[
      {"timestamp":100000,"type":"death","targetID":58},
      {"timestamp":18000,"type":"death","targetID":70,"targetInstance":1},
      {"timestamp":300000,"type":"death","targetID":70,"targetInstance":2},
      {"timestamp":380000,"type":"death","targetID":65}]""");
    var targetability = J("""[
      {"timestamp":250000,"type":"targetabilityupdate","sourceID":65,"targetID":65,"targetable":0},
      {"timestamp":270000,"type":"targetabilityupdate","sourceID":65,"targetID":65,"targetable":1}]""");
    var replay = FFLogsReplayBuilder.Build(fight, actors, casts, deaths, targetability);
    Check("participants: one per actor instance, boss HP marks bosses", () =>
    {
        Require(replay.Participants.Count == 4, $"participants={replay.Participants.Count}");
        var a = replay.Participants.Single(p => p.OID == 0x4234);
        Require(a.HPMPHistory.Values.First().MaxHP == 1_000_000 && replay.Participants.Single(p => p.OID == 0x4B78 && p.InstanceID != replay.Participants.First(q => q.OID == 0x4B78).InstanceID) != null, "boss hp / instances");
        Require(a.ZoneID == 1203, "zone");
    });
    Check("instances are separate participants with their own death", () =>
    {
        var trash = replay.Participants.Where(p => p.OID == 0x4B78).OrderBy(p => p.WorldExistence[0].Start).ToList();
        Require(trash.Count == 2, "two instances");
        Require(trash[0].DeadHistory.Keys.Single() == fight.ToTime(18000) && trash[1].DeadHistory.Keys.Single() == fight.ToTime(300000), "deaths per instance");
    });
    Check("no updates means targetable for the whole existence", () =>
    {
        var a = replay.Participants.Single(p => p.OID == 0x4234);
        Require(a.TargetableHistory.Count == 1 && a.TargetableHistory.Values[0], "targetable from spawn");
        Require(a.WorldExistence[0].Start == fight.ToTime(22000) && a.WorldExistence[0].End == fight.ToTime(100000), "existence = first event .. death");
    });
    Check("targetability updates and casts/actions land on the boss", () =>
    {
        var b = replay.Participants.Single(p => p.OID == 0x41BE);
        Require(b.TargetableHistory.Count == 3 && !b.TargetableAt(fight.ToTime(260000)) && b.TargetableAt(fight.ToTime(275000)), "targetability");
        Require(b.Casts.Count == 2 && b.Casts[0].ID.ID == 2000 && Math.Abs(b.Casts[0].ExpectedCastTime - 3f) < 0.01f, "casts");
        Require(replay.Actions.Count(x => x.Source == b) == 2, "actions");
    });
    Check("dungeon fight splits into boss pulls and Extract yields two sequences with the vanish window", () =>
    {
        var pulls = ReplayTimelineExtractor.FindPulls(replay);
        Require(pulls.Count == 2, $"pulls={pulls.Count}");
        var tl = ReplayTimelineExtractor.Extract([replay])[1203];
        Require(tl.Source == ExternalPlannerTimeline.TimelineSource.FFLogs, "source");
        Require(tl.Sequences.Count == 2 && tl.Sequences[1].BossOIDs!.SequenceEqual([0x41BEu]), "sequences");
        Require(tl.Sequences[1].Windows!.Any(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget && Math.Abs(w.Start - 45) < 0.5 && Math.Abs(w.End!.Value - 65) < 0.5), "vanish window relative to first boss cast at 205 s");
    });
    Check("no kill fights → no output", () => Require(Program.SelectFights([new FFLogsFight("X", 1, 0, 0, 1000, 1203, false)], 1203, 20).Count == 0, "wipe kept"));
    foreach (var f in failures) Console.Error.WriteLine(f);
    Console.WriteLine($"tests={tests} passed={tests - failures.Count} failed={failures.Count}; source=synthetic; replay=none");
    return failures.Count == 0 ? 0 : 1;
}
```

`FFLogsFight.ToTime(double ms) => DateTime.UnixEpoch.AddMilliseconds(ReportStartMs + ms)` をレコードに持たせる。`Extract` の Source は `Replay` 固定なので、ツール側で `tl with { Source = FFLogs, SourceFile = ... }` に付け替える(Check の "source" はその付け替え後を見る。`Extract` の戻りを加工するヘルパ `Program.Relabel(IReadOnlyDictionary<ushort, TimelineDefinition>, string sourceFile)` を作り、Check ではそれを使う)。

- [ ] **Step 2: ビルドして失敗確認** — 型が無いためエラー。
- [ ] **Step 3: 実装**
  - `FFLogsClient`: `HttpClient`、`TokenAsync` は `POST /oauth/token`(Basic 認証、`grant_type=client_credentials`)を 1 回だけ、`QueryAsync` は Bearer で `POST /api/v2/client`、`errors` があれば `InvalidOperationException` にメッセージを入れて投げる。
  - `FFLogsReplayBuilder.Build`: 3 つのイベント配列を時刻順に走査。Participant キーは `(sourceID or targetID, instance ?? 1)`。存在開始 = そのキーの最初のイベント時刻、終了 = death 時刻(なければ `EndMs`)。`WorldExistence.Add(new(start, end))`、`EffectiveExistence` 同値、`TargetableHistory[start] = true` + update 反映、`DeadHistory[death] = true`、`HPMPHistory[start] = new(max, max, 0, 0, 0)`、`HasAnyActions = true`、`Type = ActorType.Enemy`、`ZoneID = fight.Zone`。`begincast` → `Casts.Add(new(new ActionID(ActionType.Spell, id), duration/1000f, null, default, default, false) { Time = new(ts, ts + duration) })`。`cast` → `replay.Actions.Add(new(new ActionID(ActionType.Spell, id), ts, participant, null, default, 0.6f, 0, 0, default))`。actor リストに無い sourceID は無視。
  - `Program`: 引数解析(`--territory`, `--fflogs-zone`, `--encounter`, `--reports`(10), `--max-fights`(20), `--out`, `--self-test`)。フロー: token → `reportData.reports(zoneID, limit)` で `code, startTime, fights { id encounterID startTime endTime kill gameZone { id } }` → `SelectFights`(kill かつ gameZone==territory、encounter 指定時はそれも、最大 M)→ 各 fight で `report(code) { masterData { actors(type:"NPC") { id gameID name subType } } fights(fightIDs:[id]) { enemyNPCs { id } } }` と events 3 クエリ(`limit: 10000`、`nextPageTimestamp` があれば追加取得)→ `Build` → `Extract(replays)` → `Relabel` → `<territory>-fflogs.json` を `--out`(既定 `%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\timelines`)に書き、`TimelineStore.LoadUserFile` で読み戻して 1 件であることを確認。stdout: 各 fight の `code fight=<id> len=<s> enemies=<n> casts=<n> deaths=<n> targetability=<n>`、最後に `fights=<n> sequences=<n> windows=<n> no_target=<n> confidence=<f> points_spent=<rateLimitData.pointsSpentThisHour>`。fight が 0 ならファイルを書かず終了コード 1。
  - 認証: `Environment.GetEnvironmentVariable` から取得、無ければ usage を出して終了コード 2。
- [ ] **Step 4: 自己テスト実行** — `dotnet build tools/fflogs_timeline_extract -c Debug && dotnet tools/fflogs_timeline_extract/bin/Debug/net10.0-windows10.0.26100.0/FFLogsTimelineExtract.dll --self-test` → `failed=0`。
- [ ] **Step 5: Commit** — `feat(tools): fflogs_timeline_extract builds replay-style timelines from public FFLogs reports`

---

### Task 3: ハーネス `--timelines` と実測

**Files:**
- Modify: `tools/timeline_hint_harness/Program.cs`(引数 `--timelines <dir>` → `TimelineStore.UserDirectory = dir; TimelineStore.Reload();` を `Service.Config` 初期化の直後に)
- Modify: `docs/superpowers/plans/2026-09-26-unified-timeline-results.md`(FFLogs 節を追記)

- [ ] **Step 1: ハーネス引数追加** — `else if (args[i] == "--timelines") timelinesDir = args[++i];` と、設定後に `UserDirectory`/`Reload`。集計行に `timelines=<dir or ->` を足す。
- [ ] **Step 2: 実行** — 一時フォルダ `%TEMP%\tl_fflogs` に、Clyteum(`--territory 1345 --fflogs-zone 57 --encounter 4551`)、San d'Oria(`--territory 1304 --fflogs-zone 70`)、Tender Valley(`--territory 1203 --fflogs-zone 57 --encounter 4540`)を生成。Ageless Necropolis(1295)は FFLogs にノーマル用 zone が無い(Necron は Extreme 1296 のみ)ので対象外。各コマンドの stdout を記録。
- [ ] **Step 3: 計測** — `TimelineHintHarness --timelines %TEMP%\tl_fflogs --source FFLogs --sync all "%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\replays\TheClyteum_*" "...SanDoriaTheSecondWalk_*"`(ファイル指定はディレクトリ引数でよい: `--zone` は無いので Clyteum/San d'Oria のログだけ含む一時フォルダにコピーするか、ハーネスにワイルドカードを渡す。ディレクトリ再帰しか受けないので、`%TEMP%\tl_eval` に該当ログをコピーして渡す)。比較行: 同じログで `--source ranked --sync all`(自リプレイ抽出物は無いので EventTrigger 相当)と、`--timelines <BossModReborn\timelines>`(自リプレイ由来)の 3 条件。
- [ ] **Step 4: 結果を文書へ** — results.md に「FFLogs ソース」節: 生成統計(fight 数・窓・確信度)と 3 条件の集計行、所見(FFLogs 由来が自リプレイ真値に対してどれだけ当たるか)。
- [ ] **Step 5: 配置用ファイル** — 判定が「false alarm を増やさない」なら、`%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\timelines` に Tender Valley / Ageless の `-fflogs.json` を置く(Clyteum / San d'Oria は自リプレイ由来が既にあり順位で勝つので置いても害はないが、置かない)。
- [ ] **Step 6: Commit** — `feat(tools): let the timeline hint harness load a timelines folder, and record FFLogs measurements`
