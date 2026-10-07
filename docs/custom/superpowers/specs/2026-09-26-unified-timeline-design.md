# 統合コンテンツタイムライン設計

日付: 2026-09-26
ブランチ: nin-prekassatsu3rd-stability
前提調査: 2026-09-24 の外部タイムライン監査(memory `external-timeline-audit-2026-09-24`)

## 1. 目的と範囲

**目的:** ローテーション組み立ての予告(ターゲット消失・復帰・バースト整合)に、あらゆるコンテンツのタイムラインを参照できるようにする。高難易度以外・未対応コンテンツも再ビルドなしで後から追加でき、自分のリプレイから自動生成できること。

**対象外:** CooldownPlanner 用 state machine 置換(`ExternalPlannerTimeline`)の意味変更、UI 表示、raidwide/tankbuster 予告の改善、自前 BossModule の state machine の統合形式への書き出し。

**成功基準:** リプレイ再生で本物のフォロワーを回す精度ハーネスで、現行相当の設定に対し的中率が上がり、誤予告と時刻誤差が下がること。既存ハーネス(xan_timeline_harness、external_timeline_regression)は不変。

## 2. 現状と問題(監査結果の要約)

- データ `BossMod/Timeline/External/ExternalTimelines.json`(event-trigger 由来 309 本、埋め込みリソース)。消費側は `ExternalPlannerTimeline`(既定オフ)と `ExternalTimelineHints`(既定オン)。
- フォロワーは CastStart(StartsUsing 行)でしか時計を整合しない。CastStart は 1,010 state、Ability 行由来の同期点候補約 28,000 は未使用。103/309 本は CastStart が 0 で、実際のダウンタイムを 1 回観測するまで予告が出ない。
- `--untargetable--` はボス対象不可であって「殴れる敵なし」ではない。8.5 秒以上の予告窓 230 のうち 121 が adds フェーズ。
- `OnCombatChanged` は常に最初の Sequence に anchor するため、ダンジョンの 2・3 ボスは 1 ボス目の時計で始まる。
- 短い窓(8.5 秒未満)が直後の長い窓を隠す(r10n/r10s)。
- フォロワーはどのハーネスにも乗っていない。

## 3. 決定事項

| 項目 | 決定 |
|------|------|
| 主用途 | ローテ予告 |
| ソース | event-trigger + cactbot(timelineFile 経由)+ 自リプレイ抽出 |
| 置き場 | 埋め込み既定 + ユーザーディレクトリ上書き |
| 抽出 | 本体ライブラリ + 第1弾 CLI(UI は後続) |
| ダウンタイム意味論 | BossUntargetable と NoTarget を別フィールドで持ち、ローテは NoTarget を読む |
| 採否ゲート | リプレイ再生の精度ハーネス |
| 方式 | 現行 manifest 拡張(同期点を最大限残し、リプレイ由来を最優先) |

## 4. データ形式

現行 `TimelineDefinition(ZoneID, SourceFile, AutomaticFallback, Sequences[])` を後方互換で拡張する。既存フィールドは無変更、追加フィールドは省略可。

- **Timeline**: `ZoneID`, `SourceFile`, `AutomaticFallback`, `Source`(EventTrigger / Cactbot / Replay / User)、`Confidence`(0〜1)、`Sequences[]`
- **Sequence**: `Index`, `StartTime`, `States[]`, `PredictionEndTime`, 新規 `BossOIDs[]`(リプレイ由来のみ、テキスト系は空)、新規 `Windows[]`
- **State**(同期点): `Time`, `Name`, `Kind`, `IDs`, `Hint`。`Kind` に `AbilityUsed`(Ability 行 = 効果発生。`Actors.CastEvent` で整合)を追加する。テキスト系の Ability 行は現行 `Timeout` + IDs から `AbilityUsed` に変わる。
- **Window**: `Kind`(BossUntargetable / NoTarget / AddsPresent)、`Start`、`End`(null = 不明)、`Confidence`
  - テキスト系: BossUntargetable は `--untargetable--` / `--targetable--` から。NoTarget は窓内にボス以外の source の詠唱、AddedCombatant、`adds` を含む名前がない場合のみ。あれば AddsPresent。
  - リプレイ: 全 enemy participant の TargetableHistory、生存、非 ally から NoTarget を直接計算する。

planner 置換経路(`ExternalPlannerTimeline`)は `Timeout`/`AbilityUsed` を同じ扱い(単純タイムアウト)にし、挙動を変えない。

## 5. ローダとマージ

**`TimelineStore`**(新規、`BossMod/Timeline/External/`):
- 起動時に埋め込み `ExternalTimelines.json` と `<pluginConfigs>/BossMod/timelines/*.json` を読む。ユーザー側は複数ファイル可、1 ファイルに任意数の Timeline。
- zone ごとに候補を集め、優先順 Replay > User > Cactbot > EventTrigger、同順位は Confidence の高い方。上書きは Timeline 単位で、部分マージはしない(ソース間で同期点の時刻基準が違うため)。
- `ForZone(zone)` は最優先 1 本、`CandidatesForZone(zone)` は全候補を返す。
- ファイル監視はしない。設定画面の「再読込」で読み直す。
- `ExternalPlannerTimeline` は `TimelineStore.ForZone` を使う。`AutomaticFallback` の意味は不変。

**設定:** `BossModuleConfig.TimelineUserDirectory`(既定 `<pluginConfigs>/BossMod/timelines`)。`UseExternalTimelineHints` は既存。

**生成器(`tools/encounter_timeline`):**
- `CactbotTriggerCatalog` に `timelineFile` の解析を足し、cactbot の timeline .txt(312 本)を zone に対応付ける。Source=Cactbot で出力する。
- event-trigger 309 本は Source=EventTrigger。埋め込み JSON は両方を含み、同 zone の優先はローダ側で決める。
- `Windows` 生成規則(§4)を両ソースに適用する。zone 134 の test.txt は除外する。

## 6. フォロワー改修(`ExternalTimelineHints`)

**同期点:**
- `CastStarted` に加え `Actors.CastEvent` を購読し、`AbilityUsed` 状態と ID を照合する。
- `AlignmentScore` は CastStart と AbilityUsed の両方を対象にする。閾値(新しい時計は 2、既存時計の維持は 1)は現行を保ち、精度ハーネスで調整する。
- リプレイ由来 Sequence は `BossOIDs` を持つので、pull 時に戦闘に入った敵の OID で Sequence を直接選ぶ。

**Sequence 選択:** `OnCombatChanged` の「最初の Sequence 固定」をやめ、(1) OID 一致、(2) 直近 pull で確定した Sequence の次、(3) 先頭、の順で仮 anchor する。`_confirmed=false` は現行どおり。

**追跡対象:** `FindTrackedEnemy` は「最初の InCombat 敵」から「Sequence の BossOIDs、無ければ InCombat 敵の中で最大 HP」へ。

**配信:**
- `NextDowntime()` は `Windows` の NoTarget を読む。Windows が無い Timeline は現行の Untargetable 状態列にフォールバックするが、AddsPresent と重なる窓は除外する。
- 「最初の未完了の窓」ではなく「Horizon 内で 8.5 秒以上の最初の窓」を返す。
- 窓の Confidence が 0.5 未満なら配信しない。
- `CheckAgainstWorld` は「予測 NoTarget と、実際に targetable な敵がいるか」で矛盾を判定する。

**不変:** Horizon 25 秒、MinPublishedLoss 8.5 秒、Sync 寿命 60 秒、namespace と Push 経路、ローテ側 `MechanicHints` の読み方。

## 7. リプレイ抽出

**ライブラリ** `BossMod/Timeline/External/ReplayTimelineExtractor.cs`(Dalamud 非依存):
- 入力は `Replay` 1 本以上。単位は pull。`Encounter` があればそれ、無ければ「敵が InCombat になってから全滅または離脱まで」を自前で区切る(未対応コンテンツ向け)。
- `BossOIDs`: pull 中に存在した非 ally 敵のうち最大 HP 上位。トラッシュ pull(継続 20 秒未満、または HP が小さい)は除外。
- 同期点: 敵の `Casts` の開始 → CastStart、`Actions` の効果発生 → AbilityUsed。時刻は pull 基準秒、ID は ActionID.ID。
- 窓: NoTarget は「生存・非 ally・targetable な敵が 0」の区間、BossUntargetable は BossOIDs の TargetableHistory、AddsPresent は NoTarget でない BossUntargetable 区間。
- 複数 pull の統合(同 zone・同 BossOIDs): 同期点は ID と順序で対応付け、時刻は中央値。半数未満の pull にしか現れないものは落とす。窓は開始・終了とも中央値、四分位幅が 5 秒を超えたら Confidence を下げる(1 − IQR/20、下限 0.2)。
- ダンジョンは pull ごとに 1 Sequence、StartTime=0。

**CLI** `tools/replay_timeline_extract`: `dotnet run -- [--out <dir>] [--zone <id>] <replay.log|dir>...`。zone ごとに `<zone>-replay.json`(Source=Replay)。既定出力先はユーザーディレクトリ。pull 数、窓数、Confidence 分布を stdout に出す。

## 8. 精度ハーネス

**`tools/timeline_hint_harness`**(xan_timeline_harness と同じ参照構成):
- リプレイの Ops を `WorldState` に順次適用し、毎フレーム `ExternalTimelineHints.Update` を回す。本物のフォロワーと本物の `TimelineStore` を使う。
- 正解はリプレイから直接計算した NoTarget 窓(§7 の関数を流用)。
- 指標(zone 別と全体): 的中(8.5 秒以上の実窓のうち開始前に予告が出ていた割合)、リード(初回予告時点の残り秒数の中央値、上限 25)、誤予告(予告後 30 秒以内に実窓が来なかった回数)、時刻誤差(LossIn/ReturnIn と実測の差の中央値と P90)。
- リプレイ由来 Timeline は抽出に使った pull を除いて評価する(leave-one-out)。
- 条件行列: Source(EventTrigger / Cactbot / Replay)× 同期点(CastStart のみ = 現行相当 / CastStart + AbilityUsed)。

**ゲート:** 現行相当を baseline とし、的中が上がり、誤予告と時刻誤差が下がること。悪化した zone は個別に原因を見る。`external_timeline_regression` はフラグ ON 修正込みで全件合格を維持。xan_timeline_harness は全ジョブでトレース完全一致。

## 9. 実装順

1. §4 形式 + 生成器(cactbot 対応、Windows 生成、AbilityUsed)、埋め込み JSON 再生成
2. §5 `TimelineStore`、`ExternalPlannerTimeline` の差し替え、設定項目
3. §7 抽出ライブラリ + CLI
4. §8 ハーネス、baseline 計測
5. §6 フォロワー改修、再計測
6. `external_timeline_regression` のフラグ ON 修正、埋め込み JSON と結果のコミット

## 10. テスト

- 生成器・抽出・マージ: 単体テスト(合成 txt、合成 Replay)。
- フォロワー: 精度ハーネス。
- 既存回帰: external_timeline_regression、xan_timeline_harness。
