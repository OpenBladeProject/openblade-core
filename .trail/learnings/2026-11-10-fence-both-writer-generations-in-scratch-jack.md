---
title: Fence both writer generations in scratch Setup
author: Jack
date: 2026-11-10
branch: test/windows-rust-writer-handoff
touches:
  - tests/OpenBlade.Setup.Tests/ScratchWriterRetirementGate.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.WriterRetirement.cs
---

## Decided
In the scratch Setup rehearsal, gate payload moves and fake SCM start on the exact outgoing writer generation's receipt, SCM stop, clean exit on the retained process handle, and a clear recovery disposition. On ambiguity, preserve the current payload, the staged or rollback copy, and journal bytes.

## Tried and ruled out
A successful stop request, SCM Stopped, process exit, and restoration of prior files each answer a different question. Production Setup may continue into rollback after a failed stop, so the scratch gate covers rollback moves and incoming start independently.

## Watch out
The signed rehearsal uses synthetic payload bytes and inert processes. Guardian pending state, existing thermal ownership, and a Rust battery aggregate or recovery fence block transfer. Native admission still needs protected shared fencing, boot/build/process-bound receipts, and crash-safe readback of reviewed records.
