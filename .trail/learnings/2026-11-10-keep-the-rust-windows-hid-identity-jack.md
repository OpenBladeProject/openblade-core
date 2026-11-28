---
title: Keep the Rust Windows HID identity lease metadata-only
author: Jack
date: 2026-11-10
branch: feat/windows-rust-hid-identity-lease
touches:
  - docs/windows-backend-replay.md
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/discovery/mod.rs
  - tools/openblade-windows-host/src/discovery/windows.rs
  - tools/openblade-windows-host/src/discovery/hid_lease.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/tests.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/windows.rs
---

## Decided
Keep the first Rust opened-handle identity lease private to the Windows development host. The existing discovery scan selects a unique candidate from a complete inventory. The lease opens metadata only, independently checks the descriptor, then repeats host and interface selection and rechecks the same handle before retaining it. A later failure or observed removal revokes the lease and closes the handle. SKU and BIOS remain diagnostic. Use the full observed descriptor for continuity without pinning a device version for initial selection.

Managed comparison at 2809e6ad07f3f0b1f74985935a45bb8caac4f1bb: WindowsVerifiedBladeDeviceProvider.cs blob 68c55136543b687cf99bdb6870543d8e5a5752ad, BatteryValidationProbe.cs blob 7fa5afabcdff742acd92748c586789401b95d342, WindowsRazerHidSessionOpener.cs blob 30a9c0373de07d00b28aa26cef130ebd953ebef9, WindowsRazerHidSessionOpenerTests.cs blob 134111fa2de4eb2354e8e00eee004a48860c3c57, WindowsRazerHidSessionTests.cs blob baebdf5b0ebcf5851e82b180b44ec1894f5ad819, and WindowsVerifiedBladeDeviceProviderProductionTests.QueryFailures.cs blob 22c9fd0928eae50e6374f9e6d7266d0f54924f9e. ExperimentalBladeFactory.cs blob 258f6ca30863d7f8197f6053cea72c1f76ca8531 supplies the source-reviewed descriptor and separate functional selector.

## Tried and ruled out
Do not promote PnP hardware IDs to handle evidence or reopen a path to refresh an existing lease. Keep the native adapter compile-only and use fake handles for validation. The existing test transport has a separate lifetime and must not take this metadata-only handle.

## Watch out
The zero-access handle and ConfigMgr notification sequencing still need attended Windows validation, including a same-path removal and reconnect. A service owner needs explicit removal retirement and a separate operational transport with fresh controller, session, power, model-specific functional, and per-command admission.

Trail check flags lexical overlap with the offline feature-transport learning. That learning still applies to every executable entry point and test. The new adapter is compiled separately, and the fake tests exclude it even when the feature is enabled. Discovery remains a hint and revalidation never reopens the path.
