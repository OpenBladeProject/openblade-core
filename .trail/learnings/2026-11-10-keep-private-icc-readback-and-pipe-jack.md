---
title: Keep private ICC readback and pipe delivery separate
author: Jack
date: 2026-11-10
branch: feat/rust-windows-phase-6u
touches:
  - rust/crates/openblade-platform-windows/src/color_profile.rs
  - rust/crates/openblade-platform-windows/src/color_profile/tests.rs
  - rust/crates/openblade-platform-windows/src/color_profile/transaction.rs
  - rust/crates/openblade-platform-windows/src/display/scripted.rs
  - tools/openblade-windows-host/src/session_display.rs
  - tools/openblade-windows-host/src/session_windows.rs
  - rust/crates/openblade-platform-windows/src/color_profile/scripted.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.ColorProfile.cs
  - tools/openblade-windows-host/src/session_display/color.rs
---

## Decided
The private Windows v2 display replay projects WCS availability and SDR selection independently of refresh, HDR and brightness. The typed ICC setter reuses the scripted transaction and the shared display write guard. Its final no-op requires the same monitor and scope with fresh preference and independent selected-profile evidence.

## Tried and ruled out
A failed submission with a visible change does not establish ownership. A fresh match to the last verified state can admit restoration of earlier proven changes. Changing active-session or power evidence blocks further effects, including restoration.

## Watch out
Losing delivery after a submitted ICC operation pauses every display setter for the fixture lifetime, even after successful readback. A later query may report current state while that pause remains. Use the merge checkpoint as Trail's comparison base to separate Phase 6u from inherited Phase 6t matches. Native ICC mutation and live verification remain outside this local replay.
