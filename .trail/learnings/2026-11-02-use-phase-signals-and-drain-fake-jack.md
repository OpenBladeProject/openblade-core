---
title: Use phase signals and drain fake gate operations
author: Jack
date: 2026-11-02
branch: fix/codebase-reliability-audit
touches:
  - tests/OpenBlade.Protocol.Tests/FanRpmQueryRunFactoryTests.cs
---

## Decided
Hosted PR #874 timed out at the final one-second wait in QueryAsync_HoldsSharedFeatureCommandGateThroughEachSetGetResponse after both response barriers and gate-held assertions passed. Preserve the phase assertions and exact RPM result, use one named scheduling watchdog, and always release fake responses, cancel and observe outstanding operations before disposing synchronization state. Apply the same cleanup to the neighboring fan/lighting gate test because it had the same failure path.

## Tried and ruled out
Do not infer a production command-gate bug or a specific scheduler cause from this timeout. Do not change production timeouts or merely rerun CI until green. A failed nonblocking gate probe must return any unexpectedly acquired permit before throwing, so the assertion itself cannot strand cleanup. Preserve the original failure if cleanup also fails.

## Watch out
These tests drive fake sessions only. Their cancellation watchdog is test cleanup and scheduling protection, not hardware authority or a new firmware deadline. Keep both phase barriers, the lighting exclusion check, the result assertions and final gate availability. All 2,636 Protocol tests passed after both fixture changes; the rest of the hosted solution and installer/build-contract checks passed on the preceding head. Claude confirmed the sibling cleanup gap; source review and independent review adjudicate its other suggestions separately.
