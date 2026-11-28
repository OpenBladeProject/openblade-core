---
title: Native request triggers honour the low-power gate
author: Jack
date: 2026-11-22
branch: feat/native-trigger-low-power-gate
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/request_triggers.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/origin_lifetime.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/request_triggers_low_power.rs
  - docs/windows-native-thermal-mutation.md
---

## Decided

- The native request triggers (`request_triggers.rs`) read the low-power gate
  only to classify a refusal that per-write admission already made
  (`fresh.authorize()`, a step's capture and authorization,
  `WindowsGuard::authorize_for` against `epoch_for(Ordinary)`). The gate never
  admits a write. `Runtime::in_low_power` wraps
  `Lifecycle::low_power_or_unknown`, so an exhausted or unreadable lifecycle
  counts as low power. Managed's gate has no unknown state.
- The gate is checked at four points: after a report's entry refusal (only
  for a changed pair on classified power, which is where managed reaches
  `ApplyFullTransitionAsync`), after a save's entry refusal when the store is
  not fenced, before each step, and after a step that refused before anything
  was possibly submitted.
- Replies come from managed and are pinned in
  `tests/data/low-power-trigger-vectors.json`, recorded by
  `ServiceControlRuntimeTests.NativeLowPowerTriggerVectors`:
  - A report: Blocked "...recorded. Lighting intent was stored and will be
    applied after standby."
  - A save: Stored "Profiles were stored. Lighting intent ...".
  - A deferred step: Blocked "The laptop entered a low-power transition
    before the thermal command was submitted."
  - A renewal or inactive report: unchanged.
- A deferral keeps the debt owed with its attempts unchanged
  (`Reconciled::deferred`, `Reconciled::spent`). Renewals every 5 s in low
  power therefore read no firmware and never exhaust `RETRIES`.
- Resume divergence: managed retries deferred thermal intent after resume
  only on a power-source change. Native retries it on the next report after
  the gate clears. C3's hook is to run `reconcile_locked` for each
  `Debt::Owed` pair when the gate clears.

## Tried and ruled out

- Widening `WindowsGuard::capture` to let a low-power report or save through
  to the deferral reply. Production capture already refuses with the
  lifecycle inactive (`thermal_native.rs` `capture`, `active().ok_or_else`).
  Such a request replies "Saved configuration or the current session is
  unavailable: ...", reads nothing, keeps the prior pair, and does not store
  the save. The deferral replies are reached only when the gate sets after
  capture. Changing the shared capture is out of scope.
- Explicit `setPerformanceOverride` and `setFanOverride` in low power hit the
  same capture refusal. They submit nothing, as managed does, but they store
  no override and give the admission reply, whereas managed stores the
  override and replies Blocked. This is a reported gap, not fixed here.

## Watch out

- The rig's `scoped_fresh` (`CapturedFresh`) models capture only. Its steps
  fail admission because it has no `authorize_owned_stop`, so turn it off
  before asserting that a reconciliation applies.
- `Runtime::native` wiring of the probe is `cfg(not(test))` and no test
  exercises it. Tests wire the rig lifecycle with `Runtime::read_low_power`.
- `Lifecycle::exhaust` is test-only under the triggers feature. Without the
  feature gate, the profiles-lane clippy fails on dead code.
- In the fixture, a suspend at an adapter reading makes the step fail its
  next admission before it can classify the reading. A reading-time suspend
  therefore always defers. To test a gate set after a step finished, use a
  counting `read_low_power` probe, not an `on_power` hook.
- The post-step guards `!unchanged` and `!possible` survive mutation. An
  unchanged step is followed by a step whose pre-step check gives the same
  deferral. No status-2 step is possibly submitted today. Keep `!possible`
  anyway: reclassifying a possibly submitted step would hide it from
  `submitted` and allow a retry that replays it.
