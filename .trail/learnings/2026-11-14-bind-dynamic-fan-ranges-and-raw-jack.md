---
title: Bind dynamic fan ranges and raw Windows power to exact reviewed sources
author: Jack
date: 2026-11-14
branch: fix/issue-967-model-admission
touches:
  - Directory.Build.props
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/source-reviewed-blade-models.md
  - docs/source-reviewed-blades-2019-2020.md
  - docs/source-reviewed-blades-2021-2022.md
  - docs/source-reviewed-blades-issue-949.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
---

## Decided
Review issue967 across all22 admitted profiles using exact Synapse and available BIOS/EC evidence. The new immutable source ledgers pin118 records and311 UTF8 excerpts. Admit per-preset fixed targets and AC host curves on17 dynamic profiles by intersecting exact UI/MW envelopes with fresh current-preset0D83 metadata.709/711 additionally admit BatteryBalanced6 fixed targets, never battery curves.597 addsCreator2 while preserving legacyPerformance1(Gaming),Custom andSilent.709 addsMaximumPower2, never shared generic enum7. Keep physical validation separate and preserve validated-model bounds.

## Tried and ruled out
Do not turn Synapse's failed-query lowPower=false fallback into firmware-confirmed full power. Do not selectAutomatic or another preset merely to discover ranges. Do not publish metadata under a different preset, accept cached bounds as write authority, or infer BIOS/EC fields as a normal-controller bridge. Do not raise legacyMaximum/default5400 when adding5500 representation; retain separate supported bound and IPC82 coordinated update.

## Watch out
The production experimental factory receives raw Windows ExternalUnknown for AC presence. Admit that only preliminarily, resolve every thermal SET from fresh known firmware adapter evidence, and bind the original raw source through native submission. Apply this to fan and existing HyperBoost entry/restoration paths without broadening limited/battery cleanup. Propagate per-preset curves through service admission, desired normalization, target validation, maintenance/retry and UI. Missing CPU telemetry uses the actual preset maximum. Issue967 still has unresolved07/8C status failure; retaining numeric correlated status improves diagnostics but does not prove physical recovery. Historical automatic-only/Balanced-curve notes are superseded only for these exact reviewed paths.
