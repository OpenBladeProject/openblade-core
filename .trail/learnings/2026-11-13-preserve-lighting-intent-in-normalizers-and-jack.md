---
title: Preserve lighting intent in normalizers and exports
author: Jack
date: 2026-11-13
branch: fix/issue959-lighting
touches:
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/source-reviewed-lighting.md
  - docs/source-reviewed-rz09-0301.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/Lighting.cs
  - src/OpenBlade.Contracts/Profiles.cs
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.IndependentLighting.cs
---

## Decided
Explicit keyboard-effect intent is metadata, separate from firmware readback. Preserve it through every normalization, copy and persisted envelope. Canonicalize effect/color/brightness/parameters by cloning the original LightingSettings, rather than reconstructing the record and accidentally dropping metadata. On a hybrid backend an explicit legacy-shaped Off must remain a full Off request, not become unmanaged.

## Tried and ruled out
Checking only the current typed factory is insufficient: parameterless legacy Off and invisible-effect reconstruction paths can erase a newly added property. Cover the actual service dispatch after normalization, not only record equality. Hardware comparisons and frozen legacy read-only status projection intentionally strip request-only metadata; desired state and profile persistence must retain it.

Do not version only service configuration and overrides when a shared settings member changes. Profile exports also serialize LightingSettings and need their own schema boundary. Old profile/config25, override8 and export16 documents migrate without optin; new schemas26/9/17 preserve explicit intent. Strict unknown-member IPC parsing requires service81, while display-onlySession2 and frozen Rust51 discovery stay separate.

## Watch out
Independent fields take priority over a retained effect marker. Missing intent in an older profile is not permission to start controlling keyboard effects when MCU support is added. Keep full-effect selection explicit, field-only recovery scoped, queued stale field commits canceled, and readback free of fabricated intent. A new metadata field needs a complete serialization/normalization/persistence trace across managed and experimental backends before publishing.
