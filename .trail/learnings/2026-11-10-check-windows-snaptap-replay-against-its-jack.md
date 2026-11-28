---
title: Check Windows SnapTap replay against its exact local parent
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
For the private Windows SnapTap replay, check the slice against its pinned function-row parent. Synthetic observations keep the native path inactive.

## Tried and ruled out
The default comparison included inherited function-row files. The exact-base check still matched phrases in the new state machine, including hook scan codes, input admission, and failed releases. None reintroduces the behavior that the referenced notes ruled out.

## Watch out
Hook records do not establish device identity, and release observations must stay outside the callback. Typed uncertainty and a sink unwind both stop the policy without another send. Native delivery order, typematic takeover, and retirement still need separate validation.
