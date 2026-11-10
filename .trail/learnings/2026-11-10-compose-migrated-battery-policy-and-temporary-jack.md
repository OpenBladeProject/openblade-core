---
title: Compose migrated battery policy and temporary intent in the Windows fixture
author: Jack
date: 2026-11-10
branch: test/rust-battery-profile-composition
touches:
  - tools/openblade-windows-host/src/battery_runtime.rs
  - tools/openblade-windows-host/src/configuration_writer/fixture.rs
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.cs
---

## Decided

Phase 4g feeds the schema 24 migration output into one scripted RZ09-0581 battery controller and serves it through the real C# client. The managed UpdateProfiles entry point stores a battery-only edit without a setter, retains a distinct override, and drops an equivalent override after verification.

## Tried and ruled out

Do not infer hardware confirmation from the saved profile or persist the temporary limit. The lost acknowledgement case uses a later observation to settle the pending request. The interrupted writer preserves the old marker and recovery evidence while reporting the last verified state, and startup refuses that aggregate before readiness.

## Watch out

The local writer checks the exact last verified bytes and holds the serialized scenario state through commit and readback. Restart grants read-only configuration access and starts with an independent scripted device baseline. These results establish no native takeover, hardware recovery or general profile mutation.
