---
title: Round-trip the Windows scratch run marker through both stores
author: Jack
date: 2026-11-10
branch: test/rust-phase-8o-run-marker
touches:
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.RunMarkerTransition.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.WriterRetirement.cs
  - tools/openblade-windows-host/tests/fixtures/writer_fence.rs
---

## Decided
Use one enrolled NTFS root and both real opt-in marker stores for the Windows scratch round trip. A private Rust child argument reports classification after the normal ready frame. The existing retirement command, terminal frame, and process verification still apply.

## Tried and ruled out
A same-boot leftover classifies as recovery even after the fixture process exits cleanly. A separate case scripts an earlier boot using clock inputs in the test to prove each runtime actually parses the other runtime's marker. An explicit handoff with an uncleared primary marker fails after retirement verification and preserves the bytes.

## Watch out
Fixture retirement releases the lease and proves process exit. Run-marker cleanup is a separate operation. Keep the private marker argument behind the scratch build feature and retirement opt-in. Trail's lexical matches against unrelated lifecycle and recovery learnings do not change these boundaries.
