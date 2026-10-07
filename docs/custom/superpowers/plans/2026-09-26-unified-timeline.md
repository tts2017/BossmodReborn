# 統合コンテンツタイムライン 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** event-trigger / cactbot / 自リプレイ由来のコンテンツタイムラインを 1 つの形式に統合し、ローテーション予告(`ExternalTimelineHints`)が「殴れる敵なし」窓を高精度に配信できるようにする。

**Architecture:** 既存の埋め込み manifest 形式を後方互換で拡張(`AbilityUsed` 同期点、`Windows`、`Source`/`Confidence`)。新設 `TimelineStore` が埋め込み + ユーザーディレクトリを読み、zone ごとに最優先 1 本を返す。`ReplayTimelineExtractor` がリプレイから同形式を生成。`ExternalTimelineHints` を CastEvent 整合・NoTarget 配信に改修し、リプレイ再生の精度ハーネスで採否を判定する。

**Tech Stack:** C# / .NET 10、System.Text.Json、Dalamud dev ライブラリ(`%APPDATA%\XIVLauncher\addon\Hooks\dev`)、Lumina(sqpack `C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack`)、既存の Check/Require 方式コンソールテスト。

**Spec:** `docs/superpowers/specs/2026-09-26-unified-timeline-design.md`

## Global Constraints

- 作業場所はこの worktree(`F:\bossmodreborn\.claude\worktrees\kind-bassi-424582`、ブランチ `claude/content-timeline-review-d2946e`)。本体 `F:\bossmodreborn` への直接書込みはフックで禁止。
- `git push`、PR 作成は禁止。ローカルコミットのみ。`git add -A` 禁止(パス指定で add)。
- 本体ビルド: `dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64`(出力 `BossMod/bin/x64/Debug/BossModReborn.dll`。ツール群はこのパスを参照する)。
- 既存 JSON フィールド名・順序は変更しない(`ZoneID, SourceFile, AutomaticFallback, Sequences[Index, StartTime, States[Time, Name, Kind, IDs, Hint], PredictionEndTime]`)。追加フィールドは省略可で、省略時の既定値は旧挙動と同じ。
- 定数不変: Horizon 25 秒、MinPublishedLoss 8.5 秒、SyncLifetime 60 秒、namespace `bossmod.internal.timeline`。
- `tools/xan_timeline_harness` の出力(トレース CSV)は全ジョブで変更前後ビット同一であること。
- テストは各ツールの Check/Require 方式(`tools/external_timeline_regression/Program.cs` 参照)。xunit は使わない。
- 文書・コメント・コミットメッセージは通常の日本語または英語(既存コメントは英語)。

## Review Focus

1. **ユーザー JSON が壊れている / 旧形式**: `timelines/*.json` の 1 ファイルが不正でも他のファイルと埋め込み既定は読めること。旧形式(新フィールド無し)は EventTrigger・Confidence 1 として読めること。→ Task 3 の Check「broken user file is skipped」「legacy file loads with defaults」。
2. **同 zone に複数候補**: Replay と Cactbot が同 zone にあれば Replay が勝ち、同 Source 同士は Confidence 高い方。→ Task 3 の Check「priority order」。
3. **リプレイに Encounter が無い(未対応コンテンツ)**: pull を InCombat 由来ではなく「敵の存在・生存」から区切り、20 秒未満は捨てること。→ Task 4 の Check「pull without encounter」「short trash pull dropped」。
4. **窓の途中で HP フェーズがずれる**: 複数 pull で窓の開始が 5 秒超ばらつくと Confidence が下がり、0.5 未満は配信しないこと。→ Task 4 の Check「confidence drops with spread」、Task 7 の Check「low confidence window not published」。
5. **短窓が長窓を隠す**: 8.5 秒未満の窓の直後に長窓があるとき、長窓を Horizon 内で予告すること。→ Task 7 の Check「short window does not mask long window」。

---

### Task 1: 形式の拡張(本体側の型と planner 経路の互換)

**Files:**
- Modify: `BossMod/Timeline/External/ExternalPlannerTimeline.cs`(末尾の record 群と `ActionIDs`)
- Modify: `tools/external_timeline_regression/Program.cs`(`Service.Config.Initialize();` の直後、既存 Check 群)

**Interfaces:**
- Produces(後続タスクが使う型、すべて `ExternalPlannerTimeline` 内の nested 型):
  ```csharp
  public enum ExternalStateKind { Timeout, CastStart, Targetable, Untargetable, AddedCombatant, AbilityUsed }
  public enum TimelineSource { EventTrigger, Cactbot, Replay, User }
  public enum TimelineWindowKind { BossUntargetable, NoTarget, AddsPresent }
  public sealed record TimelineWindow(TimelineWindowKind Kind, float Start, float? End, float Confidence);
  public sealed record TimelineState(float Time, string Name, ExternalStateKind Kind, List<uint> IDs, int Hint);
  public sealed record TimelineSequence(int Index, float StartTime, List<TimelineState> States, float? PredictionEndTime, List<uint>? BossOIDs = null, List<TimelineWindow>? Windows = null);
  public sealed record TimelineDefinition(int ZoneID, string SourceFile, bool AutomaticFallback, List<TimelineSequence> Sequences, TimelineSource Source = TimelineSource.EventTrigger, float Confidence = 1f);
  ```

- [ ] **Step 1: regression ツールをフラグ ON で動くようにする(前提修正)**

`tools/external_timeline_regression/Program.cs` の `Service.Config.Initialize();` の直後に 1 行追加:

```csharp
Service.Config.Initialize();
Service.Config.Get<BossModuleConfig>().UseExternalPlannerTimelines = true; // the tool exercises the planner fallback, which is off by default
```

- [ ] **Step 2: 失敗する Check を追加(旧 JSON が新型で読め、新フィールドが往復する)**

`tools/external_timeline_regression/Program.cs` の `Check("loop boundary is retained without a fake label event", ...)` の直後に追加:

```csharp
Check("legacy manifest json loads with default source and confidence", () =>
{
    var json = """{"ZoneID":1,"SourceFile":"x.txt","AutomaticFallback":true,"Sequences":[{"Index":0,"StartTime":0,"States":[{"Time":1,"Name":"a","Kind":0,"IDs":[5],"Hint":0}],"PredictionEndTime":null}]}""";
    var def = JsonSerializer.Deserialize<ExternalPlannerTimeline.TimelineDefinition>(json)!;
    Require(def.Source == ExternalPlannerTimeline.TimelineSource.EventTrigger, "default source");
    Require(def.Confidence == 1f, "default confidence");
    Require(def.Sequences[0].BossOIDs == null && def.Sequences[0].Windows == null, "optional lists default to null");
});
Check("new fields round-trip through json", () =>
{
    var def = new ExternalPlannerTimeline.TimelineDefinition(2, "r.json", false,
        [new(0, 0, [new(3, "hit", ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, [7], 0)], null, [0x1234], [new(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 10, 25, 0.75f)])],
        ExternalPlannerTimeline.TimelineSource.Replay, 0.9f);
    var back = JsonSerializer.Deserialize<ExternalPlannerTimeline.TimelineDefinition>(JsonSerializer.Serialize(def))!;
    Require(back.Source == ExternalPlannerTimeline.TimelineSource.Replay && back.Confidence == 0.9f, "source/confidence lost");
    Require(back.Sequences[0].BossOIDs![0] == 0x1234, "boss oid lost");
    Require(back.Sequences[0].Windows![0] is { Kind: ExternalPlannerTimeline.TimelineWindowKind.NoTarget, Start: 10, End: 25, Confidence: 0.75f }, "window lost");
    Require(back.Sequences[0].States[0].Kind == ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, "AbilityUsed lost");
});
```

- [ ] **Step 3: ビルドして失敗を確認**

Run:
```bash
dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64 && dotnet build tools/external_timeline_regression -c Debug
```
Expected: `TimelineSource` / `TimelineWindowKind` / `AbilityUsed` が見つからずコンパイルエラー。

- [ ] **Step 4: 型を拡張する**

`ExternalPlannerTimeline.cs` の `public enum ExternalStateKind` に `AbilityUsed` を末尾追加し、末尾の record 群を次に置き換える:

```csharp
    public enum ExternalStateKind
    {
        Timeout,
        CastStart,
        Targetable,
        Untargetable,
        AddedCombatant,
        AbilityUsed // an Ability line: the effect landed, matched against Actors.CastEvent
    }

    public enum TimelineSource { EventTrigger, Cactbot, Replay, User }

    public enum TimelineWindowKind
    {
        BossUntargetable, // the boss itself cannot be targeted
        NoTarget, // nothing attackable is left: what rotations read as downtime
        AddsPresent // the boss is gone but adds can be attacked
    }

    private sealed record Manifest(List<TimelineDefinition> Timelines);
    public sealed record TimelineWindow(TimelineWindowKind Kind, float Start, float? End, float Confidence);
    public sealed record TimelineDefinition(int ZoneID, string SourceFile, bool AutomaticFallback, List<TimelineSequence> Sequences, TimelineSource Source = TimelineSource.EventTrigger, float Confidence = 1f);
    public sealed record TimelineSequence(int Index, float StartTime, List<TimelineState> States, float? PredictionEndTime, List<uint>? BossOIDs = null, List<TimelineWindow>? Windows = null);
    public sealed record TimelineState(float Time, string Name, ExternalStateKind Kind, List<uint> IDs, int Hint);
```

`ActionIDs` を AbilityUsed も含むように変更(planner 側の識別に Ability 行の ID を使う。挙動は「Timeout + IDs」だった旧データと同じ):

```csharp
    private static IEnumerable<uint> ActionIDs(TimelineSequence sequence)
        => sequence.States.Where(state => state.Kind is ExternalStateKind.CastStart or ExternalStateKind.Timeout or ExternalStateKind.AbilityUsed).SelectMany(state => state.IDs);
```

`BuildTimeline` の `anchorIndex` 計算にある `state.Kind is ExternalStateKind.CastStart or ExternalStateKind.Timeout` も同様に `or ExternalStateKind.AbilityUsed` を足す。switch の `_ => Timeout(...)` は AbilityUsed を単純タイムアウトとして扱うので変更不要。

- [ ] **Step 5: ビルドして regression を実行**

Run:
```bash
dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64 && dotnet build tools/external_timeline_regression -c Debug && dotnet tools/external_timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/ExternalTimelineRegression.dll
```
Expected: 最終行 `tests=... failed=0`、`planner_scan=273 external_fallbacks=168`(2026-09-24 計測値。モジュール追加で増えることはある)。

- [ ] **Step 6: Commit**

```bash
git add BossMod/Timeline/External/ExternalPlannerTimeline.cs tools/external_timeline_regression/Program.cs
git commit -m "feat(timeline): extend the external timeline format with sources, windows and ability sync points"
```

---

### Task 2: 生成器の拡張(cactbot を独立ソースに、AbilityUsed、Windows)

**Files:**
- Modify: `tools/encounter_timeline/EventTriggerTimelineCatalog.cs`(`Load(string, string?)`、新プロパティ)
- Modify: `tools/encounter_timeline/ExternalTimelineManifestGenerator.cs`
- Modify: `tools/encounter_timeline/Program.cs`(`--manifest` 出力)
- Modify: `tools/external_timeline_regression/Program.cs`(Check 追加)

**Interfaces:**
- Consumes: Task 1 の JSON 形式(生成器は BossMod を参照できないので mirror record を持つ。プロパティ名を一致させる)
- Produces:
  ```csharp
  // EncounterTimeline 名前空間
  public enum ExternalTimelineStateKind { Timeout, CastStart, Targetable, Untargetable, AddedCombatant, AbilityUsed }
  public enum ExternalTimelineSource { EventTrigger, Cactbot, Replay, User }
  public enum ExternalTimelineWindowKind { BossUntargetable, NoTarget, AddsPresent }
  public sealed record ExternalTimelineWindow(ExternalTimelineWindowKind Kind, float Start, float? End, float Confidence);
  public sealed record ExternalTimelineDefinition(int ZoneID, string SourceFile, bool AutomaticFallback, IReadOnlyList<ExternalTimelineSequence> Sequences, ExternalTimelineSource Source = ExternalTimelineSource.EventTrigger, float Confidence = 1f);
  public sealed record ExternalTimelineSequence(int Index, float StartTime, IReadOnlyList<ExternalTimelineState> States, float? PredictionEndTime = null, IReadOnlyList<uint>? BossOIDs = null, IReadOnlyList<ExternalTimelineWindow>? Windows = null);
  // カタログ
  public IReadOnlyDictionary<int, EventTriggerTimeline> CactbotTimelinesByZone { get; } // cactbot の全 timelineFile 対応(event-trigger と重複しても含む)
  public static ExternalTimelineManifest Generate(EventTriggerTimelineCatalog catalog, CactbotTriggerCatalog? cactbot) // EventTrigger 分 + Cactbot 分を連結、zone 134 除外
  public static IReadOnlyList<ExternalTimelineWindow> BuildWindows(EventTriggerTimeline timeline, ExternalTimelineSequence sequence) // 純関数
  ```

- [ ] **Step 1: 失敗する Check を追加**

`tools/external_timeline_regression/Program.cs` の `Fixture` 関数の直後に cactbot 用 fixture を足し、Check を追加する:

```csharp
// A cactbot layout: resources/zone_id.ts maps names to ids, ui/raidboss/data/<x>.ts names the timeline file next to it.
string CactbotFixture(string zoneName, int zoneID, string timelineContents)
{
    var root = Path.Combine(fixtureRoot, "cactbot");
    Directory.CreateDirectory(Path.Combine(root, "resources"));
    Directory.CreateDirectory(Path.Combine(root, "ui", "raidboss", "data", "t"));
    File.WriteAllText(Path.Combine(root, "resources", "zone_id.ts"), $"export default {{\n  {zoneName}: {zoneID},\n}};\n");
    File.WriteAllText(Path.Combine(root, "ui", "raidboss/data/t", "t.ts"), $"const triggerSet = {{\n  zoneId: ZoneId.{zoneName},\n  timelineFile: 't.txt',\n  triggers: [],\n}};\n");
    File.WriteAllText(Path.Combine(root, "ui", "raidboss", "data", "t", "t.txt"), timelineContents);
    return root;
}
```

Check 群(`Check("loop boundary ...")` の後):

```csharp
Check("ability lines become AbilityUsed sync points", () =>
{
    var def = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"Hit\" Ability { id: \"1234\", source: \"Boss\" }\n9.0 \"Cast\" StartsUsing { id: \"1235\", source: \"Boss\" }\n"), null).Timelines.Single();
    var states = def.Sequences[0].States;
    Require(states[0].Kind == ExternalTimelineStateKind.AbilityUsed && states[0].IDs.SequenceEqual([0x1234u]), "ability line kind");
    Require(states[1].Kind == ExternalTimelineStateKind.CastStart, "cast line kind");
    Require(def.Source == ExternalTimelineSource.EventTrigger && def.Confidence == 1f, "event trigger source");
});
Check("boss-only downtime becomes a NoTarget window", () =>
{
    var def = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n30.0 \"--targetable--\"\n35.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n"), null).Timelines.Single();
    var windows = def.Sequences[0].Windows!;
    Require(windows.Any(w => w is { Kind: ExternalTimelineWindowKind.BossUntargetable, Start: 10, End: 30 }), "boss window");
    Require(windows.Any(w => w is { Kind: ExternalTimelineWindowKind.NoTarget, Start: 10, End: 30, Confidence: 1f }), "no target window");
    Require(!windows.Any(w => w.Kind == ExternalTimelineWindowKind.AddsPresent), "no adds");
});
Check("downtime with other casters is AddsPresent, not NoTarget", () =>
{
    var def = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n15.0 \"Bite\" Ability { id: \"9\", source: \"Add\" }\n30.0 \"--targetable--\"\n35.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n"), null).Timelines.Single();
    var windows = def.Sequences[0].Windows!;
    Require(windows.Any(w => w is { Kind: ExternalTimelineWindowKind.AddsPresent, Start: 10, End: 30 }), "adds window");
    Require(!windows.Any(w => w.Kind == ExternalTimelineWindowKind.NoTarget), "no NoTarget window with adds");
});
Check("downtime with an adds title is AddsPresent", () =>
{
    var def = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n12.0 \"--adds targetable--\"\n30.0 \"--targetable--\"\n"), null).Timelines.Single();
    Require(def.Sequences[0].Windows!.Any(w => w.Kind == ExternalTimelineWindowKind.AddsPresent), "adds title");
});
Check("open downtime has no end", () =>
{
    var def = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n10.0 \"--untargetable--\"\n20.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n"), null).Timelines.Single();
    Require(def.Sequences[0].Windows!.Single(w => w.Kind == ExternalTimelineWindowKind.BossUntargetable).End == null, "open end");
});
Check("cactbot timelines are emitted as their own source, event trigger kept", () =>
{
    Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n");
    var cactbot = CactbotFixture("TestZone", 777, "6.0 \"B\" Ability { id: \"2\", source: \"Boss\" }\n");
    var catalog = EventTriggerTimelineCatalog.Load(fixtureRoot, cactbot);
    Require(catalog.CactbotTimelinesByZone.ContainsKey(777), "cactbot mapping");
    var defs = ExternalTimelineManifestGenerator.Generate(catalog, null).Timelines.Where(t => t.ZoneID == 777).ToArray();
    Require(defs.Length == 2, $"expected both sources, got {defs.Length}");
    Require(defs.Any(d => d.Source == ExternalTimelineSource.EventTrigger) && defs.Any(d => d.Source == ExternalTimelineSource.Cactbot), "sources");
});
Check("zone 134 test timeline is excluded", () =>
{
    File.WriteAllText(Path.Combine(fixtureRoot, "timelines.csv"), "777,\"test.txt\"\n134,\"test.txt\"\n");
    var defs = ExternalTimelineManifestGenerator.Generate(Fixture("5.0 \"A\" Ability { id: \"1\", source: \"Boss\" }\n"), null).Timelines;
    Require(defs.All(d => d.ZoneID != 134), "zone 134 present");
    File.WriteAllText(Path.Combine(fixtureRoot, "timelines.csv"), "777,\"test.txt\"\n");
});
```

- [ ] **Step 2: ビルドして失敗を確認**

Run: `dotnet build tools/external_timeline_regression -c Debug`
Expected: `AbilityUsed`、`Windows`、`CactbotTimelinesByZone`、`ExternalTimelineSource` が無くコンパイルエラー。

- [ ] **Step 3: カタログに cactbot 全対応を持たせる**

`EventTriggerTimelineCatalog.cs`: コンストラクタに `IReadOnlyDictionary<int, EventTriggerTimeline> cactbotTimelinesByZone` を追加し、プロパティ `public IReadOnlyDictionary<int, EventTriggerTimeline> CactbotTimelinesByZone { get; }` を足す。`Load(string)` は空辞書を渡す。`Load(string, string?)` を次に置き換える(fill 動作は残しつつ、全 cactbot 対応を別途保持):

```csharp
    public static EventTriggerTimelineCatalog Load(string resourcesDirectory, string? cactbotDirectory)
    {
        var catalog = Load(resourcesDirectory);
        if (cactbotDirectory == null)
            return catalog;

        var zoneIDs = ParseCactbotZoneIDs(Path.Combine(cactbotDirectory, "resources", "zone_id.ts"));
        var dataDirectory = Path.Combine(cactbotDirectory, "ui", "raidboss", "data");
        if (zoneIDs.Count == 0 || !Directory.Exists(dataDirectory))
            return catalog;

        var timelines = catalog.Timelines.ToList();
        var timelinesByZone = catalog.TimelinesByZone.ToDictionary(entry => entry.Key, entry => entry.Value);
        Dictionary<int, EventTriggerTimeline> cactbotByZone = [];
        foreach (var (zoneID, path) in ParseCactbotTimelineMappings(dataDirectory, zoneIDs))
        {
            if (cactbotByZone.ContainsKey(zoneID) || !File.Exists(path))
                continue;
            var timeline = ParseTimeline(path);
            // Several cactbot files only carry reset and sync lines for their trigger set: importing those adds an empty zone entry.
            if (timeline.Actions.Count == 0 && !timeline.Events.Any(entry => entry.Kind is EventTriggerTimelineEventKind.Targetable or EventTriggerTimelineEventKind.Untargetable or EventTriggerTimelineEventKind.AddedCombatant))
                continue;
            cactbotByZone.Add(zoneID, timeline);
            if (!timelinesByZone.ContainsKey(zoneID))
            {
                timelines.Add(timeline);
                timelinesByZone.Add(zoneID, timeline);
            }
        }

        return new(catalog.ResourcesDirectory, timelines, timelinesByZone, cactbotByZone);
    }
```

- [ ] **Step 4: 生成器を拡張する**

`ExternalTimelineManifestGenerator.cs` の型群を Interfaces のとおりに置き換え、`Generate` を次にする:

```csharp
    public static ExternalTimelineManifest Generate(EventTriggerTimelineCatalog catalog, CactbotTriggerCatalog? cactbot)
    {
        List<ExternalTimelineDefinition> result = [];
        foreach (var mapping in catalog.TimelinesByZone.OrderBy(mapping => mapping.Key))
        {
            if (mapping.Key == 134 && mapping.Value.FileName == "test.txt")
                continue; // event-trigger's smoke-test file is mapped to an overworld zone
            var isCactbotFill = catalog.CactbotTimelinesByZone.TryGetValue(mapping.Key, out var cactbotTimeline) && ReferenceEquals(cactbotTimeline, mapping.Value);
            if (isCactbotFill)
                continue; // emitted below with its own source
            result.Add(GenerateTimeline(mapping.Key, mapping.Value, cactbot?.TriggersForZone(mapping.Key) ?? [], ExternalTimelineSource.EventTrigger));
        }
        foreach (var mapping in catalog.CactbotTimelinesByZone.OrderBy(mapping => mapping.Key))
            result.Add(GenerateTimeline(mapping.Key, mapping.Value, cactbot?.TriggersForZone(mapping.Key) ?? [], ExternalTimelineSource.Cactbot));
        return new(result);
    }
```

`GenerateTimeline` に `ExternalTimelineSource source` 引数を足し、末尾を次にする(Windows を各 sequence に付ける):

```csharp
        for (var index = 0; index < sequences.Count; ++index)
        {
            var sequence = sequences[index];
            var end = index + 1 < sequences.Count ? sequences[index + 1].States[0].Time : float.PositiveInfinity;
            var firstJump = timeline.Jumps.Where(jump => jump.Time >= sequence.States[0].Time && jump.Time < end).Select(jump => (float?)jump.Time).FirstOrDefault();
            sequence = sequence with { PredictionEndTime = firstJump };
            sequences[index] = sequence with { Windows = BuildWindows(timeline, sequence) };
        }
        var automaticFallback = timeline.Jumps.All(jump => jump.Target is { } target && target > 0f && target < jump.Time);
        return new(zoneID, timeline.FileName, automaticFallback, sequences, source, 1f);
```

`BuildState` の kind 決定を変更(Ability 行は AbilityUsed):

```csharp
        var abilityIDs = entry.Actions
            .Where(action => action.Kind == EventTriggerTimelineActionKind.Ability)
            .SelectMany(action => action.ActionIDs)
            .Distinct()
            .Order()
            .ToArray();
        var kind = startsUsingIDs.Length > 0
            ? ExternalTimelineStateKind.CastStart
            : entry.Kinds.Contains(EventTriggerTimelineEventKind.Targetable)
                ? ExternalTimelineStateKind.Targetable
                : entry.Kinds.Contains(EventTriggerTimelineEventKind.Untargetable)
                    ? ExternalTimelineStateKind.Untargetable
                    : entry.Kinds.Contains(EventTriggerTimelineEventKind.AddedCombatant) && addedCombatantIDs.Length > 0
                        ? ExternalTimelineStateKind.AddedCombatant
                        : abilityIDs.Length > 0
                            ? ExternalTimelineStateKind.AbilityUsed
                            : ExternalTimelineStateKind.Timeout;
        var ids = kind switch
        {
            ExternalTimelineStateKind.CastStart => startsUsingIDs,
            ExternalTimelineStateKind.AddedCombatant => addedCombatantIDs,
            ExternalTimelineStateKind.AbilityUsed => abilityIDs,
            ExternalTimelineStateKind.Timeout => actionIDs,
            _ => []
        };
```

`BuildWindows` を追加(純関数。ボス名 = timeline.Actions で最頻の source):

```csharp
    private static readonly Regex AddsTitle = new(@"\badds?\b|\bspawn", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // BossUntargetable follows the --untargetable--/--targetable-- markers. NoTarget is only claimed when nothing else acts inside
    // the window: a cast by another source, an added combatant or an "adds" title means something attackable is there.
    public static IReadOnlyList<ExternalTimelineWindow> BuildWindows(EventTriggerTimeline timeline, ExternalTimelineSequence sequence)
    {
        var boss = timeline.Actions.SelectMany(action => action.Sources).GroupBy(source => source, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count()).Select(group => group.Key).FirstOrDefault();
        List<ExternalTimelineWindow> result = [];
        float? lossAt = null;
        foreach (var state in sequence.States)
        {
            if (state.Kind == ExternalTimelineStateKind.Untargetable && lossAt == null)
                lossAt = state.Time;
            else if (state.Kind == ExternalTimelineStateKind.Targetable && lossAt is { } start)
            {
                result.Add(new(ExternalTimelineWindowKind.BossUntargetable, start, state.Time, 1f));
                result.Add(new(SomethingAttackableInside(timeline, sequence, boss, start, state.Time) ? ExternalTimelineWindowKind.AddsPresent : ExternalTimelineWindowKind.NoTarget, start, state.Time, 1f));
                lossAt = null;
            }
        }
        if (lossAt is { } openStart)
            result.Add(new(ExternalTimelineWindowKind.BossUntargetable, openStart, null, 1f));
        return result;
    }

    private static bool SomethingAttackableInside(EventTriggerTimeline timeline, ExternalTimelineSequence sequence, string? boss, float start, float end)
        => timeline.Actions.Any(action => action.Time > start && action.Time < end && action.Sources.Any(source => !string.Equals(source, boss, StringComparison.Ordinal)))
        || timeline.Events.Any(entry => entry.Time > start && entry.Time < end && (entry.Kind == EventTriggerTimelineEventKind.AddedCombatant || AddsTitle.IsMatch(entry.Title)))
        || sequence.States.Any(state => state.Time > start && state.Time < end && (state.Kind == ExternalTimelineStateKind.AddedCombatant || AddsTitle.IsMatch(state.Name)));
```

`using System.Text.RegularExpressions;` を追加。`EventTriggerTimelineAction` の `Sources` と `Time`、`EventTriggerTimelineEvent` の `Title`/`Time`/`Kind` は既存の record struct が持つ。

- [ ] **Step 5: ビルドして regression を実行**

Run:
```bash
dotnet build tools/external_timeline_regression -c Debug && dotnet tools/external_timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/ExternalTimelineRegression.dll
```
Expected: `failed=0`。

- [ ] **Step 6: 埋め込み JSON を再生成し、件数を確認**

Run:
```bash
dotnet run --project tools/encounter_timeline -c Release -- "F:\event-trigger-master\timelines\src\main\resources" --all "%TEMP%\tl_skeletons" --manifest BossMod/Timeline/External/ExternalTimelines.json --cactbot F:\cactbot-current
```
Expected: `manifest_timelines=` が 308(event-trigger 309 − zone 134)+ cactbot の有効本数(300 前後)。stdout の数値を記録する。

- [ ] **Step 7: 本体をビルドし、planner 経路が壊れていないことを確認**

Run:
```bash
dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64 && dotnet tools/external_timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/ExternalTimelineRegression.dll
```
Expected: `failed=0`。`external_fallbacks` は Task 1 の値以上(同 zone に 2 本あると `LoadTimelines` の `ToDictionary` が重複キーで例外 → 空辞書になり 0 になる。その場合は Task 3 の `TimelineStore` で解決するので、この時点では `LoadTimelines` を `GroupBy(ZoneID).First()` に一時変更して通す)。

- [ ] **Step 8: Commit**

```bash
git add tools/encounter_timeline/EventTriggerTimelineCatalog.cs tools/encounter_timeline/ExternalTimelineManifestGenerator.cs tools/external_timeline_regression/Program.cs BossMod/Timeline/External/ExternalTimelines.json BossMod/Timeline/External/ExternalPlannerTimeline.cs
git commit -m "feat(timeline): emit cactbot timelines as a separate source with ability sync points and downtime windows"
```

---

### Task 3: TimelineStore(埋め込み + ユーザーディレクトリ、優先順)

**Files:**
- Create: `BossMod/Timeline/External/TimelineStore.cs`
- Modify: `BossMod/Timeline/External/ExternalPlannerTimeline.cs`(`_timelines`/`LoadTimelines` を削除して Store を使う)
- Modify: `BossMod/BossModule/BossModuleConfig.cs`(設定項目 + 再読込ボタン)
- Modify: `BossMod/Framework/Plugin.cs`(ユーザーディレクトリを設定)
- Modify: `tools/external_timeline_regression/Program.cs`(reflection 差し替えを `TimelineStore.SetCandidatesForTesting` に置換、Check 追加)

**Interfaces:**
- Consumes: Task 1 の型
- Produces:
  ```csharp
  public static class TimelineStore
  {
      public static string? UserDirectory { get; set; }            // null = ユーザー分を読まない
      public static void Reload();                                 // 埋め込み + UserDirectory/*.json を読み直す
      public static ExternalPlannerTimeline.TimelineDefinition? ForZone(ushort zone);
      public static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> CandidatesForZone(ushort zone); // 優先順に並ぶ
      public static IEnumerable<ExternalPlannerTimeline.TimelineDefinition> AllTimelines();   // zone ごとの最優先
      public static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> Rank(IEnumerable<ExternalPlannerTimeline.TimelineDefinition> candidates); // 純関数: Replay > User > Cactbot > EventTrigger、同順位は Confidence 降順
      public static void SetCandidatesForTesting(ushort zone, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition>? candidates); // null で元に戻す
      public static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> LoadUserFile(string path); // 壊れたファイルは空を返し、Service.Log に警告
  }
  ```

- [ ] **Step 1: 失敗する Check を追加**

`tools/external_timeline_regression/Program.cs` の `var lazy = typeof(ExternalPlannerTimeline).GetField("_timelines", ...)` から `finally { timelines[zone] = saved; }` までを次に置き換える(合成タイムラインの差し込みを公開 API で行う):

```csharp
    var zone = (ushort)Service.LuminaRow<Lumina.Excel.Sheets.ContentFinderCondition>(info.GroupID)!.Value.TerritoryType.RowId;
    try
    {
        var synthetic = new ExternalTimelineDefinition(zone, "synthetic.txt", true,
            [new ExternalTimelineSequence(0, 0, [
                new(2, "Opening raidwide", ExternalTimelineStateKind.Timeout, [0x653C], TimelineStateHint.Raidwide),
                new(5, "", ExternalTimelineStateKind.CastStart, [0x653C], TimelineStateHint.None),
                new(10, "Final raidwide", ExternalTimelineStateKind.Timeout, [0x653C], TimelineStateHint.Raidwide)])]);
        TimelineStore.SetCandidatesForTesting(zone, [JsonSerializer.Deserialize<ExternalPlannerTimeline.TimelineDefinition>(JsonSerializer.Serialize(synthetic))!]);
        // ... 既存の 4 つの Check はそのまま ...
    }
    finally { TimelineStore.SetCandidatesForTesting(zone, null); }
```

続けて Store 自体の Check を追加(`var scanCount = 0;` の直前):

```csharp
Check("priority order: replay > user > cactbot > event trigger, then confidence", () =>
{
    ExternalPlannerTimeline.TimelineDefinition Make(ExternalPlannerTimeline.TimelineSource source, float confidence) => new(5, source.ToString(), true, [], source, confidence);
    var ranked = TimelineStore.Rank([Make(ExternalPlannerTimeline.TimelineSource.EventTrigger, 1f), Make(ExternalPlannerTimeline.TimelineSource.Cactbot, 1f), Make(ExternalPlannerTimeline.TimelineSource.Replay, 0.3f), Make(ExternalPlannerTimeline.TimelineSource.Replay, 0.8f), Make(ExternalPlannerTimeline.TimelineSource.User, 1f)]);
    Require(ranked.Select(t => (t.Source, t.Confidence)).SequenceEqual([(ExternalPlannerTimeline.TimelineSource.Replay, 0.8f), (ExternalPlannerTimeline.TimelineSource.Replay, 0.3f), (ExternalPlannerTimeline.TimelineSource.User, 1f), (ExternalPlannerTimeline.TimelineSource.Cactbot, 1f), (ExternalPlannerTimeline.TimelineSource.EventTrigger, 1f)]), "rank order");
});
Check("user directory overrides embedded, broken file is skipped, legacy file loads", () =>
{
    var dir = Path.Combine(fixtureRoot, "timelines");
    Directory.CreateDirectory(dir);
    File.WriteAllText(Path.Combine(dir, "broken.json"), "{ not json");
    File.WriteAllText(Path.Combine(dir, "legacy.json"), """{"Timelines":[{"ZoneID":60001,"SourceFile":"legacy.txt","AutomaticFallback":true,"Sequences":[]}]}""");
    File.WriteAllText(Path.Combine(dir, "user.json"), """{"Timelines":[{"ZoneID":1122,"SourceFile":"mine.json","AutomaticFallback":true,"Sequences":[],"Source":3,"Confidence":1}]}""");
    var saved = TimelineStore.UserDirectory;
    try
    {
        TimelineStore.UserDirectory = dir;
        TimelineStore.Reload();
        Require(TimelineStore.ForZone(60001) is { Source: ExternalPlannerTimeline.TimelineSource.EventTrigger, Confidence: 1f }, "legacy defaults");
        Require(TimelineStore.ForZone(1122) is { Source: ExternalPlannerTimeline.TimelineSource.User }, "user override for TOP");
        Require(TimelineStore.CandidatesForZone(1122).Count >= 2, "embedded candidate kept behind user");
        Require(TimelineStore.ForZone(193) != null, "embedded zones still load next to a broken file");
    }
    finally
    {
        TimelineStore.UserDirectory = saved;
        TimelineStore.Reload();
    }
});
```

- [ ] **Step 2: ビルドして失敗を確認**

Run: `dotnet build tools/external_timeline_regression -c Debug`
Expected: `TimelineStore` が無くコンパイルエラー。

- [ ] **Step 3: TimelineStore を実装**

`BossMod/Timeline/External/TimelineStore.cs`:

```csharp
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace BossMod;

// Every imported fight timeline we know about, keyed by zone. The embedded manifest ships the event-trigger and cactbot imports;
// the user directory adds replay extractions and hand edits without a rebuild. A zone can have several candidates, and consumers
// get them ranked: what was measured on this client beats what was written for another.
public static class TimelineStore
{
    private const string ResourceName = "BossMod.ExternalTimelines.json";
    private static readonly object Lock = new();
    private static Dictionary<ushort, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition>> _byZone = [];
    private static readonly Dictionary<ushort, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition>> _testOverrides = [];
    private static bool _loaded;

    public static string? UserDirectory { get; set; }

    public static void Reload()
    {
        var candidates = new List<ExternalPlannerTimeline.TimelineDefinition>(LoadEmbedded());
        if (UserDirectory != null && Directory.Exists(UserDirectory))
            foreach (var path in Directory.EnumerateFiles(UserDirectory, "*.json").Order(StringComparer.OrdinalIgnoreCase))
                candidates.AddRange(LoadUserFile(path));
        var byZone = candidates
            .Where(t => t.ZoneID is > 0 and <= ushort.MaxValue)
            .GroupBy(t => (ushort)t.ZoneID)
            .ToDictionary(g => g.Key, g => Rank(g));
        lock (Lock)
        {
            _byZone = byZone;
            _loaded = true;
        }
    }

    public static ExternalPlannerTimeline.TimelineDefinition? ForZone(ushort zone) => CandidatesForZone(zone).FirstOrDefault();

    public static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> CandidatesForZone(ushort zone)
    {
        EnsureLoaded();
        lock (Lock)
        {
            if (_testOverrides.TryGetValue(zone, out var overridden))
                return overridden;
            return _byZone.TryGetValue(zone, out var list) ? list : [];
        }
    }

    public static IEnumerable<ExternalPlannerTimeline.TimelineDefinition> AllTimelines()
    {
        EnsureLoaded();
        lock (Lock)
            return _byZone.Keys.Select(z => CandidatesForZone(z)[0]).ToList();
    }

    public static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> Rank(IEnumerable<ExternalPlannerTimeline.TimelineDefinition> candidates)
        => candidates.OrderByDescending(t => Priority(t.Source)).ThenByDescending(t => t.Confidence).ToList();

    private static int Priority(ExternalPlannerTimeline.TimelineSource source) => source switch
    {
        ExternalPlannerTimeline.TimelineSource.Replay => 3,
        ExternalPlannerTimeline.TimelineSource.User => 2,
        ExternalPlannerTimeline.TimelineSource.Cactbot => 1,
        _ => 0
    };

    public static void SetCandidatesForTesting(ushort zone, IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition>? candidates)
    {
        lock (Lock)
        {
            if (candidates == null)
                _testOverrides.Remove(zone);
            else
                _testOverrides[zone] = candidates;
        }
    }

    public static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> LoadUserFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Manifest>(stream)?.Timelines ?? [];
        }
        catch (Exception ex)
        {
            Service.Log($"[TimelineStore] Skipping '{path}': {ex.Message}");
            return [];
        }
    }

    private static IReadOnlyList<ExternalPlannerTimeline.TimelineDefinition> LoadEmbedded()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            return stream != null ? JsonSerializer.Deserialize<Manifest>(stream)?.Timelines ?? [] : [];
        }
        catch
        {
            return [];
        }
    }

    private static void EnsureLoaded()
    {
        bool loaded;
        lock (Lock)
            loaded = _loaded;
        if (!loaded)
            Reload();
    }

    private sealed record Manifest(List<ExternalPlannerTimeline.TimelineDefinition> Timelines);
}
```

`Service.Log` が無い場合は `Service.Logger?.Info(...)` など既存のログ関数名に合わせる(`grep -n "public static void Log" BossMod/Framework/Service.cs` で確認)。

- [ ] **Step 4: ExternalPlannerTimeline を Store に切り替える**

`ExternalPlannerTimeline.cs` から `ResourceName`、`_timelines`、`LoadTimelines`、`Manifest` を削除し、

```csharp
        if (zoneID is 0 or > ushort.MaxValue || TimelineStore.ForZone((ushort)zoneID) is not { } timeline || !timeline.AutomaticFallback)
            return null;
```

```csharp
    public static TimelineDefinition? ForZone(ushort zoneID) => TimelineStore.ForZone(zoneID);
    public static IEnumerable<TimelineDefinition> AllTimelines() => TimelineStore.AllTimelines();
```

とする(既存呼び出し側は無変更で動く)。Task 2 Step 7 で一時変更した `GroupBy` があれば、この削除で消える。

- [ ] **Step 5: 設定と Plugin の配線**

`BossModuleConfig.cs` の `UseExternalTimelineHints` の直後に追加:

```csharp
    [PropertyDisplay("Folder with extra fight timelines", tooltip: "JSON files in this folder are loaded next to the built-in timelines. Replay extractions land here. Empty = <plugin config>/timelines.", depends: nameof(UseExternalTimelineHints))]
    public string TimelineUserDirectory = "";
```

`DrawCustom` に再読込ボタンを追加:

```csharp
        if (ImGui.Button("Reload fight timelines"))
            TimelineStore.Reload();
```

`Plugin.cs` の `_externalTimelineHints = new(_ws);` の直前に:

```csharp
        ApplyTimelineDirectory();
        Service.Config.Get<BossModuleConfig>().Modified.Subscribe(ApplyTimelineDirectory);
```

と、Plugin クラスにメソッドを追加:

```csharp
    private void ApplyTimelineDirectory()
    {
        var configured = Service.Config.Get<BossModuleConfig>().TimelineUserDirectory;
        var directory = string.IsNullOrWhiteSpace(configured) ? Path.Combine(_dalamud.ConfigDirectory.FullName, "timelines") : configured;
        if (TimelineStore.UserDirectory != directory)
        {
            TimelineStore.UserDirectory = directory;
            TimelineStore.Reload();
        }
    }
```

`Modified.Subscribe` の戻り値(`EventSubscription`)は Plugin の既存 `_subscriptions` があればそこへ追加、無ければフィールドに保持して `Dispose` で破棄する(既存の `Service.Config.Modified.Subscribe` の扱いに倣う)。

- [ ] **Step 6: ビルドして regression を実行**

Run:
```bash
dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64 && dotnet build tools/external_timeline_regression -c Debug && dotnet tools/external_timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/ExternalTimelineRegression.dll
```
Expected: `failed=0`、`external_fallbacks` ≥ Task 1 の値。

- [ ] **Step 7: Commit**

```bash
git add BossMod/Timeline/External/TimelineStore.cs BossMod/Timeline/External/ExternalPlannerTimeline.cs BossMod/BossModule/BossModuleConfig.cs BossMod/Framework/Plugin.cs tools/external_timeline_regression/Program.cs
git commit -m "feat(timeline): load fight timelines from the embedded manifest and a user folder, ranked by source"
```

---

### Task 4: ReplayTimelineExtractor(本体ライブラリ)

**Files:**
- Create: `BossMod/Timeline/External/ReplayTimelineExtractor.cs`
- Create: `tools/timeline_regression/TimelineRegression.csproj`(`tools/external_timeline_regression/ExternalTimelineRegression.csproj` をコピーし、`ProjectReference` の encounter_timeline を外す)
- Create: `tools/timeline_regression/Program.cs`

**Interfaces:**
- Consumes: `Replay`、`Replay.Participant`、`Replay.Encounter`、Task 1 の型
- Produces:
  ```csharp
  public static class ReplayTimelineExtractor
  {
      public sealed record Pull(ushort Zone, DateTime Start, DateTime End, IReadOnlyList<uint> BossOIDs, IReadOnlyList<Replay.Participant> Enemies);
      public sealed record Window(float Start, float End); // pull 基準秒、閉じた窓のみ
      public const float MinPullSeconds = 20f;
      public static IReadOnlyList<Pull> FindPulls(Replay replay);
      public static IReadOnlyList<Window> NoTargetWindows(Pull pull);        // 生存・非 ally・targetable な敵が 0 の区間
      public static IReadOnlyList<Window> BossUntargetableWindows(Pull pull); // BossOIDs の TargetableHistory
      public static ExternalPlannerTimeline.TimelineDefinition? Build(ushort zone, IReadOnlyList<Pull> pulls); // 同 zone の pull 群 → Source=Replay
      public static IReadOnlyDictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> Extract(IEnumerable<Replay> replays);
      public static float Confidence(IReadOnlyList<float> samples); // 1 − IQR/20, 下限 0.2、サンプル 1 個なら 0.6
  }
  ```

- [ ] **Step 1: 新しい regression ツールと失敗する Check を作る**

`tools/timeline_regression/TimelineRegression.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <DalamudLibPath>$(appdata)\XIVLauncher\addon\Hooks\dev\</DalamudLibPath>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="BossModReborn" HintPath="..\..\BossMod\bin\x64\Debug\BossModReborn.dll" />
    <Reference Include="Dalamud" HintPath="$(DalamudLibPath)Dalamud.dll" />
    <Reference Include="Lumina" HintPath="$(DalamudLibPath)Lumina.dll" />
    <Reference Include="Lumina.Excel" HintPath="$(DalamudLibPath)Lumina.Excel.dll" />
    <None Include="$(DalamudLibPath)*.dll" Link="%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

`tools/timeline_regression/Program.cs`(合成 Replay ヘルパ + Check):

```csharp
using System.Numerics;
using BossMod;

var failures = new List<string>();
var tests = 0;
void Check(string name, Action test)
{
    ++tests;
    try { test(); }
    catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); }
}
void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
DateTime At(float seconds) => t0.AddSeconds(seconds);

// An enemy that exists from `from` to `to`, targetable except inside `gaps`, with the given max HP.
Replay.Participant Enemy(ulong id, uint oid, float from, float to, uint maxHP, params (float start, float end)[] gaps)
{
    var p = new Replay.Participant(id) { OID = oid, Type = ActorType.Enemy, ZoneID = 1000 };
    p.WorldExistence.Add(new(At(from), At(to)));
    p.EffectiveExistence = new(At(from), At(to));
    p.HPMPHistory.Add(At(from), new(maxHP, maxHP, 0, 0, 0));
    p.TargetableHistory.Add(At(from), true);
    foreach (var (s, e) in gaps)
    {
        p.TargetableHistory.Add(At(s), false);
        p.TargetableHistory.Add(At(e), true);
    }
    p.DeadHistory.Add(At(to), true);
    p.HasAnyActions = true;
    return p;
}

Replay WithEnemies(params Replay.Participant[] enemies)
{
    var r = new Replay();
    r.Participants.AddRange(enemies);
    return r;
}

Replay.Cast Cast(uint action, float start, float duration)
    => new(new(ActionType.Spell, action), duration, null, default, default, false) { Time = new(At(start), At(start + duration)) };

Check("pull without encounter is found from enemy existence", () =>
{
    var boss = Enemy(1, 0x100, 10, 130, 1_000_000);
    var pulls = ReplayTimelineExtractor.FindPulls(WithEnemies(boss));
    Require(pulls.Count == 1, $"pulls={pulls.Count}");
    Require(pulls[0].BossOIDs.SequenceEqual([0x100u]), "boss oid");
    Require(Math.Abs((pulls[0].Start - At(10)).TotalSeconds) < 0.01, "pull start");
});
Check("short trash pull is dropped", () =>
{
    var trash = Enemy(2, 0x200, 10, 25, 50_000);
    Require(ReplayTimelineExtractor.FindPulls(WithEnemies(trash)).Count == 0, "trash kept");
});
Check("encounter wins over heuristics", () =>
{
    var boss = Enemy(1, 0x100, 10, 130, 1_000_000);
    var r = WithEnemies(boss);
    r.Encounters.Add(new(1, 0x100, 1234) { Time = new(At(12), At(128)) });
    var pulls = ReplayTimelineExtractor.FindPulls(r);
    Require(pulls.Count == 1 && pulls[0].Zone == 1234 && Math.Abs((pulls[0].Start - At(12)).TotalSeconds) < 0.01, "encounter pull");
});
Check("no-target window ignores boss gaps covered by adds", () =>
{
    var boss = Enemy(1, 0x100, 0, 200, 1_000_000, (50, 80), (120, 140));
    var add = Enemy(2, 0x101, 55, 75, 100_000);
    var pull = ReplayTimelineExtractor.FindPulls(WithEnemies(boss, add)).Single();
    var noTarget = ReplayTimelineExtractor.NoTargetWindows(pull);
    Require(noTarget.Count == 3, $"windows={noTarget.Count}: {string.Join(",", noTarget)}");
    Require(noTarget.Any(w => Math.Abs(w.Start - 120) < 0.01 && Math.Abs(w.End - 140) < 0.01), "second boss gap is a NoTarget window");
    Require(noTarget.Any(w => Math.Abs(w.Start - 50) < 0.01 && Math.Abs(w.End - 55) < 0.01), "gap before the add spawns");
    Require(noTarget.Any(w => Math.Abs(w.Start - 75) < 0.01 && Math.Abs(w.End - 80) < 0.01), "gap after the add dies");
    var bossWindows = ReplayTimelineExtractor.BossUntargetableWindows(pull);
    Require(bossWindows.Count == 2, "boss windows");
});
Check("build merges pulls by median and keeps majority sync points", () =>
{
    Replay.Participant Boss(ulong id, float shift)
    {
        var b = Enemy(id, 0x100, 0, 200, 1_000_000, (50 + shift, 80 + shift));
        b.Casts.Add(Cast(0x1000, 10 + shift, 3));
        b.Casts.Add(Cast(0x1001, 30 + shift, 3));
        return b;
    }
    var pulls = new[] { 0f, 2f, 4f }.Select(shift => ReplayTimelineExtractor.FindPulls(WithEnemies(Boss(1, shift))).Single()).ToList();
    pulls[0].Enemies[0].Casts.Add(Cast(0x1002, 40, 3)); // only in one pull: dropped
    var def = ReplayTimelineExtractor.Build(1000, pulls)!;
    Require(def.Source == ExternalPlannerTimeline.TimelineSource.Replay, "source");
    var seq = def.Sequences.Single();
    Require(seq.BossOIDs!.SequenceEqual([0x100u]), "boss oids");
    Require(seq.States.Count(s => s.Kind == ExternalPlannerTimeline.ExternalStateKind.CastStart) == 2, "majority sync points");
    Require(Math.Abs(seq.States.First(s => s.IDs[0] == 0x1000).Time - 12) < 0.01, "median time");
    var window = seq.Windows!.Single(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget);
    Require(Math.Abs(window.Start - 52) < 0.01 && Math.Abs(window.End!.Value - 82) < 0.01, "median window");
    Require(window.Confidence > 0.7f, $"tight spread keeps confidence high, got {window.Confidence}");
});
Check("confidence drops with spread", () =>
{
    Require(ReplayTimelineExtractor.Confidence([10, 11, 12]) >= 0.9f, "tight");
    Require(ReplayTimelineExtractor.Confidence([10, 20, 30, 40]) < 0.5f, "wide");
    Require(ReplayTimelineExtractor.Confidence([10]) == 0.6f, "single sample");
    Require(ReplayTimelineExtractor.Confidence([0, 100, 200, 300]) == 0.2f, "floor");
});
Check("ability events become AbilityUsed sync points", () =>
{
    var boss = Enemy(1, 0x100, 0, 200, 1_000_000);
    var r = WithEnemies(boss);
    r.Actions.Add(new(new(ActionType.Spell, 0x2000), At(15), boss, null, default, 0.6f, 0, 0, default));
    var pull = ReplayTimelineExtractor.FindPulls(r).Single();
    var def = ReplayTimelineExtractor.Build(1000, [pull])!;
    Require(def.Sequences[0].States.Any(s => s.Kind == ExternalPlannerTimeline.ExternalStateKind.AbilityUsed && s.IDs[0] == 0x2000 && Math.Abs(s.Time - 15) < 0.01), "ability sync point");
});

foreach (var failure in failures)
    Console.Error.WriteLine(failure);
Console.WriteLine($"tests={tests} passed={tests - failures.Count} failed={failures.Count}; source=synthetic; replay=none");
return failures.Count == 0 ? 0 : 1;
```

- [ ] **Step 2: ビルドして失敗を確認**

Run: `dotnet build tools/timeline_regression -c Debug`
Expected: `ReplayTimelineExtractor` が無くコンパイルエラー。

- [ ] **Step 3: 抽出器を実装**

`BossMod/Timeline/External/ReplayTimelineExtractor.cs`:

```csharp
namespace BossMod;

// Builds imported-timeline data from replays recorded on this client. A pull is the unit: the encounter when a module ran,
// otherwise the span during which boss-sized enemies existed. Sync points come from the boss casts and ability effects,
// windows from what was actually targetable, and several pulls of the same fight are merged by median.
public static class ReplayTimelineExtractor
{
    public sealed record Pull(ushort Zone, DateTime Start, DateTime End, IReadOnlyList<uint> BossOIDs, IReadOnlyList<Replay.Participant> Enemies)
    {
        public float Seconds(DateTime t) => (float)(t - Start).TotalSeconds;
    }
    public sealed record Window(float Start, float End);

    public const float MinPullSeconds = 20f;
    private const float PullGapSeconds = 5f; // enemies whose existence overlaps or nearly touches belong to one pull
    private const float BossHPShare = 0.5f; // an enemy with at least this share of the biggest max HP in the pull is a boss

    public static IReadOnlyList<Pull> FindPulls(Replay replay)
    {
        var enemies = replay.Participants.Where(p => p.Type == ActorType.Enemy && !p.WasAlly && p.WorldExistence.Count > 0 && (p.HasAnyActions || p.IsTargetOfAnyActions)).ToList();
        List<Pull> result = [];
        if (replay.Encounters.Count > 0)
        {
            foreach (var enc in replay.Encounters.Where(e => e.Time.End > e.Time.Start))
            {
                var inside = enemies.Where(p => p.WorldExistence.Any(r => r.Start < enc.Time.End && r.End > enc.Time.Start)).ToList();
                result.Add(new(enc.Zone, enc.Time.Start, enc.Time.End, [enc.OID], inside));
            }
            return result;
        }

        // No module ran: cluster enemy lifetimes into pulls.
        var spans = enemies.SelectMany(p => p.WorldExistence.Select(r => (Start: r.Start, End: EndOfLife(p, r), Enemy: p))).OrderBy(s => s.Start).ToList();
        var index = 0;
        while (index < spans.Count)
        {
            var start = spans[index].Start;
            var end = spans[index].End;
            List<Replay.Participant> members = [spans[index].Enemy];
            ++index;
            while (index < spans.Count && (spans[index].Start - end).TotalSeconds <= PullGapSeconds)
            {
                if (spans[index].End > end)
                    end = spans[index].End;
                if (!members.Contains(spans[index].Enemy))
                    members.Add(spans[index].Enemy);
                ++index;
            }
            if ((end - start).TotalSeconds < MinPullSeconds)
                continue;
            var maxHP = members.Max(p => MaxHP(p));
            if (maxHP == 0)
                continue;
            var bosses = members.Where(p => MaxHP(p) >= maxHP * BossHPShare).Select(p => p.OID).Distinct().Order().ToList();
            var zone = (ushort)(members[0].ZoneID is > 0 and <= ushort.MaxValue ? members[0].ZoneID : 0);
            result.Add(new(zone, start, end, bosses, members));
        }
        return result;
    }

    private static DateTime EndOfLife(Replay.Participant p, Replay.TimeRange existence)
    {
        var death = p.DeadHistory.FirstOrDefault(kv => kv.Value && kv.Key >= existence.Start && kv.Key <= existence.End);
        return death.Key != default ? death.Key : existence.End;
    }

    private static uint MaxHP(Replay.Participant p) => p.HPMPHistory.Count == 0 ? 0 : p.HPMPHistory.Values.Max(hp => hp.MaxHP);

    // Nothing attackable: every enemy is gone, dead, allied or untargetable.
    public static IReadOnlyList<Window> NoTargetWindows(Pull pull)
    {
        var edges = new SortedSet<DateTime> { pull.Start, pull.End };
        foreach (var p in pull.Enemies)
        {
            foreach (var r in p.WorldExistence) { edges.Add(r.Start); edges.Add(r.End); }
            foreach (var t in p.TargetableHistory.Keys) edges.Add(t);
            foreach (var t in p.DeadHistory.Keys) edges.Add(t);
            foreach (var t in p.AllyHistory.Keys) edges.Add(t);
        }
        List<Window> result = [];
        float? openAt = null;
        foreach (var edge in edges.Where(e => e >= pull.Start && e <= pull.End))
        {
            var probe = edge.AddTicks(1);
            var attackable = edge < pull.End && pull.Enemies.Any(p => p.ExistsInWorldAt(probe) && p.TargetableAt(probe) && !p.DeadAt(probe) && !p.AllyAt(probe));
            if (!attackable && openAt == null)
                openAt = pull.Seconds(edge);
            else if (attackable && openAt is { } start)
            {
                result.Add(new(start, pull.Seconds(edge)));
                openAt = null;
            }
        }
        return result;
    }

    public static IReadOnlyList<Window> BossUntargetableWindows(Pull pull)
    {
        List<Window> result = [];
        foreach (var boss in pull.Enemies.Where(p => pull.BossOIDs.Contains(p.OID)))
        {
            float? openAt = null;
            foreach (var (t, targetable) in boss.TargetableHistory)
            {
                if (t < pull.Start || t > pull.End)
                    continue;
                if (!targetable && openAt == null)
                    openAt = pull.Seconds(t);
                else if (targetable && openAt is { } start)
                {
                    result.Add(new(start, pull.Seconds(t)));
                    openAt = null;
                }
            }
        }
        return result.OrderBy(w => w.Start).ToList();
    }

    public static float Confidence(IReadOnlyList<float> samples)
    {
        if (samples.Count <= 1)
            return 0.6f;
        var sorted = samples.Order().ToList();
        var q1 = sorted[(int)Math.Floor((sorted.Count - 1) * 0.25)];
        var q3 = sorted[(int)Math.Ceiling((sorted.Count - 1) * 0.75)];
        return Math.Clamp(1f - (q3 - q1) / 20f, 0.2f, 1f);
    }

    private static float Median(IReadOnlyList<float> samples)
    {
        var sorted = samples.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2f;
    }

    public static ExternalPlannerTimeline.TimelineDefinition? Build(ushort zone, IReadOnlyList<Pull> pulls)
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
            var events = pull.Enemies.Where(p => bossOIDs.Contains(p.OID))
                .SelectMany(p => p.Casts.Where(c => c.Time.Start >= pull.Start && c.Time.Start <= pull.End).Select(c => (Time: c.Time.Start, ID: c.ID.ID, Kind: ExternalPlannerTimeline.ExternalStateKind.CastStart)))
                .OrderBy(e => e.Time);
            foreach (var e in events)
            {
                var ordinal = seen.GetValueOrDefault((e.ID, e.Kind));
                seen[(e.ID, e.Kind)] = ordinal + 1;
                samples.GetOrAdd((e.ID, ordinal, e.Kind)).Add(pull.Seconds(e.Time));
            }
        }
        List<ExternalPlannerTimeline.TimelineState> states = [];
        foreach (var (key, times) in samples.Where(kv => kv.Value.Count >= majority))
            states.Add(new(Median(times), "", key.Kind, [key.ID], 0));
        states.Sort((a, b) => a.Time.CompareTo(b.Time));

        List<ExternalPlannerTimeline.TimelineWindow> windows = [];
        windows.AddRange(MergeWindows(pulls.Select(NoTargetWindows).ToList(), majority, ExternalPlannerTimeline.TimelineWindowKind.NoTarget));
        var bossWindows = MergeWindows(pulls.Select(BossUntargetableWindows).ToList(), majority, ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable);
        windows.AddRange(bossWindows);
        foreach (var boss in bossWindows)
            if (!windows.Any(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget && w.Start < boss.End && w.End > boss.Start))
                windows.Add(boss with { Kind = ExternalPlannerTimeline.TimelineWindowKind.AddsPresent });
        windows.Sort((a, b) => a.Start.CompareTo(b.Start));

        var sequence = new ExternalPlannerTimeline.TimelineSequence(0, 0, states, null, bossOIDs, windows);
        var confidence = windows.Count == 0 ? (states.Count == 0 ? 0.2f : 0.6f) : windows.Average(w => w.Confidence);
        return new(zone, $"replay:{string.Join("+", bossOIDs.Select(o => o.ToString("X")))}", true, [sequence], ExternalPlannerTimeline.TimelineSource.Replay, confidence);
    }

    // Windows are matched by index: the i-th window of one pull is the i-th of another. Wipes cut pulls short, so only windows
    // seen in a majority of pulls survive.
    private static IEnumerable<ExternalPlannerTimeline.TimelineWindow> MergeWindows(IReadOnlyList<IReadOnlyList<Window>> perPull, int majority, ExternalPlannerTimeline.TimelineWindowKind kind)
    {
        var maxCount = perPull.Max(w => w.Count);
        for (var i = 0; i < maxCount; ++i)
        {
            var starts = perPull.Where(w => w.Count > i).Select(w => w[i].Start).ToList();
            var ends = perPull.Where(w => w.Count > i).Select(w => w[i].End).ToList();
            if (starts.Count < majority)
                continue;
            yield return new(kind, Median(starts), Median(ends), Math.Min(Confidence(starts), Confidence(ends)));
        }
    }

    public static IReadOnlyDictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> Extract(IEnumerable<Replay> replays)
    {
        var pulls = replays.SelectMany(FindPulls).Where(p => p.Zone != 0).ToList();
        Dictionary<ushort, ExternalPlannerTimeline.TimelineDefinition> result = [];
        foreach (var group in pulls.GroupBy(p => p.Zone))
        {
            // Dungeons have several bosses per zone: each boss set becomes its own sequence.
            var sequences = group.GroupBy(p => string.Join(",", p.BossOIDs)).Select(g => Build(group.Key, g.ToList())!).ToList();
            var merged = new ExternalPlannerTimeline.TimelineDefinition(group.Key, string.Join(";", sequences.Select(s => s.SourceFile)), true,
                sequences.SelectMany((s, i) => s.Sequences.Select(seq => seq with { Index = i })).ToList(), ExternalPlannerTimeline.TimelineSource.Replay, sequences.Average(s => s.Confidence));
            result[group.Key] = merged;
        }
        return result;
    }
}
```

`GetOrAdd` が無ければ `CollectionsMarshal.GetValueRefOrAddDefault` か手書きの `TryGetValue`/`Add` に置き換える。`Replay.Participant.WasAlly`、`ExistsInWorldAt`、`TargetableAt`、`DeadAt`、`AllyAt` は既存。

- [ ] **Step 4: ビルドしてテスト実行**

Run:
```bash
dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64 && dotnet build tools/timeline_regression -c Debug && dotnet tools/timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/TimelineRegression.dll
```
Expected: `failed=0`。`no-target window ignores boss gaps covered by adds` で窓数が合わない場合は、`Enemy` ヘルパの DeadHistory(死亡で存在終了)と `NoTargetWindows` の `probe` 判定を照らして直す。

- [ ] **Step 5: Commit**

```bash
git add BossMod/Timeline/External/ReplayTimelineExtractor.cs tools/timeline_regression/TimelineRegression.csproj tools/timeline_regression/Program.cs
git commit -m "feat(timeline): extract fight timelines and downtime windows from replays"
```

---

### Task 5: 抽出 CLI

**Files:**
- Create: `tools/replay_timeline_extract/ReplayTimelineExtract.csproj`(Task 4 の csproj と同内容、AssemblyName 違い)
- Create: `tools/replay_timeline_extract/Program.cs`

**Interfaces:**
- Consumes: `ReplayTimelineExtractor.Extract`、`ReplayParserLog.Parse(string path, ref float progress, CancellationToken cancel)`
- Produces: `<out>/<zone>-replay.json`(`{"Timelines":[...]}` 形式、1 zone 1 ファイル)

- [ ] **Step 1: Program.cs を書く**

```csharp
using System.Text.Json;
using BossMod;

// usage: ReplayTimelineExtract [--out <dir>] [--zone <id>] <replay.log|dir>...
// Writes one <zone>-replay.json per zone into <dir> (default: %APPDATA%\XIVLauncher\pluginConfigs\BossMod\timelines).
string? outDir = null;
ushort? onlyZone = null;
List<string> inputs = [];
for (var i = 0; i < args.Length; ++i)
{
    if (args[i] == "--out") outDir = args[++i];
    else if (args[i] == "--zone") onlyZone = ushort.Parse(args[++i]);
    else inputs.Add(args[i]);
}
if (inputs.Count == 0)
{
    Console.Error.WriteLine("usage: ReplayTimelineExtract [--out <dir>] [--zone <id>] <replay.log|dir>...");
    return 2;
}
outDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "BossMod", "timelines");
Directory.CreateDirectory(outDir);

var files = inputs.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.log", SearchOption.AllDirectories) : [p]).ToList();
List<Replay> replays = [];
foreach (var file in files)
{
    try
    {
        var progress = 0f;
        var replay = ReplayParserLog.Parse(file, ref progress, CancellationToken.None);
        if (replay.Ops.Count > 0)
            replays.Add(replay);
        Console.WriteLine($"{Path.GetFileName(file)}: ops={replay.Ops.Count} encounters={replay.Encounters.Count} pulls={ReplayTimelineExtractor.FindPulls(replay).Count}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"{file}: {ex.Message}");
    }
}

var extracted = ReplayTimelineExtractor.Extract(replays);
foreach (var (zone, timeline) in extracted.OrderBy(kv => kv.Key))
{
    if (onlyZone != null && zone != onlyZone)
        continue;
    var path = Path.Combine(outDir, $"{zone}-replay.json");
    File.WriteAllText(path, JsonSerializer.Serialize(new { Timelines = new[] { timeline } }));
    var windows = timeline.Sequences.SelectMany(s => s.Windows ?? []).ToList();
    Console.WriteLine($"zone={zone} sequences={timeline.Sequences.Count} states={timeline.Sequences.Sum(s => s.States.Count)} windows={windows.Count} no_target={windows.Count(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget)} confidence={timeline.Confidence:f2} -> {path}");
}
Console.WriteLine($"replays={replays.Count} zones={extracted.Count}");
return 0;
```

`ReplayParserLog.Parse` が `Service.LuminaGameData` を要求する場合(例外で判明)は `Service.LuminaGameData = new Lumina.GameData(@"C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack"); Service.Config.Initialize();` を先頭に足す(`tools/external_timeline_regression/Program.cs` と同じ)。

- [ ] **Step 2: ローカルのリプレイで実行**

Run:
```bash
dotnet build tools/replay_timeline_extract -c Debug && dotnet tools/replay_timeline_extract/bin/Debug/net10.0-windows10.0.26100.0/ReplayTimelineExtract.dll --out "%TEMP%\tl_replay" "%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays"
```
Expected: 各リプレイの `pulls=`、zone ごとの `no_target=` 行。`zones=` が 1 以上。出力 JSON が `TimelineStore.LoadUserFile` で読める(Task 3 の Store を経由する Task 6 のハーネスで確認)。

- [ ] **Step 3: Commit**

```bash
git add tools/replay_timeline_extract/ReplayTimelineExtract.csproj tools/replay_timeline_extract/Program.cs
git commit -m "feat(tools): replay_timeline_extract writes per-zone replay timelines for the user folder"
```

---

### Task 6: 精度ハーネス(baseline 計測)

**Files:**
- Create: `tools/timeline_hint_harness/TimelineHintHarness.csproj`(Task 4 の csproj と同内容)
- Create: `tools/timeline_hint_harness/Program.cs`
- Modify: `BossMod/Timeline/External/ExternalTimelineHints.cs`(計測用スイッチ 2 つの追加のみ、挙動不変)

**Interfaces:**
- Consumes: `ReplayPlayer`(`WorldState`、`TickForward()`)、`ExternalTimelineHints(WorldState)`/`Update(BossModule?)`、`ExternalMechanicHintProvider.TryGetSnapshot(zone, cfc, now, MechanicHintSources.Timeline, out snapshot)`、`ReplayTimelineExtractor.FindPulls/NoTargetWindows/Extract`、`TimelineStore.SetCandidatesForTesting`
- Produces(ExternalTimelineHints に追加する公開スイッチ):
  ```csharp
  public static bool UseAbilitySync = true;     // Task 7 で参照。Task 6 では宣言のみ(未使用)
  public static ExternalPlannerTimeline.TimelineSource? OnlySource; // null = Store の順位どおり。指定時はその Source の候補だけ使う
  ```

- [ ] **Step 1: スイッチを追加**

`ExternalTimelineHints.cs` のフィールド群の直後:

```csharp
    // Measurement switches for tools/timeline_hint_harness; production leaves them at their defaults.
    public static bool UseAbilitySync = true;
    public static ExternalPlannerTimeline.TimelineSource? OnlySource;
```

`Update` の `_timeline = ExternalPlannerTimeline.ForZone(_ws.CurrentZone);` を

```csharp
            _timeline = OnlySource is { } only
                ? TimelineStore.CandidatesForZone(_ws.CurrentZone).FirstOrDefault(t => t.Source == only)
                : TimelineStore.ForZone(_ws.CurrentZone);
```

に変更する。

- [ ] **Step 2: ハーネスを書く**

`tools/timeline_hint_harness/Program.cs`:

```csharp
using BossMod;

// usage: TimelineHintHarness [--source EventTrigger|Cactbot|Replay] [--sync caststart|all] [--holdout] [--csv <file>] <replay.log|dir>...
// Replays every log through the real ExternalTimelineHints and scores its published target-loss hints against the NoTarget
// windows measured from the same replay. --holdout builds replay timelines from all other logs before scoring one.
const float LongLoss = 8.5f;
const float FalseAlarmSeconds = 30f;
const float LeadCap = 25f;

ExternalPlannerTimeline.TimelineSource? source = null;
var sync = "all";
var holdout = false;
string? csv = null;
List<string> inputs = [];
for (var i = 0; i < args.Length; ++i)
{
    if (args[i] == "--source") source = Enum.Parse<ExternalPlannerTimeline.TimelineSource>(args[++i]);
    else if (args[i] == "--sync") sync = args[++i];
    else if (args[i] == "--holdout") holdout = true;
    else if (args[i] == "--csv") csv = args[++i];
    else inputs.Add(args[i]);
}
if (inputs.Count == 0)
{
    Console.Error.WriteLine("usage: TimelineHintHarness [--source X] [--sync caststart|all] [--holdout] [--csv f] <replay.log|dir>...");
    return 2;
}

Service.LuminaGameData = new Lumina.GameData(@"C:\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack");
Service.Config.Initialize();
Service.Config.Get<BossModuleConfig>().UseExternalTimelineHints = true;
ExternalTimelineHints.UseAbilitySync = sync == "all";
ExternalTimelineHints.OnlySource = source;

var files = inputs.SelectMany(p => Directory.Exists(p) ? Directory.EnumerateFiles(p, "*.log", SearchOption.AllDirectories) : [p]).ToList();
List<(string File, Replay Replay)> replays = [];
foreach (var file in files)
{
    var progress = 0f;
    var replay = ReplayParserLog.Parse(file, ref progress, CancellationToken.None);
    if (replay.Ops.Count > 0)
        replays.Add((file, replay));
}

var rows = new List<string> { "file,zone,pull,window_start,window_end,duration,predicted,lead,loss_error,return_error" };
var hits = 0; var windows = 0; var falseAlarms = 0;
List<float> leads = []; List<float> lossErrors = []; List<float> returnErrors = [];

foreach (var (file, replay) in replays)
{
    List<ushort> overridden = [];
    if (holdout)
    {
        foreach (var (zone, timeline) in ReplayTimelineExtractor.Extract(replays.Where(r => r.File != file).Select(r => r.Replay)))
        {
            TimelineStore.SetCandidatesForTesting(zone, [timeline, .. TimelineStore.CandidatesForZone(zone).Where(t => t.Source != ExternalPlannerTimeline.TimelineSource.Replay)]);
            overridden.Add(zone);
        }
    }

    var player = new ReplayPlayer(replay);
    using var hints = new ExternalTimelineHints(player.WorldState);
    // (time, predicted loss at, predicted return at) for every tick that published a hint
    List<(DateTime At, DateTime LossAt, DateTime ReturnAt)> published = [];
    while (player.TickForward())
    {
        hints.Update(null);
        var ws = player.WorldState;
        if (ExternalMechanicHintProvider.TryGetSnapshot(ws.CurrentZone, ws.CurrentCFCID, ws.CurrentTime, MechanicHintSources.Timeline, out var snapshot) && snapshot.TargetLossIn < float.MaxValue)
            published.Add((ws.CurrentTime, ws.CurrentTime.AddSeconds(snapshot.TargetLossIn), ws.CurrentTime.AddSeconds(snapshot.TargetReturnIn)));
    }
    ExternalMechanicHintProvider.ClearNamespace(ExternalTimelineHints.HintNamespace);

    var pulls = ReplayTimelineExtractor.FindPulls(replay);
    var pullIndex = 0;
    List<(DateTime Start, DateTime End)> truth = [];
    foreach (var pull in pulls)
    {
        foreach (var w in ReplayTimelineExtractor.NoTargetWindows(pull).Where(w => w.End - w.Start >= LongLoss))
        {
            var start = pull.Start.AddSeconds(w.Start);
            var end = pull.Start.AddSeconds(w.End);
            truth.Add((start, end));
            ++windows;
            // the first publication before the window started whose predicted loss lands inside the window (with slack)
            var first = published.FirstOrDefault(p => p.At < start && p.LossAt >= start.AddSeconds(-5) && p.LossAt <= end);
            var predicted = first.At != default;
            if (predicted)
            {
                ++hits;
                var lead = Math.Min(LeadCap, (float)(start - first.At).TotalSeconds);
                leads.Add(lead);
                var last = published.Last(p => p.At < start && p.LossAt >= start.AddSeconds(-5) && p.LossAt <= end);
                lossErrors.Add((float)(last.LossAt - start).TotalSeconds);
                returnErrors.Add((float)(last.ReturnAt - end).TotalSeconds);
            }
            rows.Add($"{Path.GetFileName(file)},{pull.Zone},{pullIndex},{w.Start:f1},{w.End:f1},{w.End - w.Start:f1},{(predicted ? 1 : 0)},{(predicted ? leads[^1] : -1):f1},{(predicted ? lossErrors[^1] : 0):f1},{(predicted ? returnErrors[^1] : 0):f1}");
        }
        ++pullIndex;
    }
    // a hint whose predicted loss is not followed by a real window within FalseAlarmSeconds
    foreach (var group in published.GroupBy(p => (long)Math.Floor((p.LossAt - replay.Ops[0].Timestamp).TotalSeconds / 5)))
    {
        var p = group.First();
        if (!truth.Any(t => t.Start >= p.LossAt.AddSeconds(-5) && t.Start <= p.LossAt.AddSeconds(FalseAlarmSeconds)))
            ++falseAlarms;
    }

    foreach (var zone in overridden)
        TimelineStore.SetCandidatesForTesting(zone, null);
}

float Median(List<float> v) => v.Count == 0 ? float.NaN : v.Order().ElementAt(v.Count / 2);
float P90(List<float> v) => v.Count == 0 ? float.NaN : v.Order().ElementAt((int)Math.Min(v.Count - 1, Math.Floor(v.Count * 0.9)));
Console.WriteLine($"source={source?.ToString() ?? "ranked"} sync={sync} holdout={holdout} replays={replays.Count} windows={windows} hit={hits} hit_rate={(windows == 0 ? 0 : (float)hits / windows):f3} lead_median={Median(leads):f1} false_alarms={falseAlarms} loss_err_median={Median(lossErrors.Select(Math.Abs).ToList()):f1} loss_err_p90={P90(lossErrors.Select(Math.Abs).ToList()):f1} return_err_median={Median(returnErrors.Select(Math.Abs).ToList()):f1}");
if (csv != null)
    File.WriteAllLines(csv, rows);
return 0;
```

- [ ] **Step 3: ビルドして baseline を計測**

Run(順に、出力を `docs/superpowers/plans/2026-09-26-unified-timeline-results.md` に貼る):
```bash
dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64 && dotnet build tools/timeline_hint_harness -c Debug
dotnet tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll --source EventTrigger --sync caststart "%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays"
dotnet tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll --source Cactbot --sync caststart "%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays"
```
Expected: 2 行の集計。この時点(Task 7 前)では `--sync all` は `caststart` と同値、`--source Replay` は候補なしで windows のみ出る。`hit_rate` が 0 でも失敗ではない(現行の実力)。`windows=0` なら `FindPulls` の pull 検出をリプレイ 1 本で `--csv` を見て確認する。

- [ ] **Step 4: Commit**

```bash
git add tools/timeline_hint_harness/TimelineHintHarness.csproj tools/timeline_hint_harness/Program.cs BossMod/Timeline/External/ExternalTimelineHints.cs docs/superpowers/plans/2026-09-26-unified-timeline-results.md
git commit -m "feat(tools): timeline_hint_harness scores the timeline follower against replay downtime"
```

---

### Task 7: フォロワー改修

**Files:**
- Modify: `BossMod/Timeline/External/ExternalTimelineHints.cs`
- Modify: `tools/timeline_regression/Program.cs`(純関数の Check 追加)

**Interfaces:**
- Consumes: Task 1 の型、`TimelineStore.CandidatesForZone`、`ws.Actors.CastEvent`(`Event<Actor, ActorCastEvent>`、`ActorCastEvent.Action.ID`)
- Produces(純関数として切り出し、テスト対象):
  ```csharp
  public static int AlignmentScore(ExternalPlannerTimeline.TimelineSequence sequence, float anchorTime, IReadOnlyList<(float SecondsAgo, uint ActionID, bool IsCastStart)> history, bool useAbilitySync);
  public static (float LossIn, float ReturnIn) SelectDowntime(ExternalPlannerTimeline.TimelineSequence sequence, float now, float horizon, float minLoss, float minConfidence);
  ```

- [ ] **Step 1: 失敗する Check を追加**

`tools/timeline_regression/Program.cs` の集計出力の直前:

```csharp
ExternalPlannerTimeline.TimelineSequence Seq(List<ExternalPlannerTimeline.TimelineState> states, List<ExternalPlannerTimeline.TimelineWindow>? windows = null)
    => new(0, 0, states, null, null, windows);
ExternalPlannerTimeline.TimelineState St(float t, ExternalPlannerTimeline.ExternalStateKind kind, uint id) => new(t, "", kind, [id], 0);
ExternalPlannerTimeline.TimelineWindow Win(ExternalPlannerTimeline.TimelineWindowKind kind, float s, float? e, float c = 1f) => new(kind, s, e, c);

Check("ability sync points count toward alignment when enabled", () =>
{
    var seq = Seq([St(10, ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, 1), St(20, ExternalPlannerTimeline.ExternalStateKind.AbilityUsed, 2), St(30, ExternalPlannerTimeline.ExternalStateKind.CastStart, 3)]);
    var history = new List<(float, uint, bool)> { (20f, 1u, false), (10f, 2u, false), (0f, 3u, true) };
    Require(ExternalTimelineHints.AlignmentScore(seq, 30, history, true) == 3, "all three agree");
    Require(ExternalTimelineHints.AlignmentScore(seq, 30, history, false) == 1, "only the cast counts without ability sync");
});
Check("short window does not mask long window", () =>
{
    var seq = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 12, 14), Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 20, 40)]);
    var (loss, ret) = ExternalTimelineHints.SelectDowntime(seq, 10, 25, 8.5f, 0.5f);
    Require(Math.Abs(loss - 10) < 0.01 && Math.Abs(ret - 30) < 0.01, $"expected the 20-40 window, got {loss}/{ret}");
});
Check("low confidence window not published", () =>
{
    var seq = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 20, 40, 0.3f)]);
    Require(ExternalTimelineHints.SelectDowntime(seq, 10, 25, 8.5f, 0.5f).LossIn == float.MaxValue, "published low confidence");
});
Check("adds-present window is not downtime; legacy states fall back", () =>
{
    var withAdds = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.BossUntargetable, 20, 40), Win(ExternalPlannerTimeline.TimelineWindowKind.AddsPresent, 20, 40)]);
    Require(ExternalTimelineHints.SelectDowntime(withAdds, 10, 25, 8.5f, 0.5f).LossIn == float.MaxValue, "adds window published");
    var legacy = Seq([St(20, ExternalPlannerTimeline.ExternalStateKind.Untargetable, 0), St(40, ExternalPlannerTimeline.ExternalStateKind.Targetable, 0)]);
    var (loss, ret) = ExternalTimelineHints.SelectDowntime(legacy, 10, 25, 8.5f, 0.5f);
    Require(Math.Abs(loss - 10) < 0.01 && Math.Abs(ret - 30) < 0.01, "legacy fallback");
});
Check("window beyond horizon is not published", () =>
{
    var seq = Seq([], [Win(ExternalPlannerTimeline.TimelineWindowKind.NoTarget, 60, 80)]);
    Require(ExternalTimelineHints.SelectDowntime(seq, 10, 25, 8.5f, 0.5f).LossIn == float.MaxValue, "beyond horizon");
});
```

- [ ] **Step 2: ビルドして失敗を確認**

Run: `dotnet build tools/timeline_regression -c Debug`
Expected: `AlignmentScore` / `SelectDowntime` が無くコンパイルエラー。

- [ ] **Step 3: フォロワーを改修**

`ExternalTimelineHints.cs` を次の方針で書き換える(全文は長いので変更点を列挙。既存の定数・namespace・Publish/Unpublish は不変):

1. 履歴型を `List<(DateTime At, uint ActionID, bool IsCastStart)> _history` にし、`CastEvent` を購読:
   ```csharp
   _subscriptions = new(ws.Actors.CastStarted.Subscribe(OnCastStarted), ws.Actors.CastEvent.Subscribe(OnCastEvent), ws.Actors.InCombatChanged.Subscribe(OnCombatChanged), ws.Actors.IsTargetableChanged.Subscribe(OnTargetableChanged));
   ```
   ```csharp
   private void OnCastStarted(Actor actor)
   {
       if (_timeline == null || actor.Type != ActorType.Enemy || actor.IsAlly || actor.CastInfo is not { } cast || !cast.IsSpell())
           return;
       Observe(cast.Action.ID, true);
   }

   private void OnCastEvent(Actor actor, ActorCastEvent ev)
   {
       if (!UseAbilitySync || _timeline == null || actor.Type != ActorType.Enemy || actor.IsAlly || !ev.Action.IsSpell())
           return;
       Observe(ev.Action.ID, false);
   }
   ```
   `ActionID.IsSpell()` が無ければ `ev.Action.Type == ActionType.Spell`。
2. `Observe(uint actionID, bool isCastStart)` に旧 `OnCastStarted` の整合ロジックを移す。候補状態は `isCastStart ? Kind == CastStart : Kind == AbilityUsed`。`_timeline.Sequences` に加え、`OnlySource == null` のときは `TimelineStore.CandidatesForZone(_timelineZone)` の全候補の Sequence を走査し、ベストの候補を `_timeline` にする(候補は Store の順位どおりなので同点なら先勝ち)。
3. `AlignmentScore` を純関数に:
   ```csharp
   public static int AlignmentScore(ExternalPlannerTimeline.TimelineSequence sequence, float anchorTime, IReadOnlyList<(float SecondsAgo, uint ActionID, bool IsCastStart)> history, bool useAbilitySync)
   {
       var score = 0;
       foreach (var (secondsAgo, actionID, isCastStart) in history)
       {
           if (!isCastStart && !useAbilitySync)
               continue;
           var expected = anchorTime - secondsAgo;
           if (expected < sequence.StartTime - AlignmentTolerance)
               continue;
           var wanted = isCastStart ? ExternalPlannerTimeline.ExternalStateKind.CastStart : ExternalPlannerTimeline.ExternalStateKind.AbilityUsed;
           foreach (var state in sequence.States)
           {
               if (state.Kind == wanted && state.IDs.Contains(actionID) && Math.Abs(state.Time - expected) <= AlignmentTolerance)
               {
                   ++score;
                   break;
               }
           }
       }
       return score;
   }
   ```
   呼び出し側は `_history.Select(h => ((float)(now - h.At).TotalSeconds, h.ActionID, h.IsCastStart)).ToList()` を渡す。
4. `OnCombatChanged` の Sequence 選択:
   ```csharp
   var candidates = _timeline.Sequences.Where(s => s.States.Count > 0 || s.Windows is { Count: > 0 }).ToList();
   var byOID = candidates.FirstOrDefault(s => s.BossOIDs is { Count: > 0 } && s.BossOIDs.Contains(actor.OID));
   var sequence = byOID ?? (_lastConfirmed is { } last && candidates.IndexOf(last) is var i and >= 0 && i + 1 < candidates.Count ? candidates[i + 1] : candidates.FirstOrDefault());
   ```
   `_lastConfirmed` は `_confirmed` が true になった時点の `_sequence` を保持するフィールド(zone 変更でクリア)。
5. `FindTrackedEnemy`:
   ```csharp
   private Actor? FindTrackedEnemy()
   {
       Actor? best = null;
       foreach (var actor in _ws.Actors)
       {
           if (actor.Type != ActorType.Enemy || actor.IsAlly || !actor.InCombat || actor.IsDeadOrDestroyed)
               continue;
           if (_sequence?.BossOIDs is { Count: > 0 } bosses && bosses.Contains(actor.OID))
               return actor;
           if (best == null || actor.HPMP.MaxHP > best.HPMP.MaxHP)
               best = actor;
       }
       return best;
   }
   ```
6. `CheckAgainstWorld` の比較を「予測 NoTarget」対「targetable な非 ally 敵が 1 体でもいるか」に:
   ```csharp
   var anyAttackable = _ws.Actors.Any(a => a.Type == ActorType.Enemy && !a.IsAlly && a.InCombat && !a.IsDeadOrDestroyed && a.IsTargetable);
   var predictedAttackable = !PredictedNoTarget();
   ```
   `PredictedNoTarget()` は Windows があれば NoTarget 窓に `now` が入るか、無ければ旧 `PredictedTargetable()` の否定。`_confirmed |= !predictedAttackable` は同じ意味で残す。
7. `NextDowntime()` は `SelectDowntime(_sequence, TimelineTime, Horizon, MinPublishedLoss, MinWindowConfidence)`(`private const float MinWindowConfidence = 0.5f`)を返す:
   ```csharp
   public static (float LossIn, float ReturnIn) SelectDowntime(ExternalPlannerTimeline.TimelineSequence sequence, float now, float horizon, float minLoss, float minConfidence)
   {
       var end = sequence.PredictionEndTime ?? float.MaxValue;
       if (sequence.Windows is { Count: > 0 } windows)
       {
           foreach (var w in windows.Where(w => w.Kind == ExternalPlannerTimeline.TimelineWindowKind.NoTarget).OrderBy(w => w.Start))
           {
               if (w.End is not { } wEnd || wEnd <= now || w.Start > end || w.Confidence < minConfidence || wEnd - w.Start < minLoss)
                   continue;
               if (w.Start - now > horizon)
                   break;
               return (Math.Max(0f, w.Start - now), wEnd - now);
           }
           return (float.MaxValue, float.MaxValue);
       }
       // Legacy sequences: read the targetability markers, skipping windows that overlap an AddsPresent window.
       var lossAt = float.MaxValue;
       var targetable = true;
       foreach (var state in sequence.States)
       {
           if (state.Time > end)
               break;
           if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Untargetable && targetable) { targetable = false; lossAt = state.Time; }
           else if (state.Kind == ExternalPlannerTimeline.ExternalStateKind.Targetable && !targetable)
           {
               targetable = true;
               if (state.Time > now && state.Time - lossAt >= minLoss)
               {
                   if (lossAt - now > horizon)
                       return (float.MaxValue, float.MaxValue);
                   return (Math.Max(0f, lossAt - now), state.Time - now);
               }
               lossAt = float.MaxValue;
           }
       }
       return (float.MaxValue, float.MaxValue);
   }
   ```
   `Publish` の `lossIn > Horizon || returnIn - lossIn < MinPublishedLoss` 判定は残す(二重でも無害)。
8. `_history`、`_lastConfirmed` は zone 変更時にクリア。

- [ ] **Step 4: ビルドして単体 Check と regression を実行**

Run:
```bash
dotnet build BossMod/BossModReborn.csproj -c Debug -p:Platform=x64 && dotnet build tools/timeline_regression -c Debug && dotnet tools/timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/TimelineRegression.dll && dotnet build tools/external_timeline_regression -c Debug && dotnet tools/external_timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/ExternalTimelineRegression.dll
```
Expected: 両方 `failed=0`。

- [ ] **Step 5: 精度ハーネスで再計測(ゲート)**

Run(各行の出力を results.md に追記):
```bash
dotnet build tools/timeline_hint_harness -c Debug
dotnet tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll --source EventTrigger --sync caststart "%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays"
dotnet tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll --source EventTrigger --sync all "%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays"
dotnet tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll --source Cactbot --sync all "%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays"
dotnet tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll --holdout --sync all "%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays"
dotnet tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll --sync all "%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays"
```
Expected(ゲート): Task 6 の baseline(`--source EventTrigger --sync caststart`)に対し、`--sync all` と `--holdout` で `hit_rate` 上昇、`false_alarms` と `loss_err_median` が同等以下。悪化する zone は `--csv` で特定し、原因(窓の意味論 / 同期点の誤整合 / 抽出の pull 区切り)を results.md に記す。ゲートを満たさない場合は Task 7 の改修を採用せず、原因ごとに切り分けて再計測する。

- [ ] **Step 6: xan ハーネスの不変確認**

Run(隔離手順は memory `rpr-verification-harness` 参照。ここではこの worktree で新旧トレースを比較):
```bash
git stash push -u -m "unified-timeline-task7" -- BossMod/Timeline/External/ExternalTimelineHints.cs
dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 && set XAN_HARNESS_TRACE_DIR=%TEMP%\xan_before && dotnet tools/xan_timeline_harness/bin/x64/Release/net10.0-windows10.0.26100.0/XanTimelineHarness.dll timeline-combat-matrix --job rpr
git stash apply $(git stash list --format='%H %gs' | findstr unified-timeline-task7 | cut -d" " -f1)
dotnet build tools/xan_timeline_harness -c Release -p:Platform=x64 && set XAN_HARNESS_TRACE_DIR=%TEMP%\xan_after && dotnet tools/xan_timeline_harness/bin/x64/Release/net10.0-windows10.0.26100.0/XanTimelineHarness.dll timeline-combat-matrix --job rpr
diff -r %TEMP%\xan_before %TEMP%\xan_after
```
Expected: 差分なし(xan ハーネスは自前で予告を push し `ExternalTimelineHints` を通らない)。stash は `git stash drop` で消す(タグで再検索してから)。

- [ ] **Step 7: Commit**

```bash
git add BossMod/Timeline/External/ExternalTimelineHints.cs tools/timeline_regression/Program.cs docs/superpowers/plans/2026-09-26-unified-timeline-results.md
git commit -m "feat(timeline): align the timeline follower on ability effects and publish no-target windows"
```

---

### Task 8: 仕上げ(結果記録、memory、統合)

**Files:**
- Modify: `docs/superpowers/plans/2026-09-26-unified-timeline-results.md`(最終表)
- Modify: `C:\Users\happy\.claude\projects\F--bossmodreborn\memory\external-timeline-audit-2026-09-24.md`(実装済みの旨と数値を追記)

- [ ] **Step 1: results.md を整える**

表: 条件(source × sync × holdout)ごとに windows / hit_rate / lead_median / false_alarms / loss_err_median / loss_err_p90 / return_err_median。baseline 行を先頭に。

- [ ] **Step 2: 全ツールを最終実行**

Run:
```bash
dotnet tools/external_timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/ExternalTimelineRegression.dll && dotnet tools/timeline_regression/bin/Debug/net10.0-windows10.0.26100.0/TimelineRegression.dll
```
Expected: 両方 `failed=0`。

- [ ] **Step 3: Commit と統合手順の提示**

```bash
git add docs/superpowers/plans/2026-09-26-unified-timeline-results.md
git commit -m "docs: unified timeline measurement results"
```

ユーザーへ: `nin-prekassatsu3rd-stability` への取り込みは `git -C F:/bossmodreborn merge --ff-only claude/content-timeline-review-d2946e`(本体に未コミット変更 MovementOverride.cs / MchCombatState.cs があるので、それらは merge の前後で保持されることを `git status` で確認)。配置は memory `branch-layout-nin-stability` の手順(`-p:Platform=x64` 必須)。
