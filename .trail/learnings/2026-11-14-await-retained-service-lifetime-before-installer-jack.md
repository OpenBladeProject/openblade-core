---
title: Await retained service lifetime before installer payload moves
author: Jack
date: 2026-11-14
branch: fix/setup-service-process-exit
touches:
  - Directory.Build.props
  - src/OpenBlade.Setup/SetupCommandRunner.cs
  - src/OpenBlade.Setup/SetupProcessOperations.cs
  - src/OpenBlade.Setup/WindowsSetupPlatform.cs
  - tests/OpenBlade.Setup.Tests/SetupCommandRunnerTests.cs
  - tests/OpenBlade.Setup.Tests/SetupGuardianQuiescenceGuardTests.cs
  - tests/OpenBlade.Setup.Tests/SetupPayloadTransactionTests.cs
  - tests/OpenBlade.Setup.Tests/WindowsSetupPlatformServiceTests.cs
  - tests/OpenBlade.Setup.Tests/WindowsSetupPlatformTestFactory.cs
  - src/OpenBlade.Setup/SetupServiceQuiescence.cs
---

## Decided
SCM STOPPED is not proof that the installed service process has exited or released the payload directory. Shared Setup quiescence retains native process lifetimes before stop, verifies the exact installed service executable, and bounds SCM stop plus process exit with one cancellable deadline. Use this for replacement, removal, and stopped-state restoration. Include already-stopped or absent registrations that still have a retained installed process. An identity lookup failure is ignorable only when that retained process is independently confirmed exited.

Activated-payload rollback must stop before any file move if service, Tray, or Session Agent shutdown cannot be confirmed. Preserve the original failure, the recovery failure, and active/backup trees for explicit recovery.

## Tried and ruled out
Increasing directory-move retries, sleeping for a fixed delay, polling a numeric PID after stop, force-terminating the process, or changing install-directory ACLs does not establish the required lifetime boundary. Unit coverage uses fake service/process controls and isolated temporary payloads; it runs no installed service or firmware operations. Check both standalone and bundle-managed invocation paths and mutation-check that bypassing the wait breaks rename-ordering regressions.

## Watch out
Open a stable process handle before stop to avoid deferred binding to a recycled PID. Release every retained candidate on success, timeout, cancellation, identity failure, and foreign-image filtering. Waiting for process exit does not retrospectively prove which particular handle caused an earlier installation failure. Keep guardian admission and source-specific hardware safety unchanged.
