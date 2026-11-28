---
title: Native protocol 85 reconciliation with main
author: Jack
date: 2026-11-20
branch: feat/native-reconcile-main-ipc
touches:
  - tools/openblade-windows-host/src/wire.rs
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/session_native/display_query.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/tests/fixtures/native-thermal-edge-fixtures.json
  - build/native_windows_fixtures.py
---

## Decided
- The combined Service protocol is 85: its JSON shape equals main's 84, but the branch's confirmed-Custom-with-unreported-levels relaxation is rejected by 84 peers, so it needs its own number. Native wire, reporter pipe and status projection all speak 85.
- The v80 managed `service.json` and `service-runtime.json` are frozen under `tools/openblade-windows-host/tests/data/ipc80/` (eol=lf) beside the IPC79 copies. The dev host must close every v79 and v80 request in them without a reply.
- The native Agent decodes Session Display ExternalUnknown (3) for manual setBrightness/setHdr/setRefreshRate/setColorProfile, as main's `IsManualSource` does, and blocks it at admission. applyProfile still rejects 3 at decode. Native never implements `ManualExternalPowerPresent`.
- `display_power_source()` reports 3 when the Service row's `observationReason` is RetainedPowerSource (3), matching main's DisplayAutomationCoordinator.
- The dev-host keyboard Off path accepts an absent or false `controlsKeyboardEffect` and refuses true.
- The native HID performance writer keeps its Balanced-only rule on a limited USB-C adapter. Main's f26d1cb5 widened the shared `performance_source_eligible` instead, so the installed Service now refuses a USB-C Performance override fail-closed, with zero writes, rather than writing Balanced.
- The native-thermal edge inventory gained `frozenPersisted`: schema-25 store inputs are pinned by hash and no longer compared with fresh managed output, which now writes schema 27.

## Tried and ruled out
- Regenerating the schema-25 persisted fixtures from managed: that yields schema 27/10, which native `CURRENT_SCHEMA=25` and override schema 8 treat as FutureSchema.

## Watch out
- Three Service.Tests remain red because of the persisted-schema gap: ConfigurationInspection, ProfileFixture and ConfigurationAggregate. The rust.yml configuration-writer step fails as well. Fixing them means migrating native configuration, profile and thermal_storage to 27/10, and moving the fan limit from 5400 to 5500.
- `cargo clippy -D warnings` on the agent candidate bins fails on dead code that predates this branch. CI does not run clippy on candidates.
- `Directory.Build.props` now has several PropertyGroups. Under StrictMode, `.Project.PropertyGroup.X` throws PropertyNotFoundStrict, so read values with `SelectSingleNode('/Project/PropertyGroup/X')` instead.
