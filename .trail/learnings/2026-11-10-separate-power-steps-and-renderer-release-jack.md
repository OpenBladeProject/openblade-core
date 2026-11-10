---
title: Separate power steps and renderer release in the private Rust host
author: Jack
date: 2026-11-10
branch: feat/rust-private-service-integration
touches:
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.Protected.cs
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/src/battery_runtime.rs
  - tools/openblade-windows-host/src/battery_runtime/lighting.rs
  - tools/openblade-windows-host/src/battery_runtime/power_profile.rs
  - tools/openblade-windows-host/src/fan_runtime.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/windows/scm/tests.rs
  - tools/openblade-windows-host/src/wire.rs
---

## Decided
- Close the frame on a renderer retirement signal or STOP independently of the power script. Reserve a separate acknowledgement for scripted port release. Keep EOF and timeout fenced.
- On a verified source change reset the desired preset's source with its value. Expire temporary fan intent without fabricating a handback. A confirmed full adapter permits the existing owned-pair cleanup before spending the planned preset transition.

## Tried and ruled out
- A shared step queue let a pending power trajectory delay Off and could lose the only port completion signal.
- Selecting full power before checking the Automatic baseline could consume the transition and prevent the next authorized preset attempt.

## Watch out
- The combined C# case uses a regular fixture. The protected path is exercised separately by the enabled-Administrator test, including retention of the writer lease during STOP_PENDING.
- The status block is an aggregate summary. Typed battery and lighting operations retain their own scope and fences.
