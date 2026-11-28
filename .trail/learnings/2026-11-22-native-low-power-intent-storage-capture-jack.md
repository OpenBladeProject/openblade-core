---
title: Native low-power intent storage capture
author: Jack
date: 2026-11-22
branch: feat/native-low-power-intent-capture
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_native.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/request_triggers.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/request_triggers_low_power.rs
  - docs/windows-native-thermal-mutation.md
---

This supersedes parts of
`2026-11-22-native-request-triggers-honour-the-low-jack.md` and
`2026-11-22-native-low-power-trigger-deferral-review-jack.md`. Each
superseded statement is quoted.

## Decided

- Supersedes "Widening `WindowsGuard::capture` to let a low-power report or
  save through to the deferral reply" (ruled out there) and "The queued
  follow-up slice ... is a storage-only capture". The write capture is not
  widened. A separate storage-only capture is added beside it:
  - `WindowsGuard::capture_storage` returns a `StorageGuard` whose
    `authorize()` is always `None`.
  - The write capture now builds through `WindowsGuard::identified`, then
    pins the lifecycle, so every non-lifecycle check runs once, in
    `WindowsGuard::admits`. `authorize_for` refuses without a pinned
    lifecycle (`self.lifecycle?`).
  - The custody side is `ReadCustody::authorize_storage`, which is
    `authorize` without the lifecycle epoch. In `mailbox_read.rs` the only
    change is that method on the trait, `GuardedReadCustody` and `ReadLease`,
    plus `authorize_original` made generic over its proof.
  - `Runtime::capture_intent` takes it only when the write capture refused
    non-fatally and the gate reads set. Reconciliation steps always take the
    write capture.
- Supersedes "Explicit `setPerformanceOverride` and `setFanOverride` in low
  power ... This is a reported gap, not fixed here". Explicit overrides now
  store their intent in low power, as managed does
  (`store_performance_deferred`, `store_fan_deferred`). The pair owes
  `Debt::owed(Classes::ALL)` with zero attempts, and the next report after
  the gate clears applies it once. The replies are managed's, pinned by the
  battery rows of `low-power-trigger-vectors.json`.
  - Fan Automatic from saved manual intent gets managed's
    `ApplyDirectAutomaticFanTransitionOutcomeAsync` prefix: "Fans could not
    return to Automatic with the saved {preset} performance intent." Managed
    refuses at the performance step's gate before its Custom capability
    check, so Custom also gets the prefix.
- Supersedes "`production_capture_rows_in_low_power_are_refused_at_capture`
  pins the capture-time reply". That test is now
  `production_capture_rows_in_low_power_match_the_managed_vectors`. Under
  `CapturedFresh` with `CapturedStorage`, a report or save in low power gets
  managed's deferral reply.

## Tried and ruled out

- Storing an explicit override on AC in low power. Native classifies the
  external adapter only from a fresh reading, which the gate refuses.
  Managed stores against the adapter it last read. Native replies
  `UNCLASSIFIED_BLOCKED` and stores nothing, with managed's status and
  setters. Only Windows battery is classified without a reading
  (`triggers::deferred_store_refusal`).
- Storing a fan request while `Storage::outstanding_fan()` holds. The
  handback it owes needs a write, and continuity is proved only by a write
  admission. It replies `FAN_HANDBACK_OWED` and stores nothing.

## Watch out

- Production `WindowsGuard::capture_storage` and `StorageGuard` are
  compile-only in tests. The rig models them as `CapturedStorage`. Mutants
  in the production guard are not killable by the readiness tests.
- The logon-identity check is modelled only as recorded at capture. No rig
  hook changes the logon between capture and `authorize_storage`.
- The managed recorder's `--no-build` run reads a stale copy of the vectors
  from bin. Rebuild before comparing.
- Three mutants are observably equivalent in the rig, and each sits behind
  another guard:
  - A reconciliation step taking `capture_intent`: the storage guard's
    `authorize` is `None`, and the store branch requires
    `reconcile.is_none()`.
  - `CapturedStorage::authorize` delegating to the custody: the storage
    capture is reached only after a lifecycle refusal.
  - Dropping the stores' own `profile_layer(..).at(BATTERY)` check: the
    validation before it already refuses an invalid saved set.
- A step that carries no override (`value: None`) would clear the saved
  override if it ever reached the store. The
  `a_*step_refused_at_its_own_admission_in_low_power_stores_nothing` tests
  pin that it does not.
