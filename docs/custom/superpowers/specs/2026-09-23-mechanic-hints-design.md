# メカニクス予告ヒント 設計

2026-09-23。対象ブランチ nin-prekassatsu3rd-stability。作業 worktree bossmodreborn-fps-optimization-83c83a → 完了後 F:\bossmodreborn へ書き戻し。

## 目的

ターゲット消失・強制移動・タイムラインの予告をスキル回しに反映。対象 xan(BLM, RPR, MNK, NIN, VPR, MCH)+ akechi(GNB, PLD)。プリセット UI から使用有無・情報源を選択可。

背景: 現状 BLM は切替あり既定 Off(ユーザー blm プリセット未指定 → 予告無視)。RPR・MNK は常時読込・切替なし。NIN・VPR・MCH・GNB・PLD は予告未使用。NIN・VPR・GNB は BMR モジュールのダウンタイムすら未使用。

## 用語

- 長い消失: ターゲット不在(不可視化・射程外押し出し)が 8.5 秒以上。`TargetLossIn` / `TargetReturnIn`。
- 短い射程外: 回避による一時的射程外。8.5 秒未満含む。`RangeLossIn` / `RangeReturnIn`。
- 強制移動: 現在地が被弾予定 → 移動開始必須。`ForcedMoveIn` / `ForcedMoveFor`。
- 窓: バースト効果時間、または連続 GCD シーケンス長。

## UI

- 型 `BossMod.Autorotation.MechanicHintStrategy`(新ファイル `BossMod/Autorotation/MechanicHints.cs`)。
  - `All` 全部: タイムライン+移動予測+Splatoon/IPC(既定・値 0)
  - `TimelineOnly` タイムラインのみ: BMR モジュールのステートマシン+取り込みタイムライン
  - `ForecastOnly` 移動予測のみ: 射程外・強制移動
  - `Off` 使わない: 新挙動全停止、従来通り
- 表示名「メカニクス予告ヒント」、InternalName `MechanicHints`。
- xan: 各 Strategy 構造体の**末尾**に `[Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)] public Track<MechanicHintStrategy> MechanicHints;`(58 = 旧 BLM ExternalHints と同値)。先頭 Targeting/AOE/Buffs の順序維持。
- akechi: `Track` 列挙の**末尾**に `MechanicHints`、`Definition()` 末尾で定義(UI 優先度 45)。`AkechiTools` に `DefineMechanicHints(this RotationModuleDefinition, Track)` 補助。
- プリセット保存は選択肢名 → 既存プリセット無影響。未指定プリセットは `All`。

## 情報源の分類

`ExternalMechanicHintProvider` に情報源フィルタ付き取得を追加: `TryGetSnapshot(zone, cfc, now, MechanicHintSources sources, out snapshot)`。`MechanicHintSources` はフラグ(Timeline / Forecast / External)。namespace で判定。

- Timeline: `bossmod.internal.timeline`(ExternalTimelineHints)。
- Forecast: `bossmod.internal.disengage-forecast`(DisengageForecaster の長い消失)。
- External: `bossmod.external.*`(Splatoon、IPC、SplatoonSafeImport、ハーネス)。`All` のみ。
- 既存引数なし版 `TryGetSnapshot` は全源マージのまま残す(他の呼出元互換)。

選択肢 → 情報源:
- All = Timeline | Forecast | External + ステートマシン + `Hints.Disengage` + エンカウントヒント(`ExternalEncounterHintProvider`)
- TimelineOnly = Timeline + ステートマシン
- ForecastOnly = Forecast + `Hints.Disengage`
- Off = なし

## MechanicForecast(毎フレーム)

`readonly record struct MechanicForecast`。値は「今から何秒」、`float.MaxValue` = 予告なし。

- `TargetLossIn`, `TargetReturnIn`: 長い消失。候補を集め最早 `TargetLossIn` を採用、復帰時刻はその源のもの。復帰不明 = `float.MaxValue`。
  - ステートマシン(Timeline): `Manager.Planner` の `EstimateTimeToNextDowntime`(今ダウンタイム中なら loss=0, return=残)、未来分の復帰は `EstimateTargetableWindows(horizon)` で取得。
  - スナップショット(源フィルタ済み): `TargetLossIn`, `TargetReturnIn`。
  - 8.5 秒未満の消失(return − loss < 8.5)は除外。定数は共通化(MNK/RPR/Forecaster の 8.5 と同値)。
- `ForcedMoveIn`, `ForcedMoveFor`: `Hints.Disengage`(Forecast)、スナップショット `ForcedMoveIn`(External)、ステートマシン PositioningStart / Knockback 遷移(Timeline)の最早。
- `RangeLossIn`, `RangeReturnIn`: `Hints.Disengage.TargetLossIn/ReturnIn`(Forecast のみ、短い射程外含む)。
- `LeyLinesUnsafeIn`: ステートマシン PositioningStart(Timeline)、スナップショット(External)。BLM 用。
- `Encounter`: `ExternalEncounterHintSnapshot?`(All のみ)。
- 補助:
  - `DowntimeNow` = `TargetLossIn <= 0`。
  - `ReturnKnown` = `TargetReturnIn < float.MaxValue`。
  - `LossWithin(t)`。
  - `ShouldHoldWindow(windowLength, cooldown, gcd)`: 保留条件 = `ReturnKnown` かつ `TargetLossIn < windowLength − gcd`(窓の 1 GCD 超が削られる)かつ `cooldown > TargetReturnIn`(今使うと次回使用が復帰後に遅れる)。今使っても復帰時に再使用可なら保留しない(部分使用分が得)。
  - `ExpiresDuringLoss(timeLeft)` = `TargetLossIn < timeLeft && timeLeft <= TargetReturnIn`(消失開始時点で効果残存、復帰前に切れる → 消失前に使う)。復帰不明も成立。

組込: 共通ビルダ `MechanicForecast.Build(mode, ws, hints, planner, module)`。xan は `Basexan` に `protected MechanicForecast Mechanic` と `UpdateMechanicForecast(MechanicHintStrategy)`、各モジュール `Exec` 冒頭で呼ぶ。akechi は `AkechiTools` に同名、`Execution` 冒頭で呼ぶ。

## 共通ルール

1. バースト保留: `ShouldHoldWindow` 成立時のみ。復帰不明なら保留しない。
2. 消失前消費: 消失中に効果切れ・チャージ上限停滞するものだけ。ゲージはダウンタイム中に増えない → ゲージ吐き出しなし。
2b. 長い消失前の締め(`ReturnKnown` の長い消失のみ):
   - 候補: リキャスト付き攻撃技(GCD 工具・oGCD・チャージ技・効果付与技)で、今使っても `TargetReturnIn` + 1 GCD までにリキャストが戻るもの(使用回数を損しない)。2 分バースト系(No Mercy, Dokumori, Wildfire 等)は対象外 → ルール 1。
   - 枠: 消失前の残り GCD 枠数 S(最終枠は詠唱・硬直込みで `TargetLossIn − 0.5` 秒までに完了するもの)。oGCD は残り weave 枠。
   - 選択: S 枠内で達成できる威力合計が最大になる組合せ。シーケンス(コンボ・連携・Reawaken 一連・Hypercharge 等)が枠内で完走できない場合は、枠内に入る部分の威力で評価し、単発技と比べて威力の高い方を使う。
   - 並べ: 候補が S 未満なら前半は通常回し、候補は末尾の枠に威力の低い順 → **最高威力を消失直前の最終枠**。シーケンスは連続配置を崩さない。
   - 誤差: 予告より早い消失で最終枠を失うリスクあり。0.5 秒余裕で一部吸収。
   - 適用: NIN, VPR, MCH, GNB, PLD。BLM・RPR・MNK は既存のダウンタイム最適化(プランナー地平線、`EffectiveDowntimeIn` 等)を維持し対象外。
3. 強制移動・射程外: シーケンス開始抑止のみ。
4. 復帰準備: ジョブ別(NIN Suiton、MCH Reassemble)。
5. **近接職(NIN, VPR, RPR, MNK, GNB, PLD)は射程外を遠隔攻撃で埋めない。近接職の遠隔技・突進技は既存 UI 設定に従う**(本機能で追加使用も禁止もしない)。
6. `Off` = 本機能の新挙動全停止。既存の `DowntimeIn` 利用箇所は全選択肢で不変。

## ジョブ別

### NIN
- 保留: Dokumori, Kunai's Bane, Kassatsu, Ten Chi Jin, Meisui, Bunshin, Tenri Jindo。
- 消失前消費: Raiju スタック、Phantom Kamaitachi、Tenri Jindo Ready、Kassatsu 中の忍術、2 チャージの印。
- TCJ: `ForcedMoveIn` または `TargetLossIn` が 3 GCD 以内なら開始しない。
- 締め候補: 忍術(印チャージ)、Raiju、Phantom Kamaitachi、Dream Within a Dream。TCJ が完走不可なら部分威力で比較。
- 復帰準備: `ReturnKnown` のダウンタイム中、カウントダウン同様に Suiton 仕込み(印は先、忍術は復帰時)。`PickMudra` のターゲット null 早期 return を印押下では緩和。

### VPR
- 保留: Reawaken 一連(`ReawakenSequenceDuration` 8.2〜10.2 秒)、Vicewinder コンボ(4 GCD)、Serpent's Ire。
- `RangeLossIn` が Reawaken 一連内なら Reawaken 開始しない。
- 消失前消費: 上限 Vicewinder チャージ、消失中に切れる Ready to Reawaken。
- 締め候補: Vicewinder 系コンボ、Reawaken 一連(完走不可なら部分威力で比較)、Uncoiled Fury(Coil あり)。

### MCH
- 保留: Wildfire(10 秒)、Hypercharge(約 8 秒)、Queen、Barrel Stabilizer。既存 `DowntimeIn < GCD + 6`(MCH.cs 2384, 2430)を予告込みの値にも拡張(既存判定は残す)。
- 消失前消費: 上限の工具チャージ、Excavator / FMF / Hypercharged 効果、Gauss/Ricochet チャージ。Wildfire 中 → Detonator、Queen 中 → Overdrive。
- 締め候補: Drill、Air Anchor、Chain Saw、Excavator、FMF、Gauss/Ricochet。Hypercharge は Heat Blast 5 回が入らなければ部分威力で比較。
- 復帰準備: 復帰約 5 秒前 Reassemble。

### GNB
- プランナー(planner74)へ予告を「ターゲット可能ウィンドウ」として実長で供給。
- 既存不具合修正: 未来ダウンタイム 2 GCD 固定(GNB.cs 879)、ダウンタイム中の No Mercy 残減らず(1627-1628 → 1782-1783)、ダウンタイム枠への weave 配置(1414)。
- 消失前消費: 消失中に切れる Ready to Rip/Tear/Gouge/Blast、Ready to Reign、Sonic Break。
- 締め候補: Gnashing Fang 一連(完走不可なら部分威力)、Double Down、Sonic Break、Bow Shock、Blasting Zone、Burst Strike。プランナー74 の枠計画に締めルールを反映。

### PLD
- 保留: 既存 `CanUseMajorBuffBeforeDowntime` / `ActionLikelyLandsBeforeDowntime` / `CanUseSingleHitBeforeDowntime` の入力を「`DowntimeIn` と予告の早い方」に。
- 消失前消費: 消失中に切れる Divine Might、Atonement 系、Confiteor 系、BoH Ready。この場合のみ dmacHold 無効。
- 締め候補: Circle of Scorn、Expiacion、Goring Blade、Divine Might の Holy、Atonement 系、Confiteor 系(完走不可なら部分威力)。
- 既存不具合修正: 硬直詠唱 Holy Spirit に `castTime` を渡す → `MaxCastTime` で強制移動前に詠唱開始しない。

### BLM
- `ExternalHints` トラック(Off/MechanicOnly/Full)を `MechanicHints` に置換。既存プリセット指定ゼロ確認済み。
- `BuildTimelineContext` の入力を `MechanicForecast` に。`All` = 旧 Full(エンカウントヒント含む)。
- 自前の `PushBossModuleTimelineHints`(Splatoon namespace への書込)廃止 → 共通ビルダのステートマシン由来値に置換。
- `Hints.Disengage` 直読の移動対策(Swiftcast/Triplecast、BLM.cs 7636-7640)は従来から常時有効 → 維持。
- ハーネス固定指定(Program.cs 998-1007 の `ExternalHints`/`MechanicOnly`)を `MechanicHints`/`All` に更新。

### RPR
- `_mechanicHint` / `_encounterHint` の常時読込(RPR.cs 641-642)を `MechanicForecast` 経由に。`All` で現状同一。

### MNK
- `_mechanicHint` 読込(MNK.cs 513-514)を `MechanicForecast` 経由に。`All` で現状同一。CFC 1094 除外維持。

## 非目標

- ゲージ吐き出し。
- 近接職の射程外遠隔攻撃・突進技の自動追加。
- 既存遠隔技トラックの既定変更。

## 検証

1. 不変性
   - 全ジョブ `Off` = 改修前とビット同一(GNB・PLD の既存不具合修正は独立タスクで前後比較、その前段階で確認)。
   - RPR・MNK `All` = 改修前と同一。
   - BLM `All` vs 旧 MechanicOnly: 比較、差分 run は個別説明。
2. 効果: ハーネス `All` vs `Off`。`--target-loss-hints`、`--random-disengage` + `--disengage-forecast on`。面 `timeline-matrix` / `timeline-combat-matrix`(BLM 必須)/ `dmu-full`。実回転は BLM・RPR・MNK・GNB。MCH・VPR は例外なし+判断テスト。
2b. 締め判断テスト: 長い消失予告時、消失前最終 GCD 枠 = 候補中最高威力、候補がリキャスト内に戻らない技を含まない、完走不可シーケンスは部分威力比較どおり。
3. NIN・PLD: ハーネスにアダプタ追加、判断テスト(例: 予告消失 15 秒前以内に FoF なし、強制移動 3 GCD 前以内に TCJ 開始なし)。
4. 最終: ゲーム内(アライアンス等)。

## 実装順

1. 共通基盤+8 モジュールへのトラック追加(挙動不変、BLM 既定のみ変化)。
2. BLM 移行+ハーネス更新。
3. GNB → MCH → VPR。
4. NIN → PLD+判断テスト。

各段階で検証してから次へ。

## 書き戻し

- 開始時: 本体の未コミット BLM.cs 差分(Transpose 調査の行動ロック修正、+31 行)をこの worktree に取込。
- 完了時: 本体の MERGE_HEAD 等確認、衝突確認後に書き戻し。
