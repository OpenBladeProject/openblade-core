---
title: RZ09-0529 Vapor Chamber host frames and restore contract
author: Jack
date: 2026-11-20
branch: feat/rz09-0529-vapor-chamber-frames
cost: { minutes: 30, tokens: 204347 }
touches:
  - docs/experimental-blade-support.md
  - docs/rz09-0529-lighting-expansion.md
  - docs/source-reviewed-lighting.md
  - evidence/source-reviewed-blades/2026-11-13-complete-lighting-pass.json
  - evidence/source-reviewed-blades/2026-11-13-rz09-0529-keyboard-frames.json
  - src/OpenBlade.Protocol/ExperimentalBladeModels.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Animation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.GridLighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.McuLighting.cs
---

## Decided
- RZ09-0529 (02C7) host frames drive the Vapor Chamber (MCU LED38) only when its 0F/82 region-38 baseline decodes to Off, Static, Breathing (count 0/1/2) or Wave. Spectrum, Starlight, Custom, mode 9 or inconsistent parameters keep frames keyboard-only with no region-38 write.
- The VC baseline is an optional `VaporChamber` member of ExperimentalLightingBaseline. The journal writes schema 1 without it (byte-compatible) and schema 2 with it; decode requires the schema to match presence.
- VC row is transport row 6 in the keyboard row encoding: column 0 black, columns 1..18 carry logical VC 0..17 (Synapse manifest addresses). Activation `[0,38,8]` follows `[0,5,8]` and must read back mode 8.
- Restore only while the VC reads mode 8; any other mode means another controller changed it, so leave it.
- Reactive and imported/per-key sequences stay keyboard-only and restore the VC first.

## Tried and ruled out
- An Off bridge before restoring same-count Breathing: restores always start from mode 8, so the same-mode setter shortcut never applies. A direct restore write is enough.

## Watch out
- The KI8110 review says the firmware copies row-6 transport columns 0..17 into the VC buffer and drops 18. Following Synapse addresses therefore leaves VC LED 0 black and never shows logical column 17. Only physical validation settles which is right; the mapping is isolated in Rz090529KeyboardLightingFrame.EncodeVaporChamberRow.
- An older service treats a schema 2 journal as invalid and blocks lighting until repair.
- complete-lighting-pass.json pins the SHA-256 of the CRLF working-tree bytes of each proof file; editing a proof needs the pin refreshed.
