---
title: "PR 991 round 4: refused GPU step, power-change driver read"
author: Jack
date: 2026-11-21
branch: feat/gpu-offsets-rz09-0529-0530
touches:
  - src/OpenBlade.Service/ControlCoordinator.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/WindowsVerifiedBladeDeviceProvider.cs
  - src/OpenBlade.Service/GpuClockOffsetManagement.cs
  - tests/OpenBlade.Service.Tests/ExperimentalBladeBatteryIntegrationTests.GpuOnlyRefusal.cs
  - CHANGELOG.md
  - docs/experimental-blade-support.md
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.GpuOnlyStepRefusal.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.PendingReleaseGuards.cs
  - tests/OpenBlade.Service.Tests/WindowsVerifiedBladeDeviceProviderProductionTests.PowerChangeGpuResolution.cs
---

PR #991 round 4: GPU-only refusals, the power-change driver read, and the mutant set.

## Decided
- A GPU-only Performance step refused before submission (KnownNotApplied from the NVML restore readback, or CapabilityUnavailable) no longer stops the other thermal steps. `IsRefusedGpuClockOffsetsOnlyStep` in ControlCoordinator exempts it on both the runner path and the direct path. A refused firmware preset step, a guard Blocked result, a failed ThermalPreWriteRead and the direct automatic pair still stop them.
- `ScopedApplyOutcome.GpuClockOffsetsOnly` and `FullTransitionApplyOutcome.GpuClockOffsetsOnlyTried` carry "a GPU-only setter was chosen" to the runtime. The continuation re-send flag is set on any GPU-only attempt outside the deferred set, refused or submitted, so a recreated continuation cannot send a third time.
- After an uncertain NVML submission, a power change re-arms one ungated read-only driver query (`RearmUncertainGpuClockOffsetResolution`, consumed in `ApplyFullTransitionAsync`). The write preflight query and Relinquish both resolve the uncertain state from what they read. If the driver can't be read, Relinquish returns without releasing and the state stays SubmissionUncertain. Otherwise a failed query is still retried only while the dGPU is active.
- R8 (the ExternalUnknown check in `ReleaseWriteBlockHeldByPendingConfirmations`) was dead code, because ChangePowerSourceCore returns earlier for ExternalUnknown. It is removed.
- A guard-refused destination thermal step, and an unresolved HID write whose fresh read shows another value or fails, still drop the step. Deferring those is not a small change, so it is a known limitation, along with F6 (the 2-minute expiry drops deferred steps).

## Tried and ruled out
- Killing R3 (removing `.Except(pending.Subsystems)` from the GPU-only set). It is equivalent: it differs only when Performance is both a written dependency and deferred. That needs FansFirst in a continuation after Performance was written earlier in the chain, which requires an outside fan change, and that change ends in a Mismatch instead.
- Killing R7 (the IsUnclassifiedExternalContext guard in the release). It is unreachable at that point, because every path that yields a classified source clears `externalClassificationUnavailable`. The guard is kept as defense in depth.
- Killing R10 (release without pending) and R9 (the DeviceVerified guard). R10: no published block reaches the release with a verified device, no pending confirmation and no explicit non-power block. Entering the LowPowerGate does not publish WritesBlocked, the publish layer re-derives pause, journal and reconciliation blocks, and power-only blocks come with the unclassified context or ExternalUnknown. R9: PublishObservationUnavailable sets retainedNonPowerWriteBlock whenever verification is lost. To pin R6, use one missed read (CheckingConnection with a verified device), because the publish layer masks the experimental-pause arm.
- Using `Provider.Available = false` with a single observe to get an unverified device. Bounded retries keep DeviceVerified true for about three observes, and pending confirmations survive the loss.

## Watch out
- The round-3 learning (2026-11-21-pr-991-round-3-gpu-re-jack) lists WindowsVerifiedBladeDeviceProvider.cs under touches. Round 3 did not change that file; round 4 does.
- The round-3 test TransitionContinuationGpuResend.cs models a failing re-send as Indeterminate, which covers an uncertain submission but not a refusal. The real GPU-only setter now returns KnownNotApplied, so model refusals as KnownNotApplied or CapabilityUnavailable.
- Perl `s|...|...|` with `\|\|` in the pattern matched the empty string at offset 0 and injected text at the top of the file. Use another delimiter or the Edit tool.
- A perl `-p` substitution that matches `\r$` and replaces the line drops the CR. Renormalize with `perl -pi -e 's/(?<!\r)\n/\r\n/'`.
- FirmwarePerformanceSelectorHidden and latentManualPerformanceIntent both need manual fans desired. With desired Automatic and no reported preset, a GPU-only continuation step is skipped, not sent.
