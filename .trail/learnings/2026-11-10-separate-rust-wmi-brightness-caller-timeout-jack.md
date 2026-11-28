---
title: Separate Rust WMI brightness caller timeout from native completion
author: Jack
date: 2026-11-10
branch: feat/rust-phase-6i-native-wmi
touches:
  - rust/crates/openblade-platform-windows/Cargo.toml
  - rust/crates/openblade-platform-windows/src/brightness.rs
  - rust/crates/openblade-platform-windows/src/brightness/tests.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows/native.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows/native/tests.rs
  - rust/crates/openblade-platform-windows/src/brightness/windows/tests.rs
  - rust/crates/openblade-platform-windows/src/brightness/worker.rs
  - rust/crates/openblade-platform-windows/src/brightness/worker/tests.rs
---

## Decided
Phase 6i adds a fixed local Root/WMI transport to the Phase 6g Rust brightness owner. A dedicated worker keeps COM on one MTA thread, validates the provider relative path against the complete active InstanceName, and records possible submission before ExecMethod. Acknowledgement still requires independent same-instance readback.

## Tried and ruled out
Caller timeout, dropped reply, and release of a COM call-result pointer do not establish native completion. A stalled worker remains occupied and a lost reply after possible submission blocks further writes. Fixture tests link the Windows entry point without starting it.

## Watch out
ConnectServer has its own coarser limit, and several initiating calls have no verified two-second native bound. Do not use worker disposal or replacement to recover write authority. Agent integration needs session ownership, a stop and recovery policy, and separately authorized physical validation.
