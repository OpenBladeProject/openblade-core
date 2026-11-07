---
title: Preserve Stop pause priority and distinguish waiting from owned faults
author: Jack
date: 2026-11-07
branch: feat/battery-saver-low-fan-validation
touches:
  - CHANGELOG.md
  - docs/so6120-stop-auto-guardian.md
  - src/OpenBlade.Contracts/FanStop.cs
  - src/OpenBlade.Contracts/IpcMessageCodec.cs
  - src/OpenBlade.Contracts/ServiceMessages.cs
  - src/OpenBlade.FanStop.Guardian/Program.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceAdmissionPolicies.cs
  - src/OpenBlade.FanStop.Guardian/So6120ServiceDutyPlatform.cs
  - src/OpenBlade.Service/BatteryAutomationPreferences.cs
  - src/OpenBlade.Service/BatteryConfigurationStore.cs
---

## Decided
Use one atomic eligibility record for automatic Stop, including generation, mode and pause reason. Enable/Resume captures it before waiting for the operation gate and can arm only if that exact record still holds. Every termination ingress advances the record even when already paused, so a concurrent pause cannot be overwritten. Disabled has an explicit state.

While no Stop is owned, missing/invalid/slow read-only telemetry and temporary admission failures reset the cool interval and keep waiting. Actual ownership recovery and uncertain/refused attempts retain their fences and pause re-entry. Pauses belong to explicit mode/profile intent and known upward power changes, not every internal full-profile reconciliation. Lower-power changes retain waiting intent.

## Tried and ruled out
Counting the current read's allowed acquisition duration as a missing-sample gap produced false exits. Keep each read's freshness bound and measure the uncovered interval before it starts. A real missing interval still ends owned Custom Stop and resets unowned entry; do not keep a hot owned Stop alive by repeatedly clearing its exit timers. The review's claim that normal contention rarely permits entry was not measured; no stale-time credit was added.

## Watch out
Guardian notifications reconcile existing ownership but cannot sample for automatic entry or start Stop. Stop temperature reads may borrow an ordinary active GPU reader's unexpired D0 session without extending its idle deadline; Stop-only reads release their session. UI summaries show confirmed policy, while drafts remain visible with an unsaved indication. Blocked policy/Resume requests must not claim that settings were saved. These fixes were checked after one read-only Claude review; do not claim a second Claude review or new physical validation.
