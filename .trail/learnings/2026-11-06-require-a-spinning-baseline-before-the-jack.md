---
title: Require a spinning baseline before the SO6120 zero-duty trial
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/So6120SilentTrial.cs
  - src/OpenBlade.Capture/So6120DirectDutyCoordinator.cs
  - src/OpenBlade.Capture/So6120DirectDutyJournal.cs
  - docs/so6120-supervised-fan-stop-trial.md
---

## Decided
Prepare a separate fixed SO6120 Balanced half-cycle-to-zero-to-Auto trial. The hash-bound policy first uses the already exercised half-cycle entry, then requires a fresh exact FE/half-cycle observation with positive raw tach on both fans before a durable one-shot StopClaim. Only active B8=0 then B9=0 are admitted, with existing per-write admission, independent A0 and complete checkpoints. A short bounded coast observation distinguishes applied zero PWM from two consecutive EC zero-tach samples. Auto restores the latest confirmed prefix directly, including interruption between the zero writes.

## Tried and ruled out
The pinned EC FE branch copies request bytes unchanged to the PWM registers, without a reserved-zero branch or minimum clamp. It consumes changed active staging without another FE, policy or HID command. Do not infer a stop from an already-zero baseline, add a preparatory RPM/profile change, or restore half-cycle before Auto. If a confirmed zero equals its saved staging byte, clear that field's remaining restoration responsibility; otherwise retain/acquire it for exact Auto cleanup. Rewriting an already-restored byte is unnecessary and leaves ownership bookkeeping wrong if merely skipped.

## Watch out
Raw tach is an EC estimate that can lag and quantize; zero indicates the EC reports zero rotation, not independently measured mechanical rest or calibrated RPM. The five-second coast window bounds starting observations, not total zero exposure or native calls. Keep fresh thermal and control admission for zero writes, cancellation at resolved boundaries, and no further mailbox commands after ambiguity. Preserve legacy plan hashes/record ordinals and existing journal bounds. This preparation grants no live-run authority; the previous successful half-cycle approval is consumed.
