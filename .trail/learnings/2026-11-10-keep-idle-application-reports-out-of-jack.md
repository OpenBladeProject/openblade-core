---
title: Keep idle application reports out of unified status updates
author: Jack
date: 2026-11-10
branch: fix/rust-phase-5n-idle-app-context
touches:
  - tools/openblade-windows-host/src/application_runtime.rs
  - tools/openblade-windows-host/src/battery_runtime/application.rs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.Application.cs
---

## Decided
Treat report acceptance and lease effects separately in both private Rust report paths. Idle reports record the current reporter sequence and return Stored without projecting status or waking the unified deadline watcher. The managed service keeps the same behavior.

## Tried and ruled out
A null or unmatched path alone cannot identify the idle case. A report that clears an active selection still publishes the fallback intent, and a real renewal still extends the deadline.

## Watch out
The unified report handler must skip the shared status update for idle and stale outcomes. Keep scripted power reconciliation and readback independent so context reports cannot consume pending performance-write evidence or release renderer ownership.
