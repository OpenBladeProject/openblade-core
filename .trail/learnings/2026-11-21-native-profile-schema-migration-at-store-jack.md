---
title: Native profile schema migration at store open
author: Jack
date: 2026-11-21
branch: feat/native-profile-schema-migration
touches:
  - docs/windows-native-thermal-mutation.md
  - rust/Check-Workspace.ps1
  - rust/crates/openblade-domain/src/profile_set/mod.rs
  - tests/OpenBlade.Service.Tests/OpenBlade.Service.Tests.csproj
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage_real_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage_tests.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness_tests/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/thermal_storage.rs
  - rust/crates/openblade-domain/src/profile_set/migrate.rs
---

## Decided
- Native migrates schema 1-26 aggregates and the schema-less legacy profile when the store opens (`profile_storage::migrate_on_open`, one call in `thermal_storage.rs` `Storage::open_with`, gated to `service-profiles-v1`), because managed `BatteryConfigurationStore.LoadCoreAsync` (`:349-417`) writes the migrated aggregate back at load, not at first save. An absent aggregate is still materialised by the first save.
- The payload half is `profile_set::migrate` in the domain crate; it takes a `sha256` closure for the marker because the `profile-set` feature has no hash dependency (sha2 only comes with `thermal-history`).
- The migration reuses the save's recorded transaction (`Transaction::new`), so a crash after the record lands keeps the store closed and is never auto-recovered, as managed `CheckRecoveryRequired` does. A refusal (future schema, missing file, marker mismatch, invalid or mixed document) leaves every file untouched and falls through to the reader's own refusal.
- `profile-migration-vectors.json` is recorded only by `ServiceControlRuntimeTests.NativeProfileMigrationVectors` (set `OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS`); never edit rows by hand.

## Tried and ruled out
- A legacy template with brightness 255: managed refuses every such row (brightness is 0-100), so the vectors silently became all refusals. Keep generated templates valid and check the admitted count.
- Comparing raw schema 26 bytes after open in the readiness tests: migration at open rewrites them canonically, and managed's writer omits a false `controlsKeyboardEffect` and null independent controls. Compare semantically.

## Watch out
- `sed -i` in Git Bash strips CR from CRLF files (csproj, C#, this repo's docs in the working tree); re-normalise the whole file and recheck before committing.
- A document with duplicate members that also declares a future schema: managed calls it FutureSchema, the domain reader calls it Invalid. The host envelope inspection decides first, and both leave the files untouched.
- A migration that `thermal_storage::aggregate` would refuse once written is not written, so the store stays closed where managed would migrate. Duplicate paths already compare as managed does (2026-11-20 duplicate-path learning), so only another reader disagreement reaches this.
