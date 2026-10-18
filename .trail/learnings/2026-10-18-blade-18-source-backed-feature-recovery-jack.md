---
title: Blade 18 source-backed feature recovery and sequence ownership
author: Jack
date: 2026-10-18
branch: feat/blade18-observer-support
touches:
  - CHANGELOG.md
  - docs/blade18-spatial-lighting.md
  - docs/experimental-blade-reference.md
  - docs/experimental-blade-support.md
  - docs/keyboard-lighting-file-format.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/zh-CN/experimental-blade-support.md
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/KeyboardLightingSequence.cs
---

## Decided
Blade 18 Product 672 source definitions admit linked fan controls, host Gaming Mode, regional lighting projection and the existing SET-only frame transport. Keep host-rendered color intent distinct from firmware readback. Layout-aware sequence validation must use the exact model mask; retain black padding in the shared 102-slot container.

## Tried and ruled out
The middleware useMWManifest path selects installer resources rather than dynamic feature flags. A generic SDK method is not exact-model feature admission. The signed PID02A0 .73 driver package exists, but internal callback similarity does not establish the public SnapTap interception ABI. Blade 18 (2023) has legacy THX hardware/software integration; the newer rznb11/rznb12 ABI cannot be assumed. Host function-primary remapping currently consumes external keyboard function keys before device classification, so it is not admitted.

## Watch out
A slow intermediate sequence submission can cross the end timestamp. Decide whether another frame is needed from the rendered timestamp, so a non-looping animation still submits and holds its final frame. New capability fields require old-peer read-only projection exclusions and an IPC golden update. No physical hardware validation is implied by source recovery or offline tests.
