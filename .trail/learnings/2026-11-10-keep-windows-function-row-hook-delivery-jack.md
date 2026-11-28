---
title: Keep Windows function-row hook delivery and retirement bounded
author: Jack
date: 2026-11-10
branch: feat/rust-phase-6j-function-row-hook
touches:
  - rust/Cargo.lock
  - rust/crates/openblade-domain/src/function_row.rs
  - rust/crates/openblade-platform-windows/Cargo.toml
  - rust/crates/openblade-platform-windows/src/lib.rs
  - rust/crates/openblade-platform-windows/src/function_row_hook.rs
  - rust/crates/openblade-platform-windows/src/function_row_hook/native.rs
  - rust/crates/openblade-platform-windows/src/function_row_hook/tests.rs
---

## Decided
Phase 6j adds a current-user WH_KEYBOARD_LL adapter only for the existing exact-host Blade 16 WindowsHook selection. Read one expiring admission snapshot per typed edge. Queue a stale Up and replacement Down as one bounded batch, and retire the hook before authorizing handoff. Keep lost releases and possibly submitted outputs sticky through retirement.

## Tried and ruled out
An eligible hook packet has VK and scan code, but no keyboard identity. Host admission and a matching F1-F12 packet do not establish per-device suppression. RZ09-0484, driver transport, real hook registration and input injection are outside the fixture.

## Watch out
The managed high-bit check is a prior-held hint. Verify its behavior for suppressed repeats, desktop transitions and initially held keys before activation. The agent must wire its ownership guard, same-thread message lifecycle, typed action consumer, and independent Raw Input health evidence before live handoff.
