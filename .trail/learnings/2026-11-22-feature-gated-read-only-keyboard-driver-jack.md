---
title: "Feature-gated read-only keyboard Driver mode (6a-1/6a-2, corrected)"
author: Jack
date: 2026-11-22
branch: feat/native-driver-mode-read
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-native-agent-function-row.md
  - docs/windows-rust-development-service.md
  - rust/crates/openblade-protocol/src/device_mode.rs
  - rust/crates/openblade-protocol/src/device_mode/tests.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/build.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance.rs
---

## Decided
- Supersedes `2026-11-22-native-read-only-keyboard-driver-mode-jack.md` (left unedited). Three of its claims are wrong: native publications can be `deviceVerified` (mutation lanes publish a verified feed with a null mode); `attempt.completed = true` and dropping the post-decode checkpoint are not equivalent mutants; and the read now needs its own feature. Its 15 s cadence, admission binding and vector notes still hold.
- Stage 6a-1/6a-2: the native Service reads the RZ09-0581 keyboard device mode with the reviewed `00/84:0000` query only, and only when built with `service-keyboard-driver-mode-read-v1` (implies `service-mailbox-observation-v1`). build.rs adds the capability marker of the same name, and both package readers (`native_package.rs`, `WindowsNativePackage.cs`) refuse the feature and the marker by name. Without the feature no query is sent and no `keyboardDriverModeActive` member is published (`driver_mode_absent_tests.rs`).
- The `00/04` setters live only in `openblade-protocol/src/device_mode/tests.rs`. `RazerReport::create` is public, so that is no type guarantee; `performance/device_mode_guard_tests.rs` fails when non-test source names the setter or adds a `RazerReport::create` call site (an allowlist with counts).
- No new loop. The query rides the mailbox read job once the cached result is 15 s old. `Cadence` binds each result to its `ReadAdmission`, and one job instant decides `due`, `record` and `active`.
- The query has its own 1 s budget and never takes the job's last 500 ms, so a stalled query keeps the thermal read. A sent query with no confirmed reply, and an asked job that returned nothing, are cached as null and not sent again until the interval ends; a job without time left sends nothing and stays due.
- `Reader::job` is the one job body (reads, retirement, fresh admission check on the job clock) for the native worker and the fixture workers, so fixture tests see production deadline drops and the `job.device_mode` decision.
- Published value: true Driver, false Normal, null for any failed, missing or other-mode reply and on an unverified status. `with_mailbox_read` clears it, so a mutation's own read publishes null; `rebase_thermal_mutation` copies it only with a fresh read.

## Tried and ruled out
- A third `with_mailbox_read` parameter: it breaks the two mutation callers owned by the profile layer.
- Comparing `deviceVerified` with managed after an Unavailable step: managed keeps its last verified presentation (AGENTS.md rule 4). Compare the mode only.
- Calling `attempt.completed = true` or dropping the post-decode checkpoint "equivalent": Reader-level tests (`performance/device_mode_tests.rs`) show the unsealed Reader sends more and a lost identity after decode must stay unconfirmed.

## Watch out
- In the read-only composition the feed is unverified; in mutation compositions a feed status verified after a mutation carries null. Only the read-job status carries a mode, so the native Agent still passes function-row keys through.
- The queries alone exhaust the 127-pair transaction budget after about 31 minutes of observed uptime.
- The observation-only lane carries older unused-code warnings, so CI lints the read beside `service-profiles-v1`.
- Mutation harnesses: restoring a file with `mv file.orig` brings back the older mtime, so cargo reuses the mutant build. Copy back and `touch`, and rebuild from a fresh cfg before reporting counts.
- The managed vectors' envelope-failure rows compare Rust with itself: managed `RazerReport.ValidateResponse` calls the Rust codec.
- The one-instant test uses a wall-clock `hold` in the keyboard fixture: the ReadSource rig clock is real, so only a job that really outlasts the interval margin separates the job instant from a later `Instant::now()`.
