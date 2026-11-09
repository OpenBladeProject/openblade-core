---
title: Preserve the firmware selector on Blade 16 manual fan entry
author: Jack
date: 2026-11-09
branch: fix/rz09-0581-manual-fan-power
touches:
  - src/OpenBlade.Protocol/ProductionBatteryPort.cs
  - src/OpenBlade.Protocol/ThermalModeProtocol.cs
  - docs/rz09-0581-direct-control-transitions.md
---

## Decided
The #906 generic 00/01 pair overwrote the AC performance selector. Exact SO6120 and SO6110 MCU workers write the mode to EC index 1A and manual flags to 36. On confirmed full adapter power, encode and confirm the retained selector (02 Performance, 04 Custom, 05 Silent) for the two admitted Blade 16 production paths. Pinned Product 736 and 710 Synapse policy corroborates the selector and reapplies the confirmed Custom levels after the fan targets. SO6120 BIOS 4.01 exports a different NVIDIA policy value for Balanced, Performance and Custom from the EC 1.04 factory tables.
## Tried and ruled out
Prior capture-backed manual entry proved fan state and restoration, without proving power retention. Do not use generic Balanced entry on full power from these three presets or replace the normal HID route with raw EC, internal duty selectors, GPU clock writes or a vendor side effect. Keep limited-power and battery decisions under their own existing rules.
## Watch out
Paired GET confirms MCU-reported mode, not live EC power or measured RPM. No 125–130 W limit can be derived from the raw TGPA field. On uncertain selector, target or Custom level writes, stop without using the legacy target-only continuation. Existing generic-manual sessions need a fresh Automatic baseline for the preserved entry path. The Trail check's phrase matches for unrelated model policies, merges and version fragments do not contradict this bounded fix.
