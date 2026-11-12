---
title: qualify private fixture isolation learning matches
author: Jack
date: 2026-11-12
branch: fix/history-fixture-isolation
touches:
  - src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs
  - src/OpenBlade.Deployment/ManagedMailboxStorage.cs
  - src/OpenBlade.Deployment/WindowsManagedMailboxHandoff.cs
  - src/OpenBlade.Deployment/WindowsNativeEpochFiles.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs
  - tests/OpenBlade.Setup.Tests/SetupNativePackageTransactionTests.cs
---

## Decided
Reviewed all 16 trail check conflict matches for fixture isolation. These are lexical phrase matches, not evidence of reinstating a rejected design.

1. src/OpenBlade.Deployment/ManagedMailboxStorage.cs:9 — brings back fixture path name, which Jack's learning ruled out. Comment explicitly rejects path-name inference. Resolve checks retained private owner before product resolver; no role launch, owner reset, policy bypass or product-root change.

2. src/OpenBlade.Deployment/ManagedMailboxStorage.cs:9 — brings back managed retained owner, which Jack's learning ruled out. Comment explicitly rejects path-name inference. Resolve checks retained private owner before product resolver; no role launch, owner reset, policy bypass or product-root change.

3. src/OpenBlade.Deployment/ManagedMailboxStorage.cs:9 — brings back name product path, which Jack's learning ruled out. Comment explicitly rejects path-name inference. Resolve checks retained private owner before product resolver; no role launch, owner reset, policy bypass or product-root change.

4. tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs:270 — brings back native transaction managed, which Jack's learning ruled out. Tests require omission refusal, zero product resolver calls, unchanged private history and explicit storage in the three original rows. They do not change rollback, lifetime exclusion, invalid-data wrapping, signatures/keys, selection retirement, incoming composition or terminal proof.

5. tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs:538 — brings back fixture incom preflight, which Jack's learning ruled out. Tests require omission refusal, zero product resolver calls, unchanged private history and explicit storage in the three original rows. They do not change rollback, lifetime exclusion, invalid-data wrapping, signatures/keys, selection retirement, incoming composition or terminal proof.

6. tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs:538 — brings back test managed retain, which Jack's learning ruled out. Tests require omission refusal, zero product resolver calls, unchanged private history and explicit storage in the three original rows. They do not change rollback, lifetime exclusion, invalid-data wrapping, signatures/keys, selection retirement, incoming composition or terminal proof.

7. tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs:540 — brings back must retain exclusion, which Jack's learning ruled out. Tests require omission refusal, zero product resolver calls, unchanged private history and explicit storage in the three original rows. They do not change rollback, lifetime exclusion, invalid-data wrapping, signatures/keys, selection retirement, incoming composition or terminal proof.

8. tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs:540 — brings back managed custody exact, which Jack's learning ruled out. Tests require omission refusal, zero product resolver calls, unchanged private history and explicit storage in the three original rows. They do not change rollback, lifetime exclusion, invalid-data wrapping, signatures/keys, selection retirement, incoming composition or terminal proof.

9. tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs:543 — brings back reject exception invalid, which Jack's learning ruled out. Tests require omission refusal, zero product resolver calls, unchanged private history and explicit storage in the three original rows. They do not change rollback, lifetime exclusion, invalid-data wrapping, signatures/keys, selection retirement, incoming composition or terminal proof.

10. tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs:544 — brings back reject key invalid, which Jack's learning ruled out. Tests require omission refusal, zero product resolver calls, unchanged private history and explicit storage in the three original rows. They do not change rollback, lifetime exclusion, invalid-data wrapping, signatures/keys, selection retirement, incoming composition or terminal proof.

11. tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs:545 — brings back key explicit selection, which Codex's learning ruled out. Tests require omission refusal, zero product resolver calls, unchanged private history and explicit storage in the three original rows. They do not change rollback, lifetime exclusion, invalid-data wrapping, signatures/keys, selection retirement, incoming composition or terminal proof.

12. tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs:555 — brings back reject incom selection, which Jack's learning ruled out. Tests require omission refusal, zero product resolver calls, unchanged private history and explicit storage in the three original rows. They do not change rollback, lifetime exclusion, invalid-data wrapping, signatures/keys, selection retirement, incoming composition or terminal proof.

13. tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs:555 — brings back legacy setup reject, which Jack's learning ruled out. Tests require omission refusal, zero product resolver calls, unchanged private history and explicit storage in the three original rows. They do not change rollback, lifetime exclusion, invalid-data wrapping, signatures/keys, selection retirement, incoming composition or terminal proof.

14. tests/OpenBlade.Setup.Tests/SetupNativePackageTransactionTests.cs:345 — brings back setup path sid, which Jack's learning ruled out. Only a previously omitted fixture path/SID is made explicit; synthetic paths are never probed by its inert file adapter. No linker/rollback/lifecycle behavior changes.

15. tests/OpenBlade.Setup.Tests/SetupNativePackageTransactionTests.cs:345 — brings back native window path, which Codex's learning ruled out. Only a previously omitted fixture path/SID is made explicit; synthetic paths are never probed by its inert file adapter. No linker/rollback/lifecycle behavior changes.

16. tests/OpenBlade.Setup.Tests/SetupNativePackageTransactionTests.cs:346 — brings back test file service, which Jack's learning ruled out. Only a previously omitted fixture path/SID is made explicit; synthetic paths are never probed by its inert file adapter. No linker/rollback/lifecycle behavior changes.

## Tried and ruled out
Do not bypass history authentication, launch product roles, infer fixture identity from names, or reject all metadata-only inspection.

## Watch out
The d8f43b72 test evidence remains authority-scope rejected. New execution must follow the explicit dependency audit and exact-source freeze. Existing learnings are preserved.
