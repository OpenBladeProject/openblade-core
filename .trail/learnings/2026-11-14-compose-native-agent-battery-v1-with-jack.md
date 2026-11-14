---
title: Compose native Agent Battery.v1 with owned job retirement
author: Jack
date: 2026-11-14
branch: feat/native-agent-battery-report
touches:
  - rust/crates/openblade-platform-windows/Cargo.toml
  - rust/crates/openblade-platform-windows/src/lib.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/build.rs
  - tools/openblade-windows-host/src/native_package.rs
  - tools/openblade-windows-host/src/native_package/tests.rs
  - tools/openblade-windows-host/src/pipe.rs
  - tools/openblade-windows-host/src/session_battery.rs
---

## Decided
Select the first native Agent feature with agent-battery-report-v1. The installed entry composes the owned SID/session Battery.v1 listener alongside the existing origin, status and Stop monitors. Reuse the neutral Battery.v1 wire/report sources; the private Scenario replay is not a production provider. Both native package validators must bind the exact Agent capability to its explicit feature while keeping Service capabilities separate.

The platform supplier uses only fixed System32 powercfg arguments, an OS local-data directory, a private one-SID output DACL, retained original path/file objects and a suspended child born in a non-breakaway kill-on-close job. Root exit plus zero active job processes establishes retirement. Cancellation, disconnect, dispatch timeout, Stop and origin loss signal cancellation and join before ownership is released; uncertain jobs retain their original handles and endpoint admission stays closed until read-only reconciliation confirms retirement.

## Tried and ruled out
Do not compose Scenario::Available, the development .Dev router, an arbitrary command/path supplier, or the old trusted-leaf child seam into the installed candidate. Killing only the root or dropping a spawn_blocking future does not establish process-tree retirement. Report acquisition has no HID, AC-power or competing-controller prerequisite.

## Watch out
This is one partial Agent behavior, with the default installed app still managed. Injected dispatcher tests and strict actual C# frame decoding prove software contracts; the inert test-process descendant proves job-tree behavior. They do not prove the installed endpoint or real powercfg output sharing compatibility. Actual report acquisition, product-role execution, installation, SCM, UAC and hardware were not exercised. Preserve these remaining gates for later authorized runtime validation.
