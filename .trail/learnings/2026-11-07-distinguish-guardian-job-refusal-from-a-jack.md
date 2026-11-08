---
title: distinguish guardian job refusal from a hardware trial
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-fan-stop-crash-recovery.md
---

## Decided

Treat guardian isolation refusal as a startup result, not a failed fan or Auto operation. If it precedes metadata and acquisition, preserve the one-shot launch receipts and say that no hardware trial ran. Keep the current outside-all-jobs gate until a reviewed replacement establishes the intended lifetime contract. A future caller-only guarantee must be explicit and must not claim survival of an unknown host job.

## Tried and ruled out

A separate console and CREATE_BREAKAWAY_FROM_JOB can escape an immediate caller job while retaining an ancestor. Microsoft documents that breakaway stops at a disallowing ancestor. An alternate shell or local WMI creation is not independent admission evidence; check the resulting owner itself. Windows Desktop Activity Moderator can assign desktop processes to system-managed jobs after creation, so any-job membership does not identify a caller-owned job. This is a documented possible cause, not proof that an observed unnamed job belongs to DAM.

## Watch out

QueryInformationJobObject(NULL) returns only the immediate job. Absent kill-on-close does not prevent explicit job termination or identify unknown ancestors. The read-only WMI named-job provider omits unnamed jobs, so an empty result is unresolved ownership. Use hardware-free probes and fake caller-loss tests to resolve process isolation before repeating a real trial. Persist the refusal stage and native error outside a private console that disappears on exit. Never transplant a fake fixture's relaxed job admission into the live hardware owner.
