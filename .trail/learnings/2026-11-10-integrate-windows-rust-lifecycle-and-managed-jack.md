---
title: Integrate Windows Rust lifecycle and managed writer leases
author: Jack
date: 2026-11-10
branch: feat/windows-rust-lifecycle-writer-integration
touches:
  - tools/openblade-windows-host/Cargo.toml
  - tools/openblade-windows-host/src/windows/scm/tests/metadata_lifecycle.rs
  - tests/OpenBlade.WriterFence.Scratch/README.md
---

## Decided

Integrate Windows Rust lifecycle, HID metadata retirement and competing-controller observation with the opt-in managed writer leases by preserving the exact merge parents. The only overlapping manifest merged cleanly. Cargo exposes the HID and lifecycle compile checks alongside the scratch writer feature, and the default binaries still compile.

## Tried and ruled out

The restricted token denied native scratch directory opens. The same Rust suites and two-way managed/Rust writer rehearsal passed with a non-admin Windows token. Run the existing in-process xUnit runner from the fresh test output so AppContext.BaseDirectory contains the matching native FFI and replay data. Give the managed byte exchange the Rust library test executable.

## Watch out

Private v79 tests exercise fake SCM and scripted HID evidence. The scratch rehearsal verifies writer exclusion, clean release and conservative handback after abrupt exit. Installed service lifecycle, native HID retirement and reconnect, and ACL-bound writer coordination still need independent evidence. Trail check flagged phrases inherited from both sides; the merge message records the reviewed guard behavior.
