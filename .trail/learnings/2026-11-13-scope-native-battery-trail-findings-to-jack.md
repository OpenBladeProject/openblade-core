---
title: Scope native battery Trail findings to semantic paths
author: Jack
date: 2026-11-13
branch: feat/windows-rust-battery-policy
touches:
  - ".gitattributes"
  - rust/crates/openblade-domain/src/battery_history.rs
  - rust/crates/openblade-domain/src/fan_history.rs
  - rust/crates/openblade-domain/src/lib.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - rust/crates/openblade-domain/src/thermal_mutation.rs
  - rust/crates/openblade-protocol/src/battery.rs
  - rust/crates/openblade-protocol/src/lib.rs
  - src/OpenBlade.Deployment/WindowsNativeBatteryHistory.cs
  - src/OpenBlade.Deployment/WindowsNativeFanHistory.cs
---

## Decided
Review the battery stage against accepted552e456c rather than the default main merge-base. The default Trail diff included500 historical migration files and was clipped at400KB. The exact-stage check covered40 non-learning files and returned20 phrase matches in the new pure battery history and existing fan parser tests. Each match was checked against its actual production guard or canonical fixture; the commit and retained report record the dispositions.

## Tried and ruled out
The phrase matcher treats positive requirements such as separate temporary-full-charge reads, exact original ownership and actual managed codec use as ruled-out phrases when those nouns reappear. Do not delete these safeguards or normalize old fixture bytes to clear a phrase match. Failed ACKs still stop without confirmation or replay, memory-only overrides remain memory-only, and shared scope/cache admission still applies.

## Watch out
A phrase disposition never waives semantic review. The independent review found a real separate numeric-range mismatch despite matching canonical hashes; baseline454 now has a complete rehashed shared rejection vector. Preserve the nonzero Trail outputs and exact per-finding reasoning, and explain the accepted dispositions in the commit instead of claiming a green automated check.
