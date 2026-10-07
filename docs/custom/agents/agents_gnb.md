# agents_gnb.md

# Akechi GNB 作業ルール / Codex・修正担当向け指示書

## 目的

BossMod Reborn / `AkechiGNB.cs` のガンブレ autorotation を、黄金7.x想定の上位ログ狙いで安全に改善する。

このファイルは実コードではなく、Codex・修正担当・レビュー担当が守るべき作業ルールである。

---

## 0. 最重要方針

### バグを生成しない

- 未定義メソッド、未定義変数、曖昧な名前、重複定義を作らない。
- 既存の `AkechiGNB.cs` 内に同名メソッド/フィールドがないか確認してから追加する。
- 型はBossMod側の既存ヘルパーに合わせる。
- `ActionID` と `AID` の暗黙変換を期待しない。
- 既存の `Gauge` / `GFCharges` / `Charges` などを曖昧参照にしない。

### 修正は最小差分で行う

- 既存の動いているNM/BF/GF/DD/Reignロジックを壊さない。
- 仕様変更のために大規模に書き換える場合は、必ず周辺込みの完全差し替えブロックを提示する。
- 「ここだけ追加」「このメソッドを丸ごと差し替え」の形で示す。

### 初心者向けに出力する

ユーザーはC#初心者のため、修正指示は必ず以下のように明確にする。

- どのメソッドを差し替えるか
- どのenumに追加するか
- どの行の直後に貼るか
- どの条件を何に変更するか
- どこからどこまで削除するか

曖昧な「この辺に追加」は禁止。

### ローテーションはスキル回しのみ自動化する

- 移動は手動。
- Splatoonや外部ヒントを使う場合も、移動命令やスキル命令を直接送らせない。
- 外部情報は「殴れる/殴れない時間窓」などのヒントに限定する。
- 最終的なスキル判断はBossMod側のPlannerに残す。

---

## 1. GNB 7.x 基本前提

### No Mercy

- 60秒リキャスト。
- 20秒間のバーストバフ。
- 原則として定刻運用。
- NMはズラさない。
- 遅らせるのは「押せない」「殴れない」「プランナー上明確に得」な場合のみ。
- NMを押すGCDでは `Zone` / `Bow Shock` を同時weaveしない。
- NMは原則、GCD後の前weaveで押す。
- `Wicked Talon` 直後にNMを押す場合、ContinuationはNM後に入れる。

### Bloodfest

- 60秒リキャスト。
- ソイル+3。
- 30秒間最大ソイル拡張。
- `Ready to Reign` 付与。
- 原則として `Bloodfest → No Mercy` の順番を守る。
- BFはNM直前に合わせる。
- BFが押せるのにNMだけズレる、またはNMだけ先に出る挙動を作らない。

### ソイル

- 通常最大3。
- Bloodfest中は最大6。
- `Gnashing Fang`、`Burst Strike`、`Fated Circle` はソイルを1消費。
- `Double Down` はソイルを2消費。
- `Savage Claw` / `Wicked Talon` / Reign comboはソイルを消費しない。
- NM窓で最低限欲しいソイルは `GF1 + DD` で3。
- Bloodfestで+3されるため、NM起動条件にソイル4以上などの過剰条件を入れない。

### Gnashing Fang

- GF1はソイル1消費。
- `Savage Claw` / `Wicked Talon` はGFコンボ継続。
- `No Mercy` 3GCD前GF1を最重要アンカーとして扱う。
- 可能なら `NM-3GCD GF1 → NM内にContinuation/高威力を吸わせる`。
- NM前GF1が無理な場合は、通常コンボ完走やBS調整で1GCD程度のズレを許容する。
- GFコンボ中は必ず完走を優先する。
- GFを早撃ちしてNM前アンカーを壊さない。

### Reign of Beasts

- Bloodfest後の `Ready to Reign` で使用。
- `Reign of Beasts` / `Noble Blood` / `Lion Heart` はソイルを消費しない。
- NM中はGF1よりReign優先になりやすい。
- ただし短い殴り窓や戦闘終了直前では、DDやSonicとの比較を必ず行う。
- Reign comboを始めたら完走を優先する。

### Double Down

- NM中の最重要GCD級。
- ソイル2消費。
- 短い殴り窓ではReign開始よりDD即撃ちが勝つ場合がある。
- ダウンタイム直前/討伐直前はDDを候補比較から外さない。

### Sonic Break

- NM中の高優先GCD。
- DoT完走できない場合は価値が落ちる。
- 複数体では敵HPとDoT完走可否を見て対象を選ぶ。
- NM中ではDDの次点級として扱う。

### Continuation

- GF系、Burst Strike、Fated Circle後のContinuationを取りこぼさない。
- NMを押すGCDでは、NMを前weave、Continuationを後weaveに寄せる設計を維持する。
- `Hypervelocity` / `Fated Brand` が通常時やNM前調整で失われないようにする。

---

## 2. 上位狙いローテ優先順位

通常の大方針:

1. NMを60秒周期に合わせる。
2. BFをNM直前に合わせる。
3. NM-3GCD GF1を可能な限り作る。
4. NM内にDD、Reign combo、Sonic Break、GF continuationを最大限入れる。
5. Zone / Bow Shock はNM押下GCDを避け、NM中2GCD以上経過後など安全なweaveに寄せる。
6. ソイル溢れを避けるが、NMアンカーを壊してまで通常時BSを撃たない。
7. 討伐直前はリソースを吐き切る。

NM中の優先目安:

1. Double Down
2. Reign of Beasts combo
3. Sonic Break
4. GF combo / Continuation
5. Burst Strike / Hypervelocity
6. 通常コンボ

ただし、短い残り殴り窓では以下を比較する。

- DD即撃ち
- Sonic Breakが完走するか
- Reign comboを3GCD完走できるか
- GF comboを完走できるか

---

## 3. 通常時ソイル運用

- 通常時はsoil3になっただけで `Burst Strike` を撃たない。
- 許可する通常時BSは主に以下:
  - ソイル溢れ防止
  - NM前GF1調整
  - 通常コンボ2段目 + soil3 → BS + HV → Solid Barrelでsoil3に戻す形
- soil2で通常時BSを撃つ挙動は原則NG。
- NM前調整でBSを撃つ場合でも、NM-3GCD GF1が可能ならGF1を優先。
- BF直前の過剰ソイル保持でNMが遅れるのはNG。

---

## 4. AoE / 複数体ルール

- `Fated Circle` は2体以上から候補に入れる。
- 通常AoEコンボ `Demon Slice` / `Demon Slaughter` はLv94/100基準では原則3体以上を目安にする。
  - 低レベル帯では既存仕様に合わせて2体以上でも可。
- `Double Down` は対象数に関係なく強い。複数体では必ず候補に残す。
- Reign comboは対象数に関係なく強いが、複数体では巻き込み対象を選ぶ。
- `Sonic Break` は複数体でも使えるが、DoT完走と敵HPを確認する。
- GF comboは3体以上では抑制候補。
- FullモードのPlanner74にもAoE候補を入れる。
  - `DemonSlice`
  - `DemonSlaughter`
  - `FatedCircle`
- Planner74にAoE候補を追加する場合は、以下を全て更新する。
  - `Planner74GCD`
  - `Planner74Potency`
  - `Planner74ApplyGCD`
  - `Planner74QueuePlannedGCD`
  - `Planner74BuildGCDCandidates`
- Planner74が単体GCDをVeryHighで投げ、下段AoE fallbackが負ける構造を放置しない。
- 単体↔複数切替ではGF/Reignコンボを途中破壊しない。
- 複数グループでは、Player中心AoEの条件をmainTarget依存にしすぎない。
  - `Fated Circle` / `Double Down` / `Bow Shock` / Demon系はPlayer周囲の敵数を見る。
  - mainTargetが離れているだけでPlayer周囲AoEを撃たない事故を避ける。

---

## 5. Cooldown Planner / Boss timeline連携

- BossMod RebornのCooldown Plannerが対応しているボスでは、内部タイムラインを利用してよい。
- ただしローテーションへ直接命令しない。
- タイムライン情報はHintBusへ変換する。

推奨経路:

```text
BossMod Cooldown Planner / PlanExecution
  → AkechiGNBPlanner74HintBus
  → TargetableBySlot
  → Planner74
```

Planner74へ渡す情報:

- 指定時間から殴れない
- 指定時間から殴れる
- ダウンタイム開始/終了
- 必要ならフェーズ名

運用ルール:

- NM/GF/DD/Reign/Sonicの最終判断はPlanner74が行う。
- `PlanExecution.EstimateTimeToNextDowntime()` 等を使う場合、毎フレーム古いBossTimeline sourceをClearして再投入する。
- 古いwindowを残さない。
- horizonは `Planner74HorizonSlots * SkSGCDLength + 余裕分` に収める。
- 完全対応する場合は、未来n秒のTargetable window一覧を返すAPIを追加するのが理想。

---

## 6. Splatoon連携安全ルール

### 禁止

Splatoonから直接スキル命令を送らない。

禁止例:

- No Mercyを押せ
- GFを押せ
- 移動しろ
- ターゲットを変えろ

### 許可

Splatoonから送ってよいもの:

- `DowntimeRel`
- `UptimeRel`
- `TargetWindowAbs`
- `PhaseNow`
- `PhaseAbs`
- `ClearSplatoonHints`

### IPC Bridge安全ガード

IPC Bridgeを有効化する場合は安全ガードを必ず入れる。

推奨上限:

```csharp
MaxSplatoonLookahead = 90f;
MaxSplatoonWindowDuration = 30f;
MaxSplatoonPriority = 180;
```

運用ルール:

- 戦闘外では受け付けない。
- Clockが古い場合は受け付けない。
- `duration <= 0` は拒否。
- durationが長すぎる場合は拒否。
- startが過去すぎる/未来すぎる場合は拒否。
- 戦闘終了時、死亡時、wipe時はSplatoon由来hintをClearする。
- Manual sourceを最優先、BossTimelineを次点、Splatoonはその下に置く。
- Splatoonヒントは毎フレーム大量送信しない。
  - ギミック検出時に1回
  - フェーズ変更時にClearして再送
  が基本。

---

## 7. Planner74 修正ルール

Planner74を修正する場合、以下をセットで確認する。

### `Planner74GCD`

新しいGCDを追加したら、以下を全て更新する。

- ApplyGCD
- Potency
- Queue
- 候補生成

### `Planner74Input`

- 新しい判断材料を入れる場合、`BuildPlanForCurrentState` で必ず値をセットする。
- 例:
  - `Targets5y`
  - `NormalAOEThreshold`
  - `CanUsePotionWindow`
  - `DowntimeIn`

### `Planner74State`

- 状態遷移に必要なものだけ入れる。
- 実行時だけ必要なActorやStrategyValuesをStateに入れない。

### `Planner74BuildGCDCandidates`

- 早期returnを増やしすぎない。
- DD / Reign / Sonic / GF の比較が必要な場面で、片方だけreturnして他候補を消さない。
- 短い殴り窓や討伐直前ではDD候補を残す。

### `Planner74ApplyGCD`

- ソイル消費、コンボ段階、リキャスト更新を正確に行う。
- GF1だけソイルを消費する。
- GF2/GF3はソイルを消費しない。
- Reign comboはソイルを消費しない。
- Double Downはソイル2消費。
- Burst Strike / Fated Circleはソイル1消費。

### `Planner74ApplyAutoWeaves`

- BFはNM直前。
- NM押下GCDではZone/Bow禁止。
- Wicked Talon後NMの場合、ContinuationをNM後へ寄せる。
- Zone/BowはNM中2GCD以上経過後など安全なタイミングにする。

### スコア値

- 実威力と評価補正を混同しない。
- 実威力が公式値と違う場合は、必ず「補正値」であるとコメントする。
- 可能なら `BasePotency` と `ScoreValue` を分ける。

---

## 8. Force / Manual系ルール

- UI説明が「ソイルn以上」なら条件は `Ammo >= n` にする。
- `Ammo == n` にしない。
- Force系はユーザーの手動意図を尊重する。
- Delay系は必ず尊重する。
- ただしコンボ継続中のGF/Reign/Solid等は壊さないように注意する。
- Forceであっても、対象が存在しない、スキル未解放、リキャスト不可、ソイル不足なら撃たない。

---

## 9. Potion / 薬ルール

- 薬はNo Mercy、GF、DD、Reignと合わせる。

### 8分薬

- 次のNMが8:00帯に来るなら、NM前GFの1GCD前を狙う。
- GFが無理ならNMの1GCD前。

### 6分薬

- 6分付近のNM前に同様。

### 2+8分薬

- 2分と8分のNM前に使用。

### `AlignWithRaidBuffs`

- シナジー窓の有無を見て判断。
- ただしNM回数を減らす薬待ちはしない。

### Potion index config error対策

- `Definition()` のTrack順、`SharedTrack.Potion`、`AddOption` のindex指定を触るときは慎重に確認する。
- Potion index errorを再発させない。

---

## 10. 死亡・復帰・ズレ補正

- 死亡後はNM/BF/GFの定刻が崩れるため、`AlignBurstAfterDeath` 系の状態を尊重する。
- 復帰後は無理に即NMせず、BF/NM/GF/DD/Reignの再同期を優先する。
- ただしNMが押せる状態で過剰に待つのはNG。
- Weakness / Brink of Death中の薬・バースト扱いは慎重にする。
- 手動BF+NM開幕からautoに引き継いだ場合、GF1とContinuationを拾えるようにする。

---

## 11. ダウンタイム・ターゲット不可

- `targetable=false` のGCD slotでは、Planner74は原則 `None` のみ。
- ただしBF/NMなどself/hostileの扱いには注意する。
  - Bloodfestがhostile target必須なら対象不在時に押せない。
  - No Mercyはselfなので対象不在でも押せるが、押して得かはPlannerで判断する。
- NMは定刻優先だが、押しても殴れないなら待つ判断も必要。
- ターゲットが戻った最初のGCDで、BF/NMを再開できるようにする。
- 学習式target lossは周回向け。
- Cooldown Planner対応ボスではBossTimelineヒントを優先する。
- Splatoonヒントは安全ガード付きで補助扱いにする。

---

## 12. コンパイル事故防止チェックリスト

修正後は必ず以下を確認する。

- 同名classが重複していない。
- 同名partial/namespace衝突がない。
- `Gauge` / `GFCharges` / `Charges` などが曖昧参照になっていない。
- `AID` と `ActionID` を混ぜていない。
- `ActionType.Action` のような存在しない定義を使っていない。
- `ActionReady` overloadの引数数を間違えていない。
- `CurrentCharges` を含むタプル型の要素数を間違えていない。
- `BuildBurstCandidates` / `ChooseBestBurstAction` など未実装メソッドを呼ばない。
- `LastPotionUseAt` / `Planner74PrepActive` など未定義フィールドを作らない。
- usingを追加した場合、既存型と名前衝突しない。
- enumに値を追加した場合、switchで未処理になっていないか確認する。
- `QueueGCD` / `QueueOGCD` のtargetが正しいか確認する。
  - Self中心AoEは `Player`
  - 敵対象スキルは `mainTarget` または `BestSplashTarget`
- 優先度が既存Queueを潰しすぎていないか確認する。
- Delay設定を無視していないか確認する。
- Force設定がUI説明と一致しているか確認する。

---

## 13. テスト観点

### 開幕

- 通常1段目 + BF
- 通常2段目 + NM
- auto引き継ぎ
- GF1が出る
- ContinuationがNM後に入る
- NM押下GCDでZone/Bowを撃たない

### 1分

- BF → NMが定刻で出る
- NMが1GCDずつズレない
- NM前GF1が可能なら出る

### 2分

- 薬設定がある場合、薬→GF/NMの順が崩れない
- シナジー合わせでNM回数を減らさない

### 通常時

- soil3になっただけでBSしない
- soil2で通常時BSしない
- 通常コンボ2段目 + soil3 → BS + HV → Solid Barrelができる
- HVを取りこぼさない

### NM中

- DDが高優先
- Reignが正しく始まり完走する
- Sonic Breakが入る
- GFコンボが壊れない
- Zone/BowがNM押下GCDで出ない

### NM終了後

- BF効果が残り、GF1 stack1 + soil1以上ならGF1を使える
- ただし早すぎるGF1で次NM前GFを壊さない

### 2体

- Fated Circleが候補になる
- 通常コンボが不要にDemonへ寄りすぎないか確認する

### 3体以上

- Demon Slice / Demon Slaughterへ移行する
- Fated Circleを使う
- GFを無駄に使わない
- Double Down / Bow ShockをPlayer周囲基準で使う

### 単体→複数

- GF/Reignコンボを途中で壊さない
- 通常コンボのFinish/Break設定が効く

### 複数→単体

- Demon comboを引きずりすぎない
- ソイル溢れを避ける
- Burst Strikeに戻れる

### ダウンタイム

- 消える直前にGF/Reignを始めない
- DD/Sonicを無駄撃ちしない
- 戻り直後にNM/BFを再開できる

### FightEnd

- 討伐直前にリソースを吐く
- HPロックやフェーズ移行で誤爆しないよう設定運用できる

---

## 14. 回答時のルール

Codex/修正担当がユーザーへ返す場合:

- 「できます」だけで終わらない。
- 必ず変更場所を示す。
- 大きな修正は以下の形式にする。

例:

1. `enum Planner74GCD` に追加
2. `Planner74Potency` を丸ごと差し替え
3. `Planner74ApplyGCD` のswitchに追加
4. `Planner74QueuePlannedGCD` のswitchに追加
5. `Planner74BuildGCDCandidates` に候補追加

断片コードだけ出す場合も、周辺行を含める。

ユーザーが「精査して」と言った場合は、以下の順に確認する。

1. コンパイルリスク
2. ローテ破綻リスク
3. 上位狙い火力リスク
4. 実戦事故リスク

---

## 15. このファイルの扱い

- このファイルはMarkdown形式の作業指示書。
- 実コードではないため、ローテーション機能はこのファイル単体では変わらない。
- Codexや作業者への指示書として使う。
- 実装時は必ず `AkechiGNB.cs` 本体に反映する。
