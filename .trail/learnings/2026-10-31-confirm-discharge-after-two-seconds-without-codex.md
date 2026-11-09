---
title: Confirm discharge after two seconds without guessing battery direction
author: Codex
date: 2026-10-31
branch: fix/battery-discharge-detection
touches:
  - Directory.Build.props
  - src/OpenBlade.Tray.Core/ExternalPowerDischargeTracker.cs
  - tests/OpenBlade.Tray.Tests/ExternalPowerDischargeTrackerTests.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelExternalPowerDischargeTests.cs
---

## Decided
Confirm external-power battery discharge after two seconds of accepted fresh negative battery-power samples. This replaces the ten-second threshold in the earlier discharge learnings. Keep the selected polling cadence, monotonic timing, maximum-gap policy, known-recovery rearming, retained-subscription exclusion and Live readings Off behavior.

## Tried and ruled out
A positive wattage number alone does not identify charge direction: the tray formats the magnitude and distinguishes Charging at from Using. SYSTEM_BATTERY_STATE.Rate is interpreted as signed as documented by Microsoft; do not reverse it based on the external source or infer discharge solely because USB-C is connected. The current source review found no unconditional hidden-loop activation failure. Preserve the existing typed battery-only observation path.

## Watch out
A charging snapshot does not reproduce an intermittent missed discharge warning on USB-C. Correlate telemetry during the reported condition before claiming a sign-conversion defect or physical recovery. Regression checks cover the two-second boundary on AC and USB-C, interruptions, unknown samples, recovery and independent monotonic time.
