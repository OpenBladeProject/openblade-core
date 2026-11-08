---
title: Pace mailbox polls and preserve failed-attempt evidence
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/So6120HostMailboxTransaction.cs
  - src/OpenBlade.Capture/So6120MailboxObservation.cs
  - src/OpenBlade.Capture/So6120MailboxObservationCommand.cs
  - docs/so6120-balanced-pwm-trial.md
---

## Decided
The first USB-C Balanced direct-duty trial reached only staging. B8=54 was independently confirmed, B9=49 reported handler completion, and its separate A0 exhausted 128 rapid polls. Failure10 is PollLimit, not Deadline11; saved timing bounds that transfer and fencing below401ms despite a2s budget. No F0 permission or FE selector write occurred. Native close and child exit were confirmed, but staging restoration was not. Pace pending polls across the existing shared A1/A0 deadline (16ms for2s/128), preserve bounded ledgers, cancellation and no retry.

## Tried and ruled out
Do not infer firmware hang or direct fan control from this result, extend the transaction budget, replace the fence with guessed cleanup, or reset the old attempt marker. A separate fixed observer selects only90..94/A0 at port300 and reads301; it never writes301 or publishes an EC command. Keep a new observed run and approval bound to locked original identity/evidence hashes; the prepared local launcher pins the exact reviewed artifacts. Matching command-bracketed scans are envelope evidence, not request ownership or current mapped fan state.

## Watch out
The original journal and machine marker remain fenced. The observer has fresh exact-device/driver/external-power/stopped-owner admission, read-only prior files, bounded scans and explicit close reporting. Local bundle pins are stronger than directCLI artifact selection and must be described that way. Failed native close retains pin/guard through reporting, with retention ending on process exit; there is no automatic retry or claim of continued ownership. Neither observation outcome alone admits restorative writes or reenables the trial.
