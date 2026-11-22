---
title: "Cooling-pad emergency limits share the laptop curve's 95/87 C"
author: Jack
date: 2026-11-22
branch: fix/cooling-pad-auto-interpolation
cost: { minutes: 17, tokens: 41095 }
touches:
  - CHANGELOG.md
  - docs/cooling-pad.md
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - src/OpenBlade.Service/FanCurveControl.cs
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
  - Directory.Build.props
  - src/OpenBlade.Service/CoolingPadAutoCurves.cs
  - tests/OpenBlade.Service.Tests/CoolingPadAutoCurvesTests.cs
---

## Decided
The pad's Automatic and HostCurve modes force 3200 RPM when a selected CPU reads 95 C raw or a selected, powered GPU reads 87 C raw. These are the laptop manual curve's limits, now the shared FanCurveCalculator.EmergencyCpuCelsius and EmergencyGpuCelsius constants. The user chose them over 80-85 C, which Blade CPUs pass in normal load. The check uses the raw reading, bypassing the 5 s smoothing, and the filter keeps updating so the table resumes from the smoothed temperature. Synapse has no pad emergency limit.

## Tried and ruled out
Reading unselected sensors for the emergency check. Only the selected sources are read, so a hot GPU leaves a CPU-only mode alone. Fixed mode reads no temperature and is not covered.

## Watch out
Pad runtime tests that use 95 C CPU or 87+ C GPU readings to drive a curve now hit the emergency limits. Use 94 C or 86 C when a test means curve behavior.
