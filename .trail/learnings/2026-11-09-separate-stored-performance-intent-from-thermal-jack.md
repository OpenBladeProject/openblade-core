---
title: Separate stored performance intent from thermal confirmation
author: Jack
date: 2026-11-09
branch: feat/rust-performance-parity
touches:
  - tools/openblade-windows-host/src/performance_runtime.rs
  - tools/openblade-windows-host/src/performance_runtime/device.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.RustPerformanceParity.cs
---

## Decided

The RZ09-0581 ordinary v79 performance override stores accepted intent before scoped device admission. Balanced and Performance with Automatic fans use a destination-specific pair of 0D/02 setters in channel order and independent matching 0D/82 reads. Use the exact thermal write validation fixture and the managed runtime oracle together. This is a fixed Windows development script with an in-process transport and a test-owned store.

## Tried and ruled out

Battery parity's commit-after-confirmation rule does not apply to the managed performance override. The ordinary Automatic-fan path does not require a fan setter or a reset to Balanced. Manual fan ownership and lifecycle handback are separate. The battery-specific warning against a getter on the write path does not describe managed thermal reconciliation, which makes an independently admitted read-only attempt after possible submission.

## Watch out

Pre-submission cancellation can leave accepted durable intent while the previous snapshot is still published. A bad first echo or loss of admission before channel two must stop the pair. Preserve the last confirmed state on an unavailable read, block replacement while confirmation is pending, and settle by observation without replay. A matched command echo is not a fresh observation.
