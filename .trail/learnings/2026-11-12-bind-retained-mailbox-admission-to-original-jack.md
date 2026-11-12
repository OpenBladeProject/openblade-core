---
title: Bind retained mailbox admission to original history and incoming composition
author: Jack
date: 2026-11-12
branch: fix/mailbox-history-admission
touches:
  - src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs
  - src/OpenBlade.Deployment/ManagedMailboxRecovery.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxHandoff.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxPackage.cs
  - src/OpenBlade.Deployment/WindowsNativeEpoch.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
  - src/OpenBlade.Deployment/WindowsNativeTrustLifecycle.cs
  - src/OpenBlade.Setup/Program.cs
  - src/OpenBlade.Setup/SetupNativePackageTransaction.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs
---

## Decided
Preflight the verified incoming native package before Setup lifecycle mutations. Retained no-selection preparation history admits only read-only mailbox composition; keep Begin's fresh refusal and managed rollback classifications. Preserve exact original managed run/build binding and enrollment projection in immutable run and transfer history. Every leading unreserved terminal reauthenticates original run, binding, signed cache and exact historical projection. Old data upgrades only from positive original bytes matching its binding; unresolved originals remain fenced.

Native custody cross-validates owner references, each exact selection phase/frontier, and every retained predecessor. A first-preparation prefix needs known-empty ownership, a nonempty entirely authenticated provisional generation set, ordinal one and zero predecessor. Owner-only or mixed missing-generation history never becomes bootstrap. The legacy adapters use the same production composition with private storage injected internally for fixtures; pre-stop selection does not reacquire a live enrolled run's lifetime exclusion.

## Tried and ruled out
C3's explicit-digest recovery claim was rejected by independent triage: do not grant native-to-native mailbox authority or bypass original custody. The claimed permanent-removal/no-rollback consequence was rejected too; a managed Begin refusal precedes activation and retirement flags and retains complete managed rollback. Existing owner commitments and terminal bytes remain unchanged. No owner erasure, guessed predecessor, fabricated Retired state or current-compatible-key substitution.

## Watch out
Modern and legacy terminal helpers differ: modern unreserved history needs its own original signed-build projection; legacy v1 continues to require its separately pinned historical exit contract and authenticated native mapping. Fixture snapshots cannot read exclusively held lock files; snapshot other metadata and assert lock exclusion separately. No installed roots, process/token/session/SCM/task/private-pipe probes, role launches or hardware tests are permitted in this offline stage. Required Rust core stays active with hash fc831a619423749a1ecb84c4275c4d374e7131e9a1664a4fad5938b94105baf0. Full Rust replacement and runtime/default cutover remain separate work.
