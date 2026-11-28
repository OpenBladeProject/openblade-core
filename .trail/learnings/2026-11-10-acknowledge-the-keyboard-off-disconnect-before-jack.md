---
title: Acknowledge the keyboard Off disconnect before advancing retirement
author: Jack
date: 2026-11-10
branch: test/rust-lighting-renderer-drain
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/src/battery_runtime/lighting.rs
  - tools/openblade-windows-host/src/battery_runtime/lighting/renderer.rs
  - tools/openblade-windows-host/src/wire.rs
---

## Decided
An ordinary unowned Off request queues on the existing scenario lock. Reserve the request across asynchronous drain only for the caller that closed renderer admission. Publish the disconnect acknowledgment after dropping that caller's wait, so the C# fixture can advance the native worker without racing an abandoned request.

## Tried and ruled out
A successful state refusal alone cannot prove the synthetic native lease stayed owned. Probe the lease directly before frame completion and again while native port release is still pending.

## Watch out
An early step should keep the scripted host available. The explicit retirement deadline latches uncertainty, while late fixture steps can still finish the worker and release its simulated native lease.
