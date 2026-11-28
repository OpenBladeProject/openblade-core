---
title: Preserve fan release across native battery policy
author: Jack
date: 2026-11-13
branch: feat/windows-rust-battery-policy
touches:
  - rust/crates/openblade-domain/src/fan_history.rs
  - rust/crates/openblade-domain/src/lib.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - rust/crates/openblade-domain/src/thermal_mutation.rs
  - rust/crates/openblade-protocol/src/lib.rs
  - src/OpenBlade.Deployment/WindowsNativeFanHistory.cs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - src/OpenBlade.Deployment/WindowsNativeThermalHistory.cs
  - tests/OpenBlade.Contracts.Tests/NativeThermalMutationResponseTests.cs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
---

## Decided
Use the opt-in exact0581 installed battery path through the existing serialized Reader and shared protected journal. Reserve all fourteen exchanges before baseline/Prepared: source, policy, separate temporary-full-charge query, fresh source, one setter and nine independent confirmation reads. An echoed ACK is not confirmation; only the exact prior raw baseline may remain pending between bounded reads.

Allow settled positive fan ownership to coexist with typed battery rows only under the new explicitly signed battery-capable linked-v3 contract. Bind the original retained fan row and keep its release IDs, scope, configuration and handback obligation. Battery confirmation never retires a fan owner. Old linked-v1/v2 bytes retain their old grammar.

## Tried and ruled out
The old parser's fan-only interleaving restriction does not establish an Automatic-before-battery hardware prerequisite. Policy GET does not establish temporary-full-charge state. Unimplemented temporary-full-charge actuation cannot claim Stored for an uncommitted request. Do not restore process-scoped battery overrides from durable attempt rows after restart.

A matching canonical hash does not establish numeric range parity: managed masking accepted baseline454 while Rust's u8 rejected it. Bound the raw byte before masking and feed the same complete rehashed malformed history to both real parsers.

## Watch out
Keep current configuration, requested overrides, last confirmed hardware and nullable temporary-full-charge observations separate. Check effective configuration again before submission. Preserve original producer capability on historical reopening rather than relabeling with current selection. Unknown or ambiguous output remains a global fence, including fan cleanup. These are offline software/source results; finite-ID lifetime, current-main contract integration, installed runtime, hardware and cutover gates remain unfinished.
