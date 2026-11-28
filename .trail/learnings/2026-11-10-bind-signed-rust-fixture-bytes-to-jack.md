---
title: Bind signed Rust fixture bytes to scratch Setup staging
author: Jack
date: 2026-11-10
branch: test/rust-setup-phase-7d
touches:
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.SignedFixture.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.PrivateRust.cs
---

## Decided
Phase 7d composes the canonical LocalDev manifest codec and production OpenBladeSignedReleaseVerifier with Phase 7c’s opt-in private Rust peer rehearsal. The in-memory ZIP orders paths and fixes timestamps. Its length and digest are covered by an ephemeral P-256 signature. Verify the same stream before decoding and require the fixed path inventory and independent Phase 7c role hashes before materializing any file. Compare that verified inventory with scratch Setup staging and activation. Set OPENBLADE_PHASE_7C_FIXTURES to the two Phase 7c pinned binaries and run OpenBlade.Setup.Tests.

## Tried and ruled out
Reject a wrong trust key, invalid signature, signed noncanonical manifest, same-length outer tamper, truncation, and signed ZIPs with swapped or missing roles. A valid signature establishes which bytes the key authorized. The test’s independent role pins establish which bytes belong in each backend role. Keep Phase 7c’s typed peer refusal and prior-tree restoration within the signed-fixture path.

## Watch out
The broker stages protected files with enrollment trust, verifies the manifest and staged installer, and rechecks the installer before launch. Setup independently builds a source inventory and compares it with staged readback before activation. This test connects those byte boundaries using an in-memory ZIP and private test trust, without exercising protected enrollment, Burn, Authenticode, installed startup, or live cutover. Product version and deployment policy are unchanged.
