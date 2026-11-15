---
title: Bind installer exit waits to SCM identity and separate recovery phases
author: Jack
date: 2026-11-15
branch: fix/setup-service-process-exit
touches:
  - src/OpenBlade.Setup/SetupCommandRunner.cs
  - src/OpenBlade.Setup/SetupPayloadTransaction.cs
  - src/OpenBlade.Setup/SetupProcessOperations.cs
  - src/OpenBlade.Setup/SetupServiceControl.cs
  - src/OpenBlade.Setup/SetupServiceQuiescence.cs
  - tests/OpenBlade.Setup.Tests/SetupCommandRunnerTests.cs
  - tests/OpenBlade.Setup.Tests/SetupGuardianQuiescenceGuardTests.cs
  - tests/OpenBlade.Setup.Tests/SetupPayloadTransactionTests.cs
  - tests/OpenBlade.Setup.Tests/SetupServiceQuiescenceTests.cs
  - tests/OpenBlade.Setup.Tests/WindowsSetupPlatformServiceTests.cs
---

## Decided
Installer quiescence must bind SCM's running PID even when process-name enumeration was empty, verify the exact installed image through a retained least-privilege handle, and freshly recheck SCM state/PID before stop. Image inspection and exit waiting stay attached to that process object. Use QUERY_LIMITED_INFORMATION plus SYNCHRONIZE; the asynchronous wait owns a duplicate synchronization handle. Missing, changed or ambiguous running identity fails before stop. Unknown live candidates are never silently treated as foreign.

Recovery must distinguish process/guardian quiescence from registration deletion. After quiescence, payload rollback precedes deletion of a service absent from the prior lifecycle. Before restoring old lifecycle/ownership, require the transaction to no longer be activated; a restored tree with cleanup failure is different from unresolved payload restoration. Include immutable active/staging/backup/failed paths and transaction state when explicit repair is required.

## Tried and ruled out
The Claude review candidates were checked against exact source and deterministic regressions. Skipping uninspectable live processes, forcing termination, restoring guessed registrations against an unresolved tree, and extending waits without identity proof are unsafe substitutes. A shared bounded deadline and waits for lingering installed processes remain intentional. Fresh SCM identity reads cannot be removed as redundant. Method-shape suggestions alone did not establish defects.

## Watch out
SCM STOPPED does not establish process exit, and an earlier process-name snapshot does not establish the running SCM process. Never reopen a numeric PID after retaining its lifetime. Preserve original and recovery errors. Successful old-tree restoration with failed cleanup still permits lifecycle restoration; unknown payload state does not. Fake/inert-kernel-object tests establish software ordering and handle contracts, not physical hardware recovery.
