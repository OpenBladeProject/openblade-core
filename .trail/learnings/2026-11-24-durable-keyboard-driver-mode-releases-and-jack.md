---
title: durable keyboard Driver-mode releases and loaded intents
author: jack
date: 2026-11-24
branch: feat/native-driver-mode-durable
touches:
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_owner.rs
  - tools/openblade-windows-host/src/windows/scm/installed/driver_mode_ledger.rs
  - tools/openblade-windows-host/src/windows/scm/installed/mailbox_read.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/driver_mode_record_tests.rs
---

## Decided

- Keyboard Driver-mode release doubt is the durable `ReleaseIntent` in `owner\keyboard-driver-mode.bin`; the in-memory `uncertain` flag is gone. `Keyboard::uncertain_since` only dates the doubt for `driver_mode_confirmed_since`: stamped when a release's job has retired, at load, or at the first maintain step that finds it (never earlier than the setter).
- A `ReleaseIntent` loaded from an earlier process is resolved by the step's own read-only job (`resolve_indeterminate`), not by the read cadence: Driver settles to the intent's prior, Normal to `Clear`. After this process's own release, the read cadence still resolves it, because that release activated the write lease.
- `Ledger::settle`/`revert` check the writer's custody admission first and return `Ok(false)` without writing or closing when refused. Claims have no pre-check: any claim error closes the ledger (D5).
- A release settles only while `now + write_allowance() <= start + share`; the share-timeout path records nothing. A dropped release job read later settles only while the record still holds a `ReleaseIntent`, so it never overwrites a newer claim.
- A release over an `AcquireIntent` writes `AcquireIntent -> Owned` first, then the `ReleaseIntent`; its claim allowance is two writes.

## Tried and ruled out

- Resolving a loaded `ReleaseIntent` from the read cadence: the settle's `driver_mode_record_owner()` is refused until a mutation activates this process's lease (`ReadSource::mutate` is the only activation), so the write fails and closes the keyboard owner for the process.

## Watch out

- The readiness rig's `RecordModel` refuses writes until `activations > 0`, as the Store does; a `Rig::restarted` is unactivated until its first mutation job.
- Rust owner history is never pruned: `load_history` needs a gapless owner chain from 1 and the 4096-entry cap fails the store open, so the nonce that wrote the current record is always present.
- `the_fans_join_a_keyboard_worker_slow_to_retire_within_their_drain_time` is a real-time flake under a loaded full kbd run.
