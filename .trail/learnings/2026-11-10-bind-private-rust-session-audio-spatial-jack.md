---
title: Bind private Rust Session Audio spatial confirmation to fresh runtime and output
author: Jack
date: 2026-11-10
touches:
  - rust/README.md
  - tests/OpenBlade.Tray.Tests/RustSessionAgentTests.Audio.cs
  - tools/openblade-windows-host/src/session_audio.rs
  - tools/openblade-windows-host/src/session_windows.rs
  - tools/openblade-windows-host/src/session_audio/runtime.rs
  - tools/openblade-windows-host/src/session_audio/runtime/tests.rs
---

## Decided
- Keep typed Session Audio runtime admission in the private Windows Rust fixture. Pin managed 6d6abc9b for the modern two-output first read, legacy active output check, and spatial confirmation semantics.
- Require a verified read for the requested endpoint and fresh matching admission before possible submission. Acknowledgement needs independent same-binding typed readback.

## Tried and ruled out
- A modern probe does not confirm the sibling endpoint for dispatch. A Connect does not recover an uncertain submission.

## Watch out
- A dropped submit future or lost successful reply must leave subsequent connections requiring an explicit read. The inert worker and its identity tokens are synthetic coverage only.
