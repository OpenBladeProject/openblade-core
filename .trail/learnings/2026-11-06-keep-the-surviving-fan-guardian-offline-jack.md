---
title: Keep the surviving fan guardian offline and separate cleanup facts
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - OpenBlade.slnx
  - docs/so6120-fan-stop-crash-recovery.md
  - src/OpenBlade.Capture/AssemblyInfo.cs
  - src/OpenBlade.Protocol/AssemblyInfo.cs
  - tests/OpenBlade.Capture.Tests/OpenBlade.Capture.Tests.csproj
  - src/OpenBlade.Capture/So6120TrialGuardian.cs
  - tests/OpenBlade.Capture.Tests/So6120GuardianProcessTests.cs
  - tests/OpenBlade.Capture.Tests/So6120TrialGuardianTests.cs
  - tests/OpenBlade.Guardian.TestHost/FakeTrial.cs
---

## Decided
Keep So6120TrialGuardian offline and composed only by injected tests. One surviving worker owns preparation, ExecuteApproved and disposal; explicit Auto, caller loss and cooperative lease/duration cutoffs only signal the existing confirmed-boundary handback. Keep production Stop unavailable. A caller-loss result may confirm Auto even though cancellation makes the underlying trial unsuccessful. Report Auto, ancillary cleanup, journal completion and disposal separately.

## Tried and ruled out
Fake process tests use sibling caller/owner processes, with gates during pending FE and after the committed zero checkpoint. Killing only the caller permits the same owner to complete one Auto sequence. Killing the fake owner preserves incomplete evidence and refuses same-root reuse with zero port calls. This is not proof of real deployment job inheritance, native release or firmware recovery; automatic takeover remains forbidden.

## Watch out
Use one monotonic timestamp to validate and renew a heartbeat. Set the stop token before publishing its status. Coarse progress flags describe last-reported state, not current pending native publication. A delayed watchdog is a soft stop request, not a hardware deadline. Dispose only after execution returns, never to interrupt a pending call. Open atomically published process-test reports with FileShare.Read | FileShare.Delete to avoid the Windows rename-window sharing race. Missing cleanup receipt or an apparently complete journal cannot authorize replay.
