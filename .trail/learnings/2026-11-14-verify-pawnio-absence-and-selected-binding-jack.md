---
title: Verify PawnIO absence and selected binding after Claude review
author: Jack
date: 2026-11-14
branch: fix/issue969-pawnio-admission
touches:
  - src/OpenBlade.Platform/PawnIoRuntimeAdmission.cs
  - tests/OpenBlade.Platform.Tests/PawnIoRuntimeAdmissionTests.cs
---

## Decided
Claude Opus 5.5/high completed success_native_review for PR #970 from 345b45af418ad8b74c6e28151acea2b387ca138e to original head e2091f9c53ba526c9223f07e414be7a425214823. Confirmed and fixed the missing-service branch: disconnected Enum residue without an uninstall registration or present node does not establish a partial install. VerifyNoInstallation shares the same fail-closed NORMAL locator classification as existing-service selection. Journal reconciliation remains before package absence and still prevents uncertain submission replay. Add an injectable inspection sequence proving the selected live instance is the binding checked and its located devnode is the status queried. Binding/status errors and cancellation stop that sequence. The full Platform suite passed 185 tests after these changes.

## Tried and ruled out
Reject Claude's proposal to ignore a second present root devnode merely because it has a problem code: it is a live ambiguous shared-driver binding, not disconnected residue. Do not change DN_STARTED admission, select the first working device, remove vendor registrations or resubmit unknown installer output. One completed Claude review plus confirmed corrections and final verification is the review gate; no automatic second metered review is needed.

## Watch out
The reviewed original head differs from the corrected head; record both in PR evidence. A screenshot of the old cardinality error cannot prove duplicate ghosts or reporter recovery. A missing/untrusted package with real residual registration remains blocked for repair. The local committed native-development build script overrides Cargo's target path, so verify with the existing pinned toolchain and shared C:\build-cache\cargo\openblade-core under its build lock using only a local storage-path override; do not import unrelated storage-workflow edits or copy compiler binaries.
