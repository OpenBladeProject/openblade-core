---
title: Native Silent and battery thermal presets on RZ09-0581
author: Jack
date: 2026-11-20
branch: feat/native-thermal-presets
touches:
  - docs/windows-native-thermal-mutation.md
  - rust/crates/openblade-domain/src/thermal_history.rs
  - rust/crates/openblade-domain/src/thermal_mutation.rs
  - rust/crates/openblade-protocol/src/thermal_preset.rs
  - rust/crates/openblade-types/src/lib.rs
  - src/OpenBlade.Deployment/WindowsNativeHandback.cs
  - src/OpenBlade.Deployment/WindowsNativeThermalHistory.cs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
---

## Decided
- Native setPerformanceOverride now admits Silent (selector 5), Battery Saver (3) and Battery Balanced (6) on exact RZ09-0581, one 0D/02 pair each, straight from the managed BatteryProduction fixture. Custom (5) is still refused at decode: it needs 0D/07 levels, saved customPerformance and a 0D/87 final read.
- Power admission is per destination (`thermal_mutation::admits_class`), after managed AdmitOrdinaryThermalCommandAsync. Ordinary presets need external power with Windows AC. Battery presets are also admitted on battery with Windows battery, and on classified adapters, but never on unclassified power. `PowerFence::bind_destination` applies the rule only to performance permits; fan permits keep the old `current != 1` rule.
- HyperBoost to a battery preset is `Plan::NoDirectPath`. Managed goes through the hyperboost-restore-balanced pair from the separate HyperBoost fixture, which is not admitted natively, so native stores the intent and writes nothing.
- The fan return to Automatic still writes only Balanced or Performance (`applies_on_fan_handback`), because the fan journal grammar admits selectors 0 and 2 only. A manual-fan deferral to any other destination uses the resubmit text, never the "will be applied when fans return" promise.
- Battery presets on unclassified power are rejected before storing (status 3), matching managed ValidateUnclassifiedPerformanceOverride.

## Tried and ruled out
- Asserting getStatus desiredState for a clear: the coarse metadata always reports the default slot there. That is by design, not a regression.
- Expecting a battery-preset override on an adapter to show as desiredState. Status metadata reports every external class as 3, where battery presets are not eligible.

## Watch out
- docs/windows-native-thermal-mutation.md is CRLF in the C:\tmp worktree (autocrlf) but LF in the index. Node edits must normalise to LF and write CRLF back.
- In the test rig, Windows battery is derived from `actual == 0`, but the status metadata stays external unless `rig.metadata.store(1)`. Battery projection tests need both.
- Automatic Custom with fixture levels High/High does not decode, so a rig starting at (4,0) needs other custom_levels.
