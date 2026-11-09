---
title: Separate Stop thermal sampling from blocking native readback
author: Jack
date: 2026-11-08
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.FanStop.Core/So6120OwnedStopSession.cs
  - src/OpenBlade.FanStop.Core/FanStopThermalPolicy.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceDutyPlatform.cs
  - src/OpenBlade.FanStop.Guardian/Program.cs
---

## Decided
The installed 11544956 CPU45-average / CPU80-cutoff trial with a five-second window confirmed Stopped and autonomous Auto release, but the protected owner result recorded SafetyCondition, not ThermalThreshold. Exact original settings were independently restored. Do not promote this to rolling-average exit validation.

The exported command journal verified all991 chained records. Its final coast snapshot took2.89s after the explicit1s wait. Continuation thermal sampling shares the native worker, while HasContinuity permits only2s of uncovered time; the native read necessarily prevents compliant continuity. Held-Stop typed power reads and power-transition verification share this scheduling issue. The exact safety predicate was not retained, so do not exclude additional simultaneous admission or sample failures.

## Tried and ruled out
A first attempt refused conclusively before any EC command at CPU81C against cutoff80C and restored the original settings; aftercooldown the same frozen runner was retried without changing policy. Do not raise cutoffs to hide this timing issue, reset rolling history after native reads, credit command samples toward windows, or lengthen the freshness budget. Moving session.ContinueStopAdmitted to another thread alone still blocks on the native session lock.

## Watch out
The next correction needs a bounded independent OS-only sampler with one owner of the thermal policy and immutable timestamped admission/terminal decisions. Keep native commands on their existing serialized owner and preserve stop-on-ambiguity cleanup. Verify coast, held power reads and transition readback with controlled slow operations; test genuine sampling gaps separately.

The frozen test runner can briefly observe Inactive with an Applied Stop receipt before released Automatic. Its normal loop breaks on that snapshot even though clean-refusal classification is false. Later reconciliation verified release/restoration, but did not retain the terminal reason. Preserve the original test and fix reporting in a new harness; do not mistake this false negative for an unverified hardware restoration.
