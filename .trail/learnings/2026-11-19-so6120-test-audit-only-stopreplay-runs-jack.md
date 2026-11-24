---
title: "So6120 test audit: only StopReplay runs a trial purely as setup"
author: Jack
date: 2026-11-19
branch: claude/csharp-winui-test-coverage-e49560
cost: { minutes: 2, tokens: 192784 }
---

## Decided
- Audited the So6120 Capture tests (264 methods, ~279 s summed under load) for full trials run only as setup. Exactly one qualifies: So6120DirectDutyJournalTests.StopReplayRejectsHashValidUnapprovedOrOutOfOrderZeroEvidence. Its 5 rows each run an identical complete stop trial and then tamper with a copy of the journal (~26 s summed, ~9.5%). Sharing one journal would save 4 trials, roughly 7-8% of So6120 flush work.
- All other expensive tests use the trial as the behavior under test: fault at write N, caller loss, cancellation, deadlines, release after Auto. They cannot share results. Cross-test duplication of the same default trial was not found.

## Tried and ruled out
- Dropping FileOptions.WriteThrough in the journal: Flush(true) dominates (0.77-0.97 ms per record with or without WriteThrough; WriteThrough alone is 0.11-0.26 ms but does not flush the device cache). No production lever there, and WriteThrough-only would weaken durability.

## Watch out
- With trial count fixed by coverage and per-record flushes fixed by production durability, Capture.Tests CI time is mostly legitimate. Further big gains need a reviewed production journal design change, not test edits.
