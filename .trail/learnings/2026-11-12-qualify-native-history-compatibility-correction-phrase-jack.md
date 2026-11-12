---
title: Qualify native history compatibility correction phrase matches
author: Jack
date: 2026-11-12
branch: fix/history-trust-compatibility
touches:
  - src/OpenBlade.Deployment/ManagedMailboxHistory.cs
  - src/OpenBlade.Deployment/ManagedMailboxRecovery.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxHandoff.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxPackage.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxTrustCompatibilityTests.cs
  - tests/OpenBlade.Deployment.Tests/PinnedPredecessorMailboxRecovery.cs
  - tests/OpenBlade.Setup.Tests/SetupHistoryCompatibilityTests.cs
---

## Decided
The d76fd93e-based Trail check was run after staging this complete correction and returned twenty lexical matches, not a tool pass. The unscoped check covered 492 ancestor files and truncated at 400KB; it is retained but is not this correction's check. All twenty scoped matches preserve the cited contracts, with dispositions below.

1-2. ManagedMailboxHistory.cs:3: the new namespace validates the exact original run/binding/projection and transfer commitment. Reserved Authenticate independently compares native projection before cache authentication/publication; coherent managed replacement cannot overwrite it.
3. ManagedMailboxHistory.cs:4: no directory-sharing primitive, restricted-token policy, role launch or READY-line contract changed.
4-7. ManagedMailboxHistory.cs:5: existing owner/terminal/recovery bytes stay immutable, no Directory.Exists call is added, and run identity is positively bound to exact bytes. Private fixture names grant no authority.
8-10. ManagedMailboxHistory.cs:6,10: no native compiler/linker path changes. IOException and InvalidDataException remain distinct; malformed private data is tested with the existing explicit refusal predicate.
11. ManagedMailboxHistory.cs:10: history filenames are checked against exact original run and binding bytes and transfer selection; names alone never authorize adoption.
12. ManagedMailboxRecovery.cs:31: the changed history branch routes new publication away from handoff-*.bin. The old spelling is retained only for legacy candidate reading, equality and refusal, never new legacy publication.
13. ManagedMailboxRecovery.cs:265: exact-or-publish preserves existing history and adds original projection evidence; no rollback, receipt/exit, custody or sharing invariant changes.
14. ManagedMailboxRecovery.cs:290: the new native/historical equality check strengthens trust; it never infers historical drain, stop or process exit.
15. WindowsManagedMailboxHandoff.cs:214: run/binding bytes precede additive history before current-run selection; the native anchor still remains independent at reserved recovery.
16-20. WindowsManagedMailboxPackage.cs:200-201: default resolver selection follows positive files.IsPrivateFixture refusal. Explicit original projections remain available and production null-files custody is unchanged. No generic exception substitution, path-name fixture identity or unsupported original-build fallback is added.

## Tried and ruled out
No invariant was waived to satisfy lexical phrase matching. The scoped check exit is one, retained with every diagnostic and explained in the correction commit. The accepted prior 312 fresh substantive cases retain their original classification; no product-root fallback was demonstrated there.

## Watch out
Keep source checks, private assertion bodies, held compiler inputs and physical/runtime proof separate. Changes here require complete source binding against all94 prior inputs plus additions, an exact-final independent integrated review and private-only checks; the Root-owned final Claude review is a separate pending gate.
