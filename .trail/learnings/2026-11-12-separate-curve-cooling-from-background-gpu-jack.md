---
title: Separate curve cooling from background GPU telemetry
author: Jack
date: 2026-11-12
branch: fix/idle-gpu-fan-curve
touches:
  - Directory.Build.props
  - src/OpenBlade.Capture/SilentLowFanValidationCommand.cs
  - src/OpenBlade.Capture/So6120TrialThermalGuard.cs
  - src/OpenBlade.Service/BatterySaverFanStopRuntimePolicy.cs
  - src/OpenBlade.Service/CoolingPadControlRuntime.cs
  - src/OpenBlade.Service/FanCurveControl.cs
  - src/OpenBlade.Service/ServiceControlRuntime.cs
  - tests/OpenBlade.Capture.Tests/SilentLowFanValidationCommandTests.cs
  - tests/OpenBlade.Capture.Tests/So6120TrialThermalGuardTests.cs
  - tests/OpenBlade.Service.Tests/BatterySaverFanStopRuntimePolicyTests.cs
---

## Decided
Use ReadForFanCurveAsync for laptop curve maintenance, retry revalidation, and cooling-pad HostCurve sampling. Keep ordinary ReadAsync background telemetry workload-gated so the curve correction does not add idle NVML probes to every telemetry caller or change the no-load validation baseline contract. Both explicit curve requests and fan-stop safety requests use the same D0-gated/rechecked acquisition and immediate idle-session release. Cooling-pad requests include only selected CPU/GPU sensors.

## Tried and ruled out
Do not cache admission across samples, probe unknown/D3 power, or keep idle NVML sessions open to reduce initialization cost. Do not infer a powered-down GPU from workload absence or add a timed fan cooldown. Contradictory active counters and reported D3 remain unknown for CPU-only fallback. The second cancellation check after the native power recheck is intentional: cancellation can arrive inside that query, and regression coverage exercises it.

## Watch out
The shared-source correction exposed inactive-only cooling predicates in Battery Saver Stop and developer capture guards. Require inactivity plus an actual plausible10..55C GPU sample or confirmed power-down, retain existing CPU bounds and ownership/handoff, and use the dedicated fan-stop read. The55C bound is a conservative product/experiment cooling policy, not a firmware prerequisite. Reject implausible GPU samples as unavailable before consumers see them. OS-reported D-state gating and unit tests do not prove physical no-wake behavior across an OS/driver race; no physical workload, fan write, or installation was performed.
