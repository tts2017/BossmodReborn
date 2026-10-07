# NIN バースト選択と 7.5 精査 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** xan NIN の先雷遁/先活殺を状態からの rDPS 比較で自動選択し、開幕位置を設定化し、7.5 威力と AoE 分岐点を反映する。効果はハーネスで計測して採否を決める。

**Architecture:** 純ロジックの `NINBurstPlanner.cs`(威力表・入力スナップショット・選択器 3 種)を NIN.cs から呼び、確定行動で案を固定して既存の実行機構で回す。検証は `tools/xan_timeline_harness` に NIN の模擬器とスコアラーを追加して行う(MNK/RPR/BLM/GNB と同じ方式)。

**Tech Stack:** C# (.NET 10)、BossModReborn、Lumina(クライアント sqpack)、xan_timeline_harness。

**Spec:** `docs/superpowers/specs/2026-09-24-nin-burst-selection-design.md`

## Global Constraints

- 作業場所は隔離コピー `F:\bmr_nin_iso`(以下 `ISO`)。本体 `F:\bossmodreborn` の bin/obj に触れない。本体への Write/Edit はフックで禁止されているので、書き戻しは最終タスクでコピーにより行う。
- ビルド(PowerShell、CWD = ISO): `dotnet build tools\xan_timeline_harness\XanTimelineHarness.csproj -c Release`
- 実行(CWD = ISO): `dotnet tools\xan_timeline_harness\bin\Release\net10.0-windows10.0.26100.0\XanTimelineHarness.dll <command> ...`(以下 `$H`)
- sqpack は `C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack`(自動検出、ffxivgame.ver 2026.09.15.0000.0000)。タイムラインは `F:\event-trigger-master\timelines\src\main\resources`(自動検出)。
- ファイル差し替え後にビルドが走ったかを DLL の mtime で確認する(File.Copy は mtime を保持するため、MNK で初回測定が無効になった前例あり)。
- 新トラックは Strategy 構造体の末尾に追加し、既存トラックの順序を変えない。InternalName は ASCII、表示名は日本語。
- 既定値: 開幕 毒盛 2、開幕 百雷銃 3、先雷遁/先活殺 = 自動(計画器)。
- 威力は仕様書「7.5 データ」の値(クライアント 2026.09.15)。記憶で直さず `potency-audit` で照合する。
- 目的値は rDPS(自己威力 × 自己由来倍率)。PT シナジーは同点処理にだけ使う。
- 揺れ防止マージン 20 威力。開幕制約の解除は戦闘 30 秒。窓検出の余裕 2 秒(`ReadyIn > CD − 2`)。
- 他ジョブのハーネス出力を変えない(RPR/BLM/MNK/GNB の既定実行はバイト一致を保つ。summary.csv は列追加のみ)。
- 本体の `tools/xan_timeline_harness/Program.cs` を丸ごと上書きしない。自分の差分だけを当てる。
- コミットはユーザー指示時のみ。

## Review Focus

1. 毒盛の直後に標的が消える(先雷遁で雷遁まで撃った後の消失): 固定が解除され、百雷銃が永久保留にならない。→ Task 8 のハーネス検査 `nin-lock-release`(ロス窓付きシナリオで失敗 0、復帰後 3.5 秒以内に GCD)。
2. 途中参加・開幕直後のダウンタイム: 開幕位置の制約で毒盛が詰まらない(戦闘 30 秒で解除)。→ Task 8 selftest `opener-cutoff`。
3. レベル同期(Lv30/45/54/66/70/76/80/90): 選択器が未解放技を含む案を返さず、うさぎ 0。→ Task 11 のレベル sweep。
4. 2 体(劫火に切り替わる境界): 活殺中の印が劫火の組(地→天 / 人→天)になり、うさぎ 0。→ Task 5 の `--extra-targets 1` 検査。
5. ping が大きい(アニメロック遅延 0.12 秒): 計画器の weave 枠判定が GCD を遅らせる案を選ばない。→ Task 7 selftest `high-ping-feasibility`。

---

### Task 0: 準備

**Files:**
- Modify: `ISO/tools/xan_timeline_harness/MnkPotencyDump.cs`(調査用の一時変更を元に戻す)

- [ ] **Step 1:** 一時変更を戻す。`Copy-Item F:\bossmodreborn\tools\xan_timeline_harness\MnkPotencyDump.cs ISO\tools\xan_timeline_harness\MnkPotencyDump.cs -Force` の後に `(Get-Item ...).LastWriteTime = Get-Date`。
- [ ] **Step 2:** ISO と本体の差分が無いことを確認: `git -C F:/bossmodreborn diff --stat HEAD -- BossMod` の結果と、ISO 側の同ファイル群を `diff -rq` で比較(BLM.cs と AutoClear.cs の本体未コミット差分は ISO にも入っていること)。
- [ ] **Step 3:** ビルドが通ることを確認。

### Task 1: 印チャージをクールダウン群から数える

**Files:**
- Modify: `ISO/BossMod/Autorotation/Standard/xan/Melee/NIN.cs`(`MudraCharges` プロパティ)

**Interfaces:**
- Produces: `private int MudraCharges`(戻り値の意味は変えない)、`private float MudraNextChargeIn`(次チャージまで秒、上限なら 0)

- [ ] **Step 1:** 現行の `ActionManager.Instance()->GetCurrentCharges` 経路を、Ten1 の主クールダウン群から計算する実装に置き換える。

```csharp
private int MudraCharges
{
    get
    {
        if (!Unlocked(AID.Ten1))
            return 0;
        var def = ActionDefinitions.Instance.Spell(AID.Ten1)!;
        var max = def.MaxChargesAtLevel(Player.Level);
        var cd = World.Client.Cooldowns[def.ActualMainCooldownGroup(World.Client.DutyActions)];
        if (cd.Total <= 0 || def.Cooldown <= 0)
            return max;
        return Math.Clamp((int)MathF.Floor(cd.Elapsed / def.Cooldown + 0.001f), 0, max);
    }
}

private float MudraNextChargeIn
{
    get
    {
        if (!Unlocked(AID.Ten1) || MudraChargesCapped)
            return 0;
        var def = ActionDefinitions.Instance.Spell(AID.Ten1)!;
        var cd = World.Client.Cooldowns[def.ActualMainCooldownGroup(World.Client.DutyActions)];
        return cd.Total <= 0 ? 0 : def.Cooldown - cd.Elapsed % def.Cooldown;
    }
}
```

- [ ] **Step 2:** `using static FFXIVClientStructs.FFXIV.Client.Game.ActionManager;` が不要になったら削除(他の参照が無いことを grep で確認)。
- [ ] **Step 3:** ビルド。ゲーム内と同値である根拠(ClientState の cooldown はゲームの RecastDetail の写し)をコメント 1 行で残す。

### Task 2: 7.5 威力表 `NinPotency` と実データ照合

**Files:**
- Create: `ISO/BossMod/Autorotation/Standard/xan/Melee/NINBurstPlanner.cs`(この段階では `NinPotency` のみ)
- Modify: `ISO/tools/xan_timeline_harness/PotencyAudit.cs`

**Interfaces:**
- Produces: `public static class NinPotency { public static float Of(BossMod.NIN.AID aid, int level, NinPotencyVariant variant = NinPotencyVariant.Base); }`、`public enum NinPotencyVariant { Base, Combo, Positional, ComboPositional, Meisui }`、`public const float KassatsuBonus = 1.30f, KunaiBonus = 1.10f, DokumoriBonus = 1.05f, KazematoiBonus = 100, BunshinMelee = 160, BunshinArea = 80;`

- [ ] **Step 1: 失敗するテスト。** PotencyAudit に NIN 表の照合を追加する。クライアント説明マクロを「職 30、レベル L」で評価する小さな評価器 `EvaluateMacro(string macro, int job, int level)`(`<if([gnum68==30],A,B)>` と `<if([gnum72>=N],A,B)>` の入れ子を解決し、`\,` を除去)を書き、ラベル(例 "potency of"、"Combo Potency:"、"Rear Combo Potency:"、"when under the effect of Meisui"、"Meisui Bonus: Potency is increased to")の直後の数値を読む。照合対象: 双刃旋/風断ち(基本・コンボ)/旋風刃(4 種)/強甲(4 種)/手裏剣/雷遁/火遁/水遁/風遁/氷晶/劫火/百雷銃/毒盛/夢幻/六道(基本・命水)/是正(基本・命水)/口寄せ/蝦蟇仙/天理/PK/月影雷獣牙/四門雷獣牙/投刃/血花/八卦(基本・コンボ)を Lv100・90・80 で。
- [ ] **Step 2:** `$H potency-audit` を実行し、`NinPotency` 未実装で失敗(コンパイルエラー)になることを確認。
- [ ] **Step 3:** `NinPotency.Of` を実装。値は仕様書「7.5 データ」と、各技のマクロの Lv94 未満分岐(POTENCY_DUMP の出力 `scratchpad/nin_potency_dump.txt` に全文あり)。
- [ ] **Step 4:** `$H potency-audit` で `failures=0`(既存 39 件 + NIN 表)。

### Task 3: ハーネスの NIN 模擬器・スコアラー・配線

**Files:**
- Create: `ISO/tools/xan_timeline_harness/NinCombatState.cs`
- Create: `ISO/tools/xan_timeline_harness/NinPotencyScorer.cs`
- Modify: `ISO/tools/xan_timeline_harness/Program.cs`(Jobs 登録、BuildWorld、Advance/Execute 分岐、metrics、trace、`--potions` の DEX 対応、`nin-emulator-selftest`)

**Interfaces:**
- Consumes: `NinPotency`(Task 2)
- Produces: `internal sealed class NinCombatState(WorldState world, Actor player, float frameStep, Action<ActionID, ulong, bool, float, bool> onExecuted)`(最後の bool は PT シナジー中)、プロパティ `NinkiOvercap`, `RaijuLost`, `Rabbits`, `MudraCapFrames`, `KunaiPotency`, `KunaiGCDs`, `PartyDokumoriValue`, `OpenerDokumoriGCD`, `OpenerKunaiGCD`, `RaitonFirstBursts`, `KassatsuFirstBursts`, メソッド `Advance()`, `ExecuteBestAction(AIHints)`, `Resolve(float endTime)`; `internal static class NinPotencyScorer { float Estimate(...) }`; `internal static class NinTerminalValue { float Estimate(WorldState, Actor, NinCombatState) }`

- [ ] **Step 1: 失敗するテスト。** `nin-emulator-selftest` コマンドを追加し、模擬器に行動列を直接流して結果を検査する 12 件を書く:
  1. 天→地→雷遁: 雷遁が有効、雷獣 1、印チャージ −1、GCD は 0.5/0.5/1.5。
  2. 地→天→雷遁: うさぎ 1、効果なし。
  3. 活殺→地→人→氷晶: 印チャージ不変、活殺消費、威力 ×1.3。
  4. 活殺中に天地人: 実行不可。
  5. 雷遁の後に双刃旋: 雷獣消滅、`RaijuLost` 1。雷遁の後に PK: 雷獣維持。
  6. 分身 → 双刃旋 ×5: 追撃 5 回(各 160)、忍気 +5×5 +5×5、PK Ready 付与。PK では追撃しない。
  7. 天地人 → 手裏剣(天)→雷遁(地)→水遁(人): 時間 1.0/1.0/1.5、隠形 20 秒、Lv100 で天理 Ready。
  8. 水遁 → 百雷銃: 隠形消費、標的デバフは 1.29 秒後に付与、15 秒。
  9. 毒盛: 1.07 秒後に標的デバフ 20 秒、秘技 30 秒、忍気 +40。是正で秘技消費。
  10. 命水: 隠形消費、忍気 +50、命水バフ。六道で命水消費し 550。
  11. 忍気 90 で毒盛: 溢れ 30 を計上。
  12. 百雷銃窓の着弾判定: 百雷銃押下 0.65 秒後の月影雷獣牙(着弾 +0.76)は ×1.10、双刃旋(着弾 +0.40)は ×1.00。窓の終端で PK(1.57)は外れ、雷獣(0.76)は入る境界ケース。
- [ ] **Step 2:** `$H nin-emulator-selftest` が未実装で失敗することを確認。
- [ ] **Step 3:** `NinCombatState` を実装(MnkCombatState と同じ構造: `Advance` で期限切れ除去・ヘイスト同期・保留デバフの付与・ゲージ公開、`ExecuteBestAction` で `CanExecute` 除外 → FindBest → GCD/CD 開始 → `Complete`)。
  - ゲージ: `NinjaGauge` を `Unsafe.AsPointer` で ulong 化して `GaugePayload` に書く(tools/nin_real_harness の `BuildNinjaGauge` と同じ)。
  - GCD 長: WS は `ActionSpeed.GCDRounded(SkS, Haste, level)`、印 0.5、忍術 1.5、天地人 1.0/1.0/1.5(Action シートの値)。
  - ヘイスト: Lv45 以上で 85 を `OpPlayerStatsChange` で同期。
  - `CanExecute`: 百雷銃/騙し討ちは隠形か隠れる、氷晶/劫火は活殺、是正/蝦蟇仙は秘技、PK は PK Ready、雷獣は Ready、天理は Ready、命水は隠形かつ戦闘中、天地人は活殺中不可、忍気消費技は忍気 50、印は(活殺中を除き)チャージ。
  - 押下ごとに `OpCastEvent`(SourceSequence 連番)を発行。
- [ ] **Step 4:** `NinPotencyScorer` を実装。押下時に基礎威力 × 活殺 × 薬 を決め、標的デバフは「押下時点で既に予定済みの窓」に着弾時刻が入るかで判定(アニメロックがあるので押下後に予定される窓が先に着弾することはない)。毒盛の PT 寄与は `Advance` で毒盛の窓内フレームごとに 1450 × 0.05 × 0.05 秒、PT シナジー中は ×1.5。
- [ ] **Step 5:** Program.cs 配線: `new("nin", Class.NIN, XanNIN.Definition, ...)`、`_ninCombat`、`--potions` を職に応じて DEX/STR に、`RotationMetrics` 末尾に NIN 指標(optional)を追加、`Format`/`Csv`/summary ヘッダーに列を追加、`RdpsTotal` に `NinPartyDokumori` を加算、トレースに窓内 GCD 列 `kunai_window,<開始>,<GCD 列>` を出力。
- [ ] **Step 6:** `$H nin-emulator-selftest` が 12/12 成功。`$H event-timeline --job nin` が例外なく走り、`rabbits=0`。
- [ ] **Step 7:** 他ジョブ不変の確認: `$H timeline-matrix --job rpr --scenario-limit 25` と `--job mnk` の summary(exec_ms 列除く)が変更前ビルドと一致。

### Task 4: ベースライン採取(現行 NIN.cs + Task 1 のみ)

- [ ] **Step 1:** `ISO\tools\xan_timeline_harness\bin` を `ISO\baseline_bin` へ複製。
- [ ] **Step 2:** 以下を実行して結果を `ISO\results\baseline_*.txt` と trace ディレクトリへ保存:
  - `timeline-combat-matrix --job nin`(Lv100、既定 SkS = GCD 2.12)
  - 同 `--party-buffs 7.8`
  - 同 `--target-loss-hints 30`
  - `timeline-matrix --job nin`、`dmu-full --job nin`、`event-timeline --job nin`
  - `--level 70/80/90` の combat-matrix(`--scenario-limit 400`)
  - `--extra-targets 1/2/3/4` の timeline-matrix(`--scenario-limit 100`)
- [ ] **Step 3:** 開幕の実行列(先頭 30 秒)を trace から抜き出して記録(現行の毒盛位置・百雷銃位置・先雷遁/先活殺の実態)。

### Task 5: 7.5 威力定数と AoE 分岐点

**Files:**
- Modify: `ISO/BossMod/Autorotation/Standard/xan/Melee/NIN.cs`

**Interfaces:**
- Consumes: `NinPotency`

- [ ] **Step 1:** NIN.cs の威力定数を `NinPotency.Of(..., Player.Level)` 参照に置き換える(`RaitonPotency` 等の const を削除)。
- [ ] **Step 2:** 分岐点: `useKassatsuNinjutsuAOE = NumRangedAOETargets >= 2`(劫火 850n > 氷晶 1300)。`ShouldUseNinkiAOE` を「命水中は 3 体以上、それ以外は 2 体以上」に。コメントを実威力に更新。
- [ ] **Step 3:** 計測: `--extra-targets 1..4` と Lv100 単体 combat-matrix。単体はトレース完全一致、2 体以上で rdps 向上・うさぎ 0 を確認(Review Focus 4)。

### Task 6: 百雷銃・毒盛の着弾前窓の検出

**Files:**
- Modify: `ISO/BossMod/Autorotation/Standard/xan/Melee/NIN.cs`

**Interfaces:**
- Produces: `private bool KunaiWindowStarted`(`TargetTrickLeft > 0 || ReadyIn(TrickAction) > KunaiCooldown - 2`)、`private bool DokumoriWindowStarted`(同様に 120)、`private float KunaiWindowLeft`(着弾前は 15 + 付与遅延 − 経過)、`private float DokumoriWindowLeft`

- [ ] **Step 1:** `TargetTrickLeft > 0/GCD` と `TargetMugLeft > 0/GCD` を「窓が始まったか/残り」の意味で使っている箇所を洗い出し、上記ヘルパーに置き換える(標的上の残り秒が必要な終端合わせ計算は、着弾前なら「効果時間 + 付与遅延 − 経過」で代替)。
- [ ] **Step 2:** 計測: combat-matrix と party-buffs。押下直後の誤判定(忍術保留・PK 保留)が消え、rdps が下がらないことを確認。

### Task 7: 選択器(計画器・固定)と selftest

**Files:**
- Modify: `ISO/BossMod/Autorotation/Standard/xan/Melee/NINBurstPlanner.cs`
- Modify: `ISO/tools/xan_timeline_harness/Program.cs`(`nin-planner-selftest`)

**Interfaces:**
- Produces:

```csharp
public enum NinBurstVariant { None, RaitonFirst, KassatsuFirst }
public enum NinBurstMode { Planner, Rules, RaitonFirst, KassatsuFirst }
public record struct NinBurstContext(...); // 仕様書「入力」の全項目。時間は秒、float.MaxValue = 予定なし
public readonly record struct NinBurstEvaluation(NinBurstVariant Choice, float RaitonFirstValue, float KassatsuFirstValue, bool RaitonFirstFeasible, bool KassatsuFirstFeasible, string Reason);
public interface INinBurstSelector { NinBurstEvaluation Select(in NinBurstContext ctx, NinBurstVariant tentative); }
public sealed class NinBurstPlanner : INinBurstSelector { public float Simulate(in NinBurstContext ctx, NinBurstVariant variant, List<NinPlannedStep>? trace = null); }
public sealed class NinBurstRules : INinBurstSelector { public NinBurstTuning Tuning; }
public sealed class NinFixedSelector(NinBurstVariant variant) : INinBurstSelector;
public static class NinBurstFeasibility { public static bool RaitonFirst(in NinBurstContext ctx); public static bool KassatsuFirst(in NinBurstContext ctx); }
public readonly record struct NinPlannedStep(float Time, BossMod.NIN.AID Action, float Potency, float Multiplier, bool InKunai);
```

- [ ] **Step 1: 失敗するテスト。** `nin-planner-selftest` に 10 件:
  1. `even-pk-available`: 偶数分直前、PK あり、印 2、活殺 CD 0 → 両案成立、値が有限、選択は値の大きい方。
  2. `even-no-charge`: 印 0・次チャージ 10 秒 → RF 不成立 → KF。
  3. `kassatsu-late`: 活殺 CD 25 秒 → KF 不成立 → RF。
  4. `odd-window`: 毒盛なし → 奇数分形で評価。
  5. `target-loss-mid`: 百雷銃の 6 秒後に 20 秒消失 → 消失後の行動が 0 として計上される(両案の値が消失なしより小さい)。
  6. `hysteresis`: 差が 20 未満なら仮決定を維持。
  7. `opener-cutoff`: 戦闘 31 秒・経過 GCD 0 → 開幕制約が無効(Review Focus 2)。
  8. `high-ping-feasibility`: ping 0.12 で、GCD 間に weave 枠がない位置に百雷銃を置く案を選ばない(Review Focus 5)。
  9. `level-90`: Lv90(天理なし・雷遁 650)でも値が有限で、未解放技を行動列に含まない。
  10. `trace-shape`: RF の偶数分の行動列が「雷遁 → PK → 百雷銃 → 雷獣 → 氷晶 → 天地人 …」、KF が「活殺 … 百雷銃 → 氷晶 …」になっている。
- [ ] **Step 2:** 失敗を確認。
- [ ] **Step 3:** 計画器を実装(仕様書「計画器モデル」。GCD 列の生成 → weave 枠への能力配置 → 着弾判定付きの採点 → 終端価値)。
- [ ] **Step 4:** 10/10 成功。

### Task 8: NIN.cs への統合(トラック・開幕位置・固定・実行側)

**Files:**
- Modify: `ISO/BossMod/Autorotation/Standard/xan/Melee/NIN.cs`

**Interfaces:**
- Consumes: Task 6 のヘルパー、Task 7 の選択器
- Produces: トラック `OpenerDokumoriGCD`(値 `OpenerGCD.One..Four`)、`OpenerKunaiGCD`(`Two..Six`)、`BurstVariant`(`NinBurstMode`)。`private int _openerGCDCount`、`private NinBurstVariant _lockedVariant`、`private NinBurstVariant _tentativeVariant`

- [ ] **Step 1:** トラック 3 本を Strategy 末尾に追加(表示名: 「開幕 毒盛位置」「開幕 百雷銃位置」「先雷遁/先活殺」)。
- [ ] **Step 2:** GCD 計数: `Manager.LastCast` の SourceSequence 変化を毎フレーム検出し、戦闘開始以降の WS・忍術・天地人忍術を数える。戦闘終了でリセット。
- [ ] **Step 3:** 開幕制約: 初回の毒盛は `_openerGCDCount >= OpenerDokumoriGCD`、初回の百雷銃は `>= OpenerKunaiGCD`(毒盛 + 1 に補正)。戦闘 30 秒で解除。
- [ ] **Step 4:** `SelectBurstNinjutsuPlan` を選択器呼び出しに置き換え、確定行動で `_lockedVariant` を固定、解除条件を実装。Ultimate 0 秒スタイルは選択器を通さず先活殺固定のまま。
- [ ] **Step 5:** 実行側: KF の活殺を「百雷銃前、氷晶が 15 秒に収まる範囲で遅め」、RF の活殺を「百雷銃後の最初の GCD の weave」、天地人を「氷晶の直後(印 2 なら雷遁を 1 回先)」、PK を窓の最後の GCD にしない、薬を毒盛の 1〜2 GCD 前に。
- [ ] **Step 6:** ハーネス検査を追加: 開幕の毒盛・百雷銃の GCD 位置が設定以上(`opener_dok_gcd`, `opener_kunai_gcd`)、`nin-lock-release`(毒盛直後にロス窓を置いた generic シナリオで失敗 0)。
- [ ] **Step 7:** 計測: `--track BurstVariant=RaitonFirst` / `KassatsuFirst` / `Planner` の 3 通りを combat-matrix(party-buffs 7.8)で。計画器 ≥ max(固定 2 種) − ノイズ、かつ ≥ ベースライン。

### Task 9: 事前規則(B)と調整

**Files:**
- Modify: `ISO/BossMod/Autorotation/Standard/xan/Melee/NINBurstPlanner.cs`(`NinBurstRules`, `NinBurstTuning`)
- Modify: `ISO/tools/xan_timeline_harness/Program.cs`(`nin-burst-sweep`)

- [ ] **Step 1:** `nin-burst-sweep`: 各シナリオを RF 固定・KF 固定で走らせ、百雷銃窓ごとに特徴量(偶奇、PK 有無、分身中、GCD 長、忍気、印チャージ、活殺の戻りまで、シナジーまで、消失予告まで)と rDPS 差を CSV 出力。
- [ ] **Step 2:** CSV を集計(scratchpad の py スクリプト)し、条件式と閾値を決めて `NinBurstTuning` に置く。ヒント条件(窓内に消失予告がある時の選択)も同じデータから決める。
- [ ] **Step 3:** 計測: `BurstVariant=Rules` を MechanicHints Off / TimelineOnly / All で。ベースライン以上であること。

### Task 10: 精査項目の A/B

- [ ] **Step 1:** 水遁リード時間(現行 10〜12 秒 vs 出典の 20 秒切り)を A/B。
- [ ] **Step 2:** 百雷銃の weave 位置(終端合わせ vs 最遅)を A/B。
- [ ] **Step 3:** 近接 AoE 分岐点(3/4/5 体)と天地人 AoE 経路を `--extra-targets` で A/B。
- [ ] **Step 4:** 改善したものだけ採用。

### Task 11: 退行確認・レビュー・書き戻し

- [ ] **Step 1:** レベル sweep(30/45/54/66/70/76/80/90/100)、`--target-loss-hints 30`、`--random-disengage 1`、`--extra-targets 1〜4`、dmu-full、event-timeline で失敗 0・うさぎ 0(Review Focus 3)。
- [ ] **Step 2:** 他ジョブのハーネス出力が不変であることを再確認。
- [ ] **Step 3:** 全差分をレビュー(コードレビュー用サブエージェント)。指摘を反映。
- [ ] **Step 4:** 書き戻し: 本体の MERGE_HEAD 等を確認 → 変更ファイルを本体へコピー(LF 維持、Program.cs は自分の差分のみ当てる)→ 本体側で `git diff --stat` を確認。
- [ ] **Step 5:** メモリに NIN ハーネスと結果を記録。

## 実施記録(2026-09-24)

Task 1〜11 を実施。判定モデルは着弾時刻から押下時刻(実効窓 16.29 / 21.07 秒)に変更し、A/B の採否・見つけた欠陥・結果は仕様書末尾「実装時の変更点と結果」に記録した。`nin-burst-sweep` は作らず、固定案の全走査と判断ログ(`NIN_DECISIONS_CSV` / `NIN_DECISIONS_TRACE`)で代替した。
