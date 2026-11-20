---
title: Native typed refusal for unserved Service operations
author: Jack
date: 2026-11-20
branch: feat/native-service-refusal-and-profiles
touches:
  - tools/openblade-windows-host/src/service_status/unserved.rs
  - tools/openblade-windows-host/src/service_status.rs
  - tools/openblade-windows-host/src/windows/scm/installed/readiness.rs
  - build/NativeWindowsFixtureManifest.json
  - tests/OpenBlade.Contracts.Tests/NativeThermalMutationResponseTests.cs
---

## Decided
- A well-formed v85 request naming a managed operation that the installed native Service does not serve gets managed's own `ControlRequestHandler` fallback: `success: false`, error `IPC request operation is unsupported.`, no body, null `errorCode`. It reads no source, port or storage, so every caller the status endpoint admits gets it; a `Logon::Other` caller still closes.
- The known-operation list is the contract fixture `tests/OpenBlade.Contracts.Tests/WindowsReplay/v1/service.json` `operationFamilies`, embedded with `include_str!` in `service_status/unserved.rs`. Managed `WindowsReplay_FrozenWireInventoryMatchesManagedRegistry` pins it to the `ServiceRequest` registry. It has 51 operations, not 52; `reportSessionPowerTransition` is already in `lifecycle`.
- Served-ness is feature-aware (`StatusRequest::serves`), so a read-only composition also refuses `setFanOverride` and the other mutations instead of closing. A served operation that fails its own typed decoder still closes.
- `installed_unserved_operations_reply_with_the_managed_unsupported_failure` is in the `OPENBLADE_NATIVE_THERMAL_RESPONSES` corpus; the managed fact now requires `sawUnsupported`. The Python pin moved to `[8, 2, 2, 10, 2]` and 26 recorded commands.
- Native `updateProfiles` was not implemented. Managed's save path normalises and validates the whole `ProfileSet` against device capabilities, migrates older committed aggregates (`ReadCommittedSnapshotForSave` / `ConfigurationMigrated`), and rewrites three documents with System.Text.Json Web defaults (escaping, property order) plus a recovery record with absolute backup paths. Native has no typed `ProfileSet`, no STJ-canonical writer and no installed aggregate transaction; the existing configuration writer only edits battery policy inside a fixed template.

## Tried and ruled out
- Hand-matching managed's per-operation `IServiceControlRuntime` default `Blocked` texts: they are per-op `result` bodies, which `readCoolingPadStatus` (expects `status`) and `exportDiagnostics` (expects `archive`) clients would reject. The fallback failure frame decodes through every client path.

## Watch out
- Native does not mirror managed's session access policy before the refusal; an inactive or remote caller gets the unsupported failure where managed would send its access rejection. A malformed body for an unserved operation gets the refusal where managed's codec would close.
- `cargo clippy -D warnings` on `service-metadata-only-v1` fails on dead code that predates this branch; it is not a CI lint lane.
