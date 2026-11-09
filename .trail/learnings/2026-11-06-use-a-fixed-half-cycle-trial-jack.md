---
title: Use a fixed half-cycle trial for SO6120 Auto startup
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/So6120SilentTrial.cs
  - src/OpenBlade.Capture/So6120SilentPwmCommand.cs
  - src/OpenBlade.Capture/So6120DirectDutyJournal.cs
  - docs/so6120-supervised-pwm-trial.md
---

## Decided
For the SO6120 developer experiment, add a separate fixed Balanced half-cycle target policy: floor of the captured cycle divided by two, identical targets for both fans, and each target strictly above its fresh actual PWM at every existing entry gate. At observed cycle183, targets91/91 admit the normal startup reading38/38. Targets remain immutable and the new policy is included in the approval hash. Keep the previous plus-four strict and bounded plans readable with unchanged hashes and semantics. The command sequence, exact controls, thermal and device admission, independent target confirmation, and Auto restoration remain unchanged.

## Tried and ruled out
Two attended runs captured zero PWM, staged4/4 and enabled F0.6, then stopped before FE after Automatic started the fans. The flexible run still observed0/0 in two post-F0 snapshots before38/38. Both runs independently restored their owned permission and staging and closed cleanly. Widening small jitter tolerance did not cover this state transition; do not repeat another tiny-increment trial expecting a different result or silently rebase its approved targets.

## Watch out
This is a separately approved larger absolute increase, not permission to execute under an old one-shot approval. Half-cycle describes raw counters, not measured electrical duty or RPM. Equality or a live reading above the target still refuses entry; the intent is no decrease. Direct FE and commanded fan stop remain physically unverified until a successful new attended result. Preserve consumed markers and records; no extra setting commands, retries, forced profile or manual-RPM baseline are needed.
