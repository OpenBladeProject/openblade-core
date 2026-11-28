---
title: Native application-profile resolution reads two power sources and keeps setting sources
author: Jack
date: 2026-11-21
branch: feat/native-profile-resolution
touches:
  - docs/windows-native-thermal-mutation.md
  - rust/Check-Workspace.ps1
  - rust/crates/openblade-domain/src/application_context.rs
  - rust/crates/openblade-domain/src/profile_set/application.rs
  - rust/crates/openblade-domain/src/profile_set/encode.rs
  - rust/crates/openblade-domain/src/profile_set/model.rs
  - rust/crates/openblade-domain/src/profile_set/validate.rs
  - rust/crates/openblade-domain/tests/profile_resolution_vectors.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeProfileResolutionVectors.cs
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.Protected.cs
---

Supersedes `2026-11-21-native-active-application-profile-resolution-follows-jack.md` where the two disagree. The port still writes nothing, and nothing in the installed Service calls it.

## Decided
- The selection rules in `openblade_domain::application_context` have two users, the typed `profile_set::application` binding and the replay `application_runtime::Lease`. They are not yet the installed Service's: `thermal_storage.rs` still has four stateless matchers (584-594, 643-653, 826-836, 988-998) that reselect the first enabled rule for the path on every read. After a save that gives the active path to a new id they pick the new rule at once, while managed keeps no profile until the next report. The wiring slice must replace them; this slice does not touch that file.
- `resolve` takes `Power { state, project_unclassified }`. Managed reads two sources: `EffectiveLaptopBehavior` and `GetApplicationOverride` use the state `PowerSource`, while `Resolve` uses `observedPowerSource ?? PowerSource`, and `ResolveAutomationDesired` passes `ExternalUnknown` whenever `ProjectUnclassifiedIntent` holds, even with a classified state source. Under projection the profile is `None` but the behaviour still comes from the retained classified profile.
- The predicate's desired term is `desired_intent`: the profile layer of `Resolve` for every `DesiredDeviceState` member, with each setting's `Provenance`. Managed's record equality compares the source too, so removing an override equal to its base is a change and a display-only save is not. The Rust intent test now computes this term and checks it against managed's recorded `desiredChanged`, rather than reading the flag back.
- `Lease::rule` requires the pair to be retained by the current rules (`ApplicationContext::retained`), because the battery runtime can change rules without calling the private `Lease::update`.
- The reported-path-kept rule is visible only in state. The replay change it causes is Renewed rather than Changed on the report after a case-only path edit; the selection survived that edit before.

## Tried and ruled out
- Recording `desired: null` for `ExternalUnknown` as if managed returned it. The vectors now write the string `"confirmedState"`, a recorder marker, for both `profile` and `intent`, because managed builds that base from confirmed firmware state.

## Watch out
- `rust/Check-Workspace.ps1` admits an exact list of `openblade-domain` test targets and throws on any other. CI runs it, the cargo lanes do not, so a new `tests/*.rs` passes locally and fails CI. Run `Check-Workspace.ps1` and `Test-WorkspaceCheck.ps1`.
- `RustReadOnlyHostTests` hard-coded commit-marker schema 25 in two places while the host writes 27. They now read `ProfileStore.CurrentSchemaVersion` by reflection. The tests only run with `OPENBLADE_RUST_DEV_HOST_EXE` set, and `src/OpenBlade.Tray` needs its own `dotnet restore` first.
- Managed's IPC codec already refuses an unnormalised reported path, so the native `Unnormalized` refusal differs only for in-process callers. The native check does not expand 8.3 aliases.
