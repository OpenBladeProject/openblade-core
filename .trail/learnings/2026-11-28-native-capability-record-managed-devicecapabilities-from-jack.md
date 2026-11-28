---
title: "Native capability record: managed DeviceCapabilities from compiled groups"
author: Jack
date: 2026-11-28
branch: feat/native-capability-record
---

## Decided

- Native publishes managed's `DeviceCapabilities` record on every status, built by `service_status/capability_record.rs` from the compiled groups (thermal, fans, battery, profiles, lighting, HyperBoost, GPU clock tuning, Fan Stop) and whether a confirmed state is present. Unidentified is `DeviceCapabilities.Unavailable()`; identified adds temporary full charge, read-only HyperBoost with pad-less observed only, THX `rznb12` and observed local dimming, then each group's fragment.
- `Snapshot::confirm` is the only place that sets it, and `battery_projection` now goes through confirm, so read, retained, unavailable, battery and rebased-mutation projections agree.
- The data is `capability_record.json`, pinned by managed-recorded rows in `tests/data/capability-record-vectors.json` (`ServiceControlRuntimeTests.NativeCapabilityRecordVectors`, regenerate with `OPENBLADE_REGENERATE_NATIVE_CAPABILITY_RECORD_VECTORS`).
- GPU clock tuning stays absent (`gpuOverclock` null) even with `service-gpu-clock-tuning-v1`: the tray commits it only through `setGpuClockTuning`, which nothing serves. A test ties the group to `StatusRequest::serves("setGpuClockTuning")`.
- Keyboard brightness and refresh rates join only with lighting and profiles, matching `StatusRequest::serves`.

## Tried and ruled out

- Asserting every fragment member exists in the unidentified record: the managed codec omits null `localDimming` there. Check against the full-composition row instead.
- Recording the live-reading and firmware-version frames in thermal lanes: the identified record differs per composition, so both recorders run only without thermal (and without the Driver-mode read).

## Watch out

- With a pad connected, native refuses HyperBoost entry while the record admits the pad path. The contract has no pad-less-only writable member.
- After a lost device managed keeps its last record; native returns to Unavailable with the confirmed state.
- Diagnostics export now prints managed's typed summaries for `customPerformance`, `localDimming` and `gpuOverclock`; any other new typed member still refuses the export.
- Standalone `service-live-readings-v1` and `service-diagnostics-export-v1` fail clippy with older dead-code warnings; CI lints them only beside other features.
