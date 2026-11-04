---
title: Confirm spinning fans before interpreting low target fan stop
author: Codex
date: 2026-11-04
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Protocol/BatterySaverLowFanValidationRunner.cs
  - tests/OpenBlade.Protocol.Tests/BatterySaverLowFanValidationRunnerTests.cs
  - src/OpenBlade.Capture/BatterySaverLowFanValidationCommand.cs
  - docs/battery-saver-low-fan-validation.md
  - docs/evidence/battery-saver-2000-to-1500-rz09-0581-2026-11-04.json
---

## Decided
For a causal fan-stop experiment, establish physical rotation before reducing the target. The explicitly authorized RZ09-0581 BIOS 4.01 Battery Saver battery follow-up on 2026-11-04 independently confirmed stored targets 2000/2000, then three actual pairs within the declared 1800-2200 tolerance: 1800/1800, 1800/1900, 1900/1900. Changing only both stored targets to 1500 was followed by both actual readings reaching zero, with three consecutive zero pairs. First both-zero occurred 10.755 seconds after the second 1500 target acknowledgement. Exact original 3800/3800 targets, BatterySaverAutomatic pair, installed profiles/overrides and process presence were restored. The sanitized evidence is docs/evidence/battery-saver-2000-to-1500-rz09-0581-2026-11-04.json.

## Tried and ruled out
The initial automatic baseline was already stopped, so its zero readings could not prove spin-down causation. A requested 2000 target is not the same as actual 2000: retain the measured 1800-1900 values and the explicit tolerance. Preparation is an experiment starting condition, not a new firmware prerequisite; keep the legacy direct 1500 experiment available. If either fan fails physical preparation, restore and skip the lower target rather than trying a higher value or another sequence.

## Watch out
The observed result is fan stop under this one cool Battery Saver battery condition, not steady 1500 RPM rotation or a guarantee of persistent fan-off under load, restart behavior, Silent mode or other models. Preserve native submission admission and stop-on-ambiguity for preparation, target change and restoration. Add new evidence instead of rewriting the initial record or importing this physical result into another model's range. Existing normal ranges remain unchanged by this developer test.
