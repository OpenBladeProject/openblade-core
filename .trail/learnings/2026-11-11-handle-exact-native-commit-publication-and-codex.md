---
title: Handle exact native commit publication and isolate selected build identities
author: Codex
date: 2026-11-11
branch: feat/rust-native-origin-integration
touches:
  - Directory.Build.props
  - src/OpenBlade.Setup/OpenBlade.Setup.csproj
  - tests/Build.Tests/FixtureGraphIsolation.Tests.ps1
  - tools/openblade-windows-host/src/native_package.rs
  - tools/openblade-windows-host/src/native_package/tests.rs
  - tools/openblade-windows-host/src/session_native/service_client.rs
  - tools/openblade-windows-host/src/session_native/service_client/tests/subscription.rs
  - tools/openblade-windows-host/src/windows_protected_path.rs
---

## Decided
A live provisional reader must recognize Setup's exact committed-phase/frontier-before-current publication order. Permit only a four-second, same-origin, authenticated Committed.bin interval while the original Setup owner remains live. A different frontier, rollback, missing phase, owner loss or expiry remains sticky denial at InstalledPackage/Origin. Do not replay startup or infer drain from metadata.

## Tried and ruled out
Requiring current and frontier byte equality at every sample revoked healthy native status during successful commit. A build selector with changed symbols/references but the same artifact identity collided even though separate-root builds passed. Use the existing short graph-wide .i suffix for effective LocalDev native status selection. Locale-default source decoding changed superscript Windows device aliases; use explicit UTF-8 and retain shared COM/LPT alias coverage.

## Watch out
Fresh origin checks can race intentional phase publication. Treat that one typed interval separately from an unknown selection mismatch. Keep a single retained immutable committed phase handle and a bounded deadline; do not accumulate handles. Idle pipe-monitor retirement must reuse normal reconnect classification, while mismatches remain terminal. No kernel behavior is established by injected/private-filesystem tests or graph evaluations.
