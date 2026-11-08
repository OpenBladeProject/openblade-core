---
title: Preserve Balanced during the SO6120 PWM experiment
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/So6120SilentTrial.cs
  - src/OpenBlade.Capture/So6120SilentPwmCommand.cs
  - src/OpenBlade.Capture/So6120DirectDutyJournal.cs
  - docs/so6120-supervised-pwm-trial.md
---

## Decided
The fixed SO6120 direct-duty experiment can begin in Balanced/Automatic as well as Silent/Automatic. Balanced is selector 00, distinct from Battery Balanced 06. Reuse the same actual-PWM-plus-four sequence and generic Auto handback; bind the selected profile to the command, baseline, approval and journal. Keep the legacy Silent entry strict. The recovered FE branch gates active state and F0.6, without a Silent-only or connector predicate. No physical fan-stop result follows from that source evidence.

## Tried and ruled out
Do not force a Silent transition on USB-C or make Balanced a temporary reset. Do not widen this fixed experiment to arbitrary selectors, raw duties, zero duty, or other Blade models. Do not add a power-classification firmware probe merely to prepare this change: Windows AC-line status proves external power only, and USB-C remains operator-reported context.

## Watch out
Preserve unchanged power/peripherals during the attended attempt, fresh admission, exact Auto restoration, and stop-on-ambiguity behavior. Retain the old Silent bundle and build a separate Balanced candidate. Keep follow-ups practical: a focused implementation and relevant regression review resolve this scope correction without a new investigation or test matrix. Preparing software does not authorize a live attempt.
