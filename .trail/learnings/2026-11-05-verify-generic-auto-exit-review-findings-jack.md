---
title: Verify generic Auto exit review findings before live binding
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/DirectDutyAutomaticExit.cs
  - tests/OpenBlade.Capture.Tests/DirectDutyAutomaticExitTests.cs
  - docs/so6120-direct-pwm-handback.md
---

## Decided

Claude Opus 5.5 high reviewed generic Auto exit commit bf3f3a2b against parent 34592eba. Codex checked the exact diff and reproduced error paths with an ignored offline console probe linked to unchanged coordinator source. These are open review findings, not fixes or hardware validation.

Separate host-only legacy GpuClockOffsets from independent Custom readback. Preserve submission uncertainty when ExitStopped rejects post-ack readback. Reject captured invalid manual flags before acquiring the one-shot claim. Acquisition and DisposeAsync failures need distinct provenance rather than being labeled observation failures. A dispose failure must not erase the preceding hardware outcome, but still prevents claiming overall successful session release.

## Tried and ruled out

Do not automatically accept every advisory claim. The MCU command queues both mode and manual-flag work; a later worker pass does not establish that the second Auto channel is required to finish the first command. The port promises a settled independent observation. FanHandbackConfirmed intentionally records a historical independent observation even if its subsequent journal confirmation fails; CleanupConfirmed and Success remain false. The probe reproduced that intended behavior.

## Watch out

The five implementation findings remain unresolved. Single-use claims also block reuse after zero submissions by deliberate contract; define durable read-only reconciliation before any live adapter rather than weakening replay protection. The known generic MCU route cannot freshly read the bank after closing its gate. Custom level and maximum-fan adjustments need separately reviewed typed operations; a fake copying the desired record is not a physical proof. No live adapter exists. Claude's completed native review reported final-file rather than full-diff coverage and an internal finding cap, so do not call it exhaustive. See ignored artifacts/claude-autoexit-review-20261105.txt for all ten claims and Codex dispositions.
