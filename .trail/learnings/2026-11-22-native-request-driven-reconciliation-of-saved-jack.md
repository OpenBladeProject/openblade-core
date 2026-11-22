---
title: Native request-driven reconciliation of saved intent (RZ09-0581 B2)
author: Jack
date: 2026-11-22
branch: feat/native-profile-request-triggers
---

## Decided

- Native request-driven reconciliation (RZ09-0581, Stage 4b B2) covers three triggers: a foreground report that changes the active pair (`Runtime::application`), the first admitted report per `(sid, session)` since the Service started, and a profile save that changes the active intent (`update_profiles`). All three go through `Runtime::reconcile_locked`, which applies performance and fans only; battery, lighting and managed's lighting restore are left for B3.
- `reconcile_locked` runs under the already-held mutation gate (a non-reentrant tokio Mutex), so it calls `apply_locked` and `apply_fan_locked` instead of the public setters.
- Reconcile mode is a field on the existing permits: `RequestPermit.reconcile: bool` and `FanPermit.reconcile: Option<PriorManual>`. In reconcile mode the permits skip the override validation and store nothing, and unclassified power blocks.
- Replies go through `operation_status`, which reproduces managed `ToOperationResult`. Fans go first via `fans_first`, which reproduces `DeviceSubsystemOrder.ForTransition`. Both are pinned against the recorded managed vectors in `tests/data/request-trigger-vectors.json` (regenerate with OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS).
- Native fan ordering reads saved prior manual intent, or `outstanding_fan`, captured before the trigger. Managed reads confirmed fans.
- Startup is marked before it runs. A report that is not admitted (unknown power, a pending journal, or `authorize` refusing) does not consume it.

## Tried and ruled out

- Using `status_query_power` as the class to compare: the native status base reports 3 for anything that is not battery, so reconcile never ran on AC. `reconcile_classes` returns battery as [1], AC as [0, 2] and unknown as [] instead. On AC, a save is compared at both external classes; the step at the unchanged class confirms without writing.
- Reading `save_intent` at a hypothetical class without protection: storage reads at a class retire temporaries saved for another class. `save_intent` now saves and restores `(temporary, temporary_fan)` around `save_intent_at`, and a test pins it (mutant M5).

## Watch out

- Reports now reconcile, so existing readiness tests that report Game.exe use `report_game`, which reports with unknown Windows power (`port.windows_on_battery.replace(None)`) so that nothing is applied. A test that wants a reconcile must set the power to known.
- A framed request from a caller whose `control_session` is None is refused at the pipe (PermissionDenied) before dispatch, so the managed "requires the active local Windows session" reply cannot be reached through the rig. The test compares the constant `APPLICATION_SESSION_REQUIRED` instead.
- The mutation fixture's default fans are manual, so the fans-first path defers unless the test sets the selectors and fans to 0. Confirmed Automatic fans read back as 3, not 1.
- A report whose profile read fails replies Blocked "Saved configuration or the current session is unavailable: ..."; managed would reply Stored. A pid mismatch for the same instance is Blocked "Application context belongs to another reporter."
