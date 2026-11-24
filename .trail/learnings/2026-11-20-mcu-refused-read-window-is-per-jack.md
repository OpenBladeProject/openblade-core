---
title: MCU refused-read window is per owner and consecutive
author: Jack
date: 2026-11-20
branch: feat/mcu-frames-0301-0421-0423-0427
---

## Decided
- Refines the 2026-11-20 MCU frame learning: the refused-read bound needs consecutive refusals for the current owner. It faults only after ReviewedMcuSelectionDeferralLimitCount (100) refused 0F/82 reads that also span ReviewedMcuSelectionDeferralLimit (5 s).
- The window resets in ReadReviewedMcuEffectPayloadAsync on any answer from that register (frame, restore and entry reads) and in SetLightingOwnership on every owner change. Isolated refusals far apart never fault a sampled owner.
- ExperimentalBladeFactory.LightingOwnership is internal so factory tests can assert McuCustomFrameMayRemain through the real journal load.

## Tried and ruled out
- A time-only window: a refusal 60 s after an earlier owner's refusal, or two pushes 10 s apart, faulted at once.
- Resetting in both the selection reader and the payload reader: removing either goes undetected by tests. Keep the reset only in the payload reader.

## Watch out
- The uniform push path rewraps the skipped-frame BladeWriteException without its LightingSelectionRead phase; tests tell skip from fault by message there.
- Removing the SetLightingOwnership reset alone survives the tests, because release reads 0F/82 and that read resets the window too. It is kept as defence.
