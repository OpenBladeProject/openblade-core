---
title: Keep fatal native recovery and partial startup cleanup consistent
author: Jack
date: 2026-11-08
touches:
  - src/OpenBlade.Service/Worker.cs
  - src/OpenBlade.Service/ServiceStartupCoordinator.cs
  - src/OpenBlade.Service/ServiceSessionPowerTransitionCoordinator.cs
  - src/OpenBlade.Service/FanCurveControl.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
---

## Decided
A required native policy failure is fatal across every reached service caller, including startup, detached power notifications, fan-curve maintenance, observation, thermal convergence and bounded lifecycle/renderer recovery. Exclude NativeComponentException from ordinary recovery catches; retain software preference rollback but never issue a restoration fallback or wrap the fatal error in AggregateException. Use the existing Worker task group for the power queue's independent failure signal, permanently retire pending notifications, and keep ordinary I/O recovery intact.

Startup can acquire Driver mode after runtime initialization but before the complete startup coordinator returns. Track runtime initialization separately from background-start success so a later fatal reconciliation error still reaches existing guarded bounded cleanup. Refused or uninitialized startup remains ineligible; unexpected-failure run markers remain for recovery.

## Tried and ruled out
Simply excluding the power queue's catch leaves a fire-and-forget fault unobserved and callers stranded. Keeping startupCompleted as the cleanup gate skips cleanup after ownership has already been established. Do not dispose the process-wide native singleton to synthesize tests, add a generic fatal broker, change firmware admission or replay pending work.

## Watch out
Trace all native policy callers to their catches rather than stopping at the reported startup and power cases. Tests cover the production cleanup guard and real runtime observation/convergence with typed fake failures; these are software checks, not on-device restoration evidence. Preserve fresh device/controller/readback admission and durable uncertainty handling during cleanup.
