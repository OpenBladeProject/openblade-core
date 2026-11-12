---
title: Recover Blade 14 lighting keys by packed firmware descriptor order
author: Jack
date: 2026-11-12
branch: feat/issue945-lighting-parity
touches:
  - Directory.Build.props
  - docs/experimental-blade-reference.md
  - docs/experimental-blade-support.md
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/KeyboardLightingSequence.cs
  - src/OpenBlade.Contracts/OpenBlade.Contracts.csproj
  - src/OpenBlade.Protocol/ExperimentalBladeLightingFrame.cs
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Animation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
---

## Decided
For issue #945, recover RZ09-0530/02C5's lighting key map from the pinned PI4110 MCU 1.04.00 image (SHA256 1ac3f05486d0bca5fbbb1a4a52b874943ad491a00afd298b8d6b857a49d98f11). Three packed 96x8 descriptor tables at AFE3, B313 and B63B are selected by the exhaustive firmware edition switch. Firmware lookup58C8 and renderer682C establish that record index, not its coordinate-looking bytes, is the 6x16 RGB address. Use only the 74 usage-to-slot pairs common to all variants while runtime region is unspecified; 71 have reviewed ordinary Windows scan bindings.

Reactive/Ripple and imported sequences may use that exact map on 02C5 only. Keep 029D/029F's grid effects and observed-only key effects separate. Import validation must use the model's own common-slot mask and keep container slots96..101 black. Raw Input admits only the exact02C5 host/model and source-reviewed MI00 keyboard or MI01 first keyboard collection; MI03 is vendor-defined despite its interface protocol.

Advance service IPC79 to80 for the new layout ID because old validators reject it. Keep protocol51's read-only projection exclusion; Rust's frozen51 client is unchanged. Deploy coordinated components, not a tray-only patch.

## Tried and ruled out
The recognizable6x22 HID tableBE44 belongs to startup translation and gives wrong lighting columns. Aligned table searches miss the odd-address packed descriptors. Space and ISO/Japanese exceptions invalidate record+2/+3 as command coordinates. Do not name country layouts from edition numbers, infer another model's map, admit non-keyboard collections, or claim physical color readback.

## Watch out
Source-table recovery and fake-session tests do not attest installed MCU bytes or physical output. Preserve whole-image and fixture hash pins, fresh per-row admission, complete baseline restoration, ambiguity pauses, bounded pulses/frames, and shared lifecycle drain. The existing exact-model custom transport remains authoritative; raw handoff flags do not justify invented prerequisite writes.
