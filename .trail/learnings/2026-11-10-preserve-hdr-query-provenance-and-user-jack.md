---
title: Preserve HDR query provenance and user preference
author: Jack
date: 2026-11-10
branch: feat/rust-windows-advanced-color
touches:
  - rust/crates/openblade-platform-windows/src/display.rs
  - rust/crates/openblade-platform-windows/src/display/native.rs
  - rust/crates/openblade-platform-windows/src/display/tests.rs
  - src/OpenBlade.Display/WindowsDisplaySettingsController.cs
  - tests/OpenBlade.Display.Tests/OpenBlade.Display.Tests.csproj
  - tests/OpenBlade.Display.Tests/WindowsDisplayNativeContractTests.cs
  - tests/Fixtures/Parity/v1/display-advanced-color.csv
---

## Decided
Use the SDK GET_ADVANCED_COLOR_INFO_2 packet with the adapter LUID and target ID from the successfully selected active internal path. The shared fixture exercises the production managed projection and the scripted Rust adapter.
## Tried and ruled out
The older GET_ADVANCED_COLOR_INFO and generic Advanced Color flags cannot reproduce the managed HDR support and user-preference semantics.
## Watch out
Windows may report HDR user-enabled while HDR is inactive. Preserve native query failures separately from a successful unsupported or disabled reply, and keep zero channel depth absent.
