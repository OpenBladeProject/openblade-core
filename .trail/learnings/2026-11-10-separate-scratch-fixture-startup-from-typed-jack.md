---
title: Separate scratch fixture startup from typed health
author: Jack
date: 2026-11-10
branch: test/rust-setup-phase-7e
touches:
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.PrivateRust.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.cs
---

## Decided

Phase 7e starts the exact retained fixture children in the simulated service start hook after activation. Setup's real health deadline then covers the typed service, Display and GPU readiness checks and the scripted reporter round trip. On rollback the one-shot start hook has already retired and the prior health probe is simulated.

## Tried and ruled out

Launching both processes inside the health probe passed in isolation but consumed enough of the existing ten-second budget under the broader scratch suite that the managed wait canceled before the scripted GPU refusal. Keep the production timeout and assert the typed refusal within it.

## Watch out

The unified Agent's stalled scenario returns a typed GPU failure after a bounded four-second scan. A Setup service-health timeout is a different failure and must not satisfy the rollback test. The deliberate Phase 6y role pin updates supersede only the Phase 7c test fixture inputs on this branch.
