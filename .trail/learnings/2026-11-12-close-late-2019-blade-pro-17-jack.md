---
title: Close late 2019 Blade Pro 17 identity gap with primary owner report
author: Jack
date: 2026-11-12
branch: feat/laptop-support-949
touches:
  - AGENTS.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - docs/experimental-blade-support.md
  - docs/source-reviewed-blade-models.md
  - src/OpenBlade.Contracts/RazerBladeModelCatalog.cs
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - tests/OpenBlade.Contracts.Tests/RazerBladeModelCatalogTests.cs
---

## Decided
A further primary-source search closes the late-2019 RZ09-0314 identity gap from the earlier issue-949 learning. The owner's 2021-09-24 Manjaro inxi report pairs exact SMBIOS product Blade Pro 17 (Fall 2019) with USB 1532:024C in the same report. Product 588's exact Razer guide names RZ09-0314. Admit this exact product/HID pair with its own pinned Synapse definitions, separately from RZ09-0287/0234.

## Tried and ruled out
Do not infer Fall 2019 from the stale Synapse early/late support URL. The original pinned linuxhw DMI tree did not establish this variant; the separate owner report now supplies the missing product/PID evidence. Do not inherit a sibling's command source.

## Watch out
This new identity evidence supersedes the earlier pending-identity decision only. It grants no physical-validation status, driver/audio stack, manual fans, lighting, battery controls or accessory admission. CPU/GPU levels remain0..2, thermal profile1, raw Balanced0/Custom4, guarded Automatic fan handback only.
