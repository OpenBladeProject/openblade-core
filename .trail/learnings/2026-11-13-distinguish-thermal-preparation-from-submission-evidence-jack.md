---
title: Distinguish thermal preparation from submission evidence
author: Jack
date: 2026-11-13
branch: fix/thermal-journal-diagnostics
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
---

## Decided
Classify installed thermal fences once in Storage.pending_reason. Settled Confirmed/KnownNotSubmitted history is not evidence of submission by a new Stored request. Remember failed Prepared publication separately from submission intent, preserving all partial/pending/published bytes and quarantine. Completion and later refusal use that same cause.
## Tried and ruled out
Do not reset history, fabricate settlement, infer no durable bytes from an I/O error, or retry persistence. Do not classify typed protection or original-owner failures as ordinary local I/O; the shared first-fatal latch remains authoritative.
## Watch out
Seed prior 5/6 through the actual dispatcher and actual native retirement in regression tests. Ordinary Prepared faults need a stage-specific injected replacement after successful config save, including partial pending and published-unconfirmed cuts. This stage changes diagnostics only; no setter or firmware admission expansion and no installed backend cutover.
