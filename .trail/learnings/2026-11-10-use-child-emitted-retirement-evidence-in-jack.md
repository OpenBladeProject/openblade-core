---
title: Use child-emitted retirement evidence in scratch Setup
author: Jack
date: 2026-11-10
branch: test/rust-phase-8k-child-receipt
touches:
  - tests/OpenBlade.Setup.Tests/ScratchWriterRetirementGate.cs
  - tests/OpenBlade.Setup.Tests/SetupBackendCutoverTests.WriterRetirement.cs
  - tests/OpenBlade.WriterFence.Scratch/Program.cs
---

## Decided
The signed synthetic Setup rehearsal launches the existing inert C# and Rust writer children with private redirected stdio and the same enrolled lease file. A sharing violation after startup and a successful exclusive open after the terminal frame establish the lease transition. The observed frame binds to the original handle, generation nonce, boot, sealed fixture files and synthetic service build. Fake SCM STOP, clean exit on that retained handle and independent recovery checks all gate successor startup.

## Tried and ruled out
The earlier Setup rehearsal produced the receipt in the coordinator. This phase reuses the children without changing their wire commands and compares the pre-signal recovery bytes separately from the retirement records. Missing, stale or incomplete observed frames and missing or damaged records refuse independently.

## Watch out
The redirected stream is authenticated by the fixture launch. The test trusts locally built fixtures and the local .NET runtime. Production evidence needs its own protection and service admission. A retained disk record or a reopened lease cannot reconstruct a lost observation.
