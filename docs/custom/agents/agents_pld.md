# agents_pld.md — AkechiPLD / BossMod Autorotation 作業ルール

このファイルは、Codex に `AkechiPLD.cs` を修正・拡張させるための作業ルールです。
対象は **BossMod Reborn / Autorotation / Akechi PLD** です。目的は、ナイトのスキル回しを安全に自動化しつつ、ログ上位を狙える 60 秒バースト軸・Proc 管理・単体/範囲切替を壊さないことです。

---

## 0. 最優先方針

1. **コンパイル成功を最優先**する。
   - 疑似コード、未定義メソッド、未定義変数、TODO だけの実装は禁止。
   - 既存の `AkechiTools<AID, TraitID>` の API を確認してから使う。
   - 似た名前の helper を推測で使わない。既存コードに存在しない helper は追加するか、既存 API で代替する。

2. **1 回の修正は小さく、差分の意図を明確にする。**
   - まとめて巨大な再設計をしない。
   - 変更した理由、影響範囲、テスト観点を必ず説明する。

3. **既存の Strategy / Hold / Force / Delay を破壊しない。**
   - `strategy.HoldEverything()`、`strategy.HoldAbilities()`、`strategy.HoldCDs()`、`strategy.HoldBuffs()` を尊重する。
   - Force 系は最優先、Delay 系は自動判断で使わない。
   - Manual 操作を邪魔しない。

4. **スキル回しだけを自動化する。移動は手動。**
   - Splatoon や timeline hint を使う場合も「スキル選択の参考」に限定する。
   - 自動移動、強制位置取り、移動入力は禁止。

5. **既存 namespace / class を重複作成しない。**
   - `namespace BossMod.Autorotation.akechi;`
   - `public sealed class AkechiPLD(...) : AkechiTools<AID, TraitID>`
   - 同じ namespace に `AkechiPLD` をもう 1 個作らない。

---

## 1. 対象ファイルと前提

標準対象ファイル:

```text
BossMod/Autorotation/Standard/akechi/Tank/AkechiPLD.cs
```

現在のファイル構造の前提:

- `using BossMod.PLD;`
- `using FFXIVClientStructs.FFXIV.Client.Game.Gauge;`
- `using static BossMod.AIHints;`
- `RotationModuleDefinition Definition()` で各 Track / Strategy を定義する。
- `Execution(StrategyValues strategy, Enemy? primaryTarget)` で毎フレーム判断する。
- 主な既存 Track:
  - `AOE`
  - `Atonement`
  - `BladeCombo`
  - `FightOrFlight`
  - `Requiescat`
  - `GoringBlade`
  - `Holy`
  - `Dash`
  - `Ranged`
  - `SpiritsWithin`
  - `CircleOfScorn`
  - `BladeOfHonor`

---

## 2. ナイト基本仕様メモ

このモジュールの基準は **7.4〜7.5 現行 PLD 回し** とする。ただし、ユーザーが「7.4 固定」と指定した場合は、7.5 の威力・効果変更を勝手に混ぜない。

パッチ基準の扱い:

- 公式ジョブガイドが Patch 7.5 表記でも、修正対象が 7.4 固定なら 7.4 の仕様を優先する。
- 7.4 と 7.5 で威力や効果が違う場合は、どちらを採用するか明示してから変更する。
- リキャスト、射程、対象種別のような安全性に関わる基本情報は、現在コードと公式情報を突き合わせて確認する。

### 2.1 60 秒バースト軸

- `Fight or Flight` は 60 秒リキャスト、20 秒間の自己火力バフ。
- `Fight or Flight` 付与時に `Goring Blade Ready` を得る。
- `Requiescat` は高レベルで `Imperator` に置き換わる。
- `Imperator` は 60 秒リキャスト、25y 射程、対象中心 5y 範囲。
- `Imperator` は `Requiescat` 4 スタックと `Confiteor Ready` を付与する。
- 基本思想は **FoF を 60 秒アンカーとして、Req/Imperator・Goring・Confiteor/Blade・BoH・CoS・Expiacion・Intervene を FoF 内へ寄せる**。

### 2.2 Proc / GCD リソース

- `Royal Authority` 完走で `Atonement Ready` と `Divine Might` を得る。
- `Atonement` → `Supplication` → `Sepulchre` の Proc 連携を持つ。
- `Divine Might` は次の `Holy Spirit` / `Holy Circle` を即時化し、威力を上げる。
- 原則として **次の Royal Authority を完走する前に、前回の Proc を消費する**。
- ただし、FoF 直前は Proc を全部即消費するのではなく、FoF 内に強い GCD を入れるための「貯め」を許可する。

### 2.3 Confiteor / Blade / Blade of Honor

- `Confiteor Ready` 中に `Confiteor` を使う。
- 以後 `Blade of Faith` → `Blade of Truth` → `Blade of Valor`。
- `Blade of Valor` 着弾で `Blade of Honor Ready` を得る。
- `Blade of Honor` は oGCD。基本は FoF 中に使う。
- `Imperator` は `Blade of Honor Ready` 中に `Blade of Honor` へ置き換わるため、action ID / Ready 判定を混同しない。

### 2.4 単体 / 範囲

- 単体基本コンボ: `Fast Blade` → `Riot Blade` → `Royal Authority`。
- 範囲基本コンボ: `Total Eclipse` → `Prominence`。
- 自動範囲は基本 **3 体以上**。
- 2 体時の `Holy Circle` は `Holy Spirit` とほぼニュートラルなので、均等削りを優先する設定でだけ許可してよい。
- 3 体以上では `Holy Circle` を優先。
- `Goring Blade` は 3 体までは許可、4 体以上では `Prominence` 系を優先する。

---

## 3. 現在の `AkechiPLD.cs` で特に壊しやすい箇所

### 3.1 Requiescat / Imperator のリキャスト表示

`Definition()` の `Track.Requiescat` で、`RaidBuffsOnly` / `Force` / `ForceWeave` の option 表示リキャストが `180` になっている場合がある。

ナイトの通常攻撃ローテ上は `Requiescat / Imperator` は 60 秒軸なので、ここは **60 秒表記へ修正候補**。

また、`Track.Requiescat` の option が `ActionTargets.Self` になっている場合があるが、`Requiescat / Imperator` は敵対象アクションとして扱う。表示・設定上の対象種別は **`ActionTargets.Hostile` 基準**にする。

修正時の注意:

- 実際の判定は `Cooldown(BestRequiescat)` を使う。
- `Requiescat` は 60 秒、3y の敵対象。
- `Imperator` は 60 秒、25y の敵対象。
- 表示値だけでもユーザー設定 UI の誤解や planner の勘違いにつながるため、放置しない。
- `RaidBuffsOnly` だから 180 秒に固定する、という扱いにしない。レイドバフ合わせは別ロジックで扱う。

### 3.2 `ShouldBuffUp()` を FoF と Req で共有しすぎない

現在の `ShouldBuffUp()` は `minimal` 条件で以下を見ている場合がある。

```csharp
Player.InCombat && target != null && In3y(target) && MP >= 4000
```

これは FoF には概ね安全だが、`Imperator` は 25y 射程であり、MP を直接消費しない。

修正する場合:

- `ShouldBuffUp()` を共用し続けず、FoF 用の `ShouldUseFightOrFlight(...)` と Req/Imperator 用の `ShouldUseRequiescatOrImperator(...)` に分ける。
- FoF は自己バフだが、基本的には殴れる状態・バースト可能状態で使う。
- Req/Imperator は `BestRequiescat` に応じて射程を分ける。
  - `Requiescat`: `target != null && In3y(target)`
  - `Imperator`: `target != null && In25y(target)`
- Req/Imperator を 3y 条件だけで止めると、遠隔バースト継続が壊れる。
- Req/Imperator は MP を直接消費しないため、`MP >= 4000` で止めない。
- ただし target 不在で空撃ちするのは禁止。

### 3.3 Goring Blade の AoE 体数制御

`GoringBlade.IsReady` だけで GCD queue すると、4 体以上でも `Goring Blade` が入る可能性がある。

望ましいルール:

- 1〜3 体: `Goring Blade Ready` があれば FoF 内または適切なタイミングで使用。
- 4 体以上: `Prominence` / 範囲 GCD を優先し、`Goring Blade` は自動使用しない。
- Force 指定の場合だけ例外許可。

### 3.4 Proc 温存ロジックを過剰にしない

FoF 前に `Atonement / Supplication / Sepulchre / Divine Might Holy` を貯めるのは強いが、貯めすぎると GCD が止まる。

Codex が修正する場合:

- `dmacHold` のような温存判定は、必ず「代わりに押す GCD」がある状態で成立させる。
- Proc を hold して何も押せない状態を作らない。
- Force 系を `dmacHold` で潰さない。Automatic 用 minimum と Force 用 minimum は分ける。
- `ComboTimer` が切れそうな場合は combo 完走を優先する。
- `AtonementReady` 系の 30 秒 duration を無駄に切らさない。

Force 系の意味:

- Force は「自動温存や通常の回し都合を無視する」指定であり、実行不能条件を無視する指定ではない。
- `target == null`、射程外、未習得、Ready なし、バインド中の `Intervene` などは Force でも guard する。
- `ForceAtonement` / `ForceHoly` / `ForceCircle` などは、Ready と射程と target 種別を満たす限り `dmacHold` で止めない。

### 3.5 oGCD の同時押し / weave 枠

- `Fight or Flight` と `Imperator` は同一 GCD 後の double weave 候補。
- `Circle of Scorn` / `Expiacion` / `Intervene` / `Blade of Honor` は FoF 内へ寄せる。
- ただし、weave 枠を過密にしてスキル詰まりを起こさない。
- `QueueOGCD` の優先度を上げすぎない。Force 以外は段階的にする。

---

## 4. 目標ローテーション思想

### 4.1 バーストの目標

FoF 中に優先したい行動:

1. `Fight or Flight` を 60 秒ごとに使う。
2. `Imperator / Requiescat` を FoF に合わせる。
3. `Goring Blade` を FoF 内に入れる。
4. `Confiteor` → `Blade of Faith` → `Blade of Truth` → `Blade of Valor` を入れる。
5. `Blade of Honor` を FoF 内に入れる。
6. `Circle of Scorn` / `Expiacion` / `Intervene` を FoF 内に入れる。
7. 残り GCD は `Sepulchre`、`Divine Might Holy Spirit/Circle`、`Royal Authority`、`Atonement/Supplication` など高威力順に調整する。

### 4.2 FoF をずらす条件

原則:

- FoF は 60 秒アンカー。
- 自動では大きく遅らせない。
- 遅らせるのは以下に限定する。

許可される遅延:

- `HoldBuffs` / `Delay` 設定。
- target が存在しない。
- 戦闘状態でない。
- 明確な downtime 直前で、押しても大半が無効になることが分かる場合。
- ユーザーが raid buff alignment 用 strategy を選んでいる場合。

禁止される遅延:

- Proc をもっと綺麗にしたいだけで FoF を 1〜2 GCD 以上ずらす。
- `Goring Blade Ready` の見落とし。
- `Requiescat` と完全一致しないから永遠に待つ。

### 4.3 Filler の目標

FoF 外では以下を守る。

- `Royal Authority` combo を回す。
- 付与された `Atonement / Supplication / Sepulchre / Divine Might` を、次の `Royal Authority` 完走前に消費する。
- 次 FoF に向けて、強い GCD を残せるなら残す。
- `Circle of Scorn` / `Expiacion` は基本 on cooldown だが、次 FoF に戻らない撃ち方は避ける。
- `Intervene` は FoF 内を優先しつつ、2 チャージ溢れは防ぐ。

---

## 5. Strategy 別ルール

### 5.1 `AOEStrategy`

- `AutoFinish`: 現在 combo をできるだけ完走し、体数に応じて単体/範囲へ戻る。
- `AutoBreak`: 体数変化を優先し、必要なら combo を切る。
- `ForceST*`: 体数に関係なく単体。
- `ForceAOE*`: 体数に関係なく範囲。

修正時の注意:

- `ForceAOE` の更新は `WantAOE` 判定より前に行う。
- `ForceAOE` が前フレームの値を引きずらないようにする。
- `autoTarget` が `Player` のときに単体 GCD を誤って投げない。
- 範囲 GCD は `In5y`、単体 GCD は `In3y` を基準にする。

### 5.2 `AtonementStrategy`

- `Automatic`: `Sepulchre > Supplication > Atonement` の順で実行。
- Force 系は該当 Ready があるときだけ強制。
- `Delay`: 自動使用しない。

注意:

- `Sepulchre` は強い。FoF 内へ入れられるなら優先。
- Proc 消費前に次の `Royal Authority` を完走しない。
- Proc duration が切れそうな場合は hold より消費を優先。

### 5.3 `BladeComboStrategy`

- `Automatic`: `Requiescat` active かつ combo step が有効なときに実行。
- `Confiteor` → `Blade of Faith` → `Blade of Truth` → `Blade of Valor` の順を崩さない。
- `BladeComboStep` は `PaladinGauge.ConfiteorComboStep` から取得する。

注意:

- `ConfiteorReady` だけで Blade step を誤判定しない。
- `BladeComboStep == 0` なら `Confiteor`。
- `BladeComboStep == 1` なら `Blade of Faith`。
- `BladeComboStep == 2` なら `Blade of Truth`。
- `BladeComboStep == 3` なら `Blade of Valor`。

### 5.4 `FightOrFlight` / `Requiescat`

- 両方 60 秒軸。
- `Together` は互いを合わせるための strategy。
- `RaidBuffsOnly` は raid buff 情報がある場合だけ使う。
- `ForceWeave` は次の可能な weave 枠で押す。

注意:

- Req/Imperator を `FightOrFlight.CD > 55f` だけで判定する場合、FoF 使用直後の window を意味していることを明確にする。
- FoF が押せないのに Req だけ先撃ちして burst を分離しない。
- ただし、Manual や Force 指定は尊重する。

### 5.5 `GoringBladeStrategy`

- `Automatic`: `GoringBladeReady` があるなら FoF 内を優先。
- `Early`: Req stack 消費前に使う。
- `Late`: Req stack 消費後に使う。
- `Force`: 体数や FoF に関係なく使えるなら使う。
- `Delay`: 自動使用しない。

注意:

- Ready duration は 30 秒。
- 4 体以上では自動使用しない。
- target は 3y 必須。

### 5.6 `HolyStrategy`

- `Automatic`: `DivineMight` 中に最適な `Holy Spirit / Holy Circle` を使う。
- `OnlySpirit`: 単体固定。
- `OnlyCircle`: 範囲固定。
- `Force*`: 強制。
- `Delay`: 自動使用しない。

注意:

- `Holy Spirit` は 25y target。
- `Holy Circle` は self 中心 5y。
- `BestHolyCircle` が未習得時に `Holy Spirit` へ fallback する場合、target を `Player` のままにしない。
- `HolyCircle` は `Player` target でよいが、fallback した `HolySpirit` は必ず敵 target に投げる。
- `DivineMight` と `Requiescat` が両方ある場合、ゲーム仕様上 Divine Might が優先される。
- FoF 終了直前や Divine Might 切れ直前は少し優先度を上げる。

### 5.7 `DashStrategy`

- `Intervene` は最大 2 チャージ。
- 基本は FoF 中に使う。
- `Force1` / `GapClose1` は 1 チャージ温存。
- `GapClose` は近接外でのみ使用。

注意:

- 移動用として温存する設定を壊さない。
- bound 中は実行不能なので無理に queue しない。
- target null / target 範囲外を guard する。

### 5.8 `RangedStrategy`

- 近接外では `Holy Spirit` または `Shield Lob`。
- 停止中かつ MP があるなら `Holy Spirit` を優先。
- 移動中、MP 不足、未習得なら `Shield Lob`。
- `Forbid` は一切使わない。

注意:

- `Holy Spirit` は cast が発生し得るので、`IsMoving` を見る。
- `OpenerCast` / `OpenerRangedCast` は開幕専用。
- target null を許可しない。
- `Force` / `ForceCast` / `Opener` / `OpenerCast` でも、`rTarget != null` を満たす場合だけ `QueueGCD(rAction, rTarget, ...)` する。

---

## 6. 実装時の C# 安全ルール

### 6.1 null 安全

必ず guard する。

```csharp
if (target != null && In3y(target))
    QueueGCD(action, target, priority);
```

禁止:

```csharp
QueueGCD(action, target!, priority); // 安易な ! は禁止
```

### 6.2 enum / AID / SID の扱い

- `AID` と `SID` を混同しない。
- `ActionID` と `AID` を暗黙変換しない。
- `Unlocked(AID.X)` を必ず確認する。
- 置き換えアクションは `BestX` helper を使うか、明示的に条件分岐する。

### 6.3 priority の扱い

- GCD は `GCDPriority`。
- oGCD は `OGCDPriority`。
- Force 以外で `Forced` を乱用しない。
- 既に queue 済みの低優先度 combo を高優先度 burst GCD が上書きできるようにする。

推奨目安:

```text
Forced          ユーザー強制
High            FoF 内の Goring / Blade / 重要 Proc
ModeratelyHigh  Confiteor/Blade 自動
AboveAverage    Atonement / Divine Might Holy
Low             通常 combo / ranged fallback
```

### 6.4 cooldown / charge の扱い

- `Cooldown(AID.X)`、`ReadyIn(AID.X)`、`Charges(AID.X)`、`HasCharge(AID.X)` の戻り型を確認して使う。
- 戻り型を推測して tuple 分解しない。
- 2 チャージ技は `HasCharge` だけでなく `Charges` と `ReadyIn` を必要に応じて見る。

### 6.5 helper 追加ルール

helper を追加する場合:

- `private` にする。
- 副作用を持たせない。
- 名前は用途が分かるようにする。
- 既存 helper と名前を重複させない。
- 追加後に compile error が出ないことを確認する。

例:

```csharp
private bool CanUseGoringBladeAutomatically(Actor? target)
{
    return target != null
        && In3y(target)
        && GoringBlade.IsReady
        && NumSplashTargets <= 3;
}
```

---

## 7. Codex に修正させるときの標準手順

Codex は以下の順で作業する。

1. `AkechiPLD.cs` 全体を読む。
2. `AkechiTools<AID, TraitID>` の利用可能 API を確認する。
3. 既存 enum / Track / Strategy を確認する。
4. 目的の修正が Strategy を増やす必要があるか判断する。
5. 既存処理の最小差分で修正する。
6. コンパイルエラーを潰す。
7. 実行時に何も queue されない穴がないか確認する。
8. 単体・2体・3体・4体以上で挙動を確認する。
9. FoF 0秒、FoF 10秒前、FoF 中、FoF 終了直前で挙動を確認する。
10. 変更内容を日本語で説明する。

---

## 8. テスト観点

### 8.1 コンパイル確認

必ず確認する。

- duplicate class / duplicate namespace がない。
- 未定義 helper がない。
- `AID` / `SID` / `ActionID` の型不一致がない。
- `GCDPriority` と `OGCDPriority` を混同していない。
- `Actor?` を null guard なしで使っていない。

### 8.2 木人・単体

確認すること:

- FoF が 60 秒ごとに押される。
- Req/Imperator が FoF と分離しない。
- Goring Blade が FoF 中に入る。
- Confiteor → Faith → Truth → Valor が順番通り。
- Blade of Honor が Valor 後に使われる。
- Atonement/Supplication/Sepulchre が次の RA 前に消費される。
- Divine Might Holy が無駄に消えない。
- GCD が止まらない。

### 8.3 範囲

確認すること:

- 1 体: 単体 combo。
- 2 体: 設定に応じて単体寄り、Holy Circle は許容可。
- 3 体: 範囲 combo / Holy Circle。
- 4 体以上: Goring Blade 自動使用を抑制し、範囲 GCD 優先。
- 単体↔範囲の切替時に combo finish/break strategy が効く。

### 8.4 downtime / target loss

確認すること:

- target null で FoF / Req / GCD を空撃ちしない。
- 近接外で Ranged strategy が働く。
- 近接外でも Imperator 25y が使えるなら burst 継続できる。
- target 復帰時に FoF が不必要に大きく遅れない。

### 8.5 Manual / Hold

確認すること:

- `HoldEverything` 中は何も queue しない。
- `HoldAbilities` 中は oGCD を queue しない。
- `HoldCDs` 中は CD 技を queue しない。
- `HoldBuffs` 中は FoF / Req を queue しない。
- Force 指定は Force として動く。
- Delay 指定は自動で使わない。

---

## 9. 既知の優先修正候補

Codex に最初に精査させるなら、以下の順が安全。

### 優先度 A

1. `Track.Requiescat` の option 表示リキャストを 60 秒軸に修正する。
2. `Track.Requiescat` の option 対象種別を `ActionTargets.Hostile` 基準に修正する。
3. `ShouldBuffUp()` を FoF 用と Req/Imperator 用に分離する。
4. `Imperator` は 25y、`Requiescat` は 3y として射程 gate を分ける。
5. Req/Imperator を `MP >= 4000` 条件で止めない。
6. Force 系が `dmacHold` で潰れないよう、Automatic 用 minimum と Force 用 minimum を分ける。
7. `target null` と `HolySpirit -> Player target` 事故を禁止する。
8. `RangedStrategy.Force` / `ForceCast` / `Opener` / `OpenerCast` でも `rTarget != null` を必須にし、target 不在で `QueueGCD(rAction, rTarget, ...)` しない。
9. Goring Blade の 4 体以上自動使用を抑制する。

### 優先度 B

1. `ForceAOE` の前フレーム値引きずりを確認する。
2. Proc hold による GCD 停止を確認する。
3. `Blade of Honor` の priority を FoF 中だけ上げる。
4. `Intervene` の charge 溢れと FoF 温存の両立を調整する。

### 優先度 C

1. Ranged strategy の移動中 Holy Spirit 抑制を強化する。
2. downtime / uptime hint を使った FoF 温存を追加する。
3. Potion 戦略を 0分/2分/6分/8分などに拡張する。
4. Cooldown Planner / timeline hint と連携する。

---

## 10. Codex への依頼テンプレート

```text
AkechiPLD.cs を agents_pld.md のルールに従って精査してください。
目的はコンパイルを壊さず、ナイトの 60 秒 FoF/Req バースト軸、Proc 管理、単体/範囲切替を安定させることです。

今回の修正範囲:
- [ここに修正したい内容を書く]

最初に精査する項目:
- `Track.Requiescat` の option CD が 60 秒になっているか。
- `Track.Requiescat` の option 対象種別が `ActionTargets.Hostile` になっているか。
- FoF 用 gate と Req/Imperator 用 gate が分離されているか。
- `Imperator` は 25y、`Requiescat` は 3y として判定されているか。
- Req/Imperator が MP 条件で止まっていないか。
- Force 系が `dmacHold` で潰れていないか。
- `target null` と `HolySpirit -> Player target` 事故がないか。
- `RangedStrategy.Force` / `ForceCast` / `Opener` / `OpenerCast` でも `rTarget != null` が必須になっているか。
- Goring Blade が 4 体以上で自動抑制されているか。

必須条件:
- 既存 namespace / class を重複作成しない。
- 未定義 helper / 未定義変数を使わない。
- Strategy の Force / Delay / Hold を壊さない。
- 変更箇所を周辺込みの完全差し替えブロックで提示する。
- コンパイルエラーが出る可能性がある箇所を事前に潰す。
- 最後に単体/範囲/FoF中/FoF前/target不在の確認項目を出す。
```

---

## 11. 回答フォーマット指定

Codex が回答する場合は、必ず以下の形式にする。

````text
結論:
- 何を直したか

修正箇所1:
- 場所: xxx の直後 / xxx メソッド全体差し替え
- 理由: xxx

差し替えコード:
```csharp
// 周辺込みの完全ブロック
```

確認項目:
- コンパイル確認
- 単体木人
- 3体以上範囲
- FoF 60秒周期
- Req/Imperator 連動
- Proc 消費
````

禁止回答:

- 「たぶん動きます」だけ。
- 差し替え場所が曖昧。
- 省略コード。
- 未定義 helper を当然のように使う。
- 変更後の確認項目がない。

---

## 12. ナイト用の安全な判断順サンプル

実装の思想としては、Execution 内で以下の順に考える。

```text
1. gauge / status / cooldown snapshot 更新
2. HoldEverything なら return
3. AOE / target / range 判定
4. 通常 combo fallback を低 priority で queue
5. HoldAbilities でなければ oGCD / buff 判定
6. FoF / Req / potion / offensive oGCD
7. Goring Blade
8. Confiteor / Blade combo
9. Atonement chain
10. Divine Might Holy
11. Ranged fallback
12. Goal zone / target 更新
```

重要:

- 低 priority の通常 combo を先に queue し、高 priority の burst GCD が上書きできる構造は維持してよい。
- ただし、何も押さない穴が出ないようにする。
- `primaryTarget` 更新タイミングで target が入れ替わる場合は、queue 済み action の target と矛盾しないよう注意する。

---

## 13. 追加設計をする場合の方針

### 13.1 Burst Planner を追加する場合

追加するなら、まず snapshot struct を作る。

候補:

```csharp
private readonly record struct PLDBurstState(
    bool CanHitTarget,
    bool InMelee,
    bool InRanged,
    float GCDLength,
    float FoFCD,
    float FoFLeft,
    float ReqCD,
    float ReqLeft,
    bool GoringReady,
    bool ConfiteorReady,
    int BladeComboStep,
    bool AtonementReady,
    bool SupplicationReady,
    bool SepulchreReady,
    bool DivineMightReady,
    bool BladeOfHonorReady,
    int NearbyTargets
);
```

ただし、最初から大規模 planner にしない。まず既存 Execution の安全性を上げる。

### 13.2 Timeline / Splatoon hint を使う場合

- `DowntimeIn` / `UptimeIn` / `TargetLossExpectedSoon` / `TargetReturnSoon` を snapshot に入れる。
- hint が不確実なら通常ロジックを優先する。
- hint でスキルを止める場合、停止理由を限定する。
- target が戻ったら早期に通常ロジックへ復帰する。

### 13.3 Potion 拡張

将来追加する場合の候補:

- `Immediate`
- `AlignWithBuffs`
- `AlignWithRaidBuffs`
- `UseAtCombatStart`
- `UseAtSixMinuteBurst`
- `UseBeforeEightMinuteBurst`

注意:

- Potion は `FightOrFlight` 直前〜直後に合わせる。
- 8分指定は「次 FoF が 8:00 帯に入るとき」に限定する。
- `LastPotionUseAt` のような変数を使う場合、未定義のまま使わない。

---

## 14. 最後に守ること

- PLD は GNB と違って cartridge / Bloodfest / Gnashing Fang の概念はない。GNB のルールを混ぜない。
- NIN / BLM / MNK の AGENTS からコピペしない。
- `AkechiPLD.cs` の現在の設計を尊重し、必要最小限で強くする。
- ユーザーは初心者として扱い、修正箇所は厳密に示す。
- 返答は日本語で、貼り替え可能な完成コードを出す。
