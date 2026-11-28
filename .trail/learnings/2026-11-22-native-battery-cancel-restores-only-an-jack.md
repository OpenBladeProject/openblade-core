---
title: Native battery cancel restores only an attempt that sent nothing; supersedes the unsubmitted-fence retry learning
author: Jack
date: 2026-11-22
branch: fix/native-tfc-cancel-epoch-race
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_storage.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/temporary_charge.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/transactions.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/temporary_charge_release.rs
  - docs/windows-native-thermal-mutation.md
---

## Decided
- This supersedes the 2026-11-22 learning "Native battery cancel retries after an unsubmitted fence". Its `known_unsubmitted` (joined, unconfirmed, not pending, nothing Prepared or only the progress-6 retirement) let a retry resend reserved IDs the first attempt had already sent. The pool rule (`discovery/hid_lease/performance/transactions.rs:9-21`) never reissues an ID sent without a correlated reply, so the retry could accept the first attempt's late reply.
- The epoch callback is `StatusOrigin::power_epoch` (`status_source.rs:400`, `:417`), not `OriginLifetime::power_epoch` as the old learning says.
- `BatteryPermit::known_unsubmitted` is now `joined && unsent`. `unsent` starts true. The worker reports false right before the steps that send the cancel's IDs, and once they stop reports `Transactions::unsent(&ids)`: every ID still `Held`. The pool marks an ID `Sent` before submission, so this is the pool's own record, not a position heuristic. A dropped worker future leaves false. The confirmed, pending and Prepared-implies-6 clauses were dropped: none of them can hold without a sent request. The only worker change is that report (`Permit::observe_temporary_unsent`, `temporary_charge.rs`).
- The claim is one object, `TemporaryClaim` in `battery_mutation.rs`. `take` takes the peeked owner from the slot, marks it abandoned, and calls `Storage::take_temporary_claim`, which takes the intent and records the reservation (capacity and origin) it leaves in place. `roll_back` puts back exactly that, or nothing. It refuses when:
  - an owner is in the slot, or an independent read found the charge off since the owner's positive (`Runtime::read_off_since_positive`, shared with `reconcile_temporary_owner`);
  - storage is quarantined, the last row is unsettled, or the intent slot is occupied;
  - the reservation differs from the recorded one.
- Window map, 40 positions, the same for a second unplug, a session notice and a suspend: `uuuuuuupppppppppppppppppppppiissssssssss`. Positions 0-6 send nothing and are retried. 7-27 sent a reserved ID (from the first 07/8C power read) and pause. 28-29 have intent journaled, 30-39 were submitted. The refusal map is unchanged: `p` x28, `i` x2, `s` x10.
- Queued, not done: retrying with fresh pool IDs. The journal binds an automatic cancel to the enable's `cancel_ids` (`openblade-domain/src/thermal_history.rs:372-386`), so it needs a journal-contract review across the domain crate and the managed reader.

## Tried and ruled out
- The old learning's "the retry confirms that read without a setter (progress 73)". A restored attempt that had read the charge off left desired TFC set with no owner (the reviewer's R2b probe). With the pool check no restored attempt has read anything. `roll_back` refuses after such a read anyway.
- Re-taking a reservation a Prepared row spent. A restored attempt never wrote a Prepared row, so it was dead code and was dropped. `restore_temporary_claim` refuses unless the reservation still equals the recorded one.
- Judging "sent nothing" from the fixture port's journal rows or positions. Positions 2-6 refuse at the worker's first `authorize()`, before `apply_temporary_charge` runs, so a report that exists only after the steps left them paused. That is why `unsent` defaults to true.

## Watch out
- Several rollback refusals cannot be reached once the pool check holds, so their mutants survive: the read-off check, quarantine, the unsettled row, the reservation comparison, the occupied intent slot, the occupied owner slot (the gate is held), and the poisoned pool. The `false` report before the steps matters only when the worker future is dropped. These are fail-closed structure, not tested behaviour.
- The fixture can make a performance request only on the unscoped rig: `CapturedFresh` reports no power generation. The other-writer test turns `scoped_fresh` off after the fenced tick.
- `readiness_tests/temporary_charge_release.rs` classifies a window by `port.state.sent` growth during the fenced tick, and `Runtime::temporary_owned_for_tests` reports a live, unclaimed owner.
- The near-capacity and other-writer tests use the real-clock scoped rig. On `paused_rig` with `frozen`, the fenced notice did not land in 2 of 31 full-lane runs under load. The window-map rigs never flaked.
- `battery_publication_active_subscription_expires_at_unaligned_battery_deadline` still fails now and then under load. Do not count it as a kill.
- Mutant results (full temporary-charge lane): the sent check, the join, the `unsent` default, the pool verdict, `unsent()` accepting Sent, Free or Answered IDs or a poisoned pool, the fence check, the rollback itself, and each part of what the claim takes or puts back are killed. The survivors are the unreachable refusals above, the false report before the steps, and the old equivalents: `!=` vs `>` on the monotonic epoch, the missing record, abandoning an unresolved permit, admission despite unresolved or unconfirmed, the custody check (one Reader in the Rig), restoring over a present owner, and the claim-mismatch path.
