# HP で分岐する離脱の予告 設計

日付: 2026-09-29
親仕様: `2026-09-26-unified-timeline-design.md`
前提調査: memory `dt-alliance-hp-gated-disengage`

## 1. 問題

San d'Oria: The Second Walk の Ultima(A22、OID 0x4919)は、離脱(26.5 s)が pull 開始から 100.7 s か 118.2 s のどちらかで起きる(自リプレイ 13 本で 5 対 8)。どちらになるかはボス HP で決まる。早い側は離脱 6.1 s 前に移行詠唱 44306 を始める。

現行の抽出器は窓を pull 間で番号対応させて中央値をとるので、2 つの時刻が 1 本の窓(開始 118.2 s、確信度 0.2)にまとまり、公開されない。移行詠唱を見た後も予告は出ない(0/13)。

## 2. 測定で分かったこと

- 早い分岐と遅い分岐は、移行詠唱の時刻(94.6 s)のボス HP で完全に分かれる(早い側 ≤ 46.1%、遅い側 ≥ 46.7%)。ただし分岐を決める本当の時刻はデータからは特定できない(86〜100 s のどこでも余裕 0.3〜1.2 pt で分かれる)。
- 線形外挿(直近 10 s の HP 傾き)で 94.6 s の HP を予測し、しきい値から 3 pt 以上離れた時だけ分岐を決めると、外挿距離 8 s 以内なら leave-one-out で 13/13 正解。12 s 以上では誤判定が出る。

## 3. 設計

### 3.1 形式

`TimelineSequence` に省略可の `Branch` を足す:

```
TimelineBranch(int Group, float DecisionTime, float? HpThreshold, bool Below)
```

同じ `Group` の sequence が分岐の兄弟。`DecisionTime` は分岐点(早い兄弟に固有の最初の同期点の時刻)。`Below = true` の兄弟は「DecisionTime のボス HP ≤ HpThreshold」のとき、`Below = false` の兄弟はそれを超えるときに当たる。しきい値が学習できなければ `HpThreshold = null`(HP では選ばず、詠唱の有無だけで選ぶ。NaN は既定の System.Text.Json で書けないので null で表す)。

既存 JSON は `Branch` 無しで従来どおり。`Branch` が null の sequence は JSON にキー自体を書かない(分岐の無い timeline の出力はバイト同一)。生成器の mirror は分岐を出さないので変更しない。

### 3.2 抽出器(`ReplayTimelineExtractor`)

同じボス集合の pull が 4 本以上あるとき、pull ごとの NoTarget 窓(8.5 s 以上のもの、`MinPublishedLoss` と同じ基準)を番号順に見て、最初に開始時刻が 2 つの塊(隙間 > `BranchGapSeconds = 5 s`)に分かれ、各塊に `MinBranchPulls = 2` 本以上ある番号を分岐点とする。塊が 3 つ以上なら分岐させない。1 本だけの塊がある番号は分岐点にせず次の番号へ進む。

番号ずれによる偽の 2 塊は分岐させない。長さが 8.5 s をまたぐ窓(pull によって 8.5 s 未満で数えられない。Windurst 0x4DA6 の 40 s の窓は 5.8〜10.2 s)や、一部の pull にだけある余分な長い窓(導入・雑魚)で番号がずれると、同じ窓が別の番号と比べられる。そのため、どちらかの塊の pull の過半が、もう一方の塊の開始時刻(中央値)の ±`BranchGapSeconds` 以内に自前の NoTarget 窓(長さ不問)を持つなら分岐させない。本当の分岐では、片方が離れた時刻にもう片方はまだ攻撃できる(Ultima の早い側は 118.2 s にはすでに離脱中で、そこから始まる窓は持たない)。

2 つの塊を合わせてボス集合の pull の過半(半数超)に満たなければ分岐させない(大半が分岐窓の前に全滅していると、分岐の記述として少なすぎる)。

2 つの塊の間の隙間(遅い塊の最小 − 早い塊の最大)が、幅の広い方の塊の幅(最大 − 最小)の `BranchSeparationRatio = 3` 倍に満たなければ分岐させない。火力の差で開始時刻がばらついただけなら、隙間はばらつきと同じくらいにしかならない。たとえば 100, 101, 103, 104 と 110, 112, 115, 118 は 5 s を超える隙間で 2 つに分かれるが、隙間 6 s は幅 8 s の 3 倍に足りないので分岐させない。Ultima は隙間 17.4 s、幅 1.1 s。

分岐点が見つかったら、早い塊と遅い塊の pull で別々に `Build` し、兄弟 sequence 2 本を作る。どちらの塊にも入らない pull(分岐窓の前に終わった pull)は兄弟のどちらにも使わない。

- `DecisionTime`: 早い兄弟の同期点を時刻順に見て、次の 3 つを全て満たす最初のものの時刻。
  - 早い塊の窓の開始(中央値)の前 `BranchMaxLeadSeconds = 30 s` 以内にある。
  - その ID を遅い兄弟が「早い窓の開始 + `BranchCastExclusionSeconds`(フォロワーの `SyncTolerance` = 6 s)」までに一度も持たない。遅い兄弟がそこまでに同じ ID を持てば、時刻が違っても分岐を示さない(未決の間に遅い pull でも見えてしまう)。「早い窓の開始 + 6 s」より後にだけ持つ ID は数える。
  - 元の pull でも同じことが言える(`AcceptDivergence`)。兄弟は塊ごとの多数決で作るので、遅い pull の一部だけにある ID は遅い兄弟から消えてしまい、兄弟だけを比べると見逃す。そこで、その ID が早い pull の 80% 以上で同期点の時刻の ±2 s 以内に現れ、しかも遅い pull のどれにも「早い窓の開始 + 6 s」までに一度も現れないことを求める。満たさなければ次の候補へ進む。

  どれも満たさなければ早い窓の開始時刻にする(`DecisionTime` は早い窓の開始より後にならない。未決の間に早い窓が「分岐前の共通の窓」として公開されないように)。Ultima の 44306 は遅い兄弟にもあるが 112.2 s(早い窓の開始 100.7 + 6 より後)なので分岐点になる。
- `HpThreshold`: 各 pull の主ボス(BossOIDs のうち `DecisionTime` に存在するものの中で MaxHP 最大、存在しなければその pull は除外)の `DecisionTime` での HP%。両側とも 2 本以上の標本があり、早い塊の最大 < 遅い塊の最小なら中点、そうでなければ null。
- `Group`: 早い兄弟の sequence 番号。sequence を絞り込んで番号を振り直すツール(fflogs の `KeepBossSequences`)は `Group` も付け直し、兄弟の片方が落ちたら残った方の `Branch` を消す。

### 3.3 フォロワー(`ExternalTimelineHints`)

追っている sequence に `Branch` があり、その pull で分岐が未決のとき:

1. **詠唱で決める**: 早い兄弟の分岐点の同期点が観測されれば早い兄弟に確定する(既存の整合で兄弟へ移るのを確定扱いにする。ただし整合で兄弟へ移るのを分岐の確定とみなすのは、一致した項目の時刻が `DecisionTime − 0.1 s` 以後のときだけ。それより前は兄弟が同じ戦闘を記述しており、片方だけが多数決で残した項目は分岐について何も言わない)。`DecisionTime + BranchCastGrace(1 s)` を過ぎても観測されなければ遅い兄弟に確定する。
   - いったん決まった後に整合が早い兄弟へ移ったとき、決定を早い兄弟に変えるのは、一致した項目が分岐点の同期点のときだけ。早い兄弟のほかの項目は、早い塊の多数決で残っただけかもしれず、余裕をもって出た HP の判定を覆す根拠にならない。遅い兄弟へ移るときは、これまでどおり決定を変える。
   - 分岐点の同期点は、抽出器の `DivergenceTime` と同じ判定で決める。早い兄弟の `DecisionTime ± 0.1 s` にある詠唱・効果の項目のうち、遅い兄弟が「早い窓の開始 + `SyncTolerance`」までにその ID を一度も持たないものを指す。複数あればどれを観測してもよい。遅い兄弟が「早い窓の開始 + 6 s」までに持つ ID は、時刻が違っても数えない。それより後にだけ持つ ID は数える。フォロワーは元の pull を持たないので、80% の確認(§3.2)は抽出器が `DecisionTime` を選ぶときに済ませておく。
   - 観測は、時計が `DecisionTime` から ±`SyncTolerance` 以内のときだけ数える。遅い兄弟が今の時計の ±`SyncTolerance` 以内に同じ ID を持つなら数えない。
   - 整合がちょうどその同期点に時計を置いたとき(時計が未確認だった場合など)も、早い兄弟に確定する。
   - 早い兄弟に分岐点の同期点が無いとき(`DecisionTime` が早い窓の開始で打ち切られたとき)は、消失で決める。未決の間に、早い兄弟が今の時計で NoTarget を予測し(`PredictedNoTargetAt`)、しかも攻撃できる敵が何もいない(world check と同じ判定)なら、早い兄弟に確定する。数えるのは、時計が早い兄弟自身の窓(`DecisionTime − 0.05 s` 以後に始まる最初の長い NoTarget 窓)に入ってからだけ。それより前の窓は兄弟で共通なので、そこで消えても分岐は分からない。
2. **HP で先に決める**: `HpThreshold` が有効で、`DecisionTime − now ≤ BranchMaxExtrapolation(8 s)` のとき、追跡中のボスの HP を直近 `HpSlopeWindow(10 s)` の傾きで `DecisionTime` まで外挿する。予測がしきい値から `BranchForecastMargin(3 pt)` 以上下なら早い兄弟、上なら遅い兄弟に確定する。
   - `DecisionTime` を過ぎたら、HP は遅い兄弟を確かめるのにだけ使う。今の HP がしきい値 + 3 pt 以上なら遅い兄弟に確定する(外挿しないので、傾きが無くても決める)。しきい値より下でも早い兄弟には決めない。早い pull なら分岐点の詠唱か消失が見えるので、そちらで決める。
   - 読むのは `BossOIDs` に載ったボス(複数いれば MaxHP 最大)だけ。いなければ HP では決めない。雑魚や別の敵で代用しない。
   - HP は、しきい値があって `DecisionTime − now ≤ BranchMaxExtrapolation + HpSlopeWindow` の間だけ記録する。傾きは記録を足したときにだけ計算し直す。
3. **未決の間の公開**: `DecisionTime − 0.05 s` 以後に始まる窓は公開しない(分岐前の窓は兄弟で共通なので公開してよい)。0.05 s 引くのは、打ち切られた `DecisionTime`(早い塊の窓の開始の中央値)より、早い兄弟の窓の開始がわずかに前になりうるから。

確定したら `_sequence` を兄弟に差し替える(時計・確認状態は維持)。分岐状態は pull 開始、ゾーン変更、世代変更でリセットする。

判定は純関数にする:

```
DecideBranch(now, decisionTime, threshold, hpNow, hpSlopePerSec, divergenceCastSeen, maxExtrapolation, margin, castGrace) -> -1 未決 / 0 早い兄弟 / 1 遅い兄弟
```

### 3.4 変えないもの

Horizon 25 s、MinPublishedLoss 8.5 s、SyncLifetime 60 s、逆戻りガード、枯渇ガード、pull 開始判定。分岐の無い timeline の挙動はビット同一であること。

## 4. 検証

- `tools/timeline_regression` に純関数の Check(分岐検出、分岐点、しきい値学習、DecideBranch の真理表)と、合成リプレイ 2 分岐の抽出 Check。
- `tools/external_timeline_regression` に配線 Check(兄弟 2 本の timeline で、HP を下げると早い兄弟の窓が出る。分岐点の詠唱が来なければ遅い兄弟の窓が出る)。
- ハーネス `--holdout --sync all` を `%TEMP%\tl_alliance`(San d'Oria 13 + Windurst 10)で。Ultima 0/13 → 12/13 以上、誤予告は現状(6)以下、他ボスは不変。
- アライアンスの自データ内: 新しい抽出器で自リプレイ(`%APPDATA%\XIVLauncher\pluginConfigs\BossModReborn\replays` の Jeuno・San d'Oria・Windurst、duty ごと)から作り直した timeline を `--timelines <その出力> --sync all` で `%TEMP%\tl_alliance` に当てる。Ultima 以外の行は、ユーザーフォルダの timeline(分岐なし)で測ったときと同じであること。抽出ツールが出す分岐の行は Ultima の 1 行だけであること。
- 既存条件は不変: 旧 47 本の ranked / holdout / gate、FFLogs 条件(`--timelines %TEMP%\tl_fflogs --source FFLogs --sync all`、`%TEMP%\tl_eval`)、自リプレイ条件(`--source Replay --timelines <ユーザーフォルダ> --sync all`、`%TEMP%\tl_eval`)。分岐が立った zone があれば個別に確認する。

## 5. 改訂(最終レビュー)

2026-09-29 の最終レビューを受けて、次の 3 点を §3 に反映した。

1. `DecisionTime` を過ぎたら、HP は遅い兄弟を確かめるのにだけ使う(§3.3 の 2)。早い兄弟には、分岐点の詠唱、消失、整合でしか決めない。真理値表の「分岐点の後に HP 46 → 早い」の行は「未決」に改めた。
2. 分岐点の候補は元の pull でも確かめる(§3.2、`AcceptDivergence`)。早い pull の 80% 以上で ±2 s 以内に現れ、遅い pull のどれにも「早い窓の開始 + 6 s」までに現れないこと。満たさなければ次の候補へ進み、無ければ打ち切る。フォロワーでは、決まった後に早い兄弟へ変えられるのは分岐点の同期点への整合だけにした(§3.3 の 1)。
3. 細かい点:
   - 分岐の検出で、塊の間の隙間が広い方の塊の幅の 3 倍以上であることを求める(§3.2)。
   - 両方の抽出ツール(`replay_timeline_extract`、`fflogs_timeline_extract`)は、見つけた分岐ごとに 1 行 `branch group=… boss=… decision=… threshold=… early=… late=…` を出す。
   - フォロワーが分岐点の同期点を覚えておくとき、早い兄弟の `DecisionTime` を使う。
   - 文書を直した(この仕様の §3.2・§4、`TimelineBranch` のコメント、結果文書)。

Ultima の出力(`DecisionTime` 94.6 s、しきい値 46.4)は変わらない。
