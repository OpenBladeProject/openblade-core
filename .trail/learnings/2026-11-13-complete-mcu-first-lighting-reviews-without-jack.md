---
title: Complete MCU-first lighting reviews without losing field-only intent
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
  - src/OpenBlade.Contracts/Profiles.cs
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.IndependentLighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
---

## Decided
Run a complete lighting pass per exact model and feature. Start with keyboard MCU evidence, including firmware embedded in updater resources; use pinned OpenRazer/OpenRGB definitions when an exact keyboard image is unavailable. The 22-model ledger records brightness, logo, effects, frames, input maps, ownership and restoration separately. RZ09-0301's resource-embedded0245image closes its effect-getter gap with its own active GET0 and legacy setters; it does not establish0330commands.

Seven MCU paths expose independently confirmed random/single/dual Breathing and both Wave directions. Four legacy setters copy saved brightness into active, requiring a fresh active brightness read. Static is admitted only where active consumers use the returned RGB:028A/02B7have hidden palette selectors and stay unavailable. Modern same-count RGB changes need a confirmed count-0 bridge; KI8100 Static needs a bridge, KI8110 does not. Vendor left/right mapping for the new paths is wire1/2, not the earlier unreleased guess.

## Tried and ruled out
Do not treat a SET acknowledgment, daemon cache, stored RGB fields or descriptor bounds as full lighting confirmation. Inspect active renderer consumers. Custom8can be recognized as unknown pixels and replaced directly by a reviewed builtin; it cannot confirm a submitted builtin or establish a frame restoration baseline. No other unknown mode or malformed payload is accepted. BIOS/EC generic transfers and the0330RazerECF003echo do not establish keyboard0253readback.

Do not reinterpret old0301unmanaged profiles when effect support becomes available. Explicit ControlsKeyboardEffect intent is separate from firmware state; independent fields retain priority. Old profile/config25 and override8values migrate without optin to26/9. Strict codec admission requires serviceIPC81; display-onlySessionIPC2and frozenRust51discovery remain separate.

## Watch out
Keep exact-device admission and a fresh guard at every bridge and destination. Field-only recovery observes independently readable fields without requiring unrelated effect state; different obligations widen to All and never narrow another fault. Source review remains separate from physical validation. Existing descriptor candidates for029D/029Fstill need auxiliary routing and Windows input bindings. New frame bounds do not automatically admit frames or per-key maps. Share pure software effects only through each backend's own reviewed geometry, projection, transport and semantic admission; never borrow another model's capture oracle.
