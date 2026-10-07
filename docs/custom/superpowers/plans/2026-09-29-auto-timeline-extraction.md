# リプレイからのタイムライン自動抽出 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 録画が止まるたびに、そのコンテンツのリプレイ要約から裏でタイムラインを作り直し、窓単位の holdout に通ったものだけを `timelines\auto\<zone>-auto.json` として予告に使えるようにする。

**Architecture:** 抽出器の入力を「pull 要約(`PullSummary`)」に置き換え、リプレイは 1 本ずつ解析して要約をキャッシュする。再構築は要約だけから行い、品質ゲート(`AutoTimelineGate`)で再現しない窓を落とし、既存ソースより弱ければ書かない。裏スレッド 1 本(`AutoTimelineExtractor`)が録画完了イベントと設定画面のボタンから仕事を受ける。

**Tech Stack:** C# / .NET 10、Dalamud プラグイン(BossModReborn)、System.Text.Json、回帰ツール(`tools/timeline_regression`、`tools/external_timeline_regression` の `Check`/`Require`)。

**Spec:** `docs/superpowers/specs/2026-09-29-auto-timeline-extraction-design.md`

## Global Constraints

- 作業は worktree `F:\bossmodreborn\.claude\worktrees\kind-bassi-424582` のブランチ `claude/auto-extract` だけで行う。`F:\bossmodreborn` 本体や他の worktree には書かない。
- `git push`・PR 作成は禁止。`git add -A` 禁止(パスを指定してコミット)。`git stash` を素で使わない。
- コミットメッセージの末尾は `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
- プラグインのビルドは `dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64`(x64 必須)。ツールはビルド時にプラグインの dll をコピーするので、プラグインを変えたら必ずツールもビルドし直す。
- 回帰ツールの実行: `dotnet tools/timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/TimelineRegression.dll`(現在 44/44)、`dotnet tools/external_timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/ExternalTimelineRegression.dll`(現在 318/318)、`dotnet tools/fflogs_timeline_extract/bin/Debug/net10.0-windows10.0.26100.0/FFLogsTimelineExtract.dll --self-test`(現在 15/15)。どのタスクの終わりでも全部合格していること。
- 既定値(仕様より): 自動抽出 `AutoExtractTimelines` 既定 true(`UseExternalTimelineHints` に依存)。最小 pull 数 3。窓の採用は照合 2 本以上かつ的中 3 分の 2 以上。的中 = 予測開始が実際の窓(8.5 s 以上)の [開始 −5 s, 終了] に入る。対応付けは ±10 s 以内で最も近い予測窓。公開対象の窓 = NoTarget・長さ 8.5 s 以上・確信度 0.5 以上。
- 順位(仕様 2.5): 手動 Replay > User > AutoReplay > FFLogs > Cactbot > EventTrigger。`TimelineSource.AutoReplay` は既存の値の後ろ(= 5)。
- 出力先: `<timelines>\auto\<zone>-auto.json`、要約 `<timelines>\auto\cache\<リプレイのファイル名>.json`、レポート `<timelines>\auto\report.txt`。`<timelines>` は `TimelineStore.UserDirectory`(既定 `%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\timelines`)。
- ファイルは一時名に書いてから置き換える。手で置いたファイル(`<timelines>` 直下)は読み書きしない。
- 実データ: リプレイ `%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\replays`(374 本)、旧リプレイ `%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays`(47 本)、アライアンス 23 本の写し `%TEMP%\tl_alliance`。

## Review Focus

1. 裏で要約を作っている最中にプラグインが終了する → 終了が数秒以内に返り、書きかけの要約・出力が残らない(Task 5 のチェック「dispose during a slow summarize」)。
2. キューに入れたリプレイが解析前に消えている(`MaxReplays` の削除、手で削除)→ 例外にならず、「失敗」も記録しない(次に同名が現れても処理できる)(Task 5 のチェック「missing replay is skipped」)。
3. 要約キャッシュが壊れている(書き込み中の強制終了、手での編集)→ 古い要約として扱われ、作り直される。再構築では読み飛ばされる(Task 2 のチェック「corrupt cache is stale」)。
4. 設定でタイムラインのフォルダを途中で変えた → 次の仕事から新しいフォルダを使う(Task 5 のチェック「directory is read per job」)。
5. ワイプで途中までしか無い pull が混ざる → その pull は、自分が続いた時刻より後の窓の照合に数えない(Task 3 のチェック「wiped pull is not evaluated past its end」)。

---

## ファイル構成

- 作成 `BossMod/Timeline/External/PullSummary.cs`: pull 要約の型、`Summarize(Pull, bossOIDs)`、要約からの主ボス HP 取得。
- 変更 `BossMod/Timeline/External/ReplayTimelineExtractor.cs`: `Build` / `BuildBossSet` / `Extract` を要約入力に置き換え、`BossSets` と `Assemble` を公開。既存の `Build(zone, IReadOnlyList<Pull>)` と `Extract(IEnumerable<Replay>)` は要約を作って呼ぶ包みにする。
- 作成 `BossMod/Timeline/External/ReplaySummaryCache.cs`: 要約ファイルの型・読み書き・鮮度判定。
- 作成 `BossMod/Timeline/External/AutoTimelineGate.cs`: 品質ゲートと、書くかどうかの判定・出力・レポート。
- 作成 `BossMod/Timeline/External/AutoTimelineExtractor.cs`: 裏スレッドの作業キュー。
- 変更 `BossMod/Timeline/External/ExternalPlannerTimeline.cs`: `TimelineSource.AutoReplay`、`SuppressApply`。
- 変更 `BossMod/Timeline/External/TimelineStore.cs`: `auto\` の読み込みと順位。
- 変更 `BossMod/Timeline/External/ExternalTimelineHints.cs`: `MinWindowConfidence` を public に。
- 変更 `BossMod/Replay/ReplayManagementWindow.cs`: 録画完了イベント、ログフォルダの公開。
- 変更 `BossMod/BossModule/BossModuleConfig.cs`: 設定とボタン・状態表示。
- 変更 `BossMod/Framework/Plugin.cs`: 生成・購読・破棄。
- 変更 `tools/replay_timeline_extract/Program.cs`: 1 本ずつ要約して手放す、`--auto` モード、ピークメモリ表示。
- テスト: `tools/timeline_regression/Program.cs`(合成リプレイを使う同等性)、`tools/external_timeline_regression/Program.cs`(キャッシュ・ゲート・順位・キュー)。

---

### Task 1: pull 要約と、要約を入力にする抽出器

**Files:**
- Create: `BossMod/Timeline/External/PullSummary.cs`
- Modify: `BossMod/Timeline/External/ReplayTimelineExtractor.cs`(`Build` 169-222、`BossEvents` 224-233、`PrimaryBossHPPercent` 371-380、`BuildBossSet` 382-406、`Extract` 408-430)
- Test: `tools/timeline_regression/Program.cs`

**Interfaces:**
- Produces:
  - `public sealed record PullSummary(ushort Zone, DateTime Start, float Duration, IReadOnlyList<uint> BossOIDs, IReadOnlyList<PullSummary.BossEvent> Events, IReadOnlyList<ReplayTimelineExtractor.Window> NoTarget, IReadOnlyList<ReplayTimelineExtractor.Window> BossUntargetable, IReadOnlyList<PullSummary.BossHP> Bosses)`
  - `PullSummary.BossEvent(float Time, uint ID, ExternalPlannerTimeline.ExternalStateKind Kind)`、`PullSummary.BossHP(uint OID, uint MaxHP, IReadOnlyList<PullSummary.TickRange> Existence, IReadOnlyList<PullSummary.HPPoint> History)`、`PullSummary.TickRange(long Start, long End)`、`PullSummary.HPPoint(long Ticks, uint CurHP, uint MaxHP)`
  - `public static PullSummary PullSummary.Summarize(ReplayTimelineExtractor.Pull pull, IReadOnlyList<uint>? eventBossOIDs = null)`
  - `public static float? PullSummary.PrimaryBossHPPercent(PullSummary pull, float seconds)`
  - `ReplayTimelineExtractor.Build(ushort zone, IReadOnlyList<PullSummary> pulls)`、`BuildBossSet(ushort zone, IReadOnlyList<PullSummary> pulls)`(public)、`BossSets(IEnumerable<PullSummary> zonePulls)`(`IReadOnlyList<IReadOnlyList<PullSummary>>`、Extract と同じ順)、`Assemble(ushort zone, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> sets, ExternalPlannerTimeline.TimelineSource source)`、`Extract(IEnumerable<PullSummary> pulls)`(`IReadOnlyDictionary<ushort, TimelineDefinition>`)
  - 既存の `Build(ushort, IReadOnlyList<Pull>)`、`Extract(IEnumerable<Replay>)`、`PrimaryBossHPPercent(Pull, float)` はそのまま使える(包み)。

- [ ] **Step 1: 変更前の出力を基準として保存する**

プラグインとツールを今の状態でビルドし、CLI の出力と、ハーネスの 2 条件の CSV を残す(後で完全一致を確かめる)。

```bash
cd /f/bossmodreborn/.claude/worktrees/kind-bassi-424582
dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64 -v q
dotnet build tools/replay_timeline_extract -c Debug -v q
dotnet build tools/timeline_hint_harness -c Debug -v q
X=tools/replay_timeline_extract/bin/Debug/net10.0-windows10.0.26100.0/ReplayTimelineExtract.dll
H=tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll
T="$(cygpath -w "$TEMP")"; R="$APPDATA/XIVLauncher/pluginConfigs/BossModReborn/replays"
mkdir -p "$TEMP/ae_base_al" "$TEMP/ae_base_old" "$TEMP/ae_base_cly"
dotnet $X --out "$T\\ae_base_al" "$T\\tl_alliance" > "$TEMP/ae_base_al.txt"
dotnet $X --out "$T\\ae_base_old" "$(cygpath -w "$APPDATA/XIVLauncher/pluginConfigs/BossMod/replays")" > "$TEMP/ae_base_old.txt"
args=(); for f in $(ls "$R"/TheClyteum_*.log | head -20); do args+=("$(cygpath -w "$f")"); done
dotnet $X --out "$T\\ae_base_cly" "${args[@]}" > "$TEMP/ae_base_cly.txt"
dotnet $H --holdout --sync all --csv "$T\\ae_base_A.csv" "$T\\tl_alliance" > "$TEMP/ae_base_A.txt"
dotnet $H --holdout --sync all --csv "$T\\ae_base_D.csv" "$(cygpath -w "$APPDATA/XIVLauncher/pluginConfigs/BossMod/replays")" > "$TEMP/ae_base_D.txt"
```

Expected: `ae_base_al` に 1248/1304/1368、`ae_base_old` に旧 47 本の zone、`ae_base_cly` に 1345 の json。A の集計行は `hit=67 ... false_alarms=0`、D は `hit=20 ... false_alarms=3`。

- [ ] **Step 2: 失敗するテストを書く**

`tools/timeline_regression/Program.cs` の既存チェック群の後ろ(`Extract` を使うチェックの近く)に足す。合成リプレイの作り方(`Fight`、`WithEnemies`)は同じファイルの既存チェックに合わせる。

```csharp
Check("summaries give the same timeline as replays", () =>
{
    // Several pulls of one fight with shifted timings plus a second boss: the replay path and the summary path must serialize identically.
    var replays = new[] { 0f, 2f, 4f }.Select(shift => Fight(1, shift)).ToList();
    var viaReplays = ReplayTimelineExtractor.Extract(replays);
    var summaries = replays.SelectMany(r => ReplayTimelineExtractor.FindPulls(r).Select(p => PullSummary.Summarize(p))).ToList();
    var viaSummaries = ReplayTimelineExtractor.Extract(summaries);
    Require(viaReplays.Count == viaSummaries.Count, "zone count differs");
    foreach (var (zone, timeline) in viaReplays)
        Require(System.Text.Json.JsonSerializer.Serialize(timeline) == System.Text.Json.JsonSerializer.Serialize(viaSummaries[zone]), $"zone {zone} differs");
});
Check("summaries survive a JSON round trip", () =>
{
    var pull = ReplayTimelineExtractor.FindPulls(Fight(1, 0)).Single();
    var summary = PullSummary.Summarize(pull);
    var back = System.Text.Json.JsonSerializer.Deserialize<PullSummary>(System.Text.Json.JsonSerializer.Serialize(summary))!;
    Require(System.Text.Json.JsonSerializer.Serialize(ReplayTimelineExtractor.Extract([summary])) == System.Text.Json.JsonSerializer.Serialize(ReplayTimelineExtractor.Extract([back])), "round trip changed the extraction");
    Require(back.Start == summary.Start && back.Events.Count == summary.Events.Count, "round trip lost data");
});
Check("summary HP lookup matches the replay", () =>
{
    var pull = ReplayTimelineExtractor.FindPulls(Fight(1, 0)).Single();
    var summary = PullSummary.Summarize(pull);
    foreach (var s in new[] { -1f, 0f, 3.3f, 10f, 20.5f, 1000f })
        Require(ReplayTimelineExtractor.PrimaryBossHPPercent(pull, s) == PullSummary.PrimaryBossHPPercent(summary, s), $"HP differs at {s}");
});
```

`Fight(1, shift)` が HP 履歴を持たない場合は、既存の HP 分岐チェック(同ファイルで `PrimaryBossHPPercent` を呼ぶもの)と同じ作り方で HP を足した合成リプレイを使う。

- [ ] **Step 3: 失敗を確認する**

Run: `dotnet build tools/timeline_regression -c Debug -v q`
Expected: コンパイルエラー `PullSummary` が見つからない。

- [ ] **Step 4: `PullSummary.cs` を作る**

```csharp
namespace BossMod;

// Everything the timeline extractor reads from one pull, detached from the replay: once a replay is summarized it can be dropped,
// and the summaries of every replay of a zone are enough to rebuild its timeline. Times are seconds from the pull start, except the
// boss HP history and existence, which keep absolute ticks so the HP lookup at a decision point matches the replay lookup exactly.
public sealed record PullSummary(ushort Zone, DateTime Start, float Duration, IReadOnlyList<uint> BossOIDs, IReadOnlyList<PullSummary.BossEvent> Events,
    IReadOnlyList<ReplayTimelineExtractor.Window> NoTarget, IReadOnlyList<ReplayTimelineExtractor.Window> BossUntargetable, IReadOnlyList<PullSummary.BossHP> Bosses)
{
    public sealed record BossEvent(float Time, uint ID, ExternalPlannerTimeline.ExternalStateKind Kind);
    public sealed record TickRange(long Start, long End);
    public sealed record HPPoint(long Ticks, uint CurHP, uint MaxHP);
    public sealed record BossHP(uint OID, uint MaxHP, IReadOnlyList<TickRange> Existence, IReadOnlyList<HPPoint> History);

    // eventBossOIDs: whose casts and effects count as sync points. Build takes them from the first pull of a boss set, so the
    // wrapper that builds from raw pulls passes that list; a pull summarized on its own uses its own bosses.
    public static PullSummary Summarize(ReplayTimelineExtractor.Pull pull, IReadOnlyList<uint>? eventBossOIDs = null)
    {
        var events = ReplayTimelineExtractor.BossEvents(pull, (eventBossOIDs ?? pull.BossOIDs).ToList())
            .OrderBy(e => e.Time) // stable: ties keep the cast-then-effect order Build saw before
            .Select(e => new BossEvent(pull.Seconds(e.Time), e.ID, e.Kind))
            .ToList();
        var bosses = pull.Enemies.Where(p => pull.BossOIDs.Contains(p.OID))
            .Select(p => new BossHP(p.OID, ReplayTimelineExtractor.MaxHP(p),
                p.WorldExistence.Select(r => new TickRange(r.Start.Ticks, r.End.Ticks)).ToList(),
                p.HPMPHistory.Select(kv => new HPPoint(kv.Key.Ticks, kv.Value.CurHP, kv.Value.MaxHP)).ToList()))
            .ToList();
        return new(pull.Zone, pull.Start, pull.Seconds(pull.End), pull.BossOIDs.ToList(), events,
            ReplayTimelineExtractor.NoTargetWindows(pull).ToList(), ReplayTimelineExtractor.BossUntargetableWindows(pull).ToList(), bosses);
    }

    // Same as ReplayTimelineExtractor.PrimaryBossHPPercent(Pull, seconds): of the bosses existing at that moment, the one with the
    // largest max HP (first in pull order on ties), and its latest HP entry at or before the moment.
    public static float? PrimaryBossHPPercent(PullSummary pull, float seconds)
    {
        var t = pull.Start.AddSeconds(seconds).Ticks;
        BossHP? boss = null;
        foreach (var b in pull.Bosses)
            if (b.MaxHP > 0 && b.Existence.Any(r => t >= r.Start && t <= r.End) && (boss == null || b.MaxHP > boss.MaxHP))
                boss = b;
        if (boss == null)
            return null;
        HPPoint? hp = null;
        foreach (var point in boss.History)
        {
            if (point.Ticks > t)
                break;
            hp = point;
        }
        return hp is { MaxHP: > 0 } ? 100f * hp.CurHP / hp.MaxHP : null;
    }
}
```

注: `HPMPHistory` は時刻順の `SortedList` なので `History` も時刻順。`PrimaryBossHPPercent(Pull)` の `OrderByDescending(MaxHP).FirstOrDefault()` は同値なら先頭を取るので、上の「より大きいときだけ置き換え」と同じ。

- [ ] **Step 5: 抽出器を要約入力に置き換える**

`ReplayTimelineExtractor.cs` で次を行う。

1. `BossEvents` と `MaxHP` を `internal static` にする(`PullSummary` から呼ぶ)。`BossEvents` の戻り値の型はそのまま。
2. `Build(ushort zone, IReadOnlyList<Pull> pulls)` を包みにし、要約版を足す。

```csharp
public static ExternalPlannerTimeline.TimelineDefinition? Build(ushort zone, IReadOnlyList<Pull> pulls)
    => pulls.Count == 0 ? null : Build(zone, pulls.Select(p => PullSummary.Summarize(p, pulls[0].BossOIDs)).ToList());

public static ExternalPlannerTimeline.TimelineDefinition? Build(ushort zone, IReadOnlyList<PullSummary> pulls)
{
    if (pulls.Count == 0)
        return null;
    var bossOIDs = pulls[0].BossOIDs.ToList();
    var majority = (pulls.Count + 1) / 2;

    // Sync points: the k-th occurrence of an action id in a pull is matched to the k-th occurrence in every other pull.
    Dictionary<(uint ID, int Ordinal, ExternalPlannerTimeline.ExternalStateKind Kind), List<float>> samples = [];
    foreach (var pull in pulls)
    {
        Dictionary<(uint, ExternalPlannerTimeline.ExternalStateKind), int> seen = [];
        var events = pull.Events;
        // A repeater must actually repeat: with a handful of events one occurrence already exceeds the share.
        var dominant = events.GroupBy(e => e.ID).Where(g => g.Count() > 1 && g.Count() > events.Count * DominantSyncShare).Select(g => g.Key).ToHashSet();
        foreach (var e in events.Where(e => !dominant.Contains(e.ID)).OrderBy(e => e.Time))
        {
            var ordinal = seen.GetValueOrDefault((e.ID, e.Kind));
            seen[(e.ID, e.Kind)] = ordinal + 1;
            ref var times = ref CollectionsMarshal.GetValueRefOrAddDefault(samples, (e.ID, ordinal, e.Kind), out _);
            (times ??= []).Add(e.Time);
        }
    }
    // (以降は現在の Build の states / windows / AddsPresent / sequence / confidence の組み立てをそのまま残し、
    //  pulls.Select(NoTargetWindows) を pulls.Select(p => p.NoTarget)、pulls.Select(BossUntargetableWindows) を pulls.Select(p => p.BossUntargetable) に置き換える)
}
```

`MergeWindows` の引数型 `IReadOnlyList<IReadOnlyList<Window>>` はそのまま使える(`p.NoTarget` は `IReadOnlyList<Window>`)。

3. `BuildBossSet` を要約版にして public にする。変更点は次のとおりで、残りは今のコードのまま。

```csharp
public static ExternalPlannerTimeline.TimelineDefinition BuildBossSet(ushort zone, IReadOnlyList<PullSummary> pulls)
{
    var perPull = pulls.Select(p => p.NoTarget).ToList();
    if (DetectBranch(perPull) is not { } branch)
        return Build(zone, pulls)!;

    var early = Build(zone, branch.Early.Select(i => pulls[i]).ToList())!;
    var late = Build(zone, branch.Late.Select(i => pulls[i]).ToList())!;
    var earlySequence = early.Sequences[0];
    var lateSequence = late.Sequences[0];
    var earlyWindowStart = Median(branch.Early.Select(i => LongWindows(perPull[i])[branch.WindowIndex].Start).ToList());
    List<IReadOnlyList<(float Time, uint ID)>> Events(int[] indices)
        => indices.Select(i => (IReadOnlyList<(float, uint)>)pulls[i].Events.Select(e => (e.Time, e.ID)).ToList()).ToList();
    var earlyEvents = Events(branch.Early);
    var lateEvents = Events(branch.Late);
    var exclusionEnd = earlyWindowStart + BranchCastExclusionSeconds;
    var decision = DivergenceTime(earlySequence, lateSequence, earlyWindowStart, BranchCastExclusionSeconds, BranchMaxLeadSeconds,
        state => AcceptDivergence(state, earlyEvents, lateEvents, exclusionEnd)) ?? earlyWindowStart;
    List<float> HPs(int[] indices) => indices.Select(i => PullSummary.PrimaryBossHPPercent(pulls[i], decision)).OfType<float>().ToList();
    var threshold = LearnThreshold(HPs(branch.Early), HPs(branch.Late));
    return new(zone, early.SourceFile, true,
        [earlySequence with { Branch = new(0, decision, threshold, true) }, lateSequence with { Branch = new(0, decision, threshold, false) }],
        ExternalPlannerTimeline.TimelineSource.Replay, (early.Confidence + late.Confidence) / 2f);
}
```

注: 今の `Events` は `BossEvents(pulls[i], pulls[i].BossOIDs)` を DateTime のまま作ってから秒にしている(並べ替えなし)。`AcceptDivergence` は順序に依存しないので、要約の(並べ替え済みの)`Events` で同じ結果になる。

4. `Extract` を 3 つに分ける。

```csharp
public static IReadOnlyDictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> Extract(IEnumerable<Replay> replays)
    => Extract(replays.SelectMany(r => FindPulls(r).Select(p => PullSummary.Summarize(p))));

public static IReadOnlyDictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> Extract(IEnumerable<PullSummary> pulls)
{
    Dictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> result = [];
    foreach (var group in pulls.Where(p => p.Zone != 0).GroupBy(p => p.Zone))
        result[group.Key] = Assemble(group.Key, BossSets(group).Select(set => BuildBossSet(group.Key, set)).ToList(), ExternalPlannerTimeline.TimelineSource.Replay);
    return result;
}

// Dungeons have several bosses per zone: each boss set becomes its own sequence, in the order the bosses were first met.
public static IReadOnlyList<IReadOnlyList<PullSummary>> BossSets(IEnumerable<PullSummary> zonePulls)
    => zonePulls.GroupBy(p => string.Join(",", p.BossOIDs)).OrderBy(g => g.Min(p => p.Start)).Select(g => (IReadOnlyList<PullSummary>)g.ToList()).ToList();

// A branching boss set contributes its two siblings next to each other; they share the early sibling's number as Group.
public static ExternalPlannerTimeline.TimelineDefinition Assemble(ushort zone, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> sets, ExternalPlannerTimeline.TimelineSource source)
{
    List<ExternalPlannerTimeline.TimelineSequence> sequences = [];
    foreach (var set in sets)
    {
        var first = sequences.Count;
        foreach (var sequence in set.Sequences)
            sequences.Add(sequence with { Index = sequences.Count, Branch = sequence.Branch is { } b ? b with { Group = first } : null });
    }
    return new(zone, string.Join(";", sets.Select(s => s.SourceFile)), true, sequences, source, sets.Average(s => s.Confidence));
}
```

注: 今の `Extract` は `pulls.GroupBy(p => p.Zone)` の前に全 zone の pull を `ToList()` している。要約版は `IEnumerable` のまま流すので、`Extract(IEnumerable<Replay>)` では 1 本ずつ要約して手放せる(呼び出し側がリプレイを保持しなければ)。`GroupBy` の順は最初に現れた順で、今と同じ。

5. `PrimaryBossHPPercent(Pull, float)` は残す(テストとハーネスが使う)。

- [ ] **Step 6: 単体チェックを通す**

Run: `dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64 -v q && dotnet build tools/timeline_regression -c Debug -v q && dotnet tools/timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/TimelineRegression.dll`
Expected: `tests=47 passed=47 failed=0`(44 + 新しい 3)。外部回帰ツールと fflogs の self-test も合格。

- [ ] **Step 7: 実データで完全一致を確かめる**

Step 1 と同じコマンドを出力先 `ae_new_*` で実行し、比べる。

```bash
for d in al old cly; do diff -r "$TEMP/ae_base_$d" "$TEMP/ae_new_$d" && echo "$d SAME"; done
diff <(sort "$TEMP/ae_base_A.csv") <(sort "$TEMP/ae_new_A.csv") && echo A SAME
diff <(sort "$TEMP/ae_base_D.csv") <(sort "$TEMP/ae_new_D.csv") && echo D SAME
```

Expected: 5 つとも SAME。違ったら、差の出た zone の JSON を整形して比べ、原因(並び順、float の丸め、HP 検索)を直す。一致するまで先へ進まない。

- [ ] **Step 8: コミット**

```bash
git add BossMod/Timeline/External/PullSummary.cs BossMod/Timeline/External/ReplayTimelineExtractor.cs tools/timeline_regression/Program.cs
git commit -m "refactor(timeline): extract timelines from pull summaries instead of whole replays

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: 要約キャッシュ

**Files:**
- Create: `BossMod/Timeline/External/ReplaySummaryCache.cs`
- Test: `tools/external_timeline_regression/Program.cs`

**Interfaces:**
- Consumes: `PullSummary`(Task 1)
- Produces:
  - `public sealed record ReplaySummaryFile(int Version, long SourceSize, long SourceWriteTicks, bool Failed, IReadOnlyList<PullSummary> Pulls)`
  - `public static class ReplaySummaryCache` with `const int Version = 1`、`string CacheDirectory(string timelinesDir)`、`string CachePath(string timelinesDir, string replayPath)`、`ReplaySummaryFile? Read(string cachePath)`(読めない・版違いは null)、`ReplaySummaryFile? ReadFresh(string cachePath, FileInfo replay)`(サイズ・更新時刻・版が一致したときだけ)、`void Write(string cachePath, ReplaySummaryFile file)`(一時名 → 置き換え)、`ReplaySummaryFile ForReplay(FileInfo replay, IReadOnlyList<PullSummary> pulls, bool failed)`

- [ ] **Step 1: 失敗するテストを書く**

`tools/external_timeline_regression/Program.cs` の `try` ブロック内、既存チェックの後ろに足す。

```csharp
Check("summary cache round trip and freshness", () =>
{
    var dir = Path.Combine(fixtureRoot, "cache1");
    Directory.CreateDirectory(dir);
    var replay = new FileInfo(Path.Combine(dir, "Some_Duty_2026.log"));
    File.WriteAllText(replay.FullName, "x");
    replay.Refresh();
    var pull = new PullSummary(900, new DateTime(2026, 9, 29, 1, 2, 3, DateTimeKind.Utc), 60f, [0x100u],
        [new(1.5f, 1234, ExternalPlannerTimeline.ExternalStateKind.CastStart)], [new(20f, 35f)], [], []);
    var path = ReplaySummaryCache.CachePath(dir, replay.FullName);
    ReplaySummaryCache.Write(path, ReplaySummaryCache.ForReplay(replay, [pull], false));
    var fresh = ReplaySummaryCache.ReadFresh(path, replay);
    Require(fresh != null && fresh.Pulls.Count == 1 && fresh.Pulls[0].NoTarget[0].End == 35f, "fresh read failed");
    File.AppendAllText(replay.FullName, "more");
    replay.Refresh();
    Require(ReplaySummaryCache.ReadFresh(path, replay) == null, "changed replay still fresh");
    Require(ReplaySummaryCache.Read(path) != null, "stale cache unreadable for rebuilds");
    Require(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "temp file left behind");
});
Check("corrupt cache is stale", () =>
{
    var dir = Path.Combine(fixtureRoot, "cache2");
    Directory.CreateDirectory(dir);
    var replay = new FileInfo(Path.Combine(dir, "Other_2026.log"));
    File.WriteAllText(replay.FullName, "x");
    replay.Refresh();
    var path = ReplaySummaryCache.CachePath(dir, replay.FullName);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "{\"Version\":1,\"Pulls\":[{");
    Require(ReplaySummaryCache.Read(path) == null && ReplaySummaryCache.ReadFresh(path, replay) == null, "corrupt cache accepted");
    File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(ReplaySummaryCache.ForReplay(replay, [], false) with { Version = ReplaySummaryCache.Version + 1 }));
    Require(ReplaySummaryCache.Read(path) == null, "other version accepted");
});
```

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet build tools/external_timeline_regression -c Debug -v q`
Expected: コンパイルエラー `ReplaySummaryCache` が見つからない。

- [ ] **Step 3: 実装する**

```csharp
using System.IO;
using System.Text.Json;

namespace BossMod;

// One replay's pull summaries, remembered next to the automatic timelines so a replay is parsed once. The source size and write
// time tell whether the replay changed since; a failed parse is remembered too, so a broken file is not retried on every rebuild.
public sealed record ReplaySummaryFile(int Version, long SourceSize, long SourceWriteTicks, bool Failed, IReadOnlyList<PullSummary> Pulls);

public static class ReplaySummaryCache
{
    public const int Version = 1;

    public static string CacheDirectory(string timelinesDir) => Path.Combine(timelinesDir, "auto", "cache");
    public static string CachePath(string timelinesDir, string replayPath) => Path.Combine(CacheDirectory(timelinesDir), Path.GetFileName(replayPath) + ".json");

    public static ReplaySummaryFile ForReplay(FileInfo replay, IReadOnlyList<PullSummary> pulls, bool failed)
        => new(Version, replay.Length, replay.LastWriteTimeUtc.Ticks, failed, pulls);

    // Null when the file is missing, unreadable, of another version, or not valid JSON: the caller treats it as never summarized.
    public static ReplaySummaryFile? Read(string cachePath)
    {
        try
        {
            if (!File.Exists(cachePath))
                return null;
            using var stream = File.OpenRead(cachePath);
            var file = JsonSerializer.Deserialize<ReplaySummaryFile>(stream);
            return file is { Version: Version, Pulls: not null } ? file : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    public static ReplaySummaryFile? ReadFresh(string cachePath, FileInfo replay)
        => Read(cachePath) is { } file && file.SourceSize == replay.Length && file.SourceWriteTicks == replay.LastWriteTimeUtc.Ticks ? file : null;

    public static void Write(string cachePath, ReplaySummaryFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
        var temp = cachePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(file));
        File.Move(temp, cachePath, overwrite: true);
    }
}
```

- [ ] **Step 4: 合格を確認する**

Run: プラグイン → `tools/external_timeline_regression` をビルドして実行。
Expected: `tests=320 passed=320 failed=0`。他の 2 ツールも合格。

- [ ] **Step 5: コミット**

```bash
git add BossMod/Timeline/External/ReplaySummaryCache.cs tools/external_timeline_regression/Program.cs
git commit -m "feat(timeline): cache replay pull summaries next to the automatic timelines

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: 品質ゲート(窓単位の holdout)

**Files:**
- Create: `BossMod/Timeline/External/AutoTimelineGate.cs`
- Modify: `BossMod/Timeline/External/ExternalTimelineHints.cs:24`(`MinWindowConfidence` を `public const` に)
- Modify: `BossMod/Timeline/External/ExternalPlannerTimeline.cs:180`(`TimelineSource` に `AutoReplay` を追加)
- Test: `tools/external_timeline_regression/Program.cs`

**Interfaces:**
- Consumes: `ReplayTimelineExtractor.BossSets` / `BuildBossSet` / `Assemble`、`PullSummary.PrimaryBossHPPercent`(Task 1)
- Produces:
  - `public enum TimelineSource { EventTrigger, Cactbot, Replay, User, FFLogs, AutoReplay }`
  - `public static class AutoTimelineGate` with `const int MinPulls = 3`、`const int MinEvaluated = 2`、`const float HitSlackBefore = 5f`、`const float MatchTolerance = 10f`、`static bool Publishable(ExternalPlannerTimeline.TimelineWindow w)`、`static AutoTimelineGate.Result Evaluate(ushort zone, IReadOnlyList<PullSummary> zonePulls)`
  - `public sealed record AutoTimelineGate.Result(ExternalPlannerTimeline.TimelineDefinition? Timeline, int KeptPublishable, IReadOnlyList<string> Report)`(`Timeline` は Source = AutoReplay。pull が無ければ null)

- [ ] **Step 1: 失敗するテストを書く**

```csharp
// A pull summary with the given NoTarget windows and a fixed sync cast, for gate checks.
PullSummary GatePull(ushort zone, uint boss, float duration, params (float Start, float End)[] windows)
    => new(zone, DateTime.UnixEpoch.AddHours(zone).AddMinutes(windows.Length + duration), duration, [boss],
        [new(5f, 4321, ExternalPlannerTimeline.ExternalStateKind.CastStart)],
        windows.Select(w => new ReplayTimelineExtractor.Window(w.Start, w.End)).ToList(), [], []);
Check("gate keeps a window every pull reproduces", () =>
{
    var pulls = new[] { 30f, 31f, 30.5f, 29.5f }.Select(s => GatePull(9001, 0x10, 120f, (s, s + 15f))).ToList();
    var result = AutoTimelineGate.Evaluate(9001, pulls);
    Require(result.Timeline is { Source: ExternalPlannerTimeline.TimelineSource.AutoReplay }, "no auto timeline");
    Require(result.KeptPublishable == 1, $"kept {result.KeptPublishable}");
});
Check("gate drops a window that does not reproduce", () =>
{
    // Three pulls go away at 30 s, three at 40 s, each for 9 s. The merged window (35-44 s, confidence 0.5) is publishable, but
    // holding out a 30 s pull predicts 40 s (after its window ended at 39 s) and holding out a 40 s pull predicts 30 s (more than
    // 5 s before its window): no held-out pull is hit.
    var pulls = new[] { 30f, 30f, 30f, 40f, 40f, 40f }.Select(s => GatePull(9002, 0x10, 120f, (s, s + 9f))).ToList();
    var result = AutoTimelineGate.Evaluate(9002, pulls);
    Require(result.KeptPublishable == 0, $"kept {result.KeptPublishable}");
    Require(result.Report.Any(l => l.Contains("dropped")), "no dropped line in the report");
});
Check("gate withholds windows below three pulls", () =>
{
    var pulls = new[] { 30f, 30f }.Select(s => GatePull(9003, 0x10, 120f, (s, s + 15f))).ToList();
    var result = AutoTimelineGate.Evaluate(9003, pulls);
    Require(result.KeptPublishable == 0 && result.Timeline!.Sequences.All(s => s.Windows is not { Count: > 0 }), "windows kept from two pulls");
    Require(result.Timeline.Sequences.Single().States.Count > 0, "sync points lost");
});
Check("wiped pull is not evaluated past its end", () =>
{
    // Three full pulls reproduce the window at 60 s; two wipes at 40 s must not count against it.
    var pulls = new[] { GatePull(9004, 0x10, 120f, (60f, 75f)), GatePull(9004, 0x10, 120f, (60f, 75f)), GatePull(9004, 0x10, 120f, (61f, 76f)),
        GatePull(9004, 0x10, 40f), GatePull(9004, 0x10, 40f) }.ToList();
    var result = AutoTimelineGate.Evaluate(9004, pulls);
    Require(result.KeptPublishable == 1, $"kept {result.KeptPublishable}");
});
Check("gate keeps short and non-NoTarget windows untouched", () =>
{
    var pulls = new[] { 30f, 31f, 30f }.Select(s => GatePull(9005, 0x10, 120f, (s, s + 3f), (s + 40f, s + 55f))).ToList();
    var result = AutoTimelineGate.Evaluate(9005, pulls);
    Require(result.Timeline!.Sequences.Single().Windows!.Any(w => w.End - w.Start < 5f), "short window removed");
});
```

注: 2 本目のチェックの数字は、確信度(`Confidence`: 四分位範囲 10 s → 0.5)がちょうど公開の下限になるよう選んである。数字を変えるときは、統合窓と holdout の窓がどちらも公開対象(8.5 s 以上・確信度 0.5 以上)のままで、外した pull では的中しないことを手で確かめること。

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet build tools/external_timeline_regression -c Debug -v q`
Expected: コンパイルエラー `AutoTimelineGate` が見つからない。

- [ ] **Step 3: `MinWindowConfidence` を公開し、`AutoReplay` を足す**

`ExternalTimelineHints.cs:24` の `private const float MinWindowConfidence = 0.5f;` を `public const float MinWindowConfidence = 0.5f;` にする。`ExternalPlannerTimeline.cs:180` を次にする。

```csharp
public enum TimelineSource { EventTrigger, Cactbot, Replay, User, FFLogs, AutoReplay }
```

- [ ] **Step 4: `AutoTimelineGate.cs` を作る**

```csharp
namespace BossMod;

// The quality gate of automatic timelines: a window is published only if the pulls it was learned from reproduce it. Each pull of a
// boss set is held out in turn, the set is rebuilt from the others, and the held-out pull's real NoTarget windows are compared with
// what that rebuild would publish. Windows most held-out pulls do not show (a deep dungeon's random trash, a fight that varies) are
// dropped; sync points and windows too short to publish are kept, since they only steer the clock.
public static class AutoTimelineGate
{
    public const int MinPulls = 3; // fewer pulls leave nothing to hold out against: the set keeps its sync points only
    public const int MinEvaluated = 2; // a window needs this many held-out pulls that could show it
    public const float HitSlackBefore = 5f; // same slack as the hint harness: a prediction this early still counts
    public const float MatchTolerance = 10f; // a held-out rebuild's window this close to the full one is the same window

    public sealed record Result(ExternalPlannerTimeline.TimelineDefinition? Timeline, int KeptPublishable, IReadOnlyList<string> Report);

    public static bool Publishable(ExternalPlannerTimeline.TimelineWindow w)
        => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget && w.End is { } end && end - w.Start >= ExternalTimelineHints.MinPublishedLoss
            && w.Confidence >= ExternalTimelineHints.MinWindowConfidence;

    public static Result Evaluate(ushort zone, IReadOnlyList<PullSummary> zonePulls)
    {
        var pulls = zonePulls.Where(p => p.Zone == zone).ToList();
        if (pulls.Count == 0)
            return new(null, 0, [$"zone={zone} no pulls"]);
        List<string> report = [$"zone={zone} pulls={pulls.Count}"];
        List<ExternalPlannerTimeline.TimelineDefinition> sets = [];
        var kept = 0;
        foreach (var set in ReplayTimelineExtractor.BossSets(pulls))
        {
            var full = ReplayTimelineExtractor.BuildBossSet(zone, set);
            var name = string.Join("+", set[0].BossOIDs.Select(o => o.ToString("X")));
            if (set.Count < MinPulls)
            {
                report.Add($"  set={name} pulls={set.Count}: fewer than {MinPulls} pulls, windows withheld");
                sets.Add(full with { Sequences = full.Sequences.Select(s => s with { Windows = null }).ToList() });
                continue;
            }
            report.Add($"  set={name} pulls={set.Count}");
            var holdouts = set.Select((p, i) => (Pull: p, Rebuilt: ReplayTimelineExtractor.BuildBossSet(zone, set.Where((_, j) => j != i).ToList()))).ToList();
            List<ExternalPlannerTimeline.TimelineSequence> sequences = [];
            foreach (var sequence in full.Sequences)
            {
                List<ExternalPlannerTimeline.TimelineWindow> windows = [];
                foreach (var w in sequence.Windows ?? [])
                {
                    if (!Publishable(w))
                    {
                        windows.Add(w);
                        continue;
                    }
                    var (evaluated, hits) = Score(w, sequence, holdouts);
                    var keep = evaluated >= MinEvaluated && hits * 3 >= evaluated * 2;
                    report.Add($"    window {w.Start:f1}-{w.End:f1} evaluated={evaluated} hits={hits} {(keep ? "kept" : "dropped")}");
                    if (keep)
                    {
                        windows.Add(w);
                        ++kept;
                    }
                }
                sequences.Add(sequence with { Windows = windows });
            }
            sets.Add(full with { Sequences = sequences });
        }
        return new(ReplayTimelineExtractor.Assemble(zone, sets, ExternalPlannerTimeline.TimelineSource.AutoReplay), kept, report);
    }

    private static (int Evaluated, int Hits) Score(ExternalPlannerTimeline.TimelineWindow w, ExternalPlannerTimeline.TimelineSequence sequence,
        List<(PullSummary Pull, ExternalPlannerTimeline.TimelineDefinition Rebuilt)> holdouts)
    {
        int evaluated = 0, hits = 0;
        foreach (var (pull, rebuilt) in holdouts)
        {
            if (pull.Duration < w.Start)
                continue; // wiped before the window
            if (HoldoutSequence(rebuilt, pull, sequence, w.Start) is not { } predicted)
                continue;
            ExternalPlannerTimeline.TimelineWindow? match = null;
            foreach (var h in predicted.Windows ?? [])
                if (Publishable(h) && Math.Abs(h.Start - w.Start) <= MatchTolerance && (match == null || Math.Abs(h.Start - w.Start) < Math.Abs(match.Start - w.Start)))
                    match = h;
            if (match == null)
                continue; // the other pulls do not predict it for this one
            ++evaluated;
            if (pull.NoTarget.Any(a => a.End - a.Start >= ExternalTimelineHints.MinPublishedLoss && match.Start >= a.Start - HitSlackBefore && match.Start <= a.End))
                ++hits;
        }
        return (evaluated, hits);
    }

    // Which sequence of the held-out rebuild predicts the window for this pull. Before an HP-gated branch point both siblings agree;
    // after it, the pull's primary boss HP at the decision point against the threshold picks the sibling, as the follower would, and a
    // pull that took the other sibling than the window's does not count. Without a threshold the part after the decision point is
    // not evaluated.
    private static ExternalPlannerTimeline.TimelineSequence? HoldoutSequence(ExternalPlannerTimeline.TimelineDefinition rebuilt, PullSummary pull,
        ExternalPlannerTimeline.TimelineSequence fullSequence, float windowStart)
    {
        var branch = fullSequence.Branch ?? rebuilt.Sequences.FirstOrDefault(s => s.Branch != null)?.Branch;
        if (branch == null || windowStart < branch.DecisionTime)
            return rebuilt.Sequences[0];
        if (branch.HpThreshold is not { } threshold || PullSummary.PrimaryBossHPPercent(pull, branch.DecisionTime) is not { } hp)
            return null;
        var below = hp < threshold;
        if (fullSequence.Branch != null && fullSequence.Branch.Below != below)
            return null;
        return rebuilt.Sequences.FirstOrDefault(s => s.Branch?.Below == below) ?? rebuilt.Sequences[0];
    }
}
```

- [ ] **Step 5: 合格を確認する**

Run: プラグイン → 外部回帰ツールをビルドして実行。
Expected: `tests=325 passed=325 failed=0`。他の 2 ツールも合格(`AutoReplay` を足しても既存の数値は変わらない)。

- [ ] **Step 6: コミット**

```bash
git add BossMod/Timeline/External/AutoTimelineGate.cs BossMod/Timeline/External/ExternalTimelineHints.cs BossMod/Timeline/External/ExternalPlannerTimeline.cs tools/external_timeline_regression/Program.cs
git commit -m "feat(timeline): hold out each pull to keep only reproducible windows in automatic timelines

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: 読み込みと順位、書くかどうかの判定と出力

**Files:**
- Modify: `BossMod/Timeline/External/TimelineStore.cs`(`LoadCandidates` 99-124、`Priority` 152-159)
- Modify: `BossMod/Timeline/External/AutoTimelineGate.cs`(判定・出力・レポートを追加)
- Test: `tools/external_timeline_regression/Program.cs`

**Interfaces:**
- Consumes: `AutoTimelineGate.Result`、`Publishable`(Task 3)
- Produces:
  - `public static string AutoTimelineGate.AutoDirectory(string timelinesDir)`(`<timelinesDir>\auto`)
  - `public static int AutoTimelineGate.CountPublishable(ExternalPlannerTimeline.TimelineDefinition timeline)`
  - `public static (bool Write, string Reason) AutoTimelineGate.Decide(int keptPublishable, ExternalPlannerTimeline.TimelineDefinition? lowerRanked)`
  - `public static string AutoTimelineGate.Publish(string timelinesDir, ushort zone, Result result, ExternalPlannerTimeline.TimelineDefinition? lowerRanked)`(書く/消す/レポートを行い、1 行の結果を返す)

- [ ] **Step 1: 失敗するテストを書く**

```csharp
Check("auto timelines rank between user files and FFLogs", () =>
{
    ExternalPlannerTimeline.TimelineDefinition Def(ExternalPlannerTimeline.TimelineSource s) => new(9100, s.ToString(), true, [], s, 0.5f);
    var ranked = TimelineStore.Rank([Def(ExternalPlannerTimeline.TimelineSource.EventTrigger), Def(ExternalPlannerTimeline.TimelineSource.AutoReplay),
        Def(ExternalPlannerTimeline.TimelineSource.FFLogs), Def(ExternalPlannerTimeline.TimelineSource.User), Def(ExternalPlannerTimeline.TimelineSource.Replay)]);
    Require(string.Join(",", ranked.Select(t => t.Source)) == "Replay,User,AutoReplay,FFLogs,EventTrigger", string.Join(",", ranked.Select(t => t.Source)));
});
Check("store reads the auto folder, manual files win", () =>
{
    var dir = Path.Combine(fixtureRoot, "store-auto");
    Directory.CreateDirectory(Path.Combine(dir, "auto", "cache"));
    ExternalPlannerTimeline.TimelineDefinition Def(ExternalPlannerTimeline.TimelineSource s, int zone) => new(zone, s.ToString(), true,
        [new(0, 0, [new(5f, "", ExternalPlannerTimeline.ExternalStateKind.CastStart, [1u], 0)], null)], s, 0.9f);
    File.WriteAllText(Path.Combine(dir, "9200-replay.json"), System.Text.Json.JsonSerializer.Serialize(new { Timelines = new[] { Def(ExternalPlannerTimeline.TimelineSource.Replay, 9200) } }));
    File.WriteAllText(Path.Combine(dir, "auto", "9200-auto.json"), System.Text.Json.JsonSerializer.Serialize(new { Timelines = new[] { Def(ExternalPlannerTimeline.TimelineSource.AutoReplay, 9200) } }));
    File.WriteAllText(Path.Combine(dir, "auto", "9201-auto.json"), System.Text.Json.JsonSerializer.Serialize(new { Timelines = new[] { Def(ExternalPlannerTimeline.TimelineSource.AutoReplay, 9201) } }));
    File.WriteAllText(Path.Combine(dir, "auto", "cache", "x.log.json"), "{\"Timelines\":[]}");
    var previous = TimelineStore.UserDirectory;
    try
    {
        TimelineStore.UserDirectory = dir;
        TimelineStore.Reload();
        Require(TimelineStore.ForZone(9200)?.Source == ExternalPlannerTimeline.TimelineSource.Replay, "manual file lost to auto");
        Require(TimelineStore.ForZone(9201)?.Source == ExternalPlannerTimeline.TimelineSource.AutoReplay, "auto file not loaded");
    }
    finally
    {
        TimelineStore.UserDirectory = previous;
        TimelineStore.Reload();
    }
});
Check("auto output is written, withheld or removed", () =>
{
    var dir = Path.Combine(fixtureRoot, "publish");
    var pulls = new[] { 30f, 31f, 30.5f }.Select(s => GatePull(9300, 0x10, 120f, (s, s + 15f))).ToList();
    var good = AutoTimelineGate.Evaluate(9300, pulls);
    AutoTimelineGate.Publish(dir, 9300, good, null);
    var file = Path.Combine(dir, "auto", "9300-auto.json");
    Require(File.Exists(file) && TimelineStore.LoadUserFile(file).Single().Source == ExternalPlannerTimeline.TimelineSource.AutoReplay, "not written");
    // An FFLogs timeline with more publishable windows than the auto one keeps the zone: the auto file is removed.
    var ffl = new ExternalPlannerTimeline.TimelineDefinition(9300, "ffl", true, [new(0, 0, [], null, null,
        [new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 10f, 30f, 0.9f), new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 60f, 80f, 0.9f)])],
        ExternalPlannerTimeline.TimelineSource.FFLogs, 0.9f);
    var line = AutoTimelineGate.Publish(dir, 9300, good, ffl);
    Require(!File.Exists(file) && line.Contains("FFLogs"), $"not withheld: {line}");
    var report = File.ReadAllText(Path.Combine(dir, "auto", "report.txt"));
    Require(report.Split('\n').Count(l => l.StartsWith("zone=9300", StringComparison.Ordinal)) == 1, "report keeps two blocks for one zone");
    Require(!Directory.EnumerateFiles(Path.Combine(dir, "auto"), "*.tmp").Any(), "temp file left behind");
});
Check("publishable windows of legacy sequences are counted", () =>
{
    var legacy = new ExternalPlannerTimeline.TimelineDefinition(9400, "et", true, [new(0, 0, [
        new(10f, "", ExternalPlannerTimeline.ExternalStateKind.Untargetable, [], 0), new(30f, "", ExternalPlannerTimeline.ExternalStateKind.Targetable, [], 0),
        new(40f, "", ExternalPlannerTimeline.ExternalStateKind.Untargetable, [], 0), new(44f, "", ExternalPlannerTimeline.ExternalStateKind.Targetable, [], 0)], null)],
        ExternalPlannerTimeline.TimelineSource.EventTrigger, 1f);
    Require(AutoTimelineGate.CountPublishable(legacy) == 1, $"counted {AutoTimelineGate.CountPublishable(legacy)}");
});
```

`TimelineState` と `TimelineSequence` と `TimelineDefinition` のコンストラクタ引数は `ExternalPlannerTimeline.cs` の実際の定義に合わせる(`TimelineState(Time, Name, Kind, IDs, …)` の最後の引数の意味と型、`TimelineDefinition.ZoneID` の型を確認し、テストの `Def(..., int zone)` の引数型をそれに合わせること)。

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet build tools/external_timeline_regression -c Debug -v q`
Expected: `Publish` / `CountPublishable` が無いコンパイルエラー。

- [ ] **Step 3: `TimelineStore` を直す**

`LoadCandidates` の直下の読み込みの後ろに、`auto\` を読む処理を足す(`auto\cache\` は読まない。`EnumerateFiles` は既定で直下のみ)。

```csharp
            if (directory != null && Directory.Exists(directory))
                foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                    candidates.AddRange(LoadUserFile(path));
            // Timelines the plugin extracted by itself sit one level down; they rank below the hand-placed ones (Priority).
            var autoDirectory = directory != null ? Path.Combine(directory, "auto") : null;
            if (autoDirectory != null && Directory.Exists(autoDirectory))
                foreach (var path in Directory.EnumerateFiles(autoDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                    candidates.AddRange(LoadUserFile(path));
```

`Priority` を次にする。

```csharp
    private static int Priority(ExternalPlannerTimeline.TimelineSource source) => source switch
    {
        ExternalPlannerTimeline.TimelineSource.Replay => 5,
        ExternalPlannerTimeline.TimelineSource.User => 4,
        ExternalPlannerTimeline.TimelineSource.AutoReplay => 3,
        ExternalPlannerTimeline.TimelineSource.FFLogs => 2,
        ExternalPlannerTimeline.TimelineSource.Cactbot => 1,
        _ => 0
    };
```

- [ ] **Step 4: 判定と出力を `AutoTimelineGate` に足す**

```csharp
    public static string AutoDirectory(string timelinesDir) => Path.Combine(timelinesDir, "auto");

    // Publishable windows of a timeline: measured windows through Publishable, legacy sequences through their untargetable markers.
    public static int CountPublishable(ExternalPlannerTimeline.TimelineDefinition timeline)
    {
        var count = 0;
        foreach (var sequence in timeline.Sequences)
        {
            if (sequence.Windows is { Count: > 0 } windows)
            {
                count += windows.Count(Publishable);
                continue;
            }
            float? lossAt = null;
            foreach (var state in sequence.States.OrderBy(s => s.Time))
            {
                if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Untargetable)
                    lossAt ??= state.Time;
                else if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Targetable && lossAt is { } loss)
                {
                    if (state.Time - loss >= ExternalTimelineHints.MinPublishedLoss)
                        ++count;
                    lossAt = null;
                }
            }
        }
        return count;
    }

    // Spec 3.4: nothing validated, or a lower-ranked source that covers more of the zone, means no automatic file for it.
    public static (bool Write, string Reason) Decide(int keptPublishable, ExternalPlannerTimeline.TimelineDefinition? lowerRanked)
    {
        if (keptPublishable == 0)
            return (false, "no validated window");
        if (lowerRanked != null && CountPublishable(lowerRanked) is var lower && lower > keptPublishable)
            return (false, $"{lowerRanked.Source} has {lower} windows > {keptPublishable}");
        return (true, $"written with {keptPublishable} windows");
    }

    public static string Publish(string timelinesDir, ushort zone, Result result, ExternalPlannerTimeline.TimelineDefinition? lowerRanked)
    {
        var directory = AutoDirectory(timelinesDir);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{zone}-auto.json");
        var (write, reason) = result.Timeline == null ? (false, "no pulls") : Decide(result.KeptPublishable, lowerRanked);
        if (write)
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new { Timelines = new[] { result.Timeline } }));
            File.Move(temp, path, overwrite: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
        var line = $"zone={zone} {(write ? "written" : "not written")}: {reason}";
        UpdateReport(Path.Combine(directory, "report.txt"), zone, [line, .. result.Report.Skip(1)]);
        return line;
    }

    // report.txt holds one block per zone, each starting with "zone=<id> "; a rebuild replaces its zone's block and keeps the rest.
    private static void UpdateReport(string path, ushort zone, IReadOnlyList<string> block)
    {
        var blocks = new SortedDictionary<ushort, List<string>>();
        if (File.Exists(path))
        {
            List<string>? current = null;
            foreach (var line in File.ReadAllLines(path))
            {
                if (line.StartsWith("zone=", StringComparison.Ordinal) && ushort.TryParse(line.AsSpan(5, line.IndexOf(' ') is var sp and > 5 ? sp - 5 : line.Length - 5), out var z))
                    blocks[z] = current = [];
                current?.Add(line);
            }
        }
        blocks[zone] = [.. block];
        var temp = path + ".tmp";
        File.WriteAllLines(temp, blocks.Values.SelectMany(b => b));
        File.Move(temp, path, overwrite: true);
    }
```

ファイル先頭に `using System.IO;` と `using System.Text.Json;` を足す。

- [ ] **Step 5: 合格を確認する**

Run: プラグイン → 外部回帰ツールをビルドして実行。
Expected: `tests=329 passed=329 failed=0`。他の 2 ツールも合格。

- [ ] **Step 6: コミット**

```bash
git add BossMod/Timeline/External/TimelineStore.cs BossMod/Timeline/External/AutoTimelineGate.cs tools/external_timeline_regression/Program.cs
git commit -m "feat(timeline): load automatic timelines below hand-placed ones and write them only when they cover the zone

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: 裏スレッドの作業キュー

**Files:**
- Create: `BossMod/Timeline/External/AutoTimelineExtractor.cs`
- Modify: `BossMod/Timeline/External/ExternalPlannerTimeline.cs`(`Apply` の先頭、`SuppressApply` を追加)
- Test: `tools/external_timeline_regression/Program.cs`

**Interfaces:**
- Consumes: `ReplaySummaryCache`(Task 2)、`AutoTimelineGate.Evaluate` / `Publish`(Task 3, 4)、`TimelineStore.CandidatesForZone`、`TimelineStore.ReloadInBackground`
- Produces:
  - `[ThreadStatic] public static bool ExternalPlannerTimeline.SuppressApply`
  - `public sealed class AutoTimelineExtractor : IDisposable` with
    - `public delegate IReadOnlyList<PullSummary>? Summarizer(string replayPath, CancellationToken cancel)`(null = 解析失敗)
    - `public AutoTimelineExtractor(Func<string?> timelinesDir, Func<string?> replayDir, Summarizer? summarizer = null, bool reloadStore = true)`
    - `public static AutoTimelineExtractor? Instance { get; set; }`
    - `public void EnqueueReplay(string replayPath)`、`public void EnqueueAll()`
    - `public bool Idle { get; }`、`public string Status { get; }`
    - `public static IReadOnlyList<PullSummary>? SummarizeReplay(string replayPath, CancellationToken cancel)`(既定の要約器)
    - `public void Dispose()`

- [ ] **Step 1: 失敗するテストを書く**

```csharp
// Waits until the extractor has drained its queue (the worker thread runs asynchronously).
void WaitIdle(AutoTimelineExtractor x)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (!x.Idle && sw.Elapsed < TimeSpan.FromSeconds(20))
        Thread.Sleep(20);
    Require(x.Idle, "extractor did not go idle");
}
Check("extractor summarizes once and publishes the zone", () =>
{
    var root = Path.Combine(fixtureRoot, "worker1");
    var replays = Path.Combine(root, "replays");
    Directory.CreateDirectory(replays);
    var files = new[] { 30f, 31f, 30.5f }.Select((s, i) => { var f = Path.Combine(replays, $"Duty_{i}.log"); File.WriteAllText(f, $"r{i}"); return (f, s); }).ToList();
    var calls = 0;
    using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => replays, (path, _) =>
    {
        Interlocked.Increment(ref calls);
        var s = files.Single(f => f.f == path).s;
        return [GatePull(9500, 0x10, 120f, (s, s + 15f)) with { Start = DateTime.UnixEpoch.AddMinutes(files.FindIndex(f => f.f == path)) }];
    }, reloadStore: false);
    foreach (var (f, _) in files)
        x.EnqueueReplay(f);
    WaitIdle(x);
    Require(File.Exists(Path.Combine(root, "timelines", "auto", "9500-auto.json")), "zone not published");
    Require(calls == 3, $"summarized {calls} times");
    x.EnqueueReplay(files[0].f);
    WaitIdle(x);
    Require(calls == 3, "unchanged replay summarized again");
    x.EnqueueAll();
    WaitIdle(x);
    Require(calls == 3, "EnqueueAll re-summarized fresh replays");
});
Check("missing replay is skipped", () =>
{
    var root = Path.Combine(fixtureRoot, "worker2");
    var calls = 0;
    using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (_, _) => { ++calls; return []; }, reloadStore: false);
    x.EnqueueReplay(Path.Combine(root, "gone.log"));
    WaitIdle(x);
    Require(calls == 0 && !File.Exists(ReplaySummaryCache.CachePath(Path.Combine(root, "timelines"), "gone.log")), "missing replay processed");
});
Check("dispose during a slow summarize", () =>
{
    var root = Path.Combine(fixtureRoot, "worker3");
    Directory.CreateDirectory(root);
    var file = Path.Combine(root, "Slow.log");
    File.WriteAllText(file, "x");
    var started = new ManualResetEventSlim();
    var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (_, cancel) =>
    {
        started.Set();
        cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
        cancel.ThrowIfCancellationRequested();
        return [];
    }, reloadStore: false);
    x.EnqueueReplay(file);
    Require(started.Wait(TimeSpan.FromSeconds(10)), "summarize never started");
    var sw = System.Diagnostics.Stopwatch.StartNew();
    x.Dispose();
    Require(sw.Elapsed < TimeSpan.FromSeconds(5), $"dispose took {sw.Elapsed}");
    Require(!File.Exists(ReplaySummaryCache.CachePath(Path.Combine(root, "timelines"), file)), "cancelled summary was written");
});
Check("directory is read per job", () =>
{
    var root = Path.Combine(fixtureRoot, "worker4");
    Directory.CreateDirectory(root);
    var file = Path.Combine(root, "D.log");
    File.WriteAllText(file, "x");
    var target = Path.Combine(root, "first");
    using var x = new AutoTimelineExtractor(() => target, () => root, (_, _) => [], reloadStore: false);
    target = Path.Combine(root, "second");
    x.EnqueueReplay(file);
    WaitIdle(x);
    Require(File.Exists(ReplaySummaryCache.CachePath(Path.Combine(root, "second"), file)), "old directory used");
});
Check("failed summaries are remembered", () =>
{
    var root = Path.Combine(fixtureRoot, "worker5");
    Directory.CreateDirectory(root);
    var file = Path.Combine(root, "Bad.log");
    File.WriteAllText(file, "x");
    var calls = 0;
    using var x = new AutoTimelineExtractor(() => Path.Combine(root, "timelines"), () => root, (_, _) => { ++calls; return null; }, reloadStore: false);
    x.EnqueueReplay(file);
    WaitIdle(x);
    x.EnqueueReplay(file);
    WaitIdle(x);
    Require(calls == 1 && ReplaySummaryCache.Read(ReplaySummaryCache.CachePath(Path.Combine(root, "timelines"), file))?.Failed == true, "failure not remembered");
});
```

- [ ] **Step 2: 失敗を確認する**

Run: `dotnet build tools/external_timeline_regression -c Debug -v q`
Expected: コンパイルエラー `AutoTimelineExtractor` が見つからない。

- [ ] **Step 3: `SuppressApply` を足す**

`ExternalPlannerTimeline.cs` の `Apply` の直前に足し、先頭の判定を変える。

```csharp
    // Set on the thread that parses replays for automatic extraction: a timeline replacing a module's state machine during that
    // parse would feed the extraction's own output back into the pulls it learns from.
    [ThreadStatic] public static bool SuppressApply;

    public static StateMachine Apply(BossModule module, StateMachine original)
    {
        if (!Enabled || SuppressApply)
            return original;
```

- [ ] **Step 4: `AutoTimelineExtractor.cs` を作る**

```csharp
using System.IO;
using System.Threading;

namespace BossMod;

// Builds automatic timelines in the background: each finished recording is summarized once (ReplaySummaryCache), then every zone
// it touched is rebuilt from all summaries of that zone, passed through AutoTimelineGate and published under <timelines>\auto.
// One low-priority thread works through a de-duplicated FIFO queue, so a burst of requests costs one pass per replay and zone.
public sealed class AutoTimelineExtractor : IDisposable
{
    public delegate IReadOnlyList<PullSummary>? Summarizer(string replayPath, CancellationToken cancel);

    public static AutoTimelineExtractor? Instance { get; set; }

    private enum JobKind { Summarize, Rebuild }
    private readonly record struct Job(JobKind Kind, string Path, ushort Zone);

    private readonly Func<string?> _timelinesDir;
    private readonly Func<string?> _replayDir;
    private readonly Summarizer _summarize;
    private readonly bool _reloadStore;
    private readonly object _lock = new();
    private readonly Queue<Job> _queue = new();
    private readonly HashSet<Job> _pending = [];
    private readonly CancellationTokenSource _cancel = new();
    private readonly Thread _thread;
    private bool _busy;
    private string _status = "idle";
    private string _last = "";

    public AutoTimelineExtractor(Func<string?> timelinesDir, Func<string?> replayDir, Summarizer? summarizer = null, bool reloadStore = true)
    {
        _timelinesDir = timelinesDir;
        _replayDir = replayDir;
        _summarize = summarizer ?? SummarizeReplay;
        _reloadStore = reloadStore;
        _thread = new(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "BMR auto timelines" };
        _thread.Start();
    }

    public bool Idle
    {
        get
        {
            lock (_lock)
                return _queue.Count == 0 && !_busy;
        }
    }

    public string Status
    {
        get
        {
            lock (_lock)
                return _last.Length == 0 ? _status : $"{_status} | last: {_last}";
        }
    }

    public void EnqueueReplay(string replayPath) => Enqueue(new(JobKind.Summarize, replayPath, 0));

    // Every replay in the folder, oldest first; fresh ones only cost a cache read.
    public void EnqueueAll()
    {
        var dir = _replayDir();
        if (dir == null || !Directory.Exists(dir))
            return;
        foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*.log").OrderBy(f => f.LastWriteTimeUtc))
            EnqueueReplay(file.FullName);
    }

    public void Dispose()
    {
        _cancel.Cancel();
        lock (_lock)
            Monitor.PulseAll(_lock);
        _thread.Join(TimeSpan.FromSeconds(10));
        _cancel.Dispose();
    }

    public static IReadOnlyList<PullSummary>? SummarizeReplay(string replayPath, CancellationToken cancel)
    {
        var suppressed = ExternalPlannerTimeline.SuppressApply;
        ExternalPlannerTimeline.SuppressApply = true;
        try
        {
            var progress = 0f;
            var replay = ReplayParserLog.Parse(replayPath, ref progress, cancel);
            cancel.ThrowIfCancellationRequested();
            return replay.Ops.Count == 0 ? null : ReplayTimelineExtractor.FindPulls(replay).Select(p => PullSummary.Summarize(p)).ToList();
        }
        finally
        {
            ExternalPlannerTimeline.SuppressApply = suppressed;
        }
    }

    private void Enqueue(Job job)
    {
        lock (_lock)
        {
            if (!_pending.Add(job))
                return;
            _queue.Enqueue(job);
            Monitor.PulseAll(_lock);
        }
    }

    private void Run()
    {
        while (!_cancel.IsCancellationRequested)
        {
            Job job;
            lock (_lock)
            {
                while (_queue.Count == 0 && !_cancel.IsCancellationRequested)
                {
                    _busy = false;
                    _status = "idle";
                    Monitor.Wait(_lock);
                }
                if (_cancel.IsCancellationRequested)
                    return;
                job = _queue.Dequeue();
                _pending.Remove(job);
                _busy = true;
                _status = job.Kind == JobKind.Summarize ? $"summarizing {Path.GetFileName(job.Path)} ({_queue.Count} queued)" : $"rebuilding zone {job.Zone} ({_queue.Count} queued)";
            }
            try
            {
                if (job.Kind == JobKind.Summarize)
                    Summarize(job.Path);
                else
                    Rebuild(job.Zone);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Service.Log($"[AutoTimelines] {job.Kind} {job.Path}{job.Zone} failed: {ex.Message}");
            }
        }
    }

    private void Summarize(string replayPath)
    {
        var timelines = _timelinesDir();
        var replay = new FileInfo(replayPath);
        if (timelines == null || !replay.Exists)
            return; // deleted before its turn (MaxReplays): nothing to remember, a new file of that name is handled normally
        var cachePath = ReplaySummaryCache.CachePath(timelines, replayPath);
        var file = ReplaySummaryCache.ReadFresh(cachePath, replay);
        if (file == null)
        {
            var pulls = _summarize(replayPath, _cancel.Token);
            _cancel.Token.ThrowIfCancellationRequested();
            replay.Refresh();
            file = ReplaySummaryCache.ForReplay(replay, pulls ?? [], pulls == null);
            ReplaySummaryCache.Write(cachePath, file);
            _zonesByCache.Remove(cachePath);
        }
        foreach (var zone in file.Pulls.Select(p => p.Zone).Where(z => z != 0).Distinct())
            Enqueue(new(JobKind.Rebuild, "", zone));
    }

    // Which zones each cache file holds, so a rebuild only reads the summaries of its zone. Filled on the first rebuild, kept
    // current as summaries are written; a file changed behind our back is re-read when its write time moves.
    private readonly Dictionary<string, (long WriteTicks, ushort[] Zones)> _zonesByCache = [];

    private void Rebuild(ushort zone)
    {
        var timelines = _timelinesDir();
        if (timelines == null)
            return;
        var cacheDir = ReplaySummaryCache.CacheDirectory(timelines);
        List<PullSummary> pulls = [];
        if (Directory.Exists(cacheDir))
        {
            foreach (var path in Directory.EnumerateFiles(cacheDir, "*.json"))
            {
                _cancel.Token.ThrowIfCancellationRequested();
                var ticks = File.GetLastWriteTimeUtc(path).Ticks;
                if (_zonesByCache.TryGetValue(path, out var known) && known.WriteTicks == ticks && !known.Zones.Contains(zone))
                    continue;
                if (ReplaySummaryCache.Read(path) is not { Failed: false } file)
                    continue;
                _zonesByCache[path] = (ticks, file.Pulls.Select(p => p.Zone).Distinct().ToArray());
                pulls.AddRange(file.Pulls.Where(p => p.Zone == zone));
            }
        }
        var result = AutoTimelineGate.Evaluate(zone, pulls);
        var lower = TimelineStore.CandidatesForZone(zone).FirstOrDefault(t => t.Source is ExternalPlannerTimeline.TimelineSource.FFLogs
            or ExternalPlannerTimeline.TimelineSource.Cactbot or ExternalPlannerTimeline.TimelineSource.EventTrigger);
        var line = AutoTimelineGate.Publish(timelines, zone, result, lower);
        lock (_lock)
            _last = $"{DateTime.Now:HH:mm} {line}";
        if (_reloadStore)
            TimelineStore.ReloadInBackground();
    }
}
```

注:
- `_zonesByCache` は作業スレッドだけが触る(`Summarize` と `Rebuild` は同じスレッドで走る)。
- `Summarize` で取消が来たら、要約ファイルを書く前に `ThrowIfCancellationRequested` で抜ける(書きかけを残さない)。
- テストの要約器は `CancellationToken` を見て `OperationCanceledException` を投げる。`Run` はそれを受けて終わる。

- [ ] **Step 5: 合格を確認する**

Run: プラグイン → 外部回帰ツールをビルドして実行。
Expected: `tests=334 passed=334 failed=0`。他の 2 ツールも合格。回帰ツールの実行が 30 秒以上長くならないこと(待ちは各チェック数百 ms)。

- [ ] **Step 6: コミット**

```bash
git add BossMod/Timeline/External/AutoTimelineExtractor.cs BossMod/Timeline/External/ExternalPlannerTimeline.cs tools/external_timeline_regression/Program.cs
git commit -m "feat(timeline): rebuild automatic timelines on a background queue from cached replay summaries

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: プラグインへの組み込み(録画完了・設定・ボタン)

**Files:**
- Modify: `BossMod/Replay/ReplayManagementWindow.cs`(`StopRecording` 355-388、`UpdateLogDirectory` 390-395 の近く)
- Modify: `BossMod/BossModule/BossModuleConfig.cs`(`DrawCustom` 12-20、`TimelineUserDirectory` 44 の後ろ)
- Modify: `BossMod/Framework/Plugin.cs`(`InitOnFrameworkThread` 136-160、`DisposeAsync` 170-205)

**Interfaces:**
- Consumes: `AutoTimelineExtractor`(Task 5)、`TimelineStore.UserDirectory`
- Produces:
  - `public event Action<string>? ReplayManagementWindow.RecordingFinished`、`public string ReplayManagementWindow.LogDirectory`
  - `public bool BossModuleConfig.AutoExtractTimelines = true`

- [ ] **Step 1: 録画完了イベントを足す**

`ReplayManagementWindow.cs` に足す。

```csharp
    // Raised on the draw thread after a recording's file has been closed, with its path (automatic timeline extraction listens).
    public event Action<string>? RecordingFinished;

    public string LogDirectory => _logDir.FullName;
```

`StopRecording` の `_recorder?.Dispose(); _recorder = null;` を次に置き換える。

```csharp
        var finishedPath = _recorder?.LogPath;
        _recorder?.Dispose();
        _recorder = null;
        UpdateTitle();
        if (finishedPath != null)
        {
            try
            {
                RecordingFinished?.Invoke(finishedPath);
            }
            catch (Exception ex)
            {
                Service.Log($"RecordingFinished handler failed: {ex}");
            }
        }
```

(元の `UpdateTitle();` はこの中に移したので、後ろの重複は消す。)`ReplayRecorder.LogPath` の型が `string` でなければ(`FileInfo` など)、`.FullName` で文字列にする。

- [ ] **Step 2: 設定とボタンを足す**

`BossModuleConfig.cs` の `TimelineUserDirectory` の後ろに足す。

```csharp
    [PropertyDisplay("Build fight timelines from your replays automatically", tooltip: "After each recorded duty, summarizes the replay in the background and rebuilds that content's timeline from all your replays of it. Only windows that most of your pulls reproduce are used; hand-placed timeline files always win.", depends: nameof(UseExternalTimelineHints))]
    public bool AutoExtractTimelines = true;
```

`DrawCustom` の「Reload fight timelines」の後ろに足す。

```csharp
        if (AutoTimelineExtractor.Instance is { } auto)
        {
            if (ImGui.Button("Rebuild automatic timelines from all replays"))
                auto.EnqueueAll();
            ImGui.TextUnformatted($"Automatic timelines: {auto.Status}");
        }
```

- [ ] **Step 3: `Plugin.cs` で作って購読し、破棄する**

`_wndReplay = new ReplayManagementWindow(...)` の行の後ろに足す(フィールド `private AutoTimelineExtractor? _autoTimelines;` もクラスに足す)。

```csharp
        _autoTimelines = new(() => TimelineStore.UserDirectory, () => _wndReplay.LogDirectory);
        AutoTimelineExtractor.Instance = _autoTimelines;
        _wndReplay.RecordingFinished += path =>
        {
            var cfg = Service.Config.Get<BossModuleConfig>();
            if (cfg.UseExternalTimelineHints && cfg.AutoExtractTimelines)
                _autoTimelines?.EnqueueReplay(path);
        };
```

`DisposeAsync` の `_wndReplay.Dispose();` の直前に足す。

```csharp
        AutoTimelineExtractor.Instance = null;
        _autoTimelines?.Dispose();
```

- [ ] **Step 4: ビルドと回帰ツール**

Run: プラグインを x64 でビルド(警告 0・エラー 0)。3 つの回帰ツールをビルドして実行。
Expected: 全部合格(数は Task 5 と同じ)。

- [ ] **Step 5: コミット**

```bash
git add BossMod/Replay/ReplayManagementWindow.cs BossMod/BossModule/BossModuleConfig.cs BossMod/Framework/Plugin.cs
git commit -m "feat(timeline): extract automatic timelines when a recording finishes, with a rebuild button

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: CLI の要約化と `--auto`、実データでの検証

**Files:**
- Modify: `tools/replay_timeline_extract/Program.cs`
- Create: `docs/superpowers/plans/2026-09-29-auto-timeline-extraction-results.md`

**Interfaces:**
- Consumes: `PullSummary.Summarize`、`ReplayTimelineExtractor.Extract(IEnumerable<PullSummary>)`、`AutoTimelineGate.Evaluate` / `Publish`、`TimelineStore`

- [ ] **Step 1: CLI を 1 本ずつ要約する形にし、`--auto` を足す**

`List<Replay> replays` を持たずに、1 本解析するごとに要約して手放す。`--auto <timelinesDir>` を渡したときは、zone ごとにゲートを通して `<timelinesDir>\auto\` に出力し(`Publish`)、結果の行とレポートを表示する。最下位ソースの比較に使う `TimelineStore` は埋め込み + `<timelinesDir>` 直下を読む(`UserDirectory = timelinesDir` にして `Reload`)。最後に `Process.GetCurrentProcess().PeakWorkingSet64` を MB で表示する。

```csharp
List<PullSummary> summaries = [];
var replayCount = 0;
foreach (var file in files)
{
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var progress = 0f;
        var replay = ReplayParserLog.Parse(file, ref progress, CancellationToken.None);
        if (replay.Ops.Count == 0)
            continue;
        ++replayCount;
        var pulls = ReplayTimelineExtractor.FindPulls(replay);
        summaries.AddRange(pulls.Select(p => PullSummary.Summarize(p)));
        Console.WriteLine($"{Path.GetFileName(file)}: ops={replay.Ops.Count} encounters={replay.Encounters.Count} pulls={pulls.Count} summarize_ms={sw.ElapsedMilliseconds}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{file}: {ex.Message}");
    }
}

if (autoDir != null)
{
    TimelineStore.UserDirectory = autoDir;
    TimelineStore.Reload();
    foreach (var zone in summaries.Select(p => p.Zone).Where(z => z != 0).Distinct().Order())
    {
        if (onlyZone != null && zone != onlyZone)
            continue;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = AutoTimelineGate.Evaluate(zone, summaries);
        var lower = TimelineStore.CandidatesForZone(zone).FirstOrDefault(t => t.Source is ExternalPlannerTimeline.TimelineSource.FFLogs
            or ExternalPlannerTimeline.TimelineSource.Cactbot or ExternalPlannerTimeline.TimelineSource.EventTrigger);
        Console.WriteLine($"{AutoTimelineGate.Publish(autoDir, zone, result, lower)} rebuild_ms={sw.ElapsedMilliseconds}");
        foreach (var line in result.Report.Skip(1))
            Console.WriteLine(line);
    }
}
else
{
    var extracted = ReplayTimelineExtractor.Extract(summaries);
    // (既存の zone ごとの書き出し・表示・往復チェックをそのまま、extracted を使って残す)
}
Console.WriteLine($"replays={replayCount} peak_mb={System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 / (1024 * 1024)}");
```

引数の解析に `else if (args[i] == "--auto") autoDir = args[++i];` を足し、usage の文字列にも `[--auto <timelinesDir>]` を足す。`--auto` のときは `--out` を使わない。出力の最終行の書式は `replays=N zones=M` から変わるので、Step 2 の比較では最終行を除く。

- [ ] **Step 2: 同等性を確かめる**

Task 1 Step 1 のコマンドを出力先 `ae_cli_*` で実行し、`ae_base_*` と比べる(json は完全一致、txt は最終行と `summarize_ms` を除いて一致)。

```bash
for d in al old cly; do diff -r "$TEMP/ae_base_$d" "$TEMP/ae_cli_$d" && echo "$d SAME"; done
```

Expected: 3 つとも SAME。

- [ ] **Step 3: 品質ゲートを実データで確かめる**

```bash
X=tools/replay_timeline_extract/bin/Debug/net10.0-windows10.0.26100.0/ReplayTimelineExtract.dll
R="$APPDATA/XIVLauncher/pluginConfigs/BossModReborn/replays"; T="$(cygpath -w "$TEMP")"
rm -rf "$TEMP/ae_auto"; mkdir -p "$TEMP/ae_auto"
args=(); for f in "$R"/ThePalaceOfTheDead*.log; do args+=("$(cygpath -w "$f")"); done
dotnet $X --auto "$T\\ae_auto" "${args[@]}" > "$TEMP/ae_auto_potd.txt"
args=(); for f in "$R"/SanDoria*.log "$R"/Windurst*.log "$R"/Jeuno*.log; do args+=("$(cygpath -w "$f")"); done
dotnet $X --auto "$T\\ae_auto" "${args[@]}" > "$TEMP/ae_auto_alliance.txt"
args=(); for f in "$R"/TheClyteum*.log; do args+=("$(cygpath -w "$f")"); done
dotnet $X --auto "$T\\ae_auto" "${args[@]}" > "$TEMP/ae_auto_cly.txt"
grep -h "^zone=" "$TEMP"/ae_auto_*.txt
tail -1 "$TEMP/ae_auto_cly.txt"
```

Expected:
- 死者の宮殿(zone 561〜604)は、どの zone も `not written`、または書かれても残存窓が少ない。
- 1304/1368/1248 と 1345 は `written`。
- Clyteum 94 本のピークメモリ(`peak_mb`)と `summarize_ms` の合計、`rebuild_ms` を記録する。

続けて、書かれた自動ファイルをハーネスで測る。手動ファイルの無いフォルダ(`ae_auto`)を `--timelines` に渡す(自動ファイルは `auto\` から読まれる)。

```bash
H=tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll
dotnet $H --timelines "$T\\ae_auto" --sync all --csv "$T\\ae_auto_al.csv" "$T\\tl_alliance" | tail -1
for g in ThePalaceOfTheDeadFloors110_ ThePalaceOfTheDeadFloors2130_ ThePalaceOfTheDeadFloors111120_; do
  args=(); for f in "$R"/*$g*.log; do args+=("$(cygpath -w "$f")"); done
  echo "$g $(dotnet $H --timelines "$T\\ae_auto" --sync all "${args[@]}" 2>/dev/null | tail -1)"
done
```

Expected:
- アライアンスは、手動ファイル条件(`hit=72/84 false_alarms=0`、`%TEMP%\edge_new4_B.txt`)と同等以上の hit で、false alarm は 0。
- 死者の宮殿 3 セットは false alarm が 0(前回、ゲートなしの自データ内では 40・62・9 だった。`%TEMP%\eval_groups.txt`)。

期待と違ったら、`auto\report.txt` の窓ごとの evaluated/hits を見て原因を記録する。ゲートの定数は変えずに、止めて報告する。

- [ ] **Step 4: ハーネス 7 条件の一致を確かめる**

Task 1 で要約経由になったハーネスの holdout と、`AutoReplay` を足した順位で、統合タイムラインの 7 条件の CSV が変わらないこと。基準は `%TEMP%\edge_new4_<X>.csv`(A〜G、コマンドは `docs/superpowers/plans/2026-09-26-unified-timeline-results.md` の「殴れる敵の有無」節と、`%TEMP%\edge_base_*.txt` の集計行の `timelines=` 欄を参照)。

Expected: 7 つとも CSV 一致。ユーザーフォルダに `auto\` がまだ無いので、B・G も変わらない。

- [ ] **Step 5: 結果を記録する**

`docs/superpowers/plans/2026-09-29-auto-timeline-extraction-results.md` に、Step 2〜4 の結果を書く(コマンド、集計行、zone ごとの written/not written と理由、ピークメモリ・時間)。

- [ ] **Step 6: コミット**

```bash
git add tools/replay_timeline_extract/Program.cs docs/superpowers/plans/2026-09-29-auto-timeline-extraction-results.md
git commit -m "feat(tools): summarize replays one at a time and run the automatic-timeline gate offline

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## 統合と配置(コントローラが行う)

- nin-prekassatsu3rd-stability へ早送り(進んでいればマージしてビルドと回帰ツールを確認してから)。
- 配置: 前回配置版 `cbdc5fec8` から配置用ブランチを切って `claude/auto-extract` をマージし、Release x64 でビルド。`C:\ff14_bossmod-test` の dll/json/pdb を退避してから入れる。`DefaultRotationPresets.json` / `RebornPresets.json` は触らない。
- ゲーム内で「Rebuild automatic timelines from all replays」を押し、`<timelines>\auto\report.txt` を確認する(仕様 5.5)。
- memory と results 文書を更新する。
