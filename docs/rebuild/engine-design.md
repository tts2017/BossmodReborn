# 汎用回しエンジン 設計書 (Step 2)

作成日: 2026-10-07 / ブランチ: `rotation-rebuild`

## 0. 目的と前提

- 基本コンボとバースト判断を **1 つの仕組み** で扱う。ジョブごとの手書き優先度リストをやめ、ジョブは「スキル定義」と「少数の評価重み (JSON)」だけを用意する。
- 速さと強さを両立する: 1 回の判断はフレーム予算内 (既定 0.5 ms) で終わり、状態が変わらない限り再計算しない。
- 採用: 2 層計画 (上位 = バースト窓単位の動的計画法 / 下位 = 次の 3〜5 GCD スロットの分枝限定探索)、重みの自動調整は CMA-ES。
- 不採用: MCTS、ニューラルネット (再現性・説明性・速度の理由で brief により却下)。
- 置き場所: エンジン本体は `Custom/Engine/Core/` (BossMod/Dalamud に依存しない純 C#)、BMR との接続は `Custom/Engine/Adapter/`。Core は tools 側のテスト・ベンチマーク・チューナーからそのままコンパイルして使う。

### 0.1 既存実装から引き継ぐべき能力 (§9 のチェックリスト参照)

既存の RPR (xan/Melee/RPR.cs)、NINBurstPlanner、BLM FuturePlanner が個別に手書きしている判断を、エンジンの一般機構で表現できることを要件とする。詳細は §9。

## 1. 全体構成

```
RotationModule (BMR)
  └─ EngineRotationModule<TJob> (Adapter)           … 毎フレーム呼ばれる
       ├─ TJob.ReadState(ref EngineState, ctx)      … ゲーム状態 → 固定長 struct (ジョブ側の薄いマッパー)
       ├─ TimelineReader → EngineTimeline           … FightRemaining / ダウンタイム / レイドバフ窓 / 移動予定
       └─ RotationEngine.Decide(state, timeline)    … 状態ハッシュが同じなら前回結果を再利用
            ├─ UpperPlanner (DP)                   … フェーズ変化・ダウンタイム・タイムライン変化時のみ再計算
            │    → 資源ごとの shadow price λ と、各バースト開始時の目標保有量
            └─ LowerSearch (分枝限定)               … 次の 3〜5 GCD スロット + 各スロットの oGCD ウィーブ
                 → 次に押す GCD / oGCD
  └─ Hints.ActionsToExecute に GCD・oGCD を Push
```

## 2. 状態 (EngineState)

完全に固定長の `unmanaged struct`。探索中はスタック上でコピーするだけで、ヒープ確保は一切しない。

| フィールド | 型 | 内容 |
|---|---|---|
| `Time` | float | 判断起点からの経過秒 (起点 = 0) |
| `GcdReadyIn` | float | 次の GCD を押せるまでの秒 |
| `AnimLockIn` | float | アニメーションロック (詠唱含む) 解除までの秒 |
| `ComboSkill` / `ComboLeft` | byte / float | 直前のコンボ技 (スキル index, 0xFF = なし) と残り時間 |
| `Gauges[8]` | fixed short | ジョブゲージ (ジョブ定義の順) |
| `StatusLeft[16]` / `StatusStacks[16]` | fixed float / byte | 自己バフ・対象デバフの残り秒とスタック |
| `CdReadyIn[16]` / `Charges[16]` | fixed float / byte | クールダウングループごとの次チャージまでの秒と現在チャージ数 |
| `Targets` | byte | 範囲攻撃が当たる対象数 |
| `Flags` | byte | 移動中など |

- スロット数の上限 (ゲージ 8 / ステータス 16 / CD 16 / スキル 64) は `EngineLimits` に定数で置く。現行ジョブで最大の RPR・NIN・BLM でも収まる見積もり。
- **ハッシュ**: 時間系は 0.05 秒単位に量子化して FNV-1a 64bit。置換表と「前回結果の再利用」の両方のキーになる。

## 3. タイムライン入力 (EngineTimeline)

これも固定長 struct (窓は最大 8 本)。

- `FightEndIn`: 戦闘終了までの秒 (不明なら +∞)。BMR の `hints.FightRemaining` (FightTimeEstimator) から。
- `Downtime[]` (start, end): 対象不在。BMR の PlanExecution / ExternalTimelineHints / DisengageForecast から。
- `BuffWindows[]` (start, end, multiplier): レイドバフ窓。BMR の `RaidCooldowns.DamageBuffWindows` と、既知の 2 分周期からの予測。
- `NoCastWindows[]` (start, end): 移動必須などで詠唱できない区間 (DisengageForecast.ForcedMoveIn)。
- `TimelineVersion`: 上位層の再計算トリガー。窓の集合が変わったときだけ増やす。

## 4. ジョブ定義 API

ジョブが書くのは **データだけ**。実行ロジック (状態遷移・ダメージ計算) はエンジン共通。

```csharp
var job = new JobBuilder("TOY", baseGcd: 2.5f)
    .Gauge("Heat", max: 100)
    .Status("Fury", maxDuration: 20, damageMultiplier: 1.2f)          // 自己バフ
    .Cooldown("BurstCD", recast: 120, maxCharges: 1)
    .Cooldown("StrikeCD", recast: 30, maxCharges: 2)
    .Gcd("Slash", potency: 200).GainGauge("Heat", 5).StartsCombo()
    .Gcd("Cut", potency: 150).ComboFrom("Slash", potency: 300).GainGauge("Heat", 5)
    .Gcd("Finish", potency: 150).ComboFrom("Cut", potency: 400).GainGauge("Heat", 10).EndsCombo()
    .Gcd("Blast", potency: 600).SpendGauge("Heat", 50)               // コンボを壊さない (ComboNeutral)
    .Ogcd("Burst", potency: 0, cooldown: "BurstCD").ApplyStatus("Fury", 20)
    .Ogcd("Strike", potency: 300, cooldown: "StrikeCD")
    .Build();
```

スキルが持てる要素:

- 種別 (GCD / oGCD)、威力、範囲威力と最低対象数、詠唱時間、アニメーションロック、GCD 以外のリキャスト (例: 1.5 秒 GCD)
- クールダウングループ (リキャスト・最大チャージはグループ側)
- **条件** (全て満たすと使用可): ゲージ ≥ / ≤、ステータス有/無、スタック ≥、直前コンボ = X、対象数 ≥、詠唱可 (NoCast 窓外)
- **効果**: ゲージ増減 (上限で切り捨て = 溢れ)、ステータス付与 (時間・スタック・上限)、スタック消費、ステータス解除、他 CD の短縮、コンボ設定/継続/中立/破棄
- **条件付き威力**: (条件, 威力) の列。最初に満たしたものを採用 (コンボ威力・強化版など)
- 威力倍率: 有効なステータスの `damageMultiplier` の積 × レイドバフ窓倍率

ジョブ定義は C# の Builder で書く (型安全・ID の参照ミスを Build 時に検出)。**評価重みは JSON** (`weights/<JOB>.json`) で、チューナーが書き換える対象はこちらだけ。

ジョブごとにもう 1 つだけ必要なのが、ゲーム状態 → EngineState の **マッパー** (`IJobStateReader`)。ゲージ構造体やステータス ID はジョブ固有なので、ここだけは数十行のコードになる (brief では「スキル定義と重みのみ」とあるが、ゲームからの読み取りは定義データだけでは書けないため、最小限のマッパーを許容する設計にした)。

## 5. 上位層: バースト窓 DP (UpperPlanner)

### 5.1 考え方 — 資源の shadow price

ゲージ・CD チャージ・バフの再使用などを「資源」とみなし、**今 1 単位使う価値** と **次の窓まで持ち越す価値** を比べられるようにする。上位層の出力は資源ごとの限界価値 λ (shadow price, 威力換算) で、下位層は探索の葉で `Σ λ_r × 保有量_r` を加点する。

- バースト直前: 窓内で使うと倍率が乗るので λ が高い → 下位層は使わずに抱える
- 窓内: λ は「次の窓まで持つ価値」に下がる → 下位層は使う
- 上限間際: 持ち越すと溢れるので λ ≈ 0 → 使う
- 戦闘終了・長いダウンタイム直前で失効する資源: λ = 0 → 吐く

### 5.2 区間分割

今から `min(FightEndIn, 360 s)` までを、BuffWindows / Downtime / FightEnd の境界で区間 `seg_0..seg_n` に切る。各区間は (長さ, 平均倍率, 使用可否, GCD スループット)。

### 5.3 資源ごとの 1 次元 DP

資源 r (ゲージ or CD チャージ) ごとに独立に解く (資源間の相互作用は下位層の探索で拾う、という近似)。

- 単位: そのゲージを使う最小コスト (例: 50 Heat)、CD はチャージ 1。保有量を 1/4 単位で離散化。
- 生成速度 `gain_r`: CD は `1 / recast`。ゲージは **ジョブ定義から自動推定** (起動時に「フィラーだけで 60 秒回す」シミュレーションを 1 回走らせ、平均獲得量/秒を測る)。
- 1 単位の価値 `v_r`: その資源を使う最良スキルの威力 − (GCD 消費なら代わりに失うフィラー平均威力)。ステータス付与系 (例: 自己バフ CD) は `(倍率-1) × 平均威力/秒 × 持続` で換算。
- 遷移: 区間 i の頭で保有 k、区間内で s 単位使う (上限 = 区間の使用可能スループット)。
  `k' = min(k + gain×len − s, cap)`、溢れ = `max(0, k + gain×len − s − cap)`、
  `V_i(k) = max_s [ s × v_r × mult_i + V_{i+1}(k') ]`
- 末端: 戦闘終了なら残量の価値 0、360 秒で打ち切りなら `v_r × 1.0`。
- 出力: `λ_r = (V_0(k+δ) − V_0(k−δ)) / 2δ` (現在保有量 k での傾き) と、argmax 経路から得る **各バースト開始時の目標保有量** (デバッグ表示と下位層の弱い引力に使う)。
- 計算量: 区間 ≤ 16、離散保有量 ≤ 64、使用量候補 ≤ 64 で資源あたり約 6.5 万回の加算。資源 ≤ 24 でも 1 ms 程度で、**再計算はタイムライン変化・フェーズ変化・ダウンタイム出入り・資源の大きな食い違い時のみ**。

## 6. 下位層: 分枝限定探索 (LowerSearch)

### 6.1 スロット

時間は **フレームではなく GCD スロット単位** で進める。1 スロット = GCD 1 回 + その後ろのウィーブ枠 0〜2 個。ウィーブ枠の数は `(GCD 間隔 − 詠唱 − GCD のロック) / oGCD のロック` から決まる (アニメーションロック 0.6 秒 + 回線遅延の推定値を尊重)。

### 6.2 探索

- 深さ = GCD スロット数 (既定 4、最大 5)。反復深化 (深さ 1, 2, …) で、**時間予算を超えたら直前に完了した深さの最善手を返す**。
- **共通の時間地平線**: GCD の長さがモードで変わる (Enshroud 1.5 秒など) ため、候補同士は GCD 数ではなく同じ時刻 `H = 深さ × 基本 GCD` で比べる。葉の時刻 t が H に届かなければ `(H − t) × フィラー威力/秒` を補い、超えた分は同じ率で差し引く (BLM FuturePlanner と同じ扱い)。
- **ヒステリシス**: 新しい最善手が前回の最善手より `switchMargin` (重み) 以上良くなければ前回の手を維持し、地平線の打ち切り位置による手のパタつきを防ぐ。
- 分岐: そのスロットで使える GCD 全部 × ウィーブ枠の oGCD 組 (順序違いは同一視、「何もしない」を含む)。
- 手の並べ替え: 前回の最善手 → 即時価値の高い順。
- 上界: `累積価値 + 残りスロット × (最大 GCD 価値 + 最大 oGCD 価値) × 最大倍率 + 葉評価の上界`。最善値を超えない枝を刈る。
- **置換表**: (状態ハッシュ, 残り深さ) → (値, 最善手)。2^16 エントリの struct 配列 (起動時に確保、以後確保なし)。
- **再利用**: 入力 (状態ハッシュ, タイムライン版) が前回と同じなら探索せず前回の結果を返す。

### 6.3 評価 (スコア)

価値はすべて **威力換算** で足し合わせる。

- 実現値: 探索中に実行したスキルの `威力 × ステータス倍率 × レイドバフ倍率 × 対象数係数`
- 溢れ: ゲージ溢れ・最大チャージで止まっていた CD の時間を、`v_r × 溢れ量 × w_overcap` で減点
- 葉評価:
  - `Σ λ_r × 保有量_r` (上位層の shadow price)
  - 残っている倍率ステータスの残り価値: `(倍率−1) × 平均威力/秒 × 残り秒 × 今後の平均レイド倍率`
  - 維持系デバフ (ジョブが `upkeep` 指定): 残り秒の価値と、切れそうなときの減点
  - コンボ途中の価値: 次の段の威力差 × `w_combo`
  - 目標保有量との差: `w_target × |保有 − 次バースト開始時の目標|` (弱い引力)
- **重み** (JSON): `overcap`, `combo`, `target`, `upkeep.<status>`, `lambdaScale`, `burstBias`, `holdTolerance` など 10 個前後。ジョブの性格はほぼスキル定義から出てくるので、重みは補正だけに絞る。

## 7. 時間予算と性能目標

- 既定予算 0.5 ms / 判断。Stopwatch は 256 ノードごとに確認。
- 予算切れでも必ず「完了した最深の深さの最善手」を返す (深さ 1 は必ず完了させる)。
- 目標: トイジョブで平均 < 50 µs、p99 < 500 µs、判断あたりのヒープ確保 0 バイト。マイクロベンチマーク (`tools/engine_bench`) で計測する。
- 再利用率: GCD 待ちの間は状態ハッシュがほぼ変わらないので、実プレイでは大半のフレームが探索なしで終わる見込み。

## 8. BMR への接続 (Adapter)

`EngineRotationModule<TJob>` (`Custom/Engine/Adapter/`) が RotationModule を継承し、次を行う。

1. `TJob.ReadState` で EngineState を作る (ゲージ・ステータス・CD・コンボ・対象数)。
2. `EngineTimelineReader` が `hints.FightRemaining`、PlanExecution の targetable 窓、`hints.Disengage`、`RaidCooldowns.DamageBuffWindows` から EngineTimeline を作る。
3. `RotationEngine.Decide` の結果を `Hints.ActionsToExecute.Push` に渡す。GCD は GCD 用優先度、ウィーブ oGCD は「次のウィーブ枠」の優先度で、対象は既存の Basexan と同じ選択ロジック (AOE 対象選択は既存関数を流用)。
4. Strategy (UI のトラック) は「エンジンに任せる / 強制 / 禁止」の 3 値を基本とし、強制・禁止は探索の条件に反映する (禁止 = 使用不可、強制 = 使用可になった時点で最優先)。

Step 2 ではモジュールを UI に登録しない (トイジョブはテストのみ)。

## 9. 既存実装から必要な表現力 (チェックリスト)

参考実装の調査結果 (§9.1) から、エンジンが表現できなければならない要素:

### 9.1 調査結果の要約

- **RPR (xan/Melee/RPR.cs)**: 一般探索ではなく手書きの優先度ルール。部分的な先読みとして「次 GCD 後のゲージ予測」「AC−4 秒までに Shroud 50/100 を貯める予算計算 (BuildEvenBurstShroudPlan)」「最大 6 GCD 先までの小さな前進シミュレーション (ForecastNormalCooldownWindows: 3 つの打ち切り点で評価し最小値を採用)」「DD 更新 vs フィラーの比較」を持つ。残り資源は閉形式の限界価値で評価。AC はパーティのバフ到来 −0.7 秒に合わせて最大 8 秒保持。短い対象不在 (≤ 8.5 秒) は無視。戦闘終了ルール (AC+9 秒より前に終わるなら抱えない等) あり。
- **NINBurstPlanner**: 2 つのバースト順序 (雷遁先 / 活殺先) をそれぞれ決定的に前進シミュレーションし、威力 × 倍率 + 地平線での残り資源価値で比較。差が 20 未満なら現行案を維持 (ヒステリシス)。
- **BLM FuturePlanner**: マクロ断片を手とするビームサーチ (幅 16・深さ 40・地平線 45 秒、AoE は 12/32/35 秒)。同一資源・同一時刻の状態を併合、0.05 秒単位で 2.5 秒間キャッシュ。全候補を **同じ地平線** で比較し、未シミュレーション区間はフィラー威力/秒で補い、超過分は差し引く。ダウンタイムをまたぐ詠唱・属性切れは不正手。残り資源はダウンタイム直前に割り引き (<4 秒 ×0.15、<10 秒 ×0.45)。
- **要件 (agents_rpr.md / codex_task_rpr_drift.md)**: 優先順位は GCD 稼働 → DD 維持 → 2 分バーストを「AC 自身の CD ではなくパーティのバフ窓」に合わせる → Enshroud 等の回数を落とさない → ゲージ溢れ回避 → チャージ溢れ回避。Gluttony の間隔ドリフトは偶数窓内 20.5 秒・窓外 3.5 秒超で失敗。ゲームが拒否する手を押さない (枠を約 1.2 秒塞ぐ)。「資源を貯めるだけ」の変更は利得と見なさない。

### 9.2 チェックリストとエンジンでの表現

| 必要な表現 | エンジンでの扱い |
|---|---|
| 上限付きゲージ・溢れ・条件付き獲得 (特性解放後のみ等) | ゲージ + 効果に条件を付けられる (`GainGauge(..).If(cond)`) |
| スタックを特定 GCD が消費 (Soul Reaver, Bunshin, Raiju …) | ステータスのスタック + 消費効果 + スタック条件 |
| ステータスによる使用禁止 (Reaver 中の Enshroud 等) | `ForbidStatus` 条件 |
| モード/構え (Enshroud 1.5 秒 GCD、BLM 属性、天地人の強制 3 段) | ステータスごとの `GcdRecastOverride` / `CastTimeMultiplier`、`LockSkills` (そのステータス中は許可リスト以外不可)、`MustNotExpire` (切れたら大きな減点 = 実質不正) |
| コンボ (段・タイマー・意図的な中断) | コンボ技 + 残り時間。中断は「価値で負ければ選ばれない」だけで禁止はしない |
| チャージ技と再チャージ途中の価値 | CD グループのチャージ + 葉で端数チャージにも λ を掛ける |
| 押した瞬間に乗る倍率バフ窓 | ステータス倍率 (自己) と BuffWindows (レイド) の積 |
| デバフ維持と早すぎる更新の無駄 | `Upkeep` 指定 + 上限超過分の秒数を溢れとして減点 |
| 「X の後 N 秒以内」「N GCD 前」などの連動 | X が N 秒のステータスを付与し、それを条件にする |
| 上位層のハード/ソフト目標 (AC = バフ −0.7 秒、Shroud ≥ X を期限までに、ドリフト上限) | DP の区間境界と目標保有量。ドリフト上限は「CD が最大チャージで止まっている時間」の溢れ減点で表す |
| ウィーブ枠 (0〜2、ロック + 遅延、遅延ウィーブ) | スロットモデル (§6.1) |
| 詠唱時間・ヘイスト・インスタント化 (迅速魔/三連魔)・移動必須窓 | 条件付き詠唱時間倍率、インスタント化ステータス、NoCast 窓では詠唱技を不可 |
| 対象不在 (一時的な不在の閾値)、ダウンタイムをまたぐ詠唱は不正、直前の資源割引 | Downtime 窓 (≤ 8.5 秒は Adapter で除外可)、またぐ詠唱は不可、割引は上位層 DP が自然に出す |
| 戦闘終了 (吐き切り、元が取れない DoT を撃たない、保持の解除) | `FightEndIn` 以降は価値 0。上位層の末端価値 0 で λ も 0 になる |
| 地平線での残り資源価値・共通地平線・未シミュレーション区間のフィラー補完 | 葉評価 (§6.3) と **時間地平線** (§6.2) |
| 打ち切り位置への頑健性・計画のパタつき防止 | 前回の最善手が `switchMargin` 以内なら維持 (ヒステリシス) |
| 合法性チェック・同一状態の併合・結果キャッシュ | 条件判定は全手で必須、置換表、状態ハッシュ再利用 |
| rDPS (レイドバフのパーティ価値) | スキルに `PartyValue` (威力換算) を持たせ、自分の威力とは別に加算 |

### 9.3 参考になる数値

- BLM: 地平線 45 秒 / キャッシュ量子 0.05 秒 / 再利用 2.5 秒 / ダウンタイム前の割引 ×0.15・×0.45。
- NIN: 切り替えマージン 20 (威力)、Kunai 16.29 秒 ×1.10、Dokumori 21.07 秒 ×1.05。
- RPR: Enshroud GCD 1.5 秒、一時的不在 8.5 秒、地平線 360 秒、AC 保持最大 8 秒・先行 0.7 秒、Gluttony ドリフト 3 秒 (開幕 20 秒)、Enshroud 一連の価値 4860。
- ハーネス: パーティバフ初回 7.8 秒・以後 120 秒ごと、対象不在予告 30 秒前。

## 10. CMA-ES チューナー (tools/engine_tuner)

- 調整対象: 重み JSON の数値 (n ≈ 10)。各重みに範囲 [lo, hi] を持たせ、内部では [0,1] に正規化した空間で CMA-ES を回す。
- アルゴリズム: 標準 CMA-ES (μ/μ_w, λ)、rank-1 + rank-μ 更新、ステップサイズ適応。n ≤ 30 なので共分散の固有分解は Jacobi 法で十分。外部ライブラリなし。
- 評価: シナリオ集合に対する平均 DPS (威力/秒) − ハード失敗の罰則。並列評価。乱数シード固定で再現可能。
- シナリオ: `IScenarioSource` で抽象化。
  - 汎用 JSON シナリオ (戦闘時間・GCD・ダウンタイム・対象数・レイドバフ時刻・初期資源)。既存ハーネスの `ScenarioDefinition` (rpr_regression の KillTime / Gcd / Events など) と同じ粒度にしてあり、Step 3 で変換器を書く。
  - 評価器 `IScenarioEvaluator`: Step 2 ではエンジン自身のシミュレータで評価 (トイジョブ用)。**自分のモデルで自分を評価する循環** を避けるため、Step 3 以降は既存ハーネスのエミュレータ (RprRotationEmulator 等) を評価器として差し込む。
- 出力: 最良の重み JSON と、世代ごとの (最良, 平均, σ) のログ。

## 11. brief に無く、こちらで決めたこと

1. **ゲーム状態のマッパーはジョブ側に残す** (§4)。定義データだけでは読み取りが書けないため。
2. **上位層は資源ごとに独立した 1 次元 DP** (shadow price) にした。資源の組を同時に扱う DP は状態数が爆発するので、相互作用は下位層の探索に任せる。
3. **葉評価の主役を λ (限界価値) にし、「目標保有量」は弱い引力に留めた**。目標値だけに引っ張ると、目標の誤差がそのまま損になるため。
4. **ステータス価値は「実現分 + 残り期待値」で一貫して数える**。付与の瞬間に全額計上すると、探索中の倍率と二重計上になるため。
5. **ジョブ定義は C# Builder、重みだけ JSON**。定義の参照ミスを Build 時に検出でき、チューナーが触るのは重みだけになる。
6. **Core は BossMod 非依存**。tools 側のテスト・ベンチマーク・チューナーが Dalamud なしで同じコードを使える。
7. 時間量子化は 0.05 秒 (ハッシュ)、置換表 2^16、既定予算 0.5 ms、既定深さ 4。ベンチ結果で見直す。

## 12. 実装結果 (Step 2)

### 12.1 置き場所

- `Custom/Engine/Core/`: `EngineState.cs` (状態・タイムライン)、`JobDefinition.cs` (定義と JobBuilder)、`Simulator.cs` (共通メカニクス)、`JobAnalysis.cs` (フィラー速度・資源単価の自動推定)、`UpperPlanner.cs` (DP)、`RotationEngine.cs` (下位探索とファサード)、`EngineWeights.cs` (重み JSON)。
- `Custom/Engine/Adapter/EngineRotationModule.cs`: RotationModule 用の抽象基底クラス (UI 登録なし)。
- `tools/engine_common/`: トイジョブとシナリオ実行器 (Core をそのままリンク)。`tools/engine_tests/` (xUnit)、`tools/engine_bench/`、`tools/engine_tuner/` (CMA-ES)。

### 12.2 テスト (トイジョブ、10 件すべて合格)

コンボ継続 / バースト前のゲージ保持 / バースト内での消費 (窓を通してプレイして確認) / 溢れ回避 / 戦闘終了前の吐き切り / 自己バフのレイド窓合わせ (窓 8 秒前は保持、窓内で使用) / チャージ技は上限なら使い・バースト前は 1 つ保持 / 状態不変なら結果再利用 / 300 秒シナリオでフィラーのみより 29% 多い与ダメ (46,350 vs 36,000)。

### 12.3 性能 (トイジョブ、既定設定: 深さ 4・予算 0.5 ms、Release)

| 計測 | 平均 | p50 | p99 |
|---|---:|---:|---:|
| 毎回再計画 + 再利用なし (最悪ケース、2 万回) | 188 µs | 126 µs | 587 µs |
| タイムライン安定 (2 秒ごとに再計画) | 194 µs | — | 587 µs |
| 深さ 3 (同条件) | 64 µs | 45 µs | 260 µs |
| 300 秒シナリオ (実際の判断間隔、159 回探索) | 54 µs | — | 506 µs |
| 上位層 DP の再計画 1 回 | 34 µs | | |

- ヒープ確保: **0 バイト / 判断** (2 万回で計測)。
- 深さ 4 は 91% の判断で予算内に完了し、残りは深さ 3 の結果を返す。
- 設計時の目標 (平均 < 50 µs) は深さ 4 では未達。実プレイでは 0.25 秒量子化した状態ハッシュで結果を再利用するので、探索は判断点が変わったときだけ走る (1 秒あたり数回)。
- 主なコストは評価ノード数 (1 ノード約 0.2 µs、プリミティブは各 5〜15 ns)。Step 3 で RPR を載せた時点で再計測し、必要なら既定深さを 3 に下げるか、葉評価をさらに削る。

### 12.4 実装中に決めたこと (設計書から変えた点)

1. **λ を葉の時刻で計算**: 当初は区間頭の傾きを使う設計だったが、バースト区間内で「今使う」と「区間内で後で使う」が同点になり保持し続ける問題が出たため、「その区間の残り時間で最適に使い、残りを次区間へ持ち越す」価値の傾きを葉の時刻ごとに計算し、同一区間内の持ち越しには 0.97 の割引を掛けた。計算結果は (資源, 0.25 秒, 保有量 33 段階) でキャッシュ。
2. **時間付きバフ資源の倍率**: 自己バフ CD (例: Fury 20 秒) の価値は、押した瞬間の倍率ではなく「効果時間とレイド窓の重なりの平均倍率」で評価する (`JobAnalysis.CdValueDuration`)。
3. **ウィーブ順の正規化**: 同じウィーブ窓の oGCD はスキル番号順にだけ探索 (A→B と B→A は同じ状態)。置換表キーにこの制約を混ぜる。
4. **深い層の oGCD 候補制限**: 3 層目以降は即時価値上位 2 件の oGCD のみ (トイでは発動しないが、oGCD の多いジョブ向け)。
5. **枝刈りされた子の扱い**: 子が全部刈られた場合は閾値を返し、根では採用もヒステリシス対象化もしない。
6. **結果再利用キー**: 状態ハッシュを 0.25 秒量子化 (`RotationEngine.ReuseQuantum`)。置換表は 0.05 秒。
7. **チューナーの決定性**: 時間予算で結果が揺れないよう、チューニング中は深さ 3・予算 1 秒の固定設定で評価する。
8. **Adapter の時計**: BMR の DateTime を float 秒にするとき、最初の呼び出し時刻を基準にする (絶対値だと float の精度が足りない)。

### 12.5 CMA-ES チューナー

`dotnet run --project tools/engine_tuner -c Release -- --generations 4 --seed 1` でトイジョブ・5 シナリオに対して動作確認: ベースライン 149.87 → 154.35 DPS (+3.0%)。評価はエンジン自身のシミュレータ (循環評価) で、実ハーネスへの接続は Step 3。

## 13. RPR をエンジンに載せた結果 (Step 3)

### 13.1 作ったもの

- `Custom/Engine/Jobs/RprDefinition.cs`: レベル 100 リーパーのジョブ定義 (データのみ、BossMod 非依存)。スキル名は RPR ハーネスの行動名と一致させてある。調整済みの重みを既定値として内蔵。
- `Custom/Engine/Jobs/RprEngineModule.cs`: プラグインモジュール **「RPR [Engine]」** (upstream / [Custom] の RPR と並んで選べる)。ゲーム状態リーダー、True North と推奨位置 (Gibbet=側面、Gallows=背面)。レベル 100 未満では何もしない。ポーションは未対応。
- `tools/rpr_regression`: 方針差し替え口 (`PolicyOverride`) と、窓ごとの方針時間・確保量の記録を追加 (ハーネス本体の判定・採点は無変更)。
- `tools/rpr_engine_eval`: ハーネスをエンジンで駆動するアダプタ (`RprEnginePolicy`) と、`compare` / `tune` / `explain` / `diagnose` コマンド。
- `tools/xan_timeline_harness`: 実モジュールを BMR の模擬ワールドで動かすハーネスに `rpr-engine` ジョブを追加 (旧 [Custom] RPR と同じ模擬・同じ採点で比較できる)。

### 13.2 比較 (調整後の重み)

**A. rpr_regression ハーネス (方針レベル、カタログ + 実戦風 300 パターン)**。エンジンが担当できる「レベル 100・通常ローテ・Full モード」の 369 シナリオ:

| 指標 | 旧 (ハーネス内の RPR.cs 移植) | エンジン 深さ 4 | エンジン 深さ 3 |
|---|---:|---:|---:|
| 平均 威力/秒 (DPS 指標) | 377.3 | **386.4** (+2.4%) | 388.8 (+3.0%) |
| 平均ハーネススコア | 144,131 | **144,545** | 145,074 |
| ハード失敗のあるシナリオ | 0 | 177 | 213 |
| AoeFailure | 0 | 5 | 5 |
| BurstFailure | 0 | 46 | 46 |
| DeathsDesignFailure | 0 | 45 | 59 |
| drift_full_mode_gluttony_interval | 0 | 48 | 107 |
| GaugeFailure | 0 | 86 | 85 |
| IllegalAction | 0 | 6 | 6 |
| OpenerFailure | 0 | 2 | 2 |
| PotionFailure | 0 | 1 | 1 |
| WeaveOrderFailure | 0 | 13 | 13 |
| 方針時間 / GCD 窓 平均 | 2.7 µs | 758 µs | 325 µs |
| 方針時間 / GCD 窓 p99 | 8.1 µs | 1,843 µs | 1,503 µs |
| ヒープ確保 / GCD 窓 | 513 B | 170 B | 165 B |

全 746 シナリオ (範囲外は旧方針に委譲): DPS 指標 271.5 → 276.0、スコア 104,369 → 104,574。

注意: このハーネスの「旧」は RPR.cs をハーネス用に書き直した軽量版で、1 窓 2〜3 µs で動く。実際の RPR.cs の速度は B で比較する。ハード失敗は旧方針に合わせて作られた規則 (旧方針は全件 0 になるよう調整されている) なので、エンジン側の数字は「旧方針とどこが違う判断をしたか」の目安として読む。

**B. xan_timeline_harness (実モジュールを BMR の模擬ワールドで 300 秒、20 Hz で Execute)**:

| 指標 | 旧 RPR [Custom] (実 RPR.cs) | RPR [Engine] 深さ 4 | RPR [Engine] 深さ 3 |
|---|---:|---:|---:|
| 威力 | 104,753 | 104,104 (−0.6%) | 99,241 (−5.3%) |
| 総合 (威力 + 終端価値) | 107,518 | **107,614** (+0.1%) | 102,445 (−4.7%) |
| GCD / oGCD 数 | 128 / 60 | 130 / 58 | 125 / 57 |
| Execute 平均 | 78.2 µs | **40.6 µs** | 27.6 µs |
| Execute p99 | 164 µs | 513 µs | **195 µs** |
| ヒープ確保 / 呼び出し | 1,074 B | **1 B** | 1 B |

- 深さ 4: 平均は旧の約半分だが p99 は旧より遅い (探索が走るフレームで最大 0.5 ms の予算を使うため)。
- 深さ 3: 平均・p99 とも旧より速いが、与ダメが約 5% 落ちる。
- 既定は深さ 4 (与ダメ同等を優先)。p99 を優先する場合は重みの `HorizonGcds` を 3 にする。

### 13.3 CMA-ES

`tools/rpr_engine_eval tune --real 300 --limit 80 --generations 8 --seed 1 --depth 4`。評価はハーネス (`MetricsAnalyzer.Score / 戦闘時間 − 50 × ハード失敗数`)、対象 11 パラメータ (汎用 8 + Gluttony 価値補正 + DD 維持価値 + Shroud 価値補正)。適合度 330.8 → 436.8。結果は `tools/rpr_engine_eval/tuned/weights-RPR.json` と `RprDefinition.DefaultWeightsJson`。OverCap・LambdaScale・FillerScale・GluttonyCD・Shroud が探索範囲の端に張り付いているので、範囲を広げて回し直す余地がある。

### 13.4 エンジンで表現しきれなかったもの / 工夫で逃げたもの

1. **位置取り (Gibbet=側面 / Gallows=背面、True North)**: エンジンに位置の概念は無い。モジュール側で推奨位置を出し、外れるときに True North を積む (旧 RPR.cs と同じ方式)。
2. **パーティ由来の Immortal Sacrifice スタック**: ゲームでは味方の攻撃で溜まる。エンジンは Arcane Circle が 8 スタックを直接付与するとみなす。
3. **ハーネス固有の規則への合わせ込み**: ウィーブ内 Enshroud を窓開始時のゲージで判定する規則、240 秒前はポーションを AC より先に並べる規則は、エンジンではなく評価アダプタ側で吸収した (ゲーム上は合法な手)。
4. **「X の直後の窓では Y 禁止」**: 「Enshroud 終了直後のウィーブで Soul を使わない」は、1.2 秒の擬似ステータス `EnshroudEnding` で表現した。
5. **OR 条件**: 「Shroud 50 または Ideal Host」で使える Enshroud は、同じアクション ID の 2 スキル (`Enshroud` / `EnshroudIdeal`) に分けた。
6. **ポーション**: プラグインモジュールでは未対応 (Basexan のポーション処理を移していない)。
7. **レベル 100 未満、Basic モード、近接不可時、DancingMad / Windurst 専用ローテ**: 対象外 (ハーネスでは旧方針に委譲、プラグインでは何もしない)。
8. **ダウンタイム中の Soulsow**: 「対象がいないときだけ」を表す条件が無いので、詠唱 5 秒を設定して稼働中に選ばれないようにした。

### 13.5 実装中に直したエンジンの不具合

- ゲージ単価: 「ゲージを要求するスキル」だけを消費とみなすように変更 (Communio の Void リセットを消費と誤認していた)。1 点あたりの価値で比較し、2 段目の計算で「別のゲージを生む価値」(Enshroud → Lemure 5) も含める。
- 禁止条件 (`ForbidStatus`) が付いたスキルをフィラー計測から外していた (フィラー威力 0 になっていた)。
- 葉のコンボ評価を「次段の上乗せ威力」から「残りの連鎖の (威力 − フィラー) の累積」に変更 (浅い探索でコンボを途中から始め直す方を高く見ていた)。
- 根が oGCD のときに次の GCD を特定できず GCD を積まないことがあった → 三角主変化列で追跡。
- 名前→番号の検索がラムダで毎フレーム確保していた → ループに変更。
- 短い計測では JIT が最適化前 (Tier0) のままで 10 倍遅かった → 主要メソッドに `AggressiveOptimization`。

## 14. RPR [Engine] の詰め (Step 3b)

### 14.1 Execute p99 を旧 RPR.cs 以下に

- **探索をフレームに分割**: 反復深化を `Start` / `Continue` に分け、1 回の呼び出しで使うのは `FrameBudgetMs` (RPR: 0.08 ms) まで。状態が変わらない間は次のフレームで続きから再開する (置換表と完了済みの深さを保持)。途中は「完了した最深の結果」を返す。打ち切り判定は 8 ノードごと。
- これだけでは与ダメが不安定だった (GCD 数 117〜131)。原因は 2 つで、どちらも直した:
  1. 根が oGCD のとき、置換表のヒットで主変化列が途中で切れて次の GCD が不明 → 置換表をたどる方式へフォールバック。
  2. 対象不在 (ダウンタイム中) に Slice を選び続けていた → アダプタが「対象なし / 選択不可」を直近のダウンタイムとして渡す。
- 効果がなかった / 不要だったもの: 総予算を 1.0 ms に増やす (与ダメ変わらず)、フレーム予算 0.1 ms (p99 が 157〜171 µs で旧と同等)。

### 14.2 ハード失敗の原因と対処

| 原因 | 種類 | 対処 |
|---|---|---|
| 範囲の対象がいない (best ranged AoE / line / cone が null) のに Communio 等を選ぶ | 実バグ (エンジンが照準可否を知らない) | 状態に `DisabledSkills` (ビットマスク) を追加。アダプタが照準不可のスキルを立てる |
| Soul Slice のチャージ 0.9999 を 1 とみなす | 実バグ (変換の誤差補正) | 補正なしの切り捨て |
| Soul 50 超で Soul Slice (Soul 溢れ) | 実損 | `SoulSlice` に Soul ≤ 50 の条件 (先に Blood Stalk で消費させる) |
| Enshroud 終盤 / Reaver 中に DD が切れる | 実損 | Enshroud は DD 残り 13 秒以上、Gluttony は 6 秒以上、Blood Stalk / Grim Swathe は 3.5 秒以上を条件に |
| 同じ窓で Gluttony → Arcane Circle の順 | 実損 (Gluttony に AC が乗らない) | oGCD を 1 つずつ決める呼び出しでも、ほぼ同価値なら定義順 (AC 先) を選ぶ |
| 扇形 (Guillotine 等) の対象数を円形と同じとみなす | エンジンの表現不足 | 状態に `ConeTargets`、スキルに `Cone()` を追加 |
| ポーションの「開幕を除く偶数バースト」設定を無視 | 設定の伝達漏れ | 30 秒未満はポーション不可としてエンジンに渡す |
| Lemure 1 で Communio に対象がいないと GCD なし | まれな行き止まり | その窓だけ旧方針に委譲 (ハーネス) |
| Enshroud 中の Slice / Soul Slice (ゲームが拒否、BMR 模擬環境で GCD が止まる) | 実バグ (定義の誤り) | Enshroud 中はフィラー GCD を禁止 |
| 時間予算で探索が浅くなり失敗が増える | 評価方法 | 分析・チューニングは予算無制限・深さ 4 の決定的設定で行い、実時間は xan で別に測る |

**残り 24 シナリオ** (rpr_regression、決定的評価) の内訳と判断:

- **BurstFailure 10**: すべて旧方針の個別手順を指定したシナリオ (`post_perfectio_combo_priority_*` 4、`ending_duty_*` 4、`patch73_even_burst_two_ws_before_ac`、`perfectio_before_dd_to_fit_arcane` 等)。例: `patch73_even_burst_two_ws_before_ac` は「AC の前にウェポンスキル 2 回」を要求するが、エンジンは HarvestMoon → Void → Cross [AC] と AC を 3 GCD 目に置く (バースト内容は揃っている)。`ending_duty_trash_pack_hold_burst` は「次にボスが来るので雑魚にバーストを使わない」で、エンジンにはその情報が無く通常どおりバーストを使う → **別の妥当な選択 / 情報不足**として残した。
- **GaugeFailure 11**: 開幕 2GCD の 4 シナリオは、開幕の Enshroud → Communio の間 Soul Slice が 2 チャージのまま約 11 秒 (チャージ回復の損失は約 0.37 回分 ≈ 190 威力)。Soul ≤ 50 の条件と Enshroud 中の禁止が重なるため。**小さな実損**として残した (Soul を先に吐けば解消するが、開幕の Enshroud を遅らせる方が大きい)。他は `soul_slice_before_arcane_6s` などのシナリオ固有規則と、対象不在系の特殊シナリオ。
- **OpenerFailure 2** (`opener_0s_weave_order` / `opener_2gcd_weave_order`): 開幕の oGCD の並び順指定。エンジンは SoD [Potion, AC] → Soul Slice [Gluttony] と、ポーションと AC を 1 GCD 目に入れる → **別の妥当な選択**。
- **AoeFailure 1** (`grim_swathe_three_targets_enhanced_single_wins`): シナリオ固有規則。

### 14.3 再チューニング (範囲拡大)

範囲: OverCap 0〜10、LambdaScale 0.05〜1.5、FillerScale 0.05〜1.5、Gluttony 価値補正 −600〜2500、Shroud 価値補正 −2000〜1500、DD 維持価値 0〜120。1 回目の結果から開始して 10 世代: 適合度 435.8 → 462.7。LambdaScale と FillerScale は再び下限 (0.05) に張り付いた。定義側に RPR の規則が入った分、上位層の shadow price と地平線のフィラー補完の影響は小さいという結果で、外さずに小さい値で残した。Gluttony 補正は 0 → 728 (ドリフト失敗の解消に効いた)。

### 14.4 比較 (最終)

**A. xan_timeline_harness (実モジュール、BMR 模擬ワールド)**

| | 旧 RPR [Custom] | RPR [Engine] |
|---|---:|---:|
| 1 戦闘 300 秒: 威力 | 104,753 | **104,770** |
| 1 戦闘 300 秒: 総合 | 107,518 | **107,576** |
| 1 戦闘 300 秒: GCD 数 | 128 | 129 |
| 1 戦闘: Execute 平均 / p99 | 76.1 / 169 µs | **33.4 / 133 µs** |
| 9 戦闘 (timeline-matrix): 威力 | 636,791 | **639,926** (+0.5%) |
| 9 戦闘: 総合 (威力 + 終端資源価値) | **665,096** | 659,014 (−0.9%) |
| 9 戦闘: Execute 平均 / p99 | 61.7 / 152 µs | **28.9 / 135 µs** |
| ヒープ確保 / 呼び出し | 886〜1,053 B | **0〜1 B** |

9 戦闘の総合値の差は終端資源価値 (戦闘終了時に残ったゲージ等の評価) で、旧版は終了時に資源を多く抱えている。agents_rpr.md の方針 (「資源を貯めるだけの変更は利得ではない」) に従い、威力の方を主指標とした。

**B. rpr_regression (369 シナリオ、決定的評価)**

| | 旧 (移植版) | Step 3 | Step 3b |
|---|---:|---:|---:|
| DPS 指標 | 377.3 | 386.4 | **393.5** (+4.3%) |
| ハーネススコア | 144,131 | 144,545 | **146,773** |
| 失敗シナリオ | 0 | 177 | **24** |
| AoeFailure | 0 | 5 | 1 |
| BurstFailure | 0 | 46 | 10 |
| DeathsDesignFailure | 0 | 45 | 0 |
| drift_full_mode_gluttony_interval | 0 | 48 | 0 |
| GaugeFailure | 0 | 86 | 11 |
| IllegalAction | 0 | 6 | **0** |
| OpenerFailure | 0 | 2 | 2 |
| PotionFailure | 0 | 1 | 0 |
| WeaveOrderFailure | 0 | 13 | 0 |

## 15. 7.5 仕様監査の修正: GNB 威力 / SAM 詠唱時間

### 15.1 変更内容

- GNB: `GnbDefinition.cs` と `tools/xan_timeline_harness/GnbPotencyScorer.cs` (`GnbTerminalValue` を含む) の威力を 7.4 以降の値にした。
  Burst Strike 420、Gnashing Fang 440、Savage Claw 500、Wicked Talon 560、Jugular Rip 220、Abdomen Tear 260、Eye Gouge 300、
  Hypervelocity 180、Double Down 1000 (2 体目以降 15% 減)、Sonic Break 340 + DoT 120 × 5 = 940、Demon Slice 100、Demon Slaughter 160 (コンボ時)、
  Fated Circle 300。スコアラーの特性前 (Lv84 未満) の値は Burst Strike 340、Gnashing Fang 330、Savage Claw 410、Wicked Talon 490、
  Jugular Rip 180、Abdomen Tear 220、Eye Gouge 260、Hypervelocity 140。Demon Slice / Demon Slaughter / Fated Circle は特性による強化なし。
- SAM: `SamDefinition.cs` の居合術 / 奥義波切の詠唱を 1.8 秒から 1.3 秒 (Enhanced Iaijutsu) にした。`SamCombatState.CastTime()` も Lv74 以上で
  1.3 秒を使う (xan SAM.cs の `GetCastTime` と同じ扱い)。
- 威力モデルが変わったので CMA-ES で再チューニングした (設定は前回と同じ)。
  - GNB: `tune-xan --def gnb --job gnb-engine --weights tuned/weights-GNB-v3.json --args "timeline-matrix --scenario-limit 8" --gens 14 --pop 12
    --params "OverCap:0.5:3,Combo:0:3,LambdaScale:0:1,SwitchMargin:0:20,FillerScale:0.5:1.5,BurstBias:0:5,StatusRemainder:0:2,CooldownLambdaScale:0:2,UnlockScale:0:3"`
    → `tuned/weights-GNB-v4.json` (決定論 555,848 → 567,568)
  - SAM: `tune-xan --def sam --job sam-engine --weights tuned/weights-SAM-v1.json --args "timeline-matrix --scenario-limit 8" --gens 18 --pop 12
    --params "OverCap:0:3,Combo:0:2,LambdaScale:0:1.5,CooldownLambdaScale:0:2,SwitchMargin:0:100,FillerScale:0.5:1.5,BurstBias:0:4,StatusRemainder:0:2,UnlockScale:0:2,StatusValue.Fugetsu:0:30,StatusValue.Fuka:0:30"`
    → `tuned/weights-SAM-v2.json` (決定論 691,649 → 703,069)
  - 結果を各定義の `DefaultWeightsJson` に貼った (BudgetMs 0.8)。

### 15.2 比較 (xan_timeline_harness、修正後のスコアラー)

9 戦闘 = `timeline-matrix --scenario-limit 8`、ライブ = 組み込みデフォルト (0.8 ms・フレーム分割あり) で 3 回、無制限 = `ENGINE_FRAME_MS=1000` + BudgetMs 100。
「修正前」は a0ff2bf79 (旧スコアラー・旧詠唱時間) での値。

| | 修正前 Engine | 修正前 旧版 | 修正後 Engine (旧重み) | 修正後 Engine | 修正後 旧版 |
|---|---:|---:|---:|---:|---:|
| GNB 9 戦闘 ライブ | 593,492 | Akechi 589,320 | 555,848〜556,812 | **567,568 ×3** | Akechi 562,116 |
| GNB 9 戦闘 無制限 | | | 555,848 | 567,568 | |
| GNB 300 秒 | 92,196 | 93,664 | 86,556 | 89,024 | 93,664 → 89,444 |
| SAM 9 戦闘 ライブ | 687,496 | xan 666,453 | 685,226〜687,162 | **684,973〜688,429** | xan 676,608 |
| SAM 9 戦闘 無制限 | | | 691,649 | 703,069 | |
| SAM 300 秒 | 107,248 | 109,030 | 105,656 | 105,119 (無制限 106,840) | 109,030 |

(GNB 300 秒の旧版は修正前のスコアラーで 93,664、修正後 89,444。)

Execute (9 戦闘ライブ): GNB 平均 29〜31 µs・p99 169〜173 µs (修正前 32 / 166)、ヒープ確保 0 B。
SAM 平均 60〜62 µs・p99 396〜424 µs・1 ms 超 102〜111 回 (修正前 42 / 144、37 回)。SAM-v2 は探索が重く、v1 の重みのままなら
ライブ 685,226〜687,162・p99 124〜134 µs。ライブで v2 の利得は +1.7〜3k に留まる (決定論では +11k)。

300 秒単発は修正前から両ジョブとも旧版を下回っていた (GNB −1.6%、SAM −1.6%)。修正後は GNB −0.5%、SAM −3.6%。
チューニングの対象は 9 戦闘で、300 秒単発は含めていない。

### 15.3 回帰・他ジョブ

- sam_regression engine-compare (54 シナリオ、0.8 ms): hard fail 0 / soft fail 1 で a0ff2bf79 と同じ。
- engine_tests: 17/17 合格。
- 他ジョブ (共通エンジンは未変更、ライブ 1 回): RPR 663,115 (威力 644,253、xan 威力 636,791 / 総合 665,096)、NIN 649,267 (xan 639,456)、
  MNK 581,601 (xan 578,440)、PLD 567,362 (Akechi 540,983)。いずれも前回と同じ値。

## 16. 全体監査の修正 (a88fdf502) とパーティバフ込みの再チューニング

### 16.1 変更内容

- GNB: Bloodfest 中に弾数 0〜2 で Solid Barrel / Demon Slaughter を撃つと 2 発入っていた。Bloodfest 側の加算に「弾数 3 以上」を足し、
  コンボ締めは常に 1 発にした。条件が 3 つ要るので `JobBuilder` / `Effect` に 3 つ目の条件 `If3` を足した (Simulator で If / If2 と同様に判定)。
- NIN [Engine]: Forked Raiju は対象のヒットボックスまで 3y 以内なら Fleeting Raiju で押す (xan NIN.cs と同じ規則)。
- PLD: Intervene (突進) をエンジンのジョブ定義から外した (スキル・リキャスト・リキャスト読み取り)。
- 下位探索: oGCD の子ノードは GCD 数を減らさないので 1 GCD 枠に 2 回 weave する手順も探索される。枝刈りの上界 `_maxSlotValue` を
  1 枠あたり oGCD 2 個で数えるようにした (従来は MaxDeepOgcds = 1 個で、上界として不足していた)。
- アダプタ: 戦闘終了の推定が既知のとき `FightEndIn` を GCD + 0.1 秒以上にした (推定が短すぎて対象スキルが全部不正になるのを防ぐ)。
- 再チューニング (パーティバフ込みで旧版を下回ったジョブ): `tune-xan --args "timeline-matrix --scenario-limit 8 --party-buffs 7.8" --gens 14 --pop 12`。
  - MNK: v3 から → `tuned/weights-MNK-v4.json` (決定論 587,534 → 591,239)。
    `--params "OverCap:0.5:5,Combo:0:2,LambdaScale:0:1,SwitchMargin:0:20,FillerScale:0.5:1.5,BurstBias:0:4,StatusRemainder:0:2,CooldownLambdaScale:0:1,UnlockScale:0:2"`
  - RPR: rpr_engine_eval の重みから 2 回 → `tuned/weights-RPR-v2.json` → `tuned/weights-RPR-v3.json` (決定論 682,501 → 689,570 → 689,683)。
    `--params "OverCap:0:4,Combo:0:3,LambdaScale:0:1,SwitchMargin:0:60,FillerScale:0.05:1.5,BurstBias:0:5,StatusRemainder:0:2,CooldownValue.GluttonyCD:0:1500,GaugeValue.Shroud:-400:100"`
  - BLM: v5 から 2 回 (決定論 554,668 → 558,807 → 558,807)。ライブで旧版に届かず、バフなしでは下がったので採用しない (v5 のまま)。
    `--params "OverCap:0:3,SwitchMargin:0:20,FillerScale:0.5:1.5,BurstBias:0:5,CycleScale:0.5:1.5,UnlockScale:0:1,StatusValue.Thunderhead:0:150,CooldownValue.LeyLinesCD:0:2000,CooldownValue.TriplecastCD:0:500,CooldownValue.SwiftcastCD:0:1500"`

### 16.2 比較 (xan_timeline_harness、9 戦闘、ライブ = 組み込みデフォルト)

(a) = バフなし、(b) = `--party-buffs 7.8`。値は total / rdps (rdps はパーティ貢献込み。RPR / NIN 以外は total と同じ)。
「修正前」は 350a5e806、「修正後」は d2e985d95 (RPR は 3 回の範囲)。旧版 = xan [Custom] (RPR / NIN / MNK / SAM / BLM)、Akechi (GNB / PLD)。

| | 修正前 (a) | 修正後 (a) | 旧版 (a) | 修正前 (b) | 修正後 (b) | 旧版 (b) |
|---|---:|---:|---:|---:|---:|---:|
| RPR | 663,115 / 679,725 | 666,550〜666,567 / 682,942〜682,960 | 665,096 / 681,239 | 682,516 / 704,809 | 687,291〜689,735 / 709,222〜711,892 | 685,448 / 708,296 |
| NIN | 649,267 / 678,818 | 649,242 / 678,793 | 639,456 / 667,205 | 653,466 / 691,628 | 653,174 / 691,287 | 639,076 / 677,258 |
| MNK | 581,601 | 581,190 | 578,440 | 587,534 | 591,239 | 588,786 |
| SAM | 687,592 | 687,354 | 676,608 | 685,141 | 689,578 | 676,647 |
| BLM | 560,467 | 560,467 | **564,959** | 554,668 | 554,668 | **565,344** |
| GNB | 567,568 | 566,864 | 562,116 | 579,108 | 578,686 | 578,388 |
| PLD | 567,400 | 553,005 | 540,983 | 570,679 | 562,310 | 555,506 |

buffed_potency (b): RPR 206,530〜209,587 (旧版 227,809)、NIN 178,913 (205,099)、SAM 179,440 (190,705)、BLM 125,866 (110,898)、
GNB 145,146 (175,024)、PLD 155,097 (156,208)。MNK のハーネスは buffed_potency を記録しない。

- BLM だけが両条件で旧版を下回る (修正前から同じ値。−0.8% / −1.9%)。重みの再チューニングでは埋まらなかった。
- PLD は Intervene を外した分 (約 −14k / −8k) 下がったが旧版より上。
- failures (ハード失敗): BLM は 1 (a10n 開幕の「manual Ley Lines survives another action's emergency mode」境界チェック。修正前・旧版 xan BLM も同じ 1)、ほかは 0。

Execute (修正後、平均 / p99 µs、1 ms 超の回数): RPR 29〜32 / 121〜127 / 5〜10、NIN 32〜34 / 147〜165 / 22〜25、MNK 37 / 151〜160 / 74〜76、
SAM 66〜69 / 486〜530 / 119〜151、BLM 26〜27 / 156〜169 / 6〜8、GNB 29〜30 / 168〜180 / 46〜57、PLD 27〜44 / 121〜457 / 20〜151。

### 16.3 回帰

- engine_tests: 17/17 合格。
- nin_regression engine-compare (138 シナリオ): hard fail 0 (修正前も 0)、soft 139 (修正前 138。NinkiNearOvercap 1 → 2)。
- sam_regression engine-compare (54 シナリオ): hard fail 0 / soft fail 1 (修正前と同じ)。
- rpr_engine_eval compare (369 シナリオ、時間予算): ハード失敗のあるシナリオ 27 (修正前) → 134 (修正後・旧重み) → 192 (修正後・v3)、
  威力/秒 393.0 → 389.7 → 387.5 (旧 RPR.cs 移植 377.3)。修正後・旧重みの悪化は上界の修正 (oGCD 2 個) による: 上界を元に戻すと 28 / 393.4。
  正しい上界だと枝刈りが減り、時間予算内の探索が浅くなる。v3 は Gluttony のリキャスト価値が 0 になり、drift_full_mode_gluttony_interval が増える
  (2 → 102)。xan ハーネスの採点にはこの規則がない。

## 17. レベルシンク対応

### 17.1 変更内容

- 7 ジョブの定義を `Build(float gcd, int level = 100)` にした。習得前のスキルは定義に入れず、威力・リキャスト・チャージ数・効果は
  そのレベルの特性に合わせる。値の出典は xan_timeline_harness の各スコアラー / CombatState (l66 / l74 / l84 / l94 などの分岐) と
  `BossMod/ActionQueue` の習得レベル・特性レベル。主なもの:
  - RPR: Slice 系 (74 / 84 / 94)、Soul Slice 2 チャージ (78)、Soul Reaver (70、Gluttony は 96 未満も Soul Reaver)、Shroud (80)、
    Enshroud リキャスト 15 秒 (92 未満)、Void Shroud・Lemure 系 (86)、Communio (90 未満は 5 回目のリーピングで Enshroud が終わる)、
    Arcane Circle の Immortal Sacrifice (88)、Oblatio / Sacrificium (92)、Executioner (96)、Perfectio (100)。
  - SAM: 刃風 / 刃風改 (92)、風雅 / 風光 (86)、Kenki 獲得 (52 / 62)、風月・風花 (78 未満 1.10 / 10%)、居合術詠唱 1.8 秒 (74 未満)、
    燕返し (76)、明鏡止水 2 チャージ (76)、剣気 (80)、奥義波切 (90)、残心 (96)、天道 (100)。威力は l66 / l84 / l94。
  - MNK: 92 未満は連撃が Bootshine / True Strike / Snap Punch (Fury 込みの威力)、Masterful Blitz は Elixir Field / Flint Strike /
    Tornado Kick (Elixir Burst 92 / Rising Phoenix 86 / Phantom Rush 90)、Arm of the Destroyer (82 未満)、Howling Fist (74 未満)、
    Fire's Reply (100)、Wind's Reply (96)。Riddle of Wind は Wind's Reply 以外のモデル化された効果がないので 96 未満は定義に入れない。
    モジュールの GCD は Greased Lightning のヘイストをレベル別 (76: 20%、40: 15%、20: 10%、それ未満 5%) にした。
  - NIN: 92 未満は Trick Attack (威力 400、同じ KunaisBane ステータスで 10% 窓。モジュールは SID.TrickAttack を読む)、
    76 未満は活殺の対象が雷遁 / 火遁 (KassatsuRaiton / KassatsuKaton、30% 増し。印の押し方は MudraSequence に追加)、
    雷獣 (90)、密の Bhavacakra 強化 (88)、秘技 (96)、天地人 (70)、Tenri Jindo (100)。忍気 (Shukiho 62 / 78 / 84)、風魔 (54)。
  - GNB: カートリッジ上限 2 (88 未満、Bloodfest 中 4)、Danger Zone (80 未満)、Continuation (70、Hypervelocity 86、Fated Brand 96)、
    Ready to Break (54)、Ready to Reign (100)。威力は Melee Mastery (84) と Enhanced Brutal Shell (52)。
  - PLD: Rage of Halone (60 未満)、Spirits Within (86 未満)、Requiescat (96 未満、単体 320)、Divine Might (64、Prominence は 72)、
    Sword Oath (76)、Confiteor (80)、Blade 連携 (90)、Blade of Honor (100)、Riot Blade の MP (58)。威力は l84 / l94。
  - BLM: エノキアンの倍率 (96: 1.27、86: 1.22、78: 1.15、70: 1.10、56: 1.05)、Umbral Heart (58)、Paradox (90)、Astral Soul・Flare Star・
    Despair 即時 (100)、Foul 即時 (80)、Polyglot 上限 (70: 1、80: 2、98: 3)、Fire II / Blizzard II (82 未満)、Thunder / Thunder III /
    Thunder II / Thunder IV (92 未満。モジュールは対応する DoT の SID を読む)、Manafont 120 秒 (84 未満)、Swiftcast 60 秒 (94 未満)、
    Ley Lines 1 チャージ (96 未満)。
- 置き換わるアクションはスキル名を変えずに ActionId と威力だけ替えた (例: SAM の "Gyofu" は 92 未満で刃風)。モジュール側の名前参照
  (位置取り、ActionFor など) はそのまま使える。
- モジュールは `Player.Level < 100` で全スキルを無効にしていた処理を外し、`CreateEngine` で `player.Level` を定義に渡す。
  レベルが変わると (シンク・解除) BMR がプレイヤーの ClassChanged で回転モジュールを作り直すので (RotationModuleManager.RebuildActiveModules)、
  新しいレベルの定義で組み直される。EngineRotationModule には手を入れていない。
- 登録レベル (この下では BMR がモジュールを作らない):
  RPR 30 (Slice 連撃が揃う)、SAM 30 (月光と彼岸花)、GNB 30 (カートリッジと Burst Strike)、PLD 30 (連撃 3 段と Spirits Within)、
  NIN 66 (Trick Attack / Kunai's Bane の規則が Dokumori の窓を要る)、MNK 70 (Perfect Balance / Phantom Rush の規則が Riddle of Fire と
  紅蓮の極意を要る)、BLM 60 (Fire IV と Umbral Heart の回し)。
- 重みは全レベルで Lv100 のもの (再チューニングなし)。MNK の FillerPotency (500) も Lv100 の値のまま。

### 17.2 検証

- Lv100 の同一性: 6909daac1 と変更後で 7 ジョブの `Build(gcd)` を JSON に書き出して比較 (スキル・条件・効果・威力の float まで 27,655 行)、
  完全一致。Lv1〜100 の全レベルで 7 ジョブとも定義の構築と RotationEngine の生成が例外なく通る。
- xan_timeline_harness 9 戦闘 (`timeline-matrix --scenario-limit 8`)、Lv100、6909daac1 → 変更後:

| | 決定論 (ENGINE_FRAME_MS=1000、BudgetMs 100) | ライブ (組み込みデフォルト) | Execute 平均 / p99 (ライブ) |
|---|---:|---:|---:|
| RPR | 666,657 → 666,657 | 666,550 → 666,641 | 28.0 / 121 → 27.5 / 119 µs |
| NIN | 649,673 → 649,673 | 649,283 → 649,267 | 30.6 / 142 → 30.7 / 140 µs |
| MNK | 581,190 → 581,190 | 581,190 → 581,190 | 33.1 / 143 → 33.9 / 150 µs |
| SAM | 703,069 → 703,069 | 686,682 → 685,726 | 61.9 / 413 → 60.6 / 413 µs |
| GNB | 566,864 → 566,864 | 566,864 → 566,864 | 28.8 / 174 → 29.2 / 176 µs |
| PLD | 553,005 → 553,005 | 553,005 → 553,005 | 26.2 / 120 → 25.9 / 115 µs |
| BLM | 560,467 → 560,467 | 560,467 → 560,467 | 24.6 / 164 → 24.5 / 159 µs |

  RPR / NIN / SAM のライブの差は時間で切るフレーム分割による実行ごとの揺れ (同じビルドでも変わる。SAM は 683k〜687k)。決定論は一致。
- シンクレベル (`--level`、ライブ、総合値。旧 = xan RPR / NIN / MNK / SAM / BLM、Akechi GNB / PLD):

| | Lv90 Engine | Lv90 旧 | Lv80 Engine | Lv80 旧 | Lv70 Engine | Lv70 旧 | 下限 Engine | 下限 旧 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| RPR | 554,718 | 554,336 | 470,722 | 467,597 | 370,575 | 365,343 | 294,268 (30) | 293,305 |
| NIN | 527,998 | 518,829 | 447,488 | 457,925 | 399,656 | 385,054 | 367,070 (66) | 363,288 |
| MNK | 470,951 | 472,328 | 418,088 | 423,978 | 390,772 | 396,457 | 390,772 (70) | 396,457 |
| SAM | 545,090 | 549,301 | 494,032 | 503,146 | 391,471 | 392,325 | 270,432 (30) | 255,420 |
| GNB | 506,312 | 489,456 | 401,776 | 385,640 | 355,736 | 336,934 | 268,172 (30) | 254,656 |
| PLD | 456,752 | 448,482 | 374,451 | 376,553 | 339,775 | 340,768 | 235,282 (30) | 234,948 |
| BLM | 465,512 | 473,492 | 403,900 | 408,721 | | | 325,881 (60) | 333,347 |

  失敗は BLM のみ (Lv100・Lv90・Lv80 で 1、Lv60 で 2。旧 xan BLM も同数)。Execute はどのレベルでも Lv100 と同程度
  (例 Lv80: RPR 24 / 117 µs、SAM 70 / 594 µs、BLM 20 / 123 µs)。Lv100 の重みのままで 2.3% を超えて旧版を下回るレベルはなかった
  (最大は NIN Lv80 −2.3%、BLM Lv60 −2.2%)。
- engine_tests 17/17。定義を使う tools (blm_engine_eval、rpr_engine_eval、*_regression) はビルドが通る (Lv100 の既定値のまま)。

### 17.3 入れていないもの

- レベル別の重みのチューニング (指示どおり Lv100 の重みのまま)。
- 下限より下のレベル (モジュールは作られない)。MNK の Celestial Revolution、NIN の活殺水遁、BLM の Fire / Blizzard (I) は定義にない
  (Lv100 の定義にもない、または下限より下でしか使わないもの)。
- 旧版との比較は xan ハーネスのスコアラーの範囲 (各スコアラーのレベル分岐) まで。ゲーム内での確認はしていない。

## 18. 習得スキルに応じたトラック・規則 (レベルシンク下の UI 設定)

### 18.1 変更内容

- 0c0f2e5c3 の UI トラックは `Player.Level >= 100` でだけ動いていた (定義にないスキル名を `SkillIndex` で引くと例外になるため)。
  このガードを 7 モジュールから外し、どのレベルでも動くようにした。
  - `JobDefinition.TrySkillIndex` / `HasSkill` を追加。`Force` / `Forbid` は定義にない (そのレベルで未習得の) スキルなら何もしない。
    トラック名・選択肢・既定値は変更なし。
  - モジュールが自分で押すアクションは `ActionUnlocked` (レベル・クラス・習得クエスト) で判定: RPR Harpe / Soulsow / Arcane Crest、
    NIN Hide / Throwing Dagger、MNK Six-sided Star / Riddle of Earth (従来の `Level < 64` を置き換え)、SAM Meikyo Shisui (カウントダウン
    開幕) / Enpi / Meditate / True North (50 未満は未習得)、BLM Scathe、GNB Lightning Shot / Bloodfest (強制)、PLD Intervene / Shield Lob /
    Holy Spirit (64 未満は Holy Spirit を選ばず Shield Lob に落ちる)。
  - 位置取りヒント / True North、薬、Hold、開幕処理など、従来 Lv100 だけで動いていた処理もすべてのレベルで動く。
- Lv100 前提だった規則のレベル別の扱い (旧 xan / Akechi モジュールに合わせた):
  - SAM: 2 分バースト (薬 TwoMinuteBurst、返し / 波切の Hold の解除条件) は Ogi Namikiri (90) 未満では Ikishoten (68) 使用後 30 秒
    (Ogi Namikiri Ready が続く窓と同じ)。68 未満はバースト窓なし (xan も 68 未満は開幕以外で薬を使わない)。これがないと 76〜89 で
    Hold の返しが失効まで保持される。カウントダウン開幕は Meikyo Shisui (50) 習得から。Meditate の剣気満タン判定は Shoha (80) 未満では
    瞑想ゲージを見ない (xan と同じ)。
  - GNB: 「通常 + 溢れ防止」のカートリッジ上限を定義の上限 (88 未満は 2) から取る (従来は 3 固定で、88 未満では一度も消費しなかった)。
    Fated Circle (72) 未満では OnlyFC は Burst Strike を禁止せず、ForceFC 系は Burst Strike を押す (Akechi と同じ)。
  - PLD: Imperator のリキャストは 96 未満では Requiescat から読む (Akechi と同じ。ハーネスの数値は変わらず)。
  - BLM: 「Manafont 直前は Astral Fire を出ない」規則 (6f3ec1133) は Flare Star (100) があるときだけ。Lv90 パーティバフ込みで
    −0.5% (469,559 → 467,002)、90 未満は待つ間に撃てるものがない場合がある。
  - そのままにしたもの: RPR の薬は Arcane Circle (72) 必須のまま (xan も 72 未満は使わない)。ReserveGluttony は Gluttony (76) 未満でも
    Soul 100 まで温存 (xan と同じ)。GNB の Force*3 系 (弾 3 以上) は 88 未満では Bloodfest 中のみ成立 (Akechi と同じ)。
    NIN (66+)・MNK (70+) の定義の規則が要る Dokumori / Riddle of Fire / Brotherhood は登録レベルで習得済み。

### 18.2 検証

- Release ビルド 0 エラー 0 警告、engine_tests 17/17。
- 定義の構築 + RotationEngine で 60 秒の自己対戦: 7 ジョブ × Lv1〜100、例外 0。
- モジュールの smoke (xan_timeline_harness、`--scenario-limit 2`): 各ジョブの登録レベル〜100 の全レベル × 全トラックを k 番目の選択肢に
  揃えた組 (k = 1〜最大選択肢数−1、奇数 k はカウントダウン 12 秒、偶数 k は追加の敵 3 体) で 2,957 回、例外 0
  (ハーネスの規則チェック失敗は 532 回。わざと不自然な設定の組なので想定どおり)。
- Lv100 の同一性: 9 戦闘の決定論 (ENGINE_FRAME_MS=1000、BudgetMs 100) で、バフなし・`--party-buffs 7.8` とも 7 ジョブの出力行が
  変更前 (6f3ec1133) と完全一致 (RPR 666,657 / NIN 650,308 / MNK 581,190 / SAM 661,610 / BLM 560,467 / GNB 566,864 / PLD 553,005)。
- シンクレベル、既定トラック、決定論 (前 = 6f3ec1133、後 = 変更後、旧 = xan / Akechi):

| | Lv90 前 → 後 (旧) | Lv80 前 → 後 (旧) | Lv70 前 → 後 (旧) | 下限 前 → 後 (旧) |
|---|---:|---:|---:|---:|
| RPR | 554,100 → 554,718 (554,336) | 470,792 → 470,722 (467,597) | 370,575 → 370,575 (365,343) | 294,268 → 294,268 (293,305) (30) |
| NIN | 527,105 → 527,105 (518,829) | 455,554 → 455,554 (457,925) | 396,112 → 396,112 (385,054) | 372,492 → 372,492 (363,288) (66) |
| MNK | 471,514 → 470,951 (472,328) | 418,212 → 418,088 (423,978) | 391,985 → 390,772 (396,457) | (70) |
| SAM | 548,248 → 525,934 (549,301) | 495,279 → 468,669 (503,146) | 391,692 → 375,218 (392,325) | 270,432 → 270,432 (255,420) (30) |
| BLM | 465,512 → 465,512 (473,492) | 403,900 → 403,900 (408,721) | 365,845 → 365,845 (368,145) | 325,881 → 325,881 (333,347) (60) |
| GNB | 506,312 → 506,312 (489,456) | 401,776 → 401,776 (385,640) | 355,736 → 355,736 (336,934) | 268,172 → 268,172 (254,656) (30) |
| PLD | 457,388 → 457,388 (448,482) | 378,273 → 378,273 (376,553) | 339,485 → 339,485 (340,768) | 235,282 → 235,282 (234,948) (30) |

  - SAM の低下はカウントダウン開幕 (Meikyo Shisui → 月光) が 50 以上で動くようになったため。変更前は Lv100 未満でカウントダウン中から
    殴り始め、ハーネスがそれを数えていた (短い 8 戦闘で 1 戦闘あたり約 6 GCD)。xan も Lv100 の Engine も同じ開幕をする。長い戦闘
    (z1363) 単体では Lv70 で 286,291 → 288,513。
  - MNK の差は True North (位置取り) と Riddle of Earth (予測ダメージ) が動くようになったため。Riddle of Earth でハーネスの失敗
    「DMU predicted damage で Riddle of Earth を使わない」が 1 → 0。True North を Delay にすると変更前と一致。
  - `--party-buffs 7.8` でも同様 (SAM Lv90 549,456 → 525,947、MNK Lv90 477,421 → 472,968、RPR Lv90 563,638 → 567,573、ほかは一致か ±20)。
- Lv90 (SAM は Lv80、GNB の溢れ防止は Lv70) の非既定トラック。変更前はすべて既定と同じ結果 (下は変更後の「既定 → 設定」、トレースの回数):
  RPR Buffs=Delay → Arcane Circle 21 → 0、POT (--potions 5) → 薬 13。NIN Buffs=Delay → Dokumori 21 → 0、Potion=EvenBurst → 13。
  MNK BH=Delay → Brotherhood 21 → 0、Pot=OpenerAndEvenBursts → 13。SAM (Lv80) Tsubame=Hold → 返し 64 → 28 (Ikishoten の窓で解除)、
  Delay → 0、Potion=TwoMinuteBurst → 13。BLM LL=Delay → Ley Lines 27 → 0。GNB NM=Delay → No Mercy 34 → 0、Hold=HoldEverything →
  GCD 0、Potion=AlignWithBuffs → 13、
  (Lv70) Carts=NormalOvercapOnly → Burst Strike 46・アビリティ 0。PLD Atones=Delay → Atonement 系 168 → 0、FoF=Delay → 0。
- Execute (ライブ、平均 / p99 µs): Lv100 RPR 35.5 / 138、NIN 35.2 / 157、MNK 42.8 / 168、SAM 72.3 / 486、BLM 28.9 / 171、GNB 36.0 / 198、
  PLD 33.3 / 142。Lv90 RPR 35.3 / 131、NIN 34.8 / 164、MNK 37.6 / 147、SAM 75.8 / 635、BLM 26.7 / 150、GNB 34.1 / 186、PLD 32.9 / 121。
  Lv70 RPR 31.9 / 125、NIN 30.7 / 113、MNK 38.3 / 141、SAM 45.4 / 130、BLM 27.0 / 144、GNB 25.8 / 107、PLD 30.0 / 113。

### 18.3 入れていないもの

- 登録レベルの変更 (NIN 66 / MNK 70 / BLM 60 などはそのまま。この下ではモジュールが作られない)。
- MNK Riddle of Wind は定義が 96 から (72〜95 は Wind's Reply 以外の効果をモデル化していないので入れていない、§17 と同じ)。
  RoW トラックは 96 未満では何もしない。
- 新しいトラック・選択肢、レベル別の重みの再チューニング、ゲーム内での確認。

## 19. MNK Riddle of Wind (72〜95) とシンクレベル別の重み

### 19.1 変更内容

- MNK Riddle of Wind: 72〜95 の定義に入れた (威力 0、RiddleOfWindCD 90 秒、NeedsUptime 15 秒、効果なし)。このレベルの Riddle of Wind は
  オートアタックの間隔 −50% (15 秒) だけで Wind's Reply がない。xan ハーネスの MnkPotencyScorer / MnkCombatState はオートアタックを
  採点しない (Riddle of Wind はステータスを付けるだけ) ので、価値は 0 で表した。エンジンは価値 0 のアビリティを押す理由がないため、
  モジュールが 96 未満 (定義に WindsReply がないとき) は RoW トラックが Delay 以外なら `Force("RiddleOfWind")` で押す (リキャストごと、
  xan MNK の Automatic と同じ)。RoFAligned は従来どおり Riddle of Fire 中以外は Forbid、Delay は押さない。96 以上は変更なし。
- シンクレベル別の重み: 旧版を下回っていたジョブ・レベルで CMA-ES を回し、レベル帯ごとの重みを持たせた。
  - 設定は Lv100 のチューニングと同じ: `tune-xan --def <job> --job <job>-engine --weights <Lv100 の重み、BudgetMs 100>
    --args "timeline-matrix --scenario-limit 8 --level <L>" --pop 12`、gens は MNK / NIN / BLM / PLD 14、SAM 18 (§15 と同じ)。
    params: MNK・SAM・BLM は §15 / §16 の各ジョブのもの。NIN は `OverCap:0:3,Combo:0:2,LambdaScale:0:1,SwitchMargin:0:20,
    FillerScale:0.5:1.5,BurstBias:0:2,StatusRemainder:0:2,CooldownLambdaScale:0:1,UnlockScale:0:3`、PLD は GNB のもの (§15) の
    SwitchMargin を 0:60 にしたもの (PLD の Lv100 値 27.5 が入るように)。
  - 対象: MNK 90 / 80 / 70、SAM 90 / 80 / 70、NIN 80、BLM 90 / 80 / 70 / 60 (BLM の下限 60 も旧版未満)、PLD 80 (ライブで未満) / 70。
    SAM 70 と BLM 70 は §18 の決定論で旧版を下回っていたので加えた。RPR / GNB はどのレベルも旧版以上なので対象外。
  - 結果は `tools/blm_engine_eval/tuned/weights-<JOB>-L<レベル>.json` (13 個)。定義に `WeightsL90Json` などとして埋め込み
    (調整した値はそのまま、BudgetMs / MinNodes / SliceNodes は Lv100 のライブの値)、`DefaultWeights(int level = 100)` が
    レベル帯で選ぶ: 100 → 従来の DefaultWeightsJson (変更なし)、90〜99 → L90、80〜89 → L80、70〜79 → L70、60〜69 → L60 (BLM のみ)。
    そのジョブに帯のセットがないとき (NIN の 90〜99 / 66〜79、PLD の 90〜99 / 30〜69、SAM の 30〜69 など) は Lv100 のセット
    (従来どおり。旧版以上のレベル)。モジュールは `DefaultWeights(player.Level)` を使う (引数なしは Lv100 のままなので tools は変更なし)。
  - 2 回目の CMA-ES (1 回目の結果から) も SAM 90 / 80、MNK 80 / 70 で試したが +107〜+327 で、採用していない。

### 19.2 検証

- Lv100 の同一性: 9 戦闘の決定論 (ENGINE_FRAME_MS=1000、BudgetMs 100) で、バフなし・`--party-buffs 7.8` とも 7 ジョブの出力行が
  変更前 (001d0282e) と完全一致 (RPR 666,657 / NIN 650,308 / MNK 581,190 / SAM 661,610 / BLM 560,467 / GNB 566,864 / PLD 553,005)。
  Lv100 の定義は変更していない (MNK の追加は 72〜95 の分岐、重みは level >= 100 で従来の JSON)。
- シンクレベル、既定トラック (前 = 001d0282e、RoW = Riddle of Wind のみ、後 = 調整後、旧 = xan / Akechi)。決定論は調整した重みファイル
  (BudgetMs 100)、ライブは組み込みのレベル帯選択:

| | 決定論 前 → RoW → 後 | ライブ 前 → 後 | 旧 |
|---|---:|---:|---:|
| MNK 90 | 470,951 → 471,222 → **473,324** | 471,222 → 473,324 | 472,328 |
| MNK 80 | 418,088 → 417,596 → 420,217 | 417,596 → 420,217 | 423,978 |
| MNK 70 | 390,772 → 390,772 → 393,995 | 390,772 → 393,995 | 396,457 |
| SAM 90 | 525,934 → 543,317 | 523,660 → 539,122 | 549,301 |
| SAM 80 | 468,669 → 487,097 | 465,927 → 486,244 | 503,146 |
| SAM 70 | 375,218 → 388,811 | 374,070 → 387,940 | 392,325 |
| NIN 80 | 455,554 → **470,091** | 451,640 → 457,059〜457,154 | 457,925 |
| BLM 90 | 465,512 → **476,093** | 465,512 → **476,093** | 473,492 |
| BLM 80 | 403,900 → **410,069** | 403,900 → **410,069** | 408,721 |
| BLM 70 | 365,845 → **370,475** | 365,845 → **370,475** | 368,145 |
| BLM 60 | 325,881 → 327,848 | 325,881 → 327,848 | 333,347 |
| PLD 80 | 378,273 → **381,645** | 374,236 → **379,729** | 376,553 |
| PLD 70 | 339,485 → **345,975** | 339,775 → **345,795** | 340,768 |

  (太字 = 旧版以上。MNK は MinNodes で決定論とライブが一致。NIN のライブは 4 回で 457,059〜457,154。) BLM の失敗は 90 / 80 / 70 で 1
  (変更前・旧版と同じ)、60 は 2 → 1 (旧版 2)。ほかのジョブ・レベルの失敗は 0。調整していないジョブ・レベルの決定論は変更前と一致。
- Riddle of Wind (Lv80、9 戦闘のトレース): 25 回 (長い戦闘 z1363 で 17 回 = xan MNK と同じ回数・リキャストごと)。RoW=Delay 0 回、
  RoFAligned 21 回、Force 25 回。Lv80 / 90 の MNK の差 (RoW 前後) はアビリティ枠が 1 つ埋まる分 (−492 / +271)。
- Execute (ライブ、Lv100 のセットと帯のセットを同じ負荷で並べて実行、平均 / p99 µs / 1 ms 超の回数):
  MNK 90 41.0 / 149 / 65 → 43.1 / 153 / 64、80 44.0 / 156 / 61 → 42.7 / 160 / 69、70 41.0 / 145 / 37 → 43.0 / 149 / 43。
  SAM 90 76.0 / 613 / 177 → 81.4 / 745 / 237、80 73.7 / 514 / 148 → 54.5 / 179 / 85、70 46.6 / 128 / 36 → 51.0 / 138 / 47。
  NIN 80 34.5 / 129 / 18 → 32.3 / 124 / 22。BLM 90 28.8 / 158 / 4 → 33.0 / 169 / 5、80 28.0 / 141 / 3 → 25.3 / 134 / 4、
  70 26.5 / 144 / 4 → 29.0 / 144 / 3、60 24.7 / 130 / 4 → 27.3 / 136 / 3。PLD 80 30.8 / 116 / 19 → 31.4 / 118 / 20、70 29.2 / 117 / 17 → 29.1 / 105 / 24。
  コストが大きく増えたセットはない (最大は SAM 90 の p99 +22%、1 ms 超 177 → 237)。
- MNK 70〜100 の全レベル (`--scenario-limit 2`) で例外・失敗 0。engine_tests 17/17、blm_engine_eval / rpr_engine_eval / *_regression のビルドは通る。

### 19.3 旧版に届かなかったもの (診断)

- MNK 80 (−0.9%) / 70 (−0.6%): 旧 xan MNK は Six-sided Star を 13 回 (ダウンタイム前、12,326) 使い、ダウンタイム中に Steeled Meditation
  (22 回) でチャクラを貯めて Forbidden Chakra が 8 回多い (約 +2,900)。[Engine] は Six-sided Star を強制時のみ、Meditation を戦闘外のみ
  (MnkEngineModule の既定。Lv100 で Six-sided Star を自動にすると −85) にしている。重みでは変わらない (2 回目の CMA-ES も +122 / +327)。
  Lv90 は Phantom Rush 1400 などで差が埋まり旧版以上。
- SAM 90 (−1.9%) / 80 (−3.2%) / 70 (−0.9%): [Engine] は彼岸花を早く掛け直す (Lv80 で 58 回、xan 31 回。長い戦闘で 25〜40 秒ごと、
  xan は約 60 秒ごと)。その分 Midare Setsugekka と返しが少ない (74 / 73 回、xan 87 / 87)。閃の溢れ 18 回 (xan 0)、明鏡止水のスタック
  失効 10 回 (xan 4)。定義の彼岸花 (200 + 45/3 秒 × 60 秒) と DoT の価値 (増えた秒数だけ) はハーネスと一致しているので、
  4 GCD の探索と閃の影の価格で「閃 1 つの彼岸花」と「3 つ貯めて雪月花」の比較を誤っている。Lv100 でも長い戦闘で 36 回掛けているが、
  Ogi Namikiri / Tendo の分で旧版を上回っていた。重みでは 1/3〜1/2 しか埋まらず (2 回目の CMA-ES は +107 / +109)、規則か探索の変更が要る。
- NIN 80: 決定論では旧版を上回る (470,091) が、ライブ (時間で切るフレーム分割、MinNodes なし) は 457,059〜457,154 で −0.2%。
- BLM 60 (−1.6%): 調整は +1,967 のみ。トレースの単体威力の合計は [Engine] が上 (約 304k / 303k) で、差は長い戦闘 z1363 の複数体の区間
  (DoT・2 体目以降の威力)。xan は Flare 80 回 / Fire II 29 / Thunder II 29 / Blizzard IV 65、[Engine] は Fire IV 主体で Flare 39 回、
  Fire II・Thunder II は 0 回、Blizzard IV 4 回。重みでは埋まらなかった。

### 19.4 入れていないもの

- 上の差を埋める規則・機能 (MNK の Six-sided Star / Meditation、SAM の彼岸花の掛け直し間隔、BLM 60 の回し) は入れていない (指示どおり)。
- パーティバフ込み (`--party-buffs 7.8`) でのシンクレベルの再チューニング・比較、ゲーム内での確認。

## 20. レベルシンク時の固定優先順位ルール (Lv100 未満)

### 20.1 方針

- vin の方針「レベルシンク時は rDPS より確実性を重視する」に合わせ、`Player.Level < 100` では探索 (RotationEngine の分枝限定探索 + 重み) を
  使わず、ジョブごとの固定の優先順位ルールで次の GCD とウィーブする oGCD を決める。Lv100 は定義・重み・探索・出力とも変更なし。
- ここでの「確実性」: ゲージ・チャージを溢れさせない、DoT / バフ (RPR 死の意匠、SAM 彼岸花・風月 / 風花、BLM サンダー、GNB No Mercy 内の
  必須 GCD など) を落とさない、コンボを切らない、バースト CD はガイドどおり「リキャストごと / バフ内」、ガイドにない賭け (長いホールド・
  ドリフト) はしない。rDPS は結果として報告するだけで、受け入れ基準にはしていない。
- 出典: The Balance の各ジョブ Leveling Guide / Basic Guide、Icy Veins、公式ジョブガイドから起こしたレベル帯別仕様 (2026-10-07 の調査)。
  ガイドに直接の記述がなく Lv100 のルールと習得表から導いたものは [DERIVED] と書く。
- 既存のレベル帯別重み (`WeightsL90Json` など、`tools/blm_engine_eval/tuned/weights-*-L*.json`) とファイルは削除も変更もしていない。
  エンジンはこれまでどおり `DefaultWeights(player.Level)` で作られるが、Lv100 未満では `Decide` を呼ばないので判断には使われない。

### 20.2 仕組み (EngineRotationModule)

- `HasSyncedRules` / `SyncedOgcd` / `SyncedGcd` (virtual) と `DecideSynced` を追加。`Execute` は `Player.Level < 100 && HasSyncedRules` のときだけ
  `Engine.Decide` の代わりに `DecideSynced` を使う。Lv100 の分岐 (開幕・バーストの予算倍率、`Decide`、`FinishPending`) は中身を変えずに else 側に
  入れただけ。`ReadJobState` / `ApplyStrategy` / Push の流れ (GCD は High+2、oGCD は Low+1、Force は従来どおり上乗せ) は共通。
- 合法性はレベル同期済みの定義と `Simulator.IsLegal` で判定する。UI トラックの Forbid (`DisabledSkills`) はそのまま効き、Force は従来どおり
  押される。補助: `Legal` / `FirstLegal` / `Gauge` / `StatusLeft` / `Stacks` / `Charges` / `ReadyIn` / `FullIn` / `ComboIs` / `Disabled`。
- `SyncedOgcd` は現在の状態で oGCD を 1 つ選ぶ (遅らせるときは `SyncedOgcdDelay`)。`SyncedGcd` は GCD の時点まで進めた状態 (その oGCD が GCD
  の前に入るときは実行後の状態) で GCD を選ぶ。次の GCD がその oGCD に依存し、ウィーブ枠がないときは `SyncedOgcdFirst` で oGCD を先に押し、
  その回は GCD を出さない (BLM の Manafont / Transpose / 移動中の即時化、SAM の溢れ直前の Shinten)。
- `ReadCooldown` は最大チャージで止まっているチャージ制 CD を「1 チャージ・リチャージなし」と読む (クライアントの CD グループが空になり、
  単発 CD の分岐に入るため)。シミュレーターは時間を進めた時点で最大に戻すので、GCD を押す瞬間 (経過 0) だけ値がずれ、ルールが揺れた
  (NIN の印チャージ上限の弁が効かなかった)。`DecideSynced` の中でだけ状態のコピーを補正している (Lv100 の探索側は変えていない。20.6)。

### 20.3 ジョブ別ルール

各ジョブの `*EngineModule.cs` の `SyncedOgcd` / `SyncedGcd`。レベル帯の違いは「そのレベルで定義にないスキルは選ばれない」ことと、下に書いた
レベル分岐で表している。AoE は特記がなければ 3 体以上 (`s.Targets`)。

- RPR (30〜99、Balance RPR Leveling Guide 1-49 / 50-69 / 70-79 / 80 / 81-90、Icy Veins の優先順位、91〜99 は [DERIVED]):
  - oGCD: Arcane Circle (72+) をリキャストごと (死の意匠が付いてから = SoD > AC)、薬は AC 中 (定義の条件)。Sacrificium (92+) / Lemure's
    Slice・Scythe (86+、Void Shroud 2 = リーピング 2 回ごと)。Gluttony (76+) をリキャストごと (Soul 50)。Enshroud (80+、Shroud 50 または
    Ideal Host) は Soul Slice が 2 チャージ寸前 (Soul 50 以下で 2 チャージ、または 1 チャージで 10 秒以内に 2) と Gluttony が 10 秒以内
    (90+ は 13 秒、Soul 50 以上のとき) なら待つ。ただし次の reaver 2 回で Shroud が溢れるなら待たない。Blood Stalk / Unveiled (Grim
    Swathe は 3 体以上) は Soul 50 以上で、Gluttony が 10 秒以内に戻るなら 50 を残す (Soul 100 なら使う)。
  - GCD: Enshroud 中は Communio (90+、最後の Lemure) > リーピング (強化のある側、3 体以上 Grim Reaping)。Soul Reaver / Executioner は
    即座に Gibbet / Gallows (強化側、96+ は Executioner's、Guillotine は 4 体以上)。死の意匠 (Whorl は 3 体以上) は残り 5 秒未満、Enshroud
    直前で 15 秒未満 (定義の Enshroud 条件 13 秒)、AC 直前で 30 秒未満 (Balance の Lv90 2 分:「DD 30 秒未満で入る」)。Plentiful Harvest
    (88+)、Soul Slice / Scythe (Soul 50 以下)、Harvest Moon は AC 中 (プリプルの Soulsow、Balance Lv90 オープナー) か戦闘終了 5 秒前、
    コンボ (Spinning → Nightmare は 3 体以上)、何もできないときは Soulsow (トラックがダウンタイムに限定)。
  - [DERIVED]: 死の意匠の 5 秒 (ガイドは「切らさない」のみ)、Blood Stalk の 10 秒の温存、Enshroud を待たない Shroud 条件。
- SAM (30〜99、Balance SAM Leveling Guide 50 / 52 / 60 / 62 / 68 / 70 / 74 / 76 / 80 / 90、Basic Guide、Icy Veins):
  - oGCD: 明鏡止水はコンボの間 (仕上げの後) でチャージが満タンか満タン寸前のとき (1 分ごと、2 チャージは 76+)。Ikishoten (68+) は剣気
    35 以下 (+50 と次の GCD の 15 が入る余地)。Zanshin (96+)、Senei (72+) / Guren (70+、3 体以上) をリキャストごと、Shoha (80+) は瞑想 3。
    Shinten (3 体以上は Kyuten): 70 未満は剣気 25 以上ならいつでも (Balance 60-69「25 以上で」)、70 以上は 75 以上 (溢れ防止) と Ikishoten が
    2 GCD 以内で 35 超のとき。Senei が 2 GCD 以内なら 25、Zanshin Ready なら 50 を残す (90 以上は使う)。次の GCD で溢れる (剣気 86 以上)
    ときは最優先で Senei / Shinten を GCD の前に押す (ダウンタイムの Meditate 明け)。
  - GCD: 返し (波切 > 雪月花 > 五剣) は直後に使う (保持しない)。閃 3 で Midare、閃 2 で Tenka Goken (3 体以上、または Yukikaze (50) 未満)、
    Ogi Namikiri (90+)、彼岸花は閃 1 で残り 15 秒未満 (無しを含む) かつ対象が 48 秒以上生きるとき (Balance の 15 秒ルール、Icy Veins の 48 秒;
    40 未満は月光しかないので残り 3 秒。3 体以上では掛けない)。明鏡止水中は月光 > 花車 > 雪風 (欠けている閃、全部あるときはバフの短い方、
    3 体以上は満月 / 桜花)。コンボは始めたものを仕上げ、刃風の次は 10 秒未満のバフ > 欠けている閃 (花車 > 月光 > 雪風、仕様の Lv100 の閃の
    順「明鏡止水なしは Kasha > Gekko > Yukikaze」) > バフの短い方。3 体以上は風光 → 満月 / 桜花。
  - 明鏡止水のスタックが使い切れずに切れる (カウントダウン開幕の 14 秒前に押した分) ときは Ogi Namikiri と彼岸花より先に明鏡止水の技を使う。
  - [DERIVED]: バフ 10 秒の閾値、Ikishoten の剣気 35、Shinten の 75、明鏡止水を先にする条件。
- GNB (30〜99、Balance GNB Leveling Guide 26-39 / 40-59 / 60-71 / 72-93 / 94-100、Basic Guide、FAQ):
  - oGCD: 続剣 (Jugular / Abdomen / Eye / Hypervelocity / Fated Brand) を最優先、No Mercy をリキャストごと、Bloodfest は 94+ が定義の組
    (No Mercy の直前 / 直後)、94 未満は「ゲージが空のとき、No Mercy 内」(No Mercy 中ならいつでも。定義の組は最初のウィーブだけなので
    94 未満は条件を外して押す)、Danger / Blasting Zone をリキャストごと、Bow Shock は No Mercy 内 (No Mercy が Delay なら制限なし)。
  - GCD: Gnashing Fang の連撃は切らない。No Mercy 中は Double Down > Sonic Break > Gnashing Fang (4 体以上 (94+ は 3 体) は使わない) > 残りの
    カートリッジ (No Mercy の残り時間内に戻る Double Down 分 2 と Gnashing Fang 分 1 を残す) を Burst Strike / Fated Circle (72+、2 体以上)。
    No Mercy 外は Sonic Break の残り、Double Down は No Mercy まで保持 (No Mercy が Delay なら使う)、Gnashing Fang は 2 チャージに達する
    寸前だけ、Bloodfest の上限超え分の消化。コンボは仕上げで溢れるときだけ先に Gnashing Fang / Burst Strike。AoE コンボは 40+ で 2 体以上
    (Demon Slaughter)、40 未満は 3 体以上 (Demon Slice)。
  - [DERIVED]: カートリッジ予約の数え方 (ガイドは「Double Down 用 2 + Gnashing Fang 用 1」)、損失優先 DD > SB > GF。
- PLD (30〜99、Balance PLD Leveling Guide の各レベルの記述、Basic Guide):
  - oGCD: Fight or Flight をリキャストごと。Requiescat (68) 未満は Fast Blade (3 体以上は Total Eclipse) の後に遅らせて入れる (Balance 50 / 54 /
    60: 9 GCD を窓に入れる)。Requiescat は Fight or Flight 中。Circle of Scorn と Spirits Within / Expiacion はリキャストごと。
  - GCD: Confiteor の連携 (80+、90+ は Blade 3 段) > Requiescat のスタックで Holy Spirit (3 体以上は Holy Circle、72+) > Goring Blade (54+) >
    Divine Might と Sword Oath (76+) を次の Royal Authority の前に使い切る: Fight or Flight 中は 94 未満 Divine Might 先 (「DM Holy Spirit は
    Sepulchre より強い」)、94 以上は Sepulchre 先。外では Atonement → Supplication → Sepulchre → Divine Might。その後にコンボ
    (Prominence 連携は 3 体以上)。Holy Spirit の詠唱 (DM / Requiescat なし) は使わない (「DM があるときだけ」)。
  - 予報されたダウンタイムが 4 GCD 以内に始まるときは Goring Blade を先に使う (失効防止)。
  - Intervene は定義にない (従来どおり Dash トラックが押す)。
- MNK (70〜99、Balance Leveling / Basic Guide、Icy Veins。Lv90 / 80 / 70 の公開ガイドがないので全体に [DERIVED]):
  - oGCD: Brotherhood を Riddle of Fire の直前に重ね (定義の BrotherhoodFirst)、Riddle of Fire、以後リキャストごと。Perfect Balance は
    Opo-opo の GCD の直後 (Raptor の型) だけ: 偶数 (Brotherhood) 窓で 2 回 (定義の Pre / 通常)、奇数窓で 1 回 (定義の Odd)、ただし奇数は
    Nadi が 1 つ以下のとき (両方あると Phantom Rush になり、定義の「Phantom Rush は Brotherhood 内」で使えない)。Riddle of Wind は 96+
    (72〜95 は従来どおりトラックが押す)、Forbidden Chakra はチャクラ 5 (Enlightenment / Howling Fist は 3 体以上)。
  - GCD: 闘気が満ちたら即 (Phantom Rush > Elixir > Rising Phoenix)、Wind's Reply (96+)。Perfect Balance 中: Nadi 両方なら Opo-opo を 3 回、
    そうでなければ Lunar (Opo-opo 3 回) を先、90 以上で Nadi がないときは Solar 先 (Solar Lunar オープナー)。始めた組み合わせは続ける。
    通常は型の順 (Opo-opo → Raptor → Coeurl) で、それぞれ Fury があれば消費技、なければ付与技 (Dragon Kick / Twin Snakes / Demolish)。
- NIN (66〜99、Balance NIN Leveling Guide、Basic Guide、Icy Veins):
  - oGCD: Dokumori (66+、忍気 60 以下)、Trick Attack / Kunai's Bane (定義の Dokumori 窓・奇数条件)、Kassatsu は Trick が 10 秒以内か窓中
    (「次の 1 分窓の前に」)、Ten Chi Jin (70+) は窓中で Kassatsu の後、Meisui (72+、忍気 50 以下)、Dream Within a Dream は窓中 (Trick が
    30 秒以上先なら待たない)、Bunshin (80+) をリキャストごと。忍気: 窓中は使い切る、外は 90 以上か Dokumori 直前 (60 超) だけ、Bunshin が
    5 秒以内なら 50 を残す。Bhavacakra (68+) / Hellfrog (3 体以上と 68 未満)。
  - GCD: Ten Chi Jin の連携、雷獣 (90+、他の武器技で消えるので即)、Kassatsu の忍術 (窓中か Kassatsu 残り 4 秒未満。76 未満は活殺雷遁)、
    Suiton は Trick が 20 秒以内でまだ Shadow Walker がないとき (「Trick の CD が 20 秒を切ったら」)、Raiton (3 体以上 Katon) は窓中、
    窓外は印が上限で Trick が 5 秒より先のときだけ (チャージを溢れさせない)、Phantom Kamaitachi (82+) は Dokumori 中か残り 10 秒未満、
    コンボ: 風魔の装束があれば Aeolian Edge、なければ Armor Crush (4 以上で Armor Crush にならない)、Death Blossom → Hakke は 3 体以上。
  - [DERIVED]: 窓外の印の弁 (ガイドは「2 チャージになる前に Suiton」)、忍気 90、Phantom Kamaitachi の 10 秒。
- BLM (60〜99、Balance BLM Leveling Guide 60-71 / 72-89 / 90-99 単体、58-99 範囲):
  - 単体 (2 体以下): Astral Fire は Fire IV を MP が Despair (72+) の 800 を残せるまで、Paradox (90+) はハートを使い切った後 (「F4×3 >
    Paradox > F4×3」)、Firestarter は残り 5 秒未満のときだけ (氷に持ち越す)、Despair、Blizzard III。Umbral Ice は Blizzard IV (ハート
    3 未満) > Paradox (90+) > ポリグロット (「Xenoglossy はいつでも」) > Fire III (Firestarter があれば即時の方)。属性なしは Blizzard III
    から (ガイドに Lv100 未満のオープナーがないため、ループの入り口)。サンダーは DoT 残り 3 秒未満だけ (戦闘終了 10 秒前は打たない)。
    ポリグロットは満タンで次が 3 GCD 以内なら使う、戦闘終了前に使い切る。
  - 範囲 (3 体以上): 「(Umbral Ice から) Freeze > Foul / Thunder / Freeze > Transpose > Flare ×2 > Transpose」。氷でハート 3 未満は Freeze、
    3 なら Foul / Freeze、ハート 3 で Transpose、炎は Flare、MP 800 未満で Transpose。
  - oGCD: ダウンタイムは Astral Fire を Transpose で抜けて Umbral Soul (氷 III・ハート 3 まで。それ以上は押さない)、Manafont は炎の終わり
    (単体 MP 800 未満、72 未満は 1600 未満)、Ley Lines / Amplifier (86+、ポリグロット満タンでなければ) をリキャストごと、移動中で即時の GCD
    がなければ Triplecast > Swiftcast、Triplecast が 2 チャージで Astral Fire 中なら使う、ダウンタイム明けで Umbral Ice・ハート 3 なら
    Swiftcast (なければ Triplecast) で Fire III を即時に。
  - [DERIVED]: Firestarter の 5 秒、ポリグロットの 3 GCD、Triplecast の 2 チャージ、ダウンタイム明けの Swiftcast (ハーネスの「目標復帰後
    3.5 秒以内に GCD」を Fire III の詠唱 3.0 秒が 0.1 秒超えていた)。

### 20.4 検証

- Lv100 の同一性: 9 戦闘の決定論 (ENGINE_FRAME_MS=1000、BudgetMs 100) で、バフなし・`--party-buffs 7.8` とも 7 ジョブの出力行が変更前
  (37cf028ef) と完全一致 (RPR 666,657 / NIN 650,308 / MNK 581,190 / SAM 661,610 / BLM 560,467 / GNB 566,864 / PLD 553,005)。
- engine_tests 17/17。xan_timeline_harness / blm_engine_eval / rpr_engine_eval / engine_bench / engine_tuner / *_regression (blm / mnk / nin /
  sam / rpr) の Release ビルドは 0 エラー (警告は既存のもののみ)。
- シンクレベル、xan_timeline_harness 9 戦闘 (`timeline-matrix --scenario-limit 8 --level L`、既定トラック)。ルールは探索をしないので決定論と
  ライブが同じ (2 回の実行で一致)。前 = 37cf028ef のライブ (組み込みのレベル帯の重み)、旧 = xan RPR / NIN / MNK / SAM / BLM、Akechi GNB / PLD。
  確実性の指標はハーネスのカウンター (溢れは失った量、失効は回数、「s」は秒)。コンボ切れは直接のカウンターがない (GNB は続剣失効、PLD は
  Proc 失効が近い)。

| RPR | ルール | 前 (37cf028ef) | 旧 | 旧比 | 失敗 ルール / 前 / 旧 | DD 維持率 / Soul 溢れ / Shroud 溢れ (ルール ／ 前 ／ 旧) |
|---|---:|---:|---:|---:|---|---|
| Lv90 | 555,306 | 554,718 | 554,336 | 0.2% | 0 / 0 / 0 | 0.9958 / 0 / 0 ／ 0.9842 / 0 / 0 ／ 0.9964 / 40 / 0 |
| Lv80 | 469,211 | 470,722 | 467,597 | 0.3% | 0 / 0 / 0 | 0.9982 / 0 / 0 ／ 0.9787 / 0 / 0 ／ 0.9950 / 0 / 0 |
| Lv70 | 366,106 | 370,575 | 365,343 | 0.2% | 0 / 0 / 0 | 0.9967 / 0 / 0 ／ 0.9822 / 0 / 0 ／ 0.9938 / 0 / 0 |
| Lv60 | 324,989 | 327,878 | 323,467 | 0.5% | 0 / 0 / 0 | 0.9982 / 0 / 0 ／ 0.9822 / 0 / 0 ／ 0.9953 / 0 / 0 |
| Lv50 | 303,994 | 305,704 | 303,994 | 0.0% | 0 / 0 / 0 | 0.9990 / 0 / 0 ／ 0.9961 / 0 / 0 ／ 0.9990 / 0 / 0 |

| NIN | ルール | 前 (37cf028ef) | 旧 | 旧比 | 失敗 ルール / 前 / 旧 | 忍気溢れ / 雷獣失効 / 印チャージ上限 s (ルール ／ 前 ／ 旧) |
|---|---:|---:|---:|---:|---|---|
| Lv90 | 539,116 | 527,285 | 518,829 | 3.9% | 0 / 0 / 0 | 10 / 2 / 19.5 ／ 0 / 4 / 0.2 ／ 5 / 5 / 35.7 |
| Lv80 | 479,604 | 457,079 | 457,925 | 4.7% | 0 / 0 / 0 | 5 / 0 / 15.2 ／ 0 / 0 / 0.1 ／ 0 / 0 / 31.4 |
| Lv70 | 403,665 | 400,003 | 385,054 | 4.8% | 0 / 0 / 0 | 0 / 0 / 15.2 ／ 0 / 0 / 0.1 ／ 0 / 0 / 25.0 |
| Lv68 | 383,067 | 370,589 | 366,186 | 4.6% | 0 / 0 / 0 | 5 / 0 / 17.5 ／ 0 / 0 / 0.1 ／ 0 / 0 / 20.0 |

| MNK | ルール | 前 (37cf028ef) | 旧 | 旧比 | 失敗 ルール / 前 / 旧 | チャクラ溢れ / 闘気失効 / PB 失効 (ルール ／ 前 ／ 旧) |
|---|---:|---:|---:|---:|---|---|
| Lv90 | 473,845 | 473,324 | 472,328 | 0.3% | 0 / 0 / 0 | 1 / 1 / 2 ／ 1 / 2 / 2 ／ 1 / 0 / 4 |
| Lv80 | 422,984 | 420,217 | 423,978 | -0.2% | 0 / 0 / 0 | 1 / 0 / 3 ／ 5 / 1 / 2 ／ 1 / 0 / 4 |
| Lv70 | 391,772 | 393,995 | 396,457 | -1.2% | 0 / 0 / 0 | 0 / 1 / 3 ／ 4 / 1 / 3 ／ 0 / 0 / 1 |

| SAM | ルール | 前 (37cf028ef) | 旧 | 旧比 | 失敗 ルール / 前 / 旧 | 剣気溢れ / 閃溢れ / Proc 失効 / 彼岸花切れ s / 風月なし GCD (ルール ／ 前 ／ 旧) |
|---|---:|---:|---:|---:|---|---|
| Lv90 | 534,066 | 537,867 | 549,301 | -2.8% | 0 / 0 / 0 | 10 / 0 / 5 / 38.6 / 6 ／ 85 / 3 / 9 / 29.6 / 3 ／ 20 / 0 / 6 / 114.7 / 1 |
| Lv80 | 489,536 | 486,526 | 503,146 | -2.7% | 0 / 0 / 0 | 10 / 0 / 3 / 37.9 / 9 ／ 20 / 17 / 11 / 31.9 / 2 ／ 20 / 0 / 4 / 118.2 / 1 |
| Lv70 | 395,394 | 387,940 | 392,325 | 0.8% | 0 / 0 / 0 | 20 / 0 / 2 / 42.2 / 1 ／ 0 / 11 / 9 / 42.6 / 2 ／ 25 / 0 / 3 / 153.6 / 2 |
| Lv60 | 336,216 | 321,875 | 332,428 | 1.1% | 0 / 0 / 0 | 0 / 0 / 2 / 35.1 / 1 ／ 0 / 18 / 9 / 30.5 / 2 ／ 0 / 0 / 3 / 153.6 / 2 |
| Lv50 | 318,674 | 306,661 | 314,278 | 1.4% | 0 / 0 / 0 | 0 / 0 / 2 / 36.9 / 1 ／ 0 / 17 / 9 / 34.7 / 7 ／ 0 / 1 / 3 / 189.8 / 2 |

| BLM | ルール | 前 (37cf028ef) | 旧 | 旧比 | 失敗 ルール / 前 / 旧 | ポリグロット溢れ (ルール ／ 前 ／ 旧) |
|---|---:|---:|---:|---:|---|---|
| Lv90 | 443,574 | 476,093 | 473,492 | -6.3% | 1 / 1 / 1 | 1 ／ 0 ／ 1 |
| Lv80 | 397,478 | 410,069 | 408,721 | -2.8% | 1 / 1 / 1 | 0 ／ 0 ／ 2 |
| Lv70 | 356,780 | 370,475 | 368,145 | -3.1% | 1 / 1 / 1 | 2 ／ 1 ／ 16 |
| Lv60 | 322,167 | 327,848 | 333,347 | -3.4% | 1 / 1 / 2 | 0 ／ 0 ／ 0 |

| GNB | ルール | 前 (37cf028ef) | 旧 | 旧比 | 失敗 ルール / 前 / 旧 | 弾溢れ / 続剣失効 / NM 外バースト (ルール ／ 前 ／ 旧) |
|---|---:|---:|---:|---:|---|---|
| Lv90 | 497,188 | 506,312 | 489,456 | 1.6% | 0 / 0 / 0 | 0 / 0 / 0 ／ 3 / 10 / 4 ／ 0 / 0 / 0 |
| Lv80 | 404,952 | 401,776 | 385,640 | 5.0% | 0 / 0 / 0 | 0 / 0 / 0 ／ 8 / 11 / 0 ／ 0 / 0 / 0 |
| Lv70 | 347,598 | 355,736 | 336,934 | 3.2% | 0 / 0 / 0 | 0 / 0 / 0 ／ 28 / 0 / 0 ／ 8 / 0 / 0 |
| Lv60 | 280,962 | 285,368 | 272,010 | 3.3% | 0 / 0 / 0 | 0 / 0 / 0 ／ 24 / 0 / 0 ／ 8 / 0 / 0 |
| Lv50 | 224,016 | 232,820 | 222,300 | 0.8% | 0 / 0 / 0 | 0 / 0 / 0 ／ 0 / 0 / 0 ／ 0 / 0 / 0 |

| PLD | ルール | 前 (37cf028ef) | 旧 | 旧比 | 失敗 ルール / 前 / 旧 | Goring 失効 / DM 失効 / Oath 失効 (ルール ／ 前 ／ 旧) |
|---|---:|---:|---:|---:|---|---|
| Lv90 | 462,705 | 456,752 | 448,482 | 3.2% | 0 / 0 / 0 | 2 / 1 / 1 ／ 0 / 0 / 1 ／ 0 / 3 / 22 |
| Lv80 | 385,452 | 379,729 | 376,553 | 2.4% | 0 / 0 / 0 | 2 / 1 / 1 ／ 0 / 0 / 1 ／ 0 / 3 / 22 |
| Lv70 | 347,416 | 345,795 | 340,768 | 2.0% | 0 / 0 / 0 | 2 / 0 / 0 ／ 2 / 2 / 0 ／ 0 / 1 / 0 |
| Lv60 | 253,810 | 250,845 | 248,645 | 2.1% | 0 / 0 / 0 | 0 / 0 / 0 ／ 2 / 0 / 0 ／ 0 / 0 / 0 |
| Lv50 | 230,258 | 232,418 | 225,832 | 2.0% | 0 / 0 / 0 | 0 / 0 / 0 ／ 0 / 0 / 0 ／ 0 / 0 / 0 |

- BLM の失敗 1 は全レベル・全版に共通のハーネスの境界テスト (「manual Ley Lines survives another action's emergency mode」)。それ以外の
  失敗は 0。ルール実装の途中で出た「目標復帰後 3.5 秒以内に GCD がない」(Umbral Soul を復帰直前まで押していた、Fire III の詠唱) は上の
  Umbral Soul の上限とダウンタイム明けの即時化で 0 にした。
- 残っている確実性の指標の内訳: PLD の Goring 失効 2 は 42 秒戦闘が 10 秒で予報なしに対象を失うもの (ガイドの順で Requiescat のスタックを
  先に使う)。SAM の Proc 失効は、カウントダウン開幕の明鏡止水 (従来のモジュール処理、14 秒前) の残りを明鏡止水優先にして減らした
  (Lv90 9 → 5、Lv80 以下は 2〜3)。Lv90 の残り 5 は、開幕直後に対象が消える 27.5 秒戦闘 2 本の明鏡止水 2 と Ogi Namikiri Ready 2、長い戦闘の
  明鏡止水 1。SAM の剣気溢れはすべてダウンタイム中の Meditate (モジュールの既存処理) の分。NIN の印チャージ上限は窓の直前 (Trick 5 秒
  以内) に上限に達した分など (旧版の約半分)。
- 旧版との差の主な理由: BLM はガイドの範囲閾値 3 体 (旧 xan と探索は 2 体から Flare。閾値を 2 にすると Lv90 452,709 / Lv70 365,626 で
  約 +9,000) と、ガイドどおり Blizzard III から入る開幕 (探索は Fire III + Manafont で入り、10 秒で対象が消える短い戦闘で差が大きい)。
  SAM 90 / 80 は旧版が彼岸花をほぼ 1 分おきに保持するのに対し、ルールは 15 秒ルールで早めに掛け直す (彼岸花切れは旧版の 1/3)。MNK 70 は
  旧 xan の Six-sided Star / Meditation 分 (§19.3、ルールでも変えていない)。
- UI トラック (Lv90、SAM は Lv80、GNB の溢れ防止は Lv70、トレースの回数): RPR Buffs=Delay → Arcane Circle 21 → 0、POT (--potions 5) → 薬 13。
  NIN Buffs=Delay → Dokumori 21 → 0、Potion=EvenBurst → 13。MNK BH=Delay → Brotherhood 21 → 0、Pot=OpenerAndEvenBursts → 13。
  SAM Tsubame=Hold → 返し 80 → 30、Delay → 0、Potion=TwoMinuteBurst → 13。BLM LL=Delay → Ley Lines 27 → 0。GNB NM=Delay → No Mercy 34 → 0、
  Hold=HoldEverything → GCD 0、Potion=AlignWithBuffs → 13、ST/AOE=ForceSTFinishWithOvercap → 範囲技 213 → 0、(Lv70) Carts=NormalOvercapOnly →
  アビリティ 0。PLD Atones=Delay → Atonement 系 224 → 0、FoF=Delay → 0。
- 全レベル × 全トラックのスモーク: §18.2 と同じ 2,957 回 (各ジョブの登録レベル〜100 の全レベル × 全トラックを k 番目の選択肢に揃えた組、
  奇数 k はカウントダウン 12 秒、偶数 k は追加の敵 3 体、`--scenario-limit 2`) で例外 0。ハーネスの規則チェックの失敗がある実行は 532 回
  (BLM 164 / GNB 142 / MNK 155 / PLD 71、前回と同じ組。わざと不自然な設定の組)、失敗数の合計は 1,173 → 1,108。

### 20.5 ガイドから外れた判断

- GNB: 94 未満の Bloodfest は定義の組 (No Mercy の直前 / 最初のウィーブ) を外し、「ゲージが空、No Mercy 内」ならいつでも押す (ガイドどおりに
  するため定義の条件のほうを外した)。
- BLM: ダウンタイム明けの Swiftcast / Triplecast による Fire III の即時化 (ガイドは Triplecast を「移動・ウィーブ用」とだけ書く)。
  Triplecast を 2 チャージで Astral Fire 中に使う (チャージを溢れさせないため)。
- NIN: 窓外でも印が上限なら Raiton を撃つ (ガイドは窓外の忍術を Suiton だけとし、上限になる前に Suiton を撃つとだけ書く)。
- SAM: 返し (Tsubame) は保持せず直後に使う (ガイドは「次のバフ窓まで保持」も挙げるが、30 秒で失う賭けなので採らない。Hold トラックは従来どおり)。
- PLD: 予報されたダウンタイム直前は Goring Blade を Requiescat のスタックより先に使う。
- MNK: 奇数窓の Perfect Balance は Nadi が 1 つ以下のときだけ (定義の Phantom Rush の Brotherhood 条件と合わせるため)。Riddle of Fire は
  早めのウィーブ (ガイドの Lv100 は遅め)。
- [DERIVED] の採用箇所は 20.3 の各ジョブの末尾に書いた。

### 20.6 入れていないもの

- `ReadCooldown` の最大チャージの読み違い (20.2) は Lv100 の探索側では直していない (直すと Lv100 の出力が変わる)。Lv100 でも満タンの
  チャージ制 CD (明鏡止水、Gnashing Fang、Perfect Balance、印、Triplecast、Soul Slice、Ley Lines) を 1 チャージと読んでいる。ハーネスで確認;
  ゲーム内も満タンの CD グループは空になるので同じはずだが、ゲーム内では確認していない。
- Lv100 の探索・重み・定義、レベル帯の重みファイル、登録レベル (NIN 66 / MNK 70 / BLM 60 など) は変更なし。
- 新しいトラック・設定・ログ出力。Intervene (Dash トラック)、Doton / Huton、Six-sided Star / Meditation の自動化、ゲーム内での確認。

## 21. ReadCooldown: 最大チャージで止まっているチャージ制 CD の読み

### 21.1 変更内容

- `EngineRotationModule.ReadCooldown`: CD グループが空 (経過・合計とも 0) のときのチャージ数を 1 から `cd.MaxCharges` にした。最大チャージで
  止まっているチャージ制 CD (明鏡止水、Gnashing Fang、Perfect Balance、印、Triplecast、Soul Slice、Ley Lines など) はリチャージが止まり
  グループが空になるので、これまでは「1 チャージ・リチャージなし」と読んでいた (§20.2)。単発 CD (MaxCharges 1) は従来と同じ。
- §20 で `DecideSynced` に入れていた同じ補正 (状態のコピーで最大チャージに直す処理) は不要になったので外した (二重にしない)。
- 重み・定義・探索は変更なし。

### 21.2 検証

- Lv100 の決定論 (9 戦闘、ENGINE_FRAME_MS=1000、BudgetMs 100): バフなし・`--party-buffs 7.8` とも 7 ジョブの出力行が修正前 (3c94fdbdc) と
  完全一致 (RPR 666,657 / 689,735、NIN 650,308 / 652,226、MNK 581,190 / 591,239、SAM 661,610 / 659,564、BLM 560,467 / 560,364、
  GNB 566,864 / 578,686、PLD 553,005 / 560,806)。探索は根から時間を進めた最初の一歩で最大チャージに戻すので、この 9 戦闘では判断が
  変わらなかった。
- シンクレベル (RPR / SAM / GNB / PLD 90〜50、NIN 90 / 80 / 70 / 68、MNK 90 / 80 / 70、BLM 90〜60 の 31 通り): 出力行が修正前と完全一致
  (§20 のシンク時の補正を ReadCooldown に移しただけで、ルールが見る値は同じ)。
- ライブ (組み込みの重み、BudgetMs 0.8。修正前 / 修正後は各 2 回、6 本並列で実行) と旧版 (xan [Custom] / Akechi):

| | 修正前 ライブ | 修正後 ライブ | 旧版 | 修正前 ライブ (b) | 修正後 ライブ (b) | 旧版 (b) |
|---|---:|---:|---:|---:|---:|---:|
| RPR | 665,543 / 666,637 | 666,629 / 666,452 | 665,096 | 689,735 | 689,735 | 685,448 |
| NIN | 649,267 / 649,267 | 649,443 / 649,267 | 639,456 | 652,731 | 652,333 | 639,076 |
| MNK | 581,190 / 581,190 | 581,190 / 581,190 | 578,440 | 591,239 | 591,239 | 588,786 |
| SAM | 661,854 / 661,848 | 658,758 / 661,848 | **676,608** | 659,878 | 659,139 | **676,647** |
| BLM | 560,467 / 560,467 | 560,467 / 560,467 | **564,959** | 560,364 | 560,364 | **565,344** |
| GNB | 566,864 / 566,864 | 566,864 / 566,864 | 562,116 | 578,686 | 579,259 | 578,388 |
| PLD | 553,005 / 553,005 | 552,881 / 553,005 | 540,983 | 560,806 | 560,806 | 555,506 |

  ((b) = `--party-buffs 7.8`。太字 = Engine が旧版を下回る。ライブの差は同じビルドでも出る時間分割の揺れの範囲で、決定論は一致。)
- 旧版を下回るのは SAM と BLM で、どちらも修正前から (SAM は §18 のカウントダウン開幕以降、BLM は §16 から)。修正によるものではないが、
  受け入れ基準「Lv100 の [Engine] は旧版より rDPS を下げない」に合わせ、この 2 ジョブで CMA-ES を回した (Lv100 の既存設定、
  `tune-xan --args "timeline-matrix --scenario-limit 8" --pop 12`、params は §15 / §16 と同じ):
  - SAM: Lv100 の重みから 18 世代 → 決定論 661,611 → 674,064、続けて 18 世代 → 674,471 (旧版 676,608 には届かない)。ライブ (BudgetMs 0.8、
    単独実行) は現行 661,628 / 660,821 に対して 658,283 / 657,278、`--party-buffs 7.8` は 659,322 → 653,531 と下がるので採用しない。
    探索が 0.8 ms で打ち切られる条件では効かない重みだった。
  - BLM: v5 から 14 世代、改善なし (560,467 のまま。§16 と同じ結果)。
  - 結果として重みは変更なし。調整結果のファイルはリポジトリに入れていない。
- engine_tests 17/17。xan_timeline_harness / blm_engine_eval / rpr_engine_eval / engine_bench / engine_tuner / *_regression (blm / mnk / nin /
  sam / rpr) の Release ビルドは 0 エラー。
- 全レベル × 全トラックのスモーク (§20.4 と同じ 2,957 回): 例外 0、規則チェックの失敗がある実行 532 回・失敗数の合計 1,108 で §20 と同じ。
  §20 の実行と結果が違ったのは Lv100 の 6 本 (NIN / SAM、時間で切るライブ探索の揺れ) だけで、Lv100 未満は全行一致。

### 21.3 入れていないもの

- SAM / BLM の Lv100 の旧版との差 (SAM −2.2%、BLM −0.8%) を埋める規則・定義の変更 (重みでは埋まらなかった)。
- ゲーム内での確認。

## 22. SAM: 彼岸花の掛け直しを残り 15 秒未満に絞る (Lv100) と、旧版との差の調査

### 22.1 変更内容

- `SamEngineModule.ApplyStrategy`: 彼岸花トラックが Delay / Force 以外のとき、対象の彼岸花の残りが 15 秒以上なら彼岸花を Forbid する
  (シンク時ルール §20.3 と同じ閾値)。定義 (Core) の条件に「残り○秒未満」の種類がないので、既存のトラックと同じ Forbid で表した。
  探索の地平線 (4 GCD) の間は根の状態の Forbid が続く。
- 背景: Lv100 の [Engine] SAM は旧版 xan を下回っていた (§21)。カウントダウン開幕の数え方は両者で同じ (どちらも −14 秒の明鏡止水と
  −0.75 秒の月光を数える、短い 8 戦闘で 3,360) で計測差ではなく、長い戦闘 z1363 で彼岸花を掛け直しすぎていた (36 回、間隔の中央値
  41.8 秒、xan 25 回 / 63.8 秒)。早い掛け直しは残りの DoT を上書きするだけで DoT の合計は同じ (28,250 / xan 28,363) だった。

### 22.2 検証

| SAM Lv100 | 変更前 (c0aa8d063) | 変更後 | 旧版 xan |
|---|---:|---:|---:|
| 9 戦闘 決定論 | 661,610 | 666,567 | 676,608 |
| 9 戦闘 決定論 `--party-buffs 7.8` | 659,564 | 665,068 | 676,647 |
| 9 戦闘 ライブ (0.8 ms) | 663,624 / 659,827 | 662,566 / 662,453 / 662,566 | 676,608 |
| 9 戦闘 ライブ `--party-buffs 7.8` | 658,548 | 666,427 | 676,647 |
| 300 秒単体 (`event-timeline --duration 300`) | 107,450 | **109,253** | 109,030 |
| Execute 平均 / p99 µs (ライブ) | 61.9 / 331〜349 | 56.7〜57.3 / 277〜298 | |

- **旧版には届いていない** (9 戦闘で決定論 −1.5%、`--party-buffs 7.8` −1.7%、ライブ −2.1%)。300 秒単体だけ旧版以上。
- 長い戦闘 z1363 (変更前 / 変更後 / xan): 技の威力 503,653 / 508,645 / 521,019、彼岸花 36 / 27 / 25 回 (間隔の中央値 41.8 / 50.6 / 63.8 秒、
  45 秒未満の掛け直し 25 / 0 / 2)、雪月花・返し雪月花 各 36 / 39 / 42、天道雪月花・天道返し 各 28 / 28 / 29。
- 閾値を 10 / 5 / 0 秒 (切れてから) にした版も測った (採用していない): 決定論 663,207 / 665,362 / 663,981、`--party-buffs 7.8` 666,331 /
  666,779 / 666,368、ライブ 663,888〜666,108 / 660,631〜661,139 / 662,857〜664,066、300 秒単体 106,324 / 107,457 / 107,457。
  間隔は xan 並み (61.6 / 66.0 / 68.2 秒) になるが雪月花は 39〜40 回で増えず、彼岸花の DoT が切れる分 (27,346 / 27,233 / 26,103) で相殺され、
  Execute も重くなる (平均 64〜68 / p99 415〜465 µs)。
- 他 6 ジョブの Lv100 決定論 (バフなし・あり) とシンクレベルの全 31 通り (SAM 90〜50 を含む) は出力行が変更前と完全一致。engine_tests 17/17、
  tools のビルドは 0 エラー。全レベル × 全トラックのスモーク 2,957 回で例外 0、規則チェックの失敗は §21 と同じ (532 回、合計 1,108)。

### 22.3 残りの差の調査 (ソース変更なし)

長い戦闘 z1363 (変更後) の GCD は Engine・xan とも 667 個。差 (技の威力 −12,374) の内訳:

- 閃の数: 閃を作る GCD は Engine 233 / xan 239。明鏡止水で作った閃 83 / 86 (Engine は明鏡止水のスタックの失効あり)、刃風の後にまた刃風
  (コンボのやり直し) 4 / 0、彼岸花 27 / 25。雪月花系に回る閃は 206 / 214 で、雪月花 + 天道は 67 / 71 回。
  - 明鏡止水の失効: 496 秒に明鏡止水を押した 3 秒後から 40 秒のダウンタイム (Meditate) に入り、明鏡止水のスタックと天道 (100) が切れた
    (proc_lost 2)。天道雪月花 + 天道返しの 1 組 (約 3,460) と明鏡止水の 2〜3 閃分。
  - コンボのやり直し (344 / 749 / 764 秒など): 探索が、コンボが刃風のときに刃風をもう一度選んでいる (トレースで確認、対象あり)。
- 雪風: 雪風 83 / 90、花車への分岐 (士風) 39 / 32。明鏡止水の使い先は Engine 月光 41・花車 31・雪風 11、xan 31 / 40 / 15。
  ersharifst の Lv100 解説 (通常コンボは雪から、明鏡止水は月 / 花) にはどちらも近く、雪風の差の原因は閃の数のほう (上のコンボやり直しで
  雪風コンボが 72 / 75)。
- 紅蓮: 定義に紅蓮はある (70+、閃影とリキャスト共有、剣気 25、2 体目以降も 400)。ハーネスでは紅蓮は 10y の直線で、z1363 の範囲区間では
  2 体に当たり 400 × 2 = 閃影 800 と同じ値 (xan の紅蓮 904 = 閃影 904)。Engine が紅蓮を選ばないのは値が同じだからで損ではない。
  差は使用回数 (閃影 + 紅蓮 12 / 13 回、約 −900)。
- 彼岸花のタイミング: ersharifst は「切れるタイミングでちょうどよく、明鏡止水を絡めて月 → 彼岸花」で、xan の約 64 秒間隔がこれに近い。
  15 秒版の Engine は約 13 秒早い。閾値だけ下げると上の測定のとおり DoT が切れるので、ガイドどおりにするには「明鏡止水 → 月光 → 彼岸花」で
  切れる時点に合わせる組み立てが要る。

見込み (直すとしたら。いずれも未実施):

- 明鏡止水をダウンタイム直前に押さない (定義の明鏡止水に `NeedsUptime` を足す、約 3 GCD): 天道 1 組と明鏡止水 2〜3 閃、約 +3,500〜4,500。
- コンボが刃風・陣風・士風のとき刃風 (3 体以上は風光) を Forbid (`SamEngineModule.ApplyStrategy`): 刃風 4 回分、約 +1,000〜2,000。
- 彼岸花を明鏡止水と組んで切れる時点に合わせる (上記ガイド): 掛け直し 2 回分と閃、約 +1,500 (規則の追加が要る)。
- 合計で約 +6,000〜8,000 (長い戦闘の差 12,374 の半分程度)。

### 22.4 見つけたもの: 閃影 / 紅蓮のリキャスト

- 7.0 以降、Enhanced Hissatsu (Lv94) で閃影・紅蓮のリキャストは 60 秒 (それ以前は 120 秒)。BossMod の `SAM.cs` には特性 `EnhancedHissatsu = 591`
  があるが、アクション定義のリキャストは 120 秒のままで、特性による短縮を適用する仕組みがない。
- そのため xan_timeline_harness では 94 以上でも 120 秒で、Engine (定義の `SeneiCD` は全レベル 120 秒) と xan の両方が約 120 秒ごと
  (z1363 で 12〜13 回) にしか使えない。ゲーム内では準備完了をクライアントのリキャストから読むので両モジュールとも 60 秒で使えるが、
  Engine の計画 (上位層・探索の先読み) は 120 秒として扱う。
- 直すなら: `SamDefinition` の `SeneiCD` を 94 以上で 60 秒、ハーネスの SAM のリキャスト (BossMod のアクション定義か `SamCombatState`) に
  Enhanced Hissatsu を反映。両方のスコアが上がる (z1363 で 12 回ほど増え、剣気の使い道が必殺剣・震天から閃影に替わる分、1 回あたり約 +550)。
  今回は変更していない。

出典: [Hissatsu: Senei (consolegameswiki)](https://ffxiv.consolegameswiki.com/wiki/Hissatsu:_Senei)、[Hissatsu: Guren (consolegameswiki)](https://ffxiv.consolegameswiki.com/wiki/Guren)。
