---
title: Allow a previously full battery to fall toward its active cap
author: Jack
date: 2026-11-10
branch: fix/battery-limit-discharge-warning
touches:
  - CHANGELOG.md
  - src/OpenBlade.Tray.Core/MainViewModel.ExternalPowerDischarge.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelExternalPowerDischargeTests.cs
---

## Decided
A battery charged above a newly active cap may fall toward it. Keep the discharge warning and tray badge quiet whenever Windows reports at least the confirmed active cap minus five percentage points. The first fresh negative reading below that threshold warns immediately. This extends the earlier five-point allowance on its upper side.

## Tried and ruled out
The symmetric band still warned when a previously full battery was more than five points above the active cap. Retain the existing lower threshold and all active-cap admission checks.

## Watch out
This is warning policy, with no change to signed telemetry or firmware operations. Cover a previously full battery and the inclusive lower threshold in the shared observation path. Treat any Trail match against the earlier symmetric band as superseded by the maintainer's correction.
