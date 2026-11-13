---
title: Poll origin verification alongside held status refresh
author: Jack
date: 2026-11-13
branch: fix/native-thermal-edge-corrections
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - tools/openblade-windows-host/src/windows/scm/installed/subscription_tests.rs
---

## Decided
Poll the retained-origin timer and source lock as one independent future in the existing biased select. Keep the same verify_origin method, origin-error priority, shutdown path and source retirement. A refresh holding the source mutex must remain pollable while origin verification waits for that mutex.

## Tried and ruled out
Awaiting source.lock inside the selected timer branch stops polling the refresh that owns the lock and creates a cycle. The pure outer-runtime regression holds refresh across a 501 ms virtual origin tick, releases it and proves the old loop cannot progress. Do not replace origin verification with a weaker publication guard or increase product timeouts.

## Watch out
Retain the earlier subscription timeout as an unknown-phase failure: the deterministic regression establishes the source defect, not that failure's exact phase. Verify release/material-event, origin-loss and STOP controls with the actual outer loop. Keep compiler phases idle during final exact native tests, preserve all intermediate generations, and distinguish offline fixtures from live origin, OS, HID and hardware proof.
