---
title: Native lighting is stricter than managed in four refusals
author: Jack
date: 2026-11-26
branch: feat/native-lighting
touches:
  - docs/windows-native-lighting.md
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/src/diagnostic_export/tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/lighting.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/lighting_tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/native_package.rs
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/lighting_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/live_readings_tests.rs
---

Corrects two earlier lighting learnings (L1 gates; L1 review fixes) that call native's lighting admission managed's own.

## Decided
- Native lighting refuses four requests before the store that managed stores, and the docs list them under "Stricter than managed": a keyboard delegated to Windows Dynamic Lighting (managed stores, then replies with the committed prefix), a competing controller (managed's refresh only observes and relinquishes), an unclassified adapter with HyperBoost engaged or unobserved (managed's Lighting arm in CanAdmitUnclassifiedExternalControl, UnclassifiedExternalPower.cs 60-77, has no HyperBoostActive clause), and a temporary override in low power off Windows battery (managed stores it with no power class).
- `Storage::pending` is the store quarantine or an unsettled last row of the one native journal (thermal, fan and battery rows); it is not managed's WritesBlocked flag.
- Status publishes the Off record only from a double-sampled zero; a setter readback is one sample and confirms nothing.

## Watch out
- Battery requests do write rows into the shared journal (`battery_storage.rs` `record_battery`), even though `BatteryPermit::record` refuses thermal progress.
- The diagnostic export reads the status Value by member name only; an entry that ever writes a status record whole must go through `kind_first`.
