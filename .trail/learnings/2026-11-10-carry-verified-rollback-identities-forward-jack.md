---
title: Carry verified rollback identities forward
author: Jack
date: 2026-11-10
branch: feat/rust-protected-runtime
touches:
  - tools/openblade-windows-host/src/configuration_writer.rs
  - tools/openblade-windows-host/src/configuration_writer/fixture.rs
  - tests/OpenBlade.Tray.Tests/RustReadOnlyHostTests.Protected.cs
---

## Decided
When a protected transaction replaces a primary during rollback, the confirmed prior state includes the restore file's identity. Keep the saved policy and prior bytes, carry forward only the verified rollback identities, then independently recheck the aggregate before permitting a later request.

## Tried and ruled out
Do not infer restoration from equal primary bytes when the recovery record is present, and do not make an admitted record lose its identity just to reserialize unchanged preferences.

## Watch out
The known-rollback check pins the commit marker against deletion, verifies that an earlier primary was replaced and restored, and checks the exact preimage and preference identity. A successful retry uses a new typed request. A rename or flush with uncertain effects retains the recovery record.
