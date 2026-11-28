---
title: "Native low-power trigger deferral, review corrections"
author: Jack
date: 2026-11-22
branch: feat/native-trigger-low-power-gate
touches:
  - docs/windows-native-thermal-mutation.md
---

This supersedes parts of
`2026-11-22-native-request-triggers-honour-the-low-jack.md` after a review
refuted them. Each superseded statement is quoted.

## Decided

- Supersedes "The gate is checked at four points ... after a step that
  refused before anything was possibly submitted" and
  "`Runtime::in_low_power` wraps `Lifecycle::low_power_or_unknown`". The
  probe now returns `Lifecycle::low_power()` (`Option<Transition>`).
  `Runtime::gate` reads it as `Gate::Clear(generation)` or `Gate::Low`; a
  `None` or suspending transition is Low.
  - Before each step the gate is read once (`before`). That check is the
    refusal itself, not a classification.
  - After the step, a refused (status 2) step is relabelled as a deferral
    when `self.gate() != before`: the generation moved, so a suspend counts
    even when a resume has already cleared it inside the step. It must also
    not be possibly submitted, not unchanged, and the store not fenced.
  - The unfinished arm (bound reached) applies the same test.
  - Reading the bool at the end missed a suspend plus resume inside one
    step and dropped the debt.
- Supersedes "A deferral keeps the debt owed with its attempts unchanged".
  The claim held only when every earlier step was retryable. `after()` used
  `self.retry && self.deferred`, so a possibly submitted first step (status
  2 or 3) made `retry` false and settled the debt, and the deferred fans
  were never applied.
  - `after()` is now `if self.deferred`, and the save path uses `retry ||
    deferred`.
  - This is safe: a deferral implies the store was not fenced (the pending
    check precedes the gate check). The perf step after resume re-reads
    firmware and confirms without a setter, so it journals nothing.
- Supersedes "Replies come from managed and are pinned ...", read as
  production parity. In production, `WindowsGuard::capture`
  (`thermal_native.rs:86-90`) refuses first.
  - The reply is "Saved configuration or the current session is
    unavailable: fresh installed mutation admission is unavailable".
    "permission denied" is only the rig factory's wording.
  - The entry deferral replies are reachable only when the gate sets
    between capture and the entry `authorize()`.
  - The real effect is mid-flight: the pre-step check and the post-step
    generation check.
  - The vector replay covers only that window, because the rig's default
    `Fresh` (`Custody`) does not pin the lifecycle at capture.
    `production_capture_rows_in_low_power_are_refused_at_capture` pins the
    capture-time reply.
- Supersedes "Resume divergence: managed retries deferred thermal intent
  after resume only on a power-source change". Under the default
  `ThermalControlPolicy.MaintainDesiredState`
  (`BatteryAutomationPreferences.cs:18-19`), managed reconverges after
  resume. The path is:
  - `ServiceSessionPowerTransitionCoordinator.cs:334`;
  - `HandlePowerSourceChangedAsync` (`ServiceControlRuntime.cs:12321`);
  - `ObserveCoreAsync`;
  - `EvaluateThermalConvergenceObservationAsync` (`:17599`, `:19943`);
  - repeated by the 15 s periodic loop.

  Only Strict waits for a power-source change. Native's next-report retry,
  and C3's resume reconcile later, cover MaintainDesiredState.
- The queued follow-up slice, not this branch, is a storage-only capture
  that keeps per-write admission. Without it, a low-power explicit override
  or a save is not stored, and a refused report re-sends at the next 5 s
  lease tick.

## Tried and ruled out

- A reason-carrying `Fresh::low_power()` instead of the runtime closure.
  It needs the rig's `Custody` impl in `readiness_tests/mutation.rs`, which
  this work may not edit, so the closure seam stays. Its production wiring
  is still `cfg(not(test))`.

## Watch out

- Supersedes "a reading-time suspend therefore always defers. To test a
  gate set after a step finished, use a counting probe, not an `on_power`
  hook". The fixture's `on_mutation_retired` hook sets the gate after a
  step's chain has run, so it reaches the paths `on_power` cannot:
  - the Confirmed row before the last admission check (a status-2, possibly
    submitted step);
  - an unchanged last step.
- Supersedes "The post-step guards `!unchanged` and `!possible` survive
  mutation ... No status-2 step is possibly submitted today". A status-2
  step can be possibly submitted:
  - "Thermal result lost caller or owner admission; submission evidence is
    retained".
  - `a_deferral_after_a_possibly_submitted_step_keeps_the_debt` pins
    `!possible`.
  - `a_suspend_after_an_unchanged_last_step_is_not_a_deferral` pins
    `!unchanged`.
  - Only the save's `!pending` remains an accepted equivalent.
- A relabelled step's own refusal is not reported for that reconciliation.
  The synthetic deferred step no longer counts toward `ran`.
- Four mutants of the final tree survive as equivalents:
  - the save's `!pending`: the save path refuses a fenced store first;
  - the unfinished arm's `!possible` and `!storage.pending()`: a held write
    past the bound leaves both possible and pending, so either one alone
    keeps the guard;
  - `ran`'s `!deferred`: the synthetic deferred step is Blocked, so
    `applied` and `unchanged` are already false.
