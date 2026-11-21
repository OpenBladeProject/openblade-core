---
title: manual-fan exit restore on backends without direct transitions
author: Jack
date: 2026-11-20
branch: fix/manual-to-automatic-fan-restore
touches:
  - "src/OpenBlade.Service/ControlCoordinator.cs#DefersPerformanceUnderManualFans"
  - "src/OpenBlade.Service/ControlCoordinator.cs#FanExitRestoresPerformance"
  - "src/OpenBlade.Service/ServiceControlRuntime.cs#DeferredPerformanceIntentMessage"
---

## Decided

This corrects the Nov 20 learning "manual-to-Automatic fan exit on preset-reporting backends". It was wrong to say the exit restore is redundant on every backend that reports performance under manual fans. A backend without direct manual transitions (`SupportsDirectManualFanTransitions == false`) blocks performance writes while fans are manual ("Performance changes require Automatic fan mode."), so a preset chosen there is only stored, and the exit restore is the only step that applies it. That covers RZ09-0300, 0328, 0351, 0421, 0423, 0427, 0484, 0508, 0510, 0509, 0530 and 0529. Only 029D and 029F (reporting and direct) skip the restore. `ControlCoordinator.DefersPerformanceUnderManualFans` (`capabilities is not { ReportsPerformanceWithManualFans: true, SupportsDirectManualFanTransitions: true }`) is the one predicate behind the exit restore, its declaration and the runtime's deferral message. Null capabilities defer, so the declaration stays a superset.

Rule 5 on the exit: when the drift tracker has latched `ExternalChanges` for Performance on a reporting backend, the runtime passes `performanceChangedExternally` and the coordinator writes only the fans and keeps the external preset. A shared selector hides the preset under manual fans, so drift there is no evidence and never skips the restore. The declaration ignores this gate, which only narrows it.

## Tried and ruled out

Keeping the skip for every reporting backend (Nov 20) left a preset chosen under manual fans unapplied on 0484 and on the other 11 models. Reading the deferral decision from the staged `ServiceStatusSnapshot.DesiredState` also failed: right after the override is stored it can still hold the previous preset, so a Custom request that first returns fans to Automatic was wrongly deferred. The performance override path now builds the requested desired state from the override itself.

## Watch out

On these backends, AC runtime race tests for a performance request under cached manual fans never reach the coordinator, because the request is deferred first. To get a stale cache that the coordinator still sees, observe the fans back in Automatic externally while the Fixed intent stays. The matrix test enumerates `ExperimentalBladePort.CreateCapabilities` and finds 23 model-preset pairs, excluding Balanced, Custom, BatterySaver and BatteryBalanced. Of those, six have the Balanced fixed target outside the preset range and two have no preset fan range. All are applied on exit.
