---
title: Fold Stop sections and add opt-in entry below 50 C
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - src/OpenBlade.Tray/MainWindow.xaml
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - src/OpenBlade.Service/ServiceControlRuntime.FanStop.cs
---

## Decided

The user accepted the Stop subcard design, then requested independently foldable submenus and automatic Stop after a continuous cool period. Keep End Stop and Automatic Stop as expandable subcards of Stop fans. The user explicitly selected below 50 C for automatic entry, applied to CPU and GPU; both must remain below their individual limits. A 30-second wait is still a proposed default, not an explicit user-selected duration. Keep all values editable, retain Auto versus Custom end behavior, and keep automatic entry opt-in.

## Tried and ruled out

The earlier proposal of 45 C for automatic entry was replaced by the user's 50 C choice. Do not reuse the long exit-delay warning for a long cool-entry wait: delaying fan restart and delaying fan stop have different effects. Collapsing a section must not change its enabled state or forget settings. The updated disconnected preview is artifacts/fan-stop-end-design-20261107-v2/index.html. Production source and hardware remain unchanged.

## Watch out

The proposed start limits must stay below the corresponding Custom exit limits to avoid immediate cycling. Entry uses one continuous both-cool timer; exit uses independent per-sensor hot timers. Explicit manual Auto pauses automatic re-entry until Resume, and Resume only restarts fresh observation. Thermal Auto may rearm after a new full cool interval when automation is enabled. This requested opt-in feature supersedes the earlier design's lack of automatic re-entry. Preserve fresh per-entry admission and ownership/ambiguity fences; do not retry unresolved operations or let a new start timer gate mandatory Auto cleanup. Treat a sleeping GPU separately from failed telemetry. Sensor semantics and lifecycle rearm details need implementation review; the mockup is not physical validation or live-test authorization.
