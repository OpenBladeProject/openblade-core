---
title: Allow bounded Auto PWM drift in supervised SO6120 trials
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/So6120SilentTrial.cs
  - src/OpenBlade.Capture/So6120DirectDutyCoordinator.cs
  - src/OpenBlade.Capture/So6120DirectDutySession.cs
  - src/OpenBlade.Capture/So6120DirectDutyJournal.cs
  - docs/so6120-supervised-pwm-trial.md
---

## Decided
SO6120 supervised Silent and Balanced plans keep immutable targets at captured actual PWM plus four, but new plans explicitly admit a live increase of one through eight raw counts per fan. Automatic telemetry can drift while mapped controls and ownership remain unchanged; equality with an earlier PWM sample is not required for this bounded experiment. The approval hash includes the new rule. All current entry gates and journal replay use the same predicate; old v2 plans retain their original hashes and strict semantics. Device admission, exact controls, post-FE target readback, and verified Auto restoration remain required.

## Tried and ruled out
The latest attended Balanced run confirmed B8=4, B9=4 and F0=40, then observed actual PWM rise from zero to 40. FE was definitely not submitted. The known prefix was independently restored, the journal completed, the native handle closed and both processes exited. This is verified cleanup, not direct PWM control. A target of four against actual forty still fails the new bounded-increase rule. No target rebasing, extra setting writes, retries or new firmware commands are introduced.

## Watch out
The pinned firmware normal startup path can produce PWM37 from its 20-percent floor at cycle183, then increment it. The timing after F0 does not establish causality. Sequential reads cannot remove the read-to-publication race. Keep practical tolerances in the approved experiment instead of requiring motionless Auto telemetry; this does not authorize another live run, a decrease or fan stop. Preserve consumed attempts and obtain one scoped approval for the next prepared trial.
