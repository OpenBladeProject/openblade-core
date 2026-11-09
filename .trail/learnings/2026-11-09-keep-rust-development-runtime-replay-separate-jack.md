---
title: Keep Rust development runtime replay separate from passive status
author: Jack
date: 2026-11-09
branch: feat/rust-runtime-replay
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/Cargo.lock
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/main.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/windows/scm.rs
  - tools/openblade-windows-host/src/wire.rs
  - tools/openblade-windows-host/src/wire/tests.rs
  - tools/openblade-windows-host/src/runtime.rs
---

## Decided
The Phase 4c foreground replay drives one ordered controller through harness input. A v79 subscriber captures and marks its initial revision before the first frame is written. Later publications coalesce to the latest snapshot. v51 keeps its passive projection.
## Tried and ruled out
A fixed pair of snapshots proves frame compatibility but cannot prove that the managed client observes runtime effects or reconnects to current state.
## Watch out
A missing recovery read leaves the intent pending. A different confirmed value updates presentation and leaves the requested intent as a tombstone. Full safety observation, rather than journal settlement alone, can lift the presentation block.
