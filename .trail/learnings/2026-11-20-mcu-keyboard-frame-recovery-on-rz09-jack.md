---
title: MCU keyboard frame recovery on RZ09-0301/0421/0423/0427
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
- RZ09-0301/0421/0423/0427 host frames (PR #988): a replaced live sampled owner (Ambient/Audio) keeps the journaled baseline; only a fault or a missing baseline recaptures it. The same bug existed on 02C7 and is fixed in the shared uniform branch.
- A replaced live MCU owner already holds mode 8, so the next sampled owner inherits UniformFrameActivated=1 and monitors its first sample instead of re-selecting.
- Faulted + pending journal: if a fresh 0F/82 read (after write admission) equals the journaled baseline in mode and parameters, record ExternallyRestoredToBaseline. A new host-frame owner may then adopt the pending record (journal.AdoptReconciledPending, original destination kept), and an owned restore still writes the flag-clearing 03/0A setter.
- Selection reads refused before reaching the device (Exhausted, QueuedCanceled, Disposed, ObjectDisposedException) throw BladeWriteException KnownNotApplied with FailurePhase LightingSelectionRead: no fault, no journal change; the animation loop skips the batch. In a replacement request the check runs before draining, so the current owner keeps running.
- Sampled owners check that the baseline effect is still shown before their first frame and before relinquish; a change stops without restoring.
- 03/0B staging-clear sentinel is row=start=end=0xFE (0301 handler 0xA6B6..0xA6CC), not FE in the RGB bytes.

## Tried and ruled out
- Clearing the journal with no write when the baseline is reported again: the 0301 review says a GPIO rising edge restores the saved records but leaves the custom flag and staging visible, so a cleared journal could leave a frozen frame with nothing to restore it.
- A service change for bounded faulted-renderer recovery: RecoverFaultedLightingRendererCoreAsync already consumes its candidate before writing; FailedLightingRecoveryDoesNotReArmUntilASuccessfulApply proves it.

## Watch out
- Editing a 2026-11-19 review JSON changes its pinned sha256 in 2026-11-13-complete-lighting-pass.json (CRLF working-tree bytes).
- Journal SavePending refuses an existing pending record not captured in this process, even when equal; use AdoptReconciledPending after a fresh matching read.
