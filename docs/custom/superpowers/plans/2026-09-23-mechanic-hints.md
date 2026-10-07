# メカニクス予告ヒント 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ターゲット消失・強制移動・タイムライン予告を xan(BLM/RPR/MNK/NIN/VPR/MCH)+ akechi(GNB/PLD)のスキル回しへ反映、プリセットの 4 択トラックで選択可に。

**Architecture:** 共通の毎フレーム予告 `MechanicForecast`(情報源フィルタ付き)を Basexan / AkechiTools で保持、各モジュールが読む。消失前の締めは共通 DP `WindDown`(残り GCD 枠 × 候補、最高威力を最終枠)。既存 `DowntimeIn` 利用箇所は不変、`Off` で新挙動全停止。

**Tech Stack:** C# (.NET 10, x64)、Dalamud プラグイン BossModReborn、検証は `tools/xan_timeline_harness`(BossModReborn.csproj 参照、決定的)。

**Spec:** `docs/superpowers/specs/2026-09-23-mechanic-hints-design.md`

## Global Constraints

- 作業場所: worktree `F:\bossmodreborn\.claude\worktrees\bossmodreborn-fps-optimization-83c83a`(以下 `WT`)。本体 `F:\bossmodreborn` の bin/obj に触れない。
- ハーネス実行は CWD = `WT`(相対パス `BossMod/DefaultRotationPresets.json` を読む)。sqpack・timeline root は自動検出(`C:\SquareEnix\...\sqpack`、`F:\event-trigger-master\timelines\src\main\resources`)。
- ハーネスビルド: `dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64`。実行: `dotnet tools/xan_timeline_harness/bin/x64/Release/net10.0-windows10.0.26100.0/XanTimelineHarness.dll <command> ...`(Task 0 で実パス確認、以下 `$H`)。
- スクラッチ: `S=C:/Users/happy/AppData/Local/Temp/claude/F--bossmodreborn--claude-worktrees-bossmodreborn-fps-optimization-83c83a/2e8a3ec3-26f8-41ee-9782-7284d8be6a39/scratchpad/mh`。
- トラック: 型 `BossMod.Autorotation.MechanicHintStrategy { All, TimelineOnly, ForecastOnly, Off }`(値 0 = All = 既定)。InternalName `MechanicHints`、表示名「メカニクス予告ヒント」。xan UiPriority 58、akechi 45。xan は Strategy 構造体末尾、akechi は `Track` 列挙末尾。
- 長い消失の閾値 8.5 秒(`MechanicForecast.LongLossSeconds`)。締めの最終枠余裕 0.5 秒(`WindDown.LastSlotMargin`)。
- 近接職(NIN/VPR/RPR/MNK/GNB/PLD)は射程外を遠隔攻撃で埋めない。遠隔技・突進技は既存 UI トラックに従う(本機能で追加使用も禁止もしない)。
- `Off` = 新挙動全停止。既存 `DowntimeIn` 利用箇所は全選択肢で不変。
- GNB・PLD の既存不具合修正は独立タスク(Task 7, Task 12 前半)で前後比較。`Off` 不変性は Task 4 時点で確認。
- コミットはユーザー指示時のみ。各タスク末尾はチェックポイント(`git diff --stat` 確認+scratch に差分保存)。
- 本体への書き戻しは Task 13 のみ。前に `git -C F:/bossmodreborn rev-parse --git-dir` 配下の MERGE_HEAD / rebase-merge / CHERRY_PICK_HEAD 確認。

## Review Focus

1. 復帰時刻不明(`TargetReturnIn = float.MaxValue`)の予告: 保留も締めも発動しない。→ Task 1 テスト `hold-requires-return`、Task 2 テスト `winddown-requires-return`。
2. 既にダウンタイム中(`TargetLossIn = 0`): 保留・締め無効、例外なし。→ Task 1 `downtime-now`、Task 2 `winddown-downtime-now`。
3. 短い消失(return − loss < 8.5 秒)は長い消失扱いしない。→ Task 1 `transient-ignored`。
4. 予告のフレーム間揺れ(毎フレーム loss が 0.05 秒ずつ減少)で締め選択が反転しない。→ Task 2 `winddown-stable-over-frames`。
5. 戦闘中の選択肢切替(All→Off): 即座に `MechanicForecast.None`、残留状態なし。→ Task 1 `off-is-none`。

---

### Task 0: 準備(差分取込・ベースライン採取)

**Files:**
- Modify: `WT/BossMod/Autorotation/Standard/xan/Casters/BLM.cs`(本体の未コミット差分を適用)

- [ ] **Step 1: 本体の未コミット BLM.cs / AutoClear.cs 差分を取込**

```bash
cd F:/bossmodreborn/.claude/worktrees/bossmodreborn-fps-optimization-83c83a
git -C F:/bossmodreborn diff HEAD --binary -- BossMod/Autorotation/Standard/xan/Casters/BLM.cs > /tmp/blm_transpose.patch
git apply /tmp/blm_transpose.patch
git diff --stat
```
Expected: BLM.cs +31、AutoClear.cs(取込済み)の 2 ファイル。

- [ ] **Step 2: ハーネスビルド、出力パス確認**

```bash
dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 2>&1 | tail -3
ls tools/xan_timeline_harness/bin/x64/Release/*/XanTimelineHarness.dll
```
Expected: ビルド成功、dll 1 件。パスを `$H` として控える。

- [ ] **Step 3: ベースライン bin 退避**

```bash
mkdir -p $S && cp -r tools/xan_timeline_harness/bin/x64/Release/net10.0-windows10.0.26100.0 $S/base_bin
```

- [ ] **Step 4: ベースライン面を採取(トレース付き)**

```bash
B="dotnet $S/base_bin/XanTimelineHarness.dll"
for surf in "timeline-matrix --job all" "timeline-matrix --job all --target-loss-hints 30" "dmu-full --job all" "timeline-matrix --job blm --random-disengage 7 --disengage-forecast on"; do
  tag=$(echo "$surf" | tr ' ' '_' | tr -d '-'); mkdir -p $S/base/$tag
  XAN_HARNESS_TRACE_DIR=$S/base/$tag $B $surf > $S/base/$tag/stdout.txt 2>&1
done
XAN_HARNESS_TRACE_DIR=$S/base/combat_blm $B timeline-combat-matrix --job blm --target-loss-hints 30 > $S/base/combat_blm/stdout.txt 2>&1
```
Expected: 各ディレクトリに summary.csv と run 別 csv。`combat_blm` は長時間(バックグラウンド実行可)。

- [ ] **Step 5: 比較スクリプト作成**

`$S/cmp.sh`:
```bash
#!/bin/bash
# usage: cmp.sh <base_dir> <new_dir>  -> prints identical/different run counts and per-run diffs
b=$1; n=$2
same=0; diff=0
for f in $(cd $b && ls *.csv | grep -v summary.csv); do
  if cmp -s "$b/$f" "$n/$f"; then same=$((same+1)); else diff=$((diff+1)); echo "DIFF $f"; fi
done
echo "identical=$same different=$diff"
diff <(sort $b/summary.csv) <(sort $n/summary.csv) | head -20
```

---

### Task 1: 共通予告 `MechanicForecast` と情報源フィルタ

**Files:**
- Create: `BossMod/Autorotation/MechanicHints.cs`
- Modify: `BossMod/BossModule/ExternalMechanicHintProvider.cs`(`MechanicHintSources`、`SourceOf`、源フィルタ付き `TryGetSnapshot`、namespace 定数)
- Modify: `BossMod/Timeline/External/ExternalTimelineHints.cs:9`、`BossMod/BossModule/DisengageForecast.cs:20`(namespace 定数を共通化)
- Create: `tools/xan_timeline_harness/MechanicHintsSelfTest.cs`
- Modify: `tools/xan_timeline_harness/Program.cs`(コマンド `mechanic-hints` 追加)

**Interfaces:**
- Produces:
  - `enum BossMod.MechanicHintSources { None = 0, Timeline = 1, Forecast = 2, External = 4, All = 7 }`(`[Flags]`)
  - `ExternalMechanicHintProvider.TimelineNamespace = "bossmod.internal.timeline"`, `ForecastNamespace = "bossmod.internal.disengage-forecast"`
  - `static MechanicHintSources ExternalMechanicHintProvider.SourceOf(string ns)`
  - `static bool ExternalMechanicHintProvider.TryGetSnapshot(ushort, ushort, DateTime, MechanicHintSources, out ExternalMechanicHintSnapshot)`
  - `enum BossMod.Autorotation.MechanicHintStrategy { All, TimelineOnly, ForecastOnly, Off }`
  - `readonly record struct BossMod.Autorotation.MechanicForecast(...)`、`static MechanicForecast Build(MechanicHintStrategy, WorldState, AIHints, BossModule?)`、`None`、`Enabled`、`DowntimeNow`、`ReturnKnown`、`LossWithin(float)`、`ForcedMoveWithin(float)`、`RangeLossWithin(float)`、`ShouldHoldWindow(float windowLength, float cooldown, float gcd)`、`ExpiresDuringLoss(float timeLeft)`、`SourcesFor(MechanicHintStrategy)`

- [ ] **Step 1: 失敗するテストを書く**

`tools/xan_timeline_harness/MechanicHintsSelfTest.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using BossMod;
using BossMod.Autorotation;

namespace XanTimelineHarness;

// Decision tests for the shared mechanic hint layer (MechanicForecast, WindDown) and for the per-job reactions built on it.
internal static partial class MechanicHintsSelfTest
{
    private static readonly DateTime BaseTime = new(2026, 7, 12, 0, 0, 0, DateTimeKind.Utc);
    private const string TestNamespace = "bossmod.external.splatoon.mechanic-hints-test";
    private static readonly List<string> Failures = [];
    private static int Checks;

    private static void Check(bool condition, string name, string message)
    {
        ++Checks;
        if (!condition)
            Failures.Add($"{name}: {message}");
    }

    public static int Run()
    {
        RunForecastTests();
        RunWindDownTests();
        RunJobTests();
        Console.WriteLine($"mechanic_hints checks={Checks} failures={Failures.Count}");
        foreach (var f in Failures)
            Console.WriteLine(f);
        return Failures.Count == 0 ? 0 : 3;
    }

    private static WorldState NewWorld()
    {
        var world = new WorldState(TimeSpan.TicksPerSecond, "mechanic-hints-test");
        world.Execute(new WorldState.OpFrameStart(new(BaseTime, 0, 0, 0, 0, 1), default, default, default));
        world.Execute(new WorldState.OpZoneChange(1, 0));
        return world;
    }

    private static void Push(WorldState world, string ns, float lossIn, float returnIn, float forcedMoveIn = float.MaxValue)
        => ExternalMechanicHintProvider.PushSnapshot(new(ns, world.CurrentZone, world.CurrentCFCID, ExternalZoneConfidence.TrustedSplatoonScript, world.FutureTime(1), forcedMoveIn, lossIn, returnIn, float.MaxValue, false), world.CurrentZone, world.CurrentCFCID, world.CurrentTime);

    private static void RunForecastTests()
    {
        var world = NewWorld();
        var hints = new AIHints();
        try
        {
            Push(world, TestNamespace, 5, 40);
            var f = MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null);
            Check(f.TargetLossIn == 5 && f.TargetReturnIn == 40, "external-loss", $"loss={f.TargetLossIn} return={f.TargetReturnIn}");
            Check(MechanicForecast.Build(MechanicHintStrategy.TimelineOnly, world, hints, null).TargetLossIn == float.MaxValue, "external-filtered-timeline", "external hint must not reach TimelineOnly");
            Check(MechanicForecast.Build(MechanicHintStrategy.ForecastOnly, world, hints, null).TargetLossIn == float.MaxValue, "external-filtered-forecast", "external hint must not reach ForecastOnly");
            Check(MechanicForecast.Build(MechanicHintStrategy.Off, world, hints, null) == MechanicForecast.None, "off-is-none", "Off must return None");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            Push(world, ExternalMechanicHintProvider.TimelineNamespace, 6, 30);
            Check(MechanicForecast.Build(MechanicHintStrategy.TimelineOnly, world, hints, null).TargetLossIn == 6, "timeline-source", "timeline namespace must reach TimelineOnly");
            Check(MechanicForecast.Build(MechanicHintStrategy.ForecastOnly, world, hints, null).TargetLossIn == float.MaxValue, "timeline-filtered-forecast", "timeline namespace must not reach ForecastOnly");
            ExternalMechanicHintProvider.ClearNamespace(ExternalMechanicHintProvider.TimelineNamespace);

            Push(world, TestNamespace, 3, 6); // 3 s loss: a dodge, not a downtime
            Check(MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null).TargetLossIn == float.MaxValue, "transient-ignored", "losses shorter than 8.5 s must be ignored");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            Push(world, TestNamespace, 4, float.MaxValue);
            var unknown = MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null);
            Check(unknown.TargetLossIn == 4 && !unknown.ReturnKnown, "unknown-return", "loss with unknown return is kept, return unknown");
            Check(!unknown.ShouldHoldWindow(20, 120, 2.5f), "hold-requires-return", "no hold without a known return");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            Push(world, TestNamespace, 0, 30);
            var now = MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null);
            Check(now.DowntimeNow && !now.ShouldHoldWindow(20, 120, 2.5f), "downtime-now", "during downtime nothing is held");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            Push(world, TestNamespace, 10, 40);
            var hold = MechanicForecast.Build(MechanicHintStrategy.All, world, hints, null);
            Check(hold.ShouldHoldWindow(20, 120, 2.5f), "hold-cut-window", "a 20 s window cut at 10 s with a 120 s recast must be held");
            Check(!hold.ShouldHoldWindow(20, 30, 2.5f), "no-hold-recast-back", "a 30 s recast is back by the 40 s return: use it now");
            Check(!hold.ShouldHoldWindow(11, 120, 2.5f), "no-hold-fits", "a window losing at most one GCD is not held");
            Check(hold.ExpiresDuringLoss(15) && !hold.ExpiresDuringLoss(8) && !hold.ExpiresDuringLoss(45), "expires-during-loss", "15 s left expires inside [10,40]; 8 s runs out before; 45 s survives");
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);

            hints.Disengage = new DisengageForecast(2, 1, 3, 5);
            var fc = MechanicForecast.Build(MechanicHintStrategy.ForecastOnly, world, hints, null);
            Check(fc.ForcedMoveIn == 2 && fc.ForcedMoveFor == 1 && fc.RangeLossIn == 3 && fc.RangeReturnIn == 5, "disengage-forecast", "forecast fields must come from hints.Disengage");
            Check(MechanicForecast.Build(MechanicHintStrategy.TimelineOnly, world, hints, null).ForcedMoveIn == float.MaxValue, "disengage-filtered", "TimelineOnly must ignore hints.Disengage");
        }
        finally
        {
            ExternalMechanicHintProvider.ClearNamespace(TestNamespace);
            ExternalMechanicHintProvider.ClearNamespace(ExternalMechanicHintProvider.TimelineNamespace);
        }
    }

    private static partial void RunWindDownTests();
    private static partial void RunJobTests();
}
```
同ファイル末尾に Task 2 / Task 8〜12 が埋める部分メソッドの空実装を別ファイルで置く: `tools/xan_timeline_harness/MechanicHintsSelfTest.Jobs.cs`
```csharp
namespace XanTimelineHarness;

internal static partial class MechanicHintsSelfTest
{
    private static partial void RunWindDownTests() { }
    private static partial void RunJobTests() { }
}
```
`Program.cs` の `command switch`(78-99 行)に 1 行追加:
```csharp
                "mechanic-hints" => RunMechanicHints(options),
```
`RunTimelineHints`(149 行)の直後に:
```csharp
    private static int RunMechanicHints(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        return MechanicHintsSelfTest.Run();
    }
```

- [ ] **Step 2: ビルド失敗を確認**

Run: `dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 2>&1 | grep -E "error CS" | head`
Expected: `MechanicForecast` / `MechanicHintStrategy` / `TimelineNamespace` 未定義エラー。

- [ ] **Step 3: provider に情報源フィルタ追加**

`ExternalMechanicHintProvider.cs` 先頭(namespace 直後、record 定義の前)に:
```csharp
[Flags]
public enum MechanicHintSources
{
    None = 0,
    Timeline = 1 << 0, // imported fight timelines (ExternalTimelineHints)
    Forecast = 1 << 1, // DisengageForecaster
    External = 1 << 2, // Splatoon, IPC, SplatoonSafeImport, test harnesses
    All = Timeline | Forecast | External
}
```
クラス内、`InternalNamespacePrefix` の直後に:
```csharp
    public const string TimelineNamespace = "bossmod.internal.timeline";
    public const string ForecastNamespace = "bossmod.internal.disengage-forecast";

    public static MechanicHintSources SourceOf(string ns)
        => ns.Equals(TimelineNamespace, StringComparison.OrdinalIgnoreCase) ? MechanicHintSources.Timeline
        : ns.Equals(ForecastNamespace, StringComparison.OrdinalIgnoreCase) ? MechanicHintSources.Forecast
        : MechanicHintSources.External;
```
既存 `TryGetSnapshot`(94 行)を置換:
```csharp
    public static bool TryGetSnapshot(ushort currentTerritoryId, ushort currentContentId, DateTime now, out ExternalMechanicHintSnapshot snapshot)
        => TryGetSnapshot(currentTerritoryId, currentContentId, now, MechanicHintSources.All, out snapshot);

    public static bool TryGetSnapshot(ushort currentTerritoryId, ushort currentContentId, DateTime now, MechanicHintSources sources, out ExternalMechanicHintSnapshot snapshot)
    {
        lock (LockObj)
        {
            PruneLocked(now);

            ExternalMechanicHintSnapshot? merged = null;
            foreach (var candidate in Snapshots.Values)
            {
                if (candidate.TerritoryId != 0 && candidate.TerritoryId != currentTerritoryId
                    || candidate.ContentId != 0 && candidate.ContentId != currentContentId
                    || (SourceOf(candidate.Namespace) & sources) == 0)
                    continue;

                merged = merged is { } current ? Merge(current, candidate) : candidate;
            }

            snapshot = merged ?? default;
            return merged != null;
        }
    }
```
`ExternalTimelineHints.cs:9` を `public const string HintNamespace = ExternalMechanicHintProvider.TimelineNamespace;`、`DisengageForecast.cs:20` を `public const string HintNamespace = ExternalMechanicHintProvider.ForecastNamespace;` に。

- [ ] **Step 4: `MechanicHints.cs` 作成**

```csharp
namespace BossMod.Autorotation;

// Which predicted mechanics a rotation may act on. Option 0 is the default for presets that never set the track.
public enum MechanicHintStrategy
{
    [Option("全部: タイムライン+移動予測+Splatoon/IPC")]
    All,
    [Option("タイムラインのみ: ボスモジュールのダウンタイム+取り込みタイムライン")]
    TimelineOnly,
    [Option("移動予測のみ: 射程外・強制移動")]
    ForecastOnly,
    [Option("使わない: 従来通り")]
    Off,
}

// Per-frame view of the predicted mechanics a rotation reads. Times are seconds from now; float.MaxValue means nothing is predicted.
// TargetLossIn/TargetReturnIn: the next long target loss (LongLossSeconds or more) over the enabled sources, earliest loss wins and
// keeps its own return. RangeLossIn/RangeReturnIn: hints.Disengage, short dodges included. State*: raw state machine transitions,
// unfiltered, for BLM's historical merge. Snapshot/Encounter: the provider snapshots as the older consumers (BLM, RPR, MNK) read them.
public readonly record struct MechanicForecast(
    MechanicHintStrategy Mode,
    float TargetLossIn,
    float TargetReturnIn,
    float ForcedMoveIn,
    float ForcedMoveFor,
    float RangeLossIn,
    float RangeReturnIn,
    float LeyLinesUnsafeIn,
    float StateLossIn,
    float StateReturnIn,
    float StateMoveIn,
    float StatePositioningIn,
    bool HasSnapshot,
    ExternalMechanicHintSnapshot Snapshot,
    bool HasEncounter,
    ExternalEncounterHintSnapshot Encounter)
{
    public const float LongLossSeconds = 8.5f;

    public static readonly MechanicForecast None = new(MechanicHintStrategy.Off, float.MaxValue, float.MaxValue, float.MaxValue, 0f, float.MaxValue, float.MaxValue,
        float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue, false, default, false, default);

    public bool Enabled => Mode != MechanicHintStrategy.Off;
    public bool DowntimeNow => TargetLossIn <= 0f;
    public bool ReturnKnown => TargetReturnIn < float.MaxValue;
    public bool LossWithin(float seconds) => TargetLossIn <= seconds;
    public bool ForcedMoveWithin(float seconds) => ForcedMoveIn <= seconds;
    public bool RangeLossWithin(float seconds) => RangeLossIn <= seconds;

    // Hold a window only if the loss cuts more than one GCD of it, the return is known, and using it now would push the next use past the return.
    public bool ShouldHoldWindow(float windowLength, float cooldown, float gcd)
        => ReturnKnown && !DowntimeNow && TargetLossIn < windowLength - gcd && cooldown > TargetReturnIn;

    // An effect still up when the loss starts that runs out before the target returns: spend it before the loss.
    public bool ExpiresDuringLoss(float timeLeft)
        => timeLeft > 0f && !DowntimeNow && TargetLossIn < timeLeft && timeLeft <= TargetReturnIn;

    public static MechanicHintSources SourcesFor(MechanicHintStrategy mode) => mode switch
    {
        MechanicHintStrategy.All => MechanicHintSources.All,
        MechanicHintStrategy.TimelineOnly => MechanicHintSources.Timeline,
        MechanicHintStrategy.ForecastOnly => MechanicHintSources.Forecast,
        _ => MechanicHintSources.None
    };

    public static MechanicForecast Build(MechanicHintStrategy mode, WorldState ws, AIHints hints, BossModule? module)
    {
        if (mode == MechanicHintStrategy.Off)
            return None;

        var now = ws.CurrentTime;
        var lossIn = float.MaxValue;
        var returnIn = float.MaxValue;
        var moveIn = float.MaxValue;
        var moveFor = 0f;
        var rangeLossIn = float.MaxValue;
        var rangeReturnIn = float.MaxValue;
        var leyLinesUnsafeIn = float.MaxValue;
        var stateLossIn = float.MaxValue;
        var stateReturnIn = float.MaxValue;
        var stateMoveIn = float.MaxValue;
        var statePositioningIn = float.MaxValue;

        if (mode is MechanicHintStrategy.All or MechanicHintStrategy.TimelineOnly && module?.StateMachine is { ActivePhase: not null } sm)
        {
            stateLossIn = SecondsUntil(sm.NextTransitionWithFlag(StateMachine.StateHint.DowntimeStart), now);
            stateReturnIn = SecondsUntil(sm.NextTransitionWithFlag(StateMachine.StateHint.DowntimeEnd), now);
            statePositioningIn = SecondsUntil(sm.NextTransitionWithFlag(StateMachine.StateHint.PositioningStart), now);
            stateMoveIn = Math.Min(statePositioningIn, SecondsUntil(sm.NextTransitionWithFlag(StateMachine.StateHint.Knockback), now));
            // a DowntimeEnd before the next DowntimeStart means the downtime is already running
            if (stateReturnIn < stateLossIn)
                OfferLoss(0f, stateReturnIn);
            else
                OfferLoss(stateLossIn, stateReturnIn);
            moveIn = Math.Min(moveIn, stateMoveIn);
            leyLinesUnsafeIn = Math.Min(leyLinesUnsafeIn, statePositioningIn);
        }

        var hasSnapshot = ExternalMechanicHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, now, SourcesFor(mode), out var snapshot);
        if (hasSnapshot)
        {
            OfferLoss(snapshot.TargetLossIn, snapshot.TargetReturnIn);
            if (snapshot.ForcedMoveIn < moveIn)
            {
                moveIn = snapshot.ForcedMoveIn;
                moveFor = 0f;
            }
            leyLinesUnsafeIn = Math.Min(leyLinesUnsafeIn, snapshot.LeyLinesUnsafeIn);
        }

        if (mode is MechanicHintStrategy.All or MechanicHintStrategy.ForecastOnly)
        {
            var d = hints.Disengage;
            if (d.ForcedMoveIn < moveIn)
            {
                moveIn = d.ForcedMoveIn;
                moveFor = d.ForcedMoveFor;
            }
            rangeLossIn = d.TargetLossIn;
            rangeReturnIn = d.TargetReturnIn;
        }

        ExternalEncounterHintSnapshot encounter = default;
        var hasEncounter = mode == MechanicHintStrategy.All && ExternalEncounterHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, now, out encounter);

        return new(mode, lossIn, returnIn, moveIn, moveFor, rangeLossIn, rangeReturnIn, leyLinesUnsafeIn,
            stateLossIn, stateReturnIn, stateMoveIn, statePositioningIn, hasSnapshot, snapshot, hasEncounter, encounter);

        void OfferLoss(float loss, float ret)
        {
            if (!(loss < float.MaxValue))
                return;
            loss = Math.Max(0f, loss);
            if (ret < float.MaxValue && ret - loss < LongLossSeconds)
                return;
            if (loss < lossIn)
            {
                lossIn = loss;
                returnIn = ret;
            }
        }
    }

    private static float SecondsUntil(DateTime t, DateTime now) => t == DateTime.MaxValue ? float.MaxValue : Math.Max(0f, (float)(t - now).TotalSeconds);
}
```

- [ ] **Step 5: ビルドとテスト**

```bash
dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 2>&1 | grep -E "error|エラー" | head
$H mechanic-hints
```
Expected: `mechanic_hints checks=16 failures=0`。

- [ ] **Step 6: 既存面の不変確認**

```bash
mkdir -p $S/t1/tm && XAN_HARNESS_TRACE_DIR=$S/t1/tm $H timeline-matrix --job all --target-loss-hints 30 > $S/t1/tm/stdout.txt
bash $S/cmp.sh $S/base/timelinematrix_job_all_targetlosshints_30 $S/t1/tm
```
Expected: `different=0`(provider の既存 API は全源マージのまま)。

- [ ] **Step 7: チェックポイント**

`git diff --stat > $S/t1/diffstat.txt`、`git diff > $S/t1/task1.patch`。

---

### Task 2: 締めプランナー `WindDown`

**Files:**
- Create: `BossMod/Autorotation/WindDown.cs`
- Modify: `tools/xan_timeline_harness/MechanicHintsSelfTest.Jobs.cs`(`RunWindDownTests` を別ファイル `MechanicHintsSelfTest.WindDown.cs` へ移して実装)

**Interfaces:**
- Consumes: `MechanicForecast`(Task 1)
- Produces:
  - `readonly record struct WindDownCandidate(ActionID Action, float[] StepPotency, float ReadyIn, float RecoveredIn, float ExtraTime = 0f)`
  - `readonly record struct WindDownSlot(int Candidate, int Steps)`(Candidate −1 = 通常回し)
  - `static int WindDown.SlotsBeforeLoss(in MechanicForecast m, float gcdRemaining, float gcdLength, float lockTime)`
  - `static WindDownSlot[] WindDown.Plan(ReadOnlySpan<WindDownCandidate> candidates, in MechanicForecast m, float gcdRemaining, float gcdLength, float lockTime, float fillerPotency)`
  - `static int WindDown.SelectGcd(...同引数)` → 今の枠で押す候補 index、なければ −1
  - `static int WindDown.SelectOgcd(ReadOnlySpan<WindDownCandidate> candidates, in MechanicForecast m, float gcdLength)` → 今使う oGCD index、なければ −1
  - 定数 `WindDown.MaxSlots = 8`、`WindDown.LastSlotMargin = 0.5f`

- [ ] **Step 1: 失敗するテスト**

`MechanicHintsSelfTest.Jobs.cs` から `RunWindDownTests` の空実装を削除し、`tools/xan_timeline_harness/MechanicHintsSelfTest.WindDown.cs` を作成:
```csharp
using System;
using BossMod;
using BossMod.Autorotation;

namespace XanTimelineHarness;

internal static partial class MechanicHintsSelfTest
{
    private static MechanicForecast Loss(float lossIn, float returnIn)
        => MechanicForecast.None with { Mode = MechanicHintStrategy.All, TargetLossIn = lossIn, TargetReturnIn = returnIn };

    private static WindDownCandidate C(uint id, float[] steps, float readyIn = 0, float recoveredIn = 0) => new(ActionID.MakeSpell(id), steps, readyIn, recoveredIn);

    private static partial void RunWindDownTests()
    {
        // 3 slots before a loss at 7.9 s (GCD 2.5, lock 0.6): starts 0, 2.5, 5.0 finish by 7.4
        var m = Loss(7.9f, 40);
        Check(WindDown.SlotsBeforeLoss(m, 0, 2.5f, 0.6f) == 3, "winddown-slots", $"slots={WindDown.SlotsBeforeLoss(m, 0, 2.5f, 0.6f)}");

        // two singles (600, 900) in three slots: filler first, 600, then 900 in the last slot
        WindDownCandidate[] two = [C(1, [600]), C(2, [900])];
        var plan = WindDown.Plan(two, m, 0, 2.5f, 0.6f, 300);
        Check(plan.Length == 3 && plan[0].Candidate == -1 && plan[1].Candidate == 0 && plan[2].Candidate == 1, "winddown-highest-last", Describe(plan));
        Check(WindDown.SelectGcd(two, m, 0, 2.5f, 0.6f, 300) == -1, "winddown-filler-first", "first slot belongs to the normal rotation");

        // a 3-step chain (500,560,620) vs a 1200 single with only 2 slots: single 1200 + first chain step beats two chain steps
        var m2 = Loss(5.4f, 40);
        WindDownCandidate[] chain = [C(3, [500, 560, 620]), C(4, [1200])];
        var p2 = WindDown.Plan(chain, m2, 0, 2.5f, 0.6f, 380);
        Check(p2.Length == 2 && p2[1].Candidate == 1, "winddown-partial-chain", Describe(p2));

        // recast rule: a candidate whose charge is back only after return + 1 GCD is not used
        WindDownCandidate[] recast = [C(5, [800], recoveredIn: 60)];
        Check(WindDown.SelectGcd(recast, Loss(3.0f, 40), 0, 2.5f, 0.6f, 300) == -1, "winddown-recast-rule", "60 s recast is not back by 42.5 s");

        Check(WindDown.SelectGcd(two, Loss(7.9f, float.MaxValue), 0, 2.5f, 0.6f, 300) == -1, "winddown-requires-return", "unknown return: no wind-down");
        Check(WindDown.SelectGcd(two, Loss(0, 30), 0, 2.5f, 0.6f, 300) == -1, "winddown-downtime-now", "already in downtime: no wind-down");

        // stable over frames: as the loss approaches, the plan keeps the same candidate order
        var last = -2;
        var flips = 0;
        for (var t = 0f; t < 2.5f; t += 0.05f)
        {
            var pick = WindDown.Plan(two, Loss(7.9f - t, 40 - t), 2.5f - t, 2.5f, 0.6f, 300);
            var tail = pick.Length > 0 ? pick[^1].Candidate : -1;
            if (last != -2 && tail != last)
                ++flips;
            last = tail;
        }
        Check(flips == 0, "winddown-stable-over-frames", $"flips={flips}");

        // oGCD: two eligible oGCDs, only two weave slots left -> use the lower potency one now
        WindDownCandidate[] ogcds = [C(6, [800]), C(7, [450])];
        Check(WindDown.SelectOgcd(ogcds, Loss(2.0f, 40), 2.5f) == 1, "winddown-ogcd-lowest-first", "450 first, 800 last");
        Check(WindDown.SelectOgcd(ogcds, Loss(20f, 40), 2.5f) == -1, "winddown-ogcd-plenty-slots", "plenty of weave slots: normal rotation decides");
    }

    private static string Describe(WindDownSlot[] plan) => string.Join(",", Array.ConvertAll(plan, s => $"{s.Candidate}x{s.Steps}"));
}
```

- [ ] **Step 2: 失敗確認**

Run: `dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 2>&1 | grep -E "error CS" | head -3`
Expected: `WindDown` 未定義。

- [ ] **Step 3: `WindDown.cs` 実装**

```csharp
namespace BossMod.Autorotation;

// A GCD, or a chain of GCDs, worth pressing before a long target loss. StepPotency holds the potency gained in each consecutive GCD slot
// the candidate occupies (one entry for a single action); a chain cut short by the loss still counts the steps that fit. ReadyIn is when
// the first step can be pressed; RecoveredIn is when the charge spent now is back (ChargeCapIn + Cooldown); ExtraTime is time the candidate
// needs inside its slot before the hit lands (NIN mudras).
public readonly record struct WindDownCandidate(ActionID Action, float[] StepPotency, float ReadyIn, float RecoveredIn, float ExtraTime = 0f);

public readonly record struct WindDownSlot(int Candidate, int Steps); // Candidate -1 = the normal rotation keeps the slot

// Plans the GCD slots left before a long target loss (spec rule 2b): the set of candidates that maximises potency over the normal filler,
// candidates whose recast is not back by the return (+1 GCD) excluded, laid out lowest potency first so the strongest lands in the last slot.
public static class WindDown
{
    public const int MaxSlots = 8;
    public const int MaxCandidates = 10;
    public const float LastSlotMargin = 0.5f;

    public static int SlotsBeforeLoss(in MechanicForecast m, float gcdRemaining, float gcdLength, float lockTime)
    {
        if (!m.Enabled || !m.ReturnKnown || m.DowntimeNow || gcdLength <= 0f)
            return 0;
        var slots = 0;
        while (slots < MaxSlots && gcdRemaining + slots * gcdLength + lockTime <= m.TargetLossIn - LastSlotMargin)
            ++slots;
        return slots;
    }

    public static int SelectGcd(ReadOnlySpan<WindDownCandidate> candidates, in MechanicForecast m, float gcdRemaining, float gcdLength, float lockTime, float fillerPotency)
    {
        var plan = Plan(candidates, m, gcdRemaining, gcdLength, lockTime, fillerPotency);
        return plan.Length > 0 ? plan[0].Candidate : -1;
    }

    public static WindDownSlot[] Plan(ReadOnlySpan<WindDownCandidate> candidates, in MechanicForecast m, float gcdRemaining, float gcdLength, float lockTime, float fillerPotency)
    {
        var slots = SlotsBeforeLoss(m, gcdRemaining, gcdLength, lockTime);
        var n = Math.Min(candidates.Length, MaxCandidates);
        if (slots == 0 || n == 0)
            return [];

        var cands = candidates[..n].ToArray();
        var loss = m.TargetLossIn;
        var ret = m.TargetReturnIn;

        bool Fits(int c, int k)
        {
            var t = gcdRemaining + k * gcdLength;
            var cand = cands[c];
            return cand.ReadyIn <= t + 0.05f
                && t + cand.RecoveredIn <= ret + gcdLength
                && t + cand.ExtraTime + lockTime <= loss - LastSlotMargin;
        }

        float Gain(int c, int steps)
        {
            var sum = 0f;
            for (var i = 0; i < steps; ++i)
                sum += cands[c].StepPotency[i] - fillerPotency;
            return sum;
        }

        // DP over (slot, used candidates): best total gain from slot k onwards
        var masks = 1 << n;
        var best = new float[slots + 1, masks];
        var choice = new (int Candidate, int Steps)[slots + 1, masks];
        for (var k = slots - 1; k >= 0; --k)
        {
            for (var mask = masks - 1; mask >= 0; --mask)
            {
                var bestGain = best[k + 1, mask];
                (int, int) bestChoice = (-1, 1);
                for (var c = 0; c < n; ++c)
                {
                    if ((mask & (1 << c)) != 0 || !Fits(c, k))
                        continue;
                    var steps = Math.Min(cands[c].StepPotency.Length, slots - k);
                    var gain = Gain(c, steps) + best[k + steps, mask | (1 << c)];
                    if (gain > bestGain + 0.001f)
                    {
                        bestGain = gain;
                        bestChoice = (c, steps);
                    }
                }
                best[k, mask] = bestGain;
                choice[k, mask] = bestChoice;
            }
        }

        // DP layout
        var dp = new List<WindDownSlot>();
        var picked = new List<(int Candidate, int Steps)>();
        for (int k = 0, mask = 0; k < slots;)
        {
            var (c, steps) = choice[k, mask];
            dp.Add(new(c, c < 0 ? 1 : steps));
            if (c >= 0)
            {
                picked.Add((c, steps));
                mask |= 1 << c;
                k += steps;
            }
            else
            {
                ++k;
            }
        }

        // preferred layout: normal rotation first, picked candidates packed at the end, lowest potency per slot first (strongest last)
        picked.Sort((a, b) =>
        {
            var pa = Gain(a.Candidate, a.Steps) / a.Steps;
            var pb = Gain(b.Candidate, b.Steps) / b.Steps;
            return pa != pb ? pa.CompareTo(pb) : a.Candidate.CompareTo(b.Candidate);
        });
        var used = 0;
        foreach (var p in picked)
            used += p.Steps;
        var packed = new List<WindDownSlot>();
        for (var k = 0; k < slots - used; ++k)
            packed.Add(new(-1, 1));
        var pos = slots - used;
        var valid = true;
        foreach (var p in picked)
        {
            valid &= Fits(p.Candidate, pos);
            packed.Add(new(p.Candidate, p.Steps));
            pos += p.Steps;
        }
        return valid ? [.. packed] : [.. dp];
    }

    public static int SelectOgcd(ReadOnlySpan<WindDownCandidate> candidates, in MechanicForecast m, float gcdLength)
    {
        if (!m.Enabled || !m.ReturnKnown || m.DowntimeNow || gcdLength <= 0f)
            return -1;
        var weaveSlots = 2 * (int)MathF.Floor(Math.Max(0f, m.TargetLossIn - LastSlotMargin) / gcdLength) + 1;
        var eligible = 0;
        var pick = -1;
        for (var i = 0; i < candidates.Length; ++i)
        {
            var c = candidates[i];
            if (c.ReadyIn > m.TargetLossIn - LastSlotMargin || c.RecoveredIn > m.TargetReturnIn + gcdLength)
                continue;
            ++eligible;
            if (c.ReadyIn <= 0.05f && (pick < 0 || c.StepPotency[0] < candidates[pick].StepPotency[0]))
                pick = i;
        }
        return eligible >= weaveSlots ? pick : -1;
    }
}
```

- [ ] **Step 4: テスト**

```bash
dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 2>&1 | grep -E "error|エラー" | head
$H mechanic-hints
```
Expected: `failures=0`。失敗時は `Describe` 出力で DP/配置を確認して修正。

- [ ] **Step 5: チェックポイント**(`git diff > $S/t2/task2.patch`)

---

### Task 3: 締め候補の威力表と監査

**Files:**
- Create: `BossMod/Autorotation/WindDownPotency.cs`
- Create: `tools/xan_timeline_harness/PotencyAudit.cs`
- Modify: `tools/xan_timeline_harness/Program.cs`(コマンド `potency-audit`)

**Interfaces:**
- Produces: `static class WindDownPotency` の表 `NIN`, `VPR`, `MCH`, `GNB`, `PLD`(型 `WindDownPotency.Entry[]`)、`static float WindDownPotency.Of(Entry[] table, uint action)`(Total、未登録 0)、各ジョブの `Filler` 定数。
- `readonly record struct Entry(uint Action, float Direct, float Total, bool Combo)`: Direct = 説明文の直撃威力(監査対象、Combo=true なら "Combo Potency")、Total = 順位付けに使う値(DoT・付随 oGCD 込み)。

- [ ] **Step 1: 監査コマンド(失敗するテスト)**

`tools/xan_timeline_harness/PotencyAudit.cs`:
```csharp
using System;
using System.Text.RegularExpressions;
using BossMod;
using BossMod.Autorotation;

namespace XanTimelineHarness;

// Checks WindDownPotency against the client's own action descriptions (ActionTransient, English): the direct potency of every entry must
// match "potency of N" (or "Combo Potency: N" for combo entries). Prints the description of every mismatch so the table can be corrected.
internal static partial class PotencyAudit
{
    [GeneratedRegex(@"potency of (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BaseRegex();
    [GeneratedRegex(@"Combo Potency:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ComboRegex();

    public static int Run()
    {
        var sheet = Service.LuminaSheet<Lumina.Excel.Sheets.ActionTransient>()!;
        var failures = 0;
        var checks = 0;
        foreach (var (job, table) in new (string, WindDownPotency.Entry[])[] { ("NIN", WindDownPotency.NIN), ("VPR", WindDownPotency.VPR), ("MCH", WindDownPotency.MCH), ("GNB", WindDownPotency.GNB), ("PLD", WindDownPotency.PLD) })
        {
            foreach (var e in table)
            {
                ++checks;
                var text = sheet.GetRow(e.Action).Description.ExtractText();
                var match = (e.Combo ? ComboRegex() : BaseRegex()).Match(text);
                var actual = match.Success ? float.Parse(match.Groups[1].Value) : -1;
                if (actual != e.Direct)
                {
                    ++failures;
                    Console.WriteLine($"{job} {e.Action}: table={e.Direct} client={actual}\n    {text.Replace('\n', ' ')}");
                }
            }
        }
        Console.WriteLine($"potency_audit checks={checks} failures={failures}");
        return failures == 0 ? 0 : 3;
    }
}
```
`Program.cs` の switch に `"potency-audit" => RunPotencyAudit(options),`、メソッド:
```csharp
    private static int RunPotencyAudit(HarnessOptions options)
    {
        InitializeBossMod(options.Sqpack);
        return PotencyAudit.Run();
    }
```

- [ ] **Step 2: 表を作成(候補値)**

`BossMod/Autorotation/WindDownPotency.cs`:
```csharp
namespace BossMod.Autorotation;

// Potencies that rank wind-down candidates before a long target loss (spec rule 2b). Direct is the client tooltip value checked by the
// harness command `potency-audit`; Total is what the ranking uses (DoT ticks and the oGCD that the action unlocks included). Filler is the
// average potency of the job's normal combo GCD, the baseline a candidate has to beat.
public static class WindDownPotency
{
    public readonly record struct Entry(uint Action, float Direct, float Total, bool Combo = false);

    public const float FillerNIN = 370, FillerVPR = 380, FillerMCH = 320, FillerGNB = 380, FillerPLD = 340;

    public static readonly Entry[] NIN =
    [
        new((uint)BossMod.NIN.AID.Raiton, 740, 740),
        new((uint)BossMod.NIN.AID.HyoshoRanryu, 1300, 1300),
        new((uint)BossMod.NIN.AID.FleetingRaiju, 700, 700),
        new((uint)BossMod.NIN.AID.PhantomKamaitachi, 600, 600),
    ];

    public static readonly Entry[] VPR =
    [
        new((uint)BossMod.VPR.AID.Vicewinder, 500, 500),
        new((uint)BossMod.VPR.AID.HuntersCoil, 620, 790),       // + Twinfang Bite 170
        new((uint)BossMod.VPR.AID.SwiftskinsCoil, 620, 790),    // + Twinblood Bite 170
        new((uint)BossMod.VPR.AID.UncoiledFury, 680, 1020),     // + Uncoiled Twinfang/Twinblood 170 each
        new((uint)BossMod.VPR.AID.Reawaken, 750, 750),
        new((uint)BossMod.VPR.AID.FirstGeneration, 680, 960),   // + First Legacy 280
        new((uint)BossMod.VPR.AID.Ouroboros, 1050, 1050),
    ];

    public static readonly Entry[] MCH =
    [
        new((uint)BossMod.MCH.AID.Drill, 600, 600),
        new((uint)BossMod.MCH.AID.AirAnchor, 600, 600),
        new((uint)BossMod.MCH.AID.ChainSaw, 600, 600),
        new((uint)BossMod.MCH.AID.Excavator, 600, 600),
        new((uint)BossMod.MCH.AID.FullMetalField, 900, 900),
        new((uint)BossMod.MCH.AID.BlazingShot, 240, 240),
        new((uint)BossMod.MCH.AID.DoubleCheck, 170, 170),
        new((uint)BossMod.MCH.AID.Checkmate, 170, 170),
    ];

    public static readonly Entry[] GNB =
    [
        new((uint)BossMod.GNB.AID.GnashingFang, 500, 740),      // + Jugular Rip 240
        new((uint)BossMod.GNB.AID.SavageClaw, 560, 840),        // + Abdomen Tear 280
        new((uint)BossMod.GNB.AID.WickedTalon, 620, 940),       // + Eye Gouge 320
        new((uint)BossMod.GNB.AID.DoubleDown, 1200, 1200),
        new((uint)BossMod.GNB.AID.SonicBreak, 300, 900),        // + 60 x 10 ticks
        new((uint)BossMod.GNB.AID.BurstStrike, 460, 680),       // + Hypervelocity 220
        new((uint)BossMod.GNB.AID.BlastingZone, 800, 800),
        new((uint)BossMod.GNB.AID.BowShock, 150, 450),          // + 60 x 5 ticks
    ];

    public static readonly Entry[] PLD =
    [
        new((uint)BossMod.PLD.AID.GoringBlade, 700, 700),
        new((uint)BossMod.PLD.AID.HolySpirit, 500, 500),
        new((uint)BossMod.PLD.AID.Atonement, 460, 460),
        new((uint)BossMod.PLD.AID.Supplication, 500, 500),
        new((uint)BossMod.PLD.AID.Sepulchre, 540, 540),
        new((uint)BossMod.PLD.AID.Confiteor, 500, 500),
        new((uint)BossMod.PLD.AID.BladeOfFaith, 260, 260),
        new((uint)BossMod.PLD.AID.BladeOfTruth, 380, 380),
        new((uint)BossMod.PLD.AID.BladeOfValor, 500, 500),
        new((uint)BossMod.PLD.AID.CircleOfScorn, 140, 290),     // + 30 x 5 ticks
        new((uint)BossMod.PLD.AID.Expiacion, 450, 450),
        new((uint)BossMod.PLD.AID.BladeOfHonor, 1000, 1000),
    ];

    public static float Of(Entry[] table, uint action)
    {
        foreach (var e in table)
            if (e.Action == action)
                return e.Total;
        return 0;
    }
}
```
AID 名は各 `BossMod/ActionQueue/<Role>/<JOB>.cs` の enum と照合(ビルドエラーで検出)。

- [ ] **Step 3: 監査実行 → 表修正**

```bash
dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 2>&1 | grep -E "error CS" | head
$H potency-audit
```
不一致行は `client=` 値へ Direct を修正、Total も同じ差分だけ修正(付随 oGCD・DoT は各行コメントの内訳をクライアント値で再計算)。GNB は `tools/xan_timeline_harness/GnbPotencyScorer.cs` の値とも一致させる(食い違いはクライアント値を正とし、スコアラー側の差は報告に記録)。説明文にレベル分岐があり `client=-1` の行は、出力された説明文から該当値を読んで表を修正、監査の正規表現にその行専用の分岐は作らない。再実行で `failures=0`。

- [ ] **Step 4: チェックポイント**

---

### Task 4: ベース統合+8 モジュールへトラック追加(挙動不変)

**Files:**
- Modify: `BossMod/Autorotation/Standard/xan/Basexan.cs`(`Mechanic`、`UpdateMechanicForecast`、`RecastRecoveredIn`)
- Modify: `BossMod/Autorotation/Standard/akechi/AkechiTools.cs`(同名 3 点+`ToolsExtensions.DefineMechanicHints`)
- Modify: `xan/Melee/RPR.cs`, `xan/Melee/MNK.cs`, `xan/Melee/NIN.cs`, `xan/Melee/VPR.cs`, `xan/Ranged/MCH.cs`(Strategy 構造体末尾にトラック+`Exec` 冒頭で更新)
- Modify: `akechi/Tank/AkechiGNB.cs`, `akechi/Tank/AkechiPLD.cs`(`Track` 列挙末尾+定義+`Execution` 冒頭で更新)

**Interfaces:**
- Consumes: `MechanicForecast.Build`、`MechanicHintStrategy`
- Produces: `protected MechanicForecast Mechanic { get; private set; }`、`protected void UpdateMechanicForecast(MechanicHintStrategy mode)`、`protected float RecastRecoveredIn(AID aid)`(xan / akechi 共通名)、`ToolsExtensions.DefineMechanicHints(this RotationModuleDefinition res, Enum track)`

- [ ] **Step 1: 失敗するテスト(トラック存在)**

`MechanicHintsSelfTest.Jobs.cs` の `RunJobTests` を実装:
```csharp
using System;
using BossMod.Autorotation;

namespace XanTimelineHarness;

internal static partial class MechanicHintsSelfTest
{
    private static readonly (string Job, Func<RotationModuleDefinition> Definition)[] HintedModules =
    [
        ("rpr", BossMod.Autorotation.xan.RPR.Definition),
        ("mnk", BossMod.Autorotation.xan.MNK.Definition),
        ("nin", BossMod.Autorotation.xan.NIN.Definition),
        ("vpr", BossMod.Autorotation.xan.VPR.Definition),
        ("mch", BossMod.Autorotation.xan.MCH.Definition),
        ("gnb", BossMod.Autorotation.akechi.AkechiGNB.Definition),
        ("pld", BossMod.Autorotation.akechi.AkechiPLD.Definition),
    ];

    private static partial void RunJobTests()
    {
        foreach (var (job, definition) in HintedModules)
        {
            var configs = definition().Configs;
            var track = configs.Find(c => c.InternalName == "MechanicHints") as StrategyConfigTrack;
            Check(track != null && track.Options.Count == 4 && track.Options[0].InternalName == "All" && track.Options[3].InternalName == "Off", $"track-{job}", "MechanicHints track with All..Off must exist");
        }
        RunJobDecisionTests();
    }

    private static partial void RunJobDecisionTests();
}
```
新規 `MechanicHintsSelfTest.JobDecisions.cs`:
```csharp
namespace XanTimelineHarness;

internal static partial class MechanicHintsSelfTest
{
    private static partial void RunJobDecisionTests() { }
}
```
`StrategyConfigTrack.Options` の要素型の InternalName プロパティ名は `BossMod/Autorotation/Strategy.cs:234-252` で確認し合わせる。

- [ ] **Step 2: 失敗確認**: `$H mechanic-hints` → `track-*` 7 件 fail。

- [ ] **Step 3: Basexan**(`Basexan.cs` の `DowntimeIn`/`UptimeIn` 宣言 38-39 行の直後)

```csharp
    // predicted mechanics (see MechanicForecast); modules refresh it at the top of Exec with their MechanicHints track
    protected MechanicForecast Mechanic { get; private set; } = MechanicForecast.None;
    protected void UpdateMechanicForecast(MechanicHintStrategy mode) => Mechanic = MechanicForecast.Build(mode, World, Hints, Bossmods.ActiveModule);

    // seconds until the charge spent by pressing the action now is back
    protected float RecastRecoveredIn(AID aid)
    {
        var def = ActionDefinitions.Instance.Spell(aid);
        return def == null ? float.MaxValue : def.ChargeCapIn(World.Client.Cooldowns, World.Client.DutyActions, Player.Level) + def.Cooldown;
    }
```

- [ ] **Step 4: AkechiTools**(`DowntimeIn`/`UptimeIn` 117-118 行の直後に Step 3 と同じ 3 メンバー、`ToolsExtensions` に)

```csharp
    public static RotationModuleDefinition.ConfigRef<MechanicHintStrategy> DefineMechanicHints(this RotationModuleDefinition res, Enum track)
        => res.Define(track).As<MechanicHintStrategy>("MechanicHints", "メカニクス予告ヒント", 45)
            .AddOption(MechanicHintStrategy.All, "全部: タイムライン+移動予測+Splatoon/IPC")
            .AddOption(MechanicHintStrategy.TimelineOnly, "タイムラインのみ: ボスモジュールのダウンタイム+取り込みタイムライン")
            .AddOption(MechanicHintStrategy.ForecastOnly, "移動予測のみ: 射程外・強制移動")
            .AddOption(MechanicHintStrategy.Off, "使わない: 従来通り");
```
`res.Define` の引数型が `Enum` を受けない場合は既存 `DefineOGCD`(AkechiTools 862 行)のジェネリック署名に合わせる。

- [ ] **Step 5: xan 5 モジュールにトラック追加**

各 Strategy 構造体の `readonly Targeting IStrategyCommon.Targeting` 行の直前に:
```csharp
        [Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)]
        public Track<MechanicHintStrategy> MechanicHints;
```
各 `Exec(in Strategy strategy, Enemy? primaryTarget)` の先頭行に:
```csharp
        UpdateMechanicForecast(strategy.MechanicHints.Value);
```
対象: RPR、MNK、NIN(NIN.cs:366)、VPR(VPR.cs:190)、MCH(MCH.cs:826)。

- [ ] **Step 6: akechi 2 モジュール**

`AkechiGNB.cs:11` の `Track` 列挙末尾(`RotationMode` の後)に `, MechanicHints`、`Definition()` の `return res;` 直前(194 行付近)に `res.DefineMechanicHints(Track.MechanicHints);`。`AkechiPLD.cs:9` と `Definition()` の `return res;`(139 行)直前も同様。各 `Execution` 先頭に:
```csharp
        UpdateMechanicForecast(strategy.Option(Track.MechanicHints).As<MechanicHintStrategy>());
```

- [ ] **Step 7: テスト+不変性**

```bash
dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 2>&1 | grep -E "error|エラー" | head
$H mechanic-hints
for surf in "timeline-matrix --job all" "timeline-matrix --job all --target-loss-hints 30" "dmu-full --job all"; do
  tag=$(echo "$surf" | tr ' ' '_' | tr -d '-'); mkdir -p $S/t4/$tag
  XAN_HARNESS_TRACE_DIR=$S/t4/$tag $H $surf > $S/t4/$tag/stdout.txt 2>&1
  bash $S/cmp.sh $S/base/$tag $S/t4/$tag | tail -3
done
```
Expected: `mechanic_hints ... failures=0`、3 面とも `different=0`(新トラックは読まれるだけ、挙動未接続)。

- [ ] **Step 8: チェックポイント**

---

### Task 5: RPR・MNK の読み口を `Mechanic` 経由に

**Files:**
- Modify: `xan/Melee/RPR.cs:641-642`
- Modify: `xan/Melee/MNK.cs:513-514`

**Interfaces:**
- Consumes: `Mechanic.HasSnapshot/Snapshot/HasEncounter/Encounter`(Task 1, 4)

- [ ] **Step 1: 失敗するテスト(Off で予告を無視)**

`MechanicHintsSelfTest.JobDecisions.cs` の `RunJobDecisionTests` に RPR ケース(ハーネスの `RunRprPotionTest` と同じ組立て)は重いので、代わりにトレース比較で検証する: Step 3 の `--track MechanicHints=Off --target-loss-hints 30` が `--target-loss-hints` なしベースラインと一致することを期待値にする。

- [ ] **Step 2: RPR**

```csharp
        _hasMechanicHint = Mechanic.HasSnapshot;
        _mechanicHint = Mechanic.Snapshot;
        _hasEncounterHint = Mechanic.HasEncounter;
        _encounterHint = Mechanic.Encounter;
```

- [ ] **Step 3: MNK**

```csharp
        _hasMechanicHint = UseMechanicAIHints() && Mechanic.HasSnapshot;
        _mechanicHint = Mechanic.Snapshot;
```

- [ ] **Step 4: 検証**

```bash
dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 2>&1 | grep -E "error|エラー" | head
for j in rpr mnk; do
  mkdir -p $S/t5/$j-all $S/t5/$j-off
  XAN_HARNESS_TRACE_DIR=$S/t5/$j-all $H timeline-matrix --job $j --target-loss-hints 30 > /dev/null
  XAN_HARNESS_TRACE_DIR=$S/t5/$j-off $H timeline-matrix --job $j --target-loss-hints 30 --track MechanicHints=Off > /dev/null
done
```
Expected:
- `$j-all` = ベースライン `timelinematrix_job_all_targetlosshints_30` の同ジョブ run と一致(ベースラインから `*_$j_*` だけ抜いて比較: `mkdir $S/t5/base-$j && cp $S/base/timelinematrix_job_all_targetlosshints_30/*_${j}_* $S/t5/base-$j/ && bash $S/cmp.sh $S/t5/base-$j $S/t5/$j-all`)。
- `$j-off` = ベースライン `timelinematrix_job_all`(予告なし)の同ジョブ run と一致。
- 加えて `timeline-combat-matrix --job rpr --target-loss-hints 30`(All)をバックグラウンドで回し、`combat` ベースラインがあれば一致確認(なければ Task 0 で採取漏れ → 採取して比較)。

- [ ] **Step 5: チェックポイント**

---

### Task 6: BLM 移行(`ExternalHints` → `MechanicHints`)

**Files:**
- Modify: `xan/Casters/BLM.cs`(21-22 トラック、95-103 enum、217/1409 戦略値、1009-1022 エンカウント、4651-4662 `BuildTimelineContext`、4733-4760 push/判定、5885-5901 ハンドオフ)
- Modify: `tools/xan_timeline_harness/Program.cs:998-1007`

**Interfaces:**
- Consumes: `Mechanic`(Task 4 のベース)、`MechanicForecast.State*`

- [ ] **Step 1: 旧挙動の参照値を採取(移行前)**

```bash
mkdir -p $S/t6/pre-mo $S/t6/pre-dmu
XAN_HARNESS_TRACE_DIR=$S/t6/pre-mo $H timeline-matrix --job blm --target-loss-hints 30 --track ExternalHints=MechanicOnly > /dev/null
XAN_HARNESS_TRACE_DIR=$S/t6/pre-dmu $H dmu-full --job blm --track ExternalHints=MechanicOnly > /dev/null
```
(`combat-matrix` も同条件で `pre-combat` に採取、バックグラウンド。)

- [ ] **Step 2: `SplatoonHintSource.PushTaggedHints` の変換規則を確認**

`BossMod/Autorotation/Standard/xan/SplatoonHintSource.cs` を読み、`PushCombatTimeHints`(`SplatoonHintBridge.cs:5-31`)が combatTime→「今から何秒」へ変換する際の上限・下限・TTL・`timelineStable` 条件を列挙し `$S/t6/push_rules.txt` に記録。Step 4 の State* 合成はこの規則を同じ順で再現する(例: 規則が「N 秒より先の事象は送らない」なら State* にも同じ上限を掛ける)。

- [ ] **Step 3: トラック置換**

21-22 行:
```csharp
        [Track("メカニクス予告ヒント", InternalName = "MechanicHints", UiPriority = 58)]
        public Track<MechanicHintStrategy> MechanicHints;
```
enum `ExternalHintStrategy`(95-103)削除。`CurrentExternalHintStrategy` の型を `MechanicHintStrategy` に。1409 行:
```csharp
        CurrentExternalHintStrategy = strategy.MechanicHints.Value;
        UpdateMechanicForecast(CurrentExternalHintStrategy);
```
1410 行 `PushBossModuleTimelineHints();` と定義(4733-4754)、`NextBossModuleTimelineHintPushAt`、`BLMTuning.BossModuleTimelineHintPushInterval` を削除(他参照なしを grep 確認)。
```csharp
    private bool UseExternalMechanicHints() => Mechanic.Enabled;
    private bool UseExternalEncounterHints() => Mechanic.Mode == MechanicHintStrategy.All;
```
`ExternalHintStrategy.` を参照する残り(`HandleExternalHintHandoff` 5885-5901、`PushExternalHintTest` 4730 のログ等)は型置換のみ。

- [ ] **Step 4: `BuildTimelineContext`(4651-4662)**

```csharp
        if (UseExternalMechanicHints())
        {
            var snap = Mechanic.HasSnapshot ? Mechanic.Snapshot : default;
            var extLoss = Mechanic.HasSnapshot ? snap.TargetLossIn : float.MaxValue;
            var extReturn = Mechanic.HasSnapshot ? snap.TargetReturnIn : float.MaxValue;
            var extMove = Mechanic.HasSnapshot ? snap.ForcedMoveIn : float.MaxValue;
            var extLeyLines = Mechanic.HasSnapshot ? snap.LeyLinesUnsafeIn : float.MaxValue;
            // the boss module's own timeline, as the removed PushBossModuleTimelineHints published it (in combat only)
            var inCombat = Player.InCombat;
            var stateLoss = inCombat ? Mechanic.StateLossIn : float.MaxValue;
            var stateReturn = inCombat ? Mechanic.StateReturnIn : float.MaxValue;
            var stateMove = inCombat ? Mechanic.StateMoveIn : float.MaxValue;
            var statePositioning = inCombat ? Mechanic.StatePositioningIn : float.MaxValue;
            var loss = Math.Min(extLoss, stateLoss);
            var ret = Math.Min(extReturn, stateReturn);
            if (ValidHintTime(loss))
                downtimeIn = Math.Min(downtimeIn, loss);
            if (ValidHintTime(ret))
                uptimeIn = uptimeIn == null ? ret : Math.Min(uptimeIn.Value, ret);
            var move = Math.Min(extMove, stateMove);
            if (ValidHintTime(move))
                forcedMoveIn = move;
            var leyLines = Math.Min(extLeyLines, statePositioning);
            if (ValidHintTime(leyLines))
                leyLinesUnsafeIn = leyLines;
        }
```
Step 2 の規則(上限・TTL 等)があれば `stateLoss/stateReturn/stateMove/statePositioning` に同じ条件を掛ける。

- [ ] **Step 5: エンカウント(1009-1010)**

```csharp
        if (UseExternalEncounterHints() && Mechanic.HasEncounter)
        {
            var external = Mechanic.Encounter;
```
(以降のブロック本体は既存のまま。)

- [ ] **Step 6: ハーネス固定指定**(Program.cs 998-1007)

`"ExternalHints"` → `"MechanicHints"`、`"MechanicOnly"` → `"All"`、例外文言も同様に。コメント(1000 行)「defaults to ignoring hints」を「defaults to All; pinned here so --track cannot switch it off in random-disengage mode」に。

- [ ] **Step 7: 比較**

```bash
dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 2>&1 | grep -E "error|エラー" | head
mkdir -p $S/t6/post-all $S/t6/post-dmu $S/t6/post-off
XAN_HARNESS_TRACE_DIR=$S/t6/post-all $H timeline-matrix --job blm --target-loss-hints 30 > /dev/null
XAN_HARNESS_TRACE_DIR=$S/t6/post-dmu $H dmu-full --job blm > /dev/null
XAN_HARNESS_TRACE_DIR=$S/t6/post-off $H timeline-matrix --job blm --target-loss-hints 30 --track MechanicHints=Off > /dev/null
bash $S/cmp.sh $S/t6/pre-mo $S/t6/post-all
bash $S/cmp.sh $S/t6/pre-dmu $S/t6/post-dmu
```
Expected: matrix は `different=0`(汎用タイムラインはステートマシンなし)。dmu-full は一致が目標、差分が出たら差分 run の最初の分岐フレームを `BLM_PLANNER_DEBUG` で出し、原因(Step 2 の規則の再現漏れ等)を特定・修正、説明不能な差は残さない。`post-off` はベースライン(予告なし)の BLM run と一致。combat-matrix(`pre-combat` と同条件)もバックグラウンドで比較。

- [ ] **Step 7b: トラック存在テストに BLM を追加**

`MechanicHintsSelfTest.Jobs.cs` の `HintedModules` に `("blm", BossMod.Autorotation.xan.BLM.Definition),` を追加。

- [ ] **Step 8: `$H mechanic-hints` 再実行、チェックポイント**

---

### Task 7: GNB 既存不具合修正(全選択肢で有効)

**Files:**
- Modify: `akechi/Tank/AkechiGNB.cs`(879 付近、1406-1418、1625-1628)

- [ ] **Step 1: 修正前計測**

```bash
mkdir -p $S/t7/pre $S/t7/pre-tlh
XAN_HARNESS_TRACE_DIR=$S/t7/pre $H timeline-matrix --job gnb > /dev/null
XAN_HARNESS_TRACE_DIR=$S/t7/pre-tlh $H timeline-matrix --job gnb --target-loss-hints 30 > /dev/null
```
(`timeline-combat-matrix --job gnb` と `dmu-full --job gnb` も `pre-*` に採取。)

- [ ] **Step 2: 未来ダウンタイムの実長**(`Planner74FeedCooldownPlannerTimelineHints` の else 分岐)

```csharp
            var downtimeEnd = Bossmods.ActiveModule?.StateMachine.NextTransitionWithFlag(StateMachine.StateHint.DowntimeEnd) ?? DateTime.MaxValue;
            var downtimeEndIn = downtimeEnd == DateTime.MaxValue ? float.MaxValue : (float)(downtimeEnd - World.CurrentTime).TotalSeconds;
            var upcomingEnd = downtimeEndIn > transitionIn && downtimeEndIn < float.MaxValue
                ? MathF.Min(horizon, downtimeEndIn)
                : MathF.Min(horizon, transitionIn + SkSGCDLength * TimingCurrentAddHintGCDs);
            AkechiGNBPlanner74HintBus.PushWindowAbsolute(
                CombatTimer + transitionIn,
                CombatTimer + upcomingEnd,
                targetable: false,
                AkechiGNBPlanner74HintSource.BossTimeline,
                "BossMod CD Planner: upcoming downtime",
                priority: HintPriorityBossTimelineDowntime);
```

- [ ] **Step 3: ダウンタイム・保留枠でも No Mercy 残を減らす**(`Planner74ApplyGCD` 先頭)

```csharp
        if (!canHitTarget || d.GCD == Planner74GCD.None)
        {
            // time still passes in a slot without a GCD: No Mercy keeps running out
            if (s.NoMercySlotsLeft > 0)
                s.NoMercySlotsLeft = Planner74Clamp(s.NoMercySlotsLeft - 1, 0, s.NoMercyDurationSlots);
            return 0f;
        }
```

- [ ] **Step 4: ダウンタイム枠に weave を置かない**(1414 付近の呼出)

```csharp
                    if (canHitTarget)
                        delta += Planner74ApplyAutoWeaves(input, ref s2, ref decision, slot, nmFirstBuffedSlot);
```

- [ ] **Step 5: 修正後計測・判定**

同 4 面を `post-*` に採取、`summary.csv` の potency 列を比較。判定: 各面の総威力が悪化しない。悪化した面は悪化 run の分岐を調べ、3 修正のどれが原因か 1 つずつ戻して特定。改善・中立の修正のみ残す。結果を `$S/t7/result.txt` に記録。

- [ ] **Step 6: チェックポイント**

---

### Task 8: GNB の予告挙動

**Files:**
- Modify: `akechi/Tank/AkechiGNB.cs`(ヒント源 enum 3176、フィーダ呼出 2633-2640、Sonic Break 2999-3024、Reign 3050-3072、GCD 締め)
- Modify: `tools/xan_timeline_harness/MechanicHintsSelfTest.JobDecisions.cs`

**Interfaces:**
- Consumes: `Mechanic`、`WindDown.SelectGcd/SelectOgcd`、`WindDownPotency.GNB/FillerGNB`、`RecastRecoveredIn`

- [ ] **Step 1: 失敗するテスト**

`RunJobDecisionTests` に GNB ケース追加(ハーネス既存の世界組立てに倣う最小版):
```csharp
    private static (WorldState World, Actor Player, Actor Target, AIHints Hints, RotationModuleManager Manager, BossModuleManager Bossmods) NewCombat(Class job)
    {
        var world = NewWorld();
        const ulong playerID = 0x10000001, targetID = 0x40000001;
        world.Execute(new ActorState.OpCreate(playerID, 0, 0, 0, "Player", 0, ActorType.Player, job, 100, new Vector4(0, 0, 0, 0), 0.5f, new(100000, 100000, 0, 10000, 10000), true, true, default, default, 0));
        world.Execute(new PartyState.OpModify(PartyState.PlayerSlot, new(1, playerID, false)));
        world.Execute(new ActorState.OpCreate(targetID, 0x1234, 2, 0, "Target", 0, ActorType.Enemy, Class.None, 100, new Vector4(2.5f, 0, 0, MathF.PI), 2f, new(10000000, 10000000, 0, 0, 0), true, false, default, default, 0));
        world.Execute(new ActorState.OpCombat(playerID, true));
        world.Execute(new ClientState.OpPlayerStatsChange(new(400, 400, 100)));
        world.Execute(new ClientState.OpCooldown(true, []));
        var hints = new AIHints();
        var bossmods = new BossModuleManager(world);
        var db = new RotationDatabase(new System.IO.DirectoryInfo("tools/xan_timeline_harness/.autorotation"), new System.IO.FileInfo("BossMod/DefaultRotationPresets.json"));
        var manager = new RotationModuleManager(db, bossmods, hints) { CombatStart = BaseTime.AddSeconds(-60) };
        return (world, world.Actors.Find(playerID)!, world.Actors.Find(targetID)!, hints, manager, bossmods);
    }

    private static void RefreshTargets(AIHints hints, Actor target)
    {
        hints.Clear();
        var enemy = new AIHints.Enemy(target, 1, false);
        hints.Enemies[target.CharacterSpawnIndex] = enemy;
        hints.PotentialTargets.Add(enemy);
        hints.HighestPotentialTargetPriority = 1;
    }

    private static bool Queued(AIHints hints, ActionID action, float minPriority = 0)
        => hints.ActionsToExecute.Entries.Exists(e => e.Action == action && e.Priority >= minPriority);
```
(`OpCreate` の引数並びは `Program.cs:1675` と `CreateEnemy`(1880)に合わせる。`ActorState`/`PartyState`/`Vector4`/`Actor` 用に `using System.Numerics;` を追加。)

GNB ケース:
```csharp
    private static void GnbTests()
    {
        foreach (var (name, loss, ret, mode, expectDD) in new (string, float, float, MechanicHintStrategy, bool)[]
        {
            ("gnb-dd-before-loss", 2.6f, 40f, MechanicHintStrategy.All, true),   // one slot left: Double Down (1200) is the last GCD
            ("gnb-dd-off", 2.6f, 40f, MechanicHintStrategy.Off, false),
            ("gnb-dd-unknown-return", 2.6f, float.MaxValue, MechanicHintStrategy.All, false),
        })
        {
            var (world, player, target, hints, manager, bossmods) = NewCombat(Class.GNB);
            using (bossmods)
            using (manager)
            {
                var module = new BossMod.Autorotation.akechi.AkechiGNB(manager, player);
                var strategy = new StrategyValues(BossMod.Autorotation.akechi.AkechiGNB.Definition().Configs);
                ((StrategyValueTrack)strategy.Values[strategy.Configs.FindIndex(c => c.InternalName == "MechanicHints")]).Option = (int)mode;
                world.Client.GaugePayload = new(2ul << 0, 0); // 2 cartridges (GunbreakerGauge.Ammo is the first byte)
                RefreshTargets(hints, target);
                if (loss < float.MaxValue)
                    Push(world, TestNamespace, loss, ret);
                module.Execute(strategy, target, 0.1f, false);
                var dd = Queued(hints, ActionID.MakeSpell(BossMod.GNB.AID.DoubleDown), ActionQueue.Priority.High + 1000);
                Check(dd == expectDD, name, $"DoubleDown queued={dd}");
                ExternalMechanicHintProvider.ClearNamespace(TestNamespace);
            }
        }
    }
```
`RunJobDecisionTests` から `GnbTests();` を呼ぶ。ゲージのバイト位置は `FFXIVClientStructs` の `GunbreakerGauge` 定義で確認し合わせる。

- [ ] **Step 2: 失敗確認**: `gnb-dd-before-loss` fail。

- [ ] **Step 3: ヒント源と供給**

`AkechiGNBPlanner74HintSource` に `MechanicForecast = 35,`。フィーダ群(2633-2640)に `Planner74FeedMechanicForecastHints();` を追加:
```csharp
    private void Planner74FeedMechanicForecastHints()
    {
        AkechiGNBPlanner74HintBus.ClearSource(AkechiGNBPlanner74HintSource.MechanicForecast);
        if (!Player.InCombat || !Mechanic.Enabled || Mechanic.DowntimeNow || SkSGCDLength <= 0f)
            return;
        var horizon = SkSGCDLength * Planner74HorizonSlots + TimingTargetWindowFuturePadding;
        if (Mechanic.TargetLossIn > horizon)
            return;
        var returnAt = MathF.Min(horizon, Mechanic.TargetReturnIn);
        AkechiGNBPlanner74HintBus.PushWindowAbsolute(CombatTimer + Mechanic.TargetLossIn, CombatTimer + returnAt, targetable: false,
            AkechiGNBPlanner74HintSource.MechanicForecast, "Mechanic forecast: target loss", priority: HintPriorityBossTimelineDowntime + 10);
        if (Mechanic.ReturnKnown && Mechanic.TargetReturnIn < horizon)
            AkechiGNBPlanner74HintBus.PushWindowAbsolute(CombatTimer + Mechanic.TargetReturnIn, CombatTimer + horizon, targetable: true,
                AkechiGNBPlanner74HintSource.MechanicForecast, "Mechanic forecast: target return", priority: HintPriorityBossTimelineDowntime + 10);
    }
```

- [ ] **Step 4: 消失前消費(ルール 2)**

Sonic Break 分岐(2999-3024)の条件に `|| Mechanic.ExpiresDuringLoss(SBstatus)`、Reign 分岐(3050-3072)に `|| Mechanic.ExpiresDuringLoss(Rstatus)`(`Rstatus` = Ready to Reign 残り、2573 行)を OR で追加(既存の `In3y`・`targetableNow` 条件は維持)。

- [ ] **Step 5: 締め(ルール 2b)**

`Execution` の planner 実行後、通常 GCD キューの前に:
```csharp
        if (Mechanic.Enabled && targetableNow && GunComboStep == 0)
        {
            Span<WindDownCandidate> gcds = stackalloc WindDownCandidate[4];
            var n = 0;
            var ammo = (int)Ammo;
            if (Unlocked(AID.DoubleDown) && ammo >= GameRuleDoubleDownCartridgeCost) { gcds[n++] = new(ActionID.MakeSpell(AID.DoubleDown), [WindDownPotency.Of(WindDownPotency.GNB, (uint)AID.DoubleDown)], ReadyIn(AID.DoubleDown), RecastRecoveredIn(AID.DoubleDown)); ammo -= GameRuleDoubleDownCartridgeCost; }
            if (Unlocked(AID.GnashingFang) && ammo >= 1) { gcds[n++] = new(ActionID.MakeSpell(AID.GnashingFang), [WindDownPotency.Of(WindDownPotency.GNB, (uint)AID.GnashingFang), WindDownPotency.Of(WindDownPotency.GNB, (uint)AID.SavageClaw), WindDownPotency.Of(WindDownPotency.GNB, (uint)AID.WickedTalon)], ReadyIn(AID.GnashingFang), RecastRecoveredIn(AID.GnashingFang)); ammo -= 1; }
            if (SBstatus > 0) gcds[n++] = new(ActionID.MakeSpell(AID.SonicBreak), [WindDownPotency.Of(WindDownPotency.GNB, (uint)AID.SonicBreak)], 0, 0);
            if (ammo >= 1) gcds[n++] = new(ActionID.MakeSpell(AID.BurstStrike), [WindDownPotency.Of(WindDownPotency.GNB, (uint)AID.BurstStrike)], 0, 0);
            var pick = WindDown.SelectGcd(gcds[..n], Mechanic, GCD, SkSGCDLength, 0.6f, WindDownPotency.FillerGNB);
            if (pick >= 0)
                QueueGCD((AID)gcds[pick].Action.ID, primaryTarget?.Actor, GCDPriority.VeryHigh + 5);
        }
```
`GunComboStep`(436 行、GF・Reign 連携の段、0 = 連携なし)が 0 のときだけ締めを評価(連携の継続は既存経路)。oGCD(Blasting Zone、Bow Shock)は同様に `WindDown.SelectOgcd` で `QueueOGCD(..., OGCDPriority.Severe + 5)`:
```csharp
            Span<WindDownCandidate> ogcds = stackalloc WindDownCandidate[2];
            var m = 0;
            var zone = BestZone; // 既存の Danger Zone / Blasting Zone 選択(3026-3047 で使っている AID)
            if (Unlocked(zone)) ogcds[m++] = new(ActionID.MakeSpell(zone), [WindDownPotency.Of(WindDownPotency.GNB, (uint)AID.BlastingZone)], ReadyIn(zone), RecastRecoveredIn(zone));
            if (Unlocked(AID.BowShock)) ogcds[m++] = new(ActionID.MakeSpell(AID.BowShock), [WindDownPotency.Of(WindDownPotency.GNB, (uint)AID.BowShock)], ReadyIn(AID.BowShock), RecastRecoveredIn(AID.BowShock));
            var op = WindDown.SelectOgcd(ogcds[..m], Mechanic, SkSGCDLength);
            if (op >= 0)
                QueueOGCD((AID)ogcds[op].Action.ID, primaryTarget?.Actor, OGCDPriority.Severe + 5);
```
`BestZone` は 3026-3047 の Zone 分岐が使う AID 選択式の名前に合わせる(式が inline なら同じ式を `private AID BestZone => ...` として切り出す)。

- [ ] **Step 6: テスト+計測**

```bash
$H mechanic-hints
mkdir -p $S/t8/all $S/t8/off
XAN_HARNESS_TRACE_DIR=$S/t8/all $H timeline-matrix --job gnb --target-loss-hints 30 > /dev/null
XAN_HARNESS_TRACE_DIR=$S/t8/off $H timeline-matrix --job gnb --target-loss-hints 30 --track MechanicHints=Off > /dev/null
```
Expected: `failures=0`。`off` = Task 7 後の `post-tlh` と一致。`all` vs `off` の summary potency: 総和で改善、悪化 run は分岐を調べて原因を記録。`timeline-combat-matrix --job gnb --target-loss-hints 30` と `timeline-matrix --job gnb --random-disengage 7 --disengage-forecast on` でも All vs Off を比較。

- [ ] **Step 7: チェックポイント**

---

### Task 9: MCH の予告挙動

**Files:**
- Modify: `xan/Ranged/MCH.cs`(`GetWildfireTarget` 2454、`ShouldHypercharge` 2430、`ShouldMinion` 2111、`ShouldStabilize` 2699、Exec 923-939、`PushDyingTargetFinishers` 960 付近)
- Modify: `MechanicHintsSelfTest.JobDecisions.cs`

- [ ] **Step 1: 失敗するテスト**

`MchTests()`(GNB と同じ組立て、`Class.MCH`、モジュール `BossMod.Autorotation.xan.MCH`、`module.Execute(strategy, target, 0.1f, false)`):
- `mch-wf-hold`: Wildfire ready、予告 loss 5 / return 40 → Wildfire 未キュー。
- `mch-wf-off`: 同条件 Off → Wildfire キュー(既定 Wildfire=ASAP)。
- `mch-drill-last`: Drill 2 チャージ、予告 loss 2.6 / return 40 → Drill が GCD 最高優先(`hints.ActionsToExecute.FindBest(world, player, world.Client.Cooldowns, 0, hints, 0.1f, false).Action == Drill`)。
クールダウン設定は `world.Execute(new ClientState.OpCooldown(false, [(ActionDefinitions.Instance.Spell(AID.Wildfire)!.MainCooldownGroup, new(0, 0))]))` 形式(Program.cs:1170 の `Cooldown` 関数と同じ)。

- [ ] **Step 2: 失敗確認**

- [ ] **Step 3: 保留(ルール 1)**

`GetWildfireTarget` の Delay 判定直後:
```csharp
        if (Mechanic.ShouldHoldWindow(10f, 120f, GCDLength))
            return null;
```
`ShouldHypercharge` の `if (DowntimeIn < GCD + 6) return false;` 直後:
```csharp
        if (Mechanic.Enabled && Mechanic.TargetLossIn < GCD + 6)
            return false;
```
`ShouldMinion` の Never 判定直後:
```csharp
        // the Queen's attacks after the loss are wasted; battery does not grow during downtime, so waiting costs nothing unless it caps
        if (Mechanic.ReturnKnown && !Mechanic.DowntimeNow && Mechanic.TargetLossIn < Battery / 5f && !BatteryOvercapSoon)
            return false;
```
`ShouldStabilize` の ReassembleLeft 判定直後:
```csharp
        if (Mechanic.ShouldHoldWindow(3 * GCDLength, 120f, GCDLength))
            return false;
```

- [ ] **Step 4: 消失前消費(ルール 2)**

`ToolGCDs` 先頭(Overheated 判定の後):
```csharp
        if (ExcavatorLeft > 0 && Mechanic.ExpiresDuringLoss(ExcavatorLeft))
            PushGCD(AID.Excavator, primaryTarget, 45);
        if (FMFLeft > 0 && Mechanic.ExpiresDuringLoss(FMFLeft))
            PushGCD(AID.FullMetalField, primaryTarget, 45);
```
`PushDyingTargetFinishers` と同じ位置に消失直前の Detonator / Overdrive:
```csharp
        if (Mechanic.Enabled && !Mechanic.DowntimeNow && Mechanic.TargetLossIn < GCD + 0.6f)
        {
            if (WildfireLeft > Mechanic.TargetLossIn && Unlocked(AID.Detonator) && ReadyIn(AID.Detonator) <= GCD)
                PushOGCD(AID.Detonator, Player, priority: 2);
            if (HasMinion)
            {
                var overdrive = BestActionUnlocked(AID.QueenOverdrive, AID.RookOverdrive);
                if (ReadyIn(overdrive) <= GCD)
                    PushOGCD(overdrive, Player);
            }
        }
```
Hypercharged 効果の失効: `ShouldHypercharge` 先頭の前提判定後に `if (HyperchargedLeft > 0 && Mechanic.ExpiresDuringLoss(HyperchargedLeft) && Mechanic.TargetLossIn >= 5 * 1.5f) return true;`。

- [ ] **Step 5: 締め(ルール 2b)**

Exec の非 Overheated 分岐、`ToolGCDs` の前:
```csharp
            if (Mechanic.Enabled)
            {
                Span<WindDownCandidate> gcds = stackalloc WindDownCandidate[6];
                var n = 0;
                void Add(AID aid, float readyIn, float recovered)
                {
                    if (Unlocked(aid))
                        gcds[n++] = new(ActionID.MakeSpell(aid), [WindDownPotency.Of(WindDownPotency.MCH, (uint)aid)], readyIn, recovered);
                }
                Add(AID.Drill, ReadyIn(AID.Drill), RecastRecoveredIn(AID.Drill));
                Add(AID.AirAnchor, ReadyIn(AID.AirAnchor), RecastRecoveredIn(AID.AirAnchor));
                Add(AID.ChainSaw, ReadyIn(AID.ChainSaw), RecastRecoveredIn(AID.ChainSaw));
                if (ExcavatorLeft > 0) Add(AID.Excavator, 0, 0);
                if (FMFLeft > 0) Add(AID.FullMetalField, 0, 0);
                if (Heat >= 50 || HyperchargedLeft > 0)
                {
                    // five Blazing Shots at 1.5 s fill three 2.5 s slots
                    var perSlot = WindDownPotency.Of(WindDownPotency.MCH, (uint)AID.BlazingShot) * GCDLength / 1.5f;
                    gcds[n++] = new(ActionID.MakeSpell(AID.Hypercharge), [perSlot, perSlot, perSlot], ReadyIn(AID.Hypercharge), 0);
                }
                var pick = WindDown.SelectGcd(gcds[..n], Mechanic, GCD, GCDLength, 0.6f, WindDownPotency.FillerMCH);
                if (pick >= 0)
                {
                    var aid = (AID)gcds[pick].Action.ID;
                    if (aid == AID.Hypercharge)
                        PushOGCD(AID.Hypercharge, Player, 5);
                    else
                        PushGCD(aid, primaryTarget, 60);
                }
            }
```
oGCD: Double Check / Checkmate を `WindDown.SelectOgcd` → `PushOGCD(aid, primaryTarget, 3)`(`UseCharges` 呼出の前)。

- [ ] **Step 6: 復帰準備(ルール 4)**

Exec の OGCD 呼出(938)の直前、ターゲット無しでも到達する位置:
```csharp
        if (Mechanic.DowntimeNow && Mechanic.ReturnKnown && Mechanic.TargetReturnIn <= 5f && ReassembleLeft == 0 && ReadyIn(AID.Reassemble) <= GCD)
            PushOGCD(AID.Reassemble, Player, 50);
```

- [ ] **Step 7: テスト+計測**

`$H mechanic-hints` → `failures=0`。`timeline-matrix --job mch --target-loss-hints 30` を All / Off で比較(Off は Task 4 の `t4` 同ジョブ run と一致)。MCH はハーネスにゲージ模倣がないので威力差は参考値、例外なし・失敗行なしを確認。

- [ ] **Step 8: チェックポイント**

---

### Task 10: VPR の予告挙動

**Files:**
- Modify: `xan/Melee/VPR.cs`(`ShouldReawaken` 1019、`ShouldVice` 1279、`ShouldUseSerpentsIre` 1816、Exec 365-395)
- Modify: `MechanicHintsSelfTest.JobDecisions.cs`

- [ ] **Step 1: 失敗するテスト** `VprTests()`:
- `vpr-reawaken-hold`: Ready to Reawaken 状態(SID は `BossMod/ActionQueue/Melee/VPR.cs` の `ReadyToReawaken`)付与、予告 loss 6 / return 40 → Reawaken 未キュー(残 2 枠では完走不可、かつ締め評価で 2 枠分 [750, 960] が Vicewinder より低ければ未選択)。期待は締めの DP 結果に従う: テストは `WindDown.Plan` の出力を同条件で計算して期待値にする。
- `vpr-range-loss`: `hints.Disengage = new(float.MaxValue, 0, 4, 6)`(射程外 4 秒後)、Ready to Reawaken → Reawaken 未キュー。
- `vpr-off`: 同条件 Off → Reawaken キュー。

- [ ] **Step 2: 失敗確認**

- [ ] **Step 3: 保留・射程外(ルール 1, 3)**

`ShouldReawaken` の `if (NumAOETargets == 0) return false;` 直後:
```csharp
        var sequence = ReawakenSequenceDuration();
        if (Mechanic.Enabled && (Mechanic.ReturnKnown && !Mechanic.DowntimeNow && Mechanic.TargetLossIn < sequence || Mechanic.RangeLossIn < sequence))
            return false; // a cut sequence is only started by the wind-down, which weighs the steps that fit
```
`ShouldVice` の `if (DreadCombo != 0 || ...)` 直後:
```csharp
        if (Mechanic.ReturnKnown && !Mechanic.DowntimeNow && Mechanic.TargetLossIn < 3 * AttackGCDLength)
            return false;
```
`ShouldUseSerpentsIre` の `_wantPotionNow` 判定直後:
```csharp
        if (Mechanic.ShouldHoldWindow(ReawakenSequenceDuration() + AttackGCDLength, 120f, AttackGCDLength))
            return false;
```

- [ ] **Step 4: 消失前消費(ルール 2)**

Ready to Reawaken の残りが `Mechanic.ExpiresDuringLoss(ReawakenReady)` なら締め候補の Reawaken を必ず含める(Step 5 の候補構築で `ReadyIn = 0`、`RecoveredIn = 0`)。

- [ ] **Step 5: 締め(ルール 2b)**

Exec の Generation push(367-382)の後、wait 分岐の前:
```csharp
        if (Mechanic.Enabled && DreadCombo == 0 && Anguine == 0 && primaryTarget != null)
        {
            Span<WindDownCandidate> gcds = stackalloc WindDownCandidate[3];
            var n = 0;
            float P(AID aid) => WindDownPotency.Of(WindDownPotency.VPR, (uint)aid);
            if (Unlocked(AID.Vicewinder))
                gcds[n++] = new(ActionID.MakeSpell(AID.Vicewinder), [P(AID.Vicewinder), P(AID.HuntersCoil), P(AID.SwiftskinsCoil)], ReadyIn(AID.Vicewinder), RecastRecoveredIn(AID.Vicewinder));
            if (Unlocked(AID.Reawaken) && (ReawakenReady > 0 || Offering >= 50))
            {
                var gen = P(AID.FirstGeneration);
                gcds[n++] = new(ActionID.MakeSpell(AID.Reawaken), Unlocked(AID.Ouroboros) ? [P(AID.Reawaken), gen, gen, gen, gen, P(AID.Ouroboros)] : [P(AID.Reawaken), gen, gen, gen, gen], 0, 0);
            }
            if (Coil > 0 && Unlocked(AID.UncoiledFury))
                gcds[n++] = new(ActionID.MakeSpell(AID.UncoiledFury), [P(AID.UncoiledFury)], 0, 0);
            var pick = WindDown.SelectGcd(gcds[..n], Mechanic, GCD, AttackGCDLength, 0.6f, WindDownPotency.FillerVPR);
            if (pick >= 0)
                PushGCD((AID)gcds[pick].Action.ID, primaryTarget, 40);
        }
```
(優先度 40 は Reawaken 30 より上、Generation 継続は `Anguine > 0` ガードで締めが割り込まない。)

- [ ] **Step 6: テスト+計測**(`timeline-matrix --job vpr --target-loss-hints 30` と `--random-disengage 7 --disengage-forecast on` を All/Off で、Off は t4 と一致)

- [ ] **Step 7: チェックポイント**

---

### Task 11: NIN の予告挙動

**Files:**
- Modify: `xan/Melee/NIN.cs`(Exec 366-441、PK 492、Raiju 495-502、`ShouldPK` 1345、`PickMudra` 1548、Mug 1654、`ShouldUseTrickActionNow` 1709、`ShouldUseKassatsuNow` 1964、`ShouldUseTenChiJinNow` 1999、`ShouldUseTenriJindoNow` 2034、`ShouldUseBunshinNow` 2051)
- Modify: `MechanicHintsSelfTest.JobDecisions.cs`

- [ ] **Step 1: 失敗するテスト** `NinTests()`(`Class.NIN`、モジュール `BossMod.Autorotation.xan.NIN`):
- `nin-tcj-forced-move`: TCJ ready、Kunai's Bane 効果中(ターゲットに SID 付与)、`hints.Disengage = new(1.0f, 1f, float.MaxValue, float.MaxValue)` → Ten Chi Jin 未キュー。
- `nin-dokumori-hold`: Dokumori ready、予告 loss 10 / return 40 → Dokumori 未キュー。
- `nin-dokumori-off`: 同 Off → キュー。

- [ ] **Step 2: 失敗確認**

- [ ] **Step 3: 保留(ルール 1)と TCJ(ルール 3)**

Mug(1654):
```csharp
        if (buffsOk && TargetMugLeft == 0 && !Mechanic.ShouldHoldWindow(20f, 120f, GCDLength))
            PushOGCD(MugAction, primaryTarget, priority: 100);
```
`ShouldUseTrickActionNow` 先頭: `if (Mechanic.ShouldHoldWindow(15f, 60f, GCDLength)) return false;`
`ShouldUseKassatsuNow` 先頭: `if (Mechanic.ShouldHoldWindow(15f, 60f, GCDLength)) return false;`
`ShouldUseBunshinNow` 先頭: `if (Mechanic.ShouldHoldWindow(30f, 90f, GCDLength)) return false;`
`ShouldUseTenChiJinNow` 先頭:
```csharp
        if (Mechanic.Enabled && (Mechanic.ForcedMoveWithin(3 * GCDLength) || Mechanic.LossWithin(3 * GCDLength)))
            return false; // three stationary GCDs: a move or a loss inside them breaks the sequence
        if (Mechanic.ShouldHoldWindow(6f, 120f, GCDLength))
            return false;
```

- [ ] **Step 4: 消失前消費(ルール 2)**

`ShouldPK` 先頭: `if (Mechanic.ExpiresDuringLoss(PhantomKamaitachi)) return true;`
Raiju push 条件(495)に `|| Raiju.Stacks > 0 && Mechanic.ExpiresDuringLoss(Raiju.Left)` を追加(既存の hold 判定群をバイパスする OR、距離による Forked/Fleeting の分岐は既存のまま)。
`ShouldUseTenriJindoNow` 先頭: `if (TenriJindo > 0 && Mechanic.ExpiresDuringLoss(TenriJindo)) return true;`
Kassatsu 中: 忍術ブロック(504-536)の開始条件に `|| Kassatsu > 0 && Mechanic.ExpiresDuringLoss(Kassatsu)`。

- [ ] **Step 5: 締め(ルール 2b)**

GCD 層の先頭(TCJ 分岐 445 の前、`Mudra.Left == 0 && TenChiJin == 0` のときのみ):
```csharp
        if (Mechanic.Enabled && Mudra.Left == 0 && TenChiJin.Left == 0 && primaryTarget != null)
        {
            Span<WindDownCandidate> gcds = stackalloc WindDownCandidate[3];
            var n = 0;
            if (Unlocked(AID.Raiton) && HasMudraCharge)
                gcds[n++] = new(ActionID.MakeSpell(AID.Raiton), [WindDownPotency.Of(WindDownPotency.NIN, (uint)AID.Raiton)], ReadyIn(AID.Ten1), RecastRecoveredIn(AID.Ten1), ExtraTime: 1.0f);
            if (Raiju.Stacks > 0)
                gcds[n++] = new(ActionID.MakeSpell(AID.FleetingRaiju), [WindDownPotency.Of(WindDownPotency.NIN, (uint)AID.FleetingRaiju)], 0, 0);
            if (PhantomKamaitachi > 0)
                gcds[n++] = new(ActionID.MakeSpell(AID.PhantomKamaitachi), [WindDownPotency.Of(WindDownPotency.NIN, (uint)AID.PhantomKamaitachi)], 0, 0);
            var pick = WindDown.SelectGcd(gcds[..n], Mechanic, GCD, GCDLength, 0.6f, WindDownPotency.FillerNIN);
            if (pick >= 0)
            {
                var aid = (AID)gcds[pick].Action.ID;
                if (aid == AID.Raiton)
                    UseMudra(AID.Raiton, NinjutsuTarget(false, primaryTarget), startCondition: true);
                else
                    PushGCD(aid, primaryTarget, 30);
            }
        }
```
(`Mudra`・`TenChiJin`・`Raiju` は 374-391 の `Status(...)` スナップショット、`HasMudraCharge` は 236 行。Raiju は近接の Fleeting 固定、射程外の Forked は既存トラック経路のまま。)

- [ ] **Step 6: 復帰準備(ルール 4)**

`PickMudra`(1548):
```csharp
        if (!Unlocked(mudra))
            return (AID.None, null);
```
に変え、最終忍術を積む箇所(同関数内で忍術 AID を返す分岐)で `target == null` なら `(AID.None, null)` を返す(印押下はターゲット不要)。Exec のカウントダウン分岐(434)の直前に:
```csharp
        if (Mechanic.DowntimeNow && Mechanic.ReturnKnown && Mechanic.TargetReturnIn < 6 && Unlocked(AID.Suiton) && ShadowWalker == 0 && Mudra.Left == 0)
        {
            UseMudra(AID.Suiton, NinjutsuTarget(false, primaryTarget), endCondition: Mechanic.TargetReturnIn < 1);
            return;
        }
```

- [ ] **Step 7: テスト**(NIN はハーネス面なし。`$H mechanic-hints` → `failures=0`、加えて `tools/nin_regression` の既存検証があれば実行し変化を記録)

- [ ] **Step 8: チェックポイント**

---

### Task 12: PLD(不具合修正+予告挙動)

**Files:**
- Modify: `akechi/Tank/AkechiPLD.cs`(353-357 補助、Atonement 570-588、Holy 590-622、Ranged 624-649、Goring 510-527)
- Modify: `MechanicHintsSelfTest.JobDecisions.cs`

- [ ] **Step 1: 失敗するテスト** `PldTests()`(`Class.PLD`、モジュール `BossMod.Autorotation.akechi.AkechiPLD`):
- `pld-holy-casttime`: Divine Might なし・停止中・ターゲット 10y・Ranged=RangedCast → キューの Holy Spirit エントリの `CastTime > 0`。
- `pld-fof-hold`: FoF ready、予告 loss 10 / return 40 → FoF 未キュー。
- `pld-fof-off`: 同 Off → キュー。

- [ ] **Step 2: 失敗確認**

- [ ] **Step 3: 詠唱時間を渡す(不具合修正、全選択肢)**

Ranged 分岐のキュー(649 付近):
```csharp
            if (rCondition && rTarget != null && rAction != AID.None)
            {
                // a hard-cast Holy Spirit must carry its cast time, or the forced-move cast cap (hints.MaxCastTime) cannot stop it
                var castTime = rAction == AID.HolySpirit && !holyInstant
                    ? Math.Max(0f, ActionDefinitions.Instance.Spell(AID.HolySpirit)!.CastTime * SpSGCDLength / 2.5f - 0.5f)
                    : 0f;
                QueueGCD(rAction, rTarget, rPriority, castTime: castTime);
            }
```
修正前後で `timeline-matrix --job gnb` 等は無関係(PLD はハーネス面なし)。テスト `pld-holy-casttime` のみで確認。

- [ ] **Step 4: 保留(ルール 1)**

353-357:
```csharp
    private float EffectiveDowntimeIn => Mechanic.Enabled ? Math.Min(DowntimeIn, Mechanic.TargetLossIn) : DowntimeIn;
    private bool HasKnownDowntime => Player.InCombat && EffectiveDowntimeIn >= 0f && EffectiveDowntimeIn < float.MaxValue && !float.IsNaN(EffectiveDowntimeIn) && !float.IsInfinity(EffectiveDowntimeIn);
    private bool ActionLikelyLandsBeforeDowntime(float safety = 0.8f) => !HasKnownDowntime || EffectiveDowntimeIn > safety;
    private bool CanUseMajorBuffBeforeDowntime(BuffsStrategy strategy, float requiredUptime)
        => strategy is BuffsStrategy.Force or BuffsStrategy.ForceWeave || !HasKnownDowntime || EffectiveDowntimeIn >= requiredUptime;
```

- [ ] **Step 5: 消失前消費(ルール 2)**

Atonement 分岐の `!dmacHold || ...` を `!dmacHold || lossSpend || ...` に、Holy 分岐の `holyAutoAllowed` にも `|| lossSpend` を。`lossSpend` は分岐直前で:
```csharp
            var atonementLeft = Math.Max(Atonement.Left, Math.Max(Supplication.Left, Sepulchre.Left));
            var lossSpend = Mechanic.ExpiresDuringLoss(DivineMight.Left) || Mechanic.ExpiresDuringLoss(atonementLeft);
```
Confiteor 系: Blade 分岐(536-561)の Automatic 条件に `|| Requiescat.IsActive && Mechanic.ExpiresDuringLoss(Requiescat.Left) || Confiteor.IsActive && Mechanic.ExpiresDuringLoss(Confiteor.Left)`。BoH(503-508): `|| BladeOfHonor.IsReady && Mechanic.ExpiresDuringLoss(BladeOfHonor.Left)`。(変数は 364-395 のスナップショット: `DivineMight`、`Atonement`、`Supplication`、`Sepulchre`、`Confiteor`、`Requiescat`、`BladeOfHonor` の `.Left/.IsActive/.IsReady`。)

- [ ] **Step 6: 締め(ルール 2b)**

コンボ GCD キュー(447)の直後:
```csharp
        // chains in progress (Supplication/Sepulchre step, Blade combo step) keep their existing path
        if (Mechanic.Enabled && mainTarget != null && BladeComboStep == 0 && !Supplication.IsActive && !Sepulchre.IsActive)
        {
            Span<WindDownCandidate> gcds = stackalloc WindDownCandidate[4];
            var n = 0;
            float P(AID aid) => WindDownPotency.Of(WindDownPotency.PLD, (uint)aid);
            if (GoringBlade.IsReady) gcds[n++] = new(ActionID.MakeSpell(AID.GoringBlade), [P(AID.GoringBlade)], 0, 0);
            if (DivineMight.IsActive && MP >= 1000) gcds[n++] = new(ActionID.MakeSpell(AID.HolySpirit), [P(AID.HolySpirit)], 0, 0);
            if (Atonement.IsReady) gcds[n++] = new(ActionID.MakeSpell(AID.Atonement), [P(AID.Atonement), P(AID.Supplication), P(AID.Sepulchre)], 0, 0);
            if (Confiteor.IsReady) gcds[n++] = new(ActionID.MakeSpell(AID.Confiteor), [P(AID.Confiteor), P(AID.BladeOfFaith), P(AID.BladeOfTruth), P(AID.BladeOfValor)], 0, 0);
            var pick = WindDown.SelectGcd(gcds[..n], Mechanic, GCD, SkSGCDLength, 0.6f, WindDownPotency.FillerPLD);
            if (pick >= 0)
                QueueGCD((AID)gcds[pick].Action.ID, mainTarget, GCDPriority.VeryHigh + 5);

            Span<WindDownCandidate> ogcds = stackalloc WindDownCandidate[3];
            var m = 0;
            if (Unlocked(AID.CircleOfScorn)) ogcds[m++] = new(ActionID.MakeSpell(AID.CircleOfScorn), [P(AID.CircleOfScorn)], ReadyIn(AID.CircleOfScorn), RecastRecoveredIn(AID.CircleOfScorn));
            var sw = Unlocked(AID.Expiacion) ? AID.Expiacion : AID.SpiritsWithin;
            if (Unlocked(sw)) ogcds[m++] = new(ActionID.MakeSpell(sw), [P(AID.Expiacion)], ReadyIn(sw), RecastRecoveredIn(sw));
            if (BladeOfHonor.IsReady) ogcds[m++] = new(ActionID.MakeSpell(AID.BladeOfHonor), [P(AID.BladeOfHonor)], 0, 0);
            var op = WindDown.SelectOgcd(ogcds[..m], Mechanic, SkSGCDLength);
            if (op >= 0)
                QueueOGCD((AID)ogcds[op].Action.ID, (AID)ogcds[op].Action.ID == AID.CircleOfScorn ? Player : mainTarget, OGCDPriority.Severe + 5);
        }
```
(変数は 364-395 のスナップショット、`BladeComboStep` = `gauge.ConfiteorComboStep`。Circle of Scorn は自己中心 5y、既存 474-477 と同じく `Player` 対象。)

- [ ] **Step 7: テスト**(`$H mechanic-hints` → `failures=0`)

- [ ] **Step 8: チェックポイント**

---

### Task 13: 総合検証・書き戻し・記録

- [ ] **Step 1: 全面計測(最終バイナリ)**

```bash
for surf in "timeline-matrix --job all" "timeline-matrix --job all --target-loss-hints 30" "dmu-full --job all" "timeline-matrix --job all --random-disengage 7 --disengage-forecast on"; do
  tag=$(echo "$surf" | tr ' ' '_' | tr -d '-'); mkdir -p $S/final/$tag
  XAN_HARNESS_TRACE_DIR=$S/final/$tag $H $surf > $S/final/$tag/stdout.txt 2>&1
done
```
`--track MechanicHints=Off` 付きでも同 4 面を `final-off` に。判定:
- `final-off` = ベースライン(ただし Task 7 採用分の GNB 差分は Task 7 記録どおり)。
- `final` vs ベースライン: ジョブ別総威力。悪化ジョブは原因を調べるまで書き戻さない。
- `$H mechanic-hints` と `$H potency-audit` とも `failures=0`。
- BLM は `timeline-combat-matrix --job blm --target-loss-hints 30` で Task 6 と同値。

- [ ] **Step 2: レビュー**

`superpowers:requesting-code-review` で全差分レビュー。指摘対応。

- [ ] **Step 3: 書き戻し**

```bash
git -C F:/bossmodreborn status --short | head
ls $(git -C F:/bossmodreborn rev-parse --git-dir)/MERGE_HEAD $(git -C F:/bossmodreborn rev-parse --git-dir)/CHERRY_PICK_HEAD 2>/dev/null
git -C F:/bossmodreborn diff --stat
```
本体の未コミット差分が Task 0 取込時から変化していないこと(`git -C F:/bossmodreborn diff HEAD -- BossMod/Autorotation/Standard/xan/Casters/BLM.cs` と `/tmp/blm_transpose.patch` の一致)を確認。変化があれば差分を先に worktree に取込んで再検証。その後、worktree の変更ファイル一覧(`git diff --name-only`+未追跡の新規ファイル)を本体へコピー(bin/obj は除外)。本体でプラグイン本体を `dotnet build BossMod -c Release -p:Platform=x64` し成功確認(本体 bin は通常ビルド出力先なので可、配置はユーザー判断)。

- [ ] **Step 4: メモリ更新**

`C:\Users\happy\.claude\projects\F--bossmodreborn\memory\` に「メカニクス予告ヒント」メモ(トラック名・4 択・既定・締めルール・検証コマンド・未解決事項)を作成し MEMORY.md に 1 行追加。BLM メモの「ExternalHints 既定 Off」記述を更新。
