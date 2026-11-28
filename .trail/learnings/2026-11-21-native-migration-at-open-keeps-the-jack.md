---
title: Native migration at open keeps the original order when it writes nothing
author: Jack
date: 2026-11-21
branch: feat/native-profile-schema-migration
touches:
  - docs/windows-native-thermal-mutation.md
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
---

This supersedes parts of 2026-11-21-native-migration-at-open-follows-admission-jack.md after a third review: its "Tried and ruled out" bound for `applicationProfiles: null`, its "an open that fails writes nothing" claim, and its 16-entry mismatch memory.

## Decided
- `profiles::plan_migration` is read-only and runs after the pending checks. When it returns `None` (current, absent, refused or unreadable-once-migrated aggregate), `Storage::open_with` keeps the pre-migration order: aggregate, then overrides, then journal. Only when it returns a `Planned` are overrides and the journal admitted first, then `Planned::write`, then `aggregate()`. Non-profile lanes compile `None::<()>` and the original order.
- "Writes nothing" holds for the configuration documents only. The journal store's read authenticates rows, can fail fatally on owner binding and can publish a pending roll, so a refused aggregate must be answered before the journal is read.
- The mismatch hold is one thread-local entry: (user SID, change digest). Another user's open reaching the migration step clears it, mirroring managed's reload when a user activates after another (`ServiceControlRuntime.cs:1456-1487`). No cap is needed; the old 16-entry cap held nothing once 16 stores cycled.
- Managed retries a failed load at the next request, not a session event (`ControlRequestHandler.cs:211-241`).

## Tried and ruled out
- The old bound "a null list is refused by managed too". Managed refuses `applicationProfiles: null` only from schema 15; at 2-14 it reads and discards it and migrates, so native's refusal is a declared difference. Likewise `windowsPowerModes: null` differs at 2-14 (not just below 12), and an entry with a null `name` differs at 2-21 alongside `[null]`, null overrides and null path. All are managed-recorded `nativeDifference` rows; schema 15 and 22 are ordinary boundary rows.

## Watch out
- `ProtectedFiles::replace` reads each file back and fails on a disagreement, so on real files the hold engages only when the transaction's later verification reads other bytes (another writer between replace and verification). The `SecondWriter` real-file test covers that deterministically.
- Because the hold is keyed by content, other bytes in the same store are a new change and are attempted; another user's byte-identical store is attempted too.
