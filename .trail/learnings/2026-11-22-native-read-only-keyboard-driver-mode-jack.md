---
title: Native read-only keyboard Driver mode (stage 6a-1/6a-2)
author: Jack
date: 2026-11-22
branch: feat/native-driver-mode-read
touches:
  - docs/windows-native-agent-function-row.md
  - docs/windows-rust-development-service.md
  - rust/crates/openblade-protocol/src/lib.rs
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - tools/openblade-windows-host/src/discovery/hid_lease/performance.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation_fixture.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/windows.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/worker.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/worker_tests.rs
  - tools/openblade-windows-host/src/service_status.rs
---

## Decided
- Stage 6a-1/6a-2: the native Service reads the RZ09-0581 keyboard device mode with the reviewed `00/84:0000` query only. `openblade-protocol::device_mode` encodes the `00/04` setters under `#[cfg(test)]`, so no production caller can build one; the host Reader has no setter path at all.
- No new loop. The query rides the existing serialized mailbox read job (`Reader::observe_job`) once the cached result is 15 s old (managed `ServicePeriodicObservationLoop.ObservationInterval`). `installed/driver_mode.rs` `Cadence` binds each result to the `ReadAdmission` it was read under; a lifecycle, custody or generation change makes it unknown.
- Published `keyboardDriverModeActive` is the confirmed firmware mode, not ownership: true Driver, false Normal, null on any failed, missing or other-mode reply and on an unverified status. A failed query is cached as null for the interval and never retried in between.
- `Snapshot::with_mailbox_read` keeps its two-argument signature and clears the mode; `with_keyboard_driver_mode` adds it afterwards. Mutation callers (fan_mutation.rs, thermal_mutation.rs) therefore publish null without edits; `rebase_thermal_mutation` copies the mode only with the verified read.
- Managed vectors: `tools/openblade-windows-host/tests/data/keyboard-driver-mode-vectors.json`, recorded by `ServiceControlRuntimeTests.NativeKeyboardDriverModeVectors` (regenerate with `OPENBLADE_REGENERATE_NATIVE_DRIVER_MODE_VECTORS=<dir>`), consumed by the protocol crate and the installed reader rig.

## Tried and ruled out
- A third `with_mailbox_read` parameter: it breaks the two mutation callers owned by the profile layer.
- Comparing `deviceVerified` with managed after an Unavailable step: managed keeps its last verified presentation (AGENTS.md rule 4), native publishes the unavailable read unverified. Compare the mode only.
- Millisecond interval edges in the ReadSource rig: its tokio clock is real, so each exchange ages the cache. Test the exact edge on `Cadence` with explicit instants.

## Watch out
- Native publications are never `deviceVerified`, so the status feed still carries null and the native Agent passes every function-row key through until a later stage publishes verified status.
- Each query costs one pair of the 127-pair process transaction budget (once per 15 s per admission).
- Equivalent mutants: `attempt.completed = true` (retire seals every Reader at job end), dropping the post-decode checkpoint (run() and ReadSource recheck admission before publication), prefix decode (length is already validated).
