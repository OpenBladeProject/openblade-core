---
title: Require fixture contributions caller binding and restore prerequisites
author: Jack
date: 2026-11-14
branch: feat/native-fixture-ci
touches:
  - build/NativeWindowsFixtureManifest.json
  - build/NativeWindowsFixtures.ps1
  - build/Run-AffectedCi.ps1
  - build/native_windows_fixtures.py
  - build/test_native_windows_fixtures.py
  - tests/Build.Tests/CiBuildScope.Tests.ps1
  - tests/Build.Tests/NativeWindowsFixtures.Tests.ps1
---

## Decided
Require a positive classified-frame contribution from every selected frame producer. The reviewed pure retryable-operation classifier explicitly permits zero and supplies a reason; historical counts remain calibration, not fixed expectations. Reject unclassified legacy frame markers while leaving corroborating TRACE copies outside delivery counts. Check receipts against independently supplied resolved caller root and repository during preparation and action checks.

The manifest owns the exact three managed fixture prerequisite projects. Affected Quality restores their deduplicated union with selected projects through normal repository restore semantics before the offline harness; full solution restore already includes them. Keep selected builds unchanged. A fresh fixture invocation below 50 GiB reports that no finished outputs are safely attributable to it, rechecks C, and may continue above the required 25 GiB floor without reclaiming shared or unrelated state.

## Tried and ruled out
All 135 prior records were intact and the nineteen old logs had no legacy mutation markers, so this correction closes a future missing-contribution gap rather than repairing an established frame loss. Existing rust attributes already enforce LF, refuting the CRLF pin-parsing claim. Shared-target interop builds and metadata use the same cache and pin; a wrong-DLL or unique offline failure was not established and did not justify an environment-policy rewrite.

## Watch out
A Contracts-only affected restore does not provision Service-only packages unless the actual shared prerequisite closure is added. Keep the fixture runner offline and exercise this ordering with the real getter and inert command recorder. Cold hosted capacity and total workflow duration remain unmeasured. Preserve prior seals and bind the corrected source to a fresh selected run; neither injected peer frames nor own-SID NTFS setup establishes installed-product or hardware readiness.
