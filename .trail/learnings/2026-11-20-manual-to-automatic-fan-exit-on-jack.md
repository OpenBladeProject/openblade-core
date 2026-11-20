---
title: manual-to-Automatic fan exit on preset-reporting backends
author: Jack
date: 2026-11-20
branch: fix/manual-to-automatic-fan-restore
touches:
  - "src/OpenBlade.Service/ControlCoordinator.cs#FanExitRestoresPerformance"
  - "src/OpenBlade.Service/ControlCoordinator.cs#FanControlTransitionComponents"
  - "src/OpenBlade.Service/ServiceControlRuntime.cs#SetFanControlOverrideCoreAsync"
  - "src/OpenBlade.Service/ThermalTransitionExecutor.cs#EnsureDeclared"
---

## Decided

Leaving manual fans (Fixed, ManualCurve, Maximum, Stop) for Automatic restores the saved performance intent only on backends whose shared manual selector hides the preset (`ReportsPerformanceWithManualFans == false`). Backends that report the real preset under manual fans keep it: the experimental Automatic handback rewrites only the 0D/02 fan flag with the confirmed mode byte and independently confirms the preset by readback, so a Performance restoration step there is a redundant write that can also overwrite an external preset change. `ControlCoordinator.FanExitRestoresPerformance` and `FanEntryRequiresAutomaticBridge` are the single source for both the coordinator's Performance steps and the declared thermal components (`FanControlTransitionComponents`), so they cannot drift again. All 20 source-reviewed experimental profiles with a fan range report performance under manual fans and benefit; RZ09-0581 and RZ09-0528 keep their existing restoring paths.

## Tried and ruled out

Declaring Performance for every manual-to-Automatic exit so the coordinator's restoration step is admitted. It keeps the redundant step, subjects a safety-direction fan handback to Performance admission, and can fight an external preset change, contrary to Direct transitions and rule 5. Catching `InvalidOperationException` at `ControlRequestHandler` was also rejected: the throw happens after the Automatic fan write was submitted, so turning it into an ordinary failed result would keep the service running on a stale published state without the uncertain-submission reconciliation.

## Watch out

`ThermalTransitionExecutor.EnsureDeclared` throws `InvalidOperationException` and nothing on the request path catches it, so any undeclared thermal step stops the whole service. The performance-override resolver still evaluates `RequiresPresetRangedFanRestore` against `GetStatus().ConfirmedState` while the coordinator uses a fresh read; an external change between the two could still run an undeclared Fans step. Coordinator unit tests run without a thermal step runner and never hit `EnsureDeclared`; reproduce declaration bugs through `ServiceControlRuntime` with the exact `ExperimentalBladePort.CreateCapabilities`.
