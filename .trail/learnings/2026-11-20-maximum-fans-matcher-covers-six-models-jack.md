---
title: Maximum Fans matcher covers six models; HyperBoost refusal handling
author: Jack
date: 2026-11-20
branch: feat/rz09-0482-maximum-fans
touches:
  - CHANGELOG.md
  - docs/experimental-blade-reference.md
  - evidence/source-reviewed-blades/2026-11-19-rz09-0482-maximum-fans.json
  - src/OpenBlade.Protocol/BladeWriteException.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.HyperBoost.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBlade14HyperBoostTests.cs
  - tests/OpenBlade.Service.Tests/ControlCoordinatorTests.cs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.MaximumFans.cs
---

## Decided
- The shared Custom matcher fix in ControlCoordinator (compare confirmed MaximumFans when capabilities.CustomPerformance.SupportsMaximumFans) applies to six profiles, not three: 029F, 029D, 02A0 and the 2024 models 02B6, 02B7, 02B8. Name all six in the changelog, PR and docs whenever that matcher changes.
- Startup reconciliation (preserveObservedExternalChanges: false) writes the flag in both directions: a saved Custom profile with Maximum Fans on turns fans to maximum at start, and a Synapse-set flag is cleared when the profile has it off.
- HyperBoost exit: a Maximum Fans restore refused KnownNotApplied before submission, with a fresh read confirming Custom/Automatic and matching levels, retires the journal and keeps writes open (message "HyperBoost is off. Maximum Fans was not turned back on; reapply Custom to restore it."). A possibly submitted write still goes RestorePending and blocks writes.
- HyperBoost entry: every KnownNotApplied stop after the confirmed 07/0F cleanup carries ExperimentalBladePort.MaximumFansClearedForHyperBoostNotice and BladeWriteException.MaximumFansClearedBeforeFailure; the service marks confirmed MaximumFans false and records it as its own change.

## Tried and ruled out
- Matching on the exception message in the service: fragile. Use the internal MaximumFansClearedBeforeFailure flag; KnownNotApplied exceptions pass through WindowsVerifiedBladeDeviceProvider unchanged.

## Watch out
- Leaving the confirmed flag stale after the entry stop makes the next observation report externalChanges=[Performance].
- The exit relaxation requires GpuClockTuning null and Automatic fan intent; widen it only with a test for the extra restore step.
