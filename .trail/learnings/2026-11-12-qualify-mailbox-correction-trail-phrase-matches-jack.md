---
title: Qualify mailbox correction Trail phrase matches without weakening fences
author: Jack
date: 2026-11-12
branch: fix/mailbox-renewal-race
touches:
  - src/OpenBlade.Deployment/ManagedMailboxLegacyRecovery.cs
  - src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs
  - src/OpenBlade.Deployment/ManagedMailboxRecovery.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxHandoff.cs
  - src/OpenBlade.Deployment/WindowsNativeAttemptHistory.cs
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs
  - tests/OpenBlade.Deployment.Tests/OpenBladeSignedReleaseVerifierTests.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeInventoryBurnExtractorTests.cs
---

## Decided

The correction-base Trail check for mailbox renewal reported twenty phrase matches. The exact delta is C1/C3 mailbox custody/publication plus private-filesystem regressions. Keep the checks and document the dispositions rather than changing safety rules to silence lexical matches.

## Tried and ruled out

The default-base check includes 491 inherited migration files and a 400 KB cap; it is not the twelve-source-file correction delta from 39617e4091aab27ebec309201a74128d0a59934f. The explicit correction-base check is retained separately.

Correction-base matches 1/3/8 concern exact recovery bytes: the new envelope binds only the same origin/transfer; no phase/frontier or child command is replayed. Match 2 concerns previously tested Fan Stop service bytes; those sources are unchanged. Matches 4/5/11/20 concern stop versus drain: exact original receipt/exit and independent native attempt/owner proof remain required. Matches 6/14/18 concern exclusion: startup and enrollment mutation still retain it; pre-stop Setup only authenticates existing immutable terminal bytes under metadata writer and does not mutate an enrollment. Match 7 concerns sharing/ACL/path-only admission: access primitives are unchanged and admission binds signed package, exact original transfer and custody. Match 9 preserves the independent owner fence. Matches 10/13 repeat this stage's warning about latest-only or memory-only cancellation; the implementation now authenticates all retained predecessors under fresh custody. Match 12 concerns path deletion or treating a lease as retirement: neither occurs. Match 15 concerns an empty-attempt-only inference: the signed startup policy, original receipt/exit, exact package/mapping/reservation, fresh original custody and owner fence are all required in addition to no submission/frontier/attempt. Match 16 concerns NativeAOT linker path length; no native compiler path changed. Match 17 concerns safe managed rollback; the actual Setup cleanup-await regression retains safe restoration before native activation. Match 19 concerns no-launch proof: the existing admitted read-only mailbox signed startup-custody recipe is preserved, and unknown legacy launchless artifacts remain fenced.

## Watch out

These are dispositions of phrase matches, not permission to weaken the cited invariants. Preserve the complete check JSON, source hashes, failed intermediates and exact-final independent review. A check exit code of one must be reported with its evidence; never present it as a clean pass.
