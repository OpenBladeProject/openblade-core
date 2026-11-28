---
title: Native updateProfiles is pinned to managed by recorded vectors
author: Jack
date: 2026-11-20
branch: feat/native-profile-set
touches:
  - rust/crates/openblade-domain/src/profile_set/mod.rs
  - rust/crates/openblade-domain/tests/profile_set_vectors.rs
  - tools/openblade-windows-host/src/windows/scm/installed/profile_storage.rs
  - tools/openblade-windows-host/src/windows/scm/installed/profile_mutation.rs
  - tools/openblade-windows-host/src/service_status/profiles.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeProfileVectors.cs
  - rust/Check-Workspace.ps1
  - docs/windows-native-thermal-mutation.md
---

Native `updateProfiles` (feature `service-profiles-v1`, exact RZ09-0581) is pinned to managed by vectors managed itself records. Supersedes the "native has no typed ProfileSet model" statement in 2026-11-20-native-typed-refusal-for-unserved-service-jack.md.

## Decided
- The typed model, System.Text.Json codec, validator and aggregate readers live in `rust/crates/openblade-domain/src/profile_set` behind `profile-set` (no dependencies). The host only stores and dispatches.
- serde_json cannot carry what managed binding depends on: member order (the `kind` discriminator must be first) and number text. The domain has its own ordered reader and a `Utf8JsonWriter`-compatible writer. `StatusRequest::UpdateProfiles` lets serde skip the `profiles` member and binds it from the frame bytes (`ProfilesBody::bind`).
- Expectations come from managed, never from hand-written fixtures: `ServiceControlRuntimeTests.NativeProfile*` run the IPC codec, the production RZ09-0581 validator, the real `UpdateProfilesAsync` and `BatteryConfigurationStore.LoadAsync`, and verify the same three files under `tools/openblade-windows-host/tests/data/profile-*` that the Rust tests read. Regenerate with `OPENBLADE_REGENERATE_NATIVE_PROFILE_VECTORS=<that directory>`.
- `updateProfiles` calls `ValidateStoredCustomIntent` plus `ValidateChangedThermalIntent`, not `Validate`. Stored intent the device cannot select (Turbo, 5500 RPM, Custom High/High) survives an unrelated edit and is refused only when it changes.
- The save is managed `SaveProfilesAsync`: all three documents in one recorded transaction, preferences bytes reused unless reading them migrated something, marker last, readback before the record is removed. Nothing recovers a recorded transaction after a crash, in managed or native; the record's presence preserves the aggregate.
- Saving profiles consults no device, power or controller state and writes no hardware.

## Tried and ruled out
- A `serde_json::Value` body: keys are sorted, so the first-`kind` rule cannot be checked and tests that re-serialise a request silently break lighting parameters.
- A sha2 dev-dependency for the vector test: `rust/Check-Workspace.ps1` admits exact dev-dependency, feature and test lists. The test takes SHA-256 from `thermal-history` and needs both features.
- Binding duplicate executable paths with the OS ordinal comparison at decode: `service_status.rs` is shared with the Agent binary, which has no installed module. Decode compares by Unicode upper case; the validator repeats the check with the OS comparison.
- Enabling the `Files::remove` primitive in every lane: it is dead code without the feature and fails the lane clippy gate.

## Watch out
- Managed writes get-only members (`isConfigured`, `effectiveOffsets`) and accepts them, and `[JsonIgnore]` members such as `isWellFormed`, on read with any value. Quoted numbers bind for integers and doubles but never for enums; a quoted double also takes `+1`, `.5`, `5.`, `NaN` and `-Infinity`.
- A null `lighting` reaches `ArgumentNullException` in normalisation, so the reply text is `Value cannot be null. (Parameter 'settings')`.
- Managed throws `NativeComponentException` (no reply) for an application override that selects fan Stop without fan settings, a quoted profile `schemaVersion`, and a null battery policy at schema 17 or older. Native refuses those inputs.
- `Check-Workspace.ps1` was red at cc60616d for `openblade-protocol/completed_rejection`; fixed in 94358847.
- The read path (`thermal_storage::aggregate`) still refuses schema 24 and older and the schema-less legacy profile before any request, so the save's migration is reachable only for schemas 25 and 26 and an absent aggregate.
- `build.rs` does not name `service-profiles-v1` in the artifact capability string. Do not bind a profile-serving image until `WindowsNativePackage` knows the token.
