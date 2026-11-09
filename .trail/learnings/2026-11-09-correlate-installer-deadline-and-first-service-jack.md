---
title: Correlate installer deadline and first service pipe timings
author: Jack
date: 2026-11-09
branch: fix/service-startup-timing
touches:
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Service/NamedPipeControlServer.cs
  - src/OpenBlade.Service/Program.cs
  - src/OpenBlade.Service/Worker.cs
  - src/OpenBlade.Setup/SetupServiceHealthProbe.cs
  - src/OpenBlade.Setup/WindowsSetupPlatform.cs
  - tests/OpenBlade.Service.Tests/NamedPipeControlServerTests.cs
  - tests/OpenBlade.Service.Tests/WorkerTests.cs
  - tests/OpenBlade.Setup.Tests/SetupServiceHealthProbeTests.cs
  - tests/OpenBlade.Setup.Tests/WindowsSetupPlatformServiceTests.cs
---

## Decided
Capture raw Windows QPC timestamps and frequency in the retained Setup package log for each install and rollback probe. Record connect, header, body, flush, and response read boundaries with cancellation flags. Join the Application event for the new service process by the best-effort pipe server PID and QPC timeline.

## Tried and ruled out
A late listener and an already-canceled first write can reproduce a write-side TaskCanceledException without proving why a deployment reached its deadline. Keep the existing deadline, framing, exception handling, and lifecycle intact. Trail's keyword matches for past IPC, startup, and cancellation fixes were checked against the diff. These catches record and rethrow, and the existing policies remain authoritative.

## Watch out
When a peer PID is missing, correlate by the host timeline and mark it as inference. The first service connection may belong to another client. A forced process termination can prevent the one-shot service event from reaching the log.
