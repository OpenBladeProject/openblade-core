---
title: "Cooling-pad Automatic mode, immediate apply and curve smoothing"
author: Jack
date: 2026-11-21
branch: fix/cooling-pad-razer-driver-stack
cost: { minutes: 28, tokens: 155083 }
touches:
  - docs/cooling-pad.md
  - CHANGELOG.md
  - Directory.Build.props
  - src/OpenBlade.Tray.Core/CoolingPadViewModel.cs
  - src/OpenBlade.Tray.Core/FanControlPresentation.cs
  - src/OpenBlade.Tray.Core/Properties/Resources.qps-ploc.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.ru-RU.resx
  - src/OpenBlade.Tray.Core/Properties/Resources.zh-CN.resx
  - src/OpenBlade.Tray/MainWindow.CoolingPadSnapshots.cs
---

## Decided
The cooling-pad (RC21-0230) Automatic mode is Synapse Smart from the pinned product 3907 bundle (module 74967): 0D/10 selects the firmware-owned Smart preset (Quiet 3, Balanced 4, Performance 5), then 0D/01 sends floor(rpm/50) from Synapse's default step tables. A step table returns the speed of the smallest point strictly above the reading, so Balanced CPU (40:1000, 90:2200) is 2200 RPM for any reading from 40 to 89 C. That is faithful, not a bug. Above the last point OpenBlade keeps the last speed, where Synapse sends 0.
The pad page applies each change after a 400 ms debounce, like the laptop controls, and refreshes every 5 s while visible. After an unconfirmed change, automatic reads and writes stop until the explicit Check pad read. A null mode selection is sent as Keep, so an untouched area stays on the pad's own settings.
Host fan curves (laptop manual curve on every model, and pad curve and Automatic) share FanCurveTemperatureFilter: a 5 s exponential time constant and a 3 C falling deadband, with no timed hold. Fail-safe and the pad's source pause stay on raw readings.

## Tried and ruled out
A timed cooldown hold for spike smoothing; it stays in the temperature domain. Skipping the Smart preset write because the firmware record already matches: the firmware cannot report which owner wrote last, so the write is skipped only after this session confirmed that preset.

## Watch out
Every MainWindow_CoolingPad* resource key used from code-behind (GlobalSearchCatalog, snapshots) is looked up at runtime. Retiring a resw key without updating those throws KeyNotFoundException on the first global search; CoolingPadViewModelTests now checks them. The Smart readback reports ConfirmedFanMode null; check behavior by the 0D/81 ramping target (Balanced to 2200, Quiet to 1000 on RZ09-0581). Physically checked on 2026-11-20: Auto Balanced and a direct switch to Quiet, release restoring firmware 1600 RPM and Breathing. Not physically checked: Performance, a GPU-only source, demand leases in Auto, and the laptop curve smoothing.
