---
title: Replay Windows battery reports on the private Rust Session pipe
author: Jack
date: 2026-11-10
branch: feat/windows-rust-battery-report
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/session_agent.rs
  - tools/openblade-windows-host/src/session_gpu.rs
  - tools/openblade-windows-host/src/session_windows.rs
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.Battery.cs
  - tools/openblade-windows-host/src/session_battery.rs
  - tools/openblade-windows-host/src/session_battery/report.rs
  - tools/openblade-windows-host/src/session_battery/tests.rs
---

## Decided
The Rust Session Battery report uses a separate private current-user fixture and the managed v1 typed wire. Battery policy remains a separate path.

## Tried and ruled out
The Windows XML reader emits an end-element event even for self-closing UsageEntry elements. Use that event for every stack pop. A dispatch deadline retires the connection without writing a Failed reply.

## Watch out
Synthetic XML and managed client parity do not establish native report acquisition. Preserve fixed executable/arguments, process and output bounds, cancellation, cleanup and exact peer admission when porting that path.
