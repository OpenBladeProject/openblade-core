---
title: Keep confirmed battery policy separate from temporary intent
author: Jack
date: 2026-11-09
branch: feat/rust-battery-parity
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/src/main.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/wire.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.RustBatteryParity.cs
  - tools/openblade-windows-host/src/battery_policy.rs
  - tools/openblade-windows-host/src/battery_runtime.rs
---

## Decided
Standard RZ09-0581 battery requests commit temporary intent only after scoped confirmation. The configured charge limit stays in the saved profile. A power-source change clears the protection override and retains the global charge-limit override.

## Tried and ruled out
A lost setter acknowledgement must not trigger a semantic getter on the write path or commit the request from its target value. Keep confirmed state, staged intent and the durable profile separate.

## Watch out
The query-capable battery path needs confirmed inactive temporary full charge for new standard setters and live success. A pending policy-only confirmation can settle from matching battery fields when the temporary flag is unavailable. Foreground battery parity uses fake effects and keeps other development endpoints read-only.
