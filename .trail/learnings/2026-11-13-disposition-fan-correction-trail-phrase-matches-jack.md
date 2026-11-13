---
title: Disposition fan correction Trail phrase matches without weakening fences
author: Jack
date: 2026-11-13
branch: fix/windows-rust-fan-ownership
touches:
  - rust/crates/openblade-domain/src/fan_history.rs
  - rust/crates/openblade-domain/src/thermal_history.rs
  - rust/crates/openblade-domain/src/thermal_mutation.rs
  - src/OpenBlade.Deployment/WindowsNativeFanHistory.cs
  - src/OpenBlade.Deployment/WindowsNativeThermalHistory.cs
  - tests/OpenBlade.Deployment.Tests/NativeThermalHistoryTests.cs
  - tests/OpenBlade.Deployment.Tests/OpenBlade.Deployment.Tests.csproj
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/fan.rs
  - tools/openblade-windows-host/src/discovery/hid_lease/performance/mutation.rs
  - tools/openblade-windows-host/src/windows/scm/installed/fan_mutation.rs
---

## Decided
The exact-baseline Trail check for the native fan correction (base85de177f) reported20 phrase matches, not semantic violations. Proceed with the correction while preserving every referenced safety rule. Findings1/2/3/4/5/13/18/20 concern the changed area: actual worker cancellation and capacity tests now exercise mutation-stage loss, the shared capture wrapper covers all fan/performance/policy callers, both public readers reject complete foreign SID/session histories, strict IPC80 response bytes remain asserted, and fresh explicit epoch recovery does not revive stale Stop. Both raw legacy and versioned row bytes are preserved and validated by the real parsers/protected stores.

## Tried and ruled out
Findings6/7/8/9/10/11/12/14/15/16/17/19 are lexical matches at the new managed test method declaration/fixture setup. The patch does not change IPC single-body encoding, signed package/process/launch/drain authentication, installation/rollback, owner erasure/reset, SO6120/PWM/display behavior, missing-leaf handling or WinUI. The original owner is never guessed from raw Auto: progress51 requires retained in-process identity plus a fully fresh typed independent Automatic proof, and raw legacy records remain unchanged. Transport retirement and durable firmware outcome remain separate.

## Watch out
Keep the exact Trail JSON and this disposition with the local commit. Scope/cancellation fixes remain offline source/software evidence only. New row readers must precede producers; unknown old readers refuse linked-v2 rather than relabel records. Future changes to reader retirement, row semantics, supplier scope or package roles need fresh focused review.
