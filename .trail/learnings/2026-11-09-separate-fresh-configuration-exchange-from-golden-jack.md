---
title: Separate fresh configuration exchange from golden transaction parity
author: Jack
date: 2026-11-09
branch: feat/rust-config-writer
touches:
  - tools/openblade-windows-host/src/configuration_writer/tests.rs
  - tests/OpenBlade.Service.Tests/BatteryConfigurationStoreTests.cs
  - ".github/workflows/rust.yml"
---

## Decided
Keep the pinned C# transaction fixture and the fresh bidirectional exchange as separate tests. The managed oracle supplies both prior and intent from fresh saves. Send byte arrays so reconstruction cannot normalize text. CI checks the actual passed exchange result.

## Tried and ruled out
Passing only the golden prior into a test that always writes a golden intent duplicates the fixture checks. Selecting an exchange mode with an ambient environment flag can block ordinary Cargo test runs on standard input.

## Watch out
Only the explicitly selected ignored Rust test reads redirected input. A skipped managed integration test is acceptable in ordinary local runs and must fail the focused Windows CI gate.
