---
title: MCU frame review corrections on RZ09-0301/0421/0423/0427
author: Jack
date: 2026-11-20
branch: feat/mcu-frames-0301-0421-0423-0427
touches:
  - docs/experimental-blade-support.md
  - docs/ru-RU/experimental-blade-support.md
  - docs/source-reviewed-lighting.md
  - docs/source-reviewed-rz09-0301.md
  - docs/zh-CN/experimental-blade-support.md
  - evidence/source-reviewed-blades/2026-11-13-complete-lighting-pass.json
  - evidence/source-reviewed-blades/2026-11-19-rz09-0301-keyboard-frames.json
  - evidence/source-reviewed-blades/2026-11-19-rz09-0421-keyboard-frames.json
  - evidence/source-reviewed-blades/2026-11-19-rz09-0423-keyboard-frames.json
  - evidence/source-reviewed-blades/2026-11-19-rz09-0427-keyboard-frames.json
---

## Decided
- RZ09-0301/0421/0423/0427 (PR #988): ExperimentalLightingOwnershipState.McuCustomFrameMayRemain marks that mode 8 may have been selected for the current baseline. It is set just before every 0F/02 [0,5,8] and by the factory for a journal loaded from an earlier run. A fault keeps it, and only ClearOwnedFrame clears it. While it is set, a restore always writes the flag-clearing 03/0A setter, even for a sampled owner that never sent a frame. UniformFrameActivated is not enough: SetLightingFaulted zeroes it.
- Replacing any owner that holds a journaled baseline does an admitted 0F/82 read before draining. The read requires mode 8 if this owner selected it, otherwise the journaled baseline effect. An external change stops the request without a write.
- Refused selection reads skip frames for at most ReviewedMcuSelectionDeferralLimit (5 s). After that the renderer faults as KnownNotApplied, without a write. A read that may have reached the device (Timeout, Poisoned, NativeIoFailure, ProtocolFailure) faults at once.
- An invalid journal blocks every lighting change on these four models, built-in effects included, as it already does on RZ09-0529. RequireLightingJournalAdmission(hostFrameRequest: false) runs first for every request.

## Tried and ruled out
- Correction to my 2026-11-20 commit e2ebc6fd and the PR #988 comment: they said the sampled-baseline fix "also fixes RZ09-0529". That is wrong. Main kept the baseline (Baseline ??= hostBaseline); the recapture bug existed only inside this branch.
- Correction to 2026-11-20-mcu-keyboard-frames-on-rz09-0301-jack.md: IsRestorable is not what refuses a custom mode 8 or a mode 5/6/7 baseline. Mode 8 decodes to a null baseline ("A readable pre-frame lighting baseline is required for host rendering"). Modes 5/6/7 fail in the MCU decoder (DeviceStateContentRejectedException). Only Spectrum reaches the IsRestorable refusal.

## Watch out
- The VaporChamberClock starts at timestamp 0, so a deferral timestamp needs a nullable sentinel, not 0.
- Bash heredocs containing an apostrophe abort silently. Write test snippets with the Write tool and splice them in with perl.
