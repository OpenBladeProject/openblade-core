---
title: Keep writer handoff and signed readiness evidence separate
author: Jack
date: 2026-11-10
branch: feat/windows-rust-scratch-handoff-union
touches:
  - tests/OpenBlade.Setup.Tests/ScratchWriterRetirementGate.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.WriterRetirement.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.PrivateRust.cs
---

## Decided
Retain both exact parents for the Audio/SCM and scratch writer handoff union. Keep the retirement gate in Setup.Tests. Production sources and ordinary Rust build inputs match the verified candidate.

## Tried and ruled out
The 20 handoff rows exercise synthetic writer retirement and recovery. The two signed readiness rows run pinned Rust peers and prove activation and byte-exact rollback after a typed GPU timeout. The historical Phase 7B rehearsal uses separate pinned role bytes and fake Windows operations. VSTest could not connect to its test host. A local runner passed all 34 rows. The two readiness rows needed a scoped non-admin retry for private IPC.

## Watch out
Use the ordinary Phase 7E pins for live scratch readiness and the historical Phase 7B pins for the byte-only rehearsal. The scratch gate independently requires the outgoing generation receipt, stopped state, retained child exit, and clear recovery disposition.
