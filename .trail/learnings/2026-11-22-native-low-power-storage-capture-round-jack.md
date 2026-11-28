---
title: native low-power storage capture round 2
author: Jack
date: 2026-11-22
branch: feat/native-low-power-intent-capture
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
  - tools/openblade-windows-host/src/windows/scm/installed/request_triggers.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - docs/windows-native-thermal-mutation.md
---

Supersedes 2026-11-22-native-low-power-intent-storage-capture-jack.md on the AC rationale, and records the round-2 changes to the native low-power storage capture.

## Decided

- The AC rationale in the earlier learning is wrong. Managed does not store against "the adapter it last read". Production Windows reports AC to managed only as `ExternalUnknown`, so managed samples the adapter from firmware while its gate is set. It stores when that reading classifies, and otherwise stores nothing and replies with the same `UNCLASSIFIED_BLOCKED` native gives. The recorded `pluggedIn` vector rows come from a test double that classifies the adapter. The real difference is that native declines any firmware read in low power. That fails closed and is kept.
- A second kept difference: after a transition on AC, an unplug once the gate is set, and then an explicit override, managed's interrupted `ApplyFullTransitionAsync` stores nothing (ServiceControlRuntime.cs:14483-14489, 16360-16364). Native stores at the Windows battery reading and owes the debt. Storing is not a write, and the debt is applied later only through a fresh capture and every per-write admission.
- An explicit request the gate overtakes after its own store becomes the deferral, with managed's deferral reply and `owe_stored_intent`. This happens only when `Runtime::overtaken_by_gate` holds: the gate moved since the mark taken after the request's own write authorization, `submission_possible` is false, and the store is not pending. It is the same rule `reconcile_locked` uses for steps, through the shared `submission_possible` helper. Performance applies it only when the final authorization fails; fans apply it only to an unconfirmed result.
- A fan store reads everything its save leaves first (`Storage::plan_fan_save`), then saves and owes together. A profile `fanControl` with `fixedSpeedRpm` 5500 passes the typed decoder but fails `validate_settings`, so a clear to it stores nothing, both awake and in low power.
- The guard is split. `Identity` holds the supplier checks. `WindowsGuard` is `{ identity, lifecycle: u64 }`, `StorageGuard(Identity)`, and `SettleGuard(WindowsGuard)`. An unpinned write guard cannot be built, which removes the old `self.lifecycle?` path (mutant W01) structurally.
- `ReadCustody::authorize_storage` returns `bool`. No caller used the generation.

## Tried and ruled out

- Taking the gate mark before the capture. It adds a probe read to every reconciliation step and explicit request, and it broke the probe-count tests. The mark is taken after the request's own write authorization, and only for explicit requests.
- A unit test for `Identity::admits` storage admission (mutant W02). It calls the Windows suppliers directly (console session, WTS session state, the user token, `GetSystemPowerStatus`, native controller presence), and there is no injection seam. The rig models it in `Custody::authorize_storage`, which now also refuses unknown Windows power.

## Watch out

- On battery, a fan request has no native recipe and is never confirmed. A gate generation that moves during it relabels it as a deferral even without a real suspend. This is benign: nothing was submitted, and the debt only runs a reconciliation.
- `CapturedFresh` now reports `power_generation`, so a test can keep `scoped_fresh` on through a resume.
