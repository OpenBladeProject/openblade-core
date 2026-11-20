---
title: "Native duplicate-path comparison is managed's recorded OrdinalIgnoreCase table"
author: Jack
date: 2026-11-20
branch: feat/native-profile-set
touches:
  - docs/windows-native-thermal-mutation.md
  - rust/crates/openblade-domain/src/profile_set/mod.rs
  - rust/crates/openblade-domain/src/profile_set/text.rs
  - rust/crates/openblade-domain/tests/profile_set_vectors.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - tools/openblade-windows-host/src/native_package.rs
  - tools/openblade-windows-host/src/native_package/tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage_real_tests.rs
---

The duplicate-path comparison for native `updateProfiles` is now managed's `OrdinalIgnoreCase` as a recorded table. This supersedes the statement in 2026-11-20-native-profile-saves-treat-a-stage-jack.md that `same_ordinal_ignoring_case` is pinned by recorded pairs: sixteen pairs did not pin it.

## Decided
- `ServiceControlRuntimeTests.NativeProfileOrdinalCase` records every set of characters `StringComparer.OrdinalIgnoreCase` holds equal into `tools/openblade-windows-host/tests/data/profile-ordinal-ignore-case.json` (1445 sets on .NET 10). It needs no case table: equal strings hash alike, so each character is only compared within its hash bucket.
- `profile_set/ordinal_case.rs` is that vector as a compiled range table and `text::fold_ordinal_case` reads it. The domain vector test checks every Unicode scalar against the vector, and rewrites the table when `OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS` is set.
- Both package readers refuse the `service-profiles-v1` feature and capability by name, not only because the expected capability string differs.
- `ProtectedFiles::delete` reads at most the bytes it was asked to match and deletes a stage unread.

## Tried and ruled out
- Deriving the comparison from Rust `char::to_uppercase` with exceptions: the Rust 1.99.1 table is Unicode 16/17 and joins 33 pairs .NET 10 keeps apart (U+0264/U+A7CB, U+019B/U+A7DC, U+1C89/U+1C8A, U+A7CC to U+A7DB, U+16EA0 to U+16ED3). An aggregate managed wrote with such a pair opened, and then every native save was blocked as a preserved aggregate.
- Finding managed's sets from candidate case partners (`Rune.ToUpperInvariant`, the Windows upcase table): it is only as complete as the candidates. Hash buckets need none.
- An 8 MiB cap in `delete` like the one in `read`: a leftover stage above the cap could then never be discarded.

## Watch out
- The table is managed's as of the runtime that recorded it. A newer .NET that joins more letters fails the managed test first; record the vector again, then rewrite the table with the same variable.
- A saved aggregate the legacy reader accepts but the domain validator refuses is still answered `Blocked` on every save. The comparison was one way to get there; any other disagreement between `thermal_storage::aggregate` and `read_aggregate` is another.
- A packaged Agent image built in the same invocation carries the Service features, so it is refused with a profile-serving build too.
