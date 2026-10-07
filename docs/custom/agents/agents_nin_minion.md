# agents_nin_minion 追加作業ルール: 強制忍気ゲートの最終調整

## 目的

強制忍気溢れゲートは概ね動き始めたが、以下の2点を修正する。

1. `Bhavacakra` / `ZeshoMeppo` confirmed 直後、Ninki表示更新前の1tickで再度 forced gate に入り、無駄にGCDを保持する。
2. `ProjectedNinkiAfterNextGCD` が `projected=145` のようなあり得ない値になっている。

## ログ上の現象

現在のログでは以下が確認できる。

```text
ninki=100
→ front-emergency-ninki-spend-attempt
→ front-emergency-ninki-spend-retry
→ front-emergency-ninki-spend-confirmed confirmed=Bhavacakra
→ 直後まだ ninki=100 / cd=0.99
→ forced-ninki-overcap-force-gate-hold-gcd
→ 少し後に ninki=50
```

つまり、実発動自体は成功している。  
失敗しているのは「confirmed直後のリソース反映待ち」を扱えていない点。

## 修正1: cooldown confirmed後は想定忍気を使う

`ConfirmedForcedNinkiOverflow()` が `ReadyIn(action) > 0` または `OnCooldown(action)` で confirmed した場合、実際の `Ninki` 反映が1tick遅れることがある。

そのため、confirmed時に以下を記録する。

```csharp
private long _forcedNinkiRecentlyConfirmedTick;
private int _forcedNinkiAssumedAfterSpend;
private AID _forcedNinkiRecentlyConfirmedAction;
```

confirmed時:

```csharp
_forcedNinkiRecentlyConfirmedTick = Environment.TickCount64;
_forcedNinkiAssumedAfterSpend = Math.Max(0, _forcedNinkiSpendStartNinki - NinkiSpendCost);
_forcedNinkiRecentlyConfirmedAction = _forcedNinkiSpendAction;
ClearForcedNinkiOverflow();
```

その後、短時間は `Ninki` の実値ではなく `assumedAfterSpend` を使って critical 判定する。

```csharp
private bool IsForcedNinkiRecentlyConfirmed()
    => _forcedNinkiRecentlyConfirmedTick != 0
    && RaitonFirstQueuedElapsedMs(_forcedNinkiRecentlyConfirmedTick) <= 250;

private int EffectiveNinkiForForcedGate()
    => IsForcedNinkiRecentlyConfirmed()
        ? Math.Min(Ninki, _forcedNinkiAssumedAfterSpend)
        : Ninki;
```

`IsForcedNinkiOverflowCritical(...)` は `Ninki` 直参照ではなく `EffectiveNinkiForForcedGate()` を使う。

これで `Bhavacakra confirmed` 直後に、表示上 `ninki=100` が1tick残っても再度 gate に入らない。

## 修正2: ReadyIn(action)>0だけで confirmed した場合はreturn trueでもよい

現在の `DriveForcedNinkiOverflowGate(...)` は confirmed したら `ClearForcedNinkiOverflow(); return false;` している。  
その同じtickで後続GCD処理が走ると、表示Ninkiの反映前に次処理へ進みやすい。

推奨:

```csharp
if (ConfirmedForcedNinkiOverflow())
{
    MarkForcedNinkiRecentlyConfirmed();
    LogForcedNinkiOverflowGate("confirm", $"{reason}-confirmed", _forcedNinkiSpendAction, force: true);
    ClearForcedNinkiOverflow();
    return true;
}
```

`return true` にして、そのtickは終了する。次tickで `EffectiveNinkiForForcedGate()` が効く。

## 修正3: ProjectedNinkiAfterNextGCD を100でclampする

今の `ProjectedNinkiAfterNextGCD` は、

```text
Ninki + ExpectedNormalNinkiGainBeforeNextGCD
```

をそのまま返しているため、`145` のような値が出る。

修正:

```csharp
private int ProjectedNinkiAfterNextGCD(Enemy? primaryTarget)
    => Math.Min(100, Ninki + ExpectedNormalNinkiGainBeforeNextGCD(primaryTarget));
```

## 修正4: ExpectedNormalNinkiGainBeforeNextGCD に Mug/Dokumori と Meisui を混ぜない

`ExpectedNormalNinkiGainBeforeNextGCD(...)` は「次GCDで自然に増える忍気」だけを見るべき。

現在は以下を加算している。

```csharp
if (ShadowWalker > 0 && ReadyIn(AID.Meisui) <= MathF.Max(AnimLock, GCD) && ReadyIn(BestKunai) > ShadowWalker)
    gain += 50;

if (TargetMugLeft <= 0 && ReadyIn(BestMug) <= MathF.Max(AnimLock, GCD))
    gain += 40;
```

このせいで `projected=145` になる。

修正方針:

```csharp
private int ExpectedNormalNinkiGainBeforeNextGCD(Enemy? primaryTarget)
{
    if (primaryTarget == null || Mudra.Left > 0 || TenChiJin.Left > 0)
        return 0;

    if (Raiju.Stacks > 0)
        return 5; // 実測に合わせる

    if (PhantomKamaitachi > GCD)
        return 5; // 実測に合わせる。10固定にしない

    return 5;
}
```

Mug / Dokumori / Meisui による増加は、別名の関数で扱う。

```csharp
private int ProjectedNinkiAfterPlannedOGCDs(...)
```

のように分ける。  
ただし強制gateの「次GCDで溢れるか」判定には混ぜない。

## 修正5: Ninki50台でBunshin保護中ならログを出す

現状、`projected=100` でも Bunshin 保護で `NeedsFrontBurstEmergencyNinkiSpend(...)` が false になり、ログが出ないことがある。

Bunshin保護で抑止した場合は明示ログを出す。

```text
front-emergency-ninki-spend-hold-bunshin-reserve
```

## 受け入れ条件

### 成功例

```text
ninki=100
→ front-emergency-ninki-spend-attempt
→ front-emergency-ninki-spend-confirmed
→ same tick end
→ effective ninki treated as 50
→ next GCDへ進行
```

### 禁止される挙動

```text
front-emergency-ninki-spend-confirmed
→ 直後 ninki表示が100だから forced-ninki-overcap-force-gate-hold-gcd
→ cd=0.99でGCD保持
```

これは不可。

### 予測ログ

`projected=145` のような値は不可。  
`ProjectedNinkiAfterNextGCD` は最大100にclampする。

## Codexへの最重要指示

強制忍気ゲートは実発動できるようになった。  
次は「confirmed後のリソース反映ラグ」と「projected計算の過大評価」を直す。

- confirmedしたtickはそこで処理を止める
- 250ms程度は assumed ninki を使う
- projectedは100でclamp
- 次GCD予測に Mug / Meisui を混ぜない
