---
title: "Native capability record: battery gap and GPU tripwire follow-up"
author: Jack
date: 2026-11-28
branch: feat/native-capability-record
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-rust-development-service.md
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/service_status/capability_record.rs
  - tools/openblade-windows-host/src/service_status/capability_tests.rs
  - tools/openblade-windows-host/tests/data/capability-record-vectors.json
---

## Decided

- Corrects the battery note in "Native capability record: admit where the request completes". The battery gap is narrower than that note says. On battery, managed's `PerformancePowerSourcePolicy` admits only BatterySaver and BatteryBalanced. Native's default desired preset on battery is BatterySaver. The record ranges neither, so the tray already refuses manual fans there. The only remaining case is a desired Balanced or Performance preset while on battery, and no path that produces one was found.
- The GPU overclock tripwire now covers each blocker where it would change:
  - `capability_record::serialize_confirmed_state` (the `serialize_with` on `Snapshot.confirmed_state`) has a debug assertion. A published non-null `confirmedState.gpuClockOffsets` fails while the record keeps `gpuOverclock` absent, whichever projection adds it, including in-place edits like `with_lighting_reading`.
  - `capability_tests` asserts that `setGpuClockTuning` is unserved.
  - CI runs the capability filter in the GPU lane.

## Tried and ruled out

- Asserting on the `with_mailbox_read` snapshot. Offsets would come from another projection, so that check could never fire.
- Hooking `Snapshot::confirm`. Some projections edit `confirmed_state` in place and never call it.

## Watch out

- Under Silent, Custom or a battery preset the dashboard still lists Fixed and Maximum, and choosing one reverts silently (`MainWindow.xaml.cs` restores the selection). This stays latent while native publishes `writesBlocked: true`.
- The vector rows are fragment merges, not compositions, but a row should still be one a build can compile. HyperBoost always comes with profiles, so the thermal-plus-HyperBoost row was dropped.
