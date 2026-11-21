---
title: HyperBoost handoff lifetime and unrestorable journals
author: Jack
date: 2026-11-21
branch: feat/rz09-0510-hyperboost
touches:
  - docs/experimental-hyperboost-evidence.md
  - docs/zh-CN/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-20-rz09-0510-hyperboost.json
  - src/OpenBlade.FirmwareSmoke/HyperBoostLifecycleValidation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Service/ThermalTransitionExecutor.cs
  - tests/OpenBlade.FirmwareSmoke.Tests/HyperBoostLifecycleValidationTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalHyperBoostTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.HyperBoostHandoff.cs
---

Corrects the earlier learning "HyperBoost exit is never gated on destination intent", whose Watch out said any other observation clears the handoff. It did not: the handoff outlived the AC return and masked a later outside change.

## Decided
- The HyperBoost handoff (`hyperBoostHandoffPerformance`, plus its Custom levels and power-source generation) covers the transition that recorded it and the next one only. `CompareTransitionAutomaticPerformance` consumes it once the generation has moved on, match or not. `ObserveAutomaticState` clears it on any confirmed state that isn't the handoff, and session activation clears it.
- Raw 6 off battery on 029F/029D decodes as BatteryBalanced, not an error. Synapse writes 6 on battery and sets the AC mode on attach without a read (671 policy 1,398,128 and 1,941,140; 669 1,558,229 and 1,699,212). No AC intent matches it, so the plugged-in profile applies with one typed set. `IsBalancedHandoffReading` treats Balanced and BatteryBalanced as the same handoff.
- Restore-only paths with an unadmitted restore intent retire the journal as a handoff once a fresh read shows HyperBoost off with Automatic fans: firmware pre-exit at the battery boundary, an applied indeterminate exit, a RestorePending journal on observation, and explicit recovery on battery. No write, no replay, saved intent kept. `IsUnadmittedHyperBoostHandoff` takes the observation's fresh capabilities; the published status is still null on the first observation.
- The entry restriction and direct-entry-base checks apply to forward entry steps only (`ThermalTransitionStepRunner.RestoringIntent`), so a restriction that appears mid-entry never blocks the rollback.

## Tried and ruled out
- Using `GetStatus().Capabilities` alone for the observation-time retirement: the first full observation runs before capabilities are published, so a journal present at startup never retired.

## Watch out
- A RestorePending journal with any unadmitted intent now retires on observation, AC included. The old test that expected an unsupported Custom intent to stay pending was changed on purpose.
- An entry rollback restores the entry-time intent, not the pre-entry device state; with Fixed fans outside the desired Automatic, the rollback leaves Automatic.
- The cold-start pause (service starts on battery in Balanced with a non-Balanced AC profile, then AC returns paused) comes from the initial-comparison path, not the handoff exemption. It predates this work.
