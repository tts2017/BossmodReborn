# Codex指示書: RPR `drift_full_mode_gluttony_interval` 314件の全数分類

作業前に必ずリポジトリ直下の `agents_rpr.md` を読むこと。本指示書は同ファイルの規約(採用条件・禁止事項・コマンド・既知のemulatorギャップ・解決済み事項)を前提とする。

## 対象

- `F:\bossmodreborn`
- `tools/rpr_regression`(分類器の追加のみ)
- `BossMod/Autorotation/Standard/xan/Melee/RPR.cs` は**今回のタスクでは変更しない**

## 目的

20k real harness の HardFailバケット `drift_full_mode_gluttony_interval`(定常値314件)から、RPR.cs本体修正が必要な true candidate だけを抽出する。前回までに `gauge_soul_slice_charge_overcap`(2471件)と `dd_dropped_during_enshroud`(377件)は同じ方式で全数分類済みで、いずれもactionable候補ゼロだった(`agents_rpr.md` §5参照)。

## 前提知識

- DriftFailureルール(`HardFailRules.cs` の `CheckDrift`、**変更禁止**):
  - Full modeフレームの連続Gluttony使用ペアについて `drift = 間隔 - 60`
  - even窓(`time % 120 <= 25 || >= 95`)では `drift > 20.5` でfail、それ以外では `drift > 3.5` でfail
  - Basic modeフレームを跨ぐと前回Gluttony記録はリセット
- バケット割当(`Program.cs` の `ClassifyRealHardFail`): DriftFailureがあり、Basic modeフレームが1つもないシナリオが `drift_full_mode_gluttony_interval`、Basicフレームがあるものは別バケット `drift_mode_switch_window`
- emulatorはGluttonyのリキャストを選択時に厳密チェックしない(意図的。厳密化は不採用済み — 再試行禁止)。Gluttonyが「使われない」要因は選択条件側: `red >= 50 / Reaver None / BlueSouls 0 / !waitingForArcane`(waitingForArcane = AC ready 10秒以内)

## 手順

1. **RPR.csをいきなり変更しない。** `Program.cs` に分類器を追加する(分析出力のみ。emulator・HardFailルール・シナリオ生成は非変更)。
2. 各対象シナリオについて、failしたGluttonyペア(最初の違反ペア)を特定し、**ペア間の区間に何が起きていたか**をフレームから機械判定して分類する。
3. 分類カテゴリ(目安。実データに合わせて調整可、ただし除外の根拠は必ずフレーム事実に基づくこと):
   - `target_loss_or_downtime_in_gap` — ペア間にTargetLost等のイベントが重なり、Gluttonyが物理的に撃てない区間があった
   - `kill_edge` — 違反ペアの後半がkill time際(`KillTime - t <= gcd*2.2` 相当)
   - `waiting_for_arcane_hold` — 遅延の主因がwaitingForArcane(AC合わせの意図的ホールド)。even窓判定とのズレ(AC自体のドリフトで`time%120`窓から外れたケース)はここに含めて件数を分けて報告
   - `red_gauge_starvation` — Gluttonyが撃てる時刻に `red < 50` が続いた(直前のEnshroud/Reaver占有・SoulSliceチャージ状況も記録)
   - `enshroud_or_reaver_occupancy` — BlueSouls>0 / Reaver状態が期日を跨いで続いた
   - `seeded_initial_state` — 初回ペアが開幕シード状態由来(1本目のGluttonyが opener 窓内で、シードされたred/チャージが間隔を歪めた)
   - `true_rpr_logic_candidate` — 上記いずれにも該当しない残り
4. 出力先: `tools/rpr_regression/results_real_hardfail_drift_classification`
   - `drift_gluttony_classification.md` — カテゴリ件数(レベル帯別内訳つき)、各カテゴリ代表15件(ペア時刻・間隔・drift量・even窓か・区間中のred/souls/イベント要約)
   - `drift_gluttony_buckets.json` — 全314件の個別詳細(シナリオ名・レベル・gcd・killTime・違反ペア・区間要約・カテゴリ)
5. true candidate が残ったら、`timelines/` のCSVで実トレースを確認し、さらに**RPR.cs側の実挙動**(`WaitingForArcaneCircle`・Gluttony使用条件・`gluttonyDriftUrgent` 周り)をコードで確認して、emulator簡略化由来のartifactか本物のロジック欠陥かを判定する。判定までが本タスク。**RPR.cs修正は本タスクでは行わず、修正案の提案止まりとする。**

## 確認(分類のみの回でも必須)

- `dotnet build .\BossModReborn.sln -c Debug -v minimal` → 0 warnings / 0 errors
- standard regression(`agents_rpr.md` §2のコマンド、out名 `results_drift_classification_standard_check`)→ 344/344, HardFail 0, ADOPTABLE
- real harness 20k(out名 `results_real_hardfail_drift_classification`)→ fail 3317 / rule別内訳が定常値と完全一致 / Regressions none(= 挙動変化なし、分類出力のみ追加)
- 終了時に `tools/rpr_regression/bin` / `obj` を削除

## 禁止

- RPR.cs変更、HardFailルール変更、failを消すための期待値緩和
- UI/ログ/新設定追加
- `baseline_adopted_rpr_weave_local_fixes` の上書き
- GluttonyReadyIn厳密チェックの再導入(不採用済み)

## 最終報告に含めること

- 314件のカテゴリ別件数(レベル帯別内訳つき)
- true RPR logic candidate 件数と、その代表timeline(該当区間の抜粋)
- true candidateがemulator artifactか本物か、根拠つきの判定
- RPR.cs修正が必要かどうかの結論(必要なら最小修正案の提案。実装は次タスク)
- build / standard regression / real harness の結果
