---
title: Stop-refused driver keyboard output is not a clean exit
author: Jack
date: 2026-11-25
---

## Decided
- Reverted 8cdc1d11 (e7727ad9). A worker exit where CanOutput refused output after the supervisor's stop request stays exit 1 / RecoveryRequired. This reconfirms the 2026-11-23 "ruled out" note.

## Tried and ruled out
- Classifying a stop-refused send (OutputCountMismatch, MayHaveSubmitted=false, nothing submitted) as a clean exit 0/Off so IsCleanStall rearms. A reviewer harness on the real processors showed a refused key-up for an already delivered down leaves that key down in Windows. Poison zeroes Winner and makes Disable a no-op, so cleanup never releases it.
- Catching KeyboardSubmissionDeferredException in FunctionRowDriverInputProcessor as "not submitted". `held` is updated before send, so a refused up clears HasTrackedKeys. Dropping OutputUncertain lets non-stop refusals (foreground change, stop drain) exit 0 and delete the ownership journal.

## Watch out
- A correct version needs a loop-level set of delivered, unreleased scan codes, updated only after a successful send. Release that set under `stopping` (output is allowed then), confirm it is empty, and reclassify only stop-caused refusals, capturing the refusal failure object.
- Worker Trace calls go nowhere: only the supervisor and agent register PersistentTraceLog.
