---
title: Prepare CPU worker fixtures before malformed sample assertions
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - tests/OpenBlade.Platform.Tests/WindowsCpuTemperatureReaderTests.cs
---

## Decided
Malformed CPU response tests explicitly prepare the fake worker before issuing their one sample, and assert that the sample was sent. Use a three-second test read budget for this protocol assertion; production readiness, sample deadlines, and ownership behavior are unchanged.

## Tried and ruled out
CI passed all 1,860 Capture tests after the guardian fixture corrections, but one malformed-response case waited for Stop after a cold read returned null. The shared 100 ms test deadline permits the real reader to defer sampling during initialization. Increasing only the Stop wait cannot cause the missing sample to be dispatched, and changing production warmup ownership would break the intended contract.

## Watch out
A null temperature can mean cold initialization is still owned, not that a malformed reply was consumed. Establish readiness and prove a sample request before asserting invalid-response cleanup. Keep tests that deliberately exercise cold initialization and shortened deadlines separate.
