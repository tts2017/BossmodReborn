# agents_rpr.md

BossMod Reborn / xan Autorotation の **Reaper (`RPR`) 専用エージェント指示書**(Claude Code 運用版、2026-09-19 改訂)。

対象: `BossMod/Autorotation/Standard/xan/Melee/RPR.cs`(および同フォルダの `RprPotency.cs`)、検証ハーネス `tools/xan_timeline_harness`、回帰ツール `tools/rpr_regression`。
この指示書は自己完結している。過去の会話ログは前提にしない。

---

## 0. 基本方針

**目標は rDPS の最大化**である。自分の与ダメージだけでなく、Arcane Circle がパーティに与える寄与(パーティのバースト窓に AC を重ねること)も含めて評価する。

守るもの(優先順):

1. **GCD uptime**
2. **Death's Design uptime**(Enshroud 中・2 分バースト中に切らさない)
3. **Arcane Circle / POT / Gluttony の 2 分バースト整合。整合の基準はパーティのレイドバフ窓**(AC のクールダウン終了時刻ではない)
4. **Enshroud / Communio / Perfectio 回数を落とさない**
5. **Red/Blue gauge overcap 回避**
6. **Soul Slice チャージを無駄にしない**
7. **計測で改善を示せない変更は入れない**

判断に迷ったら「実ゲームでの正しい挙動が正。ツールの数字を良く見せることが目的ではない」で考える。

役割分担: 調査・設計・採否判断は Fable 5.1 セッションが行い、コード記述は Opus 5 サブエージェントに委任する(小さな定数変更は直接でよい)。

---

## 1. 現行RPRの主要設計

### Strategy tracks(RPR.cs冒頭)

`ターゲット選択 / 範囲攻撃 / ローテーションモード / 開幕バースト / アルケインサークル / POT / レムールシュラウド(Enshroud) / コムニオ / ソウルスライス / ソウルゲージ(BloodStalk・GrimSwathe・Gluttony) / ソウルリーバー / プレンティフルハーベスト / ハーベストムーン / ペルフェクティオ / ハルパー / トゥルーノース / 戦闘中ソウルソウ / アルケインクレスト自動`

### RotationMode

- `Full`: 2分バースト計画込みの通常運用
- `Basic`: バースト計画なし。Gluttony禁止(ModeSwitchFailure対象)

### 重要predicate(RPR.cs)

- `ShouldEnshroud`: Enshroud突入判定。**DDタイマーの直接ゲートは持たない**。突入前のDD保護は次の2つのpredicateがtrueを返すことでブロックされる。
- `ShouldRefreshDeathsDesignBeforeAnyEnshroud`: 閾値 `GCD + BurstPlanGCDLength*5 + 1`(約16秒)。**even-burst planning窓 / PerfectioAvailable / PlentifulHarvestReady で意図的に抑止される**。
- `ShouldRefreshDeathsDesignBeforeEnshroud`: 閾値 `GCD + 9.0`(約11.5秒)。**抑止なしの無条件ゲート**。
- `ShouldRefreshDeathsDesignDuringAnyEnshroud`: Enshroud中DD更新の**採用済みの狭い例外**。条件: fullMode / Enshrouded / Lv90-91相当 / BlueSouls 2-4 / CombatTimer<30 / RaidBuffsなし / IdealHostなし / Perfectioなし / even-burst planning窓外。**この条件を広げない。**
- `IsEvenBurstPlanningWindow`: `CombatTimer>=90 && (cycle>=95 || cycle<=25 || RaidBuffsLeft>0 || IdealHost>0)`
- `Slice()`: SoulSlice使用判定。`RedGauge <= 50 || overcapSoon && canSpendRedBeforeSoulSlice`。`RaidBuffsLeft > 0` では早期return(バフ中はスペンダー優先、意図的)。
- RedGauge oGCD節: `soulSliceOvercapSoon && RedGauge > 50` でBloodStalk強制、`nextGCDSoulSlice && RedGauge > 50` でBloodStalk先行。comboProtected(Slice/WaxingSlice後)はred==100以外でBloodStalk/SoulSliceを我慢する。
- `EffectiveDowntimeIn`(2026-09-19): planner が「今ダウンタイム」(DowntimeIn=0)と言っていても usable なターゲットが目の前にいれば無視する。ステートマシンの desync で DD/SoulSlice/Gluttony/Enshroud が全停止するのを防ぐ。
- `IsTransientTargetLoss`(2026-09-19): mechanic hint の TargetReturnIn が既知で (return − loss) ≤ 8.5 秒のロスは無視する。DD・ゲージ・Reaver スタック・進行中 Enshroud はいずれも短いロスで失われない。
- フレーム単位スナップショット(`FrameCacheStage`)と `RprPotency` 威力テーブル(2026-09-19、挙動不変のリファクタ)。

---

## 2. 検証手段

### 2.1 主検証: `tools/xan_timeline_harness`(RPR.cs 本体を実行する)

BossModReborn.csproj を参照し、WorldState をフレーム進行させて `module.Execute` を毎フレーム呼ぶ。**RPR.cs の挙動を検証できる唯一のツール。** 決定的(同一バイナリの 2 回実行でトレース完全一致)。

```
# ビルド(warning 0 / error 0 必須。BossMod も一緒にビルドされる)
dotnet build tools\xan_timeline_harness\XanTimelineHarness.csproj -c Debug -v minimal

# 標準構成(パーティバフ + ターゲットロス予告)。XAN_HARNESS_TRACE_DIR にトレース CSV と summary.csv が出る
set XAN_HARNESS_TRACE_DIR=<dir>
dotnet <bin>\XanTimelineHarness.dll timeline-matrix        --job rpr --party-buffs 7.8 --target-loss-hints 30   # 959 本、約 45 秒
dotnet <bin>\XanTimelineHarness.dll timeline-combat-matrix --job rpr --party-buffs 7.8 --target-loss-hints 30   # 2,867 本、約 6 分
dotnet <bin>\XanTimelineHarness.dll dmu-full               --job rpr --party-buffs 7.8 --target-loss-hints 30
dotnet <bin>\XanTimelineHarness.dll rpr-potion                                                                 # ケース毎の hash 行で比較
```

- オプション: `--party-buffs <初回秒>`(DRG Battle Litany + MNK Brotherhood を 120 秒周期で発動。次のロス窓まで 15 秒未満なら温存する。module の RaidBuffsLeft/In が実値になる)、`--post-roll <秒>`(matrix シナリオの終端延長)、`--target-loss-hints <先読み秒>`(タイムラインのロス窓を mechanic hint として供給)、`--scenario-filter <文字列>`(単体再現)。
- 指標(job= 行と summary.csv): `potency`(実威力。DD×1.10、AC×1.03、Medicated×1.06、各レイドバフ×1.05)、`terminal`(終了時の保持資源価値)、`total = potency + terminal`、`party_ac`(AC がパーティに与える寄与の代理値)、`rdps = total + party_ac`、`dd_uptime`、`soul/shroud_overcap`、Enshroud/Communio/Perfectio/Gluttony/PH 回数。トレースには `dd_gap` 行(DD 切れ区間)が付く。
- 比較は必ず**同一ハーネス・同一オプションで変更前後を走らせ**、トレース差分の本数と per-scenario の rdps 差(summary.csv を run 名で join)を見る。
- 2 分バーストのタイミングを動かす変更は、シナリオ終端が 2 分マーク直後に集中する combat_matrix の構造上、終端切り捨てで不利に見える。`--post-roll 40` を付けても終端が移るだけなので、終端が 2 分マークの 40 秒以上後(または 118 秒未満)のシナリオだけを集計する「fair set」で判定し、長尺(dmu-full)の結果を主根拠にする。
- 隔離コピー(`F:\bmr_rpr_iso` など)でビルドし、メインチェックアウトの bin/obj には触れない。書き戻し前にメインツリー側の更新(別セッション)を diff で確認し、必要なら同期してから編集を再適用する。

### 2.2 副検証: `tools/rpr_regression`(エミュレータ)

`RprRotationEmulator.cs` は RPR.cs の簡易ミラーであり **RPR.cs を実行しない**。ハーネスで検証した挙動変更のうち、エミュレータがモデル化している範囲(mechanic hint や planner desync は未モデル)だけを同期する。HardFail ルール(`HardFailRules.cs`)は変更禁止。

```
dotnet run --project tools\rpr_regression -- --job RPR --all  --out <out> --baseline tools\rpr_regression\results_review_20260911\baseline.json
dotnet run --project tools\rpr_regression -- --job RPR --real --patterns 20000 --out <out> --baseline tools\rpr_regression\results_potion_third_stress_20260905\baseline.json
```

`baseline_adopted_rpr_weave_local_fixes` は現行エミュレータに対して古く、無変更でも REJECTED になる。上の 2 つが現行の定常値(standard 446 本、real 20,000 本、いずれも fail 0)。

### 2.3 実プレイ照合: リプレイプローブ

ハーネスの `RprCombatState` は、実行できないエントリ(ゲージ不足など)を `FindBest` の**前に**キューから除去する。実ゲームの ActionManagerEx は除去せず、`FindBest` が選んだものをそのまま実行してゲームに拒否され、その窓を丸ごと失う。したがって「実行できないアクションを push して下位の oGCD を塞ぐ」類の欠陥はハーネスでは原理的に検出できない。これは実リプレイで確認する。

- 道具は `F:mr_diag	oolspr_probe`(リポジトリ外)。`--rot <replay> <autorot のコピー> <preset>` で実モジュールをリプレイ上で毎フレーム回し、ゲームへ実際に送られた行動要求との一致率、Soul 不足の空振り push の区間、それに塞がれた oGCD を出す。`RPR_PROBE_LIVE=1` を付けると、ライブと同じ順序で ExternalTimelineHints と DisengageForecaster を通す。
- ハーネス側で近似するなら `XAN_HARNESS_CLIENT_REJECT=1`。ジョブ資源・ステータス条件を FindBest の後に回し、拒否されたらそのフレームは何もしない(射程と対象の有効性は共有キューも見るので従来どおり前で除く)。既定は従来動作のままで、matrix 959 本のトレースが一致することを確認済み。2026-09-25 に全 9 ジョブへ広げた(共通クラス `tools/xan_timeline_harness/ClientReject.cs`)。`blm-fork-scan --job <job> --fork-every 0` の末尾に `client_reject job=… action=… frames=… episodes=…` と事例を出し、`XAN_HARNESS_CLIENT_REJECT_LOG=1` なら区間ごとにプレイヤーのステータスも出す。ただしハーネスは方向指定を常に成功扱いにし、Crest も出さないので、塞がれた True North・Crest の損は採点されない。
- 手がかりは dalamud.log の `[AMEx] Can't execute ... status 572`。1 回あたり約 1.2 秒続き、GCD を割らない限界(残り約 0.8 秒)で止まるのが空振りの特徴。

---

## 3. 採用条件(RPR.cs変更を入れる場合)

- build 0 warnings / 0 errors
- ハーネス標準構成(§2.1)で **rdps が改善**し、`total` も悪化しない。per-scenario で悪化する本数が改善本数より明確に少ないこと(悪化例は必ず 1 本以上トレースを読んで理由を書く)
- `dd_uptime` 悪化なし、Enshroud / Communio / Perfectio 回数低下なし、overcap 悪化なし。ただし回数の低下が「本体が撃ち切れない Enshroud(戦闘終了やロスで Communio まで届かない)に入っていたのをやめた」結果で、該当シナリオの rdps が上がっていることをトレースで確認できた場合は許容する(理由を報告に書く)
- 変更が狙った状況以外のシナリオではトレース不変であること(挙動不変リファクタなら全シナリオ不変)
- rpr-potion の hash 行が変わる場合は理由を説明できること
- エミュレータがモデル化している挙動なら同期し、§2.2 が ADOPTABLE
- **候補は 1 件ずつ。複数同時に入れない。**

## 4. 禁止事項

- HardFail ルールの変更・緩和、fail を消すための期待値緩和
- UI 追加・ログ追加・新設定(Track/Option)追加(一時デバッグは書き戻し前に必ず除去)
- AoE 閾値変更
- 計測なしのバースト順・タイミング変更。**計測で rdps 改善を示せるバーストタイミング変更は許可**(例: AC をレイドバフ窓に合わせる保留)。ただし DancingMad / Windurst の FFLogs アンカー付きプロファイルには適用しない
- 既存ベースライン出力の上書き
- bin/obj を残す(隔離コピーで作業する)

---

## 5. 解決済み・再挑戦不要の項目

### 不採用 candidate(理由つき、再試行しない)

1. emulator の BloodStalk/GrimSwathe blue +10 削除 → Enshroud/Communio/Perfectio count 減で不採用
2. GluttonyReadyIn の emulator 厳密チェック → HardFail 激増で不採用
3. `ShouldRefreshDeathsDesignBeforeAnyEnshroud` の 5GCD→6GCD 拡張 → BlueGauge overcap 増で不採用
4. target loss が Enshroud 拘束中にある時だけ DD required +1GCD → overcap 増・Enshroud 減・DD uptime 悪化で不採用
5. DD 更新比較(`DeathsDesignBeatsFiller`)を Enshroud 込み前進シミュレータに置き換え → 4,576 回の判断で従来ヒューリスティックと 100% 一致、効果ゼロで不採用(試作は保管のみ)
6. ロス直前の Gluttony/Blood Stalk を復帰後に回す → 冷却がずれて使用回数が減り rdps 微減で不採用(全面版・絞り込み版とも)
7. Enshroud 温存のエミュレータ同期 → エミュレータの復帰後の優先順モデルが本体と異なり誤信号になるため不採用
 8. Enshroud 温存中は撃ち切れない Gluttony / Blood Stalk を常に止める(A5)→ Gluttony −139 回で warmup 120 の利得の大半を失う。Perfectio を落とす場合だけ止める絞り込み版を採用。Blood Stalk 側は全スイートでトレース差ゼロだったので削除
 9. 保留中の reaver GCD 数を温存判定そのものに足す版(A6b)→ 温存が減り、Gluttony を絞って止める版(A6a)より rdps が低い
10. 「ロス前に Reaping が 2 回以上入り、残りも 30 秒の Enshroud 内に収まるなら温存しない」例外(A8x)→ 開幕 2 本は直る(+416)が matrix −556 で差し引きマイナス
 11. Arcane Circle のダウンタイム温存 `ShouldHoldArcaneCircleForUpcomingDowntime` を生かす → **効果ゼロ**。POT ゲート(`strategy.Potion.Value != PotionUseStrategy.Off`)で死んでいたのは事実だが、ロス予告経由に作り替えても計測は全スイート完全不変。判定が true になる 10,485 フレームすべてで AC はクールダウン中(残 20 秒超)で、「AC を撃てる瞬間」と「ロス予告の 25 秒窓」が重ならない。判定位置を even-burst 短絡より前に上げた版も同じく不変
 12. レイドバフ窓中の Death's Design 更新を窓明けに回す → **不採用**。(a) 非 urgent 経路 `DeathsDesignBeatsFiller(urgent:false)` に延期条件を足した版は全スイート完全不変。バフ窓内の Shadow of Death はほぼ全て urgent 経路(DDExpiring)で、延期の余地がない。(b) urgent も含め「窓明けまで待つ」版(`Deathsdesign()` 冒頭でガード)は combat_matrix rdps −2,032、Enshroud 10,111→10,103、Communio 8,454→8,445 で悪化。DD 更新 GCD を後ろへずらすとバースト構築の位相が崩れる。構造の分析: バフ窓内 SoD 2,690 回のうち 2,279 回(85%)が窓開始+15.0〜17.5 秒に集中しており、前倒しすると DD を 15 秒捨てて更新回数が増えるため損。DD 30 秒周期とバフ 120 秒周期の位相差による構造的なもので、避けられない
 13. Soul Slice をコンボ完走まで待たせる(コンボ保護) → **不採用**。combat_matrix rdps は +118,192 に見えるが、内訳は実威力 −38,644 / 終端価値 +157,000 で、資源を溜め込んだだけ(§8 の「実威力と終端を分けて見る」に該当)。Enshroud 10,111→10,088、Gluttony 7,611、rpr-potion −543、予告なし matrix の dd_uptime 0.9676→0.9673 も悪化。**コンボを切ってでも Soul Slice(520) を撃つ現行が実威力では正しい。**
 14. `EnshroudSequenceValue` を公式威力から再計算した 4690 に下げる → **不採用**。同じ採点(7.5 準拠)で combat_matrix total 162,514,560→162,118,464(−396,096、−0.24%)、matrix も −121,246。Shroud 1 点の価値が下がって Enshroud 優先が弱まる。内訳の理屈(Void 580 + Cross 640×3 + Lemure 280×2 + Sacrificium 530 + Communio 1100 = 4690)は正しいが、実測では従来の 4860 の方が良い回しになる
 15. AC 前 Enshroud の突入を 1 GCD 遅らせてコンボを Infernal Slice で締める → **不採用**。背景: バースト Enshroud 2,297 回のうち 927 回が Waxing Slice 止まりで突入し、Perfectio が終わる頃に 30 秒のコンボタイマーが切れて Slice からやり直し(872 回)。最頻は「Waxing → Shadow of Death → Enshroud」644 回で、その SoD は DD 残り 0.8 秒の必須更新(DD 30 秒周期と AC 120 秒周期の位相)。(a) DD 更新を Enshroud 1 GCD 目の後に回す版は、更新が緊急なので発火せず全スイート不変。(b) SoD の後に Infernal を挟んで突入を 1 GCD 遅らせる版(見送り後は Enshroud を AC より先に押す経路が必要。AC 優先の分岐が先に return するとゲージ Enshroud が丸ごと消える)は、コンボ初期化 872→3 になるが combat_matrix rdps −4,471、Enshroud 10,111→9,846、PH 4,907→4,668。バースト全体が 2.5 秒後ろへずれ、ロス窓直前で PH や 2 回目の Communio が窓に落ちる。(c) ロス予告で見送りを止めるガード付きは rdps +128,015 に見えるが実威力 −183,953 / 終端 +313,498、Enshroud −61、PH −35、dd_uptime 0.9965→0.9946 で、資源を溜めただけ。コンボ 1 回分(+180)より突入遅延の損が大きい
 16. バースト前 DD 更新の窓を AC 残り 10 秒→12.5 秒に広げる(The Balance の「9 秒ルール」に寄せ、突入前 2 GCD をコンボ締めに空ける)→ **不採用**。combat_matrix rdps +84,447・実威力 +97,755 と出るが、更新が早まった分だけ Unveiled/Gibbet が割り込んで AC 自体が最大 4 秒ドリフトし(代表 0808 で 123.15→127.10)、Enshroud 10,111→10,060、Communio 8,454→8,434、PH 4,907→4,874、party_ac −3,090。matrix −192、dmu-full −192。AC のレイドバフ整合を崩す副作用が本体

### 分類済みバケット(RPR.cs修正不要と確定)

- **gauge_soul_slice_charge_overcap(2471件)**: 内訳 Lv<80 1914 / 高優先GCD押し出し16 / target loss・kill edge 22 / 非現実的初期state 464 / 残55(シード初期state 由来の単一パターン)。**actionable 候補ゼロ。**
- **dd_dropped_during_enshroud(377件)**: burst_planning_blocked 288 / target loss 引き伸ばし 89。288 は全件 emulator の 1.5s GCD 未モデル化による artifact。**RPR.cs 修正不要。**

### 採用済み変更

- Enshroud 中 DD 更新の狭い例外(`ShouldRefreshDeathsDesignDuringAnyEnshroud`、Lv90-91 限定)。emulator 同期済み。
- 2026-09-19: フレーム単位スナップショット + `RprPotency` 威力テーブル(挙動不変、3,828 本トレース一致)。
- 2026-09-19: `EffectiveDowntimeIn`(DMU P3 の 455 秒停止を解消、dmu-full potency +3.5%、DD uptime 0.68→0.995、他 3,827 本不変)。
- 2026-09-19: `IsTransientTargetLoss`(予告あり combat_matrix で悪化 35 本→1 本、5/8.5/12 秒のうち 8.5 秒を採用)。
- 2026-09-19: `AlignArcaneCircleWithRaidBuffs`(rDPS)。2 分目以降、AC の計画時刻をパーティのレイドバフ到来(自分の AC を除いた RaidCooldowns の最短)−0.7 秒に最大 8 秒まで寄せる。非 transient ロス、または raw mechanic hint のロスが整合後 20 秒内に来るなら保留しない。dmu-full rdps 533,110→540,994(+1.5%)、matrix +7.9k。combat_matrix の fair set(1,633 本)は、パーティが即時発動するモデルでは悪化 51 / 改善 131、ロス直前は温存するモデル(現行)では悪化 21 / 改善 20 の中立。長尺での利得を根拠に採用。パーティバフなしでは全シナリオ不変。
- 2026-09-19: **GCD 意味論のずれの修正 2 件 + Enshroud のロス前温存**(3 件を 1 件ずつ計測して採用)。エミュレータは 1 ステップ =「この GCD を撃ち、その後ろの窓で oGCD」だが、本体の NextGCD は「この窓の後に撃つ GCD」。エミュレータの `gcd is X` を `NextGCD is X` と訳すと 1 窓早くなる。
  - Gluttony: UseSoul の `debuffLeft += 30`(NextGCD が SoD なら DD 延長扱い)を削除。SoD の前に Gluttony が出ると Executioner(Reaver 970)が SoD(DDExpiring 900)を追い越し DD が切れていた。combat_matrix 悪化ゼロ、DD uptime 上昇。
  - Enshroud: `ShouldHoldEnshroudForTargetLoss`。非 transient のロスが Communio 前に来て、復帰後に 30 秒以内で残りを消化できないなら、Shroud ゲージ(常に)または Ideal Host(復帰+1 秒より長く残る場合)を温存。combat_matrix rdps +0.32%。
  - 開幕: `IsNormalOpenerSoulSliceBurstGCD` を「Soul Slice の後ろの窓」に修正(HardFail の TwoGcd ルールと The Balance に一致)。既存の `NormalOpenerArcaneCircleDelay` は毎フレーム「今から 1 秒後」を返し AC が撃たれない不具合だったので削除し、Soul Slice 後の最初のスロット(PH の Bloodsown Circle 制約)にした。ロス予告が GCD+21 秒以内なら従来の早撃ち。
  - 3 件合計(メイン比、パーティバフ+予告 30 秒): combat_matrix rdps +0.42%(DD uptime 0.9928→0.9929、Enshroud/Communio/Perfectio 増)、matrix +39.6k(DD 0.9676→0.9747)、dmu-full +185、rpr-potion 推定威力 +799(失敗 2 件は境界判定の入れ替わり)。予告もバフもない matrix では開幕修正が −45.7k(開幕直後の人工ロスを知り得ないため)。warmup 120 クラスは DD uptime 0.9929→0.9888(rdps は +38k)。
- 2026-09-20: **Enshroud のロス前温存の精密化**(A3 → A8 の 7 段階を 1 件ずつ計測)。
  - 「復帰後 30 秒以内に消化できるなら温存しない」例外を削除。例外はロス直前の Enshroud 入りを許し、復帰直後の DD 更新が遅れていた。
  - Ideal Host 温存時の Perfectio Occulta 条件は「温存すると Perfectio を落とし、かつ今入れば間に合う」ときだけ温存をやめる。今入る場合の Communio 時刻は「復帰時刻 + シーケンス長 − ロス前に入る Reaping 数 × 1.5 秒」で見積もり(ハーネスと 0.05 秒以内で一致)、余裕は 0.25 秒(1.0 秒だと 9 本で Perfectio を落とし、0.5 秒だと 4 本で落とした)。どちらでも落ちるなら温存して DD 更新を優先する。
  - Enshroud は Soul Reaver / Executioner 中に撃てないので、温存中にロス前で撃ち切れない Gluttony は、Ideal Host が切れる(Enshroud は最後の reaver GCD の直後に入るので「スタック数 − 1」GCD 分で判定)か、温存なら拾える Perfectio を落とす場合だけ止める。
  - 結果(メイン比、パーティバフ + 予告 30 秒): combat_matrix rdps +254,448(+0.155%)、DD uptime 0.9929→0.9965、Perfectio 3814→3827、Gluttony 7576→7614、Communio 8438→8446、Enshroud 10064→10062。matrix rdps +29,392(+0.175%)、DD 0.9747→0.9910、Perfectio 596→596、Enshroud 972→967、Communio 664→663。改善は実威力が主(combat_matrix で実威力 +188k、終端価値 +28k。matrix は終端価値 −1.3k)。予告なし matrix・dmu-full・event・rpr-potion は不変。
  - クラス別(combat_matrix): warmup 0 は悪化 0 / 改善 113、warmup 60 は悪化 2 / 改善 131、warmup 120 は悪化 4 / 改善 156、opening は悪化 2(計 −414)。
  - Enshroud / Communio の回数低下は全て「本体が戦闘終了やロスで撃ち切れない Enshroud に入っていた」シナリオで、1 本を除き rdps は上昇。例外の matrix 0621 は、復帰後に入れた Enshroud の途中で 2 回目のロスが入り Communio が届かなかった(−594)。
  - opening の悪化 2 本(suzaku 系、ロス 16.9〜27.2 秒)は、ロス前に Reaping 3 回が入るのに温存して AC 中の Reaping を失ったもの。例外で拾う版(A8x)は matrix が下がったので不採用。
  - 補足(2026-09-20): 上の Soul Slice 修正後に残るバフ窓内の弱い GCD は Slice 769 回・Waxing Slice 397 回・Shadow of Death 2,690 回。Slice 769 回のうち Soul Slice を代替にできたのは 8 回だけ(残りはチャージ切れ)なので、この線はもう掘れない。
- 2026-09-20: **レイドバフ中の Soul Slice 抑止をコンボ終了時だけに絞る**(`Slice()` RPR.cs:5530)。`RaidBuffsLeft > 0` で Soul Slice を丸ごと止めていたが、reaver・Perfectio・Plentiful Harvest の各ケースは手前で return 済みなので、バフ窓で代わりに出るのは Slice(420) か Waxing Slice(500) であり、抑止された Soul Slice(520) より弱い。バフが切れた次の GCD で Soul Slice が解放される「高倍率スロットに弱い GCD」の逆順が 2,645 回発生していた。条件に `&& ComboLastMove == AID.WaxingSlice` を足し、代替が Infernal Slice(600) のときだけ抑止する。結果: combat_matrix rdps 164,408,928→164,546,320(+137,392、+0.084%)、実威力 +238,000、終端 −107,327(資源を使い切る方向)、Enshroud 10,062→10,111、Communio 8,446→8,454、PH 4,868→4,907、Perfectio/Gluttony/dd_uptime/overcap 同値。matrix +28,558(+0.17%)、dmu-full +61、予告なしの matrix も +577(RaidBuffsLeft は自分の AC でも立つため不変にはならない)、rpr-potion 推定威力 +30。per-scenario は改善 2,645 / 悪化 60 / 不変 162 で、悪化 60 本は実威力 +37,580・終端 −79,388 = シナリオ終端で資源を使い切ったことによる終端価値の減少(代表 0546_rpr_z968_d263.9 は実威力 +919、終端 −1,940)。

- 2026-09-25: **Soul 消費の push を実ゲージ 50 以上に限定**(`UseSoul` のハード条件を `redGaugeAfterNextGCD < 50` から `RedGauge < 50` に変更。計画用の `redGaugeAfterNextGCD` は 100 到達判定などで引き続き使う)。従来は Soul 40 で次が Waxing/Infernal Slice のとき、または Soul 0 で次が Soul Slice のときに、Unveiled Gibbet/Gallows・Gluttony・Blood Stalk・Grim Swathe を遅延なしで push していた。ActionQueue はジョブゲージを見ないのでこれを選び、ゲームは 572 で拒否する。拒否は約 1.2 秒続き、その間は下位の oGCD(True North 10、Arcane Crest 5)が出ない。9/25 の実リプレイ 7 戦(トライアル)では空振りが 107 区間・15,042 フレーム、dalamud.log は 15,000 行あった。Arcane Crest が 4 回 1 GCD(2.2〜2.9 秒)遅れ、4 回とも被弾には間に合っていたが、最も際どいものは余裕 0.8 秒だった。修正後は同じリプレイで空振り 0・Crest の阻害 0 になり、ライブ要求との一致率は 1,641/1,708 で不変。ハーネスは全スイート(combat_matrix 2,867 本、matrix 959 本、予告なし matrix、dmu-full、event、rpr-potion 2 種)でトレースが完全一致した(§2.3 のとおりハーネスは空振りを事前に除去するため、差が出ないのが正しい)。 採否判断用の半分(`blm-fork-scan --job rpr --fork-split holdout --fork-every 0`、1,121 戦闘)での A/B でも、RPR 標準条件(パーティバフ 7.8・予告 30・post-roll 40)と全条件(予告 15・post-roll 120・パーティバフ 7・ランダム離脱 1)の両方で、既定モード・クライアント準拠モードとも全 1,121 戦闘で威力・終端価値・failures・gcd_idle が完全一致(差 0)。クライアント準拠モードの拒否フレームは修正前 216,225 / 274,718 → 修正後 0。拒否されていたのは全て Soul 消費で、採点対象の行動は 1 つも塞いでいなかった。採点上は中立で、効果は採点外(True North・Crest の遅れと拒否ログ)に限られる。

---

## 5.4 威力テーブルの 7.5 準拠(2026-09-21、採用)

公式ジョブガイド(パッチ 7.5 後)と `RprPotency.cs` を突き合わせ、採点側の 6 値が古かったので更新した。**本体 RPR.cs の判断は不変**(combat_matrix 2,867 本の行動列が 1 本も変わらない)。採点だけが正しくなる。

- `VoidReaping` 500 → `VoidReaping(m3)` で m3 なら 580(7.5 で Melee Mastery III の値が 560→580 に上昇)
- `EnhancedReapingBonus` 100 → 60(公式 580→640)
- `LemuresSlice` 240 → 280
- `GrimReapingPerTarget` 200 → 220
- `WaxingSliceUncomboed` 160 → 260、`InfernalSliceUncomboed` 180 → 280
- `Gluttony` 560 は 7.5 の変更(520→560)が既に入っていた。Slice/Waxing/Infernal/Soul Slice/Gibbet/Executioner/Unveiled/Blood Stalk/Shadow of Death/Perfectio/Plentiful Harvest/Harvest Moon は公式値と一致

**ベースラインが変わる**: combat_matrix total 159,386,752→162,514,560(+1.96%)、rdps 164,546,320→167,674,128。matrix rdps 16,898,810→17,165,048。dmu-full 541,240→552,118。これまでの計測は Enshroud 中の Reaping を 1 回あたり約 280 過小評価していた。**今後の比較は新採点同士で行うこと。**

---

## 5.5 回し全体の評価(2026-09-21)

定常区間(452 シナリオの 120〜240 秒平均)の 2 分あたり構成は理論どおりで、組み直しの余地は見つからなかった。

- 回数: Arcane Circle 1.00 / Gluttony 2.00 / Enshroud 2.99 / Communio 2.99 / Plentiful Harvest 1.00 / Perfectio 1.00 / Shadow of Death 3.95 / Soul Slice 3.99 / Unveiled 系 4.79 / Executioner 3.99 / Gibbet+Gallows 4.77。GCD 44.9 回、威力 38,319(GCD 31,207 + oGCD 7,112)。
- GCD 空転なし(`gcd_uptime` 1.0989、`gcd_idle` はターゲット可用中ほぼゼロ)、soul_overcap 570・shroud_overcap 0。
- Soul Slice は 2 分 4 回で、チャージ上限の 8 回に対して半分。ただし soul 収支は供給 352(Soul Slice 200 + フィラー 152) 対 消費 340(Gluttony 100 + Unveiled 240) でほぼ均衡しており、増やすと溢れる。増やせないのではなく増やす意味がない。
- Shroud が律速: reaver GCD 8.76 回 × 10 = 87.6 に対しゲージ Enshroud 2 回で 100 消費。Ideal Host 1 回で埋めて 3 回/2 分が上限。
- コンボは 2 分に約 1.2 回切れる(Slice 4.62 / Waxing 3.45 / Infernal 3.18)。中断元は Slice の直後が Soul Slice 1,044・Shadow of Death 568・Gallows 455、Waxing の直後が Gibbet 907・Shadow of Death 652(452 本の 2 分区間の合計)。保護版は §5 の不採用 13 番のとおり実威力が下がるので、現行の割り込みが正しい。
- 外部の 7.5 向け解説(ersharifst.com 2025-11-22)との照合(2026-09-22): 開幕「AC 最速」は 9/19 の計測で Soul Slice 後の方が良いと判明済み(§5 採用済み)。2 分バースト(SoD → Enshroud → Reaping 中に AC → PH)、事前準備(Soul 50・Shroud 50・DD 30 秒以下・Soul Slice 2 チャージ回避)、通常ループ(Soul Slice 溢れ防止・バースト外 Enshroud 1 回・SoD は AC 残りを超えない)は現行と一致。「Perfectio 直後にコンボを回さないと初期化される」は `ShouldPrioritizeComboAfterPerfectio` として実装済みだが、Perfectio 時点で Waxing から 25〜30 秒経過しており(872 件中 871 件)コンボ残りがなく、直後に回しても間に合わない。上流(突入前)で締める案は §5 不採用 15 番。「薬中 Communio 3 回は扱わない」は現行の方が攻めた設計で計測済み。**記事から新たに採用できる要素はなし。**
- The Balance の Standard rotation との照合(2026-09-22): 開幕(Harpe → SoD → Soul Slice → 遅めウィーブ AC → Gluttony)、double-Shroud の「AC 残り 9 秒 → 非 Shroud 2 GCD → Enshroud」(現行は AC−4 秒突入で同義)、通常ループの優先順、3 体で Grim Reaping / Soul Scythe / Grim Swathe、4 体以上で Guillotine と AoE コンボ、は現行と一致。「Lemure's Slice を先に撃って Sacrificium を後続バフに乗せる」は現行で問題なし(AC が 3 秒以内に後続する Sacrificium は 9,500 回中 0 件)。未検証で残るのは 10 分以降の gauge breakpoint と Deadzone(odd-minute Enshroud の保留)、および Communio Sacrifice(バフ外 Enshroud で Communio を 5 回目 Reaping に置換、ゲージ +10 Soul)。どちらも combat_matrix の 3 分シナリオでは再現しない。
- 開幕と復帰のバースト位置(2026-09-22 計測): 「特別な指定がない限り 2 GCD 殴ってからシナジーを合わせる」は現行が既に満たしている。opening シナリオ 322 本中 319 本で AC は 2 GCD 目のウィーブ(SoD → Soul Slice → AC)。ロス復帰後に AC を撃つ 308 件も全て復帰後 2 GCD 目で、0〜1 GCD 目は 0 件。例外は「ロス予告が GCD+21 秒以内」のときの早撃ち(SoD → AC)で、warmup=0 の人工シナリオに集中(100 本中 50 本、opening では 322 本中 3 本)。これは §5 採用済みの開幕ロスガードによる意図的な挙動。

---

## 6. 次の作業候補

0. (2026-09-19 夜の残課題の処理結果)
   - (a) ロス直前の Gluttony を復帰後に回す → **不採用**。全面版 rdps −1.7k、「スタックがロス中に切れる場合だけ」の絞り込み版も −2.0k。Gluttony の冷却がずれてシナリオ終端までの使用回数が減る。DD uptime は戻るが rdps が改善しない。
   - (b) 開幕ポーション → **採用**(`IsNormalOpenerDeathsDesignGCD` を `TargetDDLeft > GCD` に)。カウントダウン開幕で Harpe → SoD → ポーション(遅めウィーブ)→ Soul Slice → AC → Gluttony となり The Balance と HardFail 仕様に一致。推定威力はカウントダウン付き自然戦闘 12 本で −16(誤差)、カウントダウンなしは完全不変。仕様適合の修正として採用。
   - (c) Enshroud 温存のエミュレータ同期 → **見送り**。移植すると回帰ツールがプロファイル未除外(DMU のバースト失敗)と復帰しないロスでの温存という本体側の穴を検出したので、本体の `ShouldHoldEnshroudForTargetLoss` に FFLogs プロファイル除外と「mechanic hint の復帰時刻が既知で、そのロスが判定に使ったロスであること」を追加して是正した(ハーネス全スイートでトレース不変)。ただし温存の損得は復帰直後の DD 更新と Enshroud GCD の優先順で決まり、エミュレータはそこを本体と違う形でモデル化しているため、移植版は real 20k で悪化判定を出し、判定式の変更に対してハーネスと逆方向に反応した。誤った信号源になるので同期しない。
   - Enshroud 温存の例外削除と Perfectio 条件は 2026-09-20 に解決(§5 採用済み)。残る小さな悪化: 温存後の再入場中に 2 回目のロスが入るケース(matrix 0621、−594)、10 秒程度の短いロス中に Gluttony を使い復帰後 DD が切れるケース(combat_matrix 3 本、各 −330〜−440)、ロス窓が 2 つ続く ravana-ex(−757)。いずれも本数が少なく、次の予告(2 回目のロス)を判定に使えるかの調査が先。
   - ハーネスに `--countdown <秒>`(rpr-potion 自然戦闘をカウントダウン付きで開始し Soulsow/Harpe をプリキャスト)、`XAN_HARNESS_OPENER=1`(開幕 12 秒の行動列を出力)を追加。
1. (解決済み、上記の開幕修正で対応)開幕の AC タイミング。The Balance の標準「2nd GCD AC」は Shadow of Death → Soul Slice(AC を遅めウィーブ、続けて Gluttony)なので、現行の 0.9 秒(Shadow of Death 直後)は標準より約 2.3 秒早い。**ヘッダで `_arcaneCircleReadyIn` を `GCD + 0.05` に計画する実装は不可**(開幕ステートマシンと干渉し、AC が 16 秒、Gluttony 消失、matrix の Communio 664→359 と崩壊した)。やるなら `ArcaneCircleWeaveDelay` 側で、`IsNormalOpenerSoulSliceBurstGCD` のときだけ delay を `GCD + NormalOpenerArcaneCircleDelay` にする形で、Gluttony が同じ GCD 内の 2 スロット目に残ることを確認する。期待値はオープナー 1 回あたり rdps +0.4% 程度と小さい。
2. 通常区間の Enshroud タイミング(Gluttony 13 秒規則)のシミュレータ裁定。パーティバフ・モードで評価可能になったが、実損を示すデータはまだない。

## 7. 作業手順

1. **RPR.cs をいきなり変更しない。** まずハーネス標準構成で現行のベースラインを取り、summary.csv とトレースから損失箇所を特定する(dd_gap、回数、バフ窓内外の威力)。
2. 仮説はトレースの単体再現(`--scenario-filter`)と一時デバッグ出力で裏取りする。ハーネスの情報欠落(予告なし・バフなしでの結果)を判断品質の欠陥と取り違えない。
3. 修正は最小 1 件。Opus 5 に自己完結したプロンプトで委任し、戻った差分を親が読む。
4. §3 の全検証。悪化シナリオは必ず 1 本以上読み、理由を報告に書く。
5. 一時デバッグを除去し、メインツリーとの差分を確認してから書き戻す。エミュレータ同期は該当時のみ。

## 8. 最終判断の心得

- 数字は目的ではない。`terminal` を含む `total` と `rdps` の両方を見て、「実威力が増えて終端価値が減る」型は資源を使っただけかを確認する。
- 迷ったら変更しない。分類・根拠・不採用理由を残す方が価値が高い。
- 報告には必ず「変更前後の rdps/total/dd_uptime、変化したトレース本数、悪化例の理由、build と回帰ツールの結果」を含める。
