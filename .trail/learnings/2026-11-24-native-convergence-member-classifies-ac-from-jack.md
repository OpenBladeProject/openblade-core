---
title: Native convergence member classifies AC from the last step; only configuration fences quarantine the policy
author: Jack
date: 2026-11-24
branch: feat/native-thermal-control-policy
touches:
  - ".github/workflows/rust.yml"
  - docs/windows-native-thermal-mutation.md
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage_policy_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/profile_layer_lighting.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/thermal_policy.rs
---

## Decided

- Supersedes the AC and absent-member watch-outs of 2026-11-24-native-thermal-control-policy-and-convergence-jack.md.
- On AC the convergence member uses the class this user's own last step classified (`Storage::observe_power`), but only while the Windows power generation is unchanged (`convergence_power`, `Storage::observed_power`). A kind change, a battery reading or an unclassified reading leaves the coarse class 3, which reads PowerUnsettled. Full parity needs managed's periodic 07/8C read in the status job, which is a separate slice.
- Only the member moves to the classified class. The status's own saved intent (`status_query_power`, desiredState) stays at the coarse class, because `installed_temporary_clear_and_application_precedence_use_saved_canonical_profiles` and `thermal_presets` pin that coarse metadata claims no resolved application policy.
- Only a configuration fence quarantines the policy: a preserved aggregate, `configuration_failed` or `configuration_changed` (`configuration_quarantined`). A journal or battery fence publishes Paused/PendingReconciliation, and a policy change is still stored.
- A non-fatal store read error in `save_thermal_policy` replies Failed. Fatal admission evidence still ends the Service.
- Policy requests run under RECIPE_BUDGET because they wait for the operation gate. Views that do not carry the member skip its store reads.

## Tried and ruled out

- Changing `status_query_power` itself to return the classified class. That moved desiredState in every lane and broke the two coarse-metadata tests above.
- A persistent preferences-read fault for the fatal arm test. The status snapshot that runs before dispatch reads the retained policy first, so the arm survived. The fault is now one-shot, on a fresh rig with no retained projection.

## Watch out

- The member is absent, never Paused/LowPower, whenever the projection's write capture is refused: low power, a locked session, an unknown AC line, a controller or a fence held by another user, and a preserved aggregate. The projection that a fan request retains pins its epoch, so it goes absent at the next power or session notice.
- A swap between full and limited adapters under unchanged AC presence is not seen until the next step.
- RECIPE_BUDGET (6 s) does not cover a wait behind a full reconciliation (RECONCILE_BUDGET).
