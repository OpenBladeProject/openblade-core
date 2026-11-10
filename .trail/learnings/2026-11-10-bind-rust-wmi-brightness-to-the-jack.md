---
title: Bind Rust WMI brightness to the submitted internal panel
author: Jack
date: 2026-11-10
branch: feat/rust-wmi-brightness
touches:
  - rust/README.md
  - rust/crates/openblade-platform-windows/src/lib.rs
  - rust/crates/openblade-platform-windows/src/brightness.rs
  - rust/crates/openblade-platform-windows/src/brightness/tests.rs
---

## Decided
Use public PR 931's exact Windows CCD topology commit for Phase 6g and the exact public PR 939 head as the managed brightness oracle. One Rust owner admits both WMI entry points, picks a supported level, binds the active method object, and requires independent observation from the full submitted instance.

## Tried and ruled out
A provider Boolean is not success or rejection on its own. A sibling suffix with the requested level cannot confirm the mutation. Timeout and post-submission cancellation never justify replay or fallback. Trail matched unrelated historical phrases in the adapter documentation without a semantic conflict.

## Watch out
The phase is a scripted orchestration slice. Before production use, the concrete current-user transport must enforce deadlines and isolate outstanding calls, and the Session Agent must own the stop and recovery policy. No panel or WMI operation was run in tests.
