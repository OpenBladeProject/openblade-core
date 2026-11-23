---
title: "Native transaction pool: releasing fan and temporary-charge pins"
author: Jack
date: 2026-11-23
branch: fix/native-pool-pin-release
touches:
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/transactions.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
---

Supersedes 2026-11-22-native-transaction-pool-what-still-exhausts-jack.md and 2026-11-22-native-transaction-pool-reclamation-and-the-jack.md where they say fan and temporary-charge (TFC) pins stay out of the pool for the process: a settled record now returns them. Their other findings stand.

## Decided
- Out of date: "IDs that a fan record or a temporary-charge cancellation names stay pinned" and "pins about 24 IDs ... for the process lifetime" (reclamation learning), and the 12 fan changes, 14 manual/Automatic alternations and 30 TFC cycles exhaustion budgets (what-still-exhausts learning). A failing read still quarantines one ID per job.
- Mechanism: `Reader::retain(permit)` hands the retained IDs to the record's `RecordPins` (`Permit::retained_pins`). `settle_pins` moves them into the Runtime's shared `SettledPins` queue. Any mutation Reader takes the queue in `apply_preset` (`Permit::settled_pins`), possibly while already running, and unpins them only after `poll_retirement` confirms. `Transactions::unpin` frees only unsent (Held) IDs that are still pinned, so a sent ID stays quarantined and a second settle or unpin frees nothing.
- A queue, because the host cannot reach the port's pool and some settlements (row 6 in `retired`, TFC slot exits) happen after the record's own Reader retired.
- Fan settle sites, each after its durable row: own 50/51 in `record_fan`, 51 in `confirm_fan_noop` and `FanSettlePermit::record_fan`, 6 in `retired`, and `supersede_prior` for the replaced owner on a newer 30 or 50, which also clears the replaced owner's `owned`.
- TFC settle sites: the owner replaced by a newer confirmed enable and a joined, unconfirmed, non-pending enable (`reconcile_temporary_owner`), and `finish_temporary_release` on Released or NoMatchingReservation. The invariant (`BatteryPermit::settle_pins`) is that no request can bear the cancel IDs again: the enable's worker joined and it left every owner slot for good, no claim can restore it, and every cancel attempt that could send the IDs joined. That keeps the fenced rollback's `Transactions::unsent(&cancel_ids)` proof sound. The journal need not be settled: NoMatchingReservation can settle with `storage.pending()` true.
- Swapping held release and unpin in `Reader::retire` is equivalent only because one Reader runs at a time (`OCCUPIED`).

## Tried and ruled out
- Unpinning every provisional ID at settlement: provisional IDs that were never retained are freed at the first retirement and may be issued again, so only retained IDs enter `RecordPins`.
- Running the long TFC cycle test under `paused_rig` plus `frozen`: the virtual deadline expires as the journal grows. It uses `Rig::new` with `reserve_for_tests(100)`.
- `release_fault = 1` alone does not defer a release after a confirmed cancel: the cancel's row spends the reservation, so the release is NoMatchingReservation. Pair it with `fresh_refuse` so the cancel sends nothing.

## Watch out
- Only fan and battery mutation Readers drain the queue; read-only Readers and thermal permits do not. A failed retirement after taking the queue leaves those IDs pinned (fail-closed). An enable whose claimed cancel never joins keeps its pins.
- Read-only Readers never unpin, so a premature settle shows only through `r.runtime.queued_pins_for_tests()` or `r.port.pinned_transactions(ids)` (readiness_tests/pool_pins.rs).
- The gate mutants (settle before a durable row, on the Deferred arm, before the enable or cancel joined, unpin before `poll_retirement`) survived the full lanes until pool_pins added the held-write tick, unjoined-cancel, deferred-release, failed-close and refusal-sweep tests. Rerun them after changes to the settle sites.
- The scratchpad is shared with concurrent agents: write drafts under a private subdirectory. A shared `note.md` was overwritten once, and `trail note` saved another branch's text.
