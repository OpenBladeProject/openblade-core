---
title: "Session power relay test flake: 40 ms callback budget, not the poll"
author: Jack
date: 2026-11-22
branch: claude/jolly-haibt-5036d6
cost: { minutes: 17, tokens: 159047 }
touches:
  - tests/OpenBlade.SessionAgent.Tests/SessionPowerTransitionRelayTests.cs
  - tests/OpenBlade.SessionAgent.Tests/TempOriginalRelayRepro.cs
  - tests/OpenBlade.SessionAgent.Tests/TempPoolStarvation.cs
---

## Decided
- The SessionPowerTransitionRelay flake (TaskCanceledException from WaitUntilAsync after `requests.Count == 2`) was not the polling helper. The test passed a 40 ms `callbackBudget`, which is a real-time deadline: `ObserveResumeDeadlineAsync` and the suspend `Wait(remaining)` use wall time even when the injected QPC clock is fixed. Under thread-pool starvation the in-flight report's async continuation took over 40 ms to promote the pending resume, so the relay expired it and the second report never came.
- Fix: default 1500 ms budget, a plain TaskCompletionSource (synchronous continuations) for the in-flight report so `SetResult` promotes and sends the pending transition inline on the test thread, LongRunning threads for blocking suspend callers, and an ordering signal from the injected clock (the relay reads it under its lock when it creates a transition) so the second suspend really is superseded rather than expired.

## Tried and ruled out
- Raising the budget alone: the maximum is 1500 ms (`SessionPowerTransition.MaximumAgeMilliseconds`) and it still leaves the pending dispatch racing the thread pool. With a large budget, the old sequence also blocked the test thread for the full budget on the middle suspend.
- Injecting a TimeProvider into the relay: production change; the native suspend path blocks synchronously with `Task.Wait(TimeSpan)`.

## Watch out
- Every pending transition in this relay has a wall-clock deadline of at most 1.5 s by design. A test that checks pending dispatch must dispatch inline or tolerate that budget; it cannot fake it through the `timestamp` delegate.
- Thread-pool starvation (128 × `Task.Run(Thread.Sleep)`) reproduced the old failure locally in 1 of 8 runs.
- The clock hook keys on thread id, because the first suspend's thread may still read the clock late under load.
