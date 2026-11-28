---
title: Keep private refresh writes tied to both CCD rates and the exact panel
author: Jack
date: 2026-11-10
branch: feat/rust-windows-phase-6t
touches:
  - rust/README.md
  - rust/crates/openblade-platform-windows/src/display.rs
  - rust/crates/openblade-platform-windows/src/display/native.rs
  - rust/crates/openblade-platform-windows/src/display/refresh.rs
  - rust/crates/openblade-platform-windows/src/display/refresh/edid.rs
  - rust/crates/openblade-platform-windows/src/display/scripted.rs
  - rust/crates/openblade-platform-windows/src/display/tests.rs
  - tools/openblade-windows-host/src/session_display.rs
  - tools/openblade-windows-host/src/session_display/hdr.rs
  - tools/openblade-windows-host/src/session_windows.rs
---

## Decided
Phase 6t accepts the existing typed refresh request on the private Windows v2 fixture. Keep the desktop rate, physical signal and boost bit independent. Use one CCD frame for path and mode indices, and share the display mutation pause across refresh, HDR and brightness.

## Tried and ruled out
A split 60/240 observation with boost off confirms neither fixed rate. Omitting just the target index is appropriate when a fixed request lacks a validated signal. Dynamic refresh requires an admitted physical timing. Neither a successful apply acknowledgement nor a matching sibling confirms a mutation.

## Watch out
The high halves of the virtual-mode words identify source and target modes. The target low half identifies the desktop image. Keep the entire source word and the target low half on fixed fallback. Validate the exact packet, reread the panel and power context before submission, and pause on ambiguous completion. Native linkage here is a unit-test-only import. Live validation and durable recovery need separate authorization.
