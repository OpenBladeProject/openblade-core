---
title: Batch laptop journal settlement after storage-only recovery
author: Jack
date: 2026-11-09
branch: feat/rust-recovery-replay
touches:
  - docs/rust-boundaries.md
  - rust/crates/openblade-domain/src/recovery.rs
  - rust/crates/openblade-domain/tests/recovery_replay.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.LaptopControlRecovery.cs
  - tests/OpenBlade.Service.Tests/WindowsReplay/v1/service-runtime.json
---

## Decided
Return one proposed batch tied to the loaded journal revision. The recovery pass chooses storage-only cleanup before exposing getters, and the journal write gate separately checks pending work and exact identity.

## Tried and ruled out
Per-entry save effects could tempt a caller to commit twice against one revision. A tombstone predicate alone cannot protect pending entries or foreign journals. The base thermal classification does not include runner overrides.

## Watch out
Readback plans must use readings collected in their own pass. Keep actual retry admission, lease consumption, and continuation effects in the future typed command runner.
