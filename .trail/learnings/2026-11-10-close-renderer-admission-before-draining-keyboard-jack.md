---
title: Close renderer admission before draining keyboard Off
author: Jack
date: 2026-11-10
branch: test/rust-lighting-renderer-drain
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Protocol.Tests/ProductionLightingPortTests.cs
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/src/battery_runtime.rs
  - tools/openblade-windows-host/src/battery_runtime/lighting.rs
  - tools/openblade-windows-host/src/windows.rs
  - tools/openblade-windows-host/src/wire.rs
  - tools/openblade-windows-host/src/battery_runtime/lighting/renderer.rs
---

## Decided
Phase 5h uses a single bounded asynchronous fixture producer. Closing frame admission starts an independently retained retirement task. The synthetic native lease stays with the worker through in-flight completion and port release. Only a verified release lets a live Off request run the existing brightness sequence.

## Tried and ruled out
A scoped Off status or cancellation of the request cannot stand in for joining the producer and retiring its native lease. The pipe wait and retirement have separate lifetimes.

## Watch out
The managed ordinary Off path drains the same port. Managed invalidation separately retains the closing port after a caller cancels its wait. Keep those oracles distinct. A deadline or unverified native release leaves the renderer fenced even after a late worker exit. Keyboard readback uncertainty is a separate fence. The fixture exercises synthetic effects and establishes no production device admission.
