---
title: Require managed predecessors for retained reserved proof
author: Jack
date: 2026-11-12
branch: fix/reserved-proof-binding
touches:
  - src/OpenBlade.Deployment/ManagedMailboxRetainedRecords.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxRetainedStructureTests.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxReservedProofBindingTests.cs
---

## Decided
A present retained managed native-proof with an explicit reserved epoch requires its managed epoch-intent and exact mapping in the common RetainedRecords constructor before Selected may repair deterministic projections/current. ResolveOne publishes those predecessors before either reserved proof disposition. Keep later actual original/cache/signature/native custody authentication unchanged.

## Tried and ruled out
Requiring bindings only for terminal candidates leaves proof-only corrupted snapshots repairable. A canceled-before-native-use disposition may still be reserved; use the parsed epoch discriminator. Do not enforce managed-v2 sidecars in the shared resolution codec because legacy and not-reserved contracts differ.

## Watch out
A genuine reserved proof-only cut retains both bindings. Genuine authenticated preparation-before-binding and intent-before-mapping cuts have no managed proof yet. Test both reserved dispositions with missing intent, missing mapping and both absent before any terminal candidate; assert protected namespaces unchanged. Reuse zero-transfer scale evidence only after exact method/helper/route equality and proof-branch unreachability are established; label it parent-reused.
