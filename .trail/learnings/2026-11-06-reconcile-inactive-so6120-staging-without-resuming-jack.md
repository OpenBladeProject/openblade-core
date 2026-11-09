---
title: Reconcile inactive SO6120 staging without resuming a fenced trial
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-balanced-pwm-trial.md
  - src/OpenBlade.Capture/Program.cs
  - src/OpenBlade.Capture/So6120MailboxObservation.cs
  - src/OpenBlade.Capture/So6120MailboxObservationCommand.cs
  - src/OpenBlade.Capture/So6120MailboxObservationEvidence.cs
  - tests/OpenBlade.Capture.Tests/So6120MailboxObservationTests.cs
  - src/OpenBlade.Capture/So6120StagingRecovery.cs
  - src/OpenBlade.Capture/So6120StagingRecoveryCommand.cs
  - tests/OpenBlade.Capture.Tests/So6120StagingRecoveryTests.cs
---

## Decided
A successful read-only SO6120 envelope observation after a fenced staging failure establishes only the sampled host mailbox state. Two idle matching scans do not prove current mapped controls: the mailbox has no request identity and payload may already have been staged. Reconcile a known B8/B9-only prefix through a separate fixed recovery, bound to both evidence sets and fresh exact scope. Require the original Balanced/Automatic controls and accept only original or submitted staging bytes; restore changed bytes to zero with independent confirmation and stop on drift or ambiguity.

## Tried and ruled out
Do not resume the old journal, fabricate its ownership checkpoint, or reuse the existing same-run Auto exit against a new process. That exit expects an in-memory entry checkpoint; a new recovery cannot invent ownership of the failed B9 confirmation. A bounded staging-only command reuses the paced transaction core and native lifetime handling without changing journal replay semantics or granting FE/F0 writes.

## Watch out
The separate create-once recovery claim is consumed before the first mapped read, which publishes A0. Keep the old journal and machine marker unchanged. Confirmed restoration and close are distinct from permission to archive the marker or conduct another PWM trial. This recovery is exact-attempt scoped and establishes no physical fan-stop result or support for another model.
