---
title: Track suppressed function-row repeats from callback edges
author: Jack
date: 2026-11-10
branch: feat/rust-phase-6j-function-row-hook
touches:
  - rust/Check-Workspace.ps1
  - rust/README.md
  - rust/Test-WorkspaceCheck.ps1
  - rust/crates/openblade-platform-windows/Cargo.toml
  - rust/crates/openblade-platform-windows/src/function_row_hook.rs
  - rust/crates/openblade-platform-windows/src/function_row_hook/native.rs
  - rust/crates/openblade-platform-windows/src/function_row_hook/tests.rs
---

## Decided
Track native repeat pairing from accepted callback edges. Start each typed key in the unknown prior-state condition and pass its first observed press through until its up. Store expiry and Windows-only admission in one atomic word. A stop signal wakes the installing thread, and handoff becomes visible only after native retirement, release of the ownership slot, and terminal action delivery.

## Tried and ruled out
Using GetAsyncKeyState for the old key state can turn a suppressed autorepeat into a new press. Windows updates asynchronous key state after low-level hooks accept input. An unsent action and a passed-through held key alone do not imply uncertain ownership.

## Watch out
Keep release loss, possibly submitted output, and failed unhook sticky. Continue to require the existing host guard and independent hook-health evidence before activating the adapter. Canonical F1-F12 packets have desktop scope. Keep the explicit Windows-only domain edge and exact SDK feature admission in Check-Workspace.ps1.
