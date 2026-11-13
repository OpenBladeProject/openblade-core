---
title: Share first fatal across reader and mutation
author: Jack
date: 2026-11-13
branch: fix/native-thermal-admission-fixtures
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
---

## Decided
Share the same first-fatal record between mutation-capable original read custody and Runtime before any request. Both native and injected constructors take the reader-owned record; initial and reconstructed reader factories retain that custody. Fresh original-owner checks remain required before native operations when no fatal has already denied admission.

## Tried and ruled out
Separate sticky records were insufficient: actual framed client disconnect can drop a mutation future after FirstIntent fatal A while retirement is held, leaving later reader owner-loss B able to win the reporting path. The correction changes cause coordination only, without changing cancellation or lifecycle algorithms. A composed regression sends the request, disconnects the real in-memory client, then checks Get, Observe and a new-SID setter. This note does not claim that newly added regression has already run.

## Watch out
All previous a26 native runs and compiler outputs remain historical evidence. Re-run the affected mutation-capable composition at the next frozen source; reuse unchanged managed and feature-excluded inputs only with explicit source/cfg bindings. This is diagnostic first-cause preservation on an already fail-closed path, not a new setter/replay or installed-hardware safety claim.
