---
title: Fence private ICC selection with complete WCS evidence across restart
author: Jack
date: 2026-11-10
branch: fix/rust-windows-phase-6x
touches:
  - rust/README.md
  - rust/crates/openblade-platform-windows/src/color_profile/scripted.rs
  - rust/crates/openblade-platform-windows/src/color_profile/tests.rs
  - rust/crates/openblade-platform-windows/src/color_profile/transaction.rs
  - tools/openblade-windows-host/src/session_display.rs
  - tools/openblade-windows-host/src/session_display/color.rs
  - tools/openblade-windows-host/src/session_display/journal.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.ColorProfileRecovery.cs
---

## Decided
Phase 6x composes the exact private ICC pipe and display restart guard parents in a separate merge checkpoint. The scripted ICC transaction journals before any setter. An independent observation of the exact monitor and SDR preference, active WCS scope, and both scopes' defaults and associations distinguishes the saved baseline, the expected selection, and partial state.

## Tried and ruled out
Matching the displayed ICC ID does not settle intent. An association can change without changing the displayed baseline, and the requested ID can be visible with another scope or an unrelated association. A reply lost after confirmed readback leaves the intent pending.

## Watch out
The replacement fixture must own the same exclusive endpoint after the old process exits and must be started for explicit reconciliation. Its query performs no setter. Keep native validation, product integration, and private parent review separate.
