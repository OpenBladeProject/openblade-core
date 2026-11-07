---
title: Freeze canceled CPU observation diagnostics on normal returns
author: Jack
date: 2026-11-07
branch: feat/cpu-voltage-completion
touches:
  - CHANGELOG.md
  - src/OpenBlade.Service/ControlRequestHandler.cs
  - src/OpenBlade.Service/CpuLiveCurveTuningTransaction.cs
  - tests/OpenBlade.Service.Tests/CpuLiveCurveTuningTransactionTests.cs
---

## Decided
Freeze a canceled curve observation's diagnostic in BoundedAsync finally before unregistering its cancellation callback, including normal backend returns. Admission deliberately converts cancellation into false, so an empty observation can finish normally while a later cancellation callback still holds the notification chain. Keep the original stage and first failure immutable; retain the actual unfinished worker. Observation-only finalization leaves write admission, submission semantics and production deadlines unchanged.
## Tried and ruled out
Do not relax the active-stage assertion, extend the 600-second validation budget or normal operation deadlines, or assume the catch path always executes on cancellation. A deterministic regression holds a later cancellation callback and returns an empty observation normally; the previous code produced a null timeout context before the callback was released. CpuLivePowerObserver already checks cancellation after awaiting its result and snapshots in catch, so it does not share this gap.
## Watch out
Cancellation callbacks are LIFO, and disposing a pending registration can remove the only snapshot callback before it executes. Test cleanup must release the held callback and dispose its registration and stage even when awaiting observation fails. The real runtime test keeps an unfinished adapter read; its precise stage is useful diagnostic evidence, not an implementation detail to discard for a green test.
