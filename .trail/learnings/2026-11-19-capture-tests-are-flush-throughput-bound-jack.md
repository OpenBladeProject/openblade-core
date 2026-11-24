---
title: Capture tests are flush-throughput bound; splitting classes does not help
author: Jack
date: 2026-11-19
branch: claude/csharp-winui-test-coverage-e49560
cost: { minutes: 4, tokens: 182370 }
---

## Decided
- Capture.Tests wall time is bound by total disk-flush throughput, not by any single class. Splitting So6120SilentTrialTests into five classes left the local run unchanged (1m39s before, 1m45s after). Every So6120 class slowed together (OwnedStopSession 44 s to 103 s, DirectDutySession 29 s to 72 s), so the split was reverted.
- The only test-side lever left that keeps production durability is reducing total durable trial work: for example, reusing one completed trial journal across read-only damage theories instead of running a full trial for each row.

## Tried and ruled out
- Splitting the slow test class for parallelism: ruled out by the TRX timeline (compare per-class start/end in the TRX, not only per-test durations).
- Instant fake timers: ruled out earlier (see the 2026-11-19 per-operation durable flush learning).

## Watch out
- Per-test durations in a TRX inflate under parallel load. Before choosing a parallelism fix, use per-class start/end spans to see whether classes are competing for one shared resource.
