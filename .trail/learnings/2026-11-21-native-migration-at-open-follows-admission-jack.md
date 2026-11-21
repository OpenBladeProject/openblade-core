---
title: Native migration at open follows admission and holds a verification failure
author: Jack
date: 2026-11-21
branch: feat/native-profile-schema-migration
touches:
  - docs/windows-native-thermal-mutation.md
---

This adds to 2026-11-21-native-profile-schema-migration-at-store-jack.md after review; that learning's hook placement is superseded.

## Decided
- `profiles::migrate_on_open` runs in `Storage::open_with` after every read-only refusal: pending override and journal evidence, the recovery-required record, `overrides()` and the journal rows. An open that fails writes nothing. Managed loads overrides after migrating and quarantines them separately, but native refuses the whole open, so migrating first only rewrote an unusable aggregate.
- A migration the store reads back as other bytes (`Failure::Mismatched`) is refused with managed's quarantine text and remembered by change digest in a thread-local, at most 16 entries. Managed quarantines that case until restart: `VerifyIntendedAggregateAsync` throws `InvalidDataException`, which `IsRecoverable` admits. I/O failures are not remembered, because managed throws and retries at the next activation.
- Declared parity differences are `nativeDifference` rows in profile-migration-vectors.json. Managed's outcome stays the expectation; the domain test expects `Invalid` and the host test expects a closed store with no writes.

## Tried and ruled out
- Treating `applicationProfiles: null` below schema 22 as a difference. Managed refuses it too (InvalidDocument), so it is an ordinary row. Only `[null]`, null `overrides`, null `executablePath`, and `windowsPowerModes: null` below 12 differ.
- A process-wide static for the mismatch memory. Tests share the process and the same vector change digests, so a padded-write test would poison other tests. The runtime is `Rc`-bound to one thread, so a thread-local matches its lifetime.

## Watch out
- `forget_mismatches()` simulates a restart in tests. Any test that fails a migration with Wrong, Lost or Padded and then reopens must call it, or the reopen is refused.
- The Runtime factory in thermal_mutation.rs is `cfg(not(test))`, so it caches only successful opens. A failed open is retried on every request.
