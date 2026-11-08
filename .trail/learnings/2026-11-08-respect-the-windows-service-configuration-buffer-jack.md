---
title: Respect the Windows service configuration buffer limit for PawnIO
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Platform/PawnIoRuntimeAdmission.cs
  - tests/OpenBlade.Platform.Tests/PawnIoRuntimeAdmissionTests.cs
---

## Decided
A real LocalDev deployment installed official PawnIO 2.2.0 successfully, then product setup rolled back when PawnIoRuntimeAdmission.Inspect passed 16384 bytes to QueryServiceConfigW. Windows returned RPC_X_BAD_STUB_DATA (1783). This API permits at most 8192 bytes: query required size with NULL/0, require ERROR_INSUFFICIENT_BUFFER and a bounded structure-sized result, then read exactly that size and release the allocation deterministically. The shared correction covers setup and the isolated CPU helper on every supported processor/model. Existing other service-configuration callers already use 8192 or the size-probe contract.

## Tried and ruled out
Do not retry installation as a transient workaround for this deterministic API violation. The protected PawnIO journal recorded ExitObserved with exit code 0 and the driver was running; preserve it and let fixed setup reconcile independently verified binding. The broker's later session-agent-relaunch-failed is not the original failure. Read-only inspection reproduced the old 1783 and the corrected inspection admitted the running signed 2.2.0 package without driver I/O.

## Watch out
Passing package tests and mocked P/Invoke tests did not establish the Windows RPC buffer limit. Keep the documented maximum and focused invalid-size/native-error regressions. The existing product rollback remains separate from the successfully installed shared prerequisite. Installer and actual temperature/Stop validation must use the corrected committed payload; an admitted driver alone is not a CPU temperature measurement.
