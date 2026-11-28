---
title: "Native capability record: admit where the request completes"
author: Jack
date: 2026-11-28
branch: feat/native-capability-record
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-native-thermal-mutation.md
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Contracts.Tests/NativeThermalMutationResponseTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeCapabilityRecordVectors.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeLiveReadingVectors.cs
  - tools/openblade-windows-host/src/diagnostic_export/sections.rs
  - tools/openblade-windows-host/src/diagnostic_export/tests.rs
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/service_status/capability_record.json
---

## Decided

- Corrects the earlier capability record learning. A record member is admitted only where the request it advertises is completed, not where it is dispatched. `StatusRequest::serves` lists dispatched requests, and some of them refuse without their backend.
- The temporary full charge is its own group, gated on `service-temporary-charge-v1`. Without it `setFullChargeOverride` refuses in `battery_mutation`, so it is no longer part of every identified record.
- HyperBoost is writable only with `service-hyperboost-v1` and `service-profile-triggers-v1` together, because `Runtime::native` composes the probes only there. A `const` assertion in `thermal_mutation.rs` ties `Composition::COMPILED.hyper_boost` to that cfg.
- With fans, `fanSpeedRangesByPerformancePreset` lists only Balanced and Performance. Those are the presets `fan_recipe::Plan::select` plans manual fans from. `capability_tests` checks the map against `Plan::select` and the range against `FanIntent::checked`. The diagnostic export now summarizes the map in managed's `Preset:min-max/step` format.
- GPU overclock stays null for the real reasons. The tray does commit tuning through `setPerformanceOverride` with `gpuClockTuning`, which the GPU lane serves. What blocks it is that native publishes no `confirmedState.gpuClockOffsets`, and that the HyperBoost-active GPU-only `setGpuClockTuning` is unserved. The tripwire checks both.
- The producer guard covers every caller of `with_mailbox_read`, `battery_projection` and `with_standard_performance`.

## Tried and ruled out

- Gating HyperBoost on `serves("setHyperBoost")` or on the HyperBoost feature alone. Both are true without triggers, while every request then replies `hyperboost::UNAVAILABLE`.
- Keying the GPU group on `serves("setGpuClockTuning")` alone. The tray mainly commits through `setPerformanceOverride`, so that test could never pass for the right reason.

## Watch out

- The record does not survive "a lost device", as the earlier note said. It expires 5 s after the last completed read (`RETENTION_WINDOW` in `mailbox_read.rs`). Native has no `observationReason`, so the tray cannot retain it, and it flickers when reads are slower than that (live readings Off or at 10 s, or the tray popup alone).
- On battery, managed's `FanControlPowerPolicy` admits Fixed and Maximum regardless of the record, but native's recipe refuses power source 1. The contract cannot express this, so it is documented as a gap.
- The old "Native controls remain unavailable" and "broad device capabilities remain unavailable" messages were pinned only by Rust test constants and the recorded live-reading and firmware frames. Regenerate those frames in the read-only lane after a message change.
