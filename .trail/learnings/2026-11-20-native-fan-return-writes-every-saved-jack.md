---
title: "Native fan return writes every saved destination, Custom included"
author: Jack
date: 2026-11-20
branch: feat/native-thermal-presets
touches:
  - docs/windows-native-thermal-mutation.md
  - rust/crates/openblade-domain/src/fan_history.rs
  - rust/crates/openblade-domain/src/fan_recipe.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - rust/crates/openblade-domain/src/thermal_mutation.rs
  - src/OpenBlade.Deployment/WindowsNativeFanHistory.cs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/fan.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
---

## Decided
- The native fan return to Automatic on RZ09-0581 writes every saved native destination directly, as managed `ApplyDirectAutomaticFanTransitionOutcomeAsync` does: Balanced, Performance, Silent, Battery Saver or Battery Balanced as one 0D/02 pair, or Custom as its selector-4 pair plus the 0D/07 CPU and GPU setters. It reuses the performance writer's `Setters`, so the bytes cannot drift. `thermal_mutation::applies_on_fan_handback` and the resubmit deferral text are gone; every manual-fan deferral now promises the apply.
- The fan journal grammar takes selectors 3, 5 and 6 (Automatic rows only, battery never on source 3) and 4 with an optional `custom: [cpu, gpu]` member. That member is absent for every other row so old digests hold, and an explicit null is refused by both readers (`fan_history::validate_raw`, managed `WindowsNativeFanHistory.Custom`). A Custom row has 4 setters and one more reserved pair for the 0D/87 level reads. The vector is `tests/data/fan-history-v3-destination-return.json`.

## Tried and ruled out
- Carrying a battery destination on unclassified power. `override_source_eligible` and the profile key lookup never resolve one on source 3, so `Plan::select_toward` returning None there is a defensive branch only.

## Watch out
- In Git Bash here, `sed -i` rewrites a CRLF file as LF. Edit docs and C# with node: split on CRLF, replace, rejoin.
- A fan return stopped by a battery change mid-Custom leaves an acknowledged row open. Stop then fails with "Fan submission remains unresolved at Stop." and writes nothing; tests must drain and expect an error rather than call `Rig::stop`.
