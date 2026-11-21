---
title: "PR 991 uncertain NVML submissions, continuation scope and resume drift"
author: Jack
date: 2026-11-21
branch: feat/gpu-offsets-rz09-0529-0530
touches:
  - src/OpenBlade.Service/ControlCoordinator.cs
  - src/OpenBlade.Service/GpuClockOffsetManagement.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs
  - src/OpenBlade.Service/WriteConfirmationPolicy.cs
  - tests/OpenBlade.Service.Tests/ExperimentalBladeBatteryIntegrationTests.GpuClockOffsets.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.cs
  - tests/OpenBlade.Service.Tests/WindowsVerifiedBladeDeviceProviderProductionTests.cs
  - tests/OpenBlade.Service.Tests/WriteConfirmationPolicyTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.GpuClockOffsetResolution.cs
---

## Decided
- An NVML set that fails with anything but KnownNotApplied puts the device in `GpuClockOffsetManagementState.SubmissionUncertain` (WindowsVerifiedBladeDeviceProvider). It records the prior state and whether the write was a reset. The next managed read queries the driver once, ignoring the dGPU activity gate, and resolves the state from the real offsets: defaults after a reset (or after a write that left a confirmed default) restore ResetConfirmed or the prior default state; anything else becomes Managed with the reading clipped to the policy (or, while experimental writes are paused, to the model's PotentialCapabilities envelope).
- WriteConfirmationPolicy.Match returns Unavailable for SubmissionUncertain, so the pending write blocks thermal writes and resume instead of retiring as GpuClockOffsetsUnconfirmed. The plain retire now only happens when NVML never ran or its confirmation was lost.
- ControlCoordinator's indeterminate-write resolver takes a predicate: an uncertain submission whose follow-up read found no offsets returns ConfirmationUnavailable, never "remained active" or a contradiction. The reset disjunct in GpuClockOffsetsUnchangedAfterIndeterminateWrite is reachable (an uncertain enable that left the confirmed default in place restores ResetConfirmed).
- A deferred power transition no longer drops when a dependency's firmware half matched and only the GPU half is unconfirmed: TransitionContinuationScope adds that subsystem back to the continuation scope, so its fresh pre-write read sends the offsets through the GPU-only setter before the deferred steps.
- Experimental resume calls driftTracker.Observe(recovered) before reconciling pending writes, so the write resume confirmed is not latched as external on the next observation.
- Manual-fan performance matching skips the hidden preset but still judges the GPU half.
- A paused managed read still queries an active dGPU and drops the cached confirmation when Current differs; ranges stay the ones confirmed under the policy.

## Tried and ruled out
- Keeping the plain retire for uncertain submissions: it erased a write that may have changed the offsets.
- Querying on every read while uncertain: a failed ungated query is retried only while the dGPU is active, to bound wakes.

## Watch out
- A driver reading outside the admitted envelope resolves to Managed with no confirmation, and the pending half then retires as unconfirmed.
- With an idle dGPU and a failed first query the pending write stays blocked until NVIDIA activity or a power change.
- Mutation checks for each rule live in the *.GpuClockOffsetResolution.cs test files and the experimental GpuClockOffsets tests; keep them discriminating when changing these paths.
