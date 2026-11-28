---
title: Preserve retained fan caller scope after retired request chains
author: Jack
date: 2026-11-13
branch: fix/windows-rust-fan-ownership
touches:
  - rust/crates/openblade-domain/src/fan_history.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - src/OpenBlade.Deployment/WindowsNativeThermalHistory.cs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
  - tools/openblade-windows-host/tests/data/fan-caller-scope-chains.json
---

## Decided
While a positive fan owner remains outstanding, every parsed row must retain that owner's nonce, user SID and session and remain a fan record. Enforce this at the shared retained-owner check in both Rust thermal_history::rows_from and managed WindowsNativeThermalHistory.Rows. New requests may use a different caller PID; rows within one request and resumed original release still require their exact original identity. Existing raw linked-v1 release50 and linked-v2 independently observed-Automatic51 meanings and commitments remain unchanged.

## Tried and ruled out
Comparing SID/session only when the immediately previous row is30 misses A30 -> same-scope B0/B6 -> foreign-scope C0/C6 -> exact A release. Both readers shared this gap; it was not merely managed/Rust parity. Complete shared histories after one and three retired same-scope requests establish baseline rejection failures; unsettled prefixes do not. Blanket PID equality across requests would reject valid fresh callers. The separate manual no-op review claim is refuted because Plan::select can mark unchanged only for an Automatic intent observed Automatic; no no-op production change is justified.

## Watch out
Use the actual Rust rows/handback parser and managed public Read plus AcquireRetiredOwner with protected raw bytes. The18 shared cases include four positive different-PID chains and negative late SID/session, owner nonce, missing fan and per-request PID histories across both completion formats. This is defensive persisted-history grammar validation; no unauthenticated injection or hardware behavior is established. Preserve malformed history rather than delete/relabel it. Reuse unchanged06 provider/entrypoint evidence explicitly, and keep new repair evidence separate from the frozen06 artifacts.
