---
title: "Native transaction pool seeds each family's newest request after a restart"
author: Jack
date: 2026-11-23
branch: fix/native-pool-seed-from-journal
---

Supersedes "Native transaction pool seeded from the journal across restarts" (2026-11-23). That learning said a confirmed history seeds nothing, and that is wrong. Its cap, journal read, owner tracking and timing wording are also replaced here. Its analysis of why a previous process's reply can reach a new one still holds.

## Decided
- A confirmed request is as exposed as an unsettled one.
  - Reading a reply does not clear the mailbox. Within a process the pool frees an answered ID only after the same class and command is answered again (`transactions::answered`).
  - A new process has answered nothing, so the earlier process's newest answered ID of each command can be read back as stale.
  - Example: after a confirmed battery row 62, ids[3] (the last 07/8C read) was unseeded and the next request could draw it.
- The seed (`transaction_seed::unanswered`) takes, in order:
  1. Every ID each family's newest request (fan, battery, temporary charge) may have sent, whatever its progress (`sent`). For a fan that is preflight, ids and cleanup; for battery, ids; for temporary charge, ids and superseded_ids. An enable's cancel_ids are not included: they are a later cancel's to send.
  2. The live owners' reservations.
  3. Newest first, the known-not-submitted (6) rows' `after_prepared` IDs and every superseded_ids set.
- The live owners now come from the domain. `thermal_history::rows_with_owners` returns `Owners { fan, temporary }` from the same `rows_from` state that admits each row. `handback_complete` uses it as well, so the seed no longer hand-mirrors progress codes 30/50/51/72/73.
- The cap is 80 and is pinned at compile time: `LIMIT >= LIVE (74) && 254 - LIMIT >= 3 * DEMAND (46)`.
  - LIVE = fan 26 + battery 14 + temporary charge 16 + fan owner cleanup 10 + temporary-charge owner cancel 8.
  - A test pins `(FAN, LIVE, DEMAND, LIMIT)`, and a fixture fan record names exactly 26 IDs.
- The seed reads through `journal_read_release`, which authenticates the journal without running recovery, so composing a source writes nothing. During an interrupted roll the seed finds nothing; the first mutation's own `journal_read` completes the roll.
- A failed read is retried exactly once. A FatalAdmission failure is not retried; it is kept in `ReadSource.fatal` through `remember_failure`, and the restarted source's first exchange then fails with it.
- Timing: the seed runs in `ReadSource::with_mutation` when the source is composed. Worker objects already exist then, but no job has run.
- `MutationFactories::native()` is the single place that wires the native workers and `quarantine_native`. `windows::quarantines_process_pool` proves the wiring reaches `TRANSACTIONS`.

## Tried and ruled out
- Seeding only the unsettled final request (the previous version).
- Mutating `if settled { skip newest }` at the row: every request has an unsettled row 0, so that mutant is equivalent. To model the old bug, clear the family's newest IDs on a settled row instead.
- Including an enable's cancel_ids in `sent`: redundant with the owner, and not sent by the enable.

## Watch out
- Equivalent mutant: dropping the newest temporary-charge request's superseded_ids from `sent` survives, because the superseded history adds them too and LIMIT >= LIVE always leaves room. The live copy is kept on purpose.
- Still not seeded:
  - an earlier request's reply to a command the newest request of its family did not send, such as a setter it never reached or fan Custom levels;
  - unjournaled reads and performance setters;
  - archived segments;
  - replaced owners' reservations.
- `battery_storage.rs` still runs its own fan-owner loop to size the retained handback. It could use `rows_with_owners`, but that was out of scope.
