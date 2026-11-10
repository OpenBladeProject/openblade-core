---
title: Preserve the discharge episode across charge-limit threshold crossings
author: Jack
date: 2026-11-10
branch: fix/battery-limit-discharge-warning
touches:
  - src/OpenBlade.Tray.Core/ExternalPowerDischargeTracker.cs
  - src/OpenBlade.Tray.Core/MainViewModel.ExternalPowerDischarge.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelExternalPowerDischargeTests.cs
---

## Decided
Keep one warning notification per continuous negative battery-power episode even if the active charge-limit exception briefly hides the badge. If the first negative samples are exempt, emit the first notification when an eligible negative sample arrives. A nonnegative sample, verified unplug, or explicit reset rearms the warning.

## Tried and ruled out
Resetting the tracker every time a negative sample enters the charge-limit exception duplicates a toast when charge percentage or the limit moves across the cutoff and back. Keep the user-requested cutoff and signed telemetry independent of notification episode identity.

## Watch out
The service classifies plugged-in and USB-C sources only after Windows reports external power. The tray also samples the current Windows level and can see a power transition after the service observation. Missing level or confirmed protection retains the existing warning path. No hardware evidence was collected.
