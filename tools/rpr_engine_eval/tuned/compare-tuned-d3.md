## all scenarios (engine falls back to the built-in policy outside its coverage)

scenarios: 746; engine decided 57827 GCD windows, fell back on 53084

| metric | built-in (RPR.cs port) | engine |
|---|---:|---:|
| mean potency/s (DPS proxy) | 271.5 | 277.2 |
| mean harness score | 104369.4 | 104834.5 |
| scenarios with any hard fail | 0 | 214 |
| fail: AoeFailure | 0 | 5 |
| fail: BurstFailure | 0 | 46 |
| fail: DeathsDesignFailure | 0 | 59 |
| fail: drift_full_mode_gluttony_interval | 0 | 107 |
| fail: GaugeFailure | 0 | 86 |
| fail: IllegalAction | 0 | 6 |
| fail: OpenerFailure | 0 | 2 |
| fail: PotionFailure | 0 | 1 |
| fail: WeaveOrderFailure | 0 | 13 |
| policy time per GCD window, mean (us) | 3.0 | 172.1 |
| policy time per GCD window, p99 (us) | 7.6 | 1294.1 |
| heap allocated per GCD window (bytes) | 496.4 | 326.0 |

## covered: level 100, normal rotation, full mode (engine decides every window it can)

scenarios: 369; engine decided 57692 GCD windows, fell back on 546

| metric | built-in (RPR.cs port) | engine |
|---|---:|---:|
| mean potency/s (DPS proxy) | 377.3 | 388.8 |
| mean harness score | 144131.3 | 145074.4 |
| scenarios with any hard fail | 0 | 213 |
| fail: AoeFailure | 0 | 5 |
| fail: BurstFailure | 0 | 46 |
| fail: DeathsDesignFailure | 0 | 59 |
| fail: drift_full_mode_gluttony_interval | 0 | 107 |
| fail: GaugeFailure | 0 | 85 |
| fail: IllegalAction | 0 | 6 |
| fail: OpenerFailure | 0 | 2 |
| fail: PotionFailure | 0 | 1 |
| fail: WeaveOrderFailure | 0 | 13 |
| policy time per GCD window, mean (us) | 3.2 | 324.5 |
| policy time per GCD window, p99 (us) | 8.2 | 1502.5 |
| heap allocated per GCD window (bytes) | 523.7 | 164.8 |

