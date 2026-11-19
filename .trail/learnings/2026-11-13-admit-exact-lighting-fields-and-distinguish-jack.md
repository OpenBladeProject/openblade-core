---
title: Admit exact lighting fields and distinguish active MCU state from saved state
author: Jack
date: 2026-11-13
branch: fix/issue959-lighting
touches:
  - Directory.Build.props
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/source-reviewed-blade-models.md
  - docs/source-reviewed-blades-2019-2020.md
  - docs/source-reviewed-blades-2021-2022.md
  - docs/source-reviewed-blades-issue-949.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.IndependentLighting.cs
---

## Decided
Review lighting as a per-feature contract for every exact model, rather than inheriting support from a shared API name. The 2026-11-13 lighting coverage record accounts for all 22 profiles. Nine older profiles use independently read brightness and available logo fields; six additional MCU images establish bounded active-state effects. Source review and physical validation remain separate.

For the six MCU paths, bank 0 is active and bank 1 is saved. CH580/DI780/PI480 use their selected legacy setter, which clears custom ownership but copies saved brightness: read active brightness again before applying the requested target. SO6100/KI8100/KI8110 use bank-0 LED-5 setters; Spectrum stays observed-only. Their already-Off shortcut precedes ownership cleanup, so only that source-proven case needs a dark, independently confirmed Breathing-to-Off transition. Every submission retains fresh admission and stops after ambiguity.

## Tried and ruled out
Do not use saved-bank readback as active-state confirmation, expose RGB setters that ignore same-effect color changes, or replace all six paths with legacy commands: KI8110 legacy commands also affect LED 38. Full 12-byte GETs must validate actual colors and Wave speed 40; unknown parameters remain unknown. Exact-model Synapse logo getters are named getLedState/getLedEffect, not getLogoSystemEffect.

A missing standalone MCU download is not proof that BIOS/EC lack useful source. Inspecting RZ09-0330 BIOS 1.06 and EC 1.01 found a real EC USB stack, but its consumed identity is 1532:F003 and class 03 echoes success without lighting-state reads; class 0F is unsupported. That route cannot supply independent keyboard 0253 readback. Generic ACPI/peripheral transfers still do not establish a complete keyboard bridge.

## Watch out
The source admits only reviewed fields and exact targets, not frames, physical input maps, raw command access, driver installs or hardware validation. Thermal-only recovery must skip unrelated lighting queries on every backend. The added exact-model source pins and MCU instruction/consumer proofs supersede earlier initial-admission lighting exclusions; lexical Trail matches against those older limits are not evidence that this new source review copies another model's authority. RZ09-0330 diagnostics report EC 1.00; analysis of released 1.01 is not an attestation of installed bytes.
