---
title: Keep Rust automation selection token-only and host state intact
author: Jack
date: 2026-11-08
branch: refactor/rust-automation-selection
touches:
  - ".github/workflows/rust.yml"
  - docs/rust-boundaries.md
  - rust/README.md
  - rust/Test-NativeInterop.ps1
  - rust/Test-ProductionActivation.ps1
  - rust/crates/openblade-domain/src/lib.rs
  - rust/crates/openblade-ffi/include/openblade.h
  - rust/crates/openblade-ffi/src/lib.rs
  - rust/tests/header_smoke.c
  - src/OpenBlade.Core.Native/ProductNativeRuntime.cs
---

## Decided
Stage 8 extracts only performance override priority and lighting value/provenance selection into no_std openblade-domain. Automation ABI v1 independently negotiates two exports and exchanges only presence flags and source tokens; original C# objects, null/zero/legacy values and reference identity never cross the boundary. Preserve ExternalUnknown observed lighting as ConfirmedFirmwareState; absent observation is the fallback profile with PowerProfile provenance, ignoring overrides/application origin.

## Tried and ruled out
Do not migrate all ProfileAutomationState or project whole profiles. Compatibility filtering, recovered HyperBoost, lifetimes, foreground/path matching, fallback construction and state transitions remain host-owned. The original helpers' managed bodies are frozen at 9e53ed92 as an independent test oracle. Earlier dormant-stage restrictions describe their original scopes; stage 8 deliberately activates this parity-proven bounded selector using the already mandatory native runtime.

## Watch out
Read-only recovery may expose a new source before automation consumes it; maintenance still needs source agreement and must not replay uncertain commands. Retain lazy profile access for unknown-power observed lighting. Startup must admit automation from the fixed hash-bound component and keep its module through shutdown. Native errors use existing fatal supervision, without fallback/replay. Codec/profile ABI v1, IPC, persisted schemas, hardware commands and fresh per-write admission stay unchanged.
