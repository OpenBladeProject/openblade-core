---
title: Cooling pad full speed while idle is the emergency limit
author: Jack
date: 2026-11-26
cost: { minutes: 30, tokens: 298072 }
touches:
  - CHANGELOG.md
  - AGENTS.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - build/BuildIdentity.ps1
  - build/Complete-NightlyRelease.ps1
  - build/Export-RazerBrightnessDriverPackage.ps1
  - build/NightlyChangelog.ps1
---

## Decided
A report of the RC21-0230 pad going to full speed while the laptop was idle on Auto Quiet was the 95 C CPU emergency limit, not a fault. A per-second watch caught idle background work holding the raw CPU reading at 90-97 C for about 30 s; the limit tripped after its 2 s confirmation and released below 92 C. The user chose to keep the 2 s raw confirmation unchanged (2026-11-25).

## Tried and ruled out
Demand leases: only outside IPC clients create them, and none ran. Connection or read failures: the service log had no cooling-pad errors around the report. Quiet itself cannot exceed 1600 RPM, so anything above that in Auto comes from the emergency limit or a lease.

## Watch out
In Smart presets (Auto), ConfirmedTargetRpm after a read projects the pad's ramping speed from 0D/81, not OpenBlade's demand, so status can show values like 1750 or 1950 that no curve produces while the pad ramps toward 3200. The diagnostics export keeps no temperature history; log ReadCoolingPadStatusAsync with LiveReadings each second to catch a burst.
