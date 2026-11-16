---
title: Review voltage learning matches against actual model and recovery behavior
author: Codex
date: 2026-11-14
branch: feat/cpu-undervolt-models
touches:
  - AGENTS.md
  - Directory.Build.props
  - docs/rz09-0528-cpu-tuning.md
  - docs/source-reviewed-blade-models.md
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - src/OpenBlade.Protocol/Rz090528CpuFreshUsbIdentity.cs
  - src/OpenBlade.Protocol/Rz090528CpuLiveCurveNativeTransport.cs
  - src/OpenBlade.Protocol/Rz090528CpuLiveCurvePlan.cs
  - src/OpenBlade.Protocol/Rz090528CpuLivePowerPlan.cs
---

## Decided
The voltage expansion retains exact model/HID/CPU and native ABI admission. RZ09-0530 remains ObservedOnly. The existing RZ09-0528 V31 backend and schema3 recovery remain intact; schema4 selects the new closed eight-core contracts. No source-only claim is promoted to physical validation.

## Tried and ruled out
Reviewed the 20 trail check matches for this patch. They are phrase overlap, including the new learning's own negative sentences, not changes to fan thresholds, lighting selectors, THX, Intel IPF, hardware write authority or source provenance. The old 0..50/step1 range remains specific to 0528; new Phoenix range metadata follows its own source-defined magnitude lattice and bounded signed16 restoration.

## Watch out
The checker can flag retained restrictions as reversals when a changed sentence mentions an existing model or the same noun phrase. Review actual affected paths and tests, record the rationale in the PR, and preserve the check's reported result rather than claim it passed.
