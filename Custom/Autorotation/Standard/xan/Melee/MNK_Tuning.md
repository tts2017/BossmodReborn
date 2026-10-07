# MNK Numeric Tuning Inventory

This file is an investigation sheet for numeric gates in `MNK.cs`. It intentionally records current values first; changing these values should be done only after log comparison.

## Priority Bands

| Area | Current values | Assessment |
| --- | --- | --- |
| GCD low/filler | `Meditate=1`, `WindRanged=100`, `FireRanged=200` | Below normal combo, suitable as fallback or downtime/ranged fill. |
| GCD normal | `Basic=300`, `BasicSaver=310`, `BasicSpender=320`, `AOE=400` | Saver/spender gaps are small but deterministic; AOE clearly beats single-target when breakpoint is met. |
| GCD burst/resources | `SSS=500`, `Blitz=600`, `FiresReply=700`, `WindsReply=800`, `PR=900`, `MeditateForce=950`, `BlitzNow=1100` | Emergency/resource actions beat normal GCDs. `BlitzNow` is intentionally above PR for expiration/fight-end recovery. |
| OGCD normal | `TrueNorth=100`, `TFC=150`, `Potion=200`, `RiddleOfWind=300` | These stay below manual/core burst priorities. |
| OGCD core burst | `TFCBrotherhood=2000`, `ManualOGCD=2001`, `RiddleOfFire=2002`, `Brotherhood=2003`, `PerfectBalance=2004`, `TFCOvercap=2005` | MNK deliberately lets drift-sensitive burst actions outrank generic manual OGCD. This should remain explicit. |

## Burst Gates

| Constant | Current | Candidates | Current recommendation |
| --- | ---: | --- | --- |
| `EvenPreRoFPBStartThreshold` | `6.0s` | `5.5 / 6.0 / 6.5 / 7.0` | Keep current for now. It matches the recent rule: Opo GCD after, RoF recast <= 6s. Test 6.5/7.0 only if logs show Even PB starts too late at faster GCD. |
| `BurstImmediateWindowGCDs` | `2.0GCD` | `1.5 / 2.0 / 2.5` | Keep. It keeps RoF/BH from firing a full GCD late without treating distant windows as immediate. |
| `MajorBurstMeleeSafeGCDs` | `5GCD` | `4 / 5 / 6` | Keep. Good middle ground for RoF/potion; 4 is riskier, 6 can over-hold in mechanic-heavy fights. |
| `PBMeleeSafeGCDs` | `3GCD` | `3 / 4` | Keep. PB needs three PB GCDs; 4 is safer but may miss legitimate pre-RoF PB. |
| `RoFBrotherhoodResyncWindow` | `45s` | none fixed | Keep as investigation target; widening can over-align and lose cooldown uses. |

## Synergy Score

| Constant | Current | Assessment |
| --- | ---: | --- |
| `SynergyPlannerCycle` | `120s` | Game/burst cycle derived; do not tune casually. |
| `SynergyPlannerMaxCycles` | `6` | Cheap bounded lookahead. Increase only after profiling. |
| `SynergyResyncScoreGainThreshold` | `0.01` | Tiny tie-break margin; keep unless anchors flap in logs. |
| `SynergyWindowLeeway` | `0.1s` | Candidate: `0.2s` or `AnimationLockDelay`. Keep current until logs show near-miss buff windows. |

Per-GCD synergy weights (`SynergyBurstGCDWeights`) were removed: the planner scores whole windows via `BurstSynergyScore`, and the weighted variant was never called.

## Potion

| Constant | Current | Candidates | Assessment |
| --- | ---: | --- | --- |
| `PotionLateWeaveDelay` | `0.9s` | `0.8 / 0.9 / 1.0` | Keep. Compare ping/lock-sensitive late weaves before changing. |
| `PotionFutureWindowLeeway` | `5s` | `3 / 5 / 7` | Keep. Used only to avoid wasting last potion before a configured future window. |
| `LastPotionWindowLeeway` | `285s` | `270 / 285 / 300` | Keep. Conservative around real potion recast plus planning slack. |

Even-burst potions are only used pre-Brotherhood (`ShouldUseEvenBurstPotion` returns early while Brotherhood is active), so there is no active-BH catch-up; the `PotionEarlyWindow` / `PotionLateWindow` constants that once described it were unreferenced and have been removed.

## Fight End

| Constant | Current | Candidates | Assessment |
| --- | ---: | --- | --- |
| `FightEstimateStableSampleCount` | `3` | `3 / 4 / 5` | Keep. StableHP is already gated by low HP and close end. |
| `FightEstimateStableCloseEndWindow` | `45s` | `30 / 45 / 60` | Keep. 30 can be too late; 60 can burn too early. |
| `FightEstimateStableLowHP` | `0.25` | `0.20 / 0.25 / 0.30` | Keep. Good midpoint for final HP reliability. |
| `FightEstimateDrainSmoothing` | `0.35` | `0.25 / 0.35 / 0.50` | Keep. Existing reset/variance gates are tuned around it. |

## End Burn

| Constant | Current | Candidates | Assessment |
| --- | ---: | --- | --- |
| `LastRoFWindowLeeway` | `70s` | `65 / 70 / 75` | Keep. Covers 60s recast plus drift/slack. |
| `LastBrotherhoodWindowLeeway` | `135s` | `125 / 135 / 145` | Keep. Covers 120s recast plus slack. |
| `LastPotionWindowLeeway` | `285s` | `270 / 285 / 300` | Keep; verify against actual potion cooldown through `ActionDefinitions.IDPotionStr`. |
| `RoWEndBurnLeeway` | `20s` | `15 / 20 / 25` | Keep. Enough to avoid losing Wind Reply without over-burning too early. |

## Riddle of Wind

| Constant | Current | Candidates | Assessment |
| --- | ---: | --- | --- |
| `RoWTwoMinuteHoldWindow` | `45s` | `30 / 45 / 60` | Keep. 30 may miss 90->120 hold; 60 can over-hold if fight-end estimate is absent. |
| `RoWOpenerWindow` | `30s` | `20 / 30` | Keep. Supports opener RoW use. |
| `RoWUptimeRequired` | `15s` | `12 / 15 / 18` | Keep. Prevents use into near downtime while not requiring full buff duration. |

## Melee Safety and Engage

| Constant | Current | Candidates | Assessment |
| --- | ---: | --- | --- |
| `MeleePredictionHorizon` | `20s` | `15 / 20 / 25` | Keep. Long enough for PB/RoF safety without scanning too far. |
| `MeleePredictionSampleStep` | `0.25s` | `0.2 / 0.25 / 0.5` | Keep. Reasonable precision/cost balance. |
| `MovementSafetyBuffer` | `0.35s` | `0.25 / 0.35 / 0.5` | Keep. Avoids exact-frame movement assumptions. |
| `SafeMeleeSamples` | `24` | `16 / 24 / 32` | Keep. 16 can miss narrow safe slices; 32 costs more. |
| `PathSafetySteps` | `4` | `3 / 4 / 5` | Keep. Lightweight path validation. |
| `ThunderclapZeroLandingCountdown` | `0.7s` | `0.6 / 0.7 / 0.8` | Keep; compare opener logs before changing. |
| `ThunderclapRandomOffsetMax` | `0.3s` | `0 / 0.15 / 0.3` | Keep if desync is desired; set lower only for deterministic opener testing. |

## AoE / ST Switching

| Constant | Current | Assessment |
| --- | ---: | --- |
| `ThreeTargetAOEBreakpoint` | `3` | Used for Opo Shadow and conditional Coeurl Rockbreaker. |
| `FullAOEBreakpoint` | `4` | Keeps Raptor/Coeurl conservative unless AoE will last. |
| `ShortAOETransitionGCDs` | `2GCD` | Prevents short 3-target phases from breaking ST prep. |
| `AOELastsSeveralGCDs` | `3GCD` | Allows 3-target Coeurl AoE only when the pull is expected to last. |
| `LineTargetRange / LineAOEHalfWidth` | `10y / 2y` | Matches Enlightenment/Wind's Reply line geometry (4y wide; `TargetInAOERect` takes the half width). |

## Application and Weave Delay

| Constant | Current | Assessment |
| --- | ---: | --- |
| `SixSidedStarApplicationDelay` | `0.62s` | Used for downtime fit. Needs live hit timing evidence before tuning. |
| `DragonKickApplicationDelay` | `1.29s` | Used for opener/engage application timing. |
| `ForbiddenChakraApplicationDelay` | `1.48s` | Reserved application model. |
| `DemolishApplicationDelay` | `1.60s` | Used for opener/engage application timing. |
| `RoFLateWeaveExtraDelay` | `0.8s` | Keeps late RoF weave from losing desired fire window. |
| `TrueNorthLateWeaveDelay` | `0.72s` | Mirrors existing late-weave behavior. |
| `PotionLateWeaveDelay` | `0.9s` | Slightly later than True North; test with lock/ping before changing. |
