---
title: Keep native history trust independent and additive history predecessor-compatible
author: Jack
date: 2026-11-12
branch: fix/history-trust-compatibility
touches:
  - src/OpenBlade.Deployment/ManagedMailboxRecovery.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxHandoff.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxPackage.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs
  - src/OpenBlade.Deployment/ManagedMailboxHistory.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxTrustCompatibilityTests.cs
  - tests/OpenBlade.Deployment.Tests/PinnedPredecessorMailboxRecovery.cs
  - tests/OpenBlade.Setup.Tests/SetupHistoryCompatibilityTests.cs
---

## Decided
Reserved recovery retains the native-authenticated enrollment projection as an independent trust anchor. Historical managed projection bytes must match it exactly before cache admission or new publication. Unreserved history still uses its exact retained original projection after enrollment rotation/removal; missing originals remain preserved and fenced.

New managed history uses bounded flat mailbox-history-run-{run}.bin and mailbox-history-transfer-{request}.bin records. The complete dfdda134 reader scans transfer-*.bin and a closed handoff-*.bin suffix whitelist, so it ignores the additive namespace. The bootstrap closed namespace applies before the completion marker, before any run exists. New records bind exact run/binding/projection and transfer selection. Run and binding publish before their additive record and before current selection. Existing run-history and handoff-*-history candidate bytes stay in place, authenticate, and must agree with additive counterparts. Pre-existing incompatible candidate sidecars are not made healthy by rename/deletion.

Default cached enrollment acquisition checks the positive retained files.IsPrivateFixture identity before selecting or invoking the product resolver. Explicit private callbacks and original historical projections remain usable; production absent-root custody is unchanged.

## Tried and ruled out
Replacing native trust with a coherent managed binding/history/cache substitution loses the independent anchor. Publishing new handoff-*-history.bin poisons a healthy predecessor's closed whitelist before an ordinary managed rollback. Full pinned predecessor logic is compiled as ordinary private unit code only; no old product role is launched. Do not guess current trust for unrecoverable originals or classify null-files production custody as private.

## Watch out
Keep original exit, receipt, native retirement, owner-chain and ambiguity checks. Ordinary managed rollback remains available; injected Setup ordering must restore payload before invoking the pinned predecessor reader. Private signed/static tests and cached compilation are separate from runtime installation and hardware validation. Prior accepted 312 fresh rows showed no actual out-of-scope fallback and remain accepted.
