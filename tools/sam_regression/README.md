# SAM regression emulator

Lightweight Samurai rotation regression emulator for checking whether the current `SAM.cs` policy breaks under common target, downtime, route, level-sync, and strategy combinations.

This tool intentionally does not call or modify the production autorotation module. It mirrors the high-level rotation policy in a deterministic simulation so suspected regressions can be reported before changing `SAM.cs`.

## Commands

```powershell
dotnet build tools/sam_regression/SamRegression.csproj
dotnet run --project tools/sam_regression -- --all
dotnet run --project tools/sam_regression -- --scenario gcd208
dotnet run --project tools/sam_regression -- --all --seed 42 --dump tools/sam_regression/out/latest.json
dotnet run --project tools/sam_regression -- --all --dump tools/sam_regression/out/failures.csv
```

## Output

The console summary reports:

- `Scenarios`
- `Passed`
- `HardFail`
- `SoftFail`
- `CoverageGap`

When a check fails, the tool prints the scenario name, time, current state, suspected production helper area, and the last 20 simulated actions. `--dump` writes the same data as JSON by default, or CSV when the path ends in `.csv`.

## Scenario coverage

The catalog includes representative coverage for:

- 6 / 8 / 10 / 12 minute dummy fights
- Normal and ZeroSecond opener burst settings
- GCD208 and GCD214 routes
- single target, two target, 3+ target AoE, single/AoE/single transitions
- ranged-only target availability, target loss, and target return
- 5 / 8 / 12 / 20 / 60 second downtime windows
- short loss around 1 minute, loss before and during 2 minute burst
- P2-style 197.0 second loss and 206.0 second return
- level sync 50 / 60 / 70 / 80 / 90 / 100
- raid buff profiles and offsets
- no potion and 2 minute potion
- TrueNorth Auto / None
- Higanbana Auto / Delay / Force
- Tsubame Auto / Hold / Delay / Force
- Namikiri Auto / Hold / Delay / Force
- Meikyo Auto / Cooldown / HoldOne / Delay / Force

The seeded matrix adds deterministic mixed scenarios for interaction coverage.
`--all` currently expands to about 20,000 scenarios.
