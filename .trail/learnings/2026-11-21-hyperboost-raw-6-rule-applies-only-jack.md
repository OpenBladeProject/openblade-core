---
title: hyperboost raw-6 rule applies only on a transition from battery
author: Jack
date: 2026-11-21
branch: feat/rz09-0510-hyperboost
touches:
  - CHANGELOG.md
  - docs/experimental-hyperboost-evidence.md
  - evidence/source-reviewed-blades/2026-11-20-rz09-0510-hyperboost.json
  - src/OpenBlade.Protocol/ExperimentalBladePort.Custom.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.HyperBoostHandoffScope.cs
---

## Decided
- The mode-7 transitional raw-6 rule (029F/029D read DC Balanced raw 6 as BatteryBalanced off battery) applies only on a transition from battery: `CompareTransitionAutomaticPerformance` takes the transition's previous `ActivePowerSource` (passed from `current.ActivePowerSource` at the single transition caller of `ObserveAutomaticState`) and requires Battery.
- `ReadsRawSixAsTransitionalDcBalanced` requires `HyperBoostAccess == ReadWrite`. RZ09-0484 (02A0) has the same table shape (Balanced listed, BatteryBalanced not) but HyperBoost is ObservedOnly, so it never makes the handoff.
- Custom from a DC Balanced baseline stays scoped to 029F/029D/02B7, the models whose HyperBoost battery exit leaves raw 6. Other BatteryBalanced models keep the refusal until reviewed.

## Tried and ruled out
- Applying the rule at every automatic transition that preserves outside changes: a PluggedIn to UsbC swap then cleared an outside raw-6 latch on AC and wrote the profile, even with RazerAppEngine present.
- Treating the X1 `!pendingWriteConfirmations.HasPending` retirement guard as untestable: setting a Lighting confirmation on the tracker (reflection) with `Lighting = null` in the confirmed state keeps it pending through an observation.

## Watch out
- A service start on AC with raw 6 held is not affected: the startup reconciliation applies the plugged-in profile.
- Ordinary transition writes do not check competing-controller absence; do not claim they do.
- Service tests build here with `-p:OpenBladeNativeDevelopmentBuild=false -p:OpenBladeVersion=0.66.0`; rustc is not on PATH for the Bash tool.
