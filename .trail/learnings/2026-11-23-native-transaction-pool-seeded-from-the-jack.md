---
title: Native transaction pool seeded from the journal across restarts
author: Jack
date: 2026-11-23
branch: fix/native-pool-seed-from-journal
touches:
  - docs/windows-native-thermal-mutation.md
  - docs/windows-rust-development-service.md
  - docs/windows-rust-thermal-history.md
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation_fixture.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/transaction_tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/transactions.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/windows.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
---

## Decided
- A previous process's reply can reach a new one: the codebase's own premise is that the GET_FEATURE mailbox outlives CancelIoEx and the handle, a restart is only a handle reopen, and `validate_response` tells replies apart by ID, class and command alone. A fresh pool that reissues an ID the last process left unanswered can accept that stale reply when the new SET does not take effect.
- The fix seeds the pool from the journal, with no format change: `transaction_seed::quarantine_unanswered` runs in `ReadSource::with_mutation`, before any worker exists, and marks the named IDs Sent through `Transactions::quarantine`, which touches only Free IDs.
- Seeded, live first: the IDs an unsettled final request may have sent (cleanup IDs at fan 31 and 40-43, `ids` otherwise; tfc adds `superseded_ids`), a live fan owner's `cleanup`, and a live temporary owner's `cancel_ids`. Then, newest first, every progress-6 (known unsent) row's IDs and every tfc `superseded_ids`. Settled confirmed history seeds nothing: the firmware answered it.
- The cap is 48 (`transaction_seed::LIMIT`). The live set is at most 32 (battery 14, fan cleanup 10, tfc cancel 8), so the cap never truncates it, and 206 of the 254 IDs stay free.
- Fail closed means no wider window than today: an unreadable, absent or invalid journal seeds nothing, and startup still runs.

## Tried and ruled out
- Seeding in `Storage::open`: it is lazy per user, which is too late. Worker::new runs no job, so the ReadSource composition is the earliest point that shares the process pool.
- The `!entry.pinned` check in `quarantine`: it is an equivalent mutant, because a pinned ID is never Free, so it was removed.

## Watch out
- Coverage is partial:
  - unjournaled reads and performance setter IDs still rely only on the random round start;
  - IDs sent before a row names them are not seeded, nor are rolled segments or replaced owners' reservations.
- The native `quarantine_native` path is checked only at compile time. Tests drive the fixture pool through `MutationFactories.quarantine`.
- Managed (`ValidationRunContext`) restarts its IDs at 1 per context and has no cross-session protection.
