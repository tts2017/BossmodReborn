## all scenarios (engine falls back to the built-in policy outside its coverage)

scenarios: 746; engine decided 57684 GCD windows, fell back on 53084

| metric | built-in (RPR.cs port) | engine |
|---|---:|---:|
| mean potency/s (DPS proxy) | 271.5 | 276.0 |
| mean harness score | 104369.4 | 104573.7 |
| scenarios with any hard fail | 0 | 178 |
| fail: AoeFailure | 0 | 5 |
| fail: BurstFailure | 0 | 46 |
| fail: DeathsDesignFailure | 0 | 45 |
| fail: drift_full_mode_gluttony_interval | 0 | 48 |
| fail: drift_mode_switch_window | 0 | 1 |
| fail: GaugeFailure | 0 | 86 |
| fail: IllegalAction | 0 | 6 |
| fail: OpenerFailure | 0 | 2 |
| fail: PotionFailure | 0 | 1 |
| fail: WeaveOrderFailure | 0 | 13 |
| policy time per GCD window, mean (us) | 2.7 | 400.0 |
| policy time per GCD window, p99 (us) | 7.7 | 1732.5 |
| heap allocated per GCD window (bytes) | 489.7 | 328.4 |

## covered: level 100, normal rotation, full mode (engine decides every window it can)

scenarios: 369; engine decided 57549 GCD windows, fell back on 546

| metric | built-in (RPR.cs port) | engine |
|---|---:|---:|
| mean potency/s (DPS proxy) | 377.3 | 386.4 |
| mean harness score | 144131.3 | 144544.7 |
| scenarios with any hard fail | 0 | 177 |
| fail: AoeFailure | 0 | 5 |
| fail: BurstFailure | 0 | 46 |
| fail: DeathsDesignFailure | 0 | 45 |
| fail: drift_full_mode_gluttony_interval | 0 | 48 |
| fail: GaugeFailure | 0 | 86 |
| fail: IllegalAction | 0 | 6 |
| fail: OpenerFailure | 0 | 2 |
| fail: PotionFailure | 0 | 1 |
| fail: WeaveOrderFailure | 0 | 13 |
| policy time per GCD window, mean (us) | 2.7 | 758.0 |
| policy time per GCD window, p99 (us) | 8.1 | 1842.7 |
| heap allocated per GCD window (bytes) | 513.2 | 170.2 |

