---
title: Confirm both battery readings before settling temporary full charge
author: Jack
date: 2026-11-10
branch: test/rust-temporary-full-charge
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/src/battery_policy.rs
  - tools/openblade-windows-host/src/battery_runtime.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/wire.rs
---

## Decided
The RZ09-0581 production-wired Temporary Full Charge oracle independently admits 07/92 policy reads, 07/8F flag reads, and captured 07/0F enable and cancel. The existing C# ServiceClient can drive those typed effects through the private foreground host. Enable and cancel preserve the actual confirmed cap and protection even when the saved cap differs.

## Tried and ruled out
A partial policy-only read cannot settle a possibly submitted one-shot. Quarantine retains the expected flag and confirmed starting cap. Reconciliation and confirmed completion issue no setters. A fresh 07/8F guard refuses when any unrelated Power Mode Control bit is set.

## Watch out
The mode is local and uses private fixture storage and scripted reports only. No actual Windows service, power transition, HID transport, or physical validation is part of this replay.
