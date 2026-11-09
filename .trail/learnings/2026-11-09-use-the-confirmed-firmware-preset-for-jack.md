---
title: Use the confirmed firmware preset for fan handback and mode-7 exit
author: Jack
date: 2026-11-09
branch: fix/rz09-0581-manual-fan-power
touches:
  - src/OpenBlade.Protocol/ProductionBatteryPort.cs
  - docs/rz09-0581-direct-control-transitions.md
---

## Decided
On the two source-reviewed Blade 16 firmware paths, a fan-only full-power handback uses the confirmed preset selector with fan flag 00. Encode normal fan targets directly from the reviewed 0D/01 geometry and retain exact catalog admission. Mode 7 can go straight to each already admitted AC restoration preset; the captured Balanced restoration is not required before those targets.
## Tried and ruled out
Target-only updates leave mode and Custom levels alone. A legacy generic manual observation cannot establish a retained AC preset. Unadmitted HyperBoost destinations and low-power handoff retain their separate policies.
## Watch out
Maintain per-write source checks and stop on ambiguity. Confirm Custom levels after the handback and final mode after leaving HyperBoost. Neither firmware source nor paired MCU replies establish live GPU wattage.
