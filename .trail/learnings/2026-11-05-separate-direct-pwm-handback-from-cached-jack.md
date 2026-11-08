---
title: Separate direct PWM handback from cached Automatic selection
author: Jack
date: 2026-11-05
branch: feat/battery-saver-low-fan-validation
touches:
  - docs/so6120-direct-duty-safety.md
  - docs/so6120-ec-register-semantics.md
  - tools/firmware/README.md
  - tools/firmware/profiles/so6120-ec-1.04.json
  - tools/firmware/profiles/so6120-mcu-1.07.json
  - docs/so6120-direct-pwm-handback.md
---

## Decided

An explicitly applied normal selector and confirmed Automatic fan state can leave SO6120 FE direct duty. Silent5 is a direct destination; do not invent a Balanced or different-profile prerequisite. EC158AE..158B6 permits normal PWM feedback once the selector is not FE, even if override80425bit6 remains set. Actual fan rotation can remain zero under normal Automatic policy.

Orderly handback, exact experiment-state cleanup, and recovery when the host cannot issue a command are distinct. Normal thermal commands leave override flags, staged duty and generic access state behind. Bit6 also holds the non-fan80493 contributor. An unattended switch requires independent cooling recovery; an attended experiment needs a justified abort/recovery procedure and bounded exposure, not proof of an unattended watchdog merely to establish its ordinary exit.

## Tried and ruled out

The current GUI is not a guaranteed raw-FE recovery mechanism: typed pre-reads reject FE, and a matching or already-Automatic cached state can make a setter return without submission. Keep production admission closed rather than weakening the decoder to make a speculative escape work.

Normal MCU SET/GET share cache. Pending mode/manual reads can replace queued destination bytes before writes; SET preserves those pending read bits. EC index36 can also stage rather than apply a byte when its existing bit7 is set. Mailbox5/status4 schedules policy but does not prove the separate Automatic-flag write or settled PWM. These are conditional static paths, not measured failures.

## Watch out

Independent EC confirmation is still required, separately from cached target echoes, actual PWM and tach. Preserve unchanged raw map/table evidence and exact-image scope. Do not write old sampled PWM over resumed Automatic control. Existing RPM-test approvals do not authorize register entry or cleanup. See docs/so6120-direct-pwm-handback.md and the exact-image explorer findings; no live write path was added.
