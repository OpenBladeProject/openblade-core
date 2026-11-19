---
title: Admit exact Blade 14 RGB rows before extending lighting effects
author: Jack
date: 2026-11-12
branch: feat/issue945-lighting-parity
touches:
  - Directory.Build.props
  - docs/experimental-blade-reference.md
  - docs/experimental-blade-support.md
  - src/OpenBlade.Protocol/ExperimentalBladeLightingFrame.cs
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Animation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeDarkWakeRecoveryTests.cs
  - tests/OpenBlade.Protocol.Tests/ExperimentalBladeLightingTests.cs
---

## Decided
Issue #945 follow-up admits Static and sampled uniform effects on exact 029D/02C5 through OpenRazer 477f4d6d's independently declared 6x16 matrix contracts. Those row requests declare 70 bytes (52 meaningful, 18 zero padding) and use serialized acknowledged FF transactions; retain 029F/02A0's separately established 52-byte contracts. Shared software grid patterns also benefit 029F without borrowing another model's captures, masks or physical-validation status.

Host frames require a complete built-in restoration baseline and preserve exact raw brightness. Stage dark-wake content first. Every row needs fresh admission; after one row succeeds, a later guard refusal is still a partial frame and must pause rather than defer or replay. Recovery retains unknown RGB and a new request must establish its baseline.

## Tried and ruled out
Do not widen Product 672's SET-only batch transport to these models, use a 52-byte declared row on the new Blade 14 paths, call retained intent RGB readback, or infer a physical input-to-light map from 6x16 bounds. Procedural Fire/Wheel/Tidal are explicit software patterns, not captured vendor output.

## Watch out
The 02C5 MCU has a recognizable 6x22 HID scan table. It is not the 6x16 lighting map; trace the active descriptor and row-address path before admitting Reactive, Ripple or per-key imports. Firmware/source evidence is separate from installed-MCU attestation and physical output. Old recovery tests that assumed these models could never own RGB frames must follow the existing unknown-frame contract while preserving fresh malformed-baseline refusal before writes.
