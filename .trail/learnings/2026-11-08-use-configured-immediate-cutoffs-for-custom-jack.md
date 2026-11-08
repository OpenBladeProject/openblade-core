---
title: Use configured immediate cutoffs for Custom Stop entry
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.FanStop.Core/FanStopThermalPolicy.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - tests/OpenBlade.Capture.Tests/FanStopThermalPolicyTests.cs
  - tests/OpenBlade.Service.Tests/FanStopGuardianProgressTests.cs
  - tests/OpenBlade.Service.Tests/FanStopInitialAdmissionDiagnosticsTests.cs
---

## Decided
The user explicitly changed Custom Stop entry to use each configured immediate cutoff. Reuse the existing fresh-sample assessment for both initial and pre-command entry, including CPU/GPU equality, validity, freshness and explicit GPU D3 evidence. Keep Auto CPU55/inactive-GPU behavior and independent automatic-entry cool/dwell settings unchanged. Entry does not seed or credit rolling averages; owned continuation retains full-window rules.

## Tried and ruled out
The earlier CPU65-average / CPU80-cutoff experiment stopped at the old instantaneous EntryLimit65 guard. Increasing only the cutoff could not pass that separate guard. That historical restriction is now intentionally superseded for Custom by the user's product-change request; do not preserve it in a service layer, diagnostic label or supervised harness preflight.

## Watch out
The new trial changes only Custom mode, CPU average65 and CPU cutoff80, preserving GPU limits and averaging duration. Save and restore the entire actual baseline after independent release proof. Stop confirmation, ownership and Auto cleanup remain separate results; a successful configuration change or no-command refusal is not a successful Stop/Auto round trip. Keep existing exact-device capabilities and hardware command contracts unchanged.
