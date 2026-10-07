# 不規則イベント(--irregular)の計測

タイムラインに載らない、予告のない中断で GCD が止まる/ずれる状況を再現し、各ローテーションモジュールがどう耐えるかを測る。
`DisengageDriver`(予告付きの避け)とは独立で、イベントの時刻・種類・長さは seed とシナリオだけで決まる(全ジョブ・全ルートで同じ列)。
オプション無しの実行はバイト同一のまま(summary.csv の末尾に `irr_*` 列が追加されただけ)。

## 種類(`IrregularDriver.cs`)

| kind | 再現方法 | 長さ | ゲーム上の根拠 |
|---|---|---|---|
| `lockout` | プレイヤーに状態異常を付与(発生源=ボス)。Stun(2)/Sleep(3)/Down for the Count(625) は全アクション、Pacification(6) は WS、Silence(7) は魔法、Amnesia(1092) はアビリティを拒否 | 0.5–8 s | Status シート: 2/3/625 は `LockActions`+カテゴリ 2(「Unable to execute actions」)、6「Unable to use weaponskills.」(ＷＳ不可)、7「A stifling magic is preventing casts.」(沈黙)、1092「Unable to use abilities.」(アビリティ不可)。拒否は選択後に起こりフレームを消費(ActionManagerEx: `Can't execute ... status`、次フレーム再試行) |
| `los` | 敵の `Visibility=Blocked`。`ActionQueue.CanExecute` が `RequiresLineOfSight` のアクションを候補から外す(プレイヤー向け敵対象アクション 469 件すべて LoS 必須) | 1–10 s | `ActionQueue.cs` `entry.Target?.Visibility == Blocked && def.RequiresLineOfSight` |
| `range` | ノックバック(20 y/s)で基本射程+2–6 y の外へ飛ばし、hold 中は戻れない。hold 終了後 6 y/s で歩いて戻り、基本射程の内側に入った時点で止まる(これが終了時刻。遠隔/キャスターはその瞬間から撃てる/詠唱できる。近接は終了後に定位置まで歩き続ける)。近接は基本射程 3 y(20 y の遠隔攻撃は使える)、遠隔/キャスターは 25 y | hold 1–15 s + 飛行 + 復帰 | 射程判定は `ActionQueue.CanExecute`(射程+双方のヒットボックス) |
| `loss` | 対象を untargetable に(タイムライン窓とは別、ヒントなし) | 1–10 s | 既存の target_available 機構 |

詠唱中に lockout(該当カテゴリ)が入ると詠唱は中断されリキャストは払い戻し(移動中断と同じ扱い)。
Sleep はダメージで解けるが、ハーネスはプレイヤーへの被弾を持たないので満了まで続く。
エピソードは互いに重ならず(間隔 2 s 以上)、タイムラインの消失窓の前後(開始 1 s 前〜復帰 4.5 s 後)と戦闘終了 3 s 前には置かない
(復帰後 3.5 s の GCD 検査をエピソード自身が踏まないようにするため)。発生間隔は 60/rate × 0.5〜1.5 倍で、エピソード長が長いときはそれより伸びる。

## 使い方

```
# 単発: 既存コマンドに付ける(client-reject 計上を含意)
XanTimelineHarness.exe timeline-combat-matrix --job mnk --irregular 1 [--irregular-rate 2] [--irregular-kinds lockout,los,range,loss|all]
#  → job 行の後に irregular job=... episodes= seconds= block_s= idle_in= idle_out= gcd_in= resume_* refused_* の行、最後に client_reject 表
#  → XAN_HARNESS_TRACE_DIR のトレースには irregular,<kind>,<start>,<end>,<秒>,<詳細>,blocks_gcd=,refused=,gcd_in=,idle_in=,resume=,resume_excess= 行

# 比較: 同じシナリオをイベント無し(baseline)と有りで回し、ジョブ別の表を出す
XanTimelineHarness.exe irregular-compare --job rpr --irregular-seeds 1,2,3 --irregular-rates 2,4 --irregular-kind-sets "lockout;los;range;loss;all" --irregular-out out.csv
```

`extra_loss_ratio = (baseline − irregular − expected) / expected`、`expected = block_s × baseline の対象可能 1 秒あたり威力(total)`。
`block_s` は「このジョブの GCD を実際に止める」エピソード秒(近接への Silence、Amnesia は除外)。0 なら止まった時間相当だけ失い、正なら余計に失い、負なら(遠隔フィラー、CD の持ち越し等で)それより少なく済んだ。
`resume_excess` はエピソード終了→最初の GCD(キャスターは詠唱開始)までの遅れから、終了時点で回っていたリキャストを引いたもの。
`no_resume` は次のエピソード/終了まで 3 s 以上あるのに GCD が一つも出なかった回数。

baseline 側も `Irregular.EnforceBaseline` で同じ射程・移動の強制と client-reject 計上を通すので、差はイベントの有無だけ。
DMU(dancing_mad)の実行にはイベントを入れない(DisengageDriver と同じ)。

## 計測結果(2026-10-03)

`F:\bossmodreborn\claude_works\bmr_irreg\report.md`(集計は同フォルダの `aggregate.ps1`、生データは `results/*.csv`)。
