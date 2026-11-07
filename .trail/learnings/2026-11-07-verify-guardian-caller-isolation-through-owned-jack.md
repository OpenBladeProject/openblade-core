---
title: verify guardian caller isolation through owned job handles
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-fan-stop-crash-recovery.md
  - docs/so6120-supervised-pwm-trial.md
  - src/OpenBlade.Capture/So6120GuardianHost.cs
  - src/OpenBlade.Capture/So6120GuardianProcess.cs
  - tests/OpenBlade.Capture.Tests/So6120GuardianIntegrationTests.cs
  - tests/OpenBlade.Capture.Tests/So6120GuardianLaunchTests.cs
  - tests/OpenBlade.Guardian.TestHost/GuardianLaunchFixture.cs
  - tests/OpenBlade.Guardian.TestHost/Program.cs
  - src/OpenBlade.Capture/So6120GuardianCallerLifetime.cs
---

## Decided

Replace blanket no-job membership with an explicit supervised caller-process and private caller-job contract. The hardware-free client creates one unnamed KILL_ON_JOB_CLOSE|BREAKAWAY_OK job, assigns itself, and roots its owning handle until process teardown. Only the control pipe, a JOB_OBJECT_QUERY handle and a QUERY_LIMITED_INFORMATION|SYNCHRONIZE original-caller process handle enter the guardian's exact inheritance list. The owner verifies the job flags, original caller membership and nonsignaled state, its own exclusion from that job, and sole console attachment. Temporary query handles close before hardware admission. Ambient job membership is recorded; unknown outer-host-job, guardian, sleep and OS loss remain unsupported. The explicit scope appears before attended approvals. Production Stop stays disabled.

## Tried and ruled out

The previous outside-all-jobs requirement refused supervised startup even after native breakaway and a standalone launch. System-managed jobs are a documented possible source of ambient membership, but flags and process lists do not identify an observed job as safe or Windows-owned. The replacement establishes the needed object relationship rather than granting an exception for any guessed ambient owner. Do not use another shell, WMI fallback, alternate parent or token to evade admission.

An inherited query handle keeps a job object alive. The fake job-member sentinel initially inherited test setup handles and exposed that lifetime leak; spawn it only after the caller's inheritable duplicates close. It must die when the caller's last job handle closes while the guardian survives and completes same-owner fake Auto. An exit code of 259 does not prove a process is alive; require WAIT_TIMEOUT from the original process handle and cover an actual exited-259 caller.

## Watch out

Never dispose the private owning kill-on-close job while the caller is still executing; OS process teardown releases that one rooted handle. Startup diagnostic sidecars are create-once, durably flushed and explicitly admission-only. Persist isolation refusal synchronously before closing inherited query handles, then attempt console output after closure. A blocked console must not prolong the caller job. Print required startup messages before publishing admitted=true so output failure can still create a refusal record. Storage failure or any prior sidecar blocks acquisition; neither sidecar nor process exit proves Auto, journal completion or native release. Hardware command ordering and ambiguity handling remain unchanged. Fake-process success still does not establish live native caller-loss restoration.
