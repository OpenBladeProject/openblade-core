---
title: observe a live guardian checkpoint without granting another writer
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-fan-stop-crash-recovery.md
  - src/OpenBlade.Capture/So6120DirectDutyJournal.cs
  - tests/OpenBlade.Capture.Tests/So6120DirectDutyJournalTests.cs
---

## Decided

The attended caller-loss test needs a live zero-PWM checkpoint. The supervised v2 journal now permits read-only observers with FileShare.Read while retaining exclusive write and delete access. The observer opens Read with ReadWrite sharing, which grants no write handle. Post-release Inspect retains Read sharing and is still refused alongside the live writer. Legacy v1 remains exclusive because it has no planned live-observer path.

## Tried and ruled out

The originally reviewed guardian candidate opened the journal with FileShare.None, so a second live reader was refused. Progress output is deliberately buffered until handback and cannot trigger a timely caller exit. Offline replay alone had hidden that live-sharing constraint; reproduce the actual writer sharing mode before freezing a process test.

## Watch out

A read-only trigger validates complete chained frames, run identity, the approved half-cycle/stop plan, spinning evidence and a fresh zero checkpoint. Partial tails, a newer nonzero observation, fencing or an exit claim suppress the trigger. The controller keeps the original caller process handle and terminates only that hardware-free process. It separately verifies guardian identity and never waits on an unverified PID as if it owned it.

Caller cancellation can arrive after Auto has already started. Require recorded caller exit to precede AutoStartedTimestamp before calling the result caller-loss handback. Exit code 2 is shared by cancellation and report failures: require the independent Auto/cleanup/journal/release receipt and a matching final durable completion message. Read-sharing does not grant replay, another firmware writer or a production Stop capability; physical recovery is established only by the attended result.
