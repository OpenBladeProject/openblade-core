---
title: Share baseline-only battery confirmation and distinguish owner diagnostics
author: Jack
date: 2026-11-13
branch: fix/battery-protection-963
touches:
  - docs/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-07-models-2021-2022.json
  - evidence/source-reviewed-blades/2026-11-07-models.json
  - src/OpenBlade.Protocol/ExperimentalBladePort.Battery.cs
  - tests/OpenBlade.Protocol.Tests/SourceReviewedBladeModelTests.BatteryConfirmation.cs
---

## Decided
Claude's completed review prompted a common confirmation rule for all 11 admitted ExperimentalBladePort battery models: up to nine read-only GETs 250 ms apart, retrying only the exact saved pre-write raw byte. This software tolerance grants no new command authority. The seven Synapse profiles accept optional zero/one-byte SET state payloads; the four older profiles retain echo validation. Capture-backed ProductionBatteryPort is unchanged. Record ACK payload as acknowledgment evidence, not confirmed state, and distinguish unexpected readback from an exhausted stale window. Keep the shared operation lock through confirmation so setters cannot interleave.

## Tried and ruled out
Do not release serialization between reads, infer partially applied enable/threshold combinations, or add a factory seam solely to remove six seconds from existing recovery tests. Review fixes add explicit stale-then-other-state and legacy-bound tests. Cosmetic duplicate-predicate suggestions do not establish a defect.

## Watch out
Owner ZIP posted in issue 963 is nightly.114 on exact RZ09-0529/02C7. It records successful 60-to-80, 80-to-Off (raw 50), Off-to-80 (raw D0), 41 successful battery GETs and no failed queries or paused writes. The 80% restore occurred during explicit Reapply; the origin of the earlier 60% baseline is not captured. A slider change intentionally creates an applied global draft; Save persists it. Keep the physical charging complaint open and do not attribute these successful operations to the acknowledgment or retry hypotheses. The separate handled-pointer-event concern needs a genuine disconnected input reproduction before a UI fix is claimed.
