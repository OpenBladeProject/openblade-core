---
title: Qualify retained history correction Trail matches
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
The exact dfdda134 correction check reported twenty lexical phrase matches. Each disposition below preserves the cited invariant. The check exit remains one; this is not a clean tool pass.

1. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:109: No rollback flags or runner recovery branch changed in custody. Managed restoration remains available before native activation/retirement.
2. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:109: Each historical frontier is compared with its exact phase bytes; no phase or command is replayed.
3. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:110: No NativeAOT compiler settings or linker suffix changed; these are managed evidence paths.
4. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:110: Exact historical phase evidence preserves committed readiness rather than checking only the current phase.
5. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:121: Owner references must resolve to exact retained retired generations; no owner erasure or fabricated retirement.
6. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:121: No native compiler path changed; this is a managed owner/history admission check.
7. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:121: Existing RequireDrained and positive receipt/exit contracts remain necessary; generation presence is not transport retirement.
8. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:121: The new check refuses retained owner/mapping history whose original native generation is missing; no reset or relocation.
9. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:121: No snapshot is overwritten. All added original build records use immutable exact-or-publish and bind the same original run.
10. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:121: Existing directory sharing contracts are unchanged; new phase-file checks do not change directory leases.
11. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:121: Duplicate learning of the prior sharing rule; no directory sharing contract changed.
12. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:121: AcquireRetiredOwner still acquires the original owner writer and validates its complete chain. Missing owner writer never becomes empty.
13. src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs:122: First-preparation admission requires positively known-empty ownership and complete original generations; no history reset.
14. src/OpenBlade.Deployment/ManagedMailboxRecovery.cs:80: RequireOriginal remains first in ResolveOne and missing receipt/exit remains a refusal. Added history does not infer process exit.
15. src/OpenBlade.Deployment/ManagedMailboxRecovery.cs:227: Exact old origin/transfer bytes remain immutable; new records add signed-build provenance and never replay a child command.
16. src/OpenBlade.Deployment/ManagedMailboxRecovery.cs:259: New original build records are protected metadata bound to the same run; no lease is converted to retirement or path-only deletion.
17. src/OpenBlade.Deployment/ManagedMailboxRecovery.cs:260: No NativeAOT compiler/linker path changed; only protected managed record names were added.
18. src/OpenBlade.Deployment/ManagedMailboxRecovery.cs:260: Original file reads use existing protected OpenFile and no new ancestor sharing policy.
19. src/OpenBlade.Deployment/ManagedMailboxRecovery.cs:260: Duplicate of the same sharing rule; no lease-sharing change.
20. src/OpenBlade.Deployment/ManagedMailboxRecovery.cs:274: Native writer, owner and generation admission precede historical package authentication. Exact original projection never bypasses unknown root/owner state.

## Tried and ruled out
No cited invariant was waived to silence a phrase match. Correct managed rollback, original receipt/exit, retained owner custody, immutable exact history, and directory sharing remain intact.

## Watch out
The added historical-projection evidence is separate from current trust admission and never grants mailbox native-to-native recovery. Compilation, private filesystem tests and static bytes are separate evidence from actual process or hardware validation.
