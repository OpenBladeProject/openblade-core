---
title: Cooling-pad curves must ignore a powered-down GPU
author: Jack
date: 2026-11-21
branch: fix/cooling-pad-razer-driver-stack
cost: { minutes: 11, tokens: 185293 }
touches:
  - CHANGELOG.md
  - docs/cooling-pad.md
  - src/OpenBlade.Service/CoolingPadAutoCurves.cs
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - tests/OpenBlade.Service.Tests/CoolingPadAutoCurvesTests.cs
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
---

## Decided
The pad's curve and Automatic modes treat a GPU that Windows reports powered down (FanTemperatureReadings.GpuPoweredDown with a null GPU reading) as ignored, the same way the laptop FanCurveCalculator does. With Both they follow the CPU; with a GPU-only source they run at the first point of the GPU curve or Synapse table. A genuinely missing reading throws the runtime's private TemperatureUnavailableException: MaintainAsync keeps control and the last confirmed target and only updates the message, and ApplyAsync returns Blocked because the target is computed before any submission.

## Tried and ruled out
Treating any null selected reading as a failure. ReadForFanCurveAsync skips GPU reads in D3 (PR #946), so on RZ09-0581, whose dGPU drops to D3 every few seconds, Automatic with Both revoked pad control about 50 s after it started (Active false, RecoveryRequired true).

## Watch out
Fail(exception, submitted) revokes control when nothing was submitted, and the tray treats a Failed apply as uncertain, so a sensor gap must never reach Fail. The tray's automatic 5 s read replaces the runtime's status message, so check pad state with a cached GetStatus watch rather than the message after a read.
