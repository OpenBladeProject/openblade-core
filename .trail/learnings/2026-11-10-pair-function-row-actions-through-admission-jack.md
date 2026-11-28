---
title: Pair function row actions through admission changes
author: Jack
date: 2026-11-10
branch: feat/rust-function-row-policy
touches:
  - rust/Check-Workspace.ps1
  - rust/README.md
  - rust/crates/openblade-domain/src/lib.rs
  - rust/crates/openblade-domain/src/function_row.rs
  - rust/crates/openblade-domain/tests/function_row_policy.rs
---

## Decided
The standalone Rust function-row policy pairs each physical press through release. The Windows hook and the driver subscription keep separate state. Only the exact RZ09-0484 driver path consumes host-managed Fn stroke admission, independent of its informational readiness; the Blade 16 paths require confirmed firmware Driver mode.

## Tried and ruled out
Do not treat Fn status text or a previous confirmation as admission. Do not forward a retired identity's strokes or replay an intercepted original after a possibly submitted forward. Keep the native transport and execution outside this policy.

## Watch out
Drain held driver keys while the exact subscription is still valid. On retirement, publish media releases and reset the action sink without fabricating native F-key releases. The processor's clean retirement is one input to the host's independent ownership recovery.
