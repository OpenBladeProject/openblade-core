---
title: Treat the confirmed charge cap as a discharge warning exception
author: Jack
date: 2026-11-10
branch: fix/battery-limit-discharge-warning
touches:
  - src/OpenBlade.Tray.Core/MainViewModel.ExternalPowerDischarge.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelExternalPowerDischargeTests.cs
---

## Decided
Suppress the external-power discharge episode on an accepted negative power sample when the firmware-confirmed protection limit equals the fresh Windows battery percentage on external power. This covers barrel and USB-C through the shared tray observation path. Warn immediately on the first negative sample outside the exact cap. Keep the signed telemetry unchanged.

## Tried and ruled out
A global time delay would undo the requested immediate warning for other cases. A saved limit cannot establish effective charge protection. Suppressing an entire range near the cap would hide real discharge after the battery falls below it.

## Watch out
A missing Windows percentage or unknown override on an override-capable profile keeps the warning eligible. Profiles without the override capability can use their confirmed limiter state while retaining the unknown field as null. Trail check term matches for rate and override state do not indicate rate reinterpretation, retained readings, or synthesized readback. This is software coverage only.
