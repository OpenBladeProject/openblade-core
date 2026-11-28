---
title: Keep private HDR confirmation bound to the internal panel
author: Jack
date: 2026-11-10
branch: feat/rust-windows-phase-6s
touches:
  - rust/README.md
  - rust/crates/openblade-platform-windows/src/display/native.rs
  - rust/crates/openblade-platform-windows/src/display/scripted.rs
  - tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.cs
  - tools/openblade-windows-host/src/session_display.rs
  - tools/openblade-windows-host/src/session_windows.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.Hdr.cs
  - tools/openblade-windows-host/src/session_display/hdr.rs
---

## Decided

Phase 6s adds only SetHdr to the existing private v2 display fixture. Use one typed SET_HDR_STATE packet after checking the scripted active session, power generation, support and exact single internal source, target and monitor path. Reobserve the same target after submission and compare the INFO_2 user preference. The native setter exists only in the platform unit-test binary and is linked without invoking it.

## Tried and ruled out

A matching preference on another target or an unchanged physical SDR signal does not confirm the request. A confirmed no-op never enters the setter. SDR profiles remain unavailable independently of the preference.

## Watch out

Ambiguous submission, readback or delivery pauses display mutations for the fixture lifetime. A later query may observe the result but cannot authorize a replay. Real session and power evidence, restart-safe recovery, external review authorization and attended validation are still separate gates.
