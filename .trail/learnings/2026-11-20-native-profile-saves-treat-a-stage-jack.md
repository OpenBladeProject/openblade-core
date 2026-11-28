---
title: Native profile saves treat a stage as litter and the record as evidence
author: Jack
date: 2026-11-20
branch: feat/native-profile-set
touches:
  - docs/windows-native-thermal-mutation.md
  - rust/crates/openblade-domain/src/profile_set/text.rs
  - rust/crates/openblade-domain/src/profile_set/validate.rs
  - rust/crates/openblade-domain/tests/profile_set_vectors.rs
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tools/openblade-windows-host/build.rs
  - tools/openblade-windows-host/src/native_package/tests.rs
  - tools/openblade-windows-host/src/service_status/profiles.rs
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage_real_tests.rs
---

The cross-model review of native `updateProfiles` (feature `service-profiles-v1`) refuted its recovery. This records what changed. It supersedes three statements in 2026-11-20-native-updateprofiles-is-pinned-to-managed-jack.md: that decode compares duplicate paths by Unicode upper case and the validator by the OS comparison, that `build.rs` does not name `service-profiles-v1`, and that the readback ran before the record was removed (the semantic one did not).

## Decided
- A `.pending` stage of the profile transaction is never evidence. The transaction discards the stage of its own failed write (`Transaction::put`) and, when no record exists, clears any leftover under a name it writes before it starts. A leftover that cannot be removed is a `Failed` reply that says so, not a closed store. The record alone is evidence, as in managed.
- Managed's `VerifyIntendedAggregateAsync` runs inside the transaction: exact bytes and the admitted set, while the record and backups still stand, and a mismatch restores.
- Before anything is written, `save_profiles` reads the intended documents with `thermal_storage::aggregate`, the reader every later request and every start uses. A set that reader refuses is `Failed`; it is never stored and then unreadable.
- Duplicate executable paths and THX rule names use `profile_set::text::same_ordinal_ignoring_case` in both the frame reader and the store's validator, pinned to managed's `OrdinalIgnoreCase` by recorded pairs.
- `build.rs` gives a profile-serving build the capability `...,service-temporary-charge-v1,service-profiles-v1`. Both package readers refuse it on purpose; admitting it is the packaging stage's job.
- `serve_mutation` answers a mutation that returned after the frame deadline instead of closing the connection.

## Tried and ruled out
- Deleting the stage inside `ProtectedFiles::replace` when it fails, which the review proposed: the same primitive writes `manual-overrides.json`, whose failed `.pending` stage is evidence the handback to managed hashes (`WindowsNativeHandback.cs`) and `Storage::open_with` fences on. The profile transaction cleans up its own names instead, and `CustodialFiles::discard_stage` refuses the override document and the journal.
- Rust `to_uppercase` equality for duplicate paths: it joins a sharp s with `SS`, which managed keeps apart.
- The Windows `CompareStringOrdinal` as managed's `OrdinalIgnoreCase`: it keeps apart pairs managed joins (final sigma, micro sign, title-case digraphs, Cherokee, Georgian, letters outside the basic plane). The aggregate reader and the foreground-application match in the earlier lanes still use it.
- A semantic readback after `Transaction::run`: by then the record and the backups were gone, so a mismatch could only quarantine.
- A branch for `\\?\` paths in `executable_path_fault`: it only chose between two refusal messages that no caller can observe, because such a frame never passes body admission.
- A thermal-lane test for the late reply: a thermal set stalled at any of its journal writes is cut by `timeout_at` at its next await, with or without the change. Only the profile save returns without an await after its commit.

## Watch out
- `Path.GetFullPath` returns a `\\?\` or `\??\` path as written, dot segments and doubled separators included, and normalises a `\\.\` device path like any other. `Path.IsPathFullyQualified` accepts a leading separator followed by `?`. Managed stores all of these, so `valid_application_path` accepts them too.
- Managed `OrdinalIgnoreCase` never maps a character outside ASCII to an ASCII letter (dotless i, long s and the Kelvin sign stay distinct) and never expands (sharp s, ligatures), but it joins a Greek letter with a subscript iota to its one-character capital, and letters outside the basic plane.
- On the Windows 11 build that recorded the vectors `GetFullPath` leaves `NUL.exe` and `COM1.exe` alone. An older Windows maps them to `\\.\NUL`; that is not modelled.
- A fixed stage name plus create-new is what made a leftover stage fatal. Any new writer on `ProtectedFiles::replace` has to decide whether its stage is evidence or litter.
- A vector whose frame managed refuses records only `decode: false`. Validator messages for such sets are not pinned and should not be modelled.
- Two redundant clauses in the transaction's verification mirror managed. The meaning clause cannot be killed by a mutant: exact bytes already imply it.
