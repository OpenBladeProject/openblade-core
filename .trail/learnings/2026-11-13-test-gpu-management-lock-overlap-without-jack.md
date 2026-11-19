---
title: Test GPU management lock overlap without scheduler deadlines
author: Jack
date: 2026-11-13
branch: fix/950-startup-controller-conflict
touches:
  - tests/OpenBlade.Service.Tests/WindowsVerifiedBladeDeviceProviderProductionTests.cs
---

## Decided
The GPU activity probe lock test must check overlapping operations, not a 100 ms ThreadPool scheduling deadline. Keep the activity fake blocked until explicit cancellation or release, use a dedicated bounded management-state reader, and assert the original read is still incomplete. Enter the try/finally before waiting for the probe, so every failure path cancels/releases and observes the canceled read. Dispose the fake events.

## Tried and ruled out
CI run 43342849676 failed ActivityProbe_DoesNotHoldGpuClockOffsetManagementLock because Task.Delay(100) won while the state task later completed. Source already releases the management lock before the activity probe; no production change is warranted. Merely increasing the timer while retaining the fake's automatic five-second timeout could let a real lock bug pass after the fake unblocked itself.

## Watch out
BlockingGpuActivitySource has only this test caller. Its wait is cancellation-aware and intentionally has no self-release timer; bounded state-reader admission plus finally cleanup prevents a hang and preserves the lock regression signal. This is software test hardening and grants no GPU/hardware authority.
