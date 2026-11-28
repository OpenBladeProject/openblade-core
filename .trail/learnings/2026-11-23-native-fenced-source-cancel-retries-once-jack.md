---
title: Native fenced source cancel retries once with fresh transaction IDs
author: Jack
date: 2026-11-23
branch: fix/native-tfc-fresh-cancel-ids
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/battery_mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/battery_storage.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/temporary_charge.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - rust/crates/openblade-domain/src/temporary_charge_history.rs
  - src/OpenBlade.Deployment/WindowsNativeThermalHistory.cs
  - src/OpenBlade.Deployment/WindowsNativeTemporaryChargeHistory.cs
  - tools/openblade-windows-host/tests/data/temporary-charge-history-v1.json
  - docs/windows-native-thermal-mutation.md
---

## Decided

- This completes the fresh-ID retry the 2026-11-22 learning "Native battery cancel restores only an attempt that sent nothing" queued. A fenced automatic temporary-charge cancel that sent some of its enable's reserved `cancel_ids` is retried once with fresh pool IDs.
- Journal: the retry's row names the reserved set in the optional tfc member `superseded_ids`, after `cancel_ids`. It is valid only on an automatic cancel, disjoint from `ids`, and never an explicit null. The journal binds the cancel by `cancel_ids == superseded_ids` (or `== ids` when absent), and refuses any other automatic cancel request of that enable after the first superseding one, until the next temporary-charge 72/73 row (`replaced_by` in `thermal_history.rs`, `replacedBy` in `WindowsNativeThermalHistory`).
- No contract or schema bump. Rows without the member keep their bytes and digests. Old readers refuse the member (Rust `deny_unknown_fields`, the managed exact member set), and a stripped, rehashed row is still refused because its `ids` no longer match `cancel_ids`. The handback record is unchanged: it classifies by progress and desired state and carries no IDs.
- Runtime: `TemporaryClaim::roll_back_spent` runs only for an attempt that used the reserved IDs, joined and was not confirmed. `Storage::restore_spent_claim` then requires settled, unquarantined storage with no newer intent, and either nothing journaled with the reservation unchanged, or the attempt's own last row 6. It swaps in `retry_capacity` and marks the enable `cancel_spent`. Later attempts draw IDs with `reserve_retained_pair` and settle their own pins once joined. A fresh-ID attempt that sent pauses, so one enable sends at most two ID sets.
- Capacity: an enable reserves `max(4r, 2r + retry_capacity)`. `retry_capacity` is four upper-bound fresh rows (ids `[254; 8]` plus `superseded_ids`).
- The progress-70 order stays: checkpoint, row 70, the setter's pre-send checkpoint, send. It is the shared write-ahead order, and an unsettled 70 fences every retry.

## Tried and ruled out

- Asserting that a fresh attempt fenced at its flags read queues its pins. `retain` had not run yet, so retirement unpinned them directly. Assert that none stay pinned instead.
- One ordinary operation after release or abandonment no longer reaches a padded segment boundary (`SEGMENT - span - quota - slack`), because the reservation now also covers the retry. The prepared-race and corrections tests loop settled operations until one rolls.
- Refusal vectors that end at a Prepared row. The managed reader refuses an unsettled tail anyway, so the managed `replacedBy` and disjointness mutants survived. Each refusal vector now ends with its 6.

## Watch out

- The fixture counts `temporary_reads` when the getter is sent, not when its reply reaches the permit. An attempt fenced in between has no independent read, so it is restored, and its fresh retry reads the charge off (73). Only an observed read (`read_off_since_positive`) blocks the restore.
- Surviving mutants are defence in depth: `!permit.confirmed` (storage refuses a 72/73 last row), `last.progress == 6` (the unsettled check covers 70/71), and the restore room check together with the reserved Prepared's retry room, because the enable's `cancellation_capacity` already holds that space and other writers refuse while it stands. The enable-capacity mutant is killed.
- Killed: the reserved-ID, join, read-off and quarantine gates, the `cancel_spent` filter, pinned fresh IDs, settle-after-join, the enable capacity, and every domain and managed journal check.
