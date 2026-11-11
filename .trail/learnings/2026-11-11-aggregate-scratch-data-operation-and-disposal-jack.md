---
title: Aggregate scratch data operation and disposal failures at every scope
author: Jack
date: 2026-11-11
branch: fix/rust-diagnostics-validator-correction
touches:
  - src/OpenBlade.DeploymentBroker/ScratchFixtureFailure.cs
  - src/OpenBlade.DeploymentBroker/ScratchLaunchData.cs
  - tests/OpenBlade.Setup.Tests/ScratchLaunchDataOfflineTests.cs
---

## Decided
Preserve the operation and all release failures at every scratch-data scope, not only at the outer retained-data wrapper. Use the existing cleanup collector through an operation scope for traversal entries/pins, acquisition parent and partial re-pin. If acquisition cannot transfer ownership after a parent-release failure, release the acquired data pin once and report its retained path.

## Tried and ruled out
An outer wrapper alone is insufficient: C# implicit using/finally disposal may replace a programming fault with an I/O refusal before classification. Initial elapsed/release regressions failed before traversal-pin acquisition and did not exercise that overlap. An independent review found the remaining path; add after-acquisition operation-plus-disposal tests rather than claiming earlier passing tests covered it.

## Watch out
Preserve exact causes and their entry classification when releases also fail. Test both successful child-pin acquisition followed by parent-release failure and partial acquisition where re-pin reading and release both fail. Keep no-child empty cleanup nonrecursive, retain uncertain data, release every acquired resource once and never retry Finish. No caller-token/deadline redesign or product/native execution is needed.
