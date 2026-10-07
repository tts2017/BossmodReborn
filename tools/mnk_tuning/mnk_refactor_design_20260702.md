# MNK.cs 改修設計書(全4系統・挙動保存アプローチ)

作成日: 2026-07-02
対象: `BossMod/Autorotation/Standard/xan/Melee/MNK.cs`(ブランチ `nin-prekassatsu3rd-stability`、HEAD コミット済み、5,241 行)

## 0. 前提の裏取り(Codex 解析との差分)

実コードを全読して確認した結果、Codex 解析と異なる点が3つある。

1. **MNK.cs はメインツリーで未変更(clean)。**「作業ツリー上で変更済み」は古い情報(変更済みなのは MCH.cs と tools/mch_regression)。既存未コミット差分への配慮は不要で、HEAD からクリーンに始められる。
2. **`tools/mnk_regression` は存在する**(`generate-baseline` / `run` / `emulate` の3コマンド、`--scenario all|dancing_mad|lookaway|roe|...`、Verdict=ADOPTABLE で exit 0)。回帰ゲートとしてそのまま使える。
3. Claude Code セッションの worktree(`claude/jolly-bose-371c41`)の MNK.cs はメイン HEAD の**祖先(古い)**。改修はメインツリー側で行うか、worktree を先に `nin-prekassatsu3rd-stability` へ同期すること。

Codex 解析の指摘自体(二重ゲート、Blitz 判定の重複、1094 直書き等)はすべて実コードで確認できた。行番号つきの裏取りは §3 に記載。

## 1. 要件の解釈と狙い

要件は「全部」= ①PB/バーストタイミング ②Blitz/夢幻闘舞 ③RoF/BH/薬の順序 ④敵消失/フェーズ/防御系 の4系統。個別の不具合報告はないため、本設計は**挙動を1フレームも変えない構造改修**を土台に据える。狙いは「4系統それぞれの今後の挙動変更を、単一箇所の小さな diff で安全に行える状態」を作ること。

## 2. 設計方針(最も安全でスマートなアプローチ)

1. **挙動保存を各フェーズの不変条件にする。** 各フェーズ完了時に mnk_regression のベースライン**完全一致**を要求。差分が1件でも出たらそのフェーズの抽出ミス。
2. **判定の単一情報源化。** 同じ判定を2箇所で計算する構造を「1回計算 → 両消費者に配る」へ変える。条件式そのものは変えない。
3. **副作用の位置を凍結。** `BuildBurstPlan` / `UpdateAutomaticPBBurstTracking` はスケジュールアンカーを書き換える(`ProtectOpenerRoFOffsetSchedule`、`AdvanceStaleDelayedBurstAnchors`、`SkipLateOddRoFAfterPhaseResume`、アンカー前進)。抽出するのは純粋計算のみ。副作用呼び出しの位置・回数は不変。
4. **ログ書式を凍結。** `LogGuide73` / `LogTuning` の出力文字列は fflogs 比較・チューニングの資産。変更しない。
5. **1フェーズ = 1コミット。** 問題発生時に二分探索できる粒度を保つ。

## 3. 改修ステップ

### Phase 0: ベースライン確保

- BossMod 本体と MnkRegression をビルド。
- `generate-baseline --scenario all` と `emulate` 用ベースラインを HEAD で取得(§5 のコマンド)。

### Phase 1: Blitz 判定の単一情報源化(②Blitz/夢幻闘舞系)

**問題(確認済み):** `ShouldUseBlitzBeforeFormlessGCD()`(L4056–4095)と `UseBlitz()`(L5191–5228)が、約25行の判定変数計算(`effectiveBlitzLeft` / `fightEndBurn` / `blitzExpiring` / `canHoldForRoF` / `nextBuffIn` / `burstBuffActive` / `currentPhantomRushReady` / `canHoldCurrentPhantomRushForBuff` / `useCurrentPhantomRushNow` / `holdForQueuedRoF` / `holdForPlannedRoF` / `plannedPBContinuationBlitz` / `pbOvercapUnlockBlitz` / `holdAutomaticPhantomRushForBurst` / `burstMeleeCompression`)と同一の `strategy.Blitz` switch を丸ごと重複して持つ。片方だけ直すと「GCD 優先判定」と「実際の Blitz 使用」が食い違う(Codex 指摘の通り)。

**変更:**
- `private readonly record struct BlitzDecision(bool ConsumeFormlessFirst, bool Use, bool UrgentNow)` と `EvaluateBlitzDecision(in Strategy, AID currentBlitz, in BurstPlan)` を新設し、現在の変数計算を**そのまま移動**する(式を書き換えない)。
- `UrgentNow` = 現行の優先度昇格条件 `useCurrentPhantomRushNow || fightEndBurn || burstBuffActive || burstMeleeCompression || blitzExpiring || plannedPBContinuationBlitz || pbOvercapUnlockBlitz` をそのまま保存。
- `UseBlitz` は decision から Use 判定と優先度(`BlitzNow` / `PR` / `Blitz`)を導出。`CountAutomaticPBBlitzCompletion` と `_forceEvenPhantomRushAfterDeath` クリアは現位置のまま。
- `ShouldUseBlitzBeforeFormlessGCD` は `NumBlitzTargets > 0 && !decision.ConsumeFormlessFirst && decision.Use` に置換(現行と同値)。
- `ShouldConsumeFormlessBeforePendingBlitz` は decision 計算の最初の分岐として温存。

### Phase 2: PB ホールド判定の一本化(①PB/奇数/偶数バースト系)

**問題(確認済み):**
- 3つの遅延ヘルパー `ShouldConsumeFormlessBeforeOddAutomaticPB`(L86)/ `ShouldDelayOddAutomaticPBUntilOpoGCD`(L303)/ `ShouldDelayEvenAutomaticPBForBurstValue`(L248)が共通ゲート(`PB != Automatic` / `PerfectBalanceLeft > GCD` / `BeastGaugeBlocksPB` / `ShouldBlockPBForPendingReply`)を各自コピーしている。
- `holdAutomaticPBForPredictedGCD` は `Exec` で計算され OGCD の QueuePB 呼び出しゲート(L702–705)に使われる一方、`QueuePB` 内部でも `ShouldDelayOddAutomaticPBUntilOpoGCD` **だけ**再判定している(L5104)。even-delay と formless-consume は Exec 側ゲートのみ。「フラグは立つが押さない/想定外に押す」型バグの温床。
- `OddDelayedPBGCDIsLeapingOpo`(L147–156)と `EvenDelayedPBGCDIsLeapingOpo`(L201–210)は**本体が完全同一**。

**変更:**
- 共通ゲートを `AutomaticPBHoldGateBlocked(in Strategy)` として抽出し、3ヘルパーから呼ぶ。
- 同一の2関数を `DelayedPBGCDIsLeapingOpo(int index)` に統合。
- `Exec` で3判定を1回だけ計算し `PBHoldDecision`(bool×3)にまとめ、`OGCD` → `QueuePB` へ引数で渡す。`QueuePB` 内の再判定(L5104)は渡された値の参照に置換。判定値は同一、計算回数だけ減る。
- `BasicAndChakraOvercapOnly` 経路の `QueuePB`(PB.Force 専用、L645–647)には default(no-hold)を渡す — 現行も Automatic 分岐に到達しないため同値。

**維持:** `ShouldAllowAutomaticPBNow` のロジック、`QueuePB` 冒頭の安全ゲート、優先度計算。

### Phase 3: バースト窓・targetable 計算の共通化(③RoF/BH/薬系)

**問題(確認済み):** `BuildBurstPlan()`(L3638–)と `UpdateAutomaticPBBurstTracking()`(L4333–)が resourceBurn / targetable / lastRoFWindow / roFImmediateWindow / evenPreRoFPBWindow / scheduledNextRoFIn 系の計算を重複して持つ(L3648–3721 と L4360–4428)。ここが食い違うと `_activatedRoFBursts` の進行(奇偶判定)と BurstPlan の窓判定がずれ、PB 回数・薬・RoF/BH 同期が同時に壊れる。

**重要な非対称(意図的差分として保存):** BuildBurstPlan 側の `normalBurstTargetable` は `automaticBurstStartAllowed`(`CanStartAutomaticBurstAtMelee`)を含むが、Tracking 側は含まない。この差分は**合成を各呼び出し側に残す**ことで保存する。

**変更:**
- 純粋計算を部品単位で抽出(例: `ResourceBurnState(resourceBurnIsPhaseEnd, resourceBurnRemaining)` / `phaseEndBurstTargetable` / `endBurnTargetable` / scheduled next RoF/BH 計算)。合成(`targetable` の組み立て)は各呼び出し側に残し、非対称箇所に「意図的差分」コメントを1行付す。
- 副作用(`AdvanceStaleDelayedBurstAnchors` / `SkipLateOddRoFAfterPhaseResume` / アンカー前進 / `_activatedRoFBursts` 増分)は一切移動しない。

### Phase 4: コンテンツ判定の一元化(④敵消失/フェーズ/防御系)

**問題(確認済み):** 絶妖星乱舞の contentId `1094` が3箇所に直書き — `IgnoreDancingMadP1MechanicActionHold`(L83)、`TryGetDancingMadP1P2FallbackBurnHorizon`(L2052)、`UseMechanicAIHints`(L2151)。一方 `ClassifyContent`(L1791)は既に 1094→DancingMad の分類を持つ。

**変更:**
- `private MNKContentClass CurrentContentClass => ClassifyContent(CurrentTuningContentId);`
- `private bool IsDancingMadContent => CurrentContentClass == MNKContentClass.DancingMad;`
- 上記3箇所を置換(`UseMechanicAIHints() => !IsDancingMadContent;` 等)。挙動同一。
- RoE / EarthsReply はロジック変更なし。不変条件を明文化する: RoE は `RoEStrategy.Automatic` かつ `ConfirmedSelfDamageIn() <= 10s` のみ、EarthsReply は期限直前のみ・優先度 `TrueNorth - 1`(全バースト OGCD より下)。`--scenario roe` で担保。

### Phase 5(任意・別コミット): 既知のデッドコードの明示化

挙動保存だが差分の見た目が大きくなるため、不要なら見送り可:
- `BurstActionWeightInsideWindow` / `BurstActionInsideWindow` / `SynergyBurstGCDWeights`: **呼び出し元なし**(grep 確認済み)。
- `OddPreRoFPBWindow(...) => false;`(L4210)固定 → `oddPreRoFPBWindow` は常に false で依存分岐はデッド。将来の奇数 PB 前倒し実験の足場に見えるため、削除ではなく「常に false(無効化中)」の明示コメントが安全。
- `holdBrotherhoodForRoF = false` 固定(L3722)、`MechanicHoldReason.UnsafePosition`(スコア常時0)も同様。
- FormShift 3系統(通常 / downtime / PB 前保護)の統合は、FormShift 自体の仕様変更をする時まで**見送り**。触る動機のない統合はリスクだけ増える。

## 4. 変更禁止の安全ゲート(レビュー用チェックリスト)

- `QueuePB` 冒頭: `!PBUnlocked` / `BeastGaugeBlocksPB` / `ShouldBlockPBForPendingReply` / `PBStrategy.Delay` / `PerfectBalanceLeft > 0` / `HasPendingMasterfulBlitz`
- Strategy の Force/Delay が Automatic 系補正に上書きされない構造(必ず strategy 値の switch を通す)
- `OGCDPriority` / `GCDPriority` の数値と `ActionQueue.Priority` オフセット(モンクは weave 猶予が薄く優先度が実挙動に直結)
- チューニング定数の**値**(値変更は本設計のスコープ外。ログ比較を経て別タスクで行う)
- `LogGuide73` / `LogTuning` の書式
- `_activatedRoFBursts`・スケジュールアンカー前進の副作用順序

## 5. 検証手順(各フェーズ共通)

> **重要な注意(2026-07-02 Phase 1 レビューで判明):** 現行の `tools/mnk_regression` は **MNK.cs を実行していない**。`MnkRegression.csproj` は BossMod への ProjectReference を持たず、`MnkActionSimulator` はツール内の独立した再実装で、MNK.cs はレポート用の SHA256 ハッシュとして記録されるだけである。したがって emulate/run の ADOPTABLE は「MNK.cs リファクタの挙動保存」の証明にならない。挙動保存フェーズの実質的なゲートは (a) diff の逐行同値レビュー + ビルド成功、(b) 実際の MNK モジュールを駆動する検証手段(BossMod を参照するハーネス等)の整備、のいずれかとする。以下のコマンドは「シミュレータ側ルールの健全性確認」としては引き続き有効。

```powershell
# ビルド
dotnet build tools/mnk_regression/MnkRegression.csproj

# Phase 0 で一度だけ(HEAD 時点)
dotnet run --project tools/mnk_regression -- generate-baseline --scenario all --out tools/mnk_regression/results/baseline_head.json

# 各フェーズ後
dotnet run --project tools/mnk_regression -- run --scenario all --baseline tools/mnk_regression/results/baseline_head.json --out tools/mnk_regression/results/phaseN.json --report tools/mnk_regression/results/phaseN_report.txt
dotnet run --project tools/mnk_regression -- emulate --scenario all --baseline ... --out ... --report ...

# 重点シナリオ(Codex 指摘の回帰ゲートと一致)
dotnet run --project tools/mnk_regression -- emulate --scenario dancing_mad ...
dotnet run --project tools/mnk_regression -- emulate --scenario lookaway ...
dotnet run --project tools/mnk_regression -- emulate --scenario roe ...
```

**受け入れ基準:** Verdict = ADOPTABLE(exit 0)かつ**差分ゼロ**。挙動保存フェーズで差分が出た場合は採用せず、抽出内容を見直す。

## 6. リスクと緩和

| リスク | 緩和 |
| --- | --- |
| BuildBurstPlan 系の副作用位置ずれ(アンカー二重前進・前進漏れ) | 純粋計算のみ抽出、副作用は現位置固定。フェーズ毎の完全一致ゲート |
| 「重複に見えて微妙に違う」条件の均し込み(Plan vs Tracking の targetable) | 部品抽出+合成は呼び出し側に残す。diff レビューで条件項の消失がないか確認 |
| 毎フレーム経路への割り当て増加 | `readonly record struct` + `in` 渡しのみ。クラス化・LINQ 追加をしない |
| worktree が古い版を編集してしまう | メインツリー(`nin-prekassatsu3rd-stability`)で作業するか、先に worktree を同期 |

## 7. 完了後にできるようになること

この構造改修後、4系統それぞれの実際の挙動変更(例: 奇数 PB 前倒しの再有効化、RoW 保持窓の調整、DancingMad 以外のギミック AIHints 無効化コンテンツ追加、防御系と攻撃バーストの優先度調整)は、単一情報源への小さな diff + 回帰ベースラインとの「意図された差分」レビューという形で安全に実施できる。
