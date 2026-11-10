---
title: Keep the Windows SnapTap callback synchronous through retirement
author: Jack
date: 2026-11-10
branch: feat/rust-windows-snaptap-adapter
touches:
  - rust/crates/openblade-platform-windows/Cargo.toml
  - rust/crates/openblade-platform-windows/src/snap_tap.rs
  - rust/crates/openblade-platform-windows/src/snap_tap/adapter.rs
  - rust/crates/openblade-platform-windows/src/snap_tap/adapter/native.rs
  - rust/crates/openblade-platform-windows/src/snap_tap/adapter/tests.rs
  - rust/docs/windows-snap-tap-parity.md
---

## Decided
Keep the Rust Win32 SnapTap adapter private until an isolated supervisor owns selection, current registry/controller evidence, status, and a boot-bound journal. Use the policy's exact typed counts and separate hook/timer retirement results.

## Tried and ruled out
Function-row queue effects cannot replace a synchronous A/D handoff. A pump error poisons the state before retirement, as in the managed helper. Releasing a winner after its physical up and new down forwarded during cancellation would release an unowned press.

## Watch out
Fake tests exercise callback ordering, timer selection, cancellation, disconnect, and ambiguous submissions. Build-only Win32 evidence leaves nested native delivery, downstream input ordering, typematic behavior, and durable recovery for supervised activation work.
