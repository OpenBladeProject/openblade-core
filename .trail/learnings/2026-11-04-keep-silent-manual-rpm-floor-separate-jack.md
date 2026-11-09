---
title: Keep Silent manual RPM floor separate from automatic idle fan stop
author: Jack
date: 2026-11-04
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/silent-low-fan-validation.md
  - docs/evidence/silent-literal-zero-rz09-0581-2026-11-05.json
  - docs/evidence/silent-low-rpm-calibration-rz09-0581-2026-11-05.json
---

## Decided
On exact RZ09-0581 BIOS4.01 with full external power, reviewed Silent/manual05/01 retained1500 and separately authorized literal0 targets, but both actual fans stayed1900–2000 across30 samples in each trial. A bounded1900→1800→1700 calibration also settled near1900/2000;1700 failed the declared200-RPM tracking band and stopped lower candidates. These observations do not justify a forced Silent fan-stop capability or lower normal fixed-target range. Silent/Automatic had0/0 at the first trial baseline and1300/0 beforezero, so keep firmware-idle stopping separate from manual target behavior.

## Tried and ruled out
The zero trial had its own explicit one-shot user approval, reviewed source/build and fresh native safety admission. Zero was submitted once per channel and was not replayed; that authority is consumed. Numericzero is not a proven fan-off instruction, and its retained target is not physicalRPM. The calibration result lowestObservedSpinningTargetRpm1800 means the configured200-RPM tolerance passed at that command; actual stable readings were1900/2000, not1800. Do not substitute BatterySaver thermal profile while claiming Silent CPU/GPU performance: the pinned code calls a separate overclock API whose implementation/readback is not established here.

## Watch out
All three Silent probes restored exact3800 stored targets and05/00. The first exposed a lost temporarySilent override at service restart; a separately journaled typed restoration recovered it. The corrected shared wrapper completed both later round trips with all10 original intent fields and10 protected installed hashes matched. Final root installed-context read confirmed Running service, one expected-path tray and SessionAgent, Silent/Automatic, original UntilPowerSourceChange override, active verified admission and no unconfirmed writes. No product installation or new Silent fan-stop capability followed. Keep the separate negative records in docs/evidence and retain model/BIOS/power/condition limits; existing BatterySaver positive evidence remains distinct.
