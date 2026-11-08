---
title: Validate Mid 2019 identity and constant Blade thermal zones
author: Jack
date: 2026-11-07
branch: feat/blade-firmware-model-support
touches:
  - AGENTS.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/source-reviewed-blades-2019-2020.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Contracts/RazerBladeModelCatalog.cs
---

## Decided
RZ09-0301 Mid 2019 Advanced is source-reviewed as literal Windows product Blade paired with fresh exact Razer 1532:0245 admission. Vendor product 581 explicitly includes ordinary and Mercury editions; exact CPU/GPU Custom selectors use 0D/87 and 0D/07 with profile 1, selectors 1/2 and unchanged levels 0-2. Native GPU DLL clock effects remain separate. The old issue 758 ZIP is cached pre-HID identity provenance, not a physical control validation.

The exact RZ09-0581 BIOS 4.01 and RZ09-0528 BIOS 2.02 TZRZ._TMP definitions both return literal 0x0C6E. The retained live 0581 DSDT agrees. Remove both named PDH sources from shared CPU-temperature admission: successful PDH status and a plausible 45 C value do not establish dynamic CPU temperature. Missing CPU input retains the model-admitted curve maximum (0581 5400; 0528 5000). Preserve generic temperature conversion, including legitimate 45 C, and GPU inactivity/session behavior.

## Tried and ruled out
Do not substitute 0240 Mercury, 023A Early 2019 or 023B 2018 Base identities. Product 581's copied 2020 master-guide link is stale identity evidence. Independently verified actual product UI forwards GPU wattage levels 0-2 unchanged, without 25/35/45 W conversion. Matching updater names and SSID/SKU cases establish package provenance, not new runtime command authority. The distinct 0528 EC architecture does not inherit 0581 offsets or fan tables.

## Watch out
Physical validation remains separate from official source-reviewed support. Read all seven driver-catalog artifacts for a new model and refresh the immutable core source coverage before release. The bounded 0528 BIOS/EC review does not reconstruct its EC thermal/battery/keyboard command chains. No live PDH sample, overheating outcome, physical fan response or hardware operation was observed in this task. Trail check's broad keyword matches about OpenBlade, physical validation, driver packages and function-row scope are false positives for these changes: no Fn inversion, native GPU/driver activation, borrowed command or physical-validation promotion is introduced. Detailed evidence is in docs/bios-ec-source-validation.md and the two sanitized admission records.
