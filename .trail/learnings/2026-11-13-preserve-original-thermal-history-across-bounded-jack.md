---
title: Preserve original thermal history across bounded rollover and handback
author: Jack
date: 2026-11-13
branch: feat/windows-rust-thermal-history
touches:
  - rust/Cargo.lock
  - rust/crates/openblade-domain/Cargo.toml
  - rust/crates/openblade-domain/src/lib.rs
  - src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs
  - src/OpenBlade.Deployment/WindowsNativeAttemptHistory.cs
  - src/OpenBlade.Deployment/WindowsNativeEpochFiles.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
  - src/OpenBlade.Deployment/WindowsNativeTrustLifecycle.cs
  - tests/OpenBlade.Deployment.Tests/ManagedMailboxCorrectionTests.cs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
---

## Decided
Keep the actual deterministic thermal row/checkpoint/commitment engine in openblade-domain behind its thermal-history parsing feature, and the protected Windows adapter under the same original owner writer. Retain raw rows and original Active owner joins. Reserve the exact largest legal progress branch before Prepared. Seal only settled tails; preserve all finite owner, attempt and transaction-ID budgets. Managed positive admission needs a stable per-generation v3 thermal digest plus fresh global owner/thermal history, original signed producer and independent process drain.

## Tried and ruled out
The protected managed file API wraps missing leaves as IOException, so FileNotFoundException around OpenFile did not establish absence. Use explicit presence observation then a protected open. Carry chronology across physical segment boundaries: resetting the parser at each boundary lets the same request restart. A capacity test must assert no Prepared/intent/setter and the actual blocked result, rather than assume low-level error wording is presented unchanged.

## Watch out
Root rollover staging is evidence even when thermal-history does not exist: test it before the missing-directory early return. Fault tests cover all 18 real protected publication cuts and preserve original bytes; they are process-visible injection, not physical power-loss proof. Previous readers refuse thermal history; compatible rollback retains evidence and creates a fresh epoch. Default managed packaging, IPC, installed app and firmware admission remain unchanged. All execution here is offline inspected pure/injected work in supplied own-SID roots; production adapters compile but are never executed. Full Windows parity and runtime/cutover gates remain separate.
