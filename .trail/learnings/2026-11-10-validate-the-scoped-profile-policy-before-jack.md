---
title: Validate the scoped profile policy before Windows fixture startup
author: Jack
date: 2026-11-10
branch: feat/rust-profile-bootstrap
touches:
  - docs/windows-rust-development-service.md
  - tests/OpenBlade.Service.Tests/BatteryConfigurationStoreTests.cs
  - tests/OpenBlade.Tray.Tests/OpenBlade.Tray.Tests.csproj
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
  - tools/openblade-windows-host/src/configuration_writer.rs
  - tools/openblade-windows-host/src/configuration_writer/fixture.rs
  - tools/openblade-windows-host/src/windows.rs
  - tests/OpenBlade.Service.Tests/WindowsReplay/v1/configuration-profile.json
  - tools/openblade-windows-host/src/configuration_writer/profile.rs
---

## Decided
Windows developer profile replay decodes the battery charge limit and Fan Stop settings after aggregate envelope verification. The fixture bootstraps schema 25 defaults from an empty root and migrates schema 24 against the exact validated preimage. The migration adds 10 degrees to each legacy threshold, capped at 100. The pipe shows the persisted requested charge limit.

## Tried and ruled out
A valid marker and matching digests cannot admit an invalid enum or missing current cutoffs. Reopening never migrates the directory. Retain the exact embedded fixture boundary for the other profile and preference fields.

## Watch out
Fan Stop settings are validated and checked against the managed loader. They grant no runtime Fan Stop behavior in the developer host. Earlier aggregates, profile-only migration, owner binding, production storage, and persistent overrides need separate ports.
