---
title: Service-exit wait preserves safety contracts despite lexical check matches
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
The installer service-exit patch keeps the existing safety contracts. It pins the native process object before stop and waits for that exact object's exit with a deadline; the fallback is to retain files and report failure. The required patch bump from freshly fetched main 0.63.2 is 0.63.3. Independent static review found no actionable regression, and focused ordinary/LocalDev Setup tests and an actual-directory mutation regression validate the ordering.

## Tried and ruled out
Trail check's version-digit, PID/process, stop/recovery, invalid-exception and canceled-task matches are lexical overlaps with unrelated historical rules. The changed code does not restore obsolete SID writes, relax ACL or guardian checks, issue firmware commands, poll/reopen a numeric PID after shutdown, alter WU policy, or broaden transport/content-rejection recovery. Isolated tests use fake controls and temporary payloads rather than real machine guardian records.

## Watch out
Keep the lexical-check assessment in the commit or PR description when the checker still returns conflicts. Preserve original and recovery errors separately. A source/test fix and a process-lifetime observation do not establish the exact cause of an earlier install failure or hardware recovery.
