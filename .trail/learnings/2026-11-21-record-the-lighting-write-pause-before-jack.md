---
title: Record the lighting write pause before publishing Faulted
author: Jack
date: 2026-11-21
branch: fix/blade18-animation-fault-order
touches:
  - Directory.Build.props
  - src/OpenBlade.Protocol/ExperimentalBladePort.Animation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - tests/OpenBlade.Protocol.Tests/Blade18AnimationFaultOrderingTests.cs
---

## Decided
In `ExperimentalBladePort`, a failed lighting write must call `MapWriteFailure` (which calls `policy.RequireRecovery` for a possibly submitted write) before `SetLightingFaulted()`. `OwnershipState` and `recoveryRequired` are both volatile, so with this order any reader that sees Faulted also sees writes paused. This covers the animation loop, the uniform frame, `RelinquishRendererAsync` and `CloseLightingAsync`. The journal-clear path in `SetLightingAsync` only runs when nothing was submitted, so its order does not matter.

## Tried and ruled out
Treating `Blade18SpatialFrameFailure_RejectsFurtherPulsesAndDoesNotReplay` as test-only flakiness. An observer polling without delay saw Faulted with writes still allowed in roughly 70-85% of 500 iterations on the old order, and in none of 1,500 with the fix. A test seam was not needed.

## Watch out
`ProductionLightingPort` and `Rz090528BrightnessLightingFactory` set Faulted in their stream loops without a separate policy pause, so this ordering rule does not map onto them directly. `Blade18AnimationFault_RecordsThePauseBeforeFaultedIsObservable` runs 100 in-process iterations (about 50 ms) and fails on the old order every time it was tried.
