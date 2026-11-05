---
title: Keep Battery Saver fan stop separate from normal fan intent
author: Jack
date: 2026-11-04
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - Directory.Build.props
  - README.md
  - README.ru-RU.md
  - README.zh-CN.md
  - docs/battery-saver-low-fan-validation.md
  - docs/fan-power-source-policy.md
  - src/OpenBlade.Contracts/DeviceState.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/Profiles.cs
---

## Decided
RZ09-0581 Battery Saver fan stop is a separate default-off battery-profile preference, using the physically observed 1500 RPM target rather than widening ordinary fixed ranges. Preserve current normal fan intent and overrides; derive Stop only for exact capability, physical Battery and effective Battery Saver. Paired mode and both stored targets independently confirm application; tach readings remain separate. Other models and Silent are not admitted by matching command geometry. Profile schema 23 and service IPC 73 require coordinated full installation; product version becomes 0.51.0.

## Tried and ruled out
Do not send 0 or 100 RPM, force a 2000 RPM preparation, reset performance to Balanced, expose Stop as a normal fan mode, or copy physical results to other models. Do not rely on the ordinary Advisory session for native stop safety: the factory supplies fresh exact-host/controller admission, composed with fresh physical power, scope and <=2s CPU<=55C/confirmed GPU-inactive evidence. Capture stop context before awaiting and retain the fresh hardware guard through queued Auto/fixed restoration, without requiring stop cooling conditions for cleanup.

## Watch out
Only a successful service fan write establishes ownership; a matching observation after restart is unowned. Explicit OFF may restore current normal intent from independently confirmed unowned Stop. Warm unowned state pauses control and needs explicit OFF; do not claim automatic cooling handback occurred. Owned stop releases to firmware Automatic on unsafe sensors and latches suspension until scope/rearm. Low-power cleanup is Automatic-only, generation-bound, after guarded keyboard release, with normal/performance low-power writes still denied. Stop ambiguity halts writes rather than replaying or trying another sequence. The product integration and sleep/resume have software validation, not physical installed-product validation. The Fans row uses shared neutral styles and text-stack padding for multiline helper text; retain normal picker state separately from operational Stop.

Trail check's lexical matches on the existing developer wrapper do not establish a semantic conflict: the spinning-baseline evidence is separate from the initial already-zero baseline, ambiguous 100 RPM was not replayed, and restoration was separately admitted after read-only reconciliation. Keep all historical records intact.
