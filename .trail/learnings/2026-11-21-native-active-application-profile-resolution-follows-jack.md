---
title: Native active application-profile resolution follows managed selection
author: Jack
date: 2026-11-21
branch: feat/native-profile-resolution
touches:
  - rust/crates/openblade-domain/src/application_context.rs
  - rust/crates/openblade-domain/src/profile_set/application.rs
  - tools/openblade-windows-host/src/application_runtime.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.NativeProfileResolutionVectors.cs
  - tools/openblade-windows-host/tests/data/profile-resolution-vectors.json
---

Native active application-profile resolution is ported write-free: `openblade_domain::application_context` holds the selection rules and the save-path predicate, `profile_set::application` binds them to the typed set. Nothing in the installed Service calls it yet.

## Decided
- One implementation of managed `ProfileAutomationState` selection. The core module is allocation-free over borrowed `Rule` views and an `ApplicationContext<S>`, so the typed set and the replay `application_runtime::Lease` (serde `Value` rules, Windows ordinal comparison) share it. The Agent reporter crate keeps its own match because it mirrors a different managed class, `ApplicationProfileReporter`.
- The active pair stores the reported normalised path, not the rule's spelling. `UpdateProfiles` only retires it (enabled rule, same id, same path ignoring case) and never selects; the next report does. `GetApplicationOverride` finds the rule by id alone.
- `intent_changed` is generic over the desired state. Managed compares the whole `ResolveAutomationDesired` result, which this slice does not produce, so the typed `Resolution` exposes only the profile `Resolve` starts from.
- A reported path not already in `NormalizeExecutablePath` form is refused (`Unnormalized`), recorded as a `nativeDifference` step.

## Tried and ruled out
- Comparing the applied `PowerProfile` as the predicate's desired state: managed's `DesiredDeviceState` omits display, THX and the gaming-mode key policy, so a key-policy-only save flips the behaviour term while `desiredChanged` stays false. Comparing profiles would claim a change managed does not see for display-only saves.
- Restating the predicate in the managed test. The intent rows observe the real `UpdateProfilesAsync`: it prefixes its reply with `Profiles were stored.` exactly when the predicate held on a verified, writable device.

## Watch out
- With the RZ09-0581 composition on the scripted device, the full transition reports a battery capability refusal. It is a refusal, not a block, so the predicate rows still observe; do not assert the report reply is not `Failed`.
- The resolved profile in the managed test comes from the private `GetActiveProfile` and `GetApplicationOverride` by reflection. A rename there fails the vector test first.
- On this host `grep -c $'\r$'` cannot see CRs. Count line endings with perl, and expect `sed -i` to rewrite a CRLF file as LF.
