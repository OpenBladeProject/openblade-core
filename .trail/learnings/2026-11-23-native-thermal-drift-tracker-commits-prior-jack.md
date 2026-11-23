---
title: Native thermal drift tracker commits prior intent only after a settled reconciliation
author: Jack
date: 2026-11-23
branch: feat/native-thermal-drift-tracker
---

## Decided
- Native thermal drift (`thermal_drift.rs`, gated on service-profile-triggers-v1) ports managed `ConfirmedStateDriftTracker` plus the automatic-transition exclusion (`ResolveAutomaticWriteExclusions`, SCR 16655-16720): an in-memory baseline that the first reading only sets, unknown values are no evidence, own readbacks confirm intentional, and either thermal subsystem drifted (or HyperBoost observed) excludes both from that reconciliation. The debt settles, and nothing is written.
- One `Transition` per reconciliation decides once, from whichever step reads firmware first (performance, or fans when fans go first).
- Modes: activation = Overwrite (managed preserve=false); lease expiry = NextOnly, which reproduces managed's publish-before-transition quirk (vector `leaseExpired` is submitted=false, ext [0]); reports, saves, power and boundary = WithPrior. Merged lifecycle causes take the strongest mode.
- The prior intent is only what a reconciliation committed (`Transition::commit`, when it is neither deferred nor owed for retry) or what an explicit request's own readback confirmed. Managed carries a deferred transition's prior in `PendingTransitionContinuation.PriorDesired`. With no committed prior, the reconciliation is the startup one and applies over drift.

## Tried and ruled out
- Recording `next` as the prior at decide time: a pace refusal, deferral or lost admission then turned the unapplied intent into the prior, and the retry read OpenBlade's own untouched firmware as drift (16 failing trigger tests).
- Detecting startup by "no debt entry": report_game with unknown power inserts a debt without reconciling.
- Resolving an explicit request's intent at resolve time: a stored-but-unapplied ManualCurve (no native recipe) made the next report pause over Automatic fans.

## Watch out
- In the readiness rig, `observeState` republishes desiredState without the application layer, and getStatus shows the plugged-in power profile's source on AC even with Game applied (pre-existing). Do not assert desired source or value from lifecycle rows. getStatus is not a fresh read; use observeState to drive the tracker, then getStatus for `externalChanges`.
- Tests that "move firmware outside OpenBlade so the reconciliation has work" now pause instead. Use `Runtime::restart_drift()` (a restart re-baselines) or a power change, not a lease expiry, to force a write.
- An excluded step's own reading reaches confirmedState only at the next status read.
- Drain after owned fans plus outside drift: use `rig.origin.stop()` and drain, not `rig.stop()`; the handback cannot confirm.
- Mutant "excluded step continues" is equivalent: the later step reuses the cached exclusion.
