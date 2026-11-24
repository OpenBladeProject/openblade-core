---
title: "PR 991 round 3: GPU re-send limit and power-change block release"
author: Jack
date: 2026-11-21
branch: feat/gpu-offsets-rz09-0529-0530
touches:
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/ControlCoordinator.cs
  - src/OpenBlade.Service/NvidiaGpuClockOffsetController.cs
  - src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs
  - src/OpenBlade.Tray.Core/MainViewModel.cs
  - tests/OpenBlade.Service.Tests/WindowsVerifiedBladeDeviceProviderProductionTests.GpuClockOffsetResolution.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.TransitionContinuationGpuResend.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.PowerChangePendingRelease.cs
---

Touches: src/OpenBlade.Service/ServiceControlRuntime.cs, src/OpenBlade.Service/ControlCoordinator.cs, src/OpenBlade.Service/NvidiaGpuClockOffsetController.cs, src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs, src/OpenBlade.Tray.Core/MainViewModel.cs, tests/OpenBlade.Service.Tests/WindowsVerifiedBladeDeviceProviderProductionTests.GpuClockOffsetResolution.cs, tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.TransitionContinuationGpuResend.cs, tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.PowerChangePendingRelease.cs

## Decided
- A deferred power-transition continuation may put an unconfirmed GPU half back in scope at most once per chain. `PendingTransitionContinuation.GpuResendAttempted` is set when a continuation wrote a subsystem outside its deferred `Subsystems`; `TransitionContinuationScope` then stops widening. Without it, a re-send that kept failing uncertainly (resolved back to default evidence or Managed with no observation) was retired as Unconfirmed and widened again every 15 s, and the deferred fans, battery and lighting never applied.
- The resolver returns a `ContinuationWriteScope(Subsystems, GpuOnly)`. The coordinator writes a GpuOnly step only when `CanApplyGpuClockOffsetsOnly` holds for the transition's fresh initial read, and never through the paired firmware setter or the direct automatic pair. Under manual fans the policy skips the preset (deliberately, PR #128), so without this gate a lost preset write could be replayed by the continuation.
- `PublishFullTransitionOutcomeAsync` gets `continuation.Subsystems` plus the outcome's `WriteAttempted`, so the continuation's own GPU re-send clears its drift latch.
- `NvidiaGpuClockOffsetController.TryRestore` attempts each restore write independently and decides from a fresh readback: a refused write that left the driver at the prior offsets is KnownNotApplied, so a driver that refuses every write keeps the earlier management state.
- A power-source change that invalidates pending confirmations also releases the published write block they held (`ReleaseWriteBlockHeldByPendingConfirmations`), unless another explicit block holds (`HasExplicitNonPowerWriteBlock`) or the power is unclassified. The destination transition still does fresh admission. This was a pre-existing main defect: the destination thermal step was refused with "Thermal write authority closed".
- The tray's unconfirmed-change text says GPU clock tuning is checked again when the GPU is next in use or the power source changes, because Check current state cannot read an idle dGPU.

## Tried and ruled out
- Changing the manual-fans preset shortcut in WriteConfirmationPolicy: a wrong Mismatch recreates the PR #128 bug. The gate belongs in the coordinator.
- Clearing WritesBlocked unconditionally at a power change: it could discharge another owner's block.

## Watch out
- `sed -i` in Git Bash rewrote CRLF test files as LF; renormalize after it.
- The direct-automatic-pair exclusion for a GpuOnly step has no dedicated test.
