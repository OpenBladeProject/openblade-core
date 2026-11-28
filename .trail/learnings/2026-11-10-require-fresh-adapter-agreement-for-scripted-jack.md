---
title: Require fresh adapter agreement for scripted power-profile transitions
author: Jack
date: 2026-11-10
branch: test/rust-power-profile-transition
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/src/battery_runtime.rs
  - tools/openblade-windows-host/src/battery_runtime/thermal.rs
  - tools/openblade-windows-host/src/fan_runtime.rs
  - tools/openblade-windows-host/src/fan_runtime/device.rs
  - tools/openblade-windows-host/src/performance_runtime.rs
  - tools/openblade-windows-host/src/performance_runtime/device.rs
  - tools/openblade-windows-host/src/performance_runtime/tests.rs
  - tools/openblade-windows-host/src/windows.rs
---

## Decided

Use the same private migrated profile host for one automatic standard preset transition. Compare known decoded adapter identities before mapping to sources. Keep the source generation separate from display and verify a fresh scripted adapter reply before each setter.

## Tried and ruled out

OS external and the last displayed full-adapter source provide no write authority. Differing known replies fail even when both map to full power. Battery and limited adapter selections leave their thermal recipes closed in this slice.

## Watch out

New lifecycle evidence invalidates an earlier plan. A lost setter echo with an unavailable getter retains last confirmed state, blocks consuming another transition, and settles only through readback. The C# client sees profile provenance and the test verifies unchanged durable bytes. The installed lifecycle and protected write recovery still need independent admission.
