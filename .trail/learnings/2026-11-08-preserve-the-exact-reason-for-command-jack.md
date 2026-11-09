---
title: Preserve the exact reason for command-free Stop refusal
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-cpu-temperature-source.md
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceDutyPlatform.cs
  - tests/OpenBlade.Service.Tests/FanStopInitialAdmissionDiagnosticsTests.cs
---

## Decided
Preserve the exact pre-native refusal stage when installed Stop admission fails. Session ownership, service writer identity, CPU-worker readiness and the fresh thermal sample are separate checks. Thermal refusal reports the actual CPU/GPU values, inactivity/power evidence and sample age alongside the unchanged policy. A warm service CPU sample does not establish what a newly launched guardian measured.

## Tried and ruled out
The installed e94a93b2 build produced changing CPU readings, but its first post-archive Stop returned a conclusive command-free refusal with zero resolved EC commands. The generic message could not distinguish CPU heat, missing data or session ownership. Do not repeat that test blind, infer GPU inactivity from a null temperature, or raise limits to get through admission.

## Watch out
These diagnostics do not change thresholds, deadlines, native acquisition ordering, ownership or Auto cleanup. Known no-mutation/no-pending refusal is a failed activation, not failed restoration. A successful broker retry after directory access denial is recovery without an identified lock owner. Trail's keyword overlaps with historical IPF, cache and Capture notes do not imply those approaches were reintroduced; installed LHM observations and unchanged admission are reported separately.
