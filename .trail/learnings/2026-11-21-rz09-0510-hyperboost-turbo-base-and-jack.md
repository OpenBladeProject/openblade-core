---
title: "RZ09-0510 HyperBoost, Turbo base and the SKU recheck"
author: Jack
date: 2026-11-21
branch: feat/rz09-0510-hyperboost
touches:
  - Directory.Build.props
  - docs/experimental-blade-support.md
  - docs/experimental-hyperboost-evidence.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/source-reviewed-blade-models.md
  - docs/zh-CN/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-07-models.json
  - evidence/source-reviewed-blades/2026-11-20-rz09-0510-hyperboost.json
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/HyperBoostCapabilityPolicy.cs
---

## Decided
- RZ09-0510 (02B7, product 695) HyperBoost uses the shared mode-7 pair with MCU minimum 1.3.0.0. Turbo (raw 1) is its pad-present entry base because 695 has no Performance (raw 2) mode and defaultACPerformanceMode is 1. IPC protocol 86 adds HyperBoostBaseModeCapabilities.Turbo; 85 is reserved by PR #984.
- 02B7 reports raw 6 as BatteryBalanced, not Balanced. HyperBoostCapabilityPolicy.IsRestoredBase accepts BatteryBalanced for a Balanced exit request only on battery. The port, the service exit check and WriteConfirmationPolicy all use it; the pending confirmation carries its power source.
- The Synapse SKU recheck (systemSKU.charAt(9) in R, S, 5, 6) is one capability gate in HyperBoostSystemSkuPolicy for 029F and 02B7. It reads the admitted host's SKU and keeps HyperBoost ObservedOnly. A missing or short SKU also fails closed, which is stricter than Synapse. 029D stays ungated: 669's policy has a 5/6 predicate, but its main never sets needHyperboostRecheck.
- The 0510 Maximum Fans and CPU Boost admissions stay. The 695 UI renders both directly, and the absent hasMaxFanSpeedModeEnabled/hasCpuBoostEnabled flags only gate clear/reapply, a startup restore and a battery toggle.

## Tried and ruled out
- Reusing SupportsExperimentalHyperBoost for the raw-6 battery Balanced decode. 02B7 has its own DC Balanced preset, so that decode now uses UsesRaw6AsBatteryBalanced (029F/029D only).
- A service battery-exit test that checks only the end state. The failed-transition restore reaches the same end state, so the test must also assert that no "did not match" message appears.

## Watch out
- 02B7 can report HyperBoost with manual fans, and mode 7 has no admitted dynamic fan range. The decode records FanTargets NotQueried instead of rejecting the whole state.
- Every factory open on 029F/02B7 records one HyperBoostSystemSku Query observation. Tests that assert an empty open-time history must exclude it.
- The ExperimentalBladePort tests must pass systemSku, or 029F HyperBoost is ObservedOnly.
