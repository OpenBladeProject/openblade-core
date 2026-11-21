---
title: "RZ09-0529 Vapor Chamber review fixes: selection record, fresh reads, keyboard-only fallback"
author: Jack
date: 2026-11-20
branch: feat/rz09-0529-vapor-chamber-frames
touches:
  - docs/rz09-0529-lighting-expansion.md
  - evidence/source-reviewed-blades/2026-11-13-complete-lighting-pass.json
  - evidence/source-reviewed-blades/2026-11-13-rz09-0529-keyboard-frames.json
  - src/OpenBlade.Protocol/ExperimentalBladeFactory.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Animation.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.GridLighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.Lighting.cs
  - src/OpenBlade.Protocol/ExperimentalBladePort.McuLighting.cs
  - src/OpenBlade.Protocol/ExperimentalGridLightingEffect.cs
  - src/OpenBlade.Protocol/ExperimentalLightingBaselineJournal.cs
---

## Decided
- The RZ09-0529 Vapor Chamber (LED38) row-6 mapping follows Synapse: transport column 0 black, columns 1..18 carry logical VC 0..17. KI8110 1.07.00 stores transport columns 0..17 in VC slots 0..17 and drops 18, so VC slot 0 shows the black column and logical column 17 is dropped, exactly as with Synapse's own frames. Physical validation only decides whether that is visible; it is not an open mapping question. The evidence keeps the reviewed `FF,6,0,17,RGB*18` record and adds `openBladeRowArguments` beside it.
- The journal's `VaporChamber` member is a write-ahead selection record, not a capture of LED38: it is written before `[0,38,8]` can be submitted and removed after a confirmed restore or when LED38 no longer reads mode 8. Only that record lets a restore write LED38, so a keyboard-only owner never overwrites another controller's mode-8 frame.
- LED38 is taken only from a read under the same gate hold: mode 8 under the record, or equal to the destination first observed for the keyboard baseline. Ambient Awareness and Audio Meter decide in SetLightingAsync but select on their first sample, so WriteUniformFrameCoreAsync re-reads LED38 before activating.
- Only effects that drive LED38 read it (not Reactive, Sequence or built-ins). A failed optional read on a usable session degrades to keyboard-only; a poisoning read fails before writes, and the factory caches the negative result per (host evidence, HID identity) after one open-time probe plus the existing reopen-once path.
- VC frame colours are scaled by the keyboard brightness percent in software; OpenBlade writes no LED38 brightness.

## Tried and ruled out
- Rolling back the LED38 first-observation on a refused replacement: only animated owners lack an observation, and their failed replacement faults, so the rollback was an equivalent mutant and was removed.
- Testing duplicate suppression through Ripple: the geometry cannot make keyboard-equal, VC-different frames, so the comparison is the pure `ExperimentalBladePort.IsRepeatedKeyTriggeredFrame` and is unit-tested.

## Watch out
- A restore-readback test that leaves LED38 in mode 8 passes even with the confirmation check deleted (the null `actual` throws later). Read back a different restorable record to prove the check.
- Native-payload tests fail with category 107 when the build version differs from the copied payload; build with `-p:OpenBladeVersion=0.65.1` while the payload is 0.65.1.
- Removing `ClearOwnedFrame`'s `VaporChamberFrames = false` is an equivalent mutant today (every host op calls the selector before the flag is read); it stays as a defensive reset.
