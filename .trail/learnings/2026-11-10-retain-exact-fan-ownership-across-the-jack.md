---
title: Retain exact fan ownership across the ordinary transition
author: Jack
date: 2026-11-10
branch: feat/rust-ordinary-fan-parity
touches:
  - tools/openblade-windows-host/src/fan_runtime.rs
  - tools/openblade-windows-host/src/fan_runtime/device.rs
  - tests/OpenBlade.Service.Tests/ServiceControlRuntimeTests.RustFanParity.cs
---

## Decided

The private RZ09-0581 fan replay uses the exact full-adapter Performance path. The shared managed-port trace records both selectors, the pre-target paired read, fresh adapter checks, the two 4000 RPM setters, and the final paired read. Synthetic owned handback changes only the fan selectors and leaves saved Fixed intent intact.

## Tried and ruled out

The first managed client run rejected Fixed capabilities without a valid speed range. The replay advertises its single 4000 RPM target. A matching manual topology does not prove the exact target or ownership.

## Watch out

When one setter may have submitted, stop the recipe and reconcile read-only. A split pair retains the last confirmed observation and keeps writes gated. An eventual complete observation cannot establish ownership. Native lifecycle integration still needs current ownership continuity, fresh prior-authority cleanup admission, and production recovery.
