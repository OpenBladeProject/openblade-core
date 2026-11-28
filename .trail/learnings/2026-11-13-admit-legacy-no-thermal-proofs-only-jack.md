---
title: Admit legacy no-thermal proofs only through fresh empty generation
author: Jack
date: 2026-11-13
branch: feat/windows-rust-thermal-history
touches:
  - ".gitattributes"
  - docs/windows-rust-thermal-history.md
  - rust/Cargo.lock
  - rust/crates/openblade-domain/Cargo.toml
  - rust/crates/openblade-domain/src/lib.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - src/OpenBlade.Deployment/ManagedMailboxNativeCustody.cs
  - src/OpenBlade.Deployment/WindowsNativeAttemptHistory.cs
  - src/OpenBlade.Deployment/WindowsNativeEpochFiles.cs
  - src/OpenBlade.Deployment/WindowsNativeInstalledSelection.cs
---

## Decided
Keep fresh v3 thermal admission mandatory, but preserve an authentic predecessor v2 terminal when the freshly authenticated original generation has exactly ThermalGeneration.Empty(installation, epoch, service). Preserve exact generation, reservation and first-six retirement-field comparisons, and fresh complete global owner/thermal custody. The old proof contributes no thermal-settlement guarantee; fresh admission proves absence of thermal activity.

## Tried and ruled out
Blanket retained/fresh proof field-count equality unnecessarily fenced a signed thermal-capable predecessor that never produced a journal row. The real pinned634 owner/attempt proof producer and pinned predecessor terminal algorithm reproduce this no-thermal v2 case. Matching an empty current setting or accepting v2 for a nonempty original generation would instead lose settlement evidence.

## Watch out
Accept only v2-to-fresh-v3 with the explicit empty generation digest. Compare v3-to-v3 thermal digests exactly; reject every other version mismatch and reject the old v2 terminal after any settled original-generation row appears. Do not rewrite the old terminal/proof, infer drain, repair first, or allow current global history to be skipped. This is offline private-fixture software compatibility, not installed or physical validation.
