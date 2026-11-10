---
title: Require typed private Rust readiness in the Setup rehearsal
author: Jack
date: 2026-11-10
branch: test/rust-setup-phase-7c
touches:
  - tests/OpenBlade.Setup.Tests/OpenBlade.Setup.Tests.csproj
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.PrivateRust.cs
---

## Decided
Extend the bundle-managed scratch Setup transaction with two private, typed foreground peers. Setup's managed health probe reads service v79 and the managed Session GPU codec queries v2. Build the Rust host at 1ec18766f3afdeeb819d07c7f91771d5da9c44e4 and the Rust Session Agent at 7e4781de5144bcc72a16dd3d195057a5db878c8a in separate exact checkouts using Cargo 1.99.1, MSVC x64, and cargo build --frozen --manifest-path tools/openblade-windows-host/Cargo.toml --bin <role>. The test pins the local outputs. Set OPENBLADE_PHASE_7C_FIXTURES to the local directory with those exact outputs, then filter the Setup test project on SetupBackendCutoverTests. Set OPENBLADE_PHASE_7B_FIXTURES separately to also run the five existing fixture cases.

## Tried and ruled out
A READY line is only a startup barrier. Verify the server PID against the retained child before sending the typed request, and require both typed replies before simulated sign-in registration and real scratch commit. Copy the independently verified, activated bytes under the development executable names rather than launching the product-named paths. The agent's scripted no-data reply exercises refusal, exact child shutdown before rollback, and full prior-tree restoration without injecting a fake wire reply.

## Watch out
Plain foreground and gpu-foreground are the admitted modes here. Do not use SCM dispatch, a product pipe or the hardware runtime. OS registration and restored managed service health are simulated. This does not establish signed payload trust, installed LocalSystem/user startup, deployment rollback, or hardware behavior. The local binary pins depend on the toolchain and link inputs; do not turn a caller-provided hash into launch authority.
