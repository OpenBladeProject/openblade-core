---
title: Integrate Windows Rust GPU fixtures and dormant keyboard recovery
author: Jack
date: 2026-11-10
branch: feat/windows-rust-gpu-snaptap
touches:
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.PrivateRust.cs
---

## Decided
Retain the exact GPU and SnapTap parents in one Windows merge. Keep both Rust lockfiles in sync with the Windows platform crate's domain edge, and validate a single exact SDK allowlist. Pin both ordinary release roles to merge 3858c356f0fe6e8806bec1e37f526c5b62921456. Keep the opt-in simulated-close Agent outside the signed scratch roles and leave the managed keyboard path as the default.

## Tried and ruled out
The offline guard rejected unreviewed SDK features and a missing domain edge. The exact-GPU-base Trail matches concerned inherited keyboard wording and source, with no new runtime wiring. The pin-only Trail matches read part of the old binary digest as a number. VSTest did not connect to its testhost inside the sandbox. Directly invoking the compiled tests inside that sandbox reached START_PENDING and ROOT, then the service stopped with Win32 error 5 before READY. The identical binaries and direct runner under the ordinary non-admin token passed all nine signed cases, five private Fan Stop cases, six GPU or integrated cases, and managed SnapTap parity. The earlier broken service pipe did not recur.

## Watch out
A signed scratch archive and its byte-exact rollback are fixture evidence. Live keyboard admission, hook health and retirement, real GPU process authority, production signing and Windows registration or rollback, and hardware handback need separate evidence. Keep fixture startup errors distinct from typed GPU readiness refusals.
