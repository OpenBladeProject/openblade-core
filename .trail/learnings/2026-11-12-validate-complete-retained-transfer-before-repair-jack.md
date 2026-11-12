---
title: Validate the complete retained transfer before projection repair
author: Jack
date: 2026-11-12
branch: fix/mailbox-retained-structure
touches:
  - src/OpenBlade.Deployment/ManagedMailboxRetainedRecords.cs
  - src/OpenBlade.Deployment/ManagedMailboxRecovery.cs
  - src/OpenBlade.Deployment/ManagedMailboxHistory.cs
  - src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxRetainedStructureTests.cs
---

## Decided

Read one bounded retained-record view per transfer before repairing selection/request/run projections or transfer-current. Share its pure envelope, commitment and relationship checks with recovery and original-build authentication. Referenced original run/binding records precede transfer publication; missing later receipt, exit, mapping or terminal records instead depend on the actual publication stage. Inspect referenced legacy-only history as well as additive history. Mutable current.bin may name a later run.

Keep signed package verification, current enrollment acquisition, actual original-process observation and independent native generation/reservation/owner/drain custody in their authenticated phases. A valid managed snapshot grants none of that authority. Fresh native preparation can still recover absent managed epoch-intent/mapping; a present contradictory record cannot. Repair remains individually exact-or-publish, not a multi-file transaction.

## Tried and ruled out

Checking terminal candidates alone leaves sibling receipt, epoch, proof and original-history contradictions until after repair. Requiring every eventual record rejects genuine cuts. Scanning unrelated legacy/cache roots or caching all historical run payloads changes the contract and bounds unnecessarily.

## Watch out

The former 8192 cap counted combined additive namespace entries. A synthetic ValidateEntries iterator does not exercise the filesystem wrapper. The regression test seeds 8193 typed run/binding/history triplets using one signed fixture, starts one new private run, then performs fresh Selected admission over 8194 real runs. Compare exact names and regenerate expected bytes one record at a time. For missing completed mapping, recompute the terminal commitment so only the causal requirement discriminates; remove additive history when testing referenced-original admission.
