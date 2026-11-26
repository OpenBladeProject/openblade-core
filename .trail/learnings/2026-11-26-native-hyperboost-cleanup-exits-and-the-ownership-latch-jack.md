---
title: Native HyperBoost cleanup exits and the ownership latch (extends the H3a learning)
author: Jack
date: 2026-11-26
branch: feat/native-hyperboost-lifecycle
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/hyperboost_cleanup.rs
  - tools/openblade-windows-host/src/windows/scm/installed/hyperboost_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_drift.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/low_power_cleanup.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - docs/windows-native-thermal-mutation.md
---

This extends `2026-11-26-native-hyperboost-lifecycle-exits-h3a-jack.md`. Its "Stop and low-power exits both need a reviewed cleanup permit" is now built, and its ownership rule gains a latch. Where the two disagree, this one holds.

## Decided

- Owned HyperBoost leaves at Stop, shutdown and a confirmed suspend under the Service's cleanup admission (`hyperboost_cleanup.rs`, `ExitPermit`), the third permit that holds `Cleanup` beside the keyboard release and the fan handback.
- The cleanup exit writes only managed's unowned exit, the Balanced restore pair. It resolves no saved intent, as the fan handback applies no saved destination, and AGENTS.md has a shutdown or low-power cleanup reach the firmware handoff directly. After a suspend the entering session owes its reconciliation (`owe_stored_intent`), which the tick after the resume runs from the Balanced the exit left as OpenBlade's own. A resume boundary owes nothing by itself, so without that debt the saved intent would not come back.
- Order: keyboard release, fan handback (Stop only), then HyperBoost. Managed exits HyperBoost first because it has separate budgets. Native has one deadline, and an exit stopped after a setter leaves an unsettled row at which the keyboard release (`Release::Unsettled`) and the handback both refuse.
- The lane paces a cleanup performance permit before its Prepared row (`Pace::covers_before_rows` in `Reader::apply`), so a recipe that cannot fit writes no row.
- A cleanup never rolls the journal over: `Storage::record_cleanup` checks that the chain fits the active segment and does not call custody's `journal_reserve`, which needs the ordinary admission.
- Rows are journaled for the session that entered (`Storage::hyperboost_owner`), through the latest captured store (`Runtime::captured`, moved off the keyboard owner). The history refuses a row with session 0 or no user SID, so the Service cannot journal as itself.
- Ownership counts only while no reading since the entry showed firmware out of HyperBoost (`Tracker::left_hyperboost`, set in `Tracker::observe`, cleared by `own_performance(HyperBoost)`; `Runtime::owns_hyperboost`). An explicit exit that replies "HyperBoost is already off." sets it too. The journal is settled at the next admitted write: the explicit exit's capture, the battery boundary, or the Stop or suspend cleanup.
- A boundary exit whose own reading finds firmware out is settled whether or not the journal row can be written, so the transition goes on and its steps name the real cause (an outstanding fan change), not "HyperBoost is already off."
- The cleanup's read-only settle does not adopt the reading as OpenBlade's own: no transition follows it, and managed only retires its journal there.
- `ExitPermit::resolve` takes the firmware-already-out branch before its power checks. The settle sends no setter, so unclassified power, or a battery reading under Windows AC, must not refuse it: refused, the journal kept the entry and the next start exited a HyperBoost entered outside OpenBlade. The power checks guard only the exit's setters.
- An entry another user than the latest capture's made is refused and fails the Stop; only a process with no captured store succeeds silently.

## Tried and ruled out

- Restoring the saved destination under the cleanup admission: it needs saved-intent resolution, a Windows power generation for temporaries and longer recipes inside a 1.5 s window, and no existing cleanup resolves intent.
- Checking `verify_retained_failure` before knowing there is an exit to make: it failed `edge_original_owner_integrity_loss_after_retirement_never_reopens_reads`, as it did for `capture_cleanup`.
- Binding the cleanup exit to the custody generation that entered: the boundary exit is not bound either, and the row's user is the binding the journal needs.

## Watch out

- A test Stop must call `rig.origin.stop()` first. Without it the drain takes the origin-loss branch. A rig that ends with HyperBoost owned uses `Rig::stop_in_hyperboost`.
- `latency` in the fixture applies to keyboard exchanges too. For "no time" beside the keyboard use a spent drain deadline, not a slow device.
- `Custody::refuse_at` counts ordinary authorizations only. To lose the cleanup admission inside a job, set `custody.controller` from the fixture's `on_power` hook.
- A println that holds `rig.port.state.lock()` while awaiting `rig.get()` deadlocks the test.
- An excluded reconciliation publishes no confirmed state. Compare a rig with its control only after a fresh `observeState`.
- `driver_mode::tests::the_compiled_identity_ends_with_the_refused_marker` strips the markers appended after the keyboard's; a new marker after it needs a line there.
- Latch lost at a restart: if the earlier process saw HyperBoost off and could not settle the journal, a HyperBoost active at the new process's first reading reads as owned.
