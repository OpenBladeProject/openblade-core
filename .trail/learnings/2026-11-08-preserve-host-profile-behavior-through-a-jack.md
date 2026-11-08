---
title: Preserve host profile behavior through a dormant Rust policy projection
author: Jack
date: 2026-11-08
branch: feat/rust-profile-policy
touches:
  - ".github/workflows/rust.yml"
  - docs/rust-boundaries.md
  - rust/Cargo.lock
  - rust/Check-Workspace.ps1
  - rust/README.md
  - rust/Test-WorkspaceCheck.ps1
  - rust/crates/openblade-domain/Cargo.toml
  - rust/crates/openblade-domain/src/lib.rs
  - rust/crates/openblade-ffi/include/openblade.h
  - rust/crates/openblade-ffi/src/lib.rs
---

## Decided
Stage 6 keeps ApplicationProfileResolution authoritative and adds an allocation-free no_std profile/display projection, optional policy ABI v1 exports and an explicit developer C# mapping. Keep codec v1 negotiation/records/exports unchanged and independently loadable. The 14-case corpus is shared by current C#, Rust and real native C# tests. Map fan source to exact baseline/override objects or legacy defaults so arbitrary custom curves survive. Reconstruct with baseline with and keep CustomPerformance, GpuClockTuning, Lighting and GamingModeKeyPolicy override branches host-owned.

## Tried and ruled out
Do not validate whole profiles or reject unknown performance/inherited fan scalar values at the merge seam. Invalid legacy Fans rejects only when FromLegacy synthesis occurs; contradictory Fans/FanControl intentionally survives merging. Do not normalize old Maximum plus Fixed/5400 compatibility values. Do not activate product callers or change IPC/saved schemas. Product version remains 0.56.1 (internal dormant refactor).

## Watch out
A null override returns the same baseline; a present override normalizes null display/behavior as existing C# does. Preserve absent vs false/zero/empty UTF8. Policy record sizes are 8/12/44/88 with 4-byte alignment and offset-based key references. Strict wire UTF8/flags are boundary validity, not host profile validation. Stage 7 must preserve baseline-null and legacy mode exception semantics and whole-profile validation order; direct display ABI requires a nonnull baseline. Exact-head hosted Windows/Linux/Intel macOS/ARM macOS checks and a real isolated Claude advisory review remain mandatory; synthetic software parity is not hardware validation.
