---
title: Compose the run-marker callback with child retirement in scratch
author: Jack
date: 2026-11-10
branch: test/windows-rust-phase-8l-transition
touches:
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.RunMarkerTransition.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.WriterRetirement.cs
---

## Decided

The Setup scratch theory can call the real managed run-marker transition constructor through a test-only accessor. Reuse the Rust fixture, its pinned directory and lock file, the private receipt and original process handle, and the existing fake service stop and recovery checks. Keep the scratch composition out of production registration.

## Tried and ruled out

A simulated generation callback covers the store in isolation. Compose it with the real scratch retirement gate to verify receipt and observer failures at the marker write boundary. The phrase matches from Trail on the exact run-marker input concern broader startup writes, path-only deletion and treating a lease as retirement. The integrated behavior is opt-in, compares owned bytes before deletion and requires the separate gate.

## Watch out

When checking marker evidence before STOP, enumerate entries without opening the exclusive lock. After STOP, a complete durable receipt cannot substitute for the original observer or its child frame. The marker temp guard runs before the retirement callback.
