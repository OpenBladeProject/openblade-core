---
title: Preserve retained-data fault classification and validate fixture-only graphs
author: Jack
date: 2026-11-11
branch: fix/rust-diagnostics-validator-correction
touches:
  - src/OpenBlade.DeploymentBroker/ScratchFixtureFailure.cs
  - src/OpenBlade.DeploymentBroker/ScratchLaunchData.cs
  - tests/Build.Tests/FixtureGraphIsolation.Tests.ps1
  - tests/OpenBlade.Setup.Tests/ScratchLaunchDataOfflineTests.cs
  - tests/Build.Tests/FixtureGraphIsolation.Regression.Tests.ps1
  - tests/Build.Tests/FixtureGraphIsolation.ps1
---

## Decided
Retained launch-data diagnostics use a dedicated IOException subtype whose refusal classification recursively follows its cause. Every acquisition/Finish wrapping site uses it; ordinary typed I/O/input wrappers retain their existing contract. Finish collects operation and top-level lease-release failures before wrapping, preserving original errors and releasing every acquired top-level pin. Uncertain retirement still retains data without a retry.

Fixture graph validation always checks cross-variant identities, including conditional projects without normal rows. Root-qualified observations are retained, and repeated project observations may collapse only after all eight captured fields agree. External evidence roots reject normalized case-insensitive equality and descendants before any write.

## Tried and ruled out
Do not globally reject IOException/InvalidDataException inner causes: deliberate input/OS wrappers may be controlled refusals. Do not require caller cancellation tokens/shared deadlines for the existing synchronous cleanup. Current relocated output consumers and apostrophe quoting remain supported; do not restore PID-wrapper waiting or undo graph isolation. Prior snapshot overwrite was a recording weakness, not proof of an observed differing-context collision.

## Watch out
Cleanup's default two seconds, 1024 entries and depth 16 remain best-effort checks between I/O calls, not preemption. Keep actual entry classification, retained paths, original causes and resource releases in focused injected/private-FS tests. Validate fixture-only collisions through the real validator and root equality through the actual entry guard in a private replica. No product/native fixture child, hardware or installed action is needed.
