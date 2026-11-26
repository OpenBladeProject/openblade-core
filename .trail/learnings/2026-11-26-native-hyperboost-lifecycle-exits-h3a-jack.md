---
title: Native HyperBoost lifecycle exits (H3a)
author: Jack
date: 2026-11-26
branch: feat/native-hyperboost-lifecycle
---

## Decided
- Native HyperBoost ownership is derived from the active thermal journal tail (`thermal_history::hyperboost_owned`, `Storage::owns_hyperboost`): a HyperBoost row settled Confirmed with no later performance row Confirmed for another destination and no later HyperBoost row KnownNotSubmitted. No new state file.
- Firmware that already left HyperBoost is settled read-only by appending a HyperBoost row 0 then 6 under the exit's step id (`Storage::settle_hyperboost`), after `Tracker::own_reading` marks the fresh reading OpenBlade's own (managed ConfirmIntentional). Without the drift marking the battery step that follows is excluded as external drift.
- The battery-boundary exit runs in `reconcile_locked` before the steps when an unscoped reconciliation's classes hold Battery; an unsettled exit stops the reconciliation (`Reconciled::exit_unsettled`).
- Handback option (b): no new handback item. Managed adopts nothing; a native session left active reads as external HyperBoost to managed.
- Without service-profile-triggers-v1 no HyperBoost probes are composed, so nothing enters.

## Tried and ruled out
- A Stop exit under the ordinary write capture before `normal_stop`: SCM STOP/SHUTDOWN already revoke the loaded origin in `scm_host::installed_notice`, before `drain_by` runs, so every Fresh capture fails at Stop. The rig's `rig.stop()` does not call `rig.origin.stop()`, so a Stop test without it passes against code that is dead in production.
- A low-power exit through the ordinary write capture: the ordinary epoch is fenced once the gate sets. Stop and low-power exits both need a reviewed cleanup permit (only the keyboard release and fan handback hold the cleanup admission today).
- Refusing rollover while HyperBoost is owned (the fan-owner precedent): the exit's own Prepared row would need that rollover, so it deadlocks near segment capacity.

## Watch out
- Firmware selector 0 is Balanced and 7 is HyperBoost; selector 1 is no decoded state, and an entry from it replies only the generic CurrentAdmission status 2.
- The fixture's `sent` counts reads too; count `writes` to prove nothing was submitted.
- `reconcile_locked` prepends the exit to its step list for the aggregate; convergence `outcomes` skip it by offset.
- Native has no display-off signal at all; the power-setting reader only reports DisplayOn.
