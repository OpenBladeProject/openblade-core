---
title: Verify stage6 policy review fixes with independent version admission
author: Jack
date: 2026-11-08
branch: feat/rust-profile-policy
touches:
  - ".github/workflows/rust.yml"
  - docs/rust-boundaries.md
  - rust/README.md
  - rust/Test-NativeInterop.ps1
  - rust/crates/openblade-ffi/src/lib.rs
  - rust/crates/openblade-ffi/src/policy.rs
  - rust/crates/openblade-ffi/src/policy_tests.rs
  - src/OpenBlade.Core.Native/NativeProfilePolicy.cs
  - tests/OpenBlade.Core.Native.Tests/NativeProfilePolicyTests.cs
  - rust/tests/policy_only.c
---

## Decided
Claude Opus5.5/high completed success_native_review at basef542888e205b8e41f10b7ac3193f637600affd74 to original heada784961ecb64fb779597ed1c06bfe1132dfa780b. Confirm and fix separate version admission in Rust and managed loading, null-baseline priority before UTF8 encoding, null-override passthrough identity, and missing workflow trigger paths for Contracts and the linked C# oracle. Use explicit domain error mapping instead of a catch-all. A test-only C library with policy exports and no codec exports proves independent loader admission; all merge/parity/sentinel outcomes still use actual Rust. Existing codec exports/version and product paths remain unchanged. Hold the module explicitly through constructor negotiation.

## Tried and ruled out
Decline broad status/layout/handle deduplication and pooled buffers as style or unmeasured future work. Keep distinct fixture types because required baseline fields and sparse overrides have different reader contracts. Keep checked aggregate key length before publication: independent input references may alias, so total output length can exceed the input length even though each slice fits. Earlier Claims of all overflow checks being dead are unsupported. A consumed unpaired UTF16 surrogate cannot cross strict UTF8; retain visible encoding errors and document this activation boundary instead of silently substituting bytes.

## Watch out
Record original reviewed head separately from final tested fixes; one completed real review plus verified supported fixes is the gate, not another automatic Claude call. Cross-platform native builds exposed existing IDE0055 at IpcMessageCodec's comment-containing expression: align its checkout/analyzer LF rules without changing canonical source. Final native verification covers102 managed tests and both C smoke/independent-loader libraries. Stage7 must retain host exception/validation order and account for UTF8 limits; no production activation or hardware evidence follows.
