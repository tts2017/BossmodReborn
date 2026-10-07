# agents_mnk.md

BossMod Reborn / xan Autorotation の **Monk (`MNK`) 専用エージェント指示書**。

このファイルは、`BossMod.Autorotation.xan.MNK` を実装・修正・レビューするエージェント向けの作業規約です。  
対象は `MNK.cs` の自動回し、バースト計画、範囲/単体切替、Planner連携、外部AOE予測連携です。

---

## 0. 基本方針

MNKは、単純な「リキャスト即撃ち」ジョブとして扱わない。

最優先で守るものは以下。

1. **GCD uptime**
2. **RoF / Brotherhood / PB / Blitz / Reply の20秒窓密度**
3. **Phantom Rush回数**
4. **2分バーストへのPR/Blitz寄せ**
5. **キルタイム・フェーズ境界・ダウンタイム前の吐き切り**
6. **範囲→単体、単体→範囲の自然な接続**
7. **誤予測で2分バーストを壊さない安全性**

実装判断に迷ったら、まず「GCDを止めない」「PB中に逃げさせない」「PR回数を落とさない」「信頼できない予測でRoF/BH/薬を動かさない」の順で考える。

---

## 1. 現行MNKの主要設計

### Strategy tracks

MNKは以下のTrack群を中心に動く。

- `Targeting`
- `AOE`
- `Brotherhood`
- `BurstTiming`
- `RoF`
- `OpenerRoFOffset`
- `FiresReply`
- `RoW`
- `WindsReply`
- `PB`
- `Nadi`
- `Blitz`
- `SSS`
- `FormShift`
- `Meditate`
- `TC`
- `Pot`
- `FightEnd`
- `Engage`
- `TrueNorth`
- `RotationMode`
- `EncounterHint`

`EncounterHint` はMNKの挙動を大きく変える。雑魚、ボス復帰、バースト禁止、強制許可などはここで扱う。

### RotationMode

`RotationModeStrategy.Automatic` は通常の全自動回し。

`BasicAndChakraOvercap` は通常GCDと闘気溢れ対策だけを行う補助モード。RoF/BH/PB/RoW/薬などを勝手に吐く用途ではない。

---

## 2. フォーム・GCD・優先度の不変条件

### フォーム

MNKの単体WSと範囲WSはフォームを共有する。

- `OpoOpo`
- `Raptor`
- `Coeurl`

そのため、範囲から単体へ戻る時に特別な「移行コンボ」を作りすぎない。  
`UseAOE` が false になったら、同じ `EffectiveForm` のまま単体GCD候補へ自然に戻す。

### GCDPriority

概念上の優先度は以下を保つ。

```text
Meditate < ranged filler < Basic < BasicSaver < BasicSpender < AOE
< SSS < Blitz < Reply < Phantom Rush / emergency Blitz
```

範囲条件を満たしている時は `AOE` が通常単体GCDより勝つ。  
ただし、`SSS`、`Blitz`、`Fire's Reply`、`Wind's Reply`、`PR` は状況に応じてAoEより高くなる。

### 低レベルシンク

通常GCDとAoE GCDは、未習得アクションを直接 `PushGCD()` しない。  
フォーム別の単体GCDは `BestOpoGCD()` / `BestRaptorGCD()` / `BestCoeurlGCD()` のような `Unlocked()` 付きヘルパーを通し、範囲GCDは `BestAOEGCD()` 経由で `AID.None` の時は積まない。

PB / Blitz / Nadi 系はTrackの `MinLevel` だけに頼らず、内部入口でも `Unlocked()` または専用ヘルパーで止める。  
低レベルやGauge初期化直後の `BeastChakra` は、直接添字参照せず `BeastAt(index)` のような安全ヘルパーを通す。

踏鳴開始をBeastGaugeで止める場合は、Blitz解放後だけに限定する。  
Lv50〜59のようにBlitz/Nadi未解放の帯では、`BeastChakra` の残りで踏鳴をブロックしない。Lv60以降でBlitzが解放されている場合だけ、未消化Beastがある時に次PBを止める。

### Positional

`UseAOE == true` のときは方向指定を要求しない。  
単体時のみ `Demolish` は背面、`Snap Punch` / `Pouncing Coeurl` は側面を考える。

---

## 3. 範囲回しと単体↔複数切替

### 基本AOE判定

AOE閾値は単純な敵数だけで決めない。

現在の思想は以下。

```text
BossReturn中
→ 範囲を止めて単体復帰準備

短期で単体へ戻る
→ 3体では無理に範囲しない

Opoフォーム + Shadow of the Destroyer解放済み + OpoStacksなし
→ 3体からOpo範囲を許可

Coeurlフォーム + CoeurlStacksなし + AoEが数GCD続く
→ 3体からRockbreakerを許可

それ以外
→ 4体以上で範囲
```

`AOEBreakpoint` はこの思想を維持する。

### `BossReturn`

`EncounterHint = BossReturn` は強い手動ヒントとして扱う。

- 4体以上でも単体復帰準備を優先してよい
- RoF/BH/PB/RoW/薬は自動使用しない
- 範囲火力より、次の単体ボスに良いフォーム・スタック・リソースで戻ることを優先する

### `ShortAOERemaining`

AoE終了予測には、以下だけを使う。

- `EstimatedDowntimeStart`
- 信頼済みの `EstimatedTargetEnd`
- 有効な `DowntimeIn`

単なる `EstimatedPhaseEnd` をAoE終了扱いしない。  
StateMachineのphase境界は、必ずしも敵消失やダウンタイムを意味しない。

### 3体境界

3体時は雑にフルAoEへ寄せない。

- Opo範囲は3体から許可しやすい
- Raptorは基本単体寄り
- Coeurlは `CoeurlStacks == 0` かつAoEが数GCD続く時だけ3体Rockbreakerを許可
- 短期で単体へ戻るならDragon Kick / Twin Snakes / Demolishなどの単体準備を優先

### 闘気

闘気は以下を守る。

```text
NumLineTargets >= 3
→ Enlightenment / Howling Fist

NumLineTargets <= 2
→ Forbidden Chakra / Steel Peak
```

2体で線範囲へ逃げない。

---

## 4. EncounterHint運用

`EncounterHintAllowsAutomaticBurst()` は大技管理の中心。

### 各Hintの意味

```text
Automatic
→ 通常MNK判断

Boss
→ ボス戦。通常通りバースト許可

Trash
→ 範囲回しはするが、RoF/BH/PB/RoW/薬は温存

AllianceTrash
→ 3体以上で長く残る雑魚として扱い、条件付きでバースト許可

MajorAdd
→ 大型雑魚/中ボス。ボス同様にバースト許可

BossReturn
→ ボス復帰前。単体準備を優先し、RoF/BH/PB/RoW/薬は温存

HoldBurst
→ RoF/BH/PB/RoW/薬を温存

ForceBurst
→ 雑魚/復帰ヒントを無視して自動バースト許可
```

### 重要

`Trash` / `BossReturn` / `HoldBurst` では、自動バーストを開始しない。  
Force指定はユーザー明示なので通してよいが、Automaticは通さない。

`AllianceTrash` は「この雑魚は長く残る」という手動宣言として扱ってよい。  
完全自動で長く残る保証がないなら、`ShortAOERemaining()` などの実測情報を優先する。

---

## 5. Planner / FightEnd / Downtime

### 信頼度の分離

以下の信頼度を混同しない。

```text
FightEndBurnEstimateTrusted
→ 終盤吐き切り用。StableHP または PlannerFinal のみ。

NadiProjectionEstimateTrusted
→ Nadi/PR回数予測用。StableHP / PlannerPhase / PlannerFinal を許可。

PhaseBoundaryEstimateTrusted
→ フェーズ境界・ダウンタイム用。StableHP / PlannerPhase / PlannerFinal を許可。

BurstHorizonEstimateTrusted
→ バースト有効期限用。StableHP / PlannerPhase / PlannerFinal を許可。
```

終盤吐き切りとNadi長期予測は別物。  
PlannerPhaseは終盤吐き切りには使いすぎないが、NadiのPR回数予測やフェーズ境界には使ってよい。

### `EstimatedDowntimeStart`

Plannerから「次フェーズがStartWithDowntime」と分かる場合のみ `EstimatedDowntimeStart` を立てる。

`EstimatedPhaseEnd` をそのままダウンタイム扱いしない。

### Uptime系

以下の関係を保つ。

```csharp
PlannerDowntimeIn
EffectiveUptimeIn
EffectiveBurstUptimeIn
ResourceHorizonClamp()
```

`EffectiveUptimeIn` は通常 `DowntimeIn` と `PlannerDowntimeIn` の短い方。  
`EffectiveBurstUptimeIn` はさらに `EstimatedBurstHorizon` がphase endの場合、その残り時間でclampする。

### ResourceHorizonClamp

`ResourceHorizonClamp(strategy, remaining)` は以下に使う。

- Blitz
- Fire's Reply
- Wind's Reply
- その他、抱え落ちが起こるGCDリソース

PlannerPhaseで次にダウンタイムが来るなら、戦闘終了でなくてもReply/Blitzを抱えすぎない。

### 終盤吐き切り

RoF/BH/PB/薬の「最後の窓」判定は、`trustedFightEndBurn` を使う。

弱いHP推定だけでRoF/BH/薬/PBを吐き切らない。

---

## 6. BurstPlan

`BuildBurstPlan()` はMNKの中核。ここを変更する時は、必ず以下を守る。

### targetAvailable / targetable

```csharp
targetAvailable = HaveTarget && EffectiveBurstUptimeIn > AnimLock + 20;
targetable = targetAvailable && GCD > 0;
```

RoF/BH/PB/薬など、20秒バーストを開始する判断は原則 `targetAvailable` または `targetable` を通す。

### Brotherhood固定タイミング

`useBrotherhoodFixedTimingNow` は `HaveTarget` だけで発火させない。

必ず以下のように `targetAvailable` を見る。

```csharp
var useBrotherhoodFixedTimingNow = automaticBurstAllowed
    && targetAvailable
    && strategy.Brotherhood.Value == OffensiveStrategy.Automatic
    && nextBrotherhoodIn <= AnimationLockDelay;
```

これを守らないと、Planner上のダウンタイム直前にBrotherhoodだけ漏れる。

### RoF固定タイミング

`useRoFFixedTimingNow` も `targetAvailable` を見る。

RoFは自分の20秒火力バフなので、近いうちに殴れない場合はAutomaticでは開始しない。

### Burst Timing

`BurstTiming` はRoF/BHの同期方針を明示するTrack。

```text
SynergyFixed
→ 120秒 / Planner / PTバースト合わせを優先し、床予兆では固定RoF/BHをずらさない

MeleeSafe
→ 120秒 / Planner / PTバースト合わせを使うが、1GCD以上の近接不可ではRoF/BH予定窓、RoF前PB、固定RoF/BH、BH通常Automatic発火も近接安全gateを通す

Cooldown
→ PTバースト再同期を使わず、開幕offsetと実リキャスト周期を優先する
```

`Cooldown` では `SynchronizeRoFBrotherhoodBurstSchedule()`、`PartyBurstAligned` opener再同期、scheduled ready計算を使わない。  
`RoWStrategy.Automatic` は記事7.3方針に合わせてリキャスト優先なので、2分Adaptive寄せは `OpenerTwoMinuteAdaptive` / `RoFAligned` などの明示設定だけで行う。  
`MeleeSafe` だけがRoF/BH予定窓、RoF前PB、固定RoF/BH、BH通常Automatic発火に `MajorBurstMeleeSafe()` を要求する。`SynergyFixed` は固定詰め用なので、床予兆だけで固定RoF/BHを遅らせない。

---

## 7. RoF / Brotherhood

### 基本

- RoFは60秒周期
- Brotherhoodは120秒周期
- 偶数窓はRoF/BH同期を重視
- RoFとBHがズレる時は、外部シナジーやスケジュールを見て再同期する

### Opener RoF Offset

```text
Standard78
→ 戦闘開始 +7.8秒

Early58
→ 戦闘開始 +5.8秒

PartyBurstAligned
→ 外部PTバースト推定へ寄せる
```

固定や詰めでは `PartyBurstAligned` を優先。検出が不安定なら固定offsetを使う。

### Death / raise recovery

死亡・衰弱・強衰弱後は、無理に古いスケジュールを引きずらない。  
RoF/BH/PB/Nadiの状態から復帰用スケジュールを作る。

---

## 8. Perfect Balance / Nadi / Blitz

### PBの基本

Automatic PBは原則 **Opo GCD後** に使う。  
実装上は `CurrentForm == Form.Raptor` が「直前にOpo GCDを撃った後」を表す。

`pbPreferred = CurrentForm == Form.Raptor` を基本にする。  
ただし、BlitzがRoF内に入らなくなる、終盤吐き切り、PB回収失敗などのdeadlineでは例外を許可してよい。

### RotationMode復帰PB

`BasicAndChakraOvercap` から `Automatic` へ戻す時にPB開始補正を入れる場合でも、通常PBの安全条件を雑にバイパスしない。

最低限、以下を守る。

```text
PB設定がAutomatic
獣チャクラなし
PBバフなし
PBが使用可能
自動バースト許可中
対象あり
近接3GCD以上が入る見込み
RoF/PB窓内、または明確なdeadline
```

復帰補正でOpo後条件を外す場合は、必ず近接安全・バースト窓・PB使用数上限を別条件で保証する。  
復帰直後だからという理由だけでPBを押すと、Nadi/Blitz周期とRoF内Blitzが崩れる。

### 偶数ダブルルナー

偶数ダブルルナーの基本は以下。

```text
RoF/BH 約4〜6秒前
かつ Opo GCD後
→ 1回目PB開始

RoF/BH中
→ 2回目PB
```

1回目PBは、RoF/BH同期窓で `4.0f <= nextRoFIn <= 6.0f` かつ `CurrentForm == Form.Raptor` の時に開始する。  
記事7.3方針では、PB後に2GCDを回してからRoF/BHへ入れる形を優先する。

噛み合わない時はRoF/BHを遅らせない。  
Wind's Replyがある偶数窓では、Opo後PBを逃してRoF/BHが遅れるより、`CurrentForm == Form.Coeurl`、つまり2段目後PBの救済を許可する。これは常用ではなく、RoF/BH定刻維持のためのフォールバック。

以前の8.4〜2.4秒許容窓や7.4〜3.0秒理想窓へ戻さない。

### 2回目PB

2回目PBはRoF/BH中の最初の都合の良いOpo後が基本。

```text
1回目Blitz
→ Formless Opo
→ Fire's Reply / Wind's Reply
→ Formless Opo
→ 2回目PB
→ 2回目Blitz
```

2回目PBでは、RoF内に3PB GCD + Blitzが入るかを見る。  
遅れているならdeadlineを許可する。

### 奇数バーストPB

奇数バーストのAutomatic PBは、RoF発動後のOpo GCD直後に使う。  
RoFリキャスト2〜7秒前の事前PBは使わない。

実装上は以下を守る。

```text
activeRoFWindow == true
CurrentForm == Form.Raptor
automaticPBTargetUses == 1
evenPBBurstWindow == false
```

Solar / Lunar / Automatic のNadi選択に関係なく、奇数PBの発火タイミングはRoF後のOpo後に揃える。  
Nadi選択はPB内のフォーム選択には影響してよいが、RoF前PBを復活させない。

### Nadi Automatic

`NadiStrategy.Automatic` は、推定キルタイムでSolar Lunar / Double Lunarを選ぶ。

原則は以下。

```text
Solar LunarでPhantom Rush回数が増える
→ LunarSolar寄せ

PR回数が同じで、Double Lunarの方が2分バフへPR/Blitzを寄せられる
→ Double Lunar寄せ
```

`FightEndBurnEstimateTrusted` ではなく、Nadi用の信頼度を使う。  
PlannerPhaseはNadi予測に使ってよい。

### PR回数予測

`ProjectedPhantomRushCount()` を変更する時は、現在バーストの二重計上に注意。

特に以下に注意。

- RoF前PB中の現在バーストを未来バーストとして二重に数えない
- RoF中の `_activatedRoFBursts` と次の60秒窓のindexをズラさない
- ダウンタイムを跨ぐ未来PBは過大評価しやすい

完全なPlanner-aware PR予測にする場合は、future burstが実際にtargetableかを見る。

### Blitz

Blitzは以下を守る。

- RoF中優先
- RoFに入らないなら期限直前まで温存
- `Multi` は複数対象に当たるまで温存
- `MultiRoF` はRoF中かつ複数対象に当たるまで温存
- `ResourceHorizonClamp()` でフェーズ前・戦闘終了前の抱え落ちを防ぐ

PRは最優先級。  
ただし、無理な保持でPB/Nadi周期を壊さない。

---

## 9. Fire's Reply / Wind's Reply

### Fire's Reply

AutomaticではOpo後に使う。  
理想は `CurrentForm == Form.Raptor` のタイミング。

`FRStrategy.Ranged` は、遠隔GCDが必要になるまで温存する。

`ResourceHorizonClamp()` を必ず通す。  
期限切れ、RoF終了、フェーズ終了前では抱えない。

### Wind's Reply

`WRStrategy.Automatic` は遠隔GCD用途として扱う。  
`PreDowntime` は次のダウンタイムの2GCD以上前までに使う。

`EffectiveBurstUptimeIn` と `ResourceHorizonClamp()` を使い、PlannerPhaseのダウンタイム前にも吐き切る。

---

## 10. Riddle of Wind

RoWは90秒リキャストなので、120秒バースト合わせだけを盲目的に行わない。

`RoWStrategy.Automatic` は以下を守る。

```text
開幕で使う
以後は原則リキャスト毎に使う
2分バーストに無理に45秒保持しない
対象なし、温存ヒント、無敵、十分な殴り時間なしなどのハード条件は守る
```

2分合わせをしたい場合は、Automaticに隠さず `OpenerTwoMinuteAdaptive` / `RoFAligned` / `OpenerTwoMinuteThenCooldown` などを明示選択する。

`OpenerTwoMinuteAdaptive` は以下を守る。

```text
開幕で使う
2分までは待機候補
以後は、即撃ちした場合と2分合わせした場合の使用回数を比較
使用回数が増えるなら即撃ち
使用回数が同じならRoF/BHへ寄せる
```

`EstimatedBurstHorizon` / `EstimatedFightEnd` がある時ほどAdaptive判断は強い。  
キルタイム不明で回数落ちが怖い場合は `OpenerCooldown` も候補。

---

## 11. Potion

薬は完全自動で万能にしない。キルタイム指定を尊重する。

候補は以下。

```text
PreBuffs
SixMinuteBurst
EightMinuteBurst
TwoAndEightMinuteBursts
TwoAndTenMinuteBursts
OpenerSixAndTwelveMinuteBursts
FiveAndTenMinuteBursts
SixAndTwelveMinuteBursts
Now
Manual
```

`ShouldPotionPreBuffs()` は以下を必ず見る。

- `HaveTarget`
- `EncounterHintAllowsAutomaticBurst(strategy)`
- `EffectiveBurstUptimeIn > AnimLock + 20`
- `PotionReadyIn()`
- Brotherhoodの次窓
- LastPotionWindow

雑魚終わりやBossReturnで薬を吐かない。

---

## 12. Six-Sided Star / Form Shift / Meditate / Thunderclap

### SSS

SSSは主にダウンタイム前、または外部AOEで近接離脱が確定している時に使う。

`EffectiveDowntimeIn` はアクションのapplication delayを考慮する。  
GCD停止を避けるためのスキルであり、通常火力目的で雑に使わない。
RoF/BH中の単なるバースト〆目的では使わない。通常GCD、Blitz、Fire's Reply、Wind's Reply、PRを押せるならそちらを優先する。

### Form Shift

非戦闘、開幕前、ダウンタイム復帰で使う。  
PB中は使わない。

### Meditate

- `Safe`: 非戦闘中、または近くに敵がいなければ使う
- `Greedy`: 非戦闘中、または近接範囲に敵がいなければ使う
- `Force`: 即使用
- `Delay`: 使わない

### Thunderclap

`GapClose` は近接外からの復帰用。  
外部AOE/ForbiddenZone予測がある時は、距離だけで突進しない。

最低限、以下のgateを通す。

```text
近接不可予測なし
または CanThunderclapSafely
```

ボス足元や近接範囲が危険な時にThunderclapを自動queueしない。  
TCはGCD復帰用であり、危険な近接地点へ移動するためのボタンではない。

---

## 13. Splatoon / 外部AOE予測連携

MNKローテがSplatoonを直接読む設計にしない。

推奨構成は以下。

```text
Splatoon preset / script
→ Bridge / IPC Adapter
→ ExternalDangerZone
→ BossMod ExternalAOEProvider
→ MeleeUptimePredictor
→ MNK rotation
```

### MeleeUptimePredictionの不変条件

近接予測では、以下を必ず分離する。

```text
1. 自分の現在位置から今すぐ近接GCDを撃てるか
2. ボス周囲に安全な近接地点が存在するか
3. 自分の現在位置から次GCDまでにその安全地点へ到達できるか
```

`CanMeleeNow` は「安全近接点がどこかに存在する」ではない。  
必ず現在位置基準で判定する。

```csharp
var canMeleeNow =
    Player.DistanceToHitbox(target) <= 3
    && !ForbiddenAt(Player.Position, 0);
```

安全近接点の存在は `SafeMeleePointExists` として別に持つ。  
移動可能性を考慮する場合は、`CanReachSafeMeleePointAt(target, at)` のような到達判定を追加する。
到達判定では、距離だけでなく簡易の経路安全も見る。  
狭い安置を拾うため、近接サンプルは必要に応じて複数リング・24点以上にしてよい。

### 禁止

- 全Splatoon要素を危険AOE扱いしない
- SafeHintを確定安全として扱わない
- Generic/Unknown信頼度でRoF/BH/薬/Nadiを動かさない
- BossMod native moduleと矛盾した時にSplatoonを優先しない

### ExternalDangerZone

外部AOEは、少なくとも以下を持つ。

```csharp
ExternalZoneIntent Intent;        // Danger / SafeHint / Bait / VisualOnly
ExternalZoneConfidence Confidence;
ExternalZoneShapeKind Shape;
uint TerritoryId;
string Namespace;
float ActivatesAt;
float ExpiresAt;
bool AffectsPlayer;
bool AffectsMeleeUptime;
```

### 信頼度

```text
BossMod native module
> Trusted Splatoon script
> Trusted Splatoon preset
> Generic Splatoon
> Unknown
```

### MNKに渡す値

ローテ側ではAOE形状を直接見ない。  
`MeleeUptimePrediction` だけを見る。

```csharp
bool CanMeleeNow;
bool CanMeleeNextGCD;
bool SafeMeleePointExists;
float MeleeLossIn;
float MeleeResumeIn;
float SafeMeleeWindow;
float ForcedOutDuration;
ExternalZoneConfidence Confidence;
```

`MeleeLossIn` は「現在位置または到達可能な近接地点から見て、いつ近接GCDが破綻するか」を表す。  
`SafeMeleeWindow` を単に `MeleeLossIn` の別名にしない。  
SSS判定では `ForcedOutDuration` を見て、一瞬の離脱と長い離脱を分ける。

### 反映優先度

最初に使うべきは低リスク用途。

```text
1. SSS
2. Fire's Reply / Wind's Reply
3. Thunderclap復帰
4. PB開始抑制
5. RoF/薬開始抑制
6. Nadi/長期バースト再構成
```

RoF/BH/薬/Nadiを動かすのは、BossMod nativeまたはTrusted Script以上の信頼度だけにする。

### PB開始抑制

PBは3GCDを入れたい。  
外部AOE予測で近接不可が来る場合、PB開始は以下を要求する。

```csharp
EffectiveMeleeBurstUptimeIn > AttackGCDLength * 3 + AnimationLockDelay
```

RoF/薬はより長く見る。

```csharp
EffectiveMeleeBurstUptimeIn > AttackGCDLength * 6 + AnimationLockDelay
```

BrotherhoodはPTバフなので、個人の一時離脱だけで必ず止めるとは限らない。

### RoF / 薬

RoFと薬は個人火力への依存が大きい。  
対象やフェーズとしては殴れても、近接GCDがすぐ破綻する場合は開始を抑制する余地がある。

目安は以下。

```text
RoF / 薬
→ 5〜6GCD程度の近接安全窓を要求

Brotherhood
→ PTバフなので、個人の短時間離脱だけではRoF/薬より緩く扱ってよい
```

ただし、`LastPotionWindow` や `LastRoFWindow` のような信頼済み終盤吐き切りでは、抱え落ち回避を優先してよい。

---

## 14. 複数グループでのスキル使い回し

完全自動で「次グループの重さ」を読む設計にはしない。  
複数グループの大技管理は `EncounterHint` とPlanner/外部Hintで補助する。

推奨運用。

```text
普通のID雑魚
→ Trash

硬いアライアンス雑魚
→ AllianceTrash

大型雑魚 / 中ボス / ボス扱いAdd
→ MajorAdd

雑魚後すぐボス復帰
→ BossReturn

何が何でも吐きたい
→ ForceBurst
```

`Trash` / `BossReturn` では範囲GCDは回すが、RoF/BH/PB/RoW/薬は温存する。

---

## 15. 実装時のチェックリスト

### コンパイル前レビュー

- `targetAvailable` と `targetable` の使い分けは正しいか
- AutomaticのRoF/BH/PB/RoW/薬が `EncounterHintAllowsAutomaticBurst()` を通っているか
- Brotherhood固定タイミングが `targetAvailable` を見ているか
- `BurstTiming.Cooldown` がPTバースト再同期・PartyBurstAligned再同期を使っていないか
- `RoWStrategy.Automatic` が2分Adaptive保持ではなくリキャスト優先になっているか
- `BurstTiming.MeleeSafe` のRoF/BH予定窓、RoF前PB、固定RoF/BH、BH通常Automatic発火が `MajorBurstMeleeSafe()` を通るか
- `UpdateAutomaticPBBurstTracking()` のRoF前PB窓判定が `BuildBurstPlan()` と同じ `roFBurstWindowMeleeSafe` を通るか
- `BurstTiming.SynergyFixed` で床予兆により固定RoF/BHをずらしていないか
- `EstimatedPhaseEnd` を無条件にダウンタイム扱いしていないか
- `ResourceHorizonClamp()` をBlitz/Replyに通しているか
- `FightEndBurnEstimateTrusted` と `NadiProjectionEstimateTrusted` を混同していないか
- 3体AoEで単体移行準備を壊していないか
- 低レベルシンク時に未習得の通常GCD / AoE GCD / PB / Blitz をqueueしていないか
- `BeastChakra` の参照が `BeastAt(index)` などの安全ヘルパーを通っているか
- Lv50帯の踏鳴を `BeastChakra` で止めていないか、Lv60以降では未消化Beastだけで止めているか
- Lv70以降のBH窓が、BH発動直前/有効中/偶数RoF前PBを2PB窓として扱えているか
- `BossReturn` で4体以上でも単体準備できるか
- PBをOpo後に優先しているか
- 奇数バーストPBがRoF発動後のOpo後で、RoFリキャスト2〜7秒前に事前発火していないか
- RotationMode復帰PBが `pbMeleeSafe` / `pbRoFWindow` / `pbTimingAllowed` を不必要にバイパスしていないか
- 偶数窓の1回目PBがOpo後かつRoFリキャスト4〜6秒で入るか
- Wind's Replyがある偶数窓で、RoF/BH定刻維持のための2段目後PB救済が働くか
- 偶数2回目PBのBlitzがRoF内に入るか
- `CanMeleeNow` が現在位置基準で、`SafeMeleePointExists` と混ざっていないか
- Thunderclapが近接不可予測を無視して危険な近接地点へ突っ込まないか
- SSS判断が `ForcedOutDuration` を見ているか

### 実戦テスト

最低限、以下を確認する。

1. 開幕RoF offset 5.8 / 7.8 / PartyBurstAligned
2. 2分偶数ダブルルナー
3. 1分奇数ソーラー/ルナー選択
4. 8分前後のPR回数
5. 死亡/蘇生後の偶数PR復帰
6. Planner対応ボスの非最終フェーズダウンタイム
7. Planner final + HP推定短縮
8. 3体→1体、4体→1体、BossReturn
9. Trash / AllianceTrash / MajorAdd / HoldBurst / ForceBurst
10. Blitz Multi / MultiRoF
11. Wind's Reply PreDowntime
12. External AOEによるSSS/Reply/TC/PB抑制

---

## 16. 変更してはいけない/慎重に変える箇所

以下はMNKのログ性能に直結する。

- `EvenPreRoFPBStartThreshold = 6.0f`
- `AutomaticPBBurstIndex()`
- `AutomaticPBUsesForBurst()`
- `DetermineNadiCyclePhase()`
- `AutomaticNadiShouldUseLunarSolar()`
- `ProjectedPhantomRushCount()`
- `BuildBurstPlan()`
- `ResourceHorizonClamp()`
- `EncounterHintAllowsAutomaticBurst()`
- `AOEBreakpoint`
- `TransitionToSingleSoon()`
- `ShortAOERemaining()`

変更する場合は、必ず偶数ダブルルナー、奇数窓、PlannerPhase、BossReturn、Trash温存の全ケースを確認する。

---

## 17. 推奨デフォルト

上位狙いの一般デフォルト。

```text
RotationMode: Automatic
RoF: Automatic
Brotherhood: Automatic
OpenerRoFOffset: PartyBurstAligned / Standard78
PB: Automatic
Nadi: Automatic
Blitz: Automatic
Fire's Reply: Automatic
Wind's Reply: Automatic or PreDowntime
RoW: Automatic / OpenerTwoMinuteAdaptive
Potion: キルタイム指定
FightEnd: Automatic
SSS: Automatic
FormShift: Automatic
Meditate: Safe
Thunderclap: GapClose
EncounterHint: Automatic / 手動でTrash・BossReturn等を指定
```

キルタイム不明ではPR回数を落とさないことを優先。  
固定詰めではDouble Lunarと薬タイミングをキルタイムごとに合わせる。

---

## 18. レビュー時の合格基準

このMNKの修正は、以下を満たせば合格。

- 単体回しがフォーム/スタックを壊さない
- 4体以上でフル範囲、3体で安全な混合回しになる
- BossReturnで単体復帰準備ができる
- Trashで大技を温存できる
- MajorAdd/ForceBurstで大技を吐ける
- PlannerPhaseのダウンタイムがバースト開始・Blitz/Reply吐き切りに反映される
- 弱いHP推定でRoF/BH/薬/PBの最終吐き切りをしない
- Nadi AutomaticがPR回数を落としにくい
- RoW Automaticが原則リキャスト毎で、2分合わせは明示Adaptive設定だけで行われる
- SSSがバースト〆目的ではなく、離脱/フェーズ終了/終盤用途に限定されている
- Splatoon/外部AOEは低リスク用途から段階的に反映する

---

## 19. 重要な短い結論

MNKは「今押せる強いボタンを押す」ジョブではない。

```text
Opo後PB
偶数ダブルルナー
PR回数
RoF/BH 20秒密度
Plannerダウンタイム
範囲→単体の接続
外部AOEによる近接不可
```

これらを同時に守るジョブとして扱うこと。
