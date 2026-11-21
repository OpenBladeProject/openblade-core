---
title: HyperBoost exit is never gated on destination intent
author: Jack
date: 2026-11-21
branch: feat/rz09-0510-hyperboost
touches:
  - CHANGELOG.md
  - docs/experimental-hyperboost-evidence.md
  - docs/zh-CN/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-20-rz09-0510-hyperboost.json
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/HyperBoostCapabilityPolicy.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.HyperBoost.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Tray.Core/MainViewModel.Status.cs
---

## Decided
- A HyperBoost exit command (the 0D/02 HyperBoost step with enabled=false) is never gated on the destination intent. `HyperBoostStepRequiresAdmittedIntent(enabled, purpose)` in ServiceControlRuntime applies the intent check to entry and to bridge/restore steps only, at submission and at the fresh-session retry (the retry now receives the submitted purpose).
- When the destination intent is not in the capability table, the exit still runs, the restore write is skipped, the saved profile is kept, and the result message says the saved mode was not applied. `hyperBoostHandoffPerformance` records the mode the exit landed on, so CompareTransitionAutomaticPerformance does not mark it as an outside change and the plugged-in profile applies again on AC. The owned base is not carried over.
- The HyperBoost entry restriction is checked at every step submission and at the retry, not only on the HyperBoost command, so a stale published status cannot get a bridge write through.
- Service tests for mode-7 battery exits use the real tables (`ExperimentalBladePort.CreateCapabilities(model, true)` plus `ProfileSetValidator.CreateForDeviceCapabilities`) and the default profiles. Fake tables that list BatterySaver hid the bug.

## Tried and ruled out
- Overriding automation intent with the handoff mode (RestoreRecoveredHyperBoostIntentInAutomation) after a skipped restore. It fixed only 02B7, which lists BatteryBalanced; on 029F/029D the battery intent is rejected and the AC return still paused automation as an external change.

## Watch out
- Battery intents are limited to BatterySaver and BatteryBalanced (native `performance_source_eligible`). The default battery profile is BatterySaver, which no mode-7 table lists, and RZ09-0483/0482 list neither, so their desired state shows a battery mode the laptop never reaches.
- The handoff drift exemption matches only HyperBoost off, Automatic fans and the exact recorded mode; any other observation clears it. An outside change to that same mode before the next transition is not detected.
- IPC codec tests built with `JsonSerializer.Serialize` defaults (PascalCase) are rejected before validation and prove nothing. Use `AssertMalformedNestedCapabilityRejectedAsync`; converting the null-collection theory found that a null OneShotLaptopControls threw ArgumentNullException out of `DeviceCapabilities.IsWellFormed`.
- Git Bash `sed -i` strips CRLF from source files; edit with node.
