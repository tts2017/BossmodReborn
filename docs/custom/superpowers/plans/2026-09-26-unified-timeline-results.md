# 統合タイムライン: 精度ハーネスの計測結果

`tools/timeline_hint_harness` は本物の `ExternalTimelineHints` にリプレイを流し、公開された target-loss ヒントを、同じリプレイから測った NoTarget 窓(8.5 秒以上)と突き合わせる。入力は `%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays` の 47 ファイル(142 MB、全て PLD90 の 2024-04 の記録)、定数は LongLoss 8.5 / FalseAlarmSeconds 30 / LeadCap 25。実行手順・判定基準・各ラウンドの診断は下の付録にそのまま残してある。

## 最終結果(baseline → fix round 3、HEAD 9aec2807e、最終 fix wave は f4ec671f5)

baseline は Task 6 時点(Task 7 の ability sync 実装前、HEAD a5b179659 + スイッチ追加)、最終行は Task 7 fix round 3(HEAD 9aec2807e)の値。`--source ranked` が製品既定(EventTrigger / Cactbot / Replay を順位付けして選ぶ)、`--holdout` は評価対象のリプレイ自身を候補から除いて残りのリプレイだけから作った Replay タイムラインで追う条件。`--gate` はレビュー後の最終 fix wave(f4ec671f5)でハーネスに足したスイッチで、本体の `Plugin.Update(_bossmod.ActiveModule)` → `ExternalTimelineHints.ShouldRun` と同じ判定(登録モジュールの state machine が自明でないゾーンではヒントを出さない)をゾーン単位で模倣し、そのゾーンの窓を分母からも false alarm の真値からも外す。

| 条件 | windows | hit | hit_rate | lead_median | false_alarms | loss_err_median | loss_err_p90 | return_err_median | return_err_p90 | errors |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| baseline: EventTrigger / caststart | 38 | 0 | 0.000 | NaN | 0 | NaN | NaN | NaN | NaN | (列なし) |
| baseline: Cactbot / caststart | 38 | 0 | 0.000 | NaN | 0 | NaN | NaN | NaN | NaN | (列なし) |
| 最終: ranked / all | 41 | 4 | 0.098 | 25.0 | 0 | 0.0 | 0.5 | 0.3 | 1.6 | 0 |
| 最終: EventTrigger / all | 41 | 4 | 0.098 | 25.0 | 0 | 0.0 | 0.5 | 0.3 | 1.6 | 0 |
| 最終: ranked / all / holdout | 41 | 21 | 0.512 | 25.0 | 3 | 0.0 | 3.0 | 0.1 | 8.0 | 0 |
| 最終 fix wave(f4ec671f5): ranked / all | 41 | 4 | 0.098 | 25.0 | 0 | 0.0 | 0.5 | 0.3 | 1.6 | 0 |
| 最終 fix wave(f4ec671f5): ranked / all / holdout | 41 | 21 | 0.512 | 25.0 | 3 | 0.0 | 3.0 | 0.1 | 8.0 | 0 |
| 最終 fix wave(f4ec671f5): ranked / all / **gate** | 20 | 2 | 0.100 | 25.0 | 0 | 0.5 | 0.5 | 1.6 | 1.6 | 0 |
| 最終 fix wave(f4ec671f5): ranked / all / holdout / **gate** | 20 | 6 | 0.300 | 25.0 | 3 | 0.0 | 8.1 | 0.3 | 8.0 | 0 |

- 分母の違い: baseline と Task 7 初回の `windows=38` は、EdensVerseRefulgence 2024_04_16_17_16_08(zone 905)でボスが録画開始直後の 0.5〜0.6 秒だけ ally フラグ付きで記録され、`Participant.WasAlly`(一度でも ally)によって敵一覧から外れて pull が 0 件になっていたため。fix round 1 で敵フィルタを「一度でも非 ally なら参加」に変えたことでこの pull が回復し、真値の窓が 3 つ増えて 41 になった。以後の hit_rate は 41 を分母とするので、hit 4 のままでも 0.105 → 0.098 と見える。
- `errors=` 列は Task 7 でハーネスに追加したもので、baseline の集計行には存在しない。
- 最終 ranked / all と holdout の 2 行は fix round 3 で計測したもの(round 2 と集計行・CSV ともに完全一致)。EventTrigger / all は round 3 では走らせていなかったので Task 8 で同じバイナリで再実行した(集計行は round 2 と同一、実時間 38.3 秒)。
- 当たった 4 窓(非 holdout)は 719(Emanation)、992(The Dark Inside ×2)、1081(Abyssos 5)。holdout の false alarm 3 件はすべて zone 905 で、詳細は「既知の残課題」と付録の fix round 2 節。
- 最終 fix wave の gate なし 2 行は fix round 3 と集計行・CSV ともに完全一致(埋め込みマニフェストから Cactbot の同一双子 306 本を落として 614 → 308 本にしたが、同一内容の候補が消えただけなので選ばれるタイムラインは変わらない)。gate 付き 2 行の読み方は §採否 の注意書きを参照。

## 採否

- `--sync all`(ranked / EventTrigger、非 holdout): hit_rate 0.000 → 0.098、false_alarms 0 → 0、loss_err_median NaN → 0.0。**合格**。
- **注意(最終 fix wave で追記): 上の fix round 3 までの行と、この節の合否判定に使った数値は、すべて本体のモジュールゲートを切った状態(ハーネスが常に `hints.Update(null)` を呼ぶ)で測ったものである。** 本体では `ExternalTimelineHints.ShouldRun` により、自明でない state machine を持つ BMR モジュールが動いているコンテンツではフォロワーは何も公開しない。`--gate` でその判定をゾーン単位で模倣すると、登録モジュールのある 77 ゾーンが対象外になり、真値の窓 41 のうち 21 がそこに属する(1097 Lapis Manalis 13、1118 / 1154 各 2、992 The Dark Inside 2、1178 / 296 各 1)。gate なしの非 holdout 的中 4 窓のうち 992(The Dark Inside ×2)の 2 窓はモジュールのあるゾーンなので本体では出ず、残る 2 窓(719 Emanation、1081 Abyssos 5 はどちらもノーマルでモジュール無し)だけが本体で実際に出る予告に相当する。gate 付きの数値は、非 holdout が windows 20 / hit 2 / hit_rate 0.100 / false_alarms 0 / loss_err_median 0.5 / return_err_median 1.6、holdout が windows 20 / hit 6 / hit_rate 0.300 / false_alarms 3(gate なしと同じ zone 905 の 3 件)/ loss_err_median 0.0 / loss_err_p90 8.1 / return_err_p90 8.0。holdout で消えた 15 的中は 1097(12)、992(2)、1154(1) の窓で、いずれもモジュールのあるゾーンである。つまり本体での実効的な効果は gate 付きの行で読むべきで、gate なしの行は「モジュールが無い(または将来モジュールが外れた)場合の上限」を示す。
- `--holdout`: hit_rate 0.000 → 0.512、loss_err_median 0.0 だが false_alarms 0 → 3。コントローラ裁定(fix round 3、原文どおり): "Accepted: the strict 'false alarms not worse than baseline' gate is vacuous because the baseline publishes nothing (0 hits). Production default (ranked, --sync all) has 0 false alarms with hit rate 0 → 0.098; holdout 21/41 hits, 3 false alarms all from pull-to-pull fight divergence with a single source pull (zone 905), lead 13 s, loss error 0. Deferred follow-up: require ≥ 2 source pulls (or confidence < 0.5 for single-pull windows) before publishing replay windows." → **受理**。

## 既知の残課題

1. **zone 905(Edens Verse Refulgence)の pull 間分岐。** holdout の false alarm 3 件は同じ 2 本のログ(16_17_16_08 / 17_03_27_47)で、35 秒の詠唱が pull ごとに違う(19939 / 19938 の戦闘分岐)うえ 17_03 は開幕ブロックを 100〜164 秒で繰り返す。異なる ID 2 個(20303, 19938)の整合として正当にクロックが飛び、もう一方のログの窓を 13.1 秒後として公開する(次の異なる詠唱で戻る)。同期点の質ではなく戦闘分岐なので、他 pull が 1 本しかない holdout では回避不能。follower 側で「現在のクロックから大きく離れた跳躍には 3 ID 以上を要求する」等は可能だが MinAlignmentScore 相当のしきい値変更なので未実施。
2. **候補が無いゾーン 447 / 517 / 566。** 窓 41 件のうち 447(Limitless Blue EX)、517、566 の計 6 窓は EventTrigger / Cactbot のどちらにも候補が無く、Replay 候補(ユーザーフォルダ)未設定の非 holdout 条件では原理的に当たらない。
3. **保留中のフォローアップ: Replay 窓の公開に ≥ 2 source pull を要求する**(または単一 pull の窓は confidence < 0.5 にして `MinWindowConfidence` 0.5 で落とす)。zone 905 の holdout はもう一方の 1 本だけから作ったタイムライン(窓の信頼度 0.6、pull 1 本)で、これが精度の上限になっている。裁定で deferred。

## 最終 fix wave(f4ec671f5、レビュー後)

変更: 生成器は同ゾーンの EventTrigger と `Sequences` が一致する Cactbot タイムラインを出力しない(現状は 306 本すべてが一致し `manifest_timelines=308 manifest_sequences=658 manifest_states=39733`、`manifest_event_trigger=308 manifest_cactbot=0 manifest_zones=308`。cactbot の txt 310 本のうち event-trigger 側と字面が違うのは dancing_mad.txt の `window` 値とコメントだけで、生成結果には現れない)。`TimelineStore.Generation` を Reload 成功ごとに加算し、フォロワーはゾーン変更に加えて世代が変わったときもタイムラインを引き直す。`OnCombatChanged` の pull 開始時にも store から引き直し、順位が pull ごとに再主張される(前 pull の整合で下位候補に移っていても持ち越さない。タイムラインの実体が変わったら `_lastConfirmed` は捨てる)。窓は読み込み時に Start 順へ整列、埋め込み読込失敗と初回 Reload 失敗をログ、`GenericActionIDs` を `FrozenSet<uint>` に。Check 3 件追加(external_timeline_regression 305/305、timeline_regression 22/22)。ハーネスに `--gate` を追加(上表と §採否 の注意書き)。

| source | sync | holdout | gate | 集計行 | 実時間 |
| --- | --- | --- | --- | --- | --- |
| ranked | all | no | no | `source=ranked sync=all holdout=False gate=False replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0 gated_zones=0 gated_windows=0` | 43.2 s(4 本並列) |
| ranked | all | yes | no | `source=ranked sync=all holdout=True gate=False replays=47 windows=41 hit=21 hit_rate=0.512 lead_median=25.0 false_alarms=3 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=8.0 errors=0 gated_zones=0 gated_windows=0` | 45.1 s |
| ranked | all | no | yes | `source=ranked sync=all holdout=False gate=True replays=47 windows=20 hit=2 hit_rate=0.100 lead_median=25.0 false_alarms=0 loss_err_median=0.5 loss_err_p90=0.5 return_err_median=1.6 return_err_p90=1.6 errors=0 gated_zones=77 gated_windows=21` | 42.1 s |
| ranked | all | yes | yes | `source=ranked sync=all holdout=True gate=True replays=47 windows=20 hit=6 hit_rate=0.300 lead_median=25.0 false_alarms=3 loss_err_median=0.0 loss_err_p90=8.1 return_err_median=0.3 return_err_p90=8.0 errors=0 gated_zones=77 gated_windows=21` | 44.3 s |

gate なしの 2 行は fix round 3 と集計行・CSV(窓ごとの行と false alarm 行)ともに完全一致。gate 付きの的中行は gate なしの同じ窓の行と一致(719 / 1081 の 2 行、holdout は 719 / 1081 / 905 ×4)。

## 最終検証(Task 8、HEAD 9aec2807e、作業ツリー clean)

`dotnet tools/external_timeline_regression/.../ExternalTimelineRegression.dll && dotnet tools/timeline_regression/.../TimelineRegression.dll` の出力:

```
tests=302 passed=302 failed=0; source=external/synthetic; replay=none
tests=22 passed=22 failed=0; source=synthetic; replay=none
```

統合手順(ユーザー実施): `nin-prekassatsu3rd-stability` への取り込みは `git -C F:/bossmodreborn merge --ff-only claude/content-timeline-review-d2946e`(本体に未コミット変更 MovementOverride.cs / MchCombatState.cs があるので、merge の前後で `git status` により保持を確認)。配置は memory `branch-layout-nin-stability` の手順(`-p:Platform=x64` 必須)。

---

# 付録: ラウンド別の詳細ログ

以下は計測の時系列そのまま(baseline → Task 7 → fix round 1〜3)。見出しを 1 段下げ、冒頭の入力・定数の説明を上と重複するので省いた以外は変更していない。ハーネスの実行形は `dotnet tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll --source <S> --sync <caststart|all> [--holdout] <replays>`。

### Baseline(Task 6、Task 7 の ability sync 実装前、HEAD a5b179659 + スイッチ追加)

判定基準(レビュー fix round 1 で確定): hit は窓開始前の公開で予測 loss が `[start−5, end]` に入るもの。false alarm は予測 loss がどの NoTarget 窓(長さ不問)にも `[start−5, end]` で入らず、かつ 30 秒以内にどの窓も始まらないもの。`windows=` と hit/lead/error は 8.5 秒以上の窓のみ。

| source | sync | 集計行 | 実時間 |
| --- | --- | --- | --- |
| EventTrigger | caststart | `source=EventTrigger sync=caststart holdout=False replays=47 windows=38 hit=0 hit_rate=0.000 lead_median=NaN false_alarms=0 loss_err_median=NaN loss_err_p90=NaN return_err_median=NaN return_err_p90=NaN` | 36.7 s |
| Cactbot | caststart | `source=Cactbot sync=caststart holdout=False replays=47 windows=38 hit=0 hit_rate=0.000 lead_median=NaN false_alarms=0 loss_err_median=NaN loss_err_p90=NaN return_err_median=NaN return_err_p90=NaN` | 37.4 s |
| Replay | caststart | `source=Replay sync=caststart holdout=False replays=47 windows=38 hit=0 hit_rate=0.000 lead_median=NaN false_alarms=0 loss_err_median=NaN loss_err_p90=NaN return_err_median=NaN` (修正前のハーネス、`return_err_p90` 列なし) | 39.2 s |

`--source Replay` は埋め込みマニフェストに Replay 候補が無い(ユーザーフォルダ未設定)ため候補なし、窓の数だけが出る。この時点では `--sync all` は `caststart` と同値(`UseAbilitySync` は宣言のみ)。

### 所見(baseline の 0 件の内訳)

計測用の一時的な診断(コミットしていない)で確認した内容:

- 窓 38 件は 16 ゾーンにまたがる。うち 3 ゾーン(447 Limitless Blue EX、517、566: 計 6 窓)は EventTrigger / Cactbot のどちらにも候補が無い。残り 13 ゾーン 32 窓には両ソースの候補がある。
- 埋め込みマニフェストの状態は AbilityUsed(Kind 5)が 55,326 件、CastStart(Kind 1)が 2,598 件。現行のフォロワーは CastStart でしか同期しないため、リプレイ中の敵アクションのうち CastStart 項目に一致するのは 19 ゾーン合計で 66 件、AbilityUsed 項目に一致するのは 1,277 件。窓が最も多い 1097(Lapis Manalis、13 窓、52 pull)は CastStart 項目が 0 件で一致 0、AbilityUsed 一致 270 件。
- 47 本のうち同期(`Synced`)が一度でも立ったのは 7 本(P1S ×3、Castrum Fluminis、Euphrosyne、Mount Ordeals、Thaleia)。Euphrosyne は同期中 15,528 tick、`NextDowntime` が有限だった tick が 5,058 あるが、そのすべてで lossIn が Horizon(25 秒)を超えており、直近の同期(SyncLifetime 60 秒)が切れてから窓が来る。公開直前のフィルタで落ちているのであって、`ExternalMechanicHintProvider` への push/読み出し経路は通っている(公開可能 tick が 0 なので当然 0 件)。
- EventTrigger と Cactbot の集計と一致数が完全に同じなのは、両候補が同じ cactbot テキスト(例: `lapis_manalis.txt`)から変換されているため。

Task 7 で AbilityUsed 同期(`UseAbilitySync`)を入れた後、同じ 3 コマンドをこの表の下に追記して比較する。

### Task 7(フォロワー改修後、HEAD 279e2a3df + Task 7 作業ツリー)

改修内容: `CastEvent` 購読による AbilityUsed 同期(`UseAbilitySync`)、候補タイムライン全体を走査する整合(`AlignmentScore` を純関数化)、BossOIDs / 直前に確認した Sequence の次を使う Sequence 選択、追跡敵を最大 HP の敵に、`CheckAgainstWorld` を「予測 NoTarget」対「攻撃可能な敵が 1 体でもいるか」に、`NextDowntime` を `SelectDowntime`(NoTarget 窓、`MinWindowConfidence` 0.5、短い窓が長い窓を隠さない)に。ハーネス側は `P90` を nearest-rank に、`errors=` 列を追加、false alarm を CSV に `pull=-1` 行(loss/return はリプレイ先頭からの秒)で出力するようにした。

実行: 同じ 47 ファイル。実時間は 5 条件を直列(1 本目〜5 本目)、holdout を並列に走らせたときの値。

| source | sync | holdout | 集計行 | 実時間 |
| --- | --- | --- | --- | --- |
| EventTrigger | caststart | no | `source=EventTrigger sync=caststart holdout=False replays=47 windows=38 hit=2 hit_rate=0.053 lead_median=5.5 false_alarms=0 loss_err_median=0.1 loss_err_p90=0.1 return_err_median=0.1 return_err_p90=0.1 errors=0` | 40.8 s |
| EventTrigger | all | no | `source=EventTrigger sync=all holdout=False replays=47 windows=38 hit=4 hit_rate=0.105 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0` | 39.8 s |
| Cactbot | all | no | `source=Cactbot sync=all holdout=False replays=47 windows=38 hit=4 hit_rate=0.105 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0` | 39.2 s |
| ranked | all | yes | `source=ranked sync=all holdout=True replays=47 windows=38 hit=17 hit_rate=0.447 lead_median=25.0 false_alarms=22 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=9.0 errors=0` | 47.1 s |
| ranked | all | no | `source=ranked sync=all holdout=False replays=47 windows=38 hit=4 hit_rate=0.105 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0` | 39.4 s |
| Replay | all | no | `source=Replay sync=all holdout=False replays=47 windows=38 hit=0 hit_rate=0.000 lead_median=NaN false_alarms=0 loss_err_median=NaN loss_err_p90=NaN return_err_median=NaN return_err_p90=NaN errors=0`(ユーザーフォルダ未設定で候補なし) | 35.8 s |

2 回走らせて全条件の集計行はビット同一。

#### ゲート判定

- `--sync all`(EventTrigger / Cactbot / ranked、非 holdout): hit_rate 0.000 → 0.105、false_alarms 0 → 0、loss_err_median NaN → 0.0。**合格**。当たった窓は 719(Emanation)、992(The Dark Inside ×2)、1081(Abyssos 5)。
- `--holdout`: hit_rate 0.000 → 0.447、loss_err_median NaN → 0.0 だが false_alarms 0 → **22 で不合格**。原因は下の 2 件で、どちらも follower のしきい値調整では直せない。
- 補足: `--sync caststart` でも 992 の 2 窓が当たるようになった(baseline 0)。これは ability sync ではなく CheckAgainstWorld / 追跡敵 / Sequence 選択の変更によるもの。

#### holdout の false alarm 22 件の内訳(`--csv` の `pull=-1` 行)

1. **19 件: EdensVerseRefulgence 2024_04_16_17_16_08(zone 905)— 抽出の pull 区切り。** このログは `FindPulls` が pull 0 件を返す(encounters=0)。ボス(OID 2D73、MaxHP 6.7M、詠唱 19 回、1〜468 秒存在)が録画開始直後の 0.5〜0.6 秒だけ ally フラグ付きで記録されており、`Participant.WasAlly`(一度でも ally)で敵一覧から除外される。残る最大 HP は雑魚 2D83(715k)で、その唯一の詠唱 451.8 秒を pull 先頭とすると 16 秒 < MinPullSeconds 20 で捨てられる。真値の窓が 0 件なので、このログへの予告は全て false alarm になる。実際にはボスは 202〜308 秒と 378〜468 秒で untargetable で、予告 3 窓のうち 201.6〜230.4(雑魚出現 230/244 まで NoTarget)と 378〜450 の 2 窓は正しく、68.1〜96.8 だけが誤り(他方のログから作った窓 271.8〜300.6 [28.8 秒] を約 204 秒早く予測したもので、下の 2. と同じ機構)。5 秒刻みの再公開で 19 件に数えられている。
2. **3 件: LapisManalis ×3(zone 1097)— 同期点の誤整合(周期的な同一 ID)。** 抽出した 1097 のタイムラインは同期点 187 件のうち 71 件(38%)が汎用オートアタック(ID 870 / 872)で、約 3 秒おきに並ぶ。3 秒間隔の 2 発はタイムライン上のどの 3 秒間隔の 2 発とも整合するので、MinAlignmentScore 2 の裏付けが効かず、クロックが 87.8 ↔ 98.5 ↔ 224.8 と飛ぶ(トレース: 12_44_56 の 1415.5 秒と 1467.6 秒)。ボス 3 撃破(1470 秒)の直後に雑魚のオートで 98.5 秒へ戻され、1476.5 秒に「1501.5〜1523.8 の 22.3 秒ロス」(ボス 3 の実窓 128.8〜151.1 の再現)を公開した。04_04_42 のボス 1 撃破後(114.2 秒)も同じ。zone 905 も同期点 108 件中 53 件が ID 19923。この機構は holdout の loss_err_p90 3.0 / return_err_p90 9.0 のばらつきにも効いていると考えられる。

#### 対処候補(未実施、要判断)

- 1. は抽出側: `WasAlly` を「pull 内で ally だった時間」に変える、または ally 区間を WorldExistence から差し引く。
- 2. は抽出側で汎用オートアタック(870/872 など、あるいは「同 pull 内で N 回以上繰り返す ID」)を同期点から外すか、follower 側で `AlignmentScore` の裏付けに ID の異なる履歴を要求する。どちらも follower のしきい値変更ではない。

### Task 7 fix round 1(コントローラ裁定 2 件を適用)

変更: (1) `ReplayTimelineExtractor.FindPulls` の敵フィルタを「一度でも ally」除外から「一度でも非 ally なら参加」(`AllyHistory.Count == 0 || AllyHistory.Any(kv => !kv.Value)`)に。瞬間的な ally 区間は `NoTargetWindows` 内の `AllyAt` 判定が既に除いている。(2) 汎用オートアタック ID `{7, 8, 870, 871, 872, 873}` を `ReplayTimelineExtractor.GenericActionIDs` として共有し、抽出(詠唱・効果とも)と follower(`Observe` / `AlignmentScore`)の両方で同期点から除外。Check 3 件追加(timeline_regression 18/18、external_timeline_regression 302/302)。

効果: 1097 の同期点 187 → 116。Refulgence 16_17_16_08 に pull が立ち、真値の窓が 38 → 41 に増えた(905 の 3 窓)。

| source | sync | holdout | 集計行 | 実時間 |
| --- | --- | --- | --- | --- |
| ranked | all | no | `source=ranked sync=all holdout=False replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0` | 42.8 s |
| EventTrigger | all | no | `source=EventTrigger sync=all holdout=False replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0` | 39.7 s |
| ranked | all | yes | `source=ranked sync=all holdout=True replays=47 windows=41 hit=21 hit_rate=0.512 lead_median=25.0 false_alarms=3 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=7.9 errors=0` | 45.8 s(他 2 本と並列) |

#### ゲート判定(fix round 1)

- `--sync all`(ranked / EventTrigger): hit 4 のまま、false_alarms 0、loss_err_median 0.0。分母が 41 になったので hit_rate は 0.105 → 0.098 だが、baseline(0.000)比では上昇。**合格**。
- `--holdout`: hit 17 → 21(hit_rate 0.512)、false_alarms 22 → **3**、loss_err_median 0.0、return_err_p90 9.0 → 7.9。false_alarms が baseline の 0 を上回る(判定は fix round 2 の後のコントローラ裁定を参照)。残り 3 件は下記のとおり同じ 1 機構。

#### 残る holdout false alarm 3 件(zone 905 Edens Verse Refulgence)

| log | 予測 loss-return(リプレイ秒) | lead |
| --- | --- | --- |
| 2024_04_16_17_16_08 | 68.1-96.8 | 13.1 s |
| 2024_04_17_03_27_47 | 60.0-88.8 | 13.1 s |
| 2024_04_17_03_27_47 | 149.9-178.7 | 13.1 s |

原因: 同期点の誤整合(ボス固有の周期アクション)。zone 905 の同期点 108 件のうち 53 件はボス自身の 3 秒周期アタック(ID 19923、汎用集合には含まれない)。03_27_47 のトレースでは 46.9 秒の詠唱 19938(0x4DE2)開始時に、履歴の 19923 が 3 秒間隔でどこにでも整合するため MinAlignmentScore 2 の裏付けが成立し、クロックが 27.3 → 169.0 へ飛んで、他方のログの窓 182.1-210.9 を「13.1 秒後」として公開した(同じ理由で 136.8 秒にも再発。16_17_16_08 は逆向きの同じ機構)。次の詠唱(60.1 秒、20302)で 48.7 へ戻り公開は消える。3 件とも false alarm 1 件が 5 秒バケット 1 個に収まるので 3 行。zone 905 の holdout はもう一方の 1 本だけから作ったタイムライン(窓の信頼度 0.6、pull は 1 本)である点も精度の上限になっている。

対処候補(未実施): 抽出側で「同 pull 内で N 回以上繰り返す ID」を同期点から外す(ボス固有オートの一般化)、または follower の `AlignmentScore` の裏付けに ID の異なる履歴を要求する。どちらもしきい値調整ではないので裁定待ち。

### Task 7 fix round 2(裁定 2 件: 整合を「異なる ID の数」で数える、pull 内で支配的な ID を同期点から外す)

変更: (1) `AlignmentScore` は一致した履歴項目の数ではなく一致した action ID の集合の大きさを返す(同じ ID が複数オフセットで一致しても 1)。(2) `ReplayTimelineExtractor.Build` は pull ごとにボスの詠唱+効果イベントを数え、ある ID の出現がその `DominantSyncShare`(0.25)を超えるとその ID を同期点から外す。**注: 出現 1 回の ID は除外対象にしない**(合成 pull は 2〜4 イベントしかなく、裁定どおりの割合だけだと 3 イベントの pull の全 ID が 33% で消えて既存 Check 4 件が落ちた。「repeater は繰り返している」という定義上の下限で、しきい値ではない)。Check 2 件追加(timeline_regression 20/20、external_timeline_regression 302/302)。

効果: zone 905 の同期点 108 → 55(ID 19923 が消えた)、1097 は 116 → 90。

| source | sync | holdout | 集計行 | 実時間 |
| --- | --- | --- | --- | --- |
| ranked | all | no | `source=ranked sync=all holdout=False replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0` | 42.5 s |
| EventTrigger | all | no | `source=EventTrigger sync=all holdout=False replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0` | 40.9 s |
| ranked | all | yes | `source=ranked sync=all holdout=True replays=47 windows=41 hit=21 hit_rate=0.512 lead_median=25.0 false_alarms=3 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=8.0 errors=0` | 46.0 s(並列) |

#### ゲート判定(fix round 2)

- `--sync all`(ranked / EventTrigger): fix round 1 と同一(hit 4、false_alarms 0、loss_err_median 0.0)。**合格**。
- `--holdout`: hit 21 / false_alarms 3 / loss_err_median 0.0、round 1 と同じ 3 行が残る(return_err_p90 7.9 → 8.0 のみ変化)。**コントローラ裁定(fix round 3、原文どおり): "Accepted: the strict 'false alarms not worse than baseline' gate is vacuous because the baseline publishes nothing (0 hits). Production default (ranked, --sync all) has 0 false alarms with hit rate 0 → 0.098; holdout 21/41 hits, 3 false alarms all from pull-to-pull fight divergence with a single source pull (zone 905), lead 13 s, loss error 0. Deferred follow-up: require ≥ 2 source pulls (or confidence < 0.5 for single-pull windows) before publishing replay windows."**

#### 残る 3 件の原因(round 1 の診断を訂正)

3 行とも zone 905 Edens Verse Refulgence、同じ 2 本のログ(16_17_16_08 / 17_03_27_47)で、holdout では互いがもう一方の唯一の情報源になる。各ログを単独で抽出した詠唱列:

- 16_17: `10:19927 23:20303 35:19939 49:20302 61:19932 74:19930 87:19937 115:19928 129:19924 139:19931 157:20303 169:19938 184:19945 ...`
- 17_03: `10:19927 23:20303 35:19938 49:20302 61:19932 74:19930 100:19927 113:20303 125:19938 138:20302 151:19932 164:19930 177:19937 205:19928 218:19924 228:19931 247:20303 259:19939 273:19945 ...`

35 秒の詠唱が pull ごとに違う(19939 か 19938: 戦闘のランダム分岐)うえ、17_03 は開幕ブロックを 100〜164 秒でもう一度繰り返す。17_03 を 16_17 のタイムラインで追うと、46.9 秒の 19938 開始時点で「20303 の 12 秒後に 19938」が成り立つ場所は 157→169 の 1 か所しかなく、異なる ID 2 個(20303, 19938)の整合として正当にそこへ飛び、16_17 の窓 182.1〜210.9 を 13.1 秒後として公開する(60.0〜88.8 と 149.9〜178.7 の 2 行。繰り返しブロックで同じことが 136.8 秒にも起きる)。16_17 側は鏡像で、35 秒の 19939 が 17_03 のタイムラインでは 247:20303 → 259:19939(同じ 12 秒間隔)にしかないため 259 へ飛び、窓 271.8〜300.6 を 68.1〜96.8 として公開する。次の異なる詠唱(60.1 秒の 20302)で正しい位置に戻り公開は消える。

つまり残りは同期点の質ではなく pull 間の戦闘分岐で、他 pull が 1 本しかない holdout では回避不能(多数決の効く pull 数があれば分岐専用の同期点は majority で落ちる)。follower 側で「現在のクロックから大きく離れた場所への跳躍には 3 ID 以上を要求する」等の対処は可能だが、それは MinAlignmentScore 相当のしきい値変更なので裁定待ち。

### Task 7 fix round 3(タスクレビュー指摘 1・3・4)

変更: (1) `PredictedNoTargetAt(sequence, now, minConfidence)` を純関数化し、`CheckAgainstWorld` の世界照合でも信頼度 0.5 未満の NoTarget 窓を無視(`SelectDowntime` / `OnTargetableChanged` と同じ基準。低信頼窓が正しいクロックを矛盾扱いで落とす経路を塞ぐ)。(3) 同期中の毎フレーム経路から LINQ を排除: `CheckAgainstWorld` の `Actors.Any`、`PredictedNoTarget` のクロージャ、`SelectDowntime` の `Where`+`OrderBy` を foreach に。窓は抽出器が Start 順に並べて保存する(ユーザー JSON も抽出器出力)ので格納順のまま走査し、`Start > end` / 地平線超えで break。(4) `OnTargetableChanged` は `IsDeadOrDestroyed` で早期 return、一致判定を `MatchTargetableEdge(sequence, now, wantedTargetable, tolerance, minConfidence)` に切り出し。Check 2 件追加(timeline_regression 22/22、external_timeline_regression 302/302)。指摘 2 は上の裁定を記録(コード変更なし)。

| source | sync | holdout | 集計行 | 実時間 |
| --- | --- | --- | --- | --- |
| ranked | all | no | `source=ranked sync=all holdout=False replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0` | 41.5 s |
| ranked | all | yes | `source=ranked sync=all holdout=True replays=47 windows=41 hit=21 hit_rate=0.512 lead_median=25.0 false_alarms=3 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=8.0 errors=0` | 43.7 s(並列) |

round 2 と集計行・CSV(窓ごとの行と false alarm 行)ともに完全一致。ゲート: `--sync all` 合格、`--holdout` は上記裁定どおり受理(残 3 件は zone 905 の単一 pull 由来)。

## FFLogs ソース(Task 3、HEAD d4f292189 + ハーネス `--timelines`)

日付: 2026-09-26。仕様 `docs/superpowers/specs/2026-09-26-fflogs-source-design.md`。FFLogs の公開レポートから `tools/fflogs_timeline_extract` で生成したタイムラインを、自リプレイを真値にして `tools/timeline_hint_harness` で測った。ハーネスには `--timelines <dir>` を足し(`TimelineStore.UserDirectory` を差し替えて `Reload`)、集計行に `timelines=<dir or ->` を出す。

### 仕様 §3 からの逸脱(Task 2 で採用、ここに記録)

合成 `Replay` の Participant の存在区間は「最初のイベント 〜 death、death が無ければ **その actor の最後のイベント**」(仕様は fight 終了)。実データでは死なないヘルパー actor が fight 終了まで生き続けて、5 秒ギャップの pull 分割を橋渡しし、ボス 2・3(fight によっては 1 も)が 1 つの pull に融合した。最後のイベントで閉じると期待どおりボスごとに 1 sequence になる。副作用: death の無いボス(FFLogs が death を記録しないトライアル)は kill 時刻ではなく最後の詠唱で終わる(pull 末尾が数秒縮むだけ)。

### 生成(`%TEMP%\tl_fflogs`、各 `--reports 100 --max-fights 8`)

| zone | コマンド | fights | sequences(ボス OID) | windows | no_target | confidence | points_spent(時間累計) |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1304 San d'Oria | `--territory 1304 --fflogs-zone 70`(`--encounter` 無し: `fights(encounterID: null)` が実 API で機能、4 ボス分の fight が 1 回で取れた) | 8(listing 50 reports / 1,506 fights) | 4: `0x493C` / `0x4918,0x4919` / `0x48F8` / `0x460F` | 8 | 0 | 0.69 | 471 |
| 1345 Clyteum | `--territory 1345 --fflogs-zone 57 --encounter 4551` | 8(listing 50 reports / 35 fights) | 3: `0x4C2C` / `0x4C3F` / `0x4C28`(自リプレイ版と同じ 3 ボス) | 4 | 0 | 0.73 | 563 |
| 1203 Tender Valley | `--territory 1203 --fflogs-zone 57 --encounter 4540`(Task 2 で生成済み、再実行なし) | 5 | 3: `0x4234` / `0x41BE` / `0x4164` | 0 | 0 | 0.60 | (379) |

生成物の窓の内訳(自リプレイ版と並べる):

| zone / ボス | FFLogs 由来 | 自リプレイ由来(`timelines\<zone>-replay.json`) |
| --- | --- | --- |
| 1345 `0x4C3F` | BossUntargetable+AddsPresent 17.7–26.9、84.1–98.5 | **NoTarget**+BossUntargetable 21.9–31.0、88.4–102.7 |
| 1304 `0x460F` | BossUntargetable+AddsPresent 149.6–179.8 | **NoTarget**+BossUntargetable 155.7–185.9 |
| 1304 `0x4919` | BossUntargetable+AddsPresent 112.4–155.0、113.5–140.1 | **NoTarget**+BossUntargetable 100.8–127.3(信頼度 0.2) |
| 1304 `0x493C` | BossUntargetable+AddsPresent 201.0–248.8 | 窓なし |

窓の長さは一致する(Clyteum 9.2 s / 14.4 s、San d'Oria `0x460F` 30.2 s)が、開始が 4.2〜6.1 秒早い(pull anchor の取り方の差。詠唱同期で吸収される範囲)。**種類が違う**: FFLogs 由来は全て BossUntargetable+AddsPresent で、NoTarget が 1 つも無い。

### 計測(`%TEMP%\tl_eval`: Clyteum 12 本 = BLM100 4・WAR100 4・GNB100 2・RPR100_Vi_Ni 2、San d'Oria 5 本、計 17 本、553 MB)

注意: `%TEMP%\tl_eval` の Clyteum 12 本は、ジョブ別フォルダごとに日付の早い方から BLM 4・WAR 4・GNB 2・RPR_Vi_Ni 2 を選んだもので、自リプレイ版 `1345-replay.json` を抽出したログ集合(プラグイン設定フォルダの全リプレイ)とは一致しない可能性がある。(c) 自リプレイ条件を「真値を作ったログそのもの」として読むのはその範囲での話で、FFLogs 条件との比較は同じ 17 本で行っているので相対比較には影響しない。

| 条件 | 集計行 | 実時間 |
| --- | --- | --- |
| (a) FFLogs 由来(helper rule 前) | `source=FFLogs sync=all holdout=False gate=False timelines=%TEMP%\tl_fflogs replays=17 windows=38 hit=0 hit_rate=0.000 lead_median=NaN false_alarms=0 loss_err_median=NaN loss_err_p90=NaN return_err_median=NaN return_err_p90=NaN errors=0 gated_zones=0 gated_windows=0` | 55.6 s |
| (b) 組み込みのみ(`--timelines` 無し) | `source=ranked sync=all holdout=False gate=False timelines=- replays=17 windows=38 hit=0 hit_rate=0.000 lead_median=NaN false_alarms=0 loss_err_median=NaN loss_err_p90=NaN return_err_median=NaN return_err_p90=NaN errors=0 gated_zones=0 gated_windows=0` | 52.6 s |
| (c) 自リプレイ由来(`timelines\`、holdout ではない = 上限) | `source=Replay sync=all holdout=False gate=False timelines=%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\timelines replays=17 windows=38 hit=31 hit_rate=0.816 lead_median=24.9 false_alarms=3 loss_err_median=0.0 loss_err_p90=0.3 return_err_median=0.1 return_err_p90=0.3 errors=0 gated_zones=0 gated_windows=0` | 56.2 s |

- (b) は組み込みマニフェストに 1345 / 1304 が無いので何も測っていない(0 の確認のみ)。
- (c) は真値を作ったログそのもので測っている(holdout なし)ので上限。内訳: 1345 は 28 窓中 26 hit(外れ 2 は 00_07_27 の 402 s / 474 s = ボス 2 を 3 回引き直した後半の窓)、1304 は 10 窓中 5 hit(`0x460F` の 5 本は全 hit、`0x4919` の 5 本は信頼度 0.2 の窓なので publish されない)。false alarm 3 件は 00_07_27 の 928 s 付近 2 件と RPR 09_02 の 1435 s 付近 1 件で、いずれも同じログ内の再戦・後半の窓に対する予告。

### 所見

**FFLogs 由来のデータは自リプレイの NoTarget 窓に 1 つも当たらない(hit 0 / 38)。false alarm も 0。** 当たらない理由は同期でも時刻でもなく窓の種類で、フォロワーは NoTarget 窓しか publish しない(`ExternalTimelineHints.PredictedNoTargetAt` / `SelectDowntime`)のに、FFLogs 由来のタイムラインには NoTarget 窓が無い。

原因は合成 `Replay` の targetability の初期値: `FFLogsReplayBuilder` は全 actor を「最初のイベントで targetable = true」にし、以後 `targetabilityupdate` を反映する。FFLogs の `targetabilityupdate` は変化しか出ないので、最初から狙えないヘルパー actor(ボス消失中も詠唱している helper、Clyteum の fight あたり 26〜31 体の enemies の大半)には一度も update が来ず、ずっと「狙える敵」として存在する。`ReplayTimelineExtractor.NoTargetWindows` は「存在し・targetable で・死んでおらず・味方でない敵が 1 体も無い」ことを要求するので、ボス消失中も常に誰かが「狙える」扱いになり、NoTarget ではなく AddsPresent に分類される。

つまり現状の FFLogs ソースは、同期点(詠唱列)としては自リプレイ版と同じボスを同じ順で捉えているが、ダウンタイム予告としては空(無害だが無益)。Tender Valley のように消失フェーズの無いダンジョンでは元々 windows=0 なので差は無い。

次の手(未実施、裁定待ち): helper の判別。候補は (1) Boss でなく death も無い actor は targetable 初期値を false にする(トラッシュはダンジョン kill fight では必ず死ぬ。消失フェーズで倒さず退場する add を helper 扱いしてしまう危険がある = 偽 NoTarget)、(2) masterData の actor 情報(`subType` 以外に使えるものがあるか)で helper を除く、(3) 抽出器側で「HP を持たない/一度も攻撃対象にならなかった actor」を attackable から外す(FFLogs の `damage-taken` を引くと 1 fight あたり数 pt 追加)。どれも合成規則の変更なので Task 2 の仕様 §3 を改訂してから。

### 配置

(a) の false alarm は 0 なので、ブリーフどおり `%TEMP%\tl_fflogs\1203-fflogs.json`(Tender Valley、windows=0)を `%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\timelines\` に置いた。組み込みマニフェストに 1203 は無いので上書きする相手は無く、NoTarget 窓が無いのでヒント発行も無い(実質は同期点だけ)。1345 / 1304 は自リプレイ版が順位で勝つので置かない。

### helper rule 後(ツール修正 441e05d58: プレイヤーのダメージを一度も受けていない非ボス actor は出現時から untargetable の helper とする)

再生成(同じ `--reports 100 --max-fights 8`、生成物は `%TEMP%\tl_fflogs` を上書き。fight 数は生成物の `SourceFile` から数えた。points は本エージェントでは未計測):

| zone | fights | sequences(ボス OID) | windows | no_target | confidence |
| --- | --- | --- | --- | --- | --- |
| 1345 Clyteum | 8 | 3: `0x4C2C` / `0x4C3F` / `0x4C28` | 4 | **2**(`0x4C3F` 17.7–26.9、84.2–98.5) | 0.73 |
| 1304 San d'Oria | 8 | 4: `0x493C` / `0x4918,0x4919` / `0x48F8` / `0x460F` | 7 | **2**(`0x4918,0x4919` 113.7–140.3、`0x460F` 149.8–180.0、信頼度 0.97 / 0.60) | 0.69 |

自リプレイ版との対応: Clyteum `0x4C3F` 21.9–31.0 / 88.4–102.7(FFLogs は 4.2 s 早い、長さ 9.2 / 14.3 s で一致)、San d'Oria `0x460F` 155.7–185.9(FFLogs は 5.9 s 早い、長さ 30.2 s で一致)、`0x4919` 100.8–127.3(FFLogs は 2 ボスを 1 sequence に融合していて 12.9 s 遅い、長さ 26.5 s で一致)。

計測(同じ `%TEMP%\tl_eval` 17 本):

| 条件 | 集計行 | 実時間 |
| --- | --- | --- |
| (a) FFLogs 由来(helper rule 後) | `source=FFLogs sync=all holdout=False gate=False timelines=%TEMP%\tl_fflogs replays=17 windows=38 hit=36 hit_rate=0.947 lead_median=24.8 false_alarms=35 loss_err_median=0.0 loss_err_p90=0.2 return_err_median=0.0 return_err_p90=5.4 errors=0 gated_zones=0 gated_windows=0` | 54.1 s |
| (a2) 同上 `--csv %TEMP%\tl_fflogs_eval.csv` | 同一の集計行 | 54.4 s |

#### 窓ごとの結果(38 窓、`%TEMP%\tl_fflogs_eval.csv`)

| zone / ボス | 窓(pull 秒) | 本数 | hit | lead | loss 誤差 / return 誤差 |
| --- | --- | --- | --- | --- | --- |
| 1345 `0x4C3F` 第 1 窓 | 21.6–31.1(9.1–9.2 s) | 13(00_07_27 の pull 1・2 と 157.9 の 2 周目を含む) | 13/13 | 17.5–17.8 | loss 0.0〜0.2、return 0.0(7 本)/ +5.4(6 本) |
| 1345 `0x4C3F` 第 2 窓 | 87.8–103.2(14.3 s) | 13(224.7 の 2 周目を含む) | 13/13 | 24.8–25.0 | loss 0.0〜0.2、return 0.0〜0.3(11 本)/ −5.2(2 本) |
| 1345 00_07_27 pull 1 | 402.1–430.2(28.0 s)、474.3–533.0(58.7 s) | 2 | **0/2** | — | — |
| 1304 `0x4919`(Omega) | 100.7–118.5 開始、26.5 s | 5 | 5/5 | 1.0 / 1.1 / 6.7 / 24.4 / 24.4 | loss 0.0、return 0.0 |
| 1304 `0x460F`(Eald'narche) | 155.4–156.0 開始、30.2 s | 5 | 5/5 | 24.9–25.0 | loss 0.0、return −0.1 |

- 外れ 2 窓(00_07_27 pull 1 の 402 s / 474 s): 長さ 28 s / 58.7 s で、ボス機構の 9.2 / 14.3 s と一致しない。FFLogs にも自リプレイ由来にも対応する窓が無く、(c) 自リプレイ条件でも同じ 2 窓が外れている(同一 pull 内の再交戦扱いの区間)。同期の問題ではない。
- `0x4919` の lead が 1.0〜6.7 s に落ちる 3 本: FFLogs の sequence は Ultima(`0x4918`)の初動を起点に 2 ボスを融合しているので、Omega 単独の自リプレイ pull とは 12.9 s の位相差がある。詠唱同期で loss 誤差は 0.0 に収まるが、確定が窓の直前になった本では lead が縮む(RPR 09_02 / 09_16 は 24.4 s 取れている)。
- return 誤差 ±5.2〜5.4 s の 8 本は Clyteum `0x4C3F` の窓端の見積り差(FFLogs の窓端 26.9 / 98.5 が自リプレイの 31.0 / 102.7 と 4.2 s ずれているのを同期後のクロックが吸収しきらない本)。

#### false alarm 35 行の内訳(全て同じ 2 機構)

| zone | lead=25 の行(再アンカー予告) | lead=0 の行(「今すぐ消失」) | 計 |
| --- | --- | --- | --- |
| 1345 | 21 | 11 | 32 |
| 1304 | 2 | 1 | 3 |

トレース(ハーネスに一時的な publication トレースを入れて確認、コミットしていない)で見た機構:

1. **Clyteum `0x4C3F`(12 本中 11 本、各 2〜3 行)**: FFLogs の sequence は最後の状態が 133.1 s(自リプレイ版は 159.0 s、実際の pull は 170 s 超)。ボスの詠唱ローテは約 66 s 周期で繰り返す(48884c が 0 / 66 秒、自リプレイ版では 4 / 71 / 154 秒)。3 周目の 48884c(pull 130 s 前後)が FFLogs 版には無いので、フォロワーは 2 周目の 66 s に再整合してクロックを 60 s 前後まで戻し、第 2 窓 84.2 を「25 秒後」として再び公開する(例: BLM 23_09_40 は t=890 で pull 開始、908 / 975 の 2 窓を正しく当てた後、t=1028.3 と 1034.1 に tl=59.2 で再予告 → 1053.3 / 1059.1 の 2 行)。その後クロックが 84.2 に達した時点でボスは本当に 3 度目の消失中だが、add(`0x4BFE` ×2、`0x4BFF`)が狙えるので真値は AddsPresent であり、`lossIn=0` の公開が 3 行目(1060.0)になる。自リプレイ版は 154 s の 48884c を持つので整合が崩れず、(c) では起きない。
2. **San d'Oria `0x4918,0x4919`(5 本中 2 本 + RPR 09_02 の 2 回目)**: 融合 sequence は 305 s まであるが、2 ボスの詠唱対(44297c/44296c)が 75/80 s と 179/184 s に同じ並びで現れ、2 回目の対を 1 回目に整合した本でクロックが 88.8 に戻り、113.7 の窓を再予告する(BLM 09_02 t=631.9 → 656.9 の行、RPR 09_02 は 683.8 にもう 1 回)。RPR 09_02 の 710.0 行はその再整合クロックが窓に達した時点の `lossIn=0`。

どちらも「ソースの pull が自分の pull より短い/位相が曖昧」なときにフォロワーが過去へ再整合する既知の機構(Task 7 の zone 905 と同型)で、FFLogs 固有ではない。ただし FFLogs は高火力パーティのレポートなので **sequence が自分の pull より系統的に短い**(Clyteum 133 s vs 実 170 s 超)分だけ起きやすい。

#### 所見(改訂)

- helper rule で FFLogs 由来は NoTarget 窓を持つようになり、自リプレイ真値 38 窓中 **36 を当てる**(外れ 2 は自リプレイ版でも外れる非機構窓)。loss 誤差中央値 0.0、p90 0.2 s。ヒットの質は (c) 上限(31/38、こちらは `0x4919` の低信頼窓 5 本を出さない)と同等以上。
- しかし **false alarm 35**(バケット行)は (c) の 3 より桁違いに多く、採否基準「false alarm を増やさない」は満たさない。全件がソース sequence の末尾以降での過去への再整合で、ヒント発行の 2 窓目を「もう一度来る」と誤る。
- 対処候補(裁定待ち、いずれも follower か抽出器の規則変更): (1) フォロワーが sequence の最終状態を過ぎたら再整合を受け付けない/末尾より前へ戻る整合は異なる ID を 3 個以上要求する(Task 7 で保留した跳躍ガードと同じ)、(2) 抽出器で「窓の後に sequence 末尾まで 30 s 以上の状態が無い」窓を末尾窓として信頼度を下げる、(3) FFLogs 側で `--max-fights` を増やして長い fight(遅いパーティ)を優先して取る(listing に fight 長があるので `len` 降順に選ぶ)。(3) はツールだけで済み、Clyteum の 3 周目が入れば機構 1 は消えるはず。
- 配置: 1345 / 1304 の `-fflogs.json` は置かない(自リプレイ版が順位で勝つ)。1203(窓なし)は前節のまま。

## フォロワー逆戻りガード(Task 4、コントローラ裁定、HEAD 308983131 + 本修正)

FFLogs 条件の false alarm 35 件が全て「ソース sequence の末尾以降での過去への再整合」だったので、`ExternalTimelineHints` に 2 つのガードを入れた(製品コード。定数の追加は `MaxBackwardJump = 30 s` / `BackwardJumpScore = 3` のみ)。

1. **exhausted-sequence guard**: クロックが確定済みで `TimelineTime > lastStateTime + SyncTolerance(6 s)`(lastStateTime は States の最大 `Time` と Windows の `End` の最大の大きい方、open-ended の窓は延ばさない)なら、その sequence は尽きているとみなす。`Publish` は何も出さず(`NextDowntime` は MaxValue)、`Observe` はその sequence 上の候補を無視する。純関数 `IsExhausted(sequence, timelineTime, tolerance)`。pull 開始(`OnCombatChanged`)では期限切れのクロックと同じく引き直す。
   - 裁定からの逸脱 1 点: 裁定は「尽きたら次の pull 開始まで整合を一切受け付けない」だったが、そのままだと EventTrigger の多 sequence タイムライン(Emanation 719 は phase 0 が約 65 s で終わり、phase 2 が 1000 s 台に別 sequence として並ぶ)で **次の sequence への前進整合まで止まり**、ranked 4→3 / gate 2→1 に後退した(トレース: t=96.6 の 70.4→1044.5 が拒否され、pull 開始の引き直しは 60 s の Synced 失効後)。尽きた sequence 自身への候補だけを捨てて別 sequence への整合は通す形にしたところ ranked / gate は完全一致に戻り、FFLogs 条件の数値は同じだった(別 sequence へは backward-jump guard がそのまま効く)。
2. **backward-jump guard**: クロック確定後、候補の anchor 時刻が現在の `TimelineTime` より `MaxBackwardJump = 30 s` 以上前なら、異なる ID 2 個ではなく 3 個を要求する。別 sequence の候補も timeline 秒で同じ比較をする。純関数 `RequiredScore(confirmed, sameSequence, currentTime, candidateTime, drift, syncTolerance, maxBackwardJump)` は 1(同 sequence で drift ≤ 6 s)/ 2(既定)/ 3(確定後の 30 s 超の逆戻り)を返す。クロックが無い(NaN)ときは 2。

Check 2 件追加(`tools/timeline_regression`): IsExhausted の末尾前/末尾+6 s 後/Windows の End が後ろにある場合/open-ended 窓、RequiredScore の 9 分岐。RED は 2 メンバー未定義でのビルド失敗、GREEN は下の行。

**計測の注意**: ツールは `BossMod/bin/x64/Debug/BossModReborn.dll` を参照するので、本体を `-p:Platform=x64` でビルドし直さないとツールの再ビルドでは反映されない(最初の 1 回、`bin/Debug` の古い dll で 22/22 が出た。以下は全て x64 で本体を再ビルドしてから測った)。baseline は HEAD の follower を同じ環境(ユーザー timelines フォルダに 1203-fflogs / 1295 / 1304 / 1345-replay が配置済み)で測り直したもので、前節までの値と一致した。

回帰ツール:

```
tests=24 passed=24 failed=0; source=synthetic; replay=none
tests=305 passed=305 failed=0; source=external/synthetic; replay=none
```

メインハーネス(`%APPDATA%\XIVLauncher\pluginConfigs\BossMod\replays` 47 本)、本修正と baseline:

| 条件 | 集計行(本修正) | baseline(HEAD、同環境) |
| --- | --- | --- |
| `--sync all`(ranked) | `source=ranked sync=all holdout=False gate=False timelines=- replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0 gated_zones=0 gated_windows=0` | hit 4 / fa 0(CSV 完全一致) |
| `--holdout --sync all` | `source=ranked sync=all holdout=True gate=False timelines=- replays=47 windows=41 hit=21 hit_rate=0.512 lead_median=25.0 false_alarms=5 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=8.0 errors=0 gated_zones=0 gated_windows=0` | hit 21 / **fa 3** |
| `--gate --sync all` | `source=ranked sync=all holdout=False gate=True timelines=- replays=47 windows=20 hit=2 hit_rate=0.100 lead_median=25.0 false_alarms=0 loss_err_median=0.5 loss_err_p90=0.5 return_err_median=1.6 return_err_p90=1.6 errors=0 gated_zones=77 gated_windows=21` | hit 2 / fa 0(CSV 完全一致) |

FFLogs 条件(`%TEMP%\tl_eval` 17 本):

| 条件 | 集計行(本修正) | baseline |
| --- | --- | --- |
| `--timelines %TEMP%\tl_fflogs --source FFLogs --sync all --csv %TEMP%\tl_fflogs_eval2.csv` | `source=FFLogs sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp/tl_fflogs replays=17 windows=38 hit=34 hit_rate=0.895 lead_median=24.9 false_alarms=3 loss_err_median=0.0 loss_err_p90=0.2 return_err_median=0.0 return_err_p90=5.4 errors=0 gated_zones=0 gated_windows=0` | hit 36 / **fa 35** |
| `--timelines %APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\timelines --source Replay --sync all` | `source=Replay sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Roaming/XIVLauncher/pluginConfigs/BossModReborn/timelines replays=17 windows=38 hit=31 hit_rate=0.816 lead_median=24.9 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.3 return_err_median=0.1 return_err_p90=0.3 errors=0 gated_zones=0 gated_windows=0` | hit 31 / fa 3 |

### 所見

- FFLogs 条件は fa 35 → **3**(目標 0〜3 を達成)、hit 36 → 34(≥ 30)。Clyteum 1345 の 32 行(機構 1)は exhausted guard で全て消えた。自リプレイ条件の fa 3 → 0 も同じ機構(1304 の融合 sequence への逆戻り)で、こちらは 3 ID 要求で止まった。
- FFLogs で残る 3 行は全て San d'Oria 1304 の機構 2 そのもの(BLM 09_02 t=656.9、RPR 09_02 t=708.8 と続きの lead-0 行 710.0)。トレースでは 44297/44296 の詠唱対に加えて 3 個目の ID も 1 回目の対に整合するため、逆戻り 105 s(184.0→79.2)が score 3 で通る。定数を動かさずには消えない(裁定どおり見送り)。
- 失った 2 hit(1304 `0x4919` BLM 08_19 / BLM 09_02 の pull 3 窓 100.8 s、以前は lead 1.0 / 1.1 の際どい的中): 先に score 2 の誤った前進整合(87.2→234.2)が起き、baseline はその 7 s 後に score 2 の逆戻り(241.5→112.7)で直して lossIn=1.0 を出していた。本修正はその逆戻りに 3 ID を要求するため、修正が 3 s 遅れて score 3 で入り(244.7→115.9)、公開される lossIn=0 が真値の窓開始の 3.2 s 前に落ちて hit にも fa にもならない。FFLogs の 1304 sequence が Ultima+Omega を融合していて 12.9 s の位相差を持つのが根本。
- **後退 1 点(holdout)**: fa 3 → 5。増えた 2 行は zone 905(EdensVerseRefulgence 17_16_08 t=70.0、03_27_47 t=150.0)の lead-0 行で、どちらも既知の単一 pull 由来の誤った前進整合(35.4→258.7、score 2)の続きである。baseline はその 13 s 後に score 2 の逆戻り(271.9→138.4)で自己修正していたが、本修正はそれを 3 ID 要求で止め、誤ったクロックが窓の開始に達して lossIn=0 を 3 s 出したところで world check の矛盾(ContradictionGrace 3 s)が sequence を落とす。つまり「既存の false alarm が 5 s バケットをもう 1 つまたいで続いた」もので、新しい誤予告ではない。hit 21 は変わらず、他 3 条件は CSV まで完全一致。裁定の範囲では避けられないトレードオフなので、そのまま記録して判断を仰ぐ。

### fix round 1(レビュー指摘: 尽きた判定が派生値で、2 経路から score-2 再整合に戻れていた)

変更(`ExternalTimelineHints.cs`):

- **尽きた sequence を記憶する**: `_exhaustedSequence` を追加し、確定クロックが末尾 + 6 s を越えた時点で `RefreshExhausted()` がその sequence を記憶する。`Observe` は `_sequence` / `_confirmed` の状態に関係なく `_exhaustedSequence` 上の候補を捨てる(world check の矛盾で `_sequence` が落ちた後や Synced 失効後も戻れない)。`OnTargetableChanged` は尽きていれば何もしない。消すのは本物の pull 開始、`Update` のゾーン/世代リセット、`Dispose` だけ。
- **本物の pull 開始**: 純関数 `IsPullStart(actorInCombat, anyOtherEnemyAlreadyInCombat, actorIsListedBoss)` = `actorInCombat && (!anyOther || listedBoss)`。`OnCombatChanged` は他の非味方 enemy(InCombat かつ生存)の有無と、候補 sequence の `BossOIDs` に載っているかで判定し、pull 開始なら `_exhaustedSequence` を消して Synced 中でも引き直す(前ラウンドの `&& !Exhausted` の抜け道は撤去)。pull 開始でなければ従来どおり Synced 中は早期 return。
- **末尾のキャッシュ**: `Anchor()` で `_sequenceTail = SequenceTail(sequence)` を取り、毎フレームの判定はそれを使う。`IsExhausted` は純関数のまま `SequenceTail` を呼ぶ。
- Check 1 件追加(`IsPullStart` の真理値表 4 通り)。

回帰ツール:

```
tests=25 passed=25 failed=0; source=synthetic; replay=none
tests=305 passed=305 failed=0; source=external/synthetic; replay=none
```

ハーネス(同じ 5 条件、同じ baseline):

| 条件 | 集計行(fix round 1) | 前ラウンド → 今回 |
| --- | --- | --- |
| `--sync all`(ranked) | `source=ranked sync=all holdout=False gate=False timelines=- replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0 gated_zones=0 gated_windows=0` | 4/0 → 4/0(baseline と CSV 完全一致) |
| `--holdout --sync all` | `source=ranked sync=all holdout=True gate=False timelines=- replays=47 windows=41 hit=21 hit_rate=0.512 lead_median=25.0 false_alarms=5 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=8.0 errors=0 gated_zones=0 gated_windows=0` | 21/5 → 21/5(同じ 2 行) |
| `--gate --sync all` | `source=ranked sync=all holdout=False gate=True timelines=- replays=47 windows=20 hit=2 hit_rate=0.100 lead_median=25.0 false_alarms=0 loss_err_median=0.5 loss_err_p90=0.5 return_err_median=1.6 return_err_p90=1.6 errors=0 gated_zones=77 gated_windows=21` | 2/0 → 2/0(CSV 完全一致) |
| FFLogs(`%TEMP%\tl_fflogs`、`%TEMP%\tl_fflogs_eval2.csv`) | `source=FFLogs sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp/tl_fflogs replays=17 windows=38 hit=34 hit_rate=0.895 lead_median=24.9 false_alarms=2 loss_err_median=0.0 loss_err_p90=0.2 return_err_median=0.0 return_err_p90=5.4 errors=0 gated_zones=0 gated_windows=0` | 34/3 → **34/2** |
| 自リプレイ(`--source Replay`) | `source=Replay sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Roaming/XIVLauncher/pluginConfigs/BossModReborn/timelines replays=17 windows=38 hit=31 hit_rate=0.816 lead_median=24.9 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.3 return_err_median=0.1 return_err_p90=0.3 errors=0 gated_zones=0 gated_windows=0` | 31/0 → 31/0 |

所見: FFLogs で RPR 09_02 の lead-0 行(t=710.0)が消えた(尽きた sequence への戻りが記憶で塞がれた)。残る 2 行は BLM 09_02 t=656.9 と RPR 09_02 t=708.8 の lead-25 行で、どちらも 1304 の機構 2(3 ID で通る 105 s の逆戻り、末尾より手前なので exhausted guard の対象外)。holdout の zone 905 の 2 行は変わらず: 尽きた sequence とは無関係で、score-2 の誤った前進整合を score-2 の逆戻りで自己修正していたものが 3 ID 要求で止まる分なので、pull-start 規則では動かない。ranked / gate / 自リプレイは目標どおり。

### fix round 2(最終レビュー指摘: 追従中のボスの再戦闘入りを pull 開始と誤認、尽きた sequence の粘着を実配線で固定)

変更(`ExternalTimelineHints.cs`):

- **追従中のボスの再戦闘入りは継続**: `IsPullStart` に 4 つ目の引数 `continuationOfCurrentBoss` を足し、`actorInCombat && !continuation && (!anyOther || listedBoss)` にした。`OnCombatChanged` は `Synced` かつ尽きておらず、その OID が現在の `_sequence.BossOIDs` に載っているときに continuation = true とし、従来どおり Synced 中の早期 return に落とす(確定済みのクロックが、消失前後で点滅する戦闘フラグや再エンゲージで捨てられない)。他の enemy が誰も戦闘中でなくても、追従中のボス自身の再戦闘入りは pull 開始にしない。
- Check(`tools/timeline_regression`、`IsPullStart` の真理値表)を 4 通り → 7 通りに拡張(continuation の 2 通りと、Synced でないときの listed boss)。
- **実配線 Check** `wired follower: exhaustion sticks through a contradiction drop until a real pull start`(`tools/external_timeline_regression`): 合成 `WorldState` に 1 秒 1 フレームで `OpFrameStart` / `OpCreate` / `OpCombat` / `OpCastInfo` / `OpTargetable` を流し、本物の `ExternalTimelineHints.Update(null)` を回す。タイムラインは CastStart A(5 s)・B(10 s)・NoTarget 20–40 s(末尾 40 s)の 1 sequence(`BossOIDs` にボスを登録)。手順と assert: pull 開始でクロック配置(未確定・未公開)→ A/B の詠唱で確定し窓が公開される → **ボスが戦闘を抜けて再び入ってもクロックは 13 s のまま・公開も続く**(本ラウンドの継続規則)→ 20/40 s の targetable エッジで窓を通過 → 60 s で尽きて公開なし → ボスを untargetable にして 3 s 超の矛盾で `_sequence` が落ちる(`TimelineTime` NaN)→ 70/75 s に A/B を再演しても `TimelineTime` は NaN のまま(5–10 s に戻らない)・公開なし → 全員が戦闘を抜けて 1 体が入る本物の pull 開始の後、A/B で 6 s / 11 s に整合し直し公開が戻る。RED 確認: 矛盾で `_exhaustedSequence` を消すように壊すと「replayed casts re-aligned into the exhausted sequence: 15」で落ち、continuation を常に false にすると「boss re-entering combat restarted the clock: 1」で落ちる(どちらもツールを再ビルドしてから確認。ツールは自分の bin に `BossModReborn.dll` を複製するので、本体を x64 で再ビルドしただけでは古い dll で走る)。模倣できなかった操作はなし。

回帰ツール:

```
tests=14 passed=14 failed=0; source=synthetic; replay=none          (fflogs_timeline_extract --self-test)
tests=25 passed=25 failed=0; source=synthetic; replay=none          (timeline_regression)
tests=306 passed=306 failed=0; source=external/synthetic; replay=none (external_timeline_regression)
```

ハーネス(同じ 5 条件、同じ環境):

| 条件 | 集計行(fix round 2) | round 1 → 今回 |
| --- | --- | --- |
| `--sync all`(ranked) | `source=ranked sync=all holdout=False gate=False timelines=- replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0 gated_zones=0 gated_windows=0` | 4/0 → 4/0 |
| `--holdout --sync all` | `source=ranked sync=all holdout=True gate=False timelines=- replays=47 windows=41 hit=21 hit_rate=0.512 lead_median=25.0 false_alarms=5 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=8.0 errors=0 gated_zones=0 gated_windows=0` | 21/5 → 21/5(同じ zone 905 の 5 行) |
| `--gate --sync all` | `source=ranked sync=all holdout=False gate=True timelines=- replays=47 windows=20 hit=2 hit_rate=0.100 lead_median=25.0 false_alarms=0 loss_err_median=0.5 loss_err_p90=0.5 return_err_median=1.6 return_err_p90=1.6 errors=0 gated_zones=77 gated_windows=21` | 2/0 → 2/0 |
| FFLogs(`%TEMP%\tl_fflogs`) | `source=FFLogs sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp/tl_fflogs replays=17 windows=38 hit=34 hit_rate=0.895 lead_median=24.9 false_alarms=2 loss_err_median=0.0 loss_err_p90=0.2 return_err_median=0.0 return_err_p90=5.4 errors=0 gated_zones=0 gated_windows=0` | 34/2 → 34/2(CSV は round 1 の `%TEMP%\tl_fflogs_eval2.csv` と完全一致) |
| 自リプレイ(`--source Replay`) | `source=Replay sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Roaming/XIVLauncher/pluginConfigs/BossModReborn/timelines replays=17 windows=38 hit=31 hit_rate=0.816 lead_median=24.9 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.3 return_err_median=0.1 return_err_p90=0.3 errors=0 gated_zones=0 gated_windows=0` | 31/0 → 31/0 |

所見: 5 条件とも round 1 と集計行が同一(期待どおり)。集計行も false alarm 行も round 1 と同一なので、継続規則が計測セットで効いたとしても公開結果には影響していない。振る舞い自体は実配線 Check が固定している。

### fix round 3(再レビュー指摘: 60 s 以内の本物の引き直しまで継続扱いになっていた)

round 2 の継続規則は「Synced かつ尽きておらず、現在の `_sequence.BossOIDs` に載っている」だけで判定していたので、ワイプ → リセット → `SyncLifetime`(60 s)以内の再 pull(同じボス OID、クロックはまだ Synced)も継続と見なし、古いクロックが走り続けて 3 ID の逆戻り整合が入るまで窓を誤って公開しうる。裁定どおり、**ボスが戦闘を抜けてからの経過時間**で切る。

変更(`ExternalTimelineHints.cs`):

- `public const float ContinuationGap = 3f`(`ContradictionGrace` と同じ桁)。
- `_bossLeftCombatAt`: `OnCombatChanged` で追従中のボス(OID が現在の `_sequence.BossOIDs` に載っている actor)が戦闘を**抜けた**時刻を記録する(従来は `!InCombat` で即 return していた箇所)。ゾーン/世代リセット、`Dispose`、pull 開始の後に消す。
- 純関数 `IsContinuation(synced, exhausted, listedInCurrent, secondsSinceLeft, gap)` = `synced && !exhausted && listedInCurrent && (NaN(secondsSinceLeft) || secondsSinceLeft <= gap)`。一度も抜けていない(NaN)のに InCombat が再度立つのはフリッカーなので継続、gap 超は Synced のままでも pull 開始。
- Check 追加(`tools/timeline_regression`、8 通り): gap 1 s / ちょうど 3 s → 継続、40 s / MaxValue → pull 開始、NaN → 継続、Synced でない / 尽きた / 未掲載 → 継続でない。
- 実配線 Check に 1 段追加: Synced 後、t=12 のフリッカー(同一秒内に抜けて戻る)は従来どおり継続(クロック 13 s のまま・公開継続)。続けて t=14 に戦闘を抜け t=44 に戻る → クロックが sequence 先頭から再開(`TimelineTime` ≈ 1、未確定)、古いヒントは消えている。A/B の詠唱で再確定してから以降の手順(消失エッジ・尽き・矛盾・再演・本物の pull 開始)を 44 s ずらして続行し、全 assert が通る。RED: gap 条件を無視するように壊すと「re-pull after a 30 s gap did not restart the clock: 45」で落ちる(ツール再ビルド後に確認)。

回帰ツール:

```
tests=14 passed=14 failed=0; source=synthetic; replay=none          (fflogs_timeline_extract --self-test)
tests=26 passed=26 failed=0; source=synthetic; replay=none          (timeline_regression)
tests=306 passed=306 failed=0; source=external/synthetic; replay=none (external_timeline_regression)
```

ハーネス(同じ 5 条件、同じ環境):

| 条件 | 集計行(fix round 3) | round 2 → 今回 |
| --- | --- | --- |
| `--sync all`(ranked) | `source=ranked sync=all holdout=False gate=False timelines=- replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0 gated_zones=0 gated_windows=0` | 4/0 → 4/0(CSV 同一) |
| `--holdout --sync all` | `source=ranked sync=all holdout=True gate=False timelines=- replays=47 windows=41 hit=21 hit_rate=0.512 lead_median=25.0 false_alarms=5 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=8.0 errors=0 gated_zones=0 gated_windows=0` | 21/5 → 21/5(CSV 同一) |
| `--gate --sync all` | `source=ranked sync=all holdout=False gate=True timelines=- replays=47 windows=20 hit=2 hit_rate=0.100 lead_median=25.0 false_alarms=0 loss_err_median=0.5 loss_err_p90=0.5 return_err_median=1.6 return_err_p90=1.6 errors=0 gated_zones=77 gated_windows=21` | 2/0 → 2/0(CSV 同一) |
| FFLogs(`%TEMP%\tl_fflogs`) | `source=FFLogs sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp/tl_fflogs replays=17 windows=38 hit=34 hit_rate=0.895 lead_median=24.9 false_alarms=2 loss_err_median=0.0 loss_err_p90=0.2 return_err_median=0.0 return_err_p90=5.4 errors=0 gated_zones=0 gated_windows=0` | 34/2 → 34/2(CSV は round 1 の `tl_fflogs_eval2.csv` と同一) |
| 自リプレイ(`--source Replay`) | `source=Replay sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Roaming/XIVLauncher/pluginConfigs/BossModReborn/timelines replays=17 windows=38 hit=31 hit_rate=0.816 lead_median=24.9 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.3 return_err_median=0.1 return_err_p90=0.3 errors=0 gated_zones=0 gated_windows=0` | 31/0 → 31/0 |

所見: 5 条件とも集計行・CSV が round 2 と同一(期待どおり)。計測セットには「同じボスが 3 s 超 60 s 未満の間を置いて再戦闘入りする」場面が結果に影響する形では含まれておらず、規則は実配線 Check と真理値表で固定している。

## HP 分岐(2026-09-29)

仕様: `docs/superpowers/specs/2026-09-29-hp-gated-branches-design.md`。Task A(8cd148882)で抽出器が Ultima(zone 1304、OID 0x4919)を早い兄弟(離脱 100.7 s)と遅い兄弟(118.3 s)に分けた。Task B ではフォロワー(`ExternalTimelineHints`)がどちらの兄弟かを決める。

### 変更(`ExternalTimelineHints.cs`)

- 分岐が未決の間は、`DecisionTime`(94.6 s)以降に始まる窓を公開しない。`SelectDowntime` に省略可の引数 `windowStartLimit` を足した。開始がちょうど `DecisionTime` の窓も出さない。Task A は `DecisionTime` を早い窓の開始で打ち切るので、そうしないと打ち切った場合に早い窓が「共通の窓」として出てしまう。
- 決め方は純関数 `DecideBranch` にまとめた。
  - 早い兄弟の分岐点の詠唱を、時計が分岐点から ±6 s の所で見たら早い兄弟にする。
  - 分岐点から 1 s 過ぎても見なければ遅い兄弟にする。
  - 分岐点の 8 s 前からは、ボス HP を直近 10 s の傾きで分岐点まで延ばす。しきい値から 3 pt 以上下なら早い兄弟、3 pt 以上上なら遅い兄弟にする。
- HP の記録は、同期中で、しかも分岐が未決の間だけ行う。0.5 s ごとに 32 個までのリングバッファに入れるので、毎フレームの確保はない。傾きは最小二乗で求め、記録が 5 s 分に満たないうちは傾きなしとする。
- 決まったら `_sequence` をその兄弟に差し替える。時計・確認状態はそのままで、末尾だけ計算し直す。分岐状態は pull 開始、ゾーン/世代の変更、`Dispose` で消える。
- 追跡するボスは、名前の挙がったボスが複数いれば最大 HP のものにした(抽出器がしきい値を学ぶボスと同じ)。null になる条件は前と同じなので、world check の動きは変わらない。
- 「前の pull の次の sequence」を選ぶときは、兄弟を飛ばす(兄弟は同じボスなので)。
- **仕様・指示からの逸脱 1 点**: 既存の整合で兄弟へ移るのを確定として数えるのは、整合先が分岐点以降(`DecisionTime − 0.1 s` 以降)のときだけにした。
  - 字義どおりだと実データで誤判定が出た。holdout の BLM 08_19(早い pull)の流れ:
    1. 92.3 s に HP 予測で早いと決まり、100.7 s の窓を出した。
    2. 0.1 s 後、ボスの 0xAD1C(92.4 s)が遅い兄弟にしか無かった。この holdout では早い塊が 4 本しかなく、多数決で 0xAD1C@92.4 を残さなかった。
    3. 整合が遅い兄弟へ移り、字義どおりの規則では遅いと確定した。
    4. 92.4〜100.8 s の間、118.3 s の窓を出し続けた(loss_err 17.5)。
  - 分岐点より前は、兄弟は同じ戦闘を表している。片方にしか無い同期点は、各塊の多数決で残ったかどうかの違いにすぎない。
  - 修正後も、時計は整合に従って動く。そのうえで同じフレームのうちに、決まった兄弟へ戻る。
  - 分岐点での早い兄弟の同期点、またはそれ以降の固有の同期点への整合は、これまでどおり確定になる。
  - 規則を外すと、配線 Check の場合 D が落ちる。仕様 §3.3 の 1 に書いてある(807bbbb42 で追記)。

### 回帰ツール

```
tests=36 passed=36 failed=0; source=synthetic; replay=none          (timeline_regression)
tests=308 passed=308 failed=0; source=external/synthetic; replay=none (external_timeline_regression)
```

- `timeline_regression` に 2 件足した。
  - `DecideBranch` の真理値表。境界(8.0/8.1 s、ちょうど ±3 pt、猶予ちょうど)、分岐点を過ぎた後の外挿、NaN、しきい値なしを含む。
  - `windowStartLimit`。上限より前 / ちょうど / 後の窓、上限をまたぐ窓、短い窓の後ろの長い窓、旧形式の目印。
- `external_timeline_regression` に配線 Check を 1 件足した。兄弟 2 本の合成タイムライン(共通 0x1000@5・0x1001@10、早い兄弟 0x3000@30・NoTarget 36–60、遅い兄弟 NoTarget 56–80・遅い兄弟だけの 0x1002@25、分岐点 30 s、しきい値 50)で、毎秒の公開値を見る。
  - A: HP が 1 秒に 2 pt 減る → 22 s から早い窓(loss 14)。遅い窓は一度も出ない。
  - B: HP 90 → 31 s から遅い窓(loss 25)。
  - B': HP 50 で詠唱なし → 32 s まで何も出ず、その後遅い窓(loss 24)。
  - C: HP 50 で 30 s に 0x3000 → 30 s に早い窓(loss 6)。
  - D: A に 25 s の 0x1002 を足す → 早い窓が出たまま(上の逸脱)。
  - E: HP 90 で遅いと決まった後、30 s に 0x3000 → 整合で早い兄弟に変わり、30 s に loss 6。
- RED: スタブ(`DecideBranch` が常に -1、上限を無視)で 2 件と 1 件が落ちた。逸脱の規則を外すと D が落ちた。

### ハーネス

本体を x64 でビルドし直し、各ツールもビルドし直した(ツール側の dll の md5 が一致することを確認)。基準値は HEAD 8cd148882 のハーネスの複製で測り直した。

| 条件 | 集計行(本変更) | 基準 → 今回 |
| --- | --- | --- |
| アライアンス `--holdout --sync all`(`%TEMP%\tl_alliance` 23 本、`--csv %TEMP%\hp_holdout.csv`) | `source=ranked sync=all holdout=True gate=False timelines=- replays=23 windows=83 hit=63 hit_rate=0.759 lead_median=25.0 false_alarms=6 loss_err_median=0.0 loss_err_p90=0.3 return_err_median=0.0 return_err_p90=0.3 errors=0 gated_zones=0 gated_windows=0` | Task A 前 50/6(Ultima 0/13)、HEAD 55/6(Ultima 5/13)→ **63/6(Ultima 13/13)**。変わったのは Ultima の 13 行だけ |
| アライアンス 自データ内(`--timelines %TEMP%\tl_branch --sync all`) | `source=ranked sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp\tl_branch replays=23 windows=83 hit=70 hit_rate=0.843 lead_median=25.0 false_alarms=6 loss_err_median=0.0 loss_err_p90=1.2 return_err_median=0.0 return_err_p90=1.7 errors=0 gated_zones=0 gated_windows=0` | ユーザーフォルダ 57/6 → **70/6**(差は Ultima の 13 行だけ)。同じ `tl_branch` を HEAD のフォロワーで読むと 62/6 |
| アライアンス ユーザーフォルダ(`--timelines %APPDATA%\...\BossModReborn\timelines`) | `source=ranked sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Roaming\XIVLauncher\pluginConfigs\BossModReborn\timelines replays=23 windows=83 hit=57 hit_rate=0.687 lead_median=25.0 false_alarms=6 loss_err_median=0.0 loss_err_p90=2.2 return_err_median=0.0 return_err_p90=2.3 errors=0 gated_zones=0 gated_windows=0` | 57/6 → 57/6(CSV 同一、分岐なし) |
| 旧 47 本 `--sync all` | `source=ranked sync=all holdout=False gate=False timelines=- replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0 gated_zones=0 gated_windows=0` | 4/0 → 4/0(CSV 同一) |
| 旧 47 本 `--holdout --sync all` | `source=ranked sync=all holdout=True gate=False timelines=- replays=47 windows=41 hit=21 hit_rate=0.512 lead_median=25.0 false_alarms=5 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=8.0 errors=0 gated_zones=0 gated_windows=0` | 21/5 → 21/5(CSV 同一) |
| 旧 47 本 `--gate --sync all` | `source=ranked sync=all holdout=False gate=True timelines=- replays=47 windows=20 hit=2 hit_rate=0.100 lead_median=25.0 false_alarms=0 loss_err_median=0.5 loss_err_p90=0.5 return_err_median=1.6 return_err_p90=1.6 errors=0 gated_zones=77 gated_windows=21` | 2/0 → 2/0(CSV 同一) |
| FFLogs(`--timelines %TEMP%\tl_fflogs --source FFLogs --sync all`、`%TEMP%\tl_eval` 17 本) | `source=FFLogs sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp\tl_fflogs replays=17 windows=38 hit=34 hit_rate=0.895 lead_median=24.9 false_alarms=2 loss_err_median=0.0 loss_err_p90=0.2 return_err_median=0.0 return_err_p90=5.4 errors=0 gated_zones=0 gated_windows=0` | 34/2 → 34/2(CSV 同一) |

CSV が同一の条件は、この文書の前の節の行もそのまま有効。

`tl_branch` は新しい抽出器で作り直した(`%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\replays` の Jeuno 4・San d'Oria 13・Windurst 10 本、duty ごとに 1 回ずつ)。3 ファイルとも Task A の出力とバイト同一だった。分岐が立つのは 1304 の Ultima だけ。

Ultima の pull ごとの予告(holdout。自データ内も全く同じ値):

| リプレイ | 実際の離脱 | 兄弟 | lead | loss 誤差 |
| --- | --- | --- | --- | --- |
| BLM100_Vi_Ni 2026_08_19 | 100.8 | 早い | 8.5 | 0.0 |
| BLM100 2026_09_02 | 100.7 | 早い | 8.5 | 0.0 |
| BLM100 2026_09_29_00_57 | 100.7 | 早い | 14.1 | 0.0 |
| DRG100 2026_09_29 | 100.7 | 早い | 14.1 | 0.0 |
| RPR100 2026_08_25 | 100.7 | 早い | 14.1 | 0.0 |
| BLM100 2026_09_27 | 118.2 | 遅い | 25.0 | 0.0 |
| BLM100 2026_09_28_20_57 | 119.3 | 遅い | 25.0 | 0.0 |
| BLM100 2026_09_28_23_14 | 118.3 | 遅い | 24.9 | 0.0 |
| NIN100 2026_09_27 | 118.3 | 遅い | 25.0 | 0.0 |
| NIN100 2026_09_28 | 118.2 | 遅い | 25.0 | 0.0 |
| RPR100 2026_09_02 | 118.5 | 遅い | 25.0 | 0.0 |
| RPR100 2026_09_16 | 118.3 | 遅い | 25.0 | 0.0 |
| RPR100 2026_09_28 | 118.2 | 遅い | 24.9 | 0.0 |

### 所見

- 目標(Ultima 12/13 以上、lead 6 s 以上、誤予告 6 以下、他ボス不変)は全て満たした。
  - 13/13 で、lead は 8.5〜25.0 s。
  - 誤予告 6 件は以前からある NIN 09_28 の Eald'narche の 6 行と同じ。
  - Ultima 以外の行は CSV で同一。
- 13 pull とも HP 予測で決まり、分岐点の詠唱(6.1 s 前)も猶予(1 s 後)も使わなかった。
  - 遅い 8 本: 窓が horizon(25 s)に入る 93.3 s より前に「しきい値 +3 以上」で決まり、93.3 s から公開した(lead 24.9〜25.0 がその証拠。トレースで確かめた RPR 09_28 は、外挿を始められる最初の時刻 86.6 s に決まっていた)。
  - 早い 3 本: 86.6 s に決まり、lead 14.1。
  - 早い残り 2 本(BLM 08_19 / 09_02): 86.6 s の時点では予測が幅の中だった(BLM 08_19 は HP 48.6、傾き約 −0.33/s で予測約 46、しきい値 46.4)。92.2〜92.3 s に HP が一度に 6.5〜7 pt 落ちて予測が 43.4 を割り、そこで決まった。
- HEAD のフォロワーでは早い 5 本が lead 25 で当たっていたが、これは分岐を知らずに常に早い兄弟の窓を出していたため。遅い 8 本にも 100.7 s の窓を出していた。
  - ハーネスは「予測した離脱の 30 s 以内に本物の窓が来れば誤予告にしない」。118.3 − 100.7 = 17.6 s なので、その誤った予告は誤予告として数えられていなかった。
  - 今回のフォロワーは、遅い pull では遅いと決まるまで何も出さない。
- 分岐点の詠唱と猶予の経路は実データでは一度も使われていない。配線 Check(C・B'・E)でだけ固定している。
- 残課題: planner の fallback(`ExternalPlannerTimeline.SelectTimeline` / `BuildTimeline`)は兄弟を別々の sequence として扱ったまま(Task A の懸念)。上の逸脱は仕様 §3.3 の 1 に追記済み(807bbbb42)。

### fix round 1(レビュー指摘、HEAD 807bbbb42 の抽出器強化の上で)

変更(`ExternalTimelineHints.cs`):

- **分岐点の同期点を抽出器と同じ判定にした**(`CollectDivergenceStates`)。
  - 早い兄弟の `DecisionTime ± 0.1 s` にある詠唱・効果の項目のうち、遅い兄弟が「早い窓の開始 + `SyncTolerance`」までにその ID を一度も持たないものを指す。複数あればどれでもよく、両兄弟にある ID は数えない。
  - 早い窓の開始は、早い兄弟の NoTarget 窓のうち `DecisionTime − 0.05 s` 以後に始まる最初の長い窓(`EarlyWindowStart`)。無ければ遅い兄弟のどこにある ID も共有とみなす。
  - 観測時、遅い兄弟が今の時計の ±`SyncTolerance` 以内に同じ ID を持つなら数えない(`LateShowsIdNear`)。
  - 以前は「分岐点にある最初の項目」だけを見ていたので、共有の項目が先に並んでいると本物の分岐点を見逃していた。
- **整合がちょうど分岐点の同期点に時計を置いたら早い兄弟に確定する**。時計が未確認で `CheckDivergence` が働かない場合を拾う。ほかの項目には広げない。
- **打ち切られた分岐点は消失で決める**。未決の間に、時計が早い兄弟自身の窓に入り、早い兄弟が NoTarget を予測し、攻撃できる敵が何もいない(world check と同じ判定)なら早い兄弟にする。
  - 「早い兄弟自身の窓に入ってから」の条件は裁定に私が足したもの。分岐点より前の共通の窓で消えたのを早い兄弟の消失と取り違えないため。配線 Check の場合 K が固定している。
  - 仕様 §3.3 に追記した。
- **HP は名前の挙がったボスだけから読む**。`BossOIDs` があるのにそのボスがいなければ NaN で、HP では決めない。
- **HP の記録を必要な間だけにした**。しきい値があり、`DecisionTime − now ≤ 8 + 10 s` の間だけ記録する。傾きは記録を足したときだけ計算し直す。
  - 敵の走査は `_ws.Actors.Actors.Values`(構造体の列挙子)に替えた。`ActorState` の列挙子はインターフェースで、毎回確保するため。world check も同じ補助関数を使うので、同じ順で走査して確保しなくなった。
- **未決の間の公開の上限を `DecisionTime − 0.05 s` にした**。打ち切られた分岐点で、早い窓の開始が分岐点よりわずかに前になっても出さない。

テスト:

- `timeline_regression`(41 件)。
  - 真理値表の誤ラベル行を差し替えた。前向きの外挿(29.5 s で HP 48・傾き −4 → 46 で早い)と、分岐点を過ぎたら外挿しないこと(30.75 s で HP 46・傾き −4 → 46 で早い。逆向きに外挿すると 49 で未決)を分けた。
  - 分岐点の同期点の純関数 Check を足した(共有 ID の除外、窓が無い場合、打ち切り、`LateShowsIdNear`)。
- `external_timeline_regression`(308 件)の配線 Check を組み直した。
  - `OpCombat` の前に `Update` を呼ぶようにした。これで pull 開始の経路が通り、時計は 5 s の詠唱で確定する(以前は 10 s の整合で確定していた)。
  - 場合を足した。
    - C'(共有の 0x3001 は決めない)
    - F(未確認の時計で分岐点の詠唱 → 整合で早い)
    - G(ボス死亡後は大きな雑魚があっても HP で決めず、猶予で遅い)
    - H(同じフォロワーの 2 pull 目は 1 pull 目の決定を引き継がない)
    - I(打ち切り・消失で早い。窓は 35.98 s で分岐点 36 s より 0.02 s 前)
    - J(打ち切り・消失なし → 猶予で遅い)
    - K(打ち切り・分岐点より前の共通の窓 15–24 s で消えても決めない → 猶予で遅い)
  - 変異で確かめた(各規則を外すと、その場合が落ちる)。
    - 分岐点の同期点への整合を無効 → F
    - HP を任意の敵から読む → G
    - 消失判定を無効 → I
    - 上限の 0.05 s を外す → I
    - 共有 ID も分岐点の同期点に数える → C'
    - pull 開始でリセットしない → H
    - 消失を任意の窓で数える → K

```
tests=41 passed=41 failed=0; source=synthetic; replay=none          (timeline_regression)
tests=308 passed=308 failed=0; source=external/synthetic; replay=none (external_timeline_regression)
tests=15 passed=15 failed=0; source=synthetic; replay=none          (fflogs_timeline_extract --self-test)
```

ハーネス(本体を x64 で再ビルドし、全ツールを再ビルドした後):

| 条件 | 集計行 | 前 → 今回 |
| --- | --- | --- |
| アライアンス `--holdout --sync all` | `source=ranked sync=all holdout=True gate=False timelines=- replays=23 windows=83 hit=63 hit_rate=0.759 lead_median=25.0 false_alarms=6 loss_err_median=0.0 loss_err_p90=0.3 return_err_median=0.0 return_err_p90=0.3 errors=0 gated_zones=0 gated_windows=0` | 63/6 → 63/6(CSV が b8cf999c2 と同一。Ultima 13/13、lead も同じ) |
| アライアンス 自データ内 `--timelines %TEMP%\tl_branch2 --sync all` | `source=ranked sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp\tl_branch2 replays=23 windows=83 hit=70 hit_rate=0.843 lead_median=25.0 false_alarms=6 loss_err_median=0.0 loss_err_p90=1.2 return_err_median=0.0 return_err_p90=1.7 errors=0 gated_zones=0 gated_windows=0` | 70/6 → 70/6(CSV 同一。`tl_branch2` は `tl_branch` とバイト同一) |
| 旧 47 本 ranked / holdout / gate | 4/41 fa 0、21/41 fa 5、2/20 fa 0 | 3 条件とも CSV 同一 |
| FFLogs | 34/38 fa 2 | CSV 同一 |

所見: 実データ(Ultima)は分岐点の同期点が 1 つで共有もなく、HP 予測で全て決まるので、今回の変更は計測値を動かさない。変わったのは、打ち切られた分岐、共有の項目、未確認の時計、ボスの死亡といった、実データにまだ無い場合の振る舞い。これらは配線 Check と変異で固定した。

### 最終 fix wave(最終レビュー後、7e814b638)

最終レビューの裁定 3 件を入れた。仕様の §5「改訂(最終レビュー)」にも書いた。

- **分岐点を過ぎたら、HP は遅い兄弟を確かめるのにだけ使う**(`DecideBranch`)。今の HP がしきい値 + 3 pt 以上なら遅い兄弟にする。傾きは使わないので、傾きが無くても決まる。しきい値より下でも早い兄弟にはしない。早い pull なら分岐点の詠唱か消失が見えるので、そちらで決める。真理値表の「30.75 s で HP 46 → 早い」の行は「未決」に改め、分岐点の後の行を足した(遅い、傾きなしで遅い、幅の中、ボスなし)。
- **分岐点の候補を元の pull でも確かめる**(`AcceptDivergence`、`BuildBossSet` から呼ぶ)。
  - 候補の ID が、早い pull の 80% 以上で候補の時刻の ±2 s 以内に現れること。
  - 遅い pull のどれにも「早い窓の開始 + 6 s」までに一度も現れないこと。
  - 満たさなければ次の候補へ進む。無ければこれまでどおり早い窓の開始で打ち切る。`DivergenceTime` は純関数のままで、省略可の判定関数を受け取る。
  - フォロワーでは、いったん決まった後に整合が早い兄弟へ移ったとき、決定を変えるのは一致した項目が分岐点の同期点のときだけにした。遅い兄弟へ移るときはこれまでどおり。
- **細かい点**:
  - `DetectBranch` は、塊の間の隙間が広い方の塊の幅の 3 倍以上であることを求める。
  - `replay_timeline_extract` と `fflogs_timeline_extract` は、分岐ごとに 1 行 `branch group=… boss=… decision=… threshold=… early=… late=…` を出す(`ReplayTimelineExtractor.DescribeBranches`)。
  - 分岐点の同期点の覚えは、早い兄弟の `DecisionTime` で取る(呼び出し側は時刻を渡さない)。
  - 文書: この節の Task B の記述(§3.3 に逸脱が書かれていないという文)、`TimelineBranch` のコメント(決め方を全て並べた)、仕様 §3.2(共有 ID は「早い窓の開始 + 6 s」まで、で統一)、仕様 §4(アライアンスの自データ内と、自リプレイ条件のコマンド)。

テスト(`timeline_regression` は 41 → 44 件):

```
tests=44 passed=44 failed=0; source=synthetic; replay=none          (timeline_regression)
tests=308 passed=308 failed=0; source=external/synthetic; replay=none (external_timeline_regression)
tests=15 passed=15 failed=0; source=synthetic; replay=none          (fflogs_timeline_extract --self-test)
```

- `timeline_regression` に 3 件足した。
  - 火力のばらつき(100, 101, 103, 104 | 110, 112, 115, 118、隙間 6 < 3 × 8)は分岐しない。隙間がちょうど 3 倍なら分岐する。Ultima の実際のばらつきは分岐する。
  - `AcceptDivergence` の境界(早い pull の 80% ちょうど / 60%、±2 s の外、遅い 8 本中 3 本、上限ちょうど、上限より後だけ)と、判定関数付きの `DivergenceTime`。
  - 抽出の通し: 遅い 8 本中 3 本も 28 s に 0x2F00 を詠唱する。遅い兄弟からは多数決で消えるので、兄弟だけで比べると 28 s が分岐点になる。元の pull で退けて、次の 0x3000(30.3 s)が分岐点になる。
- 分岐ありの抽出 Check で、`DescribeBranches` の行もそのまま照合した。fflogs の self-test では、番号を付け直した後の行を照合した(窓が無いので `early=- late=-`)。
- `external_timeline_regression` の配線 Check に場合を 2 つ足した。
  - E': HP 90 で遅いと決まった後、32 s に早い兄弟だけの 0x3002(分岐点の同期点ではない)が来て、整合で早い兄弟へ移る。決定は変わらず、遅い窓(56 s)が出続ける。
  - E'': 同じ詠唱で、先に早いと決まっている場合。何も変わらない。
- 変異で確かめた。次の 4 つを同時に入れて、それぞれ対応する Check が落ちた。
  - 分岐点の後も HP で早い兄弟に決める → 真理値表
  - 決まった後の整合を無条件に確定扱いにする → E'
  - 元の pull の確認を外す → 抽出の通し(`decision=28.3`)
  - 3 倍の条件を外す → 火力のばらつき

抽出: `%TEMP%\tl_branch3` に duty ごとに作り直した(San d'Oria 13、Windurst 10、Jeuno 4 本)。分岐の行は 1 行だけ:

```
branch group=3 boss=0x4919 decision=94.60 threshold=46.36 early=100.69 late=118.28
```

3 ファイルとも `%TEMP%\tl_branch2` とバイト同一。

ハーネス(本体を x64 で再ビルドし、各ツールもビルドし直した。ツール側の dll の md5 は本体と一致):

| 条件 | 集計行 | fix round 1 → 今回 |
| --- | --- | --- |
| アライアンス `--holdout --sync all`(`--csv %TEMP%\hp_holdout_final.csv`) | `source=ranked sync=all holdout=True gate=False timelines=- replays=23 windows=83 hit=63 hit_rate=0.759 lead_median=25.0 false_alarms=6 loss_err_median=0.0 loss_err_p90=0.3 return_err_median=0.0 return_err_p90=0.3 errors=0 gated_zones=0 gated_windows=0` | 63/6 → 63/6(CSV 同一。Ultima 13/13、lead も同じ) |
| アライアンス 自データ内 `--timelines %TEMP%\tl_branch3 --sync all` | `source=ranked sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp\tl_branch3 replays=23 windows=83 hit=70 hit_rate=0.843 lead_median=25.0 false_alarms=6 loss_err_median=0.0 loss_err_p90=1.2 return_err_median=0.0 return_err_p90=1.7 errors=0 gated_zones=0 gated_windows=0` | 70/6 → 70/6(CSV 同一) |
| 旧 47 本 `--sync all` | `source=ranked sync=all holdout=False gate=False timelines=- replays=47 windows=41 hit=4 hit_rate=0.098 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.5 return_err_median=0.3 return_err_p90=1.6 errors=0 gated_zones=0 gated_windows=0` | 4/0 → 4/0(CSV 同一) |
| 旧 47 本 `--holdout --sync all` | `source=ranked sync=all holdout=True gate=False timelines=- replays=47 windows=41 hit=21 hit_rate=0.512 lead_median=25.0 false_alarms=5 loss_err_median=0.0 loss_err_p90=3.0 return_err_median=0.1 return_err_p90=8.0 errors=0 gated_zones=0 gated_windows=0` | 21/5 → 21/5(CSV 同一) |
| 旧 47 本 `--gate --sync all` | `source=ranked sync=all holdout=False gate=True timelines=- replays=47 windows=20 hit=2 hit_rate=0.100 lead_median=25.0 false_alarms=0 loss_err_median=0.5 loss_err_p90=0.5 return_err_median=1.6 return_err_p90=1.6 errors=0 gated_zones=77 gated_windows=21` | 2/0 → 2/0(CSV 同一) |
| FFLogs(`--timelines %TEMP%\tl_fflogs --source FFLogs --sync all`、`%TEMP%\tl_eval`) | `source=FFLogs sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp\tl_fflogs replays=17 windows=38 hit=34 hit_rate=0.895 lead_median=24.9 false_alarms=2 loss_err_median=0.0 loss_err_p90=0.2 return_err_median=0.0 return_err_p90=5.4 errors=0 gated_zones=0 gated_windows=0` | 34/2 → 34/2(CSV 同一) |
| 自リプレイ(`--source Replay --timelines <ユーザーフォルダ> --sync all`、`%TEMP%\tl_eval`) | `source=Replay sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Roaming\XIVLauncher\pluginConfigs\BossModReborn\timelines replays=17 windows=38 hit=31 hit_rate=0.816 lead_median=24.8 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.3 return_err_median=0.1 return_err_p90=0.3 errors=0 gated_zones=0 gated_windows=0` | 記録のみ(下の注) |

注: 自リプレイ条件はこの分岐の作業では前に測っておらず、比べる CSV が無い。ユーザーフォルダの timeline には `Branch` が 1 つも無いので、今回変えたコード(`DecideBranch`、整合での確定、分岐点の同期点の覚え)を通らない。また holdout ではないので抽出器も使わない。31/38 fa 0 は 2026-09-26/27 の値と同じで、lead の中央値だけが 24.9 → 24.8 になった。原因は確かめていない(2026-09-29 にユーザーフォルダの 1304 を作り直したこと(ledger の Item 1)か、その間のほかの変更)。

所見: 実データの分岐は Ultima だけ。分岐点が 94.6 s のまま残ったので、44306 は元の pull の確認も通っている(早い pull の 80% 以上で 94.6 s ±2 s に現れ、遅い pull には 106.7 s = 100.7 + 6 までに一度も現れない)。13 本とも分岐点より前に HP 予測で決まる。そのため、今回の 3 つの規則はどれも計測値を動かさない。効くのは、遅い pull の一部だけにある ID、分岐点を過ぎてからの HP、決まった後に早い兄弟だけの項目へ移る整合、火力でばらついた窓の開始、といった実データにまだ無い場合で、これらは Check と変異で固定した。

## Eald'narche の誤予告 6 件(2026-09-29)

アライアンス holdout に残っていた誤予告 6 件(`SanDoriaTheSecondWalk_NIN100__2026_09_28_21_33_13.log`、リプレイ開始から 1409.4〜1430.0 s)を調べた。

### 原因

予告は正しく、真値の側が欠けていた。

- フォロワーは Eald'narche の sequence に最後まで同期していた。1384.4 s(pull 内 130.9 s)に「1409.4 s 離脱、1439.6 s 復帰」を公開した。ボスは実際に 1409.3 s に対象不可になり、1439.5 s に戻っている。
- ところがこのリプレイでは、ボスの participant が OID 0 / Type None になっていた。`FindPulls` は `Type == Enemy` だけを拾うので、pull 6 の敵からボスが落ち、NoTarget 窓も出なかった。そのため正しい予告が誤予告として数えられた。
- ログ自体は正常で、1189.8 s に `OpCreate oid=460F type=Enemy name='Eald'narche'` がある。同じ instance id `40000D3E` が、ログ開始時のスナップショットで EventObj(OID 0x1E932D)として作られ、0.0 s に破棄されていた。
- `ReplayBuilder.ActorAdded` の instance id 再利用の分岐は、古い participant を閉じて新しい participant を作る。しかし OID / Type / OwnerID / LayoutID を設定するのは初回追加の分岐だけだった。そのため新しい participant は OID 0 / Type None のまま残った。upstream main も同じコード。

### 影響範囲

`%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\replays` と `...\BossMod\replays` の 421 本を走査した(OID 0 / Type None のまま世界に存在した participant を数えた)。

- 該当は 4 本、計 72 participant。
- ボス級(MaxHP 100 万超)を失っていたのは 2 本。
  - 上の NIN 09_28 の Eald'narche。
  - `SanDoriaTheSecondWalk_BLM100_Vi_Ni_2026_08_19_23_51_34.log` の 2 ボス目 0x493C(MaxHP 122.9M)。この pull は敵 0 体になり、同期点も窓も出ていなかった。
- 残り 2 本(Windurst RPR 08_25、死者の宮殿 1〜10 層 WAR 09_13)はボス以外の小物だけ。

### 修正

`5a46a9cec`(`claude/replay-id-reuse`、`4686f8413` の上)。`ActorAdded` で、participant がまだ一度も存在していなければ(初回追加でも、再利用で作り直した直後でも)識別情報を設定するようにした。`tools/external_timeline_regression` に再利用の Check を足した(修正前は `second: 0/None/0/0` で失敗、修正後 309/309)。timeline_regression 44/44、fflogs self-test 15/15。

### 計測

| 条件 | 集計行 | 前 → 後 |
| --- | --- | --- |
| アライアンス `--holdout --sync all` | `source=ranked sync=all holdout=True gate=False timelines=- replays=23 windows=84 hit=64 hit_rate=0.762 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.3 return_err_median=0.0 return_err_p90=0.3 errors=0 gated_zones=0 gated_windows=0` | 63/83 fa 6 → **64/84 fa 0** |
| アライアンス 自データ内 `--timelines %TEMP%\tl_branch3 --sync all` | `source=ranked sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp\tl_branch3 replays=23 windows=84 hit=71 hit_rate=0.845 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=1.2 return_err_median=0.0 return_err_p90=1.7 errors=0 gated_zones=0 gated_windows=0` | 70/83 fa 6 → 71/84 fa 0 |
| 旧 47 本 `--sync all` / `--holdout` / `--gate` | 4/0、21/5、2/0 | CSV 同一 |
| FFLogs | 34/38 fa 2 | CSV 同一 |
| 自リプレイ(ユーザーフォルダ) | `hit=36 ... false_alarms=0`(38 窓) | 31 → 36。変わった 5 行は全て Ultima の窓で、前回計測の後にユーザーフォルダの 1304 を分岐入りに差し替えた影響。今回の修正は 0x493C / 0x460F の participant しか変えない |

holdout の CSV の差は、誤予告 6 行が消えて NIN 09_28 の Eald'narche 窓(155.9〜186.1 s、lead 24.9、誤差 0)が 1 行増えたこと、それと Eald'narche の他の行の return 誤差が ±0.1 s 動いたことだけ。後者は抽出元の 2 本が直った影響。1304 を修正版で抽出し直すと(`%TEMP%\tl_branch4`)、0x493C と Eald'narche の同期点の中央値が 0.1〜0.2 s 動き、同期点が数個増える。窓と分岐は変わらない。

## 殴れる敵の有無の切替での合わせ直し(2026-09-29、Al-Zahbi の撃破ウェーブ)

仕様 `docs/superpowers/specs/2026-09-29-attackability-edge-sync-design.md`。コミット `fae328d6f`(ハーネス指標)、`6335c63af`(本体)、`3c81e2985`、`419ba8332`、`f9e867969`、`bd3d8ed30`(修正)。

### 調査

Al-Zahbi(zone 1368 の pull 1)はウェーブ戦で、窓の端は前のウェーブを倒し切った時刻で決まる。

- W0 撃破 → W1 出現(pull 0 s)、+20.5 s で狙える(窓 0)。
- W1 撃破 → W2 出現、+10.2 s で狙える(窓 1、5.8〜10.2 s)。
- W2 全滅 → W3(0x4DAB)出現、+8.2〜8.4 s で狙える(窓 2、約 8.6 s)。
- 窓 2 の開始は 78.6〜94.4 s とずれる。同期点はモジュール対象 0x4DA6 の技だけで、0x4DA6 は 36 s 前後に倒れる。
- HP の減り方で全滅時刻を予測する案は、窓開始の 5 s 前から ±3 s にしかならず、見送った。W2 の技を同期点に加える案も、W2 の撃破時間のばらつき(約 ±4 s)が残るので見送った。
- アライアンス holdout で、pull 終了以外に sequence を捨てたのは Al-Zahbi の 4 件(10 pull 中 4)。どれも窓の端のずれ(3〜9 s)を `CheckAgainstWorld` が 3 s の食い違いとして捨てたもの。

### 採用した規則(仕様への裁定を含む)

1. `AnyAttackable()` の切替が 1 s 続いたら、±6 s 以内の NoTarget 窓の端に、切替の実時刻へ遡って時計を合わせる。消える向きの端は終了済みの窓を、現れる向きの端はまだ始まっていない窓を候補にしない(レビュー指摘)。分岐が未決なら上限以降の窓は候補にしない。
2. 端の遅れ(6 s 以内)は食い違いとして数えない。ただし時計がこの pull で確認済みのときだけ(戦闘開始で仮置きしただけの時計は従来どおり捨てる。自リプレイ条件の Clyteum RPR 08_19 で、仮置きの誤った時計が生き残ったため)。
3. 予測が窓の中なのに殴れる敵がいる間は、確認済みかどうかによらず何も公開しない(「今離脱」は誤りなので)。
4. `MinPublishedLoss` は窓全体の長さで判定し、始まった窓は終わるまで公開し続ける。従来は残りが 8.5 s を切ると公開をやめていた(長い窓の最後の 8.5 s、合わせ直した直後の短い窓)。
5. フォロワーが止まっている間(無効、または状態機械を持つモジュールが稼働中)と、別の sequence へ移ったとき(分岐の兄弟間を除く)は、追跡状態を捨てる(レビュー指摘)。

### 計測

ハーネスの集計行に `inwin`(窓開始 +1 s 以降の最初の公開の復帰時刻の誤差)を足した。

| 条件 | 前 | 後 |
| --- | --- | --- |
| A アライアンス holdout | hit 64/84 fa 0、inwin 69 件 p90 0.2 | **hit 67/84** fa 0、inwin 77 件 p90 0.5 |
| B アライアンス 本番ファイル | hit 71/84 fa 0、loss_p90 1.2、inwin 69 件 p90 0.1 | **hit 72/84** fa 0、loss_p90 0.8、inwin 82 件 p90 0.8 |
| C 旧 47 本 既定 | 4/41 fa 0 | 同じ(CSV 同一) |
| D 旧 47 本 holdout | 21/41 fa 5、inwin 19 件 p90 9.0 | 20/41 **fa 3**、inwin 21 件 p90 6.2 |
| E 旧 47 本 gate | 2/20 fa 0 | 同じ(CSV 同一) |
| F FFLogs | 34/38 fa 2、inwin 26 件 | 同じ、inwin 36 件 |
| G 自リプレイ | 36/38 fa 0、inwin 24 件 p90 0.3 | 同じ、inwin 36 件 p90 0.1 |

- Al-Zahbi の窓 2(本番ファイル)の離脱誤差は最大 6.7 → 4.3 s。窓 1 の端で合わせ直した時計で窓 2 を予測するため。
- アライアンス 23 本のドロップ追跡で、Al-Zahbi の 4 件は全て消えた(残りは pull 終了時のもの)。
- D の hit −1(Lapis Manalis 04_15、136.7 s)は、予測より 7.7 s 遅れて敵が残っている間に「今離脱」を公開していたものが、規則 3 で消えた結果。予測としては外れていた。fa −2 も同じ種類の公開(zone 905)が消えたもの。D の誤った予測の本数(ReturnAt 96.8 / 88.8 / 178.7 の 3 つ)は前後で同じ。
- D の Lapis Manalis の loss 誤差が広がった行(−0.4 → −3.3 など)は、以前は窓直前の「今離脱」の公開で 0 付近に見えていたもので、今は本来の予測誤差が出ている。
- A の RPR 08_25 窓 94.4 の新しい hit は lead 0.0(消えた瞬間の公開)で、実質の予告ではない。予測より 8.9 s 遅い開始で、許容 6 s の外。
- inwin の p90 が A・B で悪化したのは、測れる窓が増えた(規則 4 で合わせ直した直後の短い窓も公開されるようになった)分を含む。同じ窓どうしの比較はしていない。
- テスト: external_timeline_regression 318/318、timeline_regression 44/44、fflogs self-test 15/15。

見送った指摘: HP 分岐の上限付き分岐で早い端の合わせ直しが効かない(既存の穴、実データでは分岐点前に HP で決まる)、`AnyAttackable` が InCombat を要求し抽出器は要求しない差、分岐の消失判定が瞬間値を読むこと、ハーネスの inwin の標本時刻(+1 s が合わせ直しの待ち 1 s と同じ)。
