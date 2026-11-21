---
title: Pending GPU confirmation halves and null offset evidence
author: Jack
date: 2026-11-21
branch: feat/gpu-offsets-rz09-0529-0530
touches:
  - CHANGELOG.md
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Service/ControlCoordinator.cs
  - src/OpenBlade.Service/ServiceComposition.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs
  - src/OpenBlade.Service/WriteConfirmationPolicy.cs
---

## Decided
- A pending thermal confirmation with a GPU half is judged from the device GpuClockOffsetManagementState plus the managed offset observation (MatchPendingWriteConfirmation in ServiceControlRuntime). Experimental resume must use it too; Match(..., managementState: null) can never settle a GPU half.
- An enabled GPU half with no observed offsets returns GpuClockOffsetsUnconfirmed: the entry is retired (thermal half confirmed intentional, GPU half re-sent with the next change), never treated as Match. A disabled half matches only on ResetConfirmed or RelinquishedDefaultConfirmed.
- null == null offsets are not evidence. After an indeterminate NVML write, "prior state remained active" needs non-null observed offsets or a confirmed reset state (GpuClockOffsetsUnchangedAfterIndeterminateWrite in ControlCoordinator). The device demotes its own confirmation when Set throws anything but KnownNotApplied.
- SerializedBladeDevice.ApplyGpuPolicy returns null when the policy is absent, so a paused experimental port admits no NVML write; ReadManagedGpuClockOffsetsAsync returns the cached confirmation without refreshing while paused.

## Tried and ruled out
- Changing the IVerifiedBladeDeviceProvider recovery callback signature: the provider augments the observation with GpuClockOffsets instead.
- Adding a UsbC power gate for GPU offsets: the shared rule is PowerSourcePolicy.IsExternal; the Synapse low-wattage hiding is documented as a divergence.

## Watch out
- The same pending entry blocks thermal writes on RZ09-0581 and RZ09-0528 (production path, no pause), so tests must cover the production factory as well as experimental resume.
- WindowsVerifiedBladeDeviceProviderProductionTests fake ports run out quickly; GPU paths need about 20.
