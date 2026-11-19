---
title: Admit issue 949 and 2019 Pro 17 through exact thermal sources
author: Jack
date: 2026-11-12
branch: feat/laptop-support-949
touches:
  - AGENTS.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - docs/source-reviewed-blade-models.md
  - src/OpenBlade.Contracts/RazerBladeModelCatalog.cs
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - tests/OpenBlade.Contracts.Tests/RazerBladeModelCatalogTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeIdentityTests.cs
---

## Decided
Issue 949 adds exact RZ09-0409/0276 and 2019 RZ09-0287/0234 as source-reviewed profiles, with physical validation pending. Their own pinned Synapse sources admit thermal profile 1, Balanced raw 0, Custom raw 4, CPU/GPU levels 0..2; only RZ09-0409 admits CPU Boost 3. Keep fresh adapter/device/controller/session admission and independent readback.

## Tried and ruled out
Do not admit late-2019 RZ09-0314/024C from a product-guide name: its exact Windows system product remains unestablished. Do not inherit manual fan, lighting, battery, keyboard-driver, audio or cooling-pad native authority from the expanded model catalog. Catalog coverage review records NotEstablished for all seven existing artifacts on each new model.

## Watch out
Issue 949's Observer export has inventory and zero recorded control operations; it is identity evidence, not physical control proof. Trail check's lexical warnings on catalog counts and support prose do not represent a contract change: this work adds no accessory admission, raw commands, forced Balanced preparation or hardware validation claim. Existing exact admission and recovery policy remains authoritative.
