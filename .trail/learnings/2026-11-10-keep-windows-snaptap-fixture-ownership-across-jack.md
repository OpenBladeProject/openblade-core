---
title: Keep Windows SnapTap fixture ownership across supervisor restart
author: Jack
date: 2026-11-10
branch: feat/rust-snaptap-restart-boundary
touches:
  - rust/crates/openblade-platform-windows/src/snap_tap/adapter/tests/supervisor.rs
  - rust/docs/windows-snap-tap-parity.md
  - rust/crates/openblade-platform-windows/src/snap_tap/adapter/tests/supervisor/journal.rs
---

## Decided
Keep the inert Rust Windows A/D generation claim in exclusive scratch storage. Flush reservation and exact helper binding before opening the start gate. Complete only after the matching private receipt, independent successful exit and successful publication. Keep completed generations to reject reentry.

## Tried and ruled out
A fresh supervisor cannot infer retirement from EOF, successful exit or even an uncommitted matching receipt. The managed Windows marker is boot-bound and distinct from the Razer driver's owned-record restoration. Do not import either a later-boot discard path or a driver baseline into this fixture.

## Watch out
Keep pending or malformed histories closed. An explicit reopen never creates a missing journal. The fixture models process loss on a trusted scratch path. Protected host storage, power-loss behavior, real process identity and native input remain separate activation gates.
