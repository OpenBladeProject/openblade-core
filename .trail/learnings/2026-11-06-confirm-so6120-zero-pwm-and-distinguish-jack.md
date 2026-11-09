---
title: Confirm SO6120 zero PWM and distinguish Auto restart from fan stop
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-supervised-fan-stop-trial.md
  - docs/so6120-supervised-pwm-trial.md
---

## Decided
Two attended SO6120/RZ09-0581 USB-C Balanced runs on frozen candidate ae26bd6c confirmed half-cycle 91/91 with positive raw tach, active B8=0 then B9=0, actual PWM 0/0, two consecutive zero-tach observations, and exact Auto restoration. The first run was 8b5cc38d; the unchanged repeat was a36a5107. Native close and both process exits succeeded for each. Preserve the captured controls and distinguish runtime telemetry from the operator's observation.

## Tried and ruled out
The first operator confirmation was withdrawn because the stages were unclear; it must not be cited as physical proof. The operator explicitly requested the identical repeat and then confirmed that both fans briefly stopped before Auto restoration. Reuse the reviewed binary and limits for that repeat; update only the prior completed-run pins and marker archive scope. Do not treat fans spinning after the console closes as evidence that the earlier zero interval failed, or assume a completed test leaves the fans stopped.

## Watch out
Console progress is deliberately buffered so console backpressure cannot delay Auto. Zero tach is an EC indication, not a calibrated RPM measurement. Coast limits bound new read starts rather than exact stop duration. Auto can restart fans before final cleanup/console exit. The first run's physical observation remains unconfirmed; the repeat has an attended owner confirmation. These results cover this exact Balanced device and sequence, not Silent, other Blade models, unattended recovery or a production fan-stop switch. Keep the new completed marker and immutable artifacts; do not replay a run or infer another repeat without authorization.
