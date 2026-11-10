---
title: Keep portable recovery evidence separate from durable effects
author: Jack
date: 2026-11-09
branch: feat/rust-recovery-replay
touches:
  - ".github/workflows/rust.yml"
  - docs/rust-boundaries.md
  - rust/Check-Workspace.ps1
  - rust/crates/openblade-domain/src/lib.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.LaptopControlRecovery.cs
  - tests/OpenBlade.Service.Tests/WindowsReplay/v1/service-runtime.json
  - tests/OpenBlade.Service.Tests/WindowsReplayRuntimeTests.cs
  - rust/crates/openblade-domain/src/recovery.rs
  - rust/crates/openblade-domain/tests/recovery_replay.rs
---

## Decided
Keep laptop-control restart and live completion decisions in the portable domain. A mismatch keeps a tombstone. A write-only acknowledgement can settle only in the live attempt. The host must compare exact persisted bytes at save and keep the original requested and confirmed values separate.

## Tried and ruled out
Do not treat a missing getter, cached baseline, or process restart as proof of application. Do not reuse process-local non-submission proof against a replaced journal. Scoped thermal trace classification does not own retries or specialty durable ledgers.

## Watch out
The thermal trace retains indeterminate submission chronology even after a fresh exact readback. The Windows effect runner still owns journal aggregation, durable writes, session and generation admission, and typed command execution.
