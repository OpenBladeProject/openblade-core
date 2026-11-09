---
title: Retain the measured thermal trigger through Stop cleanup
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.FanStop.Core/FanStopThermalPolicy.cs
  - src/OpenBlade.FanStop.Guardian/Program.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceDutyPlatform.cs
  - tests/OpenBlade.Capture.Tests/FanStopThermalPolicyTests.cs
  - tests/OpenBlade.Service.Tests/FanStopGuardianProgressTests.cs
---

## Decided
Capture the CPU/GPU trigger at the actual Stop thermal decision. Preserve raw readings for immediate/entry limits and the weighted mean plus complete window coverage for rolling limits. Include the configured threshold and rule, with invariant numeric formatting, before clearing history. Carry the first decision through coast-down, a later exit after confirmed Stop, successful Auto cleanup and RecoveryRequired status; keep cleanup failure separate.

## Tried and ruled out
The installed 8c869310 trial identified ThermalThreshold during coast-down but retained no sensor or numeric value. Later service CPU readings were separate from the guardian's triggering sample and cannot reconstruct it. Generic end reasons or an Applied Auto receipt do not supply the missing measurement. Keep this as diagnostics only: no extra sensor reads, changed limits, altered command admission or new averaging credit.

## Watch out
Continuity and invalid-sample failures retain priority over heat and must not publish a speculative thermal trigger. Entry refusals use separate per-attempt diagnostics and do not end the owned thermal policy. Equal values remain admitted because thresholds use greater-than comparisons. If both sensors exceed their limits, retain both. A cleanup failure must not erase the thermal record or become a successful Auto result. The supervised test still changes only Custom mode and CPU average limit to 65 C, preserves all other settings, and restores the exact baseline after verified release.
