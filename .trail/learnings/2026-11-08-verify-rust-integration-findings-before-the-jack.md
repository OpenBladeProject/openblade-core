---
title: Verify Rust integration findings before the preserved stack roll-up
author: Jack
date: 2026-11-08
touches:
  - build/Publish.ps1
  - build/CiImpactManifest.json
  - rust/Compare-Shadow.ps1
  - src/OpenBlade.Service/ServiceStartupCoordinator.cs
  - src/OpenBlade.Service/ServiceSessionPowerTransitionCoordinator.cs
  - src/OpenBlade.Service/Worker.cs
---

## Decided
The integration review completed on6d622b8d with Claude Opus5.5/high (wrapper8.1.1). Confirmed startup and detached power-queue native failures must reach Worker supervision before bounded cleanup; a fatal latch retires pending notifications and prevents replay. Capture's native distribution carries its provenance and notice alongside the DLL. Packaging shadow comparisons reuse the selected release native payload/configuration and final copies are verified against the fixed pre-compiled manifest. Native runtime source changes select installer validation in affected CI.

## Tried and ruled out
The reported stale embedded-resource cache did not reproduce on .NET SDK10.0.301: release-to-development-to-release builds embedded the correct release native hash, and the SDK compile dependency cache includes resource inputs. Do not add a cache workaround. Native project metadata already selected full installer CI; only runtime-source selection was missing. Redundant test references, caching fresh source-integrity enumeration and broad probe optimization established no current incorrect result and are deferred. Review findings are not automatically facts and later verified fixes are not labeled Claude exact-head reviewed.

## Watch out
All seven refreshed tips retain dependency-order ancestry. Main-targeted #897 is the roll-up merge candidate and must satisfy current-main required build-and-test plus full Windows/native/installer checks. GitHub can mark exact included predecessor tips merged indirectly; never fabricate or copy statuses, and independently confirm every PR's merged state and tip reachability afterward. Existing stage worktrees belong to their original chats; preserve them for owner cleanup. Only the integration chat synchronizes the primary checkout. An HTTP503/504 from the pinned PawnIO asset is an external download failure; preserve all hash/signer pins and rerun only the affected necessary gate when allowed.
