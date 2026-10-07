# FFLogs ソース追加 設計(統合タイムラインの追補)

日付: 2026-09-26
親仕様: `docs/superpowers/specs/2026-09-26-unified-timeline-design.md`
前提調査: memory `fflogs-api-spike`(2026-09-26 の spike で API の対応関係を確認済み)

## 1. 目的

自分のリプレイが無いコンテンツ(未実装 92 件: ARR/HW の Hard ダンジョン、L50〜60 トライアル、Alexander normal、新規 5 件)のタイムラインを、FFLogs の公開レポートから生成してユーザーフォルダに置けるようにする。生成物は既存の Replay 由来と同じ形式で、既存の抽出器のマージ・確信度・支配 ID 除外をそのまま使う。

## 2. 確認済みの API 対応(spike)

- 認証: client-credentials(`https://www.fflogs.com/oauth/token`)、環境変数 `FFLOGS_CLIENT_ID` / `FFLOGS_CLIENT_SECRET`。GraphQL は `https://www.fflogs.com/api/v2/client`。
- `report.fights[].gameZone.id` = TerritoryType。`masterData.actors(type:"NPC")[].gameID` = BMR の OID、`subType == "Boss"`。
- `events(dataType: Casts, hostilityType: Enemies)`: `begincast`(`duration` ms)/`cast`(`abilityGameID`)。`dataType: Deaths`: `death`(`targetID`, `targetInstance`)。`dataType: All, filterExpression: "type = 'targetabilityupdate'"`: `targetable` 0/1(変化のみ)。
- ダンジョンは 1 fight = 1 周(トラッシュ込み)。トライアル/レイドは 1 fight = 1 pull。
- コスト約 5 pt / fight、上限 3,600 pt/h。

## 3. 決定事項

- `TimelineSource` に `FFLogs` を追加(既存の値の後、= 4)。順位は Replay > User > FFLogs > Cactbot > EventTrigger。
- 新ツール `tools/fflogs_timeline_extract`(Dalamud dev ライブラリ参照は既存ツールと同じ)。抽出ロジックは本体の `ReplayTimelineExtractor` を使い、ツールは「レポート取得 → 合成 `Replay` 構築 → `Extract` → JSON 出力」だけを行う。
- 合成 `Replay` の規則:
  - Participant は FFLogs の (actorID, instance) ごと。`OID = gameID`、`Type = Enemy`、`ZoneID = fight.gameZone.id`。存在区間 = その actor の最初のイベント時刻 〜 death(なければ fight 終了)。`TargetableHistory` は存在開始で true、以後 `targetabilityupdate` を反映。`DeadHistory` は death 時刻で true。`HPMPHistory` は Boss なら MaxHP 1,000,000、それ以外 10,000(抽出器の 50% ルールでボスだけが `BossOIDs` になる)。`HasAnyActions = true`。
  - `Casts`: `begincast` → `Replay.Cast(ActionID(Spell, abilityGameID), duration/1000)`、`Time = (ts, ts + duration)`。
  - `Actions`: `cast` → `Replay.Action(ActionID(Spell, abilityGameID), ts, source, null, ...)`。
  - `Encounters` は作らない(ダンジョンの fight は 1 周分なので、抽出器の推定 pull 分割 = ボス初動 anchor に任せる)。
  - 時刻は `DateTime.UnixEpoch + report.startTime + timestamp(ms)`。
- CLI: `FFLogsTimelineExtract --territory <id> --fflogs-zone <id> [--encounter <id>] [--reports N=10] [--max-fights M=20] [--out <dir>] [--self-test]`。`reportData.reports(zoneID, limit: N)` から fight を集め、`gameZone.id == territory` かつ `kill == true` のものを最大 M 件処理。結果は `<territory>-fflogs.json`(`{"Timelines":[...]}`、`Source = FFLogs`、`SourceFile = "fflogs:<code>:<fight>;..."`)。stdout に fight 数、窓数、確信度。
- ハーネス `tools/timeline_hint_harness` に `--timelines <dir>` を追加(`TimelineStore.UserDirectory` を差し替えて `Reload`)。これで「FFLogs 由来タイムライン vs 自リプレイ真値」を `--source FFLogs` で測れる。
- 検証セット: 1345 Clyteum と 1304 San d'Oria(自リプレイあり)。採否は既存の `--sync all` と同じ基準(false alarm を増やさず hit を増やす)。

### 改訂(2026-09-26)

実装と計測(`docs/superpowers/plans/2026-09-26-unified-timeline-results.md` の「FFLogs ソース」節)で、合成 `Replay` の規則を次の 3 点で変えた。上の本文はそのまま残し、食い違う箇所はこちらが優先する。

1. **存在区間の終端は fight 終了ではなく、その actor の最後のイベント**(death があれば death)。死なないヘルパー actor が fight 終了まで生き続けて 5 秒ギャップの pull 分割を橋渡しし、ダンジョンのボスが 1 つの pull に融合したため。副作用として death の無いボスは最後の詠唱で終わる。
2. **プレイヤーのダメージを一度も受けていない非ボス actor は、出現時から untargetable のヘルパー**として扱う(`TargetableHistory` の初期値 false)。FFLogs の `targetabilityupdate` は変化しか出ないので、最初から狙えない actor には一度も update が来ず、初期値 true のままでは消失フェーズ中も「狙える敵」として残って NoTarget 窓が 1 つも出なかった。ボス(`subType == "Boss"`)はダメージの有無によらず targetable。
3. **4 つ目のイベント問い合わせ**として `events(dataType: DamageDone, hostilityType: Friendlies)` を取得し、その `targetID`/`targetInstance` を「攻撃対象になった actor」の印(`IsTargetOfAnyActions`)にする。2 の判別はこれで行う(fight あたり数 pt 追加)。

## 4. 対象外

FFLogs レポートの自動選別(パーティ火力による位相ずれの補正)、非公開レポート、UI からの取得。

## 5. テスト

- 本体: `TimelineStore.Rank` の Check に FFLogs を加える。
- ツール: `--self-test` で固定のイベント JSON(spike で取れた形)から合成 `Replay` を作り、Participant 数・BossOIDs・窓・同期点を Check/Require で検証。API は叩かない。
