---
title: Validate only surviving-owner caller-loss recovery on SO6120
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-fan-stop-crash-recovery.md
  - docs/so6120-guardian-baseline-refusal.md
  - docs/so6120-supervised-fan-stop-trial.md
  - docs/so6120-supervised-pwm-trial.md
  - docs/so6120-guardian-caller-loss-validation.md
---

## Decided
The 2026-11-07 attended SO6120 test on exact RZ09-0581, EC1.04, reported BIOS4.01, USB-C Balanced and frozen Capture1ceb0112 verified caller-loss recovery at a resolved zero-PWM checkpoint. Only the original hardware-free caller was terminated. The same guardian survived, restored Auto and original owned fields, completed its journal and released native ownership. Original process exits were separately observed. See docs/so6120-guardian-caller-loss-validation.md for immutable evidence pins and measured timing.

## Tried and ruled out
The baseline was PWM56/52, the confirmed spinning stage91/91, and the zero checkpoint0/0. Raw tach was0/16 at caller loss, so this interrupted coast does not establish that both impellers stopped. Final Auto was PWM40/40 and raw tach18/18 with original controls restored. The expected canceled trial and owner exit2 coexist with a successful caller-loss verdict and controller exit0; do not rewrite them as ordinary stop-trial success.

## Watch out
This is surviving-owner caller-process coverage only. Ambient job membership remained; unknown outer-job termination, guardian death, native hangs, sleep and OS loss remain unsupported. The observed49.7ms exit-to-Auto-start and11.10s handback are measurements, not maximum recovery guarantees. Keep the completed marker and raw evidence. Do not enable production Stop, generalize to Silent or another model, or infer takeover authority from this result. Installed-service delegation and startup ownership reconciliation remain separate work.
