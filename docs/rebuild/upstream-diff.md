# upstream 差分の分類と隔離計画 (Step 1)

作成日: 2026-10-07 / ブランチ: `rotation-rebuild` (起点 `upstream/main` = `5d1814bf0`)

## 0. 比較の前提

- 比較元: `F:\bossmodreborn` の作業ツリー (未コミット変更・未追跡ファイル込み)。`bin/ obj/ .vs/ .git/ claude_works/ results_*` は除外。F: 側は読み取りのみで、checkout / commit / fetch は行っていない。
- F: の HEAD (`1f15e91b9`, ブランチ `nin-prekassatsu3rd-stability`) は GitHub に push されていないため、F: 内で `git merge-base HEAD upstream/main` を読み取り専用で実行し、**分岐点 = `7da11ffe9` (2026-09-17, upstream PR #1358)** を特定した。
  - 分岐点以降: F: 側 256 コミット (うち merge 55) / upstream 側 116 コミット。
- 「カスタム差分」は **分岐点 `7da11ffe9` → F: 作業ツリー** で計算した (upstream/main との直接比較だと、upstream がその後に入れた 116 コミット分が「削除」に見えてしまうため)。
- F: の作業ツリーをそのまま再現したコミットを、ローカル専用ブランチ **`custom-snapshot`** (親 = `7da11ffe9`) として G:\bmr_re に作成した。以後の参照・diff はこのブランチで行える。
  - 注意: `custom-snapshot` には `tools/blm_regression/results` (約 560 MB) などの生成物も入っている。**push しないこと** (GitHub の 100 MB 制限にも掛かる)。

各ファイルの「upstream 状態」列の意味:

- 未変更: 分岐点以降 upstream はそのファイルを触っていない (同期時に衝突しない)
- **upstreamも変更**: 分岐点以降 upstream も変更している (パッチを当て直すと衝突しうる)
- 同一: F: の内容が upstream/main と完全一致
- upstreamで移動/削除: upstream/main の同じパスに存在しない

## 1. サマリ

| 分類 | 内容 | 新規 (A) | 変更 (M) | 追加行 | 削除行 |
|---|---|---:|---:|---:|---:|
| (a) | カスタム回しモジュール (xan / akechi / veyn / Utility / ActionLocks / MechanicHints / WindDown / LocalRotationAI 系) | 14 | 29 | 39,252 | 3,242 |
| (b) | tools/* ハーネス・回帰テスト・チューニング | 567 | 0 | 1,484,523 | 0 |
| (c) | upstream フレームワークファイルへの変更 | 0 | 67 | 1,499 | 341 |
| (d) | その他 (新規フレームワーク拡張・ボスモジュール修正・ドキュメント等) | 50 | 53 | 25,413 | 713 |
| 計 | | 631 | 149 | 1,550,687 | 4,296 |

(b) の行数の大半は生成物 (`blm_regression/results*`, `mch_regression/fflogs_out`, `mnk_tuning/tmp_probe*`)。

**ゲームオフセット / シグネチャの差分: なし。**
F: は `Framework/ClientStructsEx.cs`・`Network/*`・フック定義に一切手を入れておらず、追加行にも `FieldOffset` / シグネチャ文字列 / `ScanText` 系は無い (`0x...` は OID・色・ビットマスクのみ)。したがってローカルオフセット上書きファイルに移すべき値は現時点で存在しない。ゲームパッチ時のオフセット修正は upstream 同期 (と FFXIVClientStructs = Dalamud 側更新) だけで受け取れる。将来ローカルで先行修正が必要になった場合の方式は §6 に記す。

## 2. 隔離の構成 (rotation-rebuild ブランチで実施済み)

upstream のファイルは **1 つも編集していない** (`git diff upstream/main -- BossMod BossMod.SourceGen` は空)。追加したものはすべて upstream に存在しないパス。

```
Directory.Build.targets      … Custom/**/*.cs を BossModReborn アセンブリにコンパイル (upstream csproj は無変更)
global.json                  … F: から持ち込み (SDK 10.0.400, latestFeature)
Custom/                      … カスタム専用コード (コンパイル対象)
Custom/_pending/             … 参照用に保管、コンパイル対象外
  needs-upstream-hook/       … upstream 側フックが無いとビルドできないファイル (§5)
  superseded-by-upstream/    … upstream が同じものを入れたファイル (AtomosPiece.cs)
tools/                       … ハーネス類 (upstream に tools/ は無いので衝突しない)
tools/.gitignore             … 旧 fork がルート .gitignore に足していたパターンをここへ移設
docs/custom/                 … 旧 fork のメモ (agents_*.md, codex_task_*.md, docs/superpowers/*)
docs/rebuild/upstream-diff.md … この文書
```

### 別 csproj (BossMod.Custom) にしなかった理由

- 回しモジュール・ボスモジュール・設定ノードの登録は `BossMod.SourceGen` が **同一コンパイル内の型を走査して** `GeneratedRegistries` を生成する方式。別アセンブリに置くと SourceGen から見えず登録されない。
- 逆参照も成立しない: Custom は BossMod の型 (RotationModule, WorldState …) に依存するので、プラグイン側から Custom を参照すると循環参照になる。Dalamud プラグインは単一アセンブリ前提。
- そこで `Directory.Build.targets` (リポジトリ直下、upstream に存在しない) から `<Compile Include="Custom\**\*.cs">` を差し込み、**物理的には別フォルダ・論理的には同一アセンブリ** とした。条件 `'$(MSBuildProjectName)' == 'BossModReborn'` で SourceGen / tools の csproj には影響しない。
- `ExternalTimelines.json` の埋め込みリソース指定も同ファイルに移した (旧 fork は upstream csproj に追記していた)。

### upstream を編集せずに済ませた工夫

- **CustomConfig** (`Custom/Config/CustomConfig.cs`): 旧 fork が `BossModuleConfig` / `ActionTweaksConfig` に追加していた 8 項目を独自の ConfigNode に移した。参照側 (SplatoonLiveZones, ExternalPlannerTimeline, ExternalTimelineHints, MchRealtimeValuePlanner) も書き換え済み。旧設定ファイルに保存済みの値は自動移行されない。
- **UnsafeAccessor** (`Custom/ActionQueue/ActionQueueAccess.cs`): 旧 fork は `ActionQueue.CanExecute` を `private → internal` に変えていた。.NET の `[UnsafeAccessor]` で private メソッドを直接呼ぶ拡張 `CanExecuteEx` に置き換えた。upstream がシグネチャを変えるとビルドは通るが実行時 `MissingMethodException` になる点に注意。同じ手法で `PlanExecution` / `RaidCooldowns` の追加 API も upstream 無編集で Custom 側へ移せる見込み (§3 参照)。

### ビルド結果

`dotnet build BossMod/BossModReborn.csproj -c Release --no-incremental` → **成功 (エラー 0 / 警告 6)**。警告 6 件はすべて upstream ファイル内 (xan Healer/SMN/AST/MNK, GaugeVisualizer) の FFXIVClientStructs 旧 API 使用 (CS0618) で、upstream/main 単体でも出るもの。Custom 由来の警告は 0。

コンパイルされている Custom ファイル (28 本 / 約 9,200 行):
Localization 一式 (Loc, LocalizationConfig, LocalizationJapanese, LocalizationJapaneseStrategy)、FightTimeEstimator、FunctionGemmaRotationSupervisor、LocalRotationAI、LocalRotationAICollector、MchRealtimeValuePlanner、SplatoonSafeImport、SplatoonLiveZones、ExternalAOEProvider、ExternalEncounterHintProvider、ExternalMechanicHintProvider、NINBurstPlanner、RprPotency、ExternalHintAdapter、SplatoonHintSource、SplatoonHintBridge、ActionLocks、WindDownPotency、T01Zodiark (+Enums)、ReplacingFile、CustomConfig、ActionQueueAccess。

ただし **コンパイルされる ≠ 動く**。これらの多くは呼び出し元 (Plugin.cs, AIHintsBuilder.cs, ActionManagerEx.cs, 改変済み xan モジュール) が upstream ファイル側にあるため、現状では「存在するが誰からも呼ばれない」状態。配線は §3 のフックで行う。例外: T01Zodiark はボスモジュールなので SourceGen 登録だけで有効になる。

## 3. upstream フレームワークファイル (c) ごとの最小フック

方針の凡例:

- **不要(移設済)**: Custom 側で代替済み、upstream 編集なし
- **Custom化可**: upstream を触らず Custom 側 (拡張メソッド / UnsafeAccessor / 独自基底クラス / 既存イベント購読) で再現できる
- **フック**: upstream に数行の差し込み口が必要
- **PR候補**: 純粋なバグ修正・汎用改善。upstream へ PR すれば以後の維持コストがゼロになる
- **破棄推奨**: 一時的なデバッグ・好みの変更・upstream で既に同等修正済み
- **L10n**: 日本語化のための `Loc.Tr(...)` 包み。§4 参照

| ファイル | +/- | upstream | 変更内容 | 最小フック / 方針 |
|---|---|---|---|---|
| AI/AIBehaviour.cs | 14/3 | 未変更 | ナビ更新を 100ms 間引き、`Hints.PathfindTarget` があれば目的地指定探索 | フック: PathfindTarget 分岐 (NavigationDecision 改修とセット)。間引きは PR候補 |
| AI/AIManagementWindow.cs | 48/36 | 未変更 | 表示文字列の Loc.Tr 化 | L10n |
| AI/AIManager.cs | 48/26 | 未変更 | プリセット差し替え追従、EchoToChat=false 時に処理が続行してしまうバグ修正 | PR候補 (純バグ修正) |
| ActionQueue/ActionDefinition.cs | 1/1 | 未変更 | `SpellAspect` が常に None → シートの Aspect を返す | PR候補 (1 行) |
| ActionQueue/ActionQueue.cs | 1/1 | 未変更 | `CanExecute` private→internal | 不要(移設済): UnsafeAccessor |
| ActionQueue/Casters/BLM.cs | 10/10 | 未変更 | コメント上の詠唱時間のみ | 破棄推奨 (挙動に影響なし) |
| ActionQueue/Melee/BST.cs | 31/1 | 未変更 | SID 追加、ShieldCharge のチャージ数トレイト登録 | SID は Custom 側定数で代替可。トレイト登録は PR候補 |
| ActionTweaks/ActionTweaksConfig.cs | 5/2 | 未変更 | `EnableLocalRotationAI` 追加 + L10n | 不要(移設済): CustomConfig |
| ActionTweaks/ManualActionQueueTweak.cs | 46/3 | 未変更 | 黒魔紋の手動キュー保持 6 秒、縮地の着地点をターゲット外周に | フック (2 箇所) または PR候補 |
| ActionTweaks/SmartRotationTweak.cs | 87/26 | 未変更 | 視線攻撃を向く必要があるアクションを止める `ShouldBlockActionForGaze` | PR候補 (ActionManagerEx の gaze ブロックとセット) |
| Autorotation/MiscAI/GoToPositional.cs | 1/1 | 未変更 | TrueNorth を AID で探していたバグ → SID | PR候補 |
| Autorotation/MiscAI/NormalMovement.cs | 4/1 | **upstreamも変更** | PathfindTarget 分岐 | フック (AIBehaviour と同じ) |
| Autorotation/Plan.cs | 7/6 | 未変更 | クローンが浅く「元に戻す」が DB に漏れるバグ修正 + FindTrackIndex | クローン修正は PR候補。FindTrackIndex は旧日本語 internal 名の互換用 → プリセット/プランを一度変換すれば不要 |
| Autorotation/PlanExecution.cs | 99/0 | 未変更 | `OverdueGraceSeconds` / `CurrentStateOverdue` / `EstimateTargetableWindows` 追加 (純追加) | Custom化可: 公開メンバ + UnsafeAccessor (States, ForcedTargets, Pull) で拡張メソッド化 |
| Autorotation/Preset.cs | 2/2 | 未変更 | FindTrackIndex / FindOptionIndex | Plan.cs と同じ (一度変換すれば不要) |
| Autorotation/RotationModule.cs | 32/5 | 未変更 | `ResolveTargetOverride` 別名、FindDutyActionSlot 非アロケーション化、構造体スコアラ版 FindBetterTarget | Custom化可: 自前の中間基底クラスに置く。最適化部分は PR候補 |
| Autorotation/RotationModuleManager.cs | 21/11 | 未変更 | `LastActionRequest` 記録、ActiveModules 差し替え中の列挙保護、LoS 探索の範囲修正 | LastActionRequest は Custom化可 (ClientState の ActionRequested を自前購読)。残りは PR候補 |
| Autorotation/Strategy.cs | 85/7 | 未変更 | **AOEStrategy の表示文言を日本語に直書き**、UIName に Loc.TrStrategy、StrategyConfigLookup | 直書きは破棄 (翻訳辞書側へ)。UIName 2 行は L10n フック。Lookup は Plan.cs と同じ |
| Autorotation/StrategyRenderer.cs | 9/9 | 未変更 | 日本語直書き | L10n (直書き → 辞書) |
| Autorotation/UIRotationWindow.cs | 1/1 | 未変更 | "VBM Multibox" / "Movement Only" を一覧から隠す | 破棄推奨 (プリセット側の HiddenByDefault で代替) |
| Autorotation/UIStrategyValue.cs | 2/2 | 未変更 | 選択肢表示の L10n | L10n |
| BossModReborn.csproj | 1/0 | 未変更 | ExternalTimelines.json 埋め込み | 不要(移設済): Directory.Build.targets |
| BossModule/AIHints.cs | 17/1 | **upstreamも変更** | `PathfindTarget`, `AllowPathfindDiagonalCornerSqueeze`, `Disengage`, `FightRemaining` + Clear() | フック: 最小案は Custom 側 `CustomHints` (毎フレーム Plugin フックでリセット) に移し、upstream には PathfindTarget / CornerSqueeze の 2 項目だけ (経路探索が読むため) |
| BossModule/AIHintsBuilder.cs | 58/1 | 未変更 | External*Provider.ApplyToHints、FightTimeEstimator 更新、FightPriorStore | フック 1 行: Normalize 前に `CustomHooks.AfterHintsGathered(...)`。推定器・Prior は Custom 側に保持 |
| BossModule/BossModule.cs | 7/3 | **upstreamも変更** | ctor で `ExternalPlannerTimeline.Apply` により StateMachine を差し替え、`StateMachineFromTimeline` + L10n | フック 1 行 (StateMachine 生成直後)。フラグは Custom 側 ConditionalWeakTable で保持可 |
| BossModule/BossModuleConfig.cs | 32/1 | 未変更 | 設定 7 項目 + タイムライン再読込ボタン + L10n | 不要(移設済): CustomConfig。ボタンは CustomConfig.DrawCustom へ (未実装) |
| BossModule/BossModuleManager.cs | 8/3 | **upstreamも変更** | `followConfigChanges` 引数 (リプレイ解析用 BMM が設定変更を購読しない) | PR候補 (スレッド安全性) |
| BossModule/BossModuleRegistry.cs | 2/0 | 未変更 | PlanLevel 未設定時に外部タイムラインから補完 | フック 2 行 または破棄 |
| BossModule/RaidCooldowns.cs | 82/0 | 未変更 | バフ/デバフ窓のスナップショット API (純追加) | Custom化可: `[UnsafeAccessor(Field)]` で `_damageCooldowns` を読む拡張メソッド |
| BossModule/StateMachine.cs | 4/4 | 未変更 | L10n | L10n |
| BossModule/ZoneModule.cs | 1/1 | 未変更 | L10n | L10n |
| Components/StackSpread.cs | 3/1 | 未変更 | `AddZonesWithoutAllies` (ペット等が対象のときもゾーン生成) | PR候補 |
| Config/AboutTab.cs | 10/10 | 未変更 | L10n | L10n |
| Config/ConfigConverter.cs | 1/1 | 未変更 | v9 プラン変換先パス BossMod → BossModReborn | PR候補 |
| Config/ConfigUI.cs | 45/15 | 未変更 | Language タブ追加、Loc.Current 初期化、検索の訳語対応 + L10n | 言語設定は CustomConfig の 1 項目にすれば Language タブ不要。Loc.Current 初期化は Plugin フックで。残りは L10n |
| Config/ModuleViewer.cs | 65/56 | **upstreamも変更** | 表示名を日本語シートから取得 + L10n | L10n (衝突しやすいので優先度低) |
| Config/PartyRolesConfig.cs | 10/10 | 未変更 | L10n | L10n |
| Data/ActionID.cs | 2/1 | **upstreamも変更** | IEquatable 実装 | 破棄 (upstream が同じ変更を取り込み済み) |
| Data/ActionSpeed.cs | 16/1 | 未変更 | ParamGrow のレベル別キャッシュ | PR候補 (性能) |
| Data/ClientState.cs | 1/1 | **upstreamも変更** | `Stats.Equals` が `Combo` と比較していたバグ | PR候補 (upstream に未修正で残っている) |
| Debug/DebugAddon.cs | 1/1 | 未変更 | デバッグ表示のアドレス | 破棄推奨 / PR候補 |
| Debug/DebugParty.cs | 4/0 | 未変更 | null ガード | PR候補 |
| Framework/ActionManagerEx.cs | 87/6 | 未変更 | ①`static Instance` ②AutoQueue を LocalRotationAI に差し替え ③視線ブロック ④キャスト終了後の移動ブロック解除フォールバック ⑤Collector への通知 ⑥null プレイヤーガード | ①フック 1 行 ②フック 1 行 (`AutoQueue = CustomHooks.SelectAutoQueue(baseline, ...)`) ③PR候補 ④PR候補 (バグ修正) ⑤Custom化可: 既存の `ActionEffectReceived` / `ActionRequestExecuted` イベントを購読 (自動実行判定だけ要検討) ⑥PR候補 |
| Framework/IPCProvider.cs | 47/5 | 未変更 | External AOE/Hint IPC、`AI.SetEnabled`、AutoDuty プリセット保存時の既存設定維持、数値パースの InvariantCulture 化 | IPC 追加は Custom化可 (Custom 側で独自に IPC 登録)。AutoDuty 維持はフック 2 行。InvariantCulture は PR候補 |
| Framework/MovementOverride.cs | 19/0 | 未変更 | 「TEMP DIAG (not for commit)」の診断ログ | 破棄 |
| Framework/Plugin.cs | 64/2 | 未変更 | Splatoon / タイムライン / 外部ヒント / Disengage / akechi IPC ブリッジの生成・Update・Dispose、移動エスケープキー押下を isMoving に含める | フック 3 箇所: ctor 末尾で `CustomPlugin` 生成、Update で hints 構築後に `custom.Update()`、Dispose。isMoving の扱いは引数 1 箇所の変更 |
| Framework/Utils.cs | 2/1 | **upstreamも変更** | MaxAll を params span 化 | 破棄推奨 (回し側で必要なら Custom にヘルパ) |
| Framework/WorldStateGameSync.cs | 0/1 | **upstreamも変更** | 未使用変数削除のみ | 破棄 |
| Pathfinding/Map.cs | 5/2 | 未変更 | CornerSqueeze フラグ、テレポーター数指定 | PathfindTarget 改修とセット (PR候補) |
| Pathfinding/NavigationDecision.cs | 120/18 | **upstreamも変更** | `BuildPathToTarget`、ワーカースレッド用スナップショット、バッファ再利用 | PR候補 (大きい)。取り込まれない場合はフック群として維持 (衝突リスク高) |
| Pathfinding/ObstacleMaps/maplist.json | 13/0 | 未変更 | 606.217.2 のエントリ | 破棄 (upstream/main に同エントリあり) |
| Pathfinding/ThetaStar.cs | 92/4 | 未変更 | 単一目的地向け `StartToGoal` | PR候補 (NavigationDecision とセット) |
| QuestBattle/Dawntrail/MSQ/TheProtectorAndTheDestroyer.cs | 1/1 | 未変更 | null/targetable チェック | PR候補 |
| QuestBattle/QuestBattle.cs | 6/0 | 未変更 | 目標切替時に古い経路を捨てる | PR候補 |
| Replay/ReplayBuilder.cs | 12/11 | 未変更 | BMM の設定購読停止、InstanceID 再利用時のアクタ初期化修正 | PR候補 |
| Replay/ReplayManagementWindow.cs | 33/4 | **upstreamも変更** | `RecordingFinished` イベント、`LogDirectory`、*.log のみ削除、削除失敗耐性 | フック: イベント + プロパティ (自動タイムライン抽出が使う)。削除の頑健化は PR候補 |
| Replay/ReplayParserLog.cs | 1/1 | **upstreamも変更** | ActiveFate のバージョン判定 27→28 | PR候補 (要検証) |
| Replay/ReplayRecorder.cs | 1/1 | **upstreamも変更** | Brotli 圧縮レベル Optimal→Fastest | 破棄推奨 (好み) |
| Timeline/ColumnPlannerTrackStrategy.cs | 1/1 | 未変更 | L10n | L10n |
| Timeline/CooldownPlannerColumns.cs | 2/1 | 未変更 | 列挙中にコレクションを変更するバグ修正 | PR候補 |
| Util/BitmapPathfindExtensions.cs | 48/4 | 未変更 | `HasObstacleMapMovementLineOfSight` ほか | PathfindTarget 改修とセット (PR候補) |
| Util/Intersect.cs | 12/3 | 未変更 | ドーナツ扇と円の交差で内径を無視していたバグ | PR候補 |
| Util/ShapeDistance/GenericShapes.cs | 1/1 | 未変更 | 反転扇の判定バグ | PR候補 |
| Util/ShapeDistance/KnockbackInCircle.cs | 1/1 | **upstreamも変更** | `&&` → `\|\|` | PR候補 (要検証) |
| Util/ShapeDistance/Pull.cs | 1/1 | 未変更 | 座標の基準点バグ | PR候補 |
| Util/UICombo.cs | 4/4 | 未変更 | L10n | L10n |
| Util/UITabs.cs | 2/3 | 未変更 | L10n (タブ ID を英語名で固定) | L10n |

### フックを最小化した場合の upstream 編集見込み

上の表で「PR候補」「破棄」を除き、Custom化できるものを Custom へ移すと、恒常的に維持する upstream パッチは次の程度に収まる見込み:

1. `Plugin.cs`: CustomPlugin の生成 / Update / Dispose (3 箇所) + isMoving 引数
2. `ActionManagerEx.cs`: Instance 設定、AutoQueue 選択の差し込み (2 行)
3. `AIHintsBuilder.cs`: 1 行
4. `BossModule.cs`: 1 行 (外部タイムラインを使う場合のみ)
5. `AIHints.cs` + `AIBehaviour.cs` + `NormalMovement.cs` + Pathfinding 一式: PathfindTarget (ディープダンジョン自動攻略を使う場合のみ。PR が通れば不要)
6. `ReplayManagementWindow.cs`: RecordingFinished (自動タイムライン抽出を使う場合のみ)
7. L10n を続ける場合は `Strategy.cs` の UIName 2 行 (回し UI のみ日本語化)

## 4. 日本語化 (L10n) の扱い

`Loc.Tr(...)` 包みが 15 ファイル以上の upstream UI コードに散らばっており、upstream が UI 文字列を触るたびに衝突する。さらに `Strategy.cs` / `StrategyRenderer.cs` では英語文言を日本語に **直接書き換えて** いる。提案:

- 直書きはやめ、英語のまま辞書 (`LocalizationJapanese*.cs`、Custom に移設済み) で訳す。
- upstream 側の差し込みは回し UI の `StrategyConfig.UIName` / `StrategyOption.UIName` (2 行) に限定し、設定画面など他の UI は英語のままにする。全面日本語化を続けるなら、同期のたびに当て直す「L10n パッチ」として別管理する。
- 旧 fork で internal 名を日本語にしていた時期のプリセット/プラン互換 (`StrategyConfigLookup`) は、保存データを一度だけ変換するスクリプトで置き換えれば upstream 側の差分が不要になる。

## 5. Custom/_pending/needs-upstream-hook に退避したファイル

ビルドを通すため、upstream フックが無いとコンパイルできないファイルを退避した。依存の連鎖も含む。

| ファイル | 行数 | 足りないもの |
|---|---:|---|
| Autorotation/MechanicHints.cs | 194 | `PlanExecution.OverdueGraceSeconds`, `AIHints.Disengage` |
| Autorotation/WindDown.cs | 204 | `MechanicForecast` (MechanicHints.cs 内) |
| BossModule/DisengageForecast.cs | 233 | `AIHints.Disengage`, `AIHints.FightRemaining` |
| BossModule/FightPriorBuilder.cs | 70 | PullSummary (連鎖) |
| BossModule/FightPriorStore.cs | 115 | TimelineStore / ReplaySummaryCache (連鎖) |
| Modules/Global/DeepDungeon/LiveMapData.cs | 372 | AutoClear / FloorPathfind 側の partial メンバ (RoomNavigation, FindReportedPlayerRoom, HasConnection 等。改変済み DeepDungeon ファイル群に定義) |
| Timeline/External/ExternalTimelineHints.cs | 1,147 | `BossModule.StateMachineFromTimeline` |
| Timeline/External/ExternalPlannerTimeline.cs | 215 | ExternalTimelineHints / TimelineStore (連鎖) |
| Timeline/External/TimelineStore.cs | 252 | ExternalTimelineHints (連鎖) |
| Timeline/External/ReplayTimelineExtractor.cs | 491 | ExternalTimelineHints |
| Timeline/External/AutoTimelineExtractor.cs | 341 | ReplayTimelineExtractor (連鎖) |
| Timeline/External/AutoTimelineGate.cs | 257 | 連鎖 |
| Timeline/External/PullSummary.cs | 51 | 連鎖 |
| Timeline/External/ReplaySummaryCache.cs | 72 | 連鎖 |

`superseded-by-upstream/AtomosPiece.cs` は upstream が `Modules/Global/CrucibleOfTheUnbroken/05SecondMasterBoard/AtomosPiece.cs` として同等のモジュールを追加したため退避 (同名型の重複定義になる)。差分があれば upstream 版へ PR するのが筋。

## 6. ローカルオフセット上書きについて

現時点で上書きすべきオフセットは無い (§1)。将来、upstream / FFXIVClientStructs の更新を待たずに先行修正したくなった場合の案:

- BMR が独自に持つアドレス解決は `ClientStructsEx.cs` 等の少数箇所に限られる。そこに「`Custom/offsets.json` (パッチ番号 → 名前 → オフセット/シグネチャ) があればその値を優先する」読み込み口を 1 箇所だけ設ける。
- ゲームバージョン文字列をキーにし、upstream 側が更新されたら上書きを自動的に無視する (古い上書きが新しい正しい値を潰さないように)。
- FFXIVClientStructs 本体 (Dalamud 同梱 dll) のオフセットはプラグインからは差し替えられない。ここが原因の破損は Dalamud / ClientStructs の更新待ちになる。

必要になった時点で実装する (今は対象が無いので作っていない)。

## 7. (a) 回しモジュール (upstream ファイルを改変したもの)

xan / akechi / veyn の回しは upstream にも存在するファイルを直接改変している (29 本)。分岐点以降 upstream はこれらをほとんど触っていない (`xan/AI/Healer.cs` と `xan/Casters/RDM.cs` のみ、どちらも F: 側未改変) ので現状は衝突しないが、upstream 作者が手を入れた瞬間に衝突する。

隔離方針 (Step 2 以降):

- 改変版は **別名のクラスとして Custom に複製** する (例: `BossMod.Autorotation.xan.MNK` → `BossMod.Custom.Rotation.MNK`)。SourceGen が別モジュールとして登録するので、upstream 版と併存し、プリセットでどちらを使うか選べる。
- 依存する `Basexan.cs` の改変も Custom 側の基底クラス (例: `CustomBase<A, T>`) に複製する。
- フレームワーク側 API への依存 (PlanExecution 追加 API、RaidCooldowns スナップショット、AIHints.FightRemaining/Disengage、ActionManagerEx.Instance) は §3 の Custom化 / フックで提供する。
- 既存プリセットはモジュール型名で紐づいているため、移行時にプリセットの型名を書き換える変換が必要。

## 8. (d) ボスモジュールの修正 (51 本)

`BossMod/Modules/**` の変更は個別のギミック修正・AI ヒント改善。これらは回しとは無関係で、upstream にとっても有益なものが多い。

- 基本方針: **upstream へ PR**。取り込まれれば以後は同期だけで済む。
- PR しない/通らないものは、同期後に当て直すパッチとして `docs/custom/patches/` 等に置く運用 (今回は未作成)。
- **upstreamも変更** の 10 本は upstream 側の修正と重複・衝突している可能性があるので、PR 前に upstream/main と突き合わせること。
- DeepDungeon (`Modules/Global/DeepDungeon/*`, `Heavensward/DeepDungeon/*`) は自動攻略機能の改修で、Pathfinding / PathfindTarget フックに依存する。

## 9. きれいに隔離できなかったもの

1. **動作の配線**: Custom にコンパイルされている機能の大半は、呼び出し元が upstream ファイル (Plugin.cs など) なので、§3 のフックを入れるまで動かない。
2. **回しモジュール本体 (a の 29 本) と Basexan**: upstream ファイルを直接改変しているため今回は未移設。Step 2 で別名クラスとして複製する。
3. **L10n**: 15 以上の UI ファイルに散在。§4 の方針で縮小が必要。
4. **Pathfinding / ディープダンジョン**: NavigationDecision は upstream 側も変更しており、フックとして維持すると衝突しやすい。PR が現実的。
5. **設定値の移行**: CustomConfig に移した 8 項目は、旧 fork の設定ファイルに保存された値を引き継がない (既定値に戻る)。
6. **UnsafeAccessor の脆さ**: upstream が対象メンバの名前やシグネチャを変えると、ビルドは通り実行時に失敗する。同期後にスモークテストが必要。
7. **tools/**: ハーネスの csproj は `..\..\BossMod\BossModReborn.csproj` を参照しており、改変済みフレームワーク前提のコードもあるため、このブランチでのビルドは未確認 (sln にも入っていない)。生成物 (`blm_regression/results*`, `mch_regression/fflogs_out`, `mnk_tuning/tmp_probe*`, 計約 600 MB) はコピーせず F: に残した。
8. `.kimi-dotnet-env.sh` (別エージェント用のマシン固有ラッパー) は持ち込んでいない。

## 付録 A. 全ファイル一覧

列: 分類 / 状態 (A=新規, M=変更) / 追加行 / 削除行 / upstream 状態 / パス / rotation-rebuild での置き場所

| 分類 | 状態 | + | - | upstream | パス | 置き場所 |
|---|---|---:|---:|---|---|---|
| a | M | 309 | 28 | 未変更 | `BossMod/Autorotation/Standard/akechi/AkechiTools.cs` | 未適用 (フック表参照) |
| a | M | 207 | 178 | 未変更 | `BossMod/Autorotation/Standard/akechi/DPS/AkechiBLM.cs` | 未適用 (フック表参照) |
| a | M | 109 | 98 | 未変更 | `BossMod/Autorotation/Standard/akechi/DPS/AkechiDRG.cs` | 未適用 (フック表参照) |
| a | M | 660 | 178 | 未変更 | `BossMod/Autorotation/Standard/akechi/DPS/AkechiMCH.cs` | 未適用 (フック表参照) |
| a | M | 37 | 27 | 未変更 | `BossMod/Autorotation/Standard/akechi/Healer/AkechiSCH.cs` | 未適用 (フック表参照) |
| a | M | 55 | 56 | 未変更 | `BossMod/Autorotation/Standard/akechi/Tank/AkechiDRK.cs` | 未適用 (フック表参照) |
| a | M | 3415 | 276 | 未変更 | `BossMod/Autorotation/Standard/akechi/Tank/AkechiGNB.cs` | 未適用 (フック表参照) |
| a | M | 578 | 214 | 未変更 | `BossMod/Autorotation/Standard/akechi/Tank/AkechiPLD.cs` | 未適用 (フック表参照) |
| a | M | 19 | 11 | 未変更 | `BossMod/Autorotation/Standard/veyn/VeynWAR.cs` | 未適用 (フック表参照) |
| a | M | 873 | 34 | 未変更 | `BossMod/Autorotation/Standard/xan/AI/DeepDungeon.cs` | 未適用 (フック表参照) |
| a | M | 1 | 1 | 未変更 | `BossMod/Autorotation/Standard/xan/AI/Tank.cs` | 未適用 (フック表参照) |
| a | M | 4 | 4 | 未変更 | `BossMod/Autorotation/Standard/xan/AI/Variant.cs` | 未適用 (フック表参照) |
| a | M | 136 | 67 | 未変更 | `BossMod/Autorotation/Standard/xan/Basexan.cs` | 未適用 (フック表参照) |
| a | M | 6227 | 327 | 未変更 | `BossMod/Autorotation/Standard/xan/Casters/BLM.cs` | 未適用 (フック表参照) |
| a | M | 385 | 8 | 未変更 | `BossMod/Autorotation/Standard/xan/Melee/BST.cs` | 未適用 (フック表参照) |
| a | M | 82 | 23 | 未変更 | `BossMod/Autorotation/Standard/xan/Melee/DRG.cs` | 未適用 (フック表参照) |
| a | M | 5676 | 640 | 未変更 | `BossMod/Autorotation/Standard/xan/Melee/MNK.cs` | 未適用 (フック表参照) |
| a | M | 2637 | 179 | 未変更 | `BossMod/Autorotation/Standard/xan/Melee/NIN.cs` | 未適用 (フック表参照) |
| a | M | 5725 | 278 | 未変更 | `BossMod/Autorotation/Standard/xan/Melee/RPR.cs` | 未適用 (フック表参照) |
| a | M | 2860 | 226 | 未変更 | `BossMod/Autorotation/Standard/xan/Melee/SAM.cs` | 未適用 (フック表参照) |
| a | M | 2017 | 168 | 未変更 | `BossMod/Autorotation/Standard/xan/Melee/VPR.cs` | 未適用 (フック表参照) |
| a | M | 3175 | 207 | 未変更 | `BossMod/Autorotation/Standard/xan/Ranged/MCH.cs` | 未適用 (フック表参照) |
| a | M | 1 | 1 | 未変更 | `BossMod/Autorotation/Standard/xan/Tanks/DRK.cs` | 未適用 (フック表参照) |
| a | M | 2 | 1 | 未変更 | `BossMod/Autorotation/Standard/xan/Tanks/GNB.cs` | 未適用 (フック表参照) |
| a | M | 1 | 1 | 未変更 | `BossMod/Autorotation/Standard/xan/Tanks/PLD.cs` | 未適用 (フック表参照) |
| a | M | 65 | 5 | 未変更 | `BossMod/Autorotation/Standard/xan/Utility/ThirdEye.cs` | 未適用 (フック表参照) |
| a | M | 1 | 1 | 未変更 | `BossMod/Autorotation/Utility/ClassGNBUtility.cs` | 未適用 (フック表参照) |
| a | M | 18 | 3 | 未変更 | `BossMod/Autorotation/Utility/ClassNINUtility.cs` | 未適用 (フック表参照) |
| a | M | 2 | 2 | 未変更 | `BossMod/Autorotation/Utility/ClassSGEUtility.cs` | 未適用 (フック表参照) |
| a | A | 839 | 0 | — | `BossMod/ActionQueue/FunctionGemmaRotationSupervisor.cs` | Custom/ActionQueue/FunctionGemmaRotationSupervisor.cs |
| a | A | 615 | 0 | — | `BossMod/ActionQueue/LocalRotationAI.cs` | Custom/ActionQueue/LocalRotationAI.cs |
| a | A | 336 | 0 | — | `BossMod/ActionQueue/LocalRotationAICollector.cs` | Custom/ActionQueue/LocalRotationAICollector.cs |
| a | A | 343 | 0 | — | `BossMod/ActionQueue/MchRealtimeValuePlanner.cs` | Custom/ActionQueue/MchRealtimeValuePlanner.cs |
| a | A | 154 | 0 | — | `BossMod/Autorotation/ActionLocks.cs` | Custom/Autorotation/ActionLocks.cs |
| a | A | 194 | 0 | — | `BossMod/Autorotation/MechanicHints.cs` | Custom/_pending/needs-upstream-hook/Autorotation/MechanicHints.cs (未コンパイル) |
| a | A | 84 | 0 | — | `BossMod/Autorotation/Standard/xan/ExternalHintAdapter.cs` | Custom/Autorotation/Standard/xan/ExternalHintAdapter.cs |
| a | A | 104 | 0 | — | `BossMod/Autorotation/Standard/xan/Melee/MNK_Tuning.md` | Custom/Autorotation/Standard/xan/Melee/MNK_Tuning.md |
| a | A | 757 | 0 | — | `BossMod/Autorotation/Standard/xan/Melee/NINBurstPlanner.cs` | Custom/Autorotation/Standard/xan/Melee/NINBurstPlanner.cs |
| a | A | 73 | 0 | — | `BossMod/Autorotation/Standard/xan/Melee/RprPotency.cs` | Custom/Autorotation/Standard/xan/Melee/RprPotency.cs |
| a | A | 47 | 0 | — | `BossMod/Autorotation/Standard/xan/SplatoonHintBridge.cs` | Custom/Autorotation/Standard/xan/SplatoonHintBridge.cs |
| a | A | 146 | 0 | — | `BossMod/Autorotation/Standard/xan/SplatoonHintSource.cs` | Custom/Autorotation/Standard/xan/SplatoonHintSource.cs |
| a | A | 204 | 0 | — | `BossMod/Autorotation/WindDown.cs` | Custom/_pending/needs-upstream-hook/Autorotation/WindDown.cs (未コンパイル) |
| a | A | 79 | 0 | — | `BossMod/Autorotation/WindDownPotency.cs` | Custom/Autorotation/WindDownPotency.cs |
| b | A | 529 | 0 | — | `tools/blm_regression/BlmRegressionRunner.cs` | tools/blm_regression/BlmRegressionRunner.cs |
| b | A | 1523 | 0 | — | `tools/blm_regression/BlmRotationEmulator.cs` | tools/blm_regression/BlmRotationEmulator.cs |
| b | A | 383 | 0 | — | `tools/blm_regression/BlmScenario.cs` | tools/blm_regression/BlmScenario.cs |
| b | A | 672 | 0 | — | `tools/blm_regression/BlmScenarioCatalog.cs` | tools/blm_regression/BlmScenarioCatalog.cs |
| b | A | 84 | 0 | — | `tools/blm_regression/Program.cs` | tools/blm_regression/Program.cs |
| b | A | - | - | — | `tools/blm_regression/results/blm_regression_latest.json` | 未コピー (生成物, F:に残置) |
| b | A | 1 | 0 | — | `tools/blm_regression/results/blm_regression_repros.json` | 未コピー (生成物, F:に残置) |
| b | A | 34742 | 0 | — | `tools/blm_regression/results/blm_regression_summary.md` | 未コピー (生成物, F:に残置) |
| b | A | 940985 | 0 | — | `tools/blm_regression/results_highend/blm_regression_latest.json` | 未コピー (生成物, F:に残置) |
| b | A | 1 | 0 | — | `tools/blm_regression/results_highend/blm_regression_repros.json` | 未コピー (生成物, F:に残置) |
| b | A | 1680 | 0 | — | `tools/blm_regression/results_highend/blm_regression_summary.md` | 未コピー (生成物, F:に残置) |
| b | A | 15 | 0 | — | `tools/blm_regression/tools.blm_regression.csproj` | tools/blm_regression/tools.blm_regression.csproj |
| b | A | 51 | 0 | — | `tools/drg_real_harness/DrgRealHarness.csproj` | tools/drg_real_harness/DrgRealHarness.csproj |
| b | A | 515 | 0 | — | `tools/drg_real_harness/Program.cs` | tools/drg_real_harness/Program.cs |
| b | A | 123 | 0 | — | `tools/drg_regression/DrgModel.cs` | tools/drg_regression/DrgModel.cs |
| b | A | 9 | 0 | — | `tools/drg_regression/DrgRegression.csproj` | tools/drg_regression/DrgRegression.csproj |
| b | A | 477 | 0 | — | `tools/drg_regression/DrgSimulator.cs` | tools/drg_regression/DrgSimulator.cs |
| b | A | 123 | 0 | — | `tools/drg_regression/Program.cs` | tools/drg_regression/Program.cs |
| b | A | 463 | 0 | — | `tools/encounter_timeline/CactbotTriggerCatalog.cs` | tools/encounter_timeline/CactbotTriggerCatalog.cs |
| b | A | 8 | 0 | — | `tools/encounter_timeline/EncounterTimeline.csproj` | tools/encounter_timeline/EncounterTimeline.csproj |
| b | A | 326 | 0 | — | `tools/encounter_timeline/EventTriggerTimelineCatalog.cs` | tools/encounter_timeline/EventTriggerTimelineCatalog.cs |
| b | A | 267 | 0 | — | `tools/encounter_timeline/EventTriggerTimelineSkeletonGenerator.cs` | tools/encounter_timeline/EventTriggerTimelineSkeletonGenerator.cs |
| b | A | 258 | 0 | — | `tools/encounter_timeline/ExternalTimelineManifestGenerator.cs` | tools/encounter_timeline/ExternalTimelineManifestGenerator.cs |
| b | A | 171 | 0 | — | `tools/encounter_timeline/Program.cs` | tools/encounter_timeline/Program.cs |
| b | A | 63 | 0 | — | `tools/encounter_timeline/generated/1002-p1n.cs.txt` | tools/encounter_timeline/generated/1002-p1n.cs.txt |
| b | A | 96 | 0 | — | `tools/encounter_timeline/generated/1003-p1s.cs.txt` | tools/encounter_timeline/generated/1003-p1s.cs.txt |
| b | A | 55 | 0 | — | `tools/encounter_timeline/generated/1004-p2n.cs.txt` | tools/encounter_timeline/generated/1004-p2n.cs.txt |
| b | A | 93 | 0 | — | `tools/encounter_timeline/generated/1005-p2s.cs.txt` | tools/encounter_timeline/generated/1005-p2s.cs.txt |
| b | A | 72 | 0 | — | `tools/encounter_timeline/generated/1006-p3n.cs.txt` | tools/encounter_timeline/generated/1006-p3n.cs.txt |
| b | A | 122 | 0 | — | `tools/encounter_timeline/generated/1007-p3s.cs.txt` | tools/encounter_timeline/generated/1007-p3s.cs.txt |
| b | A | 72 | 0 | — | `tools/encounter_timeline/generated/1008-p4n.cs.txt` | tools/encounter_timeline/generated/1008-p4n.cs.txt |
| b | A | 141 | 0 | — | `tools/encounter_timeline/generated/1009-p4s.cs.txt` | tools/encounter_timeline/generated/1009-p4s.cs.txt |
| b | A | 231 | 0 | — | `tools/encounter_timeline/generated/1035-ultima-un.cs.txt` | tools/encounter_timeline/generated/1035-ultima-un.cs.txt |
| b | A | 54 | 0 | — | `tools/encounter_timeline/generated/1045-ifrit-nm.cs.txt` | tools/encounter_timeline/generated/1045-ifrit-nm.cs.txt |
| b | A | 105 | 0 | — | `tools/encounter_timeline/generated/1046-titan-nm.cs.txt` | tools/encounter_timeline/generated/1046-titan-nm.cs.txt |
| b | A | 80 | 0 | — | `tools/encounter_timeline/generated/1050-alzadaals_legacy.cs.txt` | tools/encounter_timeline/generated/1050-alzadaals_legacy.cs.txt |
| b | A | 357 | 0 | — | `tools/encounter_timeline/generated/1054-aglaia.cs.txt` | tools/encounter_timeline/generated/1054-aglaia.cs.txt |
| b | A | 104 | 0 | — | `tools/encounter_timeline/generated/1064-sohm_al.cs.txt` | tools/encounter_timeline/generated/1064-sohm_al.cs.txt |
| b | A | 153 | 0 | — | `tools/encounter_timeline/generated/1066-the_vault.cs.txt` | tools/encounter_timeline/generated/1066-the_vault.cs.txt |
| b | A | 601 | 0 | — | `tools/encounter_timeline/generated/1069-the_sildihn_subterrane.cs.txt` | tools/encounter_timeline/generated/1069-the_sildihn_subterrane.cs.txt |
| b | A | 83 | 0 | — | `tools/encounter_timeline/generated/1070-the_fell_court_of_troia.cs.txt` | tools/encounter_timeline/generated/1070-the_fell_court_of_troia.cs.txt |
| b | A | 147 | 0 | — | `tools/encounter_timeline/generated/1071-barbariccia.cs.txt` | tools/encounter_timeline/generated/1071-barbariccia.cs.txt |
| b | A | 163 | 0 | — | `tools/encounter_timeline/generated/1072-barbariccia-ex.cs.txt` | tools/encounter_timeline/generated/1072-barbariccia-ex.cs.txt |
| b | A | 157 | 0 | — | `tools/encounter_timeline/generated/1075-another_sildihn_subterrane.cs.txt` | tools/encounter_timeline/generated/1075-another_sildihn_subterrane.cs.txt |
| b | A | 157 | 0 | — | `tools/encounter_timeline/generated/1076-another_sildihn_subterrane-savage.cs.txt` | tools/encounter_timeline/generated/1076-another_sildihn_subterrane-savage.cs.txt |
| b | A | 50 | 0 | — | `tools/encounter_timeline/generated/1081-p5n.cs.txt` | tools/encounter_timeline/generated/1081-p5n.cs.txt |
| b | A | 99 | 0 | — | `tools/encounter_timeline/generated/1082-p5s.cs.txt` | tools/encounter_timeline/generated/1082-p5s.cs.txt |
| b | A | 75 | 0 | — | `tools/encounter_timeline/generated/1083-p6n.cs.txt` | tools/encounter_timeline/generated/1083-p6n.cs.txt |
| b | A | 96 | 0 | — | `tools/encounter_timeline/generated/1084-p6s.cs.txt` | tools/encounter_timeline/generated/1084-p6s.cs.txt |
| b | A | 105 | 0 | — | `tools/encounter_timeline/generated/1085-p7n.cs.txt` | tools/encounter_timeline/generated/1085-p7n.cs.txt |
| b | A | 91 | 0 | — | `tools/encounter_timeline/generated/1086-p7s.cs.txt` | tools/encounter_timeline/generated/1086-p7s.cs.txt |
| b | A | 177 | 0 | — | `tools/encounter_timeline/generated/1087-p8n.cs.txt` | tools/encounter_timeline/generated/1087-p8n.cs.txt |
| b | A | 279 | 0 | — | `tools/encounter_timeline/generated/1088-p8s.cs.txt` | tools/encounter_timeline/generated/1088-p8s.cs.txt |
| b | A | 136 | 0 | — | `tools/encounter_timeline/generated/1090-sephirot-un.cs.txt` | tools/encounter_timeline/generated/1090-sephirot-un.cs.txt |
| b | A | 81 | 0 | — | `tools/encounter_timeline/generated/1095-rubicante.cs.txt` | tools/encounter_timeline/generated/1095-rubicante.cs.txt |
| b | A | 100 | 0 | — | `tools/encounter_timeline/generated/1096-rubicante-ex.cs.txt` | tools/encounter_timeline/generated/1096-rubicante-ex.cs.txt |
| b | A | 121 | 0 | — | `tools/encounter_timeline/generated/1097-lapis_manalis.cs.txt` | tools/encounter_timeline/generated/1097-lapis_manalis.cs.txt |
| b | A | 352 | 0 | — | `tools/encounter_timeline/generated/1110-aetherochemical_research_facility.cs.txt` | tools/encounter_timeline/generated/1110-aetherochemical_research_facility.cs.txt |
| b | A | 103 | 0 | — | `tools/encounter_timeline/generated/1113-xelphatol.cs.txt` | tools/encounter_timeline/generated/1113-xelphatol.cs.txt |
| b | A | 83 | 0 | — | `tools/encounter_timeline/generated/1114-baelsars_wall.cs.txt` | tools/encounter_timeline/generated/1114-baelsars_wall.cs.txt |
| b | A | 268 | 0 | — | `tools/encounter_timeline/generated/1118-euphrosyne.cs.txt` | tools/encounter_timeline/generated/1118-euphrosyne.cs.txt |
| b | A | 92 | 0 | — | `tools/encounter_timeline/generated/1121-sophia-un.cs.txt` | tools/encounter_timeline/generated/1121-sophia-un.cs.txt |
| b | A | 242 | 0 | — | `tools/encounter_timeline/generated/1122-the_omega_protocol.cs.txt` | tools/encounter_timeline/generated/1122-the_omega_protocol.cs.txt |
| b | A | 95 | 0 | — | `tools/encounter_timeline/generated/1126-aetherfont.cs.txt` | tools/encounter_timeline/generated/1126-aetherfont.cs.txt |
| b | A | 74 | 0 | — | `tools/encounter_timeline/generated/1136-asura.cs.txt` | tools/encounter_timeline/generated/1136-asura.cs.txt |
| b | A | 523 | 0 | — | `tools/encounter_timeline/generated/1137-mount_rokkon.cs.txt` | tools/encounter_timeline/generated/1137-mount_rokkon.cs.txt |
| b | A | 76 | 0 | — | `tools/encounter_timeline/generated/1140-golbez.cs.txt` | tools/encounter_timeline/generated/1140-golbez.cs.txt |
| b | A | 219 | 0 | — | `tools/encounter_timeline/generated/1141-golbez-ex.cs.txt` | tools/encounter_timeline/generated/1141-golbez-ex.cs.txt |
| b | A | 82 | 0 | — | `tools/encounter_timeline/generated/1142-sirensong_sea.cs.txt` | tools/encounter_timeline/generated/1142-sirensong_sea.cs.txt |
| b | A | 138 | 0 | — | `tools/encounter_timeline/generated/1143-bardams_mettle.cs.txt` | tools/encounter_timeline/generated/1143-bardams_mettle.cs.txt |
| b | A | 133 | 0 | — | `tools/encounter_timeline/generated/1144-doma_castle.cs.txt` | tools/encounter_timeline/generated/1144-doma_castle.cs.txt |
| b | A | 140 | 0 | — | `tools/encounter_timeline/generated/1145-castrum_abania.cs.txt` | tools/encounter_timeline/generated/1145-castrum_abania.cs.txt |
| b | A | 130 | 0 | — | `tools/encounter_timeline/generated/1146-ala_mhigo.cs.txt` | tools/encounter_timeline/generated/1146-ala_mhigo.cs.txt |
| b | A | 80 | 0 | — | `tools/encounter_timeline/generated/1147-p9n.cs.txt` | tools/encounter_timeline/generated/1147-p9n.cs.txt |
| b | A | 88 | 0 | — | `tools/encounter_timeline/generated/1148-p9s.cs.txt` | tools/encounter_timeline/generated/1148-p9s.cs.txt |
| b | A | 53 | 0 | — | `tools/encounter_timeline/generated/1149-p10n.cs.txt` | tools/encounter_timeline/generated/1149-p10n.cs.txt |
| b | A | 116 | 0 | — | `tools/encounter_timeline/generated/1150-p10s.cs.txt` | tools/encounter_timeline/generated/1150-p10s.cs.txt |
| b | A | 112 | 0 | — | `tools/encounter_timeline/generated/1151-p11n.cs.txt` | tools/encounter_timeline/generated/1151-p11n.cs.txt |
| b | A | 90 | 0 | — | `tools/encounter_timeline/generated/1152-p11s.cs.txt` | tools/encounter_timeline/generated/1152-p11s.cs.txt |
| b | A | 93 | 0 | — | `tools/encounter_timeline/generated/1153-p12n.cs.txt` | tools/encounter_timeline/generated/1153-p12n.cs.txt |
| b | A | 175 | 0 | — | `tools/encounter_timeline/generated/1154-p12s.cs.txt` | tools/encounter_timeline/generated/1154-p12s.cs.txt |
| b | A | 190 | 0 | — | `tools/encounter_timeline/generated/1155-another_mount_rokkon.cs.txt` | tools/encounter_timeline/generated/1155-another_mount_rokkon.cs.txt |
| b | A | 190 | 0 | — | `tools/encounter_timeline/generated/1156-another_mount_rokkon-savage.cs.txt` | tools/encounter_timeline/generated/1156-another_mount_rokkon-savage.cs.txt |
| b | A | 112 | 0 | — | `tools/encounter_timeline/generated/1157-zurvan-un.cs.txt` | tools/encounter_timeline/generated/1157-zurvan-un.cs.txt |
| b | A | 119 | 0 | — | `tools/encounter_timeline/generated/1164-the_lunar_subterrane.cs.txt` | tools/encounter_timeline/generated/1164-the_lunar_subterrane.cs.txt |
| b | A | 89 | 0 | — | `tools/encounter_timeline/generated/1167-ihuykatumu.cs.txt` | tools/encounter_timeline/generated/1167-ihuykatumu.cs.txt |
| b | A | 72 | 0 | — | `tools/encounter_timeline/generated/1168-zeromus.cs.txt` | tools/encounter_timeline/generated/1168-zeromus.cs.txt |
| b | A | 93 | 0 | — | `tools/encounter_timeline/generated/1169-zeromus-ex.cs.txt` | tools/encounter_timeline/generated/1169-zeromus-ex.cs.txt |
| b | A | 103 | 0 | — | `tools/encounter_timeline/generated/1172-drowned_city_of_skalla.cs.txt` | tools/encounter_timeline/generated/1172-drowned_city_of_skalla.cs.txt |
| b | A | 82 | 0 | — | `tools/encounter_timeline/generated/1173-the_burn.cs.txt` | tools/encounter_timeline/generated/1173-the_burn.cs.txt |
| b | A | 143 | 0 | — | `tools/encounter_timeline/generated/1174-ghimlyt_dark.cs.txt` | tools/encounter_timeline/generated/1174-ghimlyt_dark.cs.txt |
| b | A | 156 | 0 | — | `tools/encounter_timeline/generated/1175-thordan-un.cs.txt` | tools/encounter_timeline/generated/1175-thordan-un.cs.txt |
| b | A | 519 | 0 | — | `tools/encounter_timeline/generated/1176-aloalo_island.cs.txt` | tools/encounter_timeline/generated/1176-aloalo_island.cs.txt |
| b | A | 214 | 0 | — | `tools/encounter_timeline/generated/1178-thaleia.cs.txt` | tools/encounter_timeline/generated/1178-thaleia.cs.txt |
| b | A | 187 | 0 | — | `tools/encounter_timeline/generated/1179-another_aloalo_island.cs.txt` | tools/encounter_timeline/generated/1179-another_aloalo_island.cs.txt |
| b | A | 184 | 0 | — | `tools/encounter_timeline/generated/1180-another_aloalo_island-savage.cs.txt` | tools/encounter_timeline/generated/1180-another_aloalo_island-savage.cs.txt |
| b | A | 100 | 0 | — | `tools/encounter_timeline/generated/1193-worqor-zormor.cs.txt` | tools/encounter_timeline/generated/1193-worqor-zormor.cs.txt |
| b | A | 85 | 0 | — | `tools/encounter_timeline/generated/1194-skydeep-cenote.cs.txt` | tools/encounter_timeline/generated/1194-skydeep-cenote.cs.txt |
| b | A | 123 | 0 | — | `tools/encounter_timeline/generated/1195-valigarmanda.cs.txt` | tools/encounter_timeline/generated/1195-valigarmanda.cs.txt |
| b | A | 99 | 0 | — | `tools/encounter_timeline/generated/1196-valigarmanda-ex.cs.txt` | tools/encounter_timeline/generated/1196-valigarmanda-ex.cs.txt |
| b | A | 83 | 0 | — | `tools/encounter_timeline/generated/1198-vanguard.cs.txt` | tools/encounter_timeline/generated/1198-vanguard.cs.txt |
| b | A | 89 | 0 | — | `tools/encounter_timeline/generated/1199-alexandria.cs.txt` | tools/encounter_timeline/generated/1199-alexandria.cs.txt |
| b | A | 90 | 0 | — | `tools/encounter_timeline/generated/1200-zoraal-ja.cs.txt` | tools/encounter_timeline/generated/1200-zoraal-ja.cs.txt |
| b | A | 94 | 0 | — | `tools/encounter_timeline/generated/1201-zoraal-ja-ex.cs.txt` | tools/encounter_timeline/generated/1201-zoraal-ja-ex.cs.txt |
| b | A | 125 | 0 | — | `tools/encounter_timeline/generated/1202-queen-eternal.cs.txt` | tools/encounter_timeline/generated/1202-queen-eternal.cs.txt |
| b | A | 78 | 0 | — | `tools/encounter_timeline/generated/1204-strayborough-deadwalk.cs.txt` | tools/encounter_timeline/generated/1204-strayborough-deadwalk.cs.txt |
| b | A | 94 | 0 | — | `tools/encounter_timeline/generated/1208-origenics.cs.txt` | tools/encounter_timeline/generated/1208-origenics.cs.txt |
| b | A | 102 | 0 | — | `tools/encounter_timeline/generated/1225-r1n.cs.txt` | tools/encounter_timeline/generated/1225-r1n.cs.txt |
| b | A | 156 | 0 | — | `tools/encounter_timeline/generated/1226-r1s.cs.txt` | tools/encounter_timeline/generated/1226-r1s.cs.txt |
| b | A | 61 | 0 | — | `tools/encounter_timeline/generated/1227-r2n.cs.txt` | tools/encounter_timeline/generated/1227-r2n.cs.txt |
| b | A | 113 | 0 | — | `tools/encounter_timeline/generated/1228-r2s.cs.txt` | tools/encounter_timeline/generated/1228-r2s.cs.txt |
| b | A | 80 | 0 | — | `tools/encounter_timeline/generated/1229-r3n.cs.txt` | tools/encounter_timeline/generated/1229-r3n.cs.txt |
| b | A | 111 | 0 | — | `tools/encounter_timeline/generated/1230-r3s.cs.txt` | tools/encounter_timeline/generated/1230-r3s.cs.txt |
| b | A | 94 | 0 | — | `tools/encounter_timeline/generated/1231-r4n.cs.txt` | tools/encounter_timeline/generated/1231-r4n.cs.txt |
| b | A | 117 | 0 | — | `tools/encounter_timeline/generated/1232-r4s.cs.txt` | tools/encounter_timeline/generated/1232-r4s.cs.txt |
| b | A | 237 | 0 | — | `tools/encounter_timeline/generated/1238-futures_rewritten.cs.txt` | tools/encounter_timeline/generated/1238-futures_rewritten.cs.txt |
| b | A | 130 | 0 | — | `tools/encounter_timeline/generated/1239-byakko-un.cs.txt` | tools/encounter_timeline/generated/1239-byakko-un.cs.txt |
| b | A | 158 | 0 | — | `tools/encounter_timeline/generated/1241-cloud_of_darkness_chaotic.cs.txt` | tools/encounter_timeline/generated/1241-cloud_of_darkness_chaotic.cs.txt |
| b | A | 93 | 0 | — | `tools/encounter_timeline/generated/1242-yuweyawata.cs.txt` | tools/encounter_timeline/generated/1242-yuweyawata.cs.txt |
| b | A | 101 | 0 | — | `tools/encounter_timeline/generated/1243-queen-eternal-ex.cs.txt` | tools/encounter_timeline/generated/1243-queen-eternal-ex.cs.txt |
| b | A | 350 | 0 | — | `tools/encounter_timeline/generated/1248-jeuno-first-walk.cs.txt` | tools/encounter_timeline/generated/1248-jeuno-first-walk.cs.txt |
| b | A | 1549 | 0 | — | `tools/encounter_timeline/generated/1252-occult_crescent_south_horn.cs.txt` | tools/encounter_timeline/generated/1252-occult_crescent_south_horn.cs.txt |
| b | A | 140 | 0 | — | `tools/encounter_timeline/generated/1256-r5n.cs.txt` | tools/encounter_timeline/generated/1256-r5n.cs.txt |
| b | A | 141 | 0 | — | `tools/encounter_timeline/generated/1257-r5s.cs.txt` | tools/encounter_timeline/generated/1257-r5s.cs.txt |
| b | A | 76 | 0 | — | `tools/encounter_timeline/generated/1258-r6n.cs.txt` | tools/encounter_timeline/generated/1258-r6n.cs.txt |
| b | A | 107 | 0 | — | `tools/encounter_timeline/generated/1259-r6s.cs.txt` | tools/encounter_timeline/generated/1259-r6s.cs.txt |
| b | A | 9 | 0 | — | `tools/encounter_timeline/generated/1260-r7n.cs.txt` | tools/encounter_timeline/generated/1260-r7n.cs.txt |
| b | A | 134 | 0 | — | `tools/encounter_timeline/generated/1261-r7s.cs.txt` | tools/encounter_timeline/generated/1261-r7s.cs.txt |
| b | A | 125 | 0 | — | `tools/encounter_timeline/generated/1262-r8n.cs.txt` | tools/encounter_timeline/generated/1262-r8n.cs.txt |
| b | A | 210 | 0 | — | `tools/encounter_timeline/generated/1263-r8s.cs.txt` | tools/encounter_timeline/generated/1263-r8s.cs.txt |
| b | A | 109 | 0 | — | `tools/encounter_timeline/generated/1266-the_underkeep.cs.txt` | tools/encounter_timeline/generated/1266-the_underkeep.cs.txt |
| b | A | 9 | 0 | — | `tools/encounter_timeline/generated/1270-zelenia.cs.txt` | tools/encounter_timeline/generated/1270-zelenia.cs.txt |
| b | A | 126 | 0 | — | `tools/encounter_timeline/generated/1271-zelenia-ex.cs.txt` | tools/encounter_timeline/generated/1271-zelenia-ex.cs.txt |
| b | A | 98 | 0 | — | `tools/encounter_timeline/generated/1272-suzaku-un.cs.txt` | tools/encounter_timeline/generated/1272-suzaku-un.cs.txt |
| b | A | 83 | 0 | — | `tools/encounter_timeline/generated/1292-meso-terminal.cs.txt` | tools/encounter_timeline/generated/1292-meso-terminal.cs.txt |
| b | A | 160 | 0 | — | `tools/encounter_timeline/generated/1296-necron-ex.cs.txt` | tools/encounter_timeline/generated/1296-necron-ex.cs.txt |
| b | A | 81 | 0 | — | `tools/encounter_timeline/generated/1300-arkveld.cs.txt` | tools/encounter_timeline/generated/1300-arkveld.cs.txt |
| b | A | 111 | 0 | — | `tools/encounter_timeline/generated/1302-seiryu-un.cs.txt` | tools/encounter_timeline/generated/1302-seiryu-un.cs.txt |
| b | A | 109 | 0 | — | `tools/encounter_timeline/generated/1306-arkveld-ex.cs.txt` | tools/encounter_timeline/generated/1306-arkveld-ex.cs.txt |
| b | A | 84 | 0 | — | `tools/encounter_timeline/generated/1307-doomtrain.cs.txt` | tools/encounter_timeline/generated/1307-doomtrain.cs.txt |
| b | A | 118 | 0 | — | `tools/encounter_timeline/generated/1308-doomtrain-ex.cs.txt` | tools/encounter_timeline/generated/1308-doomtrain-ex.cs.txt |
| b | A | 155 | 0 | — | `tools/encounter_timeline/generated/1311-the_final_verse_quantum.cs.txt` | tools/encounter_timeline/generated/1311-the_final_verse_quantum.cs.txt |
| b | A | 99 | 0 | — | `tools/encounter_timeline/generated/1314-mistwake.cs.txt` | tools/encounter_timeline/generated/1314-mistwake.cs.txt |
| b | A | 150 | 0 | — | `tools/encounter_timeline/generated/1318-tsukuyomi-un.cs.txt` | tools/encounter_timeline/generated/1318-tsukuyomi-un.cs.txt |
| b | A | 102 | 0 | — | `tools/encounter_timeline/generated/1320-r9n.cs.txt` | tools/encounter_timeline/generated/1320-r9n.cs.txt |
| b | A | 157 | 0 | — | `tools/encounter_timeline/generated/1321-r9s.cs.txt` | tools/encounter_timeline/generated/1321-r9s.cs.txt |
| b | A | 144 | 0 | — | `tools/encounter_timeline/generated/1322-r10n.cs.txt` | tools/encounter_timeline/generated/1322-r10n.cs.txt |
| b | A | 155 | 0 | — | `tools/encounter_timeline/generated/1323-r10s.cs.txt` | tools/encounter_timeline/generated/1323-r10s.cs.txt |
| b | A | 108 | 0 | — | `tools/encounter_timeline/generated/1324-r11n.cs.txt` | tools/encounter_timeline/generated/1324-r11n.cs.txt |
| b | A | 308 | 0 | — | `tools/encounter_timeline/generated/1325-r11s.cs.txt` | tools/encounter_timeline/generated/1325-r11s.cs.txt |
| b | A | 76 | 0 | — | `tools/encounter_timeline/generated/1326-r12n.cs.txt` | tools/encounter_timeline/generated/1326-r12n.cs.txt |
| b | A | 350 | 0 | — | `tools/encounter_timeline/generated/1327-r12s.cs.txt` | tools/encounter_timeline/generated/1327-r12s.cs.txt |
| b | A | 25 | 0 | — | `tools/encounter_timeline/generated/134-test.cs.txt` | tools/encounter_timeline/generated/134-test.cs.txt |
| b | A | 125 | 0 | — | `tools/encounter_timeline/generated/1361-enuo.cs.txt` | tools/encounter_timeline/generated/1361-enuo.cs.txt |
| b | A | 93 | 0 | — | `tools/encounter_timeline/generated/1362-enuo-ex.cs.txt` | tools/encounter_timeline/generated/1362-enuo-ex.cs.txt |
| b | A | 366 | 0 | — | `tools/encounter_timeline/generated/1363-dancing_mad.cs.txt` | tools/encounter_timeline/generated/1363-dancing_mad.cs.txt |
| b | A | 172 | 0 | — | `tools/encounter_timeline/generated/1367-shisui_of_the_violet_tides.cs.txt` | tools/encounter_timeline/generated/1367-shisui_of_the_violet_tides.cs.txt |
| b | A | 260 | 0 | — | `tools/encounter_timeline/generated/1368-windurst-third-walk.cs.txt` | tools/encounter_timeline/generated/1368-windurst-third-walk.cs.txt |
| b | A | 107 | 0 | — | `tools/encounter_timeline/generated/1372-shinryu-un.cs.txt` | tools/encounter_timeline/generated/1372-shinryu-un.cs.txt |
| b | A | 56 | 0 | — | `tools/encounter_timeline/generated/193-t10.cs.txt` | tools/encounter_timeline/generated/193-t10.cs.txt |
| b | A | 149 | 0 | — | `tools/encounter_timeline/generated/194-t11.cs.txt` | tools/encounter_timeline/generated/194-t11.cs.txt |
| b | A | 113 | 0 | — | `tools/encounter_timeline/generated/195-t12.cs.txt` | tools/encounter_timeline/generated/195-t12.cs.txt |
| b | A | 206 | 0 | — | `tools/encounter_timeline/generated/196-t13.cs.txt` | tools/encounter_timeline/generated/196-t13.cs.txt |
| b | A | 30 | 0 | — | `tools/encounter_timeline/generated/244-t4.cs.txt` | tools/encounter_timeline/generated/244-t4.cs.txt |
| b | A | 84 | 0 | — | `tools/encounter_timeline/generated/245-t5.cs.txt` | tools/encounter_timeline/generated/245-t5.cs.txt |
| b | A | 161 | 0 | — | `tools/encounter_timeline/generated/293-titan-hm.cs.txt` | tools/encounter_timeline/generated/293-titan-hm.cs.txt |
| b | A | 151 | 0 | — | `tools/encounter_timeline/generated/296-titan-ex.cs.txt` | tools/encounter_timeline/generated/296-titan-ex.cs.txt |
| b | A | 234 | 0 | — | `tools/encounter_timeline/generated/348-ultima-ex.cs.txt` | tools/encounter_timeline/generated/348-ultima-ex.cs.txt |
| b | A | 120 | 0 | — | `tools/encounter_timeline/generated/355-t6.cs.txt` | tools/encounter_timeline/generated/355-t6.cs.txt |
| b | A | 255 | 0 | — | `tools/encounter_timeline/generated/356-t7.cs.txt` | tools/encounter_timeline/generated/356-t7.cs.txt |
| b | A | 114 | 0 | — | `tools/encounter_timeline/generated/357-t8.cs.txt` | tools/encounter_timeline/generated/357-t8.cs.txt |
| b | A | 231 | 0 | — | `tools/encounter_timeline/generated/358-t9.cs.txt` | tools/encounter_timeline/generated/358-t9.cs.txt |
| b | A | 142 | 0 | — | `tools/encounter_timeline/generated/359-levi-ex.cs.txt` | tools/encounter_timeline/generated/359-levi-ex.cs.txt |
| b | A | 124 | 0 | — | `tools/encounter_timeline/generated/377-shiva-hm.cs.txt` | tools/encounter_timeline/generated/377-shiva-hm.cs.txt |
| b | A | 164 | 0 | — | `tools/encounter_timeline/generated/378-shiva-ex.cs.txt` | tools/encounter_timeline/generated/378-shiva-ex.cs.txt |
| b | A | 105 | 0 | — | `tools/encounter_timeline/generated/430-fractal_continuum.cs.txt` | tools/encounter_timeline/generated/430-fractal_continuum.cs.txt |
| b | A | 66 | 0 | — | `tools/encounter_timeline/generated/444-a3n.cs.txt` | tools/encounter_timeline/generated/444-a3n.cs.txt |
| b | A | 134 | 0 | — | `tools/encounter_timeline/generated/446-ravana-ex.cs.txt` | tools/encounter_timeline/generated/446-ravana-ex.cs.txt |
| b | A | 156 | 0 | — | `tools/encounter_timeline/generated/448-thordan-ex.cs.txt` | tools/encounter_timeline/generated/448-thordan-ex.cs.txt |
| b | A | 109 | 0 | — | `tools/encounter_timeline/generated/449-a1s.cs.txt` | tools/encounter_timeline/generated/449-a1s.cs.txt |
| b | A | 96 | 0 | — | `tools/encounter_timeline/generated/450-a2s.cs.txt` | tools/encounter_timeline/generated/450-a2s.cs.txt |
| b | A | 158 | 0 | — | `tools/encounter_timeline/generated/451-a3s.cs.txt` | tools/encounter_timeline/generated/451-a3s.cs.txt |
| b | A | 193 | 0 | — | `tools/encounter_timeline/generated/452-a4s.cs.txt` | tools/encounter_timeline/generated/452-a4s.cs.txt |
| b | A | 140 | 0 | — | `tools/encounter_timeline/generated/519-the_lost_city_of_amdapor_hard.cs.txt` | tools/encounter_timeline/generated/519-the_lost_city_of_amdapor_hard.cs.txt |
| b | A | 82 | 0 | — | `tools/encounter_timeline/generated/521-a6n.cs.txt` | tools/encounter_timeline/generated/521-a6n.cs.txt |
| b | A | 141 | 0 | — | `tools/encounter_timeline/generated/523-a8n.cs.txt` | tools/encounter_timeline/generated/523-a8n.cs.txt |
| b | A | 129 | 0 | — | `tools/encounter_timeline/generated/524-sephirot-ex.cs.txt` | tools/encounter_timeline/generated/524-sephirot-ex.cs.txt |
| b | A | 130 | 0 | — | `tools/encounter_timeline/generated/529-a5s.cs.txt` | tools/encounter_timeline/generated/529-a5s.cs.txt |
| b | A | 142 | 0 | — | `tools/encounter_timeline/generated/530-a6s.cs.txt` | tools/encounter_timeline/generated/530-a6s.cs.txt |
| b | A | 165 | 0 | — | `tools/encounter_timeline/generated/531-a7s.cs.txt` | tools/encounter_timeline/generated/531-a7s.cs.txt |
| b | A | 247 | 0 | — | `tools/encounter_timeline/generated/532-a8s.cs.txt` | tools/encounter_timeline/generated/532-a8s.cs.txt |
| b | A | 256 | 0 | — | `tools/encounter_timeline/generated/556-weeping_city.cs.txt` | tools/encounter_timeline/generated/556-weeping_city.cs.txt |
| b | A | 92 | 0 | — | `tools/encounter_timeline/generated/577-sophia-ex.cs.txt` | tools/encounter_timeline/generated/577-sophia-ex.cs.txt |
| b | A | 117 | 0 | — | `tools/encounter_timeline/generated/578-gubal_library_hard.cs.txt` | tools/encounter_timeline/generated/578-gubal_library_hard.cs.txt |
| b | A | 85 | 0 | — | `tools/encounter_timeline/generated/581-a10n.cs.txt` | tools/encounter_timeline/generated/581-a10n.cs.txt |
| b | A | 79 | 0 | — | `tools/encounter_timeline/generated/583-a12n.cs.txt` | tools/encounter_timeline/generated/583-a12n.cs.txt |
| b | A | 169 | 0 | — | `tools/encounter_timeline/generated/584-a9s.cs.txt` | tools/encounter_timeline/generated/584-a9s.cs.txt |
| b | A | 176 | 0 | — | `tools/encounter_timeline/generated/585-a10s.cs.txt` | tools/encounter_timeline/generated/585-a10s.cs.txt |
| b | A | 178 | 0 | — | `tools/encounter_timeline/generated/586-a11s.cs.txt` | tools/encounter_timeline/generated/586-a11s.cs.txt |
| b | A | 153 | 0 | — | `tools/encounter_timeline/generated/587-a12s.cs.txt` | tools/encounter_timeline/generated/587-a12s.cs.txt |
| b | A | 172 | 0 | — | `tools/encounter_timeline/generated/616-shisui_of_the_violet_tides74.cs.txt` | tools/encounter_timeline/generated/616-shisui_of_the_violet_tides74.cs.txt |
| b | A | 79 | 0 | — | `tools/encounter_timeline/generated/617-sohm_al_hard.cs.txt` | tools/encounter_timeline/generated/617-sohm_al_hard.cs.txt |
| b | A | 291 | 0 | — | `tools/encounter_timeline/generated/627-dun_scaith.cs.txt` | tools/encounter_timeline/generated/627-dun_scaith.cs.txt |
| b | A | 112 | 0 | — | `tools/encounter_timeline/generated/638-zurvan-ex.cs.txt` | tools/encounter_timeline/generated/638-zurvan-ex.cs.txt |
| b | A | 112 | 0 | — | `tools/encounter_timeline/generated/662-kugane_castle.cs.txt` | tools/encounter_timeline/generated/662-kugane_castle.cs.txt |
| b | A | 91 | 0 | — | `tools/encounter_timeline/generated/663-temple_of_the_fist.cs.txt` | tools/encounter_timeline/generated/663-temple_of_the_fist.cs.txt |
| b | A | 87 | 0 | — | `tools/encounter_timeline/generated/674-susano.cs.txt` | tools/encounter_timeline/generated/674-susano.cs.txt |
| b | A | 117 | 0 | — | `tools/encounter_timeline/generated/677-susano-ex.cs.txt` | tools/encounter_timeline/generated/677-susano-ex.cs.txt |
| b | A | 70 | 0 | — | `tools/encounter_timeline/generated/679-shinryu.cs.txt` | tools/encounter_timeline/generated/679-shinryu.cs.txt |
| b | A | 142 | 0 | — | `tools/encounter_timeline/generated/691-o1n.cs.txt` | tools/encounter_timeline/generated/691-o1n.cs.txt |
| b | A | 103 | 0 | — | `tools/encounter_timeline/generated/692-o2n.cs.txt` | tools/encounter_timeline/generated/692-o2n.cs.txt |
| b | A | 122 | 0 | — | `tools/encounter_timeline/generated/693-o3n.cs.txt` | tools/encounter_timeline/generated/693-o3n.cs.txt |
| b | A | 59 | 0 | — | `tools/encounter_timeline/generated/694-o4n.cs.txt` | tools/encounter_timeline/generated/694-o4n.cs.txt |
| b | A | 104 | 0 | — | `tools/encounter_timeline/generated/695-o1s.cs.txt` | tools/encounter_timeline/generated/695-o1s.cs.txt |
| b | A | 104 | 0 | — | `tools/encounter_timeline/generated/696-o2s.cs.txt` | tools/encounter_timeline/generated/696-o2s.cs.txt |
| b | A | 127 | 0 | — | `tools/encounter_timeline/generated/697-o3s.cs.txt` | tools/encounter_timeline/generated/697-o3s.cs.txt |
| b | A | 186 | 0 | — | `tools/encounter_timeline/generated/698-o4s.cs.txt` | tools/encounter_timeline/generated/698-o4s.cs.txt |
| b | A | 76 | 0 | — | `tools/encounter_timeline/generated/719-lakshmi.cs.txt` | tools/encounter_timeline/generated/719-lakshmi.cs.txt |
| b | A | 105 | 0 | — | `tools/encounter_timeline/generated/720-lakshmi-ex.cs.txt` | tools/encounter_timeline/generated/720-lakshmi-ex.cs.txt |
| b | A | 107 | 0 | — | `tools/encounter_timeline/generated/730-shinryu-ex.cs.txt` | tools/encounter_timeline/generated/730-shinryu-ex.cs.txt |
| b | A | 301 | 0 | — | `tools/encounter_timeline/generated/733-unending_coil_ultimate.cs.txt` | tools/encounter_timeline/generated/733-unending_coil_ultimate.cs.txt |
| b | A | 330 | 0 | — | `tools/encounter_timeline/generated/734-royal_city_of_rabanastre.cs.txt` | tools/encounter_timeline/generated/734-royal_city_of_rabanastre.cs.txt |
| b | A | 92 | 0 | — | `tools/encounter_timeline/generated/742-hells_lid.cs.txt` | tools/encounter_timeline/generated/742-hells_lid.cs.txt |
| b | A | 96 | 0 | — | `tools/encounter_timeline/generated/743-fractal_continuum_hard.cs.txt` | tools/encounter_timeline/generated/743-fractal_continuum_hard.cs.txt |
| b | A | 100 | 0 | — | `tools/encounter_timeline/generated/746-byakko.cs.txt` | tools/encounter_timeline/generated/746-byakko.cs.txt |
| b | A | 59 | 0 | — | `tools/encounter_timeline/generated/748-o5n.cs.txt` | tools/encounter_timeline/generated/748-o5n.cs.txt |
| b | A | 59 | 0 | — | `tools/encounter_timeline/generated/749-o6n.cs.txt` | tools/encounter_timeline/generated/749-o6n.cs.txt |
| b | A | 69 | 0 | — | `tools/encounter_timeline/generated/750-o7n.cs.txt` | tools/encounter_timeline/generated/750-o7n.cs.txt |
| b | A | 74 | 0 | — | `tools/encounter_timeline/generated/751-o8n.cs.txt` | tools/encounter_timeline/generated/751-o8n.cs.txt |
| b | A | 76 | 0 | — | `tools/encounter_timeline/generated/752-o5s.cs.txt` | tools/encounter_timeline/generated/752-o5s.cs.txt |
| b | A | 92 | 0 | — | `tools/encounter_timeline/generated/753-o6s.cs.txt` | tools/encounter_timeline/generated/753-o6s.cs.txt |
| b | A | 288 | 0 | — | `tools/encounter_timeline/generated/754-o7s.cs.txt` | tools/encounter_timeline/generated/754-o7s.cs.txt |
| b | A | 163 | 0 | — | `tools/encounter_timeline/generated/755-o8s.cs.txt` | tools/encounter_timeline/generated/755-o8s.cs.txt |
| b | A | 130 | 0 | — | `tools/encounter_timeline/generated/758-byakko-ex.cs.txt` | tools/encounter_timeline/generated/758-byakko-ex.cs.txt |
| b | A | 95 | 0 | — | `tools/encounter_timeline/generated/768-swallows_compass.cs.txt` | tools/encounter_timeline/generated/768-swallows_compass.cs.txt |
| b | A | 76 | 0 | — | `tools/encounter_timeline/generated/776-ridorana_lighthouse.cs.txt` | tools/encounter_timeline/generated/776-ridorana_lighthouse.cs.txt |
| b | A | 279 | 0 | — | `tools/encounter_timeline/generated/777-ultima_weapon_ultimate.cs.txt` | tools/encounter_timeline/generated/777-ultima_weapon_ultimate.cs.txt |
| b | A | 101 | 0 | — | `tools/encounter_timeline/generated/778-tsukuyomi.cs.txt` | tools/encounter_timeline/generated/778-tsukuyomi.cs.txt |
| b | A | 150 | 0 | — | `tools/encounter_timeline/generated/779-tsukuyomi-ex.cs.txt` | tools/encounter_timeline/generated/779-tsukuyomi-ex.cs.txt |
| b | A | 107 | 0 | — | `tools/encounter_timeline/generated/788-st_mocianne_hard.cs.txt` | tools/encounter_timeline/generated/788-st_mocianne_hard.cs.txt |
| b | A | 95 | 0 | — | `tools/encounter_timeline/generated/798-o9n.cs.txt` | tools/encounter_timeline/generated/798-o9n.cs.txt |
| b | A | 85 | 0 | — | `tools/encounter_timeline/generated/799-o10n.cs.txt` | tools/encounter_timeline/generated/799-o10n.cs.txt |
| b | A | 87 | 0 | — | `tools/encounter_timeline/generated/800-o11n.cs.txt` | tools/encounter_timeline/generated/800-o11n.cs.txt |
| b | A | 159 | 0 | — | `tools/encounter_timeline/generated/801-o12n.cs.txt` | tools/encounter_timeline/generated/801-o12n.cs.txt |
| b | A | 125 | 0 | — | `tools/encounter_timeline/generated/802-o9s.cs.txt` | tools/encounter_timeline/generated/802-o9s.cs.txt |
| b | A | 151 | 0 | — | `tools/encounter_timeline/generated/803-o10s.cs.txt` | tools/encounter_timeline/generated/803-o10s.cs.txt |
| b | A | 111 | 0 | — | `tools/encounter_timeline/generated/804-o11s.cs.txt` | tools/encounter_timeline/generated/804-o11s.cs.txt |
| b | A | 220 | 0 | — | `tools/encounter_timeline/generated/805-o12s.cs.txt` | tools/encounter_timeline/generated/805-o12s.cs.txt |
| b | A | 81 | 0 | — | `tools/encounter_timeline/generated/806-yojimbo.cs.txt` | tools/encounter_timeline/generated/806-yojimbo.cs.txt |
| b | A | 89 | 0 | — | `tools/encounter_timeline/generated/810-suzaku.cs.txt` | tools/encounter_timeline/generated/810-suzaku.cs.txt |
| b | A | 98 | 0 | — | `tools/encounter_timeline/generated/811-suzaku-ex.cs.txt` | tools/encounter_timeline/generated/811-suzaku-ex.cs.txt |
| b | A | 97 | 0 | — | `tools/encounter_timeline/generated/821-dohn_mheg.cs.txt` | tools/encounter_timeline/generated/821-dohn_mheg.cs.txt |
| b | A | 100 | 0 | — | `tools/encounter_timeline/generated/822-mt_gulg.cs.txt` | tools/encounter_timeline/generated/822-mt_gulg.cs.txt |
| b | A | 100 | 0 | — | `tools/encounter_timeline/generated/823-qitana_ravel.cs.txt` | tools/encounter_timeline/generated/823-qitana_ravel.cs.txt |
| b | A | 92 | 0 | — | `tools/encounter_timeline/generated/824-seiryu.cs.txt` | tools/encounter_timeline/generated/824-seiryu.cs.txt |
| b | A | 111 | 0 | — | `tools/encounter_timeline/generated/825-seiryu-ex.cs.txt` | tools/encounter_timeline/generated/825-seiryu-ex.cs.txt |
| b | A | 310 | 0 | — | `tools/encounter_timeline/generated/826-orbonne_monastery.cs.txt` | tools/encounter_timeline/generated/826-orbonne_monastery.cs.txt |
| b | A | 126 | 0 | — | `tools/encounter_timeline/generated/827-eureka_hydatos.cs.txt` | tools/encounter_timeline/generated/827-eureka_hydatos.cs.txt |
| b | A | 99 | 0 | — | `tools/encounter_timeline/generated/836-malikahs_well.cs.txt` | tools/encounter_timeline/generated/836-malikahs_well.cs.txt |
| b | A | 97 | 0 | — | `tools/encounter_timeline/generated/837-holminster_switch.cs.txt` | tools/encounter_timeline/generated/837-holminster_switch.cs.txt |
| b | A | 78 | 0 | — | `tools/encounter_timeline/generated/838-amaurot.cs.txt` | tools/encounter_timeline/generated/838-amaurot.cs.txt |
| b | A | 78 | 0 | — | `tools/encounter_timeline/generated/840-twinning.cs.txt` | tools/encounter_timeline/generated/840-twinning.cs.txt |
| b | A | 110 | 0 | — | `tools/encounter_timeline/generated/841-akadaemia_anyder.cs.txt` | tools/encounter_timeline/generated/841-akadaemia_anyder.cs.txt |
| b | A | 98 | 0 | — | `tools/encounter_timeline/generated/845-titania.cs.txt` | tools/encounter_timeline/generated/845-titania.cs.txt |
| b | A | 80 | 0 | — | `tools/encounter_timeline/generated/846-innocence.cs.txt` | tools/encounter_timeline/generated/846-innocence.cs.txt |
| b | A | 111 | 0 | — | `tools/encounter_timeline/generated/847-hades.cs.txt` | tools/encounter_timeline/generated/847-hades.cs.txt |
| b | A | 113 | 0 | — | `tools/encounter_timeline/generated/848-innocence-ex.cs.txt` | tools/encounter_timeline/generated/848-innocence-ex.cs.txt |
| b | A | 90 | 0 | — | `tools/encounter_timeline/generated/849-e1n.cs.txt` | tools/encounter_timeline/generated/849-e1n.cs.txt |
| b | A | 68 | 0 | — | `tools/encounter_timeline/generated/850-e2n.cs.txt` | tools/encounter_timeline/generated/850-e2n.cs.txt |
| b | A | 84 | 0 | — | `tools/encounter_timeline/generated/851-e3n.cs.txt` | tools/encounter_timeline/generated/851-e3n.cs.txt |
| b | A | 195 | 0 | — | `tools/encounter_timeline/generated/852-e4n.cs.txt` | tools/encounter_timeline/generated/852-e4n.cs.txt |
| b | A | 88 | 0 | — | `tools/encounter_timeline/generated/853-e1s.cs.txt` | tools/encounter_timeline/generated/853-e1s.cs.txt |
| b | A | 78 | 0 | — | `tools/encounter_timeline/generated/854-e2s.cs.txt` | tools/encounter_timeline/generated/854-e2s.cs.txt |
| b | A | 95 | 0 | — | `tools/encounter_timeline/generated/855-e3s.cs.txt` | tools/encounter_timeline/generated/855-e3s.cs.txt |
| b | A | 132 | 0 | — | `tools/encounter_timeline/generated/856-e4s.cs.txt` | tools/encounter_timeline/generated/856-e4s.cs.txt |
| b | A | 112 | 0 | — | `tools/encounter_timeline/generated/858-titania-ex.cs.txt` | tools/encounter_timeline/generated/858-titania-ex.cs.txt |
| b | A | 299 | 0 | — | `tools/encounter_timeline/generated/882-the_copied_factory.cs.txt` | tools/encounter_timeline/generated/882-the_copied_factory.cs.txt |
| b | A | 127 | 0 | — | `tools/encounter_timeline/generated/884-the_grand_cosmos.cs.txt` | tools/encounter_timeline/generated/884-the_grand_cosmos.cs.txt |
| b | A | 115 | 0 | — | `tools/encounter_timeline/generated/885-hades-ex.cs.txt` | tools/encounter_timeline/generated/885-hades-ex.cs.txt |
| b | A | 256 | 0 | — | `tools/encounter_timeline/generated/887-the_epic_of_alexander.cs.txt` | tools/encounter_timeline/generated/887-the_epic_of_alexander.cs.txt |
| b | A | 76 | 0 | — | `tools/encounter_timeline/generated/897-ruby_weapon.cs.txt` | tools/encounter_timeline/generated/897-ruby_weapon.cs.txt |
| b | A | 123 | 0 | — | `tools/encounter_timeline/generated/898-anamnesis_anyder.cs.txt` | tools/encounter_timeline/generated/898-anamnesis_anyder.cs.txt |
| b | A | 62 | 0 | — | `tools/encounter_timeline/generated/902-e5n.cs.txt` | tools/encounter_timeline/generated/902-e5n.cs.txt |
| b | A | 91 | 0 | — | `tools/encounter_timeline/generated/903-e6n.cs.txt` | tools/encounter_timeline/generated/903-e6n.cs.txt |
| b | A | 89 | 0 | — | `tools/encounter_timeline/generated/904-e7n.cs.txt` | tools/encounter_timeline/generated/904-e7n.cs.txt |
| b | A | 90 | 0 | — | `tools/encounter_timeline/generated/905-e8n.cs.txt` | tools/encounter_timeline/generated/905-e8n.cs.txt |
| b | A | 101 | 0 | — | `tools/encounter_timeline/generated/906-e5s.cs.txt` | tools/encounter_timeline/generated/906-e5s.cs.txt |
| b | A | 132 | 0 | — | `tools/encounter_timeline/generated/907-e6s.cs.txt` | tools/encounter_timeline/generated/907-e6s.cs.txt |
| b | A | 101 | 0 | — | `tools/encounter_timeline/generated/908-e7s.cs.txt` | tools/encounter_timeline/generated/908-e7s.cs.txt |
| b | A | 169 | 0 | — | `tools/encounter_timeline/generated/909-e8s.cs.txt` | tools/encounter_timeline/generated/909-e8s.cs.txt |
| b | A | 123 | 0 | — | `tools/encounter_timeline/generated/912-ruby_weapon-ex.cs.txt` | tools/encounter_timeline/generated/912-ruby_weapon-ex.cs.txt |
| b | A | 166 | 0 | — | `tools/encounter_timeline/generated/913-varis-ex.cs.txt` | tools/encounter_timeline/generated/913-varis-ex.cs.txt |
| b | A | 94 | 0 | — | `tools/encounter_timeline/generated/916-heroes_gauntlet.cs.txt` | tools/encounter_timeline/generated/916-heroes_gauntlet.cs.txt |
| b | A | 294 | 0 | — | `tools/encounter_timeline/generated/917-the_puppets_bunker.cs.txt` | tools/encounter_timeline/generated/917-the_puppets_bunker.cs.txt |
| b | A | 318 | 0 | — | `tools/encounter_timeline/generated/920-bozjan_southern_front.cs.txt` | tools/encounter_timeline/generated/920-bozjan_southern_front.cs.txt |
| b | A | 93 | 0 | — | `tools/encounter_timeline/generated/922-wol.cs.txt` | tools/encounter_timeline/generated/922-wol.cs.txt |
| b | A | 295 | 0 | — | `tools/encounter_timeline/generated/923-wol-ex.cs.txt` | tools/encounter_timeline/generated/923-wol-ex.cs.txt |
| b | A | 164 | 0 | — | `tools/encounter_timeline/generated/930-shiva-un.cs.txt` | tools/encounter_timeline/generated/930-shiva-un.cs.txt |
| b | A | 89 | 0 | — | `tools/encounter_timeline/generated/933-matoyas_relict.cs.txt` | tools/encounter_timeline/generated/933-matoyas_relict.cs.txt |
| b | A | 104 | 0 | — | `tools/encounter_timeline/generated/934-emerald_weapon.cs.txt` | tools/encounter_timeline/generated/934-emerald_weapon.cs.txt |
| b | A | 88 | 0 | — | `tools/encounter_timeline/generated/935-emerald_weapon-ex.cs.txt` | tools/encounter_timeline/generated/935-emerald_weapon-ex.cs.txt |
| b | A | 434 | 0 | — | `tools/encounter_timeline/generated/936-delubrum_reginae.cs.txt` | tools/encounter_timeline/generated/936-delubrum_reginae.cs.txt |
| b | A | 1085 | 0 | — | `tools/encounter_timeline/generated/937-delubrum_reginae_savage.cs.txt` | tools/encounter_timeline/generated/937-delubrum_reginae_savage.cs.txt |
| b | A | 60 | 0 | — | `tools/encounter_timeline/generated/938-paglthan.cs.txt` | tools/encounter_timeline/generated/938-paglthan.cs.txt |
| b | A | 56 | 0 | — | `tools/encounter_timeline/generated/942-e9n.cs.txt` | tools/encounter_timeline/generated/942-e9n.cs.txt |
| b | A | 60 | 0 | — | `tools/encounter_timeline/generated/943-e10n.cs.txt` | tools/encounter_timeline/generated/943-e10n.cs.txt |
| b | A | 80 | 0 | — | `tools/encounter_timeline/generated/944-e11n.cs.txt` | tools/encounter_timeline/generated/944-e11n.cs.txt |
| b | A | 93 | 0 | — | `tools/encounter_timeline/generated/945-e12n.cs.txt` | tools/encounter_timeline/generated/945-e12n.cs.txt |
| b | A | 58 | 0 | — | `tools/encounter_timeline/generated/946-e9s.cs.txt` | tools/encounter_timeline/generated/946-e9s.cs.txt |
| b | A | 108 | 0 | — | `tools/encounter_timeline/generated/947-e10s.cs.txt` | tools/encounter_timeline/generated/947-e10s.cs.txt |
| b | A | 165 | 0 | — | `tools/encounter_timeline/generated/948-e11s.cs.txt` | tools/encounter_timeline/generated/948-e11s.cs.txt |
| b | A | 172 | 0 | — | `tools/encounter_timeline/generated/949-e12s.cs.txt` | tools/encounter_timeline/generated/949-e12s.cs.txt |
| b | A | 77 | 0 | — | `tools/encounter_timeline/generated/950-diamond_weapon.cs.txt` | tools/encounter_timeline/generated/950-diamond_weapon.cs.txt |
| b | A | 118 | 0 | — | `tools/encounter_timeline/generated/951-diamond_weapon-ex.cs.txt` | tools/encounter_timeline/generated/951-diamond_weapon-ex.cs.txt |
| b | A | 94 | 0 | — | `tools/encounter_timeline/generated/952-the_tower_of_zot.cs.txt` | tools/encounter_timeline/generated/952-the_tower_of_zot.cs.txt |
| b | A | 151 | 0 | — | `tools/encounter_timeline/generated/953-titan-un.cs.txt` | tools/encounter_timeline/generated/953-titan-un.cs.txt |
| b | A | 381 | 0 | — | `tools/encounter_timeline/generated/966-the_tower_at_paradigms_breach.cs.txt` | tools/encounter_timeline/generated/966-the_tower_at_paradigms_breach.cs.txt |
| b | A | 329 | 0 | — | `tools/encounter_timeline/generated/968-dragonsongs_reprise_ultimate.cs.txt` | tools/encounter_timeline/generated/968-dragonsongs_reprise_ultimate.cs.txt |
| b | A | 127 | 0 | — | `tools/encounter_timeline/generated/969-the_tower_of_babil.cs.txt` | tools/encounter_timeline/generated/969-the_tower_of_babil.cs.txt |
| b | A | 100 | 0 | — | `tools/encounter_timeline/generated/970-vanaspati.cs.txt` | tools/encounter_timeline/generated/970-vanaspati.cs.txt |
| b | A | 142 | 0 | — | `tools/encounter_timeline/generated/972-levi-un.cs.txt` | tools/encounter_timeline/generated/972-levi-un.cs.txt |
| b | A | 106 | 0 | — | `tools/encounter_timeline/generated/973-the_dead_ends.cs.txt` | tools/encounter_timeline/generated/973-the_dead_ends.cs.txt |
| b | A | 90 | 0 | — | `tools/encounter_timeline/generated/974-ktisis_hyperboreia.cs.txt` | tools/encounter_timeline/generated/974-ktisis_hyperboreia.cs.txt |
| b | A | 349 | 0 | — | `tools/encounter_timeline/generated/975-zadnor.cs.txt` | tools/encounter_timeline/generated/975-zadnor.cs.txt |
| b | A | 87 | 0 | — | `tools/encounter_timeline/generated/976-smileton.cs.txt` | tools/encounter_timeline/generated/976-smileton.cs.txt |
| b | A | 63 | 0 | — | `tools/encounter_timeline/generated/978-the_aitiascope.cs.txt` | tools/encounter_timeline/generated/978-the_aitiascope.cs.txt |
| b | A | 77 | 0 | — | `tools/encounter_timeline/generated/986-stigma_dreamscape.cs.txt` | tools/encounter_timeline/generated/986-stigma_dreamscape.cs.txt |
| b | A | 95 | 0 | — | `tools/encounter_timeline/generated/992-zodiark.cs.txt` | tools/encounter_timeline/generated/992-zodiark.cs.txt |
| b | A | 114 | 0 | — | `tools/encounter_timeline/generated/993-zodiark-ex.cs.txt` | tools/encounter_timeline/generated/993-zodiark-ex.cs.txt |
| b | A | 148 | 0 | — | `tools/encounter_timeline/generated/995-hydaelyn.cs.txt` | tools/encounter_timeline/generated/995-hydaelyn.cs.txt |
| b | A | 153 | 0 | — | `tools/encounter_timeline/generated/996-hydaelyn-ex.cs.txt` | tools/encounter_timeline/generated/996-hydaelyn-ex.cs.txt |
| b | A | 42 | 0 | — | `tools/encounter_timeline/generated/997-endsinger.cs.txt` | tools/encounter_timeline/generated/997-endsinger.cs.txt |
| b | A | 95 | 0 | — | `tools/encounter_timeline/generated/998-endsinger-ex.cs.txt` | tools/encounter_timeline/generated/998-endsinger-ex.cs.txt |
| b | A | 4042 | 0 | — | `tools/encounter_timeline/generated/cactbot-sync-report.json` | tools/encounter_timeline/generated/cactbot-sync-report.json |
| b | A | 18 | 0 | — | `tools/external_timeline_regression/ExternalTimelineRegression.csproj` | tools/external_timeline_regression/ExternalTimelineRegression.csproj |
| b | A | 2056 | 0 | — | `tools/external_timeline_regression/Program.cs` | tools/external_timeline_regression/Program.cs |
| b | A | 133 | 0 | — | `tools/fflogs_timeline_extract/FFLogsClient.cs` | tools/fflogs_timeline_extract/FFLogsClient.cs |
| b | A | 150 | 0 | — | `tools/fflogs_timeline_extract/FFLogsReplayBuilder.cs` | tools/fflogs_timeline_extract/FFLogsReplayBuilder.cs |
| b | A | 17 | 0 | — | `tools/fflogs_timeline_extract/FFLogsTimelineExtract.csproj` | tools/fflogs_timeline_extract/FFLogsTimelineExtract.csproj |
| b | A | 416 | 0 | — | `tools/fflogs_timeline_extract/Program.cs` | tools/fflogs_timeline_extract/Program.cs |
| b | A | 26 | 0 | — | `tools/local_rotation_ai/README.md` | tools/local_rotation_ai/README.md |
| b | A | 300 | 0 | — | `tools/local_rotation_ai/functiongemma_server.py` | tools/local_rotation_ai/functiongemma_server.py |
| b | A | 10 | 0 | — | `tools/local_rotation_ai/median.py` | tools/local_rotation_ai/median.py |
| b | A | 5 | 0 | — | `tools/local_rotation_ai/requirements.txt` | tools/local_rotation_ai/requirements.txt |
| b | A | 44 | 0 | — | `tools/local_rotation_ai/start-functiongemma.ps1` | tools/local_rotation_ai/start-functiongemma.ps1 |
| b | A | 51 | 0 | — | `tools/mch_real_harness/MchRealHarness.csproj` | tools/mch_real_harness/MchRealHarness.csproj |
| b | A | 636 | 0 | — | `tools/mch_real_harness/Program.cs` | tools/mch_real_harness/Program.cs |
| b | A | 657 | 0 | — | `tools/mch_regression/FflogsMchExtractor.cs` | tools/mch_regression/FflogsMchExtractor.cs |
| b | A | 189 | 0 | — | `tools/mch_regression/MchActionModel.cs` | tools/mch_regression/MchActionModel.cs |
| b | A | 9 | 0 | — | `tools/mch_regression/MchRegression.csproj` | tools/mch_regression/MchRegression.csproj |
| b | A | 198 | 0 | — | `tools/mch_regression/MchRegressionRules.cs` | tools/mch_regression/MchRegressionRules.cs |
| b | A | 96 | 0 | — | `tools/mch_regression/MchReport.cs` | tools/mch_regression/MchReport.cs |
| b | A | 225 | 0 | — | `tools/mch_regression/MchScenario.cs` | tools/mch_regression/MchScenario.cs |
| b | A | 753 | 0 | — | `tools/mch_regression/MchSimulator.cs` | tools/mch_regression/MchSimulator.cs |
| b | A | 235 | 0 | — | `tools/mch_regression/MchState.cs` | tools/mch_regression/MchState.cs |
| b | A | 110 | 0 | — | `tools/mch_regression/Program.cs` | tools/mch_regression/Program.cs |
| b | A | 632 | 0 | — | `tools/mch_regression/fflogs_out/dancing_mad_mch_profile.generated.json` | 未コピー (生成物, F:に残置) |
| b | A | 115865 | 0 | — | `tools/mch_regression/fflogs_out/mch_action_events.json` | 未コピー (生成物, F:に残置) |
| b | A | 3038 | 0 | — | `tools/mch_regression/fflogs_out/mch_mitigation_events.json` | 未コピー (生成物, F:に残置) |
| b | A | 171 | 0 | — | `tools/mnk_real_harness/Invoke-MnkPreflight.ps1` | tools/mnk_real_harness/Invoke-MnkPreflight.ps1 |
| b | A | 53 | 0 | — | `tools/mnk_real_harness/MnkRealHarness.csproj` | tools/mnk_real_harness/MnkRealHarness.csproj |
| b | A | 3715 | 0 | — | `tools/mnk_real_harness/Program.cs` | tools/mnk_real_harness/Program.cs |
| b | A | 344 | 0 | — | `tools/mnk_regression/BattleEmulator.cs` | tools/mnk_regression/BattleEmulator.cs |
| b | A | 104 | 0 | — | `tools/mnk_regression/BattleEvent.cs` | tools/mnk_regression/BattleEvent.cs |
| b | A | 18 | 0 | — | `tools/mnk_regression/BattleEventTypes.cs` | tools/mnk_regression/BattleEventTypes.cs |
| b | A | 29 | 0 | — | `tools/mnk_regression/BattleScenario.cs` | tools/mnk_regression/BattleScenario.cs |
| b | A | 106 | 0 | — | `tools/mnk_regression/BattleState.cs` | tools/mnk_regression/BattleState.cs |
| b | A | 179 | 0 | — | `tools/mnk_regression/BattleTimeline.cs` | tools/mnk_regression/BattleTimeline.cs |
| b | A | 850 | 0 | — | `tools/mnk_regression/MnkActionSimulator.cs` | tools/mnk_regression/MnkActionSimulator.cs |
| b | A | 152 | 0 | — | `tools/mnk_regression/MnkPatch75Data.cs` | tools/mnk_regression/MnkPatch75Data.cs |
| b | A | 9 | 0 | — | `tools/mnk_regression/MnkRegression.csproj` | tools/mnk_regression/MnkRegression.csproj |
| b | A | 95 | 0 | — | `tools/mnk_regression/MnkStateSnapshot.cs` | tools/mnk_regression/MnkStateSnapshot.cs |
| b | A | 281 | 0 | — | `tools/mnk_regression/Program.cs` | tools/mnk_regression/Program.cs |
| b | A | 258 | 0 | — | `tools/mnk_regression/RegressionRules.cs` | tools/mnk_regression/RegressionRules.cs |
| b | A | 243 | 0 | — | `tools/mnk_regression/ResultComparer.cs` | tools/mnk_regression/ResultComparer.cs |
| b | A | 133 | 0 | — | `tools/mnk_regression/ResultModels.cs` | tools/mnk_regression/ResultModels.cs |
| b | A | 459 | 0 | — | `tools/mnk_regression/ScenarioCatalog.cs` | tools/mnk_regression/ScenarioCatalog.cs |
| b | A | 152 | 0 | — | `tools/mnk_regression/ScenarioModels.cs` | tools/mnk_regression/ScenarioModels.cs |
| b | A | 7 | 0 | — | `tools/mnk_regression/ScenarioRunner.cs` | tools/mnk_regression/ScenarioRunner.cs |
| b | A | 853 | 0 | — | `tools/mnk_tuning/README.md` | tools/mnk_tuning/README.md |
| b | A | 234 | 0 | — | `tools/mnk_tuning/analyze_blm_timing_distributions.py` | tools/mnk_tuning/analyze_blm_timing_distributions.py |
| b | A | 1403 | 0 | — | `tools/mnk_tuning/analyze_dancing_mad_timeline_profile.py` | tools/mnk_tuning/analyze_dancing_mad_timeline_profile.py |
| b | A | 184 | 0 | — | `tools/mnk_tuning/analyze_even_pre_rof_pb_threshold.py` | tools/mnk_tuning/analyze_even_pre_rof_pb_threshold.py |
| b | A | 174 | 0 | — | `tools/mnk_tuning/analyze_even_pre_rof_pb_threshold_sweep.py` | tools/mnk_tuning/analyze_even_pre_rof_pb_threshold_sweep.py |
| b | A | 206 | 0 | — | `tools/mnk_tuning/analyze_mnk_timing_distributions.py` | tools/mnk_tuning/analyze_mnk_timing_distributions.py |
| b | A | 213 | 0 | — | `tools/mnk_tuning/analyze_nin_timing_distributions.py` | tools/mnk_tuning/analyze_nin_timing_distributions.py |
| b | A | 209 | 0 | — | `tools/mnk_tuning/analyze_vpr_timing_distributions.py` | tools/mnk_tuning/analyze_vpr_timing_distributions.py |
| b | A | 116 | 0 | — | `tools/mnk_tuning/blm_ability_map_candidates.json` | tools/mnk_tuning/blm_ability_map_candidates.json |
| b | A | 202 | 0 | — | `tools/mnk_tuning/build_job_samples_from_top_cache.py` | tools/mnk_tuning/build_job_samples_from_top_cache.py |
| b | A | 85 | 0 | — | `tools/mnk_tuning/collect_mnk_top300.py` | tools/mnk_tuning/collect_mnk_top300.py |
| b | A | 262 | 0 | — | `tools/mnk_tuning/collect_public_top_jobs.py` | tools/mnk_tuning/collect_public_top_jobs.py |
| b | A | 270 | 0 | — | `tools/mnk_tuning/collect_report_events.py` | tools/mnk_tuning/collect_report_events.py |
| b | A | 186 | 0 | — | `tools/mnk_tuning/convert_cast_features_to_job_sample.py` | tools/mnk_tuning/convert_cast_features_to_job_sample.py |
| b | A | 25 | 0 | — | `tools/mnk_tuning/dancing_mad_mnk_profile_candidate.json` | tools/mnk_tuning/dancing_mad_mnk_profile_candidate.json |
| b | A | 179 | 0 | — | `tools/mnk_tuning/diagnose_cast_actor_mapping.py` | tools/mnk_tuning/diagnose_cast_actor_mapping.py |
| b | A | 260 | 0 | — | `tools/mnk_tuning/export_blm_timing_review.py` | tools/mnk_tuning/export_blm_timing_review.py |
| b | A | 120 | 0 | — | `tools/mnk_tuning/export_job_profiles.py` | tools/mnk_tuning/export_job_profiles.py |
| b | A | 243 | 0 | — | `tools/mnk_tuning/export_mnk_final_tuning_review.py` | tools/mnk_tuning/export_mnk_final_tuning_review.py |
| b | A | 132 | 0 | — | `tools/mnk_tuning/export_mnk_profiles.py` | tools/mnk_tuning/export_mnk_profiles.py |
| b | A | 219 | 0 | — | `tools/mnk_tuning/export_mnk_tuning_candidates.py` | tools/mnk_tuning/export_mnk_tuning_candidates.py |
| b | A | 178 | 0 | — | `tools/mnk_tuning/export_nin_timing_review.py` | tools/mnk_tuning/export_nin_timing_review.py |
| b | A | 265 | 0 | — | `tools/mnk_tuning/export_vpr_timing_review.py` | tools/mnk_tuning/export_vpr_timing_review.py |
| b | A | 293 | 0 | — | `tools/mnk_tuning/extract_blm_specific_features.py` | tools/mnk_tuning/extract_blm_specific_features.py |
| b | A | 152 | 0 | — | `tools/mnk_tuning/extract_cast_features.py` | tools/mnk_tuning/extract_cast_features.py |
| b | A | 125 | 0 | — | `tools/mnk_tuning/extract_job_features.py` | tools/mnk_tuning/extract_job_features.py |
| b | A | 122 | 0 | — | `tools/mnk_tuning/extract_mnk_features.py` | tools/mnk_tuning/extract_mnk_features.py |
| b | A | 261 | 0 | — | `tools/mnk_tuning/extract_mnk_specific_features.py` | tools/mnk_tuning/extract_mnk_specific_features.py |
| b | A | 270 | 0 | — | `tools/mnk_tuning/extract_nin_specific_features.py` | tools/mnk_tuning/extract_nin_specific_features.py |
| b | A | 276 | 0 | — | `tools/mnk_tuning/extract_vpr_specific_features.py` | tools/mnk_tuning/extract_vpr_specific_features.py |
| b | A | 90 | 0 | — | `tools/mnk_tuning/fflogs_client.py` | tools/mnk_tuning/fflogs_client.py |
| b | A | 109 | 0 | — | `tools/mnk_tuning/filter_fflogs_type_names.py` | tools/mnk_tuning/filter_fflogs_type_names.py |
| b | A | 292 | 0 | — | `tools/mnk_tuning/filter_nin_pre_raiton_samples.py` | tools/mnk_tuning/filter_nin_pre_raiton_samples.py |
| b | A | 43 | 0 | — | `tools/mnk_tuning/inspect_fflogs_schema.py` | tools/mnk_tuning/inspect_fflogs_schema.py |
| b | A | 17 | 0 | — | `tools/mnk_tuning/job_input_example.json` | tools/mnk_tuning/job_input_example.json |
| b | A | 117 | 0 | — | `tools/mnk_tuning/jobs.py` | tools/mnk_tuning/jobs.py |
| b | A | 31 | 0 | — | `tools/mnk_tuning/mnk_ability_map_candidates.json` | tools/mnk_tuning/mnk_ability_map_candidates.json |
| b | A | 25 | 0 | — | `tools/mnk_tuning/mnk_ability_map_example.json` | tools/mnk_tuning/mnk_ability_map_example.json |
| b | A | 18 | 0 | — | `tools/mnk_tuning/mnk_job_features_batch.json` | tools/mnk_tuning/mnk_job_features_batch.json |
| b | A | 25 | 0 | — | `tools/mnk_tuning/mnk_job_features_batch10.json` | tools/mnk_tuning/mnk_job_features_batch10.json |
| b | A | 215 | 0 | — | `tools/mnk_tuning/mnk_job_features_batch200.json` | tools/mnk_tuning/mnk_job_features_batch200.json |
| b | A | 65 | 0 | — | `tools/mnk_tuning/mnk_job_features_batch50.json` | tools/mnk_tuning/mnk_job_features_batch50.json |
| b | A | 17 | 0 | — | `tools/mnk_tuning/mnk_job_profile_batch.json` | tools/mnk_tuning/mnk_job_profile_batch.json |
| b | A | 17 | 0 | — | `tools/mnk_tuning/mnk_job_profile_batch10.json` | tools/mnk_tuning/mnk_job_profile_batch10.json |
| b | A | 17 | 0 | — | `tools/mnk_tuning/mnk_job_profile_batch200.json` | tools/mnk_tuning/mnk_job_profile_batch200.json |
| b | A | 17 | 0 | — | `tools/mnk_tuning/mnk_job_profile_batch50.json` | tools/mnk_tuning/mnk_job_profile_batch50.json |
| b | A | 2054 | 0 | — | `tools/mnk_tuning/mnk_job_samples_batch10.json` | tools/mnk_tuning/mnk_job_samples_batch10.json |
| b | A | 41425 | 0 | — | `tools/mnk_tuning/mnk_job_samples_batch200.json` | tools/mnk_tuning/mnk_job_samples_batch200.json |
| b | A | 10506 | 0 | — | `tools/mnk_tuning/mnk_job_samples_batch50.json` | tools/mnk_tuning/mnk_job_samples_batch50.json |
| b | A | 132 | 0 | — | `tools/mnk_tuning/mnk_refactor_design_20260702.md` | tools/mnk_tuning/mnk_refactor_design_20260702.md |
| b | A | 724 | 0 | — | `tools/mnk_tuning/mnk_tuning_tool.py` | tools/mnk_tuning/mnk_tuning_tool.py |
| b | A | 86 | 0 | — | `tools/mnk_tuning/nin_ability_map_candidates.json` | tools/mnk_tuning/nin_ability_map_candidates.json |
| b | A | 20 | 0 | — | `tools/mnk_tuning/profile_output_example.json` | tools/mnk_tuning/profile_output_example.json |
| b | A | 108 | 0 | — | `tools/mnk_tuning/public_data_cache.py` | tools/mnk_tuning/public_data_cache.py |
| b | A | 189 | 0 | — | `tools/mnk_tuning/review_mnk_tuning_candidates.py` | tools/mnk_tuning/review_mnk_tuning_candidates.py |
| b | A | 242 | 0 | — | `tools/mnk_tuning/review_nin_pre_raiton_timing.py` | tools/mnk_tuning/review_nin_pre_raiton_timing.py |
| b | A | 130 | 0 | — | `tools/mnk_tuning/run_job_public_pipeline.py` | tools/mnk_tuning/run_job_public_pipeline.py |
| b | A | 26 | 0 | — | `tools/mnk_tuning/sample_graphql/character_rankings_collect.graphql` | tools/mnk_tuning/sample_graphql/character_rankings_collect.graphql |
| b | A | 26 | 0 | — | `tools/mnk_tuning/sample_graphql/character_rankings_shape_probe.graphql` | tools/mnk_tuning/sample_graphql/character_rankings_shape_probe.graphql |
| b | A | 79 | 0 | — | `tools/mnk_tuning/sample_graphql/ranking_type_probe_query.graphql` | tools/mnk_tuning/sample_graphql/ranking_type_probe_query.graphql |
| b | A | 8 | 0 | — | `tools/mnk_tuning/sample_graphql/rate_limit_query.graphql` | tools/mnk_tuning/sample_graphql/rate_limit_query.graphql |
| b | A | 62 | 0 | — | `tools/mnk_tuning/sample_graphql/refined_type_probe_query.graphql` | tools/mnk_tuning/sample_graphql/refined_type_probe_query.graphql |
| b | A | 44 | 0 | — | `tools/mnk_tuning/sample_graphql/report_events_collect.graphql` | tools/mnk_tuning/sample_graphql/report_events_collect.graphql |
| b | A | 44 | 0 | — | `tools/mnk_tuning/sample_graphql/report_events_shape_probe.graphql` | tools/mnk_tuning/sample_graphql/report_events_shape_probe.graphql |
| b | A | 32 | 0 | — | `tools/mnk_tuning/sample_graphql/schema_probe_query.graphql` | tools/mnk_tuning/sample_graphql/schema_probe_query.graphql |
| b | A | 8 | 0 | — | `tools/mnk_tuning/sample_graphql/type_name_list_query.graphql` | tools/mnk_tuning/sample_graphql/type_name_list_query.graphql |
| b | A | 12 | 0 | — | `tools/mnk_tuning/sample_graphql/zone_encounters_probe.graphql` | tools/mnk_tuning/sample_graphql/zone_encounters_probe.graphql |
| b | A | 161 | 0 | — | `tools/mnk_tuning/summarize_ability_usage.py` | tools/mnk_tuning/summarize_ability_usage.py |
| b | A | 121 | 0 | — | `tools/mnk_tuning/summarize_character_rankings_probe.py` | tools/mnk_tuning/summarize_character_rankings_probe.py |
| b | A | 153 | 0 | — | `tools/mnk_tuning/summarize_fflogs_schema_probe.py` | tools/mnk_tuning/summarize_fflogs_schema_probe.py |
| b | A | 106 | 0 | — | `tools/mnk_tuning/summarize_json_shape.py` | tools/mnk_tuning/summarize_json_shape.py |
| b | A | 78 | 0 | — | `tools/mnk_tuning/summarize_zone_encounters.py` | tools/mnk_tuning/summarize_zone_encounters.py |
| b | A | 52 | 0 | — | `tools/mnk_tuning/test_phase1_blitz_decision_refactor.py` | tools/mnk_tuning/test_phase1_blitz_decision_refactor.py |
| b | A | 3010 | 0 | — | `tools/mnk_tuning/tmp_probe_dm/report_events_shape_response.json` | 未コピー (生成物, F:に残置) |
| b | A | 49 | 0 | — | `tools/mnk_tuning/tmp_probe_dm/report_events_shape_summary.json` | 未コピー (生成物, F:に残置) |
| b | A | 3162 | 0 | — | `tools/mnk_tuning/tmp_probe_dm2/report_events_shape_response.json` | 未コピー (生成物, F:に残置) |
| b | A | 35 | 0 | — | `tools/mnk_tuning/tmp_probe_dm2/report_events_shape_summary.json` | 未コピー (生成物, F:に残置) |
| b | A | 161944 | 0 | — | `tools/mnk_tuning/tmp_probe_dm3/report_events_shape_response.json` | 未コピー (生成物, F:に残置) |
| b | A | 49 | 0 | — | `tools/mnk_tuning/tmp_probe_dm3/report_events_shape_summary.json` | 未コピー (生成物, F:に残置) |
| b | A | 3162 | 0 | — | `tools/mnk_tuning/tmp_probe_dm4/report_events_shape_response.json` | 未コピー (生成物, F:に残置) |
| b | A | 35 | 0 | — | `tools/mnk_tuning/tmp_probe_dm4/report_events_shape_summary.json` | 未コピー (生成物, F:に残置) |
| b | A | 3162 | 0 | — | `tools/mnk_tuning/tmp_probe_dm5/report_events_shape_response.json` | 未コピー (生成物, F:に残置) |
| b | A | 35 | 0 | — | `tools/mnk_tuning/tmp_probe_dm5/report_events_shape_summary.json` | 未コピー (生成物, F:に残置) |
| b | A | 3162 | 0 | — | `tools/mnk_tuning/tmp_probe_dm6/report_events_shape_response.json` | 未コピー (生成物, F:に残置) |
| b | A | 35 | 0 | — | `tools/mnk_tuning/tmp_probe_dm6/report_events_shape_summary.json` | 未コピー (生成物, F:に残置) |
| b | A | 37190 | 0 | — | `tools/mnk_tuning/tmp_probe_dm7/report_events_shape_response.json` | 未コピー (生成物, F:に残置) |
| b | A | 50 | 0 | — | `tools/mnk_tuning/tmp_probe_dm7/report_events_shape_summary.json` | 未コピー (生成物, F:に残置) |
| b | A | 106 | 0 | — | `tools/mnk_tuning/vpr_ability_map_candidates.json` | tools/mnk_tuning/vpr_ability_map_candidates.json |
| b | A | 52 | 0 | — | `tools/nin_real_harness/NinRealHarness.csproj` | tools/nin_real_harness/NinRealHarness.csproj |
| b | A | 2069 | 0 | — | `tools/nin_real_harness/Program.cs` | tools/nin_real_harness/Program.cs |
| b | A | 161 | 0 | — | `tools/nin_regression/NinHardFailRules.cs` | tools/nin_regression/NinHardFailRules.cs |
| b | A | 9 | 0 | — | `tools/nin_regression/NinRegression.csproj` | tools/nin_regression/NinRegression.csproj |
| b | A | 87 | 0 | — | `tools/nin_regression/NinReport.cs` | tools/nin_regression/NinReport.cs |
| b | A | 624 | 0 | — | `tools/nin_regression/NinRotationEmulator.cs` | tools/nin_regression/NinRotationEmulator.cs |
| b | A | 115 | 0 | — | `tools/nin_regression/NinScenario.cs` | tools/nin_regression/NinScenario.cs |
| b | A | 297 | 0 | — | `tools/nin_regression/NinScenarioGenerator.cs` | tools/nin_regression/NinScenarioGenerator.cs |
| b | A | 167 | 0 | — | `tools/nin_regression/NinSimAction.cs` | tools/nin_regression/NinSimAction.cs |
| b | A | 320 | 0 | — | `tools/nin_regression/NinSimState.cs` | tools/nin_regression/NinSimState.cs |
| b | A | 98 | 0 | — | `tools/nin_regression/Program.cs` | tools/nin_regression/Program.cs |
| b | A | 292 | 0 | — | `tools/replay_profiler/Program.cs` | tools/replay_profiler/Program.cs |
| b | A | 29 | 0 | — | `tools/replay_profiler/ReplayProfiler.csproj` | tools/replay_profiler/ReplayProfiler.csproj |
| b | A | 128 | 0 | — | `tools/replay_timeline_extract/Program.cs` | tools/replay_timeline_extract/Program.cs |
| b | A | 18 | 0 | — | `tools/replay_timeline_extract/ReplayTimelineExtract.csproj` | tools/replay_timeline_extract/ReplayTimelineExtract.csproj |
| b | A | 880 | 0 | — | `tools/rpr_regression/FflogsRprExtractor.cs` | tools/rpr_regression/FflogsRprExtractor.cs |
| b | A | 1175 | 0 | — | `tools/rpr_regression/HardFailRules.cs` | tools/rpr_regression/HardFailRules.cs |
| b | A | 396 | 0 | — | `tools/rpr_regression/Models.cs` | tools/rpr_regression/Models.cs |
| b | A | 4574 | 0 | — | `tools/rpr_regression/Program.cs` | tools/rpr_regression/Program.cs |
| b | A | 207 | 0 | — | `tools/rpr_regression/RealRprHarness.cs` | tools/rpr_regression/RealRprHarness.cs |
| b | A | 9 | 0 | — | `tools/rpr_regression/RprRegression.csproj` | tools/rpr_regression/RprRegression.csproj |
| b | A | 2504 | 0 | — | `tools/rpr_regression/RprRotationEmulator.cs` | tools/rpr_regression/RprRotationEmulator.cs |
| b | A | 575 | 0 | — | `tools/rpr_regression/ScenarioCatalog.cs` | tools/rpr_regression/ScenarioCatalog.cs |
| b | A | 1357 | 0 | — | `tools/sam_real_harness/Program.cs` | tools/sam_real_harness/Program.cs |
| b | A | 51 | 0 | — | `tools/sam_real_harness/SamRealHarness.csproj` | tools/sam_real_harness/SamRealHarness.csproj |
| b | A | 136 | 0 | — | `tools/sam_regression/Program.cs` | tools/sam_regression/Program.cs |
| b | A | 51 | 0 | — | `tools/sam_regression/README.md` | tools/sam_regression/README.md |
| b | A | 358 | 0 | — | `tools/sam_regression/SamChecks.cs` | tools/sam_regression/SamChecks.cs |
| b | A | 9 | 0 | — | `tools/sam_regression/SamRegression.csproj` | tools/sam_regression/SamRegression.csproj |
| b | A | 211 | 0 | — | `tools/sam_regression/SamScenario.cs` | tools/sam_regression/SamScenario.cs |
| b | A | 848 | 0 | — | `tools/sam_regression/SamSim.cs` | tools/sam_regression/SamSim.cs |
| b | A | 223 | 0 | — | `tools/timeline_hint_harness/Program.cs` | tools/timeline_hint_harness/Program.cs |
| b | A | 18 | 0 | — | `tools/timeline_hint_harness/TimelineHintHarness.csproj` | tools/timeline_hint_harness/TimelineHintHarness.csproj |
| b | A | 862 | 0 | — | `tools/timeline_regression/Program.cs` | tools/timeline_regression/Program.cs |
| b | A | 17 | 0 | — | `tools/timeline_regression/TimelineRegression.csproj` | tools/timeline_regression/TimelineRegression.csproj |
| b | A | 80 | 0 | — | `tools/ttk_eval/Bench.cs` | tools/ttk_eval/Bench.cs |
| b | A | 448 | 0 | — | `tools/ttk_eval/Eval.cs` | tools/ttk_eval/Eval.cs |
| b | A | 224 | 0 | — | `tools/ttk_eval/Extract.cs` | tools/ttk_eval/Extract.cs |
| b | A | 149 | 0 | — | `tools/ttk_eval/Final.cs` | tools/ttk_eval/Final.cs |
| b | A | 112 | 0 | — | `tools/ttk_eval/Live.cs` | tools/ttk_eval/Live.cs |
| b | A | 54 | 0 | — | `tools/ttk_eval/Models.cs` | tools/ttk_eval/Models.cs |
| b | A | 43 | 0 | — | `tools/ttk_eval/Program.cs` | tools/ttk_eval/Program.cs |
| b | A | 98 | 0 | — | `tools/ttk_eval/PullRecord.cs` | tools/ttk_eval/PullRecord.cs |
| b | A | 365 | 0 | — | `tools/ttk_eval/SelfTest.cs` | tools/ttk_eval/SelfTest.cs |
| b | A | 30 | 0 | — | `tools/ttk_eval/TtkEval.csproj` | tools/ttk_eval/TtkEval.csproj |
| b | A | 544 | 0 | — | `tools/vpr_regression/Program.cs` | tools/vpr_regression/Program.cs |
| b | A | 51 | 0 | — | `tools/vpr_regression/VprRegression.csproj` | tools/vpr_regression/VprRegression.csproj |
| b | A | 193 | 0 | — | `tools/vpr_regression/VprRotationEmulator.cs` | tools/vpr_regression/VprRotationEmulator.cs |
| b | A | 782 | 0 | — | `tools/xan_timeline_harness/BlmCombatState.cs` | tools/xan_timeline_harness/BlmCombatState.cs |
| b | A | 466 | 0 | — | `tools/xan_timeline_harness/BurstControl.cs` | tools/xan_timeline_harness/BurstControl.cs |
| b | A | 305 | 0 | — | `tools/xan_timeline_harness/BurstOracle.cs` | tools/xan_timeline_harness/BurstOracle.cs |
| b | A | 82 | 0 | — | `tools/xan_timeline_harness/ClientReject.cs` | tools/xan_timeline_harness/ClientReject.cs |
| b | A | 219 | 0 | — | `tools/xan_timeline_harness/DisengageDriver.cs` | tools/xan_timeline_harness/DisengageDriver.cs |
| b | A | 543 | 0 | — | `tools/xan_timeline_harness/DrgCombatState.cs` | tools/xan_timeline_harness/DrgCombatState.cs |
| b | A | 121 | 0 | — | `tools/xan_timeline_harness/DrgPotencyAudit.cs` | tools/xan_timeline_harness/DrgPotencyAudit.cs |
| b | A | 84 | 0 | — | `tools/xan_timeline_harness/DrgPotencyScorer.cs` | tools/xan_timeline_harness/DrgPotencyScorer.cs |
| b | A | 82 | 0 | — | `tools/xan_timeline_harness/ExecProfile.cs` | tools/xan_timeline_harness/ExecProfile.cs |
| b | A | 201 | 0 | — | `tools/xan_timeline_harness/FightRemainingOverride.cs` | tools/xan_timeline_harness/FightRemainingOverride.cs |
| b | A | 484 | 0 | — | `tools/xan_timeline_harness/GnbCombatState.cs` | tools/xan_timeline_harness/GnbCombatState.cs |
| b | A | 146 | 0 | — | `tools/xan_timeline_harness/GnbPotencyScorer.cs` | tools/xan_timeline_harness/GnbPotencyScorer.cs |
| b | A | 43 | 0 | — | `tools/xan_timeline_harness/IRREGULAR.md` | tools/xan_timeline_harness/IRREGULAR.md |
| b | A | 538 | 0 | — | `tools/xan_timeline_harness/IrregularDriver.cs` | tools/xan_timeline_harness/IrregularDriver.cs |
| b | A | 724 | 0 | — | `tools/xan_timeline_harness/MchCombatState.cs` | tools/xan_timeline_harness/MchCombatState.cs |
| b | A | 142 | 0 | — | `tools/xan_timeline_harness/MchPotencyAudit.cs` | tools/xan_timeline_harness/MchPotencyAudit.cs |
| b | A | 91 | 0 | — | `tools/xan_timeline_harness/MchPotencyScorer.cs` | tools/xan_timeline_harness/MchPotencyScorer.cs |
| b | A | 313 | 0 | — | `tools/xan_timeline_harness/MechanicHintsSelfTest.JobDecisions.cs` | tools/xan_timeline_harness/MechanicHintsSelfTest.JobDecisions.cs |
| b | A | 32 | 0 | — | `tools/xan_timeline_harness/MechanicHintsSelfTest.Jobs.cs` | tools/xan_timeline_harness/MechanicHintsSelfTest.Jobs.cs |
| b | A | 68 | 0 | — | `tools/xan_timeline_harness/MechanicHintsSelfTest.WindDown.cs` | tools/xan_timeline_harness/MechanicHintsSelfTest.WindDown.cs |
| b | A | 107 | 0 | — | `tools/xan_timeline_harness/MechanicHintsSelfTest.cs` | tools/xan_timeline_harness/MechanicHintsSelfTest.cs |
| b | A | 549 | 0 | — | `tools/xan_timeline_harness/MnkCombatState.cs` | tools/xan_timeline_harness/MnkCombatState.cs |
| b | A | 85 | 0 | — | `tools/xan_timeline_harness/MnkPotencyDump.cs` | tools/xan_timeline_harness/MnkPotencyDump.cs |
| b | A | 150 | 0 | — | `tools/xan_timeline_harness/MnkPotencyScorer.cs` | tools/xan_timeline_harness/MnkPotencyScorer.cs |
| b | A | 1003 | 0 | — | `tools/xan_timeline_harness/NinCombatState.cs` | tools/xan_timeline_harness/NinCombatState.cs |
| b | A | 85 | 0 | — | `tools/xan_timeline_harness/NinDecisionLog.cs` | tools/xan_timeline_harness/NinDecisionLog.cs |
| b | A | 267 | 0 | — | `tools/xan_timeline_harness/NinEmulatorSelfTest.cs` | tools/xan_timeline_harness/NinEmulatorSelfTest.cs |
| b | A | 224 | 0 | — | `tools/xan_timeline_harness/NinPlannerSelfTest.cs` | tools/xan_timeline_harness/NinPlannerSelfTest.cs |
| b | A | 207 | 0 | — | `tools/xan_timeline_harness/NinPotencyAudit.cs` | tools/xan_timeline_harness/NinPotencyAudit.cs |
| b | A | 49 | 0 | — | `tools/xan_timeline_harness/NinPotencyScorer.cs` | tools/xan_timeline_harness/NinPotencyScorer.cs |
| b | A | 269 | 0 | — | `tools/xan_timeline_harness/NinReplayScan.cs` | tools/xan_timeline_harness/NinReplayScan.cs |
| b | A | 217 | 0 | — | `tools/xan_timeline_harness/OracleKnowledge.cs` | tools/xan_timeline_harness/OracleKnowledge.cs |
| b | A | 665 | 0 | — | `tools/xan_timeline_harness/OracleSearch.cs` | tools/xan_timeline_harness/OracleSearch.cs |
| b | A | 770 | 0 | — | `tools/xan_timeline_harness/PldCombatState.cs` | tools/xan_timeline_harness/PldCombatState.cs |
| b | A | 145 | 0 | — | `tools/xan_timeline_harness/PldPotencyAudit.cs` | tools/xan_timeline_harness/PldPotencyAudit.cs |
| b | A | 173 | 0 | — | `tools/xan_timeline_harness/PldPotencyScorer.cs` | tools/xan_timeline_harness/PldPotencyScorer.cs |
| b | A | 490 | 0 | — | `tools/xan_timeline_harness/PldReplayCheck.cs` | tools/xan_timeline_harness/PldReplayCheck.cs |
| b | A | 136 | 0 | — | `tools/xan_timeline_harness/PotencyAudit.cs` | tools/xan_timeline_harness/PotencyAudit.cs |
| b | A | 3509 | 0 | — | `tools/xan_timeline_harness/Program.cs` | tools/xan_timeline_harness/Program.cs |
| b | A | 481 | 0 | — | `tools/xan_timeline_harness/RprCombatState.cs` | tools/xan_timeline_harness/RprCombatState.cs |
| b | A | 166 | 0 | — | `tools/xan_timeline_harness/RprPotencyScorer.cs` | tools/xan_timeline_harness/RprPotencyScorer.cs |
| b | A | 695 | 0 | — | `tools/xan_timeline_harness/SamCombatState.cs` | tools/xan_timeline_harness/SamCombatState.cs |
| b | A | 115 | 0 | — | `tools/xan_timeline_harness/SamPotencyAudit.cs` | tools/xan_timeline_harness/SamPotencyAudit.cs |
| b | A | 66 | 0 | — | `tools/xan_timeline_harness/SamPotencyScorer.cs` | tools/xan_timeline_harness/SamPotencyScorer.cs |
| b | A | 385 | 0 | — | `tools/xan_timeline_harness/SearchControl.cs` | tools/xan_timeline_harness/SearchControl.cs |
| b | A | 231 | 0 | — | `tools/xan_timeline_harness/SplatoonImportTest.cs` | tools/xan_timeline_harness/SplatoonImportTest.cs |
| b | A | 184 | 0 | — | `tools/xan_timeline_harness/TimelineHintsValidation.cs` | tools/xan_timeline_harness/TimelineHintsValidation.cs |
| b | A | 689 | 0 | — | `tools/xan_timeline_harness/VprCombatState.cs` | tools/xan_timeline_harness/VprCombatState.cs |
| b | A | 147 | 0 | — | `tools/xan_timeline_harness/VprPotencyAudit.cs` | tools/xan_timeline_harness/VprPotencyAudit.cs |
| b | A | 70 | 0 | — | `tools/xan_timeline_harness/VprPotencyScorer.cs` | tools/xan_timeline_harness/VprPotencyScorer.cs |
| b | A | 71 | 0 | — | `tools/xan_timeline_harness/WindowStateMachineModule.cs` | tools/xan_timeline_harness/WindowStateMachineModule.cs |
| b | A | 52 | 0 | — | `tools/xan_timeline_harness/XanTimelineHarness.csproj` | tools/xan_timeline_harness/XanTimelineHarness.csproj |
| c | M | 14 | 3 | 未変更 | `BossMod/AI/AIBehaviour.cs` | 未適用 (フック表参照) |
| c | M | 48 | 36 | 未変更 | `BossMod/AI/AIManagementWindow.cs` | 未適用 (フック表参照) |
| c | M | 48 | 26 | 未変更 | `BossMod/AI/AIManager.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/ActionQueue/ActionDefinition.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/ActionQueue/ActionQueue.cs` | 未適用 (フック表参照) |
| c | M | 10 | 10 | 未変更 | `BossMod/ActionQueue/Casters/BLM.cs` | 未適用 (フック表参照) |
| c | M | 31 | 1 | 未変更 | `BossMod/ActionQueue/Melee/BST.cs` | 未適用 (フック表参照) |
| c | M | 5 | 2 | 未変更 | `BossMod/ActionTweaks/ActionTweaksConfig.cs` | 未適用 (フック表参照) |
| c | M | 46 | 3 | 未変更 | `BossMod/ActionTweaks/ManualActionQueueTweak.cs` | 未適用 (フック表参照) |
| c | M | 87 | 26 | 未変更 | `BossMod/ActionTweaks/SmartRotationTweak.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/Autorotation/MiscAI/GoToPositional.cs` | 未適用 (フック表参照) |
| c | M | 4 | 1 | **upstreamも変更** | `BossMod/Autorotation/MiscAI/NormalMovement.cs` | 未適用 (フック表参照) |
| c | M | 7 | 6 | 未変更 | `BossMod/Autorotation/Plan.cs` | 未適用 (フック表参照) |
| c | M | 99 | 0 | 未変更 | `BossMod/Autorotation/PlanExecution.cs` | 未適用 (フック表参照) |
| c | M | 2 | 2 | 未変更 | `BossMod/Autorotation/Preset.cs` | 未適用 (フック表参照) |
| c | M | 32 | 5 | 未変更 | `BossMod/Autorotation/RotationModule.cs` | 未適用 (フック表参照) |
| c | M | 21 | 11 | 未変更 | `BossMod/Autorotation/RotationModuleManager.cs` | 未適用 (フック表参照) |
| c | M | 85 | 7 | 未変更 | `BossMod/Autorotation/Strategy.cs` | 未適用 (フック表参照) |
| c | M | 9 | 9 | 未変更 | `BossMod/Autorotation/StrategyRenderer.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/Autorotation/UIRotationWindow.cs` | 未適用 (フック表参照) |
| c | M | 2 | 2 | 未変更 | `BossMod/Autorotation/UIStrategyValue.cs` | 未適用 (フック表参照) |
| c | M | 1 | 0 | 未変更 | `BossMod/BossModReborn.csproj` | Directory.Build.targets で代替 |
| c | M | 17 | 1 | **upstreamも変更** | `BossMod/BossModule/AIHints.cs` | 未適用 (フック表参照) |
| c | M | 58 | 1 | 未変更 | `BossMod/BossModule/AIHintsBuilder.cs` | 未適用 (フック表参照) |
| c | M | 7 | 3 | **upstreamも変更** | `BossMod/BossModule/BossModule.cs` | 未適用 (フック表参照) |
| c | M | 32 | 1 | 未変更 | `BossMod/BossModule/BossModuleConfig.cs` | 未適用 (フック表参照) |
| c | M | 8 | 3 | **upstreamも変更** | `BossMod/BossModule/BossModuleManager.cs` | 未適用 (フック表参照) |
| c | M | 2 | 0 | 未変更 | `BossMod/BossModule/BossModuleRegistry.cs` | 未適用 (フック表参照) |
| c | M | 82 | 0 | 未変更 | `BossMod/BossModule/RaidCooldowns.cs` | 未適用 (フック表参照) |
| c | M | 4 | 4 | 未変更 | `BossMod/BossModule/StateMachine.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/BossModule/ZoneModule.cs` | 未適用 (フック表参照) |
| c | M | 3 | 1 | 未変更 | `BossMod/Components/StackSpread.cs` | 未適用 (フック表参照) |
| c | M | 10 | 10 | 未変更 | `BossMod/Config/AboutTab.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/Config/ConfigConverter.cs` | 未適用 (フック表参照) |
| c | M | 45 | 15 | 未変更 | `BossMod/Config/ConfigUI.cs` | 未適用 (フック表参照) |
| c | M | 65 | 56 | **upstreamも変更** | `BossMod/Config/ModuleViewer.cs` | 未適用 (フック表参照) |
| c | M | 10 | 10 | 未変更 | `BossMod/Config/PartyRolesConfig.cs` | 未適用 (フック表参照) |
| c | M | 2 | 1 | **upstreamも変更** | `BossMod/Data/ActionID.cs` | 未適用 (フック表参照) |
| c | M | 16 | 1 | 未変更 | `BossMod/Data/ActionSpeed.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | **upstreamも変更** | `BossMod/Data/ClientState.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/Debug/DebugAddon.cs` | 未適用 (フック表参照) |
| c | M | 4 | 0 | 未変更 | `BossMod/Debug/DebugParty.cs` | 未適用 (フック表参照) |
| c | M | 87 | 6 | 未変更 | `BossMod/Framework/ActionManagerEx.cs` | 未適用 (フック表参照) |
| c | M | 47 | 5 | 未変更 | `BossMod/Framework/IPCProvider.cs` | 未適用 (フック表参照) |
| c | M | 19 | 0 | 未変更 | `BossMod/Framework/MovementOverride.cs` | 未適用 (フック表参照) |
| c | M | 64 | 2 | 未変更 | `BossMod/Framework/Plugin.cs` | 未適用 (フック表参照) |
| c | M | 2 | 1 | **upstreamも変更** | `BossMod/Framework/Utils.cs` | 未適用 (フック表参照) |
| c | M | 0 | 1 | **upstreamも変更** | `BossMod/Framework/WorldStateGameSync.cs` | 未適用 (フック表参照) |
| c | M | 5 | 2 | 未変更 | `BossMod/Pathfinding/Map.cs` | 未適用 (フック表参照) |
| c | M | 120 | 18 | **upstreamも変更** | `BossMod/Pathfinding/NavigationDecision.cs` | 未適用 (フック表参照) |
| c | M | 13 | 0 | 未変更 | `BossMod/Pathfinding/ObstacleMaps/maplist.json` | 未適用 (フック表参照) |
| c | M | 92 | 4 | 未変更 | `BossMod/Pathfinding/ThetaStar.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/QuestBattle/Dawntrail/MSQ/TheProtectorAndTheDestroyer.cs` | 未適用 (フック表参照) |
| c | M | 6 | 0 | 未変更 | `BossMod/QuestBattle/QuestBattle.cs` | 未適用 (フック表参照) |
| c | M | 12 | 11 | 未変更 | `BossMod/Replay/ReplayBuilder.cs` | 未適用 (フック表参照) |
| c | M | 33 | 4 | **upstreamも変更** | `BossMod/Replay/ReplayManagementWindow.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | **upstreamも変更** | `BossMod/Replay/ReplayParserLog.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | **upstreamも変更** | `BossMod/Replay/ReplayRecorder.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/Timeline/ColumnPlannerTrackStrategy.cs` | 未適用 (フック表参照) |
| c | M | 2 | 1 | 未変更 | `BossMod/Timeline/CooldownPlannerColumns.cs` | 未適用 (フック表参照) |
| c | M | 48 | 4 | 未変更 | `BossMod/Util/BitmapPathfindExtensions.cs` | 未適用 (フック表参照) |
| c | M | 12 | 3 | 未変更 | `BossMod/Util/Intersect.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/Util/ShapeDistance/GenericShapes.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | **upstreamも変更** | `BossMod/Util/ShapeDistance/KnockbackInCircle.cs` | 未適用 (フック表参照) |
| c | M | 1 | 1 | 未変更 | `BossMod/Util/ShapeDistance/Pull.cs` | 未適用 (フック表参照) |
| c | M | 4 | 4 | 未変更 | `BossMod/Util/UICombo.cs` | 未適用 (フック表参照) |
| c | M | 2 | 3 | 未変更 | `BossMod/Util/UITabs.cs` | 未適用 (フック表参照) |
| d | M | 32 | 0 | upstreamで移動/削除 | `.gitignore` | tools/.gitignore へ移設 (upstream側は未変更) |
| d | M | 22 | 7 | **upstreamも変更** | `BossMod/Modules/Dawntrail/Advanced/Ad01TheMerchantsTale/Ad013LoneSwordmaster/Ad013LoneSwordmaster.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | 未変更 | `BossMod/Modules/Dawntrail/Criterion/C01AMT/C011DaryaTheSeaMaid/C011DaryaTheSeaMaidStates.cs` | 未適用 (フック表参照) |
| d | M | 4 | 1 | 未変更 | `BossMod/Modules/Dawntrail/DeepDungeon/PilgrimsTraverse/PTFloorModule.cs` | 未適用 (フック表参照) |
| d | M | 21 | 7 | 未変更 | `BossMod/Modules/Dawntrail/Dungeon/D13TheClyteum/D131EyeoftheScorpion.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | 未変更 | `BossMod/Modules/Dawntrail/Extreme/Ex5Necron/Ex5NecronStates.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | 未変更 | `BossMod/Modules/Dawntrail/Extreme/Ex7Doomtrain/Ex7DoomtrainStates.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | 未変更 | `BossMod/Modules/Dawntrail/Extreme/Ex8Enuo/AddsPhase.cs` | 未適用 (フック表参照) |
| d | M | 1 | 17 | **upstreamも変更** | `BossMod/Modules/Dawntrail/Extreme/Ex8Enuo/NaughtHunts.cs` | 未適用 (フック表参照) |
| d | M | 23 | 1 | 未変更 | `BossMod/Modules/Dawntrail/Raid/M11NTheTyrant/M11NTheTyrant.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | 未変更 | `BossMod/Modules/Dawntrail/Savage/M04SWickedThunder/AI/AIExperiment.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | 未変更 | `BossMod/Modules/Dawntrail/Savage/M06SSugarRiot/M06SSugarRiotConfig.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | **upstreamも変更** | `BossMod/Modules/Dawntrail/Savage/M06SSugarRiot/M06SSugarRiotStates.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | **upstreamも変更** | `BossMod/Modules/Dawntrail/Savage/M09SVampFatale/M09SVampFataleStates.cs` | 未適用 (フック表参照) |
| d | M | 2 | 2 | 未変更 | `BossMod/Modules/Dawntrail/Savage/M10STheXtremes/M10STheXtremesConfig.cs` | 未適用 (フック表参照) |
| d | M | 5 | 5 | 未変更 | `BossMod/Modules/Dawntrail/Savage/M10STheXtremes/M10STheXtremesStates.cs` | 未適用 (フック表参照) |
| d | M | 2 | 18 | 未変更 | `BossMod/Modules/Dawntrail/Trial/T08Enuo/T08Enuo.cs` | 未適用 (フック表参照) |
| d | M | 16 | 1 | 未変更 | `BossMod/Modules/Dawntrail/Ultimate/DMU/DMU.cs` | 未適用 (フック表参照) |
| d | M | 2 | 4 | 未変更 | `BossMod/Modules/Dawntrail/Ultimate/DMU/DMUEnums.cs` | 未適用 (フック表参照) |
| d | M | 290 | 252 | 未変更 | `BossMod/Modules/Dawntrail/Ultimate/DMU/DMUStates.cs` | 未適用 (フック表参照) |
| d | M | 17 | 1 | **upstreamも変更** | `BossMod/Modules/Dawntrail/Ultimate/DMU/Phase1.cs` | 未適用 (フック表参照) |
| d | M | 1 | 2 | **upstreamも変更** | `BossMod/Modules/Dawntrail/Ultimate/DMU/Phase2.cs` | 未適用 (フック表参照) |
| d | M | 32 | 27 | **upstreamも変更** | `BossMod/Modules/Dawntrail/Ultimate/DMU/Phase4.cs` | 未適用 (フック表参照) |
| d | M | 20 | 48 | **upstreamも変更** | `BossMod/Modules/Dawntrail/Ultimate/DMU/Phase5.cs` | 未適用 (フック表参照) |
| d | M | 48 | 22 | upstreamで移動/削除 | `BossMod/Modules/Global/CrucibleOfTheUnbroken/SecondMasterBoard/BoogymanPiece.cs` | 未適用 (フック表参照) |
| d | M | 1 | 2 | upstreamで移動/削除 | `BossMod/Modules/Global/CrucibleOfTheUnbroken/SecondMasterBoard/DrakePiece.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | upstreamで移動/削除 | `BossMod/Modules/Global/CrucibleOfTheUnbroken/SecondMasterBoard/DurgaPiece.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | upstreamで移動/削除 | `BossMod/Modules/Global/CrucibleOfTheUnbroken/SecondMasterBoard/FlaurosPiece.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | upstreamで移動/削除 | `BossMod/Modules/Global/CrucibleOfTheUnbroken/SecondMasterBoard/GigantisPiece.cs` | 未適用 (フック表参照) |
| d | M | 39 | 4 | upstreamで移動/削除 | `BossMod/Modules/Global/CrucibleOfTheUnbroken/SecondMasterBoard/MindFlayerPiece.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | upstreamで移動/削除 | `BossMod/Modules/Global/CrucibleOfTheUnbroken/SecondMasterBoard/SphinxPiece.cs` | 未適用 (フック表参照) |
| d | M | 2679 | 126 | 未変更 | `BossMod/Modules/Global/DeepDungeon/AutoClear.cs` | 未適用 (フック表参照) |
| d | M | 3 | 3 | **upstreamも変更** | `BossMod/Modules/Global/DeepDungeon/Config.cs` | 未適用 (フック表参照) |
| d | M | 98 | 43 | 未変更 | `BossMod/Modules/Global/DeepDungeon/FloorPathfind.cs` | 未適用 (フック表参照) |
| d | M | 145 | 3 | 未変更 | `BossMod/Modules/Global/DeepDungeon/Minimap.cs` | 未適用 (フック表参照) |
| d | M | 166 | 7 | 未変更 | `BossMod/Modules/Global/DeepDungeon/ModuleUI.cs` | 未適用 (フック表参照) |
| d | M | 62 | 0 | 未変更 | `BossMod/Modules/Global/DeepDungeon/TrapData.cs` | 未適用 (フック表参照) |
| d | M | 244 | 37 | 未変更 | `BossMod/Modules/Global/DeepDungeon/Walls.cs` | 未適用 (フック表参照) |
| d | M | 58 | 2 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD100NybethObdilord.cs` | 未適用 (フック表参照) |
| d | M | 24 | 1 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD140AhPuch.cs` | 未適用 (フック表参照) |
| d | M | 164 | 0 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD150Tisiphone.cs` | 未適用 (フック表参照) |
| d | M | 2 | 2 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD170Yulunggu.cs` | 未適用 (フック表参照) |
| d | M | 76 | 6 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD180Dendainsonne.cs` | 未適用 (フック表参照) |
| d | M | 120 | 9 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD190TheGodfather.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD40Ixtab.cs` | 未適用 (フック表参照) |
| d | M | 6 | 4 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD60TheBlackRider.cs` | 未適用 (フック表参照) |
| d | M | 2 | 2 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD70Yaquaru.cs` | 未適用 (フック表参照) |
| d | M | 3 | 3 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD80Gudanna.cs` | 未適用 (フック表参照) |
| d | M | 46 | 11 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/DD90TheGodmother.cs` | 未適用 (フック表参照) |
| d | M | 647 | 15 | 未変更 | `BossMod/Modules/Heavensward/DeepDungeon/PalaceFloorModule.cs` | 未適用 (フック表参照) |
| d | M | 3 | 2 | 未変更 | `BossMod/Modules/Shadowbringers/TreasureHunt/TheShiftingOubliettesOfLyheGhiah/SecretSwallow.cs` | 未適用 (フック表参照) |
| d | M | 1 | 1 | 未変更 | `BossMod/Modules/Stormblood/Trial/T07Byakko/T07Byakko.cs` | 未適用 (フック表参照) |
| d | M | 2 | 2 | **upstreamも変更** | `BossMod/Modules/Stormblood/Ultimate/UCOB/UCOBConfig.cs` | 未適用 (フック表参照) |
| d | A | 12 | 0 | — | `.kimi-dotnet-env.sh` | 未コピー (マシン固有) |
| d | A | 233 | 0 | — | `BossMod/BossModule/DisengageForecast.cs` | Custom/_pending/needs-upstream-hook/BossModule/DisengageForecast.cs (未コンパイル) |
| d | A | 356 | 0 | — | `BossMod/BossModule/ExternalAOEProvider.cs` | Custom/BossModule/ExternalAOEProvider.cs |
| d | A | 218 | 0 | — | `BossMod/BossModule/ExternalEncounterHintProvider.cs` | Custom/BossModule/ExternalEncounterHintProvider.cs |
| d | A | 274 | 0 | — | `BossMod/BossModule/ExternalMechanicHintProvider.cs` | Custom/BossModule/ExternalMechanicHintProvider.cs |
| d | A | 70 | 0 | — | `BossMod/BossModule/FightPriorBuilder.cs` | Custom/_pending/needs-upstream-hook/BossModule/FightPriorBuilder.cs (未コンパイル) |
| d | A | 116 | 0 | — | `BossMod/BossModule/FightPriorStore.cs` | Custom/_pending/needs-upstream-hook/BossModule/FightPriorStore.cs (未コンパイル) |
| d | A | 919 | 0 | — | `BossMod/BossModule/FightTimeEstimator.cs` | Custom/BossModule/FightTimeEstimator.cs |
| d | A | 209 | 0 | — | `BossMod/BossModule/SplatoonLiveZones.cs` | Custom/BossModule/SplatoonLiveZones.cs |
| d | A | 769 | 0 | — | `BossMod/BossModule/SplatoonSafeImport.cs` | Custom/BossModule/SplatoonSafeImport.cs |
| d | A | 29 | 0 | — | `BossMod/Config/Localization.cs` | Custom/Config/Localization.cs |
| d | A | 6 | 0 | — | `BossMod/Config/LocalizationConfig.cs` | Custom/Config/LocalizationConfig.cs |
| d | A | 881 | 0 | — | `BossMod/Config/LocalizationJapanese.cs` | Custom/Config/LocalizationJapanese.cs |
| d | A | 1517 | 0 | — | `BossMod/Config/LocalizationJapaneseStrategy.cs` | Custom/Config/LocalizationJapaneseStrategy.cs |
| d | A | 374 | 0 | — | `BossMod/Modules/Endwalker/Trial/T01Zodiark/T01Zodiark.cs` | Custom/Modules/Endwalker/Trial/T01Zodiark/T01Zodiark.cs |
| d | A | 30 | 0 | — | `BossMod/Modules/Endwalker/Trial/T01Zodiark/T01ZodiarkEnums.cs` | Custom/Modules/Endwalker/Trial/T01Zodiark/T01ZodiarkEnums.cs |
| d | A | 258 | 0 | — | `BossMod/Modules/Global/CrucibleOfTheUnbroken/SecondMasterBoard/AtomosPiece.cs` | Custom/_pending/superseded-by-upstream/ (upstream版あり) |
| d | A | 372 | 0 | — | `BossMod/Modules/Global/DeepDungeon/LiveMapData.cs` | Custom/_pending/needs-upstream-hook/Modules/Global/DeepDungeon/LiveMapData.cs (未コンパイル) |
| d | A | 341 | 0 | — | `BossMod/Timeline/External/AutoTimelineExtractor.cs` | Custom/_pending/needs-upstream-hook/Timeline/External/AutoTimelineExtractor.cs (未コンパイル) |
| d | A | 257 | 0 | — | `BossMod/Timeline/External/AutoTimelineGate.cs` | Custom/_pending/needs-upstream-hook/Timeline/External/AutoTimelineGate.cs (未コンパイル) |
| d | A | 215 | 0 | — | `BossMod/Timeline/External/ExternalPlannerTimeline.cs` | Custom/_pending/needs-upstream-hook/Timeline/External/ExternalPlannerTimeline.cs (未コンパイル) |
| d | A | 1147 | 0 | — | `BossMod/Timeline/External/ExternalTimelineHints.cs` | Custom/_pending/needs-upstream-hook/Timeline/External/ExternalTimelineHints.cs (未コンパイル) |
| d | A | 1 | 0 | — | `BossMod/Timeline/External/ExternalTimelines.json` | Custom/Timeline/External/ExternalTimelines.json |
| d | A | 51 | 0 | — | `BossMod/Timeline/External/PullSummary.cs` | Custom/_pending/needs-upstream-hook/Timeline/External/PullSummary.cs (未コンパイル) |
| d | A | 24 | 0 | — | `BossMod/Timeline/External/ReplacingFile.cs` | Custom/Timeline/External/ReplacingFile.cs |
| d | A | 72 | 0 | — | `BossMod/Timeline/External/ReplaySummaryCache.cs` | Custom/_pending/needs-upstream-hook/Timeline/External/ReplaySummaryCache.cs (未コンパイル) |
| d | A | 491 | 0 | — | `BossMod/Timeline/External/ReplayTimelineExtractor.cs` | Custom/_pending/needs-upstream-hook/Timeline/External/ReplayTimelineExtractor.cs (未コンパイル) |
| d | A | 252 | 0 | — | `BossMod/Timeline/External/TimelineStore.cs` | Custom/_pending/needs-upstream-hook/Timeline/External/TimelineStore.cs (未コンパイル) |
| d | A | 479 | 0 | — | `agents_blm.md` | docs/custom/agents/agents_blm.md |
| d | A | 532 | 0 | — | `agents_gnb.md` | docs/custom/agents/agents_gnb.md |
| d | A | 924 | 0 | — | `agents_mnk.md` | docs/custom/agents/agents_mnk.md |
| d | A | 190 | 0 | — | `agents_nin_minion.md` | docs/custom/agents/agents_nin_minion.md |
| d | A | 686 | 0 | — | `agents_pld.md` | docs/custom/agents/agents_pld.md |
| d | A | 228 | 0 | — | `agents_rpr.md` | docs/custom/agents/agents_rpr.md |
| d | A | 61 | 0 | — | `codex_task_rpr_drift.md` | docs/custom/agents/codex_task_rpr_drift.md |
| d | A | 1764 | 0 | — | `docs/superpowers/plans/2026-09-23-mechanic-hints.md` | docs/custom/superpowers/plans/2026-09-23-mechanic-hints.md |
| d | A | 256 | 0 | — | `docs/superpowers/plans/2026-09-24-nin-burst-selection.md` | docs/custom/superpowers/plans/2026-09-24-nin-burst-selection.md |
| d | A | 185 | 0 | — | `docs/superpowers/plans/2026-09-26-fflogs-source.md` | docs/custom/superpowers/plans/2026-09-26-fflogs-source.md |
| d | A | 731 | 0 | — | `docs/superpowers/plans/2026-09-26-unified-timeline-results.md` | docs/custom/superpowers/plans/2026-09-26-unified-timeline-results.md |
| d | A | 1647 | 0 | — | `docs/superpowers/plans/2026-09-26-unified-timeline.md` | docs/custom/superpowers/plans/2026-09-26-unified-timeline.md |
| d | A | 809 | 0 | — | `docs/superpowers/plans/2026-09-29-auto-timeline-extraction-results.md` | docs/custom/superpowers/plans/2026-09-29-auto-timeline-extraction-results.md |
| d | A | 1476 | 0 | — | `docs/superpowers/plans/2026-09-29-auto-timeline-extraction.md` | docs/custom/superpowers/plans/2026-09-29-auto-timeline-extraction.md |
| d | A | 157 | 0 | — | `docs/superpowers/specs/2026-09-23-mechanic-hints-design.md` | docs/custom/superpowers/specs/2026-09-23-mechanic-hints-design.md |
| d | A | 205 | 0 | — | `docs/superpowers/specs/2026-09-24-nin-burst-selection-design.md` | docs/custom/superpowers/specs/2026-09-24-nin-burst-selection-design.md |
| d | A | 48 | 0 | — | `docs/superpowers/specs/2026-09-26-fflogs-source-design.md` | docs/custom/superpowers/specs/2026-09-26-fflogs-source-design.md |
| d | A | 120 | 0 | — | `docs/superpowers/specs/2026-09-26-unified-timeline-design.md` | docs/custom/superpowers/specs/2026-09-26-unified-timeline-design.md |
| d | A | 58 | 0 | — | `docs/superpowers/specs/2026-09-29-attackability-edge-sync-design.md` | docs/custom/superpowers/specs/2026-09-29-attackability-edge-sync-design.md |
| d | A | 113 | 0 | — | `docs/superpowers/specs/2026-09-29-auto-timeline-extraction-design.md` | docs/custom/superpowers/specs/2026-09-29-auto-timeline-extraction-design.md |
| d | A | 101 | 0 | — | `docs/superpowers/specs/2026-09-29-hp-gated-branches-design.md` | docs/custom/superpowers/specs/2026-09-29-hp-gated-branches-design.md |
| d | A | 7 | 0 | — | `global.json` | global.json |
