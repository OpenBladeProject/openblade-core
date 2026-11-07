---
title: Design Auto and Custom end conditions as Stop subcards
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - docs/so6120-stop-auto-guardian.md
---

## Decided

The user wants Stop end conditions in Auto and Custom modes, with the current conservative thermal rules retained as Auto. Custom should use user-defined CPU and GPU temperature thresholds and a user-defined continuous exceedance duration; GPU activity alone must not end Custom Stop. Warn for high values (60 C was the user's example) and long delays. The user explicitly wants these settings as subcards of the Stop card, and an inspectable UI design before production UI implementation.

## Tried and ruled out

Do not place the custom controls as unrelated siblings in the ordinary Fans card. The disconnected preview instead gives Stop its own parent card, with End conditions followed by CPU, GPU and duration subcards. Auto hides the custom fields while retaining their values. Proposed defaults are 55 C for each sensor and 5 seconds, with a proposed duration warning above 10 seconds; those numbers are not approved firmware operating limits. Preview artifacts are under artifacts/fan-stop-end-design-20261107. Production code and hardware are unchanged.

## Watch out

Use an independent continuous timer per sensor; reset it at or below the threshold, and do not combine alternating CPU/GPU excursions. GPU sleep is different from missing temperature data for an active GPU. Custom only replaces thermal decision rules: retain session, power-transition, controller, freshness, ownership and recovery handling. A native ambiguity still fences writes, not a blind Auto replay. Apply future thermal semantics consistently to entry and continuation. Preserve child ownership in the Stop card's reorder group and reuse shared transparent subcard styles. UI design approval is still pending.
