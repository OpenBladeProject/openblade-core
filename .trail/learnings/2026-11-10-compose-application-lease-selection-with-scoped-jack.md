---
title: Compose application lease selection with scoped scripted reconciliation
author: Jack
date: 2026-11-10
branch: feat/rust-phase-5n-app-lease-composition
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.Protected.cs
  - tools/openblade-windows-host/src/application_runtime.rs
  - tools/openblade-windows-host/src/battery_runtime.rs
  - tools/openblade-windows-host/src/battery_runtime/lighting.rs
  - tools/openblade-windows-host/src/battery_runtime/power_profile.rs
  - tools/openblade-windows-host/src/battery_runtime/thermal.rs
  - tools/openblade-windows-host/src/performance_runtime.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/windows/scm/tests.rs
---

## Decided
Keep the application context lease and its sequence watermark under the composed fixture's status guard. Full-adapter scripted steps consume the current lease-selected preset through the existing scoped recipe and readback. Temporary manual intent wins and expires on a power change. The fixture rule uses the complete managed wire shape so an explicit battery-only profile save can round-trip it without persisting it.

## Tried and ruled out
Context reports and deadline callbacks carry observation only. They supply no authority to apply a preset, settle a pending write, or release a renderer. Battery and limited power still have selection only. Refused and stale reports leave the watermark and published snapshot untouched.

## Watch out
Keep guarded expiry and stale reports from reviving the old selection. Preserve the prior target while possible submission awaits readback, even when newer application intent arrives. STOP cancels the application step and deadline drivers while the renderer completes its independent release and the writer remains retained. The private fixture does not establish native session observation, physical behavior, or deployment readiness.
