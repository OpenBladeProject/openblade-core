---
title: Cooling pad stuck at full speed was a rejected 0D/81 overshoot
author: Jack
date: 2026-11-26
branch: docs/cooling-pad-learnings
---

## Decided
- The cooling pad staying at full speed on RZ09-0581 + RC21-0230 had a second cause besides the emergency limit. The 0D/81 ramping-speed reply passes the 3200 RPM maximum target: the logged reply was `01 05 41` (65 = 3250 RPM).
  - TargetQuery accepted only `[1,5,<=64]`, so the read failed and retired the session.
  - The reconnect's baseline read then failed again. Control stopped and left the last 3200 demand running.
- PR #1016 logs the rejected bytes in DescribeFailure, accepts `[1,5,any]`, and maps a speed above 64 to no target (null). The contract, the state store and baseline restore all bound targets to 0..3200, so widening the validator alone would only move the failure. Shipped in stable 0.77.1.
- This corrects the earlier same-day learning "Cooling pad full speed while idle is the emergency limit". The emergency limit explains a burst that releases on its own; a pad that stays at 3200 after the CPU cools was this rejection.

## Tried and ruled out
- Guessing the decoder bound before seeing bytes. The HID session already kept RejectedPayloadHex for PayloadValidator rejections, and logging it gave the answer at the next failure.
- Clamping 3250 to 3200 as a target. A ramp reading is not a confirmed target, so null is the honest value.

## Watch out
- A rejected read during maintenance is caught as transient only while the session is unusable. The recovery path re-applies through ApplyCoreAsync, whose own baseline read can fail the same way and stop control without handing the fan back. If another reply shape fails, expect the same "stuck at last demand" symptom.
- To read the bytes, export diagnostics and grep logs.jsonl for `payload `.
- A broker LocalDev build (65535.x) blocks the stable installer. Uninstall it first through the Package Cache `/uninstall`; saved profiles are kept.
