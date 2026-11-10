---
title: Drive the private v2 display query through scripted native acquisition
author: Jack
date: 2026-11-10
branch: feat/rust-windows-display-integration
touches:
  - rust/crates/openblade-platform-windows/src/display/scripted.rs
  - tools/openblade-windows-host/src/session_display.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.cs
---

## Decided
Phase 6e runs the real private v2 query handler with fresh scripted CCD, GDI and INFO_2 inputs. The Windows topology and refresh bracket checks reject a target replacement even with identical rates and a reused source name. Compare the resulting wire snapshot to the existing managed oracles.

## Tried and ruled out
The prebuilt v2 success response bypasses acquisition and cannot exercise stale target identity. Product HDR preference and channel depth do not establish the active output mode.

## Watch out
Successful empty topology and a failed query must remain distinct on the wire. With the boost flag off, 60/240 leaves the key unset even when both fixed options are available. The restricted review of the combined tree requires direct authorization.
