---
title: Separate durable profile quarantine from thermal readback
author: Jack
date: 2026-11-10
branch: test/rust-thermal-profile-composition
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/src/battery_runtime.rs
  - tools/openblade-windows-host/src/fan_runtime.rs
  - tools/openblade-windows-host/src/fan_runtime/device.rs
  - tools/openblade-windows-host/src/performance_runtime.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/wire.rs
  - tools/openblade-windows-host/src/battery_runtime/thermal.rs
---

## Decided
The Windows foreground composition uses the migrated managed profile aggregate and one scenario lock for battery, thermal and profile IPC. Thermal overrides in this scenario are temporary. An owned handback preserves the temporary Fixed intent.

## Tried and ruled out
A process-local thermal override is insufficient evidence for the separate managed persistent override store. The profile aggregate verifies the saved battery policy and its transaction sidecar.

## Watch out
An unresolved configuration transaction must stop setters and handback while leaving independent getters available. A lost final fan acknowledgement followed by matching topology confirms the observed topology without establishing ownership or exact RPM. Trail check can match phrases in inherited parity workflow or failure assertions against unrelated learnings. The composed path still honors store-before-publication for the profile and requires fresh recipe admission and readback.
