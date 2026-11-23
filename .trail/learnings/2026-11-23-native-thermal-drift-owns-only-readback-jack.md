---
title: Native thermal drift owns only readback-confirmed state
author: Jack
date: 2026-11-23
branch: feat/native-thermal-drift-tracker
---

This supersedes "Native thermal drift tracker commits prior intent only after a settled reconciliation" (d801affd) where they disagree: drift no longer compares a "last committed intent"; it compares OpenBlade's own readback-confirmed state.

## Decided
- Own state is per subsystem (`thermal_drift::Own`) and set only by a readback: a step's or explicit request's confirming readback (thermal and fan post-result), a fan no-op readback and the destination it carries (`matched`, no more hard-coded `matched: true`), a reconciliation reading that shows the new intent, and a reading that settles a resume hold. Saved intent never becomes own state.
- Fan intent with no native recipe is `Fans::Unplanned` and projected onto the fans firmware reports (managed `ResolveExperimentalFanHardwareDesired`, SCR 19616-19636): Automatic fans read as the intent, manual fans still latch.
- With no own state for a subsystem only the new intent is compared; with neither (a GPU-incomplete intent gives `None`) the reading is no evidence and the latch stays.
- A setter without its own readback (a step dropped at its bound or deadline, or failed after its journaled intent) is settled by the next reading through `Tracker::submitted` / `submitted_fans`, set right after each journaled intent. The unsettled journal row still fences writes; the fresh reading does not settle the row.
- Startup is per pair (`Runtime.started`): a pair is starting until a debt of it is Settled through `owe`, including the competing-controller drop. Only the starting pair's reconciliation or an activation runs Overwrite.
- Lease expiry and a report over own preset run WithPrior and apply: a deliberate divergence from managed (`leaseExpired`, `otherReportedWithOwnPreset` keep the managed rows; the tests pin native's outcome).
- The resume window is 60 s from the last gate boundary (kept on the tick); a power change in it, in low power or after the resume, arms the hold, stamped with the reconciliation's runtime `Instant::now()`. settle has no controller term.

## Tried and ruled out
- Owning at the worker's `record(Confirmed)` / `record_fan(30|50)`: after a drop at the bound the thermal permit's checkpoint and `record` fail before Confirmed, so the hook never fires, and the fan one only fires when its authorize happens to still pass (flaky). The intent-time expectation replaced both hooks.
- Settling the dropped row by the fresh read: native keeps the row unsettled; only drift ownership is decided by that read.
- Replaying `powerToBatteryWithStopFanDrift` with fixture fan byte 4: that byte is undecodable (observation unavailable). Firmware Stop has the manual bit, so native reads it as manual fans (published fans 3).

## Watch out
- A held_write does not model "write landed, step dropped": the worker stops after the held setter. Use a blocking `before_write` sleep past `RECONCILE_BUDGET + RECONCILE_MARGIN`: the recipe runs on paused time, so its own deadline is not spent.
- Native publishes the plugged-in profile's desired preset and source on AC even while an application preset applies, so the row helper compares desired only for power-profile rows; every native manual-fan reading publishes fans 3.
- Two near-equivalent mutants survive by design: removing `observe(*readback)` before `own_performance` in the thermal post-result (own_performance writes the same baseline), and narrowing the `submitted` hook to the first intent (every intent of one step carries the same destination, and the serialized Reader admits no reading between them).
- A sed or perl edit anchored on `assert!(!rig.storage.pending());` hits several tests; edit test files with the Edit tool or an exact single-occurrence replace.
