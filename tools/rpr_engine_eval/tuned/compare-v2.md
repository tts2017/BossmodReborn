## all scenarios (engine falls back to the built-in policy outside its coverage)

scenarios: 746; engine decided 57951 GCD windows, fell back on 53089

| metric | built-in (RPR.cs port) | engine |
|---|---:|---:|
| mean potency/s (DPS proxy) | 271.5 | 279.5 |
| mean harness score | 104369.4 | 105675.7 |
| scenarios with any hard fail | 0 | 24 |
| fail: AoeFailure | 0 | 1 |
| fail: BurstFailure | 0 | 10 |
| fail: GaugeFailure | 0 | 11 |
| fail: OpenerFailure | 0 | 2 |
| policy time per GCD window, mean (us) | 2.6 | 382.5 |
| policy time per GCD window, p99 (us) | 6.9 | 3668.9 |
| heap allocated per GCD window (bytes) | 448.1 | 317.4 |

## covered: level 100, normal rotation, full mode (engine decides every window it can)

scenarios: 369; engine decided 57817 GCD windows, fell back on 551

| metric | built-in (RPR.cs port) | engine |
|---|---:|---:|
| mean potency/s (DPS proxy) | 377.3 | 393.5 |
| mean harness score | 144131.3 | 146773.3 |
| scenarios with any hard fail | 0 | 24 |
| fail: AoeFailure | 0 | 1 |
| fail: BurstFailure | 0 | 10 |
| fail: GaugeFailure | 0 | 11 |
| fail: OpenerFailure | 0 | 2 |
| policy time per GCD window, mean (us) | 2.4 | 723.4 |
| policy time per GCD window, p99 (us) | 7.0 | 4638.8 |
| heap allocated per GCD window (bytes) | 445.5 | 161.0 |

