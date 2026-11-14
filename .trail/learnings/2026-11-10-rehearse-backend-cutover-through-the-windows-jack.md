---
title: Rehearse backend cutover through the Windows Setup transaction
author: Jack
date: 2026-11-10
branch: test/rust-setup-cutover-rehearsal
touches:
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.cs
  - src/OpenBlade.Setup/SetupCommandRunner.cs
  - src/OpenBlade.Setup/SetupPayloadTransaction.cs
  - src/OpenBlade.Setup/SetupServiceHealthProbe.cs
---

## Decided
Rehearse the Windows backend cutover through the bundle-managed Setup command runner, Windows setup platform, real payload transaction, and typed service health probe. Use different service and Session Agent fixture bytes and private filesystem roots with fake Windows registration and SCM boundaries.

## Tried and ruled out
Independent fake lifecycle and payload-only rollback tests did not show which files were live when the candidate started or when rollback restarted the prior service. The protocol-51 writer emits a real legacy status response that the strict Setup health reader rejects after the candidate is reported running.

## Watch out
A successful Setup service status check precedes Session Agent sign-in registration and does not exercise Session Agent launch or IPC. Fixture hashes establish payload and persisted-data preservation within Setup, without proving signed payload admission, real SCM behavior, or service/agent writes after startup.
