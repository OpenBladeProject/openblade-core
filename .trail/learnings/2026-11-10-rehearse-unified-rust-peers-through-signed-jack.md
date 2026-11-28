---
title: Rehearse unified Rust peers through signed scratch Setup
author: Jack
date: 2026-11-10
branch: test/rust-setup-phase-7e
touches:
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.PrivateRust.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.SignedFixture.cs
---

## Decided

Phase 7e extends the Phase 7d signed scratch Setup rehearsal with both roles built offline from Phase 6y 3a7befde461e50f9cc54ee5903a703f48b19681a using Rust 1.99.1 x64 MSVC. Set OPENBLADE_PHASE_7E_FIXTURES to the directory containing the independently pinned development executables. Setup verifies service v79 and both typed Session Agent endpoints before simulated registration and scratch commit. The Agent's scripted focus and clear are observed through the staged service's typed status.

## Tried and ruled out

READY alone cannot establish readiness. The managed probe binds every connection to its exact retained child PID. A scripted stalled GPU reply after healthy service and Display causes the actual scratch transaction to stop both children and restore the byte-exact prior generation. The signed container still rejects invalid trust, tampering and misassigned roles.

## Watch out

Only launch the pinned foreground-integrated-application and integrated new modes. Stop the Agent before the service, then release the synthetic renderer after STOP_PENDING and require STOPPED. The service hands off a random current-user Temp directory for test cleanup. This rehearsal does not establish production Authenticode, installed service or sign-in registration, native hardware or installed rollback.
