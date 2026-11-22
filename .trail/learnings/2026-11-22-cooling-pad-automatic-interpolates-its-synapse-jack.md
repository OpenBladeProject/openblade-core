---
title: Cooling-pad Automatic interpolates its Synapse tables
author: Jack
date: 2026-11-22
branch: fix/cooling-pad-auto-interpolation
cost: { minutes: 12, tokens: 79866 }
touches:
  - Directory.Build.props
  - docs/cooling-pad.md
  - src/OpenBlade.Service/CoolingPadAutoCurves.cs
  - tests/OpenBlade.Service.Tests/CoolingPadAutoCurvesTests.cs
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
---

## Decided
Pad Automatic now interpolates between the Synapse Smart table points in 50-RPM steps, through the same CoolingPadControlRuntime.Evaluate as the manual pad curve. This replaces the Nov 21 decision to keep Synapse's step lookup as faithful. The user reported that Automatic ignored temperature, and with step lookup Balanced was 2200 RPM from CPU 40 to 89 C. The table points, the 0D/10 Smart presets and the 0D/01 command and range are unchanged, so no firmware behavior is new. Balanced CPU now gives 1500 RPM at 60 C and 1850 at 75 C.

## Tried and ruled out
Keeping the step semantics and only explaining them. Synapse also has no pad emergency override: above a table's last point it sends 0. Only the laptop manual curve forces maximum at raw CPU 95 C or GPU 87 C.

## Watch out
The runtime spike test needs a plateau, which interpolated Auto tables no longer have. The HostCurve plateau case covers spike rejection; AutoFollowsSmoothedTemperatureAlongItsTable covers smoothing in Auto. Local builds: rustup's proxy is missing on this host and Build-NativeDevelopment.ps1 calls rustc and cargo with +1.99.1. A stale native payload fails about 12 ServiceControlRuntimeTests with category 107.
