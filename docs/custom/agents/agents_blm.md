# agend_blm.md

## 目的

このファイルは、BossMod Reborn / Autorotation の黒魔道士（BLM）実装を Codex で修正・拡張するときの専用作業指示です。  
対象は主に `BossMod.Autorotation.xan.BLM` / `BLM.cs` です。

目標は **ログ上位を狙える実戦寄り黒魔ローテーション** を維持しつつ、以下を壊さないことです。

- GCDを止めない
- AF/UIを落とさない
- 2体から範囲回しへ切り替える
- AoE↔単体のリソース橋渡しを維持する
- Polyglot / Thunderhead / Paradox / Astral Soul を無駄にしない
- Ley Lines / Triplecast / Manafont を固定雑運用にしない
- 外部MechanicHintやSplatoon由来情報は「命令」ではなく「安全な環境ヒント」として使う

---

## 基本方針

### やること

- 小さい差分で修正する
- 既存の命名・構造・Strategy Trackを維持する
- 既存の `FuturePlanner` / `AOEFuturePlanner` の思想を維持する
- 修正後はコンパイルエラーを最優先で潰す
- ロジック変更は「なぜ必要か」をコメントまたはPR説明に残す
- `Force` 系のStrategyはユーザー明示操作として尊重する

### やらないこと

- 大規模リファクタ
- 既存API名の推測変更
- Splatoon Scriptの実行
- 外部URLや外部コードの自動取得
- `ExternalMechanicHintProvider` / `ExternalEncounterHintProvider` を信用しすぎる実装
- 黒魔紋中だからゼノ連打、黒魔紋中だから三連魔、のような固定雑ルール
- AoE/単体切替時にAstral SoulやPolyglotを雑に捨てる処理

---

## 重要な設計ルール

### 1. 2体からAoE

`AOEBreakpoint = 2` を維持する。

- 2体以上ならAoEモードへ入る
- 3体以上では `Freeze` / `Flare` / `Foul` / `High Thunder II` 寄り
- 2体では一部 `Blizzard4` など単体寄りGCDを混ぜてもよい
- AoE判定は標的数で行う(BLM の EncounterHint 分類は 2026-10-03 に削除した。`Trash` / `MajorAdd` に分けてburstを保留する処理は無い)

### 2. 単体 → 複数

単体中に2体以上になった場合は即AoE候補を出す。

確認ポイント:

- `FirePhase` / `IcePhase` が `NumAOETargets >= AOEBreakpoint` を見ていること
- Polyglotは2体以上なら `Xenoglossy` ではなく `Foul`
- AoE DoTは `BestAOEThunderTarget` / `NumAOEDotTargets` を見る
- Standard57 opener中でも2体以上になったらStandard57固定を止める

### 3. 複数 → 単体

AoE後に単体へ戻るときは、Astral Soulを捨てない。

維持すべき挙動:

- `AOEToSingleBridgeActive` / `AOEToSingleTransitionUntil` で短時間bridgeを保持
- 単体火フェーズで `AstralSoul == 6` なら `FlareStar`
- `AstralSoul >= 3 && MP >= 800` なら `Flare` でFlareStarへ繋ぐ余地を残す
- 氷復帰中は `Blizzard3 → Blizzard4` の順を壊さない
- UI1からいきなり `Blizzard4` を押さない

---

## Standard57 opener

Standard57は強いが、常に固定してはいけない。

### 有効条件

`ShouldUseStandard57Timeline(strategy)` のような統一判定で管理する。

含めるべき条件:

- `Standard57ManafontOk(strategy)`
- `Standard57OpenerActive`
- `NumAOETargets < AOEBreakpoint`
- `!timeline.DowntimeSoon`
- `EncounterAllowsStandard57()`

### 注意

- `ShouldSkipStandard57AFParadox(strategy)` も必ず同じStandard57判定を通す
- `Standard57PostManafont` は `OnCooldown(AID.Manafont)` ではなく、実際にManafontを観測したフラグを使う
- 雑魚移行、ボス消失、2体以上、Manafont Delay時にStandard57が干渉しないようにする

---

## FuturePlanner

FuturePlannerは黒魔の「局面評価器」。  
候補を増やすときは、必ず `ApplyPlannerStep` / `PlannerActionTime` / `EffectivePotency` / `TerminalStateValue` との整合を取る。

### 必須状態

PlannerStateには最低限以下を維持する。

- `Element`
- `MP`
- `Hearts`
- `Polyglot`
- `AstralSoul`
- `Paradox`
- `Firestarter`
- `Thunderhead`
- `ThunderLeft`
- `NextPolyglot`
- `ElementTimer`
- `InstantBudget`
- `ActiveInstantBudget`
- `LeyLinesLeft`
- `TransposeReadyIn`
- `ManafontReadyIn`
- `LucidReadyIn`
- `DowntimeIn`
- `UptimeIn`
- `RaidBuffsLeft`
- `RaidBuffsIn`
- `ForcedMoveSoon`
- `AllowBurstActions`
- `AllowManafont`

### Instant管理

Swiftcast / Triplecast は「押せる全部」をまとめて使える扱いにしない。

正しい考え方:

- 既に付いているSwift/Tripleだけ `ActiveInstantBudget`
- これから押す1種類だけ `ReservedInstantAction`
- `ReserveInstant` 実行後に `ActiveInstantBudget += stacks`
- `UsesReservedInstant()` は `ActiveInstantBudget > 0` のときだけtrue

禁止:

- Swiftcastしか予約していないのにTriplecast分までインスタント扱い
- `InstantBudget > 0` だけで詠唱魔法をインスタント扱い
- 三連魔を黒魔紋合わせだけで消費する固定処理

### DTR

DTRは上位向けだが暴発しやすい。

条件:

- `AllowBurstActions`
- AF中
- Firestarterあり
- Instant 2回以上相当
- Transposeが近い
- AF終盤
- 標準ルートより十分強い

比較は甘くしすぎない。  
最低でも `dtr > standard + 20` のような余白を持たせる。

---

## NearManafontBridge

NearManafontBridgeは常用ルートではなく、Manafontリキャ直前だけの特殊候補。

狙い:

```text
Despair
→ Transpose
→ Lucid Dreaming
→ Paradox
→ 高価値instant filler
→ 必要なら2個目filler
→ Transpose
→ AF1 Despair
→ Manafont
```

### 許可条件

- `ManafontReadyIn` はおおむね `5.5f〜8.5f`
- `LucidReadyIn <= GCD`
- `TransposeReadyIn <= GCD`
- `DowntimeIn > 12`
- `RaidBuffsLeft == 0`
- `AstralSoul != 6`
- `MP < FireSpellCostFor(state)`
- fillerが `Xenoglossy` または有効な `Thunder`

### 注意

- Lucid tickは3秒ごと。MP800に届かないルートは無効化されるべき
- AF1 DespairはAF3 Despairより弱い
- Manafontの使用回数やシナジーを失うなら使わない
- 固定回しではなくPlanner候補としてだけ扱う

---

## Polyglot / Xenoglossy / Foul

ゼノ連打は基本禁止。  
黒魔紋中だからゼノ連打、シナジー中だから全吐き、は危険。

撃つ条件:

- overcapしそう
- Amplifier前に空きを作る
- RaidBuff中。ただし連打抑制あり
- ForcedMoveSoon / IsMoving
- Downtime直前
- Standard57の指定位置
- AoEなら `Foul`

維持すること:

- `RecentlySpentPolyglot()` による連打抑制
- PlannerStateの `UsedPolyglotRecently`
- `ShouldSpendPolyglotNow(strategy)` の安全判定

---

## Thunder

ThunderはDoT維持だけでなく、移動用instantでもある。  
ただし短命敵には撃たない。

### 維持すること

- `ThunderRefreshWindow`
- `PlannerMovementThunderRefreshWindow`
- `ThunderMinTargetLife`
- `ThunderLowHPNoRefreshRatio`
- `ThunderTargetWorthDot`
- `EstimatedThunderTargetLife`
- `BestThunderTarget`
- `BestAOEThunderTarget`

### AoE

- 2体以上で `Thunder2` / `HighThunderII` 候補
- AoE FuturePlannerにも `Thunder2` 候補を出す
- `DotTargets >= AOEBreakpoint` を見る
- Force指定は寿命判定を無視してよい

---

## Ley Lines

黒魔紋は「置けるから置く」ではなく、踏める時間があるときに置く。

### 置く条件

- Forceならユーザー明示として通す
- FuturePlannerでPPTが上がる
- EvenBurstで安全に踏める
- targetが短命すぎない
- 外部Hintで `LeyLinesUnsafeSoon` ではない

### 禁止/抑制

- `LeyLinesUnsafeSoon` ならForce以外は置かない
- 短命雑魚に自動置きしない
- 移動要求中に無理に置かない
- 黒魔紋中だから三連魔、という固定消費はしない

---

## Triplecast / Swiftcast

三連魔は火力バフではなく、GCD停止回避・移動・復帰・DTR用リソース。

### 使う場面

- 火氷切り替えの詠唱短縮
- DTR / Transpose復帰
- ForcedMoveSoon
- 黒魔紋を踏み続けるために必要
- チャージ溢れ防止

### 使わない場面

- 黒魔紋中に棒立ち可能
- 近い将来に移動がある
- DTR/復帰が近い
- Swift/TripleをPlannerが架空消費しているだけ

---

## AoE FuturePlanner

AoE plannerは以下を候補として扱う。

- `Foul`
- `Thunder2`
- `Paradox`
- `FlareStar`
- `Flare`
- `Freeze`
- `Blizzard2`
- `Blizzard4`
- `Transpose`
- `Amplifier`

### 重要

`ApplyAOEPlannerAction` に処理があるactionは、`AOEPlannerActions` に候補として出すこ## EncounterHint(BLMでは削除済み)

BLM の「Encounter Hint」トラックと分類(Boss / Trash / AllianceTrash / MajorAdd / BossReturn / HoldBurst / ForceBurst / Off)は
2026-10-03 に削除した。Recovery AI(ShadowLogOnly / AssistRecoveryOnly)も同時に削除している。
BLM は常に旧 `Boss`(= `Off`)と同じ判断をする。burst の保留は、目標喪失のヒント(`ExternalMechanicHint` の
`TargetLossIn` など)と、瀕死の敵に対する Ley Lines の保留だけで決まる。
共有の `ExternalEncounterHintProvider` と、MNK / RPR の EncounterHint は残っている。

---


- ForceBurstはユーザー/外部明示として通す

---

## ExternalMechanicHint / Splatoon由来情報

外部情報は安全なhintとしてだけ使う。

### やってよい

- `TargetLossIn`
- `TargetReturnIn`
- `ForcedMoveIn`
- `LeyLinesUnsafeIn`
- `ShouldHoldBurst`
- `ForceBurst`
- `IsTrashPhase`
- `IsMajorAddPhase`

### やってはいけない

- Splatoon ScriptをBossMod内で実行
- 外部URLからコード取得
- reflectionでSplatoon内部型を覗く
- 外部Hintに直接スキルを命令させる
- 未設定値0やNaNを有効hint扱いする

### 必須ガード

```csharp
private static bool ValidHintTime(float value)
    => !float.IsNaN(value) && value >= 0 && value != float.MaxValue;
```

`ForcedMoveIn <= 5` のような判定は、必ず `ValidHintTime()` を通す。

---

## Manual handoff

手動操作から自動へ戻るときは、lockやbridgeを壊さない。

維持すること:

- `HandleManualControlHandoff`
- `HandleRotationModeHandoff`
- `ClearPlannerLock`
- `ResetAOEToSingleBridge`
- `ResetStandard57OpenerState`
- `ResetInstantB3TransposeReentry`
- `LastExecAt`
- `RotationModeHandoffUntil`

注意:

- 手動GCDを検知したらDTR lockを無理に継続しない
- ただしAoE→単体Bridgeは必要以上に即消ししない
- `FlareStar` 使用後はbridgeを消してよい

---

## コンパイル確認

修正後に最低限確認する。

```text
dotnet build
```

できない環境なら、以下を手作業で確認。

- 未定義型がない
- 未定義メソッドがない
- enum値の名前が正しい
- `World.CurrentZone` / `World.CurrentCFCID` の型がProviderと合っている
- `ExternalMechanicHintProvider` / `ExternalEncounterHintProvider` が重複定義されていない
- `MovementOverride` が存在しない場合は安全に無効化する
- `Bossmods.ActiveModule` が存在しない場合はnull-safe fallbackを入れる

---

## 重点テスト

### 単体

- Standard57 openerが崩れない
- Manafont位置が早漏しない
- Paradox skipがStandard57時だけ働く
- FuturePlannerでDTRが暴発しない
- 黒魔紋中にゼノ連打しない
- 黒魔紋中に三連魔を無意味に切らない

### 2体

- 2体からAoEモードに入る
- `Foul` を使う
- `Thunder2` 候補が出る
- 2体短命ならburstを抑える
- 2体大型AddならMajorAddとしてburst許可

### 3体以上

- `Freeze` / `Flare` / `Foul` / `FlareStar` が候補に出る
- `Thunder2` が更新候補に出る
- 短命パックにLey Linesを置かない
- AoE FuturePlannerがParadoxを候補にできる

### AoE → 単体

- `AOEToSingleBridgeActive` が即消えない
- `AstralSoul == 6` なら単体 `FlareStar`
- `AstralSoul >= 3` なら `Flare` bridge
- 氷復帰は `Blizzard3 → Blizzard4`
- UI1から直接 `Blizzard4` しない

### 外部Hint

- `TargetLossIn` でPolyglot吐き/Thunder抑制
- `ForcedMoveIn` でXeno/Thunder/Paradox価値上げ
- `LeyLinesUnsafeIn` でForce以外のLey Lines抑制
- 未設定/NaN/MaxValueで常時ForcedMoveにならない

---

## よくある回帰

- `AOEBreakpoint` を3に戻してしまう
- `ShouldSkipStandard57AFParadox` がstrategy/timelineを見ない
- `OnCooldown(Manafont)` だけでPostManafont扱いする
- `InstantBudget > 0` だけで詠唱魔法をインスタント扱いする
- `AOEPlannerActions` からThunder2/Paradox候補が消える
- `TryAOEToSingleIceBridge` でUI1からBlizzard4
- `LeyLinesUnsafeSoon` をForceにも適用してしまう
- Splatoon/ExternalHintを直接スキル命令として使う
- 短命敵へThunder更新し続ける
- 黒魔紋中にゼノを連打する
- 黒魔紋中に三連魔を固定で切る

---

## 最終判断

修正で迷ったら、以下を優先する。

1. GCDを止めない
2. AF/UIを落とさない
3. Polyglot/Thunderhead/Paradox/AstralSoulを溢れ・捨てすぎない
4. 2体からAoEへ入る
5. AoE↔単体bridgeを維持する
6. Burst資源はBoss/MajorAdd/ForceBurstへ寄せ、短命Trashでは温存する
7. 外部Hintは安全な補助情報としてだけ使う
