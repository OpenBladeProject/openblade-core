---
title: retain native fan ownership and cleanup reservations through refusals
author: Jack
date: 2026-11-13
branch: feat/windows-rust-native-fan-actuation
touches:
  - rust/crates/openblade-domain/src/lib.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - rust/crates/openblade-domain/src/thermal_mutation.rs
  - rust/crates/openblade-protocol/src/lib.rs
  - src/OpenBlade.Deployment/WindowsNativePackage.cs
  - src/OpenBlade.Deployment/WindowsNativeThermalHistory.cs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
  - tests/OpenBlade.Deployment.Tests/WindowsNativeEpochTests.cs
  - tools/openblade-windows-host/Cargo.toml
---

## Decided
The optional Windows native fan v2 slice keeps successful in-process fan ownership separate from a pending request. A refused identical request must not lose retained Stop IDs or byte capacity. Classify positively known prepublication capacity refusal with a typed error shared by performance and fan storage; uncertain reserve/rollover I/O stays quarantined. Exact original cleanup may resume only after a later fully retired Prepared-only request. Firmware authority, final result admission, callback retirement and durable handback are different contracts.

## Tried and ruled out
Replacing the owner before request execution lost cleanup on ID exhaustion. Treating every capacity refusal as uncertain storage failure also stranded a valid old cleanup reservation. Generic manual readback cannot prove RPM or manual subtype, and Custom paired selectors without level queries must remain truthful pending software intent rather than disappear before persistence.

## Watch out
Normal Stop immediately denies ordinary writes and reads while retaining original custody for one bounded positively owned Auto release. Unexpected trust/epoch loss never admits it. Reserve the complete operation plus ten cleanup IDs before Prepared; never reset/refill IDs. New fan rows require signed fan producer admission and the compatible managed reader before rollback. Tests here are injected/private-root only, not installed-native or physical validation. Ordinary Fixed/Maximum sleep policy, curves, guardian/duty/cooling-pad and full backend parity remain separate work.
