---
title: Windows SnapTap replay keeps its synchronous handoff
author: Jack
date: 2026-11-10
branch: feat/rust-windows-snaptap-policy
touches:
  - rust/crates/openblade-platform-windows/src/lib.rs
  - rust/crates/openblade-platform-windows/src/snap_tap.rs
  - rust/crates/openblade-platform-windows/src/snap_tap/tests.rs
  - rust/docs/windows-snap-tap-parity.md
  - tests/Fixtures/Parity/SnapTap/windows-hook.txt
  - tests/OpenBlade.SessionAgent.Tests/OpenBlade.SessionAgent.Tests.csproj
  - tests/OpenBlade.SessionAgent.Tests/SnapTapWindowsRustParityTests.cs
---

## Decided
Replay the fixed Windows SnapTap A/D policy against the pinned managed InputState, using a synchronous typed sink and synthetic observations only.

## Tried and ruled out
The function-row effect queue cannot establish release-before-forward ordering for the physical down. Reuse its test approach rather than its queued dispatch.

## Watch out
A failed winner handoff blocks the new physical down; a failed restoration passes the physical up. Either leaves uncertain output with no replay or guessed cleanup. Clean policy stop is separate from real hook retirement and journal cleanup.
