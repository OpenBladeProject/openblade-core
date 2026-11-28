---
title: Native battery cancel retries after an unsubmitted fence; corrects the Nov 22 deferral learning
author: Jack
date: 2026-11-22
branch: fix/native-tfc-cancel-epoch-race
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/status_source.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/temporary_charge_release.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - docs/windows-native-thermal-mutation.md
---

## Decided
- This corrects the 2026-11-22 learning "Native battery cancel defers when a later notice fences its tick". That fix closed only the authorize before the claim. A fence between the claim and the worker's setter still paused the cancel.
- `battery_source_event` now rereads the lifecycle epoch through a `current_epoch` callback (`OriginLifetime::power_epoch`, passed in by `status_source`). Every guard pins the epoch it captured and refuses once the epoch moves. A refused capture or authorize therefore counts as `Superseded` when the epoch is no longer the tick's, including low power where there is no epoch. It counts as `Refused` (claim once and pause) only at the tick's epoch: a competing controller, a session or identity mismatch, or a lost logon.
- Post-claim: when the attempt is fenced and `BatteryPermit::known_unsubmitted` holds, `restore_fenced_claim` puts the owner back, un-abandons it, and restores the taken intent. The next tick then retries. If the attempt already read the charge off, the retry confirms that read without a setter (progress 73). `known_unsubmitted` requires all of the following:
  - the worker joined unconfirmed;
  - the storage is not pending;
  - either nothing was Prepared, or the journal ends at the Prepared-only retirement (progress 6).
  Progress 70 (submission intent) is never restored.
- The retry reuses the owner's reserved cancel IDs. The journal validator (`openblade-domain/src/thermal_history.rs:372-386`) binds an origin cancel's ids to the enable's `cancel_ids`, and fresh IDs fail with "Thermal history is unresolved or unsupported." An attempt proven unsubmitted can have spent only getter IDs.
- The claim no longer uses `debug_assert!`. If the claimed owner is not the peeked one, the code puts it back and returns `Ok(None)` without writing, in release builds as well.
- Window map on the scoped fixture (`custody.scoped_fresh` plus `notice_skip`): 40 authorization positions in one tick, the same for a second unplug, a session notice and a suspend.
  - 28 are unsubmitted and write the cancel exactly once on a later tick, or after resume for a suspend. 2 are before the claim and 26 after it, 11 of those after a Prepared-only retirement.
  - 2 have intent journaled (progress 70 written, then the setter's pre-send checkpoint refused). They write nothing and stay pending reconciliation.
  - 10 are submitted and never replayed.
- A competing controller at each of the same 40 positions, with no notice (`refuse_at`), pauses the 28 unsubmitted positions once and never retries any position. That test is what kills restoring without the fence check.

## Tried and ruled out
- Fresh cancel IDs on retry (`reserved_ids: None`): the journal rejects them (see above).
- The old learning, line 16: Battery publication does not clear `storage.temporary_desired()`. The displayed false after a deferral came from the stale proof. The window tests assert `temporary_desired()` is still set after an unsubmitted fence, so a deferred or restored cancel keeps desired TFC until it writes.
- The old learning, line 25: a refusal during a notice, including a suspend inside the authorize, now defers instead of pausing. Only a refusal at the tick's epoch pauses.
- The 93235940 commit message said 5 of 6 new tests fail on the old code. The sixth (`..._refused_authorization_during_a_notice_still_pauses_once`) pinned the lossy pause and has been replaced.

## Watch out
- Mutants of the `known_unsubmitted` clauses survive, singly and in pairs (joined, confirmed, pending, Prepared implies 6). Later gates hide them: a restored attempt with intent journaled meets the pending-storage admission gate, and a restored confirmed one causes no read or write in the fixture. Keep the predicate. It is the direct proof of "never restore after a possible submission", and later gates are not.
- Other survivors are unreachable while the gate is held: restoring over a present owner, the fail-closed claim mismatch, and `restore_battery_temporary` overwriting a newer intent. The equivalents are `!=` vs `>` on a monotonic epoch, the missing record, the abandon of an unresolved permit, admission despite unresolved (pending covers it) and admission of an unconfirmed owner.
- An unrelated timing test, `battery_publication_active_subscription_expires_at_unaligned_battery_deadline`, failed once under mutant load. Do not count it as a kill.
- Closing the 2 intent-journaled positions needs a worker change in `discovery/hid_lease/performance/temporary_charge.rs`. Either write progress 70 only after the setter's pre-send checkpoint, or report a typed pre-send refusal so that the permit can retire 70 as known unsubmitted. Only the worker can tell.
- Custody identity (`!Arc::ptr_eq(&original.custody, custody)`) cannot be killed in the Rig, which has one Reader. That needs a second Reader in the fixture.
- On pending storage the cancel still claims once and pauses. The test asserts no getter and no write, not "no claim".
- `readiness_tests/mutation.rs` gained `other_storage`, `notice_skip`, `refuse_at` and an `authentication == usize::MAX` meaning None, all for these tests.
