---
title: Separate stored fan intent from source curve admission
author: Jack
date: 2026-11-14
branch: fix/issue-967-model-admission
touches:
  - CHANGELOG.md
  - docs/experimental-blade-support.md
  - docs/fan-power-source-policy.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/source-reviewed-thermal-967.md
  - docs/zh-CN/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-13-issue-967-software-validation/README.md
  - evidence/source-reviewed-blades/2026-11-13-issue-967-software-validation/claude-metadata.json
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Fans.cs
---

## Decided
Admit 029D/029F host curves over every already-admitted ordinary AC Fixed preset, with each model's unchanged static range and distinct upstream provenance. These readers report preset/manual state independently: map absence is not unreadability or a Balanced prerequisite. Choose typed curve maintenance from ReportsPerformanceWithManualFans, confirm the requested preset on entry, and retain fresh Full adapter admission for every curve setter.

## Tried and ruled out
Do not infer a Balanced-only curve limit from an empty per-preset range map, automatically inherit future Curve authority from Fixed, or add a 0D83 query to these static backends. Preserve their existing Fixed/DC scope. 0D81 reads targets; 0D88 reads tachometers. Source-only bounds and upstream measurements do not establish OpenBlade physical curve validation.

## Watch out
Stored thermal intent is schema data, not execution authority. Preserve old unavailable tuples and exact nullable application overrides during load/unrelated edits; validate new or changed base, explicit app, and inherited resolved intent against actual modes, presets, power and ranges. Never invent Balanced/Automatic writes from unavailable retained curve data on an unowned external state. Editor display coercion is not a user edit: preserve aliases and inactive values unless their controls were intentionally changed. Dedicated owned cleanup still hands firmware back directly without rewriting saved intent.
