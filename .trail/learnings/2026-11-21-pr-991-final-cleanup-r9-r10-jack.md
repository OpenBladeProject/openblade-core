---
title: "PR 991 final cleanup: R9 R10 R7 are reachable, lost ACK blame"
author: Jack
date: 2026-11-21
branch: feat/gpu-offsets-rz09-0529-0530
touches:
  - CHANGELOG.md
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Service/ControlCoordinator.cs
  - tests/OpenBlade.Service.Tests/ExperimentalBladeBatteryIntegrationTests.GpuOnlyRefusal.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.GpuOnlyStepRefusal.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.PendingReleaseGuards.cs
  - tests/OpenBlade.Service.Tests/WindowsVerifiedBladeDeviceProviderProductionTests.PowerChangeGpuResolution.cs
---

PR #991 final cleanup: corrections to the round-4 learning and four low fixes.

## Decided
- `confirmationPendingAfterIndeterminateWrite` in `ApplyPerformanceOutcomeAsync` applies only when the write carried GPU tuning (`effectiveGpuClockTuning is not null`). Relinquish now keeps a submission SubmissionUncertain when the driver can't be read. Before this fix, a later firmware-only preset write whose ACK was lost was blamed on the GPU and latched as Mismatch (probe PROD-C3). It is now judged from the preset alone: "prior performance state remained active", writeAttempted false, nothing pending.
- What a power change does when the driver can't be read: the pending entry and its WritesBlocked are dropped (ChangePowerSourceCore), the tray shows no unconfirmed notice, no offsets are written, and the GPU state stays SubmissionUncertain internally until a read succeeds. The docs (en, ru-RU, zh-CN) and the changelog now say that, instead of "reports/keeps the change unconfirmed".
- The changelog no longer claims that OpenBlade's own GPU changes are never reported as outside changes. When an external-to-external power change reads back partial offsets (core landed, memory not), Performance is latched by design (F8, probe RT-C3).
- The EXP refusal test counts 0D/02 and 0D/01 in `Io.Commands`. `Io.Setters` records only 07/12, so the old firmware assertion was vacuous.

## Tried and ruled out
- The round-4 learning (2026-11-21-pr-991-round-4-refused-gpu-jack) called R7, R9 and R10 equivalent or unreachable. That is wrong. The guards are correct, but each one can be reached:
  - R9 (the DeviceVerified guard in the pending-block release): a power change with no admitted device, followed by an explicit write that loses its ACK and readback, reaches the release with an unverified device, a pending confirmation and no explicit non-power block. Without the guard, the next power change publishes an unverified device with writes unblocked. Pinned by `PowerChange_UnverifiedDeviceWithPending_StaysBlocked`.
  - R10 (release only with pending): conflicting adapter replies give a power-only block with no pending confirmation. Without the gate, the classified change that follows publishes ExternalUnknown with writes unblocked. Pinned by `PowerChange_WithoutPending_KeepsAPowerOnlyBlock`.
  - R7 (the unclassified-context guard) was refuted by reading the code, not by a probe; it was not killed by a test.

## Watch out
- Re-arm one-shot (M6: the flag is never cleared; M7: it is set on every ChangePowerSourceCore call) is pinned by `PowerChange_RearmsOnceAcrossItsContinuation`: one rearm across the power change and its continuation, with two GPU sends.
- F6 (pre-existing on main): after a power-only block clears, the classified destination's thermal step reports "Thermal write authority closed". It is a follow-up, so do not pin that behaviour in a test.
- "Equivalent" claims need a probe that tries to reach the state, not a list of the paths you happened to think of.
