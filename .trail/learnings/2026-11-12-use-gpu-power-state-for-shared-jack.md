---
title: Use GPU power state for shared fan-curve temperature sampling
author: Jack
date: 2026-11-12
branch: fix/idle-gpu-fan-curve
touches:
  - Directory.Build.props
  - src/OpenBlade.Service/FanCurveControl.cs
  - tests/OpenBlade.Service.Tests/FanCurveCalculatorTests.cs
  - tests/OpenBlade.Service.Tests/FanStopTemperatureSourceTests.cs
---

## Decided
Ordinary fan curves and fan-stop sampling now share D-state-gated GPU acquisition. Read actual temperature for Windows-reported D0 even after workload counters become idle; skip NVML for D3 or unknown power. CPU-only curve fallback requires GpuPoweredDown, never merely GpuInactive. Idle samples release their driver session immediately; only an active ordinary sample may renew the existing bounded session lease. Stop may borrow but never renew that lease. This shared service change covers ordinary maintenance and retry revalidation on every admitted manual-curve model; Rust currently has no separate manual-curve sampler.

## Tried and ruled out
Do not add a timed fan cooldown hold or invent a temperature after D3. The existing five-second timer limits driver-session lifetime, not fan target lifetime. A power transition during sampling invalidates the temperature and power-down claim; missing/unknown inputs retain the curve maximum fail-safe. Focused transition tests cover actual cooling, both curve modes, D3/unknown no-probe behavior even with stale active counters, emergency limits, cancellation cleanup, and resampling after D0 returns.

## Watch out
No physical validation, GPU workload, live fan write or installation was performed. Windows CM_POWER_DATA reports the latest device D-state; unit tests prove admission decisions and session lifetime, not an atomic physical no-wake guarantee across OS/driver races. trail check emits lexical conflicts for GPU workload absence, timers, Stop ownership, lighting and presentation: this change enforces separate workload/power evidence, does not change fan-stop thresholds or ownership/admission, adds no cooldown timer, and changes no lighting/UI/native Stop contracts. Keep this rationale in the PR/commit when acknowledging those false-positive conflicts.
