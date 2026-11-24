---
title: Native Driver-mode record custody and seed (6a-5 S1-S3)
author: Jack
date: 2026-11-24
branch: feat/native-driver-mode-durable
---

## Decided
- Stage 6a-5 S1-S3: the keyboard Driver-mode record is its own owner-custody file, `keyboard-driver-mode.bin`. The domain codec in `openblade-domain/src/driver_mode_history.rs`, behind the `driver-mode-history` feature, takes strict canonical ASCII. `Store::driver_mode_record_read/replace` in `driver_mode_record_store.rs` reuses the thermal journal's staged publish, now `Store::replace_staged`, with boundary labels `driver-mode:<stage>`.
- Reads fail closed and never rewrite. Each of these is refused with a non-fatal PermissionDenied and the file is kept: a leftover `.pending` (or a directory with that name), a record that does not parse, and an owner not in `active_records`. A wrong identity or protection is fatal admission evidence.
- Only the process that activated writes (`activated_here` and `last` is Active), as that owner, by compare-and-swap, along `follows` edges only. ReadLease refuses `recovery_only` before the Store gate does.
- The seed (`transaction_seed::quarantine_unanswered`) takes the record's four IDs ahead of the journal's. LIVE is now 78 (DRIVER_MODE = 4 counts in every build), LIMIT stays 80 and DEMAND stays 66, because `operation.lock()` serializes mutation jobs. A refused record adds nothing; the closed owner sends no 00/04.

## Tried and ruled out
- Header and contract checks as a separately killable gate: removing them survives, because the canonical re-encode check already refuses every other header. Only removing both is killed. Treat them as one gate.
- A test that kills a no-op `published` check: the stage handle is held exclusive from the rename to `drop(file)`, and no boundary lies between the drop and verification, so a test cannot change the record in that window. The mutant survives.

## Watch out
- A `use super::*;` in a test module that only one feature set compiles triggers unused-import errors under `-D warnings` in the other lanes. Lint all 12 service lanes, not just the keyboard lanes.
- The `with_mutation` seed now also calls `verify_integrity` once through GuardedReadCustody at composition time. A test that counts `integrity_lost_at` from before composition would shift by one.
- The ReadLease `recovery_only` branch cannot be unit-tested, because it needs a real ProgramData root. Store-level coverage is `a_process_that_did_not_activate_never_writes_as_the_active_owner`.
