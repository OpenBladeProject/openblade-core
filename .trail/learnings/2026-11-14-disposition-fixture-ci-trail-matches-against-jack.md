---
title: Disposition fixture CI Trail matches against the accepted stage
author: Jack
date: 2026-11-14
branch: feat/native-fixture-ci
touches:
  - ".gitattributes"
  - ".github/workflows/build.yml"
  - build/CiImpactManifest.json
  - build/NativeWindowsFixtureManifest.json
  - build/NativeWindowsFixtures.ps1
  - build/Run-AffectedCi.ps1
  - build/Test-NativeWindowsFixtures.ps1
  - build/native_windows_fixtures.py
  - build/test_native_windows_fixtures.py
  - tests/Build.Tests/CiBuildScope.Tests.ps1
---

## Decided
The exact-base native-fixture CI Trail check reported twenty phrase matches in cache-step names, explicit selector mappings and manifest identifiers. Preserve the patch: it keeps the fixture host under tools, uses one shared Cargo target, retains required non-skipped strict codec consumers, uses fresh actual managed producer bytes, and leaves production admission, ACKs, guardian scheduling and NativeAOT flags untouched. The new requested CI stage explicitly admits wiring that was outside earlier correctness-only corrections.

## Tried and ruled out
A default-branch Trail check included hundreds of inherited accepted migration files. The accepted-stage-base check is the relevant review scope. Its lexical findings include positive requirements and the new stage learning itself; removing required fixture routing or changing frozen codec semantics would not address a real conflict.

## Watch out
Keep the exact-base check output and per-finding dispositions in compact stage evidence. Acknowledging lexical matches does not waive future semantic findings, exact source binding or mandatory independent review.
