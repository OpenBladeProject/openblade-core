---
title: Require a child retirement receipt for Windows SnapTap handoff
author: Jack
date: 2026-11-10
branch: feat/rust-windows-snaptap-supervisor
touches:
  - rust/crates/openblade-platform-windows/src/snap_tap/adapter/tests.rs
  - rust/crates/openblade-platform-windows/src/snap_tap/adapter/tests/supervisor.rs
  - rust/docs/windows-snap-tap-parity.md
---

## Decided
The Windows A/D supervisor fixture uses a test-only child of the same executable. Require explicit Rust selection and repeat the injected admission check before opening the start gate. Bind the terminal receipt to the retained child and its private pipe.

## Tried and ruled out
A successful process exit by itself cannot establish handoff. A no-write gate rejection is separate from verified retirement. Missing evidence, ambiguous output, teardown error, and timeout keep the recovery barrier set.

## Watch out
Publish while the child is still owned. Publication errors keep the barrier set. The private fixture has no runtime activation path. A durable Windows journal and independent real host observations belong to a later slice.
