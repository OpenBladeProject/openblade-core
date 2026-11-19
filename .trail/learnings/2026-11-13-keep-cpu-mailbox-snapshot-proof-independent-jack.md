---
title: Keep CPU mailbox snapshot proof independent of ThreadPool scheduling
author: Jack
date: 2026-11-13
branch: fix/installer-failure-details
touches:
  - tests/OpenBlade.Protocol.Tests/Rz090528CpuLiveQueryTests.cs
---

## Decided
Run the fake CPU mailbox query and frozen snapshot on dedicated LongRunning tasks. Keep the two-second snapshot liveness bound and all phase/count assertions. Hold the fake mailbox delay until explicit finally release, then join both tasks through one bounded WhenAll before disposing their events.

## Tried and ruled out
PR #952 CI run 43357155283 at efeb8787 failed only PendingMailboxWaitFreezesDistinctPhaseAndZeroCounts afterSubmission=true at Task.Run(snapshot).WaitAsync(two seconds). The log does not prove which continuation ran late. The harness depended on shared ThreadPool scheduling while another worker blocked. Extending the snapshot timeout past the fake's old five-second self-release could hide a real progress-lock regression. Reuse the same explicit-overlap principle as the GPU management lock learning.

## Watch out
This is a fake-transport test correction only; production query timing, progress locking and firmware admission stay unchanged. Keep the query incomplete while inspecting frozen diagnostics, and observe both workers on assertion, timeout and success paths. No development-time hardware action is authorized.
