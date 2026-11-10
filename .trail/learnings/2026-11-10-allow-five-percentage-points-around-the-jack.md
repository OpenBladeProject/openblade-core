---
title: Allow five percentage points around the active charge cap
author: Jack
date: 2026-11-10
branch: fix/battery-limit-discharge-warning
touches:
  - CHANGELOG.md
  - src/OpenBlade.Tray.Core/MainViewModel.ExternalPowerDischarge.cs
  - tests/OpenBlade.Tray.Tests/MainViewModelExternalPowerDischargeTests.cs
---

## Decided
The maintainer corrected the charge-protection warning exception to an inclusive five-percentage-point band around the confirmed active limit. For example, an 80% cap suppresses the warning at 75% through 85%. The next fresh negative sample outside the band warns immediately. This supersedes the exact-equality rule in the earlier charge-cap learning.

## Tried and ruled out
Exact equality missed reports that occur a few percentage points from the limit. Do not expand the band beyond the requested five points or add a delay outside it.

## Watch out
Preserve the signed power reading and the existing verified-device, external-power, and active-protection checks. The tests cover both edges and the next percentage point outside each edge. Trail check may flag the prior exact-cap decision, which this maintainer correction replaces.
