---
title: Report external-power discharge on the first fresh negative sample
author: Codex
date: 2026-10-31
branch: fix/battery-discharge-detection
touches:
  - src/OpenBlade.Tray.Core/ExternalPowerDischargeTracker.cs
  - src/OpenBlade.Tray.Core/MainViewModel.ExternalPowerDischarge.cs
  - tests/OpenBlade.Tray.Tests/ExternalPowerDischargeTrackerTests.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelExternalPowerDischargeTests.cs
  - CHANGELOG.md
---

## Decided
Report external-power battery discharge on the first accepted fresh negative battery-power reading, without a confirmation interval. The user explicitly replaced the earlier two-second request with immediate reporting. Remove candidate start/gap timestamps and the tracker's clock dependency; keep the existing observation cadence and all fresh-response, device, power-source and Live readings gates.

## Tried and ruled out
Do not set a zero-duration timer while retaining unused sustained-discharge state. Unknown readings and unknown/unverified sources are not known recovery for an active episode. Keep one warning per episode; clear/rearm only on a fresh zero/positive reading, verified unplug, or explicit reset such as Live readings Off. Retained subscription snapshots are not new battery samples.

## Watch out
Immediate means the first fresh sample, not a new zero-interval polling loop. Polling cadence still bounds sampling latency. Read-only USB-C investigation confirmed that the existing signed-rate API can report discharge and the user observed the installed notification; this does not physically validate the new immediate timing before deployment. Per-battery status flags were added only to local diagnostics, not production detection. This decision supersedes both earlier ten-second and two-second confirmation learnings.
