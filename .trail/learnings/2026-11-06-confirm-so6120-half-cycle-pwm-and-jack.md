---
title: Confirm SO6120 half-cycle PWM and Auto handback
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-balanced-pwm-trial.md
  - docs/so6120-supervised-pwm-trial.md
---

## Decided
The attended SO6120 Balanced half-cycle trial on candidate b01b548c confirmed direct FE operation and Auto restoration on the exact RZ09-0581. Cycle183 yielded approved91/91 targets. Actual PWM before FE was46/41; three complete FE observations read91/91, with nonzero tach readings. Handback directly restored selector00, manual00, F0=00 and original staging0/0. Final Auto PWM75/75 was firmware-controlled, not forcibly reset to the captured zero.

## Tried and ruled out
The complete journal verified1714 records and419 unique native publications:411 A0 and8 A1, each write with separate A0. Completion generation9 and final observation31 matched original controls. The native handle closed without errors; Capture and its wrapper both exited0. The earlier +4 trials stopped before FE because Auto startup crossed the tiny target. Do not repeat those failures as evidence that the FE path is unsupported.

## Watch out
This confirms one fixed Balanced direct-PWM trial, not zero duty, fan stop, calibrated RPM or electrical duty, Silent physical validation, unattended recovery or another model. The one-second dwell is not total exposure: measured execution was about44.9 seconds, and possible-F0-to-confirmed-restoration about21.3 seconds. Preserve the completed marker and immutable evidence; a new live experiment needs its own scope. No production fan-stop capability is enabled by this evidence alone.
