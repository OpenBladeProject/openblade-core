---
title: Bind scratch retirement to the original Windows process
author: Jack
date: 2026-11-10
branch: test/rust-phase-8g-retirement
touches:
  - tests/OpenBlade.Setup.Tests/ScratchWriterRetirementGate.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.WriterRetirement.cs
---

## Decided
The opt-in signed scratch Setup rehearsal binds the outgoing backend generation to the staged service artifact SHA-256, sealed inert executable path and SHA-256, Windows-reported boot timestamp, and creation FILETIME from the original child handle. Before STOP, it flushes a versioned intent. Both directions require fake SCM stop, signaled clean exit on the retained handle, matching complete intent and scripted receipt, and clear fixture recovery state before file moves and incoming start.

## Tried and ruled out
The older Rust ownership rehearsal at 7f8a61a0 already checks an owned child handle, a terminal receipt, and independent readback in its separate transport harness. This change uses the existing Setup child and fake SCM. A persisted observer claim and preserved records block a new observer even if the child has exited. A completed or torn record alone cannot authorize reacquisition.

## Watch out
The test uses a medium-token inert cmd.exe, signed synthetic payloads and fake SCM. The receipt and observer restart are scripted. LocalSystem still needs a reviewed protected lease and journal, actual service process and image binding, authenticated terminal receipt, real SCM and crash/reboot recovery verification.
