---
title: Isolate Setup process-wide diagnostic trace captures
author: Jack
date: 2026-11-15
branch: fix/setup-service-process-exit
touches:
  - tests/OpenBlade.Setup.Tests/WindowsSetupPlatformServiceTests.cs
---

## Decided
Setup tests that capture the process-wide Trace.Listeners stream must run in a nonparallel xUnit collection. Preserve exact diagnostic-entry assertions; concurrent unrelated health probes can otherwise add entries to the capture.
## Tried and ruled out
The hosted run of PR 974 captured four health timing lines where the test required three. Weakening Assert.Collection or accepting arbitrary matching entries would hide diagnostic regressions. Isolate the tracing class instead.
## Watch out
An ordinary and LocalDev Setup suite can pass locally while full-solution parallel collections expose the shared-listener race. This is test isolation, not a production service-health or process-exit failure.
