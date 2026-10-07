## all scenarios (engine falls back to the built-in policy outside its coverage)

scenarios: 746; engine decided 55220 GCD windows, fell back on 53082

| metric | built-in (RPR.cs port) | engine |
|---|---:|---:|
| mean potency/s (DPS proxy) | 271.5 | 263.7 |
| mean harness score | 104369.4 | 99563.5 |
| scenarios with any hard fail | 0 | 332 |
| fail: AoeFailure | 0 | 6 |
| fail: BurstFailure | 0 | 102 |
| fail: DeathsDesignFailure | 0 | 160 |
| fail: drift_full_mode_gluttony_interval | 0 | 160 |
| fail: GaugeFailure | 0 | 293 |
| fail: IllegalAction | 0 | 28 |
| fail: OpenerFailure | 0 | 2 |
| fail: PotionFailure | 0 | 1 |
| fail: ReaverSequenceFailure | 0 | 2 |
| fail: WeaveOrderFailure | 0 | 14 |
| policy time per GCD window, mean (us) | 2.4 | 408.2 |
| policy time per GCD window, p99 (us) | 6.1 | 1689.4 |
| heap allocated per GCD window (bytes) | 452.3 | 326.5 |

## covered: level 100, normal rotation, full mode (engine decides every window it can)

scenarios: 369; engine decided 55093 GCD windows, fell back on 544

| metric | built-in (RPR.cs port) | engine |
|---|---:|---:|
| mean potency/s (DPS proxy) | 377.3 | 361.6 |
| mean harness score | 144131.3 | 134449.7 |
| scenarios with any hard fail | 0 | 331 |
| fail: AoeFailure | 0 | 6 |
| fail: BurstFailure | 0 | 102 |
| fail: DeathsDesignFailure | 0 | 159 |
| fail: drift_full_mode_gluttony_interval | 0 | 160 |
| fail: GaugeFailure | 0 | 293 |
| fail: IllegalAction | 0 | 28 |
| fail: OpenerFailure | 0 | 2 |
| fail: PotionFailure | 0 | 1 |
| fail: ReaverSequenceFailure | 0 | 2 |
| fail: WeaveOrderFailure | 0 | 14 |
| policy time per GCD window, mean (us) | 2.2 | 790.3 |
| policy time per GCD window, p99 (us) | 5.9 | 1752.7 |
| heap allocated per GCD window (bytes) | 452.0 | 170.2 |

