---
title: Preserve decoded performance source eligibility across the Rust boundary
author: Jack
date: 2026-11-09
branch: refactor/rust-performance-source
touches:
  - docs/rust-boundaries.md
  - rust/crates/openblade-domain/src/automation.rs
  - rust/crates/openblade-domain/tests/automation_policy.rs
  - rust/crates/openblade-ffi/include/openblade.h
  - rust/crates/openblade-ffi/src/automation.rs
  - rust/crates/openblade-ffi/src/automation_tests.rs
  - rust/tests/header_smoke.c
  - src/OpenBlade.Core.Native/NativeAutomationPolicy.cs
  - src/OpenBlade.Core.Native/ProductNativeRuntime.cs
  - src/OpenBlade.Service/ProfileAutomationState.cs
---

## Decided
Move only the three override slots' decoded performance/source predicate to openblade-domain. Negotiate the additive scalar export by symbol and automation ABI v1, keeping the original loader usable with an older selection-only library. The hash-bound product explicitly requires the export.

## Tried and ruled out
Do not share this filter with profile validation or hardware admission. The original rules differ for USB-C, undefined active sources and observed fallback. Use a shared signed-enum matrix and the frozen pre-change C# predicate to preserve those distinctions without a managed production fallback.

## Watch out
The original slot order and presence checks matter. Keep recovered HyperBoost priority and saved intent in C#. Undefined candidates are ineligible, undefined active sources still fail before the native call, and legacy observed values pass through untouched. Native failures use existing supervision and the module stays live through shutdown.
