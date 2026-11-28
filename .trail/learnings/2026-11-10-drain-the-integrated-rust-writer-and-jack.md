---
title: Drain the integrated Rust writer and renderer on STOP
author: Jack
date: 2026-11-10
branch: feat/rust-private-service-integration
touches:
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/src/battery_runtime.rs
  - tools/openblade-windows-host/src/battery_runtime/lighting.rs
  - tools/openblade-windows-host/src/battery_runtime/power_profile.rs
  - tools/openblade-windows-host/src/battery_runtime/thermal.rs
  - tools/openblade-windows-host/src/fan_runtime.rs
  - tools/openblade-windows-host/src/performance_runtime.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/windows/scm/tests.rs
  - tools/openblade-windows-host/src/wire.rs
---

## Decided
- Use the same private Windows host lifecycle for the protected and ordinary test writers. Finish writer admission before claiming the endpoint. Start the scripted renderer only after readiness.
- Serialize accepted profile transactions and scoped battery, thermal and lighting intent through the shared scenario. Retain the endpoint claim and writer until STOP closes clients and both the admitted transaction and renderer retirement settle.

## Tried and ruled out
- Canceling the combined driver on STOP can drop the only scripted port acknowledgements and turn an orderly drain into an uncertain release.
- Clearing one projected fence cannot stand in for resolving the other. Recheck configuration admission after waiting for an owner and never submit Off from the retirement task.

## Watch out
- Unresolved external adapter evidence grants no write admission. Expire source-scoped intent on a confirmed source change and keep desired profiles separate from observed device state.
- Callback and private-pipe tests use owned fixture paths and scripted devices. Enabled-Administrator ACL and native SCM checks are separate gates.
