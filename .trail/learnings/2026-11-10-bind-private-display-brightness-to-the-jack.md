---
title: Bind private display brightness to the selected panel
author: Jack
date: 2026-11-10
branch: feat/rust-phase-6k-display-wmi
touches:
  - rust/README.md
  - rust/crates/openblade-platform-windows/src/brightness.rs
  - rust/crates/openblade-platform-windows/src/brightness/tests.rs
  - rust/crates/openblade-platform-windows/src/brightness/worker.rs
  - rust/crates/openblade-platform-windows/src/brightness/worker/tests.rs
  - rust/crates/openblade-platform-windows/src/display/scripted.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.cs
  - tools/openblade-windows-host/src/session_agent.rs
  - tools/openblade-windows-host/src/session_display.rs
  - tools/openblade-windows-host/src/session_windows.rs
---

## Decided
The private v2 display handler binds the brightness read and typed set to the selected CCD endpoints and monitor path. One fixture worker belongs to each agent. Independent display fields survive unavailable WMI. A possibly submitted write with no definitive reply pauses writes for that agent, even if later queries can report brightness.

## Tried and ruled out
The original scripted refresh path had no monitor path, so supplying a synthetic path also requires a synthetic unsupported EDID result. A sibling WMI InstanceName at the requested level is not confirmation. A cancelled pre-submit request may leave the worker busy briefly; check the baseline through a bounded read without replaying the set.

## Watch out
The scripted COM policy starts no native transport. Process exit and a caller deadline do not prove provider completion. Live ownership, process security, fresh power and session admission, durable recovery and attended hardware validation are separate. External review of held source and derivatives requires separate authorization.
