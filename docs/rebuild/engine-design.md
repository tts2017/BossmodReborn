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

## 23. SAM: 閃影 / 紅蓮のリキャスト 60 秒 (Lv94+) と、残りの差への規則の試行

### 23.1 変更内容

- `SamDefinition`: `SeneiCD` を 94 以上で 60 秒 (Enhanced Hissatsu)、それ未満は 120 秒のまま。
- `tools/xan_timeline_harness/SamCombatState.StartCooldown`: 94 以上の閃影 / 紅蓮のリキャストを 60 秒にした (アクション定義は 120 秒のままで、
  特性による短縮を適用する仕組みがない。§22.4)。Engine と xan の両方の測定に効く。BossMod 本体のアクション定義は変更していない。
- 以下の 3 つの規則を 1 つずつ試したが、効果がない・下がったので入れていない (23.3)。

### 23.2 測定 (SAM Lv100)

決定論 = 9 戦闘 (ENGINE_FRAME_MS=1000、BudgetMs 100)、ライブ = 組み込み (0.8 ms) を 2 回、(b) = `--party-buffs 7.8`、300 秒 = `event-timeline
--duration 300`。z1363 は長い戦闘 (1,514 秒) の技の威力と回数。

| | 決定論 | 決定論 (b) | ライブ | ライブ (b) | 300 秒 | z1363 | 彼岸花 回 / 間隔 | 雪月花 | 閃影+紅蓮 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| ハーネス修正前 Engine (4bbe22482) | 666,567 | 665,068 | 662,623 / 662,566 | 666,427 | 109,253 | 508,645 | 27 / 50.6 秒 | 39 | 12 |
| ハーネス修正前 xan | 676,608 | 676,647 | 676,608 | 676,647 | 109,030 | 521,019 | 25 / 63.8 秒 | 42 | 13 |
| 1. 60 秒 Engine (採用) | 672,188 | 673,349 | 672,702 / 673,099 | 673,155 | **110,496** | 514,760 | 26 / 56.4 秒 | 38 | 24 |
| 1. 60 秒 xan | 683,232 | 683,304 | 683,232 | 683,304 | 110,286 | 527,584 | 25 / 63.8 秒 | 42 | 25 |
| 2. 明鏡止水 NeedsUptime(7) | 672,188 | 673,349 | 672,652 / 672,954 | 673,007 | 110,496 | 514,760 | 26 / 56.4 秒 | 38 | 24 |
| 3. コンボ途中の刃風 / 風光を Forbid | 665,106 | 667,636 | 668,958 / 667,307 | 668,825 | 107,537 | 507,285 | 29 / 48.4 秒 | 33 | 24 |
| 4a. 彼岸花の早い更新は明鏡止水中だけ | 673,144 | 673,544 | 670,552 / 672,138 | 671,095 | 108,700 | 516,930 | 24 / 63.8 秒 | 40 | 24 |
| 4b. 4a + 残り 6 秒未満は明鏡止水なしでも | 671,560 | 671,524 | 672,382 / 672,174 | 670,867 | 108,700 | 514,752 | 24 / 61.6 秒 | 39 | 24 |

- 新しい基準 (ハーネス修正後) の差: Engine 672,188 / xan 683,232 (−1.6%)、`--party-buffs 7.8` 673,349 / 683,304 (−1.5%)、ライブ −1.5%。
  300 秒単体は Engine が上 (110,496 / 110,286)。両方とも閃影 / 紅蓮が約 2 倍 (z1363 で 24 / 25 回) になった。
- Engine の定義の 60 秒だけ (ハーネス修正後、定義は 120 秒のまま) でも結果は同じ (672,188)。準備完了は読み取ったリキャストから決まり、
  この 9 戦闘では先読みの違いが判断を変えなかった。定義はゲームに合わせて 60 秒にしてある。
- Execute (ライブ、平均 / p99 µs): ハーネス修正前 61.0 / 347、修正後 57.3〜59.8 / 308〜342 (同程度)。

### 23.3 入れなかった規則

- 2. 明鏡止水にダウンタイム前の使用禁止 (`NeedsUptime(7)`): 変化なし。z1363 の 496 秒の明鏡止水は、直後 (499 秒) の対象喪失が予報されて
  いない (トレースで探索は対象が消えるまで対象ありと見ている)。予報されないダウンタイムには効かない。
- 3. コンボ途中 (刃風・陣風・士風・風光の後で次の段を習得済み) の刃風 / 風光を Forbid: 刃風のやり直しは 4 → 0 回になったが、決定論
  −7,082。根の状態の Forbid は探索の地平線 (4 GCD) 全体に残り、仕上げの後の刃風も計画できなくなる。定義の条件に「コンボが○でない」の
  種類がないので、正しく表すには条件の種類の追加 (既存構造の変更) が要る。
- 4. 彼岸花を明鏡止水と組んで切れる時点に合わせる (ersharifst の奇数分の更新): 4a は決定論 +956 だがライブ・300 秒単体が下がり、彼岸花の
  DoT が切れる (z1363 の DoT 26,894、基準 28,080)。4b はすべて下がった。ガイドの「明鏡止水 → 月 → 彼岸花」を切れる時点に合わせる
  組み立ては、根の状態の Forbid では表せなかった。

### 23.4 検証

- 他 6 ジョブの Lv100 決定論 (バフなし・あり) とシンクレベル全 31 通り (SAM 90〜50 を含む) は出力行が §22 と完全一致。
- SAM 94〜99 (Enhanced Hissatsu の範囲) はハーネスの修正で値が変わる: Lv94 598,542 (xan 617,039)、Lv96 / 99 611,586 (xan 630,412)。
- engine_tests 17/17、tools のビルドは 0 エラー。全レベル × 全トラックのスモーク (2,957 回): 例外 0、規則チェックの失敗は §22 と同じ (532 回、
  合計 1,108)。§22 と結果が違ったのは SAM 94〜100 (ハーネスの修正) と Lv100 のライブの揺れ (NIN / PLD) だけ。

出典: [Hissatsu: Senei (consolegameswiki)](https://ffxiv.consolegameswiki.com/wiki/Hissatsu:_Senei)、[Hissatsu: Guren (consolegameswiki)](https://ffxiv.consolegameswiki.com/wiki/Guren)。

## 24. MNK: Masterful Blitz の零の型 (Formless Fist) と、RPR: Enshroud リキャスト 5 秒 (全レベル)

### 24.1 変更内容

- MNK: ゲームでは Masterful Blitz (Elixir Burst / Rising Phoenix / Phantom Rush、低レベルの Elixir Field / Flint Strike / Tornado Kick /
  Celestial Revolution) のすべてが零の型 (Formless Fist) を 30 秒付与する (consolegameswiki の Elixir Burst、ersharifst の 7.3 Lv100 ガイド
  「零の型は、演武・必殺技・乾坤闘気弾で付与されます。付与したら必ず双竜脚か猿舞連撃に使う」)。エンジンの定義も xan のハーネスもこれを
  持っていなかった (定義の先頭コメントには「ブリッツは零の型を付与しない」と書いてあった)。
  - `MnkDefinition.Blitz()`: `.ApplyStatus(Formless, 30)` を追加 (Fire's Reply は以前から付与している。`Step()` の「Opo-opo の確定クリティカル
    (零の型でも)」と「型が合わないときは零の型を消費する」はそのまま)。先頭コメントを直した。
  - `tools/xan_timeline_harness/MnkCombatState.ConsumeBlitz` (4 つのブリッツの case から呼ばれる): `Set(SID.FormlessFist, FormDuration)` を
    追加 (FiresReply / FormShift と同じ 30 秒)。tools/mnk_regression の `ApplyBlitz` は既に FormlessFistLeft = 30 にしていたので、ハーネスを
    それに揃えた形 (mnk_regression は変更なし)。Engine と xan の両方の測定に効く。
  - `MnkEngineModule.SyncedGcd` (Lv100 未満の固定ルール): ブリッツ / Wind's Reply / Perfect Balance 中の分岐の後、型の順のループの前に
    「零の型が付いていれば Opo-opo の GCD (`opo`、FirstLegal の順は LeapingOpo / DragonKick / Bootshine)」を入れた (上のガイドの規則)。
    §20.3 の MNK の GCD の項「通常は型の順 (Opo-opo → Raptor → Coeurl)」はこの一段が前に付く。Lv100 の探索は定義の変更で零の型を見る。
- RPR: パッチ 7.3 で Enshroud のリキャストは 15 秒 → 5 秒になった (レベル 92 の特性ではない。Enhanced Enshroud (92) は Oblatio を
  付与するだけ。consolegameswiki の Enshroud)。
  - `RprDefinition`: `.Cooldown(EnshroudCD, level >= 92 ? 5 : 15)` → 全レベル 5 秒。§17.1 の「Enshroud リキャスト 15 秒 (92 未満)」はこれで
    上書き (旧節は書き換えていない)。
  - `tools/xan_timeline_harness/RprCombatState.StartCooldown`: `player.Level >= 92 ? 5 : 15` → 5。tools/rpr_regression は旧モジュールの仕様の
    エミュレーションなので触っていない。Lv100 は以前から 5 秒なので出力は同じ。

### 24.2 検証

決定論 = 9 戦闘 (`timeline-matrix --scenario-limit 8`、ENGINE_FRAME_MS=1000、BudgetMs 100)、ライブ = 組み込み (0.8 ms) を 2 回、
(b) = `--party-buffs 7.8`、300 秒 = `event-timeline --duration 300`。修正前 = 0ede0b7ed のハーネス + モジュール、修正後 = 両方を直したもの。
MNK の [Engine] は MinNodes / SliceNodes でライブも決定論と一致する。

| MNK Lv100 | 決定論 | 決定論 (b) | ライブ | ライブ (b) | 300 秒 | Execute 平均 / p99 µs (ライブ) |
|---|---:|---:|---:|---:|---:|---:|
| 修正前 Engine (0ede0b7ed) | 581,190 | 591,239 | 581,190 / 581,190 | 591,239 | 92,068 | 39.9〜43.7 / 158〜182 |
| 修正前 xan | 578,440 | 588,786 | 578,440 / 578,440 | 588,786 | 92,584 | 62.1〜63.5 / 119 |
| 修正後 Engine | 578,459 | **589,056** | 578,459 / 578,459 | 589,056 | 92,352 | 41.4〜41.8 / 163〜173 |
| 修正後 xan | 578,440 | 588,786 | 578,440 / 578,440 | 588,786 | 92,584 | 68.9〜69.6 / 142〜144 (他の実行と並列) |

- 受け入れ基準 (9 戦闘 `--party-buffs 7.8` で [Engine] が xan を下回らない) は満たす: 589,056 / 588,786 (+0.05%)。バフなし 578,459 / 578,440
  (+0.003%)、ライブも同じ。300 秒単体は修正前から xan を下回っていて (92,068 / 92,584)、修正後も下 (92,352、差は縮小)。
  基準を満たしているので CMA-ES の再調整は回していない (重みは v4 のまま)。
- 修正前より [Engine] が下がった (決定論 −2,731、(b) −2,183)。9 戦闘のカウンター (修正前 → 修正後): ブリッツ 47 → 45、Phantom Rush 12 → 11、
  闘気の失効 (blitz_expired) 2 → 3、Beast Chakra の消失 (beast_drop) 8 → 11、Opo-opo の GCD 289 → 279。探索の価値が零の型の分だけ変わり、
  Perfect Balance とブリッツの並びが数戦闘で変わった。300 秒単体のトレースでは、修正後もブリッツの直後に Opo-opo を打ってはいない
  (ブリッツの直後は Brotherhood 内の 2 回目の Perfect Balance か Raptor の型の GCD)。
- xan は修正後も同じ出力 (ブリッツの直後は Raptor の型の GCD で、型が合っている間は零の型が消費されず、30 秒で切れる。零の型を Opo-opo に
  使う判断がない)。xan の Execute は他の実行と並列に測ったもの。
- 他 6 ジョブの Lv100 決定論 (バフなし / (b)) は出力行が修正前と完全一致: RPR 666,657 / 689,735、NIN 650,308 / 652,226、SAM 672,188 / 673,349、
  BLM 560,467 / 560,364、GNB 566,864 / 578,686、PLD 553,005 / 560,806 (RPR は Lv100 では以前から 5 秒)。
- シンクレベル (9 戦闘、`--level L`、既定トラック。ルールは探索をしないので決定論 = ライブ)。「ルール変更なし」= ハーネスだけ直して
  `SyncedGcd` の零の型の段を入れない版。カウンターはチャクラ溢れ / 闘気失効 / PB 失効:

| MNK | 修正前 ルール | ルール変更なし | 修正後 ルール | 旧 xan (修正前 / 後) | 旧比 (修正後) | カウンター 修正前 ／ 修正後 ／ 旧 |
|---|---:|---:|---:|---:|---:|---|
| Lv90 | 473,845 | 473,845 | 473,352 | 472,328 / 472,328 | 0.2% | 1 / 1 / 2 ／ 0 / 0 / 3 ／ 1 / 0 / 4 |
| Lv80 | 422,984 | 422,984 | 418,988 | 423,978 / 423,978 | -1.2% | 1 / 0 / 3 ／ 0 / 1 / 3 ／ 1 / 0 / 4 |
| Lv70 | 391,772 | 391,772 | 394,080 | 396,457 / 396,457 | -0.6% | 0 / 1 / 3 ／ 0 / 1 / 2 ／ 0 / 0 / 1 |

  - ハーネスの修正だけではルールの出力は変わらない (ルールは型の順を守るので零の型を消費せず、xan と同じ)。零の型の段を入れると
    Lv70 +2,308、Lv90 −493、Lv80 −3,996 (Lv80 は旧版を 1.2% 下回る。修正前は −0.2%)。Opo-opo の GCD は増える (Riddle of Fire 内の Opo-opo
    Lv80 152 → 167) が、Perfect Balance が「Opo-opo の直後」の規則で後ろにずれ、ブリッツが 46 → 44 回に減る。ガイドの規則どおりに
    したので採用し、Lv80 の差は残している (§24.3)。失敗は全部 0。

| RPR | 修正前 ルール | 修正後 ルール | 旧 xan 修正前 | 旧 xan 修正後 | 旧比 (修正後) | DD 維持率 / Soul 溢れ (修正後 ルール ／ 旧) |
|---|---:|---:|---:|---:|---:|---|
| Lv90 | 555,306 | 555,398 | 554,336 | 552,926 | 0.4% | 0.9955 / 0 ／ 0.9823 / 10 |
| Lv80 | 469,211 | 469,211 | 467,597 | 467,597 | 0.3% | 0.9982 / 0 ／ 0.9950 / 0 |

  - Lv80 以下 (70 / 60 / 50 も) は両モジュールとも出力が同じ (Enshroud を 15 秒以内に続けて使う場面がない)。Lv90 は xan が 5 秒のリキャストで
    Enshroud の並びを変えて −1,410 (Soul 溢れは 40 → 10)、ルールは +92。
- engine_tests 17/17。BossModReborn / xan_timeline_harness / blm_engine_eval / rpr_engine_eval / engine_bench / engine_tuner / *_regression
  (blm / mnk / nin / sam / rpr) の Release ビルドは 0 エラー (警告は既存のもののみ)。
- 全レベル × 全トラックのスモーク (§20.4 と同じ 2,957 回、修正後のビルド): 例外 0、規則チェックの失敗がある実行 532 回 (BLM 164 / GNB 142 /
  MNK 155 / PLD 71)・失敗数の合計 1,108 で §20〜23 と同じ。

### 24.3 入れていないもの

- MNK Lv100 の重みの再調整 (基準を満たしているので回していない。修正前より決定論で 2,731 低い)。
- MNK Lv80 のルールの差 (旧版 −1.2%): 零の型の段で Perfect Balance の位置がずれる分。ガイドの規則 (零の型は必ず Opo-opo に使う) を
  優先し、Perfect Balance の規則は変えていない。
- xan モジュール (MNK / RPR) の判断は変えていない。tools/mnk_regression / tools/rpr_regression、BossMod 本体のアクション定義も変更なし。
- ゲーム内での確認。

出典: [Elixir Burst (consolegameswiki)](https://ffxiv.consolegameswiki.com/wiki/Elixir_Burst)、[Enshroud (consolegameswiki)](https://ffxiv.consolegameswiki.com/wiki/Enshroud)、
[ersharifst 7.3 Lv100 MNK ガイド](https://www.ersharifst.com/2024/09/14/nifdv/)。

## 25. DRG [Engine] (竜騎士をエンジンに載せた)

### 25.1 変更内容

- `Custom/Engine/Jobs/DrgDefinition.cs`: 竜騎士のジョブ定義 (データのみ、`Build(gcd, level)`)。威力・仕様は xan_timeline_harness の
  `DrgCombatState` / `DrgPotencyScorer` (l76 / l94 の分岐、Drakesbane 86、Geirskogul / Nastrond 90、Jump 54) に合わせた。
  - 2 本のコンボ: True / Raiden Thrust → Vorpal Thrust (96 で Lance Barrage) → Heavens' Thrust (86、それ未満は Full Thrust) → Fang and Claw →
    Drakesbane、→ Disembowel (96 で Spiral Blow) → Chaotic Spring (86、それ未満は Chaos Thrust) → Wheeling Thrust → Drakesbane。置き換わる技は
    スキル名を変えずに ActionId と威力だけ替えた (§17 と同じ)。Raiden Thrust (76) と Draconian Fury (82) は Draconian Fire を要るので別スキル。
    範囲コンボ Doom Spike / Draconian Fury → Sonic Thrust → Coerthan Torment (10y 直線、全対象に同じ威力)。
  - ゲームが拒否する段 (コンボ外の Heavens' Thrust / Fang and Claw / Chaotic Spring / Wheeling Thrust / Coerthan Torment) は `RequiresCombo`。
    Drakesbane は Fang and Claw と Wheeling Thrust のどちらからも繋がるので、平ゲージ `FourthStep` (4 段目で 1、他の WS で 0。モジュールは
    コンボ状態から作る) を条件にした (定義の条件に「コンボが A または B」の種類がないため。VPR の `Step` も同じ)。
  - Power Surge 30 秒 ×1.10 (upkeep)、Lance Charge 20 秒 ×1.10、Battle Litany 20 秒 = クリ率 +10% をスコアラーの期待値モデルで
    全技 ×1.0522、`PartyValue` 1,514 (ハーネスのパーティ 1,450 威力/秒 × 20 秒 × 0.0522)、Life of the Dragon 20 秒 ×1.15 (70 未満は
    Blood of the Dragon ×1.10)、Chaotic Spring は DoT (24 秒、45 / 3 秒。86 未満は 40) で「新たに覆う秒数」だけを押した時点で計上
    (ハーネスと同じ)。
  - Life Surge = 次の WS が確定クリティカル: スコアラーの比 1.3913 (= 1.60 / (1 + 0.25 × 0.60)) を Heavens' Thrust / Drakesbane / Coerthan
    Torment の条件付き威力にし、全 WS が Life Surge を消す。ガイドの「対象はヘヴンスラストか雲蒸竜変 (3 体以上は Coerthan Torment) だけ」を
    Life Surge の 3 変種 (`LifeSurge` = コンボが Vorpal、`LifeSurgeDrakesbane` = FourthStep、`LifeSurgeAoe` = コンボが Sonic Thrust、
    3 体以上) の条件で表した (アビリティの `RequiresCombo` はコンボを継続させてしまうので `ComboNeutral` を重ねる)。リタニー中の比
    (1.4016) との差は 0.7% で無視した。2 チャージは 88 から。
  - High Jump 30 秒 (74 未満は Jump、68 から Dive Ready 15 秒) → Mirage Dive、Geirskogul 60 秒 → LotD + Nastrond Ready、Stardiver 30 秒
    (ロック 1.5 秒、LotD 中のみ) → Starcross Ready (100、LotD 中のみ)、Dragonfire Dive 120 秒 (92 から Dragon's Flight) → Rise of the
    Dragon、Wyrmwind Thrust 10 秒 (Focus 2 消費。Focus は Raiden Thrust / Draconian Fury で +1、90 から)、Drakesbane / Coerthan Torment
    (82) の後に Draconian Fire 30 秒。
  - `FillerPotency`: コンボ 2 本の平均威力 (Lv100 で 313。自動計測は Draconian Fire を要る Raiden Thrust で止まり 234 になっていた)。
- `Custom/Engine/Jobs/DrgEngineModule.cs`: 「DRG [Engine]」(WIP、グループ Engine、xan DRG [Custom] の Strategy)。登録レベル 30
  (True Thrust → Vorpal → Full Thrust と Disembowel の Power Surge、Lance Charge、Jump が揃う。50 で Chaos Thrust、64 で Drakesbane)。
  - 状態: DragoonGauge (Firstminds' Focus、LotD タイマー → LifeOfTheDragon ステータス)、SID (Power Surge / Lance Charge / Battle Litany /
    Life Surge / Nastrond Ready / Dive Ready / Draconian Fire / Dragon's Flight / Starcross Ready、対象の Chaotic Spring または Chaos Thrust)、
    リキャスト (Life Surge / Lance Charge / Litany / Jump または High Jump / Geirskogul / Dragonfire Dive / Stardiver / Wyrmwind)、コンボ。
  - トラック: Buffs (Battle Litany の Delay / Force)、LC (Lance Charge)、Dive (NoMove = Dragonfire Dive と Stardiver を禁止、NoLock = High
    Jump も)、HJMD (AfterBuffs = Lance Charge が使える間は High Jump を禁止、Power Surge なしで Mirage Dive を禁止; HoldMD = Mirage Dive は
    Lance Charge 中か Dive Ready が切れる前だけ; Delay / Force)、Talon (モジュールが Piercing Talon を押す: 射程外 (3 超 20 以内) で
    Automatic、Enhanced があるとき)、HoldGCD (Delay = 全 GCD 禁止)、Filler (ForceTT = 優先度 pointless の対象にはコンボ 1 段目だけ)、
    Iainuki / Zeninage (xan と同じ条件でモジュールが押す)、MechanicHints、Targeting、AOE。薬のトラックは xan DRG にないので薬は使わない。
  - 2 本のコンボの交互: Power Surge が 10 秒以上残り、かつ DoT が 3 GCD 以上残っているときは Disembowel を Forbid (xan の「Power Surge <
    10 で Disembowel」と同じ。ガイドは「64 から 2 本を交互に」)。これがないと探索は DoT の新規秒数と Power Surge の残り価値で毎回
    Disembowel 側を選び、Heavens' Thrust 側を使わなかった (4 GCD の地平線では「残り 17 秒のバフを掛け直す損」が見えない)。
  - カウントダウン開幕: True Thrust が着弾遅延 0.76 秒でプルに乗るように押す、射程外なら 0.7 秒前に Winged Glide (xan と同じ)。
  - 位置取り: Chaotic Spring / Wheeling Thrust は背面、Fang and Claw は側面。True North は外れそうなときに最低優先度で (xan DRG に
    True North のトラックはない)。対象数はコンボと同じ 10y 直線。
  - ライブのフレーム予算 0.5 ms (他ジョブは 0.03〜0.08) と MinNodes 5000 / SliceNodes 400: 8 秒ごとの再計画がフレーム予算を使い切ると探索が
    次のフレームに回り、ライブが決定論から離れた (300 秒単体: フレーム 0.05 ms で 95,537、0.5 ms で 96,542 = 決定論)。SliceNodes 40 (MNK) だと
    探索が多くのフレームにまたがって根が古くなり 95,703 だった。
- 重み: MNK の Lv100 セットから CMA-ES (`tune-xan --def drg --job drg-engine --args "timeline-matrix --scenario-limit 8 --party-buffs 7.8"
  --gens 14 --pop 12`、params は MNK の 9 個 + `CooldownValue.LanceChargeCD:0:2000,CooldownValue.LifeSurgeCD:0:400`)。620,595 → 634,072
  (LanceChargeCD の価値 700 が付き、Lance Charge が窓の頭に来る)。結果は `tools/blm_engine_eval/tuned/weights-DRG-v1.json` と
  `DrgDefinition.DefaultWeightsJson`。調整後に入れた DoT 条件 (上の Disembowel の Forbid) で決定論は 630,497 → 633,940 (バフなし) /
  634,072 → 632,547 (バフあり)。
- tools: xan_timeline_harness に `drg-engine` (ENGINE_WEIGHTS / ENGINE_FRAME_MS の上書きも)、blm_engine_eval に `--def drg`。

### 25.2 ジョブ別ルール (Lv100 未満、`SyncedOgcd` / `SyncedGcd`)

出典: The Balance DRG Leveling Guide (1-17 / 18-25 / 26-49 / 50-55 / 56-57 / 58-63 / 64-100: 「Power Surge を切らさない」「26 から Life Surge
は Full Thrust、64 から Drakesbane にも、範囲は Sonic Thrust / Coerthan Torment」「64 から 2 本を交互に」「アビリティとバフはリキャストごと」)、
ersharifst 7.3 Lv100 (ランスチャージ → リタニー → ゲイルスケグルを同じ窓に、ライフサージはヘヴンスラストと雲蒸竜変だけ、スターダイバーは
単独の枠、天竜点睛は 2 スタックで竜眼雷電の前に)、xan DRG.cs のウィーブ規則。

- oGCD: Lance Charge は Power Surge が付いてからリキャストごと (18 未満は Power Surge がないので即)。Battle Litany と Geirskogul は Lance
  Charge 中 (Lance Charge を使わない設定、または 20 秒以上先なら制限なし)。Wyrmwind Thrust は Focus 2 で LotD 中、または次の GCD が
  Raiden Thrust / Draconian Fury (Draconian Fire があってコンボが空) のとき。High Jump / Mirage Dive はトラックの条件で。Life Surge は
  定義の条件 (Heavens' Thrust / Drakesbane / Coerthan Torment の直前) で Lance Charge 中、またはチャージが満タンになる 1 GCD 前。
  Dragonfire Dive は Lance Charge 中 (同上)。Nastrond、Stardiver、Starcross、Rise of the Dragon は準備完了で即。
- GCD: 3 体以上は Doom Spike / Draconian Fury → Sonic Thrust → Coerthan Torment (62 未満は Power Surge が切れる前に Disembowel のコンボ)。
  単体は始めたコンボを仕上げ (5 → 4 → 3 → 2 段目)、2 段目は Power Surge が 10 秒未満か DoT が 3 GCD 未満なら Disembowel、それ以外は
  Vorpal Thrust。1 段目は Draconian Fire があれば Raiden Thrust。Piercing Talon はモジュール。
- [DERIVED]: Litany / Geirskogul / Dragonfire Dive を Lance Charge 中に揃える (ガイドは「リキャストごと」、ersharifst は同じ窓)、Life Surge の
  「チャージが満タンになる前」、DoT 3 GCD の閾値、Lance Charge 20 秒の逃げ。

### 25.3 検証

- Release ビルド 0 エラー (BossModReborn、xan_timeline_harness、blm_engine_eval、engine_tests 17/17、rpr_engine_eval、engine_bench、
  engine_tuner、blm / mnk / nin / sam / rpr / drg_regression。vpr_regression は変更前から upstream の xan VPR を参照していてビルドが通らない)。
- 他 7 ジョブの Lv100 決定論 (9 戦闘、ENGINE_FRAME_MS=1000、BudgetMs 100): バフなし・`--party-buffs 7.8` とも出力行が変更前 (81f2fb4fd の
  ワークツリー) と完全一致 (exec_ms を除く。RPR 666,657 / 689,735、NIN 650,308 / 652,226、MNK 578,459 / 589,056、SAM 672,188 / 673,349、
  BLM 560,467 / 560,364、GNB 566,864 / 578,686、PLD 553,005 / 560,806)。Core / Adapter は変更していない。
- Lv100 (決定論 = 9 戦闘 ENGINE_FRAME_MS=1000 + BudgetMs 100、ライブ = 組み込み (0.8 ms、フレーム 0.5 ms) を 2 回、(b) = `--party-buffs 7.8`、
  300 秒 = `event-timeline --duration 300`。xan DRG はパーティバフの有無で同じ出力):

| DRG Lv100 | 決定論 | 決定論 (b) | ライブ | ライブ (b) | 300 秒 | Execute 平均 / p99 µs / 1 ms 超 |
|---|---:|---:|---:|---:|---:|---:|
| 旧 xan DRG [Custom] | 630,336 (rdps 658,077) | 630,336 | 630,336 | 630,336 | 98,872 (rdps 103,403) | 35.1 / 72 / 15 |
| 調整前 (MNK の重み) | 619,826 | 620,595 | | | 96,684 | |
| DRG [Engine] | **633,940** (rdps 662,173) | **632,547** (rdps 660,784) | 634,249 / 634,249 | 631,855 / 631,855 | 96,542 (rdps 101,070) (ライブも 96,542) | 51.2 / 587 / 56 |

  - 受け入れ基準 (9 戦闘 `--party-buffs 7.8` で旧版以上) は決定論で満たす (+0.4%、バフなし +0.6%)。ライブも 2 回とも同じ値で旧版以上 (バフなし +0.6%、バフあり +0.2%)。300 秒単体は
    −2.4% (旧版未満)。rdps (party_litany 込み) も同じ向き。
  - 9 戦闘のカウンター (決定論 / (b) / 旧): no_surge (Power Surge なしの WS) 8 / 4 / 5、dot_gap 109.6 / 94.6 / 76.7 秒、procs_lost 6 / 6 / 7、
    ls_lost 1 / 0 / 0、Geirskogul 34、Stardiver 34、GCD 696 / oGCD 503 (旧 489)。短い 8 戦闘はすべて [Engine] が上 (例 60 秒 21,622 /
    旧 21,429、27.5 秒 10,953 / 10,307)、長い z1363 は下 (約 −1%)。
  - 300 秒単体のトレース (決定論) の開幕: True Thrust → [Geirskogul] → [Litany] → Spiral Blow → [Lance Charge] → [High Jump] → Chaotic Spring →
    [Stardiver] → Wheeling Thrust → [Dragonfire Dive] → [Life Surge] → Drakesbane → [Mirage Dive] → [Nastrond] → Raiden Thrust → [Rise of the
    Dragon] → [Starcross] → Lance Barrage → [Life Surge] → Heavens' Thrust → Fang and Claw → Drakesbane。ersharifst の画像 (True Thrust →
    Lance Barrage → Heavens' Thrust → [薬] → [Lance Charge] → [Litany] → [High Jump] → [Geirskogul] → Fang and Claw → [Mirage Dive] → [Life Surge]
    → Drakesbane → [Dragonfire Dive] → [Rise of the Dragon] → Raiden Thrust → [Nastrond] → [Stardiver] → [Starcross] → [Life Surge] → Spiral Blow
    → Chaotic Spring → ...) とは構造が違う: エンジンは Disembowel 側のコンボから入り (DoT を先に)、Geirskogul / Litany を最初のウィーブ、
    Lance Charge を 2 つ目のウィーブに置く (Lance Charge の遅れ 2.5 秒)。Life Surge は Drakesbane と Heavens' Thrust の直前、Stardiver は
    単独の枠、Wyrmwind は Focus 2 で LotD 中 (25.65 秒) で、本文の規則は再現している。開幕は固定していない。
- シンクレベル (9 戦闘 `--level L`、既定トラック。ルールは探索をしないので決定論 = ライブ。旧 = xan DRG [Custom]):

| DRG | ルール | 旧 | 旧比 | 失敗 ルール / 旧 | no_surge / dot_gap s / procs_lost / ls_lost (ルール ／ 旧) |
|---|---:|---:|---:|---|---|
| Lv90 | 515,153 | 512,626 | 0.5% | 0 / 0 | 5 / 95.2 / 3 / 0 ／ 5 / 76.7 / 3 / 0 |
| Lv80 | 451,086 | 448,296 | 0.6% | 0 / 0 | 5 / 95.2 / 3 / 0 ／ 5 / 76.7 / 3 / 0 |
| Lv70 | 383,068 | 380,059 | 0.8% | 0 / 0 | 5 / 95.2 / 0 / 0 ／ 5 / 76.7 / 0 / 0 |
| Lv60 | 296,242 | 289,262 | 2.4% | 0 / 0 | 3 / 33.5 / 0 / 0 ／ 37 / 246.4 / 0 / 0 |
| Lv50 | 265,490 | 263,463 | 0.8% | 0 / 0 | 3 / 18.5 / 0 / 3 ／ 3 / 43.6 / 0 / 3 |

  - no_surge はダウンタイム明けに Power Surge が切れたままコンボを仕上げる分 (旧版と同数)。dot_gap の差 (90〜70) は DoT 3 GCD の閾値で
    Disembowel 側が 1 GCD 遅れる分。Lv60 (Drakesbane なし、4 段コンボ) は旧版が Power Surge を 37 回落とすのに対しルールは 3 回。
- 全レベル × 全トラックのスモーク (登録レベル 30〜100 の全レベル × 全トラックを k 番目の選択肢に揃えた組、奇数 k はカウントダウン 12 秒、
  偶数 k は追加の敵 3 体、`--scenario-limit 2`): 213 回 (k = 1〜3) で例外 0。ハーネスの規則チェックの失敗は全 213 回 (合計 1,278) で、すべて「目標復帰後 3.5 秒以内に GCD がない」: 2 択のトラック HoldGCD が k ≥ 1 で必ず Delay (全 GCD 禁止) になる組で、同じ設定の xan DRG も同じ 3 件 / 戦闘を出す (わざと不自然な設定)。
- drg_regression は自前のルールシミュレーター (DrgSimulator) で、モジュールを駆動する口がないのでエンジンは測れない。

### 25.4 入れていないもの

- 薬 (xan DRG にトラックがない)、Elusive Jump + Enhanced Piercing Talon の組 (ersharifst)、2 体のときの DoT 配り (xan の 2 体規則)、
  HP ロック対象のときの Heavens' Thrust 優先 (ersharifst の「3 GCD 以内に倒せるなら順番を無視」)。
- 開幕の固定 (カウントダウンの True Thrust / Winged Glide だけ)。ゲーム内での確認。

出典: [The Balance DRG Leveling Guide](https://www.thebalanceffxiv.com/jobs/melee/dragoon/leveling-guide/)、
[ersharifst 7.3 Lv100 DRG ガイド](https://www.ersharifst.com/2025/03/21/fdgssgt/)、xan DRG.cs (Custom/Rotation/Standard/xan/Melee/DRG.cs)。

## 26. VPR [Engine] (ヴァイパーをエンジンに載せた)

### 26.1 変更内容

- `Custom/Engine/Jobs/VprDefinition.cs`: ヴァイパーのジョブ定義 (データのみ、`Build(gcd, level)`)。威力・仕様は xan_timeline_harness の
  `VprCombatState` / `VprPotencyScorer` (Melee Mastery 74 / 84、Rattling Coil 82 (2) / 88 (3)、Offering 90、双牙の毒 75 / 80、Death Rattle 55、
  Last Lash 60、Uncoiled の双牙 92、Ouroboros 96、Legacy 100、減衰 0.25) に合わせた。
  - 基本コンボ: Steel / Reaving Fangs (もう一方の Honed で +100、使った Honed を消してもう一方を付与。10 から) → Hunter's Sting (Hunter's
    Instinct 40 秒 ×1.10) / Swiftskin's Sting (Swiftscaled 40 秒、WS のリキャスト ×0.85) → Flanksting / Flanksbane / Hindsting / Hindsbane
    (方向指定込み 400、持っている毒 (Flankstung / Flanksbane / Hindstung / Hindsbane) が合えば +100、次の毒を付与、Death Rattle、Offering +10)。
    範囲は Steel / Reaving Maw (Honed +20) → Hunter's / Swiftskin's Bite → Jagged / Bloodied Maw (Grim の毒 +40、Last Lash)。
  - コンボの段は平ゲージ `Step` (1 Fangs、2 Hunter's Sting、3 Swiftskin's Sting、4 Maw、5 / 6 Bite。他の WS で 0、モジュールはクライアントの
    コンボ状態から作る): 2 段目は Steel / Reaving のどちらからも繋がり、仕上げはコンボ外ではゲームが拒否するので、段を条件にした
    (`ComboFrom` は葉のコンボ価値のために残した)。4 種の毒 (同時に 1 つ) は平ゲージ `Venom` (1〜4)、Grim の毒は `GrimVenom`。
  - 蛇尾術 (Death Rattle / Last Lash / 4 つの Legacy = 1 スキル `Legacy`、モジュールがゲージで押し分け) は平ゲージ `Tail`、双牙の窓 (coil の後
    / den の後 / Uncoiled Fury の後、どちらの側で開いたか) は `TwinWindow` (1〜5) と `TwinfangReady` / `TwinbloodReady`。どの WS も
    これらを落とす (ハーネスは失われた追撃として数える)。
  - 双牙は窓ごとに組で定義: Hunter's Coil の後は Twinfang (Hunter's Venom +50、Swiftskin's Venom を付与) → Twinblood、Swiftskin's Coil の後は
    Twinblood → Twinfang (den も同様。モジュールは同じ 2 アクションを押す)。ウィーブ窓は定義順に探索される (後ろの番号 → 前の番号の順は
    探索されない) ので、1 組の定義では Swiftskin's Coil の後も Twinfang が先になり、付与された Hunter's Venom が 35 回失効していた。
  - Vicewinder (40 秒 2 チャージ、Rattling Coil +1) は `HunterCoilOk` / `SwiftCoilOk` を両方付与し、Hunter's / Swiftskin's Coil (3.0 秒、
    方向指定込み 680、自分のバフを 40 秒、双牙の窓、Offering +5) がそれぞれ自分の分を消す。Vicepit → den も同じ。鎖の途中では基本コンボ・
    Reawaken・Uncoiled Fury を禁止 (ガイドは鎖を必ず仕上げる)。
  - Uncoiled Fury (3.5 秒、射程 20、Coil 1、92 から Poised + Uncoiled Twinfang → Twinblood)。Serpent's Ire 120 秒 (Coil +1、90 から Ready to
    Reawaken 30 秒)。Reawaken (2.2 秒、Offering 50 または Ready = 2 スキル `Reawaken` / `ReawakenReady`、Anguine を 5 (96 未満 4) に) →
    Generation 1〜4 (2.0 秒、連続威力 680、Anguine で段を縛る、100 で Legacy) → Ouroboros (96、3.0 秒、Reawakened を終える)。Reawakened 中は
    基本コンボ・鎖・Uncoiled Fury を禁止 (xan は射程外で Uncoiled Fury を挟むが入れていない)。
  - 長いリキャストは基本 GCD に比例 (coil 3.0 × gcd / 2.5 など)。Swiftscaled はステータスの `gcdRecastMultiplier` 0.85 で、モジュールは
    ヘイストなしの GCD を渡す (SAM と同じ)。`FillerPotency`: 基本コンボの平均 (Lv100 で 460 = (300 + 300 + 500 + Death Rattle 280) / 3。
    自動計測は Step ゲージを要る仕上げを回せず 296 になっていた)。
- `Custom/Engine/Jobs/VprEngineModule.cs`: 「VPR [Engine]」(WIP、グループ Engine、xan VPR [Custom] の Strategy)。登録レベル 30
  (4 つの仕上げと毒が揃う。65 で Vicewinder、90 で Reawaken)。
  - 状態: ViperGauge (Offering、Rattling Coil、Anguine、DreadCombo → Ok ステータス、SerpentCombo → Tail / TwinWindow、下位 2 ビットの残り
    回数と毒から双牙の Ready)、SID (Hunter's Instinct / Swiftscaled / Honed / 4 つの毒 → Venom / Grim → GrimVenom / Hunter's・Swiftskin's・
    Fellhunter's・Fellskin's Venom / Poised / Ready to Reawaken / Reawakened)、リキャスト (Vicewinder、Serpent's Ire)、コンボ → Step。
  - トラック: Buffs (Reawaken の Delay / Force)、SerpentsIre (Off / Force)、Potion (OpenerAndEven = Ready to Reawaken 中、または Serpent's Ire
    直後の Reawaken 中; EvenOnly は 60 秒以降)、UncoiledFuryRange (Auto = 射程外 (3 超 20 以内) で Uncoiled Fury を Force)、Snap (モジュールが
    射程外で Writhing Snap: Coil がある (Auto) ときは Uncoiled Fury に任せる、Reawaken 中は押さない)、Slither (Opener = カウントダウンの
    0.45 秒前に射程外なら; OpenerAndBurstRecovery = 戦闘中も Ready to Reawaken / Reawakened で射程外なら)、OpenerBurst (Patch75ZeroSecond =
    カウントダウンの最初の GCD が Vicewinder。それ以降の並びはエンジン)、TrueNorth、MechanicHints、Targeting、AOE。
  - Reawaken は Hunter's Instinct / Swiftscaled がシーケンス (2.2 + 4 × 2.0 (+ 3.0) 秒 × GCD / 2.5 × ヘイスト) より短いと Forbid (xan と同じ)。
    2 段目は毒の側 (Flank 系なら Hunter's、Hind 系なら Swiftskin's、毒がなければ残りの短いバフ) を Forbid で選ぶ (xan の
    SelectSingleSecondComboGCD。探索は Swiftscaled に価値を持たないので、2 本のバフを落とさないため)。
  - カウントダウン開幕: Steel Fangs (Patch75ZeroSecond は Vicewinder) を 1.16 秒前に、射程外なら Slither。位置取り: Flanksting / Flanksbane /
    Hunter's Coil は側面、Hindsting / Hindsbane / Swiftskin's Coil は背面、True North は追撃より後ろの優先度。対象数は自分中心 5y。
- 重み: NIN の Lv100 セットから CMA-ES (`tune-xan --def vpr --job vpr-engine --args "timeline-matrix --scenario-limit 8 --party-buffs 7.8"
  --gens 14 --pop 12`、params は NIN の 9 個 + `CooldownValue.SerpentsIreCD:0:2000,StatusValue.Swiftscaled:0:60`)。698,114 → 707,192
  (Swiftscaled の秒価値 39 が付く)。2 回目 (v1 から同じ設定) は 710,291 → 710,944 (+0.09%) で採用していない。結果は `tools/blm_engine_eval/tuned/weights-VPR-v1.json` と `VprDefinition.DefaultWeightsJson`
  (BudgetMs 0.8、MinNodes 2000 / SliceNodes 40: 9 戦闘の最大探索 1,553 ノード。ライブは決定論と一致)。
- tools: xan_timeline_harness に `vpr-engine`、blm_engine_eval に `--def vpr`。

### 26.2 ジョブ別ルール (Lv100 未満、`SyncedOgcd` / `SyncedGcd`)

出典: The Balance VPR Leveling Guide (全レベル: Steel と Reaving を交互に、Hunter's Instinct と Swiftscaled を常時維持、Vicewinder は
リキャストごと (2 分窓の直前を除く)、Rattling Coil はコンボの間でバフを落とさずに; 70-73 / 74-81: 偶数分の窓は Vicewinder 2 組、82 から
Coil を窓まで温存; 90-91: 偶数分は Serpent's Ire → フィラー 2 → Reawaken 2 組 → Uncoiled Fury、窓の間に Reawaken 1 回; 3 体以上は範囲
コンボ)、ersharifst 7.5 (偶数分は Offering 50 を貯めて蛇の霊気 → 祖霊降ろし 2 セット、飛蛇の魂 2 以下で壱の蛇、蛇尾術と双牙は光ったら即、
猛襲・疾速を切らさない)、xan VPR.cs。

- oGCD: 蛇尾術 (Legacy / Death Rattle / Last Lash)、双牙 (窓ごとの組の順: Uncoiled → Thresh → Bite) を最優先。Serpent's Ire は Coil が上限
  未満のときリキャストごと (90 からは Offering 50 以上、Reawaken 中、または直後の Reawaken が済んでいるとき)。
- GCD: Reawaken 中は Generation 1〜4 → Ouroboros。鎖 (coil / den) は必ず仕上げ、残りの短いバフの側から。Reawaken は Ready で即、または
  Offering 100、Serpent's Ire 直後 30 秒、または Offering 50 以上で「今使っても Serpent's Ire までに仕上げ (3 GCD に 10) で 50 に戻る」
  とき (Hunter's Instinct / Swiftscaled がシーケンス分残っているときだけ)。Uncoiled Fury は Coil が上限で次の Coil (Vicewinder のチャージ、
  2 GCD 以内の Serpent's Ire) が来るとき、またはレイドバフ中で Reawaken できないとき (バフが 3 GCD + 0.5 秒以上残るときだけ)。Vicewinder /
  Vicepit (3 体以上) はチャージがあればリキャストごと、Serpent's Ire が 10 秒以内で Offering 50 以上なら 1 チャージを窓に残す、コンボが
  3 GCD 以内に切れるときは先に仕上げる。範囲は Maw → Bite (残りの短いバフ) → Grim の毒の側の Maw、単体は Fangs (Honed の側) → Sting (毒の
  側、なければ残りの短いバフ) → 毒の側の仕上げ。Writhing Snap はモジュール。
- [DERIVED]: Serpent's Ire までの Offering の見込み (3 GCD に 10)、Uncoiled Fury のレイドバフ中の条件とバフ 3 GCD、Vicewinder の 10 秒
  (xan HoldViceBeforeSerpentsIre)、コンボ 3 GCD。

### 26.3 検証

- Release ビルド 0 エラー、engine_tests 17/17 (§25.3 と同じ組)。他 7 ジョブの Lv100 決定論の出力行は変更前と完全一致 (§25.3)。
- Lv100 (決定論 = 9 戦闘 ENGINE_FRAME_MS=1000 + BudgetMs 100、ライブ = 組み込み (0.8 ms) を 2 回、(b) = `--party-buffs 7.8`、300 秒 =
  `event-timeline --duration 300`):

| VPR Lv100 | 決定論 | 決定論 (b) | ライブ | ライブ (b) | 300 秒 | Execute 平均 / p99 µs / 1 ms 超 |
|---|---:|---:|---:|---:|---:|---:|
| 旧 xan VPR [Custom] | 700,312 | 698,736 | 700,312 | 698,736 | 113,655 | 49.2 / 109 / 6 |
| 調整前 (NIN の重み、双牙 1 組) | 702,223 | 698,115 | | | 112,718 | |
| 調整後 (双牙 1 組) | 702,223 | 707,192 | 702,223 | 707,192 | 113,548 | 38.8〜40.0 / 137〜143 / 14〜16 |
| VPR [Engine] (双牙 2 組) | **710,169** | **710,291** | 710,169 / 710,169 | 710,291 / 710,291 | 114,038 (ライブも 114,038) | 39.5〜40.8 / 138〜141 / 11〜12 |

  - 受け入れ基準 (9 戦闘 `--party-buffs 7.8` で旧版以上) を満たす (+1.7%、バフなし +1.4%)。300 秒単体も旧版以上 (+0.3%)。
    ライブは MinNodes / SliceNodes で決定論と一致する。
  - 9 戦闘のカウンター (決定論 / (b) / 旧): procs_lost 4 / 4 / 3、追撃失効 1 / 1 / 2、no_instinct 6 / 4 / 21、Reawaken 47 / 47 / 46、Generation 171 / 171 / 175、Uncoiled Fury 62 / 62 / 55 (旧 (b) 61)、coil 114 / 114 / 107、GCD 797 / oGCD 680 (旧 799 / 656)。
  - 300 秒単体のトレース (決定論) の開幕: Vicewinder → [Serpent's Ire] → Swiftskin's Coil → [Twinblood] → [Twinfang] → Hunter's Coil → [Twinfang]
    → [Twinblood] → Vicewinder → Hunter's Coil → ... → Swiftskin's Coil → ... → Reawaken (15.3 秒) → Generation 1〜4 (+ Legacy) → Ouroboros →
    Uncoiled Fury × 3 (+ 双牙)。ersharifst の画像 (コンボ 1 段目 → 蛇の霊気 → 飛蛇の牙 → 猛襲蛇牙 → [薬] → 疾速蛇牙 → 祖霊降ろし → ...) に対し
    エンジンは 2 つ目の Vicewinder の鎖を先に入れてから Reawaken する (レイドバフ窓 7.8〜27.8 秒の中)。薬込みの 3 セット (飛蛇の牙で挟む)
    は 1 分あたりの Offering では成立せず、エンジンは偶数分に Ready の 1 回 + Offering の 1 回 (ersharifst の「2 セット」) を使う。
    xan の 300 秒 (Vicewinder → Ire → Hunter's Coil → 双牙 → Swiftskin's Coil → 双牙 → Reawaken (8.5 秒)) とも Reawaken の位置が違う。
- シンクレベル (9 戦闘 `--level L`、既定トラック。ルールは探索をしないので決定論 = ライブ。旧 = xan VPR [Custom]):

| VPR | ルール | 旧 | 旧比 | 失敗 ルール / 旧 | Offering 溢れ / Coil 溢れ / procs_lost / 追撃失効 / no_instinct / Reawaken / UF / coil (ルール ／ 旧) |
|---|---:|---:|---:|---|---|
| Lv90 | 566,058 | 573,609 | -1.3% | 0 / 0 | 0 / 0 / 0 / 0 / 2 / 45 / 62 / 105 ／ 0 / 1 / 3 / 0 / 3 / 48 / 56 / 108 |
| Lv80 | 462,870 | 462,870 | 0.0% | 0 / 0 | 0 / 0 / 0 / 0 / 2 / 0 / 0 / 112 ／ 同じ |
| Lv70 | 410,618 | 410,618 | 0.0% | 0 / 0 | 0 / 0 / 0 / 0 / 2 / 0 / 0 / 112 ／ 同じ |
| Lv60 | 394,168 | 394,168 | 0.0% | 0 / 0 | 0 / 0 / 0 / 0 / 2 / 0 / 0 / 0 ／ 同じ |
| Lv50 | 311,260 | 311,260 | 0.0% | 0 / 0 | 0 / 0 / 0 / 0 / 2 / 0 / 0 / 0 ／ 同じ |

  - 80 以下はルールと旧版の出力が同じ (Vicewinder の鎖と毒のコンボだけで、選択の余地がない)。Lv90 は Reawaken が 3 回少なく Uncoiled Fury
    が 6 回多い (旧版は Ready to Reawaken の失効 3 と Coil 溢れ 1、ルールは 0)。Serpent's Ire までの Offering の見込み (上の [DERIVED]) を
    xan の予測に近づけても回数は変わらなかった (Reawaken 中のバフ条件と Serpent's Ire 直後の優先のほうが効く)。
- 全レベル × 全トラックのスモーク (登録レベル 30〜100、§25.3 と同じ組): 213 回 (k = 1〜3) で例外 0、規則チェックの失敗 0。
- vpr_regression は upstream の `BossMod.Autorotation.xan.VPR` の private フィールドをリフレクションで読む (Custom の xan VPR とも違う) ので、
  エンジンは駆動できない (変更前からビルドも通らない)。

### 26.4 入れていないもの

- Reawaken 中の射程外 Uncoiled Fury (xan)、鎖の途中の Uncoiled Fury、薬込みの 3 セット (ersharifst)、Patch75ZeroSecond のカウントダウン後の
  固定並び (Vicewinder → Swiftskin's Coil → Hunter's Coil を優先 25 で押す xan の処理。エンジンの探索に任せた)。
- Hunter's / Swiftskin's Venom などの 30 秒失効の価値付け (探索は追撃の威力でしか見ない)。ゲーム内での確認。

出典: [The Balance VPR Leveling Guide](https://www.thebalanceffxiv.com/jobs/melee/viper/leveling-guide/)、
[ersharifst 7.5 Lv100 VPR ガイド](https://www.ersharifst.com/2024/10/12/hdfbgdf/)、xan VPR.cs (Custom/Rotation/Standard/xan/Melee/VPR.cs)。

## 27. 参考サイトの回し画像との突き合わせ (SAM / GNB / NIN / RPR / MNK / BLM)

ersharifst の各ジョブ記事の「Sequence」画像 (アイコン列) を読み取ったもの (scratchpad の guide-images-transcription.md。`?` は判別に自信のない
アイコン、[ ] はアビリティ) と、HEAD (db0a6a728) の [Engine] 6 ジョブ・旧モジュールの 300 秒単体 (`event-timeline --duration 300`、決定論、
カウントダウンなし) のトレースを並べ、構造の違い (バーストの oGCD の並び、薬の位置、保持 / 落とす資源、余分・不足の技) を拾った。
`?` のアイコンに依存する差は数えていない。画像の構造と本文の規則は一致していた (§20〜24 の本文由来の規則はそのまま)。

### 27.1 画像との突き合わせ

- SAM (【7.4】Lv100侍)。画像の標準開幕: [明鏡止水] → 月光 → [意気衝天] → 花車 → 雪風 → 震天 → [薬] → 天道雪月花 → [閃影] → 雪風? → 残心 →
  奥義斬浪 → 返し天道雪月花 → 暁風? → [照波] → 返し斬浪 → 震天 → 雪風 → [明鏡止水] → 月光 → 花車 → 雪月花 → 返し雪月花。偶数分: … → 雪月花
  (返しを残す) → … → [明鏡止水] → 月光 → 花車 → 返し雪月花 → 天道雪月花 → 月光 → 彼岸花 → 返し天道雪月花 → 奥義斬浪 → 返し斬浪。
  エンジン (HEAD、300 秒): 陣風 → [明鏡止水] → 花車 → 雪風 → [意気衝天] → 月光 → 天道雪月花 → [明鏡止水] → 返し天道 → 奥義斬浪 → 返し斬浪 →
  [残心] → 月光 → 雪風 → [閃影] → 花車 → 天道雪月花 → 返し天道 → [照波] → …。2 分目: 天道雪月花 → 返し天道 (116〜117 秒) → 花車 → 月光 →
  陣風 → 雪風 → [意気衝天] (126.7) → 奥義斬浪 → 返し斬浪 → 雪月花 → 返し → [残心] → … → 彼岸花 → [閃影] (144.8)。旧 xan も天道の組は
  114.6〜115.7 秒で、[意気衝天] は 125.8 秒。
  - 差 1 (実質的): 明鏡止水をリキャストごと (55 秒: 0.65 / 10.7 / 56 / 111 / 166 / 221 / 276 秒) に押すので 1 分あたり 5 秒ずれ、2 分目以降の
    天道雪月花 + 返し天道 (約 3,060) はレイドバフ窓 (127.8〜147.8 秒など) の外に出る (116 / 173 / 226 / 285 秒)。画像は明鏡止水をバースト頭と
    奇数分の 2 回 (60 秒周期) に置き、天道の組をバーストの中に入れる。
  - 差 2 (実質的): カウントダウン開幕 (9 戦闘の短い戦闘) で、−14 秒の明鏡止水の天道を使う前 (4.95 秒) に 2 つ目の明鏡止水を押し、天道 (30 秒、
    スタックなし) を上書きして天道の組を 1 つ失う (60 秒戦闘で天道雪月花 1 回)。画像の 2 つ目の明鏡止水はどちらの開幕でも天道雪月花の後。
  - 差 3 (同等): 返し雪月花はエンジンも xan も直後に使う (画像は偶数分の頭まで保持)。30 秒で失う賭けなので §20.5 のとおり変えない (Hold
    トラックは従来どおり)。奇数分の彼岸花更新 (明鏡止水 → 月光 → 彼岸花) は §23.3 で測定済み (下がる) なので再試行していない。
- GNB (【7.3】/ 7.4 調整後)。画像 (7.4): ビートファング (準備、2 チャージ寸前) → サベッジクロウ → ウィケッドタロン → [アイガウジ ＋ ノーマーシー]
  → ビートファング → [ジャギュラーリップ ＋ ブラッドソイル] → [ブラスティングゾーン] → サベッジクロウ → [アブドメンテアー] → ウィケッドタロン →
  [アイガウジ] → ダブルダウン → ソニックブレイク → レインオブビースト → ノーブルブラッド → ライオンハート (弾 4 発、コンボ途中でバーストに入らない)。
  エンジン (HEAD、2 分目): キーンエッジ → [ブラッドソイル] → [ノーマーシー] (121.4) → ソニックブレイク → [ゾーン] → [バウショック] → レイン →
  ノーブル → ライオン → ビートファング → [ジャギュラー] → ダブルダウン → サベッジ → [アブドメン] → ウィケッド → [アイガウジ] (140.9) → キーンエッジ。
  旧 Akechi: ビートファング → サベッジ → [ブラッドソイル] → ウィケッド → [ノーマーシー] (123.4) → レイン → ソニック → ダブルダウン → ノーブル →
  ライオン → ビートファング → …。
  - 差 1 (実質的): エンジンはコンボの途中 (キーンエッジ / ブルータルシェルの後) でノーマーシーを押し、バースト GCD がコンボを切る。300 秒で
    コンボ切れ 6 回 (旧 3 回)。ノーマーシー内の GCD の組 (ビートファング 3 段、ダブルダウン、ソニック、レイン 3 段 = 8 GCD) は画像と同じで、
    ブラッドソイルはノーマーシーの直前、弾の溢れは 0。
  - 差 2 (同等): 画像の「準備のビートファング」(2 チャージ寸前にノーマーシーの前に 1 鎖) に対し、エンジンはビートファングを 30 秒ごと
    (300 秒で 10 鎖、旧 9 鎖) に使っていて 2 チャージで止まることはない。ノーマーシーの前後どちらに鎖が来るかは探索の結果。
- NIN (【7.5】Lv100忍者)。画像: 水遁 → 活殺 → [薬] → 旋風刃 → 毒盛 → [雷遁] → [分身] → 残影鎌鼬 → 百雷銃 → 月影雷獣牙 → [夢幻三段] → [活殺] →
  氷晶乱流 → [天地人] → 天地人 (風魔 → 雷遁 → 水遁) → 月影雷獣牙 → [六道輪廻 / 是生滅法] → [命水]? → …。毎分固定: 百雷銃 → 月影雷獣牙 ＋ 夢幻三段・
  活殺 → 氷晶乱流 → (偶数) 天地人 → 命水。
  エンジン (HEAD、2 分目): 旋風刃 → [毒盛] → [天地人] → 風魔 → 雷遁 → 水遁 → [活殺] → 氷晶乱流 → [百雷銃] (128.4) → 雷遁 → [是生滅法] →
  月影雷獣牙 → [夢幻三段] → [天理人道] → 月影雷獣牙 → …。奇数分 (60〜75 秒): 百雷銃なし (氷晶乱流のみ)。旧 xan: 水遁 → … → [毒盛] → 雷遁 →
  残影鎌鼬 → [百雷銃] (129.7) → [夢幻三段] → [活殺] → 氷晶乱流 → [天地人] → 天地人 → [命水] → 月影雷獣牙 →、奇数分も [百雷銃] (69.3 / 190.5 秒)。
  - 差 1 (実質的): エンジンは奇数分の百雷銃を一度も押さない (9 戦闘の百雷銃内威力 165,259、旧 255,492)。定義の `KunaisBaneOdd` は「次の毒盛まで
    55 秒以上」を条件にしていたが、偶数分の百雷銃は毒盛の約 6.5 秒後なので、そのリキャスト (60 秒) が戻る時点で毒盛の残りは約 53.5 秒。条件を
    満たす時点がなく、奇数分は常に不合法だった。
  - 差 2 (実質的、未解決): エンジンは偶数分に天地人を百雷銃より先に押し、天地人の影走り (Shadow Walker) を百雷銃に使うので、命水の影走りが
    残らず命水を一度も使わない (旧 xan は水遁 → 百雷銃 → 天地人 → 命水)。天地人の 3 つの忍術も百雷銃の 10% の外に出る。27.4 (天地人に
    百雷銃中の条件を付けると開幕が壊れて −60,000)。
- RPR (【7.5】Lv100リーパー)。画像: ハーベストムーン → シャドウオブデス → [薬] → ソウルスライス → [アルケインサークル] → [グラトニー] → ジビトゥ →
  ギャロウズ → プレンティフルハーベスト → [レムールシュラウド] → … → コムニオ → ペルフェクティオ → ソウルスライス → [ブラッドストーク] → ジビトゥ。
  2 分目: [AC] → シャドウオブデス → … → [薬] → ギャロウズ → [グラトニー] → … → プレンティフル → [レムール] → … → コムニオ → ペルフェクティオ
  (バースト前に Soul 50 / Shroud 50)。
  エンジン (HEAD): ソウルスライス → [AC] (0.65) → SoD → [ブラッドストーク] → ジビトゥ → PH → [レムール] → … → コムニオ → ソウルスライス →
  [グラトニー] → 処刑人 2 回 → SoD → ペルフェクティオ。2 分目: ワクシング → [AC] (121.2) → インファナル → スライス → PH (128) → [レムール] →
  … → コムニオ → ペルフェクティオ (139) → ワクシング → [グラトニー] (142.2)。Shroud 50 のレムールは 105.2 秒 (バフ窓 127.8 秒の 22 秒前) に
  使っていて、2 分目のバーストの中にはレムールが 1 回しかない (4 分目は 243.3 / 254.3 の 2 回)。旧 xan は 120.2 (Shroud 50) → [AC] 123.2 →
  … → PH → レムール 131.5 の 2 回。
  - 差 1 (実質的): バースト直前に Shroud 50 のレムールを使い切り、バーストの中が 1 回になる (9 戦闘のレムール回数は 48 / 旧 44 で総数は減って
    いないが、レイドバフの外に出る)。ハーベストムーンは開幕にない (ハーネスは Soulsow なしで開始、旧も同じ)。AC は最初の GCD の後 (同じ)、
    グラトニーは AC 窓の中 (画像は AC 直後 / ペルフェクティオ直後、エンジンはコムニオ後 / ペルフェクティオの 1 GCD 後。同等)。
- MNK (【7.3】Lv100モンク)。画像: 双竜脚 → [踏鳴] → [桃園結義]? → 猿舞連撃 → [薬] → 双竜脚 → [紅蓮の極意] → … → 猿舞連撃 → 陰陽闘気斬 →
  [疾風の極意] → 必殺技 → 双竜脚 → 乾坤闘気弾 → 猿舞連撃 → [踏鳴] → …。要点: 踏鳴は双竜脚 / 猿舞連撃の直後、紅蓮のリキャ 4 秒で踏鳴 → 2 GCD →
  紅蓮 ＋ 桃園、必殺技 → 零の型 → 乾坤闘気弾 → 零の型 → 踏鳴 → 必殺技、絶空拳は疾風の中。
  エンジン (HEAD): 双竜脚 → [紅蓮] → [桃園] → 乾坤闘気弾 → [疾風] → 絶空拳 → [踏鳴] → 双掌打 → 破砕拳 → 連撃 → [金剛] → 鳳凰の舞 → [闘魂] →
  [踏鳴] → 正拳 → 双掌打 → 正拳 → 必殺技 (Elixir Burst)。2 分目: 連撃 → 正拳 → [踏鳴] → 双掌打 → [紅蓮] → [桃園] → 乾坤闘気弾 → 正拳 → 崩拳 →
  崩拳 → 破砕拳 → 夢幻闘舞 → [踏鳴] → 崩拳 → 崩拳 → 破砕拳 → 必殺技。
  - 差 1: 踏鳴が双竜脚 / 猿舞連撃の直後でない (開幕は絶空拳の後、2 分目は正拳 (Raptor) の後、2 回目はブリッツの直後)。定義に「Raptor の型のとき
    だけ」を付けると (b) で下がる (27.4)。
  - 差 2 (同等): 開幕の紅蓮を 1 GCD 目の後に押す (画像は踏鳴 → 2 GCD → 紅蓮、「紅蓮のリキャ 4 秒で踏鳴」は 2 分目の `PerfectBalancePre` と
    して定義にある)。絶空拳は疾風の中 (同じ)。ブリッツ直後の零の型の猿は §24.2 のとおり使っていない。
- BLM (7.2 調整後 / マナフォント記事)。画像の開幕: ファイガ → ハイサンダー → [黒魔紋] → [三連魔] → ファイジャ → [アンプリファイア]? → [薬] →
  ファイジャ ×4 → ゼノグロシー → [迅速魔] → ファイジャ → デスペア → ファイジャ ×5 → ハイサンダー → ファイジャ → デスペア → フレアスター。
  マナフォントは「リキャ優先、AF を使い切った後」。
  エンジン (HEAD): ファイガ → ハイサンダー → [黒魔紋] → [三連魔] → デスペア (MP 8,000 で) → [マナフォント] → [アンプリファイア] → ゼノ →
  [迅速魔] → ファイジャ ×6 → フレアスター → デスペア → [トランス] → [黒魔紋] → パラドックス → ブリザジャ → ハイサンダー → [トランス] → ゼノ →
  デスペア (MP 10,000 で) → [三連魔] → ブリザガ → …。旧 xan: [迅速魔] → ファイガ → [アンプリファイア] → ハイサンダー → ファイジャ ×5 → ゼノ →
  [マナフォント] (18.8) → [黒魔紋] → ファイジャ → フレアスター → …。
  - 差 1: デスペアを MP が残ったまま (8,000 / 10,000) 使い、直後にマナフォントかブリザガで MP を戻す線。画像は AF を使い切ってからデスペア →
    マナフォント。定義で「デスペアは MP 2,399 以下 (次のファイジャで 800 を切るとき)」「マナフォントは MP 799 以下」にすると 9 戦闘で下がる
    (27.4)。サンダーの位置 (2 GCD 目、以後は Thunderhead の 30 秒で) は画像と同じ。

### 27.2 変更内容

- SAM (Lv100 だけ、シンクレベルの定義・ルールは変わらない):
  - `SamDefinition`: 明鏡止水に `.ForbidStatus(Tendo)` (100 以上)。天道はスタックしないので、天道雪月花を使う前の 2 つ目の明鏡止水は天道を
    上書きするだけだった (27.1 の差 2)。画像の両方の開幕 (2 つ目の明鏡止水は天道雪月花の後) に合わせた。
  - `SamEngineModule.ApplyStrategy`: 明鏡止水トラックが Auto のとき、レイドバフ窓の 5〜24 秒前に来る明鏡止水を窓まで Forbid する
    (窓の中・5 秒前以内は押せる。天道雪月花はその 3 GCD 後なので、組が窓の直前ではなく中に入る。27.1 の差 1)。開幕 (戦闘開始から
    RaidBuffFirst = 7.8 秒) は対象外 (開幕の明鏡止水は即押し、ゲーム内はカウントダウンで押す)。奇数分の明鏡止水はこれまでどおりリキャスト
    (55 秒) で押し、画像のような 60 秒周期への固定はしていない (27.4 の 1 で下がった)。
- `EngineRotationModule.RaidBuffTimings()` (Adapter、追加のみ): エンジンの時間線が見るレイドバフ窓 (パーティの窓、なければ戦闘開始からの
  2 分周期) を「残り / 次まで」で返す。既存の `EstimateRaidBuffTimings` (RotationModule) はパーティの CD からしか見ないので、ハーネスの
  `--party-buffs` なしの実行では常に「次まで 0 秒」になり、規則が決定論の測定で効かず (SAM の 27.4 の 1 は決定論が HEAD と同じ値)、
  ゲーム内 (パーティあり) とハーネスで判断が変わる。探索の時間線と同じ窓を読むために足した。Core は変更なし。
- NIN: `NinDefinition` の `KunaisBaneOdd` の条件 `RequiresCooldownAtLeast(DokumoriCD, 55)` → 50。偶数分の百雷銃は毒盛の約 6.5 秒後なので、
  そのリキャスト 60 秒が戻る時点で毒盛の残りは約 53.5 秒。55 では奇数分の百雷銃が合法になる時点がなく、一度も押されていなかった
  (27.1 の差 1)。50 で「次の毒盛の 10 秒以内に戻る」の意味になる。シンクレベルのルール (`FirstLegal("KunaisBane", "KunaisBaneOdd")`) は
  この条件も見るが、90 / 80 / 70 / 68 の出力行は変更前と完全一致 (下の 27.3)。
- RPR (Lv100 だけ): `RprEngineModule.ApplyStrategy`: Enshroud トラックが Auto でバフトラックが Delay でないとき、レイドバフ窓の 12 秒前
  からは Shroud 50 の `Enshroud` を Forbid する (Ideal Host の `EnshroudIdeal` はそのまま)。Shroud 50 をバーストに持ち越す (27.1 の差 1)。
  20 秒前からにすると (b) で下がる (27.4 の 4)。
- GNB / MNK / BLM: 変更なし (試した規則はすべて下がった。27.4)。Lv100 の決定論の出力行は HEAD と完全一致。

### 27.3 検証

- Release ビルド 0 エラー (BossModReborn、xan_timeline_harness、engine_tests 17/17、blm_engine_eval、rpr_engine_eval、engine_bench、
  engine_tuner、blm / mnk / nin / sam / rpr / drg_regression。vpr_regression は §25.3 のとおり変更前からビルドが通らない)。
- Lv100 (決定論 = 9 戦闘 ENGINE_FRAME_MS=1000 + BudgetMs 100、ライブ = 組み込み (0.8 ms) を 2 回、(b) = `--party-buffs 7.8`、300 秒 =
  `event-timeline --duration 300`。HEAD = db0a6a728、旧 = xan [Custom] (SAM / NIN / RPR はパーティバフの有無で同じ出力)。ライブと Execute は
  6 ジョブを並列に走らせたもので、HEAD も同じ条件で測り直した (§23 の単独実行より平均 / p99 が大きい)):

| SAM Lv100 | 決定論 | 決定論 (b) | ライブ | ライブ (b) | 300 秒 | Execute 平均 / p99 µs (ライブ 2 回) |
|---|---:|---:|---:|---:|---:|---:|
| HEAD (db0a6a728) | 672,188 | 673,349 | 673,943 / 672,846 | 670,048 | 110,496 | 74.8 / 555、73.5 / 544 |
| 旧 xan SAM | 683,232 | 683,304 | 683,232 | 683,304 | 110,286 | |
| 変更後 | **676,549** | **678,174** | 679,105 / 680,381 | 674,259 | 108,310 | 77.3 / 605、75.1 / 561 |

  - HEAD 比 +0.6% / (b) +0.7%、ライブ +0.9%、(b) +0.6%。旧版にはまだ届かない (−1.0% / −0.8%、§23 では −1.6% / −1.5%)。300 秒単体は −2.0%
    (天道の組 1 つが 127.8 秒の窓まで待つ分、彼岸花の間隔が伸び、300 秒の終わりに雪月花が 1 回間に合わない: 居合 18 / 19 回、DoT 切れ
    11.6 / 5.0 秒)。
  - 9 戦闘の内訳 (決定論 / (b)): 短い 8 戦闘が +3,300 / +3,700 (カウントダウン開幕の 2 つ目の明鏡止水が天道雪月花の後に移り、60 秒戦闘で
    天道の組が 2 つになる: 25,497 → 26,728)、長い z1363 が +1,058 / +1,093 (天道の組がレイドバフの中に入る)。
  - 300 秒 (決定論) の 2 分目: 雪月花 (116) → … → [明鏡止水] (125.1) → [意気衝天] (126.7) → 花車 → 月光 → 天道雪月花 (138.2) → 返し天道 →
    … → 彼岸花 (147)。画像の「明鏡止水 → 月光 → 花車 → 返し → 天道雪月花 → … → 返し天道 → 奥義斬浪 → 返し斬浪」の順そのものではない
    (奥義斬浪は天道の前) が、天道の組は窓の中。

| NIN Lv100 | 決定論 | 決定論 (b) | ライブ | ライブ (b) | 300 秒 | Execute 平均 / p99 µs |
|---|---:|---:|---:|---:|---:|---:|
| HEAD | 650,308 | 652,226 | 649,283 / 649,283 | 652,571 | 98,651 | 39.5 / 166、38.5 / 163 |
| 旧 xan NIN | 639,456 | 639,076 | 639,456 | 639,076 | 101,765 | |
| 変更後 | **656,049** | **656,859** | 654,060 / 654,456 | 656,490 | 101,049 | 40.4 / 170、39.9 / 171 |

  - HEAD 比 +0.9% / (b) +0.7%、300 秒 +2.4% (旧版 −0.7%、HEAD は −3.1%)。9 戦闘の百雷銃内威力 165,259 → 196,268 (旧 255,492)、雷獣失効 7 → 4、
    GCD 1,127 → 1,138 (水遁が奇数分に 1 回増える)。300 秒の奇数分: 水遁 → [活殺] → 旋風刃 → [百雷銃] (68.1) → … → [夢幻三段] → 氷晶乱流。
    百雷銃は 8.1 / 68.1 / 128.9 / 188.9 / 250.4 秒 (旧 9.0 / 69.3 / 129.7 / 190.5 / 250.7)。
  - 偶数分の天地人 → 百雷銃の順と命水なしは残っている (27.4 の 3)。

| RPR Lv100 | 決定論 | 決定論 (b) | ライブ | ライブ (b) | 300 秒 | Execute 平均 / p99 µs |
|---|---:|---:|---:|---:|---:|---:|
| HEAD | 666,657 | 689,735 | 666,567 / 666,567 | 689,735 | 108,353 | 39.5 / 133、37.3 / 130 |
| 旧 xan RPR | 665,096 | 685,448 | 665,096 | 685,448 | 107,518 | |
| 変更後 | **670,681** | **690,011** | 670,661 / 670,681 | 690,011 | 108,353 | 39.8 / 133、38.9 / 134 |

  - HEAD 比 +0.6% / (b) +0.04%、ライブも同じ向き。300 秒は同じ出力 (105 秒のレムールは窓の 22.6 秒前で 12 秒の範囲に入らない)。9 戦闘の
    レムール 48 → 47、コムニオ 42、ペルフェクティオ 15、Shroud 溢れ 0、DD 維持率 0.977 → 0.966 (b: 0.975 → 0.969)。
- GNB / MNK / BLM: 変更なし。決定論・(b) の出力行は HEAD と完全一致 (566,864 / 578,686、578,459 / 589,056、560,467 / 560,364)。同じ条件の
  ライブ・Execute (HEAD → 変更後のビルド): GNB 566,864 / 578,686、39.3 / 209 → 38.7 / 199〜207。MNK 578,459 / 589,056、48.6 / 173 → 47.6 / 167〜174。
  BLM 560,467 / 560,364、32.8 / 178 → 32.3 / 179〜181 (BLM の失敗 1 は §20.4 の境界テスト)。
- シンクレベル (9 戦闘 `--level L`、既定トラック。ルールは探索をしないので決定論 = ライブ): SAM 90 / 80 / 70 / 60 / 50 (534,066 / 489,536 /
  395,394 / 336,216 / 318,674)、NIN 90 / 80 / 70 / 68 (539,116 / 479,604 / 403,665 / 383,067)、RPR 90 / 80 (555,398 / 469,211) の出力行が
  HEAD と完全一致、失敗 0。SAM / RPR の規則は Lv100 だけ (`HasSkill("TendoSetsugekka")` / `HasSkill("Perfectio")`)、SAM の定義の条件は
  天道のある 100 だけ、NIN の条件は 100 未満では判断を変えなかった。

### 27.4 入れていないもの (測定して下げたもの)

決定論 / 決定論 (b) / 300 秒。基準は HEAD の同じ値。

1. SAM 明鏡止水を 60 秒周期に固定 (レイドバフ窓の 8 秒前〜窓の中と、その 60 秒後の 52〜68 秒前だけ合法、`EstimateRaidBuffTimings` 経由):
   672,188 (規則が効いていない。27.2 の RaidBuffTimings の理由) / 667,629 / 110,496、ライブ 672,668 / 671,178、(b) 668,703。(b) の 300 秒の
   トレースでは明鏡止水が 60 / 120 / 180 / 240 秒に揃い天道が 133.8 秒で窓の中に入るが、300 秒で明鏡止水が 7 → 6 回、天道の組が 7 → 5 組
   (雪月花に置き換わる)。55 秒リキャストを 60 秒周期にする分の損が、窓に入る分の得 (5% × 約 3,000) より大きい。
2. SAM の採用した規則の別案 (すべて 9 戦闘では採用案 676,549 / 678,174 以下、または 300 秒で下回る):
   - 保持 (5〜24 秒前) だけ、開幕の例外なし: 680,404 / 678,562 / 107,979 (9 戦闘は最高だが、カウントダウンなしの開幕で 2 つ目の明鏡止水が
     3 秒遅れて天道を上書きし、300 秒で天道の組 7 → 6)。
   - 保持 + 開幕の例外、天道の条件なし: 673,245 / 674,442 / 108,310 (短い戦闘は HEAD と同じ)。
   - 天道の条件 + 保持、開幕の例外なし: 678,679 / 677,492 / 109,441。
   - 天道の条件だけ (保持なし): 675,491 / 677,081 / 110,496 (300 秒は HEAD と同じ。採用案より (b) で −1,093、z1363 で −1,058 / −1,093)。
3. NIN 天地人に `RequiresStatus(KunaisBane)` (画像の 百雷銃 → … → 天地人 → 命水 の順にして命水の影走りを残す): 590,754 / 593,723 / 91,055。
   エンジンは偶数分の百雷銃の影走りを天地人から取っていて (水遁を先に撃たない)、天地人を百雷銃の後にすると開幕で百雷銃が水遁まで
   22 秒遅れ、途中で捨てた印 (天 → 人 の後 6 秒の空き) も出る。命水 1 回 (忍気 50 + 是生滅法 +150) より百雷銃の遅れの損が大きい。
   水遁 → 毒盛 → 百雷銃 → 天地人 → 命水 の組み立てには探索の地平線 (4 GCD) を越える先読みが要る。
4. RPR Shroud 50 の Enshroud の保持を窓の 20 秒前から: 669,740 / 689,672 / 106,891、ライブ 670,037 / 670,015、(b) 686,544。2 分目で
   Enshroud が 2 回とも窓に入る代わりにペルフェクティオ (154 秒) が窓の外に出る。12 秒前からにした。
5. GNB コンボ途中のノーマーシー禁止 (キーンエッジ / ブルータルシェル / デモンスライスの後): 553,868 / 566,127 / 87,564、ライブ 552,646 /
   553,916、(b) 566,127。根の Forbid は地平線 (4 GCD) 全体に残るので、探索はノーマーシーを待たずにレイン / ソニックでコンボを切ってから
   ノーマーシーを押す (NM 外のバースト 1 → 28 回)。ノーマーシーに加えてコンボを切る GCD (ソニック、レイン 3 段、ダブルダウン、弾が上限
   未満のときのビートファング / バーストストライク / フェイテッド) も禁止する版: 545,056 / 558,354 / 84,520 (コンボ切れは 300 秒で 6 → 3 回
   だが、ノーマーシーが 2 GCD 遅れて毎分ずれ、ビートファング 56 → 44 鎖)。「コンボ途中で入らない」は、ノーマーシーの 2〜3 GCD 前から
   コンボの段を合わせる先読み (画像の「準備のビートファング」「バーストストライク 2 回」) で、根の Forbid では表せなかった。
6. MNK 踏鳴 3 種に `RequiresStatus(RaptorForm)` (双竜脚 / 猿舞連撃の直後だけ): 578,532 / 587,572 / 92,014 (b で −1,484)。2 分目は 双竜脚 →
   [踏鳴] → 双掌打 → 連撃 → 正拳 → 夢幻闘舞 → [踏鳴] と画像の形になるが、紅蓮の 20 秒に入るブリッツが減る。
7. BLM デスペアに `RequiresGaugeAtMost(MP, 2399)` (次のファイジャで 800 を切るときだけ): 552,990 / 545,826 / 87,751。さらにマナフォントに
   `RequiresGaugeAtMost(MP, 799)`: 552,005 / 539,204 / 88,718。デスペアを塞ぐと探索は開幕 3 GCD 目にフレア (単体) で MP を捨ててマナフォント
   を押す (モデルではマナフォントの MP 10,000 が「捨てた分」を戻すので、350 / 240 の威力が無料に見える)。マナフォントも塞ぐと開幕が
   ファイガ → サンダー → [マナフォント] (MP 8,000 で、+2,000 だけ) になる。300 秒単体は上がるが 9 戦闘 (ダウンタイム前の MP 捨て) で下がる。
   §24 のトランス / マナフォントの線は触っていない。
8. 画像にあって入れていないもの: SAM 返し雪月花の偶数分までの保持 (Hold トラックで可)、SAM 奇数分の 明鏡止水 → 月光 → 彼岸花 (§23.3)、
   RPR 開幕のハーベストムーン (ハーネスが Soulsow なしで始まる。ゲーム内は Soulsow トラックで戦闘前に撒く)、MNK 開幕の 踏鳴 → 2 GCD →
   紅蓮 (エンジンは紅蓮を 1 GCD 目の後)、GNB 準備のビートファング。薬の位置は 9 戦闘 (薬なし) では測れない。
- ゲーム内での確認は未実施。

出典: ersharifst の各記事 (§22〜26 の出典と同じ URL)、scratchpad の guide-images-transcription.md (画像の読み取り)。

## 28. 方針の変更 (正しさ優先)、先読みの拡大、修正の再試行

### 28.1 方針の変更

- vin の方針変更: [Engine] の回しは「旧モジュールより速い判断」を目標にしていたが、その目標は取り下げる。「スキルの選択が間違っていなければ、
  時間がかかるのは構わない」。判断の速さより正しい選択を優先する。
- 受け入れ基準は変わらない: Lv100 は旧モジュールを下回らない (9 戦闘、`--party-buffs 7.8` が主、バフなしは従、300 秒単体は報告)、Lv100 未満は
  確実性優先、ライブは決定論 (判断は対象の GCD より前に終わる、ライブの出力行 = 決定論の出力行)。
- 記法は §27 と同じ: 決定論 = 9 戦闘 ENGINE_FRAME_MS=1000 + BudgetMs 100 (組み込みの MinNodes / SliceNodes はそのまま)、(b) = `--party-buffs 7.8`、
  300 秒 = `event-timeline --duration 300`、ライブ = 組み込みの既定値 (バフなし 2 回、(b) 1 回)、Execute = ライブの `XAN_HARNESS_EXEC_PROFILE=1`
  の平均 / p99 µs。HEAD = 41309ebbc、旧 = xan [Custom] (GNB / PLD は Akechi)。

### 28.2 先読みの拡大 (HorizonGcds 5 / 6)

- 探索の深さの上限は Core にない (`MaxPly` 40 の枠だけ)。各ジョブの重みの `HorizonGcds` を 4 → 5 / 6 (BLM は 3 → 4 / 5 / 6) にし、まず現行の
  重みのまま決定論を測った。**現行の重みのままでは全ジョブで深いほど下がった** (PLD の (b) だけ上がる)。
  - 原因を SAM で追った (`BlmEngineEval explain --def sam`、雪風を押して閃を捨てた時点の状態): 上位層の閃 (SenCount) の shadow price が彼岸花の
    DoT 込みの値 (約 1,120 / 閃。`JobAnalysis` は「その資源を使う最良スキル」で単位価値を決め、彼岸花は 1 閃で 200 + DoT 1,000 + 瞑想) で、
    雪月花 (3 閃で約 2,140) より「持っている」ほうが高い。地平線 4 では 4 GCD の中で閃を溜め切れないので表に出なかったが、5 では「閃を
    溜めたまま地平線を終える」線 (雪風 → 刃風 → 陣風 → 月光 → 刃風、閃 3 で終わり 5,249) が「陣風 → 月光 → 雪月花 → 返し → 刃風」(4,971)
    より高く、Setsu が付いているのに雪風を押していた (9 戦闘の閃溢れ 7 → 45、DoT 切れ 76 → 134 秒)。重みの `GaugeValue.SenCount` (負)
    で単位価値を下げられる (`Analysis.GaugeUnitValue = Base + GaugeValue`) ので、SAM の再調整の params に `GaugeValue.SenCount:-900:0`
    を足した (RPR の `GaugeValue.Shroud` と同じ使い方)。−500 だけで (b) 666,389 → 679,697。
  - NIN は深くすると忍術の拒否 (`ninjutsu_rejected` 0 → 1,071、`mudra_timeouts` 9) が出た。探索が「活殺 (ウィーブ) → 氷晶乱流」の線を GCD が
    回っている時点で選ぶと、Adapter は GCD (印の 1 つ目) と oGCD (活殺) を同じフレームに積み、キューは印を先に押す (天 → 人 が活殺なしの
    氷晶乱流 = 無効)。Adapter の `Execute` に「計画の oGCD で初めて合法になる GCD は、その oGCD のフレームまで積まない」
    (`GcdNeedsAbilityFirst`: 今の状態で GCD が不合法、oGCD 実行後の状態で合法) を足した。地平線 4 の決定論は NIN 以外のジョブで変更前と
    完全一致、SAM だけ 676,549 → 677,477 (明鏡止水 → 月光 (明鏡) の順が直った分)。NIN の地平線 5 は 626,603 / 623,511 → 659,338 / 658,850。
- 再調整: 深くして下がったジョブは `tune-xan --args "timeline-matrix --scenario-limit 8 --party-buffs 7.8" --gens 14 --pop 12` をその地平線の
  重み (MNK / DRG / VPR / BLM は SliceNodes を最大ノード数 / 40 に広げたもの) から 1 回 (params は §15 / §16 / §20 / §25 / §26 のもの、SAM は
  SenCount を追加)。{現行, 深い, 深い + 再調整} のうち (b) が最高で旧モジュールを下回らないものを採った。

決定論 (バフなし / (b) / 300 秒)。「現行の重みで 5 / 6」は HEAD の重みのまま地平線だけ変えたもの (MNK / DRG / VPR / BLM の 6 は組み込みの
SliceNodes 40 / 400 / 40 / 50 のまま、5 は最大ノード数 / 40 に広げた版)。再調整は (b) だけ (チューナーの値):

| | HEAD (地平線) | 現行の重みで 5 | 現行の重みで 6 | 再調整 (地平線) | 採用 | 旧モジュール |
|---|---|---|---|---|---|---|
| RPR | 670,681 / 690,011 / 108,353 (4) | 660,699 / 680,902 / 107,222 | 658,767 / 681,642 / 105,011 | 685,919 (5) | 4、重み同じ | 665,096 / 685,448 / 107,518 |
| NIN | 656,049 / 656,859 / 101,049 (4) | 659,338 / 658,850 / 100,801 (Adapter 修正後) | 633,254 / 629,233 / 97,311 (修正前) | 662,455 (5、28.3 の 2 の定義で) | 5 + 再調整 + 28.3 の 2 | 639,456 / 639,076 / 101,765 |
| MNK | 578,459 / 589,056 / 92,352 (4) | 574,097 / 583,668 / 91,998 | 575,817 / 586,182 / 92,246 | 590,339 (5) | 5 + 再調整 | 578,440 / 588,786 / 92,584 |
| SAM | 676,549 / 678,174 / 108,310 (4) | 664,929 / 665,677 / 106,365 (修正後) | 668,772 / 668,931 / 106,997 | 685,818 (5、SenCount 込み) | 5 + 再調整 | 683,232 / 683,304 / 110,286 |
| GNB | 566,864 / 578,686 / 89,024 (4) | 561,060 / 574,496 / 87,280 | 557,620 / 574,923 / 85,844 | 581,448 (5) | 5 + 再調整 | 562,116 / 578,388 / 89,444 |
| PLD | 553,005 / 560,806 / 85,106 (4) | 551,718 / 567,544 / 84,338 | 553,455 / 570,151 / 85,426 | なし (現行で上がる) | 6、重み同じ | 540,983 / 555,506 / 86,562 |
| DRG | 634,249 / 631,855 / 96,542 (4) | 614,085 / 620,140 / 95,063 | 605,944 / 618,344 / 96,171 | 639,476 (5) | 5 + 再調整 | 630,336 / 630,336 / 98,872 |
| VPR | 710,169 / 710,291 / 114,038 (4) | 700,190 / 704,554 / 114,140 | 698,633 / 701,780 / 111,595 | 705,774 (5) | 4、重み同じ | 700,312 / 698,736 / 113,655 |
| BLM | 560,467 / 560,364 / 87,197 (3) | 4: 544,087 / 550,156 / 87,106、5: 533,478 / 535,364 / 84,915 | 550,046 / 524,614 / 88,000 | 561,324 (4) | 4 + 再調整 | 564,959 / 565,344 / 89,711 |

採用した設定と結果 (組み込みの既定値。決定論 = ライブ = 出力行が完全一致、RPR の注記は下)。最大ノード = 9 戦闘の 1 判断の最大 (ノード分割
あり。PLD は中断した部分木のやり直し込みの累計で、分割なしの最大は 38,706)、フレーム = 最大ノード / SliceNodes (ハーネス 20 Hz の 1 GCD =
40〜50 フレーム; 押せる瞬間が来れば `FinishPending` が残りをそのフレームで終える)、Execute = ライブ 2 回の平均 / p99 µs と 2 回目の最大 µs
(1 回目の最大は JIT の初回で 50〜250 ms)、HEAD の Execute は §27.3 / §25.3 / §26.3 と同じ条件で測り直したもの:

| | 地平線 / BudgetMs / MinNodes / SliceNodes (フレーム予算 ms) | 決定論 / (b) / 300 秒 | HEAD 比 (b) | 旧比 (b) | 最大ノード / フレーム | Execute 平均 / p99 / 最大 | HEAD の Execute |
|---|---|---|---|---|---|---|---|
| RPR | 4 / 10 / 1,500 / 1,500 (0.08) | 670,681 / 690,011 / 108,353 | ±0 | +0.7% | 1,005 / 1 | 47.9 / 162、48.3 / 165 / 51,903 | 39.5 / 133 |
| NIN | 5 / 30 / 14,000 / 2,900 (0.03) | 657,855 / 662,455 / 101,432 | +0.9% | +3.7% | 11,702 / 5 | 109.8 / 2,462、109.5 / 2,458 / 21,540 | 39.5 / 166 |
| MNK | 5 / 50 / 25,000 / 525 (0.03) | 578,603 / 590,339 / 92,153 | +0.2% | +0.3% | 20,872 / 40 | 141.6 / 1,280、147.4 / 1,263 / 120,120 | 48.6 / 173 |
| SAM | 5 / 80 / 40,000 / 1,600 (0.05) | 678,197 / 685,819 / 110,206 | +1.1% | +0.4% | 26,146 / 17 (分割なし 32,494 / 21) | 293.9 / 3,212、320.5 / 3,205 / 129,342 | 74.8 / 555 |
| GNB | 5 / 30 / 14,000 / 560 (0.05) | 564,636 / 581,448 / 89,560 | +0.5% | +0.5% | 11,338 / 21 | 119.0 / 1,061、111.0 / 1,026 / 68,962 | 39.3 / 209 |
| PLD | 6 / 100 / 46,000 / 3,880 (0.05) | 552,344 / 570,151 / 85,426 | +1.7% | +2.6% | 152,776 (累計) / 40 | 357.7 / 5,663、350.2 / 5,612 / 107,170 | 43.1 / 213 |
| DRG | 5 / 60 / 30,000 / 625 (0.5) | 636,786 / 639,476 / 98,904 | +1.2% | +1.4% | 20,945 / 34 | 169.0 / 1,526、193.4 / 1,524 / 124,842 | 64.3 / 665 |
| VPR | 4 / 0.8 / 2,000 / 40 (0.05、変更なし) | 710,169 / 710,291 / 114,038 | ±0 | +1.7% | 1,656 / 42 | 69.2 / 211、47.5 / 164 / 45,294 | 48.2 / 170 |
| BLM | 4 / 12 / 6,000 / 100 (0.08) | 552,512 / 561,324 / 87,577 | +0.2% | −0.7% | 5,066 / 51 | 81.6 / 528、77.2 / 463 / 39,901 | 32.8 / 178 |

- RPR / VPR は深くしても再調整しても (b) が HEAD 未満なので現行のまま (重みも同じ)。RPR はライブ = 決定論にするため MinNodes / SliceNodes
  (分割幅 > 最大ノードなので 1 判断 = 1 フレーム) と BudgetMs 10 を入れた。決定論・(b)・300 秒は HEAD と完全一致。ライブのバフなしは
  670,661 / 670,643 (決定論 670,681 との差 −20 / −38) で、HEAD のライブ (670,661 / 670,681) と同じ揺れ (初回の JIT 50 ms がハーネスの最初の
  戦闘にかかる分。(b) と 300 秒は一致)。
- BLM は 4 + 再調整が (b) で最高 (+960) だが、バフなしは 560,467 → 552,512 (−1.4%) と下がる。規則 (主数値で選ぶ) どおり採ったが、バフなしの
  差は残る。旧モジュール (565,344) には §16 以降届いていない (今回も −0.7%)。
- PLD は現行の重みのまま 6 で (b) +9,345。分割なしのバフなしは 553,455 (HEAD +450) だが、SliceNodes 3,880 の版では 552,344 (HEAD −661)。
  SliceNodes 970 / 1,940 だと (b) 569,981 / 565,927 と揺れる: 中断した根の手の部分木は次のフレームでやり直す (TT に残った分は再利用) ので、
  どこで切れるかで結果が少し変わる。GNB も 280 では 578,781 (560 / 1,120 / なしで 581,448)、NIN も 580 / 1,160 では 651,456 (2,900 以上で
  662,455)、BLM は 100 で 561,324、分割なしで 560,650。分割幅は決定論が分割なしの値 (BLM は調整時の値) になる最小のものにした。
- Execute は HEAD の 2〜7 倍 (平均 50〜360 µs、p99 0.2〜5.7 ms)。1 ms 超の呼び出しは 9 戦闘 37,337 フレーム中 RPR 23 / VPR 22 / BLM 142 /
  GNB 406 / NIN 963 / DRG 748 / MNK 1,397 / PLD 2,581 / SAM 3,696 回、最大は FinishPending が大きな探索 (開幕、BudgetScale 8) を 1 フレームで
  終える分 (SAM 129 ms、DRG 125 ms、MNK 120 ms、PLD 107 ms)。方針 (選択が正しければ時間はかかってよい) に沿い、これを受け入れた。
- 重みは `tools/blm_engine_eval/tuned/weights-{SAM-v3,GNB-v5,MNK-v5,DRG-v2,BLM-v6,NIN-v2}.json` (NIN は 28.3 の 2 の定義で調整したもの)。

### 28.3 修正内容

Core / Adapter に足した仕組み (1 つずつ、tools/engine_tests にテスト付き。使わないジョブの Lv100 決定論の出力行は変更前と完全一致):

- Adapter `GcdNeedsAbilityFirst` (28.2 の NIN): 計画の oGCD で初めて合法になる GCD は、その oGCD のフレームまで積まない。
- `ConditionKind.ComboIsNot` (`RequiresComboNot(from)` / `RequiresNoCombo()`): 「コンボが X でない / コンボ中でない」を探索の各ノードで判定する
  条件。根の Forbid は地平線全体に残るが、これは探索が「コンボを仕上げてから押す」線を計画できる (ComboConditionTests: 玩具ジョブで Burst が
  Finish の後に入る、条件を使わない定義の判断は不変)。
- `ConditionKind.StatusLeftAtMost` (`RequiresStatusLeftAtMost(status, s)`): 「ステータスが残り s 秒以下 (または無し)」の各ノード条件
  (RefreshAndHoldTests)。
- `EngineState.HeldSkills / HeldUntil` (Adapter の `Hold(ref s, skill, seconds)`): 探索が「その時刻まで使えない、以後は使える」と見る保持。
  根の Forbid と違い、保持の明ける時刻が地平線の中なら探索はそこに置ける (ハッシュに入る。RefreshAndHoldTests)。
- 後の 3 つは下の 1 / 5 の試行で使ったが、採用した定義・モジュールでは使っていない (仕組みとテストは残す。engine_tests 23/23)。

試行の結果 (決定論 / (b) / 300 秒。基準 = 各ジョブの採用した探索設定の値):

1. GNB ノーマーシーをコンボ途中で押さない。
   - `RequiresNoCombo()` (地平線 4、現行の重み): 541,490 / 553,238 / 87,426。クライアントのコンボ状態は Reign of Beasts / Gnashing Fang の
     連携も「コンボ」として報告する (モジュールの `ReadCombo` がそのまま根に入る) ので、連携の間もノーマーシーが不合法になり、2 分目の
     ノーマーシーが 121.4 → 130.7 秒 (ダブルダウンの後) にずれた。
   - `RequiresComboNot(KeenEdge / BrutalShell / DemonSlice)` (1-2-3 の途中だけ不合法): 地平線 4 で 558,676 / 572,322 / 88,776、採用した
     地平線 5 の重みで 555,880 / 571,668 / 88,480、この定義で再調整 (14 世代) しても 557,732 / 574,028 / 88,376。300 秒のコンボ切れは 6 → 4 回、
     ノーマーシーは 3.15 / 63.15 / 123.15 / 183.15 / 243.25 秒と 60 秒周期に揃うが、NM 外のバースト (GF の連携) が 1 → 34 回で、9 戦闘は
     HEAD (578,686) を下回る。**不採用** (定義は変更なし)。
2. NIN 偶数分の順 (百雷銃 → 雷獣 + 夢幻三段 + 活殺 → 氷晶乱流 → 天地人 → 命水)。
   - 天地人に `RequiresStatus(KunaisBane)` だけ (地平線 5、現行の重み): 657,589 / 656,852 / 100,170 (基準 659,338 / 658,850 / 100,801 より
     少し下。命水は使うようになるが氷晶乱流が百雷銃の前に出る)。
   - 天地人と氷晶乱流の両方に `RequiresStatus(KunaisBane)` (Lv100 だけ): 現行の重みで 653,140 / 655,009 / 100,982、この定義で再調整
     (14 世代、tuned/weights-NIN-v2.json) して **657,855 / 662,455 / 101,432** → **採用**。300 秒の 2 分目: 毒盛 (121.0) → [夢幻三段] →
     旋風刃 → [是生滅法] → 水遁 → [活殺] → [百雷銃] (128.3) → 氷晶乱流 → [天地人] → 天地人 → [命水] → 雷獣 → [六道輪廻] → [天理人道]。
     9 戦闘の百雷銃内威力 209,917 → 239,551 (旧 255,492)、水遁が毒盛の前に入る (印の上限待ち mudra_cap_s 0.1 → 2.2 秒)。奇数分の百雷銃
     (41c3e172b) はそのまま。Lv100 未満の定義・ルールは変えていない (条件は 100 だけ)。300 秒単体は旧 (101,765) に −0.3%。
3. MNK 踏鳴を双竜脚 / 猿舞連撃の直後だけ (`RequiresStatus(RaptorForm)`): 地平線 4 で 578,532 / 587,572 / 92,014 (§27.4 と同じ)、採用した
   地平線 5 の重みで 575,072 / 584,267 / 91,505 (基準 578,603 / 590,339 / 92,153)。beast_drop 6 → 11、blitz_expired 0 → 3: 踏鳴の位置を縛ると
   2 回目の踏鳴の 3 GCD の後にブリッツが 2 GCD 遅れる場面が出る。**不採用**。零の型の重み (`StatusValue.FormlessFist`) は、踏鳴の条件だけで
   −6,000 なので試していない。
4. BLM デスペア / マナフォントの MP 条件と単体フレア禁止。地平線 3 (HEAD の重み): MP 条件 552,005 / 539,204 / 88,718、フレア 2 体以上
   557,967 / 551,401 / 86,366、両方 552,745 / 559,817 / 88,311。採用した地平線 4 の重みで: MP 553,938 / 540,655 / 88,224、フレア 545,659 /
   555,156 / 86,917、両方 552,300 / 525,202 / 89,562 (基準 552,512 / 561,324 / 87,577)。300 秒単体は両方で +2,000 だが 9 戦闘 (ダウンタイム前
   の MP の捨て方) で下がる。**不採用**。
5. SAM (基準 = 採用した地平線 5 の重み 678,197 / 685,819 / 110,206):
   - 彼岸花を各ノード条件 `RequiresStatusLeftAtMost(Higanbana, 15)` にして根の Forbid を外す (明鏡止水 → 月光 → 彼岸花を切れる時点に合わせ
     られるように): 674,150 / 680,564 / 108,798。閾値 10 秒: 677,657 / 677,729 / 108,885。
   - 偶数分の前の最後の返し雪月花を窓の頭まで `Hold` (レイドバフ窓の 24 秒前から、返しの残りが窓まで持つとき): 678,462 / 679,604 / 109,637。
   - 明鏡止水の保持 (§27.2) を根の Forbid から `Hold(raidBuffIn − 5)` に: 676,704 / 679,319 / 109,665。
   - 刃風のやり直し禁止 (`Gyofu.RequiresComboNot(Gyofu / Jinpu / Shifu)`、§23.3 の 3 の各ノード版): 666,371 / 665,568 / 108,697。
   - いずれも (b) で基準を下回る → **不採用** (定義・モジュールは e4d9163cb のまま)。SAM は探索設定 (地平線 5 + SenCount 込みの再調整) と
     Adapter の修正だけで旧モジュールを (b) で上回った (685,819 / 683,304、+0.4%。バフなし 678,197 / 683,232 は −0.7%、300 秒
     110,206 / 110,286 は −0.1%)。

### 28.4 検証

- Release ビルド 0 エラー (BossModReborn、xan_timeline_harness、engine_tests 23/23 (新規 6: ComboConditionTests 3、RefreshAndHoldTests 3)、
  blm_engine_eval、rpr_engine_eval、engine_bench、engine_tuner、blm / mnk / nin / sam / rpr / drg_regression。vpr_regression は §25.3 のとおり
  変更前からビルドが通らない)。
- Lv100: 上の表 (9 ジョブとも決定論 = ライブ 2 回 = (b) = 300 秒の出力行が完全一致。RPR のバフなしライブだけ上の注記)。
- シンクレベル (9 戦闘 `--level L`、既定トラック): RPR / SAM / GNB / PLD / DRG / VPR 90〜50、NIN 90 / 80 / 70 / 68、MNK 90 / 80 / 70、BLM 90〜60
  の 40 通りで出力行が HEAD と完全一致、失敗 0 (BLM の 1 は §20.4 の境界テスト)。Adapter の `GcdNeedsAbilityFirst` はルールの判断
  (`SyncedOgcdFirst` の oGCD 先行) を変えなかった。
- 全レベル × 全トラックのスモーク (§20.4 と同じ組み方、採用したビルド): 9 ジョブ 3,383 回 (RPR 213 / NIN 140 / MNK 310 / SAM 284 / BLM 164 / GNB 1,136 / PLD 710 / DRG 213 /
  VPR 213) で例外 0。ハーネスの規則チェックの失敗がある実行は BLM 164 / MNK 310 / DRG 213 / GNB 852 / PLD 426 (RPR / NIN / SAM / VPR は 0)、
  失敗数の合計 BLM 872 / MNK 620 / DRG 1,278 / GNB 5,112 / PLD 2,556: いずれも §20.4 / §25.3 と同じ種類 (MNK は dmu-full の「Riddle of Earth を
  使っていない」、DRG / GNB / PLD の k ≥ 5 は Hold 系トラックが全 GCD を止める組の「目標復帰後 3.5 秒以内に GCD がない」、BLM は境界テスト)
  で、抜き取り (GNB / PLD / MNK の k5〜6 の Lv100 / 70) は HEAD のビルドでも同じ件数・同じ文言。
- ゲーム内での確認は未実施。

### 28.5 入れていないもの

- 28.3 の 1 / 3 / 4 / 5 の規則 (数字は上)。GNB のノーマーシーは「1-2-3 の途中では押さない」をどの形で入れても (b) で 4,000〜7,000 下がる。
- RPR / VPR の深い地平線 (再調整しても (b) で HEAD 未満: RPR 685,919 / 690,011、VPR 705,774 / 710,291)。
- BLM の地平線 5 (現行の重みで 533,478 / 535,364、再調整していない) と、BLM のバフなしの差 (HEAD −1.4%、旧 −2.2%)。
- MNK / DRG / VPR / BLM の 6 は組み込みの SliceNodes (40 / 400 / 40 / 50) のまま測ったもので、分割幅を広げた版は測っていない (5 で下がる向きは
  広げても同じだった)。
- ノード分割の揺れ (PLD / GNB / NIN の SliceNodes の例) の解消。中断した部分木のやり直しをなくすには探索の再開を TT 以外でも保存する必要があり、
  今回は触っていない。
- `JobAnalysis` の DoT スキルの単位価値 (SAM の閃) を Core で直すこと (重みの `GaugeValue` で補正した)。
