---
title: Show awake idle dGPU temperature in live readings
author: Jack
date: 2026-11-22
branch: claude/dgpu-temp-display-73b0c4
cost: { minutes: 22, tokens: 255013 }
touches:
  - src/OpenBlade.Service/FanCurveControl.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Service.Tests/FanCurveCalculatorTests.cs
---

## Decided
Service live readings (ServiceControlRuntime.ReadOptionalLiveSensorsAsync) now call IFanTemperatureSource.ReadForLiveReadingsAsync. In Windows-reported D0 without a measurable workload it opens NVML only after D0 has persisted for IdleGpuLiveReadingDwell (3 s) across samples, at most once per IdleGpuLiveReadingInterval (10 s), releases the session immediately, and reuses the last D0 reading only while every sample since still reports D0. Any non-D0 or unknown sample, including a power change during the read, drops the reading and restarts the dwell. Ordinary ReadAsync stays workload-gated, so Capture's no-load validation baseline is unchanged.

## Tried and ruled out
Measured 2026-11-22 on the RTX 5090 Blade: UnrealEditor held the dGPU in D0 while idle. The 150 ms WDDM workload gate was active in 1 of 20 windows, and per-process Running Time deltas across whole 2 s intervals were ~0.01-0.04% (0 of 20 above 0.1%). Widening the counter window or latching on workload cannot show this case; only D0 evidence can. Reading on every D0 sample was rejected because a 2 s poll could keep an unheld GPU from reaching D3.

## Watch out
The 3 s dwell and 10 s spacing are not physically validated against NVIDIA's runtime-D3 idle timeout. Confirm with DEVPKEY_Device_PowerData that an unheld dGPU still reaches D3 while the dashboard is visible. A fresh worktree's native build needs the rustup proxy (cargo +pin). Without it, borrow a payload whose rust/ tree matches HEAD and build tests with -p:OpenBladeNativeDevelopmentBuild=false and that payload's -p:OpenBladeVersion; otherwise ~1,900 Service tests fail with native category 107.
