---
title: Validate automation tokens while preserving legacy source payloads
author: Jack
date: 2026-11-08
branch: refactor/rust-automation-selection
touches:
  - docs/rust-boundaries.md
  - src/OpenBlade.Core.Native/NativeAutomationPolicy.cs
  - src/OpenBlade.Service/ProfileAutomationState.cs
  - tests/OpenBlade.Core.Native.Tests/NativeAutomationPolicyTests.cs
  - tests/OpenBlade.Service.Tests/AutomationSelectionParityTests.cs
---

## Decided
Automation result validation checks bounded token availability and unknown-power lighting constraints without copying Rust priority policy into the host. Keep ignored performance flags compatible with the C ABI; validate that using real raw Rust results. Preserve the original managed performance profile-source payload, including undefined enum data, when Rust selects the profile.

## Tried and ruled out
A stale or mismatched production DLL is rejected by fixed-path, version and embedded-hash admission. Do not duplicate the full priority algorithm as a second production oracle. Do not add managed fallback/injection or batch the two calls without a current requirement or measured regression. Fan policy is a separate later seam. Known-source lighting projection deliberately retains original argument evaluation order and unknown-source lazy profile access.

## Watch out
A present manual override may contain null lighting; presence means the container, not its contents. Unknown power with no observation may legitimately select a null fallback profile, and known power without profile/observation may legitimately select null confirmed state. Reject contradictory tokens without normalizing these legacy cases. Record the original reviewed SHA separately from the final tested SHA.
