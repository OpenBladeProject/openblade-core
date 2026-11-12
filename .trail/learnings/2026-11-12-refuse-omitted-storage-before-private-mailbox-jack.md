---
title: refuse omitted storage before private mailbox fixture resolvers
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
The d8f43b72 history-admission validation was rejected for authority scope: three mailbox Begin rows omitted storage after the production legacy adapter became active. Their passing TRX results do not establish private-only execution. No conclusion about actual product-root contents or absence of probes is justified; preserve the old evidence and do not investigate or clean real roots.

Use explicit Storage(files) in both calls (two theory rows and one fact). Private fixture identity is the retained owner from CreatePrivateFixture, preserved across native custody views. Resolve refuses omitted storage before any product resolver invocation. Both production adapters and Setup selection share that boundary; explicit private dependencies still use the same authentication logic. Metadata-only paths that never resolve storage and explicit legacy callbacks retain their behavior. The product resolver and protected-root policy remain unchanged.

## Tried and ruled out
Do not restore fixture policy bypasses, guess by path names, or weaken incoming-package preflight, owner/reset fences, historical original-build authentication, original exit, or explicit-digest refusal. A passing suite alone cannot prove its dependency scope. Audit newly active adapter callers before executing a fresh harness. Setup's pre-start fixture also supplies synthetic paths and SID to avoid unnecessary identity defaults.

## Watch out
This offline correction permits own SID metadata and ACL/file-sharing operations only on private fixtures. It grants no product roots, process/token/session/SCM/task/pipe/hardware probes or role launches. Rebuild fresh exact-commit harnesses; never rerun the scope-rejected harness. Required-core activation and default managed Windows behavior remain required. Full integrated review must compare dfdda134 through the final head, including C1 incoming composition before mutations, C2 owner/preparation history consistency, and C4 historical signed-run/projection validation.
