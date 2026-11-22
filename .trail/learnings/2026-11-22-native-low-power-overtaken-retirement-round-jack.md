---
title: native low-power overtaken retirement round 3
author: Jack
date: 2026-11-22
branch: feat/native-low-power-intent-capture
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - docs/windows-native-thermal-mutation.md
---

Supersedes 2026-11-22-native-low-power-storage-capture-round-jack.md on the overtaken performance request. Its claim that such a request becomes the deferral did not hold in production until this change.

## Decided

- `Storage::record` settles a Prepared-only row as KnownNotSubmitted through `Files::replace_retired`, as the fan and battery journals already did. Every other performance append still uses a plain `replace`. In production a plain journal append reaches `ReadLease::journal_replace`, which calls `authorize()`. That needs `lifecycle.active()` and an unretired lease. So the retirement used to fail whenever the gate was set, or once a stop had begun. The store then quarantined and stayed pending, `overtaken_by_gate` never held, and the user got "lost caller or owner admission" with no debt. Reconcile steps and shutdown-time retirements had the same defect.
- The main test rig now reaches its journal the way production does, through `CustodialMemory` in readiness_tests/mutation.rs:
  - a reservation or plain journal append needs `ReadCustody::authorize`;
  - a cleanup append needs `authorize_owned_stop`;
  - a retirement needs intact custody, no pending stage, and the exact retirement row, checked by `validate_prepared_retirement` under the owner the lease opened with.
- The fan relabel tests the reply it would replace (`outcome.status == 2`), not the pre-outcome status. A clear Stored (0) on unclassified power keeps its reply.

## Tried and ruled out

- Checking the retirement owner against the live `Custody::generation`. Tests change that value to model a fresh-proof owner change, but production binds the owner when the lease opens. `battery_v1_fresh_power_controller_owner_changes_never_submit` failed for that reason only.

## Watch out

- A test that passes only because rig storage accepts any journal write proves nothing about production. `MemoryFiles` used directly (`Storage::open(Box::new(files))`, as restart tests do) still has no custody model.
- `edge_shutdown_publishes_actual_retirement_after_one_failed_settlement` is deterministic evidence of the stop path. Without the fix, the retirement never reaches the journal.
- `tfc_v1_near_capacity_performance_refuses_before_spending_reserved_source_cancel` failed once in a full lane run and passed alone. It is load-sensitive, not affected by this change.
- The fan relabel still turns a Blocked "could not be handed back" clear (an owned change outstanding on unclassified power) into a deferral when the gate generation moves. With the gate set first, the same request gets `FAN_HANDBACK_OWED` and stores nothing.
