# 自動タイムライン抽出: 実データでの検証結果

計画 `docs/superpowers/plans/2026-09-29-auto-timeline-extraction.md` の Task 7 の記録。ブランチは `claude/auto-extract`。入力は 2 つある。

- `%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\replays` の 374 本。この節では `R` と書く。
- Task 1 で基準を取ったときの入力(`%TEMP%\tl_alliance`、旧 47 本、Clyteum 20 本)。

出力はすべて `%TEMP%` の下に置いた。実際のユーザーフォルダ(`...\BossModReborn\timelines`)には何も書いていない。今も `auto\` サブフォルダは無い。

検証は 2 回行った。

- **1 回目(round 0)**: Task 6 のコミット `9640c749d` に CLI の変更を足したもの。出力は `%TEMP%\ae_auto`。
- **2 回目(fix round 1)**: 次の 2 点を直したもの。この文書の数値は、特に断らない限り 2 回目のもの。出力は `%TEMP%\ae_auto2`。
  - ゲートの判定を「的中」から「誤予告にならない」へ変えた(下の「ゲートの判定の変更」)。
  - CLI の 1 本ずつの処理を関数に分けた。

## 結論

- **同等性(Step 2)**: 要約を 1 本ずつ作る形に変えた CLI の出力は、変更前と一致した。2 回目の CLI でも、もう一度確かめて一致した。
  - json は 3 組とも完全一致した。
  - txt も、次の差分を除けば一致した。
    - 出力先フォルダ名の差。
    - 各行に足した `summarize_ms=`。
    - 最終行に足した `peak_mb=`。
- **ハーネス 7 条件(Step 4)**: A〜G の 7 つとも、基準(`edge_new4_*`)と一致した。
  - CSV は `sort` 後に一致。
  - 集計行は改行コード(LF と CRLF)を揃えて一致。
  - この 7 条件は自動ファイルを読まないので、ゲートを変えた後は再実行していない。
- **品質ゲート(Step 3、2 回目)**:
  - 死者の宮殿の 17 zone は、どれにも自動ファイルが書かれなかった。3 セットのハーネスで false alarm は 0。
  - 1304・1368・1345 は書かれた。
  - **アライアンスは、自動ファイルで hit 72/84・false alarm 0 になった。** 手動ファイルの条件(`edge_new4_B`)と CSV まで一致した。
    - 1 回目は 56/84 だった。「的中」だけで判定していた旧ルールが、しきい値ぎりぎりの窓を 1 つ落としていたため。
  - **1248 は書かれなかった。** Jeuno のデータに、公開対象になる NoTarget 窓(8.5 秒以上)がそもそも無いため。手動ファイルでも公開対象は 0 個。
  - **Clyteum は、的中は手動と同じ 194/202 だが、false alarm が 0 → 59 に増えた。**
    - ゲートを通さない普通の抽出を同じ 97 本で行っても、同じ 59 になる。ゲートが生んだ誤予告ではない。
    - 抽出元を 97 本に増やしたことで同期点が変わったためと推定している(下記)。
- **負荷**(2 回目、Clyteum 97 本):
  - `summarize_ms` の合計は 140.3 秒で、1 本あたりの中央値は 1.56 秒。
  - 再構築(`rebuild_ms`、zone 1345 のゲート評価と書き出し)は 0.88 秒。
  - ピークメモリは 529 MB。1 本ずつの処理を関数に分ける前(1 回目)は 740〜770 MB だった。

## CLI の変更(`tools/replay_timeline_extract/Program.cs`)

- **1 本ずつ処理する。** 解析と要約は、ローカル関数 `SummarizeFile` の中で行う。
  - リプレイを 1 本解析するごとに、その pull を `PullSummary.Summarize` で要約して返す。
  - リプレイは関数が返った時点で到達不能になる。次のファイルの解析中に前のリプレイが残らないので、`peak_mb` は 1 本分の解析と、全要約の合計になる。
  - インライン化もさせない。
  - 各行の末尾に `summarize_ms=`(解析と要約にかかった時間)を足した。
  - 空のリプレイ(ops=0)でも、変更前と同じく行を出す。要約とリプレイ数には入れない。
- **`--auto <timelinesDir>` を足した。**
  - zone ごとに `AutoTimelineGate.Evaluate` を通し、`Publish` で `<timelinesDir>\auto\<zone>-auto.json` に書く(または書かない)。`auto\report.txt` も更新する。
  - 画面には次を出す。
    - 結果の行と `rebuild_ms=`(Evaluate + Publish の時間)。
    - 組ごと・窓ごとの判定。
  - 書いたファイルは、プラグインのストアと同じ `TimelineStore.LoadUserFile` で読み戻す。次の 4 点を確かめ、違えば stderr に出す(普通のモードの往復チェックと同じ)。
    - 読めた数が 1。
    - zone が一致。
    - 出どころが `AutoReplay`。
    - 系列の数が一致。
  - 最下位ソースとの比較は、プラグインと同じ条件にした。`TimelineStore.UserDirectory = timelinesDir` にして `Reload` し、そこから FFLogs・Cactbot・EventTrigger の最上位を選ぶ。
  - `--out` と同時には指定できない(usage を出して終了コード 2)。
  - `--auto` のときは既定の出力フォルダを作らない。
- **最終行を `replays=N zones=M peak_mb=P` にした。** `peak_mb` は `Process.PeakWorkingSet64` を MB にしたもの。

## ゲートの判定の変更(fix round 1、`BossMod/Timeline/External/AutoTimelineGate.cs`)

1 回目の検証で、アライアンスの的中が手動ファイルを下回った。仕様 5.2 の「自動 ≧ 手動」を満たさなかったので、コントローラ裁定により、pull ごとの判定をハーネスの false alarm の意味に合わせた。

1 本抜きで作り直したタイムラインの予測(今までどおり、完全版の窓から 10 秒以内にある公開対象の窓)を、抜いた pull の実際の NoTarget 窓と比べる。

- **hit**(今までどおり): 予測の開始が、長さ 8.5 秒以上の実際の窓の [開始 − 5 秒, 終了] に入る。
- **confirmed**(新設): hit であるか、長さを問わず実際の窓が [予測の開始 − 5 秒, 予測の開始 + 30 秒] に始まる。
  - 30 秒はハーネスの `FalseAlarmSeconds` と同じ値で、定数 `FalseAlarmAfter = 30f` として `HitSlackBefore` の隣に置いた。
- **窓を残す条件**: 評価できた pull が 2 本以上(`MinEvaluated`)で、そのうち 2/3 以上が confirmed。
  - 1 回目は hit が 2/3 以上を条件にしていた。
- report.txt の窓の行は `window A-B evaluated=E confirmed=C hits=H kept|dropped` になった(数値はカルチャ非依存)。

ハーネスとの細かい違いが 1 つある。ハーネスは、予測が長さを問わない実際の窓の [開始 − 5 秒, 終了] に入る場合も誤予告にしない。この「始まってから 5 秒より後に、進行中の短い窓の中へ落ちる予測」は、ゲートの confirmed に入らない。裁定の文言どおりに実装したので、ゲートの方がわずかに厳しい。

回帰チェック(`tools/external_timeline_regression`)の変更:

- **既存 4 チェックの期待する report 行**に `confirmed=` を足した(うち 1 つは次の 26/30/30/40/40/44 の組)。
- **26/30/30/40/40/44 の組**は、手で計算し直した。
  - 抜いた pull が 40・40・44 のときの予測 30 秒は、実際の窓(40・40・44 秒開始)が [25, 60] に入るので confirmed。
  - 抜いた pull が 26・30・30 のときの予測 40 秒は、窓が 35 秒より前に始まるので confirmed でない。
  - 3/6 は 2/3 未満(9 < 12)なので、今までどおり落ちる。期待行は `window 35.0-44.0 evaluated=6 confirmed=3 hits=0 dropped`。
- **新規 1:** 30 秒から 9 秒の pull が 3 本、7 秒の pull が 2 本の組。
  - 9 秒の pull を抜くと、作り直した窓が 8 秒になり、評価されない。
  - 7 秒の pull を抜くと、30〜39 秒が予測され、実際の 7 秒の窓が 30 秒に始まるので confirmed(hit は 0)。
  - 結果は `evaluated=2 confirmed=2 hits=0 kept`。旧ルールなら落ちる。
- **新規 2:** 30〜45 秒の pull が 7 本、4 秒の窓だけの外れ値が 2 本の組。
  - 外れ値の窓が 62 秒開始のとき、予測の 32 秒後で +30 秒を超えるので、`evaluated=2 confirmed=0 hits=0 dropped`。
  - 58 秒開始のとき、28 秒後なので `confirmed=2 ... kept`。境界を両側から押さえた。

旧ゲートに新しいチェックを当てると 6 件が落ちた(RED)。うち新規 1 は keep と drop の判定自体が逆になる。新ゲートでは 352/352 が通る。

## Step 2: 同等性

Task 1 Step 1 と同じコマンドを、出力先を `ae_cli_*`(1 回目)と `ae_cli2_*`(2 回目の CLI)にして実行した。Clyteum には、Task 1 が保存した 20 本のリスト `%TEMP%\ae_cly_files.txt` をそのまま使った。

```bash
X=tools/replay_timeline_extract/bin/Debug/net10.0-windows10.0.26100.0/ReplayTimelineExtract.dll
T="$(cygpath -w "$TEMP")"
dotnet $X --out "$T\\ae_cli2_al" "$T\\tl_alliance" > "$TEMP/ae_cli2_al.txt"
dotnet $X --out "$T\\ae_cli2_old" "$(cygpath -w "$APPDATA/XIVLauncher/pluginConfigs/BossMod/replays")" > "$TEMP/ae_cli2_old.txt"
args=(); while IFS= read -r f; do args+=("$(cygpath -w "$f")"); done < "$TEMP/ae_cly_files.txt"
dotnet $X --out "$T\\ae_cli2_cly" "${args[@]}" > "$TEMP/ae_cli2_cly.txt"
for d in al old cly; do diff -r "$TEMP/ae_base_$d" "$TEMP/ae_cli2_$d" && echo "$d json SAME"; done
for d in al old cly; do
  diff <(sed '$d' "$TEMP/ae_base_$d.txt" | sed 's/ae_base_/ae_X_/') \
       <(sed '$d' "$TEMP/ae_cli2_$d.txt" | sed 's/ summarize_ms=[0-9]*$//; s/ae_cli2_/ae_X_/') && echo "$d txt SAME"
done
```

| 組 | json | txt(最終行・`summarize_ms`・フォルダ名を除く) | 最終行(変更前 → 1 回目 → 2 回目) |
| --- | --- | --- | --- |
| al(`tl_alliance` 23 本、zone 1304/1368) | SAME | SAME(出力は全 27 行) | `replays=23 zones=2` → `peak_mb=1316` → `peak_mb=856` |
| old(旧 47 本、25 zone) | SAME | SAME(出力は全 73 行) | `replays=47 zones=25` → `peak_mb=557` → `peak_mb=539` |
| cly(Clyteum 20 本、zone 1345) | SAME | SAME(出力は全 22 行) | `replays=20 zones=1` → `peak_mb=659` → `peak_mb=561` |

- 1 回目・2 回目とも 3 組すべて SAME。
- txt を比べるときは出力先フォルダ名も揃えた。`zone=` 行が json のパスを含むので、`ae_base_` と `ae_cli_` の違いが出るため。それ以外の差分は無い。
- stderr はすべて空だった。

## Step 3: 品質ゲートを実データで確かめる

### 実行したコマンド

`--auto` の出力先は `%TEMP%\ae_auto2` だけにした(1 回目は `%TEMP%\ae_auto`)。3 群を順に同じフォルダへ出したので、`auto\report.txt` には 21 zone 分のブロックが入っている。入力ファイルの一覧は 1 回目に `%TEMP%\ae_auto_{potd,alliance,cly}_files.txt` へ保存し、2 回目も同じ一覧を使った(一覧は下の glob で作ったもの)。

```bash
X=tools/replay_timeline_extract/bin/Debug/net10.0-windows10.0.26100.0/ReplayTimelineExtract.dll
R="$APPDATA/XIVLauncher/pluginConfigs/BossModReborn/replays"; T="$(cygpath -w "$TEMP")"
ls "$R"/ThePalaceOfTheDead*.log > "$TEMP/ae_auto_potd_files.txt"
ls "$R"/SanDoria*.log "$R"/Windurst*.log "$R"/Jeuno*.log > "$TEMP/ae_auto_alliance_files.txt"
ls "$R"/TheClyteum*.log > "$TEMP/ae_auto_cly_files.txt"
rm -rf "$TEMP/ae_auto2"; mkdir -p "$TEMP/ae_auto2"
for g in potd alliance cly; do
  args=(); while IFS= read -r f; do args+=("$(cygpath -w "$f")"); done < "$TEMP/ae_auto_${g}_files.txt"
  dotnet $X --auto "$T\\ae_auto2" "${args[@]}" > "$TEMP/ae_auto2_$g.txt" 2> "$TEMP/ae_auto2_$g.err"
done
```

| 群 | 本数 | 集計行 | 実時間 |
| --- | ---: | --- | ---: |
| 死者の宮殿 | 225(3.0 GB) | `replays=225 zones=17 peak_mb=601` | 139 s |
| アライアンス | 27(2.4 GB、SanDoria 13・Windurst 10・Jeuno 4) | `replays=27 zones=3 peak_mb=869` | 99 s |
| Clyteum | 97(2.2 GB) | `replays=97 zones=1 peak_mb=529` | 144 s |

- stderr は 3 群とも空だった。自動ファイルの読み戻しの不一致も無い。
- Clyteum は、計画では 94 本としていたが、今のフォルダには 97 本ある。
  - そのうち 2 本は pull が 0(数 KB の録画)で、pull を含むのは 95 本、pull は計 278 個。
  - 組 4C3F(2 体目)の pull 数がちょうど 94 だった。

### zone ごとの結果(2 回目)

| zone | 結果 | 理由(report.txt) | rebuild_ms | 1 回目 |
| --- | --- | --- | ---: | --- |
| 561〜565、593〜604(死者の宮殿 17 zone) | not written | no validated window | 1〜41 | 同じ |
| 1248(Jeuno The First Walk) | not written | no validated window | 44 | 同じ |
| 1304(San d'Oria The Second Walk) | **written** | written with 3 windows | 62 | 同じ |
| 1368(Windurst The Third Walk) | **written** | written with **6** windows | 25 | written with 5 windows |
| 1345(The Clyteum) | **written** | written with 2 windows | 875 | 同じ |

2 回目の report.txt から `confirmed=N` を消して 1 回目と比べると、違うのは 1368 の 2 行だけだった。

- `written with 5 windows` → `6 windows`
- `window 39.9-48.5 evaluated=5 hits=1 dropped` → `kept`

1304 と 1345 の自動ファイルは、1 回目とバイト単位で同じ。

**死者の宮殿**(17 zone、399 pull、295 組)では、ゲートが評価した窓は 1 つも無かった。

- 272 組は pull が 3 本未満。窓を持たず、同期点だけの組として扱われた(`fewer than 3 pulls, windows withheld`)。
- 3 本以上の組は 23 組(計 102 pull)ある。
  - うち 11 組は、階層ボスが単独の組。例: 1692 が 7 pull、169F が 7、16AC が 6、16B9 が 7、16C6 が 6、1814 が 5。
  - 残りは、同じ顔ぶれの雑魚が 3〜6 回出た組。
  - これらは全体から作り直しても、公開対象になる NoTarget 窓(8.5 秒以上・信頼度 0.5 以上)が 1 つも無い。
- 前回ゲートなしで自データ内の false alarm を出していた窓(`%TEMP%\eval_groups.txt` の 40・62・9)の出どころを確かめた。
  - 前回の自データ内タイムライン(`%TEMP%\tl_new`)の 561・563・599 で、公開対象の窓を持つ系列を今回の report.txt と照合した。
  - その 6 系列(窓 12 個、信頼度はすべて単一 pull の 0.60)は、どれも pull 1 本の雑魚の組だった。
  - 今回はそれらが窓を持たないので、どの zone も「検証済みの窓なし」で書かれない。

zone ごとの内訳:

| zone | pull | 組 | 3 本以上の組(pull) |
| --- | ---: | ---: | --- |
| 561 | 48 | 31 | 4(18) |
| 562 | 50 | 33 | 3(16) |
| 563 | 37 | 28 | 2(9) |
| 564 | 29 | 19 | 2(10) |
| 565 | 29 | 21 | 2(9) |
| 593 | 43 | 29 | 3(14) |
| 594 | 23 | 20 | 1(4) |
| 595 | 19 | 15 | 1(4) |
| 596 | 22 | 16 | 1(4) |
| 597 | 35 | 29 | 1(4) |
| 598 | 18 | 13 | 2(7) |
| 599 | 21 | 18 | 1(3) |
| 600 | 7 | 6 | 0 |
| 601 | 2 | 1 | 0 |
| 602 | 1 | 1 | 0 |
| 603 | 8 | 8 | 0 |
| 604 | 7 | 7 | 0 |

**1248(Jeuno)** は 8 組すべてが 4 pull で、公開対象の窓が 1 つも無かった。4 本を普通に抽出しても(`--out`)、窓は次の 4 つしか無い。

- BossUntargetable 82.7〜194.4
- NoTarget 82.7〜87.0(4.3 秒で、8.5 秒未満)
- AddsPresent 82.7〜82.7 と 87.0〜194.4

ボスが消えている間も雑魚が殴れるので、ローテにとってのダウンタイムは短い。ユーザーフォルダの手動ファイル `1248-replay.json` も同じ内容で、公開対象の窓は 0 個。したがって「書かれない」のはゲートの判定どおりで、ヒントとして失うものも無い。計画の期待(1248 も written)は、この事実と合わない。

**1304(San d'Oria)**: 7 組すべてが 13 pull。

- 分岐する組 4919(判定 94.6 秒、しきい値 46.4%)の窓は 2 つとも残った。
  - early 100.7〜127.2(`evaluated=5 confirmed=5 hits=5`)
  - late 118.3〜144.8(`evaluated=8 confirmed=8 hits=8`)
- 組 460F の 155.9〜186.1 も残った(`evaluated=13 confirmed=13 hits=13`)。
- 公開対象の窓は、手動ファイル `1304-replay.json` と同じ 3 つになった。

**1368(Windurst)**: 6 組すべてが 10 pull。6 窓すべてが残った。

- 150.9〜162.1(`evaluated=10 confirmed=10 hits=10`)
- 1.0〜20.6(`evaluated=10 confirmed=10 hits=10`)
- **39.9〜48.5(`evaluated=5 confirmed=5 hits=1`)**: 1 回目は `evaluated=5 hits=1 dropped` で落ちていた窓。
- 85.5〜94.0(`evaluated=10 confirmed=10 hits=9`)
- 112.0〜125.8(`evaluated=10 confirmed=10 hits=10`)
- 152.2〜205.0(`evaluated=10 confirmed=10 hits=10`)

公開対象の窓は、手動ファイル `1368-replay.json` と同じ 6 つになった。

**1345(Clyteum)**: 5 組。

- 3 本以上の 3 組: 4C2C が 91 pull、4C3F が 94、4C28 が 91。
- 組 4C3F の 21.9〜31.0 と 88.4〜102.7 が残った(ともに `evaluated=94 confirmed=94 hits=94`)。
- 残り 2 組(4BEF+… の雑魚の組)は各 1 pull で、窓を持たない。

report.txt の判定のうち、窓を持たない組(`Windows = null`)と窓が空の組では、フォロワーは状態の Untargetable/Targetable 印を読む古い形式の処理に入る。念のため確認した。

- 書かれた 3 ファイルでこれに当たる組は 9 つあり、そのどれにも Untargetable → Targetable の区間は無い。
- そもそもリプレイ抽出器は、Untargetable の状態を出さない。`ae_auto`・`ae_auto2`・`ae_base_old` の全 json で 0 件。`ae_auto2` の 3 ファイルは Targetable も 0 件。
- したがって、窓を持たない組から検証されていない予告が出ることは無い。

### 自動ファイルをハーネスで測る

```bash
H=tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll
dotnet $H --timelines "$T\\ae_auto2" --sync all --csv "$T\\ae_auto2_al.csv" "$T\\tl_alliance" | tail -1
for g in ThePalaceOfTheDeadFloors110_ ThePalaceOfTheDeadFloors2130_ ThePalaceOfTheDeadFloors111120_; do
  args=(); for f in "$R"/*$g*.log; do args+=("$(cygpath -w "$f")"); done
  echo "$g $(dotnet $H --timelines "$T\\ae_auto2" --sync all --csv "$T\\ae_auto2_potd_${g%_}.csv" "${args[@]}" 2>/dev/null | tail -1)"
done
# Clyteum 97 本(計画の Step 3 には無い。仕様 5.2 のために追加)
args=(); while IFS= read -r f; do args+=("$(cygpath -w "$f")"); done < "$TEMP/ae_auto_cly_files.txt"
dotnet $H --timelines "$T\\ae_auto2" --sync all --csv "$T\\ae_auto2_h_cly.csv" "${args[@]}" | tail -1
# 比較用: 同じ 97 本でユーザーフォルダの手動ファイル(読むだけ)
USR='C:\Users\happy\AppData\Roaming\XIVLauncher\pluginConfigs\BossModReborn\timelines'
dotnet $H --timelines "$USR" --sync all --csv "$T\\ae_auto_h_cly_usr.csv" "${args[@]}" | tail -1
```

| 条件 | replays | windows | hit | hit_rate | false_alarms | 比較対象 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| アライアンス、自動(`ae_auto2`) | 23 | 84 | **72** | 0.857 | **0** | 手動(`edge_new4_B`): hit 72、0.857、FA 0。1 回目の自動: hit 56、FA 0 |
| 死者の宮殿 1〜10、自動 | 43 | 12 | 0 | 0.000 | **0** | 前回ゲートなし自データ内: FA 40 |
| 死者の宮殿 21〜30、自動 | 27 | 2 | 0 | 0.000 | **0** | 前回: FA 62 |
| 死者の宮殿 111〜120、自動 | 8 | 4 | 0 | 0.000 | **0** | 前回: FA 9 |
| Clyteum、自動 | 97 | 202 | 194 | 0.960 | **59** | 手動(ユーザーフォルダ): hit 194、FA 0 |

- アライアンスの自動の CSV は、手動の条件 B の CSV と `sort` 後に一致した。集計行も、`timelines=` 欄と改行コードを除いて一致した。
- 死者の宮殿の 3 セットについて。
  - 自動ファイルが 1 つも書かれていないので、埋め込みの候補だけの条件(`eval_groups.txt` の base 行)と同じ値になった。
  - 他の 14 セットも同じ理由で、base 行の false alarm 0 のままになる。
- Clyteum の CSV は、1 回目(`ae_auto_h_cly_auto.csv`)と `sort` 後に一致した。

アライアンスの集計行:
`source=ranked sync=all holdout=False gate=False timelines=C:\Users\happy\AppData\Local\Temp\ae_auto2 replays=23 windows=84 hit=72 hit_rate=0.857 lead_median=25.0 false_alarms=0 loss_err_median=0.0 loss_err_p90=0.8 return_err_median=0.0 return_err_p90=1.7 errors=0 gated_zones=0 gated_windows=0 inwin=82 inwin_return_err_median=0.0 inwin_return_err_p90=0.8`

#### 1 回目にアライアンスで 16 的中を失った原因(旧ルール)

1 回目の自動ファイル(`ae_auto`)では hit 56/84 だった。手動の条件 B(`edge_new4_B.csv`)と窓ごとに突き合わせると、失った 16 的中はすべて zone 1368 の pull 1(組 4DA6)にあった。

- 旧ルールで落ちた 39.9〜48.5 の窓そのもの: 6 的中。
- その次の 85.5〜94.0 の窓: 10 的中。この窓はゲートでは残っていた。
- 1304 の 26 窓は 1 つも失っていない。

2 つの自動ファイルと手動ファイルの違いは、この 1 窓だけだった。したがって 16 件すべてが、この窓を落としたことによる。

(1) なぜ旧ルールで落ちたか:

- 真値では、10 pull のうち 6 本に 8.5 秒以上の窓が 40 秒付近にある。残る 4 本では、その付近の NoTarget は 8.5 秒未満。
- 完全版の窓は、終了の中央値(48.54 秒)と開始の中央値(39.87 秒)の差が 8.67 秒で、しきい値ぎりぎり。個々の窓の長さの中央値ではない。
- 1 本抜きで作り直すと、長い窓を持つ pull を抜いたときほど、作り直した窓が 8.5 秒を割って公開対象から外れる。そうなるとその pull は評価されない。一方、短い窓の pull を抜いたときは窓が残り、その pull は評価されて外れになる。
- 評価 5・的中 1 は、「短い 4 本 + 長い 1 本」が評価に入った形と数が合う。ただし内訳は推定で、pull ごとには確かめていない。
- 手動ファイルは、この窓を 6 本とも当てている。短い 4 本でも、実際に短いダウンタイムがあるので false alarm にはならない(ハーネスの false alarm 判定は長さを問わない)。
- 新ルールでは、評価された 5 本がすべて confirmed(実際の窓が予測の直後に始まる)になり、窓は残った。

(2) なぜ次の窓まで外れたか:

- 組 4DA6 の系列は状態が 1 つしか無く、時計の同期は窓の端(`MatchAttackabilityEdge`)に頼っている。
- 窓を消しても 40 秒付近の実際のダウンタイムは残る。予測では殴れるはずの時間に何も殴れない状態が 3 秒(`ContradictionGrace`)を超えて続くので、フォロワーは時計を捨てたと考えられる。
- その結果、85.5 の窓は予告されなくなった(false alarm も出ていない)。これはフォロワーの仕組みからの推定で、トレースでは確かめていない。
- 新ルールで窓が残ると、この 10 的中も戻った。

#### Clyteum の false alarm 59 の出どころ

計画の Step 3 には無いが、仕様 5.2 の「Clyteum でも手動ファイルと同等以上」を確かめるために測った。

| タイムライン | 作り方 | hit | false_alarms |
| --- | --- | ---: | ---: |
| 自動 `ae_auto2\auto\1345-auto.json` | 97 本、ゲートあり | 194/202 | 59 |
| 普通の抽出 `ae_diag_cly97\1345-replay.json` | 同じ 97 本、ゲートなし | 195/202 | 59 |
| Task 1 の基準 `ae_base_cly\1345-replay.json` | 20 本、ゲートなし | 195/202 | 0 |
| ユーザーフォルダの手動 `1345-replay.json` | 9/26 に作成 | 194/202 | 0 |

比較のための抽出とハーネスのコマンド:

```bash
args=(); while IFS= read -r f; do args+=("$(cygpath -w "$f")"); done < "$TEMP/ae_auto_cly_files.txt"
mkdir -p "$TEMP/ae_diag_cly97"
dotnet $X --out "$T\\ae_diag_cly97" "${args[@]}" > "$TEMP/ae_diag_cly97.txt"
dotnet $H --timelines "$T\\ae_diag_cly97" --sync all --csv "$T\\ae_diag_h_cly97.csv" "${args[@]}" | tail -1
dotnet $H --timelines "$T\\ae_base_cly" --sync all --csv "$T\\ae_diag_h_cly_base20.csv" "${args[@]}" | tail -1
```

ここから言えること:

- 自動と普通の抽出(97 本)では、false alarm の行が CSV で一致した(`pull=-1` の行を `sort` して比較)。ゲートは false alarm を増やしても減らしてもいない。
- 的中の差 1 は、ゲートが評価で落とした窓ではない。pull 1 本だけの雑魚の組(4BEF+…+4DD9)で、窓が保留(withheld)された分。
  - 普通の抽出では、その組の窓 222.6〜238.8 が、同じ 1 本(`TheClyteum_BLM100__2026_08_21_07_33_31.log` の pull 0)を自分自身から予告して当てていた。自動ファイルでは、その組は窓を持たない。
- false alarm は 97 本中 27 本で 1 回ずつ出ている。
  - どれも組 4C3F の 88.4〜102.7(14.3 秒)の窓を、リプレイ開始から 779〜1103 秒の位置に 12.7 秒前から予告したもの。
  - 残り 32 件はすべて、同じファイルでその予告から 15 秒以内に、予告した窓の途中として出し直したもの(lead 0)。
- 20 本版と 97 本版では、組 4C3F の状態が違う。
  - 97 本版は 32 状態、20 本版は 35 状態。
  - 20 本版にある 16.9 秒の CastStart、21.9 秒の AbilityUsed、159.1 秒の AbilityUsed が、97 本版には無い。
  - 組 4C28 の状態も 60 対 58 で違う。
- 原因は、抽出元を増やしたときに同期点の組み合わせが変わることだと**推定**している。根拠は状態の一覧が違うことだけで、フォロワーのトレースで誤予告の経路は確かめていない。もしそうなら、窓の位置を 1 本抜きで検証するゲートの対象外になる。
- 現在のユーザー環境では、1345・1304・1368 には手動の Replay ファイルがあり、自動(AutoReplay)より上に並ぶ。なので自動ファイルが使われることは無い。

## Step 4: ハーネス 7 条件の一致

コマンドはブリーフのとおり。`H` はハーネス、`T=C:\Users\happy\AppData\Local\Temp`、`OLD` は旧 47 本、`USR` は実際のユーザーフォルダ(読むだけ)。4 本と 3 本に分けて並列に実行した(1 回目の CLI・ゲートのとき)。この 7 条件は自動ファイルを読まないので、ゲートの変更後は再実行していない。

| 条件 | 引数 | CSV(`sort` 後に diff) | 集計行(改行コードを揃えて比較) |
| --- | --- | --- | --- |
| A | `--holdout --sync all T\tl_alliance` | 一致 | 一致(windows=84 hit=67 false_alarms=0) |
| B | `--timelines USR --sync all T\tl_alliance` | 一致 | 一致(windows=84 hit=72 false_alarms=0) |
| C | `--sync all OLD` | 一致 | 一致(windows=41 hit=4 false_alarms=0) |
| D | `--holdout --sync all OLD` | 一致 | 一致(windows=41 hit=20 false_alarms=3) |
| E | `--gate --sync all OLD` | 一致 | 一致(windows=20 hit=2 false_alarms=0) |
| F | `--timelines T\tl_fflogs --source FFLogs --sync all T\tl_eval` | 一致 | 一致(windows=38 hit=34 false_alarms=2) |
| G | `--source Replay --timelines USR --sync all T\tl_eval` | 一致 | 一致(windows=38 hit=36 false_alarms=0) |

- 基準の txt は LF、今回の出力は CRLF なので、集計行は改行コードを揃えて比べた。
- stderr は 7 つとも空だった。
- ユーザーフォルダには `auto\` が無いので、B と G も変わらなかった。

## 負荷

Clyteum 97 本の `--auto` は 3 回測った。

- 1 回目: Step 3 の実行。途中で Jeuno 4 本の抽出(約 15 秒)が並行していた。
- 1 回目の単独: ほかに何も動かさずに `%TEMP%\ae_auto_perf` へ出した。
- 2 回目: fix round 1 後の Step 3 の実行。ほかの計測とは重ねていない。

3 回の出力 json は一致した。

| 項目 | 1 回目 | 1 回目の単独 | 2 回目(関数化・新ゲート) |
| --- | ---: | ---: | ---: |
| `summarize_ms` 合計 | 159,279 ms | 168,425 ms | 140,346 ms |
| 1 本あたり 平均 / 中央値 / p90 / 最大 | 1,642 / 1,619 / 1,999 / 5,601 ms | 1,736 / 1,837 / 2,005 / 3,928 ms | 1,447 / 1,555 / 1,676 / 3,128 ms |
| `rebuild_ms`(zone 1345、278 pull、1 本抜き約 276 回) | 1,070 ms | 1,049 ms | 875 ms |
| `peak_mb` | 740 | 770 | **529** |
| 実時間 | 163 s | 172 s | 144 s |

- pull 0 の 2 本は 6〜9 ms で終わる。
- 最大の 1 本は回ごとに違うファイルになっている。ファイルに固有の遅さではなく、揺らぎと考えられる。
- 2 回目はピークが約 3 割下がった。解析と要約を関数に分けて、前のリプレイが次の解析中に残らなくなったためと考えられる(Debug ビルドでは、ループの中のローカル変数は次の代入まで前のリプレイを指し続ける)。ゲートの変更は、全ファイルの要約が終わった後の処理なので関係しない。

ほかの群の値(2 回目、括弧内は 1 回目):

| 群 | `summarize_ms` の合計 | 1 本の最大 | `peak_mb` | `rebuild_ms` |
| --- | ---: | ---: | ---: | --- |
| 死者の宮殿 225 本 | 136.1 s(174.3 s) | 3.2 s(7.7 s) | 601(836) | 1〜41 ms/zone |
| アライアンス 27 本 | 96.5 s(109.7 s) | 4.6 s(5.6 s) | 869(1274) | 25〜62 ms/zone |

- ピークメモリは、本数よりも 1 本の大きさで決まる。
  - アライアンス(1 本あたり 150 万〜260 万 op)は 869 MB。
  - Clyteum 97 本は 529 MB で、20 本の同等性チェック(561 MB)とほぼ同じ。
  - 要約を持ち続ける分は、1 本を解析している間のメモリより十分小さい。
- ゲートは組ごとに、1 本抜きの作り直しを pull の数だけ行う。それでも 278 pull で 1 秒未満。

## 回帰ツール(fix round 1 のビルド)

- `TimelineRegression.dll`: `tests=47 passed=47 failed=0`
- `ExternalTimelineRegression.dll`: `tests=352 passed=352 failed=0`(新規 2 チェックを含む)
- `FFLogsTimelineExtract.dll --self-test`: `tests=15 passed=15 failed=0`

## 期待と違った点

1. **1248 が書かれない。** Jeuno のデータに公開対象の窓が無いため(上記)。手動ファイルも同じで、実害は無い。計画の期待の方が事実と合っていない。
2. **アライアンスの的中(解決済み)。** 1 回目は旧ルール(hit の 2/3)が、しきい値ぎりぎりの窓を落として 72 → 56 になっていた。
   - コントローラ裁定でゲートの判定を「誤予告にならない」(confirmed)に変えた。定数(`MinPulls`・`MinEvaluated`・`HitSlackBefore`・`MatchTolerance`)は変えず、`FalseAlarmAfter = 30` を足した。
   - その結果、72/84・false alarm 0 になり、手動ファイルと CSV まで一致した。
3. **Clyteum の自動ファイルで false alarm が 59 出る(未解決)。** 同じ 97 本からのゲートなし抽出でも同じ 59 が出るので、ゲートではなく抽出の問題。同期点が変わるためと推定しているが、確かめてはいない。ゲートの検証範囲の外。

## 最終レビュー後の修正(final fix wave)

ブランチ全体の最終レビューの指摘を、コントローラ裁定どおりに直した。コミットは次の 4 つ(push していない)。

- `50695afc1` ゲートの判定(確認は 3 秒以上の実際の窓、的中 1 本以上、分岐の数え方、キャンセル、pull 上限の指定、共通の書き込み関数、結果の文言)
- `f06533237` 裏スレッド(読めないキャッシュで再構築を失敗にする、壊れたキャッシュは 1 回だけログ、失敗した要約の数、戦闘中は止める)
- `43d9532f9` リプレイ解析のモジュール管理が共有設定に登録しない
- 最後のコミット: pull 上限の既定(上限なし)の注記と、この節・仕様の改訂

途中の 2 コミットも、それぞれ単独でビルドと回帰チェックが通ることを確かめた(358 件・363 件)。

### 変わったこと

- **ゲートの判定**(`AutoTimelineGate`)
  - 確認(confirmed)に使う実際の窓は、長さ 3 秒以上に限った。定数 `MinConfirmSeconds = 3` で、フォロワーの `ContradictionGrace`(`ExternalTimelineHints` の private 定数)と同じ値。
  - 窓を残すには、今までの条件(評価 2 本以上、確認 3 分の 2 以上)に加えて、的中が 1 本以上(`MinHits = 1`)要る。
  - HP 分岐の組では、同じ窓を二重に数えないための判定を変えた。今までは (種類, 開始, 終了) の完全一致で重複を除いていた。今は、早い側の兄弟の窓はすべて数え、遅い側の兄弟の窓は判定時刻以降に始まるものだけを数える。兄弟はそれぞれ自分の pull の中央値で作られるので、分岐点より前の共有の窓でも少しずれることがあり、完全一致では二重に数えていた。
  - `Evaluate` はキャンセルを受け取り、1 本抜きの作り直しの合間に確かめる。裏スレッドは自分のトークンを渡す。
  - `Evaluate` に、ボスの組ごとに開始時刻の新しい K 本だけを使う指定(`maxPullsPerSet`)を足した。既定は下の実験で決めた(上限なし)。上限が効いた組は report.txt に `pulls=20 (most recent of 94)` のように出る。
  - 結果の行を文にした: `zone=1368 written: 6 validated windows`、`1 validated window`、`zone=561 not written: no validated window`、`not written: FFLogs timeline has 2 windows, more than the 1 validated`。
- **裏スレッド**(`AutoTimelineExtractor`、`ReplaySummaryCache`)
  - `ReplaySummaryCache.Read` は、開けないファイル(ほかのプロセスが掴んでいる、アクセス拒否)で IO の例外を投げるようにした。null を返すのは、無い・壊れている・不完全・版違いのときだけ。`ReadFresh` は今までどおり、読めなければ null(要約し直す)。
  - 再構築はキャッシュを 1 つずつ再試行つきで読み(書き込みと同じ 3 回・250 ms)、それでも読めなければその仕事を失敗にする(ログと状態の行に出る)。一部の pull だけで書いたり、正しい自動ファイルを消したりはしない。壊れたキャッシュは除外し、パスごとに 1 回だけログに出す。
  - 状態の行の末尾に `| failed summaries: N` を出す。最後の一括取り込み(ボタン)またはプラグイン起動から、要約が「失敗」になっているリプレイの数(新しく失敗したものと、キャッシュから読んだ失敗の両方)。
  - プレイヤーが戦闘中のあいだは次の仕事を始めない。プラグインは描画スレッドで毎フレーム `_ws.Party.Player()?.InCombat` を volatile の変数に写し、裏スレッドはそれを 1 秒ごとに見る。止まっている間の状態は `paused (in combat) (N queued)`。終了時は待たずに抜ける。手動ボタンのツールチップは「May take several minutes; best used outside duties.」。
- **リプレイ解析のモジュール管理**: `BossModuleManager` に `followConfigChanges`(既定 true、今までどおり)を足し、`ReplayBuilder` は false で作る。裏スレッドの解析が、描画スレッドで発火する共有設定の `Modified` に登録しなくなった。デモモジュールも読み込まない。解析が使う設定値(成熟度、有効なモジュール、読み込み距離)は、使うときに読むので変わらない。
- **共通化**: 一時ファイルに書いて置き換える処理を `ReplacingFile.WriteAllText` にまとめ、要約キャッシュ・自動ファイル・report.txt で使う。下位ソースの選び方を `AutoTimelineGate.LowerRanked` にまとめ、裏スレッドと CLI で使う。
- **その他**: `PullSummary.cs` の作業コピーを CRLF にした(`.gitattributes` が索引を LF に揃えるので、コミットの差分は無い)。`ReplayManagementWindow.Dispose` に、プラグイン終了で閉じた録画は `RecordingFinished` を出さない(手動ボタンで取り込む)という注記を足した。

### pull 上限の実験(項目 11)

ボスの組ごとに新しい K 本だけを使うと Clyteum の false alarm が減るか確かめた。K は 20・30・50・上限なし。入力は 2 回目と同じ一覧(アライアンス 27 本、Clyteum 97 本)。(K, 群) ごとに別のフォルダへ `--auto --max-pulls K` で出し、そのフォルダでハーネスを回した。

```bash
X=tools/replay_timeline_extract/bin/Debug/net10.0-windows10.0.26100.0/ReplayTimelineExtract.dll
H=tools/timeline_hint_harness/bin/Debug/net10.0-windows10.0.26100.0/TimelineHintHarness.dll
w() { cygpath -w "$1"; }
for k in 20 30 50 all; do for g in alliance cly; do
  args=(); while IFS= read -r f; do args+=("$(w "$f")"); done < "$TEMP/ae_auto_${g}_files.txt"
  mkdir -p "$TEMP/ae_k${k}_${g}"
  dotnet $X --auto "$(w "$TEMP/ae_k${k}_${g}")" --max-pulls $k "${args[@]}" > "$TEMP/ae_k${k}_${g}.txt"
done; done
dotnet $H --timelines "$(w "$TEMP/ae_kall_alliance")" --sync all --csv "$(w "$TEMP/ae_kall_h_al.csv")" "$(w "$TEMP/tl_alliance")" | tail -1
for k in 20 30 50 all; do   # Clyteum 97 本(args は ae_auto_cly_files.txt)
  dotnet $H --timelines "$(w "$TEMP/ae_k${k}_cly")" --sync all --csv "$(w "$TEMP/ae_k${k}_h_cly.csv")" "${args[@]}" | tail -1
done
```

| K | Clyteum の組 4C2C / 4C3F / 4C28 の pull | Clyteum hit | Clyteum false_alarms | アライアンス |
| --- | --- | ---: | ---: | --- |
| 20 | 20 / 20 / 20(元は 91 / 94 / 91) | 194/202 | **65** | 自動ファイルが上限なしと同一 → 72/84、FA 0 |
| 30 | 30 / 30 / 30 | 194/202 | **65** | 同上 |
| 50 | 50 / 50 / 50 | 194/202 | **65** | 同上 |
| 上限なし | 91 / 94 / 91 | 194/202 | **59** | 72/84、FA 0(測定) |

- アライアンスの組は 10〜13 pull なので、K ≥ 20 では何も変わらない。1304・1368 の自動ファイルは 4 通りでバイト単位で同じだったので、ハーネスは上限なしの 1 回だけ回した(同じファイルからは同じ結果になる)。
- Clyteum の 1345 は、どの K でも 4C3F の 2 窓(21.9〜31.0 と 88.4〜102.7、上限により ±0.1 秒)が残った。
- K = 20 の false alarm 65 件を上限なしの 59 件と突き合わせた。
  - 前もって出る誤予告(lead 12.7 秒)は、同じ 27 本・同じ位置(開始が 0.1 秒ずれるだけ)に残った。原因の同期点の組み合わせは、新しい 20 本に絞っても変わらない。
  - 増えた 6 件は、ほかの 2 本(`..._2026_08_21_01_24_19.log` の 474〜480 秒、`..._2026_08_21_05_59_38.log` の 624〜630 秒)で、予告した窓の途中に出し直したもの(lead 0)。
  - K = 30・50 も 65 件で、ファイルごとの件数は同じ形だった。
- **決定: 上限なし**(`AutoTimelineGate.DefaultMaxPullsPerSet = int.MaxValue`)。Clyteum の false alarm を 0 にする、または明らかに減らす K が無く、アライアンスは変わらないため。指定の仕組み(`maxPullsPerSet`、`--max-pulls`)は残した。
- 1 回目の試行は捨ててやり直した。出力先の指定を誤って 8 本が 1 つのフォルダ(`Temp$d`)に書き、さらにハーネス 8 本の同時実行でメモリが尽きたため(OutOfMemory と読み込み失敗)。上の数値はやり直した方のもので、ハーネスは 2 本ずつ、読み込み失敗 0 で回した。

### 実データでの再検証

`%TEMP%` の `ae_cli3_*` と `ae_auto3` に出した。コマンドは Step 2・Step 3 と同じで、パスの組み立てだけ `cygpath -w "$TEMP/..."` にした。

**同等性**(普通のモード、Task 1 の基準と比較):

| 組 | json | txt(最終行・`summarize_ms`・フォルダ名を除く) | 最終行 |
| --- | --- | --- | --- |
| al | SAME | SAME | `replays=23 zones=2 peak_mb=825` |
| old | SAME | SAME | `replays=47 zones=25 peak_mb=540` |
| cly | SAME | SAME | `replays=20 zones=1 peak_mb=541` |

stderr は 3 組とも空。`ReplayBuilder` のモジュール管理が設定に登録しなくなっても、抽出結果は変わらない。

**`--auto`(既定 = 上限なし)**: 死者の宮殿 `replays=225 zones=17 peak_mb=566`、アライアンス `replays=27 zones=3 peak_mb=963`、Clyteum `replays=97 zones=1 peak_mb=540`。stderr は空。書かれたのは 1304・1345・1368 の 3 つで、3 つとも 2 回目(`ae_auto2`)とバイト単位で同じ。

**report.txt の差分**(`ae_auto2` → `ae_auto3`)は次の 3 行だけだった。

```
< zone=1304 written: written with 3 windows    > zone=1304 written: 3 validated windows
< zone=1345 written: written with 2 windows    > zone=1345 written: 2 validated windows
< zone=1368 written: written with 6 windows    > zone=1368 written: 6 validated windows
```

どれも文言の変更。窓ごとの判定(evaluated・confirmed・hits・kept/dropped)は 1 行も変わっていない。理由は次のとおり。

- 残った窓は、もともと全部 hits ≥ 1 だった。一番少ないのは 1368 の 39.9〜48.5(`evaluated=5 confirmed=5 hits=1`)で、ちょうど下限。
- その窓の確認 5 本のうち 4 本は、8.5 秒未満の実際の窓によるもの。3 秒以上に限っても確認は 5 のままなので、4 本とも 3 秒以上だった。ほかの窓は確認 = 的中。
- 1304 の分岐(判定 94.6 秒)は、早い側の 100.7 と、遅い側の 118.3(判定時刻より後)を数えて 3 窓で、今までと同じ。
- 死者の宮殿は、評価の対象になる公開窓がもともと 1 つも無いので、判定の変更が効くところが無い。

**ハーネス**(`--timelines ae_auto3 --sync all`):

| 条件 | replays | windows | hit | false_alarms | 2 回目との比較 |
| --- | ---: | ---: | ---: | ---: | --- |
| アライアンス(`tl_alliance`) | 23 | 84 | 72 | **0** | CSV が `sort` 後に一致 |
| 死者の宮殿 1〜10 | 43 | 12 | 0 | **0** | 一致 |
| 死者の宮殿 21〜30 | 27 | 2 | 0 | **0** | 一致 |
| 死者の宮殿 111〜120 | 8 | 4 | 0 | **0** | 一致 |
| Clyteum 97 本 | 97 | 202 | 194 | 59 | 一致 |

**回帰ツール**(最後のビルド、プラグインの dll は 5 つのツールすべてと同一):

- `TimelineRegression.dll`: `tests=47 passed=47 failed=0`
- `ExternalTimelineRegression.dll`: `tests=364 passed=364 failed=0`(新規 12、書き換え 4)
- `FFLogsTimelineExtract.dll --self-test`: `tests=15 passed=15 failed=0`

### 残る点

- Clyteum の false alarm 59 は、上限でも減らなかった(上の実験)。同期点の選び方の問題と推定しており、ゲートの範囲外のまま。この利用者の環境では手動の `1345-replay.json` が上に並ぶので、使われない。
- ゲーム内での確認(ボタン、状態の行、戦闘中の停止、report.txt)はまだしていない。配置後に利用者が確かめる。
- 1368 の 39.9〜48.5 は hits=1 で下限ちょうど。データが増えて的中が 0 になれば落ちる(落ちると次の窓の同期まで失うことは、2 回目の調査のとおり)。

## Clyteum の false alarm 59 の根本修正(途中で終わった pull は投票しない)

ブランチ `claude/survivor-majority`(`b19722d45` から)。コミットは `f7b9f81f1`(コードと回帰チェック)とこの節。push していない。

### 原因

`ReplayTimelineExtractor.Build` は、同期点(アクション ID・何回目か・種類)も窓(種類ごとの i 番目)も、**組の全 pull の過半数**に現れたものだけを残していた。途中で終わった pull(全滅も、早い撃破も)も分母に入る。

- Clyteum の組 4C3F(94 pull)は周回するボスで、ほとんどの pull は 2 周目に入る前に終わる(長さの p90 は 162 秒)。
- 2 周目の状態、たとえば 159.4 秒の 0xBEF4 AbilityUsed は、そこまで続いた 32 本のうち 30 本に現れる。それでも全 94 本の過半数(47)に届かず、落ちていた。
- フォロワーは、2 周目の 0xBEF4 を 1 周目の 75.8 秒の状態に合わせ直し(約 83 秒の後戻り)、1 周目の 88.4〜102.7 の窓をボスが死んだ後に予告していた。
- トレース(`BLM100__2026_08_21_00_07_27.log`)で確かめた。916.0 秒の 0xBEF4 で、時計が 159.2 → 75.8 に跳び、929 秒の損失を予告した。pull は 923.2 秒に終わっている。修正後は時計が 159.4 のまま進み、予告は出ない。`RPR100__2026_08_25_22_07_59.log` の 1069.4 秒も同じ形だった。

### 修正(`BossMod/Timeline/External/ReplayTimelineExtractor.cs`)

pull は、自分が続いた時点のことにだけ投票する。

- 同期点: その鍵の標本時刻の中央値まで続いた pull(`Duration` ≥ 中央値)の数を `eligible` とする。
- 窓: その種類の i 番目の窓の開始の中央値まで続いた pull の数を `eligible` とする。
- 残す条件は `標本数 ≥ Math.Min((pulls.Count + 1) / 2, Math.Max(2, (eligible + 1) / 2))`。
- 小さな関数 `RequiredSamples(durations, medianTime)` にまとめ、状態と `MergeWindows` の両方で使う。`MergeWindows` は各 pull の長さを受け取るようにした。
- 今までの過半数より多くを求めることは無い。したがって今まで残っていたものは落ちない。pull が 1〜2 本(実際は 4 本まで)なら今までと同じ。緩めるときも標本は 2 つ以上要る。
- 中央値と信頼度の計算は変えていない。

### 回帰チェック(`tools/timeline_regression`、TDD)

要約を直接作る補助 `Lasting` を足し、3 つのチェックを足した。

- (a) 6 pull のうち、長さ 160 秒の 2 本が 140 秒の状態(0x1000 の 2 回目)と NoTarget 120〜135 を共有し、残り 4 本は 100 秒で終わる。→ 状態も窓も残る。
- (b) 6 本とも 160 秒で、50 秒の状態と 120〜135 の窓が 2 本にしか無い。→ 落ちる。
- (c) 160 秒まで続いたのが 1 本だけで、その 1 本だけが 140 秒の状態と窓を持つ。→ 落ちる(標本 2 つ以上)。

修正前のプラグインでは (a) だけが落ちた(`tests=50 passed=49 failed=1`、`second-loop state dropped`)。修正後は次のとおり。既存のゴールデン(`summaries give the same timeline as replays` ほか)も変わらず通る。

- `TimelineRegression.dll`: `tests=50 passed=50 failed=0`
- `ExternalTimelineRegression.dll`: `tests=367 passed=367 failed=0`
- `FFLogsTimelineExtract.dll --self-test`: `tests=15 passed=15 failed=0`

### 実データでの比較

修正前(`b19722d45`)と修正後(`f7b9f81f1`)で同じものを回した。出力は `%TEMP%\sm_base_*` と `%TEMP%\sm_new_*`。ツールのバイナリはスクラッチパッドに複製してから回した(ビルド中に走っている計測を壊さないため)。ハーネスは同時に 2 本まで。

- ハーネス 7 条件: 「Step 4」と同じ引数で、`--csv T\sm_{base,new}_X.csv`。
- `--auto`: 「実行したコマンド」と同じ 3 群(死者の宮殿 225 本、アライアンス 27 本、Clyteum 97 本)を、空の `%TEMP%\sm_{base,new}_auto` に順に出した。
- 自動ファイルのハーネス: `--timelines T\sm_{base,new}_auto --sync all` を `tl_alliance`、Clyteum 97 本、死者の宮殿 3 セットに対して回した。
- 普通のモード(`--out`): `tl_alliance`、旧 47 本、Clyteum 20 本(`ae_cly_files.txt`)、Clyteum 97 本。出力は `sm_{base,new}_cli_*`。

修正前の値は、この文書の前回の値と一致した。修正前の `ae_auto3` と自動ファイル 3 つがバイト単位で同じで、Clyteum の CSV も一致した。

**ハーネス 7 条件**: 7 つとも、CSV(`sort` 後)と集計行が修正前と完全に一致した。

| 条件 | windows | hit | false_alarms | inwin |
| --- | ---: | ---: | ---: | ---: |
| A `--holdout` アライアンス | 84 | 67 | 0 | 77 |
| B USR アライアンス | 84 | 72 | 0 | 82 |
| C 旧 47 本 | 41 | 4 | 0 | 4 |
| D `--holdout` 旧 47 本 | 41 | 20 | 3 | 21 |
| E `--gate` 旧 47 本 | 20 | 2 | 0 | 2 |
| F FFLogs | 38 | 34 | 2 | 36 |
| G Replay USR | 38 | 36 | 0 | 36 |

- A と D(1 本抜き)では、抽出し直したタイムラインに状態が増える(旧 47 本の 1097・1154 など、下記)。それでも的中・誤予告・CSV の行はどれも変わらなかった。
- B・C・E・F・G は、抽出し直さない条件。

**自動ファイル**:

| 条件 | windows | hit | false_alarms(修正前 → 後) | inwin | CSV |
| --- | ---: | ---: | --- | ---: | --- |
| アライアンス | 84 | 72 | 0 → **0** | 82 | 一致 |
| Clyteum 97 本 | 202 | 194 | **59 → 0** | 194 | 下記 |
| 死者の宮殿 1〜10 | 12 | 0 | 0 → 0 | 0 | 一致 |
| 死者の宮殿 21〜30 | 2 | 0 | 0 → 0 | 0 | 一致 |
| 死者の宮殿 111〜120 | 4 | 0 | 0 → 0 | 0 | 一致 |

- `report.txt` は修正前と一致した。窓ごとの判定(evaluated・confirmed・hits・kept/dropped)も、書かれた zone(1304・1345・1368)も同じ。
- `--auto` の画面出力も、時間(`summarize_ms`・`rebuild_ms`・`peak_mb`)を除いて一致した。
- 自動ファイルの中身は、3 つとも状態が増えた。
  - 1304: +9。493C・48F8・460F の終盤。
  - 1368: +4。4D5C・4DEE の終盤。
  - 1345: 4C2C +1、4C3F +38、4C28 +3。
- 1345 の 4C3F には、公開対象にならない窓が 10 個増えた。NoTarget 8 個と BossUntargetable 2 個。9 個は信頼度 0.20 で、残る 1 個(NoTarget 591.7〜597.0、信頼度 0.86)は 5.3 秒しかない。公開対象の窓は今までの 2 つ(21.9〜31.0 と 88.4〜102.7)のまま。
- Clyteum の CSV では、`pull=-1` の 59 行がすべて消えた。
- ほかに、的中の行が 4 つだけ変わった。どれも的中のままだが、lead が 17.7 → 5.0 になった(下の「懸念」)。

**普通のモード**: 変わった zone は次のとおり。ほかの zone の json はバイト単位で同じ。stderr は空。

| 組 | zone | states | windows(no_target) | confidence |
| --- | --- | --- | --- | --- |
| al | 1304 | 167 → 176 | 7(4)のまま | 0.68 のまま |
| al | 1368 | 200 → 204 | 13(8)のまま | 0.82 のまま |
| old | 1097 | 90 → 100 | 5(3)のまま | 0.53 のまま |
| old | 1154 | 105 → 132 | 2(1)のまま | 0.80 のまま |
| cly(20 本) | 1345 | 158 → 161 | 17(4)のまま | 0.68 のまま |
| cly97 | 1345 | 157 → 199 | 17(4) → 27(12) | 0.68 → 0.57 |

- 増えたのは、どれも終盤の状態。1 つも消えていない。
- 窓が増えたのは Clyteum 97 本の 4C3F だけ。
- 1345 の timeline 全体の信頼度が下がったのは、信頼度 0.20 の窓が平均に入ったため。この値は、ストアが同じ出どころの候補を並べるときにしか使われない。

### DetectBranch と BuildBossSet

- `DetectBranch` にも、全 pull に対する過半数の判定が 1 つある。`(clusters[0].Count + clusters[1].Count) * 2 <= perPullWindows.Count` なら分岐なしにする判定。周回の後半で分かれる分岐があれば、途中で終わった pull に負けて同じように捨てられる。
- 変えなかった。理由は 3 つある。
  - 回帰チェック「a branch needs a majority of the boss set's pulls in its two clusters」(10 本中 6 本が窓の前に全滅 → 分岐なし)が、「全滅した pull も分岐に反対する」という意図をはっきり固定している。
  - 分岐は同期点より影響が大きい。誤って分けると、両方の兄弟が標本を半分ずつしか持たない。HP しきい値も 2 つの塊だけから学ぶ。
  - 今のデータには当たるものが無い。診断で、アライアンス(`tl_alliance`)・Clyteum 97 本・旧 47 本の全組を調べた。2 つの塊ができた位置は 1304 の 4919(13 本中 5+8)と 1368 の 4DA6(10 本中 6+4)の 2 つだけで、どちらもこの判定を通っていた。
- `BuildBossSet` は、兄弟をそれぞれ `Build` で作るので、今回の規則がそのまま効く。`AcceptDivergence` の 80% は、その窓を持つ早い側の pull の中での割合なので、途中で終わった pull は入らない。

### 懸念: 複数回の挑戦にまたがるエンカウンタ記録

- 4C3F の 94 pull のうち、180 秒を超えるのは 3 本だけ(829・833・875 秒)。3 本とも、同じ組の別の pull を 1 本ずつ含んでいる。全滅で閉じなかったエンカウンタ記録が次の挑戦まで続き、その挑戦も別の pull として記録されている。
- 180 秒より後に続いた pull は、この 3 本だけになる。そのため、この 3 本の「つなぎ合わせた時刻」の状態(標本 2〜3)と窓が残るようになった。
- `BLM100__2026_08_21_00_07_27.log` と `RPR100__2026_08_25_22_07_59.log` をトレースした。どちらも同じ形だった(下は BLM のもの)。
  - 118 秒付近で終わった挑戦の直後に、次の挑戦が始まる。
  - その最初の 0xBEF4 詠唱(760.7 秒)が来たとき、履歴には前の挑戦の終わり(C4F1・BEF6・C4B9、102.8〜114.4 秒)が残っていた。フォロワーはこれらを、3 本の記録にある 166.9〜178.5 と 204.6 の状態に揃えた。その場の 4.2 秒より点数が高かったため。
  - 時計は 4.1 → 204.6 に跳んだ。12.7 秒後、0xBEE6 の詠唱で後戻り(点数 4)して 16.9 に戻った。
  - 21.9 秒の窓は、17.7 秒前ではなく 5.0 秒前に予告された。的中は保たれ、誤予告は出ていない。
  - この 2 つの窓は、長い記録の pull と実際の pull の両方に数えられるので、CSV では 4 行になる。
- 考えられる対策は 2 つある。どちらもこの修正には入れていない。
  - `FindPulls` で、同じ組の別の pull を含むエンカウンタ pull を分けるか捨てる。
  - 緩めたときに要る標本の下限を 3 にする。

## 次の挑戦まで開いたままのエンカウンタ記録を切る

ブランチ `claude/split-attempts`(`f689fc687` から)。コミットは `be9032267`(コードと回帰チェック)とこの節。push していない。

### 調べたこと

使い捨てのプローブで、リプレイ 421 本(BossModReborn 374 本と BossMod 47 本)のエンカウンタ記録 655 件をすべて調べた。記録ごとに次のものを出した。

- 長さ
- 主ボスの HP リセット(90% 未満に下がった後、99.5% 以上に戻る)
- 主ボスの死亡・消滅・再出現と、同じ OID の別アクター
- `ActorState.OpCombat`
- パーティの全滅と director の wipe(0x40000005)
- リセット後の最初の詠唱

1 つの記録に同じボスへの挑戦が 2 回以上入っていたのは、Clyteum 4C3F の 3 件だけだった。3 件とも同じ形をしている。

| 記録 | 長さ | 主ボスが消えた | 主ボスの死亡 | 次の挑戦の記録 | 記録を閉じたもの |
| --- | ---: | ---: | --- | ---: | --- |
| `BLM100__2026_08_21_00_07_27` | 875.1 | 118.0 | なし | 136.0 から | リプレイの終わり |
| `RPR100_Vi_Ni_2026_08_19_20_35_04` | 828.9 | 182.4 | なし | 200.3 から | リプレイの終わり |
| `RPR100__2026_08_25_22_07_59` | 833.2 | 182.5 | なし | 200.5 から | リプレイの終わり |

- 全滅の後、主ボスは死なずに消え(despawn)、同じ ID では戻っていない。
- 次の挑戦は同じ OID の別アクターで、そのアクター自身の記録がふつうに作られている。つまり、長い記録の後半にある挑戦は、もう別の pull として数えられていた。
- 疑わしく見えた残りの 21 件は、どれも挑戦 1 回だった。
  - 零式(Anabaseios)の 11 件は全滅で、director の wipe で記録が正しく閉じている。
  - Vanguard 411E は HP 0 からの変身、Ageless Necropolis 4870 は同じ ID での消滅と再出現。
  - 死者の宮殿の 2 件と Skydeep Cenote の 1 件は、全滅でボスが消えた時に記録が閉じている。
  - Jeuno 4692 の 4 件は、戦闘フラグより先に起動するモジュール。Vanguard 4479 は同じ OID の 2 体。

**記録が閉じなかった理由(モジュールの寿命)**: BMM のログを再生時刻付きで出すプローブで確かめた。

- 3 件とも、ボスが消える直前に `was moved to pending status` が出ている。死んだプレイヤーの位置がボスから 1776.6 離れていると報告され、読み込み距離(100 以上)を超えたため。
- `BossModuleManager` は、主アクターが死んだか消えた pending のモジュールを `Dispose` するだけで、`ModuleUnloaded` を出さない。
- `ReplayBuilder` は `ModuleUnloaded` でしか記録の終わりを書かない。そのため、`Finish()` がリプレイの終わりで閉じた。
- 421 本全体で、読み込み済みのモジュールが pending に移ったのは 4 回だけだった。Doomtrain の 1 回(列車で離れ、57 秒後に戻った。記録は正しく閉じた)と、この 3 件である。

**切る合図に使えないもの**: 主ボスが記録の終わりより 1 秒以上前に消えている記録は、655 件中 59 件ある。

- この 3 件を除く 56 件は、主ボスが消えた後もモジュールが動き続け、自分で閉じた正しい記録である。Windurst 4DA6、Lapis Manalis 3D56、San d'Oria 4885 のような複数ボスの戦闘などで、リプレイの終わりで閉じたものは 1 件も無い。
- したがって「主ボスが消えたら切る」だけでは誤りになる。
- 一方、同じ OID の別の記録が中で始まる記録は、この 3 件だけだった。

### 修正(`BossMod/Timeline/External/ReplayTimelineExtractor.cs`)

- `FindPulls` のエンカウンタ側に `EncounterEnd` を足した。
- 同じボス OID のモジュールは、同時に 1 つしか読み込まれない(`ActorAdded` は、同じ OID が読み込み済みなら新しいモジュールを pending にする)。だから、ある記録の中で同じ OID の別の記録が始まったなら、その記録の終わりは記録されていない。
- そのときだけ、記録の終わりを主ボスが world から去った時刻にする。主ボスがまだいれば、次の記録の開始で切る。
- 切った結果が `MinPullSeconds` より短ければ捨てる。切らない記録には、今までどおりこの下限をかけない。
- 分割ではなく切り詰めにした。後半の挑戦にはもう自分の記録があり、分割すると同じ挑戦を 2 回数えるためである。

**起点**: 切った pull の起点は、今までどおり `enc.Time.Start`。次の挑戦の起点は、その挑戦自身の記録の `enc.Time.Start` になる。これは、既定の `CheckPull`(標的可能かつ戦闘中)でモジュールが起動した時刻である。

- 全 655 件で、`enc.Time.Start` と主ボスの戦闘開始(`OpCombat`)の差を測った。
  - 1 ms 以内が 630 件、0.5 秒以内が 2 件、5 秒以内が 15 件、それを超えるのが 4 件。戦闘開始の op が無いものが 4 件。
- 3 件の次の挑戦の記録も、新しいアクターの戦闘開始(136.0・200.3・200.5 秒)と同じ時刻に始まっている。
- したがって、新しい起点を作る必要は無い。

**挑戦 1 回の記録は変わらない**: 421 本の全 pull(1,015 本)を修正前後のプラグインで出し、比べた。比べたのは、区間・敵・アクション数・要約 JSON のハッシュである。違ったのは次の 3 本だけで、ほかは一致した。

| 記録 | 長さ | 敵 | アクション |
| --- | --- | --- | --- |
| BLM 00_07_27 | 875.1 → 118.0 | 25 → 1 | 414 → 28 |
| RPR Vi_Ni 20_35_04 | 828.9 → 182.4 | 25 → 1 | 308 → 45 |
| RPR 22_07_59 | 833.2 → 182.5 | 25 → 1 | 304 → 45 |

### 回帰チェック(`tools/timeline_regression`、TDD)

Clyteum と同じ形の合成リプレイを作る補助 `LeftOpen` を足した。1 回目のボスは消え、その記録は 600 秒まで開いたまま。2 回目は同じ OID の別アクターで、150〜300 秒の記録を持つ。チェックは 4 つ。

- (1) 1 回目のボスが 130 秒に消える。→ pull は 12〜130 と 150〜300 の 2 本。zone とボスは記録のまま。1 本目に 2 回目のボスは入らない。
- (2) 1 回目のボスが最後まで残る。→ 1 本目は 2 回目の記録の開始(150)で終わる。
- (3) 1 回目のボスが 25 秒で消える。→ 13 秒しかないので捨て、150〜300 の 1 本だけ。
- (4) 主ボスが 50 秒で消えても別のボスで 190 秒まで続く記録と、閉じた後に始まる同じボスの記録。→ どちらもそのまま。

修正前のプラグインでは (1)〜(3) が落ちた(`tests=54 passed=51 failed=3`)。(4) は今までの振る舞いを固定するもので、修正前も通る。修正後は次のとおり。

- `TimelineRegression.dll`: `tests=54 passed=54 failed=0`
- `ExternalTimelineRegression.dll`: `tests=367 passed=367 failed=0`
- `FFLogsTimelineExtract.dll --self-test`: `tests=15 passed=15 failed=0`

### 実データでの比較

前回の修正後(`f7b9f81f1`、出力 `sm_new_*`)と、今回(出力 `%TEMP%\sa_new_*`)を比べた。コマンドは前節と同じ(`run_all.sh` と `run_cli.sh`)。ツールのバイナリは複製してから回し、ハーネスは同時に 2 本までにした。

**ハーネス 7 条件**:

| 条件 | windows | hit | false_alarms | inwin | CSV |
| --- | --- | --- | --- | --- | --- |
| A | 84 | 67 | 0 | 77 | 一致 |
| B | 84 | 72 | 0 | 82 | 一致 |
| C | 41 | 4 | 0 | 4 | 一致 |
| D | 41 | 20 | 3 | 21 | 一致 |
| E | 20 | 2 | 0 | 2 | 一致 |
| F | 38 → 34 | 34 → 32 | 2 → 2 | 36 → 34 | 4 行減 |
| G | 38 → 34 | 36 → 34 | 0 → 0 | 36 → 34 | 4 行減 |

- F と G で変わったのは、正解側(ハーネスが `FindPulls` から作る窓)だけ。`tl_eval` に入っている BLM 00_07_27 の長い記録から、4 行が消えた。
  - 次の挑戦の窓の重複 2 つ(157.9 と 224.7、どちらも的中)。
  - ボスとボスの間の、何もいない時間の窓 2 つ(402.1〜430.2 と 474.3〜533.0、どちらも外れ)。
- 残った行は、予告も含めて修正前と一致した。的中率は F が 0.895 → 0.941、G が 0.947 → 1.000。

**自動ファイル**:

| 条件 | windows | hit | false_alarms | inwin | return_err_p90 |
| --- | --- | --- | --- | --- | --- |
| アライアンス | 84 | 72 | 0 | 82 | 一致(CSV も一致) |
| Clyteum 97 本 | 202 → 190 | 194 → 188 | 0 → 0 | 194 → 188 | 5.2 → 0.1 |
| 死者の宮殿 1〜10 | 12 | 0 | 0 | 0 | 一致(CSV も一致) |
| 死者の宮殿 21〜30 | 2 | 0 | 0 | 0 | 一致(CSV も一致) |
| 死者の宮殿 111〜120 | 4 | 0 | 0 | 0 | 一致(CSV も一致) |

- Clyteum で消えた 12 行は、3 本の長い記録の窓である。重複 6 つ(すべて的中)と、ボスとボスの間の窓 6 つ(すべて外れ)。
- 本物の窓 190 個に限れば、的中は修正前も後も 188 で変わらない。
- 前回の懸念だった lead 5.0 の 4 行は無くなった。
  - 2 行は重複の窓として消えた。
  - 残る 2 行(BLM 00_07_27 と RPR 22_07_59 の 2 本目の pull の 21.9 秒の窓)は、lead が 17.7 に戻った。
  - 的中した行の lead は、どれも 17.4 以上になった。
- 誤予告は 0 のまま。
- `report.txt` と `--auto` の画面出力(時間を除く)は一致した。書き換わった自動ファイルは `1345-auto.json` だけ。

**`1345-auto.json` の中身**:

- 4C3F の状態は 70 → 37。長い記録がつなぎ合わせていた時刻(182 秒以降)の状態が、すべて消えた。
- 信頼度 0.20 の窓 9 個と、0.86 の短い窓 1 個も消えた。公開対象の窓(21.9〜31.0 と 88.4〜102.7)は変わらない。
- timeline の信頼度は 0.574 → 0.676(前回の修正前と同じ値)。
- 159〜178 秒の終盤の状態 7 つ(0xBEF4・BEF8・C4F1・BEF6・C4B9)は残った。今の標本は、そこまで本当に続いた pull だけから来る。
  - 4C3F の pull の長さの最大は 875 → 182 秒になった。
  - 標本数は 29・5・4・3・2・2・2。172 秒より後の 3 つは、182 秒まで続いた RPR の 2 本(3 人が倒れた後、ボスが消えるまで戦った挑戦)だけが支えている。
  - 中央値は 0.02〜0.14 秒ずれた。
- 0xBEE6 の 1 回目(CastStart 16.9 と AbilityUsed 21.9)が消えた。理由は下の節。

**普通のモード**: アライアンスと旧 47 本は、json も画面出力もすべて一致した。変わったのは 1345 だけ。

| 組 | states | windows(no_target) | confidence |
| --- | --- | --- | --- |
| cly(20 本) | 161 → 157 | 17(4)のまま | 0.68 のまま |
| cly97 | 199 → 166 | 27(12) → 17(4) | 0.57 → 0.68 |

- cly(20 本)には BLM 00_07_27 が入っている。4C3F で、0xBEE4 と 0xBEE6 の 1 回目(16.9 秒と 21.9 秒)が消え、ほかの状態は中央値が 0.05 秒以内でずれただけ。

### 0xBEE4・0xBEE6 の 1 回目が消えた理由

- この 2 つは、どちらか一方が 16.9 秒か 83 秒前後に来る詠唱である。
  - 97 本の 4C3F 94 pull で、0xBEE6 の 1 回目は 16.7〜16.9 秒に 27 本、82.6〜84.1 秒に 19 本あり、無い pull が 48 本。
- 前は、BLM 00_07_27 の長い記録が次の挑戦の詠唱(記録の 152.9 秒)を「1 回目」として数えていた。そのため、標本がちょうど過半数(94 本中 47)に届いていた。今は 46 本で、届かない。
- 20 本の組でも同じで、0xBEE4 と 0xBEE6 はどちらも 17 本中 9 → 8 になった。
- この状態が無くなったため、次の不具合も消えた。
  - 0xBEE6 を 83 秒前後に詠唱する pull では、88 秒の AbilityUsed が 21.9 秒の状態に合っていた。時計が 1 つ目の窓(長さ 9.1 秒)に戻り、2 つ目の窓(14.3 秒)の戻りを 5.2 秒早く予告していた。
  - Clyteum の CSV で戻りの誤差が -5 秒前後だった 29 行が、0 行になった。29 行とも、0xBEE6 を 82.6〜84.1 秒に詠唱した pull の 2 つ目の窓である。この 29 行は前回の修正前(`sm_base`)からあった。

### 懸念

- 根本の原因は `ReplayBuilder` と `BossModuleManager` の間にある。pending のモジュールが unload なしで捨てられると、記録はリプレイの終わりまで開いたままになる。`ForceUnload` も、pending のモジュールは `Dispose` だけしている。
  - この修正は `FindPulls` の中だけなので、ほかのリプレイ解析(詳細ウィンドウや Analysis)には、今も長い記録が見える。
  - 手当ては、`ReplayBuilder` が捨てられたモジュールの記録を閉じること。今回は入れていない。
- `ReplaySummaryCache.Version` は上げていない。`PullSummary` の形は変わっていないためである。
  - ただし、この 3 本のリプレイの要約が古いビルドでキャッシュ済みなら、リプレイが変わるまで古い長い pull が使われる。
  - 今、利用者の timelines フォルダに `auto/cache` は無い。
- 172〜178.6 秒の状態 3 つは、182 秒まで続いた RPR の 2 本だけが支えている(標本 2)。どれも本物の詠唱で、ハーネスへの悪い影響は見られなかった。
- 依頼で想定していた「Clyteum 194/202 以上」は、字面どおりには満たさない。正解の窓そのものが 202 → 190 に減ったためである。本物の窓での的中(188)は変わらない。
