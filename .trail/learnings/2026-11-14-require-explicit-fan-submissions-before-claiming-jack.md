---
title: Require explicit fan submissions before claiming ownership
author: Jack
date: 2026-11-14
branch: fix/issue-967-model-admission
touches:
  - CHANGELOG.md
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
  - src/OpenBlade.Service/ProfileSetValidator.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - src/OpenBlade.Tray.Core/ProfileAdmissionPolicy.cs
  - src/OpenBlade.Tray.Core/ProfilePresentationFactory.cs
  - src/OpenBlade.Tray/App.DeveloperHarness.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.DynamicFanRanges.cs
  - tests/OpenBlade.Service.Tests/ProfileSetValidatorTests.cs
---

## Decided
Matching manual fan state is observation, not proof of ownership. Experimental explicit Fixed or Curve entry must perform its freshly admitted target SETs and independent readback even when the requested targets already match. Only the typed curve-maintenance path may complete a matching tuple without a SET; the service admits that path only with owned exact intent, matching authority epoch and desired revision.

## Tried and ruled out
Do not infer a successful physical write from the coordinator's WriteAttempted flag: it is set before invoking a backend, so a backend no-op could previously grant ownership. Do not insert Automatic or change preset to force a write. Keep the shortest existing two-target sequence and fresh per-write safety checks.

## Watch out
The separately validated ProductionBatteryPort already skips only redundant mode writes and still submits both target commands; no production-backend change is needed. Automatic-already-Automatic may remain a no-op. A failed or ambiguous target submission still pauses and reconciles read-only, without claiming complete ownership or replaying.
