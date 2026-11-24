---
title: "Slow Capture tests: per-operation durable journal flushes in So6120 trials"
author: Jack
date: 2026-11-19
branch: claude/csharp-winui-test-coverage-e49560
cost: { minutes: 6, tokens: 168190 }
touches:
  - tests/OpenBlade.Capture.Tests/So6120DirectDutySessionTests.cs
---

## Decided
- Capture.Tests time (8-21 min in CI, 1m39s locally) is almost all in eight So6120* classes. So6120SilentTrialTests alone sums to ~100 s locally, which roughly equals the local suite wall time, so it is the critical path.
- The cost is durable journaling, which is production behavior: every mailbox read or write writes four write-through + Flush(true) records (kinds 5/6/7/8). One fixture stop trial does ~514 mailbox operations: 2,102 flushes, a 5 MB journal, 1,041 evidence captures, and 1.8 of 2.65 s spent flushing.
- Per the 2026-11-08 guardian-fixture learning, do not weaken journal durability to speed tests up. CI already routes TEMP to RUNNER_TEMP ("Prepare test temporary storage" in build.yml).

## Tried and ruled out
- Making the shared So6120DirectDutySessionTests.Clock fire one-shot timers instantly (CreateTimer override): no measurable gain (1m57s vs 1m39s, within noise). The trial delays are not the cost. Reverted.
- Per-flush cost alone looked cheap (0.69 ms) and misled the first diagnosis; the record count is what matters.

## Watch out
- Test-side levers that keep durability: split So6120SilentTrialTests so its rows run in parallel, and reuse one completed trial journal across read-only damage theories such as So6120DirectDutyJournalTests.StopReplayRejects* (5 rows, each running a full trial just as setup).
- Building Capture.Tests without rustc: -p:OpenBladeNativeDevelopmentBuild=false -p:OpenBladeNativePayloadDirectory=<existing obj/native/Debug>. A stale payload fails 2 GetterCandidateProbeRunnerTests with NativeComponentException 107.
