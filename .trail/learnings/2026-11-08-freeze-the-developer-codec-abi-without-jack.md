---
title: Freeze the developer codec ABI without activating product loading
author: Jack
date: 2026-11-08
branch: feat/rust-codec-abi
touches:
  - ".github/workflows/rust.yml"
  - docs/rust-boundaries.md
  - rust/Cargo.toml
  - rust/Check-Workspace.ps1
  - rust/README.md
  - rust/Test-WorkspaceCheck.ps1
  - rust/crates/openblade-ffi/Cargo.toml
  - rust/crates/openblade-ffi/src/lib.rs
  - OpenBlade.Native.slnx
  - rust/Test-NativeInterop.ps1
---

## Decided
Stage 4 freezes codec C ABI 0x00010000 with request/options/error records sized 8/12/16, natural alignment4, explicit Cdecl exports and caller-owned capacity buffers. Signed expected lengths and protocol validation precedence remain those of the safe stage3 codec. Only openblade-ffi admits documented unsafe pointer/export work and std unwind containment; inner crates retain no_std/unsafe forbid and production dependencies stay inward/local. OpenBlade.Core.Native is net10.0, loads a normalized explicit absolute artifact path, uses per-call SafeHandle references, validates native results and returns typed errors. OpenBlade.Native.slnx and Test-NativeInterop.ps1 are developer-only; existing product loading/builds/schemas remain independent. Four native CI lanes compile the actual C header and run actual Rust/C# libraries against the canonical29 report vectors.

## Tried and ruled out
No copied/regenerated golden JSON, transport/hardware endpoint, model admission, profile merging, product packaging/loading, installed-app action or physical-validation claim. Immutable input buffers may alias; all writable ABI ranges must be disjoint from inputs and each other. A future host maps typed boundary errors to Protocol taxonomy. Capacity91 succeeds at the ABI while the existing exact-span C# API rejects destination91. Private Rust unit-test panic injection tests both real work exports after computation and before publication; no exported production test hook. Stage4 intentionally implements the native ABI deferred by stage3; historical stage-specific no-ABI statements do not prohibit this authorized stage.

## Watch out
Trail check returns lexical matches including inherited stage1-3 diff from main and unrelated hardware vocabulary. Reviewed these against the actual stage4 delta: none establish a firmware/model/profile/IPC change. Keep the checker unchanged and disclose the nonzero result in commit/PR. The initial Windows developer launcher overquoted VsDevCmd; cmd /d /c with call and one quoted script path works. Preserve MSVC and Rust toolchain environment changes within the invoking process. Native tests must fail if OPENBLADE_NATIVE_LIBRARY is missing. Unwind catching does not recover aborts, allocation failure or process faults. Future production packaging/loading and profile/domain work require their own reviewed stages.
