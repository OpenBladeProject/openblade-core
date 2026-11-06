---
title: Bind supervised SO6120 trials without widening control authority
author: Jack
date: 2026-11-06
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Capture/So6120SilentTrial.cs
  - src/OpenBlade.Capture/So6120SilentPwmCommand.cs
  - src/OpenBlade.Capture/So6120WindowsDirectDutyPlatform.cs
  - docs/so6120-supervised-pwm-trial.md
---

## Decided
A fixed developer CLI now binds the SO6120 host-mailbox components to one attended Silent/Automatic increase: derive actual PWM plus four raw counts per fan from one explicitly unplanned baseline, require headroom, approve the immutable plan separately, then return directly to the captured normal profile and restore owned flags/staging. Version-two journals bind that plan before A1; fixed-target version-one inspection remains compatible. A same-run Windows platform verifies stopped OpenBlade owners, current exact metadata and scoped configuration, pins the reviewed driver file, and opens the existing device once. A named semaphore excludes this runner's duplicates; a retained create-once machine latch prevents changing output directories to bypass a prior attempt.

## Tried and ruled out
Do not use dummy zero targets before baseline, recalculate an approved target, force a preparatory RPM, infer host tach units from the separate HID response, or treat PWM equality as mechanical proof. Normal Auto actual-PWM drift can reject an entry without inventing ambiguous control ownership; preserve explicit cleanup of an already confirmed prefix. A Windows Console.In ReadLineAsync is not a cancellable console wait, so the fixed approval uses an independently bounded wait and never starts another reader after timeout. Non-admission temperature reads must not delay Auto while FE is active; optional observation belongs after confirmed restoration. Late cancellation finishes admitted confirmation/handback but cannot report trial success.

## Watch out
This supersedes earlier temporary notes saying no live constructor exists, not their hardware limits. Preparation, passing offline tests and accepted finite contention risks do not establish exclusion, physical fan control or stalled-command recovery. F0 permission exposure begins before FE; synchronous native/storage stalls exceed software bounds. Ambiguity fences every mailbox command including A0; no finally cleanup, kill/reopen, replay or guessed GUI Auto. Auto may naturally produce zero RPM. Keep default CLI and launcher preparation-only, pin the complete candidate/manifest, observe actual child exit/CloseHandle, and obtain separate attended execution approval. Trail lexical matches against prior Silent/Auto/ownership exclusions are not new permission: these exclusions remain and the production fan-stop capability stays disabled.
