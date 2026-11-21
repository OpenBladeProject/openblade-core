---
title: Pad curve gaps come from CPU telemetry timeouts
author: Jack
date: 2026-11-21
branch: fix/cooling-pad-razer-driver-stack
cost: { minutes: 26, tokens: 87035 }
touches:
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - tests/OpenBlade.Service.Tests/CoolingPadControlRuntimeTests.cs
  - CHANGELOG.md
  - docs/cooling-pad.md
  - src/OpenBlade.Service/CoolingPadAutoCurves.cs
  - tests/OpenBlade.Service.Tests/CoolingPadAutoCurvesTests.cs
---

## Decided
The pad status message "Cooling pad curve is waiting for a reading" is cleared to "following its temperature source again" on the next computed target. Without that, the message stays until another event overwrites it.

## Tried and ruled out
Blaming the original Automatic stop on dGPU D3 power-down. On RZ09-0581 on 2026-11-20 the RTX 5090 stayed in D0 for every sample (SetupDi power data and the service's own WindowsNvidiaGpuPowerState.Read both reported PoweredOn), and NVML returned the temperature without a traced failure. The service log instead showed "CPU temperature telemetry unavailable: read timed out" every one to two minutes, each recovering within seconds. Any such gap revoked pad control before ac7f96be. The powered-down GPU handling is still correct for hosts whose GPU reaches D3; it is source-backed here, not physically observed.

## Watch out
Do not count "waiting" messages in a cached status watch to measure gap frequency: the message is sticky, and only the 5 s tray read (while the page is visible) or a padctl read replaces it. The service's Trace output reaches the diagnostics export logs.jsonl through DiagnosticHistoryTraceListener, so check it for NvidiaNvmlTemperatureSession or CPU telemetry warnings first.
